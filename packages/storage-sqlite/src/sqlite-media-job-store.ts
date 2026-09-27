import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import {
	captureMediaJobLimits,
	captureMediaJobPurgeRequest,
	captureMediaQueuedJobListRequest,
	captureStoredMediaJob,
	isMediaJobPurgeable,
	MediaJobConflictError,
	type MediaJobLimits,
	type MediaJobPurgeRequest,
	type MediaJobPurgeResult,
	MediaJobNotFoundError,
	type MediaJobStore,
	type MediaQueuedJobListRequest,
	type StoredMediaJob,
} from "@opengameagent/media";
import {
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type JsonValue,
	JsonValueLimitError,
} from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const SQLITE_INTEGER_MAX = 9_223_372_036_854_775_807n;
const SHA256 = /^[0-9a-f]{64}$/u;
const PURGEABLE_JOB = `(json_extract(job_json, '$.attempts[#-1].disposition') = 'abandoned'
	OR json_extract(job_json, '$.attempts[#-1].status') IN ('succeeded', 'partially_succeeded', 'failed', 'cancelled'))`;

const migrations: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "durable_media_jobs",
		sql: `
			CREATE TABLE oga_media_jobs (
				job_id TEXT PRIMARY KEY,
				adapter_id TEXT NOT NULL,
				revision INTEGER NOT NULL CHECK(revision > 0),
				job_bytes INTEGER NOT NULL CHECK(job_bytes > 0),
				job_fingerprint TEXT NOT NULL CHECK(
					length(job_fingerprint) = 64 AND job_fingerprint NOT GLOB '*[^0-9a-f]*'
				),
				job_json TEXT NOT NULL CHECK(
					json_valid(job_json)
					AND json_type(job_json) = 'object'
					AND length(CAST(job_json AS BLOB)) = job_bytes
				)
			) STRICT;
		`,
	},
	{
		version: 2,
		name: "media_job_retention",
		sql: `CREATE INDEX oga_media_jobs_retention ON oga_media_jobs(
			json_extract(job_json, '$.updatedAt'), job_id
		) WHERE ${PURGEABLE_JOB};`,
	},
	{
		version: 3,
		name: "queued_media_job_recovery",
		sql: `CREATE INDEX oga_media_jobs_queued ON oga_media_jobs(adapter_id, job_id)
			WHERE json_extract(job_json, '$.attempts[#-1].status') = 'queued';`,
	},
];

export interface SqliteMediaJobStoreOptions {
	readonly limits: Readonly<MediaJobLimits>;
	/** Complete canonical persisted job snapshot budget. This package supplies no workload default. */
	readonly maxStoredJobBytes: number;
}

export class SqliteMediaJobCapacityError extends RangeError {
	constructor() {
		super("The persisted media job exceeds the configured byte budget.");
		this.name = "SqliteMediaJobCapacityError";
	}
}

export class SqliteMediaJobCorruptionError extends Error {
	constructor(options?: ErrorOptions) {
		super("The persisted media job is corrupt.", options);
		this.name = "SqliteMediaJobCorruptionError";
	}
}

export class SqliteMediaJobRevisionExhaustedError extends Error {
	constructor() {
		super("The media job exhausted SQLite's signed 64-bit integer revision space.");
		this.name = "SqliteMediaJobRevisionExhaustedError";
	}
}

interface MediaJobHeaderRow {
	job_id: unknown;
	adapter_id: unknown;
	revision: bigint;
	job_bytes: bigint;
	job_fingerprint: unknown;
}

interface MediaJobRow extends MediaJobHeaderRow {
	job_json: unknown;
}

interface PreparedMediaJob {
	job: StoredMediaJob;
	revision: bigint;
	json: string;
	bytes: number;
	fingerprint: string;
}

export class SqliteMediaJobStore implements MediaJobStore {
	private readonly maxStoredJobBytes: number;
	private readonly limits: Readonly<MediaJobLimits>;

	constructor(
		private readonly authority: SqliteAuthority,
		options: Readonly<SqliteMediaJobStoreOptions>,
	) {
		if (!Number.isSafeInteger(options?.maxStoredJobBytes) || options.maxStoredJobBytes < 1) {
			throw new RangeError("maxStoredJobBytes must be a positive safe integer.");
		}
		this.maxStoredJobBytes = options.maxStoredJobBytes;
		this.limits = captureMediaJobLimits(options.limits);
		this.authority.install("media-jobs", migrations);
	}

	async create(value: Readonly<StoredMediaJob>): Promise<void> {
		const prepared = this.prepare(value);
		this.authority.write((database) => {
			if (readHeader(database, prepared.job.jobId)) throw new MediaJobConflictError();
			database
				.prepare(
					`INSERT INTO oga_media_jobs(
						job_id, adapter_id, revision, job_bytes, job_fingerprint, job_json
					) VALUES (?, ?, ?, ?, ?, ?)`,
				)
				.run(
					prepared.job.jobId,
					prepared.job.adapterId,
					prepared.revision,
					prepared.bytes,
					prepared.fingerprint,
					prepared.json,
				);
		});
	}

	async read(jobId: string): Promise<StoredMediaJob | undefined> {
		return this.authority.snapshot((database) => {
			const header = readHeader(database, jobId);
			if (!header) return undefined;
			this.assertHeader(header, jobId);
			return hydrate(readRow(database, jobId), header, this.limits);
		});
	}

	async purge(request: Readonly<MediaJobPurgeRequest>): Promise<MediaJobPurgeResult> {
		const captured = captureMediaJobPurgeRequest(request);
		return this.authority.write((database) => {
			const select = database.prepare(`SELECT job_id, adapter_id, revision, job_bytes, job_fingerprint
				FROM oga_media_jobs WHERE ${PURGEABLE_JOB}
				AND json_extract(job_json, '$.updatedAt') < ?
				AND job_id NOT IN (SELECT value FROM json_each(?))
				ORDER BY json_extract(job_json, '$.updatedAt'), job_id LIMIT ?`);
			select.safeIntegers(true);
			const rows = select.all(
				captured.beforeUpdatedAt,
				JSON.stringify(captured.retainJobIds),
				captured.limit,
			) as MediaJobHeaderRow[];
			const remove = database.prepare("DELETE FROM oga_media_jobs WHERE job_id = ?");
			for (const header of rows) {
				if (typeof header.job_id !== "string") throw new SqliteMediaJobCorruptionError();
				this.assertHeader(header, header.job_id);
				const job = hydrate(readRow(database, header.job_id), header, this.limits);
				if (!isMediaJobPurgeable(job) || Date.parse(job.updatedAt) >= Date.parse(captured.beforeUpdatedAt)) {
					throw new SqliteMediaJobCorruptionError();
				}
				remove.run(header.job_id);
			}
			return { deletedJobs: rows.length };
		});
	}

	async listQueued(request: Readonly<MediaQueuedJobListRequest>): Promise<readonly StoredMediaJob[]> {
		const captured = captureMediaQueuedJobListRequest(request);
		return this.authority.snapshot((database) => {
			const afterFilter = captured.afterJobId === undefined ? "" : " AND job_id > ?";
			const select = database.prepare(`SELECT job_id, adapter_id, revision, job_bytes, job_fingerprint
				FROM oga_media_jobs WHERE adapter_id = ? AND json_extract(job_json, '$.attempts[#-1].status') = 'queued'
				${afterFilter} ORDER BY job_id LIMIT ?`);
			select.safeIntegers(true);
			const rows = select.all(
				captured.adapterId,
				...(captured.afterJobId === undefined ? [] : [captured.afterJobId]),
				captured.limit,
			) as MediaJobHeaderRow[];
			return rows.map((header) => {
				if (typeof header.job_id !== "string") throw new SqliteMediaJobCorruptionError();
				this.assertHeader(header, header.job_id);
				const job = hydrate(readRow(database, header.job_id), header, this.limits);
				if (job.adapterId !== captured.adapterId || job.attempts.at(-1)?.status !== "queued")
					throw new SqliteMediaJobCorruptionError();
				return job;
			});
		});
	}

	async modify(
		jobId: string,
		change: (current: Readonly<StoredMediaJob>) => Readonly<StoredMediaJob>,
	): Promise<StoredMediaJob> {
		return this.authority.write((database) => {
			const header = readHeader(database, jobId);
			if (!header) throw new MediaJobNotFoundError();
			this.assertHeader(header, jobId);
			const row = readRow(database, jobId);
			const current = hydrate(row, header, this.limits);
			const changed = change(current);
			if (changed === current) return hydrate(row, header, this.limits);
			if (header.revision === SQLITE_INTEGER_MAX) throw new SqliteMediaJobRevisionExhaustedError();
			const nextRevision = header.revision + 1n;
			const prepared = this.prepare(changed, nextRevision.toString());
			if (prepared.job.jobId !== current.jobId || prepared.job.adapterId !== current.adapterId) {
				throw new MediaJobConflictError();
			}
			const updated = database
				.prepare(
					`UPDATE oga_media_jobs
					 SET revision = ?, job_bytes = ?, job_fingerprint = ?, job_json = ?
					 WHERE job_id = ? AND revision = ?`,
				)
				.run(prepared.revision, prepared.bytes, prepared.fingerprint, prepared.json, jobId, header.revision);
			if (updated.changes !== 1) throw new MediaJobConflictError();
			return structuredClone(prepared.job);
		});
	}

	private assertHeader(row: MediaJobHeaderRow, expectedJobId: string): void {
		try {
			if (
				row.job_id !== expectedJobId ||
				typeof row.adapter_id !== "string" ||
				typeof row.revision !== "bigint" ||
				row.revision <= 0n ||
				row.revision > SQLITE_INTEGER_MAX ||
				typeof row.job_bytes !== "bigint" ||
				row.job_bytes <= 0n ||
				typeof row.job_fingerprint !== "string" ||
				!SHA256.test(row.job_fingerprint)
			) {
				throw new TypeError();
			}
		} catch (error) {
			throw new SqliteMediaJobCorruptionError({ cause: error });
		}
		if (row.job_bytes > BigInt(this.maxStoredJobBytes)) throw new SqliteMediaJobCapacityError();
	}

	private prepare(value: Readonly<StoredMediaJob>, revisionOverride?: string): PreparedMediaJob {
		let job: StoredMediaJob;
		try {
			job = captureStoredMediaJob(value, this.limits, {
				maxBytes: this.maxStoredJobBytes,
				...(revisionOverride === undefined ? {} : { revision: revisionOverride }),
			});
		} catch (error) {
			if (error instanceof JsonValueLimitError && error.code === "json_too_large") {
				throw new SqliteMediaJobCapacityError();
			}
			throw error;
		}
		assertOpaqueIdentifier(job.jobId);
		assertOpaqueIdentifier(job.adapterId);
		const revision = parseRevision(job.revision);
		const json = canonicalizeJsonValue(job as unknown as JsonValue);
		const bytes = Buffer.byteLength(json);
		if (bytes > this.maxStoredJobBytes) throw new SqliteMediaJobCapacityError();
		return {
			job,
			revision,
			json,
			bytes,
			fingerprint: createHash("sha256").update(json).digest("hex"),
		};
	}
}

function readHeader(database: Database.Database, jobId: string): MediaJobHeaderRow | undefined {
	const statement = database.prepare(`
		SELECT job_id, adapter_id, revision, job_bytes, job_fingerprint
		FROM oga_media_jobs
		WHERE job_id = ?
	`);
	statement.safeIntegers(true);
	return statement.get(jobId) as MediaJobHeaderRow | undefined;
}

function readRow(database: Database.Database, jobId: string): MediaJobRow {
	const statement = database.prepare(`
		SELECT job_id, adapter_id, revision, job_bytes, job_fingerprint, job_json
		FROM oga_media_jobs
		WHERE job_id = ?
	`);
	statement.safeIntegers(true);
	const row = statement.get(jobId) as MediaJobRow | undefined;
	if (!row) throw new SqliteMediaJobCorruptionError();
	return row;
}

function hydrate(row: MediaJobRow, header: MediaJobHeaderRow, limits: Readonly<MediaJobLimits>): StoredMediaJob {
	try {
		if (
			row.job_id !== header.job_id ||
			row.adapter_id !== header.adapter_id ||
			row.revision !== header.revision ||
			row.job_bytes !== header.job_bytes ||
			row.job_fingerprint !== header.job_fingerprint ||
			typeof row.job_json !== "string" ||
			BigInt(Buffer.byteLength(row.job_json)) !== header.job_bytes ||
			createHash("sha256").update(row.job_json).digest("hex") !== header.job_fingerprint
		) {
			throw new TypeError();
		}
		const parsed = JSON.parse(row.job_json) as unknown;
		if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) throw new TypeError();
		if (canonicalizeJsonValue(parsed as JsonValue) !== row.job_json) throw new TypeError();
		const job = captureStoredMediaJob(parsed, limits);
		if (
			job.jobId !== header.job_id ||
			job.adapterId !== header.adapter_id ||
			job.revision !== header.revision.toString()
		) {
			throw new TypeError();
		}
		return job;
	} catch (error) {
		if (error instanceof SqliteMediaJobCorruptionError) throw error;
		throw new SqliteMediaJobCorruptionError({ cause: error });
	}
}

function parseRevision(value: string): bigint {
	if (typeof value !== "string" || !/^[1-9][0-9]*$/u.test(value)) {
		throw new TypeError("Media job revision must be a positive canonical decimal string.");
	}
	if (value.length > SQLITE_INTEGER_MAX.toString().length) {
		throw new RangeError("Media job revision exceeds SQLite's signed 64-bit range.");
	}
	const revision = BigInt(value);
	if (revision > SQLITE_INTEGER_MAX) {
		throw new RangeError("Media job revision exceeds SQLite's signed 64-bit range.");
	}
	return revision;
}

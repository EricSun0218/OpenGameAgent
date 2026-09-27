import {
	ArtifactCapacityError,
	ArtifactConflictError,
	ArtifactCorruptionError,
	cloneArtifactRecord,
	freezeArtifactLimits,
	sameArtifactBinding,
	validateArtifactFingerprint,
	validateArtifactPurge,
	validateArtifactRecord,
	validateArtifactStream,
	type ArtifactLimits,
	type ArtifactRecord,
	type ArtifactStore,
} from "@opengameagent/artifacts";
import type { StreamKey } from "@opengameagent/protocol";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const migrations: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "immutable_stream_artifacts",
		sql: `
			CREATE TABLE oga_artifacts (
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				artifact_id TEXT NOT NULL,
				content_fingerprint TEXT NOT NULL,
				content_type TEXT NOT NULL,
				content_blob BLOB NOT NULL,
				byte_length INTEGER NOT NULL CHECK(byte_length > 0),
				created_at TEXT NOT NULL,
				input_id TEXT NOT NULL,
				run_id TEXT NOT NULL,
				turn TEXT NOT NULL,
				tool_call_id TEXT NOT NULL,
				tool_name TEXT NOT NULL,
				purpose TEXT NOT NULL,
				request_fingerprint TEXT NOT NULL,
				PRIMARY KEY(session_id, actor_id, artifact_id)
			) STRICT;
			CREATE INDEX oga_artifacts_partition_created
				ON oga_artifacts(session_id, actor_id, created_at, artifact_id);
		`,
	},
];

interface ArtifactRow {
	session_id: unknown;
	actor_id: unknown;
	artifact_id: unknown;
	content_fingerprint: unknown;
	content_type: unknown;
	content_blob: unknown;
	byte_length: unknown;
	created_at: unknown;
	input_id: unknown;
	run_id: unknown;
	turn: unknown;
	tool_call_id: unknown;
	tool_name: unknown;
	purpose: unknown;
	request_fingerprint: unknown;
}

interface UsageRow {
	artifact_count: unknown;
	stored_bytes: unknown;
}

const artifactColumns = `session_id, actor_id, artifact_id, content_fingerprint, content_type, content_blob,
	byte_length, created_at, input_id, run_id, turn, tool_call_id, tool_name, purpose, request_fingerprint`;

export class SqliteArtifactStore implements ArtifactStore {
	readonly limits: Readonly<ArtifactLimits>;

	constructor(
		private readonly authority: SqliteAuthority,
		limits: ArtifactLimits,
	) {
		this.limits = freezeArtifactLimits(limits);
		this.authority.install("artifacts", migrations);
	}

	async put(recordValue: Readonly<ArtifactRecord>, signal?: AbortSignal) {
		signal?.throwIfAborted();
		const record = validateArtifactRecord(recordValue, this.limits);
		return this.authority.write((database) => {
			const existingRow = database
				.prepare(`SELECT ${artifactColumns} FROM oga_artifacts
					WHERE session_id = ? AND actor_id = ? AND artifact_id = ?`)
				.get(record.sessionId, record.actorId, record.artifactId) as ArtifactRow | undefined;
			if (existingRow) {
				const existing = hydrateArtifact(existingRow, this.limits);
				if (!sameArtifactBinding(existing, record)) throw new ArtifactConflictError();
				return { status: "existing" as const, record: cloneArtifactRecord(existing) };
			}

			const usage = database
				.prepare(`SELECT COUNT(*) AS artifact_count, COALESCE(SUM(byte_length), 0) AS stored_bytes
					FROM oga_artifacts WHERE session_id = ? AND actor_id = ?`)
				.get(record.sessionId, record.actorId) as UsageRow;
			const artifactCount = safeNonnegativeInteger(usage.artifact_count);
			const storedBytes = safeNonnegativeInteger(usage.stored_bytes);
			if (
				artifactCount >= this.limits.maxArtifactsPerStream ||
				record.byteLength > this.limits.maxStoredBytesPerStream - storedBytes
			) {
				throw new ArtifactCapacityError();
			}

			database
				.prepare(`INSERT INTO oga_artifacts (${artifactColumns}) VALUES (
					?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?
				)`)
				.run(
					record.sessionId,
					record.actorId,
					record.artifactId,
					record.contentFingerprint,
					record.contentType,
					Buffer.from(record.content, "utf8"),
					record.byteLength,
					record.createdAt,
					record.origin.inputId,
					record.origin.coordinate.runId,
					record.origin.coordinate.turn,
					record.origin.toolCallId,
					record.origin.toolName,
					record.origin.purpose,
					record.origin.requestFingerprint,
				);
			return { status: "created" as const, record: cloneArtifactRecord(record) };
		});
	}

	async read(
		streamValue: Readonly<StreamKey>,
		artifactIdValue: string,
		signal?: AbortSignal,
	): Promise<ArtifactRecord | undefined> {
		signal?.throwIfAborted();
		const stream = validateArtifactStream(streamValue, this.limits);
		const artifactId = validateArtifactFingerprint(artifactIdValue, "artifactId");
		const row = this.authority.read(
			(database) =>
				database
					.prepare(`SELECT ${artifactColumns} FROM oga_artifacts
						WHERE session_id = ? AND actor_id = ? AND artifact_id = ?`)
					.get(stream.sessionId, stream.actorId, artifactId) as ArtifactRow | undefined,
		);
		return row ? cloneArtifactRecord(hydrateArtifact(row, this.limits)) : undefined;
	}

	async purge(
		streamValue: Readonly<StreamKey>,
		createdBeforeValue: string,
		limitValue: number,
		signal?: AbortSignal,
	): Promise<number> {
		signal?.throwIfAborted();
		const { stream, createdBefore, limit } = validateArtifactPurge(
			streamValue,
			createdBeforeValue,
			limitValue,
			this.limits,
		);
		return this.authority.write((database) => {
			const rows = database
				.prepare(`SELECT ${artifactColumns} FROM oga_artifacts
					WHERE session_id = ? AND actor_id = ? AND created_at < ?
					ORDER BY created_at, artifact_id LIMIT ?`)
				.all(stream.sessionId, stream.actorId, createdBefore, limit) as ArtifactRow[];
			const records = rows.map((row) => hydrateArtifact(row, this.limits));
			const remove = database.prepare(
				"DELETE FROM oga_artifacts WHERE session_id = ? AND actor_id = ? AND artifact_id = ?",
			);
			for (const record of records) remove.run(record.sessionId, record.actorId, record.artifactId);
			return records.length;
		});
	}
}

function hydrateArtifact(row: ArtifactRow, limits: Readonly<ArtifactLimits>): ArtifactRecord {
	try {
		if (!Buffer.isBuffer(row.content_blob)) throw new TypeError();
		const content = row.content_blob.toString("utf8");
		if (!Buffer.from(content, "utf8").equals(row.content_blob)) throw new TypeError();
		return validateArtifactRecord(
			{
				sessionId: row.session_id,
				actorId: row.actor_id,
				artifactId: row.artifact_id,
				contentFingerprint: row.content_fingerprint,
				contentType: row.content_type,
				content,
				byteLength: row.byte_length,
				createdAt: row.created_at,
				origin: {
					inputId: row.input_id,
					coordinate: {
						sessionId: row.session_id,
						actorId: row.actor_id,
						runId: row.run_id,
						turn: row.turn,
					},
					toolCallId: row.tool_call_id,
					toolName: row.tool_name,
					purpose: row.purpose,
					requestFingerprint: row.request_fingerprint,
				},
			},
			limits,
		);
	} catch (error) {
		if (error instanceof ArtifactCorruptionError) throw error;
		throw new ArtifactCorruptionError({ cause: error });
	}
}

function safeNonnegativeInteger(value: unknown): number {
	if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) {
		throw new ArtifactCorruptionError();
	}
	return value;
}

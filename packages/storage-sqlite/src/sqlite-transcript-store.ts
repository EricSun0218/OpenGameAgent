import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import {
	captureRetainedIdentifiers,
	assertDurableTranscriptRecord,
	assertExactTranscriptCheckpointBoundary,
	assertEventStreamCoordinate,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type DurableTranscriptRecord,
	type JsonValue,
	type StreamKey,
	type TranscriptCheckpointPurgeRequest,
	type TranscriptCheckpointPurgeResult,
	type TranscriptRetentionStore,
	type TranscriptRevision,
	TranscriptRecordIdentityConflictError,
	type TranscriptStoredStream,
	TranscriptWriteConflictError,
} from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const SQLITE_INTEGER_MAX = 9_223_372_036_854_775_807n;

const migrations: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "ordered_transcript_records",
		sql: `
			CREATE TABLE oga_transcript_streams (
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				revision INTEGER NOT NULL CHECK(revision > 0),
				record_count INTEGER NOT NULL CHECK(record_count > 0 AND record_count = revision),
				stored_bytes INTEGER NOT NULL CHECK(stored_bytes > 0),
				PRIMARY KEY(session_id, actor_id)
			) STRICT;
			CREATE TABLE oga_transcript_records (
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				sequence INTEGER NOT NULL CHECK(sequence > 0),
				record_id TEXT NOT NULL,
				schema_version TEXT NOT NULL,
				record_type TEXT NOT NULL,
				run_id TEXT NOT NULL,
				turn TEXT NOT NULL,
				record_bytes INTEGER NOT NULL CHECK(record_bytes > 0),
				record_fingerprint TEXT NOT NULL CHECK(
					length(record_fingerprint) = 64 AND record_fingerprint NOT GLOB '*[^0-9a-f]*'
				),
				record_json TEXT NOT NULL CHECK(json_valid(record_json) AND json_type(record_json) = 'object'),
				PRIMARY KEY(session_id, actor_id, sequence),
				UNIQUE(session_id, actor_id, record_id),
				FOREIGN KEY(session_id, actor_id)
					REFERENCES oga_transcript_streams(session_id, actor_id) ON DELETE CASCADE
			) STRICT;
		`,
	},
	{
		version: 2,
		name: "transcript-checkpoint-retention",
		sql: `
			ALTER TABLE oga_transcript_streams
				ADD COLUMN retained_from_sequence INTEGER NOT NULL DEFAULT 1
				CHECK(retained_from_sequence > 0 AND retained_from_sequence <= revision);
		`,
	},
];

interface StreamRow {
	revision: bigint;
	record_count: bigint;
	stored_bytes: bigint;
	retained_from_sequence: bigint;
}

interface RecordRow {
	session_id: unknown;
	actor_id: unknown;
	sequence: bigint;
	record_id: unknown;
	schema_version: unknown;
	record_type: unknown;
	run_id: unknown;
	turn: unknown;
	record_bytes: bigint;
	record_fingerprint: unknown;
	record_json: unknown;
}

interface RecordAggregateRow {
	record_count: bigint;
	earliest_sequence: bigint | null;
	latest_sequence: bigint | null;
	declared_bytes: bigint;
	json_bytes: bigint;
}

interface PreparedRecord {
	record: DurableTranscriptRecord;
	json: string;
	bytes: bigint;
	fingerprint: string;
}

export class SqliteTranscriptCorruptionError extends Error {
	constructor(options?: ErrorOptions) {
		super("The persisted durable transcript is corrupt.", options);
		this.name = "SqliteTranscriptCorruptionError";
	}
}

export class SqliteTranscriptRevisionExhaustedError extends Error {
	constructor() {
		super("The durable transcript exhausted SQLite's signed 64-bit integer revision space.");
		this.name = "SqliteTranscriptRevisionExhaustedError";
	}
}

export class SqliteTranscriptStore implements TranscriptRetentionStore {
	constructor(private readonly authority: SqliteAuthority) {
		this.authority.install("durable-transcript", migrations);
	}

	async load(streamValue: StreamKey, maxBytesValue: number): Promise<TranscriptStoredStream> {
		const stream = snapshotStream(streamValue);
		const maxBytes = positiveSafeInteger(maxBytesValue, "maxBytes");
		return this.authority.snapshot((database) => {
			const head = readHead(database, stream);
			if (!head) return { revision: "empty", records: [] };
			assertValidHead(head);
			if (head.stored_bytes > BigInt(maxBytes)) {
				throw new RangeError("The durable transcript exceeds the restore budget.");
			}
			assertRecordAggregate(database, stream, head);

			const statement = database.prepare(`
				SELECT session_id, actor_id, sequence, record_id, schema_version, record_type,
				       run_id, turn, record_bytes, record_fingerprint, record_json
				FROM oga_transcript_records
				WHERE session_id = ? AND actor_id = ?
				ORDER BY sequence
			`);
			statement.safeIntegers(true);
			const rows = statement.all(stream.sessionId, stream.actorId) as RecordRow[];
			const records: DurableTranscriptRecord[] = [];
			let storedBytes = 0n;
			let expectedSequence = head.retained_from_sequence;
			for (const row of rows) {
				const hydrated = hydrateRecord(row, stream, expectedSequence);
				storedBytes += hydrated.bytes;
				records.push(hydrated.record);
				expectedSequence += 1n;
			}
			if (
				BigInt(rows.length) !== head.revision - head.retained_from_sequence + 1n ||
				expectedSequence - 1n !== head.revision ||
				storedBytes !== head.stored_bytes
			) {
				throw new SqliteTranscriptCorruptionError();
			}
			return { revision: head.revision.toString(), records };
		});
	}

	async append(
		streamValue: StreamKey,
		expectedRevisionValue: TranscriptRevision,
		recordValues: readonly DurableTranscriptRecord[],
	): Promise<TranscriptStoredStream> {
		const stream = snapshotStream(streamValue);
		const expectedRevision = snapshotRevision(expectedRevisionValue);
		const expectedSequence = expectedRevision === "empty" ? 0n : parseSqliteRevision(expectedRevision);
		const appendCount = snapshotRecordListLength(recordValues);
		const lastSequence = expectedSequence + BigInt(appendCount);
		if (lastSequence > SQLITE_INTEGER_MAX) throw new SqliteTranscriptRevisionExhaustedError();

		const prepared: PreparedRecord[] = [];
		let appendedBytes = 0n;
		for (let index = 0; index < appendCount; index += 1) {
			const record = structuredClone(recordValues[index]);
			assertDurableTranscriptRecord(record);
			assertEventStreamCoordinate(stream, record.coordinate);
			if (record.sequence !== (expectedSequence + BigInt(index) + 1n).toString()) {
				throw new TypeError("The durable transcript append sequence is not continuous.");
			}
			const json = canonicalizeJsonValue(record as JsonValue);
			const recordBytes = BigInt(Buffer.byteLength(json));
			appendedBytes += recordBytes;
			prepared.push({
				record,
				json,
				bytes: recordBytes,
				fingerprint: createHash("sha256").update(json).digest("hex"),
			});
		}

		return this.authority.write((database) => {
			const head = readHead(database, stream);
			if (head) assertValidHead(head);
			const actual: TranscriptRevision = head ? head.revision.toString() : "empty";
			if (actual !== expectedRevision) throw new TranscriptWriteConflictError(expectedRevision, actual);
			const storedBytes = (head?.stored_bytes ?? 0n) + appendedBytes;
			if (storedBytes > SQLITE_INTEGER_MAX) {
				throw new RangeError("The durable transcript exceeds SQLite's signed 64-bit byte accounting range.");
			}
			const recordCount = (head?.record_count ?? 0n) + BigInt(prepared.length);
			if (!head) {
				database
					.prepare(`
						INSERT INTO oga_transcript_streams(
							session_id, actor_id, revision, record_count, stored_bytes, retained_from_sequence
						) VALUES (?, ?, ?, ?, ?, 1)
					`)
					.run(stream.sessionId, stream.actorId, lastSequence, recordCount, storedBytes);
			}

			const insert = database.prepare(`
				INSERT INTO oga_transcript_records(
					session_id, actor_id, sequence, record_id, schema_version, record_type,
					run_id, turn, record_bytes, record_fingerprint, record_json
				) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
				ON CONFLICT(session_id, actor_id, record_id) DO NOTHING
			`);
			for (const item of prepared) {
				const inserted = insert.run(
					stream.sessionId,
					stream.actorId,
					BigInt(item.record.sequence),
					item.record.recordId,
					item.record.schemaVersion,
					item.record.type,
					item.record.coordinate.runId,
					item.record.coordinate.turn,
					item.bytes,
					item.fingerprint,
					item.json,
				);
				if (inserted.changes !== 1) throw new TranscriptRecordIdentityConflictError();
			}
			if (head) {
				const updated = database
					.prepare(`
						UPDATE oga_transcript_streams
						SET revision = ?, record_count = ?, stored_bytes = ?
						WHERE session_id = ? AND actor_id = ? AND revision = ?
					`)
					.run(lastSequence, recordCount, storedBytes, stream.sessionId, stream.actorId, head.revision);
				if (updated.changes !== 1) throw new TranscriptWriteConflictError(expectedRevision, actual);
			}
			return {
				revision: lastSequence.toString(),
				records: prepared.map(({ record }) => structuredClone(record)),
			};
		});
	}

	async purgeBeforeCheckpoint(request: TranscriptCheckpointPurgeRequest): Promise<TranscriptCheckpointPurgeResult> {
		const captured = captureCheckpointPurgeRequest(request);
		return this.authority.write((database) => {
			const stream = { sessionId: captured.sessionId, actorId: captured.actorId };
			const head = readHead(database, captured);
			if (!head) throw new TypeError("The requested transcript checkpoint is unavailable.");
			assertValidHead(head);
			const checkpointRow = readRecord(database, captured, BigInt(captured.checkpointRevision));
			if (!checkpointRow) throw new TypeError("The requested transcript checkpoint is unavailable.");
			const checkpoint = hydrateRecord(checkpointRow, stream, BigInt(captured.checkpointRevision)).record;
			if (checkpoint.type !== "context.checkpoint") throw new TypeError("The requested revision is not a checkpoint.");
			const sourceSequence = BigInt(checkpoint.sourceRevision);
			const sourceRow = readRecord(database, captured, sourceSequence);
			if (!sourceRow) throw new TypeError("The transcript checkpoint source boundary is unavailable.");
			const source = hydrateRecord(sourceRow, stream, sourceSequence).record;
			assertExactTranscriptCheckpointBoundary(checkpoint, source);
			const purgeCount = sourceSequence - head.retained_from_sequence;
			if (
				purgeCount > BigInt(captured.limit) ||
				captured.retainRevisions.some((revision) => revision === "empty" || BigInt(revision) <= sourceSequence)
			) {
				return checkpointPurgeResult(head, 0n);
			}
			if (purgeCount === 0n) return checkpointPurgeResult(head, 0n);
			const aggregate = database
				.prepare(`
					SELECT COUNT(*) AS record_count, COALESCE(SUM(record_bytes), 0) AS stored_bytes
					FROM oga_transcript_records
					WHERE session_id = ? AND actor_id = ?
						AND sequence >= ? AND sequence < ?
				`)
				.safeIntegers(true)
				.get(captured.sessionId, captured.actorId, head.retained_from_sequence, sourceSequence) as {
				record_count: bigint;
				stored_bytes: bigint;
			};
			if (aggregate.record_count !== purgeCount || aggregate.stored_bytes <= 0n) {
				throw new SqliteTranscriptCorruptionError();
			}
			database
				.prepare(`
					DELETE FROM oga_transcript_records
					WHERE session_id = ? AND actor_id = ? AND sequence < ?
				`)
				.run(captured.sessionId, captured.actorId, sourceSequence);
			const retainedBytes = head.stored_bytes - aggregate.stored_bytes;
			const updated = database
				.prepare(`
					UPDATE oga_transcript_streams
					SET retained_from_sequence = ?, stored_bytes = ?
					WHERE session_id = ? AND actor_id = ? AND revision = ? AND retained_from_sequence = ?
				`)
				.run(
					sourceSequence,
					retainedBytes,
					captured.sessionId,
					captured.actorId,
					head.revision,
					head.retained_from_sequence,
				);
			if (updated.changes !== 1) throw new TranscriptWriteConflictError(head.revision.toString(), "empty");
			return checkpointPurgeResult(
				{ ...head, retained_from_sequence: sourceSequence, stored_bytes: retainedBytes },
				purgeCount,
			);
		});
	}
}

function readHead(database: Database.Database, stream: StreamKey): StreamRow | undefined {
	const statement = database.prepare(`
		SELECT revision, record_count, stored_bytes, retained_from_sequence
		FROM oga_transcript_streams
		WHERE session_id = ? AND actor_id = ?
	`);
	statement.safeIntegers(true);
	return statement.get(stream.sessionId, stream.actorId) as StreamRow | undefined;
}

function assertValidHead(head: StreamRow): void {
	if (
		typeof head.revision !== "bigint" ||
		typeof head.record_count !== "bigint" ||
		typeof head.stored_bytes !== "bigint" ||
		typeof head.retained_from_sequence !== "bigint" ||
		head.revision <= 0n ||
		head.record_count !== head.revision ||
		head.stored_bytes <= 0n ||
		head.retained_from_sequence <= 0n ||
		head.retained_from_sequence > head.revision
	) {
		throw new SqliteTranscriptCorruptionError();
	}
}

function assertRecordAggregate(database: Database.Database, stream: StreamKey, head: StreamRow): void {
	const statement = database.prepare(`
		SELECT COUNT(*) AS record_count,
		       MIN(sequence) AS earliest_sequence,
		       MAX(sequence) AS latest_sequence,
		       COALESCE(SUM(record_bytes), 0) AS declared_bytes,
		       COALESCE(SUM(length(CAST(record_json AS BLOB))), 0) AS json_bytes
		FROM oga_transcript_records
		WHERE session_id = ? AND actor_id = ?
	`);
	statement.safeIntegers(true);
	const aggregate = statement.get(stream.sessionId, stream.actorId) as RecordAggregateRow;
	if (
		aggregate.record_count !== head.revision - head.retained_from_sequence + 1n ||
		aggregate.earliest_sequence !== head.retained_from_sequence ||
		aggregate.latest_sequence !== head.revision ||
		aggregate.declared_bytes !== head.stored_bytes ||
		aggregate.json_bytes !== head.stored_bytes
	) {
		throw new SqliteTranscriptCorruptionError();
	}
}

function readRecord(database: Database.Database, stream: StreamKey, sequence: bigint): RecordRow | undefined {
	const statement = database.prepare(`
		SELECT session_id, actor_id, sequence, record_id, schema_version, record_type,
		       run_id, turn, record_bytes, record_fingerprint, record_json
		FROM oga_transcript_records
		WHERE session_id = ? AND actor_id = ? AND sequence = ?
	`);
	statement.safeIntegers(true);
	return statement.get(stream.sessionId, stream.actorId, sequence) as RecordRow | undefined;
}

function captureCheckpointPurgeRequest(
	request: TranscriptCheckpointPurgeRequest,
): Readonly<TranscriptCheckpointPurgeRequest> {
	const stream = snapshotStream(request);
	if (parseSqliteRevision(request.checkpointRevision) < 1n) {
		throw new TypeError("checkpointRevision must be a positive canonical decimal.");
	}
	positiveSafeInteger(request.limit, "limit");
	const retainBudget = request.retainBudget;
	const retainRevisions = captureRetainedIdentifiers(request.retainRevisions, retainBudget, "retainRevisions");
	for (const revision of retainRevisions) {
		if (revision !== "empty") parseSqliteRevision(revision);
	}
	return Object.freeze({
		...stream,
		checkpointRevision: request.checkpointRevision,
		limit: request.limit,
		retainRevisions,
		retainBudget: Object.freeze({ maxCount: retainBudget.maxCount, maxBytes: retainBudget.maxBytes }),
	});
}

function checkpointPurgeResult(head: StreamRow, purgedRecordCount: bigint): TranscriptCheckpointPurgeResult {
	return {
		purgedRecordCount: Number(purgedRecordCount),
		earliestAvailableRevision: head.retained_from_sequence.toString(),
		latestRevision: head.revision.toString(),
	};
}

function hydrateRecord(row: RecordRow, stream: StreamKey, expectedSequence: bigint): PreparedRecord {
	try {
		if (
			typeof row.record_json !== "string" ||
			typeof row.record_fingerprint !== "string" ||
			typeof row.record_bytes !== "bigint" ||
			row.record_bytes <= 0n ||
			row.sequence !== expectedSequence ||
			BigInt(Buffer.byteLength(row.record_json)) !== row.record_bytes ||
			createHash("sha256").update(row.record_json).digest("hex") !== row.record_fingerprint
		) {
			throw new TypeError();
		}
		const parsed = JSON.parse(row.record_json) as unknown;
		assertDurableTranscriptRecord(parsed);
		assertEventStreamCoordinate(stream, parsed.coordinate);
		if (
			row.session_id !== stream.sessionId ||
			row.actor_id !== stream.actorId ||
			parsed.sequence !== row.sequence.toString() ||
			parsed.recordId !== row.record_id ||
			parsed.schemaVersion !== row.schema_version ||
			parsed.type !== row.record_type ||
			parsed.coordinate.runId !== row.run_id ||
			parsed.coordinate.turn !== row.turn ||
			canonicalizeJsonValue(parsed as JsonValue) !== row.record_json
		) {
			throw new TypeError();
		}
		return {
			record: structuredClone(parsed),
			json: row.record_json,
			bytes: row.record_bytes,
			fingerprint: row.record_fingerprint,
		};
	} catch (error) {
		if (error instanceof SqliteTranscriptCorruptionError) throw error;
		throw new SqliteTranscriptCorruptionError({ cause: error });
	}
}

function snapshotStream(stream: StreamKey): Readonly<StreamKey> {
	const snapshot = { sessionId: stream.sessionId, actorId: stream.actorId };
	assertOpaqueIdentifier(snapshot.sessionId);
	assertOpaqueIdentifier(snapshot.actorId);
	return Object.freeze(snapshot);
}

function snapshotRevision(value: TranscriptRevision): TranscriptRevision {
	if (value === "empty") return value;
	parseSqliteRevision(value);
	return value;
}

function snapshotRecordListLength(records: readonly DurableTranscriptRecord[]): number {
	if (!Array.isArray(records)) throw new TypeError("The durable transcript record list is invalid.");
	const length = records.length;
	if (!Number.isSafeInteger(length) || length < 1) {
		throw new TypeError("The durable transcript append requires at least one record.");
	}
	return length;
}

function parseSqliteRevision(value: string): bigint {
	if (typeof value !== "string") {
		throw new TypeError("Transcript revision must be a non-negative canonical decimal string.");
	}
	if (value.length > SQLITE_INTEGER_MAX.toString().length) {
		throw new RangeError("Transcript revision exceeds SQLite's signed 64-bit range.");
	}
	if (!/^(0|[1-9][0-9]*)$/u.test(value)) {
		throw new TypeError("Transcript revision must be a non-negative canonical decimal string.");
	}
	const revision = BigInt(value);
	if (revision > SQLITE_INTEGER_MAX) throw new RangeError("Transcript revision exceeds SQLite's signed 64-bit range.");
	return revision;
}

function positiveSafeInteger(value: number, name: string): number {
	if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer.`);
	return value;
}

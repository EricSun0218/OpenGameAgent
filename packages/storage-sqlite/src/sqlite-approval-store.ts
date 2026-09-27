import {
	ActionApprovalConflictError,
	type ActionApprovalConsumeInput,
	type ActionApprovalDecisionInput,
	type ActionApprovalExpireInput,
	type ActionApprovalListRequest,
	ActionApprovalNotFoundError,
	type ActionApprovalRecord,
	type ActionApprovalRequest,
	type ActionApprovalStore,
	type ActionApprovalTerminalPurgeRequest,
	assertActionApprovalRecord,
	assertActionApprovalRequest,
	assertPositiveSafeInteger,
	captureApprovalTerminalPurgeRequest,
	consumeActionApprovalRecord,
	decideActionApprovalRecord,
	expireActionApprovalRecord,
} from "@opengameagent/actions";
import { assertCanonicalTimestamp, assertOpaqueIdentifier, type StreamKey } from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const SQLITE_INTEGER_MAX = 9_223_372_036_854_775_807n;

const APPROVAL_MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "action-approvals",
		sql: `
			CREATE TABLE action_approvals (
				approval_sequence INTEGER PRIMARY KEY,
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				approval_id TEXT NOT NULL,
				request_fingerprint TEXT NOT NULL CHECK(length(request_fingerprint) = 64),
				status TEXT NOT NULL CHECK(status IN ('pending', 'approved', 'rejected', 'consumed', 'expired')),
				revision INTEGER NOT NULL CHECK(revision > 0),
				expires_at TEXT NOT NULL,
				record_json TEXT NOT NULL CHECK(json_valid(record_json)),
				UNIQUE(session_id, actor_id, approval_id)
			) STRICT;
			CREATE INDEX action_approvals_pending
				ON action_approvals(session_id, actor_id, status, expires_at, approval_sequence);
		`,
	},
	{
		version: 2,
		name: "action-approval-run-selection",
		sql: `
			ALTER TABLE action_approvals ADD COLUMN run_id TEXT;
			UPDATE action_approvals
			SET run_id = json_extract(record_json, '$.request.coordinate.runId')
			WHERE json_type(record_json, '$.request.coordinate') = 'object';
			CREATE INDEX action_approvals_run_pending
				ON action_approvals(session_id, actor_id, run_id, status, expires_at, approval_sequence);
		`,
	},
	{
		version: 3,
		name: "action-approval-terminal-retention",
		sql: `
			CREATE INDEX action_approvals_terminal_retention
				ON action_approvals(
					session_id, actor_id, status,
					json_extract(record_json, '$.updatedAt'), approval_sequence
				);
		`,
	},
];

export class SqliteApprovalRevisionExhaustedError extends Error {
	constructor() {
		super("The action approval exhausted SQLite's signed 64-bit integer revision space.");
		this.name = "SqliteApprovalRevisionExhaustedError";
	}
}

interface ApprovalRow {
	approval_sequence: bigint;
	run_id: string | null;
	request_fingerprint: string;
	status: string;
	revision: bigint;
	expires_at: string;
	record_json: string;
}

export class SqliteActionApprovalStore implements ActionApprovalStore {
	static install(authority: SqliteAuthority): void {
		authority.install("action-approvals", APPROVAL_MIGRATIONS);
	}

	constructor(private readonly authority: SqliteAuthority) {
		SqliteActionApprovalStore.install(this.authority);
	}

	async ensure(request: ActionApprovalRequest): Promise<ActionApprovalRecord> {
		assertActionApprovalRequest(request);
		return this.authority.write((database) => {
			const existing = selectRecord(database, request, request.approvalId);
			if (existing) {
				if (existing.request.requestFingerprint !== request.requestFingerprint) {
					throw new ActionApprovalConflictError();
				}
				return existing;
			}
			const record: ActionApprovalRecord = {
				request: structuredClone(request),
				status: "pending",
				revision: "1",
				updatedAt: request.requestedAt,
			};
			assertActionApprovalRecord(record);
			database
				.prepare(`
					INSERT INTO action_approvals(
						session_id, actor_id, run_id, approval_id, request_fingerprint,
						status, revision, expires_at, record_json
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
				`)
				.run(
					request.sessionId,
					request.actorId,
					request.coordinate.runId,
					request.approvalId,
					request.requestFingerprint,
					record.status,
					1n,
					request.expiresAt,
					JSON.stringify(record),
				);
			return structuredClone(record);
		});
	}

	async get(stream: StreamKey, approvalId: string): Promise<ActionApprovalRecord | undefined> {
		validateStream(stream);
		assertOpaqueIdentifier(approvalId);
		return this.authority.read((database) => selectRecord(database, stream, approvalId));
	}

	async listPending(request: ActionApprovalListRequest, now: string): Promise<ActionApprovalRecord[]> {
		validateStream(request);
		if (request.runId !== undefined) assertOpaqueIdentifier(request.runId);
		assertPositiveSafeInteger(request.limit, "limit");
		assertCanonicalTimestamp(now);
		return this.authority.read((database) => {
			const runFilter = request.runId === undefined ? "" : " AND run_id = ?";
			const statement = database.prepare(`
				SELECT approval_sequence, run_id, request_fingerprint, status, revision, expires_at, record_json
				FROM action_approvals
				WHERE session_id = ? AND actor_id = ? AND run_id IS NOT NULL${runFilter}
					AND status = 'pending' AND expires_at > ?
				ORDER BY approval_sequence
				LIMIT ?
			`);
			statement.safeIntegers(true);
			return (
				statement.all(
					request.sessionId,
					request.actorId,
					...(request.runId === undefined ? [] : [request.runId]),
					now,
					BigInt(request.limit),
				) as ApprovalRow[]
			).map(rowToRecord);
		});
	}

	async decide(input: ActionApprovalDecisionInput): Promise<ActionApprovalRecord> {
		return this.authority.write((database) => {
			const row = requireRow(database, input, input.approvalId);
			if (row.revision >= SQLITE_INTEGER_MAX) throw new SqliteApprovalRevisionExhaustedError();
			const decided = decideActionApprovalRecord(rowToRecord(row), input);
			updateRecord(database, row.approval_sequence, decided);
			return structuredClone(decided);
		});
	}

	async consume(input: ActionApprovalConsumeInput): Promise<ActionApprovalRecord> {
		return this.authority.write((database) => {
			const row = requireRow(database, input, input.approvalId);
			if (row.revision >= SQLITE_INTEGER_MAX) throw new SqliteApprovalRevisionExhaustedError();
			const consumed = consumeActionApprovalRecord(rowToRecord(row), input);
			updateRecord(database, row.approval_sequence, consumed);
			return structuredClone(consumed);
		});
	}

	async expire(input: ActionApprovalExpireInput): Promise<ActionApprovalRecord> {
		input.signal?.throwIfAborted();
		return this.authority.write((database) => {
			input.signal?.throwIfAborted();
			const row = requireRow(database, input, input.approvalId);
			if (row.revision >= SQLITE_INTEGER_MAX) throw new SqliteApprovalRevisionExhaustedError();
			const expired = expireActionApprovalRecord(rowToRecord(row), input);
			updateRecord(database, row.approval_sequence, expired);
			return structuredClone(expired);
		});
	}

	async purgeTerminal(request: ActionApprovalTerminalPurgeRequest): Promise<readonly string[]> {
		const captured = captureApprovalTerminalPurgeRequest(request);
		return this.authority.write((database) => {
			const statement = database.prepare(`
				SELECT approval_sequence, approval_id
				FROM action_approvals
				WHERE session_id = ? AND actor_id = ?
					AND status IN ('rejected', 'consumed', 'expired')
					AND json_extract(record_json, '$.updatedAt') < ?
					AND approval_id NOT IN (SELECT value FROM json_each(?))
				ORDER BY json_extract(record_json, '$.updatedAt'), approval_sequence
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				captured.sessionId,
				captured.actorId,
				captured.beforeUpdatedAt,
				JSON.stringify(captured.retainApprovalIds),
				BigInt(captured.limit),
			) as Array<{ approval_sequence: bigint; approval_id: string }>;
			const remove = database.prepare("DELETE FROM action_approvals WHERE approval_sequence = ?");
			for (const row of rows) remove.run(row.approval_sequence);
			return rows.map(({ approval_id }) => approval_id);
		});
	}
}

function selectRecord(
	database: Database.Database,
	stream: StreamKey,
	approvalId: string,
): ActionApprovalRecord | undefined {
	const row = selectRow(database, stream, approvalId);
	return row ? rowToRecord(row) : undefined;
}

function selectRow(database: Database.Database, stream: StreamKey, approvalId: string): ApprovalRow | undefined {
	const statement = database.prepare(`
		SELECT approval_sequence, run_id, request_fingerprint, status, revision, expires_at, record_json
		FROM action_approvals
		WHERE session_id = ? AND actor_id = ? AND approval_id = ?
	`);
	statement.safeIntegers(true);
	return statement.get(stream.sessionId, stream.actorId, approvalId) as ApprovalRow | undefined;
}

function requireRow(database: Database.Database, stream: StreamKey, approvalId: string): ApprovalRow {
	validateStream(stream);
	assertOpaqueIdentifier(approvalId);
	const row = selectRow(database, stream, approvalId);
	if (!row) throw new ActionApprovalNotFoundError();
	return row;
}

function rowToRecord(row: ApprovalRow): ActionApprovalRecord {
	const parsed = JSON.parse(row.record_json) as unknown;
	assertActionApprovalRecord(parsed as ActionApprovalRecord);
	const record = parsed as ActionApprovalRecord;
	if (
		record.request.coordinate.runId !== row.run_id ||
		record.request.requestFingerprint !== row.request_fingerprint ||
		record.status !== row.status ||
		record.revision !== row.revision.toString() ||
		record.request.expiresAt !== row.expires_at
	) {
		throw new TypeError("The durable approval index does not match its canonical record.");
	}
	return record;
}

function updateRecord(database: Database.Database, sequence: bigint, record: ActionApprovalRecord): void {
	assertActionApprovalRecord(record);
	const result = database
		.prepare(`
			UPDATE action_approvals
			SET status = ?, revision = ?, record_json = ?
			WHERE approval_sequence = ?
		`)
		.run(record.status, BigInt(record.revision), JSON.stringify(record), sequence);
	if (result.changes !== 1) throw new ActionApprovalNotFoundError();
}

function validateStream(stream: StreamKey): void {
	assertOpaqueIdentifier(stream.sessionId);
	assertOpaqueIdentifier(stream.actorId);
}

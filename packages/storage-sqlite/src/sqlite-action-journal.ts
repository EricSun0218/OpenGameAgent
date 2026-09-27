import {
	type ActionClaimRequest,
	type ActionDeliverySelection,
	type ActionGoalProjectionLimits,
	type ActionGoalProjectionListRequest,
	type ActionGoalProjectionPage,
	type ActionGoalProjectionSettlementInput,
	ActionIntentConflictError,
	type ActionJournal,
	type ActionJournalOptions,
	ActionNotFoundError,
	type ActionReceiptInput,
	type ActionReconcileInput,
	type ActionRecord,
	type ActionRecoveryDelivery,
	type ActionTerminalPurgeRequest,
	assertActionGoalProjectionRecordFits,
	assertActionRecord,
	captureActionGoalProjectionLimits,
	captureActionGoalProjectionListRequest,
	captureActionTerminalPurgeRequest,
	claimActionRecord,
	expireActionRecord,
	isActionRunAuthorityOpen,
	type PreparedActionIntent,
	receiptActionRecord,
	reclaimActionRecord,
	reconcileActionRecord,
	retirePendingActionRecord,
	settleActionGoalProjectionRecord,
	validateActionClaimRequest,
} from "@opengameagent/actions";
import { assertOpaqueIdentifier, type StreamKey } from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import {
	assertSqliteActionGoalProjectionCapacity,
	assertSqliteActionProjectionHydrationFits,
	assertSqliteActionProjectionIndexes,
} from "./sqlite-action-projection.js";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";
import { assertSqliteRunAuthorityOpen, installSqliteRunAuthority } from "./sqlite-run-authority.js";

const SQLITE_INTEGER_MAX = 9_223_372_036_854_775_807n;

const ACTION_MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "durable-actions",
		sql: `
			CREATE TABLE action_records (
				action_sequence INTEGER PRIMARY KEY,
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				operation_id TEXT NOT NULL,
				intent_fingerprint TEXT NOT NULL CHECK(length(intent_fingerprint) = 64),
				status TEXT NOT NULL CHECK(status IN ('pending', 'claimed', 'succeeded', 'failed', 'uncertain')),
				attempt INTEGER NOT NULL CHECK(attempt >= 0),
				claim_expires_at TEXT,
				record_json TEXT NOT NULL CHECK(json_valid(record_json)),
				CHECK((status IN ('claimed', 'uncertain')) = (claim_expires_at IS NOT NULL)),
				UNIQUE(session_id, actor_id, operation_id)
			) STRICT;
			CREATE INDEX action_records_claim_queue
				ON action_records(session_id, status, action_sequence);
			CREATE INDEX action_records_expired_claims
				ON action_records(session_id, status, claim_expires_at, action_sequence);
			CREATE TABLE action_conflicts (
				session_id TEXT NOT NULL,
				conflict_key TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				operation_id TEXT NOT NULL,
				PRIMARY KEY(session_id, conflict_key),
				FOREIGN KEY(session_id, actor_id, operation_id)
					REFERENCES action_records(session_id, actor_id, operation_id)
					ON DELETE CASCADE
			) STRICT;
		`,
	},
	{
		version: 2,
		name: "durable-action-goal-projections",
		sql: `
			ALTER TABLE action_records
				ADD COLUMN has_goal_projection INTEGER NOT NULL DEFAULT 0
				CHECK(has_goal_projection IN (0, 1));
			ALTER TABLE action_records
				ADD COLUMN goal_projection_outcome TEXT
				CHECK(
					goal_projection_outcome IS NULL OR (
						has_goal_projection = 1
						AND goal_projection_outcome IN ('advanced', 'source_failed')
					)
				);
			ALTER TABLE action_records
				ADD COLUMN goal_projection_settled_at TEXT
				CHECK((goal_projection_settled_at IS NULL) = (goal_projection_outcome IS NULL));
			CREATE INDEX action_records_goal_projection_recovery
				ON action_records(
					session_id, actor_id, has_goal_projection,
					goal_projection_settled_at, action_sequence
				);
		`,
	},
	{
		version: 3,
		name: "durable-action-run-selection",
		sql: `
			CREATE TABLE action_v3_empty_guard (
				legacy_action_count INTEGER NOT NULL CHECK(legacy_action_count = 0)
			) STRICT;
			INSERT INTO action_v3_empty_guard
			SELECT count(*) FROM action_records;
			DROP TABLE action_v3_empty_guard;
			ALTER TABLE action_records ADD COLUMN run_id TEXT;
			CREATE INDEX action_records_run_claim_queue
				ON action_records(session_id, actor_id, run_id, status, action_sequence);
			CREATE INDEX action_records_run_expired_claims
				ON action_records(session_id, actor_id, run_id, status, claim_expires_at, action_sequence);
		`,
	},
	{
		version: 4,
		name: "durable-action-recovery-selection",
		sql: `
			CREATE INDEX action_records_recovery_queue
				ON action_records(session_id, actor_id, status, action_sequence);
		`,
	},
	{
		version: 5,
		name: "durable-action-terminal-retention",
		sql: `
			CREATE INDEX action_records_terminal_retention
				ON action_records(
					session_id, actor_id, status,
					json_extract(record_json, '$.updatedAt'), action_sequence
				)
				WHERE status IN ('succeeded', 'failed');
		`,
	},
];

export class SqliteActionAttemptExhaustedError extends Error {
	constructor() {
		super("The durable action exhausted SQLite's signed 64-bit integer attempt space.");
		this.name = "SqliteActionAttemptExhaustedError";
	}
}

interface ActionRow {
	action_sequence: bigint;
	run_id: string | null;
	status: string;
	attempt: bigint;
	claim_expires_at: string | null;
	has_goal_projection: bigint;
	goal_projection_outcome: string | null;
	goal_projection_settled_at: string | null;
	record_json: string;
	record_bytes: bigint;
}

export class SqliteActionJournal implements ActionJournal {
	readonly goalProjectionLimits?: Readonly<ActionGoalProjectionLimits>;

	static install(authority: SqliteAuthority): void {
		authority.install("durable-actions", ACTION_MIGRATIONS);
		installSqliteRunAuthority(authority);
	}

	constructor(
		private readonly authority: SqliteAuthority,
		options: ActionJournalOptions = {},
	) {
		SqliteActionJournal.install(this.authority);
		const goalProjectionLimits = captureActionGoalProjectionLimits(options.goalProjection);
		if (goalProjectionLimits !== undefined) this.goalProjectionLimits = goalProjectionLimits;
	}

	async ensure(intent: PreparedActionIntent, now: string): Promise<ActionRecord> {
		return this.authority.write((database) => {
			const existing = selectRecord(database, intent, intent.operationId, this.goalProjectionLimits);
			if (existing) {
				if (existing.intentFingerprint !== intent.intentFingerprint) throw new ActionIntentConflictError();
				return existing;
			}
			if (intent.goalProjection !== undefined) {
				const limits = this.requireGoalProjectionLimits();
				assertSqliteActionGoalProjectionCapacity(database, intent, limits);
			}
			const record: ActionRecord = {
				...structuredClone(intent),
				status: "pending",
				attempt: "0",
				createdAt: now,
				updatedAt: now,
			};
			assertActionRecord(record, this.goalProjectionLimits);
			assertActionGoalProjectionRecordFits(record, this.goalProjectionLimits);
			assertSqliteRunAuthorityOpen(database, record.coordinate);
			database
				.prepare(`
					INSERT INTO action_records(
						session_id, actor_id, run_id, operation_id, intent_fingerprint,
						status, attempt, claim_expires_at, has_goal_projection,
						goal_projection_outcome, goal_projection_settled_at, record_json
					) VALUES (?, ?, ?, ?, ?, ?, ?, NULL, ?, NULL, NULL, ?)
				`)
				.run(
					record.sessionId,
					record.actorId,
					record.coordinate.runId,
					record.operationId,
					record.intentFingerprint,
					record.status,
					0n,
					record.goalProjection === undefined ? 0n : 1n,
					JSON.stringify(record),
				);
			return structuredClone(record);
		});
	}

	async get(stream: StreamKey, operationId: string): Promise<ActionRecord | undefined> {
		validateStream(stream);
		assertOpaqueIdentifier(operationId);
		return this.authority.read((database) => selectRecord(database, stream, operationId, this.goalProjectionLimits));
	}

	async recoverDelivery(stream: StreamKey): Promise<ActionRecoveryDelivery | undefined> {
		validateStream(stream);
		return this.authority.read((database) => {
			const unsettled = selectOldestUnsettled(database, stream, this.goalProjectionLimits);
			return unsettled ? { kind: "reconcile", action: structuredClone(unsettled) } : undefined;
		});
	}

	async nextDelivery(request: ActionClaimRequest): Promise<ActionDeliverySelection> {
		validateActionClaimRequest(request);
		return this.authority.write((database) => {
			const retired = retireClosedPending(database, request, this.goalProjectionLimits);
			const { replayable, changed: expired } = expireDueClaims(database, request, this.goalProjectionLimits);
			const changed = retired || expired;
			const unsettled = selectOldestUnsettled(database, request, this.goalProjectionLimits, replayable);
			if (unsettled) return { changed, delivery: { kind: "reconcile", action: structuredClone(unsettled) } };
			const claimed = claimPendingRecord(database, request, replayable, this.goalProjectionLimits);
			if (claimed) return { changed: true, delivery: { kind: "dispatch", action: structuredClone(claimed) } };
			const waitingReplay = selectOldestUnsettled(database, request, this.goalProjectionLimits);
			return waitingReplay
				? { changed, delivery: { kind: "reconcile", action: structuredClone(waitingReplay) } }
				: { changed };
		});
	}

	async recordReceipt(input: ActionReceiptInput): Promise<ActionRecord> {
		return this.authority.write((database) => {
			const row = requireRow(database, input, input.operationId);
			const record = rowToRecord(row, this.goalProjectionLimits);
			const transition = receiptActionRecord(record, input);
			if (transition.record !== record) {
				if (transition.releaseConflicts) release(database, transition.record);
				updateRecord(database, row.action_sequence, transition.record, this.goalProjectionLimits);
			}
			return structuredClone(transition.record);
		});
	}

	async reconcile(input: ActionReconcileInput): Promise<ActionRecord> {
		return this.authority.write((database) => {
			const row = requireRow(database, input, input.operationId);
			const record = rowToRecord(row, this.goalProjectionLimits);
			const transition = reconcileActionRecord(record, input);
			if (transition.record.status === "pending" && !isRunOpen(database, record)) {
				transition.record = retirePendingActionRecord(transition.record, input.reconciledAt).record;
			}
			if (transition.record !== record) {
				if (transition.releaseConflicts) release(database, transition.record);
				updateRecord(database, row.action_sequence, transition.record, this.goalProjectionLimits);
			}
			return structuredClone(transition.record);
		});
	}

	async listGoalProjections(request: ActionGoalProjectionListRequest): Promise<ActionGoalProjectionPage> {
		const limits = this.requireGoalProjectionLimits();
		const captured = captureActionGoalProjectionListRequest(request, limits);
		const afterSequence = BigInt(captured.afterSequence ?? "0");
		if (afterSequence > SQLITE_INTEGER_MAX) return { candidates: [] };
		return this.authority.read((database) => {
			const statement = database.prepare(`
				SELECT action_sequence, run_id, status, attempt, claim_expires_at,
					has_goal_projection, goal_projection_outcome, goal_projection_settled_at, record_json,
					length(CAST(record_json AS BLOB)) AS record_bytes
				FROM action_records
				WHERE session_id = ? AND actor_id = ?
					AND has_goal_projection = 1
					AND goal_projection_settled_at IS NULL
					AND action_sequence > ?
				ORDER BY action_sequence
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				captured.sessionId,
				captured.actorId,
				afterSequence,
				BigInt(captured.limit) + 1n,
			) as ActionRow[];
			const hasMore = rows.length > captured.limit;
			const candidates = rows.slice(0, captured.limit).map((row) => ({
				sequence: row.action_sequence.toString(),
				action: structuredClone(rowToRecord(row, limits)),
			}));
			const nextAfterSequence = hasMore ? candidates.at(-1)?.sequence : undefined;
			return {
				candidates,
				...(nextAfterSequence === undefined ? {} : { nextAfterSequence }),
			};
		});
	}

	async settleGoalProjection(input: ActionGoalProjectionSettlementInput): Promise<ActionRecord> {
		const limits = this.requireGoalProjectionLimits();
		return this.authority.write((database) => {
			const row = requireRow(database, input, input.operationId);
			const record = rowToRecord(row, limits);
			const settled = settleActionGoalProjectionRecord(record, input);
			if (settled !== record) updateRecord(database, row.action_sequence, settled, limits);
			return structuredClone(settled);
		});
	}

	async purgeTerminal(request: ActionTerminalPurgeRequest): Promise<readonly string[]> {
		const captured = captureActionTerminalPurgeRequest(request);
		return this.authority.write((database) => {
			const statement = database.prepare(`
				SELECT action_sequence, operation_id
				FROM action_records
				WHERE session_id = ? AND actor_id = ?
					AND status IN ('succeeded', 'failed')
					AND json_extract(record_json, '$.updatedAt') < ?
					AND operation_id NOT IN (SELECT value FROM json_each(?))
					AND (has_goal_projection = 0 OR goal_projection_settled_at IS NOT NULL)
				ORDER BY json_extract(record_json, '$.updatedAt'), action_sequence
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				captured.sessionId,
				captured.actorId,
				captured.beforeUpdatedAt,
				JSON.stringify(captured.retainOperationIds),
				BigInt(captured.limit),
			) as Array<{ action_sequence: bigint; operation_id: string }>;
			const remove = database.prepare("DELETE FROM action_records WHERE action_sequence = ?");
			for (const row of rows) remove.run(row.action_sequence);
			return rows.map(({ operation_id }) => operation_id);
		});
	}

	private requireGoalProjectionLimits(): Readonly<ActionGoalProjectionLimits> {
		const limits = this.goalProjectionLimits;
		if (limits === undefined) throw new TypeError("Goal projection limits are not configured.");
		return limits;
	}
}

function selectOldestUnsettled(
	database: Database.Database,
	stream: StreamKey & { runId?: string },
	limits?: Readonly<ActionGoalProjectionLimits>,
	replayable: readonly bigint[] = [],
): ActionRecord | undefined {
	const runFilter = stream.runId === undefined ? "" : " AND run_id = ?";
	const statement = database.prepare(`
		SELECT action_sequence, run_id, status, attempt, claim_expires_at,
			has_goal_projection, goal_projection_outcome, goal_projection_settled_at, record_json,
			length(CAST(record_json AS BLOB)) AS record_bytes
		FROM action_records
		WHERE session_id = ? AND actor_id = ?${runFilter} AND status IN ('claimed', 'uncertain')
			AND action_sequence NOT IN (SELECT CAST(value AS INTEGER) FROM json_each(?))
		ORDER BY action_sequence
		LIMIT 1
	`);
	statement.safeIntegers(true);
	const row = statement.get(
		stream.sessionId,
		stream.actorId,
		...(stream.runId === undefined ? [] : [stream.runId]),
		JSON.stringify(replayable.map(String)),
	) as ActionRow | undefined;
	return row ? rowToRecord(row, limits) : undefined;
}

function claimPendingRecord(
	database: Database.Database,
	request: ActionClaimRequest,
	replayable: readonly bigint[],
	limits?: Readonly<ActionGoalProjectionLimits>,
): ActionRecord | undefined {
	const runFilter = request.runId === undefined ? "" : " AND run_id = ?";
	const statement = database.prepare(`
		SELECT action_sequence, run_id, status, attempt, claim_expires_at,
			has_goal_projection, goal_projection_outcome, goal_projection_settled_at, record_json,
			length(CAST(record_json AS BLOB)) AS record_bytes
		FROM action_records
		WHERE session_id = ? AND actor_id = ?${runFilter}
			AND (status = 'pending' OR action_sequence IN (SELECT CAST(value AS INTEGER) FROM json_each(?)))
		ORDER BY action_sequence
		LIMIT ?
	`);
	statement.safeIntegers(true);
	const rows = statement.all(
		request.sessionId,
		request.actorId,
		...(request.runId === undefined ? [] : [request.runId]),
		JSON.stringify(replayable.map(String)),
		BigInt(request.candidateLimit),
	) as ActionRow[];
	for (const row of rows) {
		const record = rowToRecord(row, limits);
		if (!isRunOpen(database, record)) continue;
		if (!canAcquire(database, record)) continue;
		if (BigInt(record.attempt) >= SQLITE_INTEGER_MAX) throw new SqliteActionAttemptExhaustedError();
		const replay = record.status === "claimed";
		const transition = replay ? reclaimActionRecord(record, request) : claimActionRecord(record, request);
		if (replay) release(database, record);
		acquire(database, transition.record);
		updateRecord(database, row.action_sequence, transition.record, limits);
		return transition.record;
	}
	return undefined;
}

function expireDueClaims(
	database: Database.Database,
	request: ActionClaimRequest,
	limits?: Readonly<ActionGoalProjectionLimits>,
): { readonly replayable: readonly bigint[]; readonly changed: boolean } {
	const runFilter = request.runId === undefined ? "" : " AND run_id = ?";
	const statement = database.prepare(`
		SELECT action_sequence, run_id, status, attempt, claim_expires_at,
			has_goal_projection, goal_projection_outcome, goal_projection_settled_at, record_json,
			length(CAST(record_json AS BLOB)) AS record_bytes
		FROM action_records
		WHERE session_id = ? AND actor_id = ?${runFilter} AND status = 'claimed' AND claim_expires_at <= ?
		ORDER BY claim_expires_at, action_sequence
		LIMIT ?
	`);
	statement.safeIntegers(true);
	const rows = statement.all(
		request.sessionId,
		request.actorId,
		...(request.runId === undefined ? [] : [request.runId]),
		request.claimedAt,
		BigInt(request.candidateLimit),
	) as ActionRow[];
	const replayable: bigint[] = [];
	let changed = false;
	for (const row of rows) {
		const record = rowToRecord(row, limits);
		if (record.replayPolicy === "safe" && isRunOpen(database, record)) {
			replayable.push(row.action_sequence);
			continue;
		}
		const transition = expireActionRecord(record, request.claimedAt);
		if (transition.releaseConflicts) release(database, transition.record);
		updateRecord(database, row.action_sequence, transition.record, limits);
		changed = true;
	}
	return { replayable, changed };
}

function isRunOpen(database: Database.Database, record: ActionRecord): boolean {
	return isActionRunAuthorityOpen(() => assertSqliteRunAuthorityOpen(database, record.coordinate));
}

function retireClosedPending(
	database: Database.Database,
	request: ActionClaimRequest,
	limits?: Readonly<ActionGoalProjectionLimits>,
): boolean {
	const statement = database.prepare(`
		SELECT action_sequence, run_id, status, attempt, claim_expires_at,
			has_goal_projection, goal_projection_outcome, goal_projection_settled_at, record_json,
			length(CAST(record_json AS BLOB)) AS record_bytes
		FROM action_records AS action
		WHERE session_id = ? AND actor_id = ? AND status = 'pending'
			AND EXISTS (
				SELECT 1 FROM oga_run_authority_admissions AS admission
				JOIN oga_run_authority_closures AS closure
					ON closure.session_id = admission.session_id AND closure.actor_id = admission.actor_id
					AND closure.scope = admission.scope AND closure.generation = admission.generation
				WHERE admission.session_id = action.session_id AND admission.actor_id = action.actor_id
					AND admission.run_id = action.run_id
			)
		ORDER BY action_sequence LIMIT ?
	`);
	statement.safeIntegers(true);
	const rows = statement.all(request.sessionId, request.actorId, BigInt(request.candidateLimit)) as ActionRow[];
	for (const row of rows) {
		const record = rowToRecord(row, limits);
		if (isRunOpen(database, record)) throw new TypeError("The closed Action run authority index is inconsistent.");
		const transition = retirePendingActionRecord(record, request.claimedAt);
		release(database, record);
		updateRecord(database, row.action_sequence, transition.record, limits);
	}
	return rows.length !== 0;
}

function selectRecord(
	database: Database.Database,
	stream: StreamKey,
	operationId: string,
	limits?: Readonly<ActionGoalProjectionLimits>,
): ActionRecord | undefined {
	const row = selectRow(database, stream, operationId);
	return row ? rowToRecord(row, limits) : undefined;
}

function selectRow(database: Database.Database, stream: StreamKey, operationId: string): ActionRow | undefined {
	const statement = database.prepare(`
		SELECT action_sequence, run_id, status, attempt, claim_expires_at,
			has_goal_projection, goal_projection_outcome, goal_projection_settled_at, record_json,
			length(CAST(record_json AS BLOB)) AS record_bytes
		FROM action_records
		WHERE session_id = ? AND actor_id = ? AND operation_id = ?
	`);
	statement.safeIntegers(true);
	return statement.get(stream.sessionId, stream.actorId, operationId) as ActionRow | undefined;
}

function requireRow(database: Database.Database, stream: StreamKey, operationId: string): ActionRow {
	validateStream(stream);
	assertOpaqueIdentifier(operationId);
	const row = selectRow(database, stream, operationId);
	if (!row) throw new ActionNotFoundError();
	return row;
}

function rowToRecord(row: ActionRow, limits?: Readonly<ActionGoalProjectionLimits>): ActionRecord {
	assertSqliteActionProjectionHydrationFits(row, limits);
	const parsed = JSON.parse(row.record_json) as unknown;
	assertActionRecord(parsed, limits);
	if (
		parsed.coordinate.runId !== row.run_id ||
		parsed.status !== row.status ||
		parsed.attempt !== row.attempt.toString() ||
		(parsed.claim?.expiresAt ?? null) !== row.claim_expires_at
	) {
		throw new TypeError("The durable action index does not match its canonical record.");
	}
	assertSqliteActionProjectionIndexes(parsed, row);
	return parsed;
}

function canAcquire(database: Database.Database, record: ActionRecord): boolean {
	const query = database.prepare("SELECT operation_id FROM action_conflicts WHERE session_id = ? AND conflict_key = ?");
	return record.conflictKeys.every((key) => {
		const row = query.get(record.sessionId, key) as { operation_id: string } | undefined;
		return row === undefined || row.operation_id === record.operationId;
	});
}

function acquire(database: Database.Database, record: ActionRecord): void {
	const insert = database.prepare(
		"INSERT INTO action_conflicts(session_id, conflict_key, actor_id, operation_id) VALUES (?, ?, ?, ?)",
	);
	for (const key of record.conflictKeys) insert.run(record.sessionId, key, record.actorId, record.operationId);
}

function release(database: Database.Database, record: ActionRecord): void {
	const remove = database.prepare(
		"DELETE FROM action_conflicts WHERE session_id = ? AND conflict_key = ? AND operation_id = ?",
	);
	for (const key of record.conflictKeys) remove.run(record.sessionId, key, record.operationId);
}

function updateRecord(
	database: Database.Database,
	actionSequence: bigint,
	record: ActionRecord,
	limits?: Readonly<ActionGoalProjectionLimits>,
): void {
	assertActionRecord(record, limits);
	assertActionGoalProjectionRecordFits(record, limits);
	const result = database
		.prepare(`
			UPDATE action_records
			SET status = ?, attempt = ?, claim_expires_at = ?,
				has_goal_projection = ?, goal_projection_outcome = ?,
				goal_projection_settled_at = ?, record_json = ?
			WHERE action_sequence = ?
		`)
		.run(
			record.status,
			BigInt(record.attempt),
			record.claim?.expiresAt ?? null,
			record.goalProjection === undefined ? 0n : 1n,
			record.goalProjectionSettlement?.outcome ?? null,
			record.goalProjectionSettlement?.settledAt ?? null,
			JSON.stringify(record),
			actionSequence,
		);
	if (result.changes !== 1) throw new ActionNotFoundError();
}

function validateStream(stream: StreamKey): void {
	assertOpaqueIdentifier(stream.sessionId);
	assertOpaqueIdentifier(stream.actorId);
}

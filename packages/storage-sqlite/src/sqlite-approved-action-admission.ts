import {
	ActionApprovalNotFoundError,
	type ActionApprovalRecord,
	type ActionGoalProjectionLimits,
	ActionIntentConflictError,
	type ActionRecord,
	type ApprovedActionAdmission,
	type ApprovedActionAdmissionInput,
	type ApprovedActionAdmissionResult,
	assertActionApprovalRecord,
	assertActionGoalProjectionRecordFits,
	assertActionRecord,
	captureActionGoalProjectionLimits,
	consumeActionApprovalRecord,
	type PreparedActionIntent,
} from "@opengameagent/actions";
import { canonicalizeJsonValue } from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import { SqliteActionJournal } from "./sqlite-action-journal.js";
import {
	assertSqliteActionGoalProjectionCapacity,
	assertSqliteActionProjectionHydrationFits,
	assertSqliteActionProjectionIndexes,
} from "./sqlite-action-projection.js";
import { SqliteActionApprovalStore, SqliteApprovalRevisionExhaustedError } from "./sqlite-approval-store.js";
import type { SqliteAuthority } from "./sqlite-authority.js";
import { assertSqliteRunAuthorityOpen } from "./sqlite-run-authority.js";

const SQLITE_INTEGER_MAX = 9_223_372_036_854_775_807n;

interface ApprovalRow {
	approval_sequence: bigint;
	run_id: string | null;
	request_fingerprint: string;
	status: string;
	revision: bigint;
	expires_at: string;
	record_json: string;
}

interface ActionRow {
	run_id: string | null;
	intent_fingerprint: string;
	status: string;
	attempt: bigint;
	claim_expires_at: string | null;
	has_goal_projection: bigint;
	goal_projection_outcome: string | null;
	goal_projection_settled_at: string | null;
	record_json: string;
	record_bytes: bigint;
}

export interface SqliteApprovedActionAdmissionOptions {
	now?: () => Date;
	goalProjection?: ActionGoalProjectionLimits;
}

export class SqliteApprovedActionAdmissionError extends Error {
	constructor() {
		super("The consumed approval and its admitted action are not atomically consistent.");
		this.name = "SqliteApprovedActionAdmissionError";
	}
}

export class SqliteApprovedActionAdmission implements ApprovedActionAdmission {
	private readonly now: () => Date;
	private readonly goalProjectionLimits?: Readonly<ActionGoalProjectionLimits>;

	constructor(
		private readonly authority: SqliteAuthority,
		options: SqliteApprovedActionAdmissionOptions = {},
	) {
		SqliteActionJournal.install(authority);
		SqliteActionApprovalStore.install(authority);
		this.now = options.now ?? (() => new Date());
		const goalProjectionLimits = captureActionGoalProjectionLimits(options.goalProjection);
		if (goalProjectionLimits !== undefined) this.goalProjectionLimits = goalProjectionLimits;
	}

	async admit(input: ApprovedActionAdmissionInput): Promise<ApprovedActionAdmissionResult> {
		assertAdmissionIntent(input);
		const consumedAt = this.now().toISOString();
		return this.authority.write((database) => {
			const row = selectApprovalRow(database, input.sessionId, input.actorId, input.approvalId);
			if (!row) throw new ActionApprovalNotFoundError();
			const current = approvalRowToRecord(row);
			const existingAction = selectActionRecord(database, input.intent, this.goalProjectionLimits);

			if (current.status === "consumed") {
				assertIdempotentAdmission(current, input, existingAction);
				return { approval: structuredClone(current), action: structuredClone(existingAction) };
			}
			assertSqliteRunAuthorityOpen(database, input.intent.coordinate);

			if (row.revision >= SQLITE_INTEGER_MAX) throw new SqliteApprovalRevisionExhaustedError();
			const consumed = consumeActionApprovalRecord(current, { ...input, consumedAt });
			if (consumed.status !== "consumed") {
				updateApprovalRecord(database, row.approval_sequence, consumed);
				return { approval: structuredClone(consumed) };
			}

			const action = ensureActionRecord(database, input.intent, consumedAt, existingAction, this.goalProjectionLimits);
			updateApprovalRecord(database, row.approval_sequence, consumed);
			return { approval: structuredClone(consumed), action: structuredClone(action) };
		});
	}
}

function assertAdmissionIntent(input: ApprovedActionAdmissionInput): void {
	if (
		input.intent.sessionId !== input.sessionId ||
		input.intent.actorId !== input.actorId ||
		input.intent.operationId !== input.operationId ||
		input.intent.intentFingerprint !== input.intentFingerprint ||
		canonicalizeJsonValue(input.intent.coordinate) !== canonicalizeJsonValue(input.coordinate)
	) {
		throw new SqliteApprovedActionAdmissionError();
	}
}

function selectApprovalRow(
	database: Database.Database,
	sessionId: string,
	actorId: string,
	approvalId: string,
): ApprovalRow | undefined {
	const statement = database.prepare(`
		SELECT approval_sequence, run_id, request_fingerprint, status, revision, expires_at, record_json
		FROM action_approvals
		WHERE session_id = ? AND actor_id = ? AND approval_id = ?
	`);
	statement.safeIntegers(true);
	return statement.get(sessionId, actorId, approvalId) as ApprovalRow | undefined;
}

function approvalRowToRecord(row: ApprovalRow): ActionApprovalRecord {
	const parsed = JSON.parse(row.record_json) as unknown;
	assertActionApprovalRecord(parsed);
	if (
		parsed.request.coordinate.runId !== row.run_id ||
		parsed.request.requestFingerprint !== row.request_fingerprint ||
		parsed.status !== row.status ||
		parsed.revision !== row.revision.toString() ||
		parsed.request.expiresAt !== row.expires_at
	) {
		throw new TypeError("The durable approval index does not match its canonical record.");
	}
	return parsed;
}

function updateApprovalRecord(database: Database.Database, sequence: bigint, record: ActionApprovalRecord): void {
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

function selectActionRecord(
	database: Database.Database,
	intent: PreparedActionIntent,
	limits?: Readonly<ActionGoalProjectionLimits>,
): ActionRecord | undefined {
	const statement = database.prepare(`
		SELECT run_id, intent_fingerprint, status, attempt, claim_expires_at,
			has_goal_projection, goal_projection_outcome, goal_projection_settled_at, record_json,
			length(CAST(record_json AS BLOB)) AS record_bytes
		FROM action_records
		WHERE session_id = ? AND actor_id = ? AND operation_id = ?
	`);
	statement.safeIntegers(true);
	const row = statement.get(intent.sessionId, intent.actorId, intent.operationId) as ActionRow | undefined;
	if (!row) return undefined;
	assertSqliteActionProjectionHydrationFits(row, limits);
	const parsed = JSON.parse(row.record_json) as unknown;
	assertActionRecord(parsed, limits);
	if (
		parsed.coordinate.runId !== row.run_id ||
		parsed.intentFingerprint !== row.intent_fingerprint ||
		parsed.status !== row.status ||
		parsed.attempt !== row.attempt.toString() ||
		(parsed.claim?.expiresAt ?? null) !== row.claim_expires_at
	) {
		throw new TypeError("The durable action index does not match its canonical record.");
	}
	assertSqliteActionProjectionIndexes(parsed, row);
	return parsed;
}

function ensureActionRecord(
	database: Database.Database,
	intent: PreparedActionIntent,
	now: string,
	existing: ActionRecord | undefined,
	limits?: Readonly<ActionGoalProjectionLimits>,
): ActionRecord {
	if (existing) {
		if (existing.intentFingerprint !== intent.intentFingerprint) throw new ActionIntentConflictError();
		return existing;
	}
	if (intent.goalProjection !== undefined) {
		if (limits === undefined) throw new TypeError("Goal projection limits are required for approved Action admission.");
		assertSqliteActionGoalProjectionCapacity(database, intent, limits);
	}
	const record: ActionRecord = {
		...structuredClone(intent),
		status: "pending",
		attempt: "0",
		createdAt: now,
		updatedAt: now,
	};
	assertActionRecord(record, limits);
	assertActionGoalProjectionRecordFits(record, limits);
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
	return record;
}

function assertIdempotentAdmission(
	approval: ActionApprovalRecord,
	input: ApprovedActionAdmissionInput,
	action: ActionRecord | undefined,
): asserts action is ActionRecord {
	if (
		BigInt(approval.revision) !== BigInt(input.expectedRevision) + 1n ||
		approval.request.sessionId !== input.sessionId ||
		approval.request.actorId !== input.actorId ||
		canonicalizeJsonValue(approval.request.coordinate) !== canonicalizeJsonValue(input.coordinate) ||
		approval.request.approvalId !== input.approvalId ||
		approval.request.operationId !== input.operationId ||
		approval.request.intentFingerprint !== input.intentFingerprint ||
		approval.request.operationId !== input.intent.operationId ||
		approval.request.intentFingerprint !== input.intent.intentFingerprint ||
		canonicalizeJsonValue(approval.request.expectedAuthority) !== canonicalizeJsonValue(input.currentAuthority) ||
		!action ||
		action.intentFingerprint !== input.intent.intentFingerprint ||
		canonicalizeJsonValue(action.coordinate) !== canonicalizeJsonValue(input.coordinate)
	) {
		throw new SqliteApprovedActionAdmissionError();
	}
	if (approval.decision?.decision !== "approved" || !approval.consumedAt) {
		throw new SqliteApprovedActionAdmissionError();
	}
}

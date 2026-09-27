import {
	ActionGoalProjectionCapacityError,
	type ActionGoalProjectionLimits,
	type ActionRecord,
} from "@opengameagent/actions";
import type { StreamKey } from "@opengameagent/protocol";
import type Database from "better-sqlite3";

export interface SqliteActionProjectionIndexes {
	readonly has_goal_projection: bigint;
	readonly goal_projection_outcome: string | null;
	readonly goal_projection_settled_at: string | null;
	readonly record_bytes: bigint;
}

export function assertSqliteActionProjectionHydrationFits(
	row: Pick<SqliteActionProjectionIndexes, "has_goal_projection" | "record_bytes">,
	limits?: Readonly<ActionGoalProjectionLimits>,
): void {
	if (row.has_goal_projection === 0n) return;
	if (limits === undefined) throw new TypeError("Goal projection limits are required for Action hydration.");
	if (row.record_bytes > BigInt(limits.maxRecordBytes)) {
		throw new RangeError("A Goal projection Action exceeds its configured hydration byte budget.");
	}
}

export function assertSqliteActionProjectionIndexes(record: ActionRecord, row: SqliteActionProjectionIndexes): void {
	if (
		(record.goalProjection === undefined ? 0n : 1n) !== row.has_goal_projection ||
		(record.goalProjectionSettlement?.outcome ?? null) !== row.goal_projection_outcome ||
		(record.goalProjectionSettlement?.settledAt ?? null) !== row.goal_projection_settled_at
	) {
		throw new TypeError("The durable action projection index does not match its canonical record.");
	}
}

export function assertSqliteActionGoalProjectionCapacity(
	database: Database.Database,
	stream: StreamKey,
	limits: Readonly<ActionGoalProjectionLimits>,
): void {
	const statement = database.prepare(`
		SELECT count(*) AS pending_count
		FROM action_records
		WHERE session_id = ? AND actor_id = ?
			AND has_goal_projection = 1
			AND goal_projection_settled_at IS NULL
	`);
	statement.safeIntegers(true);
	const row = statement.get(stream.sessionId, stream.actorId) as { pending_count: bigint };
	if (row.pending_count >= BigInt(limits.maxPendingPerStream)) {
		throw new ActionGoalProjectionCapacityError();
	}
}

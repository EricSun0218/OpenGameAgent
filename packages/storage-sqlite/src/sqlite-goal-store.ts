import { Buffer } from "node:buffer";
import {
	captureGoalLimits,
	compareGoalTicks,
	GoalAuthorityRetiredError,
	GoalAuthorityRetirementConflictError,
	type GoalAuthorityRetirementDraft,
	type GoalAuthorityRetirementRecord,
	GoalCapacityError,
	GoalConflictError,
	type GoalDraft,
	type GoalKey,
	type GoalLimits,
	type GoalListRequest,
	GoalNotFoundError,
	type GoalPage,
	type GoalPartition,
	type GoalRecord,
	type GoalReplacement,
	GoalRevisionConflictError,
	type GoalStore,
	GoalValidationError,
	normalizeGoalAuthorityRetirement,
	normalizeGoalDraft,
	normalizeGoalKey,
	normalizeGoalPartition,
	normalizeGoalPlan,
	normalizeGoalReplacement,
	normalizeGoalStatusFilter,
	normalizeGoalTime,
	serializeGoalPlan,
	serializeGoalRecord,
	validateGoalAfterKey,
	validateGoalHostAdvanceFingerprint,
	validateGoalKey,
	validateGoalPageLimit,
	validateGoalRevision,
	validateGoalStatus,
	validateGoalTimestamp,
	validateObjective,
} from "@opengameagent/extensions";
import { assertOpaqueIdentifier } from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const GOAL_MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "goal-store-v1",
		sql: `
			CREATE TABLE oga_goals (
				goal_key INTEGER PRIMARY KEY AUTOINCREMENT,
				goal_id TEXT NOT NULL,
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				session_id TEXT,
				actor_id TEXT,
				objective TEXT NOT NULL,
				plan_json TEXT NOT NULL CHECK(json_valid(plan_json)),
				status TEXT NOT NULL CHECK(status IN ('active', 'paused', 'achieved', 'failed', 'cancelled')),
				revision TEXT NOT NULL,
				created_tick TEXT NOT NULL,
				updated_tick TEXT NOT NULL,
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				UNIQUE(owner_id, timeline_id, generation, goal_id)
			) STRICT;

			CREATE INDEX oga_goals_owner_generation_status
			ON oga_goals(owner_id, timeline_id, generation, status, goal_key);

			CREATE INDEX oga_goals_terminal_retention
			ON oga_goals(owner_id, timeline_id, generation, updated_at, goal_key)
			WHERE status IN ('achieved', 'failed', 'cancelled');
		`,
	},
	{
		version: 2,
		name: "goal-host-advance-fingerprint-v2",
		sql: `
			ALTER TABLE oga_goals
			ADD COLUMN last_host_advance_fingerprint TEXT
			CHECK(
				last_host_advance_fingerprint IS NULL OR (
					length(last_host_advance_fingerprint) = 64
					AND last_host_advance_fingerprint NOT GLOB '*[^0-9a-f]*'
				)
			);
		`,
	},
	{
		version: 3,
		name: "goal-authority-retirement-v3",
		sql: `
			CREATE TABLE oga_goal_authority_retirements (
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				retirement_id TEXT NOT NULL,
				state_revision TEXT NOT NULL,
				retired_tick TEXT NOT NULL,
				retired_at TEXT NOT NULL,
				cancelled_goal_count INTEGER NOT NULL CHECK(cancelled_goal_count >= 0),
				PRIMARY KEY(owner_id, timeline_id, generation, session_id, actor_id)
			) STRICT;
		`,
	},
];

interface GoalRow {
	goal_key: bigint;
	goal_id: string;
	owner_id: string;
	timeline_id: string;
	generation: string;
	session_id: string | null;
	actor_id: string | null;
	objective: string;
	plan_json: string;
	status: string;
	revision: string;
	created_tick: string;
	updated_tick: string;
	created_at: string;
	updated_at: string;
	last_host_advance_fingerprint: string | null;
}

interface GoalAuthorityRetirementRow {
	owner_id: string;
	timeline_id: string;
	generation: string;
	session_id: string;
	actor_id: string;
	retirement_id: string;
	state_revision: string;
	retired_tick: string;
	retired_at: string;
	cancelled_goal_count: number;
}

export class SqliteGoalStore implements GoalStore {
	readonly limits: GoalLimits;

	constructor(
		private readonly authority: SqliteAuthority,
		limits: GoalLimits,
	) {
		this.limits = captureGoalLimits(limits);
		this.authority.install("goals", GOAL_MIGRATIONS);
	}

	async create(input: GoalDraft, maxOpenGoals: number, signal?: AbortSignal): Promise<GoalRecord> {
		const draft = normalizeGoalDraft(input, this.limits);
		const planJson = serializeGoalPlan(draft.plan, this.limits);
		validateGoalPageLimit(maxOpenGoals, this.limits.maxOpenGoalsPerOwnerGeneration, "invalid_goal_capacity");
		signal?.throwIfAborted();
		return this.authority.write((database) => {
			if (findGoalAuthorityRetirementRow(database, draft.partition) !== undefined) {
				throw new GoalAuthorityRetiredError();
			}
			const existing = findGoalRow(database, draft);
			if (existing !== undefined) throw new GoalConflictError();
			const openCount = database
				.prepare(`
					SELECT COUNT(*)
					FROM oga_goals
					WHERE owner_id = ? AND timeline_id = ? AND generation = ?
					  AND status IN ('active', 'paused')
				`)
				.pluck()
				.get(draft.partition.ownerId, draft.partition.timelineId, draft.partition.generation) as number;
			if (openCount >= maxOpenGoals) throw new GoalCapacityError();
			let result: Database.RunResult;
			try {
				result = database
					.prepare(`
						INSERT INTO oga_goals(
							goal_id, owner_id, timeline_id, generation, session_id, actor_id,
							objective, plan_json, status, revision, created_tick, updated_tick,
							created_at, updated_at
						) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'active', '1', ?, ?, ?, ?)
					`)
					.run(
						draft.goalId,
						draft.partition.ownerId,
						draft.partition.timelineId,
						draft.partition.generation,
						draft.partition.sessionId ?? null,
						draft.partition.actorId ?? null,
						draft.objective,
						planJson,
						draft.gameTime.tick,
						draft.gameTime.tick,
						draft.recordedAt,
						draft.recordedAt,
					);
			} catch (error) {
				if (isConstraintError(error)) throw new GoalConflictError();
				throw error;
			}
			const row = findGoalRowByKey(database, BigInt(result.lastInsertRowid));
			if (row === undefined) throw new Error("Goal insert did not produce a readable row.");
			return this.hydrate(row);
		});
	}

	async retireAuthority(
		input: GoalAuthorityRetirementDraft,
		signal?: AbortSignal,
	): Promise<GoalAuthorityRetirementRecord> {
		const draft = normalizeGoalAuthorityRetirement(input);
		signal?.throwIfAborted();
		return this.authority.write((database) => this.retireAuthorityInTransaction(database, draft));
	}

	retireAuthorityInTransaction(
		database: Database.Database,
		input: GoalAuthorityRetirementDraft,
		signal?: AbortSignal,
	): GoalAuthorityRetirementRecord {
		if (!database.inTransaction || this.authority.read((owned) => owned !== database)) {
			throw new TypeError("Goal retirement must use the same active SQLite authority transaction.");
		}
		const draft = normalizeGoalAuthorityRetirement(input);
		const existing = findGoalAuthorityRetirementRow(database, draft.partition);
		if (existing !== undefined) {
			const retirement = hydrateGoalAuthorityRetirement(existing);
			if (!sameGoalAuthorityRetirement(retirement, draft)) {
				throw new GoalAuthorityRetirementConflictError();
			}
			return retirement;
		}
		const select = database.prepare(`
				SELECT * FROM oga_goals
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND session_id IS ? AND actor_id IS ?
				  AND status IN ('active', 'paused')
				ORDER BY goal_key LIMIT ?
			`);
		select.safeIntegers(true);
		const rows = select.all(
			draft.partition.ownerId,
			draft.partition.timelineId,
			draft.partition.generation,
			draft.partition.sessionId ?? null,
			draft.partition.actorId ?? null,
			BigInt(this.limits.maxOpenGoalsPerOwnerGeneration) + 1n,
		) as GoalRow[];
		if (rows.length > this.limits.maxOpenGoalsPerOwnerGeneration) throw new GoalCapacityError();
		const updates = rows.map((row) => {
			const current = this.hydrate(row);
			if (compareGoalTicks(draft.gameTime.tick, current.gameTime.tick) < 0) {
				throw new GoalValidationError("goal_time_regression");
			}
			const updated: GoalRecord = {
				...current,
				status: "cancelled",
				gameTime: { ...draft.gameTime },
				updatedAt: draft.recordedAt,
				revision: (BigInt(current.revision) + 1n).toString(),
				lastHostAdvanceFingerprint: null,
			};
			serializeGoalRecord(updated, this.limits);
			return updated;
		});
		const retirement: GoalAuthorityRetirementRecord = {
			...draft,
			cancelledGoalCount: updates.length,
		};
		signal?.throwIfAborted();
		database
			.prepare(`
					INSERT INTO oga_goal_authority_retirements(
						owner_id, timeline_id, generation, session_id, actor_id,
						retirement_id, state_revision, retired_tick, retired_at, cancelled_goal_count
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
				`)
			.run(
				draft.partition.ownerId,
				draft.partition.timelineId,
				draft.partition.generation,
				draft.partition.sessionId ?? "",
				draft.partition.actorId ?? "",
				draft.retirementId,
				draft.stateRevision,
				draft.gameTime.tick,
				draft.recordedAt,
				updates.length,
			);
		const update = database.prepare(`
				UPDATE oga_goals
				SET status = 'cancelled', revision = ?, updated_tick = ?, updated_at = ?,
					last_host_advance_fingerprint = NULL
				WHERE goal_key = ? AND revision = ? AND status IN ('active', 'paused')
			`);
		for (const [index, row] of rows.entries()) {
			const changed = update.run(
				updates[index]?.revision,
				draft.gameTime.tick,
				draft.recordedAt,
				row.goal_key,
				row.revision,
			);
			if (changed.changes !== 1) throw new GoalRevisionConflictError();
		}
		return retirement;
	}

	async get(key: GoalKey): Promise<GoalRecord | undefined> {
		const normalized = normalizeGoalKey(key);
		return this.authority.read((database) => {
			const row = findGoalRow(database, normalized);
			return row === undefined ? undefined : this.hydrate(row);
		});
	}

	async replace(key: GoalKey, expectedRevision: string, input: GoalReplacement): Promise<GoalRecord> {
		const normalizedKey = normalizeGoalKey(key);
		validateGoalRevision(expectedRevision);
		const replacement = normalizeGoalReplacement(input, normalizedKey, this.limits);
		const planJson = serializeGoalPlan(replacement.plan, this.limits);
		return this.authority.write((database) => {
			if (findGoalAuthorityRetirementRow(database, normalizedKey) !== undefined) {
				throw new GoalAuthorityRetiredError();
			}
			const current = findGoalRow(database, normalizedKey);
			if (current === undefined) throw new GoalNotFoundError();
			if (current.revision !== expectedRevision) throw new GoalRevisionConflictError();
			const nextRevision = (BigInt(current.revision) + 1n).toString();
			const result = database
				.prepare(`
					UPDATE oga_goals
					SET plan_json = ?, status = ?, revision = ?, updated_tick = ?, updated_at = ?,
						last_host_advance_fingerprint = ?
					WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND goal_id = ? AND revision = ?
				`)
				.run(
					planJson,
					replacement.status,
					nextRevision,
					replacement.gameTime.tick,
					replacement.recordedAt,
					replacement.lastHostAdvanceFingerprint,
					normalizedKey.ownerId,
					normalizedKey.timelineId,
					normalizedKey.generation,
					normalizedKey.goalId,
					expectedRevision,
				);
			if (result.changes !== 1) throw new GoalRevisionConflictError();
			const updated = findGoalRow(database, normalizedKey);
			if (updated === undefined) throw new GoalNotFoundError();
			return this.hydrate(updated);
		});
	}

	async list(request: GoalListRequest): Promise<GoalPage> {
		const statusesInput = request.statuses;
		const afterGoalKey = request.afterGoalKey;
		const limit = request.limit;
		const partition = normalizeGoalPartition(request);
		const statuses = normalizeGoalStatusFilter(statusesInput);
		validateGoalAfterKey(afterGoalKey);
		validateGoalPageLimit(limit, this.limits.maxListPageSize, "invalid_goal_page_limit");
		return this.authority.read((database) => {
			const selectedStatuses = statuses ?? [];
			const statusSql =
				selectedStatuses.length === 0 ? "" : `AND status IN (${selectedStatuses.map(() => "?").join(", ")})`;
			const statement = database.prepare(`
				SELECT * FROM oga_goals
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND (? IS NULL OR session_id = ?)
				  AND (? IS NULL OR actor_id = ?)
				  AND goal_key > ?
				  ${statusSql}
				ORDER BY goal_key
				LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				partition.sessionId ?? null,
				partition.sessionId ?? null,
				partition.actorId ?? null,
				partition.actorId ?? null,
				afterGoalKey ?? 0n,
				...selectedStatuses,
				BigInt(limit + 1),
			) as GoalRow[];
			const hasMore = rows.length > limit;
			const records = rows.slice(0, limit).map((row) => this.hydrate(row));
			const next = hasMore ? records.at(-1)?.goalKey : undefined;
			return {
				records,
				...(next === undefined ? {} : { nextAfterGoalKey: next }),
			};
		});
	}

	async purgeTerminal(
		input: Pick<GoalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const partition = normalizeGoalPartition(input);
		validateGoalTimestamp(beforeRecordedAt);
		validateGoalPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_goal_purge_limit");
		return this.authority.write((database) => {
			const select = database.prepare(`
				SELECT goal_key
				FROM oga_goals
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?
				  AND status IN ('achieved', 'failed', 'cancelled') AND updated_at < ?
				ORDER BY goal_key
				LIMIT ?
			`);
			select.safeIntegers(true);
			const rows = select.all(
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				beforeRecordedAt,
				BigInt(limit),
			) as { goal_key: bigint }[];
			const remove = database.prepare("DELETE FROM oga_goals WHERE goal_key = ?");
			for (const row of rows) remove.run(row.goal_key);
			return rows.map((row) => row.goal_key);
		});
	}

	private hydrate(row: GoalRow): GoalRecord {
		const partition = normalizeGoalPartition({
			ownerId: row.owner_id,
			timelineId: row.timeline_id,
			generation: row.generation,
			...(row.session_id === null ? {} : { sessionId: row.session_id }),
			...(row.actor_id === null ? {} : { actorId: row.actor_id }),
		});
		validateGoalKey({ ...partition, goalId: row.goal_id });
		validateObjective(row.objective, this.limits);
		if (Buffer.byteLength(row.plan_json, "utf8") > this.limits.maxPlanBytes) {
			throw new GoalValidationError("goal_plan_too_large");
		}
		let decodedPlan: unknown;
		try {
			decodedPlan = JSON.parse(row.plan_json);
		} catch {
			throw new TypeError("Persisted goal plan is invalid.");
		}
		const plan = normalizeGoalPlan(decodedPlan, this.limits);
		validateGoalStatus(row.status);
		validateGoalRevision(row.revision);
		const createdGameTime = normalizeGoalTime(
			{ timelineId: partition.timelineId, generation: partition.generation, tick: row.created_tick },
			partition,
		);
		const gameTime = normalizeGoalTime(
			{ timelineId: partition.timelineId, generation: partition.generation, tick: row.updated_tick },
			partition,
		);
		validateGoalTimestamp(row.created_at);
		validateGoalTimestamp(row.updated_at);
		validateGoalHostAdvanceFingerprint(row.last_host_advance_fingerprint);
		const record: GoalRecord = {
			goalKey: row.goal_key,
			goalId: row.goal_id,
			partition,
			objective: row.objective,
			plan,
			status: row.status,
			revision: row.revision,
			createdGameTime,
			gameTime,
			createdAt: row.created_at,
			updatedAt: row.updated_at,
			lastHostAdvanceFingerprint: row.last_host_advance_fingerprint,
		};
		serializeGoalRecord(record, this.limits);
		return record;
	}
}

function findGoalRow(database: Database.Database, key: GoalKey | GoalDraft): GoalRow | undefined {
	const partition = "partition" in key ? key.partition : key;
	const statement = database.prepare(`
		SELECT * FROM oga_goals
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND goal_id = ?
		  AND session_id IS ? AND actor_id IS ?
		LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(
		partition.ownerId,
		partition.timelineId,
		partition.generation,
		key.goalId,
		partition.sessionId ?? null,
		partition.actorId ?? null,
	) as GoalRow | undefined;
}

function findGoalRowByKey(database: Database.Database, goalKey: bigint): GoalRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_goals WHERE goal_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(goalKey) as GoalRow | undefined;
}

function findGoalAuthorityRetirementRow(
	database: Database.Database,
	partition: GoalPartition,
): GoalAuthorityRetirementRow | undefined {
	return database
		.prepare(`
			SELECT * FROM oga_goal_authority_retirements
			WHERE owner_id = ? AND timeline_id = ? AND generation = ?
			  AND session_id = ? AND actor_id = ?
			LIMIT 1
		`)
		.get(
			partition.ownerId,
			partition.timelineId,
			partition.generation,
			partition.sessionId ?? "",
			partition.actorId ?? "",
		) as GoalAuthorityRetirementRow | undefined;
}

function hydrateGoalAuthorityRetirement(row: GoalAuthorityRetirementRow): GoalAuthorityRetirementRecord {
	const partition = normalizeGoalPartition({
		ownerId: row.owner_id,
		timelineId: row.timeline_id,
		generation: row.generation,
		...(row.session_id === "" ? {} : { sessionId: row.session_id }),
		...(row.actor_id === "" ? {} : { actorId: row.actor_id }),
	});
	assertOpaqueIdentifier(row.retirement_id);
	validateGoalRevision(row.state_revision);
	const gameTime = normalizeGoalTime(
		{ timelineId: partition.timelineId, generation: partition.generation, tick: row.retired_tick },
		partition,
	);
	validateGoalTimestamp(row.retired_at);
	if (!Number.isSafeInteger(row.cancelled_goal_count) || row.cancelled_goal_count < 0) {
		throw new GoalValidationError("invalid_goal_retirement_count");
	}
	return {
		retirementId: row.retirement_id,
		stateRevision: row.state_revision,
		partition,
		gameTime,
		recordedAt: row.retired_at,
		cancelledGoalCount: row.cancelled_goal_count,
	};
}

function sameGoalAuthorityRetirement(
	left: GoalAuthorityRetirementRecord,
	right: GoalAuthorityRetirementDraft,
): boolean {
	return (
		left.retirementId === right.retirementId &&
		left.stateRevision === right.stateRevision &&
		left.partition.ownerId === right.partition.ownerId &&
		left.partition.timelineId === right.partition.timelineId &&
		left.partition.generation === right.partition.generation &&
		left.partition.sessionId === right.partition.sessionId &&
		left.partition.actorId === right.partition.actorId &&
		left.gameTime.timelineId === right.gameTime.timelineId &&
		left.gameTime.generation === right.gameTime.generation &&
		left.gameTime.tick === right.gameTime.tick &&
		left.recordedAt === right.recordedAt
	);
}

function isConstraintError(error: unknown): boolean {
	return (
		typeof error === "object" &&
		error !== null &&
		"code" in error &&
		typeof error.code === "string" &&
		error.code.startsWith("SQLITE_CONSTRAINT")
	);
}

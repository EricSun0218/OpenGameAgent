import { DatabaseSync } from "node:sqlite";
import type { GameMoment, GameSessionKey, JsonValue } from "@opengameagent/protocol";

export type GameGoalStatus = "active" | "waiting" | "paused" | "completed" | "failed" | "cancelled";
export type GameTaskPlanStatus = "active" | "paused" | "completed" | "failed" | "cancelled";
export type GameTaskStepStatus = "pending" | "in-progress" | "completed";

export interface GameGoal {
	id: string;
	status: GameGoalStatus;
	revision: number;
	label?: string;
	data: JsonValue;
	progress?: JsonValue;
	wakeAt?: GameMoment;
	reason?: string;
	createdAt: number;
	updatedAt: number;
}

export interface GameTaskStep {
	id: string;
	status: GameTaskStepStatus;
	label?: string;
	data: JsonValue;
	evidence?: JsonValue;
}

export interface GameTaskPlan {
	id: string;
	status: GameTaskPlanStatus;
	revision: number;
	label?: string;
	data: JsonValue;
	steps: readonly GameTaskStep[];
	reason?: string;
	createdAt: number;
	updatedAt: number;
}

export type GameGoalMutation =
	| { action: "progress"; progress: JsonValue }
	| { action: "wait"; wakeAt: GameMoment; progress?: JsonValue }
	| { action: "pause"; reason?: string }
	| { action: "resume" }
	| { action: "complete"; progress?: JsonValue }
	| { action: "fail" | "cancel"; reason: string };

export interface GameTaskPlanEvidenceContext {
	session: GameSessionKey;
	inputId: string;
	plan: GameTaskPlan;
	step: GameTaskStep;
	evidence: JsonValue;
}

export interface GameTaskPlanEvidenceValidator {
	validate(context: GameTaskPlanEvidenceContext, signal?: AbortSignal): Promise<boolean> | boolean;
}

export interface SqliteGamePlanningStoreOptions {
	maximumActiveGoals?: number;
	maximumActivePlans?: number;
	maximumSteps?: number;
	terminalRetention?: number;
	maximumRecordBytes?: number;
}

interface StateRow {
	revision: number;
	status: string;
	state_json: string;
}

const terminalGoals = new Set<GameGoalStatus>(["completed", "failed", "cancelled"]);
const terminalPlans = new Set<GameTaskPlanStatus>(["completed", "failed", "cancelled"]);

function validateId(value: string, name: string): void {
	if (!/^[a-z0-9][a-z0-9._:-]{0,191}$/i.test(value))
		throw new TypeError(`${name} is not a portable bounded identifier.`);
}

function clone<T>(value: T): T {
	return structuredClone(value);
}

export class SqliteGamePlanningStore implements Disposable {
	private readonly database: DatabaseSync;
	private readonly maximumActiveGoals: number;
	private readonly maximumActivePlans: number;
	private readonly maximumSteps: number;
	private readonly terminalRetention: number;
	private readonly maximumRecordBytes: number;
	private closed = false;

	constructor(path: string, options: SqliteGamePlanningStoreOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.maximumActiveGoals = options.maximumActiveGoals ?? 32;
		this.maximumActivePlans = options.maximumActivePlans ?? 8;
		this.maximumSteps = options.maximumSteps ?? 64;
		this.terminalRetention = options.terminalRetention ?? 32;
		this.maximumRecordBytes = options.maximumRecordBytes ?? 512 * 1024;
		for (const [name, value, minimum, maximum] of [
			["maximumActiveGoals", this.maximumActiveGoals, 1, 1024],
			["maximumActivePlans", this.maximumActivePlans, 1, 256],
			["maximumSteps", this.maximumSteps, 1, 512],
			["terminalRetention", this.terminalRetention, 0, 4096],
			["maximumRecordBytes", this.maximumRecordBytes, 1024, 16 * 1024 * 1024],
		] as const) {
			if (!Number.isInteger(value) || value < minimum || value > maximum) throw new RangeError(`${name} is invalid.`);
		}
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_goals (
				world_id TEXT NOT NULL,save_id TEXT NOT NULL,timeline_id TEXT NOT NULL,generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL,session_id TEXT NOT NULL,actor_id TEXT NOT NULL,goal_id TEXT NOT NULL,
				revision INTEGER NOT NULL,status TEXT NOT NULL,updated_at INTEGER NOT NULL,state_json TEXT NOT NULL,
				PRIMARY KEY(world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,goal_id)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_goals_status ON game_goals(
				world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,status,updated_at DESC);
			CREATE TABLE IF NOT EXISTS game_task_plans (
				world_id TEXT NOT NULL,save_id TEXT NOT NULL,timeline_id TEXT NOT NULL,generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL,session_id TEXT NOT NULL,actor_id TEXT NOT NULL,plan_id TEXT NOT NULL,
				revision INTEGER NOT NULL,status TEXT NOT NULL,updated_at INTEGER NOT NULL,state_json TEXT NOT NULL,
				PRIMARY KEY(world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,plan_id)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_task_plans_status ON game_task_plans(
				world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,status,updated_at DESC);
			CREATE TABLE IF NOT EXISTS game_task_plan_advances (
				world_id TEXT NOT NULL,save_id TEXT NOT NULL,timeline_id TEXT NOT NULL,generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL,session_id TEXT NOT NULL,actor_id TEXT NOT NULL,plan_id TEXT NOT NULL,input_id TEXT NOT NULL,
				PRIMARY KEY(world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,plan_id,input_id)
			) STRICT;
			CREATE TRIGGER IF NOT EXISTS game_task_plan_advances_cleanup AFTER DELETE ON game_task_plans
			BEGIN
				DELETE FROM game_task_plan_advances
				WHERE world_id=OLD.world_id AND save_id=OLD.save_id AND timeline_id=OLD.timeline_id
					AND generation=OLD.generation AND owner_id=OLD.owner_id AND session_id=OLD.session_id
					AND actor_id=OLD.actor_id AND plan_id=OLD.plan_id;
			END;
		`);
	}

	async listGoals(session: GameSessionKey, signal?: AbortSignal): Promise<readonly GameGoal[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		return (
			this.database
				.prepare(`SELECT revision,status,state_json FROM game_goals WHERE ${this.whereSession()}
			ORDER BY updated_at DESC,goal_id`)
				.all(...this.key(session)) as unknown as StateRow[]
		).map((row) => this.parseGoal(row));
	}

	async createGoal(
		session: GameSessionKey,
		id: string,
		data: JsonValue,
		label?: string,
		signal?: AbortSignal,
	): Promise<GameGoal> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateId(id, "Goal id");
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const existing = this.readGoalRow(session, id);
			if (existing) {
				const goal = this.parseGoal(existing);
				if (JSON.stringify({ data: goal.data, label: goal.label }) !== JSON.stringify({ data, label }))
					throw new Error("Goal id identifies different content.");
				this.database.exec("COMMIT");
				return goal;
			}
			const count = this.count("game_goals", session, "status IN ('active','waiting','paused')");
			if (count >= this.maximumActiveGoals) throw new Error("Active goal capacity is exhausted.");
			const timestamp = Date.now();
			const goal: GameGoal = {
				id,
				status: "active",
				revision: 1,
				...(label === undefined ? {} : { label }),
				data,
				createdAt: timestamp,
				updatedAt: timestamp,
			};
			this.insert("game_goals", "goal_id", session, goal.id, goal);
			this.prune("game_goals", "goal_id", session, ["completed", "failed", "cancelled"]);
			this.database.exec("COMMIT");
			return clone(goal);
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async mutateGoal(
		session: GameSessionKey,
		id: string,
		expectedRevision: number,
		mutation: GameGoalMutation,
		signal?: AbortSignal,
	): Promise<GameGoal> {
		this.ensureOpen();
		signal?.throwIfAborted();
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const row = this.readGoalRow(session, id);
			if (!row) throw new Error("Goal was not found.");
			const current = this.parseGoal(row);
			if (current.revision !== expectedRevision) throw new Error("Goal revision conflict.");
			if (terminalGoals.has(current.status)) throw new Error("Terminal goals are immutable.");
			let next: GameGoal;
			const base = { ...current, revision: current.revision + 1, updatedAt: Date.now() };
			switch (mutation.action) {
				case "progress":
					next = { ...base, status: "active", progress: mutation.progress };
					break;
				case "wait":
					next = {
						...base,
						status: "waiting",
						wakeAt: mutation.wakeAt,
						...(mutation.progress === undefined ? {} : { progress: mutation.progress }),
					};
					break;
				case "pause":
					next = { ...base, status: "paused", ...(mutation.reason === undefined ? {} : { reason: mutation.reason }) };
					break;
				case "resume":
					next = { ...base, status: "active" };
					delete next.reason;
					delete next.wakeAt;
					break;
				case "complete":
					next = {
						...base,
						status: "completed",
						...(mutation.progress === undefined ? {} : { progress: mutation.progress }),
					};
					break;
				case "fail":
					next = { ...base, status: "failed", reason: mutation.reason };
					break;
				case "cancel":
					next = { ...base, status: "cancelled", reason: mutation.reason };
					break;
			}
			this.update("game_goals", "goal_id", session, id, expectedRevision, next);
			if (terminalGoals.has(next.status))
				this.prune("game_goals", "goal_id", session, ["completed", "failed", "cancelled"]);
			this.database.exec("COMMIT");
			return clone(next);
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async listPlans(session: GameSessionKey, signal?: AbortSignal): Promise<readonly GameTaskPlan[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		return (
			this.database
				.prepare(`SELECT revision,status,state_json FROM game_task_plans WHERE ${this.whereSession()}
			ORDER BY updated_at DESC,plan_id`)
				.all(...this.key(session)) as unknown as StateRow[]
		).map((row) => this.parsePlan(row));
	}

	async createPlan(
		session: GameSessionKey,
		id: string,
		steps: readonly Omit<GameTaskStep, "status" | "evidence">[],
		data: JsonValue,
		label?: string,
		signal?: AbortSignal,
	): Promise<GameTaskPlan> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateId(id, "Plan id");
		this.validateSteps(steps);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const existing = this.readPlanRow(session, id);
			if (existing) {
				const plan = this.parsePlan(existing);
				if (
					JSON.stringify({
						steps: plan.steps.map(({ status: _, evidence: __, ...step }) => step),
						data: plan.data,
						label: plan.label,
					}) !== JSON.stringify({ steps, data, label })
				)
					throw new Error("Plan id identifies different content.");
				this.database.exec("COMMIT");
				return plan;
			}
			if (this.count("game_task_plans", session, "status IN ('active','paused')") >= this.maximumActivePlans)
				throw new Error("Active plan capacity is exhausted.");
			const timestamp = Date.now();
			const normalized = steps.map(
				(step, index): GameTaskStep => ({ ...step, status: index === 0 ? "in-progress" : "pending" }),
			);
			const plan: GameTaskPlan = {
				id,
				status: "active",
				revision: 1,
				...(label === undefined ? {} : { label }),
				data,
				steps: normalized,
				createdAt: timestamp,
				updatedAt: timestamp,
			};
			this.insert("game_task_plans", "plan_id", session, id, plan);
			this.prune("game_task_plans", "plan_id", session, ["completed", "failed", "cancelled"]);
			this.database.exec("COMMIT");
			return clone(plan);
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async advancePlan(
		session: GameSessionKey,
		id: string,
		expectedRevision: number,
		inputId: string,
		evidence: JsonValue,
		validator: GameTaskPlanEvidenceValidator,
		signal?: AbortSignal,
	): Promise<GameTaskPlan> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateId(inputId, "Input id");
		const initialRow = this.readPlanRow(session, id);
		if (!initialRow) throw new Error("Plan was not found.");
		const initial = this.parsePlan(initialRow);
		if (initial.revision !== expectedRevision) throw new Error("Plan revision conflict.");
		if (initial.status !== "active") throw new Error("Only active plans can advance.");
		const initialStep = initial.steps.find((value) => value.status === "in-progress");
		if (!initialStep) throw new Error("Active plan has no in-progress step.");
		const initialDuplicate = this.database
			.prepare(
				`SELECT 1 AS found FROM game_task_plan_advances WHERE ${this.whereSession()} AND plan_id=? AND input_id=?`,
			)
			.get(...this.key(session), id, inputId);
		if (initialDuplicate) throw new Error("This input already advanced the plan.");
		if (
			!(await validator.validate(
				{ session, inputId, plan: clone(initial), step: clone(initialStep), evidence },
				signal,
			))
		)
			throw new Error("Plan evidence was rejected by the host.");
		signal?.throwIfAborted();

		this.database.exec("BEGIN IMMEDIATE");
		try {
			const row = this.readPlanRow(session, id);
			if (!row) throw new Error("Plan was not found.");
			const current = this.parsePlan(row);
			if (current.revision !== expectedRevision) throw new Error("Plan revision conflict.");
			if (current.status !== "active") throw new Error("Only active plans can advance.");
			const step = current.steps.find((value) => value.status === "in-progress");
			if (!step) throw new Error("Active plan has no in-progress step.");
			const duplicate = this.database
				.prepare(
					`SELECT 1 AS found FROM game_task_plan_advances WHERE ${this.whereSession()} AND plan_id=? AND input_id=?`,
				)
				.get(...this.key(session), id, inputId);
			if (duplicate) throw new Error("This input already advanced the plan.");
			const steps = current.steps.map((value) => ({ ...value }));
			const index = steps.findIndex((value) => value.id === step.id);
			const active = steps[index];
			if (!active) throw new Error("Plan step disappeared.");
			active.status = "completed";
			active.evidence = evidence;
			const following = steps[index + 1];
			if (following) following.status = "in-progress";
			const next: GameTaskPlan = {
				...current,
				revision: current.revision + 1,
				status: following ? "active" : "completed",
				steps,
				updatedAt: Date.now(),
			};
			this.update("game_task_plans", "plan_id", session, id, expectedRevision, next);
			this.database
				.prepare(
					`INSERT INTO game_task_plan_advances (${this.columnsSession()},plan_id,input_id) VALUES (?,?,?,?,?,?,?,?,?)`,
				)
				.run(...this.key(session), id, inputId);
			if (next.status === "completed")
				this.prune("game_task_plans", "plan_id", session, ["completed", "failed", "cancelled"]);
			this.database.exec("COMMIT");
			return clone(next);
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async pausePlan(
		session: GameSessionKey,
		id: string,
		expectedRevision: number,
		reason?: string,
		signal?: AbortSignal,
	): Promise<GameTaskPlan> {
		return this.setPlanStatus(session, id, expectedRevision, "paused", reason, signal);
	}
	async resumePlan(
		session: GameSessionKey,
		id: string,
		expectedRevision: number,
		signal?: AbortSignal,
	): Promise<GameTaskPlan> {
		return this.setPlanStatus(session, id, expectedRevision, "active", undefined, signal);
	}
	async finishPlan(
		session: GameSessionKey,
		id: string,
		expectedRevision: number,
		status: "failed" | "cancelled",
		reason: string,
		signal?: AbortSignal,
	): Promise<GameTaskPlan> {
		return this.setPlanStatus(session, id, expectedRevision, status, reason, signal);
	}

	async replaceRemaining(
		session: GameSessionKey,
		id: string,
		expectedRevision: number,
		replacement: readonly Omit<GameTaskStep, "status" | "evidence">[],
		signal?: AbortSignal,
	): Promise<GameTaskPlan> {
		this.ensureOpen();
		signal?.throwIfAborted();
		this.validateSteps(replacement);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const row = this.readPlanRow(session, id);
			if (!row) throw new Error("Plan was not found.");
			const current = this.parsePlan(row);
			if (current.revision !== expectedRevision) throw new Error("Plan revision conflict.");
			if (terminalPlans.has(current.status)) throw new Error("Terminal plans are immutable.");
			const completed = current.steps.filter((step) => step.status === "completed");
			const unfinished = replacement.map(
				(step, index): GameTaskStep => ({ ...step, status: index === 0 ? "in-progress" : "pending" }),
			);
			const next: GameTaskPlan = {
				...current,
				revision: current.revision + 1,
				steps: [...completed, ...unfinished],
				updatedAt: Date.now(),
			};
			this.update("game_task_plans", "plan_id", session, id, expectedRevision, next);
			this.database.exec("COMMIT");
			return clone(next);
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}
	[Symbol.dispose](): void {
		this.close();
	}

	private async setPlanStatus(
		session: GameSessionKey,
		id: string,
		expectedRevision: number,
		status: GameTaskPlanStatus,
		reason?: string,
		signal?: AbortSignal,
	): Promise<GameTaskPlan> {
		this.ensureOpen();
		signal?.throwIfAborted();
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const row = this.readPlanRow(session, id);
			if (!row) throw new Error("Plan was not found.");
			const current = this.parsePlan(row);
			if (current.revision !== expectedRevision) throw new Error("Plan revision conflict.");
			if (terminalPlans.has(current.status)) throw new Error("Terminal plans are immutable.");
			if (status === "active" && current.status !== "paused") throw new Error("Only paused plans can resume.");
			if (status === "paused" && current.status !== "active") throw new Error("Only active plans can pause.");
			const steps = terminalPlans.has(status)
				? current.steps.map((step) => (step.status === "in-progress" ? { ...step, status: "pending" as const } : step))
				: current.steps;
			const next: GameTaskPlan = {
				...current,
				status,
				revision: current.revision + 1,
				steps,
				updatedAt: Date.now(),
				...(reason === undefined ? {} : { reason }),
			};
			if (status === "active") delete next.reason;
			this.update("game_task_plans", "plan_id", session, id, expectedRevision, next);
			if (terminalPlans.has(next.status))
				this.prune("game_task_plans", "plan_id", session, ["completed", "failed", "cancelled"]);
			this.database.exec("COMMIT");
			return clone(next);
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	private validateSteps(steps: readonly Omit<GameTaskStep, "status" | "evidence">[]): void {
		if (steps.length < 1 || steps.length > this.maximumSteps)
			throw new RangeError("Plan steps exceed the configured bounds.");
		const ids = new Set<string>();
		for (const step of steps) {
			validateId(step.id, "Step id");
			if (ids.has(step.id)) throw new Error("Plan step ids must be unique.");
			ids.add(step.id);
		}
	}
	private parseGoal(row: StateRow): GameGoal {
		try {
			const value = JSON.parse(row.state_json) as GameGoal;
			if (
				value.revision !== row.revision ||
				value.status !== row.status ||
				!Number.isInteger(value.revision) ||
				value.revision < 1
			)
				throw new Error();
			return value;
		} catch {
			throw new Error("Stored goal is corrupt.");
		}
	}
	private parsePlan(row: StateRow): GameTaskPlan {
		try {
			const value = JSON.parse(row.state_json) as GameTaskPlan;
			if (
				value.revision !== row.revision ||
				value.status !== row.status ||
				!Number.isInteger(value.revision) ||
				value.revision < 1 ||
				!Array.isArray(value.steps)
			)
				throw new Error();
			const active = value.steps.filter((step) => step.status === "in-progress").length;
			if (!terminalPlans.has(value.status) && active !== 1) throw new Error();
			if (terminalPlans.has(value.status) && active > 0) throw new Error();
			return value;
		} catch {
			throw new Error("Stored task plan is corrupt.");
		}
	}
	private readGoalRow(session: GameSessionKey, id: string): StateRow | undefined {
		return this.database
			.prepare(`SELECT revision,status,state_json FROM game_goals WHERE ${this.whereSession()} AND goal_id=?`)
			.get(...this.key(session), id) as StateRow | undefined;
	}
	private readPlanRow(session: GameSessionKey, id: string): StateRow | undefined {
		return this.database
			.prepare(`SELECT revision,status,state_json FROM game_task_plans WHERE ${this.whereSession()} AND plan_id=?`)
			.get(...this.key(session), id) as StateRow | undefined;
	}
	private insert(
		table: string,
		idColumn: string,
		session: GameSessionKey,
		id: string,
		state: GameGoal | GameTaskPlan,
	): void {
		const json = JSON.stringify(state);
		this.ensureSize(json);
		this.database
			.prepare(
				`INSERT INTO ${table} (${this.columnsSession()},${idColumn},revision,status,updated_at,state_json) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)`,
			)
			.run(...this.key(session), id, state.revision, state.status, state.updatedAt, json);
	}
	private update(
		table: string,
		idColumn: string,
		session: GameSessionKey,
		id: string,
		expectedRevision: number,
		state: GameGoal | GameTaskPlan,
	): void {
		const json = JSON.stringify(state);
		this.ensureSize(json);
		const result = this.database
			.prepare(
				`UPDATE ${table} SET revision=?,status=?,updated_at=?,state_json=? WHERE ${this.whereSession()} AND ${idColumn}=? AND revision=?`,
			)
			.run(state.revision, state.status, state.updatedAt, json, ...this.key(session), id, expectedRevision);
		if (result.changes !== 1) throw new Error("Planning state revision conflict.");
	}
	private count(table: string, session: GameSessionKey, predicate: string): number {
		return Number(
			(
				this.database
					.prepare(`SELECT COUNT(*) AS count FROM ${table} WHERE ${this.whereSession()} AND ${predicate}`)
					.get(...this.key(session)) as { count: number }
			).count,
		);
	}
	private prune(table: string, idColumn: string, session: GameSessionKey, statuses: readonly string[]): void {
		const placeholders = statuses.map(() => "?").join(",");
		const terminalCount = Number(
			(
				this.database
					.prepare(
						`SELECT COUNT(*) AS count FROM ${table} WHERE ${this.whereSession()} AND status IN (${placeholders})`,
					)
					.get(...this.key(session), ...statuses) as { count: number }
			).count,
		);
		const excess = terminalCount - this.terminalRetention;
		if (excess <= 0) return;
		this.database
			.prepare(
				`DELETE FROM ${table} WHERE rowid IN (SELECT rowid FROM ${table} WHERE ${this.whereSession()} AND status IN (${placeholders}) ORDER BY updated_at ASC,${idColumn} ASC LIMIT ? )`,
			)
			.run(...this.key(session), ...statuses, excess);
	}
	private ensureSize(json: string): void {
		if (Buffer.byteLength(json) > this.maximumRecordBytes)
			throw new RangeError("Planning record exceeds the configured size limit.");
	}
	private columnsSession(): string {
		return "world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id";
	}
	private whereSession(): string {
		return "world_id=? AND save_id=? AND timeline_id=? AND generation=? AND owner_id=? AND session_id=? AND actor_id=?";
	}
	private key(session: GameSessionKey): [string, string, string, number, string, string, string] {
		return [
			session.worldId,
			session.saveId,
			session.timelineId,
			session.generation,
			session.ownerId,
			session.sessionId,
			session.actorId,
		];
	}
	private ensureOpen(): void {
		if (this.closed) throw new Error("SQLite planning store is closed.");
	}
}

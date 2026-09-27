import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import {
	GoalAuthorityRetiredError,
	GoalAuthorityRetirementConflictError,
	GoalCapacityError,
	type GoalLimits,
	type GoalPlanStep,
	GoalRevisionConflictError,
	GoalService,
	GoalValidationError,
} from "@opengameagent/extensions";
import { canonicalizeJsonValue } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteGoalStore } from "../src/index.js";

const roots: string[] = [];
const authorities: SqliteAuthority[] = [];
const limits: GoalLimits = {
	maxObjectiveBytes: 1_024,
	maxStepsPerGoal: 8,
	maxStepBytes: 512,
	maxPlanBytes: 8_192,
	maxRecordBytes: 32_768,
	maxModelResultBytes: 16_384,
	maxEvidencePerStep: 4,
	maxEvidenceKindBytes: 64,
	maxEvidenceReferenceBytes: 256,
	maxOpenGoalsPerOwnerGeneration: 2,
	maxListPageSize: 4,
	maxPurgeBatchSize: 4,
};

afterEach(async () => {
	for (const authority of authorities.splice(0)) authority.close();
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	for (const root of roots.splice(0)) {
		const target = resolve(root);
		if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
		await rm(target, { recursive: true, force: true });
	}
});

describe("SqliteGoalStore", () => {
	it("rejects a record whose aggregate canonical bytes exceed the store budget without committing it", async () => {
		const authority = open(await databasePath());
		const draft = {
			goalId: "goal-record-budget",
			partition: partition(),
			objective: "Fit every field but not the complete record",
			plan: [step("step-1", "Inspect", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		} as const;
		const recordBytes = initialGoalRecordBytes(draft);
		const rejectingStore = new SqliteGoalStore(authority, {
			...limits,
			maxRecordBytes: recordBytes - 1,
		} as GoalLimits);

		await expect(rejectingStore.create(draft, limits.maxOpenGoalsPerOwnerGeneration)).rejects.toMatchObject({
			code: "goal_record_too_large",
		});
		const reader = new SqliteGoalStore(authority, { ...limits, maxRecordBytes: recordBytes } as GoalLimits);
		expect(await reader.get(goalKey(draft.goalId))).toBeUndefined();
	});

	it("fails bounded hydration when an existing record exceeds a tighter recovery budget", async () => {
		const authority = open(await databasePath());
		const draft = {
			goalId: "goal-recovery-record-budget",
			partition: partition(),
			objective: "Reject an oversized recovered record",
			plan: [step("step-1", "Inspect", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		} as const;
		const writer = new SqliteGoalStore(authority, limits);
		await writer.create(draft, limits.maxOpenGoalsPerOwnerGeneration);
		const recordBytes = initialGoalRecordBytes(draft);
		const reader = new SqliteGoalStore(authority, { ...limits, maxRecordBytes: recordBytes - 1 });

		await expect(reader.get(goalKey(draft.goalId))).rejects.toMatchObject({ code: "goal_record_too_large" });
		expect(await writer.get(goalKey(draft.goalId))).toMatchObject({ goalId: draft.goalId, revision: "1" });
	});

	it("rolls back host advancement when the complete SQLite replacement crosses its record budget", async () => {
		const authority = open(await databasePath());
		const draft = {
			goalId: "goal-sqlite-replace-budget",
			partition: partition(),
			objective: "Keep the original SQLite record",
			plan: [step("step-1", "Inspect", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		} as const;
		const initialRecordBytes = initialGoalRecordBytes(draft);
		const boundedLimits = { ...limits, maxRecordBytes: initialRecordBytes };
		const current = new GoalService(
			new SqliteGoalStore(authority, boundedLimits),
			{ validate: async () => true },
			{ validate: async () => true },
		);
		await current.create(draft);

		await expect(
			current.advanceFromHost({
				goalId: draft.goalId,
				partition: partition(),
				stepId: "step-1",
				inputId: "input-1",
				expectedRevision: "1",
				evidence: { kind: "action_receipt", referenceId: "receipt-1" },
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toMatchObject({ code: "goal_record_too_large" });
		expect(await current.get(goalKey(draft.goalId))).toMatchObject({
			revision: "1",
			status: "active",
			plan: draft.plan,
		});
	});

	it("shares status-filter rejection with the in-memory authority", async () => {
		const store = new SqliteGoalStore(open(await databasePath()), limits);
		await expect(store.list({ ...partition(), statuses: [], limit: 1 })).rejects.toBeInstanceOf(GoalValidationError);
	});

	it("restores exact goal state and ordered evidence after restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority);
		await first.create({
			goalId: "goal-1",
			partition: partition(),
			objective: "Persist across restart",
			plan: [step("step-1", "Observe", "in_progress"), step("step-2", "Report", "pending")],
			gameTime: gameTime("90071992547409931234567890"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		await first.update({
			...goalKey("goal-1"),
			expectedRevision: "1",
			plan: [
				step("step-1", "Observe", "completed", [{ kind: "action_receipt", referenceId: "receipt-1" }]),
				step("step-2", "Report", "in_progress"),
			],
			gameTime: gameTime("90071992547409931234567891"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		firstAuthority.close();

		const reopened = service(open(path));
		expect(await reopened.get(goalKey("goal-1"))).toMatchObject({
			revision: "2",
			status: "active",
			createdGameTime: gameTime("90071992547409931234567890"),
			gameTime: gameTime("90071992547409931234567891"),
			plan: [
				step("step-1", "Observe", "completed", [{ kind: "action_receipt", referenceId: "receipt-1" }]),
				step("step-2", "Report", "in_progress"),
			],
		});
	});

	it("atomically retires an exact Goal authority and preserves its creation fence after restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority);
		for (const goalId of ["goal-retire-active", "goal-retire-paused"]) {
			await first.create({
				goalId,
				partition: partition(),
				objective: goalId,
				plan: [],
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
			});
		}
		await first.update({
			...goalKey("goal-retire-paused"),
			expectedRevision: "1",
			status: "paused",
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		const request = {
			retirementId: "retirement-sqlite-1",
			stateRevision: "90071992547409931234567890",
			partition: partition(),
			gameTime: gameTime("3"),
			recordedAt: "2026-08-28T00:00:02.000Z",
		} as const;
		expect(await first.retireAuthority(request)).toEqual({ ...request, cancelledGoalCount: 2 });
		firstAuthority.close();

		const reopened = service(open(path));
		expect(await reopened.retireAuthority(request)).toEqual({ ...request, cancelledGoalCount: 2 });
		expect(
			(await reopened.list({ ...partition(), statuses: ["cancelled"], limit: 4 })).records.map((goal) => ({
				goalId: goal.goalId,
				revision: goal.revision,
				gameTime: goal.gameTime,
			})),
		).toEqual([
			{ goalId: "goal-retire-active", revision: "2", gameTime: gameTime("3") },
			{ goalId: "goal-retire-paused", revision: "3", gameTime: gameTime("3") },
		]);
		await expect(
			reopened.create({
				goalId: "goal-late",
				partition: partition(),
				objective: "must remain fenced",
				plan: [],
				gameTime: gameTime("4"),
				recordedAt: "2026-08-28T00:00:03.000Z",
			}),
		).rejects.toBeInstanceOf(GoalAuthorityRetiredError);
		await expect(reopened.retireAuthority({ ...request, retirementId: "retirement-conflict" })).rejects.toBeInstanceOf(
			GoalAuthorityRetirementConflictError,
		);
		await expect(
			reopened.create({
				goalId: "goal-other-actor",
				partition: { ...partition(), actorId: "actor-2" },
				objective: "independent authority",
				plan: [],
				gameTime: gameTime("4"),
				recordedAt: "2026-08-28T00:00:03.000Z",
			}),
		).resolves.toMatchObject({ status: "active" });
	});

	it("restores the exact host-advance replay identity after restart", async () => {
		const path = await databasePath();
		const firstAuthority = open(path);
		const first = service(firstAuthority);
		await first.create({
			goalId: "goal-host-replay",
			partition: partition(),
			objective: "Replay only the committed host transition",
			plan: [step("step-1", "Observe", "in_progress")],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const request = {
			goalId: "goal-host-replay",
			partition: partition(),
			stepId: "step-1",
			inputId: "input-1",
			expectedRevision: "1",
			evidence: { kind: "action_receipt", referenceId: "receipt-1" },
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		} as const;
		expect((await first.advanceFromHost(request)).status).toBe("advanced");
		firstAuthority.close();

		const reopened = service(open(path));
		expect((await reopened.advanceFromHost(request)).status).toBe("already_advanced");
		expect((await reopened.advanceFromHost({ ...request, inputId: "input-other" })).status).toBe("revision_conflict");
	});

	it("settles competing authorities with compare-and-swap", async () => {
		const path = await databasePath();
		const first = service(open(path));
		const second = service(open(path));
		await first.create({
			goalId: "goal-1",
			partition: partition(),
			objective: "One writer wins",
			plan: [],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const stale = await second.get(goalKey("goal-1"));
		expect(stale?.revision).toBe("1");
		await first.update({
			...goalKey("goal-1"),
			expectedRevision: "1",
			status: "paused",
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		await expect(
			second.update({
				...goalKey("goal-1"),
				expectedRevision: "1",
				status: "cancelled",
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toBeInstanceOf(GoalRevisionConflictError);
	});

	it("atomically enforces open-goal capacity and isolates rewind generations", async () => {
		const current = service(open(await databasePath()));
		for (const goalId of ["goal-1", "goal-2"]) {
			await current.create({
				goalId,
				partition: partition(),
				objective: goalId,
				plan: [],
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
			});
		}
		await expect(
			current.create({
				goalId: "goal-3",
				partition: partition(),
				objective: "too many",
				plan: [],
				gameTime: gameTime("1"),
				recordedAt: "2026-08-28T00:00:00.000Z",
			}),
		).rejects.toBeInstanceOf(GoalCapacityError);
		await current.create({
			goalId: "goal-3",
			partition: { ...partition(), generation: "2" },
			objective: "rewound world",
			plan: [],
			gameTime: { ...gameTime("1"), generation: "2" },
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		expect((await current.list({ ...partition(), limit: 4 })).records).toHaveLength(2);
		expect((await current.list({ ...partition(), generation: "2", limit: 4 })).records).toHaveLength(1);
	});

	it("filters host reads and purges terminal goals in stable key order", async () => {
		const current = service(open(await databasePath()));
		await current.create({
			goalId: "goal-1",
			partition: partition(),
			objective: "first",
			plan: [],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		await current.create({
			goalId: "goal-2",
			partition: { ...partition(), actorId: "actor-2", sessionId: "session-2" },
			objective: "second",
			plan: [],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const cancelled = await current.update({
			...goalKey("goal-1"),
			expectedRevision: "1",
			status: "cancelled",
			gameTime: gameTime("2"),
			recordedAt: "2026-08-28T00:00:01.000Z",
		});
		expect(
			(await current.list({ ...partition(), actorId: "actor-1", statuses: ["cancelled"], limit: 4 })).records.map(
				(record) => record.goalId,
			),
		).toEqual(["goal-1"]);
		expect(await current.purgeTerminal(partition(), "2026-08-28T00:00:02.000Z", 1)).toEqual([cancelled.goalKey]);
		expect(await current.get(goalKey("goal-1"))).toBeUndefined();
	});

	it("does not read or replace a goal through a different optional authority partition", async () => {
		const current = service(open(await databasePath()));
		await current.create({
			goalId: "goal-private",
			partition: partition(),
			objective: "Exact partition",
			plan: [],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const wrong = { ...goalKey("goal-private"), sessionId: "session-other", actorId: "actor-other" };
		expect(await current.get(wrong)).toBeUndefined();
		await expect(
			current.update({
				...wrong,
				expectedRevision: "1",
				status: "cancelled",
				gameTime: gameTime("2"),
				recordedAt: "2026-08-28T00:00:01.000Z",
			}),
		).rejects.toThrow("goal_not_found");
		expect((await current.get(goalKey("goal-private")))?.status).toBe("active");
	});

	it("fails closed when persisted plan data no longer satisfies the public invariant", async () => {
		const authority = open(await databasePath());
		const current = service(authority);
		await current.create({
			goalId: "goal-1",
			partition: partition(),
			objective: "Detect corruption",
			plan: [],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		authority.write((database) =>
			database.prepare("UPDATE oga_goals SET plan_json = ? WHERE goal_id = ?").run('{"unexpected":true}', "goal-1"),
		);
		await expect(current.get(goalKey("goal-1"))).rejects.toBeInstanceOf(GoalValidationError);
	});

	it("rejects an oversized persisted plan before hydration even when its decoded plan is small", async () => {
		const authority = open(await databasePath());
		const current = service(authority);
		await current.create({
			goalId: "goal-oversized-row",
			partition: partition(),
			objective: "Bound recovery",
			plan: [],
			gameTime: gameTime("1"),
			recordedAt: "2026-08-28T00:00:00.000Z",
		});
		const oversizedJson = `${" ".repeat(limits.maxPlanBytes)}[]`;
		authority.write((database) =>
			database.prepare("UPDATE oga_goals SET plan_json = ? WHERE goal_id = ?").run(oversizedJson, "goal-oversized-row"),
		);

		await expect(current.get(goalKey("goal-oversized-row"))).rejects.toMatchObject({
			code: "goal_plan_too_large",
		});
	});
});

async function databasePath(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-goal-test-"));
	roots.push(root);
	return join(root, "authority.sqlite3");
}

function open(path: string): SqliteAuthority {
	const authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
	authorities.push(authority);
	return authority;
}

function service(authority: SqliteAuthority): GoalService {
	return new GoalService(
		new SqliteGoalStore(authority, limits),
		{ validate: async () => true },
		{ validate: async () => true },
	);
}

function initialGoalRecordBytes(draft: {
	readonly goalId: string;
	readonly partition: ReturnType<typeof partition>;
	readonly objective: string;
	readonly plan: readonly GoalPlanStep[];
	readonly gameTime: ReturnType<typeof gameTime>;
	readonly recordedAt: string;
}): number {
	return new TextEncoder().encode(
		canonicalizeJsonValue({
			goalId: draft.goalId,
			partition: draft.partition,
			objective: draft.objective,
			plan: draft.plan,
			gameTime: draft.gameTime,
			goalKey: "1",
			revision: "1",
			status: "active",
			createdGameTime: draft.gameTime,
			createdAt: draft.recordedAt,
			updatedAt: draft.recordedAt,
			lastHostAdvanceFingerprint: null,
		}),
	).byteLength;
}

function partition() {
	return {
		ownerId: "owner-1",
		timelineId: "world",
		generation: "1",
		sessionId: "session-1",
		actorId: "actor-1",
	} as const;
}

function goalKey(goalId: string) {
	return { ...partition(), goalId };
}

function gameTime(tick: string) {
	return { timelineId: "world", generation: "1", tick } as const;
}

function step(
	stepId: string,
	description: string,
	status: GoalPlanStep["status"],
	evidence: GoalPlanStep["evidence"] = [],
): GoalPlanStep {
	return { stepId, description, status, evidence };
}

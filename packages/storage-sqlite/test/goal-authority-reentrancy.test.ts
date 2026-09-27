import {
	GoalAuthorityRetiredError,
	GoalAuthorityRetirementConflictError,
	type GoalAuthorityRetirementRecord,
	GoalCapacityError,
	type GoalLimits,
	type GoalListRequest,
	type GoalRecord,
	GoalRevisionConflictError,
	type GoalStore,
	GoalValidationError,
	InMemoryGoalStore,
} from "@opengameagent/extensions";
import { describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteGoalStore } from "../src/index.js";

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
const partition = { ownerId: "owner", timelineId: "world", generation: "1", sessionId: "chat", actorId: "actor" };
const gameTime = { timelineId: "world", generation: "1", tick: "1" };
const recordedAt = "2026-09-05T00:00:00.000Z";
const draft = {
	goalId: "goal",
	partition,
	objective: "Inspect the destination",
	plan: [{ stepId: "step", description: "Inspect", status: "in_progress" as const, evidence: [] }],
	gameTime,
	recordedAt,
};
const key = { ...partition, goalId: draft.goalId };
const replacement = {
	plan: draft.plan,
	status: "paused" as const,
	gameTime,
	recordedAt,
	lastHostAdvanceFingerprint: null,
};
const retirement = { retirementId: "inner", stateRevision: "1", partition, gameTime, recordedAt };

it("rejects Goal retirement above a reduced SQLite recovery budget without changing records or installing a fence", async () => {
	const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
	try {
		const writer = new SqliteGoalStore(authority, limits);
		await writer.create(draft, limits.maxOpenGoalsPerOwnerGeneration);
		await writer.create({ ...draft, goalId: "second" }, limits.maxOpenGoalsPerOwnerGeneration);
		const reader = new SqliteGoalStore(authority, { ...limits, maxOpenGoalsPerOwnerGeneration: 1 });
		await expect(reader.retireAuthority(retirement)).rejects.toBeInstanceOf(GoalCapacityError);
		expect((await writer.list({ ...partition, limit: limits.maxListPageSize })).records).toMatchObject([
			{ goalId: "goal", status: "active", revision: "1" },
			{ goalId: "second", status: "active", revision: "1" },
		]);
		expect(
			authority.read((database) =>
				database.prepare("SELECT COUNT(*) FROM oga_goal_authority_retirements").pluck().get(),
			),
		).toBe(0);
	} finally {
		authority.close();
	}
});

describe.each(["memory", "sqlite"] as const)("%s Goal authority getter reentrancy", (kind) => {
	async function withStore(test: (store: GoalStore) => Promise<void>): Promise<void> {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			await test(kind === "memory" ? new InMemoryGoalStore(limits) : new SqliteGoalStore(authority, limits));
		} finally {
			authority.close();
		}
	}

	it.each([
		"ownerId",
		"timelineId",
		"generation",
		"sessionId",
		"actorId",
		"statuses",
		"afterGoalKey",
		"limit",
	] as const)("captures list %s before reading the store", (field) =>
		withStore(async (store) => {
			await store.create(draft, limits.maxOpenGoalsPerOwnerGeneration);
			await store.create({ ...draft, goalId: "second" }, limits.maxOpenGoalsPerOwnerGeneration);
			const values: GoalListRequest = { ...partition, statuses: ["active"], afterGoalKey: 0n, limit: 1 };
			let reads = 0;
			const request = {
				...values,
				get [field]() {
					reads += 1;
					return field === "limit" && reads > 1 ? limits.maxListPageSize + 1 : values[field];
				},
			};
			expect((await store.list(request)).records.map((record) => record.goalId)).toEqual([draft.goalId]);
			expect(reads).toBe(1);
		}),
	);

	it.each([0, limits.maxListPageSize + 1, Number.NaN])(
		"rejects direct list limit %s before reading the store",
		(limit) =>
			withStore(async (store) => {
				await expect(store.list({ ...partition, limit })).rejects.toMatchObject({ code: "invalid_goal_page_limit" });
			}),
	);

	it("captures creation getters before checking the permanent authority fence", () =>
		withStore(async (store) => {
			let nested: Promise<GoalAuthorityRetirementRecord> | undefined;
			await expect(
				store.create(
					{
						...draft,
						get objective() {
							nested = store.retireAuthority(retirement);
							return draft.objective;
						},
					},
					limits.maxOpenGoalsPerOwnerGeneration,
				),
			).rejects.toBeInstanceOf(GoalAuthorityRetiredError);
			expect(await nested).toMatchObject({ cancelledGoalCount: 0 });
			expect(await store.get(key)).toBeUndefined();
		}));

	it("does not replace a goal after a replacement getter retires its authority", () =>
		withStore(async (store) => {
			await store.create(draft, limits.maxOpenGoalsPerOwnerGeneration);
			let nested: Promise<GoalAuthorityRetirementRecord> | undefined;
			await expect(
				store.replace(key, "1", {
					...replacement,
					get plan() {
						nested = store.retireAuthority(retirement);
						return replacement.plan;
					},
				}),
			).rejects.toBeInstanceOf(GoalAuthorityRetiredError);
			expect(await nested).toMatchObject({ cancelledGoalCount: 1 });
			expect(await store.get(key)).toMatchObject({ status: "cancelled", revision: "2" });
		}));

	it("preserves a replacement committed by another replacement getter", () =>
		withStore(async (store) => {
			await store.create(draft, limits.maxOpenGoalsPerOwnerGeneration);
			let nested: Promise<GoalRecord> | undefined;
			await expect(
				store.replace(key, "1", {
					...replacement,
					status: "active",
					get plan() {
						nested = store.replace(key, "1", replacement);
						return replacement.plan;
					},
				}),
			).rejects.toBeInstanceOf(GoalRevisionConflictError);
			expect(await nested).toMatchObject({ status: "paused", revision: "2" });
			expect(await store.get(key)).toMatchObject({ status: "paused", revision: "2" });
		}));

	it("preserves a retirement committed by an invalid outer retirement getter", () =>
		withStore(async (store) => {
			await store.create(draft, limits.maxOpenGoalsPerOwnerGeneration);
			let nested: Promise<GoalAuthorityRetirementRecord> | undefined;
			await expect(
				store.retireAuthority({
					...retirement,
					retirementId: "outer",
					get recordedAt() {
						nested = store.retireAuthority(retirement);
						return "invalid";
					},
				}),
			).rejects.toBeInstanceOf(GoalValidationError);
			expect(await nested).toMatchObject({ cancelledGoalCount: 1 });
			expect(await store.get(key)).toMatchObject({ status: "cancelled", revision: "2" });
			expect(await store.retireAuthority(retirement)).toMatchObject({ cancelledGoalCount: 1 });
			await expect(
				store.create({ ...draft, goalId: "after" }, limits.maxOpenGoalsPerOwnerGeneration),
			).rejects.toBeInstanceOf(GoalAuthorityRetiredError);
		}));

	it("checks retirement identity after all outer request getters are captured", () =>
		withStore(async (store) => {
			await store.create(draft, limits.maxOpenGoalsPerOwnerGeneration);
			let nested: Promise<GoalAuthorityRetirementRecord> | undefined;
			await expect(
				store.retireAuthority({
					...retirement,
					retirementId: "outer",
					get recordedAt() {
						nested = store.retireAuthority(retirement);
						return recordedAt;
					},
				}),
			).rejects.toBeInstanceOf(GoalAuthorityRetirementConflictError);
			expect(await nested).toMatchObject({ cancelledGoalCount: 1 });
			expect(await store.retireAuthority(retirement)).toMatchObject({ cancelledGoalCount: 1 });
		}));
});

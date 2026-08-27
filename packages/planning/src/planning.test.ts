import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import type { GameSessionKey } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteGamePlanningStore } from "./planning.js";

const directories: string[] = [];

function session(actorId = "actor-a", ownerId = "owner-a", generation = 1): GameSessionKey {
	return {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation,
		ownerId,
		sessionId: `session-${actorId}`,
		actorId,
	};
}

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-planning-"));
	directories.push(directory);
	return join(directory, "planning.sqlite");
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("SqliteGamePlanningStore goals", () => {
	it("limits only live goals and retains a bounded terminal audit", async () => {
		const path = await databasePath();
		using store = new SqliteGamePlanningStore(path, { maximumActiveGoals: 1, terminalRetention: 2 });

		const first = await store.createGoal(session(), "goal-1", { target: "one" });
		await expect(store.createGoal(session(), "goal-blocked", {})).rejects.toThrow("capacity");
		const waiting = await store.mutateGoal(session(), first.id, first.revision, {
			action: "wait",
			wakeAt: { tick: 20, calendar: "game" },
		});
		const paused = await store.mutateGoal(session(), waiting.id, waiting.revision, { action: "pause" });
		const resumed = await store.mutateGoal(session(), paused.id, paused.revision, { action: "resume" });
		await store.mutateGoal(session(), resumed.id, resumed.revision, { action: "complete", progress: { done: true } });

		const second = await store.createGoal(session(), "goal-2", { target: "two" });
		await store.mutateGoal(session(), second.id, second.revision, { action: "fail", reason: "failed" });
		const third = await store.createGoal(session(), "goal-3", { target: "three" });
		await store.mutateGoal(session(), third.id, third.revision, { action: "cancel", reason: "cancelled" });
		const fourth = await store.createGoal(session(), "goal-4", { target: "four" });

		const goals = await store.listGoals(session());
		expect(goals.map((goal) => goal.id)).toEqual(expect.arrayContaining(["goal-2", "goal-3", "goal-4"]));
		expect(goals.map((goal) => goal.id)).not.toContain("goal-1");
		expect(goals.filter((goal) => ["completed", "failed", "cancelled"].includes(goal.status))).toHaveLength(2);
		expect(goals.find((goal) => goal.id === fourth.id)?.status).toBe("active");
	});

	it("uses revision CAS, isolates actors and fails closed on corrupt state", async () => {
		const path = await databasePath();
		{
			using store = new SqliteGamePlanningStore(path);
			const goal = await store.createGoal(session(), "goal", { value: 1 });
			await expect(
				store.mutateGoal(session(), goal.id, goal.revision + 1, { action: "progress", progress: 1 }),
			).rejects.toThrow("revision conflict");
			expect(await store.listGoals(session("actor-b"))).toEqual([]);
		}

		const database = new DatabaseSync(path);
		database.prepare("UPDATE game_goals SET state_json='not-json'").run();
		database.close();
		using reopened = new SqliteGamePlanningStore(path);
		await expect(reopened.listGoals(session())).rejects.toThrow("corrupt");
	});
});

describe("SqliteGamePlanningStore task plans", () => {
	it("validates evidence, advances once per input and supports reversible pause", async () => {
		const path = await databasePath();
		using store = new SqliteGamePlanningStore(path);
		const created = await store.createPlan(
			session(),
			"plan",
			[
				{ id: "step-1", data: { action: "first" } },
				{ id: "step-2", data: { action: "second" } },
			],
			{ objective: "test" },
		);
		expect(created.steps.map((step) => step.status)).toEqual(["in-progress", "pending"]);

		await expect(
			store.advancePlan(
				session(),
				created.id,
				created.revision,
				"input-rejected",
				{ observed: false },
				{ validate: () => false },
			),
		).rejects.toThrow("evidence was rejected");
		expect((await store.listPlans(session()))[0]?.revision).toBe(created.revision);

		const advanced = await store.advancePlan(
			session(),
			created.id,
			created.revision,
			"input-1",
			{ observed: true },
			{ validate: () => true },
		);
		expect(advanced.steps.map((step) => step.status)).toEqual(["completed", "in-progress"]);
		await expect(
			store.advancePlan(session(), advanced.id, advanced.revision, "input-1", {}, { validate: () => true }),
		).rejects.toThrow("already advanced");

		const paused = await store.pausePlan(session(), advanced.id, advanced.revision, "host pause");
		expect(paused.status).toBe("paused");
		expect(paused.steps[1]?.status).toBe("in-progress");
		const resumed = await store.resumePlan(session(), paused.id, paused.revision);
		expect(resumed.status).toBe("active");
		expect(resumed.steps[1]?.status).toBe("in-progress");

		const replaced = await store.replaceRemaining(session(), resumed.id, resumed.revision, [
			{ id: "step-3", data: { action: "third" } },
			{ id: "step-4", data: { action: "fourth" } },
		]);
		expect(replaced.steps.map((step) => [step.id, step.status])).toEqual([
			["step-1", "completed"],
			["step-3", "in-progress"],
			["step-4", "pending"],
		]);
	});

	it("survives restart, rejects concurrent stale progress and keeps terminal history bounded", async () => {
		const path = await databasePath();
		let revision = 0;
		{
			using store = new SqliteGamePlanningStore(path, { terminalRetention: 1 });
			const plan = await store.createPlan(session(), "plan-1", [{ id: "step", data: null }], null);
			revision = plan.revision;
		}
		{
			using store = new SqliteGamePlanningStore(path, { terminalRetention: 1 });
			const results = await Promise.allSettled([
				store.advancePlan(session(), "plan-1", revision, "input-a", { proof: "a" }, { validate: async () => true }),
				store.advancePlan(session(), "plan-1", revision, "input-b", { proof: "b" }, { validate: async () => true }),
			]);
			expect(results.filter((result) => result.status === "fulfilled")).toHaveLength(1);
			expect(results.filter((result) => result.status === "rejected")).toHaveLength(1);

			const second = await store.createPlan(session(), "plan-2", [{ id: "step", data: null }], null);
			await store.finishPlan(session(), second.id, second.revision, "cancelled", "host cancelled");
			const plans = await store.listPlans(session());
			expect(plans).toHaveLength(1);
			expect(plans[0]?.id).toBe("plan-2");
			expect(plans[0]?.steps[0]?.status).toBe("pending");
		}
	});
});

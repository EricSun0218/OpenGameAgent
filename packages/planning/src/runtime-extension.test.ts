import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameInput, GameToolExecutionContext } from "@opengameagent/protocol";
import { preflightGameToolSchema } from "@opengameagent/runtime";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteGamePlanningStore } from "./planning.js";
import { createGamePlanningExtension } from "./runtime-extension.js";

const directories: string[] = [];
const input: GameInput = {
	id: "input-1",
	type: "npc.command",
	session: {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 1,
		ownerId: "owner",
		sessionId: "session",
		actorId: "actor",
	},
	moment: { tick: 100.5 },
	content: [{ type: "json", value: { command: "build" } }],
};

function executionContext(): GameToolExecutionContext {
	return { input, runId: "run-1", turn: 1, toolCallIndex: 0, signal: new AbortController().signal };
}

async function planningStore(): Promise<SqliteGamePlanningStore> {
	const directory = await mkdtemp(join(tmpdir(), "oga-planning-extension-"));
	directories.push(directory);
	return new SqliteGamePlanningStore(join(directory, "planning.sqlite"));
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("GamePlanningExtension", () => {
	it("advertises schema-valid optional resources and uses the canonical input identity", async () => {
		using store = await planningStore();
		const extension = createGamePlanningExtension({ store, evidenceValidator: { validate: () => true } });
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		expect(tools.map((tool) => tool.definition.name)).toEqual(["manage_game_goal", "manage_game_task_plan"]);
		for (const tool of tools) expect(() => preflightGameToolSchema(tool.definition)).not.toThrow();

		const goalTool = tools.find((tool) => tool.definition.name === "manage_game_goal");
		await goalTool?.execute(
			{ id: "call-goal", name: "manage_game_goal", arguments: { action: "create", id: "goal", data: { target: 1 } } },
			executionContext(),
		);
		expect((await store.listGoals(input.session))[0]?.id).toBe("goal");
		expect(await store.listGoals({ ...input.session, actorId: "other" })).toEqual([]);

		const segment = await extension.contextProvider?.provide(input, new AbortController().signal);
		expect(JSON.stringify(segment?.value)).toContain("goal");
	});

	it("requires host authorization and host evidence before persistent progress", async () => {
		using store = await planningStore();
		let evidenceCalls = 0;
		const extension = createGamePlanningExtension({
			store,
			evidenceValidator: {
				validate(context) {
					evidenceCalls += 1;
					return context.evidence === "verified";
				},
			},
			authorize: ({ action }) => action !== "cancel",
		});
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		const planTool = tools.find((tool) => tool.definition.name === "manage_game_task_plan");
		await planTool?.execute(
			{
				id: "call-create",
				name: "manage_game_task_plan",
				arguments: {
					action: "create",
					id: "plan",
					data: { objective: "test" },
					steps: [{ id: "step", data: { action: "work" } }],
				},
			},
			executionContext(),
		);
		await expect(
			planTool?.execute(
				{
					id: "call-rejected",
					name: "manage_game_task_plan",
					arguments: { action: "advance", id: "plan", expectedRevision: 1, evidence: "claimed" },
				},
				executionContext(),
			),
		).rejects.toThrow("evidence was rejected");
		const completed = await planTool?.execute(
			{
				id: "call-verified",
				name: "manage_game_task_plan",
				arguments: { action: "advance", id: "plan", expectedRevision: 1, evidence: "verified" },
			},
			executionContext(),
		);
		expect(JSON.stringify(completed)).toContain("completed");
		expect(evidenceCalls).toBe(2);

		const second = await store.createPlan(input.session, "plan-2", [{ id: "step", data: null }], null);
		await expect(
			planTool?.execute(
				{
					id: "call-cancel",
					name: "manage_game_task_plan",
					arguments: {
						action: "cancel",
						id: second.id,
						expectedRevision: second.revision,
						reason: "model request",
					},
				},
				executionContext(),
			),
		).rejects.toThrow("rejected by the host");
	});

	it("can be installed without exposing planning context or tools", async () => {
		using store = await planningStore();
		const extension = createGamePlanningExtension({
			store,
			evidenceValidator: { validate: () => true },
			includeContext: false,
			includeGoalTool: false,
			includeTaskPlanTool: false,
		});
		expect(extension.contextProvider).toBeUndefined();
		expect(await extension.toolProvider.provide(input, new AbortController().signal)).toEqual([]);
	});
});

import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type {
	GameInput,
	GameTool,
	GameToolDefinition,
	GameToolExecutionContext,
	JsonObject,
} from "@opengameagent/protocol";
import { preflightGameToolSchema } from "@opengameagent/runtime";
import { CompositeGameSkillSource, createGameSkillExtension, InMemoryGameSkillSource } from "@opengameagent/skills";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	createGameBehaviorEvolution,
	createGameBehaviorReviewInput,
	type GameBehaviorEvolutionResources,
	type GameBehaviorSkillValidator,
	SqliteGameBehaviorStore,
} from "./evolution.js";

const directories: string[] = [];
const input: GameInput = {
	id: "reflection-input-1",
	type: "agent.reflection",
	session: {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 1,
		ownerId: "owner",
		sessionId: "session",
		actorId: "actor-a",
	},
	moment: { tick: 100.5 },
	content: [{ type: "json", value: { outcome: "built shelter" } }],
};

const gameTools: readonly GameToolDefinition[] = [
	{
		name: "gather_resource",
		label: "Gather resource",
		description: "Gather one resource.",
		parameters: { type: "object", properties: {}, additionalProperties: false },
	},
	{
		name: "build_structure",
		label: "Build structure",
		description: "Build one structure.",
		parameters: { type: "object", properties: {}, additionalProperties: false },
	},
];

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

async function createStorePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-evolution-"));
	directories.push(directory);
	return join(directory, "evolution.sqlite");
}

function context(currentInput: GameInput): GameToolExecutionContext {
	return {
		input: currentInput,
		runId: "run-1",
		turn: 1,
		toolCallIndex: 0,
		signal: new AbortController().signal,
	};
}

function tool(_resources: GameBehaviorEvolutionResources, tools: readonly GameTool[], name: string): GameTool {
	const found = tools.find((candidate) => candidate.definition.name === name);
	if (!found) throw new Error(`Missing ${name} tool.`);
	return found;
}

async function call(toolDefinition: GameTool, args: JsonObject, currentInput = input) {
	return toolDefinition.execute(
		{ id: `call-${toolDefinition.definition.name}`, name: toolDefinition.definition.name, arguments: args },
		context(currentInput),
	);
}

async function recordReflection(resources: GameBehaviorEvolutionResources, currentInput = input): Promise<string> {
	const tools = await resources.toolProvider.provide(currentInput, new AbortController().signal);
	const result = await call(
		tool(resources, tools, "record_game_reflection"),
		{
			outcome: "success",
			summary: "A stable resource-first build sequence succeeded.",
			observations: ["Foundation was clear."],
			patterns: ["Gather before building."],
			failures: [],
			evidence: { receipt: "safe-summary" },
		},
		currentInput,
	);
	return (result.details as { reflectionId: string }).reflectionId;
}

function proposal(reflectionId: string, overrides: JsonObject = {}): JsonObject {
	return {
		id: "build-shelter",
		name: "Build shelter",
		description: "Gather materials, build a shelter, then verify it.",
		instructions: "Use the ordered steps and stop when an authoritative tool reports failure.",
		scope: "actor",
		inputTypes: ["npc.command"],
		steps: [
			{ id: "gather", tool: "gather_resource", instruction: "Gather the required materials." },
			{ id: "build", tool: "build_structure", instruction: "Build and verify the shelter." },
		],
		reflectionId,
		...overrides,
	};
}

function resources(
	store: SqliteGameBehaviorStore,
	mode: "off" | "conservative" | "aggressive",
	validator: GameBehaviorSkillValidator = { validate: () => ({ accepted: true, evidence: { checked: true } }) },
) {
	return createGameBehaviorEvolution({ store, mode, validator, availableTools: () => gameTools });
}

describe("game behavior evolution", () => {
	it("builds a bounded review input from model-visible outcome evidence without copying the original prompt", () => {
		const review = createGameBehaviorReviewInput({
			id: "review-1",
			source: { ...input, content: [{ type: "text", text: "private original prompt" }] },
			outcome: "success",
			visibleSummary: "The shelter was committed by the host.",
			visibleEvidence: { receiptStatus: "committed" },
		});
		expect(review).toMatchObject({ id: "review-1", type: "agent.reflection", session: input.session });
		expect(JSON.stringify(review)).not.toContain("private original prompt");
	});

	it("is an optional extension and exposes no learning tools while disabled", async () => {
		using store = new SqliteGameBehaviorStore(await createStorePath());
		const disabled = resources(store, "off");
		expect(await disabled.toolProvider.provide(input, new AbortController().signal)).toEqual([]);
		expect(await disabled.skillSource.listForInput(input)).toEqual([]);
	});

	it("records visible structured reflection, validates a composite skill, and requires activation in conservative mode", async () => {
		using store = new SqliteGameBehaviorStore(await createStorePath());
		const extension = resources(store, "conservative");
		const reflectionId = await recordReflection(extension);
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		const result = await call(tool(extension, tools, "propose_game_behavior_skill"), proposal(reflectionId));
		expect(result).toMatchObject({ details: { status: "validated", active: false, version: 1 } });
		expect(result.isError).toBeUndefined();
		expect(await extension.skillSource.listForInput({ ...input, type: "npc.command" })).toEqual([]);

		await extension.controller.activate(input.session, "actor", "build-shelter", 1);
		const active = await extension.skillSource.listForInput({ ...input, type: "npc.command" });
		expect(active).toHaveLength(1);
		expect(active[0]).toMatchObject({
			id: "build-shelter",
			requiredTools: ["build_structure", "gather_resource"],
			version: "1",
		});
		expect(active[0]?.instructions).toContain("[gather_resource]");

		const otherActor = { ...input, session: { ...input.session, actorId: "actor-b" } };
		expect(await extension.skillSource.listForInput(otherActor)).toEqual([]);
	});

	it("replays identical reflection and skill mutations idempotently while rejecting changed content for one input", async () => {
		using store = new SqliteGameBehaviorStore(await createStorePath());
		const extension = resources(store, "conservative");
		const firstReflection = await recordReflection(extension);
		const replayedReflection = await recordReflection(extension);
		expect(replayedReflection).toBe(firstReflection);
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		const proposalTool = tool(extension, tools, "propose_game_behavior_skill");
		const first = await call(proposalTool, proposal(firstReflection));
		const replay = await call(proposalTool, proposal(firstReflection));
		expect(replay.details).toEqual(first.details);
		expect(store.listVersions(input.session, "actor", "build-shelter")).toHaveLength(1);
		await expect(
			call(proposalTool, proposal(firstReflection, { instructions: "Different instructions for the same input." })),
		).rejects.toThrow("different skill content");
	});

	it("activates validated skills in aggressive mode without bypassing tool availability or validation", async () => {
		using store = new SqliteGameBehaviorStore(await createStorePath());
		const validator = { validate: vi.fn(() => ({ accepted: true as const })) };
		const extension = resources(store, "aggressive", validator);
		const reflectionId = await recordReflection(extension);
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		await call(tool(extension, tools, "propose_game_behavior_skill"), proposal(reflectionId));
		expect(validator.validate).toHaveBeenCalledOnce();
		expect(await extension.skillSource.listForInput(input)).toHaveLength(1);

		await expect(
			call(
				tool(extension, tools, "propose_game_behavior_skill"),
				proposal(reflectionId, {
					id: "unsafe-skill",
					steps: [{ id: "escape", tool: "unavailable_tool", instruction: "Bypass the host." }],
				}),
			),
		).rejects.toThrow("unavailable tool");
		expect(validator.validate).toHaveBeenCalledOnce();
	});

	it("stores validation rejection without exposing the rejected behavior as a skill", async () => {
		using store = new SqliteGameBehaviorStore(await createStorePath());
		const extension = resources(store, "aggressive", {
			validate: () => ({ accepted: false, reason: "Evidence did not prove the sequence." }),
		});
		const reflectionId = await recordReflection(extension);
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		const result = await call(tool(extension, tools, "propose_game_behavior_skill"), proposal(reflectionId));
		expect(result).toMatchObject({ isError: true, details: { status: "rejected", active: false } });
		expect(await extension.skillSource.listForInput(input)).toEqual([]);
		expect(store.listVersions(input.session, "actor", "build-shelter")[0]).toMatchObject({
			status: "rejected",
			rejectionReason: "Evidence did not prove the sequence.",
		});
	});

	it("versions and rolls back behavior skills across restart", async () => {
		const path = await createStorePath();
		let store = new SqliteGameBehaviorStore(path);
		let extension = resources(store, "aggressive");
		const firstReflection = await recordReflection(extension);
		let tools = await extension.toolProvider.provide(input, new AbortController().signal);
		await call(tool(extension, tools, "propose_game_behavior_skill"), proposal(firstReflection));

		const secondInput = { ...input, id: "reflection-input-2", moment: { tick: 200 } };
		const secondReflection = await recordReflection(extension, secondInput);
		tools = await extension.toolProvider.provide(secondInput, new AbortController().signal);
		await call(
			tool(extension, tools, "propose_game_behavior_skill"),
			proposal(secondReflection, { instructions: "Use the improved ordered sequence and verify every receipt." }),
			secondInput,
		);
		expect(
			store.listVersions(input.session, "actor", "build-shelter").map((version) => [version.version, version.active]),
		).toEqual([
			[2, true],
			[1, false],
		]);
		await extension.controller.rollback(input.session, "actor", "build-shelter", 1);
		store[Symbol.dispose]();

		store = new SqliteGameBehaviorStore(path);
		extension = resources(store, "aggressive");
		expect((await extension.skillSource.listForInput(input))[0]).toMatchObject({ version: "1" });
		store[Symbol.dispose]();
	});

	it("shares only explicitly world-scoped skills and isolates save generations", async () => {
		using store = new SqliteGameBehaviorStore(await createStorePath());
		const extension = resources(store, "aggressive");
		const reflectionId = await recordReflection(extension);
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		await call(
			tool(extension, tools, "propose_game_behavior_skill"),
			proposal(reflectionId, { id: "world-build", scope: "world" }),
		);
		const peer = { ...input, session: { ...input.session, sessionId: "peer-session", actorId: "actor-b" } };
		expect(await extension.skillSource.listForInput(peer)).toEqual([]);
		await extension.controller.activate(input.session, "world", "world-build", 1);
		expect(await extension.skillSource.listForInput(peer)).toHaveLength(1);
		const loadedSave = { ...peer, session: { ...peer.session, generation: 2 } };
		expect(await extension.skillSource.listForInput(loadedSave)).toEqual([]);
	});

	it("integrates input-scoped learned skills with progressive skill disclosure", async () => {
		using store = new SqliteGameBehaviorStore(await createStorePath());
		const evolution = resources(store, "aggressive");
		const reflectionId = await recordReflection(evolution);
		const tools = await evolution.toolProvider.provide(input, new AbortController().signal);
		await call(tool(evolution, tools, "propose_game_behavior_skill"), proposal(reflectionId));

		const staticSource = new InMemoryGameSkillSource([
			{
				id: "static-skill",
				name: "Static skill",
				description: "A developer-authored skill.",
				instructions: "Use the build tool safely.",
				inputTypes: ["npc.command"],
				requiredTools: ["build_structure"],
				priority: 0,
				version: "1",
				digest: "static-digest",
				disableModelInvocation: false,
			},
		]);
		const combined = new CompositeGameSkillSource([staticSource, evolution.skillSource]);
		const skillExtension = createGameSkillExtension({ source: combined });
		const commandInput = { ...input, type: "npc.command" };
		const contextSegment = await skillExtension.postToolContextProvider.provide(
			commandInput,
			gameTools,
			new AbortController().signal,
		);
		expect(contextSegment?.value).toMatchObject({
			skills: expect.arrayContaining([
				expect.objectContaining({ id: "build-shelter", version: "1" }),
				expect.objectContaining({ id: "static-skill", version: "1" }),
			]),
		});

		const noTools = await skillExtension.postToolContextProvider.provide(
			commandInput,
			[],
			new AbortController().signal,
		);
		expect(noTools).toBeUndefined();

		expect((await combined.listForInput(commandInput)).map((skill) => skill.id).sort()).toEqual([
			"build-shelter",
			"static-skill",
		]);
	});

	it("provides a bounded reflection workflow without exposing hidden-reasoning fields", async () => {
		using store = new SqliteGameBehaviorStore(await createStorePath());
		const extension = resources(store, "conservative");
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		for (const item of tools) expect(() => preflightGameToolSchema(item.definition)).not.toThrow();
		const segment = await extension.postToolContextProvider.provide(
			input,
			tools.map((item) => item.definition),
			new AbortController().signal,
		);
		expect(segment?.value).toMatchObject({ mode: "conservative" });
		const serialized = JSON.stringify({ tools: tools.map((item) => item.definition), segment });
		expect(serialized).not.toContain("chainOfThought");
		expect(serialized).not.toContain("reasoning_content");
	});
});

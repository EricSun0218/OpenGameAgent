import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameInput, GameToolExecutionContext } from "@opengameagent/protocol";
import { preflightGameToolSchema } from "@opengameagent/runtime";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteGameMemoryStore } from "./memory.js";
import { createGameMemoryExtension } from "./runtime-extension.js";

const directories: string[] = [];

const input: GameInput = {
	id: "input-1",
	type: "npc.chat",
	session: {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 1,
		ownerId: "owner",
		sessionId: "session",
		actorId: "actor",
	},
	moment: { tick: 42.5 },
	content: [{ type: "json", value: { subject: "orchard" } }],
};

function executionContext(): GameToolExecutionContext {
	return { input, runId: "run-1", turn: 1, toolCallIndex: 0, signal: new AbortController().signal };
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

async function store(): Promise<SqliteGameMemoryStore> {
	const directory = await mkdtemp(join(tmpdir(), "oga-memory-extension-"));
	directories.push(directory);
	return new SqliteGameMemoryStore(join(directory, "memory.sqlite"));
}

describe("GameMemoryExtension", () => {
	it("provides schema-valid optional tools with idempotent actor-scoped writes", async () => {
		using memoryStore = await store();
		const extension = createGameMemoryExtension({ store: memoryStore });
		const tools = await extension.toolProvider.provide(input, new AbortController().signal);
		expect(tools.map((tool) => tool.definition.name)).toEqual(["remember_game_memory", "search_game_memory"]);
		for (const tool of tools) expect(() => preflightGameToolSchema(tool.definition)).not.toThrow();

		const remember = tools.find((tool) => tool.definition.name === "remember_game_memory");
		expect(remember).toBeDefined();
		const call = {
			id: "call-1",
			name: "remember_game_memory",
			arguments: {
				scope: "actor",
				kind: "observation",
				content: { event: "found-orchard" },
				searchText: "found an apple orchard",
				tags: ["food"],
				importance: 0.8,
			},
		};
		const first = await remember?.execute(call, executionContext());
		const second = await remember?.execute(call, executionContext());
		expect(first).toEqual(second);
		expect(
			(await memoryStore.search({ session: input.session, text: "apple orchard", limit: 8 })).matches,
		).toHaveLength(1);

		await expect(
			remember?.execute(
				{ ...call, id: "owner-call", arguments: { ...call.arguments, scope: "owner" } },
				executionContext(),
			),
		).rejects.toThrow(/not authorized/);
	});

	it("injects bounded recall using the canonical input session rather than a query-supplied identity", async () => {
		using memoryStore = await store();
		await memoryStore.put({
			id: "visible",
			session: input.session,
			scope: "actor",
			kind: "observation",
			content: { fact: "apple orchard" },
			searchText: "apple orchard",
			importance: 0.7,
			moment: input.moment,
			createdAt: 1,
		});
		const extension = createGameMemoryExtension({
			store: memoryStore,
			buildRecallQuery: () => ({
				session: { ...input.session, actorId: "malicious-other" },
				text: "apple",
				limit: 4,
			}),
		});
		const segment = await extension.contextProvider?.provide(input, new AbortController().signal);
		expect(JSON.stringify(segment?.value)).toContain("visible");
		expect(JSON.stringify(segment?.value)).not.toContain("malicious-other");
	});
});

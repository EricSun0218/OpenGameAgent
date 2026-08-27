import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameInput, GameToolExecutionContext } from "@opengameagent/protocol";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SqliteGameArtifactStore } from "./artifacts.js";
import { createExternalKnowledgeToolProvider, JsonHttpGameKnowledgeSource } from "./external-knowledge.js";

const directories: string[] = [];

const input: GameInput = {
	id: "input-knowledge",
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
	moment: { tick: 9 },
	content: [{ type: "text", text: "private player input" }],
	context: { private: "not sent by default" },
};

function context(): GameToolExecutionContext {
	return {
		input,
		runId: "run",
		turn: 1,
		toolCallIndex: 0,
		signal: new AbortController().signal,
	};
}

function contextForActor(actorId: string): GameToolExecutionContext {
	return {
		...context(),
		input: {
			...input,
			session: { ...input.session, actorId },
		},
	};
}

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-knowledge-"));
	directories.push(directory);
	return join(directory, "knowledge.db");
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("external game knowledge", () => {
	it("queries only host-registered sources and stores oversized results as artifacts", async () => {
		using artifacts = new SqliteGameArtifactStore(await databasePath());
		const source = {
			id: "world-book",
			query: vi
				.fn()
				.mockResolvedValue([{ id: "entry", title: "Entry", payload: { text: "x".repeat(2000) }, summary: "summary" }]),
		};
		const provider = createExternalKnowledgeToolProvider({
			sources: [source],
			artifactStore: artifacts,
			maximumInlineCharacters: 1024,
		});
		const [tool] = await provider.provide(input, new AbortController().signal);
		expect(tool?.definition.parameters).toMatchObject({
			properties: { source: { enum: ["world-book"] } },
		});
		const result = await tool?.execute(
			{
				id: "call",
				name: "query_external_knowledge",
				arguments: { source: "world-book", query: { tag: "lore" }, limit: 2 },
			},
			context(),
		);
		expect(source.query).toHaveBeenCalledWith({ input, query: { tag: "lore" }, limit: 2 }, expect.any(AbortSignal));
		const value = result?.content[0];
		if (value?.type !== "json" || value.value === null || Array.isArray(value.value) || typeof value.value !== "object")
			throw new Error("Expected knowledge artifact reference.");
		const id = value.value["artifactId"];
		if (typeof id !== "string") throw new Error("Expected artifact id.");
		expect(await artifacts.read(input.session, id, 0, 64)).toMatchObject({
			id,
			mediaType: "application/vnd.opengameagent.knowledge+json",
			truncated: true,
		});
		expect(
			await tool?.execute(
				{ id: "cross-actor", name: "query_external_knowledge", arguments: { source: "world-book", query: null } },
				contextForActor("other-actor"),
			),
		).toMatchObject({ isError: true });
		expect(source.query).toHaveBeenCalledTimes(1);
	});

	it("uses a fixed safe HTTP endpoint without leaking the input payload", async () => {
		let requestBody = "";
		const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async (_url, init) => {
			requestBody = String(init?.body ?? "");
			return new Response(JSON.stringify({ items: [{ id: "one", title: "One", payload: { answer: 1 } }] }), {
				status: 200,
				headers: { "content-type": "application/json" },
			});
		});
		const source = new JsonHttpGameKnowledgeSource({
			id: "local-index",
			endpoint: "http://127.0.0.1:7777/query",
			fetch,
		});
		expect(await source.query({ input, query: { q: "one" }, limit: 1 }, new AbortController().signal)).toEqual([
			{ id: "one", title: "One", payload: { answer: 1 } },
		]);
		expect(requestBody).not.toContain("private player input");
		expect(requestBody).not.toContain("not sent by default");
		expect(fetch).toHaveBeenCalledWith(
			new URL("http://127.0.0.1:7777/query"),
			expect.objectContaining({ method: "POST", redirect: "error" }),
		);
		expect(() => new JsonHttpGameKnowledgeSource({ id: "bad", endpoint: "http://example.com/query" })).toThrow("HTTPS");
		expect(
			() => new JsonHttpGameKnowledgeSource({ id: "bad", endpoint: "https://user:secret@example.com/query" }),
		).toThrow("forbidden");
	});

	it("fails closed for oversized, duplicate, and malformed source results", async () => {
		const duplicate = createExternalKnowledgeToolProvider({
			sources: [
				{
					id: "bad",
					query: async () => [
						{ id: "same", title: "One", payload: null },
						{ id: "same", title: "Two", payload: null },
					],
				},
			],
		});
		const [tool] = await duplicate.provide(input, new AbortController().signal);
		await expect(
			tool?.execute(
				{ id: "call", name: "query_external_knowledge", arguments: { source: "bad", query: null } },
				context(),
			),
		).rejects.toThrow("duplicate");

		const fetch = vi
			.fn<typeof globalThis.fetch>()
			.mockResolvedValue(new Response("not-json", { status: 200, headers: { "content-type": "application/json" } }));
		const source = new JsonHttpGameKnowledgeSource({ id: "bad-json", endpoint: "https://example.com/query", fetch });
		await expect(source.query({ input, query: null, limit: 1 }, new AbortController().signal)).rejects.toThrow(
			"invalid JSON",
		);
	});
});

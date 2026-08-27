import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameInput, GameSessionKey, GameToolExecutionContext, GameToolResult } from "@opengameagent/protocol";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createGameArtifactResources, type GameArtifact, SqliteGameArtifactStore } from "./artifacts.js";

const directories: string[] = [];

function session(actorId = "actor-a"): GameSessionKey {
	return {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 1,
		ownerId: "owner",
		sessionId: "session",
		actorId,
	};
}

function input(actorId = "actor-a"): GameInput {
	return {
		id: "input-1",
		type: "npc.chat",
		session: session(actorId),
		moment: { tick: 10 },
		content: [{ type: "text", text: "hello" }],
	};
}

function context(actorId = "actor-a"): GameToolExecutionContext {
	return {
		input: input(actorId),
		runId: "run-1",
		turn: 1,
		toolCallIndex: 0,
		signal: new AbortController().signal,
	};
}

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-artifacts-"));
	directories.push(directory);
	return join(directory, "artifacts.db");
}

function artifact(content = "0123456789"): GameArtifact {
	return {
		id: "artifact-1",
		session: session(),
		mediaType: "application/json",
		content,
		moment: { tick: 10 },
		metadata: { source: "test" },
		createdAt: 1000,
	};
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("game artifacts", () => {
	it("persists bounded pages with exact session isolation and idempotent writes", async () => {
		const path = await databasePath();
		{
			using store = new SqliteGameArtifactStore(path);
			expect(await store.put(artifact())).toBe(true);
			expect(await store.put(artifact())).toBe(false);
			await expect(store.put(artifact("different"))).rejects.toThrow("different content");
			expect(await store.read(session(), "artifact-1", 3, 4)).toEqual({
				id: "artifact-1",
				mediaType: "application/json",
				offset: 3,
				content: "3456",
				totalCharacters: 10,
				truncated: true,
				metadata: { source: "test" },
			});
			expect(await store.read(session("actor-b"), "artifact-1", 0, 10)).toBeUndefined();
		}
		using reopened = new SqliteGameArtifactStore(path);
		expect(await reopened.read(session(), "artifact-1", 8, 10)).toMatchObject({ content: "89", truncated: false });
	});

	it("offloads large tool results and pages them through the read tool", async () => {
		using store = new SqliteGameArtifactStore(await databasePath());
		const resources = createGameArtifactResources({
			store,
			maximumInlineCharacters: 1024,
			maximumPreviewCharacters: 16,
			maximumReadCharacters: 1024,
		});
		const original: GameToolResult = {
			content: [{ type: "text", text: "x".repeat(2000) }],
			details: { host: "kept" },
		};
		const next = vi.fn<() => Promise<GameToolResult>>().mockResolvedValue(original);
		const projected = await resources.execution.execute(
			{
				name: "inspect_world",
				label: "Inspect world",
				description: "Inspect",
				parameters: { type: "object" },
			},
			{ id: "call", name: "inspect_world", arguments: {} },
			context(),
			next,
		);
		expect(projected.details).toEqual({ host: "kept" });
		const metadata = projected.content[0];
		expect(metadata).toMatchObject({ type: "json", value: { truncated: true, readTool: "read_agent_artifact" } });
		if (
			metadata?.type !== "json" ||
			metadata.value === null ||
			Array.isArray(metadata.value) ||
			typeof metadata.value !== "object"
		)
			throw new Error("Expected artifact metadata.");
		const id = metadata.value["artifactId"];
		if (typeof id !== "string") throw new Error("Expected artifact id.");
		const [readTool] = await resources.toolProvider.provide(input(), new AbortController().signal);
		const read = await readTool?.execute(
			{ id: "read", name: "read_agent_artifact", arguments: { id, offset: 0, maximumCharacters: 64 } },
			context(),
		);
		expect(read?.content[0]).toMatchObject({ type: "json", value: { id, offset: 0, truncated: true } });
		const crossActor = await readTool?.execute(
			{ id: "read", name: "read_agent_artifact", arguments: { id } },
			context("actor-b"),
		);
		expect(crossActor).toMatchObject({ isError: true });
		const [actorBReadTool] = await resources.toolProvider.provide(input("actor-b"), new AbortController().signal);
		expect(
			await actorBReadTool?.execute({ id: "read", name: "read_agent_artifact", arguments: { id } }, context("actor-b")),
		).toMatchObject({ isError: true });
	});

	it("does not replace a completed tool result when optional artifact storage fails", async () => {
		const original: GameToolResult = { content: [{ type: "text", text: "x".repeat(2000) }] };
		const resources = createGameArtifactResources({
			store: {
				put: async () => {
					throw new Error("private storage detail");
				},
				read: async () => undefined,
			},
			maximumInlineCharacters: 1024,
		});
		expect(
			await resources.execution.execute(
				{ name: "write_world", label: "Write", description: "Write", parameters: { type: "object" } },
				{ id: "call", name: "write_world", arguments: {} },
				context(),
				async () => original,
			),
		).toBe(original);
	});
});

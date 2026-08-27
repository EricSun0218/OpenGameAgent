import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import type { GameConversationMessage, GameSessionKey } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteGameConversationStore } from "./sqlite-game-conversation-store.js";

const temporaryDirectories: string[] = [];

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-conversation-"));
	temporaryDirectories.push(directory);
	return join(directory, "runtime.db");
}

const session: GameSessionKey = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 2,
	ownerId: "owner",
	sessionId: "session",
	actorId: "actor",
};

const messages: GameConversationMessage[] = [
	{ role: "user", content: "hello", timestamp: 1 },
	{
		role: "assistant",
		content: [
			{ type: "reasoning", text: "private", signature: "opaque", redacted: true },
			{ type: "text", text: "hi", signature: "response-item" },
		],
		api: "responses",
		provider: "provider",
		model: "model",
		usage: { input: 2, output: 3, cacheRead: 0, cacheWrite: 0, totalTokens: 5 },
		stopReason: "stop",
		timestamp: 2,
	},
];

afterEach(async () => {
	for (const directory of temporaryDirectories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("SqliteGameConversationStore", () => {
	it("round-trips versioned provider continuity state across restart", async () => {
		const path = await databasePath();
		const first = new SqliteGameConversationStore(path);
		expect(await first.read(session)).toEqual({ revision: 0, messages: [] });
		expect(await first.save(session, 0, messages)).toEqual({ revision: 1, messages });
		first.close();

		const reopened = new SqliteGameConversationStore(path);
		expect(await reopened.read(session)).toEqual({ revision: 1, messages });
		await expect(reopened.save(session, 0, messages)).rejects.toThrow(/revision conflict/);
		expect(
			(await reopened.save(session, 1, [...messages, { role: "user", content: "again", timestamp: 3 }])).revision,
		).toBe(2);
		reopened.close();
	});

	it("isolates actors and generations", async () => {
		const path = await databasePath();
		const store = new SqliteGameConversationStore(path);
		await store.save(session, 0, messages);
		expect(await store.read({ ...session, actorId: "other" })).toEqual({ revision: 0, messages: [] });
		expect(await store.read({ ...session, generation: 3 })).toEqual({ revision: 0, messages: [] });
		store.close();
	});

	it("fails closed on corrupted transcripts", async () => {
		const path = await databasePath();
		const store = new SqliteGameConversationStore(path);
		await store.save(session, 0, messages);
		store.close();

		const database = new DatabaseSync(path);
		database.prepare("UPDATE game_conversations SET messages_json = ?").run("not-json");
		database.close();
		const reopened = new SqliteGameConversationStore(path);
		await expect(reopened.read(session)).rejects.toThrow(/corrupt/);
		reopened.close();
	});
});

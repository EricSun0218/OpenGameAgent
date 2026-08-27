import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import type { GameAgentEvent, GameInput, GameSessionKey } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteGameRuntimeEventStore } from "./sqlite-game-runtime-event-store.js";

const temporaryDirectories: string[] = [];

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-events-"));
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

const input: GameInput = {
	id: "input",
	type: "npc.chat",
	session,
	moment: { tick: 1 },
	content: [{ type: "text", text: "hello" }],
};

type MessageDeltaEvent = Extract<GameAgentEvent, { type: "message.delta" }>;

const event = (sequence: number): MessageDeltaEvent => ({
	type: "message.delta",
	sequence,
	eventId: `run:${sequence}`,
	runId: "run",
	turn: 1,
	audience: { visibility: "owner" },
	timestamp: sequence,
	text: `part-${sequence}`,
});

afterEach(async () => {
	for (const directory of temporaryDirectories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("SqliteGameRuntimeEventStore", () => {
	it("persists ordered events and resumes strictly after a cursor across restart", async () => {
		const path = await databasePath();
		const first = new SqliteGameRuntimeEventStore(path);
		const signal = new AbortController().signal;
		await first.append(input, event(1), signal);
		await first.append(input, event(2), signal);
		await first.append(input, event(2), signal);
		first.close();

		const reopened = new SqliteGameRuntimeEventStore(path);
		expect(await reopened.read(session, "run", 1, 10)).toEqual([event(2)]);
		expect(await reopened.read({ ...session, actorId: "other" }, "run", 0, 10)).toEqual([]);
		reopened.close();
	});

	it("rejects conflicting replay and corrupt persisted events", async () => {
		const path = await databasePath();
		const store = new SqliteGameRuntimeEventStore(path);
		const signal = new AbortController().signal;
		await store.append(input, event(1), signal);
		await expect(store.append(input, { ...event(1), text: "different" }, signal)).rejects.toThrow(
			/different event content/,
		);
		store.close();

		const database = new DatabaseSync(path);
		database.prepare("UPDATE game_runtime_events SET event_json = ?").run("broken");
		database.close();
		const reopened = new SqliteGameRuntimeEventStore(path);
		await expect(reopened.read(session, "run", 0, 10)).rejects.toThrow(/corrupt/);
		reopened.close();
	});
});

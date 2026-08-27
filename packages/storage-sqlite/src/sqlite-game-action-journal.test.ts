import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { createGameActionOperationId } from "@opengameagent/actions";
import type { GameActionIntent, GameActionReceipt, GameSessionKey } from "@opengameagent/protocol";
import { afterEach, describe, expect, it } from "vitest";
import { SqliteGameActionJournal } from "./sqlite-game-action-journal.js";

const temporaryDirectories: string[] = [];

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-sqlite-"));
	temporaryDirectories.push(directory);
	return join(directory, "runtime.db");
}

const session = (actorId: string, generation = 1): GameSessionKey => ({
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation,
	ownerId: "owner",
	sessionId: `session-${actorId}`,
	actorId,
});

function intent(actorId: string, inputId: string, generation = 1): GameActionIntent {
	const identity = {
		session: session(actorId, generation),
		inputId,
		runId: `run-${inputId}`,
		turn: 1,
		toolCallIndex: 0,
		action: "set_state",
	};
	return {
		...identity,
		operationId: createGameActionOperationId(identity),
		args: { key: "door", open: true },
		moment: { tick: 30 },
		expectedRevision: 9,
		conflictKey: "door",
	};
}

function receipt(action: GameActionIntent, status: GameActionReceipt["status"] = "committed"): GameActionReceipt {
	return {
		operationId: action.operationId,
		session: action.session,
		action: action.action,
		expectedRevision: action.expectedRevision,
		stateRevision: 10,
		status,
		result: { open: status === "committed" },
	};
}

afterEach(async () => {
	for (const directory of temporaryDirectories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("SqliteGameActionJournal", () => {
	it("survives restart and requires reconciliation instead of repeating a dispatched action", async () => {
		const path = await databasePath();
		const action = intent("a", "one");
		const first = new SqliteGameActionJournal(path);
		await first.prepare(action);
		expect(await first.claimDispatch(action.operationId)).toMatchObject({ kind: "dispatch" });
		first.close();

		const reopened = new SqliteGameActionJournal(path);
		expect(await reopened.claimDispatch(action.operationId)).toMatchObject({
			kind: "reconcile",
			entry: { attempt: 1 },
		});
		await reopened.markUncertain(action.operationId);
		await reopened.submitReceipt(receipt(action));
		expect(await reopened.read(action.operationId)).toMatchObject({ status: "committed", attempt: 1 });
		reopened.close();
	});

	it("keeps unresolved conflict barriers across restart and isolates generations", async () => {
		const path = await databasePath();
		const firstAction = intent("a", "first");
		const secondAction = intent("b", "second");
		const newGeneration = intent("b", "new", 2);
		const first = new SqliteGameActionJournal(path);
		await first.prepare(firstAction);
		await first.claimDispatch(firstAction.operationId);
		await first.markUncertain(firstAction.operationId);
		first.close();

		const reopened = new SqliteGameActionJournal(path);
		await reopened.prepare(secondAction);
		await reopened.prepare(newGeneration);
		expect(await reopened.claimDispatch(secondAction.operationId)).toMatchObject({
			kind: "blocked",
			blockingOperationId: firstAction.operationId,
		});
		expect(await reopened.claimDispatch(newGeneration.operationId)).toMatchObject({ kind: "dispatch" });
		await reopened.submitReceipt(receipt(firstAction, "failed"));
		expect(await reopened.claimDispatch(secondAction.operationId)).toMatchObject({ kind: "dispatch" });
		reopened.close();
	});

	it("validates receipt identity and fails closed on corrupt records", async () => {
		const path = await databasePath();
		const action = intent("a", "one");
		const journal = new SqliteGameActionJournal(path);
		await journal.prepare(action);
		await journal.claimDispatch(action.operationId);
		await expect(
			journal.submitReceipt({ ...receipt(action), session: { ...action.session, actorId: "attacker" } }),
		).rejects.toThrow(/session/);
		journal.close();

		const database = new DatabaseSync(path);
		database
			.prepare("UPDATE game_action_journal SET intent_json = ? WHERE operation_id = ?")
			.run("{broken", action.operationId);
		database.close();
		const reopened = new SqliteGameActionJournal(path);
		await expect(reopened.read(action.operationId)).rejects.toThrow(/corrupt/);
		reopened.close();
	});
});

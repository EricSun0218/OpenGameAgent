import type { GameActionIntent, GameActionReceipt, GameSessionKey } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { DurableGameActionDispatcher } from "./dispatcher.js";
import { InMemoryGameActionJournal } from "./journal.js";
import { createGameActionOperationId } from "./operation-id.js";

const session = (actorId: string, generation = 1): GameSessionKey => ({
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation,
	ownerId: "owner",
	sessionId: `session-${actorId}`,
	actorId,
});

function intent(actorId: string, inputId: string, conflictKey?: string, generation = 1): GameActionIntent {
	const identity = {
		session: session(actorId, generation),
		inputId,
		runId: `run-${inputId}`,
		turn: 1,
		toolCallIndex: 0,
		action: "place_block",
	};
	return {
		...identity,
		operationId: createGameActionOperationId(identity),
		args: { x: 4.5, y: 2, block: "stone" },
		moment: { tick: 20 },
		expectedRevision: 7,
		...(conflictKey === undefined ? {} : { conflictKey }),
	};
}

function receipt(action: GameActionIntent, status: GameActionReceipt["status"] = "committed"): GameActionReceipt {
	return {
		operationId: action.operationId,
		session: action.session,
		action: action.action,
		expectedRevision: action.expectedRevision,
		stateRevision: 8,
		status,
		result: { placed: status === "committed" },
	};
}

describe("reliable game actions", () => {
	it("creates stable versioned operation IDs without actor or session collisions", () => {
		const first = intent("a", "input");
		expect(first.operationId).toMatch(/^oga2_[A-Za-z0-9_-]{43}$/);
		expect(intent("a", "input").operationId).toBe(first.operationId);
		expect(intent("b", "input").operationId).not.toBe(first.operationId);
		expect(intent("a", "other").operationId).not.toBe(first.operationId);
	});

	it("persists dispatched before host execution and deduplicates completed replay", async () => {
		const journal = new InMemoryGameActionJournal();
		const action = intent("a", "input");
		let calls = 0;
		const dispatcher = new DurableGameActionDispatcher(journal, {
			async execute(value) {
				calls += 1;
				expect((await journal.read(value.operationId))?.status).toBe("dispatched");
				return receipt(value);
			},
		});

		expect((await dispatcher.dispatch(action)).kind).toBe("terminal");
		expect((await dispatcher.dispatch(action)).kind).toBe("terminal");
		expect(calls).toBe(1);
	});

	it("turns a lost receipt into reconcile-only state and never blindly re-executes", async () => {
		const journal = new InMemoryGameActionJournal();
		const action = intent("a", "input");
		let calls = 0;
		const dispatcher = new DurableGameActionDispatcher(journal, {
			async execute() {
				calls += 1;
				throw new Error("transport closed after delivery");
			},
		});

		await expect(dispatcher.dispatch(action)).rejects.toThrow(/transport closed/);
		expect((await journal.read(action.operationId))?.status).toBe("uncertain");
		expect((await dispatcher.dispatch(action)).kind).toBe("reconcile");
		expect(calls).toBe(1);
		await dispatcher.reconcile(receipt(action));
		expect((await journal.read(action.operationId))?.status).toBe("committed");
	});

	it("blocks same-key cross-actor actions while uncertain but isolates new generations", async () => {
		const journal = new InMemoryGameActionJournal();
		const first = intent("a", "first", "shared-resource");
		const second = intent("b", "second", "shared-resource");
		const nextGeneration = intent("b", "next", "shared-resource", 2);
		await journal.prepare(first);
		await journal.claimDispatch(first.operationId);
		await journal.markUncertain(first.operationId);
		await journal.prepare(second);
		await journal.prepare(nextGeneration);

		expect(await journal.claimDispatch(second.operationId)).toMatchObject({
			kind: "blocked",
			blockingOperationId: first.operationId,
		});
		expect(await journal.claimDispatch(nextGeneration.operationId)).toMatchObject({ kind: "dispatch" });
		await journal.submitReceipt(receipt(first, "rejected"));
		expect(await journal.claimDispatch(second.operationId)).toMatchObject({ kind: "dispatch" });
	});
});

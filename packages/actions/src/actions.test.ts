import type {
	GameActionIntent,
	GameActionReceipt,
	GameInput,
	GameSessionKey,
	GameToolDefinition,
	GameToolExecutionContext,
} from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { createGameActionTool } from "./action-tool.js";
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

const gameInput = (actorId = "a"): GameInput => ({
	id: "input",
	type: "npc.command",
	session: session(actorId),
	moment: { tick: 20 },
	content: [{ type: "text", text: "Place a block." }],
});

const executionContext = (input = gameInput()): GameToolExecutionContext => ({
	input,
	runId: "run-input",
	turn: 1,
	toolCallIndex: 0,
	signal: new AbortController().signal,
});

const actionDefinition: GameToolDefinition = {
	name: "place_block",
	label: "Place block",
	description: "Places one block through the authoritative game host.",
	parameters: {
		type: "object",
		properties: { x: { type: "number" }, block: { type: "string" } },
		required: ["x", "block"],
		additionalProperties: false,
	},
};

describe("reliable game actions", () => {
	it("exposes a bounded semantic receipt while retaining canonical host details", async () => {
		const journal = new InMemoryGameActionJournal();
		let executed: GameActionIntent | undefined;
		const tool = createGameActionTool({
			definition: actionDefinition,
			dispatcher: new DurableGameActionDispatcher(journal, {
				async execute(value) {
					executed = value;
					return receipt(value);
				},
			}),
			expectedRevision: 7,
			conflictKey: () => "tile:4:2",
		});

		const result = await tool.execute(
			{ id: "call", name: "place_block", arguments: { x: 4.5, block: "stone" } },
			executionContext(),
		);

		expect(result).toMatchObject({
			content: [{ type: "json", value: { action: "place_block", status: "committed", result: { placed: true } } }],
			isError: false,
		});
		expect(JSON.stringify(result.content)).not.toMatch(/operationId|stateRevision|timelineId|expectedRevision/);
		expect(result.details).toMatchObject({
			kind: "terminal",
			entry: {
				intent: { operationId: executed?.operationId, conflictKey: "tile:4:2", expectedRevision: 7 },
				receipt: { stateRevision: 8 },
			},
		});
	});

	it("deduplicates an identical model tool replay with the stable operation identity", async () => {
		const journal = new InMemoryGameActionJournal();
		let calls = 0;
		const tool = createGameActionTool({
			definition: actionDefinition,
			dispatcher: new DurableGameActionDispatcher(journal, {
				async execute(value) {
					calls += 1;
					return receipt(value);
				},
			}),
			expectedRevision: 7,
		});
		const call = { id: "call", name: "place_block", arguments: { x: 4.5, block: "stone" } } as const;
		await tool.execute(call, executionContext());
		await tool.execute(call, executionContext());
		expect(calls).toBe(1);
	});

	it("fails closed when a host receipt projection throws, is invalid, or exceeds its bound", async () => {
		for (const projectReceipt of [
			() => {
				throw new Error("private projection failure");
			},
			() => ["not-an-object"] as never,
			() => ({ summary: "x".repeat(65) }),
		]) {
			const journal = new InMemoryGameActionJournal();
			const tool = createGameActionTool({
				definition: actionDefinition,
				dispatcher: new DurableGameActionDispatcher(journal, {
					async execute(value) {
						return receipt(value);
					},
				}),
				expectedRevision: 7,
				projectReceipt,
				maximumModelReceiptCharacters: 64,
			});
			const result = await tool.execute(
				{ id: "call", name: "place_block", arguments: { x: 4.5, block: "stone" } },
				executionContext(),
			);
			expect(result.content).toEqual([{ type: "json", value: { status: "projection_failed" } }]);
			expect(result.isError).toBe(true);
			expect(JSON.stringify(result.content)).not.toMatch(/operationId|stateRevision|timelineId/);
			expect(result.details).toMatchObject({ entry: { status: "committed" } });
		}
	});

	it("reports uncertain replay as reconcile-only without executing the world write twice", async () => {
		const journal = new InMemoryGameActionJournal();
		let calls = 0;
		const tool = createGameActionTool({
			definition: actionDefinition,
			dispatcher: new DurableGameActionDispatcher(journal, {
				async execute() {
					calls += 1;
					throw new Error("receipt was lost");
				},
			}),
			expectedRevision: 7,
		});
		const call = { id: "call", name: "place_block", arguments: { x: 4.5, block: "stone" } } as const;
		await expect(tool.execute(call, executionContext())).rejects.toThrow(/receipt was lost/);
		const replay = await tool.execute(call, executionContext());
		expect(replay).toMatchObject({
			content: [{ type: "json", value: { status: "reconcile_required" } }],
			isError: true,
			details: { kind: "reconcile", entry: { status: "uncertain" } },
		});
		expect(calls).toBe(1);
	});

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

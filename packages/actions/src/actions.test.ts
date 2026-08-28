import type {
	GameActionIntent,
	GameActionReceipt,
	GameInput,
	GameSessionKey,
	GameToolDefinition,
	GameToolExecutionContext,
} from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import { createGameActionTool } from "./action-tool.js";
import { DurableGameActionDispatcher, type GameActionDispatchObservation } from "./dispatcher.js";
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
		const observations: GameActionDispatchObservation[] = [];
		const dispatcher = new DurableGameActionDispatcher(
			journal,
			{
				async execute(value) {
					calls += 1;
					expect((await journal.read(value.operationId))?.status).toBe("dispatched");
					return receipt(value);
				},
			},
			{ observer: { observeAction: (observation) => observations.push(observation) } },
		);

		expect((await dispatcher.dispatch(action)).kind).toBe("terminal");
		expect((await dispatcher.dispatch(action)).kind).toBe("terminal");
		expect(calls).toBe(1);
		expect(observations.map((observation) => observation.disposition)).toEqual(["executed", "duplicate-prevented"]);
		expect(observations[0]).toMatchObject({
			operationId: action.operationId,
			action: "place_block",
			terminalStatus: "committed",
		});
		expect(observations[0]?.hostMilliseconds).toBeGreaterThanOrEqual(0);
		expect(observations[0]?.frameworkMilliseconds).toBeGreaterThanOrEqual(0);
	});

	it("isolates durable action observer failures", async () => {
		const action = intent("a", "observer");
		const dispatcher = new DurableGameActionDispatcher(
			new InMemoryGameActionJournal(),
			{ execute: async (value) => receipt(value) },
			{
				observer: {
					observeAction: () => {
						throw new Error("observer failure");
					},
				},
			},
		);
		await expect(dispatcher.dispatch(action)).resolves.toMatchObject({ kind: "terminal" });
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

	it("waits for same-key actions across actors while allowing different keys to run in parallel", async () => {
		const journal = new InMemoryGameActionJournal();
		let concurrent = 0;
		let maximumConcurrent = 0;
		const started: string[] = [];
		const releases = new Map<string, () => void>();
		const dispatcher = new DurableGameActionDispatcher(
			journal,
			{
				async execute(value) {
					started.push(value.inputId);
					concurrent += 1;
					maximumConcurrent = Math.max(maximumConcurrent, concurrent);
					await new Promise<void>((resolve) => releases.set(value.inputId, resolve));
					concurrent -= 1;
					return receipt(value);
				},
			},
			{ conflictPollIntervalMilliseconds: 1, maximumConflictWaitMilliseconds: 1_000 },
		);
		const first = intent("a", "first", "shared-resource");
		const second = intent("b", "second", "shared-resource");
		const independent = intent("c", "independent", "other-resource");
		const firstRun = dispatcher.dispatch(first);
		await vi.waitFor(() => expect(started).toContain("first"));
		const secondRun = dispatcher.dispatch(second);
		const independentRun = dispatcher.dispatch(independent);
		await vi.waitFor(() => expect(started).toContain("independent"));
		expect(started).not.toContain("second");
		expect(maximumConcurrent).toBe(2);

		releases.get("first")?.();
		await vi.waitFor(() => expect(started).toContain("second"));
		releases.get("second")?.();
		releases.get("independent")?.();
		await expect(Promise.all([firstRun, secondRun, independentRun])).resolves.toHaveLength(3);
	});

	it("keeps an uncertain conflict blocked across cancellation until authoritative reconciliation", async () => {
		const journal = new InMemoryGameActionJournal();
		const first = intent("a", "uncertain", "shared-resource");
		const second = intent("b", "waiting", "shared-resource");
		let calls = 0;
		const dispatcher = new DurableGameActionDispatcher(
			journal,
			{
				async execute(value) {
					calls += 1;
					if (value.operationId === first.operationId) throw new Error("receipt lost after delivery");
					return receipt(value);
				},
			},
			{ conflictPollIntervalMilliseconds: 1, maximumConflictWaitMilliseconds: 1_000 },
		);
		await expect(dispatcher.dispatch(first)).rejects.toThrow(/receipt lost/);
		const cancellation = new AbortController();
		const waiting = dispatcher.dispatch(second, cancellation.signal);
		await vi.waitFor(() => expect(calls).toBe(1));
		cancellation.abort();
		await expect(waiting).rejects.toMatchObject({ name: "AbortError" });
		expect((await journal.read(second.operationId))?.status).toBe("prepared");

		await dispatcher.reconcile(receipt(first, "rejected"));
		await expect(dispatcher.dispatch(second)).resolves.toMatchObject({ kind: "terminal" });
		expect(calls).toBe(2);
	});

	it("returns bounded reconcile state when a conflicting action does not reach a terminal receipt", async () => {
		const journal = new InMemoryGameActionJournal();
		const first = intent("a", "blocking", "shared-resource");
		const second = intent("b", "bounded-wait", "shared-resource");
		let releaseFirst: (() => void) | undefined;
		const dispatcher = new DurableGameActionDispatcher(
			journal,
			{
				async execute(value) {
					if (value.operationId === first.operationId) {
						await new Promise<void>((resolve) => {
							releaseFirst = resolve;
						});
					}
					return receipt(value);
				},
			},
			{ conflictPollIntervalMilliseconds: 1, maximumConflictWaitMilliseconds: 5 },
		);
		const blocking = dispatcher.dispatch(first);
		await vi.waitFor(() => expect(releaseFirst).toBeDefined());

		await expect(dispatcher.dispatch(second)).resolves.toMatchObject({
			kind: "reconcile",
			blockingOperationId: first.operationId,
		});
		expect((await journal.read(second.operationId))?.status).toBe("prepared");

		releaseFirst?.();
		await blocking;
	});
});

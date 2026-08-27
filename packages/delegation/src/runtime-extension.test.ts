import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type {
	GameAgentEvent,
	GameAgentKernelPort,
	GameControlResult,
	GameInput,
	GameKernelRunRequest,
	GameSessionKey,
	GameToolExecutionContext,
} from "@opengameagent/protocol";
import { GameAgentRuntime } from "@opengameagent/runtime";
import { afterEach, describe, expect, it } from "vitest";
import { type GameDelegationOutcome, SqliteGameDelegationStore } from "./delegation.js";
import { GameDelegationManager } from "./manager.js";
import {
	type GameDelegationExecutionAuthority,
	type GameDelegationExecutor,
	type GameDelegationHandle,
	RuntimeGameDelegationExecutor,
} from "./runtime-executor.js";
import { createGameDelegationExtension } from "./runtime-extension.js";

const directories: string[] = [];

function session(actorId = "actor-a"): GameSessionKey {
	return {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 1,
		ownerId: "owner",
		sessionId: `session-${actorId}`,
		actorId,
	};
}

function input(actorId = "actor-a", type = "npc.chat"): GameInput {
	return {
		id: `input-${actorId}`,
		type,
		session: session(actorId),
		moment: { tick: 42 },
		content: [{ type: "text", text: "inspect the river" }],
	};
}

function context(gameInput = input(), turn = 1, toolCallIndex = 0): GameToolExecutionContext {
	return {
		input: gameInput,
		runId: "parent-run",
		turn,
		toolCallIndex,
		signal: new AbortController().signal,
	};
}

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-delegation-extension-"));
	directories.push(directory);
	return join(directory, "delegation.sqlite");
}

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

class ImmediateHandle implements GameDelegationHandle {
	readonly completion: Promise<GameDelegationOutcome> = Promise.resolve({
		status: "completed",
		result: { text: "safe result" },
	});

	async steer(): Promise<GameControlResult> {
		return { accepted: false, reason: "not-active" };
	}

	abort(): GameControlResult {
		return { accepted: false, reason: "not-active" };
	}

	async [Symbol.asyncDispose](): Promise<void> {}
}

class ImmediateExecutor implements GameDelegationExecutor {
	start(
		_request: Parameters<GameDelegationExecutor["start"]>[0],
		_authority: GameDelegationExecutionAuthority,
		_signal: AbortSignal,
	): GameDelegationHandle {
		return new ImmediateHandle();
	}
}

describe("createGameDelegationExtension", () => {
	it("uses stable replay ids, enforces exact ownership, and exposes only a safe bounded projection", async () => {
		using store = new SqliteGameDelegationStore(await databasePath());
		await using manager = new GameDelegationManager({ store, executor: new ImmediateExecutor() });
		const extension = createGameDelegationExtension({
			manager,
			delegates: () => [
				{ id: "scout", description: "Inspect a bounded area.", maximumTurns: 4 },
				{ id: "builder", description: "Plan a bounded build." },
			],
		});
		const tools = await extension.toolProvider.provide(input(), new AbortController().signal);
		const delegate = tools.find((tool) => tool.definition.name === "delegate_agent_task");
		if (!delegate) throw new Error("Delegation tool was not exposed.");
		expect(delegate.definition.parameters["properties"]).toMatchObject({
			delegateId: { enum: ["scout", "builder"] },
		});
		const call = {
			id: "call-a",
			name: delegate.definition.name,
			arguments: { delegateId: "scout", task: { area: 3, mode: "safe" } },
		};
		const first = await delegate.execute(call, context());
		const replay = await delegate.execute(
			{ ...call, arguments: { delegateId: "scout", task: { mode: "safe", area: 3 } } },
			context(),
		);
		expect(replay).toEqual(first);
		const value = first.content[0]?.type === "json" ? first.content[0].value : undefined;
		expect(value).toMatchObject({ delegateId: "scout", status: "completed", result: { text: "safe result" } });
		expect(JSON.stringify(value)).not.toMatch(/session|lease|fencing|parentRun|parentInput/i);
		const changed = await delegate.execute({ ...call, arguments: { ...call.arguments, maximumTurns: 3 } }, context());
		const changedValue = changed.content[0]?.type === "json" ? changed.content[0].value : undefined;
		expect((changedValue as { id: string }).id).not.toBe((value as { id: string }).id);

		const delegatedId = (value as { id: string }).id;
		const read = tools.find((tool) => tool.definition.name === "read_delegated_task");
		if (!read) throw new Error("Read tool was not exposed.");
		const denied = await read.execute(
			{ id: "call-read", name: read.definition.name, arguments: { id: delegatedId } },
			context(input("actor-b")),
		);
		expect(denied.isError).toBe(true);
		expect(denied.content).toEqual([{ type: "json", value: { error: "delegation_not_found" } }]);
	});

	it("keeps context inheritance host-controlled and removes recursive delegation at the depth limit", async () => {
		using store = new SqliteGameDelegationStore(await databasePath());
		await using manager = new GameDelegationManager({ store, executor: new ImmediateExecutor() });
		const extension = createGameDelegationExtension({
			manager,
			maximumDepth: 2,
			resolveLineage: () => ({ id: "parent", rootId: "root", depth: 2 }),
			delegates: () => [{ id: "scout", description: "Inspect without inheriting parent context." }],
		});
		const tools = await extension.toolProvider.provide(input(), new AbortController().signal);
		expect(tools.map((tool) => tool.definition.name)).not.toContain("delegate_agent_task");

		const rootExtension = createGameDelegationExtension({
			manager,
			delegates: () => [{ id: "scout", description: "Inspect without inheriting parent context." }],
		});
		const rootTools = await rootExtension.toolProvider.provide(input(), new AbortController().signal);
		const delegate = rootTools.find((tool) => tool.definition.name === "delegate_agent_task");
		if (!delegate) throw new Error("Delegation tool was not exposed.");
		await expect(
			delegate.execute(
				{
					id: "call-inherit",
					name: delegate.definition.name,
					arguments: { delegateId: "scout", task: null, inheritContext: true },
				},
				context(),
			),
		).rejects.toThrow(/does not allow/);

		let captures = 0;
		const inherited = createGameDelegationExtension({
			manager,
			delegates: () => [
				{ id: "scout", description: "Inspect with an approved snapshot.", allowContextInheritance: true },
			],
			captureContext: (gameInput) => {
				captures += 1;
				return { visibleArea: gameInput.context?.["visibleArea"] ?? null };
			},
		});
		const inheritedInput = { ...input(), context: { visibleArea: "north" } };
		const inheritedTools = await inherited.toolProvider.provide(inheritedInput, new AbortController().signal);
		const inheritedDelegate = inheritedTools.find((tool) => tool.definition.name === "delegate_agent_task");
		if (!inheritedDelegate) throw new Error("Delegation tool was not exposed.");
		await inheritedDelegate.execute(
			{
				id: "call-inherit-approved",
				name: inheritedDelegate.definition.name,
				arguments: { delegateId: "scout", task: null, inheritContext: true },
			},
			context(inheritedInput),
		);
		await inheritedDelegate.execute(
			{
				id: "call-inherit-approved-replay",
				name: inheritedDelegate.definition.name,
				arguments: { delegateId: "scout", task: null, inheritContext: true },
			},
			context({ ...inheritedInput, context: { visibleArea: "south" } }),
		);
		const records = await manager.list(inheritedInput.session);
		expect(records[0]?.request.inheritedContext).toEqual({ visibleArea: "north" });
		expect(captures).toBe(1);
	});
});

class CapturingDelegationKernel implements GameAgentKernelPort {
	readonly requests: GameKernelRunRequest[] = [];

	async *run(request: GameKernelRunRequest): AsyncIterable<GameAgentEvent> {
		this.requests.push(request);
		const base = {
			sequence: 1,
			eventId: `${request.runId}:1`,
			runId: request.runId,
			audience: { visibility: "owner" as const },
			timestamp: 1,
		};
		yield {
			...base,
			type: "run.started",
			turn: 0,
			inputId: request.input.id,
			model: {
				profileId: request.modelProfileId,
				provider: "fake",
				model: "fake",
				api: "messages",
				reasoning: false,
				input: ["text"],
				contextWindow: 4096,
				maximumOutputTokens: 256,
			},
		};
		const usage = { input: 4, output: 2, cacheRead: 0, cacheWrite: 0, totalTokens: 6 };
		yield {
			...base,
			sequence: 2,
			eventId: `${request.runId}:2`,
			type: "message.completed",
			turn: 1,
			text: "done",
			usage,
		};
		yield { ...base, sequence: 3, eventId: `${request.runId}:3`, type: "run.completed", turn: 1, usage };
	}

	steer(): GameControlResult {
		return { accepted: true };
	}

	followUp(): GameControlResult {
		return { accepted: true };
	}

	abort(): GameControlResult {
		return { accepted: true };
	}
}

describe("RuntimeGameDelegationExecutor", () => {
	it("reuses the host runtime and preserves child turn bounds without exposing authority state", async () => {
		const kernel = new CapturingDelegationKernel();
		const runtime = new GameAgentRuntime({
			kernel,
			baseSystemPrompt: "system",
			defaultModelProfileId: "default",
		});
		const executor = new RuntimeGameDelegationExecutor({
			runtime,
			createInput: (request) => ({
				id: `child-${request.id}`,
				type: "agent.delegation",
				session: structuredClone(request.session),
				moment: structuredClone(request.parentMoment),
				content: [{ type: "json", value: structuredClone(request.task) }],
			}),
		});
		const handle = executor.start(
			{
				id: "delegation-a",
				session: session(),
				parentInputId: "input-a",
				parentRunId: "run-a",
				parentTurn: 1,
				parentMoment: { tick: 42 },
				delegateId: "scout",
				task: { objective: "inspect" },
				depth: 1,
				maximumTurns: 3,
				inheritContext: false,
				rootDelegationId: "delegation-a",
			},
			{ isAuthoritative: async () => true },
			new AbortController().signal,
		);
		expect(await handle.completion).toEqual({
			status: "completed",
			result: {
				text: "done",
				usage: { input: 4, output: 2, cacheRead: 0, cacheWrite: 0, totalTokens: 6 },
			},
		});
		expect(kernel.requests).toHaveLength(1);
		expect(kernel.requests[0]).toMatchObject({
			maximumTurns: 3,
			input: { id: "child-delegation-a", type: "agent.delegation" },
		});
		await handle[Symbol.asyncDispose]();
	});

	it("supports immutable runtime composition through a lazy runtime reference", async () => {
		const kernel = new CapturingDelegationKernel();
		let runtime: GameAgentRuntime | undefined;
		const executor = new RuntimeGameDelegationExecutor({
			getRuntime: () => {
				if (!runtime) throw new Error("Runtime is not composed.");
				return runtime;
			},
			createInput: (request) => ({
				id: `child-${request.id}`,
				type: "agent.delegation",
				session: request.session,
				moment: request.parentMoment,
				content: [{ type: "json", value: request.task }],
			}),
		});
		runtime = new GameAgentRuntime({ kernel, baseSystemPrompt: "system", defaultModelProfileId: "default" });
		const handle = executor.start(
			{
				id: "delegation-lazy",
				session: session(),
				parentInputId: "input-a",
				parentRunId: "run-a",
				parentTurn: 1,
				parentMoment: { tick: 42 },
				delegateId: "scout",
				task: null,
				depth: 1,
				maximumTurns: 2,
				inheritContext: false,
				rootDelegationId: "delegation-lazy",
			},
			{ isAuthoritative: async () => true },
			new AbortController().signal,
		);
		expect((await handle.completion).status).toBe("completed");
		expect(kernel.requests[0]?.maximumTurns).toBe(2);
	});
});

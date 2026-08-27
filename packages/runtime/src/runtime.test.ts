import type {
	GameAgentEvent,
	GameAgentKernelPort,
	GameControlResult,
	GameInput,
	GameKernelRunRequest,
	GameSessionKey,
	GameTool,
} from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { GameAgentRuntime } from "./runtime.js";
import { ActorScheduler } from "./scheduler.js";
import { preflightGameToolSchema } from "./tool-schema.js";

const session = (actorId: string): GameSessionKey => ({
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 1,
	ownerId: "owner",
	sessionId: `session-${actorId}`,
	actorId,
});

const input = (actorId: string, id = `input-${actorId}`, type = "npc.chat"): GameInput => ({
	id,
	type,
	session: session(actorId),
	moment: { tick: 4.25 },
	content: [{ type: "text", text: "hello" }],
});

const event = (
	request: GameKernelRunRequest,
	type: "run.started" | "run.completed",
	sequence: number,
): GameAgentEvent => {
	const common = {
		sequence,
		eventId: `${request.runId}:${sequence}`,
		runId: request.runId,
		audience: { visibility: "owner" } as const,
		timestamp: Date.now(),
	};
	return type === "run.started"
		? {
				...common,
				type,
				turn: 0,
				inputId: request.input.id,
				model: {
					profileId: request.modelProfileId,
					provider: "test",
					model: "test",
					api: "test",
					reasoning: false,
					input: ["text"],
					contextWindow: 4096,
					maximumOutputTokens: 1024,
				},
			}
		: { ...common, type, turn: 1 };
};

class CapturingKernel implements GameAgentKernelPort {
	readonly requests: GameKernelRunRequest[] = [];

	async *run(request: GameKernelRunRequest): AsyncIterable<GameAgentEvent> {
		this.requests.push(request);
		yield event(request, "run.started", 1);
		yield event(request, "run.completed", 2);
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

async function collect(iterable: AsyncIterable<GameAgentEvent>): Promise<GameAgentEvent[]> {
	const result: GameAgentEvent[] = [];
	for await (const item of iterable) result.push(item);
	return result;
}

const validTool = (name: string): GameTool => ({
	definition: {
		name,
		label: name,
		description: "A test tool.",
		parameters: {
			type: "object",
			properties: {
				count: { type: "integer", minimum: 1, maximum: 4 },
				mode: { type: "string", enum: ["safe", "fast"] },
				points: { type: "array", items: { type: "number" }, minItems: 1, maxItems: 8 },
			},
			required: ["count"],
			additionalProperties: false,
		},
	},
	async execute() {
		return { content: [{ type: "text", text: "ok" }] };
	},
});

describe("ActorScheduler", () => {
	it("serializes each actor while allowing bounded cross-actor concurrency", async () => {
		const scheduler = new ActorScheduler({ maximumConcurrentActors: 2, maximumQueuedRuns: 4 });
		const releaseFirstA = await scheduler.acquire("a");
		let secondAStarted = false;
		const secondA = scheduler.acquire("a").then((release) => {
			secondAStarted = true;
			return release;
		});
		const releaseB = await scheduler.acquire("b");
		expect(scheduler.activeActorCount).toBe(2);
		expect(secondAStarted).toBe(false);
		releaseB();
		await Promise.resolve();
		expect(secondAStarted).toBe(false);
		releaseFirstA();
		const releaseSecondA = await secondA;
		expect(secondAStarted).toBe(true);
		releaseSecondA();
	});
});

describe("tool schema preflight", () => {
	it("accepts bounded closed game tool schemas", () => {
		expect(() => preflightGameToolSchema(validTool("gather").definition)).not.toThrow();
	});

	it("visits refs, invalid bounds, deep schemas, and hidden anyOf branches before any model call", () => {
		expect(() =>
			preflightGameToolSchema({ ...validTool("ref").definition, parameters: { $ref: "#/$defs/x" } }),
		).toThrow(/references/);
		expect(() =>
			preflightGameToolSchema({ ...validTool("minimum").definition, parameters: { type: "number", minimum: "one" } }),
		).toThrow(/finite number/);
		expect(() =>
			preflightGameToolSchema({
				...validTool("branch").definition,
				parameters: { anyOf: [{ type: "string" }, { type: "object", secretKeyword: true }] },
			}),
		).toThrow(/secretKeyword/);
		let nested: GameTool["definition"]["parameters"] = { type: "string" };
		for (let index = 0; index < 8; index += 1) nested = { type: "array", items: nested };
		expect(() => preflightGameToolSchema({ ...validTool("deep").definition, parameters: nested }, 4)).toThrow(/depth/);
	});
});

describe("GameAgentRuntime", () => {
	it("collects context concurrently, filters advertised tools per input, and persists events before projection", async () => {
		const kernel = new CapturingKernel();
		const persisted: string[] = [];
		const runtime = new GameAgentRuntime({
			kernel,
			baseSystemPrompt: "base",
			defaultModelProfileId: "default",
			modelProfilePolicy: {
				select(gameInput) {
					return gameInput.type === "npc.image" ? "vision-local" : "default";
				},
			},
			contextProviders: [
				{
					async provide() {
						return { name: "low", priority: 1, value: { value: 1 } };
					},
				},
				{
					async provide() {
						return { name: "high", priority: 9, value: { value: 9 } };
					},
				},
			],
			toolProviders: [
				{
					async provide() {
						return [validTool("remember"), validTool("generate_image")];
					},
				},
			],
			toolVisibility: {
				isVisible(gameInput, tool) {
					return gameInput.type !== "npc.image" || tool.name === "generate_image";
				},
			},
			eventStore: {
				async append(_input, storedEvent) {
					persisted.push(storedEvent.type);
				},
			},
		});

		const events = await collect(runtime.run(input("a", "image", "npc.image"), { runId: "run-image" }));
		expect(kernel.requests).toHaveLength(1);
		expect(kernel.requests[0]?.modelProfileId).toBe("vision-local");
		expect(kernel.requests[0]?.tools.map((tool) => tool.definition.name)).toEqual(["generate_image"]);
		expect(kernel.requests[0]?.systemPrompt.indexOf("high")).toBeLessThan(
			kernel.requests[0]?.systemPrompt.indexOf("low") ?? 0,
		);
		expect(persisted).toEqual(events.map((item) => item.type));
	});

	it("rejects invalid schemas before invoking the kernel", async () => {
		const kernel = new CapturingKernel();
		const runtime = new GameAgentRuntime({
			kernel,
			baseSystemPrompt: "base",
			defaultModelProfileId: "default",
			toolProviders: [
				{
					async provide() {
						return [
							{
								...validTool("broken"),
								definition: { ...validTool("broken").definition, parameters: { type: "number", minimum: "bad" } },
							},
						];
					},
				},
			],
		});

		await expect(collect(runtime.run(input("a"), { runId: "run-invalid" }))).rejects.toThrow(/finite number/);
		expect(kernel.requests).toHaveLength(0);
	});

	it("builds post-tool context from the final filtered tool catalog", async () => {
		const kernel = new CapturingKernel();
		const runtime = new GameAgentRuntime({
			kernel,
			baseSystemPrompt: "base",
			defaultModelProfileId: "default",
			toolProviders: [{ provide: async () => [validTool("visible"), validTool("hidden")] }],
			toolVisibility: { isVisible: (_gameInput, tool) => tool.name !== "hidden" },
			postToolContextProviders: [
				{
					async provide(_gameInput, tools) {
						return { name: "tool-dependent", priority: 2, value: tools.map((tool) => tool.name) };
					},
				},
			],
		});
		await collect(runtime.run(input("a"), { runId: "post-tool" }));
		expect(kernel.requests[0]?.systemPrompt).toContain("visible");
		expect(kernel.requests[0]?.systemPrompt).not.toContain("hidden");
	});

	it("re-collects authoritative context and visible tools before a later model turn", async () => {
		let worldRevision = 1;
		let updatedPrompt = "";
		let updatedTools: readonly GameTool[] = [];
		const kernel: GameAgentKernelPort = {
			async *run(request) {
				yield event(request, "run.started", 1);
				worldRevision = 2;
				const update = await request.prepareNextTurn?.(
					{ input: request.input, runId: request.runId, turn: 1, hadToolResults: true },
					new AbortController().signal,
				);
				updatedPrompt = update?.systemPrompt ?? "";
				updatedTools = update?.tools ?? [];
				yield event(request, "run.completed", 2);
			},
			steer: () => ({ accepted: true }),
			followUp: () => ({ accepted: true }),
			abort: () => ({ accepted: true }),
		};
		const runtime = new GameAgentRuntime({
			kernel,
			baseSystemPrompt: "base",
			defaultModelProfileId: "default",
			contextProviders: [
				{ provide: async () => ({ name: "world", priority: 10, value: { revision: worldRevision } }) },
			],
			toolProviders: [{ provide: async () => [validTool(worldRevision === 1 ? "old_action" : "new_action")] }],
		});

		await collect(runtime.run(input("a"), { runId: "refresh-run" }));
		expect(updatedPrompt).toContain('"revision":2');
		expect(updatedTools.map((tool) => tool.definition.name)).toEqual(["new_action"]);
	});

	it("rejects stale exact controls before they reach the kernel", async () => {
		let release: (() => void) | undefined;
		const kernel: GameAgentKernelPort = {
			async *run(request) {
				yield event(request, "run.started", 1);
				await new Promise<void>((resolve) => {
					release = resolve;
				});
				yield event(request, "run.completed", 2);
			},
			steer: () => ({ accepted: true }),
			followUp: () => ({ accepted: true }),
			abort: () => ({ accepted: true }),
		};
		const runtime = new GameAgentRuntime({ kernel, baseSystemPrompt: "base", defaultModelProfileId: "default" });
		const running = collect(runtime.run(input("a"), { runId: "active-run" }));
		await new Promise((resolve) => setTimeout(resolve, 10));
		expect(runtime.abort(session("a"), { runId: "older-run", turn: 0 })).toEqual({
			accepted: false,
			reason: "run-mismatch",
		});
		expect(runtime.abort(session("a"), { runId: "active-run", turn: 8 })).toEqual({
			accepted: false,
			reason: "turn-mismatch",
		});
		release?.();
		await running;
	});
});

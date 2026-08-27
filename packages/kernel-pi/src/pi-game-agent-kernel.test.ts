import type { StreamFn } from "@earendil-works/pi-agent-core";
import {
	type Api,
	type AssistantMessage,
	type AssistantMessageEvent,
	type Context,
	EventStream,
	type Model,
} from "@earendil-works/pi-ai";
import { InMemoryGameConversationStore } from "@opengameagent/kernel";
import type { GameAgentEvent, GameInput, GameSessionKey, GameTool } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import type { PiGameModelResolver } from "./model-registry.js";
import { PiGameAgentKernel } from "./pi-game-agent-kernel.js";

const model: Model<Api> = {
	id: "test-model",
	name: "Test model",
	api: "openai-responses",
	provider: "openai",
	baseUrl: "https://example.invalid",
	reasoning: false,
	input: ["text", "image"],
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
	contextWindow: 16_384,
	maxTokens: 4_096,
};

function modelResolver(streamFn: StreamFn): PiGameModelResolver {
	return {
		resolve(profileId) {
			expect(profileId).toBe("default");
			return {
				model,
				streamFn,
				thinkingLevel: "off",
				descriptor: {
					profileId,
					provider: model.provider,
					model: model.id,
					api: model.api,
					reasoning: model.reasoning,
					input: model.input,
					contextWindow: model.contextWindow,
					maximumOutputTokens: model.maxTokens,
				},
			};
		},
	};
}

const session: GameSessionKey = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 3,
	ownerId: "player",
	sessionId: "npc-session",
	actorId: "npc-7",
};

function input(
	id: string,
	content: GameInput["content"] = [{ type: "json", value: { event: "player_nearby" } }],
): GameInput {
	return {
		id,
		type: "npc.event",
		session,
		moment: { tick: 123.5, calendar: "spring-12" },
		content,
		context: { weather: "rain" },
	};
}

function assistant<TStopReason extends AssistantMessage["stopReason"]>(
	content: AssistantMessage["content"],
	stopReason: TStopReason,
): AssistantMessage & { stopReason: TStopReason } {
	return {
		role: "assistant",
		content,
		api: "openai-responses",
		provider: "openai",
		model: model.id,
		usage: {
			input: 10,
			output: 4,
			cacheRead: 2,
			cacheWrite: 0,
			totalTokens: 16,
			cost: { input: 0.01, output: 0.02, cacheRead: 0, cacheWrite: 0, total: 0.03 },
		},
		stopReason,
		timestamp: Date.now(),
	};
}

class MockAssistantStream extends EventStream<AssistantMessageEvent, AssistantMessage> {
	constructor() {
		super(
			(event) => event.type === "done" || event.type === "error",
			(event) => {
				if (event.type === "done") return event.message;
				if (event.type === "error") return event.error;
				throw new Error("Unexpected non-terminal event.");
			},
		);
	}
}

function completedStream(
	message: AssistantMessage & { stopReason: "length" | "stop" | "toolUse" | "deferred" },
): MockAssistantStream {
	const stream = new MockAssistantStream();
	queueMicrotask(() => stream.push({ type: "done", reason: message.stopReason, message }));
	return stream;
}

async function collect(iterable: AsyncIterable<GameAgentEvent>): Promise<GameAgentEvent[]> {
	const events: GameAgentEvent[] = [];
	for await (const event of iterable) events.push(event);
	return events;
}

describe("PiGameAgentKernel", () => {
	it("projects structured and image input through the Pi loop without exposing canonical actor coordinates", async () => {
		let capturedContext: Context | undefined;
		const streamFn: StreamFn = (_model, context) => {
			capturedContext = context;
			return completedStream(assistant([{ type: "text", text: "I can see it." }], "stop"));
		};
		const kernel = new PiGameAgentKernel({ models: modelResolver(streamFn) });

		const events = await collect(
			kernel.run({
				runId: "run-1",
				input: input("input-1", [
					{ type: "json", value: { blocks: [{ kind: "stone", x: 1.5, y: 2, z: -3.25 }] } },
					{ type: "image", mimeType: "image/png", data: "aW1hZ2U=" },
				]),
				systemPrompt: "Act inside the game using registered tools.",
				tools: [],
				modelProfileId: "default",
				maximumTurns: 4,
			}),
		);

		const user = capturedContext?.messages.find((message) => message.role === "user");
		expect(user?.content).toEqual([
			expect.objectContaining({ type: "text" }),
			{ type: "image", mimeType: "image/png", data: "aW1hZ2U=" },
		]);
		const serialized = JSON.stringify(user?.content);
		expect(serialized).toContain("blocks");
		expect(serialized).not.toContain(session.actorId);
		expect(events.map((event) => event.type)).toEqual([
			"run.started",
			"turn.started",
			"message.completed",
			"turn.completed",
			"run.completed",
		]);
		expect(events.every((event, index) => event.sequence === index + 1)).toBe(true);
	});

	it("executes registered game tools through multiple ReAct turns", async () => {
		let requests = 0;
		let executions = 0;
		const streamFn: StreamFn = () => {
			requests += 1;
			if (requests === 1) {
				return completedStream(
					assistant(
						[{ type: "toolCall", id: "tool-call-1", name: "inspect_tile", arguments: { x: 12, y: 8 } }],
						"toolUse",
					),
				);
			}
			return completedStream(assistant([{ type: "text", text: "The tile is clear." }], "stop"));
		};
		const tool: GameTool = {
			definition: {
				name: "inspect_tile",
				label: "Inspect tile",
				description: "Read one tile from the authoritative game state.",
				parameters: {
					type: "object",
					properties: { x: { type: "number" }, y: { type: "number" } },
					required: ["x", "y"],
					additionalProperties: false,
				},
			},
			async execute(call) {
				executions += 1;
				expect(call.arguments).toEqual({ x: 12, y: 8 });
				return { content: [{ type: "json", value: { passable: true } }], details: { source: "host" } };
			},
		};
		const kernel = new PiGameAgentKernel({ models: modelResolver(streamFn) });

		const events = await collect(
			kernel.run({
				runId: "run-tools",
				input: input("input-tools"),
				systemPrompt: "Use tools.",
				tools: [tool],
				modelProfileId: "default",
				maximumTurns: 4,
			}),
		);

		expect(requests).toBe(2);
		expect(executions).toBe(1);
		expect(events.filter((event) => event.type === "tool.started")).toHaveLength(1);
		expect(events.filter((event) => event.type === "tool.completed")).toHaveLength(1);
		expect(events.filter((event) => event.type === "turn.started")).toHaveLength(2);
	});

	it("restores the complete normalized transcript for long-lived NPC sessions", async () => {
		const conversationStore = new InMemoryGameConversationStore();
		let requestCount = 0;
		let secondContext: Context | undefined;
		const streamFn: StreamFn = (_model, context) => {
			requestCount += 1;
			if (requestCount === 2) secondContext = context;
			return completedStream(
				assistant(
					requestCount === 1
						? [
								{ type: "thinking", thinking: "hidden", thinkingSignature: "opaque-signature", redacted: true },
								{ type: "text", text: "first", textSignature: "response-item" },
							]
						: [{ type: "text", text: "second" }],
					"stop",
				),
			);
		};
		const kernel = new PiGameAgentKernel({ models: modelResolver(streamFn), conversationStore });

		await collect(
			kernel.run({
				runId: "run-memory-1",
				input: input("input-memory-1"),
				systemPrompt: "Remember.",
				tools: [],
				modelProfileId: "default",
				maximumTurns: 2,
			}),
		);
		const persisted = await conversationStore.read(session);
		expect(persisted.revision).toBe(1);
		expect(persisted.messages).toEqual(
			expect.arrayContaining([
				expect.objectContaining({
					role: "assistant",
					content: expect.arrayContaining([
						expect.objectContaining({ type: "reasoning", signature: "opaque-signature", redacted: true }),
						expect.objectContaining({ type: "text", signature: "response-item" }),
					]),
				}),
			]),
		);

		await collect(
			kernel.run({
				runId: "run-memory-2",
				input: input("input-memory-2"),
				systemPrompt: "Remember.",
				tools: [],
				modelProfileId: "default",
				maximumTurns: 2,
			}),
		);
		const restoredAssistant = secondContext?.messages.find((message) => message.role === "assistant");
		expect(restoredAssistant).toEqual(
			expect.objectContaining({
				content: expect.arrayContaining([
					expect.objectContaining({ type: "thinking", thinkingSignature: "opaque-signature", redacted: true }),
					expect.objectContaining({ type: "text", textSignature: "response-item" }),
				]),
			}),
		);
	});

	it("requires exact run and turn coordinates for control", async () => {
		let release: (() => void) | undefined;
		const streamFn: StreamFn = (_model, _context, options) => {
			const stream = new MockAssistantStream();
			void new Promise<void>((resolve) => {
				release = resolve;
				options?.signal?.addEventListener("abort", () => resolve(), { once: true });
			}).then(() => {
				stream.push({
					type: "error",
					reason: options?.signal?.aborted ? "aborted" : "error",
					error: assistant([], options?.signal?.aborted ? "aborted" : "error"),
				});
			});
			return stream;
		};
		const kernel = new PiGameAgentKernel({ models: modelResolver(streamFn) });
		const events = kernel.run({
			runId: "run-control",
			input: input("input-control"),
			systemPrompt: "Wait.",
			tools: [],
			modelProfileId: "default",
			maximumTurns: 2,
		});
		const collecting = collect(events);

		await new Promise((resolve) => setTimeout(resolve, 10));
		expect(kernel.abort(session, { runId: "run-control", turn: 9 })).toEqual({
			accepted: false,
			reason: "turn-mismatch",
		});
		expect(kernel.abort({ ...session, actorId: "other" }, { runId: "run-control", turn: 1 })).toEqual({
			accepted: false,
			reason: "run-mismatch",
		});
		expect(kernel.abort(session, { runId: "run-control", turn: 1 })).toEqual({ accepted: true });
		release?.();
		await collecting;
		expect(kernel.abort(session, { runId: "run-control", turn: 1 })).toEqual({ accepted: false, reason: "not-active" });
	});
});

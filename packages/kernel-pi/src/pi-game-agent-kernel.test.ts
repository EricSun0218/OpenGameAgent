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
import type {
	GameAgentEvent,
	GameImageAttachment,
	GameImageAttachmentReference,
	GameImageAttachmentStore,
	GameInput,
	GameSessionKey,
	GameTool,
} from "@opengameagent/protocol";
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

class TestAttachmentStore implements GameImageAttachmentStore {
	private readonly values = new Map<string, GameImageAttachment>();

	async admit(mimeType: string, data: Uint8Array): Promise<GameImageAttachmentReference> {
		const sha256 = createHash("sha256").update(data).digest("hex");
		const reference = {
			id: `img_${sha256}`,
			sha256,
			mimeType,
			bytes: data.byteLength,
			width: 1,
			height: 1,
		};
		this.values.set(reference.id, { reference, data: new Uint8Array(data) });
		return reference;
	}

	async read(id: string): Promise<GameImageAttachment | undefined> {
		return this.values.get(id);
	}
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
			async execute(call, context) {
				executions += 1;
				expect(call.arguments).toEqual({ x: 12, y: 8 });
				expect(context).toMatchObject({ input, runId: "run-tools", turn: 1, toolCallIndex: 0 });
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

	it("applies a refreshed system prompt and tool catalog before the next provider request", async () => {
		let requests = 0;
		let refreshedContext: Context | undefined;
		const streamFn: StreamFn = (_model, context) => {
			requests += 1;
			if (requests === 1) {
				return completedStream(
					assistant([{ type: "toolCall", id: "refresh-1", name: "old_action", arguments: {} }], "toolUse"),
				);
			}
			refreshedContext = context;
			return completedStream(assistant([{ type: "text", text: "refreshed" }], "stop"));
		};
		const tool = (name: string): GameTool => ({
			definition: {
				name,
				label: name,
				description: "A dynamic authoritative action.",
				parameters: { type: "object", properties: {}, additionalProperties: false },
			},
			execute: async () => ({ content: [{ type: "json", value: { committed: true } }] }),
		});
		const kernel = new PiGameAgentKernel({ models: modelResolver(streamFn) });
		const preparations: number[] = [];

		await collect(
			kernel.run({
				runId: "run-refresh",
				input: input("input-refresh"),
				systemPrompt: "world revision 1",
				tools: [tool("old_action")],
				modelProfileId: "default",
				maximumTurns: 4,
				async prepareNextTurn(context) {
					preparations.push(context.turn);
					return { systemPrompt: "world revision 2", tools: [tool("new_action")] };
				},
			}),
		);

		expect(preparations).toContain(1);
		expect(refreshedContext?.systemPrompt).toBe("world revision 2");
		expect(refreshedContext?.tools?.map((candidate) => candidate.name)).toEqual(["new_action"]);
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

	it("persists images as immutable references and resolves them only at the provider boundary", async () => {
		const conversationStore = new InMemoryGameConversationStore();
		const imageAttachments = new TestAttachmentStore();
		const contexts: Context[] = [];
		const streamFn: StreamFn = (_model, context) => {
			contexts.push(context);
			return completedStream(assistant([{ type: "text", text: "seen" }], "stop"));
		};
		const kernel = new PiGameAgentKernel({ models: modelResolver(streamFn), conversationStore, imageAttachments });
		const image = Buffer.from(
			"89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000049454e44ae426082",
			"hex",
		).toString("base64");

		await collect(
			kernel.run({
				runId: "run-image-1",
				input: input("input-image-1", [{ type: "image", mimeType: "image/png", data: image }]),
				systemPrompt: "Observe.",
				tools: [],
				modelProfileId: "default",
				maximumTurns: 2,
			}),
		);
		const firstSnapshot = await conversationStore.read(session);
		const serialized = JSON.stringify(firstSnapshot.messages);
		expect(serialized).toContain('"type":"imageRef"');
		expect(serialized).not.toContain(image);

		await collect(
			kernel.run({
				runId: "run-image-2",
				input: input("input-image-2"),
				systemPrompt: "Remember the image.",
				tools: [],
				modelProfileId: "default",
				maximumTurns: 2,
			}),
		);
		const restored = contexts[1]?.messages.find((message) => message.role === "user" && Array.isArray(message.content));
		expect(restored?.content).toEqual(expect.arrayContaining([{ type: "image", mimeType: "image/png", data: image }]));
	});

	it("fails closed before a provider call when an image reference is missing or tampered", async () => {
		let providerCalls = 0;
		const streamFn: StreamFn = () => {
			providerCalls += 1;
			return completedStream(assistant([{ type: "text", text: "unexpected" }], "stop"));
		};
		const kernel = new PiGameAgentKernel({
			models: modelResolver(streamFn),
			imageAttachments: new TestAttachmentStore(),
		});
		const events = await collect(
			kernel.run({
				runId: "run-missing-image",
				input: input("input-missing-image", [
					{
						type: "imageRef",
						attachment: {
							id: `img_${"a".repeat(64)}`,
							sha256: "a".repeat(64),
							mimeType: "image/png",
							bytes: 1,
							width: 1,
							height: 1,
						},
					},
				]),
				systemPrompt: "Observe.",
				tools: [],
				modelProfileId: "default",
				maximumTurns: 2,
			}),
		);
		expect(providerCalls).toBe(0);
		expect(events).toEqual([expect.objectContaining({ type: "run.failed", category: "kernel" })]);
	});

	it("persists a compacted transcript before the next run and sends its summary to the provider", async () => {
		const conversationStore = new InMemoryGameConversationStore();
		await conversationStore.save(session, 0, [{ role: "user", content: "old history", timestamp: 1 }]);
		let capturedContext: Context | undefined;
		const streamFn: StreamFn = (_model, context) => {
			capturedContext = context;
			return completedStream(assistant([{ type: "text", text: "continued" }], "stop"));
		};
		const kernel = new PiGameAgentKernel({
			models: modelResolver(streamFn),
			conversationStore,
			conversationCompactor: {
				async compact(request) {
					expect(request.snapshot.revision).toBe(1);
					expect(request.model.contextWindow).toBe(model.contextWindow);
					return {
						messages: [
							{ role: "summary", summary: "The old request was completed.", tokensBefore: 9000, timestamp: 2 },
						],
						changed: true,
						tokensBefore: 9000,
						tokensAfter: 10,
						summarizedMessages: 1,
						usage: {
							input: 4,
							output: 2,
							cacheRead: 0,
							cacheWrite: 0,
							totalTokens: 6,
							cost: { input: 0.004, output: 0.002, cacheRead: 0, cacheWrite: 0, total: 0.006 },
						},
					};
				},
			},
		});

		const events = await collect(
			kernel.run({
				runId: "run-compacted",
				input: input("input-compacted"),
				systemPrompt: "Continue.",
				tools: [],
				modelProfileId: "default",
				maximumTurns: 2,
			}),
		);

		expect(capturedContext?.messages[0]).toMatchObject({
			role: "user",
			content: expect.stringContaining("<conversation-summary>"),
		});
		const persisted = await conversationStore.read(session);
		expect(persisted.revision).toBe(3);
		expect(JSON.stringify(persisted.messages)).not.toContain("old history");
		expect(JSON.stringify(persisted.messages)).toContain("The old request was completed.");
		expect(events.find((event) => event.type === "run.completed")).toMatchObject({
			usage: { input: 14, output: 6, totalTokens: 22, cost: { total: 0.036 } },
		});
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

import { createHash } from "node:crypto";

import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type { AgentTool, StreamFn } from "@earendil-works/pi-agent-core";
import {
	type Api,
	type Context,
	createAssistantMessageEventStream,
	createModels,
	fauxAssistantMessage,
	fauxProvider,
	type Model,
} from "@earendil-works/pi-ai";
import {
	createPiModelsDeferredResponseAdapter,
	DurableTranscript,
	InMemoryTranscriptStore,
	type TranscriptStore,
} from "@opengameagent/agent";
import { GameInteractionService, type InteractionRequest } from "@opengameagent/extensions";
import type {
	CanonicalEvent,
	CanonicalEventData,
	CanonicalEventStore,
	CanonicalItem,
	StreamKey,
} from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import {
	ActorReplacementBusyError,
	GameRuntime,
	InMemoryEventStore,
	RunInputConflictError,
	RuntimeDeferredBusyError,
	RuntimeDeferredNotFoundError,
	RuntimeDeferredPendingError,
	RuntimeDeferredResumeUnavailableError,
	RuntimeInputLimitError,
} from "../src/index.js";
import {
	allowAllTestTools,
	assistant,
	controlledResponse,
	deferred,
	messageResponse,
	scriptedStream,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
	tool,
	toolCall,
} from "./pi-fakes.js";

const STREAM: StreamKey = { sessionId: "session", actorId: "actor" };

class BlockingEventStore extends InMemoryEventStore {
	blockedType: CanonicalEventData["type"] | undefined;
	allowedBlockedAppends = 0;

	override append(...parameters: Parameters<CanonicalEventStore["append"]>) {
		if (parameters[3].type === this.blockedType) {
			if (this.allowedBlockedAppends === 0) throw new Error("event sink blocked");
			this.allowedBlockedAppends -= 1;
		}
		return super.append(...parameters);
	}
}

class PausingEventStore extends InMemoryEventStore {
	pausedType: CanonicalEventData["type"] | undefined;
	readonly appendEntered = deferred();
	readonly appendRelease = deferred();

	override async append(...parameters: Parameters<CanonicalEventStore["append"]>) {
		if (parameters[3].type === this.pausedType) {
			this.appendEntered.resolve();
			await this.appendRelease.promise;
		}
		return super.append(...parameters);
	}
}

async function createRuntime(
	modelStream: StreamFn,
	options: {
		model?: Model<Api>;
		toolSource?: () => readonly AgentTool[] | Promise<readonly AgentTool[]>;
	} = {},
): Promise<GameRuntime> {
	let eventId = 0;
	let agentId = 0;
	const runtime = new GameRuntime(
		new InMemoryEventStore({
			newEventId: () => {
				eventId += 1;
				return `event-${eventId}`;
			},
			now: () => "2026-08-28T00:00:00.000Z",
		}),
		{ maxActiveActors: 1, maxQueuedRuns: 4, maxInputBytes: 4_096, maxInputDepth: 8 },
	);
	await runtime.registerActor({
		...STREAM,
		model: options.model ?? TEST_MODEL,
		modelStream,
		newId: () => {
			agentId += 1;
			return `agent-${agentId}`;
		},
		now: () => 1,
		...(options.toolSource
			? { toolSource: options.toolSource, toolPolicy: allowAllTestTools, toolLimits: TEST_TOOL_LIMITS }
			: {}),
	});
	return runtime;
}

function start(runtime: GameRuntime) {
	return runtime.start({ ...STREAM, inputId: "input", payload: { instruction: "act" } });
}

function completedItems(events: readonly CanonicalEvent[]): CanonicalItem[] {
	return events.flatMap((event) => (event.data.type === "item.completed" ? [event.data.item] : []));
}

function completedAssistantItems(
	events: readonly CanonicalEvent[],
): Array<Extract<CanonicalItem, { itemType: "message" }>> {
	return completedItems(events).filter(
		(item): item is Extract<CanonicalItem, { itemType: "message" }> =>
			item.itemType === "message" && item.role === "assistant",
	);
}

async function readAll(runtime: GameRuntime): Promise<CanonicalEvent[]> {
	const events: CanonicalEvent[] = [];
	let afterRevision = "0";
	while (true) {
		const page = await runtime.readEvents(STREAM, { afterRevision, limit: 100 });
		events.push(...page.events);
		if (!page.hasMore) return events;
		afterRevision = page.cursor;
	}
}

describe("GameRuntime with the Pi Agent", () => {
	it("reports a reconciliation gap after the complete canonical prefix is purged", async () => {
		const events = new InMemoryEventStore({
			newEventId: (() => {
				let eventId = 0;
				return () => `retention-event-${++eventId}`;
			})(),
			now: () => "2026-08-28T00:00:00.000Z",
		});
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
		});
		await runtime.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: () => textResponse("done") });
		await runtime.start({ ...STREAM, inputId: "retention-input", payload: {} }).completion;
		const stored = await events.read(STREAM, { afterRevision: "0", limit: 100 });
		await expect(
			events.purgeTerminalRuns(STREAM, {
				beforeOccurredAt: "2026-08-29T00:00:00.000Z",
				limit: stored.events.length,
				retainRunIds: [],
				retainBudget: { maxCount: 0, maxBytes: 0 },
			}),
		).resolves.toMatchObject({ purgedEventCount: stored.events.length, earliestRevision: null });

		await expect(runtime.replayEvents(STREAM, { afterRevision: "0", limit: 1 })).resolves.toEqual({
			kind: "gap",
			requestedAfterRevision: "0",
			earliestAvailableRevision: null,
			nextAfterRevision: stored.cursor,
			requiresTranscriptReconciliation: true,
		});
	});

	it("preserves the durable transcript and canonical revision across an in-process actor replacement", async () => {
		const events = new InMemoryEventStore();
		let transcriptId = 0;
		const transcriptStore = new InMemoryTranscriptStore();
		const transcript = new DurableTranscript(transcriptStore, {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
			newRecordId: () => `replacement-transcript-${++transcriptId}`,
		});
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
			newRunId: (() => {
				let runId = 0;
				return () => `replacement-run-${++runId}`;
			})(),
		});
		await runtime.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: () => textResponse("first reply") });
		await runtime.start({ ...STREAM, inputId: "first-input", payload: { instruction: "first" } }).completion;

		const replacementContexts: Context[] = [];
		await runtime.replaceActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: (_model, context) => {
				replacementContexts.push(structuredClone(context));
				return textResponse("second reply");
			},
		});
		await runtime.start({ ...STREAM, inputId: "second-input", payload: { instruction: "second" } }).completion;

		expect(replacementContexts[0]?.messages.map((message) => message.role)).toEqual(["user", "assistant", "user"]);
		expect(replacementContexts[0]?.messages[1]).toMatchObject({
			role: "assistant",
			content: [{ type: "text", text: "first reply" }],
		});
		const page = await runtime.readEvents(STREAM, { afterRevision: "0", limit: 100 });
		expect(page.events.map((event) => event.sequence)).toEqual(page.events.map((_event, index) => String(index + 1)));
		expect(page.events.filter((event) => event.data.type === "run.completed")).toHaveLength(2);
	});

	it("rejects actor replacement while a deferred provider operation remains pending", async () => {
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		const providerHandle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "replacement-pending-operation",
		};
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: providerHandle }),
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "unused" }]),
				cancel: async () => undefined,
			},
		});
		await runtime.start({ ...STREAM, inputId: "deferred-input", payload: {} }).completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");

		await expect(
			runtime.replaceActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => textResponse("replacement"),
			}),
		).rejects.toBeInstanceOf(ActorReplacementBusyError);
		await expect(runtime.abandonDeferred(STREAM, pending.operationId)).resolves.toMatchObject({
			providerCancellation: "completed",
		});
	});

	it("restores committed terminal messages into a fresh Pi Agent", async () => {
		const events = new InMemoryEventStore();
		let transcriptId = 0;
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
			newRecordId: () => {
				transcriptId += 1;
				return `transcript-${transcriptId}`;
			},
		});
		const first = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await first.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: () => textResponse("first reply") });
		await expect(
			first.start({ ...STREAM, inputId: "first-input", payload: { instruction: "first" } }).completion,
		).resolves.toMatchObject({ status: "completed" });

		const providerContexts: Context[] = [];
		const second = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await second.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: (_model, context) => {
				providerContexts.push(structuredClone(context));
				return textResponse("second reply");
			},
		});
		await expect(
			second.start({ ...STREAM, inputId: "second-input", payload: { instruction: "second" } }).completion,
		).resolves.toMatchObject({ status: "completed" });

		expect(providerContexts).toHaveLength(1);
		expect(providerContexts[0]?.messages.map((message) => message.role)).toEqual(["user", "assistant", "user"]);
		expect(providerContexts[0]?.messages[0]).toMatchObject({
			role: "user",
			content: JSON.stringify({ instruction: "first" }),
		});
		expect(providerContexts[0]?.messages[1]).toMatchObject({
			role: "assistant",
			content: [{ type: "text", text: "first reply" }],
		});
	});

	it("restores same-model Pi replay metadata without exposing it through canonical events", async () => {
		const events = new InMemoryEventStore();
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const signed = {
			...assistant(
				[
					{
						type: "thinking" as const,
						thinking: "reasoning",
						thinkingSignature: "thinking-signature-private",
					},
					{ type: "text" as const, text: "first reply", textSignature: "text-signature-private" },
				],
				"stop",
			),
			responseModel: "resolved-model-private",
			responseId: "response-id-private",
		};
		const first = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await first.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: () => messageResponse(signed) });
		await expect(
			first.start({ ...STREAM, inputId: "first-signed", payload: { instruction: "first" } }).completion,
		).resolves.toMatchObject({ status: "completed" });

		const providerContexts: Context[] = [];
		const second = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await second.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: (_model, context) => {
				providerContexts.push(structuredClone(context));
				return textResponse("second reply");
			},
		});
		await expect(
			second.start({ ...STREAM, inputId: "second-signed", payload: { instruction: "second" } }).completion,
		).resolves.toMatchObject({ status: "completed" });

		expect(providerContexts[0]?.messages[1]).toMatchObject(signed);
		const canonical = JSON.stringify(await readAll(second));
		expect(canonical).not.toContain("thinking-signature-private");
		expect(canonical).not.toContain("text-signature-private");
		expect(canonical).not.toContain("response-id-private");
		expect(canonical).not.toContain("resolved-model-private");
	});

	it("restores the committed user turn without an aborted partial assistant", async () => {
		const events = new InMemoryEventStore();
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		let markPartial: (() => void) | undefined;
		const partial = new Promise<void>((resolve) => {
			markPartial = resolve;
		});
		const partialStreamFn: StreamFn = (_model, _context, options) => {
			const stream = createAssistantMessageEventStream();
			stream.push({ type: "start", partial: assistant([], "pending") });
			stream.push({
				type: "text_start",
				contentIndex: 0,
				partial: assistant([{ type: "text", text: "" }], "pending"),
			});
			stream.push({
				type: "text_delta",
				contentIndex: 0,
				delta: "partial",
				partial: assistant([{ type: "text", text: "partial" }], "pending"),
			});
			markPartial?.();
			options?.signal?.addEventListener(
				"abort",
				() => {
					stream.push({
						type: "error",
						reason: "aborted",
						error: { ...assistant([{ type: "text", text: "partial" }], "aborted"), errorMessage: "aborted" },
					});
				},
				{ once: true },
			);
			return stream;
		};
		const first = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await first.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: partialStreamFn });
		const run = first.start({ ...STREAM, inputId: "first-input", payload: { instruction: "first" } });
		await partial;
		const active = first.readActiveCoordinate(STREAM);
		if (!active) throw new Error("Expected an active coordinate.");
		await expect(first.control(active, { type: "abort" })).resolves.toMatchObject({ accepted: true });
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });
		await expect(
			first.start({ ...STREAM, inputId: "poisoned-input", payload: { instruction: "must not continue" } }).completion,
		).resolves.toMatchObject({ status: "failed" });

		const providerContexts: Context[] = [];
		const second = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await second.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: (_model, context) => {
				providerContexts.push(structuredClone(context));
				return textResponse("second reply");
			},
		});
		await expect(
			second.start({ ...STREAM, inputId: "second-input", payload: { instruction: "second" } }).completion,
		).resolves.toMatchObject({ status: "completed" });

		expect(providerContexts[0]?.messages.map((message) => message.role)).toEqual(["user", "user"]);
		expect(JSON.stringify(providerContexts[0])).not.toContain("partial");
	});

	it("persists and completes a Tool turn when durable transcripts are enabled", async () => {
		let executeCount = 0;
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: scriptedStream([
				() => messageResponse(assistant([toolCall("call-act", "act")], "toolUse")),
				() => textResponse("done"),
			]),
			toolSource: () => [
				tool("act", async () => {
					executeCount += 1;
					return { content: [{ type: "text", text: "acted" }], details: {} };
				}),
			],
			toolPolicy: allowAllTestTools,
			toolLimits: TEST_TOOL_LIMITS,
		});

		await expect(
			runtime.start({ ...STREAM, inputId: "input", payload: { instruction: "act" } }).completion,
		).resolves.toMatchObject({ status: "completed" });
		expect(executeCount).toBe(1);
	});

	it("rejects unsupported durable media before it can diverge live and restored Pi state", async () => {
		const image = Buffer.from("durable image fixture");
		const imageBase64 = image.toString("base64");
		const imageReference = {
			type: "image-reference" as const,
			attachmentId: `img_${createHash("sha256").update(image).digest("hex")}`,
			mediaType: "image/png" as const,
			bytes: image.byteLength,
			width: 1,
			height: 1,
		};
		const events = new InMemoryEventStore();
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const liveContexts: Context[] = [];
		const actor: Parameters<GameRuntime["registerActor"]>[0] = {
			...STREAM,
			model: { ...TEST_MODEL, input: ["text", "image"] },
			modelStream: ((_model, context) => {
				liveContexts.push(structuredClone(context));
				return textResponse("seen");
			}) satisfies StreamFn,
			projectInput: (input) =>
				(input.payload as { instruction: string }).instruction === "image"
					? {
							message: {
								role: "user" as const,
								content: [{ type: "image" as const, data: imageBase64, mimeType: "image/png" }],
								timestamp: 1,
							},
							transcriptContent: [imageReference],
						}
					: {
							message: { role: "user" as const, content: "next", timestamp: 1 },
							transcriptContent: [{ type: "text" as const, text: "next" }],
						},
			inputProjectionLimits: {
				maxContentParts: 1,
				maxTextBytes: 4,
				maxReferenceBytes: Buffer.byteLength(JSON.stringify(imageReference), "utf8"),
				maxImageBytes: image.byteLength,
				maxTotalImageBytes: image.byteLength,
			},
		};
		const first = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await first.registerActor(actor);
		await expect(
			first.start({ ...STREAM, inputId: "image-input", payload: { instruction: "image" } }).completion,
		).resolves.toMatchObject({ status: "failed" });
		await expect(
			first.start({ ...STREAM, inputId: "text-input", payload: { instruction: "text" } }).completion,
		).resolves.toMatchObject({ status: "completed" });

		expect(liveContexts).toHaveLength(1);
		expect(liveContexts[0]?.messages.map((message) => message.role)).toEqual(["user"]);
		expect(JSON.stringify(liveContexts[0])).not.toContain(imageBase64);

		const restoredContexts: Context[] = [];
		const restored = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await restored.registerActor({
			...actor,
			modelStream: (_model, context) => {
				restoredContexts.push(structuredClone(context));
				return textResponse("restored");
			},
		});
		await expect(
			restored.start({ ...STREAM, inputId: "restored-input", payload: { instruction: "text" } }).completion,
		).resolves.toMatchObject({ status: "completed" });
		expect(restoredContexts[0]?.messages.map((message) => message.role)).toEqual(["user", "assistant", "user"]);
		expect(JSON.stringify(restoredContexts[0])).not.toContain(imageBase64);
	});

	it("rejects unauthorized durable media during actor registration before any provider call", async () => {
		const bytes = Buffer.from("authorized-on-write");
		const reference = {
			type: "image-reference" as const,
			attachmentId: `img_${createHash("sha256").update(bytes).digest("hex")}`,
			mediaType: "image/png" as const,
			bytes: bytes.byteLength,
			width: 1,
			height: 1,
		};
		const store = new InMemoryTranscriptStore();
		const writer = new DurableTranscript(store, {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
			media: {
				captureToolResult: async () => {
					throw new Error("unused");
				},
				read: async () => [{ type: "image", data: bytes.toString("base64"), mimeType: "image/png" }],
			},
		});
		await writer.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: { ...STREAM, runId: "stored-media", turn: "0" },
					message: { role: "user", contentFormat: "parts", content: [reference], timestamp: 1 },
				},
			],
		});
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
			media: {
				captureToolResult: async () => {
					throw new Error("unused");
				},
				read: async () => {
					throw new Error("attachment authorization denied");
				},
			},
		});
		let providerCalls = 0;
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});

		await expect(
			runtime.registerActor({
				...STREAM,
				model: { ...TEST_MODEL, input: ["text", "image"] },
				modelStream: () => {
					providerCalls += 1;
					return textResponse("must not run");
				},
			}),
		).rejects.toThrow("authorization denied");
		expect(providerCalls).toBe(0);
	});

	it("preflights batch actor registration before exposing any actor", async () => {
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 1_024,
			maxInputDepth: 4,
		});
		const actor = { ...STREAM, model: TEST_MODEL, modelStream: () => textResponse("done") };
		await expect(runtime.registerActors([actor, actor])).rejects.toThrow();
		await expect(runtime.registerActor(actor)).resolves.toBeUndefined();
	});

	it("requires and enforces host-owned in-process input budgets", async () => {
		expect(
			() =>
				new GameRuntime(new InMemoryEventStore(), {
					maxActiveActors: 1,
					maxQueuedRuns: 1,
					maxInputBytes: 0,
					maxInputDepth: 1,
				}),
		).toThrow(TypeError);
		const store = new InMemoryEventStore();
		let runIds = 0;
		let providerCalls = 0;
		const deepPayload = { a: { b: true } };
		const maxInputBytes = Buffer.byteLength(JSON.stringify(deepPayload));
		const runtime = new GameRuntime(store, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes,
			maxInputDepth: 2,
			newRunId: () => `run-${++runIds}`,
		});
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => {
				providerCalls += 1;
				return textResponse("accepted");
			},
		});

		for (const testCase of [
			{ payload: deepPayload, code: "input_too_deep", limit: 2, causeCode: "json_too_deep" },
			{
				payload: { text: "x".repeat(maxInputBytes) },
				code: "input_too_large",
				limit: maxInputBytes,
				causeCode: "json_too_large",
			},
		]) {
			const input = { ...STREAM, inputId: "input", payload: testCase.payload };
			expect(() => runtime.start(input)).toThrow(RuntimeInputLimitError);
			await expect(runtime.submit(input)).rejects.toMatchObject({
				name: "RuntimeInputLimitError",
				code: testCase.code,
				limit: testCase.limit,
				cause: { code: testCase.causeCode },
			});
		}
		expect({ runIds, providerCalls }).toEqual({ runIds: 0, providerCalls: 0 });
		expect(await store.readRun(STREAM, "input")).toBeUndefined();
		expect(await readAll(runtime)).toEqual([]);
		const accepted = await runtime.submit({ ...STREAM, inputId: "input", payload: {} });
		await expect(accepted.completion).resolves.toMatchObject({ status: "completed" });
		expect({ runIds, providerCalls }).toEqual({ runIds: 1, providerCalls: 1 });
	});

	it("projects a streamed text reply into canonical events", async () => {
		const runtime = await createRuntime(() => textResponse("hello", ["hel", "lo"]));
		const run = start(runtime);

		await expect(run.completion).resolves.toMatchObject({ runId: run.runId, status: "completed" });
		const events = await readAll(runtime);
		const assistantMessage = completedItems(events).find(
			(item): item is Extract<CanonicalItem, { itemType: "message" }> =>
				item.itemType === "message" && item.role === "assistant",
		);

		expect(events.slice(0, 2).map((event) => event.data)).toEqual([
			{
				type: "run.queued",
				inputId: "input",
				inputFingerprint: "98286554df3948844bf66de8226ddc25baa866d1fe564a9cbc74d6c885fc5094",
			},
			{ type: "run.started", inputId: "input" },
		]);
		expect(events.map((event) => event.sequence)).toEqual(events.map((_event, index) => String(index + 1)));
		expect(assistantMessage?.content).toEqual([{ type: "text", text: "hello", sourceIndex: 0 }]);
		expect(events.some((event) => event.data.type === "item.delta" && event.data.delta.text === "hel")).toBe(true);
		expect(events.at(-1)?.data).toEqual({ type: "run.completed", status: "completed" });
	});

	it("treats duplicate runtime subscriptions as one Pi-style set entry", async () => {
		const runtime = await createRuntime(() => textResponse("done"));
		let completedCalls = 0;
		let resolveFirst: (() => void) | undefined;
		const firstObserved = new Promise<void>((resolve) => {
			resolveFirst = resolve;
		});
		const listener = (event: CanonicalEvent) => {
			if (event.data.type !== "run.completed") return;
			completedCalls += 1;
			resolveFirst?.();
		};
		const unsubscribeFirst = runtime.subscribe(STREAM, listener);
		const unsubscribeSecond = runtime.subscribe(STREAM, listener);

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
		await firstObserved;
		expect(completedCalls).toBe(1);

		unsubscribeSecond();
		await expect(
			runtime.start({ ...STREAM, inputId: "input-subscription-2", payload: { instruction: "act again" } }).completion,
		).resolves.toMatchObject({ status: "completed" });
		await new Promise<void>((resolve) => setImmediate(resolve));
		expect(completedCalls).toBe(1);
		unsubscribeFirst();
	});

	it("runs immediate projections before completion without granting them event authority", async () => {
		const runtime = await createRuntime(() => textResponse("done"));
		const observed: CanonicalEventData["type"][] = [];
		const observedDeltas: string[] = [];
		runtime.subscribeImmediate(STREAM, (event) => {
			if (event.data.type !== "item.delta" || event.data.delta.type !== "text") return;
			(event.data.delta as { text: string }).text = "mutated";
		});
		runtime.subscribeImmediate(STREAM, async () => {
			throw new Error("async observer failed");
		});
		const unsubscribe = runtime.subscribeImmediate(STREAM, (event) => {
			observed.push(event.data.type);
			if (event.data.type === "item.delta" && event.data.delta.type === "text") {
				observedDeltas.push(event.data.delta.text);
			}
			if (event.data.type === "item.delta") throw new Error("observer failed");
		});

		const run = start(runtime);
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });
		expect(observed.at(-1)).toBe("run.completed");
		expect(observedDeltas).toEqual(["done"]);
		expect((await readAll(runtime)).at(-1)?.data).toEqual({ type: "run.completed", status: "completed" });

		unsubscribe();
	});

	it("fails Pi deferred completion when no durable pending-operation lifecycle is configured", async () => {
		const runtime = await createRuntime(() =>
			messageResponse(assistant([{ type: "text", text: "queued" }], "deferred")),
		);

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "failed" });
		const events = await readAll(runtime);
		expect(events.some((event) => event.data.type === "turn.completed" && event.data.finishReason === "deferred")).toBe(
			false,
		);
		expect(events.at(-1)?.data).toEqual({ type: "run.completed", status: "failed" });
	});

	it("persists deferred output without an Adapter and blocks new input instead of reporting false completion", async () => {
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		const faux = fauxProvider({ api: "test-api", provider: "provider", deferred: { pendingFetches: 1 } });
		const models = createModels();
		models.setProvider(faux.provider);
		faux.setResponses([fauxAssistantMessage("ready")]);
		let calls = 0;
		await runtime.registerActor({
			...STREAM,
			model: faux.getModel(),
			modelStream: (model, context, options) => {
				calls += 1;
				return calls === 1
					? models.streamSimple(model, context, { ...options, deferred: true })
					: messageResponse({
							...assistant([{ type: "text", text: "next" }]),
							api: model.api,
							provider: model.provider,
							model: model.id,
						});
			},
		});

		await expect(
			runtime.start({ ...STREAM, inputId: "deferred-input", payload: { instruction: "defer" } }).completion,
		).resolves.toMatchObject({ status: "deferred" });
		expect(() => runtime.start({ ...STREAM, inputId: "blocked-input", payload: { instruction: "retry" } })).toThrow(
			RuntimeDeferredPendingError,
		);
		const restored = await transcript.restore(STREAM);
		expect(restored.messages.map((message) => message.role)).toEqual(["user"]);
		expect(restored.pendingDeferred).toMatchObject({ input: { inputId: "deferred-input" } });
		expect(JSON.stringify(restored.messages)).not.toContain("queued");

		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		await expect(runtime.resumeDeferred(STREAM, "stale-operation")).rejects.toBeInstanceOf(
			RuntimeDeferredNotFoundError,
		);
		await expect(runtime.resumeDeferred(STREAM, pending.operationId)).rejects.toBeInstanceOf(
			RuntimeDeferredResumeUnavailableError,
		);
		expect(runtime.readDeferred(STREAM)).toMatchObject({ operationId: pending.operationId });
		expect((await readAll(runtime)).some((event) => event.data.type === "run.resumed")).toBe(false);
		await expect(runtime.abandonDeferred(STREAM, pending.operationId)).resolves.toEqual({
			operationId: pending.operationId,
			providerCancellation: "unavailable",
		});
		expect(runtime.readDeferred(STREAM)).toBeUndefined();
		await expect(
			runtime.start({ ...STREAM, inputId: "next-input", payload: { instruction: "continue" } }).completion,
		).resolves.toMatchObject({ status: "completed" });
	});

	it("restores and explicitly resumes one suspended run through the Pi Models Adapter", async () => {
		const eventStore = new InMemoryEventStore();
		const history = await eventStore.append(
			STREAM,
			"empty",
			{ ...STREAM, runId: "earlier-run", turn: "0" },
			{ type: "turn.started" },
		);
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const faux = fauxProvider({ api: "test-api", provider: "provider" });
		const models = createModels();
		models.setProvider(faux.provider);
		faux.setResponses([fauxAssistantMessage("ready")]);
		let ordinaryCalls = 0;
		const actor = {
			...STREAM,
			model: faux.getModel(),
			modelStream: ((model, context, options) => {
				ordinaryCalls += 1;
				return models.streamSimple(model, context, { ...options, deferred: true });
			}) satisfies StreamFn,
			deferredResponses: createPiModelsDeferredResponseAdapter(models),
		};
		const options = {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		};
		const first = new GameRuntime(eventStore, options);
		await first.registerActor(actor);
		const input = { ...STREAM, inputId: "deferred-input", payload: { instruction: "wait" } };
		const initial = await first.submit(input);
		expect(initial.eventCursor).toBe(history.revision);
		const suspended = await initial.completion;
		expect(suspended).toMatchObject({ runId: initial.runId, status: "deferred" });
		if (suspended.status !== "deferred") throw new Error("Expected a suspended run.");
		const repeated = await first.submit(input);
		expect(repeated.eventCursor).toBe(initial.eventCursor);
		await expect(repeated.completion).resolves.toEqual(suspended);
		expect(ordinaryCalls).toBe(1);

		const restarted = new GameRuntime(eventStore, options);
		await restarted.registerActor(actor);
		expect(restarted.readDeferred(STREAM)).toMatchObject({ operationId: suspended.operationId });
		expect(() => restarted.start({ ...STREAM, inputId: "other-input", payload: { instruction: "other" } })).toThrow(
			RuntimeDeferredPendingError,
		);

		const selector = { ...STREAM };
		const resuming = restarted.resumeDeferred(selector, suspended.operationId);
		selector.sessionId = "changed-session";
		selector.actorId = "changed-actor";
		const resumed = await resuming;
		expect(resumed.eventCursor).toBe(initial.eventCursor);
		await expect(resumed.completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(faux.state.deferredFetchCount).toBe(1);
		expect(ordinaryCalls).toBe(1);
		expect(restarted.readDeferred(STREAM)).toBeUndefined();
		const events = await readAll(restarted);
		expect(events.some((event) => event.data.type === "run.resumed")).toBe(true);
		expect(events.at(-1)?.data).toEqual({ type: "run.completed", status: "completed" });
	});

	it("repairs deferred canonical projection gaps from the durable transcript on registration", async () => {
		const eventStore = new BlockingEventStore();
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const faux = fauxProvider({ api: "test-api", provider: "provider", deferred: { pendingFetches: 1 } });
		const models = createModels();
		models.setProvider(faux.provider);
		faux.setResponses([fauxAssistantMessage("ready")]);
		const actor = {
			...STREAM,
			model: faux.getModel(),
			modelStream: ((model, context, options) =>
				models.streamSimple(model, context, { ...options, deferred: true })) satisfies StreamFn,
			deferredResponses: createPiModelsDeferredResponseAdapter(models),
		};
		const options = {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		};
		eventStore.blockedType = "run.suspended";
		const first = new GameRuntime(eventStore, options);
		await first.registerActor(actor);
		await expect(
			first.start({ ...STREAM, inputId: "deferred-input", payload: { instruction: "wait" } }).completion,
		).resolves.toMatchObject({ status: "deferred" });
		await expect(eventStore.readRun(STREAM, "deferred-input")).resolves.toMatchObject({ status: null });

		eventStore.blockedType = undefined;
		const restored = new GameRuntime(eventStore, options);
		await restored.registerActor(actor);
		await expect(eventStore.readRun(STREAM, "deferred-input")).resolves.toMatchObject({ status: "deferred" });
		const pending = restored.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a restored deferred operation.");

		eventStore.blockedType = "run.completed";
		await expect(restored.abandonDeferred(STREAM, pending.operationId)).rejects.toThrow("event sink blocked");
		await expect(durable.restore(STREAM)).resolves.not.toHaveProperty("pendingDeferred");
		await expect(eventStore.readRun(STREAM, "deferred-input")).resolves.toMatchObject({ status: "deferred" });

		eventStore.blockedType = undefined;
		await expect(
			(
				await restored.submit({
					...STREAM,
					inputId: "deferred-input",
					payload: { instruction: "wait" },
				})
			).completion,
		).resolves.toMatchObject({ status: "aborted" });
		await expect(eventStore.readRun(STREAM, "deferred-input")).resolves.toMatchObject({ status: "aborted" });

		const reconciled = new GameRuntime(eventStore, options);
		await reconciled.registerActor(actor);
		await expect(eventStore.readRun(STREAM, "deferred-input")).resolves.toMatchObject({ status: "aborted" });
		await expect(
			(await reconciled.submit({ ...STREAM, inputId: "deferred-input", payload: { instruction: "wait" } })).completion,
		).resolves.toMatchObject({ status: "aborted" });
	});

	it("does not reconcile a deferred terminal gap while a later run is active", async () => {
		const eventStore = new BlockingEventStore();
		const transcriptStore = new InMemoryTranscriptStore();
		const transcript = new DurableTranscript(transcriptStore, {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const controlled = controlledResponse();
		const providerHandle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "provider-operation",
		};
		let providerCalls = 0;
		const runtime = new GameRuntime(eventStore, {
			maxActiveActors: 1,
			maxQueuedRuns: 2,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: (model, context, options) => {
				providerCalls += 1;
				return providerCalls === 1
					? messageResponse({ ...assistant([], "deferred"), deferred: providerHandle })
					: controlled.streamFn(model, context, options);
			},
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "ready" }]),
				cancel: async () => undefined,
			},
		});

		const deferredInput = { ...STREAM, inputId: "deferred-input", payload: { instruction: "wait" } };
		const initial = runtime.start(deferredInput);
		const later = runtime.start({ ...STREAM, inputId: "later-input", payload: { instruction: "later" } });
		await expect(initial.completion).resolves.toMatchObject({ status: "deferred" });
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		eventStore.blockedType = "run.completed";
		await expect((await runtime.resumeDeferred(STREAM, pending.operationId)).completion).resolves.toMatchObject({
			status: "completed",
		});
		await controlled.started;
		eventStore.blockedType = undefined;
		const recordsBeforeRetry = (await transcriptStore.load(STREAM, 131_072)).records;

		await expect(runtime.submit({ ...deferredInput, payload: { instruction: "different" } })).rejects.toBeInstanceOf(
			RunInputConflictError,
		);
		await expect(runtime.submit(deferredInput)).rejects.toBeInstanceOf(RuntimeDeferredBusyError);
		expect((await transcriptStore.load(STREAM, 131_072)).records).toEqual(recordsBeforeRetry);

		controlled.finish();
		await expect(later.completion).resolves.toMatchObject({ status: "completed" });
		await expect((await runtime.submit(deferredInput)).completion).resolves.toMatchObject({ status: "completed" });
		await expect(eventStore.readRun(STREAM, deferredInput.inputId)).resolves.toMatchObject({ status: "completed" });
	});

	it("repairs a missing suspension before resuming in the same runtime", async () => {
		const eventStore = new BlockingEventStore();
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const handle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "provider-operation",
		};
		const runtime = new GameRuntime(eventStore, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		});
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: handle }),
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "ready" }]),
				cancel: async () => undefined,
			},
		});
		eventStore.blockedType = "run.suspended";
		await expect(runtime.start({ ...STREAM, inputId: "input", payload: {} }).completion).resolves.toMatchObject({
			status: "deferred",
		});
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");

		eventStore.blockedType = undefined;
		await expect((await runtime.resumeDeferred(STREAM, pending.operationId)).completion).resolves.toMatchObject({
			status: "completed",
		});
		const lifecycle = (await readAll(runtime))
			.map((event) => event.data.type)
			.filter((type) => type === "run.suspended" || type === "run.resumed" || type === "run.completed");
		expect(lifecycle).toEqual(["run.suspended", "run.resumed", "run.completed"]);
	});

	it("repairs a missing resumed terminal from its durable run fact after restart", async () => {
		const eventStore = new BlockingEventStore();
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const handle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "provider-operation",
		};
		const actor = {
			...STREAM,
			model: TEST_MODEL,
			modelStream: (() => messageResponse({ ...assistant([], "deferred"), deferred: handle })) satisfies StreamFn,
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "ready" }]),
				cancel: async () => undefined,
			},
		};
		const options = {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		};
		const first = new GameRuntime(eventStore, options);
		await first.registerActor(actor);
		await first.start({ ...STREAM, inputId: "input", payload: {} }).completion;
		const pending = first.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		eventStore.blockedType = "run.completed";
		await expect((await first.resumeDeferred(STREAM, pending.operationId)).completion).resolves.toMatchObject({
			status: "completed",
		});
		await expect(durable.restore(STREAM)).resolves.toMatchObject({
			terminalDeferredRuns: [{ operationId: pending.operationId, status: "completed" }],
		});
		await expect(eventStore.readRun(STREAM, "input")).resolves.toMatchObject({ status: null });

		eventStore.blockedType = undefined;
		await expect((await first.submit({ ...STREAM, inputId: "input", payload: {} })).completion).resolves.toMatchObject({
			status: "completed",
		});
		await expect(eventStore.readRun(STREAM, "input")).resolves.toMatchObject({ status: "completed" });

		const restarted = new GameRuntime(eventStore, options);
		await restarted.registerActor(actor);
		await expect(eventStore.readRun(STREAM, "input")).resolves.toMatchObject({ status: "completed" });
	});

	it("repairs a missing resumed terminal before committing an in-process actor replacement", async () => {
		const eventStore = new BlockingEventStore();
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const handle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "replacement-provider-operation",
		};
		const runtime = new GameRuntime(eventStore, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		});
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: handle }),
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "ready" }]),
				cancel: async () => undefined,
			},
		});
		await runtime.start({ ...STREAM, inputId: "replacement-input", payload: {} }).completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		eventStore.blockedType = "run.completed";
		await expect((await runtime.resumeDeferred(STREAM, pending.operationId)).completion).resolves.toMatchObject({
			status: "completed",
		});
		await expect(eventStore.readRun(STREAM, "replacement-input")).resolves.toMatchObject({ status: null });

		eventStore.blockedType = undefined;
		await runtime.replaceActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => textResponse("replacement reply"),
		});

		await expect(eventStore.readRun(STREAM, "replacement-input")).resolves.toMatchObject({ status: "completed" });
	});

	it("keeps the displaced actor on the repaired revision when replacement reconciliation fails partway", async () => {
		const eventStore = new BlockingEventStore();
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		let providerOperation = 0;
		const actor = {
			...STREAM,
			model: TEST_MODEL,
			modelStream: (() =>
				messageResponse({
					...assistant([], "deferred"),
					deferred: {
						provider: TEST_MODEL.provider,
						modelId: TEST_MODEL.id,
						api: TEST_MODEL.api,
						id: `rollback-operation-${++providerOperation}`,
					},
				})) satisfies StreamFn,
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "ready" }]),
				cancel: async () => undefined,
			},
		};
		const runtime = new GameRuntime(eventStore, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		});
		await runtime.registerActor(actor);
		eventStore.blockedType = "run.completed";
		for (const inputId of ["rollback-input-1", "rollback-input-2"]) {
			await runtime.start({ ...STREAM, inputId, payload: {} }).completion;
			const pending = runtime.readDeferred(STREAM);
			if (!pending) throw new Error("Expected a pending deferred operation.");
			await expect((await runtime.resumeDeferred(STREAM, pending.operationId)).completion).resolves.toMatchObject({
				status: "completed",
			});
		}
		eventStore.allowedBlockedAppends = 1;

		await expect(
			runtime.replaceActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => textResponse("unused replacement"),
			}),
		).rejects.toThrow("event sink blocked");
		await expect(eventStore.readRun(STREAM, "rollback-input-1")).resolves.toMatchObject({ status: "completed" });
		await expect(eventStore.readRun(STREAM, "rollback-input-2")).resolves.toMatchObject({ status: null });

		eventStore.blockedType = undefined;
		await expect(
			runtime.start({ ...STREAM, inputId: "after-replacement-rollback", payload: {} }).completion,
		).resolves.toMatchObject({ status: "deferred" });
	});

	it("repairs every terminal deferred run while retaining the newest pending operation", async () => {
		const eventStore = new BlockingEventStore();
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		let call = 0;
		const actor = {
			...STREAM,
			model: TEST_MODEL,
			modelStream: (() => {
				call += 1;
				return messageResponse({
					...assistant([], "deferred"),
					deferred: {
						provider: TEST_MODEL.provider,
						modelId: TEST_MODEL.id,
						api: TEST_MODEL.api,
						id: `provider-operation-${call}`,
					},
				});
			}) satisfies StreamFn,
		};
		const options = {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		};
		const first = new GameRuntime(eventStore, options);
		await first.registerActor(actor);
		await first.start({ ...STREAM, inputId: "input-1", payload: {} }).completion;
		const firstPending = first.readDeferred(STREAM);
		if (!firstPending) throw new Error("Expected the first pending operation.");
		eventStore.blockedType = "run.completed";
		await expect(first.abandonDeferred(STREAM, firstPending.operationId)).rejects.toThrow("event sink blocked");

		eventStore.blockedType = undefined;
		await first.start({ ...STREAM, inputId: "input-2", payload: {} }).completion;
		const secondPending = first.readDeferred(STREAM);
		if (!secondPending) throw new Error("Expected the second pending operation.");
		await expect(eventStore.readRun(STREAM, "input-1")).resolves.toMatchObject({ status: "deferred" });

		const restarted = new GameRuntime(eventStore, options);
		await restarted.registerActor(actor);
		await expect(eventStore.readRun(STREAM, "input-1")).resolves.toMatchObject({ status: "aborted" });
		await expect(eventStore.readRun(STREAM, "input-2")).resolves.toMatchObject({
			status: "deferred",
			operationId: secondPending.operationId,
		});
		expect(restarted.readDeferred(STREAM)).toMatchObject({ operationId: secondPending.operationId });
	});

	it("unblocks the actor lane when abandonment is durable but its terminal event fails", async () => {
		const eventStore = new BlockingEventStore();
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const faux = fauxProvider({ api: "test-api", provider: "provider", deferred: { pendingFetches: 1 } });
		const models = createModels();
		models.setProvider(faux.provider);
		faux.setResponses([fauxAssistantMessage("ready")]);
		let calls = 0;
		const runtime = new GameRuntime(eventStore, {
			maxActiveActors: 1,
			maxQueuedRuns: 2,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		});
		await runtime.registerActor({
			...STREAM,
			model: faux.getModel(),
			modelStream: (model, context, options) => {
				calls += 1;
				return calls === 1
					? models.streamSimple(model, context, { ...options, deferred: true })
					: messageResponse({
							...assistant([{ type: "text", text: "next" }]),
							api: model.api,
							provider: model.provider,
							model: model.id,
						});
			},
		});
		const deferred = runtime.start({ ...STREAM, inputId: "deferred-input", payload: {} });
		const queued = runtime.start({ ...STREAM, inputId: "queued-input", payload: {} });
		await deferred.completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");

		eventStore.blockedType = "run.completed";
		await expect(runtime.abandonDeferred(STREAM, pending.operationId)).rejects.toThrow("event sink blocked");
		eventStore.blockedType = undefined;
		await expect(queued.completion).resolves.toMatchObject({ status: "completed" });
	});

	it("does not reconcile a deferred terminal while abandonment is emitting it", async () => {
		const eventStore = new PausingEventStore();
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const providerHandle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "provider-operation",
		};
		const runtime = new GameRuntime(eventStore, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		});
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: providerHandle }),
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "unused" }]),
				cancel: async () => undefined,
			},
		});
		const input = { ...STREAM, inputId: "input", payload: {} };
		await runtime.start(input).completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");

		eventStore.pausedType = "run.completed";
		const abandonment = runtime.abandonDeferred(STREAM, pending.operationId);
		await eventStore.appendEntered.promise;
		const retryExpectation = expect(runtime.submit(input)).rejects.toBeInstanceOf(RuntimeDeferredBusyError);
		await retryExpectation;
		eventStore.appendRelease.resolve();
		await expect(abandonment).resolves.toMatchObject({ providerCancellation: "completed" });
		await expect((await runtime.submit(input)).completion).resolves.toMatchObject({ status: "aborted" });
		const terminalEvents = (await readAll(runtime)).filter(
			(event) => event.data.type === "run.completed" && event.data.status === "aborted",
		);
		expect(terminalEvents).toHaveLength(1);
	});

	it("pauses already-admitted actor work behind a deferred run and resumes it after completion", async () => {
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		let providerCalls = 0;
		const handle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "provider-operation",
		};
		const deferredMessage = { ...assistant([], "deferred"), deferred: handle };
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 2,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => {
				providerCalls += 1;
				return providerCalls === 1 ? messageResponse(deferredMessage) : textResponse("queued run");
			},
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "resumed" }]),
				cancel: async () => undefined,
			},
		});

		const suspended = runtime.start({ ...STREAM, inputId: "first", payload: { order: 1 } });
		const queued = runtime.start({ ...STREAM, inputId: "second", payload: { order: 2 } });
		await expect(suspended.completion).resolves.toMatchObject({ status: "deferred" });
		await new Promise<void>((resolve) => setImmediate(resolve));
		expect(providerCalls).toBe(1);

		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		await expect((await runtime.resumeDeferred(STREAM, pending.operationId)).completion).resolves.toMatchObject({
			status: "completed",
		});
		await expect(queued.completion).resolves.toMatchObject({ status: "completed" });
		expect(providerCalls).toBe(2);
	});

	it("does not hold the global scheduler while provider cancellation is pending", async () => {
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const eventStore = new InMemoryEventStore();
		const runtime = new GameRuntime(eventStore, {
			maxActiveActors: 1,
			maxQueuedRuns: 2,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		});
		const handle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "provider-operation",
		};
		let releaseCancel: () => void = () => undefined;
		const cancelStarted = new Promise<void>((resolve) => {
			releaseCancel = resolve;
		});
		let otherCalls = 0;
		let deferredActorCalls = 0;
		await runtime.registerActors([
			{
				sessionId: STREAM.sessionId,
				actorId: "deferred-actor",
				model: TEST_MODEL,
				modelStream: () => {
					deferredActorCalls += 1;
					return deferredActorCalls === 1
						? messageResponse({ ...assistant([], "deferred"), deferred: handle })
						: textResponse("queued");
				},
				deferredResponses: {
					fetch: async () => assistant([{ type: "text", text: "unused" }]),
					cancel: async () => cancelStarted,
				},
			},
			{
				sessionId: STREAM.sessionId,
				actorId: "other-actor",
				model: TEST_MODEL,
				modelStream: () => {
					otherCalls += 1;
					return textResponse("other");
				},
			},
		]);
		const deferredStream = { sessionId: STREAM.sessionId, actorId: "deferred-actor" };
		const deferredRun = runtime.start({ ...deferredStream, inputId: "input", payload: {} });
		const queued = runtime.start({ ...deferredStream, inputId: "queued-input", payload: {} });
		await deferredRun.completion;
		const pending = runtime.readDeferred(deferredStream);
		if (!pending) throw new Error("Expected a pending deferred operation.");

		const abandonment = runtime.abandonDeferred(deferredStream, pending.operationId);
		await new Promise<void>((resolve) => setImmediate(resolve));
		const other = runtime.start({
			sessionId: STREAM.sessionId,
			actorId: "other-actor",
			inputId: "other-input",
			payload: {},
		});
		await new Promise<void>((resolve) => setImmediate(resolve));
		expect(otherCalls).toBe(1);
		expect(deferredActorCalls).toBe(2);
		await expect(other.completion).resolves.toMatchObject({ status: "completed" });
		await expect(queued.completion).resolves.toMatchObject({ status: "completed" });
		releaseCancel();
		await expect(abandonment).resolves.toMatchObject({ providerCancellation: "completed" });
	});

	it("settles a queued resume as aborted when the pending operation is abandoned", async () => {
		const durable = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 2,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript: durable,
		});
		const providerHandle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "provider-operation",
		};
		const blocker = controlledResponse();
		await runtime.registerActors([
			{
				sessionId: STREAM.sessionId,
				actorId: "deferred-actor",
				model: TEST_MODEL,
				modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: providerHandle }),
				deferredResponses: {
					fetch: async () => assistant([{ type: "text", text: "unused" }]),
					cancel: async () => undefined,
				},
			},
			{
				sessionId: STREAM.sessionId,
				actorId: "blocker",
				model: TEST_MODEL,
				modelStream: blocker.streamFn,
			},
		]);
		const deferredStream = { sessionId: STREAM.sessionId, actorId: "deferred-actor" };
		await runtime.start({ ...deferredStream, inputId: "input", payload: {} }).completion;
		const pending = runtime.readDeferred(deferredStream);
		if (!pending) throw new Error("Expected a pending deferred operation.");
		const blockingRun = runtime.start({
			sessionId: STREAM.sessionId,
			actorId: "blocker",
			inputId: "blocking-input",
			payload: {},
		});
		await blocker.started;

		const resume = await runtime.resumeDeferred(deferredStream, pending.operationId);
		await expect(
			runtime.replaceActor({
				...deferredStream,
				model: TEST_MODEL,
				modelStream: () => textResponse("replacement"),
			}),
		).rejects.toBeInstanceOf(ActorReplacementBusyError);
		await expect(runtime.abandonDeferred(deferredStream, pending.operationId)).resolves.toMatchObject({
			providerCancellation: "completed",
		});
		await expect(resume.completion).resolves.toEqual({ runId: resume.runId, status: "aborted" });
		blocker.finish();
		await blockingRun.completion;
	});

	it("rejects a resume while deferred abandonment is crossing the runtime boundary", async () => {
		const backing = new InMemoryTranscriptStore();
		let releaseAppend: () => void = () => undefined;
		const appendGate = new Promise<void>((resolve) => {
			releaseAppend = resolve;
		});
		let enterAppend: () => void = () => undefined;
		const appendEntered = new Promise<void>((resolve) => {
			enterAppend = resolve;
		});
		const store: TranscriptStore = {
			load: backing.load.bind(backing),
			append: async (stream, revision, records) => {
				if (records.some((record) => record.type === "deferred.abandoned")) {
					enterAppend();
					await appendGate;
				}
				return backing.append(stream, revision, records);
			},
		};
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		const providerHandle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "provider-operation",
		};
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: providerHandle }),
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "unused" }]),
				cancel: async () => undefined,
			},
		});
		await runtime.start({ ...STREAM, inputId: "input", payload: {} }).completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a pending deferred operation.");

		const abandonment = runtime.abandonDeferred(STREAM, pending.operationId);
		await appendEntered;
		await expect(
			runtime.replaceActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => textResponse("replacement"),
			}),
		).rejects.toBeInstanceOf(ActorReplacementBusyError);
		await expect(runtime.resumeDeferred(STREAM, pending.operationId)).rejects.toBeInstanceOf(RuntimeDeferredBusyError);
		releaseAppend();
		await expect(abandonment).resolves.toMatchObject({ providerCancellation: "completed" });
	});

	it("executes one tool and projects progress plus its final result", async () => {
		const echo = tool("echo", async (_id, _params, _signal, onUpdate) => {
			onUpdate?.({ content: [{ type: "text", text: "working" }], details: {} });
			return { content: [{ type: "text", text: "echoed" }], details: {} };
		});
		const streamFn = scriptedStream([
			() => messageResponse(assistant([toolCall("call-echo", "echo")], "toolUse")),
			() => textResponse("finished"),
		]);
		const runtime = await createRuntime(streamFn, { toolSource: () => [echo] });

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
		const events = await readAll(runtime);
		const items = completedItems(events);
		const result = items.find(
			(item): item is Extract<CanonicalItem, { itemType: "tool-result" }> => item.itemType === "tool-result",
		);

		expect(
			events.some(
				(event) =>
					event.data.type === "item.updated" &&
					event.data.updateOrdinal === "1" &&
					event.data.content.some((content) => content.type === "text" && content.text === "working"),
			),
		).toBe(true);
		expect(result).toMatchObject({ toolCallId: "call-echo", toolName: "echo", isError: false });
		expect(result?.content).toEqual([{ type: "text", text: "echoed" }]);
	});

	it("projects Tool images as bounded internal metadata instead of embedding or dropping the media", async () => {
		const image = Buffer.from("tool image fixture");
		const inspect = tool("inspect", async () => ({
			content: [{ type: "image" as const, data: image.toString("base64"), mimeType: "image/png" }],
			details: {},
		}));
		const runtime = await createRuntime(
			scriptedStream([
				() => messageResponse(assistant([toolCall("call-image", "inspect")], "toolUse")),
				() => textResponse("done"),
			]),
			{ model: { ...TEST_MODEL, input: ["text", "image"] }, toolSource: () => [inspect] },
		);

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
		const result = completedItems(await readAll(runtime)).find(
			(item): item is Extract<CanonicalItem, { itemType: "tool-result" }> => item.itemType === "tool-result",
		);
		expect(result?.content).toEqual([
			{
				type: "media-summary",
				mediaType: "image/png",
				bytes: image.byteLength,
				sha256: createHash("sha256").update(image).digest("hex"),
			},
		]);
		expect(JSON.stringify(result)).not.toContain(image.toString("base64"));
	});

	it("runs independent tools in parallel while preserving source order in turn settlement", async () => {
		const slowGate = deferred();
		const fastGate = deferred();
		const slowStarted = deferred();
		const fastStarted = deferred();
		const slow = {
			...tool("slow", async () => {
				slowStarted.resolve();
				await slowGate.promise;
				return { content: [{ type: "text", text: "slow result" }], details: {} };
			}),
			executionMode: "parallel" as const,
		};
		const fast = {
			...tool("fast", async () => {
				fastStarted.resolve();
				await fastGate.promise;
				return { content: [{ type: "text", text: "fast result" }], details: {} };
			}),
			executionMode: "parallel" as const,
		};
		const streamFn = scriptedStream([
			() => messageResponse(assistant([toolCall("call-slow", "slow"), toolCall("call-fast", "fast")], "toolUse")),
			() => textResponse("done"),
		]);
		const runtime = await createRuntime(streamFn, { toolSource: () => [slow, fast] });
		const run = start(runtime);

		await Promise.all([slowStarted.promise, fastStarted.promise]);
		fastGate.resolve();
		await new Promise<void>((resolve) => setImmediate(resolve));
		slowGate.resolve();
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });

		const events = await readAll(runtime);
		const resultItems = completedItems(events).filter(
			(item): item is Extract<CanonicalItem, { itemType: "tool-result" }> => item.itemType === "tool-result",
		);
		expect(resultItems.map((item) => item.toolCallId)).toEqual(["call-fast", "call-slow"]);
		const resultIdByCall = new Map(resultItems.map((item) => [item.toolCallId, item.itemId]));
		const firstTurn = events.find(
			(event) => event.data.type === "turn.completed" && event.data.finishReason === "tool-use",
		);
		expect(firstTurn?.data).toMatchObject({
			type: "turn.completed",
			toolResultItemIds: [resultIdByCall.get("call-slow"), resultIdByCall.get("call-fast")],
		});
	});

	it("requires the exact active run and turn coordinate for control", async () => {
		const controlled = controlledResponse();
		const runtime = await createRuntime(controlled.streamFn);
		const run = start(runtime);
		await controlled.started;
		const active = runtime.readActiveCoordinate(STREAM);
		expect(active?.turn).toBe("1");

		await expect(runtime.control(run.initialCoordinate, { type: "abort" })).resolves.toMatchObject({
			accepted: false,
			reason: "coordinate-mismatch",
			activeCoordinate: active,
		});
		if (!active) throw new Error("Expected an active coordinate.");
		await expect(runtime.control(active, { type: "abort" })).resolves.toMatchObject({ accepted: true });
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });
		await expect(runtime.control(active, { type: "abort" })).resolves.toEqual({
			accepted: false,
			reason: "no-active-run",
		});
	});

	it("aborts the current turn atomically by exact run identity and preserves the caller reason", async () => {
		const controlled = controlledResponse();
		let providerSignal: AbortSignal | undefined;
		const runtime = await createRuntime((model, context, options) => {
			providerSignal = options?.signal;
			return controlled.streamFn(model, context, options);
		});
		const run = start(runtime);
		await controlled.started;
		const active = runtime.readActiveCoordinate(STREAM);
		if (!active) throw new Error("Expected an active coordinate.");
		expect(active.turn).toBe("1");

		const reason = new Error("owned cancellation");
		await expect(runtime.abortRun(run.initialCoordinate, reason)).resolves.toEqual({
			accepted: true,
			coordinate: active,
		});
		expect(providerSignal?.aborted).toBe(true);
		expect(providerSignal?.reason).toBe(reason);
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });
	});

	it("starts a same-actor run submitted by an abort terminal observer", async () => {
		const firstAsked = deferred();
		const secondAsked = deferred();
		const pending = new Map<
			string,
			{ readonly request: InteractionRequest; readonly resolve: (response: unknown) => void }
		>();
		let requestCount = 0;
		const interactions = new GameInteractionService({
			limits: {
				maxIdentifierBytes: 128,
				maxCounterDigits: 20,
				maxQuestionBytes: 1_024,
				maxHeaderBytes: 128,
				maxDetailBytes: 4_096,
				maxOptionLabelBytes: 256,
				maxOptionDescriptionBytes: 1_024,
				maxCustomAnswerBytes: 2_048,
				maxQuestionsPerRequest: 4,
				maxOptionsPerQuestion: 5,
				maxRequestBytes: 8_192,
				maxResponseBytes: 4_096,
				maxPendingRequests: 2,
			},
			broker: {
				ask: (request, signal) =>
					new Promise((resolve, reject) => {
						requestCount += 1;
						pending.set(request.requestId, { request, resolve });
						const aborted = () => {
							pending.delete(request.requestId);
							reject(signal.reason);
						};
						signal.addEventListener("abort", aborted, { once: true });
						(requestCount === 1 ? firstAsked : secondAsked).resolve();
					}),
			},
		});
		let modelCall = 0;
		const modelStream: StreamFn = () => {
			modelCall += 1;
			return modelCall <= 2
				? messageResponse(
						assistant(
							[
								{
									type: "toolCall",
									id: `ask-${modelCall}`,
									name: "ask_player",
									arguments: {
										questions: [
											{
												questionId: "path",
												question: "Which path?",
												options: [{ optionId: "verify", label: "Verify" }],
											},
										],
									},
								},
							],
							"toolUse",
						),
					)
				: textResponse("done");
		};
		let recordId = 0;
		const quiescenceTranscriptStore = new InMemoryTranscriptStore();
		const transcript = new DurableTranscript(quiescenceTranscriptStore, {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
			newRecordId: () => `quiescence-record-${++recordId}`,
		});
		let runId = 0;
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
			newRunId: () => `quiescence-run-${++runId}`,
		});
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream,
			toolSource: (input, signal, coordinate) => interactions.createTools(input, signal, coordinate),
			toolPolicy: allowAllTestTools,
			toolLimits: TEST_TOOL_LIMITS,
		});

		const first = runtime.start({ ...STREAM, inputId: "first", payload: { instruction: "first" } });
		await firstAsked.promise;
		let second: ReturnType<GameRuntime["start"]> | undefined;
		runtime.subscribeImmediate(STREAM, (event) => {
			if (
				event.coordinate.runId === first.runId &&
				event.data.type === "run.completed" &&
				event.data.status === "aborted"
			) {
				second = runtime.start({ ...STREAM, inputId: "second", payload: { instruction: "second" } });
			}
		});
		const active = runtime.readActiveCoordinate(STREAM);
		if (!active) throw new Error("Expected the first interaction run to be active.");
		await expect(runtime.abortRun(active, new Error("caller cancelled"))).resolves.toMatchObject({ accepted: true });
		await expect(first.completion).resolves.toMatchObject({ status: "aborted" });
		await Promise.race([
			secondAsked.promise,
			new Promise((_, reject) => setTimeout(() => reject(new Error("second interaction did not start")), 1_000)),
		]);
		if (!second) throw new Error("Expected the terminal observer to submit the second run.");
		const [secondPending] = [...pending.values()];
		if (!secondPending) throw new Error("Expected the second interaction request.");
		secondPending.resolve({
			status: "answered",
			answers: [{ questionId: "path", selectedOptionIds: ["verify"] }],
		});
		await expect(second.completion).resolves.toMatchObject({ status: "completed" });
	});

	it("records a canonical terminal when an admitted run cannot start", async () => {
		const eventStore = new InMemoryEventStore();
		const transcriptBacking = new InMemoryTranscriptStore();
		const transcriptStore: TranscriptStore = {
			load: transcriptBacking.load.bind(transcriptBacking),
			append: async () => {
				throw new Error("transcript sink unavailable");
			},
		};
		const transcript = new DurableTranscript(transcriptStore, {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		let runId = 0;
		const runtime = new GameRuntime(eventStore, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
			newRunId: () => `start-failure-run-${++runId}`,
		});
		await runtime.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: () => textResponse("unused") });

		await expect(runtime.start({ ...STREAM, inputId: "poison", payload: {} }).completion).resolves.toMatchObject({
			status: "failed",
		});
		const rejected = runtime.start({ ...STREAM, inputId: "cannot-start", payload: {} });
		await expect(rejected.completion).resolves.toMatchObject({ status: "failed" });
		await expect(eventStore.readRun(STREAM, "cannot-start")).resolves.toMatchObject({
			initialCoordinate: { runId: rejected.runId },
			status: "failed",
		});
	});

	it("never lets a stale run identity abort a newer active run", async () => {
		const newer = controlledResponse();
		let call = 0;
		let newerSignal: AbortSignal | undefined;
		const runtime = await createRuntime((model, context, options) => {
			call += 1;
			if (call === 1) return textResponse("first");
			newerSignal = options?.signal;
			return newer.streamFn(model, context, options);
		});
		const old = start(runtime);
		await expect(old.completion).resolves.toMatchObject({ status: "completed" });
		const current = runtime.start({ ...STREAM, inputId: "newer", payload: { instruction: "continue" } });
		await newer.started;

		await expect(runtime.abortRun(old.initialCoordinate, new Error("stale"))).resolves.toEqual({
			accepted: false,
			reason: "no-active-run",
		});
		expect(newerSignal?.aborted).toBe(false);
		newer.finish();
		await expect(current.completion).resolves.toMatchObject({ status: "completed" });
	});

	it("drains steering before follow-up messages through Pi's queues", async () => {
		const first = controlledResponse();
		const contexts: Context[] = [];
		let call = 0;
		const modelStream: StreamFn = (model, context, options) => {
			contexts.push(structuredClone(context));
			call += 1;
			return call === 1 ? first.streamFn(model, context, options) : textResponse(`turn ${call}`);
		};
		const runtime = await createRuntime(modelStream);
		const run = start(runtime);
		await first.started;
		const active = runtime.readActiveCoordinate(STREAM);
		if (!active) throw new Error("Expected an active coordinate.");

		await expect(runtime.control(active, { type: "steer", text: "steer now" })).resolves.toMatchObject({
			accepted: true,
		});
		await expect(runtime.control(active, { type: "follow-up", text: "afterward" })).resolves.toMatchObject({
			accepted: true,
		});
		first.finish("first");
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });

		expect(contexts).toHaveLength(3);
		expect(contexts[1]?.messages).toContainEqual(expect.objectContaining({ role: "user", content: "steer now" }));
		expect(contexts[2]?.messages).toContainEqual(expect.objectContaining({ role: "user", content: "afterward" }));
		const events = await readAll(runtime);
		expect(events.filter((event) => event.data.type === "control.accepted")).toHaveLength(2);
	});

	it("refreshes the game-owned tool snapshot before every continued Pi turn", async () => {
		const contexts: Context[] = [];
		const tools = [
			tool("first", async () => ({ content: [{ type: "text", text: "one" }], details: {} })),
			tool("second", async () => ({ content: [{ type: "text", text: "two" }], details: {} })),
		];
		let refresh = 0;
		const runtime = await createRuntime(
			scriptedStream(
				[
					() => messageResponse(assistant([toolCall("call-first", "first")], "toolUse")),
					() => messageResponse(assistant([toolCall("call-second", "second")], "toolUse")),
					() => textResponse("done"),
				],
				contexts,
			),
			{
				toolSource: () => {
					const current = refresh;
					refresh += 1;
					return current === 0 ? [tools[0] as AgentTool] : current === 1 ? [tools[1] as AgentTool] : [];
				},
			},
		);

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts.map((context) => context.tools?.map((entry) => entry.name))).toEqual([["first"], ["second"], []]);
		expect(refresh).toBe(3);
	});

	it("fails closed when a dynamic tool refresh fails", async () => {
		const contexts: Context[] = [];
		const first = tool("first", async () => ({ content: [{ type: "text", text: "one" }], details: {} }));
		let refresh = 0;
		const runtime = await createRuntime(
			scriptedStream(
				[
					() => messageResponse(assistant([toolCall("call-first", "first")], "toolUse")),
					() => textResponse("cannot use stale tools"),
				],
				contexts,
			),
			{
				toolSource: () => {
					refresh += 1;
					if (refresh > 1) throw new Error("game snapshot unavailable");
					return [first];
				},
			},
		);

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "failed" });
		expect(contexts).toHaveLength(1);
		expect(refresh).toBe(2);
		const events = await readAll(runtime);
		expect(events.filter((event) => event.data.type === "turn.started")).toHaveLength(2);
		expect(events.filter((event) => event.data.type === "turn.completed").at(-1)?.data).toMatchObject({
			type: "turn.completed",
			finishReason: "error",
		});
	});

	it("drains steering accepted while the authoritative tool snapshot is loading", async () => {
		const snapshotStarted = deferred();
		const releaseSnapshot = deferred();
		const contexts: Context[] = [];
		let refresh = 0;
		const runtime = await createRuntime(
			scriptedStream([() => textResponse("first"), () => textResponse("steered")], contexts),
			{
				toolSource: async () => {
					refresh += 1;
					if (refresh === 1) {
						snapshotStarted.resolve();
						await releaseSnapshot.promise;
					}
					return [];
				},
			},
		);
		const run = start(runtime);
		await snapshotStarted.promise;
		const active = runtime.readActiveCoordinate(STREAM);
		if (!active) throw new Error("Expected an active coordinate.");

		await expect(runtime.control(active, { type: "steer", text: "during snapshot" })).resolves.toMatchObject({
			accepted: true,
		});
		releaseSnapshot.resolve();
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });

		expect(refresh).toBe(2);
		expect(contexts).toHaveLength(2);
		expect(contexts[1]?.messages).toContainEqual(expect.objectContaining({ role: "user", content: "during snapshot" }));
	});

	it("aborts during authoritative tool loading without invoking the provider", async () => {
		const snapshotStarted = deferred();
		const releaseSnapshot = deferred();
		let providerCalls = 0;
		const runtime = await createRuntime(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			{
				toolSource: async () => {
					snapshotStarted.resolve();
					await releaseSnapshot.promise;
					return [];
				},
			},
		);
		const run = start(runtime);
		await snapshotStarted.promise;
		const active = runtime.readActiveCoordinate(STREAM);
		if (!active) throw new Error("Expected an active coordinate.");

		await expect(runtime.control(active, { type: "abort" })).resolves.toMatchObject({ accepted: true });
		releaseSnapshot.resolve();
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });
		expect(providerCalls).toBe(0);
	});

	it("normalizes a thrown provider failure into a failed canonical run", async () => {
		const runtime = await createRuntime(() => {
			throw new Error("provider exploded");
		});

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "failed" });
		const events = await readAll(runtime);
		expect(events.at(-1)?.data).toEqual({ type: "run.completed", status: "failed" });
	});

	it("never projects hidden reasoning, signatures, tool arguments, or tool details", async () => {
		const secrets = {
			reasoning: "hidden reasoning",
			thinkingSignature: "thinking signature",
			toolSignature: "tool signature",
			argument: "secret argument",
			details: "secret details",
		};
		const inspect = tool("inspect", async () => ({
			content: [{ type: "text", text: "safe result" }],
			details: { secret: secrets.details },
		}));
		const privateMessage = assistant(
			[
				{
					type: "thinking",
					thinking: secrets.reasoning,
					thinkingSignature: secrets.thinkingSignature,
				},
				{ type: "text", text: "visible" },
				{
					type: "toolCall",
					id: "call-private",
					name: "inspect",
					arguments: { value: secrets.argument },
					thoughtSignature: secrets.toolSignature,
				},
				{ type: "text", text: "after tool" },
			],
			"toolUse",
		);
		const runtime = await createRuntime(
			scriptedStream([() => messageResponse(privateMessage), () => textResponse("done")]),
			{ toolSource: () => [inspect] },
		);

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
		const events = await readAll(runtime);
		const serialized = JSON.stringify(events);
		for (const privateValue of Object.values(secrets)) expect(serialized).not.toContain(privateValue);
		expect(serialized).toContain("visible");
		expect(serialized).toContain("after tool");
		expect(serialized).toContain("safe result");
		const items = completedItems(events);
		const assistantItem = items.find(
			(item): item is Extract<CanonicalItem, { itemType: "message" }> =>
				item.itemType === "message" && item.role === "assistant" && item.content.some((part) => part.type === "text"),
		);
		const callItem = items.find(
			(item): item is Extract<CanonicalItem, { itemType: "tool-call" }> => item.itemType === "tool-call",
		);
		expect(assistantItem?.content).toEqual([
			{ type: "text", text: "visible", sourceIndex: 1 },
			{ type: "text", text: "after tool", sourceIndex: 3 },
		]);
		expect(callItem).toMatchObject({ parentMessageItemId: assistantItem?.itemId, sourceIndex: 2 });
	});

	it("projects an OpenAI Responses reasoning summary without exposing its replay envelope", async () => {
		const reasoningItem = {
			id: "rs_safe_summary",
			type: "reasoning",
			summary: [
				{ type: "summary_text", text: "Checked the nearby threat." },
				{ type: "summary_text", text: "Compared the safe actions." },
			],
			content: [{ type: "reasoning_text", text: "private raw chain of thought" }],
			encrypted_content: "private-replay-payload",
		};
		const response = assistant([
			{
				type: "thinking",
				thinking: "Checked the nearby threat.\n\nCompared the safe actions.",
				thinkingSignature: JSON.stringify(reasoningItem),
			},
			{ type: "text", text: "The path is clear." },
		]);
		const runtime = await createRuntime(() => messageResponse(response));

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
		const events = await readAll(runtime);
		const assistantItem = completedAssistantItems(events)[0];
		const startedAssistant = events.find(
			(event) =>
				event.data.type === "item.started" &&
				event.data.item.itemType === "message" &&
				event.data.item.role === "assistant",
		);
		expect(startedAssistant?.data).toMatchObject({ item: { content: [] } });
		expect(assistantItem?.content).toEqual([
			{
				type: "reasoning-summary",
				text: "Checked the nearby threat.\n\nCompared the safe actions.",
				sourceIndex: 0,
			},
			{ type: "text", text: "The path is clear.", sourceIndex: 1 },
		]);
		expect(events.flatMap((event) => (event.data.type === "item.delta" ? [event.data.delta] : []))).toEqual([
			{ type: "text", contentIndex: 1, text: "The path is clear." },
		]);
		expect(JSON.stringify(events)).not.toContain(reasoningItem.id);
		expect(JSON.stringify(events)).not.toContain(reasoningItem.encrypted_content);
		expect(JSON.stringify(events)).not.toContain(reasoningItem.content[0]?.text);
	});

	it.each([
		{
			api: "azure-openai-responses",
			provider: "azure-openai-responses",
			summary: "Compared the available routes.",
			answer: "Take the eastern route.",
		},
		{
			api: "openai-codex-responses",
			provider: "openai-codex",
			summary: "Inspected the relevant game state.",
			answer: "The state is valid.",
		},
	] as const)(
		"projects the same provider-classified summary through $api",
		async ({ api, provider, summary, answer }) => {
			const model: Model<Api> = { ...TEST_MODEL, api, provider };
			const response = assistant(
				[
					{
						type: "thinking",
						thinking: summary,
						thinkingSignature: JSON.stringify({
							id: `rs_${api}`,
							type: "reasoning",
							summary: [{ type: "summary_text", text: summary }],
							content: [],
						}),
					},
					{ type: "text", text: answer },
				],
				"stop",
				model,
			);
			const runtime = await createRuntime(() => messageResponse(response), { model });

			await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
			const assistantItem = completedAssistantItems(await readAll(runtime))[0];
			expect(assistantItem?.content).toEqual([
				{ type: "reasoning-summary", text: summary, sourceIndex: 0 },
				{ type: "text", text: answer, sourceIndex: 1 },
			]);
		},
	);

	it("does not treat a Responses-shaped signature from another Pi API as a visible summary", async () => {
		const model: Model<"openai-completions"> = {
			...TEST_MODEL,
			api: "openai-completions",
			provider: "openrouter",
		};
		const response = assistant(
			[
				{
					type: "thinking",
					thinking: "private completion reasoning",
					thinkingSignature: JSON.stringify({
						id: "not-a-responses-item",
						type: "reasoning",
						summary: [{ type: "summary_text", text: "private completion reasoning" }],
					}),
				},
				{ type: "text", text: "Visible answer." },
			],
			"stop",
			model,
		);
		const runtime = await createRuntime(() => messageResponse(response), { model });

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
		const events = await readAll(runtime);
		const assistantItem = completedAssistantItems(events)[0];
		expect(assistantItem?.content).toEqual([{ type: "text", text: "Visible answer.", sourceIndex: 1 }]);
		expect(JSON.stringify(events)).not.toContain("private completion reasoning");
	});

	it("keeps unclassified OpenAI reasoning blocks private without failing the run", async () => {
		const privateValues = [
			"raw reasoning without a summary",
			"reasoning with a malformed signature",
			"reasoning that does not match its summary",
			"redacted provider summary",
		];
		const response = assistant([
			{
				type: "thinking",
				thinking: privateValues[0] ?? "",
				thinkingSignature: JSON.stringify({
					id: "rs_raw_only",
					type: "reasoning",
					summary: [],
					content: [{ type: "reasoning_text", text: privateValues[0] }],
				}),
			},
			{
				type: "thinking",
				thinking: privateValues[1] ?? "",
				thinkingSignature: "{",
			},
			{
				type: "thinking",
				thinking: privateValues[2] ?? "",
				thinkingSignature: JSON.stringify({
					id: "rs_mismatch",
					type: "reasoning",
					summary: [{ type: "summary_text", text: "A different provider summary." }],
					content: [],
				}),
			},
			{
				type: "thinking",
				thinking: privateValues[3] ?? "",
				thinkingSignature: JSON.stringify({
					id: "rs_redacted",
					type: "reasoning",
					summary: [{ type: "summary_text", text: privateValues[3] }],
					content: [],
				}),
				redacted: true,
			},
			{ type: "text", text: "Only this answer is public." },
		]);
		const runtime = await createRuntime(() => messageResponse(response));

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
		const events = await readAll(runtime);
		const assistantItem = completedAssistantItems(events)[0];
		expect(assistantItem?.content).toEqual([{ type: "text", text: "Only this answer is public.", sourceIndex: 4 }]);
		for (const privateValue of privateValues) expect(JSON.stringify(events)).not.toContain(privateValue);
	});

	it("preserves provider summary, text, and Tool source order without publishing Tool arguments", async () => {
		const inspect = tool("inspect-order", async () => ({
			content: [{ type: "text", text: "safe result" }],
			details: {},
		}));
		const response = assistant(
			[
				{
					type: "thinking",
					thinking: "Selected the inspection Tool.",
					thinkingSignature: JSON.stringify({
						id: "rs_order",
						type: "reasoning",
						summary: [{ type: "summary_text", text: "Selected the inspection Tool." }],
						content: [],
					}),
				},
				{ type: "text", text: "Inspecting." },
				{ type: "toolCall", id: "call-order", name: "inspect-order", arguments: { secret: "hidden" } },
				{ type: "text", text: "Inspection requested." },
			],
			"toolUse",
		);
		const runtime = await createRuntime(scriptedStream([() => messageResponse(response), () => textResponse("done")]), {
			toolSource: () => [inspect],
		});

		await expect(start(runtime).completion).resolves.toMatchObject({ status: "completed" });
		const events = await readAll(runtime);
		const items = completedItems(events);
		const assistantItem = items.find(
			(item): item is Extract<CanonicalItem, { itemType: "message" }> =>
				item.itemType === "message" && item.role === "assistant" && item.content.length === 3,
		);
		const callItem = items.find(
			(item): item is Extract<CanonicalItem, { itemType: "tool-call" }> => item.itemType === "tool-call",
		);
		expect(assistantItem?.content).toEqual([
			{ type: "reasoning-summary", text: "Selected the inspection Tool.", sourceIndex: 0 },
			{ type: "text", text: "Inspecting.", sourceIndex: 1 },
			{ type: "text", text: "Inspection requested.", sourceIndex: 3 },
		]);
		expect(callItem).toMatchObject({ parentMessageItemId: assistantItem?.itemId, sourceIndex: 2 });
		expect(JSON.stringify(events)).not.toContain("hidden");
	});
});

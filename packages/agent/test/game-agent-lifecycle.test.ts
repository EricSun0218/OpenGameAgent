import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type { StreamFn } from "@earendil-works/pi-agent-core";
import { type Context, createAssistantMessageEventStream } from "@earendil-works/pi-ai";
import {
	type CanonicalEvent,
	type CanonicalEventData,
	eventAudience,
	PROTOCOL_VERSION,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { Type } from "typebox";
import { describe, expect, it, vi } from "vitest";
import {
	assistant,
	controlledResponse,
	messageResponse,
	textResponse,
	tool,
	toolCall,
	TEST_TOOL_LIMITS,
} from "../../devtools/src/pi-fakes.js";
import {
	createProviderAttemptModelStream,
	DURABLE_TOOL_EXECUTION,
	type DurableExecutionAgentTool,
	DurableToolExecutionUnresolvedError,
	DurableTranscript,
	GameAgent,
	GameAgentBusyError,
	GameToolReportedError,
	InMemoryTranscriptStore,
	measureRunPhase,
	TranscriptCompactor,
	type TranscriptMedia,
	type TranscriptStore,
} from "../src/index.js";
import type { GameAgentOptions, GameControlResult, GameInput, GameRunHandle } from "../src/types.js";

const INPUT = {
	sessionId: "session",
	actorId: "actor",
	inputId: "input",
	payload: { instruction: "act" },
} as const;

type EventHook = (
	agent: GameAgent,
	coordinate: Readonly<RunCoordinate>,
	data: Readonly<CanonicalEventData>,
) => void | Promise<void>;

function createAgent(
	modelStream: GameAgentOptions["modelStream"],
	hook?: EventHook,
	overrides: Partial<
		Pick<
			GameAgentOptions,
			| "beforeToolExecution"
			| "contextLimits"
			| "contextSources"
			| "inputProjectionLimits"
			| "maxReportedTokensPerRun"
			| "metricsNow"
			| "model"
			| "newId"
			| "projectInput"
			| "requestSources"
			| "shouldStopAfterTurn"
			| "transcript"
			| "toolLimits"
			| "toolPolicy"
			| "toolSource"
			| "transcriptCompactor"
			| "transformContext"
		>
	> = {},
): { agent: GameAgent; events: CanonicalEvent[] } {
	const events: CanonicalEvent[] = [];
	let eventSequence = 0;
	let generatedId = 0;
	let agent: GameAgent;
	const hasToolCandidates = overrides.toolSource !== undefined || overrides.requestSources !== undefined;
	agent = new GameAgent({
		sessionId: INPUT.sessionId,
		actorId: INPUT.actorId,
		model: {
			id: "test-model",
			name: "Test model",
			api: "openai-responses",
			provider: "openai",
			baseUrl: "https://example.invalid",
			reasoning: true,
			input: ["text"],
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
			contextWindow: 1,
			maxTokens: 1,
		},
		modelStream,
		...(hasToolCandidates
			? {
					toolLimits: {
						maxTools: 16,
						maxCatalogBytes: 16_384,
						maxSchemaDepth: 16,
						maxRequestSources: 16,
						maxPolicyRevisionBytes: 128,
						maxArgumentsBytes: 4_096,
						maxArgumentsDepth: 16,
						maxResultContentParts: 16,
						maxResultBytes: 16_384,
						maxResultDepth: 16,
						maxResultUpdates: 64,
					},
					toolPolicy: (_input: unknown, candidates: readonly { name: string }[]) => ({
						revision: "test-policy",
						visibleToolNames: candidates.map((candidate) => candidate.name),
					}),
				}
			: {}),
		...overrides,
		emitEvent: async (coordinate, data) => {
			eventSequence += 1;
			const ordinal = String(eventSequence);
			const event: CanonicalEvent = {
				protocolVersion: PROTOCOL_VERSION,
				eventId: `event-${ordinal}`,
				sequence: ordinal,
				revision: ordinal,
				occurredAt: "2026-08-28T00:00:00.000Z",
				coordinate: structuredClone(coordinate),
				audience: eventAudience(data),
				data: structuredClone(data),
			};
			events.push(event);
			await hook?.(agent, coordinate, data);
			return structuredClone(event);
		},
		newId:
			overrides.newId ??
			(() => {
				generatedId += 1;
				return `generated-${generatedId}`;
			}),
		now: () => 1,
	});
	return { agent, events };
}

function copyContext(context: Context): Context {
	return {
		...(context.systemPrompt === undefined ? {} : { systemPrompt: context.systemPrompt }),
		messages: structuredClone(context.messages),
		...(context.tools === undefined ? {} : { tools: [...context.tools] }),
	};
}

function imageReference(bytes: Buffer) {
	return {
		type: "image-reference" as const,
		attachmentId: `img_${createHash("sha256").update(bytes).digest("hex")}`,
		mediaType: "image/png" as const,
		bytes: bytes.byteLength,
		width: 1,
		height: 1,
	};
}

describe("GameAgent lifecycle boundaries", () => {
	it.each(["control", "abortRun"] as const)("captures %s before waiting for the Agent boundary", async (method) => {
		const controlled = controlledResponse();
		const { agent } = createAgent(controlled.streamFn);
		const run = agent.start(INPUT, "original-run");
		await controlled.started;
		const expected = agent.readActiveCoordinate();
		if (!expected) throw new Error("Expected an active original run.");
		const command = { type: "abort" as const };
		try {
			const requested = method === "control" ? agent.control(expected, command) : agent.abortRun(expected);
			expected.runId = "changed-run";
			Object.assign(command, { type: "follow-up", text: "changed" });
			await expect(requested).resolves.toMatchObject({ accepted: true, coordinate: { runId: "original-run" } });
		} finally {
			controlled.finish();
			await run.completion;
		}
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });
	});

	it("starts from one captured input identity and authority", async () => {
		const { agent, events } = createAgent(() => textResponse("done"));
		const values: GameInput = {
			...INPUT,
			admissionAuthority: { scope: "captured-scope", generation: "1" },
		};
		const input = Object.create(null) as GameInput;
		for (const [key, value] of Object.entries(values)) {
			let read = false;
			Object.defineProperty(input, key, {
				get: () => {
					if (read) throw new Error(`The ${key} field was read again.`);
					read = true;
					return value;
				},
			});
		}

		await expect(agent.start(input).completion).resolves.toMatchObject({ status: "completed" });
		expect(events.find((event) => event.data.type === "run.started")?.data).toEqual({
			type: "run.started",
			inputId: "input",
		});
	});

	it.each(["input", "newId", "metricsNow"] as const)(
		"keeps a nested run active when %s reenters start",
		async (callback) => {
			const controlled = controlledResponse();
			let agent: GameAgent;
			let reenter = true;
			let ordinal = 0;
			let tick = 0;
			let nested: GameRunHandle | undefined;
			let outer: GameRunHandle | undefined;
			const startNested = () => {
				if (!reenter) return;
				reenter = false;
				nested = agent.start({ ...INPUT, inputId: "nested" }, "nested-run");
			};
			({ agent } = createAgent(controlled.streamFn, undefined, {
				newId: () => {
					if (callback === "newId") startNested();
					return `generated-${++ordinal}`;
				},
				metricsNow: () => {
					if (callback === "metricsNow") startNested();
					return tick++;
				},
			}));
			const input: GameInput = {
				...INPUT,
				get payload() {
					if (callback === "input") startNested();
					return {};
				},
			};
			try {
				expect(() => {
					outer = agent.start(input);
				}).toThrow(GameAgentBusyError);
				expect(agent.readActiveCoordinate()?.runId).toBe("nested-run");
			} finally {
				controlled.finish();
				await Promise.all([nested?.completion, outer?.completion]);
			}
			await expect(nested?.completion).resolves.toMatchObject({ runId: "nested-run", status: "completed" });
		},
	);

	it("never publishes untransformed oversized Tool content after cancellation", async () => {
		const entered = Promise.withResolvers<void>();
		const release = Promise.withResolvers<void>();
		let transformed = 0;
		const { agent, events } = createAgent(
			() => messageResponse(assistant([toolCall("cancelled-call", "inspect")], "toolUse")),
			undefined,
			{
				toolSource: () => [
					tool("inspect", async () => {
						entered.resolve();
						await release.promise;
						return { content: [{ type: "text", text: "\u0000".repeat(100) }], details: {} };
					}),
				],
				toolLimits: { ...TEST_TOOL_LIMITS, maxResultBytes: 256 },
				requestSources: [
					{
						sourceId: "projection",
						collect: () => ({
							transformToolResultTextBytes: 100,
							transformToolResult: () => {
								transformed += 1;
								return [{ type: "text", text: "reference" }];
							},
						}),
					},
				],
			},
		);
		const run = agent.start(INPUT, "cancelled-result");
		await entered.promise;
		const coordinate = agent.readActiveCoordinate();
		if (!coordinate) throw new Error("Expected active Tool.");
		await agent.control(coordinate, { type: "abort" });
		release.resolve();
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });
		expect(transformed).toBe(0);
		const output = events.find(
			(event) => event.data.type === "item.completed" && event.data.item.itemType === "tool-result",
		);
		if (output?.data.type !== "item.completed" || output.data.item.itemType !== "tool-result")
			throw new Error("Expected bounded Tool result.");
		expect(Buffer.byteLength(JSON.stringify(output.data.item.content))).toBeLessThanOrEqual(256);
		expect(output.data.item.isError).toBe(true);
	});
	it("separates provider response latency from durable assistant persistence", async () => {
		let tick = 0;
		const store = new InMemoryTranscriptStore();
		const durable = new DurableTranscript(
			{
				load: (stream, maxBytes) => store.load(stream, maxBytes),
				append: async (stream, revision, records) => {
					if (records.some((record) => record.type === "message.completed" && record.message.role === "assistant"))
						tick += 7;
					return store.append(stream, revision, records);
				},
			},
			{ maxRecordBytes: 16_384, maxCommitBytes: 65_536, maxRestoreBytes: 131_072, maxJsonDepth: 16 },
		);
		const { agent, events } = createAgent(
			() => {
				tick += 5;
				return messageResponse(assistant([{ type: "text", text: "answer" }]));
			},
			undefined,
			{
				metricsNow: () => tick,
				transcript: { transcript: durable, restore: await durable.restore(INPUT) },
				transformContext: async (messages) => {
					tick += 3;
					return messages;
				},
			},
		);
		await agent.start(INPUT, "provider-timing").completion;
		expect(events.find((event) => event.data.type === "run.metrics")?.data).toMatchObject({
			durations: {
				contextAndRecall: "3000",
				provider: "5000",
				providerFirstResponse: "5000",
				durable: "7000",
				total: "15000",
			},
		});
	});

	it("keeps measured Tool subphases local to concurrently active runs", async () => {
		const make = (duration: number) => {
			let tick = 0;
			let requests = 0;
			return createAgent(
				() =>
					++requests === 1
						? messageResponse(assistant([toolCall("measure-call", "measure")], "toolUse"))
						: textResponse("done"),
				undefined,
				{
					metricsNow: () => tick,
					toolSource: () => [
						tool("measure", async () => {
							await measureRunPhase("approval", async () => {
								await Promise.resolve();
								tick += duration;
							});
							await measureRunPhase("settlement", async () => {
								await Promise.resolve();
								tick += duration * 2;
							});
							return { content: [], details: {} };
						}),
					],
				},
			);
		};
		const one = make(3);
		const two = make(11);
		await Promise.all([one.agent.start(INPUT, "phase-one").completion, two.agent.start(INPUT, "phase-two").completion]);
		for (const [fixture, duration] of [
			[one, 3000],
			[two, 11000],
		] as const) {
			expect(fixture.events.find((event) => event.data.type === "run.metrics")?.data).toMatchObject({
				durations: { approval: String(duration), settlement: String(duration * 2), tool: String(duration * 3) },
			});
		}
	});
	it.each([false, true])("counts committed retries and route fallbacks (changed route: %s)", async (fallback) => {
		const { agent, events } = createAgent(
			createProviderAttemptModelStream(async (model, _context, _options, attempts) => {
				attempts.begin(model).discard();
				const first = attempts.begin(model);
				const response = await first.commit(() => messageResponse(assistant([], "error")));
				await first.settle(await response.result());
				const target = fallback ? { ...model, id: "fallback-model" } : model;
				return await attempts
					.begin(target)
					.commit(() => messageResponse({ ...assistant([{ type: "text", text: "done" }]), model: target.id }));
			}),
		);
		await expect(agent.start(INPUT, "metrics-retry").completion).resolves.toMatchObject({ status: "completed" });
		expect(events.find((event) => event.data.type === "run.metrics")?.data).toMatchObject({
			counts: { providerAttempts: "2", retries: "1", fallbacks: fallback ? "1" : "0", toolCalls: "0" },
		});
	});

	it("does not count a normal Tool continuation as a provider retry", async () => {
		let requests = 0;
		const { agent, events } = createAgent(
			() =>
				++requests === 1
					? messageResponse(assistant([toolCall("inspect-call", "inspect")], "toolUse"))
					: textResponse("done"),
			undefined,
			{ toolSource: () => [tool("inspect", async () => ({ content: [], details: {} }))] },
		);
		await expect(agent.start(INPUT, "metrics-tool-run").completion).resolves.toMatchObject({ status: "completed" });
		expect(events.find((event) => event.data.type === "run.metrics")?.data).toMatchObject({
			counts: { providerAttempts: "2", retries: "0", fallbacks: "0", toolCalls: "1" },
		});
	});

	it("emits content-free metrics immediately before the segment terminal event", async () => {
		let tick = 0;
		const { agent, events } = createAgent(() => textResponse("secret output"), undefined, {
			metricsNow: () => tick++,
		});

		await agent.start(INPUT, "metrics-run").completion;

		const metricsIndex = events.findIndex((event) => event.data.type === "run.metrics");
		expect(metricsIndex).toBeGreaterThanOrEqual(0);
		expect(events[metricsIndex + 1]?.data).toEqual({ type: "run.completed", status: "completed" });
		const metrics = events[metricsIndex]?.data;
		if (metrics?.type !== "run.metrics") throw new Error("Expected run metrics.");
		expect(metrics).toMatchObject({
			type: "run.metrics",
			unit: "microseconds",
			segmentStatus: "completed",
			counts: { providerAttempts: "1", retries: "0", fallbacks: "0", toolCalls: "0" },
		});
		expect(metrics.durations.firstResponse).toMatch(/^\d+$/);
		expect(metrics.durations.firstTool).toBeUndefined();
		expect(BigInt(metrics.durations.total)).toBeGreaterThanOrEqual(
			BigInt(metrics.durations.contextAndRecall) + BigInt(metrics.durations.provider),
		);
		expect(JSON.stringify(metrics)).not.toContain("secret output");
		expect(events[metricsIndex]?.coordinate).toEqual({
			sessionId: INPUT.sessionId,
			actorId: INPUT.actorId,
			runId: "metrics-run",
			turn: "1",
		});
	});

	it("captures one verified durable restore snapshot before constructing Pi state", async () => {
		let recordId = 0;
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 4_096,
			maxCommitBytes: 8_192,
			maxRestoreBytes: 16_384,
			maxJsonDepth: 8,
			newRecordId: () => `record-${++recordId}`,
		});
		await transcript.commit({
			stream: { sessionId: INPUT.sessionId, actorId: INPUT.actorId },
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: { sessionId: INPUT.sessionId, actorId: INPUT.actorId, runId: "old-run", turn: "0" },
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "verified history" }],
						timestamp: 1,
					},
				},
			],
		});
		const verified = await transcript.restore({ sessionId: INPUT.sessionId, actorId: INPUT.actorId });
		const crossStream = {
			...verified,
			stream: { sessionId: "other-session", actorId: "other-actor" },
			messages: [{ role: "user" as const, content: "cross-stream history", timestamp: 1 }],
		};
		let restoreReads = 0;
		const binding = {
			transcript,
			get restore() {
				restoreReads += 1;
				return restoreReads <= 3 ? verified : crossStream;
			},
		};
		const contexts: Context[] = [];
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				return textResponse("done");
			},
			undefined,
			{ transcript: binding },
		);

		await expect(agent.start(INPUT, "new-run").completion).resolves.toMatchObject({ status: "completed" });
		expect(restoreReads).toBe(1);
		expect(contexts[0]?.messages[0]).toMatchObject({ role: "user", content: "verified history" });
		expect(JSON.stringify(contexts[0])).not.toContain("cross-stream history");
	});

	it("does not project a terminal reasoning summary before its transcript commit", async () => {
		const backing = new InMemoryTranscriptStore();
		const store: TranscriptStore = {
			load: (stream, maxBytes) => backing.load(stream, maxBytes),
			append: (stream, revision, records) => {
				if (records.some((record) => record.type === "message.completed" && record.message.role === "assistant")) {
					throw new Error("assistant transcript commit failed");
				}
				return backing.append(stream, revision, records);
			},
		};
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `summary-record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		const terminal = assistant([
			{
				type: "thinking",
				thinking: "Checked the durable game state.",
				thinkingSignature: JSON.stringify({
					id: "rs_uncommitted",
					type: "reasoning",
					summary: [{ type: "summary_text", text: "Checked the durable game state." }],
					content: [],
				}),
			},
		]);
		const { agent, events } = createAgent(
			() => {
				const stream = createAssistantMessageEventStream();
				queueMicrotask(() => stream.push({ type: "done", reason: "stop", message: terminal }));
				return stream;
			},
			undefined,
			{ transcript: { transcript, restore } },
		);

		await expect(agent.start(INPUT, "uncommitted-summary").completion).resolves.toMatchObject({ status: "failed" });
		expect(JSON.stringify(events)).not.toContain("Checked the durable game state.");
		expect(JSON.stringify(events)).not.toContain("rs_uncommitted");
	});

	it("durably orders Tool intent, start, effect, result, and the next Pi request", async () => {
		const backing = new InMemoryTranscriptStore();
		const order: string[] = [];
		const effects: string[] = [];
		let preparedExecution:
			| {
					operationId: string;
					execute(): Promise<{ content: [{ type: "text"; text: string }]; details: object }>;
			  }
			| undefined;
		const store: TranscriptStore = {
			load: (stream, maxBytes) => backing.load(stream, maxBytes),
			append: async (stream, revision, records) => {
				const stored = await backing.append(stream, revision, records);
				for (const record of records) {
					order.push(
						record.type === "message.completed"
							? `message.${record.message.role}`
							: record.type === "tool.completed"
								? `tool.completed.${record.outcome}`
								: record.type,
					);
					if (record.type === "tool.started" && preparedExecution) {
						preparedExecution.operationId = "operation-mutated";
						preparedExecution.execute = async () => {
							effects.push("mutated");
							return { content: [{ type: "text", text: "mutated" }], details: {} };
						};
					}
				}
				return stored;
			},
		};
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		const inspect: DurableExecutionAgentTool = {
			name: "inspect",
			label: "inspect",
			description: "inspect test tool",
			parameters: Type.Object({ prepared: Type.Boolean() }),
			prepareArguments: () => ({ prepared: true }),
			[DURABLE_TOOL_EXECUTION]: async (toolCallId, parameters) => {
				preparedExecution = {
					operationId: `operation-${toolCallId}`,
					execute: async () => {
						expect(parameters).toEqual({ prepared: true });
						expect(order.slice(-2)).toEqual(["message.assistant", "tool.started"]);
						effects.push("original");
						order.push("effect");
						return { content: [{ type: "text" as const, text: "observed" }], details: {} };
					},
				};
				return preparedExecution;
			},
			execute: async (_toolCallId, parameters) => {
				expect(parameters).toEqual({ prepared: true });
				throw new Error("The durable execution preparation must own execution.");
			},
		};
		let providerCalls = 0;
		const { agent } = createAgent(
			() => {
				providerCalls += 1;
				if (providerCalls === 1) return messageResponse(assistant([toolCall("call-1", "inspect")], "toolUse"));
				expect(order.slice(-2)).toEqual(["tool.completed.completed", "turn.closed"]);
				order.push("provider.2");
				return textResponse("done");
			},
			undefined,
			{
				transcript: { transcript, restore },
				toolSource: () => [inspect],
			},
		);

		await expect(agent.start(INPUT, "durable-tool-run").completion).resolves.toMatchObject({ status: "completed" });
		expect(order).toEqual([
			"message.user",
			"message.assistant",
			"tool.started",
			"effect",
			"tool.completed.completed",
			"turn.closed",
			"provider.2",
			"message.assistant",
			"turn.closed",
		]);
		expect(effects).toEqual(["original"]);
		const stored = await backing.load(INPUT, 32_768);
		expect(stored.records).toContainEqual(
			expect.objectContaining({
				type: "tool.started",
				operationId: "operation-call-1",
				arguments: { prepared: true },
			}),
		);
		await expect(transcript.restore(INPUT)).resolves.toMatchObject({
			messages: [
				{ role: "user" },
				{ role: "assistant" },
				{ role: "toolResult", toolCallId: "call-1", content: [{ text: "observed" }] },
				{ role: "assistant", content: [{ text: "done" }] },
			],
		});
	});

	it("leaves an aborted durable Tool start open for authoritative repair", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		let recoveries = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
			recoverStartedTool: async ({ start }) => {
				recoveries += 1;
				return {
					outcome: "outcome_unknown",
					content: [{ type: "text", text: `authoritative state for ${start.operationId}` }],
					isError: true,
					timestamp: 2,
				};
			},
		});
		const restore = await transcript.restore(INPUT);
		let markEntered: (() => void) | undefined;
		let releaseExecution: (() => void) | undefined;
		const entered = new Promise<void>((resolve) => {
			markEntered = resolve;
		});
		const released = new Promise<void>((resolve) => {
			releaseExecution = resolve;
		});
		const reliable: DurableExecutionAgentTool = {
			name: "reliable",
			label: "reliable",
			description: "durable abort test Tool",
			parameters: Type.Object({}),
			[DURABLE_TOOL_EXECUTION]: async (_toolCallId, _parameters, signal) => ({
				operationId: "operation-aborted",
				execute: async () => {
					markEntered?.();
					await released;
					signal?.throwIfAborted();
					return { content: [{ type: "text", text: "must not complete" }], details: {} };
				},
			}),
			execute: async () => {
				throw new Error("The prepared durable execution must own the effect.");
			},
		};
		const { agent, events } = createAgent(
			() => messageResponse(assistant([toolCall("call-aborted", "reliable")], "toolUse")),
			undefined,
			{
				transcript: { transcript, restore },
				toolSource: () => [reliable],
			},
		);
		const run = agent.start(INPUT, "durable-abort-run");
		await entered;
		const active = agent.readActiveCoordinate();
		if (!active) throw new Error("Expected an active durable Tool run.");
		await expect(agent.control(active, { type: "abort" })).resolves.toMatchObject({ accepted: true });
		releaseExecution?.();
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });

		const beforeRepair = await store.load(INPUT, 32_768);
		expect(beforeRepair.records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"tool.started",
		]);
		expect(
			events.some(
				(event) =>
					event.data.type === "item.completed" &&
					event.data.item.itemType === "tool-result" &&
					event.data.item.toolCallId === "call-aborted",
			),
		).toBe(false);
		expect(recoveries).toBe(0);
		await expect(transcript.restore(INPUT)).resolves.toMatchObject({
			messages: [
				{ role: "user" },
				{ role: "assistant" },
				{
					role: "toolResult",
					toolCallId: "call-aborted",
					content: [{ text: "authoritative state for operation-aborted" }],
					isError: true,
				},
			],
		});
		expect(recoveries).toBe(1);
	});

	it("marks a durable Tool unresolved when cancellation wins immediately after its start commit", async () => {
		const backing = new InMemoryTranscriptStore();
		let markStartedPersisted: (() => void) | undefined;
		let releaseStartCommit: (() => void) | undefined;
		const startedPersisted = new Promise<void>((resolve) => {
			markStartedPersisted = resolve;
		});
		const startCommitReleased = new Promise<void>((resolve) => {
			releaseStartCommit = resolve;
		});
		let gateStartCommit = true;
		const store: TranscriptStore = {
			load: (stream, maxBytes) => backing.load(stream, maxBytes),
			append: async (stream, revision, records) => {
				const stored = await backing.append(stream, revision, records);
				if (gateStartCommit && records.some((record) => record.type === "tool.started")) {
					gateStartCommit = false;
					markStartedPersisted?.();
					await startCommitReleased;
				}
				return stored;
			},
		};
		let recordId = 0;
		let recoveries = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
			recoverStartedTool: async () => {
				recoveries += 1;
				return {
					outcome: "outcome_unknown",
					content: [{ type: "text", text: "recovered after start-commit cancellation" }],
					isError: true,
					timestamp: 2,
				};
			},
		});
		const restore = await transcript.restore(INPUT);
		let executions = 0;
		const reliable: DurableExecutionAgentTool = {
			name: "reliable",
			label: "reliable",
			description: "post-start abort test Tool",
			parameters: Type.Object({}),
			[DURABLE_TOOL_EXECUTION]: async () => ({
				operationId: "operation-start-race",
				execute: async () => {
					executions += 1;
					return { content: [{ type: "text", text: "must not complete" }], details: {} };
				},
			}),
			execute: async () => {
				throw new Error("The prepared durable execution must own the effect.");
			},
		};
		const { agent, events } = createAgent(
			() => messageResponse(assistant([toolCall("call-start-race", "reliable")], "toolUse")),
			undefined,
			{ transcript: { transcript, restore }, toolSource: () => [reliable] },
		);
		const run = agent.start(INPUT, "durable-start-race");
		await startedPersisted;
		const active = agent.readActiveCoordinate();
		if (!active) throw new Error("Expected an active durable Tool run.");
		const cancellation = agent.control(active, { type: "abort" });
		releaseStartCommit?.();
		await expect(cancellation).resolves.toMatchObject({ accepted: true });
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });

		expect(executions).toBe(0);
		expect((await backing.load(INPUT, 32_768)).records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"tool.started",
		]);
		expect(
			events.some(
				(event) =>
					event.data.type === "item.completed" &&
					event.data.item.itemType === "tool-result" &&
					event.data.item.toolCallId === "call-start-race",
			),
		).toBe(false);
		const repaired = await transcript.restore(INPUT);
		expect(repaired.messages).toContainEqual(
			expect.objectContaining({
				role: "toolResult",
				toolCallId: "call-start-race",
				content: [{ type: "text", text: "recovered after start-commit cancellation" }],
			}),
		);
		expect(recoveries).toBe(1);
	});

	it("keeps an explicitly unresolved prepared durable Tool open for authoritative recovery", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		let recoveries = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
			recoverStartedTool: async () => {
				recoveries += 1;
				return {
					outcome: "completed",
					content: [{ type: "text", text: "authoritative receipt succeeded" }],
					isError: false,
					timestamp: 2,
				};
			},
		});
		const restore = await transcript.restore(INPUT);
		const reliable: DurableExecutionAgentTool = {
			name: "reliable",
			label: "reliable",
			description: "unresolved execution test Tool",
			parameters: Type.Object({}),
			[DURABLE_TOOL_EXECUTION]: async () => ({
				operationId: "operation-throwing",
				execute: async () => {
					throw new DurableToolExecutionUnresolvedError(new Error("transport failed after durable start"));
				},
			}),
			execute: async () => {
				throw new Error("The prepared durable execution must own the effect.");
			},
		};
		let providerCalls = 0;
		const { agent, events } = createAgent(
			() => {
				providerCalls += 1;
				return providerCalls === 1
					? messageResponse(assistant([toolCall("call-throwing", "reliable")], "toolUse"))
					: textResponse("must not continue from a synthetic result");
			},
			undefined,
			{ transcript: { transcript, restore }, toolSource: () => [reliable] },
		);

		await expect(agent.start(INPUT, "durable-throwing").completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(1);
		expect((await store.load(INPUT, 32_768)).records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"tool.started",
		]);
		expect(
			events.some(
				(event) =>
					event.data.type === "item.completed" &&
					event.data.item.itemType === "tool-result" &&
					event.data.item.toolCallId === "call-throwing",
			),
		).toBe(false);
		const repaired = await transcript.restore(INPUT);
		expect(repaired.messages).toContainEqual(
			expect.objectContaining({
				role: "toolResult",
				toolCallId: "call-throwing",
				content: [{ type: "text", text: "authoritative receipt succeeded" }],
				isError: false,
			}),
		);
		expect(recoveries).toBe(1);
	});

	it("commits an ordinary prepared durable Tool failure as a normal Pi result", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		let recoveries = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
			recoverStartedTool: async () => {
				recoveries += 1;
				return undefined;
			},
		});
		const restore = await transcript.restore(INPUT);
		const denied: DurableExecutionAgentTool = {
			name: "denied",
			label: "denied",
			description: "deterministic failure test Tool",
			parameters: Type.Object({}),
			[DURABLE_TOOL_EXECUTION]: async () => ({
				operationId: "operation-denied",
				execute: async () => {
					throw new Error("deterministic pre-effect denial");
				},
			}),
			execute: async () => {
				throw new Error("The prepared durable execution must own the effect.");
			},
		};
		let providerCalls = 0;
		const contexts: Context[] = [];
		const { agent, events } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				providerCalls += 1;
				return providerCalls === 1
					? messageResponse(assistant([toolCall("call-denied", "denied")], "toolUse"))
					: textResponse("handled deterministic failure");
			},
			undefined,
			{ transcript: { transcript, restore }, toolSource: () => [denied] },
		);

		await expect(agent.start(INPUT, "durable-denied").completion).resolves.toMatchObject({ status: "completed" });
		expect(providerCalls).toBe(2);
		expect(contexts[1]?.messages).toContainEqual(
			expect.objectContaining({ role: "toolResult", toolCallId: "call-denied", isError: true }),
		);
		expect((await store.load(INPUT, 32_768)).records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"tool.started",
			"tool.completed",
			"turn.closed",
			"message.completed",
			"turn.closed",
		]);
		expect(
			events.some(
				(event) =>
					event.data.type === "item.completed" &&
					event.data.item.itemType === "tool-result" &&
					event.data.item.toolCallId === "call-denied",
			),
		).toBe(true);
		await transcript.restore(INPUT);
		expect(recoveries).toBe(0);
	});

	it("uses the same normalized Tool image in the live Pi turn and durable restart", async () => {
		const sourceBytes = Buffer.from("source-image");
		const normalizedBytes = Buffer.from("normalized-image");
		const normalizedReference = imageReference(normalizedBytes);
		const normalizedImage = {
			type: "image" as const,
			data: normalizedBytes.toString("base64"),
			mimeType: normalizedReference.mediaType,
		};
		const media: TranscriptMedia = {
			captureToolResult: vi.fn(async ({ content }: Parameters<TranscriptMedia["captureToolResult"]>[0]) => ({
				content: content.map((part) => (part.type === "image" ? normalizedImage : part)),
				durableContent: content.map((part) =>
					part.type === "image" ? normalizedReference : { type: "text" as const, text: part.text },
				),
			})),
			read: vi.fn(async (requests: Parameters<TranscriptMedia["read"]>[0]) => requests.map(() => normalizedImage)),
		};
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
			media,
		});
		const restore = await transcript.restore(INPUT);
		const inspect = tool("inspect", async () => ({
			content: [
				{ type: "text" as const, text: "captured" },
				{ type: "image" as const, data: sourceBytes.toString("base64"), mimeType: "image/jpeg" },
			],
			details: {},
		}));
		const contexts: Context[] = [];
		let providerCalls = 0;
		const { agent, events } = createAgent(
			(model, context) => {
				contexts.push(copyContext(context));
				providerCalls += 1;
				return providerCalls === 1
					? messageResponse(assistant([toolCall("call-image", "inspect")], "toolUse", model))
					: textResponse("done", [], model);
			},
			undefined,
			{
				model: {
					id: "vision-model",
					name: "Vision model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text", "image"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 1,
					maxTokens: 1,
				},
				transcript: { transcript, restore },
				toolSource: () => [inspect],
			},
		);

		await expect(agent.start(INPUT, "durable-tool-image").completion).resolves.toMatchObject({ status: "completed" });
		expect(media.read).toHaveBeenCalledTimes(1);
		expect(contexts[1]?.messages).toContainEqual(
			expect.objectContaining({
				role: "toolResult",
				content: [{ type: "text", text: "captured" }, normalizedImage],
			}),
		);
		const stored = await store.load(INPUT, 32_768);
		const serialized = JSON.stringify(stored.records);
		expect(serialized).toContain(normalizedReference.attachmentId);
		expect(serialized).not.toContain(normalizedImage.data);
		expect(JSON.stringify(events)).not.toContain(normalizedImage.data);

		const restarted = await transcript.restore(INPUT);
		expect(media.read).toHaveBeenCalledTimes(2);
		expect(restarted.messages).toContainEqual(
			expect.objectContaining({
				role: "toolResult",
				content: [{ type: "text", text: "captured" }, normalizedImage],
			}),
		);
	});

	it("keeps tool.started repairable when Pi converts media finalization failure into an error result", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
			media: {
				read: async () => [],
				captureToolResult: async () => {
					throw new Error("media capture failed");
				},
			},
		});
		const restore = await transcript.restore(INPUT);
		let executions = 0;
		const inspect = tool("inspect", async () => {
			executions += 1;
			return {
				content: [
					{ type: "image" as const, data: Buffer.from("effect-image").toString("base64"), mimeType: "image/png" },
				],
				details: {},
			};
		});
		let providerCalls = 0;
		const { agent, events } = createAgent(
			(model) => {
				providerCalls += 1;
				return providerCalls === 1
					? messageResponse(assistant([toolCall("call-failed-media", "inspect")], "toolUse", model))
					: textResponse("must not run");
			},
			undefined,
			{
				model: {
					id: "vision-model",
					name: "Vision model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text", "image"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 1,
					maxTokens: 1,
				},
				transcript: { transcript, restore },
				toolSource: () => [inspect],
			},
		);

		await expect(agent.start(INPUT, "failed-tool-media").completion).resolves.toMatchObject({ status: "failed" });
		expect(executions).toBe(1);
		expect(providerCalls).toBe(1);
		const beforeRepair = await store.load(INPUT, 32_768);
		expect(beforeRepair.records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"tool.started",
		]);
		expect(
			events.some(
				(event) =>
					event.data.type === "item.completed" &&
					event.data.item.itemType === "tool-result" &&
					event.data.item.toolCallId === "call-failed-media",
			),
		).toBe(false);
		expect(
			events.some(
				(event) =>
					event.data.type === "item.started" &&
					event.data.item.itemType === "tool-call" &&
					event.data.item.toolCallId === "call-failed-media",
			),
		).toBe(true);
		expect(() => agent.start({ ...INPUT, inputId: "again" }, "again")).toThrow("restore a fresh Agent");

		const repaired = await transcript.restore(INPUT);
		expect(executions).toBe(1);
		expect(repaired.messages).toContainEqual(
			expect.objectContaining({
				role: "toolResult",
				toolCallId: "call-failed-media",
				content: [{ type: "text", text: expect.stringContaining("transcript_tool_outcome_unknown") }],
			}),
		);
	});

	it("repairs a successful reliable Tool from its receipt after media capture fails without replaying the effect", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
			recoverStartedTool: async ({ start }) => {
				if (start.operationId !== "operation-receipt") return undefined;
				return {
					outcome: "completed",
					content: [{ type: "text", text: "authoritative receipt: succeeded" }],
					isError: false,
					timestamp: 2,
				};
			},
			media: {
				read: async () => [],
				captureToolResult: async () => {
					throw new Error("media capture failed after receipt");
				},
			},
		});
		const restore = await transcript.restore(INPUT);
		let executions = 0;
		const reliable: DurableExecutionAgentTool = {
			name: "reliable",
			label: "reliable",
			description: "reliable media test",
			parameters: Type.Object({}),
			[DURABLE_TOOL_EXECUTION]: async () => ({
				operationId: "operation-receipt",
				execute: async () => {
					executions += 1;
					return {
						content: [
							{ type: "image" as const, data: Buffer.from("receipt-image").toString("base64"), mimeType: "image/png" },
						],
						details: {},
					};
				},
			}),
			execute: async () => {
				throw new Error("The prepared durable execution must own the effect.");
			},
		};
		let providerCalls = 0;
		const { agent } = createAgent(
			(model) => {
				providerCalls += 1;
				return messageResponse(assistant([toolCall("call-receipt", "reliable")], "toolUse", model));
			},
			undefined,
			{
				model: {
					id: "vision-model",
					name: "Vision model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text", "image"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 1,
					maxTokens: 1,
				},
				transcript: { transcript, restore },
				toolSource: () => [reliable],
			},
		);

		await expect(agent.start(INPUT, "receipt-media-failure").completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(1);
		expect(executions).toBe(1);

		const repaired = await transcript.restore(INPUT);
		expect(executions).toBe(1);
		expect(repaired.messages).toContainEqual(
			expect.objectContaining({
				role: "toolResult",
				toolCallId: "call-receipt",
				content: [{ type: "text", text: "authoritative receipt: succeeded" }],
			}),
		);
		const stored = await store.load(INPUT, 32_768);
		expect(stored.records).toContainEqual(expect.objectContaining({ type: "tool.completed", outcome: "completed" }));
	});

	it("reapplies the whole Tool-result budget after media normalization", async () => {
		const normalizedBytes = Buffer.alloc(512, 1);
		const normalizedReference = imageReference(normalizedBytes);
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
			media: {
				read: async () => [],
				captureToolResult: async () => ({
					content: [
						{
							type: "image",
							data: normalizedBytes.toString("base64"),
							mimeType: normalizedReference.mediaType,
						},
					],
					durableContent: [normalizedReference],
				}),
			},
		});
		const restore = await transcript.restore(INPUT);
		let executions = 0;
		const inspect = tool("inspect", async () => {
			executions += 1;
			return {
				content: [{ type: "image" as const, data: Buffer.from("x").toString("base64"), mimeType: "image/png" }],
				details: {},
			};
		});
		let providerCalls = 0;
		const { agent } = createAgent(
			(model) => {
				providerCalls += 1;
				return messageResponse(assistant([toolCall("call-expanded", "inspect")], "toolUse", model));
			},
			undefined,
			{
				model: {
					id: "vision-model",
					name: "Vision model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text", "image"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 1,
					maxTokens: 1,
				},
				transcript: { transcript, restore },
				toolSource: () => [inspect],
				toolLimits: {
					maxTools: 16,
					maxCatalogBytes: 16_384,
					maxSchemaDepth: 16,
					maxRequestSources: 16,
					maxPolicyRevisionBytes: 128,
					maxArgumentsBytes: 4_096,
					maxArgumentsDepth: 16,
					maxResultContentParts: 16,
					maxResultBytes: 256,
					maxResultDepth: 16,
					maxResultUpdates: 64,
				},
			},
		);

		await expect(agent.start(INPUT, "expanded-media").completion).resolves.toMatchObject({ status: "failed" });
		expect({ executions, providerCalls }).toEqual({ executions: 1, providerCalls: 1 });
		const beforeRepair = await store.load(INPUT, 32_768);
		expect(beforeRepair.records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"tool.started",
		]);
		await expect(transcript.restore(INPUT)).resolves.toMatchObject({
			messages: expect.arrayContaining([expect.objectContaining({ role: "toolResult", toolCallId: "call-expanded" })]),
		});
		expect(executions).toBe(1);
	});

	it("does not append a second durable turn closure when a later event projection fails", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		let failedProjection = false;
		const { agent } = createAgent(
			() => textResponse("done"),
			(_agent, _coordinate, data) => {
				if (data.type === "turn.completed" && !failedProjection) {
					failedProjection = true;
					throw new Error("event sink failed");
				}
			},
			{ transcript: { transcript, restore } },
		);

		await expect(agent.start(INPUT, "projection-failure").completion).resolves.toMatchObject({ status: "failed" });
		await expect(transcript.restore(INPUT)).resolves.toMatchObject({
			revision: "3",
			messages: [{ role: "user" }, { role: "assistant", content: [{ text: "done" }] }],
		});
		const stored = await store.load(INPUT, 32_768);
		expect(stored.records.filter((record) => record.type === "turn.closed")).toHaveLength(1);
	});

	it("commits a completed checkpoint before installing its replacement into Pi history", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		const summarize = vi.fn(async () => ({
			retainedFrom: 2,
			message: {
				...assistant([{ type: "text", text: "Earlier conversation summary." }]),
				usage: {
					input: 40,
					output: 5,
					cacheRead: 0,
					cacheWrite: 0,
					totalTokens: 45,
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				},
			},
		}));
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async ({ messages }) => {
				if (messages.length === 0) return 0;
				if (JSON.stringify(messages).includes("Earlier conversation summary.")) return messages.length === 1 ? 20 : 50;
				return 90;
			},
			summarize,
		});
		const contexts: Context[] = [];
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				return textResponse(contexts.length === 1 ? "first answer" : "second answer");
			},
			undefined,
			{
				model: {
					id: "test-model",
					name: "Test model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 100,
					maxTokens: 20,
				},
				transcript: { transcript, restore },
				transcriptCompactor: compactor,
			},
		);

		await expect(agent.start(INPUT, "first-run").completion).resolves.toMatchObject({ status: "completed" });
		await expect(agent.start({ ...INPUT, inputId: "second-input" }, "second-run").completion).resolves.toMatchObject({
			status: "completed",
		});

		expect(contexts[1]?.messages).toMatchObject([
			{ role: "user", content: [{ type: "text", text: "Earlier conversation summary." }] },
			{ role: "user" },
		]);
		expect(summarize).toHaveBeenCalledTimes(1);
		const stored = await store.load(INPUT, 32_768);
		expect(stored.records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"turn.closed",
			"context.checkpoint",
			"message.completed",
			"message.completed",
			"turn.closed",
		]);
	});

	it("keeps a matching live Pi Agent usable when summary preparation fails before checkpoint commit", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		let summaryAttempts = 0;
		const exactCounts = [90, 90, 30, 50];
		let countIndex = 0;
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async () => exactCounts[countIndex++] ?? 50,
			summarize: async (request) => {
				expect(JSON.stringify(request)).not.toContain("providerThinkingLevel");
				expect(JSON.stringify(request)).not.toContain("private-effort");
				summaryAttempts += 1;
				if (summaryAttempts === 1) throw new Error("summary provider failed");
				return {
					retainedFrom: 2,
					message: {
						...assistant([{ type: "text", text: "Recovered summary." }]),
						usage: {
							input: 40,
							output: 5,
							cacheRead: 0,
							cacheWrite: 0,
							totalTokens: 45,
							cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
						},
					},
				};
			},
		});
		const contexts: Context[] = [];
		const { agent, events } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				return messageResponse({
					...assistant([{ type: "text", text: contexts.length === 1 ? "first answer" : "second answer" }]),
					providerThinkingLevel: "private-effort",
				});
			},
			undefined,
			{
				model: {
					id: "test-model",
					name: "Test model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 100,
					maxTokens: 20,
				},
				transcript: { transcript, restore },
				transcriptCompactor: compactor,
			},
		);

		await expect(agent.start(INPUT, "first-run").completion).resolves.toMatchObject({ status: "failed" });
		await expect(store.load(INPUT, 32_768)).resolves.toMatchObject({ revision: "3" });
		await expect(agent.start({ ...INPUT, inputId: "second-input" }, "second-run").completion).resolves.toMatchObject({
			status: "completed",
		});

		expect(contexts[1]?.messages).toMatchObject([
			{ role: "user" },
			{ role: "assistant", content: [{ text: "first answer" }], providerThinkingLevel: "private-effort" },
			{ role: "user" },
		]);
		expect(summaryAttempts).toBe(2);
		expect(JSON.stringify(events)).not.toContain("providerThinkingLevel");
		expect(JSON.stringify(events)).not.toContain("private-effort");
		const restarted = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		await expect(restarted.restore(INPUT)).resolves.toMatchObject({
			messages: [
				{ role: "user", content: [{ text: "Recovered summary." }] },
				{ role: "user" },
				{ role: "assistant", content: [{ text: "second answer" }], providerThinkingLevel: "private-effort" },
			],
		});
		const stored = await store.load(INPUT, 32_768);
		expect(stored.records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"turn.closed",
			"message.completed",
			"message.completed",
			"turn.closed",
			"context.checkpoint",
		]);
	});

	it("keeps a matching live Pi Agent usable when checkpoint validation fails before append", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 512,
			maxCommitBytes: 2_048,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		let summaryAttempts = 0;
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async () => (summaryAttempts === 0 ? 90 : 20),
			summarize: async () => {
				summaryAttempts += 1;
				return {
					retainedFrom: 2,
					message: {
						...assistant([{ type: "text", text: "x".repeat(1_000) }]),
						usage: {
							input: 40,
							output: 5,
							cacheRead: 0,
							cacheWrite: 0,
							totalTokens: 45,
							cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
						},
					},
				};
			},
		});
		let providerCalls = 0;
		const { agent } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse(providerCalls === 1 ? "first" : "second");
			},
			undefined,
			{
				model: {
					id: "test-model",
					name: "Test model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 100,
					maxTokens: 20,
				},
				transcript: { transcript, restore },
				transcriptCompactor: compactor,
			},
		);

		await expect(agent.start(INPUT, "invalid-checkpoint-run").completion).resolves.toMatchObject({ status: "failed" });
		await expect(store.load(INPUT, 32_768)).resolves.toMatchObject({ revision: "3" });
		await expect(agent.start({ ...INPUT, inputId: "next-input" }, "next-run").completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(providerCalls).toBe(2);
		const stored = await store.load(INPUT, 32_768);
		expect(stored.records.some((record) => record.type === "context.checkpoint")).toBe(false);
	});

	it("uses Pi turn stopping when compaction fails after a Tool turn", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async () => 90,
			summarize: async () => {
				throw new Error("summary provider failed");
			},
		});
		let toolExecutions = 0;
		const inspect = tool("inspect", async () => {
			toolExecutions += 1;
			return { content: [{ type: "text", text: "inspected" }], details: {} };
		});
		let providerCalls = 0;
		const { agent } = createAgent(
			() => {
				providerCalls += 1;
				return providerCalls === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("must not run");
			},
			undefined,
			{
				model: {
					id: "test-model",
					name: "Test model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 100,
					maxTokens: 20,
				},
				transcript: { transcript, restore },
				transcriptCompactor: compactor,
				toolSource: () => [inspect],
			},
		);

		await expect(agent.start(INPUT, "tool-compaction-failure").completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(1);
		expect(toolExecutions).toBe(1);
		const stored = await store.load(INPUT, 32_768);
		expect(stored.records.some((record) => record.type === "context.checkpoint")).toBe(false);
	});

	it("installs a successful Tool-turn checkpoint into Pi's next provider context", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		const summarize = vi.fn(async () => ({
			retainedFrom: 3,
			message: {
				...assistant([{ type: "text", text: "Compacted tool turn." }]),
				usage: {
					input: 40,
					output: 5,
					cacheRead: 0,
					cacheWrite: 0,
					totalTokens: 45,
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				},
			},
		}));
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async ({ messages }) => {
				if (messages.length === 0) return 0;
				if (JSON.stringify(messages).includes("Compacted tool turn.")) return messages.length === 1 ? 20 : 40;
				return 90;
			},
			summarize,
		});
		const inspect = tool("inspect", async () => ({
			content: [{ type: "text", text: "raw tool result" }],
			details: {},
		}));
		const contexts: Context[] = [];
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				return contexts.length === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("finished");
			},
			undefined,
			{
				model: {
					id: "test-model",
					name: "Test model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 100,
					maxTokens: 20,
				},
				transcript: { transcript, restore },
				transcriptCompactor: compactor,
				toolSource: () => [inspect],
			},
		);

		await expect(agent.start(INPUT, "tool-compaction-success").completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(contexts).toHaveLength(2);
		expect(contexts[1]?.messages).toMatchObject([
			{ role: "user", content: [{ type: "text", text: "Compacted tool turn." }] },
		]);
		expect(JSON.stringify(contexts[1]?.messages)).not.toContain("raw tool result");
		expect(summarize).toHaveBeenCalledTimes(1);
	});

	it("does not reinstall an old checkpoint after Pi stops before queued control continuation", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		const summarize = vi.fn(async () => ({
			retainedFrom: 3,
			message: {
				...assistant([{ type: "text", text: "First turn checkpoint." }]),
				usage: {
					input: 40,
					output: 5,
					cacheRead: 0,
					cacheWrite: 0,
					totalTokens: 45,
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				},
			},
		}));
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async ({ messages }) => {
				if (messages.length === 0) return 0;
				if (JSON.stringify(messages).includes("First turn checkpoint.")) return 40;
				return 90;
			},
			summarize,
		});
		const inspect = tool("inspect", async () => ({
			content: [{ type: "text", text: "tool result" }],
			details: {},
		}));
		const contexts: Context[] = [];
		let requests = 0;
		let stopChecks = 0;
		let control: Promise<GameControlResult> | undefined;
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				requests += 1;
				if (requests === 1) return messageResponse(assistant([toolCall("call-first", "inspect")], "toolUse"));
				if (requests === 2) return messageResponse(assistant([toolCall("call-second", "inspect")], "toolUse"));
				return textResponse("finished");
			},
			(currentAgent, coordinate, data) => {
				if (data.type === "turn.completed" && requests === 1 && !control) {
					control = currentAgent.control(coordinate, { type: "steer", text: "queued after checkpoint" });
				}
			},
			{
				model: {
					id: "test-model",
					name: "Test model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 100,
					maxTokens: 20,
				},
				transcript: { transcript, restore },
				transcriptCompactor: compactor,
				toolSource: () => [inspect],
				shouldStopAfterTurn: () => {
					stopChecks += 1;
					return stopChecks === 1;
				},
			},
		);

		await expect(agent.start(INPUT, "checkpoint-stop-control").completion).resolves.toMatchObject({
			status: "completed",
		});
		if (!control) throw new Error("Expected queued control.");
		await expect(control).resolves.toMatchObject({ accepted: true });
		expect(contexts).toHaveLength(3);
		expect(contexts[2]?.messages).toMatchObject([
			{ role: "user", content: [{ type: "text", text: "First turn checkpoint." }] },
			{ role: "user", content: "queued after checkpoint" },
			{ role: "assistant", content: [{ type: "toolCall", id: "call-second", name: "inspect" }] },
			{ role: "toolResult", toolCallId: "call-second", content: [{ type: "text", text: "tool result" }] },
		]);
		expect(summarize).toHaveBeenCalledTimes(1);
	});

	it("requires a fresh Agent when another writer wins the checkpoint revision", async () => {
		const backingStore = new InMemoryTranscriptStore();
		let raced = false;
		const racingStore: TranscriptStore = {
			load: (stream, maxBytes) => backingStore.load(stream, maxBytes),
			append: async (stream, expectedRevision, records) => {
				const first = records[0];
				if (!raced && first?.type === "context.checkpoint") {
					raced = true;
					const competing = structuredClone(first);
					competing.recordId = "competing-checkpoint";
					await backingStore.append(stream, expectedRevision, [competing]);
				}
				return backingStore.append(stream, expectedRevision, records);
			},
		};
		let recordId = 0;
		const transcript = new DurableTranscript(racingStore, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
		});
		const restore = await transcript.restore(INPUT);
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async ({ messages }) => (messages.length === 2 ? 90 : 20),
			summarize: async () => ({
				retainedFrom: 2,
				message: {
					...assistant([{ type: "text", text: "Winning summary." }]),
					usage: {
						input: 40,
						output: 5,
						cacheRead: 0,
						cacheWrite: 0,
						totalTokens: 45,
						cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
					},
				},
			}),
		});
		const { agent } = createAgent(() => textResponse("answer"), undefined, {
			model: {
				id: "test-model",
				name: "Test model",
				api: "openai-responses",
				provider: "openai",
				baseUrl: "https://example.invalid",
				reasoning: true,
				input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
				contextWindow: 100,
				maxTokens: 20,
			},
			transcript: { transcript, restore },
			transcriptCompactor: compactor,
		});

		await expect(agent.start(INPUT, "raced-run").completion).resolves.toMatchObject({ status: "failed" });
		expect(() => agent.start({ ...INPUT, inputId: "next-input" }, "next-run")).toThrow("restore a fresh Agent");
		await expect(transcript.restore(INPUT)).resolves.toMatchObject({
			revision: "4",
			messages: [
				{
					role: "user",
					content: [{ type: "text", text: "Winning summary." }],
				},
			],
		});
		const stored = await backingStore.load(INPUT, 32_768);
		expect(stored.records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"turn.closed",
			"context.checkpoint",
		]);
	});

	it("projects verified image bytes to Pi while canonical events retain only durable references", async () => {
		const png = Buffer.from(
			"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAACXBIWXMAAAPoAAAD6AG1e1JrAAAADElEQVQImWNgZGIGAAAOAAeCcsnOAAAAAElFTkSuQmCC",
			"base64",
		);
		const base64 = png.toString("base64");
		const reference = {
			type: "image-reference" as const,
			attachmentId: `img_${createHash("sha256").update(png).digest("hex")}`,
			mediaType: "image/png" as const,
			bytes: png.byteLength,
			width: 1,
			height: 1,
			name: "pixel.png",
		};
		const media: TranscriptMedia = {
			captureToolResult: async () => {
				throw new Error("unused");
			},
			read: vi.fn(async (requests: Parameters<TranscriptMedia["read"]>[0]) =>
				requests.map(() => ({ type: "image" as const, data: base64, mimeType: "image/png" })),
			),
		};
		const transcriptStore = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(transcriptStore, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `user-media-record-${++recordId}`,
			media,
		});
		const restore = await transcript.restore(INPUT);
		const contexts: Context[] = [];
		const { agent, events } = createAgent(
			(model, context) => {
				expect(media.read).toHaveBeenCalledTimes(1);
				contexts.push(copyContext(context));
				return textResponse("seen", [], model);
			},
			undefined,
			{
				model: {
					id: "vision-model",
					name: "Vision model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text", "image"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 1,
					maxTokens: 1,
				},
				transcript: { transcript, restore },
				projectInput: async (_input, signal, coordinate) => {
					signal.throwIfAborted();
					expect(coordinate).toMatchObject({ runId: "run-image", turn: "0" });
					return {
						message: {
							role: "user",
							content: [
								{ type: "text", text: "inspect" },
								{ type: "image", data: base64, mimeType: "image/png" },
							],
							timestamp: 1,
						},
						transcriptContent: [{ type: "text", text: "inspect" }, reference],
					};
				},
				inputProjectionLimits: {
					maxContentParts: 2,
					maxTextBytes: 7,
					maxReferenceBytes: Buffer.byteLength(JSON.stringify(reference), "utf8"),
					maxImageBytes: png.byteLength,
					maxTotalImageBytes: png.byteLength,
				},
			},
		);

		await expect(agent.start(INPUT, "run-image").completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts[0]?.messages[0]).toMatchObject({
			role: "user",
			content: [
				{ type: "text", text: "inspect" },
				{ type: "image", data: base64, mimeType: "image/png" },
			],
		});
		const userItems = events.flatMap((event) =>
			event.data.type === "item.completed" &&
			event.data.item.itemType === "message" &&
			event.data.item.role === "user" &&
			!("context" in event.data.item)
				? [event.data.item]
				: [],
		);
		expect(userItems).toHaveLength(1);
		expect(userItems[0]?.content).toEqual([{ type: "text", text: "inspect" }, reference]);
		expect(JSON.stringify(events)).not.toContain(base64);
		const stored = await transcriptStore.load(INPUT, 32_768);
		expect(JSON.stringify(stored.records)).toContain(reference.attachmentId);
		expect(JSON.stringify(stored.records)).not.toContain(base64);

		const restarted = await transcript.restore(INPUT);
		expect(media.read).toHaveBeenCalledTimes(2);
		expect(restarted.messages[0]).toEqual(contexts[0]?.messages[0]);
	});

	it("fails before the provider for forged, oversized, or unsupported image projections", async () => {
		const bytes = Buffer.from("verified image bytes");
		const base64 = bytes.toString("base64");
		const reference = {
			type: "image-reference" as const,
			attachmentId: `img_${createHash("sha256").update(bytes).digest("hex")}`,
			mediaType: "image/png" as const,
			bytes: bytes.byteLength,
			width: 1,
			height: 1,
		};
		for (const variant of ["forged", "oversized", "unsupported"] as const) {
			let providerCalls = 0;
			const { agent } = createAgent(
				() => {
					providerCalls += 1;
					return textResponse("must not run");
				},
				undefined,
				{
					...(variant === "unsupported"
						? {}
						: {
								model: {
									id: "vision-model",
									name: "Vision model",
									api: "openai-responses" as const,
									provider: "openai",
									baseUrl: "https://example.invalid",
									reasoning: true,
									input: ["text", "image"] as const,
									cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
									contextWindow: 1,
									maxTokens: 1,
								},
							}),
					projectInput: () => ({
						message: {
							role: "user",
							content: [{ type: "image", data: base64, mimeType: "image/png" }],
							timestamp: 1,
						},
						transcriptContent: [
							variant === "forged" ? { ...reference, attachmentId: `img_${"0".repeat(64)}` } : reference,
						],
					}),
					inputProjectionLimits: {
						maxContentParts: 1,
						maxTextBytes: 1,
						maxReferenceBytes: Buffer.byteLength(JSON.stringify(reference), "utf8"),
						maxImageBytes: variant === "oversized" ? bytes.byteLength - 1 : bytes.byteLength,
						maxTotalImageBytes: bytes.byteLength,
					},
				},
			);
			await expect(agent.start(INPUT, `run-${variant}`).completion).resolves.toMatchObject({ status: "failed" });
			expect(providerCalls).toBe(0);
		}
	});

	it("rejects aggregate canonical image-reference metadata before provider or message publication", async () => {
		const bytes = Buffer.from("bounded image metadata");
		const base64 = bytes.toString("base64");
		const reference = {
			type: "image-reference" as const,
			attachmentId: `img_${createHash("sha256").update(bytes).digest("hex")}`,
			mediaType: "image/png" as const,
			bytes: bytes.byteLength,
			width: 1,
			height: 1,
			name: "n".repeat(256),
		};
		let providerCalls = 0;
		const { agent, events } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			undefined,
			{
				model: {
					id: "vision-model",
					name: "Vision model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text", "image"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 1,
					maxTokens: 1,
				},
				projectInput: () => ({
					message: {
						role: "user",
						content: [
							{ type: "image", data: base64, mimeType: "image/png" },
							{ type: "image", data: base64, mimeType: "image/png" },
						],
						timestamp: 1,
					},
					transcriptContent: [reference, reference],
				}),
				inputProjectionLimits: {
					maxContentParts: 2,
					maxTextBytes: 1,
					maxReferenceBytes: 500,
					maxImageBytes: bytes.byteLength,
					maxTotalImageBytes: bytes.byteLength * 2,
				},
			},
		);

		await expect(agent.start(INPUT, "run-reference-metadata").completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(0);
		expect(
			events.some(
				(event) =>
					event.data.type === "item.started" &&
					event.data.item.itemType === "message" &&
					event.data.item.role === "user",
			),
		).toBe(false);
	});

	it("snapshots structural input projections once and ignores unrelated host fields", async () => {
		const reads = new Map<PropertyKey, number>();
		const counted = <T extends object>(value: T, prefix: string): T =>
			new Proxy(value, {
				get(target, key, receiver) {
					reads.set(`${prefix}.${String(key)}`, (reads.get(`${prefix}.${String(key)}`) ?? 0) + 1);
					return Reflect.get(target, key, receiver);
				},
			});
		const withUnreadExtra = <T extends object>(value: T): T => {
			Object.defineProperty(value, "unrelated", {
				enumerable: true,
				get: () => {
					throw new Error("Unrelated projection fields must not be read.");
				},
			});
			return value;
		};

		const messagePart = counted(withUnreadExtra({ type: "text" as const, text: "inspect" }), "messagePart");
		const transcriptPart = counted(withUnreadExtra({ type: "text" as const, text: "inspect" }), "transcriptPart");
		const messageParts = counted([messagePart], "messageParts");
		const transcriptParts = counted([transcriptPart], "transcriptParts");
		const message = counted(withUnreadExtra({ role: "user" as const, content: messageParts, timestamp: 1 }), "message");
		const projection = counted(withUnreadExtra({ message, transcriptContent: transcriptParts }), "projection");
		const contexts: Context[] = [];
		const { agent, events } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				return textResponse("done");
			},
			undefined,
			{
				projectInput: () => projection,
				inputProjectionLimits: {
					maxContentParts: 1,
					maxTextBytes: 7,
					maxReferenceBytes: 1,
					maxImageBytes: 1,
					maxTotalImageBytes: 1,
				},
			},
		);

		await expect(agent.start(INPUT, "run-structural-projection").completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(contexts[0]?.messages[0]).toEqual({
			role: "user",
			content: [{ type: "text", text: "inspect" }],
			timestamp: 1,
		});
		const userItem = events.find(
			(event) =>
				event.data.type === "item.completed" &&
				event.data.item.itemType === "message" &&
				event.data.item.role === "user",
		);
		expect(userItem?.data).toMatchObject({
			type: "item.completed",
			item: { content: [{ type: "text", text: "inspect" }] },
		});
		expect(Object.fromEntries(reads)).toEqual({
			"projection.then": 1,
			"projection.message": 1,
			"projection.transcriptContent": 1,
			"message.role": 1,
			"message.content": 1,
			"message.timestamp": 1,
			"transcriptParts.length": 1,
			"transcriptParts.0": 1,
			"transcriptPart.type": 1,
			"transcriptPart.text": 1,
			"messageParts.length": 1,
			"messageParts.0": 1,
			"messagePart.type": 1,
			"messagePart.text": 1,
		});
	});

	it("requires and snapshots explicit custom input projection limits", async () => {
		expect(() =>
			createAgent(() => textResponse("done"), undefined, {
				projectInput: () => ({
					message: { role: "user", content: "stable", timestamp: 1 },
					transcriptContent: [{ type: "text", text: "stable" }],
				}),
			}),
		).toThrowError("invalid_configuration");

		const limits = {
			maxContentParts: 1,
			maxTextBytes: 6,
			maxReferenceBytes: 1,
			maxImageBytes: 1,
			maxTotalImageBytes: 1,
		};
		const overrides: Partial<GameAgentOptions> = {
			projectInput: () => ({
				message: { role: "user", content: "stable", timestamp: 1 },
				transcriptContent: [{ type: "text", text: "stable" }],
			}),
			inputProjectionLimits: limits,
		};
		const contexts: Context[] = [];
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				return textResponse("done");
			},
			undefined,
			overrides,
		);
		overrides.projectInput = () => {
			throw new Error("mutated projector must not run");
		};
		limits.maxTextBytes = 1;
		await expect(agent.start(INPUT, "run-snapshot").completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts[0]?.messages[0]).toMatchObject({ role: "user", content: [{ type: "text", text: "stable" }] });
	});

	it("persists the exact per-request context injected after Pi transforms", async () => {
		const contexts: Context[] = [];
		let revision = 0;
		const modelStream: StreamFn = (_model, context) => {
			contexts.push(copyContext(context));
			return textResponse("done");
		};
		const { agent, events } = createAgent(modelStream, undefined, {
			contextSources: [
				{
					sourceId: "memory",
					load: () => {
						revision += 1;
						return {
							references: [{ referenceId: "memory-1", revision: String(revision), content: { fact: revision } }],
						};
					},
				},
			],
			contextLimits: { maxReferencesPerSnapshot: 2, maxSnapshotBytes: 1_024 },
			transformContext: async (messages) => messages.slice(-1),
		});

		await expect(agent.start({ ...INPUT, inputId: "input-1" }, "run-1").completion).resolves.toMatchObject({
			status: "completed",
		});
		await expect(agent.start({ ...INPUT, inputId: "input-2" }, "run-2").completion).resolves.toMatchObject({
			status: "completed",
		});

		expect(contexts).toHaveLength(2);
		for (const [index, context] of contexts.entries()) {
			expect(context.messages).toHaveLength(2);
			const injected = context.messages[0];
			expect(injected).toMatchObject({ role: "user" });
			if (injected?.role !== "user" || typeof injected.content !== "string") {
				throw new Error("Expected an injected user context message.");
			}
			expect(JSON.parse(injected.content)).toMatchObject({
				contextVersion: "1",
				references: [{ sourceId: "memory", referenceId: "memory-1", revision: String(index + 1) }],
			});
		}

		const contextItems = events.flatMap((event) => {
			if (event.data.type !== "item.completed" || event.data.item.itemType !== "message") return [];
			return "context" in event.data.item ? [event.data.item] : [];
		});
		expect(contextItems).toHaveLength(2);
		expect(contextItems.map((item) => item.audience)).toEqual([{ visibility: "internal" }, { visibility: "internal" }]);
		expect(contextItems.map((item) => (item.content[0]?.type === "text" ? item.content[0].text : undefined))).toEqual(
			contexts.map((context) => {
				const message = context.messages[0];
				return message?.role === "user" && typeof message.content === "string" ? message.content : undefined;
			}),
		);
	});

	it("snapshots context source identity and budgets at Agent construction", async () => {
		const source = {
			sourceId: "stable-source",
			load: () => ({ references: [{ referenceId: "reference-1", revision: "1", content: { stable: true } }] }),
		};
		const limits = { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 };
		const { agent, events } = createAgent(() => textResponse("done"), undefined, {
			contextSources: [source],
			contextLimits: limits,
		});
		source.sourceId = "mutated-source";
		source.load = () => {
			throw new Error("mutated loader must not run");
		};
		limits.maxSnapshotBytes = 1;

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		const contextItem = events.find(
			(event) =>
				event.data.type === "item.completed" && event.data.item.itemType === "message" && "context" in event.data.item,
		);
		expect(contextItem?.data).toMatchObject({
			type: "item.completed",
			item: { context: { references: [{ sourceId: "stable-source" }] } },
		});
	});

	it("snapshots request source identity and collector at Agent construction", async () => {
		const source = {
			sourceId: "stable-request-source",
			collect: () => ({
				context: { references: [{ referenceId: "catalog", revision: "1", content: { stable: true } }] },
			}),
		};
		const { agent, events } = createAgent(() => textResponse("done"), undefined, {
			requestSources: [source],
			contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 },
		});
		source.sourceId = "mutated-request-source";
		source.collect = () => {
			throw new Error("mutated collector must not run");
		};

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		const contextItem = events.find(
			(event) =>
				event.data.type === "item.completed" && event.data.item.itemType === "message" && "context" in event.data.item,
		);
		expect(contextItem?.data).toMatchObject({
			type: "item.completed",
			item: { context: { references: [{ sourceId: "stable-request-source" }] } },
		});
	});

	it("refreshes context per provider call without splitting Pi Tool call and result pairs", async () => {
		const contexts: Context[] = [];
		let providerCall = 0;
		let contextRevision = 0;
		const inspect = tool("inspect", async () => ({
			content: [{ type: "text", text: "inspected" }],
			details: {},
		}));
		const modelStream: StreamFn = (_model, context) => {
			contexts.push(copyContext(context));
			providerCall += 1;
			return providerCall === 1
				? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
				: textResponse("done");
		};
		const { agent } = createAgent(modelStream, undefined, {
			contextSources: [
				{
					sourceId: "memory",
					load: () => {
						contextRevision += 1;
						return {
							references: [
								{ referenceId: "memory-1", revision: String(contextRevision), content: { contextRevision } },
							],
						};
					},
				},
			],
			contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 },
			toolSource: () => [inspect],
		});

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts).toHaveLength(2);
		expect(contextRevision).toBe(2);
		const second = contexts[1];
		if (!second) throw new Error("Expected the second provider context.");
		expect(second.messages.map((message) => message.role)).toEqual(["user", "user", "assistant", "toolResult"]);
		const injected = second.messages[0];
		if (injected?.role !== "user" || typeof injected.content !== "string") {
			throw new Error("Expected a refreshed context message.");
		}
		expect(JSON.parse(injected.content)).toMatchObject({ references: [{ revision: "2" }] });
		expect(second.messages.filter((message) => message.role === "user")).toHaveLength(2);
	});

	it("refreshes aligned context images for every Pi request without splitting Tool history or persisting bytes", async () => {
		const imageBytes = [Buffer.from("context-image-1"), Buffer.from("context-image-2")];
		const contexts: Context[] = [];
		let providerCalls = 0;
		let contextRevision = 0;
		const inspect = tool("inspect", async () => ({
			content: [{ type: "text", text: "inspected" }],
			details: {},
		}));
		const { agent, events } = createAgent(
			(model, context) => {
				contexts.push(copyContext(context));
				providerCalls += 1;
				return providerCalls === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse", model))
					: textResponse("done", [], model);
			},
			undefined,
			{
				model: {
					id: "vision-model",
					name: "Vision model",
					api: "openai-responses",
					provider: "openai",
					baseUrl: "https://example.invalid",
					reasoning: true,
					input: ["text", "image"],
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
					contextWindow: 1,
					maxTokens: 1,
				},
				contextSources: [
					{
						sourceId: "visual",
						provenanceKind: "observation",
						load: () => {
							contextRevision += 1;
							const bytes = imageBytes[contextRevision - 1];
							if (!bytes) throw new Error("Unexpected context refresh.");
							return {
								references: [
									{
										referenceId: "current-frame",
										revision: String(contextRevision),
										content: { frame: contextRevision },
									},
								],
								images: [
									{
										contextReferenceId: "current-frame",
										content: {
											type: "image",
											data: bytes.toString("base64"),
											mimeType: "image/png",
										},
										reference: imageReference(bytes),
									},
								],
							};
						},
					},
				],
				contextLimits: {
					maxReferencesPerSnapshot: 1,
					maxSnapshotBytes: 1_024,
					media: {
						maxContentParts: 2,
						maxImageBytes: Math.max(...imageBytes.map((bytes) => bytes.byteLength)),
						maxTotalImageBytes: Math.max(...imageBytes.map((bytes) => bytes.byteLength)),
					},
				},
				toolSource: () => [inspect],
			},
		);

		await expect(agent.start(INPUT, "run-visual").completion).resolves.toMatchObject({ status: "completed" });
		expect({ providerCalls, contextRevision }).toEqual({ providerCalls: 2, contextRevision: 2 });
		expect(contexts).toHaveLength(2);
		for (const [index, context] of contexts.entries()) {
			const injected = context.messages[0];
			if (injected?.role !== "user" || !Array.isArray(injected.content)) {
				throw new Error("Expected an aligned context image message.");
			}
			expect(injected.content).toEqual([
				expect.objectContaining({ type: "text" }),
				{
					type: "image",
					data: imageBytes[index]?.toString("base64"),
					mimeType: "image/png",
				},
			]);
			const text = injected.content[0];
			if (text?.type !== "text") throw new Error("Expected context text before its image.");
			expect(JSON.parse(text.text)).toMatchObject({ references: [{ revision: String(index + 1) }] });
		}
		expect(contexts[1]?.messages.map((message) => message.role)).toEqual(["user", "user", "assistant", "toolResult"]);

		const contextItems = events.flatMap((event) => {
			if (event.data.type !== "item.completed" || event.data.item.itemType !== "message") return [];
			return "context" in event.data.item ? [event.data.item] : [];
		});
		expect(contextItems).toHaveLength(2);
		expect(contextItems.map((item) => item.content.map((part) => part.type))).toEqual([
			["text", "image-reference"],
			["text", "image-reference"],
		]);
		for (const bytes of imageBytes) {
			expect(JSON.stringify(contextItems)).not.toContain(bytes.toString("base64"));
		}
	});

	it("rejects context images for a text-only model before provider or context publication", async () => {
		const bytes = Buffer.from("unsupported-context-image");
		let providerCalls = 0;
		const { agent, events } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			undefined,
			{
				contextSources: [
					{
						sourceId: "visual",
						load: () => ({
							references: [{ referenceId: "frame", revision: "1", content: { frame: 1 } }],
							images: [
								{
									contextReferenceId: "frame",
									content: { type: "image", data: bytes.toString("base64"), mimeType: "image/png" },
									reference: imageReference(bytes),
								},
							],
						}),
					},
				],
				contextLimits: {
					maxReferencesPerSnapshot: 1,
					maxSnapshotBytes: 1_024,
					media: {
						maxContentParts: 2,
						maxImageBytes: bytes.byteLength,
						maxTotalImageBytes: bytes.byteLength,
					},
				},
			},
		);

		await expect(agent.start(INPUT, "run-text-only").completion).resolves.toMatchObject({ status: "failed" });
		const contextEvents = events.filter(
			(event) =>
				(event.data.type === "item.started" || event.data.type === "item.completed") &&
				event.data.item.itemType === "message" &&
				"context" in event.data.item,
		);
		expect({ providerCalls, contextEvents: contextEvents.length }).toEqual({ providerCalls: 0, contextEvents: 0 });
	});

	it("collects request context and tools from one per-provider snapshot after base Tool admission", async () => {
		const contexts: Context[] = [];
		const visibleToolNames: string[][] = [];
		let providerCall = 0;
		let revision = 0;
		const inspect = tool("inspect", async () => ({
			content: [{ type: "text", text: "inspected" }],
			details: {},
		}));
		const modelStream: StreamFn = (_model, context) => {
			contexts.push(copyContext(context));
			providerCall += 1;
			return providerCall === 1
				? messageResponse(assistant([toolCall("call-skill", "skill")], "toolUse"))
				: textResponse("done");
		};
		const { agent } = createAgent(modelStream, undefined, {
			toolSource: () => [inspect],
			requestSources: [
				{
					sourceId: "skills",
					collect: (_input, _signal, _coordinate, admittedToolNames) => {
						revision += 1;
						expect(Object.isFrozen(admittedToolNames)).toBe(true);
						visibleToolNames.push([...admittedToolNames]);
						const snapshotRevision = revision;
						return {
							context: {
								references: [
									{
										referenceId: "catalog",
										revision: String(snapshotRevision),
										content: { snapshotRevision },
									},
								],
							},
							tools: [
								tool("skill", async () => ({
									content: [{ type: "text", text: `snapshot-${snapshotRevision}` }],
									details: {},
								})),
							],
						};
					},
				},
			],
			contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 },
		});

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });

		expect(visibleToolNames).toEqual([["inspect"], ["inspect"]]);
		expect(contexts).toHaveLength(2);
		for (const [index, context] of contexts.entries()) {
			expect(context.tools?.map((entry) => entry.name)).toEqual(["inspect", "skill"]);
			const injected = context.messages[0];
			if (injected?.role !== "user" || typeof injected.content !== "string") {
				throw new Error("Expected request-source context.");
			}
			expect(JSON.parse(injected.content)).toMatchObject({
				references: [{ sourceId: "skills", revision: String(index + 1) }],
			});
		}
		expect(contexts[1]?.messages).toContainEqual(
			expect.objectContaining({
				role: "toolResult",
				content: [{ type: "text", text: "snapshot-1" }],
			}),
		);
	});

	it("transforms one normalized Pi Tool result while preserving its error and host details", async () => {
		let requests = 0;
		const contexts: Context[] = [];
		const observations: unknown[] = [];
		const inspect = tool("inspect", async () => {
			throw new GameToolReportedError({
				content: [{ type: "text", text: "raw result" }],
				details: { provenance: "game" },
			});
		});
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				requests += 1;
				return requests === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("recovered");
			},
			undefined,
			{
				toolSource: () => [inspect],
				requestSources: [
					{
						sourceId: "projection",
						collect: () => ({
							transformToolResult(result, context, signal) {
								observations.push({
									result,
									context,
									resultFrozen: Object.isFrozen(result),
									contentFrozen: Object.isFrozen(result.content),
									contextFrozen: Object.isFrozen(context),
									signalAborted: signal.aborted,
								});
								return [{ type: "text", text: "projected result" }];
							},
						}),
					},
				],
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(observations).toEqual([
			expect.objectContaining({
				result: expect.objectContaining({
					content: [{ type: "text", text: "raw result" }],
					details: { provenance: "game" },
				}),
				context: expect.objectContaining({
					input: INPUT,
					coordinate: { sessionId: "session", actorId: "actor", runId: "run-1", turn: "1" },
					toolCallId: "call-inspect",
					toolName: "inspect",
					arguments: {},
					isError: true,
				}),
				resultFrozen: true,
				contentFrozen: true,
				contextFrozen: true,
				signalAborted: false,
			}),
		]);
		expect(contexts[1]?.messages.find((message) => message.role === "toolResult")).toEqual(
			expect.objectContaining({
				isError: true,
				content: [{ type: "text", text: "projected result" }],
				details: { provenance: "game" },
			}),
		);
	});

	it("fails request preparation when a result transformer cannot reach its declared input budget", async () => {
		let providerCalls = 0;
		const { agent } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			undefined,
			{
				requestSources: [
					{
						sourceId: "projection",
						collect: () => ({
							requiredToolResultBytes: 16_385,
							transformToolResult: () => undefined,
						}),
					},
				],
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(0);
	});

	it("admits bounded oversized content before transformation and enforces the final whole-result budget", async () => {
		let requests = 0;
		const contexts: Context[] = [];
		const inspect = tool("inspect", async () => ({
			content: [
				{ type: "text", text: "\u0000".repeat(50) },
				{ type: "text", text: "\u0000".repeat(50) },
			],
			details: { retained: "x".repeat(175) },
		}));
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				requests += 1;
				return requests === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("done");
			},
			undefined,
			{
				toolSource: () => [inspect],
				requestSources: [
					{
						sourceId: "projection",
						collect: () => ({
							transformToolResultTextBytes: 100,
							transformToolResult: () => [{ type: "text", text: "stored reference" }],
						}),
					},
				],
				toolLimits: {
					maxTools: 2,
					maxCatalogBytes: 4_096,
					maxSchemaDepth: 8,
					maxRequestSources: 1,
					maxPolicyRevisionBytes: 128,
					maxArgumentsBytes: 1_024,
					maxArgumentsDepth: 8,
					maxResultContentParts: 2,
					maxResultBytes: 256,
					maxResultDepth: 8,
					maxResultUpdates: 2,
				},
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts[1]?.messages.find((message) => message.role === "toolResult")).toEqual(
			expect.objectContaining({
				isError: false,
				content: [{ type: "text", text: "stored reference" }],
				details: { retained: "x".repeat(175) },
			}),
		);
	});

	it("fails before provider publication when a request source collides with an admitted Tool", async () => {
		let providerCalls = 0;
		const inspect = tool("inspect", async () => ({ content: [], details: {} }));
		const { agent, events } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			undefined,
			{
				toolSource: () => [inspect],
				requestSources: [
					{
						sourceId: "skills",
						collect: () => ({
							context: {
								references: [{ referenceId: "catalog", revision: "1", content: { visible: true } }],
							},
							tools: [inspect],
						}),
					},
				],
				contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 },
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(0);
		expect(
			events.some(
				(event) =>
					event.data.type === "item.completed" &&
					event.data.item.itemType === "message" &&
					"context" in event.data.item,
			),
		).toBe(false);
	});

	it("fails an atomic request snapshot when adding a source changes visibility of an existing candidate", async () => {
		let providerCalls = 0;
		const inspect = tool("inspect", async () => ({ content: [], details: {} }));
		const { agent } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			undefined,
			{
				toolSource: () => [inspect],
				requestSources: [
					{
						sourceId: "skills",
						collect: () => ({ tools: [tool("skill", async () => ({ content: [], details: {} }))] }),
					},
				],
				toolPolicy: (_input, candidates) => ({
					revision: "policy",
					visibleToolNames: candidates.length === 1 ? ["inspect"] : ["skill"],
				}),
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(0);
	});

	it("fails before provider and context publication when request preparation is incomplete", async () => {
		let providerCalls = 0;
		const { agent, events } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			undefined,
			{
				contextSources: [
					{
						sourceId: "memory",
						load: () => ({ references: [{ referenceId: "memory-1", revision: "1", content: { fact: true } }] }),
					},
				],
				contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 },
				toolSource: () => {
					throw new Error("authority unavailable");
				},
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(0);
		expect(
			events.some(
				(event) =>
					event.data.type === "item.completed" &&
					event.data.item.itemType === "message" &&
					"context" in event.data.item,
			),
		).toBe(false);
	});

	it("checks the host authority immediately before every Pi Tool execution", async () => {
		let providerCall = 0;
		let executed = false;
		const inspect = tool("inspect", async () => {
			executed = true;
			return { content: [{ type: "text", text: "must not execute" }], details: {} };
		});
		const modelStream: StreamFn = () => {
			providerCall += 1;
			return providerCall === 1
				? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
				: textResponse("authority rejected");
		};
		const checks: unknown[] = [];
		const { agent } = createAgent(modelStream, undefined, {
			toolSource: () => [inspect],
			beforeToolExecution: (input, execution) => {
				expect(Object.isFrozen(execution)).toBe(true);
				expect(Object.isFrozen(execution.coordinate)).toBe(true);
				expect(Object.isFrozen(execution.arguments)).toBe(true);
				checks.push({ input, execution });
				throw new Error("authority expired");
			},
		});

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(executed).toBe(false);
		expect(checks).toEqual([
			expect.objectContaining({
				input: INPUT,
				execution: expect.objectContaining({
					coordinate: { sessionId: "session", actorId: "actor", runId: "run-1", turn: "1" },
					toolCallId: "call-inspect",
					toolName: "inspect",
					arguments: {},
					policyRevision: "test-policy",
					descriptorFingerprint: expect.stringMatching(/^[a-f0-9]{64}$/u),
					catalogRevision: expect.stringMatching(/^[a-f0-9]{64}$/u),
				}),
			}),
		]);
	});

	it("requires explicit host Tool limits and policy before any Tool source is admitted", () => {
		const inspect = tool("inspect", async () => ({ content: [], details: {} }));
		expect(() =>
			createAgent(() => textResponse("unused"), undefined, {
				toolSource: () => [inspect],
				toolPolicy: undefined as never,
			}),
		).toThrow(/explicit Tool policy and Tool snapshot limits/u);
	});

	it("rejects a non-array Tool iterable without consuming it or invoking the provider", async () => {
		let iteratorReads = 0;
		let providerCalls = 0;
		const { agent } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			undefined,
			{
				toolSource: () =>
					({
						*[Symbol.iterator]() {
							iteratorReads += 1;
							yield tool("inspect", async () => ({ content: [], details: {} }));
						},
					}) as never,
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "failed" });
		expect(iteratorReads).toBe(0);
		expect(providerCalls).toBe(0);
	});

	it("bounds request sources and captures dynamic host contracts exactly once", async () => {
		expect(() => createAgent(() => textResponse("unused"), undefined, { requestSources: [] })).not.toThrow();
		const first = { sourceId: "first", collect: () => ({}) };
		const second = { sourceId: "second", collect: () => ({}) };
		expect(() =>
			createAgent(() => textResponse("unused"), undefined, {
				requestSources: [first, second],
				toolLimits: {
					maxTools: 1,
					maxCatalogBytes: 4_096,
					maxSchemaDepth: 8,
					maxRequestSources: 1,
					maxPolicyRevisionBytes: 128,
					maxArgumentsBytes: 1_024,
					maxArgumentsDepth: 8,
					maxResultContentParts: 8,
					maxResultBytes: 4_096,
					maxResultDepth: 8,
					maxResultUpdates: 8,
				},
			}),
		).toThrow(/source limit was exceeded/u);

		let sourceIdReads = 0;
		let collectReads = 0;
		let contributionToolsReads = 0;
		let contributionContextReads = 0;
		let contributionTransformerReads = 0;
		let contributionAdmissionReads = 0;
		let contributionAdditionalAdmissionReads = 0;
		const contribution = new Proxy({} as Record<string, unknown>, {
			get(target, property, receiver) {
				if (property === "tools") contributionToolsReads += 1;
				if (property === "context") contributionContextReads += 1;
				if (property === "transformToolResult") contributionTransformerReads += 1;
				if (property === "requiredToolResultBytes") contributionAdmissionReads += 1;
				if (property === "transformToolResultTextBytes") contributionAdditionalAdmissionReads += 1;
				return Reflect.get(target, property, receiver);
			},
		});
		class AccessorSource {
			get sourceId() {
				sourceIdReads += 1;
				return "accessor";
			}

			get collect() {
				collectReads += 1;
				return () => contribution;
			}
		}
		let sourceLengthReads = 0;
		let sourceElementReads = 0;
		const proxiedSources = new Proxy([new Proxy(new AccessorSource(), {})], {
			get(target, property, receiver) {
				if (property === "length") {
					sourceLengthReads += 1;
					return sourceLengthReads === 1 ? 1 : 100;
				}
				if (property === "0") sourceElementReads += 1;
				return Reflect.get(target, property, receiver);
			},
		});
		const { agent } = createAgent(() => textResponse("unused"), undefined, {
			requestSources: proxiedSources,
		});
		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(sourceLengthReads).toBe(1);
		expect(sourceElementReads).toBe(1);
		expect(sourceIdReads).toBe(1);
		expect(collectReads).toBe(1);
		expect(contributionToolsReads).toBe(1);
		expect(contributionContextReads).toBe(1);
		expect(contributionTransformerReads).toBe(1);
		expect(contributionAdmissionReads).toBe(1);
		expect(contributionAdditionalAdmissionReads).toBe(1);
	});

	it("publishes only the candidate names selected by the exact host policy snapshot", async () => {
		const inspect = tool("inspect", async () => ({ content: [], details: {} }));
		const act = tool("act", async () => ({ content: [], details: {} }));
		const published: string[][] = [];
		const { agent } = createAgent(
			(_model, context) => {
				published.push(context.tools?.map((candidate) => candidate.name) ?? []);
				return textResponse("done");
			},
			undefined,
			{
				toolSource: () => [inspect, act],
				toolPolicy: (_input, _candidates, _signal, coordinate) => ({
					revision: `policy-${coordinate.runId}`,
					visibleToolNames: ["inspect"],
				}),
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(published).toEqual([["inspect"]]);
	});

	it("rechecks Tool visibility after durable context publication settles", async () => {
		let revision = "policy-1";
		let providerCalls = 0;
		const inspect = tool("inspect", async () => ({ content: [], details: {} }));
		const { agent } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			async (_currentAgent, _coordinate, data) => {
				if (data.type === "item.started" && data.item.itemType === "message" && "context" in data.item) {
					await Promise.resolve();
					revision = "policy-2";
				}
			},
			{
				contextSources: [
					{
						sourceId: "memory",
						load: () => ({ references: [{ referenceId: "memory-1", revision: "1", content: { fact: true } }] }),
					},
				],
				contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 },
				toolSource: () => [inspect],
				toolPolicy: (_input, candidates) => ({
					revision,
					visibleToolNames: candidates.map((candidate) => candidate.name),
				}),
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(0);
	});

	it("denies a Tool when the policy revision changes after provider publication", async () => {
		let revision = "policy-1";
		let requests = 0;
		let executed = false;
		const contexts: Context[] = [];
		const inspect = tool("inspect", async () => {
			executed = true;
			return { content: [{ type: "text", text: "must not run" }], details: {} };
		});
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				requests += 1;
				if (requests === 1) {
					revision = "policy-2";
					return messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"));
				}
				return textResponse("denied safely");
			},
			undefined,
			{
				toolSource: () => [inspect],
				toolPolicy: (_input, candidates) => ({
					revision,
					visibleToolNames: candidates.map((candidate) => candidate.name),
				}),
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(executed).toBe(false);
		const toolResult = contexts[1]?.messages.find((message) => message.role === "toolResult");
		expect(toolResult).toEqual(
			expect.objectContaining({
				role: "toolResult",
				content: [{ type: "text", text: "tool_policy_changed" }],
			}),
		);
	});

	it("rechecks the policy after an asynchronous execution guard settles", async () => {
		let revision = "policy-1";
		let requests = 0;
		let executed = false;
		const inspect = tool("inspect", async () => {
			executed = true;
			return { content: [{ type: "text", text: "must not run" }], details: {} };
		});
		const { agent } = createAgent(
			() => {
				requests += 1;
				return requests === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("denied safely");
			},
			undefined,
			{
				toolSource: () => [inspect],
				toolPolicy: (_input, candidates) => ({
					revision,
					visibleToolNames: candidates.map((candidate) => candidate.name),
				}),
				beforeToolExecution: async () => {
					await Promise.resolve();
					revision = "policy-2";
				},
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(executed).toBe(false);
	});

	it("rechecks the policy after durable execution preparation settles", async () => {
		let revision = "policy-1";
		let requests = 0;
		let executed = false;
		const inspect: DurableExecutionAgentTool = {
			name: "inspect",
			label: "inspect",
			description: "inspect",
			parameters: Type.Object({}),
			[DURABLE_TOOL_EXECUTION]: async () => {
				await Promise.resolve();
				revision = "policy-2";
				return {
					execute: async () => {
						executed = true;
						return { content: [{ type: "text", text: "must not run" }], details: {} };
					},
				};
			},
			execute: async () => {
				throw new Error("The durable preparation must own execution.");
			},
		};
		const { agent } = createAgent(
			() => {
				requests += 1;
				return requests === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("denied safely");
			},
			undefined,
			{
				toolSource: () => [inspect],
				toolPolicy: (_input, candidates) => ({
					revision,
					visibleToolNames: candidates.map((candidate) => candidate.name),
				}),
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(executed).toBe(false);
	});

	it("executes the construction-bound Tool implementation after the source object is mutated", async () => {
		let requests = 0;
		let originalExecutions = 0;
		let replacementExecutions = 0;
		const inspect = tool("inspect", async () => {
			originalExecutions += 1;
			return { content: [{ type: "text", text: "original" }], details: {} };
		});
		const { agent } = createAgent(
			(_model, context) => {
				requests += 1;
				if (requests === 1) {
					expect(Object.isFrozen(context.tools?.[0])).toBe(true);
					inspect.execute = async () => {
						replacementExecutions += 1;
						return { content: [{ type: "text", text: "replacement" }], details: {} };
					};
					return messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"));
				}
				return textResponse("done");
			},
			undefined,
			{ toolSource: () => [inspect] },
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(originalExecutions).toBe(1);
		expect(replacementExecutions).toBe(0);
	});

	it("snapshots partial and final Tool results before Pi events or provider history can retain them", async () => {
		let requests = 0;
		const contexts: Context[] = [];
		const partial = { content: [{ type: "text" as const, text: "working" }], details: { step: 1 } };
		const finalResult = { content: [{ type: "text" as const, text: "finished" }], details: { step: 2 } };
		let lateUpdate: ((result: typeof partial) => void) | undefined;
		const inspect = tool("inspect", async (_id, _parameters, _signal, onUpdate) => {
			lateUpdate = onUpdate;
			onUpdate?.(partial);
			const partialText = partial.content[0];
			if (!partialText) throw new Error("Expected partial text fixture.");
			partialText.text = "mutated partial";
			partial.details.step = 99;
			return finalResult;
		});
		const { agent, events } = createAgent(
			(_model, context) => {
				requests += 1;
				if (requests === 1) return messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"));
				const finalText = finalResult.content[0];
				if (!finalText) throw new Error("Expected final text fixture.");
				finalText.text = "mutated final";
				finalResult.details.step = 100;
				lateUpdate?.({ content: [{ type: "text", text: "too late" }], details: { step: 3 } });
				const retained = context.messages.find((message) => message.role === "toolResult");
				expect(Object.isFrozen(retained?.content)).toBe(true);
				expect(Object.isFrozen(retained?.details)).toBe(true);
				contexts.push(copyContext(context));
				return textResponse("done");
			},
			undefined,
			{ toolSource: () => [inspect] },
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(events.filter((event) => event.data.type === "item.updated")).toHaveLength(1);
		expect(events).toContainEqual(
			expect.objectContaining({
				data: expect.objectContaining({
					type: "item.updated",
					updateOrdinal: "1",
					content: [{ type: "text", text: "working" }],
				}),
			}),
		);
		const toolResult = contexts[0]?.messages.find((message) => message.role === "toolResult");
		expect(toolResult).toEqual(
			expect.objectContaining({ content: [{ type: "text", text: "finished" }], details: { step: 2 } }),
		);
	});

	it("drops excessive partial updates without poisoning a valid final Tool result", async () => {
		let requests = 0;
		const contexts: Context[] = [];
		const inspect = tool("inspect", async (_id, _parameters, _signal, onUpdate) => {
			onUpdate?.({ content: [{ type: "text", text: "first" }], details: {} });
			try {
				onUpdate?.({ content: [{ type: "text", text: "second" }], details: {} });
			} catch {
				// A Tool cannot suppress a boundary violation by catching the callback error.
			}
			return { content: [{ type: "text", text: "must not be accepted" }], details: {} };
		});
		const { agent, events } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				requests += 1;
				return requests === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("recovered");
			},
			undefined,
			{
				toolSource: () => [inspect],
				toolLimits: {
					maxTools: 1,
					maxCatalogBytes: 4_096,
					maxSchemaDepth: 8,
					maxRequestSources: 1,
					maxPolicyRevisionBytes: 128,
					maxArgumentsBytes: 1_024,
					maxArgumentsDepth: 8,
					maxResultContentParts: 8,
					maxResultBytes: 4_096,
					maxResultDepth: 8,
					maxResultUpdates: 1,
				},
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		const result = contexts[1]?.messages.find((message) => message.role === "toolResult");
		expect(result).toEqual(
			expect.objectContaining({
				isError: false,
				content: [{ type: "text", text: "must not be accepted" }],
			}),
		);
		expect(events.filter((event) => event.data.type === "item.updated")).toHaveLength(1);
	});

	it("drops malformed partial updates without poisoning a valid final Tool result", async () => {
		let requests = 0;
		const contexts: Context[] = [];
		const inspect = tool("inspect", async (_id, _parameters, _signal, onUpdate) => {
			onUpdate?.({ content: [{ type: "unknown" }], details: {} } as never);
			return { content: [{ type: "text", text: "valid final" }], details: {} };
		});
		const { agent, events } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				requests += 1;
				return requests === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("done");
			},
			undefined,
			{ toolSource: () => [inspect] },
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts[1]?.messages.find((message) => message.role === "toolResult")).toEqual(
			expect.objectContaining({
				isError: false,
				content: [{ type: "text", text: "valid final" }],
			}),
		);
		expect(events.filter((event) => event.data.type === "item.updated")).toHaveLength(0);
	});

	it("still fails a malformed final Tool result", async () => {
		let requests = 0;
		const contexts: Context[] = [];
		const inspect = tool("inspect", async () => ({ content: [{ type: "unknown" }], details: {} }) as never);
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				requests += 1;
				return requests === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("done");
			},
			undefined,
			{ toolSource: () => [inspect] },
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts[1]?.messages.find((message) => message.role === "toolResult")).toEqual(
			expect.objectContaining({
				isError: true,
				content: [{ type: "text", text: "tool_result_invalid" }],
			}),
		);
	});

	it("preserves a useful bounded Tool exception for Pi self-correction", async () => {
		let requests = 0;
		const contexts: Context[] = [];
		const inspect = tool("inspect", async () => {
			throw new Error("provider-token-must-not-enter-history");
		});
		const { agent, events } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				requests += 1;
				return requests === 1
					? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse"))
					: textResponse("recovered");
			},
			undefined,
			{ toolSource: () => [inspect] },
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		const result = contexts[1]?.messages.find((message) => message.role === "toolResult");
		expect(result).toEqual(
			expect.objectContaining({
				isError: true,
				content: [{ type: "text", text: "provider-token-must-not-enter-history" }],
			}),
		);
		expect(JSON.stringify({ contexts, events })).toContain("provider-token-must-not-enter-history");
	});

	it("never re-reads earlier Tool callbacks while later request sources extend the catalog", async () => {
		let requests = 0;
		let baseOriginal = 0;
		let dynamicOriginal = 0;
		let replacements = 0;
		const inspect = tool("inspect", async () => {
			baseOriginal += 1;
			return { content: [{ type: "text", text: "base-original" }], details: {} };
		});
		const skill = tool("skill", async () => {
			dynamicOriginal += 1;
			return { content: [{ type: "text", text: "dynamic-original" }], details: {} };
		});
		const { agent } = createAgent(
			() => {
				requests += 1;
				return requests === 1
					? messageResponse(
							assistant([toolCall("call-inspect", "inspect"), toolCall("call-skill", "skill")], "toolUse"),
						)
					: textResponse("done");
			},
			undefined,
			{
				toolSource: () => [inspect],
				requestSources: [
					{ sourceId: "first", collect: () => ({ tools: [skill] }) },
					{
						sourceId: "second",
						collect: () => {
							inspect.execute = async () => {
								replacements += 1;
								return { content: [{ type: "text", text: "base-replacement" }], details: {} };
							};
							skill.execute = async () => {
								replacements += 1;
								return { content: [{ type: "text", text: "dynamic-replacement" }], details: {} };
							};
							return {};
						},
					},
				],
				contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 },
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect({ baseOriginal, dynamicOriginal, replacements }).toEqual({
			baseOriginal: 1,
			dynamicOriginal: 1,
			replacements: 0,
		});
	});

	it("rejects oversized validated Tool arguments before policy or implementation dispatch", async () => {
		let requests = 0;
		let policyChecks = 0;
		let executed = false;
		const contexts: Context[] = [];
		let argumentPreparerCalls = 0;
		const inspect = {
			name: "inspect",
			label: "inspect",
			description: "inspect tool",
			parameters: Type.Object({ value: Type.String() }),
			prepareArguments: (parameters: unknown) => {
				argumentPreparerCalls += 1;
				return parameters as { value: string };
			},
			execute: async () => {
				executed = true;
				return { content: [{ type: "text" as const, text: "must not run" }], details: {} };
			},
		};
		const { agent } = createAgent(
			(_model, context) => {
				contexts.push(copyContext(context));
				requests += 1;
				return requests === 1
					? messageResponse(
							assistant(
								[
									{
										type: "toolCall",
										id: "call-inspect",
										name: "inspect",
										arguments: { value: "too large" },
									},
								],
								"toolUse",
							),
						)
					: textResponse("recovered");
			},
			undefined,
			{
				toolSource: () => [inspect],
				toolLimits: {
					maxTools: 1,
					maxCatalogBytes: 4_096,
					maxSchemaDepth: 8,
					maxRequestSources: 1,
					maxPolicyRevisionBytes: 128,
					maxArgumentsBytes: 1,
					maxArgumentsDepth: 8,
					maxResultContentParts: 8,
					maxResultBytes: 4_096,
					maxResultDepth: 8,
					maxResultUpdates: 8,
				},
				toolPolicy: (_input, candidates) => {
					policyChecks += 1;
					return { revision: "policy", visibleToolNames: candidates.map((candidate) => candidate.name) };
				},
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(executed).toBe(false);
		expect(argumentPreparerCalls).toBe(0);
		expect(policyChecks).toBeGreaterThan(0);
		const toolResult = contexts[1]?.messages.find((message) => message.role === "toolResult");
		expect(toolResult).toEqual(
			expect.objectContaining({
				content: [{ type: "text", text: "tool_arguments_invalid" }],
			}),
		);
	});

	it("preserves dangerous Tool argument keys without granting inherited properties", async () => {
		let requests = 0;
		let prepared = false;
		let executed = false;
		let guardCalls = 0;
		const dangerous = JSON.parse('{"__proto__":{"admin":true},"constructor":"ctor","prototype":"proto"}') as Record<
			string,
			unknown
		>;
		const inspect = {
			name: "inspect",
			label: "inspect",
			description: "inspect tool",
			parameters: Type.Object({}, { additionalProperties: false }),
			prepareArguments: (parameters: unknown) => {
				const value = parameters as Record<string, unknown>;
				expect(Object.getPrototypeOf(value)).toBe(Object.prototype);
				expect(Object.hasOwn(value, "__proto__")).toBe(true);
				expect((value as { admin?: unknown }).admin).toBeUndefined();
				prepared = true;
				return value;
			},
			execute: async (_toolCallId: string, parameters: unknown) => {
				executed = true;
				return {
					content: [{ type: "text" as const, text: String(Object.keys(parameters as Record<string, unknown>)) }],
					details: {},
				};
			},
		};
		const { agent } = createAgent(
			() => {
				requests += 1;
				return requests === 1
					? messageResponse(
							assistant(
								[{ type: "toolCall", id: "call-inspect", name: "inspect", arguments: dangerous as never }],
								"toolUse",
							),
						)
					: textResponse("done");
			},
			undefined,
			{
				toolSource: () => [inspect],
				beforeToolExecution: () => {
					guardCalls += 1;
				},
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect({ prepared, executed, guardCalls }).toEqual({ prepared: true, executed: false, guardCalls: 0 });
	});

	it("propagates abort through context loading without invoking the provider", async () => {
		let markStarted: (() => void) | undefined;
		const started = new Promise<void>((resolve) => {
			markStarted = resolve;
		});
		let providerCalls = 0;
		const { agent } = createAgent(
			() => {
				providerCalls += 1;
				return textResponse("must not run");
			},
			undefined,
			{
				contextSources: [
					{
						sourceId: "memory",
						load: async (_input, signal) => {
							markStarted?.();
							await new Promise<void>((_resolve, reject) => {
								const rejectAbort = () => reject(signal.reason instanceof Error ? signal.reason : new Error("aborted"));
								if (signal.aborted) rejectAbort();
								else signal.addEventListener("abort", rejectAbort, { once: true });
							});
							return { references: [] };
						},
					},
				],
				contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 },
			},
		);
		const run = agent.start(INPUT, "run-1");
		await started;
		const active = agent.readActiveCoordinate();
		if (!active) throw new Error("Expected an active coordinate.");

		await expect(agent.control(active, { type: "abort" })).resolves.toMatchObject({ accepted: true });
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });
		expect(providerCalls).toBe(0);
	});

	it("allows an async event observer to await control without reentrant deadlock", async () => {
		const { agent } = createAgent(() => textResponse("done"));
		let resolveObserver: (() => void) | undefined;
		const observerDone = new Promise<void>((resolve) => {
			resolveObserver = resolve;
		});
		let result: GameControlResult | undefined;
		agent.subscribe(async (event) => {
			if (event.data.type !== "run.completed") return;
			result = await agent.control(event.coordinate, { type: "abort" });
			resolveObserver?.();
		});

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		await observerDone;
		expect(result).toMatchObject({ accepted: false });
	});

	it("treats duplicate subscriptions as one Pi-style set entry", async () => {
		const { agent } = createAgent(() => textResponse("done"));
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
		const unsubscribeFirst = agent.subscribe(listener);
		const unsubscribeSecond = agent.subscribe(listener);

		await expect(agent.start(INPUT, "run-subscription-1").completion).resolves.toMatchObject({ status: "completed" });
		await firstObserved;
		expect(completedCalls).toBe(1);

		unsubscribeSecond();
		await expect(
			agent.start({ ...INPUT, inputId: "input-subscription-2" }, "run-subscription-2").completion,
		).resolves.toMatchObject({ status: "completed" });
		await new Promise<void>((resolve) => setImmediate(resolve));
		expect(completedCalls).toBe(1);
		unsubscribeFirst();
	});

	it("continues control accepted before Pi agent_end inside the same game run", async () => {
		const contexts: Context[] = [];
		let requests = 0;
		let control: Promise<GameControlResult> | undefined;
		const modelStream: StreamFn = (_model, context) => {
			contexts.push(copyContext(context));
			requests += 1;
			return textResponse(`reply-${requests}`);
		};
		const { agent } = createAgent(modelStream, (currentAgent, coordinate, data) => {
			if (data.type === "turn.completed" && requests === 1 && !control) {
				control = currentAgent.control(coordinate, { type: "steer", text: "before terminal" });
			}
		});

		await expect(agent.start(INPUT, "run-1").completion).resolves.toEqual({
			runId: "run-1",
			status: "completed",
			finalOutput: "reply-2",
		});
		if (!control) throw new Error("Expected the hook to enqueue control.");
		await expect(control).resolves.toMatchObject({ accepted: true });
		expect(contexts).toHaveLength(2);
		expect(contexts[1]?.messages).toContainEqual(expect.objectContaining({ role: "user", content: "before terminal" }));
	});

	it("rejects control after Pi agent_end and never leaks it into the next run", async () => {
		const contexts: Context[] = [];
		let lateControl: Promise<GameControlResult> | undefined;
		const modelStream: StreamFn = (_model, context) => {
			contexts.push(copyContext(context));
			return textResponse("done");
		};
		const { agent } = createAgent(modelStream, (currentAgent, coordinate, data) => {
			if (data.type === "run.completed" && coordinate.runId === "run-1") {
				lateControl = currentAgent.control(coordinate, { type: "follow-up", text: "too late" });
			}
		});

		await expect(agent.start({ ...INPUT, inputId: "input-1" }, "run-1").completion).resolves.toMatchObject({
			status: "completed",
		});
		if (!lateControl) throw new Error("Expected the hook to enqueue late control.");
		await expect(lateControl).resolves.toMatchObject({ accepted: false, reason: "run-not-active" });
		await expect(agent.start({ ...INPUT, inputId: "input-2" }, "run-2").completion).resolves.toMatchObject({
			status: "completed",
		});

		expect(contexts).toHaveLength(2);
		expect(contexts[1]?.messages).not.toContainEqual(expect.objectContaining({ role: "user", content: "too late" }));
	});

	it("clears queued steering after abort before the next run", async () => {
		const first = controlledResponse();
		const contexts: Context[] = [];
		let requests = 0;
		const modelStream: StreamFn = (model, context, options) => {
			contexts.push(copyContext(context));
			requests += 1;
			return requests === 1 ? first.streamFn(model, context, options) : textResponse("next run");
		};
		const { agent } = createAgent(modelStream);
		const firstRun = agent.start({ ...INPUT, inputId: "input-1" }, "run-1");
		await first.started;
		const active = agent.readActiveCoordinate();
		if (!active) throw new Error("Expected an active coordinate.");

		await expect(agent.control(active, { type: "steer", text: "stale steering" })).resolves.toMatchObject({
			accepted: true,
		});
		await expect(agent.control(active, { type: "abort" })).resolves.toMatchObject({ accepted: true });
		await expect(firstRun.completion).resolves.toMatchObject({ status: "aborted" });
		await expect(agent.start({ ...INPUT, inputId: "input-2" }, "run-2").completion).resolves.toMatchObject({
			status: "completed",
		});

		expect(contexts).toHaveLength(2);
		expect(contexts[1]?.messages).not.toContainEqual(
			expect.objectContaining({ role: "user", content: "stale steering" }),
		);
	});

	it("clears queued steering after provider failure before the next run", async () => {
		const failingStream = createAssistantMessageEventStream();
		let markStarted: (() => void) | undefined;
		const started = new Promise<void>((resolve) => {
			markStarted = resolve;
		});
		const contexts: Context[] = [];
		let requests = 0;
		const modelStream: StreamFn = (_model, context) => {
			contexts.push(copyContext(context));
			requests += 1;
			if (requests > 1) return textResponse("next run");
			failingStream.push({ type: "start", partial: assistant([], "pending") });
			markStarted?.();
			return failingStream;
		};
		const { agent } = createAgent(modelStream);
		const firstRun = agent.start({ ...INPUT, inputId: "input-1" }, "run-1");
		await started;
		const active = agent.readActiveCoordinate();
		if (!active) throw new Error("Expected an active coordinate.");

		await expect(agent.control(active, { type: "steer", text: "stale after failure" })).resolves.toMatchObject({
			accepted: true,
		});
		const error = { ...assistant([], "error"), errorMessage: "provider failed" };
		failingStream.push({ type: "error", reason: "error", error });
		await expect(firstRun.completion).resolves.toMatchObject({ status: "failed" });
		await expect(agent.start({ ...INPUT, inputId: "input-2" }, "run-2").completion).resolves.toMatchObject({
			status: "completed",
		});

		expect(contexts).toHaveLength(2);
		expect(contexts[1]?.messages).not.toContainEqual(
			expect.objectContaining({ role: "user", content: "stale after failure" }),
		);
	});

	it("uses Pi's turn-settlement hook to stop before another provider call", async () => {
		let requests = 0;
		const inspect = tool("inspect", async () => ({ content: [{ type: "text", text: "done" }], details: {} }));
		const { agent } = createAgent(
			() => {
				requests += 1;
				if (requests > 1) throw new Error("A second provider call must not start.");
				const turn = assistant([toolCall("call-inspect", "inspect")], "toolUse");
				return messageResponse({ ...turn, usage: { ...turn.usage, totalTokens: 1 } });
			},
			undefined,
			{
				maxReportedTokensPerRun: 100,
				toolSource: () => [inspect],
				shouldStopAfterTurn: (_input, coordinate) => coordinate.turn === "1",
			},
		);

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(requests).toBe(1);
	});

	it("stops before another provider call when reported run tokens reach the host limit", async () => {
		let requests = 0;
		let toolExecutions = 0;
		const inspect = tool("inspect", async () => {
			toolExecutions += 1;
			return { content: [{ type: "text", text: "done" }], details: {} };
		});
		const toolTurn = assistant([toolCall("call-inspect", "inspect")], "toolUse");
		const { agent, events } = createAgent(
			() => {
				requests += 1;
				if (requests > 1) throw new Error("A second provider call must not start.");
				return messageResponse({ ...toolTurn, usage: { ...toolTurn.usage, totalTokens: 7 } });
			},
			undefined,
			{
				maxReportedTokensPerRun: 7,
				toolSource: () => [inspect],
			},
		);

		await expect(agent.start(INPUT, "token-bound-run").completion).resolves.toMatchObject({ status: "completed" });
		expect(requests).toBe(1);
		expect(toolExecutions).toBe(1);
		expect(events.at(-1)?.data).toEqual({ type: "run.completed", status: "completed" });
	});

	it("does not reopen queued control after reported-token admission closes", async () => {
		let requests = 0;
		let control: Promise<GameControlResult> | undefined;
		const completed = assistant([{ type: "text", text: "done" }]);
		const { agent } = createAgent(
			() => {
				requests += 1;
				if (requests > 1) throw new Error("A second provider call must not start.");
				return messageResponse({ ...completed, usage: { ...completed.usage, totalTokens: 7 } });
			},
			(currentAgent, coordinate, data) => {
				if (data.type === "turn.completed" && !control) {
					control = currentAgent.control(coordinate, { type: "steer", text: "queued before agent end" });
				}
			},
			{ maxReportedTokensPerRun: 7 },
		);

		await expect(agent.start(INPUT, "token-control-run").completion).resolves.toMatchObject({ status: "completed" });
		if (!control) throw new Error("Expected queued control.");
		await expect(control).resolves.toMatchObject({ accepted: true });
		expect(requests).toBe(1);
	});

	it("accumulates reported tokens across Tool turns and stops after crossing the limit", async () => {
		let requests = 0;
		let toolExecutions = 0;
		const inspect = tool("inspect", async () => {
			toolExecutions += 1;
			return { content: [{ type: "text", text: "done" }], details: {} };
		});
		const { agent } = createAgent(
			() => {
				requests += 1;
				if (requests > 2) throw new Error("A third provider call must not start.");
				const turn = assistant([toolCall(`call-${requests}`, "inspect")], "toolUse");
				return messageResponse({
					...turn,
					usage: { ...turn.usage, totalTokens: requests === 1 ? 4 : 7 },
				});
			},
			undefined,
			{
				maxReportedTokensPerRun: 10,
				toolSource: () => [inspect],
			},
		);

		await expect(agent.start(INPUT, "cumulative-token-run").completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(requests).toBe(2);
		expect(toolExecutions).toBe(2);
	});

	it("counts one assistant usage observation once when its Tool batch contains multiple calls", async () => {
		let requests = 0;
		let toolExecutions = 0;
		const inspect = tool("inspect", async () => {
			toolExecutions += 1;
			return { content: [{ type: "text", text: "done" }], details: {} };
		});
		const { agent } = createAgent(
			() => {
				requests += 1;
				if (requests > 2) throw new Error("A third provider call must not start.");
				const calls =
					requests === 1
						? [toolCall("call-1", "inspect"), toolCall("call-2", "inspect")]
						: [toolCall("call-3", "inspect")];
				const turn = assistant(calls, "toolUse");
				return messageResponse({
					...turn,
					usage: { ...turn.usage, totalTokens: requests === 1 ? 6 : 5 },
				});
			},
			undefined,
			{
				maxReportedTokensPerRun: 10,
				toolSource: () => [inspect],
			},
		);

		await expect(agent.start(INPUT, "batched-tool-token-run").completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(requests).toBe(2);
		expect(toolExecutions).toBe(3);
	});

	it("isolates reported-token admission between exact game runs", async () => {
		let requests = 0;
		const { agent } = createAgent(
			() => {
				requests += 1;
				const answer = assistant([{ type: "text", text: `answer-${requests}` }]);
				return messageResponse({ ...answer, usage: { ...answer.usage, totalTokens: 5 } });
			},
			undefined,
			{ maxReportedTokensPerRun: 5 },
		);

		await expect(agent.start({ ...INPUT, inputId: "first-input" }, "first-token-run").completion).resolves.toEqual({
			runId: "first-token-run",
			status: "completed",
			finalOutput: "answer-1",
		});
		await expect(agent.start({ ...INPUT, inputId: "second-input" }, "second-token-run").completion).resolves.toEqual({
			runId: "second-token-run",
			status: "completed",
			finalOutput: "answer-2",
		});
		expect(requests).toBe(2);
	});

	it.each([
		["missing", undefined],
		["zero or unreported", 0],
		["not finite", Number.NaN],
		["negative", -1],
		["fractional", 1.5],
		["unsafe", Number.MAX_SAFE_INTEGER + 1],
	] as const)("fails closed when completed assistant usage is %s", async (_case, totalTokens) => {
		let requests = 0;
		const turn = assistant([toolCall("call-inspect", "inspect")], "toolUse");
		const malformedUsage = totalTokens === undefined ? undefined : { ...turn.usage, totalTokens };
		const { agent, events } = createAgent(
			() => {
				requests += 1;
				if (requests > 1) throw new Error("A second provider call must not start.");
				return messageResponse({ ...turn, usage: malformedUsage as never });
			},
			undefined,
			{
				maxReportedTokensPerRun: 10,
				toolSource: () => [tool("inspect", async () => ({ content: [], details: {} }))],
			},
		);

		await expect(agent.start(INPUT, `invalid-usage-${_case}`).completion).resolves.toMatchObject({ status: "failed" });
		expect(requests).toBe(1);
		expect(events.at(-1)?.data).toEqual({ type: "run.completed", status: "failed" });
	});

	it("fails closed when cumulative reported usage cannot remain a safe integer", async () => {
		let requests = 0;
		const { agent } = createAgent(
			() => {
				requests += 1;
				if (requests > 2) throw new Error("A third provider call must not start.");
				const turn = assistant([toolCall(`call-${requests}`, "inspect")], "toolUse");
				return messageResponse({
					...turn,
					usage: { ...turn.usage, totalTokens: requests === 1 ? Number.MAX_SAFE_INTEGER - 1 : 2 },
				});
			},
			undefined,
			{
				maxReportedTokensPerRun: Number.MAX_SAFE_INTEGER,
				toolSource: () => [tool("inspect", async () => ({ content: [], details: {} }))],
			},
		);

		await expect(agent.start(INPUT, "unsafe-cumulative-usage").completion).resolves.toMatchObject({ status: "failed" });
		expect(requests).toBe(2);
	});

	it("preserves cancellation instead of treating synthetic aborted usage as malformed", async () => {
		const response = controlledResponse();
		const { agent, events } = createAgent(response.streamFn, undefined, { maxReportedTokensPerRun: 1 });
		const run = agent.start(INPUT, "cancelled-token-run");
		await response.started;
		const active = agent.readActiveCoordinate();
		if (!active) throw new Error("Expected an active coordinate.");

		await expect(agent.control(active, { type: "abort" })).resolves.toMatchObject({ accepted: true });
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });
		expect(events.at(-1)?.data).toEqual({ type: "run.completed", status: "aborted" });
	});

	it.each([0, -1, 1.5, Number.NaN, Number.MAX_SAFE_INTEGER + 1])(
		"rejects invalid reported-token run limit %s",
		(maxReportedTokensPerRun) => {
			expect(() => createAgent(() => textResponse("done"), undefined, { maxReportedTokensPerRun })).toThrow(
				"positive safe integer",
			);
		},
	);

	it("fails closed when the host turn-stop policy rejects", async () => {
		const { agent } = createAgent(() => textResponse("done"), undefined, {
			shouldStopAfterTurn: () => {
				throw new Error("policy unavailable");
			},
		});

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "failed" });
	});
});

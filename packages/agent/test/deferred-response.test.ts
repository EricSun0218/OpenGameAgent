import type { StreamFn } from "@earendil-works/pi-agent-core";
import {
	type AssistantMessage,
	createModels,
	type DeferredHandle,
	fauxAssistantMessage,
	fauxProvider,
	type Models,
} from "@earendil-works/pi-ai";
import { type CanonicalEvent, eventAudience, PROTOCOL_VERSION } from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import { allowAllTestTools, TEST_TOOL_LIMITS, tool, toolCall } from "../../devtools/src/pi-fakes.js";
import {
	createPiModelsDeferredResponseAdapter,
	DurableTranscript,
	GameAgent,
	GameAgentBusyError,
	GameAgentDeferredPendingError,
	type GameAgentOptions,
	type GameDeferredResponseAdapter,
	InMemoryTranscriptStore,
	type TranscriptStore,
} from "../src/index.js";

const STREAM = { sessionId: "session", actorId: "actor" } as const;
const INPUT = { ...STREAM, inputId: "input-1", payload: { scene: "square" } } as const;

function transcript(store: TranscriptStore = new InMemoryTranscriptStore()): DurableTranscript {
	let recordId = 0;
	return new DurableTranscript(store, {
		maxRecordBytes: 16_384,
		maxCommitBytes: 65_536,
		maxRestoreBytes: 131_072,
		maxJsonDepth: 16,
		newRecordId: () => `record-${++recordId}`,
	});
}

async function agent(
	modelStream: StreamFn,
	deferredResponses: GameDeferredResponseAdapter | undefined,
	durable = transcript(),
	model: GameAgentOptions["model"] = {
		id: "model",
		name: "Model",
		api: "test-api",
		provider: "provider",
		baseUrl: "https://example.invalid",
		reasoning: false,
		input: ["text"],
		cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
		contextWindow: 1,
		maxTokens: 1,
	},
	overrides: Partial<
		Pick<
			GameAgentOptions,
			| "beforeToolExecution"
			| "contextLimits"
			| "contextSources"
			| "maxReportedTokensPerRun"
			| "requestSources"
			| "toolLimits"
			| "toolPolicy"
			| "toolSource"
		>
	> = {},
): Promise<{ agent: GameAgent; transcript: DurableTranscript; events: CanonicalEvent[] }> {
	const restore = await durable.restore(STREAM);
	const events: CanonicalEvent[] = [];
	let eventId = 0;
	const gameAgent = new GameAgent({
		...STREAM,
		model,
		modelStream,
		transcript: { transcript: durable, restore },
		...(deferredResponses ? { deferredResponses } : {}),
		...overrides,
		emitEvent: async (coordinate, data) => {
			const revision = String(++eventId);
			const event: CanonicalEvent = {
				protocolVersion: PROTOCOL_VERSION,
				eventId: `event-${revision}`,
				sequence: revision,
				revision,
				occurredAt: "2026-08-30T00:00:00.000Z",
				coordinate: structuredClone(coordinate),
				audience: eventAudience(data),
				data: structuredClone(data),
			};
			events.push(event);
			return event;
		},
		newId: () => `id-${eventId + 1}`,
		now: () => 1,
	});
	return { agent: gameAgent, transcript: durable, events };
}

function deferredMessage(handle: DeferredHandle): AssistantMessage {
	return {
		role: "assistant",
		content: [],
		api: handle.api,
		provider: handle.provider,
		model: handle.modelId,
		usage: {
			input: 0,
			output: 0,
			cacheRead: 0,
			cacheWrite: 0,
			totalTokens: 0,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		},
		stopReason: "deferred",
		deferred: handle,
		timestamp: 1,
	};
}

describe("durable deferred provider responses", () => {
	it("fails instead of dropping a deferred handle when no durable transcript is configured", async () => {
		const handle = { provider: "provider", modelId: "model", api: "test-api", id: "operation" };
		const events: CanonicalEvent[] = [];
		let eventId = 0;
		const gameAgent = new GameAgent({
			...STREAM,
			model: {
				id: "model",
				name: "Model",
				api: "test-api",
				provider: "provider",
				baseUrl: "https://example.invalid",
				reasoning: false,
				input: ["text"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
				contextWindow: 1,
				maxTokens: 1,
			},
			modelStream: () => createDeferredStream(deferredMessage(handle)),
			emitEvent: async (coordinate, data) => {
				const revision = String(++eventId);
				const event: CanonicalEvent = {
					protocolVersion: PROTOCOL_VERSION,
					eventId: `event-${revision}`,
					sequence: revision,
					revision,
					occurredAt: "2026-08-30T00:00:00.000Z",
					coordinate: structuredClone(coordinate),
					audience: eventAudience(data),
					data: structuredClone(data),
				};
				events.push(event);
				return event;
			},
			newId: () => `id-${eventId + 1}`,
			now: () => 1,
		});

		await expect(gameAgent.start(INPUT, "run-1").completion).resolves.toEqual({
			runId: "run-1",
			status: "failed",
		});
		expect(gameAgent.readPendingDeferred()).toBeUndefined();
		expect(events.map((event) => event.data)).toContainEqual({ type: "run.completed", status: "failed" });
		expect(events.some((event) => event.data.type === "run.suspended")).toBe(false);
	});

	it("adapts Pi Models with exact handle model lookup and one immediate fetch", async () => {
		const handle = { provider: "provider", modelId: "model", api: "test-api", id: "operation" };
		const model = {
			id: "model",
			name: "Model",
			api: "test-api",
			provider: "provider",
			baseUrl: "https://example.invalid",
			reasoning: false,
			input: ["text"],
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
			contextWindow: 1,
			maxTokens: 1,
		} as const;
		const final = { ...deferredMessage(handle), stopReason: "stop" as const };
		const fetchDeferred = vi.fn(async () => final);
		const cancelDeferred = vi.fn(async () => undefined);
		const models = {
			getModel: vi.fn(() => model),
			fetchDeferred,
			cancelDeferred,
		} as unknown as Models;
		const adapter = createPiModelsDeferredResponseAdapter(models);
		const signal = new AbortController().signal;

		await expect(adapter.fetch(handle, signal)).resolves.toBe(final);
		expect(models.getModel).toHaveBeenCalledWith("provider", "model");
		expect(fetchDeferred).toHaveBeenCalledWith(model, handle, { signal, wait: 0 });
		await expect(adapter.cancel(handle, signal)).resolves.toBeUndefined();
		expect(cancelDeferred).toHaveBeenCalledWith(model, handle, { signal });
	});

	it("persists an initial handle and resumes through Pi exactly once per explicit call", async () => {
		const faux = fauxProvider({ api: "test-api", provider: "provider", deferred: { pendingFetches: 1 } });
		const models = createModels();
		models.setProvider(faux.provider);
		faux.setResponses([fauxAssistantMessage("ready")]);
		let ordinaryCalls = 0;
		const modelStream: StreamFn = (model, context, options) => {
			ordinaryCalls += 1;
			return models.streamSimple(model, context, { ...options, deferred: true });
		};
		const created = await agent(
			modelStream,
			createPiModelsDeferredResponseAdapter(models),
			transcript(),
			faux.getModel(),
		);

		await expect(created.agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "deferred" });
		const pending = created.agent.readPendingDeferred();
		expect(pending).toMatchObject({
			coordinate: { ...STREAM, runId: "run-1", turn: "1" },
			input: { inputId: "input-1", payload: { scene: "square" } },
			handle: { provider: "provider", modelId: faux.getModel().id, api: "test-api" },
		});
		expect(() => created.agent.start({ ...INPUT, inputId: "input-2" }, "run-2")).toThrow(GameAgentDeferredPendingError);

		const firstResume = created.agent.resumeDeferred(pending?.operationId ?? "missing");
		expect(created.agent.readActiveCoordinate()).toEqual(firstResume.initialCoordinate);
		await expect(firstResume.completion).resolves.toMatchObject({
			status: "deferred",
		});
		expect(faux.state.deferredFetchCount).toBe(1);
		expect(ordinaryCalls).toBe(1);

		await expect(created.agent.resumeDeferred(pending?.operationId ?? "missing").completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(faux.state.deferredFetchCount).toBe(2);
		expect(ordinaryCalls).toBe(1);
		expect(created.agent.readPendingDeferred()).toBeUndefined();
		await expect(created.transcript.restore(STREAM)).resolves.toMatchObject({
			messages: [
				{ role: "user", content: '{"scene":"square"}' },
				{ role: "assistant", content: [{ type: "text", text: "ready" }] },
			],
			terminalDeferredRuns: [
				{
					coordinate: pending?.coordinate,
					inputId: "input-1",
					operationId: pending?.operationId,
					status: "completed",
				},
			],
		});
		expect(created.events.filter((event) => event.data.type === "run.suspended")).toHaveLength(2);
		expect(created.events.filter((event) => event.data.type === "run.resumed")).toEqual([
			expect.objectContaining({ coordinate: pending?.coordinate }),
			expect.objectContaining({ coordinate: pending?.coordinate }),
		]);
		expect(created.events.at(-1)?.data).toEqual({ type: "run.completed", status: "completed" });
		const metrics = created.events.flatMap((event) => (event.data.type === "run.metrics" ? [event.data] : []));
		expect(metrics.map((data) => data.segmentStatus)).toEqual(["deferred", "deferred", "completed"]);
		expect(metrics.map((data) => data.counts)).toEqual([
			{ providerAttempts: "1", retries: "0", fallbacks: "0", toolCalls: "0" },
			{ providerAttempts: "1", retries: "0", fallbacks: "0", toolCalls: "0" },
			{ providerAttempts: "1", retries: "0", fallbacks: "0", toolCalls: "0" },
		]);
	});

	it.each([
		["the live Agent", false],
		["a restored Agent", true],
	] as const)(
		"preserves the exact run's last assistant output when deferred work resumes through %s",
		async (_case, restart) => {
			const handle = { provider: "provider", modelId: "model", api: "test-api", id: "operation" };
			const completed = (
				content: AssistantMessage["content"],
				stopReason: Extract<AssistantMessage["stopReason"], "stop" | "toolUse">,
			): AssistantMessage => {
				const { deferred: _deferred, ...base } = deferredMessage(handle);
				return { ...base, content, stopReason, providerThinkingLevel: "private-deferred-effort" };
			};
			const durable = transcript();
			const deferredResponses: GameDeferredResponseAdapter = {
				fetch: async () => completed([], "stop"),
				cancel: async () => undefined,
			};
			const overrides = {
				toolSource: () => [
					tool("inspect", async () => ({ content: [{ type: "text" as const, text: "inspected" }], details: {} })),
				],
				toolPolicy: allowAllTestTools,
				toolLimits: TEST_TOOL_LIMITS,
			} satisfies Partial<GameAgentOptions>;
			let providerCalls = 0;
			const created = await agent(
				() => {
					providerCalls += 1;
					return providerCalls === 1
						? createDeferredStream(
								completed([{ type: "text", text: "answer-before-defer" }, toolCall("call", "inspect")], "toolUse"),
							)
						: createDeferredStream({
								...deferredMessage(handle),
								content: [{ type: "text", text: "answer-at-defer" }],
							});
				},
				deferredResponses,
				durable,
				undefined,
				overrides,
			);

			await expect(created.agent.start(INPUT, "deferred-output-run").completion).resolves.toMatchObject({
				status: "deferred",
			});
			const pending = created.agent.readPendingDeferred();
			if (!pending) throw new Error("Expected a pending deferred operation.");
			const resumed = restart
				? (
						await agent(
							() => {
								throw new Error("A completed deferred response must not start another provider request.");
							},
							deferredResponses,
							durable,
							undefined,
							overrides,
						)
					).agent
				: created.agent;

			await expect(resumed.resumeDeferred(pending.operationId).completion).resolves.toEqual({
				runId: "deferred-output-run",
				status: "completed",
				finalOutput: "answer-at-defer",
			});
			const restored = await durable.restore(STREAM);
			expect(restored.messages.at(-1)).toMatchObject({
				role: "assistant",
				content: [],
				providerThinkingLevel: "private-deferred-effort",
			});
			expect(JSON.stringify(created.events)).not.toContain("private-deferred-effort");
		},
	);

	it("preserves cumulative reported-token admission across deferred suspension and resume", async () => {
		const handle = { provider: "provider", modelId: "model", api: "test-api", id: "operation" };
		const completed = (
			content: AssistantMessage["content"],
			stopReason: Extract<AssistantMessage["stopReason"], "stop" | "toolUse">,
			totalTokens: number,
		): AssistantMessage => {
			const { deferred: _deferred, ...base } = deferredMessage(handle);
			return { ...base, content, stopReason, usage: { ...base.usage, totalTokens } };
		};
		let providerCalls = 0;
		let toolExecutions = 0;
		let fetches = 0;
		const durable = transcript();
		const deferredResponses: GameDeferredResponseAdapter = {
			fetch: async () => {
				fetches += 1;
				return completed([toolCall("call-after", "inspect")], "toolUse", 7);
			},
			cancel: async () => undefined,
		};
		const overrides = {
			maxReportedTokensPerRun: 10,
			toolSource: () => [
				tool("inspect", async () => {
					toolExecutions += 1;
					return { content: [{ type: "text" as const, text: "inspected" }], details: {} };
				}),
			],
			toolPolicy: allowAllTestTools,
			toolLimits: TEST_TOOL_LIMITS,
		} satisfies Partial<GameAgentOptions>;
		const created = await agent(
			() => {
				providerCalls += 1;
				if (providerCalls === 1) {
					return createDeferredStream(completed([toolCall("call-before", "inspect")], "toolUse", 4));
				}
				if (providerCalls === 2) return createDeferredStream(deferredMessage(handle));
				throw new Error("A provider call after resumed admission closes must not start.");
			},
			deferredResponses,
			durable,
			undefined,
			overrides,
		);

		await expect(created.agent.start(INPUT, "deferred-token-run").completion).resolves.toMatchObject({
			status: "deferred",
		});
		const pending = created.agent.readPendingDeferred();
		if (!pending) throw new Error("Expected a pending deferred operation.");
		expect(pending.reportedTokensBeforeDeferred).toBe(4);
		const exhausted = await agent(
			() => {
				throw new Error("A provider call for an exhausted deferred run must not start.");
			},
			deferredResponses,
			durable,
			undefined,
			{ ...overrides, maxReportedTokensPerRun: 4 },
		);
		expect(() => exhausted.agent.resumeDeferred(pending.operationId)).toThrow(
			"already exhausted its reported-token admission limit",
		);
		expect(fetches).toBe(0);
		const restarted = await agent(
			() => {
				providerCalls += 1;
				throw new Error("A provider call after resumed admission closes must not start.");
			},
			deferredResponses,
			durable,
			undefined,
			overrides,
		);

		await expect(restarted.agent.resumeDeferred(pending.operationId).completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(providerCalls).toBe(2);
		expect(toolExecutions).toBe(2);
		expect(fetches).toBe(1);
	});

	it("fails closed before fetching an older deferred operation without cumulative token accounting", async () => {
		const handle = { provider: "provider", modelId: "model", api: "test-api", id: "operation" };
		const durable = transcript();
		let fetches = 0;
		const deferredResponses: GameDeferredResponseAdapter = {
			fetch: async () => {
				fetches += 1;
				return { ...deferredMessage(handle), content: [{ type: "text", text: "done" }], stopReason: "stop" };
			},
			cancel: async () => undefined,
		};
		const created = await agent(() => createDeferredStream(deferredMessage(handle)), deferredResponses, durable);
		await expect(created.agent.start(INPUT, "older-deferred-run").completion).resolves.toMatchObject({
			status: "deferred",
		});
		const pending = created.agent.readPendingDeferred();
		if (!pending) throw new Error("Expected a pending deferred operation.");
		expect(pending).not.toHaveProperty("reportedTokensBeforeDeferred");
		const restarted = await agent(
			() => {
				throw new Error("A provider call for an incompatible deferred run must not start.");
			},
			deferredResponses,
			durable,
			undefined,
			{ maxReportedTokensPerRun: 10 },
		);

		expect(() => restarted.agent.resumeDeferred(pending.operationId)).toThrow(
			"valid cumulative reported-token accounting",
		);
		expect(fetches).toBe(0);
	});

	it("does not recapture or republish context while fetching the same deferred request", async () => {
		const handle = { provider: "provider", modelId: "model", api: "test-api", id: "operation" };
		let contextLoads = 0;
		let requestSourceCollects = 0;
		let toolSourceLoads = 0;
		let policyChecks = 0;
		let fetches = 0;
		const created = await agent(
			() => createDeferredStream(deferredMessage(handle)),
			{
				fetch: async () => {
					fetches += 1;
					return fetches === 1
						? deferredMessage(handle)
						: {
								...deferredMessage(handle),
								content: [{ type: "text", text: "ready" }],
								stopReason: "stop",
							};
				},
				cancel: async () => undefined,
			},
			transcript(),
			undefined,
			{
				contextSources: [
					{
						sourceId: "observation",
						load: () => {
							contextLoads += 1;
							return {
								references: [
									{ referenceId: "frame", revision: String(contextLoads), content: { frame: contextLoads } },
								],
							};
						},
					},
				],
				requestSources: [
					{
						sourceId: "catalog",
						collect: () => {
							requestSourceCollects += 1;
							return {
								context: {
									references: [
										{
											referenceId: "catalog",
											revision: String(requestSourceCollects),
											content: { catalog: requestSourceCollects },
										},
									],
								},
							};
						},
					},
				],
				contextLimits: { maxReferencesPerSnapshot: 2, maxSnapshotBytes: 1_024 },
				toolSource: () => {
					toolSourceLoads += 1;
					return [tool("inspect", async () => ({ content: [{ type: "text", text: "ok" }], details: {} }))];
				},
				toolPolicy: (input, candidates) => {
					policyChecks += 1;
					return allowAllTestTools(input, candidates);
				},
				toolLimits: TEST_TOOL_LIMITS,
			},
		);

		await expect(created.agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "deferred" });
		const pending = created.agent.readPendingDeferred();
		expect(contextLoads).toBe(1);
		const initialPreparation = { requestSourceCollects, toolSourceLoads, policyChecks };

		await expect(created.agent.resumeDeferred(pending?.operationId ?? "missing").completion).resolves.toMatchObject({
			status: "deferred",
		});
		expect({ contextLoads, requestSourceCollects, toolSourceLoads, policyChecks }).toEqual({
			contextLoads: 1,
			...initialPreparation,
		});
		await expect(created.agent.resumeDeferred(pending?.operationId ?? "missing").completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(contextLoads).toBe(1);
		expect({ requestSourceCollects, toolSourceLoads, policyChecks }).toEqual(initialPreparation);
		expect(
			created.events.filter(
				(event) =>
					event.data.type === "item.completed" &&
					event.data.item.itemType === "message" &&
					"context" in event.data.item,
			),
		).toHaveLength(1);
	});

	it("rebuilds current Tools after restart only when a fetched deferred response calls one", async () => {
		const handle = { provider: "provider", modelId: "model", api: "test-api", id: "operation" };
		const durable = transcript();
		const initial = await agent(
			() => createDeferredStream(deferredMessage(handle)),
			{ fetch: async () => deferredMessage(handle), cancel: async () => undefined },
			durable,
		);
		await expect(initial.agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "deferred" });
		const pending = initial.agent.readPendingDeferred();
		if (!pending) throw new Error("Expected a pending deferred operation.");

		let requestSourceCollects = 0;
		let toolExecutions = 0;
		let providerCalls = 0;
		let policyChecks = 0;
		let executionChecks = 0;
		let observationLoads = 0;
		const collected: Array<{ inputId: string; runId: string; turn: string }> = [];
		const restarted = await agent(
			() => {
				providerCalls += 1;
				return createDeferredStream({
					...deferredMessage(handle),
					content: [{ type: "text", text: "done" }],
					stopReason: "stop",
				});
			},
			{
				fetch: async () => ({
					...deferredMessage(handle),
					content: [toolCall("call-1", "inspect")],
					stopReason: "toolUse",
				}),
				cancel: async () => undefined,
			},
			durable,
			undefined,
			{
				contextSources: [
					{
						sourceId: "observation",
						load: () => {
							observationLoads += 1;
							return { references: [{ referenceId: "frame", revision: "1", content: { frame: 1 } }] };
						},
					},
				],
				contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1_024 },
				requestSources: [
					{
						sourceId: "dynamic-tools",
						collect: (input, _signal, coordinate) => {
							requestSourceCollects += 1;
							collected.push({ inputId: input.inputId, runId: coordinate.runId, turn: coordinate.turn });
							return {
								tools: [
									tool("inspect", async () => {
										toolExecutions += 1;
										return { content: [{ type: "text", text: "inspected" }], details: {} };
									}),
								],
							};
						},
					},
				],
				toolPolicy: (input, candidates) => {
					policyChecks += 1;
					return allowAllTestTools(input, candidates);
				},
				toolLimits: TEST_TOOL_LIMITS,
				beforeToolExecution: () => {
					executionChecks += 1;
				},
			},
		);

		await expect(restarted.agent.resumeDeferred(pending.operationId).completion).resolves.toMatchObject({
			status: "completed",
		});
		expect({ requestSourceCollects, toolExecutions, providerCalls }).toEqual({
			requestSourceCollects: 2,
			toolExecutions: 1,
			providerCalls: 1,
		});
		expect({ policyChecks, executionChecks, observationLoads }).toMatchObject({
			executionChecks: 1,
			observationLoads: 1,
		});
		expect(policyChecks).toBeGreaterThan(0);
		expect(collected).toEqual([
			{ inputId: INPUT.inputId, runId: "run-1", turn: "1" },
			{ inputId: INPUT.inputId, runId: "run-1", turn: "2" },
		]);
	});

	it("allows a later Pi Tool turn to open a second deferred operation in the same run", async () => {
		const firstHandle = { provider: "provider", modelId: "model", api: "test-api", id: "operation-a" };
		const secondHandle = { provider: "provider", modelId: "model", api: "test-api", id: "operation-b" };
		let providerCalls = 0;
		const adapter: GameDeferredResponseAdapter = {
			fetch: async (handle) =>
				handle.id === firstHandle.id
					? {
							...deferredMessage(firstHandle),
							content: [toolCall("call-1", "inspect")],
							stopReason: "toolUse",
						}
					: { ...deferredMessage(secondHandle), content: [{ type: "text", text: "done" }], stopReason: "stop" },
			cancel: async () => undefined,
		};
		const created = await agent(
			() => {
				providerCalls += 1;
				return createDeferredStream(providerCalls === 1 ? deferredMessage(firstHandle) : deferredMessage(secondHandle));
			},
			adapter,
			transcript(),
			undefined,
			{
				toolSource: () => [
					tool("inspect", async () => ({
						content: [{ type: "text", text: "inspected" }],
						details: {},
					})),
				],
				toolPolicy: allowAllTestTools,
				toolLimits: TEST_TOOL_LIMITS,
			},
		);
		await created.agent.start(INPUT, "run-1").completion;
		const first = created.agent.readPendingDeferred();
		if (!first) throw new Error("Expected the first pending operation.");

		await expect(created.agent.resumeDeferred(first.operationId).completion).resolves.toMatchObject({
			status: "deferred",
		});
		const second = created.agent.readPendingDeferred();
		if (!second) throw new Error("Expected the second pending operation.");
		expect(second.handle.id).toBe(secondHandle.id);
		await expect(created.transcript.restore(STREAM)).resolves.toMatchObject({
			pendingDeferred: { operationId: second.operationId, handle: { id: secondHandle.id } },
		});

		await expect(created.agent.resumeDeferred(second.operationId).completion).resolves.toMatchObject({
			status: "completed",
		});
		await expect(created.transcript.restore(STREAM)).resolves.toMatchObject({
			terminalDeferredRuns: [
				{
					coordinate: second.coordinate,
					operationId: second.operationId,
					inputId: INPUT.inputId,
					status: "completed",
				},
			],
		});
	});

	it("keeps a failed or mismatched fetch pending and commits local abandonment before provider cancellation", async () => {
		const handle = { provider: "provider", modelId: "model", api: "test-api", id: "operation" };
		let fetch = 0;
		let storeAtCancel: Awaited<ReturnType<DurableTranscript["restore"]>> | undefined;
		const durable = transcript();
		const adapter: GameDeferredResponseAdapter = {
			fetch: async () => {
				fetch += 1;
				return fetch === 1
					? {
							...deferredMessage(handle),
							provider: "other-provider",
							content: [{ type: "text", text: "wrong-origin" }],
							stopReason: "stop",
						}
					: deferredMessage(handle);
			},
			cancel: async () => {
				storeAtCancel = await durable.restore(STREAM);
				throw new Error("provider unavailable");
			},
		};
		const initial: StreamFn = () => {
			const stream = createDeferredStream(deferredMessage(handle));
			return stream;
		};
		const created = await agent(initial, adapter, durable);
		await created.agent.start(INPUT, "run-1").completion;
		const pending = created.agent.readPendingDeferred();
		if (!pending) throw new Error("Expected a pending deferred operation.");

		await expect(created.agent.resumeDeferred(pending.operationId).completion).resolves.toMatchObject({
			status: "deferred",
		});
		expect(created.agent.readPendingDeferred()).toMatchObject({ operationId: pending.operationId });
		expect(JSON.stringify(created.events)).not.toContain("wrong-origin");

		await expect(created.agent.abandonDeferred(pending.operationId)).resolves.toEqual({
			operationId: pending.operationId,
			providerCancellation: "failed",
		});
		expect(storeAtCancel).not.toHaveProperty("pendingDeferred");
		expect(created.agent.readPendingDeferred()).toBeUndefined();
		await expect(created.transcript.restore(STREAM)).resolves.not.toHaveProperty("pendingDeferred");
	});

	it("prevents resume from racing a durable abandonment transition", async () => {
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
		const durable = transcript(store);
		const handle = { provider: "provider", modelId: "model", api: "test-api", id: "operation" };
		const created = await agent(
			() => createDeferredStream(deferredMessage(handle)),
			{ fetch: async () => deferredMessage(handle), cancel: async () => undefined },
			durable,
		);
		await created.agent.start(INPUT, "run-1").completion;
		const pending = created.agent.readPendingDeferred();
		if (!pending) throw new Error("Expected a pending deferred operation.");

		const abandonment = created.agent.abandonDeferred(pending.operationId);
		await appendEntered;
		expect(() => created.agent.resumeDeferred(pending.operationId)).toThrow(GameAgentBusyError);
		releaseAppend();
		await expect(abandonment).resolves.toMatchObject({ providerCancellation: "completed" });
	});
});

function createDeferredStream(message: AssistantMessage) {
	const stream = new (class {
		readonly result = async () => message;
		async *[Symbol.asyncIterator]() {
			yield { type: "done" as const, reason: message.stopReason, message };
		}
	})();
	return stream as unknown as ReturnType<StreamFn>;
}

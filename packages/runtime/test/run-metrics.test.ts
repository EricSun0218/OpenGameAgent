import { describe, expect, it } from "vitest";
import { DurableTranscript, InMemoryTranscriptStore } from "@opengameagent/agent";
import { GameRuntime, InMemoryEventStore } from "../src/index.js";
import {
	allowAllTestTools,
	assistant,
	controlledResponse,
	deferred,
	messageResponse,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
	tool,
	toolCall,
} from "./pi-fakes.js";

const STREAM = { sessionId: "metrics-session", actorId: "metrics-actor" };

describe("runtime run metrics", () => {
	it("keeps deferred resumption timing separate from its earlier admission and suspended wall time", async () => {
		let now = 3;
		const events = new (class extends InMemoryEventStore {
			override async append(...parameters: Parameters<InMemoryEventStore["append"]>) {
				if (parameters[3].type === "run.queued") now = 8;
				return super.append(...parameters);
			}
		})();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
			metricsNow: () => now,
			transcript: new DurableTranscript(new InMemoryTranscriptStore(), {
				maxRecordBytes: 16_384,
				maxCommitBytes: 65_536,
				maxRestoreBytes: 131_072,
				maxJsonDepth: 16,
			}),
		});
		const providerHandle = {
			provider: TEST_MODEL.provider,
			modelId: TEST_MODEL.id,
			api: TEST_MODEL.api,
			id: "operation",
		};
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			metricsNow: () => 0,
			modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: providerHandle }),
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "resumed" }]),
				cancel: async () => undefined,
			},
		});
		const run = runtime.start({ ...STREAM, inputId: "deferred", payload: {} });
		await run.completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected pending deferred operation.");
		now = 100;
		const resume = await runtime.resumeDeferred(STREAM, pending.operationId);
		await resume.completion;
		const metrics = (await events.read(STREAM, { limit: 100 })).events.flatMap((event) =>
			event.data.type === "run.metrics" ? [event.data] : [],
		);
		expect(metrics).toMatchObject([
			{ segmentStatus: "deferred", durations: { admission: "5000", queue: "0", total: "5000" } },
			{ segmentStatus: "completed", durations: { admission: "0", queue: "0", total: "0" } },
		]);
	});

	it("records a pre-provider startup failure without inventing a provider attempt", async () => {
		const events = new InMemoryEventStore();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
			metricsNow: () => 0,
		});
		let providerCalls = 0;
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			metricsNow: () => NaN,
			modelStream: () => {
				providerCalls += 1;
				return textResponse("must not start");
			},
		});
		const run = runtime.start({ ...STREAM, inputId: "startup-failure", payload: {} });
		await expect(run.completion).resolves.toMatchObject({ status: "failed" });
		const data = (await events.read(STREAM, { limit: 3 })).events.map((event) => event.data);
		expect(data).toMatchObject([
			{ type: "run.queued" },
			{ type: "run.metrics", segmentStatus: "failed", counts: { providerAttempts: "0" }, durations: { total: "0" } },
			{ type: "run.completed", status: "failed" },
		]);
		expect(providerCalls).toBe(0);
	});

	it("leaves canonical authority unsettled when queued-run metrics cannot be persisted", async () => {
		const events = new (class extends InMemoryEventStore {
			override async append(...parameters: Parameters<InMemoryEventStore["append"]>) {
				if (parameters[3].type === "run.metrics") throw new Error("metrics authority unavailable");
				return super.append(...parameters);
			}
		})();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
		});
		let providerCalls = 0;
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => {
				providerCalls += 1;
				return textResponse("must not start");
			},
		});
		const run = runtime.start({ ...STREAM, inputId: "metrics-failure", payload: {} });
		await expect(runtime.retireActor(STREAM)).rejects.toBeInstanceOf(AggregateError);
		await expect(run.completion).resolves.toMatchObject({ status: "failed" });
		expect((await events.read(STREAM, { limit: 3 })).events.map((event) => event.data.type)).toEqual([
			"run.queued",
			"control.accepted",
		]);
		await expect(events.readRun(STREAM, "metrics-failure")).resolves.toMatchObject({ status: null });
		expect(providerCalls).toBe(0);
	});

	it("emits zero-provider metrics for a retirement that cancels an in-flight admission", async () => {
		let now = 10;
		const entered = deferred();
		const release = deferred();
		const events = new (class extends InMemoryEventStore {
			override async append(...parameters: Parameters<InMemoryEventStore["append"]>) {
				if (parameters[3].type === "run.queued") {
					entered.resolve();
					await release.promise;
				}
				if (parameters[3].type === "control.accepted") now = 35;
				return super.append(...parameters);
			}
		})();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
			metricsNow: () => now,
		});
		let providerCalls = 0;
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => {
				providerCalls += 1;
				return textResponse("must not start");
			},
		});
		const run = runtime.start({ ...STREAM, inputId: "retired-admission", payload: {} });
		await entered.promise;
		const retirement = runtime.retireActor(STREAM);
		now = 20;
		release.resolve();
		await expect(retirement).resolves.toBe(true);
		await expect(run.completion).resolves.toMatchObject({ status: "aborted" });
		const runEvents = (await events.read(STREAM, { limit: 4 })).events;
		expect(runEvents.map((event) => event.data.type)).toEqual([
			"run.queued",
			"control.accepted",
			"run.metrics",
			"run.completed",
		]);
		expect(runEvents[2]?.data).toEqual({
			type: "run.metrics",
			unit: "microseconds",
			segmentStatus: "aborted",
			durations: {
				admission: "10000",
				queue: "15000",
				total: "25000",
				native: "25000",
				contextAndRecall: "0",
				provider: "0",
				durable: "0",
			},
			counts: { providerAttempts: "0", retries: "0", fallbacks: "0", toolCalls: "0" },
		});
		expect(providerCalls).toBe(0);
	});

	it("adds measured admission and queue time to one exact run before its terminal event", async () => {
		let now = 0;
		const entered = deferred();
		const release = deferred();
		const events = new (class extends InMemoryEventStore {
			override async append(...parameters: Parameters<InMemoryEventStore["append"]>) {
				const data = parameters[3];
				if (data.type === "run.queued" && data.inputId === "measured") {
					entered.resolve();
					await release.promise;
				}
				return super.append(...parameters);
			}
		})();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
			metricsNow: () => now,
		});
		const blocker = controlledResponse();
		let calls = 0;
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			metricsNow: () => 0,
			modelStream: (model, context, options) => {
				calls += 1;
				if (calls === 1) return blocker.streamFn(model, context, options);
				if (calls === 2) return messageResponse(assistant([toolCall("first-call", "test-tool")], "toolUse"));
				return textResponse("response");
			},
			toolSource: () => [tool("test-tool", async () => ({ content: [], details: {} }))],
			toolLimits: TEST_TOOL_LIMITS,
			toolPolicy: allowAllTestTools,
		});
		const active = runtime.start({ ...STREAM, inputId: "blocker", payload: {} });
		await blocker.started;
		now = 10;
		const measured = runtime.start({ ...STREAM, inputId: "measured", payload: {} });
		await entered.promise;
		now = 15;
		release.resolve();
		await runtime.control(measured.initialCoordinate, { type: "steer", text: "not active" });
		now = 30;
		blocker.finish();
		await Promise.all([active.completion, measured.completion]);
		const runEvents = (await events.read(STREAM, { limit: 100 })).events.filter(
			(event) => event.coordinate.runId === measured.runId,
		);
		const metrics = runEvents.filter((event) => event.data.type === "run.metrics");
		expect(metrics).toHaveLength(1);
		expect(metrics[0]?.data).toMatchObject({
			type: "run.metrics",
			segmentStatus: "completed",
			durations: {
				admission: "5000",
				queue: "15000",
				total: "20000",
				native: "20000",
				firstResponse: "20000",
				firstTool: "20000",
			},
			counts: { providerAttempts: "2", toolCalls: "1" },
		});
		expect(runEvents.slice(-2).map((event) => event.data.type)).toEqual(["run.metrics", "run.completed"]);
	});
});

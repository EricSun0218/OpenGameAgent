import { DurableTranscript, InMemoryTranscriptStore } from "@opengameagent/agent";
import { describe, expect, it } from "vitest";
import { GameRuntime, InMemoryEventStore } from "../src/index.js";
import { FailingRunCompletionStore } from "./event-store-fakes.js";
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

const STREAM = { sessionId: "session", actorId: "chat0" };
const AUTHORITY = { scope: "battle", generation: "1" };
const PROVIDER_HANDLE = {
	provider: TEST_MODEL.provider,
	modelId: TEST_MODEL.id,
	api: TEST_MODEL.api,
	id: "deferred-provider-operation",
};

function transcript(store = new InMemoryTranscriptStore()): DurableTranscript {
	return new DurableTranscript(store, {
		maxRecordBytes: 16_384,
		maxCommitBytes: 65_536,
		maxRestoreBytes: 131_072,
		maxJsonDepth: 16,
	});
}

describe("durable scoped run authority", () => {
	it.each(["run.metrics", "run.completed"] as const)(
		"reports failed queued deferred abandonment when %s cannot be persisted",
		async (blockedType) => {
			let blocked = false;
			const events = new (class extends InMemoryEventStore {
				override append(...parameters: Parameters<InMemoryEventStore["append"]>) {
					if (blocked && parameters[0].actorId === STREAM.actorId && parameters[3].type === blockedType)
						throw new Error("deferred settlement unavailable");
					return super.append(...parameters);
				}
			})();
			const runtime = new GameRuntime(events, {
				maxActiveActors: 1,
				maxQueuedRuns: 1,
				maxInputBytes: 2,
				maxInputDepth: 1,
				transcript: transcript(),
			});
			const blocker = controlledResponse();
			await runtime.registerActors([
				{
					...STREAM,
					model: TEST_MODEL,
					modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: PROVIDER_HANDLE }),
					deferredResponses: { fetch: async () => assistant([]), cancel: async () => undefined },
				},
				{ ...STREAM, actorId: "blocker", model: TEST_MODEL, modelStream: blocker.streamFn },
			]);
			await (await runtime.submit({ ...STREAM, inputId: "suspended", payload: {}, admissionAuthority: AUTHORITY }))
				.completion;
			const pending = runtime.readDeferred(STREAM);
			if (!pending) throw new Error("Expected a deferred operation.");
			const blocking = await runtime.submit({ ...STREAM, actorId: "blocker", inputId: "blocker", payload: {} });
			await blocker.started;
			const resumed = await runtime.resumeDeferred(STREAM, pending.operationId);
			blocked = true;
			const request = { ...STREAM, ...AUTHORITY, operationId: "failed-resume-close" };
			await expect(runtime.closeAuthority(request)).rejects.toThrow("durably closed");
			await expect(resumed.completion).resolves.toMatchObject({ status: "failed" });
			await expect(runtime.readAuthorityClosure(request)).resolves.toMatchObject(request);
			await expect(events.readRun(STREAM, "suspended")).resolves.toMatchObject({ status: "deferred" });
			expect(
				(await events.read(STREAM, { limit: 100 })).events.some((event) => event.data.type === "run.completed"),
			).toBe(false);
			blocker.finish();
			await blocking.completion;
		},
	);

	it("fences deferred provider activation when its authority closes after resume admission", async () => {
		const events = new InMemoryEventStore();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
			transcript: transcript(),
		});
		const blocker = controlledResponse();
		let fetches = 0;
		await runtime.registerActors([
			{
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: PROVIDER_HANDLE }),
				deferredResponses: {
					fetch: async () => {
						fetches += 1;
						return assistant([]);
					},
					cancel: async () => undefined,
				},
			},
			{ ...STREAM, actorId: "blocker", model: TEST_MODEL, modelStream: blocker.streamFn },
		]);
		await (await runtime.submit({ ...STREAM, inputId: "suspended", payload: {}, admissionAuthority: AUTHORITY }))
			.completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a deferred operation.");
		const blocking = await runtime.submit({ ...STREAM, actorId: "blocker", inputId: "blocker", payload: {} });
		await blocker.started;
		const resumed = await runtime.resumeDeferred(STREAM, pending.operationId);
		const request = { ...STREAM, ...AUTHORITY, operationId: "after-resume-admission" };
		await events.closeAuthority(request);
		blocker.finish();
		await blocking.completion;
		await expect(resumed.completion).resolves.toMatchObject({ status: "deferred" });
		expect(fetches).toBe(0);
		expect(runtime.readDeferred(STREAM)).toEqual(pending);
		expect((await events.read(STREAM, { limit: 100 })).events.some((event) => event.data.type === "run.resumed")).toBe(
			false,
		);
		await expect(runtime.closeAuthority(request)).resolves.toMatchObject(request);
		expect(runtime.readDeferred(STREAM)).toBeUndefined();
	});

	it("drains an active deferred fetch and durably abandons the pending operation left by cancellation", async () => {
		const events = new InMemoryEventStore();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
			transcript: transcript(),
		});
		const fetching = deferred();
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: PROVIDER_HANDLE }),
			deferredResponses: {
				fetch: async (_handle, signal) => {
					fetching.resolve();
					await new Promise<void>((_resolve, reject) => {
						signal.addEventListener("abort", () => reject(signal.reason), { once: true });
					});
					return assistant([]);
				},
				cancel: async () => undefined,
			},
		});
		await (await runtime.submit({ ...STREAM, inputId: "suspended", payload: {}, admissionAuthority: AUTHORITY }))
			.completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a deferred operation.");
		const resumed = await runtime.resumeDeferred(STREAM, pending.operationId);
		await fetching.promise;
		await expect(
			runtime.closeAuthority({ ...STREAM, ...AUTHORITY, operationId: "active-resume-close" }),
		).resolves.toBeDefined();
		await resumed.completion;
		expect(runtime.readDeferred(STREAM)).toBeUndefined();
		await expect(events.readRun(STREAM, "suspended")).resolves.toMatchObject({ status: "aborted" });
	});

	it("abandons matching deferred authority locally without waiting for remote provider cancellation", async () => {
		const events = new InMemoryEventStore();
		const durable = transcript();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
			transcript: durable,
		});
		const cancelEntered = deferred();
		const cancelRelease = deferred();
		let calls = 0;
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () =>
				++calls === 1
					? messageResponse({ ...assistant([], "deferred"), deferred: PROVIDER_HANDLE })
					: textResponse("same persona"),
			deferredResponses: {
				fetch: async () => assistant([]),
				cancel: async () => {
					cancelEntered.resolve();
					await cancelRelease.promise;
				},
			},
		});
		const input = { ...STREAM, inputId: "suspended", payload: {}, admissionAuthority: AUTHORITY };
		const suspended = await runtime.submit(input);
		await expect(suspended.completion).resolves.toMatchObject({ status: "deferred" });
		await expect(
			runtime.closeAuthority({ ...STREAM, scope: "chat", generation: "1", operationId: "unrelated" }),
		).resolves.toBeDefined();
		expect(runtime.readDeferred(STREAM)).toMatchObject({ input: { admissionAuthority: AUTHORITY } });
		const request = { ...STREAM, ...AUTHORITY, operationId: "close-deferred" };
		const close = runtime.closeAuthority(request);
		await cancelEntered.promise;
		await expect(close).resolves.toMatchObject(request);
		expect(runtime.readDeferred(STREAM)).toBeUndefined();
		await expect(events.readRun(STREAM, input.inputId)).resolves.toMatchObject({ status: "aborted" });
		expect((await durable.restore(STREAM)).pendingDeferred).toBeUndefined();
		const next = await runtime.submit({
			...input,
			inputId: "next-generation",
			admissionAuthority: { ...AUTHORITY, generation: "2" },
		});
		await expect(next.completion).resolves.toMatchObject({ status: "completed", finalOutput: "same persona" });
		cancelRelease.resolve();
	});

	it("closes a queued deferred resume without fetching or blocking an unrelated active actor", async () => {
		const events = new InMemoryEventStore();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
			transcript: transcript(),
		});
		const blocker = controlledResponse();
		let fetches = 0;
		await runtime.registerActors([
			{
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: PROVIDER_HANDLE }),
				deferredResponses: {
					fetch: async () => {
						fetches += 1;
						return assistant([]);
					},
					cancel: async () => undefined,
				},
			},
			{ ...STREAM, actorId: "blocker", model: TEST_MODEL, modelStream: blocker.streamFn },
		]);
		const first = await runtime.submit({ ...STREAM, inputId: "suspended", payload: {}, admissionAuthority: AUTHORITY });
		await first.completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a deferred operation.");
		const blocking = await runtime.submit({ ...STREAM, actorId: "blocker", inputId: "blocker", payload: {} });
		await blocker.started;
		const resume = await runtime.resumeDeferred(STREAM, pending.operationId);
		await expect(
			runtime.closeAuthority({ ...STREAM, ...AUTHORITY, operationId: "queued-resume" }),
		).resolves.toBeDefined();
		await expect(resume.completion).resolves.toMatchObject({ status: "aborted" });
		await expect(events.readRun(STREAM, "suspended")).resolves.toMatchObject({ status: "aborted" });
		const runEvents = (await events.read(STREAM, { limit: 100 })).events;
		expect(runEvents.filter((event) => event.data.type === "run.metrics")).toHaveLength(2);
		expect(runEvents.at(-2)?.data).toMatchObject({
			type: "run.metrics",
			counts: { providerAttempts: "0", toolCalls: "0" },
		});
		expect(runEvents.at(-1)?.data).toMatchObject({ type: "run.completed", status: "aborted" });
		expect(fetches).toBe(0);
		blocker.finish();
		await expect(blocking.completion).resolves.toMatchObject({ status: "completed" });
	});

	it("rejects deferred resume against an independently closed generation and preserves pending state for explicit cleanup", async () => {
		const events = new InMemoryEventStore();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
			transcript: transcript(),
		});
		let fetches = 0;
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: PROVIDER_HANDLE }),
			deferredResponses: {
				fetch: async () => {
					fetches += 1;
					return assistant([]);
				},
				cancel: async () => undefined,
			},
		});
		await (await runtime.submit({ ...STREAM, inputId: "suspended", payload: {}, admissionAuthority: AUTHORITY }))
			.completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected a deferred operation.");
		const request = { ...STREAM, ...AUTHORITY, operationId: "external-deferred-close" };
		await events.closeAuthority(request);
		await expect(runtime.resumeDeferred(STREAM, pending.operationId)).rejects.toMatchObject({
			name: "RunAuthorityClosedError",
		});
		expect(runtime.readDeferred(STREAM)).toEqual(pending);
		expect(fetches).toBe(0);
		await runtime.closeAuthority(request);
		expect(runtime.readDeferred(STREAM)).toBeUndefined();
	});

	it.each(["host guard", "transcript start"] as const)(
		"fences the final Tool invocation when authority closes during %s",
		async (boundary) => {
			const events = new InMemoryEventStore();
			const request = { ...STREAM, ...AUTHORITY, operationId: "close-tool" };
			const backing = new (class extends InMemoryTranscriptStore {
				override async append(...parameters: Parameters<InMemoryTranscriptStore["append"]>) {
					const result = await super.append(...parameters);
					if (boundary === "transcript start" && parameters[2].some((record) => record.type === "tool.started"))
						await events.closeAuthority(request);
					return result;
				}
			})();
			const runtime = new GameRuntime(events, {
				maxActiveActors: 1,
				maxQueuedRuns: 1,
				maxInputBytes: 2,
				maxInputDepth: 1,
				transcript: transcript(backing),
			});
			let effects = 0;
			let guardCalls = 0;
			await runtime.registerActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: scriptedStream([
					() => messageResponse(assistant([toolCall("effect-call", "effect")], "toolUse")),
					() => textResponse("denied"),
				]),
				toolSource: () => [
					tool("effect", async () => {
						effects += 1;
						return { content: [{ type: "text", text: "effect" }], details: {} };
					}),
				],
				toolPolicy: allowAllTestTools,
				toolLimits: TEST_TOOL_LIMITS,
				beforeToolExecution: async () => {
					guardCalls += 1;
					if (boundary === "host guard") await events.closeAuthority(request);
				},
			});
			await (await runtime.submit({ ...STREAM, inputId: "tool", payload: {}, admissionAuthority: AUTHORITY }))
				.completion;
			expect(guardCalls).toBe(1);
			expect(effects).toBe(0);
			await expect(runtime.readAuthorityClosure(request)).resolves.toBeDefined();
		},
	);

	it.each(["active", "queued"] as const)(
		"keeps the durable closure queryable when %s canonical settlement fails",
		async (failedKind) => {
			const events = new FailingRunCompletionStore("terminal unavailable");
			const runtime = new GameRuntime(events, {
				maxActiveActors: 1,
				maxQueuedRuns: 1,
				maxInputBytes: 2,
				maxInputDepth: 1,
			});
			const controlled = controlledResponse();
			await runtime.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: controlled.streamFn });
			const active = await runtime.submit({ ...STREAM, inputId: "active", payload: {}, admissionAuthority: AUTHORITY });
			await controlled.started;
			const queued = await runtime.submit({ ...STREAM, inputId: "queued", payload: {}, admissionAuthority: AUTHORITY });
			events.blockedRunId = failedKind === "active" ? active.runId : queued.runId;
			const request = { ...STREAM, ...AUTHORITY, operationId: "close-with-terminal-failure" };
			await expect(runtime.closeAuthority(request)).rejects.toThrow("durably closed");
			await expect(active.completion).resolves.toMatchObject({ status: "aborted" });
			await expect(queued.completion).resolves.toMatchObject({
				status: failedKind === "queued" ? "failed" : "aborted",
			});
			await expect(runtime.readAuthorityClosure(request)).resolves.toMatchObject(request);
			await expect(events.readRun(STREAM, failedKind)).resolves.toMatchObject({ status: null });
			await expect(
				runtime.submit({ ...STREAM, inputId: "another", payload: {}, admissionAuthority: AUTHORITY }),
			).rejects.toMatchObject({ name: "RunAuthorityClosedError" });
		},
	);

	it("drains only matching active and queued runs while the same actor continues other authority lanes", async () => {
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 3,
			maxInputBytes: 2,
			maxInputDepth: 1,
		});
		const response = controlledResponse();
		let providerCalls = 0;
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: (model, context, options) => {
				providerCalls += 1;
				return providerCalls === 1 ? response.streamFn(model, context, options) : textResponse("same persona");
			},
		});
		const active = await runtime.submit({ ...STREAM, inputId: "active", payload: {}, admissionAuthority: AUTHORITY });
		await response.started;
		const queued = await runtime.submit({ ...STREAM, inputId: "queued", payload: {}, admissionAuthority: AUTHORITY });
		const unrelated = await runtime.submit({
			...STREAM,
			inputId: "other-scope",
			payload: {},
			admissionAuthority: { scope: "chat", generation: "1" },
		});
		const next = await runtime.submit({
			...STREAM,
			inputId: "new-generation",
			payload: {},
			admissionAuthority: { ...AUTHORITY, generation: "2" },
		});
		const request = { ...STREAM, ...AUTHORITY, operationId: "close-active" };
		const [closure, repeated] = await Promise.all([runtime.closeAuthority(request), runtime.closeAuthority(request)]);
		expect(repeated).toEqual(closure);
		expect(closure).toMatchObject({ ...STREAM, ...AUTHORITY });
		await expect(Promise.all([active.completion, queued.completion])).resolves.toEqual([
			{ runId: active.runId, status: "aborted" },
			{ runId: queued.runId, status: "aborted" },
		]);
		await expect(Promise.all([unrelated.completion, next.completion])).resolves.toMatchObject([
			{ status: "completed", finalOutput: "same persona" },
			{ status: "completed", finalOutput: "same persona" },
		]);
		expect(providerCalls).toBe(3);
	});

	it("replays a lost close response across runtime restart without reopening the generation", async () => {
		const events = new InMemoryEventStore();
		const options = { maxActiveActors: 1, maxQueuedRuns: 1, maxInputBytes: 2, maxInputDepth: 1 };
		const request = { ...STREAM, ...AUTHORITY, operationId: "lost-response" };
		await new GameRuntime(events, options).closeAuthority(request);
		const restarted = new GameRuntime(events, options);
		const stored = await restarted.readAuthorityClosure(request);
		await expect(restarted.closeAuthority(request)).resolves.toEqual(stored);
		await restarted.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: () => textResponse("must not start") });
		await expect(
			restarted.submit({ ...STREAM, inputId: "rejected", payload: {}, admissionAuthority: AUTHORITY }),
		).rejects.toMatchObject({ name: "RunAuthorityClosedError" });
	});

	it("binds input idempotency to authority even when payload and actor are unchanged", async () => {
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
		});
		await runtime.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: () => textResponse("completed") });
		const first = await runtime.submit({
			...STREAM,
			inputId: "same-input",
			payload: {},
			admissionAuthority: AUTHORITY,
		});
		await first.completion;
		await expect(
			runtime.submit({
				...STREAM,
				inputId: "same-input",
				payload: {},
				admissionAuthority: { ...AUTHORITY, generation: "2" },
			}),
		).rejects.toMatchObject({ name: "RunInputConflictError" });
	});

	it("publishes closure while an earlier admission is blocked and rejects its later durable enqueue", async () => {
		const entered = deferred();
		const release = deferred();
		const closed = deferred();
		const events = new (class extends InMemoryEventStore {
			override async append(...parameters: Parameters<InMemoryEventStore["append"]>) {
				if (parameters[3].type === "run.queued") {
					entered.resolve();
					await release.promise;
				}
				return super.append(...parameters);
			}
			override async closeAuthority(...parameters: Parameters<InMemoryEventStore["closeAuthority"]>) {
				const result = await super.closeAuthority(...parameters);
				closed.resolve();
				return result;
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
				return textResponse("unexpected");
			},
		});
		const admission = runtime.submit({
			...STREAM,
			inputId: "admission-race",
			payload: {},
			admissionAuthority: AUTHORITY,
		});
		const rejected = expect(admission).rejects.toMatchObject({ name: "RunAuthorityClosedError" });
		await entered.promise;
		const closure = runtime.closeAuthority({ ...STREAM, ...AUTHORITY, operationId: "racing-close" });
		await closed.promise;
		await expect(runtime.readAuthorityClosure({ ...STREAM, ...AUTHORITY })).resolves.toBeDefined();
		release.resolve();
		await rejected;
		await expect(closure).resolves.toMatchObject({ operationId: "racing-close" });
		expect(providerCalls).toBe(0);
	});

	it("checks a separately persisted closure at queued provider activation", async () => {
		const events = new InMemoryEventStore();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 2,
			maxInputDepth: 1,
		});
		const blocker = controlledResponse();
		let providerCalls = 0;
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: (model, context, options) => {
				providerCalls += 1;
				return providerCalls === 1 ? blocker.streamFn(model, context, options) : textResponse("must not start");
			},
		});
		const active = await runtime.submit({ ...STREAM, inputId: "unscoped-blocker", payload: {} });
		await blocker.started;
		const queued = await runtime.submit({
			...STREAM,
			inputId: "closed-before-start",
			payload: {},
			admissionAuthority: AUTHORITY,
		});
		await events.closeAuthority({ ...STREAM, ...AUTHORITY, operationId: "external-close" });
		blocker.finish();
		await active.completion;
		await expect(queued.completion).resolves.toMatchObject({ status: "failed" });
		expect(providerCalls).toBe(1);
	});

	it("persists closure before any run exists and permits a different generation on the same actor", async () => {
		const events = new InMemoryEventStore();
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
				return textResponse("same persona");
			},
		});
		const request = { ...STREAM, ...AUTHORITY, operationId: "close-before-submit" };
		const closure = await runtime.closeAuthority(request);
		await expect(runtime.readAuthorityClosure(request)).resolves.toEqual(closure);
		await expect(
			runtime.submit({ ...STREAM, inputId: "closed", payload: {}, admissionAuthority: AUTHORITY }),
		).rejects.toMatchObject({ name: "RunAuthorityClosedError" });
		await expect(
			runtime.start({ ...STREAM, inputId: "closed-start", payload: {}, admissionAuthority: AUTHORITY }).completion,
		).resolves.toMatchObject({ status: "failed" });
		const next = await runtime.submit({
			...STREAM,
			inputId: "fresh",
			payload: {},
			admissionAuthority: { ...AUTHORITY, generation: "2" },
		});
		await expect(next.completion).resolves.toMatchObject({ status: "completed", finalOutput: "same persona" });
		expect(providerCalls).toBe(1);
	});
});

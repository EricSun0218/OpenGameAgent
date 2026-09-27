import type { StreamFn } from "@earendil-works/pi-agent-core";
import { DurableTranscript, InMemoryTranscriptStore } from "@opengameagent/agent";
import { describe, expect, it } from "vitest";
import {
	ActorAlreadyRegisteredError,
	ActorNotRegisteredError,
	ActorReplacementBusyError,
	ActorReplacementInProgressError,
	ActorRetirementInProgressError,
	ActorRetirementBusyError,
	ActorRetirementPersistenceError,
	GameRuntime,
	InMemoryEventStore,
	RuntimeAdmissionClosedError,
	RuntimeAdmissionOpenError,
} from "../src/index.js";
import { FailingRunCompletionStore } from "./event-store-fakes.js";
import { assistant, controlledResponse, deferred, messageResponse, TEST_MODEL, textResponse } from "./pi-fakes.js";

const STREAM = { sessionId: "session", actorId: "actor" } as const;

class ControlledRevisionReadEventStore extends InMemoryEventStore {
	private nextRead:
		| { kind: "pause"; started: ReturnType<typeof deferred>; release: ReturnType<typeof deferred> }
		| { kind: "fail"; failure: Error }
		| undefined;
	private nextRunRead: { started: ReturnType<typeof deferred>; release: ReturnType<typeof deferred> } | undefined;

	pauseNextRead(): Readonly<{ started: Promise<void>; release: () => void }> {
		if (this.nextRead) throw new Error("A revision-read control is already pending.");
		const started = deferred();
		const release = deferred();
		this.nextRead = { kind: "pause", started, release };
		return Object.freeze({ started: started.promise, release: () => release.resolve() });
	}

	failNextRead(failure: Error): void {
		if (this.nextRead) throw new Error("A revision-read control is already pending.");
		this.nextRead = { kind: "fail", failure };
	}

	pauseNextRunRead(): Readonly<{ started: Promise<void>; release: () => void }> {
		if (this.nextRunRead) throw new Error("A run-read control is already pending.");
		const started = deferred();
		const release = deferred();
		this.nextRunRead = { started, release };
		return Object.freeze({ started: started.promise, release: () => release.resolve() });
	}

	override async readRevision(...parameters: Parameters<InMemoryEventStore["readRevision"]>) {
		const control = this.nextRead;
		this.nextRead = undefined;
		if (control?.kind === "pause") {
			control.started.resolve();
			await control.release.promise;
		}
		if (control?.kind === "fail") throw control.failure;
		return super.readRevision(...parameters);
	}

	override async readRun(...parameters: Parameters<InMemoryEventStore["readRun"]>) {
		const control = this.nextRunRead;
		this.nextRunRead = undefined;
		if (control) {
			control.started.resolve();
			await control.release.promise;
		}
		return super.readRun(...parameters);
	}
}

class ControlledAppendEventStore extends InMemoryEventStore {
	private nextAppend:
		| {
				type: Parameters<InMemoryEventStore["append"]>[3]["type"];
				started: ReturnType<typeof deferred>;
				release: ReturnType<typeof deferred>;
		  }
		| undefined;

	pauseNextAppend(type: Parameters<InMemoryEventStore["append"]>[3]["type"]): Readonly<{
		started: Promise<void>;
		release: () => void;
	}> {
		if (this.nextAppend) throw new Error("An append control is already pending.");
		const started = deferred();
		const release = deferred();
		this.nextAppend = { type, started, release };
		return Object.freeze({ started: started.promise, release: () => release.resolve() });
	}

	override async append(...parameters: Parameters<InMemoryEventStore["append"]>) {
		const control = this.nextAppend;
		if (control && parameters[3].type === control.type) {
			this.nextAppend = undefined;
			control.started.resolve();
			await control.release.promise;
		}
		return super.append(...parameters);
	}
}

async function createRuntime(modelStream: StreamFn, eventStore = new InMemoryEventStore()): Promise<GameRuntime> {
	let nextRun = 0;
	const runtime = new GameRuntime(eventStore, {
		maxActiveActors: 1,
		maxQueuedRuns: 4,
		maxInputBytes: 4_096,
		maxInputDepth: 8,
		newRunId: () => `run-${++nextRun}`,
	});
	await runtime.registerActor({ ...STREAM, model: TEST_MODEL, modelStream });
	return runtime;
}

describe("GameRuntime lifecycle", () => {
	it.each(["active", "queued"] as const)(
		"releases a stopped actor but reports an unsettled canonical %s run for later reconciliation",
		async (failedKind) => {
			const response = controlledResponse();
			const events = new FailingRunCompletionStore("retirement terminal unavailable");
			let providerCalls = 0;
			const runtime = await createRuntime((model, context, options) => {
				providerCalls += 1;
				return response.streamFn(model, context, options);
			}, events);
			const active = runtime.start({ ...STREAM, inputId: "active", payload: {} });
			await response.started;
			const queued = runtime.start({ ...STREAM, inputId: "queued", payload: {} });
			const failed = failedKind === "active" ? active : queued;
			events.blockedRunId = failed.runId;
			await expect(runtime.retireActor(STREAM)).rejects.toMatchObject({
				errors: expect.arrayContaining([expect.any(ActorRetirementPersistenceError)]),
			});
			await expect(Promise.all([active.completion, queued.completion])).resolves.toEqual([
				{ runId: active.runId, status: "aborted" },
				{ runId: queued.runId, status: failedKind === "queued" ? "failed" : "aborted" },
			]);
			expect(() => runtime.readActiveCoordinate(STREAM)).toThrow(ActorNotRegisteredError);
			await expect(events.readRun(STREAM, failedKind)).resolves.toMatchObject({ status: null });
			expect(
				(await events.read(STREAM, { limit: 100 })).events.some(
					(event) => event.coordinate.runId === failed.runId && event.data.type === "run.completed",
				),
			).toBe(false);
			events.blockedRunId = undefined;
			await runtime.registerActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => {
					providerCalls += 1;
					return textResponse("must not replay orphan");
				},
			});
			const reconciled = await runtime.submit({ ...STREAM, inputId: failedKind, payload: {} });
			await expect(reconciled.completion).resolves.toEqual({ runId: failed.runId, status: "failed" });
			expect(providerCalls).toBe(1);
			await expect(runtime.retireActor(STREAM)).resolves.toBe(true);
		},
	);

	it("fences deferred resumption during retirement and keeps the pending authority recoverable", async () => {
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
			id: "retirement-pending-operation",
		};
		await runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => messageResponse({ ...assistant([], "deferred"), deferred: providerHandle }),
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "resumed" }]),
				cancel: async () => undefined,
			},
		});
		await runtime.start({ ...STREAM, inputId: "deferred-input", payload: {} }).completion;
		const pending = runtime.readDeferred(STREAM);
		if (!pending) throw new Error("Expected pending deferred authority.");
		const outcomes = await Promise.allSettled([
			runtime.retireActor(STREAM),
			runtime.resumeDeferred(STREAM, pending.operationId),
		]);
		expect(outcomes).toEqual([
			{ status: "rejected", reason: expect.any(ActorRetirementBusyError) },
			{ status: "rejected", reason: expect.any(ActorRetirementInProgressError) },
		]);
		expect(runtime.readDeferred(STREAM)).toEqual(pending);
		const resumed = await runtime.resumeDeferred(STREAM, pending.operationId);
		await expect(resumed.completion).resolves.toMatchObject({ status: "completed", finalOutput: "resumed" });
		await expect(runtime.retireActor(STREAM)).resolves.toBe(true);
	});

	it("resumes an admission held behind retirement when its abort cannot become durable", async () => {
		const events = new (class extends ControlledAppendEventStore {
			private failed = false;
			override async append(...parameters: Parameters<InMemoryEventStore["append"]>) {
				if (parameters[3].type === "control.accepted" && !this.failed) {
					this.failed = true;
					throw new Error("control write unavailable");
				}
				return super.append(...parameters);
			}
		})();
		let providerCalls = 0;
		const runtime = await createRuntime(() => {
			providerCalls += 1;
			return textResponse("resumed after failed retirement");
		}, events);
		const admission = events.pauseNextAppend("run.queued");
		const run = runtime.start({ ...STREAM, inputId: "admitting", payload: {} });
		await admission.started;
		const retirement = runtime.retireActor(STREAM);
		const rejected = expect(retirement).rejects.toBeInstanceOf(AggregateError);
		admission.release();
		await rejected;
		await new Promise<void>((resolve) => setImmediate(resolve));
		expect(providerCalls).toBe(1);
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });
		await expect(runtime.retireActor(STREAM)).resolves.toBe(true);
	});

	it("retires a queued-only actor without starting its provider and fences new admission", async () => {
		const runtime = new GameRuntime(new InMemoryEventStore(), {
			maxActiveActors: 1,
			maxQueuedRuns: 4,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
		});
		const blocker = controlledResponse();
		let retiredProviderCalls = 0;
		await runtime.registerActors([
			{
				sessionId: "session",
				actorId: "blocker",
				model: TEST_MODEL,
				modelStream: blocker.streamFn,
			},
			{
				sessionId: "session",
				actorId: "retired",
				model: TEST_MODEL,
				modelStream: () => {
					retiredProviderCalls += 1;
					return textResponse("must not start");
				},
			},
		]);
		const blockingRun = runtime.start({ sessionId: "session", actorId: "blocker", inputId: "active", payload: {} });
		await blocker.started;
		const queuedOne = runtime.start({ sessionId: "session", actorId: "retired", inputId: "one", payload: {} });
		const queuedTwo = runtime.start({ sessionId: "session", actorId: "retired", inputId: "two", payload: {} });
		const retirement = runtime.retireActor({ sessionId: "session", actorId: "retired" }, "world unloaded");

		expect(runtime.retireActor({ sessionId: "session", actorId: "retired" })).toBe(retirement);
		expect(() => runtime.start({ sessionId: "session", actorId: "retired", inputId: "fenced", payload: {} })).toThrow(
			ActorRetirementInProgressError,
		);
		await expect(retirement).resolves.toBe(true);
		await expect(Promise.all([queuedOne.completion, queuedTwo.completion])).resolves.toEqual([
			{ runId: queuedOne.runId, status: "aborted" },
			{ runId: queuedTwo.runId, status: "aborted" },
		]);
		expect(retiredProviderCalls).toBe(0);
		await expect(runtime.retireActor({ sessionId: "session", actorId: "retired" })).resolves.toBe(false);
		expect(() => runtime.readActiveCoordinate({ sessionId: "session", actorId: "retired" })).toThrow(
			ActorNotRegisteredError,
		);

		await runtime.registerActor({
			sessionId: "session",
			actorId: "retired",
			model: TEST_MODEL,
			modelStream: () => textResponse("fresh generation"),
		});
		const reopened = runtime.start({ sessionId: "session", actorId: "retired", inputId: "new", payload: {} });
		blocker.finish();
		await expect(blockingRun.completion).resolves.toMatchObject({ status: "completed" });
		await expect(reopened.completion).resolves.toMatchObject({ status: "completed", finalOutput: "fresh generation" });
	});

	it("retires one active actor and all of its queued runs in one lifecycle operation", async () => {
		const activeResponse = controlledResponse();
		let providerCalls = 0;
		const runtime = await createRuntime((model, context, options) => {
			providerCalls += 1;
			return providerCalls === 1 ? activeResponse.streamFn(model, context, options) : textResponse("must not start");
		});
		const active = runtime.start({ ...STREAM, inputId: "active", payload: {} });
		await activeResponse.started;
		const queuedOne = runtime.start({ ...STREAM, inputId: "queued-one", payload: {} });
		const queuedTwo = runtime.start({ ...STREAM, inputId: "queued-two", payload: {} });

		await expect(runtime.retireActor(STREAM, "authority revoked")).resolves.toBe(true);
		await expect(Promise.all([active.completion, queuedOne.completion, queuedTwo.completion])).resolves.toEqual([
			{ runId: active.runId, status: "aborted" },
			{ runId: queuedOne.runId, status: "aborted" },
			{ runId: queuedTwo.runId, status: "aborted" },
		]);
		expect(providerCalls).toBe(1);
	});

	it("replaces an idle actor while preserving its stream subscriptions", async () => {
		let oldProviderCalls = 0;
		let newProviderCalls = 0;
		const runtime = await createRuntime(() => {
			oldProviderCalls += 1;
			return textResponse("old reply");
		});
		const observedTypes: string[] = [];
		const immediateTypes: string[] = [];
		const unsubscribe = runtime.subscribe(STREAM, (event) => {
			observedTypes.push(event.data.type);
		});
		const unsubscribeImmediate = runtime.subscribeImmediate(STREAM, (event) => {
			immediateTypes.push(event.data.type);
		});

		await runtime.replaceActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => {
				newProviderCalls += 1;
				return textResponse("new reply");
			},
		});
		const run = runtime.start({ ...STREAM, inputId: "after-replacement", payload: {} });

		await expect(run.completion).resolves.toEqual({ runId: run.runId, status: "completed", finalOutput: "new reply" });
		expect(oldProviderCalls).toBe(0);
		expect(newProviderCalls).toBe(1);
		expect(observedTypes.filter((type) => type === "run.completed")).toHaveLength(1);
		expect(immediateTypes.filter((type) => type === "run.completed")).toHaveLength(1);

		unsubscribe();
		unsubscribeImmediate();
		const observedCount = observedTypes.length;
		const immediateCount = immediateTypes.length;
		const afterUnsubscribe = runtime.start({ ...STREAM, inputId: "after-unsubscribe", payload: {} });
		await afterUnsubscribe.completion;
		expect(observedTypes).toHaveLength(observedCount);
		expect(immediateTypes).toHaveLength(immediateCount);
	});

	it("rejects new runs while an actor replacement is still restoring", async () => {
		const events = new ControlledRevisionReadEventStore();
		const runtime = await createRuntime(() => textResponse("old"), events);
		const read = events.pauseNextRead();
		const replacement = runtime.replaceActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => textResponse("new"),
		});
		await read.started;

		expect(() => runtime.start({ ...STREAM, inputId: "during-replacement", payload: {} })).toThrow(
			ActorReplacementInProgressError,
		);

		read.release();
		await expect(replacement).resolves.toBeUndefined();
		const after = runtime.start({ ...STREAM, inputId: "after-replacement", payload: {} });
		await expect(after.completion).resolves.toEqual({ runId: after.runId, status: "completed", finalOutput: "new" });
	});

	it("rejects replacement while the actor has an active run without disturbing that run", async () => {
		const activeResponse = controlledResponse();
		const runtime = await createRuntime(activeResponse.streamFn);
		const active = runtime.start({ ...STREAM, inputId: "active", payload: {} });
		await activeResponse.started;

		await expect(
			runtime.replaceActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => textResponse("replacement"),
			}),
		).rejects.toBeInstanceOf(ActorReplacementBusyError);
		activeResponse.finish();
		await expect(active.completion).resolves.toEqual({ runId: active.runId, status: "completed", finalOutput: "done" });
	});

	it("rejects replacement while a run admission is appending its canonical queued event", async () => {
		const events = new ControlledAppendEventStore();
		const runtime = await createRuntime(() => textResponse("old"), events);
		const append = events.pauseNextAppend("run.queued");
		const admitting = runtime.start({ ...STREAM, inputId: "admitting", payload: {} });
		await append.started;

		await expect(
			runtime.replaceActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => textResponse("replacement"),
			}),
		).rejects.toBeInstanceOf(ActorReplacementBusyError);

		append.release();
		await expect(admitting.completion).resolves.toEqual({
			runId: admitting.runId,
			status: "completed",
			finalOutput: "old",
		});
	});

	it("rejects replacement while a run is queued or crossing its cancelling boundary", async () => {
		const targetStream = { sessionId: STREAM.sessionId, actorId: "target" } as const;
		const blockerStream = { sessionId: STREAM.sessionId, actorId: "blocker" } as const;
		const blockerResponse = controlledResponse();
		const events = new ControlledAppendEventStore();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 2,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			newRunId: (() => {
				let runId = 0;
				return () => `lifecycle-run-${++runId}`;
			})(),
		});
		await runtime.registerActors([
			{ ...blockerStream, model: TEST_MODEL, modelStream: blockerResponse.streamFn },
			{ ...targetStream, model: TEST_MODEL, modelStream: () => textResponse("target") },
		]);
		const blocker = runtime.start({ ...blockerStream, inputId: "blocker-input", payload: {} });
		await blockerResponse.started;
		const queuedEvent = deferred();
		runtime.subscribeImmediate(targetStream, (event) => {
			if (event.data.type === "run.queued") queuedEvent.resolve();
		});
		const queued = runtime.start({ ...targetStream, inputId: "queued-input", payload: {} });
		await queuedEvent.promise;

		const replacement = {
			...targetStream,
			model: TEST_MODEL,
			modelStream: () => textResponse("replacement"),
		};
		await expect(runtime.replaceActor(replacement)).rejects.toBeInstanceOf(ActorReplacementBusyError);
		const terminalAppend = events.pauseNextAppend("run.completed");
		const cancelling = runtime.abortRun({ ...targetStream, runId: queued.runId }, "test cancellation");
		await terminalAppend.started;
		await expect(runtime.replaceActor(replacement)).rejects.toBeInstanceOf(ActorReplacementBusyError);
		terminalAppend.release();
		await expect(cancelling).resolves.toMatchObject({ accepted: true });
		await expect(queued.completion).resolves.toEqual({ runId: queued.runId, status: "aborted" });
		blockerResponse.finish();
		await blocker.completion;
	});

	it("rejects durable submissions while an actor replacement is restoring", async () => {
		const events = new ControlledRevisionReadEventStore();
		const runtime = await createRuntime(() => textResponse("old"), events);
		const read = events.pauseNextRead();
		const replacement = runtime.replaceActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => textResponse("new"),
		});
		await read.started;

		await expect(runtime.submit({ ...STREAM, inputId: "during-replacement", payload: {} })).rejects.toBeInstanceOf(
			ActorReplacementInProgressError,
		);

		read.release();
		await expect(replacement).resolves.toBeUndefined();
	});

	it("fences a durable submission that captured the actor before replacement staging", async () => {
		const events = new ControlledRevisionReadEventStore();
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
		});
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
			transcript,
		});
		await runtime.registerActor({ ...STREAM, model: TEST_MODEL, modelStream: () => textResponse("old") });
		const runRead = events.pauseNextRunRead();
		const submission = runtime.submit({ ...STREAM, inputId: "captured-before-replacement", payload: {} });
		await runRead.started;
		const revisionRead = events.pauseNextRead();
		const replacement = runtime.replaceActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => textResponse("new"),
		});
		await new Promise<void>((resolve) => setImmediate(resolve));

		runRead.release();
		const submitted = await submission;
		await expect(submitted.completion).resolves.toMatchObject({ status: "completed" });
		revisionRead.release();

		await expect(replacement).rejects.toBeInstanceOf(ActorReplacementBusyError);
	});

	it("distinguishes an actor registration in progress from a missing actor", async () => {
		const events = new ControlledRevisionReadEventStore();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
		});
		const read = events.pauseNextRead();
		const registration = runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => textResponse("registered"),
		});
		await read.started;

		await expect(
			runtime.replaceActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => textResponse("replacement"),
			}),
		).rejects.toBeInstanceOf(ActorReplacementBusyError);
		read.release();
		await expect(registration).resolves.toBeUndefined();
	});

	it("reports missing and concurrently replacing actors with distinct errors", async () => {
		const events = new ControlledRevisionReadEventStore();
		const runtime = await createRuntime(() => textResponse("old"), events);

		await expect(
			runtime.replaceActor({
				sessionId: STREAM.sessionId,
				actorId: "missing",
				model: TEST_MODEL,
				modelStream: () => textResponse("missing"),
			}),
		).rejects.toBeInstanceOf(ActorNotRegisteredError);

		const read = events.pauseNextRead();
		const replacement = runtime.replaceActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => textResponse("new"),
		});
		await read.started;
		await expect(
			runtime.replaceActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => textResponse("other"),
			}),
		).rejects.toBeInstanceOf(ActorReplacementInProgressError);
		await expect(
			runtime.registerActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => textResponse("duplicate"),
			}),
		).rejects.toBeInstanceOf(ActorAlreadyRegisteredError);
		read.release();
		await expect(replacement).resolves.toBeUndefined();
	});

	it("keeps shutdown non-idle until an in-flight replacement has rolled back", async () => {
		const events = new ControlledRevisionReadEventStore();
		const runtime = await createRuntime(() => textResponse("old"), events);
		const read = events.pauseNextRead();
		const replacement = runtime.replaceActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => textResponse("new"),
		});
		await read.started;

		runtime.stopAdmission();
		let idle = false;
		const idleBoundary = runtime.waitForIdle().then(() => {
			idle = true;
		});
		await new Promise<void>((resolve) => setImmediate(resolve));
		expect(idle).toBe(false);

		read.release();
		await expect(replacement).rejects.toBeInstanceOf(RuntimeAdmissionClosedError);
		await expect(idleBoundary).resolves.toBeUndefined();
		expect(idle).toBe(true);
	});

	it("retains the old actor when replacement staging fails", async () => {
		const stagingFailure = new Error("replacement staging failed");
		let oldProviderCalls = 0;
		const events = new ControlledRevisionReadEventStore();
		const runtime = await createRuntime(() => {
			oldProviderCalls += 1;
			return textResponse("old");
		}, events);
		events.failNextRead(stagingFailure);

		await expect(
			runtime.replaceActor({
				...STREAM,
				model: TEST_MODEL,
				modelStream: () => textResponse("new"),
			}),
		).rejects.toBe(stagingFailure);
		const run = runtime.start({ ...STREAM, inputId: "after-failed-replacement", payload: {} });
		await expect(run.completion).resolves.toEqual({ runId: run.runId, status: "completed", finalOutput: "old" });
		expect(oldProviderCalls).toBe(1);
	});

	it("preserves an actor-construction failure without cleanup masking it", async () => {
		const constructionFailure = new Error("replacement construction failed");
		let oldProviderCalls = 0;
		const runtime = await createRuntime(() => {
			oldProviderCalls += 1;
			return textResponse("old");
		});
		const replacement = {
			...STREAM,
			modelStream: () => textResponse("new"),
		} as unknown as Parameters<GameRuntime["replaceActor"]>[0];
		Object.defineProperty(replacement, "model", {
			enumerable: true,
			get: () => {
				throw constructionFailure;
			},
		});

		await expect(runtime.replaceActor(replacement)).rejects.toBe(constructionFailure);
		const run = runtime.start({ ...STREAM, inputId: "after-construction-failure", payload: {} });
		await expect(run.completion).resolves.toEqual({ runId: run.runId, status: "completed", finalOutput: "old" });
		expect(oldProviderCalls).toBe(1);
	});

	it("requires admission to stop before exposing an idle boundary", async () => {
		const runtime = await createRuntime(() => textResponse("unused"));

		await expect(runtime.waitForIdle()).rejects.toBeInstanceOf(RuntimeAdmissionOpenError);
	});

	it("stops every new admission and drains already admitted runs", async () => {
		const first = controlledResponse();
		let providerCalls = 0;
		const runtime = await createRuntime((model, context, options) => {
			providerCalls += 1;
			return providerCalls === 1 ? first.streamFn(model, context, options) : textResponse("queued");
		});
		const active = runtime.start({ ...STREAM, inputId: "active", payload: {} });
		await first.started;
		const queued = runtime.start({ ...STREAM, inputId: "queued", payload: {} });

		runtime.stopAdmission();
		runtime.stopAdmission();
		const idle = runtime.waitForIdle();
		expect(() => runtime.start({ ...STREAM, inputId: "late-start", payload: {} })).toThrow(RuntimeAdmissionClosedError);
		expect(() => runtime.submit({ ...STREAM, inputId: "late-submit", payload: {} })).toThrow(
			RuntimeAdmissionClosedError,
		);
		await expect(
			runtime.registerActor({
				sessionId: "session",
				actorId: "late-actor",
				model: TEST_MODEL,
				modelStream: () => textResponse("unused"),
			}),
		).rejects.toBeInstanceOf(RuntimeAdmissionClosedError);

		first.finish();
		await expect(Promise.all([active.completion, queued.completion])).resolves.toEqual([
			{ runId: active.runId, status: "completed", finalOutput: "done" },
			{ runId: queued.runId, status: "completed", finalOutput: "queued" },
		]);
		await expect(idle).resolves.toBeUndefined();
		await expect(runtime.waitForIdle()).resolves.toBeUndefined();
		expect(providerCalls).toBe(2);
	});

	it("aborts every active and queued run without starting a queued provider", async () => {
		const first = controlledResponse();
		let providerCalls = 0;
		const events = new InMemoryEventStore();
		const runtime = await createRuntime((model, context, options) => {
			providerCalls += 1;
			return providerCalls === 1 ? first.streamFn(model, context, options) : textResponse("must not start");
		}, events);
		const active = runtime.start({ ...STREAM, inputId: "active", payload: {} });
		await first.started;
		const queuedOne = runtime.start({ ...STREAM, inputId: "queued-one", payload: {} });
		const queuedTwo = runtime.start({ ...STREAM, inputId: "queued-two", payload: {} });

		await expect(runtime.abortAll("host shutdown")).resolves.toBeUndefined();
		await expect(Promise.all([active.completion, queuedOne.completion, queuedTwo.completion])).resolves.toEqual([
			{ runId: active.runId, status: "aborted" },
			{ runId: queuedOne.runId, status: "aborted" },
			{ runId: queuedTwo.runId, status: "aborted" },
		]);
		await expect(runtime.waitForIdle()).resolves.toBeUndefined();
		expect(providerCalls).toBe(1);
		const page = await events.read(STREAM, { afterRevision: "0", limit: 100 });
		const activeTypes = page.events
			.filter((event) => event.coordinate.runId === active.runId)
			.map((event) => event.data.type);
		expect(activeTypes[0]).toBe("run.queued");
		expect(activeTypes).toContain("control.accepted");
		expect(activeTypes.at(-1)).toBe("run.completed");
		for (const run of [queuedOne, queuedTwo]) {
			expect(
				page.events.filter((event) => event.coordinate.runId === run.runId).map((event) => event.data.type),
			).toEqual(["run.queued", "control.accepted", "run.metrics", "run.completed"]);
		}
	});

	it("continues aborting siblings and settles idle when one queued terminal write fails", async () => {
		const first = controlledResponse();
		let providerCalls = 0;
		const events = new FailingRunCompletionStore("shutdown terminal persistence failed");
		const runtime = await createRuntime((model, context, options) => {
			providerCalls += 1;
			return providerCalls === 1 ? first.streamFn(model, context, options) : textResponse("must not start");
		}, events);
		const active = runtime.start({ ...STREAM, inputId: "active", payload: {} });
		await first.started;
		const failed = runtime.start({ ...STREAM, inputId: "failed", payload: {} });
		const sibling = runtime.start({ ...STREAM, inputId: "sibling", payload: {} });
		events.blockedRunId = failed.runId;

		await expect(runtime.abortAll()).rejects.toBeInstanceOf(AggregateError);
		await expect(Promise.all([active.completion, failed.completion, sibling.completion])).resolves.toEqual([
			{ runId: active.runId, status: "aborted" },
			{ runId: failed.runId, status: "failed" },
			{ runId: sibling.runId, status: "aborted" },
		]);
		await expect(runtime.waitForIdle()).resolves.toBeUndefined();
		expect(providerCalls).toBe(1);
		const page = await events.read(STREAM, { afterRevision: "0", limit: 100 });
		expect(
			page.events.filter((event) => event.coordinate.runId === sibling.runId).map((event) => event.data.type),
		).toEqual(["run.queued", "control.accepted", "run.metrics", "run.completed"]);
	});

	it("does not report idle while a registration started before closure is still restoring", async () => {
		const restore = deferred();
		const events = new (class extends InMemoryEventStore {
			override async readRevision(...parameters: Parameters<InMemoryEventStore["readRevision"]>) {
				await restore.promise;
				return super.readRevision(...parameters);
			}
		})();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
		});
		const registration = runtime.registerActor({
			...STREAM,
			model: TEST_MODEL,
			modelStream: () => textResponse("unused"),
		});
		await Promise.resolve();

		runtime.stopAdmission();
		let idle = false;
		const idlePromise = runtime.waitForIdle().then(() => {
			idle = true;
		});
		await new Promise<void>((resolve) => setImmediate(resolve));
		expect(idle).toBe(false);

		restore.resolve();
		await expect(registration).rejects.toBeInstanceOf(RuntimeAdmissionClosedError);
		await idlePromise;
		expect(idle).toBe(true);
	});

	it("waits for every actor in a failed registration batch before reporting idle", async () => {
		const slowReadStarted = deferred();
		const releaseSlowRead = deferred();
		const registrationFailure = new Error("batch registration failed");
		const events = new (class extends InMemoryEventStore {
			override async readRevision(...parameters: Parameters<InMemoryEventStore["readRevision"]>) {
				const stream = parameters[0];
				if (stream.actorId === "failed") throw registrationFailure;
				slowReadStarted.resolve();
				await releaseSlowRead.promise;
				return super.readRevision(...parameters);
			}
		})();
		const runtime = new GameRuntime(events, {
			maxActiveActors: 1,
			maxQueuedRuns: 1,
			maxInputBytes: 4_096,
			maxInputDepth: 8,
		});
		const registration = runtime.registerActors([
			{ sessionId: "session", actorId: "failed", model: TEST_MODEL, modelStream: () => textResponse("unused") },
			{ sessionId: "session", actorId: "slow", model: TEST_MODEL, modelStream: () => textResponse("unused") },
		]);
		const registrationOutcome = registration.then(
			() => ({ error: undefined }),
			(error: unknown) => ({ error }),
		);
		await slowReadStarted.promise;

		runtime.stopAdmission();
		let idle = false;
		const idlePromise = runtime.waitForIdle().then(() => {
			idle = true;
		});
		await new Promise<void>((resolve) => setImmediate(resolve));
		expect(idle).toBe(false);

		releaseSlowRead.resolve();
		expect((await registrationOutcome).error).toBe(registrationFailure);
		await idlePromise;
		expect(idle).toBe(true);
	});

	it("does not report idle while a durable submission started before closure is still reading authority", async () => {
		const readStarted = deferred();
		const releaseRead = deferred();
		let blockNextRead = true;
		const events = new (class extends InMemoryEventStore {
			override async readRun(...parameters: Parameters<InMemoryEventStore["readRun"]>) {
				if (blockNextRead) {
					blockNextRead = false;
					readStarted.resolve();
					await releaseRead.promise;
				}
				return super.readRun(...parameters);
			}
		})();
		const runtime = await createRuntime(() => textResponse("completed"), events);
		const submission = runtime.submit({ ...STREAM, inputId: "in-flight", payload: {} });
		await readStarted.promise;

		runtime.stopAdmission();
		let idle = false;
		const idlePromise = runtime.waitForIdle().then(() => {
			idle = true;
		});
		await Promise.resolve();
		expect(idle).toBe(false);

		releaseRead.resolve();
		const handle = await submission;
		await expect(handle.completion).resolves.toEqual({
			runId: handle.runId,
			status: "completed",
			finalOutput: "completed",
		});
		await idlePromise;
		expect(idle).toBe(true);
	});
});

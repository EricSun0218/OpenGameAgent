import { createHash, randomUUID } from "node:crypto";
import { Deque } from "@datastructures-js/deque";
import type {
	DurableTranscript,
	GameAgentOptions,
	GameControlCommand,
	GameControlResult,
	GameDeferredAbandonResult,
	GameDeferredOperation,
	GameInput,
	GameRunHandle,
	GameRunResult,
	NormalizedGameInput,
	TranscriptRestore,
} from "@opengameagent/agent";
import {
	createRuntimeGameAgentActivation,
	type RuntimeGameAgentActivation,
} from "@opengameagent/agent/runtime-internal";
import {
	assertOpaqueIdentifier,
	type CanonicalEvent,
	type CanonicalEventData,
	type CanonicalEventPage,
	type CanonicalEventReplay,
	type CanonicalEventStore,
	canonicalizeJsonValue,
	captureCloseRunAuthorityRequest,
	captureRunAdmissionAuthority,
	captureRunAuthorityKey,
	type CloseRunAuthorityRequest,
	type EventReadRequest,
	type ExpectedRevision,
	JsonValueLimitError,
	normalizeJsonValueWithin,
	parseEventRevision,
	RunAuthorityClosedError,
	type RunAuthorityClosure,
	type RunAuthorityKey,
	type RunCoordinate,
	type StoredRunRecord,
	type StreamKey,
} from "@opengameagent/protocol";
import { Mutex } from "async-mutex";

export type RuntimeActorOptions = Omit<GameAgentOptions, "emitEvent" | "transcript">;

export interface GameRuntimeOptions {
	maxActiveActors: number;
	maxQueuedRuns: number;
	maxInputBytes: number;
	maxInputDepth: number;
	transcript?: DurableTranscript;
	newRunId?: () => string;
	/** Finite monotonic milliseconds used for run admission and queue performance events. */
	metricsNow?: () => number;
	/** Required for work retirement. Wait for detached producers, dispatchers and handoffs to stop executing; do not wait for fenced lease settlement. */
	drainWorkAuthority?: (request: Readonly<CloseRunAuthorityRequest>) => Promise<void>;
}

export type GameRuntimeLimits = Readonly<
	Pick<GameRuntimeOptions, "maxActiveActors" | "maxQueuedRuns" | "maxInputBytes" | "maxInputDepth">
>;

export interface RuntimeRunHandle extends GameRunHandle {
	readonly eventCursor: string;
}

export class ActorAlreadyRegisteredError extends Error {
	constructor() {
		super("The actor is already registered in this runtime.");
		this.name = "ActorAlreadyRegisteredError";
	}
}

export class ActorNotRegisteredError extends Error {
	constructor() {
		super("The actor is not registered in this runtime.");
		this.name = "ActorNotRegisteredError";
	}
}

export class ActorReplacementInProgressError extends Error {
	constructor() {
		super("The actor is being replaced in this runtime.");
		this.name = "ActorReplacementInProgressError";
	}
}

export class ActorReplacementBusyError extends Error {
	constructor() {
		super("The actor must be idle before it can be replaced.");
		this.name = "ActorReplacementBusyError";
	}
}

export class ActorRetirementInProgressError extends Error {
	constructor() {
		super("The actor is draining for retirement from this runtime.");
		this.name = "ActorRetirementInProgressError";
	}
}

export class ActorRetirementBusyError extends Error {
	constructor() {
		super("The actor cannot retire while a deferred lifecycle transition is pending.");
		this.name = "ActorRetirementBusyError";
	}
}

export class ActorRetirementPersistenceError extends Error {
	constructor() {
		super("The actor stopped locally but retained canonical run authority remains unsettled.");
		this.name = "ActorRetirementPersistenceError";
	}
}

export class RuntimeQueueFullError extends Error {
	constructor() {
		super("The runtime run queue is full.");
		this.name = "RuntimeQueueFullError";
	}
}

export class RuntimeAdmissionClosedError extends Error {
	constructor() {
		super("The runtime is not accepting new actors or runs.");
		this.name = "RuntimeAdmissionClosedError";
	}
}

export class RuntimeAdmissionOpenError extends Error {
	constructor() {
		super("Stop runtime admission before waiting for the idle boundary.");
		this.name = "RuntimeAdmissionOpenError";
	}
}

export class RunInputConflictError extends Error {
	constructor() {
		super("The input identifier is already associated with different content.");
		this.name = "RunInputConflictError";
	}
}

export class RuntimeInputLimitError extends RangeError {
	constructor(
		readonly code: "input_too_large" | "input_too_deep",
		readonly limit: number,
		options?: ErrorOptions,
	) {
		super(
			code === "input_too_large"
				? `The run payload exceeds runtime.maxInputBytes (${limit} UTF-8 bytes).`
				: `The run payload exceeds runtime.maxInputDepth (${limit}).`,
			options,
		);
		this.name = "RuntimeInputLimitError";
	}
}

export class RuntimeDeferredNotFoundError extends Error {
	constructor() {
		super("The deferred provider operation is not pending for this actor.");
		this.name = "RuntimeDeferredNotFoundError";
	}
}

export class RuntimeDeferredBusyError extends Error {
	constructor() {
		super("The actor is completing another deferred lifecycle transition.");
		this.name = "RuntimeDeferredBusyError";
	}
}

export class RuntimeDeferredPendingError extends Error {
	constructor(public readonly operationId: string) {
		super("A deferred provider operation is pending for this actor.");
		this.name = "RuntimeDeferredPendingError";
	}
}

export class RuntimeDeferredResumeUnavailableError extends Error {
	constructor() {
		super("A deferred response Adapter is not configured for this actor.");
		this.name = "RuntimeDeferredResumeUnavailableError";
	}
}

interface ScheduledRun {
	kind: "prompt" | "resume";
	runId: string;
	coordinate: RunCoordinate;
	input: NormalizedGameInput;
	inputFingerprint: string;
	state: "admitting" | "ready" | "cancelling" | "active" | "settled";
	resolve: (result: GameRunResult) => void;
	completion: Promise<GameRunResult>;
	terminalPersisted: boolean;
	metricsPersisted: boolean;
	agentStarted: boolean;
	createdAt: number;
	admittedAt?: number;
	activatedAt?: number;
	eventCursor?: string;
	operationId?: string;
}

interface RegisteredStreamState {
	eventSerial: Mutex;
	revision: ExpectedRevision;
	listeners: Map<RuntimeEventListener, Mutex>;
	immediateListeners: Set<RuntimeImmediateEventListener>;
}

interface RegisteredActor {
	key: string;
	stream: StreamKey;
	activation: RuntimeGameAgentActivation;
	events: RegisteredStreamState;
	queue: Deque<ScheduledRun>;
	runs: Map<string, ScheduledRun>;
	active: ScheduledRun | undefined;
	deferredResponsesConfigured: boolean;
	abandoningDeferredOperationId?: string;
}

interface StagedActor {
	actor: RegisteredActor;
	transcriptRestore: TranscriptRestore | undefined;
}

export type RuntimeEventListener = (event: Readonly<CanonicalEvent>) => void | Promise<void>;
export type RuntimeImmediateEventListener = (event: Readonly<CanonicalEvent>) => void;

export class GameRuntime {
	readonly limits: GameRuntimeLimits;
	private readonly actors = new Map<string, RegisteredActor>();
	private readonly registeringActors = new Set<string>();
	private readonly replacingActors = new Set<string>();
	private readonly retiringActorKeys = new Set<string>();
	private readonly actorRetirements = new Map<string, Promise<boolean>>();
	private readonly authorityClosures = new Map<string, Promise<RunAuthorityClosure>>();
	private pendingAuthorityClosures = 0;
	private readonly scheduler = new Mutex();
	private readonly readyActorKeys = new Deque<string>();
	private readonly readyActorSet = new Set<string>();
	private readonly maxActiveActors: number;
	private readonly maxQueuedRuns: number;
	private readonly maxInputBytes: number;
	private readonly maxInputDepth: number;
	private readonly newRunId: () => string;
	private readonly metricsNow: () => number;
	private readonly drainWorkAuthority: GameRuntimeOptions["drainWorkAuthority"];
	private readonly transcript: DurableTranscript | undefined;
	private acceptingAdmission = true;
	private abortingAllRuns = false;
	private idlePromise: Promise<void> | undefined;
	private resolveIdle: (() => void) | undefined;
	private abortAllPromise: Promise<void> | undefined;
	private pendingAdmissions = 0;
	private activeActors = 0;
	private queuedRuns = 0;

	constructor(
		private readonly eventStore: CanonicalEventStore,
		options: GameRuntimeOptions,
	) {
		this.maxActiveActors = options.maxActiveActors;
		this.maxQueuedRuns = options.maxQueuedRuns;
		this.maxInputBytes = options.maxInputBytes;
		this.maxInputDepth = options.maxInputDepth;
		this.transcript = options.transcript;
		assertPositiveSafeInteger("maxActiveActors", this.maxActiveActors);
		assertPositiveSafeInteger("maxQueuedRuns", this.maxQueuedRuns);
		assertPositiveSafeInteger("maxInputBytes", this.maxInputBytes);
		assertPositiveSafeInteger("maxInputDepth", this.maxInputDepth);
		this.limits = Object.freeze({
			maxActiveActors: this.maxActiveActors,
			maxQueuedRuns: this.maxQueuedRuns,
			maxInputBytes: this.maxInputBytes,
			maxInputDepth: this.maxInputDepth,
		});
		this.newRunId = options.newRunId ?? randomUUID;
		this.metricsNow = options.metricsNow ?? (() => performance.now());
		this.drainWorkAuthority = options.drainWorkAuthority;
		if (this.drainWorkAuthority !== undefined && typeof this.drainWorkAuthority !== "function")
			throw new TypeError("drainWorkAuthority must be a function.");
	}

	stopAdmission(): void {
		if (!this.acceptingAdmission) return;
		this.acceptingAdmission = false;
		if (this.isIdle()) {
			this.idlePromise = Promise.resolve();
			return;
		}
		this.idlePromise = new Promise<void>((resolve) => {
			this.resolveIdle = resolve;
		});
	}

	waitForIdle(): Promise<void> {
		if (this.acceptingAdmission) return Promise.reject(new RuntimeAdmissionOpenError());
		return this.idlePromise ?? Promise.resolve();
	}

	abortAll(reason?: unknown): Promise<void> {
		this.stopAdmission();
		if (this.abortAllPromise) return this.abortAllPromise;
		this.abortingAllRuns = true;
		this.abortAllPromise = this.abortEveryRun(reason);
		return this.abortAllPromise;
	}

	async registerActor(options: RuntimeActorOptions): Promise<void> {
		await this.registerActors([options]);
	}

	async replaceActor(options: RuntimeActorOptions): Promise<void> {
		this.assertAcceptingAdmission();
		const stream = { sessionId: options.sessionId, actorId: options.actorId };
		assertOpaqueIdentifier(stream.sessionId);
		assertOpaqueIdentifier(stream.actorId);
		const key = actorMapKey(stream);
		if (this.retiringActorKeys.has(key)) throw new ActorRetirementInProgressError();
		const current = this.actors.get(key);
		if (!current) {
			if (this.registeringActors.has(key)) throw new ActorReplacementBusyError();
			throw new ActorNotRegisteredError();
		}
		if (this.replacingActors.has(key)) throw new ActorReplacementInProgressError();
		if (!this.isActorIdle(current)) throw new ActorReplacementBusyError();
		this.replacingActors.add(key);
		let replacement: RegisteredActor | undefined;
		let committed = false;
		try {
			await this.scheduler.runExclusive(() => {
				this.assertCurrentActorReplaceable(key, current);
			});
			const staged = await this.stageActor({ options, stream, key }, current, false);
			replacement = staged.actor;
			if (staged.actor.activation.agent.readPendingDeferred()) throw new ActorReplacementBusyError();
			await this.scheduler.runExclusive(async () => {
				this.assertCurrentActorReplaceable(key, current);
				if (staged.transcriptRestore) {
					await this.reconcileDeferredProjection(staged.actor, staged.transcriptRestore);
				}
				this.assertCurrentActorReplaceable(key, current);
				current.activation.release();
				this.actors.set(key, staged.actor);
				committed = true;
			});
		} finally {
			if (!committed) replacement?.activation.release();
			this.replacingActors.delete(key);
			this.notifyIdle();
		}
	}

	async registerActors(options: readonly RuntimeActorOptions[]): Promise<void> {
		this.assertAcceptingAdmission();
		const requested = options.map((actorOptions) => {
			const stream = { sessionId: actorOptions.sessionId, actorId: actorOptions.actorId };
			assertOpaqueIdentifier(stream.sessionId);
			assertOpaqueIdentifier(stream.actorId);
			return { options: actorOptions, stream, key: actorMapKey(stream) };
		});
		const keys = new Set<string>();
		for (const actor of requested) {
			if (
				keys.has(actor.key) ||
				this.actors.has(actor.key) ||
				this.registeringActors.has(actor.key) ||
				this.retiringActorKeys.has(actor.key)
			) {
				throw new ActorAlreadyRegisteredError();
			}
			keys.add(actor.key);
		}
		for (const key of keys) this.registeringActors.add(key);
		let staged: StagedActor[] | undefined;
		let committed = false;
		try {
			const staging = requested.map((request) => this.stageActor(request));
			try {
				staged = await Promise.all(staging);
			} catch (error) {
				const outcomes = await Promise.allSettled(staging);
				for (const outcome of outcomes) {
					if (outcome.status === "fulfilled") outcome.value.actor.activation.release();
				}
				throw error;
			}
			this.assertAcceptingAdmission();
			for (const { actor } of staged) this.actors.set(actor.key, actor);
			committed = true;
		} finally {
			if (!committed && staged) {
				for (const { actor } of staged) actor.activation.release();
			}
			for (const key of keys) this.registeringActors.delete(key);
			this.notifyIdle();
		}
	}

	retireActor(stream: StreamKey, reason?: unknown): Promise<boolean> {
		stream = { sessionId: stream.sessionId, actorId: stream.actorId };
		assertOpaqueIdentifier(stream.sessionId);
		assertOpaqueIdentifier(stream.actorId);
		const key = actorMapKey(stream);
		const existing = this.actorRetirements.get(key);
		if (existing) return existing;
		if (this.registeringActors.has(key) || this.replacingActors.has(key)) {
			return Promise.reject(new ActorRetirementBusyError());
		}
		const actor = this.actors.get(key);
		if (!actor) return Promise.resolve(false);
		this.retiringActorKeys.add(key);
		let tracked: Promise<boolean>;
		tracked = this.retireRegisteredActor(actor, reason).finally(() =>
			this.scheduler.runExclusive(() => {
				if (this.actorRetirements.get(key) === tracked) this.actorRetirements.delete(key);
				this.retiringActorKeys.delete(key);
				if (this.actors.get(key) === actor) this.markReady(actor);
				this.pump();
				this.notifyIdle();
			}),
		);
		this.actorRetirements.set(key, tracked);
		return tracked;
	}

	closeAuthority(request: CloseRunAuthorityRequest): Promise<RunAuthorityClosure> {
		const captured = captureCloseRunAuthorityRequest(request);
		this.pendingAuthorityClosures += 1;
		return this.closeRunAuthority(captured).finally(() => {
			this.pendingAuthorityClosures -= 1;
			this.notifyIdle();
		});
	}

	readAuthorityClosure(key: RunAuthorityKey): Promise<RunAuthorityClosure | undefined> {
		return this.eventStore.readAuthorityClosure(captureRunAuthorityKey(key));
	}

	private async closeRunAuthority(request: CloseRunAuthorityRequest): Promise<RunAuthorityClosure> {
		if (request.workRetirement !== undefined && this.drainWorkAuthority === undefined) {
			throw new TypeError("Work retirement requires an explicit drainWorkAuthority host binding.");
		}
		const closure = await this.eventStore.closeAuthority(request);
		const key = canonicalizeJsonValue(captureRunAuthorityKey(closure));
		const current = this.authorityClosures.get(key);
		if (current) return current;
		if (closure.workRetirementResult?.phase === "finalized") return closure;
		let tracked: Promise<RunAuthorityClosure>;
		tracked = this.drainClosedAuthority(closure)
			.then(async () => {
				if (request.workRetirement !== undefined) await this.drainWorkAuthority?.(request);
				return this.eventStore.finalizeAuthorityClosure(request);
			})
			.finally(() => {
				if (this.authorityClosures.get(key) === tracked) this.authorityClosures.delete(key);
			});
		this.authorityClosures.set(key, tracked);
		return tracked;
	}

	start(input: GameInput): GameRunHandle {
		this.assertAcceptingAdmission();
		const normalizedInput = this.normalizeInput(input);
		this.assertAcceptingAdmission();
		const actor = this.actorForAdmission(normalizedInput);
		const pending = actor.activation.agent.readPendingDeferred();
		if (pending) throw new RuntimeDeferredPendingError(pending.operationId);
		if (this.queuedRuns >= this.maxQueuedRuns) {
			throw new RuntimeQueueFullError();
		}
		const run = this.createRun(actor, normalizedInput, fingerprintInput(normalizedInput));
		void this.scheduler.runExclusive(() => this.admit(actor, run)).catch(() => undefined);
		return runHandle(run);
	}

	submit(input: GameInput): Promise<RuntimeRunHandle> {
		this.assertAcceptingAdmission();
		let normalizedInput: NormalizedGameInput;
		try {
			normalizedInput = this.normalizeInput(input);
		} catch (error) {
			return Promise.reject(error);
		}
		return this.runDurableAdmission(async () => {
			const actor = this.actorForAdmission(normalizedInput);
			const inputFingerprint = fingerprintInput(normalizedInput);
			await this.assertInputAuthorityOpen(normalizedInput);
			for (const run of actor.runs.values()) {
				if (run.input.inputId !== normalizedInput.inputId) continue;
				if (run.inputFingerprint !== inputFingerprint) throw new RunInputConflictError();
				return admittedRunHandle(run);
			}
			const pending = actor.activation.agent.readPendingDeferred();
			if (pending) {
				if (pending.input.inputId !== normalizedInput.inputId) {
					throw new RuntimeDeferredPendingError(pending.operationId);
				}
				if (fingerprintInput(pending.input) !== inputFingerprint) {
					throw new RunInputConflictError();
				}
				const stored = await this.eventStore.readRun(actor.stream, normalizedInput.inputId);
				if (!stored || stored.initialCoordinate.runId !== pending.coordinate.runId) {
					throw new TypeError("The deferred transcript does not match its canonical run admission.");
				}
				return settledRunHandle(pending.coordinate, stored.eventCursor, {
					runId: pending.coordinate.runId,
					status: "deferred",
					operationId: pending.operationId,
				});
			}

			let stored = await this.eventStore.readRun(actor.stream, normalizedInput.inputId);
			if (stored && stored.inputFingerprint !== inputFingerprint) throw new RunInputConflictError();
			if (stored && (stored.status === null || stored.status === "deferred") && this.transcript) {
				if (actor.runs.size > 0 || actor.abandoningDeferredOperationId) throw new RuntimeDeferredBusyError();
				await this.reconcileDeferredProjection(actor, await this.transcript.restore(actor.stream));
				stored = await this.eventStore.readRun(actor.stream, normalizedInput.inputId);
			}
			if (stored) {
				if (stored.status) {
					const result: GameRunResult =
						stored.status === "deferred"
							? { runId: stored.initialCoordinate.runId, status: "deferred", operationId: stored.operationId }
							: stored.status === "completed"
								? {
										runId: stored.initialCoordinate.runId,
										status: stored.status,
										...(stored.finalOutput === undefined ? {} : { finalOutput: stored.finalOutput }),
									}
								: { runId: stored.initialCoordinate.runId, status: stored.status };
					return settledRunHandle(stored.initialCoordinate, stored.eventCursor, result);
				}

				await this.appendEvent(actor, stored.latestCoordinate, { type: "run.completed", status: "failed" });
				return settledRunHandle(stored.initialCoordinate, stored.eventCursor, {
					runId: stored.initialCoordinate.runId,
					status: "failed",
				});
			}

			if (this.queuedRuns >= this.maxQueuedRuns) throw new RuntimeQueueFullError();
			const run = this.createRun(actor, normalizedInput, inputFingerprint, true);
			await this.admit(actor, run);
			return admittedRunHandle(run);
		});
	}

	readDeferred(stream: StreamKey): GameDeferredOperation | undefined {
		return this.actor(stream).activation.agent.readPendingDeferred();
	}

	resumeDeferred(stream: StreamKey, operationId: string): Promise<RuntimeRunHandle> {
		stream = { sessionId: stream.sessionId, actorId: stream.actorId };
		return this.runDurableAdmission(async () => {
			assertOpaqueIdentifier(operationId);
			const actor = this.actorForAdmission(stream);
			let pending = actor.activation.agent.readPendingDeferred();
			if (!pending || pending.operationId !== operationId) throw new RuntimeDeferredNotFoundError();
			await this.assertInputAuthorityOpen({ ...actor.stream, ...pending.input });
			await this.eventStore.assertRunAuthorityOpen(pending.coordinate);
			if (actor.abandoningDeferredOperationId === operationId) throw new RuntimeDeferredBusyError();
			const existing = actor.runs.get(pending.coordinate.runId);
			if (existing) {
				if (existing.operationId !== operationId) throw new RuntimeDeferredBusyError();
				return admittedRunHandle(existing);
			}
			if (!actor.deferredResponsesConfigured) throw new RuntimeDeferredResumeUnavailableError();

			const stored = await this.reconcilePendingDeferredProjection(actor, pending);
			pending = actor.activation.agent.readPendingDeferred();
			if (actor.abandoningDeferredOperationId === operationId) throw new RuntimeDeferredBusyError();
			if (!pending || pending.operationId !== operationId) throw new RuntimeDeferredNotFoundError();
			const input: NormalizedGameInput = {
				...actor.stream,
				inputId: pending.input.inputId,
				payload: structuredClone(pending.input.payload),
				...(pending.input.admissionAuthority === undefined
					? {}
					: {
							admissionAuthority: captureRunAdmissionAuthority(pending.input.admissionAuthority),
						}),
			};
			const run = this.createResumeRun(actor, input, fingerprintInput(input), pending, stored.eventCursor);
			actor.queue.pushFront(run);
			this.markReady(actor);
			this.pump();
			return admittedRunHandle(run);
		});
	}

	async abandonDeferred(stream: StreamKey, operationId: string): Promise<GameDeferredAbandonResult> {
		return this.abandonDeferredAtBoundary(stream, operationId);
	}

	private async abandonDeferredAtBoundary(
		stream: StreamKey,
		operationId: string,
		afterLocalAbandon?: () => void,
	): Promise<GameDeferredAbandonResult> {
		assertOpaqueIdentifier(operationId);
		const actor = this.actor(stream);
		const current = actor.activation.agent.readPendingDeferred();
		if (!current || current.operationId !== operationId) {
			throw new RuntimeDeferredNotFoundError();
		}
		if (actor.abandoningDeferredOperationId) throw new RuntimeDeferredBusyError();
		actor.abandoningDeferredOperationId = operationId;
		let scheduled: ScheduledRun | undefined;
		try {
			await this.scheduler.runExclusive(() => {
				const pending = actor.activation.agent.readPendingDeferred();
				if (!pending || pending.operationId !== operationId) {
					throw new RuntimeDeferredNotFoundError();
				}
				scheduled = actor.runs.get(pending.coordinate.runId);
				if (scheduled?.state === "active") throw new RuntimeDeferredBusyError();
				if (scheduled) {
					this.removeQueuedRun(actor, scheduled);
					scheduled.state = "cancelling";
				}
			});
			return await actor.activation.agent.abandonDeferred(operationId, () =>
				this.scheduler.runExclusive(() => {
					if (scheduled) this.settle(actor, scheduled, { runId: scheduled.runId, status: "aborted" });
					if (actor.abandoningDeferredOperationId === operationId) delete actor.abandoningDeferredOperationId;
					this.markReady(actor);
					this.pump();
					afterLocalAbandon?.();
				}),
			);
		} finally {
			if (scheduled?.state === "cancelling") {
				await this.scheduler.runExclusive(() => {
					if (scheduled) this.settle(actor, scheduled, { runId: scheduled.runId, status: "failed" });
				});
			}
			if (actor.abandoningDeferredOperationId === operationId) delete actor.abandoningDeferredOperationId;
			if (!actor.activation.agent.readPendingDeferred()) {
				await this.scheduler.runExclusive(() => {
					this.markReady(actor);
					this.pump();
				});
			}
		}
	}

	control(expected: RunCoordinate, command: GameControlCommand): Promise<GameControlResult> {
		expected = { sessionId: expected.sessionId, actorId: expected.actorId, runId: expected.runId, turn: expected.turn };
		const type = command.type;
		command = type === "abort" ? { type, reason: command.reason } : { type, text: command.text };
		return this.scheduler.runExclusive(async () => {
			const actor = this.actor(expected);
			const run = actor.runs.get(expected.runId);
			if (!run || run.state === "settled") {
				return { accepted: false, reason: "no-active-run" };
			}
			if (run.state === "active") {
				return actor.activation.agent.control(expected, command);
			}
			if (!sameCoordinate(expected, run.coordinate)) {
				return {
					accepted: false,
					reason: "coordinate-mismatch",
					activeCoordinate: run.coordinate,
				};
			}
			if (command.type !== "abort") {
				return {
					accepted: false,
					reason: command.text.length === 0 ? "invalid-message" : "run-not-active",
					activeCoordinate: run.coordinate,
				};
			}

			return this.abortQueuedRun(actor, run);
		});
	}

	abortRun(
		expected: Readonly<Pick<RunCoordinate, "sessionId" | "actorId" | "runId">>,
		reason?: unknown,
	): Promise<GameControlResult> {
		expected = { sessionId: expected.sessionId, actorId: expected.actorId, runId: expected.runId };
		assertOpaqueIdentifier(expected.runId);
		return this.scheduler.runExclusive(async () => {
			const actor = this.actor(expected);
			const run = actor.runs.get(expected.runId);
			if (!run || run.state === "settled") return { accepted: false, reason: "no-active-run" };
			if (run.state === "active") return actor.activation.agent.abortRun(expected, reason);

			return this.abortQueuedRun(actor, run);
		});
	}

	private async abortQueuedRun(actor: RegisteredActor, run: ScheduledRun): Promise<GameControlResult> {
		await this.appendEvent(actor, run.coordinate, { type: "control.accepted", control: "abort" });
		this.removeQueuedRun(actor, run);
		run.state = "cancelling";
		try {
			await this.appendEvent(actor, run.coordinate, { type: "run.completed", status: "aborted" });
		} catch (error) {
			this.settle(actor, run, { runId: run.runId, status: "failed" });
			this.markReady(actor);
			this.pump();
			throw error;
		}
		this.settle(actor, run, { runId: run.runId, status: "aborted" });
		this.markReady(actor);
		this.pump();
		return { accepted: true, coordinate: run.coordinate };
	}

	readActiveCoordinate(stream: StreamKey): RunCoordinate | undefined {
		return this.actor(stream).activation.agent.readActiveCoordinate();
	}

	readEvents(stream: StreamKey, request: EventReadRequest): Promise<CanonicalEventPage> {
		return this.eventStore.read(stream, request);
	}

	async replayEvents(stream: StreamKey, request: EventReadRequest): Promise<CanonicalEventReplay> {
		const actor = this.actor(stream);
		const requestedAfterRevision = request.afterRevision ?? "0";
		const capturedRequest = { afterRevision: requestedAfterRevision, limit: request.limit };
		const requested = parseEventRevision(requestedAfterRevision);
		const bounds = await this.eventStore.readBounds(actor.stream);
		const latest = parseEventRevision(bounds.latestRevision);
		const earliest = bounds.earliestRevision === null ? null : parseEventRevision(bounds.earliestRevision);
		if (requested > latest || (earliest === null ? requested < latest : requested + 1n < earliest)) {
			return {
				kind: "gap",
				requestedAfterRevision,
				earliestAvailableRevision: bounds.earliestRevision,
				nextAfterRevision: bounds.latestRevision,
				requiresTranscriptReconciliation: true,
			};
		}
		const page = await this.eventStore.read(actor.stream, capturedRequest);
		return {
			kind: "page",
			requestedAfterRevision,
			requiresTranscriptReconciliation: false,
			...page,
		};
	}

	subscribe(stream: StreamKey, listener: RuntimeEventListener): () => void {
		const actor = this.actor(stream);
		if (!actor.events.listeners.has(listener)) actor.events.listeners.set(listener, new Mutex());
		return () => actor.events.listeners.delete(listener);
	}

	/**
	 * Subscribe a synchronous projection that must finish before the canonical append returns.
	 * Use ordinary `subscribe()` for I/O or any asynchronous listener.
	 */
	subscribeImmediate(stream: StreamKey, listener: RuntimeImmediateEventListener): () => void {
		const actor = this.actor(stream);
		actor.events.immediateListeners.add(listener);
		return () => actor.events.immediateListeners.delete(listener);
	}

	private async reconcileDeferredProjection(actor: RegisteredActor, restore: TranscriptRestore): Promise<void> {
		for (const terminal of restore.terminalDeferredRuns) {
			const stored = await this.eventStore.readRun(actor.stream, terminal.inputId);
			if (!stored || stored.initialCoordinate.runId !== terminal.coordinate.runId) {
				throw new TypeError("The deferred transcript does not match its canonical run admission.");
			}
			if (stored.status === terminal.status) continue;
			if (stored.status !== null && stored.status !== "deferred") {
				throw new TypeError("The closed deferred transcript conflicts with its canonical run projection.");
			}
			await this.appendEvent(actor, terminal.coordinate, { type: "run.completed", status: terminal.status });
		}
		if (restore.pendingDeferred) await this.reconcilePendingDeferredProjection(actor, restore.pendingDeferred);
	}

	private async stageActor(
		request: Readonly<{ options: RuntimeActorOptions; stream: StreamKey; key: string }>,
		existing?: RegisteredActor,
		reconcileDeferred = true,
	): Promise<StagedActor> {
		const transcriptRestore = this.transcript ? await this.transcript.restore(request.stream) : undefined;
		const revision = await this.eventStore.readRevision(request.stream);
		if (existing && revision !== existing.events.revision) {
			throw new TypeError("The registered actor revision does not match canonical event authority.");
		}
		const events: RegisteredStreamState = existing?.events ?? {
			eventSerial: new Mutex(),
			revision,
			listeners: new Map(),
			immediateListeners: new Set(),
		};
		let staged: RegisteredActor | undefined;
		const activation = createRuntimeGameAgentActivation(
			{
				...request.options,
				sessionId: request.stream.sessionId,
				actorId: request.stream.actorId,
				beforeToolExecution: async (input, execution, signal) => {
					await this.eventStore.assertRunAuthorityOpen(execution.coordinate);
					await request.options.beforeToolExecution?.(input, execution, signal);
					await this.eventStore.assertRunAuthorityOpen(execution.coordinate);
				},
				emitEvent: (coordinate, data) => {
					if (!staged) return Promise.reject(new TypeError("The actor activation is not initialized."));
					return this.appendEvent(staged, coordinate, data);
				},
				...(transcriptRestore && this.transcript
					? { transcript: { transcript: this.transcript, restore: transcriptRestore } }
					: {}),
			},
			{ assertRunAuthorityOpen: (coordinate) => this.eventStore.assertRunAuthorityOpen(coordinate) },
		);
		staged = {
			key: request.key,
			stream: request.stream,
			activation,
			events,
			queue: new Deque<ScheduledRun>(),
			runs: new Map(),
			active: undefined,
			deferredResponsesConfigured: request.options.deferredResponses !== undefined,
		};
		try {
			if (reconcileDeferred && transcriptRestore) await this.reconcileDeferredProjection(staged, transcriptRestore);
			return { actor: staged, transcriptRestore };
		} catch (error) {
			activation.release();
			throw error;
		}
	}

	private async reconcilePendingDeferredProjection(
		actor: RegisteredActor,
		pending: Readonly<GameDeferredOperation>,
	): Promise<StoredRunRecord> {
		const stored = await this.eventStore.readRun(actor.stream, pending.input.inputId);
		if (!stored || stored.initialCoordinate.runId !== pending.coordinate.runId) {
			throw new TypeError("The deferred transcript does not match its canonical run admission.");
		}
		if (stored.status === "deferred" && stored.operationId === pending.operationId) return stored;
		if (stored.status !== null) {
			throw new TypeError("The pending deferred transcript conflicts with its canonical run projection.");
		}
		await this.appendEvent(actor, pending.coordinate, {
			type: "run.suspended",
			operationId: pending.operationId,
		});
		return stored;
	}

	private async admit(actor: RegisteredActor, run: ScheduledRun): Promise<void> {
		if (run.state !== "admitting") return;
		try {
			const admission = await this.appendEvent(actor, run.coordinate, {
				type: "run.queued",
				inputId: run.input.inputId,
				inputFingerprint: run.inputFingerprint,
				...(run.input.admissionAuthority === undefined ? {} : { admissionAuthority: run.input.admissionAuthority }),
			});
			run.eventCursor = (parseEventRevision(admission.revision) - 1n).toString();
			run.state = "ready";
			actor.queue.pushBack(run);
			this.markReady(actor);
			this.pump();
		} catch (error) {
			this.settle(actor, run, { runId: run.runId, status: "failed" });
			throw error;
		}
	}

	private markReady(actor: RegisteredActor): void {
		if (
			this.retiringActorKeys.has(actor.key) ||
			actor.active ||
			actor.queue.size() === 0 ||
			this.readyActorSet.has(actor.key)
		) {
			return;
		}
		const pending = actor.activation.agent.readPendingDeferred();
		if (pending && actor.queue.front()?.operationId !== pending.operationId) return;
		this.readyActorSet.add(actor.key);
		this.readyActorKeys.pushBack(actor.key);
	}

	private pump(): void {
		if (this.abortingAllRuns) return;
		while (this.activeActors < this.maxActiveActors) {
			const key = this.readyActorKeys.popFront();
			if (!key) return;
			this.readyActorSet.delete(key);
			const actor = this.actors.get(key);
			if (!actor || actor.active || this.retiringActorKeys.has(key)) continue;
			const run = actor.queue.popFront();
			if (run?.state !== "ready") {
				this.markReady(actor);
				continue;
			}

			if (run.kind === "prompt") this.queuedRuns -= 1;
			run.state = "active";
			actor.active = run;
			this.activeActors += 1;
			try {
				run.activatedAt = this.sampleMetrics(run.admittedAt ?? run.createdAt);
				const handle = run.operationId
					? actor.activation.agent.resumeDeferred(run.operationId)
					: actor.activation.agent.start(run.input, run.runId);
				run.agentStarted = true;
				void handle.completion.then((result) => this.scheduler.runExclusive(() => this.finish(actor, run, result)));
			} catch {
				const pending = actor.activation.agent.readPendingDeferred();
				const result: GameRunResult =
					pending && pending.operationId === run.operationId
						? { runId: run.runId, status: "deferred", operationId: pending.operationId }
						: { runId: run.runId, status: "failed" };
				void this.finishStartFailure(actor, run, result);
			}
		}
	}

	private async finishStartFailure(actor: RegisteredActor, run: ScheduledRun, result: GameRunResult): Promise<void> {
		if (result.status !== "deferred") {
			try {
				await this.appendEvent(actor, run.coordinate, { type: "run.completed", status: "failed" });
			} catch {
				// The run still has to release local capacity when the authoritative sink is unavailable.
			}
		}
		await this.scheduler.runExclusive(() => this.finish(actor, run, result));
	}

	private finish(actor: RegisteredActor, run: ScheduledRun, result: GameRunResult): void {
		if (actor.active !== run || run.state !== "active") return;
		actor.active = undefined;
		this.activeActors -= 1;
		this.settle(actor, run, result);
		this.markReady(actor);
		this.pump();
	}

	private settle(actor: RegisteredActor, run: ScheduledRun, result: GameRunResult): void {
		if (run.state === "settled") return;
		if (run.kind === "prompt" && (run.state === "admitting" || run.state === "ready" || run.state === "cancelling")) {
			this.queuedRuns -= 1;
		}
		run.state = "settled";
		actor.runs.delete(run.runId);
		run.resolve(result);
		this.notifyIdle();
	}

	private removeQueuedRun(actor: RegisteredActor, run: ScheduledRun): void {
		const queued = actor.queue.toArray();
		const index = queued.indexOf(run);
		if (index < 0) return;
		actor.queue.clear();
		for (const [queuedIndex, queuedRun] of queued.entries()) {
			if (queuedIndex !== index) actor.queue.pushBack(queuedRun);
		}
	}

	private appendEvent(
		actor: RegisteredActor,
		coordinate: RunCoordinate,
		data: CanonicalEventData,
	): Promise<CanonicalEvent> {
		return actor.events.eventSerial.runExclusive(async () => {
			const run = actor.runs.get(coordinate.runId);
			if (data.type === "run.completed" && run && !run.metricsPersisted) {
				if (run.agentStarted) throw new TypeError("The run terminal event requires persisted run metrics.");
				await this.appendCanonicalEvent(actor, coordinate, {
					type: "run.metrics",
					unit: "microseconds",
					segmentStatus: data.status,
					durations: { contextAndRecall: "0", provider: "0", durable: "0", native: "0", total: "0" },
					counts: { providerAttempts: "0", retries: "0", fallbacks: "0", toolCalls: "0" },
				});
			}
			return this.appendCanonicalEvent(actor, coordinate, data);
		});
	}

	private async appendCanonicalEvent(
		actor: RegisteredActor,
		coordinate: RunCoordinate,
		data: CanonicalEventData,
	): Promise<CanonicalEvent> {
		const run = actor.runs.get(coordinate.runId);
		const eventData = data.type === "run.metrics" && run ? this.captureRunMetrics(run, data) : data;
		const appended = await this.eventStore.append(actor.stream, actor.events.revision, coordinate, eventData);
		const event = immutableCanonicalEvent(appended);
		actor.events.revision = event.revision;
		if (event.data.type === "run.queued" && run) run.admittedAt = this.sampleMetrics(run.createdAt);
		if (event.data.type === "run.metrics" && run) run.metricsPersisted = true;
		if (event.data.type === "run.completed") {
			const run = actor.runs.get(event.coordinate.runId);
			if (run) run.terminalPersisted = true;
		}
		for (const listener of actor.events.immediateListeners) {
			try {
				void Promise.resolve(listener(event)).catch(() => undefined);
			} catch {
				// Observers cannot replace canonical event authority.
			}
		}
		for (const [listener, boundary] of actor.events.listeners) {
			void boundary.runExclusive(() => listener(event)).catch(() => undefined);
		}
		return event;
	}

	private actor(stream: StreamKey): RegisteredActor {
		const actor = this.actors.get(actorMapKey(stream));
		if (!actor) {
			throw new ActorNotRegisteredError();
		}
		return actor;
	}

	private actorForAdmission(stream: StreamKey): RegisteredActor {
		const actor = this.actor(stream);
		if (this.retiringActorKeys.has(actor.key)) throw new ActorRetirementInProgressError();
		if (this.replacingActors.has(actor.key)) throw new ActorReplacementInProgressError();
		return actor;
	}

	private createRun(
		actor: RegisteredActor,
		input: NormalizedGameInput,
		inputFingerprint: string,
		admissionReserved = false,
	): ScheduledRun {
		const runId = this.newRunId();
		assertOpaqueIdentifier(runId);
		const createdAt = this.sampleMetrics();
		if (!admissionReserved) {
			this.assertAcceptingAdmission();
			if (this.actorForAdmission(input) !== actor) throw new ActorReplacementBusyError();
		}
		const pending = actor.activation.agent.readPendingDeferred();
		if (pending) throw new RuntimeDeferredPendingError(pending.operationId);
		if (this.queuedRuns >= this.maxQueuedRuns) throw new RuntimeQueueFullError();
		if (actor.runs.has(runId)) {
			throw new TypeError("The run identifier is already in use for this actor.");
		}

		let resolveCompletion: ((result: GameRunResult) => void) | undefined;
		const completion = new Promise<GameRunResult>((resolve) => {
			resolveCompletion = resolve;
		});
		const coordinate: RunCoordinate = { ...actor.stream, runId, turn: "0" };
		const run: ScheduledRun = {
			kind: "prompt",
			runId,
			coordinate,
			input,
			inputFingerprint,
			state: "admitting",
			resolve: (result) => resolveCompletion?.(result),
			completion,
			terminalPersisted: false,
			metricsPersisted: false,
			agentStarted: false,
			createdAt,
		};
		actor.runs.set(runId, run);
		this.queuedRuns += 1;
		return run;
	}

	private createResumeRun(
		actor: RegisteredActor,
		input: NormalizedGameInput,
		inputFingerprint: string,
		pending: GameDeferredOperation,
		eventCursor: string,
	): ScheduledRun {
		const createdAt = this.sampleMetrics();
		let resolveCompletion: ((result: GameRunResult) => void) | undefined;
		const completion = new Promise<GameRunResult>((resolve) => {
			resolveCompletion = resolve;
		});
		const run: ScheduledRun = {
			kind: "resume",
			runId: pending.coordinate.runId,
			coordinate: structuredClone(pending.coordinate),
			input,
			inputFingerprint,
			state: "ready",
			resolve: (result) => resolveCompletion?.(result),
			completion,
			terminalPersisted: false,
			metricsPersisted: false,
			agentStarted: false,
			createdAt,
			admittedAt: createdAt,
			eventCursor,
			operationId: pending.operationId,
		};
		actor.runs.set(run.runId, run);
		return run;
	}

	private normalizeInput(input: GameInput): NormalizedGameInput {
		const sessionId = input.sessionId;
		const actorId = input.actorId;
		const inputId = input.inputId;
		const authority = input.admissionAuthority;
		assertOpaqueIdentifier(sessionId);
		assertOpaqueIdentifier(actorId);
		assertOpaqueIdentifier(inputId);
		const admissionAuthority = authority === undefined ? undefined : captureRunAdmissionAuthority(authority);
		const rawPayload = input.payload;
		let payload: NormalizedGameInput["payload"];
		try {
			payload = normalizeJsonValueWithin(rawPayload, {
				maxBytes: this.maxInputBytes,
				maxDepth: this.maxInputDepth,
			});
		} catch (error) {
			if (error instanceof JsonValueLimitError) {
				const bytesExceeded = error.code === "json_too_large";
				throw new RuntimeInputLimitError(
					bytesExceeded ? "input_too_large" : "input_too_deep",
					bytesExceeded ? this.maxInputBytes : this.maxInputDepth,
					{ cause: error },
				);
			}
			throw error;
		}
		return {
			sessionId,
			actorId,
			inputId,
			payload,
			...(admissionAuthority === undefined ? {} : { admissionAuthority }),
		};
	}

	private async assertInputAuthorityOpen(
		input: Pick<NormalizedGameInput, "sessionId" | "actorId" | "admissionAuthority">,
	): Promise<void> {
		if (input.admissionAuthority === undefined) return;
		const key = { sessionId: input.sessionId, actorId: input.actorId, ...input.admissionAuthority };
		if (await this.eventStore.readAuthorityClosure(key)) throw new RunAuthorityClosedError(key);
	}

	private sampleMetrics(previous?: number): number {
		const sampled = this.metricsNow();
		if (!Number.isFinite(sampled) || (previous !== undefined && sampled < previous)) {
			throw new TypeError("The runtime metrics clock must return finite monotonic milliseconds.");
		}
		return sampled;
	}

	private captureRunMetrics(
		run: ScheduledRun,
		data: Extract<CanonicalEventData, { type: "run.metrics" }>,
	): Extract<CanonicalEventData, { type: "run.metrics" }> {
		const queuedUntil = run.activatedAt ?? this.sampleMetrics(run.admittedAt ?? run.createdAt);
		const admittedAt = run.admittedAt ?? queuedUntil;
		const admission = metricMicroseconds(admittedAt - run.createdAt);
		const queue = metricMicroseconds(queuedUntil - admittedAt);
		const waiting = admission + queue;
		return {
			...data,
			durations: {
				...data.durations,
				admission: admission.toString(),
				queue: queue.toString(),
				total: (BigInt(data.durations.total) + waiting).toString(),
				native: (BigInt(data.durations.native) + waiting).toString(),
				...(data.durations.firstResponse === undefined
					? {}
					: {
							firstResponse: (BigInt(data.durations.firstResponse) + waiting).toString(),
						}),
				...(data.durations.firstTool === undefined
					? {}
					: {
							firstTool: (BigInt(data.durations.firstTool) + waiting).toString(),
						}),
			},
		};
	}

	private assertAcceptingAdmission(): void {
		if (!this.acceptingAdmission) throw new RuntimeAdmissionClosedError();
	}

	private assertCurrentActorReplaceable(key: string, current: RegisteredActor): void {
		this.assertAcceptingAdmission();
		if (this.actors.get(key) !== current || !this.isActorIdle(current)) {
			throw new ActorReplacementBusyError();
		}
	}

	private isIdle(): boolean {
		if (
			this.registeringActors.size > 0 ||
			this.replacingActors.size > 0 ||
			this.retiringActorKeys.size > 0 ||
			this.pendingAdmissions > 0 ||
			this.pendingAuthorityClosures > 0
		) {
			return false;
		}
		for (const actor of this.actors.values()) {
			if (actor.runs.size > 0) return false;
		}
		return true;
	}

	private isActorIdle(actor: RegisteredActor): boolean {
		return (
			actor.runs.size === 0 &&
			actor.queue.size() === 0 &&
			actor.active === undefined &&
			actor.abandoningDeferredOperationId === undefined &&
			!actor.activation.agent.isRunning &&
			actor.activation.agent.readPendingDeferred() === undefined
		);
	}

	private notifyIdle(): void {
		if (this.acceptingAdmission || !this.resolveIdle || !this.isIdle()) return;
		const resolve = this.resolveIdle;
		this.resolveIdle = undefined;
		resolve();
	}

	private runDurableAdmission<T>(operation: () => Promise<T>): Promise<T> {
		this.assertAcceptingAdmission();
		this.pendingAdmissions += 1;
		return this.scheduler
			.runExclusive(async () => {
				this.assertAcceptingAdmission();
				return operation();
			})
			.finally(() => {
				this.pendingAdmissions -= 1;
				this.notifyIdle();
			});
	}

	private async retireRegisteredActor(actor: RegisteredActor, reason: unknown): Promise<boolean> {
		const completions: Promise<GameRunResult>[] = [];
		let runs: ScheduledRun[] = [];
		const failures: unknown[] = [];
		await this.scheduler.runExclusive(async () => {
			if (this.actors.get(actor.key) !== actor) return;
			if (actor.abandoningDeferredOperationId || actor.activation.agent.readPendingDeferred()) {
				throw new ActorRetirementBusyError();
			}
			runs = [...actor.runs.values()];
			for (const run of runs) {
				try {
					if (run.state === "active") {
						await actor.activation.agent.abortRun({ ...actor.stream, runId: run.runId }, reason);
					} else if (run.state !== "settled") {
						await this.abortQueuedRun(actor, run);
					}
					completions.push(run.completion);
				} catch (error) {
					failures.push(error);
					if (run.state === "settled") completions.push(run.completion);
				}
			}
		});
		await Promise.all(completions);
		return await this.scheduler.runExclusive(() => {
			if (this.actors.get(actor.key) !== actor) return false;
			if (!this.isActorIdle(actor)) {
				if (failures.length > 0) throw new AggregateError(failures, "One or more actor runs could not be aborted.");
				throw new ActorRetirementBusyError();
			}
			if (runs.some((run) => !run.terminalPersisted)) failures.push(new ActorRetirementPersistenceError());
			this.actors.delete(actor.key);
			this.readyActorSet.delete(actor.key);
			actor.events.listeners.clear();
			actor.events.immediateListeners.clear();
			actor.activation.release();
			this.pump();
			if (failures.length > 0) {
				throw new AggregateError(failures, "The actor was released with unsettled retained canonical authority.");
			}
			return true;
		});
	}

	private async drainClosedAuthority(closure: RunAuthorityClosure): Promise<void> {
		const completions: Promise<GameRunResult>[] = [];
		const failures: unknown[] = [];
		let actor: RegisteredActor | undefined;
		let runs: ScheduledRun[] = [];
		await this.scheduler.runExclusive(async () => {
			actor = this.actors.get(actorMapKey(closure));
			if (!actor) return;
			runs = [...actor.runs.values()].filter((run) => matchesAuthority(run.input, closure));
			for (const run of runs) {
				if (run.kind === "resume" && run.state !== "active") continue;
				try {
					if (run.state === "active") {
						await actor.activation.agent.abortRun({ ...actor.stream, runId: run.runId });
					} else if (run.state !== "settled") {
						await this.abortQueuedRun(actor, run);
					}
					completions.push(run.completion);
				} catch (error) {
					failures.push(error);
					if (run.state === "settled") completions.push(run.completion);
				}
			}
		});
		await Promise.all(completions);
		if (actor && this.actors.get(actor.key) === actor) {
			const pending = actor.activation.agent.readPendingDeferred();
			if (pending && matchesAuthority({ ...actor.stream, ...pending.input }, closure)) {
				try {
					const local = Promise.withResolvers<void>();
					void this.abandonDeferredAtBoundary(actor.stream, pending.operationId, local.resolve).then(
						() => local.resolve(),
						local.reject,
					);
					await local.promise;
				} catch (error) {
					failures.push(error);
				}
			}
		}
		for (const run of runs) {
			if (!run.terminalPersisted) {
				try {
					const stored = await this.eventStore.readRun(closure, run.input.inputId);
					if (
						stored?.initialCoordinate.runId === run.runId &&
						(stored.status === "aborted" || stored.status === "completed" || stored.status === "failed")
					)
						continue;
				} catch (error) {
					failures.push(error);
				}
				failures.push(new TypeError("A scoped run has no persisted canonical terminal event."));
			}
		}
		if (failures.length > 0) {
			throw new AggregateError(
				failures,
				"The authority is durably closed but one or more local runs remain unsettled.",
			);
		}
	}

	private async abortEveryRun(reason: unknown): Promise<void> {
		const failures: unknown[] = [];
		await this.scheduler.runExclusive(async () => {
			for (const actor of this.actors.values()) {
				for (const run of [...actor.runs.values()]) {
					try {
						if (run.state === "active") {
							await actor.activation.agent.abortRun(
								{ sessionId: actor.stream.sessionId, actorId: actor.stream.actorId, runId: run.runId },
								reason,
							);
						} else if (run.state !== "settled") {
							await this.abortQueuedRun(actor, run);
						}
					} catch (error) {
						failures.push(error);
					}
				}
			}
		});
		if (failures.length > 0) throw new AggregateError(failures, "One or more runtime runs could not be aborted.");
		await this.waitForIdle();
	}
}

function immutableCanonicalEvent(event: Readonly<CanonicalEvent>): Readonly<CanonicalEvent> {
	const snapshot = structuredClone(event);
	const pending: object[] = [snapshot];
	const seen = new Set<object>();
	while (pending.length > 0) {
		const current = pending.pop();
		if (!current || seen.has(current)) continue;
		seen.add(current);
		for (const value of Object.values(current)) {
			if (value && typeof value === "object") pending.push(value);
		}
		Object.freeze(current);
	}
	return snapshot;
}

function fingerprintInput(input: Pick<NormalizedGameInput, "payload" | "admissionAuthority">): string {
	return createHash("sha256")
		.update(
			canonicalizeJsonValue({
				payload: input.payload,
				admissionAuthority: input.admissionAuthority ?? null,
			}),
		)
		.digest("hex");
}

function matchesAuthority(
	input: Pick<NormalizedGameInput, "sessionId" | "actorId" | "admissionAuthority">,
	key: RunAuthorityKey,
): boolean {
	return (
		input.sessionId === key.sessionId &&
		input.actorId === key.actorId &&
		input.admissionAuthority?.scope === key.scope &&
		input.admissionAuthority.generation === key.generation
	);
}

function metricMicroseconds(milliseconds: number): bigint {
	const value = Math.round(milliseconds * 1_000);
	if (!Number.isSafeInteger(value) || value < 0)
		throw new RangeError("The runtime metric exceeds the safe duration range.");
	return BigInt(value);
}

function runHandle(run: ScheduledRun): GameRunHandle {
	return { runId: run.runId, initialCoordinate: structuredClone(run.coordinate), completion: run.completion };
}

function admittedRunHandle(run: ScheduledRun): RuntimeRunHandle {
	if (run.eventCursor === undefined) throw new TypeError("The run has no durable admission cursor.");
	return { ...runHandle(run), eventCursor: run.eventCursor };
}

function settledRunHandle(
	initialCoordinate: RunCoordinate,
	eventCursor: string,
	result: GameRunResult,
): RuntimeRunHandle {
	return {
		runId: initialCoordinate.runId,
		initialCoordinate: structuredClone(initialCoordinate),
		eventCursor,
		completion: Promise.resolve(result),
	};
}

function actorMapKey(stream: StreamKey): string {
	return JSON.stringify([stream.sessionId, stream.actorId]);
}

function sameCoordinate(left: RunCoordinate, right: RunCoordinate): boolean {
	return (
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId &&
		left.runId === right.runId &&
		left.turn === right.turn
	);
}

function assertPositiveSafeInteger(name: string, value: number): void {
	if (!Number.isSafeInteger(value) || value <= 0) {
		throw new TypeError(`${name} must be a positive safe integer.`);
	}
}

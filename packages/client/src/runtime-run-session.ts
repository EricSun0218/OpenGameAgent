import { createHash } from "node:crypto";
import { assertActionApprovalRecord, assertActionDelivery } from "@opengameagent/actions/runtime-validation";
import {
	assertCanonicalEvent,
	assertOpaqueIdentifier,
	assertRuntimeActionSseFrame,
	assertRuntimeApprovalDecisionResponse,
	assertRuntimeApprovalWaitResponse,
	assertRuntimeControlResult,
	assertRuntimeRunAccepted,
	assertRuntimeSseFrame,
	canonicalizeJsonValue,
	raceOperationWithAbort,
	type CanonicalEvent,
	type RunCoordinate,
	type StreamKey,
} from "@opengameagent/protocol";
import type { ServerSentEventsResult } from "./generated/client/index.js";
import type {
	RuntimeActionDelivery,
	RuntimeActionNextRequest,
	RuntimeActionSseFrame,
	RuntimeApprovalDecisionRequest,
	RuntimeApprovalDecisionResponse,
	RuntimeApprovalListRequest,
	RuntimeApprovalListResponse,
	RuntimeApprovalWaitRequest,
	RuntimeApprovalWaitResponse,
	RuntimeControlRequest,
	RuntimeControlResult,
	RuntimeRunAccepted,
	RuntimeRunRequest,
	RuntimeSseFrame,
} from "./generated/types.gen.js";

const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;
const SESSION_SETTLED = Symbol("Runtime run session settled");

export interface RuntimeSseRetryPolicy {
	readonly defaultDelayMs: number;
	readonly maxDelayMs: number;
	readonly maxAttempts: number;
}

export type RuntimeRunApproval = RuntimeApprovalListResponse["approvals"][number];
export interface RuntimeRunSessionAccepted {
	readonly runId: string;
	readonly initialCoordinate: Readonly<RunCoordinate>;
	readonly eventCursor: string;
}
export type RuntimeRunTerminalEvent = CanonicalEvent & {
	readonly data: { readonly type: "run.completed"; readonly status: "completed" | "aborted" | "failed" };
};

export interface RuntimeRunSessionOptions {
	readonly eventRetry: RuntimeSseRetryPolicy;
	readonly onEvent?: {
		readonly timeoutMs: number;
		readonly handle: (event: CanonicalEvent, signal: AbortSignal) => void | Promise<void>;
	};
	readonly actions?: {
		readonly retry: RuntimeSseRetryPolicy;
		readonly timeoutMs: number;
		readonly onDelivery: (delivery: RuntimeActionDelivery, signal: AbortSignal) => void | Promise<void>;
	};
	readonly approvals?: {
		readonly waitDurationMs: number;
		readonly pageSize: number;
		readonly timeoutMs: number;
		readonly onPending: (approval: RuntimeRunApproval, signal: AbortSignal) => void | Promise<void>;
	};
	readonly signal?: AbortSignal;
}

export interface RuntimeRunApprovalDecision {
	readonly approvalId: string;
	readonly decision: "approve" | "reject";
	readonly reason?: string;
	readonly signal?: AbortSignal;
}

export class RuntimeRunSessionClosedError extends Error {
	constructor() {
		super("The local Runtime run session was closed without changing the durable run.");
		this.name = "RuntimeRunSessionClosedError";
	}
}

export class RuntimeRunSessionGapError extends Error {
	constructor(public readonly gap: Extract<RuntimeSseFrame, { type: "gap" }>["gap"]) {
		super("The Runtime run session requires transcript reconciliation before it can continue.");
		this.name = "RuntimeRunSessionGapError";
	}
}

export class RuntimeRunSessionIdentityError extends Error {
	constructor() {
		super("The Runtime run session received data for a different durable run identity.");
		this.name = "RuntimeRunSessionIdentityError";
	}
}

export class RuntimeRunSessionApprovalNotFoundError extends Error {
	constructor() {
		super("The approval is not pending in this exact Runtime run session.");
		this.name = "RuntimeRunSessionApprovalNotFoundError";
	}
}

export class RuntimeRunSessionStreamEndedError extends Error {
	constructor(public readonly stream: "events" | "actions") {
		super(`The Runtime ${stream} stream ended before the exact run reached a terminal event.`);
		this.name = "RuntimeRunSessionStreamEndedError";
	}
}

export class RuntimeRunSessionHandlerTimeoutError extends Error {
	constructor(public readonly handler: "event" | "action" | "approval") {
		super(`The Runtime run session ${handler} handler exceeded its configured deadline.`);
		this.name = "RuntimeRunSessionHandlerTimeoutError";
	}
}

interface RuntimeRunSessionTransport {
	streamEvents(
		query: { sessionId: string; actorId: string; afterRevision?: string },
		retry: RuntimeSseRetryPolicy,
		signal?: AbortSignal,
	): Promise<ServerSentEventsResult<{ 200: RuntimeSseFrame }>>;
	streamActions(
		body: RuntimeActionNextRequest,
		retry: RuntimeSseRetryPolicy,
		signal?: AbortSignal,
	): Promise<ServerSentEventsResult<{ 200: RuntimeActionSseFrame }>>;
	listApprovals(
		body: RuntimeApprovalListRequest,
		signal?: AbortSignal,
	): Promise<{ readonly data: RuntimeApprovalListResponse }>;
	waitApprovals(
		body: RuntimeApprovalWaitRequest,
		signal?: AbortSignal,
	): Promise<{ readonly data: RuntimeApprovalWaitResponse }>;
	decideApproval(
		body: RuntimeApprovalDecisionRequest,
		signal?: AbortSignal,
	): Promise<{ readonly data: RuntimeApprovalDecisionResponse }>;
	controlRun(body: RuntimeControlRequest, signal?: AbortSignal): Promise<{ readonly data: RuntimeControlResult }>;
}

interface PreparedRuntimeRunSessionOptions {
	readonly eventRetry: Readonly<RuntimeSseRetryPolicy>;
	readonly onEvent?: RuntimeRunSessionOptions["onEvent"];
	readonly actions?: Readonly<NonNullable<RuntimeRunSessionOptions["actions"]>>;
	readonly approvals?: Readonly<NonNullable<RuntimeRunSessionOptions["approvals"]>>;
	readonly signal?: AbortSignal;
}

export interface RuntimeRunSession {
	readonly accepted: RuntimeRunSessionAccepted;
	readonly completion: Promise<RuntimeRunTerminalEvent>;
	close(reason?: unknown): void;
	abort(signal?: AbortSignal): Promise<RuntimeControlResult>;
	decideApproval(input: RuntimeRunApprovalDecision): Promise<RuntimeRunApproval>;
}

class ActiveRuntimeRunSession implements RuntimeRunSession {
	readonly accepted: RuntimeRunSessionAccepted;
	readonly completion: Promise<RuntimeRunTerminalEvent>;
	private readonly lifetime = new AbortController();
	private readonly pendingApprovals = new Map<string, RuntimeRunApproval>();
	private approvalMutation = 0;
	private approvalMutationSignal = new AbortController();
	private latestCoordinate: RunCoordinate;
	private removeCallerAbort: (() => void) | undefined;
	private readonly admissionRevision: string;
	private readonly expectedInputId: string;
	private readonly expectedInputFingerprint: string;
	private readonly expectedAdmissionAuthority: string;
	private readonly activation = Promise.withResolvers<void>();

	constructor(
		private readonly transport: RuntimeRunSessionTransport,
		accepted: RuntimeRunAccepted,
		expectedRequest: RuntimeRunRequest,
		private readonly options: PreparedRuntimeRunSessionOptions,
	) {
		assertAcceptedIdentity(accepted, expectedRequest);
		const initialCoordinate = Object.freeze(structuredClone(accepted.initialCoordinate));
		this.accepted = Object.freeze({
			runId: accepted.runId,
			initialCoordinate,
			eventCursor: accepted.eventCursor,
		});
		this.latestCoordinate = structuredClone(initialCoordinate);
		this.admissionRevision = (BigInt(accepted.eventCursor) + 1n).toString();
		this.expectedInputId = expectedRequest.inputId;
		this.expectedAdmissionAuthority = canonicalizeJsonValue(expectedRequest.admissionAuthority ?? null);
		this.expectedInputFingerprint = createHash("sha256")
			.update(
				canonicalizeJsonValue({
					payload: expectedRequest.payload,
					admissionAuthority: expectedRequest.admissionAuthority ?? null,
				}),
			)
			.digest("hex");
		const callerSignal = options.signal;
		if (callerSignal?.aborted === true) {
			this.lifetime.abort(callerSignal.reason);
		} else if (callerSignal !== undefined) {
			const onAbort = () => this.lifetime.abort(callerSignal.reason);
			callerSignal.addEventListener("abort", onAbort, { once: true });
			this.removeCallerAbort = () => callerSignal.removeEventListener("abort", onAbort);
		}
		this.completion = this.observe().finally(() => {
			this.removeCallerAbort?.();
			this.removeCallerAbort = undefined;
		});
	}

	close(reason: unknown = new RuntimeRunSessionClosedError()): void {
		if (!this.lifetime.signal.aborted) this.lifetime.abort(reason);
	}

	async abort(signal?: AbortSignal): Promise<RuntimeControlResult> {
		signal?.throwIfAborted();
		const coordinate = structuredClone(this.latestCoordinate);
		const result = await runWithOptionalAbort(
			() => this.transport.controlRun({ coordinate, command: { type: "abort" } }, signal),
			signal,
		);
		return this.captureControlResult(result.data, coordinate);
	}

	async decideApproval(input: RuntimeRunApprovalDecision): Promise<RuntimeRunApproval> {
		const pending = this.pendingApprovals.get(input.approvalId);
		if (pending === undefined) throw new RuntimeRunSessionApprovalNotFoundError();
		input.signal?.throwIfAborted();
		const result = await runWithOptionalAbort(
			() =>
				this.transport.decideApproval(
					{
						sessionId: this.accepted.initialCoordinate.sessionId,
						actorId: this.accepted.initialCoordinate.actorId,
						approvalId: pending.request.approvalId,
						expectedRevision: pending.revision,
						requestFingerprint: pending.request.requestFingerprint,
						decision: input.decision,
						...(input.reason === undefined ? {} : { reason: input.reason }),
					},
					input.signal,
				),
			input.signal,
		);
		assertTrusted(result.data, assertRuntimeApprovalDecisionResponse);
		const decided = result.data.approval;
		assertTrusted(decided, assertActionApprovalRecord);
		this.assertApprovalIdentity(decided);
		if (
			canonicalizeJsonValue(decided.request) !== canonicalizeJsonValue(pending.request) ||
			decided.revision !== (BigInt(pending.revision) + 1n).toString() ||
			(decided.status !== "expired" && decided.status !== (input.decision === "approve" ? "approved" : "rejected")) ||
			(decided.status === "expired" ? decided.decision !== undefined : decided.decision?.reason !== input.reason)
		) {
			throw new RuntimeRunSessionIdentityError();
		}
		this.pendingApprovals.delete(input.approvalId);
		this.approvalMutation += 1;
		this.approvalMutationSignal.abort();
		this.approvalMutationSignal = new AbortController();
		return structuredClone(decided);
	}

	private async observe(): Promise<RuntimeRunTerminalEvent> {
		const tasks: Promise<RuntimeRunTerminalEvent>[] = [this.observeEvents()];
		if (this.options.actions !== undefined) {
			tasks.push(this.observeActions(this.options.actions));
		}
		if (this.options.approvals !== undefined) {
			tasks.push(this.observeApprovals(this.options.approvals));
		}
		try {
			return await Promise.race(tasks);
		} finally {
			if (!this.lifetime.signal.aborted) this.lifetime.abort(SESSION_SETTLED);
			await Promise.allSettled(tasks);
		}
	}

	private async observeEvents(): Promise<RuntimeRunTerminalEvent> {
		const signal = this.lifetime.signal;
		let admissionObserved = false;
		try {
			signal.throwIfAborted();
			const result = await runWithOptionalAbort(
				() =>
					this.transport.streamEvents(
						{
							sessionId: this.accepted.initialCoordinate.sessionId,
							actorId: this.accepted.initialCoordinate.actorId,
							afterRevision: this.accepted.eventCursor,
						},
						this.options.eventRetry,
						signal,
					),
				signal,
			);
			const frames = result.stream[Symbol.asyncIterator]();
			while (true) {
				const next = await raceOperationWithAbort(() => frames.next(), signal);
				if (next.done) break;
				const frame = next.value;
				assertTrusted(frame, assertRuntimeSseFrame);
				signal.throwIfAborted();
				if (frame.type === "gap") throw new RuntimeRunSessionGapError(frame.gap);
				if (frame.type !== "event") continue;
				assertTrusted(frame.event, assertCanonicalEvent);
				if (!admissionObserved) {
					this.assertAdmissionEvent(frame.event);
					admissionObserved = true;
				}
				this.assertStreamCoordinate(frame.event.coordinate);
				if (frame.event.coordinate.runId !== this.accepted.runId) continue;
				this.latestCoordinate = structuredClone(frame.event.coordinate);
				const event = structuredClone(frame.event) as CanonicalEvent;
				if (event.data.type === "run.started") {
					if (event.data.inputId !== this.expectedInputId) throw new RuntimeRunSessionIdentityError();
					this.activation.resolve();
				}
				const observer = this.options.onEvent;
				if (observer !== undefined) {
					await awaitWithTimeout(
						() => observer.handle(structuredClone(event), signal),
						observer.timeoutMs,
						signal,
						new RuntimeRunSessionHandlerTimeoutError("event"),
					);
				}
				if (event.data.type === "run.completed") return event as RuntimeRunTerminalEvent;
			}
			signal.throwIfAborted();
			throw new RuntimeRunSessionStreamEndedError("events");
		} catch (error) {
			if (signal.aborted) throw signal.reason;
			throw error;
		}
	}

	private async observeActions(
		options: NonNullable<PreparedRuntimeRunSessionOptions["actions"]>,
	): Promise<RuntimeRunTerminalEvent> {
		const signal = this.lifetime.signal;
		try {
			await raceOperationWithAbort(() => this.activation.promise, signal);
			const result = await runWithOptionalAbort(
				() =>
					this.transport.streamActions(
						{
							sessionId: this.accepted.initialCoordinate.sessionId,
							actorId: this.accepted.initialCoordinate.actorId,
							runId: this.accepted.runId,
						},
						options.retry,
						signal,
					),
				signal,
			);
			const frames = result.stream[Symbol.asyncIterator]();
			while (true) {
				const next = await raceOperationWithAbort(() => frames.next(), signal);
				if (next.done) break;
				const frame = next.value;
				assertTrusted(frame, assertRuntimeActionSseFrame);
				assertTrusted(frame.delivery, assertActionDelivery);
				this.assertCoordinate(frame.delivery.action.coordinate);
				await awaitWithTimeout(
					() => options.onDelivery(structuredClone(frame.delivery), signal),
					options.timeoutMs,
					signal,
					new RuntimeRunSessionHandlerTimeoutError("action"),
				);
			}
			signal.throwIfAborted();
			throw new RuntimeRunSessionStreamEndedError("actions");
		} catch (error) {
			if (signal.aborted) throw signal.reason;
			throw error;
		}
	}

	private async observeApprovals(
		options: NonNullable<PreparedRuntimeRunSessionOptions["approvals"]>,
	): Promise<RuntimeRunTerminalEvent> {
		const signal = this.lifetime.signal;
		try {
			await raceOperationWithAbort(() => this.activation.promise, signal);
			let cursor: string | undefined;
			poll: while (true) {
				signal.throwIfAborted();
				const approvalMutation = this.approvalMutation;
				const mutationSignal = this.approvalMutationSignal.signal;
				const requestSignal = AbortSignal.any([signal, mutationSignal]);
				let result: { readonly data: RuntimeApprovalWaitResponse };
				try {
					result = await runWithOptionalAbort(
						() =>
							this.transport.waitApprovals(
								{
									sessionId: this.accepted.initialCoordinate.sessionId,
									actorId: this.accepted.initialCoordinate.actorId,
									runId: this.accepted.runId,
									limit: options.pageSize,
									waitMilliseconds: options.waitDurationMs,
									...(cursor === undefined ? {} : { afterCursor: cursor }),
								},
								requestSignal,
							),
						requestSignal,
					);
				} catch (error) {
					if (mutationSignal.aborted && !signal.aborted) continue;
					throw error;
				}
				assertTrusted(result.data, assertRuntimeApprovalWaitResponse);
				if (approvalMutation !== this.approvalMutation) {
					continue;
				}
				cursor = result.data.cursor;
				if (result.data.approvals.length > options.pageSize) throw new RuntimeRunSessionIdentityError();
				const previous = new Map(this.pendingApprovals);
				const current = new Map<string, RuntimeRunApproval>();
				for (const approval of result.data.approvals) {
					assertTrusted(approval, assertActionApprovalRecord);
					this.assertApprovalIdentity(approval);
					if (approval.status !== "pending" || current.has(approval.request.approvalId)) {
						throw new RuntimeRunSessionIdentityError();
					}
					const captured = structuredClone(approval);
					current.set(captured.request.approvalId, captured);
				}
				this.pendingApprovals.clear();
				for (const [approvalId, approval] of current) this.pendingApprovals.set(approvalId, approval);
				for (const [approvalId, approval] of current) {
					const prior = previous.get(approvalId);
					if (
						prior?.revision === approval.revision &&
						prior.request.requestFingerprint === approval.request.requestFingerprint
					) {
						continue;
					}
					await awaitWithTimeout(
						() => options.onPending(structuredClone(approval), signal),
						options.timeoutMs,
						signal,
						new RuntimeRunSessionHandlerTimeoutError("approval"),
					);
					if (approvalMutation !== this.approvalMutation) {
						continue poll;
					}
				}
			}
		} catch (error) {
			if (signal.aborted) throw signal.reason;
			throw error;
		}
	}

	private assertCoordinate(coordinate: RunCoordinate): void {
		if (
			coordinate.sessionId !== this.accepted.initialCoordinate.sessionId ||
			coordinate.actorId !== this.accepted.initialCoordinate.actorId ||
			coordinate.runId !== this.accepted.runId
		) {
			throw new RuntimeRunSessionIdentityError();
		}
	}

	private assertAdmissionEvent(event: CanonicalEvent): void {
		this.assertExactCoordinate(event.coordinate, this.accepted.initialCoordinate);
		if (
			event.revision !== this.admissionRevision ||
			event.data.type !== "run.queued" ||
			event.data.inputId !== this.expectedInputId ||
			event.data.inputFingerprint !== this.expectedInputFingerprint ||
			canonicalizeJsonValue(event.data.admissionAuthority ?? null) !== this.expectedAdmissionAuthority
		) {
			throw new RuntimeRunSessionIdentityError();
		}
	}

	private assertStreamCoordinate(coordinate: RunCoordinate): void {
		if (
			coordinate.sessionId !== this.accepted.initialCoordinate.sessionId ||
			coordinate.actorId !== this.accepted.initialCoordinate.actorId
		) {
			throw new RuntimeRunSessionIdentityError();
		}
	}

	private assertApprovalIdentity(approval: RuntimeRunApproval): void {
		this.assertCoordinate(approval.request.coordinate);
	}

	private assertExactCoordinate(coordinate: RunCoordinate, expected: RunCoordinate): void {
		this.assertCoordinate(coordinate);
		if (coordinate.turn !== expected.turn) throw new RuntimeRunSessionIdentityError();
	}

	private captureControlResult(result: unknown, expected: RunCoordinate): RuntimeControlResult {
		assertTrusted(result, assertRuntimeControlResult);
		if (result.accepted) {
			this.assertExactCoordinate(result.coordinate, expected);
		} else if (result.reason === "no-active-run") {
			if (result.activeCoordinate !== undefined) throw new RuntimeRunSessionIdentityError();
		} else if (result.reason === "run-not-active") {
			if (result.activeCoordinate === undefined) throw new RuntimeRunSessionIdentityError();
			this.assertExactCoordinate(result.activeCoordinate, expected);
		} else if (result.reason === "coordinate-mismatch") {
			if (result.activeCoordinate === undefined) throw new RuntimeRunSessionIdentityError();
			this.assertCoordinate(result.activeCoordinate);
			if (result.activeCoordinate.turn === expected.turn) throw new RuntimeRunSessionIdentityError();
		} else {
			throw new RuntimeRunSessionIdentityError();
		}
		return structuredClone(result);
	}
}

export function createRuntimeRunSession(
	transport: RuntimeRunSessionTransport,
	accepted: RuntimeRunAccepted,
	expectedRequest: RuntimeRunRequest,
	options: PreparedRuntimeRunSessionOptions,
): RuntimeRunSession {
	return new ActiveRuntimeRunSession(transport, accepted, expectedRequest, options);
}

export function prepareRuntimeRunSessionOptions(options: RuntimeRunSessionOptions): PreparedRuntimeRunSessionOptions {
	if (options === undefined || options === null) throw new TypeError("Runtime run session options are required.");
	const eventRetry = prepareRuntimeSseRetryPolicy(options.eventRetry);
	const onEvent = options.onEvent;
	let preparedOnEvent: PreparedRuntimeRunSessionOptions["onEvent"];
	if (onEvent !== undefined) {
		if (onEvent === null || typeof onEvent !== "object" || typeof onEvent.handle !== "function") {
			throw new TypeError("onEvent must define a handler and its deadline.");
		}
		assertTimerDelay(onEvent.timeoutMs, "onEvent.timeoutMs");
		preparedOnEvent = Object.freeze({ timeoutMs: onEvent.timeoutMs, handle: onEvent.handle });
	}
	const actions = options.actions;
	let preparedActions: PreparedRuntimeRunSessionOptions["actions"];
	if (actions !== undefined) {
		const onDelivery = actions.onDelivery;
		if (typeof onDelivery !== "function") throw new TypeError("actions.onDelivery must be a function.");
		assertTimerDelay(actions.timeoutMs, "actions.timeoutMs");
		preparedActions = Object.freeze({
			retry: prepareRuntimeSseRetryPolicy(actions.retry),
			timeoutMs: actions.timeoutMs,
			onDelivery,
		});
	}
	const approvals = options.approvals;
	let preparedApprovals: PreparedRuntimeRunSessionOptions["approvals"];
	if (approvals !== undefined) {
		const waitDurationMs = approvals.waitDurationMs;
		const pageSize = approvals.pageSize;
		const onPending = approvals.onPending;
		assertTimerDelay(waitDurationMs, "approvals.waitDurationMs");
		assertPositiveSafeInteger(pageSize, "approvals.pageSize");
		if (typeof onPending !== "function") throw new TypeError("approvals.onPending must be a function.");
		assertTimerDelay(approvals.timeoutMs, "approvals.timeoutMs");
		preparedApprovals = Object.freeze({
			waitDurationMs,
			pageSize,
			timeoutMs: approvals.timeoutMs,
			onPending,
		});
	}
	const signal = options.signal;
	return Object.freeze({
		eventRetry,
		...(preparedOnEvent === undefined ? {} : { onEvent: preparedOnEvent }),
		...(preparedActions === undefined ? {} : { actions: preparedActions }),
		...(preparedApprovals === undefined ? {} : { approvals: preparedApprovals }),
		...(signal === undefined ? {} : { signal }),
	});
}

export function prepareRuntimeSseRetryPolicy(policy: RuntimeSseRetryPolicy): Readonly<RuntimeSseRetryPolicy> {
	if (policy === undefined || policy === null) throw new TypeError("An SSE retry policy is required.");
	const defaultDelayMs = policy.defaultDelayMs;
	const maxDelayMs = policy.maxDelayMs;
	const maxAttempts = policy.maxAttempts;
	assertPositiveSafeInteger(defaultDelayMs, "defaultDelayMs");
	assertPositiveSafeInteger(maxDelayMs, "maxDelayMs");
	assertPositiveSafeInteger(maxAttempts, "maxAttempts");
	if (defaultDelayMs > NODE_MAX_TIMER_DELAY_MS || maxDelayMs > NODE_MAX_TIMER_DELAY_MS) {
		throw new RangeError(`SSE retry delays must not exceed ${NODE_MAX_TIMER_DELAY_MS} milliseconds.`);
	}
	if (maxDelayMs < defaultDelayMs) {
		throw new RangeError("maxDelayMs must be greater than or equal to defaultDelayMs.");
	}
	return Object.freeze({ defaultDelayMs, maxDelayMs, maxAttempts });
}

function runWithOptionalAbort<T>(operation: () => PromiseLike<T> | T, signal?: AbortSignal): Promise<T> {
	return signal === undefined ? Promise.resolve().then(operation) : raceOperationWithAbort(operation, signal);
}

function awaitWithTimeout<T>(
	operation: () => PromiseLike<T> | T,
	milliseconds: number,
	signal: AbortSignal,
	error: Error,
): Promise<T> {
	return new Promise<T>((resolve, reject) => {
		let settled = false;
		const finish = (settle: () => void) => {
			if (settled) return;
			settled = true;
			clearTimeout(timeout);
			settle();
		};
		const timeout = setTimeout(() => finish(() => reject(error)), milliseconds);
		raceOperationWithAbort(operation, signal).then(
			(value) => finish(() => resolve(value)),
			(reason: unknown) => finish(() => reject(reason)),
		);
	});
}

function assertAcceptedIdentity(accepted: unknown, expectedStream: StreamKey): asserts accepted is RuntimeRunAccepted {
	assertTrusted(accepted, assertRuntimeRunAccepted);
	const coordinate = accepted.initialCoordinate;
	try {
		assertOpaqueIdentifier(expectedStream.sessionId);
		assertOpaqueIdentifier(expectedStream.actorId);
	} catch {
		throw new RuntimeRunSessionIdentityError();
	}
	if (
		accepted.runId !== coordinate.runId ||
		coordinate.sessionId !== expectedStream.sessionId ||
		coordinate.actorId !== expectedStream.actorId
	) {
		throw new RuntimeRunSessionIdentityError();
	}
}

function assertTrusted<T>(
	value: unknown,
	assertion: (candidate: unknown) => asserts candidate is T,
): asserts value is T {
	try {
		assertion(value);
	} catch {
		throw new RuntimeRunSessionIdentityError();
	}
}

function assertPositiveSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer.`);
}

function assertTimerDelay(value: number, name: string): void {
	assertPositiveSafeInteger(value, name);
	if (value > NODE_MAX_TIMER_DELAY_MS) throw new RangeError(`${name} must not exceed ${NODE_MAX_TIMER_DELAY_MS}.`);
}

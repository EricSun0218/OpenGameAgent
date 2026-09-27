import { createHash } from "node:crypto";
import {
	assertOpaqueIdentifier,
	assertEventStreamCoordinate,
	canonicalizeJsonValue,
	captureRetainedIdentifiers,
	type JsonValue,
	type JsonValueLimits,
	normalizeJsonValueWithin,
	parseEventRevision,
	type RunCoordinate,
	type RetentionProtectionBudget,
	type StreamKey,
} from "@opengameagent/protocol";
import { Mutex } from "async-mutex";
import { ChangeNotifier } from "./change-notifier.js";
import type { ActionAuthority, ActionWaitOptions, PreparedActionIntent } from "./types.js";
import { assertPositiveSafeInteger, validateActionWaitOptions, validateTimestamp } from "./validation.js";

export type ActionApprovalMode = "disabled" | "explicit-only" | "confirm-once" | "allowed-in-task";
export type ActionApprovalStatus = "pending" | "approved" | "rejected" | "consumed" | "expired";

export interface ActionInvocationScope {
	explicitlyRequestedActionNames?: readonly string[];
	taskId?: string;
	taskAllowedActionNames?: readonly string[];
}

export interface ActionApprovalPolicy {
	policyId: string;
	mode: ActionApprovalMode;
}

export type ActionPolicyDecision =
	| { authorized: true; taskId?: string }
	| { authorized: false; reason: "disabled" | "explicit-request-required" | "task-authorization-required" }
	| { authorized: false; reason: "approval-required" };

export type ActionPolicyDenialReason =
	| "disabled"
	| "explicit-request-required"
	| "task-authorization-required"
	| "approval-required";

export interface ActionApprovalRequest extends StreamKey {
	coordinate: RunCoordinate;
	approvalId: string;
	requestFingerprint: string;
	policyId: string;
	operationId: string;
	intentFingerprint: string;
	actionName: string;
	expectedAuthority: ActionAuthority;
	taskId?: string;
	presentation?: JsonValue;
	requestedAt: string;
	expiresAt: string;
}

export interface ActionApprovalPresentationDraft {
	value: unknown;
	limits: JsonValueLimits;
}

export interface ActionApprovalRequestOptions {
	taskId?: string;
	presentation?: ActionApprovalPresentationDraft;
}

export interface ActionApprovalDecision {
	decision: "approved" | "rejected";
	reviewerId: string;
	decidedAt: string;
	reason?: string;
}

export interface ActionApprovalRecord {
	request: ActionApprovalRequest;
	status: ActionApprovalStatus;
	revision: string;
	updatedAt: string;
	decision?: ActionApprovalDecision;
	consumedAt?: string;
}

export interface ActionApprovalDecisionInput extends StreamKey {
	approvalId: string;
	expectedRevision: string;
	requestFingerprint: string;
	decision: "approve" | "reject";
	reviewerId: string;
	reason?: string;
	decidedAt: string;
}

export interface ActionApprovalConsumeInput extends StreamKey {
	coordinate: RunCoordinate;
	approvalId: string;
	expectedRevision: string;
	operationId: string;
	intentFingerprint: string;
	currentAuthority: ActionAuthority;
	consumedAt: string;
}

export interface ActionApprovalExpireInput extends StreamKey {
	approvalId: string;
	expectedRevision: string;
	expiredAt: string;
	readonly signal?: AbortSignal;
}

export interface ActionApprovalListRequest extends StreamKey {
	readonly runId?: string;
	readonly limit: number;
}

export interface ActionApprovalTerminalPurgeRequest extends StreamKey {
	readonly beforeUpdatedAt: string;
	readonly limit: number;
	readonly retainApprovalIds: readonly string[];
	readonly retainBudget: RetentionProtectionBudget;
}

export interface ActionApprovalStore {
	ensure(request: ActionApprovalRequest): Promise<ActionApprovalRecord>;
	get(stream: StreamKey, approvalId: string): Promise<ActionApprovalRecord | undefined>;
	listPending(request: ActionApprovalListRequest, now: string): Promise<ActionApprovalRecord[]>;
	decide(input: ActionApprovalDecisionInput): Promise<ActionApprovalRecord>;
	consume(input: ActionApprovalConsumeInput): Promise<ActionApprovalRecord>;
	expire(input: ActionApprovalExpireInput): Promise<ActionApprovalRecord>;
	purgeTerminal(request: ActionApprovalTerminalPurgeRequest): Promise<readonly string[]>;
}

export class ActionApprovalConflictError extends Error {
	constructor() {
		super("The approval changed or was already decided concurrently.");
		this.name = "ActionApprovalConflictError";
	}
}

export class ActionApprovalNotFoundError extends Error {
	constructor() {
		super("The action approval was not found.");
		this.name = "ActionApprovalNotFoundError";
	}
}

export class ActionApprovalDeniedError extends Error {
	constructor(public readonly reason: ActionPolicyDenialReason) {
		super(`The action policy denied execution: ${reason}.`);
		this.name = "ActionApprovalDeniedError";
	}
}

export interface ActionApprovalCoordinatorOptions {
	now?: () => Date;
}

export class ActionApprovalCoordinator {
	private readonly now: () => Date;
	private readonly changes = new ChangeNotifier();

	constructor(
		private readonly store: ActionApprovalStore,
		options: ActionApprovalCoordinatorOptions = {},
	) {
		this.now = options.now ?? (() => new Date());
	}

	async request(
		policyId: string,
		intent: PreparedActionIntent,
		durationMs: number,
		options: ActionApprovalRequestOptions = {},
	): Promise<ActionApprovalRecord> {
		assertOpaqueIdentifier(policyId);
		assertPositiveSafeInteger(durationMs, "durationMs");
		const taskId = options.taskId;
		if (taskId !== undefined) assertOpaqueIdentifier(taskId);
		const presentationDraft = options.presentation;
		let presentation: JsonValue | undefined;
		if (presentationDraft !== undefined) {
			const configuredLimits = presentationDraft.limits;
			const maxBytes = configuredLimits.maxBytes;
			const maxDepth = configuredLimits.maxDepth;
			const value = presentationDraft.value;
			presentation = normalizeJsonValueWithin(value, { maxBytes, maxDepth });
		}
		const requestedAt = this.now();
		const expiresAt = new Date(requestedAt.getTime() + durationMs);
		if (!Number.isFinite(expiresAt.getTime())) throw new RangeError("The approval expiration is outside Date range.");
		const requestFingerprint = approvalRequestFingerprint(policyId, intent, taskId, presentation);
		const approvalId = `apr_${sha256(
			canonicalizeJsonValue({
				protocolVersion: "1",
				operationId: intent.operationId,
				requestFingerprint,
			}),
		)}`;
		const record = await this.store.ensure({
			sessionId: intent.sessionId,
			actorId: intent.actorId,
			coordinate: structuredClone(intent.coordinate),
			approvalId,
			requestFingerprint,
			policyId,
			operationId: intent.operationId,
			intentFingerprint: intent.intentFingerprint,
			actionName: intent.actionName,
			expectedAuthority: structuredClone(intent.expectedAuthority),
			...(taskId === undefined ? {} : { taskId }),
			...(presentation === undefined ? {} : { presentation }),
			requestedAt: requestedAt.toISOString(),
			expiresAt: expiresAt.toISOString(),
		});
		this.changes.notify();
		return record;
	}

	get(stream: StreamKey, approvalId: string): Promise<ActionApprovalRecord | undefined> {
		return this.store.get(stream, approvalId);
	}

	async waitForDecision(approval: ActionApprovalRecord, options: ActionWaitOptions): Promise<ActionApprovalRecord> {
		assertActionApprovalRecord(approval);
		validateActionWaitOptions(options);
		let revision = this.changes.snapshot();
		let current = await this.store.get(approval.request, approval.request.approvalId);
		options.signal?.throwIfAborted();
		if (current === undefined) throw new ActionApprovalNotFoundError();
		assertActionApprovalRecord(current);
		assertSameApprovalRequest(approval, current);
		while (current.status === "pending") {
			options.signal?.throwIfAborted();
			const now = this.now();
			if (now.getTime() >= Date.parse(current.request.expiresAt)) {
				try {
					const expired = await this.store.expire({
						sessionId: current.request.sessionId,
						actorId: current.request.actorId,
						approvalId: current.request.approvalId,
						expectedRevision: current.revision,
						expiredAt: now.toISOString(),
						...(options.signal === undefined ? {} : { signal: options.signal }),
					});
					options.signal?.throwIfAborted();
					assertActionApprovalRecord(expired);
					assertSameApprovalRequest(approval, expired);
					if (expired.status !== "expired") throw new ActionApprovalConflictError();
					this.changes.notify();
					return expired;
				} catch (error) {
					if (!(error instanceof ActionApprovalConflictError)) throw error;
				}
				options.signal?.throwIfAborted();
			} else {
				await this.changes.wait(
					revision,
					Math.min(options.pollIntervalMs, Date.parse(current.request.expiresAt) - now.getTime()),
					options.signal,
				);
			}
			revision = this.changes.snapshot();
			const stored = await this.store.get(current.request, current.request.approvalId);
			options.signal?.throwIfAborted();
			if (stored === undefined) throw new ActionApprovalNotFoundError();
			assertActionApprovalRecord(stored);
			assertSameApprovalRequest(approval, stored);
			current = stored;
		}
		return current;
	}

	listPending(request: ActionApprovalListRequest): Promise<ActionApprovalRecord[]> {
		return this.store.listPending(request, this.now().toISOString());
	}

	changeRevision(): bigint {
		return this.changes.snapshot();
	}

	waitForChange(afterRevision: bigint, timeoutMs: number, signal?: AbortSignal): Promise<boolean> {
		return this.changes.wait(afterRevision, timeoutMs, signal);
	}

	async decide(input: Omit<ActionApprovalDecisionInput, "decidedAt">): Promise<ActionApprovalRecord> {
		const record = await this.store.decide({ ...input, decidedAt: this.now().toISOString() });
		this.changes.notify();
		return record;
	}

	async consume(input: Omit<ActionApprovalConsumeInput, "consumedAt">): Promise<ActionApprovalRecord> {
		const record = await this.store.consume({ ...input, consumedAt: this.now().toISOString() });
		this.changes.notify();
		return record;
	}

	purgeTerminal(request: ActionApprovalTerminalPurgeRequest): Promise<readonly string[]> {
		return this.store.purgeTerminal(request);
	}
}

export class InMemoryActionApprovalStore implements ActionApprovalStore {
	private readonly serial = new Mutex();
	private readonly records = new Map<string, ActionApprovalRecord>();

	ensure(request: ActionApprovalRequest): Promise<ActionApprovalRecord> {
		return this.serial.runExclusive(() => {
			assertActionApprovalRequest(request);
			const key = approvalKey(request, request.approvalId);
			const existing = this.records.get(key);
			if (existing) {
				if (existing.request.requestFingerprint !== request.requestFingerprint) {
					throw new ActionApprovalConflictError();
				}
				return structuredClone(existing);
			}
			const record: ActionApprovalRecord = {
				request: structuredClone(request),
				status: "pending",
				revision: "1",
				updatedAt: request.requestedAt,
			};
			this.records.set(key, record);
			return structuredClone(record);
		});
	}

	get(stream: StreamKey, approvalId: string): Promise<ActionApprovalRecord | undefined> {
		return this.serial.runExclusive(() => {
			validateApprovalStream(stream);
			assertApprovalId(approvalId);
			const record = this.records.get(approvalKey(stream, approvalId));
			return record ? structuredClone(record) : undefined;
		});
	}

	listPending(request: ActionApprovalListRequest, now: string): Promise<ActionApprovalRecord[]> {
		return this.serial.runExclusive(() => {
			validateApprovalListRequest(request);
			validateTimestamp(now);
			return [...this.records.values()]
				.filter(
					(record) =>
						record.request.sessionId === request.sessionId &&
						record.request.actorId === request.actorId &&
						(request.runId === undefined || record.request.coordinate.runId === request.runId) &&
						record.status === "pending" &&
						record.request.expiresAt > now,
				)
				.slice(0, request.limit)
				.map((record) => structuredClone(record));
		});
	}

	decide(input: ActionApprovalDecisionInput): Promise<ActionApprovalRecord> {
		return this.serial.runExclusive(() => {
			const key = approvalKey(input, input.approvalId);
			const current = this.records.get(key);
			if (!current) throw new ActionApprovalNotFoundError();
			const decided = decideActionApprovalRecord(current, input);
			this.records.set(key, decided);
			return structuredClone(decided);
		});
	}

	consume(input: ActionApprovalConsumeInput): Promise<ActionApprovalRecord> {
		return this.serial.runExclusive(() => {
			const key = approvalKey(input, input.approvalId);
			const current = this.records.get(key);
			if (!current) throw new ActionApprovalNotFoundError();
			const consumed = consumeActionApprovalRecord(current, input);
			this.records.set(key, consumed);
			return structuredClone(consumed);
		});
	}

	expire(input: ActionApprovalExpireInput): Promise<ActionApprovalRecord> {
		input.signal?.throwIfAborted();
		return this.serial.runExclusive(() => {
			input.signal?.throwIfAborted();
			const key = approvalKey(input, input.approvalId);
			const current = this.records.get(key);
			if (!current) throw new ActionApprovalNotFoundError();
			const expired = expireActionApprovalRecord(current, input);
			this.records.set(key, expired);
			return structuredClone(expired);
		});
	}

	purgeTerminal(request: ActionApprovalTerminalPurgeRequest): Promise<readonly string[]> {
		const captured = captureApprovalTerminalPurgeRequest(request);
		return this.serial.runExclusive(() => {
			const retained = new Set(captured.retainApprovalIds);
			const candidates = [...this.records.entries()]
				.map(([key, record], sequence) => ({ key, record, sequence }))
				.filter(
					({ record }) =>
						record.request.sessionId === captured.sessionId &&
						record.request.actorId === captured.actorId &&
						!retained.has(record.request.approvalId) &&
						(record.status === "rejected" || record.status === "consumed" || record.status === "expired") &&
						record.updatedAt < captured.beforeUpdatedAt,
				)
				.sort(
					(left, right) =>
						left.record.updatedAt.localeCompare(right.record.updatedAt) || left.sequence - right.sequence,
				)
				.slice(0, captured.limit);
			for (const { key } of candidates) this.records.delete(key);
			return candidates.map(({ record }) => record.request.approvalId);
		});
	}
}

export function captureApprovalTerminalPurgeRequest(
	request: ActionApprovalTerminalPurgeRequest,
): Readonly<ActionApprovalTerminalPurgeRequest> {
	const sessionId = request.sessionId;
	const actorId = request.actorId;
	const beforeUpdatedAt = request.beforeUpdatedAt;
	const limit = request.limit;
	const retained = request.retainApprovalIds;
	const retainBudget = request.retainBudget;
	validateApprovalStream({ sessionId, actorId });
	validateTimestamp(beforeUpdatedAt);
	assertPositiveSafeInteger(limit, "limit");
	const retainApprovalIds = captureRetainedIdentifiers(retained, retainBudget, "retainApprovalIds");
	for (const approvalId of retainApprovalIds) assertApprovalId(approvalId);
	return Object.freeze({
		sessionId,
		actorId,
		beforeUpdatedAt,
		limit,
		retainApprovalIds,
		retainBudget: Object.freeze({ maxCount: retainBudget.maxCount, maxBytes: retainBudget.maxBytes }),
	});
}

function assertSameApprovalRequest(expected: ActionApprovalRecord, actual: ActionApprovalRecord): void {
	if (canonicalizeJsonValue(expected.request) !== canonicalizeJsonValue(actual.request)) {
		throw new ActionApprovalConflictError();
	}
}

export function evaluateActionPolicy(
	policy: ActionApprovalPolicy,
	actionName: string,
	scope: ActionInvocationScope,
): ActionPolicyDecision {
	assertOpaqueIdentifier(policy.policyId);
	assertOpaqueIdentifier(actionName);
	if (scope.taskId !== undefined) assertOpaqueIdentifier(scope.taskId);
	switch (policy.mode) {
		case "disabled":
			return { authorized: false, reason: "disabled" };
		case "explicit-only":
			return scope.explicitlyRequestedActionNames?.includes(actionName)
				? { authorized: true, ...(scope.taskId === undefined ? {} : { taskId: scope.taskId }) }
				: { authorized: false, reason: "explicit-request-required" };
		case "allowed-in-task":
			return scope.taskId && scope.taskAllowedActionNames?.includes(actionName)
				? { authorized: true, taskId: scope.taskId }
				: { authorized: false, reason: "task-authorization-required" };
		case "confirm-once":
			return { authorized: false, reason: "approval-required" };
	}
}

export function approvalRequestFingerprint(
	policyId: string,
	intent: PreparedActionIntent,
	taskId?: string,
	presentation?: JsonValue,
): string {
	return fingerprintApprovalRequest({
		coordinate: intent.coordinate,
		policyId,
		operationId: intent.operationId,
		intentFingerprint: intent.intentFingerprint,
		actionName: intent.actionName,
		expectedAuthority: intent.expectedAuthority,
		...(taskId === undefined ? {} : { taskId }),
		...(presentation === undefined ? {} : { presentation }),
	});
}

export function approvalRequestFingerprintFromRequest(request: ActionApprovalRequest): string {
	return fingerprintApprovalRequest(request);
}

interface ApprovalRequestFingerprintInput {
	coordinate: RunCoordinate;
	policyId: string;
	operationId: string;
	intentFingerprint: string;
	actionName: string;
	expectedAuthority: ActionAuthority;
	taskId?: string;
	presentation?: JsonValue;
}

function fingerprintApprovalRequest(input: ApprovalRequestFingerprintInput): string {
	return sha256(
		canonicalizeJsonValue({
			protocolVersion: "3",
			coordinate: input.coordinate,
			policyId: input.policyId,
			operationId: input.operationId,
			intentFingerprint: input.intentFingerprint,
			actionName: input.actionName,
			expectedAuthority: input.expectedAuthority,
			taskId: input.taskId ?? null,
			presentationPresent: input.presentation !== undefined,
			presentation: input.presentation ?? null,
		}),
	);
}

export function assertActionApprovalRequest(request: unknown): asserts request is ActionApprovalRequest {
	if (!isRecord(request)) throw new TypeError("The approval request is invalid.");
	if (typeof request.sessionId !== "string" || typeof request.actorId !== "string") {
		throw new TypeError("The approval request stream is invalid.");
	}
	validateApprovalStream({ sessionId: request.sessionId, actorId: request.actorId });
	assertEventStreamCoordinate(
		{ sessionId: request.sessionId, actorId: request.actorId },
		request.coordinate as RunCoordinate,
	);
	if (typeof request.approvalId !== "string") throw new TypeError("The approval identifier is invalid.");
	assertApprovalId(request.approvalId);
	if (typeof request.requestFingerprint !== "string") throw new TypeError("The request fingerprint is invalid.");
	if (!/^[0-9a-f]{64}$/u.test(request.requestFingerprint)) throw new TypeError("The request fingerprint is invalid.");
	for (const value of [request.policyId, request.operationId, request.actionName]) {
		if (typeof value !== "string") throw new TypeError("The approval request identifier is invalid.");
		assertOpaqueIdentifier(value);
	}
	if (typeof request.intentFingerprint !== "string") throw new TypeError("The intent fingerprint is invalid.");
	if (!/^[0-9a-f]{64}$/u.test(request.intentFingerprint)) throw new TypeError("The intent fingerprint is invalid.");
	if (request.taskId !== undefined) {
		if (typeof request.taskId !== "string") throw new TypeError("The approval task identifier is invalid.");
		assertOpaqueIdentifier(request.taskId);
	}
	if (request.presentation !== undefined) canonicalizeJsonValue(request.presentation);
	validateAuthority(request.expectedAuthority);
	if (typeof request.requestedAt !== "string" || typeof request.expiresAt !== "string") {
		throw new TypeError("The approval timestamps are invalid.");
	}
	validateTimestamp(request.requestedAt);
	validateTimestamp(request.expiresAt);
	if (request.expiresAt <= request.requestedAt) {
		throw new TypeError("The approval expiration must be after its request time.");
	}
	const candidate = request as unknown as ActionApprovalRequest;
	if (approvalRequestFingerprintFromRequest(candidate) !== request.requestFingerprint) {
		throw new TypeError("The approval request fingerprint does not match its content.");
	}
	const expectedApprovalId = `apr_${sha256(
		canonicalizeJsonValue({
			protocolVersion: "1",
			operationId: request.operationId,
			requestFingerprint: request.requestFingerprint,
		}),
	)}`;
	if (expectedApprovalId !== request.approvalId) throw new TypeError("The approval identifier is not content-bound.");
}

export function assertActionApprovalRecord(record: unknown): asserts record is ActionApprovalRecord {
	if (!isRecord(record)) throw new TypeError("The approval record is invalid.");
	assertActionApprovalRequest(record.request);
	if (
		record.status !== "pending" &&
		record.status !== "approved" &&
		record.status !== "rejected" &&
		record.status !== "consumed" &&
		record.status !== "expired"
	) {
		throw new TypeError("The approval status is invalid.");
	}
	if (typeof record.revision !== "string") throw new TypeError("The approval revision is invalid.");
	if (!/^[1-9][0-9]*$/u.test(record.revision)) throw new TypeError("The approval revision is invalid.");
	if (typeof record.updatedAt !== "string") throw new TypeError("The approval update timestamp is invalid.");
	validateTimestamp(record.updatedAt);
	if (record.decision !== undefined) {
		if (!isRecord(record.decision)) throw new TypeError("The approval decision is invalid.");
		if (record.decision.decision !== "approved" && record.decision.decision !== "rejected") {
			throw new TypeError("The approval decision is invalid.");
		}
		if (typeof record.decision.reviewerId !== "string" || typeof record.decision.decidedAt !== "string") {
			throw new TypeError("The approval decision is invalid.");
		}
		assertOpaqueIdentifier(record.decision.reviewerId);
		validateTimestamp(record.decision.decidedAt);
		if (record.decision.reason !== undefined && typeof record.decision.reason !== "string") {
			throw new TypeError("The approval decision reason is invalid.");
		}
		if (
			record.decision.decidedAt < record.request.requestedAt ||
			record.decision.decidedAt >= record.request.expiresAt
		) {
			throw new TypeError("The approval decision timestamp is invalid.");
		}
	}
	if (record.status === "pending" && record.decision) throw new TypeError("A pending approval cannot have a decision.");
	if (
		(record.status === "approved" || record.status === "rejected" || record.status === "consumed") &&
		!record.decision
	) {
		throw new TypeError("The approval state requires a durable decision.");
	}
	if (record.status === "approved" && record.decision?.decision !== "approved") {
		throw new TypeError("The approval state and decision disagree.");
	}
	if (record.status === "rejected" && record.decision?.decision !== "rejected") {
		throw new TypeError("The approval state and decision disagree.");
	}
	if (
		(record.status === "approved" || record.status === "rejected") &&
		record.updatedAt !== record.decision?.decidedAt
	) {
		throw new TypeError("The approval update timestamp does not match its decision.");
	}
	if (record.status === "consumed" && record.decision?.decision !== "approved") {
		throw new TypeError("A consumed approval must have an approved decision.");
	}
	if (record.status === "expired" && record.updatedAt !== record.request.expiresAt) {
		throw new TypeError("The approval expiration timestamp is invalid.");
	}
	if ((record.status === "consumed") !== (record.consumedAt !== undefined)) {
		throw new TypeError("The consumed approval state is invalid.");
	}
	if (record.consumedAt !== undefined) {
		if (typeof record.consumedAt !== "string") throw new TypeError("The approval consumption timestamp is invalid.");
		validateTimestamp(record.consumedAt);
		if (
			!record.decision ||
			record.consumedAt < (record.decision as unknown as ActionApprovalDecision).decidedAt ||
			record.consumedAt >= record.request.expiresAt ||
			record.updatedAt !== record.consumedAt
		) {
			throw new TypeError("The approval consumption chronology is invalid.");
		}
	}
}

export function decideActionApprovalRecord(
	current: ActionApprovalRecord,
	input: ActionApprovalDecisionInput,
): ActionApprovalRecord {
	assertActionApprovalRecord(current);
	assertDecisionInput(input);
	if (current.status !== "pending" || current.revision !== input.expectedRevision) {
		throw new ActionApprovalConflictError();
	}
	if (current.request.requestFingerprint !== input.requestFingerprint) throw new ActionApprovalConflictError();
	if (input.decidedAt < current.request.requestedAt) throw new ActionApprovalConflictError();
	if (current.request.expiresAt <= input.decidedAt) {
		return advanceApproval(current, "expired", current.request.expiresAt);
	}
	const decision: ActionApprovalDecision = {
		decision: input.decision === "approve" ? "approved" : "rejected",
		reviewerId: input.reviewerId,
		decidedAt: input.decidedAt,
		...(input.reason === undefined ? {} : { reason: input.reason }),
	};
	return advanceApproval(current, decision.decision, input.decidedAt, { decision });
}

export function consumeActionApprovalRecord(
	current: ActionApprovalRecord,
	input: ActionApprovalConsumeInput,
): ActionApprovalRecord {
	assertActionApprovalRecord(current);
	assertConsumeInput(input);
	if (current.status !== "approved" || current.revision !== input.expectedRevision) {
		throw new ActionApprovalConflictError();
	}
	if (
		canonicalizeJsonValue(current.request.coordinate) !== canonicalizeJsonValue(input.coordinate) ||
		current.request.operationId !== input.operationId ||
		current.request.intentFingerprint !== input.intentFingerprint ||
		canonicalizeJsonValue(current.request.expectedAuthority) !== canonicalizeJsonValue(input.currentAuthority)
	) {
		throw new ActionApprovalConflictError();
	}
	if (current.request.expiresAt <= input.consumedAt) {
		return advanceApproval(current, "expired", current.request.expiresAt);
	}
	return advanceApproval(current, "consumed", input.consumedAt, { consumedAt: input.consumedAt });
}

export function expireActionApprovalRecord(
	current: ActionApprovalRecord,
	input: ActionApprovalExpireInput,
): ActionApprovalRecord {
	assertActionApprovalRecord(current);
	assertExpireInput(input);
	if (
		current.status !== "pending" ||
		current.revision !== input.expectedRevision ||
		input.expiredAt < current.request.expiresAt
	) {
		throw new ActionApprovalConflictError();
	}
	return advanceApproval(current, "expired", current.request.expiresAt);
}

function assertDecisionInput(input: ActionApprovalDecisionInput): void {
	validateApprovalStream(input);
	assertApprovalId(input.approvalId);
	parseEventRevision(input.expectedRevision);
	if (!/^[0-9a-f]{64}$/u.test(input.requestFingerprint)) throw new TypeError("The request fingerprint is invalid.");
	assertOpaqueIdentifier(input.reviewerId);
	if (input.reason !== undefined && typeof input.reason !== "string")
		throw new TypeError("The decision reason is invalid.");
	validateTimestamp(input.decidedAt);
}

function assertConsumeInput(input: ActionApprovalConsumeInput): void {
	validateApprovalStream(input);
	assertEventStreamCoordinate({ sessionId: input.sessionId, actorId: input.actorId }, input.coordinate);
	assertApprovalId(input.approvalId);
	parseEventRevision(input.expectedRevision);
	assertOpaqueIdentifier(input.operationId);
	if (!/^[0-9a-f]{64}$/u.test(input.intentFingerprint)) throw new TypeError("The intent fingerprint is invalid.");
	validateAuthority(input.currentAuthority);
	validateTimestamp(input.consumedAt);
}

function assertExpireInput(input: ActionApprovalExpireInput): void {
	validateApprovalStream(input);
	assertApprovalId(input.approvalId);
	parseEventRevision(input.expectedRevision);
	validateTimestamp(input.expiredAt);
}

function validateAuthority(authority: unknown): asserts authority is ActionAuthority {
	if (!isRecord(authority)) throw new TypeError("The approval authority is invalid.");
	if (
		typeof authority.timelineId !== "string" ||
		typeof authority.generation !== "string" ||
		typeof authority.revision !== "string"
	) {
		throw new TypeError("The approval authority is invalid.");
	}
	assertOpaqueIdentifier(authority.timelineId);
	parseEventRevision(authority.generation);
	parseEventRevision(authority.revision);
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function validateApprovalStream(stream: StreamKey): void {
	assertOpaqueIdentifier(stream.sessionId);
	assertOpaqueIdentifier(stream.actorId);
}

function validateApprovalListRequest(request: ActionApprovalListRequest): void {
	validateApprovalStream(request);
	if (request.runId !== undefined) assertOpaqueIdentifier(request.runId);
	assertPositiveSafeInteger(request.limit, "limit");
}

function assertApprovalId(value: string): void {
	if (!/^apr_[0-9a-f]{64}$/u.test(value)) throw new TypeError("The approval identifier is invalid.");
}

function approvalKey(stream: StreamKey, approvalId: string): string {
	return JSON.stringify([stream.sessionId, stream.actorId, approvalId]);
}

function advanceApproval(
	current: ActionApprovalRecord,
	status: ActionApprovalStatus,
	updatedAt: string,
	extra: Partial<Pick<ActionApprovalRecord, "decision" | "consumedAt">> = {},
): ActionApprovalRecord {
	return {
		...current,
		...extra,
		status,
		revision: (BigInt(current.revision) + 1n).toString(),
		updatedAt,
	};
}

function sha256(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}

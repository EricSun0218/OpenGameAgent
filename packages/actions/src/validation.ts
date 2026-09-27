import {
	assertActionRecordEnvelope,
	assertCanonicalTimestamp,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	captureRetainedIdentifiers,
	parseEventRevision,
} from "@opengameagent/protocol";
import { ActionStateConflictError } from "./errors.js";
import { isActionOperationId, prepareActionIntent, validateAuthority } from "./identity.js";
import type {
	ActionClaimRequest,
	ActionDelivery,
	ActionGoalProjectionLimits,
	ActionReceiptInput,
	ActionReconcileInput,
	ActionRecord,
	ActionTerminalPurgeRequest,
	ActionWaitOptions,
} from "./types.js";

const MAX_TIMER_DELAY_MS = 2_147_483_647;

export function assertActionRecord(
	value: unknown,
	goalProjectionLimits?: Readonly<ActionGoalProjectionLimits>,
): asserts value is ActionRecord {
	assertActionRecordEnvelope(value);
	const prepared = prepareActionIntent(
		{
			sessionId: value.sessionId,
			actorId: value.actorId,
			coordinate: value.coordinate,
			actionName: value.actionName,
			operationKey: value.operationKey,
			payload: value.payload,
			expectedAuthority: value.expectedAuthority,
			conflictKeys: value.conflictKeys,
			replayPolicy: value.replayPolicy,
			...(value.goalProjection === undefined ? {} : { goalProjection: value.goalProjection }),
		},
		goalProjectionLimits,
	);
	if (
		prepared.operationId !== value.operationId ||
		prepared.intentFingerprint !== value.intentFingerprint ||
		prepared.payloadFingerprint !== value.payloadFingerprint
	) {
		throw new ActionStateConflictError();
	}
	parseEventRevision(value.attempt);
	validateTimestamp(value.createdAt);
	validateTimestamp(value.updatedAt);
	if ((value.status === "claimed" || value.status === "uncertain") !== (value.claim !== undefined)) {
		throw new ActionStateConflictError();
	}
	if (value.claim) {
		if (value.claim.attempt !== value.attempt || value.attempt === "0") throw new ActionStateConflictError();
		validateTimestamp(value.claim.claimedAt);
		validateTimestamp(value.claim.expiresAt);
		if (value.claim.expiresAt <= value.claim.claimedAt) throw new ActionStateConflictError();
	}
	if (value.receipt) {
		if (value.receipt.operationId !== value.operationId) throw new ActionStateConflictError();
		parseEventRevision(value.receipt.attempt);
		validateTimestamp(value.receipt.receivedAt);
		if (value.receipt.authorityAfter) validateAuthority(value.receipt.authorityAfter);
		if (value.receipt.result !== undefined) canonicalizeJsonValue(value.receipt.result);
		if (value.receipt.modelResult !== undefined) canonicalizeJsonValue(value.receipt.modelResult);
	}
	if ((value.status === "pending" || value.status === "failed") && !hasKnownUnappliedActionOutcome(value)) {
		throw new ActionStateConflictError();
	}
	if (value.goalProjectionSettlement !== undefined) {
		validateTimestamp(value.goalProjectionSettlement.settledAt);
		const outcomeMatchesSource =
			(value.goalProjectionSettlement.outcome === "advanced" &&
				value.status === "succeeded" &&
				value.receipt?.outcome === "succeeded") ||
			(value.goalProjectionSettlement.outcome === "source_failed" && isActionSourceFailed(value));
		if (
			value.goalProjection === undefined ||
			!outcomeMatchesSource ||
			value.goalProjectionSettlement.settledAt < (value.receipt?.receivedAt ?? "") ||
			value.updatedAt !== value.goalProjectionSettlement.settledAt
		) {
			throw new ActionStateConflictError();
		}
	}
}

/** A previous attempt's rejection cannot prove that a later attempt made no world change. */
export function hasKnownUnappliedActionOutcome(record: {
	readonly attempt: string;
	readonly receipt?: { readonly attempt: string; readonly outcome: string };
}): boolean {
	return record.attempt === "0"
		? record.receipt === undefined
		: record.receipt?.outcome === "rejected" && record.receipt.attempt === record.attempt;
}

export function isActionSourceFailed(record: {
	readonly status: string;
	readonly attempt: string;
	readonly receipt?: { readonly attempt: string; readonly outcome: string };
}): boolean {
	return record.status === "failed" && hasKnownUnappliedActionOutcome(record);
}

export function assertActionDelivery(
	value: unknown,
	goalProjectionLimits?: Readonly<ActionGoalProjectionLimits>,
): asserts value is ActionDelivery {
	if (value === null || typeof value !== "object" || Array.isArray(value)) throw new ActionStateConflictError();
	const delivery = value as { readonly kind?: unknown; readonly action?: unknown };
	if (delivery.kind !== "dispatch" && delivery.kind !== "reconcile") throw new ActionStateConflictError();
	assertActionRecord(delivery.action, goalProjectionLimits);
	if (
		(delivery.kind === "dispatch" && delivery.action.status !== "claimed") ||
		(delivery.kind === "reconcile" && delivery.action.status !== "claimed" && delivery.action.status !== "uncertain")
	) {
		throw new ActionStateConflictError();
	}
}

export function validateTimestamp(value: string): void {
	try {
		assertCanonicalTimestamp(value);
	} catch {
		throw new ActionStateConflictError();
	}
}

export function validateActionClaimRequest(request: ActionClaimRequest): void {
	validateStream(request);
	validateTimestamp(request.claimedAt);
	validateTimestamp(request.expiresAt);
	if (request.expiresAt <= request.claimedAt) {
		throw new TypeError("The action claim expiration must be after its claim time.");
	}
	assertPositiveSafeInteger(request.candidateLimit, "candidateLimit");
}

export function validateActionReceiptInput(input: ActionReceiptInput): void {
	validateStream(input);
	if (!isActionOperationId(input.operationId)) throw new TypeError("The action operation ID is invalid.");
	if (input.claimToken.length === 0) throw new TypeError("The action claim token is invalid.");
	if (!/^[1-9][0-9]*$/u.test(input.attempt)) throw new TypeError("A receipt attempt must be positive.");
	validateTimestamp(input.receivedAt);
	if (input.authorityAfter) validateAuthority(input.authorityAfter);
}

export function validateActionReconcileInput(input: ActionReconcileInput): void {
	validateStream(input);
	if (!isActionOperationId(input.operationId)) throw new TypeError("The action operation ID is invalid.");
	assertOpaqueIdentifier(input.claimToken);
	if (!/^[1-9][0-9]*$/u.test(input.attempt)) throw new TypeError("A reconciliation attempt must be positive.");
	validateTimestamp(input.reconciledAt);
	if (input.authorityAfter) validateAuthority(input.authorityAfter);
}

export function captureActionTerminalPurgeRequest(
	request: ActionTerminalPurgeRequest,
): Readonly<ActionTerminalPurgeRequest> {
	const sessionId = request.sessionId;
	const actorId = request.actorId;
	const beforeUpdatedAt = request.beforeUpdatedAt;
	const limit = request.limit;
	const retained = request.retainOperationIds;
	const retainBudget = request.retainBudget;
	validateStream({ sessionId, actorId });
	validateTimestamp(beforeUpdatedAt);
	assertPositiveSafeInteger(limit, "limit");
	const retainOperationIds = captureRetainedIdentifiers(retained, retainBudget, "retainOperationIds");
	for (const operationId of retainOperationIds) {
		if (!isActionOperationId(operationId)) throw new TypeError("A retained Action operation ID is invalid.");
	}
	return Object.freeze({
		sessionId,
		actorId,
		beforeUpdatedAt,
		limit,
		retainOperationIds,
		retainBudget: Object.freeze({ maxCount: retainBudget.maxCount, maxBytes: retainBudget.maxBytes }),
	});
}

export function validateStream(stream: {
	sessionId: string;
	actorId?: string;
	runId?: string;
	claimantId?: string;
	claimToken?: string;
}): void {
	assertOpaqueIdentifier(stream.sessionId);
	if (stream.actorId !== undefined) assertOpaqueIdentifier(stream.actorId);
	if (stream.runId !== undefined) assertOpaqueIdentifier(stream.runId);
	if (stream.claimantId !== undefined) assertOpaqueIdentifier(stream.claimantId);
	if (stream.claimToken !== undefined) assertOpaqueIdentifier(stream.claimToken);
}

export function assertPositiveSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer.`);
}

export function validateActionWaitOptions(options: ActionWaitOptions): void {
	assertPositiveSafeInteger(options.pollIntervalMs, "pollIntervalMs");
	if (options.pollIntervalMs > MAX_TIMER_DELAY_MS) {
		throw new RangeError("pollIntervalMs exceeds Node's maximum timer delay.");
	}
	options.signal?.throwIfAborted();
}

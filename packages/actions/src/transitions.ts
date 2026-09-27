import { canonicalizeJsonValue, normalizeJsonValue } from "@opengameagent/protocol";
import { ActionClaimConflictError, ActionIntentConflictError, ActionStateConflictError } from "./errors.js";
import type {
	ActionClaimRequest,
	ActionGoalProjectionSettlementInput,
	ActionReceipt,
	ActionReceiptInput,
	ActionReconcileInput,
	ActionRecord,
} from "./types.js";
import {
	hasKnownUnappliedActionOutcome,
	isActionSourceFailed,
	validateActionClaimRequest,
	validateActionReceiptInput,
	validateActionReconcileInput,
	validateTimestamp,
} from "./validation.js";

export interface ActionTransition {
	record: ActionRecord;
	releaseConflicts: boolean;
}

export function claimActionRecord(record: ActionRecord, request: ActionClaimRequest): ActionTransition {
	validateActionClaimRequest(request);
	if (record.status !== "pending") throw new ActionStateConflictError();
	const attempt = (BigInt(record.attempt) + 1n).toString();
	return {
		releaseConflicts: false,
		record: {
			...record,
			status: "claimed",
			attempt,
			updatedAt: request.claimedAt,
			claim: {
				claimToken: request.claimToken,
				claimantId: request.claimantId,
				attempt,
				claimedAt: request.claimedAt,
				expiresAt: request.expiresAt,
			},
		},
	};
}

export function receiptActionRecord(record: ActionRecord, input: ActionReceiptInput): ActionTransition {
	validateActionReceiptInput(input);
	if (record.claim && (record.claim.claimToken !== input.claimToken || record.claim.attempt !== input.attempt)) {
		throw new ActionClaimConflictError();
	}
	const receipt = prepareReceipt(input);
	if (
		record.receipt &&
		(!record.claim || (record.receipt.claimToken === input.claimToken && record.receipt.attempt === input.attempt))
	) {
		return replayReceipt(record, receipt);
	}
	if ((record.status !== "claimed" && record.status !== "uncertain") || !record.claim) {
		throw new ActionStateConflictError();
	}
	const status = input.outcome === "succeeded" ? "succeeded" : input.outcome === "rejected" ? "failed" : "uncertain";
	const updated: ActionRecord = { ...record, status, updatedAt: input.receivedAt, receipt };
	if (status !== "uncertain") delete updated.claim;
	return { record: updated, releaseConflicts: status !== "uncertain" };
}

function prepareReceipt(input: ActionReceiptInput): ActionReceipt {
	return {
		operationId: input.operationId,
		claimToken: input.claimToken,
		attempt: input.attempt,
		outcome: input.outcome,
		receivedAt: input.receivedAt,
		...(input.result === undefined ? {} : { result: normalizeJsonValue(input.result) }),
		...(input.modelResult === undefined ? {} : { modelResult: normalizeJsonValue(input.modelResult) }),
		...(input.authorityAfter === undefined ? {} : { authorityAfter: structuredClone(input.authorityAfter) }),
	};
}

function replayReceipt(record: ActionRecord, receipt: ActionReceipt): ActionTransition {
	if (record.receipt?.claimToken !== receipt.claimToken || record.receipt.attempt !== receipt.attempt) {
		throw new ActionClaimConflictError();
	}
	if (receiptSemantics(record.receipt) !== receiptSemantics(receipt)) throw new ActionStateConflictError();
	return { record, releaseConflicts: false };
}

function receiptSemantics(receipt: ActionReceipt): string {
	return canonicalizeJsonValue({
		operationId: receipt.operationId,
		claimToken: receipt.claimToken,
		attempt: receipt.attempt,
		outcome: receipt.outcome,
		...(receipt.result === undefined ? {} : { result: receipt.result }),
		...(receipt.modelResult === undefined ? {} : { modelResult: receipt.modelResult }),
		...(receipt.authorityAfter === undefined ? {} : { authorityAfter: receipt.authorityAfter }),
	});
}

export function reconcileActionRecord(record: ActionRecord, input: ActionReconcileInput): ActionTransition {
	validateActionReconcileInput(input);
	if (record.claim && (record.claim.claimToken !== input.claimToken || record.claim.attempt !== input.attempt)) {
		throw new ActionClaimConflictError();
	}
	const receipt = prepareReceipt({
		...input,
		outcome: input.resolution === "applied" ? "succeeded" : input.resolution === "not-applied" ? "rejected" : "unknown",
		receivedAt: input.reconciledAt,
	});
	if (!record.claim) {
		if (record.receipt) return replayReceipt(record, receipt);
		throw new ActionStateConflictError();
	}
	if (record.status !== "claimed" && record.status !== "uncertain") throw new ActionStateConflictError();
	if (record.receipt?.claimToken === input.claimToken && record.receipt.attempt === input.attempt) {
		if (receiptSemantics(record.receipt) === receiptSemantics(receipt)) {
			return { record, releaseConflicts: false };
		}
		if (input.resolution === "unknown" || record.receipt.outcome !== "unknown") {
			throw new ActionStateConflictError();
		}
	}

	const retry = input.resolution === "not-applied" && record.replayPolicy !== "never";
	const status =
		input.resolution === "unknown"
			? "uncertain"
			: input.resolution === "applied"
				? "succeeded"
				: retry
					? "pending"
					: "failed";
	const updated: ActionRecord = {
		...record,
		status,
		updatedAt: input.reconciledAt,
		receipt,
	};
	if (status !== "uncertain") delete updated.claim;
	return { record: updated, releaseConflicts: status !== "uncertain" };
}

export function reclaimActionRecord(record: ActionRecord, request: ActionClaimRequest): ActionTransition {
	if (
		record.status !== "claimed" ||
		record.replayPolicy !== "safe" ||
		!record.claim ||
		record.claim.expiresAt > request.claimedAt
	) {
		throw new ActionStateConflictError();
	}
	return claimActionRecord({ ...record, status: "pending" }, request);
}

export function expireActionRecord(record: ActionRecord, now: string): ActionTransition {
	validateTimestamp(now);
	if (record.status !== "claimed" || !record.claim || record.claim.expiresAt > now) {
		throw new ActionStateConflictError();
	}
	const updated: ActionRecord = {
		...record,
		status: "uncertain",
		updatedAt: now,
	};
	return { record: updated, releaseConflicts: false };
}

/** Only use after checking the run's closure in the same synchronous mutation. */
export function retirePendingActionRecord(record: ActionRecord, now: string): ActionTransition {
	validateTimestamp(now);
	if (record.status !== "pending" || !hasKnownUnappliedActionOutcome(record)) throw new ActionStateConflictError();
	return { record: { ...record, status: "failed", updatedAt: now }, releaseConflicts: true };
}

export function settleActionGoalProjectionRecord(
	record: ActionRecord,
	input: ActionGoalProjectionSettlementInput,
): ActionRecord {
	validateTimestamp(input.settledAt);
	if (record.intentFingerprint !== input.intentFingerprint) throw new ActionIntentConflictError();
	if (record.goalProjectionSettlement !== undefined) {
		if (record.goalProjectionSettlement.outcome !== input.outcome) throw new ActionStateConflictError();
		return record;
	}
	const sourceMatches =
		(input.outcome === "advanced" && record.status === "succeeded" && record.receipt?.outcome === "succeeded") ||
		(input.outcome === "source_failed" && isActionSourceFailed(record));
	if (record.goalProjection === undefined || !sourceMatches) throw new ActionStateConflictError();
	return {
		...record,
		updatedAt: input.settledAt,
		goalProjectionSettlement: { outcome: input.outcome, settledAt: input.settledAt },
	};
}

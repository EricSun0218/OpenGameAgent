import {
	assertCanonicalTimestamp,
	assertGameTime,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	captureWorkAuthorityRetirementRequest,
	type WorkAuthorityRetirementRequest,
	normalizeJsonValue,
} from "@opengameagent/protocol";
import { MailboxValidationError } from "./mailbox-errors.js";
import type {
	MailboxClaimRequest,
	MailboxLimits,
	MailboxListRequest,
	MailboxMessageDraft,
	MailboxMessageKey,
	MailboxMessageSequenceKey,
	MailboxMessageStatus,
	MailboxRecipient,
	MailboxRenewRequest,
} from "./mailbox-types.js";

const DECIMAL = /^(0|[1-9][0-9]*)$/u;
const encoder = new TextEncoder();
const LIMIT_NAMES = [
	"maxIdentifierBytes",
	"maxPayloadBytes",
	"maxTickDigits",
	"maxCounterDigits",
	"maxPendingMessagesPerTimelineGeneration",
	"maxPendingMessagesPerRecipient",
	"maxActiveDeliveries",
	"maxPageSize",
	"maxClaimBatchSize",
	"maxPurgeBatchSize",
] as const satisfies readonly (keyof MailboxLimits)[];

export function validateMailboxLimits(limits: MailboxLimits): void {
	for (const name of LIMIT_NAMES) {
		const value = limits[name];
		if (!Number.isSafeInteger(value) || value < 1 || value >= Number.MAX_SAFE_INTEGER) {
			throw new MailboxValidationError(`invalid_mailbox_limit:${name}`);
		}
	}
	if (limits.maxPendingMessagesPerRecipient > limits.maxPendingMessagesPerTimelineGeneration) {
		throw new MailboxValidationError("invalid_mailbox_limit:recipient_exceeds_timeline_generation");
	}
}

export function normalizeMailboxRecipient(
	recipient: MailboxRecipient,
	limits: Pick<MailboxLimits, "maxIdentifierBytes" | "maxCounterDigits">,
): MailboxRecipient {
	recipient = {
		ownerId: recipient.ownerId,
		timelineId: recipient.timelineId,
		generation: recipient.generation,
		sessionId: recipient.sessionId,
		actorId: recipient.actorId,
	};
	validateMailboxIdentifier(recipient.ownerId, limits.maxIdentifierBytes, "owner_id");
	validateMailboxIdentifier(recipient.timelineId, limits.maxIdentifierBytes, "timeline_id");
	validateMailboxDecimal(recipient.generation, "generation", limits.maxCounterDigits);
	validateMailboxIdentifier(recipient.sessionId, limits.maxIdentifierBytes, "session_id");
	validateMailboxIdentifier(recipient.actorId, limits.maxIdentifierBytes, "actor_id");
	return Object.freeze(recipient);
}

export function normalizeMailboxAuthorityRetirement(
	input: WorkAuthorityRetirementRequest,
	limits: MailboxLimits,
): WorkAuthorityRetirementRequest {
	const request = captureWorkAuthorityRetirementRequest(input);
	normalizeMailboxRecipient(request.partition, limits);
	validateMailboxIdentifier(request.retirementId, limits.maxIdentifierBytes, "retirement_id");
	return request;
}

export function normalizeMailboxListScope(
	request: MailboxListRequest,
	limits: Pick<MailboxLimits, "maxIdentifierBytes" | "maxCounterDigits">,
): Pick<MailboxListRequest, "ownerId" | "timelineId" | "generation" | "sessionId" | "actorId"> {
	validateMailboxIdentifier(request.ownerId, limits.maxIdentifierBytes, "owner_id");
	validateMailboxIdentifier(request.timelineId, limits.maxIdentifierBytes, "timeline_id");
	validateMailboxDecimal(request.generation, "generation", limits.maxCounterDigits);
	if (request.sessionId !== undefined) {
		validateMailboxIdentifier(request.sessionId, limits.maxIdentifierBytes, "session_id");
	}
	if (request.actorId !== undefined) {
		validateMailboxIdentifier(request.actorId, limits.maxIdentifierBytes, "actor_id");
	}
	return {
		ownerId: request.ownerId,
		timelineId: request.timelineId,
		generation: request.generation,
		...(request.sessionId === undefined ? {} : { sessionId: request.sessionId }),
		...(request.actorId === undefined ? {} : { actorId: request.actorId }),
	};
}

export function normalizeMailboxMessage(input: MailboxMessageDraft, limits: MailboxLimits): MailboxMessageDraft {
	validateMailboxIdentifier(input.messageId, limits.maxIdentifierBytes, "message_id");
	const recipient = normalizeMailboxRecipient(input.recipient, limits);
	if (input.senderId !== undefined) {
		validateMailboxIdentifier(input.senderId, limits.maxIdentifierBytes, "sender_id");
	}
	validateMailboxIdentifier(input.kind, limits.maxIdentifierBytes, "kind");
	if (input.correlationId !== undefined) {
		validateMailboxIdentifier(input.correlationId, limits.maxIdentifierBytes, "correlation_id");
	}
	const payload = normalizeJsonValue(input.payload);
	if (encoder.encode(canonicalizeJsonValue(payload)).byteLength > limits.maxPayloadBytes) {
		throw new MailboxValidationError("mailbox_payload_too_large");
	}
	try {
		assertGameTime(input.gameTime);
	} catch {
		throw new MailboxValidationError("invalid_mailbox_game_time");
	}
	if (input.gameTime.timelineId !== recipient.timelineId || input.gameTime.generation !== recipient.generation) {
		throw new MailboxValidationError("mailbox_time_partition_mismatch");
	}
	validateMailboxDecimal(input.gameTime.tick, "game_tick", limits.maxTickDigits);
	validateMailboxTimestamp(input.recordedAt);
	return {
		messageId: input.messageId,
		recipient,
		...(input.senderId === undefined ? {} : { senderId: input.senderId }),
		kind: input.kind,
		payload,
		gameTime: { ...input.gameTime },
		...(input.correlationId === undefined ? {} : { correlationId: input.correlationId }),
		recordedAt: input.recordedAt,
	};
}

export function normalizeMailboxClaim(request: MailboxClaimRequest, limits: MailboxLimits): MailboxClaimRequest {
	const recipient = normalizeMailboxRecipient(request, limits);
	request = {
		...recipient,
		leaseOwnerId: request.leaseOwnerId,
		operationalNow: request.operationalNow,
		leaseExpiresAt: request.leaseExpiresAt,
		limit: request.limit,
	};
	validateMailboxIdentifier(request.leaseOwnerId, limits.maxIdentifierBytes, "lease_owner_id");
	validateMailboxTimestamp(request.operationalNow);
	validateMailboxTimestamp(request.leaseExpiresAt);
	if (request.leaseExpiresAt <= request.operationalNow) {
		throw new MailboxValidationError("invalid_mailbox_lease_window");
	}
	validateMailboxPageLimit(request.limit, limits.maxClaimBatchSize, "invalid_mailbox_claim_limit");
	return {
		...recipient,
		leaseOwnerId: request.leaseOwnerId,
		operationalNow: request.operationalNow,
		leaseExpiresAt: request.leaseExpiresAt,
		limit: request.limit,
	};
}

export function normalizeMailboxRenew(request: MailboxRenewRequest, limits: MailboxLimits): MailboxRenewRequest {
	request = {
		...validateMailboxMessageSequenceKey(request, limits),
		leaseToken: request.leaseToken,
		operationalNow: request.operationalNow,
		leaseExpiresAt: request.leaseExpiresAt,
	};
	validateMailboxIdentifier(request.leaseToken, limits.maxIdentifierBytes, "lease_token");
	validateMailboxTimestamp(request.operationalNow);
	validateMailboxTimestamp(request.leaseExpiresAt);
	if (request.leaseExpiresAt <= request.operationalNow) {
		throw new MailboxValidationError("invalid_mailbox_lease_window");
	}
	return { ...request };
}

export function validateMailboxMessageKey(key: MailboxMessageKey, limits: MailboxLimits): MailboxMessageKey {
	const captured = { ...normalizeMailboxRecipient(key, limits), messageId: key.messageId };
	validateMailboxIdentifier(captured.messageId, limits.maxIdentifierBytes, "message_id");
	return Object.freeze(captured);
}

export function validateMailboxMessageSequenceKey(
	key: MailboxMessageSequenceKey,
	limits: MailboxLimits,
): MailboxMessageSequenceKey {
	const captured = { ...normalizeMailboxRecipient(key, limits), messageKey: key.messageKey };
	validatePositiveMailboxKey(captured.messageKey, "message_key");
	return Object.freeze(captured);
}

export function validateMailboxIdentifier(value: string, maximumBytes: number, name: string): void {
	try {
		assertOpaqueIdentifier(value);
	} catch {
		throw new MailboxValidationError(`invalid_mailbox_${name}`);
	}
	if (encoder.encode(value).byteLength > maximumBytes) {
		throw new MailboxValidationError(`mailbox_${name}_too_large`);
	}
}

export function validateMailboxTimestamp(value: string): void {
	try {
		assertCanonicalTimestamp(value);
	} catch {
		throw new MailboxValidationError("invalid_mailbox_timestamp");
	}
}

export function validateMailboxDecimal(value: string, name: string, maximumDigits: number): string {
	if (!DECIMAL.test(value) || value.length > maximumDigits) {
		throw new MailboxValidationError(`invalid_mailbox_${name}`);
	}
	return value;
}

export function incrementMailboxDecimal(value: string, maximumDigits: number, name: string): string {
	validateMailboxDecimal(value, name, maximumDigits);
	const result = (BigInt(value) + 1n).toString();
	if (result.length > maximumDigits) throw new MailboxValidationError(`mailbox_${name}_overflow`);
	return result;
}

export function validateMailboxCursor(value: bigint | undefined): void {
	if (value !== undefined && value < 0n) throw new MailboxValidationError("invalid_mailbox_cursor");
}

export function validatePositiveMailboxKey(value: bigint, name: string): void {
	if (value < 1n) throw new MailboxValidationError(`invalid_mailbox_${name}`);
}

export function validateMailboxPageLimit(value: number, maximum: number, code: string): void {
	if (!Number.isSafeInteger(value) || value < 1 || value > maximum) throw new MailboxValidationError(code);
}

export function validateMailboxStatus(value: string): asserts value is MailboxMessageStatus {
	if (value !== "pending" && value !== "completed" && value !== "cancelled") {
		throw new MailboxValidationError("invalid_mailbox_status");
	}
}

export function normalizeMailboxStatusFilter(
	values: readonly MailboxMessageStatus[] | undefined,
): readonly MailboxMessageStatus[] | undefined {
	if (values === undefined) return undefined;
	if (!Array.isArray(values) || values.length === 0) {
		throw new MailboxValidationError("empty_mailbox_status_filter");
	}
	for (const value of values) validateMailboxStatus(value);
	const normalized = [...values].sort();
	if (new Set(normalized).size !== normalized.length) {
		throw new MailboxValidationError("duplicate_mailbox_status_filter");
	}
	return normalized;
}

export function mailboxFingerprint(message: MailboxMessageDraft): string {
	return canonicalizeJsonValue(message);
}

export function sameMailboxGeneration(
	left: Pick<MailboxRecipient, "ownerId" | "timelineId" | "generation">,
	right: Pick<MailboxRecipient, "ownerId" | "timelineId" | "generation">,
): boolean {
	return left.ownerId === right.ownerId && left.timelineId === right.timelineId && left.generation === right.generation;
}

export function sameMailboxRecipient(left: MailboxRecipient, right: MailboxRecipient): boolean {
	return sameMailboxGeneration(left, right) && left.sessionId === right.sessionId && left.actorId === right.actorId;
}

export function laterMailboxTimestamp(left: string, right: string): string {
	validateMailboxTimestamp(left);
	validateMailboxTimestamp(right);
	return left < right ? right : left;
}

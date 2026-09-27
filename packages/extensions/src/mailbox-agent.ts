import { createHash } from "node:crypto";
import {
	canonicalizeJsonValue,
	normalizeJsonValue,
	WorkAuthorityRetiredError,
	type JsonValue,
} from "@opengameagent/protocol";
import { Mutex } from "async-mutex";
import { MailboxCapacityError, MailboxLeaseConflictError, MailboxValidationError } from "./mailbox-errors.js";
import type { MailboxService } from "./mailbox-service.js";
import type {
	MailboxDelivery,
	MailboxLimits,
	MailboxMessageRecord,
	MailboxMessageSequenceKey,
	MailboxRecipient,
} from "./mailbox-types.js";
import {
	normalizeMailboxMessage,
	normalizeMailboxRecipient,
	validateMailboxDecimal,
	validateMailboxIdentifier,
	validateMailboxTimestamp,
	validatePositiveMailboxKey,
} from "./mailbox-validation.js";

export interface MailboxGameInput {
	readonly sessionId: string;
	readonly actorId: string;
	readonly inputId: string;
	readonly payload: {
		readonly type: "mailbox.message";
		readonly deliveryAttempt: string;
		readonly message: {
			readonly messageKey: string;
			readonly messageId: string;
			readonly ownerId: string;
			readonly timelineId: string;
			readonly generation: string;
			readonly senderId?: string;
			readonly kind: string;
			readonly payload: JsonValue;
			readonly gameTime: MailboxMessageRecord["gameTime"];
			readonly correlationId?: string;
			readonly recordedAt: string;
		};
	};
}

export interface MailboxRunLeaseReceipt extends MailboxRecipient {
	readonly protocolVersion: "1";
	readonly messageKey: string;
	readonly leaseToken: string;
	readonly leaseOwnerId: string;
	readonly inputId: string;
}

interface HydratedMailboxRunLeaseReceipt extends MailboxMessageSequenceKey {
	readonly protocolVersion: "1";
	readonly leaseToken: string;
	readonly leaseOwnerId: string;
	readonly inputId: string;
}

interface GuardInput {
	readonly sessionId: string;
	readonly actorId: string;
	readonly inputId: string;
}

interface ActiveDelivery {
	delivery: MailboxDelivery;
	readonly input: MailboxGameInput;
}

export function mailboxDeliveryToGameInput(delivery: MailboxDelivery, limits: MailboxLimits): MailboxGameInput {
	const message = delivery.message;
	if (message.status !== "pending") throw new MailboxValidationError("invalid_mailbox_delivery");
	const input = mailboxMessageToGameInput(message, limits);
	validateMailboxIdentifier(delivery.leaseToken, limits.maxIdentifierBytes, "lease_token");
	validateMailboxIdentifier(delivery.leaseOwnerId, limits.maxIdentifierBytes, "lease_owner_id");
	validateMailboxTimestamp(delivery.leaseExpiresAt);
	if (
		message.completedAt !== undefined ||
		message.cancelledAt !== undefined ||
		delivery.leaseExpiresAt <= message.updatedAt
	) {
		throw new MailboxValidationError("invalid_mailbox_delivery");
	}
	return input;
}

function mailboxMessageToGameInput(message: MailboxMessageRecord, limits: MailboxLimits): MailboxGameInput {
	validatePositiveMailboxKey(message.messageKey, "message_key");
	validateMailboxDecimal(message.revision, "revision", limits.maxCounterDigits);
	validateMailboxDecimal(message.attempt, "attempt", limits.maxCounterDigits);
	if (message.attempt === "0") throw new MailboxValidationError("invalid_mailbox_delivery");
	validateMailboxTimestamp(message.createdAt);
	validateMailboxTimestamp(message.updatedAt);
	if (
		message.createdAt !== message.recordedAt ||
		message.updatedAt < message.createdAt ||
		BigInt(message.revision) <= BigInt(message.attempt)
	) {
		throw new MailboxValidationError("invalid_mailbox_delivery");
	}
	const normalized = normalizeMailboxMessage(message, limits);
	const inputIdentity = canonicalizeJsonValue({
		recipient: normalized.recipient,
		messageKey: message.messageKey.toString(),
		messageId: normalized.messageId,
		attempt: message.attempt,
	});
	return {
		sessionId: normalized.recipient.sessionId,
		actorId: normalized.recipient.actorId,
		inputId: createHash("sha256").update(inputIdentity).digest("hex"),
		payload: {
			type: "mailbox.message",
			deliveryAttempt: message.attempt,
			message: {
				messageKey: message.messageKey.toString(),
				messageId: normalized.messageId,
				ownerId: normalized.recipient.ownerId,
				timelineId: normalized.recipient.timelineId,
				generation: normalized.recipient.generation,
				...(normalized.senderId === undefined ? {} : { senderId: normalized.senderId }),
				kind: normalized.kind,
				payload: normalizeJsonValue(normalized.payload),
				gameTime: { ...normalized.gameTime },
				...(normalized.correlationId === undefined ? {} : { correlationId: normalized.correlationId }),
				recordedAt: normalized.recordedAt,
			},
		},
	};
}

export function mailboxDeliveryToRunLeaseReceipt(
	delivery: MailboxDelivery,
	limits: MailboxLimits,
): MailboxRunLeaseReceipt {
	const input = mailboxDeliveryToGameInput(delivery, limits);
	return {
		protocolVersion: "1",
		...delivery.message.recipient,
		messageKey: delivery.message.messageKey.toString(),
		leaseToken: delivery.leaseToken,
		leaseOwnerId: delivery.leaseOwnerId,
		inputId: input.inputId,
	};
}

export class MailboxRunLeaseRegistry {
	private readonly active = new Map<string, ActiveDelivery>();
	private readonly mutex = new Mutex();

	constructor(
		private readonly mailbox: MailboxService,
		private readonly operationalNow: () => string | Promise<string>,
	) {}

	async activate(delivery: MailboxDelivery, signal?: AbortSignal): Promise<MailboxGameInput> {
		return await this.mutex.runExclusive(async () => {
			signal?.throwIfAborted();
			const now = await this.operationalNow();
			signal?.throwIfAborted();
			const asserted = await this.mailbox.assertLease(messageSequenceKey(delivery.message), delivery.leaseToken, now);
			signal?.throwIfAborted();
			const input = mailboxDeliveryToGameInput(asserted, this.mailbox.limits);
			return this.register(asserted, input);
		});
	}

	async restore(receipt: unknown, signal?: AbortSignal): Promise<MailboxGameInput> {
		return await this.mutex.runExclusive(async () => {
			signal?.throwIfAborted();
			const normalized = normalizeRunLeaseReceipt(receipt, this.mailbox.limits);
			const now = await this.operationalNow();
			signal?.throwIfAborted();
			const asserted = await this.mailbox.assertLease(normalized, normalized.leaseToken, now);
			signal?.throwIfAborted();
			const input = mailboxDeliveryToGameInput(asserted, this.mailbox.limits);
			if (asserted.leaseOwnerId !== normalized.leaseOwnerId || input.inputId !== normalized.inputId) {
				throw new MailboxLeaseConflictError();
			}
			return this.register(asserted, input);
		});
	}

	readonly authorizeToolExecution = async (
		input: Readonly<GuardInput>,
		_execution: unknown,
		signal: AbortSignal,
	): Promise<void> => {
		signal.throwIfAborted();
		const key = inputMapKey(input);
		const active = await this.mutex.runExclusive(() => this.active.get(key));
		if (active === undefined) throw new MailboxLeaseConflictError();
		const now = await this.operationalNow();
		signal.throwIfAborted();
		const asserted = await this.mailbox.assertLease(
			messageSequenceKey(active.delivery.message),
			active.delivery.leaseToken,
			now,
		);
		await this.mutex.runExclusive(() => {
			if (this.active.get(key) !== active) throw new MailboxLeaseConflictError();
			active.delivery = asserted;
		});
	};

	async renew(receipt: unknown, operationalNow: string, leaseExpiresAt: string): Promise<MailboxDelivery> {
		return await this.mutex.runExclusive(async () => {
			const normalized = normalizeRunLeaseReceipt(receipt, this.mailbox.limits);
			const key = inputMapKey(normalized);
			const active = this.active.get(key);
			if (
				active !== undefined &&
				(active.delivery.leaseToken !== normalized.leaseToken ||
					active.delivery.leaseOwnerId !== normalized.leaseOwnerId)
			) {
				throw new MailboxLeaseConflictError();
			}
			const asserted = await this.mailbox.assertLease(normalized, normalized.leaseToken, operationalNow);
			const assertedInput = mailboxDeliveryToGameInput(asserted, this.mailbox.limits);
			if (asserted.leaseOwnerId !== normalized.leaseOwnerId || assertedInput.inputId !== normalized.inputId) {
				throw new MailboxLeaseConflictError();
			}
			const renewed = await this.mailbox.renew({
				...normalized,
				leaseToken: normalized.leaseToken,
				operationalNow,
				leaseExpiresAt,
			});
			const input = mailboxDeliveryToGameInput(renewed, this.mailbox.limits);
			if (renewed.leaseOwnerId !== normalized.leaseOwnerId || input.inputId !== normalized.inputId) {
				throw new MailboxLeaseConflictError();
			}
			if (active !== undefined) active.delivery = renewed;
			return renewed;
		});
	}

	async settle(
		receipt: unknown,
		status: "completed" | "aborted" | "failed",
		operationalNow: string,
	): Promise<MailboxMessageRecord> {
		return await this.mutex.runExclusive(async () => {
			const normalized = normalizeRunLeaseReceipt(receipt, this.mailbox.limits);
			const current = await this.mailbox.getByKey(normalized);
			if (current?.status !== "completed" && (await this.mailbox.readAuthorityRetirement(normalized))) {
				throw new WorkAuthorityRetiredError(normalizeMailboxRecipient(normalized, this.mailbox.limits));
			}
			if (current === undefined) throw new MailboxLeaseConflictError();
			const input = mailboxMessageToGameInput(current, this.mailbox.limits);
			if (input.inputId !== normalized.inputId) throw new MailboxLeaseConflictError();
			const key = inputMapKey(input);
			const active = this.active.get(key);
			if (
				active !== undefined &&
				(active.delivery.leaseToken !== normalized.leaseToken ||
					active.delivery.leaseOwnerId !== normalized.leaseOwnerId)
			) {
				throw new MailboxLeaseConflictError();
			}
			try {
				if (current.status === "completed") {
					if (status !== "completed") throw new MailboxLeaseConflictError();
					return await this.mailbox.complete(
						normalized,
						normalized.leaseToken,
						normalized.leaseOwnerId,
						operationalNow,
					);
				}
				if (current.status !== "pending") throw new MailboxLeaseConflictError();
				let asserted: MailboxDelivery;
				try {
					asserted = await this.mailbox.assertLease(normalized, normalized.leaseToken, operationalNow);
				} catch (error) {
					if (status === "completed" || !(error instanceof MailboxLeaseConflictError)) throw error;
					return await this.mailbox.abandon(normalized, normalized.leaseToken, normalized.leaseOwnerId, operationalNow);
				}
				const assertedInput = mailboxDeliveryToGameInput(asserted, this.mailbox.limits);
				if (asserted.leaseOwnerId !== normalized.leaseOwnerId || assertedInput.inputId !== normalized.inputId) {
					throw new MailboxLeaseConflictError();
				}
				return status === "completed"
					? await this.mailbox.complete(normalized, normalized.leaseToken, normalized.leaseOwnerId, operationalNow)
					: await this.mailbox.abandon(normalized, normalized.leaseToken, normalized.leaseOwnerId, operationalNow);
			} finally {
				if (active !== undefined && this.active.get(key) === active) this.active.delete(key);
			}
		});
	}

	async release(input: Readonly<GuardInput>): Promise<boolean> {
		return await this.mutex.runExclusive(() => this.active.delete(inputMapKey(input)));
	}

	private register(delivery: MailboxDelivery, input: MailboxGameInput): MailboxGameInput {
		const key = inputMapKey(input);
		const existing = this.active.get(key);
		if (existing !== undefined) {
			if (existing.delivery.leaseToken !== delivery.leaseToken) throw new MailboxLeaseConflictError();
			return structuredClone(existing.input);
		}
		if (this.active.size >= this.mailbox.limits.maxActiveDeliveries) {
			throw new MailboxCapacityError("active_deliveries");
		}
		this.active.set(key, { delivery, input });
		return structuredClone(input);
	}
}

function normalizeRunLeaseReceipt(receipt: unknown, limits: MailboxLimits): HydratedMailboxRunLeaseReceipt {
	if (receipt === null || typeof receipt !== "object" || Array.isArray(receipt)) {
		throw new MailboxValidationError("invalid_mailbox_run_lease_receipt");
	}
	const value = receipt as Partial<Record<keyof MailboxRunLeaseReceipt, unknown>>;
	if (value.protocolVersion !== "1") throw new MailboxValidationError("invalid_mailbox_run_lease_receipt");
	for (const field of [
		"ownerId",
		"timelineId",
		"generation",
		"sessionId",
		"actorId",
		"messageKey",
		"leaseToken",
		"leaseOwnerId",
		"inputId",
	] as const) {
		if (typeof value[field] !== "string") throw new MailboxValidationError("invalid_mailbox_run_lease_receipt");
	}
	const recipient = normalizeMailboxRecipient(value as unknown as MailboxMessageSequenceKey, limits);
	validateMailboxDecimal(value.messageKey as string, "message_key", limits.maxCounterDigits);
	const messageKey = BigInt(value.messageKey as string);
	validatePositiveMailboxKey(messageKey, "message_key");
	validateMailboxIdentifier(value.leaseToken as string, limits.maxIdentifierBytes, "lease_token");
	validateMailboxIdentifier(value.leaseOwnerId as string, limits.maxIdentifierBytes, "lease_owner_id");
	if (!/^[0-9a-f]{64}$/u.test(value.inputId as string)) {
		throw new MailboxValidationError("invalid_mailbox_run_lease_receipt");
	}
	return {
		protocolVersion: "1",
		...recipient,
		messageKey,
		leaseToken: value.leaseToken as string,
		leaseOwnerId: value.leaseOwnerId as string,
		inputId: value.inputId as string,
	};
}

function messageSequenceKey(message: MailboxMessageRecord): MailboxMessageSequenceKey {
	return { ...message.recipient, messageKey: message.messageKey };
}

function inputMapKey(input: Readonly<GuardInput>): string {
	return JSON.stringify([input.sessionId, input.actorId, input.inputId]);
}

import { randomUUID } from "node:crypto";
import {
	type WorkAuthorityLane,
	WorkAuthorityRetiredError,
	WorkAuthorityRetirementConflictError,
	type WorkAuthorityRetirementRequest,
} from "@opengameagent/protocol";
import {
	MailboxCapacityError,
	MailboxConflictError,
	MailboxLeaseConflictError,
	MailboxNotFoundError,
	MailboxOrderConflictError,
	MailboxRevisionConflictError,
	MailboxTerminalError,
	MailboxValidationError,
} from "./mailbox-errors.js";
import type {
	MailboxAuthorityRetirementRecord,
	MailboxClaimRequest,
	MailboxDelivery,
	MailboxLimits,
	MailboxListRequest,
	MailboxMessageDraft,
	MailboxMessageKey,
	MailboxMessageRecord,
	MailboxMessageSequenceKey,
	MailboxPage,
	MailboxRecipient,
	MailboxRenewRequest,
	MailboxStatusSummary,
	MailboxStore,
} from "./mailbox-types.js";
import {
	incrementMailboxDecimal,
	laterMailboxTimestamp,
	mailboxFingerprint,
	normalizeMailboxAuthorityRetirement,
	normalizeMailboxClaim,
	normalizeMailboxListScope,
	normalizeMailboxMessage,
	normalizeMailboxRecipient,
	normalizeMailboxRenew,
	normalizeMailboxStatusFilter,
	sameMailboxGeneration,
	sameMailboxRecipient,
	validateMailboxCursor,
	validateMailboxDecimal,
	validateMailboxIdentifier,
	validateMailboxLimits,
	validateMailboxMessageKey,
	validateMailboxMessageSequenceKey,
	validateMailboxPageLimit,
	validateMailboxTimestamp,
} from "./mailbox-validation.js";

interface MailboxLease {
	readonly token: string;
	readonly ownerId: string;
	readonly expiresAt: string;
}

export class InMemoryMailboxStore implements MailboxStore {
	private readonly messages = new Map<string, MailboxMessageRecord>();
	private readonly fingerprints = new Map<string, string>();
	private readonly leases = new Map<bigint, MailboxLease>();
	private readonly settledLeaseProofs = new Map<bigint, string>();
	private readonly releasedLeaseProofs = new Map<bigint, string>();
	private readonly retirements = new Map<string, MailboxAuthorityRetirementRecord>();
	private readonly retirementIds = new Map<string, string>();
	private nextMessageKey = 1n;

	constructor(
		private readonly limits: MailboxLimits,
		private readonly newLeaseToken: () => string = randomUUID,
	) {
		validateMailboxLimits(limits);
		this.limits = Object.freeze({ ...limits });
	}

	async enqueue(
		input: MailboxMessageDraft,
		maxPendingPerGeneration: number,
		maxPendingPerRecipient: number,
	): Promise<MailboxMessageRecord> {
		const draft = normalizeMailboxMessage(input, this.limits);
		this.assertAuthorityOpen(draft.recipient);
		validateCapacity(maxPendingPerGeneration, this.limits.maxPendingMessagesPerTimelineGeneration);
		validateCapacity(maxPendingPerRecipient, this.limits.maxPendingMessagesPerRecipient);
		const key = messageMapKey(draft.recipient, draft.messageId);
		const existing = this.messages.get(key);
		if (existing !== undefined) {
			if (this.fingerprints.get(key) !== mailboxFingerprint(draft)) throw new MailboxConflictError();
			return clone(existing);
		}
		const pending = [...this.messages.values()].filter((message) => message.status === "pending");
		if (
			pending.filter((message) => sameMailboxGeneration(message.recipient, draft.recipient)).length >=
			maxPendingPerGeneration
		) {
			throw new MailboxCapacityError("timeline_generation");
		}
		if (
			pending.filter((message) => sameMailboxRecipient(message.recipient, draft.recipient)).length >=
			maxPendingPerRecipient
		) {
			throw new MailboxCapacityError("recipient");
		}
		const record: MailboxMessageRecord = {
			...clone(draft),
			messageKey: this.nextMessageKey,
			status: "pending",
			revision: "1",
			attempt: "0",
			createdAt: draft.recordedAt,
			updatedAt: draft.recordedAt,
		};
		this.nextMessageKey += 1n;
		this.messages.set(key, record);
		this.fingerprints.set(key, mailboxFingerprint(draft));
		return clone(record);
	}

	async get(key: MailboxMessageKey): Promise<MailboxMessageRecord | undefined> {
		key = validateMailboxMessageKey(key, this.limits);
		const record = this.messages.get(messageMapKey(key, key.messageId));
		return record === undefined ? undefined : clone(record);
	}

	async getByKey(key: MailboxMessageSequenceKey): Promise<MailboxMessageRecord | undefined> {
		key = validateMailboxMessageSequenceKey(key, this.limits);
		const record = [...this.messages.values()].find(
			(candidate) => candidate.messageKey === key.messageKey && sameMailboxRecipient(candidate.recipient, key),
		);
		return record === undefined ? undefined : clone(record);
	}

	async list(request: MailboxListRequest): Promise<MailboxPage> {
		const scope = normalizeMailboxListScope(request, this.limits);
		const statuses = normalizeMailboxStatusFilter(request.statuses);
		validateMailboxCursor(request.afterMessageKey);
		validateMailboxPageLimit(request.limit, this.limits.maxPageSize, "invalid_mailbox_page_limit");
		const matches = [...this.messages.values()]
			.filter(
				(message) =>
					sameMailboxGeneration(message.recipient, scope) &&
					(scope.sessionId === undefined || message.recipient.sessionId === scope.sessionId) &&
					(scope.actorId === undefined || message.recipient.actorId === scope.actorId),
			)
			.sort((left, right) => (left.messageKey < right.messageKey ? -1 : 1))
			.filter(
				(message) =>
					(statuses === undefined || statuses.includes(message.status)) &&
					(request.afterMessageKey === undefined || message.messageKey > request.afterMessageKey),
			);
		const selected = matches.slice(0, request.limit);
		const next = matches.length > selected.length ? selected.at(-1)?.messageKey : undefined;
		return { records: selected.map(clone), ...(next === undefined ? {} : { nextAfterMessageKey: next }) };
	}

	async status(recipient: MailboxRecipient): Promise<MailboxStatusSummary> {
		const normalized = normalizeMailboxRecipient(recipient, this.limits);
		const messages = this.recipientMessages(normalized);
		const pending = messages.filter((message) => message.status === "pending");
		const latest = messages.at(-1);
		return {
			...normalized,
			pending: BigInt(pending.length),
			completed: BigInt(messages.filter((message) => message.status === "completed").length),
			cancelled: BigInt(messages.filter((message) => message.status === "cancelled").length),
			...(pending[0] === undefined ? {} : { oldestPendingMessageKey: pending[0].messageKey }),
			...(latest === undefined ? {} : { latestMessageKey: latest.messageKey }),
		};
	}

	async claim(request: MailboxClaimRequest): Promise<readonly MailboxDelivery[]> {
		const claim = normalizeMailboxClaim(request, this.limits);
		this.assertAuthorityOpen(claim);
		const selected: MailboxMessageRecord[] = [];
		for (const message of this.recipientMessages(claim)) {
			if (message.status !== "pending") continue;
			if (claim.operationalNow < message.updatedAt) throw new MailboxLeaseConflictError();
			const lease = this.leases.get(message.messageKey);
			if (lease !== undefined && lease.expiresAt > claim.operationalNow) break;
			selected.push(message);
			if (selected.length === claim.limit) break;
		}
		const deliveries = selected.map((message) => {
			const token = this.newLeaseToken();
			validateMailboxIdentifier(token, this.limits.maxIdentifierBytes, "lease_token");
			const updated: MailboxMessageRecord = {
				...message,
				revision: incrementMailboxDecimal(message.revision, this.limits.maxCounterDigits, "revision"),
				attempt: incrementMailboxDecimal(message.attempt, this.limits.maxCounterDigits, "attempt"),
				updatedAt: claim.operationalNow,
			};
			return {
				message: clone(updated),
				leaseToken: token,
				leaseOwnerId: claim.leaseOwnerId,
				leaseExpiresAt: claim.leaseExpiresAt,
			};
		});
		this.assertAuthorityOpen(claim);
		if (
			selected.some((message) => this.messages.get(messageMapKey(message.recipient, message.messageId)) !== message)
		) {
			throw new MailboxLeaseConflictError();
		}
		for (const delivery of deliveries) {
			const message = delivery.message;
			this.messages.set(messageMapKey(message.recipient, message.messageId), message);
			this.releasedLeaseProofs.delete(message.messageKey);
			this.leases.set(message.messageKey, {
				token: delivery.leaseToken,
				ownerId: delivery.leaseOwnerId,
				expiresAt: delivery.leaseExpiresAt,
			});
		}
		return clone(deliveries);
	}

	async renew(request: MailboxRenewRequest): Promise<MailboxDelivery> {
		const renewal = normalizeMailboxRenew(request, this.limits);
		this.assertAuthorityOpen(renewal);
		const current = this.findRequired(renewal);
		const lease = this.leases.get(current.messageKey);
		if (
			current.status !== "pending" ||
			lease?.token !== renewal.leaseToken ||
			renewal.operationalNow < current.updatedAt ||
			lease.expiresAt <= renewal.operationalNow
		) {
			throw new MailboxLeaseConflictError();
		}
		if (renewal.leaseExpiresAt === lease.expiresAt) {
			return {
				message: clone(current),
				leaseToken: lease.token,
				leaseOwnerId: lease.ownerId,
				leaseExpiresAt: lease.expiresAt,
			};
		}
		if (renewal.leaseExpiresAt < lease.expiresAt) throw new MailboxLeaseConflictError();
		const updated: MailboxMessageRecord = {
			...current,
			revision: incrementMailboxDecimal(current.revision, this.limits.maxCounterDigits, "revision"),
			updatedAt: renewal.operationalNow,
		};
		const nextLease = { ...lease, expiresAt: renewal.leaseExpiresAt };
		this.messages.set(messageMapKey(current.recipient, current.messageId), updated);
		this.leases.set(current.messageKey, nextLease);
		return {
			message: clone(updated),
			leaseToken: nextLease.token,
			leaseOwnerId: nextLease.ownerId,
			leaseExpiresAt: nextLease.expiresAt,
		};
	}

	async assertLease(
		key: MailboxMessageSequenceKey,
		leaseToken: string,
		operationalNow: string,
	): Promise<MailboxDelivery> {
		key = validateMailboxMessageSequenceKey(key, this.limits);
		validateMailboxIdentifier(leaseToken, this.limits.maxIdentifierBytes, "lease_token");
		validateMailboxTimestamp(operationalNow);
		this.assertAuthorityOpen(key);
		const current = this.findRequired(key);
		const lease = this.leases.get(current.messageKey);
		this.assertActiveLease(current, lease, leaseToken, operationalNow);
		if (
			this.recipientMessages(current.recipient).some(
				(message) => message.status === "pending" && message.messageKey < current.messageKey,
			)
		) {
			throw new MailboxOrderConflictError();
		}
		return {
			message: clone(current),
			leaseToken: lease.token,
			leaseOwnerId: lease.ownerId,
			leaseExpiresAt: lease.expiresAt,
		};
	}

	async complete(
		key: MailboxMessageSequenceKey,
		leaseToken: string,
		leaseOwnerId: string,
		operationalNow: string,
	): Promise<MailboxMessageRecord> {
		key = validateMailboxMessageSequenceKey(key, this.limits);
		validateMailboxIdentifier(leaseToken, this.limits.maxIdentifierBytes, "lease_token");
		validateMailboxIdentifier(leaseOwnerId, this.limits.maxIdentifierBytes, "lease_owner_id");
		validateMailboxTimestamp(operationalNow);
		const current = this.findRequired(key);
		const proof = leaseProofKey(leaseToken, leaseOwnerId);
		if (current.status === "completed") {
			if (this.settledLeaseProofs.get(current.messageKey) !== proof) throw new MailboxLeaseConflictError();
			return clone(current);
		}
		this.assertAuthorityOpen(key);
		const lease = this.leases.get(current.messageKey);
		this.assertActiveLease(current, lease, leaseToken, operationalNow);
		if (lease.ownerId !== leaseOwnerId) throw new MailboxLeaseConflictError();
		const earlierPending = this.recipientMessages(current.recipient).find(
			(message) => message.status === "pending" && message.messageKey < current.messageKey,
		);
		if (earlierPending !== undefined) throw new MailboxOrderConflictError();
		const completedAt = laterMailboxTimestamp(current.updatedAt, operationalNow);
		const updated: MailboxMessageRecord = {
			...current,
			status: "completed",
			revision: incrementMailboxDecimal(current.revision, this.limits.maxCounterDigits, "revision"),
			updatedAt: completedAt,
			completedAt,
		};
		this.messages.set(messageMapKey(current.recipient, current.messageId), updated);
		this.leases.delete(current.messageKey);
		this.releasedLeaseProofs.delete(current.messageKey);
		this.settledLeaseProofs.set(current.messageKey, proof);
		return clone(updated);
	}

	async abandon(
		key: MailboxMessageSequenceKey,
		leaseToken: string,
		leaseOwnerId: string,
		operationalNow: string,
	): Promise<MailboxMessageRecord> {
		key = validateMailboxMessageSequenceKey(key, this.limits);
		validateMailboxIdentifier(leaseToken, this.limits.maxIdentifierBytes, "lease_token");
		validateMailboxIdentifier(leaseOwnerId, this.limits.maxIdentifierBytes, "lease_owner_id");
		validateMailboxTimestamp(operationalNow);
		this.assertAuthorityOpen(key);
		const current = this.findRequired(key);
		const lease = this.leases.get(current.messageKey);
		const proof = leaseProofKey(leaseToken, leaseOwnerId);
		if (current.status === "pending" && lease === undefined) {
			if (this.releasedLeaseProofs.get(current.messageKey) !== proof) throw new MailboxLeaseConflictError();
			return clone(current);
		}
		this.assertActiveLease(current, lease, leaseToken, operationalNow);
		if (lease.ownerId !== leaseOwnerId) throw new MailboxLeaseConflictError();
		const updated: MailboxMessageRecord = {
			...current,
			revision: incrementMailboxDecimal(current.revision, this.limits.maxCounterDigits, "revision"),
			updatedAt: laterMailboxTimestamp(current.updatedAt, operationalNow),
		};
		this.messages.set(messageMapKey(current.recipient, current.messageId), updated);
		this.leases.delete(current.messageKey);
		this.releasedLeaseProofs.set(current.messageKey, proof);
		return clone(updated);
	}

	async cancel(
		key: MailboxMessageKey,
		expectedRevision: string,
		operationalNow: string,
	): Promise<MailboxMessageRecord> {
		key = validateMailboxMessageKey(key, this.limits);
		validateMailboxDecimal(expectedRevision, "revision", this.limits.maxCounterDigits);
		validateMailboxTimestamp(operationalNow);
		this.assertAuthorityOpen(key);
		const storageKey = messageMapKey(key, key.messageId);
		const current = this.messages.get(storageKey);
		if (current === undefined) throw new MailboxNotFoundError();
		if (current.revision !== expectedRevision) throw new MailboxRevisionConflictError();
		if (current.status !== "pending") throw new MailboxTerminalError();
		if (operationalNow < current.updatedAt) throw new MailboxLeaseConflictError();
		const lease = this.leases.get(current.messageKey);
		if (lease !== undefined && lease.expiresAt > operationalNow) throw new MailboxLeaseConflictError();
		const cancelledAt = laterMailboxTimestamp(current.updatedAt, operationalNow);
		const updated: MailboxMessageRecord = {
			...current,
			status: "cancelled",
			revision: incrementMailboxDecimal(current.revision, this.limits.maxCounterDigits, "revision"),
			updatedAt: cancelledAt,
			cancelledAt,
		};
		this.messages.set(storageKey, updated);
		this.leases.delete(current.messageKey);
		this.releasedLeaseProofs.delete(current.messageKey);
		return clone(updated);
	}

	async purgeTerminal(
		recipient: MailboxRecipient,
		beforeOperationalTime: string,
		limit: number,
	): Promise<readonly bigint[]> {
		recipient = normalizeMailboxRecipient(recipient, this.limits);
		validateMailboxTimestamp(beforeOperationalTime);
		validateMailboxPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_mailbox_purge_limit");
		const selected = this.recipientMessages(recipient)
			.filter((message) => message.status !== "pending" && message.updatedAt < beforeOperationalTime)
			.slice(0, limit);
		for (const message of selected) {
			const key = messageMapKey(message.recipient, message.messageId);
			this.messages.delete(key);
			this.fingerprints.delete(key);
			this.leases.delete(message.messageKey);
			this.settledLeaseProofs.delete(message.messageKey);
			this.releasedLeaseProofs.delete(message.messageKey);
		}
		return selected.map((message) => message.messageKey);
	}

	private recipientMessages(recipient: MailboxRecipient): MailboxMessageRecord[] {
		return [...this.messages.values()]
			.filter((message) => sameMailboxRecipient(message.recipient, recipient))
			.sort((left, right) => (left.messageKey < right.messageKey ? -1 : 1));
	}

	async beginAuthorityRetirement(request: WorkAuthorityRetirementRequest): Promise<MailboxAuthorityRetirementRecord> {
		return this.beginAuthorityRetirementSynchronously(request);
	}

	beginAuthorityRetirementSynchronously(input: WorkAuthorityRetirementRequest): MailboxAuthorityRetirementRecord {
		const request = normalizeMailboxAuthorityRetirement(input, this.limits);
		const existing = this.findRetirement(request);
		if (existing) return clone(existing);
		return this.commitRetirement(request, "fenced", 0);
	}

	async finalizeAuthorityRetirement(
		request: WorkAuthorityRetirementRequest,
	): Promise<MailboxAuthorityRetirementRecord> {
		return this.finalizeAuthorityRetirementSynchronously(request);
	}

	finalizeAuthorityRetirementSynchronously(input: WorkAuthorityRetirementRequest): MailboxAuthorityRetirementRecord {
		const request = normalizeMailboxAuthorityRetirement(input, this.limits);
		const existing = this.findRetirement(request);
		if (!existing) throw new WorkAuthorityRetirementConflictError();
		if (existing.phase === "finalized") return clone(existing);
		return this.commitRetirement(request, "finalized", existing.cancelledMessageCount);
	}

	async readAuthorityRetirement(partition: WorkAuthorityLane): Promise<MailboxAuthorityRetirementRecord | undefined> {
		const record = this.retirements.get(retirementMapKey(normalizeMailboxRecipient(partition, this.limits)));
		return record === undefined ? undefined : clone(record);
	}

	private findRetirement(request: WorkAuthorityRetirementRequest): MailboxAuthorityRetirementRecord | undefined {
		const key = retirementMapKey(request.partition);
		const idKey = this.retirementIds.get(request.retirementId);
		const existing = this.retirements.get(key);
		if (
			(idKey !== undefined && idKey !== key) ||
			(existing && (existing.retirementId !== request.retirementId || existing.recordedAt !== request.recordedAt))
		) {
			throw new WorkAuthorityRetirementConflictError();
		}
		return existing;
	}

	private commitRetirement(
		request: WorkAuthorityRetirementRequest,
		phase: "fenced" | "finalized",
		cancelled: number,
	): MailboxAuthorityRetirementRecord {
		const selected = this.recipientMessages(request.partition).filter(
			(message) => message.status === "pending" && (phase === "finalized" || !this.leases.has(message.messageKey)),
		);
		if (selected.length > this.limits.maxPendingMessagesPerRecipient) throw new MailboxCapacityError("recipient");
		if (selected.length > this.limits.maxPendingMessagesPerTimelineGeneration)
			throw new MailboxCapacityError("timeline_generation");
		const updates = selected.map((message): MailboxMessageRecord => {
			const cancelledAt = laterMailboxTimestamp(message.updatedAt, request.recordedAt);
			return {
				...message,
				status: "cancelled",
				cancelledAt,
				updatedAt: cancelledAt,
				revision: incrementMailboxDecimal(message.revision, this.limits.maxCounterDigits, "revision"),
			};
		});
		const record: MailboxAuthorityRetirementRecord = {
			...request,
			phase,
			cancelledMessageCount: cancelled + updates.length,
		};
		if (!Number.isSafeInteger(record.cancelledMessageCount))
			throw new MailboxValidationError("mailbox_retirement_count_overflow");
		this.retirements.set(retirementMapKey(request.partition), record);
		this.retirementIds.set(request.retirementId, retirementMapKey(request.partition));
		for (const message of updates) {
			this.messages.set(messageMapKey(message.recipient, message.messageId), message);
			this.leases.delete(message.messageKey);
			this.releasedLeaseProofs.delete(message.messageKey);
		}
		return clone(record);
	}

	private assertAuthorityOpen(partition: WorkAuthorityLane): void {
		if (this.retirements.has(retirementMapKey(partition)))
			throw new WorkAuthorityRetiredError(normalizeMailboxRecipient(partition, this.limits));
	}

	private findRequired(key: MailboxMessageSequenceKey): MailboxMessageRecord {
		const record = [...this.messages.values()].find(
			(candidate) => candidate.messageKey === key.messageKey && sameMailboxRecipient(candidate.recipient, key),
		);
		if (record === undefined) throw new MailboxNotFoundError();
		return record;
	}

	private assertActiveLease(
		message: MailboxMessageRecord,
		lease: MailboxLease | undefined,
		leaseToken: string,
		operationalNow: string,
	): asserts lease is MailboxLease {
		if (
			message.status !== "pending" ||
			lease?.token !== leaseToken ||
			operationalNow < message.updatedAt ||
			lease.expiresAt <= operationalNow
		) {
			throw new MailboxLeaseConflictError();
		}
	}
}

function messageMapKey(recipient: MailboxRecipient, messageId: string): string {
	return JSON.stringify([
		recipient.ownerId,
		recipient.timelineId,
		recipient.generation,
		recipient.sessionId,
		recipient.actorId,
		messageId,
	]);
}

function retirementMapKey(partition: WorkAuthorityLane): string {
	return JSON.stringify([
		partition.ownerId,
		partition.timelineId,
		partition.generation,
		partition.sessionId,
		partition.actorId,
	]);
}

function leaseProofKey(leaseToken: string, leaseOwnerId: string): string {
	return JSON.stringify([leaseToken, leaseOwnerId]);
}

function clone<T>(value: T): T {
	try {
		return structuredClone(value);
	} catch {
		throw new MailboxValidationError("mailbox_clone_failed");
	}
}

function validateCapacity(value: number, maximum: number): void {
	if (!Number.isSafeInteger(value) || value < 1 || value > maximum) {
		throw new MailboxValidationError("invalid_mailbox_capacity");
	}
}

import type { WorkAuthorityLane, WorkAuthorityRetirementRequest } from "@opengameagent/protocol";
import { MailboxNotFoundError, MailboxRevisionConflictError } from "./mailbox-errors.js";
import type {
	MailboxClaimRequest,
	MailboxAuthorityRetirementRecord,
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
	normalizeMailboxClaim,
	normalizeMailboxAuthorityRetirement,
	normalizeMailboxListScope,
	normalizeMailboxMessage,
	normalizeMailboxRecipient,
	normalizeMailboxRenew,
	normalizeMailboxStatusFilter,
	validateMailboxCursor,
	validateMailboxDecimal,
	validateMailboxIdentifier,
	validateMailboxLimits,
	validateMailboxMessageKey,
	validateMailboxMessageSequenceKey,
	validateMailboxPageLimit,
	validateMailboxTimestamp,
} from "./mailbox-validation.js";

export class MailboxService {
	readonly limits: MailboxLimits;

	constructor(
		private readonly store: MailboxStore,
		limits: MailboxLimits,
	) {
		validateMailboxLimits(limits);
		this.limits = Object.freeze({ ...limits });
	}

	async enqueue(input: MailboxMessageDraft): Promise<MailboxMessageRecord> {
		return await this.store.enqueue(
			normalizeMailboxMessage(input, this.limits),
			this.limits.maxPendingMessagesPerTimelineGeneration,
			this.limits.maxPendingMessagesPerRecipient,
		);
	}

	async beginAuthorityRetirement(request: WorkAuthorityRetirementRequest): Promise<MailboxAuthorityRetirementRecord> {
		return this.store.beginAuthorityRetirement(normalizeMailboxAuthorityRetirement(request, this.limits));
	}

	async finalizeAuthorityRetirement(
		request: WorkAuthorityRetirementRequest,
	): Promise<MailboxAuthorityRetirementRecord> {
		return this.store.finalizeAuthorityRetirement(normalizeMailboxAuthorityRetirement(request, this.limits));
	}

	async readAuthorityRetirement(partition: WorkAuthorityLane): Promise<MailboxAuthorityRetirementRecord | undefined> {
		return this.store.readAuthorityRetirement(normalizeMailboxRecipient(partition, this.limits));
	}

	async get(key: MailboxMessageKey): Promise<MailboxMessageRecord | undefined> {
		key = validateMailboxMessageKey(key, this.limits);
		return await this.store.get(key);
	}

	async getByKey(key: MailboxMessageSequenceKey): Promise<MailboxMessageRecord | undefined> {
		key = validateMailboxMessageSequenceKey(key, this.limits);
		return await this.store.getByKey(key);
	}

	async list(request: MailboxListRequest): Promise<MailboxPage> {
		const scope = normalizeMailboxListScope(request, this.limits);
		const statuses = normalizeMailboxStatusFilter(request.statuses);
		validateMailboxCursor(request.afterMessageKey);
		validateMailboxPageLimit(request.limit, this.limits.maxPageSize, "invalid_mailbox_page_limit");
		return await this.store.list({
			...scope,
			...(statuses === undefined ? {} : { statuses }),
			...(request.afterMessageKey === undefined ? {} : { afterMessageKey: request.afterMessageKey }),
			limit: request.limit,
		});
	}

	async status(recipient: MailboxRecipient): Promise<MailboxStatusSummary> {
		return await this.store.status(normalizeMailboxRecipient(recipient, this.limits));
	}

	async claim(request: MailboxClaimRequest): Promise<readonly MailboxDelivery[]> {
		return await this.store.claim(normalizeMailboxClaim(request, this.limits));
	}

	async assertLease(
		key: MailboxMessageSequenceKey,
		leaseToken: string,
		operationalNow: string,
	): Promise<MailboxDelivery> {
		key = validateMailboxMessageSequenceKey(key, this.limits);
		validateMailboxIdentifier(leaseToken, this.limits.maxIdentifierBytes, "lease_token");
		validateMailboxTimestamp(operationalNow);
		return await this.store.assertLease(key, leaseToken, operationalNow);
	}

	async renew(request: MailboxRenewRequest): Promise<MailboxDelivery> {
		return await this.store.renew(normalizeMailboxRenew(request, this.limits));
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
		return await this.store.complete(key, leaseToken, leaseOwnerId, operationalNow);
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
		return await this.store.abandon(key, leaseToken, leaseOwnerId, operationalNow);
	}

	async cancel(
		key: MailboxMessageKey,
		expectedRevision: string,
		operationalNow: string,
	): Promise<MailboxMessageRecord> {
		key = validateMailboxMessageKey(key, this.limits);
		validateMailboxDecimal(expectedRevision, "revision", this.limits.maxCounterDigits);
		validateMailboxTimestamp(operationalNow);
		const current = await this.store.get(key);
		if (current === undefined) throw new MailboxNotFoundError();
		if (current.revision !== expectedRevision) throw new MailboxRevisionConflictError();
		return await this.store.cancel(key, expectedRevision, operationalNow);
	}

	async purgeTerminal(
		recipient: MailboxRecipient,
		beforeOperationalTime: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const normalized = normalizeMailboxRecipient(recipient, this.limits);
		validateMailboxTimestamp(beforeOperationalTime);
		validateMailboxPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_mailbox_purge_limit");
		return await this.store.purgeTerminal(normalized, beforeOperationalTime, limit);
	}
}

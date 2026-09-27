import type { JsonValue } from "@opengameagent/protocol";
import { DelegationNotFoundError } from "./errors.js";
import type {
	DelegationClaimRequest,
	DelegationDelivery,
	DelegationDraft,
	DelegationKey,
	DelegationLimits,
	DelegationListRequest,
	DelegationPage,
	DelegationPartition,
	DelegationRecord,
	DelegationSequenceKey,
	DelegationStore,
} from "./types.js";
import {
	decimal,
	delegationCursor,
	delegationStatuses,
	freezeDelegationLimits,
	identifier,
	json,
	partition,
	positive,
	timestamp,
} from "./validation.js";

export class DelegationService {
	readonly limits: Readonly<DelegationLimits>;
	constructor(
		private readonly store: DelegationStore,
		limits: DelegationLimits,
	) {
		this.limits = freezeDelegationLimits(limits);
	}
	create(draft: DelegationDraft): Promise<DelegationRecord> {
		return this.store.create(draft, this.limits.maxPendingPerGeneration);
	}
	get(key: DelegationKey): Promise<DelegationRecord | undefined> {
		partition(key, this.limits);
		identifier(key.delegationId, this.limits, "delegation_id");
		return this.store.get(key);
	}
	async require(key: DelegationKey): Promise<DelegationRecord> {
		const record = await this.get(key);
		if (!record) throw new DelegationNotFoundError();
		return record;
	}
	list(request: DelegationListRequest): Promise<DelegationPage> {
		partition(request, this.limits);
		delegationStatuses(request.statuses);
		delegationCursor(request.afterDelegationKey);
		positive(request.limit, "list_limit");
		if (request.limit > this.limits.maxPageSize) throw new RangeError("delegation_page_too_large");
		return this.store.list(request);
	}
	claim(request: DelegationClaimRequest): Promise<DelegationDelivery> {
		partition(request, this.limits);
		identifier(request.delegationId, this.limits, "delegation_id");
		identifier(request.leaseOwnerId, this.limits, "lease_owner_id");
		timestamp(request.operationalNow);
		timestamp(request.leaseExpiresAt);
		return this.store.claim(request);
	}
	assertLease(key: DelegationSequenceKey, leaseToken: string, now: string): Promise<DelegationDelivery> {
		return this.store.assertLease(key, identifier(leaseToken, this.limits, "lease_token"), timestamp(now));
	}
	renew(
		key: DelegationSequenceKey,
		leaseToken: string,
		now: string,
		leaseExpiresAt: string,
	): Promise<DelegationDelivery> {
		return this.store.renew(
			key,
			identifier(leaseToken, this.limits, "lease_token"),
			timestamp(now),
			timestamp(leaseExpiresAt),
		);
	}
	bindRun(key: DelegationSequenceKey, token: string, runId: string, now: string): Promise<DelegationRecord> {
		return this.store.bindRun(
			key,
			identifier(token, this.limits, "lease_token"),
			identifier(runId, this.limits, "run_id"),
			timestamp(now),
		);
	}
	complete(key: DelegationSequenceKey, token: string, result: JsonValue, now: string): Promise<DelegationRecord> {
		return this.store.complete(
			key,
			identifier(token, this.limits, "lease_token"),
			json(result, this.limits.maxResultBytes, this.limits),
			timestamp(now),
		);
	}
	fail(key: DelegationSequenceKey, token: string, code: string, now: string): Promise<DelegationRecord> {
		return this.store.fail(
			key,
			identifier(token, this.limits, "lease_token"),
			identifier(code, this.limits, "failure_code"),
			timestamp(now),
		);
	}
	abandon(key: DelegationSequenceKey, token: string, now: string): Promise<DelegationRecord> {
		return this.store.abandon(key, identifier(token, this.limits, "lease_token"), timestamp(now));
	}
	cancel(key: DelegationKey, revision: string, now: string): Promise<DelegationRecord> {
		decimal(revision, this.limits, "revision");
		return this.store.cancel(key, revision, timestamp(now));
	}
	purgeTerminal(part: DelegationPartition, before: string, limit: number): Promise<readonly bigint[]> {
		partition(part, this.limits);
		timestamp(before);
		positive(limit, "purge_limit");
		if (limit > this.limits.maxPurgeBatchSize) throw new RangeError("delegation_purge_too_large");
		return this.store.purgeTerminal(part, before, limit);
	}
}

import { randomUUID } from "node:crypto";
import {
	DelegationCapacityError,
	DelegationConflictError,
	DelegationLeaseError,
	DelegationNotFoundError,
} from "./errors.js";
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
	fingerprint,
	increment,
	identifier,
	json,
	normalizeDelegationDraft,
	freezeDelegationLimits,
	partition,
	positive,
	timestamp,
} from "./validation.js";

interface Lease {
	readonly token: string;
	readonly ownerId: string;
	readonly expiresAt: string;
}

export class InMemoryDelegationStore implements DelegationStore {
	private readonly limits: Readonly<DelegationLimits>;
	private readonly records = new Map<string, DelegationRecord>();
	private readonly fingerprints = new Map<string, string>();
	private readonly leases = new Map<bigint, Lease>();
	private readonly settledTokens = new Map<bigint, string>();
	private nextKey = 1n;

	constructor(
		limits: DelegationLimits,
		private readonly newLeaseToken: () => string = randomUUID,
	) {
		this.limits = freezeDelegationLimits(limits);
	}

	async create(input: DelegationDraft, maxPending: number): Promise<DelegationRecord> {
		positive(maxPending, "max_pending");
		if (maxPending > this.limits.maxPendingPerGeneration) throw new DelegationCapacityError();
		const draft = normalizeDelegationDraft(input, this.limits);
		const key = mapKey(draft.partition, draft.delegationId);
		const existing = this.records.get(key);
		const source = fingerprint(draft);
		if (existing) {
			if (this.fingerprints.get(key) !== source) throw new DelegationConflictError();
			return clone(existing);
		}
		const active = this.partitionRecords(draft.partition).filter((record) => !terminal(record.status));
		if (active.length >= maxPending) throw new DelegationCapacityError();
		const record: DelegationRecord = {
			...draft,
			delegationKey: this.nextKey++,
			status: "pending",
			revision: "1",
			attempt: "0",
			updatedAt: draft.createdAt,
		};
		this.records.set(key, record);
		this.fingerprints.set(key, source);
		return clone(record);
	}

	async get(key: DelegationKey): Promise<DelegationRecord | undefined> {
		const record = this.records.get(
			mapKey(partition(key, this.limits), identifier(key.delegationId, this.limits, "delegation_id")),
		);
		return record ? clone(record) : undefined;
	}

	async list(request: DelegationListRequest): Promise<DelegationPage> {
		const part = partition(request, this.limits);
		const statuses = delegationStatuses(request.statuses);
		const after = delegationCursor(request.afterDelegationKey);
		positive(request.limit, "list_limit");
		if (request.limit > this.limits.maxPageSize) throw new DelegationCapacityError();
		const rows = this.partitionRecords(part).filter(
			(record) => (statuses === undefined || statuses.includes(record.status)) && record.delegationKey > after,
		);
		const records = rows.slice(0, request.limit);
		const next = rows.length > records.length ? records.at(-1)?.delegationKey : undefined;
		return { records: records.map(clone), ...(next === undefined ? {} : { nextAfterDelegationKey: next }) };
	}

	async claim(input: DelegationClaimRequest): Promise<DelegationDelivery> {
		const record = this.required(input);
		const now = timestamp(input.operationalNow);
		const expires = timestamp(input.leaseExpiresAt);
		if (expires <= now || terminal(record.status) || now < record.updatedAt) throw new DelegationLeaseError();
		const lease = this.leases.get(record.delegationKey);
		if (lease && lease.expiresAt > now) throw new DelegationLeaseError();
		const token = identifier(this.newLeaseToken(), this.limits, "lease_token");
		const ownerId = identifier(input.leaseOwnerId, this.limits, "lease_owner_id");
		const updated: DelegationRecord = {
			...record,
			status: "running",
			revision: increment(record.revision, this.limits),
			attempt: increment(record.attempt, this.limits),
			...(record.startedAt === undefined ? { startedAt: now } : {}),
			updatedAt: now,
		};
		this.set(updated);
		this.leases.set(record.delegationKey, { token, ownerId, expiresAt: expires });
		return { record: clone(updated), leaseToken: token, leaseOwnerId: ownerId, leaseExpiresAt: expires };
	}

	async assertLease(key: DelegationSequenceKey, token: string, now: string): Promise<DelegationDelivery> {
		const record = this.requiredSequence(key);
		const lease = this.activeLease(record, token, timestamp(now));
		return {
			record: clone(record),
			leaseToken: lease.token,
			leaseOwnerId: lease.ownerId,
			leaseExpiresAt: lease.expiresAt,
		};
	}

	async renew(
		key: DelegationSequenceKey,
		token: string,
		nowInput: string,
		expiresInput: string,
	): Promise<DelegationDelivery> {
		const record = this.requiredSequence(key);
		const now = timestamp(nowInput);
		const lease = this.activeLease(record, token, now);
		const expiresAt = timestamp(expiresInput);
		if (expiresAt < lease.expiresAt || expiresAt <= now) throw new DelegationLeaseError();
		if (expiresAt !== lease.expiresAt) {
			const updated = this.update(record, { updatedAt: now });
			this.leases.set(record.delegationKey, { ...lease, expiresAt });
			return { record: updated, leaseToken: token, leaseOwnerId: lease.ownerId, leaseExpiresAt: expiresAt };
		}
		return { record: clone(record), leaseToken: token, leaseOwnerId: lease.ownerId, leaseExpiresAt: lease.expiresAt };
	}

	async bindRun(key: DelegationSequenceKey, token: string, runId: string, now: string): Promise<DelegationRecord> {
		const record = this.requiredSequence(key);
		this.activeLease(record, token, timestamp(now));
		const childRunId = identifier(runId, this.limits, "child_run_id");
		if (record.childRunId !== undefined) {
			if (record.childRunId !== childRunId) throw new DelegationConflictError("delegation_run_conflict");
			return clone(record);
		}
		return this.update(record, { childRunId, updatedAt: timestamp(now) });
	}

	async complete(key: DelegationSequenceKey, token: string, result: unknown, now: string): Promise<DelegationRecord> {
		const record = this.requiredSequence(key);
		const normalized = json(result, this.limits.maxResultBytes, this.limits);
		const operationalNow = timestamp(now);
		if (record.status === "completed") {
			if (
				this.settledTokens.get(record.delegationKey) !== token ||
				fingerprint(record.result) !== fingerprint(normalized)
			)
				throw new DelegationLeaseError();
			return clone(record);
		}
		this.activeLease(record, token, operationalNow);
		return this.settle(record, "completed", operationalNow, { result: normalized }, token);
	}

	async fail(key: DelegationSequenceKey, token: string, code: string, now: string): Promise<DelegationRecord> {
		const record = this.requiredSequence(key);
		const failureCode = identifier(code, this.limits, "failure_code");
		const operationalNow = timestamp(now);
		if (record.status === "failed") {
			if (this.settledTokens.get(record.delegationKey) !== token || record.failureCode !== failureCode)
				throw new DelegationLeaseError();
			return clone(record);
		}
		this.activeLease(record, token, operationalNow);
		return this.settle(record, "failed", operationalNow, { failureCode }, token);
	}

	async abandon(key: DelegationSequenceKey, token: string, now: string): Promise<DelegationRecord> {
		const record = this.requiredSequence(key);
		this.activeLease(record, token, timestamp(now));
		this.leases.delete(record.delegationKey);
		return this.update(record, { status: "pending", updatedAt: timestamp(now) });
	}

	async cancel(key: DelegationKey, expectedRevision: string, now: string): Promise<DelegationRecord> {
		const record = this.required(key);
		if (record.revision !== decimal(expectedRevision, this.limits, "revision")) throw new DelegationConflictError();
		if (terminal(record.status)) throw new DelegationConflictError("delegation_terminal");
		if (now < record.updatedAt) throw new DelegationLeaseError();
		this.leases.delete(record.delegationKey);
		return this.settle(record, "cancelled", now, {});
	}

	async purgeTerminal(part: DelegationPartition, before: string, limit: number): Promise<readonly bigint[]> {
		partition(part, this.limits);
		timestamp(before);
		positive(limit, "purge_limit");
		if (limit > this.limits.maxPurgeBatchSize) throw new DelegationCapacityError();
		const selected = this.partitionRecords(part)
			.filter((r) => terminal(r.status) && r.updatedAt < before)
			.sort((left, right) =>
				left.updatedAt === right.updatedAt
					? left.delegationKey < right.delegationKey
						? -1
						: 1
					: left.updatedAt < right.updatedAt
						? -1
						: 1,
			)
			.slice(0, limit);
		for (const record of selected) {
			const key = mapKey(record.partition, record.delegationId);
			this.records.delete(key);
			this.fingerprints.delete(key);
			this.leases.delete(record.delegationKey);
			this.settledTokens.delete(record.delegationKey);
		}
		return selected.map((record) => record.delegationKey);
	}

	private settle(
		record: DelegationRecord,
		status: "completed" | "failed" | "cancelled",
		now: string,
		fields: Partial<DelegationRecord>,
		settledToken?: string,
	): DelegationRecord {
		const settledAt = timestamp(now);
		this.leases.delete(record.delegationKey);
		const updated = this.update(record, { ...fields, status, settledAt, updatedAt: settledAt });
		if (settledToken !== undefined) this.settledTokens.set(record.delegationKey, settledToken);
		return updated;
	}

	private update(record: DelegationRecord, fields: Partial<DelegationRecord>): DelegationRecord {
		const updated = { ...record, ...fields, revision: increment(record.revision, this.limits) };
		this.set(updated);
		return clone(updated);
	}

	private set(record: DelegationRecord): void {
		this.records.set(mapKey(record.partition, record.delegationId), record);
	}
	private required(key: DelegationKey): DelegationRecord {
		const record = this.records.get(
			mapKey(partition(key, this.limits), identifier(key.delegationId, this.limits, "delegation_id")),
		);
		if (!record) throw new DelegationNotFoundError();
		return record;
	}
	private requiredSequence(key: DelegationSequenceKey): DelegationRecord {
		const part = partition(key, this.limits);
		const record = this.partitionRecords(part).find((item) => item.delegationKey === key.delegationKey);
		if (!record) throw new DelegationNotFoundError();
		return record;
	}
	private activeLease(record: DelegationRecord, token: string, now: string): Lease {
		const lease = this.leases.get(record.delegationKey);
		if (record.status !== "running" || lease?.token !== token || now < record.updatedAt || lease.expiresAt <= now)
			throw new DelegationLeaseError();
		return lease;
	}
	private partitionRecords(part: DelegationPartition): DelegationRecord[] {
		return [...this.records.values()]
			.filter((r) => samePartition(r.partition, part))
			.sort((a, b) => (a.delegationKey < b.delegationKey ? -1 : 1));
	}
}

function mapKey(part: DelegationPartition, id: string): string {
	return JSON.stringify([part.ownerId, part.timelineId, part.generation, id]);
}
function samePartition(a: DelegationPartition, b: DelegationPartition): boolean {
	return a.ownerId === b.ownerId && a.timelineId === b.timelineId && a.generation === b.generation;
}
function terminal(status: DelegationRecord["status"]): boolean {
	return status === "completed" || status === "failed" || status === "cancelled";
}
function clone<T>(value: T): T {
	return structuredClone(value);
}

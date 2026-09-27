import { createHash, randomUUID } from "node:crypto";
import {
	DelegationCapacityError,
	DelegationConflictError,
	DelegationLeaseError,
	DelegationNotFoundError,
	type DelegationClaimRequest,
	type DelegationDelivery,
	type DelegationDraft,
	type DelegationKey,
	type DelegationLimits,
	type DelegationListRequest,
	type DelegationPage,
	type DelegationPartition,
	type DelegationRecord,
	type DelegationSequenceKey,
	type DelegationStore,
	decimal,
	delegationCursor,
	delegationStatuses,
	fingerprint,
	freezeDelegationLimits,
	identifier,
	increment,
	json,
	normalizeDelegationDraft,
	partition,
	positive,
	timestamp,
} from "@opengameagent/delegation";
import { canonicalizeJsonValue, parseStrictJsonText } from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const SQLITE_INTEGER_MAX = 9_223_372_036_854_775_807n;

const MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "delegation-store-v1",
		sql: `
CREATE TABLE oga_delegations (
 delegation_key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(delegation_key>0), delegation_id TEXT NOT NULL,
 owner_id TEXT NOT NULL,timeline_id TEXT NOT NULL,generation TEXT NOT NULL,record_json TEXT NOT NULL CHECK(json_valid(record_json)),
 source_hash TEXT NOT NULL CHECK(length(source_hash)=64),status TEXT NOT NULL CHECK(status IN ('pending','running','completed','failed','cancelled')),revision TEXT NOT NULL,
	updated_at TEXT NOT NULL,
 lease_token TEXT,lease_owner_id TEXT,lease_expires_at TEXT,settled_lease_hash TEXT CHECK(settled_lease_hash IS NULL OR length(settled_lease_hash)=64),
 UNIQUE(owner_id,timeline_id,generation,delegation_id),
 CHECK((lease_token IS NULL AND lease_owner_id IS NULL AND lease_expires_at IS NULL) OR (lease_token IS NOT NULL AND lease_owner_id IS NOT NULL AND lease_expires_at IS NOT NULL))
) STRICT;
CREATE INDEX oga_delegation_partition_status_key ON oga_delegations(owner_id,timeline_id,generation,status,delegation_key);
CREATE INDEX oga_delegation_partition_updated_key ON oga_delegations(owner_id,timeline_id,generation,updated_at,delegation_key);
`,
	},
];

interface Row {
	delegation_key: bigint;
	delegation_id: string;
	owner_id: string;
	timeline_id: string;
	generation: string;
	record_json: string;
	source_hash: string;
	status: string;
	revision: string;
	updated_at: string;
	lease_token: string | null;
	lease_owner_id: string | null;
	lease_expires_at: string | null;
	settled_lease_hash: string | null;
}

export class SqliteDelegationStore implements DelegationStore {
	private readonly limits: Readonly<DelegationLimits>;
	constructor(
		private readonly authority: SqliteAuthority,
		limits: DelegationLimits,
		private readonly newLeaseToken: () => string = randomUUID,
	) {
		this.limits = freezeDelegationLimits(limits);
		authority.install("delegation-store", MIGRATIONS);
	}

	async create(input: DelegationDraft, maxPending: number): Promise<DelegationRecord> {
		const draft = normalizeDelegationDraft(input, this.limits);
		positive(maxPending, "max_pending");
		if (maxPending > this.limits.maxPendingPerGeneration) throw new DelegationCapacityError();
		return this.authority.write((db) => {
			const old = find(db, draft.partition, draft.delegationId);
			const hash = digest(fingerprint(draft));
			if (old) {
				if (old.source_hash !== hash) throw new DelegationConflictError();
				return this.hydrate(old);
			}
			const count = countActive(db, draft.partition);
			if (count >= BigInt(maxPending)) throw new DelegationCapacityError();
			const base = { ...draft, status: "pending" as const, revision: "1", attempt: "0", updatedAt: draft.createdAt };
			const info = db
				.prepare(
					`INSERT INTO oga_delegations(delegation_id,owner_id,timeline_id,generation,record_json,source_hash,status,revision,updated_at) VALUES(?,?,?,?,?,?,?,?,?)`,
				)
				.run(
					draft.delegationId,
					draft.partition.ownerId,
					draft.partition.timelineId,
					draft.partition.generation,
					canonicalizeJsonValue(base),
					hash,
					"pending",
					"1",
					draft.createdAt,
				);
			const row = findByKey(db, BigInt(info.lastInsertRowid));
			if (!row) throw new DelegationNotFoundError();
			return this.hydrate(row);
		});
	}
	async get(key: DelegationKey): Promise<DelegationRecord | undefined> {
		const part = partition(key, this.limits);
		const id = identifier(key.delegationId, this.limits, "delegation_id");
		return this.authority.read((db) => {
			const row = find(db, part, id);
			return row ? this.hydrate(row) : undefined;
		});
	}
	async list(request: DelegationListRequest): Promise<DelegationPage> {
		const part = partition(request, this.limits);
		const statuses = delegationStatuses(request.statuses);
		const after = delegationCursor(request.afterDelegationKey);
		positive(request.limit, "list_limit");
		if (request.limit > this.limits.maxPageSize) throw new DelegationCapacityError();
		if (statuses?.length === 0) return { records: [] };
		if (after >= SQLITE_INTEGER_MAX) return { records: [] };
		return this.authority.read((db) => {
			const statusClause = statuses === undefined ? "" : ` AND status IN (${statuses.map(() => "?").join(",")})`;
			const statement = db.prepare(
				`SELECT * FROM oga_delegations WHERE owner_id=? AND timeline_id=? AND generation=? AND delegation_key>?${statusClause} ORDER BY delegation_key LIMIT ?`,
			);
			statement.safeIntegers(true);
			const rows = statement.all(
				part.ownerId,
				part.timelineId,
				part.generation,
				after,
				...(statuses ?? []),
				request.limit + 1,
			) as Row[];
			const selected = rows.slice(0, request.limit);
			const next = rows.length > selected.length ? selected.at(-1)?.delegation_key : undefined;
			return {
				records: selected.map((r) => this.hydrate(r)),
				...(next === undefined ? {} : { nextAfterDelegationKey: next }),
			};
		});
	}
	async claim(input: DelegationClaimRequest): Promise<DelegationDelivery> {
		return this.authority.write((db) => {
			const row = find(db, partition(input, this.limits), identifier(input.delegationId, this.limits, "delegation_id"));
			if (!row) throw new DelegationNotFoundError();
			const record = this.hydrate(row),
				now = timestamp(input.operationalNow),
				expires = timestamp(input.leaseExpiresAt);
			if (
				expires <= now ||
				terminal(record.status) ||
				now < record.updatedAt ||
				(row.lease_expires_at !== null && row.lease_expires_at > now)
			)
				throw new DelegationLeaseError();
			const token = identifier(this.newLeaseToken(), this.limits, "lease_token"),
				owner = identifier(input.leaseOwnerId, this.limits, "lease_owner_id");
			const updated = {
				...record,
				status: "running" as const,
				revision: increment(record.revision, this.limits),
				attempt: increment(record.attempt, this.limits),
				...(record.startedAt === undefined ? { startedAt: now } : {}),
				updatedAt: now,
			};
			this.write(db, row, updated, { token, owner, expires, settled: null });
			return { record: updated, leaseToken: token, leaseOwnerId: owner, leaseExpiresAt: expires };
		});
	}
	async assertLease(key: DelegationSequenceKey, token: string, now: string): Promise<DelegationDelivery> {
		return this.authority.read((db) => {
			const row = this.requiredSequence(db, key),
				record = this.hydrate(row);
			this.active(row, record, token, timestamp(now));
			return {
				record,
				leaseToken: token,
				leaseOwnerId: row.lease_owner_id as string,
				leaseExpiresAt: row.lease_expires_at as string,
			};
		});
	}
	async renew(
		key: DelegationSequenceKey,
		token: string,
		nowInput: string,
		expiresInput: string,
	): Promise<DelegationDelivery> {
		return this.authority.write((db) => {
			const row = this.requiredSequence(db, key),
				record = this.hydrate(row),
				now = timestamp(nowInput),
				expires = timestamp(expiresInput);
			this.active(row, record, token, now);
			if (expires < (row.lease_expires_at as string) || expires <= now) throw new DelegationLeaseError();
			if (expires === row.lease_expires_at)
				return { record, leaseToken: token, leaseOwnerId: row.lease_owner_id as string, leaseExpiresAt: expires };
			const updated = { ...record, revision: increment(record.revision, this.limits), updatedAt: now };
			this.write(db, row, updated, { token, owner: row.lease_owner_id as string, expires, settled: null });
			return {
				record: updated,
				leaseToken: token,
				leaseOwnerId: row.lease_owner_id as string,
				leaseExpiresAt: expires,
			};
		});
	}
	async bindRun(key: DelegationSequenceKey, token: string, runId: string, now: string): Promise<DelegationRecord> {
		return this.mutateLease(key, token, now, (record) => {
			const id = identifier(runId, this.limits, "run_id");
			if (record.childRunId !== undefined) {
				if (record.childRunId !== id) throw new DelegationConflictError("delegation_run_conflict");
				return record;
			}
			return {
				...record,
				childRunId: id,
				revision: increment(record.revision, this.limits),
				updatedAt: timestamp(now),
			};
		});
	}
	async complete(key: DelegationSequenceKey, token: string, result: unknown, now: string): Promise<DelegationRecord> {
		const value = json(result, this.limits.maxResultBytes, this.limits);
		const operationalNow = timestamp(now);
		return this.settle(
			key,
			token,
			"completed",
			operationalNow,
			{ result: value },
			digest(token),
			(r) => fingerprint(r.result) === fingerprint(value),
		);
	}
	async fail(key: DelegationSequenceKey, token: string, code: string, now: string): Promise<DelegationRecord> {
		const value = identifier(code, this.limits, "failure_code");
		const operationalNow = timestamp(now);
		return this.settle(
			key,
			token,
			"failed",
			operationalNow,
			{ failureCode: value },
			digest(token),
			(r) => r.failureCode === value,
		);
	}
	async abandon(key: DelegationSequenceKey, token: string, now: string): Promise<DelegationRecord> {
		return this.mutateLease(
			key,
			token,
			now,
			(r) => ({ ...r, status: "pending", revision: increment(r.revision, this.limits), updatedAt: timestamp(now) }),
			true,
		);
	}
	async cancel(key: DelegationKey, expected: string, nowInput: string): Promise<DelegationRecord> {
		return this.authority.write((db) => {
			const row = find(db, partition(key, this.limits), identifier(key.delegationId, this.limits, "delegation_id"));
			if (!row) throw new DelegationNotFoundError();
			const r = this.hydrate(row),
				now = timestamp(nowInput);
			if (r.revision !== decimal(expected, this.limits, "revision")) throw new DelegationConflictError();
			if (terminal(r.status)) throw new DelegationConflictError("delegation_terminal");
			if (now < r.updatedAt) throw new DelegationLeaseError();
			const updated = {
				...r,
				status: "cancelled" as const,
				revision: increment(r.revision, this.limits),
				settledAt: now,
				updatedAt: now,
			};
			this.write(db, row, updated, { token: null, owner: null, expires: null, settled: null });
			return updated;
		});
	}
	async purgeTerminal(partInput: DelegationPartition, beforeInput: string, limit: number): Promise<readonly bigint[]> {
		const part = partition(partInput, this.limits),
			before = timestamp(beforeInput);
		positive(limit, "purge_limit");
		if (limit > this.limits.maxPurgeBatchSize) throw new DelegationCapacityError();
		return this.authority.write((db) => {
			const statement = db.prepare(
				`SELECT * FROM oga_delegations WHERE owner_id=? AND timeline_id=? AND generation=? AND status IN ('completed','failed','cancelled') AND updated_at<? ORDER BY updated_at,delegation_key LIMIT ?`,
			);
			statement.safeIntegers(true);
			const rows = statement.all(part.ownerId, part.timelineId, part.generation, before, limit) as Row[];
			const keys = rows.map((r) => {
				this.hydrate(r);
				return r.delegation_key;
			});
			const del = db.prepare("DELETE FROM oga_delegations WHERE delegation_key=?");
			for (const key of keys) del.run(key);
			return keys;
		});
	}

	private async mutateLease(
		key: DelegationSequenceKey,
		token: string,
		now: string,
		change: (r: DelegationRecord) => DelegationRecord,
		clear = false,
	): Promise<DelegationRecord> {
		return this.authority.write((db) => {
			const row = this.requiredSequence(db, key),
				r = this.hydrate(row);
			this.active(row, r, token, timestamp(now));
			const updated = change(r);
			this.write(
				db,
				row,
				updated,
				clear
					? { token: null, owner: null, expires: null, settled: null }
					: { token, owner: row.lease_owner_id as string, expires: row.lease_expires_at as string, settled: null },
			);
			return updated;
		});
	}
	private async settle(
		key: DelegationSequenceKey,
		token: string,
		status: "completed" | "failed",
		now: string,
		fields: Partial<DelegationRecord>,
		hash: string,
		same: (r: DelegationRecord) => boolean,
	): Promise<DelegationRecord> {
		return this.authority.write((db) => {
			const row = this.requiredSequence(db, key),
				r = this.hydrate(row);
			if (r.status === status) {
				if (row.settled_lease_hash !== hash || !same(r)) throw new DelegationLeaseError();
				return r;
			}
			this.active(row, r, token, timestamp(now));
			const updated = {
				...r,
				...fields,
				status,
				revision: increment(r.revision, this.limits),
				settledAt: timestamp(now),
				updatedAt: timestamp(now),
			} as DelegationRecord;
			this.write(db, row, updated, { token: null, owner: null, expires: null, settled: hash });
			return updated;
		});
	}
	private write(
		db: Database.Database,
		row: Row,
		record: DelegationRecord,
		lease: { token: string | null; owner: string | null; expires: string | null; settled: string | null },
	) {
		const info = db
			.prepare(
				`UPDATE oga_delegations SET record_json=?,status=?,revision=?,updated_at=?,lease_token=?,lease_owner_id=?,lease_expires_at=?,settled_lease_hash=? WHERE delegation_key=? AND revision=?`,
			)
			.run(
				stored(record),
				record.status,
				record.revision,
				record.updatedAt,
				lease.token,
				lease.owner,
				lease.expires,
				lease.settled,
				row.delegation_key,
				row.revision,
			);
		if (info.changes !== 1) throw new DelegationConflictError();
	}
	private requiredSequence(db: Database.Database, key: DelegationSequenceKey): Row {
		const part = partition(key, this.limits);
		if (typeof key.delegationKey !== "bigint" || key.delegationKey < 1n || key.delegationKey > SQLITE_INTEGER_MAX) {
			throw new DelegationNotFoundError();
		}
		const row = findSequence(db, { ...part, delegationKey: key.delegationKey });
		if (!row) throw new DelegationNotFoundError();
		return row;
	}
	private active(row: Row, r: DelegationRecord, token: string, now: string) {
		if (
			r.status !== "running" ||
			row.lease_token !== token ||
			row.lease_expires_at === null ||
			now < r.updatedAt ||
			row.lease_expires_at <= now
		)
			throw new DelegationLeaseError();
	}
	private hydrate(row: Row): DelegationRecord {
		try {
			return this.hydrateChecked(row);
		} catch {
			throw new DelegationConflictError("corrupt_delegation_record");
		}
	}
	private hydrateChecked(row: Row): DelegationRecord {
		let raw: unknown;
		try {
			raw = parseStrictJsonText(row.record_json, this.limits.maxJsonDepth + 1);
		} catch {
			throw new DelegationConflictError("corrupt_delegation_record");
		}
		if (!raw || typeof raw !== "object" || Array.isArray(raw))
			throw new DelegationConflictError("corrupt_delegation_record");
		const source = raw as unknown as DelegationRecord;
		const draft = normalizeDelegationDraft(source, this.limits);
		const r = {
			...draft,
			delegationKey: row.delegation_key,
			status: source.status,
			revision: source.revision,
			attempt: source.attempt,
			...(source.childRunId === undefined ? {} : { childRunId: source.childRunId }),
			...(source.result === undefined ? {} : { result: source.result }),
			...(source.failureCode === undefined ? {} : { failureCode: source.failureCode }),
			...(source.startedAt === undefined ? {} : { startedAt: source.startedAt }),
			...(source.settledAt === undefined ? {} : { settledAt: source.settledAt }),
			updatedAt: source.updatedAt,
		} as DelegationRecord;
		if (
			row.delegation_key < 1n ||
			row.delegation_id !== draft.delegationId ||
			row.owner_id !== draft.partition.ownerId ||
			row.timeline_id !== draft.partition.timelineId ||
			row.generation !== draft.partition.generation ||
			r.status !== row.status ||
			r.revision !== row.revision ||
			r.updatedAt !== row.updated_at ||
			digest(fingerprint(draft)) !== row.source_hash ||
			stored(r) !== row.record_json
		)
			throw new DelegationConflictError("corrupt_delegation_record");
		decimal(r.revision, this.limits, "revision");
		decimal(r.attempt, this.limits, "attempt");
		if (r.revision === "0" || BigInt(r.revision) <= BigInt(r.attempt))
			throw new DelegationConflictError("corrupt_delegation_record");
		timestamp(r.updatedAt);
		if (r.updatedAt < r.createdAt) throw new DelegationConflictError("corrupt_delegation_record");
		if (r.childRunId !== undefined) identifier(r.childRunId, this.limits, "child_run_id");
		if (r.childRunId !== undefined && r.startedAt === undefined)
			throw new DelegationConflictError("corrupt_delegation_record");
		if (r.startedAt !== undefined) {
			timestamp(r.startedAt);
			if (r.startedAt < r.createdAt || r.startedAt > r.updatedAt)
				throw new DelegationConflictError("corrupt_delegation_record");
		}
		if (r.settledAt !== undefined) timestamp(r.settledAt);
		const activeLease = row.lease_token !== null && row.lease_owner_id !== null && row.lease_expires_at !== null;
		if ((r.status === "running") !== activeLease) throw new DelegationConflictError("corrupt_delegation_record");
		if (activeLease) {
			identifier(row.lease_token as string, this.limits, "lease_token");
			identifier(row.lease_owner_id as string, this.limits, "lease_owner_id");
			timestamp(row.lease_expires_at as string);
			if (r.attempt === "0" || (row.lease_expires_at as string) <= r.updatedAt)
				throw new DelegationConflictError("corrupt_delegation_record");
		}
		if (r.status === "pending") {
			if (
				r.settledAt !== undefined ||
				r.result !== undefined ||
				r.failureCode !== undefined ||
				row.settled_lease_hash !== null
			)
				throw new DelegationConflictError("corrupt_delegation_record");
			if (
				(r.attempt === "0" && (r.revision !== "1" || r.startedAt !== undefined || r.childRunId !== undefined)) ||
				(r.attempt !== "0" && r.startedAt === undefined)
			)
				throw new DelegationConflictError("corrupt_delegation_record");
		} else if (r.status === "running") {
			if (
				r.startedAt === undefined ||
				r.settledAt !== undefined ||
				r.result !== undefined ||
				r.failureCode !== undefined ||
				row.settled_lease_hash !== null
			)
				throw new DelegationConflictError("corrupt_delegation_record");
		} else if (r.status === "completed") {
			if (
				r.startedAt === undefined ||
				r.result === undefined ||
				r.failureCode !== undefined ||
				r.settledAt !== r.updatedAt ||
				!sha(row.settled_lease_hash)
			)
				throw new DelegationConflictError("corrupt_delegation_record");
			json(r.result, this.limits.maxResultBytes, this.limits);
		} else if (r.status === "failed") {
			if (
				r.startedAt === undefined ||
				r.result !== undefined ||
				r.failureCode === undefined ||
				r.settledAt !== r.updatedAt ||
				!sha(row.settled_lease_hash)
			)
				throw new DelegationConflictError("corrupt_delegation_record");
			identifier(r.failureCode, this.limits, "failure_code");
		} else if (r.status === "cancelled") {
			if (
				r.result !== undefined ||
				r.failureCode !== undefined ||
				r.settledAt !== r.updatedAt ||
				row.settled_lease_hash !== null
			)
				throw new DelegationConflictError("corrupt_delegation_record");
		} else throw new DelegationConflictError("corrupt_delegation_record");
		return r as DelegationRecord;
	}
}
function stored(r: DelegationRecord): string {
	const { delegationKey: _k, ...rest } = r;
	return canonicalizeJsonValue(rest);
}
function digest(v: string): string {
	return createHash("sha256").update(v).digest("hex");
}
function terminal(s: string) {
	return s === "completed" || s === "failed" || s === "cancelled";
}
function sha(value: string | null): boolean {
	return value !== null && /^[0-9a-f]{64}$/u.test(value);
}
function countActive(db: Database.Database, p: DelegationPartition): bigint {
	const q = db
		.prepare(
			`SELECT count(*) FROM oga_delegations WHERE owner_id=? AND timeline_id=? AND generation=? AND status IN ('pending','running')`,
		)
		.pluck();
	q.safeIntegers(true);
	return q.get(p.ownerId, p.timelineId, p.generation) as bigint;
}
function find(db: Database.Database, p: DelegationPartition, id: string): Row | undefined {
	const q = db.prepare(
		`SELECT * FROM oga_delegations WHERE owner_id=? AND timeline_id=? AND generation=? AND delegation_id=?`,
	);
	q.safeIntegers(true);
	return q.get(p.ownerId, p.timelineId, p.generation, id) as Row | undefined;
}
function findByKey(db: Database.Database, key: bigint): Row | undefined {
	const q = db.prepare("SELECT * FROM oga_delegations WHERE delegation_key=?");
	q.safeIntegers(true);
	return q.get(key) as Row | undefined;
}
function findSequence(db: Database.Database, k: DelegationSequenceKey): Row | undefined {
	const q = db.prepare(
		`SELECT * FROM oga_delegations WHERE delegation_key=? AND owner_id=? AND timeline_id=? AND generation=?`,
	);
	q.safeIntegers(true);
	return q.get(k.delegationKey, k.ownerId, k.timelineId, k.generation) as Row | undefined;
}

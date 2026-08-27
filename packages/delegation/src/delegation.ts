import { randomBytes } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import type { GameMoment, GameSessionKey, JsonValue } from "@opengameagent/protocol";

export type GameDelegationStatus = "pending" | "running" | "completed" | "failed" | "cancelled";

export interface GameDelegationRequest {
	id: string;
	session: GameSessionKey;
	parentInputId: string;
	parentRunId: string;
	parentTurn: number;
	parentMoment: GameMoment;
	delegateId: string;
	task: JsonValue;
	depth: number;
	maximumTurns: number;
	inheritContext: boolean;
	inheritedContext?: JsonValue;
	parentDelegationId?: string;
	rootDelegationId: string;
}

export interface GameDelegationLease {
	workerId: string;
	token: string;
	fencingToken: number;
	expiresAt: number;
}

export interface GameDelegationRecord {
	request: GameDelegationRequest;
	status: GameDelegationStatus;
	revision: number;
	attempt: number;
	createdAt: number;
	updatedAt: number;
	lease?: GameDelegationLease;
	result?: JsonValue;
	error?: string;
}

export type GameDelegationClaim =
	| { kind: "leased"; record: GameDelegationRecord; lease: GameDelegationLease }
	| { kind: "busy" | "terminal"; record: GameDelegationRecord };

export interface GameDelegationOutcome {
	status: "completed" | "failed" | "cancelled";
	result?: JsonValue;
	error?: string;
}

export interface GameDelegationStore {
	create(request: GameDelegationRequest, signal?: AbortSignal): Promise<GameDelegationRecord>;
	claim(
		session: GameSessionKey,
		delegationId: string,
		workerId: string,
		operationalNow: number,
		leaseMilliseconds: number,
		signal?: AbortSignal,
	): Promise<GameDelegationClaim>;
	renew(
		session: GameSessionKey,
		delegationId: string,
		lease: GameDelegationLease,
		operationalNow: number,
		leaseMilliseconds: number,
		signal?: AbortSignal,
	): Promise<GameDelegationLease>;
	isLeaseAuthoritative(
		session: GameSessionKey,
		delegationId: string,
		lease: GameDelegationLease,
		operationalNow: number,
		signal?: AbortSignal,
	): Promise<boolean>;
	settle(
		session: GameSessionKey,
		delegationId: string,
		lease: GameDelegationLease,
		outcome: GameDelegationOutcome,
		operationalNow: number,
		signal?: AbortSignal,
	): Promise<GameDelegationRecord>;
	cancel(
		session: GameSessionKey,
		delegationId: string,
		reason: string,
		signal?: AbortSignal,
	): Promise<GameDelegationRecord>;
	read(session: GameSessionKey, delegationId: string, signal?: AbortSignal): Promise<GameDelegationRecord | undefined>;
	list(
		session: GameSessionKey,
		maximum: number,
		rootDelegationId?: string,
		signal?: AbortSignal,
	): Promise<readonly GameDelegationRecord[]>;
	listRecoverable(
		operationalNow: number,
		maximum: number,
		signal?: AbortSignal,
	): Promise<readonly GameDelegationRecord[]>;
}

export interface SqliteGameDelegationStoreOptions {
	maximumActivePerSession?: number;
	terminalRetentionPerSession?: number;
	maximumRequestBytes?: number;
	maximumResultBytes?: number;
}

interface DelegationRow {
	record_json: string;
}

const terminalStatuses = new Set<GameDelegationStatus>(["completed", "failed", "cancelled"]);

function clone<T>(value: T): T {
	return structuredClone(value);
}

function boundedInteger(value: number, minimum: number, maximum: number, name: string): void {
	if (!Number.isInteger(value) || value < minimum || value > maximum) throw new RangeError(`${name} is invalid.`);
}

function boundedId(value: string, name: string, maximum = 192): void {
	if (!/^[a-z0-9][a-z0-9._:-]*$/iu.test(value) || value.length > maximum)
		throw new TypeError(`${name} is not a portable bounded identifier.`);
}

function validateSession(session: GameSessionKey): void {
	boundedId(session.worldId, "World id");
	boundedId(session.saveId, "Save id");
	boundedId(session.timelineId, "Timeline id");
	boundedId(session.ownerId, "Owner id");
	boundedId(session.sessionId, "Session id");
	boundedId(session.actorId, "Actor id");
	boundedInteger(session.generation, 0, Number.MAX_SAFE_INTEGER, "Session generation");
}

function serializedBytes(value: unknown): number {
	return Buffer.byteLength(JSON.stringify(value), "utf8");
}

function validateRequest(request: GameDelegationRequest, maximumBytes: number): void {
	validateSession(request.session);
	boundedId(request.id, "Delegation id", 256);
	boundedId(request.parentInputId, "Parent input id", 256);
	boundedId(request.parentRunId, "Parent run id", 256);
	boundedInteger(request.parentTurn, 0, 1_000_000, "Parent turn");
	if (!Number.isFinite(request.parentMoment.tick)) throw new RangeError("Parent game tick is invalid.");
	boundedId(request.delegateId, "Delegate id", 192);
	boundedInteger(request.depth, 1, 16, "Delegation depth");
	boundedInteger(request.maximumTurns, 1, 128, "Delegation maximum turns");
	if (!request.inheritContext && request.inheritedContext !== undefined)
		throw new Error("Inherited context requires explicit inheritance.");
	if (request.parentDelegationId !== undefined) boundedId(request.parentDelegationId, "Parent delegation id", 256);
	boundedId(request.rootDelegationId, "Root delegation id", 256);
	if (request.parentDelegationId === undefined && request.rootDelegationId !== request.id)
		throw new Error("A root delegation must identify itself as the root.");
	if (request.parentDelegationId === request.id) throw new Error("A delegation cannot be its own parent.");
	if (serializedBytes(request) > maximumBytes)
		throw new RangeError("Delegation request exceeds its configured byte limit.");
}

function validateOperationalTime(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${name} is invalid.`);
}

function validateLeaseRequest(operationalNow: number, leaseMilliseconds: number): void {
	validateOperationalTime(operationalNow, "Operational time");
	boundedInteger(leaseMilliseconds, 100, 86_400_000, "Lease duration");
	if (!Number.isSafeInteger(operationalNow + leaseMilliseconds)) throw new RangeError("Lease expiry is invalid.");
}

function validateWorker(workerId: string): void {
	boundedId(workerId, "Worker id", 256);
}

function validateLeaseToken(token: string): void {
	if (!/^[a-z0-9_-]{16,256}$/iu.test(token)) throw new TypeError("Lease token is not valid base64url data.");
}

function validateOutcome(outcome: GameDelegationOutcome, maximumResultBytes: number): void {
	if (outcome.status === "completed") {
		if (outcome.error !== undefined) throw new TypeError("A completed delegation cannot contain an error.");
	} else if (!outcome.error || outcome.error.length > 4_096) {
		throw new TypeError("A failed or cancelled delegation requires a bounded error.");
	}
	if (outcome.result !== undefined && serializedBytes(outcome.result) > maximumResultBytes)
		throw new RangeError("Delegation result exceeds its configured byte limit.");
}

function canonicalJson(value: unknown): string {
	if (value === null || typeof value !== "object") return JSON.stringify(value);
	if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
	return `{${Object.entries(value)
		.filter(([, item]) => item !== undefined)
		.sort(([left], [right]) => left.localeCompare(right))
		.map(([key, item]) => `${JSON.stringify(key)}:${canonicalJson(item)}`)
		.join(",")}}`;
}

function sameRequest(left: GameDelegationRequest, right: GameDelegationRequest): boolean {
	return canonicalJson(left) === canonicalJson(right);
}

function sameSession(left: GameSessionKey, right: GameSessionKey): boolean {
	return (
		left.worldId === right.worldId &&
		left.saveId === right.saveId &&
		left.timelineId === right.timelineId &&
		left.generation === right.generation &&
		left.ownerId === right.ownerId &&
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId
	);
}

export class SqliteGameDelegationStore implements GameDelegationStore, Disposable {
	private readonly database: DatabaseSync;
	private readonly maximumActivePerSession: number;
	private readonly terminalRetentionPerSession: number;
	private readonly maximumRequestBytes: number;
	private readonly maximumResultBytes: number;
	private closed = false;

	constructor(path: string, options: SqliteGameDelegationStoreOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.maximumActivePerSession = options.maximumActivePerSession ?? 64;
		this.terminalRetentionPerSession = options.terminalRetentionPerSession ?? 128;
		this.maximumRequestBytes = options.maximumRequestBytes ?? 512 * 1024;
		this.maximumResultBytes = options.maximumResultBytes ?? 512 * 1024;
		boundedInteger(this.maximumActivePerSession, 1, 4_096, "maximumActivePerSession");
		boundedInteger(this.terminalRetentionPerSession, 0, 100_000, "terminalRetentionPerSession");
		boundedInteger(this.maximumRequestBytes, 1_024, 16 * 1024 * 1024, "maximumRequestBytes");
		boundedInteger(this.maximumResultBytes, 1_024, 16 * 1024 * 1024, "maximumResultBytes");
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
		);
		this.database.exec(`CREATE TABLE IF NOT EXISTS game_delegations (
			world_id TEXT NOT NULL,save_id TEXT NOT NULL,timeline_id TEXT NOT NULL,generation INTEGER NOT NULL,
			owner_id TEXT NOT NULL,session_id TEXT NOT NULL,actor_id TEXT NOT NULL,delegation_id TEXT NOT NULL,
			root_id TEXT NOT NULL,parent_id TEXT,delegate_id TEXT NOT NULL,status TEXT NOT NULL,revision INTEGER NOT NULL,
			attempt INTEGER NOT NULL,fencing_token INTEGER NOT NULL,lease_worker TEXT,lease_token TEXT,lease_expires_at INTEGER,
			created_at INTEGER NOT NULL,updated_at INTEGER NOT NULL,record_json TEXT NOT NULL,
			PRIMARY KEY(world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,delegation_id),
			CHECK(status IN ('pending','running','completed','failed','cancelled')),
			CHECK((lease_worker IS NULL AND lease_token IS NULL AND lease_expires_at IS NULL)
				OR (lease_worker IS NOT NULL AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)),
			CHECK(status='running' OR lease_token IS NULL)
		) STRICT;
		CREATE INDEX IF NOT EXISTS game_delegations_session ON game_delegations(
			world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,status,updated_at DESC);
		CREATE INDEX IF NOT EXISTS game_delegations_recovery ON game_delegations(status,lease_expires_at,updated_at,delegation_id);
		CREATE INDEX IF NOT EXISTS game_delegations_root ON game_delegations(
			world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,root_id,created_at,delegation_id);`);
	}

	async create(request: GameDelegationRequest, signal?: AbortSignal): Promise<GameDelegationRecord> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateRequest(request, this.maximumRequestBytes);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const existing = this.readRow(request.session, request.id);
			if (existing) {
				const record = this.parse(existing);
				if (!sameRequest(record.request, request)) throw new Error("Delegation id identifies different content.");
				this.database.exec("COMMIT");
				return clone(record);
			}
			const active = this.countActive(request.session);
			if (active >= this.maximumActivePerSession) throw new Error("Active delegation capacity is exhausted.");
			if (request.parentDelegationId) {
				const parent = this.readRow(request.session, request.parentDelegationId);
				if (!parent) throw new Error("Parent delegation was not found in this session.");
				const parentRecord = this.parse(parent);
				if (parentRecord.request.rootDelegationId !== request.rootDelegationId)
					throw new Error("Delegation lineage root does not match its parent.");
				if (request.depth !== parentRecord.request.depth + 1)
					throw new Error("Delegation depth does not follow its parent.");
			}
			const timestamp = Date.now();
			const record: GameDelegationRecord = {
				request: clone(request),
				status: "pending",
				revision: 1,
				attempt: 0,
				createdAt: timestamp,
				updatedAt: timestamp,
			};
			this.insert(record, 0);
			this.database.exec("COMMIT");
			return clone(record);
		} catch (error) {
			this.rollback();
			throw error;
		}
	}

	async claim(
		session: GameSessionKey,
		delegationId: string,
		workerId: string,
		operationalNow: number,
		leaseMilliseconds: number,
		signal?: AbortSignal,
	): Promise<GameDelegationClaim> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		boundedId(delegationId, "Delegation id", 256);
		validateWorker(workerId);
		validateLeaseRequest(operationalNow, leaseMilliseconds);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const row = this.readRow(session, delegationId);
			if (!row) throw new Error("Delegation was not found.");
			const current = this.parse(row);
			if (terminalStatuses.has(current.status)) {
				this.database.exec("COMMIT");
				return { kind: "terminal", record: clone(current) };
			}
			if (current.status === "running" && current.lease && current.lease.expiresAt > operationalNow) {
				this.database.exec("COMMIT");
				return { kind: "busy", record: clone(current) };
			}
			const lease: GameDelegationLease = {
				workerId,
				token: randomBytes(24).toString("base64url"),
				fencingToken: (current.lease?.fencingToken ?? this.readFence(session, delegationId)) + 1,
				expiresAt: operationalNow + leaseMilliseconds,
			};
			const next: GameDelegationRecord = {
				...current,
				status: "running",
				revision: current.revision + 1,
				attempt: current.attempt + 1,
				updatedAt: Date.now(),
				lease,
			};
			delete next.error;
			delete next.result;
			this.update(current, next);
			this.database.exec("COMMIT");
			return { kind: "leased", record: clone(next), lease: clone(lease) };
		} catch (error) {
			this.rollback();
			throw error;
		}
	}

	async renew(
		session: GameSessionKey,
		delegationId: string,
		lease: GameDelegationLease,
		operationalNow: number,
		leaseMilliseconds: number,
		signal?: AbortSignal,
	): Promise<GameDelegationLease> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateLeaseRequest(operationalNow, leaseMilliseconds);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const current = this.require(session, delegationId);
			this.requireLease(current, lease, operationalNow);
			const renewed = { ...lease, expiresAt: operationalNow + leaseMilliseconds };
			const next = { ...current, revision: current.revision + 1, updatedAt: Date.now(), lease: renewed };
			this.update(current, next);
			this.database.exec("COMMIT");
			return clone(renewed);
		} catch (error) {
			this.rollback();
			throw error;
		}
	}

	async isLeaseAuthoritative(
		session: GameSessionKey,
		delegationId: string,
		lease: GameDelegationLease,
		operationalNow: number,
		signal?: AbortSignal,
	): Promise<boolean> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateOperationalTime(operationalNow, "Operational time");
		const row = this.readRow(session, delegationId);
		if (!row) return false;
		const current = this.parse(row);
		return this.sameLease(current.lease, lease) && current.status === "running" && lease.expiresAt > operationalNow;
	}

	async settle(
		session: GameSessionKey,
		delegationId: string,
		lease: GameDelegationLease,
		outcome: GameDelegationOutcome,
		operationalNow: number,
		signal?: AbortSignal,
	): Promise<GameDelegationRecord> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateOutcome(outcome, this.maximumResultBytes);
		validateOperationalTime(operationalNow, "Operational time");
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const current = this.require(session, delegationId);
			if (terminalStatuses.has(current.status)) {
				if (
					current.status !== outcome.status ||
					JSON.stringify(current.result) !== JSON.stringify(outcome.result) ||
					current.error !== outcome.error
				)
					throw new Error("Delegation already has a different terminal outcome.");
				this.database.exec("COMMIT");
				return clone(current);
			}
			this.requireLease(current, lease, operationalNow);
			const next: GameDelegationRecord = {
				...current,
				status: outcome.status,
				revision: current.revision + 1,
				updatedAt: Date.now(),
				...(outcome.result === undefined ? {} : { result: clone(outcome.result) }),
				...(outcome.error === undefined ? {} : { error: outcome.error }),
			};
			delete next.lease;
			this.update(current, next);
			this.pruneTerminals(session);
			this.database.exec("COMMIT");
			return clone(next);
		} catch (error) {
			this.rollback();
			throw error;
		}
	}

	async cancel(
		session: GameSessionKey,
		delegationId: string,
		reason: string,
		signal?: AbortSignal,
	): Promise<GameDelegationRecord> {
		this.ensureOpen();
		signal?.throwIfAborted();
		if (!reason || reason.length > 4_096) throw new TypeError("Cancellation reason is invalid.");
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const current = this.require(session, delegationId);
			if (terminalStatuses.has(current.status)) {
				this.database.exec("COMMIT");
				return clone(current);
			}
			const next: GameDelegationRecord = {
				...current,
				status: "cancelled",
				revision: current.revision + 1,
				updatedAt: Date.now(),
				error: reason,
			};
			delete next.lease;
			delete next.result;
			this.update(current, next);
			this.pruneTerminals(session);
			this.database.exec("COMMIT");
			return clone(next);
		} catch (error) {
			this.rollback();
			throw error;
		}
	}

	async read(
		session: GameSessionKey,
		delegationId: string,
		signal?: AbortSignal,
	): Promise<GameDelegationRecord | undefined> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		boundedId(delegationId, "Delegation id", 256);
		const row = this.readRow(session, delegationId);
		return row ? clone(this.parse(row)) : undefined;
	}

	async list(
		session: GameSessionKey,
		maximum: number,
		rootDelegationId?: string,
		signal?: AbortSignal,
	): Promise<readonly GameDelegationRecord[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateSession(session);
		boundedInteger(maximum, 1, 1_024, "maximum");
		if (rootDelegationId !== undefined) boundedId(rootDelegationId, "Root delegation id", 256);
		const rootClause = rootDelegationId === undefined ? "" : " AND root_id=?";
		const rows = this.database
			.prepare(`SELECT record_json FROM game_delegations WHERE ${this.whereSession()}${rootClause}
				ORDER BY created_at,delegation_id LIMIT ?`)
			.all(
				...this.key(session),
				...(rootDelegationId === undefined ? [] : [rootDelegationId]),
				maximum,
			) as unknown as DelegationRow[];
		return rows.map((row) => clone(this.parse(row)));
	}

	async listRecoverable(
		operationalNow: number,
		maximum: number,
		signal?: AbortSignal,
	): Promise<readonly GameDelegationRecord[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateOperationalTime(operationalNow, "Operational time");
		boundedInteger(maximum, 1, 10_000, "maximum");
		const rows = this.database
			.prepare(`SELECT record_json FROM game_delegations
				WHERE status='pending' OR (status='running' AND lease_expires_at<=?)
				ORDER BY updated_at,delegation_id LIMIT ?`)
			.all(operationalNow, maximum) as unknown as DelegationRow[];
		return rows.map((row) => clone(this.parse(row)));
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	[Symbol.dispose](): void {
		this.close();
	}

	private insert(record: GameDelegationRecord, fencingToken: number): void {
		const request = record.request;
		this.database
			.prepare(`INSERT INTO game_delegations(
				world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,delegation_id,root_id,parent_id,
				delegate_id,status,revision,attempt,fencing_token,lease_worker,lease_token,lease_expires_at,created_at,updated_at,record_json)
				VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`)
			.run(
				...this.key(request.session),
				request.id,
				request.rootDelegationId,
				request.parentDelegationId ?? null,
				request.delegateId,
				record.status,
				record.revision,
				record.attempt,
				fencingToken,
				null,
				null,
				null,
				record.createdAt,
				record.updatedAt,
				JSON.stringify(record),
			);
	}

	private update(current: GameDelegationRecord, next: GameDelegationRecord): void {
		const lease = next.lease;
		const result = this.database
			.prepare(`UPDATE game_delegations SET status=?,revision=?,attempt=?,fencing_token=?,lease_worker=?,lease_token=?,
				lease_expires_at=?,updated_at=?,record_json=? WHERE ${this.whereSession()} AND delegation_id=? AND revision=?`)
			.run(
				next.status,
				next.revision,
				next.attempt,
				lease?.fencingToken ??
					current.lease?.fencingToken ??
					this.readFence(current.request.session, current.request.id),
				lease?.workerId ?? null,
				lease?.token ?? null,
				lease?.expiresAt ?? null,
				next.updatedAt,
				JSON.stringify(next),
				...this.key(current.request.session),
				current.request.id,
				current.revision,
			);
		if (result.changes !== 1) throw new Error("Delegation revision conflict.");
	}

	private countActive(session: GameSessionKey): number {
		const row = this.database
			.prepare(`SELECT COUNT(*) AS count FROM game_delegations WHERE ${this.whereSession()}
				AND status IN ('pending','running')`)
			.get(...this.key(session)) as { count: number };
		return Number(row.count);
	}

	private readFence(session: GameSessionKey, delegationId: string): number {
		const row = this.database
			.prepare(`SELECT fencing_token FROM game_delegations WHERE ${this.whereSession()} AND delegation_id=?`)
			.get(...this.key(session), delegationId) as { fencing_token: number } | undefined;
		return Number(row?.fencing_token ?? 0);
	}

	private readRow(session: GameSessionKey, delegationId: string): DelegationRow | undefined {
		return this.database
			.prepare(`SELECT record_json FROM game_delegations WHERE ${this.whereSession()} AND delegation_id=?`)
			.get(...this.key(session), delegationId) as DelegationRow | undefined;
	}

	private require(session: GameSessionKey, delegationId: string): GameDelegationRecord {
		validateSession(session);
		boundedId(delegationId, "Delegation id", 256);
		const row = this.readRow(session, delegationId);
		if (!row) throw new Error("Delegation was not found.");
		const record = this.parse(row);
		if (!sameSession(record.request.session, session)) throw new Error("Delegation session mismatch.");
		return record;
	}

	private requireLease(current: GameDelegationRecord, lease: GameDelegationLease, operationalNow: number): void {
		if (current.status !== "running" || !this.sameLease(current.lease, lease))
			throw new Error("Delegation lease is stale or invalid.");
		if (current.lease.expiresAt <= operationalNow) throw new Error("Delegation lease has expired.");
	}

	private sameLease(left: GameDelegationLease | undefined, right: GameDelegationLease): left is GameDelegationLease {
		return (
			left?.workerId === right.workerId &&
			left.token === right.token &&
			left.fencingToken === right.fencingToken &&
			left.expiresAt === right.expiresAt
		);
	}

	private parse(row: DelegationRow): GameDelegationRecord {
		try {
			const record = JSON.parse(row.record_json) as GameDelegationRecord;
			validateRequest(record.request, this.maximumRequestBytes);
			if (!(["pending", "running", "completed", "failed", "cancelled"] as const).includes(record.status))
				throw new Error("status");
			boundedInteger(record.revision, 1, Number.MAX_SAFE_INTEGER, "Delegation revision");
			boundedInteger(record.attempt, 0, Number.MAX_SAFE_INTEGER, "Delegation attempt");
			validateOperationalTime(record.createdAt, "Delegation creation time");
			validateOperationalTime(record.updatedAt, "Delegation update time");
			if (record.updatedAt < record.createdAt) throw new Error("timestamp");
			if (record.status === "running" && !record.lease) throw new Error("lease");
			if (record.status !== "running" && record.lease) throw new Error("lease");
			if (record.status === "pending" || record.status === "running") {
				if (record.result !== undefined || record.error !== undefined) throw new Error("active outcome");
			} else {
				validateOutcome(
					{
						status: record.status,
						...(record.result === undefined ? {} : { result: record.result }),
						...(record.error === undefined ? {} : { error: record.error }),
					},
					this.maximumResultBytes,
				);
			}
			if (record.lease) {
				validateWorker(record.lease.workerId);
				validateLeaseToken(record.lease.token);
				boundedInteger(record.lease.fencingToken, 1, Number.MAX_SAFE_INTEGER, "Fencing token");
				validateOperationalTime(record.lease.expiresAt, "Lease expiry");
			}
			return record;
		} catch (error) {
			throw new Error("Stored delegation state is corrupt.", { cause: error });
		}
	}

	private pruneTerminals(session: GameSessionKey): void {
		this.database
			.prepare(`DELETE FROM game_delegations WHERE rowid IN (
				SELECT rowid FROM game_delegations WHERE ${this.whereSession()} AND status IN ('completed','failed','cancelled')
				ORDER BY updated_at DESC,delegation_id DESC LIMIT -1 OFFSET ?)`)
			.run(...this.key(session), this.terminalRetentionPerSession);
	}

	private whereSession(): string {
		return "world_id=? AND save_id=? AND timeline_id=? AND generation=? AND owner_id=? AND session_id=? AND actor_id=?";
	}

	private key(session: GameSessionKey): readonly (string | number)[] {
		return [
			session.worldId,
			session.saveId,
			session.timelineId,
			session.generation,
			session.ownerId,
			session.sessionId,
			session.actorId,
		];
	}

	private rollback(): void {
		try {
			this.database.exec("ROLLBACK");
		} catch {
			// Preserve the original failure.
		}
	}

	private ensureOpen(): void {
		if (this.closed) throw new Error("Delegation store is closed.");
	}
}

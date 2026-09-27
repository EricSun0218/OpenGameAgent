import { createHash, randomUUID } from "node:crypto";
import {
	incrementMailboxDecimal,
	laterMailboxTimestamp,
	type MailboxAuthorityRetirementRecord,
	MailboxCapacityError,
	type MailboxClaimRequest,
	MailboxConflictError,
	type MailboxDelivery,
	MailboxLeaseConflictError,
	type MailboxLimits,
	type MailboxListRequest,
	type MailboxMessageDraft,
	type MailboxMessageKey,
	type MailboxMessageRecord,
	type MailboxMessageSequenceKey,
	MailboxNotFoundError,
	MailboxOrderConflictError,
	type MailboxPage,
	type MailboxRecipient,
	type MailboxRenewRequest,
	MailboxRevisionConflictError,
	type MailboxStatusSummary,
	type MailboxStore,
	MailboxTerminalError,
	MailboxValidationError,
	mailboxFingerprint,
	normalizeMailboxAuthorityRetirement,
	normalizeMailboxClaim,
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
	validateMailboxStatus,
	validateMailboxTimestamp,
} from "@opengameagent/extensions";
import {
	canonicalizeJsonValue,
	type WorkAuthorityLane,
	WorkAuthorityRetiredError,
	WorkAuthorityRetirementConflictError,
	type WorkAuthorityRetirementRequest,
} from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const SHA256 = /^[0-9a-f]{64}$/u;
const MAILBOX_MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "mailbox-store-v1",
		sql: `
			CREATE TABLE oga_mailbox_messages (
				message_key INTEGER PRIMARY KEY AUTOINCREMENT CHECK(message_key > 0),
				message_id TEXT NOT NULL,
				owner_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				sender_id TEXT,
				kind TEXT NOT NULL,
				payload_json TEXT NOT NULL CHECK(json_valid(payload_json)),
				game_tick TEXT NOT NULL,
				correlation_id TEXT,
				status TEXT NOT NULL CHECK(status IN ('pending', 'completed', 'cancelled')),
				revision TEXT NOT NULL,
				attempt TEXT NOT NULL,
				lease_token TEXT,
				lease_owner_id TEXT,
				lease_expires_at TEXT,
				completed_at TEXT,
				cancelled_at TEXT,
				settled_lease_hash TEXT CHECK(settled_lease_hash IS NULL OR length(settled_lease_hash) = 64),
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				source_fingerprint TEXT NOT NULL CHECK(length(source_fingerprint) = 64),
				CHECK(
					(lease_token IS NULL AND lease_owner_id IS NULL AND lease_expires_at IS NULL)
					OR (lease_token IS NOT NULL AND lease_owner_id IS NOT NULL AND lease_expires_at IS NOT NULL)
				),
				CHECK(
					(status = 'pending' AND completed_at IS NULL AND cancelled_at IS NULL AND settled_lease_hash IS NULL)
					OR (status = 'completed' AND completed_at IS NOT NULL AND cancelled_at IS NULL
						AND settled_lease_hash IS NOT NULL AND lease_token IS NULL)
					OR (status = 'cancelled' AND completed_at IS NULL AND cancelled_at IS NOT NULL
						AND settled_lease_hash IS NULL AND lease_token IS NULL)
				),
				UNIQUE(owner_id, timeline_id, generation, session_id, actor_id, message_id)
			) STRICT;

			CREATE INDEX oga_mailbox_recipient_status_key
			ON oga_mailbox_messages(owner_id, timeline_id, generation, session_id, actor_id, status, message_key);

			CREATE INDEX oga_mailbox_generation_pending
			ON oga_mailbox_messages(owner_id, timeline_id, generation, status)
			WHERE status = 'pending';
		`,
	},
	{
		version: 2,
		name: "mailbox-generation-list-v2",
		sql: `
			CREATE INDEX oga_mailbox_generation_key
			ON oga_mailbox_messages(owner_id, timeline_id, generation, message_key);
		`,
	},
	{
		version: 3,
		name: "mailbox-released-lease-v3",
		sql: `
			ALTER TABLE oga_mailbox_messages
			ADD COLUMN released_lease_hash TEXT
			CHECK(released_lease_hash IS NULL OR length(released_lease_hash) = 64);
		`,
	},
	{
		version: 4,
		name: "mailbox-authority-retirement-v4",
		sql: `
			CREATE TABLE oga_mailbox_authority_retirements (
				owner_id TEXT NOT NULL, timeline_id TEXT NOT NULL, generation TEXT NOT NULL,
				session_id TEXT NOT NULL, actor_id TEXT NOT NULL,
				retirement_id TEXT NOT NULL UNIQUE, recorded_at TEXT NOT NULL,
				phase TEXT NOT NULL CHECK(phase IN ('fenced', 'finalized')),
				cancelled_message_count INTEGER NOT NULL CHECK(cancelled_message_count >= 0),
				PRIMARY KEY(owner_id, timeline_id, generation, session_id, actor_id)
			) STRICT;
		`,
	},
];

interface RetirementRow {
	owner_id: string;
	timeline_id: string;
	generation: string;
	session_id: string;
	actor_id: string;
	retirement_id: string;
	recorded_at: string;
	phase: "fenced" | "finalized";
	cancelled_message_count: number;
}

interface MailboxRow {
	message_key: bigint;
	message_id: string;
	owner_id: string;
	timeline_id: string;
	generation: string;
	session_id: string;
	actor_id: string;
	sender_id: string | null;
	kind: string;
	payload_json: string;
	game_tick: string;
	correlation_id: string | null;
	status: string;
	revision: string;
	attempt: string;
	lease_token: string | null;
	lease_owner_id: string | null;
	lease_expires_at: string | null;
	completed_at: string | null;
	cancelled_at: string | null;
	settled_lease_hash: string | null;
	released_lease_hash: string | null;
	created_at: string;
	updated_at: string;
	source_fingerprint: string;
}

interface MailboxStatusRow {
	pending: bigint;
	completed: bigint;
	cancelled: bigint;
	oldest_pending: bigint | null;
	latest_message: bigint | null;
}

export class SqliteMailboxStore implements MailboxStore {
	private readonly limits: MailboxLimits;

	constructor(
		private readonly authority: SqliteAuthority,
		limits: MailboxLimits,
		private readonly newLeaseToken: () => string = randomUUID,
	) {
		validateMailboxLimits(limits);
		this.limits = Object.freeze({ ...limits });
		this.authority.install("mailbox-store", MAILBOX_MIGRATIONS);
	}

	async enqueue(
		input: MailboxMessageDraft,
		maxPendingPerGeneration: number,
		maxPendingPerRecipient: number,
	): Promise<MailboxMessageRecord> {
		const draft = normalizeMailboxMessage(input, this.limits);
		validateCapacity(maxPendingPerGeneration, this.limits.maxPendingMessagesPerTimelineGeneration);
		validateCapacity(maxPendingPerRecipient, this.limits.maxPendingMessagesPerRecipient);
		return this.authority.write((database) => {
			this.assertAuthorityOpen(database, draft.recipient);
			const existing = findMailboxRow(database, { ...draft.recipient, messageId: draft.messageId });
			if (existing !== undefined) {
				const current = this.hydrate(existing);
				if (digest(mailboxFingerprint(draft)) !== existing.source_fingerprint) throw new MailboxConflictError();
				return current;
			}
			if (countGenerationPending(database, draft.recipient) >= BigInt(maxPendingPerGeneration)) {
				throw new MailboxCapacityError("timeline_generation");
			}
			if (countRecipientPending(database, draft.recipient) >= BigInt(maxPendingPerRecipient)) {
				throw new MailboxCapacityError("recipient");
			}
			const payloadJson = canonicalizeJsonValue(draft.payload);
			const result = database
				.prepare(`
					INSERT INTO oga_mailbox_messages(
						message_id, owner_id, timeline_id, generation, session_id, actor_id,
						sender_id, kind, payload_json, game_tick, correlation_id,
						status, revision, attempt, created_at, updated_at, source_fingerprint
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'pending', '1', '0', ?, ?, ?)
				`)
				.run(
					draft.messageId,
					draft.recipient.ownerId,
					draft.recipient.timelineId,
					draft.recipient.generation,
					draft.recipient.sessionId,
					draft.recipient.actorId,
					draft.senderId ?? null,
					draft.kind,
					payloadJson,
					draft.gameTime.tick,
					draft.correlationId ?? null,
					draft.recordedAt,
					draft.recordedAt,
					digest(mailboxFingerprint(draft)),
				);
			const row = findMailboxRowByKey(database, BigInt(result.lastInsertRowid));
			if (row === undefined) throw new MailboxNotFoundError();
			return this.hydrate(row);
		});
	}

	async get(key: MailboxMessageKey): Promise<MailboxMessageRecord | undefined> {
		key = validateMailboxMessageKey(key, this.limits);
		return this.authority.read((database) => {
			const row = findMailboxRow(database, key);
			return row === undefined ? undefined : this.hydrate(row);
		});
	}

	async getByKey(key: MailboxMessageSequenceKey): Promise<MailboxMessageRecord | undefined> {
		key = validateMailboxMessageSequenceKey(key, this.limits);
		return this.authority.read((database) => {
			const row = findMailboxRowBySequenceKey(database, key);
			return row === undefined ? undefined : this.hydrate(row);
		});
	}

	async list(request: MailboxListRequest): Promise<MailboxPage> {
		const scope = normalizeMailboxListScope(request, this.limits);
		const statuses = normalizeMailboxStatusFilter(request.statuses);
		validateMailboxCursor(request.afterMessageKey);
		validateMailboxPageLimit(request.limit, this.limits.maxPageSize, "invalid_mailbox_page_limit");
		return this.authority.read((database) => {
			const selectedStatuses = statuses ?? [];
			const conditions = ["owner_id = ?", "timeline_id = ?", "generation = ?"];
			const parameters: (string | bigint)[] = [scope.ownerId, scope.timelineId, scope.generation];
			if (scope.sessionId !== undefined) {
				conditions.push("session_id = ?");
				parameters.push(scope.sessionId);
			}
			if (scope.actorId !== undefined) {
				conditions.push("actor_id = ?");
				parameters.push(scope.actorId);
			}
			conditions.push("message_key > ?");
			parameters.push(request.afterMessageKey ?? 0n);
			if (selectedStatuses.length > 0) {
				conditions.push(`status IN (${selectedStatuses.map(() => "?").join(", ")})`);
				parameters.push(...selectedStatuses);
			}
			const statement = database.prepare(`
				SELECT * FROM oga_mailbox_messages
				WHERE ${conditions.join(" AND ")}
				ORDER BY message_key LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(...parameters, BigInt(request.limit + 1)) as MailboxRow[];
			const hasMore = rows.length > request.limit;
			const records = rows.slice(0, request.limit).map((row) => this.hydrate(row));
			const next = hasMore ? records.at(-1)?.messageKey : undefined;
			return { records, ...(next === undefined ? {} : { nextAfterMessageKey: next }) };
		});
	}

	async status(input: MailboxRecipient): Promise<MailboxStatusSummary> {
		const recipient = normalizeMailboxRecipient(input, this.limits);
		return this.authority.read((database) => {
			const statement = database.prepare(`
				SELECT
					COALESCE(SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END), 0) AS pending,
					COALESCE(SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END), 0) AS completed,
					COALESCE(SUM(CASE WHEN status = 'cancelled' THEN 1 ELSE 0 END), 0) AS cancelled,
					MIN(CASE WHEN status = 'pending' THEN message_key END) AS oldest_pending,
					MAX(message_key) AS latest_message
				FROM oga_mailbox_messages
				WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ?
			`);
			statement.safeIntegers(true);
			const row = statement.get(
				recipient.ownerId,
				recipient.timelineId,
				recipient.generation,
				recipient.sessionId,
				recipient.actorId,
			) as MailboxStatusRow;
			return {
				...recipient,
				pending: row.pending,
				completed: row.completed,
				cancelled: row.cancelled,
				...(row.oldest_pending === null ? {} : { oldestPendingMessageKey: row.oldest_pending }),
				...(row.latest_message === null ? {} : { latestMessageKey: row.latest_message }),
			};
		});
	}

	async claim(request: MailboxClaimRequest): Promise<readonly MailboxDelivery[]> {
		const claim = normalizeMailboxClaim(request, this.limits);
		const selected = this.authority.read((database) => {
			this.assertAuthorityOpen(database, claim);
			const statement = database.prepare(`
				SELECT * FROM oga_mailbox_messages
				WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ?
				  AND status = 'pending'
				ORDER BY message_key LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				claim.ownerId,
				claim.timelineId,
				claim.generation,
				claim.sessionId,
				claim.actorId,
				BigInt(claim.limit),
			) as MailboxRow[];
			const selected: MailboxRow[] = [];
			for (const row of rows) {
				const current = this.hydrate(row);
				if (claim.operationalNow < current.updatedAt) throw new MailboxLeaseConflictError();
				if (row.lease_token !== null && row.lease_expires_at !== null && row.lease_expires_at > claim.operationalNow) {
					break;
				}
				selected.push(row);
			}
			return selected;
		});
		const prepared = selected.map((row) => {
			const current = this.hydrate(row);
			const token = this.newLeaseToken();
			validateMailboxIdentifier(token, this.limits.maxIdentifierBytes, "lease_token");
			const nextRevision = incrementMailboxDecimal(current.revision, this.limits.maxCounterDigits, "revision");
			const nextAttempt = incrementMailboxDecimal(current.attempt, this.limits.maxCounterDigits, "attempt");
			return { current, token, nextRevision, nextAttempt };
		});
		return this.authority.write((database) => {
			this.assertAuthorityOpen(database, claim);
			return prepared.map(({ current, token, nextRevision, nextAttempt }) => {
				const result = database
					.prepare(`
						UPDATE oga_mailbox_messages
						SET revision = ?, attempt = ?, lease_token = ?, lease_owner_id = ?, lease_expires_at = ?,
						    released_lease_hash = NULL, updated_at = ?
						WHERE message_key = ? AND status = 'pending' AND revision = ?
						  AND updated_at <= ? AND (lease_token IS NULL OR lease_expires_at <= ?)
					`)
					.run(
						nextRevision,
						nextAttempt,
						token,
						claim.leaseOwnerId,
						claim.leaseExpiresAt,
						claim.operationalNow,
						current.messageKey,
						current.revision,
						claim.operationalNow,
						claim.operationalNow,
					);
				if (result.changes !== 1) throw new MailboxLeaseConflictError();
				const updated = findMailboxRowByKey(database, current.messageKey);
				if (updated === undefined) throw new MailboxNotFoundError();
				return {
					message: this.hydrate(updated),
					leaseToken: token,
					leaseOwnerId: claim.leaseOwnerId,
					leaseExpiresAt: claim.leaseExpiresAt,
				};
			});
		});
	}

	async renew(request: MailboxRenewRequest): Promise<MailboxDelivery> {
		const renewal = normalizeMailboxRenew(request, this.limits);
		return this.authority.write((database) => {
			this.assertAuthorityOpen(database, renewal);
			const row = findMailboxRowBySequenceKey(database, renewal);
			if (row === undefined) throw new MailboxNotFoundError();
			const current = this.hydrate(row);
			if (
				current.status !== "pending" ||
				row.lease_token !== renewal.leaseToken ||
				row.lease_owner_id === null ||
				row.lease_expires_at === null ||
				renewal.operationalNow < current.updatedAt ||
				row.lease_expires_at <= renewal.operationalNow
			) {
				throw new MailboxLeaseConflictError();
			}
			if (renewal.leaseExpiresAt === row.lease_expires_at) {
				return delivery(current, renewal.leaseToken, row.lease_owner_id, row.lease_expires_at);
			}
			if (renewal.leaseExpiresAt < row.lease_expires_at) throw new MailboxLeaseConflictError();
			const nextRevision = incrementMailboxDecimal(current.revision, this.limits.maxCounterDigits, "revision");
			const result = database
				.prepare(`
					UPDATE oga_mailbox_messages SET revision = ?, lease_expires_at = ?, updated_at = ?
					WHERE message_key = ? AND status = 'pending' AND revision = ? AND lease_token = ?
					  AND updated_at <= ? AND lease_expires_at > ?
				`)
				.run(
					nextRevision,
					renewal.leaseExpiresAt,
					renewal.operationalNow,
					current.messageKey,
					current.revision,
					renewal.leaseToken,
					renewal.operationalNow,
					renewal.operationalNow,
				);
			if (result.changes !== 1) throw new MailboxLeaseConflictError();
			const updated = findMailboxRowByKey(database, current.messageKey);
			if (updated === undefined || updated.lease_owner_id === null || updated.lease_expires_at === null) {
				throw new MailboxNotFoundError();
			}
			return delivery(this.hydrate(updated), renewal.leaseToken, updated.lease_owner_id, updated.lease_expires_at);
		});
	}

	async assertLease(
		key: MailboxMessageSequenceKey,
		leaseToken: string,
		operationalNow: string,
	): Promise<MailboxDelivery> {
		key = validateMailboxMessageSequenceKey(key, this.limits);
		validateMailboxIdentifier(leaseToken, this.limits.maxIdentifierBytes, "lease_token");
		validateMailboxTimestamp(operationalNow);
		return this.authority.snapshot((database) => {
			this.assertAuthorityOpen(database, key);
			const row = findMailboxRowBySequenceKey(database, key);
			if (row === undefined) throw new MailboxNotFoundError();
			const current = this.hydrate(row);
			this.assertActiveLease(row, current, leaseToken, operationalNow);
			if (hasEarlierPending(database, current)) throw new MailboxOrderConflictError();
			if (row.lease_owner_id === null || row.lease_expires_at === null) throw new MailboxLeaseConflictError();
			return delivery(current, leaseToken, row.lease_owner_id, row.lease_expires_at);
		});
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
		return this.authority.write((database) => {
			const row = findMailboxRowBySequenceKey(database, key);
			if (row === undefined) throw new MailboxNotFoundError();
			const current = this.hydrate(row);
			const proofHash = leaseProofDigest(leaseToken, leaseOwnerId);
			if (current.status === "completed") {
				if (row.settled_lease_hash !== proofHash) throw new MailboxLeaseConflictError();
				return current;
			}
			this.assertAuthorityOpen(database, key);
			this.assertActiveLease(row, current, leaseToken, operationalNow);
			if (row.lease_owner_id !== leaseOwnerId) throw new MailboxLeaseConflictError();
			if (hasEarlierPending(database, current)) throw new MailboxOrderConflictError();
			const nextRevision = incrementMailboxDecimal(current.revision, this.limits.maxCounterDigits, "revision");
			const result = database
				.prepare(`
					UPDATE oga_mailbox_messages
					SET status = 'completed', revision = ?, lease_token = NULL, lease_owner_id = NULL,
					    lease_expires_at = NULL, completed_at = ?, settled_lease_hash = ?,
					    released_lease_hash = NULL, updated_at = ?
					WHERE message_key = ? AND status = 'pending' AND revision = ?
					  AND lease_token = ? AND lease_owner_id = ?
					  AND updated_at <= ? AND lease_expires_at > ?
				`)
				.run(
					nextRevision,
					operationalNow,
					proofHash,
					operationalNow,
					current.messageKey,
					current.revision,
					leaseToken,
					leaseOwnerId,
					operationalNow,
					operationalNow,
				);
			if (result.changes !== 1) throw new MailboxLeaseConflictError();
			const updated = findMailboxRowByKey(database, current.messageKey);
			if (updated === undefined) throw new MailboxNotFoundError();
			return this.hydrate(updated);
		});
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
		return this.authority.write((database) => {
			this.assertAuthorityOpen(database, key);
			const row = findMailboxRowBySequenceKey(database, key);
			if (row === undefined) throw new MailboxNotFoundError();
			const current = this.hydrate(row);
			const proofHash = leaseProofDigest(leaseToken, leaseOwnerId);
			if (current.status === "pending" && row.lease_token === null) {
				if (row.released_lease_hash !== proofHash) throw new MailboxLeaseConflictError();
				return current;
			}
			this.assertActiveLease(row, current, leaseToken, operationalNow);
			if (row.lease_owner_id !== leaseOwnerId) throw new MailboxLeaseConflictError();
			const nextRevision = incrementMailboxDecimal(current.revision, this.limits.maxCounterDigits, "revision");
			const result = database
				.prepare(`
					UPDATE oga_mailbox_messages
					SET revision = ?, lease_token = NULL, lease_owner_id = NULL, lease_expires_at = NULL,
					    released_lease_hash = ?, updated_at = ?
					WHERE message_key = ? AND status = 'pending' AND revision = ?
					  AND lease_token = ? AND lease_owner_id = ?
					  AND updated_at <= ? AND lease_expires_at > ?
				`)
				.run(
					nextRevision,
					proofHash,
					operationalNow,
					current.messageKey,
					current.revision,
					leaseToken,
					leaseOwnerId,
					operationalNow,
					operationalNow,
				);
			if (result.changes !== 1) throw new MailboxLeaseConflictError();
			const updated = findMailboxRowByKey(database, current.messageKey);
			if (updated === undefined) throw new MailboxNotFoundError();
			return this.hydrate(updated);
		});
	}

	async cancel(
		key: MailboxMessageKey,
		expectedRevision: string,
		operationalNow: string,
	): Promise<MailboxMessageRecord> {
		key = validateMailboxMessageKey(key, this.limits);
		validateMailboxDecimal(expectedRevision, "revision", this.limits.maxCounterDigits);
		validateMailboxTimestamp(operationalNow);
		return this.authority.write((database) => {
			this.assertAuthorityOpen(database, key);
			const row = findMailboxRow(database, key);
			if (row === undefined) throw new MailboxNotFoundError();
			const current = this.hydrate(row);
			if (current.revision !== expectedRevision) throw new MailboxRevisionConflictError();
			if (current.status !== "pending") throw new MailboxTerminalError();
			if (operationalNow < current.updatedAt) throw new MailboxLeaseConflictError();
			if (row.lease_token !== null && row.lease_expires_at !== null && row.lease_expires_at > operationalNow) {
				throw new MailboxLeaseConflictError();
			}
			const nextRevision = incrementMailboxDecimal(current.revision, this.limits.maxCounterDigits, "revision");
			const result = database
				.prepare(`
					UPDATE oga_mailbox_messages
					SET status = 'cancelled', revision = ?, lease_token = NULL, lease_owner_id = NULL,
					    lease_expires_at = NULL, cancelled_at = ?, released_lease_hash = NULL, updated_at = ?
					WHERE message_key = ? AND status = 'pending' AND revision = ? AND updated_at <= ?
					  AND (lease_token IS NULL OR lease_expires_at <= ?)
				`)
				.run(
					nextRevision,
					operationalNow,
					operationalNow,
					current.messageKey,
					current.revision,
					operationalNow,
					operationalNow,
				);
			if (result.changes !== 1) throw new MailboxLeaseConflictError();
			const updated = findMailboxRowByKey(database, current.messageKey);
			if (updated === undefined) throw new MailboxNotFoundError();
			return this.hydrate(updated);
		});
	}

	async purgeTerminal(
		input: MailboxRecipient,
		beforeOperationalTime: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const recipient = normalizeMailboxRecipient(input, this.limits);
		validateMailboxTimestamp(beforeOperationalTime);
		validateMailboxPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_mailbox_purge_limit");
		return this.authority.write((database) => {
			const statement = database.prepare(`
				SELECT * FROM oga_mailbox_messages
				WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ?
				  AND status IN ('completed', 'cancelled') AND updated_at < ?
				ORDER BY message_key LIMIT ?
			`);
			statement.safeIntegers(true);
			const rows = statement.all(
				recipient.ownerId,
				recipient.timelineId,
				recipient.generation,
				recipient.sessionId,
				recipient.actorId,
				beforeOperationalTime,
				BigInt(limit),
			) as MailboxRow[];
			const keys = rows.map((row) => this.hydrate(row).messageKey);
			const remove = database.prepare("DELETE FROM oga_mailbox_messages WHERE message_key = ?");
			for (const key of keys) remove.run(key);
			return keys;
		});
	}

	async beginAuthorityRetirement(input: WorkAuthorityRetirementRequest): Promise<MailboxAuthorityRetirementRecord> {
		const request = normalizeMailboxAuthorityRetirement(input, this.limits);
		return this.authority.write((database) => this.beginAuthorityRetirementInTransaction(database, request));
	}

	beginAuthorityRetirementInTransaction(
		database: Database.Database,
		input: WorkAuthorityRetirementRequest,
	): MailboxAuthorityRetirementRecord {
		this.assertRetirementTransaction(database);
		const request = normalizeMailboxAuthorityRetirement(input, this.limits);
		const existing = this.findRetirement(database, request);
		return existing ?? this.commitRetirement(database, request, "fenced", 0);
	}

	async finalizeAuthorityRetirement(input: WorkAuthorityRetirementRequest): Promise<MailboxAuthorityRetirementRecord> {
		const request = normalizeMailboxAuthorityRetirement(input, this.limits);
		return this.authority.write((database) => this.finalizeAuthorityRetirementInTransaction(database, request));
	}

	finalizeAuthorityRetirementInTransaction(
		database: Database.Database,
		input: WorkAuthorityRetirementRequest,
	): MailboxAuthorityRetirementRecord {
		this.assertRetirementTransaction(database);
		const request = normalizeMailboxAuthorityRetirement(input, this.limits);
		const existing = this.findRetirement(database, request);
		if (!existing) throw new WorkAuthorityRetirementConflictError();
		return existing.phase === "finalized"
			? existing
			: this.commitRetirement(database, request, "finalized", existing.cancelledMessageCount);
	}

	async readAuthorityRetirement(input: WorkAuthorityLane): Promise<MailboxAuthorityRetirementRecord | undefined> {
		const partition = normalizeMailboxRecipient(input, this.limits);
		return this.authority.read((database) => {
			const row = findRetirementRow(database, partition);
			return row === undefined ? undefined : this.hydrateRetirement(row);
		});
	}

	private assertRetirementTransaction(database: Database.Database): void {
		if (!database.inTransaction || this.authority.read((owned) => owned !== database)) {
			throw new TypeError("Mailbox retirement must use the same active SQLite authority transaction.");
		}
	}

	private findRetirement(
		database: Database.Database,
		request: WorkAuthorityRetirementRequest,
	): MailboxAuthorityRetirementRecord | undefined {
		const row = findRetirementRow(database, request.partition);
		const id = database
			.prepare("SELECT retirement_id FROM oga_mailbox_authority_retirements WHERE retirement_id = ?")
			.get(request.retirementId);
		if (row === undefined) {
			if (id !== undefined) throw new WorkAuthorityRetirementConflictError();
			return undefined;
		}
		const record = this.hydrateRetirement(row);
		if (record.retirementId !== request.retirementId || record.recordedAt !== request.recordedAt)
			throw new WorkAuthorityRetirementConflictError();
		return record;
	}

	private commitRetirement(
		database: Database.Database,
		request: WorkAuthorityRetirementRequest,
		phase: "fenced" | "finalized",
		cancelled: number,
	): MailboxAuthorityRetirementRecord {
		const partition = request.partition;
		const maximum = Math.min(
			this.limits.maxPendingMessagesPerRecipient,
			this.limits.maxPendingMessagesPerTimelineGeneration,
		);
		const rows = database
			.prepare(`SELECT * FROM oga_mailbox_messages
			WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ?
			AND status = 'pending' ${phase === "fenced" ? "AND lease_token IS NULL" : ""} ORDER BY message_key LIMIT ?`)
			.safeIntegers(true)
			.all(
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				partition.sessionId,
				partition.actorId,
				BigInt(maximum) + 1n,
			) as MailboxRow[];
		if (rows.length > maximum)
			throw new MailboxCapacityError(
				maximum === this.limits.maxPendingMessagesPerRecipient ? "recipient" : "timeline_generation",
			);
		const updates = rows.map((row) => {
			const message = this.hydrate(row);
			return {
				key: message.messageKey,
				revision: incrementMailboxDecimal(message.revision, this.limits.maxCounterDigits, "revision"),
				at: laterMailboxTimestamp(message.updatedAt, request.recordedAt),
			};
		});
		const count = cancelled + updates.length;
		if (!Number.isSafeInteger(count)) throw new MailboxValidationError("mailbox_retirement_count_overflow");
		database
			.prepare(`INSERT INTO oga_mailbox_authority_retirements
			(owner_id,timeline_id,generation,session_id,actor_id,retirement_id,recorded_at,phase,cancelled_message_count)
			VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
			ON CONFLICT(owner_id,timeline_id,generation,session_id,actor_id) DO UPDATE SET phase=excluded.phase,cancelled_message_count=excluded.cancelled_message_count`)
			.run(
				partition.ownerId,
				partition.timelineId,
				partition.generation,
				partition.sessionId,
				partition.actorId,
				request.retirementId,
				request.recordedAt,
				phase,
				count,
			);
		const update =
			database.prepare(`UPDATE oga_mailbox_messages SET status='cancelled', revision=?, cancelled_at=?, updated_at=?,
			lease_token=NULL, lease_owner_id=NULL, lease_expires_at=NULL, released_lease_hash=NULL WHERE message_key=?`);
		for (const message of updates) update.run(message.revision, message.at, message.at, message.key);
		return { ...request, phase, cancelledMessageCount: count };
	}

	private hydrateRetirement(row: RetirementRow): MailboxAuthorityRetirementRecord {
		const request = normalizeMailboxAuthorityRetirement(
			{
				retirementId: row.retirement_id,
				recordedAt: row.recorded_at,
				partition: {
					ownerId: row.owner_id,
					timelineId: row.timeline_id,
					generation: row.generation,
					sessionId: row.session_id,
					actorId: row.actor_id,
				},
			},
			this.limits,
		);
		if (
			(row.phase !== "fenced" && row.phase !== "finalized") ||
			!Number.isSafeInteger(row.cancelled_message_count) ||
			row.cancelled_message_count < 0
		)
			throw new MailboxValidationError("corrupt_mailbox_retirement");
		return { ...request, phase: row.phase, cancelledMessageCount: row.cancelled_message_count };
	}

	private assertAuthorityOpen(database: Database.Database, partition: WorkAuthorityLane): void {
		if (findRetirementRow(database, partition) !== undefined)
			throw new WorkAuthorityRetiredError(normalizeMailboxRecipient(partition, this.limits));
	}

	private hydrate(row: MailboxRow): MailboxMessageRecord {
		if (row.message_key < 1n) throw new MailboxValidationError("invalid_mailbox_message_key");
		let payload: unknown;
		try {
			payload = JSON.parse(row.payload_json);
		} catch {
			throw new MailboxValidationError("corrupt_mailbox_payload");
		}
		const draft = normalizeMailboxMessage(
			{
				messageId: row.message_id,
				recipient: {
					ownerId: row.owner_id,
					timelineId: row.timeline_id,
					generation: row.generation,
					sessionId: row.session_id,
					actorId: row.actor_id,
				},
				...(row.sender_id === null ? {} : { senderId: row.sender_id }),
				kind: row.kind,
				payload: payload as never,
				gameTime: { timelineId: row.timeline_id, generation: row.generation, tick: row.game_tick },
				...(row.correlation_id === null ? {} : { correlationId: row.correlation_id }),
				recordedAt: row.created_at,
			},
			this.limits,
		);
		if (digest(mailboxFingerprint(draft)) !== row.source_fingerprint) {
			throw new MailboxValidationError("corrupt_mailbox_index");
		}
		validateMailboxStatus(row.status);
		validateMailboxDecimal(row.revision, "revision", this.limits.maxCounterDigits);
		validateMailboxDecimal(row.attempt, "attempt", this.limits.maxCounterDigits);
		if (row.revision === "0" || BigInt(row.revision) <= BigInt(row.attempt)) {
			throw new MailboxValidationError("corrupt_mailbox_revision");
		}
		validateMailboxTimestamp(row.updated_at);
		if (row.updated_at < row.created_at) throw new MailboxValidationError("corrupt_mailbox_chronology");
		const leaseFields = [row.lease_token, row.lease_owner_id, row.lease_expires_at];
		if (leaseFields.some((value) => value === null) && leaseFields.some((value) => value !== null)) {
			throw new MailboxValidationError("corrupt_mailbox_lease");
		}
		if (row.lease_token !== null && row.lease_owner_id !== null && row.lease_expires_at !== null) {
			validateMailboxIdentifier(row.lease_token, this.limits.maxIdentifierBytes, "lease_token");
			validateMailboxIdentifier(row.lease_owner_id, this.limits.maxIdentifierBytes, "lease_owner_id");
			validateMailboxTimestamp(row.lease_expires_at);
			if (row.attempt === "0" || row.lease_expires_at <= row.updated_at) {
				throw new MailboxValidationError("corrupt_mailbox_lease");
			}
		}
		if (row.completed_at !== null) validateMailboxTimestamp(row.completed_at);
		if (row.cancelled_at !== null) validateMailboxTimestamp(row.cancelled_at);
		if (row.released_lease_hash !== null && !SHA256.test(row.released_lease_hash)) {
			throw new MailboxValidationError("corrupt_mailbox_release");
		}
		if (row.status === "completed") {
			if (
				row.completed_at === null ||
				row.completed_at !== row.updated_at ||
				row.cancelled_at !== null ||
				row.settled_lease_hash === null ||
				!SHA256.test(row.settled_lease_hash) ||
				row.released_lease_hash !== null ||
				row.lease_token !== null ||
				row.attempt === "0"
			) {
				throw new MailboxValidationError("corrupt_mailbox_completion");
			}
		} else if (row.status === "cancelled") {
			if (
				row.cancelled_at === null ||
				row.cancelled_at !== row.updated_at ||
				row.completed_at !== null ||
				row.settled_lease_hash !== null ||
				row.released_lease_hash !== null ||
				row.lease_token !== null
			) {
				throw new MailboxValidationError("corrupt_mailbox_cancellation");
			}
		} else if (
			row.completed_at !== null ||
			row.cancelled_at !== null ||
			row.settled_lease_hash !== null ||
			(row.released_lease_hash !== null && (row.lease_token !== null || row.attempt === "0"))
		) {
			throw new MailboxValidationError("corrupt_mailbox_terminal_state");
		}
		return {
			...draft,
			messageKey: row.message_key,
			status: row.status,
			revision: row.revision,
			attempt: row.attempt,
			createdAt: row.created_at,
			updatedAt: row.updated_at,
			...(row.completed_at === null ? {} : { completedAt: row.completed_at }),
			...(row.cancelled_at === null ? {} : { cancelledAt: row.cancelled_at }),
		};
	}

	private assertActiveLease(
		row: MailboxRow,
		message: MailboxMessageRecord,
		leaseToken: string,
		operationalNow: string,
	): void {
		if (
			message.status !== "pending" ||
			row.lease_token !== leaseToken ||
			row.lease_expires_at === null ||
			operationalNow < message.updatedAt ||
			row.lease_expires_at <= operationalNow
		) {
			throw new MailboxLeaseConflictError();
		}
	}
}

function findRetirementRow(database: Database.Database, partition: WorkAuthorityLane): RetirementRow | undefined {
	return database
		.prepare(`SELECT * FROM oga_mailbox_authority_retirements
		WHERE owner_id=? AND timeline_id=? AND generation=? AND session_id=? AND actor_id=?`)
		.get(partition.ownerId, partition.timelineId, partition.generation, partition.sessionId, partition.actorId) as
		| RetirementRow
		| undefined;
}

function findMailboxRow(database: Database.Database, key: MailboxMessageKey): MailboxRow | undefined {
	const statement = database.prepare(`
		SELECT * FROM oga_mailbox_messages
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ? AND message_id = ?
		LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(key.ownerId, key.timelineId, key.generation, key.sessionId, key.actorId, key.messageId) as
		| MailboxRow
		| undefined;
}

function findMailboxRowByKey(database: Database.Database, messageKey: bigint): MailboxRow | undefined {
	const statement = database.prepare("SELECT * FROM oga_mailbox_messages WHERE message_key = ? LIMIT 1");
	statement.safeIntegers(true);
	return statement.get(messageKey) as MailboxRow | undefined;
}

function findMailboxRowBySequenceKey(
	database: Database.Database,
	key: MailboxMessageSequenceKey,
): MailboxRow | undefined {
	const statement = database.prepare(`
		SELECT * FROM oga_mailbox_messages
		WHERE message_key = ? AND owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ?
		LIMIT 1
	`);
	statement.safeIntegers(true);
	return statement.get(key.messageKey, key.ownerId, key.timelineId, key.generation, key.sessionId, key.actorId) as
		| MailboxRow
		| undefined;
}

function countGenerationPending(database: Database.Database, recipient: MailboxRecipient): bigint {
	const statement = database.prepare(`
		SELECT count(*) FROM oga_mailbox_messages
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND status = 'pending'
	`);
	statement.safeIntegers(true);
	return statement.pluck().get(recipient.ownerId, recipient.timelineId, recipient.generation) as bigint;
}

function countRecipientPending(database: Database.Database, recipient: MailboxRecipient): bigint {
	const statement = database.prepare(`
		SELECT count(*) FROM oga_mailbox_messages
		WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ? AND status = 'pending'
	`);
	statement.safeIntegers(true);
	return statement
		.pluck()
		.get(
			recipient.ownerId,
			recipient.timelineId,
			recipient.generation,
			recipient.sessionId,
			recipient.actorId,
		) as bigint;
}

function hasEarlierPending(database: Database.Database, message: MailboxMessageRecord): boolean {
	return (
		database
			.prepare(`
				SELECT 1 FROM oga_mailbox_messages
				WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND session_id = ? AND actor_id = ?
				  AND status = 'pending' AND message_key < ? LIMIT 1
			`)
			.get(
				message.recipient.ownerId,
				message.recipient.timelineId,
				message.recipient.generation,
				message.recipient.sessionId,
				message.recipient.actorId,
				message.messageKey,
			) !== undefined
	);
}

function delivery(
	message: MailboxMessageRecord,
	leaseToken: string,
	leaseOwnerId: string,
	leaseExpiresAt: string,
): MailboxDelivery {
	return { message, leaseToken, leaseOwnerId, leaseExpiresAt };
}

function digest(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}

function leaseProofDigest(leaseToken: string, leaseOwnerId: string): string {
	return digest(canonicalizeJsonValue([leaseToken, leaseOwnerId]));
}

function validateCapacity(value: number, maximum: number): void {
	if (!Number.isSafeInteger(value) || value < 1 || value > maximum) {
		throw new MailboxValidationError("invalid_mailbox_capacity");
	}
}

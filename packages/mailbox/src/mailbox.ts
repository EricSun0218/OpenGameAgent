import { randomBytes } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import type { GameMoment, GameSessionKey, JsonValue } from "@opengameagent/protocol";

export interface GameMailboxMessage {
	id: string;
	session: GameSessionKey;
	recipientId: string;
	kind: string;
	payload: JsonValue;
	moment: GameMoment;
	senderId?: string;
	correlationId?: string;
}

export interface GameMailboxDelivery {
	message: GameMailboxMessage;
	leaseToken: string;
	attempt: number;
	leaseExpiresAt: number;
}

export interface GameMailboxRecipientKey {
	session: GameSessionKey;
	recipientId: string;
}

export interface GameMailboxPendingStatus {
	recipient: GameMailboxRecipientKey;
	readyCount: number;
	leasedCount: number;
	incompleteCount: number;
}

export interface GameMailbox {
	enqueue(message: GameMailboxMessage, signal?: AbortSignal): Promise<boolean>;
	claim(
		recipient: GameMailboxRecipientKey,
		maximum: number,
		operationalNow: number,
		leaseMilliseconds: number,
		signal?: AbortSignal,
	): Promise<readonly GameMailboxDelivery[]>;
	complete(
		recipient: GameMailboxRecipientKey,
		messageId: string,
		leaseToken: string,
		signal?: AbortSignal,
	): Promise<void>;
	abandon(
		recipient: GameMailboxRecipientKey,
		messageId: string,
		leaseToken: string,
		signal?: AbortSignal,
	): Promise<void>;
	readPendingStatus(
		recipients: readonly GameMailboxRecipientKey[],
		operationalNow: number,
		signal?: AbortSignal,
	): Promise<readonly GameMailboxPendingStatus[]>;
}

interface MemoryEntry {
	message: GameMailboxMessage;
	sequence: number;
	attempt: number;
	leaseToken?: string;
	leaseExpiresAt?: number;
	completed: boolean;
}

interface MailboxRow {
	message_id: string;
	payload_json: string;
	kind: string;
	moment_json: string;
	sender_id: string | null;
	correlation_id: string | null;
	attempt: number;
	lease_token: string | null;
	lease_expires_at: number | null;
	completed: number;
	world_id: string;
	save_id: string;
	timeline_id: string;
	generation: number;
	owner_id: string;
	session_id: string;
	actor_id: string;
	recipient_id: string;
}

const maximumRecipientsPerQuery = 4096;

function clone<T>(value: T): T {
	return structuredClone(value);
}

function boundedId(value: string, name: string, maximum = 1024): string {
	if (!value || value.length > maximum) throw new TypeError(`${name} must be a bounded non-empty identifier.`);
	for (const character of value) {
		const code = character.codePointAt(0) ?? 0;
		if (code < 32 || code === 127) throw new TypeError(`${name} contains a control character.`);
	}
	return value;
}

function validateFinite(value: number, name: string): void {
	if (!Number.isFinite(value)) throw new TypeError(`${name} must be finite.`);
}

function validateMessage(message: GameMailboxMessage, maximumPayloadBytes: number): void {
	boundedId(message.id, "Message id");
	boundedId(message.recipientId, "Recipient id");
	boundedId(message.kind, "Message kind", 192);
	if (message.senderId !== undefined) boundedId(message.senderId, "Sender id");
	if (message.correlationId !== undefined) boundedId(message.correlationId, "Correlation id");
	for (const [name, value] of Object.entries(message.session)) {
		if (name === "generation") {
			if (!Number.isSafeInteger(value) || (value as number) < 0) throw new TypeError("Session generation is invalid.");
		} else boundedId(value as string, `Session ${name}`);
	}
	validateFinite(message.moment.tick, "Message tick");
	const serialized = JSON.stringify(message.payload);
	if (Buffer.byteLength(serialized, "utf8") > maximumPayloadBytes)
		throw new RangeError("Mailbox payload exceeds the configured byte limit.");
}

function validateClaim(maximum: number, operationalNow: number, leaseMilliseconds: number): void {
	if (!Number.isInteger(maximum) || maximum < 1 || maximum > 256) throw new RangeError("maximum must be 1..256.");
	validateFinite(operationalNow, "Operational time");
	if (!Number.isInteger(leaseMilliseconds) || leaseMilliseconds < 1 || leaseMilliseconds > 86_400_000)
		throw new RangeError("leaseMilliseconds must be 1..86400000.");
	if (!Number.isSafeInteger(operationalNow + leaseMilliseconds)) throw new RangeError("Lease expiry is out of range.");
}

function keyValues(key: GameMailboxRecipientKey): readonly (string | number)[] {
	return [
		key.session.worldId,
		key.session.saveId,
		key.session.timelineId,
		key.session.generation,
		key.session.ownerId,
		key.session.sessionId,
		key.session.actorId,
		key.recipientId,
	];
}

function keyString(key: GameMailboxRecipientKey): string {
	return JSON.stringify(keyValues(key));
}

function sameMessage(left: GameMailboxMessage, right: GameMailboxMessage): boolean {
	return JSON.stringify(left) === JSON.stringify(right);
}

function validateRecipients(recipients: readonly GameMailboxRecipientKey[], operationalNow: number): void {
	validateFinite(operationalNow, "Operational time");
	if (recipients.length > maximumRecipientsPerQuery)
		throw new RangeError(`A pending query may contain at most ${maximumRecipientsPerQuery} recipients.`);
	for (const recipient of recipients) boundedId(recipient.recipientId, "Recipient id");
}

export class InMemoryGameMailbox implements GameMailbox {
	private readonly entries = new Map<string, MemoryEntry>();
	private sequence = 0;
	private incompleteCount = 0;

	constructor(
		private readonly capacity = 100_000,
		private readonly maximumPayloadBytes = 1024 * 1024,
	) {
		if (!Number.isInteger(capacity) || capacity < 1) throw new RangeError("capacity must be positive.");
	}

	async enqueue(message: GameMailboxMessage, signal?: AbortSignal): Promise<boolean> {
		signal?.throwIfAborted();
		validateMessage(message, this.maximumPayloadBytes);
		const existing = this.entries.get(message.id);
		if (existing) {
			if (!sameMessage(existing.message, message))
				throw new Error("A mailbox message id identifies different content.");
			return false;
		}
		if (this.incompleteCount >= this.capacity) throw new Error("Mailbox capacity is exhausted.");
		this.sequence += 1;
		this.entries.set(message.id, { message: clone(message), sequence: this.sequence, attempt: 0, completed: false });
		this.incompleteCount += 1;
		return true;
	}

	async claim(
		recipient: GameMailboxRecipientKey,
		maximum: number,
		operationalNow: number,
		leaseMilliseconds: number,
		signal?: AbortSignal,
	): Promise<readonly GameMailboxDelivery[]> {
		signal?.throwIfAborted();
		validateClaim(maximum, operationalNow, leaseMilliseconds);
		const expected = keyString(recipient);
		const selected = [...this.entries.values()]
			.filter(
				(entry) =>
					!entry.completed &&
					keyString({ session: entry.message.session, recipientId: entry.message.recipientId }) === expected &&
					(entry.leaseToken === undefined || (entry.leaseExpiresAt ?? 0) <= operationalNow),
			)
			.sort((left, right) => left.sequence - right.sequence)
			.slice(0, maximum);
		return selected.map((entry) => {
			entry.attempt += 1;
			entry.leaseToken = randomBytes(24).toString("base64url");
			entry.leaseExpiresAt = operationalNow + leaseMilliseconds;
			return {
				message: clone(entry.message),
				leaseToken: entry.leaseToken,
				attempt: entry.attempt,
				leaseExpiresAt: entry.leaseExpiresAt,
			};
		});
	}

	complete(
		recipient: GameMailboxRecipientKey,
		messageId: string,
		leaseToken: string,
		signal?: AbortSignal,
	): Promise<void> {
		return this.settle(recipient, messageId, leaseToken, true, signal);
	}

	abandon(
		recipient: GameMailboxRecipientKey,
		messageId: string,
		leaseToken: string,
		signal?: AbortSignal,
	): Promise<void> {
		return this.settle(recipient, messageId, leaseToken, false, signal);
	}

	async readPendingStatus(
		recipients: readonly GameMailboxRecipientKey[],
		operationalNow: number,
		signal?: AbortSignal,
	): Promise<readonly GameMailboxPendingStatus[]> {
		signal?.throwIfAborted();
		validateRecipients(recipients, operationalNow);
		const counts = new Map<string, { ready: number; leased: number }>();
		for (const recipient of recipients) counts.set(keyString(recipient), { ready: 0, leased: 0 });
		for (const entry of this.entries.values()) {
			signal?.throwIfAborted();
			if (entry.completed) continue;
			const count = counts.get(keyString({ session: entry.message.session, recipientId: entry.message.recipientId }));
			if (!count) continue;
			if (entry.leaseToken !== undefined && (entry.leaseExpiresAt ?? 0) > operationalNow) count.leased += 1;
			else count.ready += 1;
		}
		return recipients.map((recipient) => {
			const count = counts.get(keyString(recipient)) ?? { ready: 0, leased: 0 };
			return {
				recipient: clone(recipient),
				readyCount: count.ready,
				leasedCount: count.leased,
				incompleteCount: count.ready + count.leased,
			};
		});
	}

	private async settle(
		recipient: GameMailboxRecipientKey,
		messageId: string,
		leaseToken: string,
		complete: boolean,
		signal?: AbortSignal,
	): Promise<void> {
		signal?.throwIfAborted();
		boundedId(messageId, "Message id");
		boundedId(leaseToken, "Lease token");
		const entry = this.entries.get(messageId);
		if (
			!entry ||
			keyString({ session: entry.message.session, recipientId: entry.message.recipientId }) !== keyString(recipient)
		)
			throw new Error("Mailbox message was not found for the recipient.");
		if (entry.completed) throw new Error("Mailbox message is already complete.");
		if (entry.leaseToken !== leaseToken) throw new Error("Mailbox lease is stale or invalid.");
		entry.completed = complete;
		if (complete) this.incompleteCount -= 1;
		delete entry.leaseToken;
		delete entry.leaseExpiresAt;
	}
}

export interface SqliteGameMailboxOptions {
	capacity?: number;
	maximumPayloadBytes?: number;
	completedRetention?: number;
}

export class SqliteGameMailbox implements GameMailbox, Disposable {
	private readonly database: DatabaseSync;
	private readonly capacity: number;
	private readonly maximumPayloadBytes: number;
	private readonly completedRetention: number;
	private closed = false;

	constructor(path: string, options: SqliteGameMailboxOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.capacity = options.capacity ?? 100_000;
		this.maximumPayloadBytes = options.maximumPayloadBytes ?? 1024 * 1024;
		this.completedRetention = options.completedRetention ?? 10_000;
		for (const [name, value, minimum] of [
			["capacity", this.capacity, 1],
			["maximumPayloadBytes", this.maximumPayloadBytes, 1024],
			["completedRetention", this.completedRetention, 0],
		] as const) {
			if (!Number.isInteger(value) || value < minimum) throw new RangeError(`${name} is invalid.`);
		}
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
		);
		this.database.exec(`CREATE TABLE IF NOT EXISTS game_mailbox (
			sequence INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL UNIQUE,
			world_id TEXT NOT NULL, save_id TEXT NOT NULL, timeline_id TEXT NOT NULL, generation INTEGER NOT NULL,
			owner_id TEXT NOT NULL, session_id TEXT NOT NULL, actor_id TEXT NOT NULL, recipient_id TEXT NOT NULL,
			kind TEXT NOT NULL, payload_json TEXT NOT NULL, moment_json TEXT NOT NULL, sender_id TEXT, correlation_id TEXT,
			attempt INTEGER NOT NULL DEFAULT 0 CHECK(attempt >= 0), lease_token TEXT, lease_expires_at INTEGER,
			completed INTEGER NOT NULL DEFAULT 0 CHECK(completed IN (0,1)),
			CHECK((lease_token IS NULL AND lease_expires_at IS NULL) OR (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)),
			CHECK(completed=0 OR lease_token IS NULL)
		) STRICT;
		CREATE INDEX IF NOT EXISTS game_mailbox_recipient ON game_mailbox(
			world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,recipient_id,completed,lease_expires_at,sequence);`);
	}

	async enqueue(message: GameMailboxMessage, signal?: AbortSignal): Promise<boolean> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateMessage(message, this.maximumPayloadBytes);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const existing = this.database
				.prepare("SELECT * FROM game_mailbox WHERE message_id=?")
				.get(message.id) as unknown as MailboxRow | undefined;
			if (existing) {
				if (!sameMessage(this.rowMessage(existing), message))
					throw new Error("A mailbox message id identifies different content.");
				this.database.exec("COMMIT");
				return false;
			}
			const count = this.database
				.prepare("SELECT COUNT(*) AS count FROM game_mailbox WHERE completed=0")
				.get() as unknown as {
				count: number;
			};
			if (count.count >= this.capacity) throw new Error("Mailbox capacity is exhausted.");
			this.database
				.prepare(`INSERT INTO game_mailbox(message_id,world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,
					recipient_id,kind,payload_json,moment_json,sender_id,correlation_id) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)`)
				.run(
					message.id,
					...keyValues({ session: message.session, recipientId: message.recipientId }),
					message.kind,
					JSON.stringify(message.payload),
					JSON.stringify(message.moment),
					message.senderId ?? null,
					message.correlationId ?? null,
				);
			this.pruneCompleted();
			this.database.exec("COMMIT");
			return true;
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async claim(
		recipient: GameMailboxRecipientKey,
		maximum: number,
		operationalNow: number,
		leaseMilliseconds: number,
		signal?: AbortSignal,
	): Promise<readonly GameMailboxDelivery[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateClaim(maximum, operationalNow, leaseMilliseconds);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const rows = this.database
				.prepare(`SELECT * FROM game_mailbox WHERE world_id=? AND save_id=? AND timeline_id=? AND generation=?
					AND owner_id=? AND session_id=? AND actor_id=? AND recipient_id=? AND completed=0
					AND (lease_token IS NULL OR lease_expires_at<=?) ORDER BY sequence LIMIT ?`)
				.all(...keyValues(recipient), operationalNow, maximum) as unknown as MailboxRow[];
			const deliveries: GameMailboxDelivery[] = [];
			for (const row of rows) {
				signal?.throwIfAborted();
				const leaseToken = randomBytes(24).toString("base64url");
				const leaseExpiresAt = operationalNow + leaseMilliseconds;
				const result = this.database
					.prepare(`UPDATE game_mailbox SET attempt=attempt+1,lease_token=?,lease_expires_at=?
						WHERE message_id=? AND completed=0 AND (lease_token IS NULL OR lease_expires_at<=?)`)
					.run(leaseToken, leaseExpiresAt, row.message_id, operationalNow);
				if (result.changes !== 1) throw new Error("Mailbox claim lost its transaction fence.");
				deliveries.push({
					message: this.rowMessage(row),
					leaseToken,
					attempt: row.attempt + 1,
					leaseExpiresAt,
				});
			}
			this.database.exec("COMMIT");
			return deliveries;
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	complete(
		recipient: GameMailboxRecipientKey,
		messageId: string,
		leaseToken: string,
		signal?: AbortSignal,
	): Promise<void> {
		return this.settle(recipient, messageId, leaseToken, true, signal);
	}

	abandon(
		recipient: GameMailboxRecipientKey,
		messageId: string,
		leaseToken: string,
		signal?: AbortSignal,
	): Promise<void> {
		return this.settle(recipient, messageId, leaseToken, false, signal);
	}

	async readPendingStatus(
		recipients: readonly GameMailboxRecipientKey[],
		operationalNow: number,
		signal?: AbortSignal,
	): Promise<readonly GameMailboxPendingStatus[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		validateRecipients(recipients, operationalNow);
		if (recipients.length === 0) return [];
		const requested = JSON.stringify(
			recipients.map((recipient, ordinal) => ({ ordinal, ...recipient.session, recipientId: recipient.recipientId })),
		);
		const rows = this.database
			.prepare(`WITH requested AS (
				SELECT CAST(key AS INTEGER) ordinal,
					json_extract(value,'$.worldId') world_id,json_extract(value,'$.saveId') save_id,
					json_extract(value,'$.timelineId') timeline_id,json_extract(value,'$.generation') generation,
					json_extract(value,'$.ownerId') owner_id,json_extract(value,'$.sessionId') session_id,
					json_extract(value,'$.actorId') actor_id,json_extract(value,'$.recipientId') recipient_id
				FROM json_each(?)
			)
			SELECT requested.ordinal,
				COALESCE(SUM(CASE WHEN mailbox.message_id IS NOT NULL AND (mailbox.lease_token IS NULL OR mailbox.lease_expires_at<=?) THEN 1 ELSE 0 END),0) ready,
				COALESCE(SUM(CASE WHEN mailbox.message_id IS NOT NULL AND mailbox.lease_token IS NOT NULL AND mailbox.lease_expires_at>? THEN 1 ELSE 0 END),0) leased
			FROM requested LEFT JOIN game_mailbox mailbox ON mailbox.world_id=requested.world_id AND mailbox.save_id=requested.save_id
				AND mailbox.timeline_id=requested.timeline_id AND mailbox.generation=requested.generation
				AND mailbox.owner_id=requested.owner_id AND mailbox.session_id=requested.session_id
				AND mailbox.actor_id=requested.actor_id AND mailbox.recipient_id=requested.recipient_id AND mailbox.completed=0
			GROUP BY requested.ordinal ORDER BY requested.ordinal`)
			.all(requested, operationalNow, operationalNow) as unknown as Array<{
			ordinal: number;
			ready: number;
			leased: number;
		}>;
		if (rows.length !== recipients.length) throw new Error("Mailbox pending index is corrupt.");
		return rows.map((row) => {
			const recipient = recipients[row.ordinal];
			if (!recipient || row.ready < 0 || row.leased < 0) throw new Error("Mailbox pending index is corrupt.");
			return {
				recipient: clone(recipient),
				readyCount: row.ready,
				leasedCount: row.leased,
				incompleteCount: row.ready + row.leased,
			};
		});
	}

	[Symbol.dispose](): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	private async settle(
		recipient: GameMailboxRecipientKey,
		messageId: string,
		leaseToken: string,
		complete: boolean,
		signal?: AbortSignal,
	): Promise<void> {
		this.ensureOpen();
		signal?.throwIfAborted();
		boundedId(messageId, "Message id");
		boundedId(leaseToken, "Lease token");
		const result = this.database
			.prepare(`UPDATE game_mailbox SET completed=?,lease_token=NULL,lease_expires_at=NULL
				WHERE message_id=? AND world_id=? AND save_id=? AND timeline_id=? AND generation=? AND owner_id=?
				AND session_id=? AND actor_id=? AND recipient_id=? AND completed=0 AND lease_token=?`)
			.run(complete ? 1 : 0, messageId, ...keyValues(recipient), leaseToken);
		if (result.changes !== 1) throw new Error("Mailbox message or lease was not found for the recipient.");
	}

	private rowMessage(row: MailboxRow): GameMailboxMessage {
		let payload: JsonValue;
		let moment: GameMoment;
		try {
			payload = JSON.parse(row.payload_json) as JsonValue;
			moment = JSON.parse(row.moment_json) as GameMoment;
		} catch {
			throw new Error("Mailbox storage contains corrupt JSON.");
		}
		const message: GameMailboxMessage = {
			id: row.message_id,
			session: {
				worldId: row.world_id,
				saveId: row.save_id,
				timelineId: row.timeline_id,
				generation: row.generation,
				ownerId: row.owner_id,
				sessionId: row.session_id,
				actorId: row.actor_id,
			},
			recipientId: row.recipient_id,
			kind: row.kind,
			payload,
			moment,
			...(row.sender_id === null ? {} : { senderId: row.sender_id }),
			...(row.correlation_id === null ? {} : { correlationId: row.correlation_id }),
		};
		validateMessage(message, this.maximumPayloadBytes);
		return message;
	}

	private pruneCompleted(): void {
		this.database
			.prepare(`DELETE FROM game_mailbox WHERE sequence IN (
				SELECT sequence FROM game_mailbox WHERE completed=1 ORDER BY sequence DESC LIMIT -1 OFFSET ?)`)
			.run(this.completedRetention);
	}

	private ensureOpen(): void {
		if (this.closed) throw new Error("Mailbox is closed.");
	}
}

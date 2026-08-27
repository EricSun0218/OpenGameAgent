import { DatabaseSync } from "node:sqlite";
import type { GameActionClaim, GameActionJournal } from "@opengameagent/actions";
import type {
	GameActionIntent,
	GameActionJournalEntry,
	GameActionJournalStatus,
	GameActionReceipt,
} from "@opengameagent/protocol";

export interface SqliteGameActionJournalOptions {
	maximumRecordBytes?: number;
}

interface ActionRow {
	operation_id: string;
	intent_json: string;
	status: string;
	attempt: number;
	prepared_at: number;
	dispatched_at: number | null;
	receipt_json: string | null;
}

const statuses = new Set<GameActionJournalStatus>([
	"prepared",
	"dispatched",
	"uncertain",
	"committed",
	"rejected",
	"failed",
]);

function parseJson<T>(json: string, label: string): T {
	try {
		return JSON.parse(json) as T;
	} catch {
		throw new Error(`Stored ${label} is corrupt.`);
	}
}

function sameSession(left: GameActionIntent["session"], right: GameActionReceipt["session"]): boolean {
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

export class SqliteGameActionJournal implements GameActionJournal, Disposable {
	private readonly database: DatabaseSync;
	private readonly maximumRecordBytes: number;
	private closed = false;

	constructor(path: string, options: SqliteGameActionJournalOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.maximumRecordBytes = options.maximumRecordBytes ?? 1024 * 1024;
		if (!Number.isInteger(this.maximumRecordBytes) || this.maximumRecordBytes < 1024) {
			throw new RangeError("maximumRecordBytes must be an integer of at least 1024 bytes.");
		}
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000; PRAGMA trusted_schema = OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_action_journal (
				operation_id TEXT PRIMARY KEY,
				intent_json TEXT NOT NULL,
				status TEXT NOT NULL CHECK (status IN ('prepared','dispatched','uncertain','committed','rejected','failed')),
				attempt INTEGER NOT NULL CHECK (attempt >= 0),
				prepared_at INTEGER NOT NULL,
				dispatched_at INTEGER,
				receipt_json TEXT,
				world_id TEXT NOT NULL,
				save_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation INTEGER NOT NULL,
				conflict_key TEXT
			) STRICT;
			CREATE INDEX IF NOT EXISTS ix_game_action_conflict
			ON game_action_journal(world_id, save_id, timeline_id, generation, conflict_key, status, prepared_at);
		`);
	}

	async prepare(intent: GameActionIntent, signal?: AbortSignal): Promise<GameActionJournalEntry> {
		this.ensureOpen();
		signal?.throwIfAborted();
		const json = this.serialize(intent, "action intent");
		return this.transaction(() => {
			const current = this.readRow(intent.operationId);
			if (current) {
				if (current.intent_json !== json) throw new Error("Operation ID already identifies a different action intent.");
				return this.toEntry(current);
			}
			const preparedAt = Date.now();
			this.database
				.prepare(`
					INSERT INTO game_action_journal (
						operation_id, intent_json, status, attempt, prepared_at,
						world_id, save_id, timeline_id, generation, conflict_key
					) VALUES (?, ?, 'prepared', 0, ?, ?, ?, ?, ?, ?)
				`)
				.run(
					intent.operationId,
					json,
					preparedAt,
					intent.session.worldId,
					intent.session.saveId,
					intent.session.timelineId,
					intent.session.generation,
					intent.conflictKey ?? null,
				);
			return this.toEntry(this.requireRow(intent.operationId));
		});
	}

	async claimDispatch(operationId: string, signal?: AbortSignal): Promise<GameActionClaim> {
		this.ensureOpen();
		signal?.throwIfAborted();
		return this.transaction(() => {
			const row = this.requireRow(operationId);
			const entry = this.toEntry(row);
			if (entry.status === "dispatched" || entry.status === "uncertain") return { kind: "reconcile", entry };
			if (entry.status !== "prepared") return { kind: "terminal", entry };
			if (entry.intent.conflictKey) {
				const blocker = this.database
					.prepare(`
						SELECT operation_id FROM game_action_journal
						WHERE operation_id <> ? AND world_id = ? AND save_id = ? AND timeline_id = ?
							AND generation = ? AND conflict_key = ? AND status IN ('dispatched','uncertain')
						ORDER BY prepared_at, operation_id LIMIT 1
					`)
					.get(
						operationId,
						entry.intent.session.worldId,
						entry.intent.session.saveId,
						entry.intent.session.timelineId,
						entry.intent.session.generation,
						entry.intent.conflictKey,
					) as { operation_id: string } | undefined;
				if (blocker) return { kind: "blocked", entry, blockingOperationId: blocker.operation_id };
			}
			this.database
				.prepare(
					"UPDATE game_action_journal SET status = 'dispatched', attempt = attempt + 1, dispatched_at = ? WHERE operation_id = ?",
				)
				.run(Date.now(), operationId);
			return { kind: "dispatch", entry: this.toEntry(this.requireRow(operationId)) };
		});
	}

	async markUncertain(operationId: string, signal?: AbortSignal): Promise<GameActionJournalEntry> {
		this.ensureOpen();
		signal?.throwIfAborted();
		return this.transaction(() => {
			const entry = this.toEntry(this.requireRow(operationId));
			if (entry.status === "dispatched") {
				this.database
					.prepare("UPDATE game_action_journal SET status = 'uncertain' WHERE operation_id = ?")
					.run(operationId);
			} else if (entry.status !== "uncertain") {
				throw new Error("Only a dispatched action can become uncertain.");
			}
			return this.toEntry(this.requireRow(operationId));
		});
	}

	async submitReceipt(receipt: GameActionReceipt, signal?: AbortSignal): Promise<GameActionJournalEntry> {
		this.ensureOpen();
		signal?.throwIfAborted();
		const receiptJson = this.serialize(receipt, "action receipt");
		return this.transaction(() => {
			const row = this.requireRow(receipt.operationId);
			const entry = this.toEntry(row);
			if (!sameSession(entry.intent.session, receipt.session))
				throw new Error("Action receipt session does not match its intent.");
			if (entry.intent.action !== receipt.action) throw new Error("Action receipt name does not match its intent.");
			if (entry.intent.expectedRevision !== receipt.expectedRevision) {
				throw new Error("Action receipt expected revision does not match its intent.");
			}
			if (row.receipt_json !== null) {
				if (row.receipt_json !== receiptJson) throw new Error("Conflicting receipt for a terminal action.");
				return entry;
			}
			if (entry.status !== "dispatched" && entry.status !== "uncertain") {
				throw new Error("A receipt is only valid after dispatch.");
			}
			this.database
				.prepare("UPDATE game_action_journal SET status = ?, receipt_json = ? WHERE operation_id = ?")
				.run(receipt.status, receiptJson, receipt.operationId);
			return this.toEntry(this.requireRow(receipt.operationId));
		});
	}

	async read(operationId: string, signal?: AbortSignal): Promise<GameActionJournalEntry | undefined> {
		this.ensureOpen();
		signal?.throwIfAborted();
		const row = this.readRow(operationId);
		return row ? this.toEntry(row) : undefined;
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	[Symbol.dispose](): void {
		this.close();
	}

	private serialize(value: unknown, label: string): string {
		const json = JSON.stringify(value);
		if (Buffer.byteLength(json) > this.maximumRecordBytes)
			throw new RangeError(`${label} exceeds the configured size limit.`);
		return json;
	}

	private toEntry(row: ActionRow): GameActionJournalEntry {
		if (!statuses.has(row.status as GameActionJournalStatus)) throw new Error("Stored action status is corrupt.");
		if (!Number.isInteger(row.attempt) || row.attempt < 0) throw new Error("Stored action attempt is corrupt.");
		const intent = parseJson<GameActionIntent>(row.intent_json, "action intent");
		if (intent.operationId !== row.operation_id) throw new Error("Stored action intent identity is corrupt.");
		return {
			intent,
			status: row.status as GameActionJournalStatus,
			attempt: row.attempt,
			preparedAt: row.prepared_at,
			...(row.dispatched_at === null ? {} : { dispatchedAt: row.dispatched_at }),
			...(row.receipt_json === null
				? {}
				: { receipt: parseJson<GameActionReceipt>(row.receipt_json, "action receipt") }),
		};
	}

	private readRow(operationId: string): ActionRow | undefined {
		return this.database
			.prepare(`
				SELECT operation_id, intent_json, status, attempt, prepared_at, dispatched_at, receipt_json
				FROM game_action_journal WHERE operation_id = ?
			`)
			.get(operationId) as ActionRow | undefined;
	}

	private requireRow(operationId: string): ActionRow {
		const row = this.readRow(operationId);
		if (!row) throw new Error(`Unknown action operation '${operationId}'.`);
		return row;
	}

	private transaction<T>(action: () => T): T {
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const result = action();
			this.database.exec("COMMIT");
			return result;
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	private ensureOpen(): void {
		if (this.closed) throw new Error("SQLite action journal is closed.");
	}
}

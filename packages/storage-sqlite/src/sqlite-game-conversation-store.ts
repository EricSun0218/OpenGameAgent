import { DatabaseSync } from "node:sqlite";
import type {
	GameConversationMessage,
	GameConversationSnapshot,
	GameConversationStore,
	GameSessionKey,
} from "@opengameagent/protocol";

export interface SqliteGameConversationStoreOptions {
	maximumTranscriptBytes?: number;
}

interface ConversationRow {
	revision: number;
	messages_json: string;
}

export class SqliteGameConversationStore implements GameConversationStore, Disposable {
	private readonly database: DatabaseSync;
	private readonly maximumTranscriptBytes: number;
	private closed = false;

	constructor(path: string, options: SqliteGameConversationStoreOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.maximumTranscriptBytes = options.maximumTranscriptBytes ?? 16 * 1024 * 1024;
		if (!Number.isInteger(this.maximumTranscriptBytes) || this.maximumTranscriptBytes < 1024) {
			throw new RangeError("maximumTranscriptBytes must be an integer of at least 1024 bytes.");
		}
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000; PRAGMA trusted_schema = OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_conversations (
				world_id TEXT NOT NULL,
				save_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL,
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				revision INTEGER NOT NULL CHECK (revision >= 1),
				messages_json TEXT NOT NULL,
				PRIMARY KEY (world_id, save_id, timeline_id, generation, owner_id, session_id, actor_id)
			) STRICT;
		`);
	}

	async read(session: GameSessionKey, signal?: AbortSignal): Promise<GameConversationSnapshot> {
		this.ensureOpen();
		signal?.throwIfAborted();
		const row = this.readRow(session);
		if (!row) return { revision: 0, messages: [] };
		return this.toSnapshot(row);
	}

	async save(
		session: GameSessionKey,
		expectedRevision: number,
		messages: readonly GameConversationMessage[],
		signal?: AbortSignal,
	): Promise<GameConversationSnapshot> {
		this.ensureOpen();
		signal?.throwIfAborted();
		if (!Number.isInteger(expectedRevision) || expectedRevision < 0)
			throw new RangeError("expectedRevision must be non-negative.");
		const json = JSON.stringify(messages);
		if (Buffer.byteLength(json) > this.maximumTranscriptBytes) {
			throw new RangeError("Conversation transcript exceeds the configured size limit.");
		}
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const current = this.readRow(session);
			if ((current?.revision ?? 0) !== expectedRevision) throw new Error("Conversation revision conflict.");
			const nextRevision = expectedRevision + 1;
			if (current) {
				this.database
					.prepare(`
						UPDATE game_conversations SET revision = ?, messages_json = ?
						WHERE world_id = ? AND save_id = ? AND timeline_id = ? AND generation = ?
							AND owner_id = ? AND session_id = ? AND actor_id = ? AND revision = ?
					`)
					.run(nextRevision, json, ...this.keyValues(session), expectedRevision);
			} else {
				this.database
					.prepare(`
						INSERT INTO game_conversations (
							world_id, save_id, timeline_id, generation, owner_id, session_id, actor_id, revision, messages_json
						) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
					`)
					.run(...this.keyValues(session), nextRevision, json);
			}
			this.database.exec("COMMIT");
			return { revision: nextRevision, messages: structuredClone(messages) };
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	[Symbol.dispose](): void {
		this.close();
	}

	private readRow(session: GameSessionKey): ConversationRow | undefined {
		return this.database
			.prepare(`
				SELECT revision, messages_json FROM game_conversations
				WHERE world_id = ? AND save_id = ? AND timeline_id = ? AND generation = ?
					AND owner_id = ? AND session_id = ? AND actor_id = ?
			`)
			.get(...this.keyValues(session)) as ConversationRow | undefined;
	}

	private toSnapshot(row: ConversationRow): GameConversationSnapshot {
		if (!Number.isInteger(row.revision) || row.revision < 1)
			throw new Error("Stored conversation revision is corrupt.");
		try {
			const messages = JSON.parse(row.messages_json) as GameConversationMessage[];
			if (!Array.isArray(messages)) throw new Error();
			return { revision: row.revision, messages };
		} catch {
			throw new Error("Stored conversation transcript is corrupt.");
		}
	}

	private keyValues(session: GameSessionKey): [string, string, string, number, string, string, string] {
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

	private ensureOpen(): void {
		if (this.closed) throw new Error("SQLite conversation store is closed.");
	}
}

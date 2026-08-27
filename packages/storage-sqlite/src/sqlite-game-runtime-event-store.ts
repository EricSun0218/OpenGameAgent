import { DatabaseSync } from "node:sqlite";
import type { GameAgentEvent, GameInput, GameSessionKey } from "@opengameagent/protocol";
import type { GameRuntimeEventStore } from "@opengameagent/runtime";

export interface SqliteGameRuntimeEventStoreOptions {
	maximumEventBytes?: number;
}

interface EventRow {
	sequence: number;
	event_id: string;
	event_json: string;
}

export class SqliteGameRuntimeEventStore implements GameRuntimeEventStore, Disposable {
	private readonly database: DatabaseSync;
	private readonly maximumEventBytes: number;
	private closed = false;

	constructor(path: string, options: SqliteGameRuntimeEventStoreOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.maximumEventBytes = options.maximumEventBytes ?? 1024 * 1024;
		if (!Number.isInteger(this.maximumEventBytes) || this.maximumEventBytes < 1024) {
			throw new RangeError("maximumEventBytes must be an integer of at least 1024 bytes.");
		}
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000; PRAGMA trusted_schema = OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_runtime_events (
				world_id TEXT NOT NULL,
				save_id TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL,
				session_id TEXT NOT NULL,
				actor_id TEXT NOT NULL,
				run_id TEXT NOT NULL,
				sequence INTEGER NOT NULL CHECK (sequence >= 1),
				event_id TEXT NOT NULL,
				event_json TEXT NOT NULL,
				PRIMARY KEY (world_id, save_id, timeline_id, generation, owner_id, session_id, actor_id, run_id, sequence)
			) STRICT;
		`);
	}

	async append(input: GameInput, event: GameAgentEvent, signal: AbortSignal): Promise<void> {
		this.ensureOpen();
		signal.throwIfAborted();
		if (event.runId.length === 0 || !Number.isInteger(event.sequence) || event.sequence < 1) {
			throw new Error("Runtime event coordinates are invalid.");
		}
		const json = JSON.stringify(event);
		if (Buffer.byteLength(json) > this.maximumEventBytes)
			throw new RangeError("Runtime event exceeds the configured size limit.");
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const existing = this.database
				.prepare(`
					SELECT event_id, event_json FROM game_runtime_events
					WHERE world_id = ? AND save_id = ? AND timeline_id = ? AND generation = ?
						AND owner_id = ? AND session_id = ? AND actor_id = ? AND run_id = ? AND sequence = ?
				`)
				.get(...this.keyValues(input.session), event.runId, event.sequence) as
				| { event_id: string; event_json: string }
				| undefined;
			if (existing) {
				if (existing.event_id !== event.eventId || existing.event_json !== json) {
					throw new Error("Runtime event coordinates identify different event content.");
				}
			} else {
				this.database
					.prepare(`
						INSERT INTO game_runtime_events (
							world_id, save_id, timeline_id, generation, owner_id, session_id, actor_id,
							run_id, sequence, event_id, event_json
						) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
					`)
					.run(...this.keyValues(input.session), event.runId, event.sequence, event.eventId, json);
			}
			this.database.exec("COMMIT");
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async read(
		session: GameSessionKey,
		runId: string,
		afterSequence: number,
		maximum: number,
		signal?: AbortSignal,
	): Promise<readonly GameAgentEvent[]> {
		this.ensureOpen();
		signal?.throwIfAborted();
		if (!runId) throw new TypeError("runId is required.");
		if (!Number.isInteger(afterSequence) || afterSequence < 0)
			throw new RangeError("afterSequence must be non-negative.");
		if (!Number.isInteger(maximum) || maximum < 1 || maximum > 1000)
			throw new RangeError("maximum must be between 1 and 1000.");
		const rows = this.database
			.prepare(`
				SELECT sequence, event_id, event_json FROM game_runtime_events
				WHERE world_id = ? AND save_id = ? AND timeline_id = ? AND generation = ?
					AND owner_id = ? AND session_id = ? AND actor_id = ? AND run_id = ? AND sequence > ?
				ORDER BY sequence LIMIT ?
			`)
			.all(...this.keyValues(session), runId, afterSequence, maximum) as unknown as EventRow[];
		return rows.map((row) => this.parseRow(row, runId));
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	[Symbol.dispose](): void {
		this.close();
	}

	private parseRow(row: EventRow, runId: string): GameAgentEvent {
		try {
			const event = JSON.parse(row.event_json) as GameAgentEvent;
			if (event.eventId !== row.event_id || event.sequence !== row.sequence || event.runId !== runId) throw new Error();
			return event;
		} catch {
			throw new Error("Stored runtime event is corrupt.");
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
		if (this.closed) throw new Error("SQLite runtime event store is closed.");
	}
}

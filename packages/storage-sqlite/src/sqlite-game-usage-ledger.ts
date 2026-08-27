import { DatabaseSync } from "node:sqlite";
import type { GameSessionKey } from "@opengameagent/protocol";
import type {
	GameUsageCause,
	GameUsageEntry,
	GameUsageLedger,
	GameUsageSummary,
	GameUsageTotal,
} from "@opengameagent/runtime";

interface UsageRow {
	cause: GameUsageCause;
	records: number;
	input_tokens: number;
	output_tokens: number;
	cache_read_tokens: number;
	cache_write_tokens: number;
	reasoning_tokens: number;
	total_tokens: number;
	unknown_cost_records: number;
	total_cost: number;
}

function emptyTotal(): GameUsageTotal {
	return {
		records: 0,
		input: 0,
		output: 0,
		cacheRead: 0,
		cacheWrite: 0,
		reasoning: 0,
		totalTokens: 0,
		unknownCostRecords: 0,
		cost: 0,
	};
}

function fromRow(row: UsageRow): GameUsageTotal {
	return {
		records: row.records,
		input: row.input_tokens,
		output: row.output_tokens,
		cacheRead: row.cache_read_tokens,
		cacheWrite: row.cache_write_tokens,
		reasoning: row.reasoning_tokens,
		totalTokens: row.total_tokens,
		unknownCostRecords: row.unknown_cost_records,
		cost: row.unknown_cost_records > 0 ? null : row.total_cost,
	};
}

function addTotal(target: GameUsageTotal, value: GameUsageTotal): void {
	target.records += value.records;
	target.input += value.input;
	target.output += value.output;
	target.cacheRead += value.cacheRead;
	target.cacheWrite += value.cacheWrite;
	target.reasoning += value.reasoning;
	target.totalTokens += value.totalTokens;
	target.unknownCostRecords += value.unknownCostRecords;
	target.cost = target.cost === null || value.cost === null ? null : target.cost + value.cost;
}

export class SqliteGameUsageLedger implements GameUsageLedger, Disposable {
	private readonly database: DatabaseSync;
	private closed = false;

	constructor(path: string) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000; PRAGMA trusted_schema = OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_usage_ledger (
				world_id TEXT NOT NULL, save_id TEXT NOT NULL, timeline_id TEXT NOT NULL,
				generation INTEGER NOT NULL, owner_id TEXT NOT NULL, session_id TEXT NOT NULL, actor_id TEXT NOT NULL,
				record_id TEXT NOT NULL, input_id TEXT NOT NULL, run_id TEXT NOT NULL, turn INTEGER NOT NULL,
				cause TEXT NOT NULL, provider TEXT, model TEXT, response_id TEXT,
				input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
				cache_read_tokens INTEGER NOT NULL, cache_write_tokens INTEGER NOT NULL,
				reasoning_tokens INTEGER NOT NULL, total_tokens INTEGER NOT NULL,
				cost_known INTEGER NOT NULL CHECK (cost_known IN (0, 1)), total_cost REAL,
				timestamp INTEGER NOT NULL, entry_json TEXT NOT NULL,
				PRIMARY KEY (world_id, save_id, timeline_id, generation, owner_id, session_id, actor_id, record_id)
			) STRICT;
		`);
	}

	async append(entry: GameUsageEntry, signal?: AbortSignal): Promise<void> {
		this.ensureOpen();
		signal?.throwIfAborted();
		this.validateEntry(entry);
		const json = JSON.stringify(entry);
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const existing = this.database
				.prepare(`SELECT entry_json FROM game_usage_ledger
					WHERE world_id=? AND save_id=? AND timeline_id=? AND generation=?
					AND owner_id=? AND session_id=? AND actor_id=? AND record_id=?`)
				.get(...this.keyValues(entry.session), entry.id) as { entry_json: string } | undefined;
			if (existing && existing.entry_json !== json) throw new Error("Usage record id identifies different content.");
			if (!existing) {
				this.database
					.prepare(`INSERT INTO game_usage_ledger (
						world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,record_id,
						input_id,run_id,turn,cause,provider,model,response_id,input_tokens,output_tokens,
						cache_read_tokens,cache_write_tokens,reasoning_tokens,total_tokens,cost_known,total_cost,timestamp,entry_json
					) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`)
					.run(
						...this.keyValues(entry.session),
						entry.id,
						entry.inputId,
						entry.runId,
						entry.turn,
						entry.cause,
						entry.provider ?? null,
						entry.model ?? null,
						entry.responseId ?? null,
						entry.usage.input,
						entry.usage.output,
						entry.usage.cacheRead,
						entry.usage.cacheWrite,
						entry.usage.reasoning ?? 0,
						entry.usage.totalTokens,
						entry.usage.cost === undefined ? 0 : 1,
						entry.usage.cost?.total ?? null,
						entry.timestamp,
						json,
					);
			}
			this.database.exec("COMMIT");
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async summarize(session: GameSessionKey, signal?: AbortSignal): Promise<GameUsageSummary> {
		this.ensureOpen();
		signal?.throwIfAborted();
		const rows = this.database
			.prepare(`SELECT cause, COUNT(*) AS records,
				SUM(input_tokens) AS input_tokens, SUM(output_tokens) AS output_tokens,
				SUM(cache_read_tokens) AS cache_read_tokens, SUM(cache_write_tokens) AS cache_write_tokens,
				SUM(reasoning_tokens) AS reasoning_tokens, SUM(total_tokens) AS total_tokens,
				SUM(CASE WHEN cost_known=0 THEN 1 ELSE 0 END) AS unknown_cost_records,
				COALESCE(SUM(CASE WHEN cost_known=1 THEN total_cost ELSE 0 END), 0) AS total_cost
				FROM game_usage_ledger WHERE world_id=? AND save_id=? AND timeline_id=? AND generation=?
				AND owner_id=? AND session_id=? AND actor_id=? GROUP BY cause ORDER BY cause`)
			.all(...this.keyValues(session)) as unknown as UsageRow[];
		const total = emptyTotal();
		const byCause: GameUsageSummary["byCause"] = {};
		for (const row of rows) {
			const value = fromRow(row);
			byCause[row.cause] = value;
			addTotal(total, value);
		}
		return { total, byCause };
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	[Symbol.dispose](): void {
		this.close();
	}

	private validateEntry(entry: GameUsageEntry): void {
		if (!entry.id || !entry.inputId || !entry.runId || !Number.isInteger(entry.turn) || entry.turn < 0) {
			throw new TypeError("Usage record coordinates are invalid.");
		}
		for (const value of [
			entry.usage.input,
			entry.usage.output,
			entry.usage.cacheRead,
			entry.usage.cacheWrite,
			entry.usage.reasoning ?? 0,
			entry.usage.totalTokens,
		]) {
			if (!Number.isSafeInteger(value) || value < 0)
				throw new RangeError("Usage token counts must be non-negative integers.");
		}
		if (entry.usage.cost && (!Number.isFinite(entry.usage.cost.total) || entry.usage.cost.total < 0)) {
			throw new RangeError("Usage cost must be a finite non-negative number.");
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
		if (this.closed) throw new Error("SQLite usage ledger is closed.");
	}
}

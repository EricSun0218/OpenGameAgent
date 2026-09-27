import {
	type MemoryAuthority,
	MemoryConflictError,
	type MemoryEmbedding,
	type MemoryExportPage,
	type MemoryIndexPartition,
	type MemoryLimits,
	type MemoryPartitionState,
	MemoryNotFoundError,
	type MemoryRecord,
	MemoryRevisionConflictError,
	type MemoryRevisionPatch,
	type MemorySelector,
	type MemoryViewer,
	type MemoryWrite,
	memoryFingerprint,
	normalizeIndexPartition,
	normalizeMemoryContent,
	normalizeMemorySelector,
	normalizeMemoryViewer,
	normalizeMemoryWrite,
	normalizeRevisionPatch,
	type RankedMemoryCandidate,
	validateMemoryLimits,
	validateQuery,
	validateResultLimit,
	validateRevision,
	validateTimestamp,
	validateVector,
} from "@opengameagent/memory";
import { type Audience, canonicalizeJsonValue } from "@opengameagent/protocol";
import type Database from "better-sqlite3";
import type { SqliteAuthority, SqliteMigration } from "./sqlite-authority.js";

const MEMORY_MIGRATIONS: readonly SqliteMigration[] = [
	{
		version: 1,
		name: "memory-authority-v1-external-fts",
		sql: `
			CREATE TABLE oga_memories (
				memory_key INTEGER PRIMARY KEY AUTOINCREMENT,
				memory_id TEXT NOT NULL UNIQUE,
				owner_id TEXT NOT NULL,
				actor_id TEXT,
				session_id TEXT,
				scope TEXT NOT NULL,
				kind TEXT NOT NULL,
				timeline_id TEXT NOT NULL,
				generation TEXT NOT NULL,
				game_tick TEXT NOT NULL,
				game_tick_digits INTEGER NOT NULL CHECK(game_tick_digits > 0),
				importance REAL NOT NULL CHECK(importance >= 0 AND importance <= 1),
				search_text TEXT NOT NULL,
				content_json TEXT NOT NULL,
				audience_kind TEXT NOT NULL CHECK(audience_kind IN ('internal', 'owner', 'public', 'recipient')),
				lifecycle TEXT NOT NULL CHECK(lifecycle IN ('active', 'forgotten')),
				revision INTEGER NOT NULL CHECK(revision > 0),
				source_fingerprint TEXT NOT NULL,
				created_at TEXT NOT NULL,
				updated_at TEXT NOT NULL,
				forgotten_at TEXT
			) STRICT;

			CREATE INDEX oga_memories_partition_active
			ON oga_memories(owner_id, timeline_id, generation, lifecycle, memory_key);

			CREATE TABLE oga_memory_tags (
				memory_key INTEGER NOT NULL REFERENCES oga_memories(memory_key) ON DELETE CASCADE,
				tag TEXT NOT NULL,
				PRIMARY KEY(memory_key, tag)
			) STRICT;

			CREATE INDEX oga_memory_tags_lookup ON oga_memory_tags(tag, memory_key);

			CREATE TABLE oga_memory_recipients (
				memory_key INTEGER NOT NULL REFERENCES oga_memories(memory_key) ON DELETE CASCADE,
				recipient_id TEXT NOT NULL,
				PRIMARY KEY(memory_key, recipient_id)
			) STRICT;

			CREATE INDEX oga_memory_recipients_lookup
			ON oga_memory_recipients(recipient_id, memory_key);

			CREATE TABLE oga_memory_embeddings (
				memory_key INTEGER PRIMARY KEY REFERENCES oga_memories(memory_key) ON DELETE CASCADE,
				memory_revision INTEGER NOT NULL CHECK(memory_revision > 0),
				model_id TEXT NOT NULL,
				dimensions INTEGER NOT NULL CHECK(dimensions > 0),
				vector BLOB NOT NULL,
				source_fingerprint TEXT NOT NULL,
				generated_at TEXT NOT NULL
			) STRICT;

			CREATE TABLE oga_memory_search_documents (
				memory_key INTEGER PRIMARY KEY REFERENCES oga_memories(memory_key) ON DELETE CASCADE,
				search_text TEXT NOT NULL,
				tags TEXT NOT NULL
			) STRICT;

			CREATE VIRTUAL TABLE oga_memory_fts USING fts5(
				search_text,
				tags,
				content='oga_memory_search_documents',
				content_rowid='memory_key',
				tokenize='unicode61 remove_diacritics 2'
			);

			CREATE TRIGGER oga_memory_search_documents_ai AFTER INSERT ON oga_memory_search_documents BEGIN
				INSERT INTO oga_memory_fts(rowid, search_text, tags)
				VALUES (new.memory_key, new.search_text, new.tags);
			END;

			CREATE TRIGGER oga_memory_search_documents_ad AFTER DELETE ON oga_memory_search_documents BEGIN
				INSERT INTO oga_memory_fts(oga_memory_fts, rowid, search_text, tags)
				VALUES ('delete', old.memory_key, old.search_text, old.tags);
			END;

			CREATE TRIGGER oga_memory_search_documents_au AFTER UPDATE ON oga_memory_search_documents BEGIN
				INSERT INTO oga_memory_fts(oga_memory_fts, rowid, search_text, tags)
				VALUES ('delete', old.memory_key, old.search_text, old.tags);
				INSERT INTO oga_memory_fts(rowid, search_text, tags)
				VALUES (new.memory_key, new.search_text, new.tags);
			END;
		`,
	},
	{
		version: 2,
		name: "memory-partition-counts-and-revisions",
		sql: `
			CREATE TABLE oga_memory_partition_heads (
				owner_id TEXT NOT NULL, timeline_id TEXT NOT NULL, generation TEXT NOT NULL,
				revision TEXT NOT NULL,
				PRIMARY KEY(owner_id, timeline_id, generation)
			) STRICT;
			CREATE TABLE oga_memory_lane_counts (
				owner_id TEXT NOT NULL, timeline_id TEXT NOT NULL, generation TEXT NOT NULL,
				actor_id TEXT NOT NULL, session_id TEXT NOT NULL, scope TEXT NOT NULL,
				active_count INTEGER NOT NULL CHECK(active_count >= 0),
				PRIMARY KEY(owner_id, timeline_id, generation, actor_id, session_id, scope)
			) STRICT;
			INSERT INTO oga_memory_partition_heads
			SELECT owner_id, timeline_id, generation, lower(hex(randomblob(16)))
			FROM oga_memories GROUP BY owner_id, timeline_id, generation;
			INSERT INTO oga_memory_lane_counts
			SELECT owner_id, timeline_id, generation, coalesce(actor_id, ''), coalesce(session_id, ''), scope, count(*)
			FROM oga_memories WHERE lifecycle = 'active'
			GROUP BY owner_id, timeline_id, generation, actor_id, session_id, scope;
		`,
	},
];

interface MemoryRow {
	memory_key: bigint;
	memory_id: string;
	owner_id: string;
	actor_id: string | null;
	session_id: string | null;
	scope: string;
	kind: string;
	timeline_id: string;
	generation: string;
	game_tick: string;
	importance: number;
	search_text: string;
	content_json: string;
	audience_kind: Audience["visibility"];
	lifecycle: MemoryRecord["lifecycle"];
	revision: bigint;
	source_fingerprint: string;
	created_at: string;
	updated_at: string;
	forgotten_at: string | null;
}

interface EmbeddingRow {
	memory_key: bigint;
	memory_revision: bigint;
	model_id: string;
	dimensions: bigint;
	vector: Buffer;
	source_fingerprint: string;
	generated_at: string;
}

export class SqliteMemoryAuthority implements MemoryAuthority {
	readonly limits: MemoryLimits;

	constructor(
		private readonly authority: SqliteAuthority,
		limits: MemoryLimits,
	) {
		this.limits = Object.freeze(validateMemoryLimits(limits));
		validateSqliteVariableBudget(authority, this.limits);
		this.authority.install("memory", MEMORY_MIGRATIONS);
	}

	async readPartitionState(selector: MemorySelector): Promise<MemoryPartitionState> {
		const normalized = normalizeMemorySelector(selector, this.limits);
		return this.authority.snapshot((database) => {
			const identity = [normalized.ownerId, normalized.timelineId, normalized.generation];
			const head = database
				.prepare(`SELECT revision FROM oga_memory_partition_heads
				WHERE owner_id = ? AND timeline_id = ? AND generation = ?`)
				.get(...identity) as { revision: string } | undefined;
			const clauses = ["owner_id = ?", "timeline_id = ?", "generation = ?"];
			const parameters = [...identity];
			for (const [column, value] of [
				["actor_id", normalized.actorId],
				["session_id", normalized.sessionId],
				["scope", normalized.scope],
			] as const) {
				if (value === undefined) continue;
				clauses.push(`${column} = ?`);
				parameters.push(value);
			}
			const row = database
				.prepare(`SELECT coalesce(sum(active_count), 0) AS count
				FROM oga_memory_lane_counts WHERE ${clauses.join(" AND ")}`)
				.safeIntegers()
				.get(...parameters) as { count: bigint };
			const activeCount = Number(row.count);
			if (!Number.isSafeInteger(activeCount) || activeCount < 0 || (activeCount > 0 && !head)) {
				throw new Error("Stored memory partition state is corrupt.");
			}
			return { revision: head?.revision ?? "empty", activeCount };
		});
	}

	async remember(input: MemoryWrite): Promise<MemoryRecord> {
		const write = normalizeMemoryWrite(input, this.limits);
		return this.authority.write((database) => {
			const existing = findMemoryRow(database, write.memoryId);
			const fingerprint = memoryFingerprint(write);
			if (existing) {
				if (existing.source_fingerprint !== fingerprint) throw new MemoryConflictError(write.memoryId);
				return hydrateRow(database, existing, this.limits);
			}

			const result = database
				.prepare(
					`INSERT INTO oga_memories(
						memory_id, owner_id, actor_id, session_id, scope, kind,
						timeline_id, generation, game_tick, game_tick_digits,
						importance, search_text, content_json, audience_kind,
						lifecycle, revision, source_fingerprint, created_at, updated_at
					) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'active', 1, ?, ?, ?)`,
				)
				.run(
					write.memoryId,
					write.partition.ownerId,
					write.partition.actorId ?? null,
					write.partition.sessionId ?? null,
					write.partition.scope,
					write.kind,
					write.gameTime.timelineId,
					write.gameTime.generation,
					write.gameTime.tick,
					write.gameTime.tick.length,
					write.importance,
					write.searchText,
					canonicalizeJsonValue(write.content),
					write.audience.visibility,
					fingerprint,
					write.recordedAt,
					write.recordedAt,
				);
			const memoryKey = BigInt(result.lastInsertRowid);
			writeTags(database, memoryKey, write.tags);
			writeRecipients(database, memoryKey, write.audience);
			writeSearchDocument(database, memoryKey, write.searchText, write.tags);
			const row = findMemoryRow(database, write.memoryId);
			if (!row) throw new Error("Memory insert did not produce a readable row.");
			updatePartitionState(database, row, 1);
			return hydrateRow(database, row, this.limits);
		});
	}

	async revise(
		memoryId: string,
		expectedRevision: string,
		input: MemoryRevisionPatch,
		revisedAt: string,
	): Promise<MemoryRecord> {
		validateRevision(expectedRevision);
		validateTimestamp(revisedAt);
		const patch = normalizeRevisionPatch(input, this.limits);
		return this.authority.write((database) => {
			const row = findMemoryRow(database, memoryId);
			if (!row) throw new MemoryNotFoundError(memoryId);
			if (row.revision.toString() !== expectedRevision || row.lifecycle !== "active") {
				throw new MemoryRevisionConflictError(memoryId);
			}
			const current = hydrateRow(database, row, this.limits);
			const write = normalizeMemoryWrite(
				{
					memoryId: current.memoryId,
					partition: current.partition,
					kind: current.kind,
					tags: patch.tags ?? current.tags,
					gameTime: patch.gameTime ?? current.gameTime,
					importance: patch.importance ?? current.importance,
					searchText: patch.searchText ?? current.searchText,
					content: patch.content ?? current.content,
					audience: patch.audience ?? current.audience,
					recordedAt: revisedAt,
				},
				this.limits,
			);
			if (
				write.gameTime.timelineId !== current.gameTime.timelineId ||
				write.gameTime.generation !== current.gameTime.generation
			) {
				throw new MemoryConflictError(memoryId);
			}
			const nextRevision = row.revision + 1n;
			const result = database
				.prepare(
					`UPDATE oga_memories SET
						game_tick = ?, game_tick_digits = ?, importance = ?, search_text = ?,
						content_json = ?, audience_kind = ?, revision = ?, source_fingerprint = ?, updated_at = ?
					WHERE memory_key = ? AND revision = ? AND lifecycle = 'active'`,
				)
				.run(
					write.gameTime.tick,
					write.gameTime.tick.length,
					write.importance,
					write.searchText,
					canonicalizeJsonValue(write.content),
					write.audience.visibility,
					nextRevision,
					memoryFingerprint(write),
					revisedAt,
					row.memory_key,
					row.revision,
				);
			if (result.changes !== 1) throw new MemoryRevisionConflictError(memoryId);
			database.prepare("DELETE FROM oga_memory_tags WHERE memory_key = ?").run(row.memory_key);
			database.prepare("DELETE FROM oga_memory_recipients WHERE memory_key = ?").run(row.memory_key);
			database.prepare("DELETE FROM oga_memory_embeddings WHERE memory_key = ?").run(row.memory_key);
			writeTags(database, row.memory_key, write.tags);
			writeRecipients(database, row.memory_key, write.audience);
			writeSearchDocument(database, row.memory_key, write.searchText, write.tags);
			const revised = findMemoryRow(database, memoryId);
			if (!revised) throw new Error("Revised memory disappeared.");
			updatePartitionState(database, revised, 0);
			return hydrateRow(database, revised, this.limits);
		});
	}

	async forget(memoryId: string, expectedRevision: string, forgottenAt: string): Promise<MemoryRecord> {
		validateRevision(expectedRevision);
		validateTimestamp(forgottenAt);
		return this.authority.write((database) => {
			const row = findMemoryRow(database, memoryId);
			if (!row) throw new MemoryNotFoundError(memoryId);
			if (row.revision.toString() !== expectedRevision || row.lifecycle !== "active") {
				throw new MemoryRevisionConflictError(memoryId);
			}
			const result = database
				.prepare(
					`UPDATE oga_memories
					SET lifecycle = 'forgotten', revision = ?, updated_at = ?, forgotten_at = ?
					WHERE memory_key = ? AND revision = ? AND lifecycle = 'active'`,
				)
				.run(row.revision + 1n, forgottenAt, forgottenAt, row.memory_key, row.revision);
			if (result.changes !== 1) throw new MemoryRevisionConflictError(memoryId);
			database.prepare("DELETE FROM oga_memory_search_documents WHERE memory_key = ?").run(row.memory_key);
			database.prepare("DELETE FROM oga_memory_embeddings WHERE memory_key = ?").run(row.memory_key);
			const forgotten = findMemoryRow(database, memoryId);
			if (!forgotten) throw new Error("Forgotten memory disappeared.");
			updatePartitionState(database, forgotten, -1);
			return hydrateRow(database, forgotten, this.limits);
		});
	}

	async get(memoryId: string, viewer: MemoryViewer): Promise<MemoryRecord | undefined> {
		const normalizedViewer = normalizeMemoryViewer(viewer);
		return this.authority.snapshot((database) => {
			const row = findMemoryRow(database, memoryId);
			if (!row || !canView(database, row, normalizedViewer)) return undefined;
			return hydrateRow(database, row, this.limits);
		});
	}

	async searchLexical(
		selector: MemorySelector,
		viewer: MemoryViewer,
		query: string,
		limit: number,
	): Promise<readonly RankedMemoryCandidate[]> {
		const normalizedSelector = normalizeMemorySelector(selector, this.limits);
		const normalizedViewer = normalizeMemoryViewer(viewer);
		validateQuery(query, this.limits);
		validateResultLimit(limit, this.limits.maxLexicalCandidates, "invalid_lexical_limit");
		return this.authority.read((database) => {
			const built = buildMemoryFilter(normalizedSelector, normalizedViewer, "m");
			const statement = database.prepare(
				`SELECT m.memory_key, bm25(oga_memory_fts) AS rank
				 FROM oga_memory_fts
				 JOIN oga_memories m ON m.memory_key = oga_memory_fts.rowid
				 WHERE oga_memory_fts MATCH ? AND ${built.sql}
				 ORDER BY rank, m.memory_key
				 LIMIT ?`,
			);
			const rows = statement.safeIntegers().all(quoteFtsPhrase(query), ...built.parameters, limit) as Array<{
				memory_key: bigint;
				rank: number;
			}>;
			return rows.map((row) => ({ memoryKey: row.memory_key, score: -Number(row.rank) }));
		});
	}

	async hydrate(
		memoryKeys: readonly bigint[],
		selector: MemorySelector,
		viewer: MemoryViewer,
	): Promise<readonly MemoryRecord[]> {
		const normalizedSelector = normalizeMemorySelector(selector, this.limits);
		const normalizedViewer = normalizeMemoryViewer(viewer);
		const keys = normalizeMemoryKeys(memoryKeys, this.limits.maxHydrateCandidates);
		if (keys.length === 0) return [];
		return this.authority.snapshot((database) => {
			const built = buildMemoryFilter(normalizedSelector, normalizedViewer, "m");
			const placeholders = keys.map(() => "?").join(", ");
			const rows = database
				.prepare(`SELECT m.* FROM oga_memories m WHERE m.memory_key IN (${placeholders}) AND ${built.sql}`)
				.safeIntegers()
				.all(...keys, ...built.parameters) as MemoryRow[];
			return rows.map((row) => hydrateRow(database, row, this.limits));
		});
	}

	async scanActive(
		partition: MemoryIndexPartition,
		afterMemoryKey: bigint | undefined,
		limit: number,
	): Promise<readonly MemoryRecord[]> {
		const normalizedPartition = normalizeIndexPartition(partition);
		validateResultLimit(limit, this.limits.maxIndexBatchSize, "invalid_index_batch_limit");
		return this.authority.snapshot((database) => {
			const rows = database
				.prepare(
					`SELECT * FROM oga_memories
					 WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND lifecycle = 'active' AND memory_key > ?
					 ORDER BY memory_key LIMIT ?`,
				)
				.safeIntegers()
				.all(
					normalizedPartition.ownerId,
					normalizedPartition.timelineId,
					normalizedPartition.generation,
					afterMemoryKey ?? 0n,
					limit,
				) as MemoryRow[];
			return rows.map((row) => hydrateRow(database, row, this.limits));
		});
	}

	async readEmbedding(
		memoryKey: bigint,
		memoryRevision: string,
		modelId: string,
	): Promise<MemoryEmbedding | undefined> {
		validateRevision(memoryRevision);
		return this.authority.read((database) => {
			const row = database
				.prepare(
					`SELECT * FROM oga_memory_embeddings
					 WHERE memory_key = ? AND memory_revision = ? AND model_id = ?`,
				)
				.safeIntegers()
				.get(memoryKey, BigInt(memoryRevision), modelId) as EmbeddingRow | undefined;
			return row ? embeddingFromRow(row) : undefined;
		});
	}

	async writeEmbedding(embedding: MemoryEmbedding): Promise<boolean> {
		const normalizedEmbedding = normalizeEmbedding(embedding);
		return this.authority.write((database) => {
			const current = database
				.prepare("SELECT revision, lifecycle FROM oga_memories WHERE memory_key = ?")
				.safeIntegers()
				.get(normalizedEmbedding.memoryKey) as { revision: bigint; lifecycle: string } | undefined;
			if (current?.lifecycle !== "active" || current.revision.toString() !== normalizedEmbedding.memoryRevision) {
				return false;
			}
			database
				.prepare(
					`INSERT INTO oga_memory_embeddings(
						memory_key, memory_revision, model_id, dimensions, vector, source_fingerprint, generated_at
					) VALUES (?, ?, ?, ?, ?, ?, ?)
					ON CONFLICT(memory_key) DO UPDATE SET
						memory_revision = excluded.memory_revision,
						model_id = excluded.model_id,
						dimensions = excluded.dimensions,
						vector = excluded.vector,
						source_fingerprint = excluded.source_fingerprint,
						generated_at = excluded.generated_at`,
				)
				.run(
					normalizedEmbedding.memoryKey,
					BigInt(normalizedEmbedding.memoryRevision),
					normalizedEmbedding.modelId,
					normalizedEmbedding.dimensions,
					encodeVector(normalizedEmbedding.vector),
					normalizedEmbedding.sourceFingerprint,
					normalizedEmbedding.generatedAt,
				);
			return true;
		});
	}

	async deleteEmbedding(memoryKey: bigint): Promise<void> {
		this.authority.write((database) => {
			database.prepare("DELETE FROM oga_memory_embeddings WHERE memory_key = ?").run(memoryKey);
		});
	}

	async exportPage(afterMemoryKey: bigint | undefined, limit: number): Promise<MemoryExportPage> {
		validateResultLimit(limit, this.limits.maxExportPageSize, "invalid_export_limit");
		return this.authority.snapshot((database) => {
			const rows = database
				.prepare("SELECT * FROM oga_memories WHERE memory_key > ? ORDER BY memory_key LIMIT ?")
				.safeIntegers()
				.all(afterMemoryKey ?? 0n, limit) as MemoryRow[];
			const records = rows.map((row) => hydrateRow(database, row, this.limits));
			const last = records.at(-1);
			return records.length === limit && last ? { records, nextAfterMemoryKey: last.memoryKey } : { records };
		});
	}

	async purgeForgotten(before: string, limit: number): Promise<readonly bigint[]> {
		validateTimestamp(before);
		validateResultLimit(limit, this.limits.maxPurgeBatchSize, "invalid_purge_limit");
		return this.authority.write((database) => {
			const rows = database
				.prepare(
					`SELECT memory_key FROM oga_memories
					 WHERE lifecycle = 'forgotten' AND forgotten_at < ?
					 ORDER BY memory_key LIMIT ?`,
				)
				.safeIntegers()
				.all(before, limit) as Array<{ memory_key: bigint }>;
			const keys = rows.map((row) => row.memory_key);
			if (keys.length > 0) {
				const placeholders = keys.map(() => "?").join(", ");
				database.prepare(`DELETE FROM oga_memories WHERE memory_key IN (${placeholders})`).run(...keys);
			}
			return keys;
		});
	}

	async rebuildLexicalIndex(): Promise<void> {
		this.authority.write((database) => {
			database.prepare("INSERT INTO oga_memory_fts(oga_memory_fts) VALUES('rebuild')").run();
			database.prepare("DELETE FROM oga_memory_search_documents").run();
			database.exec(`
				INSERT INTO oga_memory_search_documents(memory_key, search_text, tags)
				SELECT
					m.memory_key,
					m.search_text,
					COALESCE((
						SELECT group_concat(ordered.tag, char(10))
						FROM (
							SELECT mt.tag
							FROM oga_memory_tags mt
							WHERE mt.memory_key = m.memory_key
							ORDER BY mt.tag
						) ordered
					), '')
				FROM oga_memories m
				WHERE m.lifecycle = 'active';
				INSERT INTO oga_memory_fts(oga_memory_fts, rank) VALUES('integrity-check', 1);
			`);
		});
	}
}

function updatePartitionState(database: Database.Database, row: MemoryRow, countDelta: number): void {
	// A fresh 128-bit revision also separates independently created/restored authority databases.
	database
		.prepare(`INSERT INTO oga_memory_partition_heads VALUES (?, ?, ?, lower(hex(randomblob(16))))
		ON CONFLICT(owner_id, timeline_id, generation) DO UPDATE SET revision = excluded.revision`)
		.run(row.owner_id, row.timeline_id, row.generation);
	if (countDelta === 0) return;
	if (countDelta > 0) {
		database
			.prepare(`INSERT INTO oga_memory_lane_counts VALUES (?, ?, ?, ?, ?, ?, 1)
			ON CONFLICT(owner_id, timeline_id, generation, actor_id, session_id, scope)
			DO UPDATE SET active_count = active_count + 1`)
			.run(row.owner_id, row.timeline_id, row.generation, row.actor_id ?? "", row.session_id ?? "", row.scope);
	} else {
		const result = database
			.prepare(`UPDATE oga_memory_lane_counts SET active_count = active_count - 1
			WHERE owner_id = ? AND timeline_id = ? AND generation = ? AND actor_id = ? AND session_id = ? AND scope = ?`)
			.run(row.owner_id, row.timeline_id, row.generation, row.actor_id ?? "", row.session_id ?? "", row.scope);
		if (result.changes !== 1) throw new Error("Stored memory partition state is corrupt.");
		database
			.prepare(`DELETE FROM oga_memory_lane_counts WHERE owner_id = ? AND timeline_id = ? AND generation = ?
			AND actor_id = ? AND session_id = ? AND scope = ? AND active_count = 0`)
			.run(row.owner_id, row.timeline_id, row.generation, row.actor_id ?? "", row.session_id ?? "", row.scope);
	}
}

function findMemoryRow(database: Database.Database, memoryId: string): MemoryRow | undefined {
	return database.prepare("SELECT * FROM oga_memories WHERE memory_id = ?").safeIntegers().get(memoryId) as
		| MemoryRow
		| undefined;
}

function hydrateRow(database: Database.Database, row: MemoryRow, limits: MemoryLimits): MemoryRecord {
	validateTimestamp(row.created_at);
	validateTimestamp(row.updated_at);
	if (row.forgotten_at !== null) validateTimestamp(row.forgotten_at);
	if (
		row.updated_at < row.created_at ||
		(row.lifecycle === "forgotten") !== (row.forgotten_at !== null) ||
		(row.forgotten_at !== null && row.forgotten_at !== row.updated_at)
	) {
		throw new Error("Stored memory chronology is corrupt.");
	}
	const tags = database
		.prepare("SELECT tag FROM oga_memory_tags WHERE memory_key = ? ORDER BY tag")
		.all(row.memory_key) as Array<{ tag: string }>;
	const audience = readAudience(database, row);
	const content = normalizeMemoryContent(JSON.parse(row.content_json), limits.maxContentBytes);
	const record: MemoryRecord = {
		memoryKey: row.memory_key,
		memoryId: row.memory_id,
		partition: {
			ownerId: row.owner_id,
			...(row.actor_id === null ? {} : { actorId: row.actor_id }),
			...(row.session_id === null ? {} : { sessionId: row.session_id }),
			scope: row.scope,
		},
		kind: row.kind,
		tags: tags.map((entry) => entry.tag),
		gameTime: { timelineId: row.timeline_id, generation: row.generation, tick: row.game_tick },
		importance: row.importance,
		searchText: row.search_text,
		content,
		audience,
		revision: row.revision.toString(),
		lifecycle: row.lifecycle,
		createdAt: row.created_at,
		updatedAt: row.updated_at,
		...(row.forgotten_at === null ? {} : { forgottenAt: row.forgotten_at }),
	};
	if (memoryFingerprint(record) !== row.source_fingerprint) {
		throw new Error("Stored memory fingerprint is corrupt.");
	}
	return record;
}

function readAudience(database: Database.Database, row: MemoryRow): Audience {
	if (row.audience_kind !== "recipient") return { visibility: row.audience_kind };
	const recipients = database
		.prepare("SELECT recipient_id FROM oga_memory_recipients WHERE memory_key = ? ORDER BY recipient_id")
		.all(row.memory_key) as Array<{ recipient_id: string }>;
	if (recipients.length === 0) throw new Error("Recipient memory has no authorized recipients.");
	return { visibility: "recipient", recipientIds: recipients.map((entry) => entry.recipient_id) };
}

function writeTags(database: Database.Database, memoryKey: bigint, tags: readonly string[]): void {
	const insert = database.prepare("INSERT INTO oga_memory_tags(memory_key, tag) VALUES (?, ?)");
	for (const tag of tags) insert.run(memoryKey, tag);
}

function writeRecipients(database: Database.Database, memoryKey: bigint, audience: Audience): void {
	if (audience.visibility !== "recipient") return;
	const insert = database.prepare("INSERT INTO oga_memory_recipients(memory_key, recipient_id) VALUES (?, ?)");
	for (const recipientId of audience.recipientIds) insert.run(memoryKey, recipientId);
}

function writeSearchDocument(
	database: Database.Database,
	memoryKey: bigint,
	searchText: string,
	tags: readonly string[],
): void {
	database
		.prepare(
			`INSERT INTO oga_memory_search_documents(memory_key, search_text, tags) VALUES (?, ?, ?)
			 ON CONFLICT(memory_key) DO UPDATE SET
			 search_text = excluded.search_text,
			 tags = excluded.tags`,
		)
		.run(memoryKey, searchText, tags.join("\n"));
}

function canView(database: Database.Database, row: MemoryRow, viewer: MemoryViewer): boolean {
	if (viewer.kind === "internal") return true;
	if (row.audience_kind === "public") return true;
	if (viewer.kind === "owner" && row.audience_kind === "owner" && row.owner_id === viewer.ownerId) return true;
	const recipientId =
		viewer.kind === "owner" ? viewer.ownerId : viewer.kind === "recipient" ? viewer.recipientId : undefined;
	if (row.audience_kind !== "recipient" || recipientId === undefined) return false;
	return Boolean(
		database
			.prepare("SELECT 1 FROM oga_memory_recipients WHERE memory_key = ? AND recipient_id = ?")
			.get(row.memory_key, recipientId),
	);
}

function buildMemoryFilter(
	selector: MemorySelector,
	viewer: MemoryViewer,
	alias: string,
): { sql: string; parameters: unknown[] } {
	const clauses = [
		`${alias}.owner_id = ?`,
		`${alias}.timeline_id = ?`,
		`${alias}.generation = ?`,
		`${alias}.lifecycle = 'active'`,
	];
	const parameters: unknown[] = [selector.ownerId, selector.timelineId, selector.generation];
	if (selector.actorId !== undefined) {
		clauses.push(`${alias}.actor_id = ?`);
		parameters.push(selector.actorId);
	}
	if (selector.sessionId !== undefined) {
		clauses.push(`${alias}.session_id = ?`);
		parameters.push(selector.sessionId);
	}
	if (selector.scope !== undefined) {
		clauses.push(`${alias}.scope = ?`);
		parameters.push(selector.scope);
	}
	if (selector.kinds && selector.kinds.length > 0) {
		clauses.push(`${alias}.kind IN (${selector.kinds.map(() => "?").join(", ")})`);
		parameters.push(...selector.kinds);
	}
	const tags = selector.tags;
	if (tags && tags.length > 0) {
		clauses.push(
			`(SELECT COUNT(*) FROM oga_memory_tags mt
			  WHERE mt.memory_key = ${alias}.memory_key
			    AND mt.tag IN (${tags.map(() => "?").join(", ")})) = ?`,
		);
		parameters.push(...tags, tags.length);
	}
	if (selector.atOrBeforeTick !== undefined) {
		clauses.push(`(${alias}.game_tick_digits < ? OR (${alias}.game_tick_digits = ? AND ${alias}.game_tick <= ?))`);
		parameters.push(selector.atOrBeforeTick.length, selector.atOrBeforeTick.length, selector.atOrBeforeTick);
	}
	if (viewer.kind === "public") {
		clauses.push(`${alias}.audience_kind = 'public'`);
	} else if (viewer.kind === "recipient") {
		clauses.push(
			`(${alias}.audience_kind = 'public' OR (${alias}.audience_kind = 'recipient' AND EXISTS (
				SELECT 1 FROM oga_memory_recipients mr
				WHERE mr.memory_key = ${alias}.memory_key AND mr.recipient_id = ?
			)))`,
		);
		parameters.push(viewer.recipientId);
	} else if (viewer.kind === "owner") {
		clauses.push(
			`(${alias}.audience_kind = 'public'
			 OR (${alias}.audience_kind = 'owner' AND ${alias}.owner_id = ?)
			 OR (${alias}.audience_kind = 'recipient' AND EXISTS (
				SELECT 1 FROM oga_memory_recipients mr
				WHERE mr.memory_key = ${alias}.memory_key AND mr.recipient_id = ?
			 )))`,
		);
		parameters.push(viewer.ownerId, viewer.ownerId);
	}
	return { sql: clauses.join(" AND "), parameters };
}

function quoteFtsPhrase(query: string): string {
	return `"${query.replaceAll('"', '""')}"`;
}

function encodeVector(vector: Float32Array): Buffer {
	const bytes = Buffer.allocUnsafe(vector.length * Float32Array.BYTES_PER_ELEMENT);
	for (let index = 0; index < vector.length; index += 1) bytes.writeFloatLE(vector[index] as number, index * 4);
	return bytes;
}

function normalizeMemoryKeys(memoryKeys: readonly bigint[], maximum: number): readonly bigint[] {
	if (!Array.isArray(memoryKeys)) throw new TypeError("Memory keys must be an array.");
	const length = memoryKeys.length;
	if (length === 0) return [];
	validateResultLimit(length, maximum, "invalid_hydrate_limit");
	const normalized: bigint[] = [];
	for (let index = 0; index < length; index += 1) {
		const memoryKey = memoryKeys[index];
		if (typeof memoryKey !== "bigint") throw new TypeError("Memory keys must be bigint values.");
		normalized.push(memoryKey);
	}
	return normalized;
}

function normalizeEmbedding(embedding: MemoryEmbedding): MemoryEmbedding {
	const memoryKey = embedding.memoryKey;
	const memoryRevision = embedding.memoryRevision;
	const modelId = embedding.modelId;
	const dimensions = embedding.dimensions;
	const sourceFingerprint = embedding.sourceFingerprint;
	const generatedAt = embedding.generatedAt;
	const sourceVector = embedding.vector;
	validateRevision(memoryRevision);
	validateTimestamp(generatedAt);
	const length = sourceVector.length;
	const vector = new Float32Array(length);
	for (let index = 0; index < length; index += 1) vector[index] = sourceVector[index] as number;
	validateVector(vector, dimensions);
	return { memoryKey, memoryRevision, modelId, dimensions, vector, sourceFingerprint, generatedAt };
}

function validateSqliteVariableBudget(authority: SqliteAuthority, limits: MemoryLimits): void {
	const maximum = authority.read((database) => {
		const rows = database.pragma("compile_options") as Array<Record<string, unknown>>;
		for (const row of rows) {
			const option = Object.values(row)[0];
			if (typeof option !== "string") continue;
			const match = /^MAX_VARIABLE_NUMBER=([1-9][0-9]*)$/u.exec(option);
			if (match?.[1] !== undefined) return BigInt(match[1]);
		}
		throw new TypeError("SQLite does not report its variable limit.");
	});
	const maximumFilterVariables = BigInt(limits.maxSelectorKinds) + BigInt(limits.maxSelectorTags) + 12n;
	const lexicalVariables = maximumFilterVariables + 2n;
	const hydrateVariables = maximumFilterVariables + BigInt(limits.maxHydrateCandidates);
	if (lexicalVariables > maximum || hydrateVariables > maximum || BigInt(limits.maxPurgeBatchSize) > maximum) {
		throw new TypeError("Memory limits exceed the connected SQLite variable budget.");
	}
}

function embeddingFromRow(row: EmbeddingRow): MemoryEmbedding {
	validateTimestamp(row.generated_at);
	const dimensions = Number(row.dimensions);
	if (!Number.isSafeInteger(dimensions) || dimensions < 1 || row.vector.byteLength !== dimensions * 4) {
		throw new Error("Stored memory embedding is corrupt.");
	}
	const vector = new Float32Array(dimensions);
	for (let index = 0; index < dimensions; index += 1) vector[index] = row.vector.readFloatLE(index * 4);
	validateVector(vector, dimensions);
	return {
		memoryKey: row.memory_key,
		memoryRevision: row.memory_revision.toString(),
		modelId: row.model_id,
		dimensions,
		vector,
		sourceFingerprint: row.source_fingerprint,
		generatedAt: row.generated_at,
	};
}

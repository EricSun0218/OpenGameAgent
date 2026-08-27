import { DatabaseSync } from "node:sqlite";
import type { GameMoment, GameSessionKey, JsonValue } from "@opengameagent/protocol";

export type GameMemoryScope = "actor" | "owner" | "world";

export interface GameMemory {
	id: string;
	session: GameSessionKey;
	scope: GameMemoryScope;
	kind: string;
	content: JsonValue;
	searchText?: string;
	tags?: readonly string[];
	importance: number;
	moment: GameMoment;
	createdAt: number;
}

export interface GameMemoryQuery {
	session: GameSessionKey;
	text?: string;
	scopes?: readonly GameMemoryScope[];
	kinds?: readonly string[];
	tags?: readonly string[];
	atOrBeforeTick?: number;
	minimumImportance?: number;
	limit: number;
}

export interface GameMemoryMatch {
	memory: GameMemory;
	score: number;
	lexicalScore?: number;
	vectorScore?: number;
}

export interface GameMemorySearchDiagnostics {
	embeddingIdentity?: string;
	embeddingMilliseconds: number;
	lexicalMilliseconds: number;
	vectorCandidateMilliseconds: number;
	authoritativeMilliseconds: number;
	rerankMilliseconds: number;
	lexicalCandidates: number;
	vectorCandidates: number;
	authoritativeCandidates: number;
	returned: number;
}

export interface GameMemorySearchResult {
	matches: readonly GameMemoryMatch[];
	diagnostics: GameMemorySearchDiagnostics;
}

export interface GameMemoryEmbeddingIdentity {
	model: string;
	version: string;
	dimensions: number;
	preprocessing: string;
}

export interface GameMemoryEmbeddingProvider {
	readonly identity: GameMemoryEmbeddingIdentity;
	embedQuery(text: string, signal?: AbortSignal): Promise<Float32Array>;
	embedDocuments(texts: readonly string[], signal?: AbortSignal): Promise<readonly Float32Array[]>;
}

export interface SqliteGameMemoryStoreOptions {
	embedding?: GameMemoryEmbeddingProvider;
	maximumMemoryBytes?: number;
	maximumSearchCharacters?: number;
	maximumCandidates?: number;
	requireEmbeddingOnWrite?: boolean;
}

interface CandidateRow {
	row_id: number;
	rank?: number;
	importance?: number;
	vector?: Uint8Array;
}

const validMemoryScopes = new Set(["actor", "owner", "world"] as const);

interface MemoryRow {
	row_id: number;
	memory_json: string;
}

function now(): number {
	return performance.now();
}

function embeddingIdentity(identity: GameMemoryEmbeddingIdentity): string {
	if (!identity.model || !identity.version || !identity.preprocessing)
		throw new TypeError("Embedding identity is incomplete.");
	if (!Number.isInteger(identity.dimensions) || identity.dimensions < 1 || identity.dimensions > 65_536) {
		throw new RangeError("Embedding dimensions are invalid.");
	}
	return JSON.stringify([identity.model, identity.version, identity.dimensions, identity.preprocessing]);
}

function normalize(vector: Float32Array, dimensions: number): Float32Array {
	if (vector.length !== dimensions) throw new Error("Embedding dimensions do not match the provider identity.");
	let squared = 0;
	for (const value of vector) {
		if (!Number.isFinite(value)) throw new Error("Embedding contains a non-finite value.");
		squared += value * value;
	}
	if (squared <= 0) throw new Error("Embedding cannot be a zero vector.");
	const scale = 1 / Math.sqrt(squared);
	return Float32Array.from(vector, (value) => value * scale);
}

function encodeVector(vector: Float32Array): Uint8Array {
	const bytes = new Uint8Array(vector.length * 4);
	const view = new DataView(bytes.buffer);
	for (let index = 0; index < vector.length; index += 1) view.setFloat32(index * 4, vector[index] ?? 0, true);
	return bytes;
}

function decodeVector(bytes: Uint8Array, dimensions: number): Float32Array {
	if (bytes.byteLength !== dimensions * 4) throw new Error("Stored memory vector is corrupt.");
	const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	return Float32Array.from({ length: dimensions }, (_, index) => view.getFloat32(index * 4, true));
}

function similarity(left: Float32Array, right: Float32Array): number {
	if (left.length !== right.length) throw new Error("Embedding dimensions differ.");
	let result = 0;
	for (let index = 0; index < left.length; index += 1) result += (left[index] ?? 0) * (right[index] ?? 0);
	return Math.max(-1, Math.min(1, result));
}

function vectorBuckets(vector: Float32Array): string[] {
	const bands = Math.min(8, Math.max(1, Math.floor(vector.length / 8)));
	const bits = Math.min(12, vector.length);
	const buckets: string[] = [];
	for (let band = 0; band < bands; band += 1) {
		let value = 0;
		for (let bit = 0; bit < bits; bit += 1) {
			const index = (band * 131 + bit * 31 + band * bit * 7) % vector.length;
			if ((vector[index] ?? 0) >= 0) value |= 1 << bit;
		}
		buckets.push(`${band}:${value.toString(16)}`);
	}
	return buckets;
}

function ftsQuery(text: string): string | undefined {
	const normalized = Array.from(text.normalize("NFKC").toLocaleLowerCase().trim());
	if (normalized.length === 0) return undefined;
	const grams = new Set<string>();
	if (normalized.length <= 3) grams.add(normalized.join(""));
	else {
		for (let index = 0; index <= normalized.length - 3 && grams.size < 32; index += 1) {
			const gram = normalized
				.slice(index, index + 3)
				.join("")
				.trim();
			if (gram) grams.add(gram);
		}
	}
	return [...grams].map((gram) => `"${gram.replaceAll('"', '""')}"`).join(" OR ");
}

function bm25RankToScore(rank: number): number {
	if (!Number.isFinite(rank)) return 1 / 1_000;
	if (rank < 0) {
		const relevance = -rank;
		return relevance / (1 + relevance);
	}
	return 1 / (1 + rank);
}

function validatePortableId(value: string, name: string): void {
	if (!/^[a-z0-9][a-z0-9._:-]{0,191}$/i.test(value))
		throw new TypeError(`${name} is not a portable bounded identifier.`);
}

export class SqliteGameMemoryStore implements Disposable {
	private readonly database: DatabaseSync;
	private readonly embedding: GameMemoryEmbeddingProvider | undefined;
	private readonly identity: string | undefined;
	private readonly maximumMemoryBytes: number;
	private readonly maximumSearchCharacters: number;
	private readonly maximumCandidates: number;
	private readonly requireEmbeddingOnWrite: boolean;
	private closed = false;

	constructor(path: string, options: SqliteGameMemoryStoreOptions = {}) {
		if (!path) throw new TypeError("A SQLite database path is required.");
		this.embedding = options.embedding;
		this.identity = options.embedding ? embeddingIdentity(options.embedding.identity) : undefined;
		this.maximumMemoryBytes = options.maximumMemoryBytes ?? 256 * 1024;
		this.maximumSearchCharacters = options.maximumSearchCharacters ?? 16_384;
		this.maximumCandidates = options.maximumCandidates ?? 512;
		this.requireEmbeddingOnWrite = options.requireEmbeddingOnWrite ?? false;
		if (!Number.isInteger(this.maximumMemoryBytes) || this.maximumMemoryBytes < 1024)
			throw new RangeError("maximumMemoryBytes is invalid.");
		if (!Number.isInteger(this.maximumSearchCharacters) || this.maximumSearchCharacters < 128)
			throw new RangeError("maximumSearchCharacters is invalid.");
		if (!Number.isInteger(this.maximumCandidates) || this.maximumCandidates < 8 || this.maximumCandidates > 4096)
			throw new RangeError("maximumCandidates is invalid.");
		this.database = new DatabaseSync(path);
		this.database.exec(
			"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
		);
		this.database.exec(`
			CREATE TABLE IF NOT EXISTS game_memories (
				row_id INTEGER PRIMARY KEY,
				world_id TEXT NOT NULL, save_id TEXT NOT NULL, timeline_id TEXT NOT NULL, generation INTEGER NOT NULL,
				owner_id TEXT NOT NULL, session_id TEXT NOT NULL, actor_id TEXT NOT NULL,
				memory_id TEXT NOT NULL, scope TEXT NOT NULL CHECK(scope IN ('actor','owner','world')),
				kind TEXT NOT NULL, tags_json TEXT NOT NULL, importance REAL NOT NULL,
				tick REAL NOT NULL, created_at INTEGER NOT NULL, memory_json TEXT NOT NULL,
				UNIQUE(world_id,save_id,timeline_id,generation,memory_id)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_memories_actor_query
				ON game_memories(world_id,save_id,timeline_id,generation,actor_id,tick DESC,importance DESC);
			CREATE INDEX IF NOT EXISTS game_memories_owner_query
				ON game_memories(world_id,save_id,timeline_id,generation,owner_id,tick DESC,importance DESC);
			CREATE INDEX IF NOT EXISTS game_memories_world_query
				ON game_memories(world_id,save_id,timeline_id,generation,tick DESC,importance DESC);
			CREATE VIRTUAL TABLE IF NOT EXISTS game_memory_fts USING fts5(search_text, tags, tokenize='trigram');
			CREATE TABLE IF NOT EXISTS game_memory_vectors (
				row_id INTEGER PRIMARY KEY REFERENCES game_memories(row_id) ON DELETE CASCADE,
				embedding_identity TEXT NOT NULL, dimensions INTEGER NOT NULL, vector BLOB NOT NULL
			) STRICT;
			CREATE TABLE IF NOT EXISTS game_memory_vector_buckets (
				embedding_identity TEXT NOT NULL, bucket TEXT NOT NULL,
				row_id INTEGER NOT NULL REFERENCES game_memories(row_id) ON DELETE CASCADE,
				PRIMARY KEY(embedding_identity,bucket,row_id)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_memory_vector_bucket_lookup
				ON game_memory_vector_buckets(embedding_identity,bucket,row_id DESC);
			CREATE TABLE IF NOT EXISTS game_memory_tags (
				tag TEXT NOT NULL,
				row_id INTEGER NOT NULL REFERENCES game_memories(row_id) ON DELETE CASCADE,
				PRIMARY KEY(tag,row_id)
			) STRICT;
			CREATE INDEX IF NOT EXISTS game_memories_actor_filtered
				ON game_memories(world_id,save_id,timeline_id,generation,actor_id,scope,kind,tick DESC,importance DESC,row_id DESC);
			CREATE INDEX IF NOT EXISTS game_memories_owner_filtered
				ON game_memories(world_id,save_id,timeline_id,generation,owner_id,scope,kind,tick DESC,importance DESC,row_id DESC);
			CREATE INDEX IF NOT EXISTS game_memories_world_filtered
				ON game_memories(world_id,save_id,timeline_id,generation,scope,kind,tick DESC,importance DESC,row_id DESC);
		`);
		this.database.exec("PRAGMA foreign_keys=ON;");
	}

	async put(memory: GameMemory, signal?: AbortSignal): Promise<void> {
		await this.putMany([memory], signal);
	}

	async putMany(memories: readonly GameMemory[], signal?: AbortSignal): Promise<void> {
		this.ensureOpen();
		signal?.throwIfAborted();
		if (memories.length < 1 || memories.length > 10_000) {
			throw new RangeError("Memory batches must contain between 1 and 10000 records.");
		}
		const values = memories.map((memory) => {
			this.validateMemory(memory);
			const json = JSON.stringify(memory);
			if (Buffer.byteLength(json) > this.maximumMemoryBytes) {
				throw new RangeError("Memory exceeds the configured size limit.");
			}
			return { memory, json };
		});
		let vectors: readonly Float32Array[] | undefined;
		if (this.embedding && memories.some((memory) => memory.searchText)) {
			try {
				const embedded = await this.embedding.embedDocuments(
					memories.map((memory) => memory.searchText ?? ""),
					signal,
				);
				if (embedded.length !== memories.length) throw new Error("Embedding provider returned the wrong batch size.");
				vectors = embedded.map((vector) => normalize(vector, this.embedding?.identity.dimensions ?? 0));
			} catch (error) {
				if (this.requireEmbeddingOnWrite) throw error;
			}
		}
		signal?.throwIfAborted();
		this.database.exec("BEGIN IMMEDIATE");
		try {
			const readExisting = this.database.prepare(
				"SELECT row_id,memory_json FROM game_memories WHERE world_id=? AND save_id=? AND timeline_id=? AND generation=? AND memory_id=?",
			);
			const insertMemory = this.database.prepare(`INSERT INTO game_memories (
				world_id,save_id,timeline_id,generation,owner_id,session_id,actor_id,memory_id,scope,kind,tags_json,
				importance,tick,created_at,memory_json) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`);
			const insertFts = this.database.prepare("INSERT INTO game_memory_fts(rowid,search_text,tags) VALUES (?,?,?)");
			const insertTag = this.database.prepare("INSERT INTO game_memory_tags(tag,row_id) VALUES (?,?)");
			for (let index = 0; index < values.length; index += 1) {
				const { memory, json } = values[index] as { memory: GameMemory; json: string };
				const existing = readExisting.get(
					memory.session.worldId,
					memory.session.saveId,
					memory.session.timelineId,
					memory.session.generation,
					memory.id,
				) as { row_id: number; memory_json: string } | undefined;
				if (existing) {
					if (existing.memory_json !== json) throw new Error("Memory id identifies different content.");
					continue;
				}
				const inserted = insertMemory.run(
					memory.session.worldId,
					memory.session.saveId,
					memory.session.timelineId,
					memory.session.generation,
					memory.session.ownerId,
					memory.session.sessionId,
					memory.session.actorId,
					memory.id,
					memory.scope,
					memory.kind,
					JSON.stringify(memory.tags ?? []),
					memory.importance,
					memory.moment.tick,
					memory.createdAt,
					json,
				);
				const rowId = Number(inserted.lastInsertRowid);
				insertFts.run(rowId, memory.searchText ?? "", (memory.tags ?? []).join(" "));
				for (const tag of new Set(memory.tags ?? [])) insertTag.run(tag, rowId);
				const vector = vectors?.[index];
				if (vector && this.identity) this.writeVector(rowId, this.identity, vector);
			}
			this.database.exec("COMMIT");
		} catch (error) {
			this.database.exec("ROLLBACK");
			throw error;
		}
	}

	async search(query: GameMemoryQuery, signal?: AbortSignal): Promise<GameMemorySearchResult> {
		this.ensureOpen();
		signal?.throwIfAborted();
		this.validateQuery(query);
		const diagnostics: GameMemorySearchDiagnostics = {
			...(this.identity === undefined ? {} : { embeddingIdentity: this.identity }),
			embeddingMilliseconds: 0,
			lexicalMilliseconds: 0,
			vectorCandidateMilliseconds: 0,
			authoritativeMilliseconds: 0,
			rerankMilliseconds: 0,
			lexicalCandidates: 0,
			vectorCandidates: 0,
			authoritativeCandidates: 0,
			returned: 0,
		};
		const lexical = new Map<number, number>();
		const importance = new Map<number, number>();
		const vectorScores = new Map<number, number>();
		const candidateLimit = Math.min(this.maximumCandidates, Math.max(query.limit * 16, 32));
		const filter = this.queryPredicate(query);
		const expression = query.text ? ftsQuery(query.text) : undefined;
		const lexicalStart = now();
		if (expression) {
			const rows = this.database
				.prepare(`SELECT m.row_id,m.importance,bm25(game_memory_fts) AS rank
				FROM game_memory_fts JOIN game_memories m ON m.row_id=game_memory_fts.rowid
				WHERE game_memory_fts MATCH ? AND ${filter.sql}
				ORDER BY rank LIMIT ?`)
				.all(expression, ...filter.values, candidateLimit) as unknown as CandidateRow[];
			for (const row of rows) {
				lexical.set(row.row_id, bm25RankToScore(Number(row.rank)));
				importance.set(row.row_id, Number(row.importance ?? 0));
			}
		}
		diagnostics.lexicalMilliseconds = now() - lexicalStart;
		diagnostics.lexicalCandidates = lexical.size;

		if (this.embedding && this.identity && query.text) {
			const embeddingStart = now();
			try {
				const queryVector = normalize(
					await this.embedding.embedQuery(query.text, signal),
					this.embedding.identity.dimensions,
				);
				diagnostics.embeddingMilliseconds = now() - embeddingStart;
				const vectorStart = now();
				const buckets = vectorBuckets(queryVector);
				const placeholders = buckets.map(() => "?").join(",");
				const rows = this.database
					.prepare(`SELECT b.row_id,v.vector,m.importance,COUNT(*) AS bucket_matches
					FROM game_memory_vector_buckets b JOIN game_memory_vectors v ON v.row_id=b.row_id
					JOIN game_memories m ON m.row_id=b.row_id
					WHERE b.embedding_identity=? AND b.bucket IN (${placeholders}) AND ${filter.sql}
					GROUP BY b.row_id
					ORDER BY bucket_matches DESC,b.row_id DESC LIMIT ?`)
					.all(this.identity, ...buckets, ...filter.values, candidateLimit) as unknown as CandidateRow[];
				for (const row of rows) {
					if (!row.vector) throw new Error("Stored memory vector is missing.");
					vectorScores.set(row.row_id, similarity(queryVector, decodeVector(row.vector, queryVector.length)));
					importance.set(row.row_id, Number(row.importance ?? 0));
				}
				diagnostics.vectorCandidateMilliseconds = now() - vectorStart;
			} catch (error) {
				if (this.requireEmbeddingOnWrite) throw error;
				diagnostics.embeddingMilliseconds = now() - embeddingStart;
			}
		}
		diagnostics.vectorCandidates = vectorScores.size;

		let ids = this.selectCandidates(lexical, vectorScores, importance, candidateLimit);
		if (ids.size === 0) {
			const rows = this.database
				.prepare(`SELECT row_id FROM game_memories m WHERE ${filter.sql}
				ORDER BY importance DESC,tick DESC,row_id DESC LIMIT ?`)
				.all(...filter.values, candidateLimit) as unknown as CandidateRow[];
			ids = new Set(rows.map((row) => row.row_id));
		}
		const authoritativeStart = now();
		const boundedIds = [...ids];
		const rows =
			boundedIds.length === 0
				? []
				: (this.database
						.prepare(`SELECT row_id,memory_json FROM game_memories
			WHERE row_id IN (${boundedIds.map(() => "?").join(",")})`)
						.all(...boundedIds) as unknown as MemoryRow[]);
		const candidates = rows
			.map((row) => ({ rowId: row.row_id, memory: this.parseMemory(row.memory_json) }))
			.filter(({ memory }) => this.matches(memory, query));
		diagnostics.authoritativeMilliseconds = now() - authoritativeStart;
		diagnostics.authoritativeCandidates = candidates.length;

		const rerankStart = now();
		const matches = candidates
			.map(({ rowId, memory }): GameMemoryMatch => {
				const lexicalScore = lexical.get(rowId);
				const vectorScore = vectorScores.get(rowId);
				const relevance =
					lexicalScore === undefined
						? vectorScore === undefined
							? 0
							: (vectorScore + 1) / 2
						: vectorScore === undefined
							? lexicalScore
							: lexicalScore * 0.4 + ((vectorScore + 1) / 2) * 0.6;
				return {
					memory,
					score: relevance * 0.9 + memory.importance * 0.1,
					...(lexicalScore === undefined ? {} : { lexicalScore }),
					...(vectorScore === undefined ? {} : { vectorScore }),
				};
			})
			.sort((left, right) => right.score - left.score || right.memory.moment.tick - left.memory.moment.tick)
			.slice(0, query.limit);
		diagnostics.rerankMilliseconds = now() - rerankStart;
		diagnostics.returned = matches.length;
		return { matches, diagnostics };
	}

	async rebuildEmbeddings(session: GameSessionKey, signal?: AbortSignal): Promise<number> {
		this.ensureOpen();
		if (!this.embedding || !this.identity) throw new Error("No embedding provider is configured.");
		let afterRowId = 0;
		let rebuilt = 0;
		for (;;) {
			signal?.throwIfAborted();
			const rows = this.database
				.prepare(
					`SELECT row_id,memory_json FROM game_memories m WHERE row_id>? AND ${this.scopePredicate(session)} ORDER BY row_id LIMIT 64`,
				)
				.all(afterRowId, ...this.scopeValues(session)) as unknown as MemoryRow[];
			if (rows.length === 0) break;
			const memories = rows.map((row) => this.parseMemory(row.memory_json));
			const embedded = await this.embedding.embedDocuments(
				memories.map((memory) => memory.searchText ?? ""),
				signal,
			);
			if (embedded.length !== rows.length) throw new Error("Embedding provider returned the wrong batch size.");
			this.database.exec("BEGIN IMMEDIATE");
			try {
				for (let index = 0; index < rows.length; index += 1) {
					const row = rows[index];
					const vector = embedded[index];
					if (!row || !vector) throw new Error("Embedding rebuild batch is incomplete.");
					this.database.prepare("DELETE FROM game_memory_vector_buckets WHERE row_id=?").run(row.row_id);
					this.database.prepare("DELETE FROM game_memory_vectors WHERE row_id=?").run(row.row_id);
					this.writeVector(row.row_id, this.identity, normalize(vector, this.embedding.identity.dimensions));
					afterRowId = row.row_id;
					rebuilt += 1;
				}
				this.database.exec("COMMIT");
			} catch (error) {
				this.database.exec("ROLLBACK");
				throw error;
			}
		}
		return rebuilt;
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		this.database.close();
	}

	[Symbol.dispose](): void {
		this.close();
	}

	private writeVector(rowId: number, identity: string, vector: Float32Array): void {
		this.database
			.prepare("INSERT INTO game_memory_vectors(row_id,embedding_identity,dimensions,vector) VALUES (?,?,?,?)")
			.run(rowId, identity, vector.length, encodeVector(vector));
		const insert = this.database.prepare(
			"INSERT INTO game_memory_vector_buckets(embedding_identity,bucket,row_id) VALUES (?,?,?)",
		);
		for (const bucket of vectorBuckets(vector)) insert.run(identity, bucket, rowId);
	}

	private scopePredicate(session: GameSessionKey): string {
		void session;
		return "m.world_id=? AND m.save_id=? AND m.timeline_id=? AND m.generation=? AND ((m.scope='actor' AND m.actor_id=?) OR (m.scope='owner' AND m.owner_id=?) OR m.scope='world')";
	}

	private scopeValues(session: GameSessionKey): [string, string, string, number, string, string] {
		return [session.worldId, session.saveId, session.timelineId, session.generation, session.actorId, session.ownerId];
	}

	private queryPredicate(query: GameMemoryQuery): { sql: string; values: (string | number)[] } {
		const clauses = [this.scopePredicate(query.session)];
		const values: (string | number)[] = [...this.scopeValues(query.session)];
		if (query.scopes) {
			if (query.scopes.length === 0) clauses.push("1=0");
			else {
				clauses.push(`m.scope IN (${query.scopes.map(() => "?").join(",")})`);
				values.push(...query.scopes);
			}
		}
		if (query.kinds) {
			if (query.kinds.length === 0) clauses.push("1=0");
			else {
				clauses.push(`m.kind IN (${query.kinds.map(() => "?").join(",")})`);
				values.push(...query.kinds);
			}
		}
		for (const tag of query.tags ?? []) {
			clauses.push("EXISTS (SELECT 1 FROM game_memory_tags mt WHERE mt.tag=? AND mt.row_id=m.row_id)");
			values.push(tag);
		}
		if (query.atOrBeforeTick !== undefined) {
			clauses.push("m.tick<=?");
			values.push(query.atOrBeforeTick);
		}
		if (query.minimumImportance !== undefined) {
			clauses.push("m.importance>=?");
			values.push(query.minimumImportance);
		}
		return { sql: clauses.join(" AND "), values };
	}

	private selectCandidates(
		lexical: ReadonlyMap<number, number>,
		vectors: ReadonlyMap<number, number>,
		importance: ReadonlyMap<number, number>,
		limit: number,
	): Set<number> {
		return new Set(
			[...new Set([...lexical.keys(), ...vectors.keys()])]
				.map((rowId) => {
					const lexicalScore = lexical.get(rowId);
					const vectorScore = vectors.get(rowId);
					const relevance =
						lexicalScore === undefined
							? vectorScore === undefined
								? 0
								: (vectorScore + 1) / 2
							: vectorScore === undefined
								? lexicalScore
								: lexicalScore * 0.4 + ((vectorScore + 1) / 2) * 0.6;
					return { rowId, score: relevance * 0.9 + (importance.get(rowId) ?? 0) * 0.1 };
				})
				.sort((left, right) => right.score - left.score || right.rowId - left.rowId)
				.slice(0, limit)
				.map(({ rowId }) => rowId),
		);
	}

	private matches(memory: GameMemory, query: GameMemoryQuery): boolean {
		if (query.scopes && !query.scopes.includes(memory.scope)) return false;
		if (query.kinds && !query.kinds.includes(memory.kind)) return false;
		if (query.tags && !query.tags.every((tag) => memory.tags?.includes(tag))) return false;
		if (query.atOrBeforeTick !== undefined && memory.moment.tick > query.atOrBeforeTick) return false;
		if (query.minimumImportance !== undefined && memory.importance < query.minimumImportance) return false;
		return true;
	}

	private parseMemory(json: string): GameMemory {
		try {
			const memory = JSON.parse(json) as GameMemory;
			this.validateMemory(memory);
			return memory;
		} catch {
			throw new Error("Stored game memory is corrupt.");
		}
	}

	private validateMemory(memory: GameMemory): void {
		this.validateSession(memory.session);
		validatePortableId(memory.id, "Memory id");
		validatePortableId(memory.kind, "Memory kind");
		if (!validMemoryScopes.has(memory.scope)) throw new TypeError("Memory scope is invalid.");
		if (!Number.isFinite(memory.importance) || memory.importance < 0 || memory.importance > 1)
			throw new RangeError("Memory importance must be between 0 and 1.");
		if (!Number.isFinite(memory.moment?.tick) || !Number.isSafeInteger(memory.createdAt) || memory.createdAt < 0)
			throw new RangeError("Memory time is invalid.");
		if (memory.searchText !== undefined && memory.searchText.length > this.maximumSearchCharacters)
			throw new RangeError("Memory search text is too large.");
		if (memory.tags && (memory.tags.length > 64 || memory.tags.some((tag) => !/^[\p{L}\p{N}._:-]{1,64}$/u.test(tag))))
			throw new TypeError("Memory tags are invalid.");
	}

	private validateQuery(query: GameMemoryQuery): void {
		this.validateSession(query.session);
		if (!Number.isInteger(query.limit) || query.limit < 1 || query.limit > 128)
			throw new RangeError("Memory query limit must be between 1 and 128.");
		if (query.text !== undefined && query.text.length > this.maximumSearchCharacters)
			throw new RangeError("Memory query text is too large.");
		if (
			query.minimumImportance !== undefined &&
			(!Number.isFinite(query.minimumImportance) || query.minimumImportance < 0 || query.minimumImportance > 1)
		)
			throw new RangeError("minimumImportance is invalid.");
		if (query.atOrBeforeTick !== undefined && !Number.isFinite(query.atOrBeforeTick))
			throw new RangeError("atOrBeforeTick is invalid.");
		if (
			query.scopes &&
			(query.scopes.length > validMemoryScopes.size || query.scopes.some((scope) => !validMemoryScopes.has(scope)))
		)
			throw new TypeError("Memory query scopes are invalid.");
		if (
			query.kinds &&
			(query.kinds.length > 64 || query.kinds.some((kind) => !/^[a-z0-9][a-z0-9._:-]{0,191}$/i.test(kind)))
		)
			throw new TypeError("Memory query kinds are invalid.");
		if (query.tags && (query.tags.length > 64 || query.tags.some((tag) => !/^[\p{L}\p{N}._:-]{1,64}$/u.test(tag))))
			throw new TypeError("Memory query tags are invalid.");
	}

	private validateSession(session: GameSessionKey): void {
		validatePortableId(session.worldId, "World id");
		validatePortableId(session.saveId, "Save id");
		validatePortableId(session.timelineId, "Timeline id");
		validatePortableId(session.ownerId, "Owner id");
		validatePortableId(session.sessionId, "Session id");
		validatePortableId(session.actorId, "Actor id");
		if (!Number.isSafeInteger(session.generation) || session.generation < 0)
			throw new RangeError("Session generation is invalid.");
	}

	private ensureOpen(): void {
		if (this.closed) throw new Error("SQLite memory store is closed.");
	}
}

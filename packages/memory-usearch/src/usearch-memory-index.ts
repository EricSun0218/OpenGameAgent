import { createHash, randomUUID } from "node:crypto";
import {
	createReadStream,
	lstatSync,
	mkdirSync,
	readFileSync,
	realpathSync,
	renameSync,
	rmSync,
	writeFileSync,
} from "node:fs";
import { join, resolve } from "node:path";
import {
	type MemoryIndexPartition,
	type MemoryVectorEntry,
	type MemoryVectorIndex,
	normalizeIndexPartition,
	type RankedMemoryCandidate,
	validateEmbeddingIdentity,
	validateResultLimit,
	validateVector,
} from "@opengameagent/memory";
import { Mutex } from "async-mutex";
import { Index, type IndexConfig, MetricKind, ScalarKind } from "usearch";

export interface UsearchMemoryIndexOptions {
	readonly modelId: string;
	readonly dimensions: number;
	/** Host-selected HNSW graph connectivity: zero selects the upstream default; explicit values start at two. */
	readonly connectivity: number;
	/** Host-selected HNSW construction expansion. Zero explicitly selects the upstream default. */
	readonly expansionAdd: number;
	/** Host-selected HNSW query expansion. Zero explicitly selects the upstream default. */
	readonly expansionSearch: number;
	/** An exclusive host-owned directory for disposable index snapshots. */
	readonly persistence?: { readonly directory: string; readonly maxSnapshotBytes: number };
}

interface PartitionState {
	index: Index;
	readonly mutations: Mutex;
	revision: string | undefined;
	readyRevision: string | undefined;
}

export class UsearchMemoryIndex implements MemoryVectorIndex {
	readonly modelId: string;
	readonly dimensions: number;
	private readonly indexConfiguration: Readonly<IndexConfig>;
	private readonly partitions = new Map<string, PartitionState>();
	private readonly persistence: UsearchMemoryIndexOptions["persistence"];

	constructor(options: UsearchMemoryIndexOptions) {
		const modelId = options.modelId;
		const dimensions = options.dimensions;
		const connectivity = requireConnectivity(options.connectivity);
		const expansionAdd = requireNonNegativeInteger(options.expansionAdd, "invalid_usearch_expansion_add");
		const expansionSearch = requireNonNegativeInteger(options.expansionSearch, "invalid_usearch_expansion_search");
		validateEmbeddingIdentity(modelId, dimensions);
		this.modelId = modelId;
		this.dimensions = dimensions;
		this.indexConfiguration = Object.freeze({
			dimensions,
			metric: MetricKind.Cos,
			quantization: ScalarKind.F32,
			connectivity,
			expansion_add: expansionAdd,
			expansion_search: expansionSearch,
			multi: false,
		});
		const persistence = options.persistence;
		if (persistence !== undefined) {
			const directory = resolve(persistence.directory);
			const maxSnapshotBytes = persistence.maxSnapshotBytes;
			validateResultLimit(maxSnapshotBytes, Number.MAX_SAFE_INTEGER, "invalid_usearch_snapshot_budget");
			mkdirSync(directory, { recursive: true, mode: 0o700 });
			if (!lstatSync(directory).isDirectory() || lstatSync(directory).isSymbolicLink()) {
				throw new TypeError("USearch persistence directory must be a real directory.");
			}
			this.persistence = Object.freeze({ directory: realpathSync(directory), maxSnapshotBytes });
		}
	}

	async upsert(partition: MemoryIndexPartition, entry: MemoryVectorEntry): Promise<void> {
		const normalizedPartition = normalizeIndexPartition(partition);
		const memoryKey = entry.memoryKey;
		const vector = snapshotVector(entry.vector, this.dimensions);
		const state = this.getOrCreate(normalizedPartition);
		await state.mutations.runExclusive(() => {
			state.revision = undefined;
			state.readyRevision = undefined;
			state.index.remove(memoryKey);
			state.index.add(memoryKey, vector);
		});
	}

	async remove(partition: MemoryIndexPartition, memoryKey: bigint): Promise<void> {
		const normalizedPartition = normalizeIndexPartition(partition);
		const state = this.partitions.get(partitionKey(normalizedPartition));
		if (!state) return;
		await state.mutations.runExclusive(() => {
			state.revision = undefined;
			state.readyRevision = undefined;
			state.index.remove(memoryKey);
		});
	}

	async search(
		partition: MemoryIndexPartition,
		vector: Float32Array,
		limit: number,
	): Promise<readonly RankedMemoryCandidate[]> {
		const normalizedPartition = normalizeIndexPartition(partition);
		const normalizedVector = snapshotVector(vector, this.dimensions);
		validateResultLimit(limit, Number.MAX_SAFE_INTEGER, "invalid_vector_limit");
		const index = this.partitions.get(partitionKey(normalizedPartition))?.index;
		const size = index?.size() ?? 0;
		if (!index || size === 0) return [];
		const matches = index.search(normalizedVector, Math.min(limit, size), 0);
		return Array.from(matches.keys, (memoryKey, position) => ({
			memoryKey,
			score: 1 - (matches.distances[position] ?? Number.POSITIVE_INFINITY),
		}));
	}

	async replace(
		partition: MemoryIndexPartition,
		entries: AsyncIterable<MemoryVectorEntry>,
		revision?: string,
	): Promise<void> {
		const normalizedPartition = normalizeIndexPartition(partition);
		const state = this.getOrCreate(normalizedPartition);
		await state.mutations.runExclusive(async () => {
			const replacement = this.createIndex();
			for await (const entry of entries) {
				const memoryKey = entry.memoryKey;
				const vector = snapshotVector(entry.vector, this.dimensions);
				replacement.add(memoryKey, vector);
			}
			state.index = replacement;
			state.revision = revision;
			state.readyRevision = undefined;
		});
	}

	async restore(partition: MemoryIndexPartition, revision: string): Promise<boolean> {
		const normalized = normalizeIndexPartition(partition);
		const persistence = this.persistence;
		if (!persistence) return false;
		const state = this.getOrCreate(normalized);
		return state.mutations.runExclusive(async () => {
			if (state.readyRevision === revision) return true;
			const path = this.snapshotPath(normalized);
			try {
				// Two SHA-256 hex digests separated by one newline: fixed-size, content-free metadata.
				const metadataInfo = lstatSync(`${path}.meta`);
				if (!metadataInfo.isFile() || metadataInfo.size !== 129) return false;
				const metadata = readFileSync(`${path}.meta`, "utf8");
				if (!/^[a-f0-9]{64}\n[a-f0-9]{64}$/u.test(metadata) || metadata.slice(0, 64) !== digest(revision)) return false;
				const file = lstatSync(path);
				if (!file.isFile() || file.size > persistence.maxSnapshotBytes || file.size < 1) return false;
				if ((await fileDigest(path)) !== metadata.slice(65)) return false;
				const replacement = this.createIndex();
				replacement.load(path);
				if (replacement.dimensions() !== this.dimensions) return false;
				state.index = replacement;
				state.revision = revision;
				state.readyRevision = revision;
				return true;
			} catch (error) {
				if (error instanceof Error && "code" in error && error.code !== "ENOENT") throw error;
				// Missing or invalid derived data is rebuilt from the authority under the host's preparation policy.
				return false;
			}
		});
	}

	async checkpoint(partition: MemoryIndexPartition, revision: string): Promise<boolean> {
		const normalized = normalizeIndexPartition(partition);
		const persistence = this.persistence;
		if (!persistence) return false;
		const state = this.partitions.get(partitionKey(normalized));
		if (!state) return false;
		return state.mutations.runExclusive(async () => {
			if (state.revision !== revision) return false;
			const path = this.snapshotPath(normalized);
			const temporary = `${path}.${randomUUID()}.tmp`;
			try {
				writeFileSync(temporary, "", { flag: "wx", mode: 0o600 });
				state.index.save(temporary);
				if (lstatSync(temporary).size > persistence.maxSnapshotBytes)
					throw new RangeError("usearch_snapshot_budget_exceeded");
				const metadata = `${digest(revision)}\n${await fileDigest(temporary)}`;
				writeFileSync(`${temporary}.meta`, metadata, { flag: "wx", mode: 0o600 });
				renameSync(temporary, path);
				renameSync(`${temporary}.meta`, `${path}.meta`);
				state.readyRevision = revision;
				return true;
			} finally {
				rmSync(temporary, { force: true });
				rmSync(`${temporary}.meta`, { force: true });
			}
		});
	}

	private snapshotPath(partition: MemoryIndexPartition): string {
		if (!this.persistence) throw new TypeError("USearch persistence is not configured.");
		const directory = lstatSync(this.persistence.directory);
		if (!directory.isDirectory() || directory.isSymbolicLink()) {
			throw new TypeError("USearch persistence directory must remain a real directory.");
		}
		return join(
			this.persistence.directory,
			`${digest(JSON.stringify([this.modelId, this.indexConfiguration, partitionKey(partition)]))}.usearch`,
		);
	}

	partitionSize(partition: MemoryIndexPartition): number {
		const normalizedPartition = normalizeIndexPartition(partition);
		return this.partitions.get(partitionKey(normalizedPartition))?.index.size() ?? 0;
	}

	private getOrCreate(partition: MemoryIndexPartition): PartitionState {
		const key = partitionKey(partition);
		const existing = this.partitions.get(key);
		if (existing) return existing;
		const created = {
			index: this.createIndex(),
			mutations: new Mutex(),
			revision: undefined,
			readyRevision: undefined,
		};
		this.partitions.set(key, created);
		return created;
	}

	private createIndex(): Index {
		return new Index(this.indexConfiguration);
	}
}

function digest(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}

async function fileDigest(path: string): Promise<string> {
	const hash = createHash("sha256");
	for await (const chunk of createReadStream(path)) hash.update(chunk);
	return hash.digest("hex");
}

function requireNonNegativeInteger(value: number, code: string): number {
	if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(code);
	return value;
}

function requireConnectivity(value: number): number {
	const connectivity = requireNonNegativeInteger(value, "invalid_usearch_connectivity");
	if (connectivity === 1) throw new RangeError("invalid_usearch_connectivity");
	return connectivity;
}

function partitionKey(partition: MemoryIndexPartition): string {
	return JSON.stringify([partition.ownerId, partition.timelineId, partition.generation]);
}

function snapshotVector(vector: Float32Array, dimensions: number): Float32Array {
	const length = vector.length;
	const normalized = new Float32Array(length);
	for (let index = 0; index < length; index += 1) normalized[index] = vector[index] as number;
	validateVector(normalized, dimensions);
	return normalized;
}

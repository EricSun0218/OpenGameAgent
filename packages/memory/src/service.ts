import { createHash } from "node:crypto";
import { assertOpaqueIdentifier, raceOperationWithAbort } from "@opengameagent/protocol";
import Queue from "yocto-queue";
import { MemoryRecallUnavailableError, MemoryRerankerError } from "./errors.js";
import { fuseMemoryRanks } from "./fusion.js";
import type {
	MemoryAuthority,
	MemoryDraft,
	MemoryEmbedding,
	MemoryEmbeddingProvider,
	MemoryIndexPartition,
	MemoryIndexPreparationCloseMode,
	MemoryIndexPreparationOptions,
	MemoryMutationResult,
	MemoryRebuildResult,
	MemoryRecallHit,
	MemoryRecallRequest,
	MemoryRecallResult,
	MemoryRecallStageMetric,
	MemoryRecord,
	MemoryReranker,
	MemoryRevisionPatch,
	MemoryVectorEntry,
	MemoryVectorIndex,
} from "./types.js";
import {
	memoryIndexPartition,
	normalizeIndexPartition,
	normalizeMemorySelector,
	normalizeMemoryViewer,
	normalizeMemoryWrite,
	normalizeRevisionPatch,
	validateEmbeddingIdentity,
	validateMemoryLimits,
	validateQuery,
	validateResultLimit,
	validateRevision,
	validateTimestamp,
	validateVector,
} from "./validation.js";

export interface GameMemoryOptions {
	readonly authority: MemoryAuthority;
	readonly embeddingProvider?: MemoryEmbeddingProvider;
	readonly vectorIndex?: MemoryVectorIndex;
	readonly reranker?: MemoryReranker;
	readonly now?: () => string;
	readonly indexPreparation?: Readonly<MemoryIndexPreparationOptions>;
}

interface MemoryIndexPreparation {
	readonly background: boolean;
	readonly overflowEpoch: object;
	readonly state: MemoryIndexPartitionState;
	readonly mutationEpoch: object;
}

interface MemoryIndexPartitionState {
	activeMutations: number;
	readonly key: string;
	mutationEpoch: object;
	preparation: MemoryIndexPreparation | undefined;
	revision: string | undefined;
	status: "unknown" | "preparing" | "failed" | "ready";
}

interface MemoryBackgroundIndexPreparationJob {
	readonly controller: AbortController;
	readonly completion: Promise<void>;
	readonly key: string;
	readonly run: (signal: AbortSignal) => Promise<MemoryRebuildResult>;
	resolve(): void;
}

interface MemoryExplicitIndexPreparation {
	readonly completion: Promise<void>;
	readonly controller: AbortController;
}

interface MemoryBackgroundIndexPreparation {
	active: number;
	closed: boolean;
	readonly explicit: Set<MemoryExplicitIndexPreparation>;
	readonly jobs: Map<string, MemoryBackgroundIndexPreparationJob>;
	readonly maxConcurrentPartitions: number;
	readonly maxPendingPartitions: number;
	readonly queue: Queue<MemoryBackgroundIndexPreparationJob>;
	readonly retryFailure: MemoryIndexPreparationOptions["retryFailure"];
}

interface MemoryIndexPreparationTracker {
	readonly authority: MemoryAuthority;
	background: MemoryBackgroundIndexPreparation | undefined;
	readonly maxPartitions: number;
	mode: "unset" | "foreground" | "background";
	overflowEpoch: object;
	overflowMutations: number;
	readonly partitions: Map<string, MemoryIndexPartitionState>;
}

interface MemoryIndexMutationEntry {
	readonly key?: string;
	readonly state?: MemoryIndexPartitionState;
}

interface MemoryIndexMutation {
	readonly entries: readonly MemoryIndexMutationEntry[];
}

const INDEX_PREPARATION_BY_VECTOR_INDEX = new WeakMap<MemoryVectorIndex, MemoryIndexPreparationTracker>();

export class GameMemory {
	private readonly authority: MemoryAuthority;
	private readonly limits: MemoryAuthority["limits"];
	private readonly embeddingProvider: MemoryEmbeddingProvider | undefined;
	private readonly vectorIndex: MemoryVectorIndex | undefined;
	private readonly reranker: MemoryReranker | undefined;
	private readonly now: () => string;
	private readonly indexPreparation: MemoryIndexPreparationTracker;
	private readonly backgroundIndexPreparation: MemoryBackgroundIndexPreparation | undefined;

	constructor(options: GameMemoryOptions) {
		const sourceAuthority = options.authority;
		const authority = captureAuthority(sourceAuthority);
		const sourceEmbeddingProvider = options.embeddingProvider;
		const sourceVectorIndex = options.vectorIndex;
		const sourceReranker = options.reranker;
		const sourceIndexPreparation = options.indexPreparation;
		const embeddingProvider =
			sourceEmbeddingProvider === undefined ? undefined : captureEmbeddingProvider(sourceEmbeddingProvider);
		const vectorIndex = sourceVectorIndex === undefined ? undefined : captureVectorIndex(sourceVectorIndex);
		const reranker = sourceReranker === undefined ? undefined : captureReranker(sourceReranker);
		const now = options.now;
		const limits = authority.limits;
		if ((embeddingProvider === undefined) !== (vectorIndex === undefined)) {
			throw new TypeError("Embedding provider and vector index must be configured together.");
		}
		if (sourceIndexPreparation !== undefined && embeddingProvider === undefined) {
			throw new TypeError("Embedding provider and vector index must be configured for background index preparation.");
		}
		if (
			embeddingProvider &&
			vectorIndex &&
			(embeddingProvider.modelId !== vectorIndex.modelId || embeddingProvider.dimensions !== vectorIndex.dimensions)
		) {
			throw new TypeError("Embedding provider and vector index identities must match.");
		}
		if (embeddingProvider) {
			validateEmbeddingIdentity(embeddingProvider.modelId, embeddingProvider.dimensions);
			validateResultLimit(
				embeddingProvider.maxTextsPerRequest,
				Number.MAX_SAFE_INTEGER,
				"invalid_embedding_request_limit",
			);
		}
		if (vectorIndex) validateEmbeddingIdentity(vectorIndex.modelId, vectorIndex.dimensions);
		this.authority = authority;
		this.limits = limits;
		this.embeddingProvider = embeddingProvider;
		this.vectorIndex = vectorIndex;
		this.reranker = reranker;
		this.now = now ?? (() => new Date().toISOString());
		this.indexPreparation =
			sourceVectorIndex === undefined
				? createIndexPreparationTracker(sourceAuthority, limits.maxTrackedIndexPartitions)
				: captureIndexPreparationTracker(sourceVectorIndex, sourceAuthority, limits.maxTrackedIndexPartitions);
		this.backgroundIndexPreparation = captureBackgroundIndexPreparation(this.indexPreparation, sourceIndexPreparation);
	}

	async remember(draft: MemoryDraft, signal?: AbortSignal): Promise<MemoryMutationResult> {
		throwIfAborted(signal);
		const write = normalizeMemoryWrite({ ...draft, recordedAt: this.now() }, this.limits);
		const mutation = this.beginIndexMutation([memoryIndexPartition(write)]);
		try {
			const record = await this.authority.remember(write);
			return { record, indexStatus: await this.synchronize(record, signal) };
		} finally {
			this.endIndexMutation(mutation);
		}
	}

	async revise(
		memoryId: string,
		expectedRevision: string,
		patch: MemoryRevisionPatch,
		signal?: AbortSignal,
	): Promise<MemoryMutationResult> {
		throwIfAborted(signal);
		validateRevision(expectedRevision);
		const revisedAt = this.now();
		validateTimestamp(revisedAt);
		const normalizedPatch = normalizeRevisionPatch(patch, this.limits);
		const current = this.vectorIndex ? await this.authority.get(memoryId, { kind: "internal" }) : undefined;
		const mutation = this.beginIndexMutation(
			current === undefined ? [] : revisionIndexPartitions(current, normalizedPatch),
		);
		let revised: MemoryRecord | undefined;
		try {
			const record = await this.authority.revise(memoryId, expectedRevision, normalizedPatch, revisedAt);
			revised = record;
			const cleared = await this.removeFromIndex(record);
			const synchronized = await this.synchronize(record, signal);
			return { record, indexStatus: cleared || synchronized === "ready" ? synchronized : "pending" };
		} finally {
			this.endIndexMutation(mutation, revised === undefined ? [] : [memoryIndexPartition(revised)]);
		}
	}

	async forget(memoryId: string, expectedRevision: string): Promise<MemoryMutationResult> {
		validateRevision(expectedRevision);
		const forgottenAt = this.now();
		validateTimestamp(forgottenAt);
		const current = this.vectorIndex ? await this.authority.get(memoryId, { kind: "internal" }) : undefined;
		const mutation = this.beginIndexMutation(current === undefined ? [] : [memoryIndexPartition(current)]);
		let forgotten: MemoryRecord | undefined;
		try {
			const record = await this.authority.forget(memoryId, expectedRevision, forgottenAt);
			forgotten = record;
			if (!this.vectorIndex) return { record, indexStatus: "not-configured" };
			try {
				await this.vectorIndex.remove(memoryIndexPartition(record), record.memoryKey);
				return { record, indexStatus: "ready" };
			} catch {
				return { record, indexStatus: "pending" };
			}
		} finally {
			this.endIndexMutation(mutation, forgotten === undefined ? [] : [memoryIndexPartition(forgotten)]);
		}
	}

	async recall(request: MemoryRecallRequest): Promise<MemoryRecallResult> {
		const query = request.query;
		const selector = normalizeMemorySelector(request.selector, this.limits);
		const viewer = normalizeMemoryViewer(request.viewer);
		const resultLimit = request.resultLimit;
		const vectorFailure = request.vectorFailure;
		const signal = request.signal;
		throwIfAborted(signal);
		if (vectorFailure !== "fail" && vectorFailure !== "lexical-only") {
			throw new TypeError("Invalid vector recall failure policy.");
		}
		validateQuery(query, this.limits);
		validateResultLimit(resultLimit, this.limits.maxRecallResults, "invalid_result_limit");

		const stages: MemoryRecallStageMetric[] = [];
		let startedAt = performance.now();
		const partitionMetadata = await this.authority.readPartitionState(selector);
		throwIfAborted(signal);
		stages.push(recallStage("partition", "ok", startedAt, 1, partitionMetadata.activeCount));
		if (partitionMetadata.activeCount === 0) {
			return { hits: [], vectorStatus: this.vectorIndex ? "proven-empty" : "not-configured", stages };
		}
		startedAt = performance.now();
		const lexical = await this.authority.searchLexical(selector, viewer, query, this.limits.maxLexicalCandidates);
		throwIfAborted(signal);
		stages.push(recallStage("lexical", "ok", startedAt, 1, lexical.length));
		let vectorStatus: MemoryRecallResult["vectorStatus"] = "not-configured";
		let vector = [] as Awaited<ReturnType<MemoryVectorIndex["search"]>>;
		if (this.embeddingProvider && this.vectorIndex) {
			let vectorStage: "index-restore" | "embedding" | "vector" = "index-restore";
			startedAt = performance.now();
			try {
				let partitionState = this.indexPreparation.partitions.get(memoryIndexPartitionKey(selector));
				if (partitionState?.revision !== undefined && partitionState.revision !== partitionMetadata.revision) {
					this.invalidateIndexPartitionState(partitionState);
					partitionState = undefined;
				}
				if (partitionState === undefined && this.vectorIndex.restore) {
					const restored = await this.vectorIndex.restore(selector, partitionMetadata.revision);
					throwIfAborted(signal);
					stages.push(recallStage("index-restore", "ok", startedAt, 1, restored ? 1 : 0));
					if (restored) {
						partitionState = this.acquireIndexPartitionState(memoryIndexPartitionKey(selector));
						if (partitionState && partitionState.activeMutations === 0 && !partitionState.preparation) {
							partitionState.status = "ready";
							partitionState.revision = partitionMetadata.revision;
						}
					}
				}
				if (this.backgroundIndexPreparation && partitionState?.status !== "ready") {
					vectorStatus = this.scheduleIndexPreparation(selector);
				} else {
					vectorStage = "embedding";
					startedAt = performance.now();
					const embedded = await this.embeddingProvider.embed([query], signal);
					throwIfAborted(signal);
					const [queryVector] = snapshotVectorBatch(embedded, 1, this.embeddingProvider.dimensions);
					if (!queryVector) throw new TypeError("Embedding provider returned no query vector.");
					stages.push(recallStage("embedding", "ok", startedAt, 1, 1));
					vectorStage = "vector";
					startedAt = performance.now();
					vector = await this.vectorIndex.search(selector, queryVector, this.limits.maxVectorCandidates);
					throwIfAborted(signal);
					stages.push(recallStage("vector", "ok", startedAt, 1, vector.length));
					vectorStatus = "used";
				}
			} catch {
				throwIfAborted(signal);
				if (vectorFailure === "fail") throw new MemoryRecallUnavailableError();
				stages.push(recallStage(vectorStage, "failed-open", startedAt, 1, 0));
				vectorStatus = "failed-open";
			}
		}

		startedAt = performance.now();
		const fusionInputCount = lexical.length + vector.length;
		const fused = fuseMemoryRanks([lexical, vector]).slice(0, this.limits.maxHydrateCandidates);
		stages.push(recallStage("fusion", "ok", startedAt, fusionInputCount, fused.length));
		startedAt = performance.now();
		const records = await this.authority.hydrate(
			fused.map((candidate) => candidate.memoryKey),
			selector,
			viewer,
		);
		throwIfAborted(signal);
		stages.push(recallStage("hydrate", "ok", startedAt, fused.length, records.length));
		const scoreByKey = new Map(fused.map((candidate) => [candidate.memoryKey, candidate.score]));
		let hits = records
			.map((record): MemoryRecallHit => ({ record, score: scoreByKey.get(record.memoryKey) ?? 0 }))
			.sort((left, right) => right.score - left.score || (left.record.memoryKey < right.record.memoryKey ? -1 : 1));
		if (this.reranker && hits.length > 1) {
			startedAt = performance.now();
			const rerankInputCount = hits.length;
			hits = await this.applyReranker(query, hits, signal);
			stages.push(recallStage("rerank", "ok", startedAt, rerankInputCount, hits.length));
		}
		return { hits: hits.slice(0, resultLimit), vectorStatus, stages };
	}

	async closeIndexPreparation(mode: MemoryIndexPreparationCloseMode): Promise<void> {
		if (mode !== "drain" && mode !== "abort") throw new TypeError("Invalid memory index preparation close mode.");
		const background = this.backgroundIndexPreparation;
		if (!background) return;
		background.closed = true;
		const jobs = [...background.jobs.values()];
		const explicit = [...background.explicit];
		if (mode === "abort") {
			for (const job of jobs) job.controller.abort(new Error("Memory index preparation closed."));
			for (const operation of explicit) operation.controller.abort(new Error("Memory index preparation closed."));
		}
		await Promise.all([...jobs.map((job) => job.completion), ...explicit.map((operation) => operation.completion)]);
		if (this.indexPreparation.background === background) {
			if (mode === "abort") {
				for (const job of jobs) {
					const state = this.indexPreparation.partitions.get(job.key);
					if (state !== undefined && (state.status === "preparing" || state.status === "failed")) {
						this.invalidateIndexPartitionState(state);
					}
				}
			}
			for (const state of [...this.indexPreparation.partitions.values()]) {
				if (state.status === "failed") this.invalidateIndexPartitionState(state);
			}
			this.indexPreparation.background = undefined;
			this.indexPreparation.mode = "unset";
		}
	}

	async rebuild(partition: MemoryIndexPartition, signal?: AbortSignal): Promise<MemoryRebuildResult> {
		throwIfAborted(signal);
		if (!this.embeddingProvider || !this.vectorIndex) return { indexed: 0, embedded: 0, restored: 0 };
		if (this.backgroundIndexPreparation?.closed) throw new Error("Memory index preparation is closed.");
		const normalizedPartition = normalizeIndexPartition(partition);
		const backgroundJob = this.backgroundIndexPreparation?.jobs.get(memoryIndexPartitionKey(normalizedPartition));
		if (backgroundJob) {
			if (signal) await raceOperationWithAbort(() => backgroundJob.completion, signal);
			else await backgroundJob.completion;
		}
		throwIfAborted(signal);
		const background = this.backgroundIndexPreparation;
		if (!background) return this.rebuildPartition(normalizedPartition, signal, false);
		if (background.closed) throw new Error("Memory index preparation is closed.");
		const controller = new AbortController();
		const operationSignal = signal ? AbortSignal.any([signal, controller.signal]) : controller.signal;
		throwIfAborted(operationSignal);
		const running = this.rebuildPartition(normalizedPartition, operationSignal, false);
		const operation: MemoryExplicitIndexPreparation = {
			completion: running.then(
				() => undefined,
				() => undefined,
			),
			controller,
		};
		background.explicit.add(operation);
		try {
			return await running;
		} finally {
			background.explicit.delete(operation);
		}
	}

	private async rebuildPartition(
		input: MemoryIndexPartition,
		signal: AbortSignal | undefined,
		background: boolean,
	): Promise<MemoryRebuildResult> {
		if (!this.embeddingProvider || !this.vectorIndex) return { indexed: 0, embedded: 0, restored: 0 };
		const partition = normalizeIndexPartition(input);
		const preparation = this.beginIndexTransition(partition, background);
		let indexed = 0;
		let embedded = 0;
		let restored = 0;
		let activeRecords = 0;
		let scanCompleted = false;
		const entries = this.rebuildEntries(
			partition,
			signal,
			(count) => {
				activeRecords += count;
			},
			() => {
				scanCompleted = true;
			},
			() => {
				indexed += 1;
			},
			() => {
				embedded += 1;
			},
		);
		try {
			const metadata = await this.authority.readPartitionState(partition);
			throwIfAborted(signal);
			const didRestore = await this.vectorIndex.restore?.(partition, metadata.revision);
			if (didRestore) {
				restored = metadata.activeCount;
				activeRecords = metadata.activeCount;
				scanCompleted = true;
			} else {
				throwIfAborted(signal);
				await this.vectorIndex.replace(partition, entries, metadata.revision);
			}
			throwIfAborted(signal);
			const currentMetadata = await this.authority.readPartitionState(partition);
			throwIfAborted(signal);
			const durableStable =
				currentMetadata.revision === metadata.revision &&
				currentMetadata.activeCount === activeRecords &&
				scanCompleted &&
				indexed + restored === activeRecords;
			if (durableStable && !didRestore) {
				await this.vectorIndex.checkpoint?.(partition, metadata.revision);
				throwIfAborted(signal);
			}
			if (preparation !== undefined && this.isCurrentIndexPreparation(preparation)) {
				const stable =
					preparation.state.activeMutations === 0 &&
					preparation.state.mutationEpoch === preparation.mutationEpoch &&
					this.indexPreparation.overflowMutations === 0 &&
					this.indexPreparation.overflowEpoch === preparation.overflowEpoch;
				if (stable && durableStable) {
					preparation.state.preparation = undefined;
					preparation.state.status = "ready";
					preparation.state.revision = metadata.revision;
				} else if (stable && preparation.background) {
					preparation.state.preparation = undefined;
					preparation.state.status = "failed";
				} else this.invalidateIndexPartitionState(preparation.state);
			}
		} catch (error) {
			if (preparation !== undefined && this.isCurrentIndexPreparation(preparation)) {
				if (preparation.background) {
					preparation.state.preparation = undefined;
					preparation.state.status = "failed";
				} else this.invalidateIndexPartitionState(preparation.state);
			}
			throw error;
		}
		return { indexed, embedded, restored };
	}

	private beginIndexTransition(
		partition: MemoryIndexPartition,
		background: boolean,
	): MemoryIndexPreparation | undefined {
		const key = memoryIndexPartitionKey(partition);
		const state = this.acquireIndexPartitionState(key);
		if (state === undefined) return undefined;
		const preparation: MemoryIndexPreparation = {
			background,
			mutationEpoch: state.mutationEpoch,
			overflowEpoch: this.indexPreparation.overflowEpoch,
			state,
		};
		state.preparation = preparation;
		state.status = background ? "preparing" : "unknown";
		return preparation;
	}

	private isCurrentIndexPreparation(preparation: MemoryIndexPreparation): boolean {
		return (
			this.indexPreparation.partitions.get(preparation.state.key) === preparation.state &&
			preparation.state.preparation === preparation
		);
	}

	private beginIndexMutation(partitions: readonly MemoryIndexPartition[]): MemoryIndexMutation {
		const keys = [...new Set(partitions.map(memoryIndexPartitionKey))];
		if (keys.length === 0) return { entries: [this.beginOverflowIndexMutation()] };
		const entries = keys.map((key): MemoryIndexMutationEntry => {
			const state = this.acquireIndexPartitionState(key);
			if (state === undefined) return this.beginOverflowIndexMutation(key);
			state.activeMutations += 1;
			state.mutationEpoch = {};
			state.preparation = undefined;
			state.status = "unknown";
			return { key, state };
		});
		return { entries };
	}

	private endIndexMutation(
		mutation: MemoryIndexMutation,
		additionalPartitions: readonly MemoryIndexPartition[] = [],
	): void {
		for (const entry of mutation.entries) {
			if (entry.state === undefined) {
				this.indexPreparation.overflowMutations -= 1;
				this.indexPreparation.overflowEpoch = {};
				if (entry.key !== undefined) this.invalidateIndexPartitionKey(entry.key);
				continue;
			}
			entry.state.activeMutations -= 1;
			entry.state.mutationEpoch = {};
			this.invalidateIndexPartitionState(entry.state);
		}
		for (const partition of additionalPartitions) {
			this.invalidateIndexPartitionKey(memoryIndexPartitionKey(partition));
		}
	}

	private acquireIndexPartitionState(key: string): MemoryIndexPartitionState | undefined {
		const existing = this.indexPreparation.partitions.get(key);
		if (existing) return existing;
		if (this.indexPreparation.partitions.size >= this.indexPreparation.maxPartitions) {
			const evicted = [...this.indexPreparation.partitions].find(
				([, state]) =>
					state.activeMutations === 0 &&
					state.status !== "preparing" &&
					(state.status !== "failed" || this.indexPreparation.background?.retryFailure !== "never"),
			);
			if (evicted === undefined) return undefined;
			this.indexPreparation.partitions.delete(evicted[0]);
		}
		const state: MemoryIndexPartitionState = {
			activeMutations: 0,
			key,
			mutationEpoch: {},
			preparation: undefined,
			revision: undefined,
			status: "unknown",
		};
		this.indexPreparation.partitions.set(key, state);
		return state;
	}

	private scheduleIndexPreparation(partition: MemoryIndexPartition): MemoryRecallResult["vectorStatus"] {
		const background = this.backgroundIndexPreparation;
		if (!background || background.closed) return "unprepared";
		const key = memoryIndexPartitionKey(partition);
		const state = this.acquireIndexPartitionState(key);
		if (!state) return "unprepared";
		if (state.status === "ready") return "used";
		if (background.jobs.has(key)) return "preparing";
		if (state.preparation) return "preparing";
		if (state.status === "failed" && background.retryFailure === "never") return "preparation-failed";
		if (background.jobs.size >= background.maxPendingPartitions) return "unprepared";
		const pending = Promise.withResolvers<void>();
		const job: MemoryBackgroundIndexPreparationJob = {
			controller: new AbortController(),
			completion: pending.promise,
			key,
			run: (signal) => this.rebuildPartition(partition, signal, true),
			resolve: pending.resolve,
		};
		state.status = "preparing";
		background.jobs.set(key, job);
		background.queue.enqueue(job);
		this.pumpIndexPreparation();
		return "preparing";
	}

	private pumpIndexPreparation(): void {
		const background = this.backgroundIndexPreparation;
		if (!background) return;
		while (background.active < background.maxConcurrentPartitions) {
			const job = background.queue.dequeue();
			if (!job) return;
			background.active += 1;
			setImmediate(() => {
				void job
					.run(job.controller.signal)
					.catch(() => undefined)
					.finally(() => {
						background.active -= 1;
						if (background.jobs.get(job.key) === job) background.jobs.delete(job.key);
						job.resolve();
						this.pumpIndexPreparation();
					});
			});
		}
	}

	private beginOverflowIndexMutation(key?: string): MemoryIndexMutationEntry {
		this.indexPreparation.overflowMutations += 1;
		this.indexPreparation.overflowEpoch = {};
		return { ...(key === undefined ? {} : { key }) };
	}

	private invalidateIndexPartitionKey(key: string): void {
		const state = this.indexPreparation.partitions.get(key);
		if (state) this.invalidateIndexPartitionState(state);
	}

	private invalidateIndexPartitionState(state: MemoryIndexPartitionState): void {
		state.mutationEpoch = {};
		state.preparation = undefined;
		state.revision = undefined;
		state.status = "unknown";
		if (state.activeMutations === 0 && this.indexPreparation.partitions.get(state.key) === state) {
			this.indexPreparation.partitions.delete(state.key);
		}
	}

	private async synchronize(record: MemoryRecord, signal?: AbortSignal): Promise<MemoryMutationResult["indexStatus"]> {
		if (!this.embeddingProvider || !this.vectorIndex) return "not-configured";
		try {
			throwIfAborted(signal);
			const [vector] = snapshotVectorBatch(
				await this.embeddingProvider.embed([record.searchText], signal),
				1,
				this.embeddingProvider.dimensions,
			);
			if (!vector) throw new TypeError("Embedding provider returned no memory vector.");
			const embedding = this.createEmbedding(record, vector);
			if (!(await this.authority.writeEmbedding(embedding))) return "pending";
			await this.vectorIndex.upsert(memoryIndexPartition(record), { memoryKey: record.memoryKey, vector });
			return "ready";
		} catch {
			return "pending";
		}
	}

	private async removeFromIndex(record: MemoryRecord): Promise<boolean> {
		let cleared = true;
		try {
			await this.authority.deleteEmbedding(record.memoryKey);
		} catch {
			cleared = false;
		}
		if (!this.vectorIndex) return cleared;
		try {
			await this.vectorIndex.remove(memoryIndexPartition(record), record.memoryKey);
		} catch {
			cleared = false;
		}
		return cleared;
	}

	private async *rebuildEntries(
		partition: MemoryIndexPartition,
		signal: AbortSignal | undefined,
		onScanned: (count: number) => void,
		onComplete: () => void,
		onIndexed: () => void,
		onEmbedded: () => void,
	): AsyncGenerator<MemoryVectorEntry> {
		if (!this.embeddingProvider) return;
		let after: bigint | undefined;
		for (;;) {
			throwIfAborted(signal);
			const records = await this.authority.scanActive(partition, after, this.limits.maxIndexBatchSize);
			throwIfAborted(signal);
			if (records.length === 0) {
				onComplete();
				return;
			}
			onScanned(records.length);
			const resolved = new Map<bigint, Float32Array>();
			const missing: MemoryRecord[] = [];
			for (const record of records) {
				const existing = await this.authority.readEmbedding(
					record.memoryKey,
					record.revision,
					this.embeddingProvider.modelId,
				);
				throwIfAborted(signal);
				if (
					existing &&
					existing.dimensions === this.embeddingProvider.dimensions &&
					existing.sourceFingerprint === embeddingSourceFingerprint(record.searchText)
				) {
					resolved.set(record.memoryKey, snapshotVector(existing.vector, existing.dimensions));
				} else {
					missing.push(record);
				}
			}
			if (missing.length > 0) {
				for (let offset = 0; offset < missing.length; offset += this.embeddingProvider.maxTextsPerRequest) {
					const batch = missing.slice(offset, offset + this.embeddingProvider.maxTextsPerRequest);
					const embedded = await this.embeddingProvider.embed(
						batch.map((record) => record.searchText),
						signal,
					);
					throwIfAborted(signal);
					const vectors = snapshotVectorBatch(embedded, batch.length, this.embeddingProvider.dimensions);
					for (const [index, record] of batch.entries()) {
						const vector = vectors[index];
						if (!vector) throw new TypeError("Embedding provider omitted a batch vector.");
						const written = await this.authority.writeEmbedding(this.createEmbedding(record, vector));
						throwIfAborted(signal);
						if (!written) continue;
						resolved.set(record.memoryKey, vector);
						onEmbedded();
					}
				}
			}
			for (const record of records) {
				const vector = resolved.get(record.memoryKey);
				if (!vector) continue;
				yield { memoryKey: record.memoryKey, vector };
				throwIfAborted(signal);
				onIndexed();
			}
			after = records.at(-1)?.memoryKey;
		}
	}

	private createEmbedding(record: MemoryRecord, vector: Float32Array): MemoryEmbedding {
		if (!this.embeddingProvider) throw new TypeError("Embedding provider is not configured.");
		const generatedAt = this.now();
		validateTimestamp(generatedAt);
		return {
			memoryKey: record.memoryKey,
			memoryRevision: record.revision,
			modelId: this.embeddingProvider.modelId,
			dimensions: this.embeddingProvider.dimensions,
			vector: new Float32Array(vector),
			sourceFingerprint: embeddingSourceFingerprint(record.searchText),
			generatedAt,
		};
	}

	private async applyReranker(
		query: string,
		hits: readonly MemoryRecallHit[],
		signal?: AbortSignal,
	): Promise<MemoryRecallHit[]> {
		if (!this.reranker) return [...hits];
		const orderedIds = await this.reranker.rerank(
			query,
			hits.map((hit) => hit.record),
			signal,
		);
		throwIfAborted(signal);
		const byId = new Map(hits.map((hit) => [hit.record.memoryId, hit]));
		const seen = new Set<string>();
		const ordered: MemoryRecallHit[] = [];
		for (const memoryId of orderedIds) {
			const hit = byId.get(memoryId);
			if (!hit || seen.has(memoryId)) throw new MemoryRerankerError();
			seen.add(memoryId);
			ordered.push(hit);
		}
		for (const hit of hits) if (!seen.has(hit.record.memoryId)) ordered.push(hit);
		return ordered;
	}
}

function throwIfAborted(signal: AbortSignal | undefined): void {
	if (signal?.aborted) throw signal.reason;
}

function recallStage(
	stage: MemoryRecallStageMetric["stage"],
	outcome: MemoryRecallStageMetric["outcome"],
	startedAt: number,
	inputCount: number,
	outputCount: number,
): MemoryRecallStageMetric {
	return { stage, outcome, durationMs: performance.now() - startedAt, inputCount, outputCount };
}

function embeddingSourceFingerprint(searchText: string): string {
	return createHash("sha256").update(searchText).digest("hex");
}

function memoryIndexPartitionKey(partition: MemoryIndexPartition): string {
	return JSON.stringify([partition.ownerId, partition.timelineId, partition.generation]);
}

function revisionIndexPartitions(record: MemoryRecord, patch: MemoryRevisionPatch): readonly MemoryIndexPartition[] {
	const current = memoryIndexPartition(record);
	const gameTime = patch.gameTime;
	if (
		gameTime === undefined ||
		(gameTime.timelineId === current.timelineId && gameTime.generation === current.generation)
	) {
		return [current];
	}
	return [current, { ownerId: current.ownerId, timelineId: gameTime.timelineId, generation: gameTime.generation }];
}

function snapshotVector(source: Float32Array, dimensions: number): Float32Array {
	if (!(source instanceof Float32Array)) throw new TypeError("Embedding provider returned an invalid vector.");
	const vector = new Float32Array(source);
	validateVector(vector, dimensions);
	return vector;
}

function snapshotVectorBatch(
	sources: readonly Float32Array[],
	expectedLength: number,
	dimensions: number,
): Float32Array[] {
	if (!Array.isArray(sources) || sources.length !== expectedLength) {
		throw new TypeError("Embedding batch cardinality mismatch.");
	}
	const vectors: Float32Array[] = [];
	for (let index = 0; index < expectedLength; index += 1) {
		const source = sources[index];
		if (!source) throw new TypeError("Embedding provider omitted a batch vector.");
		vectors.push(snapshotVector(source, dimensions));
	}
	return vectors;
}

function captureAuthority(authority: MemoryAuthority): MemoryAuthority {
	const readPartitionState = authority.readPartitionState.bind(authority);
	return {
		limits: Object.freeze(validateMemoryLimits(authority.limits)),
		readPartitionState: async (selector) => {
			const { revision, activeCount } = await readPartitionState(selector);
			assertOpaqueIdentifier(revision);
			if (!Number.isSafeInteger(activeCount) || activeCount < 0) throw new TypeError("Invalid memory partition count.");
			return { revision, activeCount };
		},
		remember: authority.remember.bind(authority),
		revise: authority.revise.bind(authority),
		forget: authority.forget.bind(authority),
		get: authority.get.bind(authority),
		searchLexical: authority.searchLexical.bind(authority),
		hydrate: authority.hydrate.bind(authority),
		scanActive: authority.scanActive.bind(authority),
		readEmbedding: authority.readEmbedding.bind(authority),
		writeEmbedding: authority.writeEmbedding.bind(authority),
		deleteEmbedding: authority.deleteEmbedding.bind(authority),
		exportPage: authority.exportPage.bind(authority),
		purgeForgotten: authority.purgeForgotten.bind(authority),
	};
}

function captureEmbeddingProvider(provider: MemoryEmbeddingProvider): MemoryEmbeddingProvider {
	return {
		modelId: provider.modelId,
		dimensions: provider.dimensions,
		maxTextsPerRequest: provider.maxTextsPerRequest,
		embed: provider.embed.bind(provider),
	};
}

function captureIndexPreparationTracker(
	index: MemoryVectorIndex,
	authority: MemoryAuthority,
	maxPartitions: number,
): MemoryIndexPreparationTracker {
	const existing = INDEX_PREPARATION_BY_VECTOR_INDEX.get(index);
	if (existing) {
		if (existing.authority !== authority) {
			throw new TypeError("GameMemory instances sharing a vector index must use the same memory authority.");
		}
		if (existing.maxPartitions !== maxPartitions) {
			throw new TypeError("GameMemory instances sharing a vector index must use the same partition tracking capacity.");
		}
		return existing;
	}
	const created = createIndexPreparationTracker(authority, maxPartitions);
	INDEX_PREPARATION_BY_VECTOR_INDEX.set(index, created);
	return created;
}

function createIndexPreparationTracker(
	authority: MemoryAuthority,
	maxPartitions: number,
): MemoryIndexPreparationTracker {
	return {
		authority,
		background: undefined,
		maxPartitions,
		mode: "unset",
		overflowEpoch: {},
		overflowMutations: 0,
		partitions: new Map(),
	};
}

function captureBackgroundIndexPreparation(
	tracker: MemoryIndexPreparationTracker,
	options: Readonly<MemoryIndexPreparationOptions> | undefined,
): MemoryBackgroundIndexPreparation | undefined {
	if (options === undefined) {
		if (tracker.mode === "background") {
			throw new TypeError("GameMemory instances sharing a vector index must use the same preparation mode.");
		}
		tracker.mode = "foreground";
		return undefined;
	}
	const maxPendingPartitions = options.maxPendingPartitions;
	const maxConcurrentPartitions = options.maxConcurrentPartitions;
	const retryFailure = options.retryFailure;
	if (
		!Number.isSafeInteger(maxPendingPartitions) ||
		maxPendingPartitions < 1 ||
		!Number.isSafeInteger(maxConcurrentPartitions) ||
		maxConcurrentPartitions < 1 ||
		maxConcurrentPartitions > maxPendingPartitions ||
		(retryFailure !== "never" && retryFailure !== "next-recall")
	) {
		throw new TypeError("Invalid memory index preparation configuration.");
	}
	if (tracker.mode === "foreground") {
		throw new TypeError("GameMemory instances sharing a vector index must use the same preparation mode.");
	}
	tracker.mode = "background";
	const existing = tracker.background;
	if (existing) {
		if (existing.closed) throw new TypeError("Memory index preparation is closing.");
		if (
			existing.maxPendingPartitions !== maxPendingPartitions ||
			existing.maxConcurrentPartitions !== maxConcurrentPartitions ||
			existing.retryFailure !== retryFailure
		) {
			throw new TypeError("GameMemory instances sharing a vector index must use the same preparation configuration.");
		}
		return existing;
	}
	const created: MemoryBackgroundIndexPreparation = {
		active: 0,
		closed: false,
		explicit: new Set(),
		jobs: new Map(),
		maxConcurrentPartitions,
		maxPendingPartitions,
		queue: new Queue<MemoryBackgroundIndexPreparationJob>(),
		retryFailure,
	};
	tracker.background = created;
	return created;
}

function captureVectorIndex(index: MemoryVectorIndex): MemoryVectorIndex {
	return {
		modelId: index.modelId,
		dimensions: index.dimensions,
		upsert: index.upsert.bind(index),
		remove: index.remove.bind(index),
		search: index.search.bind(index),
		replace: index.replace.bind(index),
		...(index.restore === undefined ? {} : { restore: index.restore.bind(index) }),
		...(index.checkpoint === undefined ? {} : { checkpoint: index.checkpoint.bind(index) }),
	};
}

function captureReranker(reranker: MemoryReranker): MemoryReranker {
	return { rerank: reranker.rerank.bind(reranker) };
}

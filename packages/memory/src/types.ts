import type { Audience, GameTime, JsonValue } from "@opengameagent/protocol";

export type MemoryGameTime = GameTime;

export interface MemoryPartition {
	readonly ownerId: string;
	readonly actorId?: string;
	readonly sessionId?: string;
	readonly scope: string;
}

export interface MemoryIndexPartition {
	readonly ownerId: string;
	readonly timelineId: string;
	readonly generation: string;
}

export type MemoryLifecycle = "active" | "forgotten";

export interface MemoryDraft {
	readonly memoryId: string;
	readonly partition: MemoryPartition;
	readonly kind: string;
	readonly tags: readonly string[];
	readonly gameTime: MemoryGameTime;
	readonly importance: number;
	readonly searchText: string;
	readonly content: JsonValue;
	readonly audience: Audience;
}

export interface MemoryWrite extends MemoryDraft {
	readonly recordedAt: string;
}

export interface MemoryRevisionPatch {
	readonly tags?: readonly string[];
	readonly gameTime?: MemoryGameTime;
	readonly importance?: number;
	readonly searchText?: string;
	readonly content?: JsonValue;
	readonly audience?: Audience;
}

export interface MemoryRecord extends MemoryDraft {
	readonly memoryKey: bigint;
	readonly revision: string;
	readonly lifecycle: MemoryLifecycle;
	readonly createdAt: string;
	readonly updatedAt: string;
	readonly forgottenAt?: string;
}

export type MemoryViewer =
	| { readonly kind: "internal" }
	| { readonly kind: "owner"; readonly ownerId: string }
	| { readonly kind: "recipient"; readonly recipientId: string }
	| { readonly kind: "public" };

export interface MemorySelector extends MemoryIndexPartition {
	readonly actorId?: string;
	readonly sessionId?: string;
	readonly scope?: string;
	readonly kinds?: readonly string[];
	readonly tags?: readonly string[];
	readonly atOrBeforeTick?: string;
}

export interface MemoryLimits {
	readonly maxSearchTextBytes: number;
	readonly maxContentBytes: number;
	readonly maxTagsPerMemory: number;
	readonly maxTagBytes: number;
	readonly maxRecipientsPerMemory: number;
	readonly maxQueryTextBytes: number;
	readonly maxSelectorKinds: number;
	readonly maxSelectorTags: number;
	readonly maxLexicalCandidates: number;
	readonly maxVectorCandidates: number;
	readonly maxHydrateCandidates: number;
	readonly maxRecallResults: number;
	readonly maxIndexBatchSize: number;
	readonly maxTrackedIndexPartitions: number;
	readonly maxExportPageSize: number;
	readonly maxPurgeBatchSize: number;
}

export interface RankedMemoryCandidate {
	readonly memoryKey: bigint;
	readonly score: number;
}

export interface MemoryEmbedding {
	readonly memoryKey: bigint;
	readonly memoryRevision: string;
	readonly modelId: string;
	readonly dimensions: number;
	readonly vector: Float32Array;
	readonly sourceFingerprint: string;
	readonly generatedAt: string;
}

export interface MemoryExportPage {
	readonly records: readonly MemoryRecord[];
	readonly nextAfterMemoryKey?: bigint;
}

export interface MemoryPartitionState {
	/** Durable revision of the owner/timeline/generation index partition. */
	readonly revision: string;
	/** Active count in the selected actor/session/scope lane; other recall filters are not applied. */
	readonly activeCount: number;
}

export interface MemoryAuthority {
	readonly limits: MemoryLimits;
	readPartitionState(selector: MemorySelector): Promise<MemoryPartitionState>;
	remember(write: MemoryWrite): Promise<MemoryRecord>;
	revise(
		memoryId: string,
		expectedRevision: string,
		patch: MemoryRevisionPatch,
		revisedAt: string,
	): Promise<MemoryRecord>;
	forget(memoryId: string, expectedRevision: string, forgottenAt: string): Promise<MemoryRecord>;
	get(memoryId: string, viewer: MemoryViewer): Promise<MemoryRecord | undefined>;
	searchLexical(
		selector: MemorySelector,
		viewer: MemoryViewer,
		query: string,
		limit: number,
	): Promise<readonly RankedMemoryCandidate[]>;
	hydrate(
		memoryKeys: readonly bigint[],
		selector: MemorySelector,
		viewer: MemoryViewer,
	): Promise<readonly MemoryRecord[]>;
	scanActive(
		partition: MemoryIndexPartition,
		afterMemoryKey: bigint | undefined,
		limit: number,
	): Promise<readonly MemoryRecord[]>;
	readEmbedding(memoryKey: bigint, memoryRevision: string, modelId: string): Promise<MemoryEmbedding | undefined>;
	writeEmbedding(embedding: MemoryEmbedding): Promise<boolean>;
	deleteEmbedding(memoryKey: bigint): Promise<void>;
	exportPage(afterMemoryKey: bigint | undefined, limit: number): Promise<MemoryExportPage>;
	purgeForgotten(before: string, limit: number): Promise<readonly bigint[]>;
}

export interface MemoryEmbeddingProvider {
	readonly modelId: string;
	readonly dimensions: number;
	readonly maxTextsPerRequest: number;
	embed(texts: readonly string[], signal?: AbortSignal): Promise<readonly Float32Array[]>;
}

export interface MemoryVectorEntry {
	readonly memoryKey: bigint;
	readonly vector: Float32Array;
}

export interface MemoryVectorIndex {
	readonly modelId: string;
	readonly dimensions: number;
	upsert(partition: MemoryIndexPartition, entry: MemoryVectorEntry): Promise<void>;
	remove(partition: MemoryIndexPartition, memoryKey: bigint): Promise<void>;
	search(
		partition: MemoryIndexPartition,
		vector: Float32Array,
		limit: number,
	): Promise<readonly RankedMemoryCandidate[]>;
	replace(partition: MemoryIndexPartition, entries: AsyncIterable<MemoryVectorEntry>, revision?: string): Promise<void>;
	/** Optional durable derived-index capability. Revision must come from the authority. */
	restore?(partition: MemoryIndexPartition, revision: string): Promise<boolean>;
	checkpoint?(partition: MemoryIndexPartition, revision: string): Promise<boolean>;
}

export interface MemoryReranker {
	rerank(query: string, records: readonly MemoryRecord[], signal?: AbortSignal): Promise<readonly string[]>;
}

export interface MemoryRecallRequest {
	readonly query: string;
	readonly selector: MemorySelector;
	readonly viewer: MemoryViewer;
	readonly resultLimit: number;
	readonly vectorFailure: "fail" | "lexical-only";
	readonly signal?: AbortSignal;
}

export interface MemoryRecallHit {
	readonly record: MemoryRecord;
	readonly score: number;
}

export interface MemoryRecallStageMetric {
	readonly stage: "partition" | "lexical" | "index-restore" | "embedding" | "vector" | "fusion" | "hydrate" | "rerank";
	readonly outcome: "ok" | "failed-open";
	readonly durationMs: number;
	readonly inputCount: number;
	readonly outputCount: number;
}

export interface MemoryRecallResult {
	readonly hits: readonly MemoryRecallHit[];
	readonly vectorStatus:
		| "not-configured"
		| "unprepared"
		| "preparing"
		| "preparation-failed"
		| "proven-empty"
		| "used"
		| "failed-open";
	readonly stages: readonly MemoryRecallStageMetric[];
}

export interface MemoryIndexPreparationOptions {
	readonly maxPendingPartitions: number;
	readonly maxConcurrentPartitions: number;
	readonly retryFailure: "never" | "next-recall";
}

export type MemoryIndexPreparationCloseMode = "drain" | "abort";

export interface MemoryMutationResult {
	readonly record: MemoryRecord;
	readonly indexStatus: "not-configured" | "ready" | "pending";
}

export interface MemoryRebuildResult {
	readonly indexed: number;
	readonly embedded: number;
	readonly restored: number;
}

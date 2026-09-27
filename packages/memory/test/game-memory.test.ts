import { Buffer } from "node:buffer";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { UsearchMemoryIndex } from "@opengameagent/memory-usearch";
import { SqliteAuthority, SqliteMemoryAuthority } from "@opengameagent/storage-sqlite";
import { describe, expect, it, vi } from "vitest";
import {
	GameMemory,
	type MemoryEmbedding,
	type MemoryEmbeddingProvider,
	type MemoryIndexPartition,
	type MemoryLimits,
	MemoryRecallUnavailableError,
	type MemoryVectorEntry,
	type MemoryVectorIndex,
	type RankedMemoryCandidate,
} from "../src/index.js";

const limits: MemoryLimits = {
	maxSearchTextBytes: 4_096,
	maxContentBytes: 16_384,
	maxTagsPerMemory: 16,
	maxTagBytes: 256,
	maxRecipientsPerMemory: 16,
	maxQueryTextBytes: 1_024,
	maxSelectorKinds: 8,
	maxSelectorTags: 8,
	maxLexicalCandidates: 16,
	maxVectorCandidates: 16,
	maxHydrateCandidates: 24,
	maxRecallResults: 8,
	maxIndexBatchSize: 8,
	maxTrackedIndexPartitions: 16,
	maxExportPageSize: 16,
	maxPurgeBatchSize: 16,
};

function testUsearchIndex(embedding: Pick<MemoryEmbeddingProvider, "modelId" | "dimensions">): UsearchMemoryIndex {
	return new UsearchMemoryIndex({
		modelId: embedding.modelId,
		dimensions: embedding.dimensions,
		connectivity: 0,
		expansionAdd: 0,
		expansionSearch: 0,
	});
}

describe("GameMemory", () => {
	it("restores a stable persisted index without rescanning or embedding and rebuilds only a changed partition", async () => {
		const root = await mkdtemp(join(tmpdir(), "opengameagent-memory-checkpoint-test-"));
		const path = join(root, "memory.sqlite3");
		let authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const embedder = new SemanticFakeEmbedding();
			const createIndex = () =>
				new UsearchMemoryIndex({
					modelId: embedder.modelId,
					dimensions: embedder.dimensions,
					connectivity: 0,
					expansionAdd: 0,
					expansionSearch: 0,
					persistence: { directory: join(root, "index"), maxSnapshotBytes: 1024 * 1024 },
				});
			let store = new SqliteMemoryAuthority(authority, limits);
			const seed = new GameMemory({ authority: store, now: clock() });
			await seed.remember(draft("first", "moon herb", "1"));
			await seed.remember(draft("second", "blacksmith", "2"));
			const first = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: createIndex() });
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" };
			await first.rebuild(partition);
			await first.rebuild({ ...partition, generation: "2" });
			authority.close();
			authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
			store = new SqliteMemoryAuthority(authority, limits);
			const scan = vi.spyOn(store, "scanActive");
			const resumed = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: createIndex() });
			embedder.batchSizes.length = 0;
			const empty = await resumed.recall({
				query: "moon herb",
				selector: { ...partition, actorId: "new-actor", sessionId: "new-session" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});
			expect(empty).toMatchObject({ hits: [], vectorStatus: "proven-empty" });
			expect(stripStageDurations(empty.stages)).toEqual([
				{ stage: "partition", outcome: "ok", inputCount: 1, outputCount: 0 },
			]);
			expect(await resumed.rebuild(partition)).toEqual({ indexed: 0, embedded: 0, restored: 1 });
			expect(scan).not.toHaveBeenCalled();
			expect(embedder.batchSizes).toEqual([]);
			await store.revise("second", "1", { searchText: "new lunar remedy" }, "2026-09-01T00:00:00.000Z");
			expect(await resumed.rebuild(partition)).toEqual({ indexed: 0, embedded: 0, restored: 1 });
			expect(scan).not.toHaveBeenCalled();
			expect(await resumed.rebuild({ ...partition, generation: "2" })).toEqual({
				indexed: 1,
				embedded: 1,
				restored: 0,
			});
			expect(scan.mock.calls.every(([value]) => value.generation === "2")).toBe(true);
			expect(embedder.batchSizes).toEqual([1]);
		} finally {
			authority.close();
			await removeTemporaryRoot(root);
		}
	});

	it.each(["actorId", "sessionId", "scope"] as const)(
		"prepares and restores the whole index partition after a first recall filtered by %s",
		async (lane) => {
			const root = await mkdtemp(join(tmpdir(), "opengameagent-memory-lane-index-test-"));
			const authority = new SqliteAuthority({
				path: join(root, "memory.sqlite3"),
				busyTimeoutMs: 0,
				verifyOnOpen: "full",
			});
			let memory: GameMemory | undefined;
			try {
				const store = new SqliteMemoryAuthority(authority, limits);
				const seed = new GameMemory({ authority: store, now: clock() });
				const firstDraft = draft("first", "moon herb", "1");
				await seed.remember(firstDraft);
				await seed.remember({
					...draft("second", "moon medicine", "1"),
					partition: { ...firstDraft.partition, [lane]: "other" },
				});
				await seed.remember(draft("another-generation", "moon herb", "2"));
				const partition = { ownerId: "owner", timelineId: "world", generation: "1" };
				const selector = { ...partition, [lane]: firstDraft.partition[lane] };
				expect((await store.readPartitionState(selector)).activeCount).toBe(1);
				expect((await store.readPartitionState(partition)).activeCount).toBe(2);
				const embedder = new SemanticFakeEmbedding();
				const scan = vi.spyOn(store, "scanActive");
				const createMemory = () =>
					new GameMemory({
						authority: store,
						embeddingProvider: embedder,
						vectorIndex: new UsearchMemoryIndex({
							modelId: embedder.modelId,
							dimensions: embedder.dimensions,
							connectivity: 0,
							expansionAdd: 0,
							expansionSearch: 0,
							persistence: { directory: join(root, "index"), maxSnapshotBytes: 1024 * 1024 },
						}),
						indexPreparation: { maxPendingPartitions: 1, maxConcurrentPartitions: 1, retryFailure: "never" },
					});
				memory = createMemory();
				const request = {
					query: "moon herb",
					selector,
					viewer: { kind: "owner", ownerId: "owner" } as const,
					resultLimit: 2,
					vectorFailure: "fail" as const,
				};
				expect((await memory.recall(request)).vectorStatus).toBe("preparing");
				await vi.waitFor(async () => {
					const result = await memory?.recall(request);
					expect(result?.vectorStatus).toBe("used");
					expect(result?.hits.map((hit) => hit.record.memoryId)).toEqual(["first"]);
				});
				expect(scan).toHaveBeenCalled();
				for (const [scannedPartition] of scan.mock.calls) expect(scannedPartition).toEqual(partition);
				expect(embedder.batchSizes[0]).toBe(2);
				await memory.closeIndexPreparation("drain");
				scan.mockClear();
				embedder.batchSizes.length = 0;
				memory = createMemory();
				expect(await memory.recall(request)).toMatchObject({
					vectorStatus: "used",
					hits: [{ record: { memoryId: "first" } }],
				});
				expect(scan).not.toHaveBeenCalled();
				expect(embedder.batchSizes).toEqual([1]);
			} finally {
				await memory?.closeIndexPreparation("drain");
				authority.close();
				await removeTemporaryRoot(root);
			}
		},
	);

	it("short-circuits a new actor/session lane before any recall or index preparation work", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember(draft("existing", "moon herb", "1"));
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const scan = vi.spyOn(store, "scanActive");
			const lexical = vi.spyOn(store, "searchLexical");
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: { maxPendingPartitions: 1, maxConcurrentPartitions: 1, retryFailure: "never" },
			});
			const result = await memory.recall({
				query: "moon herb",
				selector: {
					ownerId: "owner",
					timelineId: "world",
					generation: "1",
					actorId: "new-npc",
					sessionId: "new-session",
				},
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});
			await memory.closeIndexPreparation("drain");
			expect(result).toMatchObject({ hits: [], vectorStatus: "proven-empty" });
			expect(stripStageDurations(result.stages)).toEqual([
				{ stage: "partition", outcome: "ok", inputCount: 1, outputCount: 0 },
			]);
			expect(embedder.batchSizes).toEqual([]);
			expect(scan).not.toHaveBeenCalled();
			expect(lexical).not.toHaveBeenCalled();
			expect(search).not.toHaveBeenCalled();
		} finally {
			authority.close();
		}
	});

	it("rejects background index preparation without a vector pipeline", () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			expect(
				() =>
					new GameMemory({
						authority: new SqliteMemoryAuthority(authority, limits),
						indexPreparation: {
							maxPendingPartitions: 1,
							maxConcurrentPartitions: 1,
							retryFailure: "never",
						},
					}),
			).toThrow("Embedding provider and vector index must be configured for background index preparation.");
		} finally {
			authority.close();
		}
	});

	it("rejects mixed preparation modes for GameMemory instances sharing one vector index", () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});

			expect(() => new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index })).toThrow(
				"GameMemory instances sharing a vector index must use the same preparation mode.",
			);
		} finally {
			authority.close();
		}
	});

	it("rejects different memory authorities sharing one vector index", () => {
		const firstAuthority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		const secondAuthority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const indexPreparation = {
				maxPendingPartitions: 1,
				maxConcurrentPartitions: 1,
				retryFailure: "never" as const,
			};
			new GameMemory({
				authority: new SqliteMemoryAuthority(firstAuthority, limits),
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation,
			});

			expect(
				() =>
					new GameMemory({
						authority: new SqliteMemoryAuthority(secondAuthority, limits),
						embeddingProvider: embedder,
						vectorIndex: index,
						indexPreparation,
					}),
			).toThrow("GameMemory instances sharing a vector index must use the same memory authority.");
		} finally {
			firstAuthority.close();
			secondAuthority.close();
		}
	});

	it("recalls semantically, rebuilds a disposable index, and rejects stale generations", async () => {
		const root = await mkdtemp(join(tmpdir(), "opengameagent-memory-restart-test-"));
		const path = join(root, "memory.sqlite3");
		let authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const embedder = new SemanticFakeEmbedding();
			let store = new SqliteMemoryAuthority(authority, limits);
			const firstIndex = testUsearchIndex(embedder);
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: firstIndex,
				now: clock(),
			});
			await memory.remember(draft("herb", "moon herb cures fever", "1"));
			await memory.remember(draft("forge", "the blacksmith repairs swords", "1"));
			await memory.remember(draft("rewound", "moon herb from a discarded future", "2"));

			const request = {
				query: "lunar medicine",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 2,
				vectorFailure: "fail",
			} as const;
			const beforeRestart = await memory.recall(request);
			expect(beforeRestart.vectorStatus).toBe("used");
			expect(beforeRestart.hits[0]?.record.memoryId).toBe("herb");
			expect(beforeRestart.hits.map((hit) => hit.record.memoryId)).not.toContain("rewound");

			authority.close();
			authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
			store = new SqliteMemoryAuthority(authority, limits);
			embedder.batchSizes.length = 0;
			const rebuiltIndex = testUsearchIndex(embedder);
			const restarted = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: rebuiltIndex,
				now: clock(),
			});
			expect(await restarted.rebuild(request.selector)).toEqual({ indexed: 2, embedded: 0, restored: 0 });
			expect(embedder.batchSizes).toEqual([]);
			const afterRestart = await restarted.recall(request);
			expect(afterRestart.hits.map((hit) => hit.record.memoryId)).toEqual(
				beforeRestart.hits.map((hit) => hit.record.memoryId),
			);
			expect(afterRestart.hits.map((hit) => hit.record.memoryId)).not.toContain("rewound");

			authority.close();
			authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
			store = new SqliteMemoryAuthority(authority, limits);
			embedder.batchSizes.length = 0;
			const prepared = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: testUsearchIndex(embedder),
				now: clock(),
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});
			await expect(prepared.recall(request)).resolves.toMatchObject({ vectorStatus: "preparing" });
			expect(embedder.batchSizes).toEqual([]);
			await prepared.closeIndexPreparation("drain");
			const afterBackgroundPreparation = await prepared.recall(request);
			expect(afterBackgroundPreparation.vectorStatus).toBe("used");
			expect(afterBackgroundPreparation.hits.map((hit) => hit.record.memoryId)).toEqual(
				beforeRestart.hits.map((hit) => hit.record.memoryId),
			);
			expect(embedder.batchSizes).toEqual([1]);
		} finally {
			authority.close();
			await removeTemporaryRoot(root);
		}
	});

	it("reports ordered content-free metrics for every completed recall stage", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: testUsearchIndex(embedder),
				reranker: {
					rerank: async (_query, records) => records.toReversed().map((record) => record.memoryId),
				},
				now: clock(),
			});
			await memory.remember(draft("first", "moon herb remedy", "1"));
			await memory.remember(draft("second", "moon herb journal", "1"));

			const result = await memory.recall({
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(stripStageDurations(result.stages)).toEqual([
				{ stage: "partition", outcome: "ok", inputCount: 1, outputCount: 2 },
				{ stage: "lexical", outcome: "ok", inputCount: 1, outputCount: 2 },
				{ stage: "index-restore", outcome: "ok", inputCount: 1, outputCount: 0 },
				{ stage: "embedding", outcome: "ok", inputCount: 1, outputCount: 1 },
				{ stage: "vector", outcome: "ok", inputCount: 1, outputCount: 2 },
				{ stage: "fusion", outcome: "ok", inputCount: 4, outputCount: 2 },
				{ stage: "hydrate", outcome: "ok", inputCount: 2, outputCount: 2 },
				{ stage: "rerank", outcome: "ok", inputCount: 2, outputCount: 2 },
			]);
			expect(result.hits).toHaveLength(1);
		} finally {
			authority.close();
		}
	});

	it("omits optional vector and reranker metrics when those stages do not run", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const memory = new GameMemory({ authority: store, now: clock() });
			await memory.remember(draft("lexical", "moon herb remedy", "1"));
			const result = await memory.recall({
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(stripStageDurations(result.stages)).toEqual([
				{ stage: "partition", outcome: "ok", inputCount: 1, outputCount: 1 },
				{ stage: "lexical", outcome: "ok", inputCount: 1, outputCount: 1 },
				{ stage: "fusion", outcome: "ok", inputCount: 1, outputCount: 1 },
				{ stage: "hydrate", outcome: "ok", inputCount: 1, outputCount: 1 },
			]);
		} finally {
			authority.close();
		}
	});

	it("makes vector failure policy explicit", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: testUsearchIndex(embedder),
				now: clock(),
			});
			await memory.remember(draft("herb", "moon herb cures fever", "1"));
			const base = {
				query: "embedding-failure",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 2,
			} as const;
			await expect(memory.recall({ ...base, vectorFailure: "fail" })).rejects.toBeInstanceOf(
				MemoryRecallUnavailableError,
			);
			const failedOpen = await memory.recall({ ...base, vectorFailure: "lexical-only" });
			expect(failedOpen.vectorStatus).toBe("failed-open");
			expect(stripStageDurations(failedOpen.stages)).toEqual([
				{ stage: "partition", outcome: "ok", inputCount: 1, outputCount: 1 },
				{ stage: "lexical", outcome: "ok", inputCount: 1, outputCount: 0 },
				{ stage: "index-restore", outcome: "ok", inputCount: 1, outputCount: 0 },
				{ stage: "embedding", outcome: "failed-open", inputCount: 1, outputCount: 0 },
				{ stage: "fusion", outcome: "ok", inputCount: 0, outputCount: 0 },
				{ stage: "hydrate", outcome: "ok", inputCount: 0, outputCount: 0 },
			]);
		} finally {
			authority.close();
		}
	});

	it("attributes lexical-only degradation to vector search after a successful embedding", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			vi.spyOn(index, "search").mockRejectedValue(new Error("vector unavailable"));
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				now: clock(),
			});
			await memory.remember(draft("herb", "moon herb remedy", "1"));

			const result = await memory.recall({
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "lexical-only",
			});
			expect(result.vectorStatus).toBe("failed-open");
			expect(stripStageDurations(result.stages)).toEqual([
				{ stage: "partition", outcome: "ok", inputCount: 1, outputCount: 1 },
				{ stage: "lexical", outcome: "ok", inputCount: 1, outputCount: 1 },
				{ stage: "embedding", outcome: "ok", inputCount: 1, outputCount: 1 },
				{ stage: "vector", outcome: "failed-open", inputCount: 1, outputCount: 0 },
				{ stage: "fusion", outcome: "ok", inputCount: 1, outputCount: 1 },
				{ stage: "hydrate", outcome: "ok", inputCount: 1, outputCount: 1 },
			]);
		} finally {
			authority.close();
		}
	});

	it("never converts cancellation into lexical-only recall", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember(draft("seed", "moon herb", "1"));
			const controller = new AbortController();
			const embedder: MemoryEmbeddingProvider = {
				modelId: "cancelling-embedder",
				dimensions: 2,
				maxTextsPerRequest: 2,
				embed: async () => {
					controller.abort(new Error("cancelled"));
					throw new Error("provider stopped");
				},
			};
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: testUsearchIndex(embedder),
			});
			await expect(
				memory.recall({
					query: "query",
					selector: { ownerId: "owner", timelineId: "world", generation: "1" },
					viewer: { kind: "owner", ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "lexical-only",
					signal: controller.signal,
				}),
			).rejects.toThrow("cancelled");
		} finally {
			authority.close();
		}
	});

	it.each(["partition", "lexical", "index-restore", "embedding", "vector", "hydrate", "rerank"] as const)(
		"preserves exact cancellation after %s recall work returns",
		async (stage) => {
			const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
			try {
				const store = new SqliteMemoryAuthority(authority, limits);
				const seed = new GameMemory({ authority: store, now: clock() });
				await seed.remember(draft("first", "moon herb remedy", "1"));
				await seed.remember(draft("second", "moon herb journal", "1"));

				const controller = new AbortController();
				const reason = new Error(`${stage} cancelled`);
				const abortAfter = <Value>(value: Value): Value => {
					controller.abort(reason);
					return value;
				};
				if (stage === "partition") {
					const read = store.readPartitionState.bind(store);
					vi.spyOn(store, "readPartitionState").mockImplementation(async (...args) => abortAfter(await read(...args)));
				}
				const lexical = store.searchLexical.bind(store);
				if (stage === "lexical") {
					vi.spyOn(store, "searchLexical").mockImplementation(async (...args) => abortAfter(await lexical(...args)));
				}
				const hydrate = store.hydrate.bind(store);
				if (stage === "hydrate") {
					vi.spyOn(store, "hydrate").mockImplementation(async (...args) => abortAfter(await hydrate(...args)));
				}

				const baseEmbedder = new SemanticFakeEmbedding();
				const embedder: MemoryEmbeddingProvider = {
					modelId: baseEmbedder.modelId,
					dimensions: baseEmbedder.dimensions,
					maxTextsPerRequest: baseEmbedder.maxTextsPerRequest,
					embed: async (texts) => {
						const vectors = await baseEmbedder.embed(texts);
						return stage === "embedding" ? abortAfter(vectors) : vectors;
					},
				};
				const index = testUsearchIndex(embedder);
				if (stage === "index-restore") {
					const restore = index.restore.bind(index);
					vi.spyOn(index, "restore").mockImplementation(async (...args) => abortAfter(await restore(...args)));
				}
				if (stage === "vector") {
					const search = index.search.bind(index);
					vi.spyOn(index, "search").mockImplementation(async (...args) => abortAfter(await search(...args)));
				}
				const memory = new GameMemory({
					authority: store,
					embeddingProvider: embedder,
					vectorIndex: index,
					reranker: {
						rerank: async (_query, records) =>
							stage === "rerank"
								? abortAfter([records[0]?.memoryId ?? "missing", records[0]?.memoryId ?? "missing"])
								: records.map((record) => record.memoryId),
					},
				});

				await expect(
					memory.recall({
						query: "moon herb",
						selector: { ownerId: "owner", timelineId: "world", generation: "1" },
						viewer: { kind: "owner", ownerId: "owner" },
						resultLimit: 2,
						vectorFailure: "lexical-only",
						signal: controller.signal,
					}),
				).rejects.toBe(reason);
			} finally {
				authority.close();
			}
		},
	);

	it("returns a committed revision as pending when derived cleanup fails", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new DeleteFailingSqliteMemoryAuthority(authority, limits);
			const memory = new GameMemory({ authority: store, now: clock() });
			const remembered = await memory.remember(draft("commit", "original memory", "1"));
			const revised = await memory.revise("commit", remembered.record.revision, { searchText: "revised memory" });

			expect(revised).toMatchObject({
				record: { revision: "2", searchText: "revised memory" },
				indexStatus: "pending",
			});
			await expect(memory.revise("commit", "1", { searchText: "stale retry" })).rejects.toThrow();
			expect(await store.get("commit", { kind: "owner", ownerId: "owner" })).toMatchObject({
				revision: "2",
				searchText: "revised memory",
			});
		} finally {
			authority.close();
		}
	});

	it("chunks index rebuilds by the embedding provider capability", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding(1);
			const initial = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: testUsearchIndex(embedder),
				now: clock(),
			});
			const first = await initial.remember(draft("first", "moon herb", "1"));
			const second = await initial.remember(draft("second", "blacksmith", "1"));
			await store.deleteEmbedding(first.record.memoryKey);
			await store.deleteEmbedding(second.record.memoryKey);
			embedder.batchSizes.length = 0;

			const rebuilt = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: testUsearchIndex(embedder),
				now: clock(),
			});
			expect(await rebuilt.rebuild({ ownerId: "owner", timelineId: "world", generation: "1" })).toEqual({
				indexed: 2,
				embedded: 2,
				restored: 0,
			});
			expect(embedder.batchSizes).toEqual([1, 1]);
		} finally {
			authority.close();
		}
	});

	it("bypasses query embedding and vector search after an authoritative empty rebuild", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index });
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;

			await expect(memory.rebuild(partition)).resolves.toEqual({ indexed: 0, embedded: 0, restored: 0 });
			embedder.batchSizes.length = 0;
			const result = await memory.recall({
				query: "moon herb",
				selector: partition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(embedder.batchSizes).toEqual([]);
			expect(search).not.toHaveBeenCalled();
			expect(result.vectorStatus).toBe("proven-empty");
			expect(stripStageDurations(result.stages)).toEqual([
				{ stage: "partition", outcome: "ok", inputCount: 1, outputCount: 0 },
			]);
		} finally {
			authority.close();
		}
	});

	it("proves a cold empty partition without preparing its index", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index });

			const result = await memory.recall({
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(result.vectorStatus).toBe("proven-empty");
			expect(embedder.batchSizes).toEqual([]);
			expect(search).not.toHaveBeenCalled();
		} finally {
			authority.close();
		}
	});

	it("proves a cold empty partition without scheduling background preparation", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const scanStarted = Promise.withResolvers<void>();
			const releaseScan = Promise.withResolvers<void>();
			vi.spyOn(store, "scanActive").mockImplementation(async () => {
				scanStarted.resolve();
				await releaseScan.promise;
				return [];
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});
			const request = {
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			} as const;

			const result = await memory.recall(request);

			expect(result.vectorStatus).toBe("proven-empty");
			expect(embedder.batchSizes).toEqual([]);
			expect(search).not.toHaveBeenCalled();
			expect(stripStageDurations(result.stages)).toEqual([
				{ stage: "partition", outcome: "ok", inputCount: 1, outputCount: 0 },
			]);

			releaseScan.resolve();
			await memory.closeIndexPreparation("drain");
			await expect(memory.recall(request)).resolves.toMatchObject({ vectorStatus: "proven-empty" });
		} finally {
			authority.close();
		}
	});

	it("singleflights a cold active partition while concurrent recalls return lexical hits", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember(draft("herb", "moon herb remedy", "1"));
			const scanActive = store.scanActive.bind(store);
			const scanStarted = Promise.withResolvers<void>();
			const releaseScan = Promise.withResolvers<void>();
			const scan = vi.spyOn(store, "scanActive").mockImplementation(async (...arguments_) => {
				scanStarted.resolve();
				await releaseScan.promise;
				return scanActive(...arguments_);
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});
			const request = {
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			} as const;

			const first = await memory.recall(request);
			await scanStarted.promise;
			const second = await memory.recall(request);

			expect(first).toMatchObject({ vectorStatus: "preparing", hits: [{ record: { memoryId: "herb" } }] });
			expect(second).toMatchObject({ vectorStatus: "preparing", hits: [{ record: { memoryId: "herb" } }] });
			expect(scan).toHaveBeenCalledTimes(1);
			expect(embedder.batchSizes).toEqual([]);
			expect(search).not.toHaveBeenCalled();

			releaseScan.resolve();
			await memory.closeIndexPreparation("drain");
			embedder.batchSizes.length = 0;
			await expect(memory.recall(request)).resolves.toMatchObject({ vectorStatus: "used" });
			expect(embedder.batchSizes).toEqual([1]);
			expect(search).toHaveBeenCalledOnce();
		} finally {
			authority.close();
		}
	});

	it("shares one background preparation across GameMemory instances using the same vector index", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember({
				...draft("seed", "seed fact", "1"),
				gameTime: { timelineId: "world", generation: "1", tick: "10" },
			});
			const scanStarted = Promise.withResolvers<void>();
			const releaseScan = Promise.withResolvers<void>();
			const scan = vi.spyOn(store, "scanActive").mockImplementation(async () => {
				scanStarted.resolve();
				await releaseScan.promise;
				return [];
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const options = {
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never" as const,
				},
			};
			const first = new GameMemory(options);
			const second = new GameMemory(options);
			const request = {
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			} as const;

			await expect(first.recall(request)).resolves.toMatchObject({ vectorStatus: "preparing" });
			await scanStarted.promise;
			await expect(second.recall(request)).resolves.toMatchObject({ vectorStatus: "preparing" });
			expect(scan).toHaveBeenCalledOnce();

			releaseScan.resolve();
			await second.closeIndexPreparation("drain");
		} finally {
			authority.close();
		}
	});

	it("aborts queued preparation and permits a new lifecycle after active work settles", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember({
				...draft("first", "seed fact", "1"),
				gameTime: { timelineId: "world", generation: "1", tick: "10" },
			});
			await new GameMemory({ authority: store, now: clock() }).remember({
				...draft("second", "seed fact", "2"),
				gameTime: { timelineId: "world", generation: "2", tick: "10" },
			});
			const firstScanStarted = Promise.withResolvers<void>();
			const releaseFirstScan = Promise.withResolvers<void>();
			const scannedGenerations: string[] = [];
			vi.spyOn(store, "scanActive").mockImplementation(async (partition) => {
				scannedGenerations.push(partition.generation);
				if (partition.generation === "1") {
					firstScanStarted.resolve();
					await releaseFirstScan.promise;
				}
				return [];
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const options = {
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: {
					maxPendingPartitions: 2,
					maxConcurrentPartitions: 1,
					retryFailure: "never" as const,
				},
			};
			const memory = new GameMemory(options);
			const recall = (generation: string) =>
				memory.recall({
					query: "moon herb",
					selector: { ownerId: "owner", timelineId: "world", generation },
					viewer: { kind: "owner" as const, ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				});

			await expect(recall("1")).resolves.toMatchObject({ vectorStatus: "preparing" });
			await firstScanStarted.promise;
			const blockedRebuild = memory.rebuild({ ownerId: "owner", timelineId: "world", generation: "1" });
			void blockedRebuild.catch(() => undefined);
			await expect(recall("2")).resolves.toMatchObject({ vectorStatus: "preparing" });
			const closing = memory.closeIndexPreparation("abort");
			releaseFirstScan.resolve();
			await closing;
			await expect(blockedRebuild).rejects.toThrow("Memory index preparation is closed.");
			expect(scannedGenerations).toEqual(["1"]);
			await expect(memory.rebuild({ ownerId: "owner", timelineId: "world", generation: "2" })).rejects.toThrow(
				"Memory index preparation is closed.",
			);

			const reopened = new GameMemory(options);
			await expect(
				reopened.recall({
					query: "moon herb",
					selector: { ownerId: "owner", timelineId: "world", generation: "2" },
					viewer: { kind: "owner", ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				}),
			).resolves.toMatchObject({ vectorStatus: "preparing" });
			await reopened.closeIndexPreparation("drain");
			expect(scannedGenerations).toEqual(["1", "2"]);
		} finally {
			authority.close();
		}
	});

	it("serializes an explicit rebuild behind background preparation for the same partition", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember({
				...draft("seed", "seed fact", "1"),
				gameTime: { timelineId: "world", generation: "1", tick: "10" },
			});
			const firstScanStarted = Promise.withResolvers<void>();
			const releaseFirstScan = Promise.withResolvers<void>();
			const secondScanStarted = Promise.withResolvers<void>();
			const releaseSecondScan = Promise.withResolvers<void>();
			let scanCalls = 0;
			vi.spyOn(store, "scanActive").mockImplementation(async () => {
				scanCalls += 1;
				if (scanCalls === 1) {
					firstScanStarted.resolve();
					await releaseFirstScan.promise;
				} else {
					secondScanStarted.resolve();
					await releaseSecondScan.promise;
				}
				return [];
			});
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: new SemanticFakeEmbedding(),
				vectorIndex: new RecordingVectorIndex(),
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});

			await memory.recall({
				query: "moon herb",
				selector: partition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});
			await firstScanStarted.promise;
			const rebuilding = memory.rebuild(partition);
			await new Promise((resolve) => setTimeout(resolve, 0));
			expect(scanCalls).toBe(1);

			releaseFirstScan.resolve();
			await secondScanStarted.promise;
			const closing = memory.closeIndexPreparation("drain");
			let closed = false;
			void closing.then(() => {
				closed = true;
			});
			await Promise.resolve();
			expect(closed).toBe(false);
			releaseSecondScan.resolve();
			await rebuilding;
			await closing;
			expect(scanCalls).toBe(2);
		} finally {
			authority.close();
		}
	});

	it("bounds concurrent and pending preparation across different partitions", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember({
				...draft("first", "seed fact", "1"),
				gameTime: { timelineId: "world-1", generation: "1", tick: "10" },
			});
			await new GameMemory({ authority: store, now: clock() }).remember({
				...draft("second", "seed fact", "1"),
				gameTime: { timelineId: "world-2", generation: "1", tick: "10" },
			});
			await new GameMemory({ authority: store, now: clock() }).remember({
				...draft("third", "seed fact", "1"),
				gameTime: { timelineId: "world-3", generation: "1", tick: "10" },
			});
			const started = new Set<string>();
			const releases = new Map(["world-1", "world-2"].map((timeline) => [timeline, Promise.withResolvers<void>()]));
			vi.spyOn(store, "scanActive").mockImplementation(async (partition) => {
				started.add(partition.timelineId);
				await releases.get(partition.timelineId)?.promise;
				return [];
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: {
					maxPendingPartitions: 2,
					maxConcurrentPartitions: 2,
					retryFailure: "never",
				},
			});
			const recall = (timelineId: string) =>
				memory.recall({
					query: "moon herb",
					selector: { ownerId: "owner", timelineId, generation: "1" },
					viewer: { kind: "owner" as const, ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				});

			await expect(recall("world-1")).resolves.toMatchObject({ vectorStatus: "preparing" });
			await expect(recall("world-2")).resolves.toMatchObject({ vectorStatus: "preparing" });
			await vi.waitFor(() => expect(started).toEqual(new Set(["world-1", "world-2"])));
			await expect(recall("world-3")).resolves.toMatchObject({ vectorStatus: "unprepared" });
			expect(started).not.toContain("world-3");
			expect(embedder.batchSizes).toEqual([]);

			for (const release of releases.values()) release.resolve();
			await memory.closeIndexPreparation("drain");
		} finally {
			authority.close();
		}
	});

	it("preserves FIFO order while bounding and draining background preparation", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const seed = new GameMemory({ authority: store, now: clock() });
			for (const timelineId of ["world-1", "world-2", "world-3", "world-4"]) {
				await seed.remember({
					...draft(timelineId, "seed fact", "1"),
					gameTime: { timelineId, generation: "1", tick: "10" },
				});
			}
			const started: string[] = [];
			const starts = new Map(
				["world-1", "world-2", "world-3"].map((timelineId) => [timelineId, Promise.withResolvers<void>()]),
			);
			const releases = new Map(
				["world-1", "world-2", "world-3"].map((timelineId) => [timelineId, Promise.withResolvers<void>()]),
			);
			vi.spyOn(store, "scanActive").mockImplementation(async (partition) => {
				started.push(partition.timelineId);
				starts.get(partition.timelineId)?.resolve();
				await releases.get(partition.timelineId)?.promise;
				return [];
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: {
					maxPendingPartitions: 3,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});
			const recall = (timelineId: string) =>
				memory.recall({
					query: "moon herb",
					selector: { ownerId: "owner", timelineId, generation: "1" },
					viewer: { kind: "owner" as const, ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				});

			await expect(recall("world-1")).resolves.toMatchObject({ vectorStatus: "preparing" });
			await expect(recall("world-2")).resolves.toMatchObject({ vectorStatus: "preparing" });
			await expect(recall("world-3")).resolves.toMatchObject({ vectorStatus: "preparing" });
			await expect(recall("world-4")).resolves.toMatchObject({ vectorStatus: "unprepared" });
			await starts.get("world-1")?.promise;
			expect(started).toEqual(["world-1"]);

			releases.get("world-1")?.resolve();
			await starts.get("world-2")?.promise;
			expect(started).toEqual(["world-1", "world-2"]);
			releases.get("world-2")?.resolve();
			await starts.get("world-3")?.promise;
			expect(started).toEqual(["world-1", "world-2", "world-3"]);
			expect(started).not.toContain("world-4");
			expect(embedder.batchSizes).toEqual([]);

			const draining = memory.closeIndexPreparation("drain");
			let drained = false;
			void draining.then(() => {
				drained = true;
			});
			await Promise.resolve();
			expect(drained).toBe(false);
			releases.get("world-3")?.resolve();
			await draining;
			expect(drained).toBe(true);
		} finally {
			authority.close();
		}
	});

	it("retries a failed background preparation only under the explicit next-recall policy", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember(draft("herb", "moon herb remedy", "1"));
			let embeddingCalls = 0;
			const firstFailure = Promise.withResolvers<void>();
			const retryStarted = Promise.withResolvers<void>();
			const embedder: MemoryEmbeddingProvider = {
				modelId: "retryable-fake-v1",
				dimensions: 2,
				maxTextsPerRequest: 1,
				embed: async (texts) => {
					embeddingCalls += 1;
					if (embeddingCalls === 1) {
						firstFailure.resolve();
						throw new Error("provider unavailable");
					}
					retryStarted.resolve();
					return texts.map(() => new Float32Array([1, 0]));
				},
			};
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: new RecordingVectorIndex("retryable-fake-v1", 2),
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "next-recall",
				},
			});
			const request = {
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			} as const;

			await expect(memory.recall(request)).resolves.toMatchObject({ vectorStatus: "preparing" });
			await firstFailure.promise;
			await new Promise((resolve) => setImmediate(resolve));
			await expect(memory.recall(request)).resolves.toMatchObject({ vectorStatus: "preparing" });
			await retryStarted.promise;
			expect(embeddingCalls).toBe(2);
			await memory.closeIndexPreparation("drain");
			await expect(memory.recall(request)).resolves.toMatchObject({ vectorStatus: "used" });
			expect(embeddingCalls).toBe(3);
		} finally {
			authority.close();
		}
	});

	it("does not cancel shared preparation when the initiating recall is cancelled", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember(draft("herb", "moon herb remedy", "1"));
			const embeddingStarted = Promise.withResolvers<AbortSignal | undefined>();
			const releaseEmbedding = Promise.withResolvers<void>();
			const embedder: MemoryEmbeddingProvider = {
				modelId: "shared-preparation-v1",
				dimensions: 2,
				maxTextsPerRequest: 1,
				embed: async (texts, signal) => {
					embeddingStarted.resolve(signal);
					await releaseEmbedding.promise;
					return texts.map(() => new Float32Array([1, 0]));
				},
			};
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: new RecordingVectorIndex("shared-preparation-v1", 2),
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});
			const caller = new AbortController();
			const request = {
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
				signal: caller.signal,
			} as const;

			await expect(memory.recall(request)).resolves.toMatchObject({ vectorStatus: "preparing" });
			const preparationSignal = await embeddingStarted.promise;
			caller.abort(new Error("user run cancelled"));
			expect(preparationSignal?.aborted).toBe(false);

			releaseEmbedding.resolve();
			await memory.closeIndexPreparation("drain");
		} finally {
			authority.close();
		}
	});

	it("keeps failed preparation lexical-only when retries are disabled", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember(draft("herb", "moon herb remedy", "1"));
			let embeddingCalls = 0;
			const embedder: MemoryEmbeddingProvider = {
				modelId: "failed-fake-v1",
				dimensions: 2,
				maxTextsPerRequest: 1,
				embed: async () => {
					embeddingCalls += 1;
					throw new Error("provider unavailable");
				},
			};
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: new RecordingVectorIndex("failed-fake-v1", 2),
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});
			const request = {
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			} as const;

			await expect(memory.recall(request)).resolves.toMatchObject({ vectorStatus: "preparing" });
			await vi.waitFor(async () => {
				await expect(memory.recall(request)).resolves.toMatchObject({
					vectorStatus: "preparation-failed",
					hits: [{ record: { memoryId: "herb" } }],
				});
			});
			expect(embeddingCalls).toBe(1);
			await memory.closeIndexPreparation("drain");
		} finally {
			authority.close();
		}
	});

	it("does not evict a non-retryable failure and retry it under capacity pressure", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, { ...limits, maxTrackedIndexPartitions: 1 });
			const seed = new GameMemory({ authority: store, now: clock() });
			await seed.remember(draft("first", "moon herb", "1"));
			await seed.remember(draft("second", "blacksmith", "2"));
			const failed = Promise.withResolvers<void>();
			let embeddingCalls = 0;
			const embedder: MemoryEmbeddingProvider = {
				modelId: "non-retryable-fake-v1",
				dimensions: 2,
				maxTextsPerRequest: 1,
				embed: async () => {
					embeddingCalls += 1;
					failed.resolve();
					throw new Error("provider unavailable");
				},
			};
			const index = new RecordingVectorIndex("non-retryable-fake-v1", 2);
			const options = {
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never" as const,
				},
			};
			const memory = new GameMemory(options);
			const recall = (generation: string) =>
				memory.recall({
					query: "moon herb",
					selector: { ownerId: "owner", timelineId: "world", generation },
					viewer: { kind: "owner" as const, ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				});

			await expect(recall("1")).resolves.toMatchObject({ vectorStatus: "preparing" });
			await failed.promise;
			await vi.waitFor(async () => {
				await expect(recall("1")).resolves.toMatchObject({ vectorStatus: "preparation-failed" });
			});
			await expect(recall("2")).resolves.toMatchObject({ vectorStatus: "unprepared" });
			expect(embeddingCalls).toBe(1);
			await memory.closeIndexPreparation("drain");

			const reopened = new GameMemory(options);
			await expect(
				reopened.recall({
					query: "moon herb",
					selector: { ownerId: "owner", timelineId: "world", generation: "1" },
					viewer: { kind: "owner", ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				}),
			).resolves.toMatchObject({ vectorStatus: "preparing" });
			await vi.waitFor(async () => {
				await expect(
					reopened.recall({
						query: "moon herb",
						selector: { ownerId: "owner", timelineId: "world", generation: "1" },
						viewer: { kind: "owner", ownerId: "owner" },
						resultLimit: 1,
						vectorFailure: "fail",
					}),
				).resolves.toMatchObject({ vectorStatus: "preparation-failed" });
			});
			expect(embeddingCalls).toBe(2);
			await memory.closeIndexPreparation("drain");
			await expect(
				reopened.recall({
					query: "moon herb",
					selector: { ownerId: "owner", timelineId: "world", generation: "1" },
					viewer: { kind: "owner", ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				}),
			).resolves.toMatchObject({ vectorStatus: "preparation-failed" });
			await reopened.closeIndexPreparation("drain");
		} finally {
			authority.close();
		}
	});

	it("invalidates prepared state after a memory mutation and prepares again", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const replace = vi.spyOn(index, "replace");
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				now: clock(),
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});
			const request = {
				query: "moon herb",
				selector: { ownerId: "owner", timelineId: "world", generation: "1" },
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			} as const;

			await expect(memory.recall(request)).resolves.toMatchObject({ vectorStatus: "proven-empty" });
			await vi.waitFor(async () => {
				await expect(memory.recall(request)).resolves.toMatchObject({ vectorStatus: "proven-empty" });
			});
			expect(replace).not.toHaveBeenCalled();

			await memory.remember(draft("herb", "moon herb remedy", "1"));
			embedder.batchSizes.length = 0;
			search.mockClear();
			await expect(memory.recall(request)).resolves.toMatchObject({
				vectorStatus: "preparing",
				hits: [{ record: { memoryId: "herb" } }],
			});
			expect(embedder.batchSizes).toEqual([]);
			expect(search).not.toHaveBeenCalled();

			await memory.closeIndexPreparation("drain");
			expect(replace).toHaveBeenCalledOnce();
		} finally {
			authority.close();
		}
	});

	it("preserves authoritative emptiness beyond the host tracking capacity", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, { ...limits, maxTrackedIndexPartitions: 1 });
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index });
			const first = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			const second = { ownerId: "owner", timelineId: "world", generation: "2" } as const;

			await memory.rebuild(first);
			await memory.rebuild(second);
			embedder.batchSizes.length = 0;
			const recalledFirst = await memory.recall({
				query: "moon herb",
				selector: first,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});
			const recalledSecond = await memory.recall({
				query: "moon herb",
				selector: second,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(recalledFirst.vectorStatus).toBe("proven-empty");
			expect(recalledSecond.vectorStatus).toBe("proven-empty");
			expect(embedder.batchSizes).toEqual([]);
			expect(search).not.toHaveBeenCalled();
		} finally {
			authority.close();
		}
	});

	it("does not treat unindexed active records as an authoritative empty rebuild", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember(draft("active", "moon herb", "1"));
			vi.spyOn(store, "writeEmbedding").mockResolvedValue(false);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				indexPreparation: {
					maxPendingPartitions: 1,
					maxConcurrentPartitions: 1,
					retryFailure: "never",
				},
			});
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;

			await expect(memory.rebuild(partition)).resolves.toEqual({ indexed: 0, embedded: 0, restored: 0 });
			embedder.batchSizes.length = 0;
			const result = await memory.recall({
				query: "moon herb",
				selector: partition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(embedder.batchSizes).toEqual([]);
			expect(search).not.toHaveBeenCalled();
			expect(result.vectorStatus).toBe("preparing");
			await vi.waitFor(async () => {
				await expect(
					memory.recall({
						query: "moon herb",
						selector: partition,
						viewer: { kind: "owner", ownerId: "owner" },
						resultLimit: 1,
						vectorFailure: "fail",
					}),
				).resolves.toMatchObject({ vectorStatus: "preparation-failed" });
			});
			expect(embedder.batchSizes).toEqual([1]);
			expect(search).not.toHaveBeenCalled();
			await memory.closeIndexPreparation("drain");
		} finally {
			authority.close();
		}
	});

	it("does not prove emptiness when a vector index returns without consuming the rebuild stream", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			await new GameMemory({ authority: store, now: clock() }).remember({
				...draft("seed", "seed fact", "1"),
				gameTime: { timelineId: "world", generation: "1", tick: "10" },
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			vi.spyOn(index, "replace").mockResolvedValue();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index });
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;

			await expect(memory.rebuild(partition)).resolves.toEqual({ indexed: 0, embedded: 0, restored: 0 });
			embedder.batchSizes.length = 0;
			const recalled = await memory.recall({
				query: "moon herb",
				selector: partition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(recalled.vectorStatus).toBe("used");
			expect(embedder.batchSizes).toEqual([1]);
			expect(search).toHaveBeenCalledOnce();
		} finally {
			authority.close();
		}
	});

	it("keeps recall empty until the first active record commits", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const rememberStarted = Promise.withResolvers<void>();
			const releaseRemember = Promise.withResolvers<void>();
			const remember = store.remember.bind(store);
			vi.spyOn(store, "remember").mockImplementation(async (...args) => {
				rememberStarted.resolve();
				await releaseRemember.promise;
				return remember(...args);
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				now: clock(),
			});
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			await memory.rebuild(partition);

			const remembering = memory.remember(draft("first-after-empty", "moon herb", "1"));
			await rememberStarted.promise;
			embedder.batchSizes.length = 0;
			try {
				const result = await memory.recall({
					query: "moon herb",
					selector: partition,
					viewer: { kind: "owner", ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				});

				expect(embedder.batchSizes).toEqual([]);
				expect(search).not.toHaveBeenCalled();
				expect(result.vectorStatus).toBe("proven-empty");
			} finally {
				releaseRemember.resolve();
				await expect(remembering).resolves.toMatchObject({ indexStatus: "ready" });
			}
		} finally {
			authority.close();
		}
	});

	it("does not let an empty rebuild overtake an in-flight first write", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const rememberStarted = Promise.withResolvers<void>();
			const releaseRemember = Promise.withResolvers<void>();
			const remember = store.remember.bind(store);
			vi.spyOn(store, "remember").mockImplementation(async (...args) => {
				rememberStarted.resolve();
				await releaseRemember.promise;
				return remember(...args);
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				now: clock(),
			});
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			const remembering = memory.remember(draft("overtaking-first", "moon herb", "1"));
			await rememberStarted.promise;

			await memory.rebuild(partition);
			releaseRemember.resolve();
			await remembering;
			embedder.batchSizes.length = 0;
			const recalled = await memory.recall({
				query: "moon herb",
				selector: partition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(recalled.vectorStatus).toBe("used");
			expect(embedder.batchSizes).toEqual([1]);
			expect(search).toHaveBeenCalledOnce();
		} finally {
			authority.close();
		}
	});

	it("retains an exact empty rebuild completed during an unrelated first write", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const rememberStarted = Promise.withResolvers<void>();
			const releaseRemember = Promise.withResolvers<void>();
			const remember = store.remember.bind(store);
			vi.spyOn(store, "remember").mockImplementation(async (...args) => {
				rememberStarted.resolve();
				await releaseRemember.promise;
				return remember(...args);
			});
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				now: clock(),
			});
			const remembering = memory.remember(draft("unrelated-first", "moon herb", "1"));
			await rememberStarted.promise;
			const emptyPartition = { ownerId: "owner", timelineId: "world", generation: "2" } as const;

			await memory.rebuild(emptyPartition);
			releaseRemember.resolve();
			await remembering;
			embedder.batchSizes.length = 0;
			const recalled = await memory.recall({
				query: "moon herb",
				selector: emptyPartition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(recalled.vectorStatus).toBe("proven-empty");
			expect(embedder.batchSizes).toEqual([]);
			expect(search).not.toHaveBeenCalled();
		} finally {
			authority.close();
		}
	});

	it.each(["revise", "forget"] as const)(
		"preserves an unrelated prepared-empty partition while an authority %s is in flight",
		async (operation) => {
			const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
			try {
				const store = new SqliteMemoryAuthority(authority, limits);
				const seeded = await new GameMemory({ authority: store, now: clock() }).remember(
					draft("mutation-seed", "moon herb", "1"),
				);
				const mutationStarted = Promise.withResolvers<void>();
				const releaseMutation = Promise.withResolvers<void>();
				if (operation === "revise") {
					const revise = store.revise.bind(store);
					vi.spyOn(store, "revise").mockImplementation(async (...args) => {
						mutationStarted.resolve();
						await releaseMutation.promise;
						return revise(...args);
					});
				} else {
					const forget = store.forget.bind(store);
					vi.spyOn(store, "forget").mockImplementation(async (...args) => {
						mutationStarted.resolve();
						await releaseMutation.promise;
						return forget(...args);
					});
				}
				const embedder = new SemanticFakeEmbedding();
				const index = new RecordingVectorIndex();
				const search = vi.spyOn(index, "search");
				const memory = new GameMemory({
					authority: store,
					embeddingProvider: embedder,
					vectorIndex: index,
					now: clock(),
				});
				const emptyPartition = { ownerId: "owner", timelineId: "world", generation: "2" } as const;
				await memory.rebuild(emptyPartition);
				const mutating =
					operation === "revise"
						? memory.revise("mutation-seed", seeded.record.revision, { searchText: "blacksmith" })
						: memory.forget("mutation-seed", seeded.record.revision);
				await mutationStarted.promise;
				embedder.batchSizes.length = 0;

				const duringMutation = await memory.recall({
					query: "moon herb",
					selector: emptyPartition,
					viewer: { kind: "owner", ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				});
				expect(duringMutation.vectorStatus).toBe("proven-empty");
				expect(embedder.batchSizes).toEqual([]);
				expect(search).not.toHaveBeenCalled();

				releaseMutation.resolve();
				await mutating;
				embedder.batchSizes.length = 0;
				search.mockClear();
				const afterMutation = await memory.recall({
					query: "moon herb",
					selector: emptyPartition,
					viewer: { kind: "owner", ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				});
				expect(afterMutation.vectorStatus).toBe("proven-empty");
				expect(embedder.batchSizes).toEqual([]);
				expect(search).not.toHaveBeenCalled();
			} finally {
				authority.close();
			}
		},
	);

	it("coordinates prepared-empty state across GameMemory instances sharing one vector index", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const first = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index });
			const second = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				now: clock(),
			});
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			await first.rebuild(partition);

			await second.remember(draft("shared-first", "moon herb", "1"));
			embedder.batchSizes.length = 0;
			const recalled = await first.recall({
				query: "moon herb",
				selector: partition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(recalled.vectorStatus).toBe("used");
			expect(embedder.batchSizes).toEqual([1]);
			expect(search).toHaveBeenCalledOnce();
		} finally {
			authority.close();
		}
	});

	it("keeps an authoritative empty lane empty while rebuilding", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const started = Promise.withResolvers<void>();
			const release = Promise.withResolvers<void>();
			const replace = index.replace.bind(index);
			let replacement = 0;
			vi.spyOn(index, "replace").mockImplementation(async (target, entries) => {
				replacement += 1;
				if (replacement === 2) {
					started.resolve();
					await release.promise;
				}
				await replace(target, entries);
			});
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index });
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			await memory.rebuild(partition);
			const rebuilding = memory.rebuild(partition);
			await started.promise;
			embedder.batchSizes.length = 0;

			try {
				const recalled = await memory.recall({
					query: "moon herb",
					selector: partition,
					viewer: { kind: "owner", ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				});

				expect(recalled.vectorStatus).toBe("proven-empty");
				expect(embedder.batchSizes).toEqual([]);
				expect(search).not.toHaveBeenCalled();
			} finally {
				release.resolve();
				await rebuilding;
			}
		} finally {
			authority.close();
		}
	});

	it.each(["failed", "cancelled"] as const)(
		"keeps a lane empty after a %s rebuild until authority records exist",
		async (outcome) => {
			const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
			try {
				const store = new SqliteMemoryAuthority(authority, limits);
				const embedder = new SemanticFakeEmbedding();
				const index = new RecordingVectorIndex();
				const replace = index.replace.bind(index);
				const controller = new AbortController();
				const reason = new Error(`${outcome} rebuild`);
				let replacements = 0;
				vi.spyOn(index, "replace").mockImplementation(async (partition, entries) => {
					replacements += 1;
					if (replacements === 2) {
						if (outcome === "failed") throw reason;
						controller.abort(reason);
					}
					await replace(partition, entries);
				});
				const search = vi.spyOn(index, "search");
				const memory = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index });
				const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
				await memory.rebuild(partition);

				await expect(memory.rebuild(partition, controller.signal)).rejects.toBe(reason);
				embedder.batchSizes.length = 0;
				const recalled = await memory.recall({
					query: "moon herb",
					selector: partition,
					viewer: { kind: "owner", ownerId: "owner" },
					resultLimit: 1,
					vectorFailure: "fail",
				});

				expect(recalled.vectorStatus).toBe("proven-empty");
				expect(embedder.batchSizes).toEqual([]);
				expect(search).not.toHaveBeenCalled();
			} finally {
				authority.close();
			}
		},
	);

	it("keeps an authoritative empty lane empty after a failed first write", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const writeFailure = new Error("write failed");
			vi.spyOn(store, "remember").mockRejectedValue(writeFailure);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index });
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			await memory.rebuild(partition);

			await expect(memory.remember(draft("failed-first", "moon herb", "1"))).rejects.toBe(writeFailure);
			embedder.batchSizes.length = 0;
			const recalled = await memory.recall({
				query: "moon herb",
				selector: partition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(recalled.vectorStatus).toBe("proven-empty");
			expect(embedder.batchSizes).toEqual([]);
			expect(search).not.toHaveBeenCalled();
		} finally {
			authority.close();
		}
	});

	it("does not let an older empty rebuild overwrite a concurrent first write", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const index = new RecordingVectorIndex();
			const replacementConsumed = Promise.withResolvers<void>();
			const releaseReplacement = Promise.withResolvers<void>();
			const replace = index.replace.bind(index);
			let replacement = 0;
			vi.spyOn(index, "replace").mockImplementation(async (target, entries) => {
				replacement += 1;
				if (replacement === 1) {
					await replace(target, entries);
					return;
				}
				for await (const _entry of entries) {
					throw new Error("The controlled empty rebuild unexpectedly produced an entry.");
				}
				replacementConsumed.resolve();
				await releaseReplacement.promise;
			});
			const search = vi.spyOn(index, "search");
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				now: clock(),
			});
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			await memory.rebuild(partition);
			const rebuilding = memory.rebuild(partition);
			await replacementConsumed.promise;

			await memory.remember(draft("concurrent-first", "moon herb", "1"));
			releaseReplacement.resolve();
			await rebuilding;
			embedder.batchSizes.length = 0;
			const recalled = await memory.recall({
				query: "moon herb",
				selector: partition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 1,
				vectorFailure: "fail",
			});

			expect(recalled.vectorStatus).toBe("used");
			expect(embedder.batchSizes).toEqual([1]);
			expect(search).toHaveBeenCalledOnce();
		} finally {
			authority.close();
		}
	});

	it("regenerates a persisted embedding whose source fingerprint no longer matches the memory", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const embedder = new SemanticFakeEmbedding();
			const initial = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: testUsearchIndex(embedder),
				now: clock(),
			});
			await initial.remember(draft("fingerprint", "moon herb", "1"));
			authority.write((database) =>
				database.prepare("UPDATE oga_memory_embeddings SET source_fingerprint = ?").run("0".repeat(64)),
			);
			embedder.batchSizes.length = 0;

			const restarted = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: testUsearchIndex(embedder),
				now: clock(),
			});
			expect(await restarted.rebuild({ ownerId: "owner", timelineId: "world", generation: "1" })).toEqual({
				indexed: 1,
				embedded: 1,
				restored: 0,
			});
			expect(embedder.batchSizes).toEqual([1]);
		} finally {
			authority.close();
		}
	});

	it.each([
		{
			name: "truncated vector bytes",
			corrupt: (authority: SqliteAuthority, memoryKey: bigint, dimensions: number) =>
				authority.write((database) =>
					database
						.prepare("UPDATE oga_memory_embeddings SET vector = ? WHERE memory_key = ?")
						.run(Buffer.alloc(dimensions * Float32Array.BYTES_PER_ELEMENT - 1), memoryKey),
				),
		},
		{
			name: "dimension metadata",
			corrupt: (authority: SqliteAuthority, memoryKey: bigint, dimensions: number) =>
				authority.write((database) =>
					database
						.prepare("UPDATE oga_memory_embeddings SET dimensions = ? WHERE memory_key = ?")
						.run(dimensions + 1, memoryKey),
				),
		},
		{
			name: "non-finite vector values",
			corrupt: (authority: SqliteAuthority, memoryKey: bigint, dimensions: number) => {
				const bytes = Buffer.alloc(dimensions * Float32Array.BYTES_PER_ELEMENT);
				bytes.writeFloatLE(Number.NaN, 0);
				authority.write((database) =>
					database.prepare("UPDATE oga_memory_embeddings SET vector = ? WHERE memory_key = ?").run(bytes, memoryKey),
				);
			},
		},
	])("keeps the serving vector index complete when persisted $name are corrupt", async ({ corrupt }) => {
		const root = await mkdtemp(join(tmpdir(), "opengameagent-memory-embedding-recovery-test-"));
		const path = join(root, "memory.sqlite3");
		const rebuildLimits = { ...limits, maxIndexBatchSize: 1 };
		const authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, rebuildLimits);
			const embedder = new SemanticFakeEmbedding();
			const index = testUsearchIndex(embedder);
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: embedder,
				vectorIndex: index,
				now: clock(),
			});
			await memory.remember(draft("first", "moon herb", "1"));
			const second = await memory.remember(draft("second", "blacksmith", "1"));
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			const request = {
				query: "blacksmith",
				selector: partition,
				viewer: { kind: "owner", ownerId: "owner" },
				resultLimit: 2,
				vectorFailure: "fail",
			} as const;
			const baselineIds = (await memory.recall(request)).hits.map((hit) => hit.record.memoryId);
			expect(index.partitionSize(partition)).toBe(2);

			corrupt(authority, second.record.memoryKey, embedder.dimensions);

			await expect(memory.rebuild(partition)).rejects.toThrow();
			expect(index.partitionSize(partition)).toBe(2);
			expect((await memory.recall(request)).hits.map((hit) => hit.record.memoryId)).toEqual(baselineIds);

			await store.deleteEmbedding(second.record.memoryKey);
			embedder.batchSizes.length = 0;
			expect(await memory.rebuild(partition)).toEqual({ indexed: 2, embedded: 1, restored: 0 });
			expect(embedder.batchSizes).toEqual([1]);
			expect(
				await store.readEmbedding(second.record.memoryKey, second.record.revision, embedder.modelId),
			).toMatchObject({ dimensions: embedder.dimensions });
			expect((await memory.recall(request)).hits.map((hit) => hit.record.memoryId)).toEqual(baselineIds);
		} finally {
			authority.close();
			await removeTemporaryRoot(root);
		}
	});

	it("rejects an already-aborted rebuild even when vector indexing is not configured", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const controller = new AbortController();
			const reason = new Error("rebuild cancelled");
			controller.abort(reason);
			await expect(
				new GameMemory({ authority: new SqliteMemoryAuthority(authority, limits) }).rebuild(
					{ ownerId: "owner", timelineId: "world", generation: "1" },
					controller.signal,
				),
			).rejects.toBe(reason);
		} finally {
			authority.close();
		}
	});

	it("preserves cancellation when a rebuild scan aborts and returns an empty page", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const controller = new AbortController();
			const reason = new Error("empty scan cancelled");
			vi.spyOn(store, "scanActive").mockImplementation(async () => {
				controller.abort(reason);
				return [];
			});
			const embedder = new SemanticFakeEmbedding();
			await expect(
				new GameMemory({
					authority: store,
					embeddingProvider: embedder,
					vectorIndex: testUsearchIndex(embedder),
				}).rebuild({ ownerId: "owner", timelineId: "world", generation: "1" }, controller.signal),
			).rejects.toBe(reason);
		} finally {
			authority.close();
		}
	});

	it.each(["scan", "read", "embedding", "write", "replace"] as const)(
		"stops rebuild after %s work aborts but returns normally",
		async (stage) => {
			const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
			try {
				const store = new SqliteMemoryAuthority(authority, limits);
				await new GameMemory({ authority: store, now: clock() }).remember(draft("seed", "moon herb", "1"));

				const controller = new AbortController();
				const reason = new Error(`${stage} cancelled`);
				const trace: string[] = [];
				const abortAfter = <Value>(value: Value): Value => {
					controller.abort(reason);
					return value;
				};
				const scanActive = store.scanActive.bind(store);
				vi.spyOn(store, "scanActive").mockImplementation(async (...args) => {
					trace.push("scan");
					const records = await scanActive(...args);
					return stage === "scan" ? abortAfter(records) : records;
				});
				const readEmbedding = store.readEmbedding.bind(store);
				vi.spyOn(store, "readEmbedding").mockImplementation(async (...args) => {
					trace.push("read");
					const embedding = await readEmbedding(...args);
					return stage === "read" ? abortAfter(embedding) : embedding;
				});
				const writeEmbedding = store.writeEmbedding.bind(store);
				vi.spyOn(store, "writeEmbedding").mockImplementation(async (...args) => {
					trace.push("write");
					const written = await writeEmbedding(...args);
					return stage === "write" ? abortAfter(written) : written;
				});

				const embedder: MemoryEmbeddingProvider = {
					modelId: "rebuild-cancellation",
					dimensions: 2,
					maxTextsPerRequest: 1,
					embed: async (texts) => {
						trace.push("embedding");
						const vectors = texts.map(() => new Float32Array([1, 0]));
						return stage === "embedding" ? abortAfter(vectors) : vectors;
					},
				};
				const index = testUsearchIndex(embedder);
				await index.upsert(
					{ ownerId: "owner", timelineId: "world", generation: "1" },
					{ memoryKey: 99n, vector: new Float32Array([0, 1]) },
				);
				const replace = index.replace.bind(index);
				vi.spyOn(index, "replace").mockImplementation(async (partition, entries) => {
					trace.push("replace");
					if (stage === "replace") {
						const iterator = entries[Symbol.asyncIterator]();
						const first = await iterator.next();
						if (!first.done) trace.push("yield");
						controller.abort(reason);
						await iterator.return?.();
						return;
					}
					await replace(
						partition,
						(async function* () {
							for await (const entry of entries) {
								trace.push("yield");
								yield entry;
							}
						})(),
					);
				});

				await expect(
					new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index, now: clock() }).rebuild(
						{ ownerId: "owner", timelineId: "world", generation: "1" },
						controller.signal,
					),
				).rejects.toBe(reason);

				const expectedBeforeAbort = ["replace", "scan", "read", "embedding", "write"];
				if (stage !== "replace") {
					expect(trace).toEqual(expectedBeforeAbort.slice(0, expectedBeforeAbort.indexOf(stage) + 1));
					expect(index.partitionSize({ ownerId: "owner", timelineId: "world", generation: "1" })).toBe(1);
				}
			} finally {
				authority.close();
			}
		},
	);

	it("stops yielding rebuild entries as soon as the consuming index aborts", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, limits);
			const seed = new GameMemory({ authority: store, now: clock() });
			await seed.remember(draft("first-yield", "moon herb", "1"));
			await seed.remember(draft("second-yield", "blacksmith", "1"));
			const controller = new AbortController();
			const reason = new Error("yield cancelled");
			const embedder = new SemanticFakeEmbedding();
			const index = testUsearchIndex(embedder);
			const partition = { ownerId: "owner", timelineId: "world", generation: "1" } as const;
			await index.upsert(partition, { memoryKey: 99n, vector: new Float32Array([0, 0, 1, 0]) });
			vi.spyOn(index, "replace").mockImplementation(async (_partition, entries) => {
				const iterator = entries[Symbol.asyncIterator]();
				expect((await iterator.next()).done).toBe(false);
				controller.abort(reason);
				await expect(iterator.next()).rejects.toBe(reason);
			});

			await expect(
				new GameMemory({ authority: store, embeddingProvider: embedder, vectorIndex: index, now: clock() }).rebuild(
					partition,
					controller.signal,
				),
			).rejects.toBe(reason);
			expect(index.partitionSize(partition)).toBe(1);
		} finally {
			authority.close();
		}
	});

	it("owns one vector snapshot across durable and derived writes", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new MutatingEmbeddingSqliteMemoryAuthority(authority, limits);
			const index = new RecordingVectorIndex();
			const memory = new GameMemory({
				authority: store,
				embeddingProvider: new SemanticFakeEmbedding(),
				vectorIndex: index,
				now: clock(),
			});
			const result = await memory.remember(draft("snapshot", "moon herb", "1"));
			expect(result.indexStatus).toBe("ready");
			expect(store.received).toEqual([1, 0, 0, 0]);
			expect(index.upserted).toEqual([1, 0, 0, 0]);
		} finally {
			authority.close();
		}
	});
});

class DeleteFailingSqliteMemoryAuthority extends SqliteMemoryAuthority {
	override async deleteEmbedding(_memoryKey: bigint): Promise<void> {
		throw new Error("derived cleanup unavailable");
	}
}

class MutatingEmbeddingSqliteMemoryAuthority extends SqliteMemoryAuthority {
	received: number[] = [];

	override async writeEmbedding(embedding: MemoryEmbedding): Promise<boolean> {
		this.received = Array.from(embedding.vector);
		const written = await super.writeEmbedding(embedding);
		embedding.vector.fill(9);
		return written;
	}
}

class RecordingVectorIndex implements MemoryVectorIndex {
	upserted: number[] = [];

	constructor(
		readonly modelId = "semantic-fake-v1",
		readonly dimensions = 4,
	) {}

	async upsert(_partition: MemoryIndexPartition, entry: MemoryVectorEntry): Promise<void> {
		this.upserted = Array.from(entry.vector);
	}

	async remove(_partition: MemoryIndexPartition, _memoryKey: bigint): Promise<void> {}

	async search(
		_partition: MemoryIndexPartition,
		_vector: Float32Array,
		_limit: number,
	): Promise<readonly RankedMemoryCandidate[]> {
		return [];
	}

	async replace(_partition: MemoryIndexPartition, entries: AsyncIterable<MemoryVectorEntry>): Promise<void> {
		for await (const entry of entries) this.upserted = Array.from(entry.vector);
	}
}

class SemanticFakeEmbedding implements MemoryEmbeddingProvider {
	readonly modelId = "semantic-fake-v1";
	readonly dimensions = 4;
	readonly batchSizes: number[] = [];

	constructor(readonly maxTextsPerRequest = 8) {}

	async embed(texts: readonly string[]): Promise<readonly Float32Array[]> {
		this.batchSizes.push(texts.length);
		return texts.map((text) => {
			if (text === "embedding-failure") throw new Error("provider unavailable");
			if (/moon|lunar|medicine|herb|fever/u.test(text)) return new Float32Array([1, 0, 0, 0]);
			return new Float32Array([0, 1, 0, 0]);
		});
	}
}

function draft(memoryId: string, searchText: string, generation: string) {
	return {
		memoryId,
		partition: { ownerId: "owner", actorId: "npc", sessionId: "session", scope: "world" },
		kind: "observation",
		tags: ["knowledge"],
		gameTime: { timelineId: "world", generation, tick: "10" },
		importance: 0.8,
		searchText,
		content: { fact: searchText },
		audience: { visibility: "owner" },
	} as const;
}

function clock(): () => string {
	let tick = 0;
	return () => `2026-08-28T00:00:${String(tick++).padStart(2, "0")}.000Z`;
}

async function removeTemporaryRoot(root: string): Promise<void> {
	const target = resolve(root);
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
	await rm(target, { recursive: true, force: true });
}

function stripStageDurations(
	stages: readonly {
		readonly stage: string;
		readonly outcome: string;
		readonly durationMs: number;
		readonly inputCount: number;
		readonly outputCount: number;
	}[],
) {
	return stages.map(({ durationMs, ...stage }) => {
		expect(Number.isFinite(durationMs)).toBe(true);
		expect(durationMs).toBeGreaterThanOrEqual(0);
		return stage;
	});
}

import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import type { MemoryLimits, MemoryWrite } from "@opengameagent/memory";
import { MemoryConflictError, MemoryRevisionConflictError } from "@opengameagent/memory";
import { describe, expect, it } from "vitest";
import { SqliteAuthority, SqliteMemoryAuthority } from "../src/index.js";

const limits: MemoryLimits = {
	maxSearchTextBytes: 4_096,
	maxContentBytes: 16_384,
	maxTagsPerMemory: 16,
	maxTagBytes: 256,
	maxRecipientsPerMemory: 16,
	maxQueryTextBytes: 1_024,
	maxSelectorKinds: 16,
	maxSelectorTags: 16,
	maxLexicalCandidates: 32,
	maxVectorCandidates: 32,
	maxHydrateCandidates: 48,
	maxRecallResults: 16,
	maxIndexBatchSize: 32,
	maxTrackedIndexPartitions: 32,
	maxExportPageSize: 32,
	maxPurgeBatchSize: 32,
};

describe("SqliteMemoryAuthority", () => {
	it("persists lane counts and isolates durable revision changes across index partitions", async () => {
		const root = await mkdtemp(join(tmpdir(), "opengameagent-memory-partition-state-"));
		const path = join(root, "memory.sqlite3");
		let authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			let store = new SqliteMemoryAuthority(authority, limits);
			const selector = { ownerId: "owner-1", timelineId: "world", generation: "1" };
			const write = memory("first", "owner-1", "1", "seed");
			expect((await store.readPartitionState(selector)).activeCount).toBe(0);
			await store.remember(write);
			const original = await store.readPartitionState(selector);
			expect(original.activeCount).toBe(1);
			await store.remember(write);
			expect(await store.readPartitionState(selector)).toEqual(original);
			for (const lane of [{ actorId: "new-actor" }, { sessionId: "new-session" }, { scope: "new-scope" }]) {
				expect((await store.readPartitionState({ ...selector, ...lane })).activeCount).toBe(0);
			}
			await store.remember({
				...memory("other", "owner-1", "1", "other"),
				gameTime: { timelineId: "world", generation: "2", tick: "1" },
			});
			expect(await store.readPartitionState(selector)).toEqual(original);
			const revised = await store.revise("first", "1", { searchText: "changed" }, "2026-08-28T00:00:01.000Z");
			const changed = await store.readPartitionState(selector);
			expect(changed.activeCount).toBe(1);
			expect(changed.revision).not.toBe(original.revision);
			await store.forget("first", revised.revision, "2026-08-28T00:00:02.000Z");
			const forgotten = await store.readPartitionState(selector);
			expect(forgotten.activeCount).toBe(0);
			expect(forgotten.revision).not.toBe(changed.revision);
			authority.close();
			authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
			store = new SqliteMemoryAuthority(authority, limits);
			expect(await store.readPartitionState(selector)).toEqual(forgotten);
			expect((await store.readPartitionState({ ...selector, generation: "2" })).activeCount).toBe(1);
		} finally {
			authority.close();
			await removeTemporaryRoot(root);
		}
	});

	it("rejects non-canonical memory time before durable mutation", async () => {
		const { authority, store } = createStore();
		try {
			await expect(
				store.remember({ ...memory("invalid-time", "owner-1", "1", "memory"), recordedAt: "2026-08-28" }),
			).rejects.toThrow("invalid_timestamp");
			expect((await store.exportPage(undefined, 8)).records).toEqual([]);
		} finally {
			authority.close();
		}
	});

	it("keeps identity idempotent and rejects conflicting reuse", async () => {
		const { authority, store } = createStore();
		try {
			const input = memory("memory-1", "owner-1", "1", "a remembered bridge");
			const first = await store.remember(input);
			const repeated = await store.remember(input);
			expect(repeated).toEqual(first);
			await expect(store.remember({ ...input, searchText: "different" })).rejects.toBeInstanceOf(MemoryConflictError);
		} finally {
			authority.close();
		}
	});

	it("filters lexical and hydrated candidates by generation, game time, tags, and audience", async () => {
		const { authority, store } = createStore();
		try {
			const visible = await store.remember({
				...memory("visible", "owner-1", "2", "the moon herb blooms beside the old bridge"),
				tags: ["herb", "quest"],
			});
			await store.remember({
				...memory("future", "owner-1", "20", "the moon herb blooms tomorrow"),
				tags: ["herb"],
			});
			await store.remember({
				...memory("rewound", "owner-1", "1", "the moon herb belonged to another generation"),
				gameTime: { timelineId: "world", generation: "2", tick: "1" },
			});
			await store.remember({
				...memory("private", "owner-1", "1", "the moon herb is classified"),
				audience: { visibility: "recipient", recipientIds: ["reader-2"] },
			});

			const selector = {
				ownerId: "owner-1",
				timelineId: "world",
				generation: "1",
				tags: ["herb"],
				atOrBeforeTick: "10",
			} as const;
			const candidates = await store.searchLexical(selector, { kind: "owner", ownerId: "owner-1" }, "moon herb", 8);
			expect(candidates.map((entry) => entry.memoryKey)).toEqual([visible.memoryKey]);
			const staleKeys = [visible.memoryKey, BigInt(2), BigInt(3), BigInt(4)];
			const hydrated = await store.hydrate(staleKeys, selector, { kind: "public" });
			expect(hydrated.map((record) => record.memoryId)).toEqual(["visible"]);
			expect((await store.get("private", { kind: "recipient", recipientId: "reader-2" }))?.memoryId).toBe("private");
			expect(await store.get("private", { kind: "public" })).toBeUndefined();
		} finally {
			authority.close();
		}
	});

	it("rejects a valid stored memory row whose authoritative fingerprint no longer matches", async () => {
		const { authority, store } = createStore();
		try {
			const stored = await store.remember(memory("tampered", "owner-1", "1", "moon herb"));
			authority.write((database) =>
				database
					.prepare("UPDATE oga_memories SET content_json = ? WHERE memory_key = ?")
					.run('{"fact":"tampered"}', stored.memoryKey),
			);

			await expect(
				store.hydrate(
					[stored.memoryKey],
					{ ownerId: "owner-1", timelineId: "world", generation: "1" },
					{ kind: "public" },
				),
			).rejects.toThrow("fingerprint");
		} finally {
			authority.close();
		}
	});

	it("uses the admitted viewer snapshot for authorization", async () => {
		const { authority, store } = createStore();
		try {
			await store.remember({ ...memory("secret", "owner-1", "1", "classified"), audience: { visibility: "internal" } });
			let reads = 0;
			const viewer = Object.defineProperty({}, "kind", {
				enumerable: true,
				get: () => (++reads === 1 ? "public" : "internal"),
			});
			expect(await store.get("secret", viewer as never)).toBeUndefined();
			expect(reads).toBe(1);
		} finally {
			authority.close();
		}
	});

	it("revises with CAS, stores portable embeddings, forgets, exports, and purges", async () => {
		const { authority, store } = createStore();
		try {
			const original = await store.remember(memory("memory-1", "owner-1", "1", "old text"));
			expect(
				await store.writeEmbedding({
					memoryKey: original.memoryKey,
					memoryRevision: original.revision,
					modelId: "embedding-model",
					dimensions: 3,
					vector: new Float32Array([1, 0.5, -0.25]),
					sourceFingerprint: "f".repeat(64),
					generatedAt: "2026-08-28T00:00:01.000Z",
				}),
			).toBe(true);
			const embedding = await store.readEmbedding(original.memoryKey, "1", "embedding-model");
			expect(Array.from(embedding?.vector ?? [])).toEqual([1, 0.5, -0.25]);

			const revised = await store.revise(
				"memory-1",
				"1",
				{
					searchText: "new text",
					content: { fact: "new" },
					gameTime: { timelineId: "world", generation: "1", tick: "2" },
				},
				"2026-08-28T00:00:02.000Z",
			);
			expect(revised.revision).toBe("2");
			expect(await store.readEmbedding(original.memoryKey, "1", "embedding-model")).toBeUndefined();
			expect(
				await store.searchLexical(
					{ ownerId: "owner-1", timelineId: "world", generation: "1" },
					{ kind: "public" },
					"old text",
					8,
				),
			).toEqual([]);
			expect(
				(
					await store.searchLexical(
						{ ownerId: "owner-1", timelineId: "world", generation: "1" },
						{ kind: "public" },
						"new text",
						8,
					)
				).map((candidate) => candidate.memoryKey),
			).toEqual([original.memoryKey]);
			await expect(store.forget("memory-1", "1", "2026-08-28T00:00:03.000Z")).rejects.toBeInstanceOf(
				MemoryRevisionConflictError,
			);
			const forgotten = await store.forget("memory-1", "2", "2026-08-28T00:00:03.000Z");
			expect(forgotten.lifecycle).toBe("forgotten");
			expect(
				await store.searchLexical(
					{ ownerId: "owner-1", timelineId: "world", generation: "1" },
					{ kind: "public" },
					"new text",
					8,
				),
			).toEqual([]);
			expect((await store.exportPage(undefined, 8)).records).toHaveLength(1);
			expect(await store.purgeForgotten("2026-08-29T00:00:00.000Z", 8)).toEqual([original.memoryKey]);
			expect((await store.exportPage(undefined, 8)).records).toHaveLength(0);
		} finally {
			authority.close();
		}
	});

	it("enforces lexical and hydration admission at the authority boundary", async () => {
		const { authority, store } = createStore();
		try {
			await expect(
				store.searchLexical({ ownerId: "owner-1", timelineId: "world", generation: "1" }, { kind: "internal" }, "", 1),
			).rejects.toThrow("invalid_query_bytes");
			await expect(
				store.hydrate(
					Array.from({ length: limits.maxHydrateCandidates + 1 }, (_, index) => BigInt(index + 1)),
					{ ownerId: "owner-1", timelineId: "world", generation: "1" },
					{ kind: "internal" },
				),
			).rejects.toThrow("invalid_hydrate_limit");
			await expect(
				store.searchLexical(
					{
						ownerId: "owner-1",
						timelineId: "world",
						generation: "1",
						kinds: Array.from({ length: limits.maxSelectorKinds + 1 }, (_, index) => `kind-${index}`),
					},
					{ kind: "internal" },
					"query",
					1,
				),
			).rejects.toThrow("too_many_selector_kinds");
		} finally {
			authority.close();
		}
	});

	it("filters a host-admitted large tag set without consuming SQLite expression depth", async () => {
		const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			const store = new SqliteMemoryAuthority(authority, { ...limits, maxSelectorTags: 1_001 });
			await expect(
				store.searchLexical(
					{
						ownerId: "owner-1",
						timelineId: "world",
						generation: "1",
						tags: Array.from({ length: 1_001 }, (_, index) => `tag-${index}`),
					},
					{ kind: "internal" },
					"memory",
					1,
				),
			).resolves.toEqual([]);
		} finally {
			authority.close();
		}
	});

	it("persists deeply nested content without recursive canonicalization", async () => {
		const { authority, store } = createStore();
		try {
			let content: unknown = 0;
			for (let depth = 0; depth < 4_000; depth += 1) content = [content];
			const stored = await store.remember({ ...memory("deep", "owner-1", "1", "deep"), content } as MemoryWrite);
			let cursor: unknown = stored.content;
			for (let depth = 0; depth < 4_000; depth += 1) {
				expect(Array.isArray(cursor)).toBe(true);
				cursor = (cursor as unknown[])[0];
			}
			expect(cursor).toBe(0);
		} finally {
			authority.close();
		}
	});

	it("rebuilds the disposable lexical projection from authoritative active memories after restart", async () => {
		const root = await mkdtemp(join(tmpdir(), "opengameagent-memory-lexical-rebuild-test-"));
		const path = join(root, "memory.sqlite3");
		let authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
		try {
			let store = new SqliteMemoryAuthority(authority, limits);
			const visible = await store.remember({
				...memory("visible", "owner-1", "1", "moon herb remedy"),
				tags: ["quest", "medicine"],
			});
			const forgotten = await store.remember(memory("forgotten", "owner-1", "1", "moon herb discarded"));
			await store.forget("forgotten", forgotten.revision, "2026-08-28T00:00:01.000Z");

			authority.write((database) => {
				database
					.prepare(
						`INSERT INTO oga_memory_fts(oga_memory_fts, rowid, search_text, tags)
						 VALUES ('delete', ?, ?, ?)`,
					)
					.run(visible.memoryKey, visible.searchText, visible.tags.join("\n"));
			});
			authority.close();
			authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
			store = new SqliteMemoryAuthority(authority, limits);
			expect(
				await store.searchLexical(
					{ ownerId: "owner-1", timelineId: "world", generation: "1" },
					{ kind: "public" },
					"moon herb",
					8,
				),
			).toEqual([]);
			await expect(store.rebuildLexicalIndex()).resolves.toBeUndefined();
			expect(
				(
					await store.searchLexical(
						{ ownerId: "owner-1", timelineId: "world", generation: "1", tags: ["medicine", "quest"] },
						{ kind: "public" },
						"moon herb",
						8,
					)
				).map((candidate) => candidate.memoryKey),
			).toEqual([visible.memoryKey]);
			expect(
				(
					await store.searchLexical(
						{ ownerId: "owner-1", timelineId: "world", generation: "1" },
						{ kind: "public" },
						"medicine",
						8,
					)
				).map((candidate) => candidate.memoryKey),
			).toEqual([visible.memoryKey]);

			authority.write((database) => {
				database.prepare("DELETE FROM oga_memory_search_documents WHERE memory_key = ?").run(visible.memoryKey);
			});
			authority.close();
			authority = new SqliteAuthority({ path, busyTimeoutMs: 0, verifyOnOpen: "full" });
			store = new SqliteMemoryAuthority(authority, limits);
			expect(
				await store.searchLexical(
					{ ownerId: "owner-1", timelineId: "world", generation: "1" },
					{ kind: "public" },
					"moon herb",
					8,
				),
			).toEqual([]);

			await expect(store.rebuildLexicalIndex()).resolves.toBeUndefined();
			expect(
				(
					await store.searchLexical(
						{ ownerId: "owner-1", timelineId: "world", generation: "1", tags: ["medicine"] },
						{ kind: "public" },
						"moon herb",
						8,
					)
				).map((candidate) => candidate.memoryKey),
			).toEqual([visible.memoryKey]);
			expect(
				(
					await store.searchLexical(
						{ ownerId: "owner-1", timelineId: "world", generation: "1" },
						{ kind: "public" },
						"quest",
						8,
					)
				).map((candidate) => candidate.memoryKey),
			).toEqual([visible.memoryKey]);
			expect(
				await store.searchLexical(
					{ ownerId: "owner-1", timelineId: "world", generation: "1" },
					{ kind: "public" },
					"discarded",
					8,
				),
			).toEqual([]);
		} finally {
			authority.close();
			await removeTemporaryRoot(root);
		}
	});

	it("rolls back the authoritative memory when its lexical trigger fails", async () => {
		const { authority, store } = createStore();
		try {
			authority.write((database) => database.prepare("DROP TABLE oga_memory_fts").run());
			await expect(store.remember(memory("rollback", "owner-1", "1", "not committed"))).rejects.toThrow();
			expect((await store.exportPage(undefined, 8)).records).toEqual([]);
		} finally {
			authority.close();
		}
	});

	it("rolls back a revision when lexical projection update fails", async () => {
		const { authority, store } = createStore();
		try {
			const original = await store.remember(memory("rollback-revise", "owner-1", "1", "original"));
			authority.write((database) => database.prepare("DROP TABLE oga_memory_fts").run());
			await expect(
				store.revise(
					original.memoryId,
					original.revision,
					{ searchText: "must roll back" },
					"2026-08-28T00:00:01.000Z",
				),
			).rejects.toThrow();
			expect(await store.get(original.memoryId, { kind: "public" })).toMatchObject({
				revision: original.revision,
				searchText: "original",
				lifecycle: "active",
			});
		} finally {
			authority.close();
		}
	});

	it("rolls back forgetting when lexical projection deletion fails", async () => {
		const { authority, store } = createStore();
		try {
			const original = await store.remember(memory("rollback-forget", "owner-1", "1", "original"));
			authority.write((database) => database.prepare("DROP TABLE oga_memory_fts").run());
			await expect(store.forget(original.memoryId, original.revision, "2026-08-28T00:00:01.000Z")).rejects.toThrow();
			expect(await store.get(original.memoryId, { kind: "public" })).toMatchObject({
				revision: original.revision,
				lifecycle: "active",
			});
		} finally {
			authority.close();
		}
	});
});

function createStore() {
	const authority = new SqliteAuthority({ path: ":memory:", busyTimeoutMs: 0, verifyOnOpen: "full" });
	return { authority, store: new SqliteMemoryAuthority(authority, limits) };
}

async function removeTemporaryRoot(root: string): Promise<void> {
	const target = resolve(root);
	const temporaryRoot = `${resolve(tmpdir())}${sep}`;
	if (!target.startsWith(temporaryRoot)) throw new Error("Refusing to remove a non-temporary test directory.");
	await rm(target, { recursive: true, force: true });
}

function memory(memoryId: string, ownerId: string, tick: string, searchText: string): MemoryWrite {
	return {
		memoryId,
		partition: { ownerId, actorId: "actor-1", sessionId: "session-1", scope: "world" },
		kind: "observation",
		tags: [],
		gameTime: { timelineId: "world", generation: "1", tick },
		importance: 0.5,
		searchText,
		content: { fact: searchText },
		audience: { visibility: "public" },
		recordedAt: "2026-08-28T00:00:00.000Z",
	};
}

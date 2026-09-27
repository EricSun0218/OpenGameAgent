import { mkdtemp, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import type { MemoryIndexPartition, MemoryVectorEntry } from "@opengameagent/memory";
import { describe, expect, it } from "vitest";
import { UsearchMemoryIndex } from "../src/index.js";

const partition: MemoryIndexPartition = { ownerId: "owner-1", timelineId: "world", generation: "1" };

describe("UsearchMemoryIndex", () => {
	it("restores only completed, unchanged snapshots and rejects mismatched identity or corrupted bytes", async () => {
		const root = await mkdtemp(join(tmpdir(), "opengameagent-usearch-checkpoint-"));
		try {
			const options = {
				modelId: "model",
				dimensions: 2,
				connectivity: 0,
				expansionAdd: 0,
				expansionSearch: 0,
				persistence: { directory: root, maxSnapshotBytes: 1024 * 1024 },
			};
			const index = new UsearchMemoryIndex(options);
			await index.replace(partition, entries([{ memoryKey: 1n, vector: new Float32Array([1, 0]) }]), "revision-1");
			expect(await index.restore(partition, "revision-1")).toBe(false);
			expect(await index.checkpoint(partition, "revision-1")).toBe(true);
			const restarted = new UsearchMemoryIndex(options);
			expect(await restarted.restore(partition, "revision-2")).toBe(false);
			expect(await restarted.restore(partition, "revision-1")).toBe(true);
			expect((await restarted.search(partition, new Float32Array([1, 0]), 1))[0]?.memoryKey).toBe(1n);
			expect(await new UsearchMemoryIndex({ ...options, modelId: "changed" }).restore(partition, "revision-1")).toBe(
				false,
			);
			expect(await new UsearchMemoryIndex(options).restore({ ...partition, generation: "2" }, "revision-1")).toBe(
				false,
			);
			await restarted.upsert(partition, { memoryKey: 2n, vector: new Float32Array([0, 1]) });
			expect(await restarted.checkpoint(partition, "revision-1")).toBe(false);
			const snapshot = (await readdir(root)).find((name) => name.endsWith(".usearch"));
			if (!snapshot) throw new Error("Expected completed native snapshot.");
			await writeFile(join(root, snapshot), "corrupted snapshot");
			expect(await new UsearchMemoryIndex(options).restore(partition, "revision-1")).toBe(false);
		} finally {
			await removeTemporaryRoot(root);
		}
	});

	it("enforces the host snapshot byte budget and cleans failed checkpoint temporaries", async () => {
		const root = await mkdtemp(join(tmpdir(), "opengameagent-usearch-budget-"));
		try {
			const index = new UsearchMemoryIndex({
				modelId: "model",
				dimensions: 2,
				connectivity: 0,
				expansionAdd: 0,
				expansionSearch: 0,
				persistence: { directory: root, maxSnapshotBytes: 1 },
			});
			await index.replace(partition, entries([{ memoryKey: 1n, vector: new Float32Array([1, 0]) }]), "revision");
			await expect(index.checkpoint(partition, "revision")).rejects.toThrow("usearch_snapshot_budget_exceeded");
			expect(await index.restore(partition, "revision")).toBe(false);
			expect(await readdir(root)).toEqual([]);
			expect(index.partitionSize(partition)).toBe(1);
		} finally {
			await removeTemporaryRoot(root);
		}
	});

	it("requires explicit host-selected HNSW tuning", () => {
		expect(
			() =>
				new UsearchMemoryIndex({ modelId: "model", dimensions: 3 } as ConstructorParameters<
					typeof UsearchMemoryIndex
				>[0]),
		).toThrow("invalid_usearch_connectivity");
	});

	it.each([
		[{ connectivity: -1, expansionAdd: 0, expansionSearch: 0 }, "invalid_usearch_connectivity"],
		[{ connectivity: 1, expansionAdd: 0, expansionSearch: 0 }, "invalid_usearch_connectivity"],
		[{ connectivity: 0, expansionAdd: 1.5, expansionSearch: 0 }, "invalid_usearch_expansion_add"],
		[{ connectivity: 0, expansionAdd: 0, expansionSearch: Number.NaN }, "invalid_usearch_expansion_search"],
	] as const)("rejects invalid HNSW tuning %o", (tuning, code) => {
		expect(() => new UsearchMemoryIndex({ modelId: "model", dimensions: 3, ...tuning })).toThrow(code);
	});

	it("captures host-selected HNSW tuning once at construction", () => {
		const reads = { connectivity: 0, expansionAdd: 0, expansionSearch: 0 };
		new UsearchMemoryIndex({
			modelId: "model",
			dimensions: 3,
			get connectivity() {
				reads.connectivity += 1;
				return 16;
			},
			get expansionAdd() {
				reads.expansionAdd += 1;
				return 128;
			},
			get expansionSearch() {
				reads.expansionSearch += 1;
				return 64;
			},
		} as ConstructorParameters<typeof UsearchMemoryIndex>[0]);

		expect(reads).toEqual({ connectivity: 1, expansionAdd: 1, expansionSearch: 1 });
	});

	it("isolates partitions and supports upsert/remove", async () => {
		const index = testIndex(3);
		await index.upsert(partition, { memoryKey: 1n, vector: new Float32Array([1, 0, 0]) });
		await index.upsert(partition, { memoryKey: 2n, vector: new Float32Array([0, 1, 0]) });
		await index.upsert(partition, { memoryKey: 2n, vector: new Float32Array([0.9, 0.1, 0]) });
		expect((await index.search(partition, new Float32Array([1, 0, 0]), 2)).map((entry) => entry.memoryKey)).toEqual([
			1n,
			2n,
		]);
		expect(await index.search({ ...partition, generation: "2" }, new Float32Array([1, 0, 0]), 2)).toEqual([]);
		await index.remove(partition, 1n);
		expect((await index.search(partition, new Float32Array([1, 0, 0]), 2)).map((entry) => entry.memoryKey)).toEqual([
			2n,
		]);
	});

	it("preserves the dependency's finite zero-vector semantics", async () => {
		const index = testIndex(3);
		await index.upsert(partition, { memoryKey: 1n, vector: new Float32Array([0, 0, 0]) });
		expect(index.partitionSize(partition)).toBe(1);
		expect((await index.search(partition, new Float32Array([0, 0, 0]), 1))[0]?.memoryKey).toBe(1n);
	});

	it("replaces a partition atomically", async () => {
		const index = testIndex(2);
		await index.upsert(partition, { memoryKey: 1n, vector: new Float32Array([1, 0]) });
		await expect(index.replace(partition, failingEntries())).rejects.toThrow("rebuild failed");
		expect(index.partitionSize(partition)).toBe(1);
		await index.replace(partition, entries([{ memoryKey: 2n, vector: new Float32Array([0, 1]) }]));
		expect(index.partitionSize(partition)).toBe(1);
		expect((await index.search(partition, new Float32Array([0, 1]), 1))[0]?.memoryKey).toBe(2n);
	});

	it("serializes mutations that arrive while a partition is rebuilding", async () => {
		const index = testIndex(2);
		await index.upsert(partition, { memoryKey: 1n, vector: new Float32Array([1, 0]) });
		await index.upsert(partition, { memoryKey: 3n, vector: new Float32Array([1, 1]) });
		const rebuild = controlledEntries();
		const replacing = index.replace(partition, rebuild.entries);
		await rebuild.started;

		const upserting = index.upsert(partition, { memoryKey: 3n, vector: new Float32Array([0.5, 0.5]) });
		const removing = index.remove(partition, 1n);
		rebuild.release();
		await Promise.all([replacing, upserting, removing]);

		expect(index.partitionSize(partition)).toBe(2);
		expect(
			(await index.search(partition, new Float32Array([0, 1]), 2)).map((candidate) => candidate.memoryKey).sort(),
		).toEqual([2n, 3n]);
	});
});

async function removeTemporaryRoot(root: string): Promise<void> {
	const target = resolve(root);
	if (!target.startsWith(`${resolve(tmpdir())}${sep}`)) throw new Error("Refusing non-temporary test cleanup.");
	await rm(target, { recursive: true, force: true });
}

function testIndex(dimensions: number): UsearchMemoryIndex {
	return new UsearchMemoryIndex({ modelId: "model", dimensions, connectivity: 0, expansionAdd: 0, expansionSearch: 0 });
}

async function* entries(values: readonly MemoryVectorEntry[]): AsyncGenerator<MemoryVectorEntry> {
	for (const value of values) yield value;
}

async function* failingEntries(): AsyncGenerator<MemoryVectorEntry> {
	yield { memoryKey: 2n, vector: new Float32Array([0, 1]) };
	throw new Error("rebuild failed");
}

function controlledEntries(): {
	readonly entries: AsyncGenerator<MemoryVectorEntry>;
	readonly started: Promise<void>;
	release(): void;
} {
	let markStarted: (() => void) | undefined;
	let release: (() => void) | undefined;
	const started = new Promise<void>((resolve) => {
		markStarted = resolve;
	});
	const blocked = new Promise<void>((resolve) => {
		release = resolve;
	});
	return {
		entries: (async function* () {
			yield { memoryKey: 1n, vector: new Float32Array([1, 0]) };
			markStarted?.();
			await blocked;
			yield { memoryKey: 2n, vector: new Float32Array([0, 1]) };
		})(),
		started,
		release: () => release?.(),
	};
}

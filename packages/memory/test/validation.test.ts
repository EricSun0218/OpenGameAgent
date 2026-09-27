import { describe, expect, it } from "vitest";
import {
	type MemoryLimits,
	type MemoryWrite,
	normalizeMemorySelector,
	normalizeMemoryViewer,
	normalizeMemoryWrite,
	validateMemoryLimits,
	validateVector,
} from "../src/index.js";

const limits: MemoryLimits = {
	maxSearchTextBytes: 2,
	maxContentBytes: 64,
	maxTagsPerMemory: 2,
	maxTagBytes: 8,
	maxRecipientsPerMemory: 2,
	maxQueryTextBytes: 8,
	maxSelectorKinds: 2,
	maxSelectorTags: 2,
	maxLexicalCandidates: 2,
	maxVectorCandidates: 2,
	maxHydrateCandidates: 2,
	maxRecallResults: 2,
	maxIndexBatchSize: 2,
	maxTrackedIndexPartitions: 2,
	maxExportPageSize: 2,
	maxPurgeBatchSize: 2,
};

describe("memory validation", () => {
	it("requires a positive host capacity for tracked index partitions", () => {
		expect(() => validateMemoryLimits({ ...limits, maxTrackedIndexPartitions: 0 })).toThrow(
			"invalid_limit:maxTrackedIndexPartitions",
		);
	});

	it("captures caller fields once before applying byte budgets", () => {
		let reads = 0;
		const write = baseWrite() as MemoryWrite & { searchText: string };
		Object.defineProperty(write, "searchText", {
			enumerable: true,
			get: () => (++reads === 1 ? "ok" : "x".repeat(100)),
		});

		const normalized = normalizeMemoryWrite(write, limits);
		expect(normalized.searchText).toBe("ok");
		expect(reads).toBe(1);
	});

	it("charges raw set cardinality before deterministic de-duplication", () => {
		const normalized = normalizeMemoryWrite(
			{
				...baseWrite(),
				tags: ["b", "b"],
				audience: { visibility: "recipient", recipientIds: ["reader", "reader"] },
			},
			limits,
		);
		expect(normalized.tags).toEqual(["b"]);
		expect(normalized.audience).toEqual({ visibility: "recipient", recipientIds: ["reader"] });
		expect(
			normalizeMemorySelector(
				{
					ownerId: "owner",
					timelineId: "world",
					generation: "1",
					kinds: ["z", "z"],
					tags: ["b", "b"],
				},
				limits,
			),
		).toMatchObject({ kinds: ["z"], tags: ["b"] });
		expect(() => normalizeMemoryWrite({ ...baseWrite(), tags: ["a", "a", "a"] }, limits)).toThrow("too_many_tags");
	});

	it("accepts finite zero vectors and rejects invalid viewers", () => {
		expect(() => validateVector(new Float32Array([0, 0]), 2)).not.toThrow();
		expect(() => validateVector(new Float32Array([0, Number.NaN]), 2)).toThrow("embedding_value");
		expect(() => normalizeMemoryViewer({ kind: "administrator" } as never)).toThrow("invalid_viewer");
	});
});

function baseWrite(): MemoryWrite {
	return {
		memoryId: "memory",
		partition: { ownerId: "owner", scope: "world" },
		kind: "observation",
		tags: [],
		gameTime: { timelineId: "world", generation: "1", tick: "0" },
		importance: 0.5,
		searchText: "ok",
		content: { fact: "ok" },
		audience: { visibility: "public" },
		recordedAt: "2026-08-29T00:00:00.000Z",
	};
}

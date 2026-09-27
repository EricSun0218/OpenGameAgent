import { createHash } from "node:crypto";
import { canonicalizeJsonValue, type RunCoordinate } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import {
	GameContextSnapshotError,
	prepareGameContextSnapshot,
	validateContextConfiguration,
} from "../src/context-snapshot.js";
import type { GameContextLimits, GameContextReference, GameContextSource, NormalizedGameInput } from "../src/types.js";

const INPUT: NormalizedGameInput = {
	sessionId: "session",
	actorId: "actor",
	inputId: "input",
	payload: { instruction: "act" },
};

const LIMITS: GameContextLimits = {
	maxReferencesPerSnapshot: 3,
	maxSnapshotBytes: 1_024,
};

const COORDINATE: RunCoordinate = {
	sessionId: "session",
	actorId: "actor",
	runId: "run",
	turn: "1",
};

function source(sourceId: string, references: readonly GameContextReference[]): GameContextSource {
	return { sourceId, load: () => ({ references }) };
}

describe("game context snapshots", () => {
	it("preserves configured source and ranked reference order in canonical JSON", async () => {
		const sources = [
			source("world", [
				{ referenceId: "place-2", revision: "4", content: { z: true, a: "second" } },
				{ referenceId: "place-1", revision: "7", content: { label: "first" } },
			]),
			source("memory", [{ referenceId: "memory-9", revision: "2", content: ["ranked", 9] }]),
		];
		const snapshot = await prepareGameContextSnapshot(sources, LIMITS, INPUT, new AbortController().signal, COORDINATE);

		expect(snapshot?.text).toBe(
			'{"contextVersion":"1","kind":"game-reference-context","references":[{"content":{"a":"second","z":true},"referenceId":"place-2","revision":"4","sourceId":"world"},{"content":{"label":"first"},"referenceId":"place-1","revision":"7","sourceId":"world"},{"content":["ranked",9],"referenceId":"memory-9","revision":"2","sourceId":"memory"}]}',
		);
		expect(snapshot?.provenance.references.map(({ sourceId, referenceId }) => [sourceId, referenceId])).toEqual([
			["world", "place-2"],
			["world", "place-1"],
			["memory", "memory-9"],
		]);
		expect(snapshot?.provenance.fingerprint).toBe(
			createHash("sha256")
				.update(canonicalizeJsonValue({ text: snapshot?.text ?? "", images: [] }))
				.digest("hex"),
		);
	});

	it("requires host limits and rejects duplicate source or reference identity", async () => {
		const first = source("memory", [{ referenceId: "same", revision: "1", content: null }]);
		expect(() => validateContextConfiguration([first], undefined)).toThrowError(
			new GameContextSnapshotError("invalid_context_configuration"),
		);
		expect(() => validateContextConfiguration([first, source("memory", [])], LIMITS)).toThrowError(
			new GameContextSnapshotError("duplicate_context_source"),
		);
		await expect(
			prepareGameContextSnapshot(
				[
					source("memory", [
						{ referenceId: "same", revision: "1", content: null },
						{ referenceId: "same", revision: "2", content: null },
					]),
				],
				LIMITS,
				INPUT,
				new AbortController().signal,
				COORDINATE,
			),
		).rejects.toMatchObject({ code: "duplicate_context_reference" });
	});

	it("fails instead of truncating host reference and UTF-8 byte budgets", async () => {
		const references = [
			{ referenceId: "one", revision: "1", content: "一" },
			{ referenceId: "two", revision: "1", content: "二" },
		];
		await expect(
			prepareGameContextSnapshot(
				[source("memory", references)],
				{ ...LIMITS, maxReferencesPerSnapshot: 1 },
				INPUT,
				new AbortController().signal,
				COORDINATE,
			),
		).rejects.toMatchObject({ code: "context_reference_limit_exceeded" });
		await expect(
			prepareGameContextSnapshot(
				[source("memory", [references[0] as (typeof references)[number]])],
				{ ...LIMITS, maxSnapshotBytes: 1 },
				INPUT,
				new AbortController().signal,
				COORDINATE,
			),
		).rejects.toMatchObject({ code: "context_snapshot_byte_limit_exceeded" });
	});

	it("captures each source contribution before loading the next source and deeply freezes provenance", async () => {
		const mutable = {
			referenceId: "fact",
			revision: "1",
			content: { nested: { value: "before" } },
		};
		const snapshot = await prepareGameContextSnapshot(
			[
				source("first", [mutable]),
				{
					sourceId: "second",
					load: () => {
						mutable.revision = "2";
						mutable.content.nested.value = "after";
						return { references: [] };
					},
				},
			],
			LIMITS,
			INPUT,
			new AbortController().signal,
			COORDINATE,
		);

		expect(JSON.parse(snapshot?.text ?? "null")).toMatchObject({
			references: [{ revision: "1", content: { nested: { value: "before" } } }],
		});
		expect(Object.isFrozen(snapshot?.provenance)).toBe(true);
		expect(Object.isFrozen(snapshot?.provenance.references)).toBe(true);
		expect(Object.isFrozen(snapshot?.provenance.references[0])).toBe(true);
		expect(() => {
			(snapshot?.provenance.references[0] as unknown as { revision: string }).revision = "rewritten";
		}).toThrow();
		expect(snapshot?.provenance.references[0]?.revision).toBe("1");
	});

	it("propagates cancellation through ordered context sources", async () => {
		const controller = new AbortController();
		const calls: string[] = [];
		const sources: GameContextSource[] = [
			{
				sourceId: "first",
				load: (_input, signal) => {
					calls.push("first");
					controller.abort(new Error("cancelled"));
					signal.throwIfAborted();
					return { references: [] };
				},
			},
			{
				sourceId: "second",
				load: () => {
					calls.push("second");
					return { references: [] };
				},
			},
		];

		await expect(prepareGameContextSnapshot(sources, LIMITS, INPUT, controller.signal, COORDINATE)).rejects.toThrow(
			"cancelled",
		);
		expect(calls).toEqual(["first"]);
	});
});

import { describe, expect, it, vi } from "vitest";
import type { NormalizedGameInput } from "@opengameagent/agent";
import { canonicalizeJsonValue, type RunCoordinate } from "@opengameagent/protocol";
import {
	createGameWorldbookContextSource,
	GameWorldbookError,
	type GameWorldbookEntryV1,
	type GameWorldbookLimits,
	type GameWorldbookV1,
} from "../src/index.js";

const INPUT: NormalizedGameInput = {
	sessionId: "session-1",
	actorId: "healer-lin",
	inputId: "input-1",
	payload: { text: "The fever returned." },
};
const COORDINATE: RunCoordinate = { ...INPUT, runId: "run-1", turn: "1" };
const LIMITS: GameWorldbookLimits = {
	maxIdentifierBytes: 64,
	maxDefinitionBytes: 16_384,
	maxEntries: 32,
	maxKeywordsPerEntry: 16,
	maxKeywordBytes: 128,
	maxContentBytes: 2_048,
	maxScanSegments: 16,
	maxScanBytes: 4_096,
	maxScanTags: 16,
	maxMatchEvaluations: 1_024,
	maxRecursionSteps: 8,
	maxActivatedEntries: 16,
	maxContextBytes: 8_192,
	maxContextTokenUpperBound: 2_048,
};

const keyword = (
	entryId: string,
	content: string,
	primary: readonly string[],
	options: Partial<GameWorldbookEntryV1> = {},
): GameWorldbookEntryV1 => ({
	entryId,
	revision: `${entryId}-1`,
	priority: 0,
	content,
	recursive: false,
	activation: { kind: "keywords", phase: "any", caseSensitive: false, primary },
	...options,
});

const constant = (entryId: string, content: string, priority = 0): GameWorldbookEntryV1 => ({
	entryId,
	revision: `${entryId}-1`,
	priority,
	content,
	recursive: false,
	activation: { kind: "constant" },
});

const WORLD: GameWorldbookV1 = {
	worldbookVersion: "1",
	worldbookId: "jianghu-world",
	revision: "world-4",
	entries: [
		{ ...constant("medical-law", "A physician must obtain consent from a conscious patient.", 20), revision: "law-2" },
		keyword("red-fever", "Red fever is associated with the northern marsh.", ["fever"], {
			priority: 10,
			activation: {
				kind: "keywords",
				phase: "initial",
				caseSensitive: false,
				primary: ["fever"],
				secondary: { mode: "all", keywords: ["red", "rash"] },
			},
			scope: { actorIds: ["healer-lin"], requiredTags: ["chapter-marsh"] },
		}),
		keyword("unrelated", "The capital changed its tax seals.", ["tax"]),
	],
};

function source(
	worldbook: GameWorldbookV1 = WORLD,
	overrides: Partial<Parameters<typeof createGameWorldbookContextSource>[0]> = {},
) {
	return createGameWorldbookContextSource({
		sourceId: "game-worldbook",
		worldbook,
		limits: LIMITS,
		resolveScan: vi.fn(async () => ({
			scanVersion: "1" as const,
			revision: "scan-9",
			segments: [{ segmentId: "player-input", text: "The red fever produced a rash overnight." }],
			tags: ["chapter-marsh"],
		})),
		countTokenUpperBound: vi.fn(async (text: string) => new TextEncoder().encode(text).byteLength),
		...overrides,
	});
}

describe("createGameWorldbookContextSource", () => {
	it("activates constant and scoped primary/secondary entries in deterministic priority order", async () => {
		const contextSource = source();

		const selection = await contextSource.select(INPUT, new AbortController().signal, COORDINATE);

		expect(selection.activated.map((entry) => [entry.entryId, entry.reason.kind])).toEqual([
			["medical-law", "constant"],
			["red-fever", "keyword"],
		]);
		expect(selection.selected.map((entry) => entry.entryId)).toEqual(["medical-law", "red-fever"]);
		expect(selection.omitted).toEqual([]);
		expect(selection.contribution.references).toHaveLength(1);
		expect(Object.isFrozen(selection)).toBe(true);
		expect(Object.isFrozen(selection.activated)).toBe(true);
		expect(Object.isFrozen(selection.activated[0]?.reason)).toBe(true);
		expect(selection.contribution.references[0]).toMatchObject({ referenceId: "jianghu-world" });
		expect(selection.contribution.references[0]?.content).toMatchObject({
			contextVersion: "1",
			kind: "game-worldbook",
			worldbookId: "jianghu-world",
			worldbookRevision: "world-4",
			scanRevision: "scan-9",
			entries: [
				{ entryId: "medical-law", revision: "law-2" },
				{ entryId: "red-fever", revision: "red-fever-1" },
			],
		});
		expect(await contextSource.load(INPUT, new AbortController().signal, COORDINATE)).toEqual(selection.contribution);
		expect(contextSource.provenanceKind).toBe("context");
	});

	it("supports deterministic recursive chains without rescanning through Pi or another Agent loop", async () => {
		const worldbook: GameWorldbookV1 = {
			worldbookVersion: "1",
			worldbookId: "recursive-world",
			revision: "1",
			entries: [
				keyword("plague", "The cure is hidden at Moon Well.", ["plague"], { recursive: true, priority: 10 }),
				keyword("moon-well", "Moon Well opens only under a bronze moon.", ["moon well"], {
					activation: { kind: "keywords", phase: "recursive", caseSensitive: false, primary: ["moon well"] },
					recursive: true,
					priority: 9,
				}),
				keyword("bronze-moon", "A bronze moon is visible after the bell ritual.", ["bronze moon"], {
					activation: { kind: "keywords", phase: "recursive", caseSensitive: false, primary: ["bronze moon"] },
					priority: 8,
				}),
			],
		};
		const contextSource = source(worldbook, {
			resolveScan: async () => ({
				scanVersion: "1",
				revision: "scan-1",
				segments: [{ segmentId: "quest", text: "The plague is spreading." }],
				tags: [],
			}),
		});

		const selection = await contextSource.select(INPUT, new AbortController().signal, COORDINATE);

		expect(selection.activated.map((entry) => entry.entryId)).toEqual(["plague", "moon-well", "bronze-moon"]);
		expect(selection.activated.map((entry) => entry.reason)).toEqual([
			expect.objectContaining({ kind: "keyword", phase: "initial", segmentId: "quest" }),
			expect.objectContaining({ kind: "keyword", phase: "recursive", segmentId: "plague" }),
			expect.objectContaining({ kind: "keyword", phase: "recursive", segmentId: "moon-well" }),
		]);
	});

	it("fails closed when one more recursive step would activate content", async () => {
		const worldbook: GameWorldbookV1 = {
			worldbookVersion: "1",
			worldbookId: "bounded-world",
			revision: "1",
			entries: [
				keyword("first", "second-key", ["first-key"], { recursive: true }),
				keyword("second", "third-key", ["second-key"], {
					activation: { kind: "keywords", phase: "recursive", caseSensitive: true, primary: ["second-key"] },
					recursive: true,
				}),
				keyword("third", "end", ["third-key"], {
					activation: { kind: "keywords", phase: "recursive", caseSensitive: true, primary: ["third-key"] },
				}),
			],
		};
		const contextSource = source(worldbook, {
			limits: { ...LIMITS, maxRecursionSteps: 1 },
			resolveScan: async () => ({
				scanVersion: "1",
				revision: "scan",
				segments: [{ segmentId: "input", text: "first-key" }],
				tags: [],
			}),
		});

		await expect(contextSource.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toMatchObject({
			code: "recursion_limit_exceeded",
		});
	});

	it("uses the exact counter on each candidate body and lets a smaller lower-priority entry fit", async () => {
		const worldbook: GameWorldbookV1 = {
			worldbookVersion: "1",
			worldbookId: "budget-world",
			revision: "1",
			entries: [constant("large", "x".repeat(200), 10), constant("small", "small", 9)],
		};
		const counted: string[] = [];
		const contextSource = source(worldbook, {
			limits: { ...LIMITS, maxContextTokenUpperBound: 1 },
			countTokenUpperBound: async (text) => {
				counted.push(text);
				return text.includes('"entryId":"large"') ? 2 : 1;
			},
		});

		const selection = await contextSource.select(INPUT, new AbortController().signal, COORDINATE);

		expect(selection.selected.map((entry) => entry.entryId)).toEqual(["small"]);
		expect(selection.omitted).toEqual([{ entryId: "large", reason: "token_budget" }]);
		expect(selection.contextTokenUpperBound).toBe(1);
		expect(counted).toHaveLength(2);
		expect(counted[0]).toContain('"entryId":"large"');
		expect(counted[1]).toContain('"entryId":"small"');
	});

	it("does not call the token upper-bound counter for a candidate that already exceeds the byte budget", async () => {
		const countTokenUpperBound = vi.fn(async () => 0);
		const contextSource = source(undefined, {
			limits: { ...LIMITS, maxContextBytes: 1 },
			countTokenUpperBound,
		});

		const selection = await contextSource.select(INPUT, new AbortController().signal, COORDINATE);

		expect(selection.selected).toEqual([]);
		expect(selection.omitted).toEqual([
			{ entryId: "medical-law", reason: "byte_budget" },
			{ entryId: "red-fever", reason: "byte_budget" },
		]);
		expect(countTokenUpperBound).not.toHaveBeenCalled();
	});

	it("preserves definition order when activated entries have the same priority", async () => {
		const worldbook = { ...WORLD, entries: [constant("second", "two"), constant("first", "one")] };
		const selection = await source(worldbook).select(INPUT, new AbortController().signal, COORDINATE);

		expect(selection.selected.map((entry) => entry.entryId)).toEqual(["second", "first"]);
	});

	it("applies actor and tag scope before keyword matching", async () => {
		const scoped = keyword("scoped", "visible", ["fever"], {
			scope: {
				actorIds: ["other-actor"],
				requiredTags: ["chapter-marsh"],
				excludedTags: ["sealed"],
			},
		});
		const contextSource = source({ ...WORLD, entries: [scoped] });

		const selection = await contextSource.select(INPUT, new AbortController().signal, COORDINATE);

		expect(selection.activated).toEqual([]);
		expect(selection.contribution).toEqual({ references: [] });
	});

	it("supports explicit case and secondary any/all/none logic", async () => {
		const entries: GameWorldbookEntryV1[] = [
			keyword("case-sensitive", "no", ["FEVER"], {
				activation: { kind: "keywords", phase: "initial", caseSensitive: true, primary: ["FEVER"] },
			}),
			keyword("any", "yes", ["fever"], {
				activation: {
					kind: "keywords",
					phase: "initial",
					caseSensitive: false,
					primary: ["fever"],
					secondary: { mode: "any", keywords: ["absent", "rash"] },
				},
			}),
			keyword("none", "no", ["fever"], {
				activation: {
					kind: "keywords",
					phase: "initial",
					caseSensitive: false,
					primary: ["fever"],
					secondary: { mode: "none", keywords: ["rash"] },
				},
			}),
		];
		const contextSource = source({ ...WORLD, entries });

		const selection = await contextSource.select(INPUT, new AbortController().signal, COORDINATE);

		expect(selection.activated.map((entry) => entry.entryId)).toEqual(["any"]);
	});

	it.each([
		["scan segments", { maxScanSegments: 0 }, "scan_capacity"],
		["scan bytes", { maxScanBytes: 1 }, "scan_capacity"],
		["zero scan bytes", { maxScanBytes: 0 }, "scan_capacity"],
		["scan tags", { maxScanTags: 0 }, "scan_capacity"],
		["match evaluations", { maxMatchEvaluations: 1 }, "match_capacity"],
		["activated entries", { maxActivatedEntries: 1 }, "activation_capacity"],
	] as const)("fails closed on the host %s budget", async (_name, override, code) => {
		const contextSource = source(WORLD, { limits: { ...LIMITS, ...override } });
		await expect(contextSource.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toMatchObject({ code });
	});

	it("rejects invalid worldbook structure, duplicate entries, and unknown fields at construction", () => {
		for (const worldbook of [
			{ ...WORLD, worldbookVersion: "2" },
			{ ...WORLD, entries: [WORLD.entries[0], WORLD.entries[0]] },
			{ ...WORLD, externalSource: "unsupported.json" },
			{ ...WORLD, entries: [{ ...WORLD.entries[0], probability: 50 }] },
			{ ...WORLD, entries: [keyword("empty-keywords", "fact", [])] },
		]) {
			expect(() => source(worldbook as GameWorldbookV1)).toThrow(
				Object.assign(new GameWorldbookError("invalid_worldbook"), { name: "GameWorldbookError" }),
			);
		}
	});

	it("rejects inherited definitions and Proxy arrays instead of treating them as snapshots", () => {
		const inherited = Object.create(WORLD) as GameWorldbookV1;
		const proxiedEntries = new Proxy([...WORLD.entries], {});
		const accessorActorIds = ["healer-lin"];
		Object.defineProperty(accessorActorIds, 0, { enumerable: true, get: () => "healer-lin" });
		const accessorScope = {
			...WORLD,
			entries: [{ ...constant("scoped", "fact"), scope: { actorIds: accessorActorIds } }],
		};

		for (const worldbook of [inherited, { ...WORLD, entries: proxiedEntries }, accessorScope]) {
			expect(() => source(worldbook)).toThrow(
				Object.assign(new GameWorldbookError("invalid_worldbook"), { name: "GameWorldbookError" }),
			);
		}
	});

	it("rejects a scope item count that cannot fit the definition budget before reading its elements", () => {
		const actorIds = new Array<string>(LIMITS.maxDefinitionBytes + 1);
		Object.defineProperty(actorIds, 0, {
			get() {
				throw new Error("scope item must not be read after structural budget failure");
			},
		});
		const entry = { ...constant("scoped", "fact"), scope: { actorIds } };

		expect(() => source({ ...WORLD, entries: [entry] })).toThrow(
			Object.assign(new GameWorldbookError("worldbook_capacity"), { name: "GameWorldbookError" }),
		);
	});

	it("captures array inputs without invoking host-defined iteration or mapping hooks", () => {
		const entries = [constant("fact", "Developer-owned fact")];
		Object.defineProperties(entries, {
			[Symbol.iterator]: {
				value: () => {
					throw new Error("host iterator must not run");
				},
			},
			map: {
				value: () => {
					throw new Error("host map must not run");
				},
			},
		});

		expect(() => source({ ...WORLD, entries })).not.toThrow();
	});

	it("charges maxDefinitionBytes against the exact public V1 definition without internal ordering metadata", () => {
		const worldbook: GameWorldbookV1 = {
			worldbookVersion: "1",
			worldbookId: "w",
			revision: "r",
			entries: [constant("entry", "fact")],
		};
		const exactBytes = new TextEncoder().encode(canonicalizeJsonValue(worldbook)).byteLength;

		expect(() => source(worldbook, { limits: { ...LIMITS, maxDefinitionBytes: exactBytes } })).not.toThrow();
		expect(() => source(worldbook, { limits: { ...LIMITS, maxDefinitionBytes: exactBytes - 1 } })).toThrow(
			Object.assign(new GameWorldbookError("worldbook_capacity"), { name: "GameWorldbookError" }),
		);
	});

	it("fails construction when the identifier budget cannot represent a generated SHA-256 revision", () => {
		expect(() => source(WORLD, { limits: { ...LIMITS, maxIdentifierBytes: 63 } })).toThrow(
			Object.assign(new GameWorldbookError("invalid_configuration"), { name: "GameWorldbookError" }),
		);
	});

	it("classifies definition and scan identifier byte excess as their owning capacity", async () => {
		const overlong = "x".repeat(LIMITS.maxIdentifierBytes + 1);
		expect(() => source({ ...WORLD, entries: [{ ...constant("entry", "fact"), entryId: overlong }] })).toThrow(
			Object.assign(new GameWorldbookError("worldbook_capacity"), { name: "GameWorldbookError" }),
		);

		const contextSource = source(undefined, {
			resolveScan: async () => ({ scanVersion: "1", revision: overlong, segments: [], tags: [] }),
		});
		await expect(contextSource.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toMatchObject({
			code: "scan_capacity",
		});
	});

	it.each([Number.NaN, 0.5, -0])("rejects an invalid conservative token upper bound: %s", async (value) => {
		const contextSource = source(undefined, { countTokenUpperBound: async () => value });
		await expect(contextSource.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toMatchObject({
			code: "invalid_token_upper_bound",
		});
	});

	it("rejects negative zero in a nonnegative host limit", () => {
		expect(() => source(undefined, { limits: { ...LIMITS, maxContextTokenUpperBound: -0 } })).toThrow(
			Object.assign(new GameWorldbookError("invalid_configuration"), { name: "GameWorldbookError" }),
		);
	});

	it("propagates the exact abort reason from a stalled scan resolver or token counter", async () => {
		for (const stalled of ["scan", "tokens"] as const) {
			let reached: (() => void) | undefined;
			const waiting = new Promise<void>((resolve) => {
				reached = resolve;
			});
			const contextSource = source(undefined, {
				...(stalled === "scan"
					? {
							resolveScan: async () => {
								reached?.();
								return await new Promise<never>(() => undefined);
							},
						}
					: {
							countTokenUpperBound: async () => {
								reached?.();
								return await new Promise<never>(() => undefined);
							},
						}),
			});
			const controller = new AbortController();
			const reason = new Error(`stop ${stalled}`);
			const pending = contextSource.load(INPUT, controller.signal, COORDINATE);
			await waiting;
			controller.abort(reason);
			await expect(pending).rejects.toBe(reason);
		}
	});

	it("rejects inherited scan snapshots instead of reading prototype fields", async () => {
		const inherited = Object.create({
			scanVersion: "1",
			revision: "scan",
			segments: [],
			tags: [],
		});
		const accessor = { scanVersion: "1" as const, revision: "scan", segments: [], tags: [] };
		Object.defineProperty(accessor, "segments", { enumerable: true, get: () => [] });
		for (const scan of [inherited, accessor]) {
			const contextSource = source(undefined, { resolveScan: async () => scan });
			await expect(contextSource.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toMatchObject({
				code: "invalid_scan",
			});
		}
	});
});

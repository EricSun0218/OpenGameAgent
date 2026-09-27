import { Buffer } from "node:buffer";
import { canonicalizeJsonValue } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import {
	boundedToolExecutionError,
	GameToolResultError,
	MIN_TOOL_RESULT_BYTES,
	MIN_TOOL_RESULT_DEPTH,
	prepareToolResultSnapshot,
	validateToolSnapshotLimits,
} from "../src/index.js";
import { prepareTransformableToolResultSnapshot, textToolResultContentBytes } from "../src/tool-result-snapshot.js";
import type { GameToolSnapshotLimits } from "../src/types.js";

const LIMITS = Object.freeze({
	maxTools: 4,
	maxCatalogBytes: 4_096,
	maxSchemaDepth: 8,
	maxRequestSources: 4,
	maxPolicyRevisionBytes: 128,
	maxArgumentsBytes: 1_024,
	maxArgumentsDepth: 8,
	maxResultContentParts: 4,
	maxResultBytes: 4_096,
	maxResultDepth: 8,
	maxResultUpdates: 4,
}) satisfies GameToolSnapshotLimits;

describe("Tool result snapshot", () => {
	it("detaches and deeply freezes every Pi Tool result field", () => {
		const details = JSON.parse('{"nested":{"value":1},"__proto__":{"safe":true}}') as Record<string, unknown>;
		const result = {
			content: [{ type: "text" as const, text: "before" }],
			details,
			usage: {
				input: 1,
				output: 2,
				cacheRead: 3,
				cacheWrite: 4,
				cacheWrite1h: 2,
				reasoning: 1,
				totalTokens: 10,
				cost: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4, total: 10 },
			},
			addedToolNames: ["act"],
			terminate: true,
		};
		const snapshot = prepareToolResultSnapshot(result, LIMITS);

		const firstContent = result.content[0];
		if (!firstContent) throw new Error("Expected result content fixture.");
		firstContent.text = "after";
		(result.details.nested as { value: number }).value = 2;
		result.usage.cost.total = 0;
		result.addedToolNames[0] = "inspect";

		expect(snapshot).toEqual({
			content: [{ type: "text", text: "before" }],
			details: expect.objectContaining({ nested: { value: 1 } }),
			usage: {
				input: 1,
				output: 2,
				cacheRead: 3,
				cacheWrite: 4,
				cacheWrite1h: 2,
				reasoning: 1,
				totalTokens: 10,
				cost: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4, total: 10 },
			},
			addedToolNames: ["act"],
			terminate: true,
		});
		expect(Object.hasOwn(snapshot.details as object, "__proto__")).toBe(true);
		expect(Object.isFrozen(snapshot)).toBe(true);
		expect(Object.isFrozen(snapshot.content)).toBe(true);
		expect(Object.isFrozen(snapshot.content[0])).toBe(true);
		expect(Object.isFrozen((snapshot.details as { nested: object }).nested)).toBe(true);
		expect(Object.isFrozen(snapshot.usage?.cost)).toBe(true);
		expect(Object.isFrozen(snapshot.addedToolNames)).toBe(true);
	});

	it("accepts Pi image strings and preserves undefined details", () => {
		const data = Buffer.from("image fixture").toString("base64");
		const snapshot = prepareToolResultSnapshot(
			{
				content: [{ type: "image", data: `${data}=`, mimeType: "image/png", providerMetadata: true }],
				details: undefined,
			},
			LIMITS,
		);
		expect(snapshot).toEqual({
			content: [{ type: "image", data: `${data}=`, mimeType: "image/png" }],
			details: undefined,
		});
		expect(Object.hasOwn(snapshot, "details")).toBe(true);
	});

	it("structurally snapshots class, accessor, Proxy, array-subclass, usage, and dynamic-name fields", () => {
		class ResultContent extends Array<{ type: "text"; text: string; ignored: boolean }> {}
		let contentReads = 0;
		const source = new (class {
			readonly extra = "ignored";

			get content() {
				contentReads += 1;
				return new ResultContent({ type: "text", text: "ok", ignored: true });
			}

			get details() {
				return { source: "class" };
			}

			get usage() {
				return {
					input: 1,
					output: 2,
					cacheRead: 3,
					cacheWrite: 4,
					reasoning: 9,
					totalTokens: 999,
					cost: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4, total: 999 },
					extra: true,
				};
			}

			get addedToolNames() {
				return ["new-tool", "new-tool"];
			}
		})();
		const reads = new Map<PropertyKey, number>();
		const result = new Proxy(source, {
			get(target, key, receiver) {
				reads.set(key, (reads.get(key) ?? 0) + 1);
				return Reflect.get(target, key, receiver);
			},
		});

		const snapshot = prepareToolResultSnapshot(result, LIMITS);

		expect(snapshot).toMatchObject({
			content: [{ type: "text", text: "ok" }],
			details: { source: "class" },
			usage: expect.objectContaining({ reasoning: 9, totalTokens: 999, cost: expect.objectContaining({ total: 999 }) }),
			addedToolNames: ["new-tool", "new-tool"],
		});
		expect(contentReads).toBe(1);
		expect([...reads.values()]).toEqual([1, 1, 1, 1, 1]);
		expect(Object.hasOwn(snapshot as object, "extra")).toBe(false);
	});

	it("snapshots Proxy-array lengths once before enforcing retained count budgets", () => {
		let contentLengthReads = 0;
		let nameLengthReads = 0;
		const content = new Proxy([{ type: "text" as const, text: "bounded" }], {
			get(target, key, receiver) {
				if (key === "length") {
					contentLengthReads += 1;
					return contentLengthReads === 1 ? 1 : LIMITS.maxResultContentParts + 1;
				}
				return Reflect.get(target, key, receiver);
			},
		});
		const names = new Proxy(["new-tool"], {
			get(target, key, receiver) {
				if (key === "length") {
					nameLengthReads += 1;
					return nameLengthReads === 1 ? 1 : LIMITS.maxTools + 1;
				}
				return Reflect.get(target, key, receiver);
			},
		});

		expect(prepareToolResultSnapshot({ content, details: {}, addedToolNames: names }, LIMITS)).toMatchObject({
			content: [{ type: "text", text: "bounded" }],
			addedToolNames: ["new-tool"],
		});
		expect({ contentLengthReads, nameLengthReads }).toEqual({ contentLengthReads: 1, nameLengthReads: 1 });
	});

	it("still rejects cycles and non-Pi field types at the retained JSON boundary", () => {
		const cyclic: { self?: unknown } = {};
		cyclic.self = cyclic;
		expect(() => prepareToolResultSnapshot({ content: [], details: cyclic }, LIMITS)).toThrow(GameToolResultError);
		expect(() => prepareToolResultSnapshot({ content: [], details: {}, usage: { input: Number.NaN } }, LIMITS)).toThrow(
			GameToolResultError,
		);
	});

	it("enforces exact content-count, complete UTF-8 byte, and details-depth budgets", () => {
		expect(() =>
			prepareToolResultSnapshot(
				{
					content: [
						{ type: "text", text: "a" },
						{ type: "text", text: "b" },
					],
					details: {},
				},
				{ ...LIMITS, maxResultContentParts: 1 },
			),
		).toThrowError(expect.objectContaining({ code: "tool_result_limit_exceeded" }) as GameToolResultError);

		const result = { content: [{ type: "text", text: "多字节边界".repeat(8) }], details: { ok: true } };
		const bytes = new TextEncoder().encode(canonicalizeJsonValue(result)).byteLength;
		expect(prepareToolResultSnapshot(result, { ...LIMITS, maxResultBytes: bytes })).toEqual(result);
		expect(() => prepareToolResultSnapshot(result, { ...LIMITS, maxResultBytes: bytes - 1 })).toThrowError(
			expect.objectContaining({ code: "tool_result_limit_exceeded" }) as GameToolResultError,
		);

		const nested = { one: { two: { three: { four: true } } } };
		expect(() =>
			prepareToolResultSnapshot({ content: [], details: nested }, { ...LIMITS, maxResultDepth: MIN_TOOL_RESULT_DEPTH }),
		).toThrowError(expect.objectContaining({ code: "tool_result_limit_exceeded" }) as GameToolResultError);
	});

	it("admits a complete multi-part text envelope without lending its budget to retained metadata", () => {
		const limits = { ...LIMITS, maxResultContentParts: 2, maxResultBytes: 256 };
		const details = { retained: "x".repeat(175) };
		const content = [
			{ type: "text" as const, text: "\u0000".repeat(50) },
			{ type: "text" as const, text: "\u0000".repeat(50) },
		];
		expect(textToolResultContentBytes(100, 2)).toBe(Buffer.byteLength(canonicalizeJsonValue(content), "utf8"));
		expect(() => prepareToolResultSnapshot({ content, details }, limits)).toThrowError(
			expect.objectContaining({ code: "tool_result_limit_exceeded" }) as GameToolResultError,
		);
		expect(prepareTransformableToolResultSnapshot({ content, details }, limits, 100)).toEqual({ content, details });
		expect(
			prepareToolResultSnapshot({ content: [{ type: "text", text: "stored reference" }], details }, limits),
		).toBeDefined();
		expect(() =>
			prepareTransformableToolResultSnapshot(
				{ content: [{ type: "text", text: "small" }], details: { retained: "x".repeat(300) } },
				limits,
				100,
			),
		).toThrowError(expect.objectContaining({ code: "tool_result_limit_exceeded" }) as GameToolResultError);
	});

	it("derives the minimum failure-envelope depth and byte configuration", () => {
		expect(() => validateToolSnapshotLimits({ ...LIMITS, maxResultDepth: MIN_TOOL_RESULT_DEPTH - 1 })).toThrow(
			/maxResultDepth/u,
		);
		expect(() => validateToolSnapshotLimits({ ...LIMITS, maxResultBytes: MIN_TOOL_RESULT_BYTES - 1 })).toThrow(
			/maxResultBytes/u,
		);
		expect(validateToolSnapshotLimits({ ...LIMITS, maxResultBytes: MIN_TOOL_RESULT_BYTES })).toMatchObject({
			maxResultBytes: MIN_TOOL_RESULT_BYTES,
		});
	});

	it("preserves useful Tool errors and truncates only to the configured retained-envelope budget", () => {
		expect(boundedToolExecutionError(new Error("useful provider failure"), LIMITS.maxResultBytes).message).toBe(
			"useful provider failure",
		);
		const bounded = boundedToolExecutionError(new Error("多字节错误".repeat(100)), MIN_TOOL_RESULT_BYTES);
		const bytes = new TextEncoder().encode(
			canonicalizeJsonValue({ content: [{ type: "text", text: bounded.message }], details: {} }),
		).byteLength;
		expect(bytes).toBeLessThanOrEqual(MIN_TOOL_RESULT_BYTES);
		expect(bounded.message.length).toBeGreaterThan(0);
	});
});

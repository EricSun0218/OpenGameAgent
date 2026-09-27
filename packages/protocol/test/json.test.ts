import { describe, expect, it } from "vitest";
import {
	canonicalizeJsonValue,
	InvalidJsonValueError,
	JsonValueLimitError,
	normalizeJsonValue,
	normalizeJsonValueWithin,
	parseStrictJsonText,
	StrictJsonError,
} from "../src/index.js";

describe("normalizeJsonValue", () => {
	it("returns a detached JSON data tree", () => {
		const input = { nested: [{ enabled: true }], value: null };
		const normalized = normalizeJsonValue(input);

		expect(normalized).toEqual(input);
		expect(normalized).not.toBe(input);
		expect((normalized as typeof input).nested).not.toBe(input.nested);
	});

	const sparse: unknown[] = [];
	sparse.length = 2;
	sparse[1] = "sparse";

	it.each([undefined, 1n, Number.NaN, Number.POSITIVE_INFINITY, -0, new Date(), { value: undefined }, sparse])(
		"rejects values that JSON cannot represent without coercion",
		(input) => {
			expect(() => normalizeJsonValue(input)).toThrow(InvalidJsonValueError);
		},
	);

	it("detaches shared object identity while rejecting cycles", () => {
		const child = { value: "same object" };
		const shared = { left: child, right: child };
		const cyclic: { self?: unknown } = {};
		cyclic.self = cyclic;

		const normalized = normalizeJsonValue(shared) as typeof shared;
		expect(normalized).toEqual(shared);
		expect(normalized.left).not.toBe(normalized.right);
		expect(() => normalizeJsonValue(cyclic)).toThrow(InvalidJsonValueError);
	});

	it("rejects nested Proxies without invoking their reflection traps", () => {
		let trapCalls = 0;
		const proxy = new Proxy(
			{ secret: true },
			{
				getPrototypeOf() {
					trapCalls += 1;
					return Object.prototype;
				},
				ownKeys() {
					trapCalls += 1;
					return ["secret"];
				},
			},
		);

		expect(() => normalizeJsonValue({ nested: proxy })).toThrow(InvalidJsonValueError);
		expect(trapCalls).toBe(0);
	});

	it("preserves dangerous JSON keys as own data without changing the result prototype", () => {
		const input = JSON.parse('{"__proto__":{"admin":true},"constructor":"ctor","prototype":"proto"}') as unknown;
		const normalized = normalizeJsonValue(input) as Record<string, unknown>;

		expect(Object.getPrototypeOf(normalized)).toBe(Object.prototype);
		expect(Object.hasOwn(normalized, "__proto__")).toBe(true);
		expect(Object.keys(normalized)).toEqual(["__proto__", "constructor", "prototype"]);
		expect(Reflect.get(normalized, "__proto__")).toEqual({ admin: true });
		expect((normalized as { admin?: unknown }).admin).toBeUndefined();
	});

	it("ignores non-enumerable library metadata while rejecting enumerable accessors", () => {
		let getterCalls = 0;
		const input = { value: true } as Record<string, unknown>;
		Object.defineProperty(input, "~kind", { value: "Object", enumerable: false });
		const accessor = { value: true } as Record<string, unknown>;
		Object.defineProperty(accessor, "derived", {
			enumerable: true,
			get() {
				getterCalls += 1;
				return "unsafe";
			},
		});

		expect(normalizeJsonValue(input)).toEqual({ value: true });
		expect(() => normalizeJsonValue(accessor)).toThrow(InvalidJsonValueError);
		expect(getterCalls).toBe(0);
	});
});

describe("normalizeJsonValueWithin", () => {
	it("enforces host-owned depth and complete canonical UTF-8 byte budgets", () => {
		expect(normalizeJsonValueWithin({ value: "界" }, { maxDepth: 2, maxBytes: 15 })).toEqual({ value: "界" });
		expect(() => normalizeJsonValueWithin({ nested: { value: true } }, { maxDepth: 2, maxBytes: 100 })).toThrow(
			JsonValueLimitError,
		);
		expect(() => normalizeJsonValueWithin({ value: "界" }, { maxDepth: 2, maxBytes: 14 })).toThrow(JsonValueLimitError);
	});

	it.each(['quote:"', "slash:\\", "control:\u0000", "paired:🙂", "unpaired:\ud800"])(
		"matches canonical UTF-8 size for %s without a second serialization pass",
		(value) => {
			const fixture = { value };
			const bytes = new TextEncoder().encode(canonicalizeJsonValue(fixture)).byteLength;
			expect(normalizeJsonValueWithin(fixture, { maxDepth: 2, maxBytes: bytes })).toEqual(fixture);
			expect(() => normalizeJsonValueWithin(fixture, { maxDepth: 2, maxBytes: bytes - 1 })).toThrow(
				JsonValueLimitError,
			);
		},
	);

	it("rejects array accessors without invoking them", () => {
		let accessed = false;
		const value: unknown[] = [];
		Object.defineProperty(value, 0, {
			enumerable: true,
			get() {
				accessed = true;
				return "private";
			},
		});
		expect(() => normalizeJsonValueWithin(value, { maxDepth: 2, maxBytes: 128 })).toThrow(InvalidJsonValueError);
		expect(accessed).toBe(false);
	});

	it("walks one sibling at a time and stops before inspecting a tail outside the byte budget", () => {
		let tailAccessed = false;
		const value: unknown[] = ["x".repeat(64)];
		Object.defineProperty(value, 1, {
			enumerable: true,
			get() {
				tailAccessed = true;
				return "private";
			},
		});
		try {
			normalizeJsonValueWithin(value, { maxDepth: 2, maxBytes: 16 });
			throw new Error("Expected the first item to exhaust the byte budget.");
		} catch (error) {
			expect(error).toBeInstanceOf(JsonValueLimitError);
			expect((error as JsonValueLimitError).code).toBe("json_too_large");
		}
		expect(tailAccessed).toBe(false);
	});

	it("captures host budgets once before normalizing", () => {
		const reads = { maxDepth: 0, maxBytes: 0 };
		const limits = {
			get maxDepth() {
				reads.maxDepth += 1;
				return 2;
			},
			get maxBytes() {
				reads.maxBytes += 1;
				return reads.maxBytes === 1 ? 1 : 128;
			},
		};

		expect(() => normalizeJsonValueWithin({ value: true }, limits)).toThrow(JsonValueLimitError);
		expect(reads).toEqual({ maxDepth: 1, maxBytes: 1 });
	});
});

describe("canonicalizeJsonValue", () => {
	it("produces the same canonical bytes for objects with different key insertion order", () => {
		expect(canonicalizeJsonValue({ z: 1, nested: { b: true, a: "value" } })).toBe(
			canonicalizeJsonValue({ nested: { a: "value", b: true }, z: 1 }),
		);
	});

	it("retains lossless JSON validation before canonicalization", () => {
		expect(() => canonicalizeJsonValue({ invalid: Number.NaN })).toThrow(InvalidJsonValueError);
	});

	it("keeps JCS-compatible number, escape, and UTF-16 key ordering bytes", () => {
		expect(canonicalizeJsonValue([Number("333333333.33333329"), 1e30, 4.5, 0.002, 1e-27])).toBe(
			"[333333333.3333333,1e+30,4.5,0.002,1e-27]",
		);
		expect(canonicalizeJsonValue({ value: '\b\t\n\f\r"\\\u0000' })).toBe('{"value":"\\b\\t\\n\\f\\r\\"\\\\\\u0000"}');
		expect(canonicalizeJsonValue({ "�": 6, "😀": 5, "": 4, a: 3, "1": 2, "\r": 1 })).toBe(
			'{"\\r":1,"1":2,"a":3,"":4,"😀":5,"�":6}',
		);
	});

	it("canonicalizes deeply nested admitted JSON without recursive stack growth", () => {
		let value: unknown = 0;
		for (let depth = 0; depth < 4_000; depth += 1) value = [value];
		const canonical = canonicalizeJsonValue(value);
		expect(canonical.length).toBe(8_001);
		expect(canonical.startsWith("[[[[")).toBe(true);
		expect(canonical.endsWith("]]]]")).toBe(true);
	});
});

describe("parseStrictJsonText", () => {
	it("parses a detached value under a host depth bound", () => {
		expect(parseStrictJsonText('{"safe":[1,0.5,true]}', 2)).toEqual({ safe: [1, 0.5, true] });
	});

	it.each([
		['{"a":1,"a":2}', "duplicate_json_key"],
		['{"a":{"b":1}}', "json_too_deep"],
		['{"n":9007199254740993}', "unsafe_json_number"],
		['{"n":-0}', "unsafe_json_number"],
		['{"n":1e400}', "unsafe_json_number"],
		['{"trailing":true,}', "invalid_json"],
	] as const)("rejects non-canonical input %s", (text, code) => {
		try {
			parseStrictJsonText(text, text.includes('"b"') ? 1 : 4);
			throw new Error("Expected strict JSON rejection.");
		} catch (error) {
			expect(error).toBeInstanceOf(StrictJsonError);
			expect((error as StrictJsonError).code).toBe(code);
		}
	});
});

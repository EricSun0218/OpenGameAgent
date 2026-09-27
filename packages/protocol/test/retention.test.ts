import { describe, expect, it } from "vitest";
import { captureRetainedIdentifiers, type RetentionProtectionBudget } from "../src/index.js";

describe("retention protection budgets", () => {
	it("bounds the original list before reading entries or invoking an iterator", () => {
		const oversized: string[] = [];
		Object.defineProperty(oversized, 0, {
			get: () => {
				throw new Error("entry read");
			},
		});
		expect(() => captureRetainedIdentifiers(oversized, { maxCount: 0, maxBytes: 0 }, "retainIds")).toThrow(
			"retainIds exceeds retainBudget.maxCount.",
		);
		const retained = ["龙"];
		retained[Symbol.iterator] = () => {
			throw new Error("iterator read");
		};
		expect(() => captureRetainedIdentifiers(retained, { maxCount: 1, maxBytes: 2 }, "retainIds")).toThrow(
			"retainIds exceeds retainBudget.maxBytes.",
		);
	});

	it("captures an immutable unique list at the exact UTF-8 budget without using a custom iterator", () => {
		const source = ["龙", "😀"];
		source[Symbol.iterator] = () => {
			throw new Error("iterator read");
		};
		expect(() => captureRetainedIdentifiers(source, { maxCount: 2, maxBytes: 6 }, "retainIds")).toThrow(
			"retainIds exceeds retainBudget.maxBytes.",
		);
		const captured = captureRetainedIdentifiers(source, { maxCount: 2, maxBytes: 7 }, "retainIds");
		source[0] = "changed";
		expect(captured).toEqual(["龙", "😀"]);
		expect(Object.isFrozen(captured)).toBe(true);
		expect(captureRetainedIdentifiers([], { maxCount: 0, maxBytes: 0 }, "retainIds")).toEqual([]);
	});

	it("rejects malformed budgets and lists without invoking accessors", () => {
		for (const budget of [
			undefined,
			{},
			{ maxCount: -1, maxBytes: 0 },
			{ maxCount: 0, maxBytes: NaN },
			{ maxCount: 0, maxBytes: Number.MAX_SAFE_INTEGER + 1 },
		]) {
			expect(() => captureRetainedIdentifiers([], budget as RetentionProtectionBudget, "retainIds")).toThrow(TypeError);
		}
		const accessor: string[] = [];
		Object.defineProperty(accessor, 0, {
			get: () => {
				throw new Error("getter read");
			},
		});
		for (const values of [undefined, {}, [1], Array(1), accessor, new Proxy(["a"], {}), ["a", "a"]]) {
			expect(() => captureRetainedIdentifiers(values, { maxCount: 2, maxBytes: 2 }, "retainIds")).toThrow(TypeError);
		}
	});
});

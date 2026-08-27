import { describe, expect, it } from "vitest";
import { decodeRuntimeFrame, encodeRuntimeFrame } from "./wire.js";

describe("runtime wire", () => {
	it("round-trips floating-point structured game data", () => {
		const input = { position: [1.25, -4.5, 9.75], visible: true, target: null };
		expect(decodeRuntimeFrame(encodeRuntimeFrame(input))).toEqual(input);
	});

	it("rejects oversized and non-finite values", () => {
		expect(() => encodeRuntimeFrame({ value: Number.NaN })).toThrow(/finite/);
		expect(() =>
			encodeRuntimeFrame(
				{ value: "too long" },
				{ maximumBytes: 64, maximumDepth: 4, maximumCollectionItems: 4, maximumStringCharacters: 5 },
			),
		).toThrow(/oversized string/);
	});
});

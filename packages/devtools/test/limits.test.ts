import { describe, expect, it } from "vitest";
import { LimitRegistry, P0_LIMIT_DEFINITIONS, type LimitDefinition } from "../src/index.js";

describe("limit registry", () => {
	it("resolves fixed, reference-defaulted, and host-required values", () => {
		const definitions: LimitDefinition[] = [
			{
				id: "protocol.digestBytes",
				unit: "bytes",
				evidence: "protocol",
				mode: "fixed",
				integer: true,
				minimum: 32,
				maximum: 32,
				fixedValue: 32,
			},
			{
				id: "runtime.queue",
				unit: "count",
				evidence: "reference",
				mode: "defaulted",
				integer: true,
				minimum: 1,
				defaultValue: 128,
			},
			{
				id: "host.responseBytes",
				unit: "bytes",
				evidence: "host-required",
				mode: "host-required",
				integer: true,
				minimum: 1,
			},
		];
		const registry = new LimitRegistry(definitions);

		expect(registry.resolve({ "host.responseBytes": 4096 })).toEqual({
			"protocol.digestBytes": 32,
			"runtime.queue": 128,
			"host.responseBytes": 4096,
		});
		expect(() => registry.resolve({})).toThrow(/host must configure/);
		expect(() => registry.resolve({ "host.responseBytes": 0 })).toThrow(/below its minimum/);
		expect(() => registry.resolve({ "host.responseBytes": 1, "host.unknown": 1 })).toThrow(/Unknown limit/);
	});

	it("registers every current P0 numeric boundary without duplicate identifiers", () => {
		const listed = new LimitRegistry(P0_LIMIT_DEFINITIONS).list();
		expect(new Set(listed.map((entry) => entry.id)).size).toBe(listed.length);
		expect(listed.filter((entry) => entry.mode === "host-required").length).toBeGreaterThan(0);
		expect(listed.every((entry) => entry.evidence.length > 0)).toBe(true);
	});
});

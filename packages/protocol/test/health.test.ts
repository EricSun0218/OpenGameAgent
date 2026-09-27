import { Value } from "typebox/value";
import { describe, expect, it } from "vitest";
import { RuntimeDependencyHealthSchema, RuntimeHealthSnapshotSchema } from "../src/index.js";

describe("runtime health protocol", () => {
	it("accepts declared and observed dependency records", () => {
		expect(
			Value.Check(RuntimeHealthSnapshotSchema, {
				checkedAt: "2026-08-30T00:00:00.000Z",
				status: "degraded",
				dependencies: [
					{
						id: "local-model",
						kind: "model",
						requirement: "required",
						state: "ready",
						observedAt: "2026-08-30T00:00:00.000Z",
					},
					{ id: "voice", kind: "realtime", requirement: "optional", state: "declared" },
					{
						id: "images",
						kind: "media",
						requirement: "optional",
						state: "unavailable",
						observedAt: "2026-08-30T00:00:00.000Z",
						reason: "capacity",
					},
				],
			}),
		).toBe(true);
	});

	it("keeps state-specific fields exact", () => {
		expect(
			Value.Check(RuntimeDependencyHealthSchema, {
				id: "model",
				kind: "model",
				requirement: "required",
				state: "declared",
				observedAt: "2026-08-30T00:00:00.000Z",
			}),
		).toBe(false);
		expect(
			Value.Check(RuntimeDependencyHealthSchema, {
				id: "model",
				kind: "model",
				requirement: "required",
				state: "ready",
				observedAt: "2026-08-30T00:00:00.000Z",
				reason: "unknown",
			}),
		).toBe(false);
		expect(
			Value.Check(RuntimeDependencyHealthSchema, {
				id: "model",
				kind: "model",
				requirement: "required",
				state: "degraded",
				observedAt: "2026-08-30T00:00:00.000Z",
			}),
		).toBe(false);
		expect(
			Value.Check(RuntimeDependencyHealthSchema, {
				id: "model",
				kind: "model",
				requirement: "required",
				state: "available",
				observedAt: "2026-08-30T00:00:00.000Z",
				reason: "transport",
			}),
		).toBe(false);
	});

	it("rejects unknown kinds, states, reasons, timestamps, and fields", () => {
		for (const candidate of [
			{ id: "x", kind: "database", requirement: "required", state: "declared" },
			{ id: "x", kind: "mcp", requirement: "required", state: "starting" },
			{
				id: "x",
				kind: "mcp",
				requirement: "required",
				state: "unavailable",
				observedAt: "now",
				reason: "network-error",
			},
			{ id: "x", kind: "mcp", requirement: "required", state: "declared", error: "secret" },
		]) {
			expect(Value.Check(RuntimeDependencyHealthSchema, candidate)).toBe(false);
		}
	});
});

import { describe, expect, it, vi } from "vitest";
import {
	RuntimePassiveHealthDependency,
	RuntimeHealthMonitor,
	type RuntimeDependencyDefinition,
	type RuntimeDependencyInspection,
} from "../src/index.js";

const CHECKED_AT = "2026-08-30T00:00:00.000Z";
const LIMITS = {
	maxDependencies: 8,
	maxDependencyIdBytes: 64,
	maxConcurrentInspections: 2,
	inspectionTimeoutMs: 1_000,
} as const;

function definition(
	id: string,
	requirement: "required" | "optional",
	inspection?: RuntimeDependencyInspection | ((signal: AbortSignal) => Promise<RuntimeDependencyInspection>),
): RuntimeDependencyDefinition {
	return {
		id,
		kind: "local-endpoint",
		requirement,
		...(inspection === undefined
			? {}
			: {
					inspect: typeof inspection === "function" ? inspection : async () => inspection,
				}),
	};
}

function monitor(dependencies: readonly RuntimeDependencyDefinition[], overrides = {}) {
	return new RuntimeHealthMonitor({
		dependencies,
		limits: { ...LIMITS, ...overrides },
		now: () => Date.parse(CHECKED_AT),
	});
}

describe("RuntimeHealthMonitor", () => {
	it("projects host-fresh passive operation evidence without probing the dependency", async () => {
		let monotonicNow = 100;
		const passive = new RuntimePassiveHealthDependency({
			id: "image-provider",
			kind: "media",
			requirement: "required",
			maxObservationAgeMs: 5,
			now: () => monotonicNow,
		});
		const health = monitor([passive.definition]);

		await expect(health.inspect()).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [{ id: "image-provider", kind: "media", state: "declared" }],
		});

		passive.observe({ state: "ready" });
		monotonicNow = 105;
		await expect(health.inspect()).resolves.toMatchObject({
			status: "ready",
			dependencies: [{ state: "ready" }],
		});

		monotonicNow = 106;
		await expect(health.inspect()).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [{ state: "available", reason: "stale" }],
		});

		passive.observe({ state: "degraded", reason: "capacity" });
		await expect(health.inspect()).resolves.toMatchObject({
			status: "degraded",
			dependencies: [{ state: "degraded", reason: "capacity" }],
		});

		passive.observe({ state: "declared" });
		await expect(health.inspect()).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [{ state: "declared" }],
		});
	});

	it("captures passive dependency policy and rejects invalid observations before changing evidence", async () => {
		let now = 1;
		const options = {
			id: "voice-provider",
			kind: "realtime" as const,
			requirement: "optional" as const,
			maxObservationAgeMs: 2,
			now: () => now,
		};
		const passive = new RuntimePassiveHealthDependency(options);
		options.id = "mutated";
		options.kind = "media" as never;
		options.requirement = "required" as never;
		options.maxObservationAgeMs = 99;
		options.now = () => 999;

		passive.observe({ state: "unavailable", reason: "transport" });
		expect(() => passive.observe({ state: "ready", reason: "unknown" } as never)).toThrow();
		now = 2;
		await expect(monitor([passive.definition]).inspect()).resolves.toMatchObject({
			status: "degraded",
			dependencies: [
				{
					id: "voice-provider",
					kind: "realtime",
					requirement: "optional",
					state: "unavailable",
					reason: "transport",
				},
			],
		});
	});

	it("captures an inspector result discriminant and reason exactly once", async () => {
		const reads = new Map<PropertyKey, number>();
		const inspection = new Proxy(
			{ state: "degraded" as const, reason: "capacity" as const },
			{
				get(target, property, receiver) {
					reads.set(property, (reads.get(property) ?? 0) + 1);
					return Reflect.get(target, property, receiver);
				},
			},
		);

		await expect(
			monitor([definition("provider", "optional", async () => inspection)]).inspect(),
		).resolves.toMatchObject({
			dependencies: [{ state: "degraded", reason: "capacity" }],
		});
		expect(reads.get("state")).toBe(1);
		expect(reads.get("reason")).toBe(1);
	});

	it("returns ready for an empty host and not-ready for a required declaration", async () => {
		await expect(monitor([]).inspect()).resolves.toEqual({
			checkedAt: CHECKED_AT,
			status: "ready",
			dependencies: [],
		});
		await expect(monitor([definition("model", "required")]).inspect()).resolves.toEqual({
			checkedAt: CHECKED_AT,
			status: "not-ready",
			dependencies: [{ id: "model", kind: "local-endpoint", requirement: "required", state: "declared" }],
		});
	});

	it("aggregates required and optional states without treating available as ready", async () => {
		const cases = [
			{
				dependencies: [definition("required", "required", { state: "ready" }), definition("optional", "optional")],
				status: "ready",
			},
			{
				dependencies: [
					definition("required", "required", { state: "degraded", reason: "capacity" }),
					definition("optional", "optional", { state: "ready" }),
				],
				status: "degraded",
			},
			{
				dependencies: [
					definition("required", "required", { state: "ready" }),
					definition("optional", "optional", { state: "unavailable", reason: "transport" }),
				],
				status: "degraded",
			},
			{
				dependencies: [definition("required", "required", { state: "available" })],
				status: "not-ready",
			},
			{
				dependencies: [definition("required", "required", { state: "unavailable", reason: "misconfigured" })],
				status: "not-ready",
			},
		] as const;
		for (const candidate of cases) {
			const snapshot = await monitor(candidate.dependencies).inspect();
			expect(snapshot.status).toBe(candidate.status);
		}
	});

	it("preserves registration order, snapshots results, and freezes the projection", async () => {
		const result: { state: "degraded"; reason: "stale" | "capacity" } = {
			state: "degraded",
			reason: "stale",
		};
		const definitions = [definition("second", "optional", result), definition("first", "required", { state: "ready" })];
		const health = monitor(definitions);
		definitions.reverse();
		const snapshot = await health.inspect();
		result.reason = "capacity";
		expect(snapshot.dependencies.map(({ id }) => id)).toEqual(["second", "first"]);
		expect(snapshot.dependencies[0]).toMatchObject({ state: "degraded", reason: "stale" });
		expect(Object.isFrozen(snapshot)).toBe(true);
		expect(Object.isFrozen(snapshot.dependencies)).toBe(true);
		expect(snapshot.dependencies.every(Object.isFrozen)).toBe(true);
	});

	it("uses the maintained limiter for host-bounded concurrent inspections", async () => {
		let active = 0;
		let maximum = 0;
		const releases: Array<() => void> = [];
		const dependencies = Array.from({ length: 4 }, (_, index) =>
			definition(`dependency-${index}`, "optional", async (signal) => {
				signal.throwIfAborted();
				active += 1;
				maximum = Math.max(maximum, active);
				await new Promise<void>((resolve) => releases.push(resolve));
				active -= 1;
				return { state: "ready" };
			}),
		);
		const operation = monitor(dependencies).inspect();
		await vi.waitFor(() => expect(releases).toHaveLength(2));
		releases.splice(0).forEach((release) => {
			release();
		});
		await vi.waitFor(() => expect(releases).toHaveLength(2));
		releases.splice(0).forEach((release) => {
			release();
		});
		await operation;
		expect(maximum).toBe(2);
	});

	it("maps its timeout and unknown failures while propagating caller cancellation", async () => {
		vi.useFakeTimers();
		try {
			const timed = monitor(
				[
					definition("timed", "required", async (signal) => {
						await new Promise<void>((_resolve, reject) =>
							signal.addEventListener("abort", () => reject(signal.reason), { once: true }),
						);
						return { state: "ready" };
					}),
				],
				{ inspectionTimeoutMs: 5 },
			).inspect();
			await vi.advanceTimersByTimeAsync(5);
			await expect(timed).resolves.toMatchObject({
				status: "not-ready",
				dependencies: [{ state: "unavailable", reason: "timeout" }],
			});
		} finally {
			vi.useRealTimers();
		}

		await expect(
			monitor([definition("broken", "required", async () => Promise.reject(new Error("private URL")))]).inspect(),
		).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [{ state: "unavailable", reason: "unknown" }],
		});

		const controller = new AbortController();
		const aborted = monitor([
			definition("cancelled", "required", async (signal) => {
				await new Promise<void>((_resolve, reject) =>
					signal.addEventListener("abort", () => reject(signal.reason), { once: true }),
				);
				return { state: "ready" };
			}),
		]).inspect(controller.signal);
		controller.abort(new Error("host stopped"));
		await expect(aborted).rejects.toThrow("host stopped");
	});

	it("rejects duplicate and over-budget definitions before inspection", () => {
		const inspect = vi.fn(async () => ({ state: "ready" as const }));
		expect(() =>
			monitor([
				{ ...definition("same", "required"), inspect },
				{ ...definition("same", "optional"), inspect },
			]),
		).toThrow();
		expect(inspect).not.toHaveBeenCalled();
		expect(() => monitor([definition("éé", "required")], { maxDependencyIdBytes: 3 })).toThrow();
		expect(() =>
			monitor([definition("one", "required"), definition("two", "optional")], { maxDependencies: 1 }),
		).toThrow();
		expect(() => monitor([], { maxConcurrentInspections: 9 })).toThrow();
	});

	it("maps invalid inspector shapes to the closed unknown failure", async () => {
		const snapshot = await monitor([
			definition("bad-ready", "optional", async () => ({ state: "ready", reason: "transport" }) as never),
			definition("bad-degraded", "optional", async () => ({ state: "degraded" }) as never),
			definition("bad-available", "optional", async () => ({ state: "available", reason: "transport" }) as never),
		]).inspect();
		expect(snapshot.dependencies).toEqual([
			{
				id: "bad-ready",
				kind: "local-endpoint",
				requirement: "optional",
				state: "unavailable",
				observedAt: CHECKED_AT,
				reason: "unknown",
			},
			{
				id: "bad-degraded",
				kind: "local-endpoint",
				requirement: "optional",
				state: "unavailable",
				observedAt: CHECKED_AT,
				reason: "unknown",
			},
			{
				id: "bad-available",
				kind: "local-endpoint",
				requirement: "optional",
				state: "unavailable",
				observedAt: CHECKED_AT,
				reason: "unknown",
			},
		]);
	});
});

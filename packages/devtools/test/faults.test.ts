import { describe, expect, it } from "vitest";
import { DeterministicFaultInjector, type InjectedFaultError } from "../src/index.js";

describe("deterministic fault injection", () => {
	it("injects only the configured occurrence", async () => {
		const injector = new DeterministicFaultInjector([
			{ point: "storage.after-commit", occurrence: 2, action: { kind: "throw" } },
		]);

		await expect(injector.checkpoint("storage.after-commit")).resolves.toBeUndefined();
		await expect(injector.checkpoint("storage.after-commit")).rejects.toMatchObject({
			name: "InjectedFaultError",
			point: "storage.after-commit",
			occurrence: 2,
		} satisfies Partial<InjectedFaultError>);
		await expect(injector.checkpoint("storage.after-commit")).resolves.toBeUndefined();
	});

	it("supports explicit abort and cancellation-aware delay", async () => {
		const aborting = new DeterministicFaultInjector([
			{ point: "provider.before-stream", occurrence: 1, action: { kind: "abort" } },
		]);
		await expect(aborting.checkpoint("provider.before-stream")).rejects.toMatchObject({ name: "AbortError" });

		const delaying = new DeterministicFaultInjector([
			{ point: "transport.before-send", occurrence: 1, action: { kind: "delay", milliseconds: 1_000 } },
		]);
		const controller = new AbortController();
		const pending = delaying.checkpoint("transport.before-send", controller.signal);
		controller.abort(new DOMException("cancelled", "AbortError"));
		await expect(pending).rejects.toMatchObject({ name: "AbortError" });
	});

	it("rejects duplicate or unsupported numeric rules", () => {
		expect(
			() =>
				new DeterministicFaultInjector([
					{ point: "tool.before-execute", occurrence: 1, action: { kind: "throw" } },
					{ point: "tool.before-execute", occurrence: 1, action: { kind: "abort" } },
				]),
		).toThrow(/only one action/);
		expect(
			() =>
				new DeterministicFaultInjector([{ point: "tool.before-execute", occurrence: 0, action: { kind: "throw" } }]),
		).toThrow(/positive safe integer/);
		expect(
			() =>
				new DeterministicFaultInjector([
					{ point: "tool.before-execute", occurrence: 1, action: { kind: "delay", milliseconds: 2_147_483_648 } },
				]),
		).toThrow(/between 0 and 2147483647 milliseconds/);
		expect(
			() =>
				new DeterministicFaultInjector([
					{ point: "tool.before-execute", occurrence: 1, action: { kind: "delay", milliseconds: 2_147_483_647 } },
				]),
		).not.toThrow();
	});

	it("snapshots each configured fault field once", async () => {
		const reads = { point: 0, occurrence: 0, kind: 0, milliseconds: 0 };
		const values = {
			point: "transport.before-send" as const,
			occurrence: 1,
			kind: "delay" as const,
			milliseconds: 0,
		};
		const action = Object.defineProperties(
			{},
			{
				kind: {
					get: () => {
						reads.kind += 1;
						return values.kind;
					},
				},
				milliseconds: {
					get: () => {
						reads.milliseconds += 1;
						return values.milliseconds;
					},
				},
			},
		) as { readonly kind: "delay"; readonly milliseconds: number };
		const rule = Object.defineProperties(
			{},
			{
				point: {
					get: () => {
						reads.point += 1;
						return values.point;
					},
				},
				occurrence: {
					get: () => {
						reads.occurrence += 1;
						return values.occurrence;
					},
				},
				action: { value: action },
			},
		) as {
			readonly point: "transport.before-send";
			readonly occurrence: number;
			readonly action: typeof action;
		};
		const injector = new DeterministicFaultInjector([rule]);
		values.milliseconds = 2_147_483_648;
		await expect(injector.checkpoint("transport.before-send")).resolves.toBeUndefined();
		expect(reads).toEqual({ point: 1, occurrence: 1, kind: 1, milliseconds: 1 });
	});

	it("rejects an unknown runtime fault action", () => {
		expect(
			() =>
				new DeterministicFaultInjector([
					{
						point: "tool.before-execute",
						occurrence: 1,
						action: { kind: "unknown" } as never,
					},
				]),
		).toThrow("Fault action kind is not supported");
	});
});

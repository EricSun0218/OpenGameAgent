import type { Model, Provider } from "@earendil-works/pi-ai";
import type { RuntimeHealthSnapshot } from "@opengameagent/protocol";
import { describe, expect, test } from "vitest";
import {
	ProviderProfileLifecycle,
	ProviderProfileLifecycleError,
	type ProviderProfileProbe,
} from "../src/provider-profile-lifecycle.js";
import type { CapturedProviderProfileConfiguration, ProviderProfileConfiguration } from "../src/provider-profiles.js";

const READY_HEALTH: RuntimeHealthSnapshot = {
	checkedAt: "2026-08-31T00:00:00.000Z",
	status: "ready",
	dependencies: [
		{
			id: "provider",
			kind: "provider",
			requirement: "required",
			state: "ready",
			observedAt: "2026-08-31T00:00:00.000Z",
		},
	],
};

describe("provider profile lifecycle", () => {
	test("publishes one immutable safe draft while the runtime remains inactive", async () => {
		const fixture = configurationFixture("draft");
		const probe = probeFixture();
		const lifecycle = new ProviderProfileLifecycle({ probe: probe.value });

		const result = await lifecycle.execute({ type: "draft", configuration: fixture.configuration });

		expect(result.type).toBe("drafted");
		expect(lifecycle.snapshot()).toEqual({
			draft: {
				revision: expect.any(String),
				profile: {
					targets: [
						{
							profileId: "draft",
							modelId: "model",
							name: "draft-model",
							api: "profile-lifecycle-test",
							reasoning: false,
							input: ["text"],
							contextWindow: 8192,
							maxTokens: 1024,
							supportsTools: true,
							pricing: {
								kind: "pi-catalog",
								rates: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4 },
							},
						},
					],
				},
			},
		});
		expect(fixture.catalogReads()).toBe(1);
		expect(probe.calls).toHaveLength(0);
		expect(() => lifecycle.configuration()).toThrow(lifecycleError("active_missing"));
		expect(JSON.stringify(lifecycle.snapshot())).not.toContain("secret");
		expect(Object.isFrozen(lifecycle.snapshot())).toBe(true);
	});

	test("keeps the current draft when a replacement fails provider validation", async () => {
		const lifecycle = new ProviderProfileLifecycle({ probe: probeFixture().value });
		const first = configurationFixture("first");
		const revision = await draft(lifecycle, first.configuration);
		const invalid = { ...configurationFixture("invalid").configuration, targets: [] };

		await expect(
			lifecycle.execute({
				type: "update",
				expectedDraftRevision: revision,
				configuration: invalid,
			}),
		).rejects.toEqual(lifecycleError("validation_failed"));

		expect(lifecycle.snapshot().draft?.revision).toBe(revision);
		expect(lifecycle.snapshot().draft?.profile.targets[0]?.profileId).toBe("first");
	});

	test("updates only the exact draft revision and captures the candidate once", async () => {
		const lifecycle = new ProviderProfileLifecycle({ probe: probeFixture().value });
		const revision = await draft(lifecycle, configurationFixture("first").configuration);
		const next = configurationFixture("next");

		await lifecycle.execute({
			type: "update",
			expectedDraftRevision: revision,
			configuration: next.configuration,
		});

		expect(next.catalogReads()).toBe(1);
		expect(lifecycle.snapshot().draft?.revision).not.toBe(revision);
		const stale = configurationFixture("stale");
		await expect(
			lifecycle.execute({
				type: "update",
				expectedDraftRevision: revision,
				configuration: stale.configuration,
			}),
		).rejects.toEqual(lifecycleError("conflict"));
		expect(stale.catalogReads()).toBe(0);
	});

	test("validates the captured draft without probing or writing lifecycle state", async () => {
		const probe = probeFixture();
		const lifecycle = new ProviderProfileLifecycle({ probe: probe.value });
		const revision = await draft(lifecycle, configurationFixture("validated").configuration);

		const result = await lifecycle.execute({ type: "validate", expectedDraftRevision: revision });

		expect(result).toMatchObject({ type: "validated", revision });
		expect(probe.calls).toHaveLength(0);
		expect(lifecycle.snapshot().draft?.revision).toBe(revision);
	});

	test("tests an exact revision once without persisting the observation", async () => {
		const probe = probeFixture({ acceptedForActivation: false, health: READY_HEALTH });
		const lifecycle = new ProviderProfileLifecycle({ probe: probe.value });
		const revision = await draft(lifecycle, configurationFixture("tested").configuration);

		const result = await lifecycle.execute({ type: "test", expectedDraftRevision: revision });

		expect(result).toEqual({
			type: "tested",
			revision,
			acceptedForActivation: false,
			health: READY_HEALTH,
		});
		expect(probe.calls).toHaveLength(1);
		expect(lifecycle.snapshot().draft?.revision).toBe(revision);
	});

	test("reprobes and atomically activates the exact draft for the next Pi request", async () => {
		const probe = probeFixture();
		const lifecycle = new ProviderProfileLifecycle({ probe: probe.value });
		const revision = await draft(lifecycle, configurationFixture("active").configuration);
		await lifecycle.execute({ type: "test", expectedDraftRevision: revision });

		const result = await lifecycle.execute({ type: "activate", expectedDraftRevision: revision });

		expect(result.type).toBe("activated");
		expect(probe.calls).toHaveLength(2);
		expect(lifecycle.configuration()).toBe(probe.calls[1]?.configuration);
		expect(Reflect.ownKeys(probe.calls[1]?.configuration ?? {})).toEqual(["snapshot"]);
		expect(JSON.stringify(probe.calls[1]?.configuration)).not.toContain("secret");
		expect(lifecycle.snapshot().draft).toBeUndefined();
		expect(lifecycle.snapshot().active?.revision).not.toBe(revision);
	});

	test("keeps the draft and active state when activation is rejected", async () => {
		const probe = probeFixture({ acceptedForActivation: false, health: READY_HEALTH });
		const lifecycle = new ProviderProfileLifecycle({ probe: probe.value });
		const revision = await draft(lifecycle, configurationFixture("rejected").configuration);

		await expect(lifecycle.execute({ type: "activate", expectedDraftRevision: revision })).rejects.toEqual(
			lifecycleError("test_rejected"),
		);

		expect(lifecycle.snapshot().draft?.revision).toBe(revision);
		expect(lifecycle.snapshot().active).toBeUndefined();
	});

	test("requires the exact active revision before replacing an active configuration", async () => {
		const probe = probeFixture();
		const lifecycle = new ProviderProfileLifecycle({ probe: probe.value });
		const firstDraft = await draft(lifecycle, configurationFixture("first").configuration);
		await lifecycle.execute({ type: "activate", expectedDraftRevision: firstDraft });
		const activeRevision = requireRevision(lifecycle.snapshot().active?.revision);
		const nextDraft = await draft(lifecycle, configurationFixture("next").configuration);

		await expect(lifecycle.execute({ type: "activate", expectedDraftRevision: nextDraft })).rejects.toEqual(
			lifecycleError("conflict"),
		);
		expect(probe.calls).toHaveLength(1);

		await lifecycle.execute({
			type: "activate",
			expectedDraftRevision: nextDraft,
			expectedActiveRevision: activeRevision,
		});
		expect(lifecycle.snapshot().active?.profile.targets[0]?.profileId).toBe("next");
	});

	test("discards a late activation result after the draft is updated", async () => {
		const pending = deferredProbe();
		const lifecycle = new ProviderProfileLifecycle({ probe: pending.value });
		const revision = await draft(lifecycle, configurationFixture("old").configuration);
		const activation = lifecycle.execute({ type: "activate", expectedDraftRevision: revision });
		await pending.entered;
		await lifecycle.execute({
			type: "update",
			expectedDraftRevision: revision,
			configuration: configurationFixture("new").configuration,
		});

		pending.release({ acceptedForActivation: false, health: READY_HEALTH });

		await expect(activation).rejects.toEqual(lifecycleError("conflict"));
		expect(lifecycle.snapshot().draft?.profile.targets[0]?.profileId).toBe("new");
		expect(lifecycle.snapshot().active).toBeUndefined();
	});

	test("returns a late draft observation with its captured revision without changing the newer draft", async () => {
		const pending = deferredProbe();
		const lifecycle = new ProviderProfileLifecycle({ probe: pending.value });
		const revision = await draft(lifecycle, configurationFixture("observed").configuration);
		const observation = lifecycle.execute({ type: "test", expectedDraftRevision: revision });
		await pending.entered;
		await lifecycle.execute({
			type: "update",
			expectedDraftRevision: revision,
			configuration: configurationFixture("newer").configuration,
		});

		pending.release({ acceptedForActivation: true, health: READY_HEALTH });

		await expect(observation).resolves.toMatchObject({ type: "tested", revision });
		expect(lifecycle.snapshot().draft?.profile.targets[0]?.profileId).toBe("newer");
		expect(lifecycle.snapshot().active).toBeUndefined();
	});

	test("does not resurrect a draft deleted while its activation probe is pending", async () => {
		const pending = deferredProbe();
		const lifecycle = new ProviderProfileLifecycle({ probe: pending.value });
		const revision = await draft(lifecycle, configurationFixture("deleted").configuration);
		const activation = lifecycle.execute({ type: "activate", expectedDraftRevision: revision });
		await pending.entered;
		await lifecycle.execute({ type: "delete", expectedDraftRevision: revision });

		pending.release({ acceptedForActivation: true, health: READY_HEALTH });

		await expect(activation).rejects.toEqual(lifecycleError("conflict"));
		expect(lifecycle.snapshot()).toEqual({});
	});

	test("does not replace an active configuration revoked during an activation probe", async () => {
		const pending = secondCallDeferredProbe();
		const lifecycle = new ProviderProfileLifecycle({ probe: pending.value });
		const firstDraft = await draft(lifecycle, configurationFixture("first").configuration);
		await lifecycle.execute({ type: "activate", expectedDraftRevision: firstDraft });
		const activeRevision = requireRevision(lifecycle.snapshot().active?.revision);
		const nextDraft = await draft(lifecycle, configurationFixture("next").configuration);
		const activation = lifecycle.execute({
			type: "activate",
			expectedDraftRevision: nextDraft,
			expectedActiveRevision: activeRevision,
		});
		await pending.entered;
		await lifecycle.execute({ type: "revoke", expectedActiveRevision: activeRevision });

		pending.release({ acceptedForActivation: true, health: READY_HEALTH });

		await expect(activation).rejects.toEqual(lifecycleError("conflict"));
		expect(lifecycle.snapshot().active).toBeUndefined();
		expect(lifecycle.snapshot().draft?.revision).toBe(nextDraft);
	});

	test("returns revision-bound health without changing state", async () => {
		const probe = probeFixture();
		const lifecycle = new ProviderProfileLifecycle({ probe: probe.value });
		const draftRevision = await draft(lifecycle, configurationFixture("health").configuration);
		await lifecycle.execute({ type: "activate", expectedDraftRevision: draftRevision });
		const activeRevision = lifecycle.snapshot().active?.revision ?? "";

		const result = await lifecycle.execute({
			type: "health",
			expectedActiveRevision: activeRevision,
		});

		expect(result).toEqual({
			type: "health",
			revision: activeRevision,
			health: READY_HEALTH,
		});
		expect(lifecycle.snapshot().active?.revision).toBe(activeRevision);
	});

	test("returns a late active health observation without undoing revoke", async () => {
		const pending = secondCallDeferredProbe();
		const lifecycle = new ProviderProfileLifecycle({ probe: pending.value });
		const draftRevision = await draft(lifecycle, configurationFixture("revoked-health").configuration);
		await lifecycle.execute({ type: "activate", expectedDraftRevision: draftRevision });
		const activeRevision = requireRevision(lifecycle.snapshot().active?.revision);
		const observation = lifecycle.execute({ type: "health", expectedActiveRevision: activeRevision });
		await pending.entered;
		await lifecycle.execute({ type: "revoke", expectedActiveRevision: activeRevision });

		pending.release({ acceptedForActivation: true, health: READY_HEALTH });

		await expect(observation).resolves.toEqual({
			type: "health",
			revision: activeRevision,
			health: READY_HEALTH,
		});
		expect(() => lifecycle.configuration()).toThrow(lifecycleError("active_missing"));
	});

	test("requires exact revisions for delete and revoke without consuming newer state", async () => {
		const lifecycle = new ProviderProfileLifecycle({ probe: probeFixture().value });
		const firstDraft = await draft(lifecycle, configurationFixture("first-delete").configuration);
		await lifecycle.execute({
			type: "update",
			expectedDraftRevision: firstDraft,
			configuration: configurationFixture("current-delete").configuration,
		});
		const currentDraft = requireRevision(lifecycle.snapshot().draft?.revision);
		await expect(lifecycle.execute({ type: "delete", expectedDraftRevision: firstDraft })).rejects.toEqual(
			lifecycleError("conflict"),
		);
		await lifecycle.execute({ type: "activate", expectedDraftRevision: currentDraft });
		const activeRevision = requireRevision(lifecycle.snapshot().active?.revision);
		await expect(lifecycle.execute({ type: "revoke", expectedActiveRevision: currentDraft })).rejects.toEqual(
			lifecycleError("conflict"),
		);
		expect(lifecycle.snapshot().active?.revision).toBe(activeRevision);
	});

	test("preserves caller cancellation and sanitizes unrelated probe failures", async () => {
		const abort = new DOMException("caller secret", "AbortError");
		const controller = new AbortController();
		controller.abort(abort);
		const lifecycle = new ProviderProfileLifecycle({ probe: probeFixture().value });
		const revision = await draft(lifecycle, configurationFixture("abort").configuration);

		await expect(lifecycle.execute({ type: "test", expectedDraftRevision: revision }, controller.signal)).rejects.toBe(
			abort,
		);

		const failureLifecycle = new ProviderProfileLifecycle({
			probe: {
				inspect: async () => {
					throw new Error("secret endpoint and credential");
				},
			},
		});
		const failureRevision = await draft(failureLifecycle, configurationFixture("failure").configuration);
		await expect(failureLifecycle.execute({ type: "test", expectedDraftRevision: failureRevision })).rejects.toEqual(
			lifecycleError("inspection_failed"),
		);
	});

	test("lets caller cancellation win after an in-flight probe that ignores its signal", async () => {
		const pending = deferredProbe();
		const lifecycle = new ProviderProfileLifecycle({ probe: pending.value });
		const revision = await draft(lifecycle, configurationFixture("mid-abort").configuration);
		const controller = new AbortController();
		const observation = lifecycle.execute({ type: "test", expectedDraftRevision: revision }, controller.signal);
		await pending.entered;
		const reason = new DOMException("caller boundary", "AbortError");
		controller.abort(reason);
		pending.release({ acceptedForActivation: true, health: READY_HEALTH });

		await expect(observation).rejects.toBe(reason);
		expect(lifecycle.snapshot().draft?.revision).toBe(revision);
	});

	test("maps malformed probe output to the closed lifecycle failure", async () => {
		const lifecycle = new ProviderProfileLifecycle({
			probe: {
				inspect: async () => null,
			} as unknown as ProviderProfileProbe,
		});
		const revision = await draft(lifecycle, configurationFixture("malformed").configuration);

		await expect(lifecycle.execute({ type: "test", expectedDraftRevision: revision })).rejects.toEqual(
			lifecycleError("inspection_failed"),
		);
	});

	test.each([
		{
			name: "extra private health fields",
			health: { ...READY_HEALTH, endpoint: "ENDPOINT_SENTINEL", cause: "CAUSE_SENTINEL" },
		},
		{
			name: "an invalid nested dependency",
			health: {
				...READY_HEALTH,
				dependencies: [{ ...READY_HEALTH.dependencies[0], state: "ready", reason: "CREDENTIAL_SENTINEL" }],
			},
		},
	])("rejects $name instead of publishing probe-owned health data", async ({ health }) => {
		const lifecycle = new ProviderProfileLifecycle({
			probe: {
				inspect: async () => ({ acceptedForActivation: true, health }) as never,
			},
		});
		const revision = await draft(lifecycle, configurationFixture("unsafe-health").configuration);

		let failure: unknown;
		try {
			await lifecycle.execute({ type: "test", expectedDraftRevision: revision });
		} catch (error) {
			failure = error;
		}
		expect(failure).toEqual(lifecycleError("inspection_failed"));
		expect(JSON.stringify(failure)).not.toContain("SENTINEL");
		expect(String(failure)).not.toContain("SENTINEL");
	});

	test("publishes one frozen health snapshot independent from later probe mutation", async () => {
		const mutable = structuredClone(READY_HEALTH);
		const lifecycle = new ProviderProfileLifecycle({
			probe: { inspect: async () => ({ acceptedForActivation: true, health: mutable }) },
		});
		const revision = await draft(lifecycle, configurationFixture("captured-health").configuration);

		const result = await lifecycle.execute({ type: "test", expectedDraftRevision: revision });
		if (result.type !== "tested") throw new Error("Expected a test observation.");
		mutable.checkedAt = "2026-09-01T00:00:00.000Z";
		const dependency = mutable.dependencies[0];
		if (dependency) dependency.id = "mutated";

		expect(result.health.checkedAt).toBe("2026-08-31T00:00:00.000Z");
		expect(result.health.dependencies[0]?.id).toBe("provider");
		expect(Object.isFrozen(result.health)).toBe(true);
		expect(Object.isFrozen(result.health.dependencies)).toBe(true);
		expect(Object.isFrozen(result.health.dependencies[0])).toBe(true);
	});

	test("captures the probe method and receiver once at construction", async () => {
		const original: ProviderProfileProbe & { marker: string } = {
			marker: "original",
			inspect() {
				return { acceptedForActivation: this.marker === "original", health: READY_HEALTH };
			},
		};
		const options: { probe: ProviderProfileProbe } = { probe: original };
		const lifecycle = new ProviderProfileLifecycle(options);
		options.probe = probeFixture({ acceptedForActivation: false, health: READY_HEALTH }).value;
		original.inspect = async () => ({ acceptedForActivation: false, health: READY_HEALTH });
		const revision = await draft(lifecycle, configurationFixture("captured-probe").configuration);

		await expect(lifecycle.execute({ type: "test", expectedDraftRevision: revision })).resolves.toMatchObject({
			type: "tested",
			acceptedForActivation: true,
		});
	});
});

function configurationFixture(profileId: string): {
	configuration: ProviderProfileConfiguration;
	catalogReads(): number;
} {
	let reads = 0;
	const model: Model<string> = {
		id: "model",
		name: `${profileId}-model`,
		api: "profile-lifecycle-test",
		provider: profileId,
		baseUrl: `https://${profileId}.invalid`,
		reasoning: false,
		input: ["text"],
		cost: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4 },
		contextWindow: 8192,
		maxTokens: 1024,
	};
	const provider: Provider = {
		id: profileId,
		name: profileId,
		baseUrl: `https://secret-${profileId}.invalid`,
		headers: { Authorization: "secret-header" },
		auth: { apiKey: { name: profileId, resolve: async () => ({ auth: {} }) } },
		getModels: () => {
			reads += 1;
			return [model];
		},
		stream: () => {
			throw new Error("not used");
		},
		streamSimple: () => {
			throw new Error("not used");
		},
	};
	return {
		configuration: {
			profiles: [
				{
					provider,
					credential: { kind: "reference", reference: `opaque\u0000secret-${profileId}` },
				},
			],
			targets: [
				{
					profileId,
					modelId: "model",
					supportsTools: true,
					pricing: { kind: "pi-catalog" },
				},
			],
		},
		catalogReads: () => reads,
	};
}

function probeFixture(result = { acceptedForActivation: true, health: READY_HEALTH }): {
	value: ProviderProfileProbe;
	calls: { configuration: CapturedProviderProfileConfiguration; signal: AbortSignal }[];
} {
	const calls: { configuration: CapturedProviderProfileConfiguration; signal: AbortSignal }[] = [];
	return {
		calls,
		value: {
			inspect: async (configuration, signal) => {
				calls.push({ configuration, signal });
				return result;
			},
		},
	};
}

function deferredProbe(): {
	value: ProviderProfileProbe;
	entered: Promise<void>;
	release(result: { acceptedForActivation: boolean; health: RuntimeHealthSnapshot }): void;
} {
	let enter!: () => void;
	let release!: (result: { acceptedForActivation: boolean; health: RuntimeHealthSnapshot }) => void;
	const entered = new Promise<void>((resolve) => {
		enter = resolve;
	});
	const result = new Promise<{ acceptedForActivation: boolean; health: RuntimeHealthSnapshot }>((resolve) => {
		release = resolve;
	});
	return {
		entered,
		release,
		value: {
			inspect: async () => {
				enter();
				return result;
			},
		},
	};
}

function secondCallDeferredProbe(): {
	value: ProviderProfileProbe;
	entered: Promise<void>;
	release(result: { acceptedForActivation: boolean; health: RuntimeHealthSnapshot }): void;
} {
	const pending = deferredProbe();
	let calls = 0;
	return {
		...pending,
		value: {
			inspect: async (configuration, signal) => {
				calls += 1;
				if (calls === 1) return { acceptedForActivation: true, health: READY_HEALTH };
				return pending.value.inspect(configuration, signal);
			},
		},
	};
}

async function draft(
	lifecycle: ProviderProfileLifecycle,
	configuration: ProviderProfileConfiguration,
): Promise<string> {
	await lifecycle.execute({ type: "draft", configuration });
	return lifecycle.snapshot().draft?.revision ?? "";
}

function lifecycleError(code: ConstructorParameters<typeof ProviderProfileLifecycleError>[0]) {
	return new ProviderProfileLifecycleError(code);
}

function requireRevision(revision: string | undefined): string {
	if (!revision) throw new Error("Expected a lifecycle revision.");
	return revision;
}

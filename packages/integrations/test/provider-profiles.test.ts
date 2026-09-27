import {
	type Context,
	createAssistantMessageEventStream,
	InMemoryCredentialStore,
	type Model,
	type Provider,
} from "@earendil-works/pi-ai";
import type { GameProviderAttemptLifecycle, GameProviderAttemptStreamFn } from "@opengameagent/agent";
import { describe, expect, test } from "vitest";
import { ProviderProfileLifecycle } from "../src/provider-profile-lifecycle.js";
import {
	captureProviderProfileConfiguration,
	captureProviderProfileSnapshot,
	createProviderProfileModelStream,
} from "../src/provider-profiles.js";

const CONTEXT: Context = { messages: [] };

describe("provider profile requests", () => {
	test("keeps an in-flight lifecycle activation while the next request sees the replacement", async () => {
		const first = providerFixture("active", "model", "first");
		const second = providerFixture("active", "model", "second");
		const lifecycle = new ProviderProfileLifecycle({
			probe: {
				inspect: async () => ({
					acceptedForActivation: true,
					health: {
						checkedAt: "2026-08-31T00:00:00.000Z",
						status: "ready",
						dependencies: [],
					},
				}),
			},
		});
		await lifecycle.execute({ type: "draft", configuration: configurationFor(first.provider) });
		const firstDraft = lifecycle.snapshot().draft?.revision;
		if (!firstDraft) throw new Error("Expected the first draft revision.");
		await lifecycle.execute({ type: "activate", expectedDraftRevision: firstDraft });
		const firstActive = lifecycle.snapshot().active?.revision;
		if (!firstActive) throw new Error("Expected the first active revision.");
		const modelStream = createProviderProfileModelStream({ configuration: () => lifecycle.configuration() });
		const delayed = delayedAttempts();
		const firstRequest = attemptStream(modelStream)(first.model, CONTEXT, {}, delayed.lifecycle);

		await lifecycle.execute({ type: "draft", configuration: configurationFor(second.provider) });
		const secondDraft = lifecycle.snapshot().draft?.revision;
		if (!secondDraft) throw new Error("Expected the second draft revision.");
		await lifecycle.execute({
			type: "activate",
			expectedDraftRevision: secondDraft,
			expectedActiveRevision: firstActive,
		});
		delayed.release();

		const firstResult = await (await firstRequest).result();
		const secondResult = await (
			await attemptStream(modelStream)(second.model, CONTEXT, {}, successfulAttempts())
		).result();
		expect(firstResult.content).toEqual([{ type: "text", text: "first" }]);
		expect(secondResult.content).toEqual([{ type: "text", text: "second" }]);
	});

	test("reuses an opaque captured configuration without rereading Pi catalogs or exposing runtime internals", async () => {
		const fixture = providerFixture("captured", "model", "captured");
		const configuration = {
			profiles: [
				{
					provider: {
						...fixture.provider,
						baseUrl: "https://secret-endpoint.invalid",
						headers: { Authorization: "secret-header" },
					},
					credential: { kind: "reference" as const, reference: "opaque\u0000secret-reference" },
				},
			],
			targets: [
				{
					profileId: "captured",
					modelId: "model",
					supportsTools: true,
					pricing: { kind: "pi-catalog" as const },
				},
			],
		};
		const captured = captureProviderProfileConfiguration(configuration);
		const modelStream = createProviderProfileModelStream({
			configuration: () => captured,
			credentialReferences: new InMemoryCredentialStore(),
		});

		await attemptStream(modelStream)(fixture.model, CONTEXT, {}, successfulAttempts());

		expect(fixture.observed.getModels).toBe(1);
		expect(captured.snapshot.targets[0]?.name).toBe("model");
		expect(Object.isFrozen(captured)).toBe(true);
		expect(Reflect.ownKeys(captured)).toEqual(["snapshot"]);
		expect(JSON.stringify(captured)).not.toContain("secret");
	});

	test("registers one immutable configuration identity only once across model requests", async () => {
		const fixture = providerFixture("stable", "model", "stable");
		const configuration = configurationFor(fixture.provider);
		const modelStream = createProviderProfileModelStream({ configuration: () => configuration });

		await (await attemptStream(modelStream)(fixture.model, CONTEXT, {}, successfulAttempts())).result();
		await (await attemptStream(modelStream)(fixture.model, CONTEXT, {}, successfulAttempts())).result();

		expect(fixture.observed.getModels).toBe(1);
		expect(fixture.observed.stream).toBe(2);
	});

	test("dispatches only the first explicit target through Pi with automatic retries disabled", async () => {
		const first = providerFixture("primary", "primary-model", "first");
		const second = providerFixture("secondary", "secondary-model", "second");
		const modelStream = createProviderProfileModelStream({
			configuration: () => ({
				profiles: [
					{ provider: first.provider, credential: { kind: "none" } },
					{ provider: second.provider, credential: { kind: "none" } },
				],
				targets: [
					{
						profileId: "primary",
						modelId: "primary-model",
						supportsTools: true,
						pricing: { kind: "pi-catalog" },
					},
					{
						profileId: "secondary",
						modelId: "secondary-model",
						supportsTools: true,
						pricing: { kind: "unpriced" },
					},
				],
			}),
		});

		const stream = await attemptStream(modelStream)(first.model, CONTEXT, {}, successfulAttempts());
		const result = await stream.result();

		expect(result.content).toEqual([{ type: "text", text: "first" }]);
		expect(first.observed).toEqual({ getModels: 1, stream: 1, maxRetries: 0 });
		expect(second.observed).toEqual({ getModels: 1, stream: 0, maxRetries: undefined });
	});

	test("defaults custom OpenAI-compatible profiles to non-strict tools while preserving explicit opt-in", async () => {
		const fixture = providerFixture("custom-openai", "model", "ok");
		const model = fixture.model as unknown as Model<"openai-completions">;
		model.api = "openai-completions";
		let observedSupportsStrictMode: boolean | undefined;
		const streamSimple = fixture.provider.streamSimple.bind(fixture.provider);
		const provider: Provider = {
			...fixture.provider,
			getModels: () => [model],
			streamSimple(target, context, options) {
				observedSupportsStrictMode = (target as Model<"openai-completions">).compat?.supportsStrictMode;
				return streamSimple(target, context, options);
			},
		};
		const modelStream = createProviderProfileModelStream({ configuration: () => configurationFor(provider) });

		await (await attemptStream(modelStream)(model, CONTEXT, {}, successfulAttempts())).result();

		expect(observedSupportsStrictMode).toBe(false);

		model.compat = { supportsStrictMode: true };
		observedSupportsStrictMode = undefined;
		const optedInStream = createProviderProfileModelStream({ configuration: () => configurationFor(provider) });
		await (await attemptStream(optedInStream)(model, CONTEXT, {}, successfulAttempts())).result();
		expect(observedSupportsStrictMode).toBe(true);
	});

	test("exposes an immutable target snapshot without provider or credential internals", () => {
		const fixture = providerFixture("private-profile", "model", "unused");
		const snapshot = captureProviderProfileSnapshot({
			profiles: [
				{
					provider: {
						...fixture.provider,
						baseUrl: "https://secret-endpoint.invalid",
						headers: { Authorization: "secret-header" },
					},
					credential: { kind: "reference", reference: "opaque\u0000secret-reference" },
				},
			],
			targets: [
				{
					profileId: "private-profile",
					modelId: "model",
					supportsTools: true,
					pricing: {
						kind: "host-rate-card",
						rates: { input: 5, output: 6, cacheRead: 7, cacheWrite: 8 },
					},
				},
			],
		});

		expect(snapshot).toEqual({
			targets: [
				{
					profileId: "private-profile",
					modelId: "model",
					name: "model",
					api: "profile-test",
					reasoning: false,
					input: ["text"],
					contextWindow: 8192,
					maxTokens: 1024,
					supportsTools: true,
					pricing: {
						kind: "host-rate-card",
						rates: { input: 5, output: 6, cacheRead: 7, cacheWrite: 8 },
					},
				},
			],
		});
		expect(JSON.stringify(snapshot)).not.toContain("secret");
		expect(Object.isFrozen(snapshot)).toBe(true);
		expect(Object.isFrozen(snapshot.targets)).toBe(true);
		expect(Object.isFrozen(snapshot.targets[0]?.pricing)).toBe(true);
	});

	test("keeps an in-flight provider and nested model snapshot while host replacement affects the next request", async () => {
		const first = providerFixture("replaceable", "model", "first");
		first.model.headers = { "x-model": "before" };
		first.model.samplingParams = { nested: { value: "before" } };
		(first.model as unknown as { compat: { nested: { value: string } } }).compat = {
			nested: { value: "before" },
		};
		first.model.cost.tiers = [{ inputTokensAbove: 10, input: 11, output: 12, cacheRead: 13, cacheWrite: 14 }];
		let observedModel: Model<string> | undefined;
		let observedProviderReceiver:
			| {
					baseUrl: Provider["baseUrl"];
					headers: Provider["headers"];
					auth: Provider["auth"];
			  }
			| undefined;
		let observedAuthReceiverName: string | undefined;
		const initialApiKey = {
			name: "replaceable",
			async resolve() {
				observedAuthReceiverName = this.name;
				return { auth: {} };
			},
		} satisfies NonNullable<Provider["auth"]["apiKey"]>;
		const initialStream = first.provider.streamSimple.bind(first.provider);
		const initialProvider: Provider = {
			...first.provider,
			baseUrl: "https://before.invalid",
			headers: { "x-provider": "before" },
			auth: { apiKey: initialApiKey },
			streamSimple(model, context, options) {
				observedModel = model;
				observedProviderReceiver = { baseUrl: this.baseUrl, headers: this.headers, auth: this.auth };
				return initialStream(model, context, options);
			},
		};
		const second = providerFixture("replaceable", "model", "second");
		let current: ProviderProfileConfigurationForTest = configurationFor(initialProvider);
		const modelStream = createProviderProfileModelStream({ configuration: () => current });
		const delayed = delayedAttempts();

		const firstStreamPromise = attemptStream(modelStream)(first.model, CONTEXT, {}, delayed.lifecycle);
		current = configurationFor(second.provider);
		first.model.input.push("image");
		if (first.model.headers) first.model.headers["x-model"] = "after";
		if (first.model.samplingParams) first.model.samplingParams.nested = { value: "after" };
		(first.model as unknown as { compat: { nested: { value: string } } }).compat.nested.value = "after";
		if (first.model.cost.tiers?.[0]) first.model.cost.tiers[0].input = 99;
		(initialProvider as { streamSimple: Provider["streamSimple"] }).streamSimple = () => {
			throw new Error("replacement method must not affect the in-flight request");
		};
		(initialProvider as { baseUrl: string }).baseUrl = "https://after.invalid";
		if (initialProvider.headers) initialProvider.headers["x-provider"] = "after";
		(initialApiKey as { name: string }).name = "mutated-auth";
		(initialProvider as { auth: Provider["auth"] }).auth = {
			apiKey: { name: "replacement", resolve: async () => undefined },
		};
		delayed.release();

		const firstResult = await (await firstStreamPromise).result();
		const secondResult = await (
			await attemptStream(modelStream)(first.model, CONTEXT, {}, successfulAttempts())
		).result();

		expect(firstResult.content).toEqual([{ type: "text", text: "first" }]);
		expect(secondResult.content).toEqual([{ type: "text", text: "second" }]);
		expect(observedModel).toMatchObject({
			input: ["text"],
			headers: { "x-model": "before" },
			samplingParams: { nested: { value: "before" } },
			compat: { nested: { value: "before" } },
			cost: { tiers: [{ input: 11 }] },
		});
		expect(Object.isFrozen(observedModel)).toBe(true);
		expect(Object.isFrozen(observedModel?.cost.tiers)).toBe(true);
		expect(observedProviderReceiver).toMatchObject({
			baseUrl: "https://before.invalid",
			headers: { "x-provider": "before" },
			auth: { apiKey: { name: "replaceable" } },
		});
		expect(observedAuthReceiverName).toBe("replaceable");
	});

	test("rejects a Tool-bearing request on a target that explicitly lacks Tool support before an attempt starts", async () => {
		const fixture = providerFixture("text-only", "model", "unused");
		const modelStream = createProviderProfileModelStream({
			configuration: () => ({
				profiles: [{ provider: fixture.provider, credential: { kind: "none" } }],
				targets: [
					{
						profileId: "text-only",
						modelId: "model",
						supportsTools: false,
						pricing: { kind: "unpriced" },
					},
				],
			}),
		});
		let attempts = 0;
		const lifecycle: GameProviderAttemptLifecycle = {
			begin: () => {
				attempts += 1;
				return successfulAttempts().begin(fixture.model);
			},
		};

		await expect(
			attemptStream(modelStream)(
				fixture.model,
				{
					messages: [],
					tools: [
						{
							name: "act",
							description: "act",
							parameters: { type: "object", properties: {}, additionalProperties: false },
						},
					],
				},
				{},
				lifecycle,
			),
		).rejects.toMatchObject({ code: "tools_unsupported" });
		expect(attempts).toBe(0);
		expect(fixture.observed.stream).toBe(0);
	});

	test("commits a referenced request but never falls back to ambient auth when the reference is missing", async () => {
		const fixture = providerFixture("referenced", "model", "unused");
		let ambientResolutions = 0;
		const provider: Provider = {
			...fixture.provider,
			auth: {
				apiKey: {
					name: "referenced",
					resolve: async () => {
						ambientResolutions += 1;
						return { auth: { apiKey: "ambient-secret" } };
					},
				},
			},
		};
		const modelStream = createProviderProfileModelStream({
			configuration: () => ({
				profiles: [
					{
						provider,
						credential: { kind: "reference", reference: "opaque-secret-reference" },
					},
				],
				targets: [
					{
						profileId: "referenced",
						modelId: "model",
						supportsTools: true,
						pricing: { kind: "unpriced" },
					},
				],
			}),
			credentialReferences: new InMemoryCredentialStore(),
		});
		let commits = 0;
		const lifecycle: GameProviderAttemptLifecycle = {
			begin: () => ({
				index: "1",
				discard: () => undefined,
				captureTerminal: () => undefined,
				commit: async (start) => {
					commits += 1;
					return start();
				},
				settle: async () => undefined,
			}),
		};

		const result = await (await attemptStream(modelStream)(fixture.model, CONTEXT, {}, lifecycle)).result();

		expect(result.stopReason).toBe("error");
		expect(result.errorMessage).not.toContain("ambient-secret");
		expect(result.errorMessage).not.toContain("opaque-secret-reference");
		expect(commits).toBe(1);
		expect(ambientResolutions).toBe(0);
		expect(fixture.observed.stream).toBe(0);
	});

	test("does not let a missing credential reference be bypassed by request auth overrides", async () => {
		const fixture = providerFixture("referenced-overrides", "model", "unused");
		const provider: Provider = {
			...fixture.provider,
			auth: {
				apiKey: {
					name: "referenced-overrides",
					resolve: async ({ credential }) => {
						if (credential?.type !== "api_key" || credential.key === undefined) return undefined;
						return { auth: { apiKey: credential.key } };
					},
				},
			},
		};
		const modelStream = createProviderProfileModelStream({
			configuration: () => ({
				profiles: [{ provider, credential: { kind: "reference", reference: "opaque-reference" } }],
				targets: [
					{
						profileId: provider.id,
						modelId: "model",
						supportsTools: true,
						pricing: { kind: "unpriced" },
					},
				],
			}),
			credentialReferences: new InMemoryCredentialStore(),
		});

		const result = await (
			await attemptStream(modelStream)(
				fixture.model,
				CONTEXT,
				{ apiKey: "request-secret", env: { PRIVATE_TOKEN: "request-env-secret" } },
				successfulAttempts(),
			)
		).result();

		expect(result.stopReason).toBe("error");
		expect(result.errorMessage).not.toContain("request-secret");
		expect(result.errorMessage).not.toContain("request-env-secret");
		expect(fixture.observed.stream).toBe(0);
	});

	test("maps a present opaque credential reference into the captured Pi provider", async () => {
		const fixture = providerFixture("referenced", "model", "referenced");
		let dispatchedOptions:
			| {
					apiKey: string | undefined;
					env: Record<string, string> | undefined;
					headers: Record<string, string | null> | undefined;
			  }
			| undefined;
		let headerTransforms = 0;
		const originalStream = fixture.provider.streamSimple.bind(fixture.provider);
		const provider: Provider = {
			...fixture.provider,
			auth: {
				apiKey: {
					name: "referenced",
					resolve: async ({ credential }) => {
						if (credential?.type !== "api_key" || credential.key === undefined) return undefined;
						return { auth: { apiKey: credential.key } };
					},
				},
			},
			streamSimple: (model, context, options) => {
				dispatchedOptions = { apiKey: options?.apiKey, env: options?.env, headers: options?.headers };
				return originalStream(model, context, options);
			},
		};
		const references = new InMemoryCredentialStore();
		await references.modify("opaque-reference", async () => ({ type: "api_key", key: "request-secret" }));
		const modelStream = createProviderProfileModelStream({
			configuration: () => ({
				profiles: [{ provider, credential: { kind: "reference", reference: "opaque-reference" } }],
				targets: [
					{
						profileId: "referenced",
						modelId: "model",
						supportsTools: true,
						pricing: { kind: "unpriced" },
					},
				],
			}),
			credentialReferences: references,
		});

		const requestOptions = {
			apiKey: "override-secret",
			env: { PRIVATE_TOKEN: "override-env-secret" },
			headers: { Authorization: "Bearer override-header-secret" },
			transformHeaders: (headers: Record<string, string | null>) => {
				headerTransforms += 1;
				return { ...headers, "x-transformed-secret": "override-transform-secret" };
			},
		};
		const result = await (
			await attemptStream(modelStream)(fixture.model, CONTEXT, requestOptions, successfulAttempts())
		).result();

		expect(result.content).toEqual([{ type: "text", text: "referenced" }]);
		expect(dispatchedOptions).toEqual({ apiKey: "request-secret", env: undefined, headers: undefined });
		expect(headerTransforms).toBe(0);
	});

	test("reads each provider field and model catalog once for multiple ordered targets", () => {
		const fixture = providerFixture("counted", "one", "unused");
		const secondModel = { ...fixture.model, id: "two", name: "two" };
		let catalogReads = 0;
		const propertyReads = new Map<PropertyKey, number>();
		const provider = new Proxy(
			{
				...fixture.provider,
				getModels: () => {
					catalogReads += 1;
					return [fixture.model, secondModel];
				},
			},
			{
				get: (target, property, receiver) => {
					propertyReads.set(property, (propertyReads.get(property) ?? 0) + 1);
					return Reflect.get(target, property, receiver);
				},
			},
		);

		captureProviderProfileSnapshot({
			profiles: [{ provider, credential: { kind: "none" } }],
			targets: [
				{
					profileId: "counted",
					modelId: "one",
					supportsTools: true,
					pricing: { kind: "pi-catalog" },
				},
				{
					profileId: "counted",
					modelId: "two",
					supportsTools: true,
					pricing: { kind: "unpriced" },
				},
			],
		});

		expect(catalogReads).toBe(1);
		for (const field of [
			"id",
			"name",
			"baseUrl",
			"headers",
			"auth",
			"getModels",
			"stream",
			"streamSimple",
			"fetchDeferred",
			"cancelDeferred",
		]) {
			expect(propertyReads.get(field), field).toBe(1);
		}
	});

	test("accepts one repeated profile object but rejects conflicting objects or credential bindings", () => {
		const fixture = providerFixture("shared", "model", "unused");
		const target = {
			profileId: "shared",
			modelId: "model",
			supportsTools: true,
			pricing: { kind: "unpriced" as const },
		};
		const profile = { provider: fixture.provider, credential: { kind: "none" as const } };

		expect(() => captureProviderProfileSnapshot({ profiles: [profile, profile], targets: [target] })).not.toThrow();
		expect(() =>
			captureProviderProfileSnapshot({
				profiles: [profile, { provider: { ...fixture.provider }, credential: { kind: "none" } }],
				targets: [target],
			}),
		).toThrow(expect.objectContaining({ code: "duplicate_profile" }));
		expect(() =>
			captureProviderProfileSnapshot({
				profiles: [profile, { provider: fixture.provider, credential: { kind: "ambient" } }],
				targets: [target],
			}),
		).toThrow(expect.objectContaining({ code: "duplicate_profile" }));
	});

	test("preserves repeated ordered targets without imposing a separate retry policy", () => {
		const fixture = providerFixture("unique-target", "model", "unused");
		const target = {
			profileId: "unique-target",
			modelId: "model",
			supportsTools: true,
			pricing: { kind: "unpriced" as const },
		};

		const snapshot = captureProviderProfileSnapshot({
			profiles: [{ provider: fixture.provider, credential: { kind: "none" } }],
			targets: [target, target],
		});

		expect(snapshot.targets.map(({ profileId, modelId }) => ({ profileId, modelId }))).toEqual([
			{ profileId: "unique-target", modelId: "model" },
			{ profileId: "unique-target", modelId: "model" },
		]);
	});

	test("allows credential none only when the captured provider resolves as keyless", async () => {
		const fixture = providerFixture("not-keyless", "model", "unused");
		const provider: Provider = {
			...fixture.provider,
			auth: {
				apiKey: {
					name: "not-keyless",
					resolve: async () => ({ auth: { apiKey: "must-not-leak" } }),
				},
			},
		};
		const modelStream = createProviderProfileModelStream({ configuration: () => configurationFor(provider) });

		const result = await (await attemptStream(modelStream)(fixture.model, CONTEXT, {}, successfulAttempts())).result();

		expect(result.stopReason).toBe("error");
		expect(result.errorMessage).toContain("credential_binding_invalid");
		expect(result.errorMessage).not.toContain("must-not-leak");
		expect(fixture.observed.stream).toBe(0);
	});

	test("leaves ambient credential resolution under Pi provider authority", async () => {
		const fixture = providerFixture("ambient", "model", "ambient");
		let resolutions = 0;
		let dispatchedApiKey: string | undefined;
		const originalStream = fixture.provider.streamSimple.bind(fixture.provider);
		const provider: Provider = {
			...fixture.provider,
			auth: {
				apiKey: {
					name: "ambient",
					resolve: async () => {
						resolutions += 1;
						return { auth: { apiKey: "ambient-key" } };
					},
				},
			},
			streamSimple: (model, context, options) => {
				dispatchedApiKey = options?.apiKey;
				return originalStream(model, context, options);
			},
		};
		const configuration = {
			...configurationFor(provider),
			profiles: [{ provider, credential: { kind: "ambient" as const } }],
		};
		const modelStream = createProviderProfileModelStream({ configuration: () => configuration });

		const result = await (await attemptStream(modelStream)(fixture.model, CONTEXT, {}, successfulAttempts())).result();

		expect(result.content).toEqual([{ type: "text", text: "ambient" }]);
		expect({ resolutions, dispatchedApiKey }).toEqual({ resolutions: 1, dispatchedApiKey: "ambient-key" });
	});

	test("removes explicit Pi auth overrides from a keyless binding", async () => {
		const fixture = providerFixture("keyless-overrides", "model", "keyless");
		let dispatchedOptions:
			| {
					apiKey: string | undefined;
					env: Record<string, string> | undefined;
					headers: Record<string, string | null> | undefined;
			  }
			| undefined;
		let headerTransforms = 0;
		const originalStream = fixture.provider.streamSimple.bind(fixture.provider);
		const provider: Provider = {
			...fixture.provider,
			streamSimple: (model, context, options) => {
				dispatchedOptions = { apiKey: options?.apiKey, env: options?.env, headers: options?.headers };
				return originalStream(model, context, options);
			},
		};
		const modelStream = createProviderProfileModelStream({ configuration: () => configurationFor(provider) });

		const requestOptions = {
			apiKey: "request-secret",
			env: { PRIVATE_TOKEN: "request-env-secret" },
			headers: { Authorization: "Bearer request-header-secret" },
			transformHeaders: (headers: Record<string, string | null>) => {
				headerTransforms += 1;
				return { ...headers, "x-transformed-secret": "request-transform-secret" };
			},
		};
		const result = await (
			await attemptStream(modelStream)(fixture.model, CONTEXT, requestOptions, successfulAttempts())
		).result();

		expect(result.content).toEqual([{ type: "text", text: "keyless" }]);
		expect(dispatchedOptions).toEqual({ apiKey: undefined, env: undefined, headers: undefined });
		expect(headerTransforms).toBe(0);
	});

	test("snapshots a selected model with cyclic extension data without recursive freezing", async () => {
		const fixture = providerFixture("cyclic", "model", "cyclic");
		const cyclic: Record<string, unknown> = {};
		cyclic.self = cyclic;
		fixture.model.samplingParams = cyclic;
		let observedModel: Model<string> | undefined;
		const originalStream = fixture.provider.streamSimple.bind(fixture.provider);
		const provider: Provider = {
			...fixture.provider,
			streamSimple: (model, context, options) => {
				observedModel = model as Model<string>;
				return originalStream(model, context, options);
			},
		};
		const modelStream = createProviderProfileModelStream({ configuration: () => configurationFor(provider) });

		await (await attemptStream(modelStream)(fixture.model, CONTEXT, {}, successfulAttempts())).result();

		const captured = observedModel?.samplingParams as Record<string, unknown> | undefined;
		expect(captured?.self).toBe(captured);
		expect(Object.isFrozen(captured)).toBe(true);
	});

	test("does not clone an unselected model from a referenced provider catalog", () => {
		const fixture = providerFixture("selected-catalog", "selected", "unused");
		const unselected = {
			...fixture.model,
			id: "unselected",
			name: "unselected",
			samplingParams: { unsupportedByStructuredClone: () => undefined },
		};
		const provider = { ...fixture.provider, getModels: () => [fixture.model, unselected] };

		expect(() =>
			captureProviderProfileSnapshot({
				profiles: [{ provider, credential: { kind: "none" } }],
				targets: [
					{
						profileId: provider.id,
						modelId: "selected",
						supportsTools: true,
						pricing: { kind: "unpriced" },
					},
				],
			}),
		).not.toThrow();
	});

	test("keeps zero catalog and host rates distinct from explicit free and unpriced facts", () => {
		const fixture = providerFixture("pricing", "model", "unused");
		fixture.model.cost = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 };
		const targets = (
			[
				{ kind: "pi-catalog" },
				{ kind: "host-rate-card", rates: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } },
				{ kind: "free" },
				{ kind: "unpriced" },
			] as const
		).map((pricing, index) => ({
			profileId: "pricing",
			modelId: `model-${index}`,
			supportsTools: true,
			pricing,
		}));
		const models = targets.map((target) => ({ ...fixture.model, id: target.modelId, name: target.modelId }));
		const provider = { ...fixture.provider, getModels: () => models };

		const snapshot = captureProviderProfileSnapshot({
			profiles: [{ provider, credential: { kind: "none" } }],
			targets,
		});

		expect(snapshot.targets.map((target) => target.pricing.kind)).toEqual([
			"pi-catalog",
			"host-rate-card",
			"free",
			"unpriced",
		]);
	});

	test("uses a numeric Pi placeholder without changing an unpriced target into free", async () => {
		const fixture = providerFixture("unpriced-placeholder", "model", "unpriced");
		let dispatchedCost: Model<string>["cost"] | undefined;
		const originalStream = fixture.provider.streamSimple.bind(fixture.provider);
		const provider: Provider = {
			...fixture.provider,
			streamSimple: (model, context, options) => {
				dispatchedCost = model.cost;
				return originalStream(model, context, options);
			},
		};
		const configuration = {
			profiles: [{ provider, credential: { kind: "none" as const } }],
			targets: [
				{
					profileId: provider.id,
					modelId: "model",
					supportsTools: true,
					pricing: { kind: "unpriced" as const },
				},
			],
		};
		const modelStream = createProviderProfileModelStream({ configuration: () => configuration });
		let attemptPricing: unknown;
		const lifecycle: GameProviderAttemptLifecycle = {
			begin: (model, pricing) => {
				attemptPricing = pricing;
				return successfulAttempts().begin(model, pricing);
			},
		};

		await (await attemptStream(modelStream)(fixture.model, CONTEXT, {}, lifecycle)).result();

		expect(dispatchedCost).toEqual({ input: 0, output: 0, cacheRead: 0, cacheWrite: 0 });
		expect(attemptPricing).toEqual({ kind: "unpriced" });
		expect(captureProviderProfileSnapshot(configuration).targets[0]?.pricing).toEqual({ kind: "unpriced" });
	});

	test("sanitizes a referenced provider catalog failure without catching unused providers", async () => {
		const selected = providerFixture("selected", "model", "selected");
		const unused = providerFixture("unused", "model", "unused");
		let unusedCatalogReads = 0;
		const unusedProvider: Provider = {
			...unused.provider,
			getModels: () => {
				unusedCatalogReads += 1;
				throw new Error("unused-secret");
			},
		};
		const selectedConfiguration = {
			profiles: [
				{ provider: selected.provider, credential: { kind: "none" as const } },
				{ provider: unusedProvider, credential: { kind: "none" as const } },
			],
			targets: [
				{
					profileId: "selected",
					modelId: "model",
					supportsTools: true,
					pricing: { kind: "unpriced" as const },
				},
			],
		};
		const modelStream = createProviderProfileModelStream({ configuration: () => selectedConfiguration });

		const result = await (await attemptStream(modelStream)(selected.model, CONTEXT, {}, successfulAttempts())).result();
		expect(result.content).toEqual([{ type: "text", text: "selected" }]);
		expect(unusedCatalogReads).toBe(0);

		const failingProvider = {
			...selected.provider,
			getModels: () => {
				throw new Error("catalog-secret-sentinel");
			},
		};
		let error: unknown;
		try {
			captureProviderProfileSnapshot({
				profiles: [{ provider: failingProvider, credential: { kind: "none" } }],
				targets: selectedConfiguration.targets,
			});
		} catch (caught) {
			error = caught;
		}
		expect(error).toMatchObject({ code: "model_unavailable", message: "model_unavailable" });
		expect(String(error)).not.toContain("catalog-secret-sentinel");
	});
});

type ProviderProfileConfigurationForTest = Parameters<typeof captureProviderProfileSnapshot>[0];

function configurationFor(provider: Provider): ProviderProfileConfigurationForTest {
	return {
		profiles: [{ provider, credential: { kind: "none" } }],
		targets: [
			{
				profileId: provider.id,
				modelId: "model",
				supportsTools: true,
				pricing: { kind: "pi-catalog" },
			},
		],
	};
}

function attemptStream(modelStream: ReturnType<typeof createProviderProfileModelStream>): GameProviderAttemptStreamFn {
	if (typeof modelStream === "function") throw new TypeError("Expected an attempt-aware model stream.");
	return modelStream.stream;
}

function successfulAttempts(): GameProviderAttemptLifecycle {
	return {
		begin: () => ({
			index: "1",
			discard: () => undefined,
			captureTerminal: () => undefined,
			commit: async (start) => start(),
			settle: async () => undefined,
		}),
	};
}

function delayedAttempts(): {
	lifecycle: GameProviderAttemptLifecycle;
	release: () => void;
} {
	let release: () => void = () => undefined;
	const gate = new Promise<void>((resolve) => {
		release = resolve;
	});
	return {
		lifecycle: {
			begin: () => ({
				index: "1",
				discard: () => undefined,
				captureTerminal: () => undefined,
				commit: async (start) => {
					await gate;
					return start();
				},
				settle: async () => undefined,
			}),
		},
		release,
	};
}

function providerFixture(
	providerId: string,
	modelId: string,
	text: string,
): {
	provider: Provider;
	model: Model<string>;
	observed: { getModels: number; stream: number; maxRetries: number | undefined };
} {
	const observed = { getModels: 0, stream: 0, maxRetries: undefined as number | undefined };
	const model: Model<string> = {
		id: modelId,
		name: modelId,
		api: "profile-test",
		provider: providerId,
		baseUrl: `https://${providerId}.invalid`,
		reasoning: false,
		input: ["text"],
		cost: { input: 1, output: 2, cacheRead: 3, cacheWrite: 4 },
		contextWindow: 8192,
		maxTokens: 1024,
	};
	const provider: Provider = {
		id: providerId,
		name: providerId,
		auth: { apiKey: { name: providerId, resolve: async () => ({ auth: {} }) } },
		getModels: () => {
			observed.getModels += 1;
			return [model];
		},
		stream: (target, _context, options) =>
			responseStream(target, text, () => {
				observed.stream += 1;
				observed.maxRetries = options?.maxRetries;
			}),
		streamSimple: (target, _context, options) =>
			responseStream(target, text, () => {
				observed.stream += 1;
				observed.maxRetries = options?.maxRetries;
			}),
	};
	return { provider, model, observed };
}

function responseStream(model: Model<string>, text: string, onStart: () => void) {
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		onStart();
		const message = {
			role: "assistant" as const,
			content: [{ type: "text" as const, text }],
			api: model.api,
			provider: model.provider,
			model: model.id,
			usage: {
				input: 0,
				output: 0,
				cacheRead: 0,
				cacheWrite: 0,
				totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
			},
			stopReason: "stop" as const,
			timestamp: 1,
		};
		stream.push({ type: "start", partial: { ...message, content: [], stopReason: "pending" } });
		stream.push({ type: "done", reason: "stop", message });
	});
	return stream;
}

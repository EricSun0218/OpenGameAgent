import { Buffer } from "node:buffer";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
	type AssistantImages,
	createImagesModels,
	type ImagesApi,
	type ImagesContext,
	type ImagesModel,
	type ImagesModels,
	type ImagesOptions,
} from "@earendil-works/pi-ai";
import { openrouterImagesProvider } from "@earendil-works/pi-ai/providers/openrouter-images";
import { RuntimeHealthMonitor } from "@opengameagent/health";
import {
	InMemoryMediaJobStore,
	LocalGeneratedResourceStore,
	type MediaGenerationProvider,
	type MediaJobLimits,
	MediaJobService,
	MediaCallAdmissionGroup,
	type MediaProviderOperationContext,
	type MediaProviderResource,
	type MediaResourcePublisher,
} from "@opengameagent/media";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	createPiOpenRouterImagesMediaProvider,
	PiImagesMediaProviderUnresolvedError,
} from "../src/pi-openrouter-images-media-provider.js";

const MODEL: ImagesModel<ImagesApi> = {
	id: "image-model",
	name: "Image model",
	api: "openrouter-images",
	provider: "openrouter",
	baseUrl: "https://openrouter.ai/api/v1",
	input: ["text", "image"],
	output: ["image"],
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
};

const LIMITS: MediaJobLimits = {
	maxIdentifierBytes: 128,
	maxAdapterIdBytes: 64,
	maxAdapters: 1,
	maxRequestBytes: 8_192,
	maxJsonDepth: 8,
	maxProviderHandleBytes: 512,
	maxAttemptsPerJob: 4,
	maxOutputsPerAttempt: 4,
	maxResourceBytes: 4_096,
	maxBundleBytes: 8_192,
	maxManifestBytes: 4_096,
	maxMediaTypeBytes: 128,
};

const temporaryDirectories: string[] = [];

afterEach(async () => {
	await Promise.all(temporaryDirectories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe("Pi OpenRouter images media provider", () => {
	it("reports passive media health without issuing a paid health generation", async () => {
		let monotonicNow = 100;
		const generateImages = vi.fn(async () =>
			assistantImages([{ type: "image", mimeType: "image/png", data: Buffer.from([1]).toString("base64") }]),
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages),
			modelId: MODEL.id,
			maxResponseBytes: 1,
			health: {
				id: "image-generation",
				requirement: "required",
				maxObservationAgeMs: 5,
				now: () => monotonicNow,
			},
		});
		const health = new RuntimeHealthMonitor({
			dependencies: provider.healthDependencies(),
			limits: {
				maxDependencies: 1,
				maxDependencyIdBytes: 64,
				maxConcurrentInspections: 1,
				inspectionTimeoutMs: 1_000,
			},
			now: () => Date.parse("2026-09-01T00:00:00.000Z"),
		});

		await expect(health.inspect()).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [{ kind: "media", state: "available" }],
		});
		expect(generateImages).not.toHaveBeenCalled();

		await provider.start({ kind: "image", input: { input: [] } }, operationContext());
		await expect(health.inspect()).resolves.toMatchObject({
			status: "ready",
			dependencies: [{ state: "ready" }],
		});
		expect(generateImages).toHaveBeenCalledOnce();

		monotonicNow = 106;
		await expect(health.inspect()).resolves.toMatchObject({
			status: "not-ready",
			dependencies: [{ state: "available", reason: "stale" }],
		});
		expect(generateImages).toHaveBeenCalledOnce();
	});

	it("maps partial and malformed real media outcomes into passive health", async () => {
		const bytes = Buffer.from([1]);
		const generateImages = vi
			.fn<ImagesModels["generateImages"]>()
			.mockResolvedValueOnce(
				assistantImages([
					{ type: "image", mimeType: "image/png", data: bytes.toString("base64") },
					{ type: "image", mimeType: "image/png", data: "A" },
				]),
			)
			.mockResolvedValueOnce(assistantImages([{ type: "image", mimeType: "image/png", data: "A" }]));
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages),
			modelId: MODEL.id,
			maxResponseBytes: 1,
			health: {
				id: "image-generation",
				requirement: "optional",
				maxObservationAgeMs: 10,
				now: () => 1,
			},
		});
		const inspect = provider.healthDependencies()[0]?.inspect;
		if (!inspect) throw new Error("Expected media health dependency.");

		await provider.start({ kind: "image", input: { input: [] } }, operationContext());
		await expect(inspect(new AbortController().signal)).resolves.toEqual({
			state: "degraded",
			reason: "dependency-failure",
		});

		await provider.start({ kind: "image", input: { input: [] } }, operationContext());
		await expect(inspect(new AbortController().signal)).resolves.toEqual({
			state: "unavailable",
			reason: "dependency-failure",
		});
	});

	it("captures passive media health configuration exactly once", () => {
		let healthReads = 0;
		const healthFieldReads = new Map<PropertyKey, number>();
		const health = new Proxy(
			{
				id: "image-generation",
				requirement: "optional" as const,
				maxObservationAgeMs: 10,
				now: () => 1,
			},
			{
				get(target, property, receiver) {
					healthFieldReads.set(property, (healthFieldReads.get(property) ?? 0) + 1);
					return Reflect.get(target, property, receiver);
				},
			},
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(async () => assistantImages([])),
			modelId: MODEL.id,
			maxResponseBytes: 1,
			get health() {
				healthReads += 1;
				return health;
			},
		});

		expect(healthReads).toBe(1);
		expect(Object.fromEntries(healthFieldReads)).toEqual({
			id: 1,
			requirement: 1,
			maxObservationAgeMs: 1,
			now: 1,
		});
		expect(provider.healthDependencies()).toHaveLength(1);
	});

	it("projects Pi image blocks through one bounded no-retry request and ignores non-resource metadata", async () => {
		const first = Buffer.from([0, 1, 2]);
		const second = Buffer.from([3, 4]);
		const generateImages = vi.fn(
			async (
				model: ImagesModel<ImagesApi>,
				_context: ImagesContext,
				_options?: ImagesOptions,
			): Promise<AssistantImages> =>
				assistantImages(
					[
						{ type: "text", text: "private provider prose" },
						{ type: "image", mimeType: "image/png", data: first.toString("base64") },
						{ type: "text", text: "more private provider prose" },
						{ type: "image", mimeType: "image/webp", data: second.toString("base64").replace(/=+$/u, "") },
					],
					model,
					{
						responseId: "private-response-id",
						errorMessage: "private-error-text",
					},
				),
		);
		const models = fakeModels(generateImages);
		const upstream = vi.fn<typeof fetch>();
		const provider = createPiOpenRouterImagesMediaProvider({
			models,
			modelId: MODEL.id,
			maxResponseBytes: 1,
			fetch: upstream,
		});
		const signal = new AbortController().signal;
		const input = {
			input: [
				{ type: "text", text: "paint a moon" },
				{ type: "image", mimeType: "image/png", data: first.toString("base64") },
			],
		};

		const observation = await provider.start({ kind: "image", input }, operationContext(signal));

		expect(generateImages).toHaveBeenCalledOnce();
		const [selectedModel, context, options] = generateImages.mock.calls[0] ?? [];
		expect(selectedModel).not.toBe(MODEL);
		expect(selectedModel).toEqual(MODEL);
		expect(Object.isFrozen(selectedModel)).toBe(true);
		expect(context).toEqual(input);
		expect(options).toMatchObject({ signal, maxRetries: 0 });
		expect(options?.fetch).not.toBe(upstream);
		expect(observation.status).toBe("succeeded");
		if (observation.status !== "succeeded") throw new Error("Expected generated resources.");
		expect(
			observation.resources.map(({ ordinal, kind, mediaType, declaredBytes }) => ({
				ordinal,
				kind,
				mediaType,
				declaredBytes,
			})),
		).toEqual([
			{ ordinal: 0, kind: "image", mediaType: "image/png", declaredBytes: first.byteLength },
			{ ordinal: 1, kind: "image", mediaType: "image/webp", declaredBytes: second.byteLength },
		]);
		await expect(collect(observation.resources[0])).resolves.toEqual(first);
		await expect(collect(observation.resources[1])).resolves.toEqual(second);
		expect(JSON.stringify(observation)).not.toContain("private provider prose");
		expect(JSON.stringify(observation)).not.toContain("private-response-id");
		expect(JSON.stringify(observation)).not.toContain("private-error-text");
		expect("read" in provider).toBe(false);
		expect("cancel" in provider).toBe(false);
	});

	it("captures the selected Pi model at construction", async () => {
		const mutableModel: ImagesModel<ImagesApi> = {
			...MODEL,
			input: [...MODEL.input],
			output: [...MODEL.output],
			cost: {
				...MODEL.cost,
				tiers: [{ ...MODEL.cost, inputTokensAbove: 10 }],
			},
			headers: { "x-model-header": "original" },
			samplingParams: { quality: "original" },
		};
		let usedModel: ImagesModel<ImagesApi> | undefined;
		const generateImages = vi.fn(async (model: ImagesModel<ImagesApi>) => {
			usedModel = model;
			return assistantImages(
				[{ type: "image", mimeType: "image/png", data: Buffer.from([1]).toString("base64") }],
				model,
			);
		});
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages, mutableModel),
			modelId: mutableModel.id,
			maxResponseBytes: 1,
		});

		mutableModel.id = "changed";
		mutableModel.provider = "changed";
		mutableModel.api = "changed" as ImagesApi;
		mutableModel.baseUrl = "https://changed.invalid";
		mutableModel.input.splice(0);
		mutableModel.output.splice(0);
		mutableModel.cost.input = 99;
		mutableModel.cost.tiers?.splice(0);
		if (mutableModel.headers) mutableModel.headers["x-model-header"] = "changed";
		if (mutableModel.samplingParams) mutableModel.samplingParams.quality = "changed";

		await expect(provider.start({ kind: "image", input: { input: [] } }, operationContext())).resolves.toMatchObject({
			status: "succeeded",
		});
		expect(usedModel).toMatchObject({
			id: MODEL.id,
			provider: MODEL.provider,
			api: MODEL.api,
			baseUrl: MODEL.baseUrl,
			input: MODEL.input,
			output: MODEL.output,
			cost: { input: 0, tiers: [{ inputTokensAbove: 10 }] },
			headers: { "x-model-header": "original" },
			samplingParams: { quality: "original" },
		});
		expect(Object.isFrozen(usedModel?.input)).toBe(true);
		expect(Object.isFrozen(usedModel?.output)).toBe(true);
		expect(Object.isFrozen(usedModel?.cost)).toBe(true);
		expect(Object.isFrozen(usedModel?.cost.tiers)).toBe(true);
		expect(Object.isFrozen(usedModel?.headers)).toBe(true);
		expect(Object.isFrozen(usedModel?.samplingParams)).toBe(true);
	});

	it("uses Pi's real OpenRouter provider, bounded fake HTTP, durable jobs, and original-byte CAS", async () => {
		const first = Buffer.from("first-image");
		const second = Buffer.from("second-image");
		const responseBody = JSON.stringify({
			id: "response-1",
			object: "chat.completion",
			created: 1,
			model: "openai/gpt-image-1",
			choices: [
				{
					index: 0,
					finish_reason: "stop",
					message: {
						role: "assistant",
						content: "provider-only text",
						images: [
							{ type: "image_url", image_url: { url: `data:image/png;base64,${first.toString("base64")}` } },
							{ type: "image_url", image_url: { url: `data:image/webp;base64,${second.toString("base64")}` } },
						],
					},
				},
			],
		});
		const requests: Request[] = [];
		const upstream: typeof fetch = vi.fn(async (input, init) => {
			requests.push(new Request(input, init));
			return new Response(responseBody, {
				status: 200,
				headers: { "content-type": "application/json", "content-length": String(Buffer.byteLength(responseBody)) },
			});
		});
		const provider = createPiOpenRouterImagesMediaProvider({
			models: realOpenRouterModels("test-key"),
			modelId: "openai/gpt-image-1",
			maxResponseBytes: Buffer.byteLength(responseBody),
			fetch: upstream,
		});
		const root = await temporaryDirectory("oga-pi-images-");
		const resources = await LocalGeneratedResourceStore.open({ rootDirectory: join(root, "resources") });
		const { service } = harness(provider, resources);

		const view = await service.submit("pi-openrouter-images", {
			jobId: "image-job",
			kind: "image",
			input: { input: [{ type: "text", text: "paint a forest" }] },
		});

		expect(view).toMatchObject({ status: "succeeded", bundleId: "bundle-1" });
		expect(requests).toHaveLength(1);
		expect(requests[0]?.url).toBe("https://openrouter.ai/api/v1/chat/completions");
		expect(requests[0]?.headers.get("authorization")).toBe("Bearer test-key");
		const payload = (await requests[0]?.clone().json()) as {
			readonly model?: string;
			readonly modalities?: readonly string[];
			readonly messages?: readonly unknown[];
		};
		expect(payload).toMatchObject({ model: "openai/gpt-image-1", modalities: ["image"] });
		expect(payload.messages).toHaveLength(1);
		const bundle = await service.readBundle("image-job", "bundle-1");
		expect(bundle?.resources.map((resource) => resource.ordinal)).toEqual([0, 1]);
		expect(Buffer.from(await resources.read(requiredResource(bundle, 0), readContext(first.byteLength)))).toEqual(
			first,
		);
		expect(Buffer.from(await resources.read(requiredResource(bundle, 1), readContext(second.byteLength)))).toEqual(
			second,
		);
		expect(JSON.stringify(view)).not.toContain("paint a forest");
		expect(JSON.stringify(bundle)).not.toContain("provider-only text");
		expect(JSON.stringify(bundle)).not.toContain("test-key");
	});

	it("rejects local structure and capability mismatches before Pi generation", async () => {
		const generateImages = vi.fn(async () => assistantImages([]));
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages),
			modelId: MODEL.id,
			maxResponseBytes: 1,
		});

		await expect(provider.start({ kind: "video", input: { input: [] } }, operationContext())).resolves.toEqual({
			status: "failed",
			failure: "provider",
		});
		await expect(provider.start({ kind: "image", input: {} }, operationContext())).resolves.toEqual({
			status: "failed",
			failure: "malformed",
		});
		await expect(
			provider.start(
				{ kind: "image", input: { input: [{ type: "image", mimeType: "image/png", data: "not-base64" }] } },
				operationContext(),
			),
		).resolves.toEqual({ status: "failed", failure: "malformed" });
		const textOnlyModel: ImagesModel<ImagesApi> = { ...MODEL, input: ["text"] };
		const textOnly = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages, textOnlyModel),
			modelId: MODEL.id,
			maxResponseBytes: 1,
		});
		await expect(
			textOnly.start(
				{
					kind: "image",
					input: { input: [{ type: "image", mimeType: "image/png", data: Buffer.from([1]).toString("base64") }] },
				},
				operationContext(),
			),
		).resolves.toEqual({ status: "failed", failure: "provider" });
		expect(generateImages).not.toHaveBeenCalled();
	});

	it("preserves output slots when only some Pi image blocks are malformed", async () => {
		const bytes = Buffer.from([1, 2, 3]);
		const generateImages = vi.fn(async () =>
			assistantImages([
				{ type: "image", mimeType: "image/png", data: bytes.toString("base64") },
				{ type: "text", text: "ignored" },
				{ type: "image", mimeType: "image/png", data: "A" },
				{ type: "image", mimeType: "", data: bytes.toString("base64") },
			]),
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages),
			modelId: MODEL.id,
			maxResponseBytes: 1,
		});

		const partial = await provider.start({ kind: "image", input: { input: [] } }, operationContext());

		expect(partial.status).toBe("partially_succeeded");
		if (partial.status !== "partially_succeeded") throw new Error("Expected a partial result.");
		expect(partial.resources.map((resource) => resource.ordinal)).toEqual([0]);
		expect(partial.failures).toEqual([
			{ ordinal: 1, failure: "malformed" },
			{ ordinal: 2, failure: "malformed" },
		]);
		await expect(collect(partial.resources[0])).resolves.toEqual(bytes);

		generateImages.mockResolvedValueOnce(assistantImages([{ type: "text", text: "only text" }]));
		await expect(provider.start({ kind: "image", input: { input: [] } }, operationContext())).resolves.toEqual({
			status: "failed",
			failure: "malformed",
		});
		generateImages.mockResolvedValueOnce(assistantImages([{ type: "image", mimeType: "image/png", data: "A" }]));
		await expect(provider.start({ kind: "image", input: { input: [] } }, operationContext())).resolves.toEqual({
			status: "failed",
			failure: "malformed",
		});
	});

	it("keeps Pi's flattened failures unresolved, never retries automatically, and requires explicit restart", async () => {
		const generateImages = vi.fn(
			async (_model: ImagesModel<ImagesApi>, _context: ImagesContext, _options?: ImagesOptions) =>
				assistantImages([], MODEL, { stopReason: "error", errorMessage: "secret 429 body" }),
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages),
			modelId: MODEL.id,
			maxResponseBytes: 1,
		});

		await expect(provider.start({ kind: "image", input: { input: [] } }, operationContext())).rejects.toBeInstanceOf(
			PiImagesMediaProviderUnresolvedError,
		);
		const { service, store, resources } = harness(provider);
		const first = await service.submit("pi-openrouter-images", {
			jobId: "unknown-job",
			kind: "image",
			input: { input: [] },
		});

		expect(first).toMatchObject({ status: "outcome_unknown", disposition: "active" });
		expect(generateImages).toHaveBeenCalledTimes(2);
		expect(resources.publish).not.toHaveBeenCalled();
		expect(JSON.stringify(await store.read("unknown-job"))).not.toContain("secret 429 body");
		await service.cancel({ jobId: "unknown-job" });
		const restarted = await service.restart({ jobId: "unknown-job" });
		expect(restarted.status).toBe("outcome_unknown");
		expect(generateImages).toHaveBeenCalledTimes(3);
		for (const call of generateImages.mock.calls) expect(call[2]?.maxRetries).toBe(0);
	});

	it("retains a terminal Pi result before giving a racing caller abort exact precedence", async () => {
		let resolveGeneration: ((result: AssistantImages) => void) | undefined;
		const generateImages = vi.fn(
			async () =>
				await new Promise<AssistantImages>((resolve) => {
					resolveGeneration = resolve;
				}),
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages),
			modelId: MODEL.id,
			maxResponseBytes: 1,
		});
		const controller = new AbortController();
		const reason = Object.freeze({ source: "caller" });
		const { service } = harness(provider);
		const operation = service.submit(
			"pi-openrouter-images",
			{ jobId: "abort-after-terminal", kind: "image", input: { input: [] } },
			controller.signal,
		);
		await vi.waitFor(() => expect(generateImages).toHaveBeenCalledOnce());

		controller.abort(reason);
		resolveGeneration?.(
			assistantImages([{ type: "image", mimeType: "image/png", data: Buffer.from([1]).toString("base64") }]),
		);

		await expect(operation).rejects.toBe(reason);
		await expect(service.read("abort-after-terminal")).resolves.toMatchObject({
			status: "succeeded",
			bundleId: "bundle-1",
		});
		generateImages.mockResolvedValueOnce(assistantImages([], MODEL, { stopReason: "aborted" }));
		await expect(provider.start({ kind: "image", input: { input: [] } }, operationContext())).rejects.toBeInstanceOf(
			PiImagesMediaProviderUnresolvedError,
		);
	});

	it("preserves the exact caller reason when Pi flattens in-flight cancellation", async () => {
		let resolveGeneration: ((result: AssistantImages) => void) | undefined;
		const generateImages = vi.fn(
			async () =>
				await new Promise<AssistantImages>((resolve) => {
					resolveGeneration = resolve;
				}),
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages),
			modelId: MODEL.id,
			maxResponseBytes: 1,
		});
		const controller = new AbortController();
		const reason = Object.freeze({ source: "caller", phase: "provider" });
		const { service } = harness(provider);
		const operation = service.submit(
			"pi-openrouter-images",
			{ jobId: "abort-flattened", kind: "image", input: { input: [] } },
			controller.signal,
		);
		await vi.waitFor(() => expect(generateImages).toHaveBeenCalledOnce());

		controller.abort(reason);
		resolveGeneration?.(assistantImages([], MODEL, { stopReason: "aborted" }));

		await expect(operation).rejects.toBe(reason);
		await expect(service.read("abort-flattened")).resolves.toMatchObject({ status: "outcome_unknown" });
	});

	it("bounds the complete Pi HTTP response before JSON and base64 materialization", async () => {
		const responseBody = JSON.stringify({
			id: "response-too-large",
			object: "chat.completion",
			created: 1,
			model: "openai/gpt-image-1",
			choices: [],
		});
		const upstream: typeof fetch = vi.fn(
			async () =>
				new Response(responseBody, {
					status: 200,
					headers: { "content-type": "application/json", "content-length": String(Buffer.byteLength(responseBody)) },
				}),
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: realOpenRouterModels("test-key"),
			modelId: "openai/gpt-image-1",
			maxResponseBytes: Buffer.byteLength(responseBody) - 1,
			fetch: upstream,
		});
		const { service } = harness(provider);

		await expect(
			service.submit("pi-openrouter-images", { jobId: "bounded", kind: "image", input: { input: [] } }),
		).resolves.toMatchObject({
			status: "outcome_unknown",
		});
		expect(upstream).toHaveBeenCalledOnce();
	});

	it("rejects and cancels an unexpected Pi SSE response", async () => {
		const cancelBody = vi.fn();
		const upstream: typeof fetch = vi.fn(
			async () =>
				new Response(
					new ReadableStream<Uint8Array>({
						cancel: cancelBody,
					}),
					{ status: 200, headers: { "content-type": "text/event-stream" } },
				),
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: realOpenRouterModels("test-key"),
			modelId: "openai/gpt-image-1",
			maxResponseBytes: 128,
			fetch: upstream,
		});
		const { service, store } = harness(provider);

		await expect(
			service.submit("pi-openrouter-images", { jobId: "unexpected-sse", kind: "image", input: { input: [] } }),
		).resolves.toMatchObject({ status: "outcome_unknown" });
		expect(upstream).toHaveBeenCalledOnce();
		await vi.waitFor(() => expect(cancelBody).toHaveBeenCalledOnce());
		expect(JSON.stringify(await store.read("unexpected-sse"))).not.toContain("text/event-stream");
	});

	it("disables both Pi and SDK retries for a real OpenRouter HTTP failure", async () => {
		const responseBody = JSON.stringify({ error: { message: "private throttle body", type: "rate_limit_error" } });
		const upstream: typeof fetch = vi.fn(
			async () =>
				new Response(responseBody, {
					status: 429,
					headers: { "content-type": "application/json", "content-length": String(Buffer.byteLength(responseBody)) },
				}),
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: realOpenRouterModels("test-key"),
			modelId: "openai/gpt-image-1",
			maxResponseBytes: Buffer.byteLength(responseBody),
			fetch: upstream,
		});
		const { service, store } = harness(provider);

		await expect(
			service.submit("pi-openrouter-images", { jobId: "throttled", kind: "image", input: { input: [] } }),
		).resolves.toMatchObject({
			status: "outcome_unknown",
		});
		expect(upstream).toHaveBeenCalledOnce();
		expect(JSON.stringify(await store.read("throttled"))).not.toContain("private throttle body");
	});

	it("lets existing media limits reject declared output size before the lazy bytes are read", async () => {
		const bytes = Buffer.from([1, 2, 3, 4]);
		const generateImages = vi.fn(async () =>
			assistantImages([{ type: "image", mimeType: "image/png", data: bytes.toString("base64") }]),
		);
		const provider = createPiOpenRouterImagesMediaProvider({
			models: fakeModels(generateImages),
			modelId: MODEL.id,
			maxResponseBytes: 1,
		});
		const resources: MediaResourcePublisher = { publish: vi.fn(async () => fail("bytes must remain lazy")) };
		const { service } = harness(provider, resources, {
			...LIMITS,
			maxResourceBytes: bytes.byteLength - 1,
			maxBundleBytes: bytes.byteLength - 1,
		});

		await expect(
			service.submit("pi-openrouter-images", { jobId: "capacity", kind: "image", input: { input: [] } }),
		).resolves.toMatchObject({
			status: "failed",
			failure: "capacity",
		});
		expect(resources.publish).not.toHaveBeenCalled();
	});

	it("fails construction when the exact Pi OpenRouter image model or response budget is unavailable", () => {
		const models = fakeModels(async () => assistantImages([]));
		expect(() => createPiOpenRouterImagesMediaProvider({ models, modelId: "missing", maxResponseBytes: 1 })).toThrow(
			TypeError,
		);
		expect(() =>
			createPiOpenRouterImagesMediaProvider({
				models: fakeModels(async () => assistantImages([]), { ...MODEL, output: ["text"] }),
				modelId: MODEL.id,
				maxResponseBytes: 1,
			}),
		).toThrow(TypeError);
		expect(() => createPiOpenRouterImagesMediaProvider({ models, modelId: MODEL.id, maxResponseBytes: 0 })).toThrow(
			TypeError,
		);
	});

	it("keeps missing Pi authentication unresolved without issuing HTTP", async () => {
		const upstream = vi.fn<typeof fetch>();
		const provider = createPiOpenRouterImagesMediaProvider({
			models: realOpenRouterModels(),
			modelId: "openai/gpt-image-1",
			maxResponseBytes: 1,
			fetch: upstream,
		});
		const { service } = harness(provider);

		await expect(
			service.submit("pi-openrouter-images", { jobId: "missing-auth", kind: "image", input: { input: [] } }),
		).resolves.toMatchObject({
			status: "outcome_unknown",
		});
		expect(upstream).not.toHaveBeenCalled();
	});
});

function fakeModels(
	generateImages: ImagesModels["generateImages"],
	model: ImagesModel<ImagesApi> = MODEL,
): ImagesModels {
	return {
		getProviders: () => [],
		getProvider: () => undefined,
		getModels: (provider) => (provider === undefined || provider === model.provider ? [model] : []),
		getModel: (provider, id) => (provider === model.provider && id === model.id ? model : undefined),
		refresh: async () => undefined,
		getAuth: async () => undefined,
		generateImages,
	};
}

function realOpenRouterModels(apiKey?: string) {
	const models = createImagesModels({
		authContext: {
			env: async (name) => (name === "OPENROUTER_API_KEY" ? apiKey : undefined),
			fileExists: async () => false,
		},
	});
	models.setProvider(openrouterImagesProvider());
	return models;
}

function assistantImages(
	output: AssistantImages["output"],
	model: ImagesModel<ImagesApi> = MODEL,
	overrides: Partial<AssistantImages> = {},
): AssistantImages {
	return {
		api: model.api,
		provider: model.provider,
		model: model.id,
		output,
		stopReason: "stop",
		timestamp: 1,
		...overrides,
	};
}

function operationContext(signal = new AbortController().signal): MediaProviderOperationContext {
	return { jobId: "job", attemptId: "attempt", submissionKey: "submission", signal };
}

async function collect(resource: MediaProviderResource | undefined): Promise<Buffer> {
	if (!resource) throw new Error("The generated resource is missing.");
	if (resource.bytes instanceof Uint8Array) return Buffer.from(resource.bytes);
	if ("read" in resource.bytes) {
		return Buffer.from(await resource.bytes.read(new AbortController().signal));
	}
	const chunks: Uint8Array[] = [];
	for await (const chunk of resource.bytes) chunks.push(chunk);
	return Buffer.concat(chunks);
}

function publisher(): MediaResourcePublisher & { publish: ReturnType<typeof vi.fn> } {
	return {
		publish: vi.fn(async (resource: Readonly<MediaProviderResource>) => {
			const bytes = await collect(resource);
			const sha256 = (await import("node:crypto")).createHash("sha256").update(bytes).digest("hex");
			return {
				resourceId: sha256,
				kind: resource.kind,
				mediaType: resource.mediaType,
				byteLength: bytes.byteLength,
				sha256,
				ordinal: resource.ordinal,
			};
		}),
	};
}

function harness(
	provider: MediaGenerationProvider,
	resources: MediaResourcePublisher = publisher(),
	limits: MediaJobLimits = LIMITS,
) {
	const store = new InMemoryMediaJobStore(limits);
	let now = 1_800_000_000_000;
	return {
		resources: resources as MediaResourcePublisher & { publish: ReturnType<typeof vi.fn> },
		store,
		service: new MediaJobService({
			adapters: [
				{
					adapterId: "pi-openrouter-images",
					provider,
					admission: new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 1 }),
				},
			],
			store,
			resources,
			limits,
			newId: idFactory(),
			now: () => now++,
		}),
	};
}

function idFactory() {
	const counts = { attempt: 0, submission: 0, bundle: 0 };
	return (kind: keyof typeof counts) => `${kind}-${++counts[kind]}`;
}

async function temporaryDirectory(prefix: string): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), prefix));
	temporaryDirectories.push(directory);
	return directory;
}

function requiredResource(bundle: Awaited<ReturnType<MediaJobService["readBundle"]>>, ordinal: number) {
	const resource = bundle?.resources.find((candidate) => candidate.ordinal === ordinal);
	if (!resource) throw new Error("Expected generated resource.");
	return resource;
}

function readContext(maxBytes: number) {
	return { maxBytes, maxMediaTypeBytes: 256, signal: new AbortController().signal };
}

function fail(message: string): never {
	throw new Error(message);
}

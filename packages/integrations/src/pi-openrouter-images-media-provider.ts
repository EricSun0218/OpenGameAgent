import type { AssistantImages, ImagesApi, ImagesContext, ImagesModel, ImagesModels } from "@earendil-works/pi-ai";
import { createBoundedFetch } from "@opengameagent/client";
import {
	type RuntimeDependencyDefinition,
	type RuntimeDependencyInspection,
	RuntimePassiveHealthDependency,
} from "@opengameagent/health";
import type {
	MediaFailureKind,
	MediaGenerationProvider,
	MediaGenerationRequest,
	MediaProviderObservation,
	MediaProviderOperationContext,
	MediaProviderResource,
} from "@opengameagent/media";
import { decodeCanonicalBase64, inspectCanonicalBase64 } from "./strict-base64.js";

const OPENROUTER_PROVIDER = "openrouter";
const OPENROUTER_IMAGES_API = "openrouter-images";

export interface CreatePiOpenRouterImagesMediaProviderOptions {
	readonly models: ImagesModels;
	readonly modelId: string;
	/** Complete HTTP response budget supplied by the host; this Adapter has no default. */
	readonly maxResponseBytes: number;
	readonly fetch?: typeof fetch;
	readonly health?: {
		readonly id: string;
		readonly requirement: "required" | "optional";
		readonly maxObservationAgeMs: number;
		readonly now: () => number;
	};
}

export interface PiOpenRouterImagesMediaProvider extends MediaGenerationProvider {
	healthDependencies(): readonly RuntimeDependencyDefinition[];
}

export class PiImagesMediaProviderUnresolvedError extends Error {
	constructor() {
		super("Pi image generation did not establish an authoritative terminal outcome.");
		this.name = "PiImagesMediaProviderUnresolvedError";
	}
}

/**
 * Adapts one host-selected Pi OpenRouter image model to the durable media port.
 * Pi owns auth, request construction, the provider call, and response parsing.
 */
export function createPiOpenRouterImagesMediaProvider(
	options: Readonly<CreatePiOpenRouterImagesMediaProviderOptions>,
): PiOpenRouterImagesMediaProvider {
	if (!options || typeof options !== "object" || typeof options.models?.getModel !== "function") {
		throw new TypeError("Pi ImagesModels configuration is required.");
	}
	const selectedModel = options.models.getModel(OPENROUTER_PROVIDER, options.modelId);
	if (
		!selectedModel ||
		selectedModel.id !== options.modelId ||
		selectedModel.provider !== OPENROUTER_PROVIDER ||
		selectedModel.api !== OPENROUTER_IMAGES_API ||
		!Array.isArray(selectedModel.input) ||
		!Array.isArray(selectedModel.output) ||
		!selectedModel.output.includes("image")
	) {
		throw new TypeError("The selected Pi OpenRouter image model is unavailable.");
	}
	if (typeof options.models.generateImages !== "function") {
		throw new TypeError("Pi ImagesModels generation is required.");
	}
	const model = captureModel(selectedModel);
	const fetch = createImagesFetch(options.fetch ?? globalThis.fetch, options.maxResponseBytes);
	const generateImages = options.models.generateImages.bind(options.models);
	const healthOptions = options.health;
	const health = healthOptions ? new RuntimePassiveHealthDependency({ ...healthOptions, kind: "media" }) : undefined;
	health?.observe({ state: "available" });
	const healthDependencies = Object.freeze(health ? [health.definition] : []);

	return Object.freeze({
		healthDependencies: () => healthDependencies,
		async start(
			request: Readonly<MediaGenerationRequest>,
			context: Readonly<MediaProviderOperationContext>,
		): Promise<MediaProviderObservation> {
			context.signal.throwIfAborted();
			const captured = captureContext(request, model);
			if (captured.failure !== undefined) return { status: "failed", failure: captured.failure };
			let result: AssistantImages;
			try {
				result = await generateImages(model, captured.context, {
					signal: context.signal,
					fetch,
					maxRetries: 0,
				});
			} catch (error) {
				context.signal.throwIfAborted();
				observeHealth(health, { state: "unavailable", reason: "unknown" });
				throw error;
			}
			if (
				result.api !== model.api ||
				result.provider !== model.provider ||
				result.model !== model.id ||
				result.stopReason !== "stop"
			) {
				context.signal.throwIfAborted();
				observeHealth(health, { state: "unavailable", reason: "unknown" });
				throw new PiImagesMediaProviderUnresolvedError();
			}
			const observation = captureResult(result.output);
			observeHealth(health, mediaHealthInspection(observation));
			return observation;
		},
	});
}

function mediaHealthInspection(observation: MediaProviderObservation): RuntimeDependencyInspection {
	switch (observation.status) {
		case "succeeded":
			return { state: "ready" };
		case "partially_succeeded":
			return { state: "degraded", reason: "dependency-failure" };
		case "pending":
		case "cancelled":
			return { state: "available" };
		case "failed":
			switch (observation.failure) {
				case "authentication":
					return { state: "unavailable", reason: "authentication-required" };
				case "permission":
					return { state: "unavailable", reason: "permission-denied" };
				case "transport":
					return { state: "unavailable", reason: "transport" };
				case "capacity":
					return { state: "unavailable", reason: "capacity" };
				case "provider":
				case "malformed":
				case "resource":
					return { state: "unavailable", reason: "dependency-failure" };
				case "unknown":
					return { state: "unavailable", reason: "unknown" };
			}
	}
}

function observeHealth(
	health: RuntimePassiveHealthDependency | undefined,
	inspection: RuntimeDependencyInspection,
): void {
	try {
		health?.observe(inspection);
	} catch {
		// A host health clock must never change the authoritative media result.
	}
}

function captureModel(source: Readonly<ImagesModel<ImagesApi>>): ImagesModel<ImagesApi> {
	const input = [...source.input];
	const output = [...source.output];
	Object.freeze(input);
	Object.freeze(output);
	const tiers = source.cost.tiers?.map((tier) => Object.freeze({ ...tier }));
	if (tiers) Object.freeze(tiers);
	const cost = Object.freeze({ ...source.cost, ...(tiers ? { tiers } : {}) });
	const headers = source.headers ? Object.freeze({ ...source.headers }) : undefined;
	const samplingParams = source.samplingParams ? Object.freeze({ ...source.samplingParams }) : undefined;
	return Object.freeze({
		...source,
		input,
		output,
		cost,
		...(headers ? { headers } : {}),
		...(samplingParams ? { samplingParams } : {}),
	});
}

type CapturedContext =
	| { readonly context: ImagesContext; readonly failure?: never }
	| { readonly context?: never; readonly failure: MediaFailureKind };

function captureContext(
	request: Readonly<MediaGenerationRequest>,
	model: Readonly<ImagesModel<ImagesApi>>,
): CapturedContext {
	if (request.kind !== "image") return { failure: "provider" };
	const value = request.input;
	if (!isRecord(value) || !Array.isArray(value.input)) return { failure: "malformed" };
	const input: ImagesContext["input"][number][] = [];
	for (const part of value.input) {
		if (!isRecord(part)) return { failure: "malformed" };
		if (part.type === "text") {
			if (typeof part.text !== "string") return { failure: "malformed" };
			if (!model.input.includes("text")) return { failure: "provider" };
			input.push({ type: "text", text: part.text });
			continue;
		}
		if (part.type !== "image" || typeof part.mimeType !== "string" || part.mimeType.length === 0) {
			return { failure: "malformed" };
		}
		if (!model.input.includes("image")) return { failure: "provider" };
		const base64 = inspectCanonicalBase64(part.data);
		if (!base64) return { failure: "malformed" };
		input.push({ type: "image", mimeType: part.mimeType, data: base64.data });
	}
	return { context: { input } };
}

function captureResult(output: AssistantImages["output"]): MediaProviderObservation {
	const resources: MediaProviderResource[] = [];
	const failures: { ordinal: number; failure: "malformed" }[] = [];
	let ordinal = 0;
	for (const part of output) {
		if (part.type !== "image") continue;
		const currentOrdinal = ordinal++;
		const base64 = inspectCanonicalBase64(part.data);
		if (!base64 || typeof part.mimeType !== "string" || part.mimeType.length === 0) {
			failures.push({ ordinal: currentOrdinal, failure: "malformed" });
			continue;
		}
		resources.push({
			ordinal: currentOrdinal,
			kind: "image",
			mediaType: part.mimeType,
			declaredBytes: base64.byteLength,
			bytes: decodeCanonicalBase64(base64),
		});
	}
	if (resources.length === 0) return { status: "failed", failure: "malformed" };
	if (failures.length > 0) return { status: "partially_succeeded", resources, failures };
	return { status: "succeeded", resources };
}

function createImagesFetch(upstream: typeof fetch, maxResponseBytes: number): typeof fetch {
	const bounded = createBoundedFetch(upstream, maxResponseBytes, maxResponseBytes);
	return async (input, init) => {
		const response = await bounded(input, init);
		if (response.headers.get("content-type")?.toLowerCase().includes("text/event-stream")) {
			await response.body?.cancel().catch(() => undefined);
			throw new PiImagesMediaProviderUnresolvedError();
		}
		return response;
	};
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

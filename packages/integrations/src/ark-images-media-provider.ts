import { Buffer } from "node:buffer";
import type { CredentialStore } from "@earendil-works/pi-ai";
import { createBoundedFetch } from "@opengameagent/client";
import type {
	MediaFailureKind,
	MediaGenerationProvider,
	MediaGenerationRequest,
	MediaProviderObservation,
	MediaProviderOperationContext,
	MediaProviderResource,
} from "@opengameagent/media";
import {
	canonicalizeJsonValueWithin,
	JsonValueLimitError,
	parseStrictJsonText,
	raceOperationWithAbort,
	StrictJsonError,
} from "@opengameagent/protocol";
import { decodeCanonicalBase64, inspectCanonicalBase64 } from "./strict-base64.js";

export type ArkImageOutputFormat = "jpeg" | "png";

/** Every value is mandatory host configuration; this Adapter supplies no workload defaults. */
export interface ArkImagesProviderLimits {
	readonly maxEndpointBytes: number;
	readonly maxCredentialReferenceBytes: number;
	readonly maxCredentialBytes: number;
	readonly maxModelBytes: number;
	readonly maxSizeBytes: number;
	readonly maxPromptBytes: number;
	readonly maxReferenceImages: number;
	readonly maxReferenceBytes: number;
	readonly maxReferenceTotalBytes: number;
	readonly maxRequestBytes: number;
	readonly maxResponseBytes: number;
	readonly maxResponseJsonDepth: number;
	readonly maxOutputs: number;
	readonly maxOutputBytes: number;
	readonly maxOutputUrlBytes: number;
	readonly requestTimeoutMs: number;
}

export interface ArkImageUrlOutputPolicy {
	readonly accept: (url: string, signal: AbortSignal) => boolean | Promise<boolean>;
	readonly fetch?: typeof fetch;
}

export interface CreateArkImagesMediaProviderOptions {
	readonly credentials: CredentialStore;
	readonly credentialReference: string;
	readonly endpoint: string;
	readonly modelId: string;
	readonly outputFormat: ArkImageOutputFormat;
	readonly size: string;
	readonly watermark: boolean;
	readonly limits: ArkImagesProviderLimits;
	readonly fetch?: typeof fetch;
	readonly urlOutputs?: ArkImageUrlOutputPolicy;
}

export type ArkImagesAdapterErrorCode = "capacity" | "invalid_configuration" | "request_timeout" | "transport";

export class ArkImagesAdapterError extends Error {
	constructor(public readonly code: ArkImagesAdapterErrorCode) {
		super(`The Ark images Adapter failed (${code}).`);
		this.name = "ArkImagesAdapterError";
	}
}

interface CapturedArkImagesOptions {
	readonly credentials: CredentialStore;
	readonly credentialReference: string;
	readonly endpoint: string;
	readonly modelId: string;
	readonly outputFormat: ArkImageOutputFormat;
	readonly size: string;
	readonly watermark: boolean;
	readonly limits: Readonly<ArkImagesProviderLimits>;
	readonly fetch: typeof fetch;
	readonly urlOutputs?: {
		readonly accept: ArkImageUrlOutputPolicy["accept"];
		readonly fetch: typeof fetch;
	};
}

const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;
const LIMIT_NAMES = [
	"maxEndpointBytes",
	"maxCredentialReferenceBytes",
	"maxCredentialBytes",
	"maxModelBytes",
	"maxSizeBytes",
	"maxPromptBytes",
	"maxReferenceImages",
	"maxReferenceBytes",
	"maxReferenceTotalBytes",
	"maxRequestBytes",
	"maxResponseBytes",
	"maxResponseJsonDepth",
	"maxOutputs",
	"maxOutputBytes",
	"maxOutputUrlBytes",
	"requestTimeoutMs",
] as const satisfies readonly (keyof ArkImagesProviderLimits)[];

export function createArkImagesMediaProvider(
	options: Readonly<CreateArkImagesMediaProviderOptions>,
): MediaGenerationProvider {
	const captured = captureOptions(options);
	return Object.freeze({
		async start(
			request: Readonly<MediaGenerationRequest>,
			context: Readonly<MediaProviderOperationContext>,
		): Promise<MediaProviderObservation> {
			context.signal.throwIfAborted();
			const input = captureInput(request, captured.limits);
			if ("failure" in input) return { status: "failed", failure: input.failure };
			let body: string;
			try {
				body = canonicalizeJsonValueWithin(
					{
						model: captured.modelId,
						output_format: captured.outputFormat,
						prompt: input.prompt,
						response_format: "b64_json",
						size: captured.size,
						watermark: captured.watermark,
						...(input.references.length === 0 ? {} : { image: input.references }),
					},
					{ maxBytes: captured.limits.maxRequestBytes, maxDepth: 3 },
				);
			} catch (error) {
				if (error instanceof JsonValueLimitError) return { status: "failed", failure: "capacity" };
				throw error;
			}
			const deadline = providerDeadline(context.signal, captured.limits.requestTimeoutMs);
			try {
				let credential: Awaited<ReturnType<CredentialStore["read"]>>;
				try {
					credential = await raceOperationWithAbort(
						() => captured.credentials.read(captured.credentialReference, { signal: deadline.signal }),
						deadline.signal,
					);
				} catch {
					context.signal.throwIfAborted();
					deadline.signal.throwIfAborted();
					throw new ArkImagesAdapterError("transport");
				}
				deadline.signal.throwIfAborted();
				let credentialType: unknown;
				let credentialKey: unknown;
				try {
					credentialType = credential?.type;
					credentialKey = credentialType === "api_key" ? credential?.key : undefined;
				} catch {
					return { status: "failed", failure: "authentication" };
				}
				if (credentialType !== "api_key" || typeof credentialKey !== "string" || credentialKey.length === 0) {
					return { status: "failed", failure: "authentication" };
				}
				if (Buffer.byteLength(credentialKey, "utf8") > captured.limits.maxCredentialBytes) {
					return { status: "failed", failure: "capacity" };
				}
				let outbound: Request;
				try {
					outbound = new Request(captured.endpoint, {
						method: "POST",
						redirect: "manual",
						credentials: "omit",
						headers: {
							accept: "application/json",
							authorization: `Bearer ${credentialKey}`,
							"content-type": "application/json",
						},
						body,
						signal: deadline.signal,
					});
				} catch {
					return { status: "failed", failure: "authentication" };
				}
				let response: Response;
				try {
					response = await raceOperationWithAbort(() => captured.fetch(outbound), deadline.signal);
				} catch (error) {
					context.signal.throwIfAborted();
					deadline.signal.throwIfAborted();
					if (isArkCapacityError(error)) {
						return { status: "failed", failure: "capacity" };
					}
					throw new ArkImagesAdapterError("transport");
				}
				deadline.signal.throwIfAborted();
				if (!response.ok) {
					discardResponseBody(response);
					context.signal.throwIfAborted();
					deadline.signal.throwIfAborted();
					return { status: "failed", failure: failureForStatus(response.status) };
				}
				if (!isJsonContentType(response.headers.get("content-type"))) {
					discardResponseBody(response);
					context.signal.throwIfAborted();
					deadline.signal.throwIfAborted();
					return { status: "failed", failure: "malformed" };
				}
				let bytes: Uint8Array;
				try {
					bytes = new Uint8Array(await raceOperationWithAbort(() => response.arrayBuffer(), deadline.signal));
				} catch (error) {
					context.signal.throwIfAborted();
					deadline.signal.throwIfAborted();
					if (isArkCapacityError(error)) {
						return { status: "failed", failure: "capacity" };
					}
					throw new ArkImagesAdapterError("transport");
				}
				let value: unknown;
				try {
					const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
					value = parseStrictJsonText(text, captured.limits.maxResponseJsonDepth);
				} catch (error) {
					context.signal.throwIfAborted();
					deadline.signal.throwIfAborted();
					if (error instanceof StrictJsonError && error.code === "json_too_deep") {
						return { status: "failed", failure: "capacity" };
					}
					return { status: "failed", failure: "malformed" };
				}
				context.signal.throwIfAborted();
				deadline.signal.throwIfAborted();
				if (
					!isRecord(value) ||
					typeof value.model !== "string" ||
					value.model.length === 0 ||
					!Number.isSafeInteger(value.created) ||
					(value.created as number) < 0
				) {
					return { status: "failed", failure: "malformed" };
				}
				if (!Array.isArray(value.data)) {
					return value.data === null && isArkError(value.error)
						? { status: "failed", failure: "provider" }
						: { status: "failed", failure: "malformed" };
				}
				return await captureResult(
					value.data,
					captured.outputFormat,
					captured.limits,
					captured.urlOutputs,
					deadline.signal,
				);
			} finally {
				deadline.close();
			}
		},
	});
}

function isArkError(value: unknown): boolean {
	return isRecord(value) && typeof value.code === "string" && typeof value.message === "string";
}

function providerDeadline(caller: AbortSignal, timeoutMs: number): { readonly signal: AbortSignal; close(): void } {
	caller.throwIfAborted();
	const controller = new AbortController();
	const onAbort = () => controller.abort(caller.reason);
	caller.addEventListener("abort", onAbort, { once: true });
	if (caller.aborted) onAbort();
	const timeout = setTimeout(() => controller.abort(new ArkImagesAdapterError("request_timeout")), timeoutMs);
	timeout.unref();
	return Object.freeze({
		signal: controller.signal,
		close() {
			clearTimeout(timeout);
			caller.removeEventListener("abort", onAbort);
		},
	});
}

function failureForStatus(status: number): MediaFailureKind {
	if (status === 401) return "authentication";
	if (status === 403) return "permission";
	if (status === 413) return "capacity";
	return "provider";
}

function isJsonContentType(value: string | null): boolean {
	if (value === null) return false;
	const [rawMediaType, ...parameters] = value.split(";");
	const mediaType = rawMediaType?.trim().toLowerCase();
	if (mediaType !== "application/json" && !mediaType?.endsWith("+json")) return false;
	for (const parameter of parameters) {
		const match = /^\s*charset\s*=\s*"?([^";\s]+)"?\s*$/iu.exec(parameter);
		if (!match) continue;
		const charset = match[1]?.toLowerCase();
		if (charset !== "utf-8" && charset !== "utf8") return false;
	}
	return true;
}

async function captureResult(
	data: readonly unknown[],
	outputFormat: ArkImageOutputFormat,
	limits: Readonly<ArkImagesProviderLimits>,
	urlOutputs: CapturedArkImagesOptions["urlOutputs"],
	signal: AbortSignal,
): Promise<MediaProviderObservation> {
	if (data.length === 0) return { status: "failed", failure: "malformed" };
	if (data.length > limits.maxOutputs) return { status: "failed", failure: "capacity" };
	const resources: MediaProviderResource[] = [];
	const failures: { ordinal: number; failure: MediaFailureKind }[] = [];
	for (let ordinal = 0; ordinal < data.length; ordinal += 1) {
		const item = data[ordinal];
		if (!isRecord(item)) {
			failures.push({ ordinal, failure: "malformed" });
			continue;
		}
		if (item.error !== undefined) {
			const error = item.error;
			failures.push({ ordinal, failure: isArkError(error) ? "provider" : "malformed" });
			continue;
		}
		if (item.output_format !== undefined && item.output_format !== outputFormat) {
			failures.push({ ordinal, failure: "malformed" });
			continue;
		}
		if (item.b64_json !== undefined && item.url !== undefined) {
			failures.push({ ordinal, failure: "malformed" });
			continue;
		}
		if (typeof item.url === "string") {
			const retrieved = await retrieveUrlOutput(item.url, ordinal, outputFormat, limits, urlOutputs, signal);
			if ("failure" in retrieved) failures.push(retrieved);
			else resources.push(retrieved);
			continue;
		}
		const base64 = inspectCanonicalBase64(item.b64_json);
		if (!base64) {
			failures.push({ ordinal, failure: "malformed" });
			continue;
		}
		if (base64.byteLength > limits.maxOutputBytes) {
			failures.push({ ordinal, failure: "capacity" });
			continue;
		}
		resources.push({
			ordinal,
			kind: "image",
			mediaType: outputFormat === "png" ? "image/png" : "image/jpeg",
			declaredBytes: base64.byteLength,
			bytes: decodeCanonicalBase64(base64),
		});
	}
	if (resources.length === 0) return { status: "failed", failure: collapseFailures(failures) };
	if (failures.length > 0) return { status: "partially_succeeded", resources, failures };
	return { status: "succeeded", resources };
}

async function retrieveUrlOutput(
	value: string,
	ordinal: number,
	outputFormat: ArkImageOutputFormat,
	limits: Readonly<ArkImagesProviderLimits>,
	policy: CapturedArkImagesOptions["urlOutputs"],
	signal: AbortSignal,
): Promise<MediaProviderResource | { readonly ordinal: number; readonly failure: MediaFailureKind }> {
	if (Buffer.byteLength(value, "utf8") > limits.maxOutputUrlBytes) return { ordinal, failure: "capacity" };
	let url: URL;
	try {
		url = new URL(value);
	} catch {
		return { ordinal, failure: "malformed" };
	}
	if (url.protocol !== "https:" || url.username !== "" || url.password !== "" || url.hash !== "") {
		return { ordinal, failure: "malformed" };
	}
	const canonicalUrl = url.toString();
	if (Buffer.byteLength(canonicalUrl, "utf8") > limits.maxOutputUrlBytes) {
		return { ordinal, failure: "capacity" };
	}
	if (!policy) return { ordinal, failure: "resource" };
	let accepted: boolean;
	try {
		accepted = await raceOperationWithAbort(
			() => Promise.resolve().then(() => policy.accept(canonicalUrl, signal)),
			signal,
		);
	} catch {
		signal.throwIfAborted();
		return { ordinal, failure: "resource" };
	}
	if (accepted !== true) return { ordinal, failure: "resource" };
	const mediaType = outputFormat === "png" ? "image/png" : "image/jpeg";
	const request = new Request(url, {
		method: "GET",
		redirect: "manual",
		credentials: "omit",
		headers: { accept: mediaType },
		signal,
	});
	let response: Response;
	try {
		response = await raceOperationWithAbort(() => policy.fetch(request), signal);
	} catch (error) {
		signal.throwIfAborted();
		return {
			ordinal,
			failure: isArkCapacityError(error) ? "capacity" : "resource",
		};
	}
	if (!response.ok) {
		discardResponseBody(response);
		signal.throwIfAborted();
		return { ordinal, failure: response.status === 413 ? "capacity" : "resource" };
	}
	if (normalizedMediaType(response.headers.get("content-type")) !== mediaType) {
		discardResponseBody(response);
		signal.throwIfAborted();
		return { ordinal, failure: "resource" };
	}
	try {
		const bytes = new Uint8Array(await raceOperationWithAbort(() => response.arrayBuffer(), signal));
		if (bytes.byteLength === 0) return { ordinal, failure: "resource" };
		return { ordinal, kind: "image", mediaType, declaredBytes: bytes.byteLength, bytes };
	} catch (error) {
		signal.throwIfAborted();
		return {
			ordinal,
			failure: isArkCapacityError(error) ? "capacity" : "resource",
		};
	}
}

function normalizedMediaType(value: string | null): string | undefined {
	return value?.split(";", 1)[0]?.trim().toLowerCase() || undefined;
}

function discardResponseBody(response: Readonly<Response>): void {
	try {
		const cancellation = response.body?.cancel();
		if (cancellation) void cancellation.catch(() => undefined);
	} catch {
		// Cleanup is best-effort and cannot replace or delay the attempt outcome.
	}
}

function collapseFailures(failures: readonly { readonly failure: MediaFailureKind }[]): MediaFailureKind {
	for (const kind of ["capacity", "resource", "malformed", "provider"] as const) {
		if (failures.some((failure) => failure.failure === kind)) return kind;
	}
	return "unknown";
}

function isArkCapacityError(error: unknown): boolean {
	return error instanceof ArkImagesAdapterError && error.code === "capacity";
}

function captureOptions(options: Readonly<CreateArkImagesMediaProviderOptions>): CapturedArkImagesOptions {
	if (!options || typeof options !== "object") throw new ArkImagesAdapterError("invalid_configuration");
	let credentials: CredentialStore;
	let credentialReference: unknown;
	let endpointValue: unknown;
	let modelId: unknown;
	let outputFormat: unknown;
	let size: unknown;
	let watermark: unknown;
	let limitsSource: ArkImagesProviderLimits;
	let fetchImplementation: typeof fetch | undefined;
	let urlOutputsSource: ArkImageUrlOutputPolicy | undefined;
	try {
		credentials = options.credentials;
		credentialReference = options.credentialReference;
		endpointValue = options.endpoint;
		modelId = options.modelId;
		outputFormat = options.outputFormat;
		size = options.size;
		watermark = options.watermark;
		limitsSource = options.limits;
		fetchImplementation = options.fetch;
		urlOutputsSource = options.urlOutputs;
	} catch {
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	if (typeof credentials?.read !== "function" || !limitsSource || typeof limitsSource !== "object") {
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	const limits = {} as Record<keyof ArkImagesProviderLimits, number>;
	try {
		for (const name of LIMIT_NAMES) {
			const value = limitsSource[name];
			if (!Number.isSafeInteger(value) || value <= 0) throw new ArkImagesAdapterError("invalid_configuration");
			limits[name] = value;
		}
	} catch (error) {
		if (error instanceof ArkImagesAdapterError) throw error;
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	if (limits.requestTimeoutMs > NODE_MAX_TIMER_DELAY_MS) {
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	const capturedLimits = Object.freeze(limits as unknown as ArkImagesProviderLimits);
	const credential = captureBoundedString(credentialReference, capturedLimits.maxCredentialReferenceBytes);
	const model = captureBoundedString(modelId, capturedLimits.maxModelBytes);
	const capturedSize = captureBoundedString(size, capturedLimits.maxSizeBytes);
	if (outputFormat !== "jpeg" && outputFormat !== "png") {
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	if (typeof watermark !== "boolean") throw new ArkImagesAdapterError("invalid_configuration");
	if (typeof endpointValue !== "string" || Buffer.byteLength(endpointValue, "utf8") > capturedLimits.maxEndpointBytes) {
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	let endpoint: URL;
	try {
		endpoint = new URL(endpointValue);
	} catch {
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	if (
		endpoint.protocol !== "https:" ||
		endpoint.username !== "" ||
		endpoint.password !== "" ||
		endpoint.search !== "" ||
		endpoint.hash !== ""
	) {
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	const canonicalEndpoint = endpoint.toString();
	if (Buffer.byteLength(canonicalEndpoint, "utf8") > capturedLimits.maxEndpointBytes) {
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	const upstream = fetchImplementation ?? globalThis.fetch;
	if (typeof upstream !== "function") throw new ArkImagesAdapterError("invalid_configuration");
	let urlOutputs: CapturedArkImagesOptions["urlOutputs"];
	if (urlOutputsSource !== undefined) {
		if (!urlOutputsSource || typeof urlOutputsSource !== "object") {
			throw new ArkImagesAdapterError("invalid_configuration");
		}
		let accept: ArkImageUrlOutputPolicy["accept"];
		let outputFetch: typeof fetch | undefined;
		try {
			accept = urlOutputsSource.accept;
			outputFetch = urlOutputsSource.fetch;
		} catch {
			throw new ArkImagesAdapterError("invalid_configuration");
		}
		if (typeof accept !== "function" || (outputFetch !== undefined && typeof outputFetch !== "function")) {
			throw new ArkImagesAdapterError("invalid_configuration");
		}
		urlOutputs = Object.freeze({
			accept,
			fetch: createArkBoundedFetch(outputFetch ?? upstream, capturedLimits.maxOutputBytes),
		});
	}
	return Object.freeze({
		credentials,
		credentialReference: credential,
		endpoint: canonicalEndpoint,
		modelId: model,
		outputFormat,
		size: capturedSize,
		watermark,
		limits: capturedLimits,
		fetch: createArkBoundedFetch(upstream, capturedLimits.maxResponseBytes),
		...(urlOutputs ? { urlOutputs } : {}),
	});
}

function createArkBoundedFetch(upstream: typeof fetch, maxBytes: number): typeof fetch {
	return createBoundedFetch(
		upstream,
		maxBytes,
		maxBytes,
		() => new ArkImagesAdapterError("capacity"),
		undefined,
		undefined,
		"total-body",
	);
}

function captureBoundedString(value: unknown, maxBytes: number): string {
	if (typeof value !== "string" || value.length === 0 || Buffer.byteLength(value, "utf8") > maxBytes) {
		throw new ArkImagesAdapterError("invalid_configuration");
	}
	return value;
}

interface CapturedInput {
	readonly prompt: string;
	readonly references: readonly string[];
}

type CapturedInputResult = CapturedInput | { readonly failure: "capacity" | "malformed" };

function captureInput(
	request: Readonly<MediaGenerationRequest>,
	limits: Readonly<ArkImagesProviderLimits>,
): CapturedInputResult {
	if (request.kind !== "image" || !isRecord(request.input) || !Array.isArray(request.input.input)) {
		return { failure: "malformed" };
	}
	let prompt: string | undefined;
	let referenceBytes = 0;
	const references: string[] = [];
	for (const part of request.input.input) {
		if (!isRecord(part)) return { failure: "malformed" };
		if (part.type === "text") {
			if (prompt !== undefined || typeof part.text !== "string" || part.text.length === 0) {
				return { failure: "malformed" };
			}
			if (Buffer.byteLength(part.text, "utf8") > limits.maxPromptBytes) return { failure: "capacity" };
			prompt = part.text;
			continue;
		}
		if (
			part.type !== "image" ||
			(part.mediaType !== "image/png" && part.mediaType !== "image/jpeg") ||
			typeof part.data !== "string"
		) {
			return { failure: "malformed" };
		}
		if (references.length >= limits.maxReferenceImages) return { failure: "capacity" };
		const base64 = inspectCanonicalBase64(part.data);
		if (!base64) return { failure: "malformed" };
		if (base64.byteLength > limits.maxReferenceBytes) return { failure: "capacity" };
		if (base64.byteLength > limits.maxReferenceTotalBytes - referenceBytes) return { failure: "capacity" };
		referenceBytes += base64.byteLength;
		references.push(`data:${part.mediaType};base64,${base64.data}`);
	}
	if (prompt === undefined) return { failure: "malformed" };
	return Object.freeze({ prompt, references: Object.freeze(references) });
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

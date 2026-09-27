import { BoundedFetchRedirectError, BoundedFetchResponseLimitError, createBoundedFetch } from "@opengameagent/client";
import type { RuntimeDependencyDefinition, RuntimeDependencyInspection } from "@opengameagent/health";
import {
	assertOpaqueIdentifier,
	type RuntimeDependencyKind,
	type RuntimeDependencyRequirement,
} from "@opengameagent/protocol";
import { Ollama } from "ollama";

type EndpointHeaders = ConstructorParameters<typeof Headers>[0];

interface ProviderEndpointHealthBaseOptions {
	readonly id: string;
	readonly kind: Extract<RuntimeDependencyKind, "provider" | "local-endpoint">;
	readonly requirement: RuntimeDependencyRequirement;
	readonly maxResponseBytes: number;
	readonly fetch?: typeof fetch;
	readonly headers?: EndpointHeaders;
}

export type ProviderEndpointHealthOptions =
	| (ProviderEndpointHealthBaseOptions & {
			readonly protocol: "openai-compatible";
			readonly baseUrl: string;
			readonly modelId: string;
			readonly apiKey?: string;
	  })
	| (ProviderEndpointHealthBaseOptions & {
			readonly protocol: "ollama";
			readonly baseUrl: string;
			readonly modelId: string;
	  })
	| (ProviderEndpointHealthBaseOptions & {
			readonly protocol: "llama.cpp";
			readonly healthUrl: string;
	  });

interface CapturedEndpointBase {
	readonly id: string;
	readonly kind: Extract<RuntimeDependencyKind, "provider" | "local-endpoint">;
	readonly requirement: RuntimeDependencyRequirement;
	readonly maxResponseBytes: number;
	readonly fetch: typeof fetch;
	readonly headers: Headers;
}

type CapturedEndpoint =
	| (CapturedEndpointBase & {
			readonly protocol: "openai-compatible";
			readonly baseUrl: string;
			readonly modelId: string;
			readonly apiKey?: string;
	  })
	| (CapturedEndpointBase & {
			readonly protocol: "ollama";
			readonly baseUrl: string;
			readonly modelId: string;
	  })
	| (CapturedEndpointBase & {
			readonly protocol: "llama.cpp";
			readonly healthUrl: string;
	  });

class EndpointHttpStatusError extends Error {
	constructor(readonly status: number) {
		super("The endpoint returned an unsuccessful HTTP status.");
		this.name = "EndpointHttpStatusError";
	}
}

class EndpointTransportError extends Error {
	constructor() {
		super("The endpoint request failed before a response was received.");
		this.name = "EndpointTransportError";
	}
}

class EndpointPayloadError extends Error {
	constructor() {
		super("The endpoint response could not be read as its documented payload.");
		this.name = "EndpointPayloadError";
	}
}

export function createProviderEndpointHealthDependency(
	options: ProviderEndpointHealthOptions,
): RuntimeDependencyDefinition {
	const captured = captureEndpoint(options);
	return Object.freeze({
		id: captured.id,
		kind: captured.kind,
		requirement: captured.requirement,
		inspect: (signal: AbortSignal) => inspectEndpoint(captured, signal),
	});
}

async function inspectEndpoint(endpoint: CapturedEndpoint, signal: AbortSignal): Promise<RuntimeDependencyInspection> {
	signal.throwIfAborted();
	try {
		switch (endpoint.protocol) {
			case "openai-compatible":
				return await inspectOpenAICompatible(endpoint, signal);
			case "ollama":
				return await inspectOllama(endpoint, signal);
			case "llama.cpp":
				return await inspectLlamaCpp(endpoint, signal);
		}
	} catch (error) {
		signal.throwIfAborted();
		return mapEndpointFailure(error);
	}
}

async function inspectOpenAICompatible(
	endpoint: Extract<CapturedEndpoint, { readonly protocol: "openai-compatible" }>,
	signal: AbortSignal,
): Promise<RuntimeDependencyInspection> {
	const headers = new Headers(endpoint.headers);
	if (endpoint.apiKey !== undefined && !headers.has("authorization")) {
		headers.set("authorization", `Bearer ${endpoint.apiKey}`);
	}
	const response = await endpointFetch(
		endpoint,
		signal,
	)(new Request(`${endpoint.baseUrl}/models`, { method: "GET", headers, signal }));
	if (!response.ok) await rejectHttpResponse(response);
	const page = await readJson(response);
	const data = page && typeof page === "object" ? (page as { readonly data?: unknown }).data : undefined;
	if (
		!Array.isArray(data) ||
		data.some((model) => !model || typeof model !== "object" || typeof model.id !== "string")
	) {
		return { state: "unavailable", reason: "dependency-failure" };
	}
	return data.some((model) => model.id === endpoint.modelId)
		? { state: "available" }
		: { state: "unavailable", reason: "capability-missing" };
}

async function inspectOllama(
	endpoint: Extract<CapturedEndpoint, { readonly protocol: "ollama" }>,
	signal: AbortSignal,
): Promise<RuntimeDependencyInspection> {
	const client = new Ollama({
		host: endpoint.baseUrl,
		headers: endpoint.headers,
		fetch: endpointFetch(endpoint, signal),
	});
	const installed = await readOllamaPayload(() => client.list(), signal);
	const installedIds = readOllamaModelIds(installed);
	if (installedIds === undefined) return { state: "unavailable", reason: "dependency-failure" };
	if (!installedIds.includes(endpoint.modelId)) return { state: "unavailable", reason: "capability-missing" };

	const running = await readOllamaPayload(() => client.ps(), signal);
	const runningIds = readOllamaModelIds(running);
	if (runningIds === undefined) return { state: "unavailable", reason: "dependency-failure" };
	return runningIds.includes(endpoint.modelId) ? { state: "ready" } : { state: "available" };
}

async function inspectLlamaCpp(
	endpoint: Extract<CapturedEndpoint, { readonly protocol: "llama.cpp" }>,
	signal: AbortSignal,
): Promise<RuntimeDependencyInspection> {
	const response = await endpointFetch(
		endpoint,
		signal,
		503,
	)(new Request(endpoint.healthUrl, { method: "GET", headers: endpoint.headers, signal }));
	if (response.status === 503) {
		const body = await readJson(response);
		return isLlamaUnavailable(body) ? { state: "available" } : { state: "unavailable", reason: "dependency-failure" };
	}
	if (!response.ok) await rejectHttpResponse(response);
	const body = await readJson(response);
	return isLlamaReady(body) ? { state: "ready" } : { state: "unavailable", reason: "dependency-failure" };
}

function endpointFetch(
	endpoint: CapturedEndpointBase,
	signal: AbortSignal,
	inspectableErrorStatus?: number,
): typeof fetch {
	const fetchOnce: typeof fetch = async (input, init) => {
		const request = input instanceof Request && init === undefined ? input : new Request(input, init);
		let response: Response;
		try {
			response = await endpoint.fetch(request);
		} catch {
			signal.throwIfAborted();
			throw new EndpointTransportError();
		}
		signal.throwIfAborted();
		const hasRedirectEvidence =
			response.redirected ||
			response.type === "opaqueredirect" ||
			(response.url !== "" && response.url !== request.url);
		if (!hasRedirectEvidence && response.status >= 400 && !response.ok && response.status !== inspectableErrorStatus) {
			await rejectHttpResponse(response);
		}
		return response;
	};
	const bounded = createBoundedFetch(fetchOnce, endpoint.maxResponseBytes, endpoint.maxResponseBytes);
	return async (input, init) => {
		signal.throwIfAborted();
		const request = new Request(input, { ...init, signal });
		const response = await bounded(request);
		signal.throwIfAborted();
		if (response.headers.get("content-type")?.toLowerCase().includes("text/event-stream")) {
			await cancelResponseBody(response);
			throw new EndpointPayloadError();
		}
		return response;
	};
}

async function rejectHttpResponse(response: Response): Promise<never> {
	await cancelResponseBody(response);
	throw new EndpointHttpStatusError(response.status);
}

async function cancelResponseBody(response: Response): Promise<void> {
	if (response.body) await response.body.cancel().catch(() => undefined);
}

function readOllamaModelIds(value: unknown): readonly string[] | undefined {
	if (!value || typeof value !== "object") return undefined;
	const models = (value as { readonly models?: unknown }).models;
	if (!Array.isArray(models)) return undefined;
	const ids: string[] = [];
	for (const model of models) {
		if (!model || typeof model !== "object" || typeof (model as { readonly model?: unknown }).model !== "string") {
			return undefined;
		}
		ids.push((model as { readonly model: string }).model);
	}
	return ids;
}

async function readJson(response: Response): Promise<unknown> {
	try {
		return await response.json();
	} catch (error) {
		if (error instanceof BoundedFetchResponseLimitError) throw error;
		throw new EndpointPayloadError();
	}
}

async function readOllamaPayload<T>(operation: () => Promise<T>, signal: AbortSignal): Promise<T> {
	try {
		return await operation();
	} catch (error) {
		signal.throwIfAborted();
		if (
			error instanceof EndpointHttpStatusError ||
			error instanceof EndpointTransportError ||
			error instanceof BoundedFetchResponseLimitError ||
			error instanceof BoundedFetchRedirectError
		) {
			throw error;
		}
		throw new EndpointPayloadError();
	}
}

function isLlamaReady(value: unknown): boolean {
	return Boolean(value && typeof value === "object" && (value as { readonly status?: unknown }).status === "ok");
}

function isLlamaUnavailable(value: unknown): boolean {
	if (!value || typeof value !== "object") return false;
	const error = (value as { readonly error?: unknown }).error;
	return Boolean(
		error &&
			typeof error === "object" &&
			(error as { readonly code?: unknown }).code === 503 &&
			(error as { readonly type?: unknown }).type === "unavailable_error" &&
			(error as { readonly message?: unknown }).message === "Loading model",
	);
}

function mapEndpointFailure(error: unknown): RuntimeDependencyInspection {
	if (error instanceof EndpointHttpStatusError) return mapHttpStatus(error.status);
	if (error instanceof BoundedFetchResponseLimitError) return { state: "unavailable", reason: "capacity" };
	if (error instanceof BoundedFetchRedirectError) return { state: "unavailable", reason: "misconfigured" };
	if (error instanceof EndpointPayloadError) return { state: "unavailable", reason: "dependency-failure" };
	if (error instanceof EndpointTransportError) return { state: "unavailable", reason: "transport" };
	return { state: "unavailable", reason: "unknown" };
}

function mapHttpStatus(status: number): RuntimeDependencyInspection {
	switch (status) {
		case 400:
		case 422:
			return { state: "unavailable", reason: "misconfigured" };
		case 401:
			return { state: "unavailable", reason: "authentication-required" };
		case 403:
			return { state: "unavailable", reason: "permission-denied" };
		case 404:
			return { state: "unavailable", reason: "capability-missing" };
		case 408:
			return { state: "unavailable", reason: "timeout" };
		case 429:
			return { state: "unavailable", reason: "capacity" };
		default:
			return status >= 500
				? { state: "unavailable", reason: "dependency-failure" }
				: { state: "unavailable", reason: "unknown" };
	}
}

function captureEndpoint(options: ProviderEndpointHealthOptions): CapturedEndpoint {
	if (!options || typeof options !== "object") throw new TypeError("Provider endpoint health options are required.");
	assertOpaqueIdentifier(options.id);
	if (options.kind !== "provider" && options.kind !== "local-endpoint") {
		throw new TypeError("Provider endpoint health kind is invalid.");
	}
	if (options.requirement !== "required" && options.requirement !== "optional") {
		throw new TypeError("Provider endpoint health requirement is invalid.");
	}
	if (!Number.isSafeInteger(options.maxResponseBytes) || options.maxResponseBytes <= 0) {
		throw new TypeError("maxResponseBytes must be a positive safe integer.");
	}
	const endpointFetch = options.fetch ?? globalThis.fetch;
	if (typeof endpointFetch !== "function") throw new TypeError("A Fetch API implementation is required.");
	const base = {
		id: options.id,
		kind: options.kind,
		requirement: options.requirement,
		maxResponseBytes: options.maxResponseBytes,
		fetch: endpointFetch,
		headers: new Headers(options.headers),
	} as const;
	switch (options.protocol) {
		case "openai-compatible": {
			assertModelId(options.modelId);
			const apiKey = options.apiKey;
			if (apiKey !== undefined && (typeof apiKey !== "string" || apiKey.length === 0)) {
				throw new TypeError("apiKey must be a non-empty string when supplied.");
			}
			return Object.freeze({
				...base,
				protocol: options.protocol,
				baseUrl: captureBaseUrl(options.baseUrl),
				modelId: options.modelId,
				...(apiKey === undefined ? {} : { apiKey }),
			});
		}
		case "ollama":
			assertModelId(options.modelId);
			return Object.freeze({
				...base,
				protocol: options.protocol,
				baseUrl: captureBaseUrl(options.baseUrl),
				modelId: options.modelId,
			});
		case "llama.cpp":
			return Object.freeze({
				...base,
				protocol: options.protocol,
				healthUrl: captureHealthUrl(options.healthUrl),
			});
	}
}

function assertModelId(value: string): void {
	if (typeof value !== "string") throw new TypeError("modelId must be a string.");
	assertOpaqueIdentifier(value);
}

function captureBaseUrl(value: string): string {
	return captureServiceUrl(value, false).replace(/\/$/u, "");
}

function captureHealthUrl(value: string): string {
	return captureServiceUrl(value, true);
}

function captureServiceUrl(value: string, allowQuery: boolean): string {
	if (
		typeof value !== "string" ||
		value.trim() !== value ||
		value.includes("\\") ||
		value.includes("#") ||
		(!allowQuery && value.includes("?"))
	) {
		throw new TypeError("Endpoint must use HTTPS or exact loopback HTTP.");
	}
	let url: URL;
	try {
		url = new URL(value);
	} catch {
		throw new TypeError("Endpoint must use HTTPS or exact loopback HTTP.");
	}
	if (
		(url.protocol !== "https:" && !(url.protocol === "http:" && isLoopbackHost(url.hostname))) ||
		url.username !== "" ||
		url.password !== "" ||
		url.hash !== "" ||
		(!allowQuery && url.search !== "")
	) {
		throw new TypeError("Endpoint must use HTTPS or exact loopback HTTP.");
	}
	return url.toString();
}

function isLoopbackHost(hostname: string): boolean {
	if (hostname.toLowerCase() === "localhost" || hostname === "[::1]") return true;
	const parts = hostname.split(".");
	return (
		parts.length === 4 &&
		parts[0] === "127" &&
		parts.every((part) => /^(?:0|[1-9][0-9]{0,2})$/u.test(part) && Number(part) <= 255)
	);
}

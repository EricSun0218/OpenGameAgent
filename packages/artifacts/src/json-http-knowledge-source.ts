import { isIP } from "node:net";
import {
	canonicalizeJsonValue,
	canonicalizeJsonValueWithin,
	type JsonValue,
	JsonValueLimitError,
	normalizeJsonValueWithin,
	parseStrictJsonText,
} from "@opengameagent/protocol";
import { waitForAbort } from "./abort.js";
import { byteLength } from "./artifact-validation.js";
import { KnowledgeHttpError } from "./knowledge-errors.js";
import type {
	JsonHttpKnowledgeLimits,
	JsonHttpKnowledgeSourceOptions,
	KnowledgeSource,
	KnowledgeSourceRequest,
} from "./knowledge-types.js";

const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;
const forbiddenHeaders = new Set([
	"accept",
	"connection",
	"content-length",
	"content-type",
	"host",
	"keep-alive",
	"proxy-authenticate",
	"proxy-authorization",
	"te",
	"trailer",
	"transfer-encoding",
	"upgrade",
]);
const limitNames = [
	"maxEndpointBytes",
	"maxRequestBytes",
	"maxResponseBytes",
	"maxJsonDepth",
	"maxHeaders",
	"maxHeaderNameBytes",
	"maxHeaderValueBytes",
	"requestTimeoutMs",
] as const satisfies readonly (keyof JsonHttpKnowledgeLimits)[];

export class JsonHttpKnowledgeSource implements KnowledgeSource {
	readonly sourceId: string;
	readonly description: string;
	readonly limits: Readonly<JsonHttpKnowledgeLimits>;
	private readonly endpoint: URL;
	private readonly fetchImplementation: typeof globalThis.fetch;
	private readonly headersProvider: JsonHttpKnowledgeSourceOptions["headers"];
	private readonly projectContext: JsonHttpKnowledgeSourceOptions["projectContext"];

	constructor(options: JsonHttpKnowledgeSourceOptions) {
		this.limits = freezeHttpLimits(options.limits);
		this.sourceId = options.sourceId;
		this.description = options.description;
		this.endpoint = validateEndpoint(options.endpoint, this.limits);
		this.fetchImplementation = options.fetch ?? globalThis.fetch;
		if (typeof this.fetchImplementation !== "function") throw new KnowledgeHttpError("transport_failed");
		this.headersProvider = options.headers;
		this.projectContext = options.projectContext;
	}

	async query(request: Readonly<KnowledgeSourceRequest>, signal: AbortSignal): Promise<unknown> {
		if (signal.aborted) throw signal.reason ?? new KnowledgeHttpError("request_aborted");
		const deadline = createDeadline(signal, this.limits.requestTimeoutMs);
		try {
			const extraHeaders = this.headersProvider
				? await waitForAbort(
						Promise.resolve().then(() => this.headersProvider?.(deadline.signal)),
						deadline.signal,
					)
				: undefined;
			const context = this.projectContext
				? normalizeJsonValueWithin(
						await waitForAbort(
							Promise.resolve().then(() => this.projectContext?.(request.input, deadline.signal)),
							deadline.signal,
						),
						{ maxBytes: this.limits.maxRequestBytes, maxDepth: this.limits.maxJsonDepth },
					)
				: undefined;
			let body: string;
			try {
				body = canonicalizeJsonValueWithin(
					{
						query: request.query,
						maxItems: request.maxItems,
						...(context === undefined ? {} : { context }),
					},
					{ maxBytes: this.limits.maxRequestBytes, maxDepth: this.limits.maxJsonDepth },
				);
			} catch (error) {
				if (error instanceof JsonValueLimitError) {
					throw new KnowledgeHttpError("request_too_large", { cause: error });
				}
				throw error;
			}
			const headers = normalizeHeaders(extraHeaders, this.limits);
			let response: Response;
			try {
				response = await waitForAbort(
					this.fetchImplementation(this.endpoint, {
						method: "POST",
						redirect: "manual",
						credentials: "omit",
						headers,
						body,
						signal: deadline.signal,
					}),
					deadline.signal,
				);
			} catch (error) {
				throw translateAbortOrTransport(error, signal, deadline);
			}
			if (
				response.redirected ||
				(response.url !== "" && response.url !== this.endpoint.toString()) ||
				isRedirect(response.status)
			) {
				await cancelBody(response);
				throw new KnowledgeHttpError("redirect_blocked");
			}
			if (!response.ok) {
				await cancelBody(response);
				throw new KnowledgeHttpError("http_status");
			}
			if (!isJsonContentType(response.headers.get("content-type"))) {
				await cancelBody(response);
				throw new KnowledgeHttpError("unsupported_content_type");
			}
			assertContentLength(response.headers.get("content-length"), this.limits.maxResponseBytes);
			let bytes: Uint8Array;
			try {
				bytes = await readBoundedBody(response, this.limits.maxResponseBytes, deadline.signal);
			} catch (error) {
				if (error instanceof KnowledgeHttpError) throw error;
				throw translateAbortOrTransport(error, signal, deadline);
			}
			let text: string;
			try {
				text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
			} catch (error) {
				throw new KnowledgeHttpError("invalid_response", { cause: error });
			}
			let parsed: JsonValue;
			try {
				parsed = parseStrictJsonText(text, this.limits.maxJsonDepth);
			} catch (error) {
				throw new KnowledgeHttpError("invalid_response", { cause: error });
			}
			if (!isRecord(parsed) || !Array.isArray(parsed.items)) {
				throw new KnowledgeHttpError("invalid_response");
			}
			return parsed.items;
		} catch (error) {
			if (signal.aborted) throw signal.reason ?? error;
			if (error instanceof KnowledgeHttpError) throw error;
			if (error instanceof JsonValueLimitError) {
				throw new KnowledgeHttpError("request_too_large", { cause: error });
			}
			if (deadline.expired()) throw new KnowledgeHttpError("request_timeout", { cause: error });
			throw new KnowledgeHttpError("transport_failed", { cause: error });
		} finally {
			deadline.dispose();
		}
	}
}

function freezeHttpLimits(limits: JsonHttpKnowledgeLimits): Readonly<JsonHttpKnowledgeLimits> {
	const copy = {} as Record<keyof JsonHttpKnowledgeLimits, number>;
	for (const name of limitNames) {
		const value = limits[name];
		if (!Number.isSafeInteger(value) || value <= 0) {
			throw new KnowledgeHttpError("invalid_limits");
		}
		copy[name] = value;
	}
	if (copy.requestTimeoutMs > NODE_MAX_TIMER_DELAY_MS) throw new KnowledgeHttpError("invalid_limits");
	if (copy.maxHeaders < 2 || copy.maxHeaderNameBytes < byteLength("content-type")) {
		throw new KnowledgeHttpError("invalid_headers");
	}
	if (copy.maxHeaderValueBytes < byteLength("application/json")) throw new KnowledgeHttpError("invalid_headers");
	if (copy.maxRequestBytes < byteLength(canonicalizeJsonValue({ query: null, maxItems: 1 }))) {
		throw new KnowledgeHttpError("request_too_large");
	}
	if (copy.maxResponseBytes < byteLength(canonicalizeJsonValue({ items: [] }))) {
		throw new KnowledgeHttpError("response_too_large");
	}
	return Object.freeze(copy);
}

function validateEndpoint(value: unknown, limits: Readonly<JsonHttpKnowledgeLimits>): URL {
	if (typeof value !== "string" || byteLength(value) > limits.maxEndpointBytes) {
		throw new KnowledgeHttpError("invalid_endpoint");
	}
	let endpoint: URL;
	try {
		endpoint = new URL(value);
	} catch (error) {
		throw new KnowledgeHttpError("invalid_endpoint", { cause: error });
	}
	if (endpoint.username !== "" || endpoint.password !== "" || endpoint.hash !== "") {
		throw new KnowledgeHttpError("invalid_endpoint");
	}
	if (endpoint.protocol !== "https:" && !(endpoint.protocol === "http:" && isLoopback(endpoint.hostname))) {
		throw new KnowledgeHttpError("invalid_endpoint");
	}
	return endpoint;
}

function isLoopback(hostname: string): boolean {
	const value = hostname.startsWith("[") && hostname.endsWith("]") ? hostname.slice(1, -1) : hostname;
	if (value.toLowerCase() === "localhost" || value === "::1") return true;
	return isIP(value) === 4 && value.startsWith("127.");
}

function normalizeHeaders(
	value: Readonly<Record<string, string>> | undefined,
	limits: Readonly<JsonHttpKnowledgeLimits>,
): Headers {
	const headers = new Headers({ accept: "application/json", "content-type": "application/json" });
	if (value === undefined) return headers;
	if (typeof value !== "object" || value === null || Array.isArray(value))
		throw new KnowledgeHttpError("invalid_headers");
	const prototype = Object.getPrototypeOf(value);
	if (prototype !== Object.prototype && prototype !== null) throw new KnowledgeHttpError("invalid_headers");
	const names = Object.keys(value);
	if (names.length + 2 > limits.maxHeaders) throw new KnowledgeHttpError("invalid_headers");
	const seen = new Set<string>();
	for (const rawName of names) {
		const descriptor = Object.getOwnPropertyDescriptor(value, rawName);
		if (!descriptor || !("value" in descriptor) || typeof descriptor.value !== "string") {
			throw new KnowledgeHttpError("invalid_headers");
		}
		const name = rawName.toLowerCase();
		if (
			seen.has(name) ||
			forbiddenHeaders.has(name) ||
			name.startsWith("proxy-") ||
			name.startsWith("sec-") ||
			byteLength(name) > limits.maxHeaderNameBytes ||
			byteLength(descriptor.value) > limits.maxHeaderValueBytes
		) {
			throw new KnowledgeHttpError("invalid_headers");
		}
		seen.add(name);
		try {
			headers.set(name, descriptor.value);
		} catch (error) {
			throw new KnowledgeHttpError("invalid_headers", { cause: error });
		}
	}
	return headers;
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

function assertContentLength(value: string | null, maximumBytes: number): void {
	if (value === null) return;
	if (!/^(0|[1-9][0-9]*)$/u.test(value)) throw new KnowledgeHttpError("invalid_response");
	if (BigInt(value) > BigInt(maximumBytes)) throw new KnowledgeHttpError("response_too_large");
}

async function readBoundedBody(response: Response, maximumBytes: number, signal: AbortSignal): Promise<Uint8Array> {
	if (response.body === null) throw new KnowledgeHttpError("invalid_response");
	const reader = response.body.getReader();
	const chunks: Uint8Array[] = [];
	let total = 0;
	try {
		for (;;) {
			signal.throwIfAborted();
			const { done, value } = await waitForAbort(reader.read(), signal);
			if (done) break;
			if (value.byteLength === 0) continue;
			if (value.byteLength > maximumBytes - total) {
				throw new KnowledgeHttpError("response_too_large");
			}
			total += value.byteLength;
			chunks.push(value);
		}
	} finally {
		await reader.cancel().catch(() => undefined);
	}
	const bytes = new Uint8Array(total);
	let offset = 0;
	for (const chunk of chunks) {
		bytes.set(chunk, offset);
		offset += chunk.byteLength;
	}
	return bytes;
}

function translateAbortOrTransport(
	error: unknown,
	parent: AbortSignal,
	deadline: ReturnType<typeof createDeadline>,
): unknown {
	if (parent.aborted) return parent.reason ?? new KnowledgeHttpError("request_aborted", { cause: error });
	if (deadline.expired()) return new KnowledgeHttpError("request_timeout", { cause: error });
	return new KnowledgeHttpError("transport_failed", { cause: error });
}

function createDeadline(
	parent: AbortSignal,
	delayMs: number,
): {
	readonly signal: AbortSignal;
	readonly expired: () => boolean;
	readonly dispose: () => void;
} {
	const controller = new AbortController();
	let expired = false;
	const timer = setTimeout(() => {
		expired = true;
		controller.abort(new KnowledgeHttpError("request_timeout"));
	}, delayMs);
	timer.unref();
	return {
		signal: AbortSignal.any([parent, controller.signal]),
		expired: () => expired,
		dispose: () => clearTimeout(timer),
	};
}

async function cancelBody(response: Response): Promise<void> {
	await response.body?.cancel().catch(() => undefined);
}

function isRedirect(status: number): boolean {
	return status === 301 || status === 302 || status === 303 || status === 307 || status === 308;
}

function isRecord(value: JsonValue): value is { [key: string]: JsonValue } {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

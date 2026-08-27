import type { JsonObject, JsonValue } from "@opengameagent/protocol";
import type {
	GameMediaBinary,
	GameMediaGenerationRequest,
	GameMediaGenerationResult,
	GameMediaGenerator,
	GameMediaKind,
	GameMediaProviderAuthentication,
} from "./media.js";
import { mediaByteLength, validateMediaIdentifier } from "./media.js";

export interface GameHttpImageGeneratorOptions {
	provider: string;
	model: string;
	endpoint: string;
	authentication?: GameMediaProviderAuthentication;
	fetch?: typeof globalThis.fetch;
	maximumPromptBytes?: number;
	maximumReferences?: number;
	maximumReferenceBytes?: number;
	maximumAggregateReferenceBytes?: number;
	maximumResponseBytes?: number;
	maximumOutputBytes?: number;
	maximumOutputs?: number;
}

interface CheckedHttpImageOptions {
	provider: string;
	model: string;
	endpoint: URL;
	authentication?: GameMediaProviderAuthentication;
	fetch: typeof globalThis.fetch;
	maximumPromptBytes: number;
	maximumReferences: number;
	maximumReferenceBytes: number;
	maximumAggregateReferenceBytes: number;
	maximumResponseBytes: number;
	maximumOutputBytes: number;
	maximumOutputs: number;
}

function positive(value: number | undefined, fallback: number, name: string, maximum: number): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < 1 || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

function endpoint(value: string, allowLoopbackHttp = true): URL {
	const result = new URL(value);
	const loopback = result.hostname === "localhost" || result.hostname === "127.0.0.1" || result.hostname === "[::1]";
	if (result.protocol !== "https:" && !(allowLoopbackHttp && loopback && result.protocol === "http:")) {
		throw new TypeError("Media provider endpoints must use HTTPS, except for loopback HTTP services.");
	}
	if (result.username || result.password || result.hash) throw new TypeError("Media provider endpoint is invalid.");
	return result;
}

function checkedOptions(options: GameHttpImageGeneratorOptions): CheckedHttpImageOptions {
	return {
		provider: validateMediaIdentifier(options.provider, "Provider id"),
		model: validateMediaIdentifier(options.model, "Model id"),
		endpoint: endpoint(options.endpoint),
		...(options.authentication === undefined ? {} : { authentication: options.authentication }),
		fetch: options.fetch ?? globalThis.fetch,
		maximumPromptBytes: positive(options.maximumPromptBytes, 1_000_000, "maximumPromptBytes", 16_000_000),
		maximumReferences: positive(options.maximumReferences, 16, "maximumReferences", 1_024),
		maximumReferenceBytes: positive(options.maximumReferenceBytes, 20_000_000, "maximumReferenceBytes", 512_000_000),
		maximumAggregateReferenceBytes: positive(
			options.maximumAggregateReferenceBytes,
			50_000_000,
			"maximumAggregateReferenceBytes",
			1_000_000_000,
		),
		maximumResponseBytes: positive(options.maximumResponseBytes, 100_000_000, "maximumResponseBytes", 1_000_000_000),
		maximumOutputBytes: positive(options.maximumOutputBytes, 30_000_000, "maximumOutputBytes", 512_000_000),
		maximumOutputs: positive(options.maximumOutputs, 10, "maximumOutputs", 1_024),
	};
}

function checkedRequest(request: GameMediaGenerationRequest, options: CheckedHttpImageOptions): void {
	if (request.kind !== "image") throw new TypeError("This provider only supports image generation.");
	if (
		request.prompt.length === 0 ||
		mediaByteLength(request.prompt) > options.maximumPromptBytes ||
		/\0/u.test(request.prompt)
	) {
		throw new RangeError("The image prompt is empty, too large, or invalid.");
	}
	if (request.sources.length > options.maximumReferences)
		throw new RangeError("The image reference count exceeds its limit.");
	let aggregate = 0;
	for (const source of request.sources) {
		if (source.kind !== "image" || !source.mimeType.startsWith("image/")) {
			throw new TypeError("Image generation references must be images.");
		}
		if (source.data.byteLength < 1 || source.data.byteLength > options.maximumReferenceBytes) {
			throw new RangeError("An image reference exceeds its byte limit.");
		}
		aggregate += source.data.byteLength;
		if (aggregate > options.maximumAggregateReferenceBytes)
			throw new RangeError("Image references exceed their aggregate limit.");
	}
}

function safeHeaders(headers: Readonly<Record<string, string>> | undefined): Headers {
	const result = new Headers();
	for (const [name, value] of Object.entries(headers ?? {})) {
		if (!/^[a-z0-9-]{1,64}$/iu.test(name) || /[\r\n\0]/u.test(value) || value.length > 8_192) {
			throw new TypeError("Media provider authentication returned an invalid header.");
		}
		const normalized = name.toLowerCase();
		if (["host", "content-length", "transfer-encoding", "connection"].includes(normalized)) {
			throw new TypeError("Media provider authentication returned a reserved header.");
		}
		result.set(name, value);
	}
	return result;
}

async function authenticatedHeaders(options: CheckedHttpImageOptions, signal?: AbortSignal): Promise<Headers> {
	const authentication = await options.authentication?.resolve(options.provider, signal);
	signal?.throwIfAborted();
	const headers = safeHeaders(authentication?.headers);
	if (authentication?.bearerToken !== undefined) {
		if (
			!authentication.bearerToken ||
			authentication.bearerToken.length > 16_384 ||
			/[\r\n\0]/u.test(authentication.bearerToken)
		) {
			throw new TypeError("Media provider authentication returned an invalid credential.");
		}
		headers.set("authorization", `Bearer ${authentication.bearerToken}`);
	}
	return headers;
}

async function readBounded(response: Response, maximumBytes: number, signal?: AbortSignal): Promise<Uint8Array> {
	const declared = response.headers.get("content-length");
	if (declared !== null && (!/^\d+$/u.test(declared) || Number(declared) > maximumBytes)) {
		throw new RangeError("The image provider response exceeds its byte limit.");
	}
	if (!response.body) return new Uint8Array();
	const reader = response.body.getReader();
	const chunks: Uint8Array[] = [];
	let total = 0;
	try {
		while (true) {
			signal?.throwIfAborted();
			const chunk = await reader.read();
			if (chunk.done) break;
			total += chunk.value.byteLength;
			if (total > maximumBytes) throw new RangeError("The image provider response exceeds its byte limit.");
			chunks.push(chunk.value);
		}
	} finally {
		reader.releaseLock();
	}
	const result = new Uint8Array(total);
	let offset = 0;
	for (const chunk of chunks) {
		result.set(chunk, offset);
		offset += chunk.byteLength;
	}
	return result;
}

async function readJson(response: Response, maximumBytes: number, signal?: AbortSignal): Promise<JsonObject> {
	const bytes = await readBounded(response, maximumBytes, signal);
	try {
		const parsed: unknown = JSON.parse(new TextDecoder().decode(bytes));
		if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("not-object");
		return parsed as JsonObject;
	} catch {
		throw new Error("The image provider returned malformed JSON.");
	}
}

function canonicalBase64(value: JsonValue | undefined, maximumBytes: number): Uint8Array {
	if (
		typeof value !== "string" ||
		value.length === 0 ||
		value.length % 4 !== 0 ||
		!/^[A-Za-z0-9+/]*={0,2}$/u.test(value)
	) {
		throw new TypeError("The image provider returned invalid base64 image data.");
	}
	const bytes = Buffer.from(value, "base64");
	if (bytes.byteLength < 1 || bytes.byteLength > maximumBytes || bytes.toString("base64") !== value) {
		throw new RangeError("The image provider returned invalid or oversized image data.");
	}
	return bytes;
}

function object(value: JsonValue | undefined, message: string): JsonObject {
	if (value === null || typeof value !== "object" || Array.isArray(value)) throw new TypeError(message);
	return value;
}

function array(value: JsonValue | undefined, message: string): JsonValue[] {
	if (!Array.isArray(value)) throw new TypeError(message);
	return value;
}

function responseId(response: Response): string | undefined {
	for (const name of ["x-request-id", "request-id", "x-tt-logid"]) {
		const value = response.headers.get(name);
		if (value && value.length <= 256 && !/[\r\n\0]/u.test(value)) return value;
	}
	return undefined;
}

async function sendJson(
	options: CheckedHttpImageOptions,
	body: JsonObject,
	signal?: AbortSignal,
): Promise<{ response: Response; json: JsonObject }> {
	const headers = await authenticatedHeaders(options, signal);
	headers.set("accept", "application/json");
	headers.set("content-type", "application/json");
	const response = await options.fetch(options.endpoint, {
		method: "POST",
		headers,
		body: JSON.stringify(body),
		redirect: "error",
		...(signal === undefined ? {} : { signal }),
	});
	if (!response.ok) throw new Error(`The image provider returned HTTP ${response.status}.`);
	return { response, json: await readJson(response, options.maximumResponseBytes, signal) };
}

const openAiSizes = new Set(["auto", "1024x1024", "1024x1536", "1536x1024"]);
const openAiFormats = new Set(["png", "jpeg", "webp"]);
const openAiQualities = new Set(["auto", "low", "medium", "high"]);
const openAiBackgrounds = new Set(["auto", "transparent", "opaque"]);

function optionalString(
	parameters: JsonObject,
	name: string,
	allowed: ReadonlySet<string>,
	fallback?: string,
): string | undefined {
	const value = parameters[name];
	if (value === undefined) return fallback;
	if (typeof value !== "string" || !allowed.has(value)) throw new TypeError(`Image parameter '${name}' is invalid.`);
	return value;
}

function outputCount(parameters: JsonObject, maximum: number): number {
	const value = parameters["n"] ?? 1;
	if (!Number.isInteger(value) || typeof value !== "number" || value < 1 || value > maximum) {
		throw new RangeError("Image output count is invalid.");
	}
	return value;
}

function allowedParameters(parameters: JsonObject, allowed: ReadonlySet<string>): void {
	for (const key of Object.keys(parameters)) {
		if (!allowed.has(key)) throw new TypeError("The image request contains an unsupported parameter.");
	}
}

export class OpenAIImageGenerator implements GameMediaGenerator {
	readonly provider: string;
	readonly model: string;
	readonly kinds: readonly GameMediaKind[] = ["image"];
	private readonly options: CheckedHttpImageOptions;

	constructor(options: GameHttpImageGeneratorOptions) {
		this.options = checkedOptions(options);
		this.provider = this.options.provider;
		this.model = this.options.model;
	}

	async generate(
		request: GameMediaGenerationRequest,
		_onProgress?: undefined,
		signal?: AbortSignal,
	): Promise<GameMediaGenerationResult> {
		checkedRequest(request, this.options);
		const parameters = request.parameters ?? {};
		allowedParameters(parameters, new Set(["size", "output_format", "n", "quality", "background"]));
		const size = optionalString(parameters, "size", openAiSizes, "1024x1024");
		const outputFormat = optionalString(parameters, "output_format", openAiFormats, "png");
		const quality = optionalString(parameters, "quality", openAiQualities);
		const background = optionalString(parameters, "background", openAiBackgrounds);
		const n = outputCount(parameters, this.options.maximumOutputs);
		const operation = request.sources.length === 0 ? "generations" : "edits";
		const requestUrl = new URL(this.options.endpoint);
		requestUrl.pathname = `${requestUrl.pathname.replace(/\/(generations|edits)$/u, "").replace(/\/$/u, "")}/${operation}`;
		const headers = await authenticatedHeaders(this.options, signal);
		headers.set("accept", "application/json");
		let body: string | FormData;
		if (request.sources.length === 0) {
			headers.set("content-type", "application/json");
			body = JSON.stringify({
				model: this.model,
				prompt: request.prompt,
				n,
				size,
				output_format: outputFormat,
				...(quality === undefined ? {} : { quality }),
				...(background === undefined ? {} : { background }),
			});
		} else {
			const form = new FormData();
			form.set("model", this.model);
			form.set("prompt", request.prompt);
			form.set("n", String(n));
			form.set("size", size ?? "1024x1024");
			form.set("output_format", outputFormat ?? "png");
			if (quality !== undefined) form.set("quality", quality);
			if (background !== undefined) form.set("background", background);
			request.sources.forEach((source, index) => {
				form.append("image[]", new Blob([source.data], { type: source.mimeType }), `reference-${index + 1}`);
			});
			body = form;
		}
		const response = await this.options.fetch(requestUrl, {
			method: "POST",
			headers,
			body,
			redirect: "error",
			...(signal === undefined ? {} : { signal }),
		});
		if (!response.ok) throw new Error(`The image provider returned HTTP ${response.status}.`);
		const json = await readJson(response, this.options.maximumResponseBytes, signal);
		const entries = array(json["data"], "The image provider returned no output list.");
		if (entries.length < 1 || entries.length > this.options.maximumOutputs)
			throw new RangeError("The image provider output count is invalid.");
		const mimeType = `image/${outputFormat === "jpeg" ? "jpeg" : outputFormat}`;
		const outputs = entries.map((entry) => ({
			kind: "image" as const,
			mimeType,
			data: canonicalBase64(
				object(entry, "The image provider returned an invalid output.")["b64_json"],
				this.options.maximumOutputBytes,
			),
		}));
		const id = responseId(response);
		return {
			outputs,
			provider: this.provider,
			model: this.model,
			...(id === undefined ? {} : { responseId: id }),
			...(json["usage"] === undefined ? {} : { usage: json["usage"] }),
		};
	}
}

export class VolcengineImageGenerator implements GameMediaGenerator {
	readonly provider: string;
	readonly model: string;
	readonly kinds: readonly GameMediaKind[] = ["image"];
	private readonly options: CheckedHttpImageOptions;

	constructor(options: GameHttpImageGeneratorOptions) {
		this.options = checkedOptions(options);
		this.provider = this.options.provider;
		this.model = this.options.model;
	}

	async generate(
		request: GameMediaGenerationRequest,
		_onProgress?: undefined,
		signal?: AbortSignal,
	): Promise<GameMediaGenerationResult> {
		checkedRequest(request, this.options);
		const parameters = request.parameters ?? {};
		allowedParameters(parameters, new Set(["size", "n", "watermark"]));
		const size = parameters["size"] ?? "2048x2048";
		if (typeof size !== "string" || !/^\d{2,5}x\d{2,5}$/u.test(size))
			throw new TypeError("Volcengine image size is invalid.");
		const [widthText, heightText] = size.split("x");
		const width = Number(widthText);
		const height = Number(heightText);
		if (!Number.isInteger(width) || !Number.isInteger(height) || width * height > 67_108_864)
			throw new RangeError("Volcengine image size is too large.");
		const n = outputCount(parameters, this.options.maximumOutputs);
		const watermark = parameters["watermark"] ?? false;
		if (typeof watermark !== "boolean") throw new TypeError("Volcengine watermark must be boolean.");
		const image = request.sources.map(
			(source) => `data:${source.mimeType};base64,${Buffer.from(source.data).toString("base64")}`,
		);
		const { response, json } = await sendJson(
			this.options,
			{
				model: this.model,
				prompt: request.prompt,
				response_format: "b64_json",
				size,
				n,
				stream: false,
				watermark,
				...(image.length === 0 ? {} : { image }),
			},
			signal,
		);
		const entries = array(json["data"], "The image provider returned no output list.");
		if (entries.length < 1 || entries.length > this.options.maximumOutputs)
			throw new RangeError("The image provider output count is invalid.");
		const outputs: GameMediaBinary[] = entries.map((entry) => ({
			kind: "image",
			mimeType: "image/png",
			data: canonicalBase64(
				object(entry, "The image provider returned an invalid output.")["b64_json"],
				this.options.maximumOutputBytes,
			),
		}));
		const id = responseId(response);
		return {
			outputs,
			provider: this.provider,
			model: this.model,
			...(id === undefined ? {} : { responseId: id }),
			...(json["usage"] === undefined ? {} : { usage: json["usage"] }),
		};
	}
}

import type { JsonObject, JsonValue } from "@opengameagent/protocol";
import type {
	GameMediaBinary,
	GameMediaGenerationRequest,
	GameMediaGenerationResult,
	GameMediaGenerator,
	GameMediaKind,
	GameMediaProviderAuthentication,
} from "./media.js";

export interface LocalAiMediaGeneratorOptions {
	model: string;
	endpoint?: string;
	authentication?: GameMediaProviderAuthentication;
	fetch?: typeof fetch;
	maximumRequestBytes?: number;
	maximumResponseBytes?: number;
	maximumOutputBytes?: number;
	maximumPromptBytes?: number;
	maximumSources?: number;
}

interface CheckedOptions {
	model: string;
	endpoint: string;
	authentication?: GameMediaProviderAuthentication;
	fetch: typeof fetch;
	maximumRequestBytes: number;
	maximumResponseBytes: number;
	maximumOutputBytes: number;
	maximumPromptBytes: number;
	maximumSources: number;
}

const imageParameters = new Set(["n", "size", "negative_prompt", "step", "seed", "cfg_scale"]);
const audioParameters = new Set(["voice", "response_format", "speed", "sample_rate", "language", "instructions"]);
const videoParameters = new Set([
	"negative_prompt",
	"width",
	"height",
	"num_frames",
	"fps",
	"seconds",
	"size",
	"seed",
	"cfg_scale",
	"step",
]);

function boundedInteger(value: number | undefined, fallback: number, maximum: number, name: string): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < 1 || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

function checkedEndpoint(value: string): string {
	const url = new URL(value);
	const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
	if (!loopback || (url.protocol !== "http:" && url.protocol !== "https:")) {
		throw new TypeError("LocalAI media requires a loopback HTTP endpoint.");
	}
	if (url.username || url.password || url.search || url.hash) throw new TypeError("The LocalAI endpoint is invalid.");
	return url.toString().replace(/\/$/u, "");
}

function checkedOptions(options: LocalAiMediaGeneratorOptions): CheckedOptions {
	if (!/^[a-z0-9][a-z0-9._:/-]{0,191}$/iu.test(options.model)) throw new TypeError("LocalAI model id is invalid.");
	return {
		model: options.model,
		endpoint: checkedEndpoint(options.endpoint ?? "http://127.0.0.1:8080/v1"),
		...(options.authentication ? { authentication: options.authentication } : {}),
		fetch: options.fetch ?? fetch,
		maximumRequestBytes: boundedInteger(
			options.maximumRequestBytes,
			64 * 1024 * 1024,
			256 * 1024 * 1024,
			"maximumRequestBytes",
		),
		maximumResponseBytes: boundedInteger(
			options.maximumResponseBytes,
			128 * 1024 * 1024,
			512 * 1024 * 1024,
			"maximumResponseBytes",
		),
		maximumOutputBytes: boundedInteger(
			options.maximumOutputBytes,
			128 * 1024 * 1024,
			512 * 1024 * 1024,
			"maximumOutputBytes",
		),
		maximumPromptBytes: boundedInteger(options.maximumPromptBytes, 1_000_000, 8_000_000, "maximumPromptBytes"),
		maximumSources: boundedInteger(options.maximumSources, 3, 16, "maximumSources"),
	};
}

function append(endpoint: string, relative: string, outsideV1 = false): string {
	const url = new URL(endpoint);
	let base = url.pathname.replace(/\/$/u, "");
	if (outsideV1 && base.endsWith("/v1")) base = base.slice(0, -3);
	url.pathname = `${base}/${relative}`.replace(/\/+/gu, "/");
	return url.toString();
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function checkedParameters(parameters: JsonObject | undefined, allowed: ReadonlySet<string>): JsonObject {
	const result = structuredClone(parameters ?? {});
	for (const [name, value] of Object.entries(result)) {
		if (!allowed.has(name)) throw new TypeError("The LocalAI media request contains an unsupported parameter.");
		if (typeof value === "number" && !Number.isFinite(value))
			throw new TypeError("A LocalAI numeric parameter is invalid.");
		if (typeof value === "string" && (value.length > 8_192 || /[\0\r\n]/u.test(value)))
			throw new TypeError("A LocalAI string parameter is invalid.");
		if (typeof value === "object" && value !== null)
			throw new TypeError("Nested LocalAI media parameters are not allowed.");
	}
	return result;
}

function dataUrl(source: GameMediaBinary): string {
	return `data:${source.mimeType};base64,${Buffer.from(source.data).toString("base64")}`;
}

function audioMime(format: unknown): string {
	switch (format ?? "wav") {
		case "wav":
			return "audio/wav";
		case "mp3":
			return "audio/mpeg";
		case "ogg":
			return "audio/ogg";
		case "flac":
			return "audio/flac";
		default:
			throw new TypeError("The LocalAI speech output format is invalid.");
	}
}

function magicMatches(kind: GameMediaKind, mimeType: string, bytes: Uint8Array): boolean {
	const text = (offset: number, length: number) =>
		Buffer.from(bytes.subarray(offset, offset + length)).toString("ascii");
	if (kind === "image") {
		return (
			(mimeType === "image/png" &&
				bytes.byteLength >= 8 &&
				Buffer.from(bytes.subarray(0, 8)).equals(Buffer.from("89504e470d0a1a0a", "hex"))) ||
			(mimeType === "image/jpeg" && bytes[0] === 0xff && bytes[1] === 0xd8) ||
			(mimeType === "image/webp" && text(0, 4) === "RIFF" && text(8, 4) === "WEBP")
		);
	}
	if (kind === "audio") {
		return (
			(mimeType === "audio/wav" && text(0, 4) === "RIFF" && text(8, 4) === "WAVE") ||
			(mimeType === "audio/ogg" && text(0, 4) === "OggS") ||
			(mimeType === "audio/flac" && text(0, 4) === "fLaC") ||
			(mimeType === "audio/mpeg" && (text(0, 3) === "ID3" || (bytes[0] === 0xff && ((bytes[1] ?? 0) & 0xe0) === 0xe0)))
		);
	}
	return (
		(mimeType === "video/mp4" && bytes.byteLength >= 12 && text(4, 4) === "ftyp") ||
		(mimeType === "video/webm" && Buffer.from(bytes.subarray(0, 4)).equals(Buffer.from("1a45dfa3", "hex")))
	);
}

function decodeBase64(value: unknown, maximumBytes: number): Uint8Array {
	if (typeof value !== "string" || !value || /\s/u.test(value) || value.length > Math.ceil(maximumBytes / 3) * 4 + 4)
		throw new TypeError("LocalAI returned invalid or oversized base64 media.");
	const bytes = new Uint8Array(Buffer.from(value, "base64"));
	if (bytes.byteLength < 1 || bytes.byteLength > maximumBytes || Buffer.from(bytes).toString("base64") !== value)
		throw new TypeError("LocalAI returned invalid or oversized base64 media.");
	return bytes;
}

async function boundedBytes(response: Response, maximumBytes: number, signal?: AbortSignal): Promise<Uint8Array> {
	const declared = Number(response.headers.get("content-length") ?? 0);
	if (declared > maximumBytes) throw new RangeError("LocalAI media response is too large.");
	if (!response.body) return new Uint8Array();
	const reader = response.body.getReader();
	const chunks: Uint8Array[] = [];
	let length = 0;
	try {
		for (;;) {
			signal?.throwIfAborted();
			const item = await reader.read();
			if (item.done) break;
			length += item.value.byteLength;
			if (length > maximumBytes) throw new RangeError("LocalAI media response is too large.");
			chunks.push(item.value);
		}
	} finally {
		reader.releaseLock();
	}
	const result = new Uint8Array(length);
	let offset = 0;
	for (const chunk of chunks) {
		result.set(chunk, offset);
		offset += chunk.byteLength;
	}
	return result;
}

function responseId(response: Response, body?: Record<string, unknown>): string | undefined {
	const value = body?.["id"] ?? response.headers.get("x-request-id");
	return typeof value === "string" &&
		value.length <= 512 &&
		![...value].some((character) => {
			const code = character.codePointAt(0) ?? 0;
			return code < 32 || code === 127;
		})
		? value
		: undefined;
}

export class LocalAiMediaGenerator implements GameMediaGenerator {
	readonly provider = "localai";
	readonly kinds = ["image", "audio", "video"] as const;
	readonly model: string;
	private readonly options: CheckedOptions;

	constructor(options: LocalAiMediaGeneratorOptions) {
		this.options = checkedOptions(options);
		this.model = this.options.model;
	}

	async generate(
		request: GameMediaGenerationRequest,
		onProgress?: (progress: { stage: string }) => void | Promise<void>,
		signal?: AbortSignal,
	): Promise<GameMediaGenerationResult> {
		if (Buffer.byteLength(request.prompt, "utf8") > this.options.maximumPromptBytes)
			throw new RangeError("The LocalAI media prompt is too large.");
		if (request.sources.length > this.options.maximumSources) throw new RangeError("Too many LocalAI media sources.");
		await onProgress?.({ stage: "requesting" });
		return request.kind === "audio" ? this.generateAudio(request, signal) : this.generateJson(request, signal);
	}

	private async headers(signal?: AbortSignal): Promise<Record<string, string>> {
		const auth = await this.options.authentication?.resolve(this.provider, signal);
		const result: Record<string, string> = {};
		if (auth?.bearerToken) result["Authorization"] = `Bearer ${auth.bearerToken}`;
		for (const [name, value] of Object.entries(auth?.headers ?? {})) {
			if (!/^[a-z0-9-]{1,64}$/iu.test(name) || /[\r\n\0]/u.test(value) || value.length > 8_192)
				throw new TypeError("LocalAI authentication returned an invalid header.");
			if (["host", "content-length", "content-type", "authorization"].includes(name.toLowerCase()))
				throw new TypeError("LocalAI authentication returned a controlled header.");
			result[name] = value;
		}
		return result;
	}

	private async generateAudio(
		request: GameMediaGenerationRequest,
		signal?: AbortSignal,
	): Promise<GameMediaGenerationResult> {
		if (request.sources.length > 0) throw new TypeError("LocalAI speech generation does not accept media sources.");
		const parameters = checkedParameters(request.parameters, audioParameters);
		const mimeType = audioMime(parameters["response_format"]);
		const payload = JSON.stringify({
			model: this.model,
			input: request.prompt,
			voice: parameters["voice"] ?? "alloy",
			...parameters,
		});
		if (Buffer.byteLength(payload) > this.options.maximumRequestBytes)
			throw new RangeError("LocalAI media request is too large.");
		const response = await this.options.fetch(append(this.options.endpoint, "audio/speech"), {
			method: "POST",
			headers: { ...(await this.headers(signal)), "content-type": "application/json" },
			body: payload,
			redirect: "error",
			...(signal ? { signal } : {}),
		});
		if (!response.ok) throw new Error(`LocalAI speech returned HTTP ${response.status}.`);
		const data = await boundedBytes(response, this.options.maximumOutputBytes, signal);
		if (!magicMatches("audio", mimeType, data))
			throw new TypeError("LocalAI speech bytes do not match the requested format.");
		const id = responseId(response);
		return {
			outputs: [{ kind: "audio", mimeType, data }],
			provider: this.provider,
			model: this.model,
			...(id ? { responseId: id } : {}),
		};
	}

	private async generateJson(
		request: GameMediaGenerationRequest,
		signal?: AbortSignal,
	): Promise<GameMediaGenerationResult> {
		const isVideo = request.kind === "video";
		const parameters = checkedParameters(request.parameters, isVideo ? videoParameters : imageParameters);
		if (!isVideo && request.sources.length > 0)
			throw new TypeError("Use a trusted workflow adapter for LocalAI image-to-image generation.");
		const payload: Record<string, JsonValue> = {
			model: this.model,
			prompt: request.prompt,
			response_format: "b64_json",
			...parameters,
		};
		if (isVideo) this.addVideoSources(payload, request.sources);
		const body = JSON.stringify(payload);
		if (Buffer.byteLength(body) > this.options.maximumRequestBytes)
			throw new RangeError("LocalAI media request is too large.");
		const response = await this.options.fetch(
			isVideo ? append(this.options.endpoint, "video", true) : append(this.options.endpoint, "images/generations"),
			{
				method: "POST",
				headers: { ...(await this.headers(signal)), "content-type": "application/json" },
				body,
				redirect: "error",
				...(signal ? { signal } : {}),
			},
		);
		if (!response.ok) throw new Error(`LocalAI media generation returned HTTP ${response.status}.`);
		const bytes = await boundedBytes(response, this.options.maximumResponseBytes, signal);
		let json: unknown;
		try {
			json = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
		} catch {
			throw new TypeError("LocalAI returned malformed JSON.");
		}
		if (!isRecord(json) || !Array.isArray(json["data"]) || json["data"].length < 1 || json["data"].length > 8)
			throw new TypeError("LocalAI returned an invalid media output list.");
		const mimeType = isVideo ? "video/mp4" : "image/png";
		const outputs = json["data"].map((entry): GameMediaBinary => {
			if (!isRecord(entry)) throw new TypeError("LocalAI returned an invalid media output.");
			const data = decodeBase64(entry["b64_json"], this.options.maximumOutputBytes);
			if (!magicMatches(request.kind, mimeType, data))
				throw new TypeError("LocalAI output bytes do not match their media kind.");
			return { kind: request.kind, mimeType, data };
		});
		const id = responseId(response, json);
		return {
			outputs,
			provider: this.provider,
			model: this.model,
			...(id ? { responseId: id } : {}),
			...(json["usage"] === undefined ? {} : { usage: json["usage"] as JsonValue }),
		};
	}

	private addVideoSources(payload: Record<string, JsonValue>, sources: readonly GameMediaBinary[]): void {
		let images = 0;
		let audio = false;
		for (const source of sources) {
			if (source.kind === "image") {
				if (images >= 2) throw new TypeError("LocalAI video accepts at most a start and end image.");
				payload[images++ === 0 ? "start_image" : "end_image"] = dataUrl(source);
			} else if (source.kind === "audio") {
				if (audio) throw new TypeError("LocalAI video accepts at most one audio source.");
				audio = true;
				payload["audio"] = dataUrl(source);
			} else {
				throw new TypeError("LocalAI video sources must be images or audio.");
			}
		}
	}
}

import type { RealtimeAudioFrame, RealtimeProviderAuthentication } from "@opengameagent/realtime";
import type {
	LocalSpeechRecognitionRequest,
	LocalSpeechRecognitionResult,
	LocalSpeechRecognizer,
	LocalSpeechSynthesisRequest,
	LocalSpeechSynthesizer,
} from "./local-speech.js";

interface CommonOptions {
	provider?: string;
	endpoint?: string;
	model: string;
	authentication?: RealtimeProviderAuthentication;
	fetch?: typeof fetch;
	maximumRequestBytes?: number;
	maximumResponseBytes?: number;
	timeoutMilliseconds?: number;
}

export interface OpenAICompatibleSpeechRecognizerOptions extends CommonOptions {
	maximumTextCharacters?: number;
}

export interface OpenAICompatibleSpeechSynthesizerOptions extends CommonOptions {
	responseFormat?: "pcm" | "wav";
	rawPcmSampleRate?: number;
	rawPcmChannels?: number;
	audioFrameBytes?: number;
}

interface CheckedCommon {
	provider: string;
	endpoint: string;
	model: string;
	authentication?: RealtimeProviderAuthentication;
	fetch: typeof fetch;
	maximumRequestBytes: number;
	maximumResponseBytes: number;
	timeoutMilliseconds: number;
}

function bounded(value: number | undefined, fallback: number, maximum: number, name: string): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < 1 || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

function identifier(value: string, name: string): string {
	if (!/^[a-z0-9][a-z0-9._:/-]{0,255}$/iu.test(value)) throw new TypeError(`${name} is invalid.`);
	return value;
}

function localEndpoint(value: string): string {
	const url = new URL(value);
	const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
	if (!loopback || (url.protocol !== "http:" && url.protocol !== "https:"))
		throw new TypeError("Local speech requires a loopback HTTP endpoint.");
	if (url.username || url.password || url.search || url.hash)
		throw new TypeError("The local speech endpoint is invalid.");
	return url.toString().replace(/\/$/u, "");
}

function common(options: CommonOptions): CheckedCommon {
	return {
		provider: identifier(options.provider ?? "local-speech", "Local speech provider id"),
		endpoint: localEndpoint(options.endpoint ?? "http://127.0.0.1:8080/v1"),
		model: identifier(options.model, "Local speech model id"),
		...(options.authentication ? { authentication: options.authentication } : {}),
		fetch: options.fetch ?? fetch,
		maximumRequestBytes: bounded(
			options.maximumRequestBytes,
			32 * 1024 * 1024,
			256 * 1024 * 1024,
			"maximumRequestBytes",
		),
		maximumResponseBytes: bounded(
			options.maximumResponseBytes,
			64 * 1024 * 1024,
			512 * 1024 * 1024,
			"maximumResponseBytes",
		),
		timeoutMilliseconds: bounded(options.timeoutMilliseconds, 120_000, 600_000, "timeoutMilliseconds"),
	};
}

function endpoint(base: string, suffix: string): string {
	const url = new URL(base);
	const path = url.pathname.replace(/\/$/u, "");
	url.pathname = `${path.endsWith("/v1") ? path : `${path}/v1`}/${suffix}`.replace(/\/+/gu, "/");
	return url.toString();
}

async function headers(options: CheckedCommon, signal?: AbortSignal): Promise<Record<string, string>> {
	const auth = await options.authentication?.resolve(options.provider, signal);
	const result: Record<string, string> = {};
	if (auth?.apiKey) result["Authorization"] = `Bearer ${auth.apiKey}`;
	for (const [name, value] of Object.entries(auth?.headers ?? {})) {
		if (!/^[a-z0-9-]{1,64}$/iu.test(name) || /[\r\n\0]/u.test(value) || value.length > 8_192)
			throw new TypeError("Local speech authentication returned an invalid header.");
		if (["host", "content-length", "content-type", "authorization"].includes(name.toLowerCase()))
			throw new TypeError("Local speech authentication returned a controlled header.");
		result[name] = value;
	}
	return result;
}

function requestSignal(timeoutMilliseconds: number, signal?: AbortSignal): AbortSignal {
	const timeout = AbortSignal.timeout(timeoutMilliseconds);
	return signal ? AbortSignal.any([signal, timeout]) : timeout;
}

function wave(pcm16: Uint8Array, sampleRate: number, channels: number): Uint8Array {
	if (
		pcm16.byteLength < 2 ||
		pcm16.byteLength % (2 * channels) !== 0 ||
		!Number.isInteger(sampleRate) ||
		sampleRate < 8_000 ||
		sampleRate > 192_000 ||
		!Number.isInteger(channels) ||
		channels < 1 ||
		channels > 8
	) {
		throw new TypeError("Speech recognition input is not bounded PCM16.");
	}
	const result = new Uint8Array(44 + pcm16.byteLength);
	const view = new DataView(result.buffer);
	const text = (offset: number, value: string) => result.set(Buffer.from(value, "ascii"), offset);
	text(0, "RIFF");
	view.setUint32(4, result.byteLength - 8, true);
	text(8, "WAVE");
	text(12, "fmt ");
	view.setUint32(16, 16, true);
	view.setUint16(20, 1, true);
	view.setUint16(22, channels, true);
	view.setUint32(24, sampleRate, true);
	view.setUint32(28, sampleRate * channels * 2, true);
	view.setUint16(32, channels * 2, true);
	view.setUint16(34, 16, true);
	text(36, "data");
	view.setUint32(40, pcm16.byteLength, true);
	result.set(pcm16, 44);
	return result;
}

async function boundedBytes(response: Response, maximum: number, signal: AbortSignal): Promise<Uint8Array> {
	const declared = Number(response.headers.get("content-length") ?? 0);
	if (declared > maximum) throw new RangeError("Local speech response is too large.");
	if (!response.body) return new Uint8Array();
	const reader = response.body.getReader();
	const chunks: Uint8Array[] = [];
	let total = 0;
	try {
		for (;;) {
			signal.throwIfAborted();
			const chunk = await reader.read();
			if (chunk.done) break;
			total += chunk.value.byteLength;
			if (total > maximum) throw new RangeError("Local speech response is too large.");
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

function parseWave(bytes: Uint8Array): { pcm16: Uint8Array; sampleRate: number; channels: number } {
	const text = (offset: number, length: number) =>
		Buffer.from(bytes.subarray(offset, offset + length)).toString("ascii");
	if (bytes.byteLength < 44 || text(0, 4) !== "RIFF" || text(8, 4) !== "WAVE")
		throw new TypeError("Local speech returned an invalid WAV file.");
	const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
	let offset = 12;
	let sampleRate: number | undefined;
	let channels: number | undefined;
	let pcm16: Uint8Array | undefined;
	while (offset <= bytes.byteLength - 8) {
		const kind = text(offset, 4);
		const length = view.getUint32(offset + 4, true);
		const start = offset + 8;
		if (length > bytes.byteLength - start) throw new TypeError("Local speech WAV is truncated.");
		if (kind === "fmt ") {
			if (length < 16 || view.getUint16(start, true) !== 1 || view.getUint16(start + 14, true) !== 16)
				throw new TypeError("Local speech WAV must contain PCM16 audio.");
			channels = view.getUint16(start + 2, true);
			sampleRate = view.getUint32(start + 4, true);
		} else if (kind === "data") {
			pcm16 = bytes.slice(start, start + length);
		}
		offset = start + length + (length % 2);
	}
	if (!pcm16 || !sampleRate || !channels || pcm16.byteLength < 2 || pcm16.byteLength % (channels * 2) !== 0)
		throw new TypeError("Local speech WAV is incomplete.");
	return { pcm16, sampleRate, channels };
}

export class OpenAICompatibleSpeechRecognizer implements LocalSpeechRecognizer {
	private readonly options: CheckedCommon;
	private readonly maximumTextCharacters: number;
	constructor(options: OpenAICompatibleSpeechRecognizerOptions) {
		this.options = common(options);
		this.maximumTextCharacters = bounded(options.maximumTextCharacters, 65_536, 1_000_000, "maximumTextCharacters");
	}
	async transcribe(
		request: LocalSpeechRecognitionRequest,
		signal?: AbortSignal,
	): Promise<LocalSpeechRecognitionResult> {
		const audio = wave(request.pcm16, request.sampleRate, request.channels);
		if (audio.byteLength > this.options.maximumRequestBytes) throw new RangeError("Local speech request is too large.");
		const body = new FormData();
		body.set("file", new Blob([audio], { type: "audio/wav" }), "speech.wav");
		body.set("model", this.options.model);
		body.set("response_format", "json");
		if (request.language) body.set("language", identifier(request.language, "Speech language"));
		const operation = requestSignal(this.options.timeoutMilliseconds, signal);
		const response = await this.options.fetch(endpoint(this.options.endpoint, "audio/transcriptions"), {
			method: "POST",
			headers: await headers(this.options, operation),
			body,
			redirect: "error",
			signal: operation,
		});
		if (!response.ok) throw new Error(`Local speech recognition returned HTTP ${response.status}.`);
		const bytes = await boundedBytes(response, this.options.maximumResponseBytes, operation);
		let parsed: unknown;
		try {
			parsed = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
		} catch {
			throw new TypeError("Local speech recognition returned malformed JSON.");
		}
		const text =
			typeof parsed === "object" && parsed !== null ? (parsed as Record<string, unknown>)["text"] : undefined;
		if (typeof text !== "string" || !text.trim() || text.length > this.maximumTextCharacters)
			throw new TypeError("Local speech recognition returned invalid text.");
		return { text };
	}
}

export class OpenAICompatibleSpeechSynthesizer implements LocalSpeechSynthesizer {
	private readonly options: CheckedCommon;
	private readonly responseFormat: "pcm" | "wav";
	private readonly sampleRate: number;
	private readonly channels: number;
	private readonly frameBytes: number;
	constructor(options: OpenAICompatibleSpeechSynthesizerOptions) {
		this.options = common(options);
		this.responseFormat = options.responseFormat ?? "pcm";
		this.sampleRate = bounded(options.rawPcmSampleRate, 24_000, 192_000, "rawPcmSampleRate");
		if (this.sampleRate < 8_000) throw new RangeError("rawPcmSampleRate is invalid.");
		this.channels = bounded(options.rawPcmChannels, 1, 8, "rawPcmChannels");
		this.frameBytes = bounded(options.audioFrameBytes, 24_000, 4 * 1024 * 1024, "audioFrameBytes");
		if (this.frameBytes % (2 * this.channels) !== 0) throw new RangeError("audioFrameBytes is not PCM aligned.");
	}

	async *synthesize(request: LocalSpeechSynthesisRequest, signal?: AbortSignal): AsyncIterable<RealtimeAudioFrame> {
		if (
			!request.text ||
			Buffer.byteLength(request.text, "utf8") > this.options.maximumRequestBytes ||
			/\0/u.test(request.text)
		)
			throw new RangeError("Local speech synthesis text is invalid or too large.");
		identifier(request.voice, "Speech voice");
		identifier(request.itemId, "Speech item id");
		const body = JSON.stringify({
			model: this.options.model,
			input: request.text,
			voice: request.voice,
			response_format: this.responseFormat,
		});
		const operation = requestSignal(this.options.timeoutMilliseconds, signal);
		const response = await this.options.fetch(endpoint(this.options.endpoint, "audio/speech"), {
			method: "POST",
			headers: { ...(await headers(this.options, operation)), "content-type": "application/json" },
			body,
			redirect: "error",
			signal: operation,
		});
		if (!response.ok) throw new Error(`Local speech synthesis returned HTTP ${response.status}.`);
		const contentType = response.headers.get("content-type");
		if (contentType && !contentType.startsWith("audio/") && !contentType.startsWith("application/octet-stream"))
			throw new TypeError("Local speech synthesis returned non-audio content.");
		if (this.responseFormat === "wav") {
			const parsed = parseWave(await boundedBytes(response, this.options.maximumResponseBytes, operation));
			for (let offset = 0; offset < parsed.pcm16.byteLength; offset += this.frameBytes) {
				yield {
					pcm16: parsed.pcm16.slice(offset, offset + this.frameBytes),
					sampleRate: parsed.sampleRate,
					channels: parsed.channels,
					itemId: request.itemId,
				};
			}
			return;
		}
		yield* this.readPcm(response, request.itemId, operation);
	}

	private async *readPcm(response: Response, itemId: string, signal: AbortSignal): AsyncIterable<RealtimeAudioFrame> {
		const declared = Number(response.headers.get("content-length") ?? 0);
		if (declared > this.options.maximumResponseBytes) throw new RangeError("Local speech response is too large.");
		if (!response.body) throw new TypeError("Local speech synthesis returned no audio stream.");
		const reader = response.body.getReader();
		let pending = new Uint8Array();
		let total = 0;
		try {
			for (;;) {
				signal.throwIfAborted();
				const chunk = await reader.read();
				if (chunk.done) break;
				total += chunk.value.byteLength;
				if (total > this.options.maximumResponseBytes) throw new RangeError("Local speech response is too large.");
				const merged = new Uint8Array(pending.byteLength + chunk.value.byteLength);
				merged.set(pending);
				merged.set(chunk.value, pending.byteLength);
				let offset = 0;
				while (merged.byteLength - offset >= this.frameBytes) {
					yield {
						pcm16: merged.slice(offset, offset + this.frameBytes),
						sampleRate: this.sampleRate,
						channels: this.channels,
						itemId,
					};
					offset += this.frameBytes;
				}
				pending = merged.slice(offset);
			}
			if (pending.byteLength % (2 * this.channels) !== 0) throw new TypeError("Local speech PCM ended mid-sample.");
			if (pending.byteLength > 0)
				yield { pcm16: pending, sampleRate: this.sampleRate, channels: this.channels, itemId };
		} finally {
			reader.releaseLock();
		}
	}
}

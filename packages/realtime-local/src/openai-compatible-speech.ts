import { createBoundedFetch } from "@opengameagent/client";
import { raceOperationWithAbort } from "@opengameagent/protocol";
import type {
	RealtimeAudioChunk,
	RealtimeAudioFormat,
	RealtimeSpeechRecognizer,
	RealtimeSpeechSynthesizer,
	SpeechRecognitionEvent,
	SpeechRecognitionRequest,
	SpeechSynthesisRequest,
} from "@opengameagent/realtime";
import OpenAI, { toFile } from "openai";
import {
	LocalSpeechCapacityError,
	LocalSpeechConfigurationError,
	LocalSpeechProtocolError,
	LocalSpeechProviderError,
	LocalSpeechTimeoutError,
} from "./errors.js";
import {
	captureAsyncIterator,
	captureIteratorResult,
	captureIteratorResultWithAbort,
	closeIterator,
	detachCleanup,
} from "./iterator.js";
import { collectPcmS16LeWav } from "./pcm-s16le-wav.js";
import type {
	OpenAICompatibleSpeechCommonOptions,
	OpenAICompatibleSpeechRecognizerOptions,
	OpenAICompatibleSpeechSynthesizerOptions,
	OpenAISpeechResponseFormat,
} from "./types.js";
import { PCM_S16LE_MEDIA_TYPE } from "./types.js";
import {
	addBounded,
	assertPositiveSafeInteger,
	assertTimer,
	assertWavCapacity,
	captureBoundedString,
	captureFormat,
	inspectBytes,
	NODE_MAX_TIMER_DELAY_MS,
	sameFormat,
	snapshotBytes,
	utf8Bytes,
} from "./validation.js";

interface CapturedCommon {
	readonly baseUrl: string;
	readonly apiKey: string;
	readonly modelId: string;
	readonly requestTimeoutMs: number;
	readonly maxIdentifierBytes: number;
	readonly maxMediaTypeBytes: number;
	readonly maxResponseBytes: number;
	readonly fetch: typeof fetch;
}

interface ProviderDeadline {
	readonly signal: AbortSignal;
	abort(): void;
	close(): void;
}

export class OpenAICompatibleSpeechRecognizer implements RealtimeSpeechRecognizer {
	private readonly client: OpenAI;
	private readonly options: CapturedCommon & {
		readonly inputMediaType: string;
		readonly language?: string;
		readonly maxInputChunks: number;
		readonly maxInputChunkBytes: number;
		readonly maxInputAudioBytes: number;
		readonly maxTranscriptBytes: number;
	};

	constructor(options: Readonly<OpenAICompatibleSpeechRecognizerOptions>) {
		const common = captureCommon(options);
		let inputMediaType: string;
		let language: string | undefined;
		let maxInputChunks: number;
		let maxInputChunkBytes: number;
		let maxInputAudioBytes: number;
		let maxTranscriptBytes: number;
		try {
			inputMediaType = captureBoundedString(options.inputMediaType, common.maxMediaTypeBytes, true);
			language = options.language;
			maxInputChunks = options.maxInputChunks;
			maxInputChunkBytes = options.maxInputChunkBytes;
			maxInputAudioBytes = options.maxInputAudioBytes;
			maxTranscriptBytes = options.maxTranscriptBytes;
		} catch (error) {
			if (error instanceof LocalSpeechConfigurationError) throw error;
			throw new LocalSpeechConfigurationError();
		}
		assertPositiveSafeInteger(maxInputChunks);
		assertPositiveSafeInteger(maxInputChunkBytes);
		assertPositiveSafeInteger(maxInputAudioBytes);
		assertPositiveSafeInteger(maxTranscriptBytes);
		assertWavCapacity(maxInputAudioBytes);
		const capturedLanguage =
			language === undefined ? undefined : captureBoundedString(language, common.maxIdentifierBytes, true);
		if (inputMediaType !== PCM_S16LE_MEDIA_TYPE) throw new LocalSpeechConfigurationError();
		this.options = Object.freeze({
			...common,
			inputMediaType,
			...(capturedLanguage === undefined ? {} : { language: capturedLanguage }),
			maxInputChunks,
			maxInputChunkBytes,
			maxInputAudioBytes,
			maxTranscriptBytes,
		});
		this.client = createClient(common);
	}

	async *recognize(request: Readonly<SpeechRecognitionRequest>): AsyncIterable<Readonly<SpeechRecognitionEvent>> {
		const { format, audio, signal } = captureRecognitionRequest(request, this.options.maxMediaTypeBytes);
		if (format.mediaType !== PCM_S16LE_MEDIA_TYPE || format.mediaType !== this.options.inputMediaType) {
			throw new LocalSpeechProtocolError();
		}
		const wav = await collectPcmS16LeWav(
			{ audio, format, signal },
			{
				maxChunks: this.options.maxInputChunks,
				maxChunkBytes: this.options.maxInputChunkBytes,
				maxAudioBytes: this.options.maxInputAudioBytes,
			},
		);
		const deadline = providerDeadline(signal, this.options.requestTimeoutMs);
		let transcript: string;
		try {
			const response = await raceOperationWithAbort(
				async () =>
					this.client.audio.transcriptions.create(
						{
							file: await toFile(wav, "speech.wav", { type: "audio/wav" }),
							model: this.options.modelId,
							response_format: "json",
							...(this.options.language === undefined ? {} : { language: this.options.language }),
						},
						{ signal: deadline.signal },
					),
				deadline.signal,
			);
			deadline.signal.throwIfAborted();
			if (!response || typeof response !== "object") throw new LocalSpeechProtocolError();
			let text: unknown;
			try {
				text = response.text;
			} catch {
				throw new LocalSpeechProtocolError();
			}
			if (typeof text !== "string") throw new LocalSpeechProtocolError();
			if (utf8Bytes(text) > this.options.maxTranscriptBytes) throw new LocalSpeechCapacityError();
			transcript = text;
		} catch (error) {
			rethrowProviderBoundary(error, signal, deadline.signal);
		} finally {
			deadline.close();
		}
		yield Object.freeze({ type: "transcript.final", text: transcript });
	}
}

export class OpenAICompatibleSpeechSynthesizer implements RealtimeSpeechSynthesizer {
	private readonly client: OpenAI;
	private readonly options: CapturedCommon & {
		readonly outputFormat: Readonly<RealtimeAudioFormat>;
		readonly responseFormat: OpenAISpeechResponseFormat;
		readonly maxTextSegments: number;
		readonly maxTextBytes: number;
		readonly maxResponseChunks: number;
		readonly maxResponseChunkBytes: number;
	};

	constructor(options: Readonly<OpenAICompatibleSpeechSynthesizerOptions>) {
		const common = captureCommon(options);
		let outputFormat: unknown;
		let responseFormat: OpenAISpeechResponseFormat;
		let maxTextSegments: number;
		let maxTextBytes: number;
		let maxResponseChunks: number;
		let maxResponseChunkBytes: number;
		try {
			outputFormat = options.outputFormat;
			responseFormat = options.responseFormat;
			maxTextSegments = options.maxTextSegments;
			maxTextBytes = options.maxTextBytes;
			maxResponseChunks = options.maxResponseChunks;
			maxResponseChunkBytes = options.maxResponseChunkBytes;
		} catch {
			throw new LocalSpeechConfigurationError();
		}
		if (!isResponseFormat(responseFormat)) throw new LocalSpeechConfigurationError();
		assertPositiveSafeInteger(maxTextSegments);
		assertPositiveSafeInteger(maxTextBytes);
		assertPositiveSafeInteger(maxResponseChunks);
		assertPositiveSafeInteger(maxResponseChunkBytes);
		let capturedOutputFormat: Readonly<RealtimeAudioFormat>;
		try {
			capturedOutputFormat = captureFormat(outputFormat, common.maxMediaTypeBytes);
		} catch {
			throw new LocalSpeechConfigurationError();
		}
		if (
			capturedOutputFormat.mediaType !== capturedOutputFormat.mediaType.trim() ||
			capturedOutputFormat.mediaType.includes(";")
		) {
			throw new LocalSpeechConfigurationError();
		}
		this.options = Object.freeze({
			...common,
			outputFormat: capturedOutputFormat,
			responseFormat,
			maxTextSegments,
			maxTextBytes,
			maxResponseChunks,
			maxResponseChunkBytes,
		});
		this.client = createClient(common);
	}

	async *synthesize(request: Readonly<SpeechSynthesisRequest>): AsyncIterable<Readonly<RealtimeAudioChunk>> {
		const { voiceProfileId, format, text, signal } = captureSynthesisRequest(request, this.options);
		if (!sameFormat(format, this.options.outputFormat)) throw new LocalSpeechProtocolError();
		const input = await collectText(text, signal, this.options.maxTextSegments, this.options.maxTextBytes);
		signal.throwIfAborted();
		if (input.length === 0) return;
		const deadline = providerDeadline(signal, this.options.requestTimeoutMs);
		let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
		let responseBody: ReadableStream<Uint8Array> | undefined;
		let responseRequest: Promise<Response> | undefined;
		let response: Response | undefined;
		let reachedProviderEof = false;
		try {
			responseRequest = Promise.resolve(
				this.client.audio.speech.create(
					{
						input,
						model: this.options.modelId,
						voice: voiceProfileId,
						response_format: this.options.responseFormat,
						stream_format: "audio",
					},
					{ signal: deadline.signal },
				),
			);
			response = await raceOperationWithAbort(() => responseRequest as Promise<Response>, deadline.signal);
			deadline.signal.throwIfAborted();
			if (!response.body) throw new LocalSpeechProtocolError();
			responseBody = response.body;
			assertResponseMediaType(response, this.options.outputFormat.mediaType);
			reader = responseBody.getReader();
			let chunks = 0;
			while (true) {
				const result = captureIteratorResult<Uint8Array>(
					await raceOperationWithAbort(
						() => (reader as ReadableStreamDefaultReader<Uint8Array>).read(),
						deadline.signal,
					),
				);
				if (result.done) {
					reachedProviderEof = true;
					return;
				}
				chunks += 1;
				if (chunks > this.options.maxResponseChunks) throw new LocalSpeechCapacityError();
				const view = inspectBytes({ bytes: result.value });
				if (view.byteLength > this.options.maxResponseChunkBytes) throw new LocalSpeechCapacityError();
				if (view.byteLength > 0) yield Object.freeze({ bytes: snapshotBytes(view) });
			}
		} catch (error) {
			rethrowProviderBoundary(error, signal, deadline.signal);
		} finally {
			if (!reachedProviderEof) deadline.abort();
			if (reader) {
				const activeReader = reader;
				detachCleanup(() => activeReader.cancel());
			} else if (responseBody) {
				const activeBody = responseBody;
				detachCleanup(() => activeBody.cancel());
			} else if (!response && responseRequest) {
				cancelLateResponse(responseRequest);
			}
			deadline.close();
		}
	}
}

function cancelLateResponse(responseRequest: Promise<Response>): void {
	void responseRequest.then(
		(response) => {
			let body: ReadableStream<Uint8Array> | null;
			try {
				body = response.body;
			} catch {
				return;
			}
			if (body) detachCleanup(() => body.cancel());
		},
		() => undefined,
	);
}

function captureCommon(options: Readonly<OpenAICompatibleSpeechCommonOptions>): CapturedCommon {
	if (!options || typeof options !== "object") throw new LocalSpeechConfigurationError();
	let baseUrl: string;
	let apiKey: string;
	let modelId: string;
	let requestTimeoutMs: number;
	let maxEndpointBytes: number;
	let maxCredentialBytes: number;
	let maxIdentifierBytes: number;
	let maxMediaTypeBytes: number;
	let maxResponseBytes: number;
	let configuredFetch: typeof fetch | undefined;
	try {
		baseUrl = options.baseUrl;
		apiKey = options.apiKey;
		modelId = options.modelId;
		requestTimeoutMs = options.requestTimeoutMs;
		maxEndpointBytes = options.maxEndpointBytes;
		maxCredentialBytes = options.maxCredentialBytes;
		maxIdentifierBytes = options.maxIdentifierBytes;
		maxMediaTypeBytes = options.maxMediaTypeBytes;
		maxResponseBytes = options.maxResponseBytes;
		configuredFetch = options.fetch;
	} catch {
		throw new LocalSpeechConfigurationError();
	}
	assertPositiveSafeInteger(maxEndpointBytes);
	assertPositiveSafeInteger(maxCredentialBytes);
	assertPositiveSafeInteger(maxIdentifierBytes);
	assertPositiveSafeInteger(maxMediaTypeBytes);
	assertPositiveSafeInteger(maxResponseBytes);
	assertTimer(requestTimeoutMs);
	const endpoint = captureEndpoint(baseUrl, maxEndpointBytes);
	const credential = captureBoundedString(apiKey, maxCredentialBytes, true);
	const model = captureBoundedString(modelId, maxIdentifierBytes, true);
	const upstream = configuredFetch ?? globalThis.fetch;
	if (typeof upstream !== "function") throw new LocalSpeechConfigurationError();
	return Object.freeze({
		baseUrl: endpoint,
		apiKey: credential,
		modelId: model,
		requestTimeoutMs,
		maxIdentifierBytes,
		maxMediaTypeBytes,
		maxResponseBytes,
		fetch: upstream,
	});
}

function createClient(options: CapturedCommon): OpenAI {
	const bounded = createBoundedFetch(
		options.fetch,
		options.maxResponseBytes,
		options.maxResponseBytes,
		() => new LocalSpeechCapacityError(),
		undefined,
		undefined,
		"total-body",
	);
	const abortOwned = createAbortOwnedFetch(bounded);
	Object.defineProperty(abortOwned, "Response", { value: Response });
	return new OpenAI({
		baseURL: options.baseUrl,
		apiKey: options.apiKey,
		maxRetries: 0,
		timeout: NODE_MAX_TIMER_DELAY_MS,
		fetch: abortOwned,
	});
}

function createAbortOwnedFetch(upstream: typeof fetch): typeof fetch {
	return async (input, init) => {
		let signal: AbortSignal | undefined;
		const candidate = input instanceof Request ? input.signal : init?.signal;
		signal = candidate instanceof AbortSignal ? candidate : undefined;
		signal?.throwIfAborted();
		const responseRequest = Promise.resolve(upstream(input, init));
		if (!signal) return responseRequest;
		try {
			const response = await raceOperationWithAbort(() => responseRequest, signal);
			return bindResponseBodyToAbort(response, signal);
		} catch (error) {
			cancelLateResponse(responseRequest);
			throw error;
		}
	};
}

function bindResponseBodyToAbort(response: Response, signal: AbortSignal): Response {
	if (!response.body) return response;
	const reader = response.body.getReader();
	let closed = false;
	let controller: ReadableStreamDefaultController<Uint8Array<ArrayBuffer>> | undefined;
	const finish = (reason: unknown, errorBody: boolean): boolean => {
		if (closed) return false;
		closed = true;
		signal.removeEventListener("abort", onAbort);
		if (errorBody) {
			try {
				controller?.error(reason);
			} catch {
				// The wrapper may already have been closed by its consumer.
			}
		}
		detachCleanup(() => reader.cancel(reason));
		return true;
	};
	const onAbort = () => finish(signal.reason, true);
	const body = new ReadableStream<Uint8Array<ArrayBuffer>>({
		start(activeController) {
			controller = activeController;
			signal.addEventListener("abort", onAbort, { once: true });
			if (signal.aborted) onAbort();
		},
		async pull(activeController) {
			try {
				const result = await raceOperationWithAbort(() => reader.read(), signal);
				if (result.done) {
					closed = true;
					signal.removeEventListener("abort", onAbort);
					try {
						reader.releaseLock();
					} catch {
						// A completed reader may already have released its lock.
					}
					activeController.close();
					return;
				}
				activeController.enqueue(result.value);
			} catch (error) {
				finish(error, true);
			}
		},
		cancel(reason) {
			finish(reason, false);
		},
	});
	return new Response(body, {
		status: response.status,
		statusText: response.statusText,
		headers: response.headers,
	});
}

function captureEndpoint(value: unknown, maxBytes: number): string {
	const text = captureBoundedString(value, maxBytes, true);
	if (text.trim() !== text || text.includes("\\") || text.includes("?") || text.includes("#")) {
		throw new LocalSpeechConfigurationError();
	}
	let url: URL;
	try {
		url = new URL(text);
	} catch {
		throw new LocalSpeechConfigurationError();
	}
	if (
		(url.protocol !== "https:" && !(url.protocol === "http:" && isLoopback(url.hostname))) ||
		url.username !== "" ||
		url.password !== "" ||
		url.search !== "" ||
		url.hash !== ""
	) {
		throw new LocalSpeechConfigurationError();
	}
	return url.toString().replace(/\/$/u, "");
}

function isLoopback(hostname: string): boolean {
	if (hostname.toLowerCase() === "localhost" || hostname === "[::1]") return true;
	const parts = hostname.split(".");
	return (
		parts.length === 4 &&
		parts[0] === "127" &&
		parts.every((part) => /^(?:0|[1-9][0-9]{0,2})$/u.test(part) && Number(part) <= 255)
	);
}

function captureRecognitionRequest(request: Readonly<SpeechRecognitionRequest>, maxMediaTypeBytes: number) {
	if (!request || typeof request !== "object") throw new LocalSpeechProtocolError();
	let signal: AbortSignal;
	let format: unknown;
	let audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	try {
		signal = request.signal;
	} catch {
		throw new LocalSpeechProtocolError();
	}
	if (!(signal instanceof AbortSignal)) throw new LocalSpeechProtocolError();
	signal.throwIfAborted();
	try {
		format = request.format;
		audio = request.audio;
	} catch {
		signal.throwIfAborted();
		throw new LocalSpeechProtocolError();
	}
	signal.throwIfAborted();
	let capturedFormat: Readonly<RealtimeAudioFormat>;
	try {
		capturedFormat = captureFormat(format, maxMediaTypeBytes);
	} catch (error) {
		signal.throwIfAborted();
		throw error;
	}
	signal.throwIfAborted();
	return Object.freeze({
		format: capturedFormat,
		audio,
		signal,
	});
}

function captureSynthesisRequest(
	request: Readonly<SpeechSynthesisRequest>,
	options: { readonly maxIdentifierBytes: number; readonly maxMediaTypeBytes: number },
) {
	if (!request || typeof request !== "object") throw new LocalSpeechProtocolError();
	let signal: AbortSignal;
	let voiceProfileId: unknown;
	let format: unknown;
	let text: AsyncIterable<string>;
	try {
		signal = request.signal;
	} catch {
		throw new LocalSpeechProtocolError();
	}
	if (!(signal instanceof AbortSignal)) throw new LocalSpeechProtocolError();
	signal.throwIfAborted();
	try {
		voiceProfileId = request.voiceProfileId;
		format = request.format;
		text = request.text;
	} catch {
		signal.throwIfAborted();
		throw new LocalSpeechProtocolError();
	}
	signal.throwIfAborted();
	let capturedFormat: Readonly<RealtimeAudioFormat>;
	try {
		capturedFormat = captureFormat(format, options.maxMediaTypeBytes);
	} catch (error) {
		signal.throwIfAborted();
		throw error;
	}
	signal.throwIfAborted();
	return Object.freeze({
		voiceProfileId: captureBoundedString(voiceProfileId, options.maxIdentifierBytes),
		format: capturedFormat,
		text,
		signal,
	});
}

function assertResponseMediaType(response: Response, expected: string): void {
	let value: string | null;
	try {
		value = response.headers.get("content-type");
	} catch {
		throw new LocalSpeechProtocolError();
	}
	const mediaType = value?.split(";", 1)[0]?.trim().toLowerCase();
	if (!mediaType || mediaType !== expected.toLowerCase()) throw new LocalSpeechProtocolError();
}

async function collectText(
	text: AsyncIterable<string>,
	signal: AbortSignal,
	maxSegments: number,
	maxBytes: number,
): Promise<string> {
	let iterator: ReturnType<typeof captureAsyncIterator<string>>;
	try {
		iterator = captureAsyncIterator<string>(text);
	} catch (error) {
		signal.throwIfAborted();
		throw error;
	}
	const parts: string[] = [];
	let segments = 0;
	let bytes = 0;
	try {
		while (true) {
			const result = captureIteratorResultWithAbort<string>(
				await raceOperationWithAbort(() => iterator.next(), signal),
				signal,
			);
			if (result.done) return parts.join("");
			segments += 1;
			if (segments > maxSegments) throw new LocalSpeechCapacityError();
			if (typeof result.value !== "string") throw new LocalSpeechProtocolError();
			bytes = addBounded(bytes, utf8Bytes(result.value), maxBytes);
			parts.push(result.value);
		}
	} finally {
		closeIterator(iterator);
	}
}

function providerDeadline(caller: AbortSignal, timeoutMs: number): ProviderDeadline {
	caller.throwIfAborted();
	const controller = new AbortController();
	const onAbort = () => controller.abort(caller.reason);
	caller.addEventListener("abort", onAbort, { once: true });
	if (caller.aborted) onAbort();
	const timeout = setTimeout(() => controller.abort(new LocalSpeechTimeoutError()), timeoutMs);
	return Object.freeze({
		signal: controller.signal,
		abort() {
			controller.abort();
		},
		close() {
			clearTimeout(timeout);
			caller.removeEventListener("abort", onAbort);
		},
	});
}

function rethrowProviderBoundary(error: unknown, caller: AbortSignal, provider: AbortSignal): never {
	caller.throwIfAborted();
	if (provider.aborted) throw provider.reason;
	let cause: unknown;
	try {
		cause = error && typeof error === "object" ? (error as { readonly cause?: unknown }).cause : undefined;
	} catch {
		cause = undefined;
	}
	if (isInstance(cause, LocalSpeechCapacityError)) throw cause;
	if (
		isInstance(error, LocalSpeechCapacityError) ||
		isInstance(error, LocalSpeechProtocolError) ||
		isInstance(error, LocalSpeechTimeoutError)
	) {
		throw error;
	}
	throw new LocalSpeechProviderError();
}

function isInstance<T extends object>(value: unknown, errorType: new (...args: never[]) => T): value is T {
	try {
		return value instanceof errorType;
	} catch {
		return false;
	}
}

function isResponseFormat(value: unknown): value is OpenAISpeechResponseFormat {
	return (
		value === "mp3" || value === "opus" || value === "aac" || value === "flac" || value === "wav" || value === "pcm"
	);
}

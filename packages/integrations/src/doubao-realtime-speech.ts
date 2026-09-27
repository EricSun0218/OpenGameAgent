import { Buffer } from "node:buffer";
import { randomUUID } from "node:crypto";
import type { CredentialStore } from "@earendil-works/pi-ai";
import { raceOperationWithAbort } from "@opengameagent/protocol";
import type {
	RealtimeAudioFormat,
	RealtimeSpeechRecognizer,
	RealtimeSpeechSynthesizer,
	SpeechRecognitionEvent,
	SpeechRecognitionRequest,
	SpeechSynthesisRequest,
} from "@opengameagent/realtime";
import { PCM_S16LE_MEDIA_TYPE } from "@opengameagent/realtime-local";
import {
	createDoubaoAsrAudioFrame,
	createDoubaoAsrConfigFrame,
	createDoubaoTtsEventFrame,
	parseDoubaoAsrResponse,
	parseDoubaoTtsResponse,
} from "./doubao-realtime-speech-protocol.js";
import { connectDoubaoWebSocket } from "./doubao-realtime-websocket.js";
import {
	DoubaoRealtimeSpeechAdapterError,
	type CreateDoubaoRealtimeSpeechRecognizerOptions,
	type CreateDoubaoRealtimeSpeechSynthesizerOptions,
	type DoubaoAsrLimits,
	type DoubaoAuthentication,
	type DoubaoRealtimeSpeechTransportLimits,
	type DoubaoTtsLimits,
	type DoubaoWebSocketConnection,
	type DoubaoWebSocketConnector,
	type DoubaoWebSocketMessage,
} from "./doubao-realtime-speech-types.js";

export * from "./doubao-realtime-speech-types.js";

const MAX_SIGNED_32_BIT_INTEGER = 2_147_483_647;
const TYPED_ARRAY_BYTE_LENGTH_GETTER = Object.getOwnPropertyDescriptor(
	Object.getPrototypeOf(Uint8Array.prototype) as object,
	"byteLength",
)?.get;

interface CapturedAudioFormat {
	readonly mediaType: string;
	readonly sampleRateHz: number;
	readonly channelCount: number;
}

const TRANSPORT_LIMIT_NAMES = [
	"maxEndpointBytes",
	"maxCredentialReferenceBytes",
	"maxCredentialBytes",
	"maxAppKeyBytes",
	"maxResourceIdBytes",
	"maxIdentifierBytes",
	"maxMediaTypeBytes",
	"maxFrameBytes",
	"maxPayloadBytes",
	"maxJsonDepth",
	"maxPendingFrames",
	"maxWebSocketFragments",
	"maxWebSocketBufferedChunks",
	"maxBufferedAmountBytes",
	"connectTimeoutMs",
	"messageTimeoutMs",
	"operationTimeoutMs",
	"closeTimeoutMs",
] as const satisfies readonly (keyof DoubaoRealtimeSpeechTransportLimits)[];

const ASR_LIMIT_NAMES = [
	...TRANSPORT_LIMIT_NAMES,
	"maxInputChunks",
	"maxInputChunkBytes",
	"maxInputAudioBytes",
	"maxRecognitionEvents",
	"maxRecognitionTextBytes",
] as const satisfies readonly (keyof DoubaoAsrLimits)[];

const TTS_LIMIT_NAMES = [
	...TRANSPORT_LIMIT_NAMES,
	"maxTextSegments",
	"maxTextSegmentBytes",
	"maxTextBytes",
	"maxOutputChunks",
	"maxOutputChunkBytes",
	"maxOutputAudioBytes",
] as const satisfies readonly (keyof DoubaoTtsLimits)[];

interface CapturedRecognizerOptions {
	readonly credentials: CredentialStore;
	readonly credentialReference: string;
	readonly endpoint: string;
	readonly authentication: Readonly<DoubaoAuthentication>;
	readonly resourceId: string;
	readonly userId: string;
	readonly modelName: string;
	readonly language: string;
	readonly inputFormat: Readonly<CapturedAudioFormat>;
	readonly limits: Readonly<DoubaoAsrLimits>;
	readonly connect: DoubaoWebSocketConnector;
}

interface CapturedSynthesizerOptions {
	readonly credentials: CredentialStore;
	readonly credentialReference: string;
	readonly endpoint: string;
	readonly authentication: Readonly<DoubaoAuthentication>;
	readonly resourceId: string;
	readonly outputFormat: Readonly<CapturedAudioFormat>;
	readonly outputEncoding: "pcm";
	readonly limits: Readonly<DoubaoTtsLimits>;
	readonly createSessionId: () => string;
	readonly connect: DoubaoWebSocketConnector;
}

interface CapturedAsyncIterator<T> {
	readonly next: () => PromiseLike<IteratorResult<T>> | IteratorResult<T>;
	close(): void;
}

interface CapturedCredentialOptions {
	readonly credentials: CredentialStore;
	readonly credentialReference: string;
	readonly limits: Readonly<Pick<DoubaoRealtimeSpeechTransportLimits, "maxCredentialBytes">>;
}

export function createDoubaoRealtimeSpeechRecognizer(
	options: Readonly<CreateDoubaoRealtimeSpeechRecognizerOptions>,
): RealtimeSpeechRecognizer {
	const captured = captureRecognizerOptions(options);
	return Object.freeze({
		async *recognize(request: Readonly<SpeechRecognitionRequest>): AsyncIterable<Readonly<SpeechRecognitionEvent>> {
			const callerSignal = request.signal;
			callerSignal.throwIfAborted();
			if (!sameFormat(request.format, captured.inputFormat)) throw new DoubaoRealtimeSpeechAdapterError("protocol");
			const deadline = createOperationDeadline(callerSignal, captured.limits.operationTimeoutMs);
			const signal = deadline.signal;
			let connection: DoubaoWebSocketConnection | undefined;
			let producer: AbortScope | undefined;
			let audio: CapturedAsyncIterator<Readonly<{ readonly bytes: Uint8Array }>> | undefined;
			let audioTransferred = false;
			let completed = false;
			try {
				audio = captureAsyncIterator(() => request.audio);
				const configurationFrame = createDoubaoAsrConfigFrame(
					{
						userId: captured.userId,
						sampleRateHz: captured.inputFormat.sampleRateHz,
						channelCount: captured.inputFormat.channelCount,
						language: captured.language,
						modelName: captured.modelName,
					},
					captured.limits,
				);
				const credential = await readCredential(captured, signal);
				const headers: Record<string, string> = {
					...createDoubaoAuthenticationHeaders(captured.resourceId, captured.authentication, credential),
					"X-Api-Request-Id": randomUUID(),
				};
				connection = await connectWithTimeout(captured, headers, signal);
				const activeConnection = connection;
				await sendFrame(activeConnection, configurationFrame, captured, signal);
				producer = createAbortScope(signal);
				audioTransferred = true;
				const sending = sendAsrAudio(activeConnection, audio, captured, producer.signal);
				const sendingFailure = failureOnly(sending);
				let events = 0;
				let textBytes = 0;
				let lastSequence = 0;
				while (true) {
					const message = await Promise.race([
						receiveWithTimeout(activeConnection, signal, captured.limits.messageTimeoutMs),
						sendingFailure,
					]);
					signal.throwIfAborted();
					if (message.type === "closed") throw new DoubaoRealtimeSpeechAdapterError("transport");
					if (message.type !== "binary") throw new DoubaoRealtimeSpeechAdapterError("protocol");
					const response = parseDoubaoAsrResponse(message.bytes, captured.limits);
					if (response.code !== 0) throw new DoubaoRealtimeSpeechAdapterError("transport");
					if (response.sequence !== undefined) {
						const sequence = Math.abs(response.sequence);
						if (
							sequence <= lastSequence ||
							(response.last && response.sequence >= 0) ||
							(!response.last && response.sequence <= 0)
						) {
							throw new DoubaoRealtimeSpeechAdapterError("protocol");
						}
						lastSequence = sequence;
					} else if (response.text !== undefined || response.last) {
						throw new DoubaoRealtimeSpeechAdapterError("protocol");
					}
					if (response.text !== undefined) {
						events += 1;
						const bytes = Buffer.byteLength(response.text, "utf8");
						if (
							events > captured.limits.maxRecognitionEvents ||
							bytes > captured.limits.maxRecognitionTextBytes - textBytes
						) {
							throw new DoubaoRealtimeSpeechAdapterError("capacity");
						}
						textBytes += bytes;
						yield Object.freeze({
							type: response.last ? "transcript.final" : "transcript.delta",
							text: response.text,
						});
					}
					if (response.last) {
						if (response.text === undefined || response.text.length === 0) {
							throw new DoubaoRealtimeSpeechAdapterError("protocol");
						}
						await sending;
						completed = true;
						break;
					}
				}
			} catch (error) {
				signal.throwIfAborted();
				if (error instanceof DoubaoRealtimeSpeechAdapterError) throw error;
				throw new DoubaoRealtimeSpeechAdapterError("transport");
			} finally {
				producer?.abort(signal.aborted ? signal.reason : new DoubaoRealtimeSpeechAdapterError("transport"));
				producer?.close();
				if (audio && !audioTransferred) audio.close();
				try {
					if (connection) {
						if (completed) await closeWithTimeout(connection, signal, captured.limits.closeTimeoutMs);
						else abortConnection(connection);
					}
				} finally {
					deadline.close();
				}
			}
		},
	});
}

export function createDoubaoRealtimeSpeechSynthesizer(
	options: Readonly<CreateDoubaoRealtimeSpeechSynthesizerOptions>,
): RealtimeSpeechSynthesizer {
	const captured = captureSynthesizerOptions(options);
	return Object.freeze({
		async *synthesize(request: Readonly<SpeechSynthesisRequest>) {
			const callerSignal = request.signal;
			callerSignal.throwIfAborted();
			if (!sameFormat(request.format, captured.outputFormat)) throw new DoubaoRealtimeSpeechAdapterError("protocol");
			const voiceProfileId = boundedString(
				request.voiceProfileId,
				captured.limits.maxIdentifierBytes,
				"invalid_configuration",
			);
			const deadline = createOperationDeadline(callerSignal, captured.limits.operationTimeoutMs);
			const signal = deadline.signal;
			let connection: DoubaoWebSocketConnection | undefined;
			let producer: AbortScope | undefined;
			let text: CapturedAsyncIterator<string> | undefined;
			let textExhausted = false;
			let textTransferred = false;
			let completed = false;
			try {
				const textIterator = captureAsyncIterator<string>(() => request.text);
				text = textIterator;
				const first = await abortable(() => textIterator.next(), signal);
				if (first.done) {
					textExhausted = true;
					return;
				}
				const sessionId = boundedString(
					captured.createSessionId(),
					captured.limits.maxIdentifierBytes,
					"invalid_configuration",
				);
				const credential = await readCredential(captured, signal);
				const headers = createDoubaoAuthenticationHeaders(captured.resourceId, captured.authentication, credential);
				connection = await connectWithTimeout(captured, headers, signal);
				const activeConnection = connection;
				await sendTtsEvent(activeConnection, 1, "", {}, captured, signal);
				await expectTtsEvent(activeConnection, 50, "", captured, signal);
				await sendTtsEvent(
					activeConnection,
					100,
					sessionId,
					{
						req_params: {
							speaker: voiceProfileId,
							audio_params: {
								format: captured.outputEncoding,
								sample_rate: captured.outputFormat.sampleRateHz,
							},
						},
					},
					captured,
					signal,
				);
				await expectTtsEvent(activeConnection, 150, sessionId, captured, signal);
				producer = createAbortScope(signal);
				textTransferred = true;
				const sending = (async () => {
					await sendTtsText(activeConnection, textIterator, first.value, sessionId, captured, producer.signal);
					await sendTtsEvent(activeConnection, 102, sessionId, {}, captured, producer.signal);
				})();
				const sendingFailure = failureOnly(sending);
				let chunks = 0;
				let audioBytes = 0;
				while (true) {
					const response = parseDoubaoTtsResponse(
						await Promise.race([
							receiveWithTimeout(activeConnection, signal, captured.limits.messageTimeoutMs),
							sendingFailure,
						]),
						captured.limits,
					);
					if (response.sessionId !== sessionId) throw new DoubaoRealtimeSpeechAdapterError("protocol");
					if (response.event === 352) {
						if (response.messageType !== 11 || response.payload.byteLength === 0) {
							throw new DoubaoRealtimeSpeechAdapterError("protocol");
						}
						chunks += 1;
						audioBytes += response.payload.byteLength;
						if (
							chunks > captured.limits.maxOutputChunks ||
							response.payload.byteLength > captured.limits.maxOutputChunkBytes ||
							audioBytes > captured.limits.maxOutputAudioBytes
						) {
							throw new DoubaoRealtimeSpeechAdapterError("capacity");
						}
						yield Object.freeze({ bytes: Uint8Array.from(response.payload) });
						continue;
					}
					if (response.messageType !== 9) throw new DoubaoRealtimeSpeechAdapterError("protocol");
					if (response.event === 350 || response.event === 351 || response.event === 364) continue;
					if (response.event === 153) throw new DoubaoRealtimeSpeechAdapterError("transport");
					if (response.event !== 152) throw new DoubaoRealtimeSpeechAdapterError("protocol");
					await sending;
					break;
				}
				await sendTtsEvent(activeConnection, 2, "", {}, captured, signal);
				await expectTtsEvent(activeConnection, 52, "", captured, signal);
				completed = true;
			} catch (error) {
				signal.throwIfAborted();
				if (error instanceof DoubaoRealtimeSpeechAdapterError) throw error;
				throw new DoubaoRealtimeSpeechAdapterError("transport");
			} finally {
				producer?.abort(signal.aborted ? signal.reason : new DoubaoRealtimeSpeechAdapterError("transport"));
				producer?.close();
				if (text && !textExhausted && !textTransferred) text.close();
				try {
					if (connection) {
						if (completed) await closeWithTimeout(connection, signal, captured.limits.closeTimeoutMs);
						else abortConnection(connection);
					}
				} finally {
					deadline.close();
				}
			}
		},
	});
}

function captureSynthesizerOptions(
	options: Readonly<CreateDoubaoRealtimeSpeechSynthesizerOptions>,
): CapturedSynthesizerOptions {
	return captureConfiguration(() => captureSynthesizerOptionsUnchecked(options));
}

function captureSynthesizerOptionsUnchecked(
	options: Readonly<CreateDoubaoRealtimeSpeechSynthesizerOptions>,
): CapturedSynthesizerOptions {
	if (!options || typeof options !== "object") throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	const {
		credentials,
		credentialReference,
		endpoint: endpointValue,
		authentication: authenticationValue,
		resourceId,
		outputFormat: outputFormatValue,
		limits: limitsValue,
		createSessionId,
		connect,
	} = options;
	const limits = captureLimits<DoubaoTtsLimits>(limitsValue, TTS_LIMIT_NAMES);
	const endpoint = captureWebSocketEndpoint(endpointValue, limits.maxEndpointBytes);
	if (typeof credentials?.read !== "function" || (connect !== undefined && typeof connect !== "function")) {
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	if (typeof createSessionId !== "function") {
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	const authentication = captureAuthentication(authenticationValue, limits.maxAppKeyBytes);
	const outputFormat = captureFormat(outputFormatValue, limits.maxMediaTypeBytes);
	if (outputFormat.mediaType !== PCM_S16LE_MEDIA_TYPE || outputFormat.channelCount !== 1) {
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	return Object.freeze({
		credentials,
		credentialReference: boundedString(
			credentialReference,
			limits.maxCredentialReferenceBytes,
			"invalid_configuration",
		),
		endpoint,
		authentication,
		resourceId: boundedString(resourceId, limits.maxResourceIdBytes, "invalid_configuration"),
		outputFormat,
		outputEncoding: "pcm",
		limits,
		createSessionId,
		connect: connect ?? connectDoubaoWebSocket,
	});
}

async function sendTtsText(
	connection: DoubaoWebSocketConnection,
	iterator: CapturedAsyncIterator<string>,
	first: string,
	sessionId: string,
	options: CapturedSynthesizerOptions,
	signal: AbortSignal,
): Promise<void> {
	let current: IteratorResult<string> = { done: false, value: first };
	let segments = 0;
	let total = 0;
	let reachedEnd = false;
	try {
		while (!current.done) {
			const value = current.value;
			if (typeof value !== "string" || value.length === 0) {
				throw new DoubaoRealtimeSpeechAdapterError("protocol");
			}
			const bytes = Buffer.byteLength(value, "utf8");
			segments += 1;
			total += bytes;
			if (
				segments > options.limits.maxTextSegments ||
				bytes > options.limits.maxTextSegmentBytes ||
				total > options.limits.maxTextBytes
			) {
				throw new DoubaoRealtimeSpeechAdapterError("capacity");
			}
			await sendTtsEvent(connection, 200, sessionId, { text: value }, options, signal);
			current = await abortable(() => iterator.next(), signal);
		}
		reachedEnd = true;
	} finally {
		if (!reachedEnd) iterator.close();
	}
}

async function sendTtsEvent(
	connection: DoubaoWebSocketConnection,
	event: number,
	sessionId: string,
	payload: unknown,
	options: CapturedSynthesizerOptions,
	signal: AbortSignal,
): Promise<void> {
	const frame = createDoubaoTtsEventFrame(event, sessionId, payload, options.limits);
	if (connection.bufferedAmount() > options.limits.maxBufferedAmountBytes) {
		throw new DoubaoRealtimeSpeechAdapterError("capacity");
	}
	await abortable(() => connection.send(frame, signal), signal);
}

async function expectTtsEvent(
	connection: DoubaoWebSocketConnection,
	event: number,
	sessionId: string,
	options: CapturedSynthesizerOptions,
	signal: AbortSignal,
): Promise<void> {
	const response = parseDoubaoTtsResponse(
		await receiveWithTimeout(connection, signal, options.limits.messageTimeoutMs),
		options.limits,
	);
	if (response.event === 51 || response.event === 153) throw new DoubaoRealtimeSpeechAdapterError("transport");
	if (response.event !== event || response.sessionId !== sessionId || response.messageType !== 9) {
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
}

function captureRecognizerOptions(
	options: Readonly<CreateDoubaoRealtimeSpeechRecognizerOptions>,
): CapturedRecognizerOptions {
	return captureConfiguration(() => captureRecognizerOptionsUnchecked(options));
}

function captureRecognizerOptionsUnchecked(
	options: Readonly<CreateDoubaoRealtimeSpeechRecognizerOptions>,
): CapturedRecognizerOptions {
	if (!options || typeof options !== "object") throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	const {
		credentials,
		credentialReference,
		endpoint: endpointValue,
		authentication: authenticationValue,
		resourceId,
		userId,
		modelName,
		language,
		inputFormat: inputFormatValue,
		limits: limitsValue,
		connect,
	} = options;
	const limits = captureLimits<DoubaoAsrLimits>(limitsValue, ASR_LIMIT_NAMES);
	const endpoint = captureWebSocketEndpoint(endpointValue, limits.maxEndpointBytes);
	const inputFormat = captureFormat(inputFormatValue, limits.maxMediaTypeBytes);
	if (inputFormat.mediaType !== PCM_S16LE_MEDIA_TYPE) {
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	if (typeof credentials?.read !== "function" || (connect !== undefined && typeof connect !== "function")) {
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	const authentication = captureAuthentication(authenticationValue, limits.maxAppKeyBytes);
	return Object.freeze({
		credentials,
		credentialReference: boundedString(
			credentialReference,
			limits.maxCredentialReferenceBytes,
			"invalid_configuration",
		),
		endpoint,
		authentication,
		resourceId: boundedString(resourceId, limits.maxResourceIdBytes, "invalid_configuration"),
		userId: boundedString(userId, limits.maxIdentifierBytes, "invalid_configuration"),
		modelName: boundedString(modelName, limits.maxIdentifierBytes, "invalid_configuration"),
		language: boundedString(language, limits.maxIdentifierBytes, "invalid_configuration"),
		inputFormat,
		limits,
		connect: connect ?? connectDoubaoWebSocket,
	});
}

async function readCredential(options: CapturedCredentialOptions, signal: AbortSignal): Promise<string> {
	let credential: Awaited<ReturnType<CredentialStore["read"]>>;
	try {
		credential = await abortable(() => options.credentials.read(options.credentialReference, { signal }), signal);
	} catch {
		signal.throwIfAborted();
		throw new DoubaoRealtimeSpeechAdapterError("credential");
	}
	if (credential?.type !== "api_key") throw new DoubaoRealtimeSpeechAdapterError("credential");
	return boundedString(credential.key, options.limits.maxCredentialBytes, "credential");
}

function createDoubaoAuthenticationHeaders(
	resourceId: string,
	authentication: Readonly<DoubaoAuthentication>,
	credential: string,
): Record<string, string> {
	if (authentication.type === "api-key") {
		return { "X-Api-Resource-Id": resourceId, "X-Api-Key": credential };
	}
	return {
		"X-Api-Resource-Id": resourceId,
		"X-Api-App-Key": authentication.appKey,
		"X-Api-Access-Key": credential,
	};
}

async function sendAsrAudio(
	connection: DoubaoWebSocketConnection,
	iterator: CapturedAsyncIterator<Readonly<{ readonly bytes: Uint8Array }>>,
	options: CapturedRecognizerOptions,
	signal: AbortSignal,
): Promise<void> {
	let reachedEnd = false;
	try {
		let current = await abortable(() => iterator.next(), signal);
		if (current.done) throw new DoubaoRealtimeSpeechAdapterError("protocol");
		let chunks = 0;
		let total = 0;
		let sequence = 2;
		while (!current.done) {
			signal.throwIfAborted();
			const capturedAudio = captureAudio(current.value, options.limits.maxInputChunkBytes);
			if (
				chunks >= options.limits.maxInputChunks ||
				capturedAudio.byteLength > options.limits.maxInputAudioBytes - total
			) {
				throw new DoubaoRealtimeSpeechAdapterError("capacity");
			}
			const bytes = copyAudio(capturedAudio);
			chunks += 1;
			total += capturedAudio.byteLength;
			const next = await abortable(() => iterator.next(), signal);
			const last = next.done === true;
			await sendFrame(connection, createDoubaoAsrAudioFrame(sequence, bytes, last, options.limits), options, signal);
			sequence += 1;
			current = next;
		}
		reachedEnd = true;
	} finally {
		if (!reachedEnd) iterator.close();
	}
}

async function sendFrame(
	connection: DoubaoWebSocketConnection,
	frame: Uint8Array,
	options: CapturedRecognizerOptions,
	signal: AbortSignal,
): Promise<void> {
	if (frame.byteLength > options.limits.maxFrameBytes || frame.byteLength > options.limits.maxPayloadBytes + 12) {
		throw new DoubaoRealtimeSpeechAdapterError("capacity");
	}
	if (connection.bufferedAmount() > options.limits.maxBufferedAmountBytes) {
		throw new DoubaoRealtimeSpeechAdapterError("capacity");
	}
	await abortable(() => connection.send(frame, signal), signal);
}

function captureFormat(value: Readonly<RealtimeAudioFormat>, maxMediaTypeBytes: number): Readonly<CapturedAudioFormat> {
	if (!value || typeof value !== "object") throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	const { mediaType, sampleRateHz, channelCount } = value;
	if (
		typeof mediaType !== "string" ||
		Buffer.byteLength(mediaType, "utf8") > maxMediaTypeBytes ||
		typeof sampleRateHz !== "number" ||
		!Number.isSafeInteger(sampleRateHz) ||
		sampleRateHz <= 0 ||
		typeof channelCount !== "number" ||
		!Number.isSafeInteger(channelCount) ||
		channelCount <= 0
	) {
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	return Object.freeze({
		mediaType,
		sampleRateHz,
		channelCount,
	});
}

function captureWebSocketEndpoint(value: unknown, maxBytes: number): string {
	const endpoint = boundedString(value, maxBytes, "invalid_configuration");
	let url: URL;
	try {
		url = new URL(endpoint);
	} catch {
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	if (
		(url.protocol !== "wss:" && !(url.protocol === "ws:" && isLoopback(url.hostname))) ||
		url.username !== "" ||
		url.password !== "" ||
		url.hash !== ""
	) {
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	return boundedString(url.toString(), maxBytes, "invalid_configuration");
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

function abortable<T>(operation: () => PromiseLike<T> | T, signal: AbortSignal): Promise<T> {
	return raceOperationWithAbort(() => Promise.resolve().then(operation), signal);
}

function failureOnly(operation: Promise<void>): Promise<never> {
	return operation.then(() => new Promise<never>(() => undefined));
}

function captureAsyncIterator<T>(readSource: () => unknown): CapturedAsyncIterator<T> {
	try {
		const source = readSource();
		if (!source || (typeof source !== "object" && typeof source !== "function")) {
			throw new DoubaoRealtimeSpeechAdapterError("protocol");
		}
		const createIterator = (source as AsyncIterable<T>)[Symbol.asyncIterator];
		if (typeof createIterator !== "function") throw new DoubaoRealtimeSpeechAdapterError("protocol");
		const iterator = createIterator.call(source);
		if (!iterator || typeof iterator !== "object") throw new DoubaoRealtimeSpeechAdapterError("protocol");
		const close = iterator.return;
		if (close !== undefined && typeof close !== "function") {
			throw new DoubaoRealtimeSpeechAdapterError("protocol");
		}
		let next: AsyncIterator<T>["next"];
		try {
			next = iterator.next;
		} catch {
			closeCapturedIterator(iterator, close);
			throw new DoubaoRealtimeSpeechAdapterError("protocol");
		}
		if (typeof next !== "function") {
			closeCapturedIterator(iterator, close);
			throw new DoubaoRealtimeSpeechAdapterError("protocol");
		}
		return Object.freeze({
			next: () => next.call(iterator),
			close: () => closeCapturedIterator(iterator, close),
		});
	} catch (error) {
		if (error instanceof DoubaoRealtimeSpeechAdapterError) throw error;
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
}

function closeCapturedIterator(iterator: AsyncIterator<unknown>, close: AsyncIterator<unknown>["return"]): void {
	if (typeof close !== "function") return;
	void Promise.resolve()
		.then(() => close.call(iterator))
		.catch(() => undefined);
}

interface OperationDeadline {
	readonly signal: AbortSignal;
	close(): void;
}

interface AbortScope {
	readonly signal: AbortSignal;
	abort(reason: unknown): void;
	close(): void;
}

function createAbortScope(parentSignal: AbortSignal): AbortScope {
	parentSignal.throwIfAborted();
	const controller = new AbortController();
	let closed = false;
	const onParentAbort = () => controller.abort(parentSignal.reason);
	parentSignal.addEventListener("abort", onParentAbort, { once: true });
	if (parentSignal.aborted) onParentAbort();
	return Object.freeze({
		signal: controller.signal,
		abort(reason: unknown) {
			controller.abort(reason);
		},
		close() {
			if (closed) return;
			closed = true;
			parentSignal.removeEventListener("abort", onParentAbort);
		},
	});
}

function createOperationDeadline(callerSignal: AbortSignal, timeoutMs: number): OperationDeadline {
	callerSignal.throwIfAborted();
	const controller = new AbortController();
	let closed = false;
	const onCallerAbort = () => controller.abort(callerSignal.reason);
	const timer = setTimeout(() => controller.abort(new DoubaoRealtimeSpeechAdapterError("timeout")), timeoutMs);
	callerSignal.addEventListener("abort", onCallerAbort, { once: true });
	if (callerSignal.aborted) onCallerAbort();
	return Object.freeze({
		signal: controller.signal,
		close() {
			if (closed) return;
			closed = true;
			clearTimeout(timer);
			callerSignal.removeEventListener("abort", onCallerAbort);
		},
	});
}

async function closeWithTimeout(
	connection: DoubaoWebSocketConnection,
	operationSignal: AbortSignal,
	timeoutMs: number,
): Promise<void> {
	try {
		await withTimeout((closeSignal) => connection.close(closeSignal), operationSignal, timeoutMs);
	} catch (error) {
		abortConnection(connection, error);
		operationSignal.throwIfAborted();
		if (error instanceof DoubaoRealtimeSpeechAdapterError) throw error;
		throw new DoubaoRealtimeSpeechAdapterError("transport");
	}
}

function abortConnection(connection: DoubaoWebSocketConnection, reason?: unknown): void {
	try {
		connection.abort(reason);
	} catch {
		// Cleanup is best-effort and must not replace the primary operation result.
	}
}

interface CapturedConnectionOptions {
	readonly endpoint: string;
	readonly limits: Readonly<DoubaoRealtimeSpeechTransportLimits>;
	readonly connect: DoubaoWebSocketConnector;
}

async function connectWithTimeout(
	options: Readonly<CapturedConnectionOptions>,
	headers: Readonly<Record<string, string>>,
	operationSignal: AbortSignal,
): Promise<DoubaoWebSocketConnection> {
	return withTimeout(
		async (connectSignal) => {
			const connected = await options.connect(
				Object.freeze({
					endpoint: options.endpoint,
					headers: Object.freeze({ ...headers }),
					maxFrameBytes: options.limits.maxFrameBytes,
					maxPendingFrames: options.limits.maxPendingFrames,
					maxWebSocketFragments: options.limits.maxWebSocketFragments,
					maxWebSocketBufferedChunks: options.limits.maxWebSocketBufferedChunks,
					connectTimeoutMs: options.limits.connectTimeoutMs,
					closeTimeoutMs: options.limits.closeTimeoutMs,
				}),
				connectSignal,
			);
			if (connectSignal.aborted) {
				abortConnection(connected, connectSignal.reason);
				connectSignal.throwIfAborted();
			}
			return connected;
		},
		operationSignal,
		options.limits.connectTimeoutMs,
	);
}

async function receiveWithTimeout(
	connection: DoubaoWebSocketConnection,
	operationSignal: AbortSignal,
	timeoutMs: number,
): Promise<Readonly<DoubaoWebSocketMessage>> {
	return withTimeout((receiveSignal) => connection.receive(receiveSignal), operationSignal, timeoutMs);
}

async function withTimeout<T>(
	operation: (signal: AbortSignal) => PromiseLike<T> | T,
	operationSignal: AbortSignal,
	timeoutMs: number,
): Promise<T> {
	operationSignal.throwIfAborted();
	const controller = new AbortController();
	let timedOut = false;
	const onOperationAbort = () => controller.abort(operationSignal.reason);
	const timer = setTimeout(() => {
		timedOut = true;
		controller.abort(new DoubaoRealtimeSpeechAdapterError("timeout"));
	}, timeoutMs);
	operationSignal.addEventListener("abort", onOperationAbort, { once: true });
	try {
		return await abortable(() => operation(controller.signal), controller.signal);
	} catch (error) {
		operationSignal.throwIfAborted();
		if (timedOut) throw new DoubaoRealtimeSpeechAdapterError("timeout");
		throw error;
	} finally {
		clearTimeout(timer);
		operationSignal.removeEventListener("abort", onOperationAbort);
	}
}

function captureAuthentication(value: unknown, maxAppKeyBytes: number): Readonly<DoubaoAuthentication> {
	if (!value || typeof value !== "object") throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	const type = (value as { readonly type?: unknown }).type;
	if (type === "api-key") {
		return Object.freeze({ type: "api-key" });
	}
	if (type === "legacy") {
		const appKey = (value as { readonly appKey?: unknown }).appKey;
		return Object.freeze({
			type: "legacy",
			appKey: boundedString(appKey, maxAppKeyBytes, "invalid_configuration"),
		});
	}
	throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
}

function captureConfiguration<T>(capture: () => T): T {
	try {
		return capture();
	} catch (error) {
		if (error instanceof DoubaoRealtimeSpeechAdapterError) throw error;
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
}

function captureLimits<T extends DoubaoRealtimeSpeechTransportLimits>(
	value: unknown,
	names: readonly (keyof T)[],
): Readonly<T> {
	if (!value || typeof value !== "object") throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	const captured: Partial<Record<keyof T, number>> = {};
	try {
		for (const name of names) {
			const limit = (value as Readonly<Record<keyof T, unknown>>)[name];
			if (!Number.isSafeInteger(limit) || (limit as number) <= 0) {
				throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
			}
			captured[name] = limit as number;
		}
	} catch (error) {
		if (error instanceof DoubaoRealtimeSpeechAdapterError) throw error;
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	const limits = captured as T;
	const maxInputChunks = (captured as Partial<DoubaoAsrLimits>).maxInputChunks;
	if (
		limits.connectTimeoutMs > MAX_SIGNED_32_BIT_INTEGER ||
		limits.messageTimeoutMs > MAX_SIGNED_32_BIT_INTEGER ||
		limits.operationTimeoutMs > MAX_SIGNED_32_BIT_INTEGER ||
		limits.closeTimeoutMs > MAX_SIGNED_32_BIT_INTEGER ||
		limits.maxFrameBytes > MAX_SIGNED_32_BIT_INTEGER ||
		limits.maxWebSocketFragments > MAX_SIGNED_32_BIT_INTEGER ||
		limits.maxWebSocketBufferedChunks > MAX_SIGNED_32_BIT_INTEGER ||
		(maxInputChunks !== undefined && maxInputChunks > MAX_SIGNED_32_BIT_INTEGER - 1)
	) {
		throw new DoubaoRealtimeSpeechAdapterError("invalid_configuration");
	}
	return Object.freeze(limits);
}

function sameFormat(left: Readonly<RealtimeAudioFormat>, right: Readonly<RealtimeAudioFormat>): boolean {
	return (
		left.mediaType === right.mediaType &&
		left.sampleRateHz === right.sampleRateHz &&
		left.channelCount === right.channelCount
	);
}

function captureAudio(
	value: unknown,
	maxBytes: number,
): Readonly<{ readonly bytes: Uint8Array; readonly byteLength: number }> {
	let bytes: Uint8Array;
	try {
		bytes = (value as { readonly bytes: Uint8Array }).bytes;
		if (typeof TYPED_ARRAY_BYTE_LENGTH_GETTER !== "function") {
			throw new DoubaoRealtimeSpeechAdapterError("protocol");
		}
		const byteLength: unknown = TYPED_ARRAY_BYTE_LENGTH_GETTER.call(bytes);
		if (typeof byteLength !== "number" || !Number.isSafeInteger(byteLength) || byteLength === 0) {
			throw new DoubaoRealtimeSpeechAdapterError("protocol");
		}
		if (byteLength > maxBytes) throw new DoubaoRealtimeSpeechAdapterError("capacity");
		return { bytes, byteLength };
	} catch (error) {
		if (error instanceof DoubaoRealtimeSpeechAdapterError) throw error;
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
}

function copyAudio(captured: Readonly<{ readonly bytes: Uint8Array; readonly byteLength: number }>): Uint8Array {
	try {
		const copy = new Uint8Array(captured.byteLength);
		copy.set(captured.bytes);
		return copy;
	} catch {
		throw new DoubaoRealtimeSpeechAdapterError("protocol");
	}
}

function boundedString(value: unknown, maxBytes: number, code: "invalid_configuration" | "credential"): string {
	if (
		typeof value !== "string" ||
		value.length === 0 ||
		/\p{Cc}/u.test(value) ||
		Buffer.byteLength(value, "utf8") > maxBytes
	) {
		throw new DoubaoRealtimeSpeechAdapterError(code);
	}
	return value;
}

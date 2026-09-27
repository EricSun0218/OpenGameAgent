import { raceOperationWithAbort } from "@opengameagent/protocol";
import type { RealtimeAudioChunk } from "@opengameagent/realtime";
import { LocalSpeechCapacityError, LocalSpeechConfigurationError, LocalSpeechProtocolError } from "./errors.js";
import {
	type CapturedAsyncIterator,
	captureAsyncIterator,
	captureIteratorResultWithAbort,
	closeIterator,
	detachCleanup,
} from "./iterator.js";
import type {
	CapturedVoiceUtterance,
	PcmS16LeAudioFormat,
	RealtimeVoiceActivityDetector,
	RealtimeVoiceActivitySession,
	VoiceActivityCaptureRequest,
	VoiceActivitySessionOpenRequest,
	VoiceActivityUtteranceRequest,
	VoiceActivityUtteranceCaptureOptions,
	VoiceActivityUtteranceCapturer,
	VoiceActivityUtteranceSession,
	VoiceActivityUtteranceSessionFactory,
} from "./types.js";
import { PCM_S16LE_MEDIA_TYPE } from "./types.js";
import {
	addBounded,
	assertNonNegativeSafeInteger,
	assertPositiveSafeInteger,
	captureFormat,
	inspectBytesWithAbort,
	snapshotBytes,
	utf8Bytes,
} from "./validation.js";

interface CapturedFrame {
	readonly bytes: Uint8Array;
}

interface CaptureLimits {
	readonly activationThreshold: number;
	readonly deactivationThreshold: number;
	readonly minSpeechFrames: number;
	readonly endSilenceFrames: number;
	readonly prefixPaddingFrames: number;
	readonly maxCaptureFrames: number;
	readonly maxCaptureBytes: number;
	readonly maxUtteranceFrames: number;
	readonly maxFrameBytes: number;
	readonly maxUtteranceBytes: number;
	readonly maxMediaTypeBytes: number;
}

const SESSION_CLOSED = Object.freeze({ kind: "voice-activity-utterance-session-closed" });

export class VoiceActivityUtteranceCapture
	implements VoiceActivityUtteranceCapturer, VoiceActivityUtteranceSessionFactory
{
	private readonly detector: RealtimeVoiceActivityDetector;
	private readonly open: RealtimeVoiceActivityDetector["open"];
	private readonly limits: CaptureLimits;

	constructor(options: Readonly<VoiceActivityUtteranceCaptureOptions>) {
		if (!options || typeof options !== "object") throw new LocalSpeechConfigurationError();
		let detector: RealtimeVoiceActivityDetector;
		let open: RealtimeVoiceActivityDetector["open"];
		let limits: CaptureLimits;
		try {
			detector = options.detector;
			open = detector?.open;
			limits = {
				activationThreshold: options.activationThreshold,
				deactivationThreshold: options.deactivationThreshold,
				minSpeechFrames: options.minSpeechFrames,
				endSilenceFrames: options.endSilenceFrames,
				prefixPaddingFrames: options.prefixPaddingFrames,
				maxCaptureFrames: options.maxCaptureFrames,
				maxCaptureBytes: options.maxCaptureBytes,
				maxUtteranceFrames: options.maxUtteranceFrames,
				maxFrameBytes: options.maxFrameBytes,
				maxUtteranceBytes: options.maxUtteranceBytes,
				maxMediaTypeBytes: options.maxMediaTypeBytes,
			};
		} catch {
			throw new LocalSpeechConfigurationError();
		}
		if (!detector || typeof open !== "function") throw new LocalSpeechConfigurationError();
		assertProbability(limits.activationThreshold);
		assertProbability(limits.deactivationThreshold);
		if (limits.deactivationThreshold > limits.activationThreshold) throw new LocalSpeechConfigurationError();
		assertPositiveSafeInteger(limits.minSpeechFrames);
		assertPositiveSafeInteger(limits.endSilenceFrames);
		assertNonNegativeSafeInteger(limits.prefixPaddingFrames);
		assertPositiveSafeInteger(limits.maxCaptureFrames);
		assertPositiveSafeInteger(limits.maxCaptureBytes);
		assertPositiveSafeInteger(limits.maxUtteranceFrames);
		assertPositiveSafeInteger(limits.maxFrameBytes);
		assertPositiveSafeInteger(limits.maxUtteranceBytes);
		assertPositiveSafeInteger(limits.maxMediaTypeBytes);
		if (limits.maxMediaTypeBytes < utf8Bytes(PCM_S16LE_MEDIA_TYPE)) throw new LocalSpeechConfigurationError();
		if (limits.minSpeechFrames > limits.maxCaptureFrames || limits.minSpeechFrames > limits.maxUtteranceFrames) {
			throw new LocalSpeechConfigurationError();
		}
		this.detector = detector;
		this.open = open;
		this.limits = Object.freeze(limits);
	}

	async openSession(request: Readonly<VoiceActivitySessionOpenRequest>): Promise<VoiceActivityUtteranceSession> {
		if (!request || typeof request !== "object") throw new LocalSpeechProtocolError();
		let signal: AbortSignal;
		let audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
		let rawFormat: unknown;
		try {
			signal = request.signal;
		} catch {
			throw new LocalSpeechProtocolError();
		}
		if (!(signal instanceof AbortSignal)) throw new LocalSpeechProtocolError();
		signal.throwIfAborted();
		try {
			audio = request.audio;
			rawFormat = request.format;
		} catch {
			signal.throwIfAborted();
			throw new LocalSpeechProtocolError();
		}
		let format: Readonly<PcmS16LeAudioFormat>;
		try {
			format = captureFormat(rawFormat, this.limits.maxMediaTypeBytes) as Readonly<PcmS16LeAudioFormat>;
		} catch (error) {
			signal.throwIfAborted();
			throw error;
		}
		signal.throwIfAborted();
		if (format.mediaType !== PCM_S16LE_MEDIA_TYPE) throw new LocalSpeechProtocolError();
		const bytesPerFrame = 2 * (format.channelCount as number);
		if (!Number.isSafeInteger(bytesPerFrame)) throw new LocalSpeechProtocolError();
		let iterator: CapturedAsyncIterator<Readonly<RealtimeAudioChunk>>;
		try {
			iterator = captureAsyncIterator<Readonly<RealtimeAudioChunk>>(audio);
		} catch (error) {
			signal.throwIfAborted();
			throw error;
		}
		try {
			signal.throwIfAborted();
		} catch (error) {
			closeIterator(iterator);
			throw error;
		}
		let session: RealtimeVoiceActivitySession | undefined;
		let captured: ReturnType<typeof captureSession> | undefined;
		let opening: Promise<RealtimeVoiceActivitySession> | undefined;
		try {
			opening = Promise.resolve(Reflect.apply(this.open, this.detector, [{ format, signal }]));
			session = await raceOperationWithAbort(() => opening as Promise<RealtimeVoiceActivitySession>, signal);
			captured = captureSession(session);
			signal.throwIfAborted();
			return new OpenVoiceActivityUtteranceSession(
				format,
				iterator,
				session,
				captured,
				signal,
				bytesPerFrame,
				this.limits,
			);
		} catch (error) {
			closeIterator(iterator);
			if (session && captured) closeSession(session, captured.close);
			else if (!session && opening) closeLateSession(opening);
			signal.throwIfAborted();
			throw error;
		}
	}

	async capture(request: Readonly<VoiceActivityCaptureRequest>): Promise<Readonly<CapturedVoiceUtterance> | undefined> {
		if (!request || typeof request !== "object") throw new LocalSpeechProtocolError();
		let signal: AbortSignal;
		try {
			signal = request.signal;
		} catch {
			throw new LocalSpeechProtocolError();
		}
		if (!(signal instanceof AbortSignal)) throw new LocalSpeechProtocolError();
		signal.throwIfAborted();
		let audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
		let onSpeechStart: VoiceActivityCaptureRequest["onSpeechStart"];
		let format: VoiceActivityCaptureRequest["format"];
		try {
			audio = request.audio;
			onSpeechStart = request.onSpeechStart;
			format = request.format;
		} catch {
			signal.throwIfAborted();
			throw new LocalSpeechProtocolError();
		}
		if (onSpeechStart !== undefined && typeof onSpeechStart !== "function") {
			throw new LocalSpeechProtocolError();
		}
		const session = await this.openSession({ format, audio, signal });
		try {
			return await session.captureNext(onSpeechStart ? { onSpeechStart } : {});
		} finally {
			session.close();
		}
	}
}

class OpenVoiceActivityUtteranceSession implements VoiceActivityUtteranceSession {
	private closed = false;
	private capturing = false;
	private readonly callerSignal: AbortSignal;
	private readonly lifetime = new AbortController();
	private readonly signal: AbortSignal;

	constructor(
		private readonly format: Readonly<PcmS16LeAudioFormat>,
		private readonly iterator: CapturedAsyncIterator<Readonly<RealtimeAudioChunk>>,
		private readonly session: RealtimeVoiceActivitySession,
		private readonly capturedSession: ReturnType<typeof captureSession>,
		signal: AbortSignal,
		private readonly bytesPerFrame: number,
		private readonly limits: CaptureLimits,
	) {
		this.callerSignal = signal;
		this.signal = AbortSignal.any([signal, this.lifetime.signal]);
	}

	async captureNext(
		request: Readonly<VoiceActivityUtteranceRequest> = {},
	): Promise<Readonly<CapturedVoiceUtterance> | undefined> {
		if (this.closed) return undefined;
		if (this.capturing || !request || typeof request !== "object") throw new LocalSpeechProtocolError();
		let onSpeechStart: VoiceActivityUtteranceRequest["onSpeechStart"];
		try {
			onSpeechStart = request.onSpeechStart;
		} catch {
			throw new LocalSpeechProtocolError();
		}
		if (onSpeechStart !== undefined && typeof onSpeechStart !== "function") {
			throw new LocalSpeechProtocolError();
		}
		this.signal.throwIfAborted();
		this.capturing = true;
		const prefix: CapturedFrame[] = [];
		const candidate: CapturedFrame[] = [];
		const utterance: CapturedFrame[] = [];
		let captureFrames = 0;
		let captureBytes = 0;
		let utteranceBytes = 0;
		let active = false;
		let silenceFrames = 0;
		try {
			while (true) {
				const result = captureIteratorResultWithAbort<Readonly<RealtimeAudioChunk>>(
					await raceOperationWithAbort(() => this.iterator.next(), this.signal),
					this.signal,
				);
				if (result.done) {
					this.close();
					return active ? makeUtterance(this.format, utterance, utteranceBytes) : undefined;
				}
				captureFrames += 1;
				if (captureFrames > this.limits.maxCaptureFrames) throw new LocalSpeechCapacityError();
				const view = inspectBytesWithAbort(result.value, this.signal);
				if (view.byteLength === 0 || view.byteLength > this.limits.maxFrameBytes) {
					throw view.byteLength > this.limits.maxFrameBytes
						? new LocalSpeechCapacityError()
						: new LocalSpeechProtocolError();
				}
				if (view.byteLength % this.bytesPerFrame !== 0) throw new LocalSpeechProtocolError();
				captureBytes = addBounded(captureBytes, view.byteLength, this.limits.maxCaptureBytes);
				const frame = Object.freeze({ bytes: snapshotBytes(view) });
				const detectorFrame = Object.freeze({ bytes: snapshotBytes(view) });
				const probability = await raceOperationWithAbort(
					() =>
						Reflect.apply(this.capturedSession.detect, this.session, [{ chunk: detectorFrame, signal: this.signal }]),
					this.signal,
				);
				this.signal.throwIfAborted();
				if (typeof probability !== "number" || !Number.isFinite(probability) || probability < 0 || probability > 1) {
					throw new LocalSpeechProtocolError();
				}

				if (!active) {
					if (probability >= this.limits.activationThreshold) {
						candidate.push(frame);
						if (candidate.length < this.limits.minSpeechFrames) continue;
						active = true;
						for (const buffered of prefix) {
							utteranceBytes = appendFrame(utterance, buffered, utteranceBytes, this.limits);
						}
						for (const buffered of candidate) {
							utteranceBytes = appendFrame(utterance, buffered, utteranceBytes, this.limits);
						}
						candidate.length = 0;
						if (onSpeechStart) await raceOperationWithAbort(() => onSpeechStart?.(), this.signal);
						continue;
					}
					for (const buffered of candidate) pushPrefix(prefix, buffered, this.limits.prefixPaddingFrames);
					candidate.length = 0;
					pushPrefix(prefix, frame, this.limits.prefixPaddingFrames);
					continue;
				}

				utteranceBytes = appendFrame(utterance, frame, utteranceBytes, this.limits);
				if (probability < this.limits.deactivationThreshold) {
					silenceFrames += 1;
					if (silenceFrames >= this.limits.endSilenceFrames) {
						return makeUtterance(this.format, utterance, utteranceBytes);
					}
				} else {
					silenceFrames = 0;
				}
			}
		} catch (error) {
			const stopped = this.lifetime.signal.aborted;
			this.close();
			this.callerSignal.throwIfAborted();
			if (stopped) return undefined;
			throw error;
		} finally {
			this.capturing = false;
		}
	}

	close(): void {
		if (this.closed) return;
		this.closed = true;
		this.lifetime.abort(SESSION_CLOSED);
		closeIterator(this.iterator);
		closeSession(this.session, this.capturedSession.close);
	}
}

function captureSession(session: unknown): {
	readonly detect: RealtimeVoiceActivitySession["detect"];
	readonly close: RealtimeVoiceActivitySession["close"];
} {
	if (!session || typeof session !== "object") throw new LocalSpeechProtocolError();
	let close: unknown;
	try {
		close = (session as RealtimeVoiceActivitySession).close;
		const detect = (session as RealtimeVoiceActivitySession).detect;
		if (typeof detect !== "function" || typeof close !== "function") throw new LocalSpeechProtocolError();
		const closeMethod = close;
		return Object.freeze({
			detect,
			close: closeMethod as RealtimeVoiceActivitySession["close"],
		});
	} catch (error) {
		if (typeof close === "function") {
			const closeMethod = close;
			detachCleanup(() => Reflect.apply(closeMethod, session, []));
		}
		if (error instanceof LocalSpeechProtocolError) throw error;
		throw new LocalSpeechProtocolError();
	}
}

function closeSession(session: object, close: RealtimeVoiceActivitySession["close"]): void {
	detachCleanup(() => Reflect.apply(close, session, []));
}

function closeLateSession(opening: Promise<RealtimeVoiceActivitySession>): void {
	void opening.then(
		(session) => {
			try {
				const captured = captureSession(session);
				closeSession(session, captured.close);
			} catch {
				// Invalid late sessions are handled by captureSession when a close hook exists.
			}
		},
		() => undefined,
	);
}

function assertProbability(value: number): void {
	if (typeof value !== "number" || !Number.isFinite(value) || Object.is(value, -0) || value < 0 || value > 1) {
		throw new LocalSpeechConfigurationError();
	}
}

function pushPrefix(prefix: CapturedFrame[], frame: CapturedFrame, maximum: number): void {
	if (maximum === 0) return;
	prefix.push(frame);
	if (prefix.length > maximum) prefix.shift();
}

function appendFrame(frames: CapturedFrame[], frame: CapturedFrame, bytes: number, limits: CaptureLimits): number {
	if (frames.length >= limits.maxUtteranceFrames) throw new LocalSpeechCapacityError();
	frames.push(frame);
	return addBounded(bytes, frame.bytes.byteLength, limits.maxUtteranceBytes);
}

function makeUtterance(
	format: Readonly<CapturedVoiceUtterance["format"]>,
	frames: readonly CapturedFrame[],
	byteLength: number,
): Readonly<CapturedVoiceUtterance> {
	const captured = frames.slice();
	return Object.freeze({
		format,
		frameCount: captured.length,
		byteLength,
		audio: Object.freeze({
			async *[Symbol.asyncIterator]() {
				for (const frame of captured) yield Object.freeze({ bytes: frame.bytes.slice() });
			},
		}),
	});
}

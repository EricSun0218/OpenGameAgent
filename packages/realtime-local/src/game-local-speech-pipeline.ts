import { raceOperationWithAbort } from "@opengameagent/protocol";
import type {
	RealtimeAudioChunk,
	RealtimeVoiceCoordinate,
	RealtimeVoiceInterruptResult,
	RealtimeVoiceTurnRequest,
} from "@opengameagent/realtime";
import { LocalSpeechBargeInError, LocalSpeechConfigurationError, LocalSpeechProtocolError } from "./errors.js";
import { type CapturedOwnedAsyncIterable, captureOwnedAsyncIterable } from "./iterator.js";
import type {
	CapturedVoiceUtterance,
	GameLocalSpeechPipelineOptions,
	GameLocalSpeechStartResult,
	GameLocalSpeechTurnRequest,
	LocalSpeechAudioFormat,
	PcmS16LeAudioFormat,
	RealtimeVoiceServicePort,
	VoiceActivityUtteranceCapturer,
} from "./types.js";
import {
	captureAudioFormat,
	capturePcmS16LeFormat,
	captureString,
	isPositiveSafeInteger,
	sameFormat,
} from "./validation.js";

export class GameLocalSpeechPipeline {
	private readonly voice: RealtimeVoiceServicePort;
	private readonly startVoice: RealtimeVoiceServicePort["start"];
	private readonly interruptVoice: RealtimeVoiceServicePort["interrupt"];
	private readonly capturer: VoiceActivityUtteranceCapturer;
	private readonly captureUtterance: VoiceActivityUtteranceCapturer["capture"];

	constructor(options: Readonly<GameLocalSpeechPipelineOptions>) {
		if (!options || typeof options !== "object") throw new LocalSpeechConfigurationError();
		try {
			this.voice = options.voice;
			this.startVoice = this.voice?.start;
			this.interruptVoice = this.voice?.interrupt;
			this.capturer = options.capture;
			this.captureUtterance = this.capturer?.capture;
		} catch {
			throw new LocalSpeechConfigurationError();
		}
		if (
			!this.voice ||
			typeof this.startVoice !== "function" ||
			typeof this.interruptVoice !== "function" ||
			!this.capturer ||
			typeof this.captureUtterance !== "function"
		) {
			throw new LocalSpeechConfigurationError();
		}
	}

	async startWhenSpeechDetected(
		request: Readonly<GameLocalSpeechTurnRequest>,
	): Promise<Readonly<GameLocalSpeechStartResult>> {
		if (!request || typeof request !== "object") throw new LocalSpeechProtocolError();
		let coordinate: GameLocalSpeechTurnRequest["coordinate"];
		let inputFormat: GameLocalSpeechTurnRequest["inputFormat"];
		let outputFormat: GameLocalSpeechTurnRequest["outputFormat"];
		let voiceProfileId: string;
		let audio: GameLocalSpeechTurnRequest["audio"];
		let signal: AbortSignal | undefined;
		let interrupt: GameLocalSpeechTurnRequest["interrupt"];
		try {
			signal = request.signal;
		} catch {
			throw new LocalSpeechProtocolError();
		}
		if (signal !== undefined && !(signal instanceof AbortSignal)) throw new LocalSpeechProtocolError();
		const operationSignal = signal ?? new AbortController().signal;
		operationSignal.throwIfAborted();
		try {
			coordinate = request.coordinate;
			inputFormat = request.inputFormat;
			outputFormat = request.outputFormat;
			voiceProfileId = request.voiceProfileId;
			audio = request.audio;
			interrupt = request.interrupt;
		} catch {
			operationSignal.throwIfAborted();
			throw new LocalSpeechProtocolError();
		}
		operationSignal.throwIfAborted();
		let capturedCoordinate: Readonly<RealtimeVoiceCoordinate>;
		try {
			capturedCoordinate = captureCoordinate(coordinate);
		} catch (error) {
			operationSignal.throwIfAborted();
			throw error;
		}
		operationSignal.throwIfAborted();
		let capturedInputFormat: Readonly<PcmS16LeAudioFormat>;
		try {
			capturedInputFormat = capturePcmS16LeFormat(inputFormat);
		} catch (error) {
			operationSignal.throwIfAborted();
			throw error;
		}
		operationSignal.throwIfAborted();
		let capturedOutputFormat: Readonly<LocalSpeechAudioFormat>;
		try {
			capturedOutputFormat = captureAudioFormat(outputFormat);
		} catch (error) {
			operationSignal.throwIfAborted();
			throw error;
		}
		operationSignal.throwIfAborted();
		try {
			voiceProfileId = captureString(voiceProfileId);
		} catch (error) {
			operationSignal.throwIfAborted();
			throw error;
		}
		operationSignal.throwIfAborted();
		let interruptResult: Readonly<RealtimeVoiceInterruptResult> | undefined;
		let interruptOutcome: Promise<InterruptOutcome> | undefined;
		let expectedCoordinate: GameLocalSpeechTurnRequest["coordinate"] | undefined;
		let interruptReason: unknown;
		if (interrupt !== undefined) {
			if (!interrupt || typeof interrupt !== "object") throw new LocalSpeechProtocolError();
			try {
				expectedCoordinate = captureCoordinate(interrupt.coordinate);
				interruptReason = interrupt.reason;
			} catch {
				operationSignal.throwIfAborted();
				throw new LocalSpeechProtocolError();
			}
			operationSignal.throwIfAborted();
			if (!sameVoiceChannel(capturedCoordinate, expectedCoordinate)) throw new LocalSpeechProtocolError();
		}
		let capturePending = true;
		let rawUtterance: unknown;
		let captureRequest: Promise<unknown> | undefined;
		try {
			operationSignal.throwIfAborted();
			captureRequest = Promise.resolve(
				Reflect.apply(this.captureUtterance, this.capturer, [
					{
						format: capturedInputFormat,
						audio,
						signal: operationSignal,
						...(expectedCoordinate
							? {
									onSpeechStart: () => {
										if (!capturePending || operationSignal.aborted || interruptOutcome) return;
										interruptOutcome = settleInterrupt(() =>
											Reflect.apply(this.interruptVoice, this.voice, [expectedCoordinate, interruptReason]),
										);
									},
								}
							: {}),
					},
				]),
			);
			rawUtterance = await raceOperationWithAbort(() => captureRequest as Promise<unknown>, operationSignal);
			operationSignal.throwIfAborted();
		} catch (error) {
			if (captureRequest) closeLateUtterance(captureRequest);
			operationSignal.throwIfAborted();
			throw error;
		} finally {
			capturePending = false;
		}
		if (rawUtterance === undefined) {
			if (interruptOutcome) throw new LocalSpeechProtocolError();
			return Object.freeze({ status: "no-speech" });
		}
		let utterance: ReturnType<typeof captureUtterance>;
		try {
			utterance = captureUtterance(rawUtterance);
		} catch (error) {
			operationSignal.throwIfAborted();
			throw error;
		}
		const ownedAudio = utterance.ownedAudio;
		let transferred = false;
		try {
			operationSignal.throwIfAborted();
			if (!sameFormat(utterance.format, capturedInputFormat)) throw new LocalSpeechProtocolError();
			if (expectedCoordinate) {
				if (!interruptOutcome) throw new LocalSpeechProtocolError();
				const outcome = await raceOperationWithAbort(
					() => interruptOutcome as Promise<InterruptOutcome>,
					operationSignal,
				);
				operationSignal.throwIfAborted();
				if (outcome.status === "rejected") throw outcome.reason;
				try {
					interruptResult = captureInterruptResult(outcome.value, expectedCoordinate);
				} catch (error) {
					operationSignal.throwIfAborted();
					throw error;
				}
				operationSignal.throwIfAborted();
				if (!interruptResult.accepted && interruptResult.reason === "coordinate-mismatch") {
					throw new LocalSpeechBargeInError();
				}
			}
			operationSignal.throwIfAborted();
			const voiceRequest: RealtimeVoiceTurnRequest = {
				coordinate: capturedCoordinate,
				inputFormat: capturedInputFormat,
				outputFormat: capturedOutputFormat,
				voiceProfileId,
				audio: ownedAudio.iterable,
				...(signal ? { signal } : {}),
			};
			let turn: ReturnType<RealtimeVoiceServicePort["start"]>;
			try {
				turn = Reflect.apply(this.startVoice, this.voice, [voiceRequest]);
			} catch (error) {
				operationSignal.throwIfAborted();
				throw error;
			}
			transferred = true;
			return Object.freeze({
				status: "started",
				coordinate: capturedCoordinate,
				turn,
				...(interruptResult ? { interrupt: interruptResult } : {}),
			});
		} finally {
			if (!transferred) ownedAudio.close();
		}
	}
}

type InterruptOutcome =
	| { readonly status: "fulfilled"; readonly value: unknown }
	| { readonly status: "rejected"; readonly reason: unknown };

function settleInterrupt(operation: () => PromiseLike<unknown> | unknown): Promise<InterruptOutcome> {
	try {
		return Promise.resolve(operation()).then(
			(value) => Object.freeze({ status: "fulfilled" as const, value }),
			(reason: unknown) => Object.freeze({ status: "rejected" as const, reason }),
		);
	} catch (reason) {
		return Promise.resolve(Object.freeze({ status: "rejected" as const, reason }));
	}
}

function closeLateUtterance(captureRequest: Promise<unknown>): void {
	void captureRequest.then(
		(value) => {
			if (value === undefined) return;
			try {
				captureUtterance(value).ownedAudio.close();
			} catch {
				// captureUtterance owns and closes any usable audio before rejecting malformed metadata.
			}
		},
		() => undefined,
	);
}

function captureInterruptResult(
	value: unknown,
	expected: Readonly<RealtimeVoiceCoordinate>,
): Readonly<RealtimeVoiceInterruptResult> {
	if (!value || typeof value !== "object") throw new LocalSpeechProtocolError();
	let accepted: unknown;
	let reason: unknown;
	let coordinate: unknown;
	let activeCoordinate: unknown;
	try {
		accepted = (value as RealtimeVoiceInterruptResult).accepted;
		if (accepted === true) coordinate = (value as Extract<RealtimeVoiceInterruptResult, { accepted: true }>).coordinate;
		if (accepted === false) {
			reason = (value as Extract<RealtimeVoiceInterruptResult, { accepted: false }>).reason;
			if (reason === "coordinate-mismatch") {
				activeCoordinate = (
					value as Extract<RealtimeVoiceInterruptResult, { accepted: false; reason: "coordinate-mismatch" }>
				).activeCoordinate;
			}
		}
	} catch {
		throw new LocalSpeechProtocolError();
	}
	if (accepted === true) {
		const captured = captureCoordinate(coordinate);
		if (!sameVoiceCoordinate(captured, expected)) throw new LocalSpeechProtocolError();
		return Object.freeze({ accepted: true, coordinate: captured });
	}
	if (accepted === false && reason === "no-active-turn") {
		return Object.freeze({ accepted: false, reason: "no-active-turn" });
	}
	if (accepted === false && reason === "coordinate-mismatch") {
		return Object.freeze({
			accepted: false,
			reason: "coordinate-mismatch",
			activeCoordinate: captureCoordinate(activeCoordinate),
		});
	}
	throw new LocalSpeechProtocolError();
}

function captureCoordinate(value: unknown): Readonly<RealtimeVoiceCoordinate> {
	if (!value || typeof value !== "object") throw new LocalSpeechProtocolError();
	let sessionId: unknown;
	let actorId: unknown;
	let voiceSessionId: unknown;
	let epoch: unknown;
	let utteranceId: unknown;
	try {
		const coordinate = value as RealtimeVoiceCoordinate;
		sessionId = coordinate.sessionId;
		actorId = coordinate.actorId;
		voiceSessionId = coordinate.voiceSessionId;
		epoch = coordinate.epoch;
		utteranceId = coordinate.utteranceId;
	} catch {
		throw new LocalSpeechProtocolError();
	}
	if (
		!isNonEmptyString(sessionId) ||
		!isNonEmptyString(actorId) ||
		!isNonEmptyString(voiceSessionId) ||
		!isNonEmptyString(epoch) ||
		!isNonEmptyString(utteranceId)
	) {
		throw new LocalSpeechProtocolError();
	}
	return Object.freeze({ sessionId, actorId, voiceSessionId, epoch, utteranceId });
}

function isNonEmptyString(value: unknown): value is string {
	return typeof value === "string" && value.length > 0;
}

function sameVoiceChannel(left: Readonly<RealtimeVoiceCoordinate>, right: Readonly<RealtimeVoiceCoordinate>): boolean {
	return (
		left.sessionId === right.sessionId && left.actorId === right.actorId && left.voiceSessionId === right.voiceSessionId
	);
}

function sameVoiceCoordinate(
	left: Readonly<RealtimeVoiceCoordinate>,
	right: Readonly<RealtimeVoiceCoordinate>,
): boolean {
	return sameVoiceChannel(left, right) && left.epoch === right.epoch && left.utteranceId === right.utteranceId;
}

function captureUtterance(value: unknown): {
	readonly format: Readonly<PcmS16LeAudioFormat>;
	readonly ownedAudio: Readonly<CapturedOwnedAsyncIterable<Readonly<RealtimeAudioChunk>>>;
} {
	if (!value || typeof value !== "object") throw new LocalSpeechProtocolError();
	let format: unknown;
	let audio: unknown;
	let frameCount: unknown;
	let byteLength: unknown;
	try {
		const utterance = value as CapturedVoiceUtterance;
		audio = utterance.audio;
	} catch {
		throw new LocalSpeechProtocolError();
	}
	const ownedAudio = captureOwnedAsyncIterable<Readonly<RealtimeAudioChunk>>(audio);
	let captured = false;
	try {
		try {
			const utterance = value as CapturedVoiceUtterance;
			format = utterance.format;
			frameCount = utterance.frameCount;
			byteLength = utterance.byteLength;
		} catch {
			throw new LocalSpeechProtocolError();
		}
		if (!isPositiveSafeInteger(frameCount) || !isPositiveSafeInteger(byteLength)) {
			throw new LocalSpeechProtocolError();
		}
		const result = Object.freeze({
			format: capturePcmS16LeFormat(format),
			ownedAudio,
		});
		captured = true;
		return result;
	} finally {
		if (!captured) ownedAudio.close();
	}
}

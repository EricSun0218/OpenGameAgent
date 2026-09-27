import { BoundedAsyncQueue } from "@opengameagent/runtime";
import { raceOperationWithAbort } from "@opengameagent/protocol";
import type { RealtimeAudioChunk } from "@opengameagent/realtime";
import { LocalSpeechCapacityError, LocalSpeechConfigurationError, LocalSpeechProtocolError } from "./errors.js";
import { GameLocalSpeechPipeline } from "./game-local-speech-pipeline.js";
import { captureOwnedAsyncIterable, detachCleanup } from "./iterator.js";
import type {
	GameContinuousLocalSpeechHandle,
	GameContinuousLocalSpeechResult,
	GameContinuousLocalSpeechSessionOptions,
	GameContinuousLocalSpeechStartRequest,
	GameContinuousLocalSpeechTurn,
	RealtimeVoiceServicePort,
	VoiceActivityUtteranceSession,
	VoiceActivityUtteranceSessionFactory,
} from "./types.js";
import { assertPositiveSafeInteger, captureAudioFormat, capturePcmS16LeFormat, captureString } from "./validation.js";

const STOPPED = Object.freeze({ kind: "continuous-local-speech-stopped" });

export class GameContinuousLocalSpeechSession {
	private readonly voice: RealtimeVoiceServicePort;
	private readonly interruptVoice: RealtimeVoiceServicePort["interrupt"];
	private readonly endpointing: VoiceActivityUtteranceSessionFactory;
	private readonly openEndpointing: VoiceActivityUtteranceSessionFactory["openSession"];
	private readonly maxPendingTurns: number;
	private started = false;

	constructor(options: Readonly<GameContinuousLocalSpeechSessionOptions>) {
		if (!options || typeof options !== "object") throw new LocalSpeechConfigurationError();
		try {
			this.voice = options.voice;
			this.interruptVoice = this.voice?.interrupt;
			this.endpointing = options.endpointing;
			this.openEndpointing = this.endpointing?.openSession;
			this.maxPendingTurns = options.maxPendingTurns;
		} catch {
			throw new LocalSpeechConfigurationError();
		}
		if (
			!this.voice ||
			typeof this.voice.start !== "function" ||
			typeof this.interruptVoice !== "function" ||
			!this.endpointing ||
			typeof this.openEndpointing !== "function"
		) {
			throw new LocalSpeechConfigurationError();
		}
		assertPositiveSafeInteger(this.maxPendingTurns);
	}

	start(request: Readonly<GameContinuousLocalSpeechStartRequest>): Readonly<GameContinuousLocalSpeechHandle> {
		if (this.started) throw new LocalSpeechProtocolError();
		if (!request || typeof request !== "object") throw new LocalSpeechProtocolError();
		let inputFormat: GameContinuousLocalSpeechStartRequest["inputFormat"];
		let outputFormat: GameContinuousLocalSpeechStartRequest["outputFormat"];
		let voiceProfileId: string;
		let audio: GameContinuousLocalSpeechStartRequest["audio"];
		let createCoordinate: GameContinuousLocalSpeechStartRequest["createCoordinate"];
		let callerSignal: AbortSignal | undefined;
		try {
			callerSignal = request.signal;
		} catch {
			throw new LocalSpeechProtocolError();
		}
		if (callerSignal !== undefined && !(callerSignal instanceof AbortSignal)) throw new LocalSpeechProtocolError();
		const requestSignal = callerSignal ?? new AbortController().signal;
		requestSignal.throwIfAborted();
		try {
			inputFormat = request.inputFormat;
			outputFormat = request.outputFormat;
			voiceProfileId = request.voiceProfileId;
			audio = request.audio;
			createCoordinate = request.createCoordinate;
		} catch {
			requestSignal.throwIfAborted();
			throw new LocalSpeechProtocolError();
		}
		if (typeof createCoordinate !== "function") throw new LocalSpeechProtocolError();
		try {
			inputFormat = capturePcmS16LeFormat(inputFormat);
			outputFormat = captureAudioFormat(outputFormat);
			voiceProfileId = captureString(voiceProfileId);
		} catch (error) {
			requestSignal.throwIfAborted();
			throw error;
		}
		const ownedAudio = captureOwnedAsyncIterable<Readonly<RealtimeAudioChunk>>(audio);
		try {
			requestSignal.throwIfAborted();
		} catch (error) {
			ownedAudio.close();
			throw error;
		}
		this.started = true;

		const controller = new AbortController();
		const signal = callerSignal ? AbortSignal.any([callerSignal, controller.signal]) : controller.signal;
		const turns = new BoundedAsyncQueue<Readonly<GameContinuousLocalSpeechTurn>>(this.maxPendingTurns);
		let stopReason: unknown;
		let settled = false;
		const stop = (reason?: unknown) => {
			if (controller.signal.aborted || settled) return;
			stopReason = reason;
			controller.abort(STOPPED);
		};
		const result = this.run(
			{ inputFormat, outputFormat, voiceProfileId, audio: ownedAudio.iterable, createCoordinate },
			callerSignal,
			signal,
			controller,
			turns,
			() => stopReason,
		).finally(() => {
			ownedAudio.close();
			settled = true;
			turns.close();
		});
		void result.catch(() => undefined);

		return Object.freeze({
			turns: queueIterable(turns, () => stop()),
			result,
			stop,
		});
	}

	private async run(
		request: Omit<GameContinuousLocalSpeechStartRequest, "signal">,
		callerSignal: AbortSignal | undefined,
		signal: AbortSignal,
		controller: AbortController,
		turns: BoundedAsyncQueue<Readonly<GameContinuousLocalSpeechTurn>>,
		stopReason: () => unknown,
	): Promise<Readonly<GameContinuousLocalSpeechResult>> {
		let endpointSession: VoiceActivityUtteranceSession | undefined;
		let opening: Promise<unknown> | undefined;
		let activeCoordinate: Readonly<GameContinuousLocalSpeechTurn["coordinate"]> | undefined;
		let terminalReason: unknown;
		try {
			signal.throwIfAborted();
			opening = Promise.resolve(
				Reflect.apply(this.openEndpointing, this.endpointing, [
					{ format: request.inputFormat, audio: request.audio, signal },
				]),
			);
			endpointSession = captureEndpointSession(await raceOperationWithAbort(() => opening as Promise<unknown>, signal));
			opening = undefined;
			const pipeline = new GameLocalSpeechPipeline({
				voice: this.voice,
				capture: {
					capture: (captureRequest) =>
						(endpointSession as VoiceActivityUtteranceSession).captureNext({
							...(captureRequest.onSpeechStart ? { onSpeechStart: captureRequest.onSpeechStart } : {}),
						}),
				},
			});
			for (let utteranceIndex = 1; ; utteranceIndex += 1) {
				if (!Number.isSafeInteger(utteranceIndex)) throw new LocalSpeechCapacityError();
				signal.throwIfAborted();
				const coordinate = Reflect.apply(request.createCoordinate, undefined, [utteranceIndex]);
				const started = await pipeline.startWhenSpeechDetected({
					coordinate,
					inputFormat: request.inputFormat,
					outputFormat: request.outputFormat,
					voiceProfileId: request.voiceProfileId,
					audio: request.audio,
					signal,
					...(activeCoordinate ? { interrupt: { coordinate: activeCoordinate } } : {}),
				});
				if (started.status === "no-speech") return Object.freeze({ status: "source-ended" });
				activeCoordinate = started.coordinate;
				if (
					!turns.push(
						Object.freeze({
							coordinate: started.coordinate,
							turn: started.turn,
							...(started.interrupt ? { interrupt: started.interrupt } : {}),
						}),
					)
				) {
					throw new LocalSpeechCapacityError();
				}
			}
		} catch (error) {
			terminalReason = error;
			callerSignal?.throwIfAborted();
			if (controller.signal.aborted) return Object.freeze({ status: "stopped" });
			throw error;
		} finally {
			if (opening) closeLateEndpointSession(opening);
			endpointSession?.close();
			if (activeCoordinate) {
				try {
					void Promise.resolve(
						Reflect.apply(this.interruptVoice, this.voice, [
							activeCoordinate,
							controller.signal.aborted ? stopReason() : terminalReason,
						]),
					).catch(() => undefined);
				} catch {
					// Exact cleanup cannot replace the session's primary result.
				}
			}
		}
	}
}

function captureEndpointSession(value: unknown): VoiceActivityUtteranceSession {
	if (!value || typeof value !== "object") throw new LocalSpeechProtocolError();
	let captureNext: unknown;
	let close: unknown;
	try {
		captureNext = (value as VoiceActivityUtteranceSession).captureNext;
		close = (value as VoiceActivityUtteranceSession).close;
	} catch {
		throw new LocalSpeechProtocolError();
	}
	if (typeof captureNext !== "function" || typeof close !== "function") {
		if (typeof close === "function") detachCleanup(() => Reflect.apply(close, value, []));
		throw new LocalSpeechProtocolError();
	}
	let closed = false;
	return Object.freeze({
		captureNext: (request?: Parameters<VoiceActivityUtteranceSession["captureNext"]>[0]) =>
			closed
				? Promise.resolve(undefined)
				: Promise.resolve(Reflect.apply(captureNext as VoiceActivityUtteranceSession["captureNext"], value, [request])),
		close: () => {
			if (closed) return;
			closed = true;
			detachCleanup(() => Reflect.apply(close as VoiceActivityUtteranceSession["close"], value, []));
		},
	});
}

function closeLateEndpointSession(opening: Promise<unknown>): void {
	void opening.then(
		(value) => {
			try {
				captureEndpointSession(value).close();
			} catch {
				// Invalid late sessions are closed when they expose a usable close hook.
			}
		},
		() => undefined,
	);
}

function queueIterable<T>(queue: BoundedAsyncQueue<T>, stop: () => void): AsyncIterable<T> {
	return Object.freeze({
		async *[Symbol.asyncIterator]() {
			try {
				while (true) {
					const value = await queue.next();
					if (value === undefined) return;
					yield value;
				}
			} finally {
				stop();
			}
		},
	});
}

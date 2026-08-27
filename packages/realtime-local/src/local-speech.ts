import { randomUUID } from "node:crypto";
import type {
	RealtimeAudioFrame,
	RealtimeBehaviorResult,
	RealtimeConversationEvent,
	RealtimeConversationOptions,
	RealtimeHandoffPhase,
	RealtimeTextRole,
	RealtimeTransport,
	RealtimeTransportSession,
} from "@opengameagent/realtime";

export interface LocalSpeechRecognitionRequest {
	pcm16: Uint8Array;
	sampleRate: number;
	channels: number;
	language?: string;
}

export interface LocalSpeechRecognitionResult {
	text: string;
}

export interface LocalSpeechRecognizer {
	transcribe(request: LocalSpeechRecognitionRequest, signal?: AbortSignal): Promise<LocalSpeechRecognitionResult>;
}

export interface LocalSpeechSynthesisRequest {
	text: string;
	voice: string;
	itemId: string;
}

export interface LocalSpeechSynthesizer {
	synthesize(request: LocalSpeechSynthesisRequest, signal?: AbortSignal): AsyncIterable<RealtimeAudioFrame>;
}

export interface LocalVoiceActivityDetector {
	isSpeech(frame: RealtimeAudioFrame, signal?: AbortSignal): boolean | Promise<boolean>;
}

export interface EnergyVoiceActivityDetectorOptions {
	minimumRootMeanSquare?: number;
}

export class EnergyVoiceActivityDetector implements LocalVoiceActivityDetector {
	private readonly threshold: number;
	constructor(options: EnergyVoiceActivityDetectorOptions = {}) {
		this.threshold = options.minimumRootMeanSquare ?? 0.015;
		if (!Number.isFinite(this.threshold) || this.threshold <= 0 || this.threshold >= 1)
			throw new RangeError("The energy VAD threshold is invalid.");
	}
	isSpeech(frame: RealtimeAudioFrame): boolean {
		if (frame.pcm16.byteLength < 2 || frame.pcm16.byteLength % 2 !== 0) throw new TypeError("VAD input is not PCM16.");
		const view = new DataView(frame.pcm16.buffer, frame.pcm16.byteOffset, frame.pcm16.byteLength);
		let sum = 0;
		for (let offset = 0; offset < frame.pcm16.byteLength; offset += 2) {
			const sample = view.getInt16(offset, true) / 32_768;
			sum += sample * sample;
		}
		return Math.sqrt(sum / (frame.pcm16.byteLength / 2)) >= this.threshold;
	}
}

export interface ComposableLocalSpeechTransportOptions {
	recognizer: LocalSpeechRecognizer;
	synthesizer: LocalSpeechSynthesizer;
	voiceActivityDetector?: LocalVoiceActivityDetector;
	language?: string;
	maximumBufferedInputBytes?: number;
	maximumTextCharacters?: number;
	maximumQueuedEvents?: number;
	silenceFramesToEnd?: number;
	minimumSpeechFrames?: number;
}

interface CheckedOptions {
	recognizer: LocalSpeechRecognizer;
	synthesizer: LocalSpeechSynthesizer;
	voiceActivityDetector: LocalVoiceActivityDetector;
	language?: string;
	maximumBufferedInputBytes: number;
	maximumTextCharacters: number;
	maximumQueuedEvents: number;
	silenceFramesToEnd: number;
	minimumSpeechFrames: number;
}

function bounded(value: number | undefined, fallback: number, maximum: number, name: string): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < 1 || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

function checked(options: ComposableLocalSpeechTransportOptions): CheckedOptions {
	if (!options.recognizer || !options.synthesizer) throw new TypeError("Local speech providers are required.");
	if (
		options.language !== undefined &&
		(!options.language || options.language.length > 64 || /[\0\r\n]/u.test(options.language))
	)
		throw new TypeError("Local speech language is invalid.");
	return {
		recognizer: options.recognizer,
		synthesizer: options.synthesizer,
		voiceActivityDetector: options.voiceActivityDetector ?? new EnergyVoiceActivityDetector(),
		...(options.language ? { language: options.language } : {}),
		maximumBufferedInputBytes: bounded(
			options.maximumBufferedInputBytes,
			16 * 1024 * 1024,
			256 * 1024 * 1024,
			"maximumBufferedInputBytes",
		),
		maximumTextCharacters: bounded(options.maximumTextCharacters, 65_536, 1_000_000, "maximumTextCharacters"),
		maximumQueuedEvents: bounded(options.maximumQueuedEvents, 1_024, 65_536, "maximumQueuedEvents"),
		silenceFramesToEnd: bounded(options.silenceFramesToEnd, 3, 1_000, "silenceFramesToEnd"),
		minimumSpeechFrames: bounded(options.minimumSpeechFrames, 1, 1_000, "minimumSpeechFrames"),
	};
}

class EventQueue {
	private readonly events: RealtimeConversationEvent[] = [];
	private readonly waiters: Array<() => void> = [];
	private closed = false;
	constructor(private readonly maximum: number) {}
	push(event: RealtimeConversationEvent): void {
		if (this.closed) return;
		if (this.events.length >= this.maximum) throw new Error("Local realtime event queue capacity was exceeded.");
		this.events.push(event);
		for (const wake of this.waiters.splice(0)) wake();
	}
	close(): void {
		this.closed = true;
		for (const wake of this.waiters.splice(0)) wake();
	}
	async *read(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent> {
		for (;;) {
			const event = this.events.shift();
			if (event) {
				yield event;
				continue;
			}
			if (this.closed || signal?.aborted) return;
			await new Promise<void>((resolve) => {
				this.waiters.push(resolve);
				signal?.addEventListener("abort", () => resolve(), { once: true });
			});
		}
	}
}

class ComposableLocalSpeechSession implements RealtimeTransportSession {
	readonly features = new Set([
		"audio-input",
		"input-transcription",
		"audio-output",
		"output-transcription",
		"speech-boundaries",
		"response-cancellation",
		"handoff",
	] as const);
	private readonly queue: EventQueue;
	private readonly lifetime = new AbortController();
	private inputFrames: Uint8Array[] = [];
	private inputBytes = 0;
	private inputSampleRate = 0;
	private inputChannels = 0;
	private speechFrames = 0;
	private silenceFrames = 0;
	private speaking = false;
	private inputSequence = 0;
	private ttsController: AbortController | undefined;
	private ttsTask: Promise<void> | undefined;
	private closePromise?: Promise<void>;
	private inputGate: Promise<void> = Promise.resolve();

	constructor(
		private readonly conversation: RealtimeConversationOptions,
		private readonly options: CheckedOptions,
	) {
		this.queue = new EventQueue(options.maximumQueuedEvents);
	}

	events(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent> {
		return this.queue.read(signal);
	}

	async sendAudio(frame: RealtimeAudioFrame, signal?: AbortSignal): Promise<void> {
		this.ensureOpen();
		signal?.throwIfAborted();
		this.inputGate = this.inputGate.then(() => this.processAudio(frame, signal));
		return this.inputGate;
	}

	async sendText(text: string, role: RealtimeTextRole, signal?: AbortSignal): Promise<void> {
		this.ensureOpen();
		signal?.throwIfAborted();
		this.validateText(text);
		if (role === "user") {
			const handoffId = `local-text-${++this.inputSequence}`;
			this.queue.push({ type: "input.transcript.completed", text, itemId: handoffId, timestamp: Date.now() });
			this.queue.push({ type: "handoff.requested", handoff: { handoffId, transcript: text }, timestamp: Date.now() });
			return;
		}
		if (role === "assistant") return this.sendHandoff(`local-output-${randomUUID()}`, text, "final", true, signal);
		throw new Error("Developer text belongs in the authoritative agent runtime.");
	}

	async sendHandoff(
		handoffId: string,
		text: string,
		_phase: RealtimeHandoffPhase,
		completed: boolean,
		signal?: AbortSignal,
	): Promise<void> {
		this.ensureOpen();
		this.validateText(text, true);
		if (!text) return;
		if (!completed) {
			this.queue.push({ type: "output.transcript.delta", text, responseId: handoffId, timestamp: Date.now() });
			return;
		}
		await this.cancelResponse(signal);
		const controller = new AbortController();
		this.ttsController = controller;
		const combined = signal
			? AbortSignal.any([signal, this.lifetime.signal, controller.signal])
			: AbortSignal.any([this.lifetime.signal, controller.signal]);
		const itemId = `local-audio-${randomUUID()}`;
		this.ttsTask = this.synthesize(handoffId, itemId, text, controller, combined);
		await this.ttsTask;
	}

	async sendBehaviorResult(_result: RealtimeBehaviorResult): Promise<void> {}

	async cancelResponse(_signal?: AbortSignal): Promise<void> {
		const task = this.ttsTask;
		if (!task) return;
		this.ttsController?.abort(new Error("Local speech response cancelled."));
		try {
			await task;
		} catch (error) {
			if (!this.ttsController?.signal.aborted) throw error;
		}
	}

	async truncateAudio(): Promise<void> {}

	async close(): Promise<void> {
		this.closePromise ??= this.closeCore();
		return this.closePromise;
	}

	private async processAudio(frame: RealtimeAudioFrame, signal?: AbortSignal): Promise<void> {
		if (
			frame.pcm16.byteLength < 2 ||
			frame.pcm16.byteLength % 2 !== 0 ||
			frame.channels < 1 ||
			frame.sampleRate < 8_000
		)
			throw new TypeError("Local realtime input must be valid PCM16 audio.");
		if (
			this.inputFrames.length > 0 &&
			(frame.sampleRate !== this.inputSampleRate || frame.channels !== this.inputChannels)
		)
			throw new TypeError("A local speech segment cannot change its PCM format.");
		const speech = await this.options.voiceActivityDetector.isSpeech(frame, signal);
		if (!this.speaking && !speech) return;
		if (!this.speaking) {
			this.speaking = true;
			this.inputSampleRate = frame.sampleRate;
			this.inputChannels = frame.channels;
			this.queue.push({ type: "input.speech.started", timestamp: Date.now() });
		}
		this.inputFrames.push(frame.pcm16.slice());
		this.inputBytes += frame.pcm16.byteLength;
		if (this.inputBytes > this.options.maximumBufferedInputBytes) {
			this.resetInput();
			throw new RangeError("Local speech input exceeded its buffer limit.");
		}
		if (speech) {
			this.speechFrames += 1;
			this.silenceFrames = 0;
		} else {
			this.silenceFrames += 1;
		}
		if (this.silenceFrames >= this.options.silenceFramesToEnd) await this.finishInput(signal);
	}

	private async finishInput(signal?: AbortSignal): Promise<void> {
		this.queue.push({ type: "input.speech.stopped", timestamp: Date.now() });
		const frames = this.inputFrames;
		const total = this.inputBytes;
		const sampleRate = this.inputSampleRate;
		const channels = this.inputChannels;
		const eligible = this.speechFrames >= this.options.minimumSpeechFrames;
		this.resetInput();
		if (!eligible) return;
		const pcm16 = new Uint8Array(total);
		let offset = 0;
		for (const frame of frames) {
			pcm16.set(frame, offset);
			offset += frame.byteLength;
		}
		const result = await this.options.recognizer.transcribe(
			{ pcm16, sampleRate, channels, ...(this.options.language ? { language: this.options.language } : {}) },
			signal,
		);
		const text = result.text.trim();
		this.validateText(text);
		const handoffId = `local-speech-${++this.inputSequence}-${randomUUID()}`;
		this.queue.push({ type: "input.transcript.completed", text, itemId: handoffId, timestamp: Date.now() });
		this.queue.push({ type: "handoff.requested", handoff: { handoffId, transcript: text }, timestamp: Date.now() });
	}

	private async synthesize(
		handoffId: string,
		itemId: string,
		text: string,
		controller: AbortController,
		signal: AbortSignal,
	): Promise<void> {
		let started = false;
		try {
			for await (const frame of this.options.synthesizer.synthesize(
				{ text, voice: this.conversation.voice, itemId },
				signal,
			)) {
				if (!started) {
					started = true;
					this.queue.push({ type: "response.started", itemId, responseId: handoffId, timestamp: Date.now() });
				}
				this.queue.push({
					type: "output.audio",
					audio: { ...frame, pcm16: frame.pcm16.slice(), itemId },
					itemId,
					responseId: handoffId,
					timestamp: Date.now(),
				});
			}
			if (!started) throw new Error("Local speech synthesizer returned no audio.");
			this.queue.push({
				type: "output.transcript.completed",
				text,
				itemId,
				responseId: handoffId,
				timestamp: Date.now(),
			});
			this.queue.push({ type: "response.completed", itemId, responseId: handoffId, timestamp: Date.now() });
		} catch (error) {
			if (signal.aborted) {
				this.queue.push({ type: "response.cancelled", itemId, responseId: handoffId, timestamp: Date.now() });
				return;
			}
			throw error;
		} finally {
			if (this.ttsController === controller) {
				this.ttsController = undefined;
				this.ttsTask = undefined;
			}
		}
	}

	private async closeCore(): Promise<void> {
		this.lifetime.abort(new Error("Local realtime session closed."));
		await this.cancelResponse();
		try {
			await this.inputGate;
		} catch {}
		this.queue.push({ type: "closed", timestamp: Date.now() });
		this.queue.close();
	}

	private resetInput(): void {
		this.inputFrames = [];
		this.inputBytes = 0;
		this.inputSampleRate = 0;
		this.inputChannels = 0;
		this.speechFrames = 0;
		this.silenceFrames = 0;
		this.speaking = false;
	}

	private validateText(value: string, allowEmpty = false): void {
		if ((!allowEmpty && !value) || value.length > this.options.maximumTextCharacters || /\0/u.test(value))
			throw new RangeError("Local realtime text is invalid or too large.");
	}

	private ensureOpen(): void {
		if (this.closePromise || this.lifetime.signal.aborted) throw new Error("Local realtime session is closed.");
	}
}

export class ComposableLocalSpeechTransport implements RealtimeTransport {
	private readonly options: CheckedOptions;
	constructor(options: ComposableLocalSpeechTransportOptions) {
		this.options = checked(options);
	}
	async connect(conversation: RealtimeConversationOptions, signal?: AbortSignal): Promise<RealtimeTransportSession> {
		signal?.throwIfAborted();
		return new ComposableLocalSpeechSession(structuredClone(conversation), this.options);
	}
}

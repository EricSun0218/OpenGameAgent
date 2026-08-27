import type {
	RealtimeAudioFrame,
	RealtimeBehaviorResult,
	RealtimeConversationEvent,
	RealtimeConversationOptions,
	RealtimeConversationState,
	RealtimeEventHandler,
	RealtimeHandoffPhase,
	RealtimeTextRole,
	RealtimeTransport,
	RealtimeTransportSession,
} from "./contracts.js";

function boundedIdentifier(value: string, name: string): string {
	if (
		!value ||
		value.length > 256 ||
		[...value].some((character) => (character.codePointAt(0) ?? 0) < 32 || character.codePointAt(0) === 127)
	) {
		throw new TypeError(`${name} is invalid.`);
	}
	return value;
}

function positive(value: number | undefined, fallback: number, name: string, maximum: number): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < 1 || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

interface CheckedConversationOptions extends RealtimeConversationOptions {
	maximumAudioFrameBytes: number;
	maximumTextCharacters: number;
	maximumEventCharacters: number;
	eventHandlerTimeoutMilliseconds: number;
	shutdownTimeoutMilliseconds: number;
}

function checkedOptions(options: RealtimeConversationOptions): CheckedConversationOptions {
	boundedIdentifier(options.model, "Realtime model");
	boundedIdentifier(options.voice, "Realtime voice");
	const checked: CheckedConversationOptions = {
		...structuredClone(options),
		maximumAudioFrameBytes: positive(
			options.maximumAudioFrameBytes,
			256 * 1024,
			"maximumAudioFrameBytes",
			4 * 1024 * 1024,
		),
		maximumTextCharacters: positive(options.maximumTextCharacters, 65_536, "maximumTextCharacters", 4_000_000),
		maximumEventCharacters: positive(options.maximumEventCharacters, 1_000_000, "maximumEventCharacters", 8_000_000),
		eventHandlerTimeoutMilliseconds: positive(
			options.eventHandlerTimeoutMilliseconds,
			5_000,
			"eventHandlerTimeoutMilliseconds",
			120_000,
		),
		shutdownTimeoutMilliseconds: positive(
			options.shutdownTimeoutMilliseconds,
			10_000,
			"shutdownTimeoutMilliseconds",
			120_000,
		),
	};
	if ((checked.instructions?.length ?? 0) > checked.maximumTextCharacters)
		throw new RangeError("Realtime instructions are too large.");
	if (
		checked.startupContext !== undefined &&
		JSON.stringify(checked.startupContext).length > checked.maximumEventCharacters
	) {
		throw new RangeError("Realtime startup context is too large.");
	}
	return checked;
}

function checkedAudio(frame: RealtimeAudioFrame, maximumBytes: number): RealtimeAudioFrame {
	if (
		!(frame.pcm16 instanceof Uint8Array) ||
		frame.pcm16.byteLength < 2 ||
		frame.pcm16.byteLength % 2 !== 0 ||
		frame.pcm16.byteLength > maximumBytes
	) {
		throw new RangeError("Realtime PCM16 audio frame is invalid or too large.");
	}
	if (!Number.isInteger(frame.sampleRate) || frame.sampleRate < 8_000 || frame.sampleRate > 192_000)
		throw new RangeError("Realtime sample rate is invalid.");
	if (
		!Number.isInteger(frame.channels) ||
		frame.channels < 1 ||
		frame.channels > 8 ||
		frame.pcm16.byteLength % (2 * frame.channels) !== 0
	) {
		throw new RangeError("Realtime channel count is invalid.");
	}
	return { ...frame, pcm16: frame.pcm16.slice() };
}

export class RealtimeConversation {
	private stateValue: RealtimeConversationState = "idle";
	private session?: RealtimeTransportSession;
	private readonly handlers = new Set<RealtimeEventHandler>();
	private pump?: Promise<void>;
	private lifetime = new AbortController();
	private stopPromise?: Promise<void>;
	private readonly playedAudio = new Map<string, number>();
	private options?: CheckedConversationOptions;

	constructor(private readonly transport: RealtimeTransport) {}

	get state(): RealtimeConversationState {
		return this.stateValue;
	}

	onEvent(handler: RealtimeEventHandler): () => void {
		this.handlers.add(handler);
		return () => this.handlers.delete(handler);
	}

	async start(options: RealtimeConversationOptions, signal?: AbortSignal): Promise<void> {
		if (this.stateValue !== "idle") throw new Error("Realtime conversation has already started.");
		this.stateValue = "starting";
		this.options = checkedOptions(options);
		try {
			this.session = await this.transport.connect(this.options, signal);
			this.stateValue = "active";
			this.pump = this.pumpEvents(this.session, this.lifetime.signal);
		} catch (error) {
			this.stateValue = "faulted";
			throw error;
		}
	}

	async sendAudio(frame: RealtimeAudioFrame, signal?: AbortSignal): Promise<void> {
		const session = this.requireActive();
		if (!session.features.has("audio-input")) throw new Error("Realtime transport does not support audio input.");
		await session.sendAudio(checkedAudio(frame, this.requireOptions().maximumAudioFrameBytes), signal);
	}

	async sendText(text: string, role: RealtimeTextRole, signal?: AbortSignal): Promise<void> {
		if (!text || text.length > this.requireOptions().maximumTextCharacters || /\0/u.test(text))
			throw new RangeError("Realtime text is invalid or too large.");
		await this.requireActive().sendText(text, role, signal);
	}

	async sendHandoff(
		handoffId: string,
		text: string,
		phase: RealtimeHandoffPhase,
		completed: boolean,
		signal?: AbortSignal,
	): Promise<void> {
		boundedIdentifier(handoffId, "Handoff id");
		if (text.length > this.requireOptions().maximumTextCharacters || /\0/u.test(text))
			throw new RangeError("Realtime handoff text is too large.");
		await this.requireActive().sendHandoff(handoffId, text, phase, completed, signal);
	}

	async sendBehaviorResult(result: RealtimeBehaviorResult, signal?: AbortSignal): Promise<void> {
		boundedIdentifier(result.behaviorId, "Behavior id");
		await this.requireActive().sendBehaviorResult(structuredClone(result), signal);
	}

	reportAudioPlayback(itemId: string, playedMilliseconds: number): void {
		boundedIdentifier(itemId, "Audio item id");
		if (!Number.isInteger(playedMilliseconds) || playedMilliseconds < 0 || playedMilliseconds > 24 * 60 * 60 * 1_000) {
			throw new RangeError("Audio playback position is invalid.");
		}
		this.playedAudio.set(itemId, Math.max(this.playedAudio.get(itemId) ?? 0, playedMilliseconds));
	}

	async bargeIn(itemId?: string, signal?: AbortSignal): Promise<void> {
		const session = this.requireActive();
		if (session.features.has("response-cancellation")) await session.cancelResponse(signal);
		if (itemId && session.features.has("audio-truncation")) {
			await session.truncateAudio(itemId, this.playedAudio.get(itemId) ?? 0, signal);
		}
	}

	async stop(signal?: AbortSignal): Promise<void> {
		if (this.stopPromise) return this.stopPromise;
		this.stopPromise = this.stopCore(signal);
		return this.stopPromise;
	}

	private async stopCore(signal?: AbortSignal): Promise<void> {
		if (this.stateValue === "closed") return;
		this.stateValue = "stopping";
		const session = this.session;
		this.lifetime.abort();
		if (session) {
			try {
				await session.close(signal);
			} catch (error) {
				if (!this.isClosedError(error)) throw error;
			}
		}
		try {
			await this.pump;
		} catch (error) {
			if (!this.lifetime.signal.aborted) throw error;
		}
		this.stateValue = "closed";
	}

	private async pumpEvents(session: RealtimeTransportSession, signal: AbortSignal): Promise<void> {
		try {
			for await (const event of session.events(signal)) {
				this.validateEvent(event);
				await this.dispatch(event, signal);
				if (event.type === "closed") break;
			}
			if (this.stateValue === "active") this.stateValue = "closed";
		} catch (error) {
			if (!signal.aborted) {
				this.stateValue = "faulted";
				throw error;
			}
		}
	}

	private async dispatch(event: RealtimeConversationEvent, signal: AbortSignal): Promise<void> {
		for (const handler of [...this.handlers]) {
			const timeout = AbortSignal.timeout(this.requireOptions().eventHandlerTimeoutMilliseconds);
			const combined = AbortSignal.any([signal, timeout]);
			await Promise.race([
				handler(event, combined),
				new Promise<never>((_resolve, reject) => {
					combined.addEventListener("abort", () => reject(combined.reason), { once: true });
				}),
			]);
		}
	}

	private validateEvent(event: RealtimeConversationEvent): void {
		if (JSON.stringify(event).length > this.requireOptions().maximumEventCharacters)
			throw new RangeError("Realtime event is too large.");
		if (event.type === "output.audio") checkedAudio(event.audio, this.requireOptions().maximumAudioFrameBytes);
	}

	private requireActive(): RealtimeTransportSession {
		if (this.stateValue !== "active" || !this.session) throw new Error("Realtime conversation is not active.");
		return this.session;
	}

	private requireOptions(): CheckedConversationOptions {
		if (!this.options) throw new Error("Realtime conversation has not started.");
		return this.options;
	}

	private isClosedError(error: unknown): boolean {
		return error instanceof Error && /closed|disposed|abort/iu.test(error.message);
	}
}

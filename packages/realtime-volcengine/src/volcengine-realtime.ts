import { randomUUID } from "node:crypto";
import type {
	RealtimeAudioFrame,
	RealtimeBehaviorResult,
	RealtimeConversationEvent,
	RealtimeConversationOptions,
	RealtimeHandoffPhase,
	RealtimeProviderAuthentication,
	RealtimeTextRole,
	RealtimeTransport,
	RealtimeTransportSession,
} from "@opengameagent/realtime";
import WebSocket from "ws";
import {
	decodeVolcengineFrame,
	encodeVolcengineFrame,
	VolcengineEvent,
	VolcengineMessageType,
	type VolcengineWireMessage,
} from "./wire.js";

export type VolcengineRealtimeInputMode = "dialogue" | "disabled";

export interface VolcengineSocket {
	send(data: Uint8Array, signal?: AbortSignal): Promise<void>;
	messages(signal?: AbortSignal): AsyncIterable<Uint8Array>;
	close(): Promise<void>;
}

export interface VolcengineSocketConnectRequest {
	endpoint: string;
	headers: Readonly<Record<string, string>>;
	connectTimeoutMilliseconds: number;
}

export type VolcengineSocketFactory = (
	request: VolcengineSocketConnectRequest,
	signal?: AbortSignal,
) => Promise<VolcengineSocket>;

export interface VolcengineRealtimeTransportOptions {
	provider?: string;
	dialogueEndpoint?: string;
	ttsEndpoint?: string;
	inputMode?: VolcengineRealtimeInputMode;
	dialogueResourceId?: string;
	ttsResourceId?: string;
	dialogueModel?: string;
	ttsModel?: string;
	speaker?: string;
	appId?: string;
	authentication: RealtimeProviderAuthentication;
	headers?: Readonly<Record<string, string>>;
	socketFactory?: VolcengineSocketFactory;
	inputSampleRate?: number;
	outputSampleRate?: number;
	connectTimeoutMilliseconds?: number;
	wireOperationTimeoutMilliseconds?: number;
	maximumWireFrameBytes?: number;
	maximumPayloadBytes?: number;
	maximumTextCharacters?: number;
}

interface CheckedOptions extends Required<Omit<VolcengineRealtimeTransportOptions, "headers" | "socketFactory">> {
	headers: Readonly<Record<string, string>>;
	socketFactory: VolcengineSocketFactory;
}

interface Channel {
	socket: VolcengineSocket;
	iterator: AsyncIterator<Uint8Array>;
}

class EventQueue {
	private values: RealtimeConversationEvent[] = [];
	private waiters: Array<() => void> = [];
	private done = false;
	push(value: RealtimeConversationEvent): void {
		if (this.done) return;
		this.values.push(value);
		for (const wake of this.waiters.splice(0)) wake();
	}
	close(): void {
		this.done = true;
		for (const wake of this.waiters.splice(0)) wake();
	}
	async *read(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent> {
		for (;;) {
			const value = this.values.shift();
			if (value) {
				yield value;
				continue;
			}
			if (this.done || signal?.aborted) return;
			await new Promise<void>((resolve) => {
				this.waiters.push(resolve);
				signal?.addEventListener("abort", () => resolve(), { once: true });
			});
		}
	}
}

class SocketMessageQueue {
	private values: Uint8Array[] = [];
	private waiters: Array<() => void> = [];
	private done = false;
	private failure: Error | undefined;
	push(value: Uint8Array): void {
		this.values.push(value);
		for (const wake of this.waiters.splice(0)) wake();
	}
	close(error?: Error): void {
		this.done = true;
		this.failure = error;
		for (const wake of this.waiters.splice(0)) wake();
	}
	async *read(signal?: AbortSignal): AsyncIterable<Uint8Array> {
		for (;;) {
			const value = this.values.shift();
			if (value) {
				yield value;
				continue;
			}
			if (this.done) {
				if (this.failure) throw this.failure;
				return;
			}
			if (signal?.aborted) return;
			await new Promise<void>((resolve) => {
				this.waiters.push(resolve);
				signal?.addEventListener("abort", () => resolve(), { once: true });
			});
		}
	}
}

async function defaultSocketFactory(
	request: VolcengineSocketConnectRequest,
	signal?: AbortSignal,
): Promise<VolcengineSocket> {
	const socket = new WebSocket(request.endpoint, {
		headers: request.headers,
		handshakeTimeout: request.connectTimeoutMilliseconds,
	});
	const queue = new SocketMessageQueue();
	socket.on("message", (data, binary) => {
		if (!binary) {
			queue.close(new Error("Volcengine returned a non-binary frame."));
			return;
		}
		const bytes =
			data instanceof ArrayBuffer
				? new Uint8Array(data)
				: new Uint8Array(Buffer.isBuffer(data) ? data : Buffer.concat(data));
		queue.push(bytes);
	});
	socket.on("error", () => queue.close(new Error("Volcengine WebSocket failed.")));
	socket.on("close", () => queue.close());
	await new Promise<void>((resolve, reject) => {
		socket.once("open", resolve);
		socket.once("error", () => reject(new Error("Volcengine WebSocket connection failed.")));
		signal?.addEventListener("abort", () => reject(signal.reason), { once: true });
	});
	return {
		send: (data, sendSignal) =>
			new Promise<void>((resolve, reject) => {
				sendSignal?.throwIfAborted();
				socket.send(data, { binary: true }, (error) =>
					error ? reject(new Error("Volcengine WebSocket send failed.")) : resolve(),
				);
			}),
		messages: (readSignal) => queue.read(readSignal),
		close: () =>
			new Promise<void>((resolve) => {
				if (socket.readyState === WebSocket.CLOSED) return resolve();
				socket.once("close", () => resolve());
				socket.close(1000, "done");
			}),
	};
}

function id(value: string, name: string, maximum = 512): string {
	if (!value || value.length > maximum || [...value].some((character) => (character.codePointAt(0) ?? 0) < 32))
		throw new TypeError(`${name} is invalid.`);
	return value;
}

function endpoint(value: string): string {
	const url = new URL(value);
	const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
	if (url.protocol !== "wss:" && !(loopback && url.protocol === "ws:"))
		throw new TypeError("Volcengine endpoints must use WSS except on loopback.");
	if (url.username || url.password || url.hash || url.search) throw new TypeError("Volcengine endpoint is invalid.");
	return url.toString();
}

function integer(value: number | undefined, fallback: number, minimum: number, maximum: number, name: string): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < minimum || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

function checkedOptions(options: VolcengineRealtimeTransportOptions): CheckedOptions {
	const controlled = new Set([
		"x-api-key",
		"x-api-app-id",
		"x-api-resource-id",
		"x-api-connect-id",
		"authorization",
		"host",
		"content-length",
		"connection",
		"upgrade",
	]);
	const headers: Record<string, string> = {};
	for (const [name, value] of Object.entries(options.headers ?? {})) {
		if (
			!/^[a-z0-9-]{1,64}$/iu.test(name) ||
			controlled.has(name.toLowerCase()) ||
			/[\r\n\0]/u.test(value) ||
			value.length > 8_192
		)
			throw new TypeError("A Volcengine header is invalid.");
		headers[name] = value;
	}
	return {
		provider: id(options.provider ?? "volcengine-realtime", "Provider id"),
		dialogueEndpoint: endpoint(options.dialogueEndpoint ?? "wss://openspeech.bytedance.com/api/v3/realtime/dialogue"),
		ttsEndpoint: endpoint(options.ttsEndpoint ?? "wss://openspeech.bytedance.com/api/v3/tts/bidirection"),
		inputMode: options.inputMode ?? "dialogue",
		dialogueResourceId: id(options.dialogueResourceId ?? "volc.speech.dialog", "Dialogue resource id"),
		ttsResourceId: id(options.ttsResourceId ?? "seed-tts-2.0", "TTS resource id"),
		dialogueModel: id(options.dialogueModel ?? "2.2.0.0", "Dialogue model"),
		ttsModel: id(options.ttsModel ?? "seed-tts-2.0-standard", "TTS model"),
		speaker: id(options.speaker ?? "zh_female_gaolengyujie_uranus_bigtts", "Speaker"),
		appId: options.appId === undefined ? "" : id(options.appId, "App id"),
		authentication: options.authentication,
		headers,
		socketFactory: options.socketFactory ?? defaultSocketFactory,
		inputSampleRate: integer(options.inputSampleRate, 16_000, 8_000, 48_000, "inputSampleRate"),
		outputSampleRate: integer(options.outputSampleRate, 24_000, 8_000, 48_000, "outputSampleRate"),
		connectTimeoutMilliseconds: integer(
			options.connectTimeoutMilliseconds,
			15_000,
			100,
			120_000,
			"connectTimeoutMilliseconds",
		),
		wireOperationTimeoutMilliseconds: integer(
			options.wireOperationTimeoutMilliseconds,
			15_000,
			100,
			120_000,
			"wireOperationTimeoutMilliseconds",
		),
		maximumWireFrameBytes: integer(
			options.maximumWireFrameBytes,
			8_388_608,
			1_024,
			32_000_000,
			"maximumWireFrameBytes",
		),
		maximumPayloadBytes: integer(options.maximumPayloadBytes, 4_194_304, 1_024, 16_000_000, "maximumPayloadBytes"),
		maximumTextCharacters: integer(options.maximumTextCharacters, 65_536, 1, 1_000_000, "maximumTextCharacters"),
	};
}

function jsonBytes(value: unknown): Uint8Array {
	return new TextEncoder().encode(JSON.stringify(value));
}

function parseJson(payload: Uint8Array): Record<string, unknown> | undefined {
	try {
		const parsed = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(payload));
		return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)
			? (parsed as Record<string, unknown>)
			: undefined;
	} catch {
		return undefined;
	}
}

function extractText(value: unknown, maximum: number): string {
	if (typeof value === "string") return value.length <= maximum ? value : value.slice(0, maximum);
	if (typeof value !== "object" || value === null) return "";
	const object = value as Record<string, unknown>;
	for (const key of ["text", "transcript", "result", "utterance", "sentence"]) {
		const candidate = object[key];
		if (typeof candidate === "string" && candidate)
			return candidate.length <= maximum ? candidate : candidate.slice(0, maximum);
	}
	for (const key of ["data", "payload", "result"]) {
		const nested = extractText(object[key], maximum);
		if (nested) return nested;
	}
	return "";
}

async function nextMessage(
	channel: Channel,
	options: CheckedOptions,
	signal?: AbortSignal,
): Promise<VolcengineWireMessage> {
	signal?.throwIfAborted();
	const timeout = AbortSignal.timeout(options.wireOperationTimeoutMilliseconds);
	const interrupted = new Promise<never>((_resolve, reject) => {
		const fail = () =>
			reject(signal?.aborted ? signal.reason : new Error("Volcengine protocol negotiation timed out."));
		timeout.addEventListener("abort", fail, { once: true });
		signal?.addEventListener("abort", fail, { once: true });
	});
	const result = await Promise.race([channel.iterator.next(), interrupted]);
	if (result.done) throw new Error("Volcengine closed during protocol negotiation.");
	if (result.value.byteLength > options.maximumWireFrameBytes) throw new RangeError("Volcengine frame is too large.");
	return decodeVolcengineFrame(result.value, options.maximumPayloadBytes);
}

async function sendFrame(
	channel: Channel,
	frame: Parameters<typeof encodeVolcengineFrame>[0],
	signal?: AbortSignal,
): Promise<void> {
	await channel.socket.send(encodeVolcengineFrame(frame), signal);
}

async function connectChannel(
	options: CheckedOptions,
	endpointValue: string,
	resourceId: string,
	apiKey: string,
	signal?: AbortSignal,
): Promise<Channel> {
	const headers: Record<string, string> = {
		...options.headers,
		"X-Api-Key": apiKey,
		"X-Api-Resource-Id": resourceId,
		"X-Api-Connect-Id": randomUUID(),
	};
	if (options.appId) headers["X-Api-App-ID"] = options.appId;
	const socket = await options.socketFactory(
		{ endpoint: endpointValue, headers, connectTimeoutMilliseconds: options.connectTimeoutMilliseconds },
		signal,
	);
	const channel = { socket, iterator: socket.messages(signal)[Symbol.asyncIterator]() };
	await sendFrame(
		channel,
		{
			messageType: VolcengineMessageType.fullClientRequest,
			eventType: VolcengineEvent.startConnection,
			payload: jsonBytes({}),
			serialization: "json",
		},
		signal,
	);
	const response = await nextMessage(channel, options, signal);
	if (response.messageType === VolcengineMessageType.error || response.eventType !== VolcengineEvent.connectionStarted)
		throw new Error("Volcengine connection negotiation failed.");
	return channel;
}

class VolcengineRealtimeSession implements RealtimeTransportSession {
	readonly features: ReadonlySet<
		| "audio-input"
		| "input-transcription"
		| "audio-output"
		| "output-transcription"
		| "speech-boundaries"
		| "response-cancellation"
		| "handoff"
	>;
	private readonly queue = new EventQueue();
	private readonly lifetime = new AbortController();
	private closePromise: Promise<void> | undefined;
	private activeTts:
		| {
				sessionId: string;
				handoffId: string;
				transcript: string;
				started: boolean;
				acknowledge: () => void;
				reject: (error: Error) => void;
				acknowledged: Promise<void>;
		  }
		| undefined;
	private ttsGate: Promise<void> = Promise.resolve();
	private inputTranscript = "";
	private inputSequence = 0;

	constructor(
		private readonly dialogue: Channel | undefined,
		private readonly tts: Channel,
		private readonly dialogueSessionId: string | undefined,
		private readonly speaker: string,
		private readonly options: CheckedOptions,
	) {
		this.features = new Set(
			options.inputMode === "dialogue"
				? ([
						"audio-input",
						"input-transcription",
						"audio-output",
						"output-transcription",
						"speech-boundaries",
						"response-cancellation",
						"handoff",
					] as const)
				: (["audio-output", "output-transcription", "response-cancellation", "handoff"] as const),
		);
		if (dialogue) void this.pumpDialogue();
		void this.pumpTts();
	}

	events(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent> {
		return this.queue.read(signal);
	}

	async sendAudio(frame: RealtimeAudioFrame, signal?: AbortSignal): Promise<void> {
		this.ensureOpen();
		if (!this.dialogue || !this.dialogueSessionId) throw new Error("Volcengine dialogue input is disabled.");
		if (
			frame.sampleRate !== this.options.inputSampleRate ||
			frame.channels !== 1 ||
			frame.pcm16.byteLength > this.options.maximumPayloadBytes
		)
			throw new TypeError("Volcengine dialogue requires bounded mono PCM16 at the configured sample rate.");
		await sendFrame(
			this.dialogue,
			{
				messageType: VolcengineMessageType.audioOnlyClient,
				eventType: VolcengineEvent.taskRequest,
				sessionId: this.dialogueSessionId,
				payload: frame.pcm16,
				serialization: "raw",
				compression: "gzip",
			},
			signal,
		);
	}

	async sendText(value: string, role: RealtimeTextRole, signal?: AbortSignal): Promise<void> {
		this.ensureOpen();
		if (!value || value.length > this.options.maximumTextCharacters)
			throw new RangeError("Realtime text is invalid or too large.");
		if (role === "user") {
			const handoffId = `volc-text-${++this.inputSequence}`;
			this.queue.push({ type: "input.transcript.completed", text: value, itemId: handoffId, timestamp: Date.now() });
			this.queue.push({ type: "handoff.requested", handoff: { handoffId, transcript: value }, timestamp: Date.now() });
			return;
		}
		if (role === "assistant") return this.sendHandoff(`volc-output-${randomUUID()}`, value, "final", true, signal);
		throw new Error("Developer text belongs in the authoritative agent runtime, not the speech provider.");
	}

	async sendHandoff(
		handoffId: string,
		value: string,
		_phase: RealtimeHandoffPhase,
		completed: boolean,
		signal?: AbortSignal,
	): Promise<void> {
		this.ensureOpen();
		return this.lockTts(async () => {
			if (this.activeTts && this.activeTts.handoffId !== handoffId) await this.cancelActive(signal);
			if (!this.activeTts) await this.startTts(handoffId, signal);
			const active = this.activeTts;
			if (!active) throw new Error("Volcengine TTS session was not initialized.");
			if (value) {
				if (value.length > this.options.maximumTextCharacters) throw new RangeError("TTS text is too large.");
				await sendFrame(
					this.tts,
					{
						messageType: VolcengineMessageType.fullClientRequest,
						eventType: VolcengineEvent.taskRequest,
						sessionId: active.sessionId,
						payload: jsonBytes({ req_params: { text: value } }),
						serialization: "json",
					},
					signal,
				);
			}
			if (completed)
				await sendFrame(
					this.tts,
					{
						messageType: VolcengineMessageType.fullClientRequest,
						eventType: VolcengineEvent.finishSession,
						sessionId: active.sessionId,
						payload: jsonBytes({}),
						serialization: "json",
					},
					signal,
				);
		});
	}

	async sendBehaviorResult(_result: RealtimeBehaviorResult): Promise<void> {}
	async cancelResponse(signal?: AbortSignal): Promise<void> {
		await this.lockTts(() => this.cancelActive(signal));
	}
	async truncateAudio(): Promise<void> {}
	async close(): Promise<void> {
		this.closePromise ??= this.closeCore();
		return this.closePromise;
	}

	private async lockTts(action: () => Promise<void>): Promise<void> {
		const previous = this.ttsGate;
		let release!: () => void;
		this.ttsGate = new Promise<void>((resolve) => {
			release = resolve;
		});
		await previous;
		try {
			await action();
		} finally {
			release();
		}
	}

	private async startTts(handoffId: string, signal?: AbortSignal): Promise<void> {
		const sessionId = randomUUID();
		let acknowledge!: () => void;
		let reject!: (error: Error) => void;
		const acknowledged = new Promise<void>((resolve, rejectPromise) => {
			acknowledge = resolve;
			reject = rejectPromise;
		});
		this.activeTts = { sessionId, handoffId, transcript: "", started: false, acknowledge, reject, acknowledged };
		await sendFrame(
			this.tts,
			{
				messageType: VolcengineMessageType.fullClientRequest,
				eventType: VolcengineEvent.startSession,
				sessionId,
				payload: jsonBytes({
					req_params: {
						model: this.options.ttsModel,
						speaker: this.speaker,
						audio_params: { format: "pcm", sample_rate: this.options.outputSampleRate, enable_subtitle: true },
						additions: JSON.stringify({ disable_markdown_filter: false, disable_emoji_filter: false }),
					},
				}),
				serialization: "json",
			},
			signal,
		);
		const timeout = AbortSignal.timeout(this.options.wireOperationTimeoutMilliseconds);
		await Promise.race([
			acknowledged,
			new Promise<never>((_resolve, rejectTimeout) => {
				const fail = () =>
					rejectTimeout(
						signal?.aborted ? signal.reason : new Error("Volcengine TTS session acknowledgement timed out."),
					);
				timeout.addEventListener("abort", fail, { once: true });
				signal?.addEventListener("abort", fail, { once: true });
			}),
		]);
	}

	private async cancelActive(signal?: AbortSignal): Promise<void> {
		const active = this.activeTts;
		if (!active) return;
		this.activeTts = undefined;
		await sendFrame(
			this.tts,
			{
				messageType: VolcengineMessageType.fullClientRequest,
				eventType: VolcengineEvent.cancelSession,
				sessionId: active.sessionId,
				payload: jsonBytes({}),
				serialization: "json",
			},
			signal,
		);
		this.queue.push({
			type: "response.cancelled",
			itemId: active.sessionId,
			responseId: active.handoffId,
			timestamp: Date.now(),
		});
	}

	private async pumpDialogue(): Promise<void> {
		if (!this.dialogue) return;
		try {
			for (;;) {
				const result = await this.dialogue.iterator.next();
				if (result.done || this.lifetime.signal.aborted) {
					if (result.done && !this.lifetime.signal.aborted) void this.close();
					return;
				}
				if (result.value.byteLength > this.options.maximumWireFrameBytes)
					throw new RangeError("Volcengine frame is too large.");
				this.handleDialogue(decodeVolcengineFrame(result.value, this.options.maximumPayloadBytes));
			}
		} catch {
			this.queue.push({
				type: "error",
				category: "provider",
				message: "Volcengine dialogue failed.",
				timestamp: Date.now(),
			});
			if (!this.lifetime.signal.aborted) void this.close();
		}
	}

	private handleDialogue(message: VolcengineWireMessage): void {
		if (
			message.messageType === VolcengineMessageType.error ||
			message.eventType === VolcengineEvent.sessionFailed ||
			message.eventType === VolcengineEvent.connectionFailed
		) {
			this.queue.push({
				type: "error",
				category: "provider",
				message: "Volcengine dialogue failed.",
				timestamp: Date.now(),
			});
			return;
		}
		if (message.eventType === VolcengineEvent.asrInfo) {
			this.inputTranscript = "";
			this.queue.push({ type: "input.speech.started", timestamp: Date.now() });
		} else if (message.eventType === VolcengineEvent.asrResponse) {
			const value = extractText(parseJson(message.payload), this.options.maximumTextCharacters);
			if (value) {
				this.inputTranscript = value;
				this.queue.push({
					type: "input.transcript.delta",
					text: value,
					...(message.sessionId ? { itemId: message.sessionId } : {}),
					timestamp: Date.now(),
				});
			}
		} else if (message.eventType === VolcengineEvent.asrEnded) {
			this.queue.push({ type: "input.speech.stopped", timestamp: Date.now() });
			const transcript = this.inputTranscript.trim();
			this.inputTranscript = "";
			if (transcript) {
				const handoffId = `volc-${++this.inputSequence}-${randomUUID()}`;
				this.queue.push({
					type: "input.transcript.completed",
					text: transcript,
					itemId: message.sessionId ?? handoffId,
					timestamp: Date.now(),
				});
				this.queue.push({ type: "handoff.requested", handoff: { handoffId, transcript }, timestamp: Date.now() });
			}
		}
	}

	private async pumpTts(): Promise<void> {
		try {
			for (;;) {
				const result = await this.tts.iterator.next();
				if (result.done || this.lifetime.signal.aborted) {
					if (result.done && !this.lifetime.signal.aborted) void this.close();
					return;
				}
				if (result.value.byteLength > this.options.maximumWireFrameBytes)
					throw new RangeError("Volcengine frame is too large.");
				this.handleTts(decodeVolcengineFrame(result.value, this.options.maximumPayloadBytes));
			}
		} catch {
			this.activeTts?.reject(new Error("Volcengine TTS channel closed."));
			this.queue.push({
				type: "error",
				category: "provider",
				message: "Volcengine TTS failed.",
				timestamp: Date.now(),
			});
			if (!this.lifetime.signal.aborted) void this.close();
		}
	}

	private handleTts(message: VolcengineWireMessage): void {
		const active = this.activeTts;
		if (
			message.messageType === VolcengineMessageType.error ||
			message.eventType === VolcengineEvent.sessionFailed ||
			message.eventType === VolcengineEvent.connectionFailed
		) {
			this.queue.push({
				type: "error",
				category: "provider",
				message: "Volcengine TTS failed.",
				timestamp: Date.now(),
			});
			return;
		}
		if (!active || (message.sessionId && message.sessionId !== active.sessionId)) return;
		if (message.eventType === VolcengineEvent.sessionStarted) {
			active.acknowledge();
			return;
		}
		if (
			(message.eventType === VolcengineEvent.ttsSentenceStart || message.eventType === VolcengineEvent.ttsResponse) &&
			!active.started
		) {
			active.started = true;
			this.queue.push({
				type: "response.started",
				itemId: active.sessionId,
				responseId: active.handoffId,
				timestamp: Date.now(),
			});
		}
		if (
			message.eventType === VolcengineEvent.ttsResponse ||
			(message.messageType === VolcengineMessageType.audioOnlyServer && message.payload.byteLength > 0)
		) {
			if (message.payload.byteLength % 2 !== 0) {
				this.queue.push({
					type: "error",
					category: "protocol",
					message: "Volcengine returned invalid audio.",
					timestamp: Date.now(),
				});
				return;
			}
			this.queue.push({
				type: "output.audio",
				audio: {
					pcm16: message.payload.slice(),
					sampleRate: this.options.outputSampleRate,
					channels: 1,
					itemId: active.sessionId,
				},
				itemId: active.sessionId,
				responseId: active.handoffId,
				timestamp: Date.now(),
			});
		} else if (message.eventType === VolcengineEvent.ttsSubtitle) {
			const value = extractText(parseJson(message.payload), this.options.maximumTextCharacters);
			if (value) {
				active.transcript = (active.transcript + value).slice(0, this.options.maximumTextCharacters);
				this.queue.push({
					type: "output.transcript.delta",
					text: value,
					itemId: active.sessionId,
					responseId: active.handoffId,
					timestamp: Date.now(),
				});
			}
		} else if (
			message.eventType === VolcengineEvent.ttsEnded ||
			message.eventType === VolcengineEvent.sessionFinished ||
			message.eventType === VolcengineEvent.sessionCancelled
		) {
			if (active.transcript)
				this.queue.push({
					type: "output.transcript.completed",
					text: active.transcript,
					itemId: active.sessionId,
					responseId: active.handoffId,
					timestamp: Date.now(),
				});
			this.queue.push({
				type: message.eventType === VolcengineEvent.sessionCancelled ? "response.cancelled" : "response.completed",
				itemId: active.sessionId,
				responseId: active.handoffId,
				timestamp: Date.now(),
			});
			this.activeTts = undefined;
		}
	}

	private async closeCore(): Promise<void> {
		this.lifetime.abort();
		this.activeTts?.reject(new Error("Volcengine realtime session closed."));
		try {
			await this.cancelActive();
		} catch {}
		try {
			if (this.dialogue && this.dialogueSessionId) {
				await sendFrame(this.dialogue, {
					messageType: VolcengineMessageType.fullClientRequest,
					eventType: VolcengineEvent.finishSession,
					sessionId: this.dialogueSessionId,
					payload: jsonBytes({}),
					serialization: "json",
					compression: "gzip",
				});
				await sendFrame(this.dialogue, {
					messageType: VolcengineMessageType.fullClientRequest,
					eventType: VolcengineEvent.finishConnection,
					payload: jsonBytes({}),
					serialization: "json",
					compression: "gzip",
				});
			}
			await sendFrame(this.tts, {
				messageType: VolcengineMessageType.fullClientRequest,
				eventType: VolcengineEvent.finishConnection,
				payload: jsonBytes({}),
				serialization: "json",
			});
		} catch {}
		await Promise.allSettled(
			[this.dialogue?.socket.close(), this.tts.socket.close()].filter(
				(value): value is Promise<void> => value !== undefined,
			),
		);
		this.queue.push({ type: "closed", timestamp: Date.now() });
		this.queue.close();
	}

	private ensureOpen(): void {
		if (this.closePromise || this.lifetime.signal.aborted) throw new Error("Volcengine realtime session is closed.");
	}
}

export class VolcengineRealtimeTransport implements RealtimeTransport {
	private readonly options: CheckedOptions;
	constructor(options: VolcengineRealtimeTransportOptions) {
		this.options = checkedOptions(options);
	}

	async connect(conversation: RealtimeConversationOptions, signal?: AbortSignal): Promise<RealtimeTransportSession> {
		const auth = await this.options.authentication.resolve(this.options.provider, signal);
		if (!auth?.apiKey || (auth.headers && Object.keys(auth.headers).length > 0))
			throw new Error("Volcengine realtime authentication is unavailable or unsupported.");
		const speaker =
			!conversation.voice || conversation.voice === "alloy"
				? this.options.speaker
				: id(conversation.voice, "Conversation voice", 256);
		let dialogue: Channel | undefined;
		let dialogueSessionId: string | undefined;
		if (this.options.inputMode === "dialogue") {
			dialogue = await connectChannel(
				this.options,
				this.options.dialogueEndpoint,
				this.options.dialogueResourceId,
				auth.apiKey,
				signal,
			);
			dialogueSessionId = randomUUID();
			await sendFrame(
				dialogue,
				{
					messageType: VolcengineMessageType.fullClientRequest,
					eventType: VolcengineEvent.startSession,
					sessionId: dialogueSessionId,
					payload: jsonBytes({
						asr: { extra: { end_smooth_window_ms: 800, enable_custom_vad: false } },
						tts: { audio_config: { channel: 1, format: "pcm_s16le", sample_rate: this.options.outputSampleRate } },
						dialog: {
							character_manifest: "Transcribe the speaker accurately. Game decisions are handled by the host.",
							extra: { strict_audit: false, recv_timeout: 120, input_mod: "audio", model: this.options.dialogueModel },
						},
					}),
					serialization: "json",
					compression: "gzip",
				},
				signal,
			);
			const response = await nextMessage(dialogue, this.options, signal);
			if (response.eventType !== VolcengineEvent.sessionStarted)
				throw new Error("Volcengine dialogue session negotiation failed.");
		}
		const tts = await connectChannel(
			this.options,
			this.options.ttsEndpoint,
			this.options.ttsResourceId,
			auth.apiKey,
			signal,
		);
		return new VolcengineRealtimeSession(dialogue, tts, dialogueSessionId, speaker, this.options);
	}
}

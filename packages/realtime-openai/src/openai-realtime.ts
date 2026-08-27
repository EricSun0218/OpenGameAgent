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
import OpenAI from "openai";
import { OpenAIRealtimeWebSocket } from "openai/realtime/websocket";
import type { RealtimeClientEvent } from "openai/resources/realtime/realtime";

export interface OpenAIRealtimeWire {
	send(event: Record<string, unknown>): void;
	close(): void;
	onEvent(handler: (event: Record<string, unknown>) => void): void;
	onError(handler: (error: unknown) => void): void;
	onClose(handler: () => void): void;
}

export interface OpenAIRealtimeWireFactoryOptions {
	endpoint: string;
	model: string;
	apiKey: string;
}

export type OpenAIRealtimeWireFactory = (
	options: OpenAIRealtimeWireFactoryOptions,
	signal?: AbortSignal,
) => Promise<OpenAIRealtimeWire>;

export interface OpenAIRealtimeTransportOptions {
	provider?: string;
	endpoint?: string;
	authentication?: RealtimeProviderAuthentication;
	requiresCredential?: boolean;
	inputTranscriptionModel?: string;
	turnDetection?: "server_vad" | "semantic_vad" | "none";
	maximumWireEventCharacters?: number;
	maximumAudioFrameBytes?: number;
	wireFactory?: OpenAIRealtimeWireFactory;
}

interface CheckedOptions {
	provider: string;
	endpoint: string;
	authentication?: RealtimeProviderAuthentication;
	requiresCredential: boolean;
	inputTranscriptionModel: string;
	turnDetection: "server_vad" | "semantic_vad" | "none";
	maximumWireEventCharacters: number;
	maximumAudioFrameBytes: number;
	wireFactory: OpenAIRealtimeWireFactory;
}

class AsyncEventQueue {
	private readonly values: RealtimeConversationEvent[] = [];
	private waiters: Array<() => void> = [];
	private closed = false;

	push(value: RealtimeConversationEvent): void {
		if (this.closed) return;
		this.values.push(value);
		for (const wake of this.waiters.splice(0)) wake();
	}

	close(): void {
		this.closed = true;
		for (const wake of this.waiters.splice(0)) wake();
	}

	async *read(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent> {
		for (;;) {
			const value = this.values.shift();
			if (value) {
				yield value;
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

function identifier(value: string, name: string): string {
	if (!/^[a-z0-9][a-z0-9._:/-]{0,255}$/iu.test(value)) throw new TypeError(`${name} is invalid.`);
	return value;
}

function endpoint(value: string): string {
	const url = new URL(value);
	const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
	if (url.protocol !== "https:" && !(loopback && url.protocol === "http:"))
		throw new TypeError("Realtime endpoints must use HTTPS except on loopback.");
	if (url.username || url.password || url.hash || url.search) throw new TypeError("Realtime endpoint is invalid.");
	return url.toString().replace(/\/$/u, "");
}

function positive(value: number | undefined, fallback: number, name: string, maximum: number): number {
	const result = value ?? fallback;
	if (!Number.isInteger(result) || result < 1 || result > maximum) throw new RangeError(`${name} is invalid.`);
	return result;
}

async function defaultWireFactory(
	options: OpenAIRealtimeWireFactoryOptions,
	signal?: AbortSignal,
): Promise<OpenAIRealtimeWire> {
	const client = new OpenAI({ apiKey: options.apiKey, baseURL: options.endpoint, maxRetries: 0 });
	const realtime = await OpenAIRealtimeWebSocket.create(client, { model: options.model });
	await new Promise<void>((resolve, reject) => {
		const opened = () => resolve();
		const failed = () => reject(new Error("Realtime WebSocket connection failed."));
		realtime.socket.addEventListener("open", opened, { once: true });
		realtime.socket.addEventListener("error", failed, { once: true });
		signal?.addEventListener(
			"abort",
			() => {
				realtime.close({ code: 1000, reason: "cancelled" });
				reject(signal.reason);
			},
			{ once: true },
		);
	});
	return {
		send: (event) => realtime.send(event as unknown as RealtimeClientEvent),
		close: () => realtime.close({ code: 1000, reason: "done" }),
		onEvent: (handler) => realtime.on("event", (event) => handler(event as unknown as Record<string, unknown>)),
		onError: (handler) => realtime.on("error", handler),
		onClose: (handler) => realtime.socket.addEventListener("close", handler),
	};
}

function checkedOptions(options: OpenAIRealtimeTransportOptions): CheckedOptions {
	const provider = identifier(options.provider ?? "openai-realtime", "Realtime provider");
	return {
		provider,
		endpoint: endpoint(options.endpoint ?? "https://api.openai.com/v1"),
		...(options.authentication === undefined ? {} : { authentication: options.authentication }),
		requiresCredential: options.requiresCredential ?? true,
		inputTranscriptionModel: identifier(
			options.inputTranscriptionModel ?? "gpt-4o-mini-transcribe",
			"Transcription model",
		),
		turnDetection: options.turnDetection ?? "semantic_vad",
		maximumWireEventCharacters: positive(
			options.maximumWireEventCharacters,
			2_000_000,
			"maximumWireEventCharacters",
			16_000_000,
		),
		maximumAudioFrameBytes: positive(
			options.maximumAudioFrameBytes,
			256 * 1024,
			"maximumAudioFrameBytes",
			4 * 1024 * 1024,
		),
		wireFactory: options.wireFactory ?? defaultWireFactory,
	};
}

function record(value: unknown): Record<string, unknown> | undefined {
	return typeof value === "object" && value !== null && !Array.isArray(value)
		? (value as Record<string, unknown>)
		: undefined;
}

function text(value: unknown, maximum = 1_000_000): string | undefined {
	return typeof value === "string" && value.length <= maximum && !/\0/u.test(value) ? value : undefined;
}

function strictBase64(value: unknown, maximumBytes: number): Uint8Array {
	if (
		typeof value !== "string" ||
		value.length > Math.ceil(maximumBytes / 3) * 4 + 8 ||
		!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(value)
	) {
		throw new Error("Realtime provider returned invalid audio.");
	}
	const bytes = Buffer.from(value, "base64");
	if (bytes.byteLength < 2 || bytes.byteLength > maximumBytes || bytes.byteLength % 2 !== 0)
		throw new Error("Realtime provider returned invalid audio.");
	return new Uint8Array(bytes);
}

function safeError(_error: unknown): RealtimeConversationEvent {
	return { type: "error", category: "provider", message: "Realtime provider error.", timestamp: Date.now() };
}

function functionCall(event: Record<string, unknown>): Record<string, unknown> | undefined {
	const item = record(event["item"]);
	if (item && item["type"] === "function_call") return item;
	if (event["type"] === "response.function_call_arguments.done") return event;
	return undefined;
}

function parseFunctionCall(call: Record<string, unknown>): RealtimeConversationEvent | undefined {
	const name = text(call["name"], 256);
	const callId = text(call["call_id"], 256);
	const args = text(call["arguments"], 1_000_000);
	if (!name || !callId || !args) return undefined;
	let value: unknown;
	try {
		value = JSON.parse(args);
	} catch {
		return {
			type: "error",
			category: "protocol",
			message: "Realtime tool arguments were invalid.",
			timestamp: Date.now(),
		};
	}
	const object = record(value);
	if (!object) return undefined;
	if (name === "handoff") {
		const transcript = text(object["transcript"]);
		if (!transcript) return undefined;
		return {
			type: "handoff.requested",
			timestamp: Date.now(),
			handoff: {
				handoffId: callId,
				transcript,
				...(object["context"] === undefined ? {} : { context: object["context"] as never }),
			},
		};
	}
	if (name === "behavior") {
		const channel = text(object["channel"], 256);
		const behavior = text(object["behavior"], 256);
		const argumentsValue = record(object["arguments"]);
		if (!channel || !behavior || !argumentsValue) return undefined;
		return {
			type: "behavior.requested",
			timestamp: Date.now(),
			behavior: { behaviorId: callId, channel, behavior, arguments: argumentsValue as never },
		};
	}
	return undefined;
}

class OpenAIRealtimeSession implements RealtimeTransportSession {
	readonly features = new Set([
		"audio-input",
		"input-transcription",
		"audio-output",
		"output-transcription",
		"speech-boundaries",
		"response-cancellation",
		"audio-truncation",
		"handoff",
		"behavior-requests",
	] as const);
	private readonly eventsQueue = new AsyncEventQueue();
	private closed = false;

	constructor(
		private readonly wire: OpenAIRealtimeWire,
		private readonly options: CheckedOptions,
		conversation: RealtimeConversationOptions,
	) {
		wire.onEvent((event) => this.receive(event));
		wire.onError((error) => this.eventsQueue.push(safeError(error)));
		wire.onClose(() => {
			this.eventsQueue.push({ type: "closed", timestamp: Date.now() });
			this.eventsQueue.close();
		});
		this.send({
			type: "session.update",
			session: {
				type: "realtime",
				model: conversation.model,
				instructions: conversation.instructions ?? "",
				output_modalities: [conversation.outputModality ?? "audio"],
				audio: {
					input: {
						format: { type: "audio/pcm", rate: 24_000 },
						transcription: { model: options.inputTranscriptionModel },
						turn_detection: options.turnDetection === "none" ? null : { type: options.turnDetection },
					},
					output: { format: { type: "audio/pcm", rate: 24_000 }, voice: conversation.voice },
				},
				tools: [
					{
						type: "function",
						name: "handoff",
						description: "Hand a complex or action-bearing request to the authoritative game agent.",
						parameters: {
							type: "object",
							properties: { transcript: { type: "string" }, context: {} },
							required: ["transcript"],
							additionalProperties: false,
						},
					},
					{
						type: "function",
						name: "behavior",
						description: "Request a cancellable presentation behavior from the game host.",
						parameters: {
							type: "object",
							properties: { channel: { type: "string" }, behavior: { type: "string" }, arguments: { type: "object" } },
							required: ["channel", "behavior", "arguments"],
							additionalProperties: false,
						},
					},
				],
			},
		});
	}

	events(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent> {
		return this.eventsQueue.read(signal);
	}

	async sendAudio(frame: RealtimeAudioFrame): Promise<void> {
		if (
			frame.sampleRate !== 24_000 ||
			frame.channels !== 1 ||
			frame.pcm16.byteLength > this.options.maximumAudioFrameBytes
		)
			throw new TypeError("OpenAI Realtime requires bounded 24 kHz mono PCM16 input.");
		this.send({ type: "input_audio_buffer.append", audio: Buffer.from(frame.pcm16).toString("base64") });
	}

	async sendText(value: string, role: RealtimeTextRole): Promise<void> {
		this.send({
			type: "conversation.item.create",
			item: {
				type: "message",
				role,
				content: [{ type: role === "assistant" ? "output_text" : "input_text", text: value }],
			},
		});
		if (role === "user") this.send({ type: "response.create" });
	}

	async sendHandoff(handoffId: string, value: string, phase: RealtimeHandoffPhase, completed: boolean): Promise<void> {
		if (completed) {
			this.send({
				type: "conversation.item.create",
				item: { type: "function_call_output", call_id: handoffId, output: value },
			});
			this.send({ type: "response.create" });
		} else if (value) {
			await this.sendText(value, phase === "final" ? "assistant" : "developer");
		}
	}

	async sendBehaviorResult(result: RealtimeBehaviorResult): Promise<void> {
		this.send({
			type: "conversation.item.create",
			item: {
				type: "function_call_output",
				call_id: result.behaviorId,
				output: JSON.stringify({ disposition: result.disposition, details: result.details }),
			},
		});
		this.send({ type: "response.create" });
	}

	async cancelResponse(): Promise<void> {
		this.send({ type: "response.cancel" });
	}

	async truncateAudio(itemId: string, audioEndMilliseconds: number): Promise<void> {
		this.send({
			type: "conversation.item.truncate",
			item_id: itemId,
			content_index: 0,
			audio_end_ms: Math.max(0, audioEndMilliseconds),
		});
	}

	async close(): Promise<void> {
		if (this.closed) return;
		this.closed = true;
		this.wire.close();
		this.eventsQueue.close();
	}

	private send(event: Record<string, unknown>): void {
		if (this.closed) throw new Error("Realtime session is closed.");
		if (JSON.stringify(event).length > this.options.maximumWireEventCharacters)
			throw new RangeError("Realtime outbound event is too large.");
		this.wire.send(event);
	}

	private receive(event: Record<string, unknown>): void {
		if (JSON.stringify(event).length > this.options.maximumWireEventCharacters) {
			this.eventsQueue.push({
				type: "error",
				category: "protocol",
				message: "Realtime provider event was too large.",
				timestamp: Date.now(),
			});
			return;
		}
		const type = text(event["type"], 256);
		const timestamp = Date.now();
		const itemId = text(event["item_id"], 256);
		const responseId = text(event["response_id"], 256) ?? text(record(event["response"])?.["id"], 256);
		let projected: RealtimeConversationEvent | undefined;
		switch (type) {
			case "session.created":
			case "session.updated":
				projected = { type: "session.updated", timestamp };
				break;
			case "input_audio_buffer.speech_started":
				projected = { type: "input.speech.started", timestamp, ...(itemId ? { itemId } : {}) };
				break;
			case "input_audio_buffer.speech_stopped":
				projected = { type: "input.speech.stopped", timestamp, ...(itemId ? { itemId } : {}) };
				break;
			case "conversation.item.input_audio_transcription.delta":
				projected = {
					type: "input.transcript.delta",
					timestamp,
					text: text(event["delta"]) ?? "",
					...(itemId ? { itemId } : {}),
				};
				break;
			case "conversation.item.input_audio_transcription.completed":
				projected = {
					type: "input.transcript.completed",
					timestamp,
					text: text(event["transcript"]) ?? "",
					...(itemId ? { itemId } : {}),
				};
				break;
			case "response.audio_transcript.delta":
			case "response.output_audio_transcript.delta":
			case "response.output_text.delta":
				projected = {
					type: "output.transcript.delta",
					timestamp,
					text: text(event["delta"]) ?? "",
					...(itemId ? { itemId } : {}),
					...(responseId ? { responseId } : {}),
				};
				break;
			case "response.audio_transcript.done":
			case "response.output_audio_transcript.done":
			case "response.output_text.done":
				projected = {
					type: "output.transcript.completed",
					timestamp,
					text: text(event["transcript"]) ?? text(event["text"]) ?? "",
					...(itemId ? { itemId } : {}),
					...(responseId ? { responseId } : {}),
				};
				break;
			case "response.audio.delta":
			case "response.output_audio.delta":
				projected = {
					type: "output.audio",
					timestamp,
					audio: {
						pcm16: strictBase64(event["delta"], this.options.maximumAudioFrameBytes),
						sampleRate: 24_000,
						channels: 1,
						...(itemId ? { itemId } : {}),
					},
					...(itemId ? { itemId } : {}),
					...(responseId ? { responseId } : {}),
				};
				break;
			case "response.created":
				projected = { type: "response.started", timestamp, ...(responseId ? { responseId } : {}) };
				break;
			case "response.cancelled":
				projected = { type: "response.cancelled", timestamp, ...(responseId ? { responseId } : {}) };
				break;
			case "response.done":
				projected =
					record(event["response"])?.["status"] === "cancelled"
						? { type: "response.cancelled", timestamp, ...(responseId ? { responseId } : {}) }
						: { type: "response.completed", timestamp, ...(responseId ? { responseId } : {}) };
				break;
			case "response.function_call_arguments.done":
			case "response.output_item.done":
			case "conversation.item.done":
				projected = functionCall(event) ? parseFunctionCall(functionCall(event) as Record<string, unknown>) : undefined;
				break;
			case "error":
				projected = safeError(event);
				break;
		}
		if (projected) this.eventsQueue.push(projected);
	}
}

export class OpenAIRealtimeTransport implements RealtimeTransport {
	private readonly options: CheckedOptions;

	constructor(options: OpenAIRealtimeTransportOptions = {}) {
		this.options = checkedOptions(options);
	}

	async connect(conversation: RealtimeConversationOptions, signal?: AbortSignal): Promise<RealtimeTransportSession> {
		const credential = await this.options.authentication?.resolve(this.options.provider, signal);
		if (
			(credential?.headers && Object.keys(credential.headers).length > 0) ||
			(this.options.requiresCredential && !credential?.apiKey)
		) {
			throw new Error("Realtime provider authentication is unavailable or unsupported.");
		}
		const apiKey = credential?.apiKey ?? "local";
		const wire = await this.options.wireFactory(
			{ endpoint: this.options.endpoint, model: conversation.model, apiKey },
			signal,
		);
		return new OpenAIRealtimeSession(wire, this.options, conversation);
	}
}

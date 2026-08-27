import type { GameInput, GameSessionKey, JsonObject, JsonValue } from "@opengameagent/protocol";

export type RealtimeFeature =
	| "audio-input"
	| "input-transcription"
	| "audio-output"
	| "output-transcription"
	| "speech-boundaries"
	| "response-cancellation"
	| "audio-truncation"
	| "handoff"
	| "behavior-requests";

export type RealtimeConversationState = "idle" | "starting" | "active" | "stopping" | "closed" | "faulted";
export type RealtimeTextRole = "user" | "assistant" | "developer";
export type RealtimeHandoffPhase = "commentary" | "final";
export type RealtimeBehaviorDisposition = "started" | "replaced" | "completed" | "cancelled" | "rejected" | "failed";

export interface RealtimeAudioFrame {
	pcm16: Uint8Array;
	sampleRate: number;
	channels: number;
	itemId?: string;
}

export interface RealtimeTranscriptTiming {
	startMilliseconds: number;
	endMilliseconds: number;
	confidence?: number;
	wordLevel?: boolean;
}

export interface RealtimeHandoffRequest {
	handoffId: string;
	transcript: string;
	context?: JsonValue;
	clientManaged?: boolean;
	isTranscriptTail?: boolean;
}

export interface RealtimeBehaviorRequest {
	behaviorId: string;
	channel: string;
	behavior: string;
	arguments: JsonObject;
	priority?: number;
}

export interface RealtimeBehaviorResult {
	behaviorId: string;
	disposition: RealtimeBehaviorDisposition;
	details?: JsonValue;
}

interface RealtimeEventBase {
	timestamp: number;
	itemId?: string;
	responseId?: string;
}

export type RealtimeConversationEvent =
	| (RealtimeEventBase & { type: "session.updated" })
	| (RealtimeEventBase & { type: "input.speech.started" })
	| (RealtimeEventBase & { type: "input.speech.stopped" })
	| (RealtimeEventBase & { type: "input.transcript.delta"; text: string; timing?: RealtimeTranscriptTiming })
	| (RealtimeEventBase & { type: "input.transcript.completed"; text: string; timing?: RealtimeTranscriptTiming })
	| (RealtimeEventBase & { type: "output.transcript.delta"; text: string; timing?: RealtimeTranscriptTiming })
	| (RealtimeEventBase & { type: "output.transcript.completed"; text: string; timing?: RealtimeTranscriptTiming })
	| (RealtimeEventBase & { type: "output.audio"; audio: RealtimeAudioFrame })
	| (RealtimeEventBase & { type: "response.started" })
	| (RealtimeEventBase & { type: "response.cancelled" })
	| (RealtimeEventBase & { type: "response.completed" })
	| (RealtimeEventBase & { type: "handoff.requested"; handoff: RealtimeHandoffRequest })
	| (RealtimeEventBase & { type: "behavior.requested"; behavior: RealtimeBehaviorRequest })
	| (RealtimeEventBase & { type: "behavior.cancelled"; behaviorId: string })
	| (RealtimeEventBase & { type: "error"; category: string; message: string })
	| (RealtimeEventBase & { type: "closed" });

export interface RealtimeConversationOptions {
	model: string;
	voice: string;
	instructions?: string;
	outputModality?: "audio" | "text";
	startupContext?: JsonValue;
	clientManagedHandoffs?: boolean;
	maximumAudioFrameBytes?: number;
	maximumTextCharacters?: number;
	maximumEventCharacters?: number;
	eventHandlerTimeoutMilliseconds?: number;
	shutdownTimeoutMilliseconds?: number;
}

export interface RealtimeTransportSession {
	readonly features: ReadonlySet<RealtimeFeature>;
	events(signal?: AbortSignal): AsyncIterable<RealtimeConversationEvent>;
	sendAudio(frame: RealtimeAudioFrame, signal?: AbortSignal): Promise<void>;
	sendText(text: string, role: RealtimeTextRole, signal?: AbortSignal): Promise<void>;
	sendHandoff(
		handoffId: string,
		text: string,
		phase: RealtimeHandoffPhase,
		completed: boolean,
		signal?: AbortSignal,
	): Promise<void>;
	sendBehaviorResult(result: RealtimeBehaviorResult, signal?: AbortSignal): Promise<void>;
	cancelResponse(signal?: AbortSignal): Promise<void>;
	truncateAudio(itemId: string, audioEndMilliseconds: number, signal?: AbortSignal): Promise<void>;
	close(signal?: AbortSignal): Promise<void>;
}

export interface RealtimeTransport {
	connect(options: RealtimeConversationOptions, signal?: AbortSignal): Promise<RealtimeTransportSession>;
}

export interface RealtimeProviderAuthenticationResult {
	apiKey?: string;
	headers?: Readonly<Record<string, string>>;
}

export interface RealtimeProviderAuthentication {
	resolve(provider: string, signal?: AbortSignal): Promise<RealtimeProviderAuthenticationResult | undefined>;
}

export type RealtimeEventHandler = (event: RealtimeConversationEvent, signal: AbortSignal) => void | Promise<void>;

export type RealtimeGameInputFactory = (
	handoff: RealtimeHandoffRequest,
	session: GameSessionKey,
	signal: AbortSignal,
) => Promise<GameInput>;

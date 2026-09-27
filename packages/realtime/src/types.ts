import type {
	JsonValue,
	RunAdmissionAuthority,
	RunCoordinate,
	RuntimeDependencyRequirement,
	StreamKey,
} from "@opengameagent/protocol";

export interface RealtimeVoiceCoordinate extends StreamKey {
	readonly voiceSessionId: string;
	readonly epoch: string;
	readonly utteranceId: string;
}

export interface RealtimeAudioFormat {
	readonly mediaType: string;
	readonly sampleRateHz?: number;
	readonly channelCount?: number;
}

export interface RealtimeAudioChunk {
	readonly bytes: Uint8Array;
}

export type SpeechRecognitionEvent =
	| { readonly type: "transcript.delta"; readonly text: string }
	| { readonly type: "transcript.final"; readonly text: string };

export interface SpeechRecognitionRequest {
	readonly format: Readonly<RealtimeAudioFormat>;
	readonly audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	readonly signal: AbortSignal;
}

export interface RealtimeSpeechRecognizer {
	recognize(request: Readonly<SpeechRecognitionRequest>): AsyncIterable<Readonly<SpeechRecognitionEvent>>;
}

export interface SpeechSynthesisRequest {
	readonly voiceProfileId: string;
	readonly format: Readonly<RealtimeAudioFormat>;
	readonly text: AsyncIterable<string>;
	readonly signal: AbortSignal;
}

export interface RealtimeSpeechSynthesizer {
	synthesize(request: Readonly<SpeechSynthesisRequest>): AsyncIterable<Readonly<RealtimeAudioChunk>>;
}

export interface RealtimeVoiceLimits {
	readonly maxIdentifierBytes: number;
	readonly maxMediaTypeBytes: number;
	readonly maxInputChunks: number;
	readonly maxInputChunkBytes: number;
	readonly maxInputAudioBytes: number;
	readonly maxRecognitionEvents: number;
	readonly maxRecognitionTextBytes: number;
	readonly maxTranscriptBytes: number;
	readonly maxAssistantTextEvents: number;
	readonly maxAssistantTextBytes: number;
	readonly maxPendingTextSegments: number;
	readonly maxOutputChunks: number;
	readonly maxOutputChunkBytes: number;
	readonly maxOutputAudioBytes: number;
	readonly maxEventBacklog: number;
}

export interface RealtimeVoiceHealthDependencyOptions {
	readonly id: string;
	readonly requirement: RuntimeDependencyRequirement;
	readonly maxObservationAgeMs: number;
}

export interface RealtimeVoiceHealthOptions {
	readonly now: () => number;
	readonly recognizer: Readonly<RealtimeVoiceHealthDependencyOptions>;
	readonly synthesizer: Readonly<RealtimeVoiceHealthDependencyOptions>;
}

export interface RealtimeInputProjectionRequest {
	readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
	readonly transcript: string;
}

export interface RealtimeInputProjection {
	readonly inputId: string;
	readonly payload: JsonValue;
	readonly admissionAuthority?: RunAdmissionAuthority;
}

export type RealtimeInputProjector = (
	request: Readonly<RealtimeInputProjectionRequest>,
	signal: AbortSignal,
) => Readonly<RealtimeInputProjection> | PromiseLike<Readonly<RealtimeInputProjection>>;

export interface RealtimeVoiceTurnRequest {
	readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
	readonly inputFormat: Readonly<RealtimeAudioFormat>;
	readonly outputFormat: Readonly<RealtimeAudioFormat>;
	readonly voiceProfileId: string;
	readonly audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	readonly signal?: AbortSignal;
}

export type RealtimeVoiceEvent =
	| { readonly type: "input.transcript.delta"; readonly text: string }
	| { readonly type: "input.transcript.final"; readonly text: string }
	| { readonly type: "agent.started"; readonly coordinate: Readonly<RunCoordinate> }
	| {
			readonly type: "assistant.text.delta";
			readonly itemId: string;
			readonly contentIndex: number;
			readonly text: string;
	  }
	| {
			readonly type: "assistant.audio.chunk";
			readonly sequence: string;
			readonly chunk: Readonly<RealtimeAudioChunk>;
	  }
	| { readonly type: "assistant.audio.completed"; readonly chunks: string; readonly bytes: string }
	| {
			readonly type: "turn.completed";
			readonly status: "completed" | "interrupted" | "failed";
			readonly agentStatus?: "completed" | "aborted" | "failed";
			readonly failure?: RealtimeVoiceFailureKind;
	  };

export type RealtimeVoiceFailureKind =
	| "invalid_audio"
	| "input_capacity"
	| "recognition"
	| "recognition_capacity"
	| "invalid_transcript"
	| "agent"
	| "assistant_capacity"
	| "text_backpressure"
	| "synthesis"
	| "output_capacity"
	| "event_backpressure";

export type RealtimeVoiceTurnResult =
	| {
			readonly status: "completed";
			readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
			readonly agentStatus: "completed";
	  }
	| {
			readonly status: "interrupted";
			readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
			readonly agentStatus?: "completed" | "aborted" | "failed";
	  }
	| {
			readonly status: "failed";
			readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
			readonly failure: RealtimeVoiceFailureKind;
			readonly agentStatus?: "completed" | "aborted" | "failed";
	  };

export interface RealtimeVoiceTurnHandle {
	readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
	readonly events: AsyncIterable<Readonly<RealtimeVoiceEvent>>;
	readonly completion: Promise<Readonly<RealtimeVoiceTurnResult>>;
}

export type RealtimeVoiceInterruptResult =
	| { readonly accepted: true; readonly coordinate: Readonly<RealtimeVoiceCoordinate> }
	| {
			readonly accepted: false;
			readonly reason: "no-active-turn";
	  }
	| {
			readonly accepted: false;
			readonly reason: "coordinate-mismatch";
			readonly activeCoordinate: Readonly<RealtimeVoiceCoordinate>;
	  };

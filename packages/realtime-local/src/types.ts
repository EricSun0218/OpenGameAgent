import type {
	RealtimeAudioChunk,
	RealtimeAudioFormat,
	RealtimeVoiceCoordinate,
	RealtimeVoiceInterruptResult,
	RealtimeVoiceTurnHandle,
	RealtimeVoiceTurnRequest,
} from "@opengameagent/realtime";

export const PCM_S16LE_MEDIA_TYPE = "audio/pcm;format=s16le" as const;

export interface LocalSpeechAudioFormat extends RealtimeAudioFormat {
	readonly mediaType: string;
	readonly sampleRateHz: number;
	readonly channelCount: number;
}

export interface PcmS16LeAudioFormat extends LocalSpeechAudioFormat {
	readonly mediaType: typeof PCM_S16LE_MEDIA_TYPE;
}

export interface VoiceActivityDetectionRequest {
	readonly chunk: Readonly<RealtimeAudioChunk>;
	readonly signal: AbortSignal;
}

export interface RealtimeVoiceActivitySession {
	detect(request: Readonly<VoiceActivityDetectionRequest>): PromiseLike<number> | number;
	close(): PromiseLike<unknown> | unknown;
}

export interface VoiceActivitySessionRequest {
	readonly format: Readonly<PcmS16LeAudioFormat>;
	readonly signal: AbortSignal;
}

export interface RealtimeVoiceActivityDetector {
	open(
		request: Readonly<VoiceActivitySessionRequest>,
	): PromiseLike<RealtimeVoiceActivitySession> | RealtimeVoiceActivitySession;
}

export interface VoiceActivityUtteranceCaptureOptions {
	readonly detector: RealtimeVoiceActivityDetector;
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

export interface VoiceActivitySessionOpenRequest {
	readonly format: Readonly<PcmS16LeAudioFormat>;
	readonly audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	readonly signal: AbortSignal;
}

export interface VoiceActivityUtteranceRequest {
	readonly onSpeechStart?: () => PromiseLike<void> | void;
}

export interface VoiceActivityCaptureRequest extends VoiceActivitySessionOpenRequest, VoiceActivityUtteranceRequest {}

export interface CapturedVoiceUtterance {
	readonly format: Readonly<PcmS16LeAudioFormat>;
	readonly audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	readonly frameCount: number;
	readonly byteLength: number;
}

export interface VoiceActivityUtteranceCapturer {
	capture(request: Readonly<VoiceActivityCaptureRequest>): Promise<Readonly<CapturedVoiceUtterance> | undefined>;
}

export interface VoiceActivityUtteranceSession {
	captureNext(request?: Readonly<VoiceActivityUtteranceRequest>): Promise<Readonly<CapturedVoiceUtterance> | undefined>;
	close(): void;
}

export interface VoiceActivityUtteranceSessionFactory {
	openSession(request: Readonly<VoiceActivitySessionOpenRequest>): Promise<VoiceActivityUtteranceSession>;
}

export interface RealtimeVoiceServicePort {
	start(request: Readonly<RealtimeVoiceTurnRequest>): RealtimeVoiceTurnHandle;
	interrupt(
		expected: Readonly<RealtimeVoiceCoordinate>,
		reason?: unknown,
	): Promise<Readonly<RealtimeVoiceInterruptResult>>;
}

export interface GameLocalSpeechPipelineOptions {
	readonly voice: RealtimeVoiceServicePort;
	readonly capture: VoiceActivityUtteranceCapturer;
}

export interface GameLocalSpeechBargeInRequest {
	readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
	readonly reason?: unknown;
}

export interface GameLocalSpeechTurnRequest {
	readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
	readonly inputFormat: Readonly<PcmS16LeAudioFormat>;
	readonly outputFormat: Readonly<LocalSpeechAudioFormat>;
	readonly voiceProfileId: string;
	readonly audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	readonly signal?: AbortSignal;
	readonly interrupt?: Readonly<GameLocalSpeechBargeInRequest>;
}

export type GameLocalSpeechStartResult =
	| { readonly status: "no-speech" }
	| {
			readonly status: "started";
			readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
			readonly turn: RealtimeVoiceTurnHandle;
			readonly interrupt?: Readonly<RealtimeVoiceInterruptResult>;
	  };

export interface GameContinuousLocalSpeechSessionOptions {
	readonly voice: RealtimeVoiceServicePort;
	readonly endpointing: VoiceActivityUtteranceSessionFactory;
	readonly maxPendingTurns: number;
}

export interface GameContinuousLocalSpeechStartRequest {
	readonly inputFormat: Readonly<PcmS16LeAudioFormat>;
	readonly outputFormat: Readonly<LocalSpeechAudioFormat>;
	readonly voiceProfileId: string;
	readonly audio: AsyncIterable<Readonly<RealtimeAudioChunk>>;
	readonly createCoordinate: (utteranceIndex: number) => Readonly<RealtimeVoiceCoordinate>;
	readonly signal?: AbortSignal;
}

export interface GameContinuousLocalSpeechTurn {
	readonly coordinate: Readonly<RealtimeVoiceCoordinate>;
	readonly turn: RealtimeVoiceTurnHandle;
	readonly interrupt?: Readonly<RealtimeVoiceInterruptResult>;
}

export type GameContinuousLocalSpeechResult = { readonly status: "source-ended" } | { readonly status: "stopped" };

export interface GameContinuousLocalSpeechHandle {
	readonly turns: AsyncIterable<Readonly<GameContinuousLocalSpeechTurn>>;
	readonly result: Promise<Readonly<GameContinuousLocalSpeechResult>>;
	stop(reason?: unknown): void;
}

export interface OpenAICompatibleSpeechCommonOptions {
	readonly baseUrl: string;
	readonly apiKey: string;
	readonly modelId: string;
	readonly requestTimeoutMs: number;
	readonly maxEndpointBytes: number;
	readonly maxCredentialBytes: number;
	readonly maxIdentifierBytes: number;
	readonly maxMediaTypeBytes: number;
	readonly maxResponseBytes: number;
	readonly fetch?: typeof fetch;
}

export interface OpenAICompatibleSpeechRecognizerOptions extends OpenAICompatibleSpeechCommonOptions {
	readonly inputMediaType: typeof PCM_S16LE_MEDIA_TYPE;
	readonly language?: string;
	readonly maxInputChunks: number;
	readonly maxInputChunkBytes: number;
	readonly maxInputAudioBytes: number;
	readonly maxTranscriptBytes: number;
}

export type OpenAISpeechResponseFormat = "mp3" | "opus" | "aac" | "flac" | "wav" | "pcm";

export interface OpenAICompatibleSpeechSynthesizerOptions extends OpenAICompatibleSpeechCommonOptions {
	readonly outputFormat: Readonly<LocalSpeechAudioFormat>;
	readonly responseFormat: OpenAISpeechResponseFormat;
	readonly maxTextSegments: number;
	readonly maxTextBytes: number;
	readonly maxResponseChunks: number;
	readonly maxResponseChunkBytes: number;
}

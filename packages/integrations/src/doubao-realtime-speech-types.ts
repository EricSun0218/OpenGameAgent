import type { CredentialStore } from "@earendil-works/pi-ai";
import type { RealtimeAudioFormat } from "@opengameagent/realtime";

export interface DoubaoWebSocketConnectRequest {
	readonly endpoint: string;
	readonly headers: Readonly<Record<string, string>>;
	readonly maxFrameBytes: number;
	readonly maxPendingFrames: number;
	readonly maxWebSocketFragments: number;
	readonly maxWebSocketBufferedChunks: number;
	readonly connectTimeoutMs: number;
	readonly closeTimeoutMs: number;
}

export type DoubaoWebSocketMessage =
	| { readonly type: "binary"; readonly bytes: Uint8Array }
	| { readonly type: "text"; readonly text: string }
	| { readonly type: "closed"; readonly code: number };

export interface DoubaoWebSocketConnection {
	bufferedAmount(): number;
	send(frame: Readonly<Uint8Array>, signal: AbortSignal): PromiseLike<void> | void;
	receive(signal: AbortSignal): PromiseLike<Readonly<DoubaoWebSocketMessage>> | Readonly<DoubaoWebSocketMessage>;
	close(signal: AbortSignal): PromiseLike<void> | void;
	abort(reason?: unknown): void;
}

export type DoubaoWebSocketConnector = (
	request: Readonly<DoubaoWebSocketConnectRequest>,
	signal: AbortSignal,
) => PromiseLike<DoubaoWebSocketConnection> | DoubaoWebSocketConnection;

export interface DoubaoRealtimeSpeechTransportLimits {
	readonly maxEndpointBytes: number;
	readonly maxCredentialReferenceBytes: number;
	readonly maxCredentialBytes: number;
	readonly maxAppKeyBytes: number;
	readonly maxResourceIdBytes: number;
	readonly maxIdentifierBytes: number;
	readonly maxMediaTypeBytes: number;
	readonly maxFrameBytes: number;
	readonly maxPayloadBytes: number;
	readonly maxJsonDepth: number;
	readonly maxPendingFrames: number;
	readonly maxWebSocketFragments: number;
	readonly maxWebSocketBufferedChunks: number;
	readonly maxBufferedAmountBytes: number;
	readonly connectTimeoutMs: number;
	readonly messageTimeoutMs: number;
	readonly operationTimeoutMs: number;
	readonly closeTimeoutMs: number;
}

export interface DoubaoAsrLimits extends DoubaoRealtimeSpeechTransportLimits {
	readonly maxInputChunks: number;
	readonly maxInputChunkBytes: number;
	readonly maxInputAudioBytes: number;
	readonly maxRecognitionEvents: number;
	readonly maxRecognitionTextBytes: number;
}

export interface DoubaoTtsLimits extends DoubaoRealtimeSpeechTransportLimits {
	readonly maxTextSegments: number;
	readonly maxTextSegmentBytes: number;
	readonly maxTextBytes: number;
	readonly maxOutputChunks: number;
	readonly maxOutputChunkBytes: number;
	readonly maxOutputAudioBytes: number;
}

export type DoubaoAuthentication = { readonly type: "api-key" } | { readonly type: "legacy"; readonly appKey: string };

export interface CreateDoubaoRealtimeSpeechRecognizerOptions {
	readonly credentials: CredentialStore;
	readonly credentialReference: string;
	readonly endpoint: string;
	readonly authentication: Readonly<DoubaoAuthentication>;
	readonly resourceId: string;
	readonly userId: string;
	readonly modelName: string;
	readonly language: string;
	readonly inputFormat: Readonly<RealtimeAudioFormat>;
	readonly limits: Readonly<DoubaoAsrLimits>;
	readonly connect?: DoubaoWebSocketConnector;
}

export interface CreateDoubaoRealtimeSpeechSynthesizerOptions {
	readonly credentials: CredentialStore;
	readonly credentialReference: string;
	readonly endpoint: string;
	readonly authentication: Readonly<DoubaoAuthentication>;
	readonly resourceId: string;
	readonly outputFormat: Readonly<RealtimeAudioFormat>;
	readonly limits: Readonly<DoubaoTtsLimits>;
	readonly createSessionId: () => string;
	readonly connect?: DoubaoWebSocketConnector;
}

export type DoubaoRealtimeSpeechAdapterErrorCode =
	| "invalid_configuration"
	| "credential"
	| "capacity"
	| "protocol"
	| "transport"
	| "timeout";

export class DoubaoRealtimeSpeechAdapterError extends Error {
	constructor(readonly code: DoubaoRealtimeSpeechAdapterErrorCode) {
		super(`Doubao realtime speech adapter failed: ${code}.`);
		this.name = "DoubaoRealtimeSpeechAdapterError";
	}
}

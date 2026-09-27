import type { ImageContent, TextContent } from "@earendil-works/pi-ai";
import type { ImageReferenceContent, MessageContent, RunCoordinate, StreamKey } from "@opengameagent/protocol";

export type TranscriptPiMediaContent = TextContent | ImageContent;

export interface TranscriptMediaReadRequest {
	readonly stream: Readonly<StreamKey>;
	readonly role: "user" | "toolResult";
	readonly reference: Readonly<ImageReferenceContent>;
	readonly toolCallId?: string;
	readonly toolName?: string;
}

export interface TranscriptToolResultMediaRequest {
	readonly stream: Readonly<StreamKey>;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly toolCallId: string;
	readonly toolName: string;
	readonly content: readonly TranscriptPiMediaContent[];
}

export interface PreparedTranscriptToolResultMedia {
	readonly content: readonly TranscriptPiMediaContent[];
	readonly durableContent: readonly MessageContent[];
}

/**
 * Host Adapter for immutable transcript media.
 *
 * Implementations admit final Tool images into durable storage and authorize/read
 * references as one batch. Pi content and durable references must remain aligned.
 */
export interface TranscriptMedia {
	captureToolResult(
		request: Readonly<TranscriptToolResultMediaRequest>,
		signal: AbortSignal,
	): Promise<Readonly<PreparedTranscriptToolResultMedia>>;
	read(
		requests: readonly Readonly<TranscriptMediaReadRequest>[],
		signal: AbortSignal,
	): Promise<readonly Readonly<ImageContent>[]>;
}

export class TranscriptUnsupportedMediaError extends Error {
	constructor(message = "The durable transcript requires a media Adapter for image content.") {
		super(message);
		this.name = "TranscriptUnsupportedMediaError";
	}
}

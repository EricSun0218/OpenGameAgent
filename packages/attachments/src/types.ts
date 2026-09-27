import type {
	GameInputProjectionLimits,
	GameInputProjector,
	NormalizedGameInput,
	TranscriptMediaReadRequest,
} from "@opengameagent/agent";
import type { ImageMediaType, ImageReferenceContent, MessageContent, RunCoordinate } from "@opengameagent/protocol";

export interface ImageAttachmentLimits {
	readonly maxInputBytes: number;
	readonly maxInputPixels: number;
	readonly maxInputDimension: number;
	readonly maxStoredBytes: number;
	readonly maxStoredPixels: number;
	readonly maxStoredDimension: number;
	readonly maxReadBytes: number;
	readonly maxNameBytes: number;
	readonly maxImagesPerBatch: number;
	readonly maxBatchInputBytes: number;
	readonly maxBatchStoredBytes: number;
}

export type ImageAttachmentInput =
	| {
			readonly encoding: "bytes";
			readonly data: Uint8Array;
			readonly mediaType: ImageMediaType;
			readonly name?: string;
	  }
	| {
			readonly encoding: "base64";
			readonly data: string;
			readonly mediaType: ImageMediaType;
			readonly name?: string;
	  };

export interface ImageAttachmentReader {
	read(reference: Readonly<ImageReferenceContent>, signal: AbortSignal): Promise<Uint8Array>;
}

export interface CanonicalPngAttachmentStore extends ImageAttachmentReader {
	publishCanonicalPng(bytes: Uint8Array, signal: AbortSignal): Promise<Readonly<ImageReferenceContent>>;
}

export interface ImageAttachmentStore extends ImageAttachmentReader {
	admit(
		inputs: readonly Readonly<ImageAttachmentInput>[],
		signal: AbortSignal,
	): Promise<readonly ImageReferenceContent[]>;
}

export type ImageAttachmentSelector = (
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
	coordinate: Readonly<RunCoordinate>,
) => readonly MessageContent[] | Promise<readonly MessageContent[]>;

export type ImageAttachmentAuthorizer = (
	input: Readonly<NormalizedGameInput>,
	coordinate: Readonly<RunCoordinate>,
	reference: Readonly<ImageReferenceContent>,
	signal: AbortSignal,
) => void | Promise<void>;

export interface ImageAttachmentInputProjectorOptions {
	readonly store: ImageAttachmentReader;
	readonly select: ImageAttachmentSelector;
	readonly authorize: ImageAttachmentAuthorizer;
	readonly limits: GameInputProjectionLimits;
	readonly now?: () => number;
}

export interface ImageAttachmentInputProjection {
	readonly project: GameInputProjector;
	readonly limits: Readonly<GameInputProjectionLimits>;
}

export type ImageAttachmentTranscriptMediaAuthorizer = (
	request: Readonly<TranscriptMediaReadRequest>,
	signal: AbortSignal,
) => void | Promise<void>;

export interface ImageAttachmentTranscriptMediaOptions {
	readonly store: ImageAttachmentStore;
	readonly authorize: ImageAttachmentTranscriptMediaAuthorizer;
	readonly maxRestoreMediaBytes: number;
}

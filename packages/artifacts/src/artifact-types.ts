import type { NormalizedGameInput } from "@opengameagent/agent";
import type { RunCoordinate, StreamKey } from "@opengameagent/protocol";

export interface ArtifactLimits {
	readonly maxIdentifierBytes: number;
	readonly maxCounterDigits: number;
	readonly maxContentTypeBytes: number;
	readonly maxArtifactBytes: number;
	readonly maxArtifactsPerStream: number;
	readonly maxStoredBytesPerStream: number;
	readonly maxInlineToolResultBytes: number;
	readonly maxReadBytes: number;
	readonly maxModelResultBytes: number;
	readonly maxPurgeBatch: number;
}

export interface ArtifactOrigin {
	readonly inputId: string;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly toolCallId: string;
	readonly toolName: string;
	readonly purpose: string;
	readonly requestFingerprint: string;
}

export interface ArtifactRecord extends StreamKey {
	readonly artifactId: string;
	readonly contentFingerprint: string;
	readonly contentType: string;
	readonly content: string;
	readonly byteLength: number;
	readonly createdAt: string;
	readonly origin: Readonly<ArtifactOrigin>;
}

export interface ArtifactWriteRequest {
	readonly input: Readonly<NormalizedGameInput>;
	readonly coordinate: Readonly<RunCoordinate>;
	readonly toolCallId: string;
	readonly toolName: string;
	readonly purpose: string;
	readonly requestFingerprint: string;
	readonly contentType: string;
	readonly content: string;
}

export interface ArtifactPage {
	readonly artifactId: string;
	readonly contentType: string;
	readonly offsetBytes: number;
	readonly content: string;
	readonly contentBytes: number;
	readonly totalBytes: number;
	readonly nextOffsetBytes?: number;
}

export interface ArtifactPutResult {
	readonly status: "created" | "existing";
	readonly record: Readonly<ArtifactRecord>;
}

export interface ArtifactStore {
	readonly limits: Readonly<ArtifactLimits>;
	put(record: Readonly<ArtifactRecord>, signal?: AbortSignal): Promise<ArtifactPutResult>;
	read(stream: Readonly<StreamKey>, artifactId: string, signal?: AbortSignal): Promise<ArtifactRecord | undefined>;
	purge(stream: Readonly<StreamKey>, createdBefore: string, limit: number, signal?: AbortSignal): Promise<number>;
}

export interface ArtifactServiceOptions {
	readonly store: ArtifactStore;
	readonly limits: ArtifactLimits;
	readonly now: () => string;
	readonly sourceId?: string;
	readonly readToolName?: string;
}

export interface ArtifactToolDetails {
	readonly artifactId?: string;
	readonly artifactStatus: "read" | "not_found";
}

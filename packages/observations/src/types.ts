import type { GameContextLimits, GameContextReference, NormalizedGameInput } from "@opengameagent/agent";
import type { CanonicalPngAttachmentStore, ImageAttachmentAuthorizer } from "@opengameagent/attachments";
import type { ImageReferenceContent, RunCoordinate } from "@opengameagent/protocol";

export interface VisualObservationOriginalTransform {
	readonly kind: "original";
}

export interface VisualObservationCrop {
	readonly left: number;
	readonly top: number;
	readonly width: number;
	readonly height: number;
}

export interface VisualObservationCropResizeTransform {
	readonly kind: "crop-resize";
	readonly crop?: VisualObservationCrop;
	readonly targetWidth: number;
	readonly targetHeight: number;
}

interface VisualObservationCandidateBase {
	readonly viewId: string;
	readonly required: boolean;
	readonly contextReferenceId: string;
	readonly source: Readonly<ImageReferenceContent>;
}

export interface VisualObservationOriginalCandidate extends VisualObservationCandidateBase {
	readonly transform: VisualObservationOriginalTransform;
}

export interface VisualObservationDerivedCandidate extends VisualObservationCandidateBase {
	readonly transform: VisualObservationCropResizeTransform;
	readonly maxOutputBytes: number;
}

export type VisualObservationCandidate = VisualObservationOriginalCandidate | VisualObservationDerivedCandidate;

export interface VisualObservationSnapshot {
	readonly observationVersion: "1";
	readonly revision: string;
	readonly references: readonly Readonly<GameContextReference>[];
	readonly candidates: readonly Readonly<VisualObservationCandidate>[];
}

export type VisualObservationCapture = (
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
	coordinate: Readonly<RunCoordinate>,
) => VisualObservationSnapshot | undefined | Promise<VisualObservationSnapshot | undefined>;

export interface VisualObservationContextSourceOptions {
	readonly sourceId: string;
	readonly selectionReferenceId: string;
	readonly capture: VisualObservationCapture;
	readonly authorize: ImageAttachmentAuthorizer;
	readonly store: CanonicalPngAttachmentStore;
	readonly projectionLimits: GameContextLimits;
	readonly maxCandidatesPerSnapshot: number;
	readonly maxTransformCacheEntries: number;
}

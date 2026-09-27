import { constants as bufferConstants } from "node:buffer";
import { createHash } from "node:crypto";
import {
	assertOpaqueIdentifier,
	type ContextSnapshot,
	canonicalizeJsonValue,
	type JsonValue,
	normalizeJsonValue,
	type RunCoordinate,
} from "@opengameagent/protocol";
import {
	AlignedImageProjectionError,
	captureAlignedImageProjection,
	type PreparedAlignedImage,
} from "./image-projection.js";
import { modelContextSourceKind } from "./model-context-provenance.js";
import type {
	GameContextContribution,
	GameContextImage,
	GameContextLimits,
	GameContextReference,
	GameContextSource,
	NormalizedGameInput,
} from "./types.js";
const encoder = new TextEncoder();
export class GameContextSnapshotError extends TypeError {
	constructor(
		public readonly code:
			| "invalid_context_configuration"
			| "duplicate_context_source"
			| "invalid_context_snapshot"
			| "duplicate_context_reference"
			| "context_reference_limit_exceeded"
			| "context_snapshot_byte_limit_exceeded"
			| "context_media_limits_unavailable"
			| "context_content_part_limit_exceeded"
			| "context_image_byte_limit_exceeded"
			| "context_image_integrity_mismatch"
			| "context_image_input_unsupported",
	) {
		super(code);
		this.name = "GameContextSnapshotError";
	}
}
export interface PreparedGameContextImage extends PreparedAlignedImage {
	readonly sourceId: string;
	readonly contextReferenceId: string;
	readonly revision: string;
}
export interface PreparedGameContextSnapshot {
	readonly text: string;
	readonly images: readonly PreparedGameContextImage[];
	readonly provenance: ContextSnapshot;
}
interface NormalizedReference extends GameContextReference {
	readonly sourceId: string;
	readonly content: JsonValue;
}
export interface LoadedGameContextSource {
	readonly sourceId: string;
	readonly provenanceKind: NonNullable<GameContextSource["provenanceKind"]>;
	readonly references: readonly GameContextReference[];
	readonly images: NonNullable<GameContextContribution["images"]>;
}
export function captureContextLimits(limits: GameContextLimits | undefined): Readonly<GameContextLimits> | undefined {
	if (limits === undefined) return undefined;
	const maxReferencesPerSnapshot = positive(limits.maxReferencesPerSnapshot);
	const maxSnapshotBytes = positive(limits.maxSnapshotBytes);
	const value = limits.media;
	const media =
		value === undefined
			? undefined
			: Object.freeze({
					maxContentParts: positive(value.maxContentParts),
					maxImageBytes: positiveBuffer(value.maxImageBytes),
					maxTotalImageBytes: positiveBuffer(value.maxTotalImageBytes),
				});
	if (media && media.maxImageBytes > media.maxTotalImageBytes) invalidConfiguration();
	return Object.freeze({ maxReferencesPerSnapshot, maxSnapshotBytes, ...(media ? { media } : {}) });
}
export function validateContextConfiguration(
	sources: readonly GameContextSource[] | undefined,
	limits: GameContextLimits | undefined,
	allowLimitsWithoutStaticSources = false,
): readonly GameContextSource[] {
	if (!sources || sources.length === 0) {
		if (limits !== undefined && !allowLimitsWithoutStaticSources) invalidConfiguration();
		return [];
	}
	if (!limits) invalidConfiguration();
	const ids = new Set<string>();
	const captured: GameContextSource[] = [];
	for (const source of sources) {
		const sourceId = source.sourceId;
		assertOpaqueIdentifier(sourceId);
		if (ids.has(sourceId)) throw new GameContextSnapshotError("duplicate_context_source");
		ids.add(sourceId);
		if (typeof source.load !== "function") invalidConfiguration();
		const load = source.load.bind(source);
		captured.push({
			sourceId,
			provenanceKind: modelContextSourceKind(source.provenanceKind, "context"),
			load: (input, signal, coordinate) => load(input, signal, coordinate),
		});
	}
	return Object.freeze(captured);
}
export function captureGameContextContribution(value: unknown): Readonly<GameContextContribution> {
	if (!isRecord(value)) throw new GameContextSnapshotError("invalid_context_snapshot");
	const references = value.references;
	const images = value.images;
	if (!Array.isArray(references) || (images !== undefined && !Array.isArray(images))) invalidSnapshot();
	return Object.freeze({
		references: captureReferences(references),
		...(images === undefined ? {} : { images: captureImages(images) }),
	});
}
export async function loadGameContextSources(
	sources: readonly GameContextSource[],
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
	coordinate: Readonly<RunCoordinate>,
): Promise<readonly LoadedGameContextSource[]> {
	const loaded: LoadedGameContextSource[] = [];
	for (const source of sources) {
		signal.throwIfAborted();
		const contribution = captureGameContextContribution(await source.load(input, signal, coordinate));
		loaded.push({
			sourceId: source.sourceId,
			provenanceKind: source.provenanceKind ?? "context",
			references: contribution.references,
			images: contribution.images ?? [],
		});
	}
	return loaded;
}
export function prepareLoadedGameContextSnapshot(
	loadedSources: readonly LoadedGameContextSource[],
	limits: Readonly<GameContextLimits>,
	signal: AbortSignal,
): PreparedGameContextSnapshot | undefined {
	const references: NormalizedReference[] = [];
	const images: PreparedGameContextImage[] = [];
	let total = 0;
	for (const source of loadedSources) {
		signal.throwIfAborted();
		const revisions = new Map<string, string>();
		for (const reference of source.references) {
			if (references.length >= limits.maxReferencesPerSnapshot)
				throw new GameContextSnapshotError("context_reference_limit_exceeded");
			assertOpaqueIdentifier(reference.referenceId);
			assertOpaqueIdentifier(reference.revision);
			if (revisions.has(reference.referenceId)) throw new GameContextSnapshotError("duplicate_context_reference");
			revisions.set(reference.referenceId, reference.revision);
			references.push({
				sourceId: source.sourceId,
				referenceId: reference.referenceId,
				revision: reference.revision,
				content: reference.content,
			});
		}
		for (const image of source.images) {
			const media = limits.media;
			if (!media) throw new GameContextSnapshotError("context_media_limits_unavailable");
			if (images.length + 2 > media.maxContentParts)
				throw new GameContextSnapshotError("context_content_part_limit_exceeded");
			if (!isRecord(image) || typeof image.contextReferenceId !== "string")
				throw new GameContextSnapshotError("invalid_context_snapshot");
			assertOpaqueIdentifier(image.contextReferenceId);
			const revision = revisions.get(image.contextReferenceId);
			if (!revision) throw new GameContextSnapshotError("invalid_context_snapshot");
			let prepared: PreparedAlignedImage;
			try {
				prepared = captureAlignedImageProjection(image.content, image.reference, media.maxImageBytes);
			} catch (error) {
				if (error instanceof AlignedImageProjectionError)
					throw new GameContextSnapshotError(
						error.code === "image_integrity_mismatch" ? "context_image_integrity_mismatch" : "invalid_context_snapshot",
					);
				throw error;
			}
			if (prepared.bytes > media.maxTotalImageBytes - total)
				throw new GameContextSnapshotError("context_image_byte_limit_exceeded");
			total += prepared.bytes;
			images.push(
				Object.freeze({
					...prepared,
					sourceId: source.sourceId,
					contextReferenceId: image.contextReferenceId,
					revision,
				}),
			);
		}
	}
	signal.throwIfAborted();
	if (references.length === 0) return undefined;
	const text = canonicalizeJsonValue({
		contextVersion: "1",
		kind: "game-reference-context",
		references: references.map((r) => ({
			sourceId: r.sourceId,
			referenceId: r.referenceId,
			revision: r.revision,
			content: r.content,
		})),
	});
	const proof = canonicalizeJsonValue({
		text,
		images: images.map(({ sourceId, contextReferenceId, revision, reference }) => ({
			sourceId,
			contextReferenceId,
			revision,
			reference,
		})),
	});
	if (encoder.encode(proof).byteLength > limits.maxSnapshotBytes)
		throw new GameContextSnapshotError("context_snapshot_byte_limit_exceeded");
	const provenanceReferences = Object.freeze(
		references.map((r) =>
			Object.freeze({
				sourceId: r.sourceId,
				referenceId: r.referenceId,
				revision: r.revision,
				contentFingerprint: fingerprint(canonicalizeJsonValue(r.content)),
			}),
		),
	) as unknown as ContextSnapshot["references"];
	return Object.freeze({
		text,
		images: Object.freeze(images),
		provenance: Object.freeze({
			contextVersion: "1",
			fingerprint: fingerprint(proof),
			references: provenanceReferences,
		}),
	});
}
export async function prepareGameContextSnapshot(
	sources: readonly GameContextSource[],
	limits: Readonly<GameContextLimits>,
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
	coordinate: Readonly<RunCoordinate>,
): Promise<PreparedGameContextSnapshot | undefined> {
	return prepareLoadedGameContextSnapshot(
		await loadGameContextSources(sources, input, signal, coordinate),
		limits,
		signal,
	);
}
function fingerprint(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}
function positive(value: number): number {
	if (!Number.isSafeInteger(value) || value < 1) invalidConfiguration();
	return value;
}
function positiveBuffer(value: number): number {
	const checked = positive(value);
	if (checked > bufferConstants.MAX_LENGTH) invalidConfiguration();
	return checked;
}
function invalidConfiguration(): never {
	throw new GameContextSnapshotError("invalid_context_configuration");
}
function invalidSnapshot(): never {
	throw new GameContextSnapshotError("invalid_context_snapshot");
}
function captureReferences(values: readonly unknown[]): readonly GameContextReference[] {
	const captured: GameContextReference[] = [];
	for (const value of values) {
		if (!isRecord(value)) invalidSnapshot();
		const referenceId = value.referenceId;
		const revision = value.revision;
		const content = value.content;
		if (typeof referenceId !== "string" || typeof revision !== "string") invalidSnapshot();
		captured.push(Object.freeze({ referenceId, revision, content: normalizeJsonValue(content) }));
	}
	return Object.freeze(captured);
}
function captureImages(values: readonly unknown[]): readonly GameContextImage[] {
	const captured: GameContextImage[] = [];
	for (const value of values) {
		if (!isRecord(value)) invalidSnapshot();
		const contextReferenceId = value.contextReferenceId;
		const content = value.content;
		const reference = value.reference;
		if (typeof contextReferenceId !== "string" || !isRecord(content) || !isRecord(reference)) invalidSnapshot();
		const capturedContent = Object.freeze({ type: content.type, data: content.data, mimeType: content.mimeType });
		const capturedReference = Object.freeze({
			type: reference.type,
			attachmentId: reference.attachmentId,
			mediaType: reference.mediaType,
			bytes: reference.bytes,
			width: reference.width,
			height: reference.height,
		});
		captured.push(
			Object.freeze({
				contextReferenceId,
				content: capturedContent as unknown as GameContextImage["content"],
				reference: capturedReference as unknown as GameContextImage["reference"],
			}),
		);
	}
	return Object.freeze(captured);
}
function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type {
	GameContextContribution,
	GameContextImage,
	GameContextLimits,
	GameContextReference,
	GameContextSource,
	NormalizedGameInput,
} from "@opengameagent/agent";
import { captureContextLimits, GameContextSnapshotError } from "@opengameagent/agent";
import { ImageAttachmentError, renderCanonicalPng } from "@opengameagent/attachments";
import {
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type ImageReferenceContent,
	ImageReferenceContentSchema,
	normalizeJsonValue,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { LRUCache } from "lru-cache";
import sharp from "sharp";
import { Value } from "typebox/value";
import { VisualObservationError } from "./errors.js";
import type {
	VisualObservationContextSourceOptions,
	VisualObservationDerivedCandidate,
	VisualObservationOriginalCandidate,
} from "./types.js";

interface CapturedSnapshot {
	readonly revision: string;
	readonly references: readonly GameContextReference[];
	readonly candidates: readonly CapturedCandidate[];
}

interface CapturedCandidateBase {
	readonly position: number;
	readonly viewId: string;
	readonly required: boolean;
	readonly contextReferenceId: string;
	readonly source: Readonly<ImageReferenceContent>;
}

interface CapturedOriginalCandidate extends CapturedCandidateBase {
	readonly transform: Readonly<VisualObservationOriginalCandidate["transform"]>;
}

interface CapturedDerivedCandidate extends CapturedCandidateBase {
	readonly transform: Readonly<VisualObservationDerivedCandidate["transform"]>;
	readonly maxOutputBytes: number;
}

type CapturedCandidate = CapturedOriginalCandidate | CapturedDerivedCandidate;

interface SelectedCandidate {
	readonly candidate: CapturedCandidate;
	readonly selected: boolean;
}

const rendererFingerprint = createHash("sha256")
	.update(
		canonicalizeJsonValue({
			transformVersion: "1",
			sharp: sharp.versions.sharp,
			vips: sharp.versions.vips,
			png: sharp.versions.png,
			zlib: sharp.versions["zlib-ng"],
			platform: process.platform,
			architecture: process.arch,
		}),
	)
	.digest("hex");

const emptyContribution: Readonly<GameContextContribution> = Object.freeze({ references: Object.freeze([]) });

export function createVisualObservationContextSource(
	options: VisualObservationContextSourceOptions,
): GameContextSource {
	if (!options) invalidConfiguration();
	const sourceId = options.sourceId;
	const selectionReferenceId = options.selectionReferenceId;
	const capture = options.capture;
	const authorize = options.authorize;
	const store = options.store;
	const rawRead = store?.read;
	const rawPublishCanonicalPng = store?.publishCanonicalPng;
	const projectionLimits = captureProjectionLimits(options.projectionLimits);
	const maxCandidatesPerSnapshot = positive(options.maxCandidatesPerSnapshot);
	const maxTransformCacheEntries = positive(options.maxTransformCacheEntries);
	try {
		assertOpaqueIdentifier(sourceId);
		assertOpaqueIdentifier(selectionReferenceId);
	} catch {
		invalidConfiguration();
	}
	if (
		typeof capture !== "function" ||
		typeof authorize !== "function" ||
		!store ||
		typeof rawRead !== "function" ||
		typeof rawPublishCanonicalPng !== "function"
	) {
		invalidConfiguration();
	}
	const cache = new LRUCache<string, Readonly<ImageReferenceContent>>({ max: maxTransformCacheEntries });
	const read = rawRead.bind(store);
	const publishCanonicalPng = rawPublishCanonicalPng.bind(store);

	const load: GameContextSource["load"] = async (
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
		coordinate: Readonly<RunCoordinate>,
	): Promise<GameContextContribution> => {
		signal.throwIfAborted();
		const value = await awaitHost(() => capture(input, signal, coordinate), signal);
		if (value === undefined) return emptyContribution;
		const snapshot = captureSnapshot(value, selectionReferenceId, maxCandidatesPerSnapshot, projectionLimits);
		const selected = selectCandidates(snapshot.candidates, projectionLimits);
		const images: GameContextImage[] = [];
		const views: Array<Record<string, unknown>> = [];
		for (const entry of selected) {
			const candidate = entry.candidate;
			if (!entry.selected) {
				views.push(selectionView(candidate, "omitted"));
				continue;
			}
			signal.throwIfAborted();
			await awaitHost(() => authorize(input, coordinate, candidate.source, signal), signal);
			const sourceBytes = Buffer.from(await awaitHost(() => read(candidate.source, signal), signal));
			const output = !isDerivedCandidate(candidate)
				? { reference: withoutName(candidate.source), bytes: sourceBytes }
				: await projectDerived(
						candidate,
						sourceBytes,
						input,
						coordinate,
						signal,
						authorize,
						read,
						publishCanonicalPng,
						cache,
					);
			const { reference, bytes } = output;
			images.push(
				Object.freeze({
					contextReferenceId: candidate.contextReferenceId,
					content: Object.freeze({
						type: "image" as const,
						mimeType: "image/png" as const,
						data: bytes.toString("base64"),
					}),
					reference,
				}),
			);
			views.push(selectionView(candidate, "selected", reference.attachmentId));
		}
		const selectionReference = Object.freeze({
			referenceId: selectionReferenceId,
			revision: snapshot.revision,
			content: normalizeJsonValue({
				observationVersion: "1",
				revision: snapshot.revision,
				rendererFingerprint,
				views,
			}),
		});
		return Object.freeze({
			references: Object.freeze([...snapshot.references, selectionReference]),
			...(images.length === 0 ? {} : { images: Object.freeze(images) }),
		});
	};

	return Object.freeze({
		sourceId,
		provenanceKind: "observation" as const,
		load,
	});
}

function captureSnapshot(
	value: unknown,
	selectionReferenceId: string,
	maxCandidates: number,
	limits: Readonly<GameContextLimits>,
): CapturedSnapshot {
	if (!isRecord(value)) invalidSnapshot();
	const observationVersion = value.observationVersion;
	const revision = value.revision;
	const rawReferences = value.references;
	const rawCandidates = value.candidates;
	if (!Array.isArray(rawReferences) || !Array.isArray(rawCandidates)) invalidSnapshot();
	const referenceCount = rawReferences.length;
	const candidateCount = rawCandidates.length;
	if (
		observationVersion !== "1" ||
		typeof revision !== "string" ||
		candidateCount > maxCandidates ||
		referenceCount + 1 > limits.maxReferencesPerSnapshot
	) {
		invalidSnapshot();
	}
	assertSnapshotIdentifier(revision);
	const referenceIds = new Set<string>();
	const references: GameContextReference[] = [];
	for (let position = 0; position < referenceCount; position += 1) {
		const raw = rawReferences[position];
		if (!isRecord(raw)) invalidSnapshot();
		const referenceId = raw.referenceId;
		const referenceRevision = raw.revision;
		const content = raw.content;
		if (typeof referenceId !== "string" || typeof referenceRevision !== "string") invalidSnapshot();
		assertSnapshotIdentifier(referenceId);
		assertSnapshotIdentifier(referenceRevision);
		if (referenceId === selectionReferenceId || referenceIds.has(referenceId)) invalidSnapshot();
		referenceIds.add(referenceId);
		references.push(Object.freeze({ referenceId, revision: referenceRevision, content: captureJson(content) }));
	}
	const viewIds = new Set<string>();
	const candidates: CapturedCandidate[] = [];
	for (let position = 0; position < candidateCount; position += 1) {
		const candidate = captureCandidate(rawCandidates[position], position, referenceIds);
		if (viewIds.has(candidate.viewId)) invalidSnapshot();
		viewIds.add(candidate.viewId);
		candidates.push(candidate);
	}
	return Object.freeze({
		revision,
		references: Object.freeze(references),
		candidates: Object.freeze(candidates),
	});
}

function captureCandidate(value: unknown, position: number, referenceIds: ReadonlySet<string>): CapturedCandidate {
	if (!isRecord(value)) invalidSnapshot();
	const viewId = value.viewId;
	const required = value.required;
	const contextReferenceId = value.contextReferenceId;
	const source = value.source;
	const transform = value.transform;
	if (
		typeof viewId !== "string" ||
		typeof required !== "boolean" ||
		typeof contextReferenceId !== "string" ||
		!isRecord(transform)
	) {
		invalidSnapshot();
	}
	assertSnapshotIdentifier(viewId);
	assertSnapshotIdentifier(contextReferenceId);
	if (!referenceIds.has(contextReferenceId)) invalidSnapshot();
	const capturedSource = captureImageReference(source, invalidSnapshot);
	if (transform.kind === "original") {
		return Object.freeze({
			position,
			viewId,
			required,
			contextReferenceId,
			source: capturedSource,
			transform: Object.freeze({ kind: "original" as const }),
		});
	}
	if (transform.kind !== "crop-resize") invalidSnapshot();
	const targetWidth = snapshotPositive(transform.targetWidth);
	const targetHeight = snapshotPositive(transform.targetHeight);
	const rawCrop = transform.crop;
	let crop: VisualObservationDerivedCandidate["transform"]["crop"];
	if (rawCrop !== undefined) {
		if (!isRecord(rawCrop)) invalidSnapshot();
		const left = snapshotNonNegative(rawCrop.left);
		const top = snapshotNonNegative(rawCrop.top);
		const width = snapshotPositive(rawCrop.width);
		const height = snapshotPositive(rawCrop.height);
		if (left > capturedSource.width - width || top > capturedSource.height - height) invalidSnapshot();
		crop = Object.freeze({ left, top, width, height });
	}
	const maxOutputBytes = snapshotPositive(value.maxOutputBytes);
	return Object.freeze({
		position,
		viewId,
		required,
		contextReferenceId,
		source: capturedSource,
		transform: Object.freeze({
			kind: "crop-resize" as const,
			...(crop === undefined ? {} : { crop }),
			targetWidth,
			targetHeight,
		}),
		maxOutputBytes,
	});
}

function selectCandidates(
	candidates: readonly CapturedCandidate[],
	limits: Readonly<GameContextLimits>,
): readonly SelectedCandidate[] {
	const media = limits.media;
	const maxImages = media ? Math.max(0, media.maxContentParts - 1) : 0;
	let reservedImages = 0;
	let reservedBytes = 0;
	const selected = new Set<number>();
	for (const candidate of candidates) {
		if (!candidate.required) continue;
		const reservation = reservationBytes(candidate);
		if (
			!media ||
			reservedImages >= maxImages ||
			reservation > media.maxImageBytes ||
			reservation > media.maxTotalImageBytes - reservedBytes
		) {
			throw new VisualObservationError("required_budget_exceeded");
		}
		selected.add(candidate.position);
		reservedImages += 1;
		reservedBytes += reservation;
	}
	for (const candidate of candidates) {
		if (candidate.required) continue;
		const reservation = reservationBytes(candidate);
		if (
			media &&
			reservedImages < maxImages &&
			reservation <= media.maxImageBytes &&
			reservation <= media.maxTotalImageBytes - reservedBytes
		) {
			selected.add(candidate.position);
			reservedImages += 1;
			reservedBytes += reservation;
		}
	}
	return Object.freeze(
		candidates.map((candidate) => Object.freeze({ candidate, selected: selected.has(candidate.position) })),
	);
}

async function projectDerived(
	candidate: CapturedDerivedCandidate,
	sourceBytes: Buffer,
	input: Parameters<VisualObservationContextSourceOptions["authorize"]>[0],
	coordinate: Parameters<VisualObservationContextSourceOptions["authorize"]>[1],
	signal: AbortSignal,
	authorize: VisualObservationContextSourceOptions["authorize"],
	read: CanonicalRead,
	publishCanonicalPng: CanonicalPublish,
	cache: LRUCache<string, Readonly<ImageReferenceContent>>,
): Promise<{ readonly reference: Readonly<ImageReferenceContent>; readonly bytes: Buffer }> {
	const key = transformCacheKey(candidate);
	const cached = cache.get(key);
	if (cached && cached.bytes > candidate.maxOutputBytes) {
		throw new VisualObservationError("transform_output_too_large");
	}
	if (cached) {
		await awaitHost(() => authorize(input, coordinate, cached, signal), signal);
		try {
			const bytes = Buffer.from(await awaitHost(() => read(cached, signal), signal));
			return { reference: cached, bytes };
		} catch (error) {
			signal.throwIfAborted();
			if (!staleDerivedObject(error)) throw error;
			cache.delete(key);
		}
	}
	const rendered = await renderDerived(candidate, sourceBytes, signal);
	const published = await awaitHost(() => publishCanonicalPng(rendered, signal), signal);
	const reference = captureDerivedReference(published);
	if (reference.bytes > candidate.maxOutputBytes) {
		throw new VisualObservationError("transform_output_too_large");
	}
	await awaitHost(() => authorize(input, coordinate, reference, signal), signal);
	const bytes = Buffer.from(await awaitHost(() => read(reference, signal), signal));
	cache.set(key, reference);
	return { reference, bytes };
}

async function renderDerived(
	candidate: CapturedDerivedCandidate,
	sourceBytes: Buffer,
	signal: AbortSignal,
): Promise<Buffer> {
	try {
		const inputPixels = checkedPixels(candidate.source.width, candidate.source.height);
		let pipeline = sharp(sourceBytes, { failOn: "warning", limitInputPixels: inputPixels }).toColourspace("srgb");
		const crop = candidate.transform.crop;
		if (crop) pipeline = pipeline.extract(crop);
		pipeline = pipeline.resize(candidate.transform.targetWidth, candidate.transform.targetHeight, {
			fit: "inside",
			withoutEnlargement: true,
			kernel: "lanczos3",
			fastShrinkOnLoad: false,
		});
		return await renderCanonicalPng(pipeline, candidate.maxOutputBytes, signal);
	} catch (error) {
		signal.throwIfAborted();
		if (error instanceof ImageAttachmentError) {
			if (error.code === "normalized_image_too_large") {
				throw new VisualObservationError("transform_output_too_large");
			}
			throw error;
		}
		throw new VisualObservationError("transform_failed");
	}
}

function transformCacheKey(candidate: CapturedDerivedCandidate): string {
	return createHash("sha256")
		.update(
			canonicalizeJsonValue({
				source: withoutName(candidate.source),
				transform: candidate.transform,
				rendererFingerprint,
			}),
		)
		.digest("hex");
}

function captureDerivedReference(value: unknown): Readonly<ImageReferenceContent> {
	return withoutName(
		captureImageReference(value, () => {
			throw new VisualObservationError("transform_failed");
		}),
	);
}

function staleDerivedObject(error: unknown): boolean {
	return (
		error instanceof ImageAttachmentError && (error.code === "storage_missing" || error.code === "storage_corrupt")
	);
}

function reservationBytes(candidate: CapturedCandidate): number {
	return isDerivedCandidate(candidate) ? candidate.maxOutputBytes : candidate.source.bytes;
}

function isDerivedCandidate(candidate: CapturedCandidate): candidate is CapturedDerivedCandidate {
	return candidate.transform.kind === "crop-resize";
}

function checkedPixels(width: number, height: number): number {
	if (width > Math.floor(Number.MAX_SAFE_INTEGER / height)) invalidSnapshot();
	return width * height;
}

type CanonicalRead = VisualObservationContextSourceOptions["store"]["read"];
type CanonicalPublish = VisualObservationContextSourceOptions["store"]["publishCanonicalPng"];

function selectionView(candidate: CapturedCandidate, status: "selected" | "omitted", outputAttachmentId?: string) {
	return Object.freeze({
		viewId: candidate.viewId,
		required: candidate.required,
		status,
		...(status === "omitted" ? { reason: "projection_budget" as const } : {}),
		sourceAttachmentId: candidate.source.attachmentId,
		contextReferenceId: candidate.contextReferenceId,
		transform: candidate.transform,
		...(outputAttachmentId === undefined ? {} : { outputAttachmentId }),
	});
}

function captureImageReference(value: unknown, fail: () => never): Readonly<ImageReferenceContent> {
	if (!isRecord(value)) fail();
	const type = value.type;
	const attachmentId = value.attachmentId;
	const mediaType = value.mediaType;
	const bytes = value.bytes;
	const width = value.width;
	const height = value.height;
	const name = value.name;
	const captured = {
		type,
		attachmentId,
		mediaType,
		bytes,
		width,
		height,
		...(name === undefined ? {} : { name }),
	};
	if (!Value.Check(ImageReferenceContentSchema, captured) || captured.mediaType !== "image/png") fail();
	return Object.freeze(captured);
}

function withoutName(value: Readonly<ImageReferenceContent>): Readonly<ImageReferenceContent> {
	return Object.freeze({
		type: "image-reference" as const,
		attachmentId: value.attachmentId,
		mediaType: value.mediaType,
		bytes: value.bytes,
		width: value.width,
		height: value.height,
	});
}

function captureProjectionLimits(value: GameContextLimits): Readonly<GameContextLimits> {
	try {
		const limits = captureContextLimits(value);
		if (!limits) invalidConfiguration();
		return limits;
	} catch (error) {
		if (error instanceof GameContextSnapshotError && error.code === "invalid_context_configuration") {
			invalidConfiguration();
		}
		throw error;
	}
}

function snapshotPositive(value: unknown): number {
	if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 1) invalidSnapshot();
	return value;
}

function snapshotNonNegative(value: unknown): number {
	if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) invalidSnapshot();
	return value;
}

function positive(value: number): number {
	if (!Number.isSafeInteger(value) || value < 1) invalidConfiguration();
	return value;
}

function invalidConfiguration(): never {
	throw new VisualObservationError("invalid_configuration");
}

function invalidSnapshot(): never {
	throw new VisualObservationError("invalid_snapshot");
}

function assertSnapshotIdentifier(value: string): void {
	try {
		assertOpaqueIdentifier(value);
	} catch {
		invalidSnapshot();
	}
}

function captureJson(value: unknown) {
	try {
		return normalizeJsonValue(value);
	} catch {
		invalidSnapshot();
	}
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

async function awaitHost<T>(operation: () => T | Promise<T>, signal: AbortSignal): Promise<T> {
	try {
		const value = await operation();
		signal.throwIfAborted();
		return value;
	} catch (error) {
		signal.throwIfAborted();
		throw error;
	}
}

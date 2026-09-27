import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { GameContextSource, NormalizedGameInput } from "@opengameagent/agent";
import {
	type CanonicalPngAttachmentStore,
	type ImageAttachmentAuthorizer,
	ImageAttachmentError,
	LocalImageAttachmentStore,
	renderCanonicalPng,
} from "@opengameagent/attachments";
import type { ImageReferenceContent, RunCoordinate } from "@opengameagent/protocol";
import sharp from "sharp";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createVisualObservationContextSource, type VisualObservationContextSourceOptions } from "../src/index.js";

const temporaryRoots: string[] = [];

afterEach(async () => {
	for (const root of temporaryRoots.splice(0)) await rm(root, { recursive: true, force: true });
});

const INPUT: NormalizedGameInput = {
	sessionId: "session-1",
	actorId: "actor-1",
	inputId: "input-1",
	payload: { instruction: "look" },
};

const COORDINATE: RunCoordinate = {
	sessionId: "session-1",
	actorId: "actor-1",
	runId: "run-1",
	turn: "1",
};

function createTestSource(
	options: Omit<VisualObservationContextSourceOptions, "sourceId" | "selectionReferenceId">,
): GameContextSource {
	return createVisualObservationContextSource({
		sourceId: "vision",
		selectionReferenceId: "vision-selection",
		...options,
	});
}

describe("createVisualObservationContextSource", () => {
	it("returns an empty contribution without attachment I/O when capture has no observation", async () => {
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>();
		const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>();
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const source = createTestSource({
			capture: () => undefined,
			authorize,
			store: { read, publishCanonicalPng },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 4_096,
				media: { maxContentParts: 2, maxImageBytes: 1_024, maxTotalImageBytes: 1_024 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).resolves.toEqual({ references: [] });
		expect(source).toMatchObject({ sourceId: "vision", provenanceKind: "observation" });
		expect(authorize).not.toHaveBeenCalled();
		expect(read).not.toHaveBeenCalled();
		expect(publishCanonicalPng).not.toHaveBeenCalled();
	});

	it("projects an authorized original in host position and appends a content-safe selection manifest", async () => {
		const bytes = Buffer.from([1, 2, 3]);
		const sourceReference = imageReference(bytes, 2, 1, "display-only");
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>(async () => new Uint8Array(bytes));
		const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>();
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const signal = new AbortController().signal;
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-7",
				references: [
					{
						referenceId: "scene",
						revision: "scene-3",
						content: { kind: "scene", coordinateFrame: "world", units: "meters" },
					},
				],
				candidates: [
					{
						viewId: "main-camera",
						required: true,
						contextReferenceId: "scene",
						source: sourceReference,
						transform: { kind: "original" },
					},
				],
			}),
			authorize,
			store: { read, publishCanonicalPng },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 8_192,
				media: { maxContentParts: 2, maxImageBytes: 3, maxTotalImageBytes: 3 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		const contribution = await source.load(INPUT, signal, COORDINATE);

		expect(contribution.references).toHaveLength(2);
		expect(contribution.references[0]).toEqual({
			referenceId: "scene",
			revision: "scene-3",
			content: { kind: "scene", coordinateFrame: "world", units: "meters" },
		});
		expect(contribution.references[1]).toEqual({
			referenceId: "vision-selection",
			revision: "snapshot-7",
			content: {
				observationVersion: "1",
				revision: "snapshot-7",
				rendererFingerprint: expect.stringMatching(/^[0-9a-f]{64}$/u),
				views: [
					{
						viewId: "main-camera",
						required: true,
						status: "selected",
						sourceAttachmentId: sourceReference.attachmentId,
						contextReferenceId: "scene",
						transform: { kind: "original" },
						outputAttachmentId: sourceReference.attachmentId,
					},
				],
			},
		});
		expect(contribution.images).toEqual([
			{
				contextReferenceId: "scene",
				content: { type: "image", mimeType: "image/png", data: bytes.toString("base64") },
				reference: {
					type: "image-reference",
					attachmentId: sourceReference.attachmentId,
					mediaType: "image/png",
					bytes: 3,
					width: 2,
					height: 1,
				},
			},
		]);
		expect(authorize).toHaveBeenCalledOnce();
		expect(authorize).toHaveBeenCalledWith(INPUT, COORDINATE, sourceReference, signal);
		expect(read).toHaveBeenCalledOnce();
		expect(read).toHaveBeenCalledWith(sourceReference, signal);
		expect(publishCanonicalPng).not.toHaveBeenCalled();
		expect(JSON.stringify(contribution.references[1])).not.toContain(bytes.toString("base64"));
		expect(JSON.stringify(contribution.references[1])).not.toContain("display-only");
		expect(JSON.stringify(contribution.references[1])).not.toContain('"bytes"');
	});

	it("reserves every required view first, then admits optional views greedily while preserving host output order", async () => {
		const optionalLarge = imageReference(Buffer.alloc(4, 1), 2, 2);
		const required = imageReference(Buffer.alloc(3, 2), 3, 1);
		const optionalSmall = imageReference(Buffer.alloc(2, 3), 2, 1);
		const byId = new Map([
			[optionalLarge.attachmentId, Buffer.alloc(4, 1)],
			[required.attachmentId, Buffer.alloc(3, 2)],
			[optionalSmall.attachmentId, Buffer.alloc(2, 3)],
		]);
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>(async (reference) => {
			const bytes = byId.get(reference.attachmentId);
			if (!bytes) throw new Error("unknown fixture");
			return new Uint8Array(bytes);
		});
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-8",
				references: [{ referenceId: "scene", revision: "1", content: { kind: "scene" } }],
				candidates: [
					{
						viewId: "optional-before-required",
						required: false,
						contextReferenceId: "scene",
						source: optionalLarge,
						transform: { kind: "crop-resize", targetWidth: 1, targetHeight: 1 },
						maxOutputBytes: 4,
					},
					{
						viewId: "required-middle",
						required: true,
						contextReferenceId: "scene",
						source: required,
						transform: { kind: "original" },
					},
					{
						viewId: "optional-after-required",
						required: false,
						contextReferenceId: "scene",
						source: optionalSmall,
						transform: { kind: "original" },
					},
				],
			}),
			authorize,
			store: { read, publishCanonicalPng: vi.fn() },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 16_384,
				media: { maxContentParts: 3, maxImageBytes: 4, maxTotalImageBytes: 5 },
			},
			maxCandidatesPerSnapshot: 3,
			maxTransformCacheEntries: 1,
		});

		const contribution = await source.load(INPUT, new AbortController().signal, COORDINATE);

		expect(contribution.images?.map((image) => image.reference.attachmentId)).toEqual([
			required.attachmentId,
			optionalSmall.attachmentId,
		]);
		expect(authorize.mock.calls.map((call) => call[2].attachmentId)).toEqual([
			required.attachmentId,
			optionalSmall.attachmentId,
		]);
		expect(read.mock.calls.map((call) => call[0].attachmentId)).toEqual([
			required.attachmentId,
			optionalSmall.attachmentId,
		]);
		expect(contribution.references[1]?.content).toMatchObject({
			views: [
				{ viewId: "optional-before-required", status: "omitted", reason: "projection_budget" },
				{ viewId: "required-middle", status: "selected" },
				{ viewId: "optional-after-required", status: "selected" },
			],
		});
		expect(JSON.stringify(contribution.references[1]?.content)).not.toContain('"reason":undefined');
	});

	it("fails a required projection budget before authorization or attachment I/O", async () => {
		const sourceReference = imageReference(Buffer.alloc(4, 1), 2, 2);
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>();
		const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>();
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-9",
				references: [{ referenceId: "scene", revision: "1", content: {} }],
				candidates: [
					{
						viewId: "required",
						required: true,
						contextReferenceId: "scene",
						source: sourceReference,
						transform: { kind: "original" },
					},
				],
			}),
			authorize,
			store: { read, publishCanonicalPng },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 4_096,
				media: { maxContentParts: 2, maxImageBytes: 3, maxTotalImageBytes: 3 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toMatchObject({
			name: "VisualObservationError",
			code: "required_budget_exceeded",
		});
		expect(authorize).not.toHaveBeenCalled();
		expect(read).not.toHaveBeenCalled();
		expect(publishCanonicalPng).not.toHaveBeenCalled();
	});

	it("crops before inside/no-enlarge resize and authorizes plus verifies the derived CAS object", async () => {
		const pixels = Buffer.from([
			255, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 255, 255, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 255,
		]);
		const sourceBytes = await renderCanonicalPng(
			sharp(pixels, { raw: { width: 4, height: 2, channels: 3 } }).toColourspace("srgb"),
			4_096,
			new AbortController().signal,
		);
		const parent = await mkdtemp(join(tmpdir(), "opengameagent-observation-test-"));
		temporaryRoots.push(parent);
		const backingStore = await LocalImageAttachmentStore.open({
			rootDirectory: join(parent, "store"),
			limits: {
				maxInputBytes: 4_096,
				maxInputPixels: 8,
				maxInputDimension: 4,
				maxStoredBytes: 4_096,
				maxStoredPixels: 8,
				maxStoredDimension: 4,
				maxReadBytes: 4_096,
				maxNameBytes: 16,
				maxImagesPerBatch: 1,
				maxBatchInputBytes: 4_096,
				maxBatchStoredBytes: 4_096,
			},
		});
		const sourceReference = await backingStore.publishCanonicalPng(sourceBytes, new AbortController().signal);
		const read = vi.fn(backingStore.read.bind(backingStore));
		const publishCanonicalPng = vi.fn(backingStore.publishCanonicalPng.bind(backingStore));
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-10",
				references: [{ referenceId: "right-side", revision: "1", content: { region: "right" } }],
				candidates: [
					{
						viewId: "right-thumbnail",
						required: true,
						contextReferenceId: "right-side",
						source: sourceReference,
						transform: {
							kind: "crop-resize",
							crop: { left: 2, top: 0, width: 2, height: 2 },
							targetWidth: 1,
							targetHeight: 1,
						},
						maxOutputBytes: 4_096,
					},
				],
			}),
			authorize,
			store: { read, publishCanonicalPng },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 16_384,
				media: { maxContentParts: 2, maxImageBytes: 4_096, maxTotalImageBytes: 4_096 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		const contribution = await source.load(INPUT, new AbortController().signal, COORDINATE);
		const output = contribution.images?.[0];
		expect(output?.reference).toMatchObject({ mediaType: "image/png", width: 1, height: 1 });
		const decoded = await sharp(Buffer.from(output?.content.data ?? "", "base64"))
			.raw()
			.toBuffer();
		expect([...decoded.subarray(0, 3)]).toEqual([0, 0, 255]);
		expect(publishCanonicalPng).toHaveBeenCalledOnce();
		expect(read.mock.calls.map((call) => call[0].attachmentId)).toEqual([
			sourceReference.attachmentId,
			output?.reference.attachmentId,
		]);
		expect(authorize.mock.calls.map((call) => call[2].attachmentId)).toEqual([
			sourceReference.attachmentId,
			output?.reference.attachmentId,
		]);
		expect(contribution.references[1]?.content).toMatchObject({
			views: [
				{
					viewId: "right-thumbnail",
					status: "selected",
					transform: {
						kind: "crop-resize",
						crop: { left: 2, top: 0, width: 2, height: 2 },
						targetWidth: 1,
						targetHeight: 1,
					},
					outputAttachmentId: output?.reference.attachmentId,
				},
			],
		});
	});

	it("caches only the derived reference by source, transform, and renderer rather than the output budget", async () => {
		const sourceBytes = await renderCanonicalPng(
			sharp({ create: { width: 2, height: 2, channels: 3, background: { r: 1, g: 2, b: 3 } } }).toColourspace("srgb"),
			4_096,
			new AbortController().signal,
		);
		const parent = await mkdtemp(join(tmpdir(), "opengameagent-observation-cache-test-"));
		temporaryRoots.push(parent);
		const backingStore = await LocalImageAttachmentStore.open({
			rootDirectory: join(parent, "store"),
			limits: {
				maxInputBytes: 4_096,
				maxInputPixels: 4,
				maxInputDimension: 2,
				maxStoredBytes: 4_096,
				maxStoredPixels: 4,
				maxStoredDimension: 2,
				maxReadBytes: 4_096,
				maxNameBytes: 16,
				maxImagesPerBatch: 1,
				maxBatchInputBytes: 4_096,
				maxBatchStoredBytes: 4_096,
			},
		});
		const sourceReference = await backingStore.publishCanonicalPng(sourceBytes, new AbortController().signal);
		const read = vi.fn(backingStore.read.bind(backingStore));
		const publishCanonicalPng = vi.fn(backingStore.publishCanonicalPng.bind(backingStore));
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		let captureCount = 0;
		const source = createTestSource({
			capture: () => {
				captureCount += 1;
				return {
					observationVersion: "1",
					revision: `snapshot-${captureCount}`,
					references: [{ referenceId: "scene", revision: `${captureCount}`, content: { kind: "scene" } }],
					candidates: [
						{
							viewId: "thumbnail",
							required: true,
							contextReferenceId: "scene",
							source: sourceReference,
							transform: { kind: "crop-resize", targetWidth: 4, targetHeight: 4 },
							maxOutputBytes: captureCount === 1 ? 4_096 : 2_048,
						},
					],
				};
			},
			authorize,
			store: { read, publishCanonicalPng },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 16_384,
				media: { maxContentParts: 2, maxImageBytes: 4_096, maxTotalImageBytes: 4_096 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		const first = await source.load(INPUT, new AbortController().signal, COORDINATE);
		const second = await source.load(INPUT, new AbortController().signal, { ...COORDINATE, turn: "2" });

		expect(second.images?.[0]?.reference.attachmentId).toBe(first.images?.[0]?.reference.attachmentId);
		expect(first.images?.[0]?.reference).toMatchObject({ width: 2, height: 2 });
		expect(publishCanonicalPng).toHaveBeenCalledOnce();
		expect(authorize.mock.calls.map((call) => call[2].attachmentId)).toEqual([
			sourceReference.attachmentId,
			first.images?.[0]?.reference.attachmentId,
			sourceReference.attachmentId,
			first.images?.[0]?.reference.attachmentId,
		]);
		expect(read.mock.calls.map((call) => call[0].attachmentId)).toEqual([
			sourceReference.attachmentId,
			first.images?.[0]?.reference.attachmentId,
			sourceReference.attachmentId,
			first.images?.[0]?.reference.attachmentId,
		]);
	});

	it("stops before source I/O when authorization observes cancellation", async () => {
		const bytes = Buffer.from([1]);
		const sourceReference = imageReference(bytes, 1, 1);
		const controller = new AbortController();
		const reason = new Error("cancel visual projection");
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>(async () => new Uint8Array(bytes));
		const authorize = vi.fn<ImageAttachmentAuthorizer>(() => {
			controller.abort(reason);
		});
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-cancelled",
				references: [{ referenceId: "scene", revision: "1", content: {} }],
				candidates: [
					{
						viewId: "main",
						required: true,
						contextReferenceId: "scene",
						source: sourceReference,
						transform: { kind: "original" },
					},
				],
			}),
			authorize,
			store: { read, publishCanonicalPng: vi.fn() },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 4_096,
				media: { maxContentParts: 2, maxImageBytes: 1, maxTotalImageBytes: 1 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		await expect(source.load(INPUT, controller.signal, COORDINATE)).rejects.toBe(reason);
		expect(read).not.toHaveBeenCalled();
	});

	it.each(["storage_missing", "storage_corrupt"] as const)(
		"treats a disposable cache target with %s as one rebuild while preserving source authorization",
		async (staleCode) => {
			const sourceBytes = await renderCanonicalPng(
				sharp({ create: { width: 2, height: 1, channels: 3, background: { r: 4, g: 5, b: 6 } } }),
				4_096,
				new AbortController().signal,
			);
			const sourceReference = imageReference(sourceBytes, 2, 1);
			let derivedBytes: Buffer | undefined;
			let derivedReference: ImageReferenceContent | undefined;
			let derivedReads = 0;
			const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>(async (bytes) => {
				derivedBytes = Buffer.from(bytes);
				const metadata = await sharp(derivedBytes).metadata();
				derivedReference = imageReference(derivedBytes, metadata.width, metadata.height);
				return derivedReference;
			});
			const read = vi.fn<CanonicalPngAttachmentStore["read"]>(async (reference) => {
				if (reference.attachmentId === sourceReference.attachmentId) return new Uint8Array(sourceBytes);
				derivedReads += 1;
				if (derivedReads === 2) throw new ImageAttachmentError(staleCode);
				if (!derivedBytes) throw new Error("derived fixture is missing");
				return new Uint8Array(derivedBytes);
			});
			const authorize = vi.fn<ImageAttachmentAuthorizer>();
			const source = createTestSource({
				capture: () => ({
					observationVersion: "1",
					revision: "snapshot-cache",
					references: [{ referenceId: "scene", revision: "1", content: {} }],
					candidates: [
						{
							viewId: "derived",
							required: true,
							contextReferenceId: "scene",
							source: sourceReference,
							transform: { kind: "crop-resize", targetWidth: 1, targetHeight: 1 },
							maxOutputBytes: 4_096,
						},
					],
				}),
				authorize,
				store: { read, publishCanonicalPng },
				projectionLimits: {
					maxReferencesPerSnapshot: 2,
					maxSnapshotBytes: 16_384,
					media: { maxContentParts: 2, maxImageBytes: 4_096, maxTotalImageBytes: 4_096 },
				},
				maxCandidatesPerSnapshot: 1,
				maxTransformCacheEntries: 1,
			});

			const first = await source.load(INPUT, new AbortController().signal, COORDINATE);
			const second = await source.load(INPUT, new AbortController().signal, { ...COORDINATE, turn: "2" });

			expect(second.images?.[0]?.reference.attachmentId).toBe(first.images?.[0]?.reference.attachmentId);
			expect(publishCanonicalPng).toHaveBeenCalledTimes(2);
			expect(authorize.mock.calls.map((call) => call[2].attachmentId)).toEqual([
				sourceReference.attachmentId,
				derivedReference?.attachmentId,
				sourceReference.attachmentId,
				derivedReference?.attachmentId,
				derivedReference?.attachmentId,
			]);
		},
	);

	it.each(["storage_unavailable", "storage_changed"] as const)(
		"does not reinterpret a cached CAS %s failure as a disposable cache miss",
		async (failureCode) => {
			const sourceBytes = await renderCanonicalPng(
				sharp({ create: { width: 2, height: 1, channels: 3, background: { r: 4, g: 5, b: 6 } } }),
				4_096,
				new AbortController().signal,
			);
			const sourceReference = imageReference(sourceBytes, 2, 1);
			const storageFailure = new ImageAttachmentError(failureCode);
			let derivedBytes: Buffer | undefined;
			let derivedReference: ImageReferenceContent | undefined;
			let rejectDerived = false;
			const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>(async (bytes) => {
				derivedBytes = Buffer.from(bytes);
				derivedReference = imageReference(derivedBytes, 1, 1);
				return derivedReference;
			});
			const read = vi.fn<CanonicalPngAttachmentStore["read"]>(async (reference) => {
				if (reference.attachmentId === sourceReference.attachmentId) return new Uint8Array(sourceBytes);
				if (rejectDerived) throw storageFailure;
				if (!derivedBytes) throw new Error("derived fixture is missing");
				return new Uint8Array(derivedBytes);
			});
			const source = createTestSource({
				capture: () => ({
					observationVersion: "1",
					revision: "snapshot-changed",
					references: [{ referenceId: "scene", revision: "1", content: {} }],
					candidates: [
						{
							viewId: "derived",
							required: true,
							contextReferenceId: "scene",
							source: sourceReference,
							transform: { kind: "crop-resize", targetWidth: 1, targetHeight: 1 },
							maxOutputBytes: 4_096,
						},
					],
				}),
				authorize: vi.fn(),
				store: { read, publishCanonicalPng },
				projectionLimits: {
					maxReferencesPerSnapshot: 2,
					maxSnapshotBytes: 8_192,
					media: { maxContentParts: 2, maxImageBytes: 4_096, maxTotalImageBytes: 4_096 },
				},
				maxCandidatesPerSnapshot: 1,
				maxTransformCacheEntries: 1,
			});

			await source.load(INPUT, new AbortController().signal, COORDINATE);
			rejectDerived = true;
			await expect(source.load(INPUT, new AbortController().signal, { ...COORDINATE, turn: "2" })).rejects.toBe(
				storageFailure,
			);
			expect(publishCanonicalPng).toHaveBeenCalledOnce();
			expect(derivedReference).toBeDefined();
		},
	);

	it.each(["authorize", "read"] as const)(
		"does not cache a newly published derived object before its %s verification succeeds",
		async (failurePoint) => {
			const sourceBytes = await renderCanonicalPng(
				sharp({ create: { width: 2, height: 1, channels: 3, background: { r: 7, g: 8, b: 9 } } }),
				4_096,
				new AbortController().signal,
			);
			const sourceReference = imageReference(sourceBytes, 2, 1);
			const verificationFailure = new Error(`derived ${failurePoint} failed`);
			let failureObserved = false;
			let derivedBytes: Buffer | undefined;
			const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>(async (bytes) => {
				derivedBytes = Buffer.from(bytes);
				return imageReference(derivedBytes, 1, 1);
			});
			const authorize = vi.fn<ImageAttachmentAuthorizer>((_input, _coordinate, reference) => {
				if (
					failurePoint === "authorize" &&
					reference.attachmentId !== sourceReference.attachmentId &&
					!failureObserved
				) {
					failureObserved = true;
					throw verificationFailure;
				}
			});
			const read = vi.fn<CanonicalPngAttachmentStore["read"]>(async (reference) => {
				if (reference.attachmentId === sourceReference.attachmentId) return new Uint8Array(sourceBytes);
				if (failurePoint === "read" && !failureObserved) {
					failureObserved = true;
					throw verificationFailure;
				}
				if (!derivedBytes) throw new Error("derived fixture is missing");
				return new Uint8Array(derivedBytes);
			});
			const source = createTestSource({
				capture: () => ({
					observationVersion: "1",
					revision: "snapshot-new-derived-verification",
					references: [{ referenceId: "scene", revision: "1", content: {} }],
					candidates: [
						{
							viewId: "derived",
							required: true,
							contextReferenceId: "scene",
							source: sourceReference,
							transform: { kind: "crop-resize", targetWidth: 1, targetHeight: 1 },
							maxOutputBytes: 4_096,
						},
					],
				}),
				authorize,
				store: { read, publishCanonicalPng },
				projectionLimits: {
					maxReferencesPerSnapshot: 2,
					maxSnapshotBytes: 8_192,
					media: { maxContentParts: 2, maxImageBytes: 4_096, maxTotalImageBytes: 4_096 },
				},
				maxCandidatesPerSnapshot: 1,
				maxTransformCacheEntries: 1,
			});

			await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toBe(verificationFailure);
			await expect(
				source.load(INPUT, new AbortController().signal, { ...COORDINATE, turn: "2" }),
			).resolves.toBeDefined();
			expect(publishCanonicalPng).toHaveBeenCalledTimes(2);
		},
	);

	it("reports the candidate output reservation when canonical PNG encoding cannot fit", async () => {
		const sourceBytes = await renderCanonicalPng(
			sharp({ create: { width: 1, height: 1, channels: 3, background: { r: 1, g: 1, b: 1 } } }),
			4_096,
			new AbortController().signal,
		);
		const sourceReference = imageReference(sourceBytes, 1, 1);
		const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>();
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-small-output",
				references: [{ referenceId: "scene", revision: "1", content: {} }],
				candidates: [
					{
						viewId: "derived",
						required: true,
						contextReferenceId: "scene",
						source: sourceReference,
						transform: { kind: "crop-resize", targetWidth: 1, targetHeight: 1 },
						maxOutputBytes: 1,
					},
				],
			}),
			authorize: vi.fn(),
			store: { read: async () => new Uint8Array(sourceBytes), publishCanonicalPng },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 4_096,
				media: { maxContentParts: 2, maxImageBytes: 1, maxTotalImageBytes: 1 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toMatchObject({
			name: "VisualObservationError",
			code: "transform_output_too_large",
		});
		expect(publishCanonicalPng).not.toHaveBeenCalled();
	});

	it("binds authorization and projection to one structural read of the source reference", async () => {
		const bytes = Buffer.from([8]);
		const expected = imageReference(bytes, 1, 1);
		let attachmentIdReads = 0;
		const mutableSource = Object.defineProperties(
			{},
			{
				type: { enumerable: true, value: expected.type },
				attachmentId: {
					enumerable: true,
					get: () => {
						attachmentIdReads += 1;
						return attachmentIdReads === 1 ? expected.attachmentId : `img_${"0".repeat(64)}`;
					},
				},
				mediaType: { enumerable: true, value: expected.mediaType },
				bytes: { enumerable: true, value: expected.bytes },
				width: { enumerable: true, value: expected.width },
				height: { enumerable: true, value: expected.height },
			},
		) as ImageReferenceContent;
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>(async () => new Uint8Array(bytes));
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-structural",
				references: [{ referenceId: "scene", revision: "1", content: {} }],
				candidates: [
					{
						viewId: "main",
						required: true,
						contextReferenceId: "scene",
						source: mutableSource,
						transform: { kind: "original" },
					},
				],
			}),
			authorize,
			store: { read, publishCanonicalPng: vi.fn() },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 4_096,
				media: { maxContentParts: 2, maxImageBytes: 1, maxTotalImageBytes: 1 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		const contribution = await source.load(INPUT, new AbortController().signal, COORDINATE);
		expect(attachmentIdReads).toBe(1);
		expect(authorize.mock.calls[0]?.[2]).toEqual(expected);
		expect(read.mock.calls[0]?.[0]).toEqual(expected);
		expect(contribution.images?.[0]?.reference).toEqual(expected);
	});

	it("rejects an out-of-source crop before authorization or attachment I/O", async () => {
		const sourceReference = imageReference(Buffer.from([1]), 1, 1);
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>();
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-invalid-crop",
				references: [{ referenceId: "scene", revision: "1", content: {} }],
				candidates: [
					{
						viewId: "invalid-crop",
						required: false,
						contextReferenceId: "scene",
						source: sourceReference,
						transform: {
							kind: "crop-resize",
							crop: { left: 1, top: 0, width: 1, height: 1 },
							targetWidth: 1,
							targetHeight: 1,
						},
						maxOutputBytes: 1,
					},
				],
			}),
			authorize,
			store: { read, publishCanonicalPng: vi.fn() },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 4_096,
				media: { maxContentParts: 2, maxImageBytes: 1, maxTotalImageBytes: 1 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toMatchObject({
			name: "VisualObservationError",
			code: "invalid_snapshot",
		});
		expect(authorize).not.toHaveBeenCalled();
		expect(read).not.toHaveBeenCalled();
	});

	it("does not downgrade an optional candidate authorization failure to omission", async () => {
		const denied = new Error("source revoked");
		const sourceReference = imageReference(Buffer.from([1]), 1, 1);
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>();
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-revoked",
				references: [{ referenceId: "scene", revision: "1", content: {} }],
				candidates: [
					{
						viewId: "optional",
						required: false,
						contextReferenceId: "scene",
						source: sourceReference,
						transform: { kind: "original" },
					},
				],
			}),
			authorize: () => {
				throw denied;
			},
			store: { read, publishCanonicalPng: vi.fn() },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 4_096,
				media: { maxContentParts: 2, maxImageBytes: 1, maxTotalImageBytes: 1 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toBe(denied);
		expect(read).not.toHaveBeenCalled();
	});

	it("allows semantic views to converge on the same output attachment", async () => {
		const bytes = Buffer.from([5]);
		const sourceReference = imageReference(bytes, 1, 1);
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-duplicate-output",
				references: [{ referenceId: "scene", revision: "1", content: {} }],
				candidates: ["left", "right"].map((viewId) => ({
					viewId,
					required: true,
					contextReferenceId: "scene",
					source: sourceReference,
					transform: { kind: "original" as const },
				})),
			}),
			authorize: vi.fn(),
			store: { read: async () => new Uint8Array(bytes), publishCanonicalPng: vi.fn() },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 8_192,
				media: { maxContentParts: 3, maxImageBytes: 1, maxTotalImageBytes: 2 },
			},
			maxCandidatesPerSnapshot: 2,
			maxTransformCacheEntries: 1,
		});

		const contribution = await source.load(INPUT, new AbortController().signal, COORDINATE);
		expect(contribution.images?.map((image) => image.reference.attachmentId)).toEqual([
			sourceReference.attachmentId,
			sourceReference.attachmentId,
		]);
		expect(contribution.references[1]?.content).toMatchObject({
			views: [
				{ viewId: "left", status: "selected", outputAttachmentId: sourceReference.attachmentId },
				{ viewId: "right", status: "selected", outputAttachmentId: sourceReference.attachmentId },
			],
		});
	});

	it("passes through a structured calibrated BEV snapshot without requiring media limits", async () => {
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>();
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-bev",
				references: [
					{
						referenceId: "bev",
						revision: "bev-4",
						content: {
							kind: "calibrated-bev",
							sourceFrame: "camera",
							targetFrame: "world",
							units: "meters",
							bounds: { left: -10, right: 10, bottom: -5, top: 5 },
						},
					},
				],
				candidates: [],
			}),
			authorize,
			store: { read, publishCanonicalPng: vi.fn() },
			projectionLimits: { maxReferencesPerSnapshot: 2, maxSnapshotBytes: 8_192 },
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		const contribution = await source.load(INPUT, new AbortController().signal, COORDINATE);
		expect(contribution.references[0]).toEqual({
			referenceId: "bev",
			revision: "bev-4",
			content: {
				kind: "calibrated-bev",
				sourceFrame: "camera",
				targetFrame: "world",
				units: "meters",
				bounds: { left: -10, right: 10, bottom: -5, top: 5 },
			},
		});
		expect(contribution.references[1]?.content).toMatchObject({ views: [] });
		expect(contribution.images).toBeUndefined();
		expect(authorize).not.toHaveBeenCalled();
		expect(read).not.toHaveBeenCalled();
	});

	it("preserves attachment-store integrity failures", async () => {
		const failure = new ImageAttachmentError("storage_corrupt");
		const sourceReference = imageReference(Buffer.from([1]), 1, 1);
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-corrupt",
				references: [{ referenceId: "scene", revision: "1", content: {} }],
				candidates: [
					{
						viewId: "main",
						required: false,
						contextReferenceId: "scene",
						source: sourceReference,
						transform: { kind: "original" },
					},
				],
			}),
			authorize: vi.fn(),
			store: {
				read: async () => {
					throw failure;
				},
				publishCanonicalPng: vi.fn(),
			},
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 4_096,
				media: { maxContentParts: 2, maxImageBytes: 1, maxTotalImageBytes: 1 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toBe(failure);
	});

	it("maps an upstream Sharp transform rejection to the stable transform failure", async () => {
		const sourceBytes = await renderCanonicalPng(
			sharp({ create: { width: 1, height: 1, channels: 3, background: { r: 1, g: 2, b: 3 } } }),
			4_096,
			new AbortController().signal,
		);
		const sourceReference = imageReference(sourceBytes, 1, 1);
		const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>();
		const source = createTestSource({
			capture: () => ({
				observationVersion: "1",
				revision: "snapshot-transform-failure",
				references: [{ referenceId: "scene", revision: "1", content: {} }],
				candidates: [
					{
						viewId: "impossible-target",
						required: true,
						contextReferenceId: "scene",
						source: sourceReference,
						transform: {
							kind: "crop-resize",
							targetWidth: Number.MAX_SAFE_INTEGER,
							targetHeight: 1,
						},
						maxOutputBytes: 4_096,
					},
				],
			}),
			authorize: vi.fn(),
			store: { read: async () => new Uint8Array(sourceBytes), publishCanonicalPng },
			projectionLimits: {
				maxReferencesPerSnapshot: 2,
				maxSnapshotBytes: 4_096,
				media: { maxContentParts: 2, maxImageBytes: 4_096, maxTotalImageBytes: 4_096 },
			},
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});

		await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toMatchObject({
			name: "VisualObservationError",
			code: "transform_failed",
		});
		expect(publishCanonicalPng).not.toHaveBeenCalled();
	});

	it("rejects invalid host configuration at construction", () => {
		expect(() =>
			createTestSource({
				capture: () => undefined,
				authorize: vi.fn(),
				store: { read: vi.fn(), publishCanonicalPng: vi.fn() },
				projectionLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1 },
				maxCandidatesPerSnapshot: 1,
				maxTransformCacheEntries: 0,
			}),
		).toThrow(expect.objectContaining({ name: "VisualObservationError", code: "invalid_configuration" }));
	});
});

function imageReference(bytes: Uint8Array, width: number, height: number, name?: string): ImageReferenceContent {
	return {
		type: "image-reference",
		attachmentId: `img_${createHash("sha256").update(bytes).digest("hex")}`,
		mediaType: "image/png",
		bytes: bytes.byteLength,
		width,
		height,
		...(name ? { name } : {}),
	};
}

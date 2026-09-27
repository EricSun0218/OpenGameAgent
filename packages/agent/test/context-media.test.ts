import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type { ImageReferenceContent, RunCoordinate } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { prepareGameContextSnapshot } from "../src/context-snapshot.js";
import type {
	GameContextContribution,
	GameContextLimits,
	GameContextSource,
	NormalizedGameInput,
} from "../src/types.js";

const INPUT: NormalizedGameInput = {
	sessionId: "session",
	actorId: "actor",
	inputId: "input",
	payload: { instruction: "inspect" },
};

const COORDINATE: RunCoordinate = {
	sessionId: "session",
	actorId: "actor",
	runId: "run",
	turn: "2",
};

const IMAGE_BYTES = Buffer.from("context-image");
const IMAGE_REFERENCE: ImageReferenceContent = {
	type: "image-reference",
	attachmentId: `img_${createHash("sha256").update(IMAGE_BYTES).digest("hex")}`,
	mediaType: "image/png",
	bytes: IMAGE_BYTES.byteLength,
	width: 1,
	height: 1,
};

const MEDIA_LIMITS = {
	maxContentParts: 3,
	maxImageBytes: IMAGE_BYTES.byteLength,
	maxTotalImageBytes: IMAGE_BYTES.byteLength * 2,
} as const;

const LIMITS: GameContextLimits = {
	maxReferencesPerSnapshot: 2,
	maxSnapshotBytes: 4_096,
	media: MEDIA_LIMITS,
};

function contribution(images = 1): GameContextContribution {
	return {
		references: [
			{
				referenceId: "observation",
				revision: "7",
				content: { kind: "visual-observation", selectedViews: images },
			},
		],
		images: Array.from({ length: images }, () => ({
			contextReferenceId: "observation",
			content: {
				type: "image",
				data: IMAGE_BYTES.toString("base64"),
				mimeType: "image/png",
			},
			reference: IMAGE_REFERENCE,
		})),
	};
}

function source(value: GameContextContribution, coordinates: RunCoordinate[] = []): GameContextSource {
	return {
		sourceId: "visual",
		provenanceKind: "observation",
		load: (_input, _signal, coordinate) => {
			coordinates.push(structuredClone(coordinate));
			return value;
		},
	};
}

describe("game context media", () => {
	it("captures one aligned image contribution at the exact provider request coordinate", async () => {
		const coordinates: RunCoordinate[] = [];
		const snapshot = await prepareGameContextSnapshot(
			[source(contribution(), coordinates)],
			LIMITS,
			INPUT,
			new AbortController().signal,
			COORDINATE,
		);

		expect(coordinates).toEqual([COORDINATE]);
		expect(snapshot?.images).toEqual([
			{
				sourceId: "visual",
				contextReferenceId: "observation",
				revision: "7",
				bytes: IMAGE_BYTES.byteLength,
				content: {
					type: "image",
					data: IMAGE_BYTES.toString("base64"),
					mimeType: "image/png",
				},
				reference: IMAGE_REFERENCE,
			},
		]);
	});

	it("allows two semantic views to share one immutable derived image", async () => {
		const snapshot = await prepareGameContextSnapshot(
			[source(contribution(2))],
			LIMITS,
			INPUT,
			new AbortController().signal,
			COORDINATE,
		);

		expect(snapshot?.images.map((image) => image.reference.attachmentId)).toEqual([
			IMAGE_REFERENCE.attachmentId,
			IMAGE_REFERENCE.attachmentId,
		]);
	});

	it("captures image pairs before later sources can mutate them", async () => {
		const first = contribution();
		const mutableReference = first.references[0] as {
			referenceId: string;
			revision: string;
			content: { kind: string; selectedViews: number };
		};
		const mutableImage = first.images?.[0] as {
			contextReferenceId: string;
			content: { type: "image"; data: string; mimeType: "image/png" };
			reference: ImageReferenceContent;
		};
		const snapshot = await prepareGameContextSnapshot(
			[
				source(first),
				{
					sourceId: "later",
					load: () => {
						mutableReference.revision = "8";
						mutableReference.content.selectedViews = 99;
						mutableImage.contextReferenceId = "changed";
						mutableImage.content.data = Buffer.from("changed").toString("base64");
						mutableImage.reference = { ...IMAGE_REFERENCE, attachmentId: `img_${"0".repeat(64)}` };
						return { references: [] };
					},
				},
			],
			{ ...LIMITS, maxReferencesPerSnapshot: 3 },
			INPUT,
			new AbortController().signal,
			COORDINATE,
		);

		expect(JSON.parse(snapshot?.text ?? "null")).toMatchObject({
			references: [{ revision: "7", content: { selectedViews: 1 } }],
		});
		expect(snapshot?.images[0]).toMatchObject({
			contextReferenceId: "observation",
			content: { data: IMAGE_BYTES.toString("base64") },
			reference: IMAGE_REFERENCE,
		});
	});

	it("charges immutable image-reference lineage to the existing snapshot-byte budget and drops display names", async () => {
		const named = contribution();
		const namedContribution: GameContextContribution = {
			...named,
			images: (named.images ?? []).map((image) => ({
				...image,
				reference: { ...image.reference, name: "x".repeat(100_000) },
			})),
		};
		const roomy = await prepareGameContextSnapshot(
			[source(namedContribution)],
			{ ...LIMITS, maxSnapshotBytes: 4_096 },
			INPUT,
			new AbortController().signal,
			COORDINATE,
		);
		expect(roomy?.images[0]?.reference).not.toHaveProperty("name");

		await expect(
			prepareGameContextSnapshot(
				[source(namedContribution)],
				{ ...LIMITS, maxSnapshotBytes: Buffer.byteLength(roomy?.text ?? "") },
				INPUT,
				new AbortController().signal,
				COORDINATE,
			),
		).rejects.toMatchObject({ code: "context_snapshot_byte_limit_exceeded" });
	});

	it("fails before projection when an image lacks same-source reference lineage", async () => {
		const unlinked = contribution();
		await expect(
			prepareGameContextSnapshot(
				[
					source({
						...unlinked,
						images: (unlinked.images ?? []).map((image) => ({
							...image,
							contextReferenceId: "missing",
						})),
					}),
				],
				LIMITS,
				INPUT,
				new AbortController().signal,
				COORDINATE,
			),
		).rejects.toMatchObject({ code: "invalid_context_snapshot" });
	});

	it("fails instead of truncating content-part or aggregate image-byte budgets", async () => {
		await expect(
			prepareGameContextSnapshot(
				[source(contribution())],
				{ ...LIMITS, media: { ...MEDIA_LIMITS, maxContentParts: 1 } },
				INPUT,
				new AbortController().signal,
				COORDINATE,
			),
		).rejects.toMatchObject({ code: "context_content_part_limit_exceeded" });

		await expect(
			prepareGameContextSnapshot(
				[source(contribution(2))],
				{
					...LIMITS,
					media: {
						...MEDIA_LIMITS,
						maxTotalImageBytes: IMAGE_BYTES.byteLength * 2 - 1,
					},
				},
				INPUT,
				new AbortController().signal,
				COORDINATE,
			),
		).rejects.toMatchObject({ code: "context_image_byte_limit_exceeded" });
	});

	it("fails on canonical image identity mismatch and missing host media limits", async () => {
		const mismatched = contribution();
		await expect(
			prepareGameContextSnapshot(
				[
					source({
						...mismatched,
						images: (mismatched.images ?? []).map((image) => ({
							...image,
							reference: { ...image.reference, attachmentId: `img_${"0".repeat(64)}` },
						})),
					}),
				],
				LIMITS,
				INPUT,
				new AbortController().signal,
				COORDINATE,
			),
		).rejects.toMatchObject({ code: "context_image_integrity_mismatch" });

		await expect(
			prepareGameContextSnapshot(
				[source(contribution())],
				{
					maxReferencesPerSnapshot: LIMITS.maxReferencesPerSnapshot,
					maxSnapshotBytes: LIMITS.maxSnapshotBytes,
				},
				INPUT,
				new AbortController().signal,
				COORDINATE,
			),
		).rejects.toMatchObject({ code: "context_media_limits_unavailable" });
	});
});

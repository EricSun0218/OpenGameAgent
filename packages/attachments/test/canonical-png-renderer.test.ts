import { Buffer } from "node:buffer";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import sharp from "sharp";
import { afterEach, describe, expect, it } from "vitest";
import { type ImageAttachmentError, LocalImageAttachmentStore, renderCanonicalPng } from "../src/index.js";

const temporaryRoots: string[] = [];

afterEach(async () => {
	for (const root of temporaryRoots.splice(0)) {
		await rm(root, { recursive: true, force: true });
	}
});

describe("renderCanonicalPng", () => {
	it("streams a metadata-free canonical PNG accepted unchanged by the attachment CAS", async () => {
		const jpeg = await sharp({
			create: { width: 3, height: 2, channels: 3, background: { r: 10, g: 20, b: 30 } },
		})
			.jpeg()
			.toBuffer();
		const encoded = await sharp(jpeg).autoOrient().toColourspace("srgb").png().toBuffer();

		const canonical = await renderCanonicalPng(
			sharp(jpeg).autoOrient().toColourspace("srgb"),
			encoded.byteLength,
			new AbortController().signal,
		);

		expect(canonical).toHaveLength(encoded.byteLength);
		const metadata = await sharp(canonical).metadata();
		expect(metadata).toMatchObject({
			format: "png",
			width: 3,
			height: 2,
			hasProfile: false,
		});
		expect(metadata.density).toBeUndefined();
		expect(pngChunkData(canonical, "pHYs").toString("hex")).toBe("000003e8000003e801");

		const parent = await mkdtemp(join(tmpdir(), "opengameagent-canonical-render-test-"));
		temporaryRoots.push(parent);
		const store = await LocalImageAttachmentStore.open({
			rootDirectory: join(parent, "store"),
			limits: {
				maxInputBytes: jpeg.byteLength,
				maxInputPixels: 6,
				maxInputDimension: 3,
				maxStoredBytes: canonical.byteLength,
				maxStoredPixels: 6,
				maxStoredDimension: 3,
				maxReadBytes: canonical.byteLength,
				maxNameBytes: 1,
				maxImagesPerBatch: 1,
				maxBatchInputBytes: jpeg.byteLength,
				maxBatchStoredBytes: canonical.byteLength,
			},
		});
		const reference = await store.publishCanonicalPng(canonical, new AbortController().signal);

		expect(Buffer.from(await store.read(reference, new AbortController().signal))).toEqual(canonical);
	});

	it("fails at the exact encoded-byte boundary without returning a partial PNG", async () => {
		const pipeline = sharp({
			create: { width: 2, height: 2, channels: 3, background: { r: 3, g: 2, b: 1 } },
		});
		const encoded = await pipeline.clone().png().toBuffer();

		await expectCode(
			renderCanonicalPng(pipeline, encoded.byteLength - 1, new AbortController().signal),
			"normalized_image_too_large",
		);
	});

	it("rejects an invalid output budget and propagates pre-cancellation", async () => {
		await expectCode(
			renderCanonicalPng(
				sharp({ create: { width: 1, height: 1, channels: 3, background: { r: 1, g: 2, b: 3 } } }),
				0,
				new AbortController().signal,
			),
			"invalid_configuration",
		);

		const controller = new AbortController();
		const reason = new Error("cancelled canonical render");
		controller.abort(reason);
		await expect(
			renderCanonicalPng(
				sharp({ create: { width: 1, height: 1, channels: 3, background: { r: 1, g: 2, b: 3 } } }),
				1,
				controller.signal,
			),
		).rejects.toBe(reason);
	});
});

function pngChunkData(png: Buffer, expectedType: string): Buffer {
	let offset = 8;
	while (offset <= png.byteLength - 12) {
		const length = png.readUInt32BE(offset);
		const nextOffset = offset + 12 + length;
		if (nextOffset > png.byteLength) break;
		if (png.toString("ascii", offset + 4, offset + 8) === expectedType) {
			return png.subarray(offset + 8, offset + 8 + length);
		}
		offset = nextOffset;
	}
	throw new Error(`PNG chunk ${expectedType} is missing from the fixture.`);
}

async function expectCode(promise: Promise<unknown>, code: ImageAttachmentError["code"]): Promise<void> {
	await expect(promise).rejects.toMatchObject({ name: "ImageAttachmentError", code });
}

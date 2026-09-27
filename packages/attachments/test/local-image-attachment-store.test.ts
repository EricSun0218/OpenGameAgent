import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import {
	chmod,
	type FileHandle,
	mkdir,
	mkdtemp,
	open,
	readdir,
	rename,
	rm,
	symlink,
	writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { crc32, deflateSync } from "node:zlib";
import sharp from "sharp";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	type CanonicalPngAttachmentStore,
	type ImageAttachmentError,
	type ImageAttachmentInput,
	type ImageAttachmentLimits,
	LocalImageAttachmentStore,
} from "../src/index.js";

vi.mock("node:fs/promises", async (importOriginal) => {
	const actual = await importOriginal<typeof import("node:fs/promises")>();
	return { ...actual, open: vi.fn(actual.open) };
});

interface Fixture {
	readonly bytes: Buffer;
	readonly mediaType: ImageAttachmentInput["mediaType"];
	readonly name?: string;
}

interface DerivedFixtureLimits {
	readonly limits: ImageAttachmentLimits;
	readonly normalized: readonly Buffer[];
}

const temporaryRoots: string[] = [];

afterEach(async () => {
	for (const root of temporaryRoots.splice(0)) {
		await rm(root, { recursive: true, force: true });
	}
});

describe("LocalImageAttachmentStore", () => {
	it("closes the real file handle when cancellation arrives during read open", async () => {
		const canonical = await sharp({ create: { width: 1, height: 1, channels: 3, background: { r: 0, g: 0, b: 0 } } })
			.png()
			.toBuffer();
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const { store } = await openStore({
			...derived.limits,
			maxStoredBytes: canonical.byteLength,
			maxReadBytes: canonical.byteLength,
			maxBatchStoredBytes: canonical.byteLength,
		});
		const reference = await store.publishCanonicalPng(canonical, new AbortController().signal);
		const controller = new AbortController();
		const reason = new Error("cancel after file open");
		const actual = await vi.importActual<typeof import("node:fs/promises")>("node:fs/promises");
		let handle: FileHandle | undefined;
		vi.mocked(open).mockImplementationOnce(async (...parameters) => {
			handle = await actual.open(...parameters);
			controller.abort(reason);
			return handle;
		});
		try {
			await expect(store.read(reference, controller.signal)).rejects.toBe(reason);
			expect(handle?.fd).toBe(-1);
		} finally {
			await handle?.close();
		}
	});

	it("publishes canonical PNG bytes without re-encoding", async () => {
		const canonical = await sharp({
			create: { width: 3, height: 2, channels: 3, background: { r: 80, g: 90, b: 100 } },
		})
			.png({ compressionLevel: 0 })
			.toBuffer();
		const reencoded = await sharp(canonical).png().toBuffer();
		expect(reencoded).not.toEqual(canonical);
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const limits = {
			...derived.limits,
			maxStoredBytes: canonical.byteLength,
			maxReadBytes: canonical.byteLength,
			maxBatchStoredBytes: canonical.byteLength,
		};
		const { store } = await openStore(limits);
		const publisher: CanonicalPngAttachmentStore = store;

		const reference = await publisher.publishCanonicalPng(canonical, new AbortController().signal);

		expect(Object.isFrozen(reference)).toBe(true);
		expect(reference).toEqual({
			type: "image-reference",
			attachmentId: `img_${createHash("sha256").update(canonical).digest("hex")}`,
			mediaType: "image/png",
			bytes: canonical.byteLength,
			width: 3,
			height: 2,
		});
		expect(Buffer.from(await store.read(reference, new AbortController().signal))).toEqual(canonical);
	});

	it("deduplicates canonical PNG publication and preserves it across reopen", async () => {
		const canonical = await sharp({
			create: { width: 2, height: 2, channels: 4, background: { r: 20, g: 40, b: 60, alpha: 0.5 } },
		})
			.png({ compressionLevel: 0 })
			.toBuffer();
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const limits = {
			...derived.limits,
			maxStoredBytes: canonical.byteLength,
			maxReadBytes: canonical.byteLength,
			maxBatchStoredBytes: canonical.byteLength,
		};
		const { root, store } = await openStore(limits);
		const first = await store.publishCanonicalPng(canonical, new AbortController().signal);

		const duplicate = await store.publishCanonicalPng(canonical, new AbortController().signal);
		const reopened = await LocalImageAttachmentStore.open({ rootDirectory: root, limits });

		expect(duplicate).toEqual(first);
		expect(Buffer.from(await reopened.read(first, new AbortController().signal))).toEqual(canonical);
	});

	it("distinguishes a missing addressed object from unavailable storage", async () => {
		const canonical = await sharp({
			create: { width: 2, height: 2, channels: 3, background: { r: 10, g: 20, b: 30 } },
		})
			.png()
			.toBuffer();
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const { root, store } = await openStore(derived.limits);
		const reference = await store.publishCanonicalPng(canonical, new AbortController().signal);
		const digest = reference.attachmentId.slice(4);
		await rm(join(root, "objects", digest.slice(0, 2), digest.slice(2, 4), `${reference.attachmentId}.png`));

		await expectCode(store.read(reference, new AbortController().signal), "storage_missing");
	});

	it("rechecks the stored dimension limit after reopening a CAS", async () => {
		const canonical = await sharp({
			create: { width: 3, height: 2, channels: 3, background: { r: 40, g: 50, b: 60 } },
		})
			.png()
			.toBuffer();
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const limits = {
			...derived.limits,
			maxStoredBytes: canonical.byteLength,
			maxReadBytes: canonical.byteLength,
			maxBatchStoredBytes: canonical.byteLength,
		};
		const { root, store } = await openStore(limits);
		const reference = await store.publishCanonicalPng(canonical, new AbortController().signal);
		const stricter = await LocalImageAttachmentStore.open({
			rootDirectory: root,
			limits: { ...limits, maxStoredDimension: 2 },
		});

		await expectCode(stricter.read(reference, new AbortController().signal), "storage_corrupt");
	});

	it("fully decodes a canonical PNG before publishing it", async () => {
		const canonical = await sharp({
			create: { width: 3, height: 2, channels: 3, background: { r: 1, g: 2, b: 3 } },
		})
			.png({ compressionLevel: 0 })
			.toBuffer();
		const truncated = canonical.subarray(0, canonical.byteLength - 20);
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const { root, store } = await openStore({
			...derived.limits,
			maxStoredBytes: canonical.byteLength,
			maxReadBytes: canonical.byteLength,
			maxBatchStoredBytes: canonical.byteLength,
		});

		await expectCode(store.publishCanonicalPng(truncated, new AbortController().signal), "decode_failed");
		expect(await storedObjects(root)).toEqual([]);
	});

	it("rejects a PNG that still carries source metadata", async () => {
		const withMetadata = await sharp({
			create: { width: 2, height: 2, channels: 3, background: { r: 3, g: 2, b: 1 } },
		})
			.withMetadata({ orientation: 6 })
			.png()
			.toBuffer();
		const derived = await deriveLimits([{ bytes: withMetadata, mediaType: "image/png" }]);
		const { root, store } = await openStore({
			...derived.limits,
			maxStoredBytes: withMetadata.byteLength,
			maxReadBytes: withMetadata.byteLength,
			maxBatchStoredBytes: withMetadata.byteLength,
		});

		await expectCode(store.publishCanonicalPng(withMetadata, new AbortController().signal), "invalid_input");
		expect(await storedObjects(root)).toEqual([]);
	});

	it("rejects hidden PNG metadata and bytes after IEND before publishing", async () => {
		const canonical = await sharp({
			create: { width: 2, height: 2, channels: 3, background: { r: 3, g: 2, b: 1 } },
		})
			.png()
			.toBuffer();
		const hiddenText = insertBeforeIend(canonical, pngChunk("tEXt", Buffer.from("Author\0hidden", "latin1")));
		const trailingPayload = Buffer.concat([canonical, Buffer.from("hidden")]);
		const density = Buffer.alloc(9);
		density.writeUInt32BE(11_811, 0);
		density.writeUInt32BE(11_811, 4);
		density[8] = 1;
		const physicalMetadata = replacePngChunk(canonical, "pHYs", density);
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const maxBytes = Math.max(hiddenText.byteLength, trailingPayload.byteLength, physicalMetadata.byteLength);
		const { root, store } = await openStore({
			...derived.limits,
			maxStoredBytes: maxBytes,
			maxReadBytes: maxBytes,
			maxBatchStoredBytes: maxBytes,
		});

		await expectCode(store.publishCanonicalPng(hiddenText, new AbortController().signal), "invalid_input");
		await expectCode(store.publishCanonicalPng(trailingPayload, new AbortController().signal), "invalid_input");
		await expectCode(store.publishCanonicalPng(physicalMetadata, new AbortController().signal), "invalid_input");
		expect(await storedObjects(root)).toEqual([]);
	});

	it("rejects invalid PNG chunk CRCs during publication and stored verification", async () => {
		const canonical = await sharp({
			create: { width: 2, height: 2, channels: 3, background: { r: 4, g: 5, b: 6 } },
		})
			.png()
			.toBuffer();
		const invalidPhysicalCrc = corruptPngChunkCrc(canonical, "pHYs");
		const invalidEndCrc = corruptPngChunkCrc(canonical, "IEND");
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const published = await openStore(derived.limits);

		await expectCode(
			published.store.publishCanonicalPng(invalidPhysicalCrc, new AbortController().signal),
			"decode_failed",
		);
		await expectCode(published.store.publishCanonicalPng(invalidEndCrc, new AbortController().signal), "decode_failed");
		expect(await storedObjects(published.root)).toEqual([]);

		const preloaded = await openStore(derived.limits);
		const reference = await preloadPng(preloaded.root, invalidEndCrc, 2, 2);
		await expectCode(preloaded.store.read(reference, new AbortController().signal), "storage_corrupt");
	});

	it("rejects an animated PNG before publishing it", async () => {
		const animated = createAnimatedPng();
		const derived = await deriveLimits([{ bytes: animated, mediaType: "image/png" }]);
		const { root, store } = await openStore({
			...derived.limits,
			maxStoredBytes: animated.byteLength,
			maxReadBytes: animated.byteLength,
			maxBatchStoredBytes: animated.byteLength,
		});

		await expectCode(store.publishCanonicalPng(animated, new AbortController().signal), "animated_image_unsupported");
		expect(await storedObjects(root)).toEqual([]);
	});

	it("rejects a single-frame APNG during publication and stored verification", async () => {
		const animated = createSingleFrameAnimatedPng();
		const derived = await deriveLimits([{ bytes: animated, mediaType: "image/png" }]);
		const limits = {
			...derived.limits,
			maxStoredBytes: animated.byteLength,
			maxReadBytes: animated.byteLength,
			maxBatchStoredBytes: animated.byteLength,
		};
		const published = await openStore(limits);

		await expectCode(
			published.store.publishCanonicalPng(animated, new AbortController().signal),
			"animated_image_unsupported",
		);
		expect(await storedObjects(published.root)).toEqual([]);

		const preloaded = await openStore(limits);
		const reference = await preloadPng(preloaded.root, animated, 1, 1);
		await expectCode(preloaded.store.read(reference, new AbortController().signal), "storage_corrupt");
	});

	it("rejects a canonical PNG outside the stored dimension limit", async () => {
		const canonical = await sharp({
			create: { width: 3, height: 2, channels: 3, background: { r: 10, g: 20, b: 30 } },
		})
			.png()
			.toBuffer();
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const { root, store } = await openStore({
			...derived.limits,
			maxStoredBytes: canonical.byteLength,
			maxStoredDimension: 2,
			maxReadBytes: canonical.byteLength,
			maxBatchStoredBytes: canonical.byteLength,
		});

		await expectCode(store.publishCanonicalPng(canonical, new AbortController().signal), "normalized_image_too_large");
		expect(await storedObjects(root)).toEqual([]);
	});

	it("rejects empty canonical PNG input without publishing", async () => {
		const fixture = required((await supportedFixtures())[0]);
		const derived = await deriveLimits([fixture]);
		const { root, store } = await openStore(derived.limits);

		await expectCode(store.publishCanonicalPng(new Uint8Array(), new AbortController().signal), "invalid_input");
		expect(await storedObjects(root)).toEqual([]);
	});

	it("rejects a non-PNG derivative with a media type mismatch", async () => {
		const jpeg = await sharp({
			create: { width: 2, height: 2, channels: 3, background: { r: 10, g: 10, b: 10 } },
		})
			.jpeg()
			.toBuffer();
		const derived = await deriveLimits([{ bytes: jpeg, mediaType: "image/jpeg" }]);
		const { root, store } = await openStore({
			...derived.limits,
			maxStoredBytes: jpeg.byteLength,
			maxReadBytes: jpeg.byteLength,
			maxBatchStoredBytes: jpeg.byteLength,
		});

		await expectCode(store.publishCanonicalPng(jpeg, new AbortController().signal), "media_type_mismatch");
		expect(await storedObjects(root)).toEqual([]);
	});

	it("enforces stored byte and pixel limits before publication", async () => {
		const canonical = await sharp({
			create: { width: 2, height: 2, channels: 3, background: { r: 30, g: 20, b: 10 } },
		})
			.png()
			.toBuffer();
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const byteLimited = await openStore({
			...derived.limits,
			maxStoredBytes: canonical.byteLength - 1,
			maxReadBytes: canonical.byteLength,
			maxBatchStoredBytes: canonical.byteLength,
		});
		const pixelLimited = await openStore({
			...derived.limits,
			maxStoredBytes: canonical.byteLength,
			maxStoredPixels: 3,
			maxReadBytes: canonical.byteLength,
			maxBatchStoredBytes: canonical.byteLength,
		});

		await expectCode(
			byteLimited.store.publishCanonicalPng(canonical, new AbortController().signal),
			"normalized_image_too_large",
		);
		await expectCode(
			pixelLimited.store.publishCanonicalPng(canonical, new AbortController().signal),
			"normalized_image_too_large",
		);
		expect(await storedObjects(byteLimited.root)).toEqual([]);
		expect(await storedObjects(pixelLimited.root)).toEqual([]);
	});

	it("captures canonical PNG bytes before its first await", async () => {
		const canonical = await sharp({
			create: { width: 2, height: 2, channels: 3, background: { r: 90, g: 80, b: 70 } },
		})
			.png({ compressionLevel: 0 })
			.toBuffer();
		const derived = await deriveLimits([{ bytes: canonical, mediaType: "image/png" }]);
		const { store } = await openStore({
			...derived.limits,
			maxStoredBytes: canonical.byteLength,
			maxReadBytes: canonical.byteLength,
			maxBatchStoredBytes: canonical.byteLength,
		});
		const mutable = Uint8Array.from(canonical);

		const publication = store.publishCanonicalPng(mutable, new AbortController().signal);
		mutable.fill(0);
		const reference = await publication;

		expect(reference.attachmentId).toBe(`img_${createHash("sha256").update(canonical).digest("hex")}`);
		expect(Buffer.from(await store.read(reference, new AbortController().signal))).toEqual(canonical);
	});

	it("propagates pre-cancellation before canonical PNG validation or publication", async () => {
		const fixture = required((await supportedFixtures())[0]);
		const derived = await deriveLimits([fixture]);
		const { root, store } = await openStore(derived.limits);
		const controller = new AbortController();
		const reason = new Error("cancelled canonical publication");
		controller.abort(reason);

		await expect(store.publishCanonicalPng(fixture.bytes, controller.signal)).rejects.toBe(reason);
		expect(await storedObjects(root)).toEqual([]);
	});

	it("fully decodes supported still formats, normalizes to PNG, deduplicates, and verifies reads", async () => {
		const fixtures = await supportedFixtures();
		const derived = await deriveLimits(fixtures);
		const { store } = await openStore(derived.limits);

		const references = await store.admit(
			fixtures.map((fixture) => ({
				encoding: "bytes" as const,
				data: fixture.bytes,
				mediaType: fixture.mediaType,
				...(fixture.name ? { name: fixture.name } : {}),
			})),
			new AbortController().signal,
		);

		expect(references).toHaveLength(fixtures.length);
		for (const [index, reference] of references.entries()) {
			const expected = derived.normalized[index];
			expect(expected).toBeDefined();
			expect(reference).toMatchObject({
				type: "image-reference",
				mediaType: "image/png",
				bytes: expected?.byteLength,
				attachmentId: `img_${createHash("sha256")
					.update(expected ?? Buffer.alloc(0))
					.digest("hex")}`,
			});
			expect(Buffer.from(await store.read(reference, new AbortController().signal))).toEqual(expected);
		}

		const duplicate = await store.admit(
			[
				{
					encoding: "base64",
					data: fixtures[0]?.bytes.toString("base64") ?? "",
					mediaType: fixtures[0]?.mediaType ?? "image/png",
					...(fixtures[0]?.name ? { name: fixtures[0].name } : {}),
				},
			],
			new AbortController().signal,
		);
		expect(duplicate[0]?.attachmentId).toBe(references[0]?.attachmentId);
		expect(duplicate[0]?.name).toBe(fixtures[0]?.name);
	});

	it("captures admitted byte inputs before its first await", async () => {
		const fixture = required((await supportedFixtures())[1]);
		const derived = await deriveLimits([fixture]);
		const expected = required(derived.normalized[0]);
		const { store } = await openStore(derived.limits);
		const mutable = Uint8Array.from(fixture.bytes);

		const admission = store.admit(
			[{ encoding: "bytes", data: mutable, mediaType: fixture.mediaType }],
			new AbortController().signal,
		);
		mutable.fill(0);
		const reference = required((await admission)[0]);

		expect(Buffer.from(await store.read(reference, new AbortController().signal))).toEqual(expected);
	});

	it("applies EXIF orientation, strips metadata, and derives a bounded resize", async () => {
		const input = await sharp({
			create: { width: 2, height: 3, channels: 3, background: { r: 12, g: 34, b: 56 } },
		})
			.withMetadata({ orientation: 6 })
			.jpeg()
			.toBuffer();
		const fixture: Fixture = { bytes: input, mediaType: "image/jpeg", name: "旋转.jpg" };
		const derived = await deriveLimits([fixture]);
		const resizedLimits = {
			...derived.limits,
			maxStoredDimension: 2,
			maxStoredPixels: 3,
		};
		const { store } = await openStore(resizedLimits);
		const [admittedReference] = await store.admit(
			[
				{
					encoding: "bytes",
					data: input,
					mediaType: "image/jpeg",
					...(fixture.name ? { name: fixture.name } : {}),
				},
			],
			new AbortController().signal,
		);
		const reference = required(admittedReference);
		expect(reference).toMatchObject({ width: 2, height: 1, name: fixture.name });
		const output = Buffer.from(await store.read(reference, new AbortController().signal));
		const metadata = await sharp(output).metadata();
		expect(metadata).toMatchObject({ format: "png", width: 2, height: 1, hasProfile: false });
		expect(metadata.orientation).toBeUndefined();
		expect(metadata.density).toBeUndefined();
		expect(metadata.exif).toBeUndefined();
	});

	it("rejects non-canonical input, media mismatch, malformed images, animation, and byte overflow", async () => {
		const fixture = required((await supportedFixtures())[0]);
		const derived = await deriveLimits([fixture]);
		const { store } = await openStore(derived.limits);
		const signal = new AbortController().signal;

		await expectCode(
			store.admit(
				[{ encoding: "base64", data: `${fixture.bytes.toString("base64")}\n`, mediaType: fixture.mediaType }],
				signal,
			),
			"invalid_input",
		);
		await expectCode(
			store.admit([{ encoding: "bytes", data: fixture.bytes, mediaType: "image/jpeg" }], signal),
			"media_type_mismatch",
		);
		await expectCode(
			store.admit(
				[
					{
						encoding: "bytes",
						data: fixture.bytes.subarray(0, Math.floor(fixture.bytes.length / 2)),
						mediaType: "image/png",
					},
				],
				signal,
			),
			"decode_failed",
		);
		const animated = Buffer.from(
			"47494638396101000100800000000000ffffff21f90400000000002c000000000100010000020244010021f90400000000002c00000000010001000002024c01003b",
			"hex",
		);
		const animatedLimits = await deriveLimits([{ bytes: animated, mediaType: "image/gif" }], true);
		const { store: animationStore } = await openStore(animatedLimits.limits);
		await expectCode(
			animationStore.admit([{ encoding: "bytes", data: animated, mediaType: "image/gif" }], signal),
			"animated_image_unsupported",
		);
		await expectCode(
			store.admit(
				[
					{
						encoding: "bytes",
						data: Buffer.concat([fixture.bytes, Buffer.alloc(1)]),
						mediaType: fixture.mediaType,
					},
				],
				signal,
			),
			"input_too_large",
		);

		const dimensionParent = await createTemporaryRoot();
		const dimensionStore = await LocalImageAttachmentStore.open({
			rootDirectory: join(dimensionParent, "store"),
			limits: { ...derived.limits, maxInputDimension: derived.limits.maxInputDimension - 1 },
		});
		await expectCode(
			dimensionStore.admit([{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType }], signal),
			"input_too_large",
		);

		const normalizedBytes = derived.normalized[0]?.byteLength ?? 0;
		expect(normalizedBytes).toBeGreaterThan(1);
		const storedParent = await createTemporaryRoot();
		const storedStore = await LocalImageAttachmentStore.open({
			rootDirectory: join(storedParent, "store"),
			limits: {
				...derived.limits,
				maxStoredBytes: normalizedBytes - 1,
				maxReadBytes: normalizedBytes - 1,
				maxBatchStoredBytes: normalizedBytes - 1,
			},
		});
		await expectCode(
			storedStore.admit([{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType }], signal),
			"normalized_image_too_large",
		);
	});

	it("validates a complete batch before publishing any object", async () => {
		const fixture = required((await supportedFixtures())[0]);
		const corrupt: Fixture = {
			bytes: Buffer.alloc(fixture.bytes.byteLength),
			mediaType: fixture.mediaType,
		};
		const derived = await deriveLimits([fixture, corrupt], true);
		const { root, store } = await openStore(derived.limits);
		await expectCode(
			store.admit(
				[
					{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType },
					{ encoding: "bytes", data: corrupt.bytes, mediaType: corrupt.mediaType },
				],
				new AbortController().signal,
			),
			"decode_failed",
		);
		const entries = await readdir(join(root, "objects"), { recursive: true });
		expect(entries.filter((entry) => entry.endsWith(".png"))).toEqual([]);

		const budgetParent = await createTemporaryRoot();
		const budgetRoot = join(budgetParent, "store");
		const budgetStore = await LocalImageAttachmentStore.open({
			rootDirectory: budgetRoot,
			limits: {
				...derived.limits,
				maxBatchInputBytes: fixture.bytes.byteLength,
			},
		});
		await expectCode(
			budgetStore.admit(
				[
					{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType },
					{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType },
				],
				new AbortController().signal,
			),
			"input_too_large",
		);
		expect(
			(await readdir(join(budgetRoot, "objects"), { recursive: true })).filter((entry) => entry.endsWith(".png")),
		).toEqual([]);
	});

	it("rejects internally inconsistent host limits before creating storage", async () => {
		const fixture = required((await supportedFixtures())[0]);
		const derived = await deriveLimits([fixture]);
		const parent = await createTemporaryRoot();
		const root = join(parent, "store");
		await expectCode(
			LocalImageAttachmentStore.open({
				rootDirectory: root,
				limits: { ...derived.limits, maxReadBytes: derived.limits.maxStoredBytes - 1 },
			}),
			"invalid_configuration",
		);
		await expect(readdir(parent)).resolves.toEqual([]);
	});

	it("fails closed for stored corruption, bucket symlinks, and root retargeting", async () => {
		const fixture = required((await supportedFixtures())[0]);
		const derived = await deriveLimits([fixture]);
		const first = await openStore(derived.limits);
		const [admittedReference] = await first.store.admit(
			[{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType }],
			new AbortController().signal,
		);
		const reference = required(admittedReference);
		const object = objectPath(first.root, reference.attachmentId);
		if (process.platform !== "win32") await chmod(object, 0o600);
		await writeFile(object, Buffer.alloc(reference.bytes));
		await expectCode(first.store.read(reference, new AbortController().signal), "storage_corrupt");
		await expectCode(
			first.store.admit(
				[{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType }],
				new AbortController().signal,
			),
			"storage_corrupt",
		);

		const symlinkParent = await createTemporaryRoot();
		const storeRoot = join(symlinkParent, "store");
		const outside = join(symlinkParent, "outside");
		await mkdir(outside);
		const symlinkStore = await LocalImageAttachmentStore.open({ rootDirectory: storeRoot, limits: derived.limits });
		const digest = createHash("sha256").update(required(derived.normalized[0])).digest("hex");
		const firstBucket = join(storeRoot, "objects", digest.slice(0, 2));
		await mkdir(firstBucket, { recursive: true });
		await symlink(outside, join(firstBucket, digest.slice(2, 4)), process.platform === "win32" ? "junction" : "dir");
		await expectCode(
			symlinkStore.admit(
				[{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType }],
				new AbortController().signal,
			),
			"storage_changed",
		);
		expect(await readdir(outside)).toEqual([]);

		const firstLevelParent = await createTemporaryRoot();
		const firstLevelRoot = join(firstLevelParent, "store");
		const firstLevelOutside = join(firstLevelParent, "outside");
		await mkdir(firstLevelOutside);
		const firstLevelStore = await LocalImageAttachmentStore.open({
			rootDirectory: firstLevelRoot,
			limits: derived.limits,
		});
		await symlink(
			firstLevelOutside,
			join(firstLevelRoot, "objects", digest.slice(0, 2)),
			process.platform === "win32" ? "junction" : "dir",
		);
		await expectCode(
			firstLevelStore.admit(
				[{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType }],
				new AbortController().signal,
			),
			"storage_changed",
		);
		expect(await readdir(firstLevelOutside)).toEqual([]);

		const retargetParent = await createTemporaryRoot();
		const addressed = join(retargetParent, "store");
		const moved = join(retargetParent, "moved");
		const retargeted = await LocalImageAttachmentStore.open({ rootDirectory: addressed, limits: derived.limits });
		const [admittedRetargetReference] = await retargeted.admit(
			[{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType }],
			new AbortController().signal,
		);
		const retargetReference = required(admittedRetargetReference);
		await rename(addressed, moved);
		await mkdir(addressed);
		await expectCode(retargeted.read(retargetReference, new AbortController().signal), "storage_changed");
	});

	it.each(["objects", "temporary"] as const)("rejects a %s directory retargeted through a link", async (name) => {
		const fixture = required((await supportedFixtures())[0]);
		const derived = await deriveLimits([fixture]);
		const parent = await createTemporaryRoot();
		const root = join(parent, "store");
		const moved = join(parent, `${name}-moved`);
		const store = await LocalImageAttachmentStore.open({ rootDirectory: root, limits: derived.limits });
		await rename(join(root, name), moved);
		await symlink(moved, join(root, name), process.platform === "win32" ? "junction" : "dir");

		await expectCode(
			store.admit(
				[{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType }],
				new AbortController().signal,
			),
			"storage_changed",
		);
		expect(await readdir(moved)).toEqual([]);
	});

	it("honors cancellation before validation or publication", async () => {
		const fixture = required((await supportedFixtures())[0]);
		const derived = await deriveLimits([fixture]);
		const { root, store } = await openStore(derived.limits);
		const controller = new AbortController();
		const reason = new Error("cancelled");
		controller.abort(reason);
		await expect(
			store.admit([{ encoding: "bytes", data: fixture.bytes, mediaType: fixture.mediaType }], controller.signal),
		).rejects.toBe(reason);
		const entries = await readdir(join(root, "objects"), { recursive: true });
		expect(entries.filter((entry) => entry.endsWith(".png"))).toEqual([]);
	});
});

async function supportedFixtures(): Promise<readonly Fixture[]> {
	const source = sharp({ create: { width: 3, height: 2, channels: 3, background: { r: 80, g: 90, b: 100 } } });
	return [
		{ bytes: await source.clone().png().toBuffer(), mediaType: "image/png", name: "image.png" },
		{ bytes: await source.clone().jpeg().toBuffer(), mediaType: "image/jpeg", name: "image.jpg" },
		{ bytes: await source.clone().webp().toBuffer(), mediaType: "image/webp", name: "image.webp" },
		{ bytes: await source.clone().gif().toBuffer(), mediaType: "image/gif", name: "image.gif" },
	];
}

async function deriveLimits(fixtures: readonly Fixture[], allowInvalid = false): Promise<DerivedFixtureLimits> {
	const normalized: Buffer[] = [];
	let maxInputPixels = 1;
	let maxInputDimension = 1;
	for (const fixture of fixtures) {
		try {
			const metadata = await sharp(fixture.bytes, { animated: true }).metadata();
			maxInputPixels = Math.max(maxInputPixels, metadata.autoOrient.width * metadata.autoOrient.height);
			maxInputDimension = Math.max(maxInputDimension, metadata.autoOrient.width, metadata.autoOrient.height);
			if ((metadata.pages ?? 1) === 1) {
				const output = await sharp(fixture.bytes).autoOrient().toColourspace("srgb").png().toBuffer();
				normalized.push(replacePngChunk(output, "pHYs", Buffer.from("000003e8000003e801", "hex")));
			} else {
				normalized.push(Buffer.from([0]));
			}
		} catch (error) {
			if (!allowInvalid) throw error;
			normalized.push(Buffer.from([0]));
		}
	}
	const inputBytes = fixtures.map((fixture) => fixture.bytes.byteLength);
	const storedBytes = normalized.map((entry) => entry.byteLength);
	const names = fixtures.flatMap((fixture) =>
		fixture.name ? [new TextEncoder().encode(fixture.name).byteLength] : [],
	);
	return {
		limits: {
			maxInputBytes: Math.max(...inputBytes),
			maxInputPixels,
			maxInputDimension,
			maxStoredBytes: Math.max(...storedBytes),
			maxStoredPixels: maxInputPixels,
			maxStoredDimension: maxInputDimension,
			maxReadBytes: Math.max(...storedBytes),
			maxNameBytes: Math.max(1, ...names),
			maxImagesPerBatch: fixtures.length,
			maxBatchInputBytes: inputBytes.reduce((sum, value) => sum + value, 0),
			maxBatchStoredBytes: storedBytes.reduce((sum, value) => sum + value, 0),
		},
		normalized,
	};
}

async function openStore(limits: ImageAttachmentLimits) {
	const parent = await createTemporaryRoot();
	const root = join(parent, "store");
	return { root, store: await LocalImageAttachmentStore.open({ rootDirectory: root, limits }) };
}

async function createTemporaryRoot(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-attachments-test-"));
	temporaryRoots.push(root);
	return root;
}

async function storedObjects(root: string): Promise<readonly string[]> {
	return (await readdir(join(root, "objects"), { recursive: true })).filter((entry) => entry.endsWith(".png"));
}

function createAnimatedPng(): Buffer {
	const header = Buffer.alloc(13);
	header.writeUInt32BE(1, 0);
	header.writeUInt32BE(1, 4);
	header[8] = 8;
	header[9] = 6;
	const animation = Buffer.alloc(8);
	animation.writeUInt32BE(2, 0);
	const firstFrame = createFrameControl(0);
	const secondFrame = createFrameControl(1);
	const secondPixels = deflateSync(Buffer.from([0, 0, 0, 255, 255]));
	const secondData = Buffer.alloc(4 + secondPixels.byteLength);
	secondData.writeUInt32BE(2, 0);
	secondPixels.copy(secondData, 4);
	return Buffer.concat([
		Buffer.from("89504e470d0a1a0a", "hex"),
		pngChunk("IHDR", header),
		pngChunk("acTL", animation),
		pngChunk("fcTL", firstFrame),
		pngChunk("IDAT", deflateSync(Buffer.from([0, 255, 0, 0, 255]))),
		pngChunk("fcTL", secondFrame),
		pngChunk("fdAT", secondData),
		pngChunk("IEND", Buffer.alloc(0)),
	]);
}

function createSingleFrameAnimatedPng(): Buffer {
	const header = Buffer.alloc(13);
	header.writeUInt32BE(1, 0);
	header.writeUInt32BE(1, 4);
	header[8] = 8;
	header[9] = 6;
	const animation = Buffer.alloc(8);
	animation.writeUInt32BE(1, 0);
	return Buffer.concat([
		Buffer.from("89504e470d0a1a0a", "hex"),
		pngChunk("IHDR", header),
		pngChunk("acTL", animation),
		pngChunk("fcTL", createFrameControl(0)),
		pngChunk("IDAT", deflateSync(Buffer.from([0, 255, 0, 0, 255]))),
		pngChunk("IEND", Buffer.alloc(0)),
	]);
}

function insertBeforeIend(png: Buffer, chunk: Buffer): Buffer {
	const iendBytes = 12;
	return Buffer.concat([png.subarray(0, -iendBytes), chunk, png.subarray(-iendBytes)]);
}

function replacePngChunk(png: Buffer, expectedType: string, data: Buffer): Buffer {
	let offset = 8;
	while (offset <= png.byteLength - 12) {
		const length = png.readUInt32BE(offset);
		const nextOffset = offset + 12 + length;
		if (nextOffset > png.byteLength) break;
		if (png.toString("ascii", offset + 4, offset + 8) === expectedType) {
			return Buffer.concat([png.subarray(0, offset), pngChunk(expectedType, data), png.subarray(nextOffset)]);
		}
		offset = nextOffset;
	}
	throw new Error(`PNG chunk ${expectedType} is missing from the fixture.`);
}

function corruptPngChunkCrc(png: Buffer, expectedType: string): Buffer {
	const corrupted = Buffer.from(png);
	let offset = 8;
	while (offset <= corrupted.byteLength - 12) {
		const length = corrupted.readUInt32BE(offset);
		const nextOffset = offset + 12 + length;
		if (nextOffset > corrupted.byteLength) break;
		if (corrupted.toString("ascii", offset + 4, offset + 8) === expectedType) {
			corrupted[nextOffset - 1] = required(corrupted[nextOffset - 1]) ^ 1;
			return corrupted;
		}
		offset = nextOffset;
	}
	throw new Error(`PNG chunk ${expectedType} is missing from the fixture.`);
}

function createFrameControl(sequence: number): Buffer {
	const control = Buffer.alloc(26);
	control.writeUInt32BE(sequence, 0);
	control.writeUInt32BE(1, 4);
	control.writeUInt32BE(1, 8);
	control.writeUInt16BE(1, 20);
	control.writeUInt16BE(10, 22);
	return control;
}

function pngChunk(type: string, data: Buffer): Buffer {
	const typeBytes = Buffer.from(type, "ascii");
	const chunk = Buffer.alloc(12 + data.byteLength);
	chunk.writeUInt32BE(data.byteLength, 0);
	typeBytes.copy(chunk, 4);
	data.copy(chunk, 8);
	chunk.writeUInt32BE(crc32(Buffer.concat([typeBytes, data])) >>> 0, 8 + data.byteLength);
	return chunk;
}

function objectPath(root: string, attachmentId: string): string {
	const digest = attachmentId.slice(4);
	return join(root, "objects", digest.slice(0, 2), digest.slice(2, 4), `${attachmentId}.png`);
}

async function preloadPng(root: string, bytes: Buffer, width: number, height: number) {
	const attachmentId = `img_${createHash("sha256").update(bytes).digest("hex")}`;
	const reference = {
		type: "image-reference" as const,
		attachmentId,
		mediaType: "image/png" as const,
		bytes: bytes.byteLength,
		width,
		height,
	};
	const path = objectPath(root, attachmentId);
	await mkdir(dirname(path), { recursive: true });
	await writeFile(path, bytes);
	return reference;
}

function required<T>(value: T | undefined): T {
	if (value === undefined) throw new Error("required test value is missing");
	return value;
}

async function expectCode(promise: Promise<unknown>, code: ImageAttachmentError["code"]): Promise<void> {
	await expect(promise).rejects.toMatchObject({ name: "ImageAttachmentError", code });
}

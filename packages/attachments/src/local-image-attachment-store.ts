import { Buffer } from "node:buffer";
import { createHash, randomUUID } from "node:crypto";
import { constants as fsConstants } from "node:fs";
import type { FileHandle } from "node:fs/promises";
import { chmod, link, lstat, mkdir, open, realpath, stat, unlink } from "node:fs/promises";
import { dirname, isAbsolute, join, parse, resolve, sep } from "node:path";
import { inspectPng, PngInspectionError } from "@opengameagent/png";
import type { ImageMediaType, ImageReferenceContent } from "@opengameagent/protocol";
import { ImageReferenceContentSchema } from "@opengameagent/protocol";
import sharp from "sharp";
import { Value } from "typebox/value";
import { bindSharpAbort, renderCanonicalPng, sharpCanonicalPhysicalPixels } from "./canonical-png-renderer.js";
import { ImageAttachmentError } from "./errors.js";
import type {
	CanonicalPngAttachmentStore,
	ImageAttachmentInput,
	ImageAttachmentLimits,
	ImageAttachmentStore,
} from "./types.js";
import {
	checkedAdd,
	createImageReference,
	prepareImageAttachmentInput,
	prepareImageAttachmentLimits,
} from "./validation.js";

const formatByMediaType = new Map<ImageMediaType, string>([
	["image/png", "png"],
	["image/jpeg", "jpeg"],
	["image/webp", "webp"],
	["image/gif", "gif"],
]);

export interface LocalImageAttachmentStoreOptions {
	readonly rootDirectory: string;
	readonly limits: ImageAttachmentLimits;
}

interface PreparedObject {
	readonly bytes: Buffer;
	readonly reference: ImageReferenceContent;
}

interface FileIdentity {
	readonly dev: number | bigint;
	readonly ino: number | bigint;
}

type CanonicalPngInspection =
	| {
			readonly kind: "publication";
			readonly maxPixels: number;
			readonly maxDimension: number;
	  }
	| {
			readonly kind: "storage";
			readonly reference: Readonly<ImageReferenceContent>;
			readonly maxPixels: number;
			readonly maxDimension: number;
	  };

type CanonicalPngStructure = "canonical" | "animated" | "noncanonical" | "malformed";

export class LocalImageAttachmentStore implements CanonicalPngAttachmentStore, ImageAttachmentStore {
	private constructor(
		private readonly addressedRoot: string,
		private readonly canonicalRoot: string,
		private readonly objectsRoot: string,
		private readonly temporaryRoot: string,
		private readonly rootIdentity: FileIdentity,
		private readonly objectsIdentity: FileIdentity,
		private readonly temporaryIdentity: FileIdentity,
		private readonly limits: Readonly<ImageAttachmentLimits>,
	) {}

	static async open(options: LocalImageAttachmentStoreOptions): Promise<LocalImageAttachmentStore> {
		if (
			!options ||
			typeof options.rootDirectory !== "string" ||
			options.rootDirectory.length === 0 ||
			options.rootDirectory.includes("\0")
		) {
			throw new ImageAttachmentError("invalid_configuration");
		}
		const limits = prepareImageAttachmentLimits(options.limits);
		const addressedRoot = resolve(options.rootDirectory);
		if (!isAbsolute(addressedRoot) || addressedRoot === parse(addressedRoot).root) {
			throw new ImageAttachmentError("invalid_configuration");
		}
		try {
			await mkdir(addressedRoot, { recursive: true, mode: 0o700 });
			const canonicalRoot = resolve(await realpath(addressedRoot));
			if (canonicalRoot === parse(canonicalRoot).root) throw new ImageAttachmentError("invalid_configuration");
			const objectsRoot = join(canonicalRoot, "objects");
			const temporaryRoot = join(canonicalRoot, "temporary");
			await createPrivateDirectory(objectsRoot, canonicalRoot);
			await createPrivateDirectory(temporaryRoot, canonicalRoot);
			if (process.platform !== "win32") {
				await chmod(canonicalRoot, 0o700);
				await chmod(objectsRoot, 0o700);
				await chmod(temporaryRoot, 0o700);
			}
			await syncDirectory(canonicalRoot);
			return new LocalImageAttachmentStore(
				addressedRoot,
				canonicalRoot,
				objectsRoot,
				temporaryRoot,
				await directoryIdentity(canonicalRoot),
				await directoryIdentity(objectsRoot),
				await directoryIdentity(temporaryRoot),
				limits,
			);
		} catch (error) {
			if (error instanceof ImageAttachmentError) throw error;
			throw new ImageAttachmentError("storage_unavailable");
		}
	}

	async admit(
		inputs: readonly Readonly<ImageAttachmentInput>[],
		signal: AbortSignal,
	): Promise<readonly ImageReferenceContent[]> {
		signal.throwIfAborted();
		if (!Array.isArray(inputs) || inputs.length === 0) throw new ImageAttachmentError("invalid_input");
		if (inputs.length > this.limits.maxImagesPerBatch) throw new ImageAttachmentError("input_too_large");
		const admittedInputs: ReturnType<typeof prepareImageAttachmentInput>[] = [];
		let totalInputBytes = 0;
		for (const input of inputs) {
			signal.throwIfAborted();
			const admitted = prepareImageAttachmentInput(input, this.limits);
			totalInputBytes = checkedAdd(totalInputBytes, admitted.bytes.byteLength, this.limits.maxBatchInputBytes);
			admittedInputs.push(admitted);
		}
		await this.assertRootStable(signal);

		const prepared: PreparedObject[] = [];
		let totalStoredBytes = 0;
		for (const admitted of admittedInputs) {
			signal.throwIfAborted();
			const normalized = await this.normalize(admitted.bytes, admitted.mediaType, admitted.name, signal);
			totalStoredBytes = checkedAdd(totalStoredBytes, normalized.bytes.byteLength, this.limits.maxBatchStoredBytes);
			prepared.push(normalized);
		}

		// No object is published until every member has fully decoded, normalized and passed the batch budgets.
		for (const object of new Map(prepared.map((entry) => [entry.reference.attachmentId, entry])).values()) {
			signal.throwIfAborted();
			await this.publish(object, signal);
		}
		return Object.freeze(prepared.map((entry) => Object.freeze({ ...entry.reference })));
	}

	async publishCanonicalPng(bytes: Uint8Array, signal: AbortSignal): Promise<Readonly<ImageReferenceContent>> {
		signal.throwIfAborted();
		if (!(bytes instanceof Uint8Array) || bytes.byteLength === 0) {
			throw new ImageAttachmentError("invalid_input");
		}
		if (bytes.byteLength > this.limits.maxStoredBytes) {
			throw new ImageAttachmentError("normalized_image_too_large");
		}
		const captured = Buffer.from(bytes);
		await this.assertRootStable(signal);
		try {
			const dimensions = await inspectCanonicalPng(
				captured,
				{
					kind: "publication",
					maxPixels: this.limits.maxStoredPixels,
					maxDimension: this.limits.maxStoredDimension,
				},
				signal,
			);
			const attachmentId = `img_${createHash("sha256").update(captured).digest("hex")}`;
			const reference = createImageReference(attachmentId, captured.byteLength, dimensions.width, dimensions.height);
			await this.publish(Object.freeze({ bytes: captured, reference }), signal);
			return reference;
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof ImageAttachmentError) throw error;
			throw new ImageAttachmentError("decode_failed");
		}
	}

	async read(reference: Readonly<ImageReferenceContent>, signal: AbortSignal): Promise<Uint8Array> {
		signal.throwIfAborted();
		if (!Value.Check(ImageReferenceContentSchema, reference) || reference.mediaType !== "image/png") {
			throw new ImageAttachmentError("invalid_input");
		}
		await this.assertRootStable(signal);
		const filePath = this.objectPath(reference.attachmentId);
		let canonicalPath: string;
		try {
			canonicalPath = resolve(await realpath(filePath));
			signal.throwIfAborted();
		} catch (error) {
			signal.throwIfAborted();
			if (isNodeError(error, "ENOENT")) throw new ImageAttachmentError("storage_missing");
			throw new ImageAttachmentError("storage_unavailable");
		}
		if (canonicalPath !== filePath) throw new ImageAttachmentError("storage_changed");
		let handle: FileHandle;
		try {
			handle = await open(filePath, fsConstants.O_RDONLY | (fsConstants.O_NOFOLLOW ?? 0));
		} catch (error) {
			signal.throwIfAborted();
			if (isNodeError(error, "ENOENT")) throw new ImageAttachmentError("storage_missing");
			throw new ImageAttachmentError("storage_unavailable");
		}
		try {
			signal.throwIfAborted();
			const before = await handle.stat();
			signal.throwIfAborted();
			if (!before.isFile() || before.size <= 0 || before.size > this.limits.maxReadBytes) {
				throw new ImageAttachmentError("storage_corrupt");
			}
			if (before.size !== reference.bytes) throw new ImageAttachmentError("storage_corrupt");
			const bytes = Buffer.allocUnsafe(before.size);
			let offset = 0;
			while (offset < bytes.byteLength) {
				signal.throwIfAborted();
				const result = await handle.read(bytes, offset, bytes.byteLength - offset, offset);
				if (result.bytesRead === 0) throw new ImageAttachmentError("storage_changed");
				offset += result.bytesRead;
			}
			const extra = Buffer.allocUnsafe(1);
			if ((await handle.read(extra, 0, 1, offset)).bytesRead !== 0) {
				throw new ImageAttachmentError("storage_changed");
			}
			const after = await handle.stat();
			const addressedAfter = await stat(filePath);
			signal.throwIfAborted();
			if (!sameFile(before, after) || !sameFile(after, addressedAfter)) {
				throw new ImageAttachmentError("storage_changed");
			}
			const attachmentId = `img_${createHash("sha256").update(bytes).digest("hex")}`;
			if (attachmentId !== reference.attachmentId) throw new ImageAttachmentError("storage_corrupt");
			await inspectCanonicalPng(
				bytes,
				{
					kind: "storage",
					reference,
					maxPixels: this.limits.maxStoredPixels,
					maxDimension: this.limits.maxStoredDimension,
				},
				signal,
			);
			signal.throwIfAborted();
			await this.assertRootStable(signal);
			return new Uint8Array(bytes);
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof ImageAttachmentError) throw error;
			throw new ImageAttachmentError("storage_unavailable");
		} finally {
			await handle.close().catch(() => undefined);
		}
	}

	private async normalize(
		input: Buffer,
		declaredMediaType: ImageMediaType,
		name: string | undefined,
		signal: AbortSignal,
	): Promise<PreparedObject> {
		try {
			const metadata = await readMetadata(
				input,
				{
					animated: true,
					failOn: "warning",
					limitInputPixels: this.limits.maxInputPixels,
				},
				signal,
			);
			signal.throwIfAborted();
			if (metadata.format !== formatByMediaType.get(declaredMediaType)) {
				throw new ImageAttachmentError("media_type_mismatch");
			}
			if ((metadata.pages ?? 1) !== 1) throw new ImageAttachmentError("animated_image_unsupported");
			const width = metadata.autoOrient.width;
			const height = metadata.autoOrient.height;
			if (
				!positiveDimension(width) ||
				!positiveDimension(height) ||
				width > this.limits.maxInputDimension ||
				height > this.limits.maxInputDimension ||
				!fitsPixels(width, height, this.limits.maxInputPixels)
			) {
				throw new ImageAttachmentError("input_too_large");
			}
			const target = deriveStoredDimensions(width, height, this.limits.maxStoredDimension, this.limits.maxStoredPixels);
			let pipeline = sharp(input, {
				animated: true,
				failOn: "warning",
				limitInputPixels: this.limits.maxInputPixels,
			})
				.autoOrient()
				.toColourspace("srgb");
			if (target.width !== width || target.height !== height) {
				pipeline = pipeline.resize(target.width, target.height, { fit: "fill" });
			}
			const result = await renderCanonicalPng(pipeline, this.limits.maxStoredBytes, signal);
			signal.throwIfAborted();
			if (
				result.byteLength === 0 ||
				result.byteLength > this.limits.maxStoredBytes ||
				!fitsPixels(target.width, target.height, this.limits.maxStoredPixels)
			) {
				throw new ImageAttachmentError("normalized_image_too_large");
			}
			const attachmentId = `img_${createHash("sha256").update(result).digest("hex")}`;
			const reference = createImageReference(attachmentId, result.byteLength, target.width, target.height, name);
			await inspectCanonicalPng(
				result,
				{
					kind: "storage",
					reference,
					maxPixels: this.limits.maxStoredPixels,
					maxDimension: this.limits.maxStoredDimension,
				},
				signal,
			);
			return Object.freeze({ bytes: result, reference });
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof ImageAttachmentError) throw error;
			throw new ImageAttachmentError("decode_failed");
		}
	}

	private async publish(object: PreparedObject, signal: AbortSignal): Promise<void> {
		await this.assertRootStable(signal);
		const filePath = this.objectPath(object.reference.attachmentId);
		const bucket = dirname(filePath);
		const firstBucket = dirname(bucket);
		try {
			await createPrivateDirectory(firstBucket, this.canonicalRoot);
			await createPrivateDirectory(bucket, this.canonicalRoot);
			signal.throwIfAborted();
		} catch (error) {
			if (error instanceof ImageAttachmentError) throw error;
			throw new ImageAttachmentError("storage_unavailable");
		}
		const temporaryPath = join(this.temporaryRoot, `${randomUUID()}.tmp`);
		let handle: FileHandle | undefined;
		try {
			handle = await open(temporaryPath, "wx", 0o600);
			let offset = 0;
			while (offset < object.bytes.byteLength) {
				signal.throwIfAborted();
				const written = await handle.write(object.bytes, offset, object.bytes.byteLength - offset, offset);
				if (written.bytesWritten === 0) throw new ImageAttachmentError("storage_unavailable");
				offset += written.bytesWritten;
			}
			await handle.sync();
			await handle.close();
			handle = undefined;
			signal.throwIfAborted();
			try {
				await link(temporaryPath, filePath);
				if (process.platform !== "win32") await chmod(filePath, 0o400);
				await syncDirectory(bucket);
			} catch (error) {
				if (!isNodeError(error, "EEXIST")) throw error;
				await this.read(object.reference, signal);
			}
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof ImageAttachmentError) throw error;
			throw new ImageAttachmentError("storage_unavailable");
		} finally {
			await handle?.close().catch(() => undefined);
			await unlink(temporaryPath).catch(() => undefined);
		}
	}

	private objectPath(attachmentId: string): string {
		const digest = attachmentId.slice(4);
		return join(this.objectsRoot, digest.slice(0, 2), digest.slice(2, 4), `${attachmentId}.png`);
	}

	private async assertRootStable(signal: AbortSignal): Promise<void> {
		signal.throwIfAborted();
		try {
			const current = resolve(await realpath(this.addressedRoot));
			signal.throwIfAborted();
			const [rootIdentity, objectsIdentity, temporaryIdentity] = await Promise.all([
				directoryIdentity(current),
				exactDirectoryIdentity(this.objectsRoot, this.canonicalRoot),
				exactDirectoryIdentity(this.temporaryRoot, this.canonicalRoot),
			]);
			signal.throwIfAborted();
			if (
				current !== this.canonicalRoot ||
				!sameIdentity(rootIdentity, this.rootIdentity) ||
				!sameIdentity(objectsIdentity, this.objectsIdentity) ||
				!sameIdentity(temporaryIdentity, this.temporaryIdentity)
			) {
				throw new ImageAttachmentError("storage_changed");
			}
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof ImageAttachmentError) throw error;
			throw new ImageAttachmentError("storage_changed");
		}
	}
}

async function inspectCanonicalPng(
	bytes: Buffer,
	inspection: CanonicalPngInspection,
	signal: AbortSignal,
): Promise<{ readonly width: number; readonly height: number }> {
	const storage = inspection.kind === "storage";
	const decodeFailure = storage ? "storage_corrupt" : "decode_failed";
	try {
		const metadata = await readMetadata(
			bytes,
			{
				animated: true,
				failOn: "warning",
				limitInputPixels: storage ? inspection.maxPixels : false,
			},
			signal,
		);
		if (metadata.format !== "png") {
			throw new ImageAttachmentError(storage ? "storage_corrupt" : "media_type_mismatch");
		}
		const structure = inspectCanonicalPngStructure(bytes);
		if ((metadata.pages ?? 1) !== 1 || structure === "animated") {
			throw new ImageAttachmentError(storage ? "storage_corrupt" : "animated_image_unsupported");
		}
		if (structure === "malformed") throw new ImageAttachmentError(decodeFailure);
		if (structure === "noncanonical") {
			throw new ImageAttachmentError(storage ? "storage_corrupt" : "invalid_input");
		}
		if (!positiveDimension(metadata.width) || !positiveDimension(metadata.height)) {
			throw new ImageAttachmentError(decodeFailure);
		}
		if (
			metadata.width > inspection.maxDimension ||
			metadata.height > inspection.maxDimension ||
			!fitsPixels(metadata.width, metadata.height, inspection.maxPixels)
		) {
			throw new ImageAttachmentError(storage ? "storage_corrupt" : "normalized_image_too_large");
		}
		if (
			(inspection.kind === "storage" &&
				(metadata.width !== inspection.reference.width || metadata.height !== inspection.reference.height)) ||
			(metadata.bitsPerSample !== 8 && metadata.bitsPerSample !== 16) ||
			metadata.orientation !== undefined ||
			metadata.density !== undefined ||
			metadata.hasProfile ||
			metadata.exif !== undefined ||
			metadata.icc !== undefined ||
			metadata.iptc !== undefined ||
			metadata.xmp !== undefined ||
			metadata.comments !== undefined
		) {
			throw new ImageAttachmentError(storage ? "storage_corrupt" : "invalid_input");
		}
		const expectedDecodedBytes = safeProduct(
			decodeFailure,
			metadata.width,
			metadata.height,
			metadata.channels,
			metadata.bitsPerSample / 8,
		);
		await fullyDecode(bytes, inspection.maxPixels, expectedDecodedBytes, decodeFailure, signal);
		return Object.freeze({ width: metadata.width, height: metadata.height });
	} catch (error) {
		signal.throwIfAborted();
		if (error instanceof ImageAttachmentError) throw error;
		throw new ImageAttachmentError(decodeFailure);
	}
}

async function readMetadata(bytes: Buffer, options: NonNullable<Parameters<typeof sharp>[1]>, signal: AbortSignal) {
	const pipeline = sharp(bytes, options);
	const release = bindSharpAbort(pipeline, signal);
	try {
		const metadata = await pipeline.metadata();
		signal.throwIfAborted();
		return metadata;
	} finally {
		release();
	}
}

async function fullyDecode(
	bytes: Buffer,
	maxPixels: number,
	expectedDecodedBytes: number,
	failureCode: "decode_failed" | "storage_corrupt",
	signal: AbortSignal,
): Promise<void> {
	const output = sharp(bytes, { failOn: "warning", limitInputPixels: maxPixels }).raw();
	const release = bindSharpAbort(output, signal);
	let total = 0;
	try {
		for await (const chunk of output) {
			signal.throwIfAborted();
			const bytesRead = chunk instanceof Uint8Array ? chunk.byteLength : Buffer.byteLength(chunk);
			if (bytesRead > expectedDecodedBytes - total) throw new ImageAttachmentError(failureCode);
			total += bytesRead;
		}
		signal.throwIfAborted();
		if (total !== expectedDecodedBytes) throw new ImageAttachmentError(failureCode);
	} finally {
		release();
	}
}

function safeProduct(errorCode: "decode_failed" | "storage_corrupt", ...values: readonly number[]): number {
	let product = 1;
	for (const value of values) {
		if (!Number.isSafeInteger(value) || value <= 0 || value > Math.floor(Number.MAX_SAFE_INTEGER / product)) {
			throw new ImageAttachmentError(errorCode);
		}
		product *= value;
	}
	return product;
}

function deriveStoredDimensions(
	width: number,
	height: number,
	maxDimension: number,
	maxPixels: number,
): { readonly width: number; readonly height: number } {
	const scale = Math.min(1, maxDimension / width, maxDimension / height, Math.sqrt(maxPixels / width / height));
	let targetWidth = Math.max(1, Math.round(width * scale));
	let targetHeight = Math.max(1, Math.round(height * scale));
	if (!fitsPixels(targetWidth, targetHeight, maxPixels)) {
		if (targetWidth >= targetHeight) {
			targetWidth = Math.max(1, Math.floor(maxPixels / targetHeight));
		} else {
			targetHeight = Math.max(1, Math.floor(maxPixels / targetWidth));
		}
	}
	return { width: targetWidth, height: targetHeight };
}

function positiveDimension(value: number): boolean {
	return Number.isSafeInteger(value) && value > 0;
}

function fitsPixels(width: number, height: number, maximum: number): boolean {
	return positiveDimension(width) && positiveDimension(height) && width <= Math.floor(maximum / height);
}

function inspectCanonicalPngStructure(bytes: Buffer): CanonicalPngStructure {
	try {
		const inspected = inspectPng(bytes, { maxBytes: Math.max(1, bytes.byteLength) });
		let sawImageData = false;
		let sawPhysicalPixels = false;
		let animated = false;
		let noncanonical = false;
		for (const chunk of inspected.chunks) {
			switch (chunk.type) {
				case "IHDR":
					break;
				case "IDAT":
					sawImageData = true;
					break;
				case "IEND":
					break;
				case "pHYs":
					if (
						sawPhysicalPixels ||
						sawImageData ||
						chunk.dataLength !== sharpCanonicalPhysicalPixels.byteLength ||
						!bytes.subarray(chunk.dataOffset, chunk.dataOffset + chunk.dataLength).equals(sharpCanonicalPhysicalPixels)
					) {
						noncanonical = true;
					}
					sawPhysicalPixels = true;
					break;
				case "acTL":
				case "fcTL":
				case "fdAT":
					animated = true;
					break;
				default:
					noncanonical = true;
			}
		}
		if (animated) return "animated";
		return noncanonical ? "noncanonical" : "canonical";
	} catch (error) {
		if (error instanceof PngInspectionError) {
			if (
				error.code === "trailing_data" ||
				error.code === "unknown_critical_chunk" ||
				error.code === "nonconsecutive_idat"
			) {
				return "noncanonical";
			}
			return "malformed";
		}
		throw error;
	}
}

async function assertExactDirectory(path: string, root: string): Promise<void> {
	await exactDirectoryIdentity(path, root);
}

async function exactDirectoryIdentity(path: string, root: string): Promise<FileIdentity> {
	const [canonical, addressed, followed] = await Promise.all([realpath(path), lstat(path), stat(path)]);
	if (
		!addressed.isDirectory() ||
		addressed.isSymbolicLink() ||
		!followed.isDirectory() ||
		canonical !== resolve(path) ||
		!withinRoot(canonical, root) ||
		!sameIdentity(addressed, followed)
	) {
		throw new ImageAttachmentError("storage_changed");
	}
	return { dev: followed.dev, ino: followed.ino };
}

async function createPrivateDirectory(path: string, root: string): Promise<void> {
	try {
		await mkdir(path, { mode: 0o700 });
	} catch (error) {
		if (!isNodeError(error, "EEXIST")) throw error;
	}
	await assertExactDirectory(path, root);
	if (process.platform !== "win32") await chmod(path, 0o700);
	await syncDirectory(dirname(path));
}

function withinRoot(candidate: string, root: string): boolean {
	return candidate.startsWith(`${root}${sep}`);
}

function sameFile(left: { dev: number | bigint; ino: number | bigint; size: number }, right: typeof left): boolean {
	return sameIdentity(left, right) && left.size === right.size;
}

function sameIdentity(left: FileIdentity, right: FileIdentity): boolean {
	return left.dev === right.dev && left.ino === right.ino;
}

async function directoryIdentity(path: string): Promise<FileIdentity> {
	const info = await stat(path);
	if (!info.isDirectory()) throw new ImageAttachmentError("storage_changed");
	return { dev: info.dev, ino: info.ino };
}

async function syncDirectory(path: string): Promise<void> {
	if (process.platform === "win32") return;
	const handle = await open(path, "r");
	try {
		await handle.sync();
	} finally {
		await handle.close();
	}
}

function isNodeError(error: unknown, code: string): boolean {
	return error instanceof Error && "code" in error && error.code === code;
}

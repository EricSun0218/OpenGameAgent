import { createHash, randomUUID } from "node:crypto";
import { lstat, mkdir, readFile, realpath, rename, rm, writeFile } from "node:fs/promises";
import { isAbsolute, join, resolve, sep } from "node:path";
import type {
	GameImageAttachment,
	GameImageAttachmentReference,
	GameImageAttachmentStore,
} from "@opengameagent/protocol";

export interface LocalGameImageAttachmentStoreOptions {
	directory: string;
	maximumBytes?: number;
	maximumPixels?: number;
}

const DEFAULT_MAXIMUM_BYTES = 16 * 1024 * 1024;
const DEFAULT_MAXIMUM_PIXELS = 33_554_432;
const IDENTIFIER = /^img_[a-f0-9]{64}$/u;
const SHA256 = /^[a-f0-9]{64}$/u;

const SUPPORTED_MIME_TYPES = new Set(["image/png", "image/jpeg", "image/gif", "image/webp"]);
const MAXIMUM_JPEG_SEGMENTS = 4_096;
const MAXIMUM_WEBP_CHUNKS = 1_024;

interface ImageDimensions {
	mimeType: string;
	width: number;
	height: number;
}

function requirePositiveInteger(value: number, maximum: number, name: string): number {
	if (!Number.isSafeInteger(value) || value < 1 || value > maximum) {
		throw new RangeError(`${name} must be a positive integer no greater than ${maximum}.`);
	}
	return value;
}

function normalizeMime(value: string): string {
	const mime = value.trim().toLowerCase();
	if (!SUPPORTED_MIME_TYPES.has(mime)) throw new TypeError("Unsupported image MIME type.");
	return mime;
}

function ascii(data: Uint8Array, offset: number, length: number): string {
	return String.fromCharCode(...data.subarray(offset, offset + length));
}

function uint16BigEndian(data: Uint8Array, offset: number): number {
	return ((data[offset] ?? 0) << 8) | (data[offset + 1] ?? 0);
}

function uint16LittleEndian(data: Uint8Array, offset: number): number {
	return (data[offset] ?? 0) | ((data[offset + 1] ?? 0) << 8);
}

function uint24LittleEndian(data: Uint8Array, offset: number): number {
	return (data[offset] ?? 0) | ((data[offset + 1] ?? 0) << 8) | ((data[offset + 2] ?? 0) << 16);
}

function uint32BigEndian(data: Uint8Array, offset: number): number {
	return (
		(data[offset] ?? 0) * 0x1000000 +
		((data[offset + 1] ?? 0) << 16) +
		((data[offset + 2] ?? 0) << 8) +
		(data[offset + 3] ?? 0)
	);
}

function uint32LittleEndian(data: Uint8Array, offset: number): number {
	return (
		(data[offset] ?? 0) +
		(data[offset + 1] ?? 0) * 0x100 +
		(data[offset + 2] ?? 0) * 0x10000 +
		(data[offset + 3] ?? 0) * 0x1000000
	);
}

function pngDimensions(data: Uint8Array): ImageDimensions | undefined {
	if (
		data.byteLength < 24 ||
		!Buffer.from(data.subarray(0, 8)).equals(Buffer.from("89504e470d0a1a0a", "hex")) ||
		uint32BigEndian(data, 8) !== 13 ||
		ascii(data, 12, 4) !== "IHDR"
	) {
		return undefined;
	}
	return { mimeType: "image/png", width: uint32BigEndian(data, 16), height: uint32BigEndian(data, 20) };
}

function gifDimensions(data: Uint8Array): ImageDimensions | undefined {
	if (data.byteLength < 10) return undefined;
	const signature = ascii(data, 0, 6);
	if (signature !== "GIF87a" && signature !== "GIF89a") return undefined;
	return { mimeType: "image/gif", width: uint16LittleEndian(data, 6), height: uint16LittleEndian(data, 8) };
}

const JPEG_START_OF_FRAME = new Set([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf]);

function jpegDimensions(data: Uint8Array): ImageDimensions | undefined {
	if (data.byteLength < 4 || data[0] !== 0xff || data[1] !== 0xd8) return undefined;
	let offset = 2;
	for (let segments = 0; segments < MAXIMUM_JPEG_SEGMENTS && offset < data.byteLength; segments += 1) {
		while (offset < data.byteLength && data[offset] === 0xff) offset += 1;
		const marker = data[offset];
		offset += 1;
		if (marker === undefined || marker === 0xd9 || marker === 0xda) return undefined;
		if (marker === 0x00 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
		if (offset + 2 > data.byteLength) return undefined;
		const length = uint16BigEndian(data, offset);
		if (length < 2 || offset + length > data.byteLength) return undefined;
		if (JPEG_START_OF_FRAME.has(marker)) {
			if (length < 7) return undefined;
			return {
				mimeType: "image/jpeg",
				width: uint16BigEndian(data, offset + 5),
				height: uint16BigEndian(data, offset + 3),
			};
		}
		offset += length;
	}
	return undefined;
}

function webpDimensions(data: Uint8Array): ImageDimensions | undefined {
	if (data.byteLength < 20 || ascii(data, 0, 4) !== "RIFF" || ascii(data, 8, 4) !== "WEBP") return undefined;
	const declaredLength = uint32LittleEndian(data, 4) + 8;
	if (declaredLength < 20 || declaredLength > data.byteLength) return undefined;
	let offset = 12;
	for (let chunks = 0; chunks < MAXIMUM_WEBP_CHUNKS && offset + 8 <= declaredLength; chunks += 1) {
		const type = ascii(data, offset, 4);
		const length = uint32LittleEndian(data, offset + 4);
		const payload = offset + 8;
		const next = payload + length + (length & 1);
		if (next <= offset || next > declaredLength) return undefined;
		if (type === "VP8X" && length >= 10) {
			return {
				mimeType: "image/webp",
				width: uint24LittleEndian(data, payload + 4) + 1,
				height: uint24LittleEndian(data, payload + 7) + 1,
			};
		}
		if (
			type === "VP8 " &&
			length >= 10 &&
			data[payload + 3] === 0x9d &&
			data[payload + 4] === 0x01 &&
			data[payload + 5] === 0x2a
		) {
			return {
				mimeType: "image/webp",
				width: uint16LittleEndian(data, payload + 6) & 0x3fff,
				height: uint16LittleEndian(data, payload + 8) & 0x3fff,
			};
		}
		if (type === "VP8L" && length >= 5 && data[payload] === 0x2f) {
			const b1 = data[payload + 1] ?? 0;
			const b2 = data[payload + 2] ?? 0;
			const b3 = data[payload + 3] ?? 0;
			const b4 = data[payload + 4] ?? 0;
			return {
				mimeType: "image/webp",
				width: 1 + (b1 | ((b2 & 0x3f) << 8)),
				height: 1 + ((b2 >> 6) | (b3 << 2) | ((b4 & 0x0f) << 10)),
			};
		}
		offset = next;
	}
	return undefined;
}

function detectImageDimensions(data: Uint8Array): ImageDimensions {
	const dimensions = pngDimensions(data) ?? jpegDimensions(data) ?? gifDimensions(data) ?? webpDimensions(data);
	if (!dimensions) throw new TypeError("Image attachment bytes are not a supported bounded image.");
	return dimensions;
}

function attachmentDirectory(directory: string, id: string): string {
	return join(directory, id);
}

function metadataPath(directory: string, id: string): string {
	return join(attachmentDirectory(directory, id), "metadata.json");
}

function contentPath(directory: string, id: string): string {
	return join(attachmentDirectory(directory, id), "content.bin");
}

function assertContained(directory: string, path: string): void {
	const prefix = directory.endsWith(sep) ? directory : `${directory}${sep}`;
	if (!path.startsWith(prefix)) throw new Error("Attachment path escaped its configured directory.");
}

async function assertNoReparse(path: string, allowMissing: boolean): Promise<void> {
	try {
		const stats = await lstat(path);
		if (stats.isSymbolicLink()) throw new Error("Attachment storage cannot use symbolic links or reparse points.");
	} catch (error) {
		if (allowMissing && typeof error === "object" && error !== null && "code" in error && error.code === "ENOENT")
			return;
		throw error;
	}
}

async function assertResolvedInside(root: string, path: string): Promise<void> {
	const [resolvedRoot, resolvedPath] = await Promise.all([realpath(root), realpath(path)]);
	const prefix = resolvedRoot.endsWith(sep) ? resolvedRoot : `${resolvedRoot}${sep}`;
	if (!resolvedPath.startsWith(prefix)) throw new Error("Attachment path escaped through a reparse point.");
}

function parseReference(value: unknown): GameImageAttachmentReference {
	if (value === null || typeof value !== "object" || Array.isArray(value))
		throw new Error("Invalid attachment metadata.");
	const candidate = value as Partial<GameImageAttachmentReference>;
	if (
		typeof candidate.id !== "string" ||
		!IDENTIFIER.test(candidate.id) ||
		typeof candidate.sha256 !== "string" ||
		!SHA256.test(candidate.sha256) ||
		candidate.id !== `img_${candidate.sha256}` ||
		typeof candidate.mimeType !== "string" ||
		typeof candidate.bytes !== "number" ||
		typeof candidate.width !== "number" ||
		typeof candidate.height !== "number"
	) {
		throw new Error("Invalid attachment metadata.");
	}
	return {
		id: candidate.id,
		sha256: candidate.sha256,
		mimeType: normalizeMime(candidate.mimeType),
		bytes: requirePositiveInteger(candidate.bytes, Number.MAX_SAFE_INTEGER, "attachment bytes"),
		width: requirePositiveInteger(candidate.width, 1_000_000, "attachment width"),
		height: requirePositiveInteger(candidate.height, 1_000_000, "attachment height"),
	};
}

export class LocalGameImageAttachmentStore implements GameImageAttachmentStore {
	private readonly directory: string;
	private readonly maximumBytes: number;
	private readonly maximumPixels: number;

	constructor(options: LocalGameImageAttachmentStoreOptions) {
		if (!isAbsolute(options.directory)) throw new TypeError("Attachment directory must be absolute.");
		this.directory = resolve(options.directory);
		this.maximumBytes = requirePositiveInteger(
			options.maximumBytes ?? DEFAULT_MAXIMUM_BYTES,
			1024 * 1024 * 1024,
			"maximumBytes",
		);
		this.maximumPixels = requirePositiveInteger(
			options.maximumPixels ?? DEFAULT_MAXIMUM_PIXELS,
			1_000_000_000,
			"maximumPixels",
		);
	}

	async admit(mimeType: string, data: Uint8Array, signal?: AbortSignal): Promise<GameImageAttachmentReference> {
		signal?.throwIfAborted();
		const claimedMime = normalizeMime(mimeType);
		if (data.byteLength < 1 || data.byteLength > this.maximumBytes) {
			throw new RangeError("Image attachment byte length is outside its configured bound.");
		}
		const dimensions = detectImageDimensions(data);
		if (dimensions.mimeType !== claimedMime) {
			throw new TypeError("Image attachment MIME type does not match its bytes.");
		}
		const width = requirePositiveInteger(dimensions.width, 1_000_000, "image width");
		const height = requirePositiveInteger(dimensions.height, 1_000_000, "image height");
		if (width * height > this.maximumPixels) throw new RangeError("Image attachment exceeds the pixel limit.");
		const bytes = new Uint8Array(data);
		const sha256 = createHash("sha256").update(bytes).digest("hex");
		const reference: GameImageAttachmentReference = {
			id: `img_${sha256}`,
			sha256,
			mimeType: claimedMime,
			bytes: bytes.byteLength,
			width,
			height,
		};
		await this.ensureRoot();
		const target = attachmentDirectory(this.directory, reference.id);
		const metadata = metadataPath(this.directory, reference.id);
		const content = contentPath(this.directory, reference.id);
		assertContained(this.directory, target);
		assertContained(this.directory, metadata);
		assertContained(this.directory, content);
		await assertNoReparse(target, true);
		await assertNoReparse(metadata, true);
		await assertNoReparse(content, true);
		const existing = await this.read(reference.id, signal);
		if (existing) {
			if (JSON.stringify(existing.reference) !== JSON.stringify(reference)) {
				throw new Error("Content-addressed attachment metadata conflicts with the admitted image.");
			}
			return existing.reference;
		}

		const temporaryDirectory = join(this.directory, `.tmp-${randomUUID()}`);
		assertContained(this.directory, temporaryDirectory);
		try {
			await mkdir(temporaryDirectory);
			await writeFile(join(temporaryDirectory, "content.bin"), bytes, { flag: "wx" });
			await writeFile(join(temporaryDirectory, "metadata.json"), JSON.stringify(reference), {
				flag: "wx",
				encoding: "utf8",
			});
			signal?.throwIfAborted();
			try {
				await rename(temporaryDirectory, target);
			} catch (error) {
				if (
					typeof error !== "object" ||
					error === null ||
					!("code" in error) ||
					(error.code !== "EEXIST" && error.code !== "ENOTEMPTY" && error.code !== "EPERM")
				) {
					throw error;
				}
				const raced = await this.read(reference.id, signal);
				if (!raced || JSON.stringify(raced.reference) !== JSON.stringify(reference)) {
					throw new Error("Concurrent content-addressed attachment admission did not settle consistently.");
				}
			}
		} finally {
			await rm(temporaryDirectory, { recursive: true, force: true }).catch(() => undefined);
		}
		return reference;
	}

	async read(id: string, signal?: AbortSignal): Promise<GameImageAttachment | undefined> {
		signal?.throwIfAborted();
		if (!IDENTIFIER.test(id)) throw new TypeError("Invalid image attachment identifier.");
		await this.ensureRoot();
		const target = attachmentDirectory(this.directory, id);
		const metadata = metadataPath(this.directory, id);
		const content = contentPath(this.directory, id);
		assertContained(this.directory, target);
		assertContained(this.directory, metadata);
		assertContained(this.directory, content);
		try {
			await assertNoReparse(target, false);
		} catch (error) {
			if (typeof error === "object" && error !== null && "code" in error && error.code === "ENOENT") return undefined;
			throw error;
		}
		await assertResolvedInside(this.directory, target);
		try {
			await Promise.all([assertNoReparse(metadata, false), assertNoReparse(content, false)]);
		} catch (error) {
			if (typeof error === "object" && error !== null && "code" in error && error.code === "ENOENT") {
				throw new Error("Image attachment state is incomplete.");
			}
			throw error;
		}
		let metadataBytes: Buffer;
		let contentBytes: Buffer;
		try {
			[metadataBytes, contentBytes] = await Promise.all([readFile(metadata), readFile(content)]);
		} catch (error) {
			if (typeof error === "object" && error !== null && "code" in error && error.code === "ENOENT")
				throw new Error("Image attachment state is incomplete.");
			throw error;
		}
		signal?.throwIfAborted();
		if (metadataBytes.byteLength > 4096) throw new Error("Image attachment metadata is oversized.");
		const reference = parseReference(JSON.parse(metadataBytes.toString("utf8")) as unknown);
		if (reference.id !== id || reference.bytes !== contentBytes.byteLength || reference.bytes > this.maximumBytes) {
			throw new Error("Image attachment metadata does not match its content.");
		}
		const sha256 = createHash("sha256").update(contentBytes).digest("hex");
		if (sha256 !== reference.sha256) throw new Error("Image attachment content failed integrity validation.");
		const dimensions = detectImageDimensions(contentBytes);
		if (
			dimensions.mimeType !== reference.mimeType ||
			dimensions.width !== reference.width ||
			dimensions.height !== reference.height ||
			dimensions.width * dimensions.height > this.maximumPixels
		) {
			throw new Error("Image attachment metadata does not match its decoded dimensions.");
		}
		return { reference, data: new Uint8Array(contentBytes) };
	}

	private async ensureRoot(): Promise<void> {
		await mkdir(this.directory, { recursive: true });
		await assertNoReparse(this.directory, false);
	}
}

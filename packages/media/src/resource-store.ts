import { createHash, randomUUID, timingSafeEqual } from "node:crypto";
import { lstat, mkdir, open, readFile, rename, rm } from "node:fs/promises";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import type { GameMediaBinary, GameMediaKind, GameMediaResource, GameMediaResourceStore } from "./media.js";

export interface FileGameMediaResourceStoreOptions {
	maximumResourceBytes?: number;
	maximumNameCharacters?: number;
}

const mimeKinds: Readonly<Record<string, GameMediaKind>> = {
	"image/png": "image",
	"image/jpeg": "image",
	"image/gif": "image",
	"image/webp": "image",
	"audio/wav": "audio",
	"audio/mpeg": "audio",
	"audio/ogg": "audio",
	"audio/flac": "audio",
	"video/mp4": "video",
	"video/webm": "video",
};

function matchesMagic(mimeType: string, bytes: Uint8Array): boolean {
	const text = (offset: number, length: number) =>
		Buffer.from(bytes.subarray(offset, offset + length)).toString("ascii");
	switch (mimeType) {
		case "image/png":
			return bytes.length >= 8 && Buffer.from(bytes.subarray(0, 8)).equals(Buffer.from("89504e470d0a1a0a", "hex"));
		case "image/jpeg":
			return (
				bytes.length >= 4 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes.at(-2) === 0xff && bytes.at(-1) === 0xd9
			);
		case "image/gif":
			return bytes.length >= 10 && (text(0, 6) === "GIF87a" || text(0, 6) === "GIF89a");
		case "image/webp":
			return bytes.length >= 12 && text(0, 4) === "RIFF" && text(8, 4) === "WEBP";
		case "audio/wav":
			return bytes.length >= 12 && text(0, 4) === "RIFF" && text(8, 4) === "WAVE";
		case "audio/mpeg":
			return bytes.length >= 3 && (text(0, 3) === "ID3" || (bytes[0] === 0xff && ((bytes[1] ?? 0) & 0xe0) === 0xe0));
		case "audio/ogg":
			return bytes.length >= 4 && text(0, 4) === "OggS";
		case "audio/flac":
			return bytes.length >= 4 && text(0, 4) === "fLaC";
		case "video/mp4":
			return bytes.length >= 12 && text(4, 4) === "ftyp";
		case "video/webm":
			return bytes.length >= 4 && Buffer.from(bytes.subarray(0, 4)).equals(Buffer.from("1a45dfa3", "hex"));
		default:
			return false;
	}
}

function digest(bytes: Uint8Array): string {
	return createHash("sha256").update(bytes).digest("hex");
}

function safeEqual(left: string, right: string): boolean {
	const a = Buffer.from(left, "ascii");
	const b = Buffer.from(right, "ascii");
	return a.length === b.length && timingSafeEqual(a, b);
}

async function ensureNoSymbolicLink(path: string, root: string): Promise<void> {
	let cursor = path;
	while (cursor.length >= root.length) {
		try {
			const metadata = await lstat(cursor, { bigint: false });
			if (metadata.isSymbolicLink()) throw new Error("Media resource storage contains a symbolic link.");
			if (!metadata.isDirectory()) throw new Error("Media resource storage contains a non-directory path component.");
		} catch (error) {
			if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
		}
		if (cursor === root) break;
		cursor = dirname(cursor);
	}
}

export class FileGameMediaResourceStore implements GameMediaResourceStore {
	private readonly root: string;
	private readonly maximumResourceBytes: number;
	private readonly maximumNameCharacters: number;

	constructor(rootDirectory: string, options: FileGameMediaResourceStoreOptions = {}) {
		if (!rootDirectory) throw new TypeError("A media resource directory is required.");
		this.root = resolve(rootDirectory);
		this.maximumResourceBytes = options.maximumResourceBytes ?? 256 * 1024 * 1024;
		this.maximumNameCharacters = options.maximumNameCharacters ?? 255;
		if (!Number.isInteger(this.maximumResourceBytes) || this.maximumResourceBytes < 1) {
			throw new RangeError("maximumResourceBytes must be positive.");
		}
	}

	async save(binary: GameMediaBinary, signal?: AbortSignal): Promise<GameMediaResource> {
		signal?.throwIfAborted();
		this.validateBinary(binary);
		const bytes = Uint8Array.from(binary.data);
		const sha256 = digest(bytes);
		const directory = this.resolveInside("objects", sha256.slice(0, 2));
		const temporaryDirectory = this.resolveInside("tmp");
		await ensureNoSymbolicLink(dirname(directory), this.root);
		await mkdir(directory, { recursive: true });
		await mkdir(temporaryDirectory, { recursive: true });
		await ensureNoSymbolicLink(directory, this.root);
		await ensureNoSymbolicLink(temporaryDirectory, this.root);
		const target = join(directory, sha256);
		const temporary = join(temporaryDirectory, `${sha256}.${process.pid}.${randomUUID()}`);
		try {
			const file = await open(temporary, "wx", 0o600);
			try {
				await file.writeFile(bytes);
				await file.sync();
			} finally {
				await file.close();
			}
			signal?.throwIfAborted();
			try {
				await rename(temporary, target);
			} catch (error) {
				if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error;
				const existing = await readFile(target);
				if (!safeEqual(digest(existing), sha256)) throw new Error("Media resource hash collision detected.");
			}
		} finally {
			await rm(temporary, { force: true });
		}
		return {
			id: `sha256:${sha256}`,
			sha256,
			kind: binary.kind,
			mimeType: binary.mimeType,
			bytes: bytes.byteLength,
			...(binary.name === undefined ? {} : { name: binary.name }),
		};
	}

	async read(resource: GameMediaResource, signal?: AbortSignal): Promise<GameMediaBinary> {
		signal?.throwIfAborted();
		if (!/^sha256:[0-9a-f]{64}$/u.test(resource.id) || resource.id !== `sha256:${resource.sha256}`) {
			throw new TypeError("Media resource reference is invalid.");
		}
		if (resource.bytes < 1 || resource.bytes > this.maximumResourceBytes)
			throw new RangeError("Media resource size is invalid.");
		const path = this.resolveInside("objects", resource.sha256.slice(0, 2), resource.sha256);
		await ensureNoSymbolicLink(dirname(path), this.root);
		const bytes = await readFile(path, { signal });
		if (bytes.byteLength !== resource.bytes || !safeEqual(digest(bytes), resource.sha256)) {
			throw new Error("Media resource failed integrity verification.");
		}
		const binary: GameMediaBinary = {
			kind: resource.kind,
			mimeType: resource.mimeType,
			data: bytes,
			...(resource.name === undefined ? {} : { name: resource.name }),
		};
		this.validateBinary(binary);
		return binary;
	}

	private validateBinary(binary: GameMediaBinary): void {
		if (
			!(binary.data instanceof Uint8Array) ||
			binary.data.byteLength < 1 ||
			binary.data.byteLength > this.maximumResourceBytes
		) {
			throw new RangeError("Media resource exceeds its byte limit.");
		}
		if (mimeKinds[binary.mimeType] !== binary.kind || !matchesMagic(binary.mimeType, binary.data)) {
			throw new TypeError("Media resource MIME type does not match its content.");
		}
		if (
			binary.name !== undefined &&
			(binary.name.length > this.maximumNameCharacters ||
				[...binary.name].some((character) => (character.codePointAt(0) ?? 0) < 32 || character.codePointAt(0) === 127))
		) {
			throw new TypeError("Media resource name is invalid.");
		}
	}

	private resolveInside(...parts: string[]): string {
		const path = resolve(this.root, ...parts);
		const offset = relative(this.root, path);
		if (offset.startsWith("..") || isAbsolute(offset)) throw new Error("Media resource path escaped its root.");
		return path;
	}
}

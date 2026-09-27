import type { FileHandle } from "node:fs/promises";
import { open, realpath, stat } from "node:fs/promises";
import { resolve } from "node:path";

const utf8Decoder = new TextDecoder("utf-8", { fatal: true });

export type BoundedFileReadErrorCode = "unavailable" | "not_file" | "limit_exceeded" | "not_utf8" | "changed";

export class BoundedFileReadError extends Error {
	constructor(public readonly code: BoundedFileReadErrorCode) {
		super(code);
		this.name = "BoundedFileReadError";
	}
}

export interface StableTextFile {
	readonly canonicalPath: string;
	readonly bytes: number;
	readonly text: string;
}

export interface StableFile {
	readonly canonicalPath: string;
	readonly bytes: Uint8Array;
}

/** Read immutable bytes through one handle, under one host bound, and reject observable replacement or mutation. */
export async function readStableFile(
	addressedPath: string,
	maxBytes: number,
	signal: AbortSignal,
): Promise<StableFile> {
	if (!Number.isSafeInteger(maxBytes) || maxBytes <= 0)
		throw new TypeError("maxBytes must be a positive safe integer.");
	signal.throwIfAborted();
	let canonicalBefore: string;
	try {
		canonicalBefore = resolve(await realpath(addressedPath));
		signal.throwIfAborted();
	} catch {
		signal.throwIfAborted();
		throw new BoundedFileReadError("unavailable");
	}

	let handle: FileHandle;
	try {
		handle = await open(canonicalBefore, "r");
	} catch {
		signal.throwIfAborted();
		throw new BoundedFileReadError("unavailable");
	}
	try {
		signal.throwIfAborted();
		const before = await handle.stat({ bigint: true });
		signal.throwIfAborted();
		if (!before.isFile()) throw new BoundedFileReadError("not_file");
		if (before.size > BigInt(maxBytes)) throw new BoundedFileReadError("limit_exceeded");

		const chunks: Uint8Array[] = [];
		let total = 0;
		const stream = handle.createReadStream({ autoClose: false, end: maxBytes, signal });
		try {
			for await (const chunk of stream) {
				signal.throwIfAborted();
				const bytes = chunk instanceof Uint8Array ? chunk : new Uint8Array(chunk);
				total += bytes.byteLength;
				if (!Number.isSafeInteger(total) || total > maxBytes) {
					stream.destroy();
					throw new BoundedFileReadError("limit_exceeded");
				}
				chunks.push(bytes);
			}
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof BoundedFileReadError) throw error;
			throw new BoundedFileReadError("unavailable");
		}

		const after = await handle.stat({ bigint: true });
		signal.throwIfAborted();
		if (
			before.dev !== after.dev ||
			before.ino !== after.ino ||
			before.size !== after.size ||
			before.mtimeNs !== after.mtimeNs ||
			before.ctimeNs !== after.ctimeNs ||
			BigInt(total) !== after.size
		) {
			throw new BoundedFileReadError("changed");
		}

		const bytes = new Uint8Array(total);
		let offset = 0;
		for (const chunk of chunks) {
			bytes.set(chunk, offset);
			offset += chunk.byteLength;
		}

		let canonicalAfter: string;
		let addressedAfter: Awaited<ReturnType<typeof stat>>;
		try {
			canonicalAfter = resolve(await realpath(addressedPath));
			addressedAfter = await stat(addressedPath, { bigint: true });
			signal.throwIfAborted();
		} catch {
			signal.throwIfAborted();
			throw new BoundedFileReadError("changed");
		}
		if (
			canonicalAfter !== canonicalBefore ||
			addressedAfter.dev !== after.dev ||
			addressedAfter.ino !== after.ino ||
			addressedAfter.size !== after.size ||
			addressedAfter.mtimeNs !== after.mtimeNs ||
			addressedAfter.ctimeNs !== after.ctimeNs
		) {
			throw new BoundedFileReadError("changed");
		}
		return { canonicalPath: canonicalBefore, bytes };
	} finally {
		await handle.close().catch(() => undefined);
	}
}

/** Read through one file handle, stop at the host bound, then reject path replacement. */
export async function readStableTextFile(
	addressedPath: string,
	maxBytes: number,
	signal: AbortSignal,
): Promise<StableTextFile> {
	const file = await readStableFile(addressedPath, maxBytes, signal);
	let text: string;
	try {
		text = utf8Decoder.decode(file.bytes);
	} catch {
		throw new BoundedFileReadError("not_utf8");
	}
	return { canonicalPath: file.canonicalPath, bytes: file.bytes.byteLength, text };
}

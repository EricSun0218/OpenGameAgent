import { constants as bufferConstants, Buffer } from "node:buffer";
import { crc32 } from "node:zlib";
import type { Sharp } from "sharp";
import { ImageAttachmentError } from "./errors.js";

export const sharpCanonicalPhysicalPixels = Buffer.from("000003e8000003e801", "hex");

export async function renderCanonicalPng(
	pipeline: Sharp,
	maxOutputBytes: number,
	signal: AbortSignal,
): Promise<Buffer> {
	signal.throwIfAborted();
	if (!Number.isSafeInteger(maxOutputBytes) || maxOutputBytes <= 0 || maxOutputBytes > bufferConstants.MAX_LENGTH) {
		throw new ImageAttachmentError("invalid_configuration");
	}
	const output = pipeline.png();
	const release = bindSharpAbort(output, signal);
	const chunks: Buffer[] = [];
	let total = 0;
	try {
		for await (const chunk of output) {
			signal.throwIfAborted();
			const bytes = Buffer.from(chunk);
			if (bytes.byteLength > maxOutputBytes - total) {
				output.destroy();
				throw new ImageAttachmentError("normalized_image_too_large");
			}
			total += bytes.byteLength;
			chunks.push(bytes);
		}
		signal.throwIfAborted();
		return normalizeSharpPngPhysicalPixels(Buffer.concat(chunks, total));
	} finally {
		release();
	}
}

export function bindSharpAbort(pipeline: Sharp, signal: AbortSignal): () => void {
	signal.throwIfAborted();
	const abort = () => {
		pipeline.destroy(signal.reason instanceof Error ? signal.reason : new Error("aborted"));
	};
	signal.addEventListener("abort", abort, { once: true });
	return () => signal.removeEventListener("abort", abort);
}

function normalizeSharpPngPhysicalPixels(bytes: Buffer): Buffer {
	let offset = 8;
	while (offset <= bytes.byteLength - 12) {
		const dataLength = bytes.readUInt32BE(offset);
		const nextOffset = offset + 12 + dataLength;
		if (nextOffset > bytes.byteLength) return bytes;
		if (bytes.toString("ascii", offset + 4, offset + 8) === "pHYs" && dataLength === 9) {
			sharpCanonicalPhysicalPixels.copy(bytes, offset + 8);
			bytes.writeUInt32BE(crc32(bytes.subarray(offset + 4, offset + 8 + dataLength)) >>> 0, offset + 8 + dataLength);
		}
		offset = nextOffset;
	}
	return bytes;
}

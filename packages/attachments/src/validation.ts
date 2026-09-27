import { constants as bufferConstants, Buffer } from "node:buffer";
import { ImageMediaTypeSchema, type ImageMediaType, type ImageReferenceContent } from "@opengameagent/protocol";
import { Value } from "typebox/value";
import { ImageAttachmentError } from "./errors.js";
import type { ImageAttachmentInput, ImageAttachmentLimits } from "./types.js";

const encoder = new TextEncoder();
const canonicalBase64 = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u;

export interface PreparedImageAttachmentInput {
	readonly bytes: Buffer;
	readonly mediaType: ImageMediaType;
	readonly name?: string;
}

export function prepareImageAttachmentLimits(limits: ImageAttachmentLimits): Readonly<ImageAttachmentLimits> {
	if (!isRecord(limits)) throw new ImageAttachmentError("invalid_configuration");
	const prepared = {
		maxInputBytes: positiveBufferLength(limits.maxInputBytes),
		maxInputPixels: positiveSafeInteger(limits.maxInputPixels),
		maxInputDimension: positiveSafeInteger(limits.maxInputDimension),
		maxStoredBytes: positiveBufferLength(limits.maxStoredBytes),
		maxStoredPixels: positiveSafeInteger(limits.maxStoredPixels),
		maxStoredDimension: positiveSafeInteger(limits.maxStoredDimension),
		maxReadBytes: positiveBufferLength(limits.maxReadBytes),
		maxNameBytes: positiveSafeInteger(limits.maxNameBytes),
		maxImagesPerBatch: positiveSafeInteger(limits.maxImagesPerBatch),
		maxBatchInputBytes: positiveBufferLength(limits.maxBatchInputBytes),
		maxBatchStoredBytes: positiveBufferLength(limits.maxBatchStoredBytes),
	} satisfies ImageAttachmentLimits;
	if (
		prepared.maxStoredBytes > prepared.maxReadBytes ||
		prepared.maxInputBytes > prepared.maxBatchInputBytes ||
		prepared.maxStoredBytes > prepared.maxBatchStoredBytes ||
		canonicalBase64Length(prepared.maxInputBytes) > bufferConstants.MAX_STRING_LENGTH
	) {
		throw new ImageAttachmentError("invalid_configuration");
	}
	return Object.freeze(prepared);
}

export function prepareImageAttachmentInput(
	input: Readonly<ImageAttachmentInput>,
	limits: Readonly<ImageAttachmentLimits>,
): PreparedImageAttachmentInput {
	if (!isRecord(input) || !Value.Check(ImageMediaTypeSchema, input.mediaType)) {
		throw new ImageAttachmentError("invalid_input");
	}
	const expectedKeys = Object.hasOwn(input, "name")
		? ["encoding", "data", "mediaType", "name"]
		: ["encoding", "data", "mediaType"];
	if (!hasExactKeys(input, expectedKeys)) throw new ImageAttachmentError("invalid_input");
	const name = prepareName(input.name, limits.maxNameBytes);
	let bytes: Buffer;
	if (input.encoding === "bytes" && input.data instanceof Uint8Array) {
		if (input.data.byteLength === 0 || input.data.byteLength > limits.maxInputBytes) {
			throw new ImageAttachmentError("input_too_large");
		}
		bytes = Buffer.from(input.data);
	} else if (input.encoding === "base64" && typeof input.data === "string") {
		bytes = decodeCanonicalBase64(input.data, limits.maxInputBytes);
	} else {
		throw new ImageAttachmentError("invalid_input");
	}
	return Object.freeze({ bytes, mediaType: input.mediaType, ...(name ? { name } : {}) });
}

export function createImageReference(
	attachmentId: string,
	bytes: number,
	width: number,
	height: number,
	name?: string,
): ImageReferenceContent {
	return Object.freeze({
		type: "image-reference" as const,
		attachmentId,
		mediaType: "image/png" as const,
		bytes,
		width,
		height,
		...(name ? { name } : {}),
	});
}

export function checkedAdd(current: number, next: number, maximum: number): number {
	if (!Number.isSafeInteger(current) || !Number.isSafeInteger(next) || next < 0 || next > maximum - current) {
		throw new ImageAttachmentError("input_too_large");
	}
	return current + next;
}

function decodeCanonicalBase64(data: string, maximumBytes: number): Buffer {
	const maximumCharacters = canonicalBase64Length(maximumBytes);
	if (data.length === 0 || data.length > maximumCharacters || data.length % 4 !== 0 || !canonicalBase64.test(data)) {
		throw new ImageAttachmentError("invalid_input");
	}
	const padding = data.endsWith("==") ? 2 : data.endsWith("=") ? 1 : 0;
	const decodedLength = (data.length / 4) * 3 - padding;
	if (!Number.isSafeInteger(decodedLength) || decodedLength <= 0 || decodedLength > maximumBytes) {
		throw new ImageAttachmentError("input_too_large");
	}
	const bytes = Buffer.from(data, "base64");
	if (bytes.byteLength !== decodedLength || bytes.toString("base64") !== data) {
		throw new ImageAttachmentError("invalid_input");
	}
	return bytes;
}

function prepareName(value: unknown, maximumBytes: number): string | undefined {
	if (value === undefined) return undefined;
	if (
		typeof value !== "string" ||
		value.length === 0 ||
		hasControlCharacter(value) ||
		encoder.encode(value).byteLength > maximumBytes
	) {
		throw new ImageAttachmentError("invalid_input");
	}
	return value;
}

function hasControlCharacter(value: string): boolean {
	for (let index = 0; index < value.length; index += 1) {
		const code = value.charCodeAt(index);
		if (code <= 0x1f || (code >= 0x7f && code <= 0x9f)) return true;
	}
	return false;
}

function canonicalBase64Length(bytes: number): number {
	const groups = Math.ceil(bytes / 3);
	if (groups > Math.floor(Number.MAX_SAFE_INTEGER / 4)) {
		throw new ImageAttachmentError("invalid_configuration");
	}
	return groups * 4;
}

function positiveSafeInteger(value: number): number {
	if (!Number.isSafeInteger(value) || value <= 0) throw new ImageAttachmentError("invalid_configuration");
	return value;
}

function positiveBufferLength(value: number): number {
	const checked = positiveSafeInteger(value);
	if (checked > bufferConstants.MAX_LENGTH) throw new ImageAttachmentError("invalid_configuration");
	return checked;
}

function isRecord(value: unknown): value is Record<PropertyKey, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasExactKeys(value: Record<PropertyKey, unknown>, expected: readonly string[]): boolean {
	const keys = Object.keys(value);
	return keys.length === expected.length && expected.every((key) => Object.hasOwn(value, key));
}

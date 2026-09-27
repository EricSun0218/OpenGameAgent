import { createHash } from "node:crypto";
import type { ImageContent } from "@earendil-works/pi-ai";
import { ImageReferenceContentSchema, type ImageMediaType, type ImageReferenceContent } from "@opengameagent/protocol";
import { Value } from "typebox/value";
import { decodeCanonicalBase64 } from "./canonical-base64.js";

export class AlignedImageProjectionError extends TypeError {
	constructor(public readonly code: "invalid_projection" | "image_integrity_mismatch") {
		super(code);
		this.name = "AlignedImageProjectionError";
	}
}

export interface PreparedAlignedImage {
	readonly content: Readonly<ImageContent>;
	readonly reference: Readonly<ImageReferenceContent>;
	readonly bytes: number;
}

export function captureAlignedImageProjection(
	contentValue: unknown,
	referenceValue: unknown,
	maxImageBytes: number,
): PreparedAlignedImage {
	if (!isRecord(contentValue) || !isRecord(referenceValue)) invalid();
	const content = {
		type: contentValue.type,
		data: contentValue.data,
		mimeType: contentValue.mimeType,
	};
	const reference = {
		type: referenceValue.type,
		attachmentId: referenceValue.attachmentId,
		mediaType: referenceValue.mediaType,
		bytes: referenceValue.bytes,
		width: referenceValue.width,
		height: referenceValue.height,
		...(referenceValue.name === undefined ? {} : { name: referenceValue.name }),
	};
	if (
		content.type !== "image" ||
		typeof content.data !== "string" ||
		typeof content.mimeType !== "string" ||
		content.data.length === 0 ||
		content.data.length > canonicalBase64Length(maxImageBytes) ||
		!Value.Check(ImageReferenceContentSchema, reference) ||
		content.mimeType !== reference.mediaType
	) {
		invalid();
	}
	const decoded = decodeCanonicalBase64(content.data);
	if (!decoded || decoded.byteLength > maxImageBytes) invalid();
	const attachmentId = `img_${createHash("sha256").update(decoded).digest("hex")}`;
	if (reference.bytes !== decoded.byteLength || reference.attachmentId !== attachmentId) {
		throw new AlignedImageProjectionError("image_integrity_mismatch");
	}
	return Object.freeze({
		content: Object.freeze({
			type: "image" as const,
			data: content.data,
			mimeType: content.mimeType as ImageMediaType,
		}),
		reference: Object.freeze(reference) as Readonly<ImageReferenceContent>,
		bytes: decoded.byteLength,
	});
}

function invalid(): never {
	throw new AlignedImageProjectionError("invalid_projection");
}

function canonicalBase64Length(bytes: number): number {
	return Math.ceil(bytes / 3) * 4;
}

function isRecord(value: unknown): value is Record<PropertyKey, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

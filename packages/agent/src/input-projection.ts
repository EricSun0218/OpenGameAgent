import { constants as bufferConstants } from "node:buffer";
import type { ImageContent, UserMessage } from "@earendil-works/pi-ai";
import { canonicalizeJsonValue, type MessageContent, MessageContentSchema } from "@opengameagent/protocol";
import { Value } from "typebox/value";
import { AlignedImageProjectionError, captureAlignedImageProjection } from "./image-projection.js";
import type {
	GameInputProjectionLimits,
	GameInputProjector,
	GameProjectedInput,
	NormalizedGameInput,
} from "./types.js";

const encoder = new TextEncoder();

export class GameInputProjectionError extends Error {
	constructor(
		public readonly code:
			| "invalid_configuration"
			| "invalid_projection"
			| "projection_too_large"
			| "image_integrity_mismatch"
			| "image_input_unsupported",
	) {
		super(code);
		this.name = "GameInputProjectionError";
	}
}

export interface PreparedGameInputProjection {
	readonly message: UserMessage;
	readonly transcriptContent: readonly MessageContent[];
}

export interface GameInputProjectionConfiguration {
	readonly project: GameInputProjector;
	readonly limits: Readonly<GameInputProjectionLimits>;
}

export function prepareInputProjectionConfiguration(
	project: GameInputProjector | undefined,
	limits: GameInputProjectionLimits | undefined,
): GameInputProjectionConfiguration | undefined {
	if ((project === undefined) !== (limits === undefined)) {
		throw new GameInputProjectionError("invalid_configuration");
	}
	if (!project || !limits) return undefined;
	const snapshot = {
		maxContentParts: positiveSafeInteger(limits.maxContentParts),
		maxTextBytes: positiveSafeInteger(limits.maxTextBytes),
		maxReferenceBytes: positiveSafeInteger(limits.maxReferenceBytes),
		maxImageBytes: positiveBufferLength(limits.maxImageBytes),
		maxTotalImageBytes: positiveBufferLength(limits.maxTotalImageBytes),
	} satisfies GameInputProjectionLimits;
	if (snapshot.maxImageBytes > snapshot.maxTotalImageBytes) {
		throw new GameInputProjectionError("invalid_configuration");
	}
	return Object.freeze({ project, limits: Object.freeze(snapshot) });
}

export function defaultGameInputProjection(
	input: Readonly<NormalizedGameInput>,
	timestamp: number,
): GameProjectedInput {
	const text = JSON.stringify(input.payload);
	return {
		message: { role: "user", content: text, timestamp },
		transcriptContent: [{ type: "text", text }],
	};
}

export function validateGameInputProjection(
	projection: unknown,
	limits: Readonly<GameInputProjectionLimits>,
	modelAcceptsImages: boolean,
): PreparedGameInputProjection {
	if (!isRecord(projection)) {
		throw new GameInputProjectionError("invalid_projection");
	}
	const messageValue: unknown = projection.message;
	const transcriptValue: unknown = projection.transcriptContent;
	if (!isRecord(messageValue) || !Array.isArray(transcriptValue)) {
		throw new GameInputProjectionError("invalid_projection");
	}
	const role: unknown = messageValue.role;
	const contentValue: unknown = messageValue.content;
	const timestamp: unknown = messageValue.timestamp;
	if (role !== "user" || typeof timestamp !== "number" || !Number.isSafeInteger(timestamp)) {
		throw new GameInputProjectionError("invalid_projection");
	}

	const transcriptLength = captureContentLength(transcriptValue, limits.maxContentParts);
	if (transcriptLength === 0) {
		throw new GameInputProjectionError("projection_too_large");
	}
	const canonicalTranscript: MessageContent[] = [];
	let referenceBytes = 0;
	for (let index = 0; index < transcriptLength; index += 1) {
		const part = captureTranscriptPart(transcriptValue[index]);
		if (part.type === "image-reference") {
			referenceBytes = checkedAdd(
				referenceBytes,
				encoder.encode(canonicalizeJsonValue(part)).byteLength,
				limits.maxReferenceBytes,
			);
		}
		canonicalTranscript.push(part);
	}

	const messagePartsValue: unknown =
		typeof contentValue === "string" ? [Object.freeze({ type: "text" as const, text: contentValue })] : contentValue;
	if (!Array.isArray(messagePartsValue)) {
		throw new GameInputProjectionError("invalid_projection");
	}
	const messageParts: readonly unknown[] = messagePartsValue;
	const messagePartCount = captureContentLength(messageParts, limits.maxContentParts);
	if (messagePartCount !== transcriptLength) {
		throw new GameInputProjectionError("invalid_projection");
	}

	let textBytes = 0;
	let totalImageBytes = 0;
	let imageCount = 0;
	const canonicalMessageParts: Array<{ readonly type: "text"; readonly text: string } | ImageContent> = [];
	for (let index = 0; index < messagePartCount; index += 1) {
		const part = messageParts[index];
		const transcriptPart = canonicalTranscript[index];
		if (!transcriptPart || !isRecord(part)) throw new GameInputProjectionError("invalid_projection");
		const type: unknown = part.type;
		if (type === "text" && transcriptPart.type === "text") {
			const text: unknown = part.text;
			if (typeof text !== "string" || text !== transcriptPart.text) {
				throw new GameInputProjectionError("invalid_projection");
			}
			textBytes = checkedAdd(textBytes, encoder.encode(text).byteLength, limits.maxTextBytes);
			canonicalMessageParts.push(Object.freeze({ type: "text" as const, text }));
			continue;
		}
		if (type !== "image" || transcriptPart.type !== "image-reference") {
			throw new GameInputProjectionError("invalid_projection");
		}
		const data: unknown = part.data;
		const mimeType: unknown = part.mimeType;
		let image: ReturnType<typeof captureAlignedImageProjection>;
		try {
			image = captureAlignedImageProjection({ type, data, mimeType }, transcriptPart, limits.maxImageBytes);
		} catch (error) {
			if (error instanceof AlignedImageProjectionError) {
				throw new GameInputProjectionError(error.code);
			}
			throw error;
		}
		imageCount += 1;
		totalImageBytes = checkedAdd(totalImageBytes, image.bytes, limits.maxTotalImageBytes);
		canonicalMessageParts.push(image.content);
	}
	if (imageCount > 0 && !modelAcceptsImages) {
		throw new GameInputProjectionError("image_input_unsupported");
	}
	Object.freeze(canonicalMessageParts);
	return Object.freeze({
		message: Object.freeze({ role: "user" as const, content: canonicalMessageParts, timestamp }),
		transcriptContent: Object.freeze(canonicalTranscript),
	});
}

export function contextContainsImages(messages: readonly unknown[]): boolean {
	return messages.some(
		(message) =>
			isRecord(message) &&
			Array.isArray(message.content) &&
			message.content.some((part: unknown) => isRecord(part) && part.type === "image"),
	);
}

function captureTranscriptPart(value: unknown): MessageContent {
	if (!isRecord(value)) throw new GameInputProjectionError("invalid_projection");
	const type: unknown = value.type;
	let part: unknown;
	if (type === "text") {
		const text: unknown = value.text;
		part = { type, text };
	} else if (type === "image-reference") {
		const attachmentId: unknown = value.attachmentId;
		const mediaType: unknown = value.mediaType;
		const bytes: unknown = value.bytes;
		const width: unknown = value.width;
		const height: unknown = value.height;
		const name: unknown = value.name;
		part = {
			type,
			attachmentId,
			mediaType,
			bytes,
			width,
			height,
			...(name === undefined ? {} : { name }),
		};
	} else {
		throw new GameInputProjectionError("invalid_projection");
	}
	if (!Value.Check(MessageContentSchema, part)) throw new GameInputProjectionError("invalid_projection");
	return Object.freeze(part) as MessageContent;
}

function captureContentLength(value: readonly unknown[], maximum: number): number {
	const length: unknown = value.length;
	if (!Number.isSafeInteger(length) || (length as number) < 0) {
		throw new GameInputProjectionError("invalid_projection");
	}
	if ((length as number) > maximum) throw new GameInputProjectionError("projection_too_large");
	return length as number;
}

function positiveSafeInteger(value: number): number {
	if (!Number.isSafeInteger(value) || value <= 0) throw new GameInputProjectionError("invalid_configuration");
	return value;
}

function positiveBufferLength(value: number): number {
	const checked = positiveSafeInteger(value);
	if (checked > bufferConstants.MAX_LENGTH) throw new GameInputProjectionError("invalid_configuration");
	return checked;
}

function checkedAdd(current: number, next: number, maximum: number): number {
	if (next > maximum - current) throw new GameInputProjectionError("projection_too_large");
	return current + next;
}

function isRecord(value: unknown): value is Record<PropertyKey, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

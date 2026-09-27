import { Buffer } from "node:buffer";
import type { ImageContent, TextContent } from "@earendil-works/pi-ai";
import {
	prepareInputProjectionConfiguration,
	type GameInputProjector,
	type GameInputProjectionLimits,
} from "@opengameagent/agent";
import {
	canonicalizeJsonValue,
	ImageReferenceContentSchema,
	type ImageReferenceContent,
	type MessageContent,
} from "@opengameagent/protocol";
import { Value } from "typebox/value";
import { ImageAttachmentError } from "./errors.js";
import type { ImageAttachmentInputProjection, ImageAttachmentInputProjectorOptions } from "./types.js";

const encoder = new TextEncoder();

export function createImageAttachmentInputProjection(
	options: ImageAttachmentInputProjectorOptions,
): ImageAttachmentInputProjection {
	if (
		!options ||
		typeof options.store?.read !== "function" ||
		typeof options.select !== "function" ||
		typeof options.authorize !== "function" ||
		(options.now !== undefined && typeof options.now !== "function")
	) {
		throw new ImageAttachmentError("invalid_configuration");
	}
	const read = options.store.read.bind(options.store);
	const select = options.select;
	const authorize = options.authorize;
	const now = options.now ?? Date.now;
	const limits = snapshotProjectionLimits(options.limits);

	const project: GameInputProjector = async (input, signal, coordinate) => {
		signal.throwIfAborted();
		const selected = await select(input, signal, coordinate);
		signal.throwIfAborted();
		const transcript = validateSelection(selected, limits);
		const content: Array<TextContent | ImageContent> = [];
		for (const part of transcript) {
			if (part.type === "text") {
				content.push(Object.freeze({ type: "text", text: part.text }));
				continue;
			}
			await authorize(input, Object.freeze({ ...coordinate }), part, signal);
			signal.throwIfAborted();
			const bytes = Buffer.from(await read(part, signal));
			signal.throwIfAborted();
			if (bytes.byteLength !== part.bytes || bytes.byteLength > limits.maxImageBytes) {
				throw new ImageAttachmentError("storage_corrupt");
			}
			content.push(Object.freeze({ type: "image", data: bytes.toString("base64"), mimeType: part.mediaType }));
		}
		const timestamp = now();
		if (!Number.isSafeInteger(timestamp)) throw new ImageAttachmentError("invalid_configuration");
		return Object.freeze({
			message: Object.freeze({ role: "user", content, timestamp }),
			transcriptContent: transcript,
		});
	};

	const prepared = prepareInputProjectionConfiguration(project, limits);
	if (!prepared) throw new ImageAttachmentError("invalid_configuration");
	return Object.freeze(prepared);
}

function validateSelection(selection: unknown, limits: Readonly<GameInputProjectionLimits>): readonly MessageContent[] {
	if (!Array.isArray(selection) || selection.length === 0) throw new ImageAttachmentError("invalid_input");
	if (selection.length > limits.maxContentParts) throw new ImageAttachmentError("input_too_large");
	const seen = new Set<string>();
	let textBytes = 0;
	let referenceBytes = 0;
	let imageBytes = 0;
	const prepared = selection.map((part) => {
		if (isTextPart(part)) {
			textBytes = addWithin(textBytes, encoder.encode(part.text).byteLength, limits.maxTextBytes);
			return Object.freeze({ type: "text" as const, text: part.text });
		}
		if (!Value.Check(ImageReferenceContentSchema, part)) throw new ImageAttachmentError("invalid_input");
		referenceBytes = addWithin(
			referenceBytes,
			encoder.encode(canonicalizeJsonValue(part)).byteLength,
			limits.maxReferenceBytes,
		);
		if (part.bytes > limits.maxImageBytes) throw new ImageAttachmentError("input_too_large");
		imageBytes = addWithin(imageBytes, part.bytes, limits.maxTotalImageBytes);
		if (seen.has(part.attachmentId)) throw new ImageAttachmentError("invalid_input");
		seen.add(part.attachmentId);
		return Object.freeze({ ...part }) as Readonly<ImageReferenceContent>;
	});
	return Object.freeze(prepared);
}

function snapshotProjectionLimits(limits: GameInputProjectionLimits): Readonly<GameInputProjectionLimits> {
	const probe: GameInputProjector = () => {
		throw new Error("unreachable");
	};
	const prepared = prepareInputProjectionConfiguration(probe, limits);
	if (!prepared) throw new ImageAttachmentError("invalid_configuration");
	return prepared.limits;
}

function addWithin(current: number, next: number, maximum: number): number {
	if (next > maximum - current) throw new ImageAttachmentError("input_too_large");
	return current + next;
}

function isTextPart(value: unknown): value is { readonly type: "text"; readonly text: string } {
	return (
		typeof value === "object" &&
		value !== null &&
		!Array.isArray(value) &&
		Object.keys(value).length === 2 &&
		Object.hasOwn(value, "type") &&
		Object.hasOwn(value, "text") &&
		(value as { type?: unknown }).type === "text" &&
		typeof (value as { text?: unknown }).text === "string"
	);
}

import { Buffer } from "node:buffer";
import type { ImageContent } from "@earendil-works/pi-ai";
import type {
	PreparedTranscriptToolResultMedia,
	TranscriptMedia,
	TranscriptMediaReadRequest,
	TranscriptPiMediaContent,
	TranscriptToolResultMediaRequest,
} from "@opengameagent/agent";
import type { ImageMediaType, ImageReferenceContent, MessageContent } from "@opengameagent/protocol";
import { ImageAttachmentError } from "./errors.js";
import type { ImageAttachmentInput, ImageAttachmentTranscriptMediaOptions } from "./types.js";

export function createImageAttachmentTranscriptMedia(options: ImageAttachmentTranscriptMediaOptions): TranscriptMedia {
	if (
		!options ||
		typeof options.store?.admit !== "function" ||
		typeof options.store?.read !== "function" ||
		typeof options.authorize !== "function" ||
		!Number.isSafeInteger(options.maxRestoreMediaBytes) ||
		options.maxRestoreMediaBytes <= 0
	) {
		throw new ImageAttachmentError("invalid_configuration");
	}
	const store = options.store;
	const admit = store.admit.bind(store);
	const read = store.read.bind(store);
	const authorize = options.authorize;
	const maxRestoreMediaBytes = options.maxRestoreMediaBytes;

	return Object.freeze({
		captureToolResult: async (
			request: Readonly<TranscriptToolResultMediaRequest>,
			signal: AbortSignal,
		): Promise<Readonly<PreparedTranscriptToolResultMedia>> => {
			signal.throwIfAborted();
			const inputs = request.content.flatMap((part): ImageAttachmentInput[] =>
				part.type === "image"
					? [{ encoding: "base64", data: part.data, mediaType: part.mimeType as ImageMediaType }]
					: [],
			);
			if (inputs.length === 0) {
				const content = Object.freeze(request.content.map(snapshotPiContent));
				return Object.freeze({ content, durableContent: content as readonly MessageContent[] });
			}

			const admitted = await admit(inputs, signal);
			signal.throwIfAborted();
			if (admitted.length !== inputs.length) throw new ImageAttachmentError("storage_corrupt");
			const images: TranscriptPiMediaContent[] = [];
			const references: ImageReferenceContent[] = [];
			for (const reference of admitted) {
				const durable = Object.freeze({ ...reference });
				const bytes = Buffer.from(await read(durable, signal));
				signal.throwIfAborted();
				references.push(durable);
				images.push(
					Object.freeze({ type: "image" as const, data: bytes.toString("base64"), mimeType: durable.mediaType }),
				);
			}

			let imageIndex = 0;
			const content: TranscriptPiMediaContent[] = [];
			const durableContent: MessageContent[] = [];
			for (const part of request.content) {
				if (part.type === "text") {
					const text = Object.freeze({ type: "text" as const, text: part.text });
					content.push(text);
					durableContent.push(text);
					continue;
				}
				const image = images[imageIndex];
				const reference = references[imageIndex];
				if (!image || !reference) throw new ImageAttachmentError("storage_corrupt");
				content.push(image);
				durableContent.push(reference);
				imageIndex += 1;
			}
			return Object.freeze({ content: Object.freeze(content), durableContent: Object.freeze(durableContent) });
		},
		read: async (
			requests: readonly Readonly<TranscriptMediaReadRequest>[],
			signal: AbortSignal,
		): Promise<readonly Readonly<ImageContent>[]> => {
			signal.throwIfAborted();
			let mediaBytes = 0;
			for (const request of requests) {
				if (request.reference.bytes > maxRestoreMediaBytes - mediaBytes) {
					throw new ImageAttachmentError("input_too_large");
				}
				mediaBytes += request.reference.bytes;
			}
			const content: ImageContent[] = [];
			for (const request of requests) {
				await authorize(request, signal);
				signal.throwIfAborted();
				const bytes = Buffer.from(await read(request.reference, signal));
				signal.throwIfAborted();
				content.push(
					Object.freeze({
						type: "image" as const,
						data: bytes.toString("base64"),
						mimeType: request.reference.mediaType,
					}),
				);
			}
			return Object.freeze(content);
		},
	});
}

function snapshotPiContent(part: TranscriptPiMediaContent): TranscriptPiMediaContent {
	return part.type === "text"
		? Object.freeze({ type: "text", text: part.text })
		: Object.freeze({ type: "image", data: part.data, mimeType: part.mimeType });
}

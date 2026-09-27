import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type { TranscriptMediaReadRequest } from "@opengameagent/agent";
import type { ImageReferenceContent } from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import { createImageAttachmentTranscriptMedia, type ImageAttachmentStore } from "../src/index.js";

describe("image attachment transcript media", () => {
	it("admits final Tool images as one batch and returns normalized Pi content aligned with durable references", async () => {
		const originalJpeg = Buffer.from("original-jpeg");
		const originalPng = Buffer.from("original-png");
		const normalizedFirst = image("normalized-first");
		const normalizedSecond = image("normalized-second");
		const readOrder: string[] = [];
		const store: ImageAttachmentStore = {
			admit: vi.fn(async () => [normalizedFirst.reference, normalizedSecond.reference]),
			read: async (reference) => {
				readOrder.push(reference.attachmentId);
				return reference.attachmentId === normalizedFirst.reference.attachmentId
					? normalizedFirst.bytes
					: normalizedSecond.bytes;
			},
		};
		const media = createImageAttachmentTranscriptMedia({
			store,
			maxRestoreMediaBytes: 1,
			authorize: () => undefined,
		});

		const prepared = await media.captureToolResult(
			{
				stream: { sessionId: "session", actorId: "actor" },
				coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
				toolCallId: "call",
				toolName: "inspect",
				content: [
					{ type: "text", text: "before" },
					{ type: "image", data: originalJpeg.toString("base64"), mimeType: "image/jpeg" },
					{ type: "text", text: "between" },
					{ type: "image", data: originalPng.toString("base64"), mimeType: "image/png" },
				],
			},
			new AbortController().signal,
		);

		expect(store.admit).toHaveBeenCalledOnce();
		expect(store.admit).toHaveBeenCalledWith(
			[
				{ encoding: "base64", data: originalJpeg.toString("base64"), mediaType: "image/jpeg" },
				{ encoding: "base64", data: originalPng.toString("base64"), mediaType: "image/png" },
			],
			expect.any(AbortSignal),
		);
		expect(readOrder).toEqual([normalizedFirst.reference.attachmentId, normalizedSecond.reference.attachmentId]);
		expect(prepared.content).toEqual([
			{ type: "text", text: "before" },
			{ type: "image", data: normalizedFirst.base64, mimeType: "image/png" },
			{ type: "text", text: "between" },
			{ type: "image", data: normalizedSecond.base64, mimeType: "image/png" },
		]);
		expect(prepared.durableContent).toEqual([
			{ type: "text", text: "before" },
			normalizedFirst.reference,
			{ type: "text", text: "between" },
			normalizedSecond.reference,
		]);
		expect(JSON.stringify(prepared.durableContent)).not.toContain(originalJpeg.toString("base64"));
	});

	it("rejects the restore batch by exact reference occurrences before authorization or reads", async () => {
		const retained = image("abc");
		let authorizations = 0;
		let reads = 0;
		const media = createImageAttachmentTranscriptMedia({
			store: {
				admit: async () => [],
				read: async () => {
					reads += 1;
					return retained.bytes;
				},
			},
			maxRestoreMediaBytes: retained.reference.bytes * 2 - 1,
			authorize: () => {
				authorizations += 1;
			},
		});

		await expect(
			media.read([request(retained.reference), request(retained.reference)], new AbortController().signal),
		).rejects.toMatchObject({ name: "ImageAttachmentError", code: "input_too_large" });
		expect({ authorizations, reads }).toEqual({ authorizations: 0, reads: 0 });
	});

	it("authorizes each durable reference before reading its normalized bytes", async () => {
		const first = image("first");
		const second = image("second");
		const operations: string[] = [];
		const media = createImageAttachmentTranscriptMedia({
			store: {
				admit: async () => [],
				read: async (reference) => {
					operations.push(`read:${reference.attachmentId}`);
					return reference.attachmentId === first.reference.attachmentId ? first.bytes : second.bytes;
				},
			},
			maxRestoreMediaBytes: first.reference.bytes + second.reference.bytes,
			authorize: (selected) => {
				operations.push(`authorize:${selected.role}:${selected.reference.attachmentId}`);
			},
		});
		const secondRequest: TranscriptMediaReadRequest = {
			stream: { sessionId: "session", actorId: "actor" },
			role: "toolResult",
			reference: second.reference,
			toolCallId: "call",
			toolName: "inspect",
		};

		const restored = await media.read([request(first.reference), secondRequest], new AbortController().signal);

		expect(operations).toEqual([
			`authorize:user:${first.reference.attachmentId}`,
			`read:${first.reference.attachmentId}`,
			`authorize:toolResult:${second.reference.attachmentId}`,
			`read:${second.reference.attachmentId}`,
		]);
		expect(restored).toEqual([
			{ type: "image", data: first.base64, mimeType: "image/png" },
			{ type: "image", data: second.base64, mimeType: "image/png" },
		]);
	});
});

function image(seed: string) {
	const bytes = Buffer.from(seed);
	const reference: ImageReferenceContent = {
		type: "image-reference",
		attachmentId: `img_${createHash("sha256").update(bytes).digest("hex")}`,
		mediaType: "image/png",
		bytes: bytes.byteLength,
		width: 1,
		height: 1,
	};
	return { bytes, base64: bytes.toString("base64"), reference };
}

function request(reference: ImageReferenceContent): TranscriptMediaReadRequest {
	return { stream: { sessionId: "session", actorId: "actor" }, role: "user", reference };
}

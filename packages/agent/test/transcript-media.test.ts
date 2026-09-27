import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type { ImageContent } from "@earendil-works/pi-ai";
import type { DurableTranscriptMessage, ImageReferenceContent } from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import {
	captureTranscriptMessage,
	DurableTranscript,
	InMemoryTranscriptStore,
	TranscriptUnsupportedMediaError,
	type TranscriptMedia,
} from "../src/index.js";

const STREAM = { sessionId: "session", actorId: "actor" } as const;
const COORDINATE = { ...STREAM, runId: "run", turn: "0" } as const;

describe("durable transcript media", () => {
	it("hydrates only image references in the final checkpoint projection", async () => {
		const first = image("discarded");
		const retained = image("retained");
		const reads: string[][] = [];
		const transcript = createTranscript({
			read: async (requests) => {
				reads.push(requests.map(({ reference }) => reference.attachmentId));
				return requests.map(({ reference }) => piImage(reference));
			},
		});
		const source = await commitClosedTurn(transcript, user(first));
		await transcript.checkpoint({ stream: STREAM, source, replacement: [user(retained)] });
		reads.length = 0;

		const restored = await transcript.restore(STREAM);

		expect(reads).toEqual([[retained.reference.attachmentId]]);
		expect(restored.durableMessages).toEqual([user(retained)]);
		expect(restored.messages).toEqual([{ role: "user", content: [piImage(retained.reference)], timestamp: 1 }]);
	});

	it("fails checkpoint media hydration before appending the checkpoint", async () => {
		const store = new InMemoryTranscriptStore();
		let recordId = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 8_192,
			maxCommitBytes: 16_384,
			maxRestoreBytes: 32_768,
			maxJsonDepth: 16,
			newRecordId: () => `record-${++recordId}`,
			media: {
				read: async () => {
					throw new Error("media unavailable");
				},
				captureToolResult: async () => {
					throw new Error("unused");
				},
			},
		});
		const source = await commitClosedTurn(transcript, textUser("source"));

		await expect(
			transcript.checkpoint({ stream: STREAM, source, replacement: [user(image("replacement"))] }),
		).rejects.toThrow("media unavailable");
		const stored = await store.load(STREAM, 32_768);
		expect(stored.records.some((record) => record.type === "context.checkpoint")).toBe(false);
	});

	it("verifies hydrated media alignment and integrity", async () => {
		const expected = image("expected");
		const wrong = image("wrong");
		for (const returned of [[], [piImage(wrong.reference)]] as const) {
			const transcript = createTranscript({ read: async () => returned });
			await transcript.commit({
				stream: STREAM,
				expectedRevision: "empty",
				records: [{ type: "message.completed", coordinate: COORDINATE, message: user(expected) }],
			});
			await expect(transcript.restore(STREAM)).rejects.toThrow(TranscriptUnsupportedMediaError);
		}
	});

	it("captures aligned immutable references for final Tool media", async () => {
		const original = image("original", "image/jpeg");
		const normalized = image("normalized");
		const media: TranscriptMedia = {
			read: async () => [],
			captureToolResult: vi.fn(async () => ({
				content: [
					{ type: "text" as const, text: "before" },
					piImage(normalized.reference),
					{ type: "text" as const, text: "after" },
				],
				durableContent: [
					{ type: "text" as const, text: "before" },
					normalized.reference,
					{ type: "text" as const, text: "after" },
				],
			})),
		};
		const transcript = createTranscript(media);

		const prepared = await transcript.captureToolResult(
			{
				stream: STREAM,
				coordinate: COORDINATE,
				toolCallId: "call",
				toolName: "inspect",
				content: [{ type: "text", text: "before" }, piImage(original.reference), { type: "text", text: "after" }],
			},
			new AbortController().signal,
		);

		expect(prepared.content[1]).toEqual(piImage(normalized.reference));
		const durable = captureTranscriptMessage(
			{
				role: "toolResult",
				toolCallId: "call",
				toolName: "inspect",
				content: [...prepared.content],
				details: {},
				isError: false,
				timestamp: 1,
			},
			prepared.durableContent,
		);
		expect(durable).toMatchObject({
			role: "toolResult",
			content: [{ type: "text", text: "before" }, normalized.reference, { type: "text", text: "after" }],
		});
		expect(JSON.stringify(durable)).not.toContain(normalized.base64);
	});

	it("keeps text-only transcripts usable without a media Adapter", async () => {
		const transcript = createTranscript();
		await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [{ type: "message.completed", coordinate: COORDINATE, message: textUser("hello") }],
		});

		await expect(transcript.restore(STREAM)).resolves.toMatchObject({
			durableMessages: [textUser("hello")],
			messages: [{ role: "user", content: "hello", timestamp: 1 }],
		});
	});

	it("requires a media Adapter only when the final durable projection contains an image", async () => {
		const transcript = createTranscript();
		await transcript.commit({
			stream: STREAM,
			expectedRevision: "empty",
			records: [{ type: "message.completed", coordinate: COORDINATE, message: user(image("adapter-required")) }],
		});

		await expect(transcript.restore(STREAM)).rejects.toBeInstanceOf(TranscriptUnsupportedMediaError);
	});
});

function createTranscript(media?: Partial<TranscriptMedia>): DurableTranscript {
	let recordId = 0;
	const preparedMedia = media
		? ({
				captureToolResult:
					media.captureToolResult ??
					(async () => {
						throw new Error("unused");
					}),
				read:
					media.read ??
					(async () => {
						throw new Error("unused");
					}),
			} satisfies TranscriptMedia)
		: undefined;
	return new DurableTranscript(new InMemoryTranscriptStore(), {
		maxRecordBytes: 8_192,
		maxCommitBytes: 16_384,
		maxRestoreBytes: 32_768,
		maxJsonDepth: 16,
		newRecordId: () => `record-${++recordId}`,
		...(preparedMedia ? { media: preparedMedia } : {}),
	});
}

async function commitClosedTurn(
	transcript: DurableTranscript,
	message: Exclude<DurableTranscriptMessage, { role: "toolResult" }>,
): Promise<Extract<Awaited<ReturnType<DurableTranscript["commit"]>>["records"][number], { type: "turn.closed" }>> {
	const assistantMessage = {
		role: "assistant" as const,
		content: [{ type: "text" as const, text: "done" }],
		api: "openai-responses",
		provider: "openai",
		model: "test",
		stopReason: "stop" as const,
		timestamp: 1,
	};
	const committed = await transcript.commit({
		stream: STREAM,
		expectedRevision: "empty",
		records: [
			{ type: "message.completed", coordinate: COORDINATE, message },
			{ type: "message.completed", coordinate: COORDINATE, message: assistantMessage },
			{ type: "turn.closed", coordinate: COORDINATE, status: "completed" },
		],
	});
	const closure = committed.records[2];
	if (closure?.type !== "turn.closed") throw new Error("Expected a closure.");
	return closure;
}

function textUser(text: string): Extract<DurableTranscriptMessage, { role: "user" }> {
	return { role: "user", contentFormat: "text", content: [{ type: "text", text }], timestamp: 1 };
}

function user(value: ReturnType<typeof image>): Extract<DurableTranscriptMessage, { role: "user" }> {
	return { role: "user", contentFormat: "parts", content: [value.reference], timestamp: 1 };
}

function image(seed: string, mediaType: ImageReferenceContent["mediaType"] = "image/png") {
	const bytes = Buffer.from(seed);
	const reference: ImageReferenceContent = {
		type: "image-reference",
		attachmentId: `img_${createHash("sha256").update(bytes).digest("hex")}`,
		mediaType,
		bytes: bytes.byteLength,
		width: 1,
		height: 1,
	};
	return { bytes, base64: bytes.toString("base64"), reference };
}

function piImage(reference: ImageReferenceContent): ImageContent {
	const seed =
		reference.attachmentId === image("discarded").reference.attachmentId
			? "discarded"
			: reference.attachmentId === image("retained").reference.attachmentId
				? "retained"
				: reference.attachmentId === image("expected").reference.attachmentId
					? "expected"
					: reference.attachmentId === image("wrong").reference.attachmentId
						? "wrong"
						: reference.attachmentId === image("normalized").reference.attachmentId
							? "normalized"
							: reference.attachmentId === image("original", "image/jpeg").reference.attachmentId
								? "original"
								: "unknown";
	return { type: "image", data: Buffer.from(seed).toString("base64"), mimeType: reference.mediaType };
}

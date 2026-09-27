import { Buffer } from "node:buffer";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { Context } from "@earendil-works/pi-ai";
import { GameAgent, type GameInputProjectionLimits } from "@opengameagent/agent";
import { type CanonicalEvent, eventAudience, PROTOCOL_VERSION, type RunCoordinate } from "@opengameagent/protocol";
import sharp from "sharp";
import { afterEach, describe, expect, it } from "vitest";
import { textResponse } from "../../devtools/src/pi-fakes.js";
import {
	createImageAttachmentInputProjection,
	type ImageAttachmentError,
	LocalImageAttachmentStore,
	type ImageAttachmentLimits,
} from "../src/index.js";

const temporaryRoots: string[] = [];

afterEach(async () => {
	for (const root of temporaryRoots.splice(0)) await rm(root, { recursive: true, force: true });
});

describe("image attachment input projection", () => {
	it("authorizes verified local bytes for Pi while canonical events retain only references", async () => {
		const bytes = await sharp({
			create: { width: 2, height: 1, channels: 3, background: { r: 8, g: 16, b: 32 } },
		})
			.png()
			.toBuffer();
		const parent = await createTemporaryRoot();
		const limits = await exactStoreLimits(bytes);
		const store = await LocalImageAttachmentStore.open({ rootDirectory: join(parent, "store"), limits });
		const [admittedReference] = await store.admit(
			[{ encoding: "bytes", data: bytes, mediaType: "image/png", name: "pixel.png" }],
			new AbortController().signal,
		);
		const reference = required(admittedReference);
		const text = "inspect";
		const projectionLimits: GameInputProjectionLimits = {
			maxContentParts: 2,
			maxTextBytes: new TextEncoder().encode(text).byteLength,
			maxReferenceBytes: Buffer.byteLength(JSON.stringify(reference), "utf8"),
			maxImageBytes: reference.bytes,
			maxTotalImageBytes: reference.bytes,
		};
		const authorizations: Array<{ coordinate: RunCoordinate; attachmentId: string }> = [];
		const projection = createImageAttachmentInputProjection({
			store,
			limits: projectionLimits,
			now: () => 1,
			select: (input) => {
				expect(() => {
					(input.payload as { nested: { value: number } }).nested.value = 2;
				}).toThrow(TypeError);
				return [{ type: "text", text }, reference];
			},
			authorize: (_input, coordinate, selected) => {
				authorizations.push({ coordinate: { ...coordinate }, attachmentId: selected.attachmentId });
			},
		});

		const contexts: Context[] = [];
		const modelStream: StreamFn = (model, context) => {
			contexts.push(structuredClone(context));
			return textResponse("seen", [], model);
		};
		const events: CanonicalEvent[] = [];
		let sequence = 0;
		let identifier = 0;
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: {
				id: "vision-model",
				name: "Vision model",
				api: "openai-responses",
				provider: "openai",
				baseUrl: "https://example.invalid",
				reasoning: true,
				input: ["text", "image"],
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
				contextWindow: 1,
				maxTokens: 1,
			},
			modelStream,
			projectInput: projection.project,
			inputProjectionLimits: projection.limits,
			emitEvent: async (coordinate, data) => {
				sequence += 1;
				const ordinal = String(sequence);
				const event: CanonicalEvent = {
					protocolVersion: PROTOCOL_VERSION,
					eventId: `event-${ordinal}`,
					sequence: ordinal,
					revision: ordinal,
					occurredAt: "2026-08-29T00:00:00.000Z",
					coordinate: { ...coordinate },
					audience: eventAudience(data),
					data: structuredClone(data),
				};
				events.push(event);
				return structuredClone(event);
			},
			newId: () => {
				identifier += 1;
				return `generated-${identifier}`;
			},
			now: () => 1,
		});
		const completion = await agent.start(
			{
				sessionId: "session",
				actorId: "actor",
				inputId: "input",
				payload: { nested: { value: 1 } },
			},
			"run-image",
		).completion;

		expect(completion.status).toBe("completed");
		expect(authorizations).toEqual([
			{
				coordinate: { sessionId: "session", actorId: "actor", runId: "run-image", turn: "0" },
				attachmentId: reference.attachmentId,
			},
		]);
		const user = contexts[0]?.messages[0];
		expect(user?.role).toBe("user");
		if (user?.role !== "user" || !Array.isArray(user.content)) throw new Error("expected image user message");
		expect(user.content).toEqual([
			{ type: "text", text },
			{
				type: "image",
				data: Buffer.from(await store.read(reference, new AbortController().signal)).toString("base64"),
				mimeType: "image/png",
			},
		]);
		const serializedEvents = JSON.stringify(events);
		expect(serializedEvents).toContain(reference.attachmentId);
		expect(serializedEvents).not.toContain((user.content[1] as { data: string }).data);
	});

	it("rejects duplicate and aggregate-oversized selections before authorization and storage reads", async () => {
		const reference = {
			type: "image-reference" as const,
			attachmentId: `img_${"a".repeat(64)}`,
			mediaType: "image/png" as const,
			bytes: 2,
			width: 1,
			height: 1,
		};
		let authorized = 0;
		let reads = 0;
		const reader = {
			read: async () => {
				reads += 1;
				return Buffer.alloc(reference.bytes);
			},
		};
		const projection = createImageAttachmentInputProjection({
			store: reader,
			limits: {
				maxContentParts: 2,
				maxTextBytes: 1,
				maxReferenceBytes: Buffer.byteLength(JSON.stringify(reference), "utf8") * 2,
				maxImageBytes: 2,
				maxTotalImageBytes: 4,
			},
			select: () => [reference, reference],
			authorize: () => {
				authorized += 1;
			},
		});
		reader.read = async () => {
			throw new Error("mutated reader");
		};
		await expectCode(
			Promise.resolve(
				projection.project(
					{ sessionId: "session", actorId: "actor", inputId: "input", payload: null },
					new AbortController().signal,
					{ sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
				),
			),
			"invalid_input",
		);
		expect({ authorized, reads }).toEqual({ authorized: 0, reads: 0 });

		const secondReference = { ...reference, attachmentId: `img_${"c".repeat(64)}` };
		const oversized = createImageAttachmentInputProjection({
			store: {
				read: async () => {
					reads += 1;
					return Buffer.alloc(reference.bytes);
				},
			},
			limits: {
				maxContentParts: 2,
				maxTextBytes: 1,
				maxReferenceBytes:
					Buffer.byteLength(JSON.stringify(reference), "utf8") +
					Buffer.byteLength(JSON.stringify(secondReference), "utf8"),
				maxImageBytes: 2,
				maxTotalImageBytes: 3,
			},
			select: () => [reference, secondReference],
			authorize: () => {
				authorized += 1;
			},
		});
		await expectCode(
			Promise.resolve(
				oversized.project(
					{ sessionId: "session", actorId: "actor", inputId: "input", payload: null },
					new AbortController().signal,
					{ sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
				),
			),
			"input_too_large",
		);
		expect({ authorized, reads }).toEqual({ authorized: 0, reads: 0 });
	});

	it("rejects aggregate canonical image-reference metadata before authorization and storage reads", async () => {
		const reference = {
			type: "image-reference" as const,
			attachmentId: `img_${"d".repeat(64)}`,
			mediaType: "image/png" as const,
			bytes: 1,
			width: 1,
			height: 1,
			name: "n".repeat(256),
		};
		let authorized = 0;
		let reads = 0;
		const projection = createImageAttachmentInputProjection({
			store: {
				read: async () => {
					reads += 1;
					return Buffer.alloc(1);
				},
			},
			limits: {
				maxContentParts: 2,
				maxTextBytes: 1,
				maxReferenceBytes: 500,
				maxImageBytes: 1,
				maxTotalImageBytes: 2,
			},
			select: () => [reference, { ...reference, attachmentId: `img_${"e".repeat(64)}` }],
			authorize: () => {
				authorized += 1;
			},
		});

		await expectCode(
			Promise.resolve(
				projection.project(
					{ sessionId: "session", actorId: "actor", inputId: "input", payload: null },
					new AbortController().signal,
					{ sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
				),
			),
			"input_too_large",
		);
		expect({ authorized, reads }).toEqual({ authorized: 0, reads: 0 });
	});

	it("requires authorization before reading and propagates denial without disclosure", async () => {
		const reference = {
			type: "image-reference" as const,
			attachmentId: `img_${"b".repeat(64)}`,
			mediaType: "image/png" as const,
			bytes: 1,
			width: 1,
			height: 1,
		};
		let reads = 0;
		const denial = new Error("denied");
		const projection = createImageAttachmentInputProjection({
			store: {
				read: async () => {
					reads += 1;
					return Buffer.alloc(1);
				},
			},
			limits: {
				maxContentParts: 1,
				maxTextBytes: 1,
				maxReferenceBytes: Buffer.byteLength(JSON.stringify(reference), "utf8"),
				maxImageBytes: 1,
				maxTotalImageBytes: 1,
			},
			select: () => [reference],
			authorize: () => {
				throw denial;
			},
		});
		await expect(
			projection.project(
				{ sessionId: "session", actorId: "actor", inputId: "input", payload: null },
				new AbortController().signal,
				{ sessionId: "session", actorId: "actor", runId: "run", turn: "0" },
			),
		).rejects.toBe(denial);
		expect(reads).toBe(0);
	});
});

async function exactStoreLimits(bytes: Buffer): Promise<ImageAttachmentLimits> {
	const metadata = await sharp(bytes).metadata();
	const normalized = await sharp(bytes).autoOrient().toColourspace("srgb").png().toBuffer();
	const pixels = metadata.autoOrient.width * metadata.autoOrient.height;
	const dimension = Math.max(metadata.autoOrient.width, metadata.autoOrient.height);
	return {
		maxInputBytes: bytes.byteLength,
		maxInputPixels: pixels,
		maxInputDimension: dimension,
		maxStoredBytes: normalized.byteLength,
		maxStoredPixels: pixels,
		maxStoredDimension: dimension,
		maxReadBytes: normalized.byteLength,
		maxNameBytes: new TextEncoder().encode("pixel.png").byteLength,
		maxImagesPerBatch: 1,
		maxBatchInputBytes: bytes.byteLength,
		maxBatchStoredBytes: normalized.byteLength,
	};
}

async function createTemporaryRoot(): Promise<string> {
	const root = await mkdtemp(join(tmpdir(), "opengameagent-attachment-projector-test-"));
	temporaryRoots.push(root);
	return root;
}

function required<T>(value: T | undefined): T {
	if (value === undefined) throw new Error("required test value is missing");
	return value;
}

async function expectCode(promise: Promise<unknown>, code: ImageAttachmentError["code"]): Promise<void> {
	await expect(promise).rejects.toMatchObject({ name: "ImageAttachmentError", code });
}

import type { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { Context, Model } from "@earendil-works/pi-ai";
import {
	createGameProvenanceAlias,
	DurableTranscript,
	GameAgent,
	type GameAgentOptions,
	type GameModelRequestProvenance,
	InMemoryTranscriptStore,
} from "@opengameagent/agent";
import {
	type CanonicalPngAttachmentStore,
	type ImageAttachmentAuthorizer,
	renderCanonicalPng,
} from "@opengameagent/attachments";
import {
	type CanonicalEvent,
	eventAudience,
	type ImageReferenceContent,
	PROTOCOL_VERSION,
	type RunCoordinate,
} from "@opengameagent/protocol";
import sharp from "sharp";
import { describe, expect, it, vi } from "vitest";
import {
	allowAllTestTools,
	assistant,
	messageResponse,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
	tool,
	toolCall,
} from "../../devtools/src/pi-fakes.js";
import { createVisualObservationContextSource } from "../src/index.js";

const INPUT = {
	sessionId: "session-observation",
	actorId: "actor-observation",
	inputId: "input-observation",
	payload: { instruction: "inspect the current scene" },
} as const;

const VISION_MODEL: Model<"openai-responses"> = {
	...TEST_MODEL,
	id: "test-vision-model",
	name: "Test vision model",
	input: ["text", "image"],
};

const TRANSCRIPT_TEST_LIMITS = Object.freeze({
	maxRecordBytes: 16_384,
	maxCommitBytes: 65_536,
	maxRestoreBytes: 131_072,
	maxJsonDepth: 16,
});

describe("visual observations at the Pi provider boundary", () => {
	it("refreshes one host snapshot for every real Pi request and keeps bytes out of durable surfaces", async () => {
		const frames = [
			[await canonicalPng(11, 12, 13), await canonicalPng(21, 22, 23)],
			[await canonicalPng(31, 32, 33), await canonicalPng(41, 42, 43)],
		] as const;
		const stored = new Map<string, Buffer>();
		for (const frame of frames) {
			for (const bytes of frame) stored.set(imageReference(bytes).attachmentId, bytes);
		}
		const captureCoordinates: RunCoordinate[] = [];
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>(async (reference) => {
			const bytes = stored.get(reference.attachmentId);
			if (!bytes) throw new Error("Unknown observation fixture.");
			return new Uint8Array(bytes);
		});
		const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>();
		const contextLimits = {
			maxReferencesPerSnapshot: 3,
			maxSnapshotBytes: 16_384,
			media: {
				maxContentParts: 3,
				maxImageBytes: Math.max(...frames.flat().map((bytes) => bytes.byteLength)),
				maxTotalImageBytes: Math.max(
					...frames.map((frame) => frame.reduce((total, bytes) => total + bytes.byteLength, 0)),
				),
			},
		} as const;
		let captureCount = 0;
		const source = createVisualObservationContextSource({
			sourceId: "host-observation",
			selectionReferenceId: "host-selection",
			capture: (_input, _signal, coordinate) => {
				captureCoordinates.push(structuredClone(coordinate));
				const frame = frames[captureCount++];
				if (!frame) throw new Error("The source was captured more than once for one provider request.");
				const request = captureCount;
				return {
					observationVersion: "1",
					revision: `snapshot-${request}`,
					references: [
						{
							referenceId: "scene",
							revision: `scene-${request}`,
							content: {
								kind: "scene-observation",
								coordinateFrame: "world",
								observedBy: "game-host",
								sequence: request,
							},
						},
						{
							referenceId: "bev",
							revision: `bev-${request}`,
							content: {
								kind: "calibrated-bev",
								sourceFrame: "camera",
								targetFrame: "world",
								units: "meters",
								sequence: request,
							},
						},
					],
					candidates: [
						{
							viewId: "optional-scene-view",
							required: false,
							contextReferenceId: "scene",
							source: imageReference(frame[0], `private-scene-label-${request}`),
							transform: { kind: "original" },
						},
						{
							viewId: "required-bev-view",
							required: true,
							contextReferenceId: "bev",
							source: imageReference(frame[1], `private-bev-label-${request}`),
							transform: { kind: "original" },
						},
					],
				};
			},
			authorize,
			store: { read, publishCanonicalPng },
			projectionLimits: contextLimits,
			maxCandidatesPerSnapshot: 2,
			maxTransformCacheEntries: 1,
		});
		const providerContexts: Context[] = [];
		const requests: GameModelRequestProvenance[] = [];
		let providerCalls = 0;
		const modelStream: StreamFn = (model, context) => {
			providerContexts.push(copyContext(context));
			providerCalls += 1;
			return providerCalls === 1
				? messageResponse(assistant([toolCall("call-inspect", "inspect")], "toolUse", model))
				: textResponse("done", [], model);
		};
		const transcriptBinding = await createTranscriptBinding();
		const { agent, events } = createAgent(modelStream, {
			model: VISION_MODEL,
			contextSources: [source],
			contextLimits,
			toolSource: () => [
				tool("inspect", async () => ({ content: [{ type: "text", text: "inspected" }], details: {} })),
			],
			toolPolicy: allowAllTestTools,
			toolLimits: TEST_TOOL_LIMITS,
			transcript: transcriptBinding,
			providerProvenance: {
				scopeId: "observation-integration",
				sink: {
					recordRequest: async (request) => {
						requests.push(structuredClone(request));
						return createGameProvenanceAlias("record", `observation-request-${requests.length}`);
					},
					recordResponse: async () => undefined,
				},
			},
		});

		await expect(agent.start(INPUT, "run-observation").completion).resolves.toMatchObject({ status: "completed" });

		expect({ providerCalls, captureCount }).toEqual({ providerCalls: 2, captureCount: 2 });
		expect(captureCoordinates.map(({ runId, turn }) => [runId, turn])).toEqual([
			["run-observation", "1"],
			["run-observation", "2"],
		]);
		expect(new Set(captureCoordinates.map(({ runId, turn }) => `${runId}\0${turn}`)).size).toBe(providerCalls);
		expect(authorize).toHaveBeenCalledTimes(4);
		expect(read).toHaveBeenCalledTimes(4);
		expect(publishCanonicalPng).not.toHaveBeenCalled();

		for (const [index, context] of providerContexts.entries()) {
			const message = context.messages[0];
			if (message?.role !== "user" || !Array.isArray(message.content)) {
				throw new Error("Expected the visual observation before the real user message.");
			}
			const frame = frames[index];
			if (!frame) throw new Error("Missing provider-frame fixture.");
			expect(message.content.slice(1)).toEqual(
				frame.map((bytes) => ({ type: "image", data: bytes.toString("base64"), mimeType: "image/png" })),
			);
			const text = message.content[0];
			if (text?.type !== "text") throw new Error("Expected structured observation JSON before its images.");
			const projected = JSON.parse(text.text) as {
				references: Array<{
					sourceId: string;
					referenceId: string;
					revision: string;
					content: Record<string, unknown>;
				}>;
			};
			expect(
				projected.references.slice(0, 2).map(({ sourceId, referenceId, revision }) => ({
					sourceId,
					referenceId,
					revision,
				})),
			).toEqual([
				{ sourceId: "host-observation", referenceId: "scene", revision: `scene-${index + 1}` },
				{ sourceId: "host-observation", referenceId: "bev", revision: `bev-${index + 1}` },
			]);
			expect(projected.references[2]?.content).toMatchObject({
				views: [
					{ viewId: "optional-scene-view", status: "selected" },
					{ viewId: "required-bev-view", status: "selected" },
				],
			});
		}
		expect(providerContexts[1]?.messages.map((message) => message.role)).toEqual([
			"user",
			"user",
			"assistant",
			"toolResult",
		]);

		const internalContexts = events.flatMap((event) => {
			if (event.data.type !== "item.completed" || event.data.item.itemType !== "message") return [];
			return "context" in event.data.item ? [event.data.item] : [];
		});
		expect(internalContexts).toHaveLength(2);
		expect(internalContexts.map((item) => item.content.map(({ type }) => type))).toEqual([
			["text", "image-reference", "image-reference"],
			["text", "image-reference", "image-reference"],
		]);
		expect(requests).toHaveLength(2);
		expect(requests.map((request) => request.sources[0]?.kind)).toEqual(["observation", "observation"]);
		expect(requests.map((request) => request.images.length)).toEqual([2, 2]);
		const durable = await transcriptBinding.transcript.restore({
			sessionId: INPUT.sessionId,
			actorId: INPUT.actorId,
		});
		const durableSurfaces = JSON.stringify({ events: internalContexts, provenance: requests, transcript: durable });
		for (const [index, frame] of frames.entries()) {
			for (const bytes of frame) expect(durableSurfaces).not.toContain(bytes.toString("base64"));
			expect(durableSurfaces).not.toContain(`private-scene-label-${index + 1}`);
			expect(durableSurfaces).not.toContain(`private-bev-label-${index + 1}`);
		}
	});

	it("passes host-authored scene and calibrated BEV JSON without media limits or interpretation", async () => {
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>();
		const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>();
		const contextLimits = { maxReferencesPerSnapshot: 3, maxSnapshotBytes: 8_192 } as const;
		const source = createVisualObservationContextSource({
			sourceId: "structured-observation",
			selectionReferenceId: "structured-selection",
			capture: () => ({
				observationVersion: "1",
				revision: "structured-1",
				references: [
					{
						referenceId: "scene",
						revision: "scene-1",
						content: { kind: "scene-observation", observedBy: "game-host", entities: [{ id: "tree" }] },
					},
					{
						referenceId: "bev",
						revision: "bev-1",
						content: {
							kind: "calibrated-bev",
							sourceFrame: "camera",
							targetFrame: "world",
							units: "meters",
						},
					},
				],
				candidates: [],
			}),
			authorize,
			store: { read, publishCanonicalPng },
			projectionLimits: contextLimits,
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});
		const providerContexts: Context[] = [];
		const { agent } = createAgent(
			(_model, context) => {
				providerContexts.push(copyContext(context));
				return textResponse("done");
			},
			{
				contextSources: [source],
				contextLimits,
			},
		);

		await expect(agent.start(INPUT, "run-structured").completion).resolves.toMatchObject({ status: "completed" });

		const message = providerContexts[0]?.messages[0];
		if (message?.role !== "user" || typeof message.content !== "string") {
			throw new Error("Expected a structured observation message.");
		}
		const projected = JSON.parse(message.content) as {
			references: Array<{ referenceId: string; content: Record<string, unknown> }>;
		};
		expect(projected.references.slice(0, 2)).toEqual([
			{
				sourceId: "structured-observation",
				referenceId: "scene",
				revision: "scene-1",
				content: { kind: "scene-observation", observedBy: "game-host", entities: [{ id: "tree" }] },
			},
			{
				sourceId: "structured-observation",
				referenceId: "bev",
				revision: "bev-1",
				content: {
					kind: "calibrated-bev",
					sourceFrame: "camera",
					targetFrame: "world",
					units: "meters",
				},
			},
		]);
		expect(projected.references[2]).toMatchObject({
			referenceId: "structured-selection",
			content: { views: [] },
		});
		expect(authorize).not.toHaveBeenCalled();
		expect(read).not.toHaveBeenCalled();
		expect(publishCanonicalPng).not.toHaveBeenCalled();
	});

	it("treats an undefined capture as an empty per-request contribution with zero attachment I/O", async () => {
		const capture = vi.fn(() => undefined);
		const authorize = vi.fn<ImageAttachmentAuthorizer>();
		const read = vi.fn<CanonicalPngAttachmentStore["read"]>();
		const publishCanonicalPng = vi.fn<CanonicalPngAttachmentStore["publishCanonicalPng"]>();
		const contextLimits = { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 4_096 } as const;
		const source = createVisualObservationContextSource({
			sourceId: "optional-observation",
			selectionReferenceId: "optional-selection",
			capture,
			authorize,
			store: { read, publishCanonicalPng },
			projectionLimits: contextLimits,
			maxCandidatesPerSnapshot: 1,
			maxTransformCacheEntries: 1,
		});
		const providerContexts: Context[] = [];
		const { agent, events } = createAgent(
			(_model, context) => {
				providerContexts.push(copyContext(context));
				return textResponse("done");
			},
			{
				contextSources: [source],
				contextLimits,
			},
		);

		await expect(agent.start(INPUT, "run-empty-observation").completion).resolves.toMatchObject({
			status: "completed",
		});

		expect(capture).toHaveBeenCalledOnce();
		expect(providerContexts[0]?.messages).toHaveLength(1);
		expect(authorize).not.toHaveBeenCalled();
		expect(read).not.toHaveBeenCalled();
		expect(publishCanonicalPng).not.toHaveBeenCalled();
		expect(
			events.some(
				(event) =>
					event.data.type === "item.completed" &&
					event.data.item.itemType === "message" &&
					"context" in event.data.item,
			),
		).toBe(false);
	});
});

function createAgent(
	modelStream: StreamFn,
	overrides: Partial<GameAgentOptions> = {},
): { readonly agent: GameAgent; readonly events: CanonicalEvent[] } {
	const events: CanonicalEvent[] = [];
	let eventSequence = 0;
	let generatedId = 0;
	const agent = new GameAgent({
		sessionId: INPUT.sessionId,
		actorId: INPUT.actorId,
		model: TEST_MODEL,
		modelStream,
		emitEvent: async (coordinate, data) => {
			eventSequence += 1;
			const ordinal = String(eventSequence);
			const event: CanonicalEvent = {
				protocolVersion: PROTOCOL_VERSION,
				eventId: `event-${ordinal}`,
				sequence: ordinal,
				revision: ordinal,
				occurredAt: "2026-08-30T00:00:00.000Z",
				coordinate: structuredClone(coordinate),
				audience: eventAudience(data),
				data: structuredClone(data),
			};
			events.push(event);
			return structuredClone(event);
		},
		newId: () => `generated-${++generatedId}`,
		now: () => Date.parse("2026-08-30T00:00:00.000Z"),
		...overrides,
	});
	return { agent, events };
}

async function createTranscriptBinding() {
	let recordId = 0;
	const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
		...TRANSCRIPT_TEST_LIMITS,
		newRecordId: () => `transcript-${++recordId}`,
	});
	return {
		transcript,
		restore: await transcript.restore({ sessionId: INPUT.sessionId, actorId: INPUT.actorId }),
	};
}

function copyContext(context: Context): Context {
	return {
		...(context.systemPrompt === undefined ? {} : { systemPrompt: context.systemPrompt }),
		messages: structuredClone(context.messages),
		...(context.tools === undefined ? {} : { tools: [...context.tools] }),
	};
}

function imageReference(bytes: Uint8Array, name?: string): ImageReferenceContent {
	return {
		type: "image-reference",
		attachmentId: `img_${createHash("sha256").update(bytes).digest("hex")}`,
		mediaType: "image/png",
		bytes: bytes.byteLength,
		width: 1,
		height: 1,
		...(name ? { name } : {}),
	};
}

function canonicalPng(red: number, green: number, blue: number): Promise<Buffer> {
	return renderCanonicalPng(
		sharp({ create: { width: 1, height: 1, channels: 3, background: { r: red, g: green, b: blue } } }),
		4_096,
		new AbortController().signal,
	);
}

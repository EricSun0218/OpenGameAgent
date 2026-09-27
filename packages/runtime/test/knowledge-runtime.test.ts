import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { Context } from "@earendil-works/pi-ai";
import {
	type ArtifactLimits,
	GameArtifactService,
	GameKnowledgeService,
	InMemoryArtifactStore,
	type KnowledgeLimits,
} from "@opengameagent/artifacts";
import { describe, expect, it, vi } from "vitest";
import { GameRuntime, InMemoryEventStore } from "../src/index.js";
import {
	allowAllTestTools,
	assistant,
	messageResponse,
	scriptedStream,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
} from "./pi-fakes.js";

const artifactLimits: ArtifactLimits = {
	maxIdentifierBytes: 128,
	maxCounterDigits: 20,
	maxContentTypeBytes: 128,
	maxArtifactBytes: 8192,
	maxArtifactsPerStream: 2,
	maxStoredBytesPerStream: 16_384,
	maxInlineToolResultBytes: 64,
	maxReadBytes: 2048,
	maxModelResultBytes: 4096,
	maxPurgeBatch: 2,
};

const knowledgeLimits: KnowledgeLimits = {
	maxIdentifierBytes: 128,
	maxCounterDigits: 20,
	maxSources: 2,
	maxSourceDescriptionBytes: 256,
	maxToolDescriptionBytes: 1024,
	maxQueryBytes: 1024,
	maxItemsPerQuery: 4,
	maxTitleBytes: 256,
	maxSummaryBytes: 512,
	maxUriBytes: 1024,
	maxMetadataEntries: 4,
	maxMetadataKeyBytes: 64,
	maxMetadataValueBytes: 256,
	maxItemBytes: 6144,
	maxResultBytes: 7168,
	maxJsonDepth: 16,
	maxConcurrentQueriesPerSource: 1,
	maxQueryDurationMs: 1000,
};

const input = { sessionId: "session", actorId: "actor", inputId: "input", payload: { text: "Open the gate." } };

describe("external knowledge through the Pi-backed runtime", () => {
	it("queries a host source, reads an offloaded result, and continues the same Pi Agent loop", async () => {
		const artifacts = new GameArtifactService({
			store: new InMemoryArtifactStore(artifactLimits),
			limits: artifactLimits,
			now: () => "2026-08-29T02:00:00.000Z",
		});
		const query = vi.fn(async () => [
			{
				itemId: "gate-lore",
				title: "Northern gate",
				content: { rule: "Speak the old oath.", evidence: "x".repeat(600) },
			},
		]);
		const knowledge = new GameKnowledgeService({
			sources: [{ sourceId: "world_lore", description: "Authorized world lore.", query }],
			limits: knowledgeLimits,
		});
		const contexts: Context[] = [];
		let artifactId = "";
		const streamFn = scriptedStream(
			[
				() =>
					messageResponse(
						assistant(
							[
								{
									type: "toolCall",
									id: "call-query",
									name: "query_external_knowledge",
									arguments: { sourceId: "world_lore", query: { gate: "north" }, maxItems: 1 },
								},
							],
							"toolUse",
						),
					),
				(context) => {
					const reference = lastToolJson(context);
					expect(reference).toMatchObject({ status: "stored", readTool: "read_game_artifact" });
					if (typeof reference.artifactId !== "string") throw new Error("Expected an artifact identity.");
					artifactId = reference.artifactId;
					return messageResponse(
						assistant(
							[
								{
									type: "toolCall",
									id: "call-read",
									name: "read_game_artifact",
									arguments: { artifactId, maxBytes: artifactLimits.maxReadBytes },
								},
							],
							"toolUse",
						),
					);
				},
				(context) => {
					const page = lastToolJson(context);
					expect(page).toMatchObject({
						status: "ok",
						artifactId,
						content: expect.stringContaining("Speak the old oath."),
					});
					return textResponse("The old oath opens the northern gate.");
				},
			],
			contexts,
		);
		const runtime = await createRuntime(artifacts, knowledge, streamFn);

		const handle = await runtime.submit(input);
		await expect(handle.completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts).toHaveLength(3);
		expect(query).toHaveBeenCalledWith(
			{
				input,
				coordinate: { sessionId: "session", actorId: "actor", runId: expect.any(String), turn: "1" },
				query: { gate: "north" },
				maxItems: 1,
			},
			expect.any(AbortSignal),
		);
	});
});

async function createRuntime(
	artifacts: GameArtifactService,
	knowledge: GameKnowledgeService,
	modelStream: StreamFn,
): Promise<GameRuntime> {
	let eventId = 0;
	const runtime = new GameRuntime(
		new InMemoryEventStore({
			newEventId: () => `event-${++eventId}`,
			now: () => "2026-08-29T02:00:00.000Z",
		}),
		{ maxActiveActors: 1, maxQueuedRuns: 4, maxInputBytes: 4_096, maxInputDepth: 8 },
	);
	await runtime.registerActor({
		sessionId: input.sessionId,
		actorId: input.actorId,
		model: TEST_MODEL,
		modelStream,
		requestSources: [artifacts, knowledge],
		toolPolicy: allowAllTestTools,
		toolLimits: TEST_TOOL_LIMITS,
		newId: () => `agent-${++eventId}`,
		now: () => 1,
	});
	return runtime;
}

function lastToolJson(context: Context): Record<string, unknown> {
	const result = [...context.messages].reverse().find((message) => message.role === "toolResult");
	if (result?.role !== "toolResult") throw new Error("Expected a Tool result.");
	const text = result.content.find((content) => content.type === "text");
	if (text?.type !== "text") throw new Error("Expected Tool text.");
	return JSON.parse(text.text) as Record<string, unknown>;
}

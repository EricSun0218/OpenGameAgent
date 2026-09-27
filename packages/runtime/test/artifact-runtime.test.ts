import type { AgentTool, StreamFn } from "@earendil-works/pi-agent-core";
import type { Context } from "@earendil-works/pi-ai";
import { type ArtifactLimits, GameArtifactService, InMemoryArtifactStore } from "@opengameagent/artifacts";
import { Type } from "typebox";
import { describe, expect, it } from "vitest";
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

const limits: ArtifactLimits = {
	maxIdentifierBytes: 128,
	maxCounterDigits: 20,
	maxContentTypeBytes: 128,
	maxArtifactBytes: 4096,
	maxArtifactsPerStream: 2,
	maxStoredBytesPerStream: 8192,
	maxInlineToolResultBytes: 32,
	maxReadBytes: 128,
	maxModelResultBytes: 1024,
	maxPurgeBatch: 2,
};

const input = { sessionId: "session", actorId: "actor", inputId: "input", payload: { text: "Inspect." } };

describe("durable artifacts through the Pi-backed runtime", () => {
	it("continues from an oversized Tool result through the bounded artifact read Tool", async () => {
		const service = new GameArtifactService({
			store: new InMemoryArtifactStore(limits),
			limits,
			now: () => "2026-08-29T01:00:00.000Z",
		});
		let contentReads = 0;
		let detailReads = 0;
		class InspectTool {
			readonly name = "inspect_world";
			readonly label = "Inspect world";
			readonly description = "Inspect the world.";
			readonly parameters = Type.Object({}, { additionalProperties: false });

			async execute() {
				const result = Object.create(null) as Record<string, unknown>;
				Object.defineProperties(result, {
					content: {
						enumerable: true,
						get() {
							contentReads += 1;
							return [{ type: "text", text: `ancient-map:${"x".repeat(80)}` }];
						},
					},
					details: {
						enumerable: true,
						get() {
							detailReads += 1;
							return { provenance: "game" };
						},
					},
				});
				return result as never;
			}
		}
		const inspect: AgentTool = new InspectTool();
		const contexts: Context[] = [];
		let artifactId = "";
		const streamFn = scriptedStream(
			[
				() =>
					messageResponse(
						assistant([{ type: "toolCall", id: "call-inspect", name: "inspect_world", arguments: {} }], "toolUse"),
					),
				(context) => {
					const reference = lastToolJson(context);
					expect(reference).toMatchObject({ status: "stored", readTool: "read_game_artifact" });
					if (typeof reference.artifactId !== "string") throw new Error("Expected artifact identity.");
					artifactId = reference.artifactId;
					return messageResponse(
						assistant(
							[
								{
									type: "toolCall",
									id: "call-read",
									name: "read_game_artifact",
									arguments: { artifactId, maxBytes: 128 },
								},
							],
							"toolUse",
						),
					);
				},
				(context) => {
					const page = lastToolJson(context);
					expect(page).toMatchObject({ status: "ok", artifactId, content: expect.stringContaining("ancient-map") });
					return textResponse("The ancient map is available.");
				},
			],
			contexts,
		);
		const runtime = await createRuntime(service, inspect, streamFn);

		const handle = await runtime.submit(input);
		await expect(handle.completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts).toHaveLength(3);
		expect(artifactId).toMatch(/^[0-9a-f]{64}$/u);
		expect(contentReads).toBe(1);
		expect(detailReads).toBe(1);
	});
});

async function createRuntime(
	service: GameArtifactService,
	inspect: AgentTool,
	modelStream: StreamFn,
): Promise<GameRuntime> {
	let eventId = 0;
	const runtime = new GameRuntime(
		new InMemoryEventStore({
			newEventId: () => `event-${++eventId}`,
			now: () => "2026-08-29T01:00:00.000Z",
		}),
		{ maxActiveActors: 1, maxQueuedRuns: 4, maxInputBytes: 4_096, maxInputDepth: 8 },
	);
	await runtime.registerActor({
		sessionId: input.sessionId,
		actorId: input.actorId,
		model: TEST_MODEL,
		modelStream,
		toolSource: () => [inspect],
		requestSources: [service],
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

import type { StreamFn } from "@earendil-works/pi-agent-core";
import type { Context } from "@earendil-works/pi-ai";
import { GameInteractionService, type InteractionLimits, type InteractionRequest } from "@opengameagent/extensions";
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

const limits: InteractionLimits = {
	maxIdentifierBytes: 128,
	maxCounterDigits: 20,
	maxQuestionBytes: 1024,
	maxHeaderBytes: 128,
	maxDetailBytes: 4096,
	maxOptionLabelBytes: 256,
	maxOptionDescriptionBytes: 1024,
	maxCustomAnswerBytes: 2048,
	maxQuestionsPerRequest: 3,
	maxOptionsPerQuestion: 3,
	maxRequestBytes: 8192,
	maxResponseBytes: 4096,
	maxPendingRequests: 1,
};

const input = { sessionId: "session", actorId: "actor", inputId: "input", payload: { text: "Help me choose." } };

describe("structured player interaction through the Pi-backed runtime", () => {
	it("binds the UI request to the exact Pi turn and continues with the answer as a Tool result", async () => {
		let request: InteractionRequest | undefined;
		const service = new GameInteractionService({
			limits,
			broker: {
				async ask(value) {
					request = value;
					return {
						status: "answered",
						answers: [{ questionId: "route", selectedOptionIds: ["safe"] }],
					};
				},
			},
		});
		const contexts: Context[] = [];
		const streamFn = scriptedStream(
			[
				() =>
					messageResponse(
						assistant(
							[
								{
									type: "toolCall",
									id: "call-ask",
									name: "ask_player",
									arguments: {
										questions: [
											{
												questionId: "route",
												question: "Which road?",
												options: [
													{ optionId: "safe", label: "Safe", recommended: true },
													{ optionId: "fast", label: "Fast" },
												],
												allowCustomAnswer: false,
											},
										],
									},
								},
							],
							"toolUse",
						),
					),
				(context) => {
					const toolResultText = context.messages
						.filter((message) => message.role === "toolResult")
						.flatMap((message) => message.content)
						.filter((content) => content.type === "text")
						.map((content) => content.text)
						.join("\n");
					expect(toolResultText).toContain('"selectedOptionIds":["safe"]');
					return textResponse("We will take the safe road.");
				},
			],
			contexts,
		);
		const runtime = await createRuntime(service, streamFn);

		const handle = await runtime.submit(input);
		await expect(handle.completion).resolves.toMatchObject({ status: "completed" });
		expect(request).toMatchObject({
			coordinate: { sessionId: "session", actorId: "actor", runId: handle.runId, turn: "1" },
			inputId: "input",
			toolCallId: "call-ask",
		});
		expect(contexts).toHaveLength(2);
	});

	it("aborts an unanswered interaction with its owning run even when the UI promise never settles", async () => {
		let started: (() => void) | undefined;
		const brokerStarted = new Promise<void>((resolve) => {
			started = resolve;
		});
		let brokerSignal: AbortSignal | undefined;
		const service = new GameInteractionService({
			limits,
			broker: {
				async ask(_request, signal) {
					brokerSignal = signal;
					started?.();
					return await new Promise<never>(() => undefined);
				},
			},
		});
		const modelStream: StreamFn = () =>
			messageResponse(
				assistant(
					[
						{
							type: "toolCall",
							id: "call-ask",
							name: "ask_player",
							arguments: { questions: [{ questionId: "route", question: "Which road?" }] },
						},
					],
					"toolUse",
				),
			);
		const runtime = await createRuntime(service, modelStream);

		const handle = await runtime.submit(input);
		await brokerStarted;
		const coordinate = runtime.readActiveCoordinate(input);
		if (!coordinate) throw new Error("Expected an active interaction run.");
		expect(coordinate.turn).toBe("1");
		await expect(runtime.control(coordinate, { type: "abort" })).resolves.toMatchObject({ accepted: true });
		await expect(handle.completion).resolves.toMatchObject({ status: "aborted" });
		expect(brokerSignal?.aborted).toBe(true);
	});
});

async function createRuntime(service: GameInteractionService, modelStream: StreamFn): Promise<GameRuntime> {
	let eventId = 0;
	const runtime = new GameRuntime(
		new InMemoryEventStore({
			newEventId: () => `event-${++eventId}`,
			now: () => "2026-08-29T00:00:00.000Z",
		}),
		{ maxActiveActors: 1, maxQueuedRuns: 4, maxInputBytes: 4_096, maxInputDepth: 8 },
	);
	await runtime.registerActor({
		sessionId: input.sessionId,
		actorId: input.actorId,
		model: TEST_MODEL,
		modelStream,
		toolSource: (activeInput, signal, coordinate) => service.createTools(activeInput, signal, coordinate),
		toolPolicy: allowAllTestTools,
		toolLimits: TEST_TOOL_LIMITS,
		newId: () => `agent-${++eventId}`,
		now: () => 1,
	});
	return runtime;
}

import type { GameInput, GameToolExecutionContext, JsonObject } from "@opengameagent/protocol";
import { preflightGameToolSchema } from "@opengameagent/runtime";
import { describe, expect, it } from "vitest";
import {
	createStructuredGameInteractionToolProvider,
	type GameInteractionEvent,
	type GameInteractionRequest,
} from "./structured-interaction.js";

const input: GameInput = {
	id: "input-1",
	type: "npc.chat",
	session: {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 2,
		ownerId: "owner",
		sessionId: "session",
		actorId: "actor",
	},
	moment: { tick: 12.5 },
	content: [{ type: "text", text: "Help me choose." }],
};

function context(turn = 1, signal = new AbortController().signal): GameToolExecutionContext {
	return { input, runId: "run-1", turn, toolCallIndex: 0, signal };
}

function questions(recommended = true): JsonObject {
	return {
		questions: [
			{
				id: "route",
				prompt: "Which route should we take?",
				options: [
					{ id: "safe", label: "Safe road", description: "Slower but guarded.", recommended },
					{ id: "fast", label: "Fast road", description: "Faster but exposed." },
				],
				allowCustomAnswer: false,
			},
		],
	};
}

describe("structured game interaction", () => {
	it("publishes a bounded recommended choice and returns a validated host answer", async () => {
		let request: GameInteractionRequest | undefined;
		const events: GameInteractionEvent[] = [];
		const provider = createStructuredGameInteractionToolProvider({
			broker: {
				async prompt(value) {
					request = value;
					return {
						cancelled: false,
						answers: [{ questionId: "route", selectedOptionIds: ["safe"] }],
					};
				},
			},
			onEvent(event) {
				events.push(event);
			},
		});
		const [tool] = await provider.provide(input, new AbortController().signal);
		if (!tool) throw new Error("Expected interaction tool.");
		expect(() => preflightGameToolSchema(tool.definition)).not.toThrow();
		const result = await tool.execute({ id: "call-1", name: "ask_player", arguments: questions() }, context());

		expect(tool.definition).toMatchObject({ executionMode: "sequential", risk: "medium" });
		expect(request).toMatchObject({
			inputId: input.id,
			runId: "run-1",
			turn: 1,
			questions: [
				{
					id: "route",
					multiSelect: false,
					allowCustomAnswer: false,
					options: [expect.objectContaining({ id: "safe", recommended: true }), expect.anything()],
				},
			],
		});
		expect(events.map((event) => event.type)).toEqual(["interaction.started", "interaction.completed"]);
		expect(result).toMatchObject({
			content: [
				{
					type: "json",
					value: {
						cancelled: false,
						answers: [{ questionId: "route", selectedOptionIds: ["safe"] }],
					},
				},
			],
		});
	});

	it("derives stable request ids from trusted run coordinates", async () => {
		const ids: string[] = [];
		const provider = createStructuredGameInteractionToolProvider({
			broker: {
				async prompt(request) {
					ids.push(request.requestId);
					return { cancelled: true };
				},
			},
		});
		const [tool] = await provider.provide(input, new AbortController().signal);
		if (!tool) throw new Error("Expected interaction tool.");
		const call = { id: "call-1", name: "ask_player", arguments: questions() };
		await tool.execute(call, context(1));
		await tool.execute(call, context(1));
		await tool.execute(call, context(2));
		expect(ids[0]).toBe(ids[1]);
		expect(ids[2]).not.toBe(ids[0]);
	});

	it("rejects ambiguous recommendations and malformed host answers", async () => {
		let brokerCalls = 0;
		const provider = createStructuredGameInteractionToolProvider({
			broker: {
				async prompt() {
					brokerCalls += 1;
					return { cancelled: false, answers: [{ questionId: "route", selectedOptionIds: ["unknown"] }] };
				},
			},
		});
		const [tool] = await provider.provide(input, new AbortController().signal);
		if (!tool) throw new Error("Expected interaction tool.");
		const ambiguous = questions();
		const list = ambiguous["questions"] as JsonObject[];
		const options = list[0]?.["options"] as JsonObject[];
		if (options[1]) options[1]["recommended"] = true;
		const invalidRequest = await tool.execute({ id: "call-1", name: "ask_player", arguments: ambiguous }, context());
		expect(invalidRequest.isError).toBe(true);
		expect(brokerCalls).toBe(0);

		const invalidResponse = await tool.execute({ id: "call-2", name: "ask_player", arguments: questions() }, context());
		expect(invalidResponse).toMatchObject({ isError: true, content: [{ type: "json" }] });
		expect(JSON.stringify(invalidResponse)).not.toContain("unknown");
	});

	it("propagates run cancellation without converting it into a model-visible tool error", async () => {
		const controller = new AbortController();
		const provider = createStructuredGameInteractionToolProvider({
			broker: {
				async prompt(_request, signal) {
					controller.abort(new Error("cancelled by host"));
					signal.throwIfAborted();
					return { cancelled: true };
				},
			},
		});
		const [tool] = await provider.provide(input, controller.signal);
		if (!tool) throw new Error("Expected interaction tool.");
		await expect(
			tool.execute({ id: "call", name: "ask_player", arguments: questions() }, context(1, controller.signal)),
		).rejects.toThrow("cancelled by host");
	});
});

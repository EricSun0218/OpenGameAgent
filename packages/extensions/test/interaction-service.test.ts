import {
	GameInteractionService,
	InteractionBrokerError,
	InteractionBrokerResponseError,
	InteractionCapacityError,
	InteractionConflictError,
	InteractionValidationError,
	type InteractionLimits,
	type InteractionRequest,
} from "@opengameagent/extensions";
import { describe, expect, it, vi } from "vitest";

const limits: InteractionLimits = {
	maxIdentifierBytes: 128,
	maxCounterDigits: 20,
	maxQuestionBytes: 1024,
	maxHeaderBytes: 128,
	maxDetailBytes: 4096,
	maxOptionLabelBytes: 256,
	maxOptionDescriptionBytes: 1024,
	maxCustomAnswerBytes: 2048,
	maxQuestionsPerRequest: 4,
	maxOptionsPerQuestion: 5,
	maxRequestBytes: 8192,
	maxResponseBytes: 4096,
	maxPendingRequests: 2,
};

const input = {
	sessionId: "session",
	actorId: "actor",
	inputId: "input",
	payload: { type: "chat" },
} as const;

const coordinate = { sessionId: "session", actorId: "actor", runId: "run", turn: "1" } as const;

describe("GameInteractionService", () => {
	it("freezes explicit limits and rejects a response budget below the derived minimum shape", () => {
		const configured = { ...limits };
		const service = new GameInteractionService({
			limits: configured,
			broker: {
				async ask() {
					return { status: "cancelled" };
				},
			},
		});
		configured.maxPendingRequests = 99;
		expect(service.limits.maxPendingRequests).toBe(limits.maxPendingRequests);
		expect(
			() =>
				new GameInteractionService({
					limits: { ...limits, maxResponseBytes: 1 },
					broker: {
						async ask() {
							return { status: "cancelled" };
						},
					},
				}),
		).toThrow(InteractionValidationError);
	});

	it("projects one recommended choice and validated answers through an ordinary sequential Pi Tool", async () => {
		let request: InteractionRequest | undefined;
		const service = new GameInteractionService({
			limits,
			broker: {
				async ask(value) {
					request = value;
					return {
						status: "answered",
						answers: [
							{ questionId: "note", selectedOptionIds: [], customAnswer: "Leave before dawn." },
							{ questionId: "route", selectedOptionIds: ["safe"] },
						],
					};
				},
			},
		});
		const [tool] = service.createTools(input, new AbortController().signal, coordinate);
		if (!tool) throw new Error("Expected an interaction Tool.");
		const result = await tool.execute(
			"call",
			{
				questions: [
					{
						questionId: "route",
						question: "Which road?",
						options: [
							{ optionId: "safe", label: "Safe road", description: "Guarded", recommended: true },
							{ optionId: "fast", label: "Fast road", description: "Exposed" },
						],
						allowCustomAnswer: false,
					},
					{ questionId: "note", question: "Any instruction?" },
				],
			},
			new AbortController().signal,
		);

		expect(tool.executionMode).toBe("sequential");
		expect(request).toMatchObject({
			requestId: expect.stringMatching(/^[0-9a-f]{64}$/u),
			requestFingerprint: expect.stringMatching(/^[0-9a-f]{64}$/u),
			coordinate,
			inputId: "input",
			toolCallId: "call",
			questions: [
				{
					questionId: "route",
					options: expect.arrayContaining([expect.objectContaining({ optionId: "safe", recommended: true })]),
				},
				{ questionId: "note", options: [], allowCustomAnswer: true },
			],
		});
		expect(JSON.parse(result.content[0]?.type === "text" ? result.content[0].text : "null")).toEqual({
			requestId: request?.requestId,
			status: "answered",
			answers: [
				{ questionId: "route", selectedOptionIds: ["safe"] },
				{ questionId: "note", selectedOptionIds: [], customAnswer: "Leave before dawn." },
			],
		});
	});

	it("enforces host byte/count limits and rejects ambiguous recommendations before the broker", async () => {
		const ask = vi.fn(async () => ({ status: "cancelled" }));
		const service = new GameInteractionService({ broker: { ask }, limits: { ...limits, maxQuestionBytes: 3 } });
		const [tool] = service.createTools(input, new AbortController().signal, coordinate);
		if (!tool) throw new Error("Expected an interaction Tool.");

		await expect(
			tool.execute("call", {
				questions: [{ questionId: "q", question: "界a" }],
			}),
		).rejects.toBeInstanceOf(InteractionValidationError);
		await expect(
			tool.execute("call", {
				questions: [
					{
						questionId: "q",
						question: "界",
						options: [
							{ optionId: "a", label: "A", recommended: true },
							{ optionId: "b", label: "B", recommended: true },
						],
					},
				],
			}),
		).rejects.toBeInstanceOf(InteractionValidationError);
		expect(ask).not.toHaveBeenCalled();
	});

	it("normalizes answer order and hides invalid broker content behind a stable failure", async () => {
		const service = new GameInteractionService({
			limits,
			broker: {
				async ask() {
					return {
						status: "answered",
						answers: [
							{ questionId: "second", selectedOptionIds: ["b"] },
							{ questionId: "first", selectedOptionIds: ["secret-invalid-option"] },
						],
					};
				},
			},
		});
		const [tool] = service.createTools(input, new AbortController().signal, coordinate);
		if (!tool) throw new Error("Expected an interaction Tool.");
		const execution = tool.execute("call", {
			questions: [
				{
					questionId: "first",
					question: "First?",
					options: [{ optionId: "a", label: "A" }],
					allowCustomAnswer: false,
				},
				{
					questionId: "second",
					question: "Second?",
					options: [{ optionId: "b", label: "B" }],
					allowCustomAnswer: false,
				},
			],
		});
		const error = await execution.catch((reason: unknown) => reason);
		expect(error).toBeInstanceOf(InteractionBrokerResponseError);
		expect(String(error)).not.toContain("secret-invalid-option");
	});

	it("rejects accessor-bearing broker data without invoking it", async () => {
		let accessed = false;
		const response = Object.create(null) as Record<string, unknown>;
		Object.defineProperty(response, "status", {
			enumerable: true,
			get() {
				accessed = true;
				return "cancelled";
			},
		});
		const service = new GameInteractionService({
			limits,
			broker: {
				async ask() {
					return response;
				},
			},
		});
		const [tool] = service.createTools(input, new AbortController().signal, coordinate);
		if (!tool) throw new Error("Expected an interaction Tool.");
		await expect(
			tool.execute("call", { questions: [{ questionId: "q", question: "Choose?" }] }),
		).rejects.toBeInstanceOf(InteractionBrokerResponseError);
		expect(accessed).toBe(false);
	});

	it("does not expose a broker failure message to the model-facing Tool error", async () => {
		const service = new GameInteractionService({
			limits,
			broker: {
				async ask() {
					throw new Error("private-ui-token");
				},
			},
		});
		const [tool] = service.createTools(input, new AbortController().signal, coordinate);
		if (!tool) throw new Error("Expected an interaction Tool.");
		const error = await tool
			.execute("call", { questions: [{ questionId: "q", question: "Choose?" }] })
			.catch((reason: unknown) => reason);
		expect(error).toBeInstanceOf(InteractionBrokerError);
		expect(String(error)).not.toContain("private-ui-token");
	});

	it("coalesces an identical active request and fences conflict and capacity races", async () => {
		let release: ((value: unknown) => void) | undefined;
		const ask = vi.fn(
			() =>
				new Promise<unknown>((resolve) => {
					release = resolve;
				}),
		);
		const service = new GameInteractionService({
			limits: { ...limits, maxPendingRequests: 1 },
			broker: { ask },
		});
		const [tool] = service.createTools(input, new AbortController().signal, coordinate);
		if (!tool) throw new Error("Expected an interaction Tool.");
		const first = tool.execute("same", { questions: [{ questionId: "q", question: "Choose?" }] });
		const duplicate = tool.execute("same", { questions: [{ questionId: "q", question: "Choose?" }] });
		await vi.waitFor(() => expect(ask).toHaveBeenCalledTimes(1));

		await expect(
			tool.execute("same", { questions: [{ questionId: "q", question: "Different?" }] }),
		).rejects.toBeInstanceOf(InteractionConflictError);
		await expect(
			tool.execute("other", { questions: [{ questionId: "q", question: "Choose?" }] }),
		).rejects.toBeInstanceOf(InteractionCapacityError);

		release?.({ status: "answered", answers: [{ questionId: "q", selectedOptionIds: [], customAnswer: "yes" }] });
		await expect(Promise.all([first, duplicate])).resolves.toHaveLength(2);
	});

	it("stops waiting on run abort even when the broker ignores cancellation and releases capacity", async () => {
		let calls = 0;
		const service = new GameInteractionService({
			limits: { ...limits, maxPendingRequests: 1 },
			broker: {
				async ask() {
					calls += 1;
					if (calls === 1) return await new Promise<never>(() => undefined);
					return { status: "cancelled" };
				},
			},
		});
		const controller = new AbortController();
		const [tool] = service.createTools(input, controller.signal, coordinate);
		if (!tool) throw new Error("Expected an interaction Tool.");
		const pending = tool.execute("first", { questions: [{ questionId: "q", question: "Choose?" }] }, controller.signal);
		await vi.waitFor(() => expect(calls).toBe(1));
		controller.abort(new Error("run aborted"));
		await expect(pending).rejects.toThrow("run aborted");

		const nextController = new AbortController();
		const [nextTool] = service.createTools({ ...input, inputId: "next" }, nextController.signal, {
			...coordinate,
			runId: "next-run",
		});
		if (!nextTool) throw new Error("Expected another interaction Tool.");
		await expect(
			nextTool.execute("second", { questions: [{ questionId: "q", question: "Choose?" }] }, nextController.signal),
		).resolves.toMatchObject({ details: { status: "cancelled" } });
	});
});

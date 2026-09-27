import { createHash } from "node:crypto";
import type { AgentTool, StreamFn } from "@earendil-works/pi-agent-core";
import type { AssistantMessage, Context } from "@earendil-works/pi-ai";
import { type CanonicalEvent, eventAudience, PROTOCOL_VERSION } from "@opengameagent/protocol";
import { Type } from "typebox";
import { describe, expect, it, vi } from "vitest";
import {
	allowAllTestTools,
	assistant,
	deferredVoid,
	messageResponse,
	scriptedStream,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
	tool,
	toolCall,
} from "../../devtools/src/pi-fakes.js";
import { captureExactToolRepeatPolicy } from "../src/exact-tool-repeat.js";
import {
	createGameProvenanceAlias,
	DURABLE_TOOL_EXECUTION,
	type DurableExecutionAgentTool,
	DurableToolExecutionUnresolvedError,
	DurableTranscript,
	exactToolRepeatAdvisoryMessage,
	fingerprintGameModelContext,
	GameAgent,
	type GameAgentOptions,
	type GameModelRequestProvenance,
	InMemoryTranscriptStore,
} from "../src/index.js";

const INPUT = {
	sessionId: "session",
	actorId: "actor",
	inputId: "input",
	payload: { instruction: "inspect" },
} as const;

const REPEAT_POLICY = {
	advisoryThreshold: 2,
	terminationThreshold: 3,
} as const;

function exactCall(id: string, argumentsValue: Record<string, unknown>): AssistantMessage["content"][number] {
	return { type: "toolCall", id, name: "inspect", arguments: argumentsValue };
}

function inspectTool(execute: AgentTool["execute"]): AgentTool {
	return {
		name: "inspect",
		label: "inspect",
		description: "Inspect deterministic state",
		parameters: Type.Object({ a: Type.Number(), b: Type.Number() }, { additionalProperties: false }),
		prepareArguments: (argumentsValue) => {
			if (
				typeof argumentsValue === "object" &&
				argumentsValue !== null &&
				"x" in argumentsValue &&
				typeof argumentsValue.x === "number"
			) {
				return { a: argumentsValue.x, b: 2 };
			}
			return argumentsValue as { a: number; b: number };
		},
		execute,
	};
}

function createAgent(
	modelStream: StreamFn,
	tools: readonly AgentTool[],
	overrides: Partial<Pick<GameAgentOptions, "exactToolRepeatPolicy" | "providerProvenance" | "transcript">> = {},
	eventHook?: (
		agent: GameAgent,
		coordinate: CanonicalEvent["coordinate"],
		data: CanonicalEvent["data"],
		event: CanonicalEvent,
	) => void | CanonicalEvent | Promise<void> | Promise<CanonicalEvent | undefined>,
): { readonly agent: GameAgent; readonly events: CanonicalEvent[] } {
	const events: CanonicalEvent[] = [];
	let sequence = 0;
	let agent: GameAgent;
	agent = new GameAgent({
		sessionId: INPUT.sessionId,
		actorId: INPUT.actorId,
		model: TEST_MODEL,
		modelStream,
		toolSource: () => tools,
		toolPolicy: allowAllTestTools,
		toolLimits: TEST_TOOL_LIMITS,
		exactToolRepeatPolicy: REPEAT_POLICY,
		...overrides,
		emitEvent: async (coordinate, data) => {
			sequence += 1;
			const ordinal = String(sequence);
			const event: CanonicalEvent = {
				protocolVersion: PROTOCOL_VERSION,
				eventId: `event-${ordinal}`,
				sequence: ordinal,
				revision: ordinal,
				occurredAt: "2026-09-01T00:00:00.000Z",
				coordinate: structuredClone(coordinate),
				audience: eventAudience(data),
				data: structuredClone(data),
			};
			const acknowledgment = await eventHook?.(agent, coordinate, data, event);
			events.push(event);
			return structuredClone(acknowledgment ?? event);
		},
		newId: () => `generated-${sequence + 1}`,
		now: () => 1,
	});
	return {
		agent,
		events,
	};
}

function policyMessages(context: Context): Array<Record<string, unknown>> {
	return context.messages.flatMap((message) => {
		if (message.role !== "user" || typeof message.content !== "string") return [];
		try {
			const parsed: unknown = JSON.parse(message.content);
			return typeof parsed === "object" &&
				parsed !== null &&
				!Array.isArray(parsed) &&
				(parsed as Record<string, unknown>).kind === "game-agent-policy"
				? [parsed as Record<string, unknown>]
				: [];
		} catch {
			return [];
		}
	});
}

describe("exact prepared Tool-repeat policy", () => {
	it("canonicalizes prepared arguments, advises the next request, and terminates before the repeated dispatch", async () => {
		const contexts: Context[] = [];
		const execute = vi.fn(async () => ({ content: [{ type: "text" as const, text: "same" }], details: {} }));
		const stream = scriptedStream(
			[
				() => messageResponse(assistant([exactCall("call-1", { x: 1 })], "toolUse")),
				() => messageResponse(assistant([exactCall("call-2", { b: 2, a: 1.0 })], "toolUse")),
				() => messageResponse(assistant([exactCall("call-3", { a: 1, b: 2 })], "toolUse")),
				() => textResponse("must not be requested"),
			],
			contexts,
		);
		const { agent, events } = createAgent(stream, [inspectTool(execute)]);

		await expect(agent.start(INPUT, "run-repeat").completion).resolves.toEqual({
			runId: "run-repeat",
			status: "completed",
		});

		expect(contexts).toHaveLength(3);
		expect(execute).toHaveBeenCalledTimes(2);
		expect(policyMessages(contexts[0] ?? { messages: [] })).toEqual([]);
		expect(policyMessages(contexts[1] ?? { messages: [] })).toEqual([]);
		expect(policyMessages(contexts[2] ?? { messages: [] })).toContainEqual({
			kind: "game-agent-policy",
			policy: "exact-tool-repeat",
			action: "advisory",
			toolName: "inspect",
			consecutiveCount: "2",
			instruction: "Reassess the plan before repeating this prepared Tool call.",
		});
		expect(events.filter((event) => event.data.type === "tool.repeat.detected").map((event) => event.data)).toEqual([
			{
				type: "tool.repeat.detected",
				toolCallId: "call-2",
				toolName: "inspect",
				consecutiveCount: "2",
				action: "advisory",
			},
			{
				type: "tool.repeat.detected",
				toolCallId: "call-3",
				toolName: "inspect",
				consecutiveCount: "3",
				action: "terminated",
			},
		]);
		const blocked = events.find(
			(event) =>
				event.data.type === "item.completed" &&
				event.data.item.itemType === "tool-result" &&
				event.data.item.toolCallId === "call-3",
		);
		expect(blocked?.data).toMatchObject({
			type: "item.completed",
			item: { itemType: "tool-result", isError: true },
		});

		await expect(
			agent.start({ ...INPUT, inputId: "second-input" }, "run-after-repeat").completion,
		).resolves.toMatchObject({ status: "completed" });
		expect(contexts).toHaveLength(4);
		expect(policyMessages(contexts[3] ?? { messages: [] })).toEqual([]);
	});

	it("binds a transient advisory to reconstructable provider provenance", async () => {
		let transcriptRecord = 0;
		const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
			newRecordId: () => `transcript-${++transcriptRecord}`,
		});
		const transcriptBinding = {
			transcript,
			restore: await transcript.restore({ sessionId: INPUT.sessionId, actorId: INPUT.actorId }),
		};
		const requests: GameModelRequestProvenance[] = [];
		const contexts: Context[] = [];
		const execute = vi.fn(async () => ({ content: [{ type: "text" as const, text: "same" }], details: {} }));
		const { agent, events } = createAgent(
			scriptedStream(
				[
					() => messageResponse(assistant([toolCall("call-1", "inspect")], "toolUse")),
					() => messageResponse(assistant([toolCall("call-2", "inspect")], "toolUse")),
					() => textResponse("done"),
				],
				contexts,
			),
			[tool("inspect", execute)],
			{
				exactToolRepeatPolicy: { advisoryThreshold: 2, terminationThreshold: 3 },
				transcript: transcriptBinding,
				providerProvenance: {
					scopeId: "repeat-provenance",
					sink: {
						recordRequest: async (request) => {
							requests.push(structuredClone(request));
							return createGameProvenanceAlias("record", `request-${requests.length}`);
						},
						recordResponse: async () => undefined,
					},
				},
			},
		);

		await expect(agent.start(INPUT, "run-provenance").completion).resolves.toMatchObject({ status: "completed" });

		const request = requests[2];
		const providerContext = contexts[2];
		const evidence = request?.policyContext?.exactToolRepeat;
		if (!request || !providerContext || !evidence) throw new Error("Expected advisory request provenance.");
		expect(requests.slice(0, 2).every((value) => value.policyContext === undefined)).toBe(true);
		expect(request.lineage.exactToolRepeatPolicy).toEqual(
			expect.objectContaining({ contentFingerprint: expect.stringMatching(/^[0-9a-f]{64}$/u) }),
		);
		const rawEvent = events.find((event) => createGameProvenanceAlias("event", event.eventId) === evidence.eventId);
		if (rawEvent?.data.type !== "tool.repeat.detected") throw new Error("Expected linked repeat event.");
		expect(evidence).toMatchObject({
			eventRevision: rawEvent.revision,
			toolName: createGameProvenanceAlias("tool", rawEvent.data.toolName),
			consecutiveCount: rawEvent.data.consecutiveCount,
		});
		const advisory = exactToolRepeatAdvisoryMessage(rawEvent.data.toolName, rawEvent.data.consecutiveCount);
		expect(createHash("sha256").update(advisory).digest("hex")).toBe(evidence.contentFingerprint);
		const restored = await transcript.materializeRevision(
			{ sessionId: INPUT.sessionId, actorId: INPUT.actorId },
			request.transcript.revision,
		);
		const restoredMessages = restored.messages as Context["messages"];
		const reconstructed: Context = {
			...providerContext,
			messages: [...restoredMessages, { role: "user", content: advisory, timestamp: 1 }],
		};
		expect(fingerprintGameModelContext(reconstructed)).toBe(request.modelContextFingerprint);
		expect(fingerprintGameModelContext(providerContext)).toBe(request.modelContextFingerprint);
	});

	it("keeps an explicitly exempt polling Tool outside the tracked sequence", async () => {
		const inspect = vi.fn(async () => ({ content: [{ type: "text" as const, text: "inspect" }], details: {} }));
		const poll = vi.fn(async () => ({ content: [{ type: "text" as const, text: "poll" }], details: {} }));
		const stream = scriptedStream([
			() => messageResponse(assistant([toolCall("inspect-1", "inspect")], "toolUse")),
			() => messageResponse(assistant([toolCall("poll-1", "poll")], "toolUse")),
			() => messageResponse(assistant([toolCall("inspect-2", "inspect")], "toolUse")),
			() => textResponse("must not be requested"),
		]);
		const { agent, events } = createAgent(stream, [tool("inspect", inspect), tool("poll", poll)], {
			exactToolRepeatPolicy: {
				advisoryThreshold: 1,
				terminationThreshold: 2,
				exemptToolNames: ["poll"],
			},
		});

		await expect(agent.start(INPUT, "run-poll").completion).resolves.toMatchObject({ status: "completed" });

		expect(inspect).toHaveBeenCalledTimes(1);
		expect(poll).toHaveBeenCalledTimes(1);
		expect(events.flatMap((event) => (event.data.type === "tool.repeat.detected" ? [event.data.action] : []))).toEqual([
			"advisory",
			"terminated",
		]);
	});

	it("blocks every later call in the same batch after termination", async () => {
		const execute = vi.fn(async () => ({ content: [{ type: "text" as const, text: "ran" }], details: {} }));
		const repeated = [1, 2, 3, 4].map((index) => toolCall(`call-${index}`, "inspect"));
		const { agent } = createAgent(
			scriptedStream([
				() => messageResponse(assistant(repeated, "toolUse")),
				() => textResponse("must not be requested"),
			]),
			[tool("inspect", execute)],
		);

		await expect(agent.start(INPUT, "run-batch").completion).resolves.toMatchObject({ status: "completed" });

		expect(execute).toHaveBeenCalledTimes(2);
	});

	it("records a terminated durable call as not started without preparing or dispatching its Action", async () => {
		const store = new InMemoryTranscriptStore();
		let transcriptRecord = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
			newRecordId: () => `transcript-${++transcriptRecord}`,
		});
		const preparedOperations: string[] = [];
		const dispatchedActions: string[] = [];
		const durable: DurableExecutionAgentTool = {
			...tool("write", async () => {
				throw new Error("The prepared durable execution must own the Action.");
			}),
			[DURABLE_TOOL_EXECUTION]: async (toolCallId) => {
				preparedOperations.push(toolCallId);
				return {
					operationId: `operation-${toolCallId}`,
					execute: async () => {
						dispatchedActions.push(toolCallId);
						return { content: [{ type: "text" as const, text: "committed" }], details: {} };
					},
				};
			},
		};
		const { agent } = createAgent(
			scriptedStream([
				() => messageResponse(assistant([toolCall("call-1", "write")], "toolUse")),
				() => messageResponse(assistant([toolCall("call-2", "write")], "toolUse")),
			]),
			[durable],
			{
				exactToolRepeatPolicy: { advisoryThreshold: 1, terminationThreshold: 2 },
				transcript: {
					transcript,
					restore: await transcript.restore({ sessionId: INPUT.sessionId, actorId: INPUT.actorId }),
				},
			},
		);

		await expect(agent.start(INPUT, "run-durable-termination").completion).resolves.toMatchObject({
			status: "completed",
		});

		expect(preparedOperations).toEqual(["call-1"]);
		expect(dispatchedActions).toEqual(["call-1"]);
		const stored = await store.load({ sessionId: INPUT.sessionId, actorId: INPUT.actorId }, 131_072);
		expect(
			stored.records.filter((record) => record.type === "tool.started").map((record) => record.toolCallId),
		).toEqual(["call-1"]);
		expect(stored.records).toContainEqual(
			expect.objectContaining({
				type: "tool.completed",
				outcome: "not_started",
				message: expect.objectContaining({ toolCallId: "call-2", isError: true }),
			}),
		);
	});

	it("leaves an unresolved started durable write open despite an active repeat advisory", async () => {
		const store = new InMemoryTranscriptStore();
		let transcriptRecord = 0;
		let recoveries = 0;
		const transcript = new DurableTranscript(store, {
			maxRecordBytes: 16_384,
			maxCommitBytes: 65_536,
			maxRestoreBytes: 131_072,
			maxJsonDepth: 16,
			newRecordId: () => `transcript-${++transcriptRecord}`,
			recoverStartedTool: async () => {
				recoveries += 1;
				return {
					outcome: "completed",
					content: [{ type: "text" as const, text: "authoritative receipt" }],
					isError: false,
					timestamp: 2,
				};
			},
		});
		const admittedActions: string[] = [];
		const durable: DurableExecutionAgentTool = {
			...tool("write", async () => {
				throw new Error("The prepared durable execution must own the Action.");
			}),
			[DURABLE_TOOL_EXECUTION]: async (toolCallId) => ({
				operationId: `operation-${toolCallId}`,
				execute: async () => {
					admittedActions.push(toolCallId);
					throw new DurableToolExecutionUnresolvedError(new Error("receipt transport unavailable"));
				},
			}),
		};
		const { agent } = createAgent(
			scriptedStream([() => messageResponse(assistant([toolCall("call-1", "write")], "toolUse"))]),
			[durable],
			{
				exactToolRepeatPolicy: { advisoryThreshold: 1, terminationThreshold: 2 },
				transcript: {
					transcript,
					restore: await transcript.restore({ sessionId: INPUT.sessionId, actorId: INPUT.actorId }),
				},
			},
		);

		await expect(agent.start(INPUT, "run-unresolved-advisory").completion).resolves.toMatchObject({ status: "failed" });

		expect(admittedActions).toEqual(["call-1"]);
		const beforeRepair = await store.load({ sessionId: INPUT.sessionId, actorId: INPUT.actorId }, 131_072);
		expect(beforeRepair.records.map((record) => record.type)).toEqual([
			"message.completed",
			"message.completed",
			"tool.started",
		]);
		await expect(transcript.restore({ sessionId: INPUT.sessionId, actorId: INPUT.actorId })).resolves.toMatchObject({
			messages: expect.arrayContaining([
				expect.objectContaining({ role: "toolResult", toolCallId: "call-1", isError: false }),
			]),
		});
		expect(recoveries).toBe(1);
	});

	it("fails a preflighted parallel batch closed when repeat-event recording fails without leaking the sink error", async () => {
		const execute = vi.fn(async () => ({ content: [{ type: "text" as const, text: "ran" }], details: {} }));
		const repeated = [toolCall("call-1", "inspect"), toolCall("call-2", "inspect")];
		const { agent, events } = createAgent(
			scriptedStream([() => messageResponse(assistant(repeated, "toolUse"))]),
			[{ ...tool("inspect", execute), executionMode: "parallel" }],
			{ exactToolRepeatPolicy: { advisoryThreshold: 2, terminationThreshold: 3 } },
			async (_agent, _coordinate, data) => {
				if (data.type === "tool.repeat.detected") throw new Error("secret-database-path");
			},
		);

		await expect(agent.start(INPUT, "run-event-failure").completion).resolves.toMatchObject({ status: "failed" });

		expect(execute).not.toHaveBeenCalled();
		expect(JSON.stringify(events)).not.toContain("secret-database-path");
	});

	it("fails closed when a repeat-event acknowledgment belongs to another run", async () => {
		const contexts: Context[] = [];
		const execute = vi.fn(async () => ({ content: [{ type: "text" as const, text: "ran" }], details: {} }));
		const { agent } = createAgent(
			scriptedStream([() => messageResponse(assistant([toolCall("call-1", "inspect")], "toolUse"))], contexts),
			[tool("inspect", execute)],
			{ exactToolRepeatPolicy: { advisoryThreshold: 1, terminationThreshold: 2 } },
			async (_agent, _coordinate, data, event) =>
				data.type === "tool.repeat.detected"
					? { ...event, coordinate: { ...event.coordinate, runId: "wrong-run" } }
					: undefined,
		);

		await expect(agent.start(INPUT, "run-wrong-event-ack").completion).resolves.toMatchObject({ status: "failed" });

		expect(execute).not.toHaveBeenCalled();
		expect(contexts).toHaveLength(1);
	});

	it("keeps cancellation classified as aborted when it wins before repeat-policy preflight", async () => {
		let abortPromise: Promise<unknown> | undefined;
		const execute = vi.fn(async () => ({ content: [{ type: "text" as const, text: "ran" }], details: {} }));
		const { agent, events } = createAgent(
			scriptedStream([() => messageResponse(assistant([toolCall("call-1", "inspect")], "toolUse"))]),
			[tool("inspect", execute)],
			{ exactToolRepeatPolicy: { advisoryThreshold: 1, terminationThreshold: 2 } },
			(currentAgent, coordinate, data) => {
				if (
					abortPromise === undefined &&
					data.type === "item.completed" &&
					data.item.itemType === "message" &&
					data.item.role === "assistant"
				) {
					abortPromise = Promise.resolve().then(() => currentAgent.abortRun(coordinate, "cancelled-for-test"));
				}
			},
		);

		await expect(agent.start(INPUT, "run-abort-race").completion).resolves.toMatchObject({ status: "aborted" });
		await abortPromise;
		expect(execute).not.toHaveBeenCalled();
		expect(events.some((event) => event.data.type === "tool.repeat.detected")).toBe(false);
	});

	it("resets the sequence when accepted steering supplies new evidence", async () => {
		const started = deferredVoid();
		const release = deferredVoid();
		let executions = 0;
		const inspect = vi.fn(async () => {
			executions += 1;
			if (executions === 1) {
				started.resolve();
				await release.promise;
			}
			return { content: [{ type: "text" as const, text: "inspect" }], details: {} };
		});
		const { agent, events } = createAgent(
			scriptedStream([
				() => messageResponse(assistant([toolCall("inspect-1", "inspect")], "toolUse")),
				() => messageResponse(assistant([toolCall("inspect-2", "inspect")], "toolUse")),
				() => textResponse("done"),
			]),
			[tool("inspect", inspect)],
			{ exactToolRepeatPolicy: { advisoryThreshold: 1, terminationThreshold: 2 } },
		);

		const run = agent.start(INPUT, "run-steer");
		await started.promise;
		const coordinate = agent.readActiveCoordinate();
		if (!coordinate) throw new Error("Expected an active coordinate.");
		await expect(agent.control(coordinate, { type: "steer", text: "new evidence" })).resolves.toMatchObject({
			accepted: true,
		});
		release.resolve();
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });

		expect(inspect).toHaveBeenCalledTimes(2);
		expect(events.flatMap((event) => (event.data.type === "tool.repeat.detected" ? [event.data.action] : []))).toEqual([
			"advisory",
			"advisory",
		]);
	});

	it("resets across a non-prepared invalid Tool call instead of treating it as an exemption", async () => {
		const contexts: Context[] = [];
		const inspect = vi.fn(async () => ({ content: [{ type: "text" as const, text: "inspect" }], details: {} }));
		const { agent, events } = createAgent(
			scriptedStream(
				[
					() => messageResponse(assistant([toolCall("inspect-1", "inspect")], "toolUse")),
					() => messageResponse(assistant([toolCall("missing-1", "missing")], "toolUse")),
					() => messageResponse(assistant([toolCall("inspect-2", "inspect")], "toolUse")),
					() => textResponse("done"),
				],
				contexts,
			),
			[tool("inspect", inspect)],
			{ exactToolRepeatPolicy: { advisoryThreshold: 1, terminationThreshold: 2 } },
		);

		await expect(agent.start(INPUT, "run-invalid-gap").completion).resolves.toMatchObject({ status: "completed" });

		expect(inspect).toHaveBeenCalledTimes(2);
		expect(events.flatMap((event) => (event.data.type === "tool.repeat.detected" ? [event.data.action] : []))).toEqual([
			"advisory",
			"advisory",
		]);
		expect(policyMessages(contexts[1] ?? { messages: [] })).toHaveLength(1);
		expect(policyMessages(contexts[2] ?? { messages: [] })).toEqual([]);
	});

	it("keeps termination active through the current sequential batch before applying queued steering", async () => {
		const contexts: Context[] = [];
		const started = deferredVoid();
		const release = deferredVoid();
		const inspect = vi.fn(async () => {
			started.resolve();
			await release.promise;
			return { content: [{ type: "text" as const, text: "inspect" }], details: {} };
		});
		const sequentialInspect = { ...tool("inspect", inspect), executionMode: "sequential" as const };
		const { agent, events } = createAgent(
			scriptedStream(
				[
					() =>
						messageResponse(
							assistant(
								[toolCall("inspect-1", "inspect"), toolCall("inspect-2", "inspect"), toolCall("inspect-3", "inspect")],
								"toolUse",
							),
						),
					() => textResponse("done"),
				],
				contexts,
			),
			[sequentialInspect],
			{ exactToolRepeatPolicy: { advisoryThreshold: 1, terminationThreshold: 2 } },
		);

		const run = agent.start(INPUT, "run-sequential-steer");
		await started.promise;
		const coordinate = agent.readActiveCoordinate();
		if (!coordinate) throw new Error("Expected an active coordinate.");
		await expect(agent.control(coordinate, { type: "steer", text: "new evidence" })).resolves.toMatchObject({
			accepted: true,
		});
		release.resolve();
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });

		expect(inspect).toHaveBeenCalledTimes(1);
		expect(events.flatMap((event) => (event.data.type === "tool.repeat.detected" ? [event.data.action] : []))).toEqual([
			"advisory",
			"terminated",
		]);
		expect(contexts).toHaveLength(2);
		expect(policyMessages(contexts[1] ?? { messages: [] })).toEqual([]);
	});

	it("promotes a queued follow-up at termination without issuing a stale advisory request first", async () => {
		const contexts: Context[] = [];
		const started = deferredVoid();
		const release = deferredVoid();
		const inspect = vi.fn(async () => {
			started.resolve();
			await release.promise;
			return { content: [{ type: "text" as const, text: "inspect" }], details: {} };
		});
		const sequentialInspect = { ...tool("inspect", inspect), executionMode: "sequential" as const };
		const { agent, events } = createAgent(
			scriptedStream(
				[
					() =>
						messageResponse(
							assistant(
								[toolCall("inspect-1", "inspect"), toolCall("inspect-2", "inspect"), toolCall("inspect-3", "inspect")],
								"toolUse",
							),
						),
					() => textResponse("done"),
				],
				contexts,
			),
			[sequentialInspect],
			{ exactToolRepeatPolicy: { advisoryThreshold: 1, terminationThreshold: 2 } },
		);

		const run = agent.start(INPUT, "run-sequential-follow-up");
		await started.promise;
		const coordinate = agent.readActiveCoordinate();
		if (!coordinate) throw new Error("Expected an active coordinate.");
		await expect(agent.control(coordinate, { type: "follow-up", text: "new evidence" })).resolves.toMatchObject({
			accepted: true,
		});
		release.resolve();
		await expect(run.completion).resolves.toMatchObject({ status: "completed" });

		expect(inspect).toHaveBeenCalledTimes(1);
		expect(events.flatMap((event) => (event.data.type === "tool.repeat.detected" ? [event.data.action] : []))).toEqual([
			"advisory",
			"terminated",
		]);
		expect(contexts).toHaveLength(2);
		expect(policyMessages(contexts[1] ?? { messages: [] })).toEqual([]);
		expect(contexts[1]?.messages.some((message) => message.role === "user" && message.content === "new evidence")).toBe(
			true,
		);
	});

	it("rejects missing, inverted, and ambiguous host policy configuration", () => {
		const stream = scriptedStream([() => textResponse("done")]);
		for (const exactToolRepeatPolicy of [
			{ advisoryThreshold: 0, terminationThreshold: 2 },
			{ advisoryThreshold: 2, terminationThreshold: 2 },
			{ advisoryThreshold: 3, terminationThreshold: 2 },
			{ advisoryThreshold: 1.5, terminationThreshold: 2 },
			{ advisoryThreshold: 1, terminationThreshold: Number.MAX_SAFE_INTEGER + 1 },
			{ advisoryThreshold: 1, terminationThreshold: 2, exemptToolNames: ["poll", "poll"] },
			{ advisoryThreshold: 1, terminationThreshold: 2, exemptToolNames: null },
			null,
			false,
			0,
			"",
		]) {
			expect(() =>
				createAgent(stream, [tool("inspect", async () => ({ content: [], details: {} }))], {
					exactToolRepeatPolicy: exactToolRepeatPolicy as never,
				}),
			).toThrow(TypeError);
		}
	});

	it("captures a bounded exemption array once without invoking a mutable iterator", () => {
		let lengthReads = 0;
		const names = new Proxy(["poll", "must-not-be-captured"], {
			get(target, property, receiver) {
				if (property === "length") {
					lengthReads += 1;
					return lengthReads === 1 ? 1 : target.length;
				}
				if (property === Symbol.iterator) throw new Error("iterator must not be read");
				return Reflect.get(target, property, receiver);
			},
		});

		const captured = captureExactToolRepeatPolicy(
			{ advisoryThreshold: 1, terminationThreshold: 2, exemptToolNames: names },
			1,
		);

		expect(lengthReads).toBe(1);
		expect([...captured.exemptToolNames]).toEqual(["poll"]);
	});
});

import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import { GameAgent, type NormalizedGameInput } from "@opengameagent/agent";
import type { GameMemory, MemoryRecallRequest, MemoryRecallResult, MemoryRecord } from "@opengameagent/memory";
import { type CanonicalEventData, eventAudience, PROTOCOL_VERSION } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import {
	allowAllTestTools,
	assistant,
	messageResponse,
	scriptedStream,
	TEST_MODEL,
	TEST_TOOL_LIMITS,
	textResponse,
} from "../../devtools/src/pi-fakes.js";
import {
	MEMORY_RECALL_TOOL_NAME,
	type MemoryAgentAccess,
	MemoryContextIsolationError,
	MemoryToolSource,
} from "../src/index.js";

const INPUT: NormalizedGameInput = {
	sessionId: "session-1",
	actorId: "actor-1",
	inputId: "input-1",
	payload: { instruction: "recall" },
};

function access(identityScope: MemoryAgentAccess["identityScope"]): MemoryAgentAccess {
	return {
		ownerId: "owner-1",
		timelineId: "timeline-1",
		generation: "2",
		identityScope,
		memoryScope: "world",
		kinds: ["observation"],
		tags: ["quest"],
		atOrBeforeTick: "12",
		viewer: { kind: "owner", ownerId: "owner-1" },
		resultLimit: 2,
		vectorFailure: "lexical-only",
	};
}

function record(overrides: Partial<MemoryRecord> = {}): MemoryRecord {
	return {
		memoryKey: 1n,
		memoryId: "memory-1",
		revision: "3",
		partition: { ownerId: "owner-1", actorId: "actor-1", sessionId: "session-1", scope: "world" },
		kind: "observation",
		tags: ["quest"],
		gameTime: { timelineId: "timeline-1", generation: "2", tick: "9" },
		importance: 0.8,
		searchText: "private retrieval text",
		content: { fact: "the gate is open" },
		audience: { visibility: "owner" },
		lifecycle: "active",
		createdAt: "2026-08-28T00:00:00.000Z",
		updatedAt: "2026-08-28T00:00:00.000Z",
		...overrides,
	};
}

function memory(result: MemoryRecallResult, requests: MemoryRecallRequest[]): Pick<GameMemory, "recall"> {
	return {
		recall: async (request) => {
			requests.push(request);
			return result;
		},
	};
}

describe("MemoryToolSource", () => {
	it.each([
		["owner", {}],
		["actor", { actorId: "actor-1" }],
		["session", { actorId: "actor-1", sessionId: "session-1" }],
	] as const)("contributes one Pi Tool with host-bound %s identity access", async (identityScope, identity) => {
		const requests: MemoryRecallRequest[] = [];
		const source = new MemoryToolSource({
			sourceId: "memory-tool",
			memory: memory({ hits: [], vectorStatus: "not-configured", stages: [] }, requests),
			resolveAccess: () => access(identityScope),
		});
		expect(source.provenanceKind).toBe("memory");

		const tool = await requestTool(source);
		expect(tool.name).toBe(MEMORY_RECALL_TOOL_NAME);
		expect(tool.executionMode).toBe("parallel");
		expect(tool.parameters).toMatchObject({
			type: "object",
			additionalProperties: false,
			required: ["query"],
			properties: { query: { type: "string", minLength: 1 } },
		});

		await tool.execute("call-1", { query: "open gate" });
		expect(requests).toHaveLength(1);
		expect(requests[0]).toMatchObject({
			query: "open gate",
			selector: {
				ownerId: "owner-1",
				timelineId: "timeline-1",
				generation: "2",
				scope: "world",
				kinds: ["observation"],
				tags: ["quest"],
				atOrBeforeTick: "12",
				...identity,
			},
			viewer: { kind: "owner", ownerId: "owner-1" },
			resultLimit: 2,
			vectorFailure: "lexical-only",
		});
	});

	it.each([
		"not-configured",
		"unprepared",
		"preparing",
		"preparation-failed",
		"proven-empty",
		"used",
		"failed-open",
	] as const)("projects ranked records without private retrieval fields when vectors are %s", async (vectorStatus) => {
		const source = new MemoryToolSource({
			sourceId: "memory-tool",
			memory: memory(
				{
					hits: [
						{ record: record({ memoryId: "memory-2", memoryKey: 2n, revision: "5" }), score: 0.9 },
						{ record: record(), score: 0.8 },
					],
					vectorStatus,
					stages: [{ stage: "hydrate", outcome: "ok", durationMs: 12.5, inputCount: 2, outputCount: 2 }],
				},
				[],
			),
			resolveAccess: () => access("session"),
		});

		const result = await (await requestTool(source)).execute("call-1", { query: "open gate" });
		expect(textResult(result)).toEqual({
			memories: [
				{
					referenceId: "memory-2",
					revision: "5",
					content: {
						kind: "observation",
						tags: ["quest"],
						gameTime: { timelineId: "timeline-1", generation: "2", tick: "9" },
						importance: 0.8,
						memoryScope: "world",
						content: { fact: "the gate is open" },
					},
				},
				{
					referenceId: "memory-1",
					revision: "3",
					content: {
						kind: "observation",
						tags: ["quest"],
						gameTime: { timelineId: "timeline-1", generation: "2", tick: "9" },
						importance: 0.8,
						memoryScope: "world",
						content: { fact: "the gate is open" },
					},
				},
			],
			vectorStatus,
		});
		expect(result.details).toBeUndefined();
		const serialized = JSON.stringify(result);
		for (const privateValue of [
			"private retrieval text",
			"owner-1",
			"actor-1",
			"session-1",
			"memoryKey",
			"score",
			"audience",
			"durationMs",
			"stages",
		]) {
			expect(serialized).not.toContain(privateValue);
		}
	});

	it("returns an ordinary successful result when no memory matches", async () => {
		const source = new MemoryToolSource({
			sourceId: "memory-tool",
			memory: memory({ hits: [], vectorStatus: "failed-open", stages: [] }, []),
			resolveAccess: () => access("owner"),
		});

		const result = await (await requestTool(source)).execute("call-1", { query: "unknown" });
		expect(textResult(result)).toEqual({ memories: [], vectorStatus: "failed-open" });
	});

	it("executes through the same Pi Agent loop as the game request", async () => {
		const requests: MemoryRecallRequest[] = [];
		const events: CanonicalEventData[] = [];
		const contexts: Parameters<Parameters<typeof scriptedStream>[0][number]>[0][] = [];
		const source = new MemoryToolSource({
			sourceId: "memory-tool",
			memory: memory(
				{
					hits: [{ record: record(), score: 1 }],
					vectorStatus: "used",
					stages: [
						{ stage: "partition", outcome: "ok", durationMs: 1, inputCount: 1, outputCount: 1 },
						{ stage: "hydrate", outcome: "ok", durationMs: 2, inputCount: 1, outputCount: 1 },
					],
				},
				requests,
			),
			resolveAccess: () => access("session"),
		});
		let sequence = 0;
		const agent = new GameAgent({
			sessionId: INPUT.sessionId,
			actorId: INPUT.actorId,
			model: TEST_MODEL,
			modelStream: scriptedStream(
				[
					(context) => {
						expect(context.tools?.map((tool) => tool.name)).toEqual([MEMORY_RECALL_TOOL_NAME]);
						return messageResponse(
							assistant(
								[
									{
										type: "toolCall",
										id: "call-memory",
										name: MEMORY_RECALL_TOOL_NAME,
										arguments: { query: "open gate" },
									},
								],
								"toolUse",
							),
						);
					},
					(context) => {
						const toolResult = context.messages.find((message) => message.role === "toolResult");
						if (toolResult?.role !== "toolResult") throw new Error("Expected Pi Tool result message.");
						const text = toolResult.content.find((part) => part.type === "text")?.text;
						expect(text).toContain('"referenceId":"memory-1"');
						expect(text).not.toContain("private retrieval text");
						return textResponse("done");
					},
				],
				contexts,
			),
			requestSources: [source],
			toolLimits: TEST_TOOL_LIMITS,
			toolPolicy: allowAllTestTools,
			emitEvent: async (coordinate, data) => {
				events.push(data);
				sequence += 1;
				const ordinal = String(sequence);
				return {
					protocolVersion: PROTOCOL_VERSION,
					eventId: `event-${ordinal}`,
					sequence: ordinal,
					revision: ordinal,
					occurredAt: "2026-08-28T00:00:00.000Z",
					coordinate: structuredClone(coordinate),
					audience: eventAudience(data),
					data: structuredClone(data),
				};
			},
			newId: () => "generated-id",
			now: () => 1,
		});

		await expect(agent.start(INPUT, "run-1").completion).resolves.toMatchObject({ status: "completed" });
		expect(contexts).toHaveLength(2);
		expect(requests).toHaveLength(1);
		expect(requests[0]?.query).toBe("open gate");
		expect(events.find((event) => event.type === "run.metrics")).toMatchObject({
			durations: { memoryPrepare: "1000", memorySearch: "2000" },
		});
	});

	it.each([
		{ partition: { ownerId: "owner-2", actorId: "actor-1", sessionId: "session-1", scope: "world" } },
		{ partition: { ownerId: "owner-1", actorId: "actor-2", sessionId: "session-1", scope: "world" } },
		{ partition: { ownerId: "owner-1", actorId: "actor-1", sessionId: "session-2", scope: "world" } },
		{ partition: { ownerId: "owner-1", actorId: "actor-1", sessionId: "session-1", scope: "other" } },
		{ gameTime: { timelineId: "timeline-2", generation: "2", tick: "9" } },
		{ gameTime: { timelineId: "timeline-1", generation: "3", tick: "9" } },
		{ lifecycle: "forgotten" as const, forgottenAt: "2026-08-28T00:00:01.000Z" },
	])("fails closed when recall returns inaccessible state", async (override) => {
		const source = new MemoryToolSource({
			sourceId: "memory-tool",
			memory: memory(
				{ hits: [{ record: record(override), score: 1 }], vectorStatus: "not-configured", stages: [] },
				[],
			),
			resolveAccess: () => access("session"),
		});

		await expect((await requestTool(source)).execute("call-1", { query: "gate" })).rejects.toBeInstanceOf(
			MemoryContextIsolationError,
		);
	});

	it("passes collection and execution cancellation through their matching boundaries", async () => {
		const collectionController = new AbortController();
		const executionController = new AbortController();
		let resolverSignal: AbortSignal | undefined;
		let recallSignal: AbortSignal | undefined;
		const source = new MemoryToolSource({
			sourceId: "memory-tool",
			memory: {
				recall: async (request) => {
					recallSignal = request.signal;
					return { hits: [], vectorStatus: "not-configured", stages: [] };
				},
			},
			resolveAccess: (_input, signal) => {
				resolverSignal = signal;
				return access("session");
			},
		});

		const tool = await requestTool(source, INPUT, collectionController.signal);
		await tool.execute("call-1", { query: "gate" }, executionController.signal);
		expect(resolverSignal).toBe(collectionController.signal);
		expect(recallSignal).toBe(executionController.signal);
	});

	it("preserves exact cancellation before validating resolved access", async () => {
		const controller = new AbortController();
		const reason = new Error("access resolution cancelled");
		const source = new MemoryToolSource({
			sourceId: "memory-tool",
			memory: memory({ hits: [], vectorStatus: "not-configured", stages: [] }, []),
			resolveAccess: () => {
				controller.abort(reason);
				return {} as never;
			},
		});

		await expect(requestTool(source, INPUT, controller.signal)).rejects.toBe(reason);
	});

	it("captures input identity, host access arrays, and the recall callback once", async () => {
		let actorReads = 0;
		let sessionReads = 0;
		let recallReads = 0;
		const input = { ...INPUT } as Record<string, unknown>;
		Object.defineProperties(input, {
			actorId: { enumerable: true, get: () => (++actorReads === 1 ? "actor-1" : "actor-2") },
			sessionId: { enumerable: true, get: () => (++sessionReads === 1 ? "session-1" : "session-2") },
		});
		const kinds = ["observation"];
		const tags = ["quest"];
		const requests: MemoryRecallRequest[] = [];
		const host = Object.defineProperty({}, "recall", {
			enumerable: true,
			get: () => {
				recallReads += 1;
				return memory({ hits: [{ record: record(), score: 1 }], vectorStatus: "not-configured", stages: [] }, requests)
					.recall;
			},
		});
		const source = new MemoryToolSource({
			sourceId: "memory-tool",
			memory: host as Pick<GameMemory, "recall">,
			resolveAccess: () => ({ ...access("session"), kinds, tags }),
		});

		const tool = await requestTool(source, input as unknown as NormalizedGameInput);
		kinds[0] = "mutated";
		tags[0] = "mutated";
		await tool.execute("call-1", { query: "gate" });
		expect(requests[0]?.selector).toMatchObject({
			actorId: "actor-1",
			sessionId: "session-1",
			kinds: ["observation"],
			tags: ["quest"],
		});
		expect({ actorReads, sessionReads, recallReads }).toEqual({ actorReads: 1, sessionReads: 1, recallReads: 1 });
	});
});

async function requestTool(
	source: MemoryToolSource,
	input: Readonly<NormalizedGameInput> = INPUT,
	signal: AbortSignal = new AbortController().signal,
): Promise<AgentTool> {
	const contribution = await source.collect(input, signal);
	const tools = contribution.tools ?? [];
	expect(tools).toHaveLength(1);
	const tool = tools[0];
	if (!tool) throw new Error("Expected memory_recall Tool.");
	return tool;
}

function textResult(result: AgentToolResult<unknown>): Record<string, unknown> {
	const content = result.content[0];
	if (content?.type !== "text") throw new Error("Expected a text Tool result.");
	return JSON.parse(content.text) as Record<string, unknown>;
}

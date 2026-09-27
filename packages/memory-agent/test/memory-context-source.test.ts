import { GameAgent, type NormalizedGameInput } from "@opengameagent/agent";
import type { GameMemory, MemoryRecallRequest, MemoryRecallResult, MemoryRecord } from "@opengameagent/memory";
import { type CanonicalEventData, eventAudience, PROTOCOL_VERSION, type RunCoordinate } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { TEST_MODEL, textResponse } from "../../devtools/src/pi-fakes.js";
import {
	type MemoryContextIdentityScope,
	MemoryContextIsolationError,
	type MemoryContextRecallObservation,
	MemoryContextSource,
} from "../src/index.js";

const INPUT: NormalizedGameInput = {
	sessionId: "session-1",
	actorId: "actor-1",
	inputId: "input-1",
	payload: { instruction: "remember" },
};

const COORDINATE: RunCoordinate = {
	sessionId: "session-1",
	actorId: "actor-1",
	runId: "run-1",
	turn: "1",
};

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

function query(identityScope: MemoryContextIdentityScope) {
	return {
		query: "open gate",
		ownerId: "owner-1",
		timelineId: "timeline-1",
		generation: "2",
		identityScope,
		memoryScope: "world",
		viewer: { kind: "owner" as const, ownerId: "owner-1" },
		resultLimit: 2,
		vectorFailure: "lexical-only" as const,
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

describe("MemoryContextSource", () => {
	it("reports validated memory prepare/search durations in the active agent run without a host observer", async () => {
		const events: CanonicalEventData[] = [];
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: memory(
				{
					hits: [],
					vectorStatus: "used",
					stages: [
						{ stage: "partition", outcome: "ok", durationMs: 1.25, inputCount: 1, outputCount: 1 },
						{ stage: "index-restore", outcome: "ok", durationMs: 2.5, inputCount: 1, outputCount: 1 },
						{ stage: "lexical", outcome: "ok", durationMs: 3, inputCount: 1, outputCount: 0 },
						{ stage: "embedding", outcome: "ok", durationMs: 4, inputCount: 1, outputCount: 1 },
						{ stage: "vector", outcome: "ok", durationMs: 5, inputCount: 1, outputCount: 0 },
						{ stage: "fusion", outcome: "ok", durationMs: 6, inputCount: 0, outputCount: 0 },
						{ stage: "hydrate", outcome: "ok", durationMs: 7, inputCount: 0, outputCount: 0 },
						{ stage: "rerank", outcome: "ok", durationMs: 8, inputCount: 0, outputCount: 0 },
					],
				},
				[],
			),
			resolveQuery: () => query("session"),
		});
		let sequence = 0;
		let id = 0;
		const agent = new GameAgent({
			sessionId: INPUT.sessionId,
			actorId: INPUT.actorId,
			model: TEST_MODEL,
			modelStream: () => textResponse("done"),
			contextSources: [source],
			contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 1024 },
			newId: () => `memory-run-${++id}`,
			now: () => 1,
			emitEvent: async (coordinate, data) => {
				events.push(data);
				const ordinal = String(++sequence);
				return {
					protocolVersion: PROTOCOL_VERSION,
					eventId: `event-${ordinal}`,
					sequence: ordinal,
					revision: ordinal,
					occurredAt: "2026-09-01T00:00:00.000Z",
					coordinate,
					audience: eventAudience(data),
					data,
				};
			},
		});
		await expect(agent.start(INPUT, "run-memory").completion).resolves.toMatchObject({ status: "completed" });
		const metrics = events.find((event) => event.type === "run.metrics");
		expect(metrics).toMatchObject({
			type: "run.metrics",
			unit: "microseconds",
			durations: { memoryPrepare: "3750", memorySearch: "33000" },
		});
		expect(JSON.stringify(metrics)).not.toContain("open gate");
		expect(events.findIndex((event) => event.type === "run.metrics")).toBeLessThan(
			events.findIndex((event) => event.type === "run.completed"),
		);
	});

	it("projects partition and restore metrics without copying extra source content", async () => {
		const observations: MemoryContextRecallObservation[] = [];
		const stages = ["partition", "index-restore"] as const;
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: memory(
				{
					hits: [],
					vectorStatus: "used",
					stages: stages.map((stage) => ({
						stage,
						outcome: "ok",
						durationMs: 1,
						inputCount: 1,
						outputCount: 1,
						query: "private source text",
					})),
				},
				[],
			),
			resolveQuery: () => query("session"),
			observeRecall: (value) => {
				observations.push(value);
			},
		});
		expect((await source.load(INPUT, new AbortController().signal, COORDINATE)).references).toEqual([]);
		expect(observations).toEqual([
			{
				vectorStatus: "used",
				stages: [
					{ stage: "partition", outcome: "ok", durationMs: 1, inputCount: 1, outputCount: 1 },
					{ stage: "index-restore", outcome: "ok", durationMs: 1, inputCount: 1, outputCount: 1 },
				],
			},
		]);
	});

	it.each([
		["owner", {}],
		["actor", { actorId: "actor-1" }],
		["session", { actorId: "actor-1", sessionId: "session-1" }],
	] as const)("binds %s identity scope to the current game input", async (identityScope, identity) => {
		const requests: MemoryRecallRequest[] = [];
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: memory({ hits: [{ record: record(), score: 1 }], vectorStatus: "not-configured", stages: [] }, requests),
			resolveQuery: () => query(identityScope),
		});
		expect(source.provenanceKind).toBe("memory");

		await source.load(INPUT, new AbortController().signal, COORDINATE);
		expect(requests).toHaveLength(1);
		expect(requests[0]).toMatchObject({
			query: "open gate",
			selector: {
				ownerId: "owner-1",
				timelineId: "timeline-1",
				generation: "2",
				scope: "world",
				...identity,
			},
			viewer: { kind: "owner", ownerId: "owner-1" },
			resultLimit: 2,
			vectorFailure: "lexical-only",
		});
	});

	it("preserves ranked hits while projecting only model-relevant fields", async () => {
		const requests: MemoryRecallRequest[] = [];
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: memory(
				{
					hits: [
						{ record: record({ memoryId: "memory-2", memoryKey: 2n, revision: "5" }), score: 0.9 },
						{ record: record(), score: 0.8 },
					],
					vectorStatus: "used",
					stages: [{ stage: "hydrate", outcome: "ok", durationMs: 12.5, inputCount: 2, outputCount: 2 }],
				},
				requests,
			),
			resolveQuery: () => query("session"),
		});

		const { references } = await source.load(INPUT, new AbortController().signal, COORDINATE);
		expect(references.map((reference) => reference.referenceId)).toEqual(["memory-2", "memory-1"]);
		expect(references[0]).toEqual({
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
		});
		expect(JSON.stringify(references)).not.toContain("private retrieval text");
		expect(JSON.stringify(references)).not.toContain("owner-1");
		expect(JSON.stringify(references)).not.toContain("durationMs");
		expect(JSON.stringify(references)).not.toContain("stages");
	});

	it("reports content-free recall telemetry to the host without exposing it to the model", async () => {
		const observations: MemoryContextRecallObservation[] = [];
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: memory(
				{
					hits: [],
					vectorStatus: "preparing",
					stages: [{ stage: "lexical", outcome: "ok", durationMs: 1.5, inputCount: 1, outputCount: 0 }],
				},
				[],
			),
			resolveQuery: () => query("session"),
			observeRecall: (observation) => {
				observations.push(observation);
				return Promise.reject(new Error("telemetry unavailable"));
			},
		});

		const contribution = await source.load(INPUT, new AbortController().signal, COORDINATE);

		expect(observations).toEqual([
			{
				vectorStatus: "preparing",
				stages: [{ stage: "lexical", outcome: "ok", durationMs: 1.5, inputCount: 1, outputCount: 0 }],
			},
		]);
		expect(Object.isFrozen(observations[0])).toBe(true);
		expect(Object.isFrozen(observations[0]?.stages)).toBe(true);
		expect(Object.isFrozen(observations[0]?.stages[0])).toBe(true);
		expect(JSON.stringify(contribution.references)).not.toContain("vectorStatus");
		expect(JSON.stringify(contribution.references)).not.toContain("stages");
	});

	it.each([
		{ partition: { ownerId: "owner-2", actorId: "actor-1", sessionId: "session-1", scope: "world" } },
		{ partition: { ownerId: "owner-1", actorId: "actor-2", sessionId: "session-1", scope: "world" } },
		{ partition: { ownerId: "owner-1", actorId: "actor-1", sessionId: "session-2", scope: "world" } },
		{ gameTime: { timelineId: "timeline-2", generation: "2", tick: "9" } },
		{ gameTime: { timelineId: "timeline-1", generation: "3", tick: "9" } },
		{ lifecycle: "forgotten" as const, forgottenAt: "2026-08-28T00:00:01.000Z" },
	])("fails closed when recall returns cross-partition state", async (override) => {
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: memory(
				{ hits: [{ record: record(override), score: 1 }], vectorStatus: "not-configured", stages: [] },
				[],
			),
			resolveQuery: () => query("session"),
		});

		await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toBeInstanceOf(
			MemoryContextIsolationError,
		);
	});

	it("passes the active abort signal through query resolution and recall", async () => {
		const controller = new AbortController();
		let resolverSignal: AbortSignal | undefined;
		let recallSignal: AbortSignal | undefined;
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: {
				recall: async (request) => {
					recallSignal = request.signal;
					return { hits: [], vectorStatus: "not-configured", stages: [] };
				},
			},
			resolveQuery: (_input, signal) => {
				resolverSignal = signal;
				return query("session");
			},
		});

		await source.load(INPUT, controller.signal, COORDINATE);
		expect(resolverSignal).toBe(controller.signal);
		expect(recallSignal).toBe(controller.signal);
	});

	it("preserves exact cancellation before validating a resolved query", async () => {
		const controller = new AbortController();
		const reason = new Error("query resolution cancelled");
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: memory({ hits: [], vectorStatus: "not-configured", stages: [] }, []),
			resolveQuery: () => {
				controller.abort(reason);
				return {} as never;
			},
		});

		await expect(source.load(INPUT, controller.signal, COORDINATE)).rejects.toBe(reason);
	});

	it("keeps projection identity bound to the resolved query snapshot", async () => {
		let ownerReads = 0;
		const mutableQuery = Object.defineProperty(query("owner"), "ownerId", {
			enumerable: true,
			get: () => (++ownerReads === 1 ? "owner-1" : "owner-2"),
		});
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: memory(
				{
					hits: [
						{
							record: record({
								partition: { ownerId: "owner-2", actorId: "actor-1", sessionId: "session-1", scope: "world" },
							}),
							score: 1,
						},
					],
					vectorStatus: "not-configured",
					stages: [],
				},
				[],
			),
			resolveQuery: () => mutableQuery,
		});

		await expect(source.load(INPUT, new AbortController().signal, COORDINATE)).rejects.toBeInstanceOf(
			MemoryContextIsolationError,
		);
		expect(ownerReads).toBe(1);
	});

	it("keeps selector and projection identity bound to one input snapshot", async () => {
		let actorReads = 0;
		let sessionReads = 0;
		const input = { ...INPUT } as Record<string, unknown>;
		Object.defineProperties(input, {
			actorId: {
				enumerable: true,
				get: () => (++actorReads === 1 ? "actor-1" : "actor-2"),
			},
			sessionId: {
				enumerable: true,
				get: () => (++sessionReads === 1 ? "session-1" : "session-2"),
			},
		});
		const requests: MemoryRecallRequest[] = [];
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: memory({ hits: [{ record: record(), score: 1 }], vectorStatus: "not-configured", stages: [] }, requests),
			resolveQuery: () => query("session"),
		});

		await source.load(input as unknown as NormalizedGameInput, new AbortController().signal, COORDINATE);
		expect(requests[0]?.selector).toMatchObject({ actorId: "actor-1", sessionId: "session-1" });
		expect(actorReads).toBe(1);
		expect(sessionReads).toBe(1);
	});

	it("captures the host recall callback once during construction", async () => {
		let reads = 0;
		const requests: MemoryRecallRequest[] = [];
		const host = Object.defineProperty({}, "recall", {
			enumerable: true,
			get: () => {
				reads += 1;
				return memory({ hits: [], vectorStatus: "not-configured", stages: [] }, requests).recall;
			},
		});
		const source = new MemoryContextSource({
			sourceId: "memory",
			memory: host as Pick<GameMemory, "recall">,
			resolveQuery: () => query("owner"),
		});

		await source.load(INPUT, new AbortController().signal, COORDINATE);
		expect(reads).toBe(1);
		expect(requests).toHaveLength(1);
	});
});

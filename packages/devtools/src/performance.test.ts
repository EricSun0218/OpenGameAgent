import { describe, expect, it } from "vitest";
import { evaluateGameTrace } from "./evaluation.js";
import { summarizeGamePerformance } from "./performance.js";
import { replayGameTrace } from "./replay.js";
import type { GameTraceActionRecord, GameTraceEventRecord, GameTraceRecording, GameTraceStageRecord } from "./trace.js";

const session = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 1,
	ownerId: "owner",
	sessionId: "session",
	actorId: "actor",
} as const;

const stage = (
	sequence: number,
	stageName: GameTraceStageRecord["stage"],
	durationMilliseconds: number,
): GameTraceStageRecord => ({
	schemaVersion: 1,
	kind: "stage",
	sequence,
	observedAt: 1_000 + sequence,
	session,
	inputId: "input-1",
	runId: "run-1",
	turn: 1,
	stage: stageName,
	startedAt: 1_000,
	durationMilliseconds,
	outcome: "ok",
});

const event = (
	sequence: number,
	eventType: GameTraceEventRecord["eventType"],
	timestamp: number,
	attributes: GameTraceEventRecord["attributes"] = {},
): GameTraceEventRecord => ({
	schemaVersion: 1,
	kind: "event",
	sequence,
	observedAt: timestamp,
	session,
	inputId: "input-1",
	runId: "run-1",
	turn: 1,
	eventType,
	eventSequence: sequence,
	eventId: `event-${sequence}`,
	audience: { visibility: "owner" },
	timestamp,
	attributes,
});

const action = (sequence: number, disposition: GameTraceActionRecord["disposition"]): GameTraceActionRecord => ({
	schemaVersion: 1,
	kind: "action",
	sequence,
	observedAt: 1_060,
	session,
	inputId: "input-1",
	runId: "run-1",
	turn: 1,
	operationId: `operation-${sequence}`,
	action: "move",
	startedAt: 1_020,
	durationMilliseconds: 40,
	frameworkMilliseconds: 10,
	hostMilliseconds: 30,
	disposition,
});

const recording = (): GameTraceRecording => ({
	schemaVersion: 1,
	createdAt: 1_000,
	droppedRecords: 0,
	records: [
		event(1, "run.started", 1_000, { provider: "provider", model: "model" }),
		stage(2, "queue", 5),
		stage(3, "prepare-turn", 8),
		event(4, "turn.started", 1_010),
		event(5, "tool.started", 1_020, { callId: "call-1", tool: "move" }),
		event(6, "tool.completed", 1_045, { callId: "call-1", isError: true }),
		event(7, "message.completed", 1_050, {
			characters: 12,
			usage: { input: 10, output: 5, cacheRead: 2, cacheWrite: 1, reasoning: 3, totalTokens: 21 },
		}),
		event(8, "run.completed", 1_055),
		stage(9, "run", 60),
		action(10, "uncertain"),
		action(11, "duplicate-prevented"),
	],
});

describe("performance summaries", () => {
	it("separates framework, first-output, tool, usage, and unknown cost metrics", () => {
		const summary = summarizeGamePerformance(recording());
		expect(summary.runs[0]).toMatchObject({
			provider: "provider",
			model: "model",
			totalMilliseconds: 60,
			queueMilliseconds: 5,
			preparationMilliseconds: 8,
			timeToFirstOutputMilliseconds: 10,
			timeToFirstToolMilliseconds: 10,
			toolCalls: 1,
			toolFailures: 1,
		});
		expect(summary.usage).toMatchObject({ totalTokens: 21, reasoning: 3, unknownCostRecords: 1, cost: null });
		expect(summary.tools[0]).toMatchObject({ tool: "move", failed: 1, meanMilliseconds: 25 });
		expect(summary.byProvider["provider"]?.totalTokens).toBe(21);
		expect(summary.actions[0]).toMatchObject({
			action: "move",
			dispatches: 2,
			uncertainWrites: 1,
			duplicateWritesPrevented: 1,
			totalFrameworkMilliseconds: 20,
			totalHostMilliseconds: 60,
		});
	});
});

describe("evaluation and observation replay", () => {
	it("reports configurable failures and replays only immutable observations", async () => {
		const source = recording();
		const report = await evaluateGameTrace(source, {
			maximumRunMilliseconds: 50,
			maximumToolFailureRate: 0,
			requiredEventTypes: ["run.started"],
			forbiddenTools: ["move"],
		});
		expect(report.passed).toBe(false);
		expect(report.findings.map((finding) => finding.rule)).toEqual(
			expect.arrayContaining(["maximum-run-duration", "maximum-tool-failure-rate", "forbidden-tool"]),
		);

		const observed: number[] = [];
		const delivered = await replayGameTrace(source, {
			onRecord(item) {
				observed.push(item.sequence);
			},
		});
		expect(delivered).toBe(source.records.length);
		expect(observed).toEqual(source.records.map((item) => item.sequence));
	});
});

import type { JsonObject, JsonValue } from "@opengameagent/protocol";
import type { GameRuntimeStage } from "@opengameagent/runtime";
import type { GameTraceActionRecord, GameTraceEventRecord, GameTraceRecording, GameTraceStageRecord } from "./trace.js";

export interface GameUsageMetric {
	records: number;
	input: number;
	output: number;
	cacheRead: number;
	cacheWrite: number;
	reasoning: number;
	totalTokens: number;
	unknownCostRecords: number;
	cost: number | null;
}

export interface GameStagePerformanceMetric {
	stage: GameRuntimeStage;
	name?: string;
	count: number;
	totalMilliseconds: number;
	maximumMilliseconds: number;
	errorCount: number;
	cancelledCount: number;
}

export interface GameToolPerformanceMetric {
	tool: string;
	calls: number;
	succeeded: number;
	failed: number;
	completed: number;
	meanMilliseconds: number | null;
	maximumMilliseconds: number | null;
}

export interface GameActionPerformanceMetric {
	action: string;
	dispatches: number;
	executed: number;
	duplicateWritesPrevented: number;
	reconcileRequired: number;
	conflictBlocked: number;
	uncertainWrites: number;
	reconciled: number;
	failedBeforeDispatch: number;
	totalFrameworkMilliseconds: number;
	totalHostMilliseconds: number;
	maximumMilliseconds: number;
}

export interface GameRunPerformanceSummary {
	runId: string;
	inputId: string;
	provider?: string;
	model?: string;
	outcome: "ok" | "error" | "cancelled" | "unknown";
	errorCategory?: string;
	totalMilliseconds: number | null;
	queueMilliseconds: number;
	preparationMilliseconds: number;
	timeToFirstOutputMilliseconds: number | null;
	timeToFirstToolMilliseconds: number | null;
	toolCalls: number;
	toolFailures: number;
	usage: GameUsageMetric;
}

export interface GameLatencyDistribution {
	count: number;
	mean: number | null;
	p50: number | null;
	p95: number | null;
	p99: number | null;
	maximum: number | null;
}

export interface GamePerformanceSummary {
	schemaVersion: 1;
	runs: readonly GameRunPerformanceSummary[];
	runLatency: GameLatencyDistribution;
	timeToFirstOutput: GameLatencyDistribution;
	timeToFirstTool: GameLatencyDistribution;
	toolCalls: number;
	toolFailures: number;
	toolFailureRate: number;
	actionDispatches: number;
	uncertainWrites: number;
	duplicateWritesPrevented: number;
	reconciliations: number;
	completedRuns: number;
	failedRuns: number;
	cancelledRuns: number;
	usage: GameUsageMetric;
	stages: readonly GameStagePerformanceMetric[];
	tools: readonly GameToolPerformanceMetric[];
	actions: readonly GameActionPerformanceMetric[];
	byProvider: Readonly<Record<string, GameUsageMetric>>;
	byModel: Readonly<Record<string, GameUsageMetric>>;
	byErrorCategory: Readonly<Record<string, number>>;
}

interface MutableRun {
	runId: string;
	inputId: string;
	provider?: string;
	model?: string;
	outcome: GameRunPerformanceSummary["outcome"];
	errorCategory?: string;
	totalMilliseconds: number | null;
	queueMilliseconds: number;
	preparationMilliseconds: number;
	firstTurnAt?: number;
	firstOutputAt?: number;
	firstToolAt?: number;
	toolStarts: Map<string, { tool: string; timestamp: number }>;
	toolCalls: number;
	toolFailures: number;
	usage: MutableUsage;
}

interface MutableUsage extends Omit<GameUsageMetric, "cost"> {
	knownCost: number;
}

interface MutableStage extends Omit<GameStagePerformanceMetric, "name"> {
	name?: string;
}

interface MutableTool {
	tool: string;
	calls: number;
	succeeded: number;
	failed: number;
	completed: number;
	durations: number[];
}

type MutableAction = GameActionPerformanceMetric;

export function summarizeGamePerformance(recording: GameTraceRecording): GamePerformanceSummary {
	if (recording.schemaVersion !== 1) throw new Error("Unsupported trace recording version.");
	const runs = new Map<string, MutableRun>();
	const stages = new Map<string, MutableStage>();
	const tools = new Map<string, MutableTool>();
	const actions = new Map<string, MutableAction>();
	const byProvider = new Map<string, MutableUsage>();
	const byModel = new Map<string, MutableUsage>();
	for (const record of [...recording.records].sort((left, right) => left.sequence - right.sequence)) {
		const run = getRun(runs, record.runId, record.inputId);
		if (record.kind === "stage") applyStage(record, run, stages);
		else if (record.kind === "event") applyEvent(record, run, tools, byProvider, byModel);
		else applyAction(record, actions);
	}

	const summaries = [...runs.values()].map(finalizeRun);
	const usage = emptyUsage();
	const byErrorCategory: Record<string, number> = {};
	for (const run of summaries) {
		mergeUsage(usage, run.usage);
		if (run.errorCategory) byErrorCategory[run.errorCategory] = (byErrorCategory[run.errorCategory] ?? 0) + 1;
	}
	for (const record of recording.records) {
		if (record.kind === "action" && record.errorCategory) {
			byErrorCategory[record.errorCategory] = (byErrorCategory[record.errorCategory] ?? 0) + 1;
		}
	}
	const toolCalls = summaries.reduce((total, run) => total + run.toolCalls, 0);
	const toolFailures = summaries.reduce((total, run) => total + run.toolFailures, 0);
	const actionMetrics = [...actions.values()];
	return {
		schemaVersion: 1,
		runs: summaries,
		runLatency: distribution(
			summaries.flatMap((run) => (run.totalMilliseconds === null ? [] : [run.totalMilliseconds])),
		),
		timeToFirstOutput: distribution(
			summaries.flatMap((run) =>
				run.timeToFirstOutputMilliseconds === null ? [] : [run.timeToFirstOutputMilliseconds],
			),
		),
		timeToFirstTool: distribution(
			summaries.flatMap((run) => (run.timeToFirstToolMilliseconds === null ? [] : [run.timeToFirstToolMilliseconds])),
		),
		toolCalls,
		toolFailures,
		toolFailureRate: toolCalls === 0 ? 0 : toolFailures / toolCalls,
		actionDispatches: actionMetrics.reduce((total, action) => total + action.dispatches, 0),
		uncertainWrites: actionMetrics.reduce((total, action) => total + action.uncertainWrites, 0),
		duplicateWritesPrevented: actionMetrics.reduce((total, action) => total + action.duplicateWritesPrevented, 0),
		reconciliations: actionMetrics.reduce((total, action) => total + action.reconciled, 0),
		completedRuns: summaries.filter((run) => run.outcome === "ok").length,
		failedRuns: summaries.filter((run) => run.outcome === "error").length,
		cancelledRuns: summaries.filter((run) => run.outcome === "cancelled").length,
		usage: finalizeUsage(usage),
		stages: [...stages.values()]
			.map((stage) => ({ ...stage }))
			.sort(
				(left, right) => left.stage.localeCompare(right.stage) || (left.name ?? "").localeCompare(right.name ?? ""),
			),
		tools: [...tools.values()]
			.map((tool) => ({
				tool: tool.tool,
				calls: tool.calls,
				succeeded: tool.succeeded,
				failed: tool.failed,
				completed: tool.completed,
				meanMilliseconds:
					tool.durations.length === 0
						? null
						: tool.durations.reduce((total, value) => total + value, 0) / tool.durations.length,
				maximumMilliseconds: tool.durations.length === 0 ? null : Math.max(...tool.durations),
			}))
			.sort((left, right) => left.tool.localeCompare(right.tool)),
		actions: [...actions.values()]
			.map((action) => ({ ...action }))
			.sort((left, right) => left.action.localeCompare(right.action)),
		byProvider: finalizeUsageMap(byProvider),
		byModel: finalizeUsageMap(byModel),
		byErrorCategory,
	};
}

function applyAction(record: GameTraceActionRecord, actions: Map<string, MutableAction>): void {
	let metric = actions.get(record.action);
	if (!metric) {
		metric = {
			action: record.action,
			dispatches: 0,
			executed: 0,
			duplicateWritesPrevented: 0,
			reconcileRequired: 0,
			conflictBlocked: 0,
			uncertainWrites: 0,
			reconciled: 0,
			failedBeforeDispatch: 0,
			totalFrameworkMilliseconds: 0,
			totalHostMilliseconds: 0,
			maximumMilliseconds: 0,
		};
		actions.set(record.action, metric);
	}
	metric.dispatches += 1;
	metric.totalFrameworkMilliseconds += record.frameworkMilliseconds;
	metric.totalHostMilliseconds += record.hostMilliseconds;
	metric.maximumMilliseconds = Math.max(metric.maximumMilliseconds, record.durationMilliseconds);
	switch (record.disposition) {
		case "executed":
			metric.executed += 1;
			break;
		case "duplicate-prevented":
			metric.duplicateWritesPrevented += 1;
			break;
		case "reconcile-required":
			metric.reconcileRequired += 1;
			break;
		case "conflict-blocked":
			metric.conflictBlocked += 1;
			break;
		case "uncertain":
			metric.uncertainWrites += 1;
			break;
		case "reconciled":
			metric.reconciled += 1;
			break;
		case "failed-before-dispatch":
			metric.failedBeforeDispatch += 1;
			break;
	}
}

function getRun(runs: Map<string, MutableRun>, runId: string, inputId: string): MutableRun {
	let run = runs.get(runId);
	if (!run) {
		run = {
			runId,
			inputId,
			outcome: "unknown",
			totalMilliseconds: null,
			queueMilliseconds: 0,
			preparationMilliseconds: 0,
			toolStarts: new Map(),
			toolCalls: 0,
			toolFailures: 0,
			usage: emptyUsage(),
		};
		runs.set(runId, run);
	}
	return run;
}

function applyStage(record: GameTraceStageRecord, run: MutableRun, stages: Map<string, MutableStage>): void {
	const key = `${record.stage}\u0000${record.name ?? ""}`;
	let aggregate = stages.get(key);
	if (!aggregate) {
		aggregate = {
			stage: record.stage,
			...(record.name === undefined ? {} : { name: record.name }),
			count: 0,
			totalMilliseconds: 0,
			maximumMilliseconds: 0,
			errorCount: 0,
			cancelledCount: 0,
		};
		stages.set(key, aggregate);
	}
	aggregate.count += 1;
	aggregate.totalMilliseconds += record.durationMilliseconds;
	aggregate.maximumMilliseconds = Math.max(aggregate.maximumMilliseconds, record.durationMilliseconds);
	if (record.outcome === "error") aggregate.errorCount += 1;
	if (record.outcome === "cancelled") aggregate.cancelledCount += 1;
	if (record.stage === "run") {
		run.totalMilliseconds = record.durationMilliseconds;
		run.outcome = record.outcome === "ok" ? "ok" : record.outcome;
		if (record.errorCategory) run.errorCategory = record.errorCategory;
	} else if (record.stage === "queue") run.queueMilliseconds += record.durationMilliseconds;
	else if (record.stage === "prepare-turn") run.preparationMilliseconds += record.durationMilliseconds;
}

function applyEvent(
	record: GameTraceEventRecord,
	run: MutableRun,
	tools: Map<string, MutableTool>,
	byProvider: Map<string, MutableUsage>,
	byModel: Map<string, MutableUsage>,
): void {
	if (record.eventType === "run.started") {
		const provider = stringAttribute(record.attributes, "provider");
		const model = stringAttribute(record.attributes, "model");
		if (provider !== undefined) run.provider = provider;
		if (model !== undefined) run.model = model;
	} else if (record.eventType === "run.failed") {
		run.outcome = "error";
		run.errorCategory = stringAttribute(record.attributes, "errorCategory") ?? "unknown";
	} else if (record.eventType === "run.aborted") run.outcome = "cancelled";
	else if (record.eventType === "run.completed" && run.outcome === "unknown") run.outcome = "ok";
	if (record.eventType === "turn.started" && run.firstTurnAt === undefined) run.firstTurnAt = record.timestamp;
	if (
		(record.eventType === "message.delta" ||
			record.eventType === "message.completed" ||
			record.eventType === "tool.started") &&
		run.firstOutputAt === undefined
	) {
		run.firstOutputAt = record.timestamp;
	}
	if (record.eventType === "message.completed") {
		const usage = objectAttribute(record.attributes, "usage");
		const provider = stringAttribute(record.attributes, "provider") ?? run.provider;
		const model = stringAttribute(record.attributes, "model") ?? run.model;
		if (provider !== undefined) run.provider = provider;
		if (model !== undefined) run.model = model;
		if (usage) {
			addUsage(run.usage, usage);
			if (provider !== undefined) addUsage(getUsage(byProvider, provider), usage);
			if (model !== undefined) addUsage(getUsage(byModel, model), usage);
		}
	}
	if (record.eventType === "tool.started") {
		const callId = stringAttribute(record.attributes, "callId");
		const toolName = stringAttribute(record.attributes, "tool");
		if (!callId || !toolName) return;
		run.toolCalls += 1;
		if (run.firstToolAt === undefined) run.firstToolAt = record.timestamp;
		run.toolStarts.set(callId, { tool: toolName, timestamp: record.timestamp });
		getTool(tools, toolName).calls += 1;
	}
	if (record.eventType === "tool.completed") {
		const callId = stringAttribute(record.attributes, "callId");
		if (!callId) return;
		const started = run.toolStarts.get(callId);
		if (!started) return;
		const metric = getTool(tools, started.tool);
		const failed = booleanAttribute(record.attributes, "isError") ?? false;
		metric.completed += 1;
		if (failed) {
			metric.failed += 1;
			run.toolFailures += 1;
		} else metric.succeeded += 1;
		metric.durations.push(Math.max(0, record.timestamp - started.timestamp));
	}
}

function finalizeRun(run: MutableRun): GameRunPerformanceSummary {
	return {
		runId: run.runId,
		inputId: run.inputId,
		...(run.provider === undefined ? {} : { provider: run.provider }),
		...(run.model === undefined ? {} : { model: run.model }),
		outcome: run.outcome,
		...(run.errorCategory === undefined ? {} : { errorCategory: run.errorCategory }),
		totalMilliseconds: run.totalMilliseconds,
		queueMilliseconds: run.queueMilliseconds,
		preparationMilliseconds: run.preparationMilliseconds,
		timeToFirstOutputMilliseconds:
			run.firstTurnAt === undefined || run.firstOutputAt === undefined
				? null
				: Math.max(0, run.firstOutputAt - run.firstTurnAt),
		timeToFirstToolMilliseconds:
			run.firstTurnAt === undefined || run.firstToolAt === undefined
				? null
				: Math.max(0, run.firstToolAt - run.firstTurnAt),
		toolCalls: run.toolCalls,
		toolFailures: run.toolFailures,
		usage: finalizeUsage(run.usage),
	};
}

function emptyUsage(): MutableUsage {
	return {
		records: 0,
		input: 0,
		output: 0,
		cacheRead: 0,
		cacheWrite: 0,
		reasoning: 0,
		totalTokens: 0,
		unknownCostRecords: 0,
		knownCost: 0,
	};
}

function addUsage(target: MutableUsage, value: JsonObject): void {
	target.records += 1;
	target.input += numberAttribute(value, "input") ?? 0;
	target.output += numberAttribute(value, "output") ?? 0;
	target.cacheRead += numberAttribute(value, "cacheRead") ?? 0;
	target.cacheWrite += numberAttribute(value, "cacheWrite") ?? 0;
	target.reasoning += numberAttribute(value, "reasoning") ?? 0;
	target.totalTokens += numberAttribute(value, "totalTokens") ?? 0;
	const cost = objectAttribute(value, "cost");
	const totalCost = cost ? numberAttribute(cost, "total") : undefined;
	if (totalCost === undefined) target.unknownCostRecords += 1;
	else target.knownCost += totalCost;
}

function mergeUsage(target: MutableUsage, source: GameUsageMetric): void {
	target.records += source.records;
	target.input += source.input;
	target.output += source.output;
	target.cacheRead += source.cacheRead;
	target.cacheWrite += source.cacheWrite;
	target.reasoning += source.reasoning;
	target.totalTokens += source.totalTokens;
	target.unknownCostRecords += source.unknownCostRecords;
	if (source.cost !== null) target.knownCost += source.cost;
}

function finalizeUsage(value: MutableUsage): GameUsageMetric {
	return {
		records: value.records,
		input: value.input,
		output: value.output,
		cacheRead: value.cacheRead,
		cacheWrite: value.cacheWrite,
		reasoning: value.reasoning,
		totalTokens: value.totalTokens,
		unknownCostRecords: value.unknownCostRecords,
		cost: value.unknownCostRecords === 0 ? value.knownCost : null,
	};
}

function getUsage(values: Map<string, MutableUsage>, key: string): MutableUsage {
	let value = values.get(key);
	if (!value) {
		value = emptyUsage();
		values.set(key, value);
	}
	return value;
}

function finalizeUsageMap(values: Map<string, MutableUsage>): Readonly<Record<string, GameUsageMetric>> {
	return Object.fromEntries(
		[...values].sort(([left], [right]) => left.localeCompare(right)).map(([key, value]) => [key, finalizeUsage(value)]),
	);
}

function getTool(values: Map<string, MutableTool>, tool: string): MutableTool {
	let value = values.get(tool);
	if (!value) {
		value = { tool, calls: 0, succeeded: 0, failed: 0, completed: 0, durations: [] };
		values.set(tool, value);
	}
	return value;
}

function distribution(values: readonly number[]): GameLatencyDistribution {
	const sorted = [...values].filter(Number.isFinite).sort((left, right) => left - right);
	return {
		count: sorted.length,
		mean: sorted.length === 0 ? null : sorted.reduce((total, value) => total + value, 0) / sorted.length,
		p50: percentile(sorted, 0.5),
		p95: percentile(sorted, 0.95),
		p99: percentile(sorted, 0.99),
		maximum: sorted.at(-1) ?? null,
	};
}

function percentile(sorted: readonly number[], percentileValue: number): number | null {
	if (sorted.length === 0) return null;
	const index = Math.max(0, Math.ceil(sorted.length * percentileValue) - 1);
	return sorted[index] ?? null;
}

function stringAttribute(value: JsonObject, key: string): string | undefined {
	const candidate = value[key];
	return typeof candidate === "string" ? candidate : undefined;
}

function numberAttribute(value: JsonObject, key: string): number | undefined {
	const candidate = value[key];
	return typeof candidate === "number" && Number.isFinite(candidate) ? candidate : undefined;
}

function booleanAttribute(value: JsonObject, key: string): boolean | undefined {
	const candidate = value[key];
	return typeof candidate === "boolean" ? candidate : undefined;
}

function objectAttribute(value: JsonObject, key: string): JsonObject | undefined {
	const candidate: JsonValue | undefined = value[key];
	return candidate !== null && typeof candidate === "object" && !Array.isArray(candidate) ? candidate : undefined;
}

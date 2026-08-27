import { writeFile } from "node:fs/promises";
import type { GameLatencyDistribution, GamePerformanceSummary } from "./performance.js";
import { summarizeGamePerformance } from "./performance.js";
import type { GameTraceRecording } from "./trace.js";

export interface GameBenchmarkIterationContext {
	iteration: number;
	warmup: boolean;
	signal: AbortSignal;
}

export interface GameBenchmarkScenario {
	name: string;
	run(context: GameBenchmarkIterationContext): Promise<GameTraceRecording>;
}

export interface GameBenchmarkFaultInjector {
	beforeIteration(context: GameBenchmarkIterationContext): Promise<void> | void;
}

export interface GameBenchmarkThresholds {
	maximumP95RunMilliseconds?: number;
	maximumP95TimeToFirstOutputMilliseconds?: number;
	maximumToolFailureRate?: number;
	maximumFailedIterations?: number;
}

export interface GameBenchmarkOptions {
	iterations?: number;
	warmupIterations?: number;
	concurrency?: number;
	iterationTimeoutMilliseconds?: number;
	faultInjector?: GameBenchmarkFaultInjector;
	thresholds?: GameBenchmarkThresholds;
	signal?: AbortSignal;
}

export interface GameBenchmarkIterationResult {
	iteration: number;
	status: "completed" | "failed" | "timed-out" | "cancelled";
	durationMilliseconds: number;
	performance?: GamePerformanceSummary;
	errorCategory?: string;
}

export interface GameBenchmarkReport {
	schemaVersion: 1;
	scenario: string;
	startedAt: number;
	completedAt: number;
	iterations: readonly GameBenchmarkIterationResult[];
	failedIterations: number;
	runLatency: GameLatencyDistribution;
	timeToFirstOutput: GameLatencyDistribution;
	toolCalls: number;
	toolFailures: number;
	toolFailureRate: number;
	actionDispatches: number;
	uncertainWrites: number;
	duplicateWritesPrevented: number;
	thresholdsPassed: boolean;
	thresholdFailures: readonly string[];
}

export async function runGameBenchmark(
	scenario: GameBenchmarkScenario,
	options: GameBenchmarkOptions = {},
): Promise<GameBenchmarkReport> {
	if (scenario.name.length === 0 || scenario.name.length > 256) throw new TypeError("Scenario name must be bounded.");
	const iterations = boundedInteger(options.iterations ?? 1, "iterations", 1, 100_000);
	const warmupIterations = boundedInteger(options.warmupIterations ?? 0, "warmupIterations", 0, 10_000);
	const concurrency = boundedInteger(options.concurrency ?? 1, "concurrency", 1, 256);
	const timeout = boundedInteger(
		options.iterationTimeoutMilliseconds ?? 60_000,
		"iterationTimeoutMilliseconds",
		1,
		24 * 60 * 60 * 1000,
	);
	validateThresholds(options.thresholds);
	const signal = options.signal ?? new AbortController().signal;
	for (let index = 0; index < warmupIterations; index += 1) {
		signal.throwIfAborted();
		const result = await executeIteration(scenario, index, true, timeout, options.faultInjector, signal);
		if (result.status !== "completed")
			throw new Error(`Benchmark warmup failed with category '${result.errorCategory ?? "unknown"}'.`);
	}

	const startedAt = Date.now();
	const results: GameBenchmarkIterationResult[] = new Array(iterations);
	let next = 0;
	const workers = Array.from({ length: Math.min(concurrency, iterations) }, async () => {
		while (true) {
			const iteration = next;
			next += 1;
			if (iteration >= iterations) return;
			signal.throwIfAborted();
			results[iteration] = await executeIteration(scenario, iteration, false, timeout, options.faultInjector, signal);
		}
	});
	await Promise.all(workers);
	signal.throwIfAborted();
	const completedAt = Date.now();
	const completed = results.filter((result) => result !== undefined);
	const runValues = completed.flatMap(
		(result) =>
			result.performance?.runs.flatMap((run) => (run.totalMilliseconds === null ? [] : [run.totalMilliseconds])) ?? [],
	);
	const ttftValues = completed.flatMap(
		(result) =>
			result.performance?.runs.flatMap((run) =>
				run.timeToFirstOutputMilliseconds === null ? [] : [run.timeToFirstOutputMilliseconds],
			) ?? [],
	);
	const toolCalls = completed.reduce((total, result) => total + (result.performance?.toolCalls ?? 0), 0);
	const toolFailures = completed.reduce((total, result) => total + (result.performance?.toolFailures ?? 0), 0);
	const actionDispatches = completed.reduce((total, result) => total + (result.performance?.actionDispatches ?? 0), 0);
	const uncertainWrites = completed.reduce((total, result) => total + (result.performance?.uncertainWrites ?? 0), 0);
	const duplicateWritesPrevented = completed.reduce(
		(total, result) => total + (result.performance?.duplicateWritesPrevented ?? 0),
		0,
	);
	const failedIterations = completed.filter((result) => result.status !== "completed").length;
	const runLatency = distribution(runValues);
	const timeToFirstOutput = distribution(ttftValues);
	const toolFailureRate = toolCalls === 0 ? 0 : toolFailures / toolCalls;
	const thresholdFailures = evaluateThresholds(
		options.thresholds,
		failedIterations,
		runLatency,
		timeToFirstOutput,
		toolFailureRate,
	);
	return {
		schemaVersion: 1,
		scenario: scenario.name,
		startedAt,
		completedAt,
		iterations: completed,
		failedIterations,
		runLatency,
		timeToFirstOutput,
		toolCalls,
		toolFailures,
		toolFailureRate,
		actionDispatches,
		uncertainWrites,
		duplicateWritesPrevented,
		thresholdsPassed: thresholdFailures.length === 0,
		thresholdFailures,
	};
}

export async function writeGameBenchmarkJson(path: string, report: GameBenchmarkReport): Promise<void> {
	await writeFile(path, `${JSON.stringify(report, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
}

export async function writeGameBenchmarkJsonLines(path: string, report: GameBenchmarkReport): Promise<void> {
	const lines = report.iterations.map((iteration) =>
		JSON.stringify({ schemaVersion: 1, scenario: report.scenario, ...iteration }),
	);
	await writeFile(path, `${lines.join("\n")}\n`, { encoding: "utf8", flag: "wx" });
}

export function formatGameBenchmarkReport(report: GameBenchmarkReport): string {
	return [
		`Scenario: ${report.scenario}`,
		`Iterations: ${report.iterations.length} (${report.failedIterations} failed)`,
		`Run latency p50/p95/p99: ${metric(report.runLatency.p50)} / ${metric(report.runLatency.p95)} / ${metric(report.runLatency.p99)} ms`,
		`First output p50/p95/p99: ${metric(report.timeToFirstOutput.p50)} / ${metric(report.timeToFirstOutput.p95)} / ${metric(report.timeToFirstOutput.p99)} ms`,
		`Tools: ${report.toolCalls} calls, ${report.toolFailures} failures (${(report.toolFailureRate * 100).toFixed(2)}%)`,
		`Actions: ${report.actionDispatches} dispatches, ${report.uncertainWrites} uncertain, ${report.duplicateWritesPrevented} duplicate writes prevented`,
		`Thresholds: ${report.thresholdsPassed ? "PASS" : "FAIL"}`,
		...report.thresholdFailures.map((failure) => `- ${failure}`),
	].join("\n");
}

async function executeIteration(
	scenario: GameBenchmarkScenario,
	iteration: number,
	warmup: boolean,
	timeout: number,
	faultInjector: GameBenchmarkFaultInjector | undefined,
	parentSignal: AbortSignal,
): Promise<GameBenchmarkIterationResult> {
	const controller = new AbortController();
	const forwardAbort = () => controller.abort(parentSignal.reason);
	parentSignal.addEventListener("abort", forwardAbort, { once: true });
	const started = performance.now();
	let timer: ReturnType<typeof setTimeout> | undefined;
	try {
		const context = { iteration, warmup, signal: controller.signal };
		const recording = await Promise.race([
			Promise.resolve().then(async () => {
				await faultInjector?.beforeIteration(context);
				return await scenario.run(context);
			}),
			new Promise<never>((_resolve, reject) => {
				timer = setTimeout(() => {
					const error = new DOMException("Benchmark iteration timed out.", "TimeoutError");
					controller.abort(error);
					reject(error);
				}, timeout);
			}),
		]);
		controller.signal.throwIfAborted();
		return {
			iteration,
			status: "completed",
			durationMilliseconds: performance.now() - started,
			performance: summarizeGamePerformance(recording),
		};
	} catch (error) {
		const category = safeErrorCategory(error);
		const status = parentSignal.aborted
			? "cancelled"
			: controller.signal.aborted &&
					controller.signal.reason instanceof DOMException &&
					controller.signal.reason.name === "TimeoutError"
				? "timed-out"
				: "failed";
		return {
			iteration,
			status,
			durationMilliseconds: performance.now() - started,
			errorCategory: category,
		};
	} finally {
		if (timer !== undefined) clearTimeout(timer);
		parentSignal.removeEventListener("abort", forwardAbort);
	}
}

function evaluateThresholds(
	thresholds: GameBenchmarkThresholds | undefined,
	failedIterations: number,
	runLatency: GameLatencyDistribution,
	ttft: GameLatencyDistribution,
	toolFailureRate: number,
): string[] {
	if (!thresholds) return [];
	const failures: string[] = [];
	if (
		thresholds.maximumP95RunMilliseconds !== undefined &&
		runLatency.p95 !== null &&
		runLatency.p95 > thresholds.maximumP95RunMilliseconds
	) {
		failures.push(`Run p95 exceeded ${thresholds.maximumP95RunMilliseconds} ms.`);
	}
	if (
		thresholds.maximumP95TimeToFirstOutputMilliseconds !== undefined &&
		ttft.p95 !== null &&
		ttft.p95 > thresholds.maximumP95TimeToFirstOutputMilliseconds
	) {
		failures.push(`First-output p95 exceeded ${thresholds.maximumP95TimeToFirstOutputMilliseconds} ms.`);
	}
	if (thresholds.maximumToolFailureRate !== undefined && toolFailureRate > thresholds.maximumToolFailureRate) {
		failures.push(`Tool failure rate exceeded ${thresholds.maximumToolFailureRate}.`);
	}
	if (thresholds.maximumFailedIterations !== undefined && failedIterations > thresholds.maximumFailedIterations) {
		failures.push(`Failed iterations exceeded ${thresholds.maximumFailedIterations}.`);
	}
	return failures;
}

function validateThresholds(thresholds: GameBenchmarkThresholds | undefined): void {
	if (!thresholds) return;
	for (const [name, value] of [
		["maximumP95RunMilliseconds", thresholds.maximumP95RunMilliseconds],
		["maximumP95TimeToFirstOutputMilliseconds", thresholds.maximumP95TimeToFirstOutputMilliseconds],
	] as const) {
		if (value !== undefined && (!Number.isFinite(value) || value < 0))
			throw new RangeError(`${name} must be non-negative.`);
	}
	if (
		thresholds.maximumToolFailureRate !== undefined &&
		(!Number.isFinite(thresholds.maximumToolFailureRate) ||
			thresholds.maximumToolFailureRate < 0 ||
			thresholds.maximumToolFailureRate > 1)
	) {
		throw new RangeError("maximumToolFailureRate must be between 0 and 1.");
	}
	if (thresholds.maximumFailedIterations !== undefined) {
		boundedInteger(thresholds.maximumFailedIterations, "maximumFailedIterations", 0, 100_000);
	}
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

function percentile(sorted: readonly number[], fraction: number): number | null {
	if (sorted.length === 0) return null;
	return sorted[Math.max(0, Math.ceil(sorted.length * fraction) - 1)] ?? null;
}

function metric(value: number | null): string {
	return value === null ? "unknown" : value.toFixed(2);
}

function safeErrorCategory(error: unknown): string {
	if (error instanceof DOMException && error.name === "AbortError") return "aborted";
	if (error instanceof DOMException && error.name === "TimeoutError") return "timeout";
	if (error instanceof Error && /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/u.test(error.name)) return error.name;
	return "unknown";
}

function boundedInteger(value: number, name: string, minimum: number, maximum: number): number {
	if (!Number.isInteger(value) || value < minimum || value > maximum) {
		throw new RangeError(`${name} must be an integer from ${minimum} through ${maximum}.`);
	}
	return value;
}

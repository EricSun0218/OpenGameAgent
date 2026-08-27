import type { GamePerformanceSummary } from "./performance.js";
import { summarizeGamePerformance } from "./performance.js";
import type { GameTraceRecording } from "./trace.js";

export interface GameTraceEvaluationFinding {
	rule: string;
	severity: "error" | "warning";
	message: string;
	runId?: string;
}

export interface GameTraceEvaluationRuleContext {
	recording: GameTraceRecording;
	performance: GamePerformanceSummary;
}

export interface GameTraceEvaluationRuleFinding {
	severity: "error" | "warning";
	message: string;
	runId?: string;
}

export interface GameTraceEvaluationRule {
	name: string;
	evaluate(
		context: GameTraceEvaluationRuleContext,
		signal: AbortSignal,
	): Promise<readonly GameTraceEvaluationRuleFinding[]> | readonly GameTraceEvaluationRuleFinding[];
}

export interface GameTraceEvaluationSpec {
	maximumRunMilliseconds?: number;
	maximumTimeToFirstOutputMilliseconds?: number;
	maximumToolFailureRate?: number;
	maximumUnknownCostRecords?: number;
	requiredEventTypes?: readonly string[];
	forbiddenEventTypes?: readonly string[];
	forbiddenTools?: readonly string[];
	rules?: readonly GameTraceEvaluationRule[];
	ruleTimeoutMilliseconds?: number;
	maximumRuleFindings?: number;
}

export interface GameTraceEvaluationReport {
	schemaVersion: 1;
	passed: boolean;
	findings: readonly GameTraceEvaluationFinding[];
	performance: GamePerformanceSummary;
}

export async function evaluateGameTrace(
	recording: GameTraceRecording,
	spec: GameTraceEvaluationSpec,
	signal: AbortSignal = new AbortController().signal,
): Promise<GameTraceEvaluationReport> {
	const performance = summarizeGamePerformance(recording);
	const findings: GameTraceEvaluationFinding[] = [];
	const maximumRun = optionalNonNegative(spec.maximumRunMilliseconds, "maximumRunMilliseconds");
	const maximumTtft = optionalNonNegative(
		spec.maximumTimeToFirstOutputMilliseconds,
		"maximumTimeToFirstOutputMilliseconds",
	);
	const maximumFailureRate = optionalRate(spec.maximumToolFailureRate, "maximumToolFailureRate");
	const maximumUnknownCost = optionalInteger(spec.maximumUnknownCostRecords, "maximumUnknownCostRecords");

	for (const run of performance.runs) {
		if (maximumRun !== undefined && run.totalMilliseconds !== null && run.totalMilliseconds > maximumRun) {
			findings.push({
				rule: "maximum-run-duration",
				severity: "error",
				message: `Run exceeded ${maximumRun} ms.`,
				runId: run.runId,
			});
		}
		if (
			maximumTtft !== undefined &&
			run.timeToFirstOutputMilliseconds !== null &&
			run.timeToFirstOutputMilliseconds > maximumTtft
		) {
			findings.push({
				rule: "maximum-time-to-first-output",
				severity: "error",
				message: `Run exceeded ${maximumTtft} ms before first output.`,
				runId: run.runId,
			});
		}
	}
	if (maximumFailureRate !== undefined && performance.toolFailureRate > maximumFailureRate) {
		findings.push({
			rule: "maximum-tool-failure-rate",
			severity: "error",
			message: `Tool failure rate exceeded ${maximumFailureRate}.`,
		});
	}
	if (maximumUnknownCost !== undefined && performance.usage.unknownCostRecords > maximumUnknownCost) {
		findings.push({
			rule: "maximum-unknown-cost-records",
			severity: "error",
			message: `Unknown cost records exceeded ${maximumUnknownCost}.`,
		});
	}

	const eventTypes = new Set<string>(
		recording.records.flatMap((record) => (record.kind === "event" ? [record.eventType] : [])),
	);
	for (const required of boundedNames(spec.requiredEventTypes, "requiredEventTypes")) {
		if (!eventTypes.has(required)) {
			findings.push({
				rule: "required-event",
				severity: "error",
				message: `Required event '${required}' was not observed.`,
			});
		}
	}
	for (const forbidden of boundedNames(spec.forbiddenEventTypes, "forbiddenEventTypes")) {
		if (eventTypes.has(forbidden)) {
			findings.push({
				rule: "forbidden-event",
				severity: "error",
				message: `Forbidden event '${forbidden}' was observed.`,
			});
		}
	}
	const forbiddenTools = new Set(boundedNames(spec.forbiddenTools, "forbiddenTools"));
	for (const tool of performance.tools) {
		if (forbiddenTools.has(tool.tool)) {
			findings.push({
				rule: "forbidden-tool",
				severity: "error",
				message: `Forbidden tool '${tool.tool}' was called.`,
			});
		}
	}

	const ruleTimeoutMilliseconds = boundedInteger(
		spec.ruleTimeoutMilliseconds ?? 5_000,
		"ruleTimeoutMilliseconds",
		1,
		60_000,
	);
	const ruleNames = new Set<string>();
	const maximumRuleFindings = boundedInteger(spec.maximumRuleFindings ?? 1_024, "maximumRuleFindings", 1, 10_000);
	for (const rule of spec.rules ?? []) {
		signal.throwIfAborted();
		if (!validName(rule.name) || ruleNames.has(rule.name))
			throw new TypeError("Evaluation rule names must be unique and bounded.");
		ruleNames.add(rule.name);
		try {
			const customFindings = await withTimeout(
				(ruleSignal) => rule.evaluate({ recording, performance }, ruleSignal),
				ruleTimeoutMilliseconds,
				signal,
			);
			if (customFindings.length > maximumRuleFindings)
				throw new RangeError("Evaluation rule returned too many findings.");
			findings.push(...customFindings.map((finding) => normalizeRuleFinding(rule.name, finding)));
		} catch (error) {
			if (signal.aborted) throw error;
			findings.push({ rule: rule.name, severity: "error", message: "Evaluation rule failed or timed out." });
		}
	}
	return { schemaVersion: 1, passed: findings.every((finding) => finding.severity !== "error"), findings, performance };
}

function normalizeRuleFinding(rule: string, finding: GameTraceEvaluationRuleFinding): GameTraceEvaluationFinding {
	if (finding.severity !== "error" && finding.severity !== "warning") {
		throw new TypeError("Evaluation finding severity is invalid.");
	}
	if (finding.message.length < 1 || finding.message.length > 4_096) {
		throw new TypeError("Evaluation finding message must be bounded.");
	}
	if (finding.runId !== undefined && !validName(finding.runId)) {
		throw new TypeError("Evaluation finding runId must be bounded.");
	}
	return {
		rule,
		severity: finding.severity,
		message: finding.message,
		...(finding.runId === undefined ? {} : { runId: finding.runId }),
	};
}

async function withTimeout<T>(
	operation: (signal: AbortSignal) => Promise<T> | T,
	milliseconds: number,
	signal: AbortSignal,
): Promise<T> {
	const controller = new AbortController();
	const forwardAbort = () => controller.abort(signal.reason);
	signal.addEventListener("abort", forwardAbort, { once: true });
	let timer: ReturnType<typeof setTimeout> | undefined;
	try {
		return await Promise.race([
			Promise.resolve().then(() => operation(controller.signal)),
			new Promise<T>((_resolve, reject) => {
				timer = setTimeout(() => {
					controller.abort(new DOMException("Timed out.", "TimeoutError"));
					reject(new DOMException("Timed out.", "TimeoutError"));
				}, milliseconds);
			}),
		]);
	} finally {
		if (timer !== undefined) clearTimeout(timer);
		signal.removeEventListener("abort", forwardAbort);
	}
}

function boundedNames(values: readonly string[] | undefined, name: string): readonly string[] {
	if (!values) return [];
	if (values.length > 256 || values.some((value) => !validName(value)))
		throw new TypeError(`${name} must contain bounded names.`);
	return values;
}

function validName(value: string): boolean {
	return value.length > 0 && value.length <= 256;
}

function optionalNonNegative(value: number | undefined, name: string): number | undefined {
	if (value === undefined) return undefined;
	if (!Number.isFinite(value) || value < 0) throw new RangeError(`${name} must be finite and non-negative.`);
	return value;
}

function optionalRate(value: number | undefined, name: string): number | undefined {
	if (value === undefined) return undefined;
	if (!Number.isFinite(value) || value < 0 || value > 1) throw new RangeError(`${name} must be between 0 and 1.`);
	return value;
}

function optionalInteger(value: number | undefined, name: string): number | undefined {
	if (value === undefined) return undefined;
	return boundedInteger(value, name, 0, 1_000_000);
}

function boundedInteger(value: number, name: string, minimum: number, maximum: number): number {
	if (!Number.isInteger(value) || value < minimum || value > maximum) {
		throw new RangeError(`${name} must be an integer from ${minimum} through ${maximum}.`);
	}
	return value;
}

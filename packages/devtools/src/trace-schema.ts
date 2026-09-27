import { assertCanonicalTimestamp } from "@opengameagent/protocol";

export const TRACE_SCHEMA_VERSION = "1" as const;

export const TRACE_COMPONENTS = [
	"agent",
	"runtime",
	"storage",
	"server",
	"client",
	"actions",
	"extensions",
	"memory",
	"media",
	"realtime",
	"integrations",
	"engine",
] as const;
export type TraceComponent = (typeof TRACE_COMPONENTS)[number];

export const TRACE_OPERATIONS = [
	"queue",
	"admission",
	"context",
	"memory.lexical",
	"memory.vector",
	"memory.rerank",
	"provider.first-token",
	"provider.complete",
	"tool.first",
	"tool.execute",
	"action.wait",
	"approval.wait",
	"compaction",
	"retry",
	"replan",
	"speech.stt",
	"speech.tts",
	"media.generate",
] as const;
export type TraceOperation = (typeof TRACE_OPERATIONS)[number];

export const TRACE_OUTCOMES = ["ok", "error", "cancelled", "rejected", "uncertain"] as const;
export type TraceOutcome = (typeof TRACE_OUTCOMES)[number];

export const TRACE_ERROR_KINDS = [
	"none",
	"configuration",
	"capacity",
	"timeout",
	"cancelled",
	"provider",
	"tool",
	"authority",
	"transport",
	"storage",
	"malformed",
	"unknown",
] as const;
export type TraceErrorKind = (typeof TRACE_ERROR_KINDS)[number];

export const TRACE_MEASUREMENTS = [
	"attempt",
	"queueDepth",
	"itemCount",
	"inputBytes",
	"outputBytes",
	"tokenCount",
] as const;
export type TraceMeasurement = (typeof TRACE_MEASUREMENTS)[number];
export type TraceMeasurements = Partial<Record<TraceMeasurement, number>>;

export interface SafeTraceRecord {
	readonly schemaVersion: typeof TRACE_SCHEMA_VERSION;
	readonly sequence: string;
	readonly component: TraceComponent;
	readonly operation: TraceOperation;
	readonly startedAt: string;
	readonly durationMs: number;
	readonly outcome: TraceOutcome;
	readonly errorKind: TraceErrorKind;
	readonly measurements: TraceMeasurements;
}

const COMPONENT_SET = new Set<string>(TRACE_COMPONENTS);
const OPERATION_SET = new Set<string>(TRACE_OPERATIONS);
const OUTCOME_SET = new Set<string>(TRACE_OUTCOMES);
const ERROR_KIND_SET = new Set<string>(TRACE_ERROR_KINDS);
const MEASUREMENT_SET = new Set<string>(TRACE_MEASUREMENTS);
const RECORD_KEYS = new Set([
	"schemaVersion",
	"sequence",
	"component",
	"operation",
	"startedAt",
	"durationMs",
	"outcome",
	"errorKind",
	"measurements",
]);

export class SafeTraceValidationError extends TypeError {
	constructor() {
		super("The trace record does not satisfy the content-safe schema.");
		this.name = "SafeTraceValidationError";
	}
}

export function assertSafeTraceRecord(value: unknown): asserts value is SafeTraceRecord {
	if (!isPlainObject(value) || Object.keys(value).some((key) => !RECORD_KEYS.has(key))) fail();
	if (
		value.schemaVersion !== TRACE_SCHEMA_VERSION ||
		typeof value.sequence !== "string" ||
		!isCanonicalDecimal(value.sequence) ||
		typeof value.component !== "string" ||
		!COMPONENT_SET.has(value.component) ||
		typeof value.operation !== "string" ||
		!OPERATION_SET.has(value.operation) ||
		typeof value.startedAt !== "string" ||
		!isCanonicalTimestamp(value.startedAt) ||
		!isNonNegativeFinite(value.durationMs) ||
		typeof value.outcome !== "string" ||
		!OUTCOME_SET.has(value.outcome) ||
		typeof value.errorKind !== "string" ||
		!ERROR_KIND_SET.has(value.errorKind) ||
		!isPlainObject(value.measurements)
	) {
		fail();
	}
	if ((value.outcome === "ok") !== (value.errorKind === "none")) fail();

	for (const [key, measurement] of Object.entries(value.measurements)) {
		if (!MEASUREMENT_SET.has(key) || !isNonNegativeFinite(measurement)) fail();
	}
}

export function normalizeMeasurements(measurements: TraceMeasurements = {}): TraceMeasurements {
	const normalized: TraceMeasurements = {};
	for (const key of TRACE_MEASUREMENTS) {
		const value = measurements[key];
		if (value === undefined) continue;
		if (!isNonNegativeFinite(value)) fail();
		normalized[key] = value;
	}
	return normalized;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
	const prototype = Object.getPrototypeOf(value);
	return prototype === Object.prototype || prototype === null;
}

function isCanonicalDecimal(value: string): boolean {
	return /^(?:0|[1-9][0-9]*)$/u.test(value);
}

function isCanonicalTimestamp(value: string): boolean {
	try {
		assertCanonicalTimestamp(value);
		return true;
	} catch {
		return false;
	}
}

function isNonNegativeFinite(value: unknown): value is number {
	return typeof value === "number" && Number.isFinite(value) && value >= 0;
}

function fail(): never {
	throw new SafeTraceValidationError();
}

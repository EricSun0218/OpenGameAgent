import { assertSafeTraceRecord, type SafeTraceRecord, type TraceOperation } from "./trace-schema.js";

export interface TraceAggregate {
	readonly operation: TraceOperation;
	readonly count: number;
	readonly failed: number;
	readonly totalDurationMs: number;
	readonly minimumDurationMs: number;
	readonly maximumDurationMs: number;
	readonly meanDurationMs: number;
}

export interface TraceExpectation {
	readonly operation: TraceOperation;
	readonly minimumCount?: number;
	readonly maximumFailed?: number;
	readonly maximumDurationMs?: number;
}

export interface TraceEvaluationFailure {
	readonly operation: TraceOperation;
	readonly reason: "minimum-count" | "maximum-failed" | "maximum-duration";
}

export interface TraceEvaluation {
	readonly passed: boolean;
	readonly failures: readonly TraceEvaluationFailure[];
	readonly aggregates: readonly TraceAggregate[];
}

interface MutableTraceAggregate {
	readonly operation: TraceOperation;
	count: number;
	failed: number;
	totalDurationMs: number;
	minimumDurationMs: number;
	maximumDurationMs: number;
}

export function aggregateTrace(records: readonly SafeTraceRecord[]): TraceAggregate[] {
	const groups = new Map<TraceOperation, MutableTraceAggregate>();
	for (const record of records) {
		assertSafeTraceRecord(record);
		const group = groups.get(record.operation);
		if (group === undefined) {
			groups.set(record.operation, {
				operation: record.operation,
				count: 1,
				failed: record.outcome === "ok" ? 0 : 1,
				totalDurationMs: record.durationMs,
				minimumDurationMs: record.durationMs,
				maximumDurationMs: record.durationMs,
			});
			continue;
		}
		const totalDurationMs = group.totalDurationMs + record.durationMs;
		if (!Number.isFinite(totalDurationMs)) throw new RangeError("Trace duration aggregation overflowed.");
		group.count += 1;
		if (record.outcome !== "ok") group.failed += 1;
		group.totalDurationMs = totalDurationMs;
		group.minimumDurationMs = Math.min(group.minimumDurationMs, record.durationMs);
		group.maximumDurationMs = Math.max(group.maximumDurationMs, record.durationMs);
	}

	return [...groups.values()]
		.sort((left, right) => left.operation.localeCompare(right.operation))
		.map((group) => ({ ...group, meanDurationMs: group.totalDurationMs / group.count }));
}

export function evaluateTrace(
	records: readonly SafeTraceRecord[],
	expectations: readonly TraceExpectation[],
): TraceEvaluation {
	const aggregates = aggregateTrace(records);
	const byOperation = new Map(aggregates.map((aggregate) => [aggregate.operation, aggregate]));
	const failures: TraceEvaluationFailure[] = [];

	for (const expectation of expectations) {
		validateExpectation(expectation);
		const aggregate = byOperation.get(expectation.operation);
		if (expectation.minimumCount !== undefined && (aggregate?.count ?? 0) < expectation.minimumCount) {
			failures.push({ operation: expectation.operation, reason: "minimum-count" });
		}
		if (expectation.maximumFailed !== undefined && (aggregate?.failed ?? 0) > expectation.maximumFailed) {
			failures.push({ operation: expectation.operation, reason: "maximum-failed" });
		}
		if (
			expectation.maximumDurationMs !== undefined &&
			aggregate !== undefined &&
			aggregate.maximumDurationMs > expectation.maximumDurationMs
		) {
			failures.push({ operation: expectation.operation, reason: "maximum-duration" });
		}
	}

	return { passed: failures.length === 0, failures, aggregates };
}

export async function replayTrace(
	records: readonly SafeTraceRecord[],
	observer: (record: SafeTraceRecord) => void | Promise<void>,
): Promise<void> {
	for (const record of records) {
		assertSafeTraceRecord(record);
		await observer(structuredClone(record));
	}
}

function validateExpectation(expectation: TraceExpectation): void {
	if (expectation.minimumCount !== undefined) assertNonNegativeSafeInteger(expectation.minimumCount, "minimumCount");
	if (expectation.maximumFailed !== undefined) assertNonNegativeSafeInteger(expectation.maximumFailed, "maximumFailed");
	if (
		expectation.maximumDurationMs !== undefined &&
		(!Number.isFinite(expectation.maximumDurationMs) || expectation.maximumDurationMs < 0)
	) {
		throw new RangeError("maximumDurationMs must be finite and non-negative.");
	}
}

function assertNonNegativeSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${name} must be a non-negative safe integer.`);
}

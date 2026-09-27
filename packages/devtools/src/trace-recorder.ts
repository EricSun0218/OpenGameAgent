import {
	assertSafeTraceRecord,
	normalizeMeasurements,
	type SafeTraceRecord,
	TRACE_SCHEMA_VERSION,
	type TraceComponent,
	type TraceErrorKind,
	type TraceMeasurements,
	type TraceOperation,
	type TraceOutcome,
} from "./trace-schema.js";

export interface TraceClockSample {
	readonly unixTimeMs: number;
	readonly monotonicTimeMs: number;
}

export interface TraceClock {
	now(): TraceClockSample;
}

export interface SafeTraceSink {
	record(record: SafeTraceRecord): void;
}

export type TraceSinkFailurePolicy = "fail-fast" | "attempt-all";

export interface TraceEnd {
	readonly outcome: TraceOutcome;
	readonly errorKind?: TraceErrorKind;
	readonly measurements?: TraceMeasurements;
}

export interface TraceScope {
	end(result: TraceEnd): SafeTraceRecord;
}

export class SystemTraceClock implements TraceClock {
	now(): TraceClockSample {
		return { unixTimeMs: Date.now(), monotonicTimeMs: performance.now() };
	}
}

export class ManualTraceClock implements TraceClock {
	constructor(
		private unixTimeMs: number,
		private monotonicTimeMs: number,
	) {
		assertClockValue(unixTimeMs);
		assertClockValue(monotonicTimeMs);
	}

	now(): TraceClockSample {
		return { unixTimeMs: this.unixTimeMs, monotonicTimeMs: this.monotonicTimeMs };
	}

	advance(milliseconds: number): void {
		assertClockValue(milliseconds);
		this.unixTimeMs += milliseconds;
		this.monotonicTimeMs += milliseconds;
		assertClockValue(this.unixTimeMs);
		assertClockValue(this.monotonicTimeMs);
	}
}

export class SafeTraceRecorder {
	private sequence = 0n;

	constructor(
		private readonly clock: TraceClock,
		private readonly sinks: readonly SafeTraceSink[],
		private readonly sinkFailurePolicy: TraceSinkFailurePolicy,
	) {
		if (sinkFailurePolicy !== "fail-fast" && sinkFailurePolicy !== "attempt-all") {
			throw new TypeError("sinkFailurePolicy must be fail-fast or attempt-all.");
		}
	}

	start(component: TraceComponent, operation: TraceOperation, measurements: TraceMeasurements = {}): TraceScope {
		const started = this.clock.now();
		assertClockSample(started);
		const initial = normalizeMeasurements(measurements);
		let ended = false;

		return {
			end: (result) => {
				if (ended) throw new Error("A trace scope can only be ended once.");
				ended = true;
				const finished = this.clock.now();
				assertClockSample(finished);
				const durationMs = finished.monotonicTimeMs - started.monotonicTimeMs;
				if (!Number.isFinite(durationMs) || durationMs < 0) {
					throw new RangeError("The trace clock must be monotonic.");
				}

				const record: SafeTraceRecord = {
					schemaVersion: TRACE_SCHEMA_VERSION,
					sequence: this.sequence.toString(),
					component,
					operation,
					startedAt: new Date(started.unixTimeMs).toISOString(),
					durationMs,
					outcome: result.outcome,
					errorKind: result.errorKind ?? "none",
					measurements: normalizeMeasurements({ ...initial, ...result.measurements }),
				};
				assertSafeTraceRecord(record);
				this.sequence += 1n;
				this.recordToSinks(record);
				return record;
			},
		};
	}

	private recordToSinks(record: SafeTraceRecord): void {
		if (this.sinkFailurePolicy === "fail-fast") {
			for (const sink of this.sinks) sink.record(record);
			return;
		}
		const failures: unknown[] = [];
		for (const sink of this.sinks) {
			try {
				sink.record(record);
			} catch (error) {
				failures.push(error);
			}
		}
		if (failures.length > 0) throw new AggregateError(failures, "One or more trace sinks failed.");
	}
}

export class BoundedTraceCollector implements SafeTraceSink {
	readonly records: SafeTraceRecord[] = [];

	constructor(private readonly maximumRecords: number) {
		if (!Number.isSafeInteger(maximumRecords) || maximumRecords <= 0) {
			throw new RangeError("maximumRecords must be a positive safe integer supplied by the host.");
		}
	}

	record(record: SafeTraceRecord): void {
		assertSafeTraceRecord(record);
		if (this.records.length >= this.maximumRecords) throw new TraceCapacityError();
		this.records.push(structuredClone(record));
	}
}

export class TraceCapacityError extends Error {
	constructor() {
		super("The host-configured trace record capacity was reached.");
		this.name = "TraceCapacityError";
	}
}

function assertClockSample(sample: TraceClockSample): void {
	assertClockValue(sample.unixTimeMs);
	assertClockValue(sample.monotonicTimeMs);
	if (sample.unixTimeMs > 8_640_000_000_000_000) {
		throw new RangeError("The trace wall clock is outside the ECMAScript Date range.");
	}
}

function assertClockValue(value: number): void {
	if (!Number.isFinite(value) || value < 0) throw new RangeError("Trace clock values must be finite and non-negative.");
}

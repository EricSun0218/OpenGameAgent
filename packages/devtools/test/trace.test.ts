import { type Span, type SpanOptions, SpanStatusCode, type Tracer } from "@opentelemetry/api";
import { describe, expect, it, vi } from "vitest";
import {
	aggregateTrace,
	assertSafeTraceRecord,
	BoundedTraceCollector,
	encodeTraceJsonl,
	evaluateTrace,
	ManualTraceClock,
	OpenTelemetryTraceSink,
	parseTraceJsonl,
	replayTrace,
	SafeTraceRecorder,
} from "../src/index.js";

describe("safe trace records", () => {
	it("records only the fixed content-safe vocabulary", () => {
		const clock = new ManualTraceClock(1_700_000_000_000, 20);
		const collector = new BoundedTraceCollector(1);
		const recorder = new SafeTraceRecorder(clock, [collector], "fail-fast");
		const scope = recorder.start("runtime", "provider.complete", { inputBytes: 17 });
		clock.advance(12.5);
		const record = scope.end({ outcome: "error", errorKind: "provider", measurements: { outputBytes: 3 } });

		expect(record).toEqual({
			schemaVersion: "1",
			sequence: "0",
			component: "runtime",
			operation: "provider.complete",
			startedAt: "2023-11-14T22:13:20.000Z",
			durationMs: 12.5,
			outcome: "error",
			errorKind: "provider",
			measurements: { inputBytes: 17, outputBytes: 3 },
		});
		expect(JSON.stringify(record)).not.toMatch(/prompt|reasoning|credential|session|actor|secret/iu);
		expect(() => scope.end({ outcome: "ok" })).toThrow(/only be ended once/);
	});

	it("rejects contradictory outcome and error classifications", () => {
		const base = {
			schemaVersion: "1" as const,
			sequence: "0",
			component: "runtime" as const,
			operation: "provider.complete" as const,
			startedAt: "2023-11-14T22:13:20.000Z",
			durationMs: 1,
			measurements: {},
		};

		expect(() => assertSafeTraceRecord({ ...base, outcome: "ok", errorKind: "provider" })).toThrow();
		expect(() => assertSafeTraceRecord({ ...base, outcome: "error", errorKind: "none" })).toThrow();
	});

	it("uses the protocol timestamp domain for safe records", () => {
		const record = {
			schemaVersion: "1" as const,
			sequence: "0",
			component: "runtime" as const,
			operation: "provider.complete" as const,
			startedAt: "+010000-01-01T00:00:00.000Z",
			durationMs: 1,
			outcome: "ok" as const,
			errorKind: "none" as const,
			measurements: {},
		};

		expect(() => assertSafeTraceRecord(record)).toThrow();
	});

	it("fails closed when a bounded collector fills", () => {
		const clock = new ManualTraceClock(0, 0);
		const recorder = new SafeTraceRecorder(clock, [new BoundedTraceCollector(1)], "fail-fast");
		recorder.start("agent", "admission").end({ outcome: "ok" });
		expect(() => recorder.start("agent", "queue").end({ outcome: "ok" })).toThrow(/capacity/);
	});

	it("makes fail-fast and attempt-all sink failure semantics explicit", () => {
		const failure = new Error("sink failed");
		const failFastCalls: string[] = [];
		const failFast = new SafeTraceRecorder(
			new ManualTraceClock(0, 0),
			[
				{ record: () => failFastCalls.push("first") },
				{
					record: () => {
						failFastCalls.push("failed");
						throw failure;
					},
				},
				{ record: () => failFastCalls.push("last") },
			],
			"fail-fast",
		);
		expect(() => failFast.start("agent", "queue").end({ outcome: "ok" })).toThrow(failure);
		expect(failFastCalls).toEqual(["first", "failed"]);

		const attemptAllCalls: string[] = [];
		const attemptAll = new SafeTraceRecorder(
			new ManualTraceClock(0, 0),
			[
				{
					record: () => {
						attemptAllCalls.push("failed");
						throw failure;
					},
				},
				{ record: () => attemptAllCalls.push("last") },
			],
			"attempt-all",
		);
		let aggregate: unknown;
		try {
			attemptAll.start("agent", "queue").end({ outcome: "ok" });
		} catch (error) {
			aggregate = error;
		}
		expect(attemptAllCalls).toEqual(["failed", "last"]);
		expect(aggregate).toBeInstanceOf(AggregateError);
		expect((aggregate as AggregateError).errors).toEqual([failure]);
	});
});

describe("trace JSONL, replay, and evaluation", () => {
	const records = (() => {
		const clock = new ManualTraceClock(1_700_000_000_000, 0);
		const collector = new BoundedTraceCollector(2);
		const recorder = new SafeTraceRecorder(clock, [collector], "fail-fast");
		const first = recorder.start("actions", "action.wait");
		clock.advance(4);
		first.end({ outcome: "ok" });
		const second = recorder.start("actions", "action.wait");
		clock.advance(7);
		second.end({ outcome: "uncertain", errorKind: "authority" });
		return collector.records;
	})();

	it("round trips deterministic bounded JSONL", () => {
		const encoded = encodeTraceJsonl(records);
		expect(encodeTraceJsonl(parseTraceJsonl(encoded, { maximumLineBytes: 512, maximumRecords: 2 }))).toBe(encoded);
		expect(() => parseTraceJsonl(encoded, { maximumLineBytes: 8, maximumRecords: 2 })).toThrow(/line limit/);
		expect(() => parseTraceJsonl(encoded, { maximumLineBytes: 512, maximumRecords: 1 })).toThrow(/records limit/);
		expect(() =>
			parseTraceJsonl(`${encoded.trimEnd().replace(/\}$/u, ',"prompt":"private"}')}\n`, {
				maximumLineBytes: 512,
				maximumRecords: 2,
			}),
		).toThrow();
	});

	it("aggregates, evaluates explicit thresholds, and replays in source order", async () => {
		expect(aggregateTrace(records)).toEqual([
			{
				operation: "action.wait",
				count: 2,
				failed: 1,
				totalDurationMs: 11,
				minimumDurationMs: 4,
				maximumDurationMs: 7,
				meanDurationMs: 5.5,
			},
		]);
		const evaluation = evaluateTrace(records, [
			{ operation: "action.wait", minimumCount: 2, maximumFailed: 0, maximumDurationMs: 6 },
		]);
		expect(evaluation.failures.map((failure) => failure.reason)).toEqual(["maximum-failed", "maximum-duration"]);

		const replayed: string[] = [];
		await replayTrace(records, async (record) => {
			replayed.push(record.sequence);
		});
		expect(replayed).toEqual(["0", "1"]);
	});

	it("aggregates large record arrays without spreading them into function arguments", () => {
		const record = {
			schemaVersion: "1" as const,
			sequence: "0",
			component: "runtime" as const,
			operation: "provider.complete" as const,
			startedAt: "2023-11-14T22:13:20.000Z",
			durationMs: 1,
			outcome: "ok" as const,
			errorKind: "none" as const,
			measurements: {},
		};
		const count = 150_000;

		expect(aggregateTrace(Array.from({ length: count }, () => record))).toEqual([
			{
				operation: "provider.complete",
				count,
				failed: 0,
				totalDurationMs: count,
				minimumDurationMs: 1,
				maximumDurationMs: 1,
				meanDurationMs: 1,
			},
		]);
	});
});

describe("OpenTelemetry projection", () => {
	it("leaves successful span status unset and projects only safe attributes", () => {
		const setStatus = vi.fn();
		const end = vi.fn();
		const startSpan = vi.fn((_name: string, _options?: SpanOptions) => ({ setStatus, end }) as unknown as Span);
		const tracer = { startSpan } as unknown as Tracer;
		const clock = new ManualTraceClock(1_700_000_000_000, 0);
		const recorder = new SafeTraceRecorder(clock, [new OpenTelemetryTraceSink(tracer)], "fail-fast");
		const scope = recorder.start("server", "admission", { queueDepth: 2 });
		clock.advance(3);
		scope.end({ outcome: "ok" });

		expect(startSpan).toHaveBeenCalledOnce();
		const options = startSpan.mock.calls[0]?.[1];
		expect(options?.attributes).toEqual({
			"opengameagent.trace.schema_version": "1",
			"opengameagent.component": "server",
			"opengameagent.operation": "admission",
			"opengameagent.outcome": "ok",
			"opengameagent.error.kind": "none",
			"opengameagent.measurement.queueDepth": 2,
		});
		expect(setStatus).not.toHaveBeenCalled();
		expect(end).toHaveBeenCalledWith(1_700_000_000_003);
	});

	it.each([
		["error", "provider"],
		["rejected", "capacity"],
		["uncertain", "authority"],
	] as const)("marks %s spans as errors with a low-cardinality error type", (outcome, errorKind) => {
		const setStatus = vi.fn();
		const startSpan = vi.fn(
			(_name: string, _options?: SpanOptions) => ({ setStatus, end: vi.fn() }) as unknown as Span,
		);
		const recorder = new SafeTraceRecorder(
			new ManualTraceClock(1_700_000_000_000, 0),
			[new OpenTelemetryTraceSink({ startSpan } as unknown as Tracer)],
			"fail-fast",
		);

		recorder.start("runtime", "provider.complete").end({ outcome, errorKind });

		expect(startSpan.mock.calls[0]?.[1]?.attributes).toMatchObject({
			"opengameagent.outcome": outcome,
			"opengameagent.error.kind": errorKind,
			"error.type": errorKind,
		});
		expect(setStatus).toHaveBeenCalledWith({ code: SpanStatusCode.ERROR });
	});

	it("does not treat caller cancellation as an OpenTelemetry error", () => {
		const setStatus = vi.fn();
		const startSpan = vi.fn(
			(_name: string, _options?: SpanOptions) => ({ setStatus, end: vi.fn() }) as unknown as Span,
		);
		const recorder = new SafeTraceRecorder(
			new ManualTraceClock(1_700_000_000_000, 0),
			[new OpenTelemetryTraceSink({ startSpan } as unknown as Tracer)],
			"fail-fast",
		);

		recorder.start("runtime", "provider.complete").end({ outcome: "cancelled", errorKind: "cancelled" });

		expect(startSpan.mock.calls[0]?.[1]?.attributes).not.toHaveProperty("error.type");
		expect(setStatus).not.toHaveBeenCalled();
	});
});

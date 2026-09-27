import {
	TRACE_MEASUREMENTS,
	assertSafeTraceRecord,
	type SafeTraceRecord,
	type TraceMeasurements,
} from "./trace-schema.js";

export interface TraceJsonlLimits {
	readonly maximumLineBytes: number;
	readonly maximumRecords: number;
}

export function encodeTraceJsonl(records: readonly SafeTraceRecord[]): string {
	return records.map((record) => encodeRecord(record)).join("\n") + (records.length === 0 ? "" : "\n");
}

export function parseTraceJsonl(text: string, limits: TraceJsonlLimits): SafeTraceRecord[] {
	assertPositiveSafeInteger(limits.maximumLineBytes, "maximumLineBytes");
	assertPositiveSafeInteger(limits.maximumRecords, "maximumRecords");
	const rawLines = text.split("\n");
	if (rawLines.at(-1) === "") rawLines.pop();
	if (rawLines.length > limits.maximumRecords) throw new TraceJsonlLimitError("records");

	const encoder = new TextEncoder();
	return rawLines.map((rawLine) => {
		const line = rawLine.endsWith("\r") ? rawLine.slice(0, -1) : rawLine;
		if (line.length === 0) throw new TraceJsonlValidationError();
		if (encoder.encode(line).byteLength > limits.maximumLineBytes) throw new TraceJsonlLimitError("line");
		let value: unknown;
		try {
			value = JSON.parse(line);
		} catch {
			throw new TraceJsonlValidationError();
		}
		assertSafeTraceRecord(value);
		return structuredClone(value);
	});
}

export class TraceJsonlLimitError extends RangeError {
	constructor(readonly boundary: "line" | "records") {
		super(`The trace JSONL ${boundary} limit was exceeded.`);
		this.name = "TraceJsonlLimitError";
	}
}

export class TraceJsonlValidationError extends TypeError {
	constructor() {
		super("The trace JSONL document is invalid.");
		this.name = "TraceJsonlValidationError";
	}
}

function encodeRecord(record: SafeTraceRecord): string {
	assertSafeTraceRecord(record);
	const measurements: TraceMeasurements = {};
	for (const key of TRACE_MEASUREMENTS) {
		const value = record.measurements[key];
		if (value !== undefined) measurements[key] = value;
	}
	return JSON.stringify({
		schemaVersion: record.schemaVersion,
		sequence: record.sequence,
		component: record.component,
		operation: record.operation,
		startedAt: record.startedAt,
		durationMs: record.durationMs,
		outcome: record.outcome,
		errorKind: record.errorKind,
		measurements,
	});
}

function assertPositiveSafeInteger(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value <= 0) {
		throw new RangeError(`${name} must be a positive safe integer supplied by the host.`);
	}
}

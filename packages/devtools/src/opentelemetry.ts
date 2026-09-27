import { type Attributes, SpanStatusCode, type Tracer, trace } from "@opentelemetry/api";
import { NodeSDK, type NodeSDKConfiguration } from "@opentelemetry/sdk-node";
import type { SafeTraceSink } from "./trace-recorder.js";
import { assertSafeTraceRecord, type SafeTraceRecord, TRACE_MEASUREMENTS } from "./trace-schema.js";

export type OpenGameAgentNodeSdkOptions = Omit<Partial<NodeSDKConfiguration>, "serviceName"> & {
	readonly serviceName: string;
};

export function createOpenGameAgentNodeSdk(options: OpenGameAgentNodeSdkOptions): NodeSDK {
	if (!isSafeLabel(options.serviceName)) {
		throw new TypeError("OpenTelemetry serviceName must be non-empty and control-character safe.");
	}
	return new NodeSDK(options);
}

export class OpenTelemetryTraceSink implements SafeTraceSink {
	constructor(private readonly tracer: Tracer = trace.getTracer("@opengameagent/devtools", "0.1.0-alpha.0")) {}

	record(record: SafeTraceRecord): void {
		assertSafeTraceRecord(record);
		const failed = record.outcome !== "ok" && record.outcome !== "cancelled";
		const attributes: Attributes = {
			"opengameagent.trace.schema_version": record.schemaVersion,
			"opengameagent.component": record.component,
			"opengameagent.operation": record.operation,
			"opengameagent.outcome": record.outcome,
			"opengameagent.error.kind": record.errorKind,
		};
		if (failed) attributes["error.type"] = record.errorKind;
		for (const measurement of TRACE_MEASUREMENTS) {
			const value = record.measurements[measurement];
			if (value !== undefined) attributes[`opengameagent.measurement.${measurement}`] = value;
		}

		const startedAt = Date.parse(record.startedAt);
		const span = this.tracer.startSpan(`opengameagent.${record.operation}`, {
			attributes,
			startTime: startedAt,
		});
		if (failed) span.setStatus({ code: SpanStatusCode.ERROR });
		span.end(startedAt + record.durationMs);
	}
}

function isSafeLabel(value: string): boolean {
	if (value.trim().length === 0) return false;
	for (const character of value) {
		const codePoint = character.codePointAt(0);
		if (codePoint !== undefined && (codePoint <= 31 || codePoint === 127)) return false;
	}
	return true;
}

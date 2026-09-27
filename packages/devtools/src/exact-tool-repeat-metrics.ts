import { assertCanonicalEvent, type CanonicalEvent } from "@opengameagent/protocol";
import type { Meter } from "@opentelemetry/api";
import { metrics } from "@opentelemetry/api";

export interface ExactToolRepeatMetricSnapshot {
	readonly advisories: string;
	readonly terminations: string;
}

export class ExactToolRepeatMetricsProjector {
	private advisories = 0n;
	private terminations = 0n;

	record(event: Readonly<CanonicalEvent>): void {
		const captured = structuredClone(event);
		assertCanonicalEvent(captured);
		const data = captured.data;
		if (data.type !== "tool.repeat.detected") return;
		const action = data.action;
		if (action === "advisory") this.advisories += 1n;
		else if (action === "terminated") this.terminations += 1n;
		else throw new TypeError("The exact Tool-repeat metric action is invalid.");
	}

	snapshot(): ExactToolRepeatMetricSnapshot {
		return Object.freeze({
			advisories: this.advisories.toString(),
			terminations: this.terminations.toString(),
		});
	}
}

export class OpenTelemetryExactToolRepeatMetricSink {
	private readonly counter;

	constructor(meter: Meter = metrics.getMeter("@opengameagent/devtools", "0.1.0-alpha.0")) {
		this.counter = meter.createCounter("opengameagent.tool.exact_repeat.detections", {
			description: "Exact prepared Tool-call repeat policy detections",
			unit: "{detection}",
		});
	}

	record(event: Readonly<CanonicalEvent>): void {
		const captured = structuredClone(event);
		assertCanonicalEvent(captured);
		const data = captured.data;
		if (data.type !== "tool.repeat.detected") return;
		const action = data.action;
		if (action !== "advisory" && action !== "terminated") {
			throw new TypeError("The exact Tool-repeat metric action is invalid.");
		}
		this.counter.add(1, { "opengameagent.policy.action": action });
	}
}

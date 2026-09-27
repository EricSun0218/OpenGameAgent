import type { CanonicalEvent } from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import { ExactToolRepeatMetricsProjector, OpenTelemetryExactToolRepeatMetricSink } from "../src/index.js";

function event(action: "advisory" | "terminated", sequence: string): CanonicalEvent {
	return {
		protocolVersion: "1",
		eventId: `event-${sequence}`,
		sequence,
		revision: sequence,
		occurredAt: "2026-09-01T00:00:00.000Z",
		coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "1" },
		audience: { visibility: "owner" },
		data: {
			type: "tool.repeat.detected",
			toolCallId: `call-${sequence}`,
			toolName: "inspect",
			consecutiveCount: sequence,
			action,
		},
	};
}

describe("exact Tool-repeat metrics", () => {
	it("projects unbounded counters as canonical decimals", () => {
		const metrics = new ExactToolRepeatMetricsProjector();
		metrics.record(event("advisory", "2"));
		metrics.record(event("terminated", "3"));
		metrics.record(event("advisory", "4"));

		expect(metrics.snapshot()).toEqual({ advisories: "2", terminations: "1" });
	});

	it("exports only the low-cardinality policy action to OpenTelemetry", () => {
		const add = vi.fn();
		const createCounter = vi.fn(() => ({ add }));
		const sink = new OpenTelemetryExactToolRepeatMetricSink({ createCounter } as never);

		sink.record(event("terminated", "3"));

		expect(createCounter).toHaveBeenCalledWith("opengameagent.tool.exact_repeat.detections", {
			description: "Exact prepared Tool-call repeat policy detections",
			unit: "{detection}",
		});
		expect(add).toHaveBeenCalledWith(1, { "opengameagent.policy.action": "terminated" });
	});

	it("rejects forged event data before changing counters", () => {
		const metrics = new ExactToolRepeatMetricsProjector();
		const forged = structuredClone(event("advisory", "2")) as unknown as {
			data: { consecutiveCount: string };
		};
		forged.data.consecutiveCount = "02";

		expect(() => metrics.record(forged as never)).toThrow(TypeError);
		expect(metrics.snapshot()).toEqual({ advisories: "0", terminations: "0" });
	});

	it.each(["terminated", "secret-high-cardinality-value"])(
		"captures an advisory action once even when a later getter value would become %s",
		(laterAction) => {
			const metrics = new ExactToolRepeatMetricsProjector();
			const add = vi.fn();
			const sink = new OpenTelemetryExactToolRepeatMetricSink({
				createCounter: () => ({ add }),
			} as never);
			const changingEvent = () => {
				const value = structuredClone(event("advisory", "2"));
				let actionReads = 0;
				Object.defineProperty(value.data, "action", {
					enumerable: true,
					get: () => (actionReads++ === 0 ? "advisory" : laterAction),
				});
				return { value, actionReads: () => actionReads };
			};

			const projected = changingEvent();
			metrics.record(projected.value);
			expect(projected.actionReads()).toBe(1);
			expect(metrics.snapshot()).toEqual({ advisories: "1", terminations: "0" });
			const exported = changingEvent();
			sink.record(exported.value);
			expect(exported.actionReads()).toBe(1);
			expect(add).toHaveBeenCalledWith(1, { "opengameagent.policy.action": "advisory" });
		},
	);
});

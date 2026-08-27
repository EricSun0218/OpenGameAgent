import { describe, expect, it } from "vitest";
import { formatGameBenchmarkReport, runGameBenchmark } from "./benchmark.js";
import type { GameTraceRecording } from "./trace.js";

const recording = (runId: string): GameTraceRecording => ({
	schemaVersion: 1,
	createdAt: 1,
	droppedRecords: 0,
	records: [
		{
			schemaVersion: 1,
			kind: "stage",
			sequence: 1,
			observedAt: 1,
			session: {
				worldId: "world",
				saveId: "save",
				timelineId: "timeline",
				generation: 1,
				ownerId: "owner",
				sessionId: "session",
				actorId: "actor",
			},
			inputId: `input-${runId}`,
			runId,
			turn: 1,
			stage: "run",
			startedAt: 1,
			durationMilliseconds: 10,
			outcome: "ok",
		},
	],
});

describe("benchmark runner", () => {
	it("supports bounded concurrency, injected failures, thresholds, and text reports", async () => {
		let active = 0;
		let maximumActive = 0;
		const report = await runGameBenchmark(
			{
				name: "deterministic-runtime",
				async run(context) {
					active += 1;
					maximumActive = Math.max(maximumActive, active);
					await new Promise((resolve) => setTimeout(resolve, 5));
					active -= 1;
					return recording(`run-${context.iteration}`);
				},
			},
			{
				iterations: 4,
				concurrency: 2,
				faultInjector: {
					beforeIteration(context) {
						if (context.iteration === 2) throw new Error("injected");
					},
				},
				thresholds: { maximumFailedIterations: 0 },
			},
		);

		expect(maximumActive).toBe(2);
		expect(report.failedIterations).toBe(1);
		expect(report.thresholdsPassed).toBe(false);
		expect(formatGameBenchmarkReport(report)).toContain("Thresholds: FAIL");
	});

	it("enforces timeouts even when a scenario ignores its cancellation signal", async () => {
		const started = performance.now();
		const report = await runGameBenchmark(
			{
				name: "hung-provider",
				async run() {
					return await new Promise<GameTraceRecording>(() => {});
				},
			},
			{ iterations: 1, iterationTimeoutMilliseconds: 10 },
		);
		expect(report.iterations[0]?.status).toBe("timed-out");
		expect(performance.now() - started).toBeLessThan(500);
	});
});

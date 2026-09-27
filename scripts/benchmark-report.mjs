export function withCleanupFailure(report, failures) {
	const benchmarkOutcome = report?.benchmarkOutcome ??
		report?.outcome ?? {
			status: "failed",
			code: "benchmark_execution_failed",
			errorName: "UnknownReportError",
		};
	return {
		...(report ?? { schemaVersion: 1, createdAt: new Date().toISOString() }),
		benchmarkOutcome,
		outcome: { status: "failed", code: "benchmark_cleanup_failed" },
		cleanup: { status: "failed", failures },
	};
}

export function withOutputFailure(report, error) {
	const benchmarkOutcome = report.benchmarkOutcome ?? report.outcome;
	const priorOutcome = report.outcome;
	return {
		...report,
		benchmarkOutcome,
		...(priorOutcome?.code === "benchmark_cleanup_failed" ? { cleanupOutcome: priorOutcome } : {}),
		outcome: { status: "failed", code: "benchmark_report_write_failed", errorName: error.causeName },
		output: { status: "failed", code: "benchmark_report_write_failed", errorName: error.causeName },
	};
}

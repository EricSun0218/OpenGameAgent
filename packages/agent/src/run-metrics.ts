import { AsyncLocalStorage } from "node:async_hooks";
import type { CanonicalEventData } from "@opengameagent/protocol";

export type RunMetricPhase = "memoryPrepare" | "memorySearch" | "tool" | "approval" | "settlement";
const activeMetrics = new AsyncLocalStorage<RunMetricsTracker>();

export function withRunMetrics<T>(metrics: RunMetricsTracker, operation: () => T): T {
	return activeMetrics.run(metrics, operation);
}

/** Framework integrations report only measured, content-free phase durations in the active run. */
export function recordRunPhaseDuration(phase: RunMetricPhase, durationMs: number): void {
	activeMetrics.getStore()?.recordPhase(phase, durationMs);
}

export async function measureRunPhase<T>(phase: RunMetricPhase, operation: () => T | PromiseLike<T>): Promise<T> {
	const metrics = activeMetrics.getStore();
	if (!metrics) return await operation();
	const startedAt = metrics.beginDurable();
	try {
		return await operation();
	} finally {
		metrics.endPhase(phase, startedAt);
	}
}

type SegmentStatus = "completed" | "aborted" | "failed" | "deferred";

export class RunMetricsTracker {
	private readonly startedAt: number;
	private lastSample: number;
	private contextStartedAt: number | undefined;
	private providerStartedAt: number | undefined;
	private contextMicroseconds = 0n;
	private providerMicroseconds = 0n;
	private durableMicroseconds = 0n;
	private firstResponseMicroseconds: bigint | undefined;
	private providerFirstResponseMicroseconds: bigint | undefined;
	private firstToolMicroseconds: bigint | undefined;
	private providerAttempts = 0n;
	private retries = 0n;
	private fallbacks = 0n;
	private toolCalls = 0n;
	private lastProviderRoute: string | undefined;
	private readonly phases: Partial<Record<RunMetricPhase, bigint>> = {};
	private finalized = false;
	private toolStartedAt: number | undefined;

	constructor(private readonly now: () => number) {
		this.startedAt = this.sample();
		this.lastSample = this.startedAt;
	}

	beginContext(): void {
		if (this.contextStartedAt !== undefined) throw new TypeError("Run context timing is already active.");
		this.contextStartedAt = this.sample();
	}

	endContext(): void {
		const startedAt = this.contextStartedAt;
		if (startedAt === undefined) return;
		this.contextStartedAt = undefined;
		this.contextMicroseconds += microseconds(this.sample() - startedAt);
	}

	providerCommitted(route: Readonly<{ provider: string; api: string; id: string }>, attemptIndex: string): void {
		if (this.providerStartedAt !== undefined) throw new TypeError("Run provider timing is already active.");
		const signature = `${route.provider}\u0000${route.api}\u0000${route.id}`;
		if (attemptIndex !== "1") {
			this.retries += 1n;
			if (this.lastProviderRoute !== undefined && this.lastProviderRoute !== signature) this.fallbacks += 1n;
		}
		this.lastProviderRoute = signature;
		this.providerAttempts += 1n;
		this.providerStartedAt = this.sample();
	}

	providerSettled(): void {
		const startedAt = this.providerStartedAt;
		if (startedAt === undefined) return;
		this.providerStartedAt = undefined;
		this.providerMicroseconds += microseconds(this.sample() - startedAt);
	}

	providerResponseObserved(): void {
		if (this.providerFirstResponseMicroseconds === undefined && this.providerStartedAt !== undefined) {
			this.providerFirstResponseMicroseconds = microseconds(this.sample() - this.providerStartedAt);
		}
	}

	beginDurable(): number {
		return this.sample();
	}

	endDurable(startedAt: number): void {
		this.durableMicroseconds += microseconds(this.sample() - startedAt);
	}

	recordPhase(phase: RunMetricPhase, durationMs: number): void {
		if (this.finalized) return;
		this.phases[phase] = (this.phases[phase] ?? 0n) + microseconds(durationMs);
	}

	endPhase(phase: RunMetricPhase, startedAt: number): void {
		if (!this.finalized) this.recordPhase(phase, this.sample() - startedAt);
	}

	observe(data: CanonicalEventData): void {
		if (
			this.firstResponseMicroseconds === undefined &&
			(data.type === "item.delta" ||
				(data.type === "item.completed" &&
					data.item.itemType === "message" &&
					data.item.role === "assistant" &&
					data.item.content.length > 0))
		) {
			this.firstResponseMicroseconds = microseconds(this.sample() - this.startedAt);
		}
		if (data.type === "item.started" && data.item.itemType === "tool-call") {
			this.toolCalls += 1n;
			this.firstToolMicroseconds ??= microseconds(this.sample() - this.startedAt);
			this.toolStartedAt ??= this.sample();
		}
		if (data.type === "turn.completed" && this.toolStartedAt !== undefined) {
			this.endPhase("tool", this.toolStartedAt);
			this.toolStartedAt = undefined;
		}
	}

	snapshot(segmentStatus: SegmentStatus): Extract<CanonicalEventData, { type: "run.metrics" }> {
		this.endContext();
		this.providerSettled();
		if (this.toolStartedAt !== undefined) {
			this.endPhase("tool", this.toolStartedAt);
			this.toolStartedAt = undefined;
		}
		this.finalized = true;
		const measuredTotal = microseconds(this.sample() - this.startedAt);
		const classified = this.contextMicroseconds + this.providerMicroseconds;
		const total = maximum(
			measuredTotal,
			classified,
			this.durableMicroseconds,
			this.firstResponseMicroseconds ?? 0n,
			this.firstToolMicroseconds ?? 0n,
		);
		const native = total - classified;
		const phases: Partial<Record<RunMetricPhase, string>> = {};
		for (const phase of ["memoryPrepare", "memorySearch", "tool", "approval", "settlement"] as const) {
			const duration = this.phases[phase];
			if (duration !== undefined) phases[phase] = duration.toString();
		}
		return {
			type: "run.metrics",
			unit: "microseconds",
			segmentStatus,
			durations: {
				...phases,
				contextAndRecall: this.contextMicroseconds.toString(),
				provider: this.providerMicroseconds.toString(),
				durable: this.durableMicroseconds.toString(),
				native: native.toString(),
				total: total.toString(),
				...(this.firstResponseMicroseconds === undefined
					? {}
					: { firstResponse: this.firstResponseMicroseconds.toString() }),
				...(this.firstToolMicroseconds === undefined ? {} : { firstTool: this.firstToolMicroseconds.toString() }),
				...(this.providerFirstResponseMicroseconds === undefined
					? {}
					: { providerFirstResponse: this.providerFirstResponseMicroseconds.toString() }),
			},
			counts: {
				providerAttempts: this.providerAttempts.toString(),
				retries: this.retries.toString(),
				fallbacks: this.fallbacks.toString(),
				toolCalls: this.toolCalls.toString(),
			},
		};
	}

	private sample(): number {
		const value = this.now();
		if (!Number.isFinite(value) || (this.lastSample !== undefined && value < this.lastSample)) {
			throw new TypeError("The run metrics clock must return finite monotonic milliseconds.");
		}
		this.lastSample = value;
		return value;
	}
}

function microseconds(milliseconds: number): bigint {
	const value = Math.round(milliseconds * 1_000);
	if (!Number.isSafeInteger(value) || value < 0)
		throw new RangeError("The run metric exceeds the safe duration range.");
	return BigInt(value);
}

function maximum(first: bigint, ...rest: bigint[]): bigint {
	return rest.reduce((current, candidate) => (candidate > current ? candidate : current), first);
}

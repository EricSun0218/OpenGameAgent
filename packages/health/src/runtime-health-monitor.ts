import {
	assertCanonicalTimestamp,
	assertOpaqueIdentifier,
	type RuntimeDependencyHealth,
	type RuntimeDependencyKind,
	type RuntimeDependencyReason,
	type RuntimeDependencyRequirement,
	type RuntimeHealthSnapshot,
	type RuntimeHealthStatus,
} from "@opengameagent/protocol";
import pLimit from "p-limit";

const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;
const encoder = new TextEncoder();

export type RuntimeDependencyInspection =
	| { readonly state: "declared" }
	| { readonly state: "available"; readonly reason?: "stale" }
	| { readonly state: "ready" }
	| {
			readonly state: "degraded" | "unavailable";
			readonly reason: RuntimeDependencyReason;
	  };

export interface RuntimeDependencyDefinition {
	readonly id: string;
	readonly kind: RuntimeDependencyKind;
	readonly requirement: RuntimeDependencyRequirement;
	readonly inspect?: (signal: AbortSignal) => RuntimeDependencyInspection | Promise<RuntimeDependencyInspection>;
}

export interface RuntimeHealthLimits {
	readonly maxDependencies: number;
	readonly maxDependencyIdBytes: number;
	readonly maxConcurrentInspections: number;
	readonly inspectionTimeoutMs: number;
}

export interface RuntimeHealthMonitorOptions {
	readonly dependencies: readonly RuntimeDependencyDefinition[];
	readonly limits: RuntimeHealthLimits;
	readonly now?: () => number;
}

export interface RuntimeHealthService {
	inspect(signal?: AbortSignal): Promise<RuntimeHealthSnapshot>;
}

export interface RuntimePassiveHealthDependencyOptions {
	readonly id: string;
	readonly kind: RuntimeDependencyKind;
	readonly requirement: RuntimeDependencyRequirement;
	readonly maxObservationAgeMs: number;
	readonly now: () => number;
}

interface PassiveHealthObservation {
	readonly inspection: Exclude<RuntimeDependencyInspection, { readonly state: "declared" }>;
	readonly observedAt: number;
}

const DECLARED_INSPECTION = Object.freeze({ state: "declared" } as const);
const STALE_INSPECTION = Object.freeze({ state: "available", reason: "stale" } as const);

/**
 * Holds one bounded passive fact produced by a real dependency operation.
 * Reading the definition never calls the dependency; the host owns freshness.
 */
export class RuntimePassiveHealthDependency {
	readonly definition: Readonly<RuntimeDependencyDefinition>;
	private readonly maxObservationAgeMs: number;
	private readonly now: () => number;
	private observation: PassiveHealthObservation | undefined;

	constructor(options: RuntimePassiveHealthDependencyOptions) {
		if (!options || typeof options !== "object") throw new TypeError("Passive health options are required.");
		const { id, kind, requirement, maxObservationAgeMs, now } = options;
		assertOpaqueIdentifier(id);
		if (!isDependencyKind(kind) || !isDependencyRequirement(requirement)) {
			throw new TypeError("Passive health dependency kind and requirement are invalid.");
		}
		if (!Number.isSafeInteger(maxObservationAgeMs) || maxObservationAgeMs <= 0) {
			throw new TypeError("maxObservationAgeMs must be a positive safe integer.");
		}
		if (typeof now !== "function") throw new TypeError("Passive health now must be a function.");
		this.maxObservationAgeMs = maxObservationAgeMs;
		this.now = now;
		this.definition = Object.freeze({
			id,
			kind,
			requirement,
			inspect: async (signal: AbortSignal) => this.inspect(signal),
		});
	}

	observe(inspection: RuntimeDependencyInspection): void {
		const normalized = normalizeInspection(inspection);
		if (!normalized) throw new TypeError("Passive health observation is invalid.");
		if (normalized.state === "declared") {
			this.observation = undefined;
			return;
		}
		this.observation = Object.freeze({
			inspection: Object.freeze(normalized),
			observedAt: readPassiveClock(this.now),
		});
	}

	private inspect(signal: AbortSignal): RuntimeDependencyInspection {
		signal.throwIfAborted();
		const observation = this.observation;
		if (!observation) return DECLARED_INSPECTION;
		const current = readPassiveClock(this.now);
		const age = current - observation.observedAt;
		return age < 0 || age > this.maxObservationAgeMs ? STALE_INSPECTION : observation.inspection;
	}
}

interface CapturedDependencyDefinition {
	readonly id: string;
	readonly kind: RuntimeDependencyKind;
	readonly requirement: RuntimeDependencyRequirement;
	readonly inspect?: RuntimeDependencyDefinition["inspect"];
}

type UnstampedDependencyHealth = Pick<RuntimeDependencyHealth, "id" | "kind" | "requirement"> &
	RuntimeDependencyInspection;

export class RuntimeHealthMonitor implements RuntimeHealthService {
	private readonly dependencies: readonly CapturedDependencyDefinition[];
	private readonly limits: Readonly<RuntimeHealthLimits>;
	private readonly now: () => number;

	constructor(options: RuntimeHealthMonitorOptions) {
		if (!options || !Array.isArray(options.dependencies) || !options.limits) {
			throw new TypeError("Runtime health options are required.");
		}
		this.limits = prepareLimits(options.limits);
		this.dependencies = captureDefinitions(options.dependencies, this.limits);
		this.now = options.now ?? Date.now;
		if (typeof this.now !== "function") throw new TypeError("Runtime health now must be a function.");
	}

	async inspect(signal: AbortSignal = new AbortController().signal): Promise<RuntimeHealthSnapshot> {
		signal.throwIfAborted();
		const limit = pLimit(this.limits.maxConcurrentInspections);
		const unstamped = await Promise.all(
			this.dependencies.map((definition) => limit(() => this.inspectDependency(definition, signal))),
		);
		signal.throwIfAborted();
		const checkedAt = new Date(this.now()).toISOString();
		assertCanonicalTimestamp(checkedAt);
		const dependencies = unstamped.map((dependency) =>
			Object.freeze(dependency.state === "declared" ? dependency : { ...dependency, observedAt: checkedAt }),
		) as RuntimeDependencyHealth[];
		Object.freeze(dependencies);
		return Object.freeze({ checkedAt, status: aggregateStatus(dependencies), dependencies });
	}

	private async inspectDependency(
		definition: CapturedDependencyDefinition,
		callerSignal: AbortSignal,
	): Promise<UnstampedDependencyHealth> {
		const identity = {
			id: definition.id,
			kind: definition.kind,
			requirement: definition.requirement,
		} as const;
		if (!definition.inspect) return { ...identity, state: "declared" };

		callerSignal.throwIfAborted();
		const timeout = new AbortController();
		const timer = setTimeout(
			() => timeout.abort(new DOMException("The dependency inspection timed out.", "TimeoutError")),
			this.limits.inspectionTimeoutMs,
		);
		const signal = AbortSignal.any([callerSignal, timeout.signal]);
		try {
			const result = await definition.inspect(signal);
			callerSignal.throwIfAborted();
			if (timeout.signal.aborted) return { ...identity, state: "unavailable", reason: "timeout" };
			const normalized = normalizeInspection(result);
			return normalized ? { ...identity, ...normalized } : { ...identity, state: "unavailable", reason: "unknown" };
		} catch {
			if (callerSignal.aborted) throw callerSignal.reason;
			return timeout.signal.aborted
				? { ...identity, state: "unavailable", reason: "timeout" }
				: { ...identity, state: "unavailable", reason: "unknown" };
		} finally {
			clearTimeout(timer);
		}
	}
}

function captureDefinitions(
	definitions: readonly RuntimeDependencyDefinition[],
	limits: Readonly<RuntimeHealthLimits>,
): readonly CapturedDependencyDefinition[] {
	if (definitions.length > limits.maxDependencies) {
		throw new RangeError("Runtime health dependencies exceed the configured limit.");
	}
	const ids = new Set<string>();
	const captured: CapturedDependencyDefinition[] = [];
	for (const definition of definitions) {
		if (!definition || typeof definition !== "object") throw new TypeError("Runtime health dependencies are invalid.");
		const { id, kind, requirement, inspect } = definition;
		assertOpaqueIdentifier(id);
		if (encoder.encode(id).byteLength > limits.maxDependencyIdBytes) {
			throw new RangeError("Runtime health dependency id exceeds the configured byte limit.");
		}
		if (!isDependencyKind(kind) || !isDependencyRequirement(requirement)) {
			throw new TypeError("Runtime health dependency kind and requirement are invalid.");
		}
		if (inspect !== undefined && typeof inspect !== "function") {
			throw new TypeError("Runtime health dependency inspect must be a function.");
		}
		if (ids.has(id)) throw new TypeError("Runtime health dependency ids must be unique.");
		ids.add(id);
		captured.push(Object.freeze({ id, kind, requirement, ...(inspect === undefined ? {} : { inspect }) }));
	}
	return Object.freeze(captured);
}

function prepareLimits(limits: RuntimeHealthLimits): Readonly<RuntimeHealthLimits> {
	const captured = { ...limits };
	for (const [name, value] of Object.entries(captured)) {
		if (!Number.isSafeInteger(value) || value <= 0) {
			throw new TypeError(`${name} must be a positive safe integer.`);
		}
	}
	if (captured.maxConcurrentInspections > captured.maxDependencies) {
		throw new RangeError("maxConcurrentInspections must not exceed maxDependencies.");
	}
	if (captured.inspectionTimeoutMs > NODE_MAX_TIMER_DELAY_MS) {
		throw new RangeError(`inspectionTimeoutMs must not exceed ${NODE_MAX_TIMER_DELAY_MS}.`);
	}
	return Object.freeze(captured);
}

function normalizeInspection(value: unknown): RuntimeDependencyInspection | undefined {
	if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
	const { state, reason } = value as { readonly state?: unknown; readonly reason?: unknown };
	switch (state) {
		case "declared":
			return reason === undefined ? { state: "declared" } : undefined;
		case "available":
			return reason === undefined || reason === "stale"
				? { state: "available", ...(reason === "stale" ? { reason: "stale" } : {}) }
				: undefined;
		case "ready":
			return reason === undefined ? { state: "ready" } : undefined;
		case "degraded":
		case "unavailable":
			return isDependencyReason(reason) ? { state, reason } : undefined;
		default:
			return undefined;
	}
}

function readPassiveClock(now: () => number): number {
	const value = now();
	if (!Number.isFinite(value) || value < 0)
		throw new TypeError("Passive health now must return a non-negative number.");
	return value;
}

function aggregateStatus(dependencies: readonly RuntimeDependencyHealth[]): RuntimeHealthStatus {
	let degraded = false;
	for (const dependency of dependencies) {
		if (dependency.requirement === "required" && dependency.state !== "ready" && dependency.state !== "degraded") {
			return "not-ready";
		}
		if (
			dependency.state === "degraded" ||
			(dependency.requirement === "optional" && dependency.state === "unavailable")
		) {
			degraded = true;
		}
	}
	return degraded ? "degraded" : "ready";
}

function isDependencyKind(value: unknown): value is RuntimeDependencyKind {
	return (
		value === "provider" ||
		value === "model" ||
		value === "embedding" ||
		value === "mcp" ||
		value === "local-endpoint" ||
		value === "media" ||
		value === "realtime"
	);
}

function isDependencyRequirement(value: unknown): value is RuntimeDependencyRequirement {
	return value === "required" || value === "optional";
}

function isDependencyReason(value: unknown): value is RuntimeDependencyReason {
	return (
		value === "authentication-required" ||
		value === "permission-denied" ||
		value === "misconfigured" ||
		value === "timeout" ||
		value === "transport" ||
		value === "capacity" ||
		value === "capability-missing" ||
		value === "dependency-failure" ||
		value === "stale" ||
		value === "unknown"
	);
}

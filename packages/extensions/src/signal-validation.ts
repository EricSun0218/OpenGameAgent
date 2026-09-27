import {
	assertCanonicalTimestamp,
	assertGameTime,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	normalizeJsonValue,
	type GameTime,
} from "@opengameagent/protocol";
import { SignalValidationError } from "./signal-errors.js";
import type {
	GameSignalDraft,
	GameSignalKey,
	GameSignalRecord,
	GameSignalSequenceKey,
	SignalLimits,
	SignalPartition,
	SignalWaitDraft,
	SignalWaitKey,
	SignalWaitSelector,
	SignalWaitStatus,
	SignalWaitTarget,
} from "./signal-types.js";

const DECIMAL = /^(0|[1-9][0-9]*)$/u;
const encoder = new TextEncoder();
const LIMIT_NAMES = [
	"maxPayloadBytes",
	"maxSubjectsPerSignal",
	"maxCausesPerSignal",
	"maxSignalFilterKinds",
	"maxWaitKinds",
	"maxWaitSubjects",
	"maxWaitMatchesPerSignal",
	"maxSignalPageSize",
	"maxWaitPageSize",
	"maxExpireBatchSize",
	"maxPurgeBatchSize",
] as const satisfies readonly (keyof SignalLimits)[];

export function validateSignalLimits(limits: SignalLimits): void {
	for (const name of LIMIT_NAMES) {
		const value = limits[name];
		if (!Number.isSafeInteger(value) || value < 1 || value >= Number.MAX_SAFE_INTEGER) {
			throw new SignalValidationError(`invalid_signal_limit:${name}`);
		}
	}
}

export function normalizeSignalPartition(partition: SignalPartition): SignalPartition {
	assertOpaqueIdentifier(partition.ownerId);
	assertOpaqueIdentifier(partition.timelineId);
	validateSignalDecimal(partition.generation, "generation");
	if (partition.sessionId !== undefined) assertOpaqueIdentifier(partition.sessionId);
	if (partition.actorId !== undefined) assertOpaqueIdentifier(partition.actorId);
	return {
		ownerId: partition.ownerId,
		timelineId: partition.timelineId,
		generation: partition.generation,
		...(partition.sessionId === undefined ? {} : { sessionId: partition.sessionId }),
		...(partition.actorId === undefined ? {} : { actorId: partition.actorId }),
	};
}

export function normalizeGameSignal(input: GameSignalDraft, limits: SignalLimits): GameSignalDraft {
	assertOpaqueIdentifier(input.signalId);
	const partition = normalizeSignalPartition(input.partition);
	assertOpaqueIdentifier(input.kind);
	const payload = normalizeJsonValue(input.payload);
	if (encoder.encode(canonicalizeJsonValue(payload)).byteLength > limits.maxPayloadBytes) {
		throw new SignalValidationError("signal_payload_too_large");
	}
	const gameTime = normalizeSignalGameTime(input.gameTime, partition);
	const subjectIds = normalizeIdentifierSet(input.subjectIds, limits.maxSubjectsPerSignal, "too_many_signal_subjects");
	const causeIds = normalizeIdentifierSet(input.causeIds, limits.maxCausesPerSignal, "too_many_signal_causes");
	validateSignalTimestamp(input.recordedAt);
	return {
		signalId: input.signalId,
		partition,
		kind: input.kind,
		payload,
		gameTime,
		subjectIds,
		causeIds,
		recordedAt: input.recordedAt,
	};
}

export function normalizeSignalWait(input: SignalWaitDraft, limits: SignalLimits): SignalWaitDraft {
	assertOpaqueIdentifier(input.waitId);
	const partition = normalizeSignalPartition(input.partition);
	const selector = normalizeSignalWaitSelector(input.selector, limits);
	const target = normalizeSignalWaitTarget(input.target);
	validateSignalTimestamp(input.recordedAt);
	return { waitId: input.waitId, partition, selector, target, recordedAt: input.recordedAt };
}

export function normalizeSignalWaitSelector(selector: SignalWaitSelector, limits: SignalLimits): SignalWaitSelector {
	const kinds = normalizeIdentifierSet(selector.kinds, limits.maxWaitKinds, "too_many_signal_wait_kinds");
	if (kinds.length === 0) throw new SignalValidationError("empty_signal_wait_kinds");
	const subjects =
		selector.subjectIdsAny === undefined
			? undefined
			: normalizeIdentifierSet(selector.subjectIdsAny, limits.maxWaitSubjects, "too_many_signal_wait_subjects");
	if (subjects?.length === 0) throw new SignalValidationError("empty_signal_wait_subjects");
	validateSignalCursor(selector.afterSignalKey);
	if (selector.afterTickExclusive !== undefined) validateSignalDecimal(selector.afterTickExclusive, "after_tick");
	if (selector.throughTickInclusive !== undefined) {
		validateSignalDecimal(selector.throughTickInclusive, "through_tick");
	}
	if (
		selector.afterTickExclusive !== undefined &&
		selector.throughTickInclusive !== undefined &&
		compareSignalTicks(selector.afterTickExclusive, selector.throughTickInclusive) >= 0
	) {
		throw new SignalValidationError("invalid_signal_wait_tick_range");
	}
	return {
		kinds,
		...(subjects === undefined ? {} : { subjectIdsAny: subjects }),
		...(selector.afterSignalKey === undefined ? {} : { afterSignalKey: selector.afterSignalKey }),
		...(selector.afterTickExclusive === undefined ? {} : { afterTickExclusive: selector.afterTickExclusive }),
		...(selector.throughTickInclusive === undefined ? {} : { throughTickInclusive: selector.throughTickInclusive }),
	};
}

export function normalizeSignalWaitTarget(target: SignalWaitTarget): SignalWaitTarget {
	if (target.kind === "goal_step") {
		assertOpaqueIdentifier(target.goalId);
		assertOpaqueIdentifier(target.stepId);
		return { kind: "goal_step", goalId: target.goalId, stepId: target.stepId };
	}
	if (target.kind === "correlation") {
		assertOpaqueIdentifier(target.correlationId);
		return { kind: "correlation", correlationId: target.correlationId };
	}
	throw new SignalValidationError("invalid_signal_wait_target");
}

export function validateGameSignalKey(key: GameSignalKey): void {
	assertOpaqueIdentifier(key.ownerId);
	assertOpaqueIdentifier(key.timelineId);
	validateSignalDecimal(key.generation, "generation");
	assertOpaqueIdentifier(key.signalId);
}

export function validateGameSignalSequenceKey(key: GameSignalSequenceKey): void {
	assertOpaqueIdentifier(key.ownerId);
	assertOpaqueIdentifier(key.timelineId);
	validateSignalDecimal(key.generation, "generation");
	validatePositiveSignalKey(key.signalKey, "signal_key");
}

export function validateSignalWaitKey(key: SignalWaitKey): void {
	assertOpaqueIdentifier(key.ownerId);
	assertOpaqueIdentifier(key.timelineId);
	validateSignalDecimal(key.generation, "generation");
	assertOpaqueIdentifier(key.waitId);
}

export function normalizeSignalGameTime(gameTime: GameTime, partition: SignalPartition): GameTime {
	assertGameTime(gameTime);
	if (gameTime.timelineId !== partition.timelineId || gameTime.generation !== partition.generation) {
		throw new SignalValidationError("signal_time_partition_mismatch");
	}
	return { ...gameTime };
}

export function validateSignalTimestamp(value: string): void {
	try {
		assertCanonicalTimestamp(value);
	} catch {
		throw new SignalValidationError("invalid_signal_timestamp");
	}
}

export function validateSignalStatus(value: string): asserts value is SignalWaitStatus {
	if (value !== "waiting" && value !== "matched" && value !== "cancelled" && value !== "expired") {
		throw new SignalValidationError("invalid_signal_wait_status");
	}
}

export function normalizeSignalStatusFilter(
	values: readonly SignalWaitStatus[] | undefined,
): readonly SignalWaitStatus[] | undefined {
	if (values === undefined) return undefined;
	if (!Array.isArray(values) || values.length === 0) {
		throw new SignalValidationError("empty_signal_wait_status_filter");
	}
	for (const value of values) validateSignalStatus(value);
	const normalized = [...values].sort();
	if (new Set(normalized).size !== normalized.length) {
		throw new SignalValidationError("duplicate_signal_wait_status_filter");
	}
	return normalized;
}

export function normalizeSignalKindFilter(
	values: readonly string[] | undefined,
	maximum: number,
): readonly string[] | undefined {
	if (values === undefined) return undefined;
	if (!Array.isArray(values) || values.length === 0) throw new SignalValidationError("empty_signal_filter");
	if (values.length > maximum) throw new SignalValidationError("too_many_signal_filter_kinds");
	const normalized = [...values];
	for (const value of normalized) assertOpaqueIdentifier(value);
	normalized.sort();
	if (new Set(normalized).size !== normalized.length) throw new SignalValidationError("duplicate_signal_filter");
	return normalized;
}

export function validateSignalDecimal(value: string, name: string): void {
	if (!DECIMAL.test(value)) throw new SignalValidationError(`invalid_signal_${name}`);
}

export function compareSignalTicks(left: string, right: string): number {
	validateSignalDecimal(left, "tick");
	validateSignalDecimal(right, "tick");
	if (left.length !== right.length) return left.length < right.length ? -1 : 1;
	return left === right ? 0 : left < right ? -1 : 1;
}

export function validateSignalCursor(value: bigint | undefined): void {
	if (value !== undefined && value < 0n) throw new SignalValidationError("invalid_signal_cursor");
}

export function validatePositiveSignalKey(value: bigint, name: string): void {
	if (value < 1n) throw new SignalValidationError(`invalid_signal_${name}`);
}

export function validateSignalPageLimit(value: number, maximum: number, code: string): void {
	if (!Number.isSafeInteger(value) || value < 1 || value > maximum) throw new SignalValidationError(code);
}

export function signalFingerprint(signal: GameSignalDraft): string {
	return canonicalizeJsonValue(signal);
}

export function signalWaitFingerprint(wait: SignalWaitDraft): string {
	return canonicalizeJsonValue({
		waitId: wait.waitId,
		partition: wait.partition,
		selector: {
			...wait.selector,
			...(wait.selector.afterSignalKey === undefined
				? {}
				: { afterSignalKey: wait.selector.afterSignalKey.toString() }),
		},
		target: wait.target,
		recordedAt: wait.recordedAt,
	});
}

export function signalWaitMatches(wait: SignalWaitDraft, signal: GameSignalRecord): boolean {
	return (
		sameSignalGeneration(wait.partition, signal.partition) &&
		(wait.partition.sessionId === undefined || wait.partition.sessionId === signal.partition.sessionId) &&
		(wait.partition.actorId === undefined || wait.partition.actorId === signal.partition.actorId) &&
		wait.selector.kinds.includes(signal.kind) &&
		(wait.selector.subjectIdsAny === undefined ||
			wait.selector.subjectIdsAny.some((subjectId) => signal.subjectIds.includes(subjectId))) &&
		(wait.selector.afterSignalKey === undefined || signal.signalKey > wait.selector.afterSignalKey) &&
		(wait.selector.afterTickExclusive === undefined ||
			compareSignalTicks(signal.gameTime.tick, wait.selector.afterTickExclusive) > 0) &&
		(wait.selector.throughTickInclusive === undefined ||
			compareSignalTicks(signal.gameTime.tick, wait.selector.throughTickInclusive) <= 0)
	);
}

export function sameSignalGeneration(
	left: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
	right: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
): boolean {
	return left.ownerId === right.ownerId && left.timelineId === right.timelineId && left.generation === right.generation;
}

export function laterSignalTimestamp(left: string, right: string): string {
	validateSignalTimestamp(left);
	validateSignalTimestamp(right);
	return left < right ? right : left;
}

function normalizeIdentifierSet(values: readonly string[], maximum: number, code: string): readonly string[] {
	if (!Array.isArray(values)) throw new SignalValidationError("invalid_signal_identifier_set");
	if (values.length > maximum) throw new SignalValidationError(code);
	const normalized = [...values];
	for (const value of normalized) assertOpaqueIdentifier(value);
	normalized.sort();
	for (let index = 1; index < normalized.length; index += 1) {
		if (normalized[index] === normalized[index - 1]) throw new SignalValidationError("duplicate_signal_identifier");
	}
	return normalized;
}

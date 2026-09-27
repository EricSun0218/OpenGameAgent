import {
	assertCanonicalTimestamp,
	assertGameTime,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	captureWorkAuthorityLane,
	captureWorkAuthorityRetirementRequest,
	type GameTime,
	normalizeJsonValue,
	type WorkAuthorityLane,
	type WorkAuthorityRetirementRequest,
} from "@opengameagent/protocol";
import { ScheduleValidationError } from "./schedule-errors.js";
import type {
	GameScheduleDraft,
	GameScheduleKey,
	GameScheduleSequenceKey,
	GameScheduleStatus,
	ScheduleAdvanceDraft,
	ScheduledOccurrenceListRequest,
	ScheduledOccurrenceRecord,
	ScheduledOccurrenceSequenceKey,
	ScheduledOccurrenceStatus,
	ScheduleLimits,
	ScheduleListRequest,
	ScheduleOccurrenceClaimRequest,
	SchedulePartition,
} from "./schedule-types.js";

const DECIMAL = /^(0|[1-9][0-9]*)$/u;
const encoder = new TextEncoder();
const LIMIT_NAMES = [
	"maxIdentifierBytes",
	"maxPayloadBytes",
	"maxSubjectsPerSchedule",
	"maxCausesPerSchedule",
	"maxTickDigits",
	"maxCounterDigits",
	"maxActiveSchedulesPerTimelineGeneration",
	"maxOccurrencesPerAdvance",
	"maxPendingOccurrencesPerTimelineGeneration",
	"maxSchedulePageSize",
	"maxOccurrencePageSize",
	"maxClaimBatchSize",
	"maxPurgeBatchSize",
] as const satisfies readonly (keyof ScheduleLimits)[];

export function validateScheduleLimits(limits: ScheduleLimits): void {
	for (const name of LIMIT_NAMES) {
		const value = limits[name];
		if (!Number.isSafeInteger(value) || value < 1 || value >= Number.MAX_SAFE_INTEGER) {
			throw new ScheduleValidationError(`invalid_schedule_limit:${name}`);
		}
	}
}

export function normalizeSchedulePartition(
	partition: SchedulePartition,
	limits: Pick<ScheduleLimits, "maxIdentifierBytes" | "maxCounterDigits">,
): SchedulePartition {
	const { ownerId, timelineId, generation, sessionId, actorId } = partition;
	validateScheduleIdentifier(ownerId, limits.maxIdentifierBytes, "owner_id");
	validateScheduleIdentifier(timelineId, limits.maxIdentifierBytes, "timeline_id");
	validateScheduleDecimal(generation, "generation", limits.maxCounterDigits);
	if (sessionId !== undefined) {
		validateScheduleIdentifier(sessionId, limits.maxIdentifierBytes, "session_id");
	}
	if (actorId !== undefined) {
		validateScheduleIdentifier(actorId, limits.maxIdentifierBytes, "actor_id");
	}
	return Object.freeze({
		ownerId,
		timelineId,
		generation,
		...(sessionId === undefined ? {} : { sessionId }),
		...(actorId === undefined ? {} : { actorId }),
	});
}

export function normalizeScheduleAuthorityLane(input: WorkAuthorityLane, limits: ScheduleLimits): WorkAuthorityLane {
	const lane = captureWorkAuthorityLane(input);
	normalizeSchedulePartition(lane, limits);
	return lane;
}

export function normalizeScheduleAuthorityRetirement(
	input: WorkAuthorityRetirementRequest,
	limits: ScheduleLimits,
): WorkAuthorityRetirementRequest {
	const request = captureWorkAuthorityRetirementRequest(input);
	normalizeScheduleAuthorityLane(request.partition, limits);
	validateScheduleIdentifier(request.retirementId, limits.maxIdentifierBytes, "retirement_id");
	return request;
}

export function sameScheduleAuthorityLane(left: SchedulePartition, right: WorkAuthorityLane): boolean {
	return sameScheduleGeneration(left, right) && left.sessionId === right.sessionId && left.actorId === right.actorId;
}

export function normalizeGameSchedule(input: GameScheduleDraft, limits: ScheduleLimits): GameScheduleDraft {
	const { scheduleId, kind, recordedAt, intervalTicks: requestedIntervalTicks } = input;
	validateScheduleIdentifier(scheduleId, limits.maxIdentifierBytes, "schedule_id");
	const partition = normalizeSchedulePartition(input.partition, limits);
	validateScheduleIdentifier(kind, limits.maxIdentifierBytes, "kind");
	const payload = normalizeJsonValue(input.payload);
	if (encoder.encode(canonicalizeJsonValue(payload)).byteLength > limits.maxPayloadBytes) {
		throw new ScheduleValidationError("schedule_payload_too_large");
	}
	const dueTime = normalizeScheduleGameTime(input.dueTime, partition, limits.maxTickDigits);
	const intervalTicks =
		requestedIntervalTicks === undefined
			? undefined
			: validatePositiveScheduleDecimal(requestedIntervalTicks, "interval_ticks", limits.maxTickDigits);
	let maximumOccurrences = input.maximumOccurrences;
	if (maximumOccurrences !== undefined) {
		maximumOccurrences = validatePositiveScheduleDecimal(
			maximumOccurrences,
			"maximum_occurrences",
			limits.maxCounterDigits,
		);
	}
	if (intervalTicks === undefined) {
		if (maximumOccurrences !== undefined && maximumOccurrences !== "1") {
			throw new ScheduleValidationError("invalid_one_shot_maximum_occurrences");
		}
		maximumOccurrences = "1";
	}
	const subjectIds = normalizeScheduleIdentifiers(
		input.subjectIds,
		limits.maxSubjectsPerSchedule,
		limits.maxIdentifierBytes,
		"subjects",
	);
	const causeIds = normalizeScheduleIdentifiers(
		input.causeIds,
		limits.maxCausesPerSchedule,
		limits.maxIdentifierBytes,
		"causes",
	);
	validateScheduleTimestamp(recordedAt);
	return {
		scheduleId,
		partition,
		kind,
		payload,
		dueTime,
		...(intervalTicks === undefined ? {} : { intervalTicks }),
		...(maximumOccurrences === undefined ? {} : { maximumOccurrences }),
		subjectIds,
		causeIds,
		recordedAt,
	};
}

export function normalizeScheduleAdvance(input: ScheduleAdvanceDraft, limits: ScheduleLimits): ScheduleAdvanceDraft {
	const { advanceId, recordedAt } = input;
	validateScheduleIdentifier(advanceId, limits.maxIdentifierBytes, "advance_id");
	const captured = normalizeSchedulePartition(input.partition, limits);
	const partition = { ownerId: captured.ownerId, timelineId: captured.timelineId, generation: captured.generation };
	const fromExclusive = normalizeScheduleGameTime(input.fromExclusive, partition, limits.maxTickDigits);
	const toInclusive = normalizeScheduleGameTime(input.toInclusive, partition, limits.maxTickDigits);
	if (compareScheduleDecimals(toInclusive.tick, fromExclusive.tick) <= 0) {
		throw new ScheduleValidationError("schedule_advance_not_forward");
	}
	validateScheduleTimestamp(recordedAt);
	return { advanceId, partition, fromExclusive, toInclusive, recordedAt };
}

export function normalizeScheduleClaim(
	request: ScheduleOccurrenceClaimRequest,
	limits: ScheduleLimits,
): ScheduleOccurrenceClaimRequest {
	const partition = normalizeSchedulePartition(request, limits);
	const { leaseOwnerId, operationalNow, leaseExpiresAt, limit } = request;
	validateScheduleIdentifier(leaseOwnerId, limits.maxIdentifierBytes, "lease_owner_id");
	validateScheduleTimestamp(operationalNow);
	validateScheduleTimestamp(leaseExpiresAt);
	if (leaseExpiresAt <= operationalNow) {
		throw new ScheduleValidationError("invalid_schedule_lease_window");
	}
	validateSchedulePageLimit(limit, limits.maxClaimBatchSize, "invalid_schedule_claim_limit");
	return {
		...partition,
		leaseOwnerId,
		operationalNow,
		leaseExpiresAt,
		limit,
	};
}

export function normalizeScheduleListRequest(
	request: ScheduleListRequest,
	limits: ScheduleLimits,
): ScheduleListRequest {
	const partition = normalizeSchedulePartition(request, limits);
	const { afterScheduleKey, limit } = request;
	const statuses = normalizeScheduleStatusFilter(request.statuses);
	validateScheduleCursor(afterScheduleKey);
	validateSchedulePageLimit(limit, limits.maxSchedulePageSize, "invalid_schedule_page_limit");
	return Object.freeze({
		...partition,
		...(statuses === undefined ? {} : { statuses }),
		...(afterScheduleKey === undefined ? {} : { afterScheduleKey }),
		limit,
	});
}

export function normalizeScheduledOccurrenceListRequest(
	request: ScheduledOccurrenceListRequest,
	limits: ScheduleLimits,
): ScheduledOccurrenceListRequest {
	const partition = normalizeSchedulePartition(request, limits);
	const { afterOccurrenceKey, limit } = request;
	const statuses = normalizeScheduledOccurrenceStatusFilter(request.statuses);
	validateScheduleCursor(afterOccurrenceKey);
	validateSchedulePageLimit(limit, limits.maxOccurrencePageSize, "invalid_scheduled_occurrence_page_limit");
	return Object.freeze({
		...partition,
		...(statuses === undefined ? {} : { statuses }),
		...(afterOccurrenceKey === undefined ? {} : { afterOccurrenceKey }),
		limit,
	});
}

export function validateGameScheduleKey(key: GameScheduleKey, limits: ScheduleLimits): GameScheduleKey {
	const { ownerId, timelineId, generation } = normalizeSchedulePartition(key, limits);
	const scheduleId = key.scheduleId;
	validateScheduleIdentifier(scheduleId, limits.maxIdentifierBytes, "schedule_id");
	return Object.freeze({ ownerId, timelineId, generation, scheduleId });
}

export function validateGameScheduleSequenceKey(
	key: GameScheduleSequenceKey,
	limits: ScheduleLimits,
): GameScheduleSequenceKey {
	const { ownerId, timelineId, generation } = normalizeSchedulePartition(key, limits);
	const scheduleKey = key.scheduleKey;
	validatePositiveScheduleKey(scheduleKey, "schedule_key");
	return Object.freeze({ ownerId, timelineId, generation, scheduleKey });
}

export function validateScheduledOccurrenceSequenceKey(
	key: ScheduledOccurrenceSequenceKey,
	limits: ScheduleLimits,
): ScheduledOccurrenceSequenceKey {
	const { ownerId, timelineId, generation } = normalizeSchedulePartition(key, limits);
	const occurrenceKey = key.occurrenceKey;
	validatePositiveScheduleKey(occurrenceKey, "occurrence_key");
	return Object.freeze({ ownerId, timelineId, generation, occurrenceKey });
}

export function normalizeScheduleGameTime(
	gameTime: GameTime,
	partition: Pick<SchedulePartition, "timelineId" | "generation">,
	maxTickDigits: number,
): GameTime {
	try {
		gameTime = { timelineId: gameTime.timelineId, generation: gameTime.generation, tick: gameTime.tick };
		assertGameTime(gameTime);
	} catch {
		throw new ScheduleValidationError("invalid_schedule_game_time");
	}
	if (gameTime.timelineId !== partition.timelineId || gameTime.generation !== partition.generation) {
		throw new ScheduleValidationError("schedule_time_partition_mismatch");
	}
	validateScheduleDecimal(gameTime.tick, "tick", maxTickDigits);
	return { ...gameTime };
}

export function validateScheduleIdentifier(value: string, maximumBytes: number, name: string): void {
	try {
		assertOpaqueIdentifier(value);
	} catch {
		throw new ScheduleValidationError(`invalid_schedule_${name}`);
	}
	if (encoder.encode(value).byteLength > maximumBytes) {
		throw new ScheduleValidationError(`schedule_${name}_too_large`);
	}
}

export function validateScheduleTimestamp(value: string): void {
	try {
		assertCanonicalTimestamp(value);
	} catch {
		throw new ScheduleValidationError("invalid_schedule_timestamp");
	}
}

export function validateScheduleDecimal(value: string, name: string, maximumDigits: number): string {
	if (!DECIMAL.test(value) || value.length > maximumDigits) {
		throw new ScheduleValidationError(`invalid_schedule_${name}`);
	}
	return value;
}

export function validatePositiveScheduleDecimal(value: string, name: string, maximumDigits: number): string {
	validateScheduleDecimal(value, name, maximumDigits);
	if (value === "0") throw new ScheduleValidationError(`invalid_schedule_${name}`);
	return value;
}

export function compareScheduleDecimals(left: string, right: string): number {
	if (!DECIMAL.test(left) || !DECIMAL.test(right)) throw new ScheduleValidationError("invalid_schedule_decimal");
	if (left.length !== right.length) return left.length < right.length ? -1 : 1;
	return left === right ? 0 : left < right ? -1 : 1;
}

export function addScheduleDecimals(left: string, right: string, maximumDigits: number): string {
	validateScheduleDecimal(left, "tick", maximumDigits);
	validateScheduleDecimal(right, "interval_ticks", maximumDigits);
	const result = (BigInt(left) + BigInt(right)).toString();
	if (result.length > maximumDigits) throw new ScheduleValidationError("schedule_tick_overflow");
	return result;
}

export function incrementScheduleDecimal(value: string, maximumDigits: number, name: string): string {
	validateScheduleDecimal(value, name, maximumDigits);
	const result = (BigInt(value) + 1n).toString();
	if (result.length > maximumDigits) throw new ScheduleValidationError(`schedule_${name}_overflow`);
	return result;
}

export function laterScheduleTimestamp(left: string, right: string): string {
	validateScheduleTimestamp(left);
	validateScheduleTimestamp(right);
	return left < right ? right : left;
}

export function validatePositiveScheduleKey(value: bigint, name: string): void {
	if (value < 1n) throw new ScheduleValidationError(`invalid_schedule_${name}`);
}

export function validateScheduleCursor(value: bigint | undefined): void {
	if (value !== undefined && value < 0n) throw new ScheduleValidationError("invalid_schedule_cursor");
}

export function validateSchedulePageLimit(value: number, maximum: number, code: string): void {
	if (!Number.isSafeInteger(value) || value < 1 || value > maximum) throw new ScheduleValidationError(code);
}

export function validateScheduleStatus(value: string): asserts value is GameScheduleStatus {
	if (value !== "active" && value !== "completed" && value !== "cancelled") {
		throw new ScheduleValidationError("invalid_schedule_status");
	}
}

export function normalizeScheduleStatusFilter(
	values: readonly GameScheduleStatus[] | undefined,
): readonly GameScheduleStatus[] | undefined {
	return normalizeStatusFilter(values, validateScheduleStatus, "schedule");
}

export function validateScheduledOccurrenceStatus(value: string): asserts value is ScheduledOccurrenceStatus {
	if (value !== "pending" && value !== "completed" && value !== "cancelled") {
		throw new ScheduleValidationError("invalid_scheduled_occurrence_status");
	}
}

export function normalizeScheduledOccurrenceStatusFilter(
	values: readonly ScheduledOccurrenceStatus[] | undefined,
): readonly ScheduledOccurrenceStatus[] | undefined {
	return normalizeStatusFilter(values, validateScheduledOccurrenceStatus, "scheduled_occurrence");
}

export function scheduleFingerprint(schedule: GameScheduleDraft): string {
	return canonicalizeJsonValue(schedule);
}

export function scheduleAdvanceFingerprint(advance: ScheduleAdvanceDraft): string {
	return canonicalizeJsonValue(advance);
}

export function scheduledOccurrenceFingerprint(occurrence: ScheduledOccurrenceRecord): string {
	return canonicalizeJsonValue({
		signalId: occurrence.signalId,
		scheduleId: occurrence.scheduleId,
		ordinal: occurrence.ordinal,
		advanceId: occurrence.advanceId,
		partition: occurrence.partition,
		kind: occurrence.kind,
		payload: occurrence.payload,
		dueTime: occurrence.dueTime,
		subjectIds: occurrence.subjectIds,
		causeIds: occurrence.causeIds,
		createdAt: occurrence.createdAt,
	});
}

export function sameScheduleGeneration(
	left: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
	right: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
): boolean {
	return left.ownerId === right.ownerId && left.timelineId === right.timelineId && left.generation === right.generation;
}

function normalizeScheduleIdentifiers(
	values: readonly string[],
	maximumItems: number,
	maximumBytes: number,
	name: string,
): readonly string[] {
	if (!Array.isArray(values)) throw new ScheduleValidationError(`invalid_schedule_${name}`);
	if (values.length > maximumItems) throw new ScheduleValidationError(`too_many_schedule_${name}`);
	const normalized = [...values];
	for (const value of normalized) validateScheduleIdentifier(value, maximumBytes, name);
	normalized.sort();
	for (let index = 1; index < normalized.length; index += 1) {
		if (normalized[index] === normalized[index - 1]) {
			throw new ScheduleValidationError(`duplicate_schedule_${name}`);
		}
	}
	return normalized;
}

function normalizeStatusFilter<T extends string>(
	values: readonly T[] | undefined,
	validate: (value: string) => void,
	name: string,
): readonly T[] | undefined {
	if (values === undefined) return undefined;
	if (!Array.isArray(values) || values.length === 0) {
		throw new ScheduleValidationError(`empty_${name}_status_filter`);
	}
	const normalized = [...values];
	for (const value of normalized) validate(value);
	normalized.sort();
	if (new Set(normalized).size !== normalized.length) {
		throw new ScheduleValidationError(`duplicate_${name}_status_filter`);
	}
	return Object.freeze(normalized);
}

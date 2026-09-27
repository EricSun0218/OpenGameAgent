import {
	assertCanonicalTimestamp,
	assertGameTime,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	canonicalizeJsonValueWithin,
	type GameTime,
	JsonValueLimitError,
} from "@opengameagent/protocol";
import { GoalValidationError } from "./goal-errors.js";
import type {
	GoalAuthorityRetirementDraft,
	GoalDraft,
	GoalEvidence,
	GoalKey,
	GoalLimits,
	GoalPartition,
	GoalPlanStep,
	GoalRecord,
	GoalReplacement,
	GoalStatus,
} from "./goal-types.js";

const DECIMAL = /^(0|[1-9][0-9]*)$/u;
const SHA256_HEX = /^[0-9a-f]{64}$/u;
const encoder = new TextEncoder();
const GOAL_PLAN_JSON_DEPTH = 5;
const GOAL_RECORD_JSON_DEPTH = 6;
const GOAL_MODEL_RESULT_JSON_DEPTH = 6;
const GOAL_NOT_FOUND_RESULT = canonicalizeJsonValue({ found: false });
const MIN_GOAL_MODEL_RESULT_BYTES = encoder.encode(GOAL_NOT_FOUND_RESULT).byteLength;
const LIMIT_NAMES = [
	"maxObjectiveBytes",
	"maxStepsPerGoal",
	"maxStepBytes",
	"maxPlanBytes",
	"maxRecordBytes",
	"maxModelResultBytes",
	"maxEvidencePerStep",
	"maxEvidenceKindBytes",
	"maxEvidenceReferenceBytes",
	"maxOpenGoalsPerOwnerGeneration",
	"maxListPageSize",
	"maxPurgeBatchSize",
] as const satisfies readonly (keyof GoalLimits)[];

export function validateGoalLimits(limits: GoalLimits): void {
	captureGoalLimits(limits);
}

export function captureGoalLimits(limits: GoalLimits): Readonly<GoalLimits> {
	const captured = {} as Record<keyof GoalLimits, number>;
	for (const name of LIMIT_NAMES) {
		const value = limits[name];
		if (!Number.isSafeInteger(value) || value < 1 || value >= Number.MAX_SAFE_INTEGER) {
			throw new GoalValidationError(`invalid_limit:${name}`);
		}
		captured[name] = value;
	}
	if (captured.maxModelResultBytes < MIN_GOAL_MODEL_RESULT_BYTES) {
		throw new GoalValidationError("invalid_limit:maxModelResultBytes");
	}
	return Object.freeze(captured as unknown as GoalLimits);
}

export function normalizeGoalPartition(partition: GoalPartition): GoalPartition {
	const ownerId = partition.ownerId;
	const timelineId = partition.timelineId;
	const generation = partition.generation;
	const sessionId = partition.sessionId;
	const actorId = partition.actorId;
	assertOpaqueIdentifier(ownerId);
	assertOpaqueIdentifier(timelineId);
	validateDecimal(generation, "generation");
	if (sessionId !== undefined) assertOpaqueIdentifier(sessionId);
	if (actorId !== undefined) assertOpaqueIdentifier(actorId);
	return {
		ownerId,
		timelineId,
		generation,
		...(sessionId === undefined ? {} : { sessionId }),
		...(actorId === undefined ? {} : { actorId }),
	};
}

export function normalizeGoalKey(key: GoalKey): GoalKey {
	const goalId = key.goalId;
	const partition = normalizeGoalPartition(key);
	assertOpaqueIdentifier(goalId);
	return { ...partition, goalId };
}

export function validateGoalKey(key: GoalKey): void {
	normalizeGoalKey(key);
}

export function normalizeGoalDraft(input: GoalDraft, limits: GoalLimits): GoalDraft {
	const goalId = input.goalId;
	const partitionInput = input.partition;
	const objective = input.objective;
	const planInput = input.plan;
	const gameTimeInput = input.gameTime;
	const recordedAt = input.recordedAt;
	const partition = normalizeGoalPartition(partitionInput);
	validateGoalKey({ ...partition, goalId });
	validateObjective(objective, limits);
	const plan = normalizeGoalPlan(planInput, limits);
	if (plan.some((step) => step.status === "completed")) {
		throw new GoalValidationError("completed_step_in_new_goal");
	}
	const gameTime = normalizeGoalTime(gameTimeInput, partition);
	validateGoalTimestamp(recordedAt);
	return { goalId, partition, objective, plan, gameTime, recordedAt };
}

export function normalizeGoalReplacement(input: GoalReplacement, key: GoalKey, limits: GoalLimits): GoalReplacement {
	const planInput = input.plan;
	const status = input.status;
	const gameTimeInput = input.gameTime;
	const recordedAt = input.recordedAt;
	const lastHostAdvanceFingerprint = input.lastHostAdvanceFingerprint;
	const partition = normalizeGoalPartition(key);
	const plan = normalizeGoalPlan(planInput, limits);
	validateGoalStatus(status);
	const gameTime = normalizeGoalTime(gameTimeInput, partition);
	validateGoalTimestamp(recordedAt);
	validateGoalHostAdvanceFingerprint(lastHostAdvanceFingerprint);
	return { plan, status, gameTime, recordedAt, lastHostAdvanceFingerprint };
}

export function normalizeGoalAuthorityRetirement(input: GoalAuthorityRetirementDraft): GoalAuthorityRetirementDraft {
	const retirementId = input.retirementId;
	const stateRevision = input.stateRevision;
	const partitionInput = input.partition;
	const gameTimeInput = input.gameTime;
	const recordedAt = input.recordedAt;
	const partition = normalizeGoalPartition(partitionInput);
	const gameTime = normalizeGoalTime(gameTimeInput, partition);
	assertOpaqueIdentifier(retirementId);
	validateGoalRevision(stateRevision);
	validateGoalTimestamp(recordedAt);
	return { retirementId, stateRevision, partition, gameTime, recordedAt };
}

export function validateGoalHostAdvanceFingerprint(value: string | null): void {
	if (value !== null && !SHA256_HEX.test(value)) {
		throw new GoalValidationError("invalid_goal_host_advance_fingerprint");
	}
}

export function normalizeGoalPlan(plan: unknown, limits: GoalLimits): readonly GoalPlanStep[] {
	if (!Array.isArray(plan)) throw new GoalValidationError("invalid_goal_plan");
	const captured = [...plan];
	if (captured.length > limits.maxStepsPerGoal) throw new GoalValidationError("too_many_goal_steps");
	const stepIds = new Set<string>();
	const normalized = captured.map((value) => {
		const step = asRecord(value, "invalid_goal_step");
		const stepId = step.stepId;
		const description = step.description;
		const status = step.status;
		const evidenceInput = step.evidence;
		if (
			typeof stepId !== "string" ||
			typeof description !== "string" ||
			typeof status !== "string" ||
			!Array.isArray(evidenceInput) ||
			Object.keys(step).some(
				(key) => key !== "stepId" && key !== "description" && key !== "status" && key !== "evidence",
			)
		) {
			throw new GoalValidationError("invalid_goal_step");
		}
		assertOpaqueIdentifier(stepId);
		if (stepIds.has(stepId)) throw new GoalValidationError("duplicate_goal_step");
		stepIds.add(stepId);
		validateBytes(description, limits.maxStepBytes, "invalid_goal_step_bytes");
		if (status !== "pending" && status !== "in_progress" && status !== "completed") {
			throw new GoalValidationError("invalid_goal_step_status");
		}
		const normalizedStatus: GoalPlanStep["status"] = status;
		if (evidenceInput.length > limits.maxEvidencePerStep) {
			throw new GoalValidationError("too_many_goal_evidence_items");
		}
		const evidence = normalizeGoalEvidenceList(evidenceInput, limits);
		if (status !== "completed" && evidence.length > 0) {
			throw new GoalValidationError("evidence_on_incomplete_step");
		}
		return {
			stepId,
			description,
			status: normalizedStatus,
			evidence,
		};
	});
	validatePlanShape(normalized);
	serializeGoalPlan(normalized, limits);
	return normalized;
}

export function serializeGoalPlan(plan: readonly GoalPlanStep[], limits: GoalLimits): string {
	return serializeBoundedGoalJson(plan, limits.maxPlanBytes, GOAL_PLAN_JSON_DEPTH, "goal_plan_too_large");
}

export function serializeGoalRecord(record: GoalRecord, limits: GoalLimits): string {
	return serializeBoundedGoalJson(
		{
			goalId: record.goalId,
			partition: record.partition,
			objective: record.objective,
			plan: record.plan,
			gameTime: record.gameTime,
			goalKey: record.goalKey.toString(),
			revision: record.revision,
			status: record.status,
			createdGameTime: record.createdGameTime,
			createdAt: record.createdAt,
			updatedAt: record.updatedAt,
			lastHostAdvanceFingerprint: record.lastHostAdvanceFingerprint,
		},
		limits.maxRecordBytes,
		GOAL_RECORD_JSON_DEPTH,
		"goal_record_too_large",
	);
}

export function serializeGoalModelResult(
	record: Pick<GoalRecord, "goalId" | "revision" | "status" | "objective" | "plan">,
	limits: GoalLimits,
): string {
	return serializeBoundedGoalJson(
		{
			goalId: record.goalId,
			revision: record.revision,
			status: record.status,
			objective: record.objective,
			plan: record.plan,
		},
		limits.maxModelResultBytes,
		GOAL_MODEL_RESULT_JSON_DEPTH,
		"goal_model_result_too_large",
	);
}

export function serializeGoalNotFoundResult(limits: GoalLimits): string {
	if (limits.maxModelResultBytes < MIN_GOAL_MODEL_RESULT_BYTES) {
		throw new GoalValidationError("goal_model_result_too_large");
	}
	return GOAL_NOT_FOUND_RESULT;
}

function serializeBoundedGoalJson(value: unknown, maxBytes: number, maxDepth: number, tooLargeCode: string): string {
	try {
		return canonicalizeJsonValueWithin(value, { maxBytes, maxDepth });
	} catch (error) {
		if (error instanceof JsonValueLimitError && error.code === "json_too_large") {
			throw new GoalValidationError(tooLargeCode);
		}
		throw error;
	}
}

export function normalizeGoalEvidence(value: unknown, limits: GoalLimits): GoalEvidence {
	return normalizeGoalEvidenceList([value], limits)[0] as GoalEvidence;
}

export function normalizeGoalTime(gameTime: GameTime, partition: GoalPartition): GameTime {
	const captured = {
		timelineId: gameTime.timelineId,
		generation: gameTime.generation,
		tick: gameTime.tick,
	};
	assertGameTime(captured);
	if (captured.timelineId !== partition.timelineId || captured.generation !== partition.generation) {
		throw new GoalValidationError("goal_time_partition_mismatch");
	}
	return captured;
}

export function validateGoalTimestamp(value: string): void {
	try {
		assertCanonicalTimestamp(value);
	} catch {
		throw new GoalValidationError("invalid_goal_timestamp");
	}
}

export function validateGoalRevision(value: string): void {
	validateDecimal(value, "revision");
}

export function validateGoalStatus(value: string): asserts value is GoalStatus {
	if (value !== "active" && value !== "paused" && value !== "achieved" && value !== "failed" && value !== "cancelled") {
		throw new GoalValidationError("invalid_goal_status");
	}
}

export function normalizeGoalStatusFilter(
	values: readonly GoalStatus[] | undefined,
): readonly GoalStatus[] | undefined {
	if (values === undefined) return undefined;
	if (!Array.isArray(values) || values.length === 0) throw new GoalValidationError("empty_goal_status_filter");
	for (const value of values) validateGoalStatus(value);
	const normalized = [...values].sort();
	if (new Set(normalized).size !== normalized.length) {
		throw new GoalValidationError("duplicate_goal_status_filter");
	}
	return normalized;
}

export function compareGoalTicks(left: string, right: string): number {
	validateDecimal(left, "tick");
	validateDecimal(right, "tick");
	if (left.length !== right.length) return left.length < right.length ? -1 : 1;
	return left === right ? 0 : left < right ? -1 : 1;
}

export function validateGoalPageLimit(value: number, maximum: number, code: string): void {
	if (!Number.isSafeInteger(value) || value < 1 || value > maximum) throw new GoalValidationError(code);
}

export function validateGoalAfterKey(value: bigint | undefined): void {
	if (value !== undefined && value < 0n) throw new GoalValidationError("invalid_goal_cursor");
}

export function validateObjective(value: string, limits: GoalLimits): void {
	validateBytes(value, limits.maxObjectiveBytes, "invalid_goal_objective_bytes");
}

function normalizeGoalEvidenceList(evidence: readonly unknown[], limits: GoalLimits): readonly GoalEvidence[] {
	const unique = new Set<string>();
	return [...evidence].map((value) => {
		const item = asRecord(value, "invalid_goal_evidence");
		const kind = item.kind;
		const referenceId = item.referenceId;
		if (
			typeof kind !== "string" ||
			typeof referenceId !== "string" ||
			Object.keys(item).some((key) => key !== "kind" && key !== "referenceId")
		) {
			throw new GoalValidationError("invalid_goal_evidence");
		}
		assertOpaqueIdentifier(kind);
		assertOpaqueIdentifier(referenceId);
		validateBytes(kind, limits.maxEvidenceKindBytes, "invalid_goal_evidence_kind_bytes");
		validateBytes(referenceId, limits.maxEvidenceReferenceBytes, "invalid_goal_evidence_reference_bytes");
		const key = `${kind}\0${referenceId}`;
		if (unique.has(key)) throw new GoalValidationError("duplicate_goal_evidence");
		unique.add(key);
		return { kind, referenceId };
	});
}

function asRecord(value: unknown, code: string): Record<string, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value)) throw new GoalValidationError(code);
	return value as Record<string, unknown>;
}

function validatePlanShape(plan: readonly GoalPlanStep[]): void {
	let phase: "completed" | "in_progress" | "pending" = "completed";
	for (const step of plan) {
		if (step.status === "completed") {
			if (phase !== "completed") throw new GoalValidationError("invalid_goal_plan_order");
			continue;
		}
		if (step.status === "in_progress") {
			if (phase !== "completed") throw new GoalValidationError("invalid_goal_plan_order");
			phase = "in_progress";
			continue;
		}
		phase = "pending";
	}
}

function validateBytes(value: string, maximum: number, code: string): void {
	const length = encoder.encode(value).byteLength;
	if (length < 1 || length > maximum) throw new GoalValidationError(code);
}

function validateDecimal(value: string, name: string): void {
	if (!DECIMAL.test(value)) throw new GoalValidationError(`invalid_goal_${name}`);
}

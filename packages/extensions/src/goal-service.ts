import { createHash } from "node:crypto";
import { assertOpaqueIdentifier, canonicalizeJsonValue } from "@opengameagent/protocol";
import {
	GoalEvidenceRejectedError,
	GoalNotFoundError,
	GoalRevisionConflictError,
	GoalValidationError,
} from "./goal-errors.js";
import type {
	GoalAuthorityRetirementRecord,
	GoalAuthorityRetirementRequest,
	GoalCommittedInputVerifier,
	GoalCreateRequest,
	GoalEvidenceVerifier,
	GoalHostAdvanceRequest,
	GoalHostAdvanceResult,
	GoalKey,
	GoalLimits,
	GoalListRequest,
	GoalPage,
	GoalPartition,
	GoalPlanStep,
	GoalRecord,
	GoalStatus,
	GoalStore,
	GoalUpdateRequest,
} from "./goal-types.js";
import {
	captureGoalLimits,
	compareGoalTicks,
	normalizeGoalEvidence,
	normalizeGoalKey,
	normalizeGoalPartition,
	normalizeGoalPlan,
	normalizeGoalStatusFilter,
	normalizeGoalTime,
	serializeGoalModelResult,
	validateGoalAfterKey,
	validateGoalKey,
	validateGoalPageLimit,
	validateGoalRevision,
	validateGoalStatus,
	validateGoalTimestamp,
	validateObjective,
} from "./goal-validation.js";

interface GoalHostAdvanceSnapshot {
	readonly goalId: string;
	readonly stepId: string;
	readonly inputId: string;
	readonly expectedRevision: string;
	readonly recordedAt: string;
	readonly partition: GoalPartition;
	readonly gameTime: GoalRecord["gameTime"];
	readonly evidence: GoalPlanStep["evidence"][number];
}

export class GoalService {
	readonly limits: GoalLimits;

	constructor(
		private readonly store: GoalStore,
		private readonly evidenceVerifier: GoalEvidenceVerifier,
		private readonly committedInputVerifier?: GoalCommittedInputVerifier,
	) {
		this.limits = captureGoalLimits(store.limits);
	}

	async advanceFromHost(request: GoalHostAdvanceRequest): Promise<GoalHostAdvanceResult> {
		const goalId = request.goalId;
		const stepId = request.stepId;
		const inputId = request.inputId;
		const expectedRevision = request.expectedRevision;
		const recordedAt = request.recordedAt;
		const partitionInput = request.partition;
		const gameTimeInput = request.gameTime;
		const evidenceInput = request.evidence;
		const signal = request.signal;
		assertOpaqueIdentifier(goalId);
		assertOpaqueIdentifier(stepId);
		assertOpaqueIdentifier(inputId);
		validateGoalRevision(expectedRevision);
		validateGoalTimestamp(recordedAt);
		throwIfAborted(signal);
		const partition = Object.freeze(normalizeGoalPartition(partitionInput));
		const gameTime = Object.freeze(normalizeGoalTime(gameTimeInput, partition));
		const evidence = Object.freeze(normalizeGoalEvidence(evidenceInput, this.limits));
		const snapshot: GoalHostAdvanceSnapshot = Object.freeze({
			goalId,
			stepId,
			inputId,
			expectedRevision,
			recordedAt,
			partition,
			gameTime,
			evidence,
		});
		const hostAdvanceFingerprint = fingerprintHostAdvance(snapshot);
		const key = { ...partition, goalId };
		const current = await this.store.get(key);
		throwIfAborted(signal);
		if (current === undefined || !samePartition(current.partition, partition)) return { status: "goal_not_found" };
		const index = current.plan.findIndex((step) => step.stepId === stepId);
		const step = current.plan[index];
		const alreadyAdvanced = isExactHostAdvanceReplay(current, snapshot, hostAdvanceFingerprint);
		if (alreadyAdvanced) return { status: "already_advanced", goal: current };
		if (compareGoalTicks(gameTime.tick, current.gameTime.tick) < 0) {
			throw new GoalValidationError("goal_time_regression");
		}
		if (current.revision !== expectedRevision) {
			return { status: "revision_conflict", goal: current };
		}
		if (current.status !== "active") return { status: "plan_not_active", goal: current };
		if (step?.status === "completed") {
			return { status: "step_already_completed", goal: current };
		}
		if (step === undefined || step.status !== "in_progress") {
			return { status: "step_not_current", goal: current };
		}
		if (
			this.committedInputVerifier === undefined ||
			!(await this.committedInputVerifier.validate({
				partition,
				inputId,
				...(signal === undefined ? {} : { signal }),
			}))
		) {
			return { status: "input_not_committed", goal: current };
		}
		throwIfAborted(signal);
		const accepted = await this.evidenceVerifier.validate({
			goal: current,
			step,
			evidence,
			gameTime,
			inputId,
			...(signal === undefined ? {} : { signal }),
		});
		throwIfAborted(signal);
		if (!accepted) return { status: "evidence_rejected", goal: current };
		const plan = current.plan.map((candidate, candidateIndex) => {
			if (candidateIndex === index) return { ...candidate, status: "completed" as const, evidence: [evidence] };
			if (candidateIndex === index + 1) return { ...candidate, status: "in_progress" as const };
			return candidate;
		});
		const status = index === plan.length - 1 ? "achieved" : current.status;
		this.validateNextModelResult(current, status, plan);
		try {
			const goal = await this.store.replace(key, expectedRevision, {
				plan,
				status,
				gameTime,
				recordedAt,
				lastHostAdvanceFingerprint: hostAdvanceFingerprint,
			});
			return { status: "advanced", goal };
		} catch (error) {
			if (!(error instanceof GoalRevisionConflictError)) throw error;
			const latest = await this.store.get(key);
			throwIfAborted(signal);
			if (latest === undefined || !samePartition(latest.partition, partition)) return { status: "goal_not_found" };
			return isExactHostAdvanceReplay(latest, snapshot, hostAdvanceFingerprint)
				? { status: "already_advanced", goal: latest }
				: { status: "revision_conflict", goal: latest };
		}
	}

	async create(request: GoalCreateRequest): Promise<GoalRecord> {
		const goalId = request.goalId;
		const partitionInput = request.partition;
		const objective = request.objective;
		const planInput = request.plan;
		const gameTimeInput = request.gameTime;
		const recordedAt = request.recordedAt;
		const signal = request.signal;
		assertOpaqueIdentifier(goalId);
		const partition = normalizeGoalPartition(partitionInput);
		validateObjective(objective, this.limits);
		const plan = normalizeGoalPlan(planInput, this.limits);
		if (plan.some((step) => step.status === "completed")) {
			throw new GoalValidationError("completed_step_in_new_goal");
		}
		const gameTime = normalizeGoalTime(gameTimeInput, partition);
		validateGoalTimestamp(recordedAt);
		serializeGoalModelResult({ goalId, revision: "1", status: "active", objective, plan }, this.limits);
		throwIfAborted(signal);
		const record = await this.store.create(
			{
				goalId,
				partition,
				objective,
				plan,
				gameTime,
				recordedAt,
			},
			this.limits.maxOpenGoalsPerOwnerGeneration,
			signal,
		);
		serializeGoalModelResult(record, this.limits);
		return record;
	}

	async get(key: GoalKey): Promise<GoalRecord | undefined> {
		const normalized = normalizeGoalKey(key);
		const partition = normalizeGoalPartition(normalized);
		const record = await this.store.get(normalized);
		return record === undefined || !samePartition(record.partition, partition) ? undefined : record;
	}

	async update(request: GoalUpdateRequest): Promise<GoalRecord> {
		const goalId = request.goalId;
		const expectedRevision = request.expectedRevision;
		const recordedAt = request.recordedAt;
		const planInput = request.plan;
		const statusInput = request.status;
		const gameTimeInput = request.gameTime;
		const signal = request.signal;
		const partition = normalizeGoalPartition(request);
		const key = { ...partition, goalId };
		validateGoalKey(key);
		validateGoalRevision(expectedRevision);
		validateGoalTimestamp(recordedAt);
		throwIfAborted(signal);
		if (planInput === undefined && statusInput === undefined) {
			throw new GoalValidationError("empty_goal_update");
		}
		const current = await this.store.get(key);
		throwIfAborted(signal);
		if (current === undefined || !samePartition(current.partition, partition)) throw new GoalNotFoundError();
		if (current.revision !== expectedRevision) throw new GoalRevisionConflictError();
		if (isTerminal(current.status)) throw new GoalValidationError("terminal_goal_update");
		const gameTime = normalizeGoalTime(gameTimeInput, current.partition);
		if (compareGoalTicks(gameTime.tick, current.gameTime.tick) < 0) {
			throw new GoalValidationError("goal_time_regression");
		}
		const plan = planInput === undefined ? current.plan : normalizeGoalPlan(planInput, this.limits);
		const status = statusInput ?? current.status;
		validateGoalStatus(status);
		validateStatusTransition(current.status, status, plan);
		if (planInput !== undefined) {
			if (current.status === "paused" && status !== "active" && !samePlan(current.plan, plan)) {
				throw new GoalValidationError("paused_goal_plan_update");
			}
			await this.validatePlanTransition(current, plan, gameTime, signal);
			throwIfAborted(signal);
		}
		this.validateNextModelResult(current, status, plan);
		return await this.store.replace(key, expectedRevision, {
			plan,
			status,
			gameTime,
			recordedAt,
			lastHostAdvanceFingerprint: null,
		});
	}

	async list(request: GoalListRequest): Promise<GoalPage> {
		const statusesInput = request.statuses;
		const afterGoalKey = request.afterGoalKey;
		const limit = request.limit;
		const partition = normalizeGoalPartition(request);
		const statuses = normalizeGoalStatusFilter(statusesInput);
		validateGoalAfterKey(afterGoalKey);
		validateGoalPageLimit(limit, this.limits.maxListPageSize, "invalid_goal_page_limit");
		return await this.store.list({
			...partition,
			...(statuses === undefined ? {} : { statuses }),
			...(afterGoalKey === undefined ? {} : { afterGoalKey }),
			limit,
		});
	}

	async retireAuthority(request: GoalAuthorityRetirementRequest): Promise<GoalAuthorityRetirementRecord> {
		const retirementId = request.retirementId;
		const stateRevision = request.stateRevision;
		const partitionInput = request.partition;
		const gameTimeInput = request.gameTime;
		const recordedAt = request.recordedAt;
		const signal = request.signal;
		assertOpaqueIdentifier(retirementId);
		validateGoalRevision(stateRevision);
		validateGoalTimestamp(recordedAt);
		const partition = normalizeGoalPartition(partitionInput);
		const gameTime = normalizeGoalTime(gameTimeInput, partition);
		throwIfAborted(signal);
		return await this.store.retireAuthority({ retirementId, stateRevision, partition, gameTime, recordedAt }, signal);
	}

	async purgeTerminal(
		partition: Pick<GoalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const normalized = normalizeGoalPartition(partition);
		validateGoalTimestamp(beforeRecordedAt);
		validateGoalPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_goal_purge_limit");
		return await this.store.purgeTerminal(normalized, beforeRecordedAt, limit);
	}

	private async validatePlanTransition(
		goal: GoalRecord,
		next: readonly GoalPlanStep[],
		gameTime: GoalRecord["gameTime"],
		signal: AbortSignal | undefined,
	): Promise<void> {
		const previousById = new Map(goal.plan.map((step) => [step.stepId, step]));
		const previousCompleted = goal.plan.filter((step) => step.status === "completed");
		for (const [index, previous] of previousCompleted.entries()) {
			const candidate = next[index];
			if (candidate === undefined || !sameStep(previous, candidate)) {
				throw new GoalValidationError("completed_goal_step_changed");
			}
		}
		for (const step of next) {
			const previous = previousById.get(step.stepId);
			if (previous !== undefined && previous.description !== step.description) {
				throw new GoalValidationError("goal_step_description_changed");
			}
			if (step.status !== "completed" || previous?.status === "completed") continue;
			if (previous?.status !== "in_progress") throw new GoalValidationError("goal_step_skipped_in_progress");
			if (step.evidence.length === 0) throw new GoalValidationError("missing_goal_evidence");
			for (const evidence of step.evidence) {
				throwIfAborted(signal);
				const accepted = await this.evidenceVerifier.validate({
					goal,
					step,
					evidence,
					gameTime,
					...(signal === undefined ? {} : { signal }),
				});
				throwIfAborted(signal);
				if (!accepted) throw new GoalEvidenceRejectedError();
			}
		}
	}

	private validateNextModelResult(current: GoalRecord, status: GoalStatus, plan: readonly GoalPlanStep[]): void {
		serializeGoalModelResult(
			{
				goalId: current.goalId,
				revision: (BigInt(current.revision) + 1n).toString(),
				status,
				objective: current.objective,
				plan,
			},
			this.limits,
		);
	}
}

function validateStatusTransition(current: GoalStatus, next: GoalStatus, plan: readonly GoalPlanStep[]): void {
	if (current === "active" && (next === "active" || next === "paused" || next === "failed" || next === "cancelled")) {
		return;
	}
	if (current === "paused" && (next === "paused" || next === "active" || next === "cancelled")) return;
	if (
		current === "active" &&
		next === "achieved" &&
		plan.length > 0 &&
		plan.every((step) => step.status === "completed")
	) {
		return;
	}
	throw new GoalValidationError("invalid_goal_status_transition");
}

function isTerminal(status: GoalStatus): boolean {
	return status === "achieved" || status === "failed" || status === "cancelled";
}

function sameStep(left: GoalPlanStep, right: GoalPlanStep): boolean {
	return (
		left.stepId === right.stepId &&
		left.description === right.description &&
		left.status === right.status &&
		JSON.stringify(left.evidence) === JSON.stringify(right.evidence)
	);
}

function samePlan(left: readonly GoalPlanStep[], right: readonly GoalPlanStep[]): boolean {
	return (
		left.length === right.length &&
		left.every((step, index) => right[index] !== undefined && sameStep(step, right[index]))
	);
}

function samePartition(left: GoalPartition, right: GoalPartition): boolean {
	return (
		left.ownerId === right.ownerId &&
		left.timelineId === right.timelineId &&
		left.generation === right.generation &&
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId
	);
}

function hasExactEvidence(step: GoalPlanStep, evidence: GoalPlanStep["evidence"][number]): boolean {
	return (
		step.status === "completed" &&
		step.evidence.length === 1 &&
		step.evidence[0]?.kind === evidence.kind &&
		step.evidence[0].referenceId === evidence.referenceId
	);
}

function fingerprintHostAdvance(snapshot: GoalHostAdvanceSnapshot): string {
	return createHash("sha256")
		.update(
			canonicalizeJsonValue({
				ownerId: snapshot.partition.ownerId,
				timelineId: snapshot.partition.timelineId,
				generation: snapshot.partition.generation,
				sessionId: snapshot.partition.sessionId ?? null,
				actorId: snapshot.partition.actorId ?? null,
				goalId: snapshot.goalId,
				stepId: snapshot.stepId,
				inputId: snapshot.inputId,
				expectedRevision: snapshot.expectedRevision,
				evidence: snapshot.evidence,
				gameTime: snapshot.gameTime,
				recordedAt: snapshot.recordedAt,
			}),
		)
		.digest("hex");
}

function isExactHostAdvanceReplay(goal: GoalRecord, snapshot: GoalHostAdvanceSnapshot, fingerprint: string): boolean {
	const step = goal.plan.find((candidate) => candidate.stepId === snapshot.stepId);
	return (
		goal.lastHostAdvanceFingerprint === fingerprint &&
		goal.revision === (BigInt(snapshot.expectedRevision) + 1n).toString() &&
		goal.updatedAt === snapshot.recordedAt &&
		goal.gameTime.timelineId === snapshot.gameTime.timelineId &&
		goal.gameTime.generation === snapshot.gameTime.generation &&
		goal.gameTime.tick === snapshot.gameTime.tick &&
		step !== undefined &&
		hasExactEvidence(step, snapshot.evidence)
	);
}

function throwIfAborted(signal: AbortSignal | undefined): void {
	if (signal?.aborted === true) throw signal.reason;
}

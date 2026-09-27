import type { GoalEvidenceValidationRequest, GoalEvidenceVerifier } from "./goal-types.js";
import { SignalWaitNotFoundError, SignalWaitRevisionConflictError } from "./signal-errors.js";
import type {
	GameSignalDraft,
	GameSignalKey,
	GameSignalRecord,
	GameSignalSequenceKey,
	SignalLimits,
	SignalListRequest,
	SignalPage,
	SignalPartition,
	SignalRecordResult,
	SignalStore,
	SignalWaitDraft,
	SignalWaitKey,
	SignalWaitListRequest,
	SignalWaitPage,
	SignalWaitRecord,
} from "./signal-types.js";
import {
	compareSignalTicks,
	normalizeGameSignal,
	normalizeSignalKindFilter,
	normalizeSignalPartition,
	normalizeSignalStatusFilter,
	normalizeSignalWait,
	signalWaitMatches,
	validateGameSignalKey,
	validateGameSignalSequenceKey,
	validateSignalCursor,
	validateSignalDecimal,
	validateSignalLimits,
	validateSignalPageLimit,
	validateSignalTimestamp,
	validateSignalWaitKey,
} from "./signal-validation.js";

export const SIGNAL_WAIT_EVIDENCE_KIND = "signal_wait";

export class SignalService {
	readonly limits: SignalLimits;

	constructor(
		private readonly store: SignalStore,
		limits: SignalLimits,
	) {
		validateSignalLimits(limits);
		this.limits = Object.freeze({ ...limits });
	}

	async record(input: GameSignalDraft): Promise<SignalRecordResult> {
		return await this.store.record(normalizeGameSignal(input, this.limits), this.limits.maxWaitMatchesPerSignal);
	}

	async getSignal(key: GameSignalKey): Promise<GameSignalRecord | undefined> {
		validateGameSignalKey(key);
		return await this.store.getSignal(key);
	}

	async getSignalByKey(key: GameSignalSequenceKey): Promise<GameSignalRecord | undefined> {
		validateGameSignalSequenceKey(key);
		return await this.store.getSignalByKey(key);
	}

	async listSignals(request: SignalListRequest): Promise<SignalPage> {
		const partition = normalizeSignalPartition(request);
		const kinds = normalizeSignalKindFilter(request.kinds, this.limits.maxSignalFilterKinds);
		validateSignalCursor(request.afterSignalKey);
		validateSignalPageLimit(request.limit, this.limits.maxSignalPageSize, "invalid_signal_page_limit");
		return await this.store.listSignals({
			...partition,
			...(kinds === undefined ? {} : { kinds }),
			...(request.afterSignalKey === undefined ? {} : { afterSignalKey: request.afterSignalKey }),
			limit: request.limit,
		});
	}

	async registerWait(input: SignalWaitDraft): Promise<SignalWaitRecord> {
		return await this.store.registerWait(normalizeSignalWait(input, this.limits), this.limits.maxWaitMatchesPerSignal);
	}

	async getWait(key: SignalWaitKey): Promise<SignalWaitRecord | undefined> {
		validateSignalWaitKey(key);
		return await this.store.getWait(key);
	}

	async listWaits(request: SignalWaitListRequest): Promise<SignalWaitPage> {
		const partition = normalizeSignalPartition(request);
		const statuses = normalizeSignalStatusFilter(request.statuses);
		validateSignalCursor(request.afterWaitKey);
		validateSignalPageLimit(request.limit, this.limits.maxWaitPageSize, "invalid_signal_wait_page_limit");
		return await this.store.listWaits({
			...partition,
			...(statuses === undefined ? {} : { statuses }),
			...(request.afterWaitKey === undefined ? {} : { afterWaitKey: request.afterWaitKey }),
			limit: request.limit,
		});
	}

	async cancelWait(key: SignalWaitKey, expectedRevision: string, recordedAt: string): Promise<SignalWaitRecord> {
		validateSignalWaitKey(key);
		validateSignalDecimal(expectedRevision, "wait_revision");
		validateSignalTimestamp(recordedAt);
		const current = await this.store.getWait(key);
		if (current === undefined) throw new SignalWaitNotFoundError();
		if (current.revision !== expectedRevision) throw new SignalWaitRevisionConflictError();
		return await this.store.cancelWait(key, expectedRevision, recordedAt);
	}

	async expireWaits(
		partition: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		throughTickInclusive: string,
		recordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const normalized = normalizeSignalPartition(partition);
		validateSignalDecimal(throughTickInclusive, "expiration_tick");
		validateSignalTimestamp(recordedAt);
		validateSignalPageLimit(limit, this.limits.maxExpireBatchSize, "invalid_signal_expire_limit");
		return await this.store.expireWaits(normalized, throughTickInclusive, recordedAt, limit);
	}

	async purgeTerminalWaits(
		partition: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const normalized = normalizeSignalPartition(partition);
		validateSignalTimestamp(beforeRecordedAt);
		validateSignalPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_signal_wait_purge_limit");
		return await this.store.purgeTerminalWaits(normalized, beforeRecordedAt, limit);
	}

	async purgeSignals(
		partition: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const normalized = normalizeSignalPartition(partition);
		validateSignalTimestamp(beforeRecordedAt);
		validateSignalPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_signal_purge_limit");
		return await this.store.purgeSignals(normalized, beforeRecordedAt, limit);
	}
}

export class SignalWaitGoalEvidenceVerifier implements GoalEvidenceVerifier {
	constructor(private readonly signals: SignalService) {}

	async validate(request: GoalEvidenceValidationRequest): Promise<boolean> {
		request.signal?.throwIfAborted();
		if (request.evidence.kind !== SIGNAL_WAIT_EVIDENCE_KIND) return false;
		const wait = await this.signals.getWait({
			ownerId: request.goal.partition.ownerId,
			timelineId: request.goal.partition.timelineId,
			generation: request.goal.partition.generation,
			waitId: request.evidence.referenceId,
		});
		request.signal?.throwIfAborted();
		if (
			wait === undefined ||
			wait.status !== "matched" ||
			wait.target.kind !== "goal_step" ||
			wait.target.goalId !== request.goal.goalId ||
			wait.target.stepId !== request.step.stepId ||
			wait.partition.sessionId !== request.goal.partition.sessionId ||
			wait.partition.actorId !== request.goal.partition.actorId ||
			wait.matchedSignalKey === undefined
		) {
			return false;
		}
		const signal = await this.signals.getSignalByKey({
			ownerId: wait.partition.ownerId,
			timelineId: wait.partition.timelineId,
			generation: wait.partition.generation,
			signalKey: wait.matchedSignalKey,
		});
		request.signal?.throwIfAborted();
		return (
			signal !== undefined &&
			signalWaitMatches(wait, signal) &&
			signal.partition.ownerId === request.goal.partition.ownerId &&
			signal.partition.timelineId === request.goal.partition.timelineId &&
			signal.partition.generation === request.goal.partition.generation &&
			compareSignalTicks(signal.gameTime.tick, request.gameTime.tick) <= 0
		);
	}
}

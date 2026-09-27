import {
	canonicalizeJsonValue,
	type WorkAuthorityLane,
	WorkAuthorityRetiredError,
	type WorkAuthorityRetirementRequest,
} from "@opengameagent/protocol";
import {
	ScheduleNotFoundError,
	ScheduleOccurrenceLeaseConflictError,
	ScheduleOccurrenceNotFoundError,
	ScheduleRevisionConflictError,
} from "./schedule-errors.js";
import type {
	GameScheduleDraft,
	GameScheduleKey,
	GameScheduleRecord,
	GameScheduleSequenceKey,
	ScheduleAdvanceDraft,
	ScheduleAdvanceResult,
	ScheduleAuthorityRetirementRecord,
	ScheduleDispatchResult,
	ScheduledOccurrenceDelivery,
	ScheduledOccurrenceListRequest,
	ScheduledOccurrencePage,
	ScheduledOccurrenceRecord,
	ScheduledOccurrenceSequenceKey,
	ScheduleLimits,
	ScheduleListRequest,
	ScheduleOccurrenceClaimRequest,
	SchedulePage,
	SchedulePartition,
	ScheduleStore,
} from "./schedule-types.js";
import {
	normalizeGameSchedule,
	normalizeScheduleAdvance,
	normalizeScheduleAuthorityLane,
	normalizeScheduleAuthorityRetirement,
	normalizeScheduleClaim,
	normalizeScheduledOccurrenceListRequest,
	normalizeScheduleListRequest,
	normalizeSchedulePartition,
	validateGameScheduleKey,
	validateGameScheduleSequenceKey,
	validateScheduleDecimal,
	validateScheduledOccurrenceSequenceKey,
	validateScheduleIdentifier,
	validateScheduleLimits,
	validateSchedulePageLimit,
	validateScheduleTimestamp,
} from "./schedule-validation.js";
import type { SignalService } from "./signal-service.js";
import { normalizeGameSignal } from "./signal-validation.js";

export class ScheduleService {
	readonly limits: ScheduleLimits;

	constructor(
		private readonly store: ScheduleStore,
		limits: ScheduleLimits,
	) {
		validateScheduleLimits(limits);
		this.limits = Object.freeze({ ...limits });
	}

	async create(input: GameScheduleDraft): Promise<GameScheduleRecord> {
		return await this.store.create(
			normalizeGameSchedule(input, this.limits),
			this.limits.maxActiveSchedulesPerTimelineGeneration,
		);
	}

	async beginAuthorityRetirement(request: WorkAuthorityRetirementRequest): Promise<ScheduleAuthorityRetirementRecord> {
		return await this.store.beginAuthorityRetirement(normalizeScheduleAuthorityRetirement(request, this.limits));
	}

	async finalizeAuthorityRetirement(
		request: WorkAuthorityRetirementRequest,
	): Promise<ScheduleAuthorityRetirementRecord> {
		return await this.store.finalizeAuthorityRetirement(normalizeScheduleAuthorityRetirement(request, this.limits));
	}

	async readAuthorityRetirement(partition: WorkAuthorityLane): Promise<ScheduleAuthorityRetirementRecord | undefined> {
		return await this.store.readAuthorityRetirement(normalizeScheduleAuthorityLane(partition, this.limits));
	}

	async getSchedule(key: GameScheduleKey): Promise<GameScheduleRecord | undefined> {
		key = validateGameScheduleKey(key, this.limits);
		return await this.store.getSchedule(key);
	}

	async getScheduleByKey(key: GameScheduleSequenceKey): Promise<GameScheduleRecord | undefined> {
		key = validateGameScheduleSequenceKey(key, this.limits);
		return await this.store.getScheduleByKey(key);
	}

	async listSchedules(request: ScheduleListRequest): Promise<SchedulePage> {
		return await this.store.listSchedules(normalizeScheduleListRequest(request, this.limits));
	}

	async cancelSchedule(
		key: GameScheduleKey,
		expectedRevision: string,
		recordedAt: string,
	): Promise<GameScheduleRecord> {
		key = validateGameScheduleKey(key, this.limits);
		validateScheduleDecimal(expectedRevision, "revision", this.limits.maxCounterDigits);
		validateScheduleTimestamp(recordedAt);
		const current = await this.store.getSchedule(key);
		if (current === undefined) throw new ScheduleNotFoundError();
		if (current.revision !== expectedRevision) throw new ScheduleRevisionConflictError();
		return await this.store.cancelSchedule(key, expectedRevision, recordedAt);
	}

	async advance(input: ScheduleAdvanceDraft, limit: number): Promise<ScheduleAdvanceResult> {
		validateSchedulePageLimit(limit, this.limits.maxOccurrencesPerAdvance, "invalid_schedule_advance_limit");
		return await this.store.advance(
			normalizeScheduleAdvance(input, this.limits),
			limit,
			this.limits.maxPendingOccurrencesPerTimelineGeneration,
		);
	}

	async getOccurrence(key: ScheduledOccurrenceSequenceKey): Promise<ScheduledOccurrenceRecord | undefined> {
		key = validateScheduledOccurrenceSequenceKey(key, this.limits);
		return await this.store.getOccurrence(key);
	}

	async listOccurrences(request: ScheduledOccurrenceListRequest): Promise<ScheduledOccurrencePage> {
		return await this.store.listOccurrences(normalizeScheduledOccurrenceListRequest(request, this.limits));
	}

	async claimOccurrences(request: ScheduleOccurrenceClaimRequest): Promise<readonly ScheduledOccurrenceDelivery[]> {
		return await this.store.claimOccurrences(normalizeScheduleClaim(request, this.limits));
	}

	async completeOccurrence(
		key: ScheduledOccurrenceSequenceKey,
		leaseToken: string,
		recordedAt: string,
	): Promise<ScheduledOccurrenceRecord> {
		key = validateScheduledOccurrenceSequenceKey(key, this.limits);
		validateScheduleIdentifier(leaseToken, this.limits.maxIdentifierBytes, "lease_token");
		validateScheduleTimestamp(recordedAt);
		return await this.store.completeOccurrence(key, leaseToken, recordedAt);
	}

	async abandonOccurrence(
		key: ScheduledOccurrenceSequenceKey,
		leaseToken: string,
		recordedAt: string,
	): Promise<ScheduledOccurrenceRecord> {
		key = validateScheduledOccurrenceSequenceKey(key, this.limits);
		validateScheduleIdentifier(leaseToken, this.limits.maxIdentifierBytes, "lease_token");
		validateScheduleTimestamp(recordedAt);
		return await this.store.abandonOccurrence(key, leaseToken, recordedAt);
	}

	async purgeTerminalOccurrences(
		partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const { ownerId, timelineId, generation } = normalizeSchedulePartition(partition, this.limits);
		const normalized = { ownerId, timelineId, generation };
		validateScheduleTimestamp(beforeRecordedAt);
		validateSchedulePageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_occurrence_purge_limit");
		return await this.store.purgeTerminalOccurrences(normalized, beforeRecordedAt, limit);
	}

	async purgeTerminalSchedules(
		partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const { ownerId, timelineId, generation } = normalizeSchedulePartition(partition, this.limits);
		const normalized = { ownerId, timelineId, generation };
		validateScheduleTimestamp(beforeRecordedAt);
		validateSchedulePageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_schedule_purge_limit");
		return await this.store.purgeTerminalSchedules(normalized, beforeRecordedAt, limit);
	}

	async purgeAdvances(
		partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const { ownerId, timelineId, generation } = normalizeSchedulePartition(partition, this.limits);
		const normalized = { ownerId, timelineId, generation };
		validateScheduleTimestamp(beforeRecordedAt);
		validateSchedulePageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_schedule_advance_purge_limit");
		return await this.store.purgeAdvances(normalized, beforeRecordedAt, limit);
	}
}

export class ScheduledSignalDispatcher {
	constructor(
		private readonly schedules: ScheduleService,
		private readonly signals: SignalService,
	) {}

	async dispatch(
		delivery: ScheduledOccurrenceDelivery,
		completedAt: string,
		signal?: AbortSignal,
	): Promise<ScheduleDispatchResult> {
		signal?.throwIfAborted();
		validateScheduleTimestamp(completedAt);
		const occurrence = delivery.occurrence;
		const leaseToken = delivery.leaseToken;
		const revision = occurrence.revision;
		const attempt = occurrence.attempt;
		validateScheduleIdentifier(leaseToken, this.schedules.limits.maxIdentifierBytes, "lease_token");
		validateScheduleDecimal(revision, "revision", this.schedules.limits.maxCounterDigits);
		validateScheduleDecimal(attempt, "attempt", this.schedules.limits.maxCounterDigits);
		const captured = normalizeGameSignal(
			{
				signalId: occurrence.signalId,
				partition: occurrence.partition,
				kind: occurrence.kind,
				payload: occurrence.payload,
				gameTime: occurrence.dueTime,
				subjectIds: occurrence.subjectIds,
				causeIds: occurrence.causeIds,
				recordedAt: occurrence.createdAt,
			},
			this.signals.limits,
		);
		const key = validateScheduledOccurrenceSequenceKey(
			{
				ownerId: captured.partition.ownerId,
				timelineId: captured.partition.timelineId,
				generation: captured.partition.generation,
				occurrenceKey: occurrence.occurrenceKey,
			},
			this.schedules.limits,
		);
		const persisted = await this.schedules.getOccurrence(key);
		if (persisted === undefined) throw new ScheduleOccurrenceNotFoundError();
		const { sessionId, actorId } = persisted.partition;
		if (sessionId !== undefined && actorId !== undefined) {
			const lane: WorkAuthorityLane = {
				ownerId: key.ownerId,
				timelineId: key.timelineId,
				generation: key.generation,
				sessionId,
				actorId,
			};
			if (await this.schedules.readAuthorityRetirement(lane)) throw new WorkAuthorityRetiredError(lane);
		}
		const effect = normalizeGameSignal(
			{
				signalId: persisted.signalId,
				partition: persisted.partition,
				kind: persisted.kind,
				payload: persisted.payload,
				gameTime: persisted.dueTime,
				subjectIds: persisted.subjectIds,
				causeIds: persisted.causeIds,
				recordedAt: persisted.createdAt,
			},
			this.signals.limits,
		);
		if (
			canonicalizeJsonValue(captured) !== canonicalizeJsonValue(effect) ||
			(persisted.status === "pending" && (persisted.revision !== revision || persisted.attempt !== attempt)) ||
			persisted.status === "cancelled"
		)
			throw new ScheduleOccurrenceLeaseConflictError();
		signal?.throwIfAborted();
		if (persisted.status === "completed") await this.schedules.completeOccurrence(key, leaseToken, completedAt);
		const recorded = await this.signals.record(effect);
		signal?.throwIfAborted();
		const completed = await this.schedules.completeOccurrence(key, leaseToken, completedAt);
		return { occurrence: completed, signal: recorded.signal, matchedWaitIds: recorded.matchedWaitIds };
	}
}

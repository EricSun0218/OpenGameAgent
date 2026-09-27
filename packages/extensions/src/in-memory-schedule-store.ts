import { randomUUID } from "node:crypto";
import {
	canonicalizeJsonValue,
	captureWorkAuthorityRetirementRequest,
	type WorkAuthorityLane,
	WorkAuthorityRetiredError,
	WorkAuthorityRetirementConflictError,
	type WorkAuthorityRetirementRequest,
} from "@opengameagent/protocol";
import TinyQueue from "tinyqueue";
import {
	ScheduleAdvanceConflictError,
	ScheduleAdvanceOverflowError,
	ScheduleCapacityError,
	ScheduleClockConflictError,
	ScheduleConflictError,
	ScheduleMissedOccurrenceError,
	ScheduleNotFoundError,
	ScheduleOccurrenceLeaseConflictError,
	ScheduleOccurrenceNotFoundError,
	ScheduleRevisionConflictError,
	ScheduleTerminalError,
} from "./schedule-errors.js";
import type {
	GameScheduleDraft,
	GameScheduleKey,
	GameScheduleRecord,
	GameScheduleSequenceKey,
	ScheduleAdvanceDraft,
	ScheduleAdvanceRecord,
	ScheduleAdvanceResult,
	ScheduleAuthorityRetirementRecord,
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
	addScheduleDecimals,
	compareScheduleDecimals,
	incrementScheduleDecimal,
	laterScheduleTimestamp,
	normalizeGameSchedule,
	normalizeScheduleAdvance,
	normalizeScheduleAuthorityLane,
	normalizeScheduleAuthorityRetirement,
	normalizeScheduleClaim,
	normalizeScheduledOccurrenceListRequest,
	normalizeScheduleListRequest,
	normalizeSchedulePartition,
	sameScheduleAuthorityLane,
	sameScheduleGeneration,
	scheduleAdvanceFingerprint,
	scheduleFingerprint,
	validateGameScheduleKey,
	validateGameScheduleSequenceKey,
	validateScheduleDecimal,
	validateScheduledOccurrenceSequenceKey,
	validateScheduleIdentifier,
	validateScheduleLimits,
	validateSchedulePageLimit,
	validateScheduleTimestamp,
} from "./schedule-validation.js";

interface OccurrenceLease {
	readonly token: string;
	readonly ownerId: string;
	readonly expiresAt: string;
}

interface AdvanceCursor {
	readonly schedule: GameScheduleRecord;
	dueTick: string;
	ordinal: string;
	completed: boolean;
}

export class InMemoryScheduleStore implements ScheduleStore {
	readonly limits: ScheduleLimits;
	private readonly schedules = new Map<string, GameScheduleRecord>();
	private readonly scheduleFingerprints = new Map<string, string>();
	private readonly occurrences = new Map<bigint, ScheduledOccurrenceRecord>();
	private readonly advances = new Map<string, ScheduleAdvanceRecord>();
	private readonly advanceFingerprints = new Map<string, string>();
	private readonly clocks = new Map<string, string>();
	private readonly leases = new Map<bigint, OccurrenceLease>();
	private readonly completedLeaseTokens = new Map<bigint, string>();
	private readonly retirements = new Map<string, ScheduleAuthorityRetirementRecord>();
	private readonly retirementIds = new Map<string, string>();
	private nextScheduleKey = 1n;
	private nextOccurrenceKey = 1n;
	private nextAdvanceKey = 1n;

	constructor(
		limits: ScheduleLimits,
		private readonly newLeaseToken: () => string = randomUUID,
		private readonly newSignalId: () => string = randomUUID,
	) {
		validateScheduleLimits(limits);
		this.limits = Object.freeze({ ...limits });
	}

	async create(input: GameScheduleDraft, maxActive: number): Promise<GameScheduleRecord> {
		validateSchedulePageLimit(
			maxActive,
			this.limits.maxActiveSchedulesPerTimelineGeneration,
			"invalid_active_schedule_limit",
		);
		const schedule = normalizeGameSchedule(input, this.limits);
		this.assertAuthorityOpen(schedule.partition);
		const key = scheduleMapKey(schedule);
		const existing = this.schedules.get(key);
		if (existing !== undefined) {
			if (this.scheduleFingerprints.get(key) !== scheduleFingerprint(schedule)) throw new ScheduleConflictError();
			return clone(existing);
		}
		const currentTick = this.clocks.get(scheduleGenerationKey(schedule.partition));
		if (currentTick !== undefined && compareScheduleDecimals(schedule.dueTime.tick, currentTick) <= 0) {
			throw new ScheduleMissedOccurrenceError();
		}
		const active = [...this.schedules.values()].filter(
			(candidate) => candidate.status === "active" && sameScheduleGeneration(candidate.partition, schedule.partition),
		).length;
		if (active >= maxActive) throw new ScheduleCapacityError("active_schedules");
		const record: GameScheduleRecord = {
			...clone(schedule),
			scheduleKey: this.nextScheduleKey,
			status: "active",
			revision: "1",
			occurrences: "0",
			nextDueTick: schedule.dueTime.tick,
			createdAt: schedule.recordedAt,
			updatedAt: schedule.recordedAt,
		};
		this.nextScheduleKey += 1n;
		this.schedules.set(key, record);
		this.scheduleFingerprints.set(key, scheduleFingerprint(schedule));
		return clone(record);
	}

	async beginAuthorityRetirement(input: WorkAuthorityRetirementRequest): Promise<ScheduleAuthorityRetirementRecord> {
		return this.retireAuthority(input, "fenced");
	}

	async finalizeAuthorityRetirement(input: WorkAuthorityRetirementRequest): Promise<ScheduleAuthorityRetirementRecord> {
		return this.retireAuthority(input, "finalized");
	}

	async readAuthorityRetirement(input: WorkAuthorityLane): Promise<ScheduleAuthorityRetirementRecord | undefined> {
		const partition = normalizeScheduleAuthorityLane(input, this.limits);
		const record = this.retirements.get(canonicalizeJsonValue(partition));
		return record === undefined ? undefined : clone(record);
	}

	private retireAuthority(
		input: WorkAuthorityRetirementRequest,
		phase: "fenced" | "finalized",
	): ScheduleAuthorityRetirementRecord {
		const request = normalizeScheduleAuthorityRetirement(input, this.limits);
		const key = canonicalizeJsonValue(request.partition);
		const existing = this.retirements.get(key);
		const operationKey = this.retirementIds.get(request.retirementId);
		if (
			(operationKey !== undefined && operationKey !== key) ||
			(existing !== undefined &&
				canonicalizeJsonValue(captureWorkAuthorityRetirementRequest(existing)) !== canonicalizeJsonValue(request))
		) {
			throw new WorkAuthorityRetirementConflictError();
		}
		if (existing !== undefined && (phase === "fenced" || existing.phase === "finalized")) return clone(existing);
		if (phase === "finalized" && existing === undefined) throw new WorkAuthorityRetirementConflictError();
		const schedules = [...this.schedules.values()].filter(
			(record) => record.status === "active" && sameScheduleAuthorityLane(record.partition, request.partition),
		);
		const occurrences = [...this.occurrences.values()].filter(
			(record) =>
				record.status === "pending" &&
				sameScheduleAuthorityLane(record.partition, request.partition) &&
				(phase === "finalized" || !this.leases.has(record.occurrenceKey)),
		);
		if (schedules.length > this.limits.maxActiveSchedulesPerTimelineGeneration)
			throw new ScheduleCapacityError("active_schedules");
		if (occurrences.length > this.limits.maxPendingOccurrencesPerTimelineGeneration)
			throw new ScheduleCapacityError("pending_occurrences");
		const scheduleUpdates = schedules.map((record) => {
			const { nextDueTick: _nextDueTick, ...rest } = record;
			return {
				...rest,
				status: "cancelled" as const,
				revision: incrementScheduleDecimal(record.revision, this.limits.maxCounterDigits, "revision"),
				updatedAt: laterScheduleTimestamp(record.updatedAt, request.recordedAt),
			};
		});
		const occurrenceUpdates = occurrences.map((record) => {
			const cancelledAt = laterScheduleTimestamp(record.updatedAt, request.recordedAt);
			return {
				...record,
				status: "cancelled" as const,
				revision: incrementScheduleDecimal(record.revision, this.limits.maxCounterDigits, "revision"),
				updatedAt: cancelledAt,
				cancelledAt,
			};
		});
		const retirement: ScheduleAuthorityRetirementRecord = {
			...request,
			phase,
			cancelledScheduleCount: (existing?.cancelledScheduleCount ?? 0) + scheduleUpdates.length,
			cancelledOccurrenceCount: (existing?.cancelledOccurrenceCount ?? 0) + occurrenceUpdates.length,
		};
		for (const record of scheduleUpdates) this.schedules.set(scheduleMapKey(record), record);
		for (const record of occurrenceUpdates) {
			this.occurrences.set(record.occurrenceKey, record);
			this.leases.delete(record.occurrenceKey);
		}
		this.retirements.set(key, retirement);
		this.retirementIds.set(request.retirementId, key);
		return clone(retirement);
	}

	private assertAuthorityOpen(partition: SchedulePartition): void {
		if (partition.sessionId === undefined || partition.actorId === undefined) return;
		const lane: WorkAuthorityLane = {
			ownerId: partition.ownerId,
			timelineId: partition.timelineId,
			generation: partition.generation,
			sessionId: partition.sessionId,
			actorId: partition.actorId,
		};
		if (this.retirements.has(canonicalizeJsonValue(lane))) throw new WorkAuthorityRetiredError(lane);
	}

	async getSchedule(key: GameScheduleKey): Promise<GameScheduleRecord | undefined> {
		key = validateGameScheduleKey(key, this.limits);
		const record = this.schedules.get(scheduleMapKey(key));
		return record === undefined ? undefined : clone(record);
	}

	async getScheduleByKey(key: GameScheduleSequenceKey): Promise<GameScheduleRecord | undefined> {
		key = validateGameScheduleSequenceKey(key, this.limits);
		const record = [...this.schedules.values()].find(
			(candidate) => candidate.scheduleKey === key.scheduleKey && sameScheduleGeneration(candidate.partition, key),
		);
		return record === undefined ? undefined : clone(record);
	}

	async listSchedules(request: ScheduleListRequest): Promise<SchedulePage> {
		request = normalizeScheduleListRequest(request, this.limits);
		const statuses = request.statuses;
		const matches = [...this.schedules.values()]
			.filter(
				(record) =>
					sameScheduleGeneration(record.partition, request) &&
					(request.sessionId === undefined || record.partition.sessionId === request.sessionId) &&
					(request.actorId === undefined || record.partition.actorId === request.actorId) &&
					(statuses === undefined || statuses.includes(record.status)) &&
					(request.afterScheduleKey === undefined || record.scheduleKey > request.afterScheduleKey),
			)
			.sort((left, right) => compareKeys(left.scheduleKey, right.scheduleKey));
		const selected = matches.slice(0, request.limit);
		const next = matches.length > selected.length ? selected.at(-1)?.scheduleKey : undefined;
		return {
			records: selected.map(clone),
			...(next === undefined ? {} : { nextAfterScheduleKey: next }),
		};
	}

	async cancelSchedule(
		key: GameScheduleKey,
		expectedRevision: string,
		recordedAt: string,
	): Promise<GameScheduleRecord> {
		key = validateGameScheduleKey(key, this.limits);
		validateScheduleDecimal(expectedRevision, "revision", this.limits.maxCounterDigits);
		validateScheduleTimestamp(recordedAt);
		const storageKey = scheduleMapKey(key);
		const current = this.schedules.get(storageKey);
		if (current === undefined) throw new ScheduleNotFoundError();
		this.assertAuthorityOpen(current.partition);
		if (current.revision !== expectedRevision) throw new ScheduleRevisionConflictError();
		if (current.status !== "active") throw new ScheduleTerminalError();
		const { nextDueTick: _nextDueTick, ...terminal } = current;
		const updated: GameScheduleRecord = {
			...terminal,
			status: "cancelled",
			revision: incrementScheduleDecimal(current.revision, this.limits.maxCounterDigits, "revision"),
			updatedAt: laterScheduleTimestamp(current.updatedAt, recordedAt),
		};
		this.schedules.set(storageKey, updated);
		return clone(updated);
	}

	async advance(input: ScheduleAdvanceDraft, maxGenerated: number, maxPending: number): Promise<ScheduleAdvanceResult> {
		validateSchedulePageLimit(maxGenerated, this.limits.maxOccurrencesPerAdvance, "invalid_schedule_advance_limit");
		validateSchedulePageLimit(
			maxPending,
			this.limits.maxPendingOccurrencesPerTimelineGeneration,
			"invalid_pending_occurrence_limit",
		);
		const advance = normalizeScheduleAdvance(input, this.limits);
		const identity = advanceMapKey(advance);
		const existing = this.advances.get(identity);
		if (existing !== undefined) {
			if (this.advanceFingerprints.get(identity) !== scheduleAdvanceFingerprint(advance)) {
				throw new ScheduleAdvanceConflictError();
			}
			const occurrences = this.occurrencesForAdvance(advance);
			if (occurrences.length > maxGenerated) throw new ScheduleAdvanceOverflowError();
			return {
				advance: clone(existing),
				occurrences: occurrences.map(clone),
			};
		}

		const generationKey = scheduleGenerationKey(advance.partition);
		const clock = this.clocks.get(generationKey);
		if (clock !== undefined && clock !== advance.fromExclusive.tick) throw new ScheduleClockConflictError();

		const active = [...this.schedules.values()].filter(
			(schedule) => schedule.status === "active" && sameScheduleGeneration(schedule.partition, advance.partition),
		);
		for (const schedule of active) {
			if (
				schedule.nextDueTick !== undefined &&
				compareScheduleDecimals(schedule.nextDueTick, advance.fromExclusive.tick) <= 0
			) {
				throw new ScheduleMissedOccurrenceError();
			}
		}

		const pending = [...this.occurrences.values()].filter(
			(occurrence) =>
				occurrence.status === "pending" && sameScheduleGeneration(occurrence.partition, advance.partition),
		).length;
		const cursors = new TinyQueue<AdvanceCursor>(
			active.flatMap((schedule) =>
				schedule.nextDueTick !== undefined &&
				compareScheduleDecimals(schedule.nextDueTick, advance.toInclusive.tick) <= 0
					? [{ schedule, dueTick: schedule.nextDueTick, ordinal: schedule.occurrences, completed: false }]
					: [],
			),
			compareAdvanceCursors,
		);
		const generated: ScheduledOccurrenceRecord[] = [];
		const finalBySchedule = new Map<bigint, AdvanceCursor>();
		const firstOccurrenceKey = this.nextOccurrenceKey;
		let occurrenceKey = firstOccurrenceKey;

		while (cursors.length > 0) {
			const cursor = cursors.pop();
			if (cursor === undefined) break;
			if (generated.length >= maxGenerated) throw new ScheduleAdvanceOverflowError();
			if (pending + generated.length >= maxPending) throw new ScheduleCapacityError("pending_occurrences");
			cursor.ordinal = incrementScheduleDecimal(cursor.ordinal, this.limits.maxCounterDigits, "occurrences");
			const signalId = this.newSignalId();
			validateScheduleIdentifier(signalId, this.limits.maxIdentifierBytes, "signal_id");
			const record: ScheduledOccurrenceRecord = {
				occurrenceKey,
				signalId,
				scheduleKey: cursor.schedule.scheduleKey,
				scheduleId: cursor.schedule.scheduleId,
				ordinal: cursor.ordinal,
				advanceId: advance.advanceId,
				partition: clone(cursor.schedule.partition),
				kind: cursor.schedule.kind,
				payload: clone(cursor.schedule.payload),
				dueTime: { ...cursor.schedule.dueTime, tick: cursor.dueTick },
				subjectIds: [...cursor.schedule.subjectIds],
				causeIds: [...cursor.schedule.causeIds],
				status: "pending",
				revision: "1",
				attempt: "0",
				createdAt: advance.recordedAt,
				updatedAt: advance.recordedAt,
			};
			generated.push(record);
			occurrenceKey += 1n;

			cursor.completed =
				cursor.schedule.maximumOccurrences !== undefined &&
				compareScheduleDecimals(cursor.ordinal, cursor.schedule.maximumOccurrences) >= 0;
			if (!cursor.completed && cursor.schedule.intervalTicks !== undefined) {
				cursor.dueTick = addScheduleDecimals(cursor.dueTick, cursor.schedule.intervalTicks, this.limits.maxTickDigits);
				if (compareScheduleDecimals(cursor.dueTick, advance.toInclusive.tick) <= 0) cursors.push(cursor);
			} else {
				cursor.completed = true;
			}
			finalBySchedule.set(cursor.schedule.scheduleKey, cursor);
		}

		const scheduleUpdates = [...finalBySchedule.values()].map((cursor) => {
			this.assertAuthorityOpen(cursor.schedule.partition);
			if (this.schedules.get(scheduleMapKey(cursor.schedule)) !== cursor.schedule)
				throw new ScheduleRevisionConflictError();
			const { nextDueTick: _nextDueTick, ...base } = cursor.schedule;
			const updated: GameScheduleRecord = {
				...base,
				status: cursor.completed ? "completed" : "active",
				revision: incrementScheduleDecimal(cursor.schedule.revision, this.limits.maxCounterDigits, "revision"),
				occurrences: cursor.ordinal,
				...(cursor.completed ? {} : { nextDueTick: cursor.dueTick }),
				updatedAt: laterScheduleTimestamp(cursor.schedule.updatedAt, advance.recordedAt),
			};
			return updated;
		});
		if (this.clocks.get(generationKey) !== clock) throw new ScheduleClockConflictError();
		const currentActive = [...this.schedules.values()].filter(
			(schedule) => schedule.status === "active" && sameScheduleGeneration(schedule.partition, advance.partition),
		);
		if (
			this.nextOccurrenceKey !== firstOccurrenceKey ||
			currentActive.length !== active.length ||
			currentActive.some((record, index) => record !== active[index])
		)
			throw new ScheduleRevisionConflictError();
		for (const updated of scheduleUpdates) this.schedules.set(scheduleMapKey(updated), updated);
		for (const occurrence of generated) this.occurrences.set(occurrence.occurrenceKey, occurrence);
		this.nextOccurrenceKey = occurrenceKey;
		const advanceRecord: ScheduleAdvanceRecord = { ...clone(advance), advanceKey: this.nextAdvanceKey };
		this.nextAdvanceKey += 1n;
		this.advances.set(identity, advanceRecord);
		this.advanceFingerprints.set(identity, scheduleAdvanceFingerprint(advance));
		this.clocks.set(generationKey, advance.toInclusive.tick);
		return { advance: clone(advanceRecord), occurrences: generated.map(clone) };
	}

	async getOccurrence(key: ScheduledOccurrenceSequenceKey): Promise<ScheduledOccurrenceRecord | undefined> {
		key = validateScheduledOccurrenceSequenceKey(key, this.limits);
		const record = this.occurrences.get(key.occurrenceKey);
		return record !== undefined && sameScheduleGeneration(record.partition, key) ? clone(record) : undefined;
	}

	async listOccurrences(request: ScheduledOccurrenceListRequest): Promise<ScheduledOccurrencePage> {
		request = normalizeScheduledOccurrenceListRequest(request, this.limits);
		const statuses = request.statuses;
		const matches = [...this.occurrences.values()]
			.filter(
				(record) =>
					sameScheduleGeneration(record.partition, request) &&
					(request.sessionId === undefined || record.partition.sessionId === request.sessionId) &&
					(request.actorId === undefined || record.partition.actorId === request.actorId) &&
					(statuses === undefined || statuses.includes(record.status)) &&
					(request.afterOccurrenceKey === undefined || record.occurrenceKey > request.afterOccurrenceKey),
			)
			.sort((left, right) => compareKeys(left.occurrenceKey, right.occurrenceKey));
		const selected = matches.slice(0, request.limit);
		const next = matches.length > selected.length ? selected.at(-1)?.occurrenceKey : undefined;
		return {
			records: selected.map(clone),
			...(next === undefined ? {} : { nextAfterOccurrenceKey: next }),
		};
	}

	async claimOccurrences(request: ScheduleOccurrenceClaimRequest): Promise<readonly ScheduledOccurrenceDelivery[]> {
		const claim = normalizeScheduleClaim(request, this.limits);
		this.assertAuthorityOpen(claim);
		const selected = [...this.occurrences.values()]
			.filter((record) => {
				const lease = this.leases.get(record.occurrenceKey);
				return (
					record.status === "pending" &&
					!this.isRetired(record.partition) &&
					sameScheduleGeneration(record.partition, claim) &&
					(claim.sessionId === undefined || record.partition.sessionId === claim.sessionId) &&
					(claim.actorId === undefined || record.partition.actorId === claim.actorId) &&
					(lease === undefined || lease.expiresAt <= claim.operationalNow)
				);
			})
			.sort(compareOccurrenceOrder)
			.slice(0, claim.limit);
		const deliveries = selected.map((record) => {
			const token = this.newLeaseToken();
			validateScheduleIdentifier(token, this.limits.maxIdentifierBytes, "lease_token");
			const updated: ScheduledOccurrenceRecord = {
				...record,
				revision: incrementScheduleDecimal(record.revision, this.limits.maxCounterDigits, "revision"),
				attempt: incrementScheduleDecimal(record.attempt, this.limits.maxCounterDigits, "attempt"),
				updatedAt: laterScheduleTimestamp(record.updatedAt, claim.operationalNow),
			};
			return {
				occurrence: updated,
				leaseToken: token,
				leaseOwnerId: claim.leaseOwnerId,
				leaseExpiresAt: claim.leaseExpiresAt,
			};
		});
		for (const record of selected) {
			this.assertAuthorityOpen(record.partition);
			if (this.occurrences.get(record.occurrenceKey) !== record) throw new ScheduleOccurrenceLeaseConflictError();
		}
		for (const delivery of deliveries) {
			this.occurrences.set(delivery.occurrence.occurrenceKey, delivery.occurrence);
			this.leases.set(delivery.occurrence.occurrenceKey, {
				token: delivery.leaseToken,
				ownerId: delivery.leaseOwnerId,
				expiresAt: delivery.leaseExpiresAt,
			});
		}
		return deliveries.map(clone);
	}

	private isRetired(partition: SchedulePartition): boolean {
		if (partition.sessionId === undefined || partition.actorId === undefined) return false;
		return this.retirements.has(
			canonicalizeJsonValue({
				ownerId: partition.ownerId,
				timelineId: partition.timelineId,
				generation: partition.generation,
				sessionId: partition.sessionId,
				actorId: partition.actorId,
			}),
		);
	}

	async completeOccurrence(
		key: ScheduledOccurrenceSequenceKey,
		leaseToken: string,
		recordedAt: string,
	): Promise<ScheduledOccurrenceRecord> {
		key = validateScheduledOccurrenceSequenceKey(key, this.limits);
		validateScheduleTimestamp(recordedAt);
		const current = this.occurrences.get(key.occurrenceKey);
		if (current === undefined || !sameScheduleGeneration(current.partition, key)) {
			throw new ScheduleOccurrenceNotFoundError();
		}
		if (current.status === "completed") {
			if (this.completedLeaseTokens.get(current.occurrenceKey) !== leaseToken) {
				throw new ScheduleOccurrenceLeaseConflictError();
			}
			return clone(current);
		}
		this.assertAuthorityOpen(current.partition);
		const lease = this.leases.get(current.occurrenceKey);
		if (lease?.token !== leaseToken || lease.expiresAt <= recordedAt) {
			throw new ScheduleOccurrenceLeaseConflictError();
		}
		const completedAt = laterScheduleTimestamp(current.updatedAt, recordedAt);
		const updated: ScheduledOccurrenceRecord = {
			...current,
			status: "completed",
			revision: incrementScheduleDecimal(current.revision, this.limits.maxCounterDigits, "revision"),
			updatedAt: completedAt,
			completedAt,
		};
		this.occurrences.set(current.occurrenceKey, updated);
		this.leases.delete(current.occurrenceKey);
		this.completedLeaseTokens.set(current.occurrenceKey, leaseToken);
		return clone(updated);
	}

	async abandonOccurrence(
		key: ScheduledOccurrenceSequenceKey,
		leaseToken: string,
		recordedAt: string,
	): Promise<ScheduledOccurrenceRecord> {
		key = validateScheduledOccurrenceSequenceKey(key, this.limits);
		validateScheduleTimestamp(recordedAt);
		const current = this.occurrences.get(key.occurrenceKey);
		if (current === undefined || !sameScheduleGeneration(current.partition, key)) {
			throw new ScheduleOccurrenceNotFoundError();
		}
		this.assertAuthorityOpen(current.partition);
		const lease = this.leases.get(current.occurrenceKey);
		if (current.status !== "pending" || lease?.token !== leaseToken || lease.expiresAt <= recordedAt) {
			throw new ScheduleOccurrenceLeaseConflictError();
		}
		const updated: ScheduledOccurrenceRecord = {
			...current,
			revision: incrementScheduleDecimal(current.revision, this.limits.maxCounterDigits, "revision"),
			updatedAt: laterScheduleTimestamp(current.updatedAt, recordedAt),
		};
		this.occurrences.set(current.occurrenceKey, updated);
		this.leases.delete(current.occurrenceKey);
		return clone(updated);
	}

	async purgeTerminalOccurrences(
		partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const { ownerId, timelineId, generation } = normalizeSchedulePartition(partition, this.limits);
		partition = { ownerId, timelineId, generation };
		validateScheduleTimestamp(beforeRecordedAt);
		validateSchedulePageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_occurrence_purge_limit");
		const selected = [...this.occurrences.values()]
			.filter(
				(record) =>
					record.status !== "pending" &&
					sameScheduleGeneration(record.partition, partition) &&
					record.updatedAt < beforeRecordedAt,
			)
			.sort((left, right) => compareKeys(left.occurrenceKey, right.occurrenceKey))
			.slice(0, limit);
		for (const record of selected) {
			this.occurrences.delete(record.occurrenceKey);
			this.leases.delete(record.occurrenceKey);
			this.completedLeaseTokens.delete(record.occurrenceKey);
		}
		return selected.map((record) => record.occurrenceKey);
	}

	async purgeTerminalSchedules(
		partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const { ownerId, timelineId, generation } = normalizeSchedulePartition(partition, this.limits);
		partition = { ownerId, timelineId, generation };
		validateScheduleTimestamp(beforeRecordedAt);
		validateSchedulePageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_schedule_purge_limit");
		const referenced = new Set([...this.occurrences.values()].map((record) => record.scheduleKey));
		const selected = [...this.schedules.entries()]
			.filter(
				([, record]) =>
					record.status !== "active" &&
					sameScheduleGeneration(record.partition, partition) &&
					record.updatedAt < beforeRecordedAt &&
					!referenced.has(record.scheduleKey),
			)
			.sort(([, left], [, right]) => compareKeys(left.scheduleKey, right.scheduleKey))
			.slice(0, limit);
		for (const [key] of selected) {
			this.schedules.delete(key);
			this.scheduleFingerprints.delete(key);
		}
		return selected.map(([, record]) => record.scheduleKey);
	}

	async purgeAdvances(
		partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const { ownerId, timelineId, generation } = normalizeSchedulePartition(partition, this.limits);
		partition = { ownerId, timelineId, generation };
		validateScheduleTimestamp(beforeRecordedAt);
		validateSchedulePageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_schedule_advance_purge_limit");
		const referenced = new Set([...this.occurrences.values()].map(occurrenceAdvanceMapKey));
		const selected = [...this.advances.entries()]
			.filter(
				([, record]) =>
					sameScheduleGeneration(record.partition, partition) &&
					record.recordedAt < beforeRecordedAt &&
					!referenced.has(advanceMapKey(record)),
			)
			.sort(([, left], [, right]) => compareKeys(left.advanceKey, right.advanceKey))
			.slice(0, limit);
		for (const [key] of selected) {
			this.advances.delete(key);
			this.advanceFingerprints.delete(key);
		}
		return selected.map(([, record]) => record.advanceKey);
	}

	private occurrencesForAdvance(advance: ScheduleAdvanceDraft): ScheduledOccurrenceRecord[] {
		return [...this.occurrences.values()]
			.filter(
				(occurrence) =>
					occurrence.advanceId === advance.advanceId && sameScheduleGeneration(occurrence.partition, advance.partition),
			)
			.sort(compareOccurrenceOrder);
	}
}

function scheduleMapKey(key: GameScheduleKey | GameScheduleDraft | GameScheduleRecord): string {
	const partition = "partition" in key ? key.partition : key;
	return `${partition.ownerId}\0${partition.timelineId}\0${partition.generation}\0${key.scheduleId}`;
}

function advanceMapKey(advance: ScheduleAdvanceDraft): string {
	return `${advance.partition.ownerId}\0${advance.partition.timelineId}\0${advance.partition.generation}\0${advance.advanceId}`;
}

function occurrenceAdvanceMapKey(occurrence: ScheduledOccurrenceRecord): string {
	return `${occurrence.partition.ownerId}\0${occurrence.partition.timelineId}\0${occurrence.partition.generation}\0${occurrence.advanceId}`;
}

function scheduleGenerationKey(partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">): string {
	return `${partition.ownerId}\0${partition.timelineId}\0${partition.generation}`;
}

function compareOccurrenceOrder(left: ScheduledOccurrenceRecord, right: ScheduledOccurrenceRecord): number {
	const tick = compareScheduleDecimals(left.dueTime.tick, right.dueTime.tick);
	return tick === 0 ? compareKeys(left.occurrenceKey, right.occurrenceKey) : tick;
}

function compareAdvanceCursors(left: AdvanceCursor, right: AdvanceCursor): number {
	const tick = compareScheduleDecimals(left.dueTick, right.dueTick);
	return tick === 0 ? compareKeys(left.schedule.scheduleKey, right.schedule.scheduleKey) : tick;
}

function compareKeys(left: bigint, right: bigint): number {
	return left < right ? -1 : left > right ? 1 : 0;
}

function clone<T>(value: T): T {
	return structuredClone(value);
}

import type {
	GameTime,
	JsonValue,
	WorkAuthorityLane,
	WorkAuthorityRetirementRecord,
	WorkAuthorityRetirementRequest,
} from "@opengameagent/protocol";
import type { GameSignalRecord, SignalPartition, SignalRecordResult } from "./signal-types.js";

export type SchedulePartition = SignalPartition;

export interface GameScheduleDraft {
	readonly scheduleId: string;
	readonly partition: SchedulePartition;
	readonly kind: string;
	readonly payload: JsonValue;
	readonly dueTime: GameTime;
	readonly intervalTicks?: string;
	readonly maximumOccurrences?: string;
	readonly subjectIds: readonly string[];
	readonly causeIds: readonly string[];
	readonly recordedAt: string;
}

export type GameScheduleStatus = "active" | "completed" | "cancelled";

export interface GameScheduleRecord extends GameScheduleDraft {
	readonly scheduleKey: bigint;
	readonly status: GameScheduleStatus;
	readonly revision: string;
	readonly occurrences: string;
	readonly nextDueTick?: string;
	readonly createdAt: string;
	readonly updatedAt: string;
}

export interface GameScheduleKey extends Pick<SchedulePartition, "ownerId" | "timelineId" | "generation"> {
	readonly scheduleId: string;
}

export interface GameScheduleSequenceKey extends Pick<SchedulePartition, "ownerId" | "timelineId" | "generation"> {
	readonly scheduleKey: bigint;
}

export interface ScheduleAdvanceDraft {
	readonly advanceId: string;
	readonly partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">;
	readonly fromExclusive: GameTime;
	readonly toInclusive: GameTime;
	readonly recordedAt: string;
}

export interface ScheduleAdvanceRecord extends ScheduleAdvanceDraft {
	readonly advanceKey: bigint;
}

export type ScheduledOccurrenceStatus = "pending" | "completed" | "cancelled";

export interface ScheduledOccurrenceRecord {
	readonly occurrenceKey: bigint;
	readonly signalId: string;
	readonly scheduleKey: bigint;
	readonly scheduleId: string;
	readonly ordinal: string;
	readonly advanceId: string;
	readonly partition: SchedulePartition;
	readonly kind: string;
	readonly payload: JsonValue;
	readonly dueTime: GameTime;
	readonly subjectIds: readonly string[];
	readonly causeIds: readonly string[];
	readonly status: ScheduledOccurrenceStatus;
	readonly revision: string;
	readonly attempt: string;
	readonly createdAt: string;
	readonly updatedAt: string;
	readonly completedAt?: string;
	readonly cancelledAt?: string;
}

export interface ScheduledOccurrenceSequenceKey
	extends Pick<SchedulePartition, "ownerId" | "timelineId" | "generation"> {
	readonly occurrenceKey: bigint;
}

export interface ScheduledOccurrenceDelivery {
	readonly occurrence: ScheduledOccurrenceRecord;
	readonly leaseToken: string;
	readonly leaseOwnerId: string;
	readonly leaseExpiresAt: string;
}

export interface ScheduleAdvanceResult {
	readonly advance: ScheduleAdvanceRecord;
	readonly occurrences: readonly ScheduledOccurrenceRecord[];
}

export interface ScheduleDispatchResult {
	readonly occurrence: ScheduledOccurrenceRecord;
	readonly signal: GameSignalRecord;
	readonly matchedWaitIds: SignalRecordResult["matchedWaitIds"];
}

export interface ScheduleLimits {
	readonly maxIdentifierBytes: number;
	readonly maxPayloadBytes: number;
	readonly maxSubjectsPerSchedule: number;
	readonly maxCausesPerSchedule: number;
	readonly maxTickDigits: number;
	readonly maxCounterDigits: number;
	readonly maxActiveSchedulesPerTimelineGeneration: number;
	readonly maxOccurrencesPerAdvance: number;
	readonly maxPendingOccurrencesPerTimelineGeneration: number;
	readonly maxSchedulePageSize: number;
	readonly maxOccurrencePageSize: number;
	readonly maxClaimBatchSize: number;
	readonly maxPurgeBatchSize: number;
}

export interface ScheduleListRequest extends Omit<SchedulePartition, "sessionId" | "actorId"> {
	readonly sessionId?: string;
	readonly actorId?: string;
	readonly statuses?: readonly GameScheduleStatus[];
	readonly afterScheduleKey?: bigint;
	readonly limit: number;
}

export interface SchedulePage {
	readonly records: readonly GameScheduleRecord[];
	readonly nextAfterScheduleKey?: bigint;
}

export interface ScheduledOccurrenceListRequest extends Omit<SchedulePartition, "sessionId" | "actorId"> {
	readonly sessionId?: string;
	readonly actorId?: string;
	readonly statuses?: readonly ScheduledOccurrenceStatus[];
	readonly afterOccurrenceKey?: bigint;
	readonly limit: number;
}

export interface ScheduledOccurrencePage {
	readonly records: readonly ScheduledOccurrenceRecord[];
	readonly nextAfterOccurrenceKey?: bigint;
}

export interface ScheduleOccurrenceClaimRequest extends Omit<SchedulePartition, "sessionId" | "actorId"> {
	readonly sessionId?: string;
	readonly actorId?: string;
	readonly leaseOwnerId: string;
	readonly operationalNow: string;
	readonly leaseExpiresAt: string;
	readonly limit: number;
}

export interface ScheduleAuthorityRetirementRecord extends WorkAuthorityRetirementRecord {
	readonly cancelledScheduleCount: number;
	readonly cancelledOccurrenceCount: number;
}

export interface ScheduleStore {
	beginAuthorityRetirement(request: WorkAuthorityRetirementRequest): Promise<ScheduleAuthorityRetirementRecord>;
	finalizeAuthorityRetirement(request: WorkAuthorityRetirementRequest): Promise<ScheduleAuthorityRetirementRecord>;
	readAuthorityRetirement(partition: WorkAuthorityLane): Promise<ScheduleAuthorityRetirementRecord | undefined>;
	create(schedule: GameScheduleDraft, maxActive: number): Promise<GameScheduleRecord>;
	getSchedule(key: GameScheduleKey): Promise<GameScheduleRecord | undefined>;
	getScheduleByKey(key: GameScheduleSequenceKey): Promise<GameScheduleRecord | undefined>;
	listSchedules(request: ScheduleListRequest): Promise<SchedulePage>;
	cancelSchedule(key: GameScheduleKey, expectedRevision: string, recordedAt: string): Promise<GameScheduleRecord>;
	advance(advance: ScheduleAdvanceDraft, maxGenerated: number, maxPending: number): Promise<ScheduleAdvanceResult>;
	getOccurrence(key: ScheduledOccurrenceSequenceKey): Promise<ScheduledOccurrenceRecord | undefined>;
	listOccurrences(request: ScheduledOccurrenceListRequest): Promise<ScheduledOccurrencePage>;
	claimOccurrences(request: ScheduleOccurrenceClaimRequest): Promise<readonly ScheduledOccurrenceDelivery[]>;
	completeOccurrence(
		key: ScheduledOccurrenceSequenceKey,
		leaseToken: string,
		recordedAt: string,
	): Promise<ScheduledOccurrenceRecord>;
	abandonOccurrence(
		key: ScheduledOccurrenceSequenceKey,
		leaseToken: string,
		recordedAt: string,
	): Promise<ScheduledOccurrenceRecord>;
	purgeTerminalOccurrences(
		partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]>;
	purgeTerminalSchedules(
		partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]>;
	purgeAdvances(
		partition: Pick<SchedulePartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]>;
}

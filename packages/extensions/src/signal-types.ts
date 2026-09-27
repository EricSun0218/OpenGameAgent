import type { GameTime, JsonValue } from "@opengameagent/protocol";

export interface SignalPartition {
	readonly ownerId: string;
	readonly timelineId: string;
	readonly generation: string;
	readonly sessionId?: string;
	readonly actorId?: string;
}

export interface GameSignalDraft {
	readonly signalId: string;
	readonly partition: SignalPartition;
	readonly kind: string;
	readonly payload: JsonValue;
	readonly gameTime: GameTime;
	readonly subjectIds: readonly string[];
	readonly causeIds: readonly string[];
	readonly recordedAt: string;
}

export interface GameSignalRecord extends GameSignalDraft {
	readonly signalKey: bigint;
}

export interface GameSignalKey {
	readonly ownerId: string;
	readonly timelineId: string;
	readonly generation: string;
	readonly signalId: string;
}

export interface GameSignalSequenceKey extends Pick<SignalPartition, "ownerId" | "timelineId" | "generation"> {
	readonly signalKey: bigint;
}

export interface SignalWaitSelector {
	readonly kinds: readonly string[];
	readonly subjectIdsAny?: readonly string[];
	readonly afterSignalKey?: bigint;
	readonly afterTickExclusive?: string;
	readonly throughTickInclusive?: string;
}

export type SignalWaitTarget =
	| { readonly kind: "goal_step"; readonly goalId: string; readonly stepId: string }
	| { readonly kind: "correlation"; readonly correlationId: string };

export interface SignalWaitDraft {
	readonly waitId: string;
	readonly partition: SignalPartition;
	readonly selector: SignalWaitSelector;
	readonly target: SignalWaitTarget;
	readonly recordedAt: string;
}

export type SignalWaitStatus = "waiting" | "matched" | "cancelled" | "expired";

export interface SignalWaitRecord extends SignalWaitDraft {
	readonly waitKey: bigint;
	readonly status: SignalWaitStatus;
	readonly revision: string;
	readonly matchedSignalKey?: bigint;
	readonly createdAt: string;
	readonly updatedAt: string;
}

export interface SignalWaitKey {
	readonly ownerId: string;
	readonly timelineId: string;
	readonly generation: string;
	readonly waitId: string;
}

export interface SignalLimits {
	readonly maxPayloadBytes: number;
	readonly maxSubjectsPerSignal: number;
	readonly maxCausesPerSignal: number;
	readonly maxSignalFilterKinds: number;
	readonly maxWaitKinds: number;
	readonly maxWaitSubjects: number;
	readonly maxWaitMatchesPerSignal: number;
	readonly maxSignalPageSize: number;
	readonly maxWaitPageSize: number;
	readonly maxExpireBatchSize: number;
	readonly maxPurgeBatchSize: number;
}

export interface SignalRecordResult {
	readonly signal: GameSignalRecord;
	readonly matchedWaitIds: readonly string[];
}

export interface SignalListRequest extends Omit<SignalPartition, "sessionId" | "actorId"> {
	readonly sessionId?: string;
	readonly actorId?: string;
	readonly kinds?: readonly string[];
	readonly afterSignalKey?: bigint;
	readonly limit: number;
}

export interface SignalPage {
	readonly records: readonly GameSignalRecord[];
	readonly nextAfterSignalKey?: bigint;
}

export interface SignalWaitListRequest extends Omit<SignalPartition, "sessionId" | "actorId"> {
	readonly sessionId?: string;
	readonly actorId?: string;
	readonly statuses?: readonly SignalWaitStatus[];
	readonly afterWaitKey?: bigint;
	readonly limit: number;
}

export interface SignalWaitPage {
	readonly records: readonly SignalWaitRecord[];
	readonly nextAfterWaitKey?: bigint;
}

export interface SignalStore {
	record(signal: GameSignalDraft, maxMatches: number): Promise<SignalRecordResult>;
	getSignal(key: GameSignalKey): Promise<GameSignalRecord | undefined>;
	getSignalByKey(key: GameSignalSequenceKey): Promise<GameSignalRecord | undefined>;
	listSignals(request: SignalListRequest): Promise<SignalPage>;
	registerWait(wait: SignalWaitDraft, maxMatches: number): Promise<SignalWaitRecord>;
	getWait(key: SignalWaitKey): Promise<SignalWaitRecord | undefined>;
	listWaits(request: SignalWaitListRequest): Promise<SignalWaitPage>;
	cancelWait(key: SignalWaitKey, expectedRevision: string, recordedAt: string): Promise<SignalWaitRecord>;
	expireWaits(
		partition: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		throughTickInclusive: string,
		recordedAt: string,
		limit: number,
	): Promise<readonly bigint[]>;
	purgeTerminalWaits(
		partition: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]>;
	purgeSignals(
		partition: Pick<SignalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]>;
}

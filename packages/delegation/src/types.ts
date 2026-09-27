import type { RuntimeActorOptions } from "@opengameagent/runtime";
import type { JsonValue, RunCoordinate } from "@opengameagent/protocol";

export interface DelegationPartition {
	readonly ownerId: string;
	readonly timelineId: string;
	readonly generation: string;
}

export interface DelegationParent {
	readonly sessionId: string;
	readonly actorId: string;
	readonly inputId: string;
	readonly runId: string;
	readonly turn: string;
	readonly toolCallId: string;
}

export type DelegationMode = "foreground" | "background";
export type DelegationStatus = "pending" | "running" | "completed" | "failed" | "cancelled";

export interface DelegationTarget {
	readonly delegateId: string;
	readonly sessionId: string;
	readonly actorId: string;
	readonly maxTurns: number;
}

export interface DelegationTargetRegistration extends DelegationTarget {
	readonly actor: Omit<RuntimeActorOptions, "sessionId" | "actorId" | "shouldStopAfterTurn">;
}

export interface DelegationDraft {
	readonly delegationId: string;
	readonly partition: DelegationPartition;
	readonly parent: DelegationParent;
	readonly target: DelegationTarget;
	readonly mode: DelegationMode;
	readonly task: JsonValue;
	readonly context?: JsonValue;
	readonly rootDelegationId: string;
	readonly parentDelegationId?: string;
	readonly depth: string;
	readonly childInputId: string;
	readonly createdAt: string;
}

export interface DelegationRecord extends DelegationDraft {
	readonly delegationKey: bigint;
	readonly status: DelegationStatus;
	readonly revision: string;
	readonly attempt: string;
	readonly childRunId?: string;
	readonly result?: JsonValue;
	readonly failureCode?: string;
	readonly startedAt?: string;
	readonly settledAt?: string;
	readonly updatedAt: string;
}

export interface DelegationKey extends DelegationPartition {
	readonly delegationId: string;
}

export interface DelegationSequenceKey extends DelegationPartition {
	readonly delegationKey: bigint;
}

export interface DelegationDelivery {
	readonly record: DelegationRecord;
	readonly leaseToken: string;
	readonly leaseOwnerId: string;
	readonly leaseExpiresAt: string;
}

export interface DelegationClaimRequest extends DelegationKey {
	readonly leaseOwnerId: string;
	readonly operationalNow: string;
	readonly leaseExpiresAt: string;
}

export interface DelegationListRequest extends DelegationPartition {
	readonly statuses?: readonly DelegationStatus[];
	readonly afterDelegationKey?: bigint;
	readonly limit: number;
}

export interface DelegationPage {
	readonly records: readonly DelegationRecord[];
	readonly nextAfterDelegationKey?: bigint;
}

export interface DelegationLimits {
	readonly maxIdentifierBytes: number;
	readonly maxControlTextBytes: number;
	readonly maxToolDescriptionBytes: number;
	readonly maxTaskBytes: number;
	readonly maxContextBytes: number;
	readonly maxResultBytes: number;
	readonly maxModelResultBytes: number;
	readonly maxJsonDepth: number;
	readonly maxCounterDigits: number;
	readonly maxDepth: number;
	readonly maxTargets: number;
	readonly maxPendingPerGeneration: number;
	readonly maxActiveRuns: number;
	readonly maxPageSize: number;
	readonly maxRecoveryBatchSize: number;
	readonly maxPurgeBatchSize: number;
}

export interface DelegationStore {
	create(draft: DelegationDraft, maxPendingPerGeneration: number): Promise<DelegationRecord>;
	get(key: DelegationKey): Promise<DelegationRecord | undefined>;
	list(request: DelegationListRequest): Promise<DelegationPage>;
	claim(request: DelegationClaimRequest): Promise<DelegationDelivery>;
	assertLease(key: DelegationSequenceKey, leaseToken: string, operationalNow: string): Promise<DelegationDelivery>;
	renew(
		key: DelegationSequenceKey,
		leaseToken: string,
		operationalNow: string,
		leaseExpiresAt: string,
	): Promise<DelegationDelivery>;
	bindRun(
		key: DelegationSequenceKey,
		leaseToken: string,
		childRunId: string,
		operationalNow: string,
	): Promise<DelegationRecord>;
	complete(
		key: DelegationSequenceKey,
		leaseToken: string,
		result: JsonValue,
		operationalNow: string,
	): Promise<DelegationRecord>;
	fail(
		key: DelegationSequenceKey,
		leaseToken: string,
		failureCode: string,
		operationalNow: string,
	): Promise<DelegationRecord>;
	abandon(key: DelegationSequenceKey, leaseToken: string, operationalNow: string): Promise<DelegationRecord>;
	cancel(key: DelegationKey, expectedRevision: string, operationalNow: string): Promise<DelegationRecord>;
	purgeTerminal(
		partition: DelegationPartition,
		beforeOperationalTime: string,
		limit: number,
	): Promise<readonly bigint[]>;
}

export interface DelegatedTaskPayload {
	readonly type: "delegation.task";
	readonly delegation: {
		readonly delegationId: string;
		readonly rootDelegationId: string;
		readonly parentDelegationId?: string;
		readonly depth: string;
		readonly parent: Omit<DelegationParent, "toolCallId">;
	};
	readonly task: JsonValue;
	readonly context?: JsonValue;
}

export interface DelegationOutcomeContext {
	readonly record: DelegationRecord;
	readonly childCoordinate: RunCoordinate;
	readonly childFinalOutput: string;
	readonly signal: AbortSignal;
}

export type DelegationOutcomeProjector = (context: DelegationOutcomeContext) => JsonValue | Promise<JsonValue>;

import type {
	GameTime,
	JsonValue,
	WorkAuthorityLane,
	WorkAuthorityRetirementRequest,
	WorkAuthorityRetirementRecord,
} from "@opengameagent/protocol";

export interface MailboxGenerationScope {
	readonly ownerId: string;
	readonly timelineId: string;
	readonly generation: string;
}

export interface MailboxRecipient extends WorkAuthorityLane {}

export interface MailboxAuthorityRetirementRecord extends WorkAuthorityRetirementRecord {
	readonly cancelledMessageCount: number;
}

export interface MailboxMessageDraft {
	readonly messageId: string;
	readonly recipient: MailboxRecipient;
	readonly senderId?: string;
	readonly kind: string;
	readonly payload: JsonValue;
	readonly gameTime: GameTime;
	readonly correlationId?: string;
	readonly recordedAt: string;
}

export type MailboxMessageStatus = "pending" | "completed" | "cancelled";

export interface MailboxMessageRecord extends MailboxMessageDraft {
	readonly messageKey: bigint;
	readonly status: MailboxMessageStatus;
	readonly revision: string;
	readonly attempt: string;
	readonly createdAt: string;
	readonly updatedAt: string;
	readonly completedAt?: string;
	readonly cancelledAt?: string;
}

export interface MailboxMessageKey extends MailboxRecipient {
	readonly messageId: string;
}

export interface MailboxMessageSequenceKey extends MailboxRecipient {
	readonly messageKey: bigint;
}

export interface MailboxDelivery {
	readonly message: MailboxMessageRecord;
	readonly leaseToken: string;
	readonly leaseOwnerId: string;
	readonly leaseExpiresAt: string;
}

export interface MailboxLimits {
	readonly maxIdentifierBytes: number;
	readonly maxPayloadBytes: number;
	readonly maxTickDigits: number;
	readonly maxCounterDigits: number;
	readonly maxPendingMessagesPerTimelineGeneration: number;
	readonly maxPendingMessagesPerRecipient: number;
	readonly maxActiveDeliveries: number;
	readonly maxPageSize: number;
	readonly maxClaimBatchSize: number;
	readonly maxPurgeBatchSize: number;
}

export interface MailboxListRequest extends MailboxGenerationScope {
	readonly sessionId?: string;
	readonly actorId?: string;
	readonly statuses?: readonly MailboxMessageStatus[];
	readonly afterMessageKey?: bigint;
	readonly limit: number;
}

export interface MailboxPage {
	readonly records: readonly MailboxMessageRecord[];
	readonly nextAfterMessageKey?: bigint;
}

export interface MailboxClaimRequest extends MailboxRecipient {
	readonly leaseOwnerId: string;
	readonly operationalNow: string;
	readonly leaseExpiresAt: string;
	readonly limit: number;
}

export interface MailboxRenewRequest extends MailboxMessageSequenceKey {
	readonly leaseToken: string;
	readonly operationalNow: string;
	readonly leaseExpiresAt: string;
}

export interface MailboxStatusSummary extends MailboxRecipient {
	readonly pending: bigint;
	readonly completed: bigint;
	readonly cancelled: bigint;
	readonly oldestPendingMessageKey?: bigint;
	readonly latestMessageKey?: bigint;
}

export interface MailboxStore {
	beginAuthorityRetirement(request: WorkAuthorityRetirementRequest): Promise<MailboxAuthorityRetirementRecord>;
	finalizeAuthorityRetirement(request: WorkAuthorityRetirementRequest): Promise<MailboxAuthorityRetirementRecord>;
	readAuthorityRetirement(partition: WorkAuthorityLane): Promise<MailboxAuthorityRetirementRecord | undefined>;
	enqueue(
		message: MailboxMessageDraft,
		maxPendingPerGeneration: number,
		maxPendingPerRecipient: number,
	): Promise<MailboxMessageRecord>;
	get(key: MailboxMessageKey): Promise<MailboxMessageRecord | undefined>;
	getByKey(key: MailboxMessageSequenceKey): Promise<MailboxMessageRecord | undefined>;
	list(request: MailboxListRequest): Promise<MailboxPage>;
	status(recipient: MailboxRecipient): Promise<MailboxStatusSummary>;
	claim(request: MailboxClaimRequest): Promise<readonly MailboxDelivery[]>;
	assertLease(key: MailboxMessageSequenceKey, leaseToken: string, operationalNow: string): Promise<MailboxDelivery>;
	renew(request: MailboxRenewRequest): Promise<MailboxDelivery>;
	complete(
		key: MailboxMessageSequenceKey,
		leaseToken: string,
		leaseOwnerId: string,
		operationalNow: string,
	): Promise<MailboxMessageRecord>;
	abandon(
		key: MailboxMessageSequenceKey,
		leaseToken: string,
		leaseOwnerId: string,
		operationalNow: string,
	): Promise<MailboxMessageRecord>;
	cancel(key: MailboxMessageKey, expectedRevision: string, operationalNow: string): Promise<MailboxMessageRecord>;
	purgeTerminal(recipient: MailboxRecipient, beforeOperationalTime: string, limit: number): Promise<readonly bigint[]>;
}

import type {
	JsonValue,
	RetentionProtectionBudget,
	RunCoordinate,
	RuntimeActionDelivery,
	RuntimeActionRecoveryDelivery,
	StreamKey,
} from "@opengameagent/protocol";

export type ActionReplayPolicy = "never" | "safe" | "recoverable";
export type ActionStatus = "pending" | "claimed" | "succeeded" | "failed" | "uncertain";

export interface ActionWaitOptions {
	readonly pollIntervalMs: number;
	readonly signal?: AbortSignal;
}

export interface ActionAuthority {
	timelineId: string;
	generation: string;
	revision: string;
}

export interface ActionGoalProjectionBinding {
	readonly goalId: string;
	readonly partition: {
		readonly ownerId: string;
		readonly timelineId: string;
		readonly generation: string;
		readonly sessionId?: string;
		readonly actorId?: string;
	};
	readonly stepId: string;
	readonly expectedRevision: string;
	readonly inputId: string;
}

export interface ActionGoalProjectionLimits {
	readonly maxBindingBytes: number;
	readonly maxPendingPerStream: number;
	readonly maxListBatchSize: number;
	readonly maxRecordBytes: number;
}

export interface ActionJournalOptions {
	readonly goalProjection?: ActionGoalProjectionLimits;
}

export interface ActionIntentDraft extends StreamKey {
	coordinate: RunCoordinate;
	actionName: string;
	operationKey: string;
	payload: unknown;
	expectedAuthority: ActionAuthority;
	conflictKeys: readonly string[];
	replayPolicy: ActionReplayPolicy;
	goalProjection?: ActionGoalProjectionBinding;
}

export interface PreparedActionIntent extends StreamKey {
	operationId: string;
	intentFingerprint: string;
	payloadFingerprint: string;
	coordinate: RunCoordinate;
	actionName: string;
	operationKey: string;
	payload: JsonValue;
	expectedAuthority: ActionAuthority;
	conflictKeys: string[];
	replayPolicy: ActionReplayPolicy;
	goalProjection?: ActionGoalProjectionBinding;
}

export interface ActionClaim {
	claimToken: string;
	claimantId: string;
	attempt: string;
	claimedAt: string;
	expiresAt: string;
}

export interface ActionReceipt {
	operationId: string;
	claimToken: string;
	attempt: string;
	outcome: "succeeded" | "rejected" | "unknown";
	receivedAt: string;
	result?: JsonValue;
	modelResult?: JsonValue;
	authorityAfter?: ActionAuthority;
}

export interface ActionRecord extends PreparedActionIntent {
	status: ActionStatus;
	attempt: string;
	createdAt: string;
	updatedAt: string;
	claim?: ActionClaim;
	receipt?: ActionReceipt;
	goalProjectionSettlement?: ActionGoalProjectionSettlement;
}

export interface ActionGoalProjectionSettlement {
	readonly outcome: "advanced" | "source_failed";
	readonly settledAt: string;
}

export interface ActionGoalProjectionListRequest extends StreamKey {
	readonly afterSequence?: string;
	readonly limit: number;
}

export interface ActionGoalProjectionCandidate {
	readonly sequence: string;
	readonly action: ActionRecord;
}

export interface ActionGoalProjectionPage {
	readonly candidates: readonly ActionGoalProjectionCandidate[];
	readonly nextAfterSequence?: string;
}

export interface ActionGoalProjectionSettlementInput extends StreamKey {
	readonly operationId: string;
	readonly intentFingerprint: string;
	readonly outcome: ActionGoalProjectionSettlement["outcome"];
	readonly settledAt: string;
}

export interface ActionTerminalPurgeRequest extends StreamKey {
	readonly beforeUpdatedAt: string;
	readonly limit: number;
	readonly retainOperationIds: readonly string[];
	readonly retainBudget: RetentionProtectionBudget;
}

export type ActionDelivery = Omit<RuntimeActionDelivery, "action"> & { action: ActionRecord };
export type ActionRecoveryDelivery = Omit<RuntimeActionRecoveryDelivery, "action"> & { action: ActionRecord };

/** Whether this selection committed any claim, expiry, or closed-run retirement. */
export interface ActionDeliverySelection {
	readonly delivery?: ActionDelivery;
	readonly changed: boolean;
}

export interface ActionClaimRequest {
	sessionId: string;
	actorId: string;
	runId?: string;
	claimantId: string;
	claimToken: string;
	claimedAt: string;
	expiresAt: string;
	candidateLimit: number;
}

export interface ActionReceiptInput extends StreamKey {
	operationId: string;
	claimToken: string;
	attempt: string;
	outcome: ActionReceipt["outcome"];
	receivedAt: string;
	result?: unknown;
	modelResult?: unknown;
	authorityAfter?: ActionAuthority;
}

export interface ActionReconcileInput extends StreamKey {
	operationId: string;
	claimToken: string;
	attempt: string;
	resolution: "applied" | "not-applied" | "unknown";
	reconciledAt: string;
	result?: unknown;
	modelResult?: unknown;
	authorityAfter?: ActionAuthority;
}

export interface ActionJournal {
	readonly goalProjectionLimits?: Readonly<ActionGoalProjectionLimits>;
	ensure(intent: PreparedActionIntent, now: string): Promise<ActionRecord>;
	get(stream: StreamKey, operationId: string): Promise<ActionRecord | undefined>;
	recoverDelivery(stream: StreamKey): Promise<ActionRecoveryDelivery | undefined>;
	nextDelivery(request: ActionClaimRequest): Promise<ActionDeliverySelection>;
	recordReceipt(input: ActionReceiptInput): Promise<ActionRecord>;
	reconcile(input: ActionReconcileInput): Promise<ActionRecord>;
	listGoalProjections(request: ActionGoalProjectionListRequest): Promise<ActionGoalProjectionPage>;
	settleGoalProjection(input: ActionGoalProjectionSettlementInput): Promise<ActionRecord>;
	purgeTerminal(request: ActionTerminalPurgeRequest): Promise<readonly string[]>;
}

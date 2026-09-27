import type { GameTime } from "@opengameagent/protocol";

export interface GoalPartition {
	readonly ownerId: string;
	readonly timelineId: string;
	readonly generation: string;
	readonly sessionId?: string;
	readonly actorId?: string;
}

export interface GoalKey extends GoalPartition {
	readonly goalId: string;
}

export type GoalStatus = "active" | "paused" | "achieved" | "failed" | "cancelled";
export type GoalPlanStepStatus = "pending" | "in_progress" | "completed";

export interface GoalEvidence {
	readonly kind: string;
	readonly referenceId: string;
}

export interface GoalPlanStep {
	readonly stepId: string;
	readonly description: string;
	readonly status: GoalPlanStepStatus;
	readonly evidence: readonly GoalEvidence[];
}

export interface GoalDraft {
	readonly goalId: string;
	readonly partition: GoalPartition;
	readonly objective: string;
	readonly plan: readonly GoalPlanStep[];
	readonly gameTime: GameTime;
	readonly recordedAt: string;
}

export interface GoalRecord {
	readonly goalId: string;
	readonly partition: GoalPartition;
	readonly objective: string;
	readonly plan: readonly GoalPlanStep[];
	readonly gameTime: GameTime;
	readonly goalKey: bigint;
	readonly revision: string;
	readonly status: GoalStatus;
	readonly createdGameTime: GameTime;
	readonly createdAt: string;
	readonly updatedAt: string;
	readonly lastHostAdvanceFingerprint: string | null;
}

export interface GoalReplacement {
	readonly plan: readonly GoalPlanStep[];
	readonly status: GoalStatus;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
	readonly lastHostAdvanceFingerprint: string | null;
}

export interface GoalListRequest extends Omit<GoalPartition, "sessionId" | "actorId"> {
	readonly sessionId?: string;
	readonly actorId?: string;
	readonly statuses?: readonly GoalStatus[];
	readonly afterGoalKey?: bigint;
	readonly limit: number;
}

export interface GoalPage {
	readonly records: readonly GoalRecord[];
	readonly nextAfterGoalKey?: bigint;
}

export interface GoalLimits {
	readonly maxObjectiveBytes: number;
	readonly maxStepsPerGoal: number;
	readonly maxStepBytes: number;
	readonly maxPlanBytes: number;
	readonly maxRecordBytes: number;
	readonly maxModelResultBytes: number;
	readonly maxEvidencePerStep: number;
	readonly maxEvidenceKindBytes: number;
	readonly maxEvidenceReferenceBytes: number;
	readonly maxOpenGoalsPerOwnerGeneration: number;
	readonly maxListPageSize: number;
	readonly maxPurgeBatchSize: number;
}

export interface GoalStore {
	readonly limits: Readonly<GoalLimits>;
	create(draft: GoalDraft, maxOpenGoals: number, signal?: AbortSignal): Promise<GoalRecord>;
	get(key: GoalKey): Promise<GoalRecord | undefined>;
	replace(key: GoalKey, expectedRevision: string, replacement: GoalReplacement): Promise<GoalRecord>;
	list(request: GoalListRequest): Promise<GoalPage>;
	retireAuthority(draft: GoalAuthorityRetirementDraft, signal?: AbortSignal): Promise<GoalAuthorityRetirementRecord>;
	purgeTerminal(
		partition: Pick<GoalPartition, "ownerId" | "timelineId" | "generation">,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]>;
}

export interface GoalEvidenceValidationRequest {
	readonly goal: GoalRecord;
	readonly step: GoalPlanStep;
	readonly evidence: GoalEvidence;
	readonly gameTime: GameTime;
	readonly inputId?: string;
	readonly signal?: AbortSignal;
}

export interface GoalEvidenceVerifier {
	validate(request: GoalEvidenceValidationRequest): Promise<boolean>;
}

export interface GoalCommittedInputValidationRequest {
	readonly partition: GoalPartition;
	readonly inputId: string;
	readonly signal?: AbortSignal;
}

export interface GoalCommittedInputVerifier {
	validate(request: GoalCommittedInputValidationRequest): Promise<boolean>;
}

export interface GoalHostAdvanceRequest {
	readonly goalId: string;
	readonly partition: GoalPartition;
	readonly stepId: string;
	readonly inputId: string;
	readonly expectedRevision: string;
	readonly evidence: GoalEvidence;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
	readonly signal?: AbortSignal;
}

export type GoalHostAdvanceResult =
	| { readonly status: "goal_not_found" }
	| {
			readonly status:
				| "advanced"
				| "already_advanced"
				| "revision_conflict"
				| "plan_not_active"
				| "input_not_committed"
				| "step_not_current"
				| "step_already_completed"
				| "evidence_rejected";
			readonly goal: GoalRecord;
	  };

export interface GoalCreateRequest extends GoalDraft {
	readonly signal?: AbortSignal;
}

export interface GoalUpdateRequest extends GoalKey {
	readonly expectedRevision: string;
	readonly plan?: readonly GoalPlanStep[];
	readonly status?: GoalStatus;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
	readonly signal?: AbortSignal;
}

export interface GoalAuthorityRetirementDraft {
	readonly retirementId: string;
	readonly stateRevision: string;
	readonly partition: GoalPartition;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
}

export interface GoalAuthorityRetirementRequest extends GoalAuthorityRetirementDraft {
	readonly signal?: AbortSignal;
}

export interface GoalAuthorityRetirementRecord extends GoalAuthorityRetirementDraft {
	readonly cancelledGoalCount: number;
}

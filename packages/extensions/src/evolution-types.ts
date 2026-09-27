import type { GameTime } from "@opengameagent/protocol";

export interface BehaviorPartition {
	readonly ownerId: string;
	readonly timelineId: string;
	readonly generation: string;
}

export type BehaviorRegistryScope =
	| { readonly kind: "actor"; readonly actorId: string }
	| { readonly kind: "shared"; readonly registryId: string };

export interface BehaviorRegistryKey extends BehaviorPartition {
	readonly scope: BehaviorRegistryScope;
	readonly skillName: string;
}

export interface BehaviorProposalKey extends BehaviorPartition {
	readonly proposalId: string;
}

export interface BehaviorVersionKey extends BehaviorRegistryKey {
	readonly version: string;
}

export type BehaviorEvolutionMode = "off" | "conservative" | "aggressive";

export interface BehaviorEvidence {
	readonly kind: string;
	readonly referenceId: string;
}

export interface BehaviorReflection {
	readonly summary: string;
	readonly evidence: readonly BehaviorEvidence[];
}

export interface BehaviorStepDraft {
	readonly stepId: string;
	readonly instruction: string;
	readonly toolName: string;
}

export interface BehaviorStep extends BehaviorStepDraft {
	readonly toolFingerprint: string;
}

export interface BehaviorToolDescriptor {
	readonly name: string;
	readonly fingerprint: string;
}

export interface BehaviorProposalDraft {
	readonly proposalId: string;
	readonly partition: BehaviorPartition;
	readonly scope: BehaviorRegistryScope;
	readonly skillName: string;
	readonly description: string;
	readonly reflection: BehaviorReflection;
	readonly steps: readonly BehaviorStep[];
	readonly baseVersion?: string;
	readonly contentFingerprint: string;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
}

export type BehaviorProposalStatus = "pending" | "validated" | "rejected" | "activated";

export interface BehaviorProposalRecord extends BehaviorProposalDraft {
	readonly proposalKey: bigint;
	readonly revision: string;
	readonly status: BehaviorProposalStatus;
	readonly decisionCode?: string;
	readonly createdAt: string;
	readonly updatedAt: string;
}

export interface BehaviorProposalDecision {
	readonly status: "validated" | "rejected";
	readonly decisionCode: string;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
}

export interface BehaviorSkillVersion extends BehaviorRegistryKey {
	readonly versionKey: bigint;
	readonly version: string;
	readonly sourceProposalId: string;
	readonly baseVersion?: string;
	readonly description: string;
	readonly reflection: BehaviorReflection;
	readonly steps: readonly BehaviorStep[];
	readonly contentFingerprint: string;
	readonly gameTime: GameTime;
	readonly createdAt: string;
}

export interface BehaviorRegistryState extends BehaviorRegistryKey {
	readonly revision: string;
	readonly latestVersion: string;
	readonly activeVersion?: string;
	readonly gameTime: GameTime;
	readonly updatedAt: string;
}

export type BehaviorRegistryChangeType = "activate" | "rollback" | "demote";

export interface BehaviorRegistryChange extends BehaviorRegistryKey {
	readonly changeKey: bigint;
	readonly registryRevision: string;
	readonly type: BehaviorRegistryChangeType;
	readonly fromVersion?: string;
	readonly toVersion?: string;
	readonly sourceProposalId?: string;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
}

export interface BehaviorActivationResult {
	readonly proposal: BehaviorProposalRecord;
	readonly version: BehaviorSkillVersion;
	readonly registry: BehaviorRegistryState;
	readonly change: BehaviorRegistryChange;
}

export interface BehaviorActiveSkill {
	readonly registry: BehaviorRegistryState;
	readonly version: BehaviorSkillVersion;
}

export interface BehaviorProposalCreateRequest {
	readonly proposalId: string;
	readonly partition: BehaviorPartition;
	readonly scope: BehaviorRegistryScope;
	readonly skillName: string;
	readonly description: string;
	readonly reflection: BehaviorReflection;
	readonly steps: readonly BehaviorStepDraft[];
	readonly gameTime: GameTime;
	readonly recordedAt: string;
	readonly signal?: AbortSignal;
}

export interface BehaviorProposalReviewRequest extends BehaviorProposalKey {
	readonly expectedRevision: string;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
	readonly signal?: AbortSignal;
}

export interface BehaviorProposalReviewResult {
	readonly proposal: BehaviorProposalRecord;
	readonly activation?: BehaviorActivationResult;
}

export interface BehaviorProposalActivationRequest extends BehaviorProposalKey {
	readonly expectedRevision: string;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
	readonly signal?: AbortSignal;
}

export interface BehaviorRegistryTransitionRequest extends BehaviorRegistryKey {
	readonly expectedRevision: string;
	readonly targetVersion?: string;
	readonly gameTime: GameTime;
	readonly recordedAt: string;
	readonly signal?: AbortSignal;
}

export interface BehaviorActiveListRequest extends BehaviorPartition {
	readonly scopes: readonly BehaviorRegistryScope[];
	readonly afterVersionKey?: bigint;
	readonly limit: number;
}

export interface BehaviorActivePage {
	readonly records: readonly BehaviorActiveSkill[];
	readonly nextAfterVersionKey?: bigint;
}

export interface BehaviorActiveViewRequest extends BehaviorActiveListRequest {
	readonly signal?: AbortSignal;
}

export interface BehaviorProposalListRequest extends BehaviorPartition {
	readonly statuses?: readonly BehaviorProposalStatus[];
	readonly afterProposalKey?: bigint;
	readonly limit: number;
}

export interface BehaviorProposalPage {
	readonly records: readonly BehaviorProposalRecord[];
	readonly nextAfterProposalKey?: bigint;
}

export interface BehaviorChangeListRequest extends BehaviorRegistryKey {
	readonly afterChangeKey?: bigint;
	readonly limit: number;
}

export interface BehaviorChangePage {
	readonly records: readonly BehaviorRegistryChange[];
	readonly nextAfterChangeKey?: bigint;
}

export interface BehaviorLimits {
	readonly maxSkillNameBytes: number;
	readonly maxDescriptionBytes: number;
	readonly maxReflectionBytes: number;
	readonly maxEvidencePerProposal: number;
	readonly maxEvidenceKindBytes: number;
	readonly maxEvidenceReferenceBytes: number;
	readonly maxStepsPerSkill: number;
	readonly maxStepInstructionBytes: number;
	readonly maxToolNameBytes: number;
	readonly maxDecisionCodeBytes: number;
	readonly maxJsonDepth: number;
	readonly maxToolsPerCatalog: number;
	readonly maxToolCatalogBytes: number;
	readonly maxToolSchemaDepth: number;
	readonly maxScopesPerView: number;
	readonly maxOpenProposalsPerOwnerGeneration: number;
	readonly maxVersionsPerSkill: number;
	readonly maxListPageSize: number;
	readonly maxPurgeBatchSize: number;
}

export interface BehaviorStore {
	createProposal(draft: BehaviorProposalDraft, maxOpenProposals: number): Promise<BehaviorProposalRecord>;
	getProposal(key: BehaviorProposalKey): Promise<BehaviorProposalRecord | undefined>;
	decideProposal(
		key: BehaviorProposalKey,
		expectedRevision: string,
		decision: BehaviorProposalDecision,
	): Promise<BehaviorProposalRecord>;
	activateProposal(
		key: BehaviorProposalKey,
		expectedRevision: string,
		activation: Pick<BehaviorProposalDecision, "gameTime" | "recordedAt">,
		maxVersions: number,
	): Promise<BehaviorActivationResult>;
	getRegistry(key: BehaviorRegistryKey): Promise<BehaviorRegistryState | undefined>;
	getVersion(key: BehaviorVersionKey): Promise<BehaviorSkillVersion | undefined>;
	transitionRegistry(
		key: BehaviorRegistryKey,
		expectedRevision: string,
		targetVersion: string | undefined,
		transition: Pick<BehaviorProposalDecision, "gameTime" | "recordedAt">,
	): Promise<{ readonly registry: BehaviorRegistryState; readonly change: BehaviorRegistryChange }>;
	listActive(request: BehaviorActiveListRequest): Promise<BehaviorActivePage>;
	listProposals(request: BehaviorProposalListRequest): Promise<BehaviorProposalPage>;
	listChanges(request: BehaviorChangeListRequest): Promise<BehaviorChangePage>;
	purgeProposals(partition: BehaviorPartition, beforeRecordedAt: string, limit: number): Promise<readonly bigint[]>;
	purgeInactiveVersions(key: BehaviorRegistryKey, beforeRecordedAt: string, limit: number): Promise<readonly bigint[]>;
	purgeChanges(key: BehaviorRegistryKey, beforeRecordedAt: string, limit: number): Promise<readonly bigint[]>;
}

export interface BehaviorPolicyRequest {
	readonly partition: BehaviorPartition;
	readonly scope: BehaviorRegistryScope;
	readonly signal?: AbortSignal;
}

export interface BehaviorPolicySource {
	resolve(request: BehaviorPolicyRequest): BehaviorEvolutionMode | Promise<BehaviorEvolutionMode>;
}

export interface BehaviorToolCatalogSource {
	list(request: BehaviorPolicyRequest): readonly BehaviorToolDescriptor[] | Promise<readonly BehaviorToolDescriptor[]>;
}

export interface BehaviorEvidenceValidationRequest {
	readonly proposal: BehaviorProposalRecord;
	readonly evidence: BehaviorEvidence;
	readonly signal?: AbortSignal;
}

export type BehaviorEvidenceValidationResult =
	| { readonly accepted: true }
	| { readonly accepted: false; readonly decisionCode: string };

export interface BehaviorEvidenceVerifier {
	validate(
		request: BehaviorEvidenceValidationRequest,
	): BehaviorEvidenceValidationResult | Promise<BehaviorEvidenceValidationResult>;
}

export interface BehaviorSandboxValidationRequest {
	readonly proposal: BehaviorProposalRecord;
	readonly tools: readonly BehaviorToolDescriptor[];
	readonly signal?: AbortSignal;
}

export interface BehaviorSandboxValidationResult {
	readonly accepted: boolean;
	readonly decisionCode: string;
}

export interface BehaviorValidationSandbox {
	validate(
		request: BehaviorSandboxValidationRequest,
	): BehaviorSandboxValidationResult | Promise<BehaviorSandboxValidationResult>;
}

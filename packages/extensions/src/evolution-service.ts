import {
	BehaviorEvolutionDisabledError,
	BehaviorEvolutionValidationError,
	BehaviorProposalNotFoundError,
	BehaviorProposalRevisionConflictError,
	BehaviorRegistryNotFoundError,
	BehaviorToolUnavailableError,
	BehaviorVersionNotFoundError,
} from "./evolution-errors.js";
import type {
	BehaviorActivationResult,
	BehaviorActivePage,
	BehaviorActiveViewRequest,
	BehaviorChangeListRequest,
	BehaviorChangePage,
	BehaviorEvidenceValidationResult,
	BehaviorEvidenceVerifier,
	BehaviorEvolutionMode,
	BehaviorLimits,
	BehaviorPartition,
	BehaviorPolicySource,
	BehaviorProposalActivationRequest,
	BehaviorProposalCreateRequest,
	BehaviorProposalKey,
	BehaviorProposalListRequest,
	BehaviorProposalPage,
	BehaviorProposalRecord,
	BehaviorProposalReviewRequest,
	BehaviorProposalReviewResult,
	BehaviorRegistryChange,
	BehaviorRegistryKey,
	BehaviorRegistryScope,
	BehaviorRegistryState,
	BehaviorRegistryTransitionRequest,
	BehaviorSkillVersion,
	BehaviorStore,
	BehaviorToolCatalogSource,
	BehaviorToolDescriptor,
	BehaviorValidationSandbox,
	BehaviorVersionKey,
} from "./evolution-types.js";
import {
	assertToolsAvailable,
	behaviorContentFingerprint,
	compareBehaviorDecimals,
	normalizeBehaviorPartition,
	normalizeBehaviorScope,
	normalizeBehaviorScopes,
	normalizeBehaviorStatusFilter,
	normalizeReflection,
	normalizeStepDrafts,
	normalizeToolCatalog,
	validateBehaviorCursor,
	validateBehaviorLimits,
	validateBehaviorPageLimit,
	validateBehaviorProposalKey,
	validateBehaviorRegistryKey,
	validateBehaviorRevision,
	validateBehaviorTime,
	validateBehaviorTimestamp,
	validateBehaviorVersion,
	validateDecisionCode,
	validateDescription,
	validateSkillName,
} from "./evolution-validation.js";

export interface BehaviorEvolutionServiceOptions {
	readonly store: BehaviorStore;
	readonly limits: BehaviorLimits;
	readonly policy: BehaviorPolicySource;
	readonly tools: BehaviorToolCatalogSource;
	readonly evidence: BehaviorEvidenceVerifier;
	readonly sandbox: BehaviorValidationSandbox;
}

export class BehaviorEvolutionService {
	readonly limits: BehaviorLimits;
	private readonly store: BehaviorStore;
	private readonly policy: BehaviorPolicySource;
	private readonly tools: BehaviorToolCatalogSource;
	private readonly evidence: BehaviorEvidenceVerifier;
	private readonly sandbox: BehaviorValidationSandbox;

	constructor(options: BehaviorEvolutionServiceOptions) {
		validateBehaviorLimits(options.limits);
		if (
			!options.store ||
			typeof options.store.createProposal !== "function" ||
			!options.policy ||
			typeof options.policy.resolve !== "function" ||
			!options.tools ||
			typeof options.tools.list !== "function" ||
			!options.evidence ||
			typeof options.evidence.validate !== "function" ||
			!options.sandbox ||
			typeof options.sandbox.validate !== "function"
		) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_service_configuration");
		}
		this.store = options.store;
		this.limits = Object.freeze({ ...options.limits });
		this.policy = options.policy;
		this.tools = options.tools;
		this.evidence = options.evidence;
		this.sandbox = options.sandbox;
	}

	async mode(
		partition: BehaviorPartition,
		scope: BehaviorRegistryScope,
		signal?: AbortSignal,
	): Promise<BehaviorEvolutionMode> {
		const normalizedPartition = normalizeBehaviorPartition(partition);
		const normalizedScope = normalizeBehaviorScope(scope);
		signal?.throwIfAborted();
		const mode = await this.policy.resolve({
			partition: normalizedPartition,
			scope: normalizedScope,
			...(signal === undefined ? {} : { signal }),
		});
		signal?.throwIfAborted();
		if (mode !== "off" && mode !== "conservative" && mode !== "aggressive") {
			throw new BehaviorEvolutionValidationError("invalid_behavior_evolution_mode");
		}
		return mode;
	}

	async createProposal(request: BehaviorProposalCreateRequest): Promise<BehaviorProposalRecord> {
		const partition = normalizeBehaviorPartition(request.partition);
		const scope = normalizeBehaviorScope(request.scope);
		validateBehaviorProposalKey({ ...partition, proposalId: request.proposalId });
		validateSkillName(request.skillName, this.limits);
		validateDescription(request.description, this.limits);
		const reflection = normalizeReflection(request.reflection, this.limits);
		const gameTime = validateBehaviorTime(request.gameTime, partition);
		validateBehaviorTimestamp(request.recordedAt);
		request.signal?.throwIfAborted();
		await this.requireEnabled(partition, scope, request.signal);
		const tools = await this.readTools(partition, scope, request.signal);
		const steps = normalizeStepDrafts(request.steps, this.limits, tools);
		const registry = await this.store.getRegistry({ ...partition, scope, skillName: request.skillName });
		request.signal?.throwIfAborted();
		if (
			registry !== undefined &&
			(compareBehaviorDecimals(gameTime.tick, registry.gameTime.tick) < 0 || request.recordedAt < registry.updatedAt)
		) {
			throw new BehaviorEvolutionValidationError("behavior_time_regression");
		}
		const baseVersion = registry?.activeVersion;
		const contentFingerprint = behaviorContentFingerprint({
			partition,
			scope,
			skillName: request.skillName,
			description: request.description,
			reflection,
			steps,
			...(baseVersion === undefined ? {} : { baseVersion }),
		});
		return await this.store.createProposal(
			{
				proposalId: request.proposalId,
				partition,
				scope,
				skillName: request.skillName,
				description: request.description,
				reflection,
				steps,
				...(baseVersion === undefined ? {} : { baseVersion }),
				contentFingerprint,
				gameTime,
				recordedAt: request.recordedAt,
			},
			this.limits.maxOpenProposalsPerOwnerGeneration,
		);
	}

	async reviewProposal(request: BehaviorProposalReviewRequest): Promise<BehaviorProposalReviewResult> {
		validateBehaviorProposalKey(request);
		validateBehaviorRevision(request.expectedRevision);
		validateBehaviorTimestamp(request.recordedAt);
		request.signal?.throwIfAborted();
		const proposal = await this.requireProposal(request);
		if (proposal.revision !== request.expectedRevision) throw new BehaviorProposalRevisionConflictError();
		if (proposal.status !== "pending") {
			throw new BehaviorEvolutionValidationError("behavior_proposal_already_decided");
		}
		const gameTime = validateBehaviorTime(request.gameTime, proposal.partition);
		assertNoBehaviorTimeRegression(proposal, gameTime, request.recordedAt);
		const mode = await this.requireEnabled(proposal.partition, proposal.scope, request.signal);
		const tools = await this.readTools(proposal.partition, proposal.scope, request.signal);
		this.assertProposalTools(proposal, tools);

		for (const evidence of proposal.reflection.evidence) {
			request.signal?.throwIfAborted();
			const result = await this.evidence.validate({
				proposal: structuredClone(proposal),
				evidence: { ...evidence },
				...(request.signal === undefined ? {} : { signal: request.signal }),
			});
			const normalized = this.normalizeEvidenceResult(result);
			if (!normalized.accepted) {
				validateDecisionCode(normalized.decisionCode, this.limits);
				const rejected = await this.store.decideProposal(request, request.expectedRevision, {
					status: "rejected",
					decisionCode: normalized.decisionCode,
					gameTime,
					recordedAt: request.recordedAt,
				});
				return { proposal: rejected };
			}
		}

		request.signal?.throwIfAborted();
		const validation = this.normalizeSandboxResult(
			await this.sandbox.validate({
				proposal: structuredClone(proposal),
				tools: structuredClone(tools),
				...(request.signal === undefined ? {} : { signal: request.signal }),
			}),
		);
		request.signal?.throwIfAborted();
		validateDecisionCode(validation.decisionCode, this.limits);
		const decided = await this.store.decideProposal(request, request.expectedRevision, {
			status: validation.accepted ? "validated" : "rejected",
			decisionCode: validation.decisionCode,
			gameTime,
			recordedAt: request.recordedAt,
		});
		if (!validation.accepted || mode !== "aggressive") return { proposal: decided };

		const activation = await this.store.activateProposal(
			request,
			decided.revision,
			{ gameTime, recordedAt: request.recordedAt },
			this.limits.maxVersionsPerSkill,
		);
		return { proposal: activation.proposal, activation };
	}

	async activateProposal(request: BehaviorProposalActivationRequest): Promise<BehaviorActivationResult> {
		validateBehaviorProposalKey(request);
		validateBehaviorRevision(request.expectedRevision);
		validateBehaviorTimestamp(request.recordedAt);
		request.signal?.throwIfAborted();
		const proposal = await this.requireProposal(request);
		if (proposal.revision !== request.expectedRevision) throw new BehaviorProposalRevisionConflictError();
		if (proposal.status !== "validated") {
			throw new BehaviorEvolutionValidationError("behavior_proposal_not_validated");
		}
		const gameTime = validateBehaviorTime(request.gameTime, proposal.partition);
		assertNoBehaviorTimeRegression(proposal, gameTime, request.recordedAt);
		await this.requireEnabled(proposal.partition, proposal.scope, request.signal);
		const tools = await this.readTools(proposal.partition, proposal.scope, request.signal);
		this.assertProposalTools(proposal, tools);
		request.signal?.throwIfAborted();
		return await this.store.activateProposal(
			request,
			request.expectedRevision,
			{ gameTime, recordedAt: request.recordedAt },
			this.limits.maxVersionsPerSkill,
		);
	}

	async transitionRegistry(
		request: BehaviorRegistryTransitionRequest,
	): Promise<{ readonly registry: BehaviorRegistryState; readonly change: BehaviorRegistryChange }> {
		validateBehaviorRegistryKey(request, this.limits);
		validateBehaviorRevision(request.expectedRevision);
		if (request.targetVersion !== undefined) validateBehaviorVersion(request.targetVersion);
		validateBehaviorTimestamp(request.recordedAt);
		const gameTime = validateBehaviorTime(request.gameTime, request);
		request.signal?.throwIfAborted();
		await this.requireEnabled(request, request.scope, request.signal);
		const registry = await this.store.getRegistry(request);
		if (registry === undefined) throw new BehaviorRegistryNotFoundError();
		assertNoBehaviorTimeRegression(registry, gameTime, request.recordedAt);
		if (request.targetVersion !== undefined) {
			const version = await this.store.getVersion({ ...request, version: request.targetVersion });
			if (version === undefined) throw new BehaviorVersionNotFoundError();
			const tools = await this.readTools(request, request.scope, request.signal);
			this.assertProposalTools(version, tools);
		}
		request.signal?.throwIfAborted();
		return await this.store.transitionRegistry(request, request.expectedRevision, request.targetVersion, {
			gameTime,
			recordedAt: request.recordedAt,
		});
	}

	async getProposal(key: BehaviorProposalKey): Promise<BehaviorProposalRecord | undefined> {
		validateBehaviorProposalKey(key);
		return await this.store.getProposal(key);
	}

	async getRegistry(key: BehaviorRegistryKey): Promise<BehaviorRegistryState | undefined> {
		validateBehaviorRegistryKey(key, this.limits);
		return await this.store.getRegistry(key);
	}

	async getVersion(key: BehaviorVersionKey): Promise<BehaviorSkillVersion | undefined> {
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorVersion(key.version);
		return await this.store.getVersion(key);
	}

	async listActive(request: BehaviorActiveViewRequest): Promise<BehaviorActivePage> {
		const partition = normalizeBehaviorPartition(request);
		const scopes = normalizeBehaviorScopes(request.scopes, this.limits.maxScopesPerView);
		validateBehaviorCursor(request.afterVersionKey);
		validateBehaviorPageLimit(request.limit, this.limits.maxListPageSize, "invalid_behavior_page_limit");
		request.signal?.throwIfAborted();
		const page = await this.store.listActive({
			...partition,
			scopes,
			...(request.afterVersionKey === undefined ? {} : { afterVersionKey: request.afterVersionKey }),
			limit: request.limit,
		});
		const records = [];
		for (const record of page.records) {
			request.signal?.throwIfAborted();
			if ((await this.mode(record.registry, record.registry.scope, request.signal)) === "off") continue;
			const tools = await this.readTools(record.registry, record.registry.scope, request.signal);
			try {
				this.assertProposalTools(record.version, tools);
			} catch (error) {
				if (isToolUnavailable(error)) continue;
				throw error;
			}
			records.push(record);
		}
		return {
			records,
			...(page.nextAfterVersionKey === undefined ? {} : { nextAfterVersionKey: page.nextAfterVersionKey }),
		};
	}

	async listProposals(request: BehaviorProposalListRequest): Promise<BehaviorProposalPage> {
		const partition = normalizeBehaviorPartition(request);
		const statuses = normalizeBehaviorStatusFilter(request.statuses);
		validateBehaviorCursor(request.afterProposalKey);
		validateBehaviorPageLimit(request.limit, this.limits.maxListPageSize, "invalid_behavior_page_limit");
		return await this.store.listProposals({
			...partition,
			...(statuses === undefined ? {} : { statuses }),
			...(request.afterProposalKey === undefined ? {} : { afterProposalKey: request.afterProposalKey }),
			limit: request.limit,
		});
	}

	async listChanges(request: BehaviorChangeListRequest): Promise<BehaviorChangePage> {
		validateBehaviorRegistryKey(request, this.limits);
		validateBehaviorCursor(request.afterChangeKey);
		validateBehaviorPageLimit(request.limit, this.limits.maxListPageSize, "invalid_behavior_page_limit");
		return await this.store.listChanges(request);
	}

	async purgeProposals(
		partition: BehaviorPartition,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		const normalized = normalizeBehaviorPartition(partition);
		validateBehaviorTimestamp(beforeRecordedAt);
		validateBehaviorPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_behavior_purge_limit");
		return await this.store.purgeProposals(normalized, beforeRecordedAt, limit);
	}

	async purgeInactiveVersions(
		key: BehaviorRegistryKey,
		beforeRecordedAt: string,
		limit: number,
	): Promise<readonly bigint[]> {
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorTimestamp(beforeRecordedAt);
		validateBehaviorPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_behavior_purge_limit");
		return await this.store.purgeInactiveVersions(key, beforeRecordedAt, limit);
	}

	async purgeChanges(key: BehaviorRegistryKey, beforeRecordedAt: string, limit: number): Promise<readonly bigint[]> {
		validateBehaviorRegistryKey(key, this.limits);
		validateBehaviorTimestamp(beforeRecordedAt);
		validateBehaviorPageLimit(limit, this.limits.maxPurgeBatchSize, "invalid_behavior_purge_limit");
		return await this.store.purgeChanges(key, beforeRecordedAt, limit);
	}

	private async requireProposal(key: BehaviorProposalKey): Promise<BehaviorProposalRecord> {
		const proposal = await this.store.getProposal(key);
		if (proposal === undefined) throw new BehaviorProposalNotFoundError();
		return proposal;
	}

	private async requireEnabled(
		partition: BehaviorPartition,
		scope: BehaviorRegistryScope,
		signal?: AbortSignal,
	): Promise<Exclude<BehaviorEvolutionMode, "off">> {
		const mode = await this.mode(partition, scope, signal);
		if (mode === "off") throw new BehaviorEvolutionDisabledError();
		return mode;
	}

	private async readTools(
		partition: BehaviorPartition,
		scope: BehaviorRegistryScope,
		signal?: AbortSignal,
	): Promise<readonly BehaviorToolDescriptor[]> {
		signal?.throwIfAborted();
		const tools = await this.tools.list({
			partition,
			scope,
			...(signal === undefined ? {} : { signal }),
		});
		signal?.throwIfAborted();
		return [...normalizeToolCatalog(tools, this.limits).values()];
	}

	private assertProposalTools(
		proposal: Pick<BehaviorProposalRecord | BehaviorSkillVersion, "steps">,
		tools: readonly BehaviorToolDescriptor[],
	): void {
		try {
			assertToolsAvailable(proposal.steps, tools, this.limits);
		} catch (error) {
			if (isToolUnavailable(error)) throw new BehaviorToolUnavailableError();
			throw error;
		}
	}

	private normalizeEvidenceResult(value: BehaviorEvidenceValidationResult): BehaviorEvidenceValidationResult {
		if (typeof value !== "object" || value === null || Array.isArray(value)) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_evidence_result");
		}
		if (value.accepted === true && Object.keys(value).length === 1) return { accepted: true };
		if (value.accepted === false && typeof value.decisionCode === "string" && Object.keys(value).length === 2) {
			return { accepted: false, decisionCode: value.decisionCode };
		}
		throw new BehaviorEvolutionValidationError("invalid_behavior_evidence_result");
	}

	private normalizeSandboxResult(value: unknown): { readonly accepted: boolean; readonly decisionCode: string } {
		if (
			typeof value !== "object" ||
			value === null ||
			Array.isArray(value) ||
			typeof (value as { accepted?: unknown }).accepted !== "boolean" ||
			typeof (value as { decisionCode?: unknown }).decisionCode !== "string" ||
			Object.keys(value).some((key) => key !== "accepted" && key !== "decisionCode")
		) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_sandbox_result");
		}
		return value as { readonly accepted: boolean; readonly decisionCode: string };
	}
}

function isToolUnavailable(error: unknown): boolean {
	return (
		error instanceof BehaviorToolUnavailableError ||
		(error instanceof BehaviorEvolutionValidationError && error.code === "behavior_tool_not_available")
	);
}

function assertNoBehaviorTimeRegression(
	current: { readonly gameTime: { readonly tick: string }; readonly updatedAt: string },
	nextGameTime: { readonly tick: string },
	nextRecordedAt: string,
): void {
	if (compareBehaviorDecimals(nextGameTime.tick, current.gameTime.tick) < 0 || nextRecordedAt < current.updatedAt) {
		throw new BehaviorEvolutionValidationError("behavior_time_regression");
	}
}

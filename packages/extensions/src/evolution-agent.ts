import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import type { GameContextContribution, GameContextSource, NormalizedGameInput } from "@opengameagent/agent";
import { assertOpaqueIdentifier, type GameTime, normalizeJsonValue, type RunCoordinate } from "@opengameagent/protocol";
import { type Static, Type } from "typebox";
import { BehaviorEvolutionValidationError } from "./evolution-errors.js";
import type { BehaviorEvolutionService } from "./evolution-service.js";
import type { BehaviorPartition, BehaviorProposalRecord, BehaviorRegistryScope } from "./evolution-types.js";

const EvidenceSchema = Type.Object(
	{
		kind: Type.String(),
		referenceId: Type.String(),
	},
	{ additionalProperties: false },
);

const StepSchema = Type.Object(
	{
		instruction: Type.String(),
		toolName: Type.String(),
	},
	{ additionalProperties: false },
);

const ProposeBehaviorSchema = Type.Object(
	{
		skillName: Type.String(),
		description: Type.String(),
		reflection: Type.Object(
			{
				summary: Type.String(),
				evidence: Type.Array(EvidenceSchema),
			},
			{ additionalProperties: false },
		),
		steps: Type.Array(StepSchema),
	},
	{ additionalProperties: false },
);

export interface BehaviorEvolutionToolDetails {
	readonly proposalId: string;
	readonly revision: string;
	readonly status: BehaviorProposalRecord["status"];
}

export interface BehaviorEvolutionToolsOptions {
	readonly service: BehaviorEvolutionService;
	readonly readPartition: () => BehaviorPartition;
	readonly readScope: () => BehaviorRegistryScope;
	readonly readGameTime: () => GameTime;
	readonly recordedAt: () => string;
	readonly proposalId: (toolCallId: string) => string;
	readonly stepId: (toolCallId: string, index: number) => string;
	readonly signal?: AbortSignal;
}

export async function createBehaviorEvolutionTools(
	options: BehaviorEvolutionToolsOptions,
): Promise<readonly AgentTool[]> {
	const partition = options.readPartition();
	const scope = options.readScope();
	if ((await options.service.mode(partition, scope, options.signal)) === "off") return [];
	return [createProposalTool(options)];
}

function createProposalTool(
	options: BehaviorEvolutionToolsOptions,
): AgentTool<typeof ProposeBehaviorSchema, BehaviorEvolutionToolDetails> {
	return {
		name: "behavior_skill_propose",
		label: "Propose behavior skill",
		description:
			"Propose an evidence-backed, reviewable behavior Skill composed only from currently available game tools. This does not activate the Skill or create new authority.",
		parameters: ProposeBehaviorSchema,
		executionMode: "sequential",
		execute: async (toolCallId, parameters, signal) => {
			const proposal = await options.service.createProposal({
				proposalId: options.proposalId(toolCallId),
				partition: options.readPartition(),
				scope: options.readScope(),
				skillName: parameters.skillName,
				description: parameters.description,
				reflection: parameters.reflection,
				steps: parameters.steps.map((step, index) => ({
					stepId: options.stepId(toolCallId, index),
					instruction: step.instruction,
					toolName: step.toolName,
				})),
				gameTime: options.readGameTime(),
				recordedAt: options.recordedAt(),
				...(signal === undefined ? {} : { signal }),
			});
			return proposalResult(proposal);
		},
	};
}

function proposalResult(proposal: BehaviorProposalRecord): AgentToolResult<BehaviorEvolutionToolDetails> {
	const details = {
		proposalId: proposal.proposalId,
		revision: proposal.revision,
		status: proposal.status,
	} satisfies BehaviorEvolutionToolDetails;
	return {
		content: [{ type: "text", text: JSON.stringify(details) }],
		details,
	};
}

export interface BehaviorContextView {
	readonly partition: BehaviorPartition;
	/** Earlier scopes have precedence over later scopes for duplicate Skill names. */
	readonly scopes: readonly BehaviorRegistryScope[];
	readonly limit: number;
}

export type BehaviorContextViewResolver = (
	input: Readonly<NormalizedGameInput>,
	signal: AbortSignal,
) => BehaviorContextView | Promise<BehaviorContextView>;

export interface BehaviorContextSourceOptions {
	readonly sourceId: string;
	readonly service: BehaviorEvolutionService;
	readonly resolveView: BehaviorContextViewResolver;
}

export class BehaviorContextSource implements GameContextSource {
	readonly provenanceKind = "extension" as const;
	readonly sourceId: string;
	private readonly service: BehaviorEvolutionService;
	private readonly resolveView: BehaviorContextViewResolver;

	constructor(options: BehaviorContextSourceOptions) {
		assertOpaqueIdentifier(options.sourceId);
		if (
			!options.service ||
			typeof options.service.listActive !== "function" ||
			typeof options.resolveView !== "function"
		) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_context_source_configuration");
		}
		this.sourceId = options.sourceId;
		this.service = options.service;
		this.resolveView = options.resolveView;
	}

	async load(
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
		_coordinate: Readonly<RunCoordinate>,
	): Promise<GameContextContribution> {
		signal.throwIfAborted();
		const view = await this.resolveView(input, signal);
		for (const scope of view.scopes) {
			if (scope.kind === "actor" && scope.actorId !== input.actorId) {
				throw new BehaviorEvolutionValidationError("behavior_actor_scope_mismatch");
			}
		}
		const page = await this.service.listActive({ ...view.partition, scopes: view.scopes, limit: view.limit, signal });
		signal.throwIfAborted();
		return {
			references: page.records.map(({ registry, version }) => ({
				referenceId: version.versionKey.toString(),
				revision: registry.revision,
				content: normalizeJsonValue({
					kind: "behavior-skill",
					name: version.skillName,
					description: version.description,
					version: version.version,
					steps: version.steps.map((step) => ({
						stepId: step.stepId,
						instruction: step.instruction,
						toolName: step.toolName,
					})),
				}),
			})),
		};
	}
}

export type ProposeBehaviorToolParameters = Static<typeof ProposeBehaviorSchema>;

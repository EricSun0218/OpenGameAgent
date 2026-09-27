import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import type { GameTime } from "@opengameagent/protocol";
import { type Static, Type } from "typebox";
import type { GoalService } from "./goal-service.js";
import type { GoalPartition, GoalPlanStep, GoalRecord, GoalStatus } from "./goal-types.js";
import { serializeGoalModelResult, serializeGoalNotFoundResult } from "./goal-validation.js";

const CreateStepSchema = Type.Object(
	{
		description: Type.String(),
		status: Type.Union([Type.Literal("pending"), Type.Literal("in_progress")]),
	},
	{ additionalProperties: false },
);

const EvidenceSchema = Type.Object(
	{
		kind: Type.String(),
		referenceId: Type.String(),
	},
	{ additionalProperties: false },
);

const UpdateStepSchema = Type.Object(
	{
		stepId: Type.String(),
		description: Type.String(),
		status: Type.Union([Type.Literal("pending"), Type.Literal("in_progress"), Type.Literal("completed")]),
		evidence: Type.Array(EvidenceSchema),
	},
	{ additionalProperties: false },
);

const GoalStatusSchema = Type.Union([
	Type.Literal("active"),
	Type.Literal("paused"),
	Type.Literal("achieved"),
	Type.Literal("failed"),
	Type.Literal("cancelled"),
]);

const CreateGoalSchema = Type.Object(
	{
		objective: Type.String(),
		plan: Type.Array(CreateStepSchema),
	},
	{ additionalProperties: false },
);

const UpdateGoalSchema = Type.Object(
	{
		goalId: Type.String(),
		expectedRevision: Type.String(),
		plan: Type.Optional(Type.Array(UpdateStepSchema)),
		status: Type.Optional(GoalStatusSchema),
	},
	{ additionalProperties: false },
);

const ReadGoalSchema = Type.Object({ goalId: Type.String() }, { additionalProperties: false });

export interface GoalToolDetails {
	readonly goalId: string;
	readonly revision: string;
	readonly status: GoalStatus;
}

export interface GoalToolsOptions {
	readonly service: GoalService;
	readonly readPartition: () => GoalPartition;
	readonly readGameTime: () => GameTime;
	readonly recordedAt: () => string;
	readonly goalId: (toolCallId: string) => string;
	readonly stepId: (toolCallId: string, index: number) => string;
	readonly includeModelUpdateTool?: boolean;
}

export function createGoalTools(options: GoalToolsOptions): readonly AgentTool[] {
	return options.includeModelUpdateTool === false
		? [createGoalTool(options), readGoalTool(options)]
		: [createGoalTool(options), updateGoalTool(options), readGoalTool(options)];
}

function createGoalTool(options: GoalToolsOptions): AgentTool<typeof CreateGoalSchema, GoalToolDetails> {
	return {
		name: "goal_create",
		label: "Create goal",
		description: "Create an optional persistent goal and ordered plan. Short work does not require a goal.",
		parameters: CreateGoalSchema,
		executionMode: "sequential",
		execute: async (toolCallId, parameters, signal) => {
			const plan: GoalPlanStep[] = parameters.plan.map((step, index) => ({
				stepId: options.stepId(toolCallId, index),
				description: step.description,
				status: step.status,
				evidence: [],
			}));
			const record = await options.service.create({
				goalId: options.goalId(toolCallId),
				partition: options.readPartition(),
				objective: parameters.objective,
				plan,
				gameTime: options.readGameTime(),
				recordedAt: options.recordedAt(),
				...(signal === undefined ? {} : { signal }),
			});
			return goalResult(record, options.service.limits);
		},
	};
}

function updateGoalTool(options: GoalToolsOptions): AgentTool<typeof UpdateGoalSchema, GoalToolDetails> {
	return {
		name: "goal_update",
		label: "Update goal",
		description:
			"Update an existing goal with optimistic revision control. A pending step must become in_progress before completion, and completed steps require host-verifiable evidence.",
		parameters: UpdateGoalSchema,
		executionMode: "sequential",
		execute: async (_toolCallId, parameters, signal) => {
			const partition = options.readPartition();
			const record = await options.service.update({
				ownerId: partition.ownerId,
				timelineId: partition.timelineId,
				generation: partition.generation,
				...(partition.sessionId === undefined ? {} : { sessionId: partition.sessionId }),
				...(partition.actorId === undefined ? {} : { actorId: partition.actorId }),
				goalId: parameters.goalId,
				expectedRevision: parameters.expectedRevision,
				...(parameters.plan === undefined ? {} : { plan: parameters.plan }),
				...(parameters.status === undefined ? {} : { status: parameters.status }),
				gameTime: options.readGameTime(),
				recordedAt: options.recordedAt(),
				...(signal === undefined ? {} : { signal }),
			});
			return goalResult(record, options.service.limits);
		},
	};
}

function readGoalTool(options: GoalToolsOptions): AgentTool<typeof ReadGoalSchema, GoalToolDetails | undefined> {
	return {
		name: "goal_read",
		label: "Read goal",
		description: "Read one persistent goal and its ordered plan in the current owner timeline.",
		parameters: ReadGoalSchema,
		executionMode: "parallel",
		execute: async (_toolCallId, parameters) => {
			const partition = options.readPartition();
			const record = await options.service.get({
				ownerId: partition.ownerId,
				timelineId: partition.timelineId,
				generation: partition.generation,
				...(partition.sessionId === undefined ? {} : { sessionId: partition.sessionId }),
				...(partition.actorId === undefined ? {} : { actorId: partition.actorId }),
				goalId: parameters.goalId,
			});
			if (record === undefined) {
				return {
					content: [{ type: "text", text: serializeGoalNotFoundResult(options.service.limits) }],
					details: undefined,
				};
			}
			return goalResult(record, options.service.limits);
		},
	};
}

function goalResult(record: GoalRecord, limits: GoalService["limits"]): AgentToolResult<GoalToolDetails> {
	return {
		content: [
			{
				type: "text",
				text: serializeGoalModelResult(record, limits),
			},
		],
		details: { goalId: record.goalId, revision: record.revision, status: record.status },
	};
}

export type CreateGoalToolParameters = Static<typeof CreateGoalSchema>;
export type UpdateGoalToolParameters = Static<typeof UpdateGoalSchema>;

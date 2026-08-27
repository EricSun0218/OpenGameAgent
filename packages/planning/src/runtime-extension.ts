import type { GameInput, GameTool, JsonObject, JsonValue } from "@opengameagent/protocol";
import type { GameContextProvider, GameToolProvider } from "@opengameagent/runtime";
import type {
	GameGoal,
	GameGoalMutation,
	GameTaskPlan,
	GameTaskPlanEvidenceValidator,
	GameTaskStep,
	SqliteGamePlanningStore,
} from "./planning.js";

export type GamePlanningResource = "goal" | "task-plan";

export interface GamePlanningAuthorizationContext {
	input: GameInput;
	resource: GamePlanningResource;
	action: string;
	id?: string;
	arguments: JsonObject;
}

export interface GamePlanningExtensionOptions {
	store: SqliteGamePlanningStore;
	evidenceValidator: GameTaskPlanEvidenceValidator;
	authorize?: (context: GamePlanningAuthorizationContext, signal: AbortSignal) => Promise<boolean> | boolean;
	includeContext?: boolean;
	includeGoalTool?: boolean;
	includeTaskPlanTool?: boolean;
	contextName?: string;
	contextPriority?: number;
}

export interface GamePlanningExtensionResources {
	contextProvider?: GameContextProvider;
	toolProvider: GameToolProvider;
}

const anyJsonSchema: JsonObject = {
	anyOf: [
		{ type: "null" },
		{ type: "string" },
		{ type: "number" },
		{ type: "boolean" },
		{ type: "array", items: {}, maxItems: 256 },
		{ type: "object", additionalProperties: true },
	],
};

function asJson(value: unknown): JsonValue {
	return JSON.parse(JSON.stringify(value)) as JsonValue;
}

function argument(arguments_: JsonObject, name: string): JsonValue | undefined {
	return arguments_[name];
}

function requiredString(arguments_: JsonObject, name: string): string {
	const value = argument(arguments_, name);
	if (typeof value !== "string" || value.length < 1) throw new TypeError(`${name} must be a non-empty string.`);
	return value;
}

function requiredInteger(arguments_: JsonObject, name: string): number {
	const value = argument(arguments_, name);
	if (typeof value !== "number" || !Number.isInteger(value) || value < 1)
		throw new TypeError(`${name} must be a positive integer.`);
	return value;
}

function optionalString(arguments_: JsonObject, name: string): string | undefined {
	const value = argument(arguments_, name);
	if (value === undefined) return undefined;
	if (typeof value !== "string") throw new TypeError(`${name} must be a string.`);
	return value;
}

function requiredValue(arguments_: JsonObject, name: string): JsonValue {
	const value = argument(arguments_, name);
	if (value === undefined) throw new TypeError(`${name} is required.`);
	return value;
}

function taskSteps(arguments_: JsonObject, name: string): Omit<GameTaskStep, "status" | "evidence">[] {
	const value = argument(arguments_, name);
	if (!Array.isArray(value) || value.length < 1 || value.length > 512)
		throw new TypeError(`${name} must be a bounded non-empty array.`);
	return value.map((item) => {
		if (item === null || Array.isArray(item) || typeof item !== "object")
			throw new TypeError(`${name} contains an invalid step.`);
		const id = requiredString(item, "id");
		const data = requiredValue(item, "data");
		const label = optionalString(item, "label");
		return { id, data, ...(label === undefined ? {} : { label }) };
	});
}

async function authorize(
	input: GameInput,
	resource: GamePlanningResource,
	action: string,
	id: string | undefined,
	arguments_: JsonObject,
	options: GamePlanningExtensionOptions,
	signal: AbortSignal,
): Promise<void> {
	if (
		(await options.authorize?.(
			{ input, resource, action, ...(id === undefined ? {} : { id }), arguments: arguments_ },
			signal,
		)) === false
	)
		throw new Error("Planning operation was rejected by the host.");
}

function goalTool(input: GameInput, options: GamePlanningExtensionOptions): GameTool {
	return {
		definition: {
			name: "manage_game_goal",
			label: "Manage persistent game goal",
			description: "Create, inspect, pause, resume, wait, progress, complete, fail, or cancel a persistent game goal.",
			parameters: {
				type: "object",
				properties: {
					action: {
						type: "string",
						enum: ["list", "create", "progress", "wait", "pause", "resume", "complete", "fail", "cancel"],
					},
					id: { type: "string", minLength: 1, maxLength: 192 },
					expectedRevision: { type: "integer", minimum: 1 },
					label: { type: "string", maxLength: 1024 },
					data: anyJsonSchema,
					progress: anyJsonSchema,
					wakeAt: {
						type: "object",
						properties: {
							tick: { type: "number" },
							calendar: { type: "string", maxLength: 128 },
							label: { type: "string", maxLength: 512 },
						},
						required: ["tick"],
						additionalProperties: false,
					},
					reason: { type: "string", minLength: 1, maxLength: 4096 },
				},
				required: ["action"],
				additionalProperties: false,
			},
		},
		async execute(call, signal) {
			const action = requiredString(call.arguments, "action");
			const id = action === "list" ? undefined : requiredString(call.arguments, "id");
			await authorize(input, "goal", action, id, call.arguments, options, signal);
			if (action === "list")
				return { content: [{ type: "json", value: asJson(await options.store.listGoals(input.session, signal)) }] };
			if (action === "create") {
				const goal = await options.store.createGoal(
					input.session,
					id as string,
					requiredValue(call.arguments, "data"),
					optionalString(call.arguments, "label"),
					signal,
				);
				return { content: [{ type: "json", value: asJson(goal) }] };
			}
			const expectedRevision = requiredInteger(call.arguments, "expectedRevision");
			let mutation: GameGoalMutation;
			switch (action) {
				case "progress":
					mutation = { action, progress: requiredValue(call.arguments, "progress") };
					break;
				case "wait": {
					const wakeAt = requiredValue(call.arguments, "wakeAt");
					if (
						wakeAt === null ||
						Array.isArray(wakeAt) ||
						typeof wakeAt !== "object" ||
						typeof wakeAt["tick"] !== "number"
					)
						throw new TypeError("wakeAt must be a game moment.");
					const progress = argument(call.arguments, "progress");
					mutation = {
						action,
						wakeAt: wakeAt as GameGoal["wakeAt"] & object,
						...(progress === undefined ? {} : { progress }),
					};
					break;
				}
				case "pause": {
					const reason = optionalString(call.arguments, "reason");
					mutation = { action, ...(reason === undefined ? {} : { reason }) };
					break;
				}
				case "resume":
					mutation = { action };
					break;
				case "complete": {
					const progress = argument(call.arguments, "progress");
					mutation = { action, ...(progress === undefined ? {} : { progress }) };
					break;
				}
				case "fail":
				case "cancel":
					mutation = { action, reason: requiredString(call.arguments, "reason") };
					break;
				default:
					throw new TypeError("Goal action is invalid.");
			}
			const goal = await options.store.mutateGoal(input.session, id as string, expectedRevision, mutation, signal);
			return { content: [{ type: "json", value: asJson(goal) }] };
		},
	};
}

function taskPlanTool(input: GameInput, options: GamePlanningExtensionOptions): GameTool {
	return {
		definition: {
			name: "manage_game_task_plan",
			label: "Manage persistent game task plan",
			description: "Create, inspect, advance, replan, pause, resume, fail, or cancel a persistent ordered task plan.",
			parameters: {
				type: "object",
				properties: {
					action: {
						type: "string",
						enum: ["list", "create", "advance", "replace_remaining", "pause", "resume", "fail", "cancel"],
					},
					id: { type: "string", minLength: 1, maxLength: 192 },
					expectedRevision: { type: "integer", minimum: 1 },
					label: { type: "string", maxLength: 1024 },
					data: anyJsonSchema,
					steps: {
						type: "array",
						minItems: 1,
						maxItems: 512,
						items: {
							type: "object",
							properties: {
								id: { type: "string", minLength: 1, maxLength: 192 },
								label: { type: "string", maxLength: 1024 },
								data: anyJsonSchema,
							},
							required: ["id", "data"],
							additionalProperties: false,
						},
					},
					evidence: anyJsonSchema,
					reason: { type: "string", minLength: 1, maxLength: 4096 },
				},
				required: ["action"],
				additionalProperties: false,
			},
		},
		async execute(call, signal) {
			const action = requiredString(call.arguments, "action");
			const id = action === "list" ? undefined : requiredString(call.arguments, "id");
			await authorize(input, "task-plan", action, id, call.arguments, options, signal);
			if (action === "list")
				return { content: [{ type: "json", value: asJson(await options.store.listPlans(input.session, signal)) }] };
			if (action === "create") {
				const plan = await options.store.createPlan(
					input.session,
					id as string,
					taskSteps(call.arguments, "steps"),
					requiredValue(call.arguments, "data"),
					optionalString(call.arguments, "label"),
					signal,
				);
				return { content: [{ type: "json", value: asJson(plan) }] };
			}
			const expectedRevision = requiredInteger(call.arguments, "expectedRevision");
			let plan: GameTaskPlan;
			switch (action) {
				case "advance":
					plan = await options.store.advancePlan(
						input.session,
						id as string,
						expectedRevision,
						input.id,
						requiredValue(call.arguments, "evidence"),
						options.evidenceValidator,
						signal,
					);
					break;
				case "replace_remaining":
					plan = await options.store.replaceRemaining(
						input.session,
						id as string,
						expectedRevision,
						taskSteps(call.arguments, "steps"),
						signal,
					);
					break;
				case "pause":
					plan = await options.store.pausePlan(
						input.session,
						id as string,
						expectedRevision,
						optionalString(call.arguments, "reason"),
						signal,
					);
					break;
				case "resume":
					plan = await options.store.resumePlan(input.session, id as string, expectedRevision, signal);
					break;
				case "fail":
				case "cancel":
					plan = await options.store.finishPlan(
						input.session,
						id as string,
						expectedRevision,
						action === "fail" ? "failed" : "cancelled",
						requiredString(call.arguments, "reason"),
						signal,
					);
					break;
				default:
					throw new TypeError("Task-plan action is invalid.");
			}
			return { content: [{ type: "json", value: asJson(plan) }] };
		},
	};
}

export function createGamePlanningExtension(options: GamePlanningExtensionOptions): GamePlanningExtensionResources {
	if (!options.store) throw new TypeError("A planning store is required.");
	if (!options.evidenceValidator) throw new TypeError("A host evidence validator is required.");
	const contextProvider: GameContextProvider | undefined =
		options.includeContext === false
			? undefined
			: {
					async provide(input, signal) {
						const [goals, plans] = await Promise.all([
							options.store.listGoals(input.session, signal),
							options.store.listPlans(input.session, signal),
						]);
						const activeGoals = goals.filter((goal) => !["completed", "failed", "cancelled"].includes(goal.status));
						const activePlans = plans.filter((plan) => !["completed", "failed", "cancelled"].includes(plan.status));
						if (activeGoals.length === 0 && activePlans.length === 0) return undefined;
						return {
							name: options.contextName ?? "persistent-planning",
							priority: options.contextPriority ?? 60,
							value: asJson({ goals: activeGoals, taskPlans: activePlans }),
						};
					},
				};
	return {
		...(contextProvider === undefined ? {} : { contextProvider }),
		toolProvider: {
			async provide(input) {
				return [
					...(options.includeGoalTool === false ? [] : [goalTool(input, options)]),
					...(options.includeTaskPlanTool === false ? [] : [taskPlanTool(input, options)]),
				];
			},
		},
	};
}

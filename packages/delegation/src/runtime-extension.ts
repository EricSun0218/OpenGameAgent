import { createHash } from "node:crypto";
import type { GameInput, GameTool, GameToolExecutionContext, JsonObject, JsonValue } from "@opengameagent/protocol";
import type { GamePostToolContextProvider, GameToolProvider } from "@opengameagent/runtime";
import type { GameDelegationRecord, GameDelegationRequest } from "./delegation.js";
import type { GameDelegationManager } from "./manager.js";

export interface GameDelegateDefinition {
	id: string;
	description: string;
	maximumTurns?: number;
	maximumDepth?: number;
	allowContextInheritance?: boolean;
}

export interface GameDelegationLineage {
	id: string;
	rootId: string;
	depth: number;
}

export interface GameDelegationExtensionOptions {
	manager: GameDelegationManager;
	delegates(
		input: GameInput,
		signal: AbortSignal,
	): Promise<readonly GameDelegateDefinition[]> | readonly GameDelegateDefinition[];
	resolveLineage?(input: GameInput): GameDelegationLineage | undefined;
	captureContext?(input: GameInput, signal: AbortSignal): Promise<JsonValue> | JsonValue;
	maximumDepth?: number;
	maximumTurns?: number;
	maximumListed?: number;
	includeContext?: boolean;
	contextName?: string;
	contextPriority?: number;
}

export interface GameDelegationExtensionResources {
	toolProvider: GameToolProvider;
	postToolContextProvider?: GamePostToolContextProvider;
}

function optionalString(value: JsonValue | undefined, name: string, maximum: number): string | undefined {
	if (value === undefined) return undefined;
	if (typeof value !== "string" || value.length < 1 || value.length > maximum)
		throw new TypeError(`${name} must be a bounded string.`);
	return value;
}

function optionalInteger(
	value: JsonValue | undefined,
	name: string,
	minimum: number,
	maximum: number,
): number | undefined {
	if (value === undefined) return undefined;
	if (!Number.isInteger(value) || (value as number) < minimum || (value as number) > maximum)
		throw new RangeError(`${name} is invalid.`);
	return value as number;
}

function optionalBoolean(value: JsonValue | undefined, name: string): boolean | undefined {
	if (value === undefined) return undefined;
	if (typeof value !== "boolean") throw new TypeError(`${name} must be boolean.`);
	return value;
}

function canonical(value: JsonValue): string {
	if (value === null || typeof value !== "object") return JSON.stringify(value);
	if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
	return `{${Object.keys(value)
		.sort()
		.map((key) => `${JSON.stringify(key)}:${canonical(value[key] as JsonValue)}`)
		.join(",")}}`;
}

function delegationId(context: GameToolExecutionContext, identity: JsonValue): string {
	const digest = createHash("sha256")
		.update(
			canonical(
				JSON.parse(
					JSON.stringify({
						version: 1,
						session: context.input.session,
						inputId: context.input.id,
						turn: context.turn,
						toolCallIndex: context.toolCallIndex,
						identity,
					}),
				) as JsonValue,
			),
		)
		.digest("base64url");
	return `delegation-${digest.slice(0, 43)}`;
}

function safeRecord(record: GameDelegationRecord): JsonValue {
	return {
		id: record.request.id,
		delegateId: record.request.delegateId,
		status: record.status,
		attempt: record.attempt,
		depth: record.request.depth,
		rootId: record.request.rootDelegationId,
		...(record.result === undefined ? {} : { result: structuredClone(record.result) }),
		...(record.error === undefined ? {} : { error: record.error }),
	};
}

function validateDefinitions(definitions: readonly GameDelegateDefinition[]): Map<string, GameDelegateDefinition> {
	if (definitions.length > 128) throw new RangeError("Delegate definition count exceeds 128.");
	const result = new Map<string, GameDelegateDefinition>();
	for (const definition of definitions) {
		if (!/^[a-z0-9][a-z0-9._:-]{0,191}$/iu.test(definition.id) || result.has(definition.id))
			throw new Error("Delegate ids must be unique portable identifiers.");
		if (!definition.description || definition.description.length > 2_048)
			throw new RangeError("Delegate descriptions must contain 1 to 2048 characters.");
		if (definition.maximumTurns !== undefined)
			optionalInteger(definition.maximumTurns, "Delegate maximumTurns", 1, 128);
		if (definition.maximumDepth !== undefined) optionalInteger(definition.maximumDepth, "Delegate maximumDepth", 1, 16);
		result.set(definition.id, structuredClone(definition));
	}
	return result;
}

function createTool(
	name: string,
	label: string,
	description: string,
	parameters: JsonObject,
	execute: GameTool["execute"],
): GameTool {
	return { definition: { name, label, description, parameters, executionMode: "sequential" }, execute };
}

export function createGameDelegationExtension(
	options: GameDelegationExtensionOptions,
): GameDelegationExtensionResources {
	if (!options.manager || !options.delegates) throw new TypeError("Delegation extension options are incomplete.");
	const maximumDepth = options.maximumDepth ?? 3;
	const maximumTurns = options.maximumTurns ?? 16;
	const maximumListed = options.maximumListed ?? 64;
	optionalInteger(maximumDepth, "maximumDepth", 1, 16);
	optionalInteger(maximumTurns, "maximumTurns", 1, 128);
	optionalInteger(maximumListed, "maximumListed", 1, 256);

	const toolProvider: GameToolProvider = {
		name: "agent-delegation",
		async provide(input, signal) {
			const definitions = validateDefinitions(await options.delegates(input, signal));
			const lineage = options.resolveLineage?.(input);
			if (lineage && (lineage.depth < 1 || lineage.depth > 16))
				throw new RangeError("Delegation lineage depth is invalid.");
			const availableIds = [...definitions.keys()];
			const tools: GameTool[] = [];
			if ((lineage?.depth ?? 0) < maximumDepth && availableIds.length > 0) {
				tools.push(
					createTool(
						"delegate_agent_task",
						"Delegate agent task",
						"Run an independent bounded task with a registered delegated agent. Background tasks can be checked later by id.",
						{
							type: "object",
							properties: {
								delegateId: { type: "string", enum: availableIds },
								task: {},
								background: { type: "boolean" },
								inheritContext: { type: "boolean" },
								maximumTurns: { type: "integer", minimum: 1, maximum: 128 },
							},
							required: ["delegateId", "task"],
							additionalProperties: false,
						},
						async (call, context) => {
							const delegateIdValue = optionalString(call.arguments["delegateId"], "delegateId", 192);
							if (!delegateIdValue) throw new TypeError("delegateId is required.");
							const definition = definitions.get(delegateIdValue);
							if (!definition) throw new Error("Delegate is not available for this input.");
							if (call.arguments["task"] === undefined) throw new TypeError("task is required.");
							const nextDepth = (lineage?.depth ?? 0) + 1;
							const allowedDepth = Math.min(maximumDepth, definition.maximumDepth ?? maximumDepth);
							if (nextDepth > allowedDepth) throw new Error("Delegation depth limit is exhausted.");
							const requestedTurns = optionalInteger(call.arguments["maximumTurns"], "maximumTurns", 1, 128);
							const allowedTurns = Math.min(maximumTurns, definition.maximumTurns ?? maximumTurns);
							const inheritContext = optionalBoolean(call.arguments["inheritContext"], "inheritContext") ?? false;
							const background = optionalBoolean(call.arguments["background"], "background") ?? false;
							if (inheritContext && definition.allowContextInheritance !== true)
								throw new Error("This delegate does not allow parent-context inheritance.");
							if (inheritContext && !options.captureContext)
								throw new Error("No host context-capture policy is configured for delegation.");
							const resolvedTurns = Math.min(requestedTurns ?? allowedTurns, allowedTurns);
							const id = delegationId(context, {
								delegateId: delegateIdValue,
								task: call.arguments["task"] as JsonValue,
								background,
								inheritContext,
								maximumTurns: resolvedTurns,
								depth: nextDepth,
								parentDelegationId: lineage?.id ?? null,
								rootDelegationId: lineage?.rootId ?? null,
							});
							const existing = await options.manager.read(context.input.session, id, context.signal);
							if (existing)
								return {
									content: [
										{
											type: "json",
											value: safeRecord(await options.manager.submit(existing.request, { background }, context.signal)),
										},
									],
								};
							const inheritedContext = inheritContext
								? structuredClone(await options.captureContext?.(context.input, context.signal))
								: undefined;
							const request: GameDelegationRequest = {
								id,
								session: structuredClone(context.input.session),
								parentInputId: context.input.id,
								parentRunId: context.runId,
								parentTurn: context.turn,
								parentMoment: structuredClone(context.input.moment),
								delegateId: delegateIdValue,
								task: structuredClone(call.arguments["task"] as JsonValue),
								depth: nextDepth,
								maximumTurns: resolvedTurns,
								inheritContext,
								...(inheritedContext === undefined ? {} : { inheritedContext }),
								...(lineage === undefined ? {} : { parentDelegationId: lineage.id }),
								rootDelegationId: lineage?.rootId ?? id,
							};
							const record = await options.manager.submit(request, { background }, context.signal);
							return { content: [{ type: "json", value: safeRecord(record) }] };
						},
					),
				);
			}

			tools.push(
				createTool(
					"read_delegated_task",
					"Read delegated task",
					"Read one delegated task owned by this exact game session.",
					{
						type: "object",
						properties: { id: { type: "string", minLength: 1, maxLength: 256 } },
						required: ["id"],
						additionalProperties: false,
					},
					async (call, context) => {
						const id = optionalString(call.arguments["id"], "id", 256);
						if (!id) throw new TypeError("id is required.");
						const record = await options.manager.read(context.input.session, id, context.signal);
						return {
							content: [{ type: "json", value: record ? safeRecord(record) : { error: "delegation_not_found" } }],
							...(record ? {} : { isError: true }),
						};
					},
				),
				createTool(
					"list_delegated_tasks",
					"List delegated tasks",
					"List bounded delegated-task status for this exact game session.",
					{
						type: "object",
						properties: {
							rootId: { type: "string", minLength: 1, maxLength: 256 },
							maximum: { type: "integer", minimum: 1, maximum: maximumListed },
						},
						additionalProperties: false,
					},
					async (call, context) => {
						const rootId = optionalString(call.arguments["rootId"], "rootId", 256);
						const maximum = optionalInteger(call.arguments["maximum"], "maximum", 1, maximumListed) ?? maximumListed;
						const records = await options.manager.list(context.input.session, maximum, rootId, context.signal);
						return { content: [{ type: "json", value: records.map(safeRecord) }] };
					},
				),
				createTool(
					"steer_delegated_task",
					"Steer delegated task",
					"Send a bounded structured update to a currently running delegated task.",
					{
						type: "object",
						properties: { id: { type: "string", minLength: 1, maxLength: 256 }, message: {} },
						required: ["id", "message"],
						additionalProperties: false,
					},
					async (call, context) => {
						const id = optionalString(call.arguments["id"], "id", 256);
						if (!id || call.arguments["message"] === undefined) throw new TypeError("id and message are required.");
						const result = await options.manager.steer(
							context.input.session,
							id,
							call.arguments["message"] as JsonValue,
							context.signal,
						);
						return { content: [{ type: "json", value: result as unknown as JsonValue }], isError: !result.accepted };
					},
				),
				createTool(
					"cancel_delegated_task",
					"Cancel delegated task",
					"Persistently cancel a pending or running delegated task owned by this exact game session.",
					{
						type: "object",
						properties: {
							id: { type: "string", minLength: 1, maxLength: 256 },
							reason: { type: "string", minLength: 1, maxLength: 4_096 },
						},
						required: ["id", "reason"],
						additionalProperties: false,
					},
					async (call, context) => {
						const id = optionalString(call.arguments["id"], "id", 256);
						const reason = optionalString(call.arguments["reason"], "reason", 4_096);
						if (!id || !reason) throw new TypeError("id and reason are required.");
						const record = await options.manager.cancel(context.input.session, id, reason, context.signal);
						return { content: [{ type: "json", value: safeRecord(record) }] };
					},
				),
			);
			return tools;
		},
	};

	const postToolContextProvider: GamePostToolContextProvider | undefined =
		options.includeContext === false
			? undefined
			: {
					name: options.contextName ?? "agent-delegation",
					async provide(input, _tools, signal) {
						const [delegates, records] = await Promise.all([
							options.delegates(input, signal),
							options.manager.list(input.session, maximumListed, undefined, signal),
						]);
						const active = records.filter((record) => record.status === "pending" || record.status === "running");
						if (delegates.length === 0 && active.length === 0) return undefined;
						return {
							name: options.contextName ?? "agent-delegation",
							priority: options.contextPriority ?? 45,
							value: {
								delegates: delegates.map(({ id, description }) => ({ id, description })),
								active: active.map(safeRecord),
							},
						};
					},
				};

	return { toolProvider, ...(postToolContextProvider === undefined ? {} : { postToolContextProvider }) };
}

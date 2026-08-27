import type {
	GameInput,
	GameToolCall,
	GameToolDefinition,
	GameToolExecutionContext,
	GameToolResult,
	JsonValue,
} from "@opengameagent/protocol";
import type { GameToolExecutionMiddleware, GameToolVisibilityPolicy } from "@opengameagent/runtime";

export type GameToolPolicyPhase = "advertise" | "execute";
export type GameToolPolicyEffect = "allow" | "deny" | "hide";

export interface GameToolAuthorityScope {
	id: string;
	allowedTools?: readonly string[];
	deniedTools?: readonly string[];
	attributes?: Readonly<Record<string, JsonValue>>;
}

export interface GameToolAuthorityScopeProvider {
	resolve(input: GameInput, signal: AbortSignal): Promise<GameToolAuthorityScope> | GameToolAuthorityScope;
}

export interface GameToolPolicyContext {
	phase: GameToolPolicyPhase;
	input: GameInput;
	tool: GameToolDefinition;
	scope: GameToolAuthorityScope;
	call?: GameToolCall;
	execution?: Omit<GameToolExecutionContext, "input" | "signal">;
}

export interface GameToolPolicyDecision {
	effect: GameToolPolicyEffect;
	reason: string;
}

export interface GameToolPolicy {
	evaluate(
		context: GameToolPolicyContext,
		signal: AbortSignal,
	): Promise<GameToolPolicyDecision> | GameToolPolicyDecision;
}

export interface GameToolPolicyAuditEntry {
	timestamp: number;
	phase: GameToolPolicyPhase;
	inputId: string;
	runId?: string;
	turn?: number;
	toolName: string;
	scopeId: string;
	effect: GameToolPolicyEffect;
	reason: string;
}

export interface GameToolPolicyAuditSink {
	record(entry: GameToolPolicyAuditEntry, signal: AbortSignal): Promise<void> | void;
}

export interface GameToolPolicyResourcesOptions {
	scopeProvider: GameToolAuthorityScopeProvider;
	policies?: readonly GameToolPolicy[];
	audit?: GameToolPolicyAuditSink;
	maximumPolicies?: number;
}

export interface GameToolPolicyResources {
	visibility: GameToolVisibilityPolicy;
	execution: GameToolExecutionMiddleware;
}

function validateBoundedText(value: string, name: string, maximum: number): void {
	if (!value || value.length > maximum) throw new TypeError(`${name} must be bounded and non-empty.`);
	for (const character of value) {
		const code = character.codePointAt(0) ?? 0;
		if (code < 32 || code === 127) throw new TypeError(`${name} contains a control character.`);
	}
}

function normalizeToolNames(values: readonly string[] | undefined, name: string): readonly string[] | undefined {
	if (values === undefined) return undefined;
	if (values.length > 4096) throw new RangeError(`${name} contains too many tool names.`);
	const names = [...new Set(values)];
	for (const value of names) validateBoundedText(value, name, 256);
	return names.sort((left, right) => left.localeCompare(right));
}

function normalizeScope(scope: GameToolAuthorityScope): GameToolAuthorityScope {
	validateBoundedText(scope.id, "Tool authority scope id", 256);
	const serializedAttributes = JSON.stringify(scope.attributes ?? {});
	if (serializedAttributes.length > 64 * 1024) throw new RangeError("Tool authority scope attributes are too large.");
	return {
		id: scope.id,
		...(scope.allowedTools === undefined
			? {}
			: { allowedTools: normalizeToolNames(scope.allowedTools, "Allowed tool name") as readonly string[] }),
		...(scope.deniedTools === undefined
			? {}
			: { deniedTools: normalizeToolNames(scope.deniedTools, "Denied tool name") as readonly string[] }),
		...(scope.attributes === undefined ? {} : { attributes: structuredClone(scope.attributes) }),
	};
}

function scopeDecision(scope: GameToolAuthorityScope, toolName: string): GameToolPolicyDecision {
	if (scope.deniedTools?.includes(toolName)) return { effect: "hide", reason: "scope-denied" };
	if (scope.allowedTools !== undefined && !scope.allowedTools.includes(toolName)) {
		return { effect: "hide", reason: "scope-not-allowed" };
	}
	return { effect: "allow", reason: "scope-allowed" };
}

function validateDecision(decision: GameToolPolicyDecision): GameToolPolicyDecision {
	if (decision.effect !== "allow" && decision.effect !== "deny" && decision.effect !== "hide")
		throw new TypeError("Tool policy returned an invalid effect.");
	validateBoundedText(decision.reason, "Tool policy reason", 512);
	return decision;
}

function fixedDeniedResult(): GameToolResult {
	return {
		isError: true,
		content: [
			{
				type: "json",
				value: { error: "tool_denied" },
			},
		],
	};
}

export function createGameToolPolicyResources(options: GameToolPolicyResourcesOptions): GameToolPolicyResources {
	const policies = [...(options.policies ?? [])];
	const maximumPolicies = options.maximumPolicies ?? 64;
	if (!Number.isInteger(maximumPolicies) || maximumPolicies < 0 || maximumPolicies > 1024)
		throw new RangeError("maximumPolicies must be 0..1024.");
	if (policies.length > maximumPolicies) throw new RangeError("Too many tool policies were configured.");

	const decide = async (
		phase: GameToolPolicyPhase,
		input: GameInput,
		tool: GameToolDefinition,
		signal: AbortSignal,
		call?: GameToolCall,
		execution?: Omit<GameToolExecutionContext, "input" | "signal">,
	): Promise<GameToolPolicyDecision> => {
		signal.throwIfAborted();
		const scope = normalizeScope(await options.scopeProvider.resolve(input, signal));
		let finalDecision = scopeDecision(scope, tool.name);
		const context: GameToolPolicyContext = {
			phase,
			input,
			tool,
			scope,
			...(call === undefined ? {} : { call }),
			...(execution === undefined ? {} : { execution }),
		};
		if (finalDecision.effect === "allow") {
			for (const policy of policies) {
				signal.throwIfAborted();
				const decision = validateDecision(await policy.evaluate(context, signal));
				if (decision.effect !== "allow") {
					finalDecision = decision;
					break;
				}
				finalDecision = decision;
			}
		}
		await options.audit?.record(
			{
				timestamp: Date.now(),
				phase,
				inputId: input.id,
				...(execution?.runId === undefined ? {} : { runId: execution.runId }),
				...(execution?.turn === undefined ? {} : { turn: execution.turn }),
				toolName: tool.name,
				scopeId: scope.id,
				effect: finalDecision.effect,
				reason: finalDecision.reason,
			},
			signal,
		);
		return finalDecision;
	};

	return {
		visibility: {
			async isVisible(input, tool, signal) {
				const decision = await decide("advertise", input, tool, signal);
				return decision.effect === "allow";
			},
		},
		execution: {
			async execute(tool, call, context, next) {
				const decision = await decide("execute", context.input, tool, context.signal, call, {
					runId: context.runId,
					turn: context.turn,
					toolCallIndex: context.toolCallIndex,
				});
				if (decision.effect !== "allow") return fixedDeniedResult();
				return await next();
			},
		},
	};
}

export class InMemoryGameToolPolicyAuditSink implements GameToolPolicyAuditSink {
	private readonly entries: GameToolPolicyAuditEntry[] = [];

	constructor(private readonly capacity = 10_000) {
		if (!Number.isInteger(capacity) || capacity < 1 || capacity > 1_000_000)
			throw new RangeError("Audit capacity is invalid.");
	}

	record(entry: GameToolPolicyAuditEntry, signal: AbortSignal): void {
		signal.throwIfAborted();
		this.entries.push(structuredClone(entry));
		if (this.entries.length > this.capacity) this.entries.splice(0, this.entries.length - this.capacity);
	}

	read(): readonly GameToolPolicyAuditEntry[] {
		return structuredClone(this.entries);
	}
}

import {
	assertCanonicalTimestamp,
	canonicalizeJsonValue,
	normalizeJsonValueWithin,
	type JsonValue,
} from "@opengameagent/protocol";
import { DelegationCapacityError, DelegationError } from "./errors.js";
import type { DelegationDraft, DelegationLimits, DelegationPartition, DelegationStatus } from "./types.js";

const DECIMAL = /^(?:0|[1-9][0-9]*)$/u;
const LIMIT_NAMES = [
	"maxIdentifierBytes",
	"maxControlTextBytes",
	"maxToolDescriptionBytes",
	"maxTaskBytes",
	"maxContextBytes",
	"maxResultBytes",
	"maxModelResultBytes",
	"maxJsonDepth",
	"maxCounterDigits",
	"maxDepth",
	"maxTargets",
	"maxPendingPerGeneration",
	"maxActiveRuns",
	"maxPageSize",
	"maxRecoveryBatchSize",
	"maxPurgeBatchSize",
] as const satisfies readonly (keyof DelegationLimits)[];

export function freezeDelegationLimits(input: DelegationLimits): Readonly<DelegationLimits> {
	for (const name of LIMIT_NAMES) positive(input[name], name);
	if (input.maxModelResultBytes > input.maxResultBytes) invalid("invalid_delegation_limits");
	if (input.maxJsonDepth >= Number.MAX_SAFE_INTEGER) invalid("invalid_delegation_limits");
	if (input.maxPageSize >= Number.MAX_SAFE_INTEGER) invalid("invalid_delegation_limits");
	if (input.maxRecoveryBatchSize > input.maxPageSize) invalid("invalid_delegation_limits");
	return Object.freeze({ ...input });
}

export function delegationStatuses(
	value: readonly DelegationStatus[] | undefined,
): readonly DelegationStatus[] | undefined {
	if (value === undefined) return undefined;
	const allowed: readonly DelegationStatus[] = ["pending", "running", "completed", "failed", "cancelled"];
	if (!Array.isArray(value) || value.length > allowed.length || value.some((status) => !allowed.includes(status))) {
		invalid("invalid_delegation_statuses");
	}
	return [...new Set(value)];
}

export function delegationCursor(value: bigint | undefined): bigint {
	if (value === undefined) return 0n;
	if (typeof value !== "bigint" || value < 0n) invalid("invalid_delegation_cursor");
	return value;
}

export function identifier(value: string, limits: Readonly<DelegationLimits>, name: string): string {
	if (typeof value !== "string" || value.length === 0 || byteLength(value) > limits.maxIdentifierBytes) {
		invalid(`invalid_${name}`);
	}
	return value;
}

export function partition(value: DelegationPartition, limits: Readonly<DelegationLimits>): DelegationPartition {
	return {
		ownerId: identifier(value.ownerId, limits, "owner_id"),
		timelineId: identifier(value.timelineId, limits, "timeline_id"),
		generation: decimal(value.generation, limits, "generation"),
	};
}

export function decimal(value: string, limits: Readonly<DelegationLimits>, name: string): string {
	if (typeof value !== "string" || !DECIMAL.test(value) || value.length > limits.maxCounterDigits) {
		invalid(`invalid_${name}`);
	}
	return value;
}

export function timestamp(value: string): string {
	try {
		assertCanonicalTimestamp(value);
		return value;
	} catch {
		return invalid("invalid_delegation_timestamp");
	}
}

export function json(value: unknown, maxBytes: number, limits: Readonly<DelegationLimits>): JsonValue {
	try {
		return normalizeJsonValueWithin(value, { maxBytes, maxDepth: limits.maxJsonDepth });
	} catch {
		return invalid("invalid_delegation_json");
	}
}

export function fingerprint(value: unknown): string {
	return canonicalizeJsonValue(value);
}

export function normalizeDelegationDraft(input: DelegationDraft, limits: Readonly<DelegationLimits>): DelegationDraft {
	const depth = decimal(input.depth, limits, "depth");
	positive(input.target.maxTurns, "max_turns");
	if (BigInt(depth) < 1n || BigInt(depth) > BigInt(limits.maxDepth)) throw new DelegationCapacityError();
	if (input.mode !== "foreground" && input.mode !== "background") invalid("invalid_delegation_mode");
	return {
		delegationId: identifier(input.delegationId, limits, "delegation_id"),
		partition: partition(input.partition, limits),
		parent: {
			sessionId: identifier(input.parent.sessionId, limits, "session_id"),
			actorId: identifier(input.parent.actorId, limits, "actor_id"),
			inputId: identifier(input.parent.inputId, limits, "input_id"),
			runId: identifier(input.parent.runId, limits, "run_id"),
			turn: decimal(input.parent.turn, limits, "turn"),
			toolCallId: identifier(input.parent.toolCallId, limits, "tool_call_id"),
		},
		target: {
			delegateId: identifier(input.target.delegateId, limits, "delegate_id"),
			sessionId: identifier(input.target.sessionId, limits, "target_session_id"),
			actorId: identifier(input.target.actorId, limits, "target_actor_id"),
			maxTurns: input.target.maxTurns,
		},
		mode: input.mode,
		task: json(input.task, limits.maxTaskBytes, limits),
		...(input.context === undefined ? {} : { context: json(input.context, limits.maxContextBytes, limits) }),
		rootDelegationId: identifier(input.rootDelegationId, limits, "root_delegation_id"),
		...(input.parentDelegationId === undefined
			? {}
			: { parentDelegationId: identifier(input.parentDelegationId, limits, "parent_delegation_id") }),
		depth,
		childInputId: identifier(input.childInputId, limits, "child_input_id"),
		createdAt: timestamp(input.createdAt),
	};
}

export function increment(value: string, limits: Readonly<DelegationLimits>): string {
	const next = (BigInt(decimal(value, limits, "counter")) + 1n).toString();
	return decimal(next, limits, "counter");
}

export function positive(value: number, name: string): void {
	if (!Number.isSafeInteger(value) || value < 1) invalid(`invalid_${name}`);
}

export function byteLength(value: string): number {
	return new TextEncoder().encode(value).byteLength;
}

export function invalid(code: string): never {
	throw new DelegationError(code);
}

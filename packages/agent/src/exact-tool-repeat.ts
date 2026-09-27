import { assertOpaqueIdentifier, canonicalizeJsonValue } from "@opengameagent/protocol";
import type { GameExactToolRepeatPolicy } from "./types.js";

export type ExactToolRepeatAction = "advisory" | "terminated";

export interface ExactToolRepeatObservation {
	readonly action?: ExactToolRepeatAction;
	readonly consecutiveCount: number;
	readonly block: boolean;
}

interface ExactToolRepeatAdvisory {
	readonly toolName: string;
	readonly consecutiveCount: number;
}

export interface CapturedExactToolRepeatPolicy {
	readonly advisoryThreshold: number;
	readonly terminationThreshold: number;
	readonly exemptToolNames: readonly string[];
}

const EXACT_TOOL_REPEAT_ADVISORY_INSTRUCTION = "Reassess the plan before repeating this prepared Tool call.";

export function captureExactToolRepeatPolicy(
	policy: unknown,
	maxTools: number | undefined,
): CapturedExactToolRepeatPolicy {
	if (typeof policy !== "object" || policy === null || Array.isArray(policy)) invalidPolicy();
	if (maxTools === undefined) invalidPolicy();
	const candidate = policy as Partial<GameExactToolRepeatPolicy>;
	const advisoryThreshold = positiveSafeInteger(candidate.advisoryThreshold);
	const terminationThreshold = positiveSafeInteger(candidate.terminationThreshold);
	if (terminationThreshold <= advisoryThreshold) invalidPolicy();
	const names = candidate.exemptToolNames === undefined ? [] : candidate.exemptToolNames;
	if (!Array.isArray(names)) invalidPolicy();
	let length: number;
	try {
		length = names.length;
	} catch {
		invalidPolicy();
	}
	if (!Number.isSafeInteger(length) || length < 0 || length > maxTools) invalidPolicy();
	const seenToolNames = new Set<string>();
	const exemptToolNames: string[] = [];
	for (let index = 0; index < length; index += 1) {
		let name: unknown;
		try {
			name = names[index];
		} catch {
			invalidPolicy();
		}
		if (typeof name !== "string") invalidPolicy();
		try {
			assertOpaqueIdentifier(name);
		} catch {
			invalidPolicy();
		}
		if (seenToolNames.has(name)) invalidPolicy();
		seenToolNames.add(name);
		exemptToolNames.push(name);
	}
	exemptToolNames.sort();
	return Object.freeze({
		advisoryThreshold,
		terminationThreshold,
		exemptToolNames: Object.freeze(exemptToolNames),
	});
}

export function exactToolRepeatAdvisoryMessage(toolName: string, consecutiveCount: number | string): string {
	assertOpaqueIdentifier(toolName);
	const count = canonicalPositiveSafeInteger(consecutiveCount);
	return canonicalizeJsonValue({
		kind: "game-agent-policy",
		policy: "exact-tool-repeat",
		action: "advisory",
		toolName,
		consecutiveCount: count,
		instruction: EXACT_TOOL_REPEAT_ADVISORY_INSTRUCTION,
	});
}

export function canonicalExactToolRepeatPolicyDefinition(policy: CapturedExactToolRepeatPolicy): string {
	return canonicalizeJsonValue({
		kind: "opengameagent-policy-lineage",
		policy: "exact-tool-repeat",
		version: "1",
		advisoryThreshold: String(policy.advisoryThreshold),
		terminationThreshold: String(policy.terminationThreshold),
		exemptToolNames: policy.exemptToolNames,
		providerContextTransform: {
			placement: "append-after-host-transform",
			role: "user",
			contentTemplate: {
				kind: "game-agent-policy",
				policy: "exact-tool-repeat",
				action: "advisory",
				toolName: "{event.toolName}",
				consecutiveCount: "{event.consecutiveCount}",
				instruction: EXACT_TOOL_REPEAT_ADVISORY_INSTRUCTION,
			},
		},
	});
}

export class ExactToolRepeatTracker {
	private trackedToolName: string | undefined;
	private canonicalArguments: string | undefined;
	private consecutiveCount = 0;
	private advisory: ExactToolRepeatAdvisory | undefined;
	private terminated = false;
	private batchCalls: readonly object[] = [];
	private nextBatchCall = 0;

	constructor(private readonly policy: CapturedExactToolRepeatPolicy) {}

	beginAssistantMessage(toolCalls: readonly object[]): void {
		if (!this.terminated && this.nextBatchCall < this.batchCalls.length) this.resetSequence();
		this.batchCalls = Object.freeze([...toolCalls]);
		this.nextBatchCall = 0;
	}

	finishAssistantMessage(): void {
		if (!this.terminated && this.nextBatchCall < this.batchCalls.length) this.resetSequence();
		this.batchCalls = [];
		this.nextBatchCall = 0;
	}

	observe(toolCall: object, toolName: string, argumentsValue: unknown): ExactToolRepeatObservation | undefined {
		if (this.terminated) {
			return Object.freeze({ consecutiveCount: this.consecutiveCount, block: true });
		}
		const sourceIndex = this.batchCalls.indexOf(toolCall, this.nextBatchCall);
		if (sourceIndex !== this.nextBatchCall) this.resetSequence();
		this.nextBatchCall = sourceIndex < 0 ? this.batchCalls.length : sourceIndex + 1;
		if (hasSortedName(this.policy.exemptToolNames, toolName)) return undefined;
		const canonicalArguments = canonicalizeJsonValue(argumentsValue);
		if (toolName === this.trackedToolName && canonicalArguments === this.canonicalArguments) {
			this.consecutiveCount = Math.min(this.consecutiveCount + 1, this.policy.terminationThreshold);
		} else {
			this.trackedToolName = toolName;
			this.canonicalArguments = canonicalArguments;
			this.consecutiveCount = 1;
			this.advisory = undefined;
		}

		if (this.consecutiveCount >= this.policy.terminationThreshold) {
			this.terminated = true;
			return Object.freeze({
				action: "terminated" as const,
				consecutiveCount: this.consecutiveCount,
				block: true,
			});
		}
		if (this.consecutiveCount === this.policy.advisoryThreshold) {
			this.advisory = Object.freeze({ toolName, consecutiveCount: this.consecutiveCount });
			return Object.freeze({
				action: "advisory" as const,
				consecutiveCount: this.consecutiveCount,
				block: false,
			});
		}
		return undefined;
	}

	reset(): void {
		this.resetSequence();
		this.batchCalls = [];
		this.nextBatchCall = 0;
		this.terminated = false;
	}

	private resetSequence(): void {
		this.trackedToolName = undefined;
		this.canonicalArguments = undefined;
		this.consecutiveCount = 0;
		this.advisory = undefined;
	}

	isTerminated(): boolean {
		return this.terminated;
	}

	modelAdvisory(): string | undefined {
		const advisory = this.advisory;
		if (!advisory) return undefined;
		return exactToolRepeatAdvisoryMessage(advisory.toolName, advisory.consecutiveCount);
	}
}

function canonicalPositiveSafeInteger(value: number | string): string {
	if (typeof value === "number") {
		if (!Number.isSafeInteger(value) || value < 1) throw new TypeError("The repeat count is invalid.");
		return String(value);
	}
	if (!/^[1-9][0-9]*$/u.test(value)) throw new TypeError("The repeat count is invalid.");
	const parsed = Number(value);
	if (!Number.isSafeInteger(parsed)) throw new TypeError("The repeat count is invalid.");
	return value;
}

function hasSortedName(names: readonly string[], value: string): boolean {
	let lower = 0;
	let upper = names.length - 1;
	while (lower <= upper) {
		const middle = lower + Math.floor((upper - lower) / 2);
		const candidate = names[middle];
		if (candidate === value) return true;
		if (candidate !== undefined && candidate < value) lower = middle + 1;
		else upper = middle - 1;
	}
	return false;
}

function positiveSafeInteger(value: unknown): number {
	if (!Number.isSafeInteger(value) || (value as number) < 1) invalidPolicy();
	return value as number;
}

function invalidPolicy(): never {
	throw new TypeError(
		"Exact Tool-repeat policy requires positive safe-integer thresholds, termination above advisory, and unique bounded exemptions.",
	);
}

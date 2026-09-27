import { createHash } from "node:crypto";
import type { AgentTool } from "@earendil-works/pi-agent-core";
import { prepareToolCatalogSnapshot, ToolSnapshotLimitError } from "@opengameagent/agent";
import {
	assertCanonicalTimestamp,
	assertGameTime,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type GameTime,
	normalizeJsonValue,
} from "@opengameagent/protocol";
import { BehaviorEvolutionValidationError } from "./evolution-errors.js";
import type {
	BehaviorEvidence,
	BehaviorLimits,
	BehaviorPartition,
	BehaviorProposalStatus,
	BehaviorReflection,
	BehaviorRegistryKey,
	BehaviorRegistryScope,
	BehaviorStep,
	BehaviorStepDraft,
	BehaviorToolDescriptor,
} from "./evolution-types.js";

const DECIMAL = /^(0|[1-9][0-9]*)$/u;
const POSITIVE_DECIMAL = /^[1-9][0-9]*$/u;
const SKILL_NAME = /^[a-z0-9]+(?:-[a-z0-9]+)*$/u;
const SHA256 = /^[a-f0-9]{64}$/u;
const encoder = new TextEncoder();
const LIMIT_NAMES = [
	"maxSkillNameBytes",
	"maxDescriptionBytes",
	"maxReflectionBytes",
	"maxEvidencePerProposal",
	"maxEvidenceKindBytes",
	"maxEvidenceReferenceBytes",
	"maxStepsPerSkill",
	"maxStepInstructionBytes",
	"maxToolNameBytes",
	"maxDecisionCodeBytes",
	"maxJsonDepth",
	"maxToolsPerCatalog",
	"maxToolCatalogBytes",
	"maxToolSchemaDepth",
	"maxScopesPerView",
	"maxOpenProposalsPerOwnerGeneration",
	"maxVersionsPerSkill",
	"maxListPageSize",
	"maxPurgeBatchSize",
] as const satisfies readonly (keyof BehaviorLimits)[];

export function validateBehaviorLimits(limits: BehaviorLimits): void {
	for (const name of LIMIT_NAMES) {
		const value = limits[name];
		if (!Number.isSafeInteger(value) || value < 1 || value >= Number.MAX_SAFE_INTEGER) {
			throw new BehaviorEvolutionValidationError(`invalid_limit:${name}`);
		}
	}
}

export function normalizeBehaviorPartition(partition: BehaviorPartition): BehaviorPartition {
	assertOpaqueIdentifier(partition.ownerId);
	assertOpaqueIdentifier(partition.timelineId);
	validateDecimal(partition.generation, "generation");
	return { ...partition };
}

export function normalizeBehaviorScope(scope: BehaviorRegistryScope): BehaviorRegistryScope {
	const value = asRecord(scope, "invalid_behavior_scope");
	if (value.kind === "actor" && typeof value.actorId === "string" && hasOnly(value, ["kind", "actorId"])) {
		assertOpaqueIdentifier(value.actorId);
		return { kind: "actor", actorId: value.actorId };
	}
	if (value.kind === "shared" && typeof value.registryId === "string" && hasOnly(value, ["kind", "registryId"])) {
		assertOpaqueIdentifier(value.registryId);
		return { kind: "shared", registryId: value.registryId };
	}
	throw new BehaviorEvolutionValidationError("invalid_behavior_scope");
}

export function validateBehaviorRegistryKey(key: BehaviorRegistryKey, limits: BehaviorLimits): void {
	normalizeBehaviorPartition(key);
	normalizeBehaviorScope(key.scope);
	validateSkillName(key.skillName, limits);
}

export function validateBehaviorProposalKey(key: BehaviorPartition & { readonly proposalId: string }): void {
	normalizeBehaviorPartition(key);
	assertOpaqueIdentifier(key.proposalId);
}

export function validateSkillName(value: string, limits: BehaviorLimits): void {
	if (!SKILL_NAME.test(value)) throw new BehaviorEvolutionValidationError("invalid_behavior_skill_name");
	validateBytes(value, limits.maxSkillNameBytes, "invalid_behavior_skill_name_bytes");
}

export function validateDescription(value: string, limits: BehaviorLimits): void {
	validateBytes(value, limits.maxDescriptionBytes, "invalid_behavior_description_bytes");
}

export function normalizeReflection(value: unknown, limits: BehaviorLimits): BehaviorReflection {
	const reflection = asRecord(value, "invalid_behavior_reflection");
	if (
		typeof reflection.summary !== "string" ||
		!Array.isArray(reflection.evidence) ||
		!hasOnly(reflection, ["summary", "evidence"])
	) {
		throw new BehaviorEvolutionValidationError("invalid_behavior_reflection");
	}
	validateBytes(reflection.summary, limits.maxReflectionBytes, "invalid_behavior_reflection_bytes");
	if (reflection.evidence.length < 1) throw new BehaviorEvolutionValidationError("missing_behavior_evidence");
	if (reflection.evidence.length > limits.maxEvidencePerProposal) {
		throw new BehaviorEvolutionValidationError("too_many_behavior_evidence_items");
	}
	const unique = new Set<string>();
	const evidence = reflection.evidence.map((candidate) => {
		const item = asRecord(candidate, "invalid_behavior_evidence");
		if (
			typeof item.kind !== "string" ||
			typeof item.referenceId !== "string" ||
			!hasOnly(item, ["kind", "referenceId"])
		) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_evidence");
		}
		assertOpaqueIdentifier(item.kind);
		assertOpaqueIdentifier(item.referenceId);
		validateBytes(item.kind, limits.maxEvidenceKindBytes, "invalid_behavior_evidence_kind_bytes");
		validateBytes(item.referenceId, limits.maxEvidenceReferenceBytes, "invalid_behavior_evidence_reference_bytes");
		const key = `${item.kind}\0${item.referenceId}`;
		if (unique.has(key)) throw new BehaviorEvolutionValidationError("duplicate_behavior_evidence");
		unique.add(key);
		return { kind: item.kind, referenceId: item.referenceId } satisfies BehaviorEvidence;
	});
	return { summary: reflection.summary, evidence };
}

export function normalizeStepDrafts(
	value: unknown,
	limits: BehaviorLimits,
	tools: readonly BehaviorToolDescriptor[],
): readonly BehaviorStep[] {
	if (!Array.isArray(value) || value.length < 1) throw new BehaviorEvolutionValidationError("invalid_behavior_steps");
	if (value.length > limits.maxStepsPerSkill) throw new BehaviorEvolutionValidationError("too_many_behavior_steps");
	const catalog = normalizeToolCatalog(tools, limits);
	const stepIds = new Set<string>();
	return value.map((candidate) => {
		const step = asRecord(candidate, "invalid_behavior_step");
		if (
			typeof step.stepId !== "string" ||
			typeof step.instruction !== "string" ||
			typeof step.toolName !== "string" ||
			!hasOnly(step, ["stepId", "instruction", "toolName"])
		) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_step");
		}
		assertOpaqueIdentifier(step.stepId);
		if (stepIds.has(step.stepId)) throw new BehaviorEvolutionValidationError("duplicate_behavior_step");
		stepIds.add(step.stepId);
		validateBytes(step.instruction, limits.maxStepInstructionBytes, "invalid_behavior_step_instruction_bytes");
		validateBytes(step.toolName, limits.maxToolNameBytes, "invalid_behavior_tool_name_bytes");
		if (step.toolName === "behavior_skill_propose") {
			throw new BehaviorEvolutionValidationError("behavior_proposal_tool_cannot_be_composed");
		}
		const tool = catalog.get(step.toolName);
		if (tool === undefined) throw new BehaviorEvolutionValidationError("behavior_tool_not_available");
		return {
			stepId: step.stepId,
			instruction: step.instruction,
			toolName: step.toolName,
			toolFingerprint: tool.fingerprint,
		} satisfies BehaviorStep;
	});
}

export function normalizePersistedSteps(value: unknown, limits: BehaviorLimits): readonly BehaviorStep[] {
	if (!Array.isArray(value) || value.length < 1) throw new BehaviorEvolutionValidationError("invalid_behavior_steps");
	if (value.length > limits.maxStepsPerSkill) throw new BehaviorEvolutionValidationError("too_many_behavior_steps");
	const ids = new Set<string>();
	return value.map((candidate) => {
		const step = asRecord(candidate, "invalid_behavior_step");
		if (
			typeof step.stepId !== "string" ||
			typeof step.instruction !== "string" ||
			typeof step.toolName !== "string" ||
			typeof step.toolFingerprint !== "string" ||
			!hasOnly(step, ["stepId", "instruction", "toolName", "toolFingerprint"])
		) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_step");
		}
		assertOpaqueIdentifier(step.stepId);
		if (ids.has(step.stepId)) throw new BehaviorEvolutionValidationError("duplicate_behavior_step");
		ids.add(step.stepId);
		validateBytes(step.instruction, limits.maxStepInstructionBytes, "invalid_behavior_step_instruction_bytes");
		validateBytes(step.toolName, limits.maxToolNameBytes, "invalid_behavior_tool_name_bytes");
		validateFingerprint(step.toolFingerprint);
		return step as unknown as BehaviorStep;
	});
}

export function normalizeToolCatalog(
	tools: readonly BehaviorToolDescriptor[],
	limits: BehaviorLimits,
): ReadonlyMap<string, BehaviorToolDescriptor> {
	if (!Array.isArray(tools)) throw new BehaviorEvolutionValidationError("invalid_behavior_tool_catalog");
	if (tools.length > limits.maxToolsPerCatalog) {
		throw new BehaviorEvolutionValidationError("too_many_behavior_tools");
	}
	const catalog = new Map<string, BehaviorToolDescriptor>();
	for (const candidate of tools) {
		const tool = asRecord(candidate, "invalid_behavior_tool_descriptor");
		if (
			typeof tool.name !== "string" ||
			typeof tool.fingerprint !== "string" ||
			!hasOnly(tool, ["name", "fingerprint"])
		) {
			throw new BehaviorEvolutionValidationError("invalid_behavior_tool_descriptor");
		}
		validateBytes(tool.name, limits.maxToolNameBytes, "invalid_behavior_tool_name_bytes");
		validateFingerprint(tool.fingerprint);
		if (catalog.has(tool.name)) throw new BehaviorEvolutionValidationError("duplicate_behavior_tool");
		catalog.set(tool.name, { name: tool.name, fingerprint: tool.fingerprint });
	}
	return catalog;
}

export function assertToolsAvailable(
	steps: readonly BehaviorStep[],
	tools: readonly BehaviorToolDescriptor[],
	limits: BehaviorLimits,
): void {
	const catalog = normalizeToolCatalog(tools, limits);
	for (const step of steps) {
		const tool = catalog.get(step.toolName);
		if (tool === undefined || tool.fingerprint !== step.toolFingerprint) {
			throw new BehaviorEvolutionValidationError("behavior_tool_not_available");
		}
	}
}

export function createBehaviorToolDescriptors(
	tools: readonly AgentTool[],
	limits: Pick<
		BehaviorLimits,
		"maxToolsPerCatalog" | "maxToolNameBytes" | "maxToolCatalogBytes" | "maxToolSchemaDepth"
	>,
): readonly BehaviorToolDescriptor[] {
	if (!Array.isArray(tools)) throw new BehaviorEvolutionValidationError("invalid_behavior_tool_catalog");
	if (tools.length > limits.maxToolsPerCatalog) throw new BehaviorEvolutionValidationError("too_many_behavior_tools");
	try {
		const snapshot = prepareToolCatalogSnapshot(tools, {
			maxTools: limits.maxToolsPerCatalog,
			maxCatalogBytes: limits.maxToolCatalogBytes,
			maxSchemaDepth: limits.maxToolSchemaDepth,
		});
		return snapshot.descriptors.map((tool) => {
			validateBytes(tool.name, limits.maxToolNameBytes, "invalid_behavior_tool_name_bytes");
			return { name: tool.name, fingerprint: tool.fingerprint };
		});
	} catch (error) {
		if (error instanceof ToolSnapshotLimitError) {
			throw new BehaviorEvolutionValidationError(`invalid_behavior_${error.code}`);
		}
		throw new BehaviorEvolutionValidationError("invalid_behavior_tool_descriptor");
	}
}

export function behaviorContentFingerprint(value: {
	readonly partition: BehaviorPartition;
	readonly scope: BehaviorRegistryScope;
	readonly skillName: string;
	readonly description: string;
	readonly reflection: BehaviorReflection;
	readonly steps: readonly BehaviorStep[];
	readonly baseVersion?: string;
}): string {
	return createHash("sha256")
		.update(
			canonicalizeJsonValue(
				normalizeJsonValue({
					partition: value.partition,
					scope: value.scope,
					skillName: value.skillName,
					description: value.description,
					reflection: value.reflection,
					steps: value.steps,
					...(value.baseVersion === undefined ? {} : { baseVersion: value.baseVersion }),
				}),
			),
		)
		.digest("hex");
}

export function validateBehaviorTime(gameTime: GameTime, partition: BehaviorPartition): GameTime {
	assertGameTime(gameTime);
	if (gameTime.timelineId !== partition.timelineId || gameTime.generation !== partition.generation) {
		throw new BehaviorEvolutionValidationError("behavior_time_partition_mismatch");
	}
	return { ...gameTime };
}

export function validateBehaviorTimestamp(value: string): void {
	try {
		assertCanonicalTimestamp(value);
	} catch {
		throw new BehaviorEvolutionValidationError("invalid_behavior_timestamp");
	}
}

export function validateBehaviorRevision(value: string): void {
	if (!POSITIVE_DECIMAL.test(value)) throw new BehaviorEvolutionValidationError("invalid_behavior_revision");
}

export function validateBehaviorVersion(value: string): void {
	if (!POSITIVE_DECIMAL.test(value)) throw new BehaviorEvolutionValidationError("invalid_behavior_version");
}

export function compareBehaviorDecimals(left: string, right: string): number {
	if (!DECIMAL.test(left) || !DECIMAL.test(right)) {
		throw new BehaviorEvolutionValidationError("invalid_behavior_decimal");
	}
	if (left.length !== right.length) return left.length < right.length ? -1 : 1;
	return left === right ? 0 : left < right ? -1 : 1;
}

export function validateOptionalBehaviorVersion(value: string | undefined): void {
	if (value !== undefined) validateBehaviorVersion(value);
}

export function validateDecisionCode(value: string, limits: BehaviorLimits): void {
	validateBytes(value, limits.maxDecisionCodeBytes, "invalid_behavior_decision_code_bytes");
}

export function validateBehaviorStatus(value: string): asserts value is BehaviorProposalStatus {
	if (value !== "pending" && value !== "validated" && value !== "rejected" && value !== "activated") {
		throw new BehaviorEvolutionValidationError("invalid_behavior_proposal_status");
	}
}

export function normalizeBehaviorStatusFilter(
	values: readonly BehaviorProposalStatus[] | undefined,
): readonly BehaviorProposalStatus[] | undefined {
	if (values === undefined) return undefined;
	if (!Array.isArray(values) || values.length < 1)
		throw new BehaviorEvolutionValidationError("empty_behavior_status_filter");
	for (const value of values) validateBehaviorStatus(value);
	const normalized = [...values].sort();
	if (new Set(normalized).size !== normalized.length) {
		throw new BehaviorEvolutionValidationError("duplicate_behavior_status_filter");
	}
	return normalized;
}

export function validateBehaviorPageLimit(value: number, maximum: number, code: string): void {
	if (!Number.isSafeInteger(value) || value < 1 || value > maximum) throw new BehaviorEvolutionValidationError(code);
}

export function validateBehaviorCursor(value: bigint | undefined): void {
	if (value !== undefined && value < 0n) throw new BehaviorEvolutionValidationError("invalid_behavior_cursor");
}

export function normalizeBehaviorScopes(
	scopes: readonly BehaviorRegistryScope[],
	maximum: number,
): readonly BehaviorRegistryScope[] {
	if (!Array.isArray(scopes) || scopes.length < 1) throw new BehaviorEvolutionValidationError("empty_behavior_scopes");
	if (scopes.length > maximum) throw new BehaviorEvolutionValidationError("too_many_behavior_scopes");
	const seen = new Set<string>();
	return scopes.map((scope) => {
		const normalized = normalizeBehaviorScope(scope);
		const key = scopeKey(normalized);
		if (seen.has(key)) throw new BehaviorEvolutionValidationError("duplicate_behavior_scope");
		seen.add(key);
		return normalized;
	});
}

export function behaviorScopeKey(scope: BehaviorRegistryScope): string {
	return scopeKey(scope);
}

function validateFingerprint(value: string): void {
	if (!SHA256.test(value)) throw new BehaviorEvolutionValidationError("invalid_behavior_tool_fingerprint");
}

function scopeKey(scope: BehaviorRegistryScope): string {
	return scope.kind === "actor" ? `actor\0${scope.actorId}` : `shared\0${scope.registryId}`;
}

function asRecord(value: unknown, code: string): Record<string, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value)) {
		throw new BehaviorEvolutionValidationError(code);
	}
	return value as Record<string, unknown>;
}

function hasOnly(value: Record<string, unknown>, keys: readonly string[]): boolean {
	const allowed = new Set(keys);
	return Object.keys(value).every((key) => allowed.has(key));
}

function validateBytes(value: string, maximum: number, code: string): void {
	const length = encoder.encode(value).byteLength;
	if (length < 1 || length > maximum) throw new BehaviorEvolutionValidationError(code);
}

function validateDecimal(value: string, name: string): void {
	if (!DECIMAL.test(value)) throw new BehaviorEvolutionValidationError(`invalid_behavior_${name}`);
}

export type { BehaviorStepDraft };

import { createHash } from "node:crypto";
import type { AgentTool } from "@earendil-works/pi-agent-core";
import {
	canonicalizeJsonValue,
	type JsonValue,
	JsonValueLimitError,
	normalizeJsonValueWithin,
} from "@opengameagent/protocol";
import { Ajv, type AnySchema } from "ajv";
import { DURABLE_TOOL_EXECUTION, type DurableExecutionAgentTool } from "./durable-tool-execution.js";
import { validateToolResultLimits } from "./tool-result-snapshot.js";
import type {
	GameToolCatalogLimits,
	GameToolDescriptor,
	GameToolPolicyDecision,
	GameToolSnapshotLimits,
} from "./types.js";

const schemaValidator = new Ajv({ allErrors: true });

export class InvalidToolSnapshotError extends TypeError {
	constructor(message: string) {
		super(message);
		this.name = "InvalidToolSnapshotError";
	}
}

export class ToolSnapshotLimitError extends RangeError {
	constructor(
		readonly code:
			| "tool_count_exceeded"
			| "tool_catalog_too_large"
			| "tool_schema_too_deep"
			| "tool_policy_revision_too_large",
	) {
		super(code);
		this.name = "ToolSnapshotLimitError";
	}
}

export class InvalidToolPolicyError extends TypeError {
	constructor(message: string) {
		super(message);
		this.name = "InvalidToolPolicyError";
	}
}

export interface PreparedToolSnapshot {
	readonly tools: readonly AgentTool[];
	readonly descriptors: readonly Readonly<GameToolDescriptor>[];
}

export interface VisibleToolSnapshot extends PreparedToolSnapshot {
	readonly policyRevision: string;
	readonly revision: string;
	readonly visibleToolNames: readonly string[];
}

interface PreparedCatalogMetadata extends GameToolCatalogLimits {
	readonly bytes: number;
}

const catalogMetadata = new WeakMap<PreparedToolSnapshot, Readonly<PreparedCatalogMetadata>>();

/** Validate the complete game-owned tool snapshot before it reaches a model provider. */
export function validateToolSnapshot(
	tools: readonly AgentTool[],
	limits: Readonly<GameToolCatalogLimits>,
): readonly AgentTool[] {
	prepareToolCatalogSnapshot(tools, limits);
	return tools;
}

/** Validate, detach, freeze, and bind a complete candidate catalog before policy or provider publication. */
export function prepareToolSnapshot(
	tools: readonly AgentTool[],
	limits: Readonly<GameToolSnapshotLimits>,
): PreparedToolSnapshot {
	const capturedLimits = validateToolSnapshotLimits(limits);
	return prepareToolCatalogSnapshot(tools, {
		maxTools: capturedLimits.maxTools,
		maxCatalogBytes: capturedLimits.maxCatalogBytes,
		maxSchemaDepth: capturedLimits.maxSchemaDepth,
	});
}

/** Prepare a bounded catalog for non-execution consumers such as behavior validation. */
export function prepareToolCatalogSnapshot(
	tools: readonly AgentTool[],
	limits: Readonly<GameToolCatalogLimits>,
): PreparedToolSnapshot {
	const capturedLimits = validateToolCatalogLimits(limits);
	const candidates = captureToolArrayElements(tools, capturedLimits.maxTools);
	const names = new Set<string>();

	const boundTools: AgentTool[] = [];
	const descriptors: Readonly<GameToolDescriptor>[] = [];
	let catalogBytes = 2;
	if (catalogBytes > capturedLimits.maxCatalogBytes) throw new ToolSnapshotLimitError("tool_catalog_too_large");
	for (const tool of candidates) {
		const captured = captureTool(tool, names);
		let parameters: JsonValue;
		try {
			parameters = normalizeProviderJson(captured.parameters, capturedLimits);
		} catch (error) {
			if (error instanceof ToolSnapshotLimitError) throw error;
			throw new InvalidToolSnapshotError(`Tool ${captured.name} has a non-JSON parameter schema.`);
		}
		const executionMode = captured.executionMode ?? "sequential";
		const modelDescriptor = {
			name: captured.name,
			label: captured.label,
			description: captured.description,
			parameters,
			executionMode,
		} satisfies Omit<GameToolDescriptor, "fingerprint">;
		const canonicalDescriptor = canonicalizeJsonValue(modelDescriptor);
		const nextCatalogBytes = catalogBytes + (descriptors.length === 0 ? 0 : 1) + utf8Bytes(canonicalDescriptor);
		if (nextCatalogBytes > capturedLimits.maxCatalogBytes) {
			throw new ToolSnapshotLimitError("tool_catalog_too_large");
		}
		catalogBytes = nextCatalogBytes;
		validateNormalizedSchema(captured.name, parameters);
		freezeJsonValue(parameters);
		const fingerprint = fingerprintCanonicalDescriptor(canonicalDescriptor);
		const descriptor = Object.freeze({
			name: captured.name,
			label: captured.label,
			description: captured.description,
			parameters,
			executionMode,
			fingerprint,
		}) satisfies Readonly<GameToolDescriptor>;
		const execute = captured.execute;
		const prepareArguments = captured.prepareArguments;
		const prepareDurableExecution = captured.prepareDurableExecution;
		const bound = Object.freeze({
			name: descriptor.name,
			label: descriptor.label,
			description: descriptor.description,
			parameters: descriptor.parameters as AgentTool["parameters"],
			executionMode: descriptor.executionMode,
			...(prepareArguments ? { prepareArguments: (argumentsValue: unknown) => prepareArguments(argumentsValue) } : {}),
			...(prepareDurableExecution
				? {
						[DURABLE_TOOL_EXECUTION]: (
							toolCallId: Parameters<AgentTool["execute"]>[0],
							parameters: Parameters<AgentTool["execute"]>[1],
							signal?: Parameters<AgentTool["execute"]>[2],
						) => prepareDurableExecution(toolCallId, parameters, signal),
					}
				: {}),
			execute: (
				toolCallId: Parameters<AgentTool["execute"]>[0],
				argumentsValue: Parameters<AgentTool["execute"]>[1],
				signal: Parameters<AgentTool["execute"]>[2],
				onUpdate: Parameters<AgentTool["execute"]>[3],
			) => execute(toolCallId, argumentsValue, signal, onUpdate),
		}) satisfies AgentTool;
		boundTools.push(bound);
		descriptors.push(descriptor);
	}

	const snapshot = Object.freeze({ tools: Object.freeze(boundTools), descriptors: Object.freeze(descriptors) });
	catalogMetadata.set(snapshot, Object.freeze({ ...capturedLimits, bytes: catalogBytes }));
	return snapshot;
}

/** Extend a prepared snapshot without re-reading or re-wrapping its previously captured Tools. */
export function extendPreparedToolSnapshot(
	previous: PreparedToolSnapshot,
	additionalTools: readonly AgentTool[],
	limits: Readonly<GameToolSnapshotLimits>,
): PreparedToolSnapshot {
	const capturedLimits = validateToolSnapshotLimits(limits);
	const previousMetadata = catalogMetadata.get(previous);
	if (previousMetadata === undefined)
		throw new InvalidToolSnapshotError("The prior Tool snapshot was not prepared here.");
	if (
		previousMetadata.maxTools !== capturedLimits.maxTools ||
		previousMetadata.maxCatalogBytes !== capturedLimits.maxCatalogBytes ||
		previousMetadata.maxSchemaDepth !== capturedLimits.maxSchemaDepth
	) {
		throw new InvalidToolSnapshotError("A Tool snapshot can only be extended under its original catalog limits.");
	}
	const additionalCandidates = captureToolArrayElements(
		additionalTools,
		capturedLimits.maxTools - previous.tools.length,
	);
	if (additionalCandidates.length === 0) return previous;
	const additional = prepareToolCatalogSnapshot(additionalCandidates, {
		maxTools: capturedLimits.maxTools,
		maxCatalogBytes: capturedLimits.maxCatalogBytes,
		maxSchemaDepth: capturedLimits.maxSchemaDepth,
	});
	const names = new Set(previous.descriptors.map((descriptor) => descriptor.name));
	for (const descriptor of additional.descriptors) {
		if (names.has(descriptor.name)) {
			throw new InvalidToolSnapshotError(`Tool names must be unique: ${descriptor.name}`);
		}
		names.add(descriptor.name);
	}
	const additionalMetadata = catalogMetadata.get(additional);
	if (additionalMetadata === undefined)
		throw new InvalidToolSnapshotError("The additional Tool snapshot is incomplete.");
	const catalogBytes = previousMetadata.bytes + additionalMetadata.bytes - 2 + (previous.tools.length === 0 ? 0 : 1);
	if (catalogBytes > capturedLimits.maxCatalogBytes) throw new ToolSnapshotLimitError("tool_catalog_too_large");
	const snapshot = Object.freeze({
		tools: Object.freeze([...previous.tools, ...additional.tools]),
		descriptors: Object.freeze([...previous.descriptors, ...additional.descriptors]),
	});
	catalogMetadata.set(
		snapshot,
		Object.freeze({
			maxTools: capturedLimits.maxTools,
			maxCatalogBytes: capturedLimits.maxCatalogBytes,
			maxSchemaDepth: capturedLimits.maxSchemaDepth,
			bytes: catalogBytes,
		}),
	);
	return snapshot;
}

/** Apply one host policy decision while preserving the candidate catalog's deterministic order. */
export function selectVisibleToolSnapshot(
	snapshot: PreparedToolSnapshot,
	decision: Readonly<GameToolPolicyDecision>,
	maxPolicyRevisionBytes: number,
): VisibleToolSnapshot {
	if (!Number.isSafeInteger(maxPolicyRevisionBytes) || maxPolicyRevisionBytes <= 0) {
		throw new RangeError("maxPolicyRevisionBytes must be a positive safe integer.");
	}
	if (typeof decision !== "object" || decision === null || Array.isArray(decision)) {
		throw new InvalidToolPolicyError("The Tool policy must return a decision object.");
	}
	const revisionValue: unknown = decision.revision;
	const visibleNamesValue: unknown = decision.visibleToolNames;
	if (typeof revisionValue !== "string" || revisionValue.length === 0 || hasControlCharacter(revisionValue)) {
		throw new InvalidToolPolicyError("The Tool policy revision must be a non-empty opaque identifier.");
	}
	if (utf8Bytes(revisionValue) > maxPolicyRevisionBytes) {
		throw new ToolSnapshotLimitError("tool_policy_revision_too_large");
	}
	if (!Array.isArray(visibleNamesValue)) {
		throw new InvalidToolPolicyError("The Tool policy visible names must be an array.");
	}
	const metadata = catalogMetadata.get(snapshot);
	if (metadata === undefined) {
		throw new InvalidToolSnapshotError("The Tool snapshot was not prepared here.");
	}
	const visibleNameCount = captureArrayLength(
		visibleNamesValue,
		metadata.maxTools,
		() => new InvalidToolPolicyError("The Tool policy selected too many candidates."),
	);
	const candidates = new Set(snapshot.descriptors.map((descriptor) => descriptor.name));
	const requested = new Set<string>();
	for (let index = 0; index < visibleNameCount; index += 1) {
		const name: unknown = visibleNamesValue[index];
		if (typeof name !== "string" || !candidates.has(name)) {
			throw new InvalidToolPolicyError("The Tool policy selected an unknown candidate.");
		}
		requested.add(name);
	}
	const visibleIndexes = snapshot.descriptors.flatMap((descriptor, index) =>
		requested.has(descriptor.name) ? [index] : [],
	);
	const tools = Object.freeze(visibleIndexes.map((index) => snapshot.tools[index] as AgentTool));
	const descriptors = Object.freeze(
		visibleIndexes.map((index) => snapshot.descriptors[index] as Readonly<GameToolDescriptor>),
	);
	const visibleToolNames = Object.freeze(descriptors.map((descriptor) => descriptor.name));
	const revision = createHash("sha256")
		.update(
			canonicalizeJsonValue({
				policyRevision: revisionValue,
				toolFingerprints: descriptors.map((descriptor) => descriptor.fingerprint),
			}),
		)
		.digest("hex");
	return Object.freeze({
		tools,
		descriptors,
		policyRevision: revisionValue,
		revision,
		visibleToolNames,
	});
}

export function validateToolSnapshotLimits(limits: Readonly<GameToolSnapshotLimits>): Readonly<GameToolSnapshotLimits> {
	if (typeof limits !== "object" || limits === null || Array.isArray(limits)) {
		throw new TypeError("Tool snapshot limits are required.");
	}
	const names = [
		"maxTools",
		"maxCatalogBytes",
		"maxSchemaDepth",
		"maxRequestSources",
		"maxPolicyRevisionBytes",
		"maxArgumentsBytes",
		"maxArgumentsDepth",
		"maxResultContentParts",
		"maxResultBytes",
		"maxResultDepth",
		"maxResultUpdates",
	] as const;
	const snapshot = {} as Record<(typeof names)[number], number>;
	for (const name of names) {
		const value: unknown = limits[name];
		if (!Number.isSafeInteger(value) || (value as number) <= 0) {
			throw new RangeError(`${name} must be a positive safe integer.`);
		}
		snapshot[name] = value as number;
	}
	validateToolResultLimits(snapshot);
	return Object.freeze(snapshot) as unknown as Readonly<GameToolSnapshotLimits>;
}

/** Bind a behavior or policy artifact to the exact model-visible Tool contract without retaining executable code. */
export function createToolDescriptorFingerprint(tool: AgentTool, limits: Readonly<GameToolCatalogLimits>): string {
	const descriptor = prepareToolCatalogSnapshot([tool], limits).descriptors[0];
	if (!descriptor) throw new InvalidToolSnapshotError("The Tool descriptor is unavailable.");
	return descriptor.fingerprint;
}

function validateToolCatalogLimits(limits: Readonly<GameToolCatalogLimits>): Readonly<GameToolCatalogLimits> {
	if (typeof limits !== "object" || limits === null || Array.isArray(limits)) {
		throw new TypeError("Tool catalog limits are required.");
	}
	const names = ["maxTools", "maxCatalogBytes", "maxSchemaDepth"] as const;
	const snapshot = {} as Record<(typeof names)[number], number>;
	for (const name of names) {
		const value: unknown = limits[name];
		if (!Number.isSafeInteger(value) || (value as number) <= 0) {
			throw new RangeError(`${name} must be a positive safe integer.`);
		}
		snapshot[name] = value as number;
	}
	return Object.freeze(snapshot);
}

function fingerprintCanonicalDescriptor(canonicalDescriptor: string): string {
	return createHash("sha256").update(canonicalDescriptor).digest("hex");
}

function captureToolArrayElements(tools: readonly AgentTool[], maximum: number): readonly AgentTool[] {
	if (!Array.isArray(tools)) {
		throw new InvalidToolSnapshotError("The tool source must return an array.");
	}
	const length = captureArrayLength(tools, maximum, () => new ToolSnapshotLimitError("tool_count_exceeded"));
	const captured: AgentTool[] = [];
	for (let index = 0; index < length; index += 1) {
		captured.push(tools[index] as AgentTool);
	}
	return Object.freeze(captured);
}

interface CapturedTool {
	readonly name: string;
	readonly label: string;
	readonly description: string;
	readonly parameters: unknown;
	readonly executionMode: "parallel" | "sequential" | undefined;
	readonly prepareArguments: AgentTool["prepareArguments"] | undefined;
	readonly prepareDurableExecution: NonNullable<DurableExecutionAgentTool[typeof DURABLE_TOOL_EXECUTION]> | undefined;
	readonly execute: AgentTool["execute"];
}

function captureTool(tool: AgentTool, names: Set<string>): CapturedTool {
	if (typeof tool !== "object" || tool === null || Array.isArray(tool)) {
		throw new InvalidToolSnapshotError("Every tool must be an object.");
	}
	const name: unknown = tool.name;
	const label: unknown = tool.label;
	const description: unknown = tool.description;
	const parameters: unknown = tool.parameters;
	const execute: unknown = tool.execute;
	const executionMode: unknown = tool.executionMode;
	const prepareArguments: unknown = tool.prepareArguments;
	const prepareDurableExecution: unknown = (tool as DurableExecutionAgentTool)[DURABLE_TOOL_EXECUTION];
	if (typeof name !== "string" || name.length === 0 || hasControlCharacter(name)) {
		throw new InvalidToolSnapshotError("Every tool must have a non-empty opaque name.");
	}
	if (names.has(name)) throw new InvalidToolSnapshotError(`Tool names must be unique: ${name}`);
	names.add(name);
	if (typeof description !== "string" || typeof label !== "string") {
		throw new InvalidToolSnapshotError(`Tool ${name} must have a description and label.`);
	}
	if (typeof execute !== "function") {
		throw new InvalidToolSnapshotError(`Tool ${name} must have an execute function.`);
	}
	if (executionMode !== undefined && executionMode !== "parallel" && executionMode !== "sequential") {
		throw new InvalidToolSnapshotError(`Tool ${name} has an invalid execution mode.`);
	}
	if (prepareArguments !== undefined && typeof prepareArguments !== "function") {
		throw new InvalidToolSnapshotError(`Tool ${name} has an invalid argument preparer.`);
	}
	if (prepareDurableExecution !== undefined && typeof prepareDurableExecution !== "function") {
		throw new InvalidToolSnapshotError(`Tool ${name} has an invalid durable execution hook.`);
	}
	if (typeof prepareDurableExecution === "function" && executionMode === "parallel") {
		throw new InvalidToolSnapshotError(`Tool ${name} cannot combine durable execution with parallel mode.`);
	}
	return {
		name,
		label,
		description,
		parameters,
		executionMode: executionMode as CapturedTool["executionMode"],
		prepareArguments:
			typeof prepareArguments === "function"
				? (prepareArguments.bind(tool) as AgentTool["prepareArguments"])
				: undefined,
		prepareDurableExecution:
			typeof prepareDurableExecution === "function"
				? (prepareDurableExecution.bind(tool) as NonNullable<DurableExecutionAgentTool[typeof DURABLE_TOOL_EXECUTION]>)
				: undefined,
		execute: execute.bind(tool) as AgentTool["execute"],
	};
}

function validateNormalizedSchema(toolName: string, schema: JsonValue): void {
	if (typeof schema !== "object" || schema === null || Array.isArray(schema) || schema.type !== "object") {
		throw new InvalidToolSnapshotError(`Tool ${toolName} must have an object parameter schema.`);
	}
	if (!schemaValidator.validateSchema(schema as AnySchema)) {
		throw new InvalidToolSnapshotError(
			`Tool ${toolName} has an invalid parameter schema: ${schemaValidator.errorsText()}`,
		);
	}
}

function normalizeProviderJson(value: unknown, limits: Readonly<GameToolCatalogLimits>): JsonValue {
	try {
		return normalizeJsonValueWithin(value, {
			maxDepth: limits.maxSchemaDepth,
			maxBytes: limits.maxCatalogBytes,
		});
	} catch (error) {
		if (error instanceof JsonValueLimitError) {
			throw new ToolSnapshotLimitError(
				error.code === "json_too_deep" ? "tool_schema_too_deep" : "tool_catalog_too_large",
			);
		}
		throw error;
	}
}

function captureArrayLength(value: readonly unknown[], maximum: number, limitError: () => Error): number {
	const length: unknown = value.length;
	if (!Number.isSafeInteger(length) || (length as number) < 0) {
		throw new TypeError("Array length must be a non-negative safe integer.");
	}
	if ((length as number) > maximum) throw limitError();
	return length as number;
}

function freezeJsonValue(value: JsonValue): void {
	const pending: Array<JsonValue[] | { [key: string]: JsonValue }> = [];
	if (typeof value === "object" && value !== null) pending.push(value);
	while (pending.length > 0) {
		const current = pending.pop();
		if (!current || Object.isFrozen(current)) continue;
		for (const child of Array.isArray(current) ? current : Object.values(current)) {
			if (typeof child === "object" && child !== null) pending.push(child);
		}
		Object.freeze(current);
	}
}

function utf8Bytes(value: string): number {
	return new TextEncoder().encode(value).byteLength;
}

function hasControlCharacter(value: string): boolean {
	for (let index = 0; index < value.length; index += 1) {
		const code = value.charCodeAt(index);
		if (code <= 0x1f || (code >= 0x7f && code <= 0x9f)) return true;
	}
	return false;
}

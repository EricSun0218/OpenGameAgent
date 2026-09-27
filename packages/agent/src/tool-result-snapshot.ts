import { Buffer, constants as bufferConstants } from "node:buffer";
import type { AgentToolResult } from "@earendil-works/pi-agent-core";
import type { ImageContent, TextContent, Usage } from "@earendil-works/pi-ai";
import {
	canonicalizeJsonValue,
	type JsonValue,
	JsonValueLimitError,
	normalizeJsonValueWithin,
} from "@opengameagent/protocol";
import type { GameToolSnapshotLimits } from "./types.js";

const FAILURE_MESSAGES = [
	"tool_arguments_invalid",
	"tool_execution_denied",
	"tool_execution_failed",
	"tool_policy_changed",
	"tool_result_invalid",
	"tool_result_limit_exceeded",
	"tool_run_inactive",
] as const;
const EMPTY_TEXT_RESULT_BYTES = toolResultEnvelopeBytes("");

export class GameToolResultError extends Error {
	constructor(
		public readonly code: (typeof FAILURE_MESSAGES)[number],
		options?: ErrorOptions,
	) {
		super(code, options);
		this.name = "GameToolResultError";
	}
}

/** A Tool-declared failure whose complete result should remain available to Pi for self-correction. */
export class GameToolReportedError<T = unknown> extends Error {
	constructor(
		public readonly result: AgentToolResult<T>,
		message = "tool_reported_error",
	) {
		super(message);
		this.name = "GameToolReportedError";
	}
}

/** Object -> content array -> content part -> primitive leaf. */
export const MIN_TOOL_RESULT_DEPTH = 4;

/** Exact UTF-8 size of the largest stable OGA boundary error Pi may retain. */
export const MIN_TOOL_RESULT_BYTES = Math.max(...FAILURE_MESSAGES.map((message) => toolErrorEnvelopeBytes(message)));

/** Worst-case canonical growth when a host-bounded UTF-8 string is embedded in JSON. */
export function textToolResultExpansionBytes(maximumTextBytes: number): number {
	if (!Number.isSafeInteger(maximumTextBytes) || maximumTextBytes < 1) {
		throw new RangeError("maximumTextBytes must be a positive safe integer.");
	}
	const expansion = maximumTextBytes * 6;
	if (!Number.isSafeInteger(expansion))
		throw new RangeError("The derived Tool result expansion is not a safe integer.");
	return expansion;
}

/** Derive a complete canonical content-array budget for bounded text split across host-bounded parts. */
export function textToolResultContentBytes(maximumTextBytes: number, maximumParts: number): number {
	if (!Number.isSafeInteger(maximumParts) || maximumParts < 1) {
		throw new RangeError("maximumParts must be a positive safe integer.");
	}
	const textBytes = textToolResultExpansionBytes(maximumTextBytes);
	const emptyPartBytes = Buffer.byteLength(canonicalizeJsonValue({ type: "text", text: "" }), "utf8");
	const partBytes = emptyPartBytes * maximumParts;
	const punctuationBytes = maximumParts + 1;
	const total = textBytes + partBytes + punctuationBytes;
	if (!Number.isSafeInteger(total) || total > bufferConstants.MAX_STRING_LENGTH) {
		throw new RangeError("The derived Tool result content admission exceeds the host string allocation limit.");
	}
	return total;
}

/** Derive final admission for a text Tool with a fixed empty-text result envelope. */
export function requiredTextToolResultBytes(
	maximumTextBytes: number,
	emptyTextResultBytes = EMPTY_TEXT_RESULT_BYTES,
): number {
	if (!Number.isSafeInteger(emptyTextResultBytes) || emptyTextResultBytes < EMPTY_TEXT_RESULT_BYTES) {
		throw new RangeError("emptyTextResultBytes must describe a complete Tool result envelope.");
	}
	const required = textToolResultExpansionBytes(maximumTextBytes) + emptyTextResultBytes;
	if (!Number.isSafeInteger(required)) throw new RangeError("The derived Tool result admission is not a safe integer.");
	return required;
}

export function validateToolResultLimits(
	limits: Readonly<GameToolSnapshotLimits>,
): Pick<
	Readonly<GameToolSnapshotLimits>,
	"maxResultContentParts" | "maxResultBytes" | "maxResultDepth" | "maxResultUpdates"
> {
	if (limits.maxResultDepth < MIN_TOOL_RESULT_DEPTH) {
		throw new RangeError(`maxResultDepth must be at least the derived result envelope depth ${MIN_TOOL_RESULT_DEPTH}.`);
	}
	if (limits.maxResultBytes < MIN_TOOL_RESULT_BYTES) {
		throw new RangeError(
			`maxResultBytes must admit the derived stable Tool error envelope (${MIN_TOOL_RESULT_BYTES} bytes).`,
		);
	}
	if (limits.maxResultBytes > bufferConstants.MAX_STRING_LENGTH) {
		throw new RangeError("maxResultBytes exceeds the host string allocation limit.");
	}
	return Object.freeze({
		maxResultContentParts: limits.maxResultContentParts,
		maxResultBytes: limits.maxResultBytes,
		maxResultDepth: limits.maxResultDepth,
		maxResultUpdates: limits.maxResultUpdates,
	});
}

/** Capture the Pi result fields once as detached, deeply frozen, host-bounded data. */
export function prepareToolResultSnapshot(
	result: unknown,
	limits: Readonly<GameToolSnapshotLimits>,
): Readonly<AgentToolResult<JsonValue | undefined>> {
	return prepareToolResult(result, limits);
}

/** Capture a result for a text-content transformer while retaining metadata under the final whole-result budget. */
export function prepareTransformableToolResultSnapshot(
	result: unknown,
	limits: Readonly<GameToolSnapshotLimits>,
	maximumTextBytes: number,
): Readonly<AgentToolResult<JsonValue | undefined>> {
	const transformedContentBytes = textToolResultContentBytes(maximumTextBytes, limits.maxResultContentParts);
	return prepareToolResult(result, limits, Math.max(limits.maxResultBytes, transformedContentBytes));
}

function prepareToolResult(
	result: unknown,
	limits: Readonly<GameToolSnapshotLimits>,
	maximumContentBytes?: number,
): Readonly<AgentToolResult<JsonValue | undefined>> {
	try {
		validateToolResultLimits(limits);
		const source = resultRecord(result);
		const contentValue = readField(source, "content");
		const detailsValue = readField(source, "details");
		const usageValue = readField(source, "usage");
		const addedToolNamesValue = readField(source, "addedToolNames");
		const terminateValue = readField(source, "terminate");
		const content = captureContent(contentValue, limits);
		const candidate = {
			content,
			...(detailsValue === undefined ? {} : { details: detailsValue }),
			...(usageValue === undefined ? {} : { usage: captureUsage(usageValue) }),
			...(addedToolNamesValue === undefined
				? {}
				: { addedToolNames: captureAddedToolNames(addedToolNamesValue, limits.maxTools) }),
			...(terminateValue === undefined ? {} : { terminate: captureBoolean(terminateValue) }),
		};
		const normalized =
			maximumContentBytes === undefined
				? (normalizeJsonValueWithin(candidate, {
						maxBytes: limits.maxResultBytes,
						maxDepth: limits.maxResultDepth,
					}) as Record<string, JsonValue>)
				: normalizeTransformableResult(candidate, content, limits, maximumContentBytes);
		const snapshot = {
			...normalized,
			details: detailsValue === undefined ? undefined : normalized.details,
		} as unknown as AgentToolResult<JsonValue | undefined>;
		return freezeSnapshot(snapshot);
	} catch (error) {
		if (error instanceof GameToolResultError) throw error;
		throw new GameToolResultError(
			error instanceof JsonValueLimitError ? "tool_result_limit_exceeded" : "tool_result_invalid",
			{ cause: error },
		);
	}
}

function normalizeTransformableResult(
	candidate: Record<string, unknown>,
	content: ReadonlyArray<Readonly<TextContent | ImageContent>>,
	limits: Readonly<GameToolSnapshotLimits>,
	maximumContentBytes: number,
): Record<string, JsonValue> {
	const retained = normalizeJsonValueWithin(
		{ ...candidate, content: [] },
		{
			maxBytes: limits.maxResultBytes,
			maxDepth: limits.maxResultDepth,
		},
	) as Record<string, JsonValue>;
	const normalizedContent = normalizeJsonValueWithin(content, {
		maxBytes: maximumContentBytes,
		maxDepth: limits.maxResultDepth - 1,
	});
	return { ...retained, content: normalizedContent };
}

/** Preserve useful Tool failures while bounding the exact Pi error envelope retained by the Agent. */
export function boundedToolExecutionError(error: unknown, maxResultBytes: number): Error {
	if (!Number.isSafeInteger(maxResultBytes) || maxResultBytes < MIN_TOOL_RESULT_BYTES) {
		throw new RangeError("maxResultBytes cannot admit the stable Tool execution failure.");
	}
	if (error instanceof GameToolResultError) return new GameToolResultError(error.code, { cause: error });
	let message = "tool_execution_failed";
	try {
		message = error instanceof Error ? String(error.message) : String(error);
	} catch {
		// The fallback is an OGA-owned stable boundary error.
	}
	return new Error(truncateToolErrorMessage(message, maxResultBytes), { cause: error });
}

function captureContent(
	value: unknown,
	limits: Readonly<GameToolSnapshotLimits>,
): ReadonlyArray<Readonly<TextContent | ImageContent>> {
	if (value === undefined) return [];
	if (!Array.isArray(value)) throw new GameToolResultError("tool_result_invalid");
	const length = captureArrayLength(value);
	if (length > limits.maxResultContentParts) throw new GameToolResultError("tool_result_limit_exceeded");
	const content: Array<TextContent | ImageContent> = [];
	for (let index = 0; index < length; index += 1) {
		const part = resultRecord(value[index]);
		const type = readField(part, "type");
		if (type === "text") {
			const text = readField(part, "text");
			if (typeof text !== "string") throw new GameToolResultError("tool_result_invalid");
			content.push({ type: "text", text });
			continue;
		}
		if (type === "image") {
			const data = readField(part, "data");
			const mimeType = readField(part, "mimeType");
			if (typeof data !== "string" || typeof mimeType !== "string") {
				throw new GameToolResultError("tool_result_invalid");
			}
			content.push({ type: "image", data, mimeType });
			continue;
		}
		throw new GameToolResultError("tool_result_invalid");
	}
	return content;
}

function captureUsage(value: unknown): Usage {
	const source = resultRecord(value);
	const costSource = resultRecord(readField(source, "cost"));
	const usage: Usage = {
		input: finiteNumber(readField(source, "input")),
		output: finiteNumber(readField(source, "output")),
		cacheRead: finiteNumber(readField(source, "cacheRead")),
		cacheWrite: finiteNumber(readField(source, "cacheWrite")),
		totalTokens: finiteNumber(readField(source, "totalTokens")),
		cost: {
			input: finiteNumber(readField(costSource, "input")),
			output: finiteNumber(readField(costSource, "output")),
			cacheRead: finiteNumber(readField(costSource, "cacheRead")),
			cacheWrite: finiteNumber(readField(costSource, "cacheWrite")),
			total: finiteNumber(readField(costSource, "total")),
		},
	};
	const cacheWrite1h = readField(source, "cacheWrite1h");
	if (cacheWrite1h !== undefined) usage.cacheWrite1h = finiteNumber(cacheWrite1h);
	const reasoning = readField(source, "reasoning");
	if (reasoning !== undefined) usage.reasoning = finiteNumber(reasoning);
	return usage;
}

function captureAddedToolNames(value: unknown, maximum: number): string[] {
	if (!Array.isArray(value)) throw new GameToolResultError("tool_result_invalid");
	const length = captureArrayLength(value);
	if (length > maximum) throw new GameToolResultError("tool_result_limit_exceeded");
	const names: string[] = [];
	for (let index = 0; index < length; index += 1) {
		const name = value[index];
		if (typeof name !== "string") throw new GameToolResultError("tool_result_invalid");
		names.push(name);
	}
	return names;
}

function captureArrayLength(value: unknown[]): number {
	const length = value.length;
	if (!Number.isSafeInteger(length) || length < 0) throw new GameToolResultError("tool_result_invalid");
	return length;
}

function resultRecord(value: unknown): Record<PropertyKey, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value)) {
		throw new GameToolResultError("tool_result_invalid");
	}
	return value as Record<PropertyKey, unknown>;
}

function readField(value: Record<PropertyKey, unknown>, key: string): unknown {
	return value[key];
}

function captureBoolean(value: unknown): boolean {
	if (typeof value !== "boolean") throw new GameToolResultError("tool_result_invalid");
	return value;
}

function finiteNumber(value: unknown): number {
	if (typeof value !== "number" || !Number.isFinite(value)) throw new GameToolResultError("tool_result_invalid");
	return value;
}

function truncateToolErrorMessage(message: string, maxResultBytes: number): string {
	if (toolErrorEnvelopeBytes(message) <= maxResultBytes) return message;
	let lower = 0;
	let upper = message.length;
	while (lower < upper) {
		const middle = Math.ceil((lower + upper) / 2);
		const candidate = safePrefix(message, middle);
		if (toolErrorEnvelopeBytes(candidate) <= maxResultBytes) lower = middle;
		else upper = middle - 1;
	}
	return safePrefix(message, lower);
}

function safePrefix(value: string, length: number): string {
	let end = Math.min(length, value.length);
	if (end > 0) {
		const code = value.charCodeAt(end - 1);
		if (code >= 0xd800 && code <= 0xdbff) end -= 1;
	}
	return value.slice(0, end);
}

function toolErrorEnvelopeBytes(message: string): number {
	return toolResultEnvelopeBytes(message);
}

function toolResultEnvelopeBytes(text: string): number {
	return Buffer.byteLength(canonicalizeJsonValue({ content: [{ type: "text", text }], details: {} }), "utf8");
}

function freezeSnapshot<T>(value: T): Readonly<T> {
	const pending: object[] = [];
	if (typeof value === "object" && value !== null) pending.push(value);
	while (pending.length > 0) {
		const current = pending.pop();
		if (!current || Object.isFrozen(current)) continue;
		for (const entry of Object.values(current)) {
			if (typeof entry === "object" && entry !== null) pending.push(entry);
		}
		Object.freeze(current);
	}
	return value;
}

import { isProxy } from "node:util/types";
import type { NormalizedGameInput } from "@opengameagent/agent";
import {
	assertEventStreamCoordinate,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	canonicalizeJsonValueWithin,
	type JsonValue,
	normalizeJsonValue,
	normalizeJsonValueWithin,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { byteLength } from "./artifact-validation.js";
import { KnowledgeSourceResultError, KnowledgeValidationError } from "./knowledge-errors.js";
import type { KnowledgeItem, KnowledgeLimits, KnowledgeSource } from "./knowledge-types.js";

const NODE_MAX_TIMER_DELAY_MS = 2_147_483_647;
const ABSENT = Symbol("absent");
const limitNames = [
	"maxIdentifierBytes",
	"maxCounterDigits",
	"maxSources",
	"maxSourceDescriptionBytes",
	"maxToolDescriptionBytes",
	"maxQueryBytes",
	"maxItemsPerQuery",
	"maxTitleBytes",
	"maxSummaryBytes",
	"maxUriBytes",
	"maxMetadataEntries",
	"maxMetadataKeyBytes",
	"maxMetadataValueBytes",
	"maxItemBytes",
	"maxResultBytes",
	"maxJsonDepth",
	"maxConcurrentQueriesPerSource",
	"maxQueryDurationMs",
] as const satisfies readonly (keyof KnowledgeLimits)[];

export function freezeKnowledgeLimits(limits: KnowledgeLimits): Readonly<KnowledgeLimits> {
	const copy = {} as Record<keyof KnowledgeLimits, number>;
	for (const name of limitNames) {
		const value = limits[name];
		if (!Number.isSafeInteger(value) || value <= 0) {
			throw new KnowledgeValidationError(`${name} must be a positive safe integer.`);
		}
		copy[name] = value;
	}
	if (copy.maxQueryDurationMs > NODE_MAX_TIMER_DELAY_MS) {
		throw new KnowledgeValidationError("maxQueryDurationMs exceeds the Node timer range.");
	}
	if (copy.maxJsonDepth > Number.MAX_SAFE_INTEGER - 2) {
		throw new KnowledgeValidationError("maxJsonDepth cannot admit the derived Knowledge result envelope.");
	}
	if (copy.maxItemBytes > copy.maxResultBytes) {
		throw new KnowledgeValidationError("maxItemBytes exceeds maxResultBytes.");
	}
	return Object.freeze(copy);
}

export function normalizeKnowledgeSources(
	sources: readonly KnowledgeSource[],
	limits: Readonly<KnowledgeLimits>,
): readonly KnowledgeSource[] {
	if (!Array.isArray(sources)) {
		throw new KnowledgeValidationError("Knowledge sources exceed the configured source count.");
	}
	const length: unknown = sources.length;
	if (!Number.isSafeInteger(length) || (length as number) < 1 || (length as number) > limits.maxSources) {
		throw new KnowledgeValidationError("Knowledge sources exceed the configured source count.");
	}
	const sourceIds = new Set<string>();
	const normalized: KnowledgeSource[] = [];
	for (let index = 0; index < (length as number); index += 1) {
		const source = sources[index];
		if (typeof source !== "object" || source === null) {
			throw new KnowledgeValidationError("Knowledge source must provide a query function.");
		}
		const sourceIdValue = source.sourceId;
		const descriptionValue = source.description;
		const query = source.query;
		if (typeof query !== "function") {
			throw new KnowledgeValidationError("Knowledge source must provide a query function.");
		}
		const sourceId = validateKnowledgeIdentifier(sourceIdValue, limits, "sourceId");
		if (sourceIds.has(sourceId)) throw new KnowledgeValidationError("Knowledge source ids must be unique.");
		sourceIds.add(sourceId);
		const description = boundedText(descriptionValue, limits.maxSourceDescriptionBytes, "source description");
		normalized.push(Object.freeze({ sourceId, description, query: query.bind(source) }));
	}
	return Object.freeze(normalized);
}

export function validateKnowledgeBinding(
	input: Readonly<NormalizedGameInput>,
	coordinate: Readonly<RunCoordinate>,
	limits: Readonly<KnowledgeLimits>,
): { input: NormalizedGameInput; coordinate: RunCoordinate } {
	assertEventStreamCoordinate({ sessionId: input.sessionId, actorId: input.actorId }, coordinate);
	validateKnowledgeIdentifier(input.sessionId, limits, "sessionId");
	validateKnowledgeIdentifier(input.actorId, limits, "actorId");
	validateKnowledgeIdentifier(input.inputId, limits, "inputId");
	validateKnowledgeIdentifier(coordinate.runId, limits, "runId");
	if (coordinate.turn.length > limits.maxCounterDigits) {
		throw new KnowledgeValidationError("turn exceeds the configured digit limit.");
	}
	return {
		input: {
			sessionId: input.sessionId,
			actorId: input.actorId,
			inputId: input.inputId,
			payload: normalizeJsonValue(input.payload),
		},
		coordinate: { ...coordinate },
	};
}

export function validateKnowledgeIdentifier(
	value: unknown,
	limits: Pick<KnowledgeLimits, "maxIdentifierBytes">,
	name: string,
): string {
	if (typeof value !== "string") throw new KnowledgeValidationError(`${name} must be an identifier.`);
	try {
		assertOpaqueIdentifier(value);
	} catch (error) {
		throw new KnowledgeValidationError(`${name} must be an identifier.`, { cause: error });
	}
	if (byteLength(value) > limits.maxIdentifierBytes) {
		throw new KnowledgeValidationError(`${name} exceeds the configured identifier byte limit.`);
	}
	return value;
}

export function normalizeKnowledgeQuery(value: unknown, limits: Readonly<KnowledgeLimits>): JsonValue {
	try {
		return normalizeJsonValueWithin(value, {
			maxBytes: limits.maxQueryBytes,
			maxDepth: limits.maxJsonDepth,
		});
	} catch (error) {
		throw new KnowledgeValidationError("Knowledge query must be strict JSON.", { cause: error });
	}
}

export function normalizeKnowledgeItems(
	value: unknown,
	requestedItems: number,
	limits: Readonly<KnowledgeLimits>,
): readonly KnowledgeItem[] {
	try {
		if (!Array.isArray(value) || isProxy(value)) {
			throw new TypeError();
		}
		const length = value.length;
		if (length > requestedItems || length > limits.maxItemsPerQuery) throw new TypeError();
		const itemIds = new Set<string>();
		const items: KnowledgeItem[] = [];
		for (let index = 0; index < length; index += 1) {
			const descriptor = Object.getOwnPropertyDescriptor(value, index);
			if (!descriptor?.enumerable || !("value" in descriptor)) throw new TypeError();
			const candidate = descriptor.value;
			if (!isRecord(candidate)) throw new TypeError();
			const itemId = responseIdentifier(dataProperty(candidate, "itemId"), limits.maxIdentifierBytes);
			if (itemIds.has(itemId)) throw new TypeError();
			itemIds.add(itemId);
			const summary = optionalDataProperty(candidate, "summary");
			const uri = optionalDataProperty(candidate, "uri");
			const metadata = optionalDataProperty(candidate, "metadata");
			const item = normalizeJsonValueWithin(
				{
					itemId,
					title: responseText(dataProperty(candidate, "title"), limits.maxTitleBytes),
					content: dataProperty(candidate, "content"),
					...(summary === ABSENT ? {} : { summary: responseText(summary, limits.maxSummaryBytes) }),
					...(uri === ABSENT ? {} : { uri: responseUri(uri, limits.maxUriBytes) }),
					...(metadata === ABSENT ? {} : { metadata: responseMetadata(metadata, limits) }),
				},
				{ maxBytes: limits.maxItemBytes, maxDepth: limits.maxJsonDepth },
			) as unknown as KnowledgeItem;
			items.push(item);
		}
		return items;
	} catch (error) {
		if (error instanceof KnowledgeSourceResultError) throw error;
		throw new KnowledgeSourceResultError({ cause: error });
	}
}

export function knowledgeResultText(
	sourceId: string,
	items: readonly KnowledgeItem[],
	limits: Readonly<KnowledgeLimits>,
): string {
	return canonicalizeJsonValueWithin(
		{ status: "ok", trust: "external_untrusted", sourceId, items },
		{ maxBytes: limits.maxResultBytes, maxDepth: limits.maxJsonDepth + 2 },
	);
}

export function knowledgeFailureText(reason: KnowledgeFailureReason): string {
	return canonicalizeJsonValue({ status: "knowledge_unavailable", reason, doNotRetry: true });
}

export type KnowledgeFailureReason =
	| "capacity_exceeded"
	| "source_timeout"
	| "source_unavailable"
	| "invalid_source_result"
	| "result_too_large";

function boundedText(value: unknown, maximumBytes: number, name: string): string {
	if (typeof value !== "string" || value.trim().length === 0 || byteLength(value) > maximumBytes) {
		throw new KnowledgeValidationError(`${name} must be bounded non-empty text.`);
	}
	return value;
}

function responseIdentifier(value: unknown, maximumBytes: number): string {
	if (typeof value !== "string") throw new TypeError();
	assertOpaqueIdentifier(value);
	if (byteLength(value) > maximumBytes) throw new TypeError();
	return value;
}

function responseText(value: unknown, maximumBytes: number): string {
	if (typeof value !== "string" || value.trim().length === 0 || byteLength(value) > maximumBytes) throw new TypeError();
	return value;
}

function responseUri(value: unknown, maximumBytes: number): string {
	if (typeof value !== "string" || byteLength(value) > maximumBytes) throw new TypeError();
	const uri = new URL(value);
	if (uri.username !== "" || uri.password !== "") throw new TypeError();
	return uri.toString();
}

function responseMetadata(value: unknown, limits: Readonly<KnowledgeLimits>): Readonly<Record<string, string>> {
	if (!isRecord(value)) throw new TypeError();
	const keys = Object.keys(value);
	if (keys.length > limits.maxMetadataEntries) throw new TypeError();
	const metadata = Object.create(null) as Record<string, string>;
	for (const key of keys.sort((left, right) => left.localeCompare(right))) {
		metadata[responseIdentifier(key, limits.maxMetadataKeyBytes)] = responseText(
			dataProperty(value, key),
			limits.maxMetadataValueBytes,
		);
	}
	return metadata;
}

function isRecord(value: unknown): value is Record<string, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value) || isProxy(value)) return false;
	const prototype = Object.getPrototypeOf(value);
	return prototype === Object.prototype || prototype === null;
}

function dataProperty(value: Record<string, unknown>, name: string): unknown {
	const descriptor = Object.getOwnPropertyDescriptor(value, name);
	if (!descriptor?.enumerable || !("value" in descriptor)) throw new TypeError();
	return descriptor.value;
}

function optionalDataProperty(value: Record<string, unknown>, name: string): unknown | typeof ABSENT {
	const descriptor = Object.getOwnPropertyDescriptor(value, name);
	if (!descriptor) return ABSENT;
	if (!descriptor.enumerable || !("value" in descriptor)) throw new TypeError();
	return descriptor.value;
}

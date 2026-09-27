import { createHash } from "node:crypto";
import {
	type Audience,
	assertCanonicalTimestamp,
	assertGameTime,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type JsonValue,
	JsonValueLimitError,
	normalizeJsonValueWithin,
} from "@opengameagent/protocol";
import type {
	MemoryDraft,
	MemoryGameTime,
	MemoryIndexPartition,
	MemoryLimits,
	MemoryRevisionPatch,
	MemorySelector,
	MemoryViewer,
	MemoryWrite,
} from "./types.js";

const DECIMAL = /^(0|[1-9][0-9]*)$/u;
const MEMORY_LIMIT_NAMES = [
	"maxSearchTextBytes",
	"maxContentBytes",
	"maxTagsPerMemory",
	"maxTagBytes",
	"maxRecipientsPerMemory",
	"maxQueryTextBytes",
	"maxSelectorKinds",
	"maxSelectorTags",
	"maxLexicalCandidates",
	"maxVectorCandidates",
	"maxHydrateCandidates",
	"maxRecallResults",
	"maxIndexBatchSize",
	"maxTrackedIndexPartitions",
	"maxExportPageSize",
	"maxPurgeBatchSize",
] as const satisfies readonly (keyof MemoryLimits)[];

export class MemoryValidationError extends TypeError {
	constructor(public readonly code: string) {
		super(code);
		this.name = "MemoryValidationError";
	}
}

export function validateMemoryLimits(limits: MemoryLimits): MemoryLimits {
	const normalized = {} as { -readonly [Name in keyof MemoryLimits]: number };
	for (const name of MEMORY_LIMIT_NAMES) {
		const value = limits[name];
		if (!Number.isSafeInteger(value) || value < 1) throw new MemoryValidationError(`invalid_limit:${name}`);
		normalized[name] = value;
	}
	return normalized;
}

export function normalizeMemoryWrite(write: MemoryWrite, limits: MemoryLimits): MemoryWrite {
	const memoryId = write.memoryId;
	const partition = normalizePartition(write.partition);
	const kind = write.kind;
	const tags = normalizeTags(write.tags, limits);
	const gameTime = normalizeGameTime(write.gameTime);
	const importance = write.importance;
	const searchText = write.searchText;
	const content = normalizeMemoryContent(write.content, limits.maxContentBytes);
	const audience = normalizeAudience(write.audience, limits);
	const recordedAt = write.recordedAt;
	assertOpaqueIdentifier(memoryId);
	assertOpaqueIdentifier(kind);
	validateImportance(importance);
	validateBytes("search_text", searchText, limits.maxSearchTextBytes);
	validateTimestamp(recordedAt);
	return {
		memoryId,
		partition,
		kind,
		tags,
		gameTime,
		importance,
		searchText,
		content,
		audience,
		recordedAt,
	};
}

export function normalizeRevisionPatch(patch: MemoryRevisionPatch, limits: MemoryLimits): MemoryRevisionPatch {
	const normalized: {
		tags?: readonly string[];
		gameTime?: MemoryGameTime;
		importance?: number;
		searchText?: string;
		content?: JsonValue;
		audience?: Audience;
	} = {};
	const tags = patch.tags;
	if (tags !== undefined) {
		normalized.tags = normalizeTags(tags, limits);
	}
	const gameTime = patch.gameTime;
	if (gameTime !== undefined) normalized.gameTime = normalizeGameTime(gameTime);
	const importance = patch.importance;
	if (importance !== undefined) {
		validateImportance(importance);
		normalized.importance = importance;
	}
	const searchText = patch.searchText;
	if (searchText !== undefined) {
		validateBytes("search_text", searchText, limits.maxSearchTextBytes);
		normalized.searchText = searchText;
	}
	const content = patch.content;
	if (content !== undefined) {
		normalized.content = normalizeMemoryContent(content, limits.maxContentBytes);
	}
	const audience = patch.audience;
	if (audience !== undefined) {
		normalized.audience = normalizeAudience(audience, limits);
	}
	if (Object.keys(normalized).length === 0) throw new MemoryValidationError("empty_revision");
	return normalized;
}

export function normalizeMemorySelector(selector: MemorySelector, limits: MemoryLimits): MemorySelector {
	const partition = normalizeIndexPartition(selector);
	const actorId = selector.actorId;
	const sessionId = selector.sessionId;
	const scope = selector.scope;
	const kinds = selector.kinds;
	const tags = selector.tags;
	const atOrBeforeTick = selector.atOrBeforeTick;
	if (actorId !== undefined) assertOpaqueIdentifier(actorId);
	if (sessionId !== undefined) assertOpaqueIdentifier(sessionId);
	if (scope !== undefined) assertOpaqueIdentifier(scope);
	const normalizedKinds =
		kinds === undefined ? undefined : normalizeSelectorValues(kinds, limits.maxSelectorKinds, "kinds");
	const normalizedTags =
		tags === undefined
			? undefined
			: normalizeSelectorValues(tags, limits.maxSelectorTags, "tags", (tag) =>
					validateBytes("tag", tag, limits.maxTagBytes),
				);
	if (atOrBeforeTick !== undefined) validateDecimal(atOrBeforeTick, "tick");
	return {
		...partition,
		...(actorId === undefined ? {} : { actorId }),
		...(sessionId === undefined ? {} : { sessionId }),
		...(scope === undefined ? {} : { scope }),
		...(normalizedKinds === undefined ? {} : { kinds: normalizedKinds }),
		...(normalizedTags === undefined ? {} : { tags: normalizedTags }),
		...(atOrBeforeTick === undefined ? {} : { atOrBeforeTick }),
	};
}

export function validateMemorySelector(selector: MemorySelector, limits: MemoryLimits): void {
	normalizeMemorySelector(selector, limits);
}

export function normalizeIndexPartition(partition: MemoryIndexPartition): MemoryIndexPartition {
	const ownerId = partition.ownerId;
	const timelineId = partition.timelineId;
	const generation = partition.generation;
	assertOpaqueIdentifier(ownerId);
	assertOpaqueIdentifier(timelineId);
	validateDecimal(generation, "generation");
	return { ownerId, timelineId, generation };
}

export function validateIndexPartition(partition: MemoryIndexPartition): void {
	normalizeIndexPartition(partition);
}

export function normalizeMemoryViewer(viewer: MemoryViewer): MemoryViewer {
	const kind = viewer.kind;
	if (kind === "internal" || kind === "public") return { kind };
	if (kind === "owner") {
		const ownerId = viewer.ownerId;
		assertOpaqueIdentifier(ownerId);
		return { kind, ownerId };
	}
	if (kind === "recipient") {
		const recipientId = viewer.recipientId;
		assertOpaqueIdentifier(recipientId);
		return { kind, recipientId };
	}
	throw new MemoryValidationError("invalid_viewer");
}

export function validateMemoryViewer(viewer: MemoryViewer): void {
	normalizeMemoryViewer(viewer);
}

export function validateResultLimit(value: number, maximum: number, name: string): void {
	if (!Number.isSafeInteger(value) || value < 1 || value > maximum) throw new MemoryValidationError(name);
}

export function validateQuery(query: string, limits: MemoryLimits): void {
	validateBytes("query", query, limits.maxQueryTextBytes);
}

export function validateTimestamp(value: string): void {
	try {
		assertCanonicalTimestamp(value);
	} catch {
		throw new MemoryValidationError("invalid_timestamp");
	}
}

export function validateRevision(value: string): void {
	validateDecimal(value, "revision");
}

export function validateVector(vector: Float32Array, dimensions: number): void {
	validateEmbeddingIdentity("validated-vector", dimensions);
	const length = vector.length;
	if (length !== dimensions) throw new MemoryValidationError("embedding_dimensions");
	for (let index = 0; index < length; index += 1) {
		const value = vector[index];
		if (!Number.isFinite(value)) throw new MemoryValidationError("embedding_value");
	}
}

export function validateEmbeddingIdentity(modelId: string, dimensions: number): void {
	assertOpaqueIdentifier(modelId);
	if (!Number.isSafeInteger(dimensions) || dimensions < 1) {
		throw new MemoryValidationError("embedding_dimensions");
	}
}

export function memoryFingerprint(draft: MemoryDraft): string {
	return createHash("sha256")
		.update(
			canonicalizeJsonValue({
				memoryId: draft.memoryId,
				partition: draft.partition,
				kind: draft.kind,
				tags: draft.tags,
				gameTime: draft.gameTime,
				importance: draft.importance,
				searchText: draft.searchText,
				content: draft.content,
				audience: draft.audience,
			}),
		)
		.digest("hex");
}

export function memoryIndexPartition(draft: Pick<MemoryDraft, "partition" | "gameTime">): MemoryIndexPartition {
	return {
		ownerId: draft.partition.ownerId,
		timelineId: draft.gameTime.timelineId,
		generation: draft.gameTime.generation,
	};
}

function normalizePartition(partition: MemoryDraft["partition"]): MemoryDraft["partition"] {
	const ownerId = partition.ownerId;
	const actorId = partition.actorId;
	const sessionId = partition.sessionId;
	const scope = partition.scope;
	assertOpaqueIdentifier(ownerId);
	assertOpaqueIdentifier(scope);
	if (actorId !== undefined) assertOpaqueIdentifier(actorId);
	if (sessionId !== undefined) assertOpaqueIdentifier(sessionId);
	return {
		ownerId,
		...(actorId === undefined ? {} : { actorId }),
		...(sessionId === undefined ? {} : { sessionId }),
		scope,
	};
}

function normalizeGameTime(gameTime: MemoryGameTime): MemoryGameTime {
	const normalized = {
		timelineId: gameTime.timelineId,
		generation: gameTime.generation,
		tick: gameTime.tick,
	};
	assertGameTime(normalized);
	return normalized;
}

function validateImportance(value: number): void {
	if (!Number.isFinite(value) || value < 0 || value > 1) throw new MemoryValidationError("invalid_importance");
}

function normalizeTags(tags: readonly string[], limits: MemoryLimits): readonly string[] {
	if (!Array.isArray(tags)) throw new MemoryValidationError("invalid_tags");
	const length = tags.length;
	if (length > limits.maxTagsPerMemory) throw new MemoryValidationError("too_many_tags");
	const unique = new Set<string>();
	for (let index = 0; index < length; index += 1) {
		const tag = tags[index];
		if (typeof tag !== "string") throw new MemoryValidationError("invalid_tags");
		assertOpaqueIdentifier(tag);
		validateBytes("tag", tag, limits.maxTagBytes);
		unique.add(tag);
	}
	return [...unique].sort();
}

function normalizeAudience(audience: Audience, limits: MemoryLimits): Audience {
	const visibility = audience.visibility;
	if (visibility === "internal" || visibility === "owner" || visibility === "public") return { visibility };
	if (visibility !== "recipient") throw new MemoryValidationError("invalid_audience");
	const recipientIds = audience.recipientIds;
	if (!Array.isArray(recipientIds)) throw new MemoryValidationError("invalid_audience");
	const length = recipientIds.length;
	if (length < 1) throw new MemoryValidationError("invalid_audience");
	if (length > limits.maxRecipientsPerMemory) throw new MemoryValidationError("too_many_recipients");
	const unique = new Set<string>();
	for (let index = 0; index < length; index += 1) {
		const recipientId = recipientIds[index];
		if (typeof recipientId !== "string") throw new MemoryValidationError("invalid_audience");
		assertOpaqueIdentifier(recipientId);
		unique.add(recipientId);
	}
	return { visibility, recipientIds: [...unique].sort() };
}

function validateBytes(name: string, value: string, maximum: number): void {
	const length = Buffer.byteLength(value, "utf8");
	if (length < 1 || length > maximum) throw new MemoryValidationError(`invalid_${name}_bytes`);
}

function validateDecimal(value: string, name: string): void {
	if (!DECIMAL.test(value)) throw new MemoryValidationError(`invalid_${name}`);
}

function normalizeSelectorValues(
	values: readonly string[],
	maximum: number,
	name: string,
	validate?: (value: string) => void,
): readonly string[] {
	if (!Array.isArray(values)) throw new MemoryValidationError(`invalid_selector_${name}`);
	const length = values.length;
	if (length > maximum) throw new MemoryValidationError(`too_many_selector_${name}`);
	const unique = new Set<string>();
	for (let index = 0; index < length; index += 1) {
		const value = values[index];
		if (typeof value !== "string") throw new MemoryValidationError(`invalid_selector_${name}`);
		assertOpaqueIdentifier(value);
		validate?.(value);
		unique.add(value);
	}
	return [...unique].sort();
}

export function normalizeMemoryContent(content: JsonValue, maximumBytes: number): JsonValue {
	try {
		return normalizeJsonValueWithin(content, {
			maxBytes: maximumBytes,
			maxDepth: Math.ceil(maximumBytes / 2),
		});
	} catch (error) {
		if (error instanceof JsonValueLimitError) throw new MemoryValidationError("invalid_content_bytes");
		throw error;
	}
}

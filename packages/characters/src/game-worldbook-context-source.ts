import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type { GameContextContribution, NormalizedGameInput } from "@opengameagent/agent";
import {
	assertOpaqueIdentifier,
	canonicalizeJsonValueWithin,
	type JsonValue,
	JsonValueLimitError,
	normalizeJsonValueWithin,
	raceOperationWithAbort,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { GameWorldbookError } from "./errors.js";
import type {
	ActivatedGameWorldbookEntry,
	GameWorldbookActivation,
	GameWorldbookActivationReason,
	GameWorldbookContextSource,
	GameWorldbookContextSourceOptions,
	GameWorldbookEntryScope,
	GameWorldbookEntryV1,
	GameWorldbookLimits,
	GameWorldbookScanResolver,
	GameWorldbookScanSegment,
	GameWorldbookScanSnapshot,
	GameWorldbookSelection,
	GameWorldbookV1,
	OmittedGameWorldbookEntry,
} from "./types.js";

const WORLD_KEYS = new Set(["worldbookVersion", "worldbookId", "revision", "entries"]);
const ENTRY_KEYS = new Set(["entryId", "revision", "priority", "content", "recursive", "activation", "scope"]);
const CONSTANT_KEYS = new Set(["kind"]);
const KEYWORD_KEYS = new Set(["kind", "phase", "caseSensitive", "primary", "secondary"]);
const SECONDARY_KEYS = new Set(["mode", "keywords"]);
const SCOPE_KEYS = new Set(["actorIds", "requiredTags", "excludedTags"]);
const SCAN_KEYS = new Set(["scanVersion", "revision", "segments", "tags"]);
const SEGMENT_KEYS = new Set(["segmentId", "text"]);
/** Root -> entries -> entry -> activation -> secondary -> keywords -> string. */
const WORLDBOOK_SCHEMA_DEPTH = 7;
/** Root -> segments -> segment -> string. */
const SCAN_SCHEMA_DEPTH = 4;
/** Root -> entries -> entry -> string. */
const CONTEXT_SCHEMA_DEPTH = 4;
/** A SHA-256 digest is 256 bits, represented as two hexadecimal characters per byte. */
const SHA256_HEX_BYTES = 64;
const EMPTY_CONTRIBUTION: Readonly<GameContextContribution> = Object.freeze({ references: Object.freeze([]) });

interface CapturedEntry extends GameWorldbookEntryV1 {
	readonly position: number;
}

interface CapturedWorldbook extends Omit<GameWorldbookV1, "entries"> {
	readonly entries: readonly CapturedEntry[];
}

interface CapturedConfiguration {
	readonly sourceId: string;
	readonly worldbook: CapturedWorldbook;
	readonly limits: Readonly<GameWorldbookLimits>;
	readonly resolveScan: GameWorldbookContextSourceOptions["resolveScan"];
	readonly countTokenUpperBound: GameWorldbookContextSourceOptions["countTokenUpperBound"];
}

interface MatchedEntry {
	readonly entry: CapturedEntry;
	readonly reason: Readonly<GameWorldbookActivationReason>;
}

interface MatchBudget {
	used: number;
	readonly maximum: number;
}

interface GameWorldbookContextBody {
	readonly contextVersion: "1";
	readonly kind: "game-worldbook";
	readonly worldbookId: string;
	readonly worldbookRevision: string;
	readonly scanRevision: string;
	readonly entries: readonly Readonly<{
		readonly entryId: string;
		readonly revision: string;
		readonly content: string;
	}>[];
}

export function createGameWorldbookContextSource(
	options: GameWorldbookContextSourceOptions,
): GameWorldbookContextSource {
	const configuration = captureConfiguration(options);
	const select = async (
		input: Readonly<NormalizedGameInput>,
		signal: AbortSignal,
		coordinate: Readonly<RunCoordinate>,
	): Promise<GameWorldbookSelection> => {
		signal.throwIfAborted();
		const rawScan = await raceOperationWithAbort(() => configuration.resolveScan(input, signal, coordinate), signal);
		signal.throwIfAborted();
		const scan = captureScan(rawScan, configuration.limits);
		const activated = activateEntries(configuration.worldbook, scan, input.actorId, configuration.limits, signal);
		return await admitContext(
			configuration.worldbook,
			scan,
			activated,
			configuration.limits,
			configuration.countTokenUpperBound,
			signal,
		);
	};
	return Object.freeze({
		sourceId: configuration.sourceId,
		provenanceKind: "context" as const,
		select,
		load: async (input: Readonly<NormalizedGameInput>, signal: AbortSignal, coordinate: Readonly<RunCoordinate>) =>
			(await select(input, signal, coordinate)).contribution,
	});
}

function captureConfiguration(options: unknown): CapturedConfiguration {
	try {
		if (!isRecord(options)) throw new TypeError();
		const sourceId = options.sourceId;
		const rawWorldbook = options.worldbook;
		const rawLimits = options.limits;
		const resolveScan = options.resolveScan as GameWorldbookScanResolver;
		const countTokenUpperBound =
			options.countTokenUpperBound as GameWorldbookContextSourceOptions["countTokenUpperBound"];
		if (
			typeof sourceId !== "string" ||
			!isRecord(rawLimits) ||
			typeof resolveScan !== "function" ||
			typeof countTokenUpperBound !== "function"
		) {
			throw new TypeError();
		}
		const limits = captureLimits(rawLimits);
		if (limits.maxIdentifierBytes < SHA256_HEX_BYTES) throw new TypeError();
		assertBoundedIdentifier(sourceId, limits.maxIdentifierBytes);
		return Object.freeze({
			sourceId,
			worldbook: captureWorldbook(rawWorldbook, limits),
			limits,
			resolveScan,
			countTokenUpperBound,
		});
	} catch (error) {
		if (error instanceof GameWorldbookError && error.code !== "invalid_configuration") throw error;
		throw new GameWorldbookError("invalid_configuration");
	}
}

function captureLimits(value: Readonly<Record<string, unknown>>): Readonly<GameWorldbookLimits> {
	return Object.freeze({
		maxIdentifierBytes: positive(value.maxIdentifierBytes),
		maxDefinitionBytes: positive(value.maxDefinitionBytes),
		maxEntries: positive(value.maxEntries),
		maxKeywordsPerEntry: positive(value.maxKeywordsPerEntry),
		maxKeywordBytes: positive(value.maxKeywordBytes),
		maxContentBytes: positive(value.maxContentBytes),
		maxScanSegments: nonnegative(value.maxScanSegments),
		maxScanBytes: nonnegative(value.maxScanBytes),
		maxScanTags: nonnegative(value.maxScanTags),
		maxMatchEvaluations: positive(value.maxMatchEvaluations),
		maxRecursionSteps: positive(value.maxRecursionSteps),
		maxActivatedEntries: positive(value.maxActivatedEntries),
		maxContextBytes: nonnegative(value.maxContextBytes),
		maxContextTokenUpperBound: nonnegative(value.maxContextTokenUpperBound),
	});
}

function captureWorldbook(rawValue: unknown, limits: Readonly<GameWorldbookLimits>): CapturedWorldbook {
	try {
		const value = normalizeJsonValueWithin(rawValue, {
			maxDepth: WORLDBOOK_SCHEMA_DEPTH,
			maxBytes: limits.maxDefinitionBytes,
		});
		if (!isRecord(value) || !hasExactKeys(value, WORLD_KEYS)) throw new TypeError();
		const worldbookVersion = value.worldbookVersion;
		const worldbookId = value.worldbookId;
		const revision = value.revision;
		const rawEntries = value.entries;
		if (worldbookVersion !== "1" || typeof worldbookId !== "string" || typeof revision !== "string") {
			throw new TypeError();
		}
		assertBoundedIdentifier(worldbookId, limits.maxIdentifierBytes, "worldbook_capacity");
		assertBoundedIdentifier(revision, limits.maxIdentifierBytes, "worldbook_capacity");
		if (!Array.isArray(rawEntries)) throw new TypeError();
		const entryCount = rawEntries.length;
		if (entryCount > limits.maxEntries) throw new GameWorldbookError("worldbook_capacity");
		const entryIds = new Set<string>();
		const entries: CapturedEntry[] = [];
		for (let position = 0; position < entryCount; position += 1) {
			const captured = captureEntry(rawEntries[position], position, limits);
			if (entryIds.has(captured.entryId)) throw new TypeError();
			entryIds.add(captured.entryId);
			entries.push(captured);
		}
		const captured = Object.freeze({
			worldbookVersion,
			worldbookId,
			revision,
			entries: Object.freeze(entries),
		}) satisfies CapturedWorldbook;
		return captured;
	} catch (error) {
		if (error instanceof GameWorldbookError) throw error;
		if (error instanceof JsonValueLimitError && error.code === "json_too_large") {
			throw new GameWorldbookError("worldbook_capacity");
		}
		throw new GameWorldbookError("invalid_worldbook");
	}
}

function captureEntry(value: unknown, position: number, limits: Readonly<GameWorldbookLimits>): CapturedEntry {
	if (!isRecord(value) || !hasExactKeys(value, ENTRY_KEYS)) throw new TypeError();
	const entryId = value.entryId;
	const revision = value.revision;
	const priority = value.priority;
	const content = value.content;
	const recursive = value.recursive;
	const activation = value.activation;
	const scope = value.scope;
	if (
		typeof entryId !== "string" ||
		typeof revision !== "string" ||
		!Number.isSafeInteger(priority) ||
		typeof content !== "string" ||
		content.length === 0 ||
		typeof recursive !== "boolean"
	) {
		throw new TypeError();
	}
	assertBoundedIdentifier(entryId, limits.maxIdentifierBytes, "worldbook_capacity");
	assertBoundedIdentifier(revision, limits.maxIdentifierBytes, "worldbook_capacity");
	if (Buffer.byteLength(content) > limits.maxContentBytes) throw new GameWorldbookError("worldbook_capacity");
	return Object.freeze({
		entryId,
		revision,
		priority: priority as number,
		content,
		recursive,
		activation: captureActivation(activation, limits),
		...(scope === undefined ? {} : { scope: captureScope(scope, limits) }),
		position,
	});
}

function captureActivation(value: unknown, limits: Readonly<GameWorldbookLimits>): Readonly<GameWorldbookActivation> {
	if (!isRecord(value)) throw new TypeError();
	const kind = value.kind;
	if (kind === "constant") {
		if (!hasExactKeys(value, CONSTANT_KEYS)) throw new TypeError();
		return Object.freeze({ kind });
	}
	if (kind !== "keywords" || !hasExactKeys(value, KEYWORD_KEYS)) throw new TypeError();
	const phase = value.phase;
	const caseSensitive = value.caseSensitive;
	const primary = value.primary;
	const secondary = value.secondary;
	if (!(["initial", "recursive", "any"] as const).includes(phase as never) || typeof caseSensitive !== "boolean") {
		throw new TypeError();
	}
	const capturedPrimary = captureKeywords(primary, limits);
	let capturedSecondary: { readonly mode: "any" | "all" | "none"; readonly keywords: readonly string[] } | undefined;
	if (secondary !== undefined) {
		if (!isRecord(secondary) || !hasExactKeys(secondary, SECONDARY_KEYS)) throw new TypeError();
		const mode = secondary.mode;
		if (!(["any", "all", "none"] as const).includes(mode as never)) throw new TypeError();
		capturedSecondary = Object.freeze({
			mode: mode as "any" | "all" | "none",
			keywords: captureKeywords(secondary.keywords, limits),
		});
	}
	if (capturedPrimary.length + (capturedSecondary?.keywords.length ?? 0) > limits.maxKeywordsPerEntry) {
		throw new GameWorldbookError("worldbook_capacity");
	}
	const normalized = new Set<string>();
	for (const keyword of [...capturedPrimary, ...(capturedSecondary?.keywords ?? [])]) {
		const key = caseSensitive ? keyword : keyword.toLowerCase();
		if (normalized.has(key)) throw new TypeError();
		normalized.add(key);
	}
	return Object.freeze({
		kind,
		phase: phase as "initial" | "recursive" | "any",
		caseSensitive,
		primary: capturedPrimary,
		...(capturedSecondary ? { secondary: capturedSecondary } : {}),
	});
}

function captureKeywords(value: unknown, limits: Readonly<GameWorldbookLimits>): readonly string[] {
	if (!Array.isArray(value)) throw new GameWorldbookError("invalid_worldbook");
	const length = value.length;
	if (length === 0) throw new GameWorldbookError("invalid_worldbook");
	if (length > limits.maxKeywordsPerEntry) throw new GameWorldbookError("worldbook_capacity");
	const captured: string[] = [];
	for (let index = 0; index < length; index += 1) {
		const keyword = value[index];
		if (typeof keyword !== "string" || keyword.length === 0) throw new TypeError();
		if (Buffer.byteLength(keyword) > limits.maxKeywordBytes) throw new GameWorldbookError("worldbook_capacity");
		captured.push(keyword);
	}
	return Object.freeze(captured);
}

function captureScope(value: unknown, limits: Readonly<GameWorldbookLimits>): Readonly<GameWorldbookEntryScope> {
	if (!isRecord(value) || !hasExactKeys(value, SCOPE_KEYS)) throw new TypeError();
	const actorIds = value.actorIds;
	const requiredTags = value.requiredTags;
	const excludedTags = value.excludedTags;
	if (actorIds === undefined && requiredTags === undefined && excludedTags === undefined) throw new TypeError();
	return Object.freeze({
		...(actorIds === undefined
			? {}
			: { actorIds: identifierList(actorIds, limits.maxIdentifierBytes, false, "worldbook_capacity") }),
		...(requiredTags === undefined
			? {}
			: { requiredTags: identifierList(requiredTags, limits.maxIdentifierBytes, false, "worldbook_capacity") }),
		...(excludedTags === undefined
			? {}
			: { excludedTags: identifierList(excludedTags, limits.maxIdentifierBytes, false, "worldbook_capacity") }),
	});
}

function captureScan(rawValue: unknown, limits: Readonly<GameWorldbookLimits>): GameWorldbookScanSnapshot {
	try {
		if (limits.maxScanBytes === 0) throw new GameWorldbookError("scan_capacity");
		const value = normalizeJsonValueWithin(rawValue, {
			maxDepth: SCAN_SCHEMA_DEPTH,
			maxBytes: limits.maxScanBytes,
		});
		if (!isRecord(value) || !hasExactKeys(value, SCAN_KEYS)) throw new TypeError();
		const scanVersion = value.scanVersion;
		const revision = value.revision;
		const rawSegments = value.segments;
		const rawTags = value.tags;
		if (scanVersion !== "1" || typeof revision !== "string") throw new TypeError();
		assertBoundedIdentifier(revision, limits.maxIdentifierBytes, "scan_capacity");
		if (!Array.isArray(rawSegments) || !Array.isArray(rawTags)) throw new TypeError();
		const segmentCount = rawSegments.length;
		const tagCount = rawTags.length;
		if (segmentCount > limits.maxScanSegments || tagCount > limits.maxScanTags) {
			throw new GameWorldbookError("scan_capacity");
		}
		const segmentIds = new Set<string>();
		const segmentValues: Readonly<GameWorldbookScanSegment>[] = [];
		for (let index = 0; index < segmentCount; index += 1) {
			const segment = rawSegments[index];
			if (!isRecord(segment) || !hasExactKeys(segment, SEGMENT_KEYS)) throw new TypeError();
			const segmentId = segment.segmentId;
			const text = segment.text;
			if (typeof segmentId !== "string" || typeof text !== "string") throw new TypeError();
			assertBoundedIdentifier(segmentId, limits.maxIdentifierBytes, "scan_capacity");
			if (segmentIds.has(segmentId)) throw new TypeError();
			segmentIds.add(segmentId);
			segmentValues.push(Object.freeze({ segmentId, text }));
		}
		const segments = Object.freeze(segmentValues);
		const tags = identifierList(rawTags, limits.maxIdentifierBytes, true, "scan_capacity");
		const captured = Object.freeze({ scanVersion, revision, segments, tags });
		return captured;
	} catch (error) {
		if (error instanceof GameWorldbookError) throw error;
		if (error instanceof JsonValueLimitError && error.code === "json_too_large") {
			throw new GameWorldbookError("scan_capacity");
		}
		throw new GameWorldbookError("invalid_scan");
	}
}

function activateEntries(
	worldbook: CapturedWorldbook,
	scan: Readonly<GameWorldbookScanSnapshot>,
	actorId: string,
	limits: Readonly<GameWorldbookLimits>,
	signal: AbortSignal,
): readonly MatchedEntry[] {
	const tags = new Set(scan.tags);
	const eligible = worldbook.entries.filter((entry) => scopeMatches(entry.scope, actorId, tags));
	const activated = new Map<string, MatchedEntry>();
	const budget: MatchBudget = { used: 0, maximum: limits.maxMatchEvaluations };
	for (const entry of eligible) {
		signal.throwIfAborted();
		const reason = initialMatch(entry, scan.segments, budget);
		if (reason) activate(entry, reason, activated, limits.maxActivatedEntries);
	}
	let frontier = [...activated.values()].filter(({ entry }) => entry.recursive);
	const recursiveSegments: GameWorldbookScanSegment[] = [];
	for (let step = 0; step < limits.maxRecursionSteps && frontier.length > 0; step += 1) {
		for (const { entry } of frontier) recursiveSegments.push({ segmentId: entry.entryId, text: entry.content });
		const discovered: MatchedEntry[] = [];
		for (const entry of eligible) {
			if (activated.has(entry.entryId)) continue;
			const reason = recursiveMatch(entry, recursiveSegments, budget);
			if (!reason) continue;
			const matched = activate(entry, reason, activated, limits.maxActivatedEntries);
			discovered.push(matched);
		}
		frontier = discovered.filter(({ entry }) => entry.recursive);
	}
	if (frontier.length > 0) {
		for (const { entry } of frontier) recursiveSegments.push({ segmentId: entry.entryId, text: entry.content });
		for (const entry of eligible) {
			if (!activated.has(entry.entryId) && recursiveMatch(entry, recursiveSegments, budget)) {
				throw new GameWorldbookError("recursion_limit_exceeded");
			}
		}
	}
	return Object.freeze(
		[...activated.values()].sort(
			(left, right) => right.entry.priority - left.entry.priority || left.entry.position - right.entry.position,
		),
	);
}

function initialMatch(
	entry: CapturedEntry,
	segments: readonly Readonly<GameWorldbookScanSegment>[],
	budget: MatchBudget,
): Readonly<GameWorldbookActivationReason> | undefined {
	if (entry.activation.kind === "constant") return Object.freeze({ kind: "constant" });
	if (entry.activation.phase === "recursive") return undefined;
	return keywordMatch(entry.activation, segments, "initial", budget);
}

function recursiveMatch(
	entry: CapturedEntry,
	segments: readonly Readonly<GameWorldbookScanSegment>[],
	budget: MatchBudget,
): Readonly<GameWorldbookActivationReason> | undefined {
	if (entry.activation.kind === "constant" || entry.activation.phase === "initial") return undefined;
	return keywordMatch(entry.activation, segments, "recursive", budget);
}

function keywordMatch(
	activation: Extract<GameWorldbookActivation, { readonly kind: "keywords" }>,
	segments: readonly Readonly<GameWorldbookScanSegment>[],
	phase: "initial" | "recursive",
	budget: MatchBudget,
): Readonly<GameWorldbookActivationReason> | undefined {
	const primary = firstKeywordMatch(activation.primary, segments, activation.caseSensitive, budget);
	if (!primary) return undefined;
	const secondary = activation.secondary;
	if (secondary) {
		if (
			secondary.mode === "any" &&
			!firstKeywordMatch(secondary.keywords, segments, activation.caseSensitive, budget)
		) {
			return undefined;
		}
		if (secondary.mode === "all") {
			for (const keyword of secondary.keywords) {
				if (!firstKeywordMatch([keyword], segments, activation.caseSensitive, budget)) return undefined;
			}
		}
		if (secondary.mode === "none") {
			if (firstKeywordMatch(secondary.keywords, segments, activation.caseSensitive, budget)) return undefined;
		}
	}
	return Object.freeze({
		kind: "keyword",
		phase,
		primaryKeyword: primary.keyword,
		segmentId: primary.segmentId,
	});
}

function firstKeywordMatch(
	keywords: readonly string[],
	segments: readonly Readonly<GameWorldbookScanSegment>[],
	caseSensitive: boolean,
	budget: MatchBudget,
): { readonly keyword: string; readonly segmentId: string } | undefined {
	for (const keyword of keywords) {
		const needle = caseSensitive ? keyword : keyword.toLowerCase();
		for (const segment of segments) {
			chargeMatch(budget);
			const text = caseSensitive ? segment.text : segment.text.toLowerCase();
			if (text.includes(needle)) return { keyword, segmentId: segment.segmentId };
		}
	}
	return undefined;
}

function activate(
	entry: CapturedEntry,
	reason: Readonly<GameWorldbookActivationReason>,
	activated: Map<string, MatchedEntry>,
	maximum: number,
): MatchedEntry {
	if (activated.size >= maximum) throw new GameWorldbookError("activation_capacity");
	const matched = Object.freeze({ entry, reason });
	activated.set(entry.entryId, matched);
	return matched;
}

async function admitContext(
	worldbook: CapturedWorldbook,
	scan: Readonly<GameWorldbookScanSnapshot>,
	activated: readonly MatchedEntry[],
	limits: Readonly<GameWorldbookLimits>,
	countTokenUpperBound: GameWorldbookContextSourceOptions["countTokenUpperBound"],
	signal: AbortSignal,
): Promise<GameWorldbookSelection> {
	const selected: ActivatedGameWorldbookEntry[] = [];
	const omitted: OmittedGameWorldbookEntry[] = [];
	let contextBytes = 0;
	let contextTokenUpperBound = 0;
	let admittedCanonicalBody: string | undefined;
	for (const matched of activated) {
		signal.throwIfAborted();
		const projected = projectMatched(matched);
		const body = contextBody(worldbook, scan.revision, [...selected, projected]);
		let canonical: string;
		try {
			if (limits.maxContextBytes === 0) throw new JsonValueLimitError("json_too_large");
			canonical = canonicalizeJsonValueWithin(body as unknown as JsonValue, {
				maxDepth: CONTEXT_SCHEMA_DEPTH,
				maxBytes: limits.maxContextBytes,
			});
		} catch (error) {
			if (!(error instanceof JsonValueLimitError) || error.code !== "json_too_large") throw error;
			omitted.push(Object.freeze({ entryId: matched.entry.entryId, reason: "byte_budget" }));
			continue;
		}
		const bytes = Buffer.byteLength(canonical);
		const tokenUpperBound = await raceOperationWithAbort(() => countTokenUpperBound(canonical, signal), signal);
		signal.throwIfAborted();
		if (!Number.isSafeInteger(tokenUpperBound) || tokenUpperBound < 0 || Object.is(tokenUpperBound, -0)) {
			throw new GameWorldbookError("invalid_token_upper_bound");
		}
		if (tokenUpperBound > limits.maxContextTokenUpperBound) {
			omitted.push(Object.freeze({ entryId: matched.entry.entryId, reason: "token_budget" }));
			continue;
		}
		selected.push(projected);
		contextBytes = bytes;
		contextTokenUpperBound = tokenUpperBound;
		admittedCanonicalBody = canonical;
	}
	const finalBody = contextBody(worldbook, scan.revision, selected);
	if (selected.length > 0 && admittedCanonicalBody === undefined) {
		throw new TypeError("Selected worldbook context is missing its admitted canonical body.");
	}
	const contribution =
		selected.length === 0
			? EMPTY_CONTRIBUTION
			: Object.freeze({
					references: Object.freeze([
						Object.freeze({
							referenceId: worldbook.worldbookId,
							revision: createHash("sha256")
								.update(admittedCanonicalBody as string)
								.digest("hex"),
							content: finalBody as unknown as JsonValue,
						}),
					]),
				});
	return Object.freeze({
		activated: Object.freeze(activated.map(projectMatched)),
		selected: Object.freeze(selected),
		omitted: Object.freeze(omitted),
		contextBytes,
		contextTokenUpperBound,
		contribution,
	});
}

function contextBody(
	worldbook: CapturedWorldbook,
	scanRevision: string,
	entries: readonly Readonly<ActivatedGameWorldbookEntry>[],
): Readonly<GameWorldbookContextBody> {
	return Object.freeze({
		contextVersion: "1" as const,
		kind: "game-worldbook" as const,
		worldbookId: worldbook.worldbookId,
		worldbookRevision: worldbook.revision,
		scanRevision,
		entries: Object.freeze(
			entries.map((entry) =>
				Object.freeze({ entryId: entry.entryId, revision: entry.revision, content: entry.content }),
			),
		),
	});
}

function projectMatched(matched: MatchedEntry): Readonly<ActivatedGameWorldbookEntry> {
	return Object.freeze({
		entryId: matched.entry.entryId,
		revision: matched.entry.revision,
		content: matched.entry.content,
		reason: matched.reason,
	});
}

function scopeMatches(
	scope: Readonly<GameWorldbookEntryScope> | undefined,
	actorId: string,
	tags: ReadonlySet<string>,
) {
	if (!scope) return true;
	if (scope.actorIds && !scope.actorIds.includes(actorId)) return false;
	if (scope.requiredTags && !scope.requiredTags.every((tag) => tags.has(tag))) return false;
	if (scope.excludedTags?.some((tag) => tags.has(tag))) return false;
	return true;
}

function identifierList(
	value: unknown,
	maxIdentifierBytes: number,
	allowEmpty = false,
	capacityCode?: "worldbook_capacity" | "scan_capacity",
): readonly string[] {
	if (!Array.isArray(value)) throw new TypeError();
	const length = value.length;
	if (!allowEmpty && length === 0) throw new TypeError();
	const seen = new Set<string>();
	const captured: string[] = [];
	for (let index = 0; index < length; index += 1) {
		const item = value[index];
		if (typeof item !== "string") throw new TypeError();
		assertBoundedIdentifier(item, maxIdentifierBytes, capacityCode);
		if (seen.has(item)) throw new TypeError();
		seen.add(item);
		captured.push(item);
	}
	return Object.freeze(captured);
}

function chargeMatch(budget: MatchBudget): void {
	if (budget.used >= budget.maximum) throw new GameWorldbookError("match_capacity");
	budget.used += 1;
}

function assertBoundedIdentifier(
	value: string,
	maxBytes: number,
	capacityCode?: "worldbook_capacity" | "scan_capacity",
): void {
	assertOpaqueIdentifier(value);
	if (Buffer.byteLength(value) > maxBytes) {
		if (capacityCode) throw new GameWorldbookError(capacityCode);
		throw new TypeError();
	}
}

function positive(value: unknown): number {
	if (!Number.isSafeInteger(value) || (value as number) < 1) throw new TypeError();
	return value as number;
}

function nonnegative(value: unknown): number {
	if (!Number.isSafeInteger(value) || (value as number) < 0 || Object.is(value, -0)) throw new TypeError();
	return value as number;
}

function hasExactKeys(value: Readonly<Record<string, unknown>>, allowed: ReadonlySet<string>): boolean {
	return Object.keys(value).every((key) => allowed.has(key));
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

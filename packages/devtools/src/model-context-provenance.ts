import { randomUUID } from "node:crypto";
import { mkdir, readFile, stat } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { isDeepStrictEqual } from "node:util";
import {
	createGameProvenanceAlias,
	GAME_MODEL_CONTEXT_SOURCE_KINDS,
	type GameModelRequestProvenance,
	type GameModelResponseProvenance,
	type GameProvenanceAlias,
	type GameProviderProvenanceSink,
	isGameProvenanceAlias,
} from "@opengameagent/agent";
import { assertCanonicalTimestamp, assertImageMediaType } from "@opengameagent/protocol";
import { Mutex } from "async-mutex";
import writeFileAtomic from "write-file-atomic";

const SCHEMA_VERSION = "4" as const;
const encoder = new TextEncoder();
const RESPONSE_KEYS = ["completedAt", "servedModel", "stopReason", "outcome", "errorKind", "cost", "usage"] as const;

export interface ModelContextProvenanceLimits {
	readonly maxRecords: number;
	readonly maxRecordBytes: number;
	readonly maxStoreBytes: number;
}

export interface ModelContextProvenanceRequestRecord extends GameModelRequestProvenance {
	readonly schemaVersion: typeof SCHEMA_VERSION;
	readonly type: "provider.request";
	readonly recordId: GameProvenanceAlias;
	readonly sequence: string;
}

export interface ModelContextProvenanceResponseRecord extends GameModelResponseProvenance {
	readonly schemaVersion: typeof SCHEMA_VERSION;
	readonly type: "provider.response";
	readonly requestRecordId: GameProvenanceAlias;
	readonly sequence: string;
}

export type ModelContextProvenanceRecord = ModelContextProvenanceRequestRecord | ModelContextProvenanceResponseRecord;

type ModelContextProvenanceRecordDraft =
	| Omit<ModelContextProvenanceRequestRecord, "sequence">
	| Omit<ModelContextProvenanceResponseRecord, "sequence">;

export interface ModelContextProvenanceStoredState {
	readonly schemaVersion: typeof SCHEMA_VERSION;
	readonly revision: "empty" | string;
	readonly records: readonly ModelContextProvenanceRecord[];
}

export interface ModelContextProvenanceStore {
	load(maxBytes: number): Promise<ModelContextProvenanceStoredState>;
	append(
		expectedRevision: ModelContextProvenanceStoredState["revision"],
		record: ModelContextProvenanceRecord,
	): Promise<ModelContextProvenanceStoredState>;
}

export class ModelContextProvenanceRecorder implements GameProviderProvenanceSink {
	private state: ModelContextProvenanceStoredState | undefined;
	private readonly limits: Readonly<ModelContextProvenanceLimits>;
	private readonly newId: () => string;
	private readonly mutex = new Mutex();

	constructor(
		private readonly options: {
			readonly store: ModelContextProvenanceStore;
			readonly limits: ModelContextProvenanceLimits;
			readonly newId?: () => string;
		},
	) {
		if (
			typeof options !== "object" ||
			options === null ||
			typeof options.store?.load !== "function" ||
			typeof options.store?.append !== "function"
		) {
			fail("The provenance recorder configuration is invalid.");
		}
		this.limits = validateLimits(options.limits);
		this.newId = options.newId ?? randomUUID;
	}

	async restore(): Promise<ModelContextProvenanceStoredState> {
		return await this.mutex.runExclusive(() => this.restoreUnlocked());
	}

	private async restoreUnlocked(): Promise<ModelContextProvenanceStoredState> {
		if (!this.state) {
			this.state = validateStoredState(await this.options.store.load(this.limits.maxStoreBytes), this.limits);
		}
		return snapshotState(this.state);
	}

	async recordRequest(request: Readonly<GameModelRequestProvenance>): Promise<GameProvenanceAlias> {
		const capturedRequest = structuredClone(request);
		assertRequest(capturedRequest, true);
		return await this.mutex.runExclusive(async () => {
			const recordId = createGameProvenanceAlias("record", this.newId());
			const state = await this.restoreUnlocked();
			assertNextAttempt(state.records, capturedRequest);
			if (state.records.some((record) => record.type === "provider.request" && record.recordId === recordId)) {
				fail("The provenance request identity already exists.");
			}
			await this.appendUnlocked({
				schemaVersion: SCHEMA_VERSION,
				type: "provider.request",
				recordId,
				...capturedRequest,
			});
			return recordId;
		});
	}

	async recordResponse(
		requestRecordId: GameProvenanceAlias,
		response: Readonly<GameModelResponseProvenance>,
	): Promise<void> {
		assertAlias(requestRecordId);
		const capturedResponse = structuredClone(response);
		assertResponse(capturedResponse, true);
		await this.mutex.runExclusive(async () => {
			const state = await this.restoreUnlocked();
			const request = state.records.find(
				(record) => record.type === "provider.request" && record.recordId === requestRecordId,
			);
			if (request?.type !== "provider.request") fail("The provenance response has no request.");
			const settled = state.records.some(
				(record) => record.type === "provider.response" && record.requestRecordId === requestRecordId,
			);
			if (settled) fail("The provenance request is already settled.");
			await this.appendUnlocked({
				schemaVersion: SCHEMA_VERSION,
				type: "provider.response",
				requestRecordId,
				...capturedResponse,
			});
		});
	}

	private async appendUnlocked(draft: ModelContextProvenanceRecordDraft): Promise<void> {
		const state = await this.restoreUnlocked();
		if (state.records.length >= this.limits.maxRecords) {
			throw new ModelContextProvenanceCapacityError("The provenance record capacity was reached.");
		}
		const sequence = (BigInt(state.revision === "empty" ? "0" : state.revision) + 1n).toString();
		const record = { ...draft, sequence } as ModelContextProvenanceRecord;
		assertRecord(record);
		if (byteLength(record) > this.limits.maxRecordBytes) {
			throw new ModelContextProvenanceCapacityError("The provenance record byte limit was exceeded.");
		}
		const candidate: ModelContextProvenanceStoredState = Object.freeze({
			schemaVersion: SCHEMA_VERSION,
			revision: sequence,
			records: Object.freeze([...state.records, structuredClone(record)]),
		});
		if (byteLength(candidate) > this.limits.maxStoreBytes) {
			throw new ModelContextProvenanceCapacityError("The provenance store byte limit was exceeded.");
		}
		try {
			const committed = validateStoredState(
				await this.options.store.append(state.revision, structuredClone(record)),
				this.limits,
			);
			if (!isDeepStrictEqual(committed, candidate)) {
				fail("The provenance Store did not confirm the appended record.");
			}
			this.state = committed;
		} catch (error) {
			this.state = undefined;
			throw error;
		}
	}
}

export class InMemoryModelContextProvenanceStore implements ModelContextProvenanceStore {
	private state = emptyState();

	async load(maxBytes: number): Promise<ModelContextProvenanceStoredState> {
		assertPositiveSafeInteger(maxBytes, "maxBytes");
		if (byteLength(this.state) > maxBytes) {
			throw new ModelContextProvenanceCapacityError("The provenance store byte limit was exceeded.");
		}
		return snapshotState(this.state);
	}

	async append(expectedRevision: "empty" | string, record: ModelContextProvenanceRecord) {
		if (expectedRevision !== this.state.revision) throw new ModelContextProvenanceWriteConflictError();
		assertRecord(record);
		this.state = Object.freeze({
			schemaVersion: SCHEMA_VERSION,
			revision: record.sequence,
			records: Object.freeze([...this.state.records, structuredClone(record)]),
		});
		return snapshotState(this.state);
	}
}

export class FileModelContextProvenanceStore implements ModelContextProvenanceStore {
	private readonly path: string;
	private readonly maxDocumentBytes: number;

	constructor(options: { readonly path: string; readonly maxDocumentBytes: number }) {
		if (typeof options?.path !== "string" || options.path.length === 0) {
			fail("The provenance file path is invalid.");
		}
		assertPositiveSafeInteger(options.maxDocumentBytes, "maxDocumentBytes");
		this.path = resolve(options.path);
		this.maxDocumentBytes = options.maxDocumentBytes;
	}

	async load(maxBytes: number): Promise<ModelContextProvenanceStoredState> {
		assertPositiveSafeInteger(maxBytes, "maxBytes");
		const maximum = Math.min(maxBytes, this.maxDocumentBytes);
		let text: string;
		try {
			const metadata = await stat(this.path);
			if (metadata.size > maximum) {
				throw new ModelContextProvenanceCapacityError("The provenance store byte limit was exceeded.");
			}
			text = await readFile(this.path, "utf8");
		} catch (error) {
			if (isMissing(error)) return emptyState();
			throw error;
		}
		if (encoder.encode(text).byteLength > maximum) {
			throw new ModelContextProvenanceCapacityError("The provenance store byte limit was exceeded.");
		}
		let value: unknown;
		try {
			value = JSON.parse(text);
		} catch {
			fail("The provenance file is corrupt.");
		}
		return validateStoredState(value, {
			maxRecords: Number.MAX_SAFE_INTEGER,
			maxRecordBytes: maximum,
			maxStoreBytes: maximum,
		});
	}

	async append(expectedRevision: "empty" | string, record: ModelContextProvenanceRecord) {
		return await serializePath(this.path, async () => {
			const current = await this.load(this.maxDocumentBytes);
			if (current.revision !== expectedRevision) throw new ModelContextProvenanceWriteConflictError();
			assertRecord(record);
			const next: ModelContextProvenanceStoredState = {
				schemaVersion: SCHEMA_VERSION,
				revision: record.sequence,
				records: [...current.records, structuredClone(record)],
			};
			const text = JSON.stringify(next);
			if (encoder.encode(text).byteLength > this.maxDocumentBytes) {
				throw new ModelContextProvenanceCapacityError("The provenance store byte limit was exceeded.");
			}
			await mkdir(dirname(this.path), { recursive: true });
			await writeFileAtomic(this.path, text, { encoding: "utf8", fsync: true });
			return snapshotState(next);
		});
	}
}

export class ModelContextProvenanceValidationError extends TypeError {
	constructor(message: string) {
		super(message);
		this.name = "ModelContextProvenanceValidationError";
	}
}

export class ModelContextProvenanceCapacityError extends RangeError {
	constructor(message: string) {
		super(message);
		this.name = "ModelContextProvenanceCapacityError";
	}
}

export class ModelContextProvenanceWriteConflictError extends Error {
	constructor() {
		super("The provenance store revision did not match.");
		this.name = "ModelContextProvenanceWriteConflictError";
	}
}

function validateStoredState(value: unknown, limits: ModelContextProvenanceLimits): ModelContextProvenanceStoredState {
	if (!isObject(value) || !exactKeys(value, ["schemaVersion", "revision", "records"])) invalid();
	if (value.schemaVersion !== SCHEMA_VERSION || !Array.isArray(value.records)) invalid();
	if (value.records.length > limits.maxRecords || byteLength(value) > limits.maxStoreBytes) capacity();
	let expected = 1n;
	let revision: "empty" | string = "empty";
	const requests = new Map<string, ModelContextProvenanceRequestRecord>();
	const responses = new Set<string>();
	const records: ModelContextProvenanceRecord[] = [];
	for (const raw of value.records) {
		assertRecord(raw);
		if (raw.sequence !== expected.toString()) invalid();
		if (byteLength(raw) > limits.maxRecordBytes) capacity();
		if (raw.type === "provider.request") {
			assertNextAttempt(records, raw);
			if (requests.has(raw.recordId)) invalid();
			requests.set(raw.recordId, raw);
		} else {
			const request = requests.get(raw.requestRecordId);
			if (!request || responses.has(raw.requestRecordId)) invalid();
			responses.add(raw.requestRecordId);
		}
		records.push(structuredClone(raw));
		revision = raw.sequence;
		expected += 1n;
	}
	if (value.revision !== revision) invalid();
	return Object.freeze({ schemaVersion: SCHEMA_VERSION, revision, records: Object.freeze(records) });
}

function assertRecord(value: unknown): asserts value is ModelContextProvenanceRecord {
	if (!isObject(value) || value.schemaVersion !== SCHEMA_VERSION || !canonicalDecimal(value.sequence)) invalid();
	if (value.type === "provider.request") {
		if (!exactKeys(value, ["schemaVersion", "type", "recordId", "sequence", ...requestKeys(value)])) invalid();
		assertAlias(value.recordId);
		assertRequest(value, false);
		return;
	}
	if (value.type === "provider.response") {
		if (!exactKeys(value, ["schemaVersion", "type", "requestRecordId", "sequence", ...RESPONSE_KEYS])) invalid();
		assertAlias(value.requestRecordId);
		assertResponse(value, false);
		return;
	}
	invalid();
}

function assertRequest(value: unknown, exact: boolean): asserts value is GameModelRequestProvenance {
	if (!isObject(value) || (exact && !exactKeys(value, requestKeys(value)))) invalid();
	if (
		value.operation !== "inference" ||
		!isObject(value.coordinate) ||
		!canonicalTimestamp(value.startedAt) ||
		!isObject(value.requestedRoute) ||
		!isObject(value.attempt) ||
		!sha256(value.modelContextFingerprint) ||
		!isObject(value.transcript) ||
		!isObject(value.lineage) ||
		!Array.isArray(value.sources) ||
		!Array.isArray(value.tools) ||
		!Array.isArray(value.images)
	) {
		invalid();
	}
	assertCoordinate(value.coordinate);
	assertRoute(value.requestedRoute);
	assertAttempt(value.attempt);
	assertTranscript(value.transcript);
	assertLineage(value.lineage);
	if (value.policyContext !== undefined) {
		assertPolicyContext(value.policyContext);
		if (value.lineage.exactToolRepeatPolicy === undefined) invalid();
	}
	if (value.toolSnapshot !== undefined) assertToolSnapshot(value.toolSnapshot);
	for (const source of value.sources) assertSource(source);
	for (const tool of value.tools) assertTool(tool);
	for (const image of value.images) assertImage(image);
}

function assertResponse(value: unknown, exact: boolean): asserts value is GameModelResponseProvenance {
	if (!isObject(value) || (exact && !exactKeys(value, RESPONSE_KEYS))) invalid();
	if (
		!canonicalTimestamp(value.completedAt) ||
		!isGameProvenanceAlias(value.servedModel) ||
		!includes(["stop", "length", "toolUse", "error", "aborted", "deferred"], value.stopReason) ||
		!includes(["completed", "failed", "cancelled", "deferred"], value.outcome) ||
		!includes(["none", "provider", "cancelled"], value.errorKind) ||
		!isObject(value.cost) ||
		!isObject(value.usage)
	) {
		invalid();
	}
	assertCost(value.cost);
	assertUsage(value.usage);
	if (value.usage.kind === "unreported" && !includes(["error", "aborted"], value.stopReason)) invalid();
	if (value.cost.kind === "unknown" && value.usage.kind !== "unreported") invalid();
	if (value.cost.kind === "calculated" && value.usage.kind !== "reported") invalid();
	const expected =
		value.stopReason === "error"
			? ["failed", "provider"]
			: value.stopReason === "aborted"
				? ["cancelled", "cancelled"]
				: value.stopReason === "deferred"
					? ["deferred", "none"]
					: ["completed", "none"];
	if (value.outcome !== expected[0] || value.errorKind !== expected[1]) invalid();
}

function assertCost(value: Record<string, unknown>): void {
	if (value.kind === "unpriced") {
		if (!exactKeys(value, ["kind"])) invalid();
		return;
	}
	if (value.kind === "unknown") {
		if (!exactKeys(value, ["kind", "source"]) || !includes(["pi-catalog", "host-rate-card"], value.source)) {
			invalid();
		}
		return;
	}
	if (value.kind === "free") {
		if (!exactKeys(value, ["kind", "usd"]) || !isObject(value.usd)) invalid();
		assertUsdCost(value.usd);
		for (const amount of Object.values(value.usd)) if (amount !== 0) invalid();
		return;
	}
	if (
		!exactKeys(value, ["kind", "source", "usd"]) ||
		value.kind !== "calculated" ||
		!includes(["pi-catalog", "host-rate-card"], value.source)
	) {
		invalid();
	}
	assertUsdCost(value.usd);
}

function assertUsage(value: Record<string, unknown>): void {
	if (value.kind === "unreported") {
		if (!exactKeys(value, ["kind"])) invalid();
		return;
	}
	if (value.kind !== "reported") invalid();
	const usageKeys = [
		"kind",
		"input",
		"output",
		"cacheRead",
		"cacheWrite",
		...(value.cacheWrite1h === undefined ? [] : ["cacheWrite1h"]),
		...(value.reasoning === undefined ? [] : ["reasoning"]),
		"totalTokens",
	];
	if (!exactKeys(value, usageKeys)) invalid();
	for (const [key, amount] of Object.entries(value)) {
		if (key !== "kind" && !nonnegative(amount)) invalid();
	}
}

function assertUsdCost(value: unknown): void {
	if (!isObject(value) || !exactKeys(value, ["input", "output", "cacheRead", "cacheWrite", "total"])) invalid();
	for (const amount of Object.values(value)) if (!nonnegative(amount)) invalid();
}

function assertRoute(value: Record<string, unknown>): void {
	if (!exactKeys(value, ["provider", "api", "model"])) invalid();
	if (
		!isGameProvenanceAlias(value.provider) ||
		!isGameProvenanceAlias(value.api) ||
		!isGameProvenanceAlias(value.model)
	) {
		invalid();
	}
}

function assertAttempt(value: Record<string, unknown>): void {
	if (!exactKeys(value, ["index", "targetRoute"]) || !canonicalDecimal(value.index) || value.index === "0") invalid();
	if (!isObject(value.targetRoute)) invalid();
	assertRoute(value.targetRoute);
}

function assertNextAttempt(
	records: readonly ModelContextProvenanceRecord[],
	request: Readonly<GameModelRequestProvenance>,
): void {
	const previous = records.findLast(
		(record): record is ModelContextProvenanceRequestRecord =>
			record.type === "provider.request" && sameCoordinate(record.coordinate, request.coordinate),
	);
	if (!previous) {
		if (request.attempt.index !== "1") invalid();
		return;
	}
	if (BigInt(request.attempt.index) !== BigInt(previous.attempt.index) + 1n) invalid();
	if (
		request.modelContextFingerprint !== previous.modelContextFingerprint ||
		request.requestedRoute.provider !== previous.requestedRoute.provider ||
		request.requestedRoute.api !== previous.requestedRoute.api ||
		request.requestedRoute.model !== previous.requestedRoute.model
	) {
		invalid();
	}
	if (!records.some((record) => record.type === "provider.response" && record.requestRecordId === previous.recordId)) {
		invalid();
	}
}

function sameCoordinate(
	left: GameModelRequestProvenance["coordinate"],
	right: GameModelRequestProvenance["coordinate"],
): boolean {
	return (
		left.scopeId === right.scopeId &&
		left.streamId === right.streamId &&
		left.runId === right.runId &&
		left.turn === right.turn
	);
}

function assertTranscript(value: unknown): void {
	if (!isObject(value)) invalid();
	const keys = value.checkpointRevision === undefined ? ["revision"] : ["revision", "checkpointRevision"];
	if (!exactKeys(value, keys) || !canonicalDecimal(value.revision)) invalid();
	if (value.checkpointRevision !== undefined && !canonicalDecimal(value.checkpointRevision)) invalid();
	if (value.checkpointRevision !== undefined && BigInt(value.checkpointRevision) > BigInt(value.revision as string)) {
		invalid();
	}
}

function assertCoordinate(value: unknown): void {
	if (!isObject(value) || !exactKeys(value, ["scopeId", "streamId", "runId", "turn"])) invalid();
	if (
		!isGameProvenanceAlias(value.scopeId) ||
		!isGameProvenanceAlias(value.streamId) ||
		!isGameProvenanceAlias(value.runId) ||
		!canonicalDecimal(value.turn)
	) {
		invalid();
	}
}

function assertLineage(value: unknown): void {
	if (!isObject(value)) invalid();
	const keys = [
		"baseInstructions",
		...(value.contextTransformer === undefined ? [] : ["contextTransformer"]),
		...(value.exactToolRepeatPolicy === undefined ? [] : ["exactToolRepeatPolicy"]),
	];
	if (!exactKeys(value, keys)) invalid();
	assertLineageReference(value.baseInstructions);
	if (value.contextTransformer !== undefined) assertLineageReference(value.contextTransformer);
	if (value.exactToolRepeatPolicy !== undefined) assertLineageReference(value.exactToolRepeatPolicy);
}

function assertPolicyContext(value: unknown): void {
	if (!isObject(value) || !exactKeys(value, ["exactToolRepeat"]) || !isObject(value.exactToolRepeat)) invalid();
	const exact = value.exactToolRepeat;
	if (
		!exactKeys(exact, ["eventId", "eventRevision", "toolName", "consecutiveCount", "contentFingerprint"]) ||
		!isGameProvenanceAlias(exact.eventId) ||
		!canonicalDecimal(exact.eventRevision) ||
		!isGameProvenanceAlias(exact.toolName) ||
		!canonicalPositiveSafeInteger(exact.consecutiveCount) ||
		!sha256(exact.contentFingerprint)
	) {
		invalid();
	}
}

function assertLineageReference(value: unknown): void {
	if (!isObject(value) || !exactKeys(value, ["sourceId", "referenceId", "revision", "contentFingerprint"])) {
		invalid();
	}
	if (
		!isGameProvenanceAlias(value.sourceId) ||
		!isGameProvenanceAlias(value.referenceId) ||
		!isGameProvenanceAlias(value.revision) ||
		!sha256(value.contentFingerprint)
	) {
		invalid();
	}
}

function assertToolSnapshot(value: unknown): void {
	if (!isObject(value) || !exactKeys(value, ["revision", "policyRevision"])) invalid();
	if (!isGameProvenanceAlias(value.revision) || !isGameProvenanceAlias(value.policyRevision)) invalid();
}

function assertSource(value: unknown): void {
	if (!isObject(value) || !exactKeys(value, ["kind", "sourceId", "references"])) invalid();
	if (!sourceKind(value.kind) || !isGameProvenanceAlias(value.sourceId) || !Array.isArray(value.references)) invalid();
	for (const reference of value.references) {
		if (!isObject(reference) || !exactKeys(reference, ["referenceId", "revision", "contentFingerprint"])) {
			invalid();
		}
		if (
			!isGameProvenanceAlias(reference.referenceId) ||
			!isGameProvenanceAlias(reference.revision) ||
			!sha256(reference.contentFingerprint)
		) {
			invalid();
		}
	}
}

function assertTool(value: unknown): void {
	if (!isObject(value) || !exactKeys(value, ["name", "sourceId", "fingerprint"])) invalid();
	if (!isGameProvenanceAlias(value.name) || !isGameProvenanceAlias(value.sourceId) || !sha256(value.fingerprint)) {
		invalid();
	}
}

function assertImage(value: unknown): void {
	const keys =
		isObject(value) && value.context !== undefined
			? ["messageIndex", "contentIndex", "mediaType", "bytes", "sha256", "context"]
			: ["messageIndex", "contentIndex", "mediaType", "bytes", "sha256"];
	if (!isObject(value) || !exactKeys(value, keys)) {
		invalid();
	}
	if (
		!nonnegativeInteger(value.messageIndex) ||
		!nonnegativeInteger(value.contentIndex) ||
		!imageMediaType(value.mediaType) ||
		!positiveInteger(value.bytes) ||
		!sha256(value.sha256)
	) {
		invalid();
	}
	if (value.context !== undefined) {
		if (!isObject(value.context) || !exactKeys(value.context, ["sourceId", "referenceId", "revision"])) invalid();
		if (
			!isGameProvenanceAlias(value.context.sourceId) ||
			!isGameProvenanceAlias(value.context.referenceId) ||
			!isGameProvenanceAlias(value.context.revision)
		) {
			invalid();
		}
	}
}

function validateLimits(value: ModelContextProvenanceLimits): Readonly<ModelContextProvenanceLimits> {
	if (!isObject(value) || !exactKeys(value, ["maxRecords", "maxRecordBytes", "maxStoreBytes"])) invalid();
	assertPositiveSafeInteger(value.maxRecords, "maxRecords");
	assertPositiveSafeInteger(value.maxRecordBytes, "maxRecordBytes");
	assertPositiveSafeInteger(value.maxStoreBytes, "maxStoreBytes");
	return Object.freeze({ ...value });
}

const pathQueues = new Map<string, Promise<void>>();

async function serializePath<T>(path: string, operation: () => Promise<T>): Promise<T> {
	const previous = pathQueues.get(path) ?? Promise.resolve();
	let release!: () => void;
	const current = new Promise<void>((resolvePromise) => {
		release = resolvePromise;
	});
	const queued = previous.then(() => current);
	pathQueues.set(path, queued);
	await previous;
	try {
		return await operation();
	} finally {
		release();
		if (pathQueues.get(path) === queued) pathQueues.delete(path);
	}
}

function requestKeys(value: Record<string, unknown>): string[] {
	return [
		"operation",
		"coordinate",
		"startedAt",
		"requestedRoute",
		"attempt",
		"modelContextFingerprint",
		"transcript",
		"lineage",
		...(value.policyContext === undefined ? [] : ["policyContext"]),
		...(value.toolSnapshot === undefined ? [] : ["toolSnapshot"]),
		"sources",
		"tools",
		"images",
	];
}

function emptyState(): ModelContextProvenanceStoredState {
	return Object.freeze({ schemaVersion: SCHEMA_VERSION, revision: "empty", records: Object.freeze([]) });
}

function snapshotState(value: ModelContextProvenanceStoredState): ModelContextProvenanceStoredState {
	return structuredClone(value);
}

function sourceKind(value: unknown): boolean {
	return typeof value === "string" && GAME_MODEL_CONTEXT_SOURCE_KINDS.includes(value as never);
}

function includes(values: readonly string[], value: unknown): value is string {
	return typeof value === "string" && values.includes(value);
}

function canonicalPositiveSafeInteger(value: unknown): boolean {
	return typeof value === "string" && /^[1-9][0-9]*$/u.test(value) && Number.isSafeInteger(Number(value));
}

function exactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
	const actual = Object.keys(value);
	return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}

function isObject(value: unknown): value is Record<string, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
	const prototype = Object.getPrototypeOf(value);
	return prototype === Object.prototype || prototype === null;
}

function assertAlias(value: unknown): void {
	if (!isGameProvenanceAlias(value)) invalid();
}

function canonicalDecimal(value: unknown): value is string {
	return typeof value === "string" && /^(?:0|[1-9][0-9]*)$/u.test(value);
}

function canonicalTimestamp(value: unknown): value is string {
	try {
		assertCanonicalTimestamp(value);
		return true;
	} catch {
		return false;
	}
}

function imageMediaType(value: unknown): boolean {
	try {
		assertImageMediaType(value);
		return true;
	} catch {
		return false;
	}
}

function sha256(value: unknown): value is string {
	return typeof value === "string" && /^[0-9a-f]{64}$/u.test(value);
}

function nonnegative(value: unknown): value is number {
	return typeof value === "number" && Number.isFinite(value) && value >= 0;
}

function nonnegativeInteger(value: unknown): value is number {
	return Number.isSafeInteger(value) && (value as number) >= 0;
}

function positiveInteger(value: unknown): value is number {
	return Number.isSafeInteger(value) && (value as number) > 0;
}

function assertPositiveSafeInteger(value: unknown, name: string): void {
	if (!positiveInteger(value)) fail(`${name} must be a positive safe integer.`);
}

function byteLength(value: unknown): number {
	return encoder.encode(JSON.stringify(value)).byteLength;
}

function isMissing(error: unknown): boolean {
	return typeof error === "object" && error !== null && "code" in error && error.code === "ENOENT";
}

function invalid(): never {
	throw new ModelContextProvenanceValidationError("The model-context provenance record is invalid.");
}

function fail(message: string): never {
	throw new ModelContextProvenanceValidationError(message);
}

function capacity(): never {
	throw new ModelContextProvenanceCapacityError("The model-context provenance capacity was exceeded.");
}

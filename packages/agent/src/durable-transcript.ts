import { Buffer } from "node:buffer";
import { createHash, randomUUID } from "node:crypto";
import type { AgentMessage } from "@earendil-works/pi-agent-core";
import type { AssistantMessage, ImageContent, Message, ToolResultMessage, UserMessage } from "@earendil-works/pi-ai";
import {
	captureRetainedIdentifiers,
	assertDurableTranscriptRecord,
	assertExactTranscriptCheckpointBoundary,
	assertEventStreamCoordinate,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type DurableTranscriptAssistantMessage,
	type DurableTranscriptDeferredOpenedRecord,
	type DurableTranscriptDeferredRunClosedRecord,
	type DurableTranscriptMessage,
	DurableTranscriptMessageSchema,
	type DurableTranscriptRecord,
	type DurableTranscriptRecordData,
	type DurableTranscriptToolCallContent,
	type DurableTranscriptToolResultMessage,
	type DurableTranscriptToolStartedRecord,
	type DurableTranscriptTurnClosedRecord,
	type DurableTranscriptUserMessage,
	ImageReferenceContentSchema,
	type ImageReferenceContent,
	type JsonValue,
	type MessageContent,
	MessageContentSchema,
	normalizeJsonValueWithin,
	type RunCoordinate,
	type StreamKey,
	type TranscriptCheckpointPurgeRequest,
	type TranscriptCheckpointPurgeResult,
	type TranscriptRetentionStore,
	type TranscriptRevision,
	TranscriptRecordIdentityConflictError,
	type TranscriptStore,
	type TranscriptStoredStream,
	TranscriptWriteConflictError,
	TRANSCRIPT_SCHEMA_VERSION,
} from "@opengameagent/protocol";
import { Value } from "typebox/value";
import { decodeCanonicalBase64 } from "./canonical-base64.js";
import { safeRetainedBoundaries } from "./transcript-history.js";
import {
	type PreparedTranscriptToolResultMedia,
	type TranscriptMedia,
	type TranscriptMediaReadRequest,
	type TranscriptToolResultMediaRequest,
	TranscriptUnsupportedMediaError,
} from "./transcript-media.js";

export type { TranscriptRevision, TranscriptStore, TranscriptStoredStream } from "@opengameagent/protocol";
export { TranscriptRecordIdentityConflictError, TranscriptWriteConflictError } from "@opengameagent/protocol";

export interface TranscriptLimits {
	maxRecordBytes: number;
	maxCommitBytes: number;
	maxRestoreBytes: number;
	maxJsonDepth: number;
}

export interface TranscriptStartedToolRecoveryInput {
	readonly start: Readonly<DurableTranscriptToolStartedRecord>;
	readonly toolCall: Readonly<DurableTranscriptToolCallContent>;
}

export interface TranscriptRecoveredToolResult {
	readonly outcome: "completed" | "not_started" | "outcome_unknown";
	readonly content: readonly { readonly type: "text"; readonly text: string }[];
	readonly addedToolNames?: readonly string[];
	readonly isError: boolean;
	readonly timestamp: number;
}

export type TranscriptStartedToolRecovery = (
	input: Readonly<TranscriptStartedToolRecoveryInput>,
) => TranscriptRecoveredToolResult | undefined | Promise<TranscriptRecoveredToolResult | undefined>;

export interface DurableTranscriptOptions extends TranscriptLimits {
	newRecordId?: () => string;
	recoverStartedTool?: TranscriptStartedToolRecovery;
	media?: TranscriptMedia;
}

type DistributiveOmit<T, K extends PropertyKey> = T extends unknown ? Omit<T, Extract<K, keyof T>> : never;
type TranscriptCheckpointRecordData = Extract<DurableTranscriptRecordData, { type: "context.checkpoint" }>;

export type TranscriptRecordDraft = DistributiveOmit<
	Exclude<DurableTranscriptRecordData, TranscriptCheckpointRecordData>,
	"schemaVersion"
>;
type TranscriptCheckpointRecordDraft = DistributiveOmit<TranscriptCheckpointRecordData, "schemaVersion">;
type PreparedTranscriptRecordDraft = TranscriptRecordDraft | TranscriptCheckpointRecordDraft;
export type TranscriptRecord = DurableTranscriptRecord;

export interface TranscriptRestore {
	stream: Readonly<StreamKey>;
	revision: TranscriptRevision;
	checkpointRevision?: string;
	messages: readonly AgentMessage[];
	durableMessages: readonly DurableTranscriptMessage[];
	terminalDeferredRuns: readonly Readonly<TranscriptDeferredRun>[];
	pendingDeferred?: Readonly<DurableTranscriptDeferredOpenedRecord>;
	pendingDeferredFinalOutput?: string;
}

export interface TranscriptRevisionMaterialization {
	stream: Readonly<StreamKey>;
	revision: TranscriptRevision;
	checkpointRevision?: string;
	messages: readonly AgentMessage[];
	durableMessages: readonly DurableTranscriptMessage[];
}

export interface TranscriptDeferredRun {
	readonly coordinate: RunCoordinate;
	readonly inputId: string;
	readonly operationId: string;
	readonly status: "completed" | "aborted" | "failed";
}

export interface GameAgentTranscriptBinding {
	transcript: DurableTranscript;
	restore: TranscriptRestore;
}

export interface TranscriptCommitRequest {
	stream: Readonly<StreamKey>;
	expectedRevision: TranscriptRevision;
	records: readonly [TranscriptRecordDraft, ...TranscriptRecordDraft[]];
}

export interface TranscriptCommitResult {
	revision: string;
	records: readonly [TranscriptRecord, ...TranscriptRecord[]];
}

export interface TranscriptCheckpointRequest {
	stream: Readonly<StreamKey>;
	source: Readonly<DurableTranscriptTurnClosedRecord>;
	replacement: readonly DurableTranscriptMessage[];
	signal?: Parameters<TranscriptMedia["read"]>[1];
}

export interface TranscriptCheckpointResult extends TranscriptCommitResult {
	messages: readonly AgentMessage[];
	durableMessages: readonly DurableTranscriptMessage[];
}

export class TranscriptCheckpointCommitError extends Error {
	constructor(cause: unknown) {
		super("The durable transcript checkpoint append did not complete.", { cause });
		this.name = "TranscriptCheckpointCommitError";
	}
}

export class TranscriptUnsupportedDeferredError extends Error {
	constructor() {
		super("A durable pending-operation lifecycle adapter is required for deferred assistant output.");
		this.name = "TranscriptUnsupportedDeferredError";
	}
}

interface OpenToolCall {
	readonly assistantRecordId: string;
	readonly sourceIndex: number;
	readonly call: DurableTranscriptToolCallContent;
	start?: DurableTranscriptToolStartedRecord;
	completed: boolean;
}

interface OpenTurn {
	readonly coordinate: RunCoordinate;
	hasUserMessage: boolean;
	assistantRecordId?: string;
	assistantStopReason?: DurableTranscriptAssistantMessage["stopReason"];
	toolCalls: OpenToolCall[];
	deferred?: {
		readonly opened: DurableTranscriptDeferredOpenedRecord;
		status: "pending" | "completed" | "abandoned";
	};
}

interface TranscriptClosedTurn {
	readonly coordinate: RunCoordinate;
	readonly status: DurableTranscriptTurnClosedRecord["status"];
	readonly assistantStopReason?: DurableTranscriptAssistantMessage["stopReason"];
}

interface TranscriptScanTarget {
	readonly revision: TranscriptRevision;
	readonly checkpointRevision?: string;
	readonly durableMessages: readonly DurableTranscriptMessage[];
}

interface TranscriptScan {
	readonly loadedRevision: TranscriptRevision;
	readonly bytes: number;
	readonly openTurn?: OpenTurn;
	readonly checkpointRevision?: string;
	readonly recordIds: Set<string>;
	readonly durableMessages: DurableTranscriptMessage[];
	readonly latestDeferredOrigins: Map<string, DurableTranscriptDeferredOpenedRecord>;
	readonly deferredSettlements: Map<string, "completed" | "abandoned">;
	readonly closedDeferredRuns: Set<string>;
	readonly lastClosedTurns: Map<string, TranscriptClosedTurn>;
	readonly terminalDeferredRuns: TranscriptDeferredRun[];
	readonly finalOutputs: Map<string, string>;
	readonly target?: TranscriptScanTarget;
}

export class DurableTranscript {
	private readonly maxRecordBytes: number;
	private readonly maxCommitBytes: number;
	private readonly maxRestoreBytes: number;
	private readonly maxJsonDepth: number;
	private readonly newRecordId: () => string;
	private readonly recoverStartedTool: TranscriptStartedToolRecovery | undefined;
	private readonly media: TranscriptMedia | undefined;

	constructor(
		private readonly store: TranscriptStore,
		limits: DurableTranscriptOptions,
	) {
		this.maxRecordBytes = positiveSafeInteger("maxRecordBytes", limits.maxRecordBytes);
		this.maxCommitBytes = positiveSafeInteger("maxCommitBytes", limits.maxCommitBytes);
		this.maxRestoreBytes = positiveSafeInteger("maxRestoreBytes", limits.maxRestoreBytes);
		this.maxJsonDepth = positiveSafeInteger("maxJsonDepth", limits.maxJsonDepth);
		this.newRecordId = limits.newRecordId ?? randomUUID;
		this.recoverStartedTool = limits.recoverStartedTool;
		const media = limits.media;
		if (media && (typeof media.captureToolResult !== "function" || typeof media.read !== "function")) {
			throw new TypeError("The durable transcript media Adapter is invalid.");
		}
		this.media = media
			? Object.freeze({
					captureToolResult: media.captureToolResult.bind(media),
					read: media.read.bind(media),
				})
			: undefined;
	}

	private scanCommittedTranscript(
		stream: Readonly<StreamKey>,
		loaded: TranscriptStoredStream,
		targetRevision?: TranscriptRevision,
	): TranscriptScan {
		const loadedRevision = snapshotRevision(loaded.revision, this.maxRecordBytes);
		const loadedRecords = loaded.records;
		const loadedRecordCount = snapshotRecordListLength(loadedRecords, 0);
		let bytes = 0;
		let expected = 1n;
		let hasRetainedPrefix = false;
		let actualRevision: TranscriptRevision = "empty";
		let openTurn: OpenTurn | undefined;
		let checkpointRevision: string | undefined;
		let previousRecord: TranscriptRecord | undefined;
		const deferredOrigins = new Map<string, DurableTranscriptDeferredOpenedRecord>();
		const latestDeferredOrigins = new Map<string, DurableTranscriptDeferredOpenedRecord>();
		const deferredSettlements = new Map<string, "completed" | "abandoned">();
		const closedDeferredRuns = new Set<string>();
		const lastClosedTurns = new Map<string, TranscriptClosedTurn>();
		const terminalDeferredRuns: TranscriptDeferredRun[] = [];
		const finalOutputs = new Map<string, string>();
		const recordIds = new Set<string>();
		const durableMessages: DurableTranscriptMessage[] = [];
		let target: TranscriptScanTarget | undefined =
			targetRevision === "empty"
				? Object.freeze({ revision: "empty", durableMessages: snapshotDurableMessages([]) })
				: undefined;

		for (let index = 0; index < loadedRecordCount; index += 1) {
			const rawRecord = loadedRecords[index];
			const record = snapshotRecord(rawRecord, this.maxRecordBytes, this.maxJsonDepth);
			if (index === 0) {
				expected = parseRevision(record.sequence);
				hasRetainedPrefix = expected > 1n;
				if (hasRetainedPrefix && targetRevision === "empty") target = undefined;
			}
			if (recordIds.has(record.recordId)) throw new TranscriptRecordIdentityConflictError();
			recordIds.add(record.recordId);
			assertEventStreamCoordinate(stream, record.coordinate);
			if (record.sequence !== expected.toString()) {
				throw new TypeError("The durable transcript sequence is not continuous.");
			}
			expected += 1n;
			const recordBytes = jsonBytes(record);
			if (recordBytes > this.maxRestoreBytes - bytes) {
				throw new RangeError("The durable transcript exceeds the restore budget.");
			}
			bytes += recordBytes;
			actualRevision = record.sequence;
			if (hasRetainedPrefix && index === 0) {
				if (record.type !== "turn.closed" || record.status !== "completed" || loadedRecordCount < 2) {
					throw new TypeError("The retained durable transcript does not begin at a completed checkpoint source.");
				}
				previousRecord = record;
				continue;
			}
			if (hasRetainedPrefix && index === 1 && record.type !== "context.checkpoint") {
				throw new TypeError("The retained durable transcript source is missing its immediate checkpoint.");
			}
			if (closedDeferredRuns.has(record.coordinate.runId)) {
				throw new TypeError("The durable transcript appends a record after its deferred run terminal.");
			}
			if (record.type === "context.checkpoint") checkpointRevision = record.sequence;
			if (record.type === "message.completed" && record.message.role === "assistant") {
				const output = durableAssistantText(record.message);
				if (output !== undefined) finalOutputs.set(record.coordinate.runId, output);
			}
			if (record.type === "deferred.completed") {
				const output = durableAssistantText(record.message);
				if (output !== undefined) finalOutputs.set(record.coordinate.runId, output);
			}
			const closingTurn = record.type === "turn.closed" ? openTurn : undefined;
			if (record.type === "deferred.opened") {
				if (record.finalOutput !== undefined) finalOutputs.set(record.coordinate.runId, record.finalOutput);
				if (deferredOrigins.has(record.operationId)) {
					throw new TypeError("The durable transcript contains a repeated provider operation identifier.");
				}
				deferredOrigins.set(record.operationId, record);
				latestDeferredOrigins.set(record.coordinate.runId, record);
			}
			if (record.type === "deferred.completed") deferredSettlements.set(record.operationId, "completed");
			if (record.type === "deferred.abandoned") deferredSettlements.set(record.operationId, "abandoned");
			if (record.type === "deferred.run.closed") {
				const origin = latestDeferredOrigins.get(record.coordinate.runId);
				const settlement = origin ? deferredSettlements.get(origin.operationId) : undefined;
				if (
					!origin ||
					origin.operationId !== record.operationId ||
					origin.input.inputId !== record.inputId ||
					settlement === undefined
				) {
					throw new TypeError("The durable deferred run terminal does not match its latest provider operation.");
				}
				if (settlement === "abandoned" && record.status !== "aborted") {
					throw new TypeError("The durable deferred run terminal does not match its abandoned provider operation.");
				}
				const closure = lastClosedTurns.get(record.coordinate.runId);
				if (!closure || !sameCoordinate(closure.coordinate, record.coordinate)) {
					throw new TypeError("The durable deferred run terminal does not match its final turn closure.");
				}
				closedDeferredRuns.add(record.coordinate.runId);
				terminalDeferredRuns.push(deferredRunFromTerminal(record));
			}
			openTurn = foldRecord(record, openTurn, durableMessages, previousRecord);
			if (record.type === "turn.closed" && closingTurn) {
				lastClosedTurns.set(record.coordinate.runId, {
					coordinate: structuredClone(record.coordinate),
					status: record.status,
					...(closingTurn.assistantStopReason ? { assistantStopReason: closingTurn.assistantStopReason } : {}),
				});
			}
			previousRecord = record;
			if (record.sequence === targetRevision) {
				target = Object.freeze({
					revision: record.sequence,
					...(checkpointRevision === undefined ? {} : { checkpointRevision }),
					durableMessages: snapshotDurableMessages(durableMessages),
				});
			}
		}
		if (loadedRevision !== actualRevision) {
			throw new TypeError("The durable transcript head does not match its committed records.");
		}
		if (targetRevision !== undefined && target === undefined) {
			throw new TypeError("The durable transcript revision is unavailable.");
		}

		return {
			loadedRevision,
			bytes,
			...(openTurn ? { openTurn } : {}),
			...(checkpointRevision === undefined ? {} : { checkpointRevision }),
			recordIds,
			durableMessages,
			latestDeferredOrigins,
			deferredSettlements,
			closedDeferredRuns,
			lastClosedTurns,
			terminalDeferredRuns,
			finalOutputs,
			...(target ? { target } : {}),
		};
	}

	async restore(stream: StreamKey, signal?: Parameters<TranscriptMedia["read"]>[1]): Promise<TranscriptRestore> {
		const operationSignal = signal ?? new AbortController().signal;
		const restoredStream = snapshotStream(stream);
		operationSignal.throwIfAborted();
		const loaded = await this.store.load(restoredStream, this.maxRestoreBytes);
		operationSignal.throwIfAborted();
		const {
			loadedRevision,
			bytes,
			openTurn,
			checkpointRevision,
			recordIds,
			durableMessages,
			latestDeferredOrigins,
			deferredSettlements,
			closedDeferredRuns,
			lastClosedTurns,
			terminalDeferredRuns,
			finalOutputs,
		} = this.scanCommittedTranscript(restoredStream, loaded);

		const pendingDeferred =
			openTurn?.deferred?.status === "pending" ? structuredClone(openTurn.deferred.opened) : undefined;
		const pendingDeferredFinalOutput = pendingDeferred ? finalOutputs.get(pendingDeferred.coordinate.runId) : undefined;
		let revision = loadedRevision;
		const repair: TranscriptRecordDraft[] = [];
		const repairedRunIds = new Set<string>();
		if (openTurn && !pendingDeferred) {
			repair.push(...(await this.prepareRepair(openTurn)));
			const repairingOrigin = latestDeferredOrigins.get(openTurn.coordinate.runId);
			if (repairingOrigin && !closedDeferredRuns.has(openTurn.coordinate.runId)) {
				repair.push({
					type: "deferred.run.closed",
					coordinate: openTurn.coordinate,
					operationId: repairingOrigin.operationId,
					inputId: repairingOrigin.input.inputId,
					status: deferredSettlements.get(repairingOrigin.operationId) === "abandoned" ? "aborted" : "failed",
				});
				repairedRunIds.add(openTurn.coordinate.runId);
			}
		}
		for (const [runId, origin] of latestDeferredOrigins) {
			if (
				closedDeferredRuns.has(runId) ||
				repairedRunIds.has(runId) ||
				pendingDeferred?.operationId === origin.operationId
			) {
				continue;
			}
			const closure = lastClosedTurns.get(runId);
			if (!closure || (closure.status === "completed" && closure.assistantStopReason === "toolUse")) {
				continue;
			}
			repair.push({
				type: "deferred.run.closed",
				coordinate: closure.coordinate,
				operationId: origin.operationId,
				inputId: origin.input.inputId,
				status: deferredSettlements.get(origin.operationId) === "abandoned" ? "aborted" : "failed",
			});
			repairedRunIds.add(runId);
		}
		if (repair.length > 0) {
			const prepared = this.prepareRecords(
				restoredStream,
				loadedRevision,
				repair as [TranscriptRecordDraft, ...TranscriptRecordDraft[]],
				this.maxRestoreBytes - bytes,
				"The durable transcript exceeds the restore budget.",
				this.maxCommitBytes,
				{ existingRecordIds: recordIds },
			);
			for (const record of prepared) {
				const committed = await this.appendPrepared(restoredStream, revision, [record]);
				revision = committed.revision;
				if (record.type === "tool.completed") durableMessages.push(structuredClone(record.message));
				if (record.type === "deferred.run.closed") {
					closedDeferredRuns.add(record.coordinate.runId);
					terminalDeferredRuns.push(deferredRunFromTerminal(record));
				}
			}
		}
		operationSignal.throwIfAborted();
		const durableSnapshot = snapshotDurableMessages(durableMessages);
		const messages = await this.materialize(restoredStream, durableSnapshot, operationSignal);

		return Object.freeze({
			stream: restoredStream,
			revision,
			...(checkpointRevision === undefined ? {} : { checkpointRevision }),
			messages,
			durableMessages: durableSnapshot,
			terminalDeferredRuns: Object.freeze(structuredClone(terminalDeferredRuns)),
			...(pendingDeferred ? { pendingDeferred: Object.freeze(pendingDeferred) } : {}),
			...(pendingDeferredFinalOutput === undefined ? {} : { pendingDeferredFinalOutput }),
		});
	}

	/**
	 * Materialize one committed historical revision without repair or append.
	 * This is the read-only replay seam used by provider provenance verification.
	 */
	async materializeRevision(
		stream: StreamKey,
		revision: TranscriptRevision,
		signal?: Parameters<TranscriptMedia["read"]>[1],
	): Promise<TranscriptRevisionMaterialization> {
		const operationSignal = signal ?? new AbortController().signal;
		const restoredStream = snapshotStream(stream);
		const targetRevision = snapshotRevision(revision, this.maxRecordBytes);
		operationSignal.throwIfAborted();
		const loaded = await this.store.load(restoredStream, this.maxRestoreBytes);
		operationSignal.throwIfAborted();
		const target = this.scanCommittedTranscript(restoredStream, loaded, targetRevision).target;
		if (!target) throw new TypeError("The durable transcript revision is unavailable.");
		operationSignal.throwIfAborted();
		const messages = await this.materialize(restoredStream, target.durableMessages, operationSignal);
		return Object.freeze({
			stream: restoredStream,
			revision: targetRevision,
			...(target.checkpointRevision === undefined ? {} : { checkpointRevision: target.checkpointRevision }),
			messages,
			durableMessages: target.durableMessages,
		});
	}

	async purgeBeforeCheckpoint(request: TranscriptCheckpointPurgeRequest): Promise<TranscriptCheckpointPurgeResult> {
		const store = this.store as Partial<TranscriptRetentionStore>;
		if (typeof store.purgeBeforeCheckpoint !== "function") {
			throw new TypeError("The durable transcript store does not support checkpoint retention.");
		}
		return store.purgeBeforeCheckpoint(request);
	}

	async materialize(
		stream: StreamKey,
		messages: readonly DurableTranscriptMessage[],
		signal?: Parameters<TranscriptMedia["read"]>[1],
	): Promise<readonly AgentMessage[]> {
		const restoredStream = snapshotStream(stream);
		const durable = snapshotDurableMessages(messages);
		return materializeMessages(restoredStream, durable, this.media, signal ?? new AbortController().signal);
	}

	async captureToolResult(
		request: TranscriptToolResultMediaRequest,
		signal: Parameters<TranscriptMedia["captureToolResult"]>[1],
	): Promise<Readonly<PreparedTranscriptToolResultMedia>> {
		const stream = snapshotStream(request.stream);
		assertEventStreamCoordinate(stream, request.coordinate);
		assertOpaqueIdentifier(request.toolCallId);
		assertOpaqueIdentifier(request.toolName);
		const content = snapshotPiMediaContent(request.content);
		if (!content.some((part) => part.type === "image")) {
			return Object.freeze({
				content,
				durableContent: Object.freeze(
					content.map((part) => {
						if (part.type !== "text") throw new TranscriptUnsupportedMediaError();
						return { type: "text" as const, text: part.text };
					}),
				),
			});
		}
		const media = this.media;
		if (!media) throw new TranscriptUnsupportedMediaError();
		signal.throwIfAborted();
		const captured = await media.captureToolResult(
			Object.freeze({
				stream,
				coordinate: Object.freeze({ ...request.coordinate }),
				toolCallId: request.toolCallId,
				toolName: request.toolName,
				content,
			}),
			signal,
		);
		signal.throwIfAborted();
		return validateCapturedToolResult(content, captured);
	}

	assertMediaSupported(message: Readonly<DurableTranscriptMessage>): void {
		if (message.role === "assistant" || this.media) return;
		if (message.content.some((part) => part.type === "image-reference")) {
			throw new TranscriptUnsupportedMediaError();
		}
	}

	async commit(request: TranscriptCommitRequest): Promise<TranscriptCommitResult> {
		const stream = snapshotStream(request.stream);
		const expectedRevision = snapshotRevision(request.expectedRevision, this.maxRecordBytes);
		const drafts = request.records as readonly [PreparedTranscriptRecordDraft, ...PreparedTranscriptRecordDraft[]];
		const records = this.prepareRecords(
			stream,
			expectedRevision,
			drafts,
			this.maxCommitBytes,
			"The durable transcript commit exceeds its budget.",
			this.maxCommitBytes,
		);
		return this.appendPrepared(stream, expectedRevision, records);
	}

	async checkpoint(request: TranscriptCheckpointRequest): Promise<TranscriptCheckpointResult> {
		const stream = snapshotStream(request.stream);
		const source = snapshotRecord(request.source, this.maxRecordBytes, this.maxJsonDepth);
		if (source.type !== "turn.closed" || source.status !== "completed") {
			throw new TypeError("A transcript checkpoint requires a completed turn closure.");
		}
		assertEventStreamCoordinate(stream, source.coordinate);
		const expectedRevision = source.sequence;
		const replacement = structuredClone(request.replacement);
		if (!Array.isArray(replacement) || replacement.length === 0) {
			throw new TypeError("A transcript checkpoint requires replacement history.");
		}
		safeRetainedBoundaries(replacement);
		const records = this.prepareRecords(
			stream,
			expectedRevision,
			[
				{
					type: "context.checkpoint",
					coordinate: source.coordinate,
					sourceRevision: expectedRevision,
					sourceRecordId: source.recordId,
					replacement,
				},
			],
			this.maxCommitBytes,
			"The durable transcript commit exceeds its budget.",
			this.maxCommitBytes,
			{ authority: "checkpoint" },
		);
		const checkpoint = records[0];
		if (checkpoint.type !== "context.checkpoint") {
			throw new TypeError("The durable transcript checkpoint was not prepared.");
		}
		const durableMessages = snapshotDurableMessages(checkpoint.replacement);
		const messages = await this.materialize(stream, durableMessages, request.signal);
		let committed: TranscriptCommitResult;
		try {
			committed = await this.appendPrepared(stream, expectedRevision, records);
		} catch (error) {
			throw new TranscriptCheckpointCommitError(error);
		}
		return Object.freeze({
			...committed,
			messages,
			durableMessages,
		});
	}

	private prepareRecords(
		stream: Readonly<StreamKey>,
		expectedRevision: TranscriptRevision,
		drafts: readonly [PreparedTranscriptRecordDraft, ...PreparedTranscriptRecordDraft[]],
		aggregateBudget: number,
		aggregateBudgetError: string,
		individualCommitBudget: number,
		options: {
			readonly existingRecordIds?: ReadonlySet<string>;
			readonly authority?: "ordinary" | "checkpoint";
		} = {},
	): readonly [TranscriptRecord, ...TranscriptRecord[]] {
		const draftCount = snapshotRecordListLength(drafts, 1);
		const firstSequence = expectedRevision === "empty" ? 1n : parseRevision(expectedRevision) + 1n;
		const records: TranscriptRecord[] = [];
		const recordIds = new Set(options.existingRecordIds);
		let aggregateBytes = 0;
		for (let index = 0; index < draftCount; index += 1) {
			const draft = drafts[index];
			if (!draft) throw new TypeError("The durable transcript record is missing.");
			const capturedDraft = { ...draft } as PreparedTranscriptRecordDraft;
			if (options.authority !== "checkpoint" && capturedDraft.type === "context.checkpoint") {
				throw new TypeError("A transcript checkpoint must use the checkpoint API.");
			}
			const recordId = this.newRecordId();
			assertOpaqueIdentifier(recordId);
			if (recordIds.has(recordId)) throw new TranscriptRecordIdentityConflictError();
			recordIds.add(recordId);
			const record = snapshotRecord(
				{
					...capturedDraft,
					schemaVersion: TRANSCRIPT_SCHEMA_VERSION,
					recordId,
					sequence: (firstSequence + BigInt(index)).toString(),
				},
				this.maxRecordBytes,
				this.maxJsonDepth,
			);
			assertEventStreamCoordinate(stream, record.coordinate);
			const recordBytes = jsonBytes(record);
			if (recordBytes > individualCommitBudget) {
				throw new RangeError("The durable transcript commit exceeds its budget.");
			}
			if (recordBytes > aggregateBudget - aggregateBytes) throw new RangeError(aggregateBudgetError);
			aggregateBytes += recordBytes;
			records.push(record);
		}
		return Object.freeze(records) as unknown as readonly [TranscriptRecord, ...TranscriptRecord[]];
	}

	private async appendPrepared(
		stream: Readonly<StreamKey>,
		expectedRevision: TranscriptRevision,
		records: readonly [TranscriptRecord, ...TranscriptRecord[]],
	): Promise<TranscriptCommitResult> {
		const stored = await this.store.append(stream, expectedRevision, records);
		const storedRevision = snapshotRevision(stored.revision, this.maxRecordBytes);
		const revision = records.at(-1)?.sequence;
		if (!revision || storedRevision !== revision) {
			throw new TypeError("The durable transcript store returned an unexpected head.");
		}
		return Object.freeze({ revision, records: Object.freeze(records) });
	}

	private async prepareRepair(
		openTurn: OpenTurn,
	): Promise<readonly [TranscriptRecordDraft, ...TranscriptRecordDraft[]]> {
		const records: TranscriptRecordDraft[] = [];
		for (const tool of openTurn.toolCalls) {
			if (tool.completed) continue;
			let recovered: TranscriptRecoveredToolResult;
			if (!tool.start) {
				recovered = syntheticNotStarted();
			} else {
				const start = structuredClone(tool.start);
				const toolCall = structuredClone(tool.call);
				recovered = (await this.recoverStartedTool?.({ start, toolCall })) ?? syntheticOutcomeUnknown();
			}
			const message = recoveredToolResultMessage(tool.call, recovered);
			const record: TranscriptRecordDraft = {
				type: "tool.completed",
				coordinate: openTurn.coordinate,
				assistantRecordId: tool.assistantRecordId,
				...(tool.start ? { startedRecordId: tool.start.recordId } : {}),
				sourceIndex: tool.sourceIndex,
				outcome: recovered.outcome,
				message,
			};
			records.push(record);
		}
		records.push({ type: "turn.closed", coordinate: openTurn.coordinate, status: "interrupted" });
		return records as [TranscriptRecordDraft, ...TranscriptRecordDraft[]];
	}
}

export class InMemoryTranscriptStore implements TranscriptRetentionStore {
	private readonly streams = new Map<string, TranscriptRecord[]>();

	async load(stream: StreamKey, maxBytes: number): Promise<TranscriptStoredStream> {
		const restoredStream = snapshotStream(stream);
		const records = this.streams.get(streamKey(restoredStream)) ?? [];
		let bytes = 0;
		const snapshot: TranscriptRecord[] = [];
		for (const record of records) {
			bytes += jsonBytes(record);
			if (bytes > maxBytes) throw new RangeError("The durable transcript exceeds the restore budget.");
			snapshot.push(structuredClone(record));
		}
		return {
			revision: records.at(-1)?.sequence ?? "empty",
			records: snapshot,
		};
	}

	async append(
		stream: StreamKey,
		expectedRevision: TranscriptRevision,
		records: readonly TranscriptRecord[],
	): Promise<TranscriptStoredStream> {
		const committedStream = snapshotStream(stream);
		const key = streamKey(committedStream);
		const current = this.streams.get(key) ?? [];
		const actual = current.at(-1)?.sequence ?? "empty";
		if (actual !== expectedRevision) throw new TranscriptWriteConflictError(expectedRevision, actual);
		const incoming = structuredClone(records);
		const recordIds = new Set(current.map(({ recordId }) => recordId));
		for (const { recordId } of incoming) {
			if (recordIds.has(recordId)) throw new TranscriptRecordIdentityConflictError();
			recordIds.add(recordId);
		}
		const committed = [...current, ...incoming];
		this.streams.set(key, committed);
		return { revision: committed.at(-1)?.sequence ?? "empty", records: structuredClone(incoming) };
	}

	async purgeBeforeCheckpoint(request: TranscriptCheckpointPurgeRequest): Promise<TranscriptCheckpointPurgeResult> {
		const captured = captureCheckpointPurgeRequest(request);
		const key = streamKey(captured);
		const records = this.streams.get(key) ?? [];
		const checkpointIndex = records.findIndex(({ sequence }) => sequence === captured.checkpointRevision);
		if (checkpointIndex < 1) throw new TypeError("The requested transcript checkpoint is unavailable.");
		const checkpoint = records[checkpointIndex];
		if (checkpoint?.type !== "context.checkpoint") throw new TypeError("The requested revision is not a checkpoint.");
		const sourceIndex = checkpointIndex - 1;
		const source = records[sourceIndex];
		if (!source) throw new TypeError("The transcript checkpoint source boundary is invalid.");
		assertExactTranscriptCheckpointBoundary(checkpoint, source);
		if (
			sourceIndex > captured.limit ||
			captured.retainRevisions.some((revision) => revision === "empty" || BigInt(revision) <= BigInt(source.sequence))
		) {
			return transcriptPurgeResult(records, 0);
		}
		this.streams.set(key, records.slice(sourceIndex));
		return transcriptPurgeResult(this.streams.get(key) ?? [], sourceIndex);
	}
}

function captureCheckpointPurgeRequest(
	request: TranscriptCheckpointPurgeRequest,
): Readonly<TranscriptCheckpointPurgeRequest> {
	const stream = snapshotStream(request);
	if (parseRevision(request.checkpointRevision) < 1n) {
		throw new TypeError("checkpointRevision must be a positive canonical decimal.");
	}
	positiveSafeInteger("limit", request.limit);
	const retainBudget = request.retainBudget;
	const retainRevisions = captureRetainedIdentifiers(request.retainRevisions, retainBudget, "retainRevisions");
	for (const revision of retainRevisions) {
		if (revision !== "empty") parseRevision(revision);
	}
	return Object.freeze({
		...stream,
		checkpointRevision: request.checkpointRevision,
		limit: request.limit,
		retainRevisions,
		retainBudget: Object.freeze({ maxCount: retainBudget.maxCount, maxBytes: retainBudget.maxBytes }),
	});
}

function transcriptPurgeResult(
	records: readonly TranscriptRecord[],
	purgedRecordCount: number,
): TranscriptCheckpointPurgeResult {
	return {
		purgedRecordCount,
		earliestAvailableRevision: records[0]?.sequence ?? null,
		latestRevision: records.at(-1)?.sequence ?? "empty",
	};
}

export function captureTranscriptMessage(
	message: Message,
	durableContent?: readonly MessageContent[],
): DurableTranscriptMessage | undefined {
	switch (message.role) {
		case "user":
			return captureUserMessage(message, durableContent);
		case "assistant": {
			if (message.stopReason === "deferred") throw new TranscriptUnsupportedDeferredError();
			if (message.stopReason !== "stop" && message.stopReason !== "length" && message.stopReason !== "toolUse") {
				return undefined;
			}
			const captured = captureAssistantContent(message);
			return {
				role: "assistant",
				content: captured.content,
				api: message.api,
				provider: message.provider,
				model: message.model,
				...(captured.providerReplay ? { providerReplay: captured.providerReplay } : {}),
				stopReason: message.stopReason,
				timestamp: message.timestamp,
			};
		}
		case "toolResult":
			return captureToolResultMessage(message, durableContent);
	}
}

function captureAssistantContent(message: AssistantMessage): {
	readonly content: DurableTranscriptAssistantMessage["content"];
	readonly providerReplay?: NonNullable<DurableTranscriptAssistantMessage["providerReplay"]>;
} {
	const captured: DurableTranscriptAssistantMessage["content"] = [];
	const replayBlocks: Array<
		| { type: "text"; textSignature?: string }
		| { type: "thinking"; thinkingSignature?: string; redacted?: boolean }
		| { type: "toolCall"; thoughtSignature?: string }
	> = [];
	const providerThinkingLevel = message.providerThinkingLevel;
	let hasReplay =
		message.responseModel !== undefined || message.responseId !== undefined || providerThinkingLevel !== undefined;
	for (const content of message.content) {
		if (content.type === "thinking") {
			captured.push({ type: "thinking", thinking: content.thinking });
			replayBlocks.push({
				type: "thinking",
				...(content.thinkingSignature === undefined ? {} : { thinkingSignature: content.thinkingSignature }),
				...(content.redacted === undefined ? {} : { redacted: content.redacted }),
			});
			if (content.thinkingSignature !== undefined || content.redacted !== undefined) hasReplay = true;
			continue;
		}
		if (content.type === "text") {
			captured.push({ type: "text", text: content.text });
			replayBlocks.push({
				type: "text",
				...(content.textSignature === undefined ? {} : { textSignature: content.textSignature }),
			});
			if (content.textSignature !== undefined) hasReplay = true;
			continue;
		}
		captured.push({
			type: "toolCall",
			id: content.id,
			name: content.name,
			arguments: content.arguments,
			...(content.namespace === undefined ? {} : { namespace: content.namespace }),
		});
		replayBlocks.push({
			type: "toolCall",
			...(content.thoughtSignature === undefined ? {} : { thoughtSignature: content.thoughtSignature }),
		});
		if (content.thoughtSignature !== undefined) hasReplay = true;
	}
	return {
		content: captured,
		...(hasReplay
			? {
					providerReplay: {
						format: "pi",
						version: "1",
						api: message.api,
						provider: message.provider,
						model: message.model,
						...(message.responseModel === undefined ? {} : { responseModel: message.responseModel }),
						...(message.responseId === undefined ? {} : { responseId: message.responseId }),
						...(providerThinkingLevel === undefined ? {} : { providerThinkingLevel }),
						stopReason: message.stopReason as "stop" | "length" | "toolUse",
						blocks: replayBlocks,
					},
				}
			: {}),
	};
}

function durableAssistantText(message: Readonly<DurableTranscriptAssistantMessage>): string | undefined {
	const text = message.content.flatMap((part) => (part.type === "text" ? [part.text] : [])).join("");
	return text.trim().length === 0 ? undefined : text;
}

function captureUserMessage(
	message: UserMessage,
	durableContent: readonly MessageContent[] | undefined,
): DurableTranscriptUserMessage {
	const messageContent =
		typeof message.content === "string" ? [{ type: "text" as const, text: message.content }] : message.content;
	const content = captureDurableMediaContent(messageContent, durableContent);
	if (typeof message.content === "string") {
		const part = content[0];
		if (part?.type !== "text" || content.length !== 1) {
			throw new TypeError("A string user message requires one transcript text part.");
		}
		return {
			role: "user",
			contentFormat: "text",
			content: [part],
			timestamp: message.timestamp,
		};
	}
	return {
		role: "user",
		contentFormat: "parts",
		content,
		timestamp: message.timestamp,
	};
}

function captureToolResultMessage(
	message: ToolResultMessage,
	durableContent: readonly MessageContent[] | undefined,
): DurableTranscriptToolResultMessage {
	return {
		role: "toolResult",
		toolCallId: message.toolCallId,
		toolName: message.toolName,
		content: captureDurableMediaContent(message.content, durableContent),
		...(message.addedToolNames?.length ? { addedToolNames: [...message.addedToolNames] } : {}),
		isError: message.isError,
		timestamp: message.timestamp,
	};
}

function captureDurableMediaContent(
	piContent: readonly ({ readonly type: "text"; readonly text: string } | ImageContent)[],
	durableContent: readonly MessageContent[] | undefined,
): DurableTranscriptToolResultMessage["content"] {
	if (durableContent === undefined) {
		return piContent.map((part) => {
			if (part.type !== "text") throw new TranscriptUnsupportedMediaError();
			return { type: "text" as const, text: part.text };
		});
	}
	if (!Array.isArray(durableContent) || durableContent.length !== piContent.length) {
		throw new TranscriptUnsupportedMediaError("Durable transcript media content is not aligned.");
	}
	return piContent.map((part, index) => {
		const durable = durableContent[index];
		if (part.type === "text") {
			if (!Value.Check(MessageContentSchema, durable) || durable.type !== "text" || durable.text !== part.text) {
				throw new TranscriptUnsupportedMediaError("Durable transcript media content is not aligned.");
			}
			return { type: "text" as const, text: durable.text };
		}
		if (durable.type !== "image-reference") {
			throw new TranscriptUnsupportedMediaError("Durable transcript media content is not aligned.");
		}
		validateImageReference(part, durable);
		return { ...durable };
	});
}

function foldRecord(
	record: TranscriptRecord,
	openTurn: OpenTurn | undefined,
	messages: DurableTranscriptMessage[],
	previousRecord?: TranscriptRecord,
): OpenTurn | undefined {
	if (record.type === "deferred.run.closed") {
		if (openTurn) throw new TypeError("The durable transcript closes a deferred run before its turn closes.");
		return undefined;
	}
	if (record.type === "context.checkpoint") {
		if (openTurn || previousRecord?.type !== "turn.closed") {
			throw new TypeError("The durable transcript checkpoint does not follow its closed source boundary.");
		}
		assertExactTranscriptCheckpointBoundary(record, previousRecord);
		messages.splice(0, messages.length, ...structuredClone(record.replacement));
		return undefined;
	}
	if (record.type === "turn.closed") {
		if (!openTurn || !sameCoordinate(openTurn.coordinate, record.coordinate)) {
			throw new TypeError("The durable transcript contains an orphan turn closure.");
		}
		if (openTurn.deferred?.status === "pending") {
			throw new TypeError("The durable transcript closes a turn with a pending provider operation.");
		}
		if (openTurn.toolCalls.some((tool) => !tool.completed)) {
			throw new TypeError("The durable transcript closes a turn with unanswered Tool calls.");
		}
		if (record.status === "completed" && !openTurn.assistantRecordId) {
			throw new TypeError("A completed durable turn requires a terminal assistant message.");
		}
		return undefined;
	}

	const current = openTurn ?? { coordinate: record.coordinate, hasUserMessage: false, toolCalls: [] };
	if (!sameCoordinate(current.coordinate, record.coordinate)) {
		throw new TypeError("The durable transcript starts a new turn before closing the previous turn.");
	}

	if (record.type === "message.completed") {
		if (record.message.role === "assistant") {
			if (current.assistantRecordId || current.deferred) {
				throw new TypeError("The durable transcript contains more than one assistant message in a turn.");
			}
			current.assistantRecordId = record.recordId;
			current.assistantStopReason = record.message.stopReason;
			current.toolCalls = openToolCalls(record.recordId, record.message);
		} else if (current.assistantRecordId || current.deferred) {
			throw new TypeError("The durable transcript inserts user input after an assistant message in the same turn.");
		} else {
			current.hasUserMessage = true;
		}
		messages.push(structuredClone(record.message));
		return current;
	}

	if (record.type === "deferred.opened") {
		if (current.deferred) {
			throw new TypeError("The durable transcript contains a second open provider operation in one turn.");
		}
		if (current.assistantRecordId) {
			throw new TypeError("The durable transcript provider operation follows an assistant in the same turn.");
		}
		current.deferred = { opened: record, status: "pending" };
		return current;
	}

	if (record.type === "deferred.completed") {
		const deferred = current.deferred;
		if (
			deferred?.status !== "pending" ||
			record.operationId !== deferred.opened.operationId ||
			record.openedRecordId !== deferred.opened.recordId
		) {
			throw new TypeError("The durable transcript provider completion does not match its open operation.");
		}
		if (
			record.message.api !== deferred.opened.handle.api ||
			record.message.provider !== deferred.opened.handle.provider ||
			record.message.model !== deferred.opened.handle.modelId
		) {
			throw new TypeError("The durable transcript provider completion does not match its provider origin.");
		}
		deferred.status = "completed";
		current.assistantRecordId = record.recordId;
		current.assistantStopReason = record.message.stopReason;
		current.toolCalls = openToolCalls(record.recordId, record.message);
		messages.push(structuredClone(record.message));
		return current;
	}

	if (record.type === "deferred.abandoned") {
		const deferred = current.deferred;
		if (
			deferred?.status !== "pending" ||
			record.operationId !== deferred.opened.operationId ||
			record.openedRecordId !== deferred.opened.recordId
		) {
			throw new TypeError("The durable transcript provider abandonment does not match its open operation.");
		}
		deferred.status = "abandoned";
		return current;
	}

	if (record.type === "tool.started") {
		const tool = findToolCall(
			current,
			record.assistantRecordId,
			record.sourceIndex,
			record.toolCallId,
			record.toolName,
		);
		if (tool.start) throw new TypeError("The durable transcript starts one Tool call more than once.");
		if (tool.completed) throw new TypeError("The durable transcript starts a Tool call after its result.");
		tool.start = record;
		return current;
	}

	const next = current.toolCalls.find((tool) => !tool.completed);
	if (!next) throw new TypeError("The durable transcript contains an orphan Tool result.");
	const tool = findToolCall(
		current,
		record.assistantRecordId,
		record.sourceIndex,
		record.message.toolCallId,
		record.message.toolName,
	);
	if (tool !== next) throw new TypeError("The durable transcript Tool results are not in assistant source order.");
	if (record.outcome !== "not_started" && !tool.start) {
		throw new TypeError("The durable transcript result requires a Tool start record.");
	}
	if (record.startedRecordId !== tool.start?.recordId) {
		throw new TypeError("The durable transcript Tool result does not match its start record.");
	}
	tool.completed = true;
	messages.push(structuredClone(record.message));
	return current;
}

function deferredRunFromTerminal(record: DurableTranscriptDeferredRunClosedRecord): TranscriptDeferredRun {
	return {
		coordinate: structuredClone(record.coordinate),
		inputId: record.inputId,
		operationId: record.operationId,
		status: record.status,
	};
}

function openToolCalls(assistantRecordId: string, message: DurableTranscriptAssistantMessage): OpenToolCall[] {
	return message.content.flatMap((content, sourceIndex) =>
		content.type === "toolCall"
			? [
					{
						assistantRecordId,
						sourceIndex,
						call: content,
						completed: false,
					},
				]
			: [],
	);
}

function findToolCall(
	turn: OpenTurn,
	assistantRecordId: string,
	sourceIndex: number,
	toolCallId: string,
	toolName: string,
): OpenToolCall {
	const tool = turn.toolCalls.find(
		(candidate) => candidate.assistantRecordId === assistantRecordId && candidate.sourceIndex === sourceIndex,
	);
	if (!tool || tool.call.id !== toolCallId || tool.call.name !== toolName) {
		throw new TypeError("The durable transcript Tool record does not match an assistant call.");
	}
	return tool;
}

async function materializeMessages(
	stream: Readonly<StreamKey>,
	messages: readonly DurableTranscriptMessage[],
	media: TranscriptMedia | undefined,
	signal: AbortSignal,
): Promise<readonly AgentMessage[]> {
	signal.throwIfAborted();
	const requests: TranscriptMediaReadRequest[] = [];
	for (const message of messages) {
		if (message.role === "assistant") continue;
		for (const part of message.content) {
			if (part.type !== "image-reference") continue;
			requests.push(
				Object.freeze({
					stream,
					role: message.role,
					reference: Object.freeze({ ...part }),
					...(message.role === "toolResult" ? { toolCallId: message.toolCallId, toolName: message.toolName } : {}),
				}),
			);
		}
	}
	let images: readonly Readonly<ImageContent>[] = [];
	if (requests.length > 0) {
		if (!media) throw new TranscriptUnsupportedMediaError();
		images = await media.read(Object.freeze(requests), signal);
		signal.throwIfAborted();
		if (!Array.isArray(images) || images.length !== requests.length) {
			throw new TranscriptUnsupportedMediaError("The transcript media Adapter returned a misaligned read batch.");
		}
	}
	let imageIndex = 0;
	const materialized = messages.map((message) =>
		materializeMessage(message, (reference) => {
			const image = images[imageIndex];
			const request = requests[imageIndex];
			imageIndex += 1;
			if (!image || !request || request.reference.attachmentId !== reference.attachmentId) {
				throw new TranscriptUnsupportedMediaError("The transcript media Adapter returned a misaligned read batch.");
			}
			return validateHydratedImage(image, reference);
		}),
	);
	return Object.freeze(materialized);
}

function materializeMessage(
	message: DurableTranscriptMessage,
	takeImage: (reference: Readonly<ImageReferenceContent>) => ImageContent,
): AgentMessage {
	if (message.role === "user") {
		const content = message.content.map((part) =>
			part.type === "text" ? { type: "text" as const, text: part.text } : takeImage(part),
		);
		const first = content[0];
		if (!first) throw new TypeError("A durable user message is empty.");
		if (message.contentFormat === "text" && first.type !== "text") {
			throw new TypeError("A durable text user message contains media.");
		}
		return {
			role: "user",
			content: message.contentFormat === "text" && first.type === "text" ? first.text : content,
			timestamp: message.timestamp,
		};
	}
	if (message.role === "toolResult") {
		return {
			role: "toolResult",
			toolCallId: message.toolCallId,
			toolName: message.toolName,
			content: message.content.map((part) =>
				part.type === "text" ? { type: "text" as const, text: part.text } : takeImage(part),
			),
			...(message.addedToolNames ? { addedToolNames: [...message.addedToolNames] } : {}),
			isError: message.isError,
			timestamp: message.timestamp,
		};
	}
	return {
		role: "assistant",
		content: materializeAssistantContent(message),
		api: message.api as AssistantMessage["api"],
		provider: message.provider as AssistantMessage["provider"],
		model: message.model,
		...(message.providerReplay?.responseModel === undefined
			? {}
			: { responseModel: message.providerReplay.responseModel }),
		...(message.providerReplay?.responseId === undefined ? {} : { responseId: message.providerReplay.responseId }),
		...(message.providerReplay?.providerThinkingLevel === undefined
			? {}
			: { providerThinkingLevel: message.providerReplay.providerThinkingLevel }),
		usage: {
			input: 0,
			output: 0,
			cacheRead: 0,
			cacheWrite: 0,
			totalTokens: 0,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		},
		stopReason: message.stopReason,
		timestamp: message.timestamp,
	};
}

function materializeAssistantContent(message: DurableTranscriptAssistantMessage): AssistantMessage["content"] {
	const replay = message.providerReplay;
	return message.content.map((part, index) => {
		const replayBlock = replay?.blocks[index];
		if (part.type === "thinking") {
			return {
				type: "thinking" as const,
				thinking: part.thinking,
				...(replayBlock?.type === "thinking" && replayBlock.thinkingSignature !== undefined
					? { thinkingSignature: replayBlock.thinkingSignature }
					: {}),
				...(replayBlock?.type === "thinking" && replayBlock.redacted !== undefined
					? { redacted: replayBlock.redacted }
					: {}),
			};
		}
		if (part.type === "text") {
			return {
				type: "text" as const,
				text: part.text,
				...(replayBlock?.type === "text" && replayBlock.textSignature !== undefined
					? { textSignature: replayBlock.textSignature }
					: {}),
			};
		}
		return {
			type: "toolCall" as const,
			id: part.id,
			name: part.name,
			arguments: structuredClone(part.arguments) as Record<string, unknown>,
			...(replayBlock?.type === "toolCall" && replayBlock.thoughtSignature !== undefined
				? { thoughtSignature: replayBlock.thoughtSignature }
				: {}),
			...(part.namespace === undefined ? {} : { namespace: part.namespace }),
		};
	});
}

function snapshotDurableMessages(messages: readonly DurableTranscriptMessage[]): readonly DurableTranscriptMessage[] {
	if (!Array.isArray(messages)) throw new TypeError("The durable transcript message list is invalid.");
	const snapshot = structuredClone(messages);
	for (const message of snapshot) {
		if (!Value.Check(DurableTranscriptMessageSchema, message)) {
			throw new TypeError("The durable transcript message is invalid.");
		}
		if (message.role === "assistant") assertAssistantReplayAlignment(message);
	}
	return Object.freeze(snapshot);
}

function snapshotPiMediaContent(
	content: readonly ({ readonly type: "text"; readonly text: string } | ImageContent)[],
): readonly ({ readonly type: "text"; readonly text: string } | ImageContent)[] {
	if (!Array.isArray(content)) throw new TranscriptUnsupportedMediaError("The Tool media content is invalid.");
	return Object.freeze(
		content.map((part) => {
			if (part?.type === "text" && typeof part.text === "string") {
				return Object.freeze({ type: "text" as const, text: part.text });
			}
			if (part?.type === "image") return validatePiImage(part);
			throw new TranscriptUnsupportedMediaError("The Tool media content is invalid.");
		}),
	);
}

function validateCapturedToolResult(
	source: readonly ({ readonly type: "text"; readonly text: string } | ImageContent)[],
	captured: Readonly<PreparedTranscriptToolResultMedia>,
): Readonly<PreparedTranscriptToolResultMedia> {
	const content = captured?.content;
	const durableContent = captured?.durableContent;
	if (
		!Array.isArray(content) ||
		!Array.isArray(durableContent) ||
		content.length !== source.length ||
		durableContent.length !== source.length
	) {
		throw new TranscriptUnsupportedMediaError("The Tool media capture result is not aligned.");
	}
	const normalized: Array<{ readonly type: "text"; readonly text: string } | ImageContent> = [];
	const durable: MessageContent[] = [];
	for (let index = 0; index < source.length; index += 1) {
		const original = source[index];
		const next = content[index];
		const reference = durableContent[index];
		if (!original || !next || !reference) {
			throw new TranscriptUnsupportedMediaError("The Tool media capture result is invalid.");
		}
		if (original.type === "text") {
			if (
				!Value.Check(MessageContentSchema, reference) ||
				next.type !== "text" ||
				reference.type !== "text" ||
				next.text !== original.text ||
				reference.text !== original.text
			) {
				throw new TranscriptUnsupportedMediaError("The Tool media capture result is not aligned.");
			}
			normalized.push(Object.freeze({ type: "text", text: original.text }));
			durable.push(Object.freeze({ type: "text", text: original.text }));
			continue;
		}
		if (next.type !== "image" || reference.type !== "image-reference") {
			throw new TranscriptUnsupportedMediaError("The Tool media capture result is not aligned.");
		}
		const image = validateImageReference(next, reference);
		normalized.push(image);
		durable.push(Object.freeze({ ...reference }));
	}
	return Object.freeze({ content: Object.freeze(normalized), durableContent: Object.freeze(durable) });
}

function validateHydratedImage(
	image: Readonly<ImageContent>,
	reference: Readonly<ImageReferenceContent>,
): ImageContent {
	return validateImageReference(image, reference, true);
}

function validateImageReference(
	image: Readonly<ImageContent>,
	reference: Readonly<ImageReferenceContent>,
	referenceValidated = false,
): ImageContent {
	if (!referenceValidated && !Value.Check(ImageReferenceContentSchema, reference)) {
		throw new TranscriptUnsupportedMediaError("The transcript media reference is invalid.");
	}
	const { content, bytes } = decodePiImage(image);
	const attachmentId = `img_${createHash("sha256").update(bytes).digest("hex")}`;
	if (
		content.mimeType !== reference.mediaType ||
		bytes.byteLength !== reference.bytes ||
		attachmentId !== reference.attachmentId
	) {
		throw new TranscriptUnsupportedMediaError("The transcript media content does not match its durable reference.");
	}
	return content;
}

function validatePiImage(image: Readonly<ImageContent>): ImageContent {
	return decodePiImage(image).content;
}

function decodePiImage(image: Readonly<ImageContent>): { readonly content: ImageContent; readonly bytes: Buffer } {
	if (
		image?.type !== "image" ||
		typeof image.data !== "string" ||
		image.data.length === 0 ||
		typeof image.mimeType !== "string" ||
		image.mimeType.length === 0
	) {
		throw new TranscriptUnsupportedMediaError("The transcript media Adapter returned invalid image content.");
	}
	const decoded = decodeCanonicalBase64(image.data);
	if (!decoded) {
		throw new TranscriptUnsupportedMediaError("The transcript media Adapter returned invalid base64 image content.");
	}
	return Object.freeze({
		content: Object.freeze({ type: "image", data: image.data, mimeType: image.mimeType }),
		bytes: decoded,
	});
}

function recoveredToolResultMessage(
	toolCall: DurableTranscriptToolCallContent,
	recovered: TranscriptRecoveredToolResult,
): DurableTranscriptToolResultMessage {
	return {
		role: "toolResult",
		toolCallId: toolCall.id,
		toolName: toolCall.name,
		content: recovered.content.map((part) => ({ type: "text", text: part.text })),
		...(recovered.addedToolNames?.length ? { addedToolNames: [...recovered.addedToolNames] } : {}),
		isError: recovered.isError,
		timestamp: recovered.timestamp,
	};
}

function syntheticNotStarted(): TranscriptRecoveredToolResult {
	return {
		outcome: "not_started",
		content: [{ type: "text", text: "transcript_tool_not_started: the previous run ended before execution started." }],
		isError: true,
		timestamp: Date.now(),
	};
}

function syntheticOutcomeUnknown(): TranscriptRecoveredToolResult {
	return {
		outcome: "outcome_unknown",
		content: [
			{
				type: "text",
				text: "transcript_tool_outcome_unknown: the previous run ended after execution started; check authoritative state before any retry.",
			},
		],
		isError: true,
		timestamp: Date.now(),
	};
}

function snapshotRecord(record: unknown, maxBytes: number, maxDepth: number): TranscriptRecord {
	const normalized = normalizeJsonValueWithin(record as unknown as JsonValue, { maxBytes, maxDepth });
	assertDurableTranscriptRecord(normalized);
	assertRecordReplayAlignment(normalized);
	parseRevision(normalized.sequence);
	return Object.freeze(normalized);
}

function assertRecordReplayAlignment(record: TranscriptRecord): void {
	if (record.type === "message.completed") {
		if (record.message.role === "assistant") assertAssistantReplayAlignment(record.message);
		return;
	}
	if (record.type === "deferred.completed") {
		assertAssistantReplayAlignment(record.message);
		return;
	}
	if (record.type !== "context.checkpoint") return;
	for (const message of record.replacement) {
		if (message.role === "assistant") assertAssistantReplayAlignment(message);
	}
}

function assertAssistantReplayAlignment(message: DurableTranscriptAssistantMessage): void {
	const replay = message.providerReplay;
	if (!replay) return;
	if (
		replay.api !== message.api ||
		replay.provider !== message.provider ||
		replay.model !== message.model ||
		replay.stopReason !== message.stopReason ||
		replay.blocks.length !== message.content.length
	) {
		throw new TypeError("The durable provider replay envelope does not match its assistant message.");
	}
	for (let index = 0; index < message.content.length; index += 1) {
		if (message.content[index]?.type !== replay.blocks[index]?.type) {
			throw new TypeError("The durable provider replay block does not match its assistant content.");
		}
	}
}

function positiveSafeInteger(name: string, value: number): number {
	if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer.`);
	return value;
}

function parseRevision(value: string): bigint {
	if (!/^(0|[1-9][0-9]*)$/u.test(value)) throw new TypeError("Transcript revisions must be canonical decimals.");
	return BigInt(value);
}

function validateStream(stream: StreamKey): void {
	assertOpaqueIdentifier(stream.sessionId);
	assertOpaqueIdentifier(stream.actorId);
}

function snapshotStream(stream: StreamKey): Readonly<StreamKey> {
	const snapshot = { sessionId: stream.sessionId, actorId: stream.actorId };
	validateStream(snapshot);
	return Object.freeze(snapshot);
}

function snapshotRevision(value: TranscriptRevision, maxBytes: number): TranscriptRevision {
	if (value === "empty") return value;
	if (typeof value !== "string") throw new TypeError("The durable transcript revision is invalid.");
	if (value.length > maxBytes) throw new RangeError("The durable transcript revision exceeds its record budget.");
	parseRevision(value);
	return value;
}

function snapshotRecordListLength(records: readonly unknown[], minimum: 0 | 1): number {
	if (!Array.isArray(records)) throw new TypeError("The durable transcript record list is invalid.");
	const length = records.length;
	if (!Number.isSafeInteger(length) || length < minimum) {
		throw new TypeError("The durable transcript record list is invalid.");
	}
	return length;
}

function sameCoordinate(left: RunCoordinate, right: RunCoordinate): boolean {
	return (
		left.sessionId === right.sessionId &&
		left.actorId === right.actorId &&
		left.runId === right.runId &&
		left.turn === right.turn
	);
}

function streamKey(stream: StreamKey): string {
	return JSON.stringify([stream.sessionId, stream.actorId]);
}

function jsonBytes(value: unknown): number {
	return Buffer.byteLength(canonicalizeJsonValue(value as JsonValue));
}

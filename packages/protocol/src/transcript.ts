import { type Static, Type } from "typebox";
import { Value } from "typebox/value";
import type { RetentionProtectionBudget } from "./retention.js";
import { RunAdmissionAuthoritySchema } from "./run-authority.js";
import {
	DecimalSchema,
	ImageReferenceContentSchema,
	OpaqueIdentifierSchema,
	RunCoordinateSchema,
	Sha256Schema,
	type StreamKey,
} from "./schema.js";

export const TRANSCRIPT_SCHEMA_VERSION = "6" as const;

export const DurableTranscriptTextContentSchema = Type.Object(
	{
		type: Type.Literal("text"),
		text: Type.String(),
	},
	{ additionalProperties: false },
);

const DurableTranscriptMessageContentSchema = Type.Union([
	DurableTranscriptTextContentSchema,
	ImageReferenceContentSchema,
]);

export const DurableTranscriptThinkingContentSchema = Type.Object(
	{
		type: Type.Literal("thinking"),
		thinking: Type.String(),
	},
	{ additionalProperties: false },
);

const DurableTranscriptReplayTextBlockSchema = Type.Object(
	{
		type: Type.Literal("text"),
		textSignature: Type.Optional(Type.String()),
	},
	{ additionalProperties: false },
);

const DurableTranscriptReplayThinkingBlockSchema = Type.Object(
	{
		type: Type.Literal("thinking"),
		thinkingSignature: Type.Optional(Type.String()),
		redacted: Type.Optional(Type.Boolean()),
	},
	{ additionalProperties: false },
);

const DurableTranscriptReplayToolCallBlockSchema = Type.Object(
	{
		type: Type.Literal("toolCall"),
		thoughtSignature: Type.Optional(Type.String()),
	},
	{ additionalProperties: false },
);

export const DurableTranscriptProviderReplaySchema = Type.Object(
	{
		format: Type.Literal("pi"),
		version: Type.Literal("1"),
		api: Type.String(),
		provider: Type.String(),
		model: Type.String(),
		responseModel: Type.Optional(Type.String()),
		responseId: Type.Optional(Type.String()),
		providerThinkingLevel: Type.Optional(Type.String()),
		stopReason: Type.Union([Type.Literal("stop"), Type.Literal("length"), Type.Literal("toolUse")]),
		blocks: Type.Array(
			Type.Union([
				DurableTranscriptReplayTextBlockSchema,
				DurableTranscriptReplayThinkingBlockSchema,
				DurableTranscriptReplayToolCallBlockSchema,
			]),
		),
	},
	{ additionalProperties: false },
);

const DurableTranscriptArgumentsSchema = Type.Object({}, { additionalProperties: true });

export const DurableTranscriptToolCallContentSchema = Type.Object(
	{
		type: Type.Literal("toolCall"),
		id: OpaqueIdentifierSchema,
		name: OpaqueIdentifierSchema,
		arguments: DurableTranscriptArgumentsSchema,
		namespace: Type.Optional(OpaqueIdentifierSchema),
	},
	{ additionalProperties: false },
);

const DurableTranscriptTimestampSchema = Type.Integer({
	minimum: Number.MIN_SAFE_INTEGER,
	maximum: Number.MAX_SAFE_INTEGER,
});

const DurableTranscriptJsonValueSchema = Type.Cyclic(
	{
		Json: Type.Union([
			Type.String(),
			Type.Number(),
			Type.Boolean(),
			Type.Null(),
			Type.Array(Type.Ref("Json")),
			Type.Record(Type.String(), Type.Ref("Json")),
		]),
	},
	"Json",
);

export const DurableTranscriptDeferredHandleSchema = Type.Object(
	{
		provider: Type.String(),
		modelId: Type.String(),
		api: Type.String(),
		id: Type.String(),
		expiresAt: Type.Optional(DurableTranscriptTimestampSchema),
		pollAfterMs: Type.Optional(Type.Integer({ minimum: 0, maximum: Number.MAX_SAFE_INTEGER })),
		data: Type.Optional(DurableTranscriptJsonValueSchema),
	},
	{ additionalProperties: false },
);

export const DurableTranscriptDeferredInputSchema = Type.Object(
	{
		inputId: OpaqueIdentifierSchema,
		payload: DurableTranscriptJsonValueSchema,
		admissionAuthority: Type.Optional(RunAdmissionAuthoritySchema),
	},
	{ additionalProperties: false },
);

export const DurableTranscriptUserMessageSchema = Type.Union([
	Type.Object(
		{
			role: Type.Literal("user"),
			contentFormat: Type.Literal("text"),
			content: Type.Tuple([DurableTranscriptTextContentSchema]),
			timestamp: DurableTranscriptTimestampSchema,
		},
		{ additionalProperties: false },
	),
	Type.Object(
		{
			role: Type.Literal("user"),
			contentFormat: Type.Literal("parts"),
			content: Type.Array(DurableTranscriptMessageContentSchema),
			timestamp: DurableTranscriptTimestampSchema,
		},
		{ additionalProperties: false },
	),
]);

export const DurableTranscriptAssistantMessageSchema = Type.Object(
	{
		role: Type.Literal("assistant"),
		content: Type.Array(
			Type.Union([
				DurableTranscriptTextContentSchema,
				DurableTranscriptThinkingContentSchema,
				DurableTranscriptToolCallContentSchema,
			]),
		),
		api: Type.String(),
		provider: Type.String(),
		model: Type.String(),
		providerReplay: Type.Optional(DurableTranscriptProviderReplaySchema),
		stopReason: Type.Union([Type.Literal("stop"), Type.Literal("length"), Type.Literal("toolUse")]),
		timestamp: DurableTranscriptTimestampSchema,
	},
	{ additionalProperties: false },
);

export const DurableTranscriptToolResultMessageSchema = Type.Object(
	{
		role: Type.Literal("toolResult"),
		toolCallId: OpaqueIdentifierSchema,
		toolName: OpaqueIdentifierSchema,
		content: Type.Array(DurableTranscriptMessageContentSchema),
		addedToolNames: Type.Optional(Type.Array(OpaqueIdentifierSchema)),
		isError: Type.Boolean(),
		timestamp: DurableTranscriptTimestampSchema,
	},
	{ additionalProperties: false },
);

export const DurableTranscriptCompletedMessageSchema = Type.Union([
	DurableTranscriptUserMessageSchema,
	DurableTranscriptAssistantMessageSchema,
]);

export const DurableTranscriptMessageSchema = Type.Union([
	DurableTranscriptCompletedMessageSchema,
	DurableTranscriptToolResultMessageSchema,
]);

const ContextCheckpointDataProperties = {
	schemaVersion: Type.Literal(TRANSCRIPT_SCHEMA_VERSION),
	type: Type.Literal("context.checkpoint"),
	coordinate: RunCoordinateSchema,
	sourceRevision: DecimalSchema,
	sourceRecordId: OpaqueIdentifierSchema,
	replacement: Type.Array(DurableTranscriptMessageSchema, { minItems: 1 }),
};

const MessageCompletedDataProperties = {
	schemaVersion: Type.Literal(TRANSCRIPT_SCHEMA_VERSION),
	type: Type.Literal("message.completed"),
	coordinate: RunCoordinateSchema,
	message: DurableTranscriptCompletedMessageSchema,
};

const ToolStartedDataProperties = {
	schemaVersion: Type.Literal(TRANSCRIPT_SCHEMA_VERSION),
	type: Type.Literal("tool.started"),
	coordinate: RunCoordinateSchema,
	assistantRecordId: OpaqueIdentifierSchema,
	sourceIndex: Type.Integer({ minimum: 0, maximum: Number.MAX_SAFE_INTEGER }),
	toolCallId: OpaqueIdentifierSchema,
	toolName: OpaqueIdentifierSchema,
	arguments: DurableTranscriptArgumentsSchema,
	descriptorFingerprint: Sha256Schema,
	catalogRevision: OpaqueIdentifierSchema,
	policyRevision: OpaqueIdentifierSchema,
	operationId: Type.Optional(OpaqueIdentifierSchema),
};

const ToolCompletedDataProperties = {
	schemaVersion: Type.Literal(TRANSCRIPT_SCHEMA_VERSION),
	type: Type.Literal("tool.completed"),
	coordinate: RunCoordinateSchema,
	assistantRecordId: OpaqueIdentifierSchema,
	startedRecordId: Type.Optional(OpaqueIdentifierSchema),
	sourceIndex: Type.Integer({ minimum: 0, maximum: Number.MAX_SAFE_INTEGER }),
	outcome: Type.Union([Type.Literal("completed"), Type.Literal("not_started"), Type.Literal("outcome_unknown")]),
	message: DurableTranscriptToolResultMessageSchema,
};

const TurnClosedDataProperties = {
	schemaVersion: Type.Literal(TRANSCRIPT_SCHEMA_VERSION),
	type: Type.Literal("turn.closed"),
	coordinate: RunCoordinateSchema,
	status: Type.Union([Type.Literal("completed"), Type.Literal("interrupted")]),
};

const DeferredOpenedDataProperties = {
	schemaVersion: Type.Literal(TRANSCRIPT_SCHEMA_VERSION),
	type: Type.Literal("deferred.opened"),
	coordinate: RunCoordinateSchema,
	operationId: OpaqueIdentifierSchema,
	input: DurableTranscriptDeferredInputSchema,
	handle: DurableTranscriptDeferredHandleSchema,
	finalOutput: Type.Optional(Type.String({ minLength: 1, pattern: "\\S" })),
	reportedTokensBeforeDeferred: Type.Optional(Type.Integer({ minimum: 0, maximum: Number.MAX_SAFE_INTEGER })),
};

const DeferredCompletedDataProperties = {
	schemaVersion: Type.Literal(TRANSCRIPT_SCHEMA_VERSION),
	type: Type.Literal("deferred.completed"),
	coordinate: RunCoordinateSchema,
	operationId: OpaqueIdentifierSchema,
	openedRecordId: OpaqueIdentifierSchema,
	message: DurableTranscriptAssistantMessageSchema,
};

const DeferredAbandonedDataProperties = {
	schemaVersion: Type.Literal(TRANSCRIPT_SCHEMA_VERSION),
	type: Type.Literal("deferred.abandoned"),
	coordinate: RunCoordinateSchema,
	operationId: OpaqueIdentifierSchema,
	openedRecordId: OpaqueIdentifierSchema,
	reason: Type.Literal("cancelled"),
};

const DeferredRunClosedDataProperties = {
	schemaVersion: Type.Literal(TRANSCRIPT_SCHEMA_VERSION),
	type: Type.Literal("deferred.run.closed"),
	coordinate: RunCoordinateSchema,
	operationId: OpaqueIdentifierSchema,
	inputId: OpaqueIdentifierSchema,
	status: Type.Union([Type.Literal("completed"), Type.Literal("aborted"), Type.Literal("failed")]),
};

export const DurableTranscriptRecordDataSchema = Type.Union([
	Type.Object(MessageCompletedDataProperties, { additionalProperties: false }),
	Type.Object(ToolStartedDataProperties, { additionalProperties: false }),
	Type.Object(ToolCompletedDataProperties, { additionalProperties: false }),
	Type.Object(TurnClosedDataProperties, { additionalProperties: false }),
	Type.Object(ContextCheckpointDataProperties, { additionalProperties: false }),
	Type.Object(DeferredOpenedDataProperties, { additionalProperties: false }),
	Type.Object(DeferredCompletedDataProperties, { additionalProperties: false }),
	Type.Object(DeferredAbandonedDataProperties, { additionalProperties: false }),
	Type.Object(DeferredRunClosedDataProperties, { additionalProperties: false }),
]);

const RecordEnvelopeProperties = {
	recordId: OpaqueIdentifierSchema,
	sequence: DecimalSchema,
};

export const DurableTranscriptRecordSchema = Type.Union([
	Type.Object({ ...MessageCompletedDataProperties, ...RecordEnvelopeProperties }, { additionalProperties: false }),
	Type.Object({ ...ToolStartedDataProperties, ...RecordEnvelopeProperties }, { additionalProperties: false }),
	Type.Object({ ...ToolCompletedDataProperties, ...RecordEnvelopeProperties }, { additionalProperties: false }),
	Type.Object({ ...TurnClosedDataProperties, ...RecordEnvelopeProperties }, { additionalProperties: false }),
	Type.Object({ ...ContextCheckpointDataProperties, ...RecordEnvelopeProperties }, { additionalProperties: false }),
	Type.Object({ ...DeferredOpenedDataProperties, ...RecordEnvelopeProperties }, { additionalProperties: false }),
	Type.Object({ ...DeferredCompletedDataProperties, ...RecordEnvelopeProperties }, { additionalProperties: false }),
	Type.Object({ ...DeferredAbandonedDataProperties, ...RecordEnvelopeProperties }, { additionalProperties: false }),
	Type.Object({ ...DeferredRunClosedDataProperties, ...RecordEnvelopeProperties }, { additionalProperties: false }),
]);

export type DurableTranscriptTextContent = Static<typeof DurableTranscriptTextContentSchema>;
export type DurableTranscriptThinkingContent = Static<typeof DurableTranscriptThinkingContentSchema>;
export type DurableTranscriptProviderReplay = Static<typeof DurableTranscriptProviderReplaySchema>;
export type DurableTranscriptToolCallContent = Static<typeof DurableTranscriptToolCallContentSchema>;
export type DurableTranscriptUserMessage = Static<typeof DurableTranscriptUserMessageSchema>;
export type DurableTranscriptAssistantMessage = Static<typeof DurableTranscriptAssistantMessageSchema>;
export type DurableTranscriptToolResultMessage = Static<typeof DurableTranscriptToolResultMessageSchema>;
export type DurableTranscriptCompletedMessage = Static<typeof DurableTranscriptCompletedMessageSchema>;
export type DurableTranscriptMessage = Static<typeof DurableTranscriptMessageSchema>;
export type DurableTranscriptDeferredHandle = Static<typeof DurableTranscriptDeferredHandleSchema>;
export type DurableTranscriptDeferredInput = Static<typeof DurableTranscriptDeferredInputSchema>;
export type DurableTranscriptRecordData = Static<typeof DurableTranscriptRecordDataSchema>;
export type DurableTranscriptRecord = Static<typeof DurableTranscriptRecordSchema>;
export type DurableTranscriptMessageCompletedRecord = Extract<DurableTranscriptRecord, { type: "message.completed" }>;
export type DurableTranscriptToolStartedRecord = Extract<DurableTranscriptRecord, { type: "tool.started" }>;
export type DurableTranscriptToolCompletedRecord = Extract<DurableTranscriptRecord, { type: "tool.completed" }>;
export type DurableTranscriptTurnClosedRecord = Extract<DurableTranscriptRecord, { type: "turn.closed" }>;
export type DurableTranscriptContextCheckpointRecord = Extract<DurableTranscriptRecord, { type: "context.checkpoint" }>;
export type DurableTranscriptDeferredOpenedRecord = Extract<DurableTranscriptRecord, { type: "deferred.opened" }>;
export type DurableTranscriptDeferredCompletedRecord = Extract<DurableTranscriptRecord, { type: "deferred.completed" }>;
export type DurableTranscriptDeferredAbandonedRecord = Extract<DurableTranscriptRecord, { type: "deferred.abandoned" }>;
export type DurableTranscriptDeferredRunClosedRecord = Extract<
	DurableTranscriptRecord,
	{ type: "deferred.run.closed" }
>;
export type TranscriptRevision = "empty" | string;

export interface TranscriptStoredStream {
	revision: TranscriptRevision;
	records: readonly DurableTranscriptRecord[];
}

export interface TranscriptStore {
	load(stream: StreamKey, maxBytes: number): Promise<TranscriptStoredStream>;
	append(
		stream: StreamKey,
		expectedRevision: TranscriptRevision,
		records: readonly DurableTranscriptRecord[],
	): Promise<TranscriptStoredStream>;
}

export interface TranscriptCheckpointPurgeRequest extends StreamKey {
	readonly checkpointRevision: string;
	readonly limit: number;
	readonly retainRevisions: readonly TranscriptRevision[];
	readonly retainBudget: RetentionProtectionBudget;
}

export interface TranscriptCheckpointPurgeResult {
	readonly purgedRecordCount: number;
	readonly earliestAvailableRevision: string | null;
	readonly latestRevision: TranscriptRevision;
}

export interface TranscriptRetentionStore extends TranscriptStore {
	purgeBeforeCheckpoint(request: TranscriptCheckpointPurgeRequest): Promise<TranscriptCheckpointPurgeResult>;
}

export class TranscriptWriteConflictError extends Error {
	constructor(
		public readonly expected: TranscriptRevision,
		public readonly actual: TranscriptRevision,
	) {
		super("The durable transcript revision did not match.");
		this.name = "TranscriptWriteConflictError";
	}
}

export class TranscriptRecordIdentityConflictError extends Error {
	constructor() {
		super("The durable transcript record identity already exists in this stream.");
		this.name = "TranscriptRecordIdentityConflictError";
	}
}

export class TranscriptProtocolValidationError extends TypeError {
	constructor() {
		super("invalid_transcript_record");
		this.name = "TranscriptProtocolValidationError";
	}
}

export function assertDurableTranscriptRecordData(value: unknown): asserts value is DurableTranscriptRecordData {
	if (!Value.Check(DurableTranscriptRecordDataSchema, value)) throw new TranscriptProtocolValidationError();
}

export function assertDurableTranscriptRecord(value: unknown): asserts value is DurableTranscriptRecord {
	if (!Value.Check(DurableTranscriptRecordSchema, value)) throw new TranscriptProtocolValidationError();
}

export function safeRetainedBoundaries(messages: readonly DurableTranscriptMessage[]): readonly number[] {
	let pending: Array<{ id: string; name: string }> = [];
	const boundaries: number[] = [];
	for (let index = 0; index < messages.length; index += 1) {
		const message = messages[index];
		if (!message) throw new TypeError("The transcript history contains a missing message.");
		if (pending.length > 0) {
			const expected = pending[0];
			if (message.role !== "toolResult" || message.toolCallId !== expected?.id || message.toolName !== expected.name) {
				throw new TypeError("The transcript history contains an unbalanced Tool call.");
			}
			pending = pending.slice(1);
			if (pending.length === 0) boundaries.push(index + 1);
			continue;
		}
		if (message.role === "toolResult") throw new TypeError("The transcript history contains an orphan Tool result.");
		if (message.role === "assistant") {
			pending = message.content.flatMap((part) => (part.type === "toolCall" ? [{ id: part.id, name: part.name }] : []));
			if (pending.length > 0) continue;
		}
		boundaries.push(index + 1);
	}
	if (pending.length > 0) throw new TypeError("The transcript history contains an unbalanced Tool call.");
	return boundaries;
}

export function assertExactTranscriptCheckpointBoundary(
	checkpoint: DurableTranscriptContextCheckpointRecord,
	source: DurableTranscriptRecord,
): asserts source is DurableTranscriptTurnClosedRecord {
	if (
		source.type !== "turn.closed" ||
		source.status !== "completed" ||
		BigInt(checkpoint.sequence) !== BigInt(source.sequence) + 1n ||
		checkpoint.sourceRevision !== source.sequence ||
		checkpoint.sourceRecordId !== source.recordId ||
		checkpoint.coordinate.sessionId !== source.coordinate.sessionId ||
		checkpoint.coordinate.actorId !== source.coordinate.actorId ||
		checkpoint.coordinate.runId !== source.coordinate.runId ||
		checkpoint.coordinate.turn !== source.coordinate.turn
	) {
		throw new TypeError("The transcript checkpoint source boundary is invalid.");
	}
	safeRetainedBoundaries(checkpoint.replacement);
}

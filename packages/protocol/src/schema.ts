import { type Static, Type } from "typebox";
import { Value } from "typebox/value";

export const PROTOCOL_VERSION = "1" as const;

export const OpaqueIdentifierSchema = Type.String({
	minLength: 1,
	pattern: "^[^\\u0000-\\u001F\\u007F-\\u009F]+$",
});

export const DecimalSchema = Type.String({ pattern: "^(0|[1-9][0-9]*)$" });
export const RunAdmissionAuthoritySchema = Type.Object(
	{ scope: OpaqueIdentifierSchema, generation: DecimalSchema },
	{ additionalProperties: false },
);
const PositiveDecimalSchema = Type.String({ pattern: "^[1-9][0-9]*$" });
export const Sha256Schema = Type.String({ pattern: "^[0-9a-f]{64}$" });
export const CanonicalTimestampSchema = Type.String({
	pattern: "^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]\\.[0-9]{3}Z$",
});

export const StreamKeySchema = Type.Object(
	{
		sessionId: OpaqueIdentifierSchema,
		actorId: OpaqueIdentifierSchema,
	},
	{ additionalProperties: false },
);

export const RunCoordinateSchema = Type.Object(
	{
		sessionId: OpaqueIdentifierSchema,
		actorId: OpaqueIdentifierSchema,
		runId: OpaqueIdentifierSchema,
		turn: DecimalSchema,
	},
	{ additionalProperties: false },
);

export const GameTimeSchema = Type.Object(
	{
		timelineId: OpaqueIdentifierSchema,
		generation: DecimalSchema,
		tick: DecimalSchema,
	},
	{ additionalProperties: false },
);

export const AudienceSchema = Type.Union([
	Type.Object({ visibility: Type.Literal("internal") }, { additionalProperties: false }),
	Type.Object({ visibility: Type.Literal("owner") }, { additionalProperties: false }),
	Type.Object({ visibility: Type.Literal("public") }, { additionalProperties: false }),
	Type.Object(
		{
			visibility: Type.Literal("recipient"),
			recipientIds: Type.Array(OpaqueIdentifierSchema, { minItems: 1, uniqueItems: true }),
		},
		{ additionalProperties: false },
	),
]);

export const TextContentSchema = Type.Object(
	{
		type: Type.Literal("text"),
		text: Type.String(),
	},
	{ additionalProperties: false },
);

export const ImageMediaTypeSchema = Type.Union([
	Type.Literal("image/png"),
	Type.Literal("image/jpeg"),
	Type.Literal("image/webp"),
	Type.Literal("image/gif"),
]);

export const ImageAttachmentIdSchema = Type.String({ pattern: "^img_[0-9a-f]{64}$" });

export const ImageReferenceContentSchema = Type.Object(
	{
		type: Type.Literal("image-reference"),
		attachmentId: ImageAttachmentIdSchema,
		mediaType: ImageMediaTypeSchema,
		bytes: Type.Integer({ minimum: 1, maximum: Number.MAX_SAFE_INTEGER }),
		width: Type.Integer({ minimum: 1, maximum: Number.MAX_SAFE_INTEGER }),
		height: Type.Integer({ minimum: 1, maximum: Number.MAX_SAFE_INTEGER }),
		name: Type.Optional(Type.String({ minLength: 1, pattern: "^[^\\u0000-\\u001F\\u007F-\\u009F]+$" })),
	},
	{ additionalProperties: false },
);

export const MessageContentSchema = Type.Union([TextContentSchema, ImageReferenceContentSchema]);

const InternalContextImageReferenceSchema = Type.Omit(ImageReferenceContentSchema, ["name"], {
	additionalProperties: false,
});
const InternalContextMessageContentSchema = Type.Unsafe<
	readonly [Static<typeof TextContentSchema>, ...Array<Static<typeof InternalContextImageReferenceSchema>>]
>({
	type: "array",
	minItems: 1,
	prefixItems: [TextContentSchema],
	items: InternalContextImageReferenceSchema,
});

export const AssistantTextContentSchema = Type.Object(
	{
		type: Type.Literal("text"),
		text: Type.String(),
		sourceIndex: Type.Integer({ minimum: 0, maximum: Number.MAX_SAFE_INTEGER }),
	},
	{ additionalProperties: false },
);

export const AssistantReasoningSummaryContentSchema = Type.Object(
	{
		type: Type.Literal("reasoning-summary"),
		text: Type.String(),
		sourceIndex: Type.Integer({ minimum: 0, maximum: Number.MAX_SAFE_INTEGER }),
	},
	{ additionalProperties: false },
);

export const AssistantMessageContentSchema = Type.Union([
	AssistantTextContentSchema,
	AssistantReasoningSummaryContentSchema,
]);

export const ToolMediaSummaryContentSchema = Type.Object(
	{
		type: Type.Literal("media-summary"),
		mediaType: Type.String({ minLength: 1 }),
		bytes: Type.Integer({ minimum: 1, maximum: Number.MAX_SAFE_INTEGER }),
		sha256: Sha256Schema,
	},
	{ additionalProperties: false },
);

export const ToolResultContentSchema = Type.Union([TextContentSchema, ToolMediaSummaryContentSchema]);

export const ContextReferenceSchema = Type.Object(
	{
		sourceId: OpaqueIdentifierSchema,
		referenceId: OpaqueIdentifierSchema,
		revision: OpaqueIdentifierSchema,
		contentFingerprint: Sha256Schema,
	},
	{ additionalProperties: false },
);

export const ContextSnapshotSchema = Type.Object(
	{
		contextVersion: Type.Literal("1"),
		fingerprint: Sha256Schema,
		references: Type.Array(ContextReferenceSchema, { minItems: 1 }),
	},
	{ additionalProperties: false },
);

export const MessageItemSchema = Type.Union([
	Type.Object(
		{
			itemType: Type.Literal("message"),
			itemId: OpaqueIdentifierSchema,
			role: Type.Literal("user"),
			content: Type.Array(MessageContentSchema),
			audience: AudienceSchema,
		},
		{ additionalProperties: false },
	),
	Type.Object(
		{
			itemType: Type.Literal("message"),
			itemId: OpaqueIdentifierSchema,
			role: Type.Literal("assistant"),
			content: Type.Array(AssistantMessageContentSchema),
			audience: AudienceSchema,
		},
		{ additionalProperties: false },
	),
	Type.Object(
		{
			itemType: Type.Literal("message"),
			itemId: OpaqueIdentifierSchema,
			role: Type.Literal("user"),
			content: InternalContextMessageContentSchema,
			audience: Type.Object({ visibility: Type.Literal("internal") }, { additionalProperties: false }),
			context: ContextSnapshotSchema,
		},
		{ additionalProperties: false },
	),
]);

export const ToolCallItemSchema = Type.Object(
	{
		itemType: Type.Literal("tool-call"),
		itemId: OpaqueIdentifierSchema,
		toolCallId: OpaqueIdentifierSchema,
		toolName: OpaqueIdentifierSchema,
		parentMessageItemId: Type.Optional(OpaqueIdentifierSchema),
		sourceIndex: Type.Optional(Type.Integer({ minimum: 0, maximum: Number.MAX_SAFE_INTEGER })),
		audience: AudienceSchema,
	},
	{ additionalProperties: false },
);

export const ToolResultItemSchema = Type.Object(
	{
		itemType: Type.Literal("tool-result"),
		itemId: OpaqueIdentifierSchema,
		toolCallId: OpaqueIdentifierSchema,
		toolName: OpaqueIdentifierSchema,
		isError: Type.Boolean(),
		content: Type.Array(ToolResultContentSchema),
		audience: AudienceSchema,
	},
	{ additionalProperties: false },
);

export const CanonicalItemSchema = Type.Union([MessageItemSchema, ToolCallItemSchema, ToolResultItemSchema]);

export const FinishReasonSchema = Type.Union([
	Type.Literal("stop"),
	Type.Literal("tool-use"),
	Type.Literal("length"),
	Type.Literal("aborted"),
	Type.Literal("error"),
	Type.Literal("deferred"),
	Type.Literal("other"),
]);

export const ToolRepeatPolicyActionSchema = Type.Union([Type.Literal("advisory"), Type.Literal("terminated")], {
	title: "ToolRepeatPolicyAction",
});

export const CanonicalEventDataSchema = Type.Union([
	Type.Object(
		{
			type: Type.Literal("run.queued"),
			inputId: OpaqueIdentifierSchema,
			inputFingerprint: Sha256Schema,
			admissionAuthority: Type.Optional(RunAdmissionAuthoritySchema),
		},
		{ additionalProperties: false },
	),
	Type.Object({ type: Type.Literal("run.started"), inputId: OpaqueIdentifierSchema }, { additionalProperties: false }),
	Type.Object(
		{ type: Type.Literal("run.resumed"), operationId: OpaqueIdentifierSchema },
		{ additionalProperties: false },
	),
	Type.Object(
		{ type: Type.Literal("run.suspended"), operationId: OpaqueIdentifierSchema },
		{ additionalProperties: false },
	),
	Type.Object({ type: Type.Literal("turn.started") }, { additionalProperties: false }),
	Type.Object({ type: Type.Literal("item.started"), item: CanonicalItemSchema }, { additionalProperties: false }),
	Type.Object(
		{
			type: Type.Literal("item.delta"),
			itemId: OpaqueIdentifierSchema,
			audience: AudienceSchema,
			delta: Type.Object(
				{
					type: Type.Literal("text"),
					contentIndex: Type.Integer({ minimum: 0, maximum: Number.MAX_SAFE_INTEGER }),
					text: Type.String(),
				},
				{ additionalProperties: false },
			),
		},
		{ additionalProperties: false },
	),
	Type.Object(
		{
			type: Type.Literal("item.updated"),
			itemId: OpaqueIdentifierSchema,
			audience: AudienceSchema,
			updateOrdinal: DecimalSchema,
			content: Type.Array(ToolResultContentSchema),
		},
		{ additionalProperties: false },
	),
	Type.Object({ type: Type.Literal("item.completed"), item: CanonicalItemSchema }, { additionalProperties: false }),
	Type.Object(
		{
			type: Type.Literal("turn.completed"),
			finishReason: FinishReasonSchema,
			toolResultItemIds: Type.Array(OpaqueIdentifierSchema),
		},
		{ additionalProperties: false },
	),
	Type.Object(
		{
			type: Type.Literal("control.accepted"),
			control: Type.Union([Type.Literal("abort"), Type.Literal("steer"), Type.Literal("follow-up")]),
		},
		{ additionalProperties: false },
	),
	Type.Object(
		{
			type: Type.Literal("tool.repeat.detected"),
			toolCallId: OpaqueIdentifierSchema,
			toolName: OpaqueIdentifierSchema,
			consecutiveCount: PositiveDecimalSchema,
			action: ToolRepeatPolicyActionSchema,
		},
		{ additionalProperties: false },
	),
	Type.Object(
		{
			type: Type.Literal("run.completed"),
			status: Type.Union([Type.Literal("completed"), Type.Literal("aborted"), Type.Literal("failed")]),
		},
		{ additionalProperties: false },
	),
	Type.Object(
		{
			type: Type.Literal("run.metrics"),
			unit: Type.Literal("microseconds"),
			segmentStatus: Type.Union([
				Type.Literal("completed"),
				Type.Literal("aborted"),
				Type.Literal("failed"),
				Type.Literal("deferred"),
			]),
			durations: Type.Object(
				{
					contextAndRecall: DecimalSchema,
					provider: DecimalSchema,
					durable: DecimalSchema,
					native: DecimalSchema,
					total: DecimalSchema,
					firstResponse: Type.Optional(DecimalSchema),
					providerFirstResponse: Type.Optional(DecimalSchema),
					firstTool: Type.Optional(DecimalSchema),
					admission: Type.Optional(DecimalSchema),
					queue: Type.Optional(DecimalSchema),
					memoryPrepare: Type.Optional(DecimalSchema),
					memorySearch: Type.Optional(DecimalSchema),
					tool: Type.Optional(DecimalSchema),
					approval: Type.Optional(DecimalSchema),
					settlement: Type.Optional(DecimalSchema),
				},
				{ additionalProperties: false },
			),
			counts: Type.Object(
				{
					providerAttempts: DecimalSchema,
					retries: DecimalSchema,
					fallbacks: DecimalSchema,
					toolCalls: DecimalSchema,
				},
				{ additionalProperties: false },
			),
		},
		{ additionalProperties: false },
	),
]);

export const CanonicalEventSchema = Type.Object(
	{
		protocolVersion: Type.Literal(PROTOCOL_VERSION),
		eventId: OpaqueIdentifierSchema,
		sequence: DecimalSchema,
		revision: DecimalSchema,
		occurredAt: CanonicalTimestampSchema,
		coordinate: RunCoordinateSchema,
		audience: AudienceSchema,
		data: CanonicalEventDataSchema,
	},
	{ additionalProperties: false },
);

export type StreamKey = Static<typeof StreamKeySchema>;
export type RunCoordinate = Static<typeof RunCoordinateSchema>;
export type GameTime = Static<typeof GameTimeSchema>;
export type Audience = Static<typeof AudienceSchema>;
export type TextContent = Static<typeof TextContentSchema>;
export type ImageMediaType = Static<typeof ImageMediaTypeSchema>;
export type ImageReferenceContent = Static<typeof ImageReferenceContentSchema>;
export type MessageContent = Static<typeof MessageContentSchema>;
export type AssistantTextContent = Static<typeof AssistantTextContentSchema>;
export type AssistantReasoningSummaryContent = Static<typeof AssistantReasoningSummaryContentSchema>;
export type AssistantMessageContent = Static<typeof AssistantMessageContentSchema>;
export type ToolMediaSummaryContent = Static<typeof ToolMediaSummaryContentSchema>;
export type ToolResultContent = Static<typeof ToolResultContentSchema>;
export type ContextReference = Static<typeof ContextReferenceSchema>;
export type ContextSnapshot = Static<typeof ContextSnapshotSchema>;
export type CanonicalItem = Static<typeof CanonicalItemSchema>;
export type FinishReason = Static<typeof FinishReasonSchema>;
export type ToolRepeatPolicyAction = Static<typeof ToolRepeatPolicyActionSchema>;
export type CanonicalEventData = Static<typeof CanonicalEventDataSchema>;
export type CanonicalEvent = Static<typeof CanonicalEventSchema>;

export function canonicalAssistantText(item: CanonicalItem): string | undefined {
	if (item.itemType !== "message" || item.role !== "assistant") return undefined;
	const text = item.content.flatMap((part) => (part.type === "text" ? [part.text] : [])).join("");
	return text.trim().length > 0 ? text : undefined;
}

export class ProtocolValidationError extends TypeError {
	constructor(
		public readonly code:
			| "invalid_identifier"
			| "invalid_image_media_type"
			| "invalid_game_time"
			| "invalid_event_data"
			| "invalid_event"
			| "invalid_stream_coordinate"
			| "invalid_timestamp",
	) {
		super(code);
		this.name = "ProtocolValidationError";
	}
}

export function assertOpaqueIdentifier(value: string): void {
	if (!Value.Check(OpaqueIdentifierSchema, value)) {
		throw new ProtocolValidationError("invalid_identifier");
	}
}

export function assertImageMediaType(value: unknown): asserts value is ImageMediaType {
	if (!Value.Check(ImageMediaTypeSchema, value)) {
		throw new ProtocolValidationError("invalid_image_media_type");
	}
}

export function assertGameTime(value: unknown): asserts value is GameTime {
	if (!Value.Check(GameTimeSchema, value)) {
		throw new ProtocolValidationError("invalid_game_time");
	}
}

export function assertCanonicalTimestamp(value: unknown): asserts value is string {
	if (typeof value !== "string" || !Value.Check(CanonicalTimestampSchema, value)) {
		throw new ProtocolValidationError("invalid_timestamp");
	}
	const timestamp = new Date(value);
	if (!Number.isFinite(timestamp.getTime()) || timestamp.toISOString() !== value) {
		throw new ProtocolValidationError("invalid_timestamp");
	}
}

export function assertEventStreamCoordinate(stream: StreamKey, coordinate: RunCoordinate): void {
	if (
		!Value.Check(StreamKeySchema, stream) ||
		!Value.Check(RunCoordinateSchema, coordinate) ||
		stream.sessionId !== coordinate.sessionId ||
		stream.actorId !== coordinate.actorId
	) {
		throw new ProtocolValidationError("invalid_stream_coordinate");
	}
}

export function assertCanonicalEventData(value: unknown): asserts value is CanonicalEventData {
	if (!Value.Check(CanonicalEventDataSchema, value)) {
		throw new ProtocolValidationError("invalid_event_data");
	}
	assertRunMetrics(value);
}

export function assertCanonicalEvent(value: unknown): asserts value is CanonicalEvent {
	if (!Value.Check(CanonicalEventSchema, value)) {
		throw new ProtocolValidationError("invalid_event");
	}
	if (!audiencesEqual(value.audience, eventAudience(value.data))) {
		throw new ProtocolValidationError("invalid_event");
	}
	assertCanonicalTimestamp(value.occurredAt);
	assertRunMetrics(value.data);
}

function assertRunMetrics(data: CanonicalEventData): void {
	if (data.type !== "run.metrics") return;
	const { durations, counts } = data;
	const attempts = BigInt(counts.providerAttempts);
	const retries = BigInt(counts.retries);
	const total = BigInt(durations.total);
	if (
		retries > (attempts === 0n ? 0n : attempts - 1n) ||
		BigInt(counts.fallbacks) > retries ||
		BigInt(durations.contextAndRecall) + BigInt(durations.provider) + BigInt(durations.native) !== total ||
		BigInt(durations.durable) > total ||
		(durations.firstResponse !== undefined && BigInt(durations.firstResponse) > total) ||
		(durations.firstTool !== undefined && BigInt(durations.firstTool) > total) ||
		(durations.providerFirstResponse !== undefined && BigInt(durations.providerFirstResponse) > total)
	)
		throw new ProtocolValidationError("invalid_event_data");
}

function audiencesEqual(left: Audience, right: Audience): boolean {
	if (left.visibility !== right.visibility) return false;
	if (left.visibility !== "recipient" || right.visibility !== "recipient") return true;
	if (left.recipientIds.length !== right.recipientIds.length) return false;
	return left.recipientIds.every((recipientId, index) => recipientId === right.recipientIds[index]);
}

export function eventAudience(data: CanonicalEventData): Audience {
	switch (data.type) {
		case "item.started":
		case "item.completed":
			return data.item.audience;
		case "item.delta":
		case "item.updated":
			return data.audience;
		default:
			return { visibility: "owner" };
	}
}

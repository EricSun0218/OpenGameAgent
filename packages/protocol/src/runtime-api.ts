import { type Static, Type, type TSchema } from "typebox";
import { Value } from "typebox/value";
import {
	RunAdmissionAuthoritySchema,
	RunAuthorityKeySchema,
	CloseRunAuthorityRequestSchema,
	RunAuthorityClosureSchema,
	captureCloseRunAuthorityRequest,
} from "./run-authority.js";
import {
	ActionApprovalRecordSchema,
	ActionAttemptSchema,
	ActionAuthoritySchema,
	ActionOperationIdSchema,
	ActionRecordSchema,
} from "./actions.js";
import {
	CanonicalEventSchema,
	DecimalSchema,
	ImageReferenceContentSchema,
	OpaqueIdentifierSchema,
	RunCoordinateSchema,
	Sha256Schema,
} from "./schema.js";

export const RUNTIME_API_VERSION = "1" as const;

export const RUNTIME_CAPABILITIES = [
	"runs.submit",
	"authority.close",
	"deferred.lifecycle",
	"controls.exact",
	"events.page",
	"events.sse",
	"events.reconcile-gap",
	"audience.owner",
	"audience.recipient",
	"actions.exchange",
	"actions.sse",
	"approvals.exact",
	"attachments.read",
	"health.snapshot",
] as const;

export const RUNTIME_BASE_CAPABILITIES = RUNTIME_CAPABILITIES.filter(
	(capability) =>
		capability !== "actions.exchange" &&
		capability !== "actions.sse" &&
		capability !== "approvals.exact" &&
		capability !== "attachments.read" &&
		capability !== "health.snapshot",
);

export const JsonValueSchema = Type.Unknown({
	description: "A losslessly representable JSON value validated at the transport boundary.",
});

export const RuntimeInitializeResponseSchema = Type.Object(
	{
		protocolVersion: Type.Literal(RUNTIME_API_VERSION),
		capabilities: Type.Array(Type.Union(RUNTIME_CAPABILITIES.map((capability) => Type.Literal(capability)))),
	},
	{ additionalProperties: false },
);

export const RuntimeStreamSelectorSchema = Type.Object(
	{
		sessionId: OpaqueIdentifierSchema,
		actorId: OpaqueIdentifierSchema,
	},
	{ additionalProperties: false },
);

export const RuntimeRunRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		inputId: OpaqueIdentifierSchema,
		payload: JsonValueSchema,
		admissionAuthority: Type.Optional(RunAdmissionAuthoritySchema),
	},
	{ additionalProperties: false },
);

export const RuntimeRunAcceptedSchema = Type.Object(
	{
		runId: OpaqueIdentifierSchema,
		initialCoordinate: RunCoordinateSchema,
		eventCursor: DecimalSchema,
	},
	{ additionalProperties: false },
);

export const RuntimeAuthorityCloseRequestSchema = CloseRunAuthorityRequestSchema;
export const RuntimeAuthorityReadRequestSchema = RunAuthorityKeySchema;
export const RuntimeAuthorityClosureSchema = RunAuthorityClosureSchema;
export const RuntimeAuthorityReadResponseSchema = Type.Object(
	{ closure: Type.Union([RunAuthorityClosureSchema, Type.Null()]) },
	{ additionalProperties: false },
);

export const RuntimeDeferredOperationSchema = Type.Object(
	{
		operationId: OpaqueIdentifierSchema,
		inputId: OpaqueIdentifierSchema,
		coordinate: RunCoordinateSchema,
	},
	{ additionalProperties: false },
);

export const RuntimeDeferredReadRequestSchema = RuntimeStreamSelectorSchema;

export const RuntimeDeferredReadResponseSchema = Type.Object(
	{ deferred: Type.Union([RuntimeDeferredOperationSchema, Type.Null()]) },
	{ additionalProperties: false },
);

export const RuntimeDeferredCommandRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		operationId: OpaqueIdentifierSchema,
	},
	{ additionalProperties: false },
);

export const RuntimeDeferredAbandonResponseSchema = Type.Object(
	{
		operationId: OpaqueIdentifierSchema,
		providerCancellation: Type.Union([Type.Literal("completed"), Type.Literal("failed"), Type.Literal("unavailable")]),
	},
	{ additionalProperties: false },
);

export const RuntimeEventPageRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		afterRevision: Type.Optional(DecimalSchema),
		limit: Type.Integer({ minimum: 1 }),
	},
	{ additionalProperties: false },
);

export const RuntimeEventPageSchema = Type.Object(
	{
		kind: Type.Literal("page"),
		requestedAfterRevision: DecimalSchema,
		events: Type.Array(CanonicalEventSchema),
		cursor: DecimalSchema,
		hasMore: Type.Boolean(),
		requiresTranscriptReconciliation: Type.Literal(false),
	},
	{ additionalProperties: false },
);

export const RuntimeEventGapSchema = Type.Object(
	{
		kind: Type.Literal("gap"),
		requestedAfterRevision: DecimalSchema,
		earliestAvailableRevision: Type.Union([DecimalSchema, Type.Null()]),
		nextAfterRevision: DecimalSchema,
		requiresTranscriptReconciliation: Type.Literal(true),
	},
	{ additionalProperties: false },
);

export const RuntimeEventReplaySchema = Type.Union([RuntimeEventPageSchema, RuntimeEventGapSchema]);

export const RuntimeControlCommandSchema = Type.Union([
	Type.Object({ type: Type.Literal("abort") }, { additionalProperties: false }),
	Type.Object({ type: Type.Literal("steer"), text: Type.String({ minLength: 1 }) }, { additionalProperties: false }),
	Type.Object(
		{ type: Type.Literal("follow-up"), text: Type.String({ minLength: 1 }) },
		{ additionalProperties: false },
	),
]);

export const RuntimeControlRequestSchema = Type.Object(
	{
		coordinate: RunCoordinateSchema,
		command: RuntimeControlCommandSchema,
	},
	{ additionalProperties: false },
);

export const RuntimeControlResultReasonSchema = Type.Union([
	Type.Literal("no-active-run"),
	Type.Literal("coordinate-mismatch"),
	Type.Literal("invalid-message"),
	Type.Literal("run-not-active"),
]);

export const RuntimeControlResultSchema = Type.Union([
	Type.Object({ accepted: Type.Literal(true), coordinate: RunCoordinateSchema }, { additionalProperties: false }),
	Type.Object(
		{
			accepted: Type.Literal(false),
			reason: RuntimeControlResultReasonSchema,
			activeCoordinate: Type.Optional(RunCoordinateSchema),
		},
		{ additionalProperties: false },
	),
]);

export const RuntimeErrorSchema = Type.Object(
	{
		error: Type.Object(
			{
				code: Type.String(),
				message: Type.String(),
			},
			{ additionalProperties: false },
		),
	},
	{ additionalProperties: false },
);

export const RuntimeActionDeliverySchema = Type.Object(
	{
		kind: Type.Union([Type.Literal("dispatch"), Type.Literal("reconcile")]),
		action: ActionRecordSchema,
	},
	{ additionalProperties: false },
);
export const RuntimeActionRecoveryDeliverySchema = Type.Object(
	{
		kind: Type.Literal("reconcile"),
		action: ActionRecordSchema,
	},
	{ additionalProperties: false },
);
export const RuntimeActionNextRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		runId: Type.Optional(OpaqueIdentifierSchema),
	},
	{ additionalProperties: false },
);
export const RuntimeActionNextResponseSchema = Type.Object(
	{ delivery: Type.Union([RuntimeActionDeliverySchema, Type.Null()]) },
	{ additionalProperties: false },
);
export const RuntimeActionRecoveryRequestSchema = Type.Object(RuntimeStreamSelectorSchema.properties, {
	additionalProperties: false,
});
export const RuntimeActionRecoveryResponseSchema = Type.Object(
	{ delivery: Type.Union([RuntimeActionRecoveryDeliverySchema, Type.Null()]) },
	{ additionalProperties: false },
);
export const RuntimeActionSseFrameSchema = Type.Object(
	{ type: Type.Literal("delivery"), delivery: RuntimeActionDeliverySchema },
	{ additionalProperties: false },
);
export const RuntimeActionGetRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		operationId: ActionOperationIdSchema,
	},
	{ additionalProperties: false },
);
export const RuntimeActionGetResponseSchema = Type.Object(
	{ action: Type.Union([ActionRecordSchema, Type.Null()]) },
	{ additionalProperties: false },
);
export const RuntimeActionReceiptRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		operationId: ActionOperationIdSchema,
		claimToken: OpaqueIdentifierSchema,
		attempt: ActionAttemptSchema,
		outcome: Type.Union([Type.Literal("succeeded"), Type.Literal("rejected"), Type.Literal("unknown")]),
		result: Type.Optional(Type.Unknown()),
		modelResult: Type.Optional(Type.Unknown()),
		authorityAfter: Type.Optional(ActionAuthoritySchema),
	},
	{ additionalProperties: false },
);
export const RuntimeActionReconcileRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		operationId: ActionOperationIdSchema,
		claimToken: OpaqueIdentifierSchema,
		attempt: ActionAttemptSchema,
		resolution: Type.Union([Type.Literal("applied"), Type.Literal("not-applied"), Type.Literal("unknown")]),
		result: Type.Optional(Type.Unknown()),
		modelResult: Type.Optional(Type.Unknown()),
		authorityAfter: Type.Optional(ActionAuthoritySchema),
	},
	{ additionalProperties: false },
);
export const RuntimeActionRecordResponseSchema = Type.Object(
	{ action: ActionRecordSchema },
	{ additionalProperties: false },
);
export const RuntimeApprovalListRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		runId: Type.Optional(OpaqueIdentifierSchema),
		limit: Type.Integer({ minimum: 1 }),
	},
	{ additionalProperties: false },
);
export const RuntimeApprovalListResponseSchema = Type.Object(
	{ approvals: Type.Array(ActionApprovalRecordSchema) },
	{ additionalProperties: false },
);
export const RuntimeApprovalWaitRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		runId: OpaqueIdentifierSchema,
		limit: Type.Integer({ minimum: 1 }),
		waitMilliseconds: Type.Integer({ minimum: 1 }),
		afterCursor: Type.Optional(Sha256Schema),
	},
	{ additionalProperties: false },
);
export const RuntimeApprovalWaitResponseSchema = Type.Object(
	{
		approvals: Type.Array(ActionApprovalRecordSchema),
		cursor: Sha256Schema,
	},
	{ additionalProperties: false },
);
export const RuntimeApprovalDecisionRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		approvalId: Type.String({ pattern: "^apr_[0-9a-f]{64}$" }),
		expectedRevision: DecimalSchema,
		requestFingerprint: Sha256Schema,
		decision: Type.Union([Type.Literal("approve"), Type.Literal("reject")]),
		reason: Type.Optional(Type.String()),
	},
	{ additionalProperties: false },
);
export const RuntimeApprovalDecisionResponseSchema = Type.Object(
	{ approval: ActionApprovalRecordSchema },
	{ additionalProperties: false },
);

export const RuntimeAttachmentReadRequestSchema = Type.Object(
	{
		...RuntimeStreamSelectorSchema.properties,
		reference: ImageReferenceContentSchema,
	},
	{ additionalProperties: false },
);

export const RuntimeSseFrameSchema = Type.Union([
	Type.Object({ type: Type.Literal("event"), event: CanonicalEventSchema }, { additionalProperties: false }),
	Type.Object({ type: Type.Literal("cursor"), revision: DecimalSchema }, { additionalProperties: false }),
	Type.Object({ type: Type.Literal("gap"), gap: RuntimeEventGapSchema }, { additionalProperties: false }),
]);

export type RuntimeInitializeResponse = Static<typeof RuntimeInitializeResponseSchema>;
export type RuntimeStreamSelector = Static<typeof RuntimeStreamSelectorSchema>;
export type RuntimeRunRequest = Static<typeof RuntimeRunRequestSchema>;
export type RuntimeRunAccepted = Static<typeof RuntimeRunAcceptedSchema>;
export type RuntimeAuthorityCloseRequest = Static<typeof RuntimeAuthorityCloseRequestSchema>;
export type RuntimeAuthorityReadRequest = Static<typeof RuntimeAuthorityReadRequestSchema>;
export type RuntimeAuthorityClosure = Static<typeof RuntimeAuthorityClosureSchema>;
export type RuntimeAuthorityReadResponse = Static<typeof RuntimeAuthorityReadResponseSchema>;
export type RuntimeDeferredOperation = Static<typeof RuntimeDeferredOperationSchema>;
export type RuntimeDeferredReadRequest = Static<typeof RuntimeDeferredReadRequestSchema>;
export type RuntimeDeferredReadResponse = Static<typeof RuntimeDeferredReadResponseSchema>;
export type RuntimeDeferredCommandRequest = Static<typeof RuntimeDeferredCommandRequestSchema>;
export type RuntimeDeferredAbandonResponse = Static<typeof RuntimeDeferredAbandonResponseSchema>;
export type RuntimeEventPageRequest = Static<typeof RuntimeEventPageRequestSchema>;
export type RuntimeEventReplay = Static<typeof RuntimeEventReplaySchema>;
export type RuntimeControlRequest = Static<typeof RuntimeControlRequestSchema>;
export type RuntimeControlResultReason = Static<typeof RuntimeControlResultReasonSchema>;
export type RuntimeControlResult = Static<typeof RuntimeControlResultSchema>;
export type RuntimeError = Static<typeof RuntimeErrorSchema>;
export type RuntimeSseFrame = Static<typeof RuntimeSseFrameSchema>;
export type RuntimeActionDelivery = Static<typeof RuntimeActionDeliverySchema>;
export type RuntimeActionRecoveryDelivery = Static<typeof RuntimeActionRecoveryDeliverySchema>;
export type RuntimeActionNextRequest = Static<typeof RuntimeActionNextRequestSchema>;
export type RuntimeActionNextResponse = Static<typeof RuntimeActionNextResponseSchema>;
export type RuntimeActionRecoveryRequest = Static<typeof RuntimeActionRecoveryRequestSchema>;
export type RuntimeActionRecoveryResponse = Static<typeof RuntimeActionRecoveryResponseSchema>;
export type RuntimeActionSseFrame = Static<typeof RuntimeActionSseFrameSchema>;
export type RuntimeActionGetRequest = Static<typeof RuntimeActionGetRequestSchema>;
export type RuntimeActionGetResponse = Static<typeof RuntimeActionGetResponseSchema>;
export type RuntimeActionReceiptRequest = Static<typeof RuntimeActionReceiptRequestSchema>;
export type RuntimeActionReconcileRequest = Static<typeof RuntimeActionReconcileRequestSchema>;
export type RuntimeActionRecordResponse = Static<typeof RuntimeActionRecordResponseSchema>;
export type RuntimeApprovalListRequest = Static<typeof RuntimeApprovalListRequestSchema>;
export type RuntimeApprovalListResponse = Static<typeof RuntimeApprovalListResponseSchema>;
export type RuntimeApprovalWaitRequest = Static<typeof RuntimeApprovalWaitRequestSchema>;
export type RuntimeApprovalWaitResponse = Static<typeof RuntimeApprovalWaitResponseSchema>;
export type RuntimeApprovalDecisionRequest = Static<typeof RuntimeApprovalDecisionRequestSchema>;
export type RuntimeApprovalDecisionResponse = Static<typeof RuntimeApprovalDecisionResponseSchema>;
export type RuntimeAttachmentReadRequest = Static<typeof RuntimeAttachmentReadRequestSchema>;

export class RuntimeProtocolValidationError extends TypeError {
	constructor() {
		super("The Runtime response does not satisfy the canonical protocol schema.");
		this.name = "RuntimeProtocolValidationError";
	}
}

export function assertRuntimeRunAccepted(value: unknown): asserts value is RuntimeRunAccepted {
	assertRuntimeSchema(RuntimeRunAcceptedSchema, value);
}

export function assertRuntimeAuthorityClosure(value: unknown): asserts value is RuntimeAuthorityClosure {
	assertRuntimeSchema(RuntimeAuthorityClosureSchema, value);
	const closure = value as RuntimeAuthorityClosure;
	if ((closure.workRetirement === undefined) !== (closure.workRetirementResult === undefined)) {
		throw new RuntimeProtocolValidationError();
	}
	try {
		captureCloseRunAuthorityRequest(closure);
	} catch {
		throw new RuntimeProtocolValidationError();
	}
}

export function assertRuntimeAuthorityReadResponse(value: unknown): asserts value is RuntimeAuthorityReadResponse {
	assertRuntimeSchema(RuntimeAuthorityReadResponseSchema, value);
	const response = value as RuntimeAuthorityReadResponse;
	if (response.closure !== null) assertRuntimeAuthorityClosure(response.closure);
}

export function assertRuntimeSseFrame(value: unknown): asserts value is RuntimeSseFrame {
	assertRuntimeSchema(RuntimeSseFrameSchema, value);
}

export function assertRuntimeActionSseFrame(value: unknown): asserts value is RuntimeActionSseFrame {
	assertRuntimeSchema(RuntimeActionSseFrameSchema, value);
}

export function assertRuntimeActionRecoveryResponse(value: unknown): asserts value is RuntimeActionRecoveryResponse {
	assertRuntimeSchema(RuntimeActionRecoveryResponseSchema, value);
}

export function assertRuntimeApprovalListResponse(value: unknown): asserts value is RuntimeApprovalListResponse {
	assertRuntimeSchema(RuntimeApprovalListResponseSchema, value);
}

export function assertRuntimeApprovalWaitResponse(value: unknown): asserts value is RuntimeApprovalWaitResponse {
	assertRuntimeSchema(RuntimeApprovalWaitResponseSchema, value);
}

export function assertRuntimeApprovalDecisionResponse(
	value: unknown,
): asserts value is RuntimeApprovalDecisionResponse {
	assertRuntimeSchema(RuntimeApprovalDecisionResponseSchema, value);
}

export function assertRuntimeControlResult(value: unknown): asserts value is RuntimeControlResult {
	assertRuntimeSchema(RuntimeControlResultSchema, value);
}

function assertRuntimeSchema(schema: TSchema, value: unknown): void {
	if (!Value.Check(schema, value)) throw new RuntimeProtocolValidationError();
}

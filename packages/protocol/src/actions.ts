import { type Static, Type } from "typebox";
import { Value } from "typebox/value";
import {
	CanonicalTimestampSchema,
	DecimalSchema,
	OpaqueIdentifierSchema,
	RunCoordinateSchema,
	Sha256Schema,
	StreamKeySchema,
} from "./schema.js";

export const ActionOperationIdSchema = Type.String({ pattern: "^act_[0-9a-f]{64}$" });
export const ActionAttemptSchema = Type.String({ pattern: "^[1-9][0-9]*$" });
export const ActionReplayPolicySchema = Type.Union([
	Type.Literal("never"),
	Type.Literal("safe"),
	Type.Literal("recoverable"),
]);
export const ActionStatusSchema = Type.Union([
	Type.Literal("pending"),
	Type.Literal("claimed"),
	Type.Literal("succeeded"),
	Type.Literal("failed"),
	Type.Literal("uncertain"),
]);
export const ActionAuthoritySchema = Type.Object(
	{
		timelineId: OpaqueIdentifierSchema,
		generation: DecimalSchema,
		revision: DecimalSchema,
	},
	{ additionalProperties: false },
);
export const ActionGoalProjectionBindingSchema = Type.Object(
	{
		goalId: OpaqueIdentifierSchema,
		partition: Type.Object(
			{
				ownerId: OpaqueIdentifierSchema,
				timelineId: OpaqueIdentifierSchema,
				generation: DecimalSchema,
				sessionId: Type.Optional(OpaqueIdentifierSchema),
				actorId: Type.Optional(OpaqueIdentifierSchema),
			},
			{ additionalProperties: false },
		),
		stepId: OpaqueIdentifierSchema,
		expectedRevision: DecimalSchema,
		inputId: OpaqueIdentifierSchema,
	},
	{ additionalProperties: false },
);
export const ActionGoalProjectionSettlementSchema = Type.Object(
	{
		outcome: Type.Union([Type.Literal("advanced"), Type.Literal("source_failed")]),
		settledAt: CanonicalTimestampSchema,
	},
	{ additionalProperties: false },
);
export const ActionClaimSchema = Type.Object(
	{
		claimToken: OpaqueIdentifierSchema,
		claimantId: OpaqueIdentifierSchema,
		attempt: ActionAttemptSchema,
		claimedAt: CanonicalTimestampSchema,
		expiresAt: CanonicalTimestampSchema,
	},
	{ additionalProperties: false },
);
export const ActionReceiptSchema = Type.Object(
	{
		operationId: ActionOperationIdSchema,
		claimToken: OpaqueIdentifierSchema,
		attempt: ActionAttemptSchema,
		outcome: Type.Union([Type.Literal("succeeded"), Type.Literal("rejected"), Type.Literal("unknown")]),
		receivedAt: CanonicalTimestampSchema,
		result: Type.Optional(Type.Unknown()),
		modelResult: Type.Optional(Type.Unknown()),
		authorityAfter: Type.Optional(ActionAuthoritySchema),
	},
	{ additionalProperties: false },
);
export const ActionRecordSchema = Type.Object(
	{
		...StreamKeySchema.properties,
		operationId: ActionOperationIdSchema,
		intentFingerprint: Sha256Schema,
		payloadFingerprint: Sha256Schema,
		coordinate: RunCoordinateSchema,
		actionName: OpaqueIdentifierSchema,
		operationKey: OpaqueIdentifierSchema,
		payload: Type.Unknown(),
		expectedAuthority: ActionAuthoritySchema,
		conflictKeys: Type.Array(OpaqueIdentifierSchema),
		replayPolicy: ActionReplayPolicySchema,
		goalProjection: Type.Optional(ActionGoalProjectionBindingSchema),
		status: ActionStatusSchema,
		attempt: DecimalSchema,
		createdAt: CanonicalTimestampSchema,
		updatedAt: CanonicalTimestampSchema,
		claim: Type.Optional(ActionClaimSchema),
		receipt: Type.Optional(ActionReceiptSchema),
		goalProjectionSettlement: Type.Optional(ActionGoalProjectionSettlementSchema),
	},
	{ additionalProperties: false },
);

export const ActionApprovalStatusSchema = Type.Union([
	Type.Literal("pending"),
	Type.Literal("approved"),
	Type.Literal("rejected"),
	Type.Literal("consumed"),
	Type.Literal("expired"),
]);
export const ActionApprovalRequestSchema = Type.Object(
	{
		...StreamKeySchema.properties,
		coordinate: RunCoordinateSchema,
		approvalId: Type.String({ pattern: "^apr_[0-9a-f]{64}$" }),
		requestFingerprint: Sha256Schema,
		policyId: OpaqueIdentifierSchema,
		operationId: ActionOperationIdSchema,
		intentFingerprint: Sha256Schema,
		actionName: OpaqueIdentifierSchema,
		expectedAuthority: ActionAuthoritySchema,
		taskId: Type.Optional(OpaqueIdentifierSchema),
		presentation: Type.Optional(
			Type.Unknown({ description: "Host-projected JSON data for presenting this exact approval request." }),
		),
		requestedAt: CanonicalTimestampSchema,
		expiresAt: CanonicalTimestampSchema,
	},
	{ additionalProperties: false },
);
export const ActionApprovalDecisionSchema = Type.Object(
	{
		decision: Type.Union([Type.Literal("approved"), Type.Literal("rejected")]),
		reviewerId: OpaqueIdentifierSchema,
		decidedAt: CanonicalTimestampSchema,
		reason: Type.Optional(Type.String()),
	},
	{ additionalProperties: false },
);
export const ActionApprovalRecordSchema = Type.Object(
	{
		request: ActionApprovalRequestSchema,
		status: ActionApprovalStatusSchema,
		revision: DecimalSchema,
		updatedAt: CanonicalTimestampSchema,
		decision: Type.Optional(ActionApprovalDecisionSchema),
		consumedAt: Type.Optional(CanonicalTimestampSchema),
	},
	{ additionalProperties: false },
);

export type ActionRecordEnvelope = Static<typeof ActionRecordSchema>;
export type ActionApprovalRecordEnvelope = Static<typeof ActionApprovalRecordSchema>;

export class ActionProtocolValidationError extends TypeError {
	constructor() {
		super("The action record does not satisfy the canonical protocol schema.");
		this.name = "ActionProtocolValidationError";
	}
}

export function assertActionRecordEnvelope(value: unknown): asserts value is ActionRecordEnvelope {
	if (!Value.Check(ActionRecordSchema, value)) throw new ActionProtocolValidationError();
}

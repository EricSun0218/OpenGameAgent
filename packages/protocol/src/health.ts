import { type Static, Type } from "typebox";
import { CanonicalTimestampSchema, OpaqueIdentifierSchema } from "./schema.js";

export const RuntimeDependencyKindSchema = Type.Union([
	Type.Literal("provider"),
	Type.Literal("model"),
	Type.Literal("embedding"),
	Type.Literal("mcp"),
	Type.Literal("local-endpoint"),
	Type.Literal("media"),
	Type.Literal("realtime"),
]);

export const RuntimeDependencyRequirementSchema = Type.Union([Type.Literal("required"), Type.Literal("optional")]);

export const RuntimeDependencyReasonSchema = Type.Union([
	Type.Literal("authentication-required"),
	Type.Literal("permission-denied"),
	Type.Literal("misconfigured"),
	Type.Literal("timeout"),
	Type.Literal("transport"),
	Type.Literal("capacity"),
	Type.Literal("capability-missing"),
	Type.Literal("dependency-failure"),
	Type.Literal("stale"),
	Type.Literal("unknown"),
]);

export const RuntimeAvailableDependencyReasonSchema = Type.Literal("stale");

const RuntimeDependencyIdentityProperties = {
	id: OpaqueIdentifierSchema,
	kind: RuntimeDependencyKindSchema,
	requirement: RuntimeDependencyRequirementSchema,
} as const;

export const RuntimeDeclaredDependencyHealthSchema = Type.Object(
	{
		...RuntimeDependencyIdentityProperties,
		state: Type.Literal("declared"),
	},
	{ additionalProperties: false },
);

export const RuntimeAvailableDependencyHealthSchema = Type.Object(
	{
		...RuntimeDependencyIdentityProperties,
		state: Type.Literal("available"),
		observedAt: CanonicalTimestampSchema,
		reason: Type.Optional(RuntimeAvailableDependencyReasonSchema),
	},
	{ additionalProperties: false },
);

export const RuntimeReadyDependencyHealthSchema = Type.Object(
	{
		...RuntimeDependencyIdentityProperties,
		state: Type.Literal("ready"),
		observedAt: CanonicalTimestampSchema,
	},
	{ additionalProperties: false },
);

export const RuntimeImpairedDependencyHealthSchema = Type.Object(
	{
		...RuntimeDependencyIdentityProperties,
		state: Type.Union([Type.Literal("degraded"), Type.Literal("unavailable")]),
		observedAt: CanonicalTimestampSchema,
		reason: RuntimeDependencyReasonSchema,
	},
	{ additionalProperties: false },
);

export const RuntimeDependencyHealthSchema = Type.Union([
	RuntimeDeclaredDependencyHealthSchema,
	RuntimeAvailableDependencyHealthSchema,
	RuntimeReadyDependencyHealthSchema,
	RuntimeImpairedDependencyHealthSchema,
]);

export const RuntimeHealthStatusSchema = Type.Union([
	Type.Literal("ready"),
	Type.Literal("degraded"),
	Type.Literal("not-ready"),
]);

export const RuntimeHealthSnapshotSchema = Type.Object(
	{
		checkedAt: CanonicalTimestampSchema,
		status: RuntimeHealthStatusSchema,
		dependencies: Type.Array(RuntimeDependencyHealthSchema),
	},
	{ additionalProperties: false },
);

export type RuntimeDependencyKind = Static<typeof RuntimeDependencyKindSchema>;
export type RuntimeDependencyRequirement = Static<typeof RuntimeDependencyRequirementSchema>;
export type RuntimeDependencyReason = Static<typeof RuntimeDependencyReasonSchema>;
export type RuntimeAvailableDependencyReason = Static<typeof RuntimeAvailableDependencyReasonSchema>;
export type RuntimeDeclaredDependencyHealth = Static<typeof RuntimeDeclaredDependencyHealthSchema>;
export type RuntimeAvailableDependencyHealth = Static<typeof RuntimeAvailableDependencyHealthSchema>;
export type RuntimeReadyDependencyHealth = Static<typeof RuntimeReadyDependencyHealthSchema>;
export type RuntimeImpairedDependencyHealth = Static<typeof RuntimeImpairedDependencyHealthSchema>;
export type RuntimeDependencyHealth = Static<typeof RuntimeDependencyHealthSchema>;
export type RuntimeHealthStatus = Static<typeof RuntimeHealthStatusSchema>;
export type RuntimeHealthSnapshot = Static<typeof RuntimeHealthSnapshotSchema>;

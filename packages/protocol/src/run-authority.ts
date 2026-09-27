import { type Static, Type } from "typebox";
import { Value } from "typebox/value";

export { RunAdmissionAuthoritySchema } from "./schema.js";

import {
	assertCanonicalTimestamp,
	CanonicalTimestampSchema,
	DecimalSchema,
	GameTimeSchema,
	OpaqueIdentifierSchema,
	RunAdmissionAuthoritySchema,
	StreamKeySchema,
} from "./schema.js";

export const RunAuthorityKeySchema = Type.Object(
	{
		...StreamKeySchema.properties,
		...RunAdmissionAuthoritySchema.properties,
	},
	{ additionalProperties: false },
);
export const WorkAuthorityLaneSchema = Type.Object(
	{
		ownerId: OpaqueIdentifierSchema,
		timelineId: OpaqueIdentifierSchema,
		generation: DecimalSchema,
		sessionId: OpaqueIdentifierSchema,
		actorId: OpaqueIdentifierSchema,
	},
	{ additionalProperties: false },
);
export const WorkAuthorityRetirementRequestSchema = Type.Object(
	{
		retirementId: OpaqueIdentifierSchema,
		partition: WorkAuthorityLaneSchema,
		recordedAt: CanonicalTimestampSchema,
	},
	{ additionalProperties: false },
);
export const WorkAuthorityRetirementPhaseSchema = Type.Union([Type.Literal("fenced"), Type.Literal("finalized")]);
export const RunAuthorityWorkRetirementResultSchema = Type.Object(
	{
		phase: WorkAuthorityRetirementPhaseSchema,
		cancelledScheduleCount: DecimalSchema,
		cancelledOccurrenceCount: DecimalSchema,
		cancelledMessageCount: DecimalSchema,
	},
	{ additionalProperties: false },
);
export type WorkAuthorityLane = Static<typeof WorkAuthorityLaneSchema>;
export type WorkAuthorityRetirementRequest = Static<typeof WorkAuthorityRetirementRequestSchema>;
export interface WorkAuthorityRetirementRecord extends WorkAuthorityRetirementRequest {
	readonly phase: Static<typeof WorkAuthorityRetirementPhaseSchema>;
}
export type RunAuthorityWorkRetirementResult = Static<typeof RunAuthorityWorkRetirementResultSchema>;

export class WorkAuthorityRetiredError extends Error {
	constructor(public readonly partition: Readonly<WorkAuthorityLane>) {
		super("The work authority lane is permanently retired.");
		this.name = "WorkAuthorityRetiredError";
	}
}
export class WorkAuthorityRetirementConflictError extends Error {
	constructor() {
		super("The work retirement identity conflicts with its durable record or phase.");
		this.name = "WorkAuthorityRetirementConflictError";
	}
}

export function captureWorkAuthorityLane(input: WorkAuthorityLane): Readonly<WorkAuthorityLane> {
	const value = {
		ownerId: input?.ownerId,
		timelineId: input?.timelineId,
		generation: input?.generation,
		sessionId: input?.sessionId,
		actorId: input?.actorId,
	};
	if (!Value.Check(WorkAuthorityLaneSchema, value)) throw new TypeError("Invalid work authority lane.");
	return Object.freeze(value);
}

export function captureWorkAuthorityRetirementRequest(
	input: WorkAuthorityRetirementRequest,
): Readonly<WorkAuthorityRetirementRequest> {
	const value = {
		retirementId: input?.retirementId,
		partition: captureWorkAuthorityLane(input?.partition),
		recordedAt: input?.recordedAt,
	};
	if (!Value.Check(WorkAuthorityRetirementRequestSchema, value))
		throw new TypeError("Invalid work authority retirement.");
	assertCanonicalTimestamp(value.recordedAt);
	return Object.freeze(value);
}

export const RunAuthorityGoalRetirementSchema = Type.Object(
	{
		retirementId: OpaqueIdentifierSchema,
		stateRevision: DecimalSchema,
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
		gameTime: GameTimeSchema,
		recordedAt: CanonicalTimestampSchema,
	},
	{ additionalProperties: false },
);
export const CloseRunAuthorityRequestSchema = Type.Object(
	{
		...RunAuthorityKeySchema.properties,
		operationId: OpaqueIdentifierSchema,
		goalRetirement: Type.Optional(RunAuthorityGoalRetirementSchema),
		workRetirement: Type.Optional(WorkAuthorityRetirementRequestSchema),
	},
	{ additionalProperties: false },
);
export const RunAuthorityClosureSchema = Type.Object(
	{
		...CloseRunAuthorityRequestSchema.properties,
		closedAt: CanonicalTimestampSchema,
		workRetirementResult: Type.Optional(RunAuthorityWorkRetirementResultSchema),
	},
	{ additionalProperties: false },
);

export type RunAdmissionAuthority = Static<typeof RunAdmissionAuthoritySchema>;
export type RunAuthorityKey = Static<typeof RunAuthorityKeySchema>;
export type RunAuthorityGoalRetirement = Static<typeof RunAuthorityGoalRetirementSchema>;
export type CloseRunAuthorityRequest = Static<typeof CloseRunAuthorityRequestSchema>;
export type RunAuthorityClosure = Static<typeof RunAuthorityClosureSchema>;

export class RunAuthorityClosedError extends Error {
	constructor(public readonly key: Readonly<RunAuthorityKey>) {
		super("The run admission authority is permanently closed.");
		this.name = "RunAuthorityClosedError";
	}
}
export class RunAuthorityConflictError extends Error {
	constructor() {
		super("The run authority identity conflicts with its durable record.");
		this.name = "RunAuthorityConflictError";
	}
}

export function captureRunAdmissionAuthority(input: RunAdmissionAuthority): Readonly<RunAdmissionAuthority> {
	const value = { scope: input?.scope, generation: input?.generation };
	if (!Value.Check(RunAdmissionAuthoritySchema, value)) throw new TypeError("Invalid run admission authority.");
	return Object.freeze(value);
}

export function captureRunAuthorityKey(input: RunAuthorityKey): Readonly<RunAuthorityKey> {
	const value = { sessionId: input?.sessionId, actorId: input?.actorId, ...captureRunAdmissionAuthority(input) };
	if (!Value.Check(RunAuthorityKeySchema, value)) throw new TypeError("Invalid run authority key.");
	return Object.freeze(value);
}

export function captureCloseRunAuthorityRequest(input: CloseRunAuthorityRequest): Readonly<CloseRunAuthorityRequest> {
	const key = captureRunAuthorityKey(input);
	const operationId = input.operationId;
	const provided = input.goalRetirement;
	let goalRetirement: RunAuthorityGoalRetirement | undefined;
	if (provided !== undefined) {
		const source = provided.partition;
		const sessionId = source?.sessionId;
		const actorId = source?.actorId;
		const partition = Object.freeze({
			ownerId: source?.ownerId,
			timelineId: source?.timelineId,
			generation: source?.generation,
			...(sessionId === undefined ? {} : { sessionId }),
			...(actorId === undefined ? {} : { actorId }),
		});
		const time = provided.gameTime;
		goalRetirement = Object.freeze({
			retirementId: provided.retirementId,
			stateRevision: provided.stateRevision,
			partition,
			gameTime: Object.freeze({ timelineId: time?.timelineId, generation: time?.generation, tick: time?.tick }),
			recordedAt: provided.recordedAt,
		});
		if (!Value.Check(RunAuthorityGoalRetirementSchema, goalRetirement)) throw new TypeError("Invalid Goal retirement.");
		assertCanonicalTimestamp(goalRetirement.recordedAt);
		if (
			partition.timelineId !== goalRetirement.gameTime.timelineId ||
			partition.generation !== goalRetirement.gameTime.generation
		) {
			throw new TypeError("Goal retirement time does not match its partition.");
		}
	}
	const providedWork = input.workRetirement;
	const workRetirement = providedWork === undefined ? undefined : captureWorkAuthorityRetirementRequest(providedWork);
	if (
		workRetirement !== undefined &&
		(workRetirement.partition.sessionId !== key.sessionId || workRetirement.partition.actorId !== key.actorId)
	) {
		throw new TypeError("Work retirement must match the closed run stream.");
	}
	const value = {
		...key,
		operationId,
		...(goalRetirement === undefined ? {} : { goalRetirement }),
		...(workRetirement === undefined ? {} : { workRetirement }),
	};
	if (!Value.Check(CloseRunAuthorityRequestSchema, value)) throw new TypeError("Invalid run authority close request.");
	return Object.freeze(value);
}

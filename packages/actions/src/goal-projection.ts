import { Buffer } from "node:buffer";
import { assertOpaqueIdentifier, canonicalizeJsonValue, parseEventRevision } from "@opengameagent/protocol";
import type {
	ActionAuthority,
	ActionGoalProjectionBinding,
	ActionGoalProjectionLimits,
	ActionGoalProjectionListRequest,
	ActionRecord,
} from "./types.js";
import { assertPositiveSafeInteger } from "./validation.js";

export function captureActionGoalProjectionLimits(
	value: ActionGoalProjectionLimits | undefined,
): Readonly<ActionGoalProjectionLimits> | undefined {
	if (value === undefined) return undefined;
	const captured = {
		maxBindingBytes: value.maxBindingBytes,
		maxPendingPerStream: value.maxPendingPerStream,
		maxListBatchSize: value.maxListBatchSize,
		maxRecordBytes: value.maxRecordBytes,
	};
	for (const [name, limit] of Object.entries(captured)) assertPositiveSafeInteger(limit, name);
	return Object.freeze(captured);
}

export function assertActionGoalProjectionRecordFits(
	record: ActionRecord,
	limits?: Readonly<ActionGoalProjectionLimits>,
): void {
	if (record.goalProjection === undefined) return;
	if (limits === undefined) throw new TypeError("Goal projection limits are required for Action persistence.");
	const budgetedRecord =
		record.goalProjectionSettlement === undefined
			? {
					...record,
					goalProjectionSettlement: {
						outcome: "source_failed",
						settledAt: record.updatedAt,
					},
				}
			: record;
	if (Buffer.byteLength(canonicalizeJsonValue(budgetedRecord), "utf8") > limits.maxRecordBytes) {
		throw new RangeError("The Goal projection Action record exceeds its configured byte budget.");
	}
}

export function captureActionGoalProjectionListRequest(
	request: ActionGoalProjectionListRequest,
	limits: Readonly<ActionGoalProjectionLimits>,
): Readonly<ActionGoalProjectionListRequest> {
	const afterSequence = request.afterSequence;
	const captured = {
		sessionId: request.sessionId,
		actorId: request.actorId,
		...(afterSequence === undefined ? {} : { afterSequence }),
		limit: request.limit,
	};
	assertOpaqueIdentifier(captured.sessionId);
	assertOpaqueIdentifier(captured.actorId);
	assertPositiveSafeInteger(captured.limit, "limit");
	if (captured.limit > limits.maxListBatchSize) {
		throw new RangeError("The Goal projection page limit exceeds its configured maximum.");
	}
	if (captured.afterSequence !== undefined) parseEventRevision(captured.afterSequence);
	return Object.freeze(captured);
}

export function normalizeActionGoalProjectionBinding(
	input: ActionGoalProjectionBinding,
	action: { readonly sessionId: string; readonly actorId: string; readonly expectedAuthority: ActionAuthority },
	limits?: Readonly<ActionGoalProjectionLimits>,
): ActionGoalProjectionBinding {
	const goalId = input.goalId;
	const partitionInput = input.partition;
	const ownerId = partitionInput.ownerId;
	const timelineId = partitionInput.timelineId;
	const generation = partitionInput.generation;
	const sessionId = partitionInput.sessionId;
	const actorId = partitionInput.actorId;
	const stepId = input.stepId;
	const expectedRevision = input.expectedRevision;
	const inputId = input.inputId;
	for (const identifier of [goalId, ownerId, timelineId, stepId, inputId]) assertOpaqueIdentifier(identifier);
	if (sessionId !== undefined) assertOpaqueIdentifier(sessionId);
	if (actorId !== undefined) assertOpaqueIdentifier(actorId);
	parseEventRevision(generation);
	parseEventRevision(expectedRevision);
	if (timelineId !== action.expectedAuthority.timelineId || generation !== action.expectedAuthority.generation) {
		throw new TypeError("The Goal projection partition does not match the Action authority.");
	}
	if (
		(sessionId !== undefined && sessionId !== action.sessionId) ||
		(actorId !== undefined && actorId !== action.actorId)
	) {
		throw new TypeError("The Goal projection partition does not match the Action stream.");
	}
	const normalized: ActionGoalProjectionBinding = {
		goalId,
		partition: {
			ownerId,
			timelineId,
			generation,
			...(sessionId === undefined ? {} : { sessionId }),
			...(actorId === undefined ? {} : { actorId }),
		},
		stepId,
		expectedRevision,
		inputId,
	};
	if (limits !== undefined && Buffer.byteLength(canonicalizeJsonValue(normalized), "utf8") > limits.maxBindingBytes) {
		throw new RangeError("The Goal projection binding exceeds its configured byte budget.");
	}
	return normalized;
}

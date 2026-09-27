import { createHash } from "node:crypto";
import {
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type JsonValue,
	normalizeJsonValue,
	parseEventRevision,
	type RunCoordinate,
} from "@opengameagent/protocol";
import { normalizeActionGoalProjectionBinding } from "./goal-projection.js";
import type { ActionAuthority, ActionGoalProjectionLimits, ActionIntentDraft, PreparedActionIntent } from "./types.js";

export function prepareActionIntent(
	draft: ActionIntentDraft,
	goalProjectionLimits?: Readonly<ActionGoalProjectionLimits>,
): PreparedActionIntent {
	const captured = captureActionIntentDraft(draft);
	validateCoordinate(captured);
	const operationId = actionOperationId(captured);
	validateAuthority(captured.expectedAuthority);
	if (
		captured.replayPolicy !== "never" &&
		captured.replayPolicy !== "safe" &&
		captured.replayPolicy !== "recoverable"
	) {
		throw new TypeError("The action replay policy is invalid.");
	}
	const payload = normalizeJsonValue(captured.payload);
	const conflictKeys = normalizeConflictKeys(captured.conflictKeys);
	const goalProjection =
		captured.goalProjection === undefined
			? undefined
			: normalizeActionGoalProjectionBinding(captured.goalProjection, captured, goalProjectionLimits);
	const payloadFingerprint = fingerprintNormalizedActionPayload(payload);
	const intentFingerprint = sha256(
		canonicalizeJsonValue({
			protocolVersion: "2",
			coordinate: captured.coordinate,
			actionName: captured.actionName,
			operationKey: captured.operationKey,
			payload,
			expectedAuthority: captured.expectedAuthority,
			conflictKeys,
			replayPolicy: captured.replayPolicy,
			...(goalProjection === undefined ? {} : { goalProjection }),
		}),
	);
	return {
		sessionId: captured.sessionId,
		actorId: captured.actorId,
		operationId,
		intentFingerprint,
		payloadFingerprint,
		coordinate: structuredClone(captured.coordinate),
		actionName: captured.actionName,
		operationKey: captured.operationKey,
		payload,
		expectedAuthority: structuredClone(captured.expectedAuthority),
		conflictKeys,
		replayPolicy: captured.replayPolicy,
		...(goalProjection === undefined ? {} : { goalProjection }),
	};
}

export function actionOperationId(input: {
	readonly sessionId: string;
	readonly actorId: string;
	readonly coordinate: RunCoordinate;
	readonly actionName: string;
	readonly operationKey: string;
}): string {
	const coordinate = input.coordinate;
	const captured = {
		sessionId: input.sessionId,
		actorId: input.actorId,
		coordinate: {
			sessionId: coordinate.sessionId,
			actorId: coordinate.actorId,
			runId: coordinate.runId,
			turn: coordinate.turn,
		},
		actionName: input.actionName,
		operationKey: input.operationKey,
	};
	assertOpaqueIdentifier(captured.sessionId);
	assertOpaqueIdentifier(captured.actorId);
	validateCoordinate(captured);
	assertOpaqueIdentifier(captured.actionName);
	assertOpaqueIdentifier(captured.operationKey);
	return `act_${sha256(
		canonicalizeJsonValue({
			protocolVersion: "2",
			coordinate: captured.coordinate,
			actionName: captured.actionName,
			operationKey: captured.operationKey,
		}),
	)}`;
}

export function isActionOperationId(value: unknown): value is string {
	return typeof value === "string" && /^act_[0-9a-f]{64}$/u.test(value);
}

export function fingerprintActionPayload(payload: unknown): string {
	return fingerprintNormalizedActionPayload(normalizeJsonValue(payload));
}

export function validateAuthority(authority: ActionAuthority): void {
	assertOpaqueIdentifier(authority.timelineId);
	parseEventRevision(authority.generation);
	parseEventRevision(authority.revision);
}

function validateCoordinate(draft: Pick<ActionIntentDraft, "sessionId" | "actorId" | "coordinate">): void {
	const coordinate = draft.coordinate;
	for (const value of [coordinate.sessionId, coordinate.actorId, coordinate.runId]) {
		assertOpaqueIdentifier(value);
	}
	parseEventRevision(coordinate.turn);
	if (coordinate.sessionId !== draft.sessionId || coordinate.actorId !== draft.actorId) {
		throw new TypeError("The action coordinate must match its session and actor.");
	}
}

function normalizeConflictKeys(keys: readonly string[]): string[] {
	const normalized = [...keys];
	for (const key of normalized) assertOpaqueIdentifier(key);
	normalized.sort();
	for (let index = 1; index < normalized.length; index += 1) {
		if (normalized[index] === normalized[index - 1]) {
			throw new TypeError("Action conflict keys must be unique.");
		}
	}
	return normalized;
}

function sha256(value: string): string {
	return createHash("sha256").update(value).digest("hex");
}

function fingerprintNormalizedActionPayload(payload: JsonValue): string {
	return sha256(canonicalizeJsonValue(payload));
}

function captureActionIntentDraft(draft: ActionIntentDraft): ActionIntentDraft {
	const coordinate = draft.coordinate;
	const expectedAuthority = draft.expectedAuthority;
	const goalProjection = draft.goalProjection;
	const goalPartition = goalProjection?.partition;
	return {
		sessionId: draft.sessionId,
		actorId: draft.actorId,
		coordinate: {
			sessionId: coordinate.sessionId,
			actorId: coordinate.actorId,
			runId: coordinate.runId,
			turn: coordinate.turn,
		},
		actionName: draft.actionName,
		operationKey: draft.operationKey,
		payload: draft.payload,
		expectedAuthority: {
			timelineId: expectedAuthority.timelineId,
			generation: expectedAuthority.generation,
			revision: expectedAuthority.revision,
		},
		conflictKeys: draft.conflictKeys,
		replayPolicy: draft.replayPolicy,
		...(goalProjection === undefined || goalPartition === undefined
			? {}
			: {
					goalProjection: {
						goalId: goalProjection.goalId,
						partition: {
							ownerId: goalPartition.ownerId,
							timelineId: goalPartition.timelineId,
							generation: goalPartition.generation,
							...(goalPartition.sessionId === undefined ? {} : { sessionId: goalPartition.sessionId }),
							...(goalPartition.actorId === undefined ? {} : { actorId: goalPartition.actorId }),
						},
						stepId: goalProjection.stepId,
						expectedRevision: goalProjection.expectedRevision,
						inputId: goalProjection.inputId,
					},
				}),
	};
}

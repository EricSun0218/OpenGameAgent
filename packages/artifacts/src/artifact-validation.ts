import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import { isProxy } from "node:util/types";
import { type NormalizedGameInput, requiredTextToolResultBytes } from "@opengameagent/agent";
import {
	assertCanonicalTimestamp,
	assertEventStreamCoordinate,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	type RunCoordinate,
	type StreamKey,
} from "@opengameagent/protocol";
import { ArtifactCorruptionError, ArtifactValidationError } from "./artifact-errors.js";
import type { ArtifactLimits, ArtifactRecord, ArtifactWriteRequest } from "./artifact-types.js";

const fingerprintPattern = /^[0-9a-f]{64}$/u;
const contentTypePattern = /^[!#$%&'*+.^_`|~0-9A-Za-z-]+\/[!#$%&'*+.^_`|~0-9A-Za-z-]+$/u;
const limitNames = [
	"maxIdentifierBytes",
	"maxCounterDigits",
	"maxContentTypeBytes",
	"maxArtifactBytes",
	"maxArtifactsPerStream",
	"maxStoredBytesPerStream",
	"maxInlineToolResultBytes",
	"maxReadBytes",
	"maxModelResultBytes",
	"maxPurgeBatch",
] as const satisfies readonly (keyof ArtifactLimits)[];

export function freezeArtifactLimits(limits: ArtifactLimits): Readonly<ArtifactLimits> {
	const copy = {} as Record<keyof ArtifactLimits, number>;
	for (const name of limitNames) {
		const value = limits[name];
		if (!Number.isSafeInteger(value) || value <= 0) {
			throw new ArtifactValidationError(`${name} must be a positive safe integer.`);
		}
		copy[name] = value;
	}
	if (copy.maxReadBytes < 4) {
		throw new ArtifactValidationError("maxReadBytes must fit one maximum-width UTF-8 code point.");
	}
	if (copy.maxContentTypeBytes < 3) {
		throw new ArtifactValidationError("maxContentTypeBytes cannot represent the minimum media type.");
	}
	if (copy.maxArtifactBytes <= copy.maxInlineToolResultBytes) {
		throw new ArtifactValidationError("maxArtifactBytes must exceed maxInlineToolResultBytes.");
	}
	if (copy.maxStoredBytesPerStream < copy.maxArtifactBytes) {
		throw new ArtifactValidationError("maxStoredBytesPerStream must fit one maximum-size artifact.");
	}
	if (copy.maxModelResultBytes < byteLength(canonicalizeJsonValue({ status: "not_found" }))) {
		throw new ArtifactValidationError("maxModelResultBytes cannot represent the minimum Tool result.");
	}
	return Object.freeze(copy);
}

export function assertArtifactLimitsEqual(left: Readonly<ArtifactLimits>, right: Readonly<ArtifactLimits>): void {
	for (const name of limitNames) {
		if (left[name] !== right[name]) {
			throw new ArtifactValidationError("Artifact service and store limits must match exactly.");
		}
	}
}

/** Final Agent admission required by the Artifact read Tool's fixed result envelope. */
export function requiredArtifactReadToolResultBytes(limits: Readonly<ArtifactLimits>): number {
	const emptyReadResultBytes = byteLength(
		canonicalizeJsonValue({
			content: [{ type: "text", text: "" }],
			details: { artifactId: "0".repeat(64), artifactStatus: "read" },
		}),
	);
	try {
		return requiredTextToolResultBytes(limits.maxModelResultBytes, emptyReadResultBytes);
	} catch {
		throw new ArtifactValidationError("Artifact Tool result admission exceeds the host numeric range.");
	}
}

export function validateArtifactBinding(
	input: Readonly<NormalizedGameInput>,
	coordinate: Readonly<RunCoordinate>,
	limits: Readonly<ArtifactLimits>,
): { inputId: string; coordinate: RunCoordinate } {
	assertEventStreamCoordinate({ sessionId: input.sessionId, actorId: input.actorId }, coordinate);
	validateArtifactIdentifier(input.sessionId, limits, "sessionId");
	validateArtifactIdentifier(input.actorId, limits, "actorId");
	validateArtifactIdentifier(input.inputId, limits, "inputId");
	validateArtifactIdentifier(coordinate.runId, limits, "runId");
	if (coordinate.turn.length > limits.maxCounterDigits) {
		throw new ArtifactValidationError("turn exceeds the configured digit limit.");
	}
	return { inputId: input.inputId, coordinate: { ...coordinate } };
}

export function validateArtifactStream(stream: Readonly<StreamKey>, limits: Readonly<ArtifactLimits>): StreamKey {
	validateArtifactIdentifier(stream.sessionId, limits, "sessionId");
	validateArtifactIdentifier(stream.actorId, limits, "actorId");
	return { ...stream };
}

export function validateArtifactIdentifier(
	value: unknown,
	limits: Pick<ArtifactLimits, "maxIdentifierBytes">,
	name: string,
): string {
	if (typeof value !== "string") throw new ArtifactValidationError(`${name} must be an identifier.`);
	try {
		assertOpaqueIdentifier(value);
	} catch (error) {
		throw new ArtifactValidationError(`${name} must be an identifier.`, { cause: error });
	}
	if (byteLength(value) > limits.maxIdentifierBytes) {
		throw new ArtifactValidationError(`${name} exceeds the configured identifier byte limit.`);
	}
	return value;
}

export function validateArtifactFingerprint(value: unknown, name: string): string {
	if (typeof value !== "string" || !fingerprintPattern.test(value)) {
		throw new ArtifactValidationError(`${name} must be a SHA-256 fingerprint.`);
	}
	return value;
}

export function validateArtifactContentType(value: unknown, limits: Readonly<ArtifactLimits>): string {
	if (typeof value !== "string" || !contentTypePattern.test(value) || byteLength(value) > limits.maxContentTypeBytes) {
		throw new ArtifactValidationError("contentType must be a bounded media type.");
	}
	return value.toLowerCase();
}

export function createArtifactRecord(
	request: Readonly<ArtifactWriteRequest>,
	createdAt: string,
	limits: Readonly<ArtifactLimits>,
): ArtifactRecord {
	const binding = validateArtifactBinding(request.input, request.coordinate, limits);
	const toolCallId = validateArtifactIdentifier(request.toolCallId, limits, "toolCallId");
	const toolName = validateArtifactIdentifier(request.toolName, limits, "toolName");
	const purpose = validateArtifactIdentifier(request.purpose, limits, "purpose");
	const requestFingerprint = validateArtifactFingerprint(request.requestFingerprint, "requestFingerprint");
	const contentType = validateArtifactContentType(request.contentType, limits);
	if (typeof request.content !== "string" || request.content.length === 0) {
		throw new ArtifactValidationError("Artifact content must be non-empty UTF-8 text.");
	}
	const contentBytes = byteLength(request.content);
	if (contentBytes > limits.maxArtifactBytes) {
		throw new ArtifactValidationError("Artifact content exceeds the configured byte limit.");
	}
	validateTimestamp(createdAt, "createdAt");

	const identity = {
		sessionId: request.input.sessionId,
		actorId: request.input.actorId,
		inputId: binding.inputId,
		coordinate: binding.coordinate,
		toolCallId,
		toolName,
		purpose,
		requestFingerprint,
	};
	const record: ArtifactRecord = {
		sessionId: request.input.sessionId,
		actorId: request.input.actorId,
		artifactId: digest({ kind: "artifact_identity", ...identity }),
		contentFingerprint: digest({ kind: "artifact_content", contentType, content: request.content }),
		contentType,
		content: request.content,
		byteLength: contentBytes,
		createdAt,
		origin: {
			inputId: binding.inputId,
			coordinate: binding.coordinate,
			toolCallId,
			toolName,
			purpose,
			requestFingerprint,
		},
	};
	return validateArtifactRecord(record, limits);
}

export function validateArtifactRecord(value: unknown, limits: Readonly<ArtifactLimits>): ArtifactRecord {
	try {
		const normalized = captureExactRecord(value, [
			"sessionId",
			"actorId",
			"artifactId",
			"contentFingerprint",
			"contentType",
			"content",
			"byteLength",
			"createdAt",
			"origin",
		]);
		const sessionId = validateArtifactIdentifier(normalized.sessionId, limits, "sessionId");
		const actorId = validateArtifactIdentifier(normalized.actorId, limits, "actorId");
		const artifactId = validateArtifactFingerprint(normalized.artifactId, "artifactId");
		const contentFingerprint = validateArtifactFingerprint(normalized.contentFingerprint, "contentFingerprint");
		const contentType = validateArtifactContentType(normalized.contentType, limits);
		if (typeof normalized.content !== "string" || normalized.content.length === 0) throw new TypeError();
		const actualBytes = byteLength(normalized.content);
		if (actualBytes > limits.maxArtifactBytes || normalized.byteLength !== actualBytes) throw new TypeError();
		validateTimestamp(normalized.createdAt, "createdAt");
		const normalizedOrigin = captureExactRecord(normalized.origin, [
			"inputId",
			"coordinate",
			"toolCallId",
			"toolName",
			"purpose",
			"requestFingerprint",
		]);
		const coordinate = captureExactRecord(normalizedOrigin.coordinate, ["sessionId", "actorId", "runId", "turn"]);
		const runCoordinate: RunCoordinate = {
			sessionId: validateArtifactIdentifier(coordinate.sessionId, limits, "coordinate.sessionId"),
			actorId: validateArtifactIdentifier(coordinate.actorId, limits, "coordinate.actorId"),
			runId: validateArtifactIdentifier(coordinate.runId, limits, "coordinate.runId"),
			turn: validateCounter(coordinate.turn, limits),
		};
		if (runCoordinate.sessionId !== sessionId || runCoordinate.actorId !== actorId) throw new TypeError();
		const origin = {
			inputId: validateArtifactIdentifier(normalizedOrigin.inputId, limits, "origin.inputId"),
			coordinate: runCoordinate,
			toolCallId: validateArtifactIdentifier(normalizedOrigin.toolCallId, limits, "origin.toolCallId"),
			toolName: validateArtifactIdentifier(normalizedOrigin.toolName, limits, "origin.toolName"),
			purpose: validateArtifactIdentifier(normalizedOrigin.purpose, limits, "origin.purpose"),
			requestFingerprint: validateArtifactFingerprint(normalizedOrigin.requestFingerprint, "origin.requestFingerprint"),
		};
		const expectedArtifactId = digest({ kind: "artifact_identity", sessionId, actorId, ...origin });
		const expectedContentFingerprint = digest({
			kind: "artifact_content",
			contentType,
			content: normalized.content,
		});
		if (artifactId !== expectedArtifactId || contentFingerprint !== expectedContentFingerprint) throw new TypeError();
		return {
			sessionId,
			actorId,
			artifactId,
			contentFingerprint,
			contentType,
			content: normalized.content,
			byteLength: actualBytes,
			createdAt: normalized.createdAt,
			origin,
		};
	} catch (error) {
		if (error instanceof ArtifactCorruptionError) throw error;
		throw new ArtifactCorruptionError({ cause: error });
	}
}

export function sameArtifactBinding(left: Readonly<ArtifactRecord>, right: Readonly<ArtifactRecord>): boolean {
	const withoutCreationTime = (record: Readonly<ArtifactRecord>) => {
		const { createdAt: _createdAt, ...bound } = record;
		return bound;
	};
	return canonicalizeJsonValue(withoutCreationTime(left)) === canonicalizeJsonValue(withoutCreationTime(right));
}

export function cloneArtifactRecord(record: Readonly<ArtifactRecord>): ArtifactRecord {
	return {
		...record,
		origin: { ...record.origin, coordinate: { ...record.origin.coordinate } },
	};
}

export function validateArtifactPurge(
	stream: Readonly<StreamKey>,
	createdBefore: unknown,
	limit: unknown,
	limits: Readonly<ArtifactLimits>,
): { stream: StreamKey; createdBefore: string; limit: number } {
	const normalizedStream = validateArtifactStream(stream, limits);
	validateTimestamp(createdBefore, "createdBefore");
	if (!Number.isSafeInteger(limit) || (limit as number) <= 0 || (limit as number) > limits.maxPurgeBatch) {
		throw new ArtifactValidationError("purge limit exceeds the configured batch limit.");
	}
	return { stream: normalizedStream, createdBefore, limit: limit as number };
}

export function digest(value: unknown): string {
	return createHash("sha256").update(canonicalizeJsonValue(value)).digest("hex");
}

export function byteLength(value: string): number {
	return Buffer.byteLength(value, "utf8");
}

function validateCounter(value: unknown, limits: Readonly<ArtifactLimits>): string {
	if (typeof value !== "string" || !/^(0|[1-9][0-9]*)$/u.test(value) || value.length > limits.maxCounterDigits) {
		throw new ArtifactValidationError("turn must be a bounded canonical counter.");
	}
	return value;
}

function validateTimestamp(value: unknown, name: string): asserts value is string {
	try {
		assertCanonicalTimestamp(value);
	} catch (error) {
		throw new ArtifactValidationError(`${name} must be a canonical timestamp.`, { cause: error });
	}
}

function isRecord(value: unknown): value is Record<string, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value) || isProxy(value)) return false;
	const prototype = Object.getPrototypeOf(value);
	return prototype === Object.prototype || prototype === null;
}

function captureExactRecord(value: unknown, expected: readonly string[]): Record<string, unknown> {
	if (!isRecord(value)) throw new TypeError();
	assertExactKeys(value, expected);
	const captured: Record<string, unknown> = Object.create(null);
	for (const key of expected) {
		const descriptor = Object.getOwnPropertyDescriptor(value, key);
		if (!descriptor || !("value" in descriptor)) throw new TypeError();
		captured[key] = descriptor.value;
	}
	return captured;
}

function assertExactKeys(value: Record<string, unknown>, expected: readonly string[]): void {
	const actual = Object.keys(value).sort();
	const sortedExpected = [...expected].sort();
	if (actual.length !== sortedExpected.length || actual.some((key, index) => key !== sortedExpected[index])) {
		throw new TypeError();
	}
}

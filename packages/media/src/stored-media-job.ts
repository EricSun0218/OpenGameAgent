import { Buffer } from "node:buffer";
import {
	assertCanonicalTimestamp,
	assertOpaqueIdentifier,
	canonicalizeJsonValue,
	canonicalizeJsonValueWithin,
	type JsonValue,
	JsonValueLimitError,
	normalizeJsonValue,
	normalizeJsonValueWithin,
} from "@opengameagent/protocol";
import { MediaJobCapacityError, MediaJobValidationError } from "./errors.js";
import {
	type GeneratedResourceRef,
	MEDIA_FAILURE_KINDS,
	MEDIA_JOB_STATUSES,
	MEDIA_KINDS,
	type MediaCancellation,
	type MediaChildFailure,
	type MediaJobDisposition,
	type MediaJobLimits,
	type ResultBundle,
	type StoredMediaAttempt,
	type StoredMediaJob,
	type StoredMediaOutputSettlement,
} from "./types.js";

const SHA256 = /^[0-9a-f]{64}$/u;
const TERMINAL_STATUSES = new Set<StoredMediaAttempt["status"]>([
	"succeeded",
	"partially_succeeded",
	"failed",
	"cancelled",
]);
const STORED_JOB_FIXED_DEPTH = 7;
const NESTED_VALUE_ENVELOPE_DEPTH = 3;
const DISPOSITIONS = ["active", "abandoned"] as const satisfies readonly MediaJobDisposition[];
const CANCELLATIONS = [
	"not_requested",
	"requested",
	"unsupported",
	"unknown",
	"rejected",
	"confirmed",
] as const satisfies readonly MediaCancellation[];
const BUNDLE_DISPOSITIONS = ["complete", "partial", "late"] as const satisfies readonly ResultBundle["disposition"][];

export function captureMediaJobLimits(value: Readonly<MediaJobLimits>): Readonly<MediaJobLimits> {
	if (!value || typeof value !== "object") throw new RangeError("Media job limits are required.");
	const captured: MediaJobLimits = {
		maxIdentifierBytes: value.maxIdentifierBytes,
		maxAdapterIdBytes: value.maxAdapterIdBytes,
		maxAdapters: value.maxAdapters,
		maxRequestBytes: value.maxRequestBytes,
		maxJsonDepth: value.maxJsonDepth,
		maxProviderHandleBytes: value.maxProviderHandleBytes,
		maxAttemptsPerJob: value.maxAttemptsPerJob,
		maxOutputsPerAttempt: value.maxOutputsPerAttempt,
		maxResourceBytes: value.maxResourceBytes,
		maxBundleBytes: value.maxBundleBytes,
		maxManifestBytes: value.maxManifestBytes,
		maxMediaTypeBytes: value.maxMediaTypeBytes,
	};
	for (const [name, limit] of Object.entries(captured)) {
		if (!Number.isSafeInteger(limit) || limit < 1) throw new RangeError(`${name} must be a positive safe integer.`);
	}
	return Object.freeze(captured);
}

export interface StoredMediaJobCaptureOptions {
	/** Store-owned revision override used by atomic modify implementations. */
	readonly revision?: string;
	/** Optional complete canonical snapshot budget used by durable Stores. */
	readonly maxBytes?: number;
}

/** Captures the complete Store-owned state with the same domain limits used by MediaJobService. */
export function captureStoredMediaJob(
	value: unknown,
	limits: Readonly<MediaJobLimits>,
	options: Readonly<StoredMediaJobCaptureOptions> = {},
): StoredMediaJob {
	try {
		const source = exactRecord(captureCompleteValue(value, limits, options.maxBytes), [
			"jobId",
			"adapterId",
			"request",
			"revision",
			"createdAt",
			"updatedAt",
			"currentAttemptId",
			"attempts",
			"bundles",
		]);
		const jobId = identifier(source.jobId, limits.maxIdentifierBytes);
		const adapterId = identifier(source.adapterId, limits.maxAdapterIdBytes);
		const requestSource = exactRecord(source.request, ["kind", "input"]);
		if (!includes(MEDIA_KINDS, requestSource.kind)) invalid();
		const request = {
			kind: requestSource.kind,
			input: normalizeJsonValueWithin(requestSource.input, {
				maxDepth: limits.maxJsonDepth,
				maxBytes: limits.maxRequestBytes,
			}),
		};
		const revision = positiveRevision(options.revision ?? source.revision);
		const createdAt = timestamp(source.createdAt);
		const updatedAt = timestamp(source.updatedAt);
		const currentAttemptId = identifier(source.currentAttemptId, limits.maxIdentifierBytes);
		if (!Array.isArray(source.attempts) || source.attempts.length === 0) invalid();
		if (source.attempts.length > limits.maxAttemptsPerJob) throw new MediaJobCapacityError("attempts");
		const attempts = source.attempts.map((attempt, index) => captureAttempt(attempt, index, limits));
		if (new Set(attempts.map((attempt) => attempt.attemptId)).size !== attempts.length) invalid();
		if (new Set(attempts.map((attempt) => attempt.submissionKey)).size !== attempts.length) invalid();
		if (attempts.at(-1)?.attemptId !== currentAttemptId) invalid();
		for (const attempt of attempts.slice(0, -1)) {
			if (!TERMINAL_STATUSES.has(attempt.status) && attempt.disposition !== "abandoned") invalid();
		}
		if (!Array.isArray(source.bundles)) invalid();
		if (source.bundles.length > limits.maxAttemptsPerJob) throw new MediaJobCapacityError("attempts");
		const bundles = source.bundles.map((bundle) => captureBundle(bundle, jobId, limits));
		if (new Set(bundles.map((bundle) => bundle.bundleId)).size !== bundles.length) invalid();
		assertBundleLinks(attempts, bundles);
		return {
			jobId,
			adapterId,
			request,
			revision,
			createdAt,
			updatedAt,
			currentAttemptId,
			attempts,
			bundles,
		};
	} catch (error) {
		if (
			error instanceof MediaJobValidationError ||
			error instanceof MediaJobCapacityError ||
			error instanceof JsonValueLimitError
		) {
			throw error;
		}
		throw new MediaJobValidationError("invalid_job");
	}
}

function captureAttempt(value: unknown, expectedOrdinal: number, limits: Readonly<MediaJobLimits>): StoredMediaAttempt {
	const source = exactRecord(
		value,
		[
			"attemptId",
			"ordinal",
			"submissionKey",
			"status",
			"disposition",
			"cancellation",
			"startedAt",
			"updatedAt",
			"outputSettlements",
		],
		["providerHandle", "settlingStatus", "bundleId", "failure"],
	);
	const attemptId = identifier(source.attemptId, limits.maxIdentifierBytes);
	const submissionKey = identifier(source.submissionKey, limits.maxIdentifierBytes);
	if (source.ordinal !== expectedOrdinal || !Number.isSafeInteger(source.ordinal)) invalid();
	if (!includes(MEDIA_JOB_STATUSES, source.status)) invalid();
	if (!includes(DISPOSITIONS, source.disposition)) invalid();
	if (!includes(CANCELLATIONS, source.cancellation)) invalid();
	const startedAt = timestamp(source.startedAt);
	const updatedAt = timestamp(source.updatedAt);
	const providerHandle = Object.hasOwn(source, "providerHandle")
		? normalizeJsonValueWithin(source.providerHandle, {
				maxDepth: limits.maxJsonDepth,
				maxBytes: limits.maxProviderHandleBytes,
			})
		: undefined;
	const settlingStatus = Object.hasOwn(source, "settlingStatus") ? source.settlingStatus : undefined;
	if (settlingStatus !== undefined && settlingStatus !== "succeeded" && settlingStatus !== "partially_succeeded") {
		invalid();
	}
	const bundleId = Object.hasOwn(source, "bundleId")
		? identifier(source.bundleId, limits.maxIdentifierBytes)
		: undefined;
	const failure = Object.hasOwn(source, "failure") ? source.failure : undefined;
	if (failure !== undefined && !includes(MEDIA_FAILURE_KINDS, failure)) invalid();
	if (!Array.isArray(source.outputSettlements)) invalid();
	if (source.outputSettlements.length > limits.maxOutputsPerAttempt) throw new MediaJobCapacityError("outputs");
	const outputSettlements = source.outputSettlements.map((settlement) => captureSettlement(settlement, limits));
	assertStrictOrdinals(outputSettlements);
	assertAttemptState(
		source.status,
		source.disposition,
		source.cancellation,
		outputSettlements,
		settlingStatus,
		bundleId,
		failure,
		providerHandle,
	);
	return {
		attemptId,
		ordinal: expectedOrdinal,
		submissionKey,
		status: source.status,
		disposition: source.disposition,
		cancellation: source.cancellation,
		startedAt,
		updatedAt,
		outputSettlements,
		...(providerHandle === undefined ? {} : { providerHandle }),
		...(settlingStatus === undefined ? {} : { settlingStatus }),
		...(bundleId === undefined ? {} : { bundleId }),
		...(failure === undefined ? {} : { failure }),
	};
}

function captureSettlement(value: unknown, limits: Readonly<MediaJobLimits>): StoredMediaOutputSettlement {
	const base = exactRecord(value, ["ordinal", "status"], ["resource", "failure"]);
	const ordinal = outputOrdinal(base.ordinal, limits);
	if (base.status === "published") {
		exactKeys(base, ["ordinal", "status", "resource"]);
		return { ordinal, status: "published", resource: captureResource(base.resource, limits) };
	}
	if (base.status === "failed") {
		exactKeys(base, ["ordinal", "status", "failure"]);
		if (!includes(MEDIA_FAILURE_KINDS, base.failure)) invalid();
		return { ordinal, status: "failed", failure: base.failure };
	}
	return invalid();
}

function captureResource(value: unknown, limits: Readonly<MediaJobLimits>): GeneratedResourceRef {
	const source = exactRecord(value, ["resourceId", "kind", "mediaType", "byteLength", "sha256", "ordinal"]);
	if (
		typeof source.sha256 !== "string" ||
		!SHA256.test(source.sha256) ||
		source.resourceId !== source.sha256 ||
		!includes(MEDIA_KINDS, source.kind) ||
		typeof source.mediaType !== "string" ||
		source.mediaType.length === 0 ||
		!Number.isSafeInteger(source.byteLength) ||
		(source.byteLength as number) < 0
	) {
		invalid();
	}
	if (Buffer.byteLength(source.mediaType) > limits.maxMediaTypeBytes) throw new MediaJobCapacityError("resource");
	if ((source.byteLength as number) > limits.maxResourceBytes) throw new MediaJobCapacityError("resource");
	const ordinal = outputOrdinal(source.ordinal, limits);
	return {
		resourceId: source.sha256,
		kind: source.kind,
		mediaType: source.mediaType,
		byteLength: source.byteLength as number,
		sha256: source.sha256,
		ordinal,
	};
}

function captureBundle(value: unknown, jobId: string, limits: Readonly<MediaJobLimits>): ResultBundle {
	const source = exactRecord(value, ["bundleId", "jobId", "attemptId", "disposition", "resources", "failures"]);
	const bundleId = identifier(source.bundleId, limits.maxIdentifierBytes);
	if (source.jobId !== jobId) invalid();
	const attemptId = identifier(source.attemptId, limits.maxIdentifierBytes);
	if (!includes(BUNDLE_DISPOSITIONS, source.disposition)) invalid();
	if (!Array.isArray(source.resources) || !Array.isArray(source.failures)) invalid();
	if (
		source.resources.length > limits.maxOutputsPerAttempt ||
		source.failures.length > limits.maxOutputsPerAttempt - source.resources.length
	) {
		throw new MediaJobCapacityError("outputs");
	}
	const resources = source.resources.map((resource) => captureResource(resource, limits));
	const failures = source.failures.map((failure) => captureFailure(failure, limits));
	assertStrictOrdinals(resources);
	assertStrictOrdinals(failures);
	if (new Set([...resources, ...failures].map((item) => item.ordinal)).size !== resources.length + failures.length) {
		invalid();
	}
	let bundleBytes = 0;
	for (const resource of resources) {
		if (resource.byteLength > limits.maxBundleBytes - bundleBytes) throw new MediaJobCapacityError("bundle");
		bundleBytes += resource.byteLength;
	}
	const bundle: ResultBundle = { bundleId, jobId, attemptId, disposition: source.disposition, resources, failures };
	canonicalizeJsonValueWithin(bundle as unknown as JsonValue, {
		maxDepth: limits.maxJsonDepth,
		maxBytes: limits.maxManifestBytes,
	});
	return bundle;
}

function captureFailure(value: unknown, limits: Readonly<MediaJobLimits>): MediaChildFailure {
	const source = exactRecord(value, ["ordinal", "failure"]);
	if (!includes(MEDIA_FAILURE_KINDS, source.failure)) invalid();
	return { ordinal: outputOrdinal(source.ordinal, limits), failure: source.failure };
}

function assertAttemptState(
	status: StoredMediaAttempt["status"],
	disposition: StoredMediaAttempt["disposition"],
	cancellation: StoredMediaAttempt["cancellation"],
	settlements: readonly StoredMediaOutputSettlement[],
	settlingStatus: StoredMediaAttempt["settlingStatus"],
	bundleId: string | undefined,
	failure: StoredMediaAttempt["failure"],
	providerHandle: JsonValue | undefined,
): void {
	if (disposition === "abandoned" && cancellation === "not_requested") invalid();
	if (
		disposition === "active" &&
		cancellation !== "not_requested" &&
		!(status === "cancelled" && cancellation === "confirmed")
	) {
		invalid();
	}
	if (status === "pending" && disposition !== "active") invalid();
	if (status === "queued" && (disposition !== "active" || providerHandle !== undefined)) invalid();
	if (status === "cancel_requested" && (disposition !== "abandoned" || cancellation === "confirmed")) invalid();
	if (status === "submitting" && disposition === "abandoned" && cancellation !== "requested") invalid();
	if (status === "submitting" && providerHandle !== undefined) invalid();
	if (status === "settling") {
		if (settlingStatus === undefined || bundleId !== undefined || failure !== undefined) invalid();
		const hasFailedSettlement = settlements.some((settlement) => settlement.status === "failed");
		if (
			(settlingStatus === "succeeded" && hasFailedSettlement) ||
			(settlingStatus === "partially_succeeded" && !hasFailedSettlement)
		) {
			invalid();
		}
		return;
	}
	if (settlingStatus !== undefined) invalid();
	if (status === "succeeded") {
		if (
			bundleId === undefined ||
			failure !== undefined ||
			settlements.length === 0 ||
			settlements.some((settlement) => settlement.status !== "published")
		) {
			invalid();
		}
		return;
	}
	if (status === "partially_succeeded") {
		if (
			bundleId === undefined ||
			failure !== undefined ||
			!settlements.some((settlement) => settlement.status === "published") ||
			!settlements.some((settlement) => settlement.status === "failed")
		) {
			invalid();
		}
		return;
	}
	if (bundleId !== undefined) invalid();
	if (status === "failed") {
		if (failure === undefined) invalid();
		return;
	}
	if (status === "outcome_unknown") {
		if (failure !== "unknown" || settlements.length !== 0) invalid();
		return;
	}
	if (failure !== undefined || settlements.length !== 0) invalid();
	if ((status === "pending" || status === "cancel_requested") && providerHandle === undefined) invalid();
}

function captureCompleteValue(value: unknown, limits: Readonly<MediaJobLimits>, maxBytes: number | undefined): unknown {
	if (maxBytes === undefined) return normalizeJsonValue(value);
	if (!Number.isSafeInteger(maxBytes) || maxBytes < 1)
		throw new RangeError("maxBytes must be a positive safe integer.");
	const nestedDepth =
		limits.maxJsonDepth > Number.MAX_SAFE_INTEGER - NESTED_VALUE_ENVELOPE_DEPTH
			? Number.MAX_SAFE_INTEGER
			: limits.maxJsonDepth + NESTED_VALUE_ENVELOPE_DEPTH;
	const json = canonicalizeJsonValueWithin(value, {
		maxDepth: Math.max(STORED_JOB_FIXED_DEPTH, nestedDepth),
		maxBytes,
	});
	return JSON.parse(json) as unknown;
}

function assertBundleLinks(attempts: readonly StoredMediaAttempt[], bundles: readonly ResultBundle[]): void {
	const byId = new Map(attempts.map((attempt) => [attempt.attemptId, attempt]));
	const linked = new Set<string>();
	for (const bundle of bundles) {
		const attempt = byId.get(bundle.attemptId);
		if (!attempt || attempt.bundleId !== bundle.bundleId || linked.has(attempt.attemptId)) invalid();
		linked.add(attempt.attemptId);
		const expectedDisposition =
			attempt.disposition === "abandoned"
				? "late"
				: attempt.status === "succeeded"
					? "complete"
					: attempt.status === "partially_succeeded"
						? "partial"
						: undefined;
		if (bundle.disposition !== expectedDisposition) invalid();
		const resources = attempt.outputSettlements.flatMap((settlement) =>
			settlement.status === "published" ? [settlement.resource] : [],
		);
		const failures = attempt.outputSettlements.flatMap((settlement) =>
			settlement.status === "failed" ? [{ ordinal: settlement.ordinal, failure: settlement.failure }] : [],
		);
		if (
			canonicalizeJsonValue(resources as unknown as JsonValue) !==
				canonicalizeJsonValue(bundle.resources as unknown as JsonValue) ||
			canonicalizeJsonValue(failures as unknown as JsonValue) !==
				canonicalizeJsonValue(bundle.failures as unknown as JsonValue)
		) {
			invalid();
		}
	}
	for (const attempt of attempts) {
		if ((attempt.bundleId !== undefined) !== linked.has(attempt.attemptId)) invalid();
	}
}

function exactRecord(
	value: unknown,
	required: readonly string[],
	optional: readonly string[] = [],
): Record<string, unknown> {
	if (typeof value !== "object" || value === null || Array.isArray(value)) invalid();
	const record = value as Record<string, unknown>;
	const keys = Object.keys(record);
	if (required.some((key) => !Object.hasOwn(record, key))) invalid();
	if (keys.some((key) => !required.includes(key) && !optional.includes(key))) invalid();
	return record;
}

function exactKeys(value: Record<string, unknown>, expected: readonly string[]): void {
	const keys = Object.keys(value);
	if (keys.length !== expected.length || keys.some((key) => !expected.includes(key))) invalid();
}

function identifier(value: unknown, maxBytes: number): string {
	if (typeof value !== "string") return invalid();
	assertOpaqueIdentifier(value);
	if (Buffer.byteLength(value) > maxBytes) throw new MediaJobCapacityError("identifier");
	return value;
}

function timestamp(value: unknown): string {
	assertCanonicalTimestamp(value);
	return value;
}

function positiveRevision(value: unknown): string {
	if (typeof value !== "string" || !/^[1-9][0-9]*$/u.test(value)) return invalid();
	BigInt(value);
	return value;
}

function outputOrdinal(value: unknown, limits: Readonly<MediaJobLimits>): number {
	if (!Number.isSafeInteger(value) || (value as number) < 0) return invalid();
	if ((value as number) >= limits.maxOutputsPerAttempt) throw new MediaJobCapacityError("outputs");
	return value as number;
}

function assertStrictOrdinals(values: readonly { readonly ordinal: number }[]): void {
	for (let index = 1; index < values.length; index += 1) {
		if ((values[index - 1]?.ordinal ?? -1) >= (values[index]?.ordinal ?? -1)) invalid();
	}
}

function includes<const T extends readonly unknown[]>(values: T, value: unknown): value is T[number] {
	return values.includes(value);
}

function invalid(): never {
	throw new MediaJobValidationError("invalid_job");
}

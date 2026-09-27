import type { JsonValue, RetentionProtectionBudget } from "@opengameagent/protocol";
import type { ImmutableResourceByteReader, ImmutableResourceBytes } from "@opengameagent/resources";
import type { MediaCallAdmissionGroup } from "./media-call-admission.js";

export const MEDIA_KINDS = ["image", "video", "audio", "file"] as const;
export type MediaKind = (typeof MEDIA_KINDS)[number];

export const MEDIA_JOB_STATUSES = [
	"queued",
	"submitting",
	"pending",
	"cancel_requested",
	"settling",
	"outcome_unknown",
	"succeeded",
	"partially_succeeded",
	"failed",
	"cancelled",
] as const;
export type MediaJobStatus = (typeof MEDIA_JOB_STATUSES)[number];

export type MediaJobDisposition = "active" | "abandoned";
export type MediaCancellation = "not_requested" | "requested" | "unsupported" | "unknown" | "rejected" | "confirmed";

export const MEDIA_FAILURE_KINDS = [
	"authentication",
	"permission",
	"transport",
	"provider",
	"malformed",
	"capacity",
	"resource",
	"unknown",
] as const;
export type MediaFailureKind = (typeof MEDIA_FAILURE_KINDS)[number];

/** Every value is mandatory host configuration; this package supplies no workload defaults. */
export interface MediaJobLimits {
	readonly maxIdentifierBytes: number;
	readonly maxAdapterIdBytes: number;
	readonly maxAdapters: number;
	readonly maxRequestBytes: number;
	readonly maxJsonDepth: number;
	readonly maxProviderHandleBytes: number;
	readonly maxAttemptsPerJob: number;
	readonly maxOutputsPerAttempt: number;
	readonly maxResourceBytes: number;
	readonly maxBundleBytes: number;
	readonly maxManifestBytes: number;
	readonly maxMediaTypeBytes: number;
}

export interface MediaJobDraft {
	readonly jobId: string;
	readonly kind: MediaKind;
	readonly input: JsonValue;
}

export interface MediaGenerationRequest {
	readonly kind: MediaKind;
	readonly input: JsonValue;
}

export type MediaResourceByteReader = ImmutableResourceByteReader;
export type MediaResourceBytes = ImmutableResourceBytes;

export interface MediaProviderResource {
	readonly ordinal: number;
	readonly kind: MediaKind;
	readonly mediaType: string;
	readonly bytes: MediaResourceBytes;
	readonly declaredBytes?: number;
}

export interface MediaChildFailure {
	readonly ordinal: number;
	readonly failure: MediaFailureKind;
}

type MediaProviderHandle = { readonly handle?: JsonValue };

export type MediaProviderObservation =
	| ({ readonly status: "pending"; readonly handle: JsonValue } & MediaProviderHandle)
	| ({ readonly status: "succeeded"; readonly resources: readonly MediaProviderResource[] } & MediaProviderHandle)
	| ({
			readonly status: "partially_succeeded";
			readonly resources: readonly MediaProviderResource[];
			readonly failures: readonly MediaChildFailure[];
	  } & MediaProviderHandle)
	| ({ readonly status: "failed"; readonly failure: MediaFailureKind } & MediaProviderHandle)
	| ({ readonly status: "cancelled" } & MediaProviderHandle);

export type MediaProviderCancelObservation =
	| { readonly status: "confirmed" | "unsupported" | "rejected" | "unknown" }
	| { readonly status: "settled"; readonly observation: MediaProviderObservation };

export interface MediaProviderOperationContext {
	readonly jobId: string;
	readonly attemptId: string;
	readonly submissionKey: string;
	readonly signal: AbortSignal;
}

export interface MediaGenerationProvider {
	start(
		request: Readonly<MediaGenerationRequest>,
		context: Readonly<MediaProviderOperationContext>,
	): Promise<MediaProviderObservation>;
	read?(
		handle: Readonly<JsonValue>,
		context: Readonly<MediaProviderOperationContext>,
	): Promise<MediaProviderObservation>;
	cancel?(
		handle: Readonly<JsonValue>,
		context: Readonly<MediaProviderOperationContext>,
	): Promise<MediaProviderCancelObservation>;
}

export interface MediaGenerationAdapter {
	readonly adapterId: string;
	readonly provider: MediaGenerationProvider;
	readonly admission: MediaCallAdmissionGroup;
}

export interface GeneratedResourceRef {
	readonly resourceId: string;
	readonly kind: MediaKind;
	readonly mediaType: string;
	readonly byteLength: number;
	readonly sha256: string;
	readonly ordinal: number;
}

export interface GeneratedResourceSelector {
	readonly jobId: string;
	readonly attemptId: string;
	readonly bundleId: string;
	readonly ordinal: number;
	readonly resourceId: string;
}

export interface GeneratedResourceSelection {
	readonly jobId: string;
	readonly attemptId: string;
	readonly bundleId: string;
	readonly bundleDisposition: "complete" | "partial";
	readonly resource: GeneratedResourceRef;
}

export interface MediaResourcePublishContext {
	readonly maxBytes: number;
	readonly maxMediaTypeBytes: number;
	readonly signal: AbortSignal;
}

export interface MediaResourcePublisher {
	publish(
		resource: Readonly<MediaProviderResource>,
		context: Readonly<MediaResourcePublishContext>,
	): Promise<GeneratedResourceRef>;
}

export interface MediaResourceReadContext {
	readonly maxBytes: number;
	readonly maxMediaTypeBytes: number;
	readonly signal: AbortSignal;
}

export interface MediaResourceReader {
	read(reference: Readonly<GeneratedResourceRef>, context: Readonly<MediaResourceReadContext>): Promise<Uint8Array>;
}

export interface ResultBundle {
	readonly bundleId: string;
	readonly jobId: string;
	readonly attemptId: string;
	readonly disposition: "complete" | "partial" | "late";
	readonly resources: readonly GeneratedResourceRef[];
	readonly failures: readonly MediaChildFailure[];
}

export type StoredMediaOutputSettlement =
	| { readonly ordinal: number; readonly status: "published"; readonly resource: GeneratedResourceRef }
	| { readonly ordinal: number; readonly status: "failed"; readonly failure: MediaFailureKind };

export interface StoredMediaAttempt {
	readonly attemptId: string;
	readonly ordinal: number;
	readonly submissionKey: string;
	readonly status: MediaJobStatus;
	readonly disposition: MediaJobDisposition;
	readonly cancellation: MediaCancellation;
	readonly startedAt: string;
	readonly updatedAt: string;
	readonly providerHandle?: JsonValue;
	readonly settlingStatus?: "succeeded" | "partially_succeeded";
	readonly outputSettlements: readonly StoredMediaOutputSettlement[];
	readonly bundleId?: string;
	readonly failure?: MediaFailureKind;
}

export interface StoredMediaJob {
	readonly jobId: string;
	readonly adapterId: string;
	readonly request: MediaGenerationRequest;
	readonly revision: string;
	readonly createdAt: string;
	readonly updatedAt: string;
	readonly currentAttemptId: string;
	readonly attempts: readonly StoredMediaAttempt[];
	readonly bundles: readonly ResultBundle[];
}

export interface MediaJobStore {
	create(job: Readonly<StoredMediaJob>): Promise<void>;
	read(jobId: string): Promise<StoredMediaJob | undefined>;
	modify(
		jobId: string,
		change: (current: Readonly<StoredMediaJob>) => Readonly<StoredMediaJob>,
	): Promise<StoredMediaJob>;
	purge(request: Readonly<MediaJobPurgeRequest>): Promise<MediaJobPurgeResult>;
	listQueued(request: Readonly<MediaQueuedJobListRequest>): Promise<readonly StoredMediaJob[]>;
}

export interface MediaQueuedJobListRequest {
	readonly adapterId: string;
	readonly afterJobId?: string;
	readonly limit: number;
}

export interface MediaJobPurgeRequest {
	readonly beforeUpdatedAt: string;
	readonly limit: number;
	/** Complete job IDs still referenced by the host, including durable archives. */
	readonly retainJobIds: readonly string[];
	readonly retainBudget: RetentionProtectionBudget;
}

export interface MediaJobPurgeResult {
	readonly deletedJobs: number;
}

export interface MediaAttemptView {
	readonly attemptId: string;
	readonly ordinal: number;
	readonly status: MediaJobStatus;
	readonly disposition: MediaJobDisposition;
	readonly cancellation: MediaCancellation;
	readonly startedAt: string;
	readonly updatedAt: string;
	readonly bundleId?: string;
	readonly failure?: MediaFailureKind;
}

export interface MediaJobView {
	readonly jobId: string;
	readonly kind: MediaKind;
	readonly revision: string;
	readonly createdAt: string;
	readonly updatedAt: string;
	readonly currentAttemptId: string;
	readonly status: MediaJobStatus;
	readonly disposition: MediaJobDisposition;
	readonly cancellation: MediaCancellation;
	readonly bundleId?: string;
	readonly failure?: MediaFailureKind;
	readonly attempts: readonly MediaAttemptView[];
}

export interface MediaJobControl {
	readonly jobId: string;
	readonly attemptId?: string;
	readonly expectedRevision?: string;
}

export interface MediaJobRestart {
	readonly jobId: string;
	readonly expectedRevision?: string;
}

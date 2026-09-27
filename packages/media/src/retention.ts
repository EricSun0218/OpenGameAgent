import { assertCanonicalTimestamp, assertOpaqueIdentifier, captureRetainedIdentifiers } from "@opengameagent/protocol";
import type { MediaJobPurgeRequest, MediaQueuedJobListRequest, StoredMediaJob } from "./types.js";

export function captureMediaJobPurgeRequest(request: Readonly<MediaJobPurgeRequest>): Readonly<MediaJobPurgeRequest> {
	const beforeUpdatedAt = request.beforeUpdatedAt;
	const limit = request.limit;
	assertCanonicalTimestamp(beforeUpdatedAt);
	if (!Number.isSafeInteger(limit) || limit < 1) throw new RangeError("limit must be a positive safe integer.");
	const retainBudget = request.retainBudget;
	const retainJobIds = captureRetainedIdentifiers(request.retainJobIds, retainBudget, "retainJobIds");
	for (const jobId of retainJobIds) assertOpaqueIdentifier(jobId);
	return Object.freeze({ beforeUpdatedAt, limit, retainJobIds, retainBudget });
}

/** Older attempts are already terminal or abandoned by the stored-job contract. */
export function isMediaJobPurgeable(job: Readonly<StoredMediaJob>): boolean {
	const current = job.attempts.at(-1);
	return (
		current !== undefined &&
		(current.disposition === "abandoned" ||
			current.status === "succeeded" ||
			current.status === "partially_succeeded" ||
			current.status === "failed" ||
			current.status === "cancelled")
	);
}

export function captureMediaQueuedJobListRequest(
	request: Readonly<MediaQueuedJobListRequest>,
): Readonly<MediaQueuedJobListRequest> {
	const adapterId = request.adapterId;
	const afterJobId = request.afterJobId;
	const limit = request.limit;
	assertOpaqueIdentifier(adapterId);
	if (afterJobId !== undefined) assertOpaqueIdentifier(afterJobId);
	if (!Number.isSafeInteger(limit) || limit < 1) throw new RangeError("limit must be a positive safe integer.");
	return Object.freeze({ adapterId, limit, ...(afterJobId === undefined ? {} : { afterJobId }) });
}

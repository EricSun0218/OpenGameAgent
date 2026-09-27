import { Mutex } from "async-mutex";
import { MediaJobConflictError, MediaJobNotFoundError } from "./errors.js";
import { captureMediaJobLimits, captureStoredMediaJob } from "./stored-media-job.js";
import { captureMediaJobPurgeRequest, captureMediaQueuedJobListRequest, isMediaJobPurgeable } from "./retention.js";
import type {
	MediaJobLimits,
	MediaJobPurgeRequest,
	MediaJobPurgeResult,
	MediaJobStore,
	MediaQueuedJobListRequest,
	StoredMediaJob,
} from "./types.js";

/** Process-local test and lightweight host store. Durable hosts use the same Store contract. */
export class InMemoryMediaJobStore implements MediaJobStore {
	private readonly mutex = new Mutex();
	private readonly jobs = new Map<string, StoredMediaJob>();
	private readonly limits: Readonly<MediaJobLimits>;

	constructor(limits: Readonly<MediaJobLimits>) {
		this.limits = captureMediaJobLimits(limits);
	}

	async create(job: Readonly<StoredMediaJob>): Promise<void> {
		const captured = captureStoredMediaJob(job, this.limits);
		await this.mutex.runExclusive(() => {
			if (this.jobs.has(captured.jobId)) throw new MediaJobConflictError();
			this.jobs.set(captured.jobId, captured);
		});
	}

	async read(jobId: string): Promise<StoredMediaJob | undefined> {
		return this.mutex.runExclusive(() => {
			const job = this.jobs.get(jobId);
			return job ? structuredClone(job) : undefined;
		});
	}

	async purge(request: Readonly<MediaJobPurgeRequest>): Promise<MediaJobPurgeResult> {
		const captured = captureMediaJobPurgeRequest(request);
		const retained = new Set(captured.retainJobIds);
		return this.mutex.runExclusive(() => {
			let deletedJobs = 0;
			for (const [jobId, job] of this.jobs) {
				if (
					retained.has(jobId) ||
					Date.parse(job.updatedAt) >= Date.parse(captured.beforeUpdatedAt) ||
					!isMediaJobPurgeable(job)
				)
					continue;
				this.jobs.delete(jobId);
				if (++deletedJobs === captured.limit) break;
			}
			return { deletedJobs };
		});
	}

	async listQueued(request: Readonly<MediaQueuedJobListRequest>): Promise<readonly StoredMediaJob[]> {
		const captured = captureMediaQueuedJobListRequest(request);
		return this.mutex.runExclusive(() => {
			const result: StoredMediaJob[] = [];
			// ponytail: bounded selection over a process-local Map; use the indexed SQLite Store for large job histories.
			for (const job of this.jobs.values()) {
				if (
					job.adapterId !== captured.adapterId ||
					job.attempts.at(-1)?.status !== "queued" ||
					(captured.afterJobId !== undefined && compareJobIds(job.jobId, captured.afterJobId) <= 0)
				)
					continue;
				const index = result.findIndex((other) => compareJobIds(job.jobId, other.jobId) < 0);
				if (index >= 0) result.splice(index, 0, job);
				else if (result.length < captured.limit) result.push(job);
				if (result.length > captured.limit) result.pop();
			}
			return structuredClone(result);
		});
	}

	async modify(
		jobId: string,
		change: (current: Readonly<StoredMediaJob>) => Readonly<StoredMediaJob>,
	): Promise<StoredMediaJob> {
		return this.mutex.runExclusive(() => {
			const stored = this.jobs.get(jobId);
			if (!stored) throw new MediaJobNotFoundError();
			const current = structuredClone(stored);
			const changed = change(current);
			if (changed === current) return structuredClone(stored);
			const next = captureStoredMediaJob(changed, this.limits, { revision: (BigInt(stored.revision) + 1n).toString() });
			if (next.jobId !== stored.jobId || next.adapterId !== stored.adapterId) {
				throw new MediaJobConflictError();
			}
			this.jobs.set(jobId, next);
			return structuredClone(next);
		});
	}
}

function compareJobIds(left: string, right: string): number {
	return Buffer.compare(Buffer.from(left, "utf8"), Buffer.from(right, "utf8"));
}

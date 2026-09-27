import {
	assertCanonicalTimestamp,
	assertOpaqueIdentifier,
	canonicalizeJsonValueWithin,
	type JsonValue,
	JsonValueLimitError,
	normalizeJsonValueWithin,
} from "@opengameagent/protocol";
import {
	MediaJobAdapterUnavailableError,
	MediaJobCapacityError,
	MediaJobConflictError,
	MediaJobControlUnsupportedError,
	MediaJobNotFoundError,
	MediaJobValidationError,
} from "./errors.js";
import { captureMediaJobLimits } from "./stored-media-job.js";
import { MediaCallAdmissionGroup, type MediaCallReservation } from "./media-call-admission.js";
import { captureMediaQueuedJobListRequest } from "./retention.js";
import {
	type GeneratedResourceRef,
	type GeneratedResourceSelection,
	type GeneratedResourceSelector,
	MEDIA_FAILURE_KINDS,
	MEDIA_KINDS,
	type MediaAttemptView,
	type MediaCancellation,
	type MediaChildFailure,
	type MediaFailureKind,
	type MediaGenerationAdapter,
	type MediaGenerationProvider,
	type MediaGenerationRequest,
	type MediaJobControl,
	type MediaJobDisposition,
	type MediaJobDraft,
	type MediaJobLimits,
	type MediaJobRestart,
	type MediaJobStatus,
	type MediaJobStore,
	type MediaJobView,
	type MediaQueuedJobListRequest,
	type MediaProviderCancelObservation,
	type MediaProviderObservation,
	type MediaProviderOperationContext,
	type MediaProviderResource,
	type MediaResourceBytes,
	type MediaResourcePublisher,
	type ResultBundle,
	type StoredMediaAttempt,
	type StoredMediaJob,
	type StoredMediaOutputSettlement,
} from "./types.js";

const SHA256 = /^[0-9a-f]{64}$/u;
const TERMINAL = new Set<MediaJobStatus>(["succeeded", "partially_succeeded", "failed", "cancelled"]);

export interface MediaJobServiceOptions {
	readonly adapters: Iterable<Readonly<MediaGenerationAdapter>>;
	readonly store: MediaJobStore;
	readonly resources: MediaResourcePublisher;
	readonly limits: MediaJobLimits;
	readonly newId: (kind: "attempt" | "submission" | "bundle") => string;
	readonly now: () => number;
}

type CapturedProviderHandle = { readonly handle?: JsonValue };
type CapturedProviderObservation =
	| ({ readonly status: "pending"; readonly handle: JsonValue } & CapturedProviderHandle)
	| ({ readonly status: "succeeded"; readonly resources: readonly MediaProviderResource[] } & CapturedProviderHandle)
	| ({
			readonly status: "partially_succeeded";
			readonly resources: readonly MediaProviderResource[];
			readonly failures: readonly MediaChildFailure[];
	  } & CapturedProviderHandle)
	| ({ readonly status: "failed"; readonly failure: MediaFailureKind } & CapturedProviderHandle)
	| ({ readonly status: "cancelled" } & CapturedProviderHandle);
type CapturedProviderCompletion = Extract<
	CapturedProviderObservation,
	{ readonly status: "succeeded" | "partially_succeeded" }
>;

export class MediaJobService {
	private readonly adapters: ReadonlyMap<string, MediaGenerationAdapter>;
	private readonly queuedStarts = new Map<string, Set<AbortController>>();
	private readonly store: MediaJobStore;
	private readonly resources: MediaResourcePublisher;
	private readonly limits: Readonly<MediaJobLimits>;
	private readonly newId: MediaJobServiceOptions["newId"];
	private readonly now: MediaJobServiceOptions["now"];

	constructor(options: MediaJobServiceOptions) {
		if (!options || typeof options !== "object") throw new MediaJobValidationError("invalid_job");
		const limits = options.limits;
		const adapters = options.adapters;
		const store = options.store;
		const resources = options.resources;
		const newId = options.newId;
		const now = options.now;
		this.limits = captureMediaJobLimits(limits);
		this.adapters = captureAdapters(adapters, this.limits);
		if (
			typeof store?.create !== "function" ||
			typeof store.read !== "function" ||
			typeof store.modify !== "function" ||
			typeof store.listQueued !== "function" ||
			typeof store.purge !== "function"
		) {
			throw new MediaJobValidationError("invalid_job");
		}
		if (typeof resources?.publish !== "function") throw new MediaJobValidationError("invalid_job");
		if (typeof newId !== "function" || typeof now !== "function") {
			throw new MediaJobValidationError("invalid_job");
		}
		this.store = store;
		this.resources = resources;
		this.newId = newId;
		this.now = now;
	}

	async submit(adapterId: string, draft: Readonly<MediaJobDraft>, signal?: AbortSignal): Promise<MediaJobView> {
		if (typeof adapterId !== "string") throw new MediaJobAdapterUnavailableError();
		const selectedAdapterId = identifier(adapterId, this.limits.maxAdapterIdBytes);
		this.providerFor(selectedAdapterId);
		const captured = this.captureDraft(draft);
		const request = captured.request;
		const jobId = captured.jobId;
		const operationSignal = signal ?? new AbortController().signal;
		operationSignal.throwIfAborted();
		const recordedAt = this.timestamp();
		const attempt = this.newAttempt(0, recordedAt);
		const reservation = this.reserveStart(selectedAdapterId, jobId, attempt.attemptId, operationSignal);
		try {
			await this.store.create({
				jobId,
				adapterId: selectedAdapterId,
				request,
				revision: "1",
				createdAt: recordedAt,
				updatedAt: recordedAt,
				currentAttemptId: attempt.attemptId,
				attempts: [attempt],
				bundles: [],
			});
			return await this.startAttempt(jobId, attempt.attemptId, operationSignal, reservation);
		} finally {
			reservation.release();
		}
	}

	async read(jobId: string): Promise<MediaJobView | undefined> {
		const current = await this.store.read(identifier(jobId, this.limits.maxIdentifierBytes));
		if (!current) return undefined;
		this.providerFor(current.adapterId);
		return view(current);
	}

	async listQueued(request: Readonly<MediaQueuedJobListRequest>): Promise<readonly MediaJobView[]> {
		const captured = captureMediaQueuedJobListRequest(request);
		this.providerFor(identifier(captured.adapterId, this.limits.maxAdapterIdBytes));
		if (captured.afterJobId !== undefined) identifier(captured.afterJobId, this.limits.maxIdentifierBytes);
		return (await this.store.listQueued(captured)).map(view);
	}

	async readBundle(jobId: string, bundleId: string): Promise<ResultBundle | undefined> {
		const current = await this.store.read(identifier(jobId, this.limits.maxIdentifierBytes));
		if (!current) return undefined;
		this.providerFor(current.adapterId);
		const safeBundleId = identifier(bundleId, this.limits.maxIdentifierBytes);
		const bundle = current.bundles.find((candidate) => candidate.bundleId === safeBundleId);
		return bundle ? freezeBundle(bundle) : undefined;
	}

	async selectResource(
		selector: Readonly<GeneratedResourceSelector>,
		signal?: AbortSignal,
	): Promise<GeneratedResourceSelection | undefined> {
		const captured = this.captureResourceSelector(selector);
		const operationSignal = signal ?? new AbortController().signal;
		operationSignal.throwIfAborted();
		const current = await this.store.read(captured.jobId);
		operationSignal.throwIfAborted();
		if (!current) return undefined;
		this.providerFor(current.adapterId);
		const bundle = current.bundles.find((candidate) => candidate.bundleId === captured.bundleId);
		if (
			!bundle ||
			bundle.attemptId !== captured.attemptId ||
			(bundle.disposition !== "complete" && bundle.disposition !== "partial")
		) {
			return undefined;
		}
		const resource = bundle.resources.find(
			(candidate) => candidate.ordinal === captured.ordinal && candidate.resourceId === captured.resourceId,
		);
		if (!resource) return undefined;
		return Object.freeze({
			jobId: current.jobId,
			attemptId: bundle.attemptId,
			bundleId: bundle.bundleId,
			bundleDisposition: bundle.disposition,
			resource: Object.freeze({ ...resource }),
		});
	}

	async reconcile(control: Readonly<MediaJobControl>, signal?: AbortSignal): Promise<MediaJobView> {
		const captured = this.captureControl(control);
		const jobId = captured.jobId;
		const operationSignal = signal ?? new AbortController().signal;
		operationSignal.throwIfAborted();
		const current = await this.required(jobId);
		operationSignal.throwIfAborted();
		const provider = this.providerFor(current.adapterId);
		this.assertExpected(current, captured.expectedRevision);
		const attempt = this.selectAttempt(current, captured.attemptId);
		if (TERMINAL.has(attempt.status)) return view(current);
		if (attempt.status === "queued") return this.startAttempt(current.jobId, attempt.attemptId, operationSignal);
		if (attempt.status === "submitting" && attempt.providerHandle === undefined) {
			const changed = await this.recoverInterruptedSubmission(current.jobId, attempt.attemptId);
			operationSignal.throwIfAborted();
			return changed;
		}
		const handle = attempt.providerHandle;
		const read = provider.read;
		if (handle === undefined || typeof read !== "function") {
			throw new MediaJobControlUnsupportedError();
		}
		let observation: MediaProviderObservation;
		let invoked = false;
		try {
			observation = await this.withProviderAdmission(current.adapterId, operationSignal, () => {
				invoked = true;
				return read(structuredClone(handle), this.operationContext(current, attempt, operationSignal));
			});
		} catch (error) {
			if (!invoked) throw error;
			const changed = await this.markUnknown(current.jobId, attempt.attemptId);
			if (operationSignal.aborted) throw operationSignal.reason;
			return changed;
		}
		const changed = await this.applyObservation(current.jobId, attempt.attemptId, observation, operationSignal);
		operationSignal.throwIfAborted();
		return changed;
	}

	async cancel(control: Readonly<MediaJobControl>, signal?: AbortSignal): Promise<MediaJobView> {
		const captured = this.captureControl(control);
		const jobId = captured.jobId;
		const operationSignal = signal ?? new AbortController().signal;
		operationSignal.throwIfAborted();
		const marked = await this.store.modify(jobId, (current) => {
			this.providerFor(current.adapterId);
			this.assertExpected(current, captured.expectedRevision);
			const attempt = this.selectAttempt(current, captured.attemptId);
			if (TERMINAL.has(attempt.status)) return current;
			const status =
				attempt.status === "queued" ? "cancelled" : attempt.status === "pending" ? "cancel_requested" : attempt.status;
			return replaceAttempt(current, attempt.attemptId, {
				...attempt,
				status,
				disposition: "abandoned",
				cancellation: "requested",
				updatedAt: this.timestamp(),
			});
		});
		const provider = this.providerFor(marked.adapterId);
		const attempt = this.selectAttempt(marked, captured.attemptId);
		if (attempt.status === "cancelled") {
			for (const controller of this.queuedStarts.get(JSON.stringify([jobId, attempt.attemptId])) ?? [])
				controller.abort();
		}
		operationSignal.throwIfAborted();
		if (TERMINAL.has(attempt.status)) return view(marked);
		if (attempt.status === "settling") return view(marked);
		if (attempt.providerHandle === undefined) return view(marked);
		const cancel = provider.cancel;
		const handle = attempt.providerHandle;
		if (typeof cancel !== "function") {
			const changed = await this.recordCancellation(marked.jobId, attempt.attemptId, "unsupported");
			operationSignal.throwIfAborted();
			return changed;
		}
		let result: MediaProviderCancelObservation;
		let invoked = false;
		try {
			result = await this.withProviderAdmission(marked.adapterId, operationSignal, () => {
				invoked = true;
				return cancel(structuredClone(handle), this.operationContext(marked, attempt, operationSignal));
			});
		} catch (error) {
			if (!invoked) throw error;
			const changed = await this.recordCancellation(marked.jobId, attempt.attemptId, "unknown");
			if (operationSignal.aborted) throw operationSignal.reason;
			return changed;
		}
		if (!result || typeof result !== "object" || !("status" in result)) {
			const changed = await this.recordCancellation(marked.jobId, attempt.attemptId, "unknown");
			operationSignal.throwIfAborted();
			return changed;
		}
		if (result.status === "settled") {
			const changed = await this.applyObservation(marked.jobId, attempt.attemptId, result.observation, operationSignal);
			operationSignal.throwIfAborted();
			return changed;
		}
		if (!includes(["confirmed", "unsupported", "rejected", "unknown"] as const, result.status)) {
			const changed = await this.recordCancellation(marked.jobId, attempt.attemptId, "unknown");
			operationSignal.throwIfAborted();
			return changed;
		}
		const changed = await this.recordCancellation(marked.jobId, attempt.attemptId, result.status);
		operationSignal.throwIfAborted();
		return changed;
	}

	async restart(input: Readonly<MediaJobRestart>, signal?: AbortSignal): Promise<MediaJobView> {
		const captured = this.captureRestart(input);
		const jobId = captured.jobId;
		const operationSignal = signal ?? new AbortController().signal;
		operationSignal.throwIfAborted();
		const before = await this.required(jobId);
		this.assertRestartable(before, captured.expectedRevision);
		const proposedAttemptId = this.generatedId("attempt");
		const proposedSubmissionKey = this.generatedId("submission");
		const reservation = this.reserveStart(before.adapterId, jobId, proposedAttemptId, operationSignal);
		try {
			const restarted = await this.store.modify(jobId, (current) => {
				this.assertRestartable(current, captured.expectedRevision);
				if (
					current.attempts.some(
						(candidate) =>
							candidate.attemptId === proposedAttemptId || candidate.submissionKey === proposedSubmissionKey,
					)
				) {
					throw new MediaJobConflictError();
				}
				const attempt = this.newAttempt(
					current.attempts.length,
					this.timestamp(),
					proposedAttemptId,
					proposedSubmissionKey,
				);
				return {
					...current,
					currentAttemptId: attempt.attemptId,
					attempts: [...current.attempts, attempt],
					updatedAt: attempt.startedAt,
				};
			});
			return await this.startAttempt(jobId, restarted.currentAttemptId, operationSignal, reservation);
		} finally {
			reservation.release();
		}
	}

	private async startAttempt(
		jobId: string,
		attemptId: string,
		signal: AbortSignal,
		reservation?: MediaCallReservation,
	): Promise<MediaJobView> {
		const before = await this.required(jobId);
		const reserved = reservation ?? this.reserveStart(before.adapterId, jobId, attemptId, signal);
		try {
			await reserved.ready;
			let claimed = false;
			const current = await this.store.modify(jobId, (record) => {
				const attempt = this.selectAttempt(record, attemptId);
				if (attempt.status !== "queued") return record;
				if (signal.aborted)
					return replaceAttempt(record, attemptId, {
						...attempt,
						status: "cancelled",
						disposition: "abandoned",
						cancellation: "requested",
						updatedAt: this.timestamp(),
					});
				claimed = true;
				return replaceAttempt(record, attemptId, { ...attempt, status: "submitting", updatedAt: this.timestamp() });
			});
			if (claimed && signal.aborted) {
				await this.store.modify(jobId, (record) => {
					const attempt = this.selectAttempt(record, attemptId);
					if (attempt.status !== "submitting" || attempt.providerHandle !== undefined) return record;
					return replaceAttempt(record, attemptId, {
						...attempt,
						status: "cancelled",
						disposition: "abandoned",
						cancellation: "requested",
						updatedAt: this.timestamp(),
					});
				});
			}
			signal.throwIfAborted();
			if (!claimed) return view(current);
			const provider = this.providerFor(current.adapterId);
			const attempt = this.selectAttempt(current, attemptId);
			let observation: MediaProviderObservation;
			try {
				observation = await provider.start(
					freezeRequest(current.request),
					this.operationContext(current, attempt, signal),
				);
			} catch {
				const changed = await this.markUnknown(jobId, attemptId);
				if (signal.aborted) throw signal.reason;
				return changed;
			} finally {
				reserved.release();
			}
			const changed = await this.applyObservation(jobId, attemptId, observation, signal);
			signal.throwIfAborted();
			return changed;
		} finally {
			reserved.release();
		}
	}

	private reserveStart(adapterId: string, jobId: string, attemptId: string, signal: AbortSignal): MediaCallReservation {
		const controller = new AbortController();
		const reservation = this.admissionFor(adapterId).reserve(AbortSignal.any([signal, controller.signal]));
		const key = JSON.stringify([jobId, attemptId]);
		const controllers = this.queuedStarts.get(key) ?? new Set<AbortController>();
		controllers.add(controller);
		this.queuedStarts.set(key, controllers);
		let released = false;
		return {
			ready: reservation.ready,
			release: () => {
				if (released) return;
				released = true;
				controllers.delete(controller);
				if (controllers.size === 0) this.queuedStarts.delete(key);
				reservation.release();
			},
		};
	}

	private async withProviderAdmission<T>(
		adapterId: string,
		signal: AbortSignal,
		operation: () => Promise<T>,
	): Promise<T> {
		const reservation = this.admissionFor(adapterId).reserve(signal);
		try {
			await reservation.ready;
			signal.throwIfAborted();
			return await operation();
		} finally {
			reservation.release();
		}
	}

	private admissionFor(adapterId: string): MediaCallAdmissionGroup {
		const registered = this.adapters.get(adapterId);
		if (!registered) throw new MediaJobAdapterUnavailableError();
		return registered.admission;
	}

	private async applyObservation(
		jobId: string,
		attemptId: string,
		value: unknown,
		signal: AbortSignal,
	): Promise<MediaJobView> {
		let observation: CapturedProviderObservation;
		try {
			observation = this.captureObservation(value);
		} catch (error) {
			return this.markFailed(
				jobId,
				attemptId,
				error instanceof MediaJobCapacityError || error instanceof JsonValueLimitError ? "capacity" : "malformed",
			);
		}
		if (observation.status === "pending") {
			const handle = observation.handle;
			const changed = await this.store.modify(jobId, (current) => {
				const attempt = this.selectAttempt(current, attemptId);
				if (TERMINAL.has(attempt.status) || attempt.status === "settling") return current;
				return replaceAttempt(current, attemptId, {
					...clearAttemptOutcome(attempt),
					status: attempt.disposition === "abandoned" ? "cancel_requested" : "pending",
					providerHandle: handle,
					updatedAt: this.timestamp(),
				});
			});
			return view(changed);
		}
		if (observation.status === "failed") {
			return this.markFailed(jobId, attemptId, observation.failure, observation.handle);
		}
		if (observation.status === "cancelled") {
			const changed = await this.store.modify(jobId, (current) => {
				const attempt = this.selectAttempt(current, attemptId);
				if (TERMINAL.has(attempt.status) || attempt.status === "settling") return current;
				return replaceAttempt(current, attemptId, {
					...clearAttemptOutcome(attempt),
					status: "cancelled",
					cancellation: "confirmed",
					...(observation.handle === undefined ? {} : { providerHandle: observation.handle }),
					updatedAt: this.timestamp(),
				});
			});
			return view(changed);
		}
		return this.publishBundle(jobId, attemptId, observation, signal);
	}

	private async publishBundle(
		jobId: string,
		attemptId: string,
		observation: CapturedProviderCompletion,
		signal: AbortSignal,
	): Promise<MediaJobView> {
		const resources = observation.resources;
		const failures = observation.status === "partially_succeeded" ? observation.failures : [];
		const bundleId = this.generatedId("bundle");
		const published: GeneratedResourceRef[] = [];
		let totalBytes = 0;
		for (const resource of resources) {
			const remaining = this.limits.maxBundleBytes - totalBytes;
			if (resource.declaredBytes !== undefined && resource.declaredBytes > remaining) {
				const changed = await this.recordOutputFailure(
					jobId,
					attemptId,
					published,
					failures,
					resource.ordinal,
					"capacity",
					observation.status,
					observation.handle,
				);
				if (signal.aborted) throw signal.reason;
				return changed;
			}
			const maxBytes = Math.min(this.limits.maxResourceBytes, remaining);
			let reference: GeneratedResourceRef;
			try {
				reference = await this.resources.publish(resource, {
					maxBytes,
					maxMediaTypeBytes: this.limits.maxMediaTypeBytes,
					signal,
				});
			} catch (error) {
				if (signal.aborted) {
					await this.recordSettling(jobId, attemptId, published, failures, observation.status, observation.handle);
					throw signal.reason;
				}
				return this.recordOutputFailure(
					jobId,
					attemptId,
					published,
					failures,
					resource.ordinal,
					error instanceof MediaJobCapacityError ? "capacity" : "resource",
					observation.status,
					observation.handle,
				);
			}
			let captured: GeneratedResourceRef;
			try {
				captured = this.captureResourceReference(reference, resource, maxBytes);
			} catch (error) {
				if (!(error instanceof MediaJobCapacityError || error instanceof MediaJobValidationError)) throw error;
				const changed = await this.recordOutputFailure(
					jobId,
					attemptId,
					published,
					failures,
					resource.ordinal,
					error instanceof MediaJobCapacityError ? "capacity" : "resource",
					observation.status,
					observation.handle,
				);
				if (signal.aborted) throw signal.reason;
				return changed;
			}
			totalBytes += captured.byteLength;
			published.push(captured);
		}
		const newSettlements = freezeOutputSettlements(published, failures);
		const changed = await this.store.modify(jobId, (current) => {
			const attempt = this.selectAttempt(current, attemptId);
			if (TERMINAL.has(attempt.status)) return current;
			if (attempt.status === "settling" && attempt.settlingStatus !== observation.status) return current;
			if (current.bundles.some((bundle) => bundle.bundleId === bundleId)) throw new MediaJobConflictError();
			const outputSettlements = mergeOutputSettlements(attempt.outputSettlements, newSettlements);
			const updatedAt = this.timestamp();
			if (exceedsBundleBytes(outputSettlements, this.limits.maxBundleBytes)) {
				return replaceAttempt(current, attemptId, {
					...clearAttemptOutcome(attempt),
					status: "failed",
					failure: "capacity",
					outputSettlements,
					...(observation.handle === undefined ? {} : { providerHandle: observation.handle }),
					updatedAt,
				});
			}
			const disposition = bundleDisposition(observation.status, attempt.disposition);
			const bundle = freezeBundle({
				bundleId,
				jobId,
				attemptId,
				disposition,
				resources: outputSettlements.flatMap((settlement) =>
					settlement.status === "published" ? [settlement.resource] : [],
				),
				failures: outputSettlements.flatMap((settlement) =>
					settlement.status === "failed" ? [{ ordinal: settlement.ordinal, failure: settlement.failure }] : [],
				),
			});
			try {
				canonicalizeJsonValueWithin(bundle, {
					maxDepth: this.limits.maxJsonDepth,
					maxBytes: this.limits.maxManifestBytes,
				});
			} catch (error) {
				if (!(error instanceof JsonValueLimitError)) throw error;
				return replaceAttempt(current, attemptId, {
					...clearAttemptOutcome(attempt),
					status: "failed",
					failure: "capacity",
					outputSettlements,
					...(observation.handle === undefined ? {} : { providerHandle: observation.handle }),
					updatedAt,
				});
			}
			return {
				...replaceAttempt(current, attemptId, {
					...clearAttemptOutcome(attempt),
					status: observation.status,
					bundleId,
					outputSettlements,
					...(observation.handle === undefined ? {} : { providerHandle: observation.handle }),
					updatedAt,
				}),
				bundles: [...current.bundles, bundle],
			};
		});
		return view(changed);
	}

	private async recordSettling(
		jobId: string,
		attemptId: string,
		published: readonly GeneratedResourceRef[],
		providerFailures: readonly MediaChildFailure[],
		providerStatus: "succeeded" | "partially_succeeded",
		handle?: JsonValue,
	): Promise<MediaJobView> {
		const newSettlements = freezeOutputSettlements(published, providerFailures);
		const changed = await this.store.modify(jobId, (current) => {
			const attempt = this.selectAttempt(current, attemptId);
			if (TERMINAL.has(attempt.status)) return current;
			if (attempt.status === "settling" && attempt.settlingStatus !== providerStatus) return current;
			const outputSettlements = mergeOutputSettlements(attempt.outputSettlements, newSettlements);
			const updatedAt = this.timestamp();
			if (exceedsBundleBytes(outputSettlements, this.limits.maxBundleBytes)) {
				return replaceAttempt(current, attemptId, {
					...clearAttemptOutcome(attempt),
					status: "failed",
					failure: "capacity",
					outputSettlements,
					...(handle === undefined ? {} : { providerHandle: handle }),
					updatedAt,
				});
			}
			return replaceAttempt(current, attemptId, {
				...clearAttemptOutcome(attempt),
				status: "settling",
				settlingStatus: providerStatus,
				outputSettlements,
				...(handle === undefined ? {} : { providerHandle: handle }),
				updatedAt,
			});
		});
		return view(changed);
	}

	private async markUnknown(jobId: string, attemptId: string, handle?: JsonValue): Promise<MediaJobView> {
		const changed = await this.store.modify(jobId, (current) => {
			const attempt = this.selectAttempt(current, attemptId);
			if (TERMINAL.has(attempt.status) || attempt.status === "settling") return current;
			return replaceAttempt(current, attemptId, {
				...clearAttemptOutcome(attempt),
				status: "outcome_unknown",
				failure: "unknown",
				...(handle === undefined ? {} : { providerHandle: handle }),
				updatedAt: this.timestamp(),
			});
		});
		return view(changed);
	}

	private async recoverInterruptedSubmission(jobId: string, attemptId: string): Promise<MediaJobView> {
		const changed = await this.store.modify(jobId, (current) => {
			const attempt = this.selectAttempt(current, attemptId);
			if (attempt.status !== "submitting" || attempt.providerHandle !== undefined) return current;
			return replaceAttempt(current, attemptId, {
				...clearAttemptOutcome(attempt),
				status: "outcome_unknown",
				failure: "unknown",
				updatedAt: this.timestamp(),
			});
		});
		return view(changed);
	}

	private async markFailed(
		jobId: string,
		attemptId: string,
		failure: MediaFailureKind,
		handle?: JsonValue,
	): Promise<MediaJobView> {
		const changed = await this.store.modify(jobId, (current) => {
			const attempt = this.selectAttempt(current, attemptId);
			if (TERMINAL.has(attempt.status) || attempt.status === "settling") return current;
			return replaceAttempt(current, attemptId, {
				...clearAttemptOutcome(attempt),
				status: "failed",
				failure,
				...(handle === undefined ? {} : { providerHandle: handle }),
				updatedAt: this.timestamp(),
			});
		});
		return view(changed);
	}

	private async recordOutputFailure(
		jobId: string,
		attemptId: string,
		published: readonly GeneratedResourceRef[],
		providerFailures: readonly MediaChildFailure[],
		ordinal: number,
		failure: "capacity" | "resource",
		providerStatus: "succeeded" | "partially_succeeded",
		handle?: JsonValue,
	): Promise<MediaJobView> {
		const newSettlements = freezeOutputSettlements(published, [...providerFailures, { ordinal, failure }]);
		const changed = await this.store.modify(jobId, (current) => {
			const attempt = this.selectAttempt(current, attemptId);
			if (TERMINAL.has(attempt.status)) return current;
			if (attempt.status === "settling" && attempt.settlingStatus !== providerStatus) return current;
			const outputSettlements = mergeOutputSettlements(attempt.outputSettlements, newSettlements);
			const capacityExceeded = exceedsBundleBytes(outputSettlements, this.limits.maxBundleBytes);
			return replaceAttempt(current, attemptId, {
				...clearAttemptOutcome(attempt),
				status: "failed",
				failure: capacityExceeded ? "capacity" : failure,
				outputSettlements,
				...(handle === undefined ? {} : { providerHandle: handle }),
				updatedAt: this.timestamp(),
			});
		});
		return view(changed);
	}

	private async recordCancellation(
		jobId: string,
		attemptId: string,
		cancellation: Exclude<MediaCancellation, "not_requested" | "requested">,
	): Promise<MediaJobView> {
		const changed = await this.store.modify(jobId, (current) => {
			const attempt = this.selectAttempt(current, attemptId);
			const status =
				cancellation === "confirmed" && !TERMINAL.has(attempt.status) && attempt.status !== "settling"
					? "cancelled"
					: attempt.status;
			if (attempt.cancellation === cancellation && attempt.status === status) return current;
			const base = status === "cancelled" ? clearAttemptOutcome(attempt) : attempt;
			return replaceAttempt(current, attemptId, {
				...base,
				status,
				cancellation,
				updatedAt: this.timestamp(),
			});
		});
		return view(changed);
	}

	private captureDraft(draft: Readonly<MediaJobDraft>): {
		readonly jobId: string;
		readonly request: MediaGenerationRequest;
	} {
		if (!draft || typeof draft !== "object") throw new MediaJobValidationError("invalid_job");
		const rawJobId = draft.jobId;
		const rawKind = draft.kind;
		const rawInput = draft.input;
		if (!includes(MEDIA_KINDS, rawKind)) throw new MediaJobValidationError("invalid_job");
		return Object.freeze({
			jobId: identifier(rawJobId, this.limits.maxIdentifierBytes),
			request: freezeRequest({
				kind: rawKind,
				input: normalizeJsonValueWithin(rawInput, {
					maxDepth: this.limits.maxJsonDepth,
					maxBytes: this.limits.maxRequestBytes,
				}),
			}),
		});
	}

	private captureObservation(value: unknown): CapturedProviderObservation {
		if (!value || typeof value !== "object") throw new MediaJobValidationError("invalid_provider_result");
		const candidate = value as Partial<MediaProviderObservation>;
		const handle = Object.hasOwn(candidate, "handle")
			? normalizeJsonValueWithin(candidate.handle, {
					maxDepth: this.limits.maxJsonDepth,
					maxBytes: this.limits.maxProviderHandleBytes,
				})
			: undefined;
		if (candidate.status === "pending") {
			if (handle === undefined) throw new MediaJobValidationError("invalid_provider_result");
			return Object.freeze({ status: "pending", handle });
		}
		if (candidate.status === "failed") {
			if (!includes(MEDIA_FAILURE_KINDS, candidate.failure)) {
				throw new MediaJobValidationError("invalid_provider_result");
			}
			return Object.freeze({
				status: "failed",
				failure: candidate.failure,
				...(handle === undefined ? {} : { handle }),
			});
		}
		if (candidate.status === "cancelled") {
			return Object.freeze({ status: "cancelled", ...(handle === undefined ? {} : { handle }) });
		}
		if (candidate.status !== "succeeded" && candidate.status !== "partially_succeeded") {
			throw new MediaJobValidationError("invalid_provider_result");
		}
		if (!Array.isArray(candidate.resources)) throw new MediaJobValidationError("invalid_provider_result");
		if (candidate.status === "partially_succeeded" && !Array.isArray(candidate.failures)) {
			throw new MediaJobValidationError("invalid_provider_result");
		}
		const rawFailures =
			candidate.status === "partially_succeeded" && Array.isArray(candidate.failures) ? candidate.failures : [];
		if (candidate.resources.length === 0 || (candidate.status === "partially_succeeded" && rawFailures.length === 0)) {
			throw new MediaJobValidationError("invalid_provider_result");
		}
		if (
			candidate.resources.length > this.limits.maxOutputsPerAttempt ||
			rawFailures.length > this.limits.maxOutputsPerAttempt - candidate.resources.length
		) {
			throw new MediaJobCapacityError("outputs");
		}
		const resources = candidate.resources
			.map((resource) => this.captureProviderResource(resource))
			.sort((left, right) => left.ordinal - right.ordinal);
		const failures =
			candidate.status === "partially_succeeded"
				? rawFailures
						.map((failure) => this.captureChildFailure(failure))
						.sort((left, right) => left.ordinal - right.ordinal)
				: [];
		const ordinals = new Set([
			...resources.map((resource) => resource.ordinal),
			...failures.map((failure) => failure.ordinal),
		]);
		if (ordinals.size !== resources.length + failures.length) {
			throw new MediaJobValidationError("invalid_provider_result");
		}
		return Object.freeze({
			status: candidate.status,
			resources: Object.freeze(resources),
			failures: Object.freeze(failures),
			...(handle === undefined ? {} : { handle }),
		});
	}

	private captureControl(value: Readonly<MediaJobControl>): Readonly<MediaJobControl> {
		const rawAttemptId = value.attemptId;
		const expectedRevision = value.expectedRevision;
		return {
			jobId: identifier(value.jobId, this.limits.maxIdentifierBytes),
			...(rawAttemptId === undefined ? {} : { attemptId: identifier(rawAttemptId, this.limits.maxIdentifierBytes) }),
			...(expectedRevision === undefined ? {} : { expectedRevision }),
		};
	}

	private captureRestart(value: Readonly<MediaJobRestart>): Readonly<MediaJobRestart> {
		const expectedRevision = value.expectedRevision;
		return {
			jobId: identifier(value.jobId, this.limits.maxIdentifierBytes),
			...(expectedRevision === undefined ? {} : { expectedRevision }),
		};
	}

	private captureResourceSelector(value: Readonly<GeneratedResourceSelector>): GeneratedResourceSelector {
		const ordinal = value.ordinal;
		if (!Number.isSafeInteger(ordinal) || ordinal < 0) throw new MediaJobValidationError("invalid_resource");
		if (ordinal >= this.limits.maxOutputsPerAttempt) throw new MediaJobCapacityError("outputs");
		const resourceId = value.resourceId;
		if (typeof resourceId !== "string" || !SHA256.test(resourceId)) {
			throw new MediaJobValidationError("invalid_resource");
		}
		return {
			jobId: identifier(value.jobId, this.limits.maxIdentifierBytes),
			attemptId: identifier(value.attemptId, this.limits.maxIdentifierBytes),
			bundleId: identifier(value.bundleId, this.limits.maxIdentifierBytes),
			ordinal,
			resourceId,
		};
	}

	private captureProviderResource(value: unknown): MediaProviderResource {
		if (!value || typeof value !== "object") throw new MediaJobValidationError("invalid_resource");
		const resource = value as Partial<MediaProviderResource>;
		const capturedBytes = captureMediaResourceBytes(resource.bytes);
		const declaredBytes = resource.declaredBytes ?? capturedBytes.knownByteLength;
		if (
			!Number.isSafeInteger(resource.ordinal) ||
			(resource.ordinal ?? -1) < 0 ||
			!includes(MEDIA_KINDS, resource.kind) ||
			typeof resource.mediaType !== "string" ||
			resource.mediaType.length === 0 ||
			(declaredBytes !== undefined &&
				capturedBytes.knownByteLength !== undefined &&
				declaredBytes !== capturedBytes.knownByteLength)
		) {
			throw new MediaJobValidationError("invalid_resource");
		}
		if ((resource.ordinal ?? this.limits.maxOutputsPerAttempt) >= this.limits.maxOutputsPerAttempt) {
			throw new MediaJobCapacityError("outputs");
		}
		if (Buffer.byteLength(resource.mediaType) > this.limits.maxMediaTypeBytes) {
			throw new MediaJobCapacityError("resource");
		}
		if (declaredBytes !== undefined && (!Number.isSafeInteger(declaredBytes) || declaredBytes < 0)) {
			throw new MediaJobValidationError("invalid_resource");
		}
		if (declaredBytes !== undefined && declaredBytes > this.limits.maxResourceBytes) {
			throw new MediaJobCapacityError("resource");
		}
		const ordinal = resource.ordinal;
		if (ordinal === undefined) throw new MediaJobValidationError("invalid_resource");
		return Object.freeze({
			ordinal,
			kind: resource.kind,
			mediaType: resource.mediaType,
			bytes: capturedBytes.bytes,
			...(declaredBytes === undefined ? {} : { declaredBytes }),
		});
	}

	private captureChildFailure(value: unknown): MediaChildFailure {
		if (!value || typeof value !== "object") throw new MediaJobValidationError("invalid_provider_result");
		const failure = value as Partial<MediaChildFailure>;
		if (
			!Number.isSafeInteger(failure.ordinal) ||
			(failure.ordinal ?? -1) < 0 ||
			!includes(MEDIA_FAILURE_KINDS, failure.failure)
		) {
			throw new MediaJobValidationError("invalid_provider_result");
		}
		if ((failure.ordinal ?? this.limits.maxOutputsPerAttempt) >= this.limits.maxOutputsPerAttempt) {
			throw new MediaJobCapacityError("outputs");
		}
		const ordinal = failure.ordinal;
		if (ordinal === undefined) throw new MediaJobValidationError("invalid_provider_result");
		return Object.freeze({ ordinal, failure: failure.failure });
	}

	private captureResourceReference(
		value: unknown,
		resource: Readonly<MediaProviderResource>,
		maxBytes: number,
	): GeneratedResourceRef {
		if (!value || typeof value !== "object") throw new MediaJobValidationError("invalid_resource");
		const reference = value as Partial<GeneratedResourceRef>;
		if (
			typeof reference.sha256 !== "string" ||
			!SHA256.test(reference.sha256) ||
			reference.resourceId !== reference.sha256 ||
			reference.kind !== resource.kind ||
			reference.mediaType !== resource.mediaType ||
			reference.ordinal !== resource.ordinal ||
			!Number.isSafeInteger(reference.byteLength) ||
			(reference.byteLength ?? -1) < 0 ||
			(resource.declaredBytes !== undefined && reference.byteLength !== resource.declaredBytes)
		) {
			throw new MediaJobValidationError("invalid_resource");
		}
		const byteLength = reference.byteLength;
		if (byteLength === undefined) throw new MediaJobValidationError("invalid_resource");
		if (byteLength > maxBytes) {
			throw new MediaJobCapacityError(maxBytes < this.limits.maxResourceBytes ? "bundle" : "resource");
		}
		return Object.freeze({
			resourceId: reference.resourceId,
			kind: reference.kind,
			mediaType: reference.mediaType,
			byteLength,
			sha256: reference.sha256,
			ordinal: reference.ordinal,
		});
	}

	private newAttempt(
		ordinal: number,
		recordedAt: string,
		attemptId = this.generatedId("attempt"),
		submissionKey = this.generatedId("submission"),
	): StoredMediaAttempt {
		return Object.freeze({
			attemptId,
			ordinal,
			submissionKey,
			status: "queued",
			disposition: "active",
			cancellation: "not_requested",
			outputSettlements: Object.freeze([]),
			startedAt: recordedAt,
			updatedAt: recordedAt,
		});
	}

	private generatedId(kind: "attempt" | "submission" | "bundle"): string {
		return identifier(this.newId(kind), this.limits.maxIdentifierBytes);
	}

	private timestamp(): string {
		const timestamp = new Date(this.now()).toISOString();
		assertCanonicalTimestamp(timestamp);
		return timestamp;
	}

	private operationContext(
		job: Readonly<StoredMediaJob>,
		attempt: Readonly<StoredMediaAttempt>,
		signal: AbortSignal,
	): MediaProviderOperationContext {
		return Object.freeze({
			jobId: job.jobId,
			attemptId: attempt.attemptId,
			submissionKey: attempt.submissionKey,
			signal,
		});
	}

	private async required(jobId: string): Promise<StoredMediaJob> {
		const job = await this.store.read(jobId);
		if (!job) throw new MediaJobNotFoundError();
		this.providerFor(job.adapterId);
		return job;
	}

	private selectAttempt(job: Readonly<StoredMediaJob>, attemptId?: string): StoredMediaAttempt {
		const selectedId =
			attemptId === undefined ? job.currentAttemptId : identifier(attemptId, this.limits.maxIdentifierBytes);
		const attempt = job.attempts.find((candidate) => candidate.attemptId === selectedId);
		if (!attempt) throw new MediaJobNotFoundError();
		return attempt;
	}

	private providerFor(adapterId: string): MediaGenerationProvider {
		const provider = this.adapters.get(adapterId);
		if (!provider) throw new MediaJobAdapterUnavailableError();
		return provider.provider;
	}

	private assertExpected(job: Readonly<StoredMediaJob>, expectedRevision?: string): void {
		if (expectedRevision !== undefined && job.revision !== expectedRevision) throw new MediaJobConflictError();
	}

	private assertRestartable(job: Readonly<StoredMediaJob>, expectedRevision?: string): void {
		this.providerFor(job.adapterId);
		this.assertExpected(job, expectedRevision);
		const active = this.selectAttempt(job, job.currentAttemptId);
		if (!TERMINAL.has(active.status) && active.disposition !== "abandoned") throw new MediaJobConflictError();
		if (job.attempts.length >= this.limits.maxAttemptsPerJob) throw new MediaJobCapacityError("attempts");
	}
}

function captureAdapters(
	value: Iterable<Readonly<MediaGenerationAdapter>>,
	limits: Readonly<MediaJobLimits>,
): ReadonlyMap<string, MediaGenerationAdapter> {
	if (value === null || (typeof value !== "object" && typeof value !== "function")) {
		throw new MediaJobValidationError("invalid_job");
	}
	const iterator = value[Symbol.iterator];
	if (typeof iterator !== "function") throw new MediaJobValidationError("invalid_job");
	const captured = new Map<string, MediaGenerationAdapter>();
	for (const rawRegistration of { [Symbol.iterator]: iterator.bind(value) }) {
		if (captured.size >= limits.maxAdapters) throw new MediaJobCapacityError("adapters");
		if (!rawRegistration || typeof rawRegistration !== "object") {
			throw new MediaJobValidationError("invalid_job");
		}
		const rawAdapterId = rawRegistration.adapterId;
		const rawProvider = rawRegistration.provider;
		const adapterId = identifier(rawAdapterId, limits.maxAdapterIdBytes);
		if (captured.has(adapterId)) throw new MediaJobValidationError("invalid_job");
		const admission = rawRegistration.admission;
		if (!(admission instanceof MediaCallAdmissionGroup)) throw new MediaJobValidationError("invalid_job");
		captured.set(adapterId, { adapterId, provider: captureProvider(rawProvider), admission });
	}
	if (captured.size === 0) throw new MediaJobValidationError("invalid_job");
	return captured;
}

function captureProvider(value: unknown): MediaGenerationProvider {
	if (!value || (typeof value !== "object" && typeof value !== "function")) {
		throw new MediaJobValidationError("invalid_job");
	}
	const provider = value as MediaGenerationProvider;
	const start = provider.start;
	const read = provider.read;
	const cancel = provider.cancel;
	if (
		typeof start !== "function" ||
		(read !== undefined && typeof read !== "function") ||
		(cancel !== undefined && typeof cancel !== "function")
	) {
		throw new MediaJobValidationError("invalid_job");
	}
	return Object.freeze({
		start: (request: Readonly<MediaGenerationRequest>, context: Readonly<MediaProviderOperationContext>) =>
			Reflect.apply(start, value, [request, context]),
		...(read === undefined
			? {}
			: {
					read: (handle: Readonly<JsonValue>, context: Readonly<MediaProviderOperationContext>) =>
						Reflect.apply(read, value, [handle, context]),
				}),
		...(cancel === undefined
			? {}
			: {
					cancel: (handle: Readonly<JsonValue>, context: Readonly<MediaProviderOperationContext>) =>
						Reflect.apply(cancel, value, [handle, context]),
				}),
	});
}

function identifier(value: unknown, maxBytes: number): string {
	if (typeof value !== "string") throw new MediaJobValidationError("invalid_job");
	assertOpaqueIdentifier(value);
	if (Buffer.byteLength(value) > maxBytes) throw new MediaJobCapacityError("identifier");
	return value;
}

function includes<const T extends readonly unknown[]>(values: T, value: unknown): value is T[number] {
	return values.includes(value);
}

function captureMediaResourceBytes(value: unknown): {
	readonly bytes: MediaResourceBytes;
	readonly knownByteLength?: number;
} {
	if (value instanceof Uint8Array) return { bytes: value };
	if (value === null || (typeof value !== "object" && typeof value !== "function")) {
		throw new MediaJobValidationError("invalid_resource");
	}
	const iterator = (value as AsyncIterable<Uint8Array>)[Symbol.asyncIterator];
	if (typeof iterator === "function") return { bytes: value as AsyncIterable<Uint8Array> };
	const candidate = value as { readonly byteLength?: unknown; readonly read?: unknown };
	const byteLength = candidate.byteLength;
	const read = candidate.read;
	if (!Number.isSafeInteger(byteLength) || (byteLength as number) < 0 || typeof read !== "function") {
		throw new MediaJobValidationError("invalid_resource");
	}
	return {
		bytes: Object.freeze({
			byteLength: byteLength as number,
			read: (signal: AbortSignal) => read.call(value, signal) as Uint8Array | Promise<Uint8Array>,
		}),
		knownByteLength: byteLength as number,
	};
}

function replaceAttempt(
	job: Readonly<StoredMediaJob>,
	attemptId: string,
	attempt: Readonly<StoredMediaAttempt>,
): StoredMediaJob {
	return {
		...job,
		attempts: job.attempts.map((candidate) => (candidate.attemptId === attemptId ? attempt : candidate)),
		updatedAt: attempt.updatedAt,
	};
}

function clearAttemptOutcome(attempt: Readonly<StoredMediaAttempt>): StoredMediaAttempt {
	return {
		attemptId: attempt.attemptId,
		ordinal: attempt.ordinal,
		submissionKey: attempt.submissionKey,
		status: attempt.status,
		disposition: attempt.disposition,
		cancellation: attempt.cancellation,
		startedAt: attempt.startedAt,
		updatedAt: attempt.updatedAt,
		outputSettlements: Object.freeze([]),
		...(attempt.providerHandle === undefined ? {} : { providerHandle: structuredClone(attempt.providerHandle) }),
	};
}

function freezeOutputSettlements(
	resources: readonly GeneratedResourceRef[],
	failures: readonly MediaChildFailure[],
): readonly StoredMediaOutputSettlement[] {
	return Object.freeze(
		[
			...resources.map((resource) =>
				Object.freeze({
					ordinal: resource.ordinal,
					status: "published" as const,
					resource: Object.freeze({ ...resource }),
				}),
			),
			...failures.map((failure) =>
				Object.freeze({ ordinal: failure.ordinal, status: "failed" as const, failure: failure.failure }),
			),
		].sort((left, right) => left.ordinal - right.ordinal),
	);
}

function mergeOutputSettlements(
	existing: readonly StoredMediaOutputSettlement[],
	incoming: readonly StoredMediaOutputSettlement[],
): readonly StoredMediaOutputSettlement[] {
	const merged = new Map(existing.map((settlement) => [settlement.ordinal, settlement]));
	for (const settlement of incoming) {
		const prior = merged.get(settlement.ordinal);
		if (prior && !sameOutputSettlement(prior, settlement)) throw new MediaJobConflictError();
		if (!prior) merged.set(settlement.ordinal, settlement);
	}
	return Object.freeze([...merged.values()].sort((left, right) => left.ordinal - right.ordinal));
}

function sameOutputSettlement(
	left: Readonly<StoredMediaOutputSettlement>,
	right: Readonly<StoredMediaOutputSettlement>,
): boolean {
	if (left.status !== right.status || left.ordinal !== right.ordinal) return false;
	if (left.status === "failed" && right.status === "failed") return left.failure === right.failure;
	if (left.status !== "published" || right.status !== "published") return false;
	return (
		left.resource.resourceId === right.resource.resourceId &&
		left.resource.kind === right.resource.kind &&
		left.resource.mediaType === right.resource.mediaType &&
		left.resource.byteLength === right.resource.byteLength &&
		left.resource.sha256 === right.resource.sha256 &&
		left.resource.ordinal === right.resource.ordinal
	);
}

function exceedsBundleBytes(settlements: readonly StoredMediaOutputSettlement[], maxBytes: number): boolean {
	let total = 0;
	for (const settlement of settlements) {
		if (settlement.status !== "published") continue;
		if (settlement.resource.byteLength > maxBytes - total) return true;
		total += settlement.resource.byteLength;
	}
	return false;
}

function bundleDisposition(
	status: CapturedProviderObservation["status"],
	disposition: MediaJobDisposition,
): ResultBundle["disposition"] {
	if (disposition === "abandoned") return "late";
	return status === "partially_succeeded" ? "partial" : "complete";
}

function freezeRequest(request: Readonly<MediaGenerationRequest>): MediaGenerationRequest {
	return Object.freeze({ kind: request.kind, input: structuredClone(request.input) });
}

function view(job: Readonly<StoredMediaJob>): MediaJobView {
	const attempts = Object.freeze(job.attempts.map((attempt) => freezeAttemptView(attempt)));
	const current = attempts.find((attempt) => attempt.attemptId === job.currentAttemptId);
	if (!current) throw new MediaJobValidationError("invalid_job");
	return Object.freeze({
		jobId: job.jobId,
		kind: job.request.kind,
		revision: job.revision,
		createdAt: job.createdAt,
		updatedAt: job.updatedAt,
		currentAttemptId: current.attemptId,
		status: current.status,
		disposition: current.disposition,
		cancellation: current.cancellation,
		...(current.bundleId === undefined ? {} : { bundleId: current.bundleId }),
		...(current.failure === undefined ? {} : { failure: current.failure }),
		attempts,
	});
}

function freezeAttemptView(attempt: Readonly<StoredMediaAttempt>): MediaAttemptView {
	return Object.freeze({
		attemptId: attempt.attemptId,
		ordinal: attempt.ordinal,
		status: attempt.status,
		disposition: attempt.disposition,
		cancellation: attempt.cancellation,
		startedAt: attempt.startedAt,
		updatedAt: attempt.updatedAt,
		...(attempt.bundleId === undefined ? {} : { bundleId: attempt.bundleId }),
		...(attempt.failure === undefined ? {} : { failure: attempt.failure }),
	});
}

function freezeBundle(bundle: Readonly<ResultBundle>): ResultBundle {
	return Object.freeze({
		bundleId: bundle.bundleId,
		jobId: bundle.jobId,
		attemptId: bundle.attemptId,
		disposition: bundle.disposition,
		resources: Object.freeze(bundle.resources.map((resource) => Object.freeze({ ...resource }))),
		failures: Object.freeze(bundle.failures.map((failure) => Object.freeze({ ...failure }))),
	});
}

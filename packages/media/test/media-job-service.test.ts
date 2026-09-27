import { createHash } from "node:crypto";
import { describe, expect, it, vi } from "vitest";
import {
	InMemoryMediaJobStore,
	MediaCallAdmissionGroup,
	type MediaGenerationProvider,
	MediaJobAdapterUnavailableError,
	MediaJobCapacityError,
	MediaJobConflictError,
	type MediaJobLimits,
	MediaJobService,
	type MediaJobStore,
	MediaJobValidationError,
	type MediaProviderCancelObservation,
	type MediaProviderObservation,
	type MediaProviderResource,
	type MediaResourcePublisher,
} from "../src/index.js";

const LIMITS: MediaJobLimits = {
	maxIdentifierBytes: 128,
	maxAdapterIdBytes: 64,
	maxAdapters: 4,
	maxRequestBytes: 1_024,
	maxJsonDepth: 8,
	maxProviderHandleBytes: 512,
	maxAttemptsPerJob: 4,
	maxOutputsPerAttempt: 4,
	maxResourceBytes: 1_024,
	maxBundleBytes: 2_048,
	maxManifestBytes: 2_048,
	maxMediaTypeBytes: 128,
};

const image = (bytes = Uint8Array.of(1, 2, 3), ordinal = 0): MediaProviderResource => ({
	ordinal,
	kind: "image",
	mediaType: "image/png",
	bytes,
});

function publisher(): MediaResourcePublisher {
	return {
		publish: vi.fn(async (resource) => {
			if (!(resource.bytes instanceof Uint8Array)) throw new Error("The test publisher requires eager bytes.");
			const sha256 = createHash("sha256").update(resource.bytes).digest("hex");
			return {
				resourceId: sha256,
				kind: resource.kind,
				mediaType: resource.mediaType,
				byteLength: resource.bytes.byteLength,
				sha256,
				ordinal: resource.ordinal,
			};
		}),
	};
}

function idFactory() {
	const counts = { attempt: 0, submission: 0, bundle: 0 };
	return (kind: keyof typeof counts) => `${kind}-${++counts[kind]}`;
}

function testAdmission() {
	return new MediaCallAdmissionGroup({ maxConcurrentCalls: 4, maxQueuedCalls: 4 });
}

function harness(
	provider: MediaGenerationProvider,
	resources = publisher(),
	limits: MediaJobLimits = LIMITS,
	store: MediaJobStore = new InMemoryMediaJobStore(limits),
) {
	let now = 1_800_000_000_000;
	return {
		store,
		resources,
		service: new MediaJobService({
			adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider }],
			store,
			resources,
			limits,
			newId: idFactory(),
			now: () => now++,
		}),
	};
}

describe("MediaJobService", () => {
	it("routes each host-selected submission through its registered durable adapter", async () => {
		const horizontal: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { profile: "horizontal" } })),
		};
		const vertical: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { profile: "vertical" } })),
		};
		const limits = { ...LIMITS, maxAdapters: 2 };
		const store = new InMemoryMediaJobStore(limits);
		const service = new MediaJobService({
			adapters: [
				{ adapterId: "ark-horizontal", admission: testAdmission(), provider: horizontal },
				{ adapterId: "ark-vertical", admission: testAdmission(), provider: vertical },
			],
			store,
			resources: publisher(),
			limits,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		});

		await service.submit("ark-horizontal", { jobId: "job-h", kind: "image", input: { prompt: "h" } });
		await service.submit("ark-vertical", { jobId: "job-v", kind: "image", input: { prompt: "v" } });

		expect(horizontal.start).toHaveBeenCalledOnce();
		expect(horizontal.start).toHaveBeenCalledWith(
			{ kind: "image", input: { prompt: "h" } },
			expect.objectContaining({ jobId: "job-h" }),
		);
		expect(vertical.start).toHaveBeenCalledOnce();
		expect(vertical.start).toHaveBeenCalledWith(
			{ kind: "image", input: { prompt: "v" } },
			expect.objectContaining({ jobId: "job-v" }),
		);
	});

	it("requires an explicit host adapter selection even when exactly one adapter is registered", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { profile: "only" } })),
		};
		const service = new MediaJobService({
			adapters: [{ adapterId: "only-adapter", admission: testAdmission(), provider }],
			store: new InMemoryMediaJobStore(LIMITS),
			resources: publisher(),
			limits: LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		});

		await expect(
			Reflect.apply(service.submit, service, [{ jobId: "implicit", kind: "image", input: {} }]),
		).rejects.toBeInstanceOf(MediaJobAdapterUnavailableError);
		expect(provider.start).not.toHaveBeenCalled();
	});

	it("fails closed when a multi-adapter submission omits or names an unavailable adapter", async () => {
		const horizontal: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { profile: "horizontal" } })),
		};
		const vertical: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { profile: "vertical" } })),
		};
		const store = new InMemoryMediaJobStore(LIMITS);
		const service = new MediaJobService({
			adapters: [
				{ adapterId: "ark-horizontal", admission: testAdmission(), provider: horizontal },
				{ adapterId: "ark-vertical", admission: testAdmission(), provider: vertical },
			],
			store,
			resources: publisher(),
			limits: LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		});

		await expect(
			Reflect.apply(service.submit, service, [{ jobId: "implicit", kind: "image", input: {} }]),
		).rejects.toBeInstanceOf(MediaJobAdapterUnavailableError);
		await expect(service.submit("missing", { jobId: "unknown", kind: "image", input: {} })).rejects.toBeInstanceOf(
			MediaJobAdapterUnavailableError,
		);
		await expect(service.read("implicit")).resolves.toBeUndefined();
		await expect(service.read("unknown")).resolves.toBeUndefined();
		expect(horizontal.start).not.toHaveBeenCalled();
		expect(vertical.start).not.toHaveBeenCalled();
	});

	it("keeps job identity authority-wide so a duplicate cannot switch adapters", async () => {
		const horizontal: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { profile: "horizontal" } })),
		};
		const vertical: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { profile: "vertical" } })),
		};
		const service = new MediaJobService({
			adapters: [
				{ adapterId: "ark-horizontal", admission: testAdmission(), provider: horizontal },
				{ adapterId: "ark-vertical", admission: testAdmission(), provider: vertical },
			],
			store: new InMemoryMediaJobStore(LIMITS),
			resources: publisher(),
			limits: LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		});

		await service.submit("ark-horizontal", { jobId: "shared-job", kind: "image", input: {} });
		await expect(
			service.submit("ark-vertical", { jobId: "shared-job", kind: "image", input: {} }),
		).rejects.toBeInstanceOf(MediaJobConflictError);
		expect(vertical.start).not.toHaveBeenCalled();
	});

	it("requires a non-empty bounded registry with unique adapter identities", () => {
		const provider: MediaGenerationProvider = { start: vi.fn() };
		const base = {
			store: new InMemoryMediaJobStore(LIMITS),
			resources: publisher(),
			limits: LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		};
		expect(() => new MediaJobService({ ...base, adapters: [] })).toThrow(MediaJobValidationError);
		expect(
			() =>
				new MediaJobService({
					...base,
					adapters: [
						{ adapterId: "duplicate", admission: testAdmission(), provider },
						{ adapterId: "duplicate", admission: testAdmission(), provider },
					],
				}),
		).toThrow(MediaJobValidationError);
		expect(
			() =>
				new MediaJobService({
					...base,
					limits: { ...LIMITS, maxAdapters: 1 },
					adapters: [
						{ adapterId: "one", admission: testAdmission(), provider },
						{ adapterId: "two", admission: testAdmission(), provider },
					],
				}),
		).toThrow(MediaJobCapacityError);
		expect(
			() =>
				new MediaJobService({
					...base,
					limits: { ...LIMITS, maxAdapters: undefined } as unknown as MediaJobLimits,
					adapters: [{ adapterId: "one", admission: testAdmission(), provider }],
				}),
		).toThrow(RangeError);
	});

	it("captures host options, adapter registrations, provider methods, and draft fields exactly once", async () => {
		const once = <T>(value: T) => {
			let reads = 0;
			return () => {
				reads += 1;
				if (reads !== 1) throw new Error("accessor read more than once");
				return value;
			};
		};
		const start = vi.fn(async () => ({ status: "pending" as const, handle: { captured: true } }));
		const provider = Object.defineProperties(
			{},
			{
				start: { enumerable: true, get: once(start) },
				read: { enumerable: true, get: once(undefined) },
				cancel: { enumerable: true, get: once(undefined) },
			},
		) as MediaGenerationProvider;
		const registration = Object.defineProperties(
			{},
			{
				adapterId: { enumerable: true, get: once("captured-adapter") },
				provider: { enumerable: true, get: once(provider) },
				admission: { enumerable: true, get: once(testAdmission()) },
			},
		) as { readonly adapterId: string; readonly provider: MediaGenerationProvider };
		const store = new InMemoryMediaJobStore(LIMITS);
		const resources = publisher();
		const newId = idFactory();
		const now = () => 1_800_000_000_000;
		const options = Object.defineProperties(
			{},
			{
				adapters: { enumerable: true, get: once([registration]) },
				store: { enumerable: true, get: once(store) },
				resources: { enumerable: true, get: once(resources) },
				limits: { enumerable: true, get: once(LIMITS) },
				newId: { enumerable: true, get: once(newId) },
				now: { enumerable: true, get: once(now) },
			},
		) as ConstructorParameters<typeof MediaJobService>[0];
		const service = new MediaJobService(options);
		const draft = Object.defineProperties(
			{},
			{
				jobId: { enumerable: true, get: once("captured-job") },
				kind: { enumerable: true, get: once("image" as const) },
				input: { enumerable: true, get: once({ prompt: "captured" }) },
			},
		) as { readonly jobId: string; readonly kind: "image"; readonly input: { readonly prompt: string } };

		await expect(service.submit("captured-adapter", draft)).resolves.toMatchObject({ status: "pending" });
		expect(start).toHaveBeenCalledOnce();
	});

	it("persists the attempt before provider submission and publishes a safe immutable bundle", async () => {
		const store = new InMemoryMediaJobStore(LIMITS);
		const resources = publisher();
		let persistedBeforeStart = false;
		const provider: MediaGenerationProvider = {
			start: vi.fn(async (_request, context) => {
				const stored = await store.read("job-1");
				persistedBeforeStart =
					stored?.attempts[0]?.status === "submitting" &&
					stored.attempts[0].attemptId === context.attemptId &&
					stored.attempts[0].submissionKey === context.submissionKey;
				return { status: "succeeded" as const, resources: [image()] };
			}),
		};
		const service = new MediaJobService({
			adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider }],
			store,
			resources,
			limits: LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		});

		const view = await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: { prompt: "tree" } });

		expect(persistedBeforeStart).toBe(true);
		expect(view).toMatchObject({
			jobId: "job-1",
			revision: "3",
			status: "succeeded",
			disposition: "active",
			currentAttemptId: "attempt-1",
			bundleId: "bundle-1",
		});
		expect(view).not.toHaveProperty("request");
		expect(view).not.toHaveProperty("providerHandle");
		expect(view).not.toHaveProperty("submissionKey");
		const bundle = await service.readBundle("job-1", "bundle-1");
		expect(bundle).toMatchObject({
			bundleId: "bundle-1",
			disposition: "complete",
			resources: [{ ordinal: 0, byteLength: 3 }],
			failures: [],
		});
		expect(Object.isFrozen(bundle)).toBe(true);
		const stored = await store.read("job-1");
		expect(stored?.attempts[0]?.outputSettlements).toEqual([
			expect.objectContaining({ ordinal: 0, status: "published", resource: bundle?.resources[0] }),
		]);
	});

	it("admits a bounded lazy media reader and preflights its exact length before one publication read", async () => {
		const expected = Uint8Array.of(4, 5, 6);
		const read = vi.fn((signal: AbortSignal) => {
			signal.throwIfAborted();
			return expected;
		});
		const bytes = { byteLength: expected.byteLength, read };
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "succeeded" as const,
				resources: [{ ...image(expected), bytes }],
			})),
		};
		const resources: MediaResourcePublisher = {
			publish: vi.fn(async (resource, context) => {
				expect(resource.bytes).not.toBe(bytes);
				expect(resource.declaredBytes).toBe(expected.byteLength);
				expect(context.maxBytes).toBe(expected.byteLength);
				if (!("read" in resource.bytes)) throw new Error("Expected captured lazy reader.");
				const published = await resource.bytes.read(context.signal);
				const sha256 = createHash("sha256").update(published).digest("hex");
				return {
					resourceId: sha256,
					kind: resource.kind,
					mediaType: resource.mediaType,
					byteLength: published.byteLength,
					sha256,
					ordinal: resource.ordinal,
				};
			}),
		};
		const { service } = harness(provider, resources, {
			...LIMITS,
			maxResourceBytes: expected.byteLength,
			maxBundleBytes: expected.byteLength,
		});

		const result = await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });

		expect(result).toMatchObject({ status: "succeeded", bundleId: "bundle-1" });
		expect(resources.publish).toHaveBeenCalledOnce();
		expect(read).toHaveBeenCalledOnce();
	});

	it("selects an exact immutable resource lineage when identical bytes occupy different output slots", async () => {
		const bytes = Uint8Array.of(7, 8, 9);
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "succeeded" as const,
				resources: [image(bytes, 0), image(bytes, 1)],
			})),
		};
		const { service } = harness(provider);
		await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });
		const resourceId = createHash("sha256").update(bytes).digest("hex");

		const first = await service.selectResource({
			jobId: "job-1",
			attemptId: "attempt-1",
			bundleId: "bundle-1",
			ordinal: 0,
			resourceId,
		});
		const second = await service.selectResource({
			jobId: "job-1",
			attemptId: "attempt-1",
			bundleId: "bundle-1",
			ordinal: 1,
			resourceId,
		});

		expect(first).toEqual({
			jobId: "job-1",
			attemptId: "attempt-1",
			bundleId: "bundle-1",
			bundleDisposition: "complete",
			resource: expect.objectContaining({ ordinal: 0, resourceId, sha256: resourceId }),
		});
		expect(second).toEqual({
			jobId: "job-1",
			attemptId: "attempt-1",
			bundleId: "bundle-1",
			bundleDisposition: "complete",
			resource: expect.objectContaining({ ordinal: 1, resourceId, sha256: resourceId }),
		});
		expect(Object.isFrozen(first)).toBe(true);
		expect(Object.isFrozen(first?.resource)).toBe(true);
	});

	it("returns no selection for a mismatched, missing, failed, or late output", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "partially_succeeded" as const,
				resources: [image(Uint8Array.of(9), 1)],
				failures: [{ ordinal: 0, failure: "provider" as const }],
			})),
		};
		const { service } = harness(provider);
		await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });
		const resourceId = createHash("sha256").update(Uint8Array.of(9)).digest("hex");
		const selection = {
			jobId: "job-1",
			attemptId: "attempt-1",
			bundleId: "bundle-1",
			ordinal: 1,
			resourceId,
		} as const;

		await expect(service.selectResource(selection)).resolves.toMatchObject({ bundleDisposition: "partial" });
		await expect(service.selectResource({ ...selection, ordinal: 0 })).resolves.toBeUndefined();
		await expect(service.selectResource({ ...selection, attemptId: "attempt-2" })).resolves.toBeUndefined();
		await expect(service.selectResource({ ...selection, bundleId: "bundle-2" })).resolves.toBeUndefined();
		await expect(service.selectResource({ ...selection, jobId: "job-2" })).resolves.toBeUndefined();
		await expect(service.selectResource({ ...selection, resourceId: "0".repeat(64) })).resolves.toBeUndefined();

		const lateProvider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { id: "provider" } })),
			cancel: vi.fn(async () => ({
				status: "settled" as const,
				observation: { status: "succeeded" as const, resources: [image()] },
			})),
		};
		const late = harness(lateProvider).service;
		const pending = await late.submit("selected-adapter", { jobId: "late-job", kind: "image", input: {} });
		const completed = await late.cancel({ jobId: "late-job", expectedRevision: pending.revision });
		const bundle = await late.readBundle("late-job", completed.bundleId ?? "missing");
		const resource = bundle?.resources[0];
		if (!bundle || !resource) throw new Error("Expected a retained late bundle fixture.");
		await expect(
			late.selectResource({
				jobId: bundle.jobId,
				attemptId: bundle.attemptId,
				bundleId: bundle.bundleId,
				ordinal: resource.ordinal,
				resourceId: resource.resourceId,
			}),
		).resolves.toBeUndefined();
	});

	it("captures a resource selector before Store I/O and preserves caller cancellation", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
		};
		const { service, store } = harness(provider);
		await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });
		const bundle = await service.readBundle("job-1", "bundle-1");
		const resource = bundle?.resources[0];
		if (!bundle || !resource) throw new Error("Expected a selectable resource fixture.");
		const selector = {
			jobId: bundle.jobId,
			attemptId: bundle.attemptId,
			bundleId: bundle.bundleId,
			ordinal: resource.ordinal,
			resourceId: resource.resourceId,
		};
		const selecting = service.selectResource(selector);
		selector.jobId = "job-2";
		selector.attemptId = "attempt-2";
		selector.bundleId = "bundle-2";
		selector.ordinal = 1;
		selector.resourceId = "0".repeat(64);
		await expect(selecting).resolves.toMatchObject({ resource: { ordinal: 0 } });

		const read = vi.spyOn(store, "read");
		read.mockClear();
		const controller = new AbortController();
		const reason = new Error("selection stopped");
		controller.abort(reason);
		await expect(service.selectResource(selector, controller.signal)).rejects.toBe(reason);
		expect(read).not.toHaveBeenCalled();
	});

	it("preserves cancellation that arrives while reading a resource selection snapshot", async () => {
		const backing = new InMemoryMediaJobStore(LIMITS);
		const controlled = controllableStore(backing);
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
		};
		const { service } = harness(provider, publisher(), LIMITS, controlled.store);
		await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });
		const bundle = await service.readBundle("job-1", "bundle-1");
		const resource = bundle?.resources[0];
		if (!bundle || !resource) throw new Error("Expected a selectable resource fixture.");
		const paused = controlled.pauseNextRead();
		const controller = new AbortController();
		const reason = new Error("selection read stopped");
		const selection = service.selectResource(
			{
				jobId: bundle.jobId,
				attemptId: bundle.attemptId,
				bundleId: bundle.bundleId,
				ordinal: resource.ordinal,
				resourceId: resource.resourceId,
			},
			controller.signal,
		);
		await paused.entered;
		controller.abort(reason);
		paused.release();

		await expect(selection).rejects.toBe(reason);
	});

	it.each([
		[{ ordinal: -1 }, MediaJobValidationError],
		[{ ordinal: 0.5 }, MediaJobValidationError],
		[{ ordinal: LIMITS.maxOutputsPerAttempt }, MediaJobCapacityError],
		[{ resourceId: "not-a-digest" }, MediaJobValidationError],
	] as const)("rejects malformed resource selection input before Store I/O", async (change, ErrorType) => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
		};
		const { service, store } = harness(provider);
		await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });
		const bundle = await service.readBundle("job-1", "bundle-1");
		const resource = bundle?.resources[0];
		if (!bundle || !resource) throw new Error("Expected a selectable resource fixture.");
		const read = vi.spyOn(store, "read");
		read.mockClear();

		await expect(
			service.selectResource({
				jobId: bundle.jobId,
				attemptId: bundle.attemptId,
				bundleId: bundle.bundleId,
				ordinal: resource.ordinal,
				resourceId: resource.resourceId,
				...change,
			}),
		).rejects.toBeInstanceOf(ErrorType);
		expect(read).not.toHaveBeenCalled();
	});

	it("rejects a Store missing its read port before any job can be written", () => {
		const create = vi.fn();
		expect(
			() =>
				new MediaJobService({
					adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider: { start: vi.fn() } }],
					store: { create, modify: vi.fn() } as unknown as MediaJobStore,
					resources: publisher(),
					limits: LIMITS,
					newId: idFactory(),
					now: () => 1_800_000_000_000,
				}),
		).toThrow(MediaJobValidationError);
		expect(create).not.toHaveBeenCalled();
	});

	it("rejects a missing mandatory host limit during construction", () => {
		expect(
			() =>
				new MediaJobService({
					adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider: { start: vi.fn() } }],
					store: new InMemoryMediaJobStore(LIMITS),
					resources: publisher(),
					limits: { ...LIMITS, maxBundleBytes: undefined } as unknown as MediaJobLimits,
					newId: idFactory(),
					now: () => 1_800_000_000_000,
				}),
		).toThrow(RangeError);
	});

	it("never resubmits a duplicate local job identity", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
		};
		const { service } = harness(provider);
		await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: { prompt: "orbit" } });

		await expect(
			service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: { prompt: "orbit" } }),
		).rejects.toBeInstanceOf(MediaJobConflictError);
		expect(provider.start).toHaveBeenCalledTimes(1);
	});

	it.each(["attempt", "submission"] as const)(
		"rejects a reused generated %s identity on restart",
		async (reusedKind) => {
			const counts = { attempt: 0, submission: 0, bundle: 0 };
			const provider: MediaGenerationProvider = {
				start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			};
			const store = new InMemoryMediaJobStore(LIMITS);
			const service = new MediaJobService({
				adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider }],
				store,
				resources: publisher(),
				limits: LIMITS,
				newId: (kind) => (kind === reusedKind ? `reused-${kind}` : `${kind}-${++counts[kind]}`),
				now: () => 1_800_000_000_000,
			});
			const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
			const abandoned = await service.cancel({ jobId: "job-1", expectedRevision: pending.revision });

			await expect(service.restart({ jobId: "job-1", expectedRevision: abandoned.revision })).rejects.toBeInstanceOf(
				MediaJobConflictError,
			);
			expect(provider.start).toHaveBeenCalledTimes(1);
		},
	);

	it("rejects a reused generated bundle identity without replacing the first manifest", async () => {
		const counts = { attempt: 0, submission: 0 };
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
		};
		const store = new InMemoryMediaJobStore(LIMITS);
		const service = new MediaJobService({
			adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider }],
			store,
			resources: publisher(),
			limits: LIMITS,
			newId: (kind) => (kind === "bundle" ? "reused-bundle" : `${kind}-${++counts[kind]}`),
			now: () => 1_800_000_000_000,
		});
		const first = await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });

		await expect(service.restart({ jobId: "job-1", expectedRevision: first.revision })).rejects.toBeInstanceOf(
			MediaJobConflictError,
		);
		await expect(service.readBundle("job-1", "reused-bundle")).resolves.toMatchObject({ attemptId: "attempt-1" });
		await expect(service.read("job-1")).resolves.toMatchObject({ status: "submitting", currentAttemptId: "attempt-2" });
	});

	it("keeps read local and performs exactly one provider observation per reconcile", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			read: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", {
			jobId: "job-1",
			kind: "video",
			input: { prompt: "orbit" },
		});

		await expect(service.read("job-1")).resolves.toEqual(pending);
		expect(provider.read).not.toHaveBeenCalled();
		const settled = await service.reconcile({ jobId: "job-1", expectedRevision: pending.revision });
		expect(settled.status).toBe("succeeded");
		expect(provider.read).toHaveBeenCalledTimes(1);
	});

	it("captures reconcile control before awaiting the Store snapshot", async () => {
		const backing = new InMemoryMediaJobStore(LIMITS);
		const controlled = controllableStore(backing);
		const provider: MediaGenerationProvider = {
			start: vi.fn(async (_request, context) => ({
				status: "pending" as const,
				handle: { attemptId: context.attemptId },
			})),
			read: vi.fn(async () => ({ status: "pending" as const, handle: { attemptId: "attempt-1" } })),
		};
		const { service } = harness(provider, publisher(), LIMITS, controlled.store);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const paused = controlled.pauseNextRead();
		const control = {
			jobId: "job-1",
			attemptId: "attempt-1",
			expectedRevision: pending.revision,
		};
		const reconciliation = service.reconcile(control);
		await paused.entered;
		control.attemptId = "attempt-2";
		control.expectedRevision = "stale";
		paused.release();

		await expect(reconciliation).resolves.toMatchObject({ currentAttemptId: "attempt-1", status: "pending" });
		expect(provider.read).toHaveBeenCalledWith(
			{ attemptId: "attempt-1" },
			expect.objectContaining({ attemptId: "attempt-1" }),
		);
	});

	it("preserves caller cancellation before validating a reconciled Store snapshot", async () => {
		const backing = new InMemoryMediaJobStore(LIMITS);
		const controlled = controllableStore(backing);
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			read: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
		};
		const { service } = harness(provider, publisher(), LIMITS, controlled.store);
		await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const paused = controlled.pauseNextRead();
		const controller = new AbortController();
		const reason = new Error("reconcile cancelled");
		const reconciliation = service.reconcile(
			{ jobId: "job-1", attemptId: "attempt-missing", expectedRevision: "stale" },
			controller.signal,
		);
		await paused.entered;
		controller.abort(reason);
		paused.release();

		await expect(reconciliation).rejects.toBe(reason);
		expect(provider.read).not.toHaveBeenCalled();
	});

	it("reconciles a pending attempt after constructing a fresh service over the same Store", async () => {
		const store = new InMemoryMediaJobStore(LIMITS);
		const firstProvider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "durable" } })),
		};
		const first = harness(firstProvider, publisher(), LIMITS, store).service;
		const pending = await first.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const restoredProvider: MediaGenerationProvider = {
			start: vi.fn(async () => {
				throw new Error("restore must not submit again");
			}),
			read: vi.fn(async (handle) => {
				expect(handle).toEqual({ operation: "durable" });
				return { status: "succeeded" as const, resources: [image()] };
			}),
		};
		const restored = harness(restoredProvider, publisher(), LIMITS, store).service;

		const completed = await restored.reconcile({ jobId: "job-1", expectedRevision: pending.revision });
		expect(completed).toMatchObject({ status: "succeeded", bundleId: "bundle-1" });
		expect(restoredProvider.start).not.toHaveBeenCalled();
		expect(restoredProvider.read).toHaveBeenCalledTimes(1);
	});

	it("rejects a stale revision before reading the provider", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			read: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
		};
		const { service } = harness(provider);
		await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });

		await expect(service.reconcile({ jobId: "job-1", expectedRevision: "1" })).rejects.toBeInstanceOf(
			MediaJobConflictError,
		);
		expect(provider.read).not.toHaveBeenCalled();
	});

	it.each([null, false, 0, ""])("preserves the valid falsy JSON provider handle %j", async (handle) => {
		const read = vi.fn<NonNullable<MediaGenerationProvider["read"]>>(async () => ({
			status: "succeeded",
			resources: [image()],
		}));
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle })),
			read,
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });

		await expect(service.reconcile({ jobId: "job-1", expectedRevision: pending.revision })).resolves.toMatchObject({
			status: "succeeded",
		});
		expect(read).toHaveBeenCalledWith(handle, expect.objectContaining({ jobId: "job-1" }));
	});

	it("records an ambiguous submit once and requires an explicit abandoned restart", async () => {
		const start = vi
			.fn<MediaGenerationProvider["start"]>()
			.mockRejectedValueOnce(new Error("socket closed"))
			.mockResolvedValueOnce({ status: "pending", handle: { operation: "second" } });
		const provider: MediaGenerationProvider = { start };
		const { service } = harness(provider);
		const unknown = await service.submit("selected-adapter", {
			jobId: "job-1",
			kind: "image",
			input: { prompt: "tree" },
		});

		expect(unknown).toMatchObject({ status: "outcome_unknown", disposition: "active" });
		await expect(service.restart({ jobId: "job-1", expectedRevision: unknown.revision })).rejects.toBeInstanceOf(
			MediaJobConflictError,
		);
		const abandoned = await service.cancel({ jobId: "job-1", expectedRevision: unknown.revision });
		const restarted = await service.restart({ jobId: "job-1", expectedRevision: abandoned.revision });
		expect(restarted).toMatchObject({ status: "pending", currentAttemptId: "attempt-2" });
		expect(restarted.attempts).toHaveLength(2);
		expect(restarted.attempts[0]).toMatchObject({ attemptId: "attempt-1", status: "outcome_unknown" });
		expect(start).toHaveBeenCalledTimes(2);
		expect(start.mock.calls[0]?.[1].submissionKey).not.toBe(start.mock.calls[1]?.[1].submissionKey);
	});

	it("reconciles an interrupted submitting attempt to outcome unknown after recovery", async () => {
		let rejectStart: ((reason: unknown) => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn(
				() =>
					new Promise<MediaProviderObservation>((_resolve, reject) => {
						rejectStart = reject;
					}),
			),
		};
		const { service: first, store } = harness(provider);
		const submission = first.submit("selected-adapter", { jobId: "job-1", kind: "audio", input: { text: "line" } });
		await vi.waitFor(() => expect(provider.start).toHaveBeenCalledOnce());
		const recovered = new MediaJobService({
			adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider }],
			store,
			resources: publisher(),
			limits: LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_001,
		});

		await expect(recovered.reconcile({ jobId: "job-1", expectedRevision: "2" })).resolves.toMatchObject({
			status: "outcome_unknown",
			failure: "unknown",
			disposition: "active",
		});
		rejectStart?.(new Error("old process ended"));
		await expect(submission).resolves.toMatchObject({ status: "outcome_unknown" });
	});

	it("does not replace a durable handle that wins the interrupted-submission recovery race", async () => {
		const backing = new InMemoryMediaJobStore(LIMITS);
		let promoteBeforeModify = false;
		let resolveStart: ((observation: MediaProviderObservation) => void) | undefined;
		const store: MediaJobStore = {
			create: (job) => backing.create(job),
			purge: (request) => backing.purge(request),
			listQueued: (request) => backing.listQueued(request),
			read: (jobId) => backing.read(jobId),
			modify: async (jobId, change) => {
				if (promoteBeforeModify) {
					promoteBeforeModify = false;
					await backing.modify(jobId, (current) => {
						const attempt = current.attempts[0];
						if (!attempt) throw new Error("expected submitting attempt");
						return {
							...current,
							attempts: [{ ...attempt, status: "pending", providerHandle: { ticket: "durable" } }],
						};
					});
				}
				return backing.modify(jobId, change);
			},
		};
		const provider: MediaGenerationProvider = {
			start: vi.fn(
				() =>
					new Promise<MediaProviderObservation>((resolve) => {
						resolveStart = resolve;
					}),
			),
		};
		const first = new MediaJobService({
			adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider }],
			store,
			resources: publisher(),
			limits: LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		});
		const submission = first.submit("selected-adapter", { jobId: "job-1", kind: "audio", input: {} });
		await vi.waitFor(() => expect(provider.start).toHaveBeenCalledOnce());
		const recovered = new MediaJobService({
			adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider }],
			store,
			resources: publisher(),
			limits: LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_001,
		});
		promoteBeforeModify = true;

		const pending = await recovered.reconcile({ jobId: "job-1", expectedRevision: "2" });
		expect(pending).toMatchObject({ status: "pending" });
		expect(pending).not.toHaveProperty("failure");
		resolveStart?.({ status: "pending", handle: { ticket: "durable" } });
		await expect(submission).resolves.toMatchObject({ status: "pending" });
	});

	it("persists abandonment before one cancel call and does not invent provider cancellation", async () => {
		const store = new InMemoryMediaJobStore(LIMITS);
		let abandonedBeforeCancel = false;
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			cancel: vi.fn(async (_handle, context) => {
				const stored = await store.read(context.jobId);
				abandonedBeforeCancel =
					stored?.attempts[0]?.disposition === "abandoned" && stored.attempts[0].cancellation === "requested";
				return { status: "unsupported" as const };
			}),
		};
		const service = new MediaJobService({
			adapters: [{ adapterId: "selected-adapter", admission: testAdmission(), provider }],
			store,
			resources: publisher(),
			limits: LIMITS,
			newId: idFactory(),
			now: () => 1_800_000_000_000,
		});
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const cancelled = await service.cancel({ jobId: "job-1", expectedRevision: pending.revision });

		expect(abandonedBeforeCancel).toBe(true);
		expect(cancelled).toMatchObject({
			status: "cancel_requested",
			disposition: "abandoned",
			cancellation: "unsupported",
		});
		expect(provider.cancel).toHaveBeenCalledTimes(1);
	});

	it("cancels the attempt captured before the Store transaction returns", async () => {
		const backing = new InMemoryMediaJobStore(LIMITS);
		const controlled = controllableStore(backing);
		const cancel = vi.fn<NonNullable<MediaGenerationProvider["cancel"]>>(async () => ({ status: "unsupported" }));
		const provider: MediaGenerationProvider = {
			start: vi.fn(async (_request, context) => ({
				status: "pending" as const,
				handle: { attemptId: context.attemptId },
			})),
			cancel,
		};
		const { service } = harness(provider, publisher(), LIMITS, controlled.store);
		const first = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const abandoned = await service.cancel({ jobId: "job-1", expectedRevision: first.revision });
		const second = await service.restart({ jobId: "job-1", expectedRevision: abandoned.revision });
		cancel.mockClear();
		const paused = controlled.pauseNextModifyAfterCommit();
		const control = {
			jobId: "job-1",
			attemptId: "attempt-2",
			expectedRevision: second.revision,
		};
		const cancellation = service.cancel(control);
		await paused.entered;
		control.attemptId = "attempt-1";
		control.expectedRevision = "stale";
		paused.release();

		await expect(cancellation).resolves.toMatchObject({ currentAttemptId: "attempt-2" });
		expect(cancel).toHaveBeenCalledTimes(1);
		expect(cancel).toHaveBeenCalledWith(
			{ attemptId: "attempt-2" },
			expect.objectContaining({ attemptId: "attempt-2" }),
		);
	});

	it("captures restart control before waiting to enter the Store transaction", async () => {
		const backing = new InMemoryMediaJobStore(LIMITS);
		const controlled = controllableStore(backing);
		const provider: MediaGenerationProvider = {
			start: vi.fn(async (_request, context) => ({
				status: "pending" as const,
				handle: { attemptId: context.attemptId },
			})),
			cancel: vi.fn(async () => ({ status: "unsupported" as const })),
		};
		const { service } = harness(provider, publisher(), LIMITS, controlled.store);
		const first = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const abandoned = await service.cancel({ jobId: "job-1", expectedRevision: first.revision });
		const paused = controlled.pauseNextModifyBeforeCommit();
		const control = { jobId: "job-1", expectedRevision: abandoned.revision };
		const restart = service.restart(control);
		await paused.entered;
		control.expectedRevision = "stale";
		paused.release();

		await expect(restart).resolves.toMatchObject({ currentAttemptId: "attempt-2", status: "pending" });
	});

	it("does not inspect attempt control fields that restart does not own", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			cancel: vi.fn(async () => ({ status: "unsupported" as const })),
		};
		const { service } = harness(provider);
		const first = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const abandoned = await service.cancel({ jobId: "job-1", expectedRevision: first.revision });
		const input = { jobId: "job-1", expectedRevision: abandoned.revision };
		Object.defineProperty(input, "attemptId", {
			get: () => {
				throw new Error("restart must not read attemptId");
			},
		});

		await expect(service.restart(input)).resolves.toMatchObject({ currentAttemptId: "attempt-2", status: "pending" });
	});

	it.each(["rejected", "unknown"] as const)("retains the explicit provider cancel outcome %s", async (status) => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			cancel: vi.fn(async () => ({ status })),
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });

		await expect(service.cancel({ jobId: "job-1", expectedRevision: pending.revision })).resolves.toMatchObject({
			status: "cancel_requested",
			disposition: "abandoned",
			cancellation: status,
		});
	});

	it("records an unknown cancel outcome when the provider call throws", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			cancel: vi.fn(async () => {
				throw new Error("transport closed");
			}),
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });

		await expect(service.cancel({ jobId: "job-1", expectedRevision: pending.revision })).resolves.toMatchObject({
			status: "cancel_requested",
			disposition: "abandoned",
			cancellation: "unknown",
		});
	});

	it("retains a real completion returned by the provider cancel call", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			cancel: vi.fn(async () => ({
				status: "settled" as const,
				observation: { status: "succeeded" as const, resources: [image()] },
			})),
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });

		const completed = await service.cancel({ jobId: "job-1", expectedRevision: pending.revision });
		expect(completed).toMatchObject({
			status: "succeeded",
			disposition: "abandoned",
			cancellation: "requested",
			bundleId: "bundle-1",
		});
		await expect(service.readBundle("job-1", "bundle-1")).resolves.toMatchObject({ disposition: "late" });
	});

	it("keeps provider completion when it races a later cancel confirmation", async () => {
		let confirmCancel: (() => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			read: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
			cancel: vi.fn(
				() =>
					new Promise<MediaProviderCancelObservation>((resolve) => {
						confirmCancel = () => resolve({ status: "confirmed" as const });
					}),
			),
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const cancelling = service.cancel({ jobId: "job-1", expectedRevision: pending.revision });
		await vi.waitFor(() => expect(confirmCancel).toBeTypeOf("function"));
		const marked = await service.read("job-1");
		if (!marked) throw new Error("Expected a stored media job.");
		const settled = await service.reconcile({ jobId: "job-1", expectedRevision: marked.revision });
		confirmCancel?.();
		await cancelling;

		await expect(service.read("job-1")).resolves.toMatchObject({
			status: "succeeded",
			disposition: "abandoned",
			cancellation: "confirmed",
			bundleId: settled.bundleId,
		});
		await expect(service.readBundle("job-1", settled.bundleId ?? "missing")).resolves.toMatchObject({
			disposition: "late",
		});
	});

	it("keeps a provider cancellation that settles before a later completion observation", async () => {
		let resolveRead: ((observation: MediaProviderObservation) => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			read: vi.fn(
				() =>
					new Promise<MediaProviderObservation>((resolve) => {
						resolveRead = resolve;
					}),
			),
			cancel: vi.fn(async () => ({ status: "confirmed" as const })),
		};
		const { service, store } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const reconciliation = service.reconcile({ jobId: "job-1", expectedRevision: pending.revision });
		await vi.waitFor(() => expect(resolveRead).toBeTypeOf("function"));
		const cancelled = await service.cancel({ jobId: "job-1", expectedRevision: pending.revision });
		expect(cancelled).toMatchObject({ status: "cancelled", cancellation: "confirmed" });
		resolveRead?.({ status: "succeeded", resources: [image()] });

		await expect(reconciliation).resolves.toMatchObject({ status: "cancelled", cancellation: "confirmed" });
		expect((await store.read("job-1"))?.bundles).toEqual([]);
	});

	it("publishes partial output distinctly and records admitted child failures", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "partially_succeeded" as const,
				resources: [image(Uint8Array.of(9), 0)],
				failures: [{ ordinal: 1, failure: "provider" as const }],
			})),
		};
		const { service } = harness(provider);
		const view = await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });

		expect(view.status).toBe("partially_succeeded");
		await expect(service.readBundle("job-1", view.bundleId ?? "missing")).resolves.toMatchObject({
			disposition: "partial",
			failures: [{ ordinal: 1, failure: "provider" }],
		});
	});

	it("derives both stored slots and the bundle from one canonical ordinal order", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "succeeded" as const,
				resources: [image(Uint8Array.of(2), 1), image(Uint8Array.of(1), 0)],
			})),
		};
		const resources = publisher();
		const { service, store } = harness(provider, resources);
		const result = await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });

		const stored = await store.read("job-1");
		expect(stored?.attempts[0]?.outputSettlements.map((slot) => slot.ordinal)).toEqual([0, 1]);
		await expect(service.readBundle("job-1", result.bundleId ?? "missing")).resolves.toMatchObject({
			resources: [{ ordinal: 0 }, { ordinal: 1 }],
		});
		expect(vi.mocked(resources.publish).mock.calls.map(([resource]) => resource.ordinal)).toEqual([0, 1]);
	});

	it("admits a zero-byte slot after another resource exactly fills the bundle budget", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "succeeded" as const,
				resources: [
					{ ...image(Uint8Array.of(1), 0), declaredBytes: 1 },
					{ ...image(new Uint8Array(), 1), declaredBytes: 0 },
				],
			})),
		};
		const { service } = harness(provider, publisher(), { ...LIMITS, maxBundleBytes: 1 });

		const completed = await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });
		expect(completed.status).toBe("succeeded");
		await expect(service.readBundle("job-1", completed.bundleId ?? "missing")).resolves.toMatchObject({
			resources: [
				{ ordinal: 0, byteLength: 1 },
				{ ordinal: 1, byteLength: 0 },
			],
		});
	});

	it("fails bundle settlement without publishing a manifest or replaying the provider", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
		};
		const resources: MediaResourcePublisher = {
			publish: vi.fn(async () => {
				throw new Error("disk full");
			}),
		};
		const { service } = harness(provider, resources);
		const view = await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });

		expect(view).toMatchObject({ status: "failed", failure: "resource" });
		expect(view).not.toHaveProperty("bundleId");
		expect(provider.start).toHaveBeenCalledTimes(1);
	});

	it("classifies an output beyond the host slot admission as capacity without resource I/O", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded" as const, resources: [image(Uint8Array.of(1), 4)] })),
		};
		const resources = publisher();
		const { service } = harness(provider, resources);

		await expect(
			service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} }),
		).resolves.toMatchObject({
			status: "failed",
			failure: "capacity",
		});
		expect(resources.publish).not.toHaveBeenCalled();
	});

	it("checks the raw output count before reading provider array entries", async () => {
		let entryRead = false;
		const resources = new Array<MediaProviderResource>(LIMITS.maxOutputsPerAttempt + 1);
		Object.defineProperty(resources, 0, {
			get: () => {
				entryRead = true;
				throw new Error("output entry should not be read");
			},
		});
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded", resources }) as MediaProviderObservation),
		};
		const outputStore = publisher();
		const { service } = harness(provider, outputStore);

		await expect(
			service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} }),
		).resolves.toMatchObject({
			status: "failed",
			failure: "capacity",
		});
		expect(entryRead).toBe(false);
		expect(outputStore.publish).not.toHaveBeenCalled();
	});

	it("checks the combined partial-output count before reading provider array entries", async () => {
		let entryRead = false;
		const resources = new Array<MediaProviderResource>(LIMITS.maxOutputsPerAttempt);
		Object.defineProperty(resources, 0, {
			get: () => {
				entryRead = true;
				throw new Error("output entry should not be read");
			},
		});
		const provider: MediaGenerationProvider = {
			start: vi.fn(
				async () =>
					({
						status: "partially_succeeded",
						resources,
						failures: [{ ordinal: LIMITS.maxOutputsPerAttempt, failure: "provider" }],
					}) as MediaProviderObservation,
			),
		};
		const outputStore = publisher();
		const { service } = harness(provider, outputStore);

		await expect(
			service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} }),
		).resolves.toMatchObject({
			status: "failed",
			failure: "capacity",
		});
		expect(entryRead).toBe(false);
		expect(outputStore.publish).not.toHaveBeenCalled();
	});

	it("classifies a provider handle beyond the host JSON budget as capacity", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: "x".repeat(LIMITS.maxProviderHandleBytes + 1) })),
		};
		const { service } = harness(provider);

		await expect(
			service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} }),
		).resolves.toMatchObject({
			status: "failed",
			failure: "capacity",
		});
	});

	it("classifies a media type beyond the host byte budget as capacity", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "succeeded" as const,
				resources: [{ ...image(), mediaType: "界".repeat(LIMITS.maxMediaTypeBytes) }],
			})),
		};
		const resources = publisher();
		const { service } = harness(provider, resources);

		await expect(
			service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} }),
		).resolves.toMatchObject({
			status: "failed",
			failure: "capacity",
		});
		expect(resources.publish).not.toHaveBeenCalled();
	});

	it("rejects a declared bundle overflow before resource publication", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "succeeded" as const,
				resources: [{ ...image(), declaredBytes: 3 }],
			})),
		};
		const resources = publisher();
		const { service } = harness(provider, resources, { ...LIMITS, maxBundleBytes: 2 });

		await expect(
			service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} }),
		).resolves.toMatchObject({
			status: "failed",
			failure: "capacity",
		});
		expect(resources.publish).not.toHaveBeenCalled();
	});

	it("classifies an actual resource beyond the remaining bundle budget as capacity", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
		};
		const { service } = harness(provider, publisher(), { ...LIMITS, maxBundleBytes: 2 });

		await expect(
			service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} }),
		).resolves.toMatchObject({
			status: "failed",
			failure: "capacity",
		});
	});

	it("classifies a bundle manifest beyond its host budget as capacity", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
		};
		const { service } = harness(provider, publisher(), { ...LIMITS, maxManifestBytes: 1 });

		const result = await service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} });
		expect(result).toMatchObject({ status: "failed", failure: "capacity" });
		expect(result).not.toHaveProperty("bundleId");
	});

	it("does not convert an infrastructure failure during manifest activation into a resource failure", async () => {
		const inner = new InMemoryMediaJobStore(LIMITS);
		const fault = new Error("store unavailable");
		let providerCompleted = false;
		const store: MediaJobStore = {
			create: (job) => inner.create(job),
			purge: (request) => inner.purge(request),
			listQueued: (request) => inner.listQueued(request),
			read: (jobId) => inner.read(jobId),
			modify: (jobId, change) => {
				if (providerCompleted) throw fault;
				return inner.modify(jobId, change);
			},
		};
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => {
				providerCompleted = true;
				return { status: "succeeded" as const, resources: [image()] };
			}),
		};
		const { service } = harness(provider, publisher(), LIMITS, store);

		await expect(service.submit("selected-adapter", { jobId: "job-1", kind: "image", input: {} })).rejects.toBe(fault);
		await expect(service.read("job-1")).resolves.toMatchObject({ status: "submitting" });
	});

	it("maps a malformed cancel result to unknown without claiming provider cancellation", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "private" } })),
			cancel: vi.fn(async () => null) as unknown as NonNullable<MediaGenerationProvider["cancel"]>,
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });

		await expect(service.cancel({ jobId: "job-1", expectedRevision: pending.revision })).resolves.toMatchObject({
			status: "cancel_requested",
			disposition: "abandoned",
			cancellation: "unknown",
		});
	});

	it("clears a stale unknown failure after a later provider observation succeeds", async () => {
		const read = vi
			.fn<NonNullable<MediaGenerationProvider["read"]>>()
			.mockRejectedValueOnce(new Error("temporary transport loss"))
			.mockResolvedValueOnce({ status: "pending", handle: { operation: "second" } })
			.mockResolvedValueOnce({ status: "succeeded", resources: [image()] });
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "first" } })),
			read,
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const unknown = await service.reconcile({ jobId: "job-1", expectedRevision: pending.revision });
		expect(unknown).toMatchObject({ status: "outcome_unknown", failure: "unknown" });

		const recovered = await service.reconcile({ jobId: "job-1", expectedRevision: unknown.revision });
		expect(recovered.status).toBe("pending");
		expect(recovered).not.toHaveProperty("failure");
		const completed = await service.reconcile({ jobId: "job-1", expectedRevision: recovered.revision });
		expect(completed.status).toBe("succeeded");
		expect(completed).not.toHaveProperty("failure");
	});

	it("clears a stale unknown failure when provider cancellation is later confirmed", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "first" } })),
			read: vi.fn(async () => {
				throw new Error("temporary transport loss");
			}),
			cancel: vi.fn(async () => ({ status: "confirmed" as const })),
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const unknown = await service.reconcile({ jobId: "job-1", expectedRevision: pending.revision });

		const cancelled = await service.cancel({ jobId: "job-1", expectedRevision: unknown.revision });
		expect(cancelled).toMatchObject({ status: "cancelled", cancellation: "confirmed" });
		expect(cancelled).not.toHaveProperty("failure");
	});

	it("propagates the caller abort reason after retaining an ambiguous submitted attempt", async () => {
		const provider: MediaGenerationProvider = {
			start: vi.fn<MediaGenerationProvider["start"]>(
				(_request, context) =>
					new Promise<MediaProviderObservation>((_resolve, reject) => {
						context.signal.addEventListener("abort", () => reject(context.signal.reason), { once: true });
					}),
			),
		};
		const { service } = harness(provider);
		const controller = new AbortController();
		const reason = new Error("caller stopped");
		const submission = service.submit(
			"selected-adapter",
			{ jobId: "job-1", kind: "video", input: {} },
			controller.signal,
		);
		await vi.waitFor(() => expect(provider.start).toHaveBeenCalledTimes(1));
		controller.abort(reason);

		await expect(submission).rejects.toBe(reason);
		await expect(service.read("job-1")).resolves.toMatchObject({ status: "outcome_unknown" });
	});

	it("records a coherent local cancellation when abort lands after persistence but before provider start", async () => {
		const inner = new InMemoryMediaJobStore(LIMITS);
		let releaseCreate: (() => void) | undefined;
		let created = false;
		const store: MediaJobStore = {
			create: async (job) => {
				await inner.create(job);
				created = true;
				await new Promise<void>((resolve) => {
					releaseCreate = resolve;
				});
			},
			read: (jobId) => inner.read(jobId),
			purge: (request) => inner.purge(request),
			listQueued: (request) => inner.listQueued(request),
			modify: (jobId, change) => inner.modify(jobId, change),
		};
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "unexpected" } })),
		};
		const { service } = harness(provider, publisher(), LIMITS, store);
		const controller = new AbortController();
		const reason = new Error("caller stopped");
		const submission = service.submit(
			"selected-adapter",
			{ jobId: "job-1", kind: "video", input: {} },
			controller.signal,
		);
		await vi.waitFor(() => expect(created).toBe(true));
		controller.abort(reason);
		releaseCreate?.();

		await expect(submission).rejects.toBe(reason);
		expect(provider.start).not.toHaveBeenCalled();
		await expect(service.read("job-1")).resolves.toMatchObject({
			status: "cancelled",
			disposition: "abandoned",
			cancellation: "requested",
		});
	});

	it("propagates a caller abort when start ignores the signal and then returns", async () => {
		let resolveStart: ((observation: MediaProviderObservation) => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn(
				() =>
					new Promise<MediaProviderObservation>((resolve) => {
						resolveStart = resolve;
					}),
			),
		};
		const { service } = harness(provider);
		const controller = new AbortController();
		const reason = new Error("caller stopped");
		const submission = service.submit(
			"selected-adapter",
			{ jobId: "job-1", kind: "video", input: {} },
			controller.signal,
		);
		await vi.waitFor(() => expect(resolveStart).toBeTypeOf("function"));
		controller.abort(reason);
		resolveStart?.({ status: "pending", handle: { operation: "accepted" } });

		await expect(submission).rejects.toBe(reason);
		await expect(service.read("job-1")).resolves.toMatchObject({ status: "pending" });
	});

	it("propagates a caller abort when read ignores the signal and then returns", async () => {
		let resolveRead: ((observation: MediaProviderObservation) => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "first" } })),
			read: vi.fn(
				() =>
					new Promise<MediaProviderObservation>((resolve) => {
						resolveRead = resolve;
					}),
			),
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const controller = new AbortController();
		const reason = new Error("caller stopped");
		const reconciliation = service.reconcile({ jobId: "job-1", expectedRevision: pending.revision }, controller.signal);
		await vi.waitFor(() => expect(resolveRead).toBeTypeOf("function"));
		controller.abort(reason);
		resolveRead?.({ status: "pending", handle: { operation: "second" } });

		await expect(reconciliation).rejects.toBe(reason);
		await expect(service.read("job-1")).resolves.toMatchObject({ status: "pending" });
	});

	it("propagates a caller abort when cancel ignores the signal and then confirms", async () => {
		let resolveCancel: ((observation: MediaProviderCancelObservation) => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "first" } })),
			cancel: vi.fn(
				() =>
					new Promise<MediaProviderCancelObservation>((resolve) => {
						resolveCancel = resolve;
					}),
			),
		};
		const { service } = harness(provider);
		const pending = await service.submit("selected-adapter", { jobId: "job-1", kind: "video", input: {} });
		const controller = new AbortController();
		const reason = new Error("caller stopped");
		const cancellation = service.cancel({ jobId: "job-1", expectedRevision: pending.revision }, controller.signal);
		await vi.waitFor(() => expect(resolveCancel).toBeTypeOf("function"));
		controller.abort(reason);
		resolveCancel?.({ status: "confirmed" });

		await expect(cancellation).rejects.toBe(reason);
		await expect(service.read("job-1")).resolves.toMatchObject({ status: "cancelled", cancellation: "confirmed" });
	});

	it("propagates a caller abort when resource publication ignores the signal", async () => {
		let resolvePublish: ((reference: Awaited<ReturnType<MediaResourcePublisher["publish"]>>) => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({ status: "succeeded" as const, resources: [image()] })),
		};
		const resources: MediaResourcePublisher = {
			publish: vi.fn(
				(resource) =>
					new Promise<Awaited<ReturnType<MediaResourcePublisher["publish"]>>>((resolve) => {
						const sha256 = createHash("sha256")
							.update(resource.bytes as Uint8Array)
							.digest("hex");
						resolvePublish = () =>
							resolve({
								resourceId: sha256,
								kind: resource.kind,
								mediaType: resource.mediaType,
								byteLength: (resource.bytes as Uint8Array).byteLength,
								sha256,
								ordinal: resource.ordinal,
							});
					}),
			),
		};
		const { service } = harness(provider, resources);
		const controller = new AbortController();
		const reason = new Error("caller stopped");
		const submission = service.submit(
			"selected-adapter",
			{ jobId: "job-1", kind: "image", input: {} },
			controller.signal,
		);
		await vi.waitFor(() => expect(resolvePublish).toBeTypeOf("function"));
		controller.abort(reason);
		resolvePublish?.({} as never);

		await expect(submission).rejects.toBe(reason);
		await expect(service.read("job-1")).resolves.toMatchObject({ status: "succeeded", bundleId: "bundle-1" });
	});

	it("retains admitted output slots when caller abort stops later resource publication", async () => {
		let secondStarted = false;
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "succeeded" as const,
				handle: { operation: "completed" },
				resources: [image(Uint8Array.of(1), 0), image(Uint8Array.of(2), 1)],
			})),
			read: vi.fn(async () => ({ status: "pending" as const, handle: { operation: "completed" } })),
			cancel: vi.fn(async () => ({ status: "confirmed" as const })),
		};
		const resources: MediaResourcePublisher = {
			publish: vi.fn(async (resource, context) => {
				if (!(resource.bytes instanceof Uint8Array)) throw new Error("Expected eager test bytes.");
				if (resource.ordinal === 1) {
					secondStarted = true;
					await new Promise<void>((_resolve, reject) => {
						context.signal.addEventListener("abort", () => reject(context.signal.reason), { once: true });
					});
				}
				const sha256 = createHash("sha256").update(resource.bytes).digest("hex");
				return {
					resourceId: sha256,
					kind: resource.kind,
					mediaType: resource.mediaType,
					byteLength: resource.bytes.byteLength,
					sha256,
					ordinal: resource.ordinal,
				};
			}),
		};
		const { service, store } = harness(provider, resources);
		const controller = new AbortController();
		const reason = new Error("caller stopped");
		const submission = service.submit(
			"selected-adapter",
			{ jobId: "job-1", kind: "image", input: {} },
			controller.signal,
		);
		await vi.waitFor(() => expect(secondStarted).toBe(true));
		controller.abort(reason);

		await expect(submission).rejects.toBe(reason);
		const settling = await service.read("job-1");
		expect(settling).toMatchObject({ status: "settling" });
		if (!settling) throw new Error("Expected a settling job.");
		const abandoned = await service.cancel({ jobId: "job-1", expectedRevision: settling.revision });
		expect(abandoned).toMatchObject({ status: "settling", disposition: "abandoned", cancellation: "requested" });
		expect(provider.cancel).not.toHaveBeenCalled();
		const stillSettling = await service.reconcile({ jobId: "job-1", expectedRevision: abandoned.revision });
		expect(stillSettling).toMatchObject({ status: "settling", disposition: "abandoned", cancellation: "requested" });
		expect(provider.read).toHaveBeenCalledTimes(1);
		const stored = await store.read("job-1");
		expect(stored?.attempts[0]?.outputSettlements).toEqual([
			expect.objectContaining({ ordinal: 0, status: "published" }),
		]);
		expect(stored?.bundles).toEqual([]);
	});

	it("rechecks the bundle byte budget after merging a retained settling prefix", async () => {
		let abortingStart = true;
		let secondStarted = false;
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => ({
				status: "succeeded" as const,
				handle: { operation: "completed" },
				resources: [image(Uint8Array.of(1), 0), image(Uint8Array.of(2), 1)],
			})),
			read: vi.fn(async () => ({
				status: "succeeded" as const,
				handle: { operation: "completed" },
				resources: [image(Uint8Array.of(3), 2)],
			})),
		};
		const resources: MediaResourcePublisher = {
			publish: vi.fn(async (resource, context) => {
				if (!(resource.bytes instanceof Uint8Array)) throw new Error("Expected eager test bytes.");
				if (abortingStart && resource.ordinal === 1) {
					secondStarted = true;
					await new Promise<void>((_resolve, reject) => {
						context.signal.addEventListener("abort", () => reject(context.signal.reason), { once: true });
					});
				}
				const sha256 = createHash("sha256").update(resource.bytes).digest("hex");
				return {
					resourceId: sha256,
					kind: resource.kind,
					mediaType: resource.mediaType,
					byteLength: resource.bytes.byteLength,
					sha256,
					ordinal: resource.ordinal,
				};
			}),
		};
		const { service, store } = harness(provider, resources, { ...LIMITS, maxBundleBytes: 1 });
		const controller = new AbortController();
		const reason = new Error("caller stopped");
		const submission = service.submit(
			"selected-adapter",
			{ jobId: "job-1", kind: "image", input: {} },
			controller.signal,
		);
		await vi.waitFor(() => expect(secondStarted).toBe(true));
		controller.abort(reason);
		await expect(submission).rejects.toBe(reason);
		const settling = await service.read("job-1");
		if (!settling) throw new Error("Expected a settling job.");

		abortingStart = false;
		const failed = await service.reconcile({ jobId: "job-1", expectedRevision: settling.revision });
		expect(failed).toMatchObject({ status: "failed", failure: "capacity" });
		const stored = await store.read("job-1");
		expect(stored?.attempts[0]?.outputSettlements.map((slot) => slot.ordinal)).toEqual([0, 2]);
		expect(stored?.bundles).toEqual([]);
	});
});

function controllableStore(delegate: MediaJobStore) {
	let nextRead: ExecutionGate | undefined;
	let nextModifyBeforeCommit: ExecutionGate | undefined;
	let nextModifyAfterCommit: ExecutionGate | undefined;
	return {
		store: {
			create: (job) => delegate.create(job),
			purge: (request) => delegate.purge(request),
			listQueued: (request) => delegate.listQueued(request),
			read: async (jobId) => {
				const result = await delegate.read(jobId);
				const gate = nextRead;
				nextRead = undefined;
				if (gate) await gate.pause();
				return result;
			},
			modify: async (jobId, change) => {
				const before = nextModifyBeforeCommit;
				nextModifyBeforeCommit = undefined;
				if (before) await before.pause();
				const result = await delegate.modify(jobId, change);
				const after = nextModifyAfterCommit;
				nextModifyAfterCommit = undefined;
				if (after) await after.pause();
				return result;
			},
		} satisfies MediaJobStore,
		pauseNextRead: () => (nextRead = executionGate()),
		pauseNextModifyBeforeCommit: () => (nextModifyBeforeCommit = executionGate()),
		pauseNextModifyAfterCommit: () => (nextModifyAfterCommit = executionGate()),
	};
}

interface ExecutionGate {
	readonly entered: Promise<void>;
	pause(): Promise<void>;
	release(): void;
}

function executionGate(): ExecutionGate {
	const entered = deferred<void>();
	const released = deferred<void>();
	return {
		entered: entered.promise,
		pause: () => {
			entered.resolve(undefined);
			return released.promise;
		},
		release: () => released.resolve(undefined),
	};
}

function deferred<T>() {
	let resolve!: (value: T) => void;
	const promise = new Promise<T>((settle) => {
		resolve = settle;
	});
	return { promise, resolve };
}

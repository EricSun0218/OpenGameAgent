import { describe, expect, it, vi } from "vitest";
import {
	InMemoryMediaJobStore,
	MediaCallAdmissionGroup,
	MediaJobAdmissionFullError,
	MediaJobService,
	type MediaGenerationProvider,
	type MediaJobLimits,
	type MediaJobStore,
	type MediaProviderObservation,
} from "../src/index.js";

const limits: MediaJobLimits = {
	maxIdentifierBytes: 128,
	maxAdapterIdBytes: 64,
	maxAdapters: 4,
	maxRequestBytes: 1024,
	maxJsonDepth: 8,
	maxProviderHandleBytes: 512,
	maxAttemptsPerJob: 4,
	maxOutputsPerAttempt: 4,
	maxResourceBytes: 1024,
	maxBundleBytes: 2048,
	maxManifestBytes: 2048,
	maxMediaTypeBytes: 128,
};

describe("media provider admission", () => {
	it("requires explicit limits and never installs an implicit admission group", () => {
		for (const configured of [
			{ maxConcurrentCalls: 0, maxQueuedCalls: 0 },
			{ maxConcurrentCalls: 1, maxQueuedCalls: -1 },
			{ maxConcurrentCalls: Number.POSITIVE_INFINITY, maxQueuedCalls: 1 },
		])
			expect(() => new MediaCallAdmissionGroup(configured)).toThrow(RangeError);
		expect(
			() =>
				new MediaJobService({
					adapters: [{ adapterId: "adapter", provider: { start: vi.fn() } }],
					store: new InMemoryMediaJobStore(limits),
					limits,
					resources: { publish: vi.fn() },
					newId: () => "id",
					now: () => 0,
				} as unknown as ConstructorParameters<typeof MediaJobService>[0]),
		).toThrow();
	});

	it("persists a waiting job, removes cancelled waiters, and rejects overflow before durable creation", async () => {
		const group = new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 1 });
		let finish: ((result: MediaProviderObservation) => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn<MediaGenerationProvider["start"]>(
				() =>
					new Promise((resolve) => {
						finish = resolve;
					}),
			),
		};
		const store = new InMemoryMediaJobStore(limits);
		const service = host(store, provider, group);
		const active = service.submit("adapter", { jobId: "active", kind: "image", input: {} });
		await vi.waitFor(() => expect(provider.start).toHaveBeenCalledOnce());
		const waiting = service.submit("adapter", { jobId: "waiting", kind: "image", input: {} });
		await vi.waitFor(async () => expect(await service.read("waiting")).toMatchObject({ status: "queued" }));
		await expect(service.submit("adapter", { jobId: "overflow", kind: "image", input: {} })).rejects.toBeInstanceOf(
			MediaJobAdmissionFullError,
		);
		expect(await store.read("overflow")).toBeUndefined();
		await expect(service.cancel({ jobId: "waiting" })).resolves.toMatchObject({ status: "cancelled" });
		await expect(waiting).resolves.toMatchObject({ status: "cancelled" });
		const controller = new AbortController();
		const reason = new Error("cancel queued request");
		const replacement = service.submit(
			"adapter",
			{ jobId: "replacement", kind: "image", input: {} },
			controller.signal,
		);
		await vi.waitFor(async () => expect(await service.read("replacement")).toMatchObject({ status: "queued" }));
		controller.abort(reason);
		await expect(replacement).rejects.toBe(reason);
		expect(await store.read("replacement")).toMatchObject({
			attempts: [expect.objectContaining({ status: "cancelled" })],
		});
		expect(provider.start).toHaveBeenCalledOnce();
		finish?.({ status: "pending", handle: { ticket: "remote" } });
		await expect(active).resolves.toMatchObject({ status: "pending" });
	});

	it("shares capacity across adapters in one group and resumes a durable queued attempt exactly once", async () => {
		const group = new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 2 });
		const isolated = new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 0 });
		let finish: ((result: MediaProviderObservation) => void) | undefined;
		const busy: MediaGenerationProvider = {
			start: vi.fn<MediaGenerationProvider["start"]>(
				() =>
					new Promise((resolve) => {
						finish = resolve;
					}),
			),
		};
		const available: MediaGenerationProvider = {
			start: vi.fn<MediaGenerationProvider["start"]>(async () => ({ status: "pending", handle: { ticket: "once" } })),
		};
		const store = new InMemoryMediaJobStore(limits);
		let sequence = 0;
		const service = new MediaJobService({
			adapters: [
				{ adapterId: "busy", provider: busy, admission: group },
				{ adapterId: "shared", provider: available, admission: group },
				{ adapterId: "isolated", provider: available, admission: isolated },
			],
			store,
			limits,
			resources: {
				publish: async () => {
					throw new Error("unexpected resources");
				},
			},
			newId: (kind) => `${kind}-${++sequence}`,
			now: () => Date.parse("2026-09-01T00:00:00.000Z"),
		});
		const active = service.submit("busy", { jobId: "active", kind: "image", input: {} });
		await vi.waitFor(() => expect(busy.start).toHaveBeenCalledOnce());
		const shared = service.submit("shared", { jobId: "shared", kind: "image", input: {} });
		await vi.waitFor(async () => expect(await service.read("shared")).toMatchObject({ status: "queued" }));
		await expect(service.submit("isolated", { jobId: "isolated", kind: "image", input: {} })).resolves.toMatchObject({
			status: "pending",
		});
		expect(available.start).toHaveBeenCalledOnce();
		const recovered = service.reconcile({ jobId: "shared" });
		finish?.({ status: "pending", handle: { ticket: "busy" } });
		await active;
		await shared;
		await recovered;
		expect(available.start).toHaveBeenCalledTimes(2);
	});

	it("records local cancellation if abort arrives after durable claim but before provider invocation", async () => {
		const inner = new InMemoryMediaJobStore(limits);
		const controller = new AbortController();
		const reason = new Error("cancel before external effect");
		const store: MediaJobStore = {
			create: (job) => inner.create(job),
			read: (jobId) => inner.read(jobId),
			purge: (request) => inner.purge(request),
			listQueued: (request) => inner.listQueued(request),
			modify: async (jobId, change) => {
				const changed = await inner.modify(jobId, change);
				if (changed.attempts.at(-1)?.status === "submitting") controller.abort(reason);
				return changed;
			},
		};
		const provider: MediaGenerationProvider = { start: vi.fn<MediaGenerationProvider["start"]>() };
		const service = host(store, provider, new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 0 }));
		await expect(service.submit("adapter", { jobId: "job", kind: "image", input: {} }, controller.signal)).rejects.toBe(
			reason,
		);
		expect(provider.start).not.toHaveBeenCalled();
		expect(await service.read("job")).toMatchObject({ status: "cancelled", disposition: "abandoned" });
	});

	it("holds capacity until a provider that ignores abort settles and does not count remote pending jobs as calls", async () => {
		const group = new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 1 });
		const completions = new Map<string, (result: MediaProviderObservation) => void>();
		const provider: MediaGenerationProvider = {
			start: vi.fn<MediaGenerationProvider["start"]>(
				(_input, context) =>
					new Promise((resolve) => {
						completions.set(context.jobId, resolve);
					}),
			),
		};
		const service = host(new InMemoryMediaJobStore(limits), provider, group);
		const controller = new AbortController();
		const reason = new Error("active caller abort");
		const first = service.submit("adapter", { jobId: "first", kind: "image", input: {} }, controller.signal);
		const firstOutcome = first.catch((error: unknown) => error);
		await vi.waitFor(() => expect(completions.has("first")).toBe(true));
		controller.abort(reason);
		const second = service.submit("adapter", { jobId: "second", kind: "image", input: {} });
		await vi.waitFor(async () => expect(await service.read("second")).toMatchObject({ status: "queued" }));
		expect(provider.start).toHaveBeenCalledOnce();
		await expect(service.submit("adapter", { jobId: "overflow", kind: "image", input: {} })).rejects.toBeInstanceOf(
			MediaJobAdmissionFullError,
		);
		completions.get("first")?.({ status: "pending", handle: { remote: "first" } });
		expect(await firstOutcome).toBe(reason);
		await vi.waitFor(() => expect(completions.has("second")).toBe(true));
		completions.get("second")?.({ status: "pending", handle: { remote: "second" } });
		await expect(second).resolves.toMatchObject({ status: "pending" });
		expect(await service.read("first")).toMatchObject({ status: "pending" });
	});

	it("applies the same group to read and cancel and preserves durable state when a waiting control aborts", async () => {
		let finishRead: ((result: MediaProviderObservation) => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn<MediaGenerationProvider["start"]>(async () => ({ status: "pending", handle: { remote: "job" } })),
			read: vi.fn<NonNullable<MediaGenerationProvider["read"]>>(
				() =>
					new Promise((resolve) => {
						finishRead = resolve;
					}),
			),
			cancel: vi.fn<NonNullable<MediaGenerationProvider["cancel"]>>(async () => ({ status: "confirmed" })),
		};
		const service = host(
			new InMemoryMediaJobStore(limits),
			provider,
			new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 1 }),
		);
		await service.submit("adapter", { jobId: "job", kind: "image", input: {} });
		const reading = service.reconcile({ jobId: "job" });
		await vi.waitFor(() => expect(provider.read).toHaveBeenCalledOnce());
		const controller = new AbortController();
		const reason = new Error("cancel control stopped waiting");
		const cancelling = service.cancel({ jobId: "job" }, controller.signal);
		await vi.waitFor(async () => expect(await service.read("job")).toMatchObject({ status: "cancel_requested" }));
		await expect(service.reconcile({ jobId: "job" })).rejects.toBeInstanceOf(MediaJobAdmissionFullError);
		controller.abort(reason);
		await expect(cancelling).rejects.toBe(reason);
		expect(provider.cancel).not.toHaveBeenCalled();
		expect(await service.read("job")).toMatchObject({ status: "cancel_requested", cancellation: "requested" });
		const replacement = service.submit("adapter", { jobId: "replacement", kind: "image", input: {} });
		await vi.waitFor(async () => expect(await service.read("replacement")).toMatchObject({ status: "queued" }));
		finishRead?.({ status: "pending", handle: { remote: "job" } });
		await reading;
		await expect(replacement).resolves.toMatchObject({ status: "pending" });
		expect(provider.start).toHaveBeenCalledTimes(2);
	});

	it("does not add a restart attempt when admission is full and resumes successfully after capacity returns", async () => {
		let finish: ((result: MediaProviderObservation) => void) | undefined;
		const provider: MediaGenerationProvider = {
			start: vi.fn<MediaGenerationProvider["start"]>(async (_input, context) => {
				if (context.jobId === "active")
					return await new Promise<MediaProviderObservation>((resolve) => {
						finish = resolve;
					});
				return { status: "failed", failure: "provider" };
			}),
		};
		const store = new InMemoryMediaJobStore(limits);
		const service = host(store, provider, new MediaCallAdmissionGroup({ maxConcurrentCalls: 1, maxQueuedCalls: 0 }));
		const first = await service.submit("adapter", { jobId: "retry", kind: "image", input: {} });
		const active = service.submit("adapter", { jobId: "active", kind: "image", input: {} });
		await vi.waitFor(() => expect(finish).toBeDefined());
		await expect(service.restart({ jobId: "retry", expectedRevision: first.revision })).rejects.toBeInstanceOf(
			MediaJobAdmissionFullError,
		);
		expect((await store.read("retry"))?.attempts).toHaveLength(1);
		finish?.({ status: "pending", handle: { remote: "active" } });
		await active;
		expect((await service.restart({ jobId: "retry", expectedRevision: first.revision })).attempts).toHaveLength(2);
	});
});

function host(store: MediaJobStore, provider: MediaGenerationProvider, admission: MediaCallAdmissionGroup) {
	let sequence = 0;
	return new MediaJobService({
		adapters: [{ adapterId: "adapter", provider, admission }],
		store,
		limits,
		resources: {
			publish: async () => {
				throw new Error("unexpected resources");
			},
		},
		newId: (kind) => `${kind}-${++sequence}`,
		now: () => Date.parse("2026-09-01T00:00:00.000Z"),
	});
}

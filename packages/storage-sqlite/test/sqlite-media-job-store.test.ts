import { createHash } from "node:crypto";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
	InMemoryMediaJobStore,
	MediaCallAdmissionGroup,
	MediaJobAdapterUnavailableError,
	type MediaGenerationAdapter,
	type MediaGenerationProvider,
	MediaJobConflictError,
	MediaJobNotFoundError,
	MediaJobService,
	type MediaJobStore,
	MediaJobValidationError,
	type MediaResourcePublisher,
	type StoredMediaJob,
} from "@opengameagent/media";
import { canonicalizeJsonValue, type JsonValue } from "@opengameagent/protocol";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	SqliteAuthority,
	SqliteMediaJobCapacityError,
	SqliteMediaJobCorruptionError,
	SqliteMediaJobRevisionExhaustedError,
	SqliteMediaJobStore,
} from "../src/index.js";

const directories: string[] = [];
const maxStoredJobBytes = 16_384;
const limits = {
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
} as const;

afterEach(async () => {
	for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
});

describe("SqliteMediaJobStore", () => {
	it("matches bounded detached queued listing in memory and SQLite without exposing other adapter jobs", async () => {
		const sqlite = authority(await databasePath());
		try {
			for (const media of [new InMemoryMediaJobStore(limits), store(sqlite)]) {
				const original = job();
				const attempt = original.attempts[0];
				if (!attempt) throw new Error("missing attempt fixture");
				const { providerHandle: _handle, ...unsubmitted } = attempt;
				for (const jobId of ["c", "a", "b"])
					await media.create({ ...original, jobId, attempts: [{ ...unsubmitted, status: "queued" }] });
				await media.create({
					...original,
					adapterId: "other",
					jobId: "other",
					attempts: [{ ...unsubmitted, status: "queued" }],
				});
				const batch = await media.listQueued({ adapterId: "adapter", limit: 2 });
				expect(batch.map((entry) => entry.jobId)).toEqual(["a", "b"]);
				(batch[0]?.request.input as { prompt: string }).prompt = "modified detached result";
				expect((await media.read("a"))?.request.input).toEqual({ prompt: "a garden" });
				expect(
					(await media.listQueued({ adapterId: "adapter", afterJobId: "b", limit: 2 })).map((entry) => entry.jobId),
				).toEqual(["c"]);
				await expect(media.listQueued({ adapterId: "adapter", limit: 0 })).rejects.toBeInstanceOf(RangeError);
			}
		} finally {
			sqlite.close();
		}
	});

	it("lists queued jobs in explicit batches and resumes one after cold restart without resending a submitted attempt", async () => {
		const path = await databasePath();
		const initialAuthority = authority(path);
		const initial = store(initialAuthority);
		const original = job();
		const attempt = original.attempts[0];
		if (!attempt) throw new Error("missing attempt fixture");
		const { providerHandle: _handle, ...unsubmitted } = attempt;
		for (const jobId of ["a", "b", "c"])
			await initial.create({ ...original, jobId, attempts: [{ ...unsubmitted, status: "queued" }] });
		await initial.create({ ...original, jobId: "submitted", attempts: [{ ...unsubmitted, status: "submitting" }] });
		initialAuthority.close();
		const reopenedAuthority = authority(path);
		try {
			const reopened = store(reopenedAuthority);
			const start = vi.fn<MediaGenerationProvider["start"]>(async () => ({
				status: "pending",
				handle: { ticket: "restored" },
			}));
			const restored = service(reopened, { start });
			expect((await restored.listQueued({ adapterId: "adapter", limit: 1 })).map((entry) => entry.jobId)).toEqual([
				"a",
			]);
			expect(
				(await restored.listQueued({ adapterId: "adapter", afterJobId: "a", limit: 1 })).map((entry) => entry.jobId),
			).toEqual(["b"]);
			await Promise.all([restored.reconcile({ jobId: "a" }), restored.reconcile({ jobId: "a" })]);
			expect(start).toHaveBeenCalledOnce();
			await expect(restored.reconcile({ jobId: "submitted" })).resolves.toMatchObject({ status: "outcome_unknown" });
			expect(start).toHaveBeenCalledOnce();
			expect((await restored.listQueued({ adapterId: "adapter", limit: 2 })).map((entry) => entry.jobId)).toEqual([
				"b",
				"c",
			]);
		} finally {
			reopenedAuthority.close();
		}
	});
	it("purges only unretained terminal or abandoned jobs before the strict cutoff in bounded batches", async () => {
		const sqlite = authority(await databasePath());
		try {
			for (const media of [new InMemoryMediaJobStore(limits), store(sqlite)]) {
				for (const [jobId, state] of [
					["active", "active"],
					["abandoned", "abandoned"],
					["terminal", "terminal"],
					["retained", "terminal"],
				] as const) {
					const original = job();
					await media.create({
						...original,
						jobId,
						attempts: original.attempts.map((attempt) =>
							state === "active"
								? attempt
								: state === "abandoned"
									? { ...attempt, status: "cancel_requested", disposition: "abandoned", cancellation: "requested" }
									: { ...attempt, status: "failed", failure: "provider" },
						),
					});
				}
				const request = {
					beforeUpdatedAt: "2026-08-31T01:00:00.000Z",
					limit: 1,
					retainJobIds: ["retained"],
					retainBudget: { maxCount: 1, maxBytes: 8 },
				};
				expect(await media.purge(request)).toEqual({ deletedJobs: 0 });
				const eligible = { ...request, beforeUpdatedAt: "2026-08-31T01:00:00.001Z" };
				expect(await media.purge(eligible)).toEqual({ deletedJobs: 1 });
				expect(await media.purge(eligible)).toEqual({ deletedJobs: 1 });
				expect(await media.purge(eligible)).toEqual({ deletedJobs: 0 });
				expect(await media.read("active")).toBeDefined();
				expect(await media.read("retained")).toBeDefined();
				expect(await media.read("abandoned")).toBeUndefined();
				expect(await media.read("terminal")).toBeUndefined();
			}
		} finally {
			sqlite.close();
		}
	});

	it("admits the complete retain budget before mutation and never purges a restarted active attempt", async () => {
		const sqlite = authority(await databasePath());
		try {
			for (const media of [new InMemoryMediaJobStore(limits), store(sqlite)]) {
				const original = job();
				const attempt = original.attempts[0];
				if (!attempt) throw new Error("missing attempt fixture");
				await media.create({
					...original,
					jobId: "保留",
					attempts: [{ ...attempt, status: "failed", failure: "provider" }],
				});
				await media.create({
					...original,
					currentAttemptId: "restart",
					attempts: [
						{ ...attempt, status: "cancel_requested", disposition: "abandoned", cancellation: "requested" },
						{ ...attempt, attemptId: "restart", ordinal: 1, submissionKey: "restart-submission" },
					],
				});
				const request = {
					beforeUpdatedAt: "2026-09-01T00:00:00.000Z",
					limit: 4,
					retainJobIds: ["保留"],
					retainBudget: { maxCount: 1, maxBytes: 6 },
				};
				await expect(media.purge({ ...request, retainBudget: { maxCount: 0, maxBytes: 6 } })).rejects.toBeInstanceOf(
					RangeError,
				);
				await expect(media.purge({ ...request, retainBudget: { maxCount: 1, maxBytes: 5 } })).rejects.toBeInstanceOf(
					RangeError,
				);
				await expect(media.purge({ ...request, limit: 0 })).rejects.toBeInstanceOf(RangeError);
				expect(await media.purge(request)).toEqual({ deletedJobs: 0 });
				expect(await media.read("保留")).toBeDefined();
				expect(await media.read("job")).toMatchObject({ currentAttemptId: "restart" });
			}
		} finally {
			sqlite.close();
		}
	});

	it("preserves retained bundles after cold restart while purging an unretained completed job", async () => {
		const path = await databasePath();
		const firstAuthority = authority(path);
		const firstStore = store(firstAuthority);
		const media = service(firstStore, {
			start: async () => ({
				status: "succeeded",
				resources: [{ ordinal: 0, kind: "image", mediaType: "image/png", bytes: Uint8Array.of(1) }],
			}),
		});
		await media.submit("adapter", { jobId: "retained", kind: "image", input: {} });
		await media.submit("adapter", { jobId: "obsolete", kind: "image", input: {} });
		const retained = await firstStore.read("retained");
		firstAuthority.close();
		const reopenedAuthority = authority(path);
		try {
			const reopened = store(reopenedAuthority);
			expect(
				await reopened.purge({
					beforeUpdatedAt: "2026-09-01T00:00:00.000Z",
					limit: 2,
					retainJobIds: ["retained"],
					retainBudget: { maxCount: 1, maxBytes: 8 },
				}),
			).toEqual({ deletedJobs: 1 });
			expect(await reopened.read("retained")).toEqual(retained);
			expect(await reopened.read("obsolete")).toBeUndefined();
		} finally {
			reopenedAuthority.close();
		}
	});

	it("rolls back the entire purge batch if an eligible durable job fails integrity validation", async () => {
		const sqlite = authority(await databasePath());
		try {
			const media = store(sqlite);
			const original = job();
			for (const jobId of ["first", "job"])
				await media.create({
					...original,
					jobId,
					attempts: original.attempts.map((attempt) => ({ ...attempt, status: "failed", failure: "provider" })),
				});
			sqlite.write((database) =>
				database.prepare("UPDATE oga_media_jobs SET job_fingerprint = ? WHERE job_id = ?").run("0".repeat(64), "job"),
			);
			await expect(
				media.purge({
					beforeUpdatedAt: "2026-09-01T00:00:00.000Z",
					limit: 2,
					retainJobIds: [],
					retainBudget: { maxCount: 0, maxBytes: 0 },
				}),
			).rejects.toBeInstanceOf(SqliteMediaJobCorruptionError);
			expect(await media.read("first")).toBeDefined();
		} finally {
			sqlite.close();
		}
	});

	it("persists one detached job across a cold restart", async () => {
		const path = await databasePath();
		const firstAuthority = authority(path);
		const first = store(firstAuthority);
		const original = job();

		await first.create(original);
		const warm = await first.read(original.jobId);
		expect(warm).toEqual(original);
		if (!warm) throw new Error("expected media job");
		(warm.request.input as { prompt: string }).prompt = "mutated return";
		expect(await first.read(original.jobId)).toEqual(original);
		firstAuthority.close();

		const reopenedAuthority = authority(path);
		const reopened = store(reopenedAuthority);
		expect(await reopened.read(original.jobId)).toEqual(original);
		reopenedAuthority.close();
	});

	it("continues one pending provider attempt after restart and atomically exposes its bundle", async () => {
		const path = await databasePath();
		const firstAuthority = authority(path);
		const firstStore = store(firstAuthority);
		const firstService = service(firstStore, {
			start: async () => ({ status: "pending", handle: { ticket: "provider-ticket" } }),
		});
		expect(
			await firstService.submit("adapter", { jobId: "job", kind: "image", input: { prompt: "garden" } }),
		).toMatchObject({
			status: "pending",
			revision: "3",
		});
		firstAuthority.close();

		const secondAuthority = authority(path);
		const secondStore = store(secondAuthority);
		let observedHandle: unknown;
		const secondService = service(secondStore, {
			start: async () => {
				throw new Error("restart must reconcile the durable handle");
			},
			read: async (handle) => {
				observedHandle = handle;
				return {
					status: "succeeded",
					resources: [{ ordinal: 0, kind: "image", mediaType: "image/png", bytes: Uint8Array.of(1, 2, 3) }],
				};
			},
		});
		const completed = await secondService.reconcile({ jobId: "job", expectedRevision: "3" });
		expect(observedHandle).toEqual({ ticket: "provider-ticket" });
		expect(completed).toMatchObject({ status: "succeeded", revision: "4", bundleId: "bundle-1" });
		expect(await secondService.readBundle("job", "bundle-1")).toMatchObject({
			disposition: "complete",
			resources: [{ ordinal: 0, byteLength: 3, mediaType: "image/png" }],
		});
		secondAuthority.close();
	});

	it("routes every lifecycle operation through the adapter persisted before a cold restart", async () => {
		const path = await databasePath();
		const firstAuthority = authority(path);
		const horizontalStart = vi.fn(async () => ({ status: "pending" as const, handle: { ticket: "horizontal" } }));
		const verticalStart = vi.fn(async () => ({ status: "pending" as const, handle: { ticket: "vertical" } }));
		const firstService = multiService(store(firstAuthority), [
			{ adapterId: "ark-horizontal", admission: testAdmission(), provider: { start: horizontalStart } },
			{ adapterId: "ark-vertical", admission: testAdmission(), provider: { start: verticalStart } },
		]);
		await firstService.submit("ark-horizontal", { jobId: "horizontal-job", kind: "image", input: {} });
		await firstService.submit("ark-vertical", { jobId: "vertical-job", kind: "image", input: {} });
		firstAuthority.close();

		const secondAuthority = authority(path);
		const horizontalRead = vi.fn(async () => ({
			status: "succeeded" as const,
			resources: [{ ordinal: 0, kind: "image" as const, mediaType: "image/png", bytes: Uint8Array.of(1, 2, 3) }],
		}));
		const horizontalCancel = vi.fn(async () => ({ status: "confirmed" as const }));
		const restartedVerticalStart = vi.fn(async () => ({
			status: "pending" as const,
			handle: { ticket: "vertical-restarted" },
		}));
		const verticalRead = vi.fn(async () => ({ status: "failed" as const, failure: "provider" as const }));
		const verticalCancel = vi.fn(async () => ({ status: "confirmed" as const }));
		const restored = multiService(store(secondAuthority), [
			{
				adapterId: "ark-horizontal",
				admission: testAdmission(),
				provider: { start: vi.fn(), read: horizontalRead, cancel: horizontalCancel },
			},
			{
				adapterId: "ark-vertical",
				admission: testAdmission(),
				provider: { start: restartedVerticalStart, read: verticalRead, cancel: verticalCancel },
			},
		]);

		await expect(restored.read("horizontal-job")).resolves.toMatchObject({ status: "pending" });
		await expect(restored.read("vertical-job")).resolves.toMatchObject({ status: "pending" });
		const horizontal = await restored.reconcile({ jobId: "horizontal-job", expectedRevision: "3" });
		const bundle = await restored.readBundle("horizontal-job", horizontal.bundleId ?? "missing");
		const resource = bundle?.resources[0];
		if (!bundle || !resource) throw new Error("expected a persisted horizontal bundle");
		await expect(
			restored.selectResource({
				jobId: bundle.jobId,
				attemptId: bundle.attemptId,
				bundleId: bundle.bundleId,
				ordinal: resource.ordinal,
				resourceId: resource.resourceId,
			}),
		).resolves.toMatchObject({ resource: { ordinal: 0 } });
		const cancelled = await restored.cancel({ jobId: "vertical-job", expectedRevision: "3" });
		await expect(
			restored.restart({ jobId: "vertical-job", expectedRevision: cancelled.revision }),
		).resolves.toMatchObject({ status: "pending" });

		expect(horizontalRead).toHaveBeenCalledOnce();
		expect(horizontalCancel).not.toHaveBeenCalled();
		expect(verticalRead).not.toHaveBeenCalled();
		expect(verticalCancel).toHaveBeenCalledOnce();
		expect(restartedVerticalStart).toHaveBeenCalledOnce();
		secondAuthority.close();
	});

	it("fails closed after restart when a persisted adapter is removed from the host registry", async () => {
		const path = await databasePath();
		const firstAuthority = authority(path);
		const first = multiService(store(firstAuthority), [
			{
				adapterId: "removed-adapter",
				admission: testAdmission(),
				provider: { start: async () => ({ status: "pending", handle: { ticket: "durable" } }) },
			},
		]);
		await first.submit("removed-adapter", { jobId: "job", kind: "image", input: {} });
		firstAuthority.close();

		const secondAuthority = authority(path);
		const remainingProvider: MediaGenerationProvider = { start: vi.fn() };
		const restored = multiService(store(secondAuthority), [
			{ adapterId: "remaining-adapter", admission: testAdmission(), provider: remainingProvider },
		]);
		await expect(restored.read("job")).rejects.toMatchObject({
			name: "MediaJobAdapterUnavailableError",
			message: "The media adapter is unavailable.",
		});
		await expect(restored.reconcile({ jobId: "job" })).rejects.toBeInstanceOf(MediaJobAdapterUnavailableError);
		expect(remainingProvider.start).not.toHaveBeenCalled();
		secondAuthority.close();
	});

	it("preserves exact resource selection across a cold restart without provider or resource I/O", async () => {
		const path = await databasePath();
		const bytes = Uint8Array.of(7, 8, 9);
		const firstAuthority = authority(path);
		const firstService = service(store(firstAuthority), {
			start: vi.fn(async () => ({
				status: "succeeded" as const,
				resources: [
					{ ordinal: 0, kind: "image" as const, mediaType: "image/png", bytes },
					{ ordinal: 1, kind: "image" as const, mediaType: "image/png", bytes },
				],
			})),
		});
		const completed = await firstService.submit("adapter", { jobId: "job", kind: "image", input: {} });
		const bundle = await firstService.readBundle("job", completed.bundleId ?? "missing");
		const firstResource = bundle?.resources[0];
		const secondResource = bundle?.resources[1];
		if (!bundle || !firstResource || !secondResource) throw new Error("expected two persisted media resources");
		const selectors = [firstResource, secondResource].map((resource) => ({
			jobId: bundle.jobId,
			attemptId: bundle.attemptId,
			bundleId: bundle.bundleId,
			ordinal: resource.ordinal,
			resourceId: resource.resourceId,
		}));
		const warm = await Promise.all(selectors.map((selector) => firstService.selectResource(selector)));
		expect(warm.map((selection) => selection?.resource.ordinal)).toEqual([0, 1]);
		expect(firstResource.resourceId).toBe(secondResource.resourceId);
		firstAuthority.close();

		const secondAuthority = authority(path);
		const provider: MediaGenerationProvider = {
			start: vi.fn(async () => {
				throw new Error("selection must not call the provider");
			}),
		};
		const resources: MediaResourcePublisher = {
			publish: vi.fn(async () => {
				throw new Error("selection must not read or publish resource bytes");
			}),
		};
		const secondStore = store(secondAuthority);
		const restored = service(secondStore, provider, resources);
		const before = await secondStore.read("job");
		const cold = await Promise.all(selectors.map((selector) => restored.selectResource(selector)));

		expect(cold).toEqual(warm);
		expect(cold.every((selection) => Object.isFrozen(selection) && Object.isFrozen(selection?.resource))).toBe(true);
		expect(await secondStore.read("job")).toEqual(before);
		expect(provider.start).not.toHaveBeenCalled();
		expect(resources.publish).not.toHaveBeenCalled();
		secondAuthority.close();
	});

	it("matches in-memory create, missing, no-op, identity, and revision behavior", async () => {
		const sqlite = authority(await databasePath());
		const media = store(sqlite);
		const original = job();

		await media.create(original);
		await expect(media.create(original)).rejects.toBeInstanceOf(MediaJobConflictError);
		await expect(media.modify("missing", (current) => current)).rejects.toBeInstanceOf(MediaJobNotFoundError);

		const unchanged = await media.modify(original.jobId, (current) => {
			(current.request.input as { prompt: string }).prompt = "discarded";
			return current;
		});
		expect(unchanged).toEqual(original);

		const changed = await media.modify(original.jobId, (current) => ({
			...current,
			revision: "caller value is ignored",
			updatedAt: "2026-08-31T01:00:01.000Z",
		}));
		expect(changed.revision).toBe("2");
		expect(changed.updatedAt).toBe("2026-08-31T01:00:01.000Z");
		await expect(
			media.modify(original.jobId, (current) => ({ ...current, adapterId: "other" })),
		).rejects.toBeInstanceOf(MediaJobConflictError);
		await expect(media.modify(original.jobId, (current) => ({ ...current, jobId: "other" }))).rejects.toBeInstanceOf(
			MediaJobConflictError,
		);
		expect((await media.read(original.jobId))?.revision).toBe("2");
		sqlite.close();
	});

	it("rolls back a throwing change and preserves revision continuity across shared authorities", async () => {
		const path = await databasePath();
		const firstAuthority = authority(path);
		const secondAuthority = authority(path);
		const first = store(firstAuthority);
		const second = store(secondAuthority);
		await first.create(job());

		await expect(
			first.modify("job", () => {
				throw new Error("host callback failed");
			}),
		).rejects.toThrow("host callback failed");
		expect((await first.read("job"))?.revision).toBe("1");

		const seen: string[] = [];
		await first.modify("job", (current) => {
			seen.push(current.revision);
			return { ...current, updatedAt: "2026-08-31T01:00:01.000Z" };
		});
		await second.modify("job", (current) => {
			seen.push(current.revision);
			return { ...current, updatedAt: "2026-08-31T01:00:02.000Z" };
		});
		expect(seen).toEqual(["1", "2"]);
		expect((await first.read("job"))?.revision).toBe("3");
		firstAuthority.close();
		secondAuthority.close();
	});

	it("preflights the host byte budget on write and cold read", async () => {
		const path = await databasePath();
		const sqlite = authority(path);
		const media = new SqliteMediaJobStore(sqlite, { limits, maxStoredJobBytes: 64 });
		const oversized = job();
		(oversized.request.input as { prompt: string }).prompt = "x".repeat(1_024);
		let lateReads = 0;
		Object.defineProperty(oversized, "attempts", {
			enumerable: true,
			configurable: true,
			get: () => {
				lateReads += 1;
				return [];
			},
		});
		await expect(media.create(oversized)).rejects.toBeInstanceOf(SqliteMediaJobCapacityError);
		expect(lateReads).toBe(0);
		expect(await media.read("job")).toBeUndefined();
		sqlite.close();

		const writerAuthority = authority(path);
		await store(writerAuthority).create(job());
		writerAuthority.close();
		const constrainedAuthority = authority(path);
		const constrained = new SqliteMediaJobStore(constrainedAuthority, { limits, maxStoredJobBytes: 64 });
		await expect(constrained.read("job")).rejects.toBeInstanceOf(SqliteMediaJobCapacityError);
		let called = false;
		await expect(
			constrained.modify("job", (current) => {
				called = true;
				return current;
			}),
		).rejects.toBeInstanceOf(SqliteMediaJobCapacityError);
		expect(called).toBe(false);
		constrainedAuthority.close();
	});

	it("rejects unreachable attempt state before create or modify", async () => {
		const sqlite = authority(await databasePath());
		const stores: MediaJobStore[] = [new InMemoryMediaJobStore(limits), store(sqlite)];
		for (const media of stores) {
			const impossibleBase = job();
			const [impossibleAttempt] = impossibleBase.attempts;
			if (impossibleAttempt === undefined) throw new Error("The media job fixture must contain an attempt.");
			const impossible: StoredMediaJob = {
				...impossibleBase,
				attempts: [
					{
						...impossibleAttempt,
						status: "cancel_requested",
						disposition: "active",
						cancellation: "not_requested",
					},
				],
			};
			await expect(media.create(impossible)).rejects.toBeInstanceOf(MediaJobValidationError);

			const multipleActiveBase = job();
			const [multipleActiveAttempt] = multipleActiveBase.attempts;
			if (multipleActiveAttempt === undefined) {
				throw new Error("The media job fixture must contain an attempt.");
			}
			const multipleActive: StoredMediaJob = {
				...multipleActiveBase,
				currentAttemptId: "attempt-2",
				attempts: [
					multipleActiveAttempt,
					{
						...multipleActiveAttempt,
						attemptId: "attempt-2",
						ordinal: 1,
						submissionKey: "submission-2",
					},
				],
			};
			await expect(media.create(multipleActive)).rejects.toBeInstanceOf(MediaJobValidationError);
		}
		sqlite.close();
	});

	it("never invokes caller accessors or Proxy traps while capturing Store identity", async () => {
		const sqlite = authority(await databasePath());
		const stores: MediaJobStore[] = [new InMemoryMediaJobStore(limits), store(sqlite)];
		for (const media of stores) {
			let accessorReads = 0;
			const accessorJob = job();
			Object.defineProperty(accessorJob, "jobId", {
				enumerable: true,
				configurable: true,
				get: () => {
					accessorReads += 1;
					return `job-${accessorReads}`;
				},
			});
			await expect(media.create(accessorJob)).rejects.toBeInstanceOf(MediaJobValidationError);
			expect(accessorReads).toBe(0);

			let proxyReads = 0;
			const proxied = new Proxy(job(), {
				get: (target, key, receiver) => {
					proxyReads += 1;
					return Reflect.get(target, key, receiver);
				},
			});
			await expect(media.create(proxied)).rejects.toBeInstanceOf(MediaJobValidationError);
			expect(proxyReads).toBe(0);

			await media.create(job());
			const changed = job();
			Object.defineProperty(changed, "adapterId", {
				enumerable: true,
				configurable: true,
				get: () => {
					accessorReads += 1;
					return "other";
				},
			});
			await expect(media.modify("job", () => changed)).rejects.toBeInstanceOf(MediaJobValidationError);
			expect(accessorReads).toBe(0);

			const proxiedChanged = new Proxy(job(), {
				get: (target, key, receiver) => {
					proxyReads += 1;
					return Reflect.get(target, key, receiver);
				},
			});
			await expect(media.modify("job", () => proxiedChanged)).rejects.toBeInstanceOf(MediaJobValidationError);
			expect(proxyReads).toBe(0);
			expect((await media.read("job"))?.revision).toBe("1");
		}
		sqlite.close();
	});

	it("detects a changed durable snapshot before exposing it or invoking a modifier", async () => {
		const sqlite = authority(await databasePath());
		const media = store(sqlite);
		await media.create(job());
		rewriteJobJson(sqlite, (attempt) => {
			attempt.status = "invalid_status";
		});

		await expect(media.read("job")).rejects.toBeInstanceOf(SqliteMediaJobCorruptionError);
		let called = false;
		await expect(
			media.modify("job", (current) => {
				called = true;
				return current;
			}),
		).rejects.toBeInstanceOf(SqliteMediaJobCorruptionError);
		expect(called).toBe(false);
		sqlite.close();
	});

	it("rejects unreachable nonterminal states after a cold read even with recomputed integrity fields", async () => {
		const corruptions: ReadonlyArray<(attempt: Record<string, unknown>) => void> = [
			(attempt) => {
				attempt.status = "submitting";
			},
			(attempt) => {
				attempt.status = "settling";
				attempt.settlingStatus = "succeeded";
				attempt.outputSettlements = [{ ordinal: 0, status: "failed", failure: "provider" }];
			},
			(attempt) => {
				attempt.status = "settling";
				attempt.settlingStatus = "partially_succeeded";
				attempt.outputSettlements = [];
			},
		];
		for (const corrupt of corruptions) {
			const sqlite = authority(await databasePath());
			try {
				const media = store(sqlite);
				await media.create(job());
				rewriteJobJson(sqlite, corrupt);

				await expect(media.read("job")).rejects.toBeInstanceOf(SqliteMediaJobCorruptionError);
			} finally {
				sqlite.close();
			}
		}
	});

	it("fails explicitly when a changed job exhausts SQLite revision space", async () => {
		const sqlite = authority(await databasePath());
		const media = store(sqlite);
		await media.create(job());
		const maximum = "9223372036854775807";
		sqlite.write((database) => {
			const row = database.prepare("SELECT job_json FROM oga_media_jobs WHERE job_id = ?").get("job") as {
				job_json: string;
			};
			const parsed = JSON.parse(row.job_json) as StoredMediaJob;
			const jobJson = JSON.stringify({ ...parsed, revision: maximum });
			database
				.prepare(
					"UPDATE oga_media_jobs SET revision = ?, job_json = ?, job_bytes = ?, job_fingerprint = ? WHERE job_id = ?",
				)
				.run(
					BigInt(maximum),
					jobJson,
					Buffer.byteLength(jobJson),
					createHash("sha256").update(jobJson).digest("hex"),
					"job",
				);
		});

		await expect(
			media.modify("job", (current) => ({ ...current, updatedAt: "2026-08-31T01:00:01.000Z" })),
		).rejects.toBeInstanceOf(SqliteMediaJobRevisionExhaustedError);
		expect((await media.read("job"))?.revision).toBe(maximum);
		sqlite.close();
	});

	it("requires one explicit positive host byte budget", async () => {
		const sqlite = authority(await databasePath());
		for (const value of [0, -1, 1.5, Number.POSITIVE_INFINITY]) {
			expect(() => new SqliteMediaJobStore(sqlite, { limits, maxStoredJobBytes: value })).toThrow(RangeError);
		}
		sqlite.close();
	});
});

function store(sqlite: SqliteAuthority): SqliteMediaJobStore {
	return new SqliteMediaJobStore(sqlite, { limits, maxStoredJobBytes });
}

function testAdmission() {
	return new MediaCallAdmissionGroup({ maxConcurrentCalls: 4, maxQueuedCalls: 4 });
}

function service(
	storeValue: SqliteMediaJobStore,
	provider: MediaGenerationProvider,
	resources: MediaResourcePublisher = resourcePublisher(),
): MediaJobService {
	const counters = { attempt: 0, submission: 0, bundle: 0 };
	return new MediaJobService({
		adapters: [{ adapterId: "adapter", admission: testAdmission(), provider }],
		store: storeValue,
		resources,
		limits,
		newId: (kind) => `${kind}-${++counters[kind]}`,
		now: () => Date.parse("2026-08-31T01:00:00.000Z"),
	});
}

function multiService(
	storeValue: SqliteMediaJobStore,
	adapters: readonly Readonly<MediaGenerationAdapter>[],
	resources: MediaResourcePublisher = resourcePublisher(),
): MediaJobService {
	const counters = { attempt: 0, submission: 0, bundle: 0 };
	return new MediaJobService({
		adapters,
		store: storeValue,
		resources,
		limits,
		newId: (kind) => `${kind}-${++counters[kind]}`,
		now: () => Date.parse("2026-08-31T01:00:00.000Z"),
	});
}

function resourcePublisher(): MediaResourcePublisher {
	return {
		publish: async (resource) => {
			if (!(resource.bytes instanceof Uint8Array)) throw new Error("expected eager test bytes");
			const sha256 = createHash("sha256").update(resource.bytes).digest("hex");
			return {
				resourceId: sha256,
				kind: resource.kind,
				mediaType: resource.mediaType,
				byteLength: resource.bytes.byteLength,
				sha256,
				ordinal: resource.ordinal,
			};
		},
	};
}

function job(): StoredMediaJob {
	return {
		jobId: "job",
		adapterId: "adapter",
		request: { kind: "image", input: { prompt: "a garden" } },
		revision: "1",
		createdAt: "2026-08-31T01:00:00.000Z",
		updatedAt: "2026-08-31T01:00:00.000Z",
		currentAttemptId: "attempt",
		attempts: [
			{
				attemptId: "attempt",
				ordinal: 0,
				submissionKey: "submission",
				status: "pending",
				disposition: "active",
				cancellation: "not_requested",
				startedAt: "2026-08-31T01:00:00.000Z",
				updatedAt: "2026-08-31T01:00:00.000Z",
				providerHandle: { id: "provider-handle" },
				outputSettlements: [],
			},
		],
		bundles: [],
	};
}

function authority(path: string): SqliteAuthority {
	return new SqliteAuthority({ path, busyTimeoutMs: 1_000, verifyOnOpen: "full" });
}

function rewriteJobJson(sqlite: SqliteAuthority, change: (attempt: Record<string, unknown>) => void): void {
	sqlite.write((database) => {
		const row = database.prepare("SELECT job_json FROM oga_media_jobs WHERE job_id = ?").get("job") as {
			job_json: string;
		};
		const parsed = JSON.parse(row.job_json) as { attempts: Record<string, unknown>[] };
		const [attempt] = parsed.attempts;
		if (attempt === undefined) throw new Error("The stored media job fixture must contain an attempt.");
		change(attempt);
		const changed = canonicalizeJsonValue(parsed as unknown as JsonValue);
		database
			.prepare("UPDATE oga_media_jobs SET job_json = ?, job_bytes = ?, job_fingerprint = ? WHERE job_id = ?")
			.run(changed, Buffer.byteLength(changed), createHash("sha256").update(changed).digest("hex"), "job");
	});
}

async function databasePath(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-media-job-store-"));
	directories.push(directory);
	return join(directory, "authority.db");
}

import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type {
	GameModelRequestProvenance,
	GameModelResponseProvenance,
	GameProvenanceAlias,
} from "@opengameagent/agent";
import { afterEach, describe, expect, it } from "vitest";
import {
	FileModelContextProvenanceStore,
	InMemoryModelContextProvenanceStore,
	ModelContextProvenanceCapacityError,
	type ModelContextProvenanceLimits,
	ModelContextProvenanceRecorder,
	type ModelContextProvenanceStore,
	ModelContextProvenanceValidationError,
	ModelContextProvenanceWriteConflictError,
} from "../src/model-context-provenance.js";

const directories: string[] = [];
const limits = { maxRecords: 8, maxRecordBytes: 8_192, maxStoreBytes: 32_768 } as const;
const alias = (character: string): GameProvenanceAlias => `pv_${character.repeat(64)}` as GameProvenanceAlias;

afterEach(async () => {
	await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe("model-context provenance", () => {
	it("persists only reconstructable source metadata and redacted Pi response accounting", async () => {
		const store = new InMemoryModelContextProvenanceStore();
		const recorder = new ModelContextProvenanceRecorder({
			store,
			limits,
			newId: () => "local-request-1",
		});

		const requestId = await recorder.recordRequest(request());
		await recorder.recordResponse(requestId, response());

		const restored = await recorder.restore();
		expect(restored.records).toMatchObject([
			{
				type: "provider.request",
				recordId: expect.stringMatching(/^pv_[0-9a-f]{64}$/u),
				sequence: "1",
				operation: "inference",
				coordinate: {
					scopeId: alias("0"),
					streamId: alias("1"),
					runId: alias("2"),
					turn: "2",
				},
				requestedRoute: { provider: alias("3"), api: alias("4"), model: alias("5") },
				attempt: {
					index: "1",
					targetRoute: { provider: alias("3"), api: alias("4"), model: alias("5") },
				},
				modelContextFingerprint: "d".repeat(64),
				transcript: { revision: "7", checkpointRevision: "4" },
				lineage: {
					baseInstructions: {
						sourceId: alias("6"),
						referenceId: alias("7"),
						revision: alias("8"),
						contentFingerprint: "e".repeat(64),
					},
				},
				toolSnapshot: { revision: alias("9"), policyRevision: alias("a") },
				sources: [
					{
						kind: "memory",
						sourceId: alias("b"),
						references: [
							{
								referenceId: alias("c"),
								revision: alias("d"),
								contentFingerprint: "a".repeat(64),
							},
						],
					},
				],
				tools: [{ name: alias("e"), sourceId: alias("f"), fingerprint: "b".repeat(64) }],
				images: [
					{
						messageIndex: 2,
						contentIndex: 1,
						mediaType: "image/png",
						bytes: 3,
						sha256: "c".repeat(64),
					},
				],
			},
			{
				type: "provider.response",
				sequence: "2",
				requestRecordId: expect.stringMatching(/^pv_[0-9a-f]{64}$/u),
				servedModel: alias("0"),
				stopReason: "stop",
				cost: { kind: "unpriced" },
				usage: {
					kind: "reported",
					input: 11,
					output: 5,
					cacheRead: 3,
					cacheWrite: 0,
					cacheWrite1h: 0,
					reasoning: 2,
					totalTokens: 16,
				},
			},
		]);
		const encoded = JSON.stringify(restored);
		expect(restored.records[0]).not.toHaveProperty("deferred");
		for (const forbidden of [
			"secret prompt",
			"secret answer",
			"hidden reasoning",
			"tool arguments",
			"provider-response-id",
			"credential",
			"operation-id",
		]) {
			expect(encoded).not.toContain(forbidden);
		}
	});

	it("persists only aliased exact-repeat advisory evidence and immutable policy lineage", async () => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => "exact-repeat-request",
		});
		const base = request();
		const exactRequest: GameModelRequestProvenance = {
			...base,
			lineage: {
				...base.lineage,
				exactToolRepeatPolicy: {
					sourceId: alias("1"),
					referenceId: alias("2"),
					revision: alias("3"),
					contentFingerprint: "4".repeat(64),
				},
			},
			policyContext: {
				exactToolRepeat: {
					eventId: alias("5"),
					eventRevision: "9",
					toolName: alias("6"),
					consecutiveCount: "2",
					contentFingerprint: "7".repeat(64),
				},
			},
		};

		await recorder.recordRequest(exactRequest);

		expect((await recorder.restore()).records[0]).toMatchObject({
			lineage: { exactToolRepeatPolicy: exactRequest.lineage.exactToolRepeatPolicy },
			policyContext: exactRequest.policyContext,
		});
		const exactEvidence = exactRequest.policyContext?.exactToolRepeat;
		if (!exactEvidence) throw new Error("Expected exact-repeat evidence fixture.");
		await expect(
			recorder.recordRequest({
				...exactRequest,
				policyContext: {
					exactToolRepeat: { ...exactEvidence, consecutiveCount: "02" },
				},
			}),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
		await expect(recorder.recordRequest({ ...exactRequest, lineage: base.lineage })).rejects.toBeInstanceOf(
			ModelContextProvenanceValidationError,
		);
	});

	it.each(["pi-catalog", "host-rate-card"] as const)("persists a %s calculated USD cost", async (source) => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => `${source}-cost`,
		});
		const requestId = await recorder.recordRequest(request());
		const cost = {
			kind: "calculated" as const,
			source,
			usd: { input: 0.1, output: 0.2, cacheRead: 0.03, cacheWrite: 0.04, total: 0.37 },
		};

		await recorder.recordResponse(requestId, { ...response(), cost });

		expect((await recorder.restore()).records).toMatchObject([{ type: "provider.request" }, { cost }]);
	});

	it.each(["pi-catalog", "host-rate-card"] as const)(
		"persists unreported failed usage with unknown %s cost",
		async (source) => {
			const recorder = new ModelContextProvenanceRecorder({
				store: new InMemoryModelContextProvenanceStore(),
				limits,
				newId: () => `${source}-unknown-cost`,
			});
			const requestId = await recorder.recordRequest(request());
			const failed: GameModelResponseProvenance = {
				...response(),
				stopReason: "error",
				outcome: "failed",
				errorKind: "provider",
				cost: { kind: "unknown", source },
				usage: { kind: "unreported" },
			};

			await recorder.recordResponse(requestId, failed);

			expect((await recorder.restore()).records).toMatchObject([
				{ type: "provider.request" },
				{ cost: { kind: "unknown", source }, usage: { kind: "unreported" } },
			]);
		},
	);

	it.each([
		[
			"calculated cost without reported usage",
			{
				...response(),
				stopReason: "error",
				outcome: "failed",
				errorKind: "provider",
				cost: {
					kind: "calculated",
					source: "pi-catalog",
					usd: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				},
				usage: { kind: "unreported" },
			},
		],
		["unknown cost with reported usage", { ...response(), cost: { kind: "unknown", source: "host-rate-card" } }],
		["unreported usage on a successful response", { ...response(), usage: { kind: "unreported" } }],
		[
			"an unknown cost carrying a USD value",
			{
				...response(),
				stopReason: "error",
				outcome: "failed",
				errorKind: "provider",
				cost: {
					kind: "unknown",
					source: "pi-catalog",
					usd: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				},
				usage: { kind: "unreported" },
			},
		],
		[
			"numeric fields on unreported usage",
			{
				...response(),
				stopReason: "error",
				outcome: "failed",
				errorKind: "provider",
				usage: { kind: "unreported", input: 0 },
			},
		],
	] as const)("rejects %s", async (_name, invalidResponse) => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => "invalid-response-combination",
		});
		const requestId = await recorder.recordRequest(request());

		await expect(recorder.recordResponse(requestId, invalidResponse as never)).rejects.toBeInstanceOf(
			ModelContextProvenanceValidationError,
		);
	});

	it("restores unreported usage and unknown cost from the current file schema", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-provenance-unreported-"));
		directories.push(directory);
		const path = join(directory, "provenance.json");
		const first = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
			newId: () => "unreported-file",
		});
		const requestId = await first.recordRequest(request());
		await first.recordResponse(requestId, {
			...response(),
			stopReason: "error",
			outcome: "failed",
			errorKind: "provider",
			cost: { kind: "unknown", source: "pi-catalog" },
			usage: { kind: "unreported" },
		});
		const restarted = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
		});

		expect(await restarted.restore()).toMatchObject({
			schemaVersion: "4",
			records: [{ type: "provider.request" }, { cost: { kind: "unknown" }, usage: { kind: "unreported" } }],
		});
	});

	it("persists an explicitly free zero USD cost", async () => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => "free-cost",
		});
		const requestId = await recorder.recordRequest(request());
		const cost = {
			kind: "free" as const,
			usd: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		};

		await recorder.recordResponse(requestId, { ...response(), cost });

		expect((await recorder.restore()).records).toMatchObject([{ type: "provider.request" }, { cost }]);
	});

	it("persists an unpriced cost without a USD amount", async () => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => "unpriced-cost",
		});
		const requestId = await recorder.recordRequest(request());

		await recorder.recordResponse(requestId, response());

		expect((await recorder.restore()).records).toMatchObject([
			{ type: "provider.request" },
			{ cost: { kind: "unpriced" } },
		]);
	});

	it.each([
		{
			kind: "free" as const,
			usd: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		},
		{ kind: "unpriced" as const },
	])("keeps $kind pricing explicit when failed usage is unreported", async (cost) => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => `${cost.kind}-unreported`,
		});
		const requestId = await recorder.recordRequest(request());

		await recorder.recordResponse(requestId, {
			...response(),
			stopReason: "error",
			outcome: "failed",
			errorKind: "provider",
			cost,
			usage: { kind: "unreported" },
		});

		expect((await recorder.restore()).records).toMatchObject([
			{ type: "provider.request" },
			{ cost, usage: { kind: "unreported" } },
		]);
	});

	it("allows only the next settled provider attempt for the same coordinate", async () => {
		let id = 0;
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => `attempt-${++id}`,
		});
		const firstRequestId = await recorder.recordRequest(request());
		const secondAttempt = {
			...request(),
			attempt: {
				index: "2",
				targetRoute: { provider: alias("a"), api: alias("b"), model: alias("c") },
			},
		};

		await expect(recorder.recordRequest(secondAttempt)).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
		await recorder.recordResponse(firstRequestId, response());
		await expect(
			recorder.recordRequest({
				...secondAttempt,
				requestedRoute: { ...secondAttempt.requestedRoute, model: alias("e") },
			}),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
		await expect(
			recorder.recordRequest({ ...secondAttempt, modelContextFingerprint: "e".repeat(64) }),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
		const secondRequestId = await recorder.recordRequest(secondAttempt);
		await recorder.recordResponse(secondRequestId, { ...response(), servedModel: alias("d") });

		expect((await recorder.restore()).records).toMatchObject([
			{ type: "provider.request", attempt: { index: "1" } },
			{ type: "provider.response", servedModel: alias("0") },
			{
				type: "provider.request",
				attempt: {
					index: "2",
					targetRoute: { provider: alias("a"), api: alias("b"), model: alias("c") },
				},
			},
			{ type: "provider.response", servedModel: alias("d") },
		]);
	});

	it("requires attempt one to open a provider coordinate", async () => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
		});

		await expect(recorder.recordRequest(requestForAttempt("2"))).rejects.toBeInstanceOf(
			ModelContextProvenanceValidationError,
		);
	});

	it.each([
		["repeated", "1"],
		["skipped", "3"],
	] as const)("rejects a %s provider attempt index after settlement", async (_label, index) => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => `invalid-attempt-${index}`,
		});
		const firstRequestId = await recorder.recordRequest(request());
		await recorder.recordResponse(firstRequestId, response());

		await expect(recorder.recordRequest(requestForAttempt(index))).rejects.toBeInstanceOf(
			ModelContextProvenanceValidationError,
		);
	});

	it("serializes complete recorder transactions for concurrent actors", async () => {
		let id = 0;
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => `request-${++id}`,
		});

		const [first, second] = await Promise.all([
			recorder.recordRequest(request()),
			recorder.recordRequest(requestForTurn("3")),
		]);
		const [, third] = await Promise.all([
			recorder.recordResponse(first, response()),
			recorder.recordRequest(requestForTurn("4")),
			recorder.recordResponse(second, response()),
		]);
		await recorder.recordResponse(third, response());

		const restored = await recorder.restore();
		expect(restored.records.map(({ sequence }) => sequence)).toEqual(["1", "2", "3", "4", "5", "6"]);
		expect(restored.records.filter(({ type }) => type === "provider.request")).toHaveLength(3);
		expect(restored.records.filter(({ type }) => type === "provider.response")).toHaveLength(3);
	});

	it("rejects a Store that falsely acknowledges a request without committing it", async () => {
		const empty = { schemaVersion: "4" as const, revision: "empty" as const, records: [] };
		const store: ModelContextProvenanceStore = {
			load: async () => empty,
			append: async (_expectedRevision, record) => {
				if (record.type !== "provider.request") throw new Error("Expected a request record.");
				return {
					schemaVersion: "4",
					revision: record.sequence,
					records: [{ ...record, recordId: alias("f") }],
				};
			},
		};
		const recorder = new ModelContextProvenanceRecorder({ store, limits, newId: () => "false-request-ack" });

		await expect(recorder.recordRequest(request())).rejects.toThrow("did not confirm");
		expect((await recorder.restore()).records).toHaveLength(0);
	});

	it("rejects a Store that mutates the request append input before acknowledging it", async () => {
		let loadCalls = 0;
		let appendCalls = 0;
		const empty = { schemaVersion: "4" as const, revision: "empty" as const, records: [] };
		const store: ModelContextProvenanceStore = {
			load: async () => {
				loadCalls += 1;
				return empty;
			},
			append: async (_expectedRevision, record) => {
				appendCalls += 1;
				if (record.type !== "provider.request") throw new Error("Expected a request record.");
				(record.coordinate as { turn: string }).turn = "9";
				return { schemaVersion: "4", revision: record.sequence, records: [record] };
			},
		};
		const recorder = new ModelContextProvenanceRecorder({ store, limits, newId: () => "mutated-request-ack" });

		await expect(recorder.recordRequest(request())).rejects.toThrow("did not confirm");
		expect({ appendCalls, loadCalls }).toEqual({ appendCalls: 1, loadCalls: 1 });
		expect((await recorder.restore()).records).toHaveLength(0);
		expect(loadCalls).toBe(2);
	});

	it("rejects a Store that falsely acknowledges a response without committing it", async () => {
		const backing = new InMemoryModelContextProvenanceStore();
		const store: ModelContextProvenanceStore = {
			load: (maxBytes) => backing.load(maxBytes),
			append: async (expectedRevision, record) =>
				record.type === "provider.request"
					? backing.append(expectedRevision, record)
					: backing.load(limits.maxStoreBytes),
		};
		const recorder = new ModelContextProvenanceRecorder({ store, limits, newId: () => "false-response-ack" });
		const requestId = await recorder.recordRequest(request());

		await expect(recorder.recordResponse(requestId, response())).rejects.toThrow("did not confirm");
		expect((await recorder.restore()).records).toHaveLength(1);
	});

	it("rejects a Store that mutates the response append input before acknowledging it", async () => {
		let loadCalls = 0;
		let appendCalls = 0;
		let state = { schemaVersion: "4" as const, revision: "empty" as "empty" | string, records: [] as unknown[] };
		const store: ModelContextProvenanceStore = {
			load: async () => {
				loadCalls += 1;
				return structuredClone(state) as never;
			},
			append: async (_expectedRevision, record) => {
				appendCalls += 1;
				if (record.type === "provider.response") {
					if (record.cost.kind !== "calculated") throw new Error("Expected calculated response cost.");
					(record.cost.usd as { input: number }).input = 99;
				}
				state = {
					schemaVersion: "4",
					revision: record.sequence,
					records: [...state.records, record],
				};
				return state as never;
			},
		};
		const recorder = new ModelContextProvenanceRecorder({ store, limits, newId: () => "mutated-response-ack" });
		const requestId = await recorder.recordRequest(request());
		const input = {
			...response(),
			cost: {
				kind: "calculated" as const,
				source: "pi-catalog" as const,
				usd: { input: 0.1, output: 0.2, cacheRead: 0.03, cacheWrite: 0.04, total: 0.37 },
			},
		};

		await expect(recorder.recordResponse(requestId, input)).rejects.toThrow("did not confirm");
		expect({ appendCalls, loadCalls }).toEqual({ appendCalls: 2, loadCalls: 1 });
		expect((await recorder.restore()).records).toMatchObject([
			{ type: "provider.request" },
			{ cost: { kind: "calculated", usd: { input: 99 } } },
		]);
		expect(loadCalls).toBe(2);
	});

	it("captures the request at method entry before waiting for Store load", async () => {
		const backing = new InMemoryModelContextProvenanceStore();
		const loadStarted = Promise.withResolvers<void>();
		const releaseLoad = Promise.withResolvers<void>();
		const store: ModelContextProvenanceStore = {
			load: async (maxBytes) => {
				loadStarted.resolve();
				await releaseLoad.promise;
				return await backing.load(maxBytes);
			},
			append: (expectedRevision, record) => backing.append(expectedRevision, record),
		};
		const recorder = new ModelContextProvenanceRecorder({ store, limits, newId: () => "request-entry-snapshot" });
		const input = request();

		const pending = recorder.recordRequest(input);
		await loadStarted.promise;
		(input.coordinate as { turn: string }).turn = "9";
		releaseLoad.resolve();
		await pending;

		expect((await recorder.restore()).records).toMatchObject([{ coordinate: { turn: "2" } }]);
	});

	it("captures the response at method entry before waiting for the recorder mutex", async () => {
		const backing = new InMemoryModelContextProvenanceStore();
		const appendStarted = Promise.withResolvers<void>();
		const releaseAppend = Promise.withResolvers<void>();
		let blockNextRequest = false;
		const store: ModelContextProvenanceStore = {
			load: (maxBytes) => backing.load(maxBytes),
			append: async (expectedRevision, record) => {
				if (blockNextRequest && record.type === "provider.request") {
					appendStarted.resolve();
					await releaseAppend.promise;
				}
				return await backing.append(expectedRevision, record);
			},
		};
		let id = 0;
		const recorder = new ModelContextProvenanceRecorder({ store, limits, newId: () => `entry-snapshot-${++id}` });
		const requestId = await recorder.recordRequest(request());
		blockNextRequest = true;
		const blockingRequest = recorder.recordRequest(requestForTurn("3"));
		await appendStarted.promise;
		const input: GameModelResponseProvenance = {
			...response(),
			cost: {
				kind: "calculated",
				source: "host-rate-card",
				usd: { input: 0.1, output: 0.2, cacheRead: 0.03, cacheWrite: 0.04, total: 0.37 },
			},
		};

		const pending = recorder.recordResponse(requestId, input);
		(input.usage as { kind: "reported"; input: number }).input = 99;
		if (input.cost.kind !== "calculated") throw new Error("Expected calculated response cost.");
		(input.cost.usd as { input: number }).input = 99;
		releaseAppend.resolve();
		await blockingRequest;
		await pending;

		const storedResponse = (await recorder.restore()).records.find(
			(record) => record.type === "provider.response" && record.requestRecordId === requestId,
		);
		expect(storedResponse).toMatchObject({ cost: { usd: { input: 0.1 } }, usage: { kind: "reported", input: 11 } });
	});

	it("detaches the recorder cache from a Store result retained and mutated after append", async () => {
		let loadCalls = 0;
		let retained: Parameters<ModelContextProvenanceStore["append"]>[1] | undefined;
		const store: ModelContextProvenanceStore = {
			load: async () => {
				loadCalls += 1;
				return { schemaVersion: "4", revision: "empty", records: [] };
			},
			append: async (_expectedRevision, record) => {
				retained = record;
				return { schemaVersion: "4", revision: record.sequence, records: [record] };
			},
		};
		const recorder = new ModelContextProvenanceRecorder({ store, limits, newId: () => "retained-store-result" });

		await recorder.recordRequest(request());
		if (retained?.type !== "provider.request") throw new Error("Expected a retained request record.");
		(retained.coordinate as { turn: string }).turn = "9";

		expect((await recorder.restore()).records).toMatchObject([{ coordinate: { turn: "2" } }]);
		expect(loadCalls).toBe(1);
	});

	it("keeps same-path writers atomic while surfacing cross-recorder conflicts", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-provenance-conflict-"));
		directories.push(directory);
		const path = join(directory, "provenance.json");
		const createRecorder = (id: string) =>
			new ModelContextProvenanceRecorder({
				store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
				limits,
				newId: () => id,
			});
		const first = createRecorder("writer-1");
		const second = createRecorder("writer-2");
		const results = await Promise.allSettled([first.recordRequest(request()), second.recordRequest(request())]);
		expect(results.filter(({ status }) => status === "fulfilled")).toHaveLength(1);
		const rejection = results.find(({ status }) => status === "rejected");
		expect(rejection).toMatchObject({ reason: expect.any(ModelContextProvenanceWriteConflictError) });
		const rejectedIndex = results.findIndex(({ status }) => status === "rejected");
		const rejectedRecorder = [first, second][rejectedIndex];
		if (!rejectedRecorder) throw new Error("Expected one rejected recorder.");
		expect((await rejectedRecorder.restore()).records).toHaveLength(1);
		expect((await createRecorder("reader").restore()).records).toHaveLength(1);
	});

	it("restores an atomic file ledger and rejects corrupt or mismatched response records", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-provenance-"));
		directories.push(directory);
		const path = join(directory, "provenance.json");
		const first = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
			newId: () => "request-file",
		});
		const requestId = await first.recordRequest(request());

		const second = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
			newId: () => "unused",
		});
		await second.recordResponse(requestId, {
			...response(),
			cost: {
				kind: "calculated",
				source: "host-rate-card",
				usd: { input: 0.1, output: 0.2, cacheRead: 0.03, cacheWrite: 0.04, total: 0.37 },
			},
		});
		expect((await second.restore()).records).toHaveLength(2);
		expect(JSON.parse(await readFile(path, "utf8"))).toMatchObject({
			schemaVersion: "4",
			revision: "2",
			records: [{ type: "provider.request" }, { cost: { kind: "calculated", source: "host-rate-card" } }],
		});

		await expect(second.recordResponse("missing" as GameProvenanceAlias, response())).rejects.toBeInstanceOf(
			ModelContextProvenanceValidationError,
		);
		await expect(second.recordResponse(requestId, response())).rejects.toBeInstanceOf(
			ModelContextProvenanceValidationError,
		);

		await writeFile(path, '{"schemaVersion":"4","revision":"2","records":[', "utf8");
		const corrupt = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
		});
		await expect(corrupt.restore()).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
	});

	it("rejects earlier schema ledgers instead of migrating them", async () => {
		const recorder = new ModelContextProvenanceRecorder({
			store: {
				load: async () => ({ schemaVersion: "2", revision: "empty", records: [] }) as never,
				append: async () => {
					throw new Error("append must not run");
				},
			},
			limits,
		});

		await expect(recorder.restore()).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
	});

	it("uses only host-supplied record and storage limits", async () => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits: { ...limits, maxRecords: 1 },
			newId: () => "bounded",
		});
		const requestId = await recorder.recordRequest(request());
		await expect(recorder.recordResponse(requestId, response())).rejects.toThrow("record capacity");

		const oversized = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits: { ...limits, maxRecordBytes: 1 },
			newId: () => "oversized",
		});
		await expect(oversized.recordRequest(request())).rejects.toThrow("record byte");
	});

	it("accepts exact byte budgets and rejects the same documents at one byte less", async () => {
		const seed = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => "boundary",
		});
		await seed.recordRequest(request());
		const seeded = await seed.restore();
		const recordBytes = Buffer.byteLength(JSON.stringify(seeded.records[0]), "utf8");
		const storeBytes = Buffer.byteLength(JSON.stringify(seeded), "utf8");
		const createBoundaryRecorder = (overrides: Partial<ModelContextProvenanceLimits>) =>
			new ModelContextProvenanceRecorder({
				store: new InMemoryModelContextProvenanceStore(),
				limits: { ...limits, ...overrides },
				newId: () => "boundary",
			});

		await expect(
			createBoundaryRecorder({ maxRecordBytes: recordBytes, maxStoreBytes: storeBytes }).recordRequest(request()),
		).resolves.toMatch(/^pv_/u);
		await expect(
			createBoundaryRecorder({ maxRecordBytes: recordBytes - 1, maxStoreBytes: storeBytes }).recordRequest(request()),
		).rejects.toThrow("record byte");
		await expect(
			createBoundaryRecorder({ maxRecordBytes: recordBytes, maxStoreBytes: storeBytes - 1 }).recordRequest(request()),
		).rejects.toThrow("store byte");

		const directory = await mkdtemp(join(tmpdir(), "oga-provenance-exact-"));
		directories.push(directory);
		const path = join(directory, "provenance.json");
		await writeFile(path, JSON.stringify(seeded), "utf8");
		await expect(
			new FileModelContextProvenanceStore({ path, maxDocumentBytes: storeBytes }).load(storeBytes),
		).resolves.toMatchObject({ revision: "1" });
		await expect(
			new FileModelContextProvenanceStore({ path, maxDocumentBytes: storeBytes - 1 }).load(storeBytes),
		).rejects.toBeInstanceOf(ModelContextProvenanceCapacityError);
	});

	it("rejects non-allowlisted request and response metadata", async () => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => "request",
		});
		await expect(recorder.recordRequest({ ...request(), prompt: "secret" } as never)).rejects.toBeInstanceOf(
			ModelContextProvenanceValidationError,
		);
		await expect(
			recorder.recordRequest({
				...request(),
				attempt: { ...request().attempt, priorIndex: "0" },
			} as never),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
		const invalidRoute = request();
		await expect(
			recorder.recordRequest({
				...invalidRoute,
				attempt: {
					...invalidRoute.attempt,
					targetRoute: { ...invalidRoute.attempt.targetRoute, model: "raw-model-id" },
				},
			} as never),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
		expect((await recorder.restore()).records).toHaveLength(0);

		const requestId = await recorder.recordRequest(request());
		await expect(
			recorder.recordResponse(requestId, { ...response(), provider: "other" } as never),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
		await expect(
			recorder.recordResponse(requestId, {
				...response(),
				stopReason: "error",
			} as never),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
		expect((await recorder.restore()).records).toHaveLength(1);
	});

	it.each([
		[
			"USD data on an unpriced cost",
			{ kind: "unpriced", usd: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
		],
		[
			"a non-zero explicitly free cost",
			{ kind: "free", usd: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0.01 } },
		],
		[
			"a calculated cost without its source",
			{ kind: "calculated", usd: { input: 0.1, output: 0.2, cacheRead: 0.03, cacheWrite: 0.04, total: 0.37 } },
		],
		[
			"an unknown calculated cost source",
			{
				kind: "calculated",
				source: "provider-report",
				usd: { input: 0.1, output: 0.2, cacheRead: 0.03, cacheWrite: 0.04, total: 0.37 },
			},
		],
		[
			"an incomplete calculated USD amount",
			{
				kind: "calculated",
				source: "pi-catalog",
				usd: { input: 0.1, output: 0.2, cacheRead: 0.03, cacheWrite: 0.04 },
			},
		],
		["an unknown cost key", { kind: "unpriced", note: "not allowed" }],
		[
			"an unknown USD key",
			{
				kind: "calculated",
				source: "host-rate-card",
				usd: {
					input: 0.1,
					output: 0.2,
					cacheRead: 0.03,
					cacheWrite: 0.04,
					total: 0.37,
					currency: "USD",
				},
			},
		],
	] as const)("rejects %s", async (_label, cost) => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => "invalid-cost",
		});
		const requestId = await recorder.recordRequest(request());

		await expect(recorder.recordResponse(requestId, { ...response(), cost } as never)).rejects.toBeInstanceOf(
			ModelContextProvenanceValidationError,
		);
		expect((await recorder.restore()).records).toHaveLength(1);
	});

	it("uses the protocol timestamp domain for request and response records", async () => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
			newId: () => "timestamp-domain",
		});
		await expect(
			recorder.recordRequest({ ...request(), startedAt: "+010000-01-01T00:00:00.000Z" }),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
		const requestId = await recorder.recordRequest(request());
		await expect(
			recorder.recordResponse(requestId, { ...response(), completedAt: "-000001-01-01T00:00:00.000Z" }),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
	});

	it.each([
		["Windows path", "C:\\Users\\secret\\save.json"],
		["POSIX path", "/srv/private/save.json"],
		["URL", "https://private.invalid/a"],
		["credential", "sk-live-secret"],
		["email account", "owner@example.invalid"],
	])("rejects a raw %s in any provenance identity field", async (_label, unsafe) => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
		});
		const source = request().sources[0];
		if (!source) throw new Error("Expected a source fixture.");
		await expect(
			recorder.recordRequest({
				...request(),
				sources: [{ ...source, sourceId: unsafe }],
			} as never),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
	});

	it.each([
		["Windows path", "C:\\Users\\secret\\image.png"],
		["POSIX path", "/srv/private/image.png"],
		["URL", "https://private.invalid/a"],
		["credential", "sk-live-secret"],
		["unsupported MIME type", "image/svg+xml"],
	])("rejects a raw %s in image mediaType", async (_label, unsafe) => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
		});
		const image = request().images[0];
		if (!image) throw new Error("Expected an image fixture.");
		await expect(
			recorder.recordRequest({
				...request(),
				images: [{ ...image, mediaType: unsafe }],
			} as never),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
	});

	it("rejects a checkpoint revision newer than its provider-visible transcript revision", async () => {
		const recorder = new ModelContextProvenanceRecorder({
			store: new InMemoryModelContextProvenanceStore(),
			limits,
		});
		await expect(
			recorder.recordRequest({
				...request(),
				transcript: { revision: "7", checkpointRevision: "8" },
			}),
		).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
	});

	it("rejects an oversized file before parsing it", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-provenance-bounded-"));
		directories.push(directory);
		const path = join(directory, "provenance.json");
		await writeFile(path, "x".repeat(limits.maxStoreBytes + 1), "utf8");
		const recorder = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
		});
		await expect(recorder.restore()).rejects.toBeInstanceOf(ModelContextProvenanceCapacityError);
	});

	it.each([
		[
			"sequence gap",
			(state: PersistedState): void => {
				recordAt(state, 0).sequence = "2";
			},
		],
		[
			"response before request",
			(state: PersistedState) => {
				state.records.reverse();
				recordAt(state, 0).sequence = "1";
				recordAt(state, 1).sequence = "2";
			},
		],
		[
			"unknown persisted key",
			(state: PersistedState): void => {
				recordAt(state, 0).prompt = "secret";
			},
		],
		[
			"malformed persisted cost",
			(state: PersistedState): void => {
				const responseRecord = recordAt(state, 1);
				responseRecord.cost = {
					kind: "unpriced",
					usd: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				};
			},
		],
		[
			"missing persisted response reference",
			(state: PersistedState): void => {
				recordAt(state, 1).requestRecordId = alias("a");
			},
		],
		[
			"non-one first attempt",
			(state: PersistedState): void => {
				const attempt = recordAt(state, 0).attempt as Record<string, unknown>;
				attempt.index = "2";
			},
		],
	] as const)("rejects a persisted %s", async (_label, mutate) => {
		const directory = await mkdtemp(join(tmpdir(), "oga-provenance-invalid-"));
		directories.push(directory);
		const path = join(directory, "provenance.json");
		const writer = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
			newId: () => "persisted-invalid",
		});
		const requestId = await writer.recordRequest(request());
		await writer.recordResponse(requestId, response());
		const state = JSON.parse(await readFile(path, "utf8")) as PersistedState;
		mutate(state);
		await writeFile(path, JSON.stringify(state), "utf8");
		const reader = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
		});
		await expect(reader.restore()).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
	});

	it.each([
		[
			"skipped next attempt",
			(state: PersistedState): void => {
				const attempt = recordAt(state, 2).attempt as Record<string, unknown>;
				attempt.index = "3";
			},
		],
		[
			"next attempt before prior response",
			(state: PersistedState): void => {
				const priorResponse = recordAt(state, 1);
				const nextRequest = recordAt(state, 2);
				state.records[1] = { ...nextRequest, sequence: "2" };
				state.records[2] = { ...priorResponse, sequence: "3" };
			},
		],
	] as const)("rejects a persisted %s", async (_label, mutate) => {
		const directory = await mkdtemp(join(tmpdir(), "oga-provenance-attempt-invalid-"));
		directories.push(directory);
		const path = join(directory, "provenance.json");
		let id = 0;
		const writer = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
			newId: () => `persisted-attempt-${++id}`,
		});
		const firstRequestId = await writer.recordRequest(request());
		await writer.recordResponse(firstRequestId, response());
		const secondRequestId = await writer.recordRequest(requestForAttempt("2"));
		await writer.recordResponse(secondRequestId, response());
		const state = JSON.parse(await readFile(path, "utf8")) as PersistedState;
		mutate(state);
		await writeFile(path, JSON.stringify(state), "utf8");

		const reader = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: limits.maxStoreBytes }),
			limits,
		});
		await expect(reader.restore()).rejects.toBeInstanceOf(ModelContextProvenanceValidationError);
	});
});

interface PersistedState {
	revision: string;
	records: Array<Record<string, unknown> & { sequence: string }>;
}

function recordAt(state: PersistedState, index: number): PersistedState["records"][number] {
	const record = state.records[index];
	if (!record) throw new Error("Expected a persisted record fixture.");
	return record;
}

function request(): GameModelRequestProvenance {
	return {
		operation: "inference" as const,
		coordinate: { scopeId: alias("0"), streamId: alias("1"), runId: alias("2"), turn: "2" },
		startedAt: "2026-08-30T00:00:00.000Z",
		requestedRoute: { provider: alias("3"), api: alias("4"), model: alias("5") },
		attempt: {
			index: "1",
			targetRoute: { provider: alias("3"), api: alias("4"), model: alias("5") },
		},
		modelContextFingerprint: "d".repeat(64),
		transcript: { revision: "7", checkpointRevision: "4" },
		lineage: {
			baseInstructions: {
				sourceId: alias("6"),
				referenceId: alias("7"),
				revision: alias("8"),
				contentFingerprint: "e".repeat(64),
			},
		},
		toolSnapshot: { revision: alias("9"), policyRevision: alias("a") },
		sources: [
			{
				kind: "memory" as const,
				sourceId: alias("b"),
				references: [{ referenceId: alias("c"), revision: alias("d"), contentFingerprint: "a".repeat(64) }],
			},
		],
		tools: [{ name: alias("e"), sourceId: alias("f"), fingerprint: "b".repeat(64) }],
		images: [
			{
				messageIndex: 2,
				contentIndex: 1,
				mediaType: "image/png",
				bytes: 3,
				sha256: "c".repeat(64),
			},
		],
	};
}

function requestForTurn(turn: string): GameModelRequestProvenance {
	const value = request();
	return { ...value, coordinate: { ...value.coordinate, turn } };
}

function requestForAttempt(index: string): GameModelRequestProvenance {
	const value = request();
	return { ...value, attempt: { ...value.attempt, index } };
}

function response(): GameModelResponseProvenance {
	return {
		completedAt: "2026-08-30T00:00:01.000Z",
		servedModel: alias("0"),
		stopReason: "stop" as const,
		outcome: "completed" as const,
		errorKind: "none" as const,
		cost: { kind: "unpriced" as const },
		usage: {
			kind: "reported" as const,
			input: 11,
			output: 5,
			cacheRead: 3,
			cacheWrite: 0,
			cacheWrite1h: 0,
			reasoning: 2,
			totalTokens: 16,
		},
	};
}

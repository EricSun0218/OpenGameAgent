import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import type { GameSessionKey } from "@opengameagent/protocol";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { GameMediaAssetImportContext, GameMediaAssetImporter, GameMediaAssetRequest } from "./durable-media.js";
import {
	createDurableGameMediaTool,
	createGameMediaAssetOperationId,
	createGameMediaImportOperationId,
	DurableGameMediaPipeline,
	SqliteGameMediaAssetJobStore,
} from "./durable-media.js";
import type { GameMediaGenerationResult, GameMediaGenerator } from "./media.js";
import { GameMediaRegistry } from "./media.js";
import { FileGameMediaResourceStore } from "./resource-store.js";

const roots: string[] = [];

afterEach(async () => {
	await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

function session(actorId = "npc-a"): GameSessionKey {
	return {
		worldId: "world",
		saveId: "save",
		timelineId: "timeline",
		generation: 3,
		ownerId: "owner",
		sessionId: "session",
		actorId,
	};
}

function png(): Uint8Array {
	return Uint8Array.from([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0]);
}

function generated(): GameMediaGenerationResult {
	return {
		outputs: [{ kind: "image", mimeType: "image/png", data: png(), name: "portrait.png" }],
		provider: "test-provider",
		model: "test-model",
		responseId: "provider-response",
	};
}

function request(operationId = "asset-operation", actorId = "npc-a"): GameMediaAssetRequest {
	const owner = session(actorId);
	return {
		operationId,
		session: owner,
		assetType: "portrait",
		provider: "test-provider",
		model: "test-model",
		importerId: "engine-importer",
		expectedRevision: 41,
		generation: {
			id: `${operationId}-generation`,
			session: owner,
			kind: "image",
			prompt: "private portrait prompt",
			sources: [],
		},
	};
}

function generator(run: (signal?: AbortSignal) => Promise<GameMediaGenerationResult>): GameMediaGenerator {
	return {
		provider: "test-provider",
		model: "test-model",
		kinds: ["image"],
		generate: (_request, _onProgress, signal) => run(signal),
	};
}

function committedImporter(overrides: Partial<GameMediaAssetImporter> = {}): GameMediaAssetImporter {
	return {
		id: "engine-importer",
		async import(context) {
			return {
				operationId: context.importOperationId,
				session: context.job.session,
				expectedRevision: context.job.expectedRevision,
				status: "committed",
				stateRevision: 42,
			};
		},
		async reconcile(context) {
			return {
				operationId: context.importOperationId,
				session: context.job.session,
				expectedRevision: context.job.expectedRevision,
				status: "committed",
				stateRevision: 42,
			};
		},
		...overrides,
	};
}

async function fixture(run: (signal?: AbortSignal) => Promise<GameMediaGenerationResult>) {
	const root = await mkdtemp(join(tmpdir(), "oga-durable-media-"));
	roots.push(root);
	const registry = new GameMediaRegistry();
	registry.register(generator(run));
	const jobs = new SqliteGameMediaAssetJobStore(join(root, "jobs.db"));
	const resources = new FileGameMediaResourceStore(join(root, "resources"));
	return { root, registry, jobs, resources, pipeline: new DurableGameMediaPipeline(registry, jobs, resources) };
}

describe("DurableGameMediaPipeline", () => {
	it("persists generation and authoritative import exactly once across replay and restart", async () => {
		let generations = 0;
		let imports = 0;
		const state = await fixture(async () => {
			generations += 1;
			return generated();
		});
		const importer = committedImporter({
			async import(context) {
				imports += 1;
				return {
					operationId: context.importOperationId,
					session: context.job.session,
					expectedRevision: context.job.expectedRevision,
					status: "committed",
					stateRevision: 42,
				};
			},
		});
		const first = await state.pipeline.execute(request(), importer);
		expect(first).toMatchObject({ status: "completed", revision: 5 });
		expect(first.manifest?.resources).toHaveLength(1);
		expect(generations).toBe(1);
		expect(imports).toBe(1);
		await expect(state.pipeline.execute(request(), importer)).resolves.toMatchObject({ status: "completed" });
		expect(generations).toBe(1);
		expect(imports).toBe(1);

		state.jobs.close();
		using restartedJobs = new SqliteGameMediaAssetJobStore(join(state.root, "jobs.db"));
		const restarted = new DurableGameMediaPipeline(state.registry, restartedJobs, state.resources);
		await expect(restarted.execute(request(), importer)).resolves.toMatchObject({ status: "completed" });
		expect(generations).toBe(1);
		expect(imports).toBe(1);
	});

	it("lets only one concurrent caller dispatch a generation operation", async () => {
		let generations = 0;
		let release: (() => void) | undefined;
		const state = await fixture(async () => {
			generations += 1;
			await new Promise<void>((resolve) => {
				release = resolve;
			});
			return generated();
		});
		const importer = committedImporter();
		const first = state.pipeline.execute(request("concurrent"), importer);
		await vi.waitFor(() => expect(generations).toBe(1));
		const second = await state.pipeline.execute(request("concurrent"), importer);
		expect(second.status).toBe("generating");
		expect(generations).toBe(1);
		release?.();
		await expect(first).resolves.toMatchObject({ status: "completed" });
		state.jobs.close();
	});

	it("never replays an uncertain provider operation and accepts explicit generation reconciliation", async () => {
		let generations = 0;
		const state = await fixture(async () => {
			generations += 1;
			throw new Error("provider connection ended after dispatch");
		});
		const importer = committedImporter();
		const uncertain = await state.pipeline.execute(request("uncertain-generation"), importer);
		expect(uncertain).toMatchObject({
			status: "generation-uncertain",
			failure: { category: "generation-outcome-uncertain" },
		});
		await expect(state.pipeline.execute(request("uncertain-generation"), importer)).resolves.toMatchObject({
			status: "generation-uncertain",
		});
		expect(generations).toBe(1);
		await expect(
			state.pipeline.resolveGeneration(session(), "uncertain-generation", generated(), importer),
		).resolves.toMatchObject({ status: "completed" });
		expect(generations).toBe(1);
		state.jobs.close();
	});

	it("reconciles an uncertain engine import after restart without repeating the mutation", async () => {
		let imports = 0;
		let reconciles = 0;
		let importOperationId: string | undefined;
		const state = await fixture(async () => generated());
		const importer = committedImporter({
			async import(context) {
				imports += 1;
				importOperationId = context.importOperationId;
				throw new Error("engine response was lost after commit");
			},
			async reconcile(context) {
				reconciles += 1;
				expect(context.importOperationId).toBe(importOperationId);
				return {
					operationId: context.importOperationId,
					session: context.job.session,
					expectedRevision: context.job.expectedRevision,
					status: "committed",
					stateRevision: 42,
				};
			},
		});
		const uncertain = await state.pipeline.execute(request("uncertain-import"), importer);
		expect(uncertain.status).toBe("import-uncertain");
		expect(imports).toBe(1);

		state.jobs.close();
		using restartedJobs = new SqliteGameMediaAssetJobStore(join(state.root, "jobs.db"));
		const restarted = new DurableGameMediaPipeline(state.registry, restartedJobs, state.resources);
		const completed = await restarted.resumeImport(session(), "uncertain-import", importer);
		expect(completed.status).toBe("completed");
		expect(imports).toBe(1);
		expect(reconciles).toBe(1);
		expect(completed.importReceipt?.operationId).toBe(createGameMediaImportOperationId(completed));
	});

	it("binds operation identity to the exact request and isolates session owners", async () => {
		const state = await fixture(async () => generated());
		const importer = committedImporter();
		await state.pipeline.execute(request("bound-request"), importer);
		const changed = request("bound-request");
		changed.generation = { ...changed.generation, prompt: "different prompt" };
		await expect(state.pipeline.execute(changed, importer)).rejects.toThrow("another request");
		await expect(state.pipeline.read(session("npc-b"), "bound-request")).resolves.toBeUndefined();
		state.jobs.close();
	});

	it("rejects forged import receipts before they can settle an authoritative job", async () => {
		const state = await fixture(async () => generated());
		const importer = committedImporter({
			async import(context: GameMediaAssetImportContext) {
				return {
					operationId: "forged-operation",
					session: context.job.session,
					expectedRevision: context.job.expectedRevision,
					status: "committed",
				};
			},
		});
		const result = await state.pipeline.execute(request("forged-receipt"), importer);
		expect(result).toMatchObject({ status: "import-uncertain", failure: { category: "import-outcome-uncertain" } });
		state.jobs.close();
	});

	it("persists an uncertain marker independently when cancellation races provider dispatch", async () => {
		let entered = false;
		const state = await fixture(
			async (signal) =>
				await new Promise<GameMediaGenerationResult>((_resolve, reject) => {
					entered = true;
					signal?.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")), { once: true });
				}),
		);
		const controller = new AbortController();
		const running = state.pipeline.execute(
			request("cancelled-generation"),
			committedImporter(),
			undefined,
			controller.signal,
		);
		await vi.waitFor(() => expect(entered).toBe(true));
		controller.abort();
		await expect(running).rejects.toMatchObject({ name: "AbortError" });
		await expect(state.pipeline.read(session(), "cancelled-generation")).resolves.toMatchObject({
			status: "generation-uncertain",
			failure: { category: "generation-cancelled-after-dispatch" },
		});
		state.jobs.close();
	});

	it("fails closed when persisted job state is corrupt", async () => {
		const state = await fixture(async () => generated());
		await state.pipeline.execute(request("corrupt-job"), committedImporter());
		state.jobs.close();
		using raw = new DatabaseSync(join(state.root, "jobs.db"));
		raw.prepare("UPDATE game_media_asset_jobs SET job_json='{}' WHERE operation_id=?").run("corrupt-job");
		raw.close();
		using restarted = new SqliteGameMediaAssetJobStore(join(state.root, "jobs.db"));
		await expect(restarted.read(session(), "corrupt-job")).rejects.toThrow("corrupt");
	});

	it("exposes a bounded semantic Tool result while keeping canonical recovery details", async () => {
		const state = await fixture(async () => generated());
		const context = {
			input: {
				id: "input-media",
				type: "npc.media",
				session: session(),
				moment: { tick: 5 },
				content: [{ type: "text" as const, text: "create portrait" }],
			},
			runId: "run-media",
			turn: 2,
			toolCallIndex: 1,
			signal: new AbortController().signal,
		};
		const tool = createDurableGameMediaTool({
			definition: {
				name: "generate_portrait",
				label: "Generate portrait",
				description: "Generate and import a portrait.",
				parameters: { type: "object", properties: {}, additionalProperties: false },
			},
			pipeline: state.pipeline,
			importer: committedImporter(),
			createRequest: (_call, execution) => request(createGameMediaAssetOperationId(execution, "generate_portrait")),
		});
		const result = await tool.execute({ id: "call-media", name: "generate_portrait", arguments: {} }, context);
		expect(result.isError).toBe(false);
		expect(result.content[0]).toMatchObject({
			type: "json",
			value: { status: "completed", assetType: "portrait", importStatus: "committed" },
		});
		const visible = JSON.stringify(result.content);
		expect(visible).not.toContain("provider-response");
		expect(visible).not.toContain("stateRevision");
		expect(result.details).toMatchObject({ status: "completed", importReceipt: { stateRevision: 42 } });
		state.jobs.close();
	});

	it("rejects a Tool request that changes the authoritative session before provider dispatch", async () => {
		let generations = 0;
		const state = await fixture(async () => {
			generations += 1;
			return generated();
		});
		const tool = createDurableGameMediaTool({
			definition: {
				name: "generate_portrait",
				label: "Generate portrait",
				description: "Generate and import a portrait.",
				parameters: { type: "object", properties: {}, additionalProperties: false },
			},
			pipeline: state.pipeline,
			importer: committedImporter(),
			createRequest: () => request("cross-owner", "npc-b"),
		});
		await expect(
			tool.execute(
				{ id: "call-media", name: "generate_portrait", arguments: {} },
				{
					input: {
						id: "input-media",
						type: "npc.media",
						session: session(),
						moment: { tick: 5 },
						content: [],
					},
					runId: "run-media",
					turn: 1,
					toolCallIndex: 0,
					signal: new AbortController().signal,
				},
			),
		).rejects.toThrow("authoritative input session");
		expect(generations).toBe(0);
		state.jobs.close();
	});
});

import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { StreamFn } from "@earendil-works/pi-agent-core";
import { createModels, createProvider, type Model } from "@earendil-works/pi-ai";
import {
	createPiModelsDeferredResponseAdapter,
	DurableTranscript,
	fingerprintGameModelContext,
	GameAgent,
	InMemoryTranscriptStore,
} from "@opengameagent/agent";
import { createOpenAIBackgroundResponsesApi } from "@opengameagent/integrations";
import { eventAudience, PROTOCOL_VERSION } from "@opengameagent/protocol";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	FileModelContextProvenanceStore,
	type ModelContextProvenanceLimits,
	ModelContextProvenanceRecorder,
	type ModelContextProvenanceStore,
} from "../src/model-context-provenance.js";
import { allowAllTestTools, TEST_TOOL_LIMITS, tool } from "../src/pi-fakes.js";

const STREAM = { sessionId: "session", actorId: "actor" } as const;
const INPUT = { ...STREAM, inputId: "input", payload: { instruction: "inspect" } } as const;
const MODEL: Model<"openai-responses"> = {
	id: "gpt-test",
	name: "Test model",
	api: "openai-responses",
	provider: "openai",
	baseUrl: "https://api.openai.com/v1",
	reasoning: false,
	input: ["text"],
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
	contextWindow: 128_000,
	maxTokens: 16_384,
};
const PROVENANCE_LIMITS = {
	maxRecords: 8,
	maxRecordBytes: 8_192,
	maxStoreBytes: 32_768,
} as const satisfies ModelContextProvenanceLimits;
const BACKGROUND_RESPONSE_ID = "resp_background_private_handle";
const BACKGROUND_CREATED = {
	id: BACKGROUND_RESPONSE_ID,
	object: "response",
	created_at: 1,
	status: "in_progress",
	model: MODEL.id,
	output: [],
};
const TOOL_ITEM = {
	id: "fc_inspect_1",
	type: "function_call",
	status: "completed",
	call_id: "call_inspect_1",
	name: "inspect",
	arguments: "{}",
};
const TOOL_RESPONSE = {
	...BACKGROUND_CREATED,
	status: "completed",
	output: [TOOL_ITEM],
	usage: usage(),
};
const FINAL_RESPONSE = {
	...BACKGROUND_CREATED,
	id: "resp_followup",
	status: "completed",
	output: [
		{
			id: "message_final",
			type: "message",
			status: "completed",
			role: "assistant",
			phase: "final_answer",
			content: [{ type: "output_text", text: "finished", annotations: [], logprobs: [] }],
		},
	],
	usage: usage(),
};
const directories: string[] = [];

afterEach(async () => {
	vi.unstubAllGlobals();
	await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe("OpenAI background model-context provenance", () => {
	it("blocks OpenAI create when request provenance cannot be persisted", async () => {
		const append = vi.fn(async () => {
			throw new Error("disk unavailable");
		});
		const store: ModelContextProvenanceStore = {
			load: async () => ({ schemaVersion: "4", revision: "empty", records: [] }),
			append,
		};
		const recorder = new ModelContextProvenanceRecorder({
			store,
			limits: PROVENANCE_LIMITS,
			newId: () => "failed-request",
		});
		const harness = await createHarness(recorder, []);

		await expect(harness.agent.start(INPUT, "failed-run").completion).resolves.toEqual({
			runId: "failed-run",
			status: "failed",
		});
		expect(append).toHaveBeenCalledTimes(1);
		expect(providerCreates(harness.requests)).toHaveLength(0);
	});

	it("keeps deferred fetches outside inference provenance and records only the post-Tool Pi request", async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-openai-provenance-"));
		directories.push(directory);
		const path = join(directory, "provenance.json");
		const recorder = new ModelContextProvenanceRecorder({
			store: new FileModelContextProvenanceStore({ path, maxDocumentBytes: PROVENANCE_LIMITS.maxStoreBytes }),
			limits: PROVENANCE_LIMITS,
			newId: ids("provenance"),
		});
		const harness = await createHarness(recorder, [
			sseResponse([createdEvent(BACKGROUND_CREATED, 0)]),
			Response.json({ ...BACKGROUND_CREATED, status: "queued" }),
			Response.json(BACKGROUND_CREATED),
			Response.json(TOOL_RESPONSE),
			sseResponse(toolReplayEvents()),
			sseResponse(finalResponseEvents()),
		]);

		await expect(harness.agent.start(INPUT, "background-run").completion).resolves.toMatchObject({
			status: "deferred",
		});
		const pending = harness.agent.readPendingDeferred();
		if (!pending) throw new Error("Expected a pending OpenAI response.");
		await expect(provenanceCounts(recorder)).resolves.toEqual({ requests: 1, responses: 1 });
		expect(providerCreates(harness.requests)).toHaveLength(1);
		const initialText = await readFile(path, "utf8");
		expect(initialText).not.toContain(BACKGROUND_RESPONSE_ID);
		const initialDocument = JSON.parse(initialText) as { records: Array<Record<string, unknown>> };
		expect(initialDocument.records.find((record) => record.type === "provider.request")).not.toHaveProperty("deferred");

		await expect(harness.agent.resumeDeferred(pending.operationId).completion).resolves.toMatchObject({
			status: "deferred",
		});
		await expect(provenanceCounts(recorder)).resolves.toEqual({ requests: 1, responses: 1 });
		expect(providerCreates(harness.requests)).toHaveLength(1);

		await expect(harness.agent.resumeDeferred(pending.operationId).completion).resolves.toMatchObject({
			status: "deferred",
		});
		await expect(provenanceCounts(recorder)).resolves.toEqual({ requests: 1, responses: 1 });
		expect(providerCreates(harness.requests)).toHaveLength(1);

		await expect(harness.agent.resumeDeferred(pending.operationId).completion).resolves.toMatchObject({
			status: "completed",
		});
		expect(harness.execute).toHaveBeenCalledTimes(1);
		expect(providerCreates(harness.requests)).toHaveLength(2);
		await expect(provenanceCounts(recorder)).resolves.toEqual({ requests: 2, responses: 2 });

		const finalState = await recorder.restore();
		const requestRecords = finalState.records.filter((record) => record.type === "provider.request");
		const responseRecords = finalState.records.filter((record) => record.type === "provider.response");
		expect(finalState.records.map(({ type }) => type)).toEqual([
			"provider.request",
			"provider.response",
			"provider.request",
			"provider.response",
		]);
		expect(requestRecords.map(({ modelContextFingerprint }) => modelContextFingerprint)).toEqual(
			harness.inferenceFingerprints,
		);
		expect(responseRecords.map(({ stopReason }) => stopReason)).toEqual(["deferred", "stop"]);
		expect(harness.inferenceFingerprints).toHaveLength(2);
		for (const request of requestRecords) expect(request).not.toHaveProperty("deferred");
		expect(await readFile(path, "utf8")).not.toContain(BACKGROUND_RESPONSE_ID);
	});
});

async function createHarness(recorder: ModelContextProvenanceRecorder, responses: readonly Response[]) {
	const scripted = scriptedFetch(responses);
	vi.stubGlobal("fetch", scripted.fetch);
	const models = createModels();
	models.setProvider(
		createProvider({
			id: MODEL.provider,
			name: "OpenAI test",
			baseUrl: MODEL.baseUrl,
			auth: {
				apiKey: {
					name: "Test",
					resolve: async () => ({ auth: { apiKey: "test" } }),
				},
			},
			models: [MODEL],
			api: createOpenAIBackgroundResponsesApi(),
		}),
	);
	const inferenceFingerprints: string[] = [];
	const modelStream: StreamFn = (model, context, options) => {
		inferenceFingerprints.push(fingerprintGameModelContext(context));
		return models.streamSimple(model, context, {
			...options,
			deferred: inferenceFingerprints.length === 1,
		});
	};
	const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
		maxRecordBytes: 16_384,
		maxCommitBytes: 65_536,
		maxRestoreBytes: 131_072,
		maxJsonDepth: 16,
		newRecordId: ids("transcript"),
	});
	let eventId = 0;
	const execute = vi.fn(async () => ({
		content: [{ type: "text" as const, text: "inspected" }],
		details: undefined,
	}));
	const agent = new GameAgent({
		...STREAM,
		model: MODEL,
		modelStream,
		deferredResponses: createPiModelsDeferredResponseAdapter(models),
		transcript: { transcript, restore: await transcript.restore(STREAM) },
		providerProvenance: { sink: recorder, scopeId: "openai-background-test" },
		toolSource: () => [tool("inspect", execute)],
		toolPolicy: allowAllTestTools,
		toolLimits: TEST_TOOL_LIMITS,
		emitEvent: async (coordinate, data) => {
			const revision = String(++eventId);
			return {
				protocolVersion: PROTOCOL_VERSION,
				eventId: `event-${revision}`,
				sequence: revision,
				revision,
				occurredAt: "2026-08-30T00:00:00.000Z",
				coordinate: structuredClone(coordinate),
				audience: eventAudience(data),
				data: structuredClone(data),
			};
		},
		newId: ids("agent"),
		now: () => Date.parse("2026-08-30T00:00:00.000Z"),
	});
	return { agent, execute, inferenceFingerprints, requests: scripted.requests };
}

function scriptedFetch(responses: readonly Response[]): { fetch: typeof fetch; requests: Request[] } {
	const queue = [...responses];
	const requests: Request[] = [];
	return {
		requests,
		fetch: async (input, init) => {
			const request = new Request(input, init);
			requests.push(request.clone());
			const response = queue.shift();
			if (!response) throw new Error("Unexpected provider request.");
			return response;
		},
	};
}

function providerCreates(requests: readonly Request[]): Request[] {
	return requests.filter((request) => request.method === "POST" && new URL(request.url).pathname === "/v1/responses");
}

async function provenanceCounts(recorder: ModelContextProvenanceRecorder) {
	const state = await recorder.restore();
	return {
		requests: state.records.filter((record) => record.type === "provider.request").length,
		responses: state.records.filter((record) => record.type === "provider.response").length,
	};
}

function sseResponse(events: readonly Record<string, unknown>[]): Response {
	const body = events.map((event) => `data: ${JSON.stringify(event)}\n\n`).join("");
	return new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } });
}

function createdEvent(response: Record<string, unknown>, sequenceNumber: number): Record<string, unknown> {
	return { type: "response.created", response, sequence_number: sequenceNumber };
}

function toolReplayEvents(): readonly Record<string, unknown>[] {
	return [
		{
			type: "response.output_item.added",
			output_index: 0,
			sequence_number: 1,
			item: { ...TOOL_ITEM, status: "in_progress", arguments: "" },
		},
		{
			type: "response.function_call_arguments.done",
			item_id: TOOL_ITEM.id,
			name: TOOL_ITEM.name,
			output_index: 0,
			sequence_number: 2,
			arguments: TOOL_ITEM.arguments,
		},
		{ type: "response.output_item.done", output_index: 0, sequence_number: 3, item: TOOL_ITEM },
		{ type: "response.completed", response: TOOL_RESPONSE, sequence_number: 4 },
	];
}

function finalResponseEvents(): readonly Record<string, unknown>[] {
	const item = FINAL_RESPONSE.output[0];
	if (!item) throw new Error("Expected a final response item.");
	return [
		createdEvent({ ...BACKGROUND_CREATED, id: FINAL_RESPONSE.id }, 0),
		{
			type: "response.output_item.added",
			output_index: 0,
			sequence_number: 1,
			item: { ...item, status: "in_progress", content: [] },
		},
		{
			type: "response.output_text.delta",
			output_index: 0,
			content_index: 0,
			item_id: item.id,
			sequence_number: 2,
			delta: "finished",
			logprobs: [],
		},
		{ type: "response.output_item.done", output_index: 0, sequence_number: 3, item },
		{ type: "response.completed", response: FINAL_RESPONSE, sequence_number: 4 },
	];
}

function usage() {
	return {
		input_tokens: 2,
		input_tokens_details: { cached_tokens: 0 },
		output_tokens: 1,
		output_tokens_details: { reasoning_tokens: 0 },
		total_tokens: 3,
	};
}

function ids(prefix: string): () => string {
	let value = 0;
	return () => `${prefix}-${++value}`;
}

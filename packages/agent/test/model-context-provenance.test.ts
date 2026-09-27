import { createHash } from "node:crypto";
import type { AgentMessage } from "@earendil-works/pi-agent-core";
import type { AssistantMessage, Context, Model } from "@earendil-works/pi-ai";
import { createAssistantMessageEventStream } from "@earendil-works/pi-ai";
import { describe, expect, it, vi } from "vitest";
import { prepareGameContextSnapshot, prepareLoadedGameContextSnapshot } from "../src/context-snapshot.js";
import {
	DurableTranscript,
	type DurableTranscriptOptions,
	InMemoryTranscriptStore,
} from "../src/durable-transcript.js";
import { createProviderAttemptModelStream, GameAgent } from "../src/game-agent.js";
import {
	type CapturedGameProviderProvenanceBinding,
	captureProviderProvenanceBinding,
	fingerprintGameModelContext,
	prepareModelRequestProvenance,
	prepareModelResponseProvenance,
} from "../src/model-context-provenance.js";
import { createGameProvenanceAlias } from "../src/provenance-alias.js";
import type {
	GameContextTransformer,
	GameModelRequestProvenance,
	GameModelResponseProvenance,
	GameProviderAttempt,
	GameProviderProvenanceSink,
} from "../src/types.js";
import { GAME_MODEL_CONTEXT_SOURCE_KINDS } from "../src/types.js";

const MODEL: Model<"openai-responses"> = {
	id: "requested-model",
	name: "Requested model",
	api: "openai-responses",
	provider: "gateway",
	baseUrl: "https://secret-route.invalid/v1",
	reasoning: true,
	input: ["text", "image"],
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
	contextWindow: 10_000,
	maxTokens: 1_000,
};

describe("GameAgent model-context provenance", () => {
	it.each(["pi-catalog", "host-rate-card"] as const)(
		"keeps unreported failed usage unknown without inventing %s cost",
		(source) => {
			const response = prepareModelResponseProvenance(
				assistant([], "error"),
				MODEL,
				"2026-08-31T00:00:00.000Z",
				{ kind: source },
				"unreported",
			);

			expect(response).toMatchObject({
				outcome: "failed",
				cost: { kind: "unknown", source },
				usage: { kind: "unreported" },
			});
			expect(response.cost).not.toHaveProperty("usd");
			expect(response.usage).toEqual({ kind: "unreported" });
			expect(Object.isFrozen(response.usage)).toBe(true);
		},
	);

	it.each([
		[{ kind: "free" }, { kind: "free", usd: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } }],
		[{ kind: "unpriced" }, { kind: "unpriced" }],
	] as const)("retains the %s pricing fact when usage is unreported", (pricing, expectedCost) => {
		const response = prepareModelResponseProvenance(
			assistant([], "error"),
			MODEL,
			"2026-08-31T00:00:00.000Z",
			pricing,
			"unreported",
		);

		expect(response).toMatchObject({ cost: expectedCost, usage: { kind: "unreported" } });
	});

	it("captures exact reported Pi usage and cost", () => {
		const response = prepareModelResponseProvenance(
			assistant([], "stop"),
			MODEL,
			"2026-08-31T00:00:00.000Z",
			{ kind: "host-rate-card" },
			"reported",
		);

		expect(response).toMatchObject({
			cost: {
				kind: "calculated",
				source: "host-rate-card",
				usd: { input: 1, output: 2, cacheRead: 0, cacheWrite: 0, total: 3 },
			},
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
		});
	});

	it("rejects unreported usage for a successful Pi response", () => {
		expect(() =>
			prepareModelResponseProvenance(
				assistant([], "stop"),
				MODEL,
				"2026-08-31T00:00:00.000Z",
				{ kind: "pi-catalog" },
				"unreported",
			),
		).toThrow("Successful provider responses require reported usage");
	});

	it("rejects a Pi response whose request model differs from its committed target", () => {
		expect(() =>
			prepareModelResponseProvenance(
				assistantWithoutResponseModel({ model: "different-request-model", stopReason: "stop" }),
				MODEL,
				"2026-08-31T00:00:00.000Z",
				{ kind: "unpriced" },
				"reported",
			),
		).toThrow("committed provider attempt");
	});

	it("records each real Pi request at the frozen provider boundary and its final assistant metadata", async () => {
		const requests: GameModelRequestProvenance[] = [];
		const frozenRequests: GameModelRequestProvenance[] = [];
		const responses: { requestId: string; value: GameModelResponseProvenance }[] = [];
		const sink: GameProviderProvenanceSink = {
			recordRequest: vi.fn(async (value) => {
				frozenRequests.push(value);
				requests.push(structuredClone(value));
				return createGameProvenanceAlias("record", `request-${requests.length}`);
			}),
			recordResponse: vi.fn(async (requestId, value) => {
				responses.push({ requestId, value: structuredClone(value) });
			}),
		};
		const imageBytes = Buffer.from("img");
		const contextImageBytes = Buffer.from("context-img");
		const imageReference = {
			type: "image-reference" as const,
			attachmentId: `img_${createHash("sha256").update(imageBytes).digest("hex")}`,
			mediaType: "image/png" as const,
			bytes: imageBytes.byteLength,
			width: 1,
			height: 1,
		};
		const contextImageReference = {
			type: "image-reference" as const,
			attachmentId: `img_${createHash("sha256").update(contextImageBytes).digest("hex")}`,
			mediaType: "image/png" as const,
			bytes: contextImageBytes.byteLength,
			width: 1,
			height: 1,
		};
		const providerContexts: unknown[] = [];
		let calls = 0;
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			now: () => Date.parse("2026-08-30T00:00:00.000Z"),
			transcript: await transcriptBinding(imageBytes),
			providerProvenance: { sink, scopeId: "test-scope" },
			contextSources: [
				{
					sourceId: "memory",
					provenanceKind: "memory",
					load: () => ({
						references: [{ referenceId: "memory-1", revision: "3", content: { fact: "private" } }],
						images: [
							{
								contextReferenceId: "memory-1",
								content: {
									type: "image",
									data: contextImageBytes.toString("base64"),
									mimeType: "image/png",
								},
								reference: contextImageReference,
							},
						],
					}),
				},
			],
			contextLimits: {
				maxReferencesPerSnapshot: 4,
				maxSnapshotBytes: 4_096,
				media: {
					maxContentParts: 2,
					maxImageBytes: contextImageBytes.byteLength,
					maxTotalImageBytes: contextImageBytes.byteLength,
				},
			},
			projectInput: () => ({
				message: {
					role: "user",
					content: [
						{ type: "text", text: "secret prompt" },
						{ type: "image", data: imageBytes.toString("base64"), mimeType: "image/png" },
					],
					timestamp: 1,
				},
				transcriptContent: [{ type: "text", text: "secret prompt" }, imageReference],
			}),
			inputProjectionLimits: {
				maxContentParts: 2,
				maxTextBytes: 1_024,
				maxReferenceBytes: new TextEncoder().encode(JSON.stringify(imageReference)).byteLength,
				maxImageBytes: 3,
				maxTotalImageBytes: 3,
			},
			modelStream: (_model, context) => {
				providerContexts.push(
					structuredClone({
						...(context.systemPrompt === undefined ? {} : { systemPrompt: context.systemPrompt }),
						messages: context.messages,
						tools: context.tools?.map(({ name, description, parameters }) => ({ name, description, parameters })),
					}),
				);
				calls += 1;
				return messageStream(
					calls === 1
						? assistant([{ type: "toolCall", id: "call", name: "inspect", arguments: { secret: true } }], "toolUse")
						: assistant([{ type: "text", text: "secret answer" }], "stop"),
				);
			},
			toolSource: () => [
				{
					name: "inspect",
					label: "Inspect",
					description: "Inspect",
					parameters: {
						type: "object",
						properties: { secret: { type: "boolean" } },
						required: ["secret"],
						additionalProperties: false,
					},
					execute: async () => ({
						content: [{ type: "text", text: "tool result secret" }],
						details: { private: "tool details secret" },
					}),
				},
			],
			toolPolicy: (_input, candidates) => ({
				revision: "policy-1",
				visibleToolNames: candidates.map((tool) => tool.name),
			}),
			toolLimits: {
				maxTools: 2,
				maxCatalogBytes: 4_096,
				maxSchemaDepth: 8,
				maxRequestSources: 1,
				maxPolicyRevisionBytes: 64,
				maxArgumentsBytes: 1_024,
				maxArgumentsDepth: 8,
				maxResultContentParts: 2,
				maxResultBytes: 4_096,
				maxResultDepth: 8,
				maxResultUpdates: 2,
			},
		});

		const result = await agent.start({
			sessionId: "session",
			actorId: "actor",
			inputId: "input",
			payload: { secret: true },
		}).completion;
		expect({
			result,
			requestCount: requests.length,
			responseCount: responses.length,
			providerCalls: calls,
		}).toMatchObject({
			result: { status: "completed" },
			requestCount: 2,
			responseCount: 2,
			providerCalls: 2,
		});

		expect(requests).toHaveLength(2);
		const frozenRequest = frozenRequests[0];
		if (!frozenRequest?.toolSnapshot || !frozenRequest.sources[0]?.references[0] || !frozenRequest.tools[0]) {
			throw new Error("Expected complete request provenance.");
		}
		expect(
			[frozenRequest, frozenRequest.toolSnapshot, frozenRequest.sources[0].references[0], frozenRequest.tools[0]].every(
				Object.isFrozen,
			),
		).toBe(true);
		expect(() => {
			(frozenRequest.toolSnapshot as { policyRevision: string }).policyRevision = "changed";
		}).toThrow(TypeError);
		expect(requests[0]).toMatchObject({
			operation: "inference",
			coordinate: {
				scopeId: createGameProvenanceAlias("scope", "test-scope"),
				streamId: createGameProvenanceAlias("stream", "session\0actor"),
				runId: expect.stringMatching(/^pv_[0-9a-f]{64}$/u),
				turn: "1",
			},
			requestedRoute: {
				provider: createGameProvenanceAlias("provider", "gateway"),
				api: createGameProvenanceAlias("api", "openai-responses"),
				model: createGameProvenanceAlias("model", "requested-model"),
			},
			attempt: {
				index: "1",
				targetRoute: {
					provider: createGameProvenanceAlias("provider", "gateway"),
					api: createGameProvenanceAlias("api", "openai-responses"),
					model: createGameProvenanceAlias("model", "requested-model"),
				},
			},
			modelContextFingerprint: expect.stringMatching(/^[0-9a-f]{64}$/u),
			transcript: { revision: "1" },
			lineage: { baseInstructions: { contentFingerprint: createHash("sha256").update("").digest("hex") } },
			toolSnapshot: { policyRevision: createGameProvenanceAlias("revision", "policy-1") },
			sources: [
				{
					kind: "memory",
					sourceId: createGameProvenanceAlias("source", "memory"),
					references: [
						{
							referenceId: createGameProvenanceAlias("reference", "memory\0memory-1"),
							revision: createGameProvenanceAlias("revision", ["memory", "memory-1", "3"].join("\0")),
						},
					],
				},
			],
			tools: [
				{
					name: createGameProvenanceAlias("tool", "inspect"),
					sourceId: createGameProvenanceAlias("host-tool-owner", "host"),
				},
			],
		});
		expect(requests[0]?.images).toEqual([
			{
				messageIndex: 0,
				contentIndex: 1,
				mediaType: "image/png",
				bytes: contextImageBytes.byteLength,
				sha256: createHash("sha256").update(contextImageBytes).digest("hex"),
				context: {
					sourceId: createGameProvenanceAlias("source", "memory"),
					referenceId: createGameProvenanceAlias("reference", "memory\0memory-1"),
					revision: createGameProvenanceAlias("revision", ["memory", "memory-1", "3"].join("\0")),
				},
			},
			{
				messageIndex: 1,
				contentIndex: 1,
				mediaType: "image/png",
				bytes: imageBytes.byteLength,
				sha256: createHash("sha256").update(imageBytes).digest("hex"),
			},
		]);
		expect(requests[0]?.images[1]).not.toHaveProperty("context");
		expect(responses.map(({ requestId }) => requestId)).toEqual([
			createGameProvenanceAlias("record", "request-1"),
			createGameProvenanceAlias("record", "request-2"),
		]);
		expect(responses[1]?.value).toMatchObject({
			servedModel: createGameProvenanceAlias("model", "actual-model"),
			stopReason: "stop",
			usage: { kind: "reported", input: 11, output: 5, cacheWrite1h: 0, totalTokens: 16 },
		});
		const encoded = JSON.stringify({ requests, responses });
		for (const forbidden of [
			"secret prompt",
			"secret answer",
			"tool result secret",
			"tool details secret",
			"provider-response-id",
			"secret-route",
			'"secret":true',
			'"provider":"gateway"',
			'"name":"inspect"',
		]) {
			expect(encoded).not.toContain(forbidden);
		}
		expect(providerContexts).toHaveLength(2);
	});

	it("records each explicit fallback target against one requested route", async () => {
		const firstTarget = { ...MODEL, id: "first-target" };
		const secondTarget = { ...MODEL, id: "second-target" };
		const requests: GameModelRequestProvenance[] = [];
		const responses: GameModelResponseProvenance[] = [];
		const providerCalls: string[] = [];
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			transcript: await transcriptBinding(),
			providerProvenance: {
				scopeId: "attempt-scope",
				sink: {
					recordRequest: async (request) => {
						requests.push(structuredClone(request));
						return createGameProvenanceAlias("record", `attempt-${requests.length}`);
					},
					recordResponse: async (_recordId, response) => {
						responses.push(structuredClone(response));
					},
				},
			},
			modelStream: createProviderAttemptModelStream(async (_requested, _context, _options, attempts) => {
				const discarded = attempts.begin({ ...MODEL, id: "discarded-target" });
				discarded.discard();

				const first = attempts.begin(firstTarget);
				const firstStream = await first.commit(() => {
					providerCalls.push(firstTarget.id);
					return messageStream(assistantWithoutResponseModel({ model: firstTarget.id, stopReason: "error" }));
				});
				await first.settle(await firstStream.result());

				const second = attempts.begin(secondTarget);
				return await second.commit(() => {
					providerCalls.push(secondTarget.id);
					return messageStream(
						assistantWithoutResponseModel({
							model: secondTarget.id,
							responseModel: "served-second-target",
							stopReason: "stop",
						}),
					);
				});
			}),
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: {} }).completion,
		).resolves.toMatchObject({ status: "completed" });
		expect(providerCalls).toEqual(["first-target", "second-target"]);
		expect(requests.map(({ attempt }) => attempt.index)).toEqual(["1", "2"]);
		expect(requests.map(({ requestedRoute }) => requestedRoute.model)).toEqual([
			createGameProvenanceAlias("model", MODEL.id),
			createGameProvenanceAlias("model", MODEL.id),
		]);
		expect(requests.map(({ attempt }) => attempt.targetRoute.model)).toEqual([
			createGameProvenanceAlias("model", firstTarget.id),
			createGameProvenanceAlias("model", secondTarget.id),
		]);
		expect(requests[0]?.modelContextFingerprint).toBe(requests[1]?.modelContextFingerprint);
		expect(responses).toMatchObject([
			{
				servedModel: createGameProvenanceAlias("model", firstTarget.id),
				outcome: "failed",
			},
			{
				servedModel: createGameProvenanceAlias("model", "served-second-target"),
				outcome: "completed",
			},
		]);
	});

	it("settles the final attempt even when no provenance sink is configured", async () => {
		const finalMessage = assistantWithoutResponseModel({ model: MODEL.id, stopReason: "stop" });
		let finalAttempt: GameProviderAttempt | undefined;
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			modelStream: createProviderAttemptModelStream(async (model, _context, _options, attempts) => {
				finalAttempt = attempts.begin(model);
				return await finalAttempt.commit(() => messageStream(finalMessage));
			}),
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: {} }).completion,
		).resolves.toMatchObject({ status: "completed" });
		if (!finalAttempt) throw new Error("Expected a committed final attempt.");
		await expect(finalAttempt.settle(finalMessage)).rejects.toThrow("committed provider attempt");
	});

	it("rejects an attempt-aware stream that returns without a committed final attempt", async () => {
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			modelStream: createProviderAttemptModelStream(() => messageStream(assistant([], "stop"))),
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: {} }).completion,
		).resolves.toMatchObject({ status: "failed" });
	});

	it("uses Pi responseModel as the served model without changing the committed request model", async () => {
		const requests: GameModelRequestProvenance[] = [];
		const responses: GameModelResponseProvenance[] = [];
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			transcript: await transcriptBinding(),
			modelStream: () => messageStream({ ...assistant([], "stop"), responseModel: "server-selected-model" }),
			providerProvenance: {
				scopeId: "server-fallback-scope",
				sink: {
					recordRequest: async (request) => {
						requests.push(structuredClone(request));
						return createGameProvenanceAlias("record", "server-fallback");
					},
					recordResponse: async (_recordId, response) => {
						responses.push(structuredClone(response));
					},
				},
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: {} }).completion,
		).resolves.toMatchObject({ status: "completed" });
		expect(requests).toHaveLength(1);
		expect(responses).toEqual([
			expect.objectContaining({
				servedModel: createGameProvenanceAlias("model", "server-selected-model"),
			}),
		]);
	});

	it("keeps a request source named host distinct from host-owned Tools", async () => {
		const requests: GameModelRequestProvenance[] = [];
		const tool = (name: string) => ({
			name,
			label: name,
			description: name,
			parameters: { type: "object", properties: {}, additionalProperties: false },
			execute: async () => ({ content: [{ type: "text" as const, text: "unused" }], details: undefined }),
		});
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			transcript: await transcriptBinding(),
			modelStream: () => messageStream(assistant([{ type: "text", text: "done" }], "stop")),
			toolSource: () => [tool("base")],
			requestSources: [
				{
					sourceId: "host",
					provenanceKind: "skill",
					collect: () => ({ tools: [tool("sourced")] }),
				},
			],
			toolPolicy: (_input, candidates) => ({
				revision: "policy",
				visibleToolNames: candidates.map(({ name }) => name),
			}),
			toolLimits: {
				maxTools: 2,
				maxCatalogBytes: 4_096,
				maxSchemaDepth: 8,
				maxRequestSources: 1,
				maxPolicyRevisionBytes: 64,
				maxArgumentsBytes: 1_024,
				maxArgumentsDepth: 8,
				maxResultContentParts: 1,
				maxResultBytes: 1_024,
				maxResultDepth: 8,
				maxResultUpdates: 1,
			},
			providerProvenance: {
				scopeId: "host-source-scope",
				sink: {
					recordRequest: async (request) => {
						requests.push(structuredClone(request));
						return createGameProvenanceAlias("record", "host-source");
					},
					recordResponse: async () => undefined,
				},
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }).completion,
		).resolves.toMatchObject({ status: "completed" });
		const request = requests[0];
		if (!request) throw new Error("Expected one provider request.");
		const sourceAlias = createGameProvenanceAlias("source", "host");
		expect(request.sources).toEqual([{ kind: "skill", sourceId: sourceAlias, references: [] }]);
		expect(request.tools).toHaveLength(2);
		expect(request.tools[0]?.sourceId).not.toBe(sourceAlias);
		expect(request.tools[1]?.sourceId).toBe(sourceAlias);
	});

	it("does not call the provider when durable request provenance cannot be recorded", async () => {
		const provider = vi.fn(() => messageStream(assistant([], "stop")));
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			modelStream: provider,
			transcript: await transcriptBinding(),
			providerProvenance: {
				scopeId: "test-scope",
				sink: {
					recordRequest: async () => {
						throw new Error("store unavailable");
					},
					recordResponse: async () => undefined,
				},
			},
		});
		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }).completion,
		).resolves.toMatchObject({
			status: "failed",
		});
		expect(provider).not.toHaveBeenCalled();
	});

	it("does not call the provider when a context transformer injects non-canonical image data", async () => {
		const provider = vi.fn(() => messageStream(assistant([], "stop")));
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			modelStream: provider,
			transcript: await transcriptBinding(),
			transformContext: async (messages) => [
				...messages,
				{
					role: "user" as const,
					content: [{ type: "image" as const, data: "not-canonical-base64", mimeType: "image/png" }],
					timestamp: 2,
				},
			],
			providerProvenance: {
				scopeId: "invalid-transform-image",
				contextTransformer: {
					sourceId: "host",
					referenceId: "transformer",
					revision: "1",
					contentFingerprint: "f".repeat(64),
				},
				sink: {
					recordRequest: async () => createGameProvenanceAlias("record", "unexpected"),
					recordResponse: async () => undefined,
				},
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }).completion,
		).resolves.toMatchObject({ status: "failed" });
		expect(provider).not.toHaveBeenCalled();
	});

	it("reconstructs and re-fingerprints the exact Pi context after transcript restart", async () => {
		const transcriptStore = new InMemoryTranscriptStore();
		const transcriptOptions = transcriptOptionsForTest();
		const transcript = new DurableTranscript(transcriptStore, transcriptOptions);
		const requests: GameModelRequestProvenance[] = [];
		let providerContext: Context | undefined;
		const systemPrompt = "private base instructions";
		const source = {
			sourceId: "skill-source",
			provenanceKind: "skill" as const,
			load: () => ({
				references: [{ referenceId: "skill-1", revision: "4", content: { instruction: "inspect" } }],
			}),
		};
		const transformContext: GameContextTransformer = async (messages: AgentMessage[]) => [...messages];
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			now: () => Date.parse("2026-08-30T00:00:00.000Z"),
			systemPrompt,
			transformContext,
			transcript: {
				transcript,
				restore: await transcript.restore({ sessionId: "session", actorId: "actor" }),
			},
			providerProvenance: {
				scopeId: "restart-scope",
				sink: {
					recordRequest: async (request) => {
						requests.push(structuredClone(request));
						return createGameProvenanceAlias("record", `restart-${requests.length}`);
					},
					recordResponse: async () => undefined,
				},
				baseInstructions: {
					sourceId: "host-config",
					referenceId: "base-instructions",
					revision: "9",
					contentFingerprint: createHash("sha256").update(systemPrompt).digest("hex"),
				},
				contextTransformer: {
					sourceId: "host-code",
					referenceId: "context-transformer",
					revision: "2",
					contentFingerprint: "f".repeat(64),
				},
			},
			contextSources: [source],
			contextLimits: { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 4_096 },
			modelStream: (_model, context) => {
				providerContext = structuredClone(context);
				return messageStream({ ...assistant([{ type: "text", text: "done" }], "stop"), providerThinkingLevel: "high" });
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "restart-input", payload: null }).completion,
		).resolves.toMatchObject({ status: "completed" });
		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "restart-input-2", payload: null }).completion,
		).resolves.toMatchObject({ status: "completed" });
		const request = requests[1];
		if (!request || !providerContext) throw new Error("Expected the second captured provider request.");
		expect(providerContext.messages).toContainEqual(
			expect.objectContaining({ role: "assistant", providerThinkingLevel: "high" }),
		);

		const restartedTranscript = new DurableTranscript(transcriptStore, transcriptOptionsForTest());
		const restored = await restartedTranscript.materializeRevision(
			{ sessionId: "session", actorId: "actor" },
			request.transcript.revision,
		);
		expect(restored.revision).toBe(request.transcript.revision);

		const messages = (await transformContext([...restored.messages])) as Context["messages"];
		const snapshot = await prepareGameContextSnapshot(
			[source],
			{ maxReferencesPerSnapshot: 1, maxSnapshotBytes: 4_096 },
			{ sessionId: "session", actorId: "actor", inputId: "restart-input-2", payload: null },
			new AbortController().signal,
			{ sessionId: "session", actorId: "actor", runId: "reconstruct", turn: "1" },
		);
		if (!snapshot) throw new Error("Expected a reconstructable source snapshot.");
		const insertionIndex = messages.findLastIndex((message) => message.role === "user");
		messages.splice(insertionIndex < 0 ? 0 : insertionIndex, 0, {
			role: "user",
			content: snapshot.text,
			timestamp: 0,
		});
		const reconstructed: Context = { systemPrompt, messages, tools: [] };
		expect(fingerprintGameModelContext(reconstructed)).toBe(request.modelContextFingerprint);
		expect(fingerprintGameModelContext(providerContext)).toBe(request.modelContextFingerprint);
	});

	it("does not resend a Tool-use request when response provenance settlement fails", async () => {
		const execute = vi.fn(async () => ({ content: [{ type: "text" as const, text: "unused" }], details: undefined }));
		const provider = vi.fn(() =>
			messageStream(assistant([{ type: "toolCall", id: "call", name: "inspect", arguments: {} }], "toolUse")),
		);
		const recordRequest = vi.fn(async () => createGameProvenanceAlias("record", "request"));
		const recordResponse = vi.fn(async () => {
			throw new Error("store unavailable");
		});
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			modelStream: provider,
			transcript: await transcriptBinding(),
			providerProvenance: { sink: { recordRequest, recordResponse }, scopeId: "test-scope" },
			toolSource: () => [
				{
					name: "inspect",
					label: "Inspect",
					description: "Inspect",
					parameters: { type: "object", properties: {}, additionalProperties: false },
					execute,
				},
			],
			toolPolicy: (_input, candidates) => ({
				revision: "policy-1",
				visibleToolNames: candidates.map((tool) => tool.name),
			}),
			toolLimits: {
				maxTools: 1,
				maxCatalogBytes: 4_096,
				maxSchemaDepth: 8,
				maxRequestSources: 1,
				maxPolicyRevisionBytes: 64,
				maxArgumentsBytes: 1_024,
				maxArgumentsDepth: 8,
				maxResultContentParts: 1,
				maxResultBytes: 1_024,
				maxResultDepth: 8,
				maxResultUpdates: 1,
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }).completion,
		).resolves.toMatchObject({
			status: "failed",
		});
		expect(provider).toHaveBeenCalledTimes(1);
		expect(recordRequest).toHaveBeenCalledTimes(1);
		expect(recordResponse).toHaveBeenCalledTimes(1);
		expect(execute).not.toHaveBeenCalled();
	});

	it("does not settle provenance when the durable assistant commit fails", async () => {
		class AssistantCommitFailureStore extends InMemoryTranscriptStore {
			private appends = 0;

			override async append(...args: Parameters<InMemoryTranscriptStore["append"]>) {
				this.appends += 1;
				if (this.appends === 2) throw new Error("transcript unavailable");
				return await super.append(...args);
			}
		}
		const transcript = new DurableTranscript(new AssistantCommitFailureStore(), transcriptOptionsForTest());
		const recordResponse = vi.fn(async () => undefined);
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			modelStream: () => messageStream(assistant([{ type: "text", text: "answer" }], "stop")),
			transcript: {
				transcript,
				restore: await transcript.restore({ sessionId: "session", actorId: "actor" }),
			},
			providerProvenance: {
				scopeId: "commit-failure",
				sink: {
					recordRequest: async () => createGameProvenanceAlias("record", "commit-failure"),
					recordResponse,
				},
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }).completion,
		).resolves.toMatchObject({ status: "failed" });
		expect(recordResponse).not.toHaveBeenCalled();
	});

	it("fails a deferred call when provenance settlement fails while preserving its provider operation", async () => {
		const handle = {
			provider: "gateway",
			modelId: "requested-model",
			api: "openai-responses",
			id: "private-provider-operation",
		};
		const recordResponse = vi.fn(async () => {
			throw new Error("provenance unavailable");
		});
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			modelStream: () => messageStream({ ...assistant([], "deferred"), deferred: handle }),
			transcript: await transcriptBinding(),
			deferredResponses: {
				fetch: async () => assistant([{ type: "text", text: "fetched" }], "stop"),
				cancel: async () => undefined,
			},
			providerProvenance: {
				scopeId: "deferred-failure",
				sink: {
					recordRequest: async () => createGameProvenanceAlias("record", "deferred-failure"),
					recordResponse,
				},
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }).completion,
		).resolves.toMatchObject({ status: "failed" });
		expect(recordResponse).toHaveBeenCalledTimes(1);
		const pending = agent.readPendingDeferred();
		expect(pending).toMatchObject({ handle: { id: handle.id } });
		if (!pending) throw new Error("Expected the unsettled provider operation to remain resumable.");
		await expect(agent.resumeDeferred(pending.operationId).completion).resolves.toMatchObject({ status: "completed" });
		expect(agent.readPendingDeferred()).toBeUndefined();
	});

	it("fails instead of suspending when a deferred response mismatches its committed target", async () => {
		const handle = {
			provider: "gateway",
			modelId: "requested-model",
			api: "openai-responses",
			id: "private-mismatched-operation",
		};
		const recordResponse = vi.fn(async () => undefined);
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			modelStream: () => messageStream({ ...assistant([], "deferred"), model: "unexpected-model", deferred: handle }),
			transcript: await transcriptBinding(),
			providerProvenance: {
				scopeId: "deferred-route-mismatch",
				sink: {
					recordRequest: async () => createGameProvenanceAlias("record", "deferred-route-mismatch"),
					recordResponse,
				},
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }).completion,
		).resolves.toMatchObject({ status: "failed" });
		expect(recordResponse).not.toHaveBeenCalled();
		expect(agent.readPendingDeferred()).toMatchObject({ handle: { id: handle.id } });
	});

	it.each([
		["error", "failed", "failed", "provider"],
		["aborted", "aborted", "cancelled", "cancelled"],
	] as const)(
		"settles a provider %s without persisting its error payload",
		async (stopReason, runStatus, outcome, errorKind) => {
			const requests: GameModelRequestProvenance[] = [];
			const responses: GameModelResponseProvenance[] = [];
			const agent = new GameAgent({
				sessionId: "session",
				actorId: "actor",
				model: MODEL,
				emitEvent: async () => event(),
				modelStream: () => messageStream(assistant([], stopReason)),
				transcript: await transcriptBinding(),
				providerProvenance: {
					scopeId: "settlement-scope",
					sink: {
						recordRequest: async (request) => {
							requests.push(structuredClone(request));
							return createGameProvenanceAlias("record", "settlement");
						},
						recordResponse: async (_requestId, response) => {
							responses.push(structuredClone(response));
						},
					},
				},
			});

			await expect(
				agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }).completion,
			).resolves.toMatchObject({ status: runStatus });
			expect(requests).toHaveLength(1);
			expect(responses).toMatchObject([{ stopReason, outcome, errorKind }]);
			expect(JSON.stringify(responses)).not.toContain("secret provider failure");
		},
	);

	it("does not create a second provider request manifest for a deferred fetch", async () => {
		const handle = {
			provider: "gateway",
			modelId: "requested-model",
			api: "openai-responses",
			id: "private-provider-operation",
		};
		const requests: GameModelRequestProvenance[] = [];
		const responses: GameModelResponseProvenance[] = [];
		const provider = vi.fn(() => messageStream({ ...assistant([], "deferred"), deferred: handle }));
		const fetch = vi.fn(async () => assistant([{ type: "text", text: "fetched" }], "stop"));
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			modelStream: provider,
			transcript: await transcriptBinding(),
			deferredResponses: { fetch, cancel: async () => undefined },
			providerProvenance: {
				scopeId: "deferred-scope",
				sink: {
					recordRequest: async (request) => {
						requests.push(structuredClone(request));
						return createGameProvenanceAlias("record", "deferred");
					},
					recordResponse: async (_requestId, response) => {
						responses.push(structuredClone(response));
					},
				},
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }, "run-1").completion,
		).resolves.toMatchObject({ status: "deferred" });
		const pending = agent.readPendingDeferred();
		if (!pending) throw new Error("Expected a pending deferred response.");
		await expect(agent.resumeDeferred(pending.operationId).completion).resolves.toMatchObject({ status: "completed" });
		expect(provider).toHaveBeenCalledTimes(1);
		expect(fetch).toHaveBeenCalledTimes(1);
		expect(requests).toHaveLength(1);
		expect(responses).toMatchObject([{ stopReason: "deferred", outcome: "deferred" }]);
		expect(JSON.stringify({ requests, responses })).not.toContain(handle.id);
	});

	it("records only the real inference that follows a fetched deferred Tool call", async () => {
		const handle = {
			provider: "gateway",
			modelId: "requested-model",
			api: "openai-responses",
			id: "private-provider-operation",
		};
		const requests: GameModelRequestProvenance[] = [];
		const responses: GameModelResponseProvenance[] = [];
		const execute = vi.fn(async () => ({
			content: [{ type: "text" as const, text: "inspected" }],
			details: undefined,
		}));
		let providerCalls = 0;
		const provider = vi.fn(() => {
			providerCalls += 1;
			return messageStream(
				providerCalls === 1
					? { ...assistant([], "deferred"), deferred: handle }
					: assistant([{ type: "text", text: "complete" }], "stop"),
			);
		});
		const fetch = vi.fn(async () =>
			assistant([{ type: "toolCall", id: "fetched-call", name: "inspect", arguments: {} }], "toolUse"),
		);
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			modelStream: provider,
			transcript: await transcriptBinding(),
			deferredResponses: { fetch, cancel: async () => undefined },
			providerProvenance: {
				scopeId: "deferred-tool-scope",
				sink: {
					recordRequest: async (request) => {
						requests.push(structuredClone(request));
						return createGameProvenanceAlias("record", `deferred-tool-${requests.length}`);
					},
					recordResponse: async (_requestId, response) => {
						responses.push(structuredClone(response));
					},
				},
			},
			toolSource: () => [
				{
					name: "inspect",
					label: "Inspect",
					description: "Inspect",
					parameters: { type: "object", properties: {}, additionalProperties: false },
					execute,
				},
			],
			toolPolicy: (_input, candidates) => ({
				revision: "policy-1",
				visibleToolNames: candidates.map(({ name }) => name),
			}),
			toolLimits: {
				maxTools: 1,
				maxCatalogBytes: 4_096,
				maxSchemaDepth: 8,
				maxRequestSources: 1,
				maxPolicyRevisionBytes: 64,
				maxArgumentsBytes: 1_024,
				maxArgumentsDepth: 8,
				maxResultContentParts: 1,
				maxResultBytes: 1_024,
				maxResultDepth: 8,
				maxResultUpdates: 1,
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }, "run-1").completion,
		).resolves.toMatchObject({ status: "deferred" });
		const pending = agent.readPendingDeferred();
		if (!pending) throw new Error("Expected a pending deferred response.");
		await expect(agent.resumeDeferred(pending.operationId).completion).resolves.toMatchObject({ status: "completed" });
		expect(fetch).toHaveBeenCalledTimes(1);
		expect(execute).toHaveBeenCalledTimes(1);
		expect(provider).toHaveBeenCalledTimes(2);
		expect(requests).toHaveLength(2);
		expect(responses.map(({ stopReason }) => stopReason)).toEqual(["deferred", "stop"]);
	});

	it("does not create provenance records while abandoning a deferred operation", async () => {
		const handle = {
			provider: "gateway",
			modelId: "requested-model",
			api: "openai-responses",
			id: "private-provider-operation",
		};
		const requests: GameModelRequestProvenance[] = [];
		const responses: GameModelResponseProvenance[] = [];
		const cancel = vi.fn(async () => undefined);
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			modelStream: () => messageStream({ ...assistant([], "deferred"), deferred: handle }),
			transcript: await transcriptBinding(),
			deferredResponses: { fetch: async () => assistant([], "stop"), cancel },
			providerProvenance: {
				scopeId: "deferred-cancel-scope",
				sink: {
					recordRequest: async (request) => {
						requests.push(structuredClone(request));
						return createGameProvenanceAlias("record", "deferred-cancel");
					},
					recordResponse: async (_requestId, response) => {
						responses.push(structuredClone(response));
					},
				},
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "input", payload: null }, "run-1").completion,
		).resolves.toMatchObject({ status: "deferred" });
		const pending = agent.readPendingDeferred();
		if (!pending) throw new Error("Expected a pending deferred response.");
		await expect(agent.abandonDeferred(pending.operationId)).resolves.toMatchObject({
			providerCancellation: "completed",
		});
		expect(cancel).toHaveBeenCalledTimes(1);
		expect(requests).toHaveLength(1);
		expect(responses).toHaveLength(1);
	});

	it("preserves every public context-source classification in one request manifest", () => {
		const loaded = GAME_MODEL_CONTEXT_SOURCE_KINDS.map((kind, index) => ({
			sourceId: `source-${index}`,
			provenanceKind: kind,
			references: [{ referenceId: `reference-${index}`, revision: "1", content: { kind } }],
			images: [],
		}));
		const snapshot = prepareLoadedGameContextSnapshot(
			loaded,
			{ maxReferencesPerSnapshot: loaded.length, maxSnapshotBytes: 16_384 },
			new AbortController().signal,
		);
		if (!snapshot) throw new Error("Expected a context snapshot.");
		const request = prepareModelRequestProvenance({
			requestedModel: MODEL,
			targetModel: MODEL,
			attemptIndex: "1",
			context: { messages: [{ role: "user", content: snapshot.text, timestamp: 1 }], tools: [] },
			coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "1" },
			startedAt: "2026-08-30T00:00:00.000Z",
			transcriptRevision: "1",
			provenance: capturedProvenance(),
			contextSnapshot: snapshot,
			loadedContextSources: loaded,
			toolOwners: new Map(),
		});
		expect(request.sources.map(({ kind }) => kind)).toEqual(GAME_MODEL_CONTEXT_SOURCE_KINDS);
	});

	it("reconstructs a post-checkpoint request with a Tool turn, request source, and hydrated image", async () => {
		const imageBytes = Buffer.from("checkpoint-image");
		const media: NonNullable<DurableTranscriptOptions["media"]> = {
			captureToolResult: async () => {
				throw new Error("unused");
			},
			read: async (requests) =>
				requests.map((request) => ({
					type: "image" as const,
					data: imageBytes.toString("base64"),
					mimeType: request.reference.mediaType,
				})),
		};
		const store = new InMemoryTranscriptStore();
		const options = { ...transcriptOptionsForTest(), media };
		const transcript = new DurableTranscript(store, options);
		const oldCoordinate = { sessionId: "session", actorId: "actor", runId: "old-run", turn: "1" } as const;
		const oldMessages = await transcript.commit({
			stream: { sessionId: "session", actorId: "actor" },
			expectedRevision: "empty",
			records: [
				{
					type: "message.completed",
					coordinate: oldCoordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "old question" }],
						timestamp: 1,
					},
				},
				{
					type: "message.completed",
					coordinate: oldCoordinate,
					message: {
						role: "assistant",
						content: [{ type: "toolCall", id: "old-call", name: "inspect", arguments: {} }],
						api: "openai-responses",
						provider: "gateway",
						model: "requested-model",
						stopReason: "toolUse",
						timestamp: 2,
					},
				},
			],
		});
		const assistantRecord = oldMessages.records[1];
		if (assistantRecord?.type !== "message.completed") throw new Error("Expected a durable Tool-use assistant.");
		const started = await transcript.commit({
			stream: { sessionId: "session", actorId: "actor" },
			expectedRevision: oldMessages.revision,
			records: [
				{
					type: "tool.started",
					coordinate: oldCoordinate,
					assistantRecordId: assistantRecord.recordId,
					sourceIndex: 0,
					toolCallId: "old-call",
					toolName: "inspect",
					arguments: {},
					descriptorFingerprint: "a".repeat(64),
					catalogRevision: "catalog-1",
					policyRevision: "policy-1",
				},
			],
		});
		const startRecord = started.records[0];
		if (startRecord?.type !== "tool.started") throw new Error("Expected a durable Tool start.");
		const completed = await transcript.commit({
			stream: { sessionId: "session", actorId: "actor" },
			expectedRevision: started.revision,
			records: [
				{
					type: "tool.completed",
					coordinate: oldCoordinate,
					assistantRecordId: assistantRecord.recordId,
					startedRecordId: startRecord.recordId,
					sourceIndex: 0,
					outcome: "completed",
					message: {
						role: "toolResult",
						toolCallId: "old-call",
						toolName: "inspect",
						content: [{ type: "text", text: "old result" }],
						isError: false,
						timestamp: 3,
					},
				},
				{ type: "turn.closed", coordinate: oldCoordinate, status: "completed" },
			],
		});
		const closure = completed.records[1];
		if (closure?.type !== "turn.closed") throw new Error("Expected a closed turn.");
		const checkpoint = await transcript.checkpoint({
			stream: { sessionId: "session", actorId: "actor" },
			source: closure,
			replacement: [
				{
					role: "user",
					contentFormat: "text",
					content: [{ type: "text", text: "summary" }],
					timestamp: 3,
				},
			],
		});
		const suffixCoordinate = {
			sessionId: "session",
			actorId: "actor",
			runId: "post-checkpoint-run",
			turn: "1",
		} as const;
		const suffixMessages = await transcript.commit({
			stream: { sessionId: "session", actorId: "actor" },
			expectedRevision: checkpoint.revision,
			records: [
				{
					type: "message.completed",
					coordinate: suffixCoordinate,
					message: {
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "inspect after checkpoint" }],
						timestamp: 4,
					},
				},
				{
					type: "message.completed",
					coordinate: suffixCoordinate,
					message: {
						role: "assistant",
						content: [{ type: "toolCall", id: "suffix-call", name: "inspect", arguments: {} }],
						api: "openai-responses",
						provider: "gateway",
						model: "requested-model",
						stopReason: "toolUse",
						timestamp: 5,
					},
				},
			],
		});
		const suffixAssistant = suffixMessages.records[1];
		if (suffixAssistant?.type !== "message.completed") throw new Error("Expected a post-checkpoint Tool use.");
		const suffixStarted = await transcript.commit({
			stream: { sessionId: "session", actorId: "actor" },
			expectedRevision: suffixMessages.revision,
			records: [
				{
					type: "tool.started",
					coordinate: suffixCoordinate,
					assistantRecordId: suffixAssistant.recordId,
					sourceIndex: 0,
					toolCallId: "suffix-call",
					toolName: "inspect",
					arguments: {},
					descriptorFingerprint: "b".repeat(64),
					catalogRevision: "catalog-2",
					policyRevision: "policy-2",
				},
			],
		});
		const suffixStartRecord = suffixStarted.records[0];
		if (suffixStartRecord?.type !== "tool.started") throw new Error("Expected a post-checkpoint Tool start.");
		const suffixCompleted = await transcript.commit({
			stream: { sessionId: "session", actorId: "actor" },
			expectedRevision: suffixStarted.revision,
			records: [
				{
					type: "tool.completed",
					coordinate: suffixCoordinate,
					assistantRecordId: suffixAssistant.recordId,
					startedRecordId: suffixStartRecord.recordId,
					sourceIndex: 0,
					outcome: "completed",
					message: {
						role: "toolResult",
						toolCallId: "suffix-call",
						toolName: "inspect",
						content: [{ type: "text", text: "post-checkpoint result" }],
						isError: false,
						timestamp: 6,
					},
				},
				{ type: "turn.closed", coordinate: suffixCoordinate, status: "completed" },
			],
		});
		const imageReference = {
			type: "image-reference" as const,
			attachmentId: `img_${createHash("sha256").update(imageBytes).digest("hex")}` as const,
			mediaType: "image/png" as const,
			bytes: imageBytes.byteLength,
			width: 1,
			height: 1,
		};
		const contextLimits = { maxReferencesPerSnapshot: 1, maxSnapshotBytes: 4_096 } as const;
		const sourceReference = {
			referenceId: "quest-state",
			revision: "12",
			content: { objective: "inspect the shrine" },
		};
		const inspectTool = {
			name: "inspect",
			label: "Inspect",
			description: "Inspect the current target",
			parameters: { type: "object", properties: {}, additionalProperties: false },
			execute: async () => ({ content: [{ type: "text" as const, text: "unused" }], details: undefined }),
		};
		const requests: GameModelRequestProvenance[] = [];
		let providerFingerprint: string | undefined;
		const agent = new GameAgent({
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			newId: ids(),
			projectInput: () => ({
				message: {
					role: "user",
					content: [
						{ type: "text", text: "new question" },
						{ type: "image", data: imageBytes.toString("base64"), mimeType: "image/png" },
					],
					timestamp: 7,
				},
				transcriptContent: [{ type: "text", text: "new question" }, imageReference],
			}),
			inputProjectionLimits: {
				maxContentParts: 2,
				maxTextBytes: 1_024,
				maxReferenceBytes: new TextEncoder().encode(JSON.stringify(imageReference)).byteLength,
				maxImageBytes: imageBytes.byteLength,
				maxTotalImageBytes: imageBytes.byteLength,
			},
			modelStream: (_model, context) => {
				providerFingerprint = fingerprintGameModelContext(context);
				return messageStream(assistant([{ type: "text", text: "new answer" }], "stop"));
			},
			transcript: {
				transcript,
				restore: await transcript.restore({ sessionId: "session", actorId: "actor" }),
			},
			requestSources: [
				{
					sourceId: "skill-request",
					provenanceKind: "skill",
					collect: () => ({ context: { references: [sourceReference] }, tools: [inspectTool] }),
				},
			],
			contextLimits,
			toolPolicy: (_input, candidates) => ({
				revision: "policy-2",
				visibleToolNames: candidates.map(({ name }) => name),
			}),
			toolLimits: {
				maxTools: 1,
				maxCatalogBytes: 4_096,
				maxSchemaDepth: 8,
				maxRequestSources: 1,
				maxPolicyRevisionBytes: 64,
				maxArgumentsBytes: 1_024,
				maxArgumentsDepth: 8,
				maxResultContentParts: 1,
				maxResultBytes: 1_024,
				maxResultDepth: 8,
				maxResultUpdates: 1,
			},
			providerProvenance: {
				scopeId: "checkpoint-scope",
				sink: {
					recordRequest: async (request) => {
						requests.push(structuredClone(request));
						return createGameProvenanceAlias("record", "checkpoint");
					},
					recordResponse: async () => undefined,
				},
			},
		});

		await expect(
			agent.start({ sessionId: "session", actorId: "actor", inputId: "new-input", payload: null }).completion,
		).resolves.toMatchObject({ status: "completed" });
		const request = requests[0];
		if (!request || !providerFingerprint) throw new Error("Expected one provider request.");
		expect(request.transcript).toEqual({
			revision: (BigInt(suffixCompleted.revision) + 1n).toString(),
			checkpointRevision: checkpoint.revision,
		});
		expect(request.sources).toMatchObject([
			{ kind: "skill", references: [{ contentFingerprint: expect.any(String) }] },
		]);
		expect(request.tools).toHaveLength(1);
		expect(request.images).toMatchObject([{ mediaType: "image/png", bytes: imageBytes.byteLength }]);

		const restarted = new DurableTranscript(store, { ...transcriptOptionsForTest(), media });
		const materialized = await restarted.materializeRevision(
			{ sessionId: "session", actorId: "actor" },
			request.transcript.revision,
		);
		expect(materialized.messages).toEqual(
			expect.arrayContaining([
				expect.objectContaining({ role: "assistant", stopReason: "toolUse" }),
				expect.objectContaining({ role: "toolResult", toolCallId: "suffix-call" }),
			]),
		);
		const snapshot = prepareLoadedGameContextSnapshot(
			[{ sourceId: "skill-request", provenanceKind: "skill", references: [sourceReference], images: [] }],
			contextLimits,
			new AbortController().signal,
		);
		if (!snapshot) throw new Error("Expected a request-source snapshot.");
		const messages = [...materialized.messages] as Context["messages"];
		const insertionIndex = messages.findLastIndex((message) => message.role === "user");
		messages.splice(insertionIndex < 0 ? 0 : insertionIndex, 0, {
			role: "user",
			content: snapshot.text,
			timestamp: 0,
		});
		const reconstructed: Context = { systemPrompt: "", messages, tools: [inspectTool] };
		expect(fingerprintGameModelContext(reconstructed)).toBe(request.modelContextFingerprint);
		expect(providerFingerprint).toBe(request.modelContextFingerprint);
	});

	it("fingerprints model-visible image coordinates and Tool descriptions deterministically", () => {
		type ContextChange = {
			imageData?: string;
			imageFirst?: boolean;
			imageMediaType?: "image/jpeg" | "image/png";
			text?: string;
			toolDescription?: string;
		};
		const firstImage = Buffer.from("first-image").toString("base64");
		const secondImage = Buffer.from("second-image").toString("base64");
		const context = (change: ContextChange = {}): Context => {
			const text = { type: "text" as const, text: change.text ?? "inspect this image" };
			const image = {
				type: "image" as const,
				data: change.imageData ?? firstImage,
				mimeType: change.imageMediaType ?? ("image/png" as const),
			};
			return {
				messages: [
					{
						role: "user",
						content: change.imageFirst ? [image, text] : [text, image],
						timestamp: 1,
					},
				],
				tools: [
					{
						name: "inspect",
						description: change.toolDescription ?? "Inspect the current scene",
						parameters: { type: "object", properties: {}, additionalProperties: false },
					},
				],
			};
		};
		const baselineContext = context();
		const baselineFingerprint = fingerprintGameModelContext(baselineContext);

		expect(fingerprintGameModelContext(baselineContext)).toBe(baselineFingerprint);
		const cases: readonly (readonly [label: string, value: Context])[] = [
			["image bytes", context({ imageData: secondImage })],
			["image MIME", context({ imageMediaType: "image/jpeg" })],
			["image position", context({ imageFirst: true })],
			["text", context({ text: "inspect the other image" })],
			["Tool description", context({ toolDescription: "Inspect every visible object" })],
		];
		for (const [label, value] of cases) {
			expect(fingerprintGameModelContext(value), `${label} should affect the fingerprint`).not.toBe(
				baselineFingerprint,
			);
		}
	});

	it("fingerprints exact optional provider thinking effort", () => {
		const message = assistant([{ type: "text", text: "answer" }], "stop");
		const context = (providerThinkingLevel?: string): Context => ({
			messages: [{ ...message, ...(providerThinkingLevel === undefined ? {} : { providerThinkingLevel }) }],
		});
		const high = fingerprintGameModelContext(context("high"));
		expect(fingerprintGameModelContext(context("high"))).toBe(high);
		expect(fingerprintGameModelContext(context("low"))).not.toBe(high);
		expect(fingerprintGameModelContext(context())).not.toBe(high);
		expect(fingerprintGameModelContext(context(""))).not.toBe(fingerprintGameModelContext(context()));
	});

	it("does not fingerprint Tool details that Pi does not send to a model", () => {
		const context = (details: unknown): Context => ({
			messages: [
				{ role: "user", content: "question", timestamp: 1 },
				assistant([{ type: "toolCall", id: "call", name: "inspect", arguments: {} }], "toolUse"),
				{
					role: "toolResult",
					toolCallId: "call",
					toolName: "inspect",
					content: [{ type: "text", text: "result" }],
					details,
					isError: false,
					timestamp: 2,
				},
			],
			tools: [],
		});
		const prepare = (value: Context) =>
			prepareModelRequestProvenance({
				requestedModel: MODEL,
				targetModel: MODEL,
				attemptIndex: "1",
				context: value,
				coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "1" },
				startedAt: "2026-08-30T00:00:00.000Z",
				transcriptRevision: "1",
				provenance: capturedProvenance(),
				loadedContextSources: [],
				toolOwners: new Map(),
			});

		expect(prepare(context({ private: "one" })).modelContextFingerprint).toBe(
			prepare(context({ private: "two" })).modelContextFingerprint,
		);
	});

	it("rejects provenance without a durable transcript", () => {
		expect(
			() =>
				new GameAgent({
					sessionId: "session",
					actorId: "actor",
					model: MODEL,
					emitEvent: async () => event(),
					modelStream: () => messageStream(assistant([], "stop")),
					providerProvenance: {
						scopeId: "scope",
						sink: {
							recordRequest: async () => createGameProvenanceAlias("record", "request"),
							recordResponse: async () => undefined,
						},
					},
				}),
		).toThrow("durable transcript");
	});

	it("requires matching base-instruction and transformer lineage", async () => {
		const transcript = await transcriptBinding();
		const sink = {
			recordRequest: async () => createGameProvenanceAlias("record", "request"),
			recordResponse: async () => undefined,
		};
		const common = {
			sessionId: "session",
			actorId: "actor",
			model: MODEL,
			emitEvent: async () => event(),
			modelStream: () => messageStream(assistant([], "stop")),
			transcript,
		} as const;
		expect(
			() =>
				new GameAgent({
					...common,
					systemPrompt: "private prompt",
					providerProvenance: { sink, scopeId: "scope" },
				}),
		).toThrow("base instructions");
		expect(
			() =>
				new GameAgent({
					...common,
					transformContext: async (messages) => messages,
					providerProvenance: { sink, scopeId: "scope" },
				}),
		).toThrow("transformer");
	});

	it.each([
		["source", { sourceId: "host\0config", referenceId: "base-instructions", revision: "1" }],
		["reference", { sourceId: "host-config", referenceId: "base\0instructions", revision: "1" }],
		["revision", { sourceId: "host-config", referenceId: "base-instructions", revision: "1\0next" }],
	])("rejects control characters in %s lineage identifiers before aliasing", (_field, identity) => {
		const contentFingerprint = createHash("sha256").update("").digest("hex");
		expect(() =>
			captureProviderProvenanceBinding(
				{
					scopeId: "scope",
					sink: {
						recordRequest: async () => createGameProvenanceAlias("record", "request"),
						recordResponse: async () => undefined,
					},
					baseInstructions: { ...identity, contentFingerprint },
				},
				{ hasTranscript: true, systemPrompt: "", hasContextTransformer: false },
			),
		).toThrow("invalid_identifier");
	});
});

function assistant(content: AssistantMessage["content"], stopReason: AssistantMessage["stopReason"]): AssistantMessage {
	return {
		role: "assistant",
		content,
		api: "openai-responses",
		provider: "gateway",
		model: "requested-model",
		responseModel: "actual-model",
		responseId: "provider-response-id",
		usage: {
			input: 11,
			output: 5,
			cacheRead: 3,
			cacheWrite: 0,
			cacheWrite1h: 0,
			reasoning: 2,
			totalTokens: 16,
			cost: { input: 1, output: 2, cacheRead: 0, cacheWrite: 0, total: 3 },
		},
		stopReason,
		...(stopReason === "error" ? { errorMessage: "secret provider failure" } : {}),
		timestamp: 1,
	};
}

function assistantWithoutResponseModel(
	overrides: Partial<AssistantMessage> & Pick<AssistantMessage, "model" | "stopReason">,
): AssistantMessage {
	const { responseModel: _responseModel, ...base } = assistant([], overrides.stopReason);
	return { ...base, ...overrides };
}

function messageStream(message: AssistantMessage) {
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		stream.push({ type: "start", partial: { ...message, stopReason: "pending" } });
		if (message.stopReason === "error" || message.stopReason === "aborted") {
			stream.push({ type: "error", reason: message.stopReason, error: message });
		} else if (message.stopReason !== "pending") {
			stream.push({ type: "done", reason: message.stopReason, message });
		}
	});
	return stream;
}

function ids() {
	let value = 0;
	return () => `id-${++value}`;
}

function event() {
	return {
		protocolVersion: "1" as const,
		eventId: "event",
		sequence: "1",
		revision: "1",
		occurredAt: "2026-08-30T00:00:00.000Z",
		coordinate: { sessionId: "session", actorId: "actor", runId: "run", turn: "1" },
		audience: { visibility: "owner" as const },
		data: { type: "run.started" as const, inputId: "input" },
	};
}

async function transcriptBinding(imageBytes?: Uint8Array) {
	let recordId = 0;
	const transcript = new DurableTranscript(new InMemoryTranscriptStore(), {
		...transcriptOptionsForTest(),
		newRecordId: () => `transcript-${++recordId}`,
		...(imageBytes
			? {
					media: {
						captureToolResult: async () => {
							throw new Error("unused");
						},
						read: async (requests) =>
							requests.map((request) => ({
								type: "image" as const,
								data: Buffer.from(imageBytes).toString("base64"),
								mimeType: request.reference.mediaType,
							})),
					},
				}
			: {}),
	});
	return {
		transcript,
		restore: await transcript.restore({ sessionId: "session", actorId: "actor" }),
	};
}

function transcriptOptionsForTest(): DurableTranscriptOptions {
	let recordId = 0;
	return {
		maxRecordBytes: 16_384,
		maxCommitBytes: 65_536,
		maxRestoreBytes: 131_072,
		maxJsonDepth: 16,
		newRecordId: () => `restarted-transcript-${++recordId}`,
	};
}

function capturedProvenance(): CapturedGameProviderProvenanceBinding {
	return {
		sink: {
			recordRequest: async () => createGameProvenanceAlias("record", "request"),
			recordResponse: async () => undefined,
		},
		scopeId: "scope",
		baseInstructions: {
			sourceId: "agent",
			referenceId: "empty-base-instructions",
			revision: "empty",
			contentFingerprint: createHash("sha256").update("").digest("hex"),
		},
	};
}

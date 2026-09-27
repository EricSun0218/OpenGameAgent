import type { Context, DeferredHandle, Model } from "@earendil-works/pi-ai";
import { openAIResponsesApi } from "@earendil-works/pi-ai/api/openai-responses.lazy";
import { Type } from "typebox";
import { describe, expect, it } from "vitest";
import { createOpenAIBackgroundResponsesApi } from "../src/openai-background-responses.js";

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

const CONTEXT: Context = {
	messages: [{ role: "user", content: "continue", timestamp: 1 }],
};

const CREATED_RESPONSE = {
	id: "resp_background",
	object: "response",
	created_at: 1,
	status: "in_progress",
	model: MODEL.id,
	output: [],
};

const COMPLETED_RESPONSE = {
	...CREATED_RESPONSE,
	status: "completed",
	output: [
		{
			id: "message_1",
			type: "message",
			status: "completed",
			role: "assistant",
			phase: "final_answer",
			content: [{ type: "output_text", text: "finished", annotations: [], logprobs: [] }],
		},
	],
	usage: {
		input_tokens: 2,
		input_tokens_details: { cached_tokens: 0 },
		output_tokens: 1,
		output_tokens_details: { reasoning_tokens: 0 },
		total_tokens: 3,
	},
};

const HANDLE: DeferredHandle = {
	provider: MODEL.provider,
	modelId: MODEL.id,
	api: MODEL.api,
	id: CREATED_RESPONSE.id,
	data: { sequenceNumber: 0 },
};

describe("OpenAI background Responses Pi Adapter", () => {
	it("delegates foreground requests to Pi without enabling background mode", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		const result = await api
			.streamSimple(MODEL, CONTEXT, {
				apiKey: "test",
				fetch: recordingFetch(requests, [
					sseResponse([
						createdEvent({ ...CREATED_RESPONSE, status: "in_progress" }, 0),
						outputItemAddedEvent(1),
						outputTextDeltaEvent(2),
						outputItemDoneEvent(3),
						completedEvent(4),
					]),
				]),
			})
			.result();

		expect(result).toMatchObject({ stopReason: "stop", content: [{ type: "text", text: "finished" }] });
		expect(requests).toHaveLength(1);
		const payload = await requests[0]?.json();
		expect(payload).toMatchObject({ stream: true, store: false });
		expect(payload).not.toHaveProperty("background");
	});

	it("submits one stored streaming background response and returns the durable Pi handle", async () => {
		const requests: Request[] = [];
		const responseStatuses: number[] = [];
		let payloadCalls = 0;
		let observedPayload: unknown;
		const api = createOpenAIBackgroundResponsesApi();
		const result = await api
			.streamSimple(MODEL, CONTEXT, {
				apiKey: "test",
				deferred: true,
				fetch: recordingFetch(requests, [sseResponse([createdEvent(CREATED_RESPONSE, 0)])]),
				onPayload: (payload) => {
					payloadCalls += 1;
					observedPayload = payload;
					return {
						...(payload as Record<string, unknown>),
						metadata: { test: true },
					};
				},
				onResponse: ({ status }) => {
					responseStatuses.push(status);
				},
			})
			.result();

		expect(result).toMatchObject({
			stopReason: "deferred",
			deferred: HANDLE,
			responseId: CREATED_RESPONSE.id,
		});
		expect(requests).toHaveLength(1);
		expect(await requests[0]?.json()).toMatchObject({
			background: true,
			stream: true,
			store: true,
			metadata: { test: true },
		});
		expect(payloadCalls).toBe(1);
		expect(observedPayload).toMatchObject({ background: true, stream: true, store: true });
		expect(Object.hasOwn(observedPayload as object, "prompt_cache_retention")).toBe(true);
		expect(responseStatuses).toEqual([200]);
	});

	it("never submits when onPayload explicitly replaces the request with null", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		const result = await api
			.streamSimple(MODEL, CONTEXT, {
				apiKey: "test",
				deferred: true,
				fetch: recordingFetch(requests, []),
				onPayload: () => null,
			})
			.result();

		expect(result).toMatchObject({ stopReason: "error" });
		expect(requests).toHaveLength(0);
	});

	it("keeps the forced payload when onPayload returns undefined", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		const result = await api
			.streamSimple(MODEL, CONTEXT, {
				apiKey: "test",
				deferred: true,
				fetch: recordingFetch(requests, [sseResponse([createdEvent(CREATED_RESPONSE, 0)])]),
				onPayload: () => undefined,
			})
			.result();

		expect(result).toMatchObject({ stopReason: "deferred", deferred: HANDLE });
		expect(await requests[0]?.json()).toMatchObject({ background: true, stream: true, store: true });
	});

	it("never submits when onPayload throws or disables background storage", async () => {
		const api = createOpenAIBackgroundResponsesApi();
		for (const onPayload of [
			() => {
				throw new Error("payload hook failed");
			},
			(payload: unknown) => ({ ...(payload as Record<string, unknown>), store: false }),
		]) {
			const requests: Request[] = [];
			const result = await api
				.streamSimple(MODEL, CONTEXT, {
					apiKey: "test",
					deferred: true,
					fetch: recordingFetch(requests, []),
					onPayload,
				})
				.result();
			expect(result).toMatchObject({ stopReason: "error" });
			expect(requests).toHaveLength(0);
		}
	});

	it("keeps an admitted handle recoverable when onResponse throws", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		const callbackError = Object.assign(new Error("telemetry unavailable"), { code: "TELEMETRY_DOWN" });
		const result = await api
			.streamSimple(MODEL, CONTEXT, {
				apiKey: "test",
				deferred: true,
				fetch: recordingFetch(requests, [sseResponse([createdEvent(CREATED_RESPONSE, 0)])]),
				onResponse: () => {
					throw callbackError;
				},
			})
			.result();

		expect(result).toMatchObject({
			stopReason: "deferred",
			deferred: HANDLE,
			diagnostics: [{ type: "provider_response_callback_failure", error: { message: "telemetry unavailable" } }],
		});
		expect(result.diagnostics?.[0]?.error).toMatchObject({ code: "TELEMETRY_DOWN" });
		expect(result.diagnostics?.[0]?.error?.stack).toContain("telemetry unavailable");
		expect(requests).toHaveLength(1);
	});

	it("reports the HTTP response before consuming a malformed identity stream", async () => {
		const requests: Request[] = [];
		const responseStatuses: number[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		const result = await api
			.streamSimple(MODEL, CONTEXT, {
				apiKey: "test",
				deferred: true,
				fetch: recordingFetch(requests, [sseResponse([])]),
				onResponse: ({ status }) => {
					responseStatuses.push(status);
				},
			})
			.result();

		expect(result).toMatchObject({ stopReason: "error" });
		expect(responseStatuses).toEqual([200]);
	});

	it("preserves Pi session affinity headers for the background submission", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		await api
			.streamSimple(MODEL, CONTEXT, {
				apiKey: "test",
				deferred: true,
				sessionId: "session-test",
				fetch: recordingFetch(requests, [sseResponse([createdEvent(CREATED_RESPONSE, 0)])]),
			})
			.result();

		expect(requests).toHaveLength(1);
		expect(requests[0]?.headers.get("session_id")).toBe("session-test");
		expect(requests[0]?.headers.get("x-client-request-id")).toBe("session-test");
		expect(requests[0]?.headers.get("user-agent")).toMatch(/^pi \(/);
	});

	it("accepts Pi's Cloudflare AI Gateway authorization header without an API key", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		const result = await api
			.streamSimple(MODEL, CONTEXT, {
				deferred: true,
				headers: { "CF-AIG-Authorization": "Bearer gateway-test" },
				fetch: recordingFetch(requests, [sseResponse([createdEvent(CREATED_RESPONSE, 0)])]),
			})
			.result();

		expect(result).toMatchObject({ stopReason: "deferred", deferred: HANDLE });
		expect(requests).toHaveLength(1);
		expect(requests[0]?.headers.get("cf-aig-authorization")).toBe("Bearer gateway-test");
	});

	it("performs one status check and preserves the same handle while the provider is pending", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		if (!api.fetchDeferred) throw new Error("Expected deferred fetch support.");

		const result = await api
			.fetchDeferred(MODEL, HANDLE, {
				apiKey: "test",
				fetch: recordingFetch(requests, [Response.json(CREATED_RESPONSE)]),
				wait: 0,
			})
			.result();

		expect(result).toMatchObject({ stopReason: "deferred", deferred: HANDLE });
		expect(requests).toHaveLength(1);
		expect(requests[0]?.method).toBe("GET");
		expect(new URL(requests[0]?.url ?? "https://invalid").pathname).toBe("/v1/responses/resp_background");
	});

	it("replays the saved background stream cursor through Pi's response converter", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		if (!api.fetchDeferred) throw new Error("Expected deferred fetch support.");

		const result = await api
			.fetchDeferred(MODEL, HANDLE, {
				apiKey: "test",
				fetch: recordingFetch(requests, [
					Response.json(COMPLETED_RESPONSE),
					sseResponse([outputItemAddedEvent(1), outputTextDeltaEvent(2), outputItemDoneEvent(3), completedEvent(4)]),
				]),
			})
			.result();

		expect(result).toMatchObject({
			stopReason: "stop",
			responseId: CREATED_RESPONSE.id,
			content: [{ type: "text", text: "finished" }],
			usage: { input: 2, output: 1, totalTokens: 3 },
		});
		expect(requests).toHaveLength(2);
		const replayUrl = new URL(requests[1]?.url ?? "https://invalid");
		expect(replayUrl.searchParams.get("stream")).toBe("true");
		expect(replayUrl.searchParams.get("starting_after")).toBe("0");
	});

	it("persists Pi grammar Tool conversion data across handle serialization", async () => {
		const requests: Request[] = [];
		const grammarModel: Model<"openai-responses"> = {
			...MODEL,
			compat: { supportsOpenAIGrammarTools: true },
		};
		const grammarContext: Context = {
			...CONTEXT,
			tools: [
				{
					name: "apply_patch",
					description: "Apply one patch",
					parameters: Type.Object({ patch: Type.String() }),
					constrainedSampling: { type: "grammar", variants: { openai_regex: ".+" } },
				},
			],
		};
		const api = createOpenAIBackgroundResponsesApi();
		const submitted = await api
			.streamSimple(grammarModel, grammarContext, {
				apiKey: "test",
				deferred: true,
				fetch: recordingFetch(requests, [sseResponse([createdEvent(CREATED_RESPONSE, 0)])]),
			})
			.result();
		const restoredHandle = JSON.parse(JSON.stringify(submitted.deferred)) as DeferredHandle;
		if (!api.fetchDeferred) throw new Error("Expected deferred fetch support.");
		const completed = await api
			.fetchDeferred(grammarModel, restoredHandle, {
				apiKey: "test",
				fetch: recordingFetch(requests, [
					Response.json(COMPLETED_RESPONSE),
					sseResponse([
						customToolAddedEvent(1),
						customToolInputDoneEvent(2),
						customToolDoneEvent(3),
						completedEvent(4),
					]),
				]),
			})
			.result();

		expect(completed).toMatchObject({
			stopReason: "toolUse",
			content: [{ type: "toolCall", name: "apply_patch", arguments: { patch: "replace x" } }],
		});
	});

	it.each([
		["flex", "gpt-priced"],
		["priority", "gpt-5.5"],
	] as const)("matches Pi %s service-tier usage pricing", async (serviceTier, modelId) => {
		const requests: Request[] = [];
		const pricedModel: Model<"openai-responses"> = {
			...MODEL,
			id: modelId,
			cost: { input: 1_000_000, output: 1_000_000, cacheRead: 1_000_000, cacheWrite: 1_000_000 },
		};
		const pricedResponse = { ...COMPLETED_RESPONSE, model: modelId, service_tier: serviceTier };
		const pricedHandle: DeferredHandle = {
			...HANDLE,
			modelId,
			data: { sequenceNumber: 0, serviceTier },
		};
		const api = createOpenAIBackgroundResponsesApi();
		const foreground = await openAIResponsesApi()
			.streamSimple(pricedModel, CONTEXT, {
				apiKey: "test",
				samplingParams: { service_tier: serviceTier },
				fetch: recordingFetch(
					[],
					[
						sseResponse([
							createdEvent({ ...CREATED_RESPONSE, model: modelId }, 0),
							outputItemAddedEvent(1),
							outputTextDeltaEvent(2),
							outputItemDoneEvent(3),
							completedEventFor(pricedResponse, 4),
						]),
					],
				),
			})
			.result();
		if (!api.fetchDeferred) throw new Error("Expected deferred fetch support.");
		const result = await api
			.fetchDeferred(pricedModel, pricedHandle, {
				apiKey: "test",
				fetch: recordingFetch(requests, [
					Response.json(pricedResponse),
					sseResponse([
						outputItemAddedEvent(1),
						outputTextDeltaEvent(2),
						outputItemDoneEvent(3),
						completedEventFor(pricedResponse, 4),
					]),
				]),
			})
			.result();

		expect(result.usage.cost).toEqual(foreground.usage.cost);
	});

	it("reports abort when the signal wins after a terminal provider event", async () => {
		const requests: Request[] = [];
		const controller = new AbortController();
		const api = createOpenAIBackgroundResponsesApi();
		if (!api.fetchDeferred) throw new Error("Expected deferred fetch support.");
		const result = await api
			.fetchDeferred(MODEL, HANDLE, {
				apiKey: "test",
				signal: controller.signal,
				fetch: recordingFetch(requests, [
					Response.json(COMPLETED_RESPONSE),
					sseResponseThenAbort([completedEvent(4)], controller),
				]),
			})
			.result();

		expect(result).toMatchObject({ stopReason: "aborted" });
	});

	it("removes Pi streaming scratch fields when replay fails", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		if (!api.fetchDeferred) throw new Error("Expected deferred fetch support.");
		const result = await api
			.fetchDeferred(MODEL, HANDLE, {
				apiKey: "test",
				fetch: recordingFetch(requests, [
					Response.json({ ...COMPLETED_RESPONSE, status: "failed" }),
					sseResponse([functionToolAddedEvent(1), functionToolDeltaEvent(2), failedEvent(3)]),
				]),
			})
			.result();

		expect(result).toMatchObject({ stopReason: "error", content: [{ type: "toolCall" }] });
		expect(result.responseId).toBe(HANDLE.id);
		expect(result.content[0]).not.toHaveProperty("partialJson");
		expect(result.content[0]).not.toHaveProperty("customInput");
		expect(result.content[0]).not.toHaveProperty("index");
	});

	it("cancels exactly once through the official background endpoint", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		if (!api.cancelDeferred) throw new Error("Expected deferred cancellation support.");

		await api.cancelDeferred(MODEL, HANDLE, {
			apiKey: "test",
			fetch: recordingFetch(requests, [Response.json({ ...CREATED_RESPONSE, status: "cancelled" })]),
		});

		expect(requests).toHaveLength(1);
		expect(requests[0]?.method).toBe("POST");
		expect(new URL(requests[0]?.url ?? "https://invalid").pathname).toBe("/v1/responses/resp_background/cancel");
	});

	it("does not retry an uncertain background submission", async () => {
		const requests: Request[] = [];
		const api = createOpenAIBackgroundResponsesApi();
		const result = await api
			.streamSimple(MODEL, CONTEXT, {
				apiKey: "test",
				deferred: true,
				maxRetries: 5,
				fetch: recordingFetch(requests, [Response.json({ error: { message: "uncertain" } }, { status: 500 })]),
			})
			.result();

		expect(result).toMatchObject({ stopReason: "error" });
		expect(requests).toHaveLength(1);
	});
});

function recordingFetch(requests: Request[], responses: Response[]): typeof fetch {
	return async (input, init) => {
		const request = new Request(input, init);
		requests.push(request.clone());
		const response = responses.shift();
		if (!response) throw new Error("Unexpected provider request.");
		return response;
	};
}

function sseResponse(events: readonly Record<string, unknown>[]): Response {
	const body = events.map((event) => `data: ${JSON.stringify(event)}\n\n`).join("");
	return new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } });
}

function createdEvent(response: Record<string, unknown>, sequenceNumber: number): Record<string, unknown> {
	return { type: "response.created", response, sequence_number: sequenceNumber };
}

function outputItemAddedEvent(sequenceNumber: number): Record<string, unknown> {
	return {
		type: "response.output_item.added",
		output_index: 0,
		sequence_number: sequenceNumber,
		item: {
			id: "message_1",
			type: "message",
			status: "in_progress",
			role: "assistant",
			phase: "final_answer",
			content: [],
		},
	};
}

function outputTextDeltaEvent(sequenceNumber: number): Record<string, unknown> {
	return {
		type: "response.output_text.delta",
		output_index: 0,
		content_index: 0,
		item_id: "message_1",
		sequence_number: sequenceNumber,
		delta: "finished",
		logprobs: [],
	};
}

function outputItemDoneEvent(sequenceNumber: number): Record<string, unknown> {
	return {
		type: "response.output_item.done",
		output_index: 0,
		sequence_number: sequenceNumber,
		item: COMPLETED_RESPONSE.output[0],
	};
}

function completedEvent(sequenceNumber: number): Record<string, unknown> {
	return { type: "response.completed", response: COMPLETED_RESPONSE, sequence_number: sequenceNumber };
}

function completedEventFor(response: Record<string, unknown>, sequenceNumber: number): Record<string, unknown> {
	return { type: "response.completed", response, sequence_number: sequenceNumber };
}

function customToolAddedEvent(sequenceNumber: number): Record<string, unknown> {
	return {
		type: "response.output_item.added",
		output_index: 0,
		sequence_number: sequenceNumber,
		item: {
			id: "custom_1",
			type: "custom_tool_call",
			status: "in_progress",
			call_id: "call_1",
			name: "apply_patch",
			input: "",
		},
	};
}

function customToolInputDoneEvent(sequenceNumber: number): Record<string, unknown> {
	return {
		type: "response.custom_tool_call_input.done",
		output_index: 0,
		sequence_number: sequenceNumber,
		item_id: "custom_1",
		input: "replace x",
	};
}

function customToolDoneEvent(sequenceNumber: number): Record<string, unknown> {
	return {
		type: "response.output_item.done",
		output_index: 0,
		sequence_number: sequenceNumber,
		item: {
			id: "custom_1",
			type: "custom_tool_call",
			status: "completed",
			call_id: "call_1",
			name: "apply_patch",
			input: "replace x",
		},
	};
}

function functionToolAddedEvent(sequenceNumber: number): Record<string, unknown> {
	return {
		type: "response.output_item.added",
		output_index: 0,
		sequence_number: sequenceNumber,
		item: {
			id: "function_1",
			type: "function_call",
			status: "in_progress",
			call_id: "call_1",
			name: "act",
			arguments: "",
		},
	};
}

function functionToolDeltaEvent(sequenceNumber: number): Record<string, unknown> {
	return {
		type: "response.function_call_arguments.delta",
		output_index: 0,
		sequence_number: sequenceNumber,
		item_id: "function_1",
		delta: '{"x":',
	};
}

function failedEvent(sequenceNumber: number): Record<string, unknown> {
	return {
		type: "response.failed",
		sequence_number: sequenceNumber,
		response: {
			...CREATED_RESPONSE,
			status: "failed",
			error: { code: "server_error", message: "failed" },
		},
	};
}

function sseResponseThenAbort(events: readonly Record<string, unknown>[], controller: AbortController): Response {
	const body = new TextEncoder().encode(events.map((event) => `data: ${JSON.stringify(event)}\n\n`).join(""));
	let sent = false;
	return new Response(
		new ReadableStream({
			pull(streamController) {
				if (!sent) {
					sent = true;
					streamController.enqueue(body);
					return;
				}
				controller.abort();
				streamController.close();
			},
		}),
		{ status: 200, headers: { "content-type": "text/event-stream" } },
	);
}

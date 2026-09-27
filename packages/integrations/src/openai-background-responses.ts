import {
	type Api,
	type AssistantMessage,
	type AssistantMessageEventStream,
	appendAssistantMessageDiagnostic,
	type Context,
	createAssistantMessageDiagnostic,
	createAssistantMessageEventStream,
	type DeferredCancelOptions,
	type DeferredFetchOptions,
	type DeferredHandle,
	type JsonValue,
	type Model,
	type ProviderHeaders,
	type ProviderStreams,
	type SimpleStreamOptions,
	type Usage,
} from "@earendil-works/pi-ai";
import { createGrammarToolInputProperties } from "@earendil-works/pi-ai/api/constrained-sampling";
import { openAIResponsesApi } from "@earendil-works/pi-ai/api/openai-responses.lazy";
import { processResponsesStream } from "@earendil-works/pi-ai/api/openai-responses-shared";
import OpenAI from "openai";
import type { ResponseCreatedEvent, ResponseCreateParamsStreaming } from "openai/resources/responses/responses";

const CAPTURE_REQUEST = new Error("capture Pi OpenAI Responses request");
const PI_REQUEST_HEADERS = ["user-agent", "session_id", "x-client-request-id", "x-session-id"] as const;

interface CapturedPiRequest {
	payload: unknown;
	headers: Readonly<Record<string, string>>;
}

/**
 * Add official OpenAI background-response submission to Pi's OpenAI Responses API.
 * Foreground requests remain entirely Pi-owned.
 */
export function createOpenAIBackgroundResponsesApi(): ProviderStreams {
	const foreground = openAIResponsesApi();
	const api: ProviderStreams = {
		stream: foreground.stream,
		streamSimple: (model, context, options) =>
			options?.deferred
				? submitBackground(foreground, model, context, options)
				: foreground.streamSimple(model, context, options),
		fetchDeferred: (model, handle, options) => fetchBackground(model, handle, options),
		cancelDeferred: (model, handle, options) => cancelBackground(model, handle, options),
	};
	return Object.freeze(api);
}

function submitBackground(
	foreground: ProviderStreams,
	model: Model<Api>,
	context: Context,
	options: SimpleStreamOptions,
): AssistantMessageEventStream {
	const stream = createAssistantMessageEventStream();
	const output = pendingMessage(model);
	void (async () => {
		try {
			const captured = await capturePiRequest(foreground, model, context, options);
			const payload = await prepareBackgroundPayload(captured.payload, model, options);
			const client = createClient(model, options, captured.headers);
			const request = client.responses.create(payload, requestOptions(options));
			const { data: providerStream, response } = await request.withResponse();
			try {
				await reportResponse(options, model, response);
			} catch (error) {
				appendAssistantMessageDiagnostic(
					output,
					createAssistantMessageDiagnostic("provider_response_callback_failure", error),
				);
			}

			let created: ResponseCreatedEvent | undefined;
			for await (const event of providerStream) {
				if (event.type === "response.created") {
					created = event;
					break;
				}
			}
			if (!created) throw new Error("OpenAI background stream ended before returning its response identity.");

			const handle: DeferredHandle = {
				provider: model.provider,
				modelId: model.id,
				api: model.api,
				id: created.response.id,
				data: createHandleData(
					created.sequence_number,
					createGrammarToolInputProperties(context.tools, supportsOpenAIGrammarTools(model)),
					payload.service_tier,
				),
			};
			output.responseId = created.response.id;
			output.stopReason = "deferred";
			output.deferred = handle;
			stream.push({ type: "start", partial: output });
			stream.push({ type: "done", reason: "deferred", message: output });
			stream.end();
		} catch (error) {
			fail(stream, output, error, options.signal?.aborted === true);
		}
	})();
	return stream;
}

function fetchBackground(
	model: Model<Api>,
	handle: DeferredHandle,
	options?: DeferredFetchOptions,
): AssistantMessageEventStream {
	const stream = createAssistantMessageEventStream();
	const output = pendingMessage(model);
	void (async () => {
		try {
			assertHandle(model, handle);
			const client = createClient(model, options);
			const statusRequest = client.responses.retrieve(handle.id, {}, requestOptions(options));
			const { data: status, response: statusResponse } = await statusRequest.withResponse();
			await reportResponse(options, model, statusResponse);
			if (status.status === "queued" || status.status === "in_progress") {
				output.responseId = handle.id;
				output.stopReason = "deferred";
				output.deferred = structuredClone(handle);
				stream.push({ type: "start", partial: output });
				stream.push({ type: "done", reason: "deferred", message: output });
				stream.end();
				return;
			}

			const cursor = readSequenceNumber(handle.data);
			output.responseId = handle.id;
			const replayRequest = client.responses.retrieve(
				handle.id,
				{
					stream: true,
					...(cursor === undefined ? {} : { starting_after: cursor }),
				},
				requestOptions(options),
			);
			const { data: replay, response: replayResponse } = await replayRequest.withResponse();
			await reportResponse(options, model, replayResponse);
			stream.push({ type: "start", partial: output });
			await processResponsesStream(replay, output, stream, model, {
				grammarToolInputProperties: readGrammarToolInputProperties(handle.data),
				serviceTier: readServiceTier(handle.data),
				applyServiceTierPricing: (usage, serviceTier) => applyOpenAIProcessingRate(usage, serviceTier, model),
			});
			if (options?.signal?.aborted) throw new Error("Request was aborted");
			if (output.stopReason === "pending") {
				throw new Error("OpenAI background replay ended without a terminal response.");
			}
			if (output.stopReason === "error" || output.stopReason === "aborted") {
				throw new Error(output.errorMessage ?? "OpenAI background response failed.");
			}
			stream.push({ type: "done", reason: output.stopReason, message: output });
			stream.end();
		} catch (error) {
			fail(stream, output, error, options?.signal?.aborted === true);
		}
	})();
	return stream;
}

async function cancelBackground(
	model: Model<Api>,
	handle: DeferredHandle,
	options?: DeferredCancelOptions,
): Promise<void> {
	assertHandle(model, handle);
	const client = createClient(model, options);
	const { response } = await client.responses.cancel(handle.id, requestOptions(options)).withResponse();
	await reportResponse(options, model, response);
}

async function capturePiRequest(
	foreground: ProviderStreams,
	model: Model<Api>,
	context: Context,
	options: SimpleStreamOptions,
): Promise<CapturedPiRequest> {
	let payload: unknown;
	let headers: Readonly<Record<string, string>> | undefined;
	let sawPayload = false;
	const {
		deferred: _deferred,
		fetch: _fetch,
		maxRetries: _maxRetries,
		onPayload: _onPayload,
		onResponse: _onResponse,
		...foregroundOptions
	} = options;
	const captureResult = await foreground
		.streamSimple(model, context, {
			...foregroundOptions,
			maxRetries: 0,
			onPayload: (candidate) => {
				payload = candidate;
				sawPayload = true;
				return candidate;
			},
			fetch: async (input, init) => {
				const request = new Request(input, init);
				headers = capturePiRequestHeaders(request.headers);
				throw CAPTURE_REQUEST;
			},
		})
		.result();
	if (!sawPayload || !headers) {
		throw new Error(captureResult.errorMessage ?? "Pi did not produce an OpenAI Responses request.");
	}
	return { payload, headers };
}

async function prepareBackgroundPayload(
	payload: unknown,
	model: Model<Api>,
	options: SimpleStreamOptions,
): Promise<ResponseCreateParamsStreaming> {
	const candidate = {
		...requireRequestRecord(payload),
		background: true,
		stream: true,
		store: true,
	};
	const replacement = await options.onPayload?.(candidate, model);
	const selected = replacement === undefined ? candidate : requireRequestRecord(replacement);
	if (selected.background !== true || selected.stream !== true || selected.store !== true) {
		throw new Error("OpenAI background Responses require background, stream, and store to remain enabled.");
	}
	return selected as unknown as ResponseCreateParamsStreaming;
}

function requireRequestRecord(payload: unknown): Record<string, unknown> {
	if (payload === null || typeof payload !== "object" || Array.isArray(payload)) {
		throw new TypeError("OpenAI Responses payload replacement must be an object.");
	}
	return payload as Record<string, unknown>;
}

function createClient(
	model: Model<Api>,
	options?: Pick<SimpleStreamOptions, "apiKey" | "fetch" | "headers">,
	piRequestHeaders?: Readonly<Record<string, string>>,
): OpenAI {
	return new OpenAI({
		apiKey: resolveApiKey(model, options?.apiKey, options?.headers),
		baseURL: model.baseUrl,
		dangerouslyAllowBrowser: true,
		...(options?.fetch ? { fetch: options.fetch } : {}),
		defaultHeaders: mergeHeaders(piRequestHeaders, model.headers, options?.headers),
	});
}

function resolveApiKey(model: Model<Api>, apiKey: string | undefined, headers: ProviderHeaders | undefined): string {
	if (apiKey) return apiKey;
	for (const [name, value] of Object.entries(headers ?? {})) {
		const normalizedName = name.toLowerCase();
		if (
			(normalizedName === "authorization" || normalizedName === "cf-aig-authorization") &&
			value !== null &&
			value.trim().length > 0
		) {
			return "unused";
		}
	}
	throw new Error(`No API key for provider: ${model.provider}`);
}

function mergeHeaders(
	piRequestHeaders: Readonly<Record<string, string>> | undefined,
	modelHeaders: Readonly<Record<string, string>> | undefined,
	requestHeaders: ProviderHeaders | undefined,
): Record<string, string> {
	const merged = new Headers();
	for (const source of [piRequestHeaders, modelHeaders, requestHeaders]) {
		for (const [name, value] of Object.entries(source ?? {})) {
			if (value === null) merged.delete(name);
			else merged.set(name, value);
		}
	}
	return Object.fromEntries(merged.entries());
}

function capturePiRequestHeaders(headers: Headers): Record<string, string> {
	const captured: Record<string, string> = {};
	for (const name of PI_REQUEST_HEADERS) {
		const value = headers.get(name);
		if (value !== null) captured[name] = value;
	}
	return captured;
}

function requestOptions(options?: DeferredFetchOptions | DeferredCancelOptions | SimpleStreamOptions) {
	return {
		...(options?.signal ? { signal: options.signal } : {}),
		...(options?.timeoutMs === undefined ? {} : { timeout: options.timeoutMs }),
		maxRetries: 0,
	};
}

async function reportResponse(
	options: DeferredFetchOptions | DeferredCancelOptions | SimpleStreamOptions | undefined,
	model: Model<Api>,
	response: Response,
): Promise<void> {
	await options?.onResponse?.(
		{ status: response.status, headers: Object.fromEntries(response.headers.entries()) },
		model,
	);
}

function pendingMessage(model: Model<Api>): AssistantMessage {
	return {
		role: "assistant",
		content: [],
		api: model.api,
		provider: model.provider,
		model: model.id,
		usage: {
			input: 0,
			output: 0,
			cacheRead: 0,
			cacheWrite: 0,
			totalTokens: 0,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		},
		stopReason: "pending",
		timestamp: Date.now(),
	};
}

function assertHandle(model: Model<Api>, handle: DeferredHandle): void {
	if (handle.provider !== model.provider || handle.modelId !== model.id || handle.api !== model.api) {
		throw new Error("The deferred OpenAI response does not match the selected Pi model.");
	}
}

function readSequenceNumber(data: JsonValue | undefined): number | undefined {
	if (!data || Array.isArray(data) || typeof data !== "object") return undefined;
	const value = data.sequenceNumber;
	return typeof value === "number" ? value : undefined;
}

function createHandleData(
	sequenceNumber: number,
	grammarToolInputProperties: ReadonlyMap<string, string>,
	serviceTier: ResponseCreateParamsStreaming["service_tier"],
): JsonValue {
	return {
		sequenceNumber,
		...(grammarToolInputProperties.size === 0
			? {}
			: { grammarToolInputProperties: Object.fromEntries(grammarToolInputProperties) }),
		...(typeof serviceTier === "string" ? { serviceTier } : {}),
	};
}

function supportsOpenAIGrammarTools(model: Model<Api>): boolean {
	return Boolean(
		model.compat && "supportsOpenAIGrammarTools" in model.compat && model.compat.supportsOpenAIGrammarTools === true,
	);
}

function readGrammarToolInputProperties(data: JsonValue | undefined): ReadonlyMap<string, string> {
	if (!data || Array.isArray(data) || typeof data !== "object") return new Map();
	const value = data.grammarToolInputProperties;
	if (value === undefined) return new Map();
	if (!value || Array.isArray(value) || typeof value !== "object") {
		throw new Error("Deferred OpenAI grammar Tool conversion data is invalid.");
	}
	const entries = Object.entries(value);
	if (entries.some(([, inputProperty]) => typeof inputProperty !== "string")) {
		throw new Error("Deferred OpenAI grammar Tool conversion data is invalid.");
	}
	return new Map(entries as [string, string][]);
}

function readServiceTier(data: JsonValue | undefined): ResponseCreateParamsStreaming["service_tier"] {
	if (!data || Array.isArray(data) || typeof data !== "object") return undefined;
	const value = data.serviceTier;
	return value === "auto" || value === "default" || value === "flex" || value === "scale" || value === "priority"
		? value
		: undefined;
}

function applyOpenAIProcessingRate(
	usage: Usage,
	serviceTier: ResponseCreateParamsStreaming["service_tier"],
	model: Model<Api>,
): void {
	const rate = serviceTier === "flex" ? 0.5 : serviceTier === "priority" ? (model.id === "gpt-5.5" ? 2.5 : 2) : 1;
	if (rate === 1) return;
	usage.cost.input *= rate;
	usage.cost.output *= rate;
	usage.cost.cacheRead *= rate;
	usage.cost.cacheWrite *= rate;
	usage.cost.total = usage.cost.input + usage.cost.output + usage.cost.cacheRead + usage.cost.cacheWrite;
}

function fail(stream: AssistantMessageEventStream, output: AssistantMessage, error: unknown, aborted: boolean): void {
	for (const block of output.content) {
		const scratch = block as typeof block & { index?: number; partialJson?: string; customInput?: unknown };
		delete scratch.index;
		delete scratch.partialJson;
		delete scratch.customInput;
	}
	const reason = aborted ? "aborted" : "error";
	output.stopReason = reason;
	output.errorMessage = error instanceof Error ? error.message : String(error);
	stream.push({ type: "error", reason, error: output });
	stream.end();
}

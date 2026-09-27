import type { AgentTool, StreamFn } from "@earendil-works/pi-agent-core";
import {
	type Api,
	type AssistantMessage,
	type AssistantMessageEventStream,
	type Context,
	createAssistantMessageEventStream,
	type Model,
	type StopReason,
} from "@earendil-works/pi-ai";
import { Type } from "typebox";

export const TEST_MODEL: Model<"openai-responses"> = {
	id: "test-model",
	name: "Test model",
	api: "openai-responses",
	provider: "openai",
	baseUrl: "https://example.invalid",
	reasoning: true,
	input: ["text"],
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
	contextWindow: 1,
	maxTokens: 1,
};

/** Fixture-derived budgets and policy used only by deterministic tests. */
export const TEST_TOOL_LIMITS = Object.freeze({
	maxTools: 32,
	maxCatalogBytes: 131_072,
	maxSchemaDepth: 32,
	maxRequestSources: 32,
	maxPolicyRevisionBytes: 1_024,
	maxArgumentsBytes: 32_768,
	maxArgumentsDepth: 32,
	maxResultContentParts: 32,
	maxResultBytes: 131_072,
	maxResultDepth: 32,
	maxResultUpdates: 128,
});

export function allowAllTestTools(
	_input: unknown,
	candidates: readonly { readonly name: string }[],
): { revision: string; visibleToolNames: readonly string[] } {
	return { revision: "test-policy", visibleToolNames: candidates.map((candidate) => candidate.name) };
}

const EMPTY_USAGE = {
	input: 0,
	output: 0,
	cacheRead: 0,
	cacheWrite: 0,
	totalTokens: 0,
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
};

export function assistant(
	content: AssistantMessage["content"],
	stopReason: StopReason = "stop",
	model: Readonly<Pick<Model<Api>, "api" | "provider" | "id">> = TEST_MODEL,
): AssistantMessage {
	return {
		role: "assistant",
		content,
		api: model.api,
		provider: model.provider,
		model: model.id,
		usage: EMPTY_USAGE,
		stopReason,
		timestamp: 1,
	};
}

export function textResponse(
	text: string,
	deltas: readonly string[] = [],
	model: Readonly<Pick<Model<Api>, "api" | "provider" | "id">> = TEST_MODEL,
): AssistantMessageEventStream {
	const finalMessage = assistant([{ type: "text", text }], "stop", model);
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		stream.push({ type: "start", partial: assistant([], "pending", model) });
		let accumulated = "";
		stream.push({
			type: "text_start",
			contentIndex: 0,
			partial: assistant([{ type: "text", text: "" }], "pending", model),
		});
		for (const delta of deltas.length > 0 ? deltas : [text]) {
			accumulated += delta;
			stream.push({
				type: "text_delta",
				contentIndex: 0,
				delta,
				partial: assistant([{ type: "text", text: accumulated }], "pending", model),
			});
		}
		stream.push({ type: "text_end", contentIndex: 0, content: text, partial: finalMessage });
		stream.push({ type: "done", reason: "stop", message: finalMessage });
	});
	return stream;
}

export function messageResponse(message: AssistantMessage): AssistantMessageEventStream {
	const stream = createAssistantMessageEventStream();
	queueMicrotask(() => {
		stream.push({ type: "start", partial: partialMessage(message, []) });
		if (message.stopReason === "error" || message.stopReason === "aborted") {
			stream.push({ type: "error", reason: message.stopReason, error: message });
			return;
		}
		if (message.stopReason === "pending") throw new Error("A final fake message cannot be pending.");
		const content: AssistantMessage["content"] = [];
		for (const part of message.content) {
			const contentIndex = content.length;
			switch (part.type) {
				case "text":
					stream.push({
						type: "text_start",
						contentIndex,
						partial: partialMessage(message, [...content, { type: "text", text: "" }]),
					});
					stream.push({
						type: "text_delta",
						contentIndex,
						delta: part.text,
						partial: partialMessage(message, [...content, part]),
					});
					content.push(part);
					stream.push({
						type: "text_end",
						contentIndex,
						content: part.text,
						partial: partialMessage(message, [...content]),
					});
					break;
				case "thinking":
					stream.push({
						type: "thinking_start",
						contentIndex,
						partial: partialMessage(message, [...content, { type: "thinking", thinking: "" }]),
					});
					stream.push({
						type: "thinking_delta",
						contentIndex,
						delta: part.thinking,
						partial: partialMessage(message, [...content, part]),
					});
					content.push(part);
					stream.push({
						type: "thinking_end",
						contentIndex,
						content: part.thinking,
						partial: partialMessage(message, [...content]),
					});
					break;
				case "toolCall":
					stream.push({
						type: "toolcall_start",
						contentIndex,
						partial: partialMessage(message, [...content, { ...part, arguments: {} }]),
					});
					stream.push({
						type: "toolcall_delta",
						contentIndex,
						delta: JSON.stringify(part.arguments),
						partial: partialMessage(message, [...content, part]),
					});
					content.push(part);
					stream.push({
						type: "toolcall_end",
						contentIndex,
						toolCall: part,
						partial: partialMessage(message, [...content]),
					});
					break;
			}
		}
		stream.push({ type: "done", reason: message.stopReason, message });
	});
	return stream;
}

function partialMessage(
	message: AssistantMessage,
	content: AssistantMessage["content"],
): AssistantMessage & { stopReason: "pending" } {
	return {
		role: "assistant",
		content,
		api: message.api,
		provider: message.provider,
		model: message.model,
		...(message.responseModel === undefined ? {} : { responseModel: message.responseModel }),
		usage: EMPTY_USAGE,
		stopReason: "pending",
		timestamp: message.timestamp,
	};
}

export interface ControlledResponse {
	readonly streamFn: StreamFn;
	readonly started: Promise<void>;
	finish(text?: string): void;
}

export function controlledResponse(): ControlledResponse {
	const stream = createAssistantMessageEventStream();
	let markStarted: (() => void) | undefined;
	const started = new Promise<void>((resolve) => {
		markStarted = resolve;
	});
	let settled = false;
	return {
		streamFn: (_model, _context, options) => {
			stream.push({ type: "start", partial: assistant([], "pending") });
			markStarted?.();
			const abort = () => {
				if (settled) return;
				settled = true;
				const error = { ...assistant([], "aborted"), errorMessage: "aborted" };
				stream.push({ type: "error", reason: "aborted", error });
			};
			if (options?.signal?.aborted) abort();
			else options?.signal?.addEventListener("abort", abort, { once: true });
			return stream;
		},
		started,
		finish: (text = "done") => {
			if (settled) return;
			settled = true;
			const message = assistant([{ type: "text", text }]);
			stream.push({ type: "done", reason: "stop", message });
		},
	};
}

export function scriptedStream(
	responses: readonly ((context: Context) => AssistantMessageEventStream)[],
	contexts: Context[] = [],
): StreamFn {
	let index = 0;
	return (_model, context) => {
		contexts.push({
			...(context.systemPrompt === undefined ? {} : { systemPrompt: context.systemPrompt }),
			messages: structuredClone(context.messages),
			...(context.tools === undefined ? {} : { tools: [...context.tools] }),
		});
		const response = responses[index++];
		if (!response) throw new Error("The fake provider received an unexpected request.");
		return response(context);
	};
}

export function tool(name: string, execute: AgentTool["execute"]): AgentTool {
	return { name, label: name, description: `${name} test tool`, parameters: Type.Object({}), execute };
}

export function toolCall(id: string, name: string): AssistantMessage["content"][number] {
	return { type: "toolCall", id, name, arguments: {} };
}

export function deferredVoid(): { promise: Promise<void>; resolve: () => void } {
	let resolvePromise: (() => void) | undefined;
	const promise = new Promise<void>((resolve) => {
		resolvePromise = resolve;
	});
	return { promise, resolve: () => resolvePromise?.() };
}

import type { AgentMessage } from "@earendil-works/pi-agent-core";
import type { Api, AssistantMessage, Message, Model } from "@earendil-works/pi-ai";
import type { DurableTranscriptMessage, JsonValue } from "@opengameagent/protocol";
import { captureTranscriptMessage } from "./durable-transcript.js";
import { safeRetainedBoundaries } from "./transcript-history.js";

export interface TranscriptContextTool {
	readonly name: string;
	readonly description: string;
	readonly parameters: JsonValue;
}

export interface TranscriptContextCountInput {
	readonly model: Model<Api>;
	readonly systemPrompt: string;
	readonly tools: readonly TranscriptContextTool[];
	readonly messages: readonly AgentMessage[];
}

export type TranscriptContextCounter = (
	input: Readonly<TranscriptContextCountInput>,
	signal: AbortSignal,
) => number | Promise<number>;

export interface TranscriptSummaryRequest {
	readonly messages: readonly DurableTranscriptMessage[];
	readonly safeRetainedFrom: readonly number[];
	readonly maxRetainedContextTokens: number;
	readonly maxOutputTokens: number;
}

export interface TranscriptSummaryResult {
	readonly message: AssistantMessage;
	readonly retainedFrom: number;
}

export type TranscriptSummarizer = (
	request: Readonly<TranscriptSummaryRequest>,
	signal: AbortSignal,
) => TranscriptSummaryResult | Promise<TranscriptSummaryResult>;

export interface TranscriptCompactorOptions {
	readonly reserveTokens: number;
	readonly maxRetainedContextTokens: number;
	readonly maxSummaryOutputTokens: number;
	readonly countContext: TranscriptContextCounter;
	readonly summarize: TranscriptSummarizer;
}

export interface TranscriptCompactionInput {
	readonly model: Model<Api>;
	readonly systemPrompt: string;
	readonly tools: readonly TranscriptContextTool[];
	readonly messages: readonly DurableTranscriptMessage[];
	readonly providerMessages: readonly AgentMessage[];
	readonly signal: AbortSignal;
}

export interface TranscriptCompactionPlan {
	readonly replacement: readonly DurableTranscriptMessage[];
}

export function transcriptCompactionMessages(messages: readonly AgentMessage[]): readonly DurableTranscriptMessage[] {
	if (!Array.isArray(messages)) throw new TypeError("The transcript compaction message list is invalid.");
	const captured: DurableTranscriptMessage[] = [];
	for (const message of messages) {
		if (!isPiMessage(message)) throw new TypeError("Transcript compaction does not support custom Pi messages.");
		const durable = captureTranscriptMessage(message);
		if (!durable) throw new TypeError("Transcript compaction requires terminal Pi messages.");
		captured.push(durable);
	}
	return Object.freeze(captured);
}

export class TranscriptCompactor {
	private readonly reserveTokens: number;
	private readonly maxRetainedContextTokens: number;
	private readonly maxSummaryOutputTokens: number;
	private readonly countContext: TranscriptContextCounter;
	private readonly summarize: TranscriptSummarizer;

	constructor(options: TranscriptCompactorOptions) {
		this.reserveTokens = positiveSafeInteger("reserveTokens", options.reserveTokens);
		this.maxRetainedContextTokens = positiveSafeInteger("maxRetainedContextTokens", options.maxRetainedContextTokens);
		this.maxSummaryOutputTokens = positiveSafeInteger("maxSummaryOutputTokens", options.maxSummaryOutputTokens);
		if (typeof options.countContext !== "function") throw new TypeError("countContext must be a function.");
		if (typeof options.summarize !== "function") throw new TypeError("summarize must be a function.");
		this.countContext = options.countContext;
		this.summarize = options.summarize;
	}

	async prepare(input: TranscriptCompactionInput): Promise<TranscriptCompactionPlan | undefined> {
		input.signal.throwIfAborted();
		const contextWindow = positiveSafeInteger("model.contextWindow", input.model.contextWindow);
		if (this.reserveTokens >= contextWindow) {
			throw new RangeError("reserveTokens must be smaller than the selected model context window.");
		}
		const source = input.messages;
		const providerSource = input.providerMessages;
		if (!Array.isArray(providerSource) || providerSource.length !== source.length) {
			throw new TypeError("The durable and provider transcript histories are not aligned.");
		}
		const context = {
			model: input.model,
			systemPrompt: input.systemPrompt,
			tools: input.tools,
		};
		const callbackInput = (messages: readonly AgentMessage[]): TranscriptContextCountInput => ({
			...context,
			messages,
		});
		const sourceTokens = await this.countContext(callbackInput(providerSource), input.signal);
		input.signal.throwIfAborted();
		const admittedSourceTokens = nonNegativeSafeInteger("context token count", sourceTokens);
		if (admittedSourceTokens <= contextWindow - this.reserveTokens) return undefined;

		const safeRetainedFrom = safeRetainedBoundaries(source);
		const allowedRetainedFrom = new Set(safeRetainedFrom);
		const summaryMessages = transcriptSummaryMessages(source);
		const result = await this.summarize(
			{
				messages: summaryMessages,
				safeRetainedFrom,
				maxRetainedContextTokens: this.maxRetainedContextTokens,
				maxOutputTokens: this.maxSummaryOutputTokens,
			},
			input.signal,
		);
		input.signal.throwIfAborted();
		const retainedFrom = result.retainedFrom;
		if (!allowedRetainedFrom.has(retainedFrom)) {
			throw new TypeError("The transcript summarizer selected an invalid retained boundary.");
		}
		const summary = captureSummary(result.message, this.maxSummaryOutputTokens);
		const retained = source.slice(retainedFrom);
		const providerRetained = providerSource.slice(retainedFrom);
		const retainedTokens = nonNegativeSafeInteger(
			"retained context token count",
			await this.countContext(callbackInput(providerRetained), input.signal),
		);
		input.signal.throwIfAborted();
		if (retainedTokens > this.maxRetainedContextTokens) {
			throw new RangeError("The retained transcript context exceeds its token budget.");
		}
		const replacement = [summary, ...retained];
		const providerReplacement = [materializeSummary(summary), ...providerRetained];
		const replacementTokens = nonNegativeSafeInteger(
			"replacement context token count",
			await this.countContext(callbackInput(providerReplacement), input.signal),
		);
		input.signal.throwIfAborted();
		if (replacementTokens > contextWindow - this.reserveTokens) {
			throw new RangeError("The compacted transcript does not leave the required model reserve.");
		}
		return Object.freeze({ replacement: Object.freeze(replacement) });
	}
}

function materializeSummary(message: Extract<DurableTranscriptMessage, { role: "user" }>): AgentMessage {
	return {
		role: "user",
		content: message.content.map((part) => {
			if (part.type !== "text") throw new TypeError("The transcript summary contains media.");
			return { type: "text" as const, text: part.text };
		}),
		timestamp: message.timestamp,
	};
}

function transcriptSummaryMessages(messages: readonly DurableTranscriptMessage[]): readonly DurableTranscriptMessage[] {
	return messages.map((message) => {
		if (message.role !== "assistant") return message;
		return {
			role: message.role,
			content: message.content.filter((part) => part.type !== "thinking"),
			api: message.api,
			provider: message.provider,
			model: message.model,
			stopReason: message.stopReason,
			timestamp: message.timestamp,
		};
	});
}

function captureSummary(
	message: AssistantMessage,
	maxOutputTokens: number,
): Extract<DurableTranscriptMessage, { role: "user" }> {
	if (message.stopReason !== "stop") throw new TypeError("The transcript summary is not a completed response.");
	const outputTokens = nonNegativeSafeInteger("summary output token count", message.usage.output);
	if (outputTokens > maxOutputTokens) throw new RangeError("The transcript summary exceeds its output token budget.");
	const content: Array<{ type: "text"; text: string }> = [];
	for (const part of message.content) {
		if (part.type === "thinking") {
			if (part.thinkingSignature || part.redacted === true) {
				throw new TypeError("The transcript summary contains provider continuation state.");
			}
			continue;
		}
		if (part.type !== "text") throw new TypeError("The transcript summary must not call Tools.");
		if (part.textSignature) throw new TypeError("The transcript summary contains provider continuation state.");
		content.push({ type: "text", text: part.text });
	}
	if (content.length === 0 || content.every((part) => part.text.trim().length === 0)) {
		throw new TypeError("The transcript summary is empty.");
	}
	return {
		role: "user",
		contentFormat: "parts",
		content,
		timestamp: message.timestamp,
	};
}

function positiveSafeInteger(name: string, value: number): number {
	if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer.`);
	return value;
}

function nonNegativeSafeInteger(name: string, value: number): number {
	if (!Number.isSafeInteger(value) || value < 0) throw new TypeError(`${name} must be a non-negative safe integer.`);
	return value;
}

function isPiMessage(value: AgentMessage): value is Message {
	return value.role === "user" || value.role === "assistant" || value.role === "toolResult";
}

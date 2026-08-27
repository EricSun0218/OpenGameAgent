import type {
	GameConversationContent,
	GameConversationMessage,
	GameConversationSnapshot,
	GameResolvedModel,
	GameSessionKey,
	GameUsage,
} from "@opengameagent/protocol";

export interface GameTranscriptSummaryRequest {
	transcript: string;
	maximumOutputTokens: number;
}

export interface GameTranscriptSummaryResult {
	summary: string;
	usage?: GameUsage;
}

export interface GameTranscriptSummarizer {
	summarize(request: GameTranscriptSummaryRequest, signal: AbortSignal): Promise<GameTranscriptSummaryResult>;
}

export interface GameConversationCompactionRequest {
	session: GameSessionKey;
	snapshot: GameConversationSnapshot;
	model: GameResolvedModel;
}

export interface GameConversationCompactionResult {
	messages: readonly GameConversationMessage[];
	changed: boolean;
	tokensBefore: number;
	tokensAfter: number;
	summarizedMessages: number;
	usage?: GameUsage;
}

export interface GameConversationCompactor {
	compact(request: GameConversationCompactionRequest, signal: AbortSignal): Promise<GameConversationCompactionResult>;
}

export interface SummarizingGameConversationCompactorOptions {
	summarizer: GameTranscriptSummarizer;
	reserveTokens?: number;
	keepRecentTokens?: number;
	maximumSummaryTokens?: number;
	maximumSummaryCharacters?: number;
	maximumSummaryInputCharacters?: number;
	maximumMessages?: number;
}

const IMAGE_TOKEN_ESTIMATE = 1_200;
const DEFAULT_RESERVE_TOKENS = 16_384;
const DEFAULT_KEEP_RECENT_TOKENS = 20_000;
const DEFAULT_MAXIMUM_SUMMARY_TOKENS = 2_048;
const DEFAULT_MAXIMUM_SUMMARY_CHARACTERS = 32_000;
const DEFAULT_MAXIMUM_SUMMARY_INPUT_CHARACTERS = 512_000;
const DEFAULT_MAXIMUM_MESSAGES = 8_192;
const MAXIMUM_TOOL_RESULT_CHARACTERS = 2_000;

function requireInteger(value: number, minimum: number, maximum: number, name: string): number {
	if (!Number.isSafeInteger(value) || value < minimum || value > maximum) {
		throw new RangeError(`${name} must be an integer between ${minimum} and ${maximum}.`);
	}
	return value;
}

function jsonLength(value: unknown): number {
	try {
		return JSON.stringify(value).length;
	} catch {
		return 0;
	}
}

function contentCharacters(content: string | readonly GameConversationContent[]): number {
	if (typeof content === "string") return content.length;
	let characters = 0;
	for (const part of content) {
		switch (part.type) {
			case "text":
			case "reasoning":
				characters += part.text.length;
				break;
			case "image":
				characters += IMAGE_TOKEN_ESTIMATE * 4;
				break;
			case "toolCall":
				characters += part.name.length + jsonLength(part.arguments);
				break;
		}
	}
	return characters;
}

export function estimateGameConversationMessageTokens(message: GameConversationMessage): number {
	if (message.role === "summary") return Math.ceil(message.summary.length / 4);
	return Math.ceil(contentCharacters(message.content) / 4);
}

export function estimateGameConversationTokens(messages: readonly GameConversationMessage[]): number {
	return messages.reduce((total, message) => total + estimateGameConversationMessageTokens(message), 0);
}

function visibleText(content: string | readonly GameConversationContent[]): string[] {
	if (typeof content === "string") return content.length === 0 ? [] : [content];
	return content.flatMap((part) => (part.type === "text" && part.text.length > 0 ? [part.text] : []));
}

function truncate(value: string, maximum: number): string {
	if (value.length <= maximum) return value;
	return `${value.slice(0, maximum)}\n[truncated ${value.length - maximum} characters]`;
}

function sanitizeMessage(message: GameConversationMessage): string | undefined {
	if (message.role === "summary") return `[Prior summary]\n${message.summary}`;
	if (message.role === "user") {
		const text = visibleText(message.content).join("\n");
		const hasImage = Array.isArray(message.content) && message.content.some((part) => part.type === "image");
		const value = [text, hasImage ? "[image omitted]" : ""].filter(Boolean).join("\n");
		return value.length === 0 ? undefined : `[User]\n${value}`;
	}
	if (message.role === "assistant") {
		const text = visibleText(message.content).join("\n");
		const tools = message.content
			.filter((part) => part.type === "toolCall")
			.map((part) => `[called tool: ${part.name}]`);
		const value = [text, ...tools].filter(Boolean).join("\n");
		return value.length === 0 ? undefined : `[Assistant]\n${value}`;
	}
	const text = truncate(visibleText(message.content).join("\n"), MAXIMUM_TOOL_RESULT_CHARACTERS);
	return text.length === 0 ? undefined : `[Tool result: ${message.toolName}]\n${text}`;
}

function cutAtCompleteTurn(messages: readonly GameConversationMessage[], keepRecentTokens: number): number {
	const userStarts = messages.flatMap((message, index) => (message.role === "user" ? [index] : []));
	if (userStarts.length === 0) return messages.length;
	let cut = userStarts[userStarts.length - 1] ?? messages.length;
	for (let index = userStarts.length - 1; index >= 0; index -= 1) {
		const candidate = userStarts[index];
		if (candidate === undefined) continue;
		const tokens = estimateGameConversationTokens(messages.slice(candidate));
		if (tokens > keepRecentTokens && index !== userStarts.length - 1) break;
		cut = candidate;
	}
	return cut;
}

export class SummarizingGameConversationCompactor implements GameConversationCompactor {
	private readonly reserveTokens: number;
	private readonly keepRecentTokens: number;
	private readonly maximumSummaryTokens: number;
	private readonly maximumSummaryCharacters: number;
	private readonly maximumSummaryInputCharacters: number;
	private readonly maximumMessages: number;

	constructor(private readonly options: SummarizingGameConversationCompactorOptions) {
		this.reserveTokens = requireInteger(
			options.reserveTokens ?? DEFAULT_RESERVE_TOKENS,
			256,
			1_000_000,
			"reserveTokens",
		);
		this.keepRecentTokens = requireInteger(
			options.keepRecentTokens ?? DEFAULT_KEEP_RECENT_TOKENS,
			0,
			1_000_000,
			"keepRecentTokens",
		);
		this.maximumSummaryTokens = requireInteger(
			options.maximumSummaryTokens ?? DEFAULT_MAXIMUM_SUMMARY_TOKENS,
			1,
			100_000,
			"maximumSummaryTokens",
		);
		this.maximumSummaryCharacters = requireInteger(
			options.maximumSummaryCharacters ?? DEFAULT_MAXIMUM_SUMMARY_CHARACTERS,
			64,
			1_000_000,
			"maximumSummaryCharacters",
		);
		this.maximumSummaryInputCharacters = requireInteger(
			options.maximumSummaryInputCharacters ?? DEFAULT_MAXIMUM_SUMMARY_INPUT_CHARACTERS,
			256,
			4_000_000,
			"maximumSummaryInputCharacters",
		);
		this.maximumMessages = requireInteger(
			options.maximumMessages ?? DEFAULT_MAXIMUM_MESSAGES,
			1,
			100_000,
			"maximumMessages",
		);
	}

	async compact(
		request: GameConversationCompactionRequest,
		signal: AbortSignal,
	): Promise<GameConversationCompactionResult> {
		signal.throwIfAborted();
		if (request.snapshot.messages.length > this.maximumMessages) {
			throw new RangeError("Conversation exceeds the configured compaction message limit.");
		}
		const tokensBefore = estimateGameConversationTokens(request.snapshot.messages);
		const threshold = request.model.contextWindow - this.reserveTokens;
		if (threshold < 1) throw new RangeError("Compaction reserve leaves no model context capacity.");
		if (tokensBefore <= threshold) {
			return {
				messages: request.snapshot.messages,
				changed: false,
				tokensBefore,
				tokensAfter: tokensBefore,
				summarizedMessages: 0,
			};
		}

		const cut = cutAtCompleteTurn(request.snapshot.messages, this.keepRecentTokens);
		const prefix = request.snapshot.messages.slice(0, cut);
		const retained = request.snapshot.messages.slice(cut);
		if (prefix.length === 0) throw new RangeError("The latest complete conversation turn exceeds the context budget.");
		const transcript = prefix.flatMap((message) => sanitizeMessage(message) ?? []).join("\n\n");
		if (transcript.length === 0) throw new Error("Conversation compaction produced no safe summary input.");
		if (transcript.length > this.maximumSummaryInputCharacters) {
			throw new RangeError("Safe conversation summary input exceeds its configured character limit.");
		}
		const summarized = await this.options.summarizer.summarize(
			{ transcript, maximumOutputTokens: this.maximumSummaryTokens },
			signal,
		);
		signal.throwIfAborted();
		const summary = summarized.summary.trim();
		if (summary.length === 0 || summary.length > this.maximumSummaryCharacters) {
			throw new Error("Conversation summarizer returned an empty or oversized summary.");
		}
		const messages: GameConversationMessage[] = [
			{ role: "summary", summary, tokensBefore, timestamp: Date.now() },
			...structuredClone(retained),
		];
		const tokensAfter = estimateGameConversationTokens(messages);
		if (tokensAfter >= tokensBefore) throw new Error("Conversation compaction did not reduce the transcript.");
		return {
			messages,
			changed: true,
			tokensBefore,
			tokensAfter,
			summarizedMessages: prefix.length,
			...(summarized.usage === undefined ? {} : { usage: summarized.usage }),
		};
	}
}

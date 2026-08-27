import type { AssistantMessage, Usage, UserMessage } from "@earendil-works/pi-ai";
import type { GameUsage } from "@opengameagent/protocol";
import type {
	GameTranscriptSummarizer,
	GameTranscriptSummaryRequest,
	GameTranscriptSummaryResult,
} from "@opengameagent/transcript";
import type { PiGameModelResolver } from "./model-registry.js";

export interface PiGameTranscriptSummarizerOptions {
	models: PiGameModelResolver;
	modelProfileId: string;
}

const SYSTEM_PROMPT =
	"Summarize the supplied game-character conversation as a compact factual checkpoint. Preserve goals, commitments, relationships, known world facts, completed actions, unresolved work, and player preferences. Do not continue the conversation. Do not invent facts. Output only the summary.";

function toGameUsage(usage: Usage): GameUsage {
	return {
		input: usage.input,
		output: usage.output,
		cacheRead: usage.cacheRead,
		cacheWrite: usage.cacheWrite,
		...(usage.reasoning === undefined ? {} : { reasoning: usage.reasoning }),
		totalTokens: usage.totalTokens,
		cost: { ...usage.cost },
	};
}

function responseText(message: AssistantMessage): string {
	if (message.stopReason !== "stop") {
		throw new Error(`Conversation summary request ended with '${message.stopReason}'.`);
	}
	if (message.content.some((part) => part.type === "toolCall")) {
		throw new Error("Conversation summary request returned an unexpected tool call.");
	}
	return message.content
		.filter((part) => part.type === "text")
		.map((part) => part.text)
		.join("")
		.trim();
}

export class PiGameTranscriptSummarizer implements GameTranscriptSummarizer {
	constructor(private readonly options: PiGameTranscriptSummarizerOptions) {}

	async summarize(request: GameTranscriptSummaryRequest, signal: AbortSignal): Promise<GameTranscriptSummaryResult> {
		signal.throwIfAborted();
		const resolved = this.options.models.resolve(this.options.modelProfileId);
		const message: UserMessage = {
			role: "user",
			content: request.transcript,
			timestamp: Date.now(),
		};
		const stream = await resolved.streamFn(
			resolved.model,
			{ systemPrompt: SYSTEM_PROMPT, messages: [message] },
			{
				maxTokens: Math.min(request.maximumOutputTokens, resolved.descriptor.maximumOutputTokens),
				signal,
				cacheRetention: "none",
				toolChoice: "none",
				deferred: false,
			},
		);
		const response = await stream.result();
		const summary = responseText(response);
		if (summary.length === 0) throw new Error("Conversation summary request returned no visible text.");
		return { summary, usage: toGameUsage(response.usage) };
	}
}

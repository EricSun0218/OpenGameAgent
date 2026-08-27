import type { StreamFn } from "@earendil-works/pi-agent-core";
import {
	type Api,
	type AssistantMessage,
	type AssistantMessageEvent,
	EventStream,
	type Model,
} from "@earendil-works/pi-ai";
import { describe, expect, it } from "vitest";
import type { PiGameModelResolver } from "./model-registry.js";
import { PiGameTranscriptSummarizer } from "./pi-game-transcript-summarizer.js";

const model: Model<Api> = {
	id: "summary-model",
	name: "Summary model",
	api: "openai-responses",
	provider: "test",
	baseUrl: "https://example.invalid",
	reasoning: true,
	input: ["text"],
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
	contextWindow: 16_384,
	maxTokens: 4_096,
};

class SummaryStream extends EventStream<AssistantMessageEvent, AssistantMessage> {
	constructor(message: AssistantMessage) {
		super(
			(event) => event.type === "done" || event.type === "error",
			(event) => {
				if (event.type === "done") return event.message;
				if (event.type === "error") return event.error;
				throw new Error("Unexpected non-terminal summary event.");
			},
		);
		queueMicrotask(() => this.push({ type: "done", reason: "stop", message }));
	}
}

describe("PiGameTranscriptSummarizer", () => {
	it("uses an isolated zero-tool, no-cache request with reasoning disabled", async () => {
		let stream: Parameters<StreamFn>[1] | undefined;
		let options: Parameters<StreamFn>[2] | undefined;
		const streamFn: StreamFn = (_model, context, requestOptions) => {
			stream = context;
			options = requestOptions;
			return new SummaryStream({
				role: "assistant",
				content: [
					{ type: "thinking", thinking: "hidden" },
					{ type: "text", text: "  compact checkpoint  " },
				],
				api: model.api,
				provider: model.provider,
				model: model.id,
				usage: {
					input: 10,
					output: 3,
					cacheRead: 0,
					cacheWrite: 0,
					totalTokens: 13,
					cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
				},
				stopReason: "stop",
				timestamp: 1,
			});
		};
		const models: PiGameModelResolver = {
			resolve(profileId) {
				expect(profileId).toBe("summary");
				return {
					model,
					streamFn,
					thinkingLevel: "high",
					descriptor: {
						profileId,
						provider: model.provider,
						model: model.id,
						api: model.api,
						reasoning: true,
						input: ["text"],
						contextWindow: model.contextWindow,
						maximumOutputTokens: model.maxTokens,
					},
				};
			},
		};
		const summarizer = new PiGameTranscriptSummarizer({ models, modelProfileId: "summary" });
		const result = await summarizer.summarize(
			{ transcript: "[User]\nhello", maximumOutputTokens: 512 },
			new AbortController().signal,
		);

		expect(result.summary).toBe("compact checkpoint");
		expect(stream?.tools).toBeUndefined();
		expect(JSON.stringify(stream)).toContain("[User]");
		expect(options).toMatchObject({ maxTokens: 512, cacheRetention: "none", toolChoice: "none", deferred: false });
		expect(options).not.toHaveProperty("reasoning");
		expect(result.usage?.totalTokens).toBe(13);
	});
});

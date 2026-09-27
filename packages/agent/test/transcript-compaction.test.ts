import { Buffer } from "node:buffer";
import { createHash } from "node:crypto";
import type { AgentMessage } from "@earendil-works/pi-agent-core";
import type { AssistantMessage, Model } from "@earendil-works/pi-ai";
import type { DurableTranscriptMessage } from "@opengameagent/protocol";
import { describe, expect, it, vi } from "vitest";
import { TranscriptCompactor } from "../src/index.js";

const MODEL = {
	id: "test-model",
	name: "Test model",
	api: "openai-responses",
	provider: "openai",
	baseUrl: "https://example.invalid",
	reasoning: true,
	input: ["text"],
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
	contextWindow: 100,
	maxTokens: 20,
} satisfies Model<"openai-responses">;

function summaryMessage(content: AssistantMessage["content"], output = 5): AssistantMessage {
	return {
		role: "assistant",
		content,
		api: "openai-responses",
		provider: "openai",
		model: "summary-model",
		usage: {
			input: 30,
			output,
			cacheRead: 0,
			cacheWrite: 0,
			totalTokens: 30 + output,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
		},
		stopReason: "stop",
		timestamp: 10,
	};
}

const CLOSED_MESSAGES: readonly DurableTranscriptMessage[] = [
	{
		role: "user",
		contentFormat: "text",
		content: [{ type: "text", text: "one" }],
		timestamp: 1,
	},
	{
		role: "assistant",
		content: [{ type: "text", text: "answer one" }],
		api: "openai-responses",
		provider: "openai",
		model: "test-model",
		stopReason: "stop",
		timestamp: 2,
	},
];

function toProviderMessages(messages: readonly DurableTranscriptMessage[]): readonly AgentMessage[] {
	return messages.map((message) => {
		if (message.role === "user") {
			const content = message.content.map((part) => {
				if (part.type !== "text") throw new Error("Test media requires an explicit hydrated provider message.");
				return { type: "text" as const, text: part.text };
			});
			return {
				role: "user" as const,
				content: message.contentFormat === "text" ? (content[0]?.text ?? "") : content,
				timestamp: message.timestamp,
			};
		}
		if (message.role === "toolResult") {
			return {
				role: "toolResult" as const,
				toolCallId: message.toolCallId,
				toolName: message.toolName,
				content: message.content.map((part) => {
					if (part.type !== "text") throw new Error("Test media requires an explicit hydrated provider message.");
					return { type: "text" as const, text: part.text };
				}),
				isError: message.isError,
				timestamp: message.timestamp,
			};
		}
		return {
			role: "assistant" as const,
			content: structuredClone(message.content),
			api: message.api as AssistantMessage["api"],
			provider: message.provider,
			model: message.model,
			usage: {
				input: 0,
				output: 0,
				cacheRead: 0,
				cacheWrite: 0,
				totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
			},
			stopReason: message.stopReason,
			timestamp: message.timestamp,
		};
	});
}

describe("TranscriptCompactor", () => {
	it("does not summarize when exact provider accounting leaves the host reserve", async () => {
		const summarize = vi.fn();
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async () => 80,
			summarize,
		});

		await expect(
			compactor.prepare({
				model: MODEL,
				systemPrompt: "system",
				tools: [],
				messages: [
					{
						role: "user",
						contentFormat: "text",
						content: [{ type: "text", text: "hello" }],
						timestamp: 1,
					},
				],
				providerMessages: [{ role: "user", content: "hello", timestamp: 1 }],
				signal: new AbortController().signal,
			}),
		).resolves.toBeUndefined();
		expect(summarize).not.toHaveBeenCalled();
	});

	it("builds a smaller exact replacement from a completed summary and retained suffix", async () => {
		const messages: DurableTranscriptMessage[] = [
			{
				role: "user" as const,
				contentFormat: "text" as const,
				content: [{ type: "text", text: "one" }],
				timestamp: 1,
			},
			{
				role: "assistant" as const,
				content: [{ type: "text", text: "answer one" }],
				api: "openai-responses",
				provider: "openai",
				model: "test-model",
				stopReason: "stop" as const,
				timestamp: 2,
			},
			{
				role: "user" as const,
				contentFormat: "text" as const,
				content: [{ type: "text", text: "two" }],
				timestamp: 3,
			},
			{
				role: "assistant" as const,
				content: [{ type: "text", text: "answer two" }],
				api: "openai-responses",
				provider: "openai",
				model: "test-model",
				stopReason: "stop" as const,
				timestamp: 4,
			},
		];
		let summaryRequest: unknown;
		const summarize = vi.fn(async (request: unknown) => {
			summaryRequest = request;
			return {
				retainedFrom: 2,
				message: {
					role: "assistant" as const,
					content: [{ type: "text" as const, text: "Earlier: one." }],
					api: "openai-responses" as const,
					provider: "openai",
					model: "summary-model",
					usage: {
						input: 30,
						output: 5,
						cacheRead: 0,
						cacheWrite: 0,
						totalTokens: 35,
						cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
					},
					stopReason: "stop" as const,
					timestamp: 10,
				},
			};
		});
		const countContext = vi.fn(async ({ messages: current }: { messages: readonly unknown[] }) => {
			if (current.length === 4) return 90;
			if (current.length === 2) return 30;
			return 50;
		});
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext,
			summarize,
		});

		await expect(
			compactor.prepare({
				model: MODEL,
				systemPrompt: "system",
				tools: [],
				messages,
				providerMessages: toProviderMessages(messages),
				signal: new AbortController().signal,
			}),
		).resolves.toEqual({
			replacement: [
				{
					role: "user",
					contentFormat: "parts",
					content: [{ type: "text", text: "Earlier: one." }],
					timestamp: 10,
				},
				messages[2],
				messages[3],
			],
		});
		expect(summarize).toHaveBeenCalledWith(
			expect.objectContaining({
				maxOutputTokens: 20,
				maxRetainedContextTokens: 40,
				safeRetainedFrom: expect.arrayContaining([2]),
			}),
			expect.any(AbortSignal),
		);
		expect(summaryRequest).not.toHaveProperty("model");
		expect(summaryRequest).not.toHaveProperty("systemPrompt");
		expect(summaryRequest).not.toHaveProperty("tools");
		expect(countContext).toHaveBeenCalledTimes(3);
	});

	it("keeps replay metadata for exact accounting and the retained suffix but hides it from the summarizer", async () => {
		const replayAssistant: DurableTranscriptMessage = {
			role: "assistant",
			content: [
				{ type: "thinking", thinking: "private reasoning" },
				{ type: "text", text: "answer one" },
			],
			api: "openai-responses",
			provider: "openai",
			model: "test-model",
			providerReplay: {
				format: "pi",
				version: "1",
				api: "openai-responses",
				provider: "openai",
				model: "test-model",
				responseModel: "resolved-model",
				responseId: "response-private",
				providerThinkingLevel: "effort-private",
				stopReason: "stop",
				blocks: [
					{ type: "thinking", thinkingSignature: "thinking-private" },
					{ type: "text", textSignature: "text-private" },
				],
			},
			stopReason: "stop",
			timestamp: 2,
		};
		const messages: readonly DurableTranscriptMessage[] = [
			{
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "one" }],
				timestamp: 1,
			},
			replayAssistant,
			{
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "two" }],
				timestamp: 3,
			},
			{
				role: "assistant",
				content: [{ type: "text", text: "answer two" }],
				api: "openai-responses",
				provider: "openai",
				model: "test-model",
				stopReason: "stop",
				timestamp: 4,
			},
		];
		const replayProviderAssistant: AssistantMessage = {
			role: "assistant",
			content: [
				{ type: "thinking", thinking: "private reasoning", thinkingSignature: "thinking-private" },
				{ type: "text", text: "answer one", textSignature: "text-private" },
			],
			api: "openai-responses",
			provider: "openai",
			model: "test-model",
			responseModel: "resolved-model",
			responseId: "response-private",
			providerThinkingLevel: "effort-private",
			usage: {
				input: 0,
				output: 0,
				cacheRead: 0,
				cacheWrite: 0,
				totalTokens: 0,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
			},
			stopReason: "stop",
			timestamp: 2,
		};
		const providerMessages = [...toProviderMessages(messages)];
		providerMessages[1] = replayProviderAssistant;
		const counted: (readonly AgentMessage[])[] = [];
		let summarized: readonly DurableTranscriptMessage[] | undefined;
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async ({ messages: current }) => {
				counted.push(current);
				if (current.length === 4) return 90;
				if (current.length === 2) return 30;
				return 50;
			},
			summarize: async (request) => {
				summarized = request.messages;
				return { retainedFrom: 2, message: summaryMessage([{ type: "text", text: "Earlier." }]) };
			},
		});

		const result = await compactor.prepare({
			model: MODEL,
			systemPrompt: "system",
			tools: [],
			messages,
			providerMessages,
			signal: new AbortController().signal,
		});

		expect(counted[0]?.[1]).toEqual(replayProviderAssistant);
		expect(summarized?.[1]).toEqual({
			role: "assistant",
			content: [{ type: "text", text: "answer one" }],
			api: "openai-responses",
			provider: "openai",
			model: "test-model",
			stopReason: "stop",
			timestamp: 2,
		});
		expect(JSON.stringify(summarized)).not.toContain("private");
		expect(result?.replacement.slice(1)).toEqual(messages.slice(2));
	});

	it("does not permit a retained suffix to begin between a Tool call and its result", async () => {
		let offeredBoundaries: readonly number[] = [];
		const summarize = vi.fn(async (request: { readonly safeRetainedFrom: readonly number[] }) => {
			offeredBoundaries = [...request.safeRetainedFrom];
			return {
				retainedFrom: 2,
				message: {
					role: "assistant" as const,
					content: [{ type: "text" as const, text: "Earlier state." }],
					api: "openai-responses" as const,
					provider: "openai",
					model: "summary-model",
					usage: {
						input: 30,
						output: 5,
						cacheRead: 0,
						cacheWrite: 0,
						totalTokens: 35,
						cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
					},
					stopReason: "stop" as const,
					timestamp: 10,
				},
			};
		});
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async ({ messages }) => (messages.length === 4 ? 90 : 30),
			summarize,
		});
		const messages: DurableTranscriptMessage[] = [
			{
				role: "user",
				contentFormat: "text",
				content: [{ type: "text", text: "inspect" }],
				timestamp: 1,
			},
			{
				role: "assistant",
				content: [{ type: "toolCall", id: "call-1", name: "inspect", arguments: {} }],
				api: "openai-responses",
				provider: "openai",
				model: "test-model",
				stopReason: "toolUse",
				timestamp: 2,
			},
			{
				role: "toolResult",
				toolCallId: "call-1",
				toolName: "inspect",
				content: [{ type: "text", text: "done" }],
				isError: false,
				timestamp: 3,
			},
			{
				role: "assistant",
				content: [{ type: "text", text: "finished" }],
				api: "openai-responses",
				provider: "openai",
				model: "test-model",
				stopReason: "stop",
				timestamp: 4,
			},
		];

		await expect(
			compactor.prepare({
				model: MODEL,
				systemPrompt: "system",
				tools: [],
				messages,
				providerMessages: toProviderMessages(messages),
				signal: new AbortController().signal,
			}),
		).rejects.toThrow("invalid retained boundary");
		expect(offeredBoundaries).not.toContain(2);
	});

	it("counts hydrated provider media while exposing only durable references to the summarizer", async () => {
		const bytes = Buffer.from("compaction-image");
		const base64 = bytes.toString("base64");
		const reference = {
			type: "image-reference" as const,
			attachmentId: `img_${createHash("sha256").update(bytes).digest("hex")}`,
			mediaType: "image/png" as const,
			bytes: bytes.byteLength,
			width: 1,
			height: 1,
		};
		const messages: readonly DurableTranscriptMessage[] = [
			{ role: "user", contentFormat: "parts", content: [reference], timestamp: 1 },
			{
				role: "assistant",
				content: [{ type: "text", text: "seen" }],
				api: "openai-responses",
				provider: "openai",
				model: "test-model",
				stopReason: "stop",
				timestamp: 2,
			},
		];
		const providerMessages: readonly AgentMessage[] = [
			{ role: "user", content: [{ type: "image", data: base64, mimeType: "image/png" }], timestamp: 1 },
			...toProviderMessages(messages.slice(1)),
		];
		const counted: string[] = [];
		let summarized = "";
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async ({ messages: current }) => {
				counted.push(JSON.stringify(current));
				return current.length === 2 ? 90 : current.length === 0 ? 0 : 20;
			},
			summarize: async (request) => {
				summarized = JSON.stringify(request.messages);
				return { retainedFrom: 2, message: summaryMessage([{ type: "text", text: "Earlier image." }]) };
			},
		});

		await expect(
			compactor.prepare({
				model: MODEL,
				systemPrompt: "system",
				tools: [],
				messages,
				providerMessages,
				signal: new AbortController().signal,
			}),
		).resolves.toMatchObject({ replacement: [{ role: "user" }] });
		expect(counted[0]).toContain(base64);
		expect(summarized).toContain(reference.attachmentId);
		expect(summarized).not.toContain(base64);
	});

	it("omits unsigned reasoning from a completed textual summary", async () => {
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async ({ messages }) => (messages.length === 2 ? 90 : messages.length === 0 ? 0 : 20),
			summarize: async () => ({
				retainedFrom: 2,
				message: summaryMessage([
					{ type: "thinking", thinking: "private", thinkingSignature: "" },
					{ type: "text", text: "Earlier.", textSignature: "" },
				]),
			}),
		});

		await expect(
			compactor.prepare({
				model: MODEL,
				systemPrompt: "system",
				tools: [],
				messages: CLOSED_MESSAGES,
				providerMessages: toProviderMessages(CLOSED_MESSAGES),
				signal: new AbortController().signal,
			}),
		).resolves.toEqual({
			replacement: [
				{
					role: "user",
					contentFormat: "parts",
					content: [{ type: "text", text: "Earlier." }],
					timestamp: 10,
				},
			],
		});
	});

	it.each([
		["signed reasoning", [{ type: "thinking", thinking: "private", thinkingSignature: "opaque" }]],
		["redacted reasoning", [{ type: "thinking", thinking: "private", redacted: true }]],
		["signed text", [{ type: "text", text: "Earlier.", textSignature: "opaque" }]],
		["Tool call", [{ type: "toolCall", id: "call-1", name: "inspect", arguments: {} }]],
	] as const)("rejects a summary containing %s", async (_label, content) => {
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async () => 90,
			summarize: async () => ({
				retainedFrom: 2,
				message: summaryMessage(structuredClone(content) as unknown as AssistantMessage["content"]),
			}),
		});

		await expect(
			compactor.prepare({
				model: MODEL,
				systemPrompt: "system",
				tools: [],
				messages: CLOSED_MESSAGES,
				providerMessages: toProviderMessages(CLOSED_MESSAGES),
				signal: new AbortController().signal,
			}),
		).rejects.toThrow();
	});

	it("rejects an unbalanced source before invoking the summarizer", async () => {
		const summarize = vi.fn();
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async () => 90,
			summarize,
		});
		const source: readonly DurableTranscriptMessage[] = [
			{
				role: "assistant",
				content: [{ type: "toolCall", id: "call-1", name: "inspect", arguments: {} }],
				api: "openai-responses",
				provider: "openai",
				model: "test-model",
				stopReason: "toolUse",
				timestamp: 1,
			},
		];

		await expect(
			compactor.prepare({
				model: MODEL,
				systemPrompt: "system",
				tools: [],
				messages: source,
				providerMessages: toProviderMessages(source),
				signal: new AbortController().signal,
			}),
		).rejects.toThrow("unbalanced Tool call");
		expect(summarize).not.toHaveBeenCalled();
	});

	it.each([
		["retained transcript context exceeds", [90, 41]],
		["required model reserve", [90, 30, 81]],
	] as const)("rejects when the %s its exact budget", async (message, counts) => {
		let index = 0;
		const compactor = new TranscriptCompactor({
			reserveTokens: 20,
			maxRetainedContextTokens: 40,
			maxSummaryOutputTokens: 20,
			countContext: async () => {
				const count = counts[index++] ?? counts.at(-1);
				if (count === undefined) throw new Error("Expected an exact token-count fixture.");
				return count;
			},
			summarize: async () => ({
				retainedFrom: 1,
				message: summaryMessage([{ type: "text", text: "Earlier." }]),
			}),
		});

		await expect(
			compactor.prepare({
				model: MODEL,
				systemPrompt: "system",
				tools: [],
				messages: CLOSED_MESSAGES,
				providerMessages: toProviderMessages(CLOSED_MESSAGES),
				signal: new AbortController().signal,
			}),
		).rejects.toThrow(message);
	});
});

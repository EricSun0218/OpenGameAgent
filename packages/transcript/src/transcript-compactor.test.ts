import type {
	GameConversationMessage,
	GameConversationSnapshot,
	GameResolvedModel,
	GameSessionKey,
} from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { SummarizingGameConversationCompactor } from "./transcript-compactor.js";

const session: GameSessionKey = {
	worldId: "world-secret",
	saveId: "save-secret",
	timelineId: "timeline-secret",
	generation: 2,
	ownerId: "owner-secret",
	sessionId: "session-secret",
	actorId: "actor-secret",
};

const model: GameResolvedModel = {
	profileId: "summary",
	provider: "test",
	model: "summary-model",
	api: "test",
	reasoning: false,
	input: ["text"],
	contextWindow: 1_200,
	maximumOutputTokens: 256,
};

const usage = {
	input: 10,
	output: 2,
	cacheRead: 0,
	cacheWrite: 0,
	totalTokens: 12,
	cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
};

function snapshot(messages: readonly GameConversationMessage[]): GameConversationSnapshot {
	return { revision: 3, messages };
}

describe("SummarizingGameConversationCompactor", () => {
	it("leaves a transcript below the threshold untouched without a model call", async () => {
		let calls = 0;
		const compactor = new SummarizingGameConversationCompactor({
			summarizer: {
				async summarize() {
					calls += 1;
					return { summary: "unused" };
				},
			},
			reserveTokens: 256,
		});
		const messages: GameConversationMessage[] = [{ role: "user", content: "hello", timestamp: 1 }];
		const result = await compactor.compact(
			{ session, snapshot: snapshot(messages), model },
			new AbortController().signal,
		);
		expect(result).toMatchObject({ changed: false, summarizedMessages: 0 });
		expect(result.messages).toEqual(messages);
		expect(calls).toBe(0);
	});

	it("summarizes only complete old turns and strips private reasoning, arguments, images, and authority coordinates", async () => {
		let captured = "";
		const compactor = new SummarizingGameConversationCompactor({
			summarizer: {
				async summarize(request) {
					captured = request.transcript;
					return { summary: "The player requested a shelter; the first action completed.", usage };
				},
			},
			reserveTokens: 256,
			keepRecentTokens: 100,
		});
		const messages: GameConversationMessage[] = [
			{
				role: "user",
				content: [
					{ type: "text", text: `Build a shelter. ${"context ".repeat(350)}` },
					{ type: "image", mimeType: "image/png", data: "private-base64" },
				],
				timestamp: 1,
			},
			{
				role: "assistant",
				content: [
					{ type: "reasoning", text: "hidden-chain", signature: "opaque" },
					{
						type: "toolCall",
						id: "private-call-id",
						name: "build",
						arguments: { operationId: "private-operation", x: 10 },
					},
				],
				api: "test",
				provider: "test",
				model: "test",
				usage,
				stopReason: "toolUse",
				timestamp: 2,
			},
			{
				role: "toolResult",
				toolCallId: "private-call-id",
				toolName: "build",
				content: [{ type: "text", text: "Shelter committed." }],
				details: { timelineId: session.timelineId, operationId: "private-operation" },
				isError: false,
				timestamp: 3,
			},
			{ role: "user", content: "What should we do next?", timestamp: 4 },
			{
				role: "assistant",
				content: [{ type: "text", text: "We should add lighting." }],
				api: "test",
				provider: "test",
				model: "test",
				usage,
				stopReason: "stop",
				timestamp: 5,
			},
		];

		const result = await compactor.compact(
			{ session, snapshot: snapshot(messages), model },
			new AbortController().signal,
		);
		expect(result.changed).toBe(true);
		expect(result.summarizedMessages).toBe(3);
		expect(result.messages).toEqual([
			expect.objectContaining({ role: "summary", summary: expect.stringContaining("shelter") }),
			messages[3],
			messages[4],
		]);
		expect(captured).toContain("Build a shelter");
		expect(captured).toContain("[called tool: build]");
		expect(captured).toContain("Shelter committed");
		expect(captured).not.toMatch(
			/hidden-chain|opaque|private-base64|private-call-id|private-operation|world-secret|timeline-secret|actor-secret/,
		);
		expect(result.usage).toEqual(usage);
		expect(result.tokensAfter).toBeLessThan(result.tokensBefore);
	});

	it("fails closed on oversized summaries and does not replace canonical history", async () => {
		const compactor = new SummarizingGameConversationCompactor({
			summarizer: {
				async summarize() {
					return { summary: "x".repeat(65) };
				},
			},
			reserveTokens: 256,
			keepRecentTokens: 0,
			maximumSummaryCharacters: 64,
		});
		const messages: GameConversationMessage[] = [
			{ role: "user", content: "x".repeat(5_000), timestamp: 1 },
			{ role: "user", content: "latest", timestamp: 2 },
		];
		await expect(
			compactor.compact({ session, snapshot: snapshot(messages), model }, new AbortController().signal),
		).rejects.toThrow(/empty or oversized/);
		expect(messages).toHaveLength(2);
	});
});

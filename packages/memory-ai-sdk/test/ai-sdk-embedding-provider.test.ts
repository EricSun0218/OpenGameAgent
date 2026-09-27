import type { EmbeddingModelV3, EmbeddingModelV3CallOptions } from "@ai-sdk/provider";
import { describe, expect, it } from "vitest";
import { AiSdkEmbeddingProvider } from "../src/index.js";

describe("AiSdkEmbeddingProvider", () => {
	it("delegates batching and explicit concurrency to the AI SDK", async () => {
		const model = new FakeEmbeddingModel();
		const provider = new AiSdkEmbeddingProvider({
			model,
			modelId: "remote-test-model",
			dimensions: 3,
			maxTextsPerRequest: 4,
			maxTextBytes: 64,
			maxRetries: 0,
			maxParallelCalls: 1,
		});
		const vectors = await provider.embed(["one", "two", "three"]);
		expect(vectors.map((vector) => Array.from(vector))).toEqual([
			[3, 1, 1],
			[3, 1, 1],
			[5, 1, 1],
		]);
		expect(model.calls).toEqual([["one", "two"], ["three"]]);
		expect(model.maximumActiveCalls).toBe(1);
	});

	it("rejects host-limit and malformed-vector failures", async () => {
		const provider = new AiSdkEmbeddingProvider({
			model: new FakeEmbeddingModel(true),
			modelId: "remote-test-model",
			dimensions: 3,
			maxTextsPerRequest: 2,
			maxTextBytes: 4,
			maxRetries: 0,
			maxParallelCalls: 1,
		});
		await expect(provider.embed(["one", "two", "three"])).rejects.toThrow("invalid_embedding_batch_size");
		await expect(provider.embed([""])).rejects.toThrow("invalid_embedding_text_bytes");
		await expect(provider.embed(["12345"])).rejects.toThrow("invalid_embedding_text_bytes");
		await expect(provider.embed(["one"])).rejects.toThrow("embedding_dimensions");
	});

	it("preserves cancellation", async () => {
		const controller = new AbortController();
		controller.abort(new Error("cancelled"));
		const provider = new AiSdkEmbeddingProvider({
			model: new FakeEmbeddingModel(),
			modelId: "remote-test-model",
			dimensions: 3,
			maxTextsPerRequest: 2,
			maxTextBytes: 64,
			maxRetries: 0,
			maxParallelCalls: 1,
		});
		await expect(provider.embed(["one"], controller.signal)).rejects.toThrow("cancelled");
	});

	it("passes the same admitted text snapshot to the AI SDK", async () => {
		const model = new FakeEmbeddingModel();
		const provider = new AiSdkEmbeddingProvider({
			model,
			modelId: "remote-test-model",
			dimensions: 3,
			maxTextsPerRequest: 2,
			maxTextBytes: 2,
			maxRetries: 0,
			maxParallelCalls: 1,
		});
		let reads = 0;
		const texts = new Proxy(["ok"], {
			get: (target, property, receiver) =>
				property === "0" ? (++reads === 1 ? "ok" : "x".repeat(100)) : Reflect.get(target, property, receiver),
		});
		await provider.embed(texts);
		expect(model.calls).toEqual([["ok"]]);
		expect(reads).toBe(1);
	});

	it("snapshots the model output batch before validating its vectors", async () => {
		const model = new AccessorEmbeddingModel();
		const provider = new AiSdkEmbeddingProvider({
			model,
			modelId: "remote-test-model",
			dimensions: 3,
			maxTextsPerRequest: 2,
			maxTextBytes: 64,
			maxRetries: 0,
			maxParallelCalls: 1,
		});
		expect((await provider.embed(["one"])).map((vector) => Array.from(vector))).toEqual([[1, 2, 3]]);
		expect(model.lengthReads).toBe(1);
		expect(model.embeddingReads).toBe(1);
	});
});

class AccessorEmbeddingModel implements EmbeddingModelV3 {
	readonly specificationVersion = "v3";
	readonly provider = "test";
	readonly modelId = "accessor-model";
	readonly maxEmbeddingsPerCall = undefined;
	readonly supportsParallelCalls = false;
	embeddingReads = 0;
	lengthReads = 0;

	async doEmbed(_options: EmbeddingModelV3CallOptions) {
		const embeddings = new Proxy([[1, 2, 3]], {
			get: (target, property, receiver) => {
				if (property === "0") this.embeddingReads += 1;
				if (property === "length") return ++this.lengthReads === 1 ? 1 : 0;
				return Reflect.get(target, property, receiver);
			},
		});
		return { embeddings, usage: { tokens: 1 }, warnings: [] };
	}
}

class FakeEmbeddingModel implements EmbeddingModelV3 {
	readonly specificationVersion = "v3";
	readonly provider = "test";
	readonly modelId = "test-model";
	readonly maxEmbeddingsPerCall = 2;
	readonly supportsParallelCalls = true;
	readonly calls: string[][] = [];
	maximumActiveCalls = 0;
	private activeCalls = 0;

	constructor(private readonly invalid = false) {}

	async doEmbed(options: EmbeddingModelV3CallOptions) {
		this.calls.push([...options.values]);
		this.activeCalls += 1;
		this.maximumActiveCalls = Math.max(this.maximumActiveCalls, this.activeCalls);
		await Promise.resolve();
		this.activeCalls -= 1;
		return {
			embeddings: options.values.map((value) => (this.invalid ? [1, 0] : [value.length, 1, 1])),
			usage: { tokens: options.values.length },
			warnings: [],
		};
	}
}

import { describe, expect, it } from "vitest";
import {
	BGE_M3_DIMENSIONS,
	BgeM3EmbeddingError,
	BgeM3EmbeddingProvider,
	type BgeM3EmbeddingProviderOptions,
} from "../src/index.js";
import { validateBgeM3Options } from "../src/validation.js";

describe("BgeM3EmbeddingProvider", () => {
	it("derives a stable model identity from every output-affecting artifact", () => {
		const first = new BgeM3EmbeddingProvider(options());
		const second = new BgeM3EmbeddingProvider(options());
		const shortened = new BgeM3EmbeddingProvider(options({ maxTokens: 511 }));

		expect(first.dimensions).toBe(BGE_M3_DIMENSIONS);
		expect(first.modelId).toBe(second.modelId);
		expect(first.modelId).toMatch(/^bge-m3-local:[a-f\d]{64}$/);
		expect(shortened.modelId).not.toBe(first.modelId);
	});

	it("rejects invalid host limits and artifact identities before loading a model", () => {
		expect(() => new BgeM3EmbeddingProvider(options({ maxTokens: 8193 }))).toThrow(
			new BgeM3EmbeddingError("invalid_configuration"),
		);
		expect(() => new BgeM3EmbeddingProvider(options({ requestTimeoutMs: 2_147_483_647 }))).not.toThrow();
		expect(() => new BgeM3EmbeddingProvider(options({ requestTimeoutMs: 2_147_483_648 }))).toThrow(
			new BgeM3EmbeddingError("invalid_configuration"),
		);
		expect(
			() =>
				new BgeM3EmbeddingProvider({
					...options(),
					artifacts: { ...options().artifacts, model: { path: "model.onnx", sha256: "not-a-hash" } },
				}),
		).toThrow(new BgeM3EmbeddingError("invalid_configuration"));
	});

	it("captures host configuration fields once", () => {
		const input = options();
		let reads = 0;
		Object.defineProperty(input.limits, "maxTextBytes", {
			enumerable: true,
			get: () => (++reads === 1 ? 8 : Number.MAX_VALUE),
		});
		expect(validateBgeM3Options(input).limits.maxTextBytes).toBe(8);
		expect(reads).toBe(1);
	});

	it("enforces byte and batch bounds and preserves pre-dispatch cancellation", async () => {
		const provider = new BgeM3EmbeddingProvider(options());
		await expect(provider.embed([])).rejects.toThrow("bge_m3_invalid_batch_size");
		await expect(provider.embed(["界界界界"])).rejects.toThrow("bge_m3_invalid_text_bytes");

		const controller = new AbortController();
		controller.abort(new Error("cancelled"));
		await expect(provider.embed(["ok"], controller.signal)).rejects.toThrow("cancelled");

		await provider.dispose();
		await expect(provider.embed(["ok"])).rejects.toThrow("bge_m3_provider_disposed");
	});
});

function options(limits: Partial<BgeM3EmbeddingProviderOptions["limits"]> = {}): BgeM3EmbeddingProviderOptions {
	return {
		artifacts: {
			model: { path: "model.onnx", sha256: "1".repeat(64) },
			tokenizer: { path: "tokenizer.json", sha256: "2".repeat(64) },
			tokenizerConfig: { path: "tokenizer_config.json", sha256: "3".repeat(64) },
			modelConfig: { path: "config.json", sha256: "4".repeat(64) },
		},
		limits: {
			maxTextsPerBatch: 4,
			maxTextBytes: 8,
			maxTokens: 512,
			requestTimeoutMs: 30_000,
			maxPendingRequests: 1,
			...limits,
		},
	};
}

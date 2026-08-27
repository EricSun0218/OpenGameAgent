import { describe, expect, it } from "vitest";
import { BgeM3OnnxEmbeddingProvider } from "./provider.js";

const directory = process.env["OGA_BGE_M3_MODEL_DIR"];
const integrationTest = directory ? it : it.skip;

describe("BGE-M3 real model smoke", () => {
	integrationTest(
		"runs the official INT8 ONNX model entirely in-process",
		async () => {
			if (!directory) return;
			const provider = new BgeM3OnnxEmbeddingProvider({
				modelDirectory: directory,
				manifest: { modelVersion: process.env["OGA_BGE_M3_MODEL_VERSION"] ?? "local-smoke" },
				maximumTokens: 128,
				timeoutMilliseconds: 180_000,
			});
			try {
				const vectors = await provider.embedDocuments(["build a safe shelter", "建造一座安全的房屋"]);
				expect(vectors).toHaveLength(2);
				for (const vector of vectors) {
					expect(vector).toHaveLength(1_024);
					const norm = Math.sqrt(vector.reduce((sum, value) => sum + value * value, 0));
					expect(norm).toBeCloseTo(1, 4);
				}
			} finally {
				await provider[Symbol.asyncDispose]();
			}
		},
		240_000,
	);
});

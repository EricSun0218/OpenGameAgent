import { createHash } from "node:crypto";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { BgeM3EmbeddingBackend, BgeM3EmbeddingBackendResult, BgeM3EmbeddingMetrics } from "./provider.js";
import { BgeM3OnnxEmbeddingProvider, validateBgeM3ModelDirectory } from "./provider.js";

const directories: string[] = [];

async function modelDirectory(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-bge-m3-"));
	directories.push(directory);
	await mkdir(join(directory, "onnx"));
	await writeFile(
		join(directory, "config.json"),
		JSON.stringify({ model_type: "xlm-roberta", hidden_size: 1024, max_position_embeddings: 8194 }),
	);
	await writeFile(
		join(directory, "tokenizer_config.json"),
		JSON.stringify({ tokenizer_class: "XLMRobertaTokenizer", model_max_length: 8192 }),
	);
	await writeFile(join(directory, "tokenizer.json"), "{}");
	await writeFile(join(directory, "sentencepiece.bpe.model"), "model");
	await writeFile(join(directory, "onnx", "model_int8.onnx"), "onnx");
	return directory;
}

function vector(): Float32Array {
	const value = new Float32Array(1_024);
	value[0] = 3;
	value[1] = 4;
	return value;
}

class FakeBackend implements BgeM3EmbeddingBackend {
	active = 0;
	maximumActive = 0;
	calls: readonly string[][] = [];
	disposed = false;
	gate: Promise<void> | undefined;

	async embed(
		texts: readonly string[],
		_maximumTokens: number,
		signal: AbortSignal,
	): Promise<BgeM3EmbeddingBackendResult> {
		this.calls = [...this.calls, [...texts]];
		this.active += 1;
		this.maximumActive = Math.max(this.maximumActive, this.active);
		try {
			if (this.gate) {
				await Promise.race([
					this.gate,
					new Promise<never>((_, reject) => {
						signal.addEventListener("abort", () => reject(new DOMException("cancelled", "AbortError")), {
							once: true,
						});
					}),
				]);
			}
			return {
				vectors: texts.map(vector),
				loadMilliseconds: 2,
				tokenizationMilliseconds: 3,
				inferenceMilliseconds: 4,
				truncatedInputs: 0,
			};
		} finally {
			this.active -= 1;
		}
	}

	async [Symbol.asyncDispose](): Promise<void> {
		this.disposed = true;
	}
}

afterEach(async () => {
	await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe("BGE-M3 model validation", () => {
	it("accepts the official local layout and verifies optional digests", async () => {
		const directory = await modelDirectory();
		const digest = createHash("sha256").update("onnx").digest("hex");
		const validated = await validateBgeM3ModelDirectory(directory, {
			modelVersion: "xenova-main-a206e10e",
			files: { "onnx/model_int8.onnx": { sha256: digest, minimumBytes: 4, maximumBytes: 8 } },
		});
		expect(validated.directory).toBe(directory);
		expect(validated.files["onnx/model_int8.onnx"]).toEqual({ bytes: 4, sha256: digest });
	});

	it("fails closed on integrity, architecture, and missing tokenizer assets", async () => {
		const integrityDirectory = await modelDirectory();
		await expect(
			validateBgeM3ModelDirectory(integrityDirectory, {
				modelVersion: "1",
				files: { "onnx/model_int8.onnx": { sha256: "0".repeat(64) } },
			}),
		).rejects.toThrow("SHA-256");

		const architectureDirectory = await modelDirectory();
		await writeFile(
			join(architectureDirectory, "config.json"),
			JSON.stringify({ model_type: "bert", hidden_size: 1024, max_position_embeddings: 8194 }),
		);
		await expect(validateBgeM3ModelDirectory(architectureDirectory, { modelVersion: "1" })).rejects.toThrow(
			"supported 1024-dimensional BGE-M3",
		);

		const missingDirectory = await modelDirectory();
		await rm(join(missingDirectory, "tokenizer.json"));
		await expect(validateBgeM3ModelDirectory(missingDirectory, { modelVersion: "1" })).rejects.toThrow();
	});
});

describe("BgeM3OnnxEmbeddingProvider", () => {
	it("isolates caller cancellation from the shared model validation", async () => {
		const directory = await modelDirectory();
		const provider = new BgeM3OnnxEmbeddingProvider({
			modelDirectory: directory,
			manifest: { modelVersion: "1" },
			backendFactory: () => new FakeBackend(),
		});
		try {
			const controller = new AbortController();
			const cancelledLoad = provider.load(controller.signal);
			controller.abort();
			await expect(cancelledLoad).rejects.toMatchObject({ name: "AbortError" });
			await expect(provider.load()).resolves.toBeUndefined();
		} finally {
			await provider[Symbol.asyncDispose]();
		}
	});

	it("batches, normalizes, versions preprocessing, and emits content-free metrics", async () => {
		const directory = await modelDirectory();
		const backend = new FakeBackend();
		const metrics: BgeM3EmbeddingMetrics[] = [];
		const provider = new BgeM3OnnxEmbeddingProvider({
			modelDirectory: directory,
			manifest: { modelVersion: "2026-08-28" },
			maximumBatchSize: 2,
			backendFactory: () => backend,
			onMetrics: (entry) => metrics.push(entry),
		});
		try {
			const vectors = await provider.embedDocuments(["secret phrase", "two", "three"]);
			expect(backend.calls).toEqual([["secret phrase", "two"], ["three"]]);
			expect(vectors).toHaveLength(3);
			expect(vectors[0]?.[0]).toBeCloseTo(0.6);
			expect(vectors[0]?.[1]).toBeCloseTo(0.8);
			expect(provider.identity).toEqual({
				model: "bge-m3-int8-onnx",
				version: "2026-08-28",
				dimensions: 1024,
				preprocessing: "xlm-roberta:mean-pool:l2:max-8192",
			});
			expect(JSON.stringify(metrics)).not.toContain("secret phrase");
			expect(metrics.map((entry) => entry.batchSize).sort()).toEqual([1, 2]);
		} finally {
			await provider[Symbol.asyncDispose]();
		}
		expect(backend.disposed).toBe(true);
	});

	it("bounds its queue and cancels queued work without invoking the backend", async () => {
		const directory = await modelDirectory();
		let release: (() => void) | undefined;
		const backend = new FakeBackend();
		backend.gate = new Promise<void>((resolvePromise) => {
			release = resolvePromise;
		});
		const provider = new BgeM3OnnxEmbeddingProvider({
			modelDirectory: directory,
			manifest: { modelVersion: "1" },
			maximumQueuedBatches: 1,
			backendFactory: () => backend,
		});
		try {
			const first = provider.embedQuery("one");
			await vi.waitFor(() => expect(backend.calls).toHaveLength(1));
			const controller = new AbortController();
			const second = provider.embedQuery("two", controller.signal);
			await Promise.resolve();
			await expect(provider.embedQuery("three")).rejects.toThrow("queue is full");
			controller.abort();
			release?.();
			await expect(second).rejects.toMatchObject({ name: "AbortError" });
			await expect(first).resolves.toHaveLength(1_024);
			expect(backend.calls).toHaveLength(1);
		} finally {
			await provider[Symbol.asyncDispose]();
		}
	});

	it("times out an in-flight batch and reports a bounded failure category", async () => {
		const directory = await modelDirectory();
		const backend = new FakeBackend();
		backend.gate = new Promise<void>(() => undefined);
		const metrics: BgeM3EmbeddingMetrics[] = [];
		const provider = new BgeM3OnnxEmbeddingProvider({
			modelDirectory: directory,
			manifest: { modelVersion: "1" },
			timeoutMilliseconds: 100,
			backendFactory: () => backend,
			onMetrics: (entry) => metrics.push(entry),
		});
		try {
			await expect(provider.embedQuery("bounded input")).rejects.toThrow("timeout");
			expect(metrics.at(-1)?.failure).toBe("timeout");
		} finally {
			await provider[Symbol.asyncDispose]();
		}
	});
});

import { beforeEach, describe, expect, it, vi } from "vitest";
import { BGE_M3_DIMENSIONS, BgeM3EmbeddingProvider, type BgeM3EmbeddingProviderOptions } from "../src/index.js";

interface PostedEmbedRequest {
	readonly kind: "embed";
	readonly requestId: number;
	readonly texts: readonly string[];
}

interface FakeWorker {
	readonly posted: unknown[];
	readonly terminated: boolean;
	emit(event: string, value: unknown): void;
}

const workerHarness = vi.hoisted(() => ({ workers: [] as FakeWorker[] }));

vi.mock("node:worker_threads", () => {
	class Worker {
		readonly posted: unknown[] = [];
		terminated = false;
		private readonly listeners = new Map<string, Array<(value: unknown) => void>>();

		constructor() {
			workerHarness.workers.push(this);
		}

		on(event: string, listener: (value: unknown) => void): this {
			const listeners = this.listeners.get(event) ?? [];
			listeners.push(listener);
			this.listeners.set(event, listeners);
			return this;
		}

		postMessage(message: unknown): void {
			this.posted.push(message);
		}

		emit(event: string, value: unknown): void {
			for (const listener of this.listeners.get(event) ?? []) listener(value);
		}

		terminate(): Promise<number> {
			this.terminated = true;
			return Promise.resolve(0);
		}
	}

	return { Worker };
});

describe("BgeM3EmbeddingProvider pending requests", () => {
	beforeEach(() => {
		workerHarness.workers.length = 0;
	});

	it("preserves FIFO order, exact queued cancellation, and recovered capacity", async () => {
		const provider = new BgeM3EmbeddingProvider(options({ maxPendingRequests: 3 }));
		const cancelled = new AbortController();
		const first = provider.embed(["first"]);
		const second = provider.embed(["same"], cancelled.signal);
		const third = provider.embed(["same"]);
		await expect(provider.embed(["full"])).rejects.toThrow("bge_m3_request_capacity");

		const cancelledOutcome = expect(second).rejects.toThrow("cancelled");
		cancelled.abort(new Error("cancelled"));
		await cancelledOutcome;
		const fourth = provider.embed(["fourth"]);

		const worker = onlyWorker();
		worker.emit("message", { kind: "ready" });
		expect(postedRequests(worker).map(({ texts }) => texts)).toEqual([["first"]]);

		worker.emit("message", resultFor(postedRequests(worker)[0]));
		await expect(first).resolves.toHaveLength(1);
		expect(postedRequests(worker).map(({ texts }) => texts)).toEqual([["first"], ["same"]]);

		worker.emit("message", resultFor(postedRequests(worker)[1]));
		await expect(third).resolves.toHaveLength(1);
		expect(postedRequests(worker).map(({ texts }) => texts)).toEqual([["first"], ["same"], ["fourth"]]);

		worker.emit("message", resultFor(postedRequests(worker)[2]));
		await expect(fourth).resolves.toHaveLength(1);
		await provider.dispose();
	});

	it("keeps the warm worker and fences a cancelled in-flight result", async () => {
		const provider = new BgeM3EmbeddingProvider(options({ maxPendingRequests: 2 }));
		const cancelled = new AbortController();
		const first = provider.embed(["first"], cancelled.signal);
		const second = provider.embed(["second"]);
		const firstWorker = onlyWorker();
		firstWorker.emit("message", { kind: "ready" });

		const cancelledOutcome = expect(first).rejects.toThrow("cancelled");
		cancelled.abort(new Error("cancelled"));
		await cancelledOutcome;
		expect(firstWorker.terminated).toBe(false);
		expect(workerHarness.workers).toHaveLength(1);
		expect(postedRequests(firstWorker).map(({ texts }) => texts)).toEqual([["first"]]);

		firstWorker.emit("message", resultFor(postedRequests(firstWorker)[0]));
		expect(postedRequests(firstWorker).map(({ texts }) => texts)).toEqual([["first"], ["second"]]);
		firstWorker.emit("message", resultFor(postedRequests(firstWorker)[1]));
		await expect(second).resolves.toHaveLength(1);
		await provider.dispose();
	});

	it("rejects active and queued work when the provider closes", async () => {
		const provider = new BgeM3EmbeddingProvider(options({ maxPendingRequests: 2 }));
		const active = provider.embed(["active"]);
		const queued = provider.embed(["queued"]);
		const worker = onlyWorker();

		const activeOutcome = expect(active).rejects.toThrow("bge_m3_provider_disposed");
		const queuedOutcome = expect(queued).rejects.toThrow("bge_m3_provider_disposed");
		await provider.dispose();

		await activeOutcome;
		await queuedOutcome;
		expect(worker.terminated).toBe(true);
		expect(postedRequests(worker)).toEqual([]);
	});
});

function onlyWorker(): FakeWorker {
	const worker = workerHarness.workers[0];
	if (worker === undefined) throw new Error("Expected one embedding worker");
	return worker;
}

function postedRequests(worker: FakeWorker | undefined): readonly PostedEmbedRequest[] {
	if (worker === undefined) return [];
	return worker.posted.filter(
		(message): message is PostedEmbedRequest =>
			typeof message === "object" && message !== null && "kind" in message && message.kind === "embed",
	);
}

function resultFor(request: PostedEmbedRequest | undefined) {
	if (request === undefined) throw new Error("Expected a posted embedding request");
	return {
		kind: "result" as const,
		requestId: request.requestId,
		vectors: request.texts.map(() => new Float32Array(BGE_M3_DIMENSIONS).buffer),
	};
}

function options(limits: Partial<BgeM3EmbeddingProviderOptions["limits"]>): BgeM3EmbeddingProviderOptions {
	return {
		artifacts: {
			model: { path: "model.onnx", sha256: "1".repeat(64) },
			tokenizer: { path: "tokenizer.json", sha256: "2".repeat(64) },
			tokenizerConfig: { path: "tokenizer_config.json", sha256: "3".repeat(64) },
			modelConfig: { path: "config.json", sha256: "4".repeat(64) },
		},
		limits: {
			maxTextsPerBatch: 4,
			maxTextBytes: 64,
			maxTokens: 512,
			requestTimeoutMs: 30_000,
			maxPendingRequests: 1,
			...limits,
		},
	};
}

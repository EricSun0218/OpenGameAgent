import { describe, expect, it, vi } from "vitest";
import {
	LocalGameModelCatalogClient,
	type LocalGameModelProcessController,
	LocalGameModelService,
} from "./local-model-service.js";

function json(value: unknown, status = 200): Response {
	return new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json" } });
}

describe("local model catalog", () => {
	it("discovers OpenAI-compatible and Ollama catalogs through fixed loopback endpoints", async () => {
		const openAiFetch = vi
			.fn<typeof globalThis.fetch>()
			.mockResolvedValue(json({ data: [{ id: "vision" }, { id: "text" }] }));
		const openAi = new LocalGameModelCatalogClient({ backend: "lm-studio", fetch: openAiFetch });
		expect(await openAi.list()).toEqual([{ id: "text" }, { id: "vision" }]);
		expect(openAiFetch).toHaveBeenCalledWith(
			new URL("http://127.0.0.1:1234/v1/models"),
			expect.objectContaining({ method: "GET", redirect: "error" }),
		);

		const ollamaFetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(json({ models: [{ name: "qwen:latest" }] }));
		const ollama = new LocalGameModelCatalogClient({ backend: "ollama", fetch: ollamaFetch });
		expect(await ollama.list()).toEqual([{ id: "qwen:latest" }]);
		expect(ollamaFetch).toHaveBeenCalledWith(new URL("http://127.0.0.1:11434/api/tags"), expect.any(Object));
	});

	it("supports llama.cpp router state and explicit load/unload without arbitrary endpoints", async () => {
		const fetcher = vi.fn<typeof globalThis.fetch>().mockImplementation(async (url) => {
			if (String(url).endsWith("/models")) {
				return json({
					data: [
						{
							id: "local.gguf",
							status: { value: "loaded" },
							architecture: { input_modalities: ["text", "image", "audio"] },
							meta: { n_ctx: 32768 },
						},
					],
				});
			}
			return json({ ok: true });
		});
		const client = new LocalGameModelCatalogClient({ backend: "llama.cpp", fetch: fetcher });
		expect(await client.list()).toEqual([
			{ id: "local.gguf", state: "loaded", input: ["text", "image"], contextWindow: 32768 },
		]);
		await client.load("local.gguf");
		await client.unload("local.gguf");
		expect(fetcher).toHaveBeenCalledWith(
			new URL("http://127.0.0.1:8080/models/load"),
			expect.objectContaining({ method: "POST", body: JSON.stringify({ model: "local.gguf" }) }),
		);
		expect(fetcher).toHaveBeenCalledWith(
			new URL("http://127.0.0.1:8080/models/unload"),
			expect.objectContaining({ method: "POST", body: JSON.stringify({ model: "local.gguf" }) }),
		);
		expect(
			() => new LocalGameModelCatalogClient({ backend: "vllm", endpoint: "https://models.example.test/v1" }),
		).toThrow("loopback");
	});

	it("fails closed for invalid, duplicate, oversized, and remote catalogs", async () => {
		const duplicate = new LocalGameModelCatalogClient({
			backend: "localai",
			fetch: async () => json({ data: [{ id: "same" }, { id: "same" }] }),
		});
		await expect(duplicate.list()).rejects.toThrow("duplicate");
		const invalid = new LocalGameModelCatalogClient({
			backend: "localai",
			fetch: async () => json({ data: "not-an-array" }),
		});
		await expect(invalid.list()).rejects.toThrow("invalid");
		const oversized = new LocalGameModelCatalogClient({
			backend: "localai",
			maximumResponseBytes: 1024,
			fetch: async () => json({ data: [{ id: "x".repeat(2000) }] }),
		});
		await expect(oversized.list()).rejects.toThrow("too large");
	});
});

describe("local model service lifecycle", () => {
	it("uses an already running service without claiming process ownership", async () => {
		const process: LocalGameModelProcessController = {
			start: vi.fn(async () => undefined),
			stop: vi.fn(async () => undefined),
		};
		const service = new LocalGameModelService({
			backend: "vllm",
			process,
			fetch: async () => json({ data: [{ id: "ready" }] }),
		});
		expect(await service.start()).toEqual({ state: "ready", models: [{ id: "ready" }], startedProcess: false });
		expect(process.start).not.toHaveBeenCalled();
		await service.stop();
		expect(process.stop).not.toHaveBeenCalled();
	});

	it("starts an optional host process and waits for the same catalog to become ready", async () => {
		let running = false;
		const process: LocalGameModelProcessController = {
			start: vi.fn(async () => {
				running = true;
			}),
			stop: vi.fn(async () => {
				running = false;
			}),
		};
		const service = new LocalGameModelService({
			backend: "ollama",
			process,
			startupTimeoutMilliseconds: 1000,
			pollIntervalMilliseconds: 10,
			fetch: async () => {
				if (!running) throw new Error("offline");
				return json({ models: [{ name: "game-model" }] });
			},
		});
		expect(await service.start()).toEqual({
			state: "ready",
			models: [{ id: "game-model" }],
			startedProcess: true,
		});
		expect(process.start).toHaveBeenCalledOnce();
		await service.stop();
		expect(process.stop).toHaveBeenCalledOnce();
		expect(service.snapshot().state).toBe("stopped");
	});

	it("deduplicates concurrent starts and returns bounded failure categories", async () => {
		const process: LocalGameModelProcessController = {
			start: vi.fn(async () => undefined),
			stop: vi.fn(async () => undefined),
		};
		const service = new LocalGameModelService({
			backend: "localai",
			process,
			startupTimeoutMilliseconds: 100,
			pollIntervalMilliseconds: 10,
			fetch: async () => {
				throw new Error("secret provider detail");
			},
		});
		const [first, second] = await Promise.all([service.start(), service.start()]);
		expect(first).toEqual(second);
		expect(first).toEqual({ state: "failed", models: [], startedProcess: false, failure: "unreachable" });
		expect(process.start).toHaveBeenCalledOnce();
		expect(process.stop).toHaveBeenCalledOnce();
	});

	it("stops an owned process when startup is cancelled", async () => {
		const controller = new AbortController();
		const process: LocalGameModelProcessController = {
			start: vi.fn(async () => {
				controller.abort(new Error("cancelled"));
			}),
			stop: vi.fn(async () => undefined),
		};
		const service = new LocalGameModelService({
			backend: "localai",
			process,
			startupTimeoutMilliseconds: 1000,
			pollIntervalMilliseconds: 10,
			fetch: async () => {
				throw new Error("offline");
			},
		});
		expect(await service.start(controller.signal)).toEqual({
			state: "failed",
			models: [],
			startedProcess: false,
			failure: "cancelled",
		});
		expect(process.stop).toHaveBeenCalledOnce();
	});
});

import { describe, expect, it } from "vitest";
import { createLocalGameModelPreset } from "./local-models.js";
import { createPiGameModelRegistry } from "./model-registry.js";

describe("PiGameModelRegistry", () => {
	it("resolves only host-registered model profiles", () => {
		const registry = createPiGameModelRegistry({
			includeBuiltinProviders: false,
			providers: [
				{
					id: "local-engine",
					baseUrl: "http://127.0.0.1:11434/v1",
					models: [
						{
							id: "vision-model",
							input: ["text", "image"],
							contextWindow: 32_768,
							maximumOutputTokens: 4096,
						},
					],
				},
			],
			profiles: [{ id: "npc-vision", provider: "local-engine", model: "vision-model" }],
		});

		expect(registry.resolve("npc-vision").descriptor).toEqual(
			expect.objectContaining({
				profileId: "npc-vision",
				provider: "local-engine",
				model: "vision-model",
				input: ["text", "image"],
			}),
		);
		expect(() => registry.resolve("client-injected-profile")).toThrow(/Unknown model profile/);
	});

	it("rejects insecure remote endpoints and invalid profile targets before any request", () => {
		expect(() =>
			createPiGameModelRegistry({
				includeBuiltinProviders: false,
				providers: [
					{
						id: "remote",
						baseUrl: "http://models.example.com/v1",
						models: [{ id: "model", contextWindow: 4096, maximumOutputTokens: 512 }],
					},
				],
				profiles: [{ id: "remote", provider: "remote", model: "model" }],
			}),
		).toThrow(/HTTPS/);

		expect(() =>
			createPiGameModelRegistry({
				includeBuiltinProviders: false,
				profiles: [{ id: "missing", provider: "missing", model: "missing" }],
			}),
		).toThrow(/unknown provider\/model/);
	});
});

describe("local model presets", () => {
	it("creates host-owned Ollama and LM Studio profiles without requiring a secret", () => {
		const ollama = createLocalGameModelPreset({ backend: "ollama", model: "qwen3.5-9b", input: ["text", "image"] });
		expect(ollama.provider).toMatchObject({
			id: "ollama",
			baseUrl: "http://127.0.0.1:11434/v1",
			protocol: "responses",
			anonymousApiKey: "ollama",
		});
		expect(ollama.profile.provider).toBe("ollama");
		expect(ollama.provider.models[0]?.input).toEqual(["text", "image"]);

		const studio = createLocalGameModelPreset({ backend: "lm-studio", model: "local-model" });
		expect(studio.provider.baseUrl).toBe("http://127.0.0.1:1234/v1");
		expect(studio.provider.protocol).toBe("responses");
	});

	it("supports completion-only servers and rejects remote endpoints", () => {
		const server = createLocalGameModelPreset({ backend: "llama.cpp", model: "game-model" });
		expect(server.provider).toMatchObject({
			baseUrl: "http://127.0.0.1:8080/v1",
			protocol: "completions",
		});
		expect(() =>
			createLocalGameModelPreset({ backend: "vllm", model: "model", endpoint: "https://models.example.test/v1" }),
		).toThrow("loopback");
	});
});

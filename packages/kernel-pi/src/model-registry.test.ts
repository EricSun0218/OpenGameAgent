import { describe, expect, it } from "vitest";
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

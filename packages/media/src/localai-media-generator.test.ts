import type { GameSessionKey } from "@opengameagent/protocol";
import { describe, expect, it } from "vitest";
import { LocalAiMediaGenerator } from "./localai-media-generator.js";

const session: GameSessionKey = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 1,
	ownerId: "owner",
	sessionId: "session",
	actorId: "actor",
};

function png(): Uint8Array {
	return new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0]);
}

function wav(): Uint8Array {
	return new Uint8Array(Buffer.from("RIFF0000WAVE", "ascii"));
}

function mp4(): Uint8Array {
	return new Uint8Array(Buffer.from("0000ftypisom", "ascii"));
}

function jsonOutput(data: Uint8Array, id: string): Response {
	return new Response(JSON.stringify({ id, data: [{ b64_json: Buffer.from(data).toString("base64") }] }), {
		status: 200,
		headers: { "content-type": "application/json" },
	});
}

describe("LocalAiMediaGenerator", () => {
	it("uses LocalAI image generation with bounded inline output", async () => {
		const calls: Array<{ url: string; init?: RequestInit }> = [];
		const generator = new LocalAiMediaGenerator({
			model: "local-image",
			fetch: async (input, init) => {
				calls.push({ url: String(input), ...(init ? { init } : {}) });
				return jsonOutput(png(), "image-1");
			},
		});
		const result = await generator.generate({
			id: "request-1",
			session,
			kind: "image",
			prompt: "a lantern garden",
			sources: [],
			parameters: { size: "1024x1024", n: 1 },
		});
		expect(calls[0]?.url).toBe("http://127.0.0.1:8080/v1/images/generations");
		expect(JSON.parse(String(calls[0]?.init?.body))).toEqual({
			model: "local-image",
			prompt: "a lantern garden",
			response_format: "b64_json",
			size: "1024x1024",
			n: 1,
		});
		expect(result).toMatchObject({ provider: "localai", model: "local-image", responseId: "image-1" });
		expect(result.outputs[0]?.data).toEqual(png());
	});

	it("uses OpenAI-compatible local speech and validates the returned format", async () => {
		const calls: Array<{ url: string; init?: RequestInit }> = [];
		const fetcher: typeof fetch = async (input, init) => {
			calls.push({ url: String(input), ...(init ? { init } : {}) });
			return new Response(wav(), { status: 200, headers: { "x-request-id": "speech-1" } });
		};
		const generator = new LocalAiMediaGenerator({ model: "local-tts", fetch: fetcher });
		const result = await generator.generate({
			id: "request-2",
			session,
			kind: "audio",
			prompt: "hello",
			sources: [],
			parameters: { voice: "npc-one", response_format: "wav", speed: 1.1 },
		});
		expect(calls[0]?.url).toBe("http://127.0.0.1:8080/v1/audio/speech");
		expect(JSON.parse(String(calls[0]?.init?.body))).toMatchObject({
			model: "local-tts",
			input: "hello",
			voice: "npc-one",
			response_format: "wav",
		});
		expect(result.outputs[0]).toMatchObject({ kind: "audio", mimeType: "audio/wav" });
	});

	it("maps image and audio conditioning into LocalAI video without external URLs", async () => {
		let payload: Record<string, unknown> | undefined;
		const generator = new LocalAiMediaGenerator({
			model: "local-video",
			fetch: async (_input, init) => {
				payload = JSON.parse(String(init?.body));
				return jsonOutput(mp4(), "video-1");
			},
		});
		const result = await generator.generate({
			id: "request-3",
			session,
			kind: "video",
			prompt: "the scene moves",
			sources: [
				{ kind: "image", mimeType: "image/png", data: png() },
				{ kind: "audio", mimeType: "audio/wav", data: wav() },
			],
			parameters: { width: 1280, height: 720, fps: 24 },
		});
		expect(payload).toMatchObject({
			model: "local-video",
			prompt: "the scene moves",
			response_format: "b64_json",
			width: 1280,
			height: 720,
			fps: 24,
		});
		expect(payload?.["start_image"]).toMatch(/^data:image\/png;base64,/u);
		expect(payload?.["audio"]).toMatch(/^data:audio\/wav;base64,/u);
		expect(result.outputs[0]).toMatchObject({ kind: "video", mimeType: "video/mp4" });
	});

	it("rejects remote endpoints, arbitrary parameters, image edit ambiguity, and malformed outputs", async () => {
		expect(() => new LocalAiMediaGenerator({ model: "x", endpoint: "https://remote.example.test/v1" })).toThrow(
			"loopback",
		);
		const generator = new LocalAiMediaGenerator({
			model: "x",
			fetch: async () => jsonOutput(new TextEncoder().encode("not an image"), "bad"),
		});
		await expect(
			generator.generate({
				id: "request-4",
				session,
				kind: "image",
				prompt: "x",
				sources: [],
				parameters: { endpoint: "https://attacker.test" },
			}),
		).rejects.toThrow("unsupported parameter");
		await expect(
			generator.generate({
				id: "request-5",
				session,
				kind: "image",
				prompt: "x",
				sources: [{ kind: "image", mimeType: "image/png", data: png() }],
			}),
		).rejects.toThrow("trusted workflow");
		await expect(
			generator.generate({ id: "request-6", session, kind: "image", prompt: "x", sources: [] }),
		).rejects.toThrow("do not match");
	});
});

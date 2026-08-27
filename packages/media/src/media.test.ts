import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type {
	AssistantImages,
	ImagesApi,
	ImagesContext,
	ImagesModel,
	ImagesModels,
	ImagesOptions,
} from "@earendil-works/pi-ai";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ComfyUiImageGenerator } from "./comfyui-image-generator.js";
import { OpenAIImageGenerator, VolcengineImageGenerator } from "./http-image-generators.js";
import { GameMediaRegistry } from "./media.js";
import { PiGameImageGenerator } from "./pi-images.js";
import { FileGameMediaResourceStore } from "./resource-store.js";

const roots: string[] = [];
afterEach(async () => {
	await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

const session = {
	worldId: "world",
	saveId: "save",
	timelineId: "timeline",
	generation: 3,
	ownerId: "owner",
	sessionId: "session",
	actorId: "actor",
};

function png(): Uint8Array {
	return Buffer.from(
		"89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000049454e44ae426082",
		"hex",
	);
}

describe("GameMediaRegistry", () => {
	it("enforces bounds and preserves provider/model identity", async () => {
		const registry = new GameMediaRegistry({ maximumOutputs: 2, timeoutMilliseconds: 1_000 });
		registry.register({
			provider: "local",
			model: "image",
			kinds: ["image"],
			async generate(request) {
				return {
					provider: "local",
					model: "image",
					outputs: [{ kind: request.kind, mimeType: "image/png", data: png() }],
				};
			},
		});
		const result = await registry.generate("local", "image", {
			id: "request-1",
			session,
			kind: "image",
			prompt: "draw a house",
			sources: [],
		});
		expect(result.outputs).toHaveLength(1);
		await expect(
			registry.generate("local", "image", {
				id: "request-2",
				session,
				kind: "video",
				prompt: "animate",
				sources: [],
			}),
		).rejects.toThrow("does not support");
	});
});

describe("FileGameMediaResourceStore", () => {
	it("round-trips content-addressed media and detects corruption", async () => {
		const root = await mkdtemp(join(tmpdir(), "oga-media-"));
		roots.push(root);
		const store = new FileGameMediaResourceStore(root);
		const resource = await store.save({ kind: "image", mimeType: "image/png", data: png(), name: "one.png" });
		expect(resource.id).toBe(`sha256:${resource.sha256}`);
		const result = await store.read(resource);
		expect(result.data).toEqual(png());

		const path = join(root, "objects", resource.sha256.slice(0, 2), resource.sha256);
		const corrupt = await readFile(path);
		corrupt[corrupt.length - 1] = (corrupt.at(-1) ?? 0) ^ 1;
		await writeFile(path, corrupt);
		await expect(store.read(resource)).rejects.toThrow("integrity");
	});

	it("rejects declared media that does not match bytes", async () => {
		const root = await mkdtemp(join(tmpdir(), "oga-media-"));
		roots.push(root);
		const store = new FileGameMediaResourceStore(root);
		await expect(store.save({ kind: "audio", mimeType: "audio/wav", data: png() })).rejects.toThrow("MIME");
	});
});

describe("PiGameImageGenerator", () => {
	it("uses Pi image models without adding a second agent loop", async () => {
		const model = {
			id: "image-model",
			name: "image-model",
			api: "test-images",
			provider: "test",
			baseUrl: "https://example.invalid",
			input: ["text", "image"],
			output: ["image"],
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
		} as ImagesModel<ImagesApi>;
		const response: AssistantImages = {
			api: "test-images",
			provider: "test",
			model: "image-model",
			output: [{ type: "image", mimeType: "image/png", data: Buffer.from(png()).toString("base64") }],
			stopReason: "stop",
			timestamp: Date.now(),
		};
		const generateImages = vi.fn(
			async (_model: ImagesModel<ImagesApi>, _context: ImagesContext, _options?: ImagesOptions) => response,
		);
		const models = {
			getModel: () => model,
			generateImages,
		} as unknown as ImagesModels;
		const generator = new PiGameImageGenerator({ models, model });
		const result = await generator.generate({
			id: "image-1",
			session,
			kind: "image",
			prompt: "create",
			sources: [{ kind: "image", mimeType: "image/png", data: png() }],
			parameters: { size: "1024x1024" },
		});
		expect(result.outputs[0]?.data).toEqual(png());
		expect(generateImages).toHaveBeenCalledOnce();
		const firstCall = generateImages.mock.calls[0];
		if (!firstCall) throw new Error("Expected an image provider call.");
		const [calledModel, context, options] = firstCall;
		expect(calledModel).toBe(model);
		expect(context?.input).toHaveLength(2);
		expect(options?.metadata).toEqual({ size: "1024x1024" });
	});

	it("rejects malformed provider base64", async () => {
		const model = { id: "m", provider: "p" } as ImagesModel<ImagesApi>;
		const models = {
			getModel: () => model,
			generateImages: async () => ({
				api: "test",
				provider: "p",
				model: "m",
				output: [{ type: "image", mimeType: "image/png", data: "not base64" }],
				stopReason: "stop",
				timestamp: Date.now(),
			}),
		} as unknown as ImagesModels;
		const generator = new PiGameImageGenerator({ models, model });
		await expect(generator.generate({ id: "x", session, kind: "image", prompt: "x", sources: [] })).rejects.toThrow(
			"base64",
		);
	});
});

describe("HTTP image generators", () => {
	it("builds bounded OpenAI generation and multipart edit requests", async () => {
		const calls: Array<{ url: string; init: RequestInit }> = [];
		const fetch = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
			calls.push({ url: String(input), init: init ?? {} });
			return new Response(JSON.stringify({ data: [{ b64_json: Buffer.from(png()).toString("base64") }] }), {
				status: 200,
				headers: { "content-type": "application/json", "x-request-id": "request-1" },
			});
		});
		const generator = new OpenAIImageGenerator({
			provider: "openai",
			model: "gpt-image",
			endpoint: "https://api.example.test/v1/images",
			fetch,
			authentication: { resolve: async () => ({ bearerToken: "secret-token" }) },
		});
		const generated = await generator.generate({
			id: "generation",
			session,
			kind: "image",
			prompt: "a garden",
			sources: [],
			parameters: { size: "1024x1536", output_format: "png", n: 1 },
		});
		expect(generated.responseId).toBe("request-1");
		expect(calls[0]?.url).toBe("https://api.example.test/v1/images/generations");
		expect(JSON.parse(String(calls[0]?.init.body))).toMatchObject({
			model: "gpt-image",
			prompt: "a garden",
			size: "1024x1536",
		});
		expect(new Headers(calls[0]?.init.headers).get("authorization")).toBe("Bearer secret-token");

		await generator.generate({
			id: "edit",
			session,
			kind: "image",
			prompt: "add a lantern",
			sources: [
				{ kind: "image", mimeType: "image/png", data: png() },
				{ kind: "image", mimeType: "image/png", data: png() },
			],
		});
		expect(calls[1]?.url).toBe("https://api.example.test/v1/images/edits");
		const form = calls[1]?.init.body;
		expect(form).toBeInstanceOf(FormData);
		expect((form as FormData).getAll("image[]")).toHaveLength(2);
	});

	it("builds Volcengine Seedream requests with image arrays and no watermark by default", async () => {
		let body: Record<string, unknown> | undefined;
		const fetch = vi.fn(async (_input: string | URL | Request, init?: RequestInit) => {
			body = JSON.parse(String(init?.body)) as Record<string, unknown>;
			return new Response(
				JSON.stringify({
					data: [{ b64_json: Buffer.from(png()).toString("base64") }],
					usage: { generated_images: 1 },
				}),
				{ status: 200, headers: { "content-type": "application/json", "x-tt-logid": "ark-1" } },
			);
		});
		const generator = new VolcengineImageGenerator({
			provider: "volcengine",
			model: "seedream",
			endpoint: "https://ark.example.test/api/v3/images/generations",
			fetch,
		});
		const result = await generator.generate({
			id: "seedream-1",
			session,
			kind: "image",
			prompt: "a village",
			sources: [{ kind: "image", mimeType: "image/png", data: png() }],
			parameters: { size: "2048x1152" },
		});
		expect(body).toMatchObject({
			model: "seedream",
			response_format: "b64_json",
			size: "2048x1152",
			stream: false,
			watermark: false,
		});
		expect(body?.["image"]).toEqual([`data:image/png;base64,${Buffer.from(png()).toString("base64")}`]);
		expect(result.responseId).toBe("ark-1");
	});

	it("does not expose credentials, prompts, or provider bodies in errors", async () => {
		const hidden = "private-value";
		const generator = new OpenAIImageGenerator({
			provider: "openai",
			model: "gpt-image",
			endpoint: "https://api.example.test/v1/images",
			authentication: { resolve: async () => ({ bearerToken: hidden }) },
			fetch: async () => new Response(JSON.stringify({ error: hidden }), { status: 400 }),
		});
		let message = "";
		try {
			await generator.generate({
				id: "failed",
				session,
				kind: "image",
				prompt: hidden,
				sources: [],
			});
		} catch (error) {
			message = error instanceof Error ? error.message : String(error);
		}
		expect(message).toBe("The image provider returned HTTP 400.");
		expect(message).not.toContain(hidden);
	});
});

describe("ComfyUiImageGenerator", () => {
	it("uploads references, submits a host workflow, polls history, and downloads bounded outputs", async () => {
		const calls: Array<{ url: URL; init: RequestInit }> = [];
		let submitted: Record<string, unknown> | undefined;
		const fetch = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
			const url = new URL(String(input));
			calls.push({ url, init: init ?? {} });
			if (url.pathname.endsWith("/upload/image")) return new Response("{}", { status: 200 });
			if (url.pathname.endsWith("/prompt")) {
				submitted = JSON.parse(String(init?.body)) as Record<string, unknown>;
				return new Response(JSON.stringify({ prompt_id: "prompt-1" }), { status: 200 });
			}
			if (url.pathname.endsWith("/history/prompt-1")) {
				return new Response(
					JSON.stringify({
						"prompt-1": {
							status: { completed: true },
							outputs: { "9": { images: [{ filename: "output.png", subfolder: "", type: "output" }] } },
						},
					}),
					{ status: 200 },
				);
			}
			if (url.pathname.endsWith("/view")) {
				return new Response(png(), { status: 200, headers: { "content-type": "image/png" } });
			}
			return new Response(undefined, { status: 404 });
		});
		const generator = new ComfyUiImageGenerator({
			model: "local-workflow",
			endpoint: "http://127.0.0.1:8188/",
			workflow: {
				"6": { class_type: "CLIPTextEncode", inputs: { text: "" } },
				"7": { class_type: "LoadImage", inputs: { image: "" } },
				"9": { class_type: "SaveImage", inputs: { images: ["8", 0] } },
			},
			promptTarget: { nodeId: "6", input: "text" },
			referenceTargets: [{ nodeId: "7", input: "image" }],
			outputNodeIds: ["9"],
			fetch,
		});
		const progress: string[] = [];
		const result = await generator.generate(
			{
				id: "comfy-1",
				session,
				kind: "image",
				prompt: "a lantern garden",
				sources: [{ kind: "image", mimeType: "image/png", data: png() }],
			},
			(event) => {
				progress.push(event.stage);
			},
		);
		const prompt = submitted?.["prompt"] as Record<string, { inputs: Record<string, unknown> }>;
		expect(prompt["6"]?.inputs["text"]).toBe("a lantern garden");
		expect(prompt["7"]?.inputs["image"]).toMatch(/^[a-f0-9]{64}\.png$/u);
		expect(calls.map((call) => call.url.pathname)).toEqual(["/upload/image", "/prompt", "/history/prompt-1", "/view"]);
		expect(calls[3]?.url.searchParams.get("filename")).toBe("output.png");
		expect(result.responseId).toBe("prompt-1");
		expect(Buffer.from(result.outputs[0]?.data ?? [])).toEqual(png());
		expect(progress).toEqual(["submitted", "completed"]);
	});

	it("rejects remote endpoints and workflow-controlled reference overflow", async () => {
		expect(
			() =>
				new ComfyUiImageGenerator({
					model: "workflow",
					endpoint: "https://remote.example.test/",
					workflow: { "1": { inputs: { text: "" } } },
					promptTarget: { nodeId: "1", input: "text" },
				}),
		).toThrow("loopback");
	});
});

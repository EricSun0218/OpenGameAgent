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

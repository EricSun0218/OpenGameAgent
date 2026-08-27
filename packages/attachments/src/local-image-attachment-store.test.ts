import { mkdir, mkdtemp, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { LocalGameImageAttachmentStore } from "./local-image-attachment-store.js";

const roots: string[] = [];

afterEach(async () => {
	await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

function png(width = 1, height = 1): Uint8Array {
	const bytes = Buffer.from(
		"89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000049454e44ae426082",
		"hex",
	);
	bytes.writeUInt32BE(width, 16);
	bytes.writeUInt32BE(height, 20);
	return bytes;
}

function gif(width: number, height: number): Uint8Array {
	const bytes = Buffer.alloc(10);
	bytes.write("GIF89a", 0, "ascii");
	bytes.writeUInt16LE(width, 6);
	bytes.writeUInt16LE(height, 8);
	return bytes;
}

function jpeg(width: number, height: number): Uint8Array {
	const bytes = Buffer.alloc(21);
	bytes.set([0xff, 0xd8, 0xff, 0xc0, 0x00, 0x11, 0x08]);
	bytes.writeUInt16BE(height, 7);
	bytes.writeUInt16BE(width, 9);
	return bytes;
}

function webp(width: number, height: number): Uint8Array {
	const bytes = Buffer.alloc(30);
	bytes.write("RIFF", 0, "ascii");
	bytes.writeUInt32LE(22, 4);
	bytes.write("WEBP", 8, "ascii");
	bytes.write("VP8X", 12, "ascii");
	bytes.writeUInt32LE(10, 16);
	bytes.writeUIntLE(width - 1, 24, 3);
	bytes.writeUIntLE(height - 1, 27, 3);
	return bytes;
}

describe("LocalGameImageAttachmentStore", () => {
	it("atomically admits, deduplicates, reloads, and verifies content-addressed PNG attachments", async () => {
		const root = await mkdtemp(join(tmpdir(), "oga-attachments-"));
		roots.push(root);
		const store = new LocalGameImageAttachmentStore({ directory: root });
		const [first, second] = await Promise.all([
			store.admit("image/png", png(7, 9)),
			store.admit("image/png", png(7, 9)),
		]);
		expect(second).toEqual(first);
		expect(first).toMatchObject({ mimeType: "image/png", width: 7, height: 9 });

		const reloaded = await new LocalGameImageAttachmentStore({ directory: root }).read(first.id);
		expect(reloaded?.reference).toEqual(first);
		expect(Array.from(reloaded?.data ?? [])).toEqual(Array.from(png(7, 9)));
	});

	it("rejects invalid bytes, MIME mismatches, pixel excess, and pre-cancelled operations", async () => {
		const root = await mkdtemp(join(tmpdir(), "oga-attachments-"));
		roots.push(root);
		const store = new LocalGameImageAttachmentStore({ directory: root, maximumPixels: 16 });
		await expect(store.admit("image/png", Buffer.from("not-an-image"))).rejects.toThrow("supported bounded image");
		await expect(store.admit("image/jpeg", png())).rejects.toThrow("MIME");
		await expect(store.admit("image/png", png(5, 5))).rejects.toThrow("pixel limit");
		const abort = new AbortController();
		abort.abort();
		await expect(store.admit("image/png", png(), abort.signal)).rejects.toThrow("aborted");
	});

	it.each([
		["image/jpeg", jpeg(32, 24), 32, 24],
		["image/gif", gif(18, 12), 18, 12],
		["image/webp", webp(40, 20), 40, 20],
	] as const)("admits bounded %s headers", async (mimeType, bytes, width, height) => {
		const root = await mkdtemp(join(tmpdir(), "oga-attachments-"));
		roots.push(root);
		const reference = await new LocalGameImageAttachmentStore({ directory: root }).admit(mimeType, bytes);
		expect(reference).toMatchObject({ mimeType, width, height });
	});

	it("fails closed for partial or corrupted state", async () => {
		const root = await mkdtemp(join(tmpdir(), "oga-attachments-"));
		roots.push(root);
		const store = new LocalGameImageAttachmentStore({ directory: root });
		const reference = await store.admit("image/png", png());
		await writeFile(join(root, reference.id, "content.bin"), png(2, 2));
		await expect(store.read(reference.id)).rejects.toThrow(/metadata does not match|integrity validation/u);

		const partialId = `img_${"a".repeat(64)}`;
		await mkdir(join(root, partialId));
		await writeFile(join(root, partialId, "metadata.json"), "{}");
		await expect(store.read(partialId)).rejects.toThrow("incomplete");
	});

	it("rejects identifier paths redirected through symbolic links", async () => {
		const root = await mkdtemp(join(tmpdir(), "oga-attachments-"));
		const outside = await mkdtemp(join(tmpdir(), "oga-attachments-outside-"));
		roots.push(root, outside);
		const id = `img_${"b".repeat(64)}`;
		await writeFile(join(outside, "metadata.json"), "{}");
		await writeFile(join(outside, "content.bin"), png());
		try {
			await symlink(outside, join(root, id), "junction");
		} catch (error) {
			if (typeof error === "object" && error !== null && "code" in error && error.code === "EPERM") return;
			throw error;
		}
		await expect(new LocalGameImageAttachmentStore({ directory: root }).read(id)).rejects.toThrow(
			/symbolic links|reparse points/u,
		);
	});

	it("does not persist plaintext bytes in metadata", async () => {
		const root = await mkdtemp(join(tmpdir(), "oga-attachments-"));
		roots.push(root);
		const store = new LocalGameImageAttachmentStore({ directory: root });
		const reference = await store.admit("image/png", png());
		const metadata = await readFile(join(root, reference.id, "metadata.json"), "utf8");
		expect(metadata).not.toContain(Buffer.from(png()).toString("base64"));
	});
});

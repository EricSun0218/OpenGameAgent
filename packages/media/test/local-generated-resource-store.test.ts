import { createHash } from "node:crypto";
import { mkdtemp, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	type GeneratedResourceRef,
	LocalGeneratedResourceStore,
	MediaJobCapacityError,
	type MediaProviderResource,
	MediaResourceStoreError,
} from "../src/index.js";

const directories: string[] = [];

afterEach(async () => {
	await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe("LocalGeneratedResourceStore media Adapter", () => {
	it("collects through complete resource IDs while retaining content shared across media slots", async () => {
		const { store } = await openedStore();
		const first = await store.publish(providerResource(Uint8Array.of(1)), publishContext(1));
		const shared = await store.publish({ ...providerResource(Uint8Array.of(1)), ordinal: 2 }, publishContext(1));
		const garbage = await store.publish(providerResource(Uint8Array.of(2)), publishContext(1));
		const result = await store.collectGarbage({
			beforeModifiedAt: "9999-12-31T23:59:59.999Z",
			limit: 2,
			maxScanEntries: 16,
			retainResourceIds: [first.resourceId],
			retainBudget: { maxCount: 1, maxBytes: 64 },
			signal: new AbortController().signal,
		});
		expect(result).toMatchObject({ deletedObjects: 1, deletedTemporaryFiles: 0, deletedBytes: "1" });
		await expect(store.read(shared, readContext(1))).resolves.toEqual(Uint8Array.of(1));
		await expect(store.read(garbage, readContext(1))).rejects.toMatchObject({ code: "storage_missing" });
	});

	it("decorates one neutral content identity with independent media metadata", async () => {
		const { root, store } = await openedStore();
		const bytes = Uint8Array.from([1, 2, 3]);
		const first = await store.publish(providerResource(bytes), publishContext(bytes.byteLength));
		const second = await store.publish(
			{ ...providerResource(bytes), kind: "audio", mediaType: "audio/wav", ordinal: 3 },
			publishContext(bytes.byteLength),
		);

		expect(first).toEqual({
			resourceId: digest(bytes),
			kind: "image",
			mediaType: "image/png",
			byteLength: bytes.byteLength,
			sha256: digest(bytes),
			ordinal: 0,
		});
		expect(second).toMatchObject({
			resourceId: first.resourceId,
			sha256: first.sha256,
			kind: "audio",
			mediaType: "audio/wav",
			ordinal: 3,
		});
		expect(await storedObjects(root)).toHaveLength(1);
		await expect(store.read(second, readContext(bytes.byteLength))).resolves.toEqual(bytes);
	});

	it("preflights media metadata and lazy-reader length before opening the source", async () => {
		const { store } = await openedStore();
		const read = vi.fn(() => Uint8Array.from([1, 2, 3]));
		const resource = providerResource({ byteLength: 3, read });

		await expect(store.publish(resource, publishContext(2))).rejects.toBeInstanceOf(MediaJobCapacityError);
		await expect(
			store.publish(resource, {
				...publishContext(3),
				maxMediaTypeBytes: Buffer.byteLength(resource.mediaType) - 1,
			}),
		).rejects.toBeInstanceOf(MediaJobCapacityError);
		expect(read).not.toHaveBeenCalled();
	});

	it("rechecks the media-type byte budget before storage read", async () => {
		const { store } = await openedStore();
		const bytes = Uint8Array.from([4, 5, 6]);
		const reference = await store.publish(providerResource(bytes), publishContext(bytes.byteLength));

		await expect(
			store.read(reference, {
				...readContext(bytes.byteLength),
				maxMediaTypeBytes: Buffer.byteLength(reference.mediaType) - 1,
			}),
		).rejects.toBeInstanceOf(MediaJobCapacityError);
	});

	it("maps neutral invalid input and invalid references to the existing media errors", async () => {
		const { store } = await openedStore();
		const malformed = providerResource({ byteLength: 1, read: () => Uint8Array.from([]) });
		await expect(store.publish(malformed, publishContext(1))).rejects.toBeInstanceOf(MediaResourceStoreError);

		const invalid = {
			resourceId: "0".repeat(64),
			sha256: "1".repeat(64),
			kind: "image",
			mediaType: "image/png",
			byteLength: 0,
			ordinal: 0,
		} as GeneratedResourceRef;
		await expect(store.read(invalid, readContext(0))).rejects.toMatchObject({ code: "invalid_reference" });
	});
});

async function openedStore(): Promise<{ root: string; store: LocalGeneratedResourceStore }> {
	const root = await mkdtemp(join(tmpdir(), "oga-media-resources-"));
	directories.push(root);
	return { root, store: await LocalGeneratedResourceStore.open({ rootDirectory: root }) };
}

function providerResource(bytes: MediaProviderResource["bytes"]): MediaProviderResource {
	return { ordinal: 0, kind: "image", mediaType: "image/png", bytes };
}

function publishContext(maxBytes: number) {
	return { maxBytes, maxMediaTypeBytes: 64, signal: new AbortController().signal };
}

function readContext(maxBytes: number) {
	return { maxBytes, maxMediaTypeBytes: 64, signal: new AbortController().signal };
}

function digest(bytes: Uint8Array): string {
	return createHash("sha256").update(bytes).digest("hex");
}

async function storedObjects(root: string): Promise<string[]> {
	const files: string[] = [];
	for (const first of await readdir(join(root, "objects"))) {
		for (const second of await readdir(join(root, "objects", first))) {
			for (const file of await readdir(join(root, "objects", first, second))) files.push(file);
		}
	}
	return files;
}

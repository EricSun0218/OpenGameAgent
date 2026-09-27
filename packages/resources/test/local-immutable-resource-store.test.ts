import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import {
	access,
	type FileHandle,
	mkdir,
	mkdtemp,
	open,
	readdir,
	rename,
	rm,
	symlink,
	unlink,
	utimes,
	writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, parse } from "node:path";
import { Readable } from "node:stream";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	ImmutableResourceCapacityError,
	type ImmutableResourceInput,
	type ImmutableResourceRef,
	ImmutableResourceStoreError,
	LocalImmutableResourceStore,
} from "../src/index.js";

vi.mock("node:fs/promises", async (importOriginal) => {
	const actual = await importOriginal<typeof import("node:fs/promises")>();
	return { ...actual, open: vi.fn(actual.open) };
});

const directories: string[] = [];

afterEach(async () => {
	await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe("LocalImmutableResourceStore", () => {
	it("closes the real file handle when cancellation arrives during read open", async () => {
		const { store } = await openedStore();
		const reference = await store.publish(resourceInput(Uint8Array.of(1)), publishContext(1));
		const controller = new AbortController();
		const reason = new Error("cancel after file open");
		const actual = await vi.importActual<typeof import("node:fs/promises")>("node:fs/promises");
		let handle: FileHandle | undefined;
		vi.mocked(open).mockImplementationOnce(async (...parameters) => {
			handle = await actual.open(...parameters);
			controller.abort(reason);
			return handle;
		});
		try {
			await expect(store.read(reference, { maxBytes: 1, signal: controller.signal })).rejects.toBe(reason);
			expect(handle?.fd).toBe(-1);
		} finally {
			await handle?.close();
		}
	});

	it("collects only old unretained objects and crash temporaries within the explicit deletion limit", async () => {
		const { root, store } = await openedStore();
		const retained = await store.publish(resourceInput(Uint8Array.of(1)), publishContext(1));
		const unreferenced = await store.publish(resourceInput(Uint8Array.of(2)), publishContext(1));
		const fresh = await store.publish(resourceInput(Uint8Array.of(3)), publishContext(1));
		const atCutoff = await store.publish(resourceInput(Uint8Array.of(4)), publishContext(1));
		const old = new Date("2026-01-01T00:00:00.000Z");
		const temporary = join(root, "temporary", "00000000-0000-4000-8000-000000000000.tmp");
		await writeFile(temporary, "orphan");
		await writeFile(join(root, "temporary", "host-file"), "retain unknown content");
		for (const file of [objectPath(root, retained.sha256), objectPath(root, unreferenced.sha256), temporary]) {
			await utimes(file, old, old);
		}
		const boundary = new Date("2026-01-02T00:00:00.000Z");
		await utimes(objectPath(root, atCutoff.sha256), boundary, boundary);
		const request = {
			beforeModifiedAt: "2026-01-02T00:00:00.000Z",
			limit: 1,
			maxScanEntries: 32,
			retainSha256: [retained.sha256],
			retainBudget: { maxCount: 1, maxBytes: 64 },
			signal: new AbortController().signal,
		};
		const first = await store.collectGarbage(request);
		const second = await store.collectGarbage(request);
		expect(first.deletedObjects + first.deletedTemporaryFiles).toBe(1);
		expect(second.deletedObjects + second.deletedTemporaryFiles).toBe(1);
		expect(BigInt(first.deletedBytes) + BigInt(second.deletedBytes)).toBe(7n);
		expect(await store.collectGarbage(request)).toMatchObject({
			deletedObjects: 0,
			deletedTemporaryFiles: 0,
			deletedBytes: "0",
		});
		await expect(store.read(retained, readContext(1))).resolves.toEqual(Uint8Array.of(1));
		await expect(store.read(fresh, readContext(1))).resolves.toEqual(Uint8Array.of(3));
		await expect(store.read(atCutoff, readContext(1))).resolves.toEqual(Uint8Array.of(4));
		const reopened = await LocalImmutableResourceStore.open({ rootDirectory: root });
		await expect(reopened.read(retained, readContext(1))).resolves.toEqual(Uint8Array.of(1));
		await expect(store.read(unreferenced, readContext(1))).rejects.toMatchObject({ code: "storage_missing" });
		expect(await readdir(join(root, "temporary"))).toEqual(["host-file"]);
	});

	it("fails before deletion when a scan or retention budget is insufficient, then accepts a complete retry", async () => {
		const { root, store } = await openedStore();
		const reference = await store.publish(resourceInput(Uint8Array.of(7)), publishContext(1));
		const request = collectionRequest();
		await expect(store.collectGarbage({ ...request, maxScanEntries: 1 })).rejects.toMatchObject({ code: "scan" });
		await expect(store.read(reference, readContext(1))).resolves.toEqual(Uint8Array.of(7));
		await expect(store.collectGarbage({ ...request, retainSha256: [reference.sha256] })).rejects.toBeInstanceOf(
			RangeError,
		);
		await expect(
			store.collectGarbage({
				...request,
				retainSha256: [reference.sha256],
				retainBudget: { maxCount: 1, maxBytes: 63 },
			}),
		).rejects.toBeInstanceOf(RangeError);
		await expect(
			store.collectGarbage({ ...request, retainSha256: ["../objects"], retainBudget: { maxCount: 1, maxBytes: 10 } }),
		).rejects.toMatchObject({ code: "invalid_reference" });
		expect(await storedObjects(root)).toEqual([reference.sha256]);
		await expect(store.collectGarbage(request)).resolves.toMatchObject({ deletedObjects: 1 });
	});

	it("fences cleanup while a publication is active and fences new operations until cleanup finishes", async () => {
		const { store } = await openedStore();
		let release: ((bytes: Uint8Array) => void) | undefined;
		const reader = vi.fn(
			() =>
				new Promise<Uint8Array>((resolve) => {
					release = resolve;
				}),
		);
		const publication = store.publish(resourceInput({ byteLength: 1, read: reader }), publishContext(1));
		await vi.waitFor(() => expect(reader).toHaveBeenCalled());
		await expect(store.collectGarbage(collectionRequest())).rejects.toMatchObject({ code: "storage_busy" });
		release?.(Uint8Array.of(4));
		const reference = await publication;
		const collection = store.collectGarbage(collectionRequest());
		await expect(store.read(reference, readContext(1))).rejects.toMatchObject({ code: "storage_busy" });
		await expect(store.publish(resourceInput(Uint8Array.of(5)), publishContext(1))).rejects.toMatchObject({
			code: "storage_busy",
		});
		await expect(store.collectGarbage(collectionRequest())).rejects.toMatchObject({ code: "storage_busy" });
		await expect(collection).resolves.toMatchObject({ deletedObjects: 1 });
		await expect(store.publish(resourceInput(Uint8Array.of(5)), publishContext(1))).resolves.toHaveProperty("sha256");
	});

	it("rejects a linked shard without touching its target and preserves resources on abort", async () => {
		const { root, store } = await openedStore();
		const reference = await store.publish(resourceInput(Uint8Array.of(8)), publishContext(1));
		const controller = new AbortController();
		const reason = new Error("host cancelled cleanup");
		controller.abort(reason);
		await expect(store.collectGarbage({ ...collectionRequest(), signal: controller.signal })).rejects.toBe(reason);
		await expect(store.read(reference, readContext(1))).resolves.toEqual(Uint8Array.of(8));
		const original = join(root, "objects", reference.sha256.slice(0, 2));
		const moved = `${root}-shard`;
		directories.push(moved);
		await rename(original, moved);
		await symlink(moved, original, process.platform === "win32" ? "junction" : "dir");
		await expect(store.collectGarbage(collectionRequest())).rejects.toMatchObject({ code: "storage_changed" });
		expect(await readdir(join(moved, reference.sha256.slice(2, 4)))).toEqual([reference.sha256]);
	});

	it("publishes and verifies exact eager bytes including an empty resource", async () => {
		const { store } = await openedStore();
		for (const bytes of [new Uint8Array(), Uint8Array.from([0, 1, 2, 255])]) {
			const resource = resourceInput(bytes);
			const reference = await store.publish(resource, publishContext(32));

			expect(reference).toEqual({
				sha256: digest(bytes),
				byteLength: bytes.byteLength,
			});
			const read = await store.read(reference, readContext(32));
			expect(read).toEqual(bytes);
			expect(read.constructor).toBe(Uint8Array);
			expect(read.buffer.byteLength).toBe(read.byteLength);
		}
	});

	it("captures eager bytes before the first await", async () => {
		const { store } = await openedStore();
		const bytes = Uint8Array.from([1, 2, 3]);
		const publication = store.publish(resourceInput(bytes), publishContext(3));
		bytes[0] = 9;
		const reference = await publication;

		await expect(store.read(reference, readContext(3))).resolves.toEqual(Uint8Array.from([1, 2, 3]));
	});

	it.each(["eager", "reader", "stream"] as const)(
		"uses intrinsic typed-array length and bytes for a hostile %s source",
		async (kind) => {
			const { root, store } = await openedStore();
			const hostile = new MisleadingBytes([1, 2, 3]);
			const bytes: ImmutableResourceInput["bytes"] =
				kind === "eager" ? hostile : kind === "reader" ? { byteLength: 0, read: () => hostile } : stream(hostile);

			await expect(store.publish(resourceInput(bytes), publishContext(0))).rejects.toBeInstanceOf(
				ImmutableResourceCapacityError,
			);
			expect(await storedObjects(root)).toEqual([]);
			expect(await readdir(join(root, "temporary"))).toEqual([]);
		},
	);

	it.each(["eager", "reader", "stream"] as const)(
		"rejects a proxied typed array from a %s source without publishing bytes",
		async (kind) => {
			const { root, store } = await openedStore();
			const proxied = new Proxy(Uint8Array.from([1]), {});
			const bytes: ImmutableResourceInput["bytes"] =
				kind === "eager" ? proxied : kind === "reader" ? { byteLength: 1, read: () => proxied } : stream(proxied);

			await expect(store.publish(resourceInput(bytes), publishContext(1))).rejects.toMatchObject({
				code: "invalid_resource",
			});
			expect(await storedObjects(root)).toEqual([]);
			expect(await readdir(join(root, "temporary"))).toEqual([]);
		},
	);

	it("streams bounded chunks and verifies a declared byte count", async () => {
		const { store } = await openedStore();
		const bytes = Uint8Array.from([1, 2, 3, 4]);
		const resource: ImmutableResourceInput = {
			...resourceInput(stream(Uint8Array.from([1]), new Uint8Array(), Uint8Array.from([2, 3, 4]))),
			declaredBytes: bytes.byteLength,
		};

		const reference = await store.publish(resource, publishContext(bytes.byteLength));

		expect(reference.byteLength).toBe(bytes.byteLength);
		await expect(store.read(reference, readContext(bytes.byteLength))).resolves.toEqual(bytes);
	});

	it("treats a Node Readable as its advertised async byte stream even though it also has read()", async () => {
		const { store } = await openedStore();
		const expected = Uint8Array.from([1, 2, 3, 4]);
		const source = Readable.from([expected.subarray(0, 2), expected.subarray(2)]);

		const reference = await store.publish(resourceInput(source), publishContext(expected.byteLength));

		expect(reference).toEqual({ sha256: digest(expected), byteLength: expected.byteLength });
		await expect(store.read(reference, readContext(expected.byteLength))).resolves.toEqual(expected);
	});

	it("publishes a bounded lazy reader after capturing its exact method and receiver", async () => {
		const { store } = await openedStore();
		const expected = Uint8Array.from([5, 6, 7]);
		let receiver: unknown;
		const firstRead = vi.fn(function (this: unknown, signal: AbortSignal) {
			receiver = this;
			signal.throwIfAborted();
			return expected;
		});
		const source = {
			byteLength: expected.byteLength,
			read: firstRead,
		};
		const publication = store.publish(resourceInput(source), publishContext(expected.byteLength));
		source.read = vi.fn(() => Uint8Array.from([9, 9, 9]));

		const reference = await publication;

		expect(firstRead).toHaveBeenCalledOnce();
		expect(receiver).toBe(source);
		await expect(store.read(reference, readContext(expected.byteLength))).resolves.toEqual(expected);
	});

	it("preflights lazy-reader length before reading", async () => {
		const { store } = await openedStore();
		const read = vi.fn(() => Uint8Array.from([1, 2, 3]));
		const source = { byteLength: 3, read };

		await expect(store.publish(resourceInput(source), publishContext(2))).rejects.toBeInstanceOf(
			ImmutableResourceCapacityError,
		);
		expect(read).not.toHaveBeenCalled();
	});

	it("rejects a lazy reader whose returned length disagrees with its captured declaration", async () => {
		const { store } = await openedStore();
		const source = { byteLength: 2, read: () => Uint8Array.from([1]) };

		await expect(store.publish(resourceInput(source), publishContext(2))).rejects.toMatchObject({
			code: "invalid_resource",
		});
	});

	it("interrupts a stalled lazy reader with the exact caller reason", async () => {
		const { root, store } = await openedStore();
		const controller = new AbortController();
		const reason = new Error("stop stalled reader");
		let reached: (() => void) | undefined;
		const waiting = new Promise<void>((resolve) => {
			reached = resolve;
		});
		const source = {
			byteLength: 1,
			read: async () => {
				reached?.();
				return await new Promise<Uint8Array>(() => undefined);
			},
		};
		const publication = store.publish(resourceInput(source), { ...publishContext(1), signal: controller.signal });
		await waiting;
		controller.abort(reason);

		await expect(publication).rejects.toBe(reason);
		expect(await readdir(join(root, "temporary"))).toEqual([]);
	});

	it("publishes an empty async stream and captures its iterator exactly once", async () => {
		const { store } = await openedStore();
		let iteratorCalls = 0;
		const source: AsyncIterable<Uint8Array> = {
			[Symbol.asyncIterator]() {
				iteratorCalls += 1;
				return stream()[Symbol.asyncIterator]();
			},
		};

		const reference = await store.publish(resourceInput(source), publishContext(0));

		expect(iteratorCalls).toBe(1);
		expect(reference.sha256).toBe(digest(new Uint8Array()));
		await expect(store.read(reference, readContext(0))).resolves.toEqual(new Uint8Array());
	});

	it("does not inspect an iterator return getter after normal completion", async () => {
		const { store } = await openedStore();
		let returnReads = 0;
		const source: AsyncIterable<Uint8Array> = {
			[Symbol.asyncIterator]() {
				const iterator = {
					next: vi
						.fn()
						.mockResolvedValueOnce({ done: false, value: Uint8Array.of(1) })
						.mockResolvedValue({ done: true }),
				};
				Object.defineProperty(iterator, "return", {
					get() {
						returnReads += 1;
						throw new Error("cleanup must not be inspected");
					},
				});
				return iterator;
			},
		};

		const reference = await store.publish(resourceInput(source), publishContext(1));

		expect(returnReads).toBe(0);
		await expect(store.read(reference, readContext(1))).resolves.toEqual(Uint8Array.of(1));
	});

	it("reads an iterator return getter once only on failure and preserves the primary error", async () => {
		const { root, store } = await openedStore();
		let returnReads = 0;
		const source: AsyncIterable<Uint8Array> = {
			[Symbol.asyncIterator]() {
				const iterator = { next: async () => ({ done: false as const, value: Uint8Array.of(1, 2) }) };
				Object.defineProperty(iterator, "return", {
					get() {
						returnReads += 1;
						throw new Error("cleanup getter failed");
					},
				});
				return iterator;
			},
		};

		await expect(store.publish(resourceInput(source), publishContext(1))).rejects.toBeInstanceOf(
			ImmutableResourceCapacityError,
		);
		expect(returnReads).toBe(1);
		expect(await readdir(join(root, "temporary"))).toEqual([]);
	});

	it("deduplicates identical bytes across separate publications", async () => {
		const { root, store } = await openedStore();
		const bytes = Uint8Array.from([7, 8, 9]);
		const first = await store.publish(resourceInput(bytes), publishContext(bytes.byteLength));
		const second = await store.publish(resourceInput(bytes), publishContext(bytes.byteLength));

		expect(second).toEqual(first);
		expect(await storedObjects(root)).toHaveLength(1);
		await expect(store.read(second, readContext(bytes.byteLength))).resolves.toEqual(bytes);
	});

	it("deduplicates concurrent publication across reopened store instances", async () => {
		const { root, store } = await openedStore();
		const reopened = await LocalImmutableResourceStore.open({ rootDirectory: root });
		const bytes = Uint8Array.from([11, 12, 13]);

		const [first, second] = await Promise.all([
			store.publish(resourceInput(stream(bytes)), publishContext(bytes.byteLength)),
			reopened.publish(resourceInput(bytes), publishContext(bytes.byteLength)),
		]);

		expect(second.sha256).toBe(first.sha256);
		expect(await storedObjects(root)).toHaveLength(1);
		await expect(reopened.read(first, readContext(bytes.byteLength))).resolves.toEqual(bytes);
	});

	it("does not expose the final digest path before a stream is complete", async () => {
		const { root, store } = await openedStore();
		const completeBytes = Uint8Array.from([21, 22]);
		let reached: (() => void) | undefined;
		let release: (() => void) | undefined;
		const waiting = new Promise<void>((resolve) => {
			reached = resolve;
		});
		const gate = new Promise<void>((resolve) => {
			release = resolve;
		});
		async function* bytes(): AsyncIterable<Uint8Array> {
			yield completeBytes.subarray(0, 1);
			reached?.();
			await gate;
			yield completeBytes.subarray(1);
		}
		const publication = store.publish(resourceInput(bytes()), publishContext(completeBytes.byteLength));
		await waiting;

		await expect(access(objectPath(root, digest(completeBytes)))).rejects.toBeDefined();
		release?.();
		await publication;
		await expect(access(objectPath(root, digest(completeBytes)))).resolves.toBeUndefined();
	});

	it("rejects actual or declared overflow without leaving a temporary object", async () => {
		const { root, store } = await openedStore();
		await expect(
			store.publish(resourceInput(stream(Uint8Array.from([1, 2]), Uint8Array.from([3]))), publishContext(2)),
		).rejects.toBeInstanceOf(ImmutableResourceCapacityError);
		await expect(
			store.publish({ ...resourceInput(new Uint8Array()), declaredBytes: 1 }, publishContext(1)),
		).rejects.toMatchObject({ code: "invalid_resource" });
		await expect(
			store.publish({ ...resourceInput(stream(Uint8Array.from([1, 2]))), declaredBytes: 1 }, publishContext(2)),
		).rejects.toMatchObject({ code: "invalid_resource" });
		expect(await readdir(join(root, "temporary"))).toEqual([]);
	});

	it("closes a failed iterator and removes its temporary object", async () => {
		const { root, store } = await openedStore();
		let closed = false;
		async function* oversized(): AsyncIterable<Uint8Array> {
			try {
				yield Uint8Array.from([1, 2]);
			} finally {
				closed = true;
			}
		}

		await expect(store.publish(resourceInput(oversized()), publishContext(1))).rejects.toBeInstanceOf(
			ImmutableResourceCapacityError,
		);
		expect(closed).toBe(true);
		expect(await readdir(join(root, "temporary"))).toEqual([]);
	});

	it("maps iterator failure to invalid_resource and removes its temporary object", async () => {
		const { root, store } = await openedStore();
		async function* broken(): AsyncIterable<Uint8Array> {
			yield Uint8Array.from([1]);
			throw new Error("source failure");
		}

		await expect(store.publish(resourceInput(broken()), publishContext(2))).rejects.toMatchObject({
			code: "invalid_resource",
		});
		expect(await readdir(join(root, "temporary"))).toEqual([]);
	});

	it("preserves caller cancellation during streamed publication and cleans the temporary file", async () => {
		const { root, store } = await openedStore();
		const controller = new AbortController();
		const reason = new Error("stop");
		let release: (() => void) | undefined;
		let reached: (() => void) | undefined;
		const waiting = new Promise<void>((resolve) => {
			reached = resolve;
		});
		const gate = new Promise<void>((resolve) => {
			release = resolve;
		});
		async function* bytes(): AsyncIterable<Uint8Array> {
			yield Uint8Array.from([1]);
			reached?.();
			await gate;
			yield Uint8Array.from([2]);
		}
		const publication = store.publish(resourceInput(bytes()), {
			...publishContext(2),
			signal: controller.signal,
		});
		await waiting;
		controller.abort(reason);
		release?.();

		await expect(publication).rejects.toBe(reason);
		expect(await readdir(join(root, "temporary"))).toEqual([]);
	});

	it("interrupts a stalled async-iterator next with the exact caller reason", async () => {
		const { root, store } = await openedStore();
		const controller = new AbortController();
		const reason = new Error("stop stalled iterator");
		let reached: (() => void) | undefined;
		const waiting = new Promise<void>((resolve) => {
			reached = resolve;
		});
		const source: AsyncIterable<Uint8Array> = {
			[Symbol.asyncIterator]() {
				return {
					next: async () => {
						reached?.();
						return await new Promise<IteratorResult<Uint8Array>>(() => undefined);
					},
				};
			},
		};
		const publication = store.publish(resourceInput(source), {
			...publishContext(1),
			signal: controller.signal,
		});
		await waiting;
		controller.abort(reason);

		await expect(publication).rejects.toBe(reason);
		expect(await readdir(join(root, "temporary"))).toEqual([]);
	});

	it("preserves cancellation after physical publication and leaves only a verified inert object", async () => {
		const { root, store } = await openedStore();
		const bytes = Uint8Array.from([41, 42, 43]);
		const sha256 = digest(bytes);
		const finalPath = objectPath(root, sha256);
		const reason = new Error("cancel after publication");
		const signal = {
			throwIfAborted() {
				if (existsSync(finalPath)) throw reason;
			},
		} as AbortSignal;

		await expect(store.publish(resourceInput(bytes), { ...publishContext(bytes.byteLength), signal })).rejects.toBe(
			reason,
		);
		expect(existsSync(finalPath)).toBe(true);
		const reference: ImmutableResourceRef = {
			sha256,
			byteLength: bytes.byteLength,
		};
		await expect(store.read(reference, readContext(bytes.byteLength))).resolves.toEqual(bytes);
	});

	it("preserves a pre-aborted reason before validating budgets or references", async () => {
		const { store } = await openedStore();
		const controller = new AbortController();
		const reason = new Error("already stopped");
		controller.abort(reason);

		await expect(
			store.publish(resourceInput(new Uint8Array()), {
				...publishContext(-1),
				signal: controller.signal,
			}),
		).rejects.toBe(reason);
		await expect(store.read({} as ImmutableResourceRef, { maxBytes: -1, signal: controller.signal })).rejects.toBe(
			reason,
		);
	});

	it("rejects malformed stream chunks with a closed resource error", async () => {
		const { store } = await openedStore();
		async function* malformed(): AsyncIterable<Uint8Array> {
			yield "not bytes" as unknown as Uint8Array;
		}

		await expect(store.publish(resourceInput(malformed()), publishContext(32))).rejects.toMatchObject({
			code: "invalid_resource",
		});
	});

	it("rejects a non-boolean iterator completion flag and closes the iterator", async () => {
		const { store } = await openedStore();
		const close = vi.fn(async () => ({ done: true as const, value: undefined }));
		const malformed = {
			[Symbol.asyncIterator]() {
				return {
					next: async () => ({ done: "yes" as unknown as boolean, value: undefined }),
					return: close,
				};
			},
		} as unknown as AsyncIterable<Uint8Array>;

		await expect(store.publish(resourceInput(malformed), publishContext(0))).rejects.toMatchObject({
			code: "invalid_resource",
		});
		expect(close).toHaveBeenCalledOnce();
	});

	it("distinguishes missing and corrupt resources and enforces the read budget first", async () => {
		const { root, store } = await openedStore();
		const bytes = Uint8Array.from([4, 5, 6]);
		const reference = await store.publish(resourceInput(bytes), publishContext(bytes.byteLength));
		const path = objectPath(root, reference.sha256);

		await expect(store.read(reference, readContext(bytes.byteLength - 1))).rejects.toBeInstanceOf(
			ImmutableResourceCapacityError,
		);
		await unlink(path);
		await writeFile(path, Uint8Array.from([6, 5, 4]));
		await expect(store.read(reference, readContext(bytes.byteLength))).rejects.toMatchObject({
			code: "storage_corrupt",
		});
		await unlink(path);
		await expect(store.read(reference, readContext(bytes.byteLength))).rejects.toMatchObject({
			code: "storage_missing",
		});
	});

	it("fails closed when an existing digest object is corrupt instead of overwriting it", async () => {
		const { root, store } = await openedStore();
		const bytes = Uint8Array.from([31, 32, 33]);
		const reference = await store.publish(resourceInput(bytes), publishContext(bytes.byteLength));
		const path = objectPath(root, reference.sha256);
		const corrupt = Uint8Array.from([33, 32, 31]);
		await unlink(path);
		await writeFile(path, corrupt);

		await expect(store.publish(resourceInput(bytes), publishContext(bytes.byteLength))).rejects.toMatchObject({
			code: "storage_corrupt",
		});
		await expect(store.read(reference, readContext(bytes.byteLength))).rejects.toMatchObject({
			code: "storage_corrupt",
		});
	});

	it("detects a renamed and recreated storage root", async () => {
		const { root, store } = await openedStore();
		const moved = `${root}-moved`;
		directories.push(moved);
		const reference = await store.publish(resourceInput(new Uint8Array()), publishContext(0));
		await rename(root, moved);
		await mkdir(root);

		await expect(store.read(reference, readContext(0))).rejects.toMatchObject({ code: "storage_changed" });
		await expect(store.publish(resourceInput(new Uint8Array()), publishContext(0))).rejects.toMatchObject({
			code: "storage_changed",
		});
		await expect(store.collectGarbage(collectionRequest())).rejects.toMatchObject({ code: "storage_changed" });
	});

	it.each(["objects", "temporary"] as const)("detects a %s directory retargeted through a link", async (name) => {
		const { root, store } = await openedStore();
		const original = join(root, name);
		const moved = `${root}-${name}`;
		directories.push(moved);
		await rename(original, moved);
		await symlink(moved, original, process.platform === "win32" ? "junction" : "dir");

		await expect(store.publish(resourceInput(new Uint8Array()), publishContext(0))).rejects.toMatchObject({
			code: "storage_changed",
		});
		await expect(store.collectGarbage(collectionRequest())).rejects.toMatchObject({ code: "storage_changed" });
		expect(await readdir(moved)).toEqual([]);
	});

	it("rejects a malformed reference before filesystem access", async () => {
		const { store } = await openedStore();
		const invalid = {
			sha256: "x".repeat(64),
			byteLength: 0,
		} as ImmutableResourceRef;

		await expect(store.read(invalid, readContext(0))).rejects.toBeInstanceOf(ImmutableResourceStoreError);
		await expect(store.read(invalid, readContext(0))).rejects.toMatchObject({ code: "invalid_reference" });
	});

	it("requires a dedicated non-root storage directory and explicit nonnegative operation budgets", async () => {
		await expect(LocalImmutableResourceStore.open({ rootDirectory: "" })).rejects.toMatchObject({
			code: "invalid_configuration",
		});
		await expect(LocalImmutableResourceStore.open({ rootDirectory: parse(tmpdir()).root })).rejects.toMatchObject({
			code: "invalid_configuration",
		});
		const { store } = await openedStore();
		for (const maxBytes of [-1, 1.5, Number.POSITIVE_INFINITY]) {
			await expect(store.publish(resourceInput(new Uint8Array()), publishContext(maxBytes))).rejects.toBeInstanceOf(
				RangeError,
			);
		}
	});
});

async function openedStore(): Promise<{ root: string; store: LocalImmutableResourceStore }> {
	const root = await mkdtemp(join(tmpdir(), "oga-immutable-resources-"));
	directories.push(root);
	return { root, store: await LocalImmutableResourceStore.open({ rootDirectory: root }) };
}

function resourceInput(bytes: ImmutableResourceInput["bytes"]): ImmutableResourceInput {
	return { bytes };
}

function publishContext(maxBytes: number) {
	return {
		maxBytes,
		signal: new AbortController().signal,
	};
}

function readContext(maxBytes: number) {
	return { maxBytes, signal: new AbortController().signal };
}

function collectionRequest() {
	return {
		beforeModifiedAt: "9999-12-31T23:59:59.999Z",
		limit: 16,
		maxScanEntries: 64,
		retainSha256: [],
		retainBudget: { maxCount: 0, maxBytes: 0 },
		signal: new AbortController().signal,
	};
}

async function* stream(...chunks: Uint8Array[]): AsyncIterable<Uint8Array> {
	for (const chunk of chunks) yield chunk;
}

function digest(bytes: Uint8Array): string {
	return createHash("sha256").update(bytes).digest("hex");
}

function objectPath(root: string, sha256: string): string {
	return join(root, "objects", sha256.slice(0, 2), sha256.slice(2, 4), sha256);
}

async function storedObjects(root: string): Promise<readonly string[]> {
	const first = await readdir(join(root, "objects"));
	const objects: string[] = [];
	for (const firstBucket of first) {
		for (const secondBucket of await readdir(join(root, "objects", firstBucket))) {
			for (const object of await readdir(join(root, "objects", firstBucket, secondBucket))) {
				objects.push(object);
			}
		}
	}
	return objects;
}

class MisleadingBytes extends Uint8Array {
	override get byteLength(): number {
		return 0;
	}

	override get length(): number {
		return 3;
	}
}

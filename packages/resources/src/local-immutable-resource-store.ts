import { Buffer, constants as bufferConstants } from "node:buffer";
import { createHash, randomUUID } from "node:crypto";
import { constants as fsConstants, type Stats } from "node:fs";
import type { FileHandle } from "node:fs/promises";
import { chmod, link, lstat, mkdir, open, opendir, realpath, stat, unlink } from "node:fs/promises";
import { dirname, join, parse, resolve, sep } from "node:path";
import { assertCanonicalTimestamp, captureRetainedIdentifiers, raceOperationWithAbort } from "@opengameagent/protocol";
import { ImmutableResourceCapacityError, ImmutableResourceStoreError } from "./errors.js";
import type {
	ImmutableResourceByteReader,
	ImmutableResourceGarbageCollectionRequest,
	ImmutableResourceGarbageCollectionResult,
	ImmutableResourceInput,
	ImmutableResourceOperationContext,
	ImmutableResourceRef,
	ImmutableResourceStore,
} from "./types.js";

const SHA256 = /^[0-9a-f]{64}$/u;
const SHARD = /^[0-9a-f]{2}$/u;
const TEMPORARY = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\.tmp$/u;
const TYPED_ARRAY_PROTOTYPE = Object.getPrototypeOf(Uint8Array.prototype) as object;
const TYPED_ARRAY_BUFFER = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "buffer")?.get as (
	this: Uint8Array,
) => ArrayBufferLike;
const TYPED_ARRAY_BYTE_LENGTH = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "byteLength")?.get as (
	this: Uint8Array,
) => number;
const TYPED_ARRAY_BYTE_OFFSET = Object.getOwnPropertyDescriptor(TYPED_ARRAY_PROTOTYPE, "byteOffset")?.get as (
	this: Uint8Array,
) => number;
const TYPED_ARRAY_SET = Uint8Array.prototype.set;

export interface LocalImmutableResourceStoreOptions {
	readonly rootDirectory: string;
}

interface CapturedPublication {
	readonly declaredBytes?: number;
	readonly source:
		| { readonly kind: "eager"; readonly bytes: Buffer }
		| { readonly kind: "reader"; readonly reader: CapturedByteReader }
		| { readonly kind: "stream"; readonly iterator: CapturedAsyncIterator };
}

interface CapturedAsyncIterator {
	next(): Promise<IteratorResult<Uint8Array>>;
	close(): PromiseLike<unknown> | unknown;
}

interface CapturedByteReader {
	readonly byteLength: number;
	read(signal: AbortSignal): PromiseLike<Uint8Array> | Uint8Array;
}

interface IntrinsicByteView {
	readonly buffer: ArrayBufferLike;
	readonly byteLength: number;
	readonly byteOffset: number;
}

interface FileIdentity {
	readonly dev: number | bigint;
	readonly ino: number | bigint;
}

interface FileSnapshot extends FileIdentity {
	readonly size: number;
}

interface WrittenObject {
	readonly bytes: number;
	readonly sha256: string;
}

interface GarbageCandidate {
	readonly path: string;
	readonly kind: "object" | "temporary";
	readonly snapshot: Stats;
}

/**
 * Local immutable original-byte storage. The digest identifies bytes only; semantic metadata stays in each reference.
 */
export class LocalImmutableResourceStore implements ImmutableResourceStore {
	private activeOperations = 0;
	private collecting = false;
	private constructor(
		private readonly addressedRoot: string,
		private readonly canonicalRoot: string,
		private readonly objectsRoot: string,
		private readonly temporaryRoot: string,
		private readonly rootIdentity: FileIdentity,
		private readonly objectsIdentity: FileIdentity,
		private readonly temporaryIdentity: FileIdentity,
	) {}

	static async open(options: Readonly<LocalImmutableResourceStoreOptions>): Promise<LocalImmutableResourceStore> {
		const rootDirectory = options?.rootDirectory;
		if (typeof rootDirectory !== "string" || rootDirectory.length === 0 || rootDirectory.includes("\0")) {
			throw new ImmutableResourceStoreError("invalid_configuration");
		}
		const addressedRoot = resolve(rootDirectory);
		if (addressedRoot === parse(addressedRoot).root) {
			throw new ImmutableResourceStoreError("invalid_configuration");
		}
		try {
			await mkdir(addressedRoot, { recursive: true, mode: 0o700 });
			const canonicalRoot = resolve(await realpath(addressedRoot));
			if (canonicalRoot === parse(canonicalRoot).root) {
				throw new ImmutableResourceStoreError("invalid_configuration");
			}
			const objectsRoot = join(canonicalRoot, "objects");
			const temporaryRoot = join(canonicalRoot, "temporary");
			await createPrivateDirectory(objectsRoot, canonicalRoot);
			await createPrivateDirectory(temporaryRoot, canonicalRoot);
			if (process.platform !== "win32") {
				await chmod(canonicalRoot, 0o700);
				await chmod(objectsRoot, 0o700);
				await chmod(temporaryRoot, 0o700);
			}
			await syncDirectory(canonicalRoot);
			return new LocalImmutableResourceStore(
				addressedRoot,
				canonicalRoot,
				objectsRoot,
				temporaryRoot,
				await directoryIdentity(canonicalRoot),
				await directoryIdentity(objectsRoot),
				await directoryIdentity(temporaryRoot),
			);
		} catch (error) {
			if (error instanceof ImmutableResourceStoreError) throw error;
			throw new ImmutableResourceStoreError("storage_unavailable");
		}
	}

	async publish(
		resource: Readonly<ImmutableResourceInput>,
		context: Readonly<ImmutableResourceOperationContext>,
	): Promise<ImmutableResourceRef> {
		this.enterOperation();
		try {
			return await this.publishResource(resource, context);
		} finally {
			this.activeOperations--;
		}
	}

	private async publishResource(
		resource: Readonly<ImmutableResourceInput>,
		context: Readonly<ImmutableResourceOperationContext>,
	): Promise<ImmutableResourceRef> {
		const signal = operationSignal(context?.signal);
		signal.throwIfAborted();
		const maxBytes = operationByteLimit(context?.maxBytes);
		const captured = capturePublication(resource, maxBytes);
		await this.assertRootStable(signal);

		const temporaryPath = join(this.temporaryRoot, `${randomUUID()}.tmp`);
		let handle: FileHandle | undefined;
		try {
			handle = await open(temporaryPath, "wx", 0o600);
			const written = await writeObject(handle, captured, maxBytes, signal);
			signal.throwIfAborted();
			if (process.platform !== "win32") await handle.chmod(0o400);
			await handle.sync();
			signal.throwIfAborted();
			await handle.close();
			handle = undefined;
			await this.assertRootStable(signal);

			const reference = Object.freeze({
				sha256: written.sha256,
				byteLength: written.bytes,
			});
			const objectPath = await this.prepareObjectPath(written.sha256);
			const bucket = dirname(objectPath);
			signal.throwIfAborted();
			let alreadyExists = false;
			try {
				await link(temporaryPath, objectPath);
			} catch (error) {
				if (!isNodeError(error, "EEXIST")) throw error;
				alreadyExists = true;
			}
			if (alreadyExists) await this.read(reference, { maxBytes, signal });
			await syncDirectory(bucket);
			signal.throwIfAborted();
			await this.assertRootStable(signal);
			return reference;
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof ImmutableResourceCapacityError || error instanceof ImmutableResourceStoreError) throw error;
			throw new ImmutableResourceStoreError("storage_unavailable");
		} finally {
			await handle?.close().catch(() => undefined);
			await unlink(temporaryPath).catch(() => undefined);
		}
	}

	async read(
		reference: Readonly<ImmutableResourceRef>,
		context: Readonly<ImmutableResourceOperationContext>,
	): Promise<Uint8Array> {
		this.enterOperation();
		try {
			return await this.readResource(reference, context);
		} finally {
			this.activeOperations--;
		}
	}

	private async readResource(
		reference: Readonly<ImmutableResourceRef>,
		context: Readonly<ImmutableResourceOperationContext>,
	): Promise<Uint8Array> {
		const signal = operationSignal(context?.signal);
		signal.throwIfAborted();
		const maxBytes = operationByteLimit(context?.maxBytes);
		const captured = captureReference(reference);
		if (captured.byteLength > maxBytes || captured.byteLength > bufferConstants.MAX_LENGTH) {
			throw new ImmutableResourceCapacityError("resource");
		}
		await this.assertRootStable(signal);
		const filePath = this.objectPath(captured.sha256);
		let canonicalPath: string;
		try {
			canonicalPath = resolve(await realpath(filePath));
			signal.throwIfAborted();
		} catch (error) {
			signal.throwIfAborted();
			if (isNodeError(error, "ENOENT")) throw new ImmutableResourceStoreError("storage_missing");
			if (isChangedPathError(error)) throw new ImmutableResourceStoreError("storage_changed");
			throw new ImmutableResourceStoreError("storage_unavailable");
		}
		if (canonicalPath !== filePath) throw new ImmutableResourceStoreError("storage_changed");

		let handle: FileHandle;
		try {
			handle = await open(filePath, fsConstants.O_RDONLY | (fsConstants.O_NOFOLLOW ?? 0));
		} catch (error) {
			signal.throwIfAborted();
			if (isNodeError(error, "ENOENT") || isChangedPathError(error)) {
				throw new ImmutableResourceStoreError("storage_changed");
			}
			throw new ImmutableResourceStoreError("storage_unavailable");
		}
		try {
			signal.throwIfAborted();
			const before = await handle.stat();
			signal.throwIfAborted();
			if (!before.isFile()) throw new ImmutableResourceStoreError("storage_changed");
			if (before.size !== captured.byteLength || before.size > maxBytes) {
				throw new ImmutableResourceStoreError("storage_corrupt");
			}
			const bytes = new Uint8Array(before.size);
			let offset = 0;
			while (offset < bytes.byteLength) {
				signal.throwIfAborted();
				const result = await handle.read(bytes, offset, bytes.byteLength - offset, offset);
				if (result.bytesRead === 0) throw new ImmutableResourceStoreError("storage_changed");
				offset += result.bytesRead;
			}
			const extra = Buffer.allocUnsafe(1);
			if ((await handle.read(extra, 0, 1, offset)).bytesRead !== 0) {
				throw new ImmutableResourceStoreError("storage_changed");
			}
			const after = await handle.stat();
			let addressedAfter: FileSnapshot;
			try {
				addressedAfter = await stat(filePath);
			} catch (error) {
				if (isNodeError(error, "ENOENT") || isChangedPathError(error)) {
					throw new ImmutableResourceStoreError("storage_changed");
				}
				throw error;
			}
			signal.throwIfAborted();
			if (!sameFile(before, after) || !sameFile(after, addressedAfter)) {
				throw new ImmutableResourceStoreError("storage_changed");
			}
			if (createHash("sha256").update(bytes).digest("hex") !== captured.sha256) {
				throw new ImmutableResourceStoreError("storage_corrupt");
			}
			signal.throwIfAborted();
			await this.assertRootStable(signal);
			return bytes;
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof ImmutableResourceStoreError) throw error;
			throw new ImmutableResourceStoreError("storage_unavailable");
		} finally {
			await handle.close().catch(() => undefined);
		}
	}

	async collectGarbage(
		request: Readonly<ImmutableResourceGarbageCollectionRequest>,
	): Promise<ImmutableResourceGarbageCollectionResult> {
		const signal = operationSignal(request?.signal);
		signal.throwIfAborted();
		const beforeModifiedAt = request.beforeModifiedAt;
		assertCanonicalTimestamp(beforeModifiedAt);
		const cutoff = Date.parse(beforeModifiedAt);
		const limit = positiveLimit(request.limit, "limit");
		const maxScanEntries = positiveLimit(request.maxScanEntries, "maxScanEntries");
		const references = captureRetainedIdentifiers(request.retainSha256, request.retainBudget, "retainSha256");
		for (const sha256 of references) {
			if (!SHA256.test(sha256)) throw new ImmutableResourceStoreError("invalid_reference");
		}
		const retained = new Set(references);
		if (this.collecting || this.activeOperations > 0) throw new ImmutableResourceStoreError("storage_busy");
		this.collecting = true;
		try {
			await this.assertRootStable(signal);
			const candidates: GarbageCandidate[] = [];
			let scannedEntries = 0;
			const visit = async (directory: string, prefix: string | undefined): Promise<void> => {
				await assertExactDirectory(directory, this.canonicalRoot);
				// One entry is buffered at a time; the two shard levels come from the content-address layout.
				const entries = await opendir(directory, { bufferSize: 1 });
				for await (const entry of entries) {
					signal.throwIfAborted();
					if (++scannedEntries > maxScanEntries) throw new ImmutableResourceCapacityError("scan");
					const path = join(directory, entry.name);
					if (prefix !== undefined && prefix.length < 4) {
						if (SHARD.test(entry.name)) await visit(path, prefix + entry.name);
						continue;
					}
					const kind = prefix === undefined ? "temporary" : "object";
					if (
						kind === "temporary"
							? !TEMPORARY.test(entry.name)
							: !SHA256.test(entry.name) || !entry.name.startsWith(prefix as string)
					)
						continue;
					if (kind === "object" && retained.has(entry.name)) continue;
					const snapshot = await exactFileSnapshot(path);
					if (snapshot.mtimeMs >= cutoff || candidates.length === limit) continue;
					candidates.push({ path, kind, snapshot });
				}
			};
			await visit(this.objectsRoot, "");
			await visit(this.temporaryRoot, undefined);
			let deletedObjects = 0;
			let deletedTemporaryFiles = 0;
			let deletedBytes = 0n;
			for (const candidate of candidates) {
				await this.assertRootStable(signal);
				await assertExactDirectory(dirname(candidate.path), this.canonicalRoot);
				const current = await exactFileSnapshot(candidate.path);
				if (!sameFile(current, candidate.snapshot) || current.mtimeMs !== candidate.snapshot.mtimeMs) {
					throw new ImmutableResourceStoreError("storage_changed");
				}
				signal.throwIfAborted();
				await unlink(candidate.path);
				if (candidate.kind === "object") deletedObjects++;
				else deletedTemporaryFiles++;
				deletedBytes += BigInt(current.size);
				await syncDirectory(dirname(candidate.path));
			}
			await this.assertRootStable(signal);
			return { scannedEntries, deletedObjects, deletedTemporaryFiles, deletedBytes: deletedBytes.toString() };
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof ImmutableResourceStoreError || error instanceof ImmutableResourceCapacityError) throw error;
			throw new ImmutableResourceStoreError("storage_unavailable");
		} finally {
			this.collecting = false;
		}
	}

	private enterOperation(): void {
		if (this.collecting) throw new ImmutableResourceStoreError("storage_busy");
		this.activeOperations++;
	}

	private async prepareObjectPath(sha256: string): Promise<string> {
		const first = join(this.objectsRoot, sha256.slice(0, 2));
		const second = join(first, sha256.slice(2, 4));
		await createPrivateDirectory(first, this.canonicalRoot);
		await createPrivateDirectory(second, this.canonicalRoot);
		return join(second, sha256);
	}

	private objectPath(sha256: string): string {
		return join(this.objectsRoot, sha256.slice(0, 2), sha256.slice(2, 4), sha256);
	}

	private async assertRootStable(signal: AbortSignal): Promise<void> {
		signal.throwIfAborted();
		try {
			const current = resolve(await realpath(this.addressedRoot));
			signal.throwIfAborted();
			const [rootIdentity, objectsIdentity, temporaryIdentity] = await Promise.all([
				directoryIdentity(current),
				exactDirectoryIdentity(this.objectsRoot, this.canonicalRoot),
				exactDirectoryIdentity(this.temporaryRoot, this.canonicalRoot),
			]);
			signal.throwIfAborted();
			if (
				current !== this.canonicalRoot ||
				!sameIdentity(rootIdentity, this.rootIdentity) ||
				!sameIdentity(objectsIdentity, this.objectsIdentity) ||
				!sameIdentity(temporaryIdentity, this.temporaryIdentity)
			) {
				throw new ImmutableResourceStoreError("storage_changed");
			}
		} catch (error) {
			signal.throwIfAborted();
			if (error instanceof ImmutableResourceStoreError) throw error;
			throw new ImmutableResourceStoreError("storage_changed");
		}
	}
}

function capturePublication(resource: Readonly<ImmutableResourceInput>, maxBytes: number): CapturedPublication {
	try {
		if (!resource || typeof resource !== "object") throw new TypeError();
		const declaredBytes = resource.declaredBytes;
		const bytes = resource.bytes;
		if (declaredBytes !== undefined && (!Number.isSafeInteger(declaredBytes) || declaredBytes < 0)) {
			throw new TypeError();
		}
		if (declaredBytes !== undefined && declaredBytes > maxBytes) throw new ImmutableResourceCapacityError("resource");
		let source: CapturedPublication["source"];
		let capturedDeclaredBytes = declaredBytes;
		if (bytes instanceof Uint8Array) {
			const view = inspectBytes(bytes);
			if (view.byteLength > maxBytes) throw new ImmutableResourceCapacityError("resource");
			if (declaredBytes !== undefined && declaredBytes !== view.byteLength) throw new TypeError();
			source = { kind: "eager", bytes: snapshotBytes(view) };
		} else {
			const iterator = captureAsyncIterator(bytes);
			if (iterator !== undefined) {
				source = { kind: "stream", iterator };
			} else {
				const reader = captureByteReader(bytes);
				if (reader === undefined) throw new TypeError();
				if (reader.byteLength > maxBytes) throw new ImmutableResourceCapacityError("resource");
				if (declaredBytes !== undefined && declaredBytes !== reader.byteLength) throw new TypeError();
				capturedDeclaredBytes = reader.byteLength;
				source = { kind: "reader", reader };
			}
		}
		return {
			...(capturedDeclaredBytes === undefined ? {} : { declaredBytes: capturedDeclaredBytes }),
			source,
		};
	} catch (error) {
		if (error instanceof ImmutableResourceCapacityError) throw error;
		throw new ImmutableResourceStoreError("invalid_resource");
	}
}

function captureByteReader(source: unknown): CapturedByteReader | undefined {
	if (source === null || (typeof source !== "object" && typeof source !== "function")) return undefined;
	const read = (source as Partial<ImmutableResourceByteReader>).read;
	if (typeof read !== "function") return undefined;
	const byteLength = (source as Partial<ImmutableResourceByteReader>).byteLength;
	if (!Number.isSafeInteger(byteLength) || (byteLength as number) < 0) throw new TypeError();
	return {
		byteLength: byteLength as number,
		read: (signal) => read.call(source as ImmutableResourceByteReader, signal),
	};
}

function captureAsyncIterator(source: unknown): CapturedAsyncIterator | undefined {
	if (source === null || (typeof source !== "object" && typeof source !== "function")) return undefined;
	const method = (source as AsyncIterable<Uint8Array>)[Symbol.asyncIterator];
	if (typeof method !== "function") return undefined;
	const iterator = method.call(source);
	if (!iterator || typeof iterator !== "object") throw new TypeError();
	const next = iterator.next;
	if (typeof next !== "function") throw new TypeError();
	return {
		next: async () => await next.call(iterator),
		close: () => {
			const close = iterator.return;
			return typeof close === "function" ? close.call(iterator) : undefined;
		},
	};
}

function captureReference(reference: Readonly<ImmutableResourceRef>): ImmutableResourceRef {
	try {
		if (!reference || typeof reference !== "object") throw new TypeError();
		const byteLength = reference.byteLength;
		const sha256 = reference.sha256;
		if (typeof sha256 !== "string" || !SHA256.test(sha256) || !Number.isSafeInteger(byteLength) || byteLength < 0) {
			throw new TypeError();
		}
		return { byteLength, sha256 };
	} catch {
		throw new ImmutableResourceStoreError("invalid_reference");
	}
}

async function writeObject(
	handle: FileHandle,
	publication: Readonly<CapturedPublication>,
	maxBytes: number,
	signal: AbortSignal,
): Promise<WrittenObject> {
	const hash = createHash("sha256");
	let total = 0;
	const source = publication.source;
	if (source.kind === "eager") {
		total = await writeChunk(handle, source.bytes, total, hash, signal);
	} else if (source.kind === "reader") {
		let view: IntrinsicByteView;
		try {
			const returned = await raceOperationWithAbort(() => source.reader.read(signal), signal);
			view = inspectBytes(returned);
		} catch {
			signal.throwIfAborted();
			throw new ImmutableResourceStoreError("invalid_resource");
		}
		signal.throwIfAborted();
		if (view.byteLength > maxBytes) throw new ImmutableResourceCapacityError("resource");
		if (view.byteLength !== source.reader.byteLength) {
			throw new ImmutableResourceStoreError("invalid_resource");
		}
		let bytes: Buffer;
		try {
			bytes = snapshotBytes(view);
		} catch {
			throw new ImmutableResourceStoreError("invalid_resource");
		}
		total = await writeChunk(handle, bytes, total, hash, signal);
	} else {
		let complete = false;
		try {
			while (true) {
				signal.throwIfAborted();
				let result: IteratorResult<Uint8Array>;
				try {
					result = await raceOperationWithAbort(() => source.iterator.next(), signal);
				} catch {
					signal.throwIfAborted();
					throw new ImmutableResourceStoreError("invalid_resource");
				}
				signal.throwIfAborted();
				if (!result || typeof result !== "object") throw new ImmutableResourceStoreError("invalid_resource");
				let done: unknown;
				let value: unknown;
				try {
					done = (result as { readonly done?: unknown }).done;
					if (done !== undefined && typeof done !== "boolean") {
						throw new TypeError();
					}
					if (done !== true) value = (result as { readonly value?: unknown }).value;
				} catch {
					throw new ImmutableResourceStoreError("invalid_resource");
				}
				if (done === true) {
					complete = true;
					break;
				}
				let view: IntrinsicByteView;
				try {
					view = inspectBytes(value);
				} catch {
					throw new ImmutableResourceStoreError("invalid_resource");
				}
				if (view.byteLength > maxBytes - total) throw new ImmutableResourceCapacityError("resource");
				if (publication.declaredBytes !== undefined && view.byteLength > publication.declaredBytes - total) {
					throw new ImmutableResourceStoreError("invalid_resource");
				}
				let chunk: Buffer;
				try {
					chunk = snapshotBytes(view);
				} catch {
					throw new ImmutableResourceStoreError("invalid_resource");
				}
				total = await writeChunk(handle, chunk, total, hash, signal);
			}
		} finally {
			if (!complete) closeIterator(source.iterator);
		}
	}
	signal.throwIfAborted();
	if (publication.declaredBytes !== undefined && publication.declaredBytes !== total) {
		throw new ImmutableResourceStoreError("invalid_resource");
	}
	return { bytes: total, sha256: hash.digest("hex") };
}

async function writeChunk(
	handle: FileHandle,
	chunk: Buffer,
	total: number,
	hash: ReturnType<typeof createHash>,
	signal: AbortSignal,
): Promise<number> {
	let offset = 0;
	while (offset < chunk.byteLength) {
		signal.throwIfAborted();
		const result = await handle.write(chunk, offset, chunk.byteLength - offset, total + offset);
		if (result.bytesWritten === 0) throw new ImmutableResourceStoreError("storage_unavailable");
		offset += result.bytesWritten;
	}
	hash.update(chunk);
	return total + chunk.byteLength;
}

function operationByteLimit(value: unknown): number {
	if (!Number.isSafeInteger(value) || (value as number) < 0) {
		throw new RangeError("maxBytes must be a nonnegative safe integer.");
	}
	return value as number;
}

function positiveLimit(value: unknown, name: string): number {
	if (!Number.isSafeInteger(value) || (value as number) < 1)
		throw new RangeError(`${name} must be a positive safe integer.`);
	return value as number;
}

async function exactFileSnapshot(path: string): Promise<Stats> {
	const [canonical, snapshot] = await Promise.all([realpath(path), lstat(path)]);
	if (canonical !== path || !snapshot.isFile() || snapshot.isSymbolicLink() || !Number.isSafeInteger(snapshot.size)) {
		throw new ImmutableResourceStoreError("storage_changed");
	}
	return snapshot;
}

function closeIterator(iterator: CapturedAsyncIterator): void {
	try {
		const pending = iterator.close();
		if (pending !== undefined) void Promise.resolve(pending).catch(() => undefined);
	} catch {
		// Iterator closure is best-effort; preserve the primary failure or abort reason.
	}
}

function inspectBytes(value: unknown): IntrinsicByteView {
	if (!(value instanceof Uint8Array)) throw new TypeError();
	return {
		buffer: TYPED_ARRAY_BUFFER.call(value),
		byteLength: TYPED_ARRAY_BYTE_LENGTH.call(value),
		byteOffset: TYPED_ARRAY_BYTE_OFFSET.call(value),
	};
}

function snapshotBytes(view: Readonly<IntrinsicByteView>): Buffer {
	const snapshot = new Uint8Array(view.byteLength);
	TYPED_ARRAY_SET.call(snapshot, new Uint8Array(view.buffer, view.byteOffset, view.byteLength));
	return Buffer.from(snapshot.buffer, 0, view.byteLength);
}

function operationSignal(value: unknown): AbortSignal {
	if (!value || typeof value !== "object" || typeof (value as AbortSignal).throwIfAborted !== "function") {
		throw new TypeError("signal must be an AbortSignal.");
	}
	return value as AbortSignal;
}

async function createPrivateDirectory(path: string, root: string): Promise<void> {
	try {
		await mkdir(path, { mode: 0o700 });
	} catch (error) {
		if (!isNodeError(error, "EEXIST")) throw error;
	}
	await assertExactDirectory(path, root);
	if (process.platform !== "win32") await chmod(path, 0o700);
	await syncDirectory(dirname(path));
}

async function assertExactDirectory(path: string, root: string): Promise<void> {
	await exactDirectoryIdentity(path, root);
}

async function exactDirectoryIdentity(path: string, root: string): Promise<FileIdentity> {
	const [canonical, addressed, followed] = await Promise.all([realpath(path), lstat(path), stat(path)]);
	if (
		!addressed.isDirectory() ||
		addressed.isSymbolicLink() ||
		!followed.isDirectory() ||
		canonical !== resolve(path) ||
		!withinRoot(canonical, root) ||
		!sameIdentity(addressed, followed)
	) {
		throw new ImmutableResourceStoreError("storage_changed");
	}
	return { dev: followed.dev, ino: followed.ino };
}

function withinRoot(candidate: string, root: string): boolean {
	return candidate.startsWith(`${root}${sep}`);
}

function sameFile(left: FileSnapshot, right: FileSnapshot): boolean {
	return sameIdentity(left, right) && left.size === right.size;
}

function sameIdentity(left: FileIdentity, right: FileIdentity): boolean {
	return left.dev === right.dev && left.ino === right.ino;
}

async function directoryIdentity(path: string): Promise<FileIdentity> {
	const info = await stat(path);
	if (!info.isDirectory()) throw new ImmutableResourceStoreError("storage_changed");
	return { dev: info.dev, ino: info.ino };
}

async function syncDirectory(path: string): Promise<void> {
	// Pure Node cannot request directory-entry fsync on Windows; the file flush was requested before atomic publication.
	if (process.platform === "win32") return;
	const handle = await open(path, "r");
	try {
		await handle.sync();
	} finally {
		await handle.close();
	}
}

function isChangedPathError(error: unknown): boolean {
	return isNodeError(error, "ELOOP") || isNodeError(error, "ENOTDIR");
}

function isNodeError(error: unknown, code: string): boolean {
	return error instanceof Error && "code" in error && error.code === code;
}

import { constants as bufferConstants } from "node:buffer";
import { createHash, randomBytes, randomUUID } from "node:crypto";
import { type BigIntStats, constants as fsConstants } from "node:fs";
import type { FileHandle } from "node:fs/promises";
import { lstat, open, opendir, realpath, rename, stat, unlink } from "node:fs/promises";
import { basename, dirname, join, parse, resolve } from "node:path";
import { performance } from "node:perf_hooks";
import type { AuthOperationOptions, Credential, CredentialInfo, CredentialStore } from "@earendil-works/pi-ai";
import { JsonValueLimitError, normalizeJsonValueWithin } from "@opengameagent/protocol";
import { Dpapi, isPlatformSupported } from "@primno/dpapi";
import { flockSync } from "fs-ext-extra-prebuilt";
import koffi from "koffi";

const STORE_VERSION = 1;
const PAYLOAD_VERSION = 1;
const ENTROPY_BYTES = 32;
const STORE_JSON_DEPTH = 3;
const MAX_TIMER_DELAY_MS = 2_147_483_647;
const INVALID_FILE_ATTRIBUTES = 0xffff_ffff;
const FILE_ATTRIBUTE_REPARSE_POINT = 0x0000_0400;
const HASH_PATTERN = /^[0-9a-f]{64}$/u;
const BASE64_PATTERN = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u;

type GetFileAttributes = (path: string) => number;

let getFileAttributes: GetFileAttributes | undefined;

interface FileIdentity {
	readonly dev: bigint;
	readonly ino: bigint;
}

interface PersistedEntry {
	readonly entropy: string;
	readonly ciphertext: string;
}

interface PersistedStore {
	readonly version: 1;
	readonly entries: Readonly<Record<string, PersistedEntry>>;
}

interface PersistedCredentialPayload {
	readonly version: 1;
	readonly providerId: string;
	readonly credential: Credential;
}

interface CapturedCredential {
	readonly credential: Credential;
	readonly payloadBytes: Buffer;
}

interface LoadedStoreSnapshot {
	readonly entries: Map<string, PersistedEntry>;
	readonly identity: FileIdentity | undefined;
}

interface LockHandleCandidate {
	readonly handle: FileHandle;
	readonly identity: FileIdentity;
}

interface LockWaitBudget {
	readonly started: number;
	readonly waitMs: number;
	readonly retryDelayMs: number;
	readonly signal: AbortSignal | undefined;
}

export interface WindowsCurrentUserCredentialStoreLimits {
	readonly maxEntries: number;
	readonly maxCredentialBytes: number;
	readonly maxStoreBytes: number;
	readonly maxCredentialJsonDepth: number;
	readonly maxDirectoryEntriesToScan: number;
	readonly lockWaitMs: number;
	readonly lockRetryDelayMs: number;
}

export interface OpenWindowsCurrentUserCredentialStoreOptions {
	/** Existing application-data file path. The parent directory must not resolve through a reparse point. */
	readonly filePath: string;
	/** Required host budgets; this package does not select product capacities or lock timing. */
	readonly limits: Readonly<WindowsCurrentUserCredentialStoreLimits>;
}

export type WindowsCurrentUserCredentialStoreErrorCode =
	| "unsupported_platform"
	| "invalid_configuration"
	| "invalid_credential"
	| "capacity"
	| "locked"
	| "corrupt"
	| "storage_changed"
	| "storage_unavailable";

const ERROR_MESSAGES: Readonly<Record<WindowsCurrentUserCredentialStoreErrorCode, string>> = Object.freeze({
	unsupported_platform: "Windows CurrentUser credential storage is unavailable.",
	invalid_configuration: "Credential store configuration is invalid.",
	invalid_credential: "Credential update is invalid.",
	capacity: "Credential store capacity was exceeded.",
	locked: "Credential store lock wait was exhausted.",
	corrupt: "Credential storage is corrupt or inaccessible to this Windows user.",
	storage_changed: "Credential storage path changed during the operation.",
	storage_unavailable: "Credential storage operation failed.",
});

export class WindowsCurrentUserCredentialStoreError extends Error {
	constructor(readonly code: WindowsCurrentUserCredentialStoreErrorCode) {
		super(ERROR_MESSAGES[code]);
		this.name = "WindowsCurrentUserCredentialStoreError";
	}
}

class LockInitializationRace extends Error {}

export async function openWindowsCurrentUserCredentialStore(
	options: Readonly<OpenWindowsCurrentUserCredentialStoreOptions>,
): Promise<CredentialStore> {
	if (process.platform !== "win32" || !isPlatformSupported) {
		throw new WindowsCurrentUserCredentialStoreError("unsupported_platform");
	}
	const captured = captureOptions(options);
	const addressedRoot = dirname(captured.filePath);
	try {
		const canonicalRoot = resolve(await realpath(addressedRoot));
		if (canonicalRoot !== addressedRoot) throw new WindowsCurrentUserCredentialStoreError("invalid_configuration");
		const rootIdentity = await exactDirectoryIdentity(canonicalRoot, "invalid_configuration");
		await assertOptionalExactFile(captured.filePath, "invalid_configuration");
		await assertOptionalExactFile(`${captured.filePath}.lock`, "invalid_configuration");
		return new WindowsCurrentUserCredentialStore(captured.filePath, canonicalRoot, rootIdentity, captured.limits);
	} catch (error) {
		if (error instanceof WindowsCurrentUserCredentialStoreError) throw error;
		throw new WindowsCurrentUserCredentialStoreError("invalid_configuration");
	}
}

class WindowsCurrentUserCredentialStore implements CredentialStore {
	private readonly lockPath: string;
	private readonly temporaryNamePattern: RegExp;

	constructor(
		private readonly filePath: string,
		private readonly rootPath: string,
		private readonly rootIdentity: FileIdentity,
		private readonly limits: Readonly<WindowsCurrentUserCredentialStoreLimits>,
	) {
		this.lockPath = `${filePath}.lock`;
		this.temporaryNamePattern = new RegExp(
			`^\\.${escapeRegExp(basename(filePath))}\\.[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\.tmp$`,
			"u",
		);
	}

	read(providerId: string, options?: AuthOperationOptions): Promise<Credential | undefined> {
		return this.withLock(options, async (signal) => {
			const { entries } = await this.loadEntries(signal);
			const entry = entries.get(providerStorageKey(providerId));
			return entry === undefined ? undefined : this.unprotectEntry(providerId, entry);
		});
	}

	list(options?: AuthOperationOptions): Promise<readonly CredentialInfo[]> {
		return this.withLock(options, async (signal) => {
			const { entries } = await this.loadEntries(signal);
			const listed: CredentialInfo[] = [];
			for (const [key, entry] of entries) {
				signal?.throwIfAborted();
				const payload = this.unprotectPayload(key, entry);
				listed.push({ providerId: payload.providerId, type: payload.credential.type });
			}
			listed.sort((left, right) => compareText(left.providerId, right.providerId));
			return listed;
		});
	}

	modify(
		providerId: string,
		fn: (current: Credential | undefined) => Promise<Credential | undefined>,
		options?: AuthOperationOptions,
	): Promise<Credential | undefined> {
		return this.withLock(options, async (signal, lockHandle) => {
			const snapshot = await this.loadEntries(signal);
			const { entries } = snapshot;
			const key = providerStorageKey(providerId);
			const currentEntry = entries.get(key);
			const current = currentEntry === undefined ? undefined : this.unprotectEntry(providerId, currentEntry);
			const next = await fn(current);
			signal?.throwIfAborted();
			await assertExactOpenFile(this.lockPath, lockHandle);
			await assertOptionalFileUnchanged(this.filePath, snapshot.identity);
			if (next === undefined) return current;
			if (currentEntry === undefined && entries.size >= this.limits.maxEntries) {
				throw new WindowsCurrentUserCredentialStoreError("capacity");
			}
			const captured = captureCredential(providerId, next, this.limits);
			entries.set(key, protectPayload(captured.payloadBytes));
			await this.writeEntries(entries, snapshot.identity, lockHandle, signal);
			return captured.credential;
		});
	}

	delete(providerId: string, options?: AuthOperationOptions): Promise<void> {
		return this.withLock(options, async (signal, lockHandle) => {
			const snapshot = await this.loadEntries(signal);
			const { entries } = snapshot;
			const key = providerStorageKey(providerId);
			const current = entries.get(key);
			if (current === undefined) return;
			this.unprotectEntry(providerId, current);
			entries.delete(key);
			signal?.throwIfAborted();
			if (entries.size === 0) await this.removeStore(snapshot.identity, lockHandle, signal);
			else await this.writeEntries(entries, snapshot.identity, lockHandle, signal);
		});
	}

	private async withLock<T>(
		options: AuthOperationOptions | undefined,
		operation: (signal: AbortSignal | undefined, lockHandle: FileHandle) => Promise<T>,
	): Promise<T> {
		const signal = options?.signal;
		signal?.throwIfAborted();
		const lockBudget: LockWaitBudget = {
			started: performance.now(),
			waitMs: this.limits.lockWaitMs,
			retryDelayMs: this.limits.lockRetryDelayMs,
			signal,
		};
		while (true) {
			await this.assertRootStable(signal);
			let candidate: LockHandleCandidate;
			try {
				candidate = await this.openLockHandle(lockBudget);
			} catch (error) {
				if (!(error instanceof LockInitializationRace)) throw error;
				await this.assertRootStable(signal);
				await waitForLockRetry(lockBudget);
				continue;
			}
			try {
				await acquireExclusiveLock(candidate.handle, lockBudget);
				await this.assertRootStable(signal);
				const currentLockIdentity = await assertOptionalExactFile(this.lockPath, "storage_changed");
				if (currentLockIdentity === undefined || !sameIdentity(currentLockIdentity, candidate.identity)) {
					throw new WindowsCurrentUserCredentialStoreError("storage_changed");
				}
				await this.cleanupTemporaryFiles(candidate.handle, signal);
				const result = await operation(signal, candidate.handle);
				await assertExactOpenFile(this.lockPath, candidate.handle);
				return result;
			} finally {
				await candidate.handle.close().catch(() => undefined);
			}
		}
	}

	private async openLockHandle(budget: Readonly<LockWaitBudget>): Promise<LockHandleCandidate> {
		let handle: FileHandle | undefined;
		let expectedIdentity: FileIdentity | undefined;
		try {
			expectedIdentity = await assertOptionalExactFile(this.lockPath, "storage_changed");
			budget.signal?.throwIfAborted();
			handle = await open(
				this.lockPath,
				expectedIdentity === undefined
					? fsConstants.O_RDWR | fsConstants.O_CREAT | fsConstants.O_EXCL
					: fsConstants.O_RDWR,
				0o600,
			);
			const opened = await handle.stat({ bigint: true });
			if (!opened.isFile()) throw new WindowsCurrentUserCredentialStoreError("storage_changed");
			const openedIdentity = identityOf(opened);
			const currentIdentity = await assertOptionalExactFile(this.lockPath, "storage_changed");
			if (
				currentIdentity === undefined ||
				(expectedIdentity !== undefined && !sameIdentity(expectedIdentity, currentIdentity)) ||
				!sameIdentity(currentIdentity, openedIdentity)
			) {
				throw new WindowsCurrentUserCredentialStoreError("storage_changed");
			}
			budget.signal?.throwIfAborted();
			return { handle, identity: openedIdentity };
		} catch (error) {
			await handle?.close().catch(() => undefined);
			budget.signal?.throwIfAborted();
			if (expectedIdentity === undefined && isNodeError(error, "EEXIST")) throw new LockInitializationRace();
			if (isNodeError(error, "ENOENT") || isChangedPathError(error)) {
				throw new WindowsCurrentUserCredentialStoreError("storage_changed");
			}
			throwStoreOperationError(error, budget.signal, "storage_unavailable");
		}
	}

	private async loadEntries(signal: AbortSignal | undefined): Promise<LoadedStoreSnapshot> {
		signal?.throwIfAborted();
		await this.assertRootStable(signal);
		let handle: FileHandle;
		try {
			handle = await open(this.filePath, fsConstants.O_RDONLY | (fsConstants.O_NOFOLLOW ?? 0));
		} catch (error) {
			signal?.throwIfAborted();
			if (isNodeError(error, "ENOENT")) return { entries: new Map(), identity: undefined };
			if (isChangedPathError(error)) throw new WindowsCurrentUserCredentialStoreError("storage_changed");
			throw new WindowsCurrentUserCredentialStoreError("storage_unavailable");
		}
		try {
			await assertExactOpenFile(this.filePath, handle);
			const before = await handle.stat({ bigint: true });
			if (before.size > BigInt(this.limits.maxStoreBytes) || before.size > BigInt(bufferConstants.MAX_LENGTH)) {
				throw new WindowsCurrentUserCredentialStoreError("capacity");
			}
			const bytes = Buffer.alloc(Number(before.size));
			let offset = 0;
			while (offset < bytes.byteLength) {
				signal?.throwIfAborted();
				const result = await handle.read(bytes, offset, bytes.byteLength - offset, offset);
				if (result.bytesRead === 0) throw new WindowsCurrentUserCredentialStoreError("storage_changed");
				offset += result.bytesRead;
			}
			const extra = Buffer.allocUnsafe(1);
			if ((await handle.read(extra, 0, 1, offset)).bytesRead !== 0) {
				throw new WindowsCurrentUserCredentialStoreError("storage_changed");
			}
			const after = await handle.stat({ bigint: true });
			const addressedAfter = await assertExactOpenFile(this.filePath, handle);
			if (!sameFile(before, after) || !sameIdentity(after, addressedAfter)) {
				throw new WindowsCurrentUserCredentialStoreError("storage_changed");
			}
			signal?.throwIfAborted();
			await this.assertRootStable(signal);
			return { entries: parseStore(bytes, this.limits), identity: identityOf(after) };
		} catch (error) {
			throwStoreOperationError(error, signal, "storage_unavailable");
		} finally {
			await handle.close().catch(() => undefined);
		}
	}

	private unprotectEntry(providerId: string, entry: PersistedEntry): Credential {
		const payload = this.unprotectPayload(providerStorageKey(providerId), entry);
		if (payload.providerId !== providerId) throw new WindowsCurrentUserCredentialStoreError("corrupt");
		return payload.credential;
	}

	private unprotectPayload(key: string, entry: PersistedEntry): PersistedCredentialPayload {
		let plaintext: Uint8Array | undefined;
		try {
			const entropy = decodeBase64(entry.entropy, ENTROPY_BYTES);
			const ciphertext = decodeBase64(entry.ciphertext);
			plaintext = Dpapi.unprotectData(ciphertext, entropy, "CurrentUser");
			if (plaintext.byteLength > this.limits.maxCredentialBytes) {
				throw new WindowsCurrentUserCredentialStoreError("capacity");
			}
			const text = new TextDecoder("utf-8", { fatal: true }).decode(plaintext);
			assertJsonDepth(text, this.limits.maxCredentialJsonDepth, "capacity");
			const value: unknown = JSON.parse(text);
			const payload = capturePersistedPayload(value, this.limits);
			if (providerStorageKey(payload.providerId) !== key || JSON.stringify(payload) !== text) {
				throw new WindowsCurrentUserCredentialStoreError("corrupt");
			}
			return payload;
		} catch (error) {
			if (error instanceof WindowsCurrentUserCredentialStoreError) throw error;
			throw new WindowsCurrentUserCredentialStoreError("corrupt");
		} finally {
			plaintext?.fill(0);
		}
	}

	private async writeEntries(
		entries: ReadonlyMap<string, PersistedEntry>,
		expectedIdentity: FileIdentity | undefined,
		lockHandle: FileHandle,
		signal: AbortSignal | undefined,
	): Promise<void> {
		signal?.throwIfAborted();
		const persisted: PersistedStore = {
			version: STORE_VERSION,
			entries: Object.fromEntries([...entries].sort(([left], [right]) => compareText(left, right))),
		};
		const bytes = Buffer.from(JSON.stringify(persisted), "utf8");
		if (bytes.byteLength > this.limits.maxStoreBytes || bytes.byteLength > bufferConstants.MAX_LENGTH) {
			throw new WindowsCurrentUserCredentialStoreError("capacity");
		}
		await this.atomicReplace(bytes, expectedIdentity, lockHandle, signal);
	}

	private async atomicReplace(
		bytes: Buffer,
		expectedIdentity: FileIdentity | undefined,
		lockHandle: FileHandle,
		signal: AbortSignal | undefined,
	): Promise<void> {
		await this.assertRootStable(signal);
		await assertExactOpenFile(this.lockPath, lockHandle);
		await assertOptionalFileUnchanged(this.filePath, expectedIdentity);
		signal?.throwIfAborted();
		let temporaryPath: string;
		try {
			temporaryPath = join(this.rootPath, `.${basename(this.filePath)}.${randomUUID()}.tmp`);
		} catch (error) {
			throwStoreOperationError(error, signal, "storage_unavailable");
		}
		let handle: FileHandle | undefined;
		let temporaryIdentity: FileIdentity | undefined;
		try {
			handle = await open(temporaryPath, "wx", 0o600);
			temporaryIdentity = await assertExactOpenFile(temporaryPath, handle);
			await handle.writeFile(bytes);
			const written = await handle.stat({ bigint: true });
			if (!written.isFile() || written.size !== BigInt(bytes.byteLength)) {
				throw new WindowsCurrentUserCredentialStoreError("storage_unavailable");
			}
			if (!sameIdentity(written, temporaryIdentity)) {
				throw new WindowsCurrentUserCredentialStoreError("storage_changed");
			}
			await handle.sync();
			signal?.throwIfAborted();
			await handle.close();
			handle = undefined;
			await this.assertRootStable(signal);
			await assertExactOpenFile(this.lockPath, lockHandle);
			await assertOptionalFileUnchanged(this.filePath, expectedIdentity);
			await assertOptionalFileUnchanged(temporaryPath, temporaryIdentity);
			signal?.throwIfAborted();
			await rename(temporaryPath, this.filePath);
			await assertOptionalFileUnchanged(this.filePath, temporaryIdentity);
		} catch (error) {
			throwStoreOperationError(error, signal, "storage_unavailable");
		} finally {
			await handle?.close().catch(() => undefined);
			if (temporaryIdentity !== undefined) {
				await unlinkFileIfUnchanged(temporaryPath, temporaryIdentity).catch(() => undefined);
			}
		}
	}

	private async removeStore(
		expectedIdentity: FileIdentity | undefined,
		lockHandle: FileHandle,
		signal: AbortSignal | undefined,
	): Promise<void> {
		signal?.throwIfAborted();
		await this.assertRootStable(signal);
		await assertExactOpenFile(this.lockPath, lockHandle);
		await assertOptionalFileUnchanged(this.filePath, expectedIdentity);
		signal?.throwIfAborted();
		try {
			await unlink(this.filePath);
		} catch (error) {
			if (isNodeError(error, "ENOENT")) throw new WindowsCurrentUserCredentialStoreError("storage_changed");
			throw new WindowsCurrentUserCredentialStoreError("storage_unavailable");
		}
	}

	private async cleanupTemporaryFiles(lockHandle: FileHandle, signal: AbortSignal | undefined): Promise<void> {
		try {
			const temporaryFiles: { readonly path: string; readonly identity: FileIdentity }[] = [];
			let scanned = 0;
			for await (const entry of await opendir(this.rootPath)) {
				signal?.throwIfAborted();
				scanned += 1;
				if (scanned > this.limits.maxDirectoryEntriesToScan) {
					throw new WindowsCurrentUserCredentialStoreError("capacity");
				}
				if (!this.temporaryNamePattern.test(entry.name)) continue;
				const path = join(this.rootPath, entry.name);
				const identity = await assertOptionalExactFile(path, "storage_changed", false);
				if (identity === undefined) throw new WindowsCurrentUserCredentialStoreError("storage_changed");
				temporaryFiles.push({ path, identity });
			}
			for (const temporaryFile of temporaryFiles) {
				signal?.throwIfAborted();
				await assertOptionalFileUnchanged(temporaryFile.path, temporaryFile.identity);
				signal?.throwIfAborted();
				await assertExactOpenFile(this.lockPath, lockHandle);
				await unlink(temporaryFile.path);
			}
		} catch (error) {
			throwStoreOperationError(error, signal, "storage_unavailable");
		}
	}

	private async assertRootStable(signal: AbortSignal | undefined): Promise<void> {
		signal?.throwIfAborted();
		try {
			const current = resolve(await realpath(this.rootPath));
			const identity = await exactDirectoryIdentity(current, "storage_changed");
			signal?.throwIfAborted();
			if (current !== this.rootPath || !sameIdentity(identity, this.rootIdentity)) {
				throw new WindowsCurrentUserCredentialStoreError("storage_changed");
			}
		} catch (error) {
			throwStoreOperationError(error, signal, "storage_changed");
		}
	}
}

function throwStoreOperationError(
	error: unknown,
	signal: AbortSignal | undefined,
	fallbackCode: Extract<WindowsCurrentUserCredentialStoreErrorCode, "storage_changed" | "storage_unavailable">,
): never {
	signal?.throwIfAborted();
	if (error instanceof WindowsCurrentUserCredentialStoreError) throw error;
	throw new WindowsCurrentUserCredentialStoreError(fallbackCode);
}

function captureOptions(options: Readonly<OpenWindowsCurrentUserCredentialStoreOptions>): {
	readonly filePath: string;
	readonly limits: Readonly<WindowsCurrentUserCredentialStoreLimits>;
} {
	try {
		if (!options || typeof options !== "object") throw new TypeError();
		const filePath = options.filePath;
		if (typeof filePath !== "string" || filePath.length === 0 || filePath.includes("\0")) throw new TypeError();
		const capturedPath = resolve(filePath);
		const root = parse(capturedPath).root;
		if (capturedPath === root || dirname(capturedPath) === root) throw new TypeError();
		const limits = options.limits;
		if (!limits || typeof limits !== "object") throw new TypeError();
		const capturedLimits: WindowsCurrentUserCredentialStoreLimits = {
			maxEntries: limits.maxEntries,
			maxCredentialBytes: limits.maxCredentialBytes,
			maxStoreBytes: limits.maxStoreBytes,
			maxCredentialJsonDepth: limits.maxCredentialJsonDepth,
			maxDirectoryEntriesToScan: limits.maxDirectoryEntriesToScan,
			lockWaitMs: limits.lockWaitMs,
			lockRetryDelayMs: limits.lockRetryDelayMs,
		};
		for (const value of [
			capturedLimits.maxEntries,
			capturedLimits.maxCredentialBytes,
			capturedLimits.maxStoreBytes,
			capturedLimits.maxDirectoryEntriesToScan,
			capturedLimits.lockWaitMs,
		]) {
			if (!Number.isSafeInteger(value) || value < 0) throw new TypeError();
		}
		if (
			!Number.isSafeInteger(capturedLimits.maxCredentialJsonDepth) ||
			capturedLimits.maxCredentialJsonDepth < 1 ||
			!Number.isSafeInteger(capturedLimits.lockRetryDelayMs) ||
			capturedLimits.lockRetryDelayMs < 1 ||
			capturedLimits.lockRetryDelayMs > MAX_TIMER_DELAY_MS ||
			capturedLimits.maxCredentialBytes > bufferConstants.MAX_LENGTH ||
			capturedLimits.maxStoreBytes > bufferConstants.MAX_LENGTH
		) {
			throw new TypeError();
		}
		return {
			filePath: capturedPath,
			limits: Object.freeze(capturedLimits),
		};
	} catch {
		throw new WindowsCurrentUserCredentialStoreError("invalid_configuration");
	}
}

async function acquireExclusiveLock(handle: FileHandle, budget: Readonly<LockWaitBudget>): Promise<void> {
	while (true) {
		budget.signal?.throwIfAborted();
		try {
			flockSync(handle.fd, "exnb");
			return;
		} catch (error) {
			if (!isNodeError(error, "EWOULDBLOCK")) {
				throw new WindowsCurrentUserCredentialStoreError("storage_unavailable");
			}
		}
		await waitForLockRetry(budget);
	}
}

async function waitForLockRetry(budget: Readonly<LockWaitBudget>): Promise<void> {
	budget.signal?.throwIfAborted();
	const elapsed = performance.now() - budget.started;
	if (elapsed >= budget.waitMs) throw new WindowsCurrentUserCredentialStoreError("locked");
	await delayWithSignal(Math.min(budget.retryDelayMs, budget.waitMs - elapsed), budget.signal);
}

async function delayWithSignal(milliseconds: number, signal: AbortSignal | undefined): Promise<void> {
	if (milliseconds <= 0) return;
	if (signal === undefined) {
		await new Promise<void>((resolveDelay) => setTimeout(resolveDelay, milliseconds));
		return;
	}
	signal.throwIfAborted();
	await new Promise<void>((resolveDelay, rejectDelay) => {
		const onAbort = (): void => {
			clearTimeout(timer);
			rejectDelay(signal.reason);
		};
		const timer = setTimeout(() => {
			signal.removeEventListener("abort", onAbort);
			resolveDelay();
		}, milliseconds);
		signal.addEventListener("abort", onAbort, { once: true });
		if (signal.aborted) onAbort();
	});
}

function captureCredential(
	providerId: string,
	credential: Credential,
	limits: Readonly<WindowsCurrentUserCredentialStoreLimits>,
): CapturedCredential {
	try {
		if (typeof providerId !== "string") throw new TypeError();
		const captured = captureCredentialValue(credential, limits);
		const payload: PersistedCredentialPayload = { version: PAYLOAD_VERSION, providerId, credential: captured };
		const payloadText = JSON.stringify(payload);
		assertJsonDepth(payloadText, limits.maxCredentialJsonDepth, "capacity");
		const payloadBytes = Buffer.from(payloadText, "utf8");
		if (payloadBytes.byteLength > limits.maxCredentialBytes) {
			throw new WindowsCurrentUserCredentialStoreError("capacity");
		}
		return { credential: captured, payloadBytes };
	} catch (error) {
		if (error instanceof WindowsCurrentUserCredentialStoreError) throw error;
		throw new WindowsCurrentUserCredentialStoreError("invalid_credential");
	}
}

function capturePersistedPayload(
	value: unknown,
	limits: Readonly<WindowsCurrentUserCredentialStoreLimits>,
): PersistedCredentialPayload {
	if (!isPlainRecord(value) || !hasExactKeys(value, ["credential", "providerId", "version"])) {
		throw new WindowsCurrentUserCredentialStoreError("corrupt");
	}
	if (value.version !== PAYLOAD_VERSION || typeof value.providerId !== "string") {
		throw new WindowsCurrentUserCredentialStoreError("corrupt");
	}
	try {
		return {
			version: PAYLOAD_VERSION,
			providerId: value.providerId,
			credential: captureCredentialValue(value.credential, limits),
		};
	} catch {
		throw new WindowsCurrentUserCredentialStoreError("corrupt");
	}
}

function captureCredentialValue(value: unknown, limits: Readonly<WindowsCurrentUserCredentialStoreLimits>): Credential {
	if (limits.maxCredentialBytes === 0) throw new WindowsCurrentUserCredentialStoreError("capacity");
	let normalized: ReturnType<typeof normalizeJsonValueWithin>;
	try {
		normalized = normalizeJsonValueWithin(value, {
			maxBytes: limits.maxCredentialBytes,
			maxDepth: limits.maxCredentialJsonDepth,
		});
	} catch (error) {
		if (error instanceof JsonValueLimitError) throw new WindowsCurrentUserCredentialStoreError("capacity");
		throw error;
	}
	if (!isPlainRecord(normalized) || typeof normalized.type !== "string") throw new TypeError();
	if (normalized.type === "api_key") {
		if (!hasOnlyKeys(normalized, ["env", "key", "type"])) throw new TypeError();
		if (normalized.key !== undefined && typeof normalized.key !== "string") throw new TypeError();
		let env: Record<string, string> | undefined;
		if (normalized.env !== undefined) {
			if (!isPlainRecord(normalized.env)) throw new TypeError();
			env = Object.create(null) as Record<string, string>;
			for (const key of Object.keys(normalized.env).sort()) {
				const item = normalized.env[key];
				if (typeof item !== "string") throw new TypeError();
				env[key] = item;
			}
		}
		return {
			type: "api_key",
			...(normalized.key === undefined ? {} : { key: normalized.key }),
			...(env === undefined ? {} : { env }),
		};
	}
	if (normalized.type !== "oauth" || typeof normalized.refresh !== "string" || typeof normalized.access !== "string") {
		throw new TypeError();
	}
	if (typeof normalized.expires !== "number" || !Number.isFinite(normalized.expires)) throw new TypeError();
	const output: Record<string, unknown> = {
		type: "oauth",
		refresh: normalized.refresh,
		access: normalized.access,
		expires: normalized.expires,
	};
	for (const key of Object.keys(normalized).sort()) {
		if (key === "type" || key === "refresh" || key === "access" || key === "expires") continue;
		Object.defineProperty(output, key, {
			value: normalized[key],
			enumerable: true,
			configurable: true,
			writable: true,
		});
	}
	return output as Credential;
}

function protectPayload(payloadBytes: Buffer): PersistedEntry {
	let entropy: Buffer | undefined;
	try {
		entropy = randomBytes(ENTROPY_BYTES);
		const ciphertext = Buffer.from(Dpapi.protectData(payloadBytes, entropy, "CurrentUser"));
		if (ciphertext.byteLength === 0) throw new Error();
		return { entropy: entropy.toString("base64"), ciphertext: ciphertext.toString("base64") };
	} catch {
		throw new WindowsCurrentUserCredentialStoreError("storage_unavailable");
	} finally {
		payloadBytes.fill(0);
		entropy?.fill(0);
	}
}

function parseStore(
	bytes: Buffer,
	limits: Readonly<WindowsCurrentUserCredentialStoreLimits>,
): Map<string, PersistedEntry> {
	try {
		if (bytes.byteLength === 0) throw new TypeError();
		const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
		assertJsonDepth(text, STORE_JSON_DEPTH);
		const value: unknown = JSON.parse(text);
		if (!isPlainRecord(value) || !hasExactKeys(value, ["entries", "version"]) || value.version !== STORE_VERSION) {
			throw new TypeError();
		}
		if (!isPlainRecord(value.entries)) throw new TypeError();
		const entries = new Map<string, PersistedEntry>();
		const keys = Object.keys(value.entries);
		const sortedKeys = [...keys].sort(compareText);
		if (keys.some((key, index) => key !== sortedKeys[index])) throw new TypeError();
		for (const key of sortedKeys) {
			if (!HASH_PATTERN.test(key)) throw new TypeError();
			const raw = value.entries[key];
			if (!isPlainRecord(raw) || !hasExactKeys(raw, ["ciphertext", "entropy"])) throw new TypeError();
			if (typeof raw.entropy !== "string" || typeof raw.ciphertext !== "string") throw new TypeError();
			decodeBase64(raw.entropy, ENTROPY_BYTES);
			decodeBase64(raw.ciphertext);
			entries.set(key, { entropy: raw.entropy, ciphertext: raw.ciphertext });
		}
		if (entries.size > limits.maxEntries) throw new WindowsCurrentUserCredentialStoreError("capacity");
		const canonical: PersistedStore = { version: STORE_VERSION, entries: Object.fromEntries(entries) };
		if (JSON.stringify(canonical) !== text) throw new TypeError();
		return entries;
	} catch (error) {
		if (error instanceof WindowsCurrentUserCredentialStoreError) throw error;
		throw new WindowsCurrentUserCredentialStoreError("corrupt");
	} finally {
		bytes.fill(0);
	}
}

function decodeBase64(value: string, exactBytes?: number): Buffer {
	if (value.length === 0 || !BASE64_PATTERN.test(value)) throw new TypeError();
	const bytes = Buffer.from(value, "base64");
	if (bytes.toString("base64") !== value || (exactBytes !== undefined && bytes.byteLength !== exactBytes)) {
		throw new TypeError();
	}
	return bytes;
}

function providerStorageKey(providerId: string): string {
	if (typeof providerId !== "string") throw new WindowsCurrentUserCredentialStoreError("invalid_credential");
	return createHash("sha256").update(Buffer.from(providerId, "utf16le")).digest("hex");
}

function compareText(left: string, right: string): number {
	return left < right ? -1 : left > right ? 1 : 0;
}

function assertJsonDepth(
	text: string,
	maximum: number,
	maximumError?: Extract<WindowsCurrentUserCredentialStoreErrorCode, "capacity">,
): void {
	let depth = 0;
	let inString = false;
	let escaped = false;
	for (const character of text) {
		if (inString) {
			if (escaped) escaped = false;
			else if (character === "\\") escaped = true;
			else if (character === '"') inString = false;
			continue;
		}
		if (character === '"') inString = true;
		else if (character === "{" || character === "[") {
			depth += 1;
			if (depth > maximum) {
				if (maximumError !== undefined) throw new WindowsCurrentUserCredentialStoreError(maximumError);
				throw new TypeError();
			}
		} else if (character === "}" || character === "]") {
			depth -= 1;
			if (depth < 0) throw new TypeError();
		}
	}
	if (depth !== 0 || inString || escaped) throw new TypeError();
}

async function exactDirectoryIdentity(
	path: string,
	errorCode: "invalid_configuration" | "storage_changed",
): Promise<FileIdentity> {
	const addressed = await lstat(path, { bigint: true });
	const followed = await assertAddressedPath(path, addressed, "directory", errorCode);
	return { dev: followed.dev, ino: followed.ino };
}

async function assertOptionalExactFile(
	path: string,
	errorCode: "invalid_configuration" | "storage_changed",
	allowMissing = true,
): Promise<FileIdentity | undefined> {
	let addressed: BigIntStats;
	try {
		addressed = await lstat(path, { bigint: true });
	} catch (error) {
		if (isNodeError(error, "ENOENT")) {
			if (allowMissing) return undefined;
			throw new WindowsCurrentUserCredentialStoreError(errorCode);
		}
		throw error;
	}
	return identityOf(await assertAddressedPath(path, addressed, "file", errorCode));
}

async function assertOptionalFileUnchanged(path: string, expected: FileIdentity | undefined): Promise<void> {
	const current = await assertOptionalExactFile(path, "storage_changed");
	if (
		(expected === undefined && current !== undefined) ||
		(expected !== undefined && (current === undefined || !sameIdentity(current, expected)))
	) {
		throw new WindowsCurrentUserCredentialStoreError("storage_changed");
	}
}

async function assertExactOpenFile(path: string, handle: FileHandle): Promise<FileIdentity> {
	try {
		const addressed = await lstat(path, { bigint: true });
		const [followed, opened] = await Promise.all([
			assertAddressedPath(path, addressed, "file", "storage_changed"),
			handle.stat({ bigint: true }),
		]);
		if (!opened.isFile() || !sameIdentity(followed, opened)) {
			throw new WindowsCurrentUserCredentialStoreError("storage_changed");
		}
		return identityOf(opened);
	} catch (error) {
		throwStoreOperationError(error, undefined, "storage_changed");
	}
}

async function unlinkFileIfUnchanged(path: string, expected: FileIdentity): Promise<void> {
	const current = await assertOptionalExactFile(path, "storage_changed");
	if (current !== undefined && sameIdentity(current, expected)) await unlink(path);
}

async function assertAddressedPath(
	path: string,
	addressed: BigIntStats,
	kind: "directory" | "file",
	errorCode: "invalid_configuration" | "storage_changed",
): Promise<BigIntStats> {
	assertNoWindowsReparsePath(path, errorCode);
	const [canonical, followed] = await Promise.all([realpath(path), stat(path, { bigint: true })]);
	assertNoWindowsReparsePath(path, errorCode);
	const addressedMatches = kind === "directory" ? addressed.isDirectory() : addressed.isFile();
	const followedMatches = kind === "directory" ? followed.isDirectory() : followed.isFile();
	if (
		resolve(canonical) !== resolve(path) ||
		!addressedMatches ||
		addressed.isSymbolicLink() ||
		!followedMatches ||
		!sameIdentity(addressed, followed)
	) {
		throw new WindowsCurrentUserCredentialStoreError(errorCode);
	}
	return followed;
}

function assertNoWindowsReparsePath(path: string, errorCode: "invalid_configuration" | "storage_changed"): void {
	try {
		const target = resolve(path);
		const root = parse(target).root;
		const components = [target];
		while (components[components.length - 1] !== root) {
			components.push(dirname(components[components.length - 1] ?? root));
		}
		for (const component of components.reverse()) {
			const attributes = windowsFileAttributes(component);
			if (
				attributes === INVALID_FILE_ATTRIBUTES ||
				(attributes & FILE_ATTRIBUTE_REPARSE_POINT) === FILE_ATTRIBUTE_REPARSE_POINT
			) {
				throw new WindowsCurrentUserCredentialStoreError(errorCode);
			}
		}
	} catch (error) {
		if (error instanceof WindowsCurrentUserCredentialStoreError) throw error;
		throw new WindowsCurrentUserCredentialStoreError(errorCode);
	}
}

function windowsFileAttributes(path: string): number {
	if (getFileAttributes === undefined) {
		const kernel32 = koffi.load("kernel32.dll");
		getFileAttributes = kernel32.func("__stdcall", "GetFileAttributesW", "uint32", [
			"str16",
		]) as unknown as GetFileAttributes;
	}
	return getFileAttributes(path);
}

function hasExactKeys(value: Readonly<Record<string, unknown>>, expected: readonly string[]): boolean {
	const actual = Object.keys(value).sort();
	return actual.length === expected.length && actual.every((key, index) => key === expected[index]);
}

function escapeRegExp(value: string): string {
	return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}

function hasOnlyKeys(value: Readonly<Record<string, unknown>>, allowed: readonly string[]): boolean {
	const accepted = new Set(allowed);
	return Object.keys(value).every((key) => accepted.has(key));
}

function isPlainRecord(value: unknown): value is Record<string, unknown> {
	if (value === null || typeof value !== "object" || Array.isArray(value)) return false;
	const prototype = Object.getPrototypeOf(value);
	return prototype === Object.prototype || prototype === null;
}

function sameFile(left: BigIntStats, right: BigIntStats): boolean {
	return sameIdentity(left, right) && left.size === right.size;
}

function identityOf(value: FileIdentity): FileIdentity {
	return { dev: value.dev, ino: value.ino };
}

function sameIdentity(left: FileIdentity, right: FileIdentity): boolean {
	return left.dev === right.dev && left.ino === right.ino;
}

function isChangedPathError(error: unknown): boolean {
	return isNodeError(error, "ELOOP") || isNodeError(error, "ENOTDIR");
}

function isNodeError(error: unknown, code: string): boolean {
	return error instanceof Error && "code" in error && error.code === code;
}

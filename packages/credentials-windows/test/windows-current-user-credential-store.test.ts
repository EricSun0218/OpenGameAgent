import { type ChildProcess, spawn } from "node:child_process";
import { once } from "node:events";
import { closeSync, openSync, readdirSync, renameSync, writeFileSync } from "node:fs";
import { mkdir, mkdtemp, readdir, readFile, rename, rm, stat, symlink, unlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { performance } from "node:perf_hooks";
import { fileURLToPath } from "node:url";
import { flockSync } from "fs-ext-extra-prebuilt";
import { afterEach, expect, test } from "vitest";
import {
	openWindowsCurrentUserCredentialStore,
	WindowsCurrentUserCredentialStoreError,
	type WindowsCurrentUserCredentialStoreLimits,
} from "../src/index.js";

const temporaryDirectories: string[] = [];
const TEST_PROGRESS_TIMEOUT_MS = 5_000;
const TEST_LIMITS: Readonly<WindowsCurrentUserCredentialStoreLimits> = Object.freeze({
	maxEntries: 2,
	maxCredentialBytes: 4_096,
	maxStoreBytes: 16_384,
	maxCredentialJsonDepth: 6,
	maxDirectoryEntriesToScan: 64,
	lockWaitMs: 1_000,
	lockRetryDelayMs: 10,
});
const SMALL_TEST_LIMITS: Readonly<WindowsCurrentUserCredentialStoreLimits> = Object.freeze({
	...TEST_LIMITS,
	maxEntries: 1,
	maxCredentialBytes: 1_024,
	maxStoreBytes: 8_192,
	maxCredentialJsonDepth: 4,
});

async function createTemporaryDirectory(): Promise<string> {
	const directory = await mkdtemp(join(tmpdir(), "oga-credentials-windows-"));
	temporaryDirectories.push(directory);
	return directory;
}

async function findNumberCollidingDirectories(
	base: string,
): Promise<{ readonly original: string; readonly replacement: string } | undefined> {
	const started = performance.now();
	while (performance.now() - started < TEST_PROGRESS_TIMEOUT_MS / 2) {
		const identities = new Map<string, { readonly path: string; readonly ino: bigint }>();
		const paths: string[] = [];
		for (let index = 0; index < TEST_LIMITS.maxDirectoryEntriesToScan; index += 1) {
			const path = join(base, `identity-${index}`);
			await mkdir(path);
			paths.push(path);
			const identity = await stat(path, { bigint: true });
			const key = `${identity.dev}:${Number(identity.ino)}`;
			const earlier = identities.get(key);
			if (earlier !== undefined && earlier.ino !== identity.ino) {
				return { original: earlier.path, replacement: path };
			}
			identities.set(key, { path, ino: identity.ino });
		}
		for (const path of paths) await rm(path, { recursive: true });
	}
	return undefined;
}

function testLimits(
	overrides: Partial<WindowsCurrentUserCredentialStoreLimits> = {},
): WindowsCurrentUserCredentialStoreLimits {
	return { ...TEST_LIMITS, ...overrides };
}

function smallTestLimits(
	overrides: Partial<WindowsCurrentUserCredentialStoreLimits> = {},
): WindowsCurrentUserCredentialStoreLimits {
	return { ...SMALL_TEST_LIMITS, ...overrides };
}

function deferred(): { readonly promise: Promise<void>; readonly resolve: () => void } {
	let resolvePromise!: () => void;
	const promise = new Promise<void>((resolve) => {
		resolvePromise = resolve;
	});
	return { promise, resolve: resolvePromise };
}

type OpenedWindowsCredentialStore = Awaited<ReturnType<typeof openWindowsCurrentUserCredentialStore>>;

interface HeldStoreLock {
	readonly store: OpenedWindowsCredentialStore;
	readonly blockingWrite: ReturnType<OpenedWindowsCredentialStore["modify"]>;
	readonly release: () => void;
}

async function holdStoreLock(
	filePath: string,
	onHeld: () => PromiseLike<void> | void = () => undefined,
): Promise<HeldStoreLock> {
	const options = {
		filePath,
		limits: smallTestLimits({ lockWaitMs: TEST_PROGRESS_TIMEOUT_MS }),
	} as const;
	const [store, blocker] = await Promise.all([
		openWindowsCurrentUserCredentialStore(options),
		openWindowsCurrentUserCredentialStore(options),
	]);
	const entered = deferred();
	const release = deferred();
	const blockingWrite = blocker.modify("deepseek", async (current) => {
		await onHeld();
		entered.resolve();
		await release.promise;
		return current ?? { type: "api_key", key: "blocker" };
	});
	await entered.promise;
	return { store, blockingWrite, release: release.resolve };
}

async function waitForLockedLine(child: ChildProcess): Promise<void> {
	const stdout = child.stdout;
	if (stdout === null) throw new Error("child stdout is unavailable");
	await new Promise<void>((resolveLine, rejectLine) => {
		let captured = "";
		const timer = setTimeout(() => finish(new Error("child lock readiness timed out")), TEST_PROGRESS_TIMEOUT_MS);
		const onData = (chunk: Buffer): void => {
			captured += chunk.toString("utf8");
			if (captured.length > "locked\n".length || (captured.includes("\n") && captured !== "locked\n")) {
				finish(new Error("child emitted invalid lock readiness output"));
			} else if (captured === "locked\n") finish();
		};
		const onError = (error: Error): void => finish(error);
		const onClose = (): void => finish(new Error("child exited before lock readiness"));
		const finish = (error?: Error): void => {
			clearTimeout(timer);
			stdout.off("data", onData);
			child.off("error", onError);
			child.off("close", onClose);
			if (error === undefined) resolveLine();
			else rejectLine(error);
		};
		stdout.on("data", onData);
		child.once("error", onError);
		child.once("close", onClose);
	});
}

afterEach(async () => {
	for (const directory of temporaryDirectories.splice(0)) {
		await rm(directory, { recursive: true, force: true });
	}
});

test("fails fast without filesystem access when the current platform is not Windows", async () => {
	const descriptor = Object.getOwnPropertyDescriptor(process, "platform");
	if (descriptor === undefined) throw new Error("process.platform descriptor is unavailable");
	Object.defineProperty(process, "platform", { ...descriptor, value: "linux" });
	try {
		const failure = await openWindowsCurrentUserCredentialStore(undefined as never).catch((error: unknown) => error);
		expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("unsupported_platform");
	} finally {
		Object.defineProperty(process, "platform", descriptor);
	}
});

test.runIf(process.platform === "win32")("captures each host-owned limit exactly once", async () => {
	const directory = await createTemporaryDirectory();
	const values = {
		maxEntries: 1,
		maxCredentialBytes: 1_024,
		maxStoreBytes: 8_192,
		maxCredentialJsonDepth: 4,
		maxDirectoryEntriesToScan: 64,
		lockWaitMs: 1_000,
		lockRetryDelayMs: 10,
	};
	const reads = new Map<keyof typeof values, number>();
	const limits = {} as typeof values;
	for (const [key, value] of Object.entries(values) as [keyof typeof values, number][]) {
		Object.defineProperty(limits, key, {
			enumerable: true,
			get(): number {
				reads.set(key, (reads.get(key) ?? 0) + 1);
				return value;
			},
		});
	}

	await openWindowsCurrentUserCredentialStore({ filePath: join(directory, "credentials.json"), limits });
	expect(Object.fromEntries(reads)).toEqual({
		maxEntries: 1,
		maxCredentialBytes: 1,
		maxStoreBytes: 1,
		maxCredentialJsonDepth: 1,
		maxDirectoryEntriesToScan: 1,
		lockWaitMs: 1,
		lockRetryDelayMs: 1,
	});
});

test.runIf(process.platform === "win32")(
	"persists credentials for a fresh instance without plaintext provider data at rest",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const options = {
			filePath,
			limits: testLimits({
				maxEntries: 4,
				maxCredentialJsonDepth: 8,
			}),
		} as const;

		const first = await openWindowsCurrentUserCredentialStore(options);
		const credential = {
			type: "api_key" as const,
			key: "deepseek-private-key",
			env: { DEEPSEEK_ACCOUNT: "private-account" },
		};
		await expect(first.modify("deepseek", async () => credential)).resolves.toEqual(credential);

		const persisted = await readFile(filePath, "utf8");
		expect(persisted).not.toContain("deepseek");
		expect(persisted).not.toContain("deepseek-private-key");
		expect(persisted).not.toContain("private-account");

		const second = await openWindowsCurrentUserCredentialStore(options);
		await expect(second.read("deepseek")).resolves.toEqual(credential);
		await expect(second.list()).resolves.toEqual([{ providerId: "deepseek", type: "api_key" }]);
		await expect(second.delete("deepseek")).resolves.toBeUndefined();
		await expect(first.read("deepseek")).resolves.toBeUndefined();
	},
);

test.runIf(process.platform === "win32")(
	"recovers the cross-process lock immediately after the owning process is terminated",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const store = await openWindowsCurrentUserCredentialStore({
			filePath,
			limits: smallTestLimits({
				lockWaitMs: 25,
				lockRetryDelayMs: 5,
			}),
		});
		const child = spawn(
			process.execPath,
			[fileURLToPath(new URL("./fixtures/hold-windows-file-lock.mjs", import.meta.url)), `${filePath}.lock`],
			{ stdio: ["pipe", "pipe", "pipe"] },
		);
		try {
			await waitForLockedLine(child);
			const blocked = await store
				.modify("deepseek", async () => ({ type: "api_key", key: "blocked" }))
				.catch((error: unknown) => error);
			expect(blocked).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
			expect((blocked as WindowsCurrentUserCredentialStoreError).code).toBe("locked");

			const closed = once(child, "close");
			expect(child.kill()).toBe(true);
			await closed;
			await expect(store.modify("deepseek", async () => ({ type: "api_key", key: "recovered" }))).resolves.toEqual({
				type: "api_key",
				key: "recovered",
			});
		} finally {
			child.stdin?.end();
			if (child.exitCode === null && child.signalCode === null) child.kill();
		}
	},
	TEST_PROGRESS_TIMEOUT_MS,
);

test.runIf(process.platform === "win32")(
	"serializes concurrent first writes from independent store instances",
	async () => {
		const directory = await createTemporaryDirectory();
		const options = {
			filePath: join(directory, "credentials.json"),
			limits: smallTestLimits({ lockWaitMs: TEST_PROGRESS_TIMEOUT_MS }),
		} as const;
		const stores = await Promise.all(
			Array.from({ length: 40 }, async () => openWindowsCurrentUserCredentialStore(options)),
		);

		await Promise.all(
			stores.map(async (store) =>
				store.modify("deepseek", async (current) => ({
					type: "api_key",
					key: `${current?.type === "api_key" ? current.key : ""}x`,
				})),
			),
		);
		const finalCredential = await stores[0]?.read("deepseek");
		expect(finalCredential?.type).toBe("api_key");
		if (finalCredential?.type !== "api_key") throw new Error("final credential is unavailable");
		expect(finalCredential.key).toHaveLength(stores.length);
	},
	TEST_PROGRESS_TIMEOUT_MS * 4,
);

test.runIf(process.platform === "win32")(
	"recovers sibling first writes when the lock creator is cancelled during initialization",
	async () => {
		const base = await createTemporaryDirectory();
		await Promise.all(
			Array.from({ length: 40 }, async (_, index) => {
				const directory = join(base, `cancelled-creator-${index}`);
				await mkdir(directory);
				const options = {
					filePath: join(directory, "credentials.json"),
					limits: smallTestLimits({ lockWaitMs: TEST_PROGRESS_TIMEOUT_MS }),
				} as const;
				const [creator, sibling] = await Promise.all([
					openWindowsCurrentUserCredentialStore(options),
					openWindowsCurrentUserCredentialStore(options),
				]);
				const cancellation = new Error(`cancelled-lock-creator-${index}`);
				let aborted = false;
				let observedCreatedLock = false;
				let siblingPromise: ReturnType<typeof sibling.modify> | undefined;
				const siblingStarted = deferred();
				const creatorSignal = {
					throwIfAborted(): void {
						if (aborted) {
							observedCreatedLock = readdirSync(directory).includes("credentials.json.lock");
							if (!observedCreatedLock) throw new Error("lock-creator cancellation boundary was not reached");
							throw cancellation;
						}
						if (siblingPromise === undefined && readdirSync(directory).includes("credentials.json.lock")) {
							siblingPromise = sibling.modify("deepseek", async () => ({ type: "api_key", key: "sibling" }));
							siblingStarted.resolve();
							queueMicrotask(() => {
								aborted = true;
							});
						}
					},
				} as AbortSignal;

				const creatorPromise = creator.read("deepseek", { signal: creatorSignal });
				await siblingStarted.promise;
				if (siblingPromise === undefined) throw new Error("sibling first write was not started");
				const [creatorResult, siblingResult] = await Promise.allSettled([creatorPromise, siblingPromise]);
				expect(creatorResult).toEqual({ status: "rejected", reason: cancellation });
				expect(observedCreatedLock).toBe(true);
				expect(siblingResult).toEqual({
					status: "fulfilled",
					value: { type: "api_key", key: "sibling" },
				});
			}),
		);
	},
	TEST_PROGRESS_TIMEOUT_MS * 4,
);

test.runIf(process.platform === "win32")(
	"keeps one lock domain when a lock creator is cancelled after another instance acquires its file",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const lockPath = `${filePath}.lock`;
		const options = {
			filePath,
			limits: smallTestLimits({ lockWaitMs: TEST_PROGRESS_TIMEOUT_MS }),
		} as const;
		const [creator, follower] = await Promise.all([
			openWindowsCurrentUserCredentialStore(options),
			openWindowsCurrentUserCredentialStore(options),
		]);
		const cancellation = new Error("cancelled-after-sibling-lock-acquisition");
		let siblingLockDescriptor: number | undefined;
		const creatorSignal = {
			throwIfAborted(): void {
				if (siblingLockDescriptor !== undefined) throw cancellation;
				if (!readdirSync(directory).includes("credentials.json.lock")) return;
				siblingLockDescriptor = openSync(lockPath, "r+");
				flockSync(siblingLockDescriptor, "exnb");
				throw cancellation;
			},
		} as AbortSignal;

		await expect(creator.read("deepseek", { signal: creatorSignal })).rejects.toBe(cancellation);
		if (siblingLockDescriptor === undefined) throw new Error("sibling did not acquire the created lock file");

		let followerEntered = false;
		const followerWrite = follower.modify("deepseek", async () => {
			followerEntered = true;
			return { type: "api_key", key: "follower" };
		});
		try {
			await new Promise<void>((resolve) => setTimeout(resolve, options.limits.lockRetryDelayMs * 3));
			expect(followerEntered).toBe(false);
		} finally {
			flockSync(siblingLockDescriptor, "un");
			closeSync(siblingLockDescriptor);
		}
		await expect(followerWrite).resolves.toEqual({ type: "api_key", key: "follower" });
	},
	TEST_PROGRESS_TIMEOUT_MS * 2,
);

test.runIf(process.platform === "win32")(
	"keeps distinct provider identifiers that UTF-8 would encode with the same replacement character",
	async () => {
		const directory = await createTemporaryDirectory();
		const store = await openWindowsCurrentUserCredentialStore({
			filePath: join(directory, "credentials.json"),
			limits: testLimits(),
		});
		const unpairedHighSurrogate = String.fromCharCode(0xd800);
		const replacementCharacter = "�";

		await store.modify(unpairedHighSurrogate, async () => ({ type: "api_key", key: "surrogate" }));
		await expect(store.read(replacementCharacter)).resolves.toBeUndefined();
		await store.modify(replacementCharacter, async () => ({ type: "api_key", key: "replacement" }));
		await expect(store.read(unpairedHighSurrogate)).resolves.toEqual({ type: "api_key", key: "surrogate" });
		await expect(store.read(replacementCharacter)).resolves.toEqual({ type: "api_key", key: "replacement" });
	},
);

test.runIf(process.platform === "win32")(
	"honors cancellation before lock creation and after the update callback but before publication",
	async () => {
		const directory = await createTemporaryDirectory();
		const store = await openWindowsCurrentUserCredentialStore({
			filePath: join(directory, "credentials.json"),
			limits: smallTestLimits(),
		});

		const preCancelled = new AbortController();
		const preCancelledReason = new Error("pre-cancelled");
		preCancelled.abort(preCancelledReason);
		let preCancelledCallbackEntered = false;
		await expect(
			store.modify(
				"deepseek",
				async () => {
					preCancelledCallbackEntered = true;
					return { type: "api_key", key: "forbidden" };
				},
				{ signal: preCancelled.signal },
			),
		).rejects.toBe(preCancelledReason);
		expect(preCancelledCallbackEntered).toBe(false);
		await expect(readdir(directory)).resolves.toEqual([]);

		const duringCallback = new AbortController();
		const duringCallbackReason = new Error("cancelled-before-publication");
		const entered = deferred();
		const release = deferred();
		const pending = store.modify(
			"deepseek",
			async () => {
				entered.resolve();
				await release.promise;
				return { type: "api_key", key: "forbidden" };
			},
			{ signal: duringCallback.signal },
		);
		await entered.promise;
		duringCallback.abort(duringCallbackReason);
		release.resolve();
		await expect(pending).rejects.toBe(duringCallbackReason);
		expect(await readdir(directory)).not.toContain("credentials.json");
	},
);

test.runIf(process.platform === "win32")(
	"does not create the lock when cancellation occurs while root validation is pending",
	async () => {
		const directory = await createTemporaryDirectory();
		const store = await openWindowsCurrentUserCredentialStore({
			filePath: join(directory, "credentials.json"),
			limits: smallTestLimits(),
		});
		const cancellation = new Error("cancelled-after-root-validation");
		const cancellationController = new AbortController();

		const pending = store.read("deepseek", { signal: cancellationController.signal });
		queueMicrotask(() => cancellationController.abort(cancellation));
		await expect(pending).rejects.toBe(cancellation);
		await expect(readdir(directory)).resolves.toEqual([]);
	},
);

test.runIf(process.platform === "win32")(
	"rejects replacement of the loaded data file during an update callback before publication",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const displacedPath = join(directory, "displaced-credentials.json");
		const replacement = "caller-must-not-overwrite-this-replacement";
		const store = await openWindowsCurrentUserCredentialStore({ filePath, limits: smallTestLimits() });
		await store.modify("deepseek", async () => ({ type: "api_key", key: "baseline" }));
		const baseline = await readFile(filePath, "utf8");

		const failure = await store
			.modify("deepseek", async (current) => {
				expect(current).toEqual({ type: "api_key", key: "baseline" });
				await rename(filePath, displacedPath);
				await writeFile(filePath, replacement, "utf8");
				return { type: "api_key", key: "must-not-publish" };
			})
			.catch((error: unknown) => error);
		expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
		await expect(readFile(filePath, "utf8")).resolves.toBe(replacement);
		await expect(readFile(displacedPath, "utf8")).resolves.toBe(baseline);
	},
);

test.runIf(process.platform === "win32")(
	"rejects replacement of the held lock file during an update callback before publication",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const lockPath = `${filePath}.lock`;
		const displacedLockPath = join(directory, "displaced-credentials.lock");
		const store = await openWindowsCurrentUserCredentialStore({ filePath, limits: smallTestLimits() });
		await store.modify("deepseek", async () => ({ type: "api_key", key: "baseline" }));
		const baseline = await readFile(filePath, "utf8");

		const failure = await store
			.modify("deepseek", async () => {
				await rename(lockPath, displacedLockPath);
				await writeFile(lockPath, "replacement-lock", "utf8");
				return { type: "api_key", key: "must-not-publish" };
			})
			.catch((error: unknown) => error);
		expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
		await expect(readFile(filePath, "utf8")).resolves.toBe(baseline);
		await expect(readFile(lockPath, "utf8")).resolves.toBe("replacement-lock");
	},
);

test.runIf(process.platform === "win32")(
	"does not retry into a replacement lock domain observed by the same operation",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const lockPath = `${filePath}.lock`;
		const displacedLockPath = join(directory, "displaced-credentials.lock");
		const options = {
			filePath,
			limits: smallTestLimits({ lockWaitMs: TEST_PROGRESS_TIMEOUT_MS }),
		} as const;
		const [owner, follower] = await Promise.all([
			openWindowsCurrentUserCredentialStore(options),
			openWindowsCurrentUserCredentialStore(options),
		]);
		await owner.modify("deepseek", async () => ({ type: "api_key", key: "baseline" }));
		const baseline = await readFile(filePath, "utf8");
		const ownerEntered = deferred();
		const releaseOwner = deferred();
		const ownerWrite = owner.modify("deepseek", async () => {
			ownerEntered.resolve();
			await releaseOwner.promise;
			return { type: "api_key", key: "owner-must-not-publish" };
		});
		const ownerResultPromise = ownerWrite.catch((error: unknown) => error);
		await ownerEntered.promise;

		const controller = new AbortController();
		const followerWaiting = deferred();
		const addEventListener = controller.signal.addEventListener.bind(controller.signal);
		const observeAbortSubscription = (...args: Parameters<typeof addEventListener>): void => {
			if (args[0] === "abort") followerWaiting.resolve();
			addEventListener(...args);
		};
		Object.defineProperty(controller.signal, "addEventListener", {
			value: observeAbortSubscription,
		});
		let followerEntered = false;
		const followerResultPromise = follower
			.modify(
				"deepseek",
				async () => {
					followerEntered = true;
					return { type: "api_key", key: "follower-must-not-publish" };
				},
				{ signal: controller.signal },
			)
			.catch((error: unknown) => error);
		await followerWaiting.promise;
		renameSync(lockPath, displacedLockPath);
		writeFileSync(lockPath, "replacement-lock", "utf8");
		releaseOwner.resolve();
		const followerResult = await followerResultPromise;

		expect(followerEntered).toBe(false);
		expect(followerResult).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((followerResult as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
		const ownerResult = await ownerResultPromise;
		expect(ownerResult).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((ownerResult as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
		await expect(readFile(filePath, "utf8")).resolves.toBe(baseline);
	},
	TEST_PROGRESS_TIMEOUT_MS * 2,
);

test.runIf(process.platform === "win32")(
	"maps disappearance of the held lock file to a redacted storage-changed error",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const lockPath = `${filePath}.lock`;
		const store = await openWindowsCurrentUserCredentialStore({ filePath, limits: smallTestLimits() });
		await store.modify("deepseek", async () => ({ type: "api_key", key: "baseline" }));
		const baseline = await readFile(filePath, "utf8");

		const failure = await store
			.modify("deepseek", async () => {
				await unlink(lockPath);
				return { type: "api_key", key: "must-not-publish" };
			})
			.catch((error: unknown) => error);
		expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
		expect((failure as Error).message).not.toContain(directory);
		await expect(readFile(filePath, "utf8")).resolves.toBe(baseline);
	},
);

test.runIf(process.platform === "win32")(
	"does not publish when cancellation becomes observable at the final publication boundary",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const store = await openWindowsCurrentUserCredentialStore({
			filePath,
			limits: smallTestLimits(),
		});
		await store.modify("deepseek", async () => ({ type: "api_key", key: "baseline" }));
		const baseline = await readFile(filePath, "utf8");

		const cancellation = new Error("cancelled-at-publication-boundary");
		let observedTemporaryFile = false;
		const boundarySignal = {
			throwIfAborted(): void {
				if (!readdirSync(directory).some((name) => name.endsWith(".tmp"))) return;
				observedTemporaryFile = true;
				throw cancellation;
			},
		} as AbortSignal;
		await expect(
			store.modify("deepseek", async () => ({ type: "api_key", key: "must-not-publish" }), {
				signal: boundarySignal,
			}),
		).rejects.toBe(cancellation);
		expect(observedTemporaryFile).toBe(true);
		await expect(readFile(filePath, "utf8")).resolves.toBe(baseline);
		await expect(store.read("deepseek")).resolves.toEqual({ type: "api_key", key: "baseline" });
	},
);

test.runIf(process.platform === "win32")(
	"rejects replacement of the created temporary file before publication",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const displacedTemporaryPath = join(directory, "displaced-temporary-file");
		const store = await openWindowsCurrentUserCredentialStore({ filePath, limits: smallTestLimits() });
		await store.modify("deepseek", async () => ({ type: "api_key", key: "baseline" }));
		const baseline = await readFile(filePath, "utf8");
		let replaced = false;
		let replacementTemporaryPath: string | undefined;
		const boundarySignal = {
			throwIfAborted(): void {
				if (replaced) return;
				const temporaryName = readdirSync(directory).find((name) => name.endsWith(".tmp"));
				if (temporaryName === undefined) return;
				try {
					replacementTemporaryPath = join(directory, temporaryName);
					renameSync(replacementTemporaryPath, displacedTemporaryPath);
					writeFileSync(replacementTemporaryPath, "replacement-temporary-file", "utf8");
					replaced = true;
				} catch (error) {
					if (
						error instanceof Error &&
						"code" in error &&
						(error.code === "EACCES" || error.code === "EBUSY" || error.code === "EPERM")
					) {
						return;
					}
					throw error;
				}
			},
		} as AbortSignal;

		const failure = await store
			.modify("deepseek", async () => ({ type: "api_key", key: "must-not-publish" }), {
				signal: boundarySignal,
			})
			.catch((error: unknown) => error);
		expect(replaced).toBe(true);
		expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
		await expect(readFile(filePath, "utf8")).resolves.toBe(baseline);
		if (replacementTemporaryPath === undefined) throw new Error("temporary replacement path was not captured");
		await expect(readFile(replacementTemporaryPath, "utf8")).resolves.toBe("replacement-temporary-file");
	},
);

test.runIf(process.platform === "win32")("does not delete the store when its queued delete is cancelled", async () => {
	const directory = await createTemporaryDirectory();
	const filePath = join(directory, "credentials.json");
	const seed = await openWindowsCurrentUserCredentialStore({ filePath, limits: smallTestLimits() });
	await seed.modify("deepseek", async () => ({ type: "api_key", key: "must-remain" }));
	const { store, blockingWrite, release } = await holdStoreLock(filePath);
	const cancellationController = new AbortController();
	const cancellation = new Error("cancelled-queued-delete");
	const pendingDelete = store.delete("deepseek", { signal: cancellationController.signal });
	cancellationController.abort(cancellation);
	release();

	await expect(pendingDelete).rejects.toBe(cancellation);
	await expect(blockingWrite).resolves.toEqual({ type: "api_key", key: "must-remain" });
	await expect(store.read("deepseek")).resolves.toEqual({ type: "api_key", key: "must-remain" });
});

test.runIf(process.platform === "win32")(
	"round-trips OAuth extension data and preserves an update callback failure without writing",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const store = await openWindowsCurrentUserCredentialStore({
			filePath,
			limits: testLimits(),
		});
		const credential = {
			type: "oauth" as const,
			refresh: "refresh-secret",
			access: "access-secret",
			expires: 1_900_000_000_000,
			account: { id: "account-id", scopes: ["models:read", "chat:write"] },
		};
		await store.modify("oauth-provider", async () => credential);
		await expect(store.read("oauth-provider")).resolves.toEqual(credential);
		await expect(store.list()).resolves.toEqual([{ providerId: "oauth-provider", type: "oauth" }]);
		const persisted = await readFile(filePath, "utf8");
		const callbackFailure = new Error("caller-owned-failure");

		await expect(
			store.modify("oauth-provider", async () => {
				throw callbackFailure;
			}),
		).rejects.toBe(callbackFailure);
		await expect(readFile(filePath, "utf8")).resolves.toBe(persisted);
	},
);

test.runIf(process.platform === "win32")(
	"preserves a JSON __proto__ OAuth extension as data without changing the credential prototype",
	async () => {
		const directory = await createTemporaryDirectory();
		const store = await openWindowsCurrentUserCredentialStore({
			filePath: join(directory, "credentials.json"),
			limits: testLimits({
				maxEntries: 1,
			}),
		});
		const credential = {
			type: "oauth" as const,
			refresh: "refresh-secret",
			access: "access-secret",
			expires: 1_900_000_000_000,
		};
		Object.defineProperty(credential, "__proto__", {
			value: { marker: "ordinary-json-data" },
			enumerable: true,
			configurable: true,
			writable: true,
		});

		const written = await store.modify("oauth-provider", async () => credential);
		expect(Object.getPrototypeOf(written)).toBe(Object.prototype);
		expect(Object.hasOwn(written ?? {}, "__proto__")).toBe(true);
		expect(Object.getOwnPropertyDescriptor(written, "__proto__")?.value).toEqual({ marker: "ordinary-json-data" });
		const restored = await store.read("oauth-provider");
		expect(Object.getPrototypeOf(restored)).toBe(Object.prototype);
		expect(Object.hasOwn(restored ?? {}, "__proto__")).toBe(true);
		expect(Object.getOwnPropertyDescriptor(restored, "__proto__")?.value).toEqual({ marker: "ordinary-json-data" });
	},
);

test.runIf(process.platform === "win32")(
	"enforces host entry, credential-byte, store-byte, and JSON-depth budgets before mutation",
	async () => {
		const directory = await createTemporaryDirectory();
		const common = {
			maxEntries: 1,
			maxCredentialBytes: 2_048,
			maxStoreBytes: 8_192,
			maxCredentialJsonDepth: 6,
			maxDirectoryEntriesToScan: 64,
			lockWaitMs: 1_000,
			lockRetryDelayMs: 10,
		} as const;
		const entryFile = join(directory, "entry-limit.json");
		const entryStore = await openWindowsCurrentUserCredentialStore({ filePath: entryFile, limits: common });
		await entryStore.modify("first", async () => ({ type: "api_key", key: "kept" }));
		const beforeEntryFailure = await readFile(entryFile, "utf8");

		const failures = [
			await entryStore
				.modify("second", async () => ({ type: "api_key", key: "forbidden" }))
				.catch((error: unknown) => error),
			await (
				await openWindowsCurrentUserCredentialStore({
					filePath: join(directory, "credential-byte-limit.json"),
					limits: { ...common, maxCredentialBytes: 1 },
				})
			)
				.modify("deepseek", async () => ({ type: "api_key", key: "too-large" }))
				.catch((error: unknown) => error),
			await (
				await openWindowsCurrentUserCredentialStore({
					filePath: join(directory, "store-byte-limit.json"),
					limits: { ...common, maxStoreBytes: 1 },
				})
			)
				.modify("deepseek", async () => ({ type: "api_key", key: "too-large-on-disk" }))
				.catch((error: unknown) => error),
		];
		for (const failure of failures) {
			expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
			expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("capacity");
		}

		const depthStore = await openWindowsCurrentUserCredentialStore({
			filePath: join(directory, "depth-limit.json"),
			limits: { ...common, maxCredentialJsonDepth: 2 },
		});
		const depthFailure = await depthStore
			.modify("oauth-provider", async () => ({
				type: "oauth",
				refresh: "refresh",
				access: "access",
				expires: 1,
				metadata: { nested: { value: "too-deep" } },
			}))
			.catch((error: unknown) => error);
		expect(depthFailure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((depthFailure as WindowsCurrentUserCredentialStoreError).code).toBe("capacity");
		const wrapperDepthFailure = await depthStore
			.modify("api-provider", async () => ({
				type: "api_key",
				key: "key",
				env: { ACCOUNT: "would-create-a-third-payload-level" },
			}))
			.catch((error: unknown) => error);
		expect(wrapperDepthFailure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((wrapperDepthFailure as WindowsCurrentUserCredentialStoreError).code).toBe("capacity");

		await expect(readFile(entryFile, "utf8")).resolves.toBe(beforeEntryFailure);
		expect(await readdir(directory)).not.toContain("credential-byte-limit.json");
		expect(await readdir(directory)).not.toContain("store-byte-limit.json");
		expect(await readdir(directory)).not.toContain("depth-limit.json");
	},
);

test.runIf(process.platform === "win32")("reports a zero credential-byte budget as exhausted capacity", async () => {
	const directory = await createTemporaryDirectory();
	const store = await openWindowsCurrentUserCredentialStore({
		filePath: join(directory, "credentials.json"),
		limits: smallTestLimits({
			maxCredentialBytes: 0,
		}),
	});

	const failure = await store
		.modify("deepseek", async () => ({ type: "api_key", key: "cannot-fit" }))
		.catch((error: unknown) => error);
	expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
	expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("capacity");
	expect(await readdir(directory)).not.toContain("credentials.json");
});

test.runIf(process.platform === "win32")(
	"detects root replacement when distinct Windows file identities collide after Number conversion",
	async ({ skip }) => {
		const base = await createTemporaryDirectory();
		const collision = await findNumberCollidingDirectories(base);
		if (collision === undefined) {
			return skip("the test volume did not expose a rounded file-identity collision within the probe budget");
		}
		const { original, replacement } = collision;
		const store = await openWindowsCurrentUserCredentialStore({
			filePath: join(original, "credentials.json"),
			limits: smallTestLimits(),
		});
		await rename(original, join(base, "displaced"));
		await rename(replacement, original);

		const failure = await store.read("deepseek").catch((error: unknown) => error);
		expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
	},
	TEST_PROGRESS_TIMEOUT_MS * 2,
);

test.runIf(process.platform === "win32")(
	"rejects a junction-backed root and detects replacement of an opened root directory",
	async () => {
		const base = await createTemporaryDirectory();
		const realRoot = join(base, "real");
		const junctionRoot = join(base, "junction");
		await mkdir(realRoot);
		await symlink(realRoot, junctionRoot, "junction");
		const limits = smallTestLimits();

		const junctionFailure = await openWindowsCurrentUserCredentialStore({
			filePath: join(junctionRoot, "credentials.json"),
			limits,
		}).catch((error: unknown) => error);
		expect(junctionFailure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((junctionFailure as WindowsCurrentUserCredentialStoreError).code).toBe("invalid_configuration");

		const store = await openWindowsCurrentUserCredentialStore({
			filePath: join(realRoot, "credentials.json"),
			limits,
		});
		await rename(realRoot, join(base, "moved"));
		await mkdir(realRoot);
		const replacementFailure = await store.read("deepseek").catch((error: unknown) => error);
		expect(replacementFailure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((replacementFailure as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
	},
);

test.runIf(process.platform === "win32")(
	"rejects a structurally invalid lock path as storage changed without exhausting lock contention",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const store = await openWindowsCurrentUserCredentialStore({
			filePath,
			limits: smallTestLimits({ lockWaitMs: 25, lockRetryDelayMs: 5 }),
		});
		await mkdir(`${filePath}.lock`);

		const failure = await store.read("deepseek").catch((error: unknown) => error);
		expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
		expect((failure as Error).message).not.toContain(directory);
	},
);

test.runIf(process.platform === "win32")(
	"removes only crash-left atomic temporary files while holding the store lock",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const store = await openWindowsCurrentUserCredentialStore({
			filePath,
			limits: smallTestLimits(),
		});
		await store.modify("deepseek", async () => ({ type: "api_key", key: "survives" }));
		const staleName = ".credentials.json.00000000-0000-4000-8000-000000000000.tmp";
		const nonAdapterUuidName = ".credentials.json.00000000-0000-0000-0000-000000000000.tmp";
		await writeFile(join(directory, staleName), "interrupted-write", "utf8");
		await writeFile(join(directory, nonAdapterUuidName), "keep", "utf8");
		await writeFile(join(directory, "unrelated.tmp"), "keep", "utf8");

		await expect(store.read("deepseek")).resolves.toEqual({ type: "api_key", key: "survives" });
		await expect(readdir(directory)).resolves.toEqual(
			expect.arrayContaining(["credentials.json", "credentials.json.lock", nonAdapterUuidName, "unrelated.tmp"]),
		);
		expect(await readdir(directory)).not.toContain(staleName);
	},
);

test.runIf(process.platform === "win32")(
	"bounds the application-data directory scan before removing any temporary file",
	async () => {
		const directory = await createTemporaryDirectory();
		const staleName = ".credentials.json.00000000-0000-4000-8000-000000000000.tmp";
		await writeFile(join(directory, staleName), "interrupted-write", "utf8");
		await writeFile(join(directory, "unrelated.tmp"), "keep", "utf8");
		const store = await openWindowsCurrentUserCredentialStore({
			filePath: join(directory, "credentials.json"),
			limits: smallTestLimits({
				maxDirectoryEntriesToScan: 2,
			}),
		});

		const failure = await store.read("deepseek").catch((error: unknown) => error);
		expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("capacity");
		await expect(readdir(directory)).resolves.toEqual(
			expect.arrayContaining([staleName, "unrelated.tmp", "credentials.json.lock"]),
		);
	},
);

test.runIf(process.platform === "win32")(
	"retains a crash-left temporary file when the queued cleanup is cancelled",
	async () => {
		const directory = await createTemporaryDirectory();
		const staleName = ".credentials.json.00000000-0000-4000-8000-000000000000.tmp";
		const { store, blockingWrite, release } = await holdStoreLock(join(directory, "credentials.json"), async () => {
			await writeFile(join(directory, staleName), "interrupted-write", "utf8");
		});
		const cancellationController = new AbortController();
		const cancellation = new Error("cancelled-queued-temporary-cleanup");
		const pendingRead = store.read("deepseek", { signal: cancellationController.signal });
		cancellationController.abort(cancellation);
		release();

		await expect(pendingRead).rejects.toBe(cancellation);
		await expect(blockingWrite).resolves.toEqual({ type: "api_key", key: "blocker" });
		await expect(readdir(directory)).resolves.toContain(staleName);
	},
);

test.runIf(process.platform === "win32")(
	"fails every credential operation closed when a ciphertext loses DPAPI integrity",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const store = await openWindowsCurrentUserCredentialStore({
			filePath,
			limits: testLimits({
				maxCredentialBytes: 2_048,
				maxStoreBytes: 8_192,
			}),
		});
		const secret = "must-never-appear-in-an-error";
		await store.modify("deepseek", async () => ({ type: "api_key", key: secret }));

		const envelope = JSON.parse(await readFile(filePath, "utf8")) as {
			entries: Record<string, { ciphertext: string; entropy: string }>;
			version: number;
		};
		const entry = Object.values(envelope.entries)[0];
		expect(entry).toBeDefined();
		if (entry === undefined) throw new Error("missing fixture entry");
		entry.ciphertext = `${entry.ciphertext[0] === "A" ? "B" : "A"}${entry.ciphertext.slice(1)}`;
		const corrupted = JSON.stringify(envelope);
		await writeFile(filePath, corrupted, "utf8");

		let callbackEntered = false;
		const operations = [
			() => store.read("deepseek"),
			() => store.list(),
			() =>
				store.modify("deepseek", async () => {
					callbackEntered = true;
					return { type: "api_key" };
				}),
			() => store.delete("deepseek"),
		];
		for (const operation of operations) {
			const failure = await operation().catch((error: unknown) => error);
			expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
			expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("corrupt");
			expect(String(failure)).not.toContain("deepseek");
			expect(String(failure)).not.toContain(secret);
			expect(String(failure)).not.toContain(filePath);
		}
		expect(callbackEntered).toBe(false);
		await expect(readFile(filePath, "utf8")).resolves.toBe(corrupted);
	},
);

test.runIf(process.platform === "win32")(
	"rejects an otherwise valid store whose entry keys are not in canonical order",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const store = await openWindowsCurrentUserCredentialStore({ filePath, limits: testLimits() });
		await store.modify("first", async () => ({ type: "api_key", key: "first-secret" }));
		await store.modify("second", async () => ({ type: "api_key", key: "second-secret" }));
		const envelope = JSON.parse(await readFile(filePath, "utf8")) as {
			entries: Record<string, { ciphertext: string; entropy: string }>;
			version: number;
		};
		const canonicalKeys = Object.keys(envelope.entries);
		const reversedEntries = Object.fromEntries(Object.entries(envelope.entries).reverse());
		expect(Object.keys(reversedEntries)).toEqual([...canonicalKeys].reverse());
		await writeFile(filePath, JSON.stringify({ version: envelope.version, entries: reversedEntries }), "utf8");

		const failure = await store.list().catch((error: unknown) => error);
		expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("corrupt");
	},
);

test.runIf(process.platform === "win32")(
	"bounds lock waiting and cancels a queued mutation before its callback",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const holder = await openWindowsCurrentUserCredentialStore({
			filePath,
			limits: smallTestLimits({
				lockRetryDelayMs: 5,
			}),
		});
		const contender = await openWindowsCurrentUserCredentialStore({
			filePath,
			limits: smallTestLimits({
				lockWaitMs: 25,
				lockRetryDelayMs: 5,
			}),
		});
		const entered = deferred();
		const release = deferred();
		const held = holder.modify("deepseek", async () => {
			entered.resolve();
			await release.promise;
			return { type: "api_key", key: "held" };
		});
		await entered.promise;

		let timedCallbackEntered = false;
		const timedFailure = await contender
			.modify("deepseek", async () => {
				timedCallbackEntered = true;
				return { type: "api_key", key: "forbidden" };
			})
			.catch((error: unknown) => error);
		expect(timedCallbackEntered).toBe(false);
		expect(timedFailure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
		expect((timedFailure as WindowsCurrentUserCredentialStoreError).code).toBe("locked");

		const controller = new AbortController();
		const cancellation = new Error("cancelled-by-host");
		let cancelledCallbackEntered = false;
		const cancelled = contender.modify(
			"deepseek",
			async () => {
				cancelledCallbackEntered = true;
				return { type: "api_key", key: "forbidden" };
			},
			{ signal: controller.signal },
		);
		controller.abort(cancellation);
		await expect(cancelled).rejects.toBe(cancellation);
		expect(cancelledCallbackEntered).toBe(false);

		release.resolve();
		await held;
		await expect(holder.read("deepseek")).resolves.toEqual({ type: "api_key", key: "held" });
	},
);

test.runIf(process.platform === "win32")("serializes modify callbacks across independent store instances", async () => {
	const directory = await createTemporaryDirectory();
	const options = {
		filePath: join(directory, "credentials.json"),
		limits: testLimits({
			maxCredentialBytes: 1_024,
			maxStoreBytes: 8_192,
			maxCredentialJsonDepth: 4,
		}),
	} as const;
	const firstStore = await openWindowsCurrentUserCredentialStore(options);
	const secondStore = await openWindowsCurrentUserCredentialStore(options);
	const firstEntered = deferred();
	const releaseFirst = deferred();
	let secondEntered = false;

	const firstWrite = firstStore.modify("deepseek", async () => {
		firstEntered.resolve();
		await releaseFirst.promise;
		return { type: "api_key", key: "first" };
	});
	await firstEntered.promise;
	const secondWrite = secondStore.modify("deepseek", async (current) => {
		secondEntered = true;
		expect(current).toEqual({ type: "api_key", key: "first" });
		return { type: "api_key", key: "second" };
	});
	await new Promise<void>((resolve) => setTimeout(resolve, options.limits.lockRetryDelayMs * 3));
	expect(secondEntered).toBe(false);

	releaseFirst.resolve();
	await expect(firstWrite).resolves.toEqual({ type: "api_key", key: "first" });
	await expect(secondWrite).resolves.toEqual({ type: "api_key", key: "second" });
	await expect(firstStore.read("deepseek")).resolves.toEqual({ type: "api_key", key: "second" });
});

test.runIf(process.platform === "win32")(
	"uses fresh protection for each accepted write and leaves storage unchanged for an undefined update",
	async () => {
		const directory = await createTemporaryDirectory();
		const filePath = join(directory, "credentials.json");
		const store = await openWindowsCurrentUserCredentialStore({
			filePath,
			limits: smallTestLimits(),
		});
		const credential = { type: "api_key" as const, key: "same-secret" };

		await store.modify("volcengine", async () => credential);
		const firstWrite = await readFile(filePath, "utf8");
		await store.modify("volcengine", async () => credential);
		const secondWrite = await readFile(filePath, "utf8");
		const [firstProtected] = Object.values(
			(JSON.parse(firstWrite) as { entries: Record<string, { entropy: string; ciphertext: string }> }).entries,
		);
		const [secondProtected] = Object.values(
			(JSON.parse(secondWrite) as { entries: Record<string, { entropy: string; ciphertext: string }> }).entries,
		);
		expect(firstProtected).toBeDefined();
		expect(secondProtected).toBeDefined();
		expect(Buffer.from(firstProtected?.entropy ?? "", "base64")).toHaveLength(32);
		expect(Buffer.from(secondProtected?.entropy ?? "", "base64")).toHaveLength(32);
		expect(secondProtected?.entropy).not.toBe(firstProtected?.entropy);
		expect(secondProtected?.ciphertext).not.toBe(firstProtected?.ciphertext);

		await expect(store.modify("volcengine", async () => undefined)).resolves.toEqual(credential);
		await expect(readFile(filePath, "utf8")).resolves.toBe(secondWrite);
	},
);

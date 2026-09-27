import { readdirSync } from "node:fs";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expect, test, vi } from "vitest";

const cancellationBoundary = vi.hoisted(() => ({
	abort: undefined as (() => void) | undefined,
	armed: false,
	rootPath: undefined as string | undefined,
	atomicValidationStarted: false,
	temporaryFileObserved: false,
}));

vi.mock("node:fs/promises", async (importOriginal) => {
	const original = await importOriginal<typeof import("node:fs/promises")>();
	return {
		...original,
		lstat: async (...args: Parameters<typeof original.lstat>) => {
			const [path] = args;
			if (cancellationBoundary.armed && String(path) === cancellationBoundary.rootPath) {
				cancellationBoundary.atomicValidationStarted = true;
			}
			if (cancellationBoundary.atomicValidationStarted && String(path).endsWith(".lock")) {
				cancellationBoundary.armed = false;
				cancellationBoundary.atomicValidationStarted = false;
				cancellationBoundary.abort?.();
			}
			return original.lstat(...args);
		},
	};
});

import { openWindowsCurrentUserCredentialStore } from "../src/index.js";
import { WINDOWS_CREDENTIAL_TEST_LIMITS } from "./fixtures/windows-store-limits.js";

test.runIf(process.platform === "win32")(
	"does not create a temporary file when cancellation occurs during final identity validation",
	async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-credentials-windows-cancel-"));
		try {
			const filePath = join(directory, "credentials.json");
			cancellationBoundary.rootPath = directory;
			const store = await openWindowsCurrentUserCredentialStore({
				filePath,
				limits: WINDOWS_CREDENTIAL_TEST_LIMITS,
			});
			await store.modify("deepseek", async () => ({ type: "api_key", key: "baseline" }));
			const baseline = await readFile(filePath, "utf8");
			const controller = new AbortController();
			const cancellation = new Error("cancelled-during-final-identity-validation");
			const throwIfAborted = controller.signal.throwIfAborted.bind(controller.signal);
			Object.defineProperty(controller.signal, "throwIfAborted", {
				value: (): void => {
					if (controller.signal.aborted && readdirSync(directory).some((name) => name.endsWith(".tmp"))) {
						cancellationBoundary.temporaryFileObserved = true;
					}
					throwIfAborted();
				},
			});
			cancellationBoundary.abort = () => controller.abort(cancellation);

			await expect(
				store.modify(
					"deepseek",
					async () => {
						cancellationBoundary.armed = true;
						return { type: "api_key", key: "must-not-write" };
					},
					{ signal: controller.signal },
				),
			).rejects.toBe(cancellation);
			expect(cancellationBoundary.temporaryFileObserved).toBe(false);
			await expect(readFile(filePath, "utf8")).resolves.toBe(baseline);
		} finally {
			cancellationBoundary.abort = undefined;
			cancellationBoundary.armed = false;
			cancellationBoundary.rootPath = undefined;
			cancellationBoundary.atomicValidationStarted = false;
			cancellationBoundary.temporaryFileObserved = false;
			await rm(directory, { recursive: true, force: true });
		}
	},
);

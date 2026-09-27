import { mkdir, mkdtemp, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expect, test, vi } from "vitest";

const nativeAttributes = vi.hoisted(() => new Map<string, number>());

vi.mock("koffi", () => ({
	default: {
		load: () => ({
			func:
				() =>
				(path: string): number =>
					nativeAttributes.get(path) ?? 0,
		}),
	},
}));

import { openWindowsCurrentUserCredentialStore, WindowsCurrentUserCredentialStoreError } from "../src/index.js";
import { WINDOWS_CREDENTIAL_TEST_LIMITS } from "./fixtures/windows-store-limits.js";

const FILE_ATTRIBUTE_REPARSE_POINT = 0x0000_0400;

test.runIf(process.platform === "win32")(
	"rejects a raw reparse attribute on an ancestor before opening and before a later write",
	async () => {
		const base = await mkdtemp(join(tmpdir(), "oga-credentials-windows-reparse-"));
		try {
			const ancestor = join(base, "mounted-ancestor");
			const root = join(ancestor, "app-data");
			await mkdir(root, { recursive: true });
			const options = {
				filePath: join(root, "credentials.json"),
				limits: WINDOWS_CREDENTIAL_TEST_LIMITS,
			} as const;

			nativeAttributes.set(ancestor, FILE_ATTRIBUTE_REPARSE_POINT);
			const configurationFailure = await openWindowsCurrentUserCredentialStore(options).catch(
				(error: unknown) => error,
			);
			expect(configurationFailure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
			expect((configurationFailure as WindowsCurrentUserCredentialStoreError).code).toBe("invalid_configuration");
			await expect(readdir(root)).resolves.toEqual([]);

			nativeAttributes.clear();
			const store = await openWindowsCurrentUserCredentialStore(options);
			nativeAttributes.set(ancestor, FILE_ATTRIBUTE_REPARSE_POINT);
			let callbackEntered = false;
			const storageFailure = await store
				.modify("deepseek", async () => {
					callbackEntered = true;
					return { type: "api_key", key: "must-not-write" };
				})
				.catch((error: unknown) => error);
			expect(storageFailure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
			expect((storageFailure as WindowsCurrentUserCredentialStoreError).code).toBe("storage_changed");
			expect(callbackEntered).toBe(false);
			await expect(readdir(root)).resolves.toEqual([]);
		} finally {
			nativeAttributes.clear();
			await rm(base, { recursive: true, force: true });
		}
	},
);

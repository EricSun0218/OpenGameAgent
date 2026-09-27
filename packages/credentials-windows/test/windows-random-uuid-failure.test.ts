import { mkdtemp, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expect, test, vi } from "vitest";

const UUID_FAILURE_SECRET = "native-uuid-failure-must-be-redacted";

vi.mock("node:crypto", async (importOriginal) => ({
	...(await importOriginal<typeof import("node:crypto")>()),
	randomUUID: () => {
		throw new Error(UUID_FAILURE_SECRET);
	},
}));

import { openWindowsCurrentUserCredentialStore, WindowsCurrentUserCredentialStoreError } from "../src/index.js";
import { WINDOWS_CREDENTIAL_TEST_LIMITS } from "./fixtures/windows-store-limits.js";

test.runIf(process.platform === "win32")(
	"maps UUID entropy failure to a redacted storage error before creating data or temporary files",
	async () => {
		const directory = await mkdtemp(join(tmpdir(), "oga-credentials-windows-uuid-"));
		try {
			const filePath = join(directory, "credentials.json");
			const store = await openWindowsCurrentUserCredentialStore({
				filePath,
				limits: WINDOWS_CREDENTIAL_TEST_LIMITS,
			});

			const failure = await store
				.modify("deepseek", async () => ({ type: "api_key", key: "must-not-write" }))
				.catch((error: unknown) => error);
			expect(failure).toBeInstanceOf(WindowsCurrentUserCredentialStoreError);
			expect((failure as WindowsCurrentUserCredentialStoreError).code).toBe("storage_unavailable");
			expect(String(failure)).not.toContain(UUID_FAILURE_SECRET);
			expect(String(failure)).not.toContain(directory);
			await expect(readdir(directory)).resolves.toEqual(["credentials.json.lock"]);
		} finally {
			await rm(directory, { recursive: true, force: true });
		}
	},
);

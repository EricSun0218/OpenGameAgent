import type { WindowsCurrentUserCredentialStoreLimits } from "../../src/index.js";

export const WINDOWS_CREDENTIAL_TEST_LIMITS: Readonly<WindowsCurrentUserCredentialStoreLimits> = Object.freeze({
	maxEntries: 1,
	maxCredentialBytes: 1_024,
	maxStoreBytes: 8_192,
	maxCredentialJsonDepth: 4,
	maxDirectoryEntriesToScan: 8,
	lockWaitMs: 100,
	lockRetryDelayMs: 5,
});

# `@opengameagent/credentials-windows`

Optional Windows CurrentUser DPAPI persistence for the Pi `CredentialStore` interface used by OpenGameAgent provider
integrations. This is a developer-side storage Adapter for a game's own sidecar. It is not a player credential manager,
character-card importer, provider selector, or login UI.

```ts
import { openWindowsCurrentUserCredentialStore } from "@opengameagent/credentials-windows";
import { createProviderProfileModelStream } from "@opengameagent/integrations";

const credentialReferences = await openWindowsCurrentUserCredentialStore({
  filePath: gamePrivateDataPath("provider-credentials.json"),
  limits: {
    maxEntries: gameCredentialEntryBudget,
    maxCredentialBytes: gameCredentialPayloadByteBudget,
    maxStoreBytes: gameCredentialFileByteBudget,
    maxCredentialJsonDepth: gameCredentialJsonDepthBudget,
    maxDirectoryEntriesToScan: gameCredentialDirectoryEntryBudget,
    lockWaitMs: gameCredentialLockWaitBudget,
    lockRetryDelayMs: gameCredentialLockRetryDelay,
  },
});

const modelStream = createProviderProfileModelStream({
  credentialReferences,
  configuration: gameProviderConfiguration,
  health: gameProviderHealthPolicy,
});
```

Every limit is required. `maxCredentialBytes` bounds the UTF-8 versioned payload containing the provider ID and one
credential. `maxStoreBytes` bounds the complete ciphertext envelope. `maxCredentialJsonDepth` bounds the complete
protected payload, including its version/provider wrapper. `maxDirectoryEntriesToScan` bounds one complete parent-folder
scan before any crash-left temporary file is removed. `lockWaitMs` is the total monotonic cross-process wait budget;
`lockRetryDelayMs` is the interval between nonblocking lock attempts. The framework does not choose product capacities or
timing.

The parent directory must already exist, must not be a filesystem root, and must resolve to its exact addressed path.
Existing data and lock paths must be ordinary files rather than symlinks, junctions, or other reparse points. The Adapter
captures the parent directory identity and rechecks it around operations and atomic publication. It keeps an empty
`<filePath>.lock` coordination file. That file contains no credential data and may remain after a clean or abrupt exit.

Each accepted write encrypts a versioned provider-and-credential payload with Windows DPAPI `CurrentUser` and fresh
32-byte entropy. The file exposes only the format version, SHA-256 provider keys, entropy, and ciphertext. `list()`
decrypts internally but returns only Pi's provider ID and credential type metadata. Data publication writes and flushes a
same-directory temporary file before atomic replacement; a later locked operation removes only temporary files matching
this Adapter's exact name. The Windows kernel file lock is scoped to the open handle, so process exit releases it without
guessing a stale-lock age.

CurrentUser protection normally requires the same Windows user profile on the same machine. It is encryption at rest,
not isolation from malicious code already running as that user. A host that needs a hostile same-account boundary must
also protect the directory with an appropriate ACL or a stronger process/account boundary. Account-password
administrative reset, user-profile loss, or machine migration can make ciphertext unrecoverable; the Adapter reports
that state as `corrupt` and never falls back to plaintext or `LocalMachine` protection.

`read` resolves `undefined` only for a missing provider. Malformed envelopes, wrong entropy/ciphertext, provider-hash
mismatch, and DPAPI integrity failures fail closed. A `modify` callback runs while the cross-process lock is held; its own
rejection is propagated unchanged. Returning `undefined` leaves the entry and file unchanged. Aborting before publication
prevents the credential mutation. Storage errors use stable `WindowsCurrentUserCredentialStoreError.code` values and do
not include provider IDs, credential values, paths, native error text, or ciphertext in their messages. This package does
not log.

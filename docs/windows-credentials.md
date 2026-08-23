# Windows credential persistence

[中文](windows-credentials.zh-CN.md)

`OpenGameAgent.Models.Credentials.Windows` is an optional Windows-only implementation of `IGameCredentialStore` for desktop games, local sidecars, and self-hosted tools. It stores provider credentials with Windows Data Protection API (DPAPI) in `CurrentUser` scope. The package does not choose a provider, download credentials, or add a second authentication system.

```csharp
using OpenGameAgent.Models;
using OpenGameAgent.Models.Credentials.Windows;

var directory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MyGame",
    "OpenGameAgent");
var credentials = new WindowsDpapiGameCredentialStore(
    new WindowsDpapiGameCredentialStoreOptions(directory)
    {
        Capacity = 32,
    });

var authentication = new StoredGameProviderAuthentication(
    providerId: "my-provider",
    store: credentials,
    schemes: new[] { "api-key" },
    login: async (_, interaction, cancellationToken) =>
    {
        var secret = await interaction.PromptAsync!(
            "Provider API key",
            true,
            cancellationToken);
        return new GameCredential(GameCredentialKind.ApiKey, secret);
    });
```

The store has these security and durability properties:

- Every credential payload, including provider/profile identifiers and metadata, is encrypted independently with DPAPI `CurrentUser` and fresh random entropy.
- A versioned JSON document contains only DPAPI ciphertext and random entropy. It never intentionally writes plaintext credentials, hashes of credentials, prompts, or provider responses.
- A cross-instance exclusive lease serializes `SetAsync`, `RemoveAsync`, and the full read/modify/write unit of `ModifyAsync`. Writes use a flushed same-directory temporary file and atomic replacement. An interrupted uncommitted temporary file is discarded on the next operation.
- Capacity, plaintext size, encrypted document size, lock wait, metadata, and key formats are bounded. Corrupt, tampered, wrong-user, or unsupported-version data fails closed and is never interpreted as plaintext.
- Existing directory components and store files are rejected when they are symbolic links or Windows reparse points. The host should still choose a per-user application-data directory with normal Windows ACLs.
- Exceptions do not include secrets, ciphertext, entropy, or credential-derived digests. The package does not log credential material.

`CurrentUser` means another Windows account cannot decrypt the file, but any process already running as the same user can normally invoke DPAPI. This is local at-rest protection, not a sandbox against malware running as the player. Backups restored under another account or a reset Windows profile may be undecryptable; treat that as a re-authentication condition rather than falling back to plaintext.

The public constructor throws `PlatformNotSupportedException` on non-Windows systems. Use the platform's native secure store behind `IGameCredentialStore` on macOS or Linux. A shipped client still cannot protect a permanent developer-owned upstream key from the player; use player BYOK, a local provider, developer-issued short-lived credentials, or a trusted server for that case.

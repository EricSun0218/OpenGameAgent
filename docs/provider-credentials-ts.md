# Provider credentials

`@opengameagent/credentials-keyring` is an optional adapter between OGA's provider credential boundary and the operating-system credential store. On Windows, the native keyring implementation uses the current user's Windows credential service. The package contains no provider keys and never copies a secret into an exception, transcript, event, trace, or model request metadata.

```ts
import { KeyringGameProviderCredentialStore } from "@opengameagent/credentials-keyring";
import { createPiGameModelRegistry } from "@opengameagent/kernel-pi";

const credentials = new KeyringGameProviderCredentialStore({ service: "MyGame.AI" });
await credentials.set("deepseek", { key: playerProvidedKey });

const models = createPiGameModelRegistry({
  credentials,
  profiles,
});
```

Credentials are isolated by host-defined service and provider identifiers. Values use a versioned envelope and revision checks. Removing a credential overwrites its secret with a non-secret revision tombstone, so later compare-and-set operations cannot suffer an ABA version reset. Concurrent access through one store instance is serialized per provider. Corrupt or unavailable OS entries fail closed; there is no plaintext fallback.

This protects credentials at rest from ordinary file disclosure. It cannot hide a permanent developer-owned key from a player who controls the same machine and process. Shipped client games should use player-supplied keys, local models, short-lived developer-issued tokens, or a trusted game service. Server deployments may replace this package with their normal secret manager while retaining the same `GameProviderCredentialSource` boundary.

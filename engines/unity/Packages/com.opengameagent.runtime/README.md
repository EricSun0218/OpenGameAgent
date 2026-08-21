# OpenGameAgent for Unity

Unity 6 adapter for the OpenGameAgent runtime.

## Install

Install the immutable GitHub UPM release from **Window > Package Manager > + > Add package from git URL**:

```text
https://github.com/EricSun0218/OpenGameAgent.git#upm/0.3.0-alpha.3
```

Or install the same release from OpenUPM:

```bash
openupm add com.opengameagent.runtime@0.3.0-alpha.3
```

The `upm/*` tags point to a generated package-only branch containing the tested adapter, runtime/client assemblies, notices, and sample. Do not install the package source subdirectory from `main`; generated DLLs intentionally do not live there.

For local development, build the package with `engines/unity/build-package.ps1` and add the generated `artifacts/unity/com.opengameagent.runtime` directory through Unity Package Manager.

Add `OpenGameAgentBehaviour` to a persistent GameObject and call `Configure(runtime)` for in-process execution or `ConfigureRemote(client)` for a .NET service.

For a no-key first run, import **Minimal Local Agent** from Package Manager > OpenGameAgent > Samples, add `OpenGameAgentQuickstart` to a GameObject, and enter Play Mode. The sample uses a deterministic provider so it never contacts an external service.

The component exposes typed async methods, a JSON start method, actor-scoped steering and abort in local or remote mode, bounded main-thread event delivery with reserved terminal callbacks, cancellation, and teardown safety. Subscribe to `RunEvent`, `RunCompleted`, and `RunFailed` in code or the Inspector. Unity calls `PumpCallbacks()` from `Update`; custom player loops may call it explicitly.

Generated package DLLs belong in the repository-level `artifacts/unity` directory and are not committed.

The base UPM package contains the adapter plus shared runtime/client assemblies. Persistence, provider, official extension, model-catalog, and external-tool packages are versioned separately so each game can choose its deployment surface. A permanent model key embedded in a Unity player can be extracted; use BYOK, a local endpoint, or developer-issued short-lived credentials.

OpenGameAgent itself does not collect or transmit project data. Data leaves the game only through providers or services explicitly configured by the developer. Provider accounts, API terms, usage charges, and data handling are controlled by those third parties. Never put a permanent provider key in a shipped player build.

This package is open source under the MIT License. See `LICENSE.md` and `Third-Party Notices.txt` in the package root.

Full setup, credentials, lifecycle, and main-thread guidance is in the repository's [engine integration guide](https://github.com/EricSun0218/OpenGameAgent/blob/main/docs/engine-integration.md).

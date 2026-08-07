# OpenGameAgent for Unity

Unity 6 adapter for the OpenGameAgent runtime.

1. Build the local UPM package with `engines/unity/build-package.ps1`.
2. Add the generated directory through Unity Package Manager.
3. Add `OpenGameAgentBehaviour` to a persistent GameObject.
4. Call `Configure(runtime)` for in-process execution or `ConfigureRemote(client)` for a .NET service.

The component exposes typed async methods, a JSON start method, actor-scoped steering and abort in local or remote mode, bounded main-thread event delivery with reserved terminal callbacks, cancellation, and teardown safety. Subscribe to `RunEvent`, `RunCompleted`, and `RunFailed` in code or the Inspector. Unity calls `PumpCallbacks()` from `Update`; custom player loops may call it explicitly.

Generated package DLLs belong in the repository-level `artifacts/unity` directory and are not committed.

Full setup, credentials, lifecycle, and main-thread guidance is in the repository's [engine integration guide](https://github.com/EricSun0218/OpenGameAgent/blob/main/docs/engine-integration.md).

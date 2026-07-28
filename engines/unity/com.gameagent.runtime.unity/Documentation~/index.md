# Unity host integration

## Architecture

```text
UnityAgentRuntimeHost (MonoBehaviour)
  ├─ action dispatcher (bounded, authoritative)
  ├─ runtime-event dispatcher (bounded, best effort)
  └─ UnityAgentRuntimeFacade
       ├─ IUnityDurableAgentRuntimeBackend
       │    └─ shared GameAgent.Core.IDurableAgentRuntime
       └─ headless compatibility backend
            └─ shared GameAgent.Core.HeadlessAgentRuntime
```

The adapter owns engine lifecycle and thread affinity only. Protocol, Agent
Loop, persistence semantics, budgets, operation ids, and receipts remain in the
shared assemblies. The facade depends on backend interfaces, so a game can
inject the shared durable runtime or another compatible backend without
changing the Unity lifecycle layer.

## Configure the host

Add `UnityAgentRuntimeHost` to a bootstrap GameObject, or call
`UnityAgentRuntimeHost.EnsureCreated()`. For durable runs, construct a
`UnityMainThreadGameHost` with `host.Dispatcher`, pass
`host.EventPublisher` to `GameAgentRuntimeBuilder.PublishEventsTo`, then call
`host.Configure(built)`. This transfers ownership of the complete builder
composition by default. Custom integrations may instead configure an
`IDurableAgentRuntime` or `IUnityDurableAgentRuntimeBackend` together with its
session store. The host exposes `RunAsync(DurableRunRequest)`, `ResumeAsync`,
and `DurableControls`.

The provider/store/action-handler `Configure` overload remains available for
the compact headless loop.

`DontDestroyOnLoad` is enabled by default, so scene unload does not end a
world/session. Stable game ids, not `GameObject` references or instance ids,
belong in protocol objects and persisted state.

## Main-thread dispatch

Provider and Agent work may execute off-thread. Every game action crosses
`UnityMainThreadGameHost`, which posts to the bounded dispatcher and begins the
handler on the thread that created the host.

The authoritative action lane defaults are:

- 1024 queued operations;
- 64 dispatches per frame;
- 2 ms pump budget per frame.

The runtime-event lane is separate, defaults to 1024 queued notifications and
128 deliveries per frame, and may drop notifications under pressure.
`DroppedRuntimeEventCount` exposes the cumulative count. Telemetry cannot fill
or delay the action queue. These are serialized fields on the host. Action
queue overflow fails the awaiting operation with
`UnityDispatcherQueueFullException`; authoritative work is never silently
dropped. The returned run task and durable journal remain authoritative.

The facade admits at most 32 active Run/Resume calls by default. Excess
admission fails immediately with `UnityRunCapacityExceededException`, rather
than allocating an unbounded set of tasks behind the runtime.

Do not call Unity APIs before an async handler's first await and then assume its
continuation is still on the Unity thread. Keep Unity mutation in the
synchronous prefix, split longer work into another dispatch, or explicitly use
the dispatcher again. Cancellation completes queued action tasks immediately;
the bounded queue slot is reclaimed on the next pump. Once an action handler
has started, cancellation is cooperative: its token is signaled, and the
runtime waits for the handler to return before durable shutdown can finish.

## Structured DTO bridge

Unity's serializer does not understand `JsonElement` or the full protocol
graph. The package therefore provides Unity-serializable field DTOs:

- `UnityObservationData`;
- `UnityActionRequestData`;
- `UnityActionReceiptData`.

`UnityProtocolBridge` converts these to the authoritative protocol types and
also provides reflection-free JSON parsing/serialization for
`ObservationEnvelope`, `ActionRequest`, `ActionReceipt`, and `RuntimeEvent`.
Natural language is not required: `payloadJson`, arguments, receipts, and final
output can all be structured JSON.

The bridge calls `ProtocolValidator` for game-supplied observations, action
requests, and action receipts. `RuntimeEvent` JSON uses the protocol's
reflection-free serializer and is intended for runtime-produced journal data.
Treat JSON from models, mods, saves, or networks as untrusted and apply
game-specific schema and size limits before mutation.

## Cancellation and shutdown

- A caller token cancels one run.
- `CancelActiveRuns` cancels every currently tracked run without permanently
  shutting down the host.
- `ShutdownAsync` prevents new runs, cancels active runs, waits for them,
  flushes `IDurableSessionStore`, and disposes an owned store.
- Dispatcher shutdown atomically closes work admission. Work claimed before
  that boundary remains part of the shutdown drain; dequeued or direct work
  that has not been claimed is cancelled without entering the game callback.
- Obtain `EventPublisher` during bootstrap. Access after shutdown has started
  throws `ObjectDisposedException` and never creates a replacement event lane.
- Cancelling one `ShutdownAsync` caller only cancels that caller's wait. The
  shared cleanup continues, and a later caller can await the same shutdown.
- Exceptions thrown by cancellation callbacks do not skip run draining or
  store cleanup; `ShutdownAsync` reports them after cleanup completes.
- `OnApplicationQuit` and `OnDestroy` perform non-blocking best-effort
  cancellation because Unity lifecycle callbacks cannot be awaited.

For a controlled quit, save the game and await `ShutdownAsync` before calling
`Application.Quit`. Mobile operating systems and WebGL can terminate without a
reliable quit callback, so durable ActionRequest/Receipt records must be
flushed at their normal write boundary. Do not rely on `OnDestroy` as a save
operation.

## Mono, IL2CPP, and stripping

The adapter:

- targets the shared `netstandard2.1` assemblies;
- uses no `Reflection.Emit`, expression compilation, runtime code generation,
  or reflection-based DTO discovery;
- calls the protocol's source-generated `System.Text.Json` entry points;
- marks the runtime assembly with `AlwaysLinkAssembly`;
- marks public Unity bridge and lifecycle roots with `Preserve`.

The artifact builder bundles the exact shared runtime assemblies and their
composition builder, optional streaming provider adapter, and managed
`System.Text.Json` dependencies. If a game already ships different versions of
those assemblies, resolve the dependency versions deliberately; do not keep
two assemblies with the same identity.

Unity documents that managed stripping also processes package/plugin
assemblies, and recommends annotations for code the static analyzer cannot see.
Our direct call graph and generated JSON metadata minimize that surface:

- https://docs.unity3d.com/6000.0/Documentation/Manual/managed-code-stripping.html
- https://docs.unity3d.com/6000.0/Documentation/ScriptReference/RuntimeInitializeOnLoadMethodAttribute.html

## Verification

Without a Unity installation:

```powershell
powershell -ExecutionPolicy Bypass -File `
  engines/unity/scripts/Test-UnityPackage.ps1
```

This performs:

1. a `netstandard2.1` compile of Runtime, Samples, and package test sources
   using deterministic Unity API stubs;
2. dispatcher/cancellation/DTO tests;
3. a full durable Context → streamed ToolCall → main-thread Action →
   journaled Receipt → final conformance loop;
4. assembly of a complete UPM artifact;
5. checks that Unity sources do not duplicate the Agent core.

With a real Unity project that has the staged package installed and listed in
the project's `testables`, run:

```powershell
powershell -ExecutionPolicy Bypass -File `
  engines/unity/scripts/Invoke-UnityEditorGate.ps1 `
  -UnityEditorPath "C:\Program Files\Unity\Hub\Editor\...\Editor\Unity.exe" `
  -ProjectPath "C:\path\to\gate-project" `
  -Backend Both
```

This runs EditMode and PlayMode tests, then builds and executes Windows Mono
and IL2CPP Players. Each Player must complete the same deterministic durable
tool loop and write a validated JSON pass marker. The IL2CPP platform module
must be installed.

## Verified support

| Environment | Status |
|---|---|
| .NET 8 SDK stub compile of Unity-facing source as `netstandard2.1` | Passed |
| Stubbed main-thread structured tool-loop conformance | Passed |
| Unity 2022.3 Editor / Mono Player | Not run on this machine |
| Unity 2022.3 Windows IL2CPP Player | Not run on this machine |
| Unity 6 Editor / IL2CPP Player | Not run on this machine |
| Mobile, WebGL, consoles | Not claimed |

Stub compilation is a portability gate, not evidence that UnityLinker or
IL2CPP succeeded. Do not upgrade the support claim until the real Player gates
run in CI.

## Official Unity references

- Package layout and tests:
  https://docs.unity3d.com/Manual/CustomPackages.html
- Package manifest:
  https://docs.unity3d.com/6000.0/Documentation/Manual/upm-manifestPkg.html
- .NET profile support:
  https://docs.unity3d.com/Manual/dotnet-profile-support.html
- MonoBehaviour lifecycle:
  https://docs.unity3d.com/ScriptReference/MonoBehaviour.html
- Managed stripping:
  https://docs.unity3d.com/6000.0/Documentation/Manual/managed-code-stripping.html

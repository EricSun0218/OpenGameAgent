# Unreal Engine compatibility probe

This directory is a compile-ready Unreal Engine 5 Runtime plugin probe. It establishes the engine boundary without claiming a complete production transport.

Implemented:

- a C++17 wire model and bounded, strict JSON parser for observations, action requests, action receipts, and runtime events;
- a versioned C ABI table suitable for an optional in-process native runtime;
- a GameThread dispatcher with an explicit shutdown boundary;
- a host router that validates action JSON before invoking game code on the GameThread;
- a transport interface shared by future sidecar and in-process backends;
- engine automation tests and a portable C++ protocol/ABI smoke test.

Not implemented yet:

- process launch, IPC framing, restart supervision, or authentication for a sidecar backend;
- dynamic-library loading and lifecycle management for an in-process native backend;
- packaging and validation against a specific Unreal Engine release.

## Runtime boundary

Game code implements `IGameAgentHostBoundary`. Incoming action requests cross the wire as UTF-8 JSON, are validated before queue admission, and are queued through `FGameAgentMainThreadDispatcher`. The router may be called from any producer thread; the host method itself always runs on the GameThread.

The generic JSON parser remains usable for bounded game payload objects, while
the protocol decoder applies the stricter contract to envelope extensions:
64 properties are accepted and 65 are rejected before the extension object is
copied into a decoded request, observation, receipt, or runtime event.

A host must invoke each action completion. It may complete asynchronously; the
transport adapter must therefore accept completion from any thread and preserve
the operation identifier and receipt revision. The router forwards only the
first completion and ignores duplicates. Hosts must implement
`StopAndDrainActions` by preventing new host-side action work and joining every
thread or task that may retain a router completion. The method must not return
while such a callback can still run. `Stop` waits for callbacks already entering
the router. Calls to `Stop` or `UnbindHost` from an action completion or from
`StopAndDrainActions` are rejected with a `false` return value, which prevents
lifecycle cleanup from waiting on itself.
One router accepts one host binding for its lifetime: replacing a live host is
rejected, and `UnbindHost` permanently stops the router before releasing the
host. This keeps the quiesce handle alive even after an action has completed and
the host still retains a duplicate completion.

The router owner must retain a strong reference until a terminal `Stop` or
`UnbindHost` call returns `true`, and that terminal call must happen outside
`ExecuteAction`, an action completion, and `StopAndDrainActions`. Releasing the
last router reference from one of those callbacks violates the ownership
contract because no synchronous destructor can join its own callback stack.
Development builds fail an assertion if this misuse reaches the destructor. The
runtime module follows the contract by retaining its router until shutdown has
fenced the host.

The runtime can later be connected through either backend behind `IGameAgentRuntimeTransport`:

1. `Sidecar` keeps the agent loop outside the game process and uses authenticated local IPC.
2. `InProcessNative` loads a native library and negotiates `GAR_RuntimeApiV1`.

Both backends consume the same wire objects. Backend selection therefore does not leak into gameplay code.

The C ABI is an API table rather than a set of directly linked runtime functions. An in-process backend resolves `GAR_GET_RUNTIME_API_V1_SYMBOL`, calls it through `GAR_GetRuntimeApiV1Fn`, and validates the returned version and table size. A .NET NativeAOT library can export that single entry point without exposing managed types. Callers must set every `StructSize`, request an exact ABI version, and retain callback targets until `Destroy` returns. Byte spans are borrowed and valid only for the duration of the call. No exception or engine-owned type may cross this boundary.

## Add to a project

Copy this directory to:

```text
<Project>/Plugins/GameAgentRuntime
```

Regenerate project files, then add `GameAgentRuntime` to the consuming module:

```csharp
PrivateDependencyModuleNames.Add("GameAgentRuntime");
```

Bind a host during game initialization:

```cpp
FGameAgentRuntimeModule::Get()
    .GetHostRouter()
    ->BindHost(MyHost.ToSharedRef());
```

Call `UnbindHost` before the owning game system is destroyed; unbind is a
synchronous, terminal operation for that router. The module ticker drains a
bounded number of queued actions per frame. The dispatcher also caps pending
work. During GameThread shutdown, every unresolved accepted action, including
one already started by an asynchronous host, is completed with an `unknown`
receipt using its original operation identifier. The router then quiesces the
host and fences callbacks already in flight so module code cannot be unloaded
under a retained completion thunk. A dispatcher item drained after terminal
router shutdown observes the closed router and skips `ExecuteAction`, so a later
tick cannot restart work on the quiesced host.

## Verification

With CMake and a C++17 compiler installed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/unreal/scripts/Test-PortableWire.ps1 `
  -RequireToolchain
```

On Windows without Visual Studio, put `zig` and `ninja` on `PATH` and select
the portable toolchain explicitly:

```powershell
$env:GAME_AGENT_UNREAL_PORTABLE_TOOLCHAIN = 'zig'
./engines/unreal/scripts/Test-PortableWire.ps1 -RequireToolchain
```

The script uses Zig only for this portable protocol/ABI probe. It does not
replace Unreal Build Tool or prove an Unreal Editor integration.

This first exercises the automation-report parser with synthetic pass, missing,
and failure reports. It then compiles the exact portable wire source used by the
Unreal module, treats warnings as errors, parses repository protocol fixtures,
tests JSON safety limits, and exercises the native ABI function table.

An Unreal Editor build additionally compiles three automation tests:

```text
GameAgent.Runtime.Unreal.WireParser
GameAgent.Runtime.Unreal.GameThreadDispatcher
GameAgent.Runtime.Unreal.HostRouter
```

With an installed engine, build the plugin package and run the three tests:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/unreal/scripts/Build-UnrealPlugin.ps1 `
  -EngineRoot C:\Unreal\UE_5.x

powershell -NoProfile -ExecutionPolicy Bypass `
  -File engines/unreal/scripts/Test-UnrealAutomation.ps1 `
  -EngineRoot C:\Unreal\UE_5.x `
  -ProjectFile C:\Game\Game.uproject
```

The project used for automation must have this plugin enabled. A zero Editor
exit code is not sufficient: the gate requires a fresh JSON report, verifies
that all three named tests were discovered exactly once, and requires each test
and the report summary to pass. Run both gates before changing the supported
Unreal Engine version from compatibility-probe status.

## Safety expectations

- Validate all remote or sidecar input before queuing game work.
- Keep credentials in the selected runtime backend; the host bridge does not accept or persist model credentials.
- Authenticate sidecar IPC before enabling world-changing actions.
- Journal an action request before the host executes it, and return an `unknown` receipt if completion cannot be proven.
- Apply game-specific authorization and state validation inside the host implementation.

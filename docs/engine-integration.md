# Engine integration

Godot and Unity use the same shared runtime. The adapters exist to connect lifecycle, cancellation, JSON calls, and engine-thread callbacks; gameplay remains in game code.

## Godot 4.7 .NET

Build the distributable add-on:

```powershell
./engines/godot/build-package.ps1 -GodotSharpDir <GodotSharp/Api/Debug>
```

Copy the generated `addons/open_game_agent` directory into the game project. Import its props file from the game's C# project:

```xml
<Import Project="addons\open_game_agent\OpenGameAgent.Godot.props" />
```

Add `OpenGameAgentNode` to a scene or Autoload. Configure one mode before running inputs:

```csharp
agentNode.Configure(localRuntime);
// or
agentNode.ConfigureRemote(serverClient);
```

C# callers can await `RunAsync` or `RunRemoteAsync`. GDScript and signal-oriented integrations can call `RunJson` and listen for:

- `run_event(input_id, event_json)`
- `run_completed(input_id, result_json)`
- `run_failed(input_id, error)`

Signals enter a bounded queue and are drained on the Godot thread each frame. The optional `Configure` arguments control active runs, queued signals, and per-frame delivery. Every admitted run reserves space for its terminal signal; when saturated, intermediate stream events yield to terminal signals and a new run can be rejected until pending terminals are delivered. `Cancel(inputId)` cancels an active client call. Input IDs must be unique among calls currently tracked by one node. `SteerActorAsync` and `AbortActorAsync` target the model/tool loop by session and actor in both local and remote modes. `_ExitTree` cancels all active calls and drops late callbacks.

Runtime actor lanes start on the worker pool, so context, provider, and tool work do not run inline on the Godot frame that submitted an input.

Verify package structure and a real editor import/scene run:

```powershell
./engines/godot/test-package.ps1 -GodotSharpDir <GodotSharp/Api/Debug>
./engines/godot/test-engine.ps1 -Godot <godot_console.exe> -GodotSharpDir <GodotSharp/Api/Debug>
```

## Unity 6

Build the distributable UPM directory:

```powershell
./engines/unity/build-package.ps1 -UnityManagedDir <Unity/Editor/Data/Managed/UnityEngine>
```

Add the generated package through Package Manager using its local directory. Add `OpenGameAgentBehaviour` to a persistent GameObject and call:

```csharp
behaviour.Configure(localRuntime);
// or
behaviour.ConfigureRemote(serverClient);
```

C# callers can await `RunAsync` or `RunRemoteAsync`. `RunJson` starts a fire-and-signal call. `SteerActorAsync` and `AbortActorAsync` work with either configured placement. Subscribe in the Inspector or code to `RunEvent`, `RunCompleted`, and `RunFailed`.

Runtime actor lanes start on the worker pool, so context, provider, and tool work do not run inline on the Unity frame that submitted an input. The component queues event callbacks and drains a bounded number in `Update`; every admitted run reserves a terminal callback and intermediate events yield when the queue is saturated. Inspector limits are validated and snapshotted by `Configure` or `ConfigureRemote`; editing serialized fields while a run is active does not change that configured queue contract. Input IDs must be unique among calls currently tracked by one component. `PumpCallbacks()` exposes the same bounded main-thread drain for custom player loops and headless editor tests. The component cancels active calls and clears queued callbacks in `OnDestroy`.

Verify package structure and a real editor import/compile/execute:

```powershell
./engines/unity/test-package.ps1 -UnityManagedDir <Unity/Editor/Data/Managed/UnityEngine>
./engines/unity/test-editor.ps1 -UnityEditor <Unity.exe> -UnityManagedDir <Unity/Editor/Data/Managed/UnityEngine>
```

## Local and remote modes

Local mode embeds `OpenGameAgent`, the provider adapter, tools, and optional stores in the game process. Remote mode embeds only the client-facing shared assemblies and sends `GameInput` over JSON/SSE. Actor steering and abort use authenticated control requests; canceling the local HTTP call remains available when only that caller should stop waiting.

The base engine archives contain the adapter and shared runtime/client assemblies. Add the separately versioned `OpenGameAgent.Persistence`, provider, `OpenGameAgent.Extensions`, `OpenGameAgent.Models`, or external-tool connector packages only when the game uses them. Keeping these packages optional lets a dialogue-only client avoid carrying server storage or connector dependencies while preserving the same extension contracts in local and remote placement.

In a shipped client, use BYOK, a local endpoint, or short-lived credentials issued by a developer-controlled gateway. Engine placement never makes an embedded permanent key secret.

Use `GameAgentWire.SerializeInput` and `ParseInput` when crossing an engine's dynamic-language or event boundary. Floating-point JSON values are preserved as JSON numbers.

Streaming `MessageUpdated` wire events carry only the new delta for that event. Accumulate deltas for transient UI text and treat `MessageEnded` or the terminal run result as the canonical complete message. Engine queues may drop intermediate events under pressure, so gameplay correctness must never depend on receiving every visual streaming delta.

## Main-thread actions

The adapters marshal public events, not arbitrary context providers or tool handlers. If a tool mutates a scene, use `QueuedGameActionHandler` when the host needs a reusable bounded handoff from background work to its game thread:

```csharp
var actionHandler = new QueuedGameActionHandler(
    intent =>
    {
        ValidateArguments(intent);
        ValidateGeneration(intent.GenerationId);
        return ExecuteOnGameThread(intent);
    },
    intent => RecoverOnGameThread(intent),
    capacity: 256);

// Call this from the engine's tick/update callback.
actionHandler.Pump(maximumWorkItems: 32);
```

`ExecuteAsync` and `RecoverAsync` wait for the host thread without moving the game callback to a worker thread. Requests that have not started can be cancelled; a request already claimed by `Pump` is allowed to finish. Call `Stop` when a scene or application is shutting down so waiting requests fail and new requests are rejected. Keep the operation ID in the game save/command ledger so `RecoverAsync` can answer after a crash. The host remains responsible for authoritative rules and `generationId` validation.

## Versions

The current gates use Godot 4.7.1 .NET and Unity 6000.5.6f1 on Windows. Shared runtime and server projects build on Windows and Linux.

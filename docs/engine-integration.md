# Engine integration

Godot and Unity can embed the shared runtime. Unreal uses a native C++ plugin with the remote JSON/SSE placement so the engine process does not host a CLR. All adapters connect lifecycle, cancellation, structured calls, and engine-thread callbacks; gameplay remains in game code.

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

## Unreal Engine 5.8

Unreal projects normally use native C++, so the official adapter is a native plugin rather than an embedded C# runtime. Copy `engines/unreal/Plugins/OpenGameAgent` into the project's `Plugins` directory, enable it, and obtain `UOpenGameAgentSubsystem` from the game instance. Configure a trusted local sidecar or remote service, then send canonical input JSON:

```cpp
FString Error;
AgentSubsystem->ConfigureRemote(TEXT("http://127.0.0.1:5080"), PairingToken, false, Error);

FString InputId;
AgentSubsystem->RunJson(CanonicalInputJson, InputId, Error);
```

`OnRunEvent`, `OnRunCompleted`, `OnRunFailed`, `OnControlCompleted`, and `OnActionResponse` are Blueprint-assignable and always delivered on the game thread. `SteerActor` and `AbortActor` target an active actor. `CancelRun` only stops the local HTTP caller and never asserts that a durable mutation did or did not commit. Request, response, stream-event, identifier, and active-run limits are enforced before callbacks reach gameplay.

For authoritative mutations, claim a bounded batch through `ClaimActions`, compare every operation with the game's save/operation ledger, execute or recover it on the game thread, and submit the canonical receipt with `SubmitActionReceiptJson`. Use `ReconcileAction` after a restart or uncertain delivery. The sidecar persists the action intent and delivery state; Unreal remains responsible for world validation, generation/revision checks, the actual mutation, and its authoritative receipt.

The plugin rejects non-loopback plaintext HTTP unless explicitly enabled. Provider credentials stay in the sidecar; an optional sidecar access token is sent only as an Authorization header and is never copied into run JSON or event errors.

Verify the distributable plugin structure and then compile/run its automation tests in a real editor:

```powershell
./engines/unreal/test-package.ps1
./engines/unreal/test-plugin.ps1 -UnrealRoot <UE_5.8>
```

## Local and remote modes

Local mode embeds `OpenGameAgent`, the provider adapter, tools, and optional stores in a compatible C# game process. Remote mode uses the C# client or native Unreal plugin and sends `GameInput` over JSON/SSE. Actor steering and abort use authenticated control requests; canceling the local HTTP call remains available when only that caller should stop waiting.

For a remote C# UI, call `ServerGameAgentClient.ReadTranscriptAsync(new GameSessionKey(sessionId, actorId), pageSize, cursor)` to reopen the current persisted conversation. The server authorizes the session/actor before loading it, applies the configured audience projection, and returns attachment descriptors without image bytes. Treat `nextCursor` as opaque; restart paging when the server reports that the transcript revision changed.

The base engine archives contain the adapter and shared runtime/client assemblies. Add the separately versioned `OpenGameAgent.Persistence`, provider, `OpenGameAgent.Extensions`, `OpenGameAgent.Models`, or external-tool connector packages only when the game uses them. Keeping these packages optional lets a dialogue-only client avoid carrying server storage or connector dependencies while preserving the same extension contracts in local and remote placement.

In a shipped client, use BYOK, a local endpoint, or short-lived credentials issued by a developer-controlled gateway. Engine placement never makes an embedded permanent key secret.

Use `GameAgentWire.SerializeInput` and `ParseInput` when crossing an engine's dynamic-language or event boundary. Floating-point JSON values are preserved as JSON numbers.

Streaming `MessageUpdated` wire events carry only the new delta for that event. Accumulate deltas for transient UI text and treat `MessageEnded` or the terminal run result as the canonical complete message. Engine queues may drop intermediate events under pressure, so gameplay correctness must never depend on receiving every visual streaming delta.

## Realtime speech and behavior

Add `OpenGameAgent.Realtime` for the engine-neutral conversation manager and bridge. Add a provider package such as `OpenGameAgent.Providers.OpenAI.Realtime` only in the process that owns the provider credential. Audio capture calls `TrySendAudio`; a saturated audio queue drops the newest frame instead of blocking the engine capture callback. Audio output, transcripts, and behavior events must be marshalled through the same bounded engine callback queue described above.

`IRealtimeBehaviorHandler` is for reversible presentation state such as gaze, gesture, facial expression, or a replaceable locomotion intent. A new request on the same behavior channel cancels the previous request. Implement the handler by queueing work to the engine thread. Inventory changes, construction, combat results, quest state, and other authoritative mutations must remain ordinary game tools backed by `DurableGameActionDispatcher`.

The realtime speech loop remains responsive while `GameRealtimeAgentBridge` starts a full agent run. A later handoff steers that active actor instead of starting a competing run. Streamed agent text is returned to speech in bounded time slices. Player barge-in cancels the current response and truncates only the audio already played, so the provider transcript does not claim unheard speech.

## Main-thread actions

The adapters marshal public events, not arbitrary context providers or tool handlers. If a tool mutates a scene, create an `IGameActionHandler` that:

1. validates arguments without touching engine state;
2. queues a command onto the engine thread;
3. awaits a completion source;
4. returns the game-generated `GameActionReceipt`.

Bound that queue and cancel waiting callers during scene or application shutdown. Keep the operation ID in the game save/command ledger so `RecoverAsync` can answer after a crash.

## Versions

The current gates use Godot 4.7.1 .NET, Unity 6000.5.6f1, and Unreal Engine 5.8 on Windows. Shared runtime and server projects build on Windows and Linux.

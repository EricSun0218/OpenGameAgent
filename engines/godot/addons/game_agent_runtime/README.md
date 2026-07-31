# Game Agent Runtime for Godot

Godot 4.7 .NET host for the shared durable Agent Runtime. The addon owns engine
lifecycle, bounded main-thread dispatch, bounded signal delivery, and Variant
mapping. Providers, tools, skills, persistence policy, game rules, and state
remain game configuration.

## Install

1. Copy `addons/game_agent_runtime` into a Godot 4.7 .NET project.
2. Import the packaged assemblies from the project `.csproj`:

   ```xml
   <Import Project="addons\game_agent_runtime\GameAgentRuntime.props" />
   ```

3. Build the C# solution.
4. Enable **Game Agent Runtime** under **Project > Project Settings >
   Plugins**. The plugin registers `GameAgentRuntimeNode.tscn` as the
   `GameAgentRuntime` Autoload.

The assembled addon includes Protocol, Core, Persistence, Runtime, Workflow,
and the OpenAI-compatible and Anthropic provider adapters. Credentials come
from an `IProviderCredentialSource`; the addon never stores them.

## Compose the runtime

Compose shared services in C# and configure the Autoload once:

```csharp
var runtimeNode = GetNode<GameAgentRuntimeNode>("/root/GameAgentRuntime");

var gameHost = new GodotMainThreadGameHost(runtimeNode.Dispatcher);
gameHost.Register("inspect_state", request =>
{
    // Read authoritative Godot state on the main thread.
    return ValueTask.FromResult(new ActionReceipt
    {
        OperationId = request.OperationId,
        Status = ReceiptStatuses.Succeeded,
        Result = ProtocolJson.ParseElement("{\"visible\":true}")
    });
});

BuiltGameAgentRuntime built = new GameAgentRuntimeBuilder()
    // Configure provider, profile, tools, stores, and policies here.
    .Build();

runtimeNode.Typed.ConfigureDurable(built);
```

The game owns the `GameAgentRuntimeBuilder` composition root. Do not construct
one runtime per frame or per NPC; use sessions and run identifiers to isolate
work within a bounded host.

## Start runs

C# callers use the typed host:

```csharp
string requestId = runtimeNode.Typed.StartRun(request);
```

GDScript callers may use the Variant-compatible methods on the Autoload:

```gdscript
var request_id := GameAgentRuntime.start_agent_run(run_dictionary, observations)
```

Available GDScript operations include starting and resuming durable runs,
starting multi-actor batches, resuming or abandoning a participant, and
posting cancel, interrupt, steer, or follow-up controls. Inputs are converted
to strict protocol DTOs before entering the runtime.

## Signals

The Autoload publishes bounded main-thread signals for runtime start/stop,
runtime events, run completion/failure, and multi-actor lifecycle. Critical
terminal messages use reserved delivery capacity; best-effort progress may be
dropped under sustained overload. Treat durable outcomes and stores as the
source of truth.

## Main-thread boundary

Provider streaming, context work, and persistence run asynchronously. An action
descriptor marked `engine_main_thread` is submitted through
`GodotMainThreadGameHost` and drained from `_Process` within configurable item
and time budgets. Never mutate a Node from a provider callback or background
task.

## Multi-actor decisions

`GodotRuntimeHost.ConfigureMultiActor` binds the shared
`MultiActorDecisionCoordinator`. `StartBatch` accepts bounded participant
requests, isolates participant failures, and publishes deterministic lifecycle
messages. The game decides when results are applied simultaneously and remains
responsible for conflicts in authoritative state.

## Shutdown

The node stops accepting work, requests cancellation, drains main-thread and
event queues, stops the backend, and flushes owned durable stores within a
bounded shutdown window. Check `IsShutdownIncomplete` when a host must surface
an unclean exit. Avoid freeing the Autoload while game-owned action callbacks
can still complete.

## Package contents

The distributable package contains only the engine adapter and shared runtime
assemblies. It deliberately contains no game schema, world editor, content
archive format, tools, skills, credentials, or executable user content.

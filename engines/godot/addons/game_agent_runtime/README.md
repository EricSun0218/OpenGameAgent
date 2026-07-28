# Game Agent Runtime for Godot

Godot 4.7 .NET host for the durable `GameAgent.Core` agent loop. The addon owns
engine lifecycle, bounded main-thread dispatch, bounded signal delivery, and
Variant mapping. Providers, tools, skills, persistence policy, and game rules
remain normal Core configuration.

## Install

1. Copy `addons/game_agent_runtime` into a Godot 4.7 .NET project.
2. Import the packaged assemblies from the project's `.csproj`:

   ```xml
   <Import Project="addons\game_agent_runtime\GameAgentRuntime.props" />
   ```

3. Build the C# solution, then enable **Game Agent Runtime** under
   **Project > Project Settings > Plugins**. The plugin registers
   `GameAgentRuntimeNode.tscn` as the `GameAgentRuntime` Autoload.

The package contains Protocol, Core, Persistence, the OpenAI-compatible
streaming provider, and the composition builder. Credentials are supplied by
an `IProviderCredentialSource`; they are not stored by the addon.

## Configure the durable runtime

Compose the durable runtime in C# with the packaged builder, then give its
owned result to the Autoload:

```csharp
using GameAgent.Core;
using GameAgent.Godot;
using GameAgent.Providers.OpenAICompatible;
using GameAgent.Runtime;
using Godot;

var hostNode = GetNode<GameAgentRuntimeNode>("/root/GameAgentRuntime");
var gameHost = new GodotMainThreadGameHost(
    hostNode.Dispatcher,
    new SystemRuntimeClock());

var built = new GameAgentRuntimeBuilder(gameHost)
    .UseFileJournal(ProjectSettings.GlobalizePath(
        "user://agent-runtime.journal"))
    .UseOpenAiCompatibleProvider(
        new OpenAiCompatibleProviderOptions
        {
            ProviderId = "game-provider",
            BaseUri = new Uri("https://api.example.com"),
            Model = "game-model"
        },
        credentialSource)
    .WithTools(toolDescriptors)
    .WithSkills(skillManifests)
    .PublishEventsTo(hostNode.Typed.EventPublisher)
    .Build();

hostNode.Typed.ConfigureDurable(built);
```

`GodotMainThreadGameHost` routes registered action handlers through the bounded
dispatcher, so Godot APIs are touched only on the engine main thread.
The builder and built composition both use asynchronous cleanup. If setup can
fail after the builder takes ownership of a store, keep the builder in an
`await using` scope and await its cleanup rather than blocking the main thread.
Both synchronous handlers and cancellation-aware
`ValueTask<ActionReceipt>` handlers are supported. Async handlers start on the
main thread, may complete across frames, and remain tracked until their inner
operation finishes. Cancellation or failure after execution starts remains an
unknown-outcome path for durable reconciliation.
For a custom composition root, the overload accepting
`IDurableAgentRuntime` plus `IDurableSessionStore` remains available.

## Start and control from GDScript

`start_agent_run` accepts a protocol `AgentRun` Dictionary and an Array of
protocol `ObservationEnvelope` Dictionaries. Each observation becomes a
required Core `ContextCandidate`; the input may contain any JSON-compatible
structured data and does not need to be natural language.

```gdscript
GameAgentRuntime.run_completed.connect(func(outcome: Dictionary) -> void:
    print(outcome["run"]["state"])
    print(outcome["final_output"])
)

var request_id := GameAgentRuntime.start_agent_run(run, observations)
```

The returned `request_id` correlates Godot signals. Controls target the
protocol `run_id`:

```gdscript
GameAgentRuntime.steer_run(run_id, changed_world_observation)
GameAgentRuntime.follow_up_run(run_id, follow_up_observation)
GameAgentRuntime.interrupt_run(run_id)
GameAgentRuntime.cancel_run(run_id)
```

`resume_agent_run(run_id)` resumes a journaled run with default continuation
options. C# exposes the complete typed surface:

- `StartRun(DurableRunRequest)`
- `ResumeRun(runId, DurableRunContinuation?, IGameOperationReconciler?)`
- `TryPostControl(runId, RunControlCommand)`
- `CancelRun`, `InterruptRun`, `SteerRun`, and `FollowUpRun`

Tool and skill catalogs are configured on the durable Core registries, rather
than being copied into every Godot call.

## Signals and threading

- `runtime_event_published` receives events published by Core through the
  injected `IRuntimeEventPublisher`.
- `run_completed` carries `request_id`, `run`, `final_output`, normalized error
  fields, and `reconciliation_required`.
- `run_failed` is reserved for an exceptional adapter/backend failure.
- Provider, journal, and agent-loop work runs off the Godot main thread.
- Godot APIs and registered world-action handlers run through the bounded
  dispatcher during `_Process`.
- Signals are emitted only by the bounded main-thread event pump. Notification
  overflow is reported; durable journal data remains authoritative.

Call `await GameAgentRuntime.Typed.StopAsync(...)` before intentionally
quitting. Shutdown closes dispatcher admission, cancels queued and active work,
waits for every started action handler, then disposes the built runtime (flush,
runtime/provider cleanup, store disposal) and closes the event pump. A caller
may cancel its own wait, but shared cleanup continues and never disposes
runtime state underneath a started handler.

`runtime_stopped` is a terminal, at-most-once signal. Its `status` is
`graceful` only when shutdown completes without an error; otherwise it is
`shutdown_incomplete`. Retryable direct-stop failures report the incomplete
terminal state once, while `_ExitTree` performs bounded retries before choosing
its terminal status. `IsShutdownIncomplete` remains true after a terminal
cleanup error or while retryable cleanup is still outstanding.

Each `GameAgentRuntimeNode` instance is single-use. If a scene removes or
reparents the runtime node, create a new instance instead of adding the stopped
instance back to the tree.

## Headless compatibility

The previous adapter remains available for migration:

```csharp
hostNode.Typed.ConfigureHeadless(provider, gameHost, store, clock, ids);
```

Its GDScript entry point remains
`start_run(run, observations, tools)`. New integrations should use the durable
configuration and `start_agent_run`.

The validated support target is Godot 4.7.1 .NET on Windows desktop and
headless Windows. Godot C# Web export is not supported.

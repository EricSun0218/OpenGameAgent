# OpenGameAgent for Godot

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
4. Enable **OpenGameAgent** under **Project > Project Settings >
   Plugins**. The plugin registers `GameAgentRuntimeNode.tscn` as the
   `GameAgentRuntime` Autoload.

The assembled addon includes Protocol, Core, Persistence, Runtime, Workflow,
Generation, and the OpenAI-compatible, Anthropic, and media HTTP provider
adapters. Credentials come from credential-source interfaces; the addon never
stores them.

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

BuiltGameAgentRuntime built = new GameAgentRuntimeBuilder(gameHost)
    // Configure provider, profile, tools, stores, and policies here.
    .Build();

runtimeNode.Typed.ConfigureDurable(built);
```

Media and structured-content generation is a separate optional composition:

```csharp
runtimeNode.Typed.ConfigureGeneration(generationRuntime);
string generationRequestId = runtimeNode.Typed.StartGeneration(request);
```

GDScript can call `start_generation`, `refresh_generation`,
`wait_generation`, and `cancel_generation`. Connect `generation_updated` and
`generation_failed` to observe bounded main-thread results. A generation
request accepts `operation_id`, `modality`, optional `model`, arbitrary JSON
`input`, `options`, string `metadata`, optional `authority_id`, and optional
`idempotency_key`.

The game owns the `GameAgentRuntimeBuilder` composition root. Do not construct
one runtime per frame or per NPC; use sessions and run identifiers to isolate
work within a bounded host.

## Start runs

C# callers use the typed host:

```csharp
string requestId = runtimeNode.Typed.StartRun(request);
string routedId = runtimeNode.Typed.StartRoutedRun(routedRequest);
string completionId = runtimeNode.Typed.StartCompletion(completionRequest);
string childId = runtimeNode.Typed.StartChildRun(parentRunId, childRequest);
string laterChildId = runtimeNode.Typed.StartChildRun(
    persistedParentRun,
    laterChildRequest);
bool cancelledRequest = runtimeNode.Typed.CancelRequest(completionId);
```

GDScript callers may use the Variant-compatible methods on the Autoload:

```gdscript
var request_id := GameAgent.start_agent_run(run_dictionary, observations)
var routed_id := GameAgent.start_routed_run(
    route_dictionary,
    run_dictionary,
    observations,
    options,
    {})
var completion_id := GameAgent.start_completion({
    "operation_id": "ambient-line",
    "messages": normalized_messages,
    "max_output_tokens": 48,
})
```

Available GDScript operations include starting and resuming durable runs,
choosing Direct or Agent execution plus inference/provider-route options through
`start_agent_run_with_options`, deterministic Direct/Agent/Workflow routing,
stateless completion, starting and cancelling child Agent runs, starting
multi-actor batches, resuming or abandoning a participant, and posting cancel,
interrupt, steer, or follow-up controls. Inputs are converted to strict protocol
DTOs before entering the runtime.

```gdscript
var child_id := GameAgent.start_child_agent_run(
    parent_run_id,
    child_run,
    child_observations,
    child_options)
var cancelled := GameAgent.cancel_child_agent_runs(parent_run_id)
var later_child_id := GameAgent.start_child_agent_run_with_parent(
    persisted_parent_run,
    later_child_run,
    later_child_observations,
    later_child_options)
var run_cancelled := GameAgent.cancel_run(run_dictionary["runId"])
```

Use the `AgentRun`/`persisted_parent_run` form when a completed parent was
loaded from storage, delegation crosses a restart, or the supervisor's bounded
lineage cache may have evicted it. The string-only form is for a root or a
parent still known to the active supervisor. `cancel_request(request_id)`
cancels typed C# or GDScript routed/completion requests. GDScript durable starts
use `cancel_run(run_id)`, which is a durable Agent control and has different
semantics.

## Signals

The Autoload publishes bounded main-thread signals for runtime start/stop,
runtime events, run completion/failure, and multi-actor lifecycle. Critical
terminal messages use reserved delivery capacity; best-effort progress may be
dropped under sustained overload. Treat durable outcomes and stores as the
source of truth.

The Variant surface is a versioned data contract, not an untyped shortcut.
Protocol DTO Dictionaries (`run`, observations, receipts, and tools) use the
protocol's `camelCase` JSON field names. Addon-only `options` Dictionaries use
the `snake_case` keys listed below. Unknown option keys, non-string Dictionary
keys, unsupported Variant types, circular containers, non-finite floats, and
values over the documented ingress budgets are rejected before dispatch.
Ordinary game values may be negative or fractional. Known protocol integer
fields produced by Godot's JSON parser are recovered only when the Float is an
exact, safely representable integer; construct int64 values beyond the exact
Float range directly as Variant Int. Game payload and extension numbers remain
opaque, so values such as `1.0` and `-0.0` keep their Float identity.
Outbound JSON numbers must fit an `int64` or a finite Godot `float` without
discarding significant digits. Otherwise the terminal signal is replaced by a
`runtime_error` with `godot_json_number_out_of_range` or
`godot_json_number_precision_loss`; encode exact decimal values as JSON strings.

Every start/resume method returns a request ID immediately. An empty string
means synchronous input/admission failure and is followed by `runtime_error`.
Keep the request ID to correlate a later terminal signal. Request-ID
cancellation applies to routed/completion requests from either language
surface; durable Agent runs are controlled by their run ID. Request cancellation
has its own process-wide bounded dispatcher (eight active callbacks plus 4,088
queued reservations), separate from the 72-owner lifecycle lane. `MaxActiveRuns`
cannot exceed that 4,096-operation ownership boundary.

| Signal | Stable payload shape |
| --- | --- |
| `runtime_started` | The same Dictionary returned by `get_runtime_status()` |
| `runtime_event_published` | Serialized protocol runtime event |
| `run_completed` | `request_id`, `run`, `final_output`, `error_code`, `error_category`, `safe_error_message`, `reconciliation_required` |
| `routed_run_completed` | Routed outcome plus `request_id` |
| `completion_completed` | Completion outcome plus `request_id` |
| `generation_updated` | Generation job plus `request_id`; inspect the job's actual `status` |
| `generation_failed` | Generation error plus `request_id` and uncertainty evidence |
| `run_failed`, `batch_failed`, `runtime_error` | Error envelope described below |
| `batch_completed` | Multi-actor outcome plus `request_id` |
| `batch_participant_completed` | Participant result plus `request_id` and `operation` |
| `batch_started` | Deterministic multi-actor manifest |
| `actor_finished` | Actor result plus `batch_id` |
| `batch_aborted` | `batch_id`, `reason_code` |
| `runtime_stopped` | `status`, `message`, `active_runs` |

The error envelope contains `request_id`, `code`, `category`, `message`,
`count`, `reconciliation_required`, `phase`, `batch_id`, participant identity
fields, and `affected_run_ids`. Branch on `code`; `message` is safe diagnostic
text, not a stable programmatic value. If `reconciliation_required` is true,
reload authoritative state and reconcile before retrying an effect.

A minimal GDScript durable request looks like this:

```gdscript
func _ready() -> void:
    GameAgent.run_completed.connect(_on_run_completed)
    GameAgent.run_failed.connect(_on_run_failed)
    GameAgent.runtime_error.connect(_on_runtime_error)

    # Supply the current UTC time in an ISO-8601 string with an offset.
    var now := "2026-01-01T00:00:00Z"
    var run := {
        "protocolVersion": "0.2",
        "schemaVersion": "0.2",
        "extensions": {},
        "runId": "npc-turn-0001",
        "agentId": "npc-42",
        "worldId": "save-slot-1",
        "trigger": {"type": "manual"},
        "triggerObservationIds": ["obs-0001"],
        "state": "queued",
        "revision": 0,
        "runtimeGeneration": 1,
        "budget": {
            "maxTurns": 8,
            "maxDurationMs": 30000,
            "maxTokens": 8000,
            "maxCostUsd": "1",
            "maxActions": 8
        },
        "usage": {
            "turns": 0,
            "durationMs": 0,
            "inputTokens": 0,
            "outputTokens": 0,
            "costUsd": "0",
            "actions": 0,
            "hasUnaccountedUsage": false,
            "unaccountedProviderAttempts": 0
        },
        "pendingOperationIds": [],
        "createdAt": now,
        "updatedAt": now
    }
    var observations := [{
        "protocolVersion": "0.2",
        "schemaVersion": "0.2",
        "extensions": {},
        "observationId": "obs-0001",
        "worldId": "save-slot-1",
        "source": "game",
        "kind": "snapshot",
        "subjectIds": ["npc-42"],
        "contentType": "application/json",
        "payload": {
            "game_tick": 840,
            "health": 73.5,
            "nearby_actor_ids": ["npc-7", "player"]
        },
        "observedAt": now,
        "trust": "authoritative",
        "visibility": {"scope": "world", "audienceIds": []},
        "priority": 100
    }]
    var options := {
        "execution_mode": "agent",
        "workload_class": "interactive",
        "lane_id": "npc:npc-42",
        "active_skills": [],
        "initial_transcript": []
    }
    var request_id := GameAgent.start_agent_run_with_options(
        run, observations, options)
    if request_id.is_empty():
        push_error("Agent request was rejected before dispatch")

func _on_run_completed(outcome: Dictionary) -> void:
    var durable_run: Dictionary = outcome["run"]
    var output: Variant = outcome["final_output"]
    # Stage output in game code; validate and commit authoritative effects there.

func _on_run_failed(error: Dictionary) -> void:
    push_error("%s: %s" % [error["code"], error["message"]])

func _on_runtime_error(error: Dictionary) -> void:
    push_error("%s: %s" % [error["code"], error["message"]])
```

`start_agent_run_with_options` accepts only `active_skills`,
`workload_class`, `lane_id`, `initial_transcript`, `execution_mode`,
`inference`, and `provider_route`. `execution_mode` is `direct` or `agent`.
Use Direct only when the caller does not require tools, skills, multiple model
turns, durable effects, or multi-actor coordination; the routed API is safer
when this decision is dynamic. The `inference` and `provider_route` objects are
validated against the selected provider's declared capabilities before any
network dispatch.

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

## Routing and child Agents

The built backend exposes stateless completion, durable Direct/Agent routing,
configured workflows, and bounded child supervision from the same in-process
runtime. Child completion uses the normal run-completed/run-failed signal path;
validated root/parent/depth lineage is stored in the child run extensions. The
game must still stage concurrent results and resolve them against authoritative
state rather than applying them in network-completion order.

Stateless completion signals include the selected provider route identity and
complete usage availability/cache counters needed for routing audits and
cost-accounting decisions.

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
The package props add only explicit assembly references; they do not change the
consumer project's nullable mode, implicit usings, or C# language version.

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

The package contains Protocol, Core, Persistence, the OpenAI-compatible and
native Anthropic streaming providers, the composition builder, the optional
durable Workflow module, and the engine-neutral interactive-world assembly.
Credentials are supplied by an `IProviderCredentialSource`; they are not
stored by the addon.

## Author and run a native interactive world

Enabling the plugin adds an **Agent World** dock. It can create a seven-file
starter, validate the closed JSON contracts with structured diagnostics, and
build a deterministic `.gaworld` archive. The optional import fields accept a
Character Card v2/v3 `.json` or Character Card `.png`, plus a lorebook `.json`.
Each non-empty path requires a portable content ID; an empty path means that
kind is unbound. **Validate imports** parses those files with the bounded
Compatibility adapters. **Build bound package** adds the admitted character,
lore, deterministic adapter diagnostics, and an explicit
`agentId -> characterContentId + loreContentIds` descriptor.

Imported content remains inert, untrusted data. Publishing it requires the
dock's explicit acceptance checkbox, including when an adapter reports a
warning. Acceptance does not install code or grant tools, skills, credentials,
providers, or extension authority. Import errors never publish or replace the
previous archive.

The source directory is a closed seven-file workspace. Unknown files,
subdirectories, filesystem links, oversized inputs, and package destinations
inside that source directory are rejected. Editor authoring is intentionally
bounded to 4 MiB per file and 16 MiB total so synchronous validation cannot
admit release-scale archives on the editor thread. Build larger packages with
the engine-neutral APIs in an application-owned background pipeline.
Character and lore source files are separate from that closed directory and
must be ordinary non-link files with the exact allowed extensions. A bound
dock build allows at most 12 package files, 4 MiB per file, and 24 MiB
expanded/compressed; at most five of those files are the two inert imports,
their diagnostics, and one binding.

At runtime, read the archive first and rehydrate only the strict imported
character/knowledge v1 and import-diagnostics v2 contracts. Diagnostics v2
keeps the original source-byte `sourceDigest` provenance separate from the
required canonical `normalizedContentDigest`. The reader rebuilds the
normalized character or lore value and verifies that digest before returning
content. Legacy diagnostics v1 and missing digest fields fail closed. The
reader also verifies path, media type, content ID, diagnostic pairing, binding
references, duplicate properties, and unknown shape before the existing
activator is used:

These digests detect inconsistent or damaged package content; they do not
authenticate an untrusted publisher. Games that distribute world packages
across a trust boundary must pin the expected package digest or add a
game-owned signature policy.

```csharp
using var stream = File.OpenRead(packagePath);
var definition = WorldPackageArchive.Read(stream);
var imports = new ImportedWorldPackageContentReader().Read(definition);
var binding = imports.AgentBindings["keeper-agent"];

var policy = new ImportedRuntimeActivationPolicy(
    ImportedContentAcceptance.AcceptAsUntrustedData,
    worldId,
    "npc:keeper",
    recordedAt);
var activator = new ImportedRuntimeContentActivator();
var character = activator.ActivateCharacter(
    binding.CharacterContentId!,
    imports.Characters[binding.CharacterContentId!],
    policy);
var lore = activator.ActivateLoreBook(
    binding.LoreContentIds[0],
    imports.LoreBooks[binding.LoreContentIds[0]],
    policy,
    activationContext);
var profile = AgentProfileBuilder.FromImported(character)
    .AddProvider(gameSelectedProvider)
    .Build();
```

The host decides which activated lore memories enter a run or a configured
memory lifecycle. Neither the package binding nor rehydration selects game
rules or executable capabilities.

The high-level C# path activates the package and owns one atomic runtime
generation:

```csharp
var world = GetNode<GodotInteractiveWorldNode>(
    "/root/GameAgentRuntime/InteractiveWorld");
world.ConfigureNative();

var loaded = await world.LoadNativePackageFileAsync(
    "res://build/world.gaworld");
if (!loaded.Activated)
{
    throw new InvalidOperationException(
        string.Join(" | ", loaded.Diagnostics.Select(
            item => $"{item.Code} {item.Path}: {item.Message}")));
}

WorldAuthoritativeStateSnapshot snapshot =
    await world.Native.ReadSnapshotAsync()
    ?? throw new InvalidOperationException("World state is missing.");
```

`world.Native` then exposes typed interaction query/plan/execute, discrete
clock advancement, durable schedules, exact-coordinate reads, package export,
and settled save capture/load. Package or save replacement first validates a
complete candidate, pauses admission, drains the previous generation, and
publishes one atomic swap. Call and await `ShutdownNativeAsync` during
controlled application shutdown.

## Configure the interactive-world layer

The native path above provides the built-in declarative evaluator. Advanced
games can instead register custom handlers and configure the lower-level
portable facade.

The Autoload scene contains
`/root/GameAgentRuntime/InteractiveWorld`. Register all game rules in C#, build
the portable planner, and configure that child:

```csharp
var handlers = new WorldEventHandlerRegistryBuilder()
    .AddCondition("game.condition", condition)
    .AddAdmission("game.admission", admission)
    .AddParticipantSelector("game.selector", selector)
    .AddResolver("game.resolver", resolver)
    .AddEffect("game.effect", effect)
    .Build();
var world = GetNode<GodotInteractiveWorldNode>(
    "/root/GameAgentRuntime/InteractiveWorld");
world.Configure(new InteractiveWorldFacade(
    new WorldEventPlanner(
        handlers,
        durableWorldHistory),
    gameOwnedAtomicExecutor));
```

`ImportPackage`, `ExportPackage`, `ImportSave`, and `ExportSave` have byte and
file variants. Godot `res://` and `user://` paths are globalized by the node.
The equivalent snake-case artifact methods are available to GDScript after
C# configuration. `TryScheduleTrigger`, `TryScheduleInteractionQuery`,
`TryScheduleInteraction`, and `TryScheduleExecution` use one bounded
background lane. Typed results arrive through `TypedOperationCompleted`;
GDScript receives bounded summary signals `world_operation_completed` or
`world_operation_failed` during `_Process`.

Every query and execution takes a `WorldStateFence`. A changed world,
timeline, save revision, state version, or interaction-catalog digest fails
closed. The adapter does not implement clocks, attributes, relationships,
costs, cooldowns, or any other game rule. Those remain registered handlers
and authoritative game state.

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

When an action changes the simulation snapshot, attach its authoritative
post-action coordinate before returning the terminal receipt:

```csharp
GameContextReceiptEnvelope.AttachResulting(
    receipt,
    resultingCoordinate);
```

The next memory callback and action request then observe that coordinate.
`GodotProtocolVariantMapper.ToDictionary(ActionReceipt)` and
`ToActionReceipt(Dictionary)` preserve this structured extension for Variant
boundaries. An `unknown` receipt cannot advance it; return the coordinate only
with a later terminal reconciliation result.

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

Use `start_agent_run_with_options` when a GDScript caller needs the complete
durable start input:

```gdscript
var request_id := GameAgentRuntime.start_agent_run_with_options(
    run,
    observations,
    {
        "active_skills": [
            {"skill_id": "npc-navigation", "version": "1"}
        ],
        "workload_class": "background",
        "lane_id": "village-background-npcs",
        "initial_transcript": [
            {
                "messageId": "seed-1",
                "role": "user",
                "createdAt": "2026-07-30T00:00:00Z",
                "parts": [
                    {
                        "type": "json",
                        "json": {"goal": "patrol"}
                    }
                ]
            }
        ]
    })
```

The options Dictionary is a closed schema: unknown fields, duplicate skills,
unsupported workload classes, malformed transcript messages, and values over
the documented bounds are rejected before a durable backend call starts.
`active_skills` accepts at most 128 unique `{skill_id, version}` entries,
`initial_transcript` accepts at most 2,048 normalized messages, `lane_id` is
limited to 256 UTF-8 bytes, and the encoded options are limited to 1 MiB.
Transcript messages use the normalized journal shape shown above, accept at
most 256 parts per message, and require a bounded runtime identifier for
`messageId`; they are not free-form chat objects. The observation Array keeps
the Core limit of 512 entries.

Every game-supplied Dictionary is traversed before Godot JSON serialization.
The ingress guard accepts only JSON-compatible Variant types and rejects
circular or over-depth graphs, containers over 2,048 items, strings over
65,536 UTF-8 bytes, and bounded node or byte budget overflow. Ordinary run
inputs have a 1 MiB per-object ceiling and a 16 MiB aggregate ceiling; batch
input retains its documented 16 MiB ceiling. The normalized JSON is measured
again before protocol deserialization.

The returned `request_id` correlates Godot signals. Controls target the
protocol `run_id`:

```gdscript
GameAgentRuntime.steer_run(run_id, changed_world_observation)
GameAgentRuntime.follow_up_run(run_id, follow_up_observation)
GameAgentRuntime.interrupt_run(run_id)
GameAgentRuntime.cancel_run(run_id)
```

`resume_agent_run(run_id)` resumes a journaled run with default continuation
options. `resume_agent_run_with_options` supplies continuation context,
active-skill replacement, lane, and workload scheduling from GDScript:

```gdscript
var request_id := GameAgentRuntime.resume_agent_run_with_options(
    run_id,
    {
        "context": [
            {
                "id": "current-danger",
                "category": "state",
                "content": {"danger": 7},
                "priority": 50,
                "required": true,
                "can_defer": false,
                "estimated_tokens": 8,
                "provenance": "trusted-host"
            }
        ],
        "active_skills": [
            {"skill_id": "npc-combat", "version": "2"}
        ],
        "replace_active_skills": true,
        "lane_id": "urgent-npc-decisions",
        "workload_class": "interactive",
        "resume_guard": {
            "semantic_extension_name": "gameContext",
            "expected_semantic_extension_sha256": current_context_sha256
        }
    })
```

The digest must come from the current game state through
`CanonicalJsonDigest.ComputeSha256`, not from the recovered run. A custom
backend must implement `IGodotGuardedDurableRuntimeBackend`; requesting a guard
through a backend without that capability fails closed with
`durable_resume_guard_not_supported`.

Continuation `context` accepts at most 512 unique candidates and 256 KiB of
encoded JSON. Each candidate must contain exactly one of `content` or
`resource`; resource fields are `uri`, `media_type`, optional `digest`, and
optional `size_bytes`. Optional `expires_at` is an RFC 3339 timestamp.
Priorities are limited to -1,000 through 1,000, and `estimated_tokens` is
limited to 0 through 1,000,000.

The GDScript resume surface deliberately does not accept a reconciliation
callback. When a recovered run reports `reconciliation_required`, register an
`IGameOperationReconciler` in C# and call the typed `ResumeRun` method. C#
exposes the complete typed surface:

- `StartRun(DurableRunRequest)`
- `ResumeRun(runId, DurableRunContinuation?, IGameOperationReconciler?)`
- guarded `ResumeRun` overloads and `GodotDurableResumeOptions`
- `TryPostControl(runId, RunControlCommand)`
- `CancelRun`, `InterruptRun`, `SteerRun`, and `FollowUpRun`
- `StartBatch(MultiActorDecisionBatch)`
- `ResumeBatchParticipant(...)` and `AbandonBatchParticipant(...)`

Tool and skill catalogs are configured on the durable Core registries, rather
than being copied into every Godot call.

## Multi-NPC batches from GDScript

The durable configuration overloads that accept `IDurableAgentRuntime` or
`BuiltGameAgentRuntime` automatically enable Core multi-actor coordination.
For a custom `IGodotDurableRuntimeBackend`, call
`hostNode.Typed.ConfigureMultiActor(runtime)` with the same durable runtime
that owns the backend's journals and run identities.

`start_agent_batch` accepts one closed Dictionary. Every run entry uses the
same `run`, `observations`, and optional advanced `options` shapes documented
above:

```gdscript
var request_id := GameAgentRuntime.start_agent_batch({
    "batch_id": "village-tick-700",
    "coordinate": {
        "world_id": "world-1",
        "timeline_id": "main",
        "save_revision": 42,
        "session_id": "save-slot-3",
        "scene_id": "village",
        "region_id": "north",
        "state_version": "state-42",
        "game_time": {
            "clock_id": "world-clock",
            "timeline_id": "main",
            "epoch": 1,
            "tick": 700
        },
        "causality": {
            "event_id": "tick-700",
            "based_on_state_version": "state-42",
            "parent_event_ids": ["tick-699"]
        }
    },
    "aggregate_budget": {
        "max_tokens": 16000,
        "max_actions": 8,
        "max_duration_ms": 60000,
        "max_cost_usd": "2.00"
    },
    "runs": [
        {
            "run": guard_run,
            "observations": guard_observations,
            "options": {
                "workload_class": "interactive",
                "lane_id": "village-guards"
            }
        },
        {
            "run": merchant_run,
            "observations": merchant_observations,
            "options": {
                "workload_class": "background",
                "lane_id": "village-merchants"
            }
        }
    ]
})
```

Every run must have a unique `runId`, `agentId`, and `decisionKey`, and its
world must match the shared coordinate. The optional shared `session_id`
binds the coordinate to one immutable run session. Its value, including
whether it is absent, must match every participant run's `sessionId`. The
field is preserved in
`manifest["coordinate"]`. The returned `request_id` correlates
`batch_completed` or `batch_failed`. `batch_completed` contains input-ordered
`results` and a durable `manifest`. Persist each participant Dictionary from
`manifest["participants"]`; it is the guarded handle for later operations:

```gdscript
var resume_id := GameAgentRuntime.resume_agent_batch_participant(
    manifest["batch_id"],
    manifest["participants"][0],
    {
        "active_skills": [
            {"skill_id": "npc-combat", "version": "2"}
        ],
        "replace_active_skills": true,
        "workload_class": "interactive",
        "semantic_expectation": {
            "extension_name": "gameContext",
            "expected_sha256": current_context_sha256
        }
    })

var abandon_id := GameAgentRuntime.abandon_agent_batch_participant(
    manifest["batch_id"],
    manifest["participants"][1],
    "npc_despawned")
```

Both participant operations emit `batch_participant_completed` or
`batch_failed`. The completion includes `operation`, the guarded participant
identity, and an `outcome`. If `outcome["reconciliation_required"]` is true,
the GDScript call has made no unsafe guess: supply an
`IGameOperationReconciler` through the typed C# `ResumeBatchParticipant` or
`AbandonBatchParticipant` method. Abandonment becomes `actor_finished` only
after durable cancellation reaches a terminal state.

The participant semantic expectation is caller-owned current state. The
coordinator combines it with batch/actor/decision/input-index identity and does
not reconstruct it from the persisted manifest.

`aggregate_budget` is optional. When present, the coordinator reserves the sum
of every participant's declared hard token, action, duration, and cost budgets
before lifecycle callbacks or model work. Any overflow rejects the whole batch.
The admitted totals and limits are returned as
`manifest["budget_reservation"]`; omitting the field keeps only per-run limits.

Lifecycle signals are:

- `batch_started(manifest)`, emitted on the main thread before any participant
  runtime starts;
- `actor_finished(result)`, emitted only for a terminal participant;
- `batch_aborted(error)`, emitted when the Core staging window must be
  discarded.

Signal handlers run synchronously during signal emission. Do immediate
main-thread staging there, or implement transactional staging in the C#
`IGameHost`; an asynchronous GDScript continuation is not treated as a
settlement acknowledgement. The runtime coordinates concurrent decisions and
durable identities only. Game-specific action validation, conflict resolution,
and world mutation stay in the game host.

The Autoload defaults to batches of at most 256 actors, 32 concurrent actor
runs per batch, four concurrently admitted batches, and 32 concurrent
participant operations. These are exported as `MaxActorBatchSize`,
`MaxConcurrentActorRuns`, `MaxConcurrentActorBatches`, and
`MaxConcurrentParticipantOperations`; the hard facade maximum is 1,024 actors.
The combined lifecycle concurrency must fit both the configured dispatcher
capacity and the 1,024-notification hard limit, preventing simultaneous
batches from multiplying main-thread pressure without a bound.
Batch input is limited to 16 MiB and remains subject to every per-run limit.
Batch result signals use a bounded run summary and a 48 MiB aggregate ceiling;
individual `final_output` values over 32 KiB are returned as `null` with
`final_output_omitted = true`. Full durable data remains available from the
journal or an individual typed resume.

## Signals and threading

- `runtime_event_published` receives events published by Core through the
  injected `IRuntimeEventPublisher`.
- `run_completed` carries `request_id`, `run`, `final_output`, normalized error
  fields, and `reconciliation_required`.
- `run_failed` is reserved for an exceptional adapter/backend failure.
- `batch_completed`, `batch_participant_completed`, and `batch_failed` report
  asynchronous multi-NPC operations without changing input order.
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

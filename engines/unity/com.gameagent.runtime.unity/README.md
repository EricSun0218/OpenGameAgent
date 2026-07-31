# Game Agent Runtime for Unity

This package hosts the shared C# Agent Runtime inside Unity. It does not contain
a second Unity-specific Agent Loop.

## Requirements

- Unity 2022.3 LTS or newer.
- API Compatibility Level: .NET Standard 2.1.
- Mono or IL2CPP scripting backend.

The repository package directory is a release template. Build the complete UPM
artifact, including the authoritative shared runtime assemblies, before
installing it:

```powershell
powershell -ExecutionPolicy Bypass -File `
  engines/unity/scripts/Build-UpmPackage.ps1 -Force
```

Then install this local folder through Package Manager:

```text
engines/unity/artifacts/com.gameagent.runtime.unity
```

For registry or Git releases, publish that assembled artifact rather than the
source template.

The default build omits managed symbols. Add `-IncludeSymbols` when a
development package needs portable PDBs; the builder fails if any GameAgent
assembly lacks its matching symbol file.

The artifact includes `GameAgent.Runtime.dll` for the complete durable
composition builder, `GameAgent.Workflow.dll` for optional durable workflows,
and both the OpenAI-compatible and native Anthropic streaming-provider
adapters. A Unity project can therefore assemble a runtime from the installed
package without separately compiling repository projects. It also includes
`GameAgent.World.dll`; Unity does not maintain a separate world artifact or
event format.

## Interactive worlds

For the built-in declarative evaluator, add a
`UnityNativeWorldSessionHost`, configure it once, and activate a `.gaworld`
archive:

```csharp
var worldHost = gameObject.AddComponent<UnityNativeWorldSessionHost>();
worldHost.Configure();
var loaded = await worldHost.Facade.LoadPackageAsync(packageBytes);
if (!loaded.Activated)
{
    throw new InvalidOperationException(
        string.Join(" | ", loaded.Diagnostics.Select(
            item => $"{item.Code} {item.Path}: {item.Message}")));
}

WorldAuthoritativeStateSnapshot snapshot =
    await worldHost.Facade.Typed.ReadSnapshotAsync()
    ?? throw new InvalidOperationException("World state is missing.");
```

The typed session supports structured interaction query/plan/execute, named
discrete clock advancement, schedules, exact-coordinate reads, deterministic
package export, and settled save capture/load. Package or save replacement
validates a complete candidate, drains the previous generation, and then
publishes one atomic generation swap. Await `ShutdownAsync` during controlled
scene or application shutdown.

The assembled UPM artifact includes world-v1 JSON Schemas under
`Documentation~/Schemas` and a complete inert source example under
`Samples~/InteractiveWorld`. It does not install tools, skills, scripts, or
credentials.

For game-specific evaluators, construct the lower-level portable facade.
Construct the engine-neutral `InteractiveWorldFacade` from game-registered
handlers and pass it to `UnityInteractiveWorldFacade`, or configure a
`UnityInteractiveWorldHost` component:

```csharp
var portableWorld = new InteractiveWorldFacade(
    new WorldEventPlanner(gameHandlers, durableWorldHistory),
    gameOwnedAtomicExecutor);
var worldHost = gameObject.AddComponent<UnityInteractiveWorldHost>();
worldHost.Configure(portableWorld);
worldHost.OperationCompleted += HandleWorldResult;
```

The facade provides byte and file import/export for native packages and saves,
typed trigger planning, read-only interaction queries, typed interaction
planning, and optional execution through the game-owned transaction boundary.
Every query or execution uses a `WorldStateFence`; stale world, timeline, save,
state, or catalog identity is rejected before planning.

`TryScheduleTrigger`, `TryScheduleInteractionQuery`,
`TryScheduleInteraction`, and `TryScheduleExecution` use a bounded shared
managed queue. `UnityInteractiveWorldHost` delivers completions from `Update`.
The package defines no game clocks, attributes, interactions, effects, or
numeric fields. Unity object access and authoritative mutation remain
game-owned handler responsibilities.

## Minimal integration

```csharp
var host = UnityAgentRuntimeHost.EnsureCreated();
var gameHost = new UnityMainThreadGameHost(
    host.Dispatcher,
    (request, cancellationToken) =>
    {
        var receipt = ApplyToAuthoritativeGameState(request);
        return new ValueTask<ActionReceipt>(receipt);
    });

BuiltGameAgentRuntime runtime = new GameAgentRuntimeBuilder(gameHost)
    .UseFileJournal(journalPath)
    .AddProvider(provider)
    .WithTools(toolDescriptors)
    .WithSkills(skillManifests)
    .PublishEventsTo(host.EventPublisher)
    .Build();
host.Configure(runtime);

DurableRunOutcome outcome = await host.RunAsync(runRequest);
```

The builder composes providers, tools, skills, context policy, journal,
recovery, and budgets. `Configure(BuiltGameAgentRuntime)` transfers shutdown
ownership to the Unity host by default, so the runtime, provider transports,
and journal are flushed and released together. Runtime events supplied through
`host.EventPublisher` are delivered on Unity's main thread through the bounded
dispatcher. Games with a custom implementation can instead inject
`IUnityDurableAgentRuntimeBackend`. A compact provider/store/action-handler
overload remains available for the headless compatibility loop.

The game remains authoritative for legality checks and world mutation. Return
`unknown` if the outcome of an operation cannot be proven, and reconcile it by
the same `operationId`; never invent a replacement id and replay blindly.
For a terminal action that commits a new simulation snapshot, call
`GameContextReceiptEnvelope.AttachResulting(receipt, resultingCoordinate)`.
`UnityActionReceiptData.extensionsJson` and the JSON bridge preserve the
resulting coordinate unchanged. The runtime checkpoints it before memory and
the next provider turn; an `unknown` receipt never advances it.

## Multi-actor decisions

Create the Core coordinator through the Unity host so every participant remains
inside the host's active-run capacity, cancellation, and shutdown lifecycle:

```csharp
var lifecycle = new GameDecisionBatchLifecycle();
var coordinator = host.CreateMultiActorCoordinator(
    new MultiActorCoordinatorOptions(
        maxBatchSize: 64,
        maxConcurrentRuns: 8,
        maxConcurrentParticipantResumes: 4),
    lifecycle);

MultiActorBatchOutcome batchOutcome = await coordinator.RunAsync(
    new MultiActorDecisionBatch(
        batchId,
        sharedGameContextCoordinate,
        perNpcDurableRunRequests,
        new MultiActorBatchBudget(
            maxTokens: 64_000,
            maxActions: 64,
            maxDurationMs: 240_000,
            maxCostUsd: "8.00")),
    cancellationToken);

// Persist the manifest with the game's staging window. Recovery must use the
// exact participant descriptor rather than accepting only an arbitrary run id.
MultiActorBatchParticipant participant =
    batchOutcome.Manifest.Participants[0];
var currentExpectation = DurableRunSemanticExpectation.FromJson(
    GameContextEnvelope.ExtensionName,
    GameContextEnvelope.ToJson(currentGameContextCoordinate));
MultiActorRunResult resumed = await coordinator.ResumeParticipantAsync(
    batchOutcome.BatchId,
    participant,
    currentExpectation,
    continuation,
    reconciler,
    cancellationToken);
```

`sharedGameContextCoordinate.SessionId` must exactly match every participant
run's `SessionId`, including whether the value is absent. Unity passes the
strongly typed Core coordinate without a separate adapter DTO, and the returned
manifest preserves that same session identity.

`IMultiActorDecisionLifecycle.BatchStartedAsync` receives the complete manifest
before any participant starts. Lifecycle callbacks must be idempotent by batch
and run identity. They execute as asynchronous Core callbacks; an implementation
that touches Unity objects must marshal that work through `host.Dispatcher`.
If the game permanently abandons a paused participant, call
`ReconcileAbandonedParticipantAsync` with the persisted participant descriptor
and an operation reconciler. Custom durable backends must implement
`IUnityGuardedDurableAgentRuntimeBackend` and truthfully report
`SupportsGuardedResume`; otherwise coordinator creation or participant recovery
fails closed instead of performing an unguarded resume.
The semantic expectation must come from current game state, never from the old
batch manifest.
The optional aggregate budget is reserved from all participant hard budgets
before lifecycle callbacks or provider work; admitted totals are available as
`batchOutcome.Manifest.BudgetReservation`.

For games that reuse entity IDs, populate
`UnityObservationData.audienceIncarnations` and enable
`DurableAgentRuntimeOptions.RequireAudienceIncarnationForRestrictedObservations`.
The bridge writes the bounded protocol extension, and Core rejects stale
private/restricted startup or control observations before provider-visible
context is produced.

See [Documentation](Documentation~/index.md) and the Structured Tool Loop
sample for lifecycle, persistence, DTO bridge, and build instructions.

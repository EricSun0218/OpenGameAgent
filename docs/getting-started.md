# Getting started

## Requirements

- .NET SDK 8 to build and test this repository.
- Godot 4.7 .NET for the verified engine path, or a Unity 2022.3-or-newer
  Editor for the intended Unity package target. Unity Editor and Player gates
  have not yet been executed for this alpha.
- A streaming chat-completions endpoint, or an implementation of
  `IStreamingModelProvider`.

The protocol, core, persistence, provider adapter, and composition package
compile against `netstandard2.1`.

For the Godot-first layer that imports authored characters and
world-book content, exposes typed interactions and portable numeric state,
advances fixed events at discrete clock boundaries, and keeps native packages
separate from saves, see
[Interactive world v1](interactive-world-v1.md).

## 1. Define typed tools

```csharp
var inspectState = new ToolDescriptor
{
    Name = "inspect_state",
    Version = "1",
    Description = "Read authoritative state for one entity.",
    ParametersSchema = ProtocolJson.ParseElement(
        """
        {
          "type":"object",
          "properties":{"entityId":{"type":"string"}},
          "required":["entityId"],
          "additionalProperties":false
        }
        """),
    Effect = ToolEffects.PureRead,
    ConflictScopes = new List<string> { "entity:{entityId}" },
    RetryPolicy = ToolRetryPolicies.Never,
    IdempotencyPolicy = ToolIdempotencyPolicies.None,
    Visibility = ToolVisibilities.Direct
};
```

Use `world_command` or `external_write` for side effects. Those calls form
scheduler barriers. Use `engine_main_thread` when a handler must touch engine
objects.

`Visibility = ToolVisibilities.Deferred` keeps a tool out of the initial
provider schema and makes it eligible for bounded search and exact activation.
`ToolVisibilities.Internal` is never model-visible. A successful model
activation takes effect on the next provider turn, while an authorized deferred
tool required by an explicitly active skill is admitted for that same turn.
Optional skill tools do not auto-activate.

The alpha validates retry and idempotency declarations but does not
automatically repeat host tool calls. Configure provider retry separately.

## 2. Implement the game host

```csharp
public sealed class GameHost : IGameHost
{
    public ValueTask<ActionReceipt> SubmitActionAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        // Validate game-specific rules, mutate state if legal, then return
        // the authoritative result.
        return new ValueTask<ActionReceipt>(receipt);
    }
}
```

Engine packages provide main-thread dispatchers so the handler does not need to
block the frame loop.

## 3. Build the runtime

```csharp
await using var built = new GameAgentRuntimeBuilder(new GameHost())
    .UseFileJournal(journalPath)
    .UseOpenAiCompatibleProvider(providerOptions, credentialSource)
    .WithProviderRouteResilience(
        new ProviderRouteResilienceOptions
        {
            InitialCooldown = TimeSpan.FromSeconds(15),
            MaxCooldown = TimeSpan.FromMinutes(2),
            MaxTrackedRoutes = 256
        })
    .WithTools(new[] { inspectState })
    .WithToolDisclosurePolicy(gameToolDisclosurePolicy)
    .Build();
```

The default route-resilience values match the example. Route-local failures are
shared across runs handled by this built runtime. During cooldown the route is
skipped before dispatch; after cooldown only one run probes it while concurrent
NPC runs use the next configured provider. This availability state is
process-local and is not written into the game journal.

The builder also uses a script-aware conservative token estimator. Override it
for a known tokenizer without changing the agent loop:

```csharp
builder.WithTokenEstimator(myEstimator);
```

A provider can implement `IProviderPromptTokenEstimator` for model-specific
context-window checks. If it also implements
`ICalibratingProviderPromptTokenEstimator`, the runner supplies accounted input
usage after successful completion so the estimator can increase a bounded
safety margin.

Omit `WithToolDisclosurePolicy(...)` to allow every registered deferred tool.
Use `DurableAgentRuntimeOptions.ToolDisclosureLimits` to cap durable
activations, search results, control calls, and query bytes.

The built-in skill admission policy is fail-closed: it accepts only explicitly
activated `builtin` or `trusted` skills with empty capability and activation
objects. Required tool references must exactly match the same turn's captured
tool versions. If the game implements those application-specific declarations,
inject an `ISkillAdmissionPolicy` with
`WithSkillAdmissionPolicy(gameSkillPolicy)`. Custom policies may broaden trust
or declaration handling, but cannot bypass required-tool/version validation.

Runtime shutdown is asynchronous because it cancels and drains active runs
before flushing or disposing the journal. `StopAsync` is the bounded
operational stop and may return while already-started cleanup continues.
`DisposeAsync` and `WaitForShutdownDrainAsync` wait for active runs,
provider-owned cleanup, conversation work, root cancellation callbacks, and
the bounded cancellation/isolation phase for skill-content resolvers. They are
the safe boundary before disposing a runtime-owned or injected journal or
provider transport. They do not claim that a resolver which ignored
cancellation has exited. Inspect
`DetachedSkillContentResolverCallCount` and
`SkillContentResolversDrainedOnStop` for that distinction.
`BuiltGameAgentRuntime` uses this ownership path automatically. Never
synchronously block an engine main thread waiting for shutdown.

An arbitrary host tool callback cannot be forcibly stopped. Shutdown waits for
detached tool callbacks only for the configured bounded interval, then keeps
them quarantined and prevents their late continuation from mutating runtime
state. The host must separately retain any application-owned dependency that
such a callback captured until that callback returns.
`GameAgentRuntimeBuilder` is also async-disposable so a failed build can release
an owned asynchronous store without blocking that thread.

Core workload limits are bounded even without an engine host. A direct durable
runtime can receive a configured ownership registry, while the compact headless
runtime accepts an additional limits argument:

```csharp
var ownership = new RunOwnershipRegistry(
    new RunOwnershipLimits(
        maxActiveRuns: 64,
        maxLanes: 32,
        maxWaitersPerLane: 8));

var headless = new HeadlessAgentRuntime(
    provider,
    gameHost,
    sessionStore,
    clock,
    ids,
    new HeadlessAgentRuntimeLimits(
        maxActiveRuns: 16,
        maxInFlightActions: 16));
```

Engine adapters may impose a lower capacity before work reaches the core.
Capacity rejection is fail-fast; semaphore-backed lane admission guarantees
exclusion but does not promise FIFO ordering.

For games that run background simulation beside player-facing conversations,
reserve provider capacity explicitly:

```csharp
var options = new DurableAgentRuntimeOptions
{
    MaxConcurrentProviderCalls = 3,
    MaxConcurrentBackgroundProviderCalls = 2
};

var monthlyNpcRun = new DurableRunRequest
{
    Run = run,
    WorkloadClass = ProviderWorkloadClasses.Background
};
```

The remaining provider slot stays available to interactive work. Workload
classes control capacity only; the game still owns actor selection, scheduling,
and conflict rules.

`IProviderCredentialSource` lets applications read a token at request time.
Never commit a credential or store it in a scene/resource asset.

Provider-private continuation state is local and ephemeral by default. If a
provider documents that its bounded state contains no secret and must survive a
process restart, both sides must opt in:

```csharp
var options = new DurableAgentRuntimeOptions
{
    AllowProviderDeclaredNonSecretContinuationPersistence = true
};
```

The provider must still mark each update `DurableNonSecret`. The runtime binds
the envelope to its exact provider route and dialect state version, clears it
when a completed response supplies no update, and never persists another value
on terminal completion. Leave the option disabled for bearer tokens, hidden
reasoning, server session secrets, or state of uncertain sensitivity.

`UseFileJournal` supplies the built-in durable implementation. A custom
`IDurableSessionStore` must atomically implement `AppendAtomicBatchAsync`;
multi-tool requests are persisted as one all-or-nothing batch before dispatch.
If the operation ledger is a separate object, it must read the same committed
transactional state. Store decorators must forward batch appends unchanged.

## 4. Start with typed context

```csharp
var request = new DurableRunRequest
{
    Run = run,
    Context = new[]
    {
        new ContextCandidate(
            "tick-104",
            "simulation_tick",
            ProtocolJson.ParseElement(
                """{"tick":104,"weatherId":3,"threatLevel":2}"""),
            priority: 100,
            required: true,
            canDefer: false)
    }
};

DurableRunOutcome result =
    await built.Runtime.RunAsync(request, cancellationToken);
```

Context may contain strings, numbers, arrays, objects, booleans, or resource
references. It does not need to be natural language.

## 5. Resume uncertain work

If `result.ReconciliationRequired` is true, query the game by `operationId` and
resume:

```csharp
DurableRunOutcome resumed = await built.Runtime.ResumeAsync(
    result.Run.RunId,
    reconciler: gameOperationReconciler,
    cancellationToken: cancellationToken);
```

## 6. Author an interactive world in Godot

Enable the packaged Godot plugin and open the **Agent World** dock. Create a
starter in an empty source directory, edit its seven inert JSON files, validate
them, and build a deterministic `.gaworld` archive. The addon includes the
world-v1 JSON Schemas and an interactive example in its `authoring` directory.

Activate that package through the high-level engine session:

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

var snapshot = await world.Native.ReadSnapshotAsync();
byte[] save = await world.CaptureNativeSaveAsync();
```

Queries and mutations use the exact world/timeline/epoch/save/state coordinate
from `snapshot`. Structured interaction parameters can be JSON objects,
numbers, booleans, arrays, or strings; they do not need to be prose. The game
defines all fields and business rules in its package or trusted extension.

An unresolved operation remains paused. It is never automatically repeated.

Games that must reject stale save/timeline/state coordinates can guard a
nonterminal resume with any opaque durable run extension:

```csharp
JsonElement currentCoordinate = GameContextEnvelope.ToJson(currentGameContext);
var guard = new DurableRunResumeGuard
{
    SemanticExtensionName = GameContextEnvelope.ExtensionName,
    ExpectedSemanticExtensionSha256 =
        CanonicalJsonDigest.ComputeSha256(currentCoordinate)
};

DurableRunOutcome resumed = await built.Runtime.ResumeAsync(
    result.Run.RunId,
    continuation: null,
    reconciler: gameOperationReconciler,
    cancellationToken: cancellationToken,
    guard: guard);
```

Set `DurableAgentRuntimeOptions.RequireSemanticResumeGuard = true` when every
nonterminal resume in an integration must supply this pair. The default is
`false` for compatibility. With the option enabled, an unguarded nonterminal
resume fails with `semantic_guard_required` before ownership or side effects.
An already terminal outcome can still be replayed without a guard because it
cannot re-enter the agent loop.

When no skill override is supplied, resume inherits the latest durable active
skills. A non-empty `ActiveSkills` collection replaces them. To explicitly clear
the activation, use:

```csharp
var continuation = new DurableRunContinuation
{
    ActiveSkills = Array.Empty<SkillReference>(),
    ReplaceActiveSkills = true
};
```

Context deferred by a previous execution is not a durable payload store. If the
run returned for reconciliation or the process restarted, pass any still-valid
candidate again through `DurableRunContinuation.Context`:

```csharp
var continuation = new DurableRunContinuation
{
    Context = stillRelevantCandidates
};

DurableRunOutcome resumed = await built.Runtime.ResumeAsync(
    result.Run.RunId,
    continuation,
    gameOperationReconciler,
    cancellationToken);
```

The resumed turn rechecks TTL, priority, and prompt limits. Omitting a candidate
does not cause the runtime to invent or replay its payload from a prior
`deferredIds` report.

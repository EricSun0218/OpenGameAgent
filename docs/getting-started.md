# Getting started

## Requirements

- .NET SDK 8 to build and test this repository.
- Godot 4.7 .NET or a supported Unity editor for an engine package.
- A streaming chat-completions endpoint, or an implementation of
  `IStreamingModelProvider`.

The protocol, core, persistence, provider adapter, and composition package
compile against `netstandard2.1`.

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
    .WithTools(new[] { inspectState })
    .WithToolDisclosurePolicy(gameToolDisclosurePolicy)
    .Build();
```

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
before flushing or disposing the journal. Use `await using`, `DisposeAsync`, or
`StopAsync`; never synchronously block an engine main thread waiting for
shutdown. `GameAgentRuntimeBuilder` is also async-disposable so a failed build
can release an owned asynchronous store without blocking that thread.

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
    new HeadlessAgentRuntimeLimits(maxActiveRuns: 16));
```

Engine adapters may impose a lower capacity before work reaches the core.
Capacity rejection is fail-fast; semaphore-backed lane admission guarantees
exclusion but does not promise FIFO ordering.

`IProviderCredentialSource` lets applications read a token at request time.
Never commit a credential or store it in a scene/resource asset.

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

An unresolved operation remains paused. It is never automatically repeated.

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

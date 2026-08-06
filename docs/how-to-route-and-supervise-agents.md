# Route work and supervise child Agents

Use this guide when one game needs cheap one-turn responses, full action loops,
fixed workflows, and bounded multi-NPC delegation in the same runtime.

## Choose the smallest correct execution surface

Use stateless completion only when the operation needs no durable game context:

```csharp
var result = await built.Completion.CompleteAsync(
    new SimpleCompletionRequest
    {
        OperationId = "classify-intent-42",
        Messages = normalizedMessages,
        MaxOutputTokens = 64,
        Inference = new ModelInferenceOptions
        {
            ReasoningEnabled = false,
            Temperature = 0
        },
        RoutePreference = new ProviderRoutePreference
        {
            ProviderIds = new[] { "fast-dialogue" },
            AllowUnlistedFallback = true
        }
    }, cancellationToken);
```

Use the common router when the result belongs to a durable run:

```csharp
var outcome = await built.Execution.RunAsync(
    new RoutedExecutionRequest
    {
        Route = new ExecutionRouteRequest
        {
            OperationKind = "npc-turn",
            Requirements = ExecutionRequirements.Tools
                           | ExecutionRequirements.DurableEffects,
            Signal = ProtocolJson.ParseElement(
                """{"urgency":"interactive"}""")
        },
        Run = durableRunRequest
    }, cancellationToken);
```

The default policy chooses `Agent` for this request because declared
requirements always win. When requirements are absent, it also reads the
bounded `Signal` and latest normalized user input: a short dialogue can use
`Direct`, while actionable, structured, long, or ambiguous work uses `Agent`.
Use `ExplicitPath` only when the caller already knows the path and can supply
compatible requirements.

Configure automatic model tiers once at composition time:

```csharp
builder.WithAutomaticExecutionRouting(
    new AutomaticExecutionRoutingOptions
    {
        DirectModelProfile = new ExecutionRouteModelProfile
        {
            Inference = new ModelInferenceOptions
            {
                ReasoningEnabled = false,
                ReasoningEffort = ModelReasoningEfforts.None
            },
            RoutePreference = new ProviderRoutePreference
            {
                ProviderIds = new[] { "fast-dialogue" },
                AllowUnlistedFallback = true
            }
        },
        AgentModelProfile = new ExecutionRouteModelProfile
        {
            RoutePreference = new ProviderRoutePreference
            {
                ProviderIds = new[] { "capable-agent" },
                AllowUnlistedFallback = true
            }
        }
    });
```

Provider IDs are application configuration identities, not model-name guesses.
The runtime cannot infer which configured route is cheaper or faster. Explicit
per-run inference or provider preferences override the selected profile.

For a fixed orchestration, compile and register workflows, then attach the
routed workflow runtime:

```csharp
var routedWorkflows = new RoutedWorkflowRuntime(workflowRunner, workflows);

await using var built = new GameAgentRuntimeBuilder(gameHost)
    // Configure persistence, providers, tools, and skills.
    .WithRoutedWorkflowRuntime(routedWorkflows)
    .Build();
```

A workflow request supplies `WorkflowId`, `RunKey`, `OwnerId`, and structured
`Input`; it does not also supply a durable Agent run request.

## Add a game-specific router safely

Implement `IExecutionRoutePolicy` when signals such as distance from the
player, narrative importance, combat state, or latency class affect routing.
Return a stable policy ID, version, path, and reason code. Then register it:

```csharp
builder.WithExecutionRoutePolicy(
    gameRoutePolicy,
    new ExecutionRouterOptions
    {
        PolicyTimeout = TimeSpan.FromMilliseconds(100),
        MaxConcurrentPolicyCalls = 4
    });
```

Do not ask a model merely to choose between `Direct` and `Agent` for every
request. The built-in router resolves obvious inputs locally and invokes an
optional classifier only for ambiguous text. Prefer deterministic requirements
first. Classifier failure or timeout falls back to the conservative local
result; required tools or durability are never skipped.

## Run bounded child Agents

Configure limits at composition time:

```csharp
builder.WithChildAgentSupervisorOptions(
    new ChildAgentSupervisorOptions
    {
        MaxDepth = 3,
        MaxConcurrentChildren = 8,
        MaxActiveChildrenPerParent = 4,
        MaxChildrenPerBatch = 16,
        MaxRememberedLineages = 4096,
        ChildTimeout = TimeSpan.FromSeconds(30)
    });
```

Start one delegated run:

```csharp
ChildAgentRunResult child = await built.Children.RunChildAsync(
    parentRunId,
    childRequest,
    cancellationToken);
```

The supervisor remembers a bounded set of recent child lineages, so a later
delegation by child run ID retains root and depth semantics. Across process
restart or after that bounded cache expires, pass the persisted parent run
instead; its `gameAgent.childLineage` extension is the durable source of truth:

```csharp
ChildAgentRunResult grandchild = await built.Children.RunChildAsync(
    child.Outcome.Run,
    grandchildRequest,
    cancellationToken);
```

Or evaluate many NPCs concurrently:

```csharp
ChildAgentBatchResult batch = await built.Children.RunManyAsync(
    parentRunId,
    npcRequests,
    cancellationToken);

foreach (var item in batch.Items) // original request order
{
    if (item.Succeeded)
    {
        StageForGameResolution(item.Result!.Outcome);
    }
    else
    {
        RecordNpcFailure(item.ChildRunId, item.ErrorType);
    }
}
```

Do not mutate the world merely because a child finished first. Stage results,
then let the game resolve simultaneous actions against one authoritative state
version. Use `CancelChildren(parentRunId)` when the scene, encounter, save, or
parent decision becomes obsolete.

## Attach middleware without moving game rules

Lifecycle middleware is useful for permission prechecks, trace correlation,
telemetry, and audit evidence:

```csharp
builder.WithLifecycleMiddleware(
    new[]
    {
        new AgentLifecycleMiddlewareRegistration(permissionGuard),
        new AgentLifecycleMiddlewareRegistration(telemetry, required: false)
    },
    new AgentLifecyclePipelineOptions
    {
        MiddlewareTimeout = TimeSpan.FromMilliseconds(250),
        MaxConcurrentCalls = 16
    });
```

Keep current-state legality and final mutation in the host tool handler. A
middleware decision can reject work before dispatch, but it cannot assert that
a future world mutation succeeded.

## Keep game time explicit

When child Agents or memory operate in a simulated world, attach named clock,
timeline, epoch, tick, save revision, state version, and observer incarnation to
the run coordinate. Configure `GameAwareMemoryReranker` for game-clock recency;
leave wall-clock recency disabled unless real time is intentionally a game rule.

This prevents paused games, time skips, branch reloads, and parallel simulations
from inheriting assumptions based on process time or arrival order.

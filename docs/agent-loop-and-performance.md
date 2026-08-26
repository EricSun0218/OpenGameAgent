# Agent loop and performance

[中文](agent-loop-and-performance.zh-CN.md)

OpenGameAgent uses one execution model for dialogue, tool use, and complex tasks:

1. Collect bounded game context, skills, and currently authorized tools.
2. Send one model request.
3. If the model returns an assistant message, finish the turn.
4. If the model returns tool calls, validate and execute them, append structured results, refresh context and dynamic tools, then continue the same loop.

There is no preliminary complexity-classifier request and no separate Quick, Agent, Plan, or Workflow runtime. A greeting stays fast because the model answers once. A building, investigation, negotiation, or combat-support task can use one or many tool turns without changing execution engines.

## Optional persistent planning

Persistent goals and ordered task plans are extension tools, not alternate routes. Install `GoalLoopExtension` or `TaskPlanExtension` only when an NPC needs work that survives later inputs. The model may answer directly, use ordinary tools, or create a durable plan from the same loop.

The host controls whether those tools exist for a particular input:

```csharp
var runtime = new GameAgentBuilder(provider, model)
    .Configure(options => options.ExecutionScopeProvider = (input, cancellationToken) =>
        new ValueTask<GameExecutionScope>(
            hostPolicy.AllowsPersistentPlanning(input.SessionId, input.ActorId)
                ? GameExecutionScope.Unrestricted
                : GameExecutionScope.NoOptionalCapabilities))
    .UseExtension(new GoalLoopExtension())
    .UseExtension(new TaskPlanExtension(hostEvidenceValidator))
    .Build();
```

Derive the scope from authenticated host policy. Never turn a client-supplied metadata claim into a capability grant. Withholding persistent planning hides its context and tools before the model request; ordinary game tools remain available. Existing plans remain stored and cannot be modified until a later input receives the capability again.

Fixed game processes—monthly simulation, combat resolution, economy settlement, asset import, or quest state transitions—remain game-owned state machines and tools. They do not need a second model loop.

## Latency attribution

`GameAgentTracingExtension` records bounded timing without copying input payloads, tool arguments, secrets, or hidden reasoning. `GameAgentPerformanceSummary.Create(recording)` separates:

- actor queue and session loading;
- input preparation, named context providers, tool collection, and skill selection;
- each provider request, provider time to first response, and response completion;
- first tool call, each tool duration, approval waiting, authoritative host action time, and durable-action framework time;
- retries, provider fallback, exact tool-repeat protection, uncertain writes, recovery, tokens, and known or unknown cost.

Because there is no classifier request, time to first response and usage belong to the actual Agent work. Direct answers require one provider request; tools add only the model turns that consume their results.

## Performance rules

- Keep context and tool schemas bounded; large tool results should spill to artifacts.
- Filter tool visibility before every model request so the model sees only usable capabilities.
- Use dynamic tool refresh only when a tool turn changed available capabilities.
- Never remove durable action journaling, receipts, conflict coordination, or approval gates to improve latency.
- Benchmark framework overhead separately from provider and game-host latency.

The benchmark harness supports fake or fixed providers, deterministic tools, injected failure, concurrency, JSON/JSONL export, and configurable thresholds. See [DevTools](devtools.md).

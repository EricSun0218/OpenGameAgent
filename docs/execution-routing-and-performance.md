# Execution routing and performance

OpenGameAgent separates a low-latency answer, a bounded tool loop, and durable orchestration without creating multiple incompatible agent runtimes. Every path begins with the same `GameInput`, session, context, provider boundary, usage ledger, trace, and game-owned authority boundary.

[中文](execution-routing-and-performance.zh-CN.md)

## Execution modes

`AutomaticGameRoutePolicy` resolves a route before producing a user-visible answer:

| Input metadata | Meaning |
| --- | --- |
| `agent.route=auto` or absent | Apply typed routes, structural evidence, an optional classifier, then the conservative fallback. |
| `agent.route=quick` | One model turn. No tools are advertised or executed. |
| `agent.route=agent` or `direct` | A bounded short-task agent loop with zero or more tool calls. `direct` hides the official persistent goal/task-plan tools and their guidance. |
| `agent.route=plan` | The normal agent loop with explicit persistent-plan guidance and task-plan tools. |
| `agent.route=workflow:<name>` | A registered deterministic or hybrid workflow. |

Resolution order is explicit metadata, typed route, authoritative pending work, optional classifier, available tools, then `QuickResponse`. Pending work bypasses the classifier because it already requires the full loop. Merely having tools available does not prove that the current input needs them, so a configured classifier may still select the side-effect-free Quick path for ordinary conversation; without a classifier, tools conservatively select Agent. `ModelGameRouteClassifier` usage and latency are recorded as routing work and share the same per-input model-token budget as compaction, workflow output, and the final answer.

Quick mode is deliberately non-speculative. It cannot call a tool, write long-term memory through a tool, create a goal or plan, or mutate the world. `auto` should therefore classify before Quick produces a final response instead of running Quick and replaying the input after side effects may already have occurred. If structural policy cannot decide safely, configure typed routes or `ModelGameRouteClassifier`. An explicitly forced `quick` route is a caller promise and does not silently escalate.

The Agent route handles short tasks directly: multiple tool turns, progress, bounded retry/fallback, steering, follow-up, cancellation, refreshed context, and durable action receipts do not require a persistent plan. When a task grows, the same loop can call the official goal/task-plan tools to create durable work. Completed world writes retain their operation IDs and receipts; creating a plan does not replay them. `direct` intentionally removes those official persistence tools and their prompt guidance, while `plan` supplies explicit guidance. Registered workflows remain preselected named protocols rather than model-created business logic.

## Capability audit

| Requirement | Framework coverage |
| --- | --- |
| Quick / Agent / Workflow | Complete: `GameRouteKind`, `AutomaticGameRoutePolicy`, `IGameWorkflow`. |
| Explicit auto / direct / plan | Complete through `agent.route`; aliases preserve the existing three route kinds. |
| Safe escalation | Complete for `auto` through preflight classification and for Agent-to-plan through `TaskPlanExtension`; intentionally no speculative Quick replay. |
| Quick has no side effects | Complete: the runtime supplies an empty tool list and the kernel stops after one turn. |
| Short multi-tool tasks | Complete in `Agent`, including progress, steering, follow-up, abort, limits, refresh, retry and fallback. |
| Durable complex work | Complete through goals, task plans, waits, workflows, checkpoints, mailboxes, game-time scheduling, and host evidence validation. |
| Shared AI services | Complete through provider/model routing, tools, visibility and policy, memory, context, skills, knowledge, realtime, media, usage, trace, replay and evaluation packages. |
| Stable observable state | Complete through lifecycle events, run results, extension change events/readers, receipts, usage and trace recordings. |
| Authoritative writes | Complete at the framework boundary through typed tools, operation IDs, conflict keys, journaled dispatch, receipts and reconciliation; game rules remain game-owned. |
| Passive agents | Complete: no `RunAsync` input means no route selection, provider request, memory write or plan activity. Deterministic follow/path/animation maintenance belongs to the game. |

## Per-input usage

`GameAgentRunResult.RunUsage` contains only usage caused by the current input. The persisted session ledger remains available through `ReadUsageAsync`. Causes distinguish routing, compaction, assistant turns, tool-related model work, workflows and recovery. Unknown price remains unknown; it is never converted to zero.

## Timing and reliability metrics

Register `GameAgentTracingExtension`, read its bounded recording, then derive a machine-readable summary:

```csharp
var recording = await GameAgentTraceRecordingReader.ReadAsync("traces/run.jsonl", cancellationToken: token);
var metrics = GameAgentPerformanceSummary.Create(recording);

await File.WriteAllTextAsync("artifacts/metrics.json", metrics.ToJson(), token);
await File.WriteAllTextAsync("artifacts/metrics.jsonl", metrics.ToJsonLines(), token);
Console.WriteLine(metrics.ToText());
```

`GameAgentLatencyBreakdown` separates actor-queue delay, input preparation, session load, context, tool collection, routing, skill selection, end-to-end TTFT, provider TTFT, response completion, first tool, model request time, tool execution, authoritative host action time, durable-action framework time, other framework overhead, execution, and total queue-to-completion time.

Tool results use `ToolFailureCategory` (`InvalidArguments`, `Authorization`, `RuleRejected`, `Transient`, `Timeout`, `Cancelled`, `Conflict`, `Internal`, or `Unspecified`). Custom tools should return the most precise category they can prove. Summaries aggregate by tool, category, route, resolved provider, and model. They separately count durable world writes, calculate the uncertain-write rate only over those writes, and track provider retries, task-plan replans, fallbacks, recoveries, and prevented duplicate writes. Operation IDs remain available without recording tool arguments by default. For the official generic goal and task-plan tools, tracing projects only the bounded `action` enum; it still omits objectives, steps, evidence and other arguments.

`DurableGameActionDispatcher.ExecuteDetailedAsync` reports `GameActionDispatchTimings`. `HostMilliseconds` measures the authoritative handler or recovery call. `FrameworkMilliseconds` covers operation serialization, journal work, conflict waiting, idempotency checks, and durable receipt settlement. This avoids blaming a game handler for framework storage or coordination latency.

Provider retry and fallback decorators emit bounded diagnostics only after an actual retry or fallback. Credentials, endpoints, arbitrary headers, prompts, tool arguments and game payloads are not added to performance summaries.

## Realtime and media

`RealtimeMetricsCollector` is an optional bounded observer for first/final input transcript, first/complete output audio, and barge-in cancellation latency. Register `HandleAsync` with `RealtimeConversationManager.RegisterHandler`; call `MarkBargeInRequested` immediately before requesting cancellation.

`GameMediaMetricsCollector.GenerateAsync` wraps any `IGameMediaGenerator` and measures first progress plus asset-available or failure latency. Neither collector changes transport, provider, action, or persistence semantics.

## Benchmark and evaluation harness

`GameAgentBenchmarkRunner` runs bounded scenarios with warmups, concurrency, per-iteration timeout and configurable thresholds. A scenario returns a trace recording, so a game can compose a fixed or fake provider, deterministic tools, injected transport/tool/action failures, or a real allowlisted provider without changing the harness.

```csharp
var scenario = new GameAgentBenchmarkScenario("fixed-provider", async (iteration, token) =>
{
    // Build/run a test runtime and return its bounded trace recording.
    return await RunScenarioAsync(iteration, token);
});

var report = await GameAgentBenchmarkRunner.RunAsync(
    new[] { scenario },
    new GameAgentBenchmarkOptions
    {
        Iterations = 50,
        WarmupIterations = 3,
        MaximumConcurrency = 8,
        IterationTimeout = TimeSpan.FromSeconds(30),
    },
    new GameAgentBenchmarkThresholds
    {
        MaximumFailureRate = 0.01,
        MaximumP95TimeToFirstResponseMilliseconds = 2_000,
        MinimumToolSuccessRate = 0.99,
        MaximumUncertainWrites = 0,
    },
    token);
```

Reports export JSON, JSONL and human-readable text. Thresholds belong to the consuming game or CI environment; OpenGameAgent does not hard-code a provider or network SLA. The harness is observational and never bypasses tool policy, action receipts, idempotency or reconciliation to improve a number.

## Boundaries

- Route policy decides *which execution primitive* to use. It does not decide combat, inventory, pathfinding, gifting, construction, UI, or permissions.
- The game decides when to submit an input. Idle NPCs make zero model calls.
- The game validates and commits every world mutation. Framework traces are evidence about orchestration, not the authoritative world state.
- An explicit `quick` or `direct` choice may restrict capabilities. Use `auto` when the framework should conservatively promote work based on tools, pending work, typed routes or a classifier.

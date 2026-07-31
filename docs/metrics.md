# Runtime metrics

Metrics are an optional live signal. The durable journal remains the source of
truth for recovery and audit.

Configure a sink at composition time:

```csharp
await using var built = new GameAgentRuntimeBuilder(gameHost)
    .UseFileJournal(journalPath)
    .AddProvider(provider)
    .WithMetrics(
        metricsSink,
        new RuntimeMetricsOptions
        {
            Capacity = 4096,
            ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100)
        })
    .Build();
```

`IRuntimeMetricsSink.RecordAsync` is never called on the agent loop, provider
callback, tool scheduler, or engine frame thread. Those paths only enqueue into
a bounded process-local buffer. A full buffer drops metrics and increments
`RuntimeMetricsHealth.Dropped`; a sink exception increments
`RuntimeMetricsHealth.SinkFailures`. Sink failure, slowness, and shutdown
timeout never fail a run or alter durable state.

The runtime emits:

| Metric | Kind | Dimensions |
| --- | --- | --- |
| `runtime.workload.queue_depth` | gauge | `workloadClass` |
| `runtime.workload.queue_wait_ms` | histogram | `workloadClass`, `outcome` |
| `runtime.prompt.assembly_ms` | histogram | `workloadClass`, `outcome` |
| `runtime.prompt.utf8_bytes` | histogram | `workloadClass`, `outcome` |
| `runtime.memory.recall_ms` | histogram | `workloadClass`, `outcome` |
| `runtime.memory.commit_ms` | histogram | `outcome` |
| `runtime.compaction.duration_ms` | histogram | `outcome` |
| `runtime.compaction.reclaimed_messages` | histogram | `outcome` |
| `runtime.tool.queue_depth` | gauge | none |
| `runtime.tool.queue_wait_ms` | histogram | `workloadClass`, `outcome` |
| `runtime.tool.execution_ms` | histogram | `workloadClass`, `outcome` |
| `runtime.provider.ttft_ms` | histogram | `workloadClass`, `outcome` |
| `runtime.provider.stream_duration_ms` | histogram | `workloadClass`, `outcome` |
| `runtime.event_pump.dropped` | counter | `engine`, `outcome` |
| `runtime.event_pump.dispatch_latency_ms` | histogram | `engine`, `outcome` |

The dimension schema is deliberately closed. Workload class, outcome, and
engine values are normalized to finite framework values. Run IDs, agent/NPC
IDs, tool names, model names, decision keys, world IDs, and arbitrary
game-provided strings are not metric dimensions. Put per-run investigation
data in the bounded trace exporter or durable journal instead.

For Godot, configure the event-pump sink before the node enters the scene tree:

```csharp
runtimeNode.ConfigureMetrics(metricsSink);
```

Use the same sink instance for the composition builder and the Godot node when
one exporter should receive both agent-loop and frame-pump metrics. Each
producer retains its own bounded failure-isolation queue.

## Performance gates

`GameAgent.PerformanceSmoke` measures cold and warm multi-actor coordination at
1, 10, and 100 actors, cold and warm physical `FileSessionStore` journal
flushes, context compaction, memory search, streaming coalescing, and trace
export. The Godot headless test floods the bounded event pump with 1,000 events
and verifies both coalesced drop accounting and dispatch-latency coverage.

The gates are intentionally generous enough for shared CI hosts but finite.
Treat a gate failure as a regression investigation, not as a reason to silently
raise the threshold. Record a new local baseline and explain environment or
workload changes before changing a budget.

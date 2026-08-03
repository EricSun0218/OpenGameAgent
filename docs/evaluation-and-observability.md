# Evaluation and observability

`GameAgent.Evaluation` scores recorded evidence without calling a model. A
scenario can evaluate character adherence, action legality, cited-memory
precision/recall, world consistency, latency, tokens, and cost. Missing
required evidence fails closed; illegal actions and inconsistent world state
have independent hard gates. JSON Lines input makes the same suite usable in
CI, offline replay, and model comparisons.

Evaluation is intentionally separate from provider execution. A judge model
may produce evidence in a game-owned test pipeline, but the deterministic
scorer does not hide an extra provider dependency or cost.

`GameAgent.Observability.OpenTelemetry` implements `IRuntimeMetricsSink` with
standard .NET meters and an activity source:

```csharp
services.AddGameAgentOpenTelemetryMetrics();
services.AddOpenTelemetry()
    .AddGameAgentRuntimeInstrumentation()
    .WithMetrics(metrics => /* choose exporter */)
    .WithTracing(tracing => /* choose exporter */);
```

Only `workload.class`, `outcome`, and `engine` are exported as runtime metric
dimensions. Run, player, actor, world, prompt, tool-argument, and model-output
values are deliberately absent. Queue depth is an observable gauge reporting
the latest value rather than a cumulative counter.

Recommended release evidence:

1. deterministic scenario score and hard-gate result;
2. p50/p95/p99 run, first-token, tool, and queue latency;
3. token and cost distributions split only by safe workload class;
4. retry, fallback, dropped-event, unknown-receipt, and reconciliation counts;
5. storage contention, restart, and shutdown-drain results;
6. no identity or prompt data in metric labels.

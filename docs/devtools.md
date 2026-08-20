# Traces, playback, and offline evaluation

`OpenGameAgent.DevTools` turns the bounded lifecycle events produced by `GameAgentTracingExtension` into an append-only JSONL recording. The companion CLI can summarize a recording, generate a self-contained local HTML report, or evaluate it in CI.

Playback is observation-only. It never calls a model, executes a tool, dispatches a durable action, restores a checkpoint, or touches the game host. Use the runtime's normal recovery protocols when an action must actually be reconciled.

## Record a run

Reference the runtime, extensions, and DevTools projects or equivalent release packages:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent/OpenGameAgent.csproj" />
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.Extensions/OpenGameAgent.Extensions.csproj" />
  <ProjectReference Include="path/to/OpenGameAgent/src/OpenGameAgent.DevTools/OpenGameAgent.DevTools.csproj" />
</ItemGroup>
```

Register the sink as an ordinary extension:

```csharp
await using var trace = new JsonLinesGameAgentTraceSink(
    "traces/session-001.jsonl",
    new GameAgentTraceFileOptions
    {
        Mode = GameAgentTraceFileMode.CreateNew,
        MaximumFileBytes = 256L * 1024 * 1024,
        FlushEachEntry = true,
    });

var runtime = new GameAgentBuilder(modelProvider, "model-id")
    .UseExtension(new GameAgentTracingExtension(trace))
    // Add game context, tools, routes, stores, and other extensions here.
    .Build();
```

Input payloads and tool arguments are omitted by default. Enable `GameAgentTracingOptions.IncludeInputPayload` or `IncludeToolArguments` only when the trace's storage and viewer authorization match the data's sensitivity. Credentials are resolved below the trace boundary and are not part of trace entries.

The writer serializes concurrent events, flushes complete JSONL entries, bounds each line and the whole file, and refuses to continue after an interrupted write. The reader accepts a valid final JSON object without a trailing newline and can ignore one crash-truncated final line. Corruption in the middle of a recording fails closed.

Each entry keeps game time (`timelineId`, `tick`, optional calendar JSON) separate from the operational UTC timestamp. Completed runs record provider/model/response identity and full token and known-cost fields. `session.saved` entries include the persistent cumulative usage ledger and per-cause totals.

## Inspect and replay observations

Run the CLI from source:

```powershell
dotnet run --project tools/OpenGameAgent.DevTools.Cli -- \
  inspect traces/session-001.jsonl --out artifacts/session-001.html
```

The report is a self-contained local HTML file with filters, a timeline scrubber, playback speed, failure highlighting, and paged rendering for long sessions. Untrusted trace values are embedded as base64 JSON and rendered with `textContent` under a restrictive content-security policy.

To omit event details from the report:

```powershell
dotnet run --project tools/OpenGameAgent.DevTools.Cli -- \
  inspect traces/session-001.jsonl --no-details
```

## Summarize or evaluate in CI

```powershell
dotnet run --project tools/OpenGameAgent.DevTools.Cli -- \
  summarize traces/session-001.jsonl --out artifacts/summary.json

dotnet run --project tools/OpenGameAgent.DevTools.Cli -- \
  evaluate traces/session-001.jsonl \
  --spec examples/trace-evaluation.json \
  --out artifacts/evaluation.json
```

The `evaluate` command exits with `0` when every rule passes, `2` for a valid recording with failed rules, and `1` for invalid input or storage errors. Specifications are strict JSON: duplicate or unknown properties are rejected.

Built-in rules cover entry, failed-run, tool-call, tool-error, and run-duration limits plus required or forbidden event kinds and tools. Games can add bounded custom rules through `IGameAgentTraceEvaluationRule`; each rule has an independent timeout and a failing rule cannot hang the evaluation process.

## Security and retention

- Treat trace files as potentially private game telemetry.
- Keep payload capture disabled unless it is needed for a controlled test.
- Apply game-owned retention and access policy to recordings and generated reports.
- Do not treat playback as proof that a world mutation did or did not commit; authoritative receipts and journals remain the source of truth.
- Do not use an operational timestamp as narrative time. Evaluate story order against `GameMoment`.

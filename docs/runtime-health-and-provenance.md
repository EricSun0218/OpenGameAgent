# Runtime health and model-context provenance

OpenGameAgent exposes two complementary operational surfaces. Runtime health answers whether configured components can serve work now. Model-context provenance answers which bounded inputs formed a past provider request. Neither surface changes game authority or stores credentials.

## Runtime health

`GameRuntimeHealthMonitor` runs host-registered `IGameRuntimeHealthProbe` implementations with bounded count, concurrency, and timeout. Components use the common kinds `Runtime`, `Provider`, `Mcp`, `LocalEndpoint`, `Realtime`, `Media`, `Extension`, and `Other`, and one of these states:

- `Declared`: configured but not yet resolved;
- `Available`: reachable or loadable, but not fully warmed;
- `Ready`: ready for the declared capability;
- `Degraded`: usable with a known limitation;
- `Unavailable`: the probe could not provide the capability.

Required unavailable components make the aggregate unavailable. Other non-ready required states and degraded components make it degraded. Optional unavailable components are degraded by default and may be configured otherwise.

```csharp
var health = new GameRuntimeHealthMonitor(new IGameRuntimeHealthProbe[]
{
    new StaticGameRuntimeHealthProbe(
        GameRuntimeComponentKind.Runtime,
        "agent-runtime",
        required: true,
        GameRuntimeComponentState.Ready),
    new DelegateGameRuntimeHealthProbe(
        GameRuntimeComponentKind.Provider,
        "local-dialogue",
        required: true,
        async token => await ProbeDialogueProviderAsync(token)),
});

var host = new InProcessGameAgentRuntimeHost(runtime, health: health);
GameRuntimeHealthSnapshot snapshot = await host.ReadHealthAsync(token);
```

The stock server keeps `GET /healthz` as a public process-liveness check. The detailed `GET /v1/health` endpoint follows server API-key protection and returns only bounded component names, states, timing, stable diagnostic codes, and host-authored details. `ServerGameAgentClient.ReadHealthAsync` provides the typed remote projection. Probe exceptions are reduced to an exception type and stable category; response bodies, prompts, credentials, and provider error text are not copied.

## Model-visible context provenance

Register `GameModelContextProvenanceExtension` with an in-memory or file store:

```csharp
var provenance = new FileGameModelContextProvenanceStore(
    Path.Combine(saveRoot, "agent-provenance"));

var runtime = new GameAgentBuilder(provider, model)
    .UseExtension(new GameModelContextProvenanceExtension(provenance))
    .Build();
```

For every model turn, the extension records stable request and response entries containing:

- session, actor, input, run, and turn coordinates;
- run and turn coordinates;
- provider/model request parameters;
- ordered message/content hashes and sizes;
- context source, version, priority, and payload hash;
- selected skill identity and source;
- advertised tool names and schema hashes;
- source/derived/replaced image relationships;
- resolved provider, API, response model, response ID, and stop reason.

Visible prompt, JSON, tool arguments, and skill instructions are not copied by default. Set `GameModelContextProvenanceOptions.IncludeModelVisibleContent` only for a private store whose access policy permits that content. Hidden reasoning and signatures are never copied; only bounded presence, length, and digest diagnostics may be recorded. Credentials never enter the contract.

The file store is append-only, actor-isolated, bounded by entry and file-size limits, idempotent by entry ID, restart-safe after completed appends, and fail-closed on corruption or storage-identity mismatch. Treat it as private debugging/evaluation data and align its retention with the game's save and privacy policy.

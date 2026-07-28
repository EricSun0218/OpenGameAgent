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

The artifact includes `GameAgent.Runtime.dll` for the complete durable
composition builder and `GameAgent.Providers.OpenAICompatible.dll` for an
optional streaming provider. A Unity project can therefore assemble a runtime
from the installed package without separately compiling repository projects.

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

See [Documentation](Documentation~/index.md) and the Structured Tool Loop
sample for lifecycle, persistence, DTO bridge, and build instructions.

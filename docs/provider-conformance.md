# Provider conformance

`GameProviderConformance` is a bounded, provider-neutral validation runner for `IModelProvider` adapters. It lives in `OpenGameAgent.Models`, outside the Kernel and every vendor package. Adapter authors pair it with a scripted HTTP/WebSocket transport so native, compatible, remote, and local providers follow the same normalized-stream rules.

```csharp
var report = await GameProviderConformance.RunAsync(
    providerBackedByScriptedTransport,
    GameProviderConformanceFixtures.CreateToolRequest("fixture-model"),
    new GameProviderConformanceOptions
    {
        RequireProviderIdentity = true,
        ForbiddenValues = new[] { testCredential },
        Timeout = TimeSpan.FromSeconds(10),
    });

if (!report.Passed)
{
    throw new InvalidOperationException(string.Join(
        Environment.NewLine,
        report.Diagnostics.Select(value => $"{value.Code}: {value.Message}")));
}
```

The runner checks preflight before stream consumption, exactly one leading `Started`, bounded event count and duration, text/reasoning/tool content lifecycles, one terminal response, no events after terminal, terminal stop-reason consistency, optional resolved provider/model identity, and forbidden-value exclusion from errors and diagnostics. `RunCancellationProbeAsync` separately verifies that a deliberately blocked transport observes cancellation.

The standard fixtures create bounded text-only and tool-capable requests. They do not call a public model endpoint and contain no credentials. A vendor adapter test remains responsible for feeding representative raw frames into the adapter; the conformance runner verifies the public normalized contract rather than duplicating each wire protocol.

Conformance does not certify service uptime, answer quality, price metadata, or a game-specific tool. Run each provider package's protocol tests as well as:

```powershell
dotnet test tests/OpenGameAgent.Models.Tests -c Release
```


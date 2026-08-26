using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.DevTools.Tests;

public sealed class TracingIntegrationTests
{
    [Fact]
    public async Task RuntimeTraceContainsQueuePhaseModelAndPerInputUsageSignals()
    {
        var sink = new InMemoryGameAgentTraceSink();
        await using var runtime = new GameAgentBuilder(new OneResponseProvider(), "model")
            .Configure(options =>
            {
                options.ContextProvider = new EmptyContextProvider();
                options.Extensions.Add(new GameAgentTracingExtension(sink));
            })
            .Build();

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1), "input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var entries = sink.Snapshot();
        Assert.Contains(entries, value => value.Kind == "model.request.started");
        Assert.Contains(entries, value => value.Kind == "kernel.messagestarted");
        var input = Assert.Single(entries, value => value.Kind == "input.received");
        using (var details = JsonDocument.Parse(input.DetailsJson))
        {
            Assert.True(details.RootElement.GetProperty("queueMilliseconds").GetDouble() >= 0);
            Assert.True(details.RootElement.GetProperty("sessionLoadMilliseconds").GetDouble() >= 0);
        }

        var completed = Assert.Single(entries, value => value.Kind == "run.completed");
        using (var details = JsonDocument.Parse(completed.DetailsJson))
        {
            Assert.Equal(5, details.RootElement.GetProperty("usage").GetProperty("totalTokens").GetInt64());
            Assert.Contains(
                details.RootElement.GetProperty("usageByCause").EnumerateArray(),
                value => value.GetProperty("cause").GetString() == "Assistant");
        }

        var metrics = GameAgentPerformanceSummary.Create(new GameAgentTraceRecording(entries));
        Assert.Single(metrics.Runs);
        Assert.Equal(5, metrics.TotalTokens);
    }

    private sealed class EmptyContextProvider : IGameContextProvider
    {
        public ValueTask<IReadOnlyList<GameContextSlice>> GetContextAsync(
            GameInput input,
            CancellationToken cancellationToken) =>
            new(Array.Empty<GameContextSlice>());
    }

    private sealed class OneResponseProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop,
                new ModelUsage(3, 2),
                provider: "fake",
                responseModel: "fake-model"));
            await Task.CompletedTask;
        }
    }

}

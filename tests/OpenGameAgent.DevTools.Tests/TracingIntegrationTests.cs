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

    [Fact]
    public async Task RouteTraceAndPerformancePreserveClassifierFailureAndFallbackReason()
    {
        var sink = new InMemoryGameAgentTraceSink();
        var classifier = new ModelGameRouteClassifier(new InvalidRouteProvider(), "router");
        await using var runtime = new GameAgentBuilder(new OneResponseProvider(), "model")
            .Configure(options =>
            {
                options.RoutePolicy = new AutomaticGameRoutePolicy(classifier: classifier.ClassifyAsync);
                options.ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
                {
                    new AgentTool(
                        new ToolDefinition("inspect", "Read game state", "{\"type\":\"object\"}"),
                        (_, _, _) => new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("ok") }))),
                });
                options.Extensions.Add(new GameAgentTracingExtension(sink));
            })
            .Build();

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1), "route-fallback"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(GameRouteKind.Agent, result.Route.Route);
        var route = Assert.Single(sink.Snapshot(), value => value.Kind == "route.selected");
        using (var details = JsonDocument.Parse(route.DetailsJson))
        {
            Assert.Equal("fallback", details.RootElement.GetProperty("classificationStatus").GetString());
            Assert.Equal("invalid-json", details.RootElement.GetProperty("classificationFailure").GetString());
            Assert.Equal("tools-available", details.RootElement.GetProperty("classificationFallbackReason").GetString());
            Assert.Equal("classifier-invalid-json-fallback-tools-available", details.RootElement.GetProperty("reason").GetString());
        }

        var performance = GameAgentPerformanceSummary.Create(new GameAgentTraceRecording(sink.Snapshot()));
        var run = Assert.Single(performance.Runs);
        Assert.Equal("invalid-json", run.RouteClassificationFailure);
        Assert.Equal("tools-available", run.RouteFallbackReason);
        Assert.Equal(1, performance.RouteClassificationFailures);
        Assert.Equal(1, performance.RouteFallbacks);
        Assert.True(run.Latency.RoutingModelMilliseconds >= 0);
        Assert.Equal(8, performance.TotalTokens);
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

    private sealed class InvalidRouteProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("not-json") },
                ModelStopReason.Stop,
                new ModelUsage(2, 1),
                provider: "fake-router",
                responseModel: "fake-router-model"));
            await Task.CompletedTask;
        }
    }
}

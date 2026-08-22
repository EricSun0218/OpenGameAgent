using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class ModelContextProvenanceTests
{
    [Fact]
    public async Task RecordsBoundedModelVisibleManifestAndResolvedProviderWithoutHiddenReasoning()
    {
        var store = new InMemoryGameModelContextProvenanceStore();
        var options = new GameAgentRuntimeOptions(new Provider(), "model-a")
        {
            ContextProvider = new ContextProvider(),
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                new AgentTool(
                    new ToolDefinition(
                        "inspect",
                        "secret-description",
                        "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                        new AgentContent[] { new TextContent("ok") })),
                    ToolRisk.ReadOnly),
            }),
        };
        options.Extensions.Add(new GameModelContextProvenanceExtension(store));
        await using var runtime = new GameAgentRuntime(options);
        var input = new GameInput(
            "session",
            "actor",
            "question",
            "{\"secret\":\"context-value\"}",
            new GameMoment("world", 1),
            "input");

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var records = await store.ListAsync(
            new GameSessionKey("session", "actor"),
            "input",
            10,
            TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "model-request", "provider-response" }, records.Select(value => value.Kind));
        var request = records[0].DetailsJson;
        Assert.Contains("world-state", request, StringComparison.Ordinal);
        Assert.Contains("inspect", request, StringComparison.Ordinal);
        Assert.Contains("model-a", request, StringComparison.Ordinal);
        Assert.DoesNotContain("context-value", request, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-description", request, StringComparison.Ordinal);
        Assert.StartsWith("oga-provenance-v1:", records[0].EntryId, StringComparison.Ordinal);
        Assert.Contains("provider-a", records[1].DetailsJson, StringComparison.Ordinal);
    }

    private sealed class ContextProvider : IGameContextProvider
    {
        public ValueTask<IReadOnlyList<GameContextSlice>> GetContextAsync(
            GameInput input,
            CancellationToken cancellationToken) =>
            new(new[] { new GameContextSlice("world-state", "{\"place\":\"hidden-room\"}", version: "7") });
    }

    private sealed class Provider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("answer") },
                ModelStopReason.Stop,
                provider: "provider-a",
                api: "responses",
                responseModel: "resolved-model",
                responseId: "response-1"));
        }
    }
}

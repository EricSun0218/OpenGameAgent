using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class ActionTelemetryTests
{
    [Fact]
    public async Task DetailedDispatchReportsExecutedAndReplayWithoutRepeatingWrite()
    {
        var handler = new CountingHandler();
        var dispatcher = new DurableGameActionDispatcher(new InMemoryGameActionJournal(), handler);
        var intent = new GameActionIntent(
            "operation",
            "input",
            "session",
            "actor",
            "build",
            "{}",
            new GameMoment("world", 1));

        var first = await dispatcher.ExecuteDetailedAsync(intent, TestContext.Current.CancellationToken);
        var replay = await dispatcher.ExecuteDetailedAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(GameActionDispatchDisposition.Executed, first.Disposition);
        Assert.True(first.Timings.TotalMilliseconds >= first.Timings.HostMilliseconds);
        Assert.True(first.Timings.FrameworkMilliseconds >= 0);
        Assert.Equal(GameActionDispatchDisposition.Replayed, replay.Disposition);
        Assert.True(replay.DuplicateExecutionPrevented);
        Assert.Equal(0, replay.Timings.HostMilliseconds);
        Assert.Equal(1, handler.Executions);
    }

    [Fact]
    public async Task ActionToolProjectsOperationAndRuleFailureForMetrics()
    {
        var input = new GameInput(
            "session",
            "actor",
            "command",
            "{}",
            new GameMoment("world", 1),
            inputId: "input",
            metadata: new Dictionary<string, string> { ["agent.route"] = "agent" });
        var dispatcher = new DurableGameActionDispatcher(
            new InMemoryGameActionJournal(),
            new RejectingHandler());
        var tool = GameActionTool.Create(input, "build", "Build", "{\"type\":\"object\"}", dispatcher);
        var options = new GameAgentRuntimeOptions(new ToolThenStopProvider(), "model")
        {
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { tool }),
        };
        using var runtime = new GameAgentRuntime(options);
        ToolResult? observed = null;

        var run = await runtime.RunAsync(
            input,
            (_, value, _) =>
            {
                if (value.Kind == AgentEventKind.ToolEnded)
                {
                    observed = value.ToolResult;
                }

                return default;
            },
            TestContext.Current.CancellationToken);

        Assert.True(run.Succeeded);
        var result = Assert.IsType<ToolResult>(observed);
        Assert.True(result.IsError);
        Assert.Equal(ToolFailureCategory.RuleRejected, result.FailureCategory);
        Assert.Contains("\"operationId\"", result.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"dispatch\":\"executed\"", result.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"hostMilliseconds\"", result.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"frameworkMilliseconds\"", result.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolFailureCategoryRequiresAnErrorResult()
    {
        Assert.Throws<ArgumentException>(() => new ToolResult(
            new AgentContent[] { new TextContent("ok") },
            failureCategory: ToolFailureCategory.Transient));
        Assert.Equal(
            ToolFailureCategory.Timeout,
            ToolResult.Error("timeout", ToolFailureCategory.Timeout).FailureCategory);
    }

    private sealed class CountingHandler : IGameActionHandler
    {
        public int Executions { get; private set; }

        public ValueTask<GameActionReceipt> ExecuteAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executions++;
            return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(intent, "{}"));
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(GameActionIntent intent, CancellationToken cancellationToken) =>
            new((GameActionReceipt?)null);
    }

    private sealed class RejectingHandler : IGameActionHandler
    {
        public ValueTask<GameActionReceipt> ExecuteAsync(GameActionIntent intent, CancellationToken cancellationToken) =>
            new(GameActionReceipt.Rejected(intent, "rule", "not allowed"));

        public ValueTask<GameActionReceipt?> RecoverAsync(GameActionIntent intent, CancellationToken cancellationToken) =>
            new((GameActionReceipt?)null);
    }

    private sealed class ToolThenStopProvider : IModelProvider
    {
        private int _calls;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = Interlocked.Increment(ref _calls) == 1
                ? new ModelResponse(
                    new AgentContent[] { new ToolCallContent("call", "build", "{}") },
                    ModelStopReason.ToolUse,
                    new ModelUsage(1, 1))
                : new ModelResponse(
                    new AgentContent[] { new TextContent("done") },
                    ModelStopReason.Stop,
                    new ModelUsage(1, 1));
            yield return ModelStreamEvent.Terminal(response);
            await Task.CompletedTask;
        }
    }
}

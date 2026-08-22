using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class RunOperationTests
{
    [Fact]
    public async Task NonReplayableToolIsNotExecutedAgainAfterUncertainDispatch()
    {
        var journal = new InMemoryGameRunOperationJournal();
        var executions = 0;
        var input = Input();

        await RunAsync(journal, input, ToolReplayPolicy.Never, Execute, recover: null);
        await RunAsync(journal, input, ToolReplayPolicy.Never, Execute, recover: null);

        Assert.Equal(1, executions);

        ValueTask<ToolResult> Execute(JsonElement _, ToolExecutionContext __, CancellationToken ___)
        {
            Interlocked.Increment(ref executions);
            return new ValueTask<ToolResult>(new ToolResult(
                new AgentContent[] { new TextContent("outcome unknown") },
                isError: true,
                outcomeUncertain: true,
                failureCategory: ToolFailureCategory.Transient));
        }
    }

    [Fact]
    public async Task SafeToolRetriesWhileRecoverableToolUsesRecoveryCallback()
    {
        var input = Input();
        var safeJournal = new InMemoryGameRunOperationJournal();
        var safeExecutions = 0;
        await RunAsync(safeJournal, input, ToolReplayPolicy.Safe, SafeExecute, recover: null);
        await RunAsync(safeJournal, input, ToolReplayPolicy.Safe, SafeExecute, recover: null);
        Assert.Equal(2, safeExecutions);

        var recoverableJournal = new InMemoryGameRunOperationJournal();
        var executions = 0;
        var recoveries = 0;
        await RunAsync(recoverableJournal, input, ToolReplayPolicy.Recoverable, Execute, Recover);
        await RunAsync(recoverableJournal, input, ToolReplayPolicy.Recoverable, Execute, Recover);
        Assert.Equal(1, executions);
        Assert.Equal(1, recoveries);

        ValueTask<ToolResult> SafeExecute(JsonElement _, ToolExecutionContext __, CancellationToken ___)
        {
            Interlocked.Increment(ref safeExecutions);
            return new ValueTask<ToolResult>(Uncertain());
        }

        ValueTask<ToolResult> Execute(JsonElement _, ToolExecutionContext __, CancellationToken ___)
        {
            Interlocked.Increment(ref executions);
            return new ValueTask<ToolResult>(Uncertain());
        }

        ValueTask<ToolResult?> Recover(JsonElement _, ToolExecutionContext __, CancellationToken ___)
        {
            Interlocked.Increment(ref recoveries);
            return new ValueTask<ToolResult?>(new ToolResult(new AgentContent[] { new TextContent("recovered") }));
        }
    }

    [Fact]
    public void OperationIdIsStableAcrossRunIdsAndCanonicalObjectPropertyOrder()
    {
        var input = Input();
        var first = Context("run-a", "{\"x\":1,\"nested\":{\"b\":2,\"a\":1}}");
        var second = Context("run-b", "{\"nested\":{\"a\":1,\"b\":2},\"x\":1}");

        var firstId = GameRunToolOperationIds.CreateV1(input, first);
        var secondId = GameRunToolOperationIds.CreateV1(input, second);

        Assert.Equal(firstId, secondId);
        Assert.True(GameRunToolOperationIds.IsVersion1(firstId));
        Assert.Equal(GameRunToolOperationIds.Version1Prefix.Length + 64, firstId.Length);
    }

    private static async Task RunAsync(
        IGameRunOperationJournal journal,
        GameInput input,
        ToolReplayPolicy replayPolicy,
        Func<JsonElement, ToolExecutionContext, CancellationToken, ValueTask<ToolResult>> execute,
        Func<JsonElement, ToolExecutionContext, CancellationToken, ValueTask<ToolResult?>>? recover)
    {
        var provider = new ToolThenTextProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            RunOperationJournal = journal,
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["command"] = GameRouteDecision.Agent("test"),
            }),
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                new AgentTool(
                    new ToolDefinition("ordinary", "Ordinary test tool.", "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\"}},\"required\":[\"x\"],\"additionalProperties\":false}"),
                    execute,
                    replayPolicy == ToolReplayPolicy.Never ? ToolRisk.NonIdempotentWrite : ToolRisk.IdempotentWrite,
                    replayPolicy: replayPolicy,
                    recover: recover),
            }),
        });

        var result = await runtime.RunAsync(input, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
    }

    private static ToolResult Uncertain() => new(
        new AgentContent[] { new TextContent("unknown") },
        isError: true,
        outcomeUncertain: true,
        failureCategory: ToolFailureCategory.Transient);

    private static GameInput Input() => new(
        "session",
        "actor",
        "command",
        "{}",
        new GameMoment("world", 1),
        "input");

    private static BeforeToolExecutionContext Context(string runId, string arguments) => new(
        runId,
        1,
        0,
        new ToolCallContent("call", "ordinary", arguments),
        JsonDocument.Parse(arguments).RootElement,
        null,
        ToolRisk.ReadOnly,
        ToolReplayPolicy.Safe,
        new AgentContext(string.Empty, Array.Empty<AgentMessage>(), Array.Empty<AgentTool>()));

    private sealed class ToolThenTextProvider : IModelProvider
    {
        private int _calls;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            var response = call == 1
                ? new ModelResponse(
                    new AgentContent[] { new ToolCallContent("call", "ordinary", "{\"x\":1}") },
                    ModelStopReason.ToolUse)
                : new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop);
            yield return ModelStreamEvent.Terminal(response);
            await Task.CompletedTask;
        }
    }
}

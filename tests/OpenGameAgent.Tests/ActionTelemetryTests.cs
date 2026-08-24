using System.Collections.Concurrent;
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
        Assert.Equal(result.DetailsJson, Assert.Single(result.Content.OfType<JsonContent>()).Json);
    }

    [Fact]
    public async Task ActionToolCanProjectOnlySemanticReceiptFieldsToTheModel()
    {
        var input = new GameInput(
            "session",
            "actor",
            "command",
            "{}",
            new GameMoment("private-timeline", 42),
            inputId: "input",
            metadata: new Dictionary<string, string> { ["agent.route"] = "agent" });
        var journal = new InMemoryGameActionJournal();
        var handler = new SemanticResultHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        var provider = new ToolThenStopProvider();
        var tool = GameActionTool.Create(
            input,
            "build",
            "Build",
            "{\"type\":\"object\"}",
            dispatcher,
            modelReceiptProjector: context => JsonSerializer.Serialize(new
            {
                action = context.Intent.Action,
                status = context.Receipt.Status.ToString().ToLowerInvariant(),
                label = GameJsonForTest(context.Receipt.ResultJson).GetProperty("label").GetString(),
                reward = GameJsonForTest(context.Receipt.ResultJson).GetProperty("reward").GetInt32(),
            }));
        using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { tool }),
        });

        var run = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(run.Succeeded);
        var secondRequest = Assert.Single(provider.Requests.Skip(1));
        var toolMessage = Assert.Single(secondRequest.Messages, message => message.Role == AgentRole.Tool);
        var projected = Assert.Single(toolMessage.Content.OfType<JsonContent>()).Json;
        Assert.Equal("{\"action\":\"build\",\"status\":\"committed\",\"label\":\"Oak house\",\"reward\":3}", projected);
        Assert.DoesNotContain("operationId", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("stateRevision", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("timeline", projected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tick", projected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dispatch", projected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duplicate", projected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recovered", projected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Milliseconds", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("hostNote", projected, StringComparison.Ordinal);
        Assert.Contains("\"operationId\"", toolMessage.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"timelineId\":\"private-timeline\"", toolMessage.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"hostNote\":\"internal-only\"", toolMessage.DetailsJson, StringComparison.Ordinal);

        var intent = Assert.IsType<GameActionIntent>(handler.Intent);
        var canonical = await journal.FindAsync(intent.OperationId, TestContext.Current.CancellationToken);
        Assert.NotNull(canonical?.Receipt);
        Assert.Equal("private-timeline", canonical.Receipt.Moment.TimelineId);
        Assert.Equal(42, canonical.Receipt.Moment.Tick);
        Assert.Equal(9, canonical.Receipt.StateRevision);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("invalid")]
    [InlineData("non-object")]
    [InlineData("oversized")]
    public async Task InvalidModelReceiptProjectionFailsClosedWithoutCanonicalFallback(string failure)
    {
        var input = CreateActionInput();
        var journal = new InMemoryGameActionJournal();
        var handler = new SemanticResultHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        GameActionModelReceiptProjector projector = failure switch
        {
            "throw" => _ => throw new InvalidOperationException("private-timeline operationId hostNote"),
            "invalid" => _ => "{not-json",
            "non-object" => _ => "[\"operationId\"]",
            "oversized" => _ => "{\"value\":\"" + new string('x', GameActionTool.MaximumModelReceiptCharacters) + "\"}",
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var provider = new ToolThenStopProvider();
        var tool = GameActionTool.Create(
            input,
            "build",
            "Build",
            "{\"type\":\"object\"}",
            dispatcher,
            modelReceiptProjector: projector);
        using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { tool }),
        });
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
        Assert.Single(provider.Requests);
        var result = Assert.IsType<ToolResult>(observed);
        Assert.True(result.IsError);
        Assert.True(result.Terminate);
        Assert.False(result.OutcomeUncertain);
        Assert.Equal(ToolFailureCategory.Internal, result.FailureCategory);
        var modelJson = Assert.Single(result.Content.OfType<JsonContent>()).Json;
        Assert.Equal("{\"status\":\"projection_failed\"}", modelJson);
        Assert.DoesNotContain("operationId", modelJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-timeline", modelJson, StringComparison.Ordinal);
        Assert.DoesNotContain("hostNote", modelJson, StringComparison.Ordinal);
        Assert.Contains("\"operationId\"", result.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"hostNote\":\"internal-only\"", result.DetailsJson, StringComparison.Ordinal);
        var entry = await journal.FindAsync(
            Assert.IsType<GameActionIntent>(handler.Intent).OperationId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(entry?.Receipt);
    }

    [Fact]
    public async Task ModelReceiptProjectionIsStableAcrossDurableReplay()
    {
        var input = CreateActionInput();
        var journal = new InMemoryGameActionJournal();
        var handler = new SemanticResultHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        static string Project(GameActionModelReceiptProjectionContext context) => JsonSerializer.Serialize(new
        {
            action = context.Intent.Action,
            status = context.Receipt.Status.ToString().ToLowerInvariant(),
            result = GameJsonForTest(context.Receipt.ResultJson),
        });

        var first = await RunProjectedActionAsync(input, dispatcher, Project);
        var replay = await RunProjectedActionAsync(input, dispatcher, Project);

        Assert.Equal(first.ModelJson, replay.ModelJson);
        Assert.Contains("\"dispatch\":\"executed\"", first.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"dispatch\":\"replayed\"", replay.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"duplicateExecutionPrevented\":true", replay.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("dispatch", replay.ModelJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duplicate", replay.ModelJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.Executions);
    }

    [Fact]
    public async Task ProjectedReceiptRoundTripsThroughRunJournalWithoutReprojection()
    {
        var input = CreateActionInput();
        var actionJournal = new InMemoryGameActionJournal();
        var runJournal = new InMemoryGameRunOperationJournal();
        var handler = new SemanticResultHandler();
        var dispatcher = new DurableGameActionDispatcher(actionJournal, handler);
        var projections = 0;
        string Project(GameActionModelReceiptProjectionContext context)
        {
            Interlocked.Increment(ref projections);
            return JsonSerializer.Serialize(new
            {
                action = context.Intent.Action,
                status = context.Receipt.Status.ToString().ToLowerInvariant(),
            });
        }

        var first = await RunProjectedActionAsync(input, dispatcher, Project, runJournal);
        var replay = await RunProjectedActionAsync(input, dispatcher, Project, runJournal);

        Assert.Equal(first.ModelJson, replay.ModelJson);
        Assert.Equal(first.CanonicalJson, replay.CanonicalJson);
        Assert.Equal(1, projections);
        Assert.Equal(1, handler.Executions);
    }

    [Fact]
    public async Task ModelReceiptProjectionDoesNotHideCanonicalRecoveryFromTheHost()
    {
        var input = CreateActionInput();
        var journal = new InMemoryGameActionJournal();
        var expectedIntent = new GameActionIntent(
            "operation",
            input.InputId,
            input.SessionId,
            input.ActorId,
            "build",
            "{}",
            input.Moment);
        await journal.ReserveAsync(expectedIntent, TestContext.Current.CancellationToken);
        Assert.True(await journal.MarkDispatchedAsync("operation", TestContext.Current.CancellationToken));
        var handler = new RecoveringHandler();
        var dispatcher = new DurableGameActionDispatcher(journal, handler);
        var provider = new ToolThenStopProvider();
        var tool = GameActionTool.Create(
            input,
            "build",
            "Build",
            "{\"type\":\"object\"}",
            dispatcher,
            operationIdFactory: (_, _, _) => "operation",
            modelReceiptProjector: context => JsonSerializer.Serialize(new
            {
                action = context.Intent.Action,
                status = context.Receipt.Status.ToString().ToLowerInvariant(),
            }));
        using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { tool }),
        });

        var run = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(run.Succeeded);
        var toolMessage = Assert.Single(Assert.Single(provider.Requests.Skip(1)).Messages, message => message.Role == AgentRole.Tool);
        var modelJson = Assert.Single(toolMessage.Content.OfType<JsonContent>()).Json;
        Assert.Equal("{\"action\":\"build\",\"status\":\"committed\"}", modelJson);
        Assert.DoesNotContain("recovered", modelJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"dispatch\":\"recovered\"", toolMessage.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"recovered\":true", toolMessage.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(0, handler.Executions);
        Assert.Equal(1, handler.Recoveries);
        var entry = await journal.FindAsync("operation", TestContext.Current.CancellationToken);
        Assert.Equal(GameActionStatus.Committed, entry?.Receipt?.Status);
        Assert.Equal(11, entry?.Receipt?.StateRevision);
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

    private sealed class SemanticResultHandler : IGameActionHandler
    {
        public GameActionIntent? Intent { get; private set; }

        public int Executions { get; private set; }

        public ValueTask<GameActionReceipt> ExecuteAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intent = intent;
            Executions++;
            return new ValueTask<GameActionReceipt>(
                GameActionReceipt.Committed(
                    intent,
                    "{\"label\":\"Oak house\",\"reward\":3,\"hostNote\":\"internal-only\"}",
                    9));
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(GameActionIntent intent, CancellationToken cancellationToken) =>
            new((GameActionReceipt?)null);
    }

    private sealed class RecoveringHandler : IGameActionHandler
    {
        public int Executions { get; private set; }

        public int Recoveries { get; private set; }

        public ValueTask<GameActionReceipt> ExecuteAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            Executions++;
            return new ValueTask<GameActionReceipt>(GameActionReceipt.Committed(intent, "{}", 11));
        }

        public ValueTask<GameActionReceipt?> RecoverAsync(GameActionIntent intent, CancellationToken cancellationToken)
        {
            Recoveries++;
            return new ValueTask<GameActionReceipt?>(GameActionReceipt.Committed(intent, "{}", 11));
        }
    }

    private sealed class ToolThenStopProvider : IModelProvider
    {
        private int _calls;

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
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

    private static JsonElement GameJsonForTest(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static GameInput CreateActionInput() => new(
        "session",
        "actor",
        "command",
        "{}",
        new GameMoment("private-timeline", 42),
        inputId: "input",
        metadata: new Dictionary<string, string> { ["agent.route"] = "agent" });

    private static async Task<(string ModelJson, string CanonicalJson)> RunProjectedActionAsync(
        GameInput input,
        DurableGameActionDispatcher dispatcher,
        GameActionModelReceiptProjector projector,
        IGameRunOperationJournal? runOperationJournal = null)
    {
        var provider = new ToolThenStopProvider();
        var tool = GameActionTool.Create(
            input,
            "build",
            "Build",
            "{\"type\":\"object\"}",
            dispatcher,
            modelReceiptProjector: projector);
        using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "model")
        {
            RunOperationJournal = runOperationJournal,
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[] { tool }),
        });

        var run = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(run.Succeeded);
        var toolMessage = Assert.Single(Assert.Single(provider.Requests.Skip(1)).Messages, message => message.Role == AgentRole.Tool);
        return (
            Assert.Single(toolMessage.Content.OfType<JsonContent>()).Json,
            Assert.IsType<string>(toolMessage.DetailsJson));
    }
}

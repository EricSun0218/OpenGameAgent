using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class OfficialExtensionTests
{
    [Fact]
    public async Task StructuredInteractionUsesOneEngineNeutralBrokerContract()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? new ModelResponse(
                new AgentContent[]
                {
                    new ToolCallContent(
                        "ask-1",
                        "ask_player",
                        """
                        {"questions":[{"id":"approach","prompt":"Choose an approach","options":[{"id":"safe","label":"Safe","description":"Validate first","recommended":true},{"id":"fast","label":"Fast","description":"Skip optional checks"}]}]}
                        """),
                },
                ModelStopReason.ToolUse)
            : new ModelResponse(new AgentContent[] { new TextContent("done") }, ModelStopReason.Stop));
        var broker = new RecordingBroker();
        var lifecycle = new ConcurrentQueue<string>();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new StructuredInteractionExtension(broker))
            .UseExtension(
                "interaction.listener",
                "1",
                api =>
                {
                    api.Subscribe(StructuredInteractionExtension.InteractionStarted, (_, _) =>
                    {
                        lifecycle.Enqueue("started");
                        return ValueTask.CompletedTask;
                    });
                    api.Subscribe(StructuredInteractionExtension.InteractionCompleted, (_, _) =>
                    {
                        lifecycle.Enqueue("completed");
                        return ValueTask.CompletedTask;
                    });
                })
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(broker.Requests);
        var question = Assert.Single(request.Questions);
        Assert.Equal("safe", Assert.Single(question.Options, option => option.Recommended).Id);
        Assert.Equal(new[] { "started", "completed" }, lifecycle.ToArray());
        var secondRequest = provider.Requests.ElementAt(1);
        var toolResult = secondRequest.Messages.Last(message => message.Role == AgentRole.Tool);
        var json = Assert.IsType<JsonContent>(Assert.Single(toolResult.Content)).Json;
        Assert.Contains("safe", json);
    }

    [Fact]
    public void StructuredInteractionContractsRejectAmbiguousCancelledOrUnboundedAnswers()
    {
        Assert.Throws<ArgumentException>(() => new GameInteractionResponse(
            true,
            new[] { new GameInteractionAnswer("question", new[] { "choice" }) }));
        Assert.Throws<ArgumentException>(() => new GameInteractionAnswer(
            "question",
            Enumerable.Range(0, 9).Select(index => "choice-" + index)));
        Assert.Throws<ArgumentException>(() => new GameInteractionAnswer(
            new string('q', 129),
            new[] { "choice" }));
    }

    [Fact]
    public async Task ToolPolicyDenialPreventsBusinessHandlerExecution()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? new ModelResponse(
                new AgentContent[] { new ToolCallContent("delete-1", "delete_world", "{}") },
                ModelStopReason.ToolUse)
            : new ModelResponse(new AgentContent[] { new TextContent("denied") }, ModelStopReason.Stop));
        var executed = 0;
        var audits = new ConcurrentQueue<GameToolPolicyAudit>();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "game.tools",
                "1",
                api => api.RegisterTool(new AgentTool(
                    new ToolDefinition("delete_world", "Delete the world.", "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executed);
                        return new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("deleted") }));
                    },
                    ToolRisk.NonIdempotentWrite)))
            .UseExtension(new ToolPolicyExtension(new[] { new DenyDeletePolicy() }))
            .UseExtension(
                "policy.listener",
                "1",
                api => api.Subscribe(ToolPolicyExtension.DecisionRecorded, (audit, _) =>
                {
                    audits.Enqueue(audit);
                    return ValueTask.CompletedTask;
                }))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Contains(provider.Requests.First().Tools, tool => tool.Name == "delete_world");
        Assert.Equal(0, Volatile.Read(ref executed));
        var audit = Assert.Single(audits);
        Assert.Equal(GameToolPolicyOutcome.Deny, audit.Outcome);
        Assert.Equal("delete_world", audit.ToolName);
        var toolMessage = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
        Assert.True(toolMessage.IsError);
    }

    [Fact]
    public async Task PolicyExceptionsFailClosedByDefault()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? new ModelResponse(
                new AgentContent[] { new ToolCallContent("call", "write", "{}") },
                ModelStopReason.ToolUse)
            : new ModelResponse(new AgentContent[] { new TextContent("handled") }, ModelStopReason.Stop));
        var executed = false;
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "game.tools",
                "1",
                api => api.RegisterTool(new AgentTool(
                    new ToolDefinition("write", "Write game state.", "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) =>
                    {
                        executed = true;
                        return new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("written") }));
                    },
                    ToolRisk.IdempotentWrite)))
            .UseExtension(new ToolPolicyExtension(new[] { new ThrowingPolicy() }))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(executed);
        var toolMessage = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
        Assert.Contains("failed closed", Assert.IsType<TextContent>(Assert.Single(toolMessage.Content)).Text);
    }

    [Fact]
    public async Task ToolCatalogActivatesOnlySelectedSchemaOnNextTurn()
    {
        var provider = new ScriptedProvider(call => call switch
        {
            1 => new ModelResponse(
                new AgentContent[]
                {
                    new ToolCallContent("activate", "set_active_game_tools", "{\"names\":[\"build_house\"]}"),
                },
                ModelStopReason.ToolUse),
            2 => new ModelResponse(
                new AgentContent[] { new ToolCallContent("build", "build_house", "{}") },
                ModelStopReason.ToolUse),
            _ => new ModelResponse(new AgentContent[] { new TextContent("built") }, ModelStopReason.Stop),
        });
        var executions = 0;
        var catalog = new InMemoryGameToolCatalog(new[]
        {
            new GameToolCatalogEntry(
                "build_house",
                "Build a house in the current settlement.",
                (_, _) => new ValueTask<AgentTool>(new AgentTool(
                    new ToolDefinition("build_house", "Build a house.", "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("receipt") }));
                    },
                    ToolRisk.IdempotentWrite)),
                tags: new[] { "building", "settlement" }),
        });
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new ToolCatalogExtension(catalog))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, executions);
        Assert.Equal(
            new[] { "search_game_tools", "set_active_game_tools" },
            provider.Requests.First().Tools.Select(tool => tool.Name).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Assert.Contains(provider.Requests.ElementAt(1).Tools, tool => tool.Name == "build_house");
    }

    [Fact]
    public async Task GoalLoopWaitsOnGameTimeAndEventThenResumesDurably()
    {
        var provider = new ScriptedProvider(call => call switch
        {
            1 => ToolCall("create", "manage_goal", "{\"action\":\"create\",\"goalId\":\"monthly-plan\",\"objective\":{\"kind\":\"advance_month\"}}"),
            2 => ToolCall("wait", "manage_goal", "{\"action\":\"wait\",\"goalId\":\"monthly-plan\",\"expectedRevision\":1,\"notBeforeTick\":10,\"eventTypes\":[\"month_advanced\"]}"),
            3 => TextResponse("waiting"),
            4 => TextResponse("still waiting"),
            5 => ToolCall("complete", "manage_goal", "{\"action\":\"complete\",\"goalId\":\"monthly-plan\",\"expectedRevision\":3}"),
            _ => TextResponse("complete"),
        });
        var store = new InMemoryGameSessionStore();
        var changes = new ConcurrentQueue<GameGoalChanged>();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseSessionStore(store)
            .UseExtension(new GoalLoopExtension())
            .UseExtension(
                "goal.listener",
                "1",
                api => api.Subscribe(GoalLoopExtension.GoalChanged, (change, _) =>
                {
                    changes.Enqueue(change);
                    return ValueTask.CompletedTask;
                }))
            .Build();

        await runtime.RunAsync(
            new GameInput("session", "actor", "request", "{}", new GameMoment("world", 1), "one"),
            TestContext.Current.CancellationToken);
        await runtime.RunAsync(
            new GameInput("session", "actor", "unrelated", "{}", new GameMoment("world", 10), "two"),
            TestContext.Current.CancellationToken);
        await runtime.RunAsync(
            new GameInput("session", "actor", "month_advanced", "{}", new GameMoment("world", 10), "three"),
            TestContext.Current.CancellationToken);

        var snapshot = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        var stateJson = Assert.Single(snapshot!.ExtensionState).Value;
        using var document = System.Text.Json.JsonDocument.Parse(stateJson);
        Assert.Equal("Completed", document.RootElement.GetProperty("Status").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("Revision").GetInt64());
        Assert.All(changes, change => Assert.Equal(new GameSessionKey("session", "actor"), change.Session));
        Assert.Contains(changes, change => change.Reason == "resumed" && change.InputId == "three");
    }

    [Fact]
    public async Task GoalLoopRetainsActiveAndWaitingGoalsWhileBoundingTerminalAuditHistory()
    {
        var provider = new ScriptedProvider(call => call switch
        {
            1 => ToolCall("create-waiting", "manage_goal", "{\"action\":\"create\",\"goalId\":\"waiting\",\"objective\":{}}"),
            2 => ToolCall("wait", "manage_goal", "{\"action\":\"wait\",\"goalId\":\"waiting\",\"expectedRevision\":1,\"eventTypes\":[\"future\"]}"),
            3 => ToolCall("create-old", "manage_goal", "{\"action\":\"create\",\"goalId\":\"old\",\"objective\":{}}"),
            4 => ToolCall("complete-old", "manage_goal", "{\"action\":\"complete\",\"goalId\":\"old\",\"expectedRevision\":1}"),
            5 => ToolCall("create-recent", "manage_goal", "{\"action\":\"create\",\"goalId\":\"recent\",\"objective\":{}}"),
            6 => ToolCall("complete-recent", "manage_goal", "{\"action\":\"complete\",\"goalId\":\"recent\",\"expectedRevision\":1}"),
            7 => ToolCall("create-active", "manage_goal", "{\"action\":\"create\",\"goalId\":\"active\",\"objective\":{}}"),
            _ => TextResponse("created"),
        });
        var store = new InMemoryGameSessionStore();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseSessionStore(store)
            .UseExtension(new GoalLoopExtension(new GoalLoopOptions
            {
                MaximumActiveGoals = 2,
                MaximumRetainedTerminalGoals = 1,
            }))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var snapshot = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        var goals = snapshot!.ExtensionState.Values
            .Select(json =>
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                return (
                    Id: document.RootElement.GetProperty("Id").GetString(),
                    Status: document.RootElement.GetProperty("Status").GetString());
            })
            .ToDictionary(goal => goal.Id!, goal => goal.Status, StringComparer.Ordinal);
        Assert.Equal(3, goals.Count);
        Assert.Equal("Active", goals["active"]);
        Assert.Equal("Waiting", goals["waiting"]);
        Assert.Equal("Completed", goals["recent"]);
        Assert.DoesNotContain("old", goals);
    }

    [Fact]
    public async Task ConcurrentGoalUpdatesUseSessionCasWithoutLosingExistingActiveGoals()
    {
        var store = new InMemoryGameSessionStore();
        await using (var seedRuntime = new GameAgentBuilder(
                new ScriptedProvider(call => call == 1
                    ? ToolCall("create-base", "manage_goal", "{\"action\":\"create\",\"goalId\":\"base\",\"objective\":{}}")
                    : TextResponse("seeded")),
                "model")
            .UseSessionStore(store)
            .UseExtension(new GoalLoopExtension())
            .Build())
        {
            Assert.True((await seedRuntime.RunAsync(Input(), TestContext.Current.CancellationToken)).Succeeded);
        }

        var gate = new ConcurrentRunGate(2);
        await using var leftRuntime = new GameAgentBuilder(
                new FirstCallBarrierProvider(
                    gate,
                    ToolCall("create-left", "manage_goal", "{\"action\":\"create\",\"goalId\":\"left\",\"objective\":{}}")),
                "model")
            .UseSessionStore(store)
            .UseExtension(new GoalLoopExtension())
            .Build();
        await using var rightRuntime = new GameAgentBuilder(
                new FirstCallBarrierProvider(
                    gate,
                    ToolCall("create-right", "manage_goal", "{\"action\":\"create\",\"goalId\":\"right\",\"objective\":{}}")),
                "model")
            .UseSessionStore(store)
            .UseExtension(new GoalLoopExtension())
            .Build();

        var results = await Task.WhenAll(
            leftRuntime.RunAsync(
                new GameInput("session", "actor", "request", "{}", new GameMoment("world", 6), "left-input"),
                TestContext.Current.CancellationToken),
            rightRuntime.RunAsync(
                new GameInput("session", "actor", "request", "{}", new GameMoment("world", 6), "right-input"),
                TestContext.Current.CancellationToken));

        // A winning tool checkpoint can commit before a later usage settlement advances the
        // session again, so both callers may conservatively report a conflict under load.
        Assert.Contains(results, result => result.Status == GameAgentRunStatus.SessionConflict);
        Assert.All(
            results,
            result => Assert.True(
                result.Status is GameAgentRunStatus.Completed or GameAgentRunStatus.SessionConflict,
                $"Unexpected concurrent run status '{result.Status}'."));
        var snapshot = await store.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        var goalIds = snapshot!.ExtensionState.Values
            .Select(json =>
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                return document.RootElement.GetProperty("Id").GetString();
            })
            .ToArray();
        Assert.Contains("base", goalIds);
        Assert.True(goalIds.Contains("left", StringComparer.Ordinal) ^ goalIds.Contains("right", StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkflowGraphRunsIndependentNodesConcurrentlyAndJoinsInDeclarationOrder()
    {
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<GameWorkflowNodeResult> ParallelNode(
            GameWorkflowNodeContext context,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(cancellationToken);
            if (context.NodeId == "first")
            {
                await Task.Delay(20, cancellationToken);
            }

            return GameWorkflowNodeResult.Complete(
                System.Text.Json.JsonSerializer.Serialize(new { node = context.NodeId }),
                Assistant(context.NodeId));
        }

        var checkpoints = new InMemoryGameWorkflowCheckpointStore();
        var graph = new DurableGameWorkflowGraph(
            "evolve",
            new[]
            {
                new GameWorkflowNode("first", ParallelNode),
                new GameWorkflowNode("second", ParallelNode),
                new GameWorkflowNode(
                    "join",
                    (context, _) =>
                    {
                        Assert.Contains("first", context.DependencyOutputs["first"], StringComparison.Ordinal);
                        Assert.Contains("second", context.DependencyOutputs["second"], StringComparison.Ordinal);
                        return new ValueTask<GameWorkflowNodeResult>(
                            GameWorkflowNodeResult.Complete("{\"joined\":true}", Assistant("join")));
                    },
                    new[] { "first", "second" }),
            },
            checkpoints,
            maximumConcurrentNodes: 2);
        var sessions = new InMemoryGameSessionStore();
        var provider = new ScriptedProvider(_ => throw new InvalidOperationException("Workflow must not call the model."));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseSessionStore(sessions)
            .UseExtension("game.workflows", "1", api => api.RegisterWorkflow(graph))
            .Build();

        var result = await runtime.RunAsync(
            WorkflowInput("one", "shared"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Empty(provider.Requests);
        var session = await sessions.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            new[] { "first", "second", "join" },
            session!.Messages
                .Where(message => message.Role == AgentRole.Assistant)
                .Select(message => Assert.IsType<TextContent>(Assert.Single(message.Content)).Text));
        var instanceId = string.Join(":", new[] { "session", "actor", "evolve", "shared" }.Select(Uri.EscapeDataString));
        Assert.True((await checkpoints.LoadAsync(instanceId, TestContext.Current.CancellationToken))!.Completed);
    }

    [Fact]
    public async Task WorkflowGraphWaitsAndResumesOnlyTheBlockedBranch()
    {
        var attempts = 0;
        var checkpoints = new InMemoryGameWorkflowCheckpointStore();
        var graph = new DurableGameWorkflowGraph(
            "evolve",
            new[]
            {
                new GameWorkflowNode("always", (_, _) => new ValueTask<GameWorkflowNodeResult>(
                    GameWorkflowNodeResult.Complete("{\"stable\":true}", Assistant("always")))),
                new GameWorkflowNode("wait", (context, _) =>
                {
                    var attempt = Interlocked.Increment(ref attempts);
                    Assert.Equal(attempt == 1 ? "{}" : "{\"waiting\":true}", context.PreviousOutputJson);
                    return new ValueTask<GameWorkflowNodeResult>(attempt == 1
                        ? GameWorkflowNodeResult.Wait("{\"waiting\":true}", Assistant("waiting"))
                        : GameWorkflowNodeResult.Complete("{\"ready\":true}", Assistant("ready")));
                }),
                new GameWorkflowNode(
                    "after",
                    (_, _) => new ValueTask<GameWorkflowNodeResult>(
                        GameWorkflowNodeResult.Complete("{\"done\":true}", Assistant("after"))),
                    new[] { "wait" }),
            },
            checkpoints,
            maximumConcurrentNodes: 2);
        var sessions = new InMemoryGameSessionStore();
        var provider = new ScriptedProvider(_ => throw new InvalidOperationException("Workflow must not call the model."));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseSessionStore(sessions)
            .UseExtension("game.workflows", "1", api => api.RegisterWorkflow(graph))
            .Build();

        Assert.True((await runtime.RunAsync(
            WorkflowInput("one", "shared"),
            TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await runtime.RunAsync(
            WorkflowInput("two", "shared"),
            TestContext.Current.CancellationToken)).Succeeded);

        Assert.Equal(2, attempts);
        var session = await sessions.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        var output = session!.Messages
            .Where(message => message.Role == AgentRole.Assistant)
            .Select(message => Assert.IsType<TextContent>(Assert.Single(message.Content)).Text)
            .ToArray();
        Assert.Equal(new[] { "always", "waiting", "ready", "after" }, output);
    }

    [Fact]
    public async Task WorkflowGraphReplaysTheSameInputAfterSessionCommitFailure()
    {
        var executions = 0;
        var checkpoints = new InMemoryGameWorkflowCheckpointStore();
        var graph = new DurableGameWorkflowGraph(
            "evolve",
            new[]
            {
                new GameWorkflowNode("once", (_, _) =>
                {
                    Interlocked.Increment(ref executions);
                    return new ValueTask<GameWorkflowNodeResult>(
                        GameWorkflowNodeResult.Complete("{\"done\":true}", Assistant("durable output")));
                }),
            },
            checkpoints);
        var sessions = new FailOnceGameSessionStore();
        var provider = new ScriptedProvider(_ => throw new InvalidOperationException("Workflow must not call the model."));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseSessionStore(sessions)
            .UseExtension("game.workflows", "1", api => api.RegisterWorkflow(graph))
            .Build();
        var input = WorkflowInput("replay-input", "replay-instance");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.RunAsync(input, TestContext.Current.CancellationToken));
        var replayed = await runtime.RunAsync(input, TestContext.Current.CancellationToken);

        Assert.True(replayed.Succeeded);
        Assert.Equal(1, executions);
        var session = await sessions.LoadAsync(
            new GameSessionKey("session", "actor"),
            TestContext.Current.CancellationToken);
        Assert.Contains(session!.Messages, message =>
            message.Role == AgentRole.Assistant
            && Assert.IsType<TextContent>(Assert.Single(message.Content)).Text == "durable output");
    }

    [Fact]
    public void WorkflowGraphRejectsCyclesAndMissingDependencies()
    {
        static ValueTask<GameWorkflowNodeResult> Complete(GameWorkflowNodeContext _, CancellationToken __) =>
            new(GameWorkflowNodeResult.Complete("{}"));

        Assert.Throws<ArgumentException>(() => new DurableGameWorkflowGraph(
            "cycle",
            new[]
            {
                new GameWorkflowNode("a", Complete, new[] { "b" }),
                new GameWorkflowNode("b", Complete, new[] { "a" }),
            },
            new InMemoryGameWorkflowCheckpointStore()));
        Assert.Throws<ArgumentException>(() => new DurableGameWorkflowGraph(
            "missing",
            new[] { new GameWorkflowNode("a", Complete, new[] { "missing" }) },
            new InMemoryGameWorkflowCheckpointStore()));
    }

    [Fact]
    public async Task WorkflowGraphValidatesCumulativeOutputBeforeCheckpointing()
    {
        var checkpoints = new InMemoryGameWorkflowCheckpointStore();
        var graph = new DurableGameWorkflowGraph(
            "evolve",
            new[]
            {
                new GameWorkflowNode(
                    "oversized",
                    (_, _) => new ValueTask<GameWorkflowNodeResult>(GameWorkflowNodeResult.Complete(
                        "{}",
                        Assistant(new string('x', 65))))),
            },
            checkpoints);
        var provider = new ScriptedProvider(_ => throw new InvalidOperationException("Workflow must not call the model."));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .Configure(options => options.AgentLimits.MaxTextCharactersPerPart = 64)
            .UseExtension("game.workflows", "1", api => api.RegisterWorkflow(graph))
            .Build();

        await Assert.ThrowsAsync<AgentLimitException>(async () => await runtime.RunAsync(
            WorkflowInput("oversized-input", "oversized-instance"),
            TestContext.Current.CancellationToken));
        var instanceId = string.Join(":", new[]
        {
            "session",
            "actor",
            "evolve",
            "oversized-instance",
        }.Select(Uri.EscapeDataString));
        Assert.Null(await checkpoints.LoadAsync(instanceId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DelegatedAgentRunsWithAnIsolatedContextAndDurableResult()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? ToolCall(
                "delegate",
                "delegate_agent",
                "{\"delegationId\":\"research-1\",\"task\":{\"kind\":\"inspect_region\"},\"inheritContext\":false}")
            : TextResponse("delegated"));
        var executor = new ImmediateDelegateExecutor(new GameAgentDelegateOutcome(
            true,
            new[]
            {
                new AgentMessage(
                    AgentRole.Assistant,
                    new AgentContent[] { new TextContent("region inspected") },
                    DateTimeOffset.UtcNow,
                    model: "delegate-model",
                    stopReason: ModelStopReason.Stop),
            }));
        var store = new InMemoryGameAgentDelegationStore();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new AgentDelegationExtension(executor, store))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(executor.Requests);
        Assert.False(request.InheritContext);
        Assert.Equal(1, request.Depth);
        Assert.Contains("inspect_region", request.TaskJson);
        var record = await store.LoadAsync("session", "actor", "research-1", TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        Assert.Equal(GameAgentDelegationStatus.Completed, record.Status);
        Assert.Equal(3, record.Revision);
        Assert.Contains("region inspected", record.ResultJson);
    }

    [Fact]
    public async Task BackgroundDelegationCanBeCancelledFromANewGameInput()
    {
        var provider = new ScriptedProvider(call => call switch
        {
            1 => ToolCall(
                "delegate",
                "delegate_agent",
                "{\"delegationId\":\"background-1\",\"task\":{\"kind\":\"long_task\"},\"background\":true}"),
            2 => TextResponse("started"),
            3 => ToolCall("cancel", "cancel_delegate", "{\"delegationId\":\"background-1\"}"),
            _ => TextResponse("cancel requested"),
        });
        var executor = new ControllableDelegateExecutor();
        var store = new InMemoryGameAgentDelegationStore();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new AgentDelegationExtension(executor, store))
            .Build();

        var started = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);
        Assert.True(started.Succeeded);
        await WaitUntilAsync(() => executor.Handle is not null, TestContext.Current.CancellationToken);

        var cancelled = await runtime.RunAsync(
            new GameInput("session", "actor", "cancel", "{}", new GameMoment("world", 6), "cancel-input"),
            TestContext.Current.CancellationToken);

        Assert.True(cancelled.Succeeded);
        await WaitUntilAsync(
            () => store.LoadAsync("session", "actor", "background-1", CancellationToken.None).AsTask().GetAwaiter().GetResult()?.Status
                  == GameAgentDelegationStatus.Cancelled,
            TestContext.Current.CancellationToken);
        Assert.True(executor.Handle!.CancelCalled);
        var record = await store.LoadAsync("session", "actor", "background-1", TestContext.Current.CancellationToken);
        Assert.Equal(GameAgentDelegationStatus.Cancelled, record!.Status);
    }

    [Fact]
    public async Task RuntimeShutdownLeavesUncooperativeDelegateRecoverable()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? ToolCall(
                "delegate",
                "delegate_agent",
                "{\"delegationId\":\"stubborn\",\"task\":{},\"background\":true}")
            : TextResponse("started"));
        var executor = new UncooperativeDelegateExecutor();
        var store = new InMemoryGameAgentDelegationStore();
        var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new AgentDelegationExtension(
                executor,
                store,
                settlementTimeoutMilliseconds: 100,
                leaseDurationMilliseconds: 1_000))
            .Build();
        Assert.True((await runtime.RunAsync(Input(), TestContext.Current.CancellationToken)).Succeeded);
        await WaitUntilAsync(() => executor.Handle is not null, TestContext.Current.CancellationToken);

        await runtime.DisposeAsync();

        Assert.True(executor.Handle!.CancelCalled);
        Assert.True(executor.Handle.Disposed);
        var recoverable = await store.LoadAsync(
            "session",
            "actor",
            "stubborn",
            TestContext.Current.CancellationToken);
        Assert.Equal(GameAgentDelegationStatus.Running, recoverable!.Status);
        Assert.NotNull(recoverable.LeaseExpiresAt);
    }

    [Fact]
    public async Task DelegateExecutorFailureBecomesATerminalRecord()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? ToolCall(
                "delegate",
                "delegate_agent",
                "{\"delegationId\":\"failed-1\",\"task\":{\"kind\":\"fail\"}}")
            : TextResponse("handled"));
        var store = new InMemoryGameAgentDelegationStore();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new AgentDelegationExtension(new ThrowingDelegateExecutor(), store))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var record = await store.LoadAsync("session", "actor", "failed-1", TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        Assert.Equal(GameAgentDelegationStatus.Failed, record.Status);
        Assert.Contains("executor failed", record.Error);
    }

    [Fact]
    public async Task ExpiredDelegationLeaseResumesOnceWithPersistedAuthorityAndContext()
    {
        var input = Input();
        var request = new GameAgentDelegateRequest(
            "recover-1",
            input,
            "{\"kind\":\"resume\"}",
            1,
            12,
            inheritContext: true,
            new[] { AgentMessage.User("inherited") },
            GameExecutionScope.Restricted(new[] { GameExecutionCapabilities.PersistentPlanning }));
        var expired = new GameAgentDelegationRecord(
            "recover-1",
            input.SessionId,
            input.ActorId,
            1,
            GameAgentDelegationStatus.Running,
            request.TaskJson,
            request.Depth,
            input.Moment,
            request: request,
            leaseId: "dead-worker",
            leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            attempt: 1);
        var store = new InMemoryGameAgentDelegationStore();
        Assert.True((await store.SaveAsync(expired, 0, TestContext.Current.CancellationToken)).Saved);
        var executor = new ImmediateDelegateExecutor(new GameAgentDelegateOutcome(
            true,
            new[] { Assistant("recovered") }));
        var extension = new AgentDelegationExtension(executor, store, leaseDurationMilliseconds: 1_000);
        await using var runtime = new GameAgentBuilder(new ScriptedProvider(_ => TextResponse("unused")), "model")
            .UseExtension(extension)
            .Build();

        var resumes = await Task.WhenAll(
            extension.ResumePendingAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask(),
            extension.ResumePendingAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, resumes.Sum());
        await WaitUntilAsync(
            () => store.LoadAsync("session", "actor", "recover-1", CancellationToken.None)
                      .AsTask().GetAwaiter().GetResult()?.Status == GameAgentDelegationStatus.Completed,
            TestContext.Current.CancellationToken);
        var completed = await store.LoadAsync("session", "actor", "recover-1", TestContext.Current.CancellationToken);
        Assert.NotNull(completed);
        Assert.Equal(2, completed.Attempt);
        var replayed = Assert.Single(executor.Requests);
        Assert.True(replayed.InheritContext);
        Assert.True(replayed.ExecutionScope.Allows(GameExecutionCapabilities.PersistentPlanning));
        Assert.Single(replayed.ParentMessages);
    }

    [Fact]
    public async Task DelegationLineageIsOwnerScopedAndBounded()
    {
        var store = new InMemoryGameAgentDelegationStore();
        var moment = new GameMoment("world", 1);
        var root = new GameAgentDelegationRecord(
            "root",
            "session",
            "actor",
            1,
            GameAgentDelegationStatus.Pending,
            "{}",
            1,
            moment);
        var child = new GameAgentDelegationRecord(
            "child",
            "session",
            "actor",
            1,
            GameAgentDelegationStatus.Pending,
            "{}",
            2,
            new GameMoment("world", 2),
            parentDelegationId: "root",
            rootDelegationId: "root");
        var other = new GameAgentDelegationRecord(
            "other",
            "other-session",
            "actor",
            1,
            GameAgentDelegationStatus.Pending,
            "{}",
            1,
            moment);
        Assert.True((await store.SaveAsync(root, 0, TestContext.Current.CancellationToken)).Saved);
        Assert.True((await store.SaveAsync(child, 0, TestContext.Current.CancellationToken)).Saved);
        Assert.True((await store.SaveAsync(other, 0, TestContext.Current.CancellationToken)).Saved);

        var lineage = await store.ListAsync(
            "session",
            "actor",
            "root",
            maximum: 1,
            TestContext.Current.CancellationToken);

        var only = Assert.Single(lineage);
        Assert.Equal("root", only.Id);
    }

    [Fact]
    public async Task LocalDelegateChecksExecutionFenceBeforeEveryToolCall()
    {
        var executions = 0;
        var provider = new ScriptedProvider(call => call == 1
            ? ToolCall("write", "mutate_world", "{}")
            : TextResponse("unexpected"));
        var executor = new LocalGameAgentDelegateExecutor(
            provider,
            "model",
            tools: (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                new AgentTool(
                    new ToolDefinition("mutate_world", "Mutate a test world.", "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return new ValueTask<ToolResult>(new ToolResult(new AgentContent[] { new TextContent("written") }));
                    },
                    ToolRisk.NonIdempotentWrite),
            }));
        var input = Input();
        var request = new GameAgentDelegateRequest(
            "fenced",
            input,
            "{}",
            1,
            4,
            inheritContext: false,
            Array.Empty<AgentMessage>(),
            leaseValidator: _ => new ValueTask<bool>(false));

        using var handle = executor.Start(request, TestContext.Current.CancellationToken);
        var outcome = await handle.Completion;

        Assert.Equal(0, executions);
        Assert.Contains(
            outcome.Messages,
            message => message.Role == AgentRole.Tool
                       && message.IsError
                       && message.Content.OfType<TextContent>()
                           .Any(content => content.Text.Contains("lease", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DelegationIdsAreScopedAndSerializedResultsAreBounded()
    {
        var store = new InMemoryGameAgentDelegationStore();
        var first = new GameAgentDelegationRecord(
            "shared",
            "session-a",
            "actor",
            1,
            GameAgentDelegationStatus.Pending,
            "{}",
            1,
            new GameMoment("world", 1));
        var second = new GameAgentDelegationRecord(
            "shared",
            "session-b",
            "actor",
            1,
            GameAgentDelegationStatus.Pending,
            "{}",
            1,
            new GameMoment("world", 1));
        Assert.True((await store.SaveAsync(first, 0, TestContext.Current.CancellationToken)).Saved);
        Assert.True((await store.SaveAsync(second, 0, TestContext.Current.CancellationToken)).Saved);
        Assert.Same(first, await store.LoadAsync("session-a", "actor", "shared", TestContext.Current.CancellationToken));
        Assert.Same(second, await store.LoadAsync("session-b", "actor", "shared", TestContext.Current.CancellationToken));

        var provider = new ScriptedProvider(call => call == 1
            ? ToolCall("delegate", "delegate_agent", "{\"delegationId\":\"bounded\",\"task\":{}}")
            : TextResponse("done"));
        var executor = new ImmediateDelegateExecutor(new GameAgentDelegateOutcome(
            true,
            new[]
            {
                new AgentMessage(
                    AgentRole.Assistant,
                    new AgentContent[] { new TextContent(new string('x', 50_000)) },
                    DateTimeOffset.UtcNow,
                    model: "delegate-model",
                    stopReason: ModelStopReason.Stop),
            }));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new AgentDelegationExtension(executor, store, maximumResultCharacters: 1_024))
            .Build();

        Assert.True((await runtime.RunAsync(Input(), TestContext.Current.CancellationToken)).Succeeded);
        var bounded = await store.LoadAsync("session", "actor", "bounded", TestContext.Current.CancellationToken);
        Assert.NotNull(bounded);
        Assert.True(bounded.ResultJson!.Length <= 1_024);
    }

    [Fact]
    public async Task DelegationIdCannotBeReusedForDifferentTaskContent()
    {
        var store = new InMemoryGameAgentDelegationStore();
        var original = new GameAgentDelegationRecord(
            "stable-id",
            "session",
            "actor",
            1,
            GameAgentDelegationStatus.Pending,
            "{\"task\":1}",
            1,
            new GameMoment("world", 1));
        Assert.True((await store.SaveAsync(original, 0, TestContext.Current.CancellationToken)).Saved);
        var conflicting = new GameAgentDelegationRecord(
            "stable-id",
            "session",
            "actor",
            1,
            GameAgentDelegationStatus.Pending,
            "{\"task\":2}",
            1,
            new GameMoment("world", 1));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SaveAsync(conflicting, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ArtifactIdsAreScopedToActorSessions()
    {
        var store = new InMemoryGameAgentArtifactStore();
        var first = new GameAgentArtifact(
            "shared",
            "session-a",
            "actor",
            "text/plain",
            "first",
            new GameMoment("world", 1));
        var second = new GameAgentArtifact(
            "shared",
            "session-b",
            "actor",
            "text/plain",
            "second",
            new GameMoment("world", 1));

        await store.PutAsync(first, TestContext.Current.CancellationToken);
        await store.PutAsync(second, TestContext.Current.CancellationToken);

        Assert.Same(first, await store.GetAsync("session-a", "actor", "shared", TestContext.Current.CancellationToken));
        Assert.Same(second, await store.GetAsync("session-b", "actor", "shared", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LargeToolResultsSpillToArtifactsAndRemainReadableInTheSameRun()
    {
        var largeValue = new string('x', 2_048);
        var store = new InMemoryGameAgentArtifactStore();
        var provider = new ScriptedProvider((call, request) =>
        {
            if (call == 1)
            {
                return ToolCall("large", "large_result", "{}");
            }

            if (call == 2)
            {
                var message = request.Messages.Last(value => value.Role == AgentRole.Tool);
                var handle = Assert.IsType<JsonContent>(Assert.Single(message.Content));
                using var document = System.Text.Json.JsonDocument.Parse(handle.Json);
                var artifactId = document.RootElement.GetProperty("artifactId").GetString();
                Assert.NotNull(artifactId);
                Assert.DoesNotContain(largeValue, handle.Json, StringComparison.Ordinal);
                return ToolCall(
                    "read",
                    "read_agent_artifact",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        artifactId,
                        maximumCharacters = 4_096,
                    }));
            }

            return TextResponse("read");
        });
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "game.large-results",
                "1",
                api => api.RegisterTool(new AgentTool(
                    new ToolDefinition(
                        "large_result",
                        "Return a large result.",
                        "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                        new AgentContent[] { new TextContent(largeValue) })))))
            .UseExtension(new GameAgentArtifactExtension(
                store,
                spillToolResultsAboveCharacters: 1_024,
                maximumInlinePreviewCharacters: 64))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var readMessage = provider.Requests.ElementAt(2).Messages.Last(message => message.Role == AgentRole.Tool);
        var readJson = Assert.IsType<JsonContent>(Assert.Single(readMessage.Content)).Json;
        Assert.Contains(largeValue, readJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeToolResultSpillPreservesResourcesAndExecutionMetadata()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? ToolCall("large", "large_result", "{}")
            : TextResponse("handled"));
        var observed = new ConcurrentQueue<ToolResult>();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "game.large-results",
                "1",
                api => api.RegisterTool(new AgentTool(
                    new ToolDefinition(
                        "large_result",
                        "Return a large result.",
                        "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                        new AgentContent[]
                        {
                            new TextContent(new string('x', 2_048)),
                            new ResourceContent("game://scene/castle", "application/json", "castle"),
                        },
                        isError: true,
                        detailsJson: "{\"code\":\"partial\"}",
                        usage: new ModelUsage(7, 3),
                        outcomeUncertain: true)),
                    ToolRisk.IdempotentWrite)))
            .UseExtension(new GameAgentArtifactExtension(
                new InMemoryGameAgentArtifactStore(),
                spillToolResultsAboveCharacters: 1_024,
                maximumInlinePreviewCharacters: 64))
            .UseExtension(
                "game.observer",
                "1",
                api => api.On(GameAgentExtensionEvents.KernelEvent, (value, _, _) =>
                {
                    if (value.Value.Kind == AgentEventKind.ToolEnded && value.Value.ToolResult is not null)
                    {
                        observed.Enqueue(value.Value.ToolResult);
                    }

                    return ValueTask.CompletedTask;
                }))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var spilled = Assert.Single(observed);
        Assert.True(spilled.IsError);
        Assert.True(spilled.OutcomeUncertain);
        Assert.Equal("{\"code\":\"partial\"}", spilled.DetailsJson);
        Assert.Equal(10, spilled.Usage!.TotalTokens);
        var resource = Assert.Single(spilled.Content.OfType<ResourceContent>());
        Assert.Equal("game://scene/castle", resource.Uri);
        Assert.Single(spilled.Content.OfType<JsonContent>());
    }

    [Fact]
    public async Task ArtifactStoreFailureLeavesTheAuthoritativeToolResultUntouched()
    {
        var largeValue = new string('x', 2_048);
        var provider = new ScriptedProvider(call => call == 1
            ? ToolCall("large", "large_result", "{}")
            : TextResponse("handled"));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "game.large-results",
                "1",
                api => api.RegisterTool(new AgentTool(
                    new ToolDefinition(
                        "large_result",
                        "Return a large result.",
                        "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                        new AgentContent[] { new TextContent(largeValue) })))))
            .UseExtension(new GameAgentArtifactExtension(
                new FailingArtifactStore(),
                spillToolResultsAboveCharacters: 1_024,
                maximumInlinePreviewCharacters: 64))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var message = provider.Requests.ElementAt(1).Messages.Last(value => value.Role == AgentRole.Tool);
        Assert.Equal(largeValue, Assert.IsType<TextContent>(Assert.Single(message.Content)).Text);
    }

    [Fact]
    public async Task MemoryToolsPreserveGameTimeAndFloatingPointPayloads()
    {
        var provider = new ScriptedProvider(call => call switch
        {
            1 => ToolCall(
                "remember",
                "remember_game_memory",
                "{\"memoryId\":\"memory-1\",\"scope\":\"relationship\",\"kind\":\"relationship\",\"payload\":{\"affinity\":0.75},\"importance\":0.8}"),
            2 => ToolCall(
                "search",
                "search_game_memory",
                "{\"scopes\":[\"relationship\"],\"atOrBeforeTick\":5}"),
            _ => TextResponse("remembered"),
        });
        var store = new InMemoryGameMemoryStore();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new GameMemoryExtension(store))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var searchResult = provider.Requests.ElementAt(2).Messages.Last(message => message.Role == AgentRole.Tool);
        var json = Assert.IsType<JsonContent>(Assert.Single(searchResult.Content)).Json;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var memory = Assert.Single(document.RootElement.GetProperty("memories").EnumerateArray());
        Assert.Equal(0.75, memory.GetProperty("payload").GetProperty("affinity").GetDouble());
        Assert.Equal(5, memory.GetProperty("tick").GetInt64());
    }

    [Fact]
    public async Task MemoryToolVisibilityCanBeScopedByInputAndConfiguredPerTool()
    {
        var provider = new ScriptedProvider(_ => TextResponse("done"));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(
                "game.image-tools",
                "1",
                api => api.RegisterTool(new AgentTool(
                    new ToolDefinition(
                        "generate_image",
                        "Generate an image.",
                        "{\"type\":\"object\",\"additionalProperties\":false}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                        new AgentContent[] { new TextContent("generated") })))))
            .UseExtension(new GameMemoryExtension(
                new InMemoryGameMemoryStore(),
                rememberToolVisibility: (context, _) =>
                    new ValueTask<bool>(IsMemoryToolEnabled(context.Input, "remember_game_memory")),
                searchToolVisibility: (context, _) =>
                    new ValueTask<bool>(IsMemoryToolEnabled(context.Input, "search_game_memory"))))
            .Build();

        await runtime.RunAsync(
            new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1), "ordinary"),
            TestContext.Current.CancellationToken);
        await runtime.RunAsync(
            new GameInput("session", "actor", "image", "{}", new GameMoment("world", 2), "image-only"),
            TestContext.Current.CancellationToken);
        await runtime.RunAsync(
            new GameInput(
                "session",
                "actor",
                "chat",
                "{\"disabledTools\":[\"remember_game_memory\"]}",
                new GameMoment("world", 3),
                "remember-disabled"),
            TestContext.Current.CancellationToken);
        await runtime.RunAsync(
            new GameInput(
                "session",
                "actor",
                "chat",
                "{\"disabledTools\":[\"search_game_memory\"]}",
                new GameMoment("world", 4),
                "search-disabled"),
            TestContext.Current.CancellationToken);

        var requests = provider.Requests.ToArray();
        Assert.Equal(
            new[] { "generate_image", "remember_game_memory", "search_game_memory" },
            requests[0].Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal("generate_image", Assert.Single(requests[1].Tools).Name);
        Assert.Equal(
            new[] { "generate_image", "search_game_memory" },
            requests[2].Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "generate_image", "remember_game_memory" },
            requests[3].Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        static bool IsMemoryToolEnabled(GameInput input, string toolName)
        {
            if (string.Equals(input.Type, "image", StringComparison.Ordinal))
            {
                return false;
            }

            using var document = System.Text.Json.JsonDocument.Parse(input.PayloadJson);
            return !document.RootElement.TryGetProperty("disabledTools", out var disabled)
                   || !disabled.EnumerateArray().Any(value => string.Equals(value.GetString(), toolName, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task DefaultMemoryIdentityIsStableAcrossRunRetriesAndNamespacedByOwner()
    {
        var store = new InMemoryGameMemoryStore();

        var first = await RunRememberAsync("actor");
        var retry = await RunRememberAsync("actor");
        var otherActor = await RunRememberAsync("other");

        Assert.Equal(first, retry);
        Assert.NotEqual(first, otherActor);
        Assert.StartsWith("oga-memory-v1:", first, StringComparison.Ordinal);
        Assert.Single(await store.SearchAsync(
            new GameMemoryQuery("session", 10, ownerId: "actor"),
            TestContext.Current.CancellationToken));
        Assert.Single(await store.SearchAsync(
            new GameMemoryQuery("session", 10, ownerId: "other"),
            TestContext.Current.CancellationToken));

        async Task<string> RunRememberAsync(string actorId)
        {
            var provider = new ScriptedProvider(call => call == 1
                ? ToolCall(
                    "remember",
                    "remember_game_memory",
                    "{\"scope\":\"facts\",\"kind\":\"fact\",\"payload\":{\"value\":1.25}}")
                : TextResponse("remembered"));
            await using var runtime = new GameAgentBuilder(provider, "model")
                .UseExtension(new GameMemoryExtension(store))
                .Build();
            var result = await runtime.RunAsync(
                new GameInput(
                    "session",
                    actorId,
                    "request",
                    "{}",
                    new GameMoment("world", 5),
                    "stable-input"),
                TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
            var toolResult = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
            var json = Assert.IsType<JsonContent>(Assert.Single(toolResult.Content)).Json;
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.GetProperty("memoryId").GetString()!;
        }
    }

    [Fact]
    public async Task GeneratedExtensionOperationIdsRemainStableAcrossFreshRunAttempts()
    {
        var broker = new RecordingBroker();
        var firstInteraction = await RunInteractionAsync(broker);
        var retriedInteraction = await RunInteractionAsync(broker);
        Assert.Equal(firstInteraction, retriedInteraction);

        var executor = new ImmediateDelegateExecutor(new GameAgentDelegateOutcome(
            true,
            new[] { Assistant("delegated") }));
        var delegations = new InMemoryGameAgentDelegationStore();
        await RunDelegationAsync(executor, delegations);
        await RunDelegationAsync(executor, delegations);
        var delegated = Assert.Single(executor.Requests);
        Assert.StartsWith("oga-delegation-v1:", delegated.Id, StringComparison.Ordinal);

        var artifacts = new InMemoryGameAgentArtifactStore();
        var firstArtifact = await RunKnowledgeAsync(artifacts);
        var retriedArtifact = await RunKnowledgeAsync(artifacts);
        Assert.Equal(firstArtifact, retriedArtifact);

        var toolArtifacts = new InMemoryGameAgentArtifactStore();
        var firstToolArtifact = await RunToolArtifactAsync(toolArtifacts, "provider-call-a");
        var retriedToolArtifact = await RunToolArtifactAsync(toolArtifacts, "provider-call-b");
        Assert.Equal(firstToolArtifact, retriedToolArtifact);

        async Task<string> RunInteractionAsync(RecordingBroker targetBroker)
        {
            var provider = new ScriptedProvider(call => call == 1
                ? ToolCall(
                    "ask",
                    "ask_player",
                    "{\"questions\":[{\"id\":\"approach\",\"prompt\":\"Choose\",\"options\":[{\"id\":\"safe\",\"label\":\"Safe\",\"description\":\"Validate\"},{\"id\":\"fast\",\"label\":\"Fast\",\"description\":\"Skip\"}]}]}")
                : TextResponse("done"));
            await using var runtime = new GameAgentBuilder(provider, "model")
                .UseExtension(new StructuredInteractionExtension(targetBroker))
                .Build();
            Assert.True((await runtime.RunAsync(Input(), TestContext.Current.CancellationToken)).Succeeded);
            return targetBroker.Requests.Last().RequestId;
        }

        async Task RunDelegationAsync(
            ImmediateDelegateExecutor targetExecutor,
            InMemoryGameAgentDelegationStore targetStore)
        {
            var provider = new ScriptedProvider(call => call == 1
                ? ToolCall("delegate", "delegate_agent", "{\"task\":{\"kind\":\"inspect\"}}")
                : TextResponse("done"));
            await using var runtime = new GameAgentBuilder(provider, "model")
                .UseExtension(new AgentDelegationExtension(targetExecutor, targetStore))
                .Build();
            Assert.True((await runtime.RunAsync(Input(), TestContext.Current.CancellationToken)).Succeeded);
        }

        async Task<string> RunKnowledgeAsync(InMemoryGameAgentArtifactStore targetArtifacts)
        {
            var provider = new ScriptedProvider(call => call == 1
                ? ToolCall(
                    "knowledge",
                    "query_external_knowledge",
                    "{\"source\":\"local\",\"query\":{\"topic\":\"world\"},\"limit\":1}")
                : TextResponse("done"));
            await using var runtime = new GameAgentBuilder(provider, "model")
                .UseExtension(new ExternalKnowledgeExtension(
                    new[] { new LargeKnowledgeSource() },
                    maximumInlineResultCharacters: 1_024,
                    artifactStore: targetArtifacts))
                .Build();
            Assert.True((await runtime.RunAsync(Input(), TestContext.Current.CancellationToken)).Succeeded);
            var tool = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
            using var document = System.Text.Json.JsonDocument.Parse(
                Assert.IsType<JsonContent>(Assert.Single(tool.Content)).Json);
            return document.RootElement.GetProperty("artifactId").GetString()!;
        }

        async Task<string> RunToolArtifactAsync(
            InMemoryGameAgentArtifactStore targetArtifacts,
            string providerToolCallId)
        {
            var provider = new ScriptedProvider(call => call == 1
                ? ToolCall(providerToolCallId, "large_result", "{}")
                : TextResponse("done"));
            await using var runtime = new GameAgentBuilder(provider, "model")
                .UseExtension(
                    "game.large-results",
                    "1",
                    api => api.RegisterTool(new AgentTool(
                        new ToolDefinition(
                            "large_result",
                            "Return a large result.",
                            "{\"type\":\"object\",\"additionalProperties\":false}"),
                        (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                            new AgentContent[] { new TextContent(new string('x', 2_048)) })))))
                .UseExtension(new GameAgentArtifactExtension(
                    targetArtifacts,
                    spillToolResultsAboveCharacters: 1_024,
                    maximumInlinePreviewCharacters: 64))
                .Build();
            Assert.True((await runtime.RunAsync(Input(), TestContext.Current.CancellationToken)).Succeeded);
            var tool = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
            using var document = System.Text.Json.JsonDocument.Parse(
                Assert.IsType<JsonContent>(Assert.Single(tool.Content)).Json);
            return document.RootElement.GetProperty("artifactId").GetString()!;
        }
    }

    [Fact]
    public async Task AutomaticMemoryRecallDefaultsToCurrentActorAndCurrentGameMoment()
    {
        var store = new InMemoryGameMemoryStore();
        await store.AppendAsync(
            new GameMemory(
                "past",
                "session",
                "actor",
                "facts",
                GameMemoryKind.Fact,
                "{\"value\":\"known\"}",
                new GameMoment("world", 5)),
            TestContext.Current.CancellationToken);
        await store.AppendAsync(
            new GameMemory(
                "other-actor",
                "session",
                "other",
                "facts",
                GameMemoryKind.Fact,
                "{\"value\":\"private\"}",
                new GameMoment("world", 4)),
            TestContext.Current.CancellationToken);
        await store.AppendAsync(
            new GameMemory(
                "future",
                "session",
                "actor",
                "facts",
                GameMemoryKind.Fact,
                "{\"value\":\"spoiler\"}",
                new GameMoment("world", 10)),
            TestContext.Current.CancellationToken);
        var provider = new ScriptedProvider(_ => TextResponse("done"));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new GameMemoryExtension(
                store,
                (context, _) => new ValueTask<GameMemoryQuery?>(new GameMemoryQuery(
                    context.Input.SessionId,
                    8))))
            .Build();

        var result = await runtime.RunAsync(
            new GameInput("session", "actor", "request", "{}", new GameMoment("world", 7), "memory-input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var prompt = Assert.Single(provider.Requests).SystemPrompt;
        Assert.Contains("known", prompt);
        Assert.DoesNotContain("spoiler", prompt);
        Assert.DoesNotContain("private", prompt);
    }

    [Fact]
    public async Task MemoryRecallTraceExposesProviderAndBoundedStagesWithoutMemoryContent()
    {
        const string secretMemoryText = "private-memory-body-must-not-enter-traces";
        var store = new InMemoryGameMemoryStore();
        await store.AppendAsync(
            new GameMemory(
                "memory",
                "session",
                "actor",
                "facts",
                GameMemoryKind.Fact,
                "{\"secret\":\"" + secretMemoryText + "\"}",
                new GameMoment("world", 4),
                searchableText: secretMemoryText),
            TestContext.Current.CancellationToken);
        var sink = new InMemoryGameAgentTraceSink();
        var provider = new ScriptedProvider(_ => TextResponse("done"));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new GameAgentTracingExtension(sink))
            .UseExtension(new GameMemoryExtension(
                store,
                (context, _) => new ValueTask<GameMemoryQuery?>(new GameMemoryQuery(
                    context.Input.SessionId,
                    8,
                    ownerId: context.Input.ActorId,
                    atOrBefore: context.Input.Moment))))
            .Build();

        Assert.True((await runtime.RunAsync(Input(), TestContext.Current.CancellationToken)).Succeeded);

        var traces = sink.Snapshot();
        var memory = Assert.Single(traces, trace => trace.Kind == "memory.search.completed");
        Assert.Contains("AuthoritativeSnapshot", memory.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("LexicalSearch", memory.DetailsJson, StringComparison.Ordinal);
        var providerTrace = Assert.Single(
            traces,
            trace => trace.Kind == "context.provider.completed"
                     && trace.DetailsJson.Contains("memory-recall", StringComparison.Ordinal));
        Assert.Contains("opengameagent.memory", providerTrace.DetailsJson, StringComparison.Ordinal);
        Assert.All(traces, trace => Assert.DoesNotContain(secretMemoryText, trace.DetailsJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MemorySearchToolRejectsFutureGameTime()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? ToolCall("search", "search_game_memory", "{\"atOrBeforeTick\":6}")
            : TextResponse("done"));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new GameMemoryExtension(new InMemoryGameMemoryStore()))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var tool = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
        Assert.True(tool.IsError);
        Assert.Contains("future", Assert.IsType<TextContent>(Assert.Single(tool.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeKnowledgeResultsAreStoredOutsideModelContext()
    {
        var provider = new ScriptedProvider(call => call == 1
            ? ToolCall(
                "knowledge",
                "query_external_knowledge",
                "{\"source\":\"local\",\"query\":{\"topic\":\"world\"},\"limit\":1}")
            : TextResponse("stored"));
        var artifacts = new InMemoryGameAgentArtifactStore();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new GameAgentArtifactExtension(artifacts))
            .UseExtension(new ExternalKnowledgeExtension(
                new[] { new LargeKnowledgeSource() },
                maximumInlineResultCharacters: 1_024,
                artifactStore: artifacts))
            .Build();

        var result = await runtime.RunAsync(Input(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var toolMessage = provider.Requests.ElementAt(1).Messages.Last(message => message.Role == AgentRole.Tool);
        var json = Assert.IsType<JsonContent>(Assert.Single(toolMessage.Content)).Json;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var artifactId = document.RootElement.GetProperty("artifactId").GetString();
        Assert.NotNull(artifactId);
        var artifact = await artifacts.GetAsync("session", "actor", artifactId, TestContext.Current.CancellationToken);
        Assert.NotNull(artifact);
        Assert.Contains(new string('x', 512), artifact.Content);
        Assert.DoesNotContain(new string('x', 512), json);
    }

    [Fact]
    public async Task KnowledgeHttpSourceRejectsInjectedHeadersBeforeTransport()
    {
        var handler = new KnowledgeHandler(_ => throw new InvalidOperationException("transport must not run"));
        var source = new JsonHttpGameKnowledgeSource(
            "remote",
            new HttpClient(handler),
            new Uri("https://knowledge.test/query"),
            (_, _) => new ValueTask<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["X-Session"] = "value\r\ninjected" }));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.QueryAsync(
                new GameExternalKnowledgeRequest(Input(), "{}", 1),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task KnowledgeHttpSourceRejectsAmbiguousResponseObjects()
    {
        var handler = new KnowledgeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"items\":[],\"items\":[]}",
                Encoding.UTF8,
                "application/json"),
        });
        var source = new JsonHttpGameKnowledgeSource(
            "remote",
            new HttpClient(handler),
            new Uri("https://knowledge.test/query"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.QueryAsync(
                new GameExternalKnowledgeRequest(Input(), "{}", 1),
                TestContext.Current.CancellationToken));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TracingRecordsLifecycleWithoutInputPayloadByDefault()
    {
        var provider = new ScriptedProvider(_ => new ModelResponse(
            new AgentContent[] { new TextContent("done") },
            ModelStopReason.Stop,
            new ModelUsage(
                inputTokens: 20,
                outputTokens: 5,
                reasoningTokens: 2,
                cost: new ModelCost(input: 0.01, output: 0.02, isKnown: true)),
            provider: "test-provider",
            api: "test-api",
            responseModel: "test-response-model",
            responseId: "response-1"));
        var sink = new InMemoryGameAgentTraceSink();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new GameAgentTracingExtension(sink))
            .Build();

        var result = await runtime.RunAsync(
            new GameInput(
                "session",
                "actor",
                "secret_input",
                "{\"secret\":\"not-for-traces\"}",
                new GameMoment("world", 12),
                "trace-input"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var traces = sink.Snapshot();
        Assert.Contains(traces, trace => trace.Kind == "input.received");
        Assert.Contains(traces, trace => trace.Kind == "kernel.runstarted");
        Assert.Contains(traces, trace => trace.Kind == "run.completed");
        Assert.Contains(traces, trace => trace.Kind == "session.saved");
        var completed = traces.Single(trace => trace.Kind == "run.completed");
        Assert.Contains("\"reasoningTokens\":2", completed.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"responseId\":\"response-1\"", completed.DetailsJson, StringComparison.Ordinal);
        var saved = traces.Single(trace => trace.Kind == "session.saved");
        Assert.Contains("\"usageRecords\":1", saved.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"totalTokens\":25", saved.DetailsJson, StringComparison.Ordinal);
        Assert.All(traces, trace => Assert.DoesNotContain("not-for-traces", trace.DetailsJson));
        Assert.All(traces, trace => Assert.Equal(12, trace.Moment.Tick));
    }

    private static GameInput Input() =>
        new("session", "actor", "request", "{}", new GameMoment("world", 5), "input");

    private static GameInput WorkflowInput(string inputId, string instanceId) =>
        new(
            "session",
            "actor",
            "evolve",
            "{}",
            new GameMoment("world", 5),
            inputId,
            new Dictionary<string, string>
            {
                ["agent.route"] = "workflow:evolve",
                ["agent.workflow_instance"] = instanceId,
            });

    private static AgentMessage Assistant(string text) =>
        new(
            AgentRole.Assistant,
            new AgentContent[] { new TextContent(text) },
            DateTimeOffset.UnixEpoch,
            model: "workflow",
            stopReason: ModelStopReason.Stop);

    private static ModelResponse ToolCall(string id, string name, string arguments) =>
        new(new AgentContent[] { new ToolCallContent(id, name, arguments) }, ModelStopReason.ToolUse);

    private static ModelResponse TextResponse(string text) =>
        new(new AgentContent[] { new TextContent(text) }, ModelStopReason.Stop);

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate())
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException("The expected asynchronous state was not reached.");
    }

    private sealed class RecordingBroker : IGameInteractionBroker
    {
        public ConcurrentQueue<GameInteractionRequest> Requests { get; } = new();

        public ValueTask<GameInteractionResponse> PromptAsync(
            GameInteractionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            return new ValueTask<GameInteractionResponse>(new GameInteractionResponse(
                false,
                new[] { new GameInteractionAnswer("approach", new[] { "safe" }) }));
        }
    }

    private sealed class DenyDeletePolicy : IGameToolPolicy
    {
        public string Id => "deny-delete";

        public ValueTask<GameToolPolicyDecision> EvaluateAsync(
            GameToolPolicyContext context,
            CancellationToken cancellationToken) =>
            new(context.Call.Name == "delete_world"
                ? GameToolPolicyDecision.Deny("Deletion is disabled.")
                : GameToolPolicyDecision.NotApplicable());
    }

    private sealed class ThrowingPolicy : IGameToolPolicy
    {
        public string Id => "broken";

        public ValueTask<GameToolPolicyDecision> EvaluateAsync(
            GameToolPolicyContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("policy unavailable");
    }

    private sealed class ImmediateDelegateExecutor : IGameAgentDelegateExecutor
    {
        private readonly GameAgentDelegateOutcome _outcome;

        public ImmediateDelegateExecutor(GameAgentDelegateOutcome outcome)
        {
            _outcome = outcome;
        }

        public ConcurrentQueue<GameAgentDelegateRequest> Requests { get; } = new();

        public IGameAgentDelegateHandle Start(GameAgentDelegateRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            return new CompletedDelegateHandle(_outcome);
        }
    }

    private sealed class FailOnceGameSessionStore : IGameSessionStore
    {
        private readonly InMemoryGameSessionStore _inner = new();
        private int _failuresRemaining = 1;

        public ValueTask<GameSessionSnapshot?> LoadAsync(
            GameSessionKey key,
            CancellationToken cancellationToken) =>
            _inner.LoadAsync(key, cancellationToken);

        public ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _failuresRemaining, 0) != 0)
            {
                throw new InvalidOperationException("simulated session commit failure");
            }

            return _inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }
    }

    private sealed class CompletedDelegateHandle : IGameAgentDelegateHandle
    {
        public CompletedDelegateHandle(GameAgentDelegateOutcome outcome)
        {
            Completion = Task.FromResult(outcome);
        }

        public Task<GameAgentDelegateOutcome> Completion { get; }

        public bool TrySteer(AgentMessage message) => false;

        public bool TryCancel() => false;

        public void Dispose()
        {
        }
    }

    private sealed class ControllableDelegateExecutor : IGameAgentDelegateExecutor
    {
        public ControllableDelegateHandle? Handle { get; private set; }

        public IGameAgentDelegateHandle Start(GameAgentDelegateRequest request, CancellationToken cancellationToken)
        {
            Handle = new ControllableDelegateHandle(cancellationToken);
            return Handle;
        }
    }

    private sealed class UncooperativeDelegateExecutor : IGameAgentDelegateExecutor
    {
        public UncooperativeDelegateHandle? Handle { get; private set; }

        public IGameAgentDelegateHandle Start(GameAgentDelegateRequest request, CancellationToken cancellationToken)
        {
            Handle = new UncooperativeDelegateHandle();
            return Handle;
        }
    }

    private sealed class UncooperativeDelegateHandle : IGameAgentDelegateHandle
    {
        private readonly TaskCompletionSource<GameAgentDelegateOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public Task<GameAgentDelegateOutcome> Completion => _completion.Task;

        public bool CancelCalled { get; private set; }

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public bool TrySteer(AgentMessage message) => false;

        public bool TryCancel()
        {
            CancelCalled = true;
            return true;
        }

        public void Release() => _completion.TrySetResult(new GameAgentDelegateOutcome(
            false,
            Array.Empty<AgentMessage>(),
            "released after shutdown"));

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class ControllableDelegateHandle : IGameAgentDelegateHandle
    {
        private readonly TaskCompletionSource<GameAgentDelegateOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public ControllableDelegateHandle(CancellationToken cancellationToken)
        {
            _registration = cancellationToken.Register(Cancel);
        }

        public bool CancelCalled { get; private set; }

        public Task<GameAgentDelegateOutcome> Completion => _completion.Task;

        public bool TrySteer(AgentMessage message) => true;

        public bool TryCancel()
        {
            if (_completion.Task.IsCompleted)
            {
                return false;
            }

            CancelCalled = true;
            Cancel();
            return true;
        }

        public void Dispose() => _registration.Dispose();

        private void Cancel() => _completion.TrySetResult(new GameAgentDelegateOutcome(
            false,
            Array.Empty<AgentMessage>(),
            "cancelled",
            cancelled: true));
    }

    private sealed class ThrowingDelegateExecutor : IGameAgentDelegateExecutor
    {
        public IGameAgentDelegateHandle Start(GameAgentDelegateRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("executor failed");
    }

    private sealed class LargeKnowledgeSource : IGameExternalKnowledgeSource
    {
        public string Id => "local";

        public ValueTask<IReadOnlyList<GameExternalKnowledgeItem>> QueryAsync(
            GameExternalKnowledgeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<GameExternalKnowledgeItem>>(new[]
            {
                new GameExternalKnowledgeItem(
                    "item",
                    "Large local result",
                    System.Text.Json.JsonSerializer.Serialize(new { content = new string('x', 2_048) })),
            });
        }
    }

    private sealed class KnowledgeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        private int _calls;

        public KnowledgeHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_response(request));
        }
    }

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly Func<int, ModelRequest, ModelResponse> _response;
        private int _calls;

        public ScriptedProvider(Func<int, ModelResponse> response)
        {
            ArgumentNullException.ThrowIfNull(response);
            _response = (call, _) => response(call);
        }

        public ScriptedProvider(Func<int, ModelRequest, ModelResponse> response)
        {
            _response = response ?? throw new ArgumentNullException(nameof(response));
        }

        public ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            var call = Interlocked.Increment(ref _calls);
            yield return ModelStreamEvent.Terminal(_response(call, request));
            await Task.CompletedTask;
        }
    }

    private sealed class ConcurrentRunGate
    {
        private readonly int _participantCount;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public ConcurrentRunGate(int participantCount)
        {
            _participantCount = participantCount;
        }

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == _participantCount)
            {
                _release.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FirstCallBarrierProvider : IModelProvider
    {
        private readonly ConcurrentRunGate _gate;
        private readonly ModelResponse _firstResponse;
        private int _calls;

        public FirstCallBarrierProvider(ConcurrentRunGate gate, ModelResponse firstResponse)
        {
            _gate = gate;
            _firstResponse = firstResponse;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                await _gate.ArriveAsync(cancellationToken);
                yield return ModelStreamEvent.Terminal(_firstResponse);
                yield break;
            }

            yield return ModelStreamEvent.Terminal(TextResponse("done"));
        }
    }

    private sealed class FailingArtifactStore : IGameAgentArtifactStore
    {
        public ValueTask PutAsync(GameAgentArtifact artifact, CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("store unavailable"));

        public ValueTask<GameAgentArtifact?> GetAsync(
            string sessionId,
            string actorId,
            string artifactId,
            CancellationToken cancellationToken) =>
            new((GameAgentArtifact?)null);
    }
}

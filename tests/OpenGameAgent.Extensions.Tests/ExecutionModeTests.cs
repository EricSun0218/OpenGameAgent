using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class ExecutionModeTests
{
    [Fact]
    public void ExecutionScopesAreImmutableBoundedHostGrants()
    {
        Assert.True(GameExecutionScope.Unrestricted.Allows(GameExecutionCapabilities.PersistentPlanning));
        Assert.False(GameExecutionScope.ShortTaskOnly.Allows(GameExecutionCapabilities.PersistentPlanning));

        var restricted = GameExecutionScope.Restricted(new[]
        {
            "example.second",
            GameExecutionCapabilities.PersistentPlanning,
            "example.second",
        });
        Assert.Equal(
            new[] { "example.second", GameExecutionCapabilities.PersistentPlanning },
            restricted.GrantedCapabilities);
        Assert.True(restricted.Allows(GameExecutionCapabilities.PersistentPlanning));
        Assert.False(restricted.Allows("example.missing"));
        Assert.Throws<ArgumentException>(() => GameExecutionScope.Restricted(new[] { "bad\ncapability" }));
        Assert.Throws<ArgumentException>(() => GameExecutionScope.Restricted(
            Enumerable.Range(0, 65).Select(index => "capability." + index)));
    }

    [Fact]
    public async Task ShortTaskScopeKeepsAutomaticQuickClassificationWithoutPlanningExposure()
    {
        var answerProvider = new CapturingProvider(TextResponse("answer", new ModelUsage(1, 2)));
        var routingProvider = new CapturingProvider(
            JsonResponse("{\"route\":\"quick\",\"reason\":\"ordinary-question\"}", new ModelUsage(2, 3)));
        await using var runtime = new GameAgentBuilder(answerProvider, "model")
            .Configure(options =>
            {
                options.ExecutionScopeProvider = (_, _) =>
                    new ValueTask<GameExecutionScope>(GameExecutionScope.ShortTaskOnly);
                options.RoutePolicy = new AutomaticGameRoutePolicy(
                    classifier: new ModelGameRouteClassifier(routingProvider, "router").ClassifyAsync);
            })
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(new GoalLoopExtension())
            .Build();

        var result = await runtime.RunAsync(Input("auto"), TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.QuickResponse, result.Route.Route);
        Assert.Equal(5, result.RunUsage.TotalsByCause[GameSessionUsageCause.Routing].TotalTokens);
        Assert.Empty(Assert.Single(routingProvider.Requests).Tools);
        var request = Assert.Single(answerProvider.Requests);
        Assert.Empty(request.Tools);
        Assert.DoesNotContain("manage_task_plan", request.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("manage_goal", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortTaskScopeAllowsAutomaticAgentToolsButCannotUpgradeToPersistentPlanning()
    {
        var answerProvider = new CapturingProvider(TextResponse("done"));
        var routingProvider = new CapturingProvider(JsonResponse("{\"route\":\"agent\"}"));
        await using var runtime = new GameAgentBuilder(answerProvider, "model")
            .Configure(options =>
            {
                options.ExecutionScopeProvider = (_, _) =>
                    new ValueTask<GameExecutionScope>(GameExecutionScope.ShortTaskOnly);
                options.RoutePolicy = new AutomaticGameRoutePolicy(
                    classifier: new ModelGameRouteClassifier(routingProvider, "router").ClassifyAsync);
                options.ToolProvider = (_, _) =>
                    new ValueTask<IReadOnlyList<AgentTool>>(new[] { InspectTool() });
            })
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(new GoalLoopExtension())
            .Build();

        var result = await runtime.RunAsync(Input("auto"), TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.Agent, result.Route.Route);
        var request = Assert.Single(answerProvider.Requests);
        Assert.Contains(request.Tools, tool => tool.Name == "inspect_world");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "manage_task_plan");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "list_task_plans");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "manage_goal");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "list_goals");
        Assert.DoesNotContain("manage_task_plan", request.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("manage_goal", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortTaskPendingWorkStillSelectsAgentWithoutSpendingAClassifierCall()
    {
        var answerProvider = new CapturingProvider(TextResponse("continued"));
        var routingProvider = new CapturingProvider(JsonResponse("{\"route\":\"quick\"}"));
        await using var runtime = new GameAgentBuilder(answerProvider, "model")
            .Configure(options =>
            {
                options.ExecutionScopeProvider = (_, _) =>
                    new ValueTask<GameExecutionScope>(GameExecutionScope.ShortTaskOnly);
                options.RoutePolicy = new AutomaticGameRoutePolicy(
                    classifier: new ModelGameRouteClassifier(routingProvider, "router").ClassifyAsync);
                options.PendingWorkProvider = (_, _) => new ValueTask<bool>(true);
                options.ToolProvider = (_, _) =>
                    new ValueTask<IReadOnlyList<AgentTool>>(new[] { InspectTool() });
            })
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(new GoalLoopExtension())
            .Build();

        var result = await runtime.RunAsync(Input("auto"), TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.Agent, result.Route.Route);
        Assert.Equal("pending-work", result.Route.Reason);
        Assert.Empty(routingProvider.Requests);
        Assert.Contains(Assert.Single(answerProvider.Requests).Tools, tool => tool.Name == "inspect_world");
    }

    [Fact]
    public async Task ShortTaskScopeRejectsExplicitPlanBeforeAnyProviderRequest()
    {
        var provider = new CapturingProvider(TextResponse("unreachable"));
        await using var runtime = new GameAgentBuilder(provider, "model")
            .Configure(options => options.ExecutionScopeProvider = (_, _) =>
                new ValueTask<GameExecutionScope>(GameExecutionScope.ShortTaskOnly))
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(new GoalLoopExtension())
            .Build();

        var exception = await Assert.ThrowsAsync<GameExecutionCapabilityDeniedException>(
            () => runtime.RunAsync(Input("plan"), TestContext.Current.CancellationToken));

        Assert.Equal(GameExecutionCapabilities.PersistentPlanning, exception.Capability);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ExistingPersistentPlanDoesNotWakeARestrictedActor()
    {
        var store = new InMemoryGameSessionStore();
        var creator = new SequenceProvider(new[]
        {
            new ModelResponse(
                new AgentContent[]
                {
                    new ToolCallContent(
                        "create-plan",
                        "manage_task_plan",
                        "{\"action\":\"create\",\"planId\":\"durable\",\"objective\":\"test\",\"steps\":[\"one\",\"two\"]}"),
                },
                ModelStopReason.ToolUse),
            TextResponse("created"),
        });
        await using (var unrestricted = new GameAgentBuilder(creator, "model")
            .UseSessionStore(store)
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .Build())
        {
            var created = await unrestricted.RunAsync(
                Input("auto", "create-input"),
                TestContext.Current.CancellationToken);
            Assert.True(created.Succeeded);
        }

        Assert.Single((await TaskPlanExtension.ReadAsync(
            store,
            new GameSessionKey("session", "actor"),
            cancellationToken: TestContext.Current.CancellationToken)).Plans);

        var answerProvider = new CapturingProvider(TextResponse("ordinary answer"));
        var routingProvider = new CapturingProvider(JsonResponse("{\"route\":\"quick\"}"));
        await using var restricted = new GameAgentBuilder(answerProvider, "model")
            .UseSessionStore(store)
            .Configure(options =>
            {
                options.ExecutionScopeProvider = (_, _) =>
                    new ValueTask<GameExecutionScope>(GameExecutionScope.ShortTaskOnly);
                options.RoutePolicy = new AutomaticGameRoutePolicy(
                    classifier: new ModelGameRouteClassifier(routingProvider, "router").ClassifyAsync);
            })
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .Build();

        var result = await restricted.RunAsync(
            Input("auto", "restricted-input"),
            TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.QuickResponse, result.Route.Route);
        Assert.Single(routingProvider.Requests);
        Assert.DoesNotContain(
            Assert.Single(answerProvider.Requests).Tools,
            tool => tool.Name == "manage_task_plan");
    }

    [Fact]
    public async Task DirectModeUsesShortAgentLoopWithoutPersistentPlanningToolsOrGuidance()
    {
        var provider = new CapturingProvider();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(new GoalLoopExtension())
            .Build();

        var result = await runtime.RunAsync(Input("direct"), TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.Agent, result.Route.Route);
        var request = Assert.Single(provider.Requests);
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "manage_task_plan");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "list_task_plans");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "manage_goal");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "list_goals");
        Assert.DoesNotContain("manage_task_plan", request.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("manage_goal", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanModeKeepsPlanToolsAndAddsInputScopedDurableGuidance()
    {
        var provider = new CapturingProvider();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(new GoalLoopExtension())
            .Build();

        var result = await runtime.RunAsync(Input("plan"), TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.Agent, result.Route.Route);
        Assert.Equal("explicit-plan", result.Route.Reason);
        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.Tools, tool => tool.Name == "manage_task_plan");
        Assert.Contains(request.Tools, tool => tool.Name == "manage_goal");
        Assert.Contains("explicitly requested persistent-plan execution", request.SystemPrompt, StringComparison.Ordinal);
    }

    private static GameInput Input(string mode, string? inputId = null) => new(
        "session",
        "actor",
        "request",
        "{}",
        new GameMoment("world", 1),
        inputId: inputId ?? "input-" + mode,
        metadata: new Dictionary<string, string> { ["agent.route"] = mode });

    private static AgentTool InspectTool() => new(
        new ToolDefinition(
            "inspect_world",
            "Read bounded world state.",
            "{\"type\":\"object\",\"additionalProperties\":false}"),
        (_, _, _) => new ValueTask<ToolResult>(
            new ToolResult(new AgentContent[] { new TextContent("clear") })));

    private static ModelResponse TextResponse(string text, ModelUsage? usage = null) =>
        new(new AgentContent[] { new TextContent(text) }, ModelStopReason.Stop, usage);

    private static ModelResponse JsonResponse(string json, ModelUsage? usage = null) =>
        new(new AgentContent[] { new JsonContent(json) }, ModelStopReason.Stop, usage);

    private sealed class CapturingProvider : IModelProvider
    {
        private readonly ModelResponse _response;

        public CapturingProvider()
            : this(TextResponse("done", new ModelUsage(1, 1)))
        {
        }

        public CapturingProvider(ModelResponse response)
        {
            _response = response;
        }

        public List<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return ModelStreamEvent.Terminal(_response);
            await Task.CompletedTask;
        }
    }

    private sealed class SequenceProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelResponse> _responses;
        private int _index;

        public SequenceProvider(IReadOnlyList<ModelResponse> responses)
        {
            _responses = responses;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _index) - 1;
            var response = index < _responses.Count ? _responses[index] : TextResponse("done");
            yield return ModelStreamEvent.Terminal(response);
            await Task.CompletedTask;
        }
    }
}

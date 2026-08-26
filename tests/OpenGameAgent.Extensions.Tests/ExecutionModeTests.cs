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
        Assert.False(GameExecutionScope.NoOptionalCapabilities.Allows(GameExecutionCapabilities.PersistentPlanning));

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
    public async Task RestrictedScopeKeepsOrdinaryToolsAndHidesPersistentPlanning()
    {
        var provider = new CapturingProvider();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .Configure(options =>
            {
                options.ExecutionScopeProvider = (_, _) =>
                    new ValueTask<GameExecutionScope>(GameExecutionScope.NoOptionalCapabilities);
                options.ToolProvider = (_, _) =>
                    new ValueTask<IReadOnlyList<AgentTool>>(new[] { InspectTool() });
            })
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(new GoalLoopExtension())
            .Build();

        var result = await runtime.RunAsync(Input("restricted"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.Tools, tool => tool.Name == "inspect_world");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "manage_task_plan");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "list_task_plans");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "manage_goal");
        Assert.DoesNotContain(request.Tools, tool => tool.Name == "list_goals");
        Assert.DoesNotContain("manage_task_plan", request.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("manage_goal", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrestrictedScopeExposesPersistentPlanningAsOptionalTools()
    {
        var provider = new CapturingProvider();
        await using var runtime = new GameAgentBuilder(provider, "model")
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .UseExtension(new GoalLoopExtension())
            .Build();

        var result = await runtime.RunAsync(Input("unrestricted"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.Tools, tool => tool.Name == "manage_task_plan");
        Assert.Contains(request.Tools, tool => tool.Name == "manage_goal");
        Assert.Contains("manage_task_plan", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("manage_goal", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingPersistentPlanStaysStoredWhilePlanningCapabilityIsWithheld()
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
            Assert.True((await unrestricted.RunAsync(
                Input("create", "create-input"),
                TestContext.Current.CancellationToken)).Succeeded);
        }

        var before = await TaskPlanExtension.ReadAsync(
            store,
            new GameSessionKey("session", "actor"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(before.Plans);

        var provider = new CapturingProvider();
        await using var restricted = new GameAgentBuilder(provider, "model")
            .UseSessionStore(store)
            .Configure(options => options.ExecutionScopeProvider = (_, _) =>
                new ValueTask<GameExecutionScope>(GameExecutionScope.NoOptionalCapabilities))
            .UseExtension(new TaskPlanExtension((_, _) => new ValueTask<bool>(true)))
            .Build();

        Assert.True((await restricted.RunAsync(
            Input("ordinary", "restricted-input"),
            TestContext.Current.CancellationToken)).Succeeded);
        Assert.DoesNotContain(
            Assert.Single(provider.Requests).Tools,
            tool => tool.Name == "manage_task_plan");

        var after = await TaskPlanExtension.ReadAsync(
            store,
            new GameSessionKey("session", "actor"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(after.Plans);
        Assert.Equal(before.Plans[0].Revision, after.Plans[0].Revision);
    }

    private static GameInput Input(string type, string? inputId = null) => new(
        "session",
        "actor",
        type,
        "{}",
        new GameMoment("world", 1),
        inputId: inputId ?? "input-" + type);

    private static AgentTool InspectTool() => new(
        new ToolDefinition(
            "inspect_world",
            "Read bounded world state.",
            "{\"type\":\"object\",\"additionalProperties\":false}"),
        (_, _, _) => new ValueTask<ToolResult>(
            new ToolResult(new AgentContent[] { new TextContent("clear") })));

    private static ModelResponse TextResponse(string text) =>
        new(new AgentContent[] { new TextContent(text) }, ModelStopReason.Stop);

    private sealed class CapturingProvider : IModelProvider
    {
        public List<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return ModelStreamEvent.Terminal(TextResponse("done"));
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
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _index) - 1;
            var response = index < _responses.Count ? _responses[index] : TextResponse("done");
            yield return ModelStreamEvent.Terminal(response);
            await Task.CompletedTask;
        }
    }
}

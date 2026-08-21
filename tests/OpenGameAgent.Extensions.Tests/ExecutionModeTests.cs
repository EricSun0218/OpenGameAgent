using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Extensions.Tests;

public sealed class ExecutionModeTests
{
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

    private static GameInput Input(string mode) => new(
        "session",
        "actor",
        "request",
        "{}",
        new GameMoment("world", 1),
        inputId: "input-" + mode,
        metadata: new Dictionary<string, string> { ["agent.route"] = mode });

    private sealed class CapturingProvider : IModelProvider
    {
        public List<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop,
                new ModelUsage(1, 1)));
            await Task.CompletedTask;
        }
    }
}

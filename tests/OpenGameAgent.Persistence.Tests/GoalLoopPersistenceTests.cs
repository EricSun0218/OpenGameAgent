using System.Runtime.CompilerServices;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class GoalLoopPersistenceTests
{
    [Fact]
    public async Task TerminalRetentionAndActiveCapacitySurviveSessionStoreRestart()
    {
        using var directory = new TemporaryDirectory();
        var options = new GoalLoopOptions
        {
            MaximumActiveGoals = 2,
            MaximumRetainedTerminalGoals = 1,
        };

        await using (var runtime = new GameAgentBuilder(
                new ScriptedProvider(call => call switch
                {
                    1 => ToolCall("create-waiting", "{\"action\":\"create\",\"goalId\":\"waiting\",\"objective\":{}}"),
                    2 => ToolCall("wait", "{\"action\":\"wait\",\"goalId\":\"waiting\",\"expectedRevision\":1,\"eventTypes\":[\"future\"]}"),
                    3 => ToolCall("create-old", "{\"action\":\"create\",\"goalId\":\"old\",\"objective\":{}}"),
                    4 => ToolCall("complete-old", "{\"action\":\"complete\",\"goalId\":\"old\",\"expectedRevision\":1}"),
                    5 => ToolCall("create-recent", "{\"action\":\"create\",\"goalId\":\"recent\",\"objective\":{}}"),
                    6 => ToolCall("complete-recent", "{\"action\":\"complete\",\"goalId\":\"recent\",\"expectedRevision\":1}"),
                    _ => TextResponse("saved"),
                }),
                "model")
            .UseSessionStore(new FileGameSessionStore(directory.Path))
            .UseExtension(new GoalLoopExtension(options))
            .Build())
        {
            var result = await runtime.RunAsync(
                new GameInput("session", "actor", "request", "{}", new GameMoment("world", 1), "first"),
                TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        var restartedStore = new FileGameSessionStore(directory.Path);
        await using (var restartedRuntime = new GameAgentBuilder(
                new ScriptedProvider(call => call == 1
                    ? ToolCall("create-active", "{\"action\":\"create\",\"goalId\":\"active\",\"objective\":{}}")
                    : TextResponse("restored")),
                "model")
            .UseSessionStore(restartedStore)
            .UseExtension(new GoalLoopExtension(options))
            .Build())
        {
            var result = await restartedRuntime.RunAsync(
                new GameInput("session", "actor", "request", "{}", new GameMoment("world", 2), "second"),
                TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        var query = await GoalLoopExtension.ReadAsync(
            new FileGameSessionStore(directory.Path),
            new GameSessionKey("session", "actor"),
            includeTerminal: true,
            cancellationToken: TestContext.Current.CancellationToken);
        var goals = query.Goals.ToDictionary(goal => goal.Id, goal => goal.Status, StringComparer.Ordinal);
        Assert.Equal(new GameSessionKey("session", "actor"), query.Session);
        Assert.True(query.SessionRevision > 0);
        Assert.Equal(3, goals.Count);
        Assert.Equal(GameGoalStatus.Waiting, goals["waiting"]);
        Assert.Equal(GameGoalStatus.Completed, goals["recent"]);
        Assert.Equal(GameGoalStatus.Active, goals["active"]);
        Assert.DoesNotContain("old", goals);

        var activeOnly = await GoalLoopExtension.ReadAsync(
            new FileGameSessionStore(directory.Path),
            query.Session,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "active", "waiting" }, activeOnly.Goals.Select(goal => goal.Id).ToArray());
    }

    private static ModelResponse ToolCall(string id, string arguments) =>
        new(new AgentContent[] { new ToolCallContent(id, "manage_goal", arguments) }, ModelStopReason.ToolUse);

    private static ModelResponse TextResponse(string text) =>
        new(new AgentContent[] { new TextContent(text) }, ModelStopReason.Stop);

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly Func<int, ModelResponse> _response;
        private int _calls;

        public ScriptedProvider(Func<int, ModelResponse> response)
        {
            _response = response;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ModelStreamEvent.Terminal(_response(Interlocked.Increment(ref _calls)));
            await Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenGameAgent.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var root = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OpenGameAgent.Tests"));
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the test root.");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }
}

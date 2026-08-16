using System.Runtime.CompilerServices;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class TaskPlanPersistenceTests
{
    [Fact]
    public async Task ChecklistRevisionAndAdvanceGuardSurviveProcessRestart()
    {
        using var directory = new TemporaryDirectory();
        var evidenceCalls = 0;
        GameTaskPlanEvidenceValidator validator = (request, _) =>
        {
            Interlocked.Increment(ref evidenceCalls);
            return new ValueTask<bool>(request.Reference == "receipt-1");
        };

        await using (var runtime = new GameAgentBuilder(
                new ScriptedProvider(call => call == 1
                    ? ToolCall("create", "{\"action\":\"create\",\"planId\":\"persistent\",\"objective\":\"persist\",\"steps\":[\"one\",\"two\"]}")
                    : TextResponse("created")),
                "model")
            .UseSessionStore(new FileGameSessionStore(directory.Path))
            .UseExtension(new TaskPlanExtension(validator))
            .Build())
        {
            var result = await runtime.RunAsync(Input("create"), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        await using (var runtime = new GameAgentBuilder(
                new ScriptedProvider(call => call switch
                {
                    1 => ToolCall("advance", "{\"action\":\"advance\",\"planId\":\"persistent\",\"expectedRevision\":1,\"evidence\":{\"kind\":\"receipt\",\"reference\":\"receipt-1\"}}"),
                    2 => ToolCall("duplicate", "{\"action\":\"advance\",\"planId\":\"persistent\",\"expectedRevision\":2,\"evidence\":{\"kind\":\"receipt\",\"reference\":\"receipt-1\"}}"),
                    _ => TextResponse("advanced"),
                }),
                "model")
            .UseSessionStore(new FileGameSessionStore(directory.Path))
            .UseExtension(new TaskPlanExtension(validator))
            .Build())
        {
            var result = await runtime.RunAsync(Input("advance"), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        var query = await TaskPlanExtension.ReadAsync(
            new FileGameSessionStore(directory.Path),
            new GameSessionKey("session", "actor"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new GameSessionKey("session", "actor"), query.Session);
        Assert.True(query.SessionRevision > 0);
        var plan = Assert.Single(query.Plans);
        Assert.Equal(2, plan.Revision);
        Assert.Equal(
            new[] { GameTaskPlanStepStatus.Completed, GameTaskPlanStepStatus.InProgress },
            plan.Steps.Select(step => step.Status).ToArray());
        Assert.Equal(1, Volatile.Read(ref evidenceCalls));
    }

    [Fact]
    public async Task PausedChecklistAndInProgressStepSurviveProcessRestart()
    {
        using var directory = new TemporaryDirectory();
        var key = new GameSessionKey("session", "actor");
        GameTaskPlanEvidenceValidator validator = (_, _) => new ValueTask<bool>(true);
        await using (var runtime = new GameAgentBuilder(
                new ScriptedProvider(call => call == 1
                    ? ToolCall("create", "{\"action\":\"create\",\"planId\":\"paused\",\"objective\":\"persist pause\",\"steps\":[\"one\",\"two\"]}")
                    : TextResponse("created")),
                "model")
            .UseSessionStore(new FileGameSessionStore(directory.Path))
            .UseExtension(new TaskPlanExtension(validator))
            .Build())
        {
            var result = await runtime.RunAsync(Input("create"), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        await using (var runtime = new GameAgentBuilder(
                new ScriptedProvider(call => call == 1
                    ? ToolCall("pause", "{\"action\":\"pause\",\"planId\":\"paused\",\"expectedRevision\":1}")
                    : TextResponse("paused")),
                "model")
            .UseSessionStore(new FileGameSessionStore(directory.Path))
            .UseExtension(new TaskPlanExtension(validator))
            .Build())
        {
            var result = await runtime.RunAsync(Input("pause"), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        var restartedStore = new FileGameSessionStore(directory.Path);
        var paused = Assert.Single((await TaskPlanExtension.ReadAsync(
            restartedStore,
            key,
            cancellationToken: TestContext.Current.CancellationToken)).Plans);
        Assert.Equal(GameTaskPlanStatus.Paused, paused.Status);
        Assert.Equal(2, paused.Revision);
        var inProgress = Assert.Single(paused.Steps, step => step.Status == GameTaskPlanStepStatus.InProgress);

        await using (var runtime = new GameAgentBuilder(
                new ScriptedProvider(call => call == 1
                    ? ToolCall("resume", "{\"action\":\"resume\",\"planId\":\"paused\",\"expectedRevision\":2}")
                    : TextResponse("resumed")),
                "model")
            .UseSessionStore(new FileGameSessionStore(directory.Path))
            .UseExtension(new TaskPlanExtension(validator))
            .Build())
        {
            var result = await runtime.RunAsync(Input("resume"), TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded);
        }

        var resumed = Assert.Single((await TaskPlanExtension.ReadAsync(
            new FileGameSessionStore(directory.Path),
            key,
            cancellationToken: TestContext.Current.CancellationToken)).Plans);
        Assert.Equal(GameTaskPlanStatus.Active, resumed.Status);
        Assert.Equal(3, resumed.Revision);
        Assert.Equal(inProgress.Id, Assert.Single(
            resumed.Steps,
            step => step.Status == GameTaskPlanStepStatus.InProgress).Id);
    }

    private static GameInput Input(string inputId) =>
        new("session", "actor", "request", "{}", new GameMoment("world", 1), inputId);

    private static ModelResponse ToolCall(string id, string arguments) =>
        new(new AgentContent[] { new ToolCallContent(id, "manage_task_plan", arguments) }, ModelStopReason.ToolUse);

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

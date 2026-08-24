using System.Runtime.CompilerServices;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Persistence.Tests;

public sealed class BehaviorLearningPersistenceTests
{
    [Fact]
    public async Task ActiveVersionAndEvaluationSurviveFileStoreRestart()
    {
        using var directory = new TemporaryDirectory();
        var boundary = new GameBehaviorWorldBoundary("world", "generation-1", 12);
        var extension = new BehaviorLearningExtension(
            (_, _) => new ValueTask<GameBehaviorWorldBoundary>(boundary),
            (_, _) => new ValueTask<bool>(true),
            inRunPolicy: _ => true);
        var store = new FileGameSessionStore(directory.Path);
        await using (var runtime = new GameAgentBuilder(new ScriptedProvider(), "model")
            .UseSessionStore(store)
            .UseExtension(extension)
            .Build())
        {
            var result = await runtime.RunAsync(
                new GameInput("session", "actor", "request", "{}", new GameMoment("world", 1), "learn"),
                TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Error ?? result.AgentResult?.Error);
        }

        var key = new GameSessionKey("session", "actor");
        var restarted = new FileGameSessionStore(directory.Path);
        var loaded = await restarted.LoadAsync(key, TestContext.Current.CancellationToken);
        var activated = await extension.ActivateAsync(
            restarted,
            key,
            "safe-procedure",
            1,
            loaded!.Revision,
            boundary,
            TestContext.Current.CancellationToken);
        Assert.True(activated.Changed);

        restarted = new FileGameSessionStore(directory.Path);
        loaded = await restarted.LoadAsync(key, TestContext.Current.CancellationToken);
        var evaluated = await extension.RecordEvaluationAsync(
            restarted,
            key,
            "safe-procedure",
            1,
            loaded!.Revision,
            true,
            "offline-eval-1",
            TestContext.Current.CancellationToken);
        Assert.True(evaluated.Changed);

        var final = await BehaviorLearningExtension.ReadAsync(
            new FileGameSessionStore(directory.Path),
            key,
            cancellationToken: TestContext.Current.CancellationToken);
        var behavior = Assert.Single(final.Behaviors);
        Assert.Equal(GameLearnedBehaviorStatus.Active, behavior.Status);
        Assert.Equal(1, behavior.SuccessfulEvaluations);
        var evaluation = Assert.Single(behavior.RecentEvaluations);
        Assert.True(evaluation.Succeeded);
        Assert.Equal("offline-eval-1", evaluation.EvidenceReference);
        Assert.False(string.IsNullOrWhiteSpace(behavior.CreatedRunId));

        await using (var runtime = new GameAgentBuilder(new ScriptedProvider(), "model")
            .UseSessionStore(new FileGameSessionStore(directory.Path))
            .UseExtension(extension)
            .Build())
        {
            var result = await runtime.RunAsync(
                new GameInput("session", "actor", "request", "{}", new GameMoment("world", 2), "learn-after-restart"),
                TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Error ?? result.AgentResult?.Error);
        }

        var versioned = await BehaviorLearningExtension.ReadAsync(
            new FileGameSessionStore(directory.Path),
            key,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 2 }, versioned.Behaviors.Select(value => value.Version));
    }

    private sealed class ScriptedProvider : IModelProvider
    {
        private int _calls;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                yield return ModelStreamEvent.Terminal(new ModelResponse(
                    new AgentContent[]
                    {
                        new ToolCallContent(
                            "proposal",
                            "propose_behavior_learning",
                            "{\"behaviorId\":\"safe-procedure\",\"title\":\"Safe procedure\",\"instructions\":\"Use only verified observations.\",\"scope\":\"world_generation\",\"reflection\":{\"observation\":\"The action committed.\",\"strategy\":\"Use verified observations.\",\"outcome\":\"The task succeeded.\",\"applicability\":\"Use in the same world generation.\"},\"evidence\":[{\"kind\":\"receipt\",\"reference\":\"operation-1\"}]}"),
                    },
                    ModelStopReason.ToolUse,
                    responseId: "response-1"));
            }
            else
            {
                yield return ModelStreamEvent.Terminal(new ModelResponse(
                    new AgentContent[] { new TextContent("done") },
                    ModelStopReason.Stop,
                    responseId: "response-2"));
            }

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
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

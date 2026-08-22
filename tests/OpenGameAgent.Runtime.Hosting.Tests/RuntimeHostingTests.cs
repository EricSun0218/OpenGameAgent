using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using OpenGameAgent.Runtime.Protocol;
using Xunit;

namespace OpenGameAgent.Runtime.Hosting.Tests;

public sealed class RuntimeTests
{
    private static readonly GameSessionKey Key = new("session", "actor");

    [Fact]
    public void ProjectionUsesStableItemAndTurnIdsForEveryLifecycleEvent()
    {
        var projector = new GameRuntimeAgentEventProjector(Key, "input");
        var started = Project(projector, AgentEventKind.MessageStarted);
        var delta = Project(projector, AgentEventKind.MessageUpdated);
        var completed = Project(projector, AgentEventKind.MessageEnded);

        Assert.Equal(started.ItemId, delta.ItemId);
        Assert.Equal(started.ItemId, completed.ItemId);
        Assert.Equal(started.TurnId, completed.TurnId);
        Assert.Equal(GameRuntimeLifecycle.Started, started.Lifecycle);
        Assert.Equal(GameRuntimeLifecycle.Delta, delta.Lifecycle);
        Assert.Equal(GameRuntimeLifecycle.Completed, completed.Lifecycle);
    }

    [Fact]
    public void JournalRetainsBoundedHistoryAndReportsAnExplicitGap()
    {
        var journal = new InMemoryGameRuntimeEventJournal(
            maximumEventsPerSession: 16,
            clock: () => DateTimeOffset.UnixEpoch);
        for (var index = 0; index < 20; index++)
        {
            journal.Publish(Draft(GameRuntimeEventKind.Turn, GameRuntimeLifecycle.Delta, $"delta_{index}"));
        }

        var page = journal.Read(Key, afterSequence: 0, maximum: 16);

        Assert.True(page.Gap);
        Assert.Equal(5, page.FirstRetainedSequence);
        Assert.Equal(20, page.LastSequence);
        Assert.Equal(16, page.Events.Count);
        Assert.True(journal.IsKnownEvent(Key, page.Events[^1].EventId));
        Assert.False(journal.IsKnownEvent(new GameSessionKey("session", "other"), page.Events[^1].EventId));
    }

    [Fact]
    public void TerminalResultCompletesAnyOpenItemBeforePublishingTerminal()
    {
        var journal = new InMemoryGameRuntimeEventJournal(clock: () => DateTimeOffset.UnixEpoch);
        journal.Publish(new GameRuntimeEventDraft(
            Key,
            "input",
            GameRuntimeEventKind.Item,
            GameRuntimeLifecycle.Started,
            "tool_started",
            "{}",
            "run",
            1,
            GameRuntimeIds.Turn("run", 1),
            GameRuntimeIds.Item("run", 1, GameRuntimeItemKind.Tool, "call"),
            GameRuntimeItemKind.Tool));

        var published = journal.Publish(new GameRuntimeEventDraft(
            Key,
            "input",
            GameRuntimeEventKind.Result,
            GameRuntimeLifecycle.Completed,
            "result_failed",
            "{}",
            "run",
            terminal: true));

        Assert.Equal(2, published.Count);
        Assert.Equal("item_interrupted", published[0].Name);
        Assert.Equal(GameRuntimeLifecycle.Completed, published[0].Lifecycle);
        Assert.True(published[1].Terminal);
        Assert.Equal(published[0].Sequence + 1, published[1].Sequence);
    }

    [Fact]
    public async Task WaitForChangeIsBoundedAndDoesNotMissPublication()
    {
        var journal = new InMemoryGameRuntimeEventJournal();
        var wait = journal.WaitForChangeAsync(Key, 0, TestContext.Current.CancellationToken).AsTask();

        var value = Assert.Single(journal.Publish(Draft(
            GameRuntimeEventKind.Run,
            GameRuntimeLifecycle.Started,
            "run_started")));

        Assert.Equal(value.Sequence, await wait.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            value.Sequence,
            await journal.WaitForChangeAsync(Key, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InProcessRuntimeEmitsTheSameTypedProtocolAndReducesToResult()
    {
        await using var agentRuntime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            new SingleResponseProvider(),
            "test")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["agent"] = GameRouteDecision.Agent("test"),
            }),
        });
        var runtimeHost = new InProcessGameAgentRuntimeHost(agentRuntime);
        var input = new GameInput(
            "session",
            "actor",
            "agent",
            "{}",
            new GameMoment("world", 1),
            "input");
        var events = new List<GameRuntimeEventEnvelope>();

        var result = await runtimeHost.RunAsync(
            new GameRuntimeStartRequest("request", GameAgentWire.SerializeInput(input)),
            (value, _) =>
            {
                events.Add(value);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(events[^1].Terminal);
        Assert.All(events, value => Assert.Equal(GameRuntimeProtocol.Version, value.ProtocolVersion));
        var reducer = new GameRuntimeReducer();
        foreach (var value in events)
        {
            reducer.Apply(value);
        }

        Assert.Equal(GameRuntimeRunStatus.Completed, reducer.Snapshot.Status);
        Assert.False(reducer.Snapshot.RequiresTranscriptReconciliation);
        Assert.Equal(2, reducer.Snapshot.Items.Count(value => value.Kind == GameRuntimeItemKind.Message));
    }

    private static GameRuntimeEventDraft Project(
        GameRuntimeAgentEventProjector projector,
        AgentEventKind kind)
    {
        var message = new AgentMessage(
            AgentRole.Assistant,
            new AgentContent[] { new TextContent("answer") },
            DateTimeOffset.UnixEpoch);
        var agentEvent = new AgentEventFactory().Create(kind, message);
        return projector.Project(agentEvent, "{}");
    }

    private static GameRuntimeEventDraft Draft(
        GameRuntimeEventKind kind,
        GameRuntimeLifecycle lifecycle,
        string name) => new(Key, "input", kind, lifecycle, name, "{}", "run", 1, GameRuntimeIds.Turn("run", 1));

    private sealed class SingleResponseProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop));
        }
    }

    private sealed class AgentEventFactory
    {
        internal AgentEvent Create(AgentEventKind kind, AgentMessage message)
        {
            var constructor = typeof(AgentEvent).GetConstructors(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Single();
            return (AgentEvent)constructor.Invoke(new object?[]
            {
                kind,
                "run",
                1,
                message,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
            });
        }
    }
}

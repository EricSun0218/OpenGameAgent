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

    [Fact]
    public async Task HealthMonitorAggregatesRequiredAndOptionalComponentsDeterministically()
    {
        var monitor = new GameRuntimeHealthMonitor(
            new IGameRuntimeHealthProbe[]
            {
                new StaticGameRuntimeHealthProbe(
                    GameRuntimeComponentKind.Realtime,
                    "voice",
                    required: false,
                    GameRuntimeComponentState.Unavailable,
                    "not-configured"),
                new StaticGameRuntimeHealthProbe(
                    GameRuntimeComponentKind.Provider,
                    "primary-model",
                    required: true,
                    GameRuntimeComponentState.Ready),
            },
            clock: () => DateTimeOffset.UnixEpoch);

        var snapshot = await monitor.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GameRuntimeComponentState.Degraded, snapshot.State);
        Assert.Equal(DateTimeOffset.UnixEpoch, snapshot.CheckedAt);
        Assert.Equal(GameRuntimeComponentKind.Provider, snapshot.Components[0].Kind);
        Assert.Equal("not-configured", snapshot.Components[1].DiagnosticCode);
    }

    [Fact]
    public async Task HealthMonitorBoundsConcurrencyAndClassifiesTimeoutsWithoutExceptionText()
    {
        var active = 0;
        var maximumActive = 0;
        var probes = Enumerable.Range(0, 4).Select(index =>
            (IGameRuntimeHealthProbe)new DelegateGameRuntimeHealthProbe(
                GameRuntimeComponentKind.LocalEndpoint,
                "local-" + index,
                required: index == 0,
                async token =>
                {
                    var current = Interlocked.Increment(ref active);
                    InterlockedExtensions.Max(ref maximumActive, current);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), token);
                        return new GameRuntimeHealthProbeResult(GameRuntimeComponentState.Ready);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                })).ToArray();
        var monitor = new GameRuntimeHealthMonitor(probes, new GameRuntimeHealthMonitorOptions
        {
            MaximumConcurrency = 2,
            ProbeTimeout = TimeSpan.FromMilliseconds(30),
        });

        var snapshot = await monitor.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GameRuntimeComponentState.Unavailable, snapshot.State);
        Assert.True(maximumActive <= 2);
        Assert.All(snapshot.Components, component => Assert.Equal("probe-timeout", component.DiagnosticCode));
        Assert.All(snapshot.Components, component => Assert.Null(component.Detail));
    }

    [Fact]
    public async Task HealthMonitorPropagatesCallerCancellation()
    {
        var monitor = new GameRuntimeHealthMonitor(new[]
        {
            new DelegateGameRuntimeHealthProbe(
                GameRuntimeComponentKind.Media,
                "image",
                required: false,
                async token =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                    return new GameRuntimeHealthProbeResult(GameRuntimeComponentState.Ready);
                }),
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await monitor.ReadAsync(cancellation.Token));
    }

    [Fact]
    public async Task InProcessHostExposesConfiguredHealthMonitor()
    {
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new SingleResponseProvider(), "test"));
        var monitor = new GameRuntimeHealthMonitor(new[]
        {
            new StaticGameRuntimeHealthProbe(
                GameRuntimeComponentKind.Mcp,
                "world-tools",
                required: true,
                GameRuntimeComponentState.Available),
        });
        var host = new InProcessGameAgentRuntimeHost(runtime, health: monitor);

        var snapshot = await host.ReadHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GameRuntimeComponentState.Degraded, snapshot.State);
        Assert.Equal(GameRuntimeComponentKind.Mcp, Assert.Single(snapshot.Components).Kind);
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

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value)
            {
                var prior = Interlocked.CompareExchange(ref target, value, current);
                if (prior == current)
                {
                    return;
                }

                current = prior;
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class PersistentAgentGraphTests
{
    [Fact]
    public async Task Bulk_registration_preserves_ten_thousand_durable_identities()
    {
        var store = new CountingGraphStore();
        var graph = new PersistentAgentGraph(store);
        var nodes = Enumerable.Range(0, 10_000)
            .Select(index => Node($"npc-{index:D5}"))
            .ToArray();

        var registered = await graph.RegisterManyAsync(nodes);
        var state = await store.ReadAsync(default);

        Assert.Equal(10_000, registered.Count);
        Assert.Equal(10_000, state.Nodes.Count);
        Assert.Equal(1, store.SuccessfulWrites);
        Assert.Equal("npc-00000", state.Nodes[0].AgentId);
        Assert.Equal("npc-09999", state.Nodes[^1].AgentId);
    }

    [Fact]
    public async Task Coalescing_replaces_payload_but_keeps_original_message_identity()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await graph.RegisterAsync(Node("npc"));
        await graph.EnqueueAsync("npc", Message("first", "movement", 1, 1.25));
        await graph.EnqueueAsync("npc", Message("second", "movement", 2, 3.5));

        var node = await graph.TryGetAsync("npc");

        var message = Assert.Single(node!.Mailbox);
        Assert.Equal("first", message.MessageId);
        Assert.Equal(2, message.OrderingKey);
        Assert.Equal(3.5, message.Payload.GetProperty("speed").GetDouble());
        Assert.Equal(2, message.Revision);

        await graph.EnqueueAsync("npc", Message("second", "movement", 2, 3.5));
        await graph.EnqueueAsync("npc", Message("first", "movement", 1, 1.25));
        message = Assert.Single((await graph.TryGetAsync("npc"))!.Mailbox);
        Assert.Equal(2, message.Revision);
        Assert.Single(message.AcceptedMessages);

        var conflict = await Assert.ThrowsAsync<PersistentAgentGraphException>(async () =>
            await graph.EnqueueAsync("npc", Message("second", "movement", 3, 9.5)));
        Assert.Equal("agent_mailbox_message_conflict", conflict.ReasonCode);
    }

    [Fact]
    public async Task Delivery_ack_is_idempotent_after_an_unknown_commit_result()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await graph.RegisterAsync(Node("npc"));
        var enqueued = await graph.EnqueueAsync(
            "npc",
            Message("message", null, 1, 1.25));
        var revision = Assert.Single(enqueued.Mailbox).Revision;

        var delivered = await graph.MarkDeliveredAsync("npc", "message", revision);
        var replay = await graph.MarkDeliveredAsync("npc", "message", revision);

        Assert.Equal(
            AgentMailboxMessageStates.Delivered,
            Assert.Single(delivered.Mailbox).State);
        Assert.Equal(
            Assert.Single(delivered.Mailbox).Revision,
            Assert.Single(replay.Mailbox).Revision);
    }

    [Fact]
    public async Task Concurrent_lease_touches_preserve_the_highest_durable_ordering_key()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await graph.RegisterAsync(Node("npc"));
        var loader = new FakeRuntimeLoader(null);
        var residency = new AgentResidencyManager(graph, loader);
        var first = await residency.AcquireAsync("npc", 1);
        var second = await residency.AcquireAsync("npc", 2);
        first.Touch(100);

        await first.DisposeAsync();
        await second.DisposeAsync();

        Assert.Equal(100, (await graph.TryGetAsync("npc"))!.LastAccessOrderingKey);
        await residency.DisposeAsync();
    }

    [Fact]
    public async Task A_late_failed_load_releases_quarantined_capacity()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await graph.RegisterManyAsync(new[] { Node("a"), Node("b") });
        var loader = new LateFailingRuntimeLoader();
        var residency = new AgentResidencyManager(
            graph,
            loader,
            new AgentResidencyOptions
            {
                MaxResidentInstances = 1,
                LoadTimeout = TimeSpan.FromMilliseconds(50)
            });

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await residency.AcquireAsync("a", 1));
        Assert.Equal(1, residency.ResidentCount);
        loader.FailA.TrySetException(new InvalidOperationException("late load failure"));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (residency.ResidentCount != 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, residency.ResidentCount);
        await (await residency.AcquireAsync("b", 2)).DisposeAsync();
        await residency.DisposeAsync();
    }

    [Fact]
    public async Task Agent_owning_unsettled_side_effect_cannot_be_evicted()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await graph.RegisterAsync(Node("npc"));
        await graph.TransitionAsync(
            "npc",
            PersistentAgentStates.Running,
            1,
            ownsUnsettledSideEffect: true);
        await graph.TransitionAsync(
            "npc",
            PersistentAgentStates.Waiting,
            2,
            ownsUnsettledSideEffect: true);

        var exception = await Assert.ThrowsAsync<PersistentAgentGraphException>(
            async () => await graph.TransitionAsync(
                "npc",
                PersistentAgentStates.Evicted,
                3,
                ownsUnsettledSideEffect: false));

        Assert.Equal("persistent_agent_side_effect_owned", exception.ReasonCode);
    }

    [Fact]
    public async Task Residency_evicts_deterministic_idle_victim_and_keeps_mailbox()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await graph.RegisterManyAsync(new[] { Node("a"), Node("b"), Node("c") });
        await graph.EnqueueAsync("a", Message("mail-a", null, 1, 2.5));
        var loader = new FakeRuntimeLoader(sideEffectOwner: "b");
        var residency = new AgentResidencyManager(
            graph,
            loader,
            new AgentResidencyOptions
            {
                MaxResidentInstances = 2,
                MaxConcurrentExecutions = 1,
                MaxConcurrentModelCalls = 1
            });

        await (await residency.AcquireAsync("a", 1)).DisposeAsync();
        await (await residency.AcquireAsync("b", 2)).DisposeAsync();
        await (await residency.AcquireAsync("c", 3)).DisposeAsync();

        Assert.Equal(PersistentAgentStates.Evicted, (await graph.TryGetAsync("a"))!.State);
        Assert.NotEqual(PersistentAgentStates.Evicted, (await graph.TryGetAsync("b"))!.State);
        Assert.Single((await graph.TryGetAsync("a"))!.Mailbox);
        Assert.Contains("a", loader.DisposedAgentIds);

        await (await residency.AcquireAsync("a", 4)).DisposeAsync();
        Assert.Single((await graph.TryGetAsync("a"))!.Mailbox);
        Assert.Equal(2, residency.ResidentCount);
        loader.SettleAllSideEffects();
        await residency.DisposeAsync();
    }

    [Fact]
    public async Task Residency_refuses_capacity_when_all_idle_instances_own_side_effects()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await graph.RegisterManyAsync(new[] { Node("a"), Node("b") });
        var loader = new FakeRuntimeLoader(sideEffectOwner: "a");
        var residency = new AgentResidencyManager(
            graph,
            loader,
            new AgentResidencyOptions { MaxResidentInstances = 1 });
        await (await residency.AcquireAsync("a", 1)).DisposeAsync();

        var exception = await Assert.ThrowsAsync<PersistentAgentGraphException>(
            async () => await residency.AcquireAsync("b", 2));

        Assert.Equal("agent_residency_capacity", exception.ReasonCode);
        Assert.Equal(1, residency.ResidentCount);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await residency.DisposeAsync());
        loader.SettleAllSideEffects();
        await residency.DisposeAsync();
    }

    [Fact]
    public async Task Execution_and_model_capacity_are_independent_and_cancellable()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await using var residency = new AgentResidencyManager(
            graph,
            new FakeRuntimeLoader(null),
            new AgentResidencyOptions
            {
                MaxResidentInstances = 1,
                MaxConcurrentExecutions = 1,
                MaxConcurrentModelCalls = 1
            });
        using var execution = await residency.AcquireExecutionAsync();
        using var model = await residency.AcquireModelCallAsync();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await residency.AcquireExecutionAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await residency.AcquireModelCallAsync(cancelled.Token));
    }

    [Fact]
    public async Task Agent_cannot_reload_while_its_previous_runtime_is_unloading()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await graph.RegisterManyAsync(new[] { Node("a"), Node("b") });
        var loader = new BlockingDisposeRuntimeLoader();
        var residency = new AgentResidencyManager(
            graph,
            loader,
            new AgentResidencyOptions
            {
                MaxResidentInstances = 1,
                UnloadTimeout = TimeSpan.FromSeconds(5)
            });
        await (await residency.AcquireAsync("a", 1)).DisposeAsync();

        var acquireB = residency.AcquireAsync("b", 2).AsTask();
        await loader.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await residency.AcquireAsync("a", 3, cancelled.Token));

        Assert.Equal(1, loader.LoadCounts["a"]);
        loader.AllowDispose.TrySetResult(true);
        await (await acquireB).DisposeAsync();
        await residency.DisposeAsync();
    }

    [Fact]
    public async Task Slow_successful_unload_stays_quarantined_then_releases_capacity()
    {
        var graph = new PersistentAgentGraph(new InMemoryPersistentAgentGraphStore());
        await graph.RegisterManyAsync(new[] { Node("a"), Node("b") });
        var loader = new BlockingDisposeRuntimeLoader();
        var residency = new AgentResidencyManager(
            graph,
            loader,
            new AgentResidencyOptions
            {
                MaxResidentInstances = 1,
                UnloadTimeout = TimeSpan.FromMilliseconds(50)
            });
        await (await residency.AcquireAsync("a", 1)).DisposeAsync();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await residency.AcquireAsync("b", 2));
        Assert.Equal(1, residency.ResidentCount);
        Assert.Equal(1, loader.LoadCounts["a"]);

        loader.AllowDispose.TrySetResult(true);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (residency.ResidentCount != 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, residency.ResidentCount);
        await (await residency.AcquireAsync("b", 3)).DisposeAsync();
        await residency.DisposeAsync();
    }

    private static PersistentAgentNode Node(string id) => new()
    {
        AgentId = id,
        WorldId = "world",
        HistoryId = $"history-{id}",
        ContextInheritancePolicy = AgentContextInheritancePolicies.Selected
    };

    private static AgentMailboxMessage Message(
        string id,
        string? coalesceKey,
        long orderingKey,
        double speed)
    {
        using var document = JsonDocument.Parse($"{{\"speed\":{speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        return new AgentMailboxMessage
        {
            MessageId = id,
            Kind = "move",
            CoalesceKey = coalesceKey,
            OrderingKey = orderingKey,
            Payload = document.RootElement.Clone()
        };
    }

    private sealed class CountingGraphStore : IPersistentAgentGraphStore
    {
        private readonly InMemoryPersistentAgentGraphStore _inner = new();

        public int SuccessfulWrites { get; private set; }

        public ValueTask<PersistentAgentGraphState> ReadAsync(CancellationToken cancellationToken) =>
            _inner.ReadAsync(cancellationToken);

        public async ValueTask<bool> TryPutAsync(
            PersistentAgentGraphState state,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var written = await _inner.TryPutAsync(
                state,
                expectedRevision,
                cancellationToken);
            if (written)
            {
                SuccessfulWrites++;
            }

            return written;
        }
    }

    private sealed class FakeRuntimeLoader : IResidentAgentRuntimeLoader
    {
        private readonly string? _sideEffectOwner;

        public FakeRuntimeLoader(string? sideEffectOwner)
        {
            _sideEffectOwner = sideEffectOwner;
        }

        public ConcurrentBag<string> DisposedAgentIds { get; } = new();

        public ConcurrentBag<FakeRuntime> Runtimes { get; } = new();

        public void SettleAllSideEffects()
        {
            foreach (var runtime in Runtimes)
            {
                runtime.OwnsUnsettledSideEffect = false;
            }
        }

        public ValueTask<IResidentAgentRuntime> LoadAsync(
            PersistentAgentNode node,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = new FakeRuntime(
                node.AgentId,
                node.AgentId == _sideEffectOwner,
                DisposedAgentIds);
            Runtimes.Add(runtime);
            return new ValueTask<IResidentAgentRuntime>(runtime);
        }
    }

    private sealed class FakeRuntime : IResidentAgentRuntime
    {
        private readonly ConcurrentBag<string> _disposed;

        public FakeRuntime(
            string agentId,
            bool ownsUnsettledSideEffect,
            ConcurrentBag<string> disposed)
        {
            AgentId = agentId;
            OwnsUnsettledSideEffect = ownsUnsettledSideEffect;
            _disposed = disposed;
        }

        public string AgentId { get; }

        public bool OwnsUnsettledSideEffect { get; set; }

        public ValueTask DisposeAsync()
        {
            _disposed.Add(AgentId);
            return default;
        }
    }

    private sealed class BlockingDisposeRuntimeLoader : IResidentAgentRuntimeLoader
    {
        public ConcurrentDictionary<string, int> LoadCounts { get; } =
            new(StringComparer.Ordinal);

        public TaskCompletionSource<bool> DisposeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AllowDispose { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IResidentAgentRuntime> LoadAsync(
            PersistentAgentNode node,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCounts.AddOrUpdate(node.AgentId, 1, (_, count) => count + 1);
            return new ValueTask<IResidentAgentRuntime>(
                new BlockingDisposeRuntime(node.AgentId, DisposeStarted, AllowDispose));
        }
    }

    private sealed class LateFailingRuntimeLoader : IResidentAgentRuntimeLoader
    {
        private readonly ConcurrentBag<string> _disposed = new();

        public TaskCompletionSource<IResidentAgentRuntime> FailA { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IResidentAgentRuntime> LoadAsync(
            PersistentAgentNode node,
            CancellationToken cancellationToken)
        {
            if (node.AgentId == "a")
            {
                return new ValueTask<IResidentAgentRuntime>(FailA.Task);
            }

            return new ValueTask<IResidentAgentRuntime>(
                new FakeRuntime(node.AgentId, false, _disposed));
        }
    }

    private sealed class BlockingDisposeRuntime : IResidentAgentRuntime
    {
        private readonly TaskCompletionSource<bool> _disposeStarted;
        private readonly TaskCompletionSource<bool> _allowDispose;

        public BlockingDisposeRuntime(
            string agentId,
            TaskCompletionSource<bool> disposeStarted,
            TaskCompletionSource<bool> allowDispose)
        {
            AgentId = agentId;
            _disposeStarted = disposeStarted;
            _allowDispose = allowDispose;
        }

        public string AgentId { get; }

        public bool OwnsUnsettledSideEffect => false;

        public async ValueTask DisposeAsync()
        {
            _disposeStarted.TrySetResult(true);
            await _allowDispose.Task.ConfigureAwait(false);
        }
    }
}

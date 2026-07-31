using System.Collections;
using GameAgent.Core;
using GameAgent.Runtime;

namespace GameAgent.Tests;

public sealed partial class WorldAgentEvolutionTests
{
    [Fact]
    public async Task ResumeRejectsAggregateContinuationBytesBeforeStoreAccess()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var store = new FailOnAccessEvolutionStore();
        var runner = CapacityRunner(
            fixture,
            runtime,
            store,
            new WorldAgentEvolutionRunnerOptions(
                maxParticipants: 8,
                maxConcurrentActors: 2,
                maxBatchSnapshotUtf8Bytes: 20_000));
        var continuations = fixture.Command.Participants.ToDictionary(
            participant => participant.Job.RunId,
            participant => new DurableRunContinuation
            {
                Context = new[]
                {
                    new ContextCandidate(
                        "large-" + participant.Job.RunId,
                        "test",
                        Json(
                            "{\"value\":\""
                            + new string('x', 10_000)
                            + "\"}"))
                }
            },
            StringComparer.Ordinal);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => runner.ResumeAsync(fixture.Command, continuations).AsTask());

        Assert.Equal(
            "multi_actor_batch_snapshot_bytes_exceeded",
            error.LimitCode);
        Assert.Equal(0, store.AccessCount);
        Assert.Equal(0, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task ResumeRejectsAggregateContinuationNodesBeforeStoreAccess()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var store = new FailOnAccessEvolutionStore();
        var runner = CapacityRunner(
            fixture,
            runtime,
            store,
            new WorldAgentEvolutionRunnerOptions(
                maxParticipants: 8,
                maxConcurrentActors: 2,
                maxBatchSnapshotJsonNodes: 80));
        var values = string.Join(",", Enumerable.Repeat("0", 30));
        var continuations = fixture.Command.Participants.ToDictionary(
            participant => participant.Job.RunId,
            participant => new DurableRunContinuation
            {
                Context = new[]
                {
                    new ContextCandidate(
                        "node-heavy-" + participant.Job.RunId,
                        "test",
                        Json("{\"values\":[" + values + "]}"))
                }
            },
            StringComparer.Ordinal);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => runner.ResumeAsync(fixture.Command, continuations).AsTask());

        Assert.Equal(
            "multi_actor_batch_snapshot_nodes_exceeded",
            error.LimitCode);
        Assert.Equal(0, store.AccessCount);
        Assert.Equal(0, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task ResumeRejectsLyingContinuationEnumerationBeforeStoreAccess()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var store = new FailOnAccessEvolutionStore();
        var runner = CapacityRunner(
            fixture,
            runtime,
            store,
            new WorldAgentEvolutionRunnerOptions(
                maxParticipants: 8,
                maxConcurrentActors: 2));
        var continuation = new DurableRunContinuation();
        var continuations = new MismatchedReadOnlyDictionary<
            string,
            DurableRunContinuation>(
            reportedCount: 0,
            new KeyValuePair<string, DurableRunContinuation>(
                "run-npc-a",
                continuation),
            new KeyValuePair<string, DurableRunContinuation>(
                "run-npc-b",
                continuation),
            new KeyValuePair<string, DurableRunContinuation>(
                "run-extra",
                continuation));

        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.ResumeAsync(fixture.Command, continuations).AsTask());

        Assert.Equal(0, store.AccessCount);
        Assert.Equal(0, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Theory]
    [InlineData(4_095)]
    [InlineData(536_870_913)]
    public void EvolutionContinuationByteBudgetIsBounded(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorldAgentEvolutionRunnerOptions(
                maxBatchSnapshotUtf8Bytes: value));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(16_777_217)]
    public void EvolutionContinuationNodeBudgetIsBounded(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorldAgentEvolutionRunnerOptions(
                maxBatchSnapshotJsonNodes: value));
    }

    private static WorldAgentEvolutionRunner CapacityRunner(
        Fixture fixture,
        IDurableAgentRuntime runtime,
        IWorldAgentEvolutionStore evolutionStore,
        WorldAgentEvolutionRunnerOptions options)
    {
        return new WorldAgentEvolutionRunner(
            runtime,
            new FixedInputFactory(),
            fixture.Store,
            evolutionStore,
            new WinnerReducer(fixture),
            new FixedWorldAgentRuntimePolicySnapshotSource(
                fixture.Command.RuntimeGeneration),
            options);
    }

    private sealed class FailOnAccessEvolutionStore
        : IWorldAgentEvolutionStore
    {
        private int _accessCount;

        public int AccessCount => Volatile.Read(ref _accessCount);

        public ValueTask<WorldAgentEvolutionCheckpoint?> ReadAsync(
            string commandId,
            CancellationToken cancellationToken = default)
        {
            _ = commandId;
            _ = cancellationToken;
            Interlocked.Increment(ref _accessCount);
            throw new InvalidOperationException(
                "Continuation preflight must run before store access.");
        }

        public ValueTask<WorldAgentEvolutionStoreWriteResult>
            CompareExchangeAsync(
                WorldAgentEvolutionCheckpoint checkpoint,
                long expectedRevision,
                CancellationToken cancellationToken = default)
        {
            _ = checkpoint;
            _ = expectedRevision;
            _ = cancellationToken;
            Interlocked.Increment(ref _accessCount);
            throw new InvalidOperationException(
                "Continuation preflight must run before store access.");
        }
    }

    private sealed class MismatchedReadOnlyDictionary<TKey, TValue>
        : IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly KeyValuePair<TKey, TValue>[] _items;
        private readonly int _reportedCount;

        public MismatchedReadOnlyDictionary(
            int reportedCount,
            params KeyValuePair<TKey, TValue>[] items)
        {
            _reportedCount = reportedCount;
            _items = items;
        }

        public int Count => _reportedCount;

        public IEnumerable<TKey> Keys =>
            _items.Select(item => item.Key);

        public IEnumerable<TValue> Values =>
            _items.Select(item => item.Value);

        public TValue this[TKey key] =>
            _items.Single(
                item => EqualityComparer<TKey>.Default.Equals(
                    item.Key,
                    key)).Value;

        public bool ContainsKey(TKey key)
        {
            return _items.Any(
                item => EqualityComparer<TKey>.Default.Equals(
                    item.Key,
                    key));
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            foreach (var item in _items)
            {
                if (EqualityComparer<TKey>.Default.Equals(
                        item.Key,
                        key))
                {
                    value = item.Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return ((IEnumerable<KeyValuePair<TKey, TValue>>)_items)
                .GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}

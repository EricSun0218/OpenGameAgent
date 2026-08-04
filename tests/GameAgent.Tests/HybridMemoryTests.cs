using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class HybridMemoryTests
{
    private static readonly TimeSpan TestWaitTimeout =
        TimeSpan.FromSeconds(30);

    [Fact]
    public async Task VectorStoreRanksSemanticMatchesAndHonorsFilters()
    {
        var store = new VectorMemoryStore(
            new KeywordEmbeddingProvider(),
            capacity: 8);
        await store.UpsertAsync(Record("apple", "apple orchard", "agent"), cancellationToken: TestContext.Current.CancellationToken);
        await store.UpsertAsync(Record("banana", "banana market", "agent"), cancellationToken: TestContext.Current.CancellationToken);
        await store.UpsertAsync(Record("other", "apple secret", "other"), cancellationToken: TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(Query("apple", "agent"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "apple", "banana" },
            results.Select(item => item.Record.MemoryId));
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task FailedVectorReplacementPreservesPreviousRecord()
    {
        var store = new VectorMemoryStore(
            new KeywordEmbeddingProvider(),
            capacity: 8);
        await store.UpsertAsync(Record("fact", "apple orchard", "agent"), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.UpsertAsync(
                    Record("fact", "invalid-dimension", "agent"),
                    CancellationToken.None)
                .AsTask());

        var results = await store.SearchAsync(Query("apple", "agent"), cancellationToken: TestContext.Current.CancellationToken);
        var record = Assert.Single(results).Record;
        Assert.Contains("apple", record.Content.GetRawText());
    }

    [Fact]
    public void VectorStoreRejectsCapacityBeyondVectorBound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new VectorMemoryStore(
                new KeywordEmbeddingProvider(),
                capacity: 3,
                options: new VectorMemoryStoreOptions(
                    maxVectorValues: 5)));
    }

    [Fact]
    public async Task VectorAtomicBatchDoesNotPublishPartiallyPreparedData()
    {
        var store = new VectorMemoryStore(
            new KeywordEmbeddingProvider(),
            capacity: 8);
        await store.UpsertAsync(Record("existing", "apple", "agent"), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ApplyAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("new", "banana", "agent")),
                        MemoryMutation.Upsert(
                            Record("bad", "invalid-dimension", "agent"))
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        var results = await store.SearchAsync(Query("banana", "agent"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(
            results,
            item => item.Record.MemoryId is "new" or "bad");
        Assert.Contains(results, item => item.Record.MemoryId == "existing");
    }

    [Fact]
    public async Task VectorIdempotentBatchRejectsIdentityReuseWithNewPayload()
    {
        var store = new VectorMemoryStore(
            new KeywordEmbeddingProvider(),
            capacity: 8);
        var first = new[]
        {
            MemoryMutation.Upsert(Record("fact", "apple", "agent"))
        };

        var applied = await store.ApplyIdempotentAtomicBatchAsync(
            "commit-1",
            first, cancellationToken: TestContext.Current.CancellationToken);
        var replayed = await store.ApplyIdempotentAtomicBatchAsync(
            "commit-1",
            first, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(applied).Changed);
        Assert.False(Assert.Single(replayed).Changed);
        await Assert.ThrowsAsync<MemoryBatchIdempotencyConflictException>(
            () => store.ApplyIdempotentAtomicBatchAsync(
                    "commit-1",
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("fact", "banana", "agent"))
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
    }

    [Fact]
    public async Task EmbeddingDeadlineIsReportedAsTimeout()
    {
        var store = new VectorMemoryStore(
            new BlockingEmbeddingProvider(),
            capacity: 1,
            options: new VectorMemoryStoreOptions(
                embeddingTimeout: TimeSpan.FromMilliseconds(20)));

        await Assert.ThrowsAsync<TimeoutException>(
            () => store.UpsertAsync(
                    Record("fact", "apple", "agent"), cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
    }

    [Fact]
    public async Task SynchronousEmbeddingPrefixIsTimedOutAndSlotRecovers()
    {
        var provider = new SynchronouslyBlockingEmbeddingProvider();
        var store = new VectorMemoryStore(
            provider,
            "isolated-sync-vector-memory",
            2,
            new VectorMemoryStoreOptions(
                maxConcurrentEmbeddings: 1,
                embeddingTimeout: TimeSpan.FromMilliseconds(25)),
            new BoundedCallbackExecutionDispatcher(
                1,
                new BoundedCallbackProcessLimiter(1)));

        var first = store.UpsertAsync(
                Record("first", "apple", "agent"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await provider.Entered.Task.WaitAsync(TestWaitTimeout, cancellationToken: TestContext.Current.CancellationToken);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(
            () => first.WaitAsync(TestWaitTimeout, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            "Memory embedding exceeded its configured timeout.",
            timeout.Message);
        await provider.Cancelled.Task.WaitAsync(TestWaitTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(provider.RanOnThreadPool);

        await store.UpsertAsync(Record("second", "banana", "agent"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TestWaitTimeout, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task SynchronousEmbeddingPrefixesHaveProcessWideCapacity()
    {
        var processLimiter = new BoundedCallbackProcessLimiter(1);
        var blockedDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            processLimiter);
        var healthyDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            processLimiter);
        var blockedProvider = new NonCooperativeSynchronousEmbeddingProvider();
        var blocked = new VectorMemoryStore(
            blockedProvider,
            "blocked-vector-memory",
            2,
            new VectorMemoryStoreOptions(
                maxConcurrentEmbeddings: 1,
                embeddingTimeout: TimeSpan.FromMilliseconds(25)),
            blockedDispatcher);
        var healthyProvider = new KeywordEmbeddingProvider();
        var healthy = new VectorMemoryStore(
            healthyProvider,
            "healthy-vector-memory",
            2,
            new VectorMemoryStoreOptions(
                maxConcurrentEmbeddings: 1,
                embeddingTimeout: TimeSpan.FromSeconds(1)),
            healthyDispatcher);

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => blocked.UpsertAsync(
                        Record("blocked", "apple", "agent"), cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1, blockedDispatcher.ActivePrefixes);
            Assert.Equal(1, processLimiter.Active);
            Assert.False(blockedProvider.RanOnThreadPool);

            var capacity = await Assert.ThrowsAsync<
                RuntimeContentLimitException>(
                () => healthy.UpsertAsync(
                        Record("healthy", "banana", "agent"), cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains(
                "capacity is exhausted",
                capacity.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                "memory_embedding_execution_capacity_exhausted",
                capacity.LimitCode);
        }
        finally
        {
            blockedProvider.Release();
        }

        await blockedProvider.Completed.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(
            SpinWait.SpinUntil(
                () => blockedDispatcher.ActivePrefixes == 0
                      && processLimiter.Active == 0,
                TimeSpan.FromSeconds(2)));
        await healthy.UpsertAsync(Record("healthy", "banana", "agent"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EmbeddingDeadlineQuarantinesProviderThatIgnoresCancellation()
    {
        var provider = new NonCooperativeEmbeddingProvider();
        var store = new VectorMemoryStore(
            provider,
            capacity: 2,
            options: new VectorMemoryStoreOptions(
                maxConcurrentEmbeddings: 1,
                embeddingTimeout: TimeSpan.FromMilliseconds(20)));

        await Assert.ThrowsAsync<TimeoutException>(
            () => store.UpsertAsync(
                    Record("first", "apple", "agent"), cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        using var callerDeadline =
            new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.UpsertAsync(
                    Record("second", "banana", "agent"),
                    callerDeadline.Token)
                .AsTask());
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ReciprocalRankFusionCombinesIncomparableProviders()
    {
        var common = Record("common", "common", "agent");
        var lexicalOnly = Record("lexical", "lexical", "agent");
        var vectorOnly = Record("vector", "vector", "agent");
        var lexical = new FixedMemoryProvider(
            "lexical",
            new MemorySearchResult(lexicalOnly, 1_000),
            new MemorySearchResult(common, 900));
        var vector = new FixedMemoryProvider(
            "vector",
            new MemorySearchResult(vectorOnly, 1_000_000),
            new MemorySearchResult(common, 1));

        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { lexical, vector },
            options: new MemoryLifecycleOptions
            {
                RankingMode = MemoryRankingModes.ReciprocalRankFusion
            });

        var report = await lifecycle.RecallAsync(
            Query("anything", "agent", maxResults: 3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.IsPartial);
        Assert.Equal(
            MemoryRankingModes.ReciprocalRankFusion,
            report.RankingMode);
        Assert.Equal("common", report.Results[0].Record.MemoryId);
        Assert.True(report.Results[0].Score > report.Results[1].Score);
        var evidence = report.CandidateEvidence[0];
        Assert.Equal("common", evidence.MemoryId);
        Assert.Equal(report.Results[0].Score, evidence.FinalScore);
        Assert.Equal(
            new[] { "lexical", "vector" },
            evidence.Providers.Select(item => item.ProviderId));
        Assert.All(evidence.Providers, item => Assert.Equal(2, item.Rank));
        Assert.Equal(new[] { 900, 1 },
            evidence.Providers.Select(item => item.RawScore));
        Assert.Equal(
            new[] { "lexical", "vector" },
            report.Results
                .Skip(1)
                .Select(item => item.Record.MemoryId)
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReciprocalRankFusionAccumulatesBeforeGlobalPruning()
    {
        const int retainedCandidates = 128;
        var shared = Record("shared", "shared", "agent");
        var first = Enumerable.Range(0, retainedCandidates - 1)
            .Select(index => new MemorySearchResult(
                Record($"first-{index:D3}", "first", "agent"),
                retainedCandidates - index))
            .Append(new MemorySearchResult(shared, 1))
            .ToArray();
        var second = Enumerable.Range(0, retainedCandidates - 1)
            .Select(index => new MemorySearchResult(
                Record($"second-{index:D3}", "second", "agent"),
                retainedCandidates - index))
            .Append(new MemorySearchResult(shared, 1))
            .ToArray();
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[]
            {
                new FixedMemoryProvider("first", first),
                new FixedMemoryProvider("second", second)
            },
            options: new MemoryLifecycleOptions
            {
                RankingMode = MemoryRankingModes.ReciprocalRankFusion,
                MaxRetainedCandidates = retainedCandidates
            });

        var report = await lifecycle.RecallAsync(
            Query("anything", "agent", maxResults: retainedCandidates), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(
            report.Results,
            item => item.Record.MemoryId == "shared");
        Assert.Equal(
            2,
            report.CandidateEvidence.Single(
                    item => item.MemoryId == "shared")
                .Providers.Count);
    }

    [Fact]
    public async Task RawRankingRemainsTheDefault()
    {
        var low = Record("low", "low", "agent");
        var high = Record("high", "high", "agent");
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[]
            {
                new FixedMemoryProvider(
                    "one",
                    new MemorySearchResult(low, 10)),
                new FixedMemoryProvider(
                    "two",
                    new MemorySearchResult(high, 1_000_000))
            });

        var report = await lifecycle.RecallAsync(Query("q", "agent"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("high", report.Results[0].Record.MemoryId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void UnknownMemoryRankingModeIsRejected(string rankingMode)
    {
        Assert.Throws<ArgumentException>(
            () => new RuntimeMemoryLifecycle(
                Array.Empty<IMemoryProvider>(),
                options: new MemoryLifecycleOptions
                {
                    RankingMode = rankingMode
                }));
    }

    [Fact]
    public async Task VectorStoreRejectsEmbeddingIdentityDrift()
    {
        var embeddings = new MutableEmbeddingProvider();
        var store = new VectorMemoryStore(embeddings, capacity: 2);
        await store.UpsertAsync(Record("fact", "apple", "agent"), cancellationToken: TestContext.Current.CancellationToken);
        embeddings.VersionValue = "2";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SearchAsync(Query("apple", "agent"), cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    private static MemoryRecord Record(
        string id,
        string text,
        string scope) =>
        new(
            id,
            scope,
            ProtocolJson.ParseElement(
                $$"""{"text":"{{text}}"}"""),
            tags: Array.Empty<string>(),
            importance: 50,
            createdAt: DateTimeOffset.UnixEpoch,
            updatedAt: DateTimeOffset.UnixEpoch);

    private static MemoryQuery Query(
        string text,
        string scope,
        int maxResults = 8) =>
        new(
            scope,
            ProtocolJson.ParseElement(
                $$"""{"text":"{{text}}"}"""),
            maxResults: maxResults);

    private sealed class KeywordEmbeddingProvider : IMemoryEmbeddingProvider
    {
        public string ProviderId => "keyword-embedding";

        public string ModelId => "keyword-test";

        public string Version => "1";

        public int Dimensions => 2;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(
            JsonElement value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = value.GetRawText();
            if (text.Contains("invalid-dimension", StringComparison.Ordinal))
            {
                return new ValueTask<ReadOnlyMemory<float>>(
                    new float[] { 1 });
            }

            if (text.Contains("apple", StringComparison.Ordinal))
            {
                return new ValueTask<ReadOnlyMemory<float>>(
                    new float[] { 1, 0 });
            }

            if (text.Contains("banana", StringComparison.Ordinal))
            {
                return new ValueTask<ReadOnlyMemory<float>>(
                    new float[] { 0, 1 });
            }

            return new ValueTask<ReadOnlyMemory<float>>(
                new float[] { 1, 1 });
        }
    }

    private sealed class FixedMemoryProvider : IMemoryProvider
    {
        private readonly IReadOnlyList<MemorySearchResult> _results;

        public FixedMemoryProvider(
            string providerId,
            params MemorySearchResult[] results)
        {
            ProviderId = providerId;
            _results = results;
        }

        public string ProviderId { get; }

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<MemorySearchResult>>(_results);
        }
    }

    private sealed class BlockingEmbeddingProvider
        : IMemoryEmbeddingProvider
    {
        public string ProviderId => "blocking-embedding";

        public string ModelId => "blocking-test";

        public string Version => "1";

        public int Dimensions => 2;

        public async ValueTask<ReadOnlyMemory<float>> EmbedAsync(
            JsonElement value,
            CancellationToken cancellationToken)
        {
            _ = value;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new float[] { 1, 0 };
        }
    }

    private sealed class SynchronouslyBlockingEmbeddingProvider
        : IMemoryEmbeddingProvider
    {
        private int _callCount;
        private int _ranOnThreadPool;

        public string ProviderId => "sync-blocking-embedding";

        public string ModelId => "sync-blocking-test";

        public string Version => "1";

        public int Dimensions => 2;

        public int CallCount => Volatile.Read(ref _callCount);

        public bool RanOnThreadPool =>
            Volatile.Read(ref _ranOnThreadPool) != 0;

        public TaskCompletionSource<bool> Cancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(
            JsonElement value,
            CancellationToken cancellationToken)
        {
            _ = value;
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                Entered.TrySetResult(true);
                if (Thread.CurrentThread.IsThreadPoolThread)
                {
                    Volatile.Write(ref _ranOnThreadPool, 1);
                }

                using var registration = cancellationToken.Register(
                    () => Cancelled.TrySetResult(true));
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new ValueTask<ReadOnlyMemory<float>>(
                new float[] { 1, 0 });
        }
    }

    private sealed class NonCooperativeSynchronousEmbeddingProvider
        : IMemoryEmbeddingProvider
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ranOnThreadPool;

        public string ProviderId => "non-cooperative-sync-embedding";

        public string ModelId => "non-cooperative-sync-test";

        public string Version => "1";

        public int Dimensions => 2;

        public bool RanOnThreadPool =>
            Volatile.Read(ref _ranOnThreadPool) != 0;

        public Task Completed => _completed.Task;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(
            JsonElement value,
            CancellationToken cancellationToken)
        {
            _ = value;
            _ = cancellationToken;
            if (Thread.CurrentThread.IsThreadPoolThread)
            {
                Volatile.Write(ref _ranOnThreadPool, 1);
            }

            _release.Wait();
            _completed.TrySetResult();
            return new ValueTask<ReadOnlyMemory<float>>(
                new float[] { 1, 0 });
        }

        public void Release()
        {
            _release.Set();
        }
    }

    private sealed class MutableEmbeddingProvider
        : IMemoryEmbeddingProvider
    {
        public string ProviderId => "mutable-embedding";

        public string ModelId => "mutable-test";

        public string Version => VersionValue;

        public string VersionValue { get; set; } = "1";

        public int Dimensions => 2;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(
            JsonElement value,
            CancellationToken cancellationToken)
        {
            _ = value;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ReadOnlyMemory<float>>(
                new float[] { 1, 0 });
        }
    }

    private sealed class NonCooperativeEmbeddingProvider
        : IMemoryEmbeddingProvider
    {
        private readonly TaskCompletionSource<ReadOnlyMemory<float>> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public string ProviderId => "non-cooperative-embedding";

        public string ModelId => "non-cooperative-test";

        public string Version => "1";

        public int Dimensions => 2;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(
            JsonElement value,
            CancellationToken cancellationToken)
        {
            _ = value;
            _ = cancellationToken;
            CallCount++;
            return new ValueTask<ReadOnlyMemory<float>>(_never.Task);
        }
    }
}

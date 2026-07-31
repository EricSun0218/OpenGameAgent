using System.Reflection;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class MemoryLifecycleTests
{
    [Fact]
    public async Task StoreFiltersByCommittedWorldAndSaveRevision()
    {
        var store = new DeterministicMemoryStore();
        await store.UpsertAsync(
            Record(
                "current",
                "dragon",
                Provenance("world-a", 3, committed: true)),
            CancellationToken.None);
        await store.UpsertAsync(
            Record(
                "future",
                "dragon",
                Provenance("world-a", 5, committed: true)),
            CancellationToken.None);
        await store.UpsertAsync(
            Record(
                "other",
                "dragon",
                Provenance("world-b", 1, committed: true)),
            CancellationToken.None);
        await store.UpsertAsync(
            Record(
                "uncommitted",
                "dragon",
                Provenance("world-a", 1, committed: false)),
            CancellationToken.None);

        var results = await store.SearchAsync(
            Query(
                "dragon",
                worldId: "world-a",
                maximumSaveRevision: 3,
                requireCommitted: true),
            CancellationToken.None);

        Assert.Equal(
            new[] { "current" },
            results.Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task LifecycleCombinesProvidersAndReportsPartialRecall()
    {
        var first = new DeterministicMemoryStore("first");
        var second = new DeterministicMemoryStore("second");
        await first.UpsertAsync(
            Record("shared", "castle", Provenance("world", 1, true), 20),
            CancellationToken.None);
        await second.UpsertAsync(
            Record("shared", "castle", Provenance("world", 1, true), 80),
            CancellationToken.None);
        await second.UpsertAsync(
            Record("unique", "castle", Provenance("world", 1, true), 50),
            CancellationToken.None);
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[]
            {
                first,
                new FailingProvider(),
                second
            });

        var report = await lifecycle.RecallAsync(
            Query("castle", worldId: "world", requireCommitted: true));

        Assert.True(report.IsPartial);
        Assert.Equal(new[] { "failed" }, report.FailedProviderIds);
        Assert.Equal(
            new[] { "shared", "unique" },
            report.Results.Select(item => item.Record.MemoryId));
        Assert.Equal(2_800, report.Results[0].Score);
    }

    [Fact]
    public async Task LifecycleEnforcesIsolationOnUnfilteredProviders()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var observer = new GameEntityIdentity("npc", 1);
        var validProvenance = new MemoryProvenance(
            "world-a",
            "session-a",
            3,
            "run",
            "event",
            committed: true,
            timelineId: "prime",
            perspective: new GameKnowledgePerspective(
                observer,
                "witnessed"));
        var records = new[]
        {
            FilteredRecord("valid", validProvenance, now.AddHours(1)),
            FilteredRecord(
                "cross-world",
                new MemoryProvenance(
                    "world-b",
                    "session-a",
                    1,
                    "run",
                    "event",
                    true,
                    "prime",
                    new GameKnowledgePerspective(
                        observer,
                        "witnessed")),
                now.AddHours(1)),
            FilteredRecord(
                "future-save",
                new MemoryProvenance(
                    "world-a",
                    "session-a",
                    4,
                    "run",
                    "event",
                    true,
                    "prime",
                    new GameKnowledgePerspective(
                        observer,
                        "witnessed")),
                now.AddHours(1)),
            FilteredRecord(
                "uncommitted",
                new MemoryProvenance(
                    "world-a",
                    "session-a",
                    1,
                    "run",
                    "event",
                    false,
                    "prime",
                    new GameKnowledgePerspective(
                        observer,
                        "witnessed")),
                now.AddHours(1)),
            FilteredRecord("expired", validProvenance, now),
            FilteredRecord(
                "wrong-tags",
                validProvenance,
                now.AddHours(1),
                tags: new[] { "other" })
        };
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new[] { new UnfilteredProvider(records) });
        var query = new MemoryQuery(
            "agent",
            ProtocolJson.ParseElement("""{"text":"fact"}"""),
            requiredTags: new[] { "approved" },
            now: now,
            worldId: "world-a",
            sessionId: "session-a",
            maximumSaveRevision: 3,
            requireCommittedProvenance: true,
            timelineId: "prime",
            observer: observer,
            gameTime: new GameTimePoint(
                "calendar",
                "prime",
                epoch: 1,
                tick: 10));

        var report = await lifecycle.RecallAsync(query);

        Assert.Equal(
            new[] { "valid" },
            report.Results.Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task LifecycleFailClosesPerspectiveForUnfilteredProviders()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var observer = new GameEntityIdentity("npc", 1);
        var records = new[]
        {
            FilteredRecord(
                "general",
                new MemoryProvenance(
                    "world",
                    "session",
                    1,
                    "run",
                    "event",
                    committed: true,
                    timelineId: "prime"),
                now.AddHours(1)),
            FilteredRecord(
                "own",
                new MemoryProvenance(
                    "world",
                    "session",
                    1,
                    "run",
                    "event",
                    committed: true,
                    timelineId: "prime",
                    perspective: new GameKnowledgePerspective(
                        observer,
                        "witnessed")),
                now.AddHours(1)),
            FilteredRecord(
                "other",
                new MemoryProvenance(
                    "world",
                    "session",
                    1,
                    "run",
                    "event",
                    committed: true,
                    timelineId: "prime",
                    perspective: new GameKnowledgePerspective(
                        new GameEntityIdentity("npc", 2),
                        "witnessed")),
                now.AddHours(1))
        };
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new[] { new UnfilteredProvider(records) });

        MemoryQuery QueryFor(
            GameEntityIdentity? queryObserver = null,
            bool includeAllPerspectives = false)
        {
            return new MemoryQuery(
                "agent",
                ProtocolJson.ParseElement("""{"text":"fact"}"""),
                now: now,
                worldId: "world",
                timelineId: "prime",
                observer: queryObserver,
                gameTime: new GameTimePoint(
                    "calendar",
                    "prime",
                    epoch: 1,
                    tick: 10),
                includeAllPerspectives: includeAllPerspectives);
        }

        var defaultReport = await lifecycle.RecallAsync(QueryFor());
        var observerReport = await lifecycle.RecallAsync(QueryFor(observer));
        var privilegedReport = await lifecycle.RecallAsync(
            QueryFor(includeAllPerspectives: true));

        Assert.Equal(
            new[] { "general" },
            defaultReport.Results
                .Select(item => item.Record.MemoryId)
                .OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "general", "own" },
            observerReport.Results
                .Select(item => item.Record.MemoryId)
                .OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "general", "other", "own" },
            privilegedReport.Results
                .Select(item => item.Record.MemoryId)
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OversizedProviderResultIsRejectedWithoutMaterialization()
    {
        var provider = new OversizedProvider(resultCount: 5);
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider },
            options: new MemoryLifecycleOptions
            {
                MaxResultsPerProvider = 4
            });

        var report = await lifecycle.RecallAsync(
            Query("fact", worldId: "world"));

        Assert.Empty(report.Results);
        Assert.Equal(new[] { provider.ProviderId }, report.FailedProviderIds);
        Assert.Equal(0, provider.IndexReads);
    }

    [Fact]
    public async Task ProviderResultsAreSnapshottedByIndexWithoutEnumeration()
    {
        var provider = new IndexedOnlyProvider(
            new MemorySearchResult(
                Record(
                    "indexed",
                    "fact",
                    Provenance("world", 1, committed: true)),
                score: 50));
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider });

        var report = await lifecycle.RecallAsync(
            Query(
                "fact",
                worldId: "world",
                requireCommitted: true));

        Assert.False(report.IsPartial);
        Assert.Equal(
            new[] { "indexed" },
            report.Results.Select(item => item.Record.MemoryId));
        Assert.Equal(1, provider.CountReads);
        Assert.Equal(1, provider.IndexReads);
        Assert.Equal(0, provider.EnumerationAttempts);
    }

    [Fact]
    public async Task ProviderIdentityIsFrozenAtRegistration()
    {
        var provider = new ChangingIdFailingProvider();
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider });

        var report = await lifecycle.RecallAsync(Query("fact"));

        Assert.Equal(new[] { "registered" }, report.FailedProviderIds);
        Assert.Equal(1, provider.ProviderIdReads);
    }

    [Fact]
    public async Task LifecycleOnlyWritesCommittedProvenance()
    {
        var store = new DeterministicMemoryStore();
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new[] { store },
            store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await lifecycle.CommitAsync(
                Record(
                    "unsafe",
                    "rumor",
                    Provenance("world", 1, committed: false))));
        await lifecycle.CommitAsync(
            Record(
                "safe",
                "fact",
                Provenance("world", 1, committed: true)));

        var result = await store.SearchAsync(
            Query("fact", worldId: "world", requireCommitted: true),
            CancellationToken.None);
        Assert.Equal("safe", Assert.Single(result).Record.MemoryId);
    }

    [Fact]
    public async Task PrefetchIsKeyedAndConsumedOnce()
    {
        var store = new DeterministicMemoryStore();
        await store.UpsertAsync(
            Record("one", "harbor", Provenance("world", 1, true)),
            CancellationToken.None);
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new[] { store });

        lifecycle.Prefetch(
            "turn-1",
            Query("harbor", worldId: "world", requireCommitted: true));
        var first = await lifecycle.TakePrefetchedAsync("turn-1");
        var second = await lifecycle.TakePrefetchedAsync("turn-1");

        Assert.Equal("one", Assert.Single(first!.Results).Record.MemoryId);
        Assert.Null(second);
    }

    [Fact]
    public async Task ConcurrentSameKeyPrefetchStartsOnlyOneProviderQuery()
    {
        var provider = new BlockingProvider();
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider },
            options: new MemoryLifecycleOptions
            {
                MaxConcurrentPrefetches = 16
            });
        using var start = new ManualResetEventSlim();
        var callers = Enumerable.Range(0, 32)
            .Select(
                _ => Task.Run(
                    () =>
                    {
                        start.Wait();
                        lifecycle.Prefetch(
                            "shared",
                            Query(
                                "harbor",
                                worldId: "world",
                                requireCommitted: true));
                    }))
            .ToArray();

        start.Set();
        await Task.WhenAll(callers);
        await provider.Entered.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, provider.Calls);

        provider.Release();
        Assert.NotNull(await lifecycle.TakePrefetchedAsync("shared"));
    }

    [Fact]
    public async Task ConcurrentUniquePrefetchesCannotExceedCapacity()
    {
        var provider = new BlockingProvider();
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider },
            options: new MemoryLifecycleOptions
            {
                MaxConcurrentPrefetches = 16,
                MaxPrefetchEntries = 2
            });
        using var start = new ManualResetEventSlim();
        var callers = Enumerable.Range(0, 32)
            .Select(
                index => Task.Run(
                    () =>
                    {
                        start.Wait();
                        try
                        {
                            lifecycle.Prefetch(
                                "key-" + index,
                                Query("harbor", worldId: "world"));
                            return true;
                        }
                        catch (RuntimeContentLimitException exception)
                            when (exception.LimitCode
                                  == "memory_prefetch_capacity_exceeded")
                        {
                            return false;
                        }
                    }))
            .ToArray();

        start.Set();
        var admitted = await Task.WhenAll(callers);
        provider.Release();

        Assert.Equal(2, admitted.Count(value => value));
    }

    [Fact]
    public async Task ShutdownTracksAPrefetchAfterItIsConsumed()
    {
        var provider = new NonCooperativeBlockingProvider();
        var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider },
            options: new MemoryLifecycleOptions
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(20)
            });
        lifecycle.Prefetch(
            "consumed",
            Query("harbor", worldId: "world"));
        await provider.Entered.WaitAsync(TimeSpan.FromSeconds(1));
        var consumed = lifecycle
            .TakePrefetchedAsync("consumed")
            .AsTask();

        await lifecycle.DisposeAsync();
        provider.Release();

        var report = await consumed.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(report!.Results);
    }

    [Fact]
    public async Task SynchronousPrefetchProviderDoesNotHoldAdmissionLock()
    {
        var provider = new SynchronouslyBlockingProvider();
        var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider },
            options: new MemoryLifecycleOptions
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(20)
            });
        var prefetch = Task.Run(
            () => lifecycle.Prefetch(
                "blocking",
                Query("harbor", worldId: "world")));
        await provider.Entered.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            await lifecycle
                .DisposeAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            provider.Release();
        }

        await prefetch.WaitAsync(TimeSpan.FromSeconds(1));
        await lifecycle.DisposeAsync();
    }

    [Fact]
    public async Task NonCooperativeProviderFailsSoftWithoutHidingHealthyMemory()
    {
        var blocked = new NonCooperativeBlockingProvider();
        var healthy = new DeterministicMemoryStore("healthy");
        await healthy.UpsertAsync(
            Record("healthy-result", "harbor", Provenance("world", 1, true)),
            CancellationToken.None);
        var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { blocked, healthy },
            options: new MemoryLifecycleOptions
            {
                ProviderTimeout = TimeSpan.FromMilliseconds(25),
                MaxConcurrentProviderCalls = 2,
                ShutdownTimeout = TimeSpan.FromMilliseconds(25)
            });

        try
        {
            var report = await lifecycle
                .RecallAsync(Query("harbor", worldId: "world"))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(
                new[] { "healthy-result" },
                report.Results.Select(item => item.Record.MemoryId));
            Assert.Equal(
                new[] { blocked.ProviderId },
                report.FailedProviderIds);
        }
        finally
        {
            blocked.Release();
            await lifecycle.DisposeAsync();
        }

        Assert.True(lifecycle.DetachedProviderCallsDrainedOnDispose);
    }

    [Fact]
    public async Task DisposeWaitsForDetachedProviderCleanup()
    {
        var provider = new NonCooperativeBlockingProvider();
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task PauseCleanupAsync()
        {
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task.ConfigureAwait(false);
        }

        var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider },
            writeStore: null,
            options: new MemoryLifecycleOptions
            {
                ProviderTimeout = TimeSpan.FromMilliseconds(20),
                ShutdownTimeout = TimeSpan.FromSeconds(1)
            },
            new BoundedCancellationDispatcher(),
            PauseCleanupAsync);
        try
        {
            var report = await lifecycle.RecallAsync(
                Query("harbor", worldId: "world"));
            Assert.True(report.IsPartial);

            var dispose = lifecycle.DisposeAsync().AsTask();
            provider.Release();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(dispose.IsCompleted);
            Assert.Null(lifecycle.DetachedProviderCallsDrainedOnDispose);

            releaseCleanup.TrySetResult();
            await dispose.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(lifecycle.DetachedProviderCallsDrainedOnDispose);
        }
        finally
        {
            provider.Release();
            releaseCleanup.TrySetResult();
            if (lifecycle.DetachedProviderCallsDrainedOnDispose is null)
            {
                await lifecycle.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ShutdownDrainWaitsPastBoundedProviderTimeout()
    {
        var provider = new NonCooperativeBlockingProvider();
        var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider },
            options: new MemoryLifecycleOptions
            {
                ProviderTimeout = TimeSpan.FromMilliseconds(20),
                ShutdownTimeout = TimeSpan.FromMilliseconds(20)
            });
        Task? drain = null;
        try
        {
            var report = await lifecycle.RecallAsync(
                Query("harbor", worldId: "world"));
            Assert.True(report.IsPartial);

            drain = lifecycle.WaitForShutdownDrainAsync().AsTask();
            await WaitUntilAsync(
                () => lifecycle.DetachedProviderCallsDrainedOnDispose
                      == false,
                TimeSpan.FromSeconds(1));

            Assert.False(drain.IsCompleted);
            Assert.False(lifecycle.ShutdownResourceCleanupCompleted);

            provider.Release();
            await drain.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(
                lifecycle.DetachedProviderCallsDrainedOnDispose);
            Assert.True(lifecycle.ShutdownResourceCleanupCompleted);
        }
        finally
        {
            provider.Release();
            if (drain is null)
            {
                await lifecycle.WaitForShutdownDrainAsync();
            }
            else
            {
                await drain.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Fact]
    public async Task ShutdownDrainCallerCancellationDoesNotWaitForBoundedDispose()
    {
        var provider = new NonCooperativeBlockingProvider();
        var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider },
            options: new MemoryLifecycleOptions
            {
                ProviderTimeout = TimeSpan.FromMilliseconds(20),
                ShutdownTimeout = TimeSpan.FromSeconds(2)
            });
        Task? cancelledWait = null;
        try
        {
            var report = await lifecycle.RecallAsync(
                Query("harbor", worldId: "world"));
            Assert.True(report.IsPartial);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            cancelledWait = lifecycle
                .WaitForShutdownDrainAsync(cancellation.Token)
                .AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelledWait.WaitAsync(
                    TimeSpan.FromMilliseconds(250)));

            Assert.False(lifecycle.ShutdownResourceCleanupCompleted);
        }
        finally
        {
            provider.Release();
            if (cancelledWait is not null)
            {
                try
                {
                    await cancelledWait.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException)
                {
                    // Caller cancellation must not cancel shared cleanup.
                }
            }

            await lifecycle
                .WaitForShutdownDrainAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(lifecycle.ShutdownResourceCleanupCompleted);
    }

    [Fact]
    public async Task RepeatedDisposeStartsOnlyOneBackgroundCleanupTask()
    {
        var provider = new NonCooperativeBlockingProvider();
        var lifecycle = new RuntimeMemoryLifecycle(
            new[] { provider },
            options: new MemoryLifecycleOptions
            {
                ProviderTimeout = TimeSpan.FromMilliseconds(20),
                ShutdownTimeout = TimeSpan.FromMilliseconds(20)
            });
        try
        {
            var report = await lifecycle.RecallAsync(
                Query("harbor", worldId: "world"));
            Assert.True(report.IsPartial);

            await lifecycle
                .DisposeAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(lifecycle.DetachedProviderCallsDrainedOnDispose);
            Assert.False(lifecycle.ShutdownResourceCleanupCompleted);

            var cleanupField = typeof(RuntimeMemoryLifecycle).GetField(
                "_resourceCleanupTask",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(cleanupField);
            var sharedCleanup = Assert.IsAssignableFrom<Task>(
                cleanupField!.GetValue(lifecycle));
            Assert.False(sharedCleanup.IsCompleted);

            for (var call = 0; call < 8; call++)
            {
                await lifecycle
                    .DisposeAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Same(
                    sharedCleanup,
                    cleanupField.GetValue(lifecycle));
                Assert.False(lifecycle.ShutdownResourceCleanupCompleted);
            }
        }
        finally
        {
            provider.Release();
            await lifecycle
                .WaitForShutdownDrainAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(lifecycle.ShutdownResourceCleanupCompleted);
    }

    [Fact]
    public async Task ShutdownAdmissionFailureIsExplicitAndRetriable()
    {
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        Assert.True(dispatcher.TryReserve(out var occupied));
        var lifecycle = new RuntimeMemoryLifecycle(
            Array.Empty<IMemoryProvider>(),
            writeStore: null,
            options: new MemoryLifecycleOptions(),
            dispatcher);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => lifecycle.DisposeAsync().AsTask());
            Assert.Contains(
                "capacity",
                error.Message,
                StringComparison.OrdinalIgnoreCase);

            await lifecycle.RecallAsync(
                Query("still-open", worldId: "world"));
            occupied!.Dispose();
            occupied = null;

            await lifecycle.DisposeAsync();
            Assert.True(
                lifecycle.DetachedProviderCallsDrainedOnDispose);
            await lifecycle.DisposeAsync();
            Assert.Equal(0, dispatcher.ActiveReservations);
        }
        finally
        {
            occupied?.Dispose();
            if (lifecycle.DetachedProviderCallsDrainedOnDispose is null)
            {
                await lifecycle.DisposeAsync();
            }
        }
    }

    [Fact]
    public void ProviderEnumerationStopsAtConfiguredLimit()
    {
        var reads = 0;

        IEnumerable<IMemoryProvider> Providers()
        {
            while (true)
            {
                reads++;
                yield return new DeterministicMemoryStore(
                    "provider-" + reads);
            }
        }

        Assert.Throws<ArgumentException>(
            () => new RuntimeMemoryLifecycle(
                Providers(),
                options: new MemoryLifecycleOptions
                {
                    MaxProviders = 2
                }));
        Assert.Equal(3, reads);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The expected memory shutdown state was not observed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private static MemoryRecord Record(
        string id,
        string text,
        MemoryProvenance provenance,
        int importance = 50)
    {
        return new MemoryRecord(
            id,
            "agent",
            ProtocolJson.ParseElement(
                $$"""{"text":"{{text}}"}"""),
            new[] { "test" },
            importance,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: provenance);
    }

    private static MemoryProvenance Provenance(
        string worldId,
        long revision,
        bool committed)
    {
        return new MemoryProvenance(
            worldId,
            "session",
            revision,
            "run",
            "event",
            committed);
    }

    private static MemorySearchResult FilteredRecord(
        string id,
        MemoryProvenance provenance,
        DateTimeOffset expiresAt,
        IEnumerable<string>? tags = null)
    {
        return new MemorySearchResult(
            new MemoryRecord(
                id,
                "agent",
                ProtocolJson.ParseElement("""{"text":"fact"}"""),
                tags ?? new[] { "approved" },
                importance: 50,
                createdAt: DateTimeOffset.UnixEpoch,
                updatedAt: DateTimeOffset.UnixEpoch,
                expiresAt,
                provenance,
                new GameTimeWindow(
                    new GameTimePoint(
                        "calendar",
                        "prime",
                        epoch: 1,
                        tick: 0),
                    new GameTimePoint(
                        "calendar",
                        "prime",
                        epoch: 1,
                        tick: 20))),
            score: 100);
    }

    private static MemoryQuery Query(
        string text,
        string? worldId = null,
        long? maximumSaveRevision = null,
        bool requireCommitted = false)
    {
        return new MemoryQuery(
            "agent",
            ProtocolJson.ParseElement(
                $$"""{"text":"{{text}}"}"""),
            worldId: worldId,
            maximumSaveRevision: maximumSaveRevision,
            requireCommittedProvenance: requireCommitted);
    }

    private sealed class FailingProvider : IMemoryProvider
    {
        public string ProviderId => "failed";

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("expected");
        }
    }

    private sealed class UnfilteredProvider : IMemoryProvider
    {
        private readonly IReadOnlyList<MemorySearchResult> _results;

        public UnfilteredProvider(
            IReadOnlyList<MemorySearchResult> results)
        {
            _results = results;
        }

        public string ProviderId => "unfiltered";

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<MemorySearchResult>>(
                _results);
        }
    }

    private sealed class OversizedProvider : IMemoryProvider
    {
        private readonly OversizedResultList _results;

        public OversizedProvider(int resultCount)
        {
            _results = new OversizedResultList(resultCount);
        }

        public string ProviderId => "oversized";

        public int IndexReads => _results.IndexReads;

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<MemorySearchResult>>(
                _results);
        }
    }

    private sealed class OversizedResultList
        : IReadOnlyList<MemorySearchResult>
    {
        private int _indexReads;

        public OversizedResultList(int count)
        {
            Count = count;
        }

        public int Count { get; }

        public int IndexReads => Volatile.Read(ref _indexReads);

        public MemorySearchResult this[int index]
        {
            get
            {
                Interlocked.Increment(ref _indexReads);
                throw new InvalidOperationException(
                    "Oversized results must not be enumerated.");
            }
        }

        public IEnumerator<MemorySearchResult> GetEnumerator()
        {
            throw new InvalidOperationException(
                "Oversized results must not be enumerated.");
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class IndexedOnlyProvider : IMemoryProvider
    {
        private readonly IndexedOnlyResultList _results;

        public IndexedOnlyProvider(MemorySearchResult result)
        {
            _results = new IndexedOnlyResultList(result);
        }

        public string ProviderId => "indexed-only";

        public int CountReads => _results.CountReads;

        public int IndexReads => _results.IndexReads;

        public int EnumerationAttempts => _results.EnumerationAttempts;

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<MemorySearchResult>>(
                _results);
        }
    }

    private sealed class IndexedOnlyResultList :
        IReadOnlyList<MemorySearchResult>
    {
        private readonly MemorySearchResult _result;

        public IndexedOnlyResultList(MemorySearchResult result)
        {
            _result = result;
        }

        public int Count
        {
            get
            {
                CountReads++;
                return 1;
            }
        }

        public MemorySearchResult this[int index]
        {
            get
            {
                IndexReads++;
                return index == 0
                    ? _result
                    : throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public int CountReads { get; private set; }

        public int IndexReads { get; private set; }

        public int EnumerationAttempts { get; private set; }

        public IEnumerator<MemorySearchResult> GetEnumerator()
        {
            EnumerationAttempts++;
            throw new NotSupportedException(
                "The provider result collection cannot be enumerated.");
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class ChangingIdFailingProvider : IMemoryProvider
    {
        private int _reads;

        public int ProviderIdReads => Volatile.Read(ref _reads);

        public string ProviderId =>
            Interlocked.Increment(ref _reads) == 1
                ? "registered"
                : "changed";

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("expected");
        }
    }

    private sealed class BlockingProvider : IMemoryProvider
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public string ProviderId => "blocking";

        public int Calls => Volatile.Read(ref _calls);

        public Task Entered => _entered.Task;

        public async ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            Interlocked.Increment(ref _calls);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return Array.Empty<MemorySearchResult>();
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class NonCooperativeBlockingProvider : IMemoryProvider
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "non-cooperative";

        public Task Entered => _entered.Task;

        public async ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            _ = cancellationToken;
            _entered.TrySetResult(true);
            await _release.Task;
            return Array.Empty<MemorySearchResult>();
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class SynchronouslyBlockingProvider : IMemoryProvider
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();

        public string ProviderId => "synchronously-blocking";

        public Task Entered => _entered.Task;

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            _ = query;
            _ = cancellationToken;
            _entered.TrySetResult(true);
            _release.Wait();
            return new ValueTask<IReadOnlyList<MemorySearchResult>>(
                Array.Empty<MemorySearchResult>());
        }

        public void Release()
        {
            _release.Set();
        }
    }
}

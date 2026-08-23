using System.Reflection;
using System.Text.Json;
using OpenGameAgent.Memory;
using OpenGameAgent.Persistence;
using Xunit;

namespace OpenGameAgent.Memory.Tests;

public sealed class VectorMemoryTests
{
    [Fact]
    public async Task SemanticRecallFindsMemoryThatLexicalSearchMisses()
    {
        var provider = new TestEmbeddingProvider(version: "1");
        await using var store = new VectorMemoryStore(
            new InMemoryGameMemoryStore(),
            new InMemoryVectorMemoryIndex(),
            provider,
            reranker: new GameAwareMemoryReranker());
        await store.AppendAsync(Memory("feline", "A quiet feline watches the gate."), TestCancellation);

        var results = await store.SearchAsync(Query("cat"), TestCancellation);

        var result = Assert.Single(results);
        Assert.Equal("feline", result.MemoryId);
        Assert.Equal(1, provider.QueryCalls);
        Assert.Equal(1, provider.DocumentCalls);
    }

    [Fact]
    public async Task EmbeddingFailurePreservesAuthoritativeMemoryAndFallsBackToLexical()
    {
        var diagnostics = new RecordingDiagnosticSink();
        var provider = new TestEmbeddingProvider(version: "1") { FailDocuments = true };
        await using var store = new VectorMemoryStore(
            new InMemoryGameMemoryStore(),
            new InMemoryVectorMemoryIndex(),
            provider,
            diagnostics: diagnostics,
            options: new VectorMemoryStoreOptions(embeddingTimeout: TimeSpan.FromSeconds(1)));

        await store.AppendAsync(Memory("saved", "orchard ledger"), TestCancellation);
        var results = await store.SearchAsync(Query("orchard"), TestCancellation);
        var status = await store.GetStatusAsync("session", TestCancellation);

        Assert.Equal("saved", Assert.Single(results).MemoryId);
        Assert.Equal(VectorMemoryState.Degraded, status.State);
        Assert.Equal(1, status.PendingEntries);
        Assert.Contains(diagnostics.Items, item => item.Code == "memory_embedding_append_failed");
    }

    [Fact]
    public async Task NonCooperativeEmbeddingIsBoundedAndReported()
    {
        var diagnostics = new RecordingDiagnosticSink();
        var provider = new TestEmbeddingProvider(version: "1") { NeverCompleteDocuments = true };
        await using var store = new VectorMemoryStore(
            new InMemoryGameMemoryStore(),
            new InMemoryVectorMemoryIndex(),
            provider,
            diagnostics: diagnostics,
            options: new VectorMemoryStoreOptions(embeddingTimeout: TimeSpan.FromMilliseconds(25)));

        await store.AppendAsync(Memory("bounded", "timeout record"), TestCancellation).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), TestCancellation);

        Assert.Contains(diagnostics.Items, item => item.Code == "memory_embedding_append_failed");
        provider.CompletePendingDocuments();
    }

    [Fact]
    public async Task TimedOutNonCooperativeEmbeddingKeepsItsConcurrencyLeaseUntilSettlement()
    {
        var provider = new TestEmbeddingProvider(version: "1") { NeverCompleteDocuments = true };
        await using var store = new VectorMemoryStore(
            new InMemoryGameMemoryStore(),
            new InMemoryVectorMemoryIndex(),
            provider,
            options: new VectorMemoryStoreOptions(
                maximumConcurrentEmbeddingCalls: 1,
                embeddingTimeout: TimeSpan.FromMilliseconds(25)));

        await store.AppendAsync(Memory("first", "first record"), TestCancellation);
        await store.AppendAsync(Memory("second", "second record"), TestCancellation).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1), TestCancellation);
        Assert.Equal(1, provider.DocumentCalls);

        provider.CompletePendingDocuments();
    }

    [Fact]
    public async Task ModelIdentityChangeRequiresExplicitRebuildAndSurvivesRestart()
    {
        var root = TempDirectory();
        try
        {
            var authoritative = new FileGameMemoryStore(Path.Combine(root, "memory"));
            var indexPath = Path.Combine(root, "vectors");
            await using (var first = new VectorMemoryStore(
                             authoritative,
                             new FileVectorMemoryIndex(indexPath),
                             new TestEmbeddingProvider(version: "bge-m3-v1")))
            {
                await first.AppendAsync(Memory("legacy", "feline sentry"), TestCancellation);
                Assert.Equal(VectorMemoryState.Ready, (await first.GetStatusAsync("session", TestCancellation)).State);
            }

            await using (var second = new VectorMemoryStore(
                             authoritative,
                             new FileVectorMemoryIndex(indexPath),
                             new TestEmbeddingProvider(version: "bge-m3-v2")))
            {
                var before = await second.GetStatusAsync("session", TestCancellation);
                Assert.Equal(VectorMemoryState.RebuildRequired, before.State);
                Assert.Equal(1, before.StaleEntries);
                Assert.Empty(await second.SearchAsync(Query("cat"), TestCancellation));

                var after = await second.RebuildAsync("session", TestCancellation);
                Assert.Equal(VectorMemoryState.Ready, after.State);
                Assert.Equal("bge-m3-v2", after.ActiveIdentity.Version);
            }

            await using var reopened = new VectorMemoryStore(
                authoritative,
                new FileVectorMemoryIndex(indexPath),
                new TestEmbeddingProvider(version: "bge-m3-v2"));
            var persisted = await reopened.GetStatusAsync("session", TestCancellation);
            Assert.Equal(VectorMemoryState.Ready, persisted.State);
            Assert.Equal("legacy", Assert.Single(await reopened.SearchAsync(Query("cat"), TestCancellation)).MemoryId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RebuildImportsMemoriesThatPredateTheVectorWrapper()
    {
        var authoritative = new InMemoryGameMemoryStore();
        await authoritative.AppendAsync(Memory("existing", "feline archive"), TestCancellation);
        await using var store = new VectorMemoryStore(
            authoritative,
            new InMemoryVectorMemoryIndex(),
            new TestEmbeddingProvider(version: "1"));

        var before = await store.GetStatusAsync("session", TestCancellation);
        Assert.Equal(VectorMemoryState.Degraded, before.State);
        Assert.True(before.RequiresRebuild);
        var rebuilt = await store.RebuildAsync("session", TestCancellation);

        Assert.Equal(VectorMemoryState.Ready, rebuilt.State);
        Assert.Equal("existing", Assert.Single(await store.SearchAsync(Query("cat"), TestCancellation)).MemoryId);
    }

    [Fact]
    public async Task ConcurrentAppendRemainsAuthoritativeAndMakesRebuildExplicitlyIncomplete()
    {
        var authoritative = new InMemoryGameMemoryStore();
        await authoritative.AppendAsync(Memory("existing", "feline archive"), TestCancellation);
        var provider = new OrderedEmbeddingProvider();
        await using var store = new VectorMemoryStore(
            authoritative,
            new InMemoryVectorMemoryIndex(),
            provider,
            options: new VectorMemoryStoreOptions(maximumConcurrentEmbeddingCalls: 2));

        var rebuild = store.RebuildAsync("session", TestCancellation).AsTask();
        await provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestCancellation);
        var append = store.AppendAsync(Memory("concurrent", "new feline record"), TestCancellation).AsTask();
        await provider.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestCancellation);

        provider.CompleteSecond();
        await append;
        provider.CompleteFirst();
        var status = await rebuild;

        Assert.True(status.RequiresRebuild);
        Assert.Equal(1, status.PendingEntries);
        Assert.Contains(
            await authoritative.SearchAsync(Query("record"), TestCancellation),
            memory => memory.MemoryId == "concurrent");
    }

    [Fact]
    public async Task DerivedOrphanNeverEntersRecallAndExplicitRebuildRemovesIt()
    {
        var authoritative = new InMemoryGameMemoryStore();
        var index = new InMemoryVectorMemoryIndex();
        var provider = new TestEmbeddingProvider(version: "1");
        await index.UpsertAsync(
            new VectorMemoryIndexEntry(
                Memory("orphan", "feline impostor"),
                provider.Identity,
                new float[] { 1, 0 }),
            TestCancellation);
        await using var store = new VectorMemoryStore(authoritative, index, provider);

        var before = await store.GetStatusAsync("session", TestCancellation);
        Assert.Equal(1, before.OrphanEntries);
        Assert.Empty(await store.SearchAsync(Query("cat"), TestCancellation));

        var after = await store.RebuildAsync("session", TestCancellation);
        Assert.Equal(VectorMemoryState.Empty, after.State);
        Assert.Equal(0, after.OrphanEntries);
    }

    [Fact]
    public async Task VectorRecallPreservesSessionActorAndGameTimeFilters()
    {
        await using var store = new VectorMemoryStore(
            new InMemoryGameMemoryStore(),
            new InMemoryVectorMemoryIndex(),
            new TestEmbeddingProvider(version: "1"));
        await store.AppendAsync(Memory("visible", "feline", owner: "npc", tick: 2), TestCancellation);
        await store.AppendAsync(Memory("future", "feline", owner: "npc", tick: 8), TestCancellation);
        await store.AppendAsync(Memory("other-owner", "feline", owner: "other", tick: 2), TestCancellation);
        await store.AppendAsync(Memory("other-session", "feline", owner: "npc", tick: 2, session: "other-session"), TestCancellation);

        var results = await store.SearchAsync(
            new GameMemoryQuery(
                "session",
                10,
                ownerId: "npc",
                text: "cat",
                atOrBefore: new GameMoment("world", 5)),
            TestCancellation);

        Assert.Equal("visible", Assert.Single(results).MemoryId);
    }

    [Fact]
    public async Task GameAwareRerankerUsesGameTimeAndNeverWallClock()
    {
        var reranker = new GameAwareMemoryReranker(new GameAwareMemoryRerankerOptions(
            sourceOrderWeight: 0,
            importanceWeight: 0,
            gameTimeRecencyWeight: 1_000_000,
            diversityPenalty: 0));
        var old = Memory("old", "same", tick: 1);
        var recent = Memory("recent", "same", tick: 99);

        var ranked = await reranker.RankAsync(
            new GameMemoryQuery("session", 2, text: "same", atOrBefore: new GameMoment("world", 100)),
            new[] { old, recent },
            TestCancellation);

        Assert.Equal(new[] { "recent", "old" }, ranked.Select(memory => memory.MemoryId));
    }

    [Fact]
    public async Task FileIndexRejectsCorruptDerivedStateWithoutDamagingAuthoritativeSave()
    {
        var root = TempDirectory();
        try
        {
            var authoritative = new FileGameMemoryStore(Path.Combine(root, "memory"));
            var indexPath = Path.Combine(root, "vectors");
            await using var store = new VectorMemoryStore(
                authoritative,
                new FileVectorMemoryIndex(indexPath),
                new TestEmbeddingProvider(version: "1"));
            await store.AppendAsync(Memory("safe", "orchard"), TestCancellation);
            var file = Assert.Single(Directory.GetFiles(indexPath, "*.vector-memory.json"));
            await File.WriteAllTextAsync(file, "{broken", TestCancellation);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await new FileVectorMemoryIndex(indexPath).ListAsync("session", 100, TestCancellation));
            Assert.Equal("safe", Assert.Single(await authoritative.SearchAsync(Query("orchard"), TestCancellation)).MemoryId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeLifecycleOwnsExplicitInspectionRebuildAndCleanup()
    {
        var provider = new TestEmbeddingProvider(version: "1");
        var authoritative = new InMemoryGameMemoryStore();
        await authoritative.AppendAsync(Memory("existing", "feline"), TestCancellation);
        var store = new VectorMemoryStore(authoritative, new InMemoryVectorMemoryIndex(), provider);
        await using (var lifecycle = new RuntimeMemoryLifecycle(store))
        {
            var before = await lifecycle.InspectAsync("session", TestCancellation);
            Assert.Equal(VectorMemoryState.Degraded, before.State);
            Assert.True(before.RequiresRebuild);
            Assert.Equal(VectorMemoryState.Ready, (await lifecycle.RebuildAsync("session", TestCancellation)).State);
        }

        Assert.True(provider.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await store.GetStatusAsync("session", TestCancellation));
    }

    [Fact]
    public async Task SnapshotSourcesAreDeterministicAndSessionScoped()
    {
        var memory = new InMemoryGameMemoryStore();
        await memory.AppendAsync(Memory("b", "two", owner: "z"), TestCancellation);
        await memory.AppendAsync(Memory("a", "one", owner: "a"), TestCancellation);
        await memory.AppendAsync(Memory("foreign", "three", session: "foreign"), TestCancellation);

        var items = new List<GameMemory>();
        await foreach (var item in memory.EnumerateAsync("session", TestCancellation))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { "a", "b" }, items.Select(item => item.MemoryId));
    }

    [Fact]
    public async Task HybridSearchReusesOneAuthoritativeSnapshotAndReportsEveryStage()
    {
        var authoritative = new RecordingSnapshotStore();
        await using var store = new VectorMemoryStore(
            authoritative,
            new InMemoryVectorMemoryIndex(),
            new TestEmbeddingProvider(version: "1"),
            reranker: new GameAwareMemoryReranker());
        await store.AppendAsync(Memory("one", "feline archive"), TestCancellation);
        await store.AppendAsync(Memory("two", "orchard ledger"), TestCancellation);
        authoritative.ResetCounters();

        var snapshot = await store.SearchSnapshotAsync(Query("cat"), 100, TestCancellation);

        Assert.Contains(snapshot.Memories, memory => memory.MemoryId == "one");
        Assert.Equal(1, authoritative.SearchSnapshotCalls);
        Assert.Equal(0, authoritative.SearchCalls);
        Assert.Equal(0, authoritative.EnumerateCalls);
        var authoritativeStage = Assert.Single(
            snapshot.Stages,
            stage => stage.Stage == GameMemorySearchStageKind.AuthoritativeSnapshot);
        Assert.True(authoritativeStage.Reused);
        Assert.Contains(snapshot.Stages, stage => stage.Stage == GameMemorySearchStageKind.LexicalSearch);
        Assert.Contains(snapshot.Stages, stage => stage.Stage == GameMemorySearchStageKind.Embedding);
        Assert.Contains(snapshot.Stages, stage => stage.Stage == GameMemorySearchStageKind.VectorIndexRead);
        Assert.Contains(snapshot.Stages, stage => stage.Stage == GameMemorySearchStageKind.VectorScoring);
        Assert.Contains(snapshot.Stages, stage => stage.Stage == GameMemorySearchStageKind.Rerank);
    }

    [Fact]
    public async Task FileVectorIndexMigratesLegacyFlatFilesAndRestartsWithOwnerPartitions()
    {
        var root = TempDirectory();
        try
        {
            var index = new FileVectorMemoryIndex(root, capacity: 100);
            var identity = new MemoryEmbeddingIdentity("local", "test", "1", 2);
            await index.UpsertAsync(
                new VectorMemoryIndexEntry(Memory("one", "alpha", owner: "owner-a"), identity, new float[] { 1, 0 }),
                TestCancellation);
            await index.UpsertAsync(
                new VectorMemoryIndexEntry(Memory("two", "beta", owner: "owner-b"), identity, new float[] { 0, 1 }),
                TestCancellation);

            Directory.Delete(Path.Combine(root, ".vector-partitions-v2"), recursive: true);
            var migrated = new FileVectorMemoryIndex(root, capacity: 100);
            Assert.Equal(2, await migrated.MigrateLegacyIndexAsync(TestCancellation));
            Assert.Equal(
                "one",
                Assert.Single(await migrated.ListAsync("session", "owner-a", 100, TestCancellation)).Memory.MemoryId);

            var restarted = new FileVectorMemoryIndex(root, capacity: 100);
            Assert.Equal(
                "two",
                Assert.Single(await restarted.ListAsync("session", "owner-b", 100, TestCancellation)).Memory.MemoryId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileVectorOwnerLookupDoesNotReadAnUnrelatedCorruptPartition()
    {
        var root = TempDirectory();
        try
        {
            var index = new FileVectorMemoryIndex(root, capacity: 100);
            var identity = new MemoryEmbeddingIdentity("local", "test", "1", 2);
            await index.UpsertAsync(
                new VectorMemoryIndexEntry(Memory("target", "alpha", owner: "target"), identity, new float[] { 1, 0 }),
                TestCancellation);
            await index.UpsertAsync(
                new VectorMemoryIndexEntry(Memory("unrelated", "beta", owner: "unrelated"), identity, new float[] { 0, 1 }),
                TestCancellation);
            var unrelatedPath = Directory.GetFiles(root, "*.vector-memory.json", SearchOption.TopDirectoryOnly)
                .Single(path => File.ReadAllText(path).Contains("unrelated", StringComparison.Ordinal));
            await File.WriteAllTextAsync(unrelatedPath, "{broken", TestCancellation);

            var restarted = new FileVectorMemoryIndex(root, capacity: 100);
            Assert.Equal(
                "target",
                Assert.Single(await restarted.ListAsync("session", "target", 100, TestCancellation)).Memory.MemoryId);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await restarted.ListAsync("session", 100, TestCancellation));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentFileVectorWritersRemainBoundedAndRestartable()
    {
        var root = TempDirectory();
        try
        {
            var first = new FileVectorMemoryIndex(root, capacity: 100);
            var second = new FileVectorMemoryIndex(root, capacity: 100);
            var identity = new MemoryEmbeddingIdentity("local", "test", "1", 2);
            await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
                (index % 2 == 0 ? first : second).UpsertAsync(
                        new VectorMemoryIndexEntry(
                            Memory("memory-" + index, "value", owner: "owner"),
                            identity,
                            new float[] { 1, 0 }),
                        TestCancellation)
                    .AsTask()));

            var restarted = new FileVectorMemoryIndex(root, capacity: 100);
            Assert.Equal(40, (await restarted.ListAsync("session", "owner", 100, TestCancellation)).Count);
            Assert.Equal(40, await restarted.MigrateLegacyIndexAsync(TestCancellation));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PublicApiContainsTheStableVectorMemoryEntryPoints()
    {
        var assembly = typeof(VectorMemoryStore).Assembly;
        var exported = assembly.GetExportedTypes().Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("OpenGameAgent.Memory.IMemoryEmbeddingProvider", exported);
        Assert.Contains("OpenGameAgent.Memory.VectorMemoryStore", exported);
        Assert.Contains("OpenGameAgent.Memory.RuntimeMemoryLifecycle", exported);
        Assert.Contains("OpenGameAgent.Memory.GameAwareMemoryReranker", exported);
    }

    [Fact]
    public void StoredVectorValueBoundRejectsAnOversizedIndexAtCompositionTime()
    {
        var exception = Assert.Throws<ArgumentException>(() => new VectorMemoryStore(
            new InMemoryGameMemoryStore(),
            new InMemoryVectorMemoryIndex(),
            new TestEmbeddingProvider(version: "1"),
            options: new VectorMemoryStoreOptions(
                maximumIndexEntries: 10,
                maximumStoredVectorValues: 10)));

        Assert.Equal("options", exception.ParamName);
    }

    private static GameMemory Memory(
        string id,
        string text,
        string owner = "npc",
        long tick = 1,
        string session = "session") =>
        new(
            id,
            session,
            owner,
            "personal",
            GameMemoryKind.Fact,
            "{\"text\":\"" + text + "\"}",
            new GameMoment("world", tick),
            searchableText: text);

    private static GameMemoryQuery Query(string text) => new(
        "session",
        10,
        ownerId: "npc",
        text: text,
        atOrBefore: new GameMoment("world", 100));

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "opengameagent-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingDiagnosticSink : IMemoryVectorDiagnosticSink
    {
        public List<MemoryVectorDiagnostic> Items { get; } = new();

        public ValueTask ReportAsync(MemoryVectorDiagnostic diagnostic, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items.Add(diagnostic);
            return default;
        }
    }

    private sealed class RecordingSnapshotStore :
        IGameMemoryStore,
        IGameMemorySnapshotSource,
        IGameMemoryPartitionSnapshotSource,
        IGameMemorySearchSnapshotSource
    {
        private readonly InMemoryGameMemoryStore _inner = new();

        public int SearchCalls { get; private set; }

        public int SearchSnapshotCalls { get; private set; }

        public int EnumerateCalls { get; private set; }

        public ValueTask AppendAsync(GameMemory memory, CancellationToken cancellationToken) =>
            _inner.AppendAsync(memory, cancellationToken);

        public async ValueTask<IReadOnlyList<GameMemory>> SearchAsync(
            GameMemoryQuery query,
            CancellationToken cancellationToken)
        {
            SearchCalls++;
            return await _inner.SearchAsync(query, cancellationToken);
        }

        public async ValueTask<GameMemorySearchSnapshot> SearchSnapshotAsync(
            GameMemoryQuery query,
            int maximumSnapshotEntries,
            CancellationToken cancellationToken)
        {
            SearchSnapshotCalls++;
            return await _inner.SearchSnapshotAsync(query, maximumSnapshotEntries, cancellationToken);
        }

        public async IAsyncEnumerable<GameMemory> EnumerateAsync(
            string sessionId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            EnumerateCalls++;
            await foreach (var memory in _inner.EnumerateAsync(sessionId, cancellationToken))
            {
                yield return memory;
            }
        }

        public async IAsyncEnumerable<GameMemory> EnumerateAsync(
            string sessionId,
            string ownerId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            EnumerateCalls++;
            await foreach (var memory in _inner.EnumerateAsync(sessionId, ownerId, cancellationToken))
            {
                yield return memory;
            }
        }

        public void ResetCounters()
        {
            SearchCalls = 0;
            SearchSnapshotCalls = 0;
            EnumerateCalls = 0;
        }
    }

    private sealed class TestEmbeddingProvider : IMemoryEmbeddingProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<ReadOnlyMemory<float>>> _pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestEmbeddingProvider(string version)
        {
            Identity = new MemoryEmbeddingIdentity("local", "bge-m3", version, 2);
        }

        public MemoryEmbeddingIdentity Identity { get; }

        public bool FailDocuments { get; set; }

        public bool NeverCompleteDocuments { get; set; }

        public int QueryCalls { get; private set; }

        public int DocumentCalls { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<ReadOnlyMemory<float>> EmbedQueryAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCalls++;
            return new ValueTask<ReadOnlyMemory<float>>(Embed(text));
        }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentCalls++;
            if (FailDocuments)
            {
                throw new InvalidOperationException("simulated embedding outage");
            }

            if (NeverCompleteDocuments)
            {
                return new ValueTask<IReadOnlyList<ReadOnlyMemory<float>>>(_pending.Task);
            }

            IReadOnlyList<ReadOnlyMemory<float>> results = texts.Select(Embed).ToArray();
            return new ValueTask<IReadOnlyList<ReadOnlyMemory<float>>>(results);
        }

        public void CompletePendingDocuments()
        {
            IReadOnlyList<ReadOnlyMemory<float>> value = new[] { Embed("fallback") };
            _pending.TrySetResult(value);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            CompletePendingDocuments();
            return default;
        }

        private static ReadOnlyMemory<float> Embed(string text) =>
            text.Contains("cat", StringComparison.OrdinalIgnoreCase)
            || text.Contains("feline", StringComparison.OrdinalIgnoreCase)
                ? new float[] { 1, 0 }
                : new float[] { 0, 1 };
    }

    private sealed class OrderedEmbeddingProvider : IMemoryEmbeddingProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<ReadOnlyMemory<float>>> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<ReadOnlyMemory<float>>> _second =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public MemoryEmbeddingIdentity Identity { get; } = new("local", "ordered", "1", 2);

        public TaskCompletionSource<bool> FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ReadOnlyMemory<float>> EmbedQueryAsync(string text, CancellationToken cancellationToken) =>
            new(new ReadOnlyMemory<float>(new float[] { 1, 0 }));

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstStarted.TrySetResult(true);
                return new(_first.Task);
            }

            if (call == 2)
            {
                SecondStarted.TrySetResult(true);
                return new(_second.Task);
            }

            throw new InvalidOperationException("Unexpected embedding call.");
        }

        public void CompleteFirst() => _first.TrySetResult(new[] { new ReadOnlyMemory<float>(new float[] { 1, 0 }) });

        public void CompleteSecond() => _second.TrySetResult(new[] { new ReadOnlyMemory<float>(new float[] { 1, 0 }) });

        public ValueTask DisposeAsync()
        {
            CompleteFirst();
            CompleteSecond();
            return default;
        }
    }
}

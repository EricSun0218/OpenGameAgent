using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class MemoryBatchTests
{
    [Fact]
    public void OriginalMemoryConstructorSignaturesRemainAvailable()
    {
        Assert.NotNull(
            typeof(MemoryRecord).GetConstructor(
                new[]
                {
                    typeof(string),
                    typeof(string),
                    typeof(JsonElement),
                    typeof(IEnumerable<string>),
                    typeof(int),
                    typeof(DateTimeOffset),
                    typeof(DateTimeOffset),
                    typeof(DateTimeOffset?)
                }));
        Assert.NotNull(
            typeof(MemoryQuery).GetConstructor(
                new[]
                {
                    typeof(string),
                    typeof(JsonElement),
                    typeof(IEnumerable<string>),
                    typeof(int),
                    typeof(int),
                    typeof(DateTimeOffset?)
                }));
    }

    [Fact]
    public async Task MixedBatchCommitsAsOneVisibleState()
    {
        IAtomicMemoryBatchStore store =
            new DeterministicMemoryStore(capacity: 1);
        await store.UpsertAsync(
            Record("old", """{"version":1}"""),
            CancellationToken.None);

        var results = await store.ApplyAtomicBatchAsync(
            new[]
            {
                MemoryMutation.Upsert(
                    Record("new", """{"version":2}""")),
                MemoryMutation.Delete("old")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal(MemoryMutationKind.Upsert, result.Kind);
                Assert.Equal("new", result.MemoryId);
                Assert.True(result.Changed);
            },
            result =>
            {
                Assert.Equal(MemoryMutationKind.Delete, result.Kind);
                Assert.Equal("old", result.MemoryId);
                Assert.True(result.Changed);
            });
        Assert.Equal(
            new[] { "new" },
            (await SearchAllAsync(store))
            .Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task DuplicateIdIsRejectedBeforeAnyMutation()
    {
        IAtomicMemoryBatchStore store =
            new DeterministicMemoryStore(capacity: 2);
        await store.UpsertAsync(
            Record("existing", """{"version":1}"""),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<MemoryBatchValidationException>(
            () => store.ApplyAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("duplicate", """{"version":1}""")),
                        MemoryMutation.Delete("duplicate")
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(
            MemoryBatchReasonCodes.DuplicateMemoryId,
            error.ReasonCode);
        Assert.Equal(1, error.MutationIndex);
        Assert.Equal("duplicate", error.MemoryId);
        Assert.Equal(
            new[] { "existing" },
            (await SearchAllAsync(store))
            .Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task CancellationLeavesThePriorStateUntouched()
    {
        IAtomicMemoryBatchStore store =
            new DeterministicMemoryStore();
        await store.UpsertAsync(
            Record("existing", """{"version":1}"""),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ApplyAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Delete("existing"),
                        MemoryMutation.Upsert(
                            Record("new", """{"version":2}"""))
                    },
                    cancellation.Token)
                .AsTask());

        Assert.Equal(
            new[] { "existing" },
            (await SearchAllAsync(store))
            .Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task CapacityFailureDoesNotApplyEarlierBatchMembers()
    {
        IAtomicMemoryBatchStore store =
            new DeterministicMemoryStore(capacity: 1);
        await store.UpsertAsync(
            Record("existing", """{"version":1}"""),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => store.ApplyAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("new-a", """{"version":2}""")),
                        MemoryMutation.Delete("existing"),
                        MemoryMutation.Upsert(
                            Record("new-b", """{"version":3}"""))
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal("memory_capacity_exceeded", error.LimitCode);
        Assert.Equal(
            new[] { "existing" },
            (await SearchAllAsync(store))
            .Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task InvalidBatchShapeHasStableFailureCodes()
    {
        IAtomicMemoryBatchStore store = new DeterministicMemoryStore();

        var empty = await Assert.ThrowsAsync<MemoryBatchValidationException>(
            () => store.ApplyAtomicBatchAsync(
                    Array.Empty<MemoryMutation>(), cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
        Assert.Equal(MemoryBatchReasonCodes.Empty, empty.ReasonCode);

        var withNull =
            await Assert.ThrowsAsync<MemoryBatchValidationException>(
                () => store.ApplyAtomicBatchAsync(
                        new MemoryMutation[] { null! }, cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.NullMutation,
            withNull.ReasonCode);
        Assert.Equal(0, withNull.MutationIndex);
    }

    [Fact]
    public async Task BatchResourceBoundsHaveStableFailureCodes()
    {
        IAtomicMemoryBatchStore store = new DeterministicMemoryStore();
        var exact = Enumerable.Range(0, MemoryBatchLimits.MaxMutations)
            .Select(index => MemoryMutation.Delete($"missing-{index}"))
            .ToArray();
        var exactResults = await store.ApplyAtomicBatchAsync(exact, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(MemoryBatchLimits.MaxMutations, exactResults.Count);
        Assert.All(exactResults, result => Assert.False(result.Changed));

        var tooMany = Enumerable
            .Repeat(
                MemoryMutation.Delete("missing"),
                MemoryBatchLimits.MaxMutations + 1)
            .ToArray();

        var countError =
            await Assert.ThrowsAsync<MemoryBatchValidationException>(
                () => store.ApplyAtomicBatchAsync(tooMany, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.TooManyMutations,
            countError.ReasonCode);

        var content = Json(
            $$"""{"first":"{{new string('a', 60_000)}}","second":"{{new string('b', 60_000)}}"}""");
        var timestamp = DateTimeOffset.UnixEpoch;
        var large = Enumerable.Range(0, 70)
            .Select(
                index => MemoryMutation.Upsert(
                    new MemoryRecord(
                        $"large-{index}",
                        "shared",
                        content,
                        Array.Empty<string>(),
                        50,
                        timestamp,
                        timestamp)))
            .ToArray();

        var bytesError =
            await Assert.ThrowsAsync<MemoryBatchValidationException>(
                () => store.ApplyAtomicBatchAsync(large, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.AggregateContentBytesExceeded,
            bytesError.ReasonCode);
        Assert.Empty(await SearchAllAsync(store));
    }

    [Theory]
    [InlineData("deterministic")]
    [InlineData("bm25")]
    [InlineData("vector")]
    public void BuiltInStoresAdvertiseCurrentRuntimeMutationContract(
        string storeKind)
    {
        var store = Assert.IsAssignableFrom<
            IRuntimeAuthoritativeMemoryBatchStore>(Store(storeKind));

        Assert.Equal(
            RuntimeMemoryMutationContract.CurrentVersion,
            store.RuntimeMutationContractVersion);
    }

    [Theory]
    [InlineData("deterministic")]
    [InlineData("bm25")]
    [InlineData("vector")]
    public async Task BuiltInStoresRejectSameIdAcrossWorldOrScope(
        string storeKind)
    {
        var store = Store(storeKind);
        var original = BoundRecord(
            "shared-id",
            "npc:npc-1",
            "world-a",
            "save-a",
            "original");
        await store.UpsertAsync(original, CancellationToken.None);

        var worldConflict = await Assert.ThrowsAsync<
            MemoryMutationConflictException>(
            () => store.UpsertAsync(
                    BoundRecord(
                        "shared-id",
                        "npc:npc-1",
                        "world-b",
                        "save-a",
                        "other world"),
                    CancellationToken.None)
                .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.NamespaceConflict,
            worldConflict.ReasonCode);

        var scopeConflict = await Assert.ThrowsAsync<
            MemoryMutationConflictException>(
            () => store.UpsertAsync(
                    BoundRecord(
                        "shared-id",
                        "npc:npc-2",
                        "world-a",
                        "save-a",
                        "other scope"),
                    CancellationToken.None)
                .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.NamespaceConflict,
            scopeConflict.ReasonCode);

        var saveConflict = await Assert.ThrowsAsync<
            MemoryMutationConflictException>(
            () => store.UpsertAsync(
                    BoundRecord(
                        "shared-id",
                        "npc:npc-1",
                        "world-a",
                        "save-b",
                        "other save"),
                    CancellationToken.None)
                .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.NamespaceConflict,
            saveConflict.ReasonCode);

        var retained = Assert.Single(
            await store.SearchAsync(
                new MemoryQuery(
                    original.Scope,
                    Json("{}"),
                    worldId: "world-a",
                    sessionId: "save-a",
                    requireCommittedProvenance: true),
                CancellationToken.None));
        Assert.Contains(
            "original",
            retained.Record.Content.GetRawText(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deterministic")]
    [InlineData("bm25")]
    [InlineData("vector")]
    public async Task BuiltInStoresRequireExactRecordForGuardedDelete(
        string storeKind)
    {
        var store = Store(storeKind);
        var original = BoundRecord(
            "shared-id",
            "npc:npc-1",
            "world-a",
            "save-a",
            "original");
        await store.UpsertAsync(original, CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<
            MemoryMutationConflictException>(
            () => store.ApplyAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Delete(
                            BoundRecord(
                                "shared-id",
                                "npc:npc-1",
                                "world-b",
                                "save-a",
                                "original"))
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.PreconditionFailed,
            conflict.ReasonCode);

        var stale = await Assert.ThrowsAsync<
            MemoryMutationConflictException>(
            () => store.ApplyAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Delete(
                            BoundRecord(
                                "shared-id",
                                "npc:npc-1",
                                "world-a",
                                "save-a",
                                "stale content"))
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.PreconditionFailed,
            stale.ReasonCode);

        var deleted = await store.ApplyAtomicBatchAsync(
            new[] { MemoryMutation.Delete(original) }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(deleted).Changed);
        var replayed = await store.ApplyAtomicBatchAsync(
            new[] { MemoryMutation.Delete(original) }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(replayed).Changed);
    }

    [Theory]
    [InlineData("deterministic")]
    [InlineData("bm25")]
    [InlineData("vector")]
    public async Task RuntimeStyleBatchesTreatBareUpsertAsCreateOnly(
        string storeKind)
    {
        var store = (IIdempotentAtomicMemoryBatchStore)Store(storeKind);
        var original = BoundRecord(
            "conditional-id",
            "npc:npc-1",
            "world-a",
            "save-a",
            "version one");
        var replacement = BoundRecord(
            "conditional-id",
            "npc:npc-1",
            "world-a",
            "save-a",
            "version two");
        await store.UpsertAsync(original, CancellationToken.None);

        var unguarded = await Assert.ThrowsAsync<
            MemoryMutationConflictException>(
            () => store.ApplyIdempotentAtomicBatchAsync(
                    "unguarded-replace-" + storeKind,
                    new[] { MemoryMutation.Upsert(replacement) }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.PreconditionFailed,
            unguarded.ReasonCode);

        var replaced = await store.ApplyIdempotentAtomicBatchAsync(
            "guarded-replace-" + storeKind,
            new[] { MemoryMutation.Upsert(replacement, original) }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(replaced).Changed);

        var stale = await Assert.ThrowsAsync<MemoryMutationConflictException>(
            () => store.ApplyIdempotentAtomicBatchAsync(
                    "stale-replace-" + storeKind,
                    new[]
                    {
                        MemoryMutation.Upsert(
                            BoundRecord(
                                "conditional-id",
                                "npc:npc-1",
                                "world-a",
                                "save-a",
                                "version three"),
                            original)
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.PreconditionFailed,
            stale.ReasonCode);
    }

    [Theory]
    [InlineData("deterministic", "timeline")]
    [InlineData("deterministic", "epoch")]
    [InlineData("deterministic", "observer")]
    [InlineData("deterministic", "observer_incarnation")]
    [InlineData("deterministic", "source_incarnation")]
    [InlineData("deterministic", "perspective_kind")]
    [InlineData("deterministic", "game_clock")]
    [InlineData("deterministic", "game_timeline")]
    [InlineData("deterministic", "game_epoch")]
    [InlineData("bm25", "timeline")]
    [InlineData("bm25", "observer_incarnation")]
    [InlineData("bm25", "source_incarnation")]
    [InlineData("bm25", "game_epoch")]
    [InlineData("vector", "timeline")]
    [InlineData("vector", "observer_incarnation")]
    [InlineData("vector", "source_incarnation")]
    [InlineData("vector", "game_epoch")]
    public async Task BuiltInStoresIsolateGameSemanticAuthorities(
        string storeKind,
        string boundary)
    {
        var store = Store(storeKind);
        var original = SemanticRecord("semantic-id");
        var foreign = boundary switch
        {
            "timeline" => SemanticRecord(
                "semantic-id",
                timelineId: "timeline-fork",
                gameTimeTimelineId: "timeline-fork"),
            "epoch" => SemanticRecord(
                "semantic-id",
                timelineEpoch: 3,
                gameTimeEpoch: 3),
            "observer" => SemanticRecord(
                "semantic-id",
                observerEntityId: "npc-9"),
            "observer_incarnation" => SemanticRecord(
                "semantic-id",
                observerIncarnation: 3),
            "source_incarnation" => SemanticRecord(
                "semantic-id",
                sourceIncarnation: 4),
            "perspective_kind" => SemanticRecord(
                "semantic-id",
                perspectiveKind: "rumor"),
            "game_clock" => SemanticRecord(
                "semantic-id",
                gameTimeClockId: "dream-clock"),
            "game_timeline" => SemanticRecord(
                "semantic-id",
                timelineId: "timeline-fork",
                gameTimeTimelineId: "timeline-fork"),
            "game_epoch" => SemanticRecord(
                "semantic-id",
                gameTimeEpoch: 3),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        await store.UpsertAsync(original, CancellationToken.None);

        var overwrite = await Assert.ThrowsAsync<
            MemoryMutationConflictException>(
            () => store.UpsertAsync(foreign, CancellationToken.None)
                .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.NamespaceConflict,
            overwrite.ReasonCode);

        var delete = await Assert.ThrowsAsync<MemoryMutationConflictException>(
            () => store.ApplyAtomicBatchAsync(
                    new[] { MemoryMutation.Delete(foreign) }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.PreconditionFailed,
            delete.ReasonCode);

        var retained = await store.ApplyAtomicBatchAsync(
            new[] { MemoryMutation.Delete(original) }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(retained).Changed);
    }

    private static async Task<IReadOnlyList<MemorySearchResult>>
        SearchAllAsync(IMemoryProvider store)
    {
        return await store.SearchAsync(
            new MemoryQuery(
                "shared",
                Json("{}"),
                maxResults: 128,
                maxUtf8Bytes: 1_048_576),
            CancellationToken.None);
    }

    private static MemoryRecord Record(string id, string content)
    {
        return new MemoryRecord(
            id,
            "shared",
            Json(content),
            Array.Empty<string>(),
            50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static MemoryRecord BoundRecord(
        string id,
        string scope,
        string worldId,
        string? sessionId,
        string text)
    {
        return new MemoryRecord(
            id,
            scope,
            Json($$"""{"text":"{{text}}"}"""),
            Array.Empty<string>(),
            50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                worldId,
                sessionId,
                saveRevision: 1,
                sourceRunId: "source-run",
                sourceEventId: "source-event",
                committed: true));
    }

    private static MemoryRecord SemanticRecord(
        string id,
        string timelineId = "timeline-main",
        long timelineEpoch = 2,
        string observerEntityId = "npc-1",
        long observerIncarnation = 2,
        string perspectiveKind = "observation",
        long sourceIncarnation = 3,
        string gameTimeClockId = "world-clock",
        string gameTimeTimelineId = "timeline-main",
        long gameTimeEpoch = 2)
    {
        return new MemoryRecord(
            id,
            "npc:npc-1",
            Json("""{"text":"semantic memory"}"""),
            Array.Empty<string>(),
            50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                "world-a",
                "save-a",
                saveRevision: 5,
                sourceRunId: "source-run",
                sourceEventId: "source-event",
                committed: true,
                timelineId,
                new GameKnowledgePerspective(
                    new GameEntityIdentity(
                        observerEntityId,
                        observerIncarnation),
                    perspectiveKind,
                    new GameEntityIdentity("npc-2", sourceIncarnation)),
                timelineEpoch),
            gameTimeWindow: new GameTimeWindow(
                validFrom: new GameTimePoint(
                    gameTimeClockId,
                    gameTimeTimelineId,
                    gameTimeEpoch,
                    tick: 10)));
    }

    private static IAtomicMemoryBatchStore Store(string kind)
    {
        return kind switch
        {
            "deterministic" => new DeterministicMemoryStore(),
            "bm25" => new Bm25MemoryStore(),
            "vector" => new VectorMemoryStore(new FixedEmbeddingProvider()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private sealed class FixedEmbeddingProvider : IMemoryEmbeddingProvider
    {
        public string ProviderId => "fixed-embedding";

        public string ModelId => "fixed-test";

        public string Version => "1";

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

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}

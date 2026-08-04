using System.Collections;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class Bm25MemoryStoreTests
{
    [Fact]
    public void UnicodeTokenizerNormalizesEnglishAndBuildsCjkBigrams()
    {
        var tokenizer = new DeterministicUnicodeTokenizer();

        var terms = tokenizer.Tokenize("ＦＯＯ 北桥天气");

        Assert.Contains("foo", terms);
        Assert.Contains("北", terms);
        Assert.Contains("北桥", terms);
        Assert.Contains("桥天", terms);
        Assert.Contains("天气", terms);
    }

    [Fact]
    public async Task EnglishFrequencyRanksRelevantDocumentAndTiesAreStable()
    {
        var first = new Bm25MemoryStore();
        var second = new Bm25MemoryStore();
        var records = new[]
        {
            Record("b", "\"apple orchard\"", importance: 10),
            Record("a", "\"apple orchard\"", importance: 10),
            Record(
                "frequent",
                "\"apple apple apple orchard\"",
                importance: 10),
            Record(
                "diluted",
                "\"apple river mountain village road\"",
                importance: 10)
        };
        foreach (var record in records)
        {
            await first.UpsertAsync(record, TestContext.Current.CancellationToken);
        }

        foreach (var record in records.Reverse())
        {
            await second.UpsertAsync(record, TestContext.Current.CancellationToken);
        }

        var firstResults = await Search(first, "\"apple\"");
        var secondResults = await Search(second, "\"apple\"");

        Assert.Equal("frequent", firstResults[0].Record.MemoryId);
        Assert.True(
            firstResults[0].Score > firstResults[^1].Score);
        Assert.Equal(
            firstResults.Select(ResultIdentity),
            secondResults.Select(ResultIdentity));
        Assert.True(
            Array.IndexOf(
                firstResults.Select(
                        item => item.Record.MemoryId)
                    .ToArray(),
                "a")
            < Array.IndexOf(
                firstResults.Select(
                        item => item.Record.MemoryId)
                    .ToArray(),
                "b"));
    }

    [Fact]
    public async Task CjkUpdateAndDeleteKeepIndexConsistent()
    {
        var store = new Bm25MemoryStore();
        await store.UpsertAsync(
            Record("bridge", "\"北桥已经关闭\""),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            Record("gate", "\"南门仍然开放\""),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "bridge",
            Assert.Single(await Search(store, "\"北桥\""))
                .Record.MemoryId);

        await store.UpsertAsync(
            Record("bridge", "\"东港已经开放\""),
            TestContext.Current.CancellationToken);
        Assert.Empty(await Search(store, "\"北桥\""));
        Assert.Equal(
            "bridge",
            Assert.Single(await Search(store, "\"东港\""))
                .Record.MemoryId);

        Assert.True(await store.DeleteAsync("bridge", TestContext.Current.CancellationToken));
        Assert.Empty(await Search(store, "\"东港\""));
    }

    [Fact]
    public async Task EveryGameVisibilityFilterIsAppliedBeforeRanking()
    {
        var store = new Bm25MemoryStore(capacity: 16);
        var observer = new GameEntityIdentity("npc", 1);
        await store.UpsertAsync(
            FilteredRecord(
                "match",
                observer,
                "world",
                "session",
                saveRevision: 4,
                committed: true,
                "timeline",
                fromTick: 5,
                untilTick: 15),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            FilteredRecord(
                "wrong-world",
                observer,
                "other",
                "session",
                4,
                true,
                "timeline",
                5,
                15),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            FilteredRecord(
                "wrong-session",
                observer,
                "world",
                "other",
                4,
                true,
                "timeline",
                5,
                15),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            FilteredRecord(
                "future-save",
                observer,
                "world",
                "session",
                5,
                true,
                "timeline",
                5,
                15),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            FilteredRecord(
                "uncommitted",
                observer,
                "world",
                "session",
                4,
                false,
                "timeline",
                5,
                15),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            FilteredRecord(
                "wrong-timeline",
                observer,
                "world",
                "session",
                4,
                true,
                "other-timeline",
                5,
                15),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            FilteredRecord(
                "wrong-incarnation",
                new GameEntityIdentity("npc", 2),
                "world",
                "session",
                4,
                true,
                "timeline",
                5,
                15),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            FilteredRecord(
                "outside-game-time",
                observer,
                "world",
                "session",
                4,
                true,
                "timeline",
                20,
                30),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            FilteredRecord(
                "wrong-scope",
                observer,
                "world",
                "session",
                4,
                true,
                "timeline",
                5,
                15,
                scope: "other-scope"),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            FilteredRecord(
                "missing-tag",
                observer,
                "world",
                "session",
                4,
                true,
                "timeline",
                5,
                15,
                tags: Array.Empty<string>()),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            new MemoryRecord(
                "expired",
                "scope",
                Json("\"signal\""),
                new[] { "required" },
                50,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                new MemoryProvenance(
                    "world",
                    "session",
                    4,
                    "run",
                    "event",
                    true,
                    "timeline",
                    new GameKnowledgePerspective(
                        observer,
                        "observed")),
                new GameTimeWindow(
                    new GameTimePoint(
                        "clock",
                        "timeline",
                        0,
                        5),
                    new GameTimePoint(
                        "clock",
                        "timeline",
                        0,
                        15))),
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json("\"signal\""),
                requiredTags: new[] { "required" },
                now: DateTimeOffset.UnixEpoch.AddDays(1),
                worldId: "world",
                sessionId: "session",
                maximumSaveRevision: 4,
                requireCommittedProvenance: true,
                timelineId: "timeline",
                observer: observer,
                gameTime: new GameTimePoint(
                    "clock",
                    "timeline",
                    0,
                    10)),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "match" },
            results.Select(item => item.Record.MemoryId));

        var privileged = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json("\"signal\""),
                requiredTags: new[] { "required" },
                now: DateTimeOffset.UnixEpoch.AddDays(1),
                worldId: "world",
                sessionId: "session",
                maximumSaveRevision: 4,
                requireCommittedProvenance: true,
                timelineId: "timeline",
                gameTime: new GameTimePoint(
                    "clock",
                    "timeline",
                    0,
                    10),
                includeAllPerspectives: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            new[] { "match", "wrong-incarnation" },
            privileged.Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task AtomicAndIdempotentBatchesRollbackAndDeduplicate()
    {
        var store = new Bm25MemoryStore(capacity: 1);
        await store.UpsertAsync(Record("old", "\"old\""), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => store.ApplyAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("new-a", "\"new\"")),
                        MemoryMutation.Upsert(
                            Record("new-b", "\"new\"")),
                        MemoryMutation.Delete("old")
                    }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
        Assert.Equal(
            "old",
            Assert.Single(await Search(store, "\"old\""))
                .Record.MemoryId);

        var mutations = new[]
        {
            MemoryMutation.Delete("old"),
            MemoryMutation.Upsert(Record("new", "\"new\""))
        };
        var first = await store.ApplyIdempotentAtomicBatchAsync(
            "commit-1",
            mutations, cancellationToken: TestContext.Current.CancellationToken);
        var duplicate = await store.ApplyIdempotentAtomicBatchAsync(
            "commit-1",
            mutations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(first, result => Assert.True(result.Changed));
        Assert.All(duplicate, result => Assert.False(result.Changed));
        Assert.Equal(
            "new",
            Assert.Single(await Search(store, "\"new\""))
                .Record.MemoryId);
        await Assert.ThrowsAsync<MemoryBatchIdempotencyConflictException>(
            () => store.ApplyIdempotentAtomicBatchAsync(
                    "commit-1",
                    new[] { MemoryMutation.Delete("new") }, cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
    }

    [Fact]
    public async Task LimitsAndLyingCollectionsFailClosed()
    {
        var tokenizer = new DeterministicUnicodeTokenizer(
            new DeterministicUnicodeTokenizerLimits(
                maxInputUtf8Bytes: 8,
                maxTextSegments: 1,
                maxTerms: 2,
                maxUniqueTerms: 2,
                maxTermUtf8Bytes: 4));
        var bytes = Assert.Throws<LexicalSearchLimitException>(
            () => tokenizer.Tokenize("123456789"));
        Assert.Equal(
            LexicalSearchReasonCodes.InputBytesExceeded,
            bytes.ReasonCode);
        var terms = Assert.Throws<LexicalSearchLimitException>(
            () => tokenizer.Tokenize("a b c"));
        Assert.Equal(
            LexicalSearchReasonCodes.TermsExceeded,
            terms.ReasonCode);

        var queryBound = new Bm25MemoryStore(
            options: new Bm25MemoryStoreOptions(
                maxQueryUtf8Bytes: 4));
        var queryBytes =
            await Assert.ThrowsAsync<LexicalSearchLimitException>(
                () => Search(queryBound, "\"term\""));
        Assert.Equal(
            LexicalSearchReasonCodes.QueryBytesExceeded,
            queryBytes.ReasonCode);

        var indexBound = new Bm25MemoryStore(
            options: new Bm25MemoryStoreOptions(maxIndexTerms: 1));
        var indexTerms =
            await Assert.ThrowsAsync<LexicalSearchLimitException>(
                () => indexBound.UpsertAsync(
                        Record("too-many-terms", "\"a b\""),
                        TestContext.Current.CancellationToken)
                    .AsTask());
        Assert.Equal(
            LexicalSearchReasonCodes.IndexTermsExceeded,
            indexTerms.ReasonCode);

        var bounded = new Bm25MemoryStore(
            options: new Bm25MemoryStoreOptions(
                maxComparisonsPerSearch: 1));
        await bounded.UpsertAsync(Record("a", "\"term\""), TestContext.Current.CancellationToken);
        await bounded.UpsertAsync(Record("b", "\"term\""), TestContext.Current.CancellationToken);
        var comparisons =
            await Assert.ThrowsAsync<LexicalSearchLimitException>(
                () => Search(bounded, "\"term\""));
        Assert.Equal(
            LexicalSearchReasonCodes.ComparisonsExceeded,
            comparisons.ReasonCode);

        IAtomicMemoryBatchStore batches = new Bm25MemoryStore();
        var lying = new LyingMutationList(
            Enumerable.Range(
                    0,
                    MemoryBatchLimits.MaxMutations + 1)
                .Select(
                    index => MemoryMutation.Delete(
                        "missing-" + index)));
        var count =
            await Assert.ThrowsAsync<MemoryBatchValidationException>(
                () => batches.ApplyAtomicBatchAsync(lying, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.TooManyMutations,
            count.ReasonCode);

        var scorer = new DeterministicBm25Scorer(
            maxFieldsPerTerm: 2);
        Assert.Throws<LexicalSearchLimitException>(
            () => scorer.ScoreTerm(
                documentCount: 1,
                documentFrequency: 1,
                new LyingFieldCollection(
                    Enumerable.Repeat(
                        new Bm25FieldMatch(1, 1, 1),
                        3))));
        Assert.Throws<ArgumentException>(
            () => scorer.ScoreTerm(
                documentCount: 1,
                documentFrequency: 1,
                new[] { default(Bm25FieldMatch) }));
    }

    private static async Task<MemorySearchResult[]> Search(
        IMemoryProvider store,
        string query)
    {
        var results = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json(query),
                maxResults: 128,
                maxUtf8Bytes: 1_048_576),
            default);
        return results.ToArray();
    }

    private static string ResultIdentity(MemorySearchResult result)
    {
        return result.Record.MemoryId + ":" + result.Score;
    }

    private static MemoryRecord Record(
        string id,
        string content,
        int importance = 50)
    {
        return new MemoryRecord(
            id,
            "scope",
            Json(content),
            Array.Empty<string>(),
            importance,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static MemoryRecord FilteredRecord(
        string id,
        GameEntityIdentity observer,
        string world,
        string session,
        long saveRevision,
        bool committed,
        string timeline,
        long fromTick,
        long untilTick,
        string scope = "scope",
        IEnumerable<string>? tags = null)
    {
        return new MemoryRecord(
            id,
            scope,
            Json("\"signal\""),
            tags ?? new[] { "required" },
            50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                world,
                session,
                saveRevision,
                "run",
                "event-" + id,
                committed,
                timeline,
                new GameKnowledgePerspective(
                    observer,
                    "observed")),
            gameTimeWindow: new GameTimeWindow(
                new GameTimePoint(
                    "clock",
                    timeline,
                    0,
                    fromTick),
                new GameTimePoint(
                    "clock",
                    timeline,
                    0,
                    untilTick)));
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class LyingMutationList :
        IReadOnlyList<MemoryMutation>
    {
        private readonly IEnumerable<MemoryMutation> _values;

        public LyingMutationList(IEnumerable<MemoryMutation> values)
        {
            _values = values;
        }

        public int Count => 1;

        public MemoryMutation this[int index] =>
            throw new InvalidOperationException(
                "The validator must enumerate its owned snapshot.");

        public IEnumerator<MemoryMutation> GetEnumerator() =>
            _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class LyingFieldCollection :
        IReadOnlyCollection<Bm25FieldMatch>
    {
        private readonly IEnumerable<Bm25FieldMatch> _values;

        public LyingFieldCollection(
            IEnumerable<Bm25FieldMatch> values)
        {
            _values = values;
        }

        public int Count => 0;

        public IEnumerator<Bm25FieldMatch> GetEnumerator() =>
            _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class MemoryTests
{
    [Fact]
    public async Task StructuredJsonQueriesFindScopedMemoryWithoutEmbeddings()
    {
        var store = new DeterministicMemoryStore(capacity: 4);
        await store.UpsertAsync(
            Record(
                "m-1",
                "world:w-1/agent:a-1",
                """{"npcId":"lin","faction":"river","favor":12}""",
                new[] { "relationship" },
                importance: 40),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            Record(
                "m-2",
                "world:w-1/agent:a-1",
                """{"npcId":"zhou","faction":"mountain","favor":80}""",
                new[] { "relationship" },
                importance: 90),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            Record(
                "m-3",
                "world:w-2/agent:a-1",
                """{"npcId":"lin","faction":"river"}""",
                new[] { "relationship" },
                importance: 100),
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new MemoryQuery(
                "world:w-1/agent:a-1",
                Json("""{"npcId":"lin","faction":"river"}"""),
                requiredTags: new[] { "relationship" },
                maxResults: 1),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("m-1", result.Record.MemoryId);
    }

    [Fact]
    public async Task CjkSubstringQueryRecallsLongerChineseMemory()
    {
        var store = new DeterministicMemoryStore(capacity: 2);
        await store.UpsertAsync(
            Record(
                "north-bridge",
                "world:w-1/agent:a-1",
                """{"description":"北桥已经关闭，禁止通行。"}""",
                Array.Empty<string>(),
                importance: 50),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            Record(
                "south-gate",
                "world:w-1/agent:a-1",
                """{"description":"南门仍然开放。"}""",
                Array.Empty<string>(),
                importance: 50),
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new MemoryQuery(
                "world:w-1/agent:a-1",
                Json("""{"search":"北桥"}""")),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "north-bridge" },
            results.Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task SearchIsBoundedStableAndFiltersExpiredRecords()
    {
        var store = new DeterministicMemoryStore(capacity: 3);
        await store.UpsertAsync(
            Record(
                "b",
                "scope",
                """{"event":"fire"}""",
                Array.Empty<string>(),
                importance: 5),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            Record(
                "a",
                "scope",
                """{"event":"fire"}""",
                Array.Empty<string>(),
                importance: 5),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            new MemoryRecord(
                "expired",
                "scope",
                Json("""{"event":"fire"}"""),
                Array.Empty<string>(),
                100,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(1)),
            TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json("""{"event":"fire"}"""),
                maxResults: 1,
                now: DateTimeOffset.UnixEpoch.AddDays(1)),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("a", result.Record.MemoryId);
    }

    [Fact]
    public async Task PerspectivalMemoryRequiresObserverOrPrivilegedQuery()
    {
        var store = new DeterministicMemoryStore();
        var observer = new GameEntityIdentity("npc", 1);
        await store.UpsertAsync(
            Record(
                "general",
                "scope",
                """{"event":"meeting"}""",
                Array.Empty<string>(),
                importance: 50),
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            Record(
                "private",
                "scope",
                """{"event":"meeting"}""",
                Array.Empty<string>(),
                importance: 50,
                provenance: new MemoryProvenance(
                    "world",
                    "session",
                    1,
                    "run",
                    "event",
                    committed: true,
                    perspective: new GameKnowledgePerspective(
                        observer,
                        "witnessed"))),
            TestContext.Current.CancellationToken);

        var defaultResults = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json("""{"event":"meeting"}""")),
            TestContext.Current.CancellationToken);
        var observerResults = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json("""{"event":"meeting"}"""),
                observer: observer),
            TestContext.Current.CancellationToken);
        var otherObserverResults = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json("""{"event":"meeting"}"""),
                observer: new GameEntityIdentity("npc", 2)),
            TestContext.Current.CancellationToken);
        var privilegedResults = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json("""{"event":"meeting"}"""),
                includeAllPerspectives: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "general" },
            defaultResults.Select(item => item.Record.MemoryId));
        Assert.Equal(
            new[] { "general", "private" },
            observerResults.Select(item => item.Record.MemoryId));
        Assert.Equal(
            new[] { "general" },
            otherObserverResults.Select(item => item.Record.MemoryId));
        Assert.Equal(
            new[] { "general", "private" },
            privilegedResults.Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task GameTimeImplicitlyScopesTimelessMemoryToItsTimeline()
    {
        var store = new DeterministicMemoryStore();
        foreach (var timeline in new[] { "branch-a", "branch-b" })
        {
            await store.UpsertAsync(
                Record(
                    timeline,
                    "scope",
                    """{"event":"meeting"}""",
                    Array.Empty<string>(),
                    importance: 50,
                    provenance: new MemoryProvenance(
                        "world",
                        "session",
                        1,
                        "run",
                        "event",
                        committed: true,
                        timelineId: timeline)),
                TestContext.Current.CancellationToken);
        }

        var results = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json("""{"event":"meeting"}"""),
                gameTime: new GameTimePoint(
                    "simulation",
                    "branch-a",
                    epoch: 0,
                    tick: 10)),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "branch-a" },
            results.Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task CapacityIsFailClosedAndDeleteReleasesSpace()
    {
        var store = new DeterministicMemoryStore(capacity: 1);
        await store.UpsertAsync(
            Record(
                "m-1",
                "scope",
                """{"value":1}""",
                Array.Empty<string>(),
                importance: 0),
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            async () => await store.UpsertAsync(
                Record(
                    "m-2",
                    "scope",
                    """{"value":2}""",
                    Array.Empty<string>(),
                    importance: 0),
                TestContext.Current.CancellationToken));
        Assert.Equal("memory_capacity_exceeded", error.LimitCode);

        Assert.True(await store.DeleteAsync("m-1", TestContext.Current.CancellationToken));
        await store.UpsertAsync(
            Record(
                "m-2",
                "scope",
                """{"value":2}""",
                Array.Empty<string>(),
                importance: 0),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExternalProvidersCanConstructSearchResults()
    {
        IMemoryProvider provider = new CustomMemoryProvider(
            new MemorySearchResult(
                Record(
                    "custom-1",
                    "scope",
                    """{"fact":"bridge"}""",
                    new[] { "custom" },
                    importance: 50),
                score: 900));

        var results = await provider.SearchAsync(
            new MemoryQuery("scope", Json("""{"fact":"bridge"}""")),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("custom-1", result.Record.MemoryId);
        Assert.Equal(900, result.Score);
    }

    private static MemoryRecord Record(
        string id,
        string scope,
        string json,
        IReadOnlyList<string> tags,
        int importance,
        MemoryProvenance? provenance = null)
    {
        return new MemoryRecord(
            id,
            scope,
            Json(json),
            tags,
            importance,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: provenance);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class CustomMemoryProvider : IMemoryProvider
    {
        private readonly MemorySearchResult _result;

        public CustomMemoryProvider(MemorySearchResult result)
        {
            _result = result;
        }

        public string ProviderId => "custom";

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<MemorySearchResult>>(
                new[] { _result });
        }
    }
}

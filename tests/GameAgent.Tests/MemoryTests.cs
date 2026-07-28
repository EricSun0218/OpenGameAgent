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
            default);
        await store.UpsertAsync(
            Record(
                "m-2",
                "world:w-1/agent:a-1",
                """{"npcId":"zhou","faction":"mountain","favor":80}""",
                new[] { "relationship" },
                importance: 90),
            default);
        await store.UpsertAsync(
            Record(
                "m-3",
                "world:w-2/agent:a-1",
                """{"npcId":"lin","faction":"river"}""",
                new[] { "relationship" },
                importance: 100),
            default);

        var results = await store.SearchAsync(
            new MemoryQuery(
                "world:w-1/agent:a-1",
                Json("""{"npcId":"lin","faction":"river"}"""),
                requiredTags: new[] { "relationship" },
                maxResults: 1),
            default);

        var result = Assert.Single(results);
        Assert.Equal("m-1", result.Record.MemoryId);
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
            default);
        await store.UpsertAsync(
            Record(
                "a",
                "scope",
                """{"event":"fire"}""",
                Array.Empty<string>(),
                importance: 5),
            default);
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
            default);

        var results = await store.SearchAsync(
            new MemoryQuery(
                "scope",
                Json("""{"event":"fire"}"""),
                maxResults: 1,
                now: DateTimeOffset.UnixEpoch.AddDays(1)),
            default);

        var result = Assert.Single(results);
        Assert.Equal("a", result.Record.MemoryId);
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
            default);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            async () => await store.UpsertAsync(
                Record(
                    "m-2",
                    "scope",
                    """{"value":2}""",
                    Array.Empty<string>(),
                    importance: 0),
                default));
        Assert.Equal("memory_capacity_exceeded", error.LimitCode);

        Assert.True(await store.DeleteAsync("m-1", default));
        await store.UpsertAsync(
            Record(
                "m-2",
                "scope",
                """{"value":2}""",
                Array.Empty<string>(),
                importance: 0),
            default);
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
            default);

        var result = Assert.Single(results);
        Assert.Equal("custom-1", result.Record.MemoryId);
        Assert.Equal(900, result.Score);
    }

    private static MemoryRecord Record(
        string id,
        string scope,
        string json,
        IReadOnlyList<string> tags,
        int importance)
    {
        return new MemoryRecord(
            id,
            scope,
            Json(json),
            tags,
            importance,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
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

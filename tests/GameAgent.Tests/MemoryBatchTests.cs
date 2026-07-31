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
            });

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
                    })
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
                    })
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
                    Array.Empty<MemoryMutation>())
                .AsTask());
        Assert.Equal(MemoryBatchReasonCodes.Empty, empty.ReasonCode);

        var withNull =
            await Assert.ThrowsAsync<MemoryBatchValidationException>(
                () => store.ApplyAtomicBatchAsync(
                        new MemoryMutation[] { null! })
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
        var exactResults = await store.ApplyAtomicBatchAsync(exact);
        Assert.Equal(MemoryBatchLimits.MaxMutations, exactResults.Count);
        Assert.All(exactResults, result => Assert.False(result.Changed));

        var tooMany = Enumerable
            .Repeat(
                MemoryMutation.Delete("missing"),
                MemoryBatchLimits.MaxMutations + 1)
            .ToArray();

        var countError =
            await Assert.ThrowsAsync<MemoryBatchValidationException>(
                () => store.ApplyAtomicBatchAsync(tooMany).AsTask());
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
                () => store.ApplyAtomicBatchAsync(large).AsTask());
        Assert.Equal(
            MemoryBatchReasonCodes.AggregateContentBytesExceeded,
            bytesError.ReasonCode);
        Assert.Empty(await SearchAllAsync(store));
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

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}

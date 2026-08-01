using System.Collections;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class MemoryLifecycleBatchTests
{
    [Fact]
    public async Task LifecycleCommitsAValidatedAtomicBatch()
    {
        var store = new DeterministicMemoryStore();
        await store.UpsertAsync(
            Record("old", committed: true),
            CancellationToken.None);
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);

        var results = await lifecycle.CommitAtomicBatchAsync(
            new[]
            {
                MemoryMutation.Delete("old"),
                MemoryMutation.Upsert(
                    Record("new", committed: true))
            });

        Assert.All(results, result => Assert.True(result.Changed));
        Assert.Equal(
            new[] { "new" },
            (await SearchAllAsync(store))
            .Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task LifecycleRejectsUncommittedMemberBeforeWholeBatch()
    {
        var store = new DeterministicMemoryStore();
        await store.UpsertAsync(
            Record("old", committed: true),
            CancellationToken.None);
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.CommitAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Delete("old"),
                        MemoryMutation.Upsert(
                            Record("new", committed: false))
                    })
                .AsTask());

        Assert.Equal(
            new[] { "old" },
            (await SearchAllAsync(store))
            .Select(item => item.Record.MemoryId));
    }

    [Fact]
    public async Task LifecycleReportsSingleWriteOnlyStoreExplicitly()
    {
        var store = new SingleWriteMemoryStore();
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);

        var error =
            await Assert.ThrowsAsync<MemoryBatchNotSupportedException>(
                () => lifecycle.CommitAtomicBatchAsync(
                        new[]
                        {
                            MemoryMutation.Upsert(
                                Record("new", committed: true))
                        })
                    .AsTask());

        Assert.Equal(
            MemoryBatchReasonCodes.NotSupported,
            error.ReasonCode);
        Assert.Empty(await SearchAllAsync(store));
    }

    [Fact]
    public async Task LifecycleSnapshotsBatchResultsByIndexWithoutEnumeration()
    {
        var rawResults = new IndexedOnlyList<MemoryMutationResult>(
            new[]
            {
                new MemoryMutationResult(
                    MemoryMutationKind.Upsert,
                    "new",
                    changed: true)
            });
        var store = new AdversarialBatchStore(rawResults);
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);

        var results = await lifecycle.CommitAtomicBatchAsync(
            new[]
            {
                MemoryMutation.Upsert(Record("new", committed: true))
            });

        Assert.Single(results);
        Assert.True(results[0].Changed);
        Assert.Equal(1, rawResults.CountReads);
        Assert.False(rawResults.EnumeratorAccessed);
    }

    [Fact]
    public async Task LifecycleRejectsBatchResultCountMismatch()
    {
        var store = new AdversarialBatchStore(
            Array.Empty<MemoryMutationResult>());
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => lifecycle.CommitAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("new", committed: true))
                    })
                .AsTask());
    }

    [Theory]
    [InlineData(MemoryMutationKind.Delete, "new")]
    [InlineData(MemoryMutationKind.Upsert, "other")]
    public async Task LifecycleRejectsMismatchedBatchResultIdentity(
        MemoryMutationKind resultKind,
        string resultMemoryId)
    {
        var store = new AdversarialBatchStore(
            new[]
            {
                new MemoryMutationResult(
                    resultKind,
                    resultMemoryId,
                    changed: true)
            });
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => lifecycle.CommitAtomicBatchAsync(
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("new", committed: true))
                    })
                .AsTask());
    }

    [Fact]
    public async Task RuntimeWritebackRejectsLegacyIdempotentStoreContract()
    {
        var store = new LegacyIdempotentMemoryStore();
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);

        var error = await Assert.ThrowsAsync<
            MemoryRuntimeMutationContractNotSupportedException>(
            () => lifecycle.CommitIdempotentAtomicBatchAsync(
                    "legacy-store-commit",
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("new", committed: true))
                    })
                .AsTask());

        Assert.Equal(
            MemoryBatchReasonCodes.RuntimeMutationContractNotSupported,
            error.ReasonCode);
        Assert.Equal(0, store.ApplyCount);
    }

    [Fact]
    public async Task RuntimeWritebackRejectsUnknownMutationContractVersion()
    {
        var store = new VersionedMemoryStore(version: 999);
        await using var lifecycle = new RuntimeMemoryLifecycle(
            new IMemoryProvider[] { store },
            store);

        var error = await Assert.ThrowsAsync<
            MemoryRuntimeMutationContractNotSupportedException>(
            () => lifecycle.CommitIdempotentAtomicBatchAsync(
                    "future-store-commit",
                    new[]
                    {
                        MemoryMutation.Upsert(
                            Record("new", committed: true))
                    })
                .AsTask());

        Assert.Equal(999, error.AdvertisedVersion);
        Assert.Equal(0, store.ApplyCount);
    }

    private static MemoryRecord Record(string id, bool committed)
    {
        return new MemoryRecord(
            id,
            "shared",
            Json("""{"fact":"bridge"}"""),
            Array.Empty<string>(),
            50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            provenance: new MemoryProvenance(
                "world",
                "session",
                1,
                "run",
                "event",
                committed));
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

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class SingleWriteMemoryStore : IMemoryStore
    {
        private readonly DeterministicMemoryStore _inner =
            new(providerId: "single-write");

        public string ProviderId => _inner.ProviderId;

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            return _inner.SearchAsync(query, cancellationToken);
        }

        public ValueTask UpsertAsync(
            MemoryRecord record,
            CancellationToken cancellationToken)
        {
            return _inner.UpsertAsync(record, cancellationToken);
        }

        public ValueTask<bool> DeleteAsync(
            string memoryId,
            CancellationToken cancellationToken)
        {
            return _inner.DeleteAsync(memoryId, cancellationToken);
        }
    }

    private sealed class AdversarialBatchStore : IAtomicMemoryBatchStore
    {
        private readonly IReadOnlyList<MemoryMutationResult> _results;

        public AdversarialBatchStore(
            IReadOnlyList<MemoryMutationResult> results)
        {
            _results = results;
        }

        public string ProviderId => "adversarial-batch";

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken)
        {
            return new ValueTask<IReadOnlyList<MemorySearchResult>>(
                Array.Empty<MemorySearchResult>());
        }

        public ValueTask UpsertAsync(
            MemoryRecord record,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(
            string memoryId,
            CancellationToken cancellationToken)
        {
            return new ValueTask<bool>(false);
        }

        public ValueTask<IReadOnlyList<MemoryMutationResult>>
            ApplyAtomicBatchAsync(
                IReadOnlyList<MemoryMutation> mutations,
                CancellationToken cancellationToken = default)
        {
            return new ValueTask<IReadOnlyList<MemoryMutationResult>>(
                _results);
        }
    }

    private class LegacyIdempotentMemoryStore :
        IIdempotentAtomicMemoryBatchStore
    {
        private readonly DeterministicMemoryStore _inner = new();

        public string ProviderId => "legacy-idempotent";

        public int ApplyCount { get; private set; }

        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken) =>
            _inner.SearchAsync(query, cancellationToken);

        public ValueTask UpsertAsync(
            MemoryRecord record,
            CancellationToken cancellationToken) =>
            _inner.UpsertAsync(record, cancellationToken);

        public ValueTask<bool> DeleteAsync(
            string memoryId,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(memoryId, cancellationToken);

        public ValueTask<IReadOnlyList<MemoryMutationResult>>
            ApplyAtomicBatchAsync(
                IReadOnlyList<MemoryMutation> mutations,
                CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            return _inner.ApplyAtomicBatchAsync(
                mutations,
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<MemoryMutationResult>>
            ApplyIdempotentAtomicBatchAsync(
                string commitId,
                IReadOnlyList<MemoryMutation> mutations,
                CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            return _inner.ApplyIdempotentAtomicBatchAsync(
                commitId,
                mutations,
                cancellationToken);
        }
    }

    private sealed class VersionedMemoryStore :
        LegacyIdempotentMemoryStore,
        IRuntimeAuthoritativeMemoryBatchStore
    {
        public VersionedMemoryStore(int version)
        {
            RuntimeMutationContractVersion = version;
        }

        public int RuntimeMutationContractVersion { get; }
    }

    private sealed class IndexedOnlyList<T> : IReadOnlyList<T>
    {
        private readonly T[] _items;

        public IndexedOnlyList(T[] items)
        {
            _items = items;
        }

        public int CountReads { get; private set; }

        public bool EnumeratorAccessed { get; private set; }

        public int Count
        {
            get
            {
                CountReads++;
                return _items.Length;
            }
        }

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator()
        {
            EnumeratorAccessed = true;
            throw new InvalidOperationException(
                "Enumeration is not supported.");
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}

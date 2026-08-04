using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

/// <summary>
/// Produces a fixed-size embedding for bounded JSON memory content. An
/// implementation may call a local model, a game service, or a remote API.
/// </summary>
public interface IMemoryEmbeddingProvider
{
    /// <summary>
    /// Identifies the embedding service implementation. This is not the
    /// memory provider id.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Identifies the model and preprocessing semantics that produced the
    /// vector. Change this identity when either one changes.
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// Identifies the model/preprocessing contract version.
    /// </summary>
    string Version { get; }

    int Dimensions { get; }

    ValueTask<ReadOnlyMemory<float>> EmbedAsync(
        JsonElement value,
        CancellationToken cancellationToken);
}

public sealed class VectorMemoryStoreOptions
{
    public VectorMemoryStoreOptions(
        int maxDimensions = 4_096,
        long maxVectorValues = 20_000_000,
        long maxComparisonsPerSearch = 20_000_000,
        int maxConcurrentEmbeddings = 8,
        TimeSpan? embeddingTimeout = null,
        double minimumSimilarity = 0,
        int scoreScale = 1_000_000)
    {
        if (maxDimensions is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDimensions));
        }

        if (maxVectorValues < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxVectorValues));
        }

        if (maxComparisonsPerSearch < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxComparisonsPerSearch));
        }

        if (maxConcurrentEmbeddings is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentEmbeddings));
        }

        var timeout = embeddingTimeout ?? TimeSpan.FromSeconds(15);
        if (timeout < TimeSpan.FromMilliseconds(1)
            || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(embeddingTimeout));
        }

        if (double.IsNaN(minimumSimilarity)
            || double.IsInfinity(minimumSimilarity)
            || minimumSimilarity is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumSimilarity));
        }

        if (scoreScale is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(scoreScale));
        }

        MaxDimensions = maxDimensions;
        MaxVectorValues = maxVectorValues;
        MaxComparisonsPerSearch = maxComparisonsPerSearch;
        MaxConcurrentEmbeddings = maxConcurrentEmbeddings;
        EmbeddingTimeout = timeout;
        MinimumSimilarity = minimumSimilarity;
        ScoreScale = scoreScale;
    }

    public int MaxDimensions { get; }

    public long MaxVectorValues { get; }

    public long MaxComparisonsPerSearch { get; }

    public int MaxConcurrentEmbeddings { get; }

    public TimeSpan EmbeddingTimeout { get; }

    public double MinimumSimilarity { get; }

    public int ScoreScale { get; }
}

/// <summary>
/// A bounded in-memory cosine-similarity index. It is optional and does not
/// require the rest of the runtime to configure an embedding model. Combine it
/// with a lexical provider through reciprocal-rank fusion for hybrid recall.
/// </summary>
public sealed class VectorMemoryStore :
    IRuntimeAuthoritativeMemoryBatchStore,
    ILegacyRuntimeMemoryBatchReplayStore
{
    private readonly object _sync = new();
    private readonly IMemoryEmbeddingProvider _embeddingProvider;
    private readonly VectorMemoryStoreOptions _options;
    private readonly int _capacity;
    private readonly int _dimensions;
    private readonly string _embeddingProviderId;
    private readonly string _embeddingModelId;
    private readonly string _embeddingVersion;
    private readonly SemaphoreSlim _embeddingSlots;
    private readonly BoundedCallbackExecutionDispatcher
        _embeddingExecutionDispatcher;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private Dictionary<string, IndexedMemory> _records =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idempotentBatchDigests =
        new(StringComparer.Ordinal);

    public VectorMemoryStore(
        IMemoryEmbeddingProvider embeddingProvider,
        string providerId = "vector-memory",
        int capacity = 10_000,
        VectorMemoryStoreOptions? options = null)
        : this(
            embeddingProvider,
            providerId,
            capacity,
            options,
            BoundedCallbackExecutionDispatcher.MemoryShared)
    {
    }

    internal VectorMemoryStore(
        IMemoryEmbeddingProvider embeddingProvider,
        string providerId,
        int capacity,
        VectorMemoryStoreOptions? options,
        BoundedCallbackExecutionDispatcher embeddingExecutionDispatcher)
    {
        _embeddingProvider = embeddingProvider
                             ?? throw new ArgumentNullException(
                                 nameof(embeddingProvider));
        _embeddingExecutionDispatcher = embeddingExecutionDispatcher
                                        ?? throw new ArgumentNullException(
                                            nameof(
                                                embeddingExecutionDispatcher));
        ProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));
        _embeddingProviderId = RuntimeGuard.RequiredUtf8(
            embeddingProvider.ProviderId,
            128,
            nameof(embeddingProvider));
        _embeddingModelId = RuntimeGuard.RequiredUtf8(
            embeddingProvider.ModelId,
            256,
            nameof(embeddingProvider));
        _embeddingVersion = RuntimeGuard.RequiredUtf8(
            embeddingProvider.Version,
            64,
            nameof(embeddingProvider));
        if (capacity is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _options = options ?? new VectorMemoryStoreOptions();
        _dimensions = embeddingProvider.Dimensions;
        if (_dimensions < 1 || _dimensions > _options.MaxDimensions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(embeddingProvider),
                "Embedding dimensions exceed the configured bound.");
        }

        if (checked((long)capacity * _dimensions)
            > _options.MaxVectorValues)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                "Vector capacity exceeds the configured value bound.");
        }

        _capacity = capacity;
        _embeddingSlots = new SemaphoreSlim(
            _options.MaxConcurrentEmbeddings,
            _options.MaxConcurrentEmbeddings);
    }

    public string ProviderId { get; }

    public int RuntimeMutationContractVersion =>
        RuntimeMemoryMutationContract.CurrentVersion;

    public string EmbeddingProviderId => _embeddingProviderId;

    public string EmbeddingModelId => _embeddingModelId;

    public string EmbeddingVersion => _embeddingVersion;

    public int Dimensions => _dimensions;

    public async ValueTask UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken = default)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }
        await _mutationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var vector = await EmbedNormalizedAsync(
                    record.Content,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _records.TryGetValue(record.MemoryId, out var existing);
                MemoryMutationAdmission.EnsureCanApplyUnconditionalUpsert(
                    MemoryMutation.Upsert(record),
                    existing?.Record);
                if (!_records.ContainsKey(record.MemoryId)
                    && _records.Count >= _capacity)
                {
                    throw new RuntimeContentLimitException(
                        nameof(record),
                        "memory_capacity_exceeded",
                        $"Memory capacity exceeds {_capacity} records.");
                }

                _records[record.MemoryId] = new IndexedMemory(
                    Snapshot(record),
                    vector);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask<bool> DeleteAsync(
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        memoryId = RuntimeGuard.RequiredUtf8(
            memoryId,
            128,
            nameof(memoryId));
        await _mutationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                return _records.Remove(memoryId);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }
        IndexedMemory[] snapshot;
        lock (_sync)
        {
            snapshot = _records.Values.ToArray();
        }

        if (checked((long)snapshot.Length * _dimensions)
            > _options.MaxComparisonsPerSearch)
        {
            throw new RuntimeContentLimitException(
                nameof(query),
                "memory_vector_comparison_limit_exceeded",
                "Vector recall exceeds the configured comparison bound.");
        }

        if (snapshot.Length == 0)
        {
            return Array.Empty<MemorySearchResult>();
        }

        var queryVector = await EmbedNormalizedAsync(
                query.Query,
                cancellationToken)
            .ConfigureAwait(false);

        var ranked = new List<MemorySearchResult>();
        foreach (var indexed in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MemoryQueryFilter.Matches(indexed.Record, query))
            {
                continue;
            }

            double similarity = 0;
            for (var index = 0; index < _dimensions; index++)
            {
                similarity += queryVector[index] * indexed.Vector[index];
            }

            similarity = Math.Max(-1, Math.Min(1, similarity));
            if (similarity < _options.MinimumSimilarity)
            {
                continue;
            }

            var normalized = (similarity + 1) / 2;
            var score = checked((int)Math.Round(
                normalized * _options.ScoreScale,
                MidpointRounding.AwayFromZero));
            ranked.Add(new MemorySearchResult(indexed.Record, score));
        }

        ranked.Sort(CompareResults);
        var selected = new List<MemorySearchResult>();
        var retainedBytes = 0;
        foreach (var result in ranked)
        {
            if (selected.Count >= query.MaxResults)
            {
                break;
            }

            var bytes = Encoding.UTF8.GetByteCount(
                result.Record.Content.GetRawText());
            if (checked(retainedBytes + bytes) > query.MaxUtf8Bytes)
            {
                continue;
            }

            selected.Add(result);
            retainedBytes += bytes;
        }

        return new ReadOnlyCollection<MemorySearchResult>(selected);
    }

    public async ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyAtomicBatchAsync(
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        await _mutationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await ApplyPreparedBatchAsync(
                    snapshot,
                    allowLegacyReplay: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        return await ApplyIdempotentAtomicBatchCoreAsync(
                commitId,
                mutations,
                allowLegacyReplay: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyLegacyIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        return await ApplyIdempotentAtomicBatchCoreAsync(
                commitId,
                mutations,
                allowLegacyReplay: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyIdempotentAtomicBatchCoreAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            bool allowLegacyReplay,
            CancellationToken cancellationToken)
    {
        commitId = RuntimeGuard.RequiredUtf8(
            commitId,
            256,
            nameof(commitId));
        var snapshot = MemoryBatchValidator.Snapshot(
            mutations,
            cancellationToken);
        var digest = RuntimeMemoryCommitJournalCodec
            .ComputeMutationDigest(snapshot);
        await _mutationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_idempotentBatchDigests.TryGetValue(
                        commitId,
                        out var existingDigest))
                {
                    if (!string.Equals(
                            existingDigest,
                            digest,
                            StringComparison.Ordinal))
                    {
                        throw new MemoryBatchIdempotencyConflictException(
                            commitId);
                    }

                    return new ReadOnlyCollection<MemoryMutationResult>(
                        snapshot
                            .Select(item => new MemoryMutationResult(
                                item.Kind,
                                item.MemoryId,
                                changed: false))
                            .ToArray());
                }

                if (_idempotentBatchDigests.Count
                    >= MemoryBatchLimits.MaxInMemoryIdempotencyKeys)
                {
                    throw new RuntimeContentLimitException(
                        nameof(commitId),
                        MemoryBatchReasonCodes.IdempotencyCapacityExceeded,
                        "Memory batch idempotency capacity is exhausted.");
                }
            }

            var results = await ApplyPreparedBatchAsync(
                    snapshot,
                    allowLegacyReplay,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _idempotentBatchDigests.Add(commitId, digest);
            }

            return results;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<MemoryMutationResult>>
        ApplyPreparedBatchAsync(
            MemoryMutation[] mutations,
            bool allowLegacyReplay,
            CancellationToken cancellationToken)
    {
        var prepared = new IndexedMemory?[mutations.Length];
        for (var index = 0; index < mutations.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutation = mutations[index];
            if (mutation.Kind == MemoryMutationKind.Upsert)
            {
                var record = mutation.Record
                             ?? throw new InvalidOperationException(
                                 "An upsert mutation requires a record.");
                var vector = await EmbedNormalizedAsync(
                        record.Content,
                        cancellationToken)
                    .ConfigureAwait(false);
                prepared[index] = new IndexedMemory(
                    Snapshot(record),
                    vector);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var staged = new Dictionary<string, IndexedMemory>(
                _records,
                StringComparer.Ordinal);
            var results = new MemoryMutationResult[mutations.Length];
            for (var index = 0; index < mutations.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mutation = mutations[index];
                staged.TryGetValue(mutation.MemoryId, out var existing);
                if (allowLegacyReplay)
                {
                    MemoryMutationAdmission.EnsureCanReplayLegacy(mutation);
                }
                else
                {
                    MemoryMutationAdmission.EnsureCanApply(
                        mutation,
                        existing?.Record);
                }
                switch (mutation.Kind)
                {
                    case MemoryMutationKind.Upsert:
                        staged[mutation.MemoryId] = prepared[index]
                            ?? throw new InvalidOperationException(
                                "An upsert mutation was not prepared.");
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            changed: true);
                        break;
                    case MemoryMutationKind.Delete:
                        results[index] = new MemoryMutationResult(
                            mutation.Kind,
                            mutation.MemoryId,
                            staged.Remove(mutation.MemoryId));
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown memory mutation kind "
                            + $"'{mutation.Kind}'.");
                }
            }

            if (staged.Count > _capacity)
            {
                throw new RuntimeContentLimitException(
                    nameof(mutations),
                    "memory_capacity_exceeded",
                    $"Memory capacity exceeds {_capacity} records.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _records = staged;
            return new ReadOnlyCollection<MemoryMutationResult>(results);
        }
    }

    private async ValueTask<float[]> EmbedNormalizedAsync(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        EnsureEmbeddingIdentity();
        await _embeddingSlots.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var releaseSlot = true;
        IsolatedCancellationLease? cancellation = null;
        try
        {
            cancellation = IsolatedCancellationLease.Create(
                BoundedCancellationDispatcher.MemoryExtensionShared);
            cancellationToken.ThrowIfCancellationRequested();

            using var signals = new OperationDeadlineSignals(
                _options.EmbeddingTimeout,
                cancellationToken);
            Task<ReadOnlyMemory<float>> operation;
            try
            {
                var providerToken = cancellation.Token;
                var input = value.Clone();
                if (!_embeddingExecutionDispatcher.TryExecute(
                        () => _embeddingProvider.EmbedAsync(
                            input,
                            providerToken),
                        out var acceptedOperation))
                {
                    throw new RuntimeContentLimitException(
                        nameof(value),
                        "memory_embedding_execution_capacity_exhausted",
                        "Memory embedding execution capacity is exhausted.");
                }

                operation = acceptedOperation;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Memory embedding exceeded its configured timeout.");
            }

            var completed = await Task.WhenAny(
                    operation,
                    signals.Timeout,
                    signals.Cancellation)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, operation))
            {
                _ = cancellation.TryCancel();
                releaseSlot = false;
                var detachedCancellation = cancellation;
                cancellation = null;
                _ = ObserveDetachedEmbeddingAsync(
                    operation,
                    detachedCancellation);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    "Memory embedding exceeded its configured timeout.");
            }

            await cancellation.DisposeAsync().ConfigureAwait(false);
            cancellation = null;
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<float> memory;
            try
            {
                memory = await operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Memory embedding exceeded its configured timeout.");
            }
            EnsureEmbeddingIdentity();
            if (memory.Length != _dimensions)
            {
                throw new InvalidDataException(
                    "The embedding provider returned an unexpected dimension.");
            }

            var values = memory.ToArray();
            var normalized = new float[_dimensions];
            double magnitudeSquared = 0;
            for (var index = 0; index < values.Length; index++)
            {
                var valueAtIndex = values[index];
                if (float.IsNaN(valueAtIndex)
                    || float.IsInfinity(valueAtIndex))
                {
                    throw new InvalidDataException(
                        "The embedding provider returned a non-finite value.");
                }

                magnitudeSquared += (double)valueAtIndex * valueAtIndex;
            }

            if (magnitudeSquared <= 0 || double.IsInfinity(magnitudeSquared))
            {
                throw new InvalidDataException(
                    "The embedding provider returned a zero or invalid vector.");
            }

            var magnitude = Math.Sqrt(magnitudeSquared);
            for (var index = 0; index < values.Length; index++)
            {
                normalized[index] = (float)(values[index] / magnitude);
            }

            return normalized;
        }
        finally
        {
            if (cancellation is not null)
            {
                await cancellation.DisposeAsync().ConfigureAwait(false);
            }
            if (releaseSlot)
            {
                _embeddingSlots.Release();
            }
        }
    }

    private async Task ObserveDetachedEmbeddingAsync(
        Task<ReadOnlyMemory<float>> operation,
        IsolatedCancellationLease cancellation)
    {
        try
        {
            _ = await operation.ConfigureAwait(false);
        }
        catch
        {
            // The caller already received timeout or cancellation. Observing
            // the provider task prevents an unobserved fault.
        }
        finally
        {
            cancellation.DisposeDetached();
            _embeddingSlots.Release();
        }
    }

    private void EnsureEmbeddingIdentity()
    {
        if (_embeddingProvider.Dimensions != _dimensions
            || !string.Equals(
                _embeddingProvider.ProviderId,
                _embeddingProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                _embeddingProvider.ModelId,
                _embeddingModelId,
                StringComparison.Ordinal)
            || !string.Equals(
                _embeddingProvider.Version,
                _embeddingVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The embedding identity changed after the vector index "
                + "was created.");
        }
    }

    private static int CompareResults(
        MemorySearchResult left,
        MemorySearchResult right)
    {
        var score = right.Score.CompareTo(left.Score);
        if (score != 0)
        {
            return score;
        }

        var updated = right.Record.UpdatedAt.CompareTo(
            left.Record.UpdatedAt);
        return updated != 0
            ? updated
            : StringComparer.Ordinal.Compare(
                left.Record.MemoryId,
                right.Record.MemoryId);
    }

    private static MemoryRecord Snapshot(MemoryRecord record) =>
        new(
            record.MemoryId,
            record.Scope,
            record.Content,
            record.Tags,
            record.Importance,
            record.CreatedAt,
            record.UpdatedAt,
            record.ExpiresAt,
            record.Provenance,
            record.GameTimeWindow);

    private sealed class IndexedMemory
    {
        public IndexedMemory(MemoryRecord record, float[] vector)
        {
            Record = record;
            Vector = vector;
        }

        public MemoryRecord Record { get; }

        public float[] Vector { get; }
    }
}

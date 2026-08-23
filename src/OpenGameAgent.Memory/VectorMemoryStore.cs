using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace OpenGameAgent.Memory;

public sealed class VectorMemoryStoreOptions
{
    public VectorMemoryStoreOptions(
        int maximumIndexEntries = 100_000,
        long maximumStoredVectorValues = 200_000_000,
        long maximumVectorComparisonsPerSearch = 20_000_000,
        int candidateMultiplier = 4,
        int maximumCandidates = 512,
        int rebuildBatchSize = 32,
        int maximumConcurrentEmbeddingCalls = 4,
        TimeSpan? embeddingTimeout = null,
        TimeSpan? diagnosticTimeout = null,
        bool failWhenEmbeddingUnavailable = false,
        bool disposeEmbeddingProvider = true)
    {
        if (maximumIndexEntries < 1 || maximumIndexEntries > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumIndexEntries));
        }

        if (maximumVectorComparisonsPerSearch < 1 || maximumVectorComparisonsPerSearch > 10_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumVectorComparisonsPerSearch));
        }

        if (maximumStoredVectorValues < 1 || maximumStoredVectorValues > 10_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStoredVectorValues));
        }

        if (candidateMultiplier < 1 || candidateMultiplier > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateMultiplier));
        }

        if (maximumCandidates < 1 || maximumCandidates > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        if (rebuildBatchSize < 1 || rebuildBatchSize > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(rebuildBatchSize));
        }

        if (maximumConcurrentEmbeddingCalls < 1 || maximumConcurrentEmbeddingCalls > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentEmbeddingCalls));
        }

        var effectiveEmbeddingTimeout = embeddingTimeout ?? TimeSpan.FromSeconds(30);
        var effectiveDiagnosticTimeout = diagnosticTimeout ?? TimeSpan.FromMilliseconds(500);
        ValidateTimeout(effectiveEmbeddingTimeout, nameof(embeddingTimeout), TimeSpan.FromMinutes(10));
        ValidateTimeout(effectiveDiagnosticTimeout, nameof(diagnosticTimeout), TimeSpan.FromSeconds(30));

        MaximumIndexEntries = maximumIndexEntries;
        MaximumStoredVectorValues = maximumStoredVectorValues;
        MaximumVectorComparisonsPerSearch = maximumVectorComparisonsPerSearch;
        CandidateMultiplier = candidateMultiplier;
        MaximumCandidates = maximumCandidates;
        RebuildBatchSize = rebuildBatchSize;
        MaximumConcurrentEmbeddingCalls = maximumConcurrentEmbeddingCalls;
        EmbeddingTimeout = effectiveEmbeddingTimeout;
        DiagnosticTimeout = effectiveDiagnosticTimeout;
        FailWhenEmbeddingUnavailable = failWhenEmbeddingUnavailable;
        DisposeEmbeddingProvider = disposeEmbeddingProvider;
    }

    public int MaximumIndexEntries { get; }

    public long MaximumStoredVectorValues { get; }

    public long MaximumVectorComparisonsPerSearch { get; }

    public int CandidateMultiplier { get; }

    public int MaximumCandidates { get; }

    public int RebuildBatchSize { get; }

    public int MaximumConcurrentEmbeddingCalls { get; }

    public TimeSpan EmbeddingTimeout { get; }

    public TimeSpan DiagnosticTimeout { get; }

    public bool FailWhenEmbeddingUnavailable { get; }

    public bool DisposeEmbeddingProvider { get; }

    private static void ValidateTimeout(TimeSpan value, string parameterName, TimeSpan maximum)
    {
        if (value < TimeSpan.FromMilliseconds(1) || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>
/// Adds a derived vector index and hybrid retrieval to any authoritative
/// memory store. The authoritative store is always written first. Embedding
/// failures therefore degrade recall to lexical search without losing memory.
/// </summary>
public sealed class VectorMemoryStore :
    IGameMemoryStore,
    IGameMemorySnapshotSource,
    IGameMemoryPartitionSnapshotSource,
    IGameMemorySearchSnapshotSource,
    IAsyncDisposable
{
    private readonly IGameMemoryStore _authoritativeStore;
    private readonly IGameMemorySnapshotSource _snapshotSource;
    private readonly IVectorMemoryIndex _index;
    private readonly IMemoryEmbeddingProvider _embeddingProvider;
    private readonly IMemoryEmbeddingTextProjector _projector;
    private readonly IGameMemoryRanker? _reranker;
    private readonly IMemoryVectorDiagnosticSink _diagnostics;
    private readonly VectorMemoryStoreOptions _options;
    private readonly SemaphoreSlim _embeddingSlots;
    private readonly SemaphoreSlim _diagnosticSlot = new(1, 1);
    private readonly MemoryEmbeddingIdentity _activeIdentity;
    private int _disposed;

    public VectorMemoryStore(
        IGameMemoryStore authoritativeStore,
        IVectorMemoryIndex index,
        IMemoryEmbeddingProvider embeddingProvider,
        IMemoryEmbeddingTextProjector? projector = null,
        IGameMemoryRanker? reranker = null,
        IMemoryVectorDiagnosticSink? diagnostics = null,
        VectorMemoryStoreOptions? options = null,
        IGameMemorySnapshotSource? snapshotSource = null)
    {
        _authoritativeStore = authoritativeStore ?? throw new ArgumentNullException(nameof(authoritativeStore));
        _snapshotSource = snapshotSource ?? authoritativeStore as IGameMemorySnapshotSource
            ?? throw new ArgumentException(
                "The authoritative store must expose deterministic snapshots for vector rebuilds.",
                nameof(authoritativeStore));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _activeIdentity = embeddingProvider.Identity
            ?? throw new ArgumentException("The embedding provider requires an identity.", nameof(embeddingProvider));
        _projector = projector ?? new DefaultMemoryEmbeddingTextProjector();
        _reranker = reranker;
        _diagnostics = diagnostics ?? NullMemoryVectorDiagnosticSink.Instance;
        _options = options ?? new VectorMemoryStoreOptions();
        if (checked((long)_options.MaximumIndexEntries * _activeIdentity.Dimensions)
            > _options.MaximumStoredVectorValues)
        {
            throw new ArgumentException(
                "The configured vector index size exceeds its stored-value bound.",
                nameof(options));
        }

        _embeddingSlots = new SemaphoreSlim(
            _options.MaximumConcurrentEmbeddingCalls,
            _options.MaximumConcurrentEmbeddingCalls);
    }

    public MemoryEmbeddingIdentity ActiveIdentity => _activeIdentity;

    public async ValueTask AppendAsync(GameMemory memory, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (memory is null)
        {
            throw new ArgumentNullException(nameof(memory));
        }

        await _authoritativeStore.AppendAsync(memory, cancellationToken).ConfigureAwait(false);
        try
        {
            await _index.UpsertAsync(
                    new VectorMemoryIndexEntry(memory, null, null, "embedding_pending"),
                    cancellationToken)
                .ConfigureAwait(false);
            var vector = await EmbedDocumentAsync(memory, cancellationToken).ConfigureAwait(false);
            await _index.UpsertAsync(
                    new VectorMemoryIndexEntry(memory, ActiveIdentity, vector),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReportAsync(
                    "memory_embedding_append_failed",
                    MemoryVectorDiagnosticSeverity.Warning,
                    "The memory was saved, but its derived vector is unavailable. Lexical recall remains available.",
                    memory,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
            if (_options.FailWhenEmbeddingUnavailable)
            {
                throw;
            }
        }
    }

    public async ValueTask<IReadOnlyList<GameMemory>> SearchAsync(
        GameMemoryQuery query,
        CancellationToken cancellationToken)
    {
        var result = await SearchSnapshotAsync(query, _options.MaximumIndexEntries, cancellationToken)
            .ConfigureAwait(false);
        return result.Memories;
    }

    public async ValueTask<GameMemorySearchSnapshot> SearchSnapshotAsync(
        GameMemoryQuery query,
        int maximumSnapshotEntries,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (maximumSnapshotEntries < 1 || maximumSnapshotEntries > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshotEntries));
        }

        maximumSnapshotEntries = Math.Min(maximumSnapshotEntries, _options.MaximumIndexEntries);

        var candidateLimit = Math.Min(
            _options.MaximumCandidates,
            Math.Max(query.Limit, checked(query.Limit * _options.CandidateMultiplier)));
        var expandedQuery = CopyQuery(query, candidateLimit);
        var stages = new List<GameMemorySearchStageMetric>();
        IReadOnlyList<GameMemory> lexical;
        IReadOnlyDictionary<(string OwnerId, string MemoryId), GameMemory> authoritative;
        if (_authoritativeStore is IGameMemorySearchSnapshotSource combined)
        {
            var combinedSnapshot = await combined.SearchSnapshotAsync(
                    expandedQuery,
                    maximumSnapshotEntries,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The authoritative memory store returned null.");
            lexical = combinedSnapshot.Memories;
            authoritative = BuildAuthoritativeSnapshot(query, combinedSnapshot.AuthoritativeMemories, maximumSnapshotEntries);
            stages.AddRange(combinedSnapshot.Stages.Select(stage =>
                stage.Stage == GameMemorySearchStageKind.AuthoritativeSnapshot
                    ? new GameMemorySearchStageMetric(
                        stage.Stage,
                        stage.Duration,
                        stage.ScannedCount,
                        stage.CandidateCount,
                        reused: true)
                    : stage));
        }
        else
        {
            var lexicalStartedAt = Stopwatch.GetTimestamp();
            lexical = await _authoritativeStore.SearchAsync(expandedQuery, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The authoritative memory store returned null.");
            stages.Add(new GameMemorySearchStageMetric(
                GameMemorySearchStageKind.LexicalSearch,
                Elapsed(lexicalStartedAt),
                lexical.Count,
                lexical.Count));
            var authoritativeStartedAt = Stopwatch.GetTimestamp();
            authoritative = await LoadAuthoritativeSnapshotAsync(query, maximumSnapshotEntries, cancellationToken)
                .ConfigureAwait(false);
            stages.Add(new GameMemorySearchStageMetric(
                GameMemorySearchStageKind.AuthoritativeSnapshot,
                Elapsed(authoritativeStartedAt),
                authoritative.Count,
                authoritative.Count,
                reused: true));
        }

        ValidateCandidates(expandedQuery, lexical);

        IReadOnlyList<GameMemory> vector = Array.Empty<GameMemory>();
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            try
            {
                vector = await SearchVectorAsync(
                        query,
                        candidateLimit,
                        authoritative,
                        stages,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await ReportAsync(
                        "memory_vector_search_failed",
                        MemoryVectorDiagnosticSeverity.Warning,
                        "Vector recall is unavailable for this query. Lexical recall was used.",
                        memory: null,
                        exception,
                        cancellationToken,
                        query.SessionId)
                    .ConfigureAwait(false);
                if (_options.FailWhenEmbeddingUnavailable)
                {
                    throw;
                }
            }
        }

        var fused = Fuse(lexical, vector, candidateLimit);
        if (_reranker is not null && fused.Count > 0)
        {
            var rerankStartedAt = Stopwatch.GetTimestamp();
            var reranked = await _reranker.RankAsync(query, fused, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The memory reranker returned null.");
            fused = ValidateReranked(fused, reranked);
            stages.Add(new GameMemorySearchStageMetric(
                GameMemorySearchStageKind.Rerank,
                Elapsed(rerankStartedAt),
                fused.Count,
                reranked.Count));
        }

        return new GameMemorySearchSnapshot(
            Array.AsReadOnly(fused.Take(query.Limit).ToArray()),
            authoritative.Values
                .OrderBy(memory => memory.OwnerId, StringComparer.Ordinal)
                .ThenBy(memory => memory.MemoryId, StringComparer.Ordinal)
                .ToArray(),
            stages);
    }

    public async IAsyncEnumerable<GameMemory> EnumerateAsync(
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        sessionId = MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024);
        await foreach (var memory in _snapshotSource.EnumerateAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            yield return memory;
        }
    }

    public async IAsyncEnumerable<GameMemory> EnumerateAsync(
        string sessionId,
        string ownerId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        sessionId = MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024);
        ownerId = MemoryVectorGuard.Id(ownerId, nameof(ownerId), 1_024);
        if (_snapshotSource is IGameMemoryPartitionSnapshotSource partitioned)
        {
            await foreach (var memory in partitioned.EnumerateAsync(sessionId, ownerId, cancellationToken).ConfigureAwait(false))
            {
                yield return memory;
            }

            yield break;
        }

        await foreach (var memory in _snapshotSource.EnumerateAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(memory.OwnerId, ownerId, StringComparison.Ordinal))
            {
                yield return memory;
            }
        }
    }

    public async ValueTask<VectorMemoryStatus> GetStatusAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        sessionId = MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024);
        var entries = await _index.ListAsync(
                sessionId,
                _options.MaximumIndexEntries,
                cancellationToken)
            .ConfigureAwait(false);
        var authoritative = await LoadAuthoritativeSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return BuildStatus(entries, authoritative);
    }

    /// <summary>
    /// Explicitly regenerates derived vectors using the active provider
    /// identity. Existing stale vectors remain excluded until each replacement
    /// is durably written, so cancellation or a crash leaves a recoverable
    /// partial rebuild.
    /// </summary>
    public async ValueTask<VectorMemoryStatus> RebuildAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        sessionId = MemoryVectorGuard.Id(sessionId, nameof(sessionId), 1_024);
        var authoritative = await LoadAuthoritativeSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var memories = authoritative.Values.ToList();
        var completed = true;

        for (var offset = 0; offset < memories.Count; offset += _options.RebuildBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = memories.Skip(offset).Take(_options.RebuildBatchSize).ToArray();
            try
            {
                var vectors = await EmbedDocumentsAsync(batch, cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < batch.Length; index++)
                {
                    await _index.UpsertAsync(
                            new VectorMemoryIndexEntry(batch[index], ActiveIdentity, vectors[index]),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await ReportAsync(
                        "memory_embedding_rebuild_failed",
                        MemoryVectorDiagnosticSeverity.Error,
                        "The vector rebuild stopped after a batch failed. Retry the explicit rebuild after fixing the embedding provider or derived index.",
                        batch[0],
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var memory in batch)
                {
                    try
                    {
                        await _index.UpsertAsync(
                                new VectorMemoryIndexEntry(memory, null, null, "embedding_rebuild_failed"),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception indexException)
                    {
                        await ReportAsync(
                                "memory_vector_index_write_failed",
                                MemoryVectorDiagnosticSeverity.Error,
                                "The derived vector index could not persist a failed rebuild marker.",
                                memory,
                                indexException,
                                cancellationToken)
                            .ConfigureAwait(false);
                        throw new InvalidOperationException(
                            "The derived vector index failed while recording rebuild state.",
                            indexException);
                    }
                }
                completed = false;
                break;
            }
        }

        if (completed)
        {
            var current = await _index.ListAsync(sessionId, _options.MaximumIndexEntries, cancellationToken)
                .ConfigureAwait(false);
            foreach (var entry in current)
            {
                if (!authoritative.ContainsKey((entry.Memory.OwnerId, entry.Memory.MemoryId)))
                {
                    await _index.DeleteAsync(
                            entry.Memory.SessionId,
                            entry.Memory.OwnerId,
                            entry.Memory.MemoryId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        var status = await GetStatusAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!status.RequiresRebuild)
        {
            await ReportAsync(
                    "memory_embedding_rebuild_completed",
                    MemoryVectorDiagnosticSeverity.Information,
                    "The vector memory index was rebuilt with the active embedding identity.",
                    memory: null,
                    exception: null,
                    cancellationToken,
                    sessionId)
                .ConfigureAwait(false);
        }

        return status;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_options.DisposeEmbeddingProvider)
        {
            await _embeddingProvider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<IReadOnlyList<float>> EmbedDocumentAsync(
        GameMemory memory,
        CancellationToken cancellationToken)
    {
        var vectors = await EmbedDocumentsAsync(new[] { memory }, cancellationToken).ConfigureAwait(false);
        return vectors[0];
    }

    private async ValueTask<IReadOnlyList<IReadOnlyList<float>>> EmbedDocumentsAsync(
        IReadOnlyList<GameMemory> memories,
        CancellationToken cancellationToken)
    {
        var texts = memories.Select(_projector.ProjectDocument).ToArray();
        var raw = await InvokeEmbeddingAsync(
                token => _embeddingProvider.EmbedDocumentsAsync(texts, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (raw is null || raw.Count != memories.Count)
        {
            throw new InvalidOperationException("The embedding provider returned an invalid batch size.");
        }

        return new ReadOnlyCollection<IReadOnlyList<float>>(
            raw.Select(vector => (IReadOnlyList<float>)Array.AsReadOnly(
                    MemoryVectorGuard.Normalize(vector, ActiveIdentity, nameof(raw))))
                .ToArray());
    }

    private async ValueTask<IReadOnlyList<GameMemory>> SearchVectorAsync(
        GameMemoryQuery query,
        int candidateLimit,
        IReadOnlyDictionary<(string OwnerId, string MemoryId), GameMemory> authoritative,
        ICollection<GameMemorySearchStageMetric> stages,
        CancellationToken cancellationToken)
    {
        var text = _projector.ProjectQuery(query);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<GameMemory>();
        }

        var embeddingStartedAt = Stopwatch.GetTimestamp();
        var queryVector = await InvokeEmbeddingAsync(
                token => _embeddingProvider.EmbedQueryAsync(text, token),
                cancellationToken)
            .ConfigureAwait(false);
        var normalized = MemoryVectorGuard.Normalize(queryVector, ActiveIdentity, nameof(queryVector));
        stages.Add(new GameMemorySearchStageMetric(
            GameMemorySearchStageKind.Embedding,
            Elapsed(embeddingStartedAt),
            scannedCount: 1,
            candidateCount: 1));
        var indexStartedAt = Stopwatch.GetTimestamp();
        var listed = query.OwnerId is not null && _index is IVectorMemoryPartitionIndex partitioned
            ? await partitioned.ListAsync(query.SessionId, query.OwnerId, _options.MaximumIndexEntries, cancellationToken)
                .ConfigureAwait(false)
            : await _index.ListAsync(query.SessionId, _options.MaximumIndexEntries, cancellationToken)
                .ConfigureAwait(false);
        var entries = query.OwnerId is null
            ? listed
            : listed.Where(entry => string.Equals(entry.Memory.OwnerId, query.OwnerId, StringComparison.Ordinal)).ToArray();
        stages.Add(new GameMemorySearchStageMetric(
            GameMemorySearchStageKind.VectorIndexRead,
            Elapsed(indexStartedAt),
            entries.Count,
            entries.Count));
        if (checked((long)entries.Count * ActiveIdentity.Dimensions) > _options.MaximumVectorComparisonsPerSearch)
        {
            throw new InvalidOperationException("Vector recall exceeded the configured comparison bound.");
        }

        var scoringStartedAt = Stopwatch.GetTimestamp();
        var ranked = new List<(GameMemory Memory, double Score)>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!authoritative.TryGetValue((entry.Memory.OwnerId, entry.Memory.MemoryId), out var authoritativeMemory))
            {
                throw new InvalidDataException("The derived vector index contains a memory absent from the authoritative store.");
            }

            MemoryVectorIndexCodec.EnsureSameMemory(authoritativeMemory, entry.Memory);
            if (entry.Vector is null
                || entry.Identity is null
                || !entry.Identity.Equals(ActiveIdentity)
                || !MatchesQuery(authoritativeMemory, query))
            {
                continue;
            }

            double score = 0;
            for (var index = 0; index < normalized.Length; index++)
            {
                score += normalized[index] * entry.Vector[index];
            }

            ranked.Add((authoritativeMemory, Math.Max(-1, Math.Min(1, score))));
        }

        var result = Array.AsReadOnly(ranked
            .OrderByDescending(value => value.Score)
            .ThenByDescending(value => value.Memory.Importance)
            .ThenByDescending(value => value.Memory.Moment.Tick)
            .ThenBy(value => value.Memory.OwnerId, StringComparer.Ordinal)
            .ThenBy(value => value.Memory.MemoryId, StringComparer.Ordinal)
            .Take(candidateLimit)
            .Select(value => value.Memory)
            .ToArray());
        stages.Add(new GameMemorySearchStageMetric(
            GameMemorySearchStageKind.VectorScoring,
            Elapsed(scoringStartedAt),
            entries.Count,
            result.Count));
        return result;
    }

    private async ValueTask<T> InvokeEmbeddingAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.EmbeddingTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _embeddingSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The memory embedding queue exceeded its configured deadline.");
        }

        var releaseSlot = true;
        try
        {
            Task<T> task;
            try
            {
                task = operation(linked.Token).AsTask();
            }
            catch
            {
                throw;
            }

            var delay = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (completed == task)
            {
                return await task.ConfigureAwait(false);
            }

            // A provider is allowed to ignore cancellation. Keep its concurrency
            // lease until it actually settles so repeated timeouts cannot create
            // an unbounded number of detached embedding calls.
            releaseSlot = false;
            _ = task.ContinueWith(
                continuation =>
                {
                    _ = continuation.Exception;
                    _embeddingSlots.Release();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new TimeoutException("The memory embedding provider exceeded its configured deadline.");
        }
        finally
        {
            if (releaseSlot)
            {
                _embeddingSlots.Release();
            }
        }
    }

    private VectorMemoryStatus BuildStatus(
        IReadOnlyList<VectorMemoryIndexEntry> entries,
        IReadOnlyDictionary<(string OwnerId, string MemoryId), GameMemory> authoritative)
    {
        var byId = entries.ToDictionary(entry => (entry.Memory.OwnerId, entry.Memory.MemoryId));
        var ready = 0;
        var stale = 0;
        var pending = 0;
        foreach (var pair in authoritative)
        {
            if (!byId.TryGetValue(pair.Key, out var entry))
            {
                pending++;
                continue;
            }

            MemoryVectorIndexCodec.EnsureSameMemory(pair.Value, entry.Memory);
            if (entry.Vector is null)
            {
                pending++;
            }
            else if (entry.Identity?.Equals(ActiveIdentity) == true)
            {
                ready++;
            }
            else
            {
                stale++;
            }
        }

        var orphans = byId.Keys.Count(key => !authoritative.ContainsKey(key));
        var state = authoritative.Count == 0 && orphans == 0
            ? VectorMemoryState.Empty
            : stale > 0
                ? VectorMemoryState.RebuildRequired
                : pending > 0 || orphans > 0
                    ? VectorMemoryState.Degraded
                    : VectorMemoryState.Ready;
        return new VectorMemoryStatus(state, ActiveIdentity, authoritative.Count, ready, pending, stale, orphans);
    }

    private async ValueTask<IReadOnlyDictionary<(string OwnerId, string MemoryId), GameMemory>> LoadAuthoritativeSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken)
        => await LoadAuthoritativeSnapshotAsync(
                new GameMemoryQuery(sessionId, 0),
                _options.MaximumIndexEntries,
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<IReadOnlyDictionary<(string OwnerId, string MemoryId), GameMemory>> LoadAuthoritativeSnapshotAsync(
        GameMemoryQuery query,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        var values = new List<GameMemory>();
        if (query.OwnerId is not null && _snapshotSource is IGameMemoryPartitionSnapshotSource partitioned)
        {
            await foreach (var memory in partitioned.EnumerateAsync(query.SessionId, query.OwnerId, cancellationToken)
                               .ConfigureAwait(false))
            {
                values.Add(memory);
                if (values.Count > maximumEntries)
                {
                    throw new InvalidOperationException("The authoritative memory snapshot exceeded the configured bound.");
                }
            }
        }
        else
        {
            await foreach (var memory in _snapshotSource.EnumerateAsync(query.SessionId, cancellationToken).ConfigureAwait(false))
            {
                if (query.OwnerId is null || string.Equals(memory.OwnerId, query.OwnerId, StringComparison.Ordinal))
                {
                    values.Add(memory);
                }

                if (values.Count > maximumEntries)
                {
                    throw new InvalidOperationException("The authoritative memory snapshot exceeded the configured bound.");
                }
            }
        }

        return BuildAuthoritativeSnapshot(query, values, maximumEntries);
    }

    private static IReadOnlyDictionary<(string OwnerId, string MemoryId), GameMemory> BuildAuthoritativeSnapshot(
        GameMemoryQuery query,
        IReadOnlyList<GameMemory> values,
        int maximumEntries)
    {
        var memories = new Dictionary<(string OwnerId, string MemoryId), GameMemory>();
        foreach (var memory in values)
        {
            if (!string.Equals(memory.SessionId, query.SessionId, StringComparison.Ordinal)
                || (query.OwnerId is not null && !string.Equals(memory.OwnerId, query.OwnerId, StringComparison.Ordinal))
                || !memories.TryAdd((memory.OwnerId, memory.MemoryId), memory))
            {
                throw new InvalidOperationException("The authoritative memory snapshot returned an invalid identity.");
            }

            if (memories.Count > maximumEntries)
            {
                throw new InvalidOperationException("The authoritative memory snapshot exceeded the configured bound.");
            }
        }

        return new ReadOnlyDictionary<(string OwnerId, string MemoryId), GameMemory>(memories);
    }

    private static TimeSpan Elapsed(long startedAt)
    {
        var ticks = checked(Stopwatch.GetTimestamp() - startedAt);
        return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }

    private async ValueTask ReportAsync(
        string code,
        MemoryVectorDiagnosticSeverity severity,
        string message,
        GameMemory? memory,
        Exception? exception,
        CancellationToken cancellationToken,
        string? sessionId = null)
    {
        var details = exception is null
            ? null
            : JsonSerializer.Serialize(new { exception = exception.GetType().Name });
        var diagnostic = new MemoryVectorDiagnostic(
            code,
            severity,
            message,
            sessionId ?? memory?.SessionId,
            memory?.OwnerId,
            memory?.MemoryId,
            details);
        try
        {
            using var timeout = new CancellationTokenSource(_options.DiagnosticTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await _diagnosticSlot.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var releaseSlot = true;
            try
            {
                var task = _diagnostics.ReportAsync(diagnostic, linked.Token).AsTask();
                var delay = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
                if (completed == task)
                {
                    await task.ConfigureAwait(false);
                    return;
                }

                releaseSlot = false;
                _ = task.ContinueWith(
                    continuation =>
                    {
                        _ = continuation.Exception;
                        _diagnosticSlot.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                if (releaseSlot)
                {
                    _diagnosticSlot.Release();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static GameMemoryQuery CopyQuery(GameMemoryQuery query, int limit) => new(
        query.SessionId,
        limit,
        query.OwnerId,
        query.Scopes,
        query.Kinds,
        query.Tags,
        query.Text,
        query.AtOrBefore,
        query.MinimumImportance);

    private static IReadOnlyList<GameMemory> Fuse(
        IReadOnlyList<GameMemory> lexical,
        IReadOnlyList<GameMemory> vector,
        int maximumCandidates)
    {
        const double rankConstant = 60;
        var candidates = new Dictionary<(string OwnerId, string MemoryId), FusedCandidate>();
        Add(lexical);
        Add(vector);
        return Array.AsReadOnly(candidates.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Memory.Importance)
            .ThenByDescending(candidate => candidate.Memory.Moment.Tick)
            .ThenBy(candidate => candidate.Memory.OwnerId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Memory.MemoryId, StringComparer.Ordinal)
            .Take(maximumCandidates)
            .Select(candidate => candidate.Memory)
            .ToArray());

        void Add(IReadOnlyList<GameMemory> source)
        {
            for (var rank = 0; rank < source.Count; rank++)
            {
                var memory = source[rank] ?? throw new InvalidOperationException("A memory provider returned null.");
                var key = (memory.OwnerId, memory.MemoryId);
                if (!candidates.TryGetValue(key, out var candidate))
                {
                    candidate = new FusedCandidate(memory);
                    candidates.Add(key, candidate);
                }
                else
                {
                    MemoryVectorIndexCodec.EnsureSameMemory(candidate.Memory, memory);
                }

                candidate.Score += 1d / (rankConstant + rank + 1);
            }
        }
    }

    private static IReadOnlyList<GameMemory> ValidateReranked(
        IReadOnlyList<GameMemory> source,
        IReadOnlyList<GameMemory> ranked)
    {
        if (ranked.Count > source.Count)
        {
            throw new InvalidOperationException("The memory reranker returned too many candidates.");
        }

        var canonical = source.ToDictionary(memory => (memory.OwnerId, memory.MemoryId));
        var seen = new HashSet<(string OwnerId, string MemoryId)>();
        var output = new List<GameMemory>(ranked.Count);
        foreach (var memory in ranked)
        {
            if (memory is null
                || !canonical.TryGetValue((memory.OwnerId, memory.MemoryId), out var original)
                || !seen.Add((memory.OwnerId, memory.MemoryId)))
            {
                throw new InvalidOperationException("The memory reranker returned an unknown, duplicate, or null memory.");
            }

            MemoryVectorIndexCodec.EnsureSameMemory(original, memory);
            output.Add(original);
        }

        return new ReadOnlyCollection<GameMemory>(output);
    }

    private static void ValidateCandidates(GameMemoryQuery query, IReadOnlyList<GameMemory> candidates)
    {
        if (candidates.Count > query.Limit)
        {
            throw new InvalidOperationException("The authoritative memory store exceeded the candidate limit.");
        }

        var ids = new HashSet<(string OwnerId, string MemoryId)>();
        foreach (var memory in candidates)
        {
            if (memory is null || !ids.Add((memory.OwnerId, memory.MemoryId)) || !MatchesQuery(memory, query))
            {
                throw new InvalidOperationException("The authoritative memory store returned an invalid candidate.");
            }
        }
    }

    private static bool MatchesQuery(GameMemory memory, GameMemoryQuery query)
    {
        if (!string.Equals(memory.SessionId, query.SessionId, StringComparison.Ordinal)
            || (query.OwnerId is not null && !string.Equals(memory.OwnerId, query.OwnerId, StringComparison.Ordinal))
            || (query.Scopes.Count > 0 && !query.Scopes.Contains(memory.Scope, StringComparer.Ordinal))
            || (query.Kinds.Count > 0 && !query.Kinds.Contains(memory.Kind))
            || query.Tags.Any(tag => !memory.Tags.Contains(tag, StringComparer.Ordinal))
            || memory.Importance < query.MinimumImportance)
        {
            return false;
        }

        if (query.AtOrBefore is not { } moment)
        {
            return true;
        }

        return string.Equals(memory.Moment.TimelineId, moment.TimelineId, StringComparison.Ordinal)
            && memory.Moment.Tick <= moment.Tick
            && (memory.ExpiresAt is null || moment.Tick < memory.ExpiresAt.Value.Tick);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(VectorMemoryStore));
        }
    }

    private sealed class FusedCandidate
    {
        public FusedCandidate(GameMemory memory)
        {
            Memory = memory;
        }

        public GameMemory Memory { get; }

        public double Score { get; set; }

    }
}

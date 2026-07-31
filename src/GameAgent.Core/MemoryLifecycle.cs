using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;

namespace GameAgent.Core;

public sealed class MemoryLifecycleOptions
{
    public int MaxProviders { get; set; } = 16;

    public int MaxConcurrentPrefetches { get; set; } = 4;

    public int MaxPrefetchEntries { get; set; } = 128;

    public int MaxResultsPerProvider { get; set; } = 1_024;

    public int MaxRetainedCandidates { get; set; } = 4_096;

    public int MaxConcurrentProviderCalls { get; set; } = 8;

    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    internal MemoryLifecycleOptions Snapshot()
    {
        if (MaxProviders is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxProviders));
        }

        if (MaxConcurrentPrefetches is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentPrefetches));
        }

        if (MaxPrefetchEntries is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPrefetchEntries));
        }

        if (MaxResultsPerProvider is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResultsPerProvider));
        }

        if (MaxRetainedCandidates is < 128 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetainedCandidates));
        }

        if (MaxConcurrentProviderCalls is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentProviderCalls));
        }

        if (ProviderTimeout < TimeSpan.FromMilliseconds(1)
            || ProviderTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ProviderTimeout));
        }

        if (ShutdownTimeout <= TimeSpan.Zero
            || ShutdownTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        }

        return new MemoryLifecycleOptions
        {
            MaxProviders = MaxProviders,
            MaxConcurrentPrefetches = MaxConcurrentPrefetches,
            MaxPrefetchEntries = MaxPrefetchEntries,
            MaxResultsPerProvider = MaxResultsPerProvider,
            MaxRetainedCandidates = MaxRetainedCandidates,
            MaxConcurrentProviderCalls = MaxConcurrentProviderCalls,
            ProviderTimeout = ProviderTimeout,
            ShutdownTimeout = ShutdownTimeout
        };
    }
}

public sealed class MemoryRecallReport
{
    internal MemoryRecallReport(
        IReadOnlyList<MemorySearchResult> results,
        IReadOnlyList<string> failedProviderIds)
    {
        Results = results;
        FailedProviderIds = failedProviderIds;
    }

    public IReadOnlyList<MemorySearchResult> Results { get; }

    public IReadOnlyList<string> FailedProviderIds { get; }

    public bool IsPartial => FailedProviderIds.Count > 0;
}

/// <summary>
/// Coordinates bounded recall, prefetch, committed writes, and shutdown.
/// Memory remains derived and untrusted; it cannot prove a host action.
/// </summary>
public sealed class RuntimeMemoryLifecycle : IAsyncDisposable
{
    private readonly IReadOnlyList<IMemoryProvider> _providers;
    private readonly IReadOnlyList<string> _providerIds;
    private readonly IMemoryStore? _writeStore;
    private readonly MemoryLifecycleOptions _options;
    private readonly SemaphoreSlim _prefetchSlots;
    private readonly SemaphoreSlim _providerSlots;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _cleanupSync = new();
    private readonly TaskCompletionSource<bool> _resourceCleanupCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BoundedCancellationDispatcher _shutdownDispatcher;
    private readonly Func<Task>? _detachedProviderCleanupCheckpoint;
    private readonly object _prefetchAdmission = new();
    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<MemoryRecallReport>>>
        _prefetches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _detachedProviderCalls =
        new();
    private long _nextDetachedProviderCallId;
    private int _closed;
    private int _activeOperations;
    private int _resourcesDisposed;
    private TaskCompletionSource<bool>? _idleCompletion;
    private Task? _shutdownCancellationTask;
    private Task? _resourceCleanupTask;

    public bool? DetachedProviderCallsDrainedOnDispose { get; private set; }

    /// <summary>
    /// Reports whether every active operation and detached provider call has
    /// settled and the lifecycle's internal resources have been released.
    /// </summary>
    public bool ShutdownResourceCleanupCompleted =>
        _resourceCleanupCompletion.Task.IsCompletedSuccessfully;

    public RuntimeMemoryLifecycle(
        IEnumerable<IMemoryProvider> providers,
        IMemoryStore? writeStore = null,
        MemoryLifecycleOptions? options = null)
        : this(
            providers,
            writeStore,
            options,
            BoundedCancellationDispatcher.LifecycleShared,
            detachedProviderCleanupCheckpoint: null)
    {
    }

    internal RuntimeMemoryLifecycle(
        IEnumerable<IMemoryProvider> providers,
        IMemoryStore? writeStore,
        MemoryLifecycleOptions? options,
        BoundedCancellationDispatcher shutdownDispatcher,
        Func<Task>? detachedProviderCleanupCheckpoint = null)
    {
        _options = (options ?? new MemoryLifecycleOptions()).Snapshot();
        _shutdownDispatcher = shutdownDispatcher
                              ?? throw new ArgumentNullException(
                                  nameof(shutdownDispatcher));
        _detachedProviderCleanupCheckpoint =
            detachedProviderCleanupCheckpoint;
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        var materialized = new List<IMemoryProvider>();
        foreach (var provider in providers)
        {
            if (provider is null
                || materialized.Count >= _options.MaxProviders)
            {
                throw new ArgumentException(
                    "The memory provider list is invalid.",
                    nameof(providers));
            }

            materialized.Add(provider);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var providerIds = new List<string>(materialized.Count);
        foreach (var provider in materialized)
        {
            var id = RuntimeGuard.RequiredUtf8(
                provider.ProviderId,
                128,
                nameof(providers));
            if (!ids.Add(id))
            {
                throw new ArgumentException(
                    "Memory provider ids must be unique.",
                    nameof(providers));
            }

            providerIds.Add(id);
        }

        _providers = new ReadOnlyCollection<IMemoryProvider>(materialized);
        _providerIds = new ReadOnlyCollection<string>(providerIds);
        _writeStore = writeStore;
        _prefetchSlots = new SemaphoreSlim(
            _options.MaxConcurrentPrefetches,
            _options.MaxConcurrentPrefetches);
        _providerSlots = new SemaphoreSlim(
            _options.MaxConcurrentProviderCalls,
            _options.MaxConcurrentProviderCalls);
    }

    public async ValueTask<MemoryRecallReport> RecallAsync(
        MemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var shutdownToken = EnterOperation();
        try
        {
            if (query is null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                shutdownToken);
            return await RecallCoreAsync(query, linked.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private async Task<MemoryRecallReport> RecallCoreAsync(
        MemoryQuery query,
        CancellationToken cancellationToken)
    {
        var results = new BoundedCandidateSet(
            _options.MaxRetainedCandidates);
        var failures = new List<string>();
        var recalls = _providers
            .Select(
                (provider, index) => RecallProviderAsync(
                    provider,
                    _providerIds[index],
                    index,
                    query,
                    cancellationToken))
            .ToArray();
        var providerReports = await Task.WhenAll(recalls)
            .ConfigureAwait(false);
        foreach (var report in providerReports.OrderBy(item => item.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (report.Failed)
            {
                failures.Add(report.ProviderId);
                continue;
            }

            foreach (var result in report.Results)
            {
                results.Add(result);
            }
        }

        var selected = Select(results.Ranked, query);
        failures.Sort(StringComparer.Ordinal);
        return new MemoryRecallReport(
            new ReadOnlyCollection<MemorySearchResult>(selected),
            new ReadOnlyCollection<string>(failures));
    }

    private async Task<ProviderRecallReport> RecallProviderAsync(
        IMemoryProvider provider,
        string providerId,
        int index,
        MemoryQuery query,
        CancellationToken cancellationToken)
    {
        var enteredSlot = false;
        try
        {
            enteredSlot = await _providerSlots.WaitAsync(
                    _options.ProviderTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!enteredSlot)
            {
                return ProviderRecallReport.Failure(
                    index,
                    providerId);
            }

            var providerDeadline =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            providerDeadline.CancelAfter(_options.ProviderTimeout);
            Task<IReadOnlyList<MemorySearchResult>> operation;
            try
            {
                operation = Task.Run(
                    async () => await provider
                        .SearchAsync(query, providerDeadline.Token)
                        .ConfigureAwait(false));
            }
            catch
            {
                providerDeadline.Dispose();
                throw;
            }

            var timeout = Task.Delay(_options.ProviderTimeout);
            var callerCancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                state => ((TaskCompletionSource<bool>)state!)
                    .TrySetResult(true),
                callerCancelled);
            var completed = await Task.WhenAny(
                    operation,
                    timeout,
                    callerCancelled.Task)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, operation))
            {
                enteredSlot = false;
                TrackDetachedProviderCall(operation, providerDeadline);
                cancellationToken.ThrowIfCancellationRequested();
                return ProviderRecallReport.Failure(
                    index,
                    providerId);
            }

            providerDeadline.Dispose();
            var recalled = await operation.ConfigureAwait(false);
            if (recalled is null)
            {
                throw new InvalidOperationException(
                    "A memory provider returned null.");
            }

            var recalledSnapshot = SnapshotProviderResults(
                recalled,
                _options.MaxResultsPerProvider);

            var providerResults = new BoundedCandidateSet(
                Math.Min(
                    _options.MaxResultsPerProvider,
                    _options.MaxRetainedCandidates));
            for (var resultIndex = 0;
                 resultIndex < recalledSnapshot.Length;
                 resultIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = recalledSnapshot[resultIndex];
                if (MemoryQueryFilter.Matches(result.Record, query)
                    && Encoding.UTF8.GetByteCount(
                        result.Record.Content.GetRawText())
                    <= query.MaxUtf8Bytes)
                {
                    providerResults.Add(result);
                }
            }

            return ProviderRecallReport.Success(
                index,
                providerId,
                providerResults.Ranked.ToArray());
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            return ProviderRecallReport.Failure(index, providerId);
        }
        finally
        {
            if (enteredSlot)
            {
                _providerSlots.Release();
            }
        }
    }

    private void TrackDetachedProviderCall(
        Task operation,
        CancellationTokenSource deadline)
    {
        long id;
        TaskCompletionSource<bool> start;
        Task cleanup;
        do
        {
            id = Interlocked.Increment(ref _nextDetachedProviderCallId);
            start = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cleanup = CompleteDetachedProviderCallAsync(
                id,
                operation,
                deadline,
                start.Task);
        }
        while (!_detachedProviderCalls.TryAdd(id, cleanup));

        start.TrySetResult(true);
        _ = cleanup.ContinueWith(
            ObserveCompletion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task CompleteDetachedProviderCallAsync(
        long id,
        Task operation,
        CancellationTokenSource deadline,
        Task start)
    {
        await start.ConfigureAwait(false);
        try
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch
            {
                ObserveCompletion(operation);
            }

            if (_detachedProviderCleanupCheckpoint is not null)
            {
                await _detachedProviderCleanupCheckpoint()
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                deadline.Dispose();
                _providerSlots.Release();
            }
            finally
            {
                _detachedProviderCalls.TryRemove(id, out _);
            }
        }
    }

    public void Prefetch(string key, MemoryQuery query)
    {
        RuntimeGuard.RequiredUtf8(key, 256, nameof(key));
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        Lazy<Task<MemoryRecallReport>> prefetch;
        var created = false;
        lock (_prefetchAdmission)
        {
            ThrowIfClosedLocked();
            if (_prefetches.Count >= _options.MaxPrefetchEntries
                && !_prefetches.ContainsKey(key))
            {
                throw new RuntimeContentLimitException(
                    nameof(key),
                    "memory_prefetch_capacity_exceeded",
                    "The memory prefetch cache is full.");
            }

            if (!_prefetches.TryGetValue(key, out prefetch!))
            {
                _activeOperations++;
                var shutdownToken = _shutdown.Token;
                prefetch = new Lazy<Task<MemoryRecallReport>>(
                    () => Task.Run(
                        () => PrefetchCoreAsync(query, shutdownToken)),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _prefetches[key] = prefetch;
                created = true;
            }
        }

        if (created)
        {
            _ = prefetch.Value.ContinueWith(
                ObserveCompletion,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public async ValueTask<MemoryRecallReport?> TakePrefetchedAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        RuntimeGuard.RequiredUtf8(key, 256, nameof(key));
        Lazy<Task<MemoryRecallReport>>? prefetch;
        lock (_prefetchAdmission)
        {
            ThrowIfClosedLocked();
            if (!_prefetches.TryRemove(key, out prefetch))
            {
                return null;
            }
        }

        return await WaitWithCancellationAsync(
                prefetch.Value,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask CommitAsync(
        MemoryRecord record,
        CancellationToken cancellationToken = default)
    {
        var shutdownToken = EnterOperation();
        try
        {
            if (record is null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            if (record.Provenance is null || !record.Provenance.Committed)
            {
                throw new InvalidOperationException(
                    "Runtime-managed memory writes require committed provenance.");
            }

            if (_writeStore is null)
            {
                throw new InvalidOperationException(
                    "No memory write store is configured.");
            }

            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    shutdownToken);
            await _writeStore
                .UpsertAsync(record, linked.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    public async ValueTask<IReadOnlyList<MemoryMutationResult>>
        CommitAtomicBatchAsync(
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        var shutdownToken = EnterOperation();
        try
        {
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    shutdownToken);
            var snapshot = MemoryBatchValidator.Snapshot(
                mutations,
                linked.Token);
            foreach (var mutation in snapshot)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (mutation.Kind == MemoryMutationKind.Upsert
                    && (mutation.Record?.Provenance is null
                        || !mutation.Record.Provenance.Committed))
                {
                    throw new InvalidOperationException(
                        "Runtime-managed memory writes require committed "
                        + "provenance.");
                }
            }

            if (_writeStore is null)
            {
                throw new InvalidOperationException(
                    "No memory write store is configured.");
            }

            if (_writeStore is not IAtomicMemoryBatchStore batchStore)
            {
                throw new MemoryBatchNotSupportedException();
            }

            var rawResults = await batchStore.ApplyAtomicBatchAsync(
                    snapshot,
                    linked.Token)
                .ConfigureAwait(false);
            return SnapshotBatchResults(rawResults, snapshot);
        }
        finally
        {
            ExitOperation();
        }
    }

    public async ValueTask<IReadOnlyList<MemoryMutationResult>>
        CommitIdempotentAtomicBatchAsync(
            string commitId,
            IReadOnlyList<MemoryMutation> mutations,
            CancellationToken cancellationToken = default)
    {
        var shutdownToken = EnterOperation();
        try
        {
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    shutdownToken);
            var snapshot = MemoryBatchValidator.Snapshot(
                mutations,
                linked.Token);
            foreach (var mutation in snapshot)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (mutation.Kind == MemoryMutationKind.Upsert
                    && (mutation.Record?.Provenance is null
                        || !mutation.Record.Provenance.Committed))
                {
                    throw new InvalidOperationException(
                        "Runtime-managed memory writes require committed "
                        + "provenance.");
                }
            }

            if (_writeStore is null)
            {
                throw new InvalidOperationException(
                    "No memory write store is configured.");
            }

            if (_writeStore is not IIdempotentAtomicMemoryBatchStore
                batchStore)
            {
                throw new MemoryIdempotentBatchNotSupportedException();
            }

            var rawResults =
                await batchStore.ApplyIdempotentAtomicBatchAsync(
                        commitId,
                        snapshot,
                        linked.Token)
                    .ConfigureAwait(false);
            return SnapshotBatchResults(rawResults, snapshot);
        }
        finally
        {
            ExitOperation();
        }
    }

    private static IReadOnlyList<MemoryMutationResult> SnapshotBatchResults(
        IReadOnlyList<MemoryMutationResult>? results,
        IReadOnlyList<MemoryMutation> mutations)
    {
        if (results is null)
        {
            throw new InvalidDataException(
                "The memory batch store returned no mutation results.");
        }

        var count = results.Count;
        if (count != mutations.Count)
        {
            throw new InvalidDataException(
                "The memory batch store returned a different result count "
                + "than the submitted mutation count.");
        }

        var snapshot = new MemoryMutationResult[count];
        for (var index = 0; index < count; index++)
        {
            var result = results[index]
                         ?? throw new InvalidDataException(
                             $"Memory batch result {index} is null.");
            var mutation = mutations[index];
            if (result.Kind != mutation.Kind
                || !string.Equals(
                    result.MemoryId,
                    mutation.MemoryId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Memory batch result {index} does not match its "
                    + "submitted mutation.");
            }

            snapshot[index] = new MemoryMutationResult(
                result.Kind,
                result.MemoryId,
                result.Changed);
        }

        return new ReadOnlyCollection<MemoryMutationResult>(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            BoundedCancellationDispatcher.CancellationDispatchReservation?
                cancellationReservation = null;
            if (_shutdownCancellationTask is null
                && !_shutdownDispatcher.TryReserve(
                    out cancellationReservation))
            {
                DetachedProviderCallsDrainedOnDispose = false;
                throw new InvalidOperationException(
                    "Memory shutdown cancellation capacity is exhausted.");
            }

            Task idle;
            lock (_prefetchAdmission)
            {
                if (_closed == 0)
                {
                    _closed = 1;
                    _prefetches.Clear();
                }

                idle = _activeOperations == 0
                    ? Task.CompletedTask
                    : (_idleCompletion ??= new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously))
                        .Task;
            }

            if (_shutdownCancellationTask is null)
            {
                var acceptedReservation = cancellationReservation!;
                _shutdownCancellationTask =
                    acceptedReservation.DispatchAsync(_shutdown);
                _ = _shutdownCancellationTask.ContinueWith(
                    _ => acceptedReservation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var cancellation = _shutdownCancellationTask
                               ?? Task.CompletedTask;
            var operationsIdle = Task.WhenAll(idle, cancellation);
            var timeout = Task.Delay(_options.ShutdownTimeout);
            var completed = await Task.WhenAny(operationsIdle, timeout)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completed, operationsIdle))
            {
                DetachedProviderCallsDrainedOnDispose = false;
                EnsureResourceCleanup(operationsIdle);
                return;
            }

            ObserveCompletion(operationsIdle);
            var detached = _detachedProviderCalls.Values.ToArray();
            var drain = Task.WhenAll(detached);
            var remaining = _options.ShutdownTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero
                || !ReferenceEquals(
                    await Task.WhenAny(drain, Task.Delay(remaining))
                        .ConfigureAwait(false),
                    drain))
            {
                DetachedProviderCallsDrainedOnDispose = false;
                EnsureResourceCleanup(Task.CompletedTask);
                return;
            }

            ObserveCompletion(drain);
            DetachedProviderCallsDrainedOnDispose = true;
            CleanupResources();
        }
        finally
        {
            _stopGate.Release();
        }
    }

    /// <summary>
    /// Initiates bounded disposal and then waits for the actual operation and
    /// detached-provider drain. Caller cancellation stops only this wait; it
    /// does not cancel the shared cleanup.
    /// </summary>
    public async ValueTask WaitForShutdownDrainAsync(
        CancellationToken cancellationToken = default)
    {
        var boundedDispose = DisposeAsync().AsTask();
        ObserveBackground(boundedDispose);
        await WaitWithCancellationAsync(
                boundedDispose,
                cancellationToken)
            .ConfigureAwait(false);
        await WaitWithCancellationAsync(
                _resourceCleanupCompletion.Task,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MemoryRecallReport> PrefetchCoreAsync(
        MemoryQuery query,
        CancellationToken shutdownToken)
    {
        var enteredSlot = false;
        try
        {
            await _prefetchSlots.WaitAsync(shutdownToken)
                .ConfigureAwait(false);
            enteredSlot = true;
            try
            {
                return await RecallCoreAsync(query, shutdownToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (shutdownToken.IsCancellationRequested)
            {
                return new MemoryRecallReport(
                    Array.Empty<MemorySearchResult>(),
                    new ReadOnlyCollection<string>(
                        _providers
                            .Select((_, index) => _providerIds[index])
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToArray()));
            }
        }
        finally
        {
            if (enteredSlot)
            {
                _prefetchSlots.Release();
            }

            ExitOperation();
        }
    }

    private static List<MemorySearchResult> Select(
        IEnumerable<MemorySearchResult> results,
        MemoryQuery query)
    {
        var selected = new List<MemorySearchResult>();
        var bytes = 0;
        foreach (var result in results)
        {
            var size = Encoding.UTF8.GetByteCount(
                result.Record.Content.GetRawText());
            if (selected.Count >= query.MaxResults)
            {
                break;
            }

            if (checked(bytes + size) > query.MaxUtf8Bytes)
            {
                continue;
            }

            selected.Add(result);
            bytes += size;
        }

        return selected;
    }

    private static MemorySearchResult[] SnapshotProviderResults(
        IReadOnlyList<MemorySearchResult> recalled,
        int maximumCount)
    {
        int count;
        try
        {
            count = recalled.Count;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                "A memory provider returned an unreadable candidate count.",
                exception);
        }

        if (count < 0)
        {
            throw new InvalidDataException(
                "A memory provider returned a negative candidate count.");
        }

        if (count > maximumCount)
        {
            throw new RuntimeContentLimitException(
                nameof(recalled),
                "memory_provider_result_count_exceeded",
                "A memory provider returned too many candidates.");
        }

        var snapshots = new MemorySearchResult[count];
        for (var index = 0; index < count; index++)
        {
            MemorySearchResult? result;
            try
            {
                result = recalled[index];
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                throw new InvalidDataException(
                    "A memory provider result collection did not match "
                    + "its declared count.",
                    exception);
            }

            if (result?.Record is null)
            {
                throw new InvalidDataException(
                    "A memory provider returned an invalid candidate.");
            }

            var record = result.Record;
            var recordSnapshot = new MemoryRecord(
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
            snapshots[index] = new MemorySearchResult(
                recordSnapshot,
                result.Score);
        }

        return snapshots;
    }

    private sealed class BoundedCandidateSet
    {
        private readonly int _capacity;
        private readonly Dictionary<string, MemorySearchResult> _byId =
            new(StringComparer.Ordinal);
        private readonly SortedSet<MemorySearchResult> _ranked =
            new(MemorySearchResultComparer.Instance);

        public BoundedCandidateSet(int capacity)
        {
            _capacity = capacity;
        }

        public IEnumerable<MemorySearchResult> Ranked => _ranked;

        public void Add(MemorySearchResult candidate)
        {
            var memoryId = candidate.Record.MemoryId;
            if (_byId.TryGetValue(memoryId, out var current))
            {
                if (MemorySearchResultComparer.Instance.Compare(
                        candidate,
                        current) >= 0)
                {
                    return;
                }

                _ranked.Remove(current);
                _byId.Remove(memoryId);
            }

            if (_ranked.Count >= _capacity)
            {
                var worst = _ranked.Max!;
                if (MemorySearchResultComparer.Instance.Compare(
                        candidate,
                        worst) >= 0)
                {
                    return;
                }

                _ranked.Remove(worst);
                _byId.Remove(worst.Record.MemoryId);
            }

            _ranked.Add(candidate);
            _byId.Add(memoryId, candidate);
        }
    }

    private sealed class MemorySearchResultComparer
        : IComparer<MemorySearchResult>
    {
        public static MemorySearchResultComparer Instance { get; } = new();

        public int Compare(MemorySearchResult? left, MemorySearchResult? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

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
    }

    private static async Task<T> WaitWithCancellationAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await task.ConfigureAwait(false);
        }

        var cancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancelled);
        if (task != await Task.WhenAny(task, cancelled.Task)
                .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await task.ConfigureAwait(false);
    }

    private static async Task WaitWithCancellationAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancelled);
        if (task != await Task.WhenAny(task, cancelled.Task)
                .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        await task.ConfigureAwait(false);
    }

    private CancellationToken EnterOperation()
    {
        lock (_prefetchAdmission)
        {
            ThrowIfClosedLocked();
            _activeOperations++;
            return _shutdown.Token;
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource<bool>? idle = null;
        lock (_prefetchAdmission)
        {
            _activeOperations--;
            if (_activeOperations < 0)
            {
                throw new InvalidOperationException(
                    "The memory operation count became invalid.");
            }

            if (_closed != 0 && _activeOperations == 0)
            {
                idle = _idleCompletion;
            }
        }

        idle?.TrySetResult(true);
    }

    private void ThrowIfClosedLocked()
    {
        if (_closed != 0)
        {
            throw new ObjectDisposedException(nameof(RuntimeMemoryLifecycle));
        }
    }

    private void CleanupResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        try
        {
            _prefetchSlots.Dispose();
            _providerSlots.Dispose();
            _shutdown.Dispose();
            _resourceCleanupCompletion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            _resourceCleanupCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task CleanupAfterOperationsAsync(Task operationsIdle)
    {
        try
        {
            await operationsIdle.ConfigureAwait(false);
        }
        catch
        {
            ObserveCompletion(operationsIdle);
        }

        var detached = _detachedProviderCalls.Values.ToArray();
        var drain = Task.WhenAll(detached);
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch
        {
            ObserveCompletion(drain);
        }

        CleanupResources();
    }

    private void EnsureResourceCleanup(Task operationsIdle)
    {
        Task cleanup;
        var created = false;
        lock (_cleanupSync)
        {
            if (_resourceCleanupTask is null)
            {
                _resourceCleanupTask =
                    CleanupAfterOperationsAsync(operationsIdle);
                created = true;
            }

            cleanup = _resourceCleanupTask;
        }

        if (created)
        {
            ObserveBackground(cleanup);
        }
    }

    private static void ObserveCompletion(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    private static void ObserveBackground(Task task)
    {
        _ = task.ContinueWith(
            ObserveCompletion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class ProviderRecallReport
    {
        private ProviderRecallReport(
            int index,
            string providerId,
            bool failed,
            IReadOnlyList<MemorySearchResult> results)
        {
            Index = index;
            ProviderId = providerId;
            Failed = failed;
            Results = results;
        }

        public int Index { get; }

        public string ProviderId { get; }

        public bool Failed { get; }

        public IReadOnlyList<MemorySearchResult> Results { get; }

        public static ProviderRecallReport Failure(
            int index,
            string providerId)
        {
            return new ProviderRecallReport(
                index,
                providerId,
                failed: true,
                Array.Empty<MemorySearchResult>());
        }

        public static ProviderRecallReport Success(
            int index,
            string providerId,
            IReadOnlyList<MemorySearchResult> results)
        {
            return new ProviderRecallReport(
                index,
                providerId,
                failed: false,
                results);
        }
    }
}

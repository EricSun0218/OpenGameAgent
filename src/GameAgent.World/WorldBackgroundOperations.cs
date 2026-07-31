using System.Collections.Concurrent;

namespace GameAgent.World;

public enum WorldBackgroundOperationKind
{
    TriggerPlanning = 0,
    InteractionQuery = 1,
    InteractionPlanning = 2,
    PlanExecution = 3
}

public static class WorldBackgroundOperationReasonCodes
{
    public const string QueueAtCapacity =
        "world_background_queue_at_capacity";

    public const string DuplicateOperation =
        "world_background_operation_duplicate";

    public const string QueueStopped =
        "world_background_queue_stopped";
}

public sealed class WorldBackgroundOperationResult
{
    internal WorldBackgroundOperationResult(
        string operationId,
        WorldBackgroundOperationKind kind,
        object? value,
        Exception? exception,
        bool canceled)
    {
        OperationId = operationId;
        Kind = kind;
        Value = value;
        Exception = exception;
        IsCanceled = canceled;
    }

    public string OperationId { get; }

    public WorldBackgroundOperationKind Kind { get; }

    public bool Succeeded =>
        !IsCanceled && Exception is null && Value is not null;

    public bool IsCanceled { get; }

    public object? Value { get; }

    public Exception? Exception { get; }
}

public sealed class WorldBackgroundShutdownIncompleteException
    : OperationCanceledException
{
    internal WorldBackgroundShutdownIncompleteException(
        CancellationToken cancellationToken,
        IReadOnlyList<string> outstandingOperationIds,
        IReadOnlyList<string> authoritativeOperationIds)
        : base(
            "World background shutdown did not settle every admitted operation.",
            cancellationToken)
    {
        OutstandingOperationIds =
            new System.Collections.ObjectModel.ReadOnlyCollection<string>(
                outstandingOperationIds.ToArray());
        AuthoritativeOperationIds =
            new System.Collections.ObjectModel.ReadOnlyCollection<string>(
                authoritativeOperationIds.ToArray());
    }

    public IReadOnlyList<string> OutstandingOperationIds { get; }

    public IReadOnlyList<string> AuthoritativeOperationIds { get; }
}

/// <summary>
/// Bounded engine-neutral background lane. Completion callbacks run only when
/// an engine explicitly drains the queue from its main-thread update hook.
/// </summary>
public sealed class WorldBackgroundOperationQueue : IAsyncDisposable
{
    public const int CancellationCallbackConcurrencyLimit = 8;

    public const int BackgroundWorkConcurrencyLimit = 16;

    public const int BackgroundWorkOutstandingLimit = 4_096;

    private readonly object _lifecycleGate = new();
    private readonly ConcurrentDictionary<string, Registration> _operations =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<WorldBackgroundOperationResult>
        _completed = new();
    private readonly int _capacity;
    private int _acceptedCount;
    private int _stopped;
    private Task<IReadOnlyList<WorldBackgroundOperationResult>>?
        _shutdownTask;
    private Task? _disposeTask;

    public WorldBackgroundOperationQueue(int capacity = 256)
    {
        if (capacity is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int Capacity => _capacity;

    /// <summary>
    /// Includes running operations and completed results not yet pumped.
    /// </summary>
    public int OutstandingCount => Volatile.Read(ref _acceptedCount);

    public int CompletedCount => _completed.Count;

    public bool TrySchedule(
        string operationId,
        WorldBackgroundOperationKind kind,
        Func<CancellationToken, ValueTask<object?>> operation,
        out string? rejectionReason,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = WorldValidation.Required(
            operationId,
            nameof(operationId),
            512);
        if (!Enum.IsDefined(typeof(WorldBackgroundOperationKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                rejectionReason =
                    WorldBackgroundOperationReasonCodes.QueueStopped;
                return false;
            }

            var accepted = Interlocked.Increment(ref _acceptedCount);
            if (accepted > _capacity)
            {
                Interlocked.Decrement(ref _acceptedCount);
                rejectionReason =
                    WorldBackgroundOperationReasonCodes.QueueAtCapacity;
                return false;
            }

            if (!WorldCancellationDispatcher.TryCreateOwner(
                    out var cancellation)
                || cancellation is null)
            {
                Interlocked.Decrement(ref _acceptedCount);
                rejectionReason =
                    WorldBackgroundOperationReasonCodes.QueueAtCapacity;
                return false;
            }

            var registration = new Registration(kind, cancellation);
            registration.AttachExternalCancellation(cancellationToken);
            if (!_operations.TryAdd(normalizedId, registration))
            {
                registration.Cleanup();
                Interlocked.Decrement(ref _acceptedCount);
                rejectionReason =
                    WorldBackgroundOperationReasonCodes.DuplicateOperation;
                return false;
            }

            if (!WorldBackgroundWorkDispatcher.TryDispatch(
                    () => RunAsync(
                        normalizedId,
                        kind,
                        operation,
                        registration),
                    out var task)
                || task is null)
            {
                _operations.TryRemove(normalizedId, out _);
                registration.Cleanup();
                Interlocked.Decrement(ref _acceptedCount);
                rejectionReason =
                    WorldBackgroundOperationReasonCodes.QueueAtCapacity;
                return false;
            }

            registration.Task = task;
            rejectionReason = null;
            return true;
        }
    }

    public bool TryCancel(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return false;
        }

        if (!_operations.TryGetValue(operationId, out var registration))
        {
            return false;
        }

        try
        {
            return registration.RequestCancellation();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public int Drain(
        int maximumResults,
        Action<WorldBackgroundOperationResult> publish)
    {
        if (maximumResults < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        if (publish is null)
        {
            throw new ArgumentNullException(nameof(publish));
        }

        var drained = 0;
        while (drained < maximumResults
               && _completed.TryDequeue(out var result))
        {
            if (_operations.TryRemove(
                    result.OperationId,
                    out var registration))
            {
                registration.Cleanup();
                Interlocked.Decrement(ref _acceptedCount);
            }

            publish(result);
            drained++;
        }

        return drained;
    }

    /// <summary>
    /// Closes admission, requests cancellation without running callbacks on
    /// the caller, and waits for every accepted operation to settle. Returned
    /// results have not been published by <see cref="Drain"/> and must be
    /// applied by the engine on its main thread. If the wait is cancelled,
    /// the exception retains operation IDs whose handler/store ownership
    /// cannot yet be released.
    /// </summary>
    public ValueTask<IReadOnlyList<WorldBackgroundOperationResult>>
        ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<WorldBackgroundOperationResult>> shutdown;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopped) == 2)
            {
                throw new ObjectDisposedException(
                    nameof(WorldBackgroundOperationQueue));
            }

            if (_shutdownTask is null)
            {
                Volatile.Write(ref _stopped, 1);
                var registrations = _operations.ToArray();
                foreach (var pair in registrations)
                {
                    _ = pair.Value.RequestCancellation();
                }

                _shutdownTask = ShutdownCoreAsync(registrations);
            }

            shutdown = _shutdownTask;
        }

        return new ValueTask<IReadOnlyList<
            WorldBackgroundOperationResult>>(
            WaitForShutdownAsync(shutdown, cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            Volatile.Write(ref _stopped, 2);
            disposal = DisposeCore();
            _disposeTask = disposal;
        }

        return new ValueTask(disposal);
    }

    private Task DisposeCore()
    {
        while (_completed.TryDequeue(out _))
        {
        }

        foreach (var pair in _operations.ToArray())
        {
            if (_operations.TryRemove(pair.Key, out var registration))
            {
                Interlocked.Decrement(ref _acceptedCount);
                _ = registration.RequestCancellation();
                registration.ObserveAndCleanup();
            }
        }

        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<WorldBackgroundOperationResult>>
        ShutdownCoreAsync(
            IReadOnlyList<KeyValuePair<string, Registration>>
                registrations)
    {
        await Task.WhenAll(
                registrations.Select(
                    pair => pair.Value.Task ?? Task.CompletedTask))
            .ConfigureAwait(false);
        var results = new List<WorldBackgroundOperationResult>(
            registrations.Count);
        while (_completed.TryDequeue(out var result))
        {
            if (_operations.TryRemove(
                    result.OperationId,
                    out var registration))
            {
                registration.Cleanup();
                Interlocked.Decrement(ref _acceptedCount);
            }

            results.Add(result);
        }

        foreach (var pair in registrations)
        {
            if (_operations.TryRemove(pair.Key, out var registration))
            {
                registration.Cleanup();
                Interlocked.Decrement(ref _acceptedCount);
            }
        }

        return new System.Collections.ObjectModel.ReadOnlyCollection<
            WorldBackgroundOperationResult>(results);
    }

    private async Task<IReadOnlyList<WorldBackgroundOperationResult>>
        WaitForShutdownAsync(
            Task<IReadOnlyList<WorldBackgroundOperationResult>> shutdown,
            CancellationToken cancellationToken)
    {
        try
        {
            await WaitWithCancellationAsync(
                    shutdown,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            IReadOnlyList<string> outstanding;
            IReadOnlyList<string> authoritative;
            lock (_lifecycleGate)
            {
                outstanding = _operations.Keys
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                authoritative = _operations
                    .Where(
                        pair => pair.Value.Kind
                                == WorldBackgroundOperationKind.PlanExecution)
                    .Select(pair => pair.Key)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
            }

            throw new WorldBackgroundShutdownIncompleteException(
                cancellationToken,
                outstanding,
                authoritative);
        }

        return await shutdown.ConfigureAwait(false);
    }

    private static async Task WaitWithCancellationAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        if (task.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var canceled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state =>
                ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            canceled);
        if (!ReferenceEquals(
                await Task.WhenAny(task, canceled.Task)
                    .ConfigureAwait(false),
                task))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        await task.ConfigureAwait(false);
    }

    private async Task RunAsync(
        string operationId,
        WorldBackgroundOperationKind kind,
        Func<CancellationToken, ValueTask<object?>> operation,
        Registration registration)
    {
        object? value = null;
        Exception? failure = null;
        if (!registration.CancellationWasRequested)
        {
            try
            {
                value = await operation(registration.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        var cancellationWon = registration.MarkOperationCompleted();
        var preservesAuthoritativeResult =
            kind == WorldBackgroundOperationKind.PlanExecution
            && failure is null
            && value is not null;
        var canceled = cancellationWon && !preservesAuthoritativeResult;
        if (canceled)
        {
            value = null;
            failure = null;
        }
        else if (failure is null && value is null)
        {
            failure = new InvalidOperationException(
                "A background world operation returned null.");
        }

        var result = new WorldBackgroundOperationResult(
            operationId,
            kind,
            value,
            failure,
            canceled);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopped) != 2)
            {
                _completed.Enqueue(result);
            }
        }
    }

    private sealed class Registration
    {
        private const int Running = 0;
        private const int CancelRequested = 1;
        private const int Completed = 2;
        private const int CompletedAfterCancellation = 3;

        private readonly WorldCancellationDispatcher.CancellationOwner
            _cancellation;
        private CancellationTokenRegistration _externalCancellation;
        private int _cleaned;
        private int _lifecycle;

        public Registration(
            WorldBackgroundOperationKind kind,
            WorldCancellationDispatcher.CancellationOwner cancellation)
        {
            Kind = kind;
            _cancellation = cancellation
                            ?? throw new ArgumentNullException(
                                nameof(cancellation));
        }

        public WorldBackgroundOperationKind Kind { get; }

        public CancellationToken CancellationToken =>
            _cancellation.Token;

        public bool CancellationWasRequested =>
            Volatile.Read(ref _lifecycle)
            is CancelRequested or CompletedAfterCancellation;

        public Task? Task { get; set; }

        public void AttachExternalCancellation(
            CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return;
            }

            _externalCancellation = cancellationToken.Register(
                static state =>
                {
                    _ = ((Registration)state!).RequestCancellation();
                },
                this);
        }

        public bool RequestCancellation()
        {
            while (true)
            {
                var state = Volatile.Read(ref _lifecycle);
                if (state == CancelRequested)
                {
                    return true;
                }

                if (state is Completed or CompletedAfterCancellation)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(
                        ref _lifecycle,
                        CancelRequested,
                        Running) == Running)
                {
                    _ = _cancellation.Request();
                    return true;
                }
            }
        }

        public bool MarkOperationCompleted()
        {
            while (true)
            {
                var state = Volatile.Read(ref _lifecycle);
                if (state == Running)
                {
                    if (Interlocked.CompareExchange(
                            ref _lifecycle,
                            Completed,
                            Running) != Running)
                    {
                        continue;
                    }

                    _externalCancellation.Dispose();
                    return false;
                }

                if (state == CancelRequested)
                {
                    if (Interlocked.CompareExchange(
                            ref _lifecycle,
                            CompletedAfterCancellation,
                            CancelRequested) != CancelRequested)
                    {
                        continue;
                    }

                    _externalCancellation.Dispose();
                    return true;
                }

                return state == CompletedAfterCancellation;
            }
        }

        public void ObserveAndCleanup()
        {
            var task = Task;
            if (task is null || task.IsCompleted)
            {
                _ = task?.Exception;
                Cleanup();
                return;
            }

            _ = task.ContinueWith(
                static (completed, state) =>
                {
                    _ = completed.Exception;
                    ((Registration)state!).Cleanup();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void Cleanup()
        {
            if (Interlocked.Exchange(ref _cleaned, 1) != 0)
            {
                return;
            }

            _externalCancellation.Dispose();
            _cancellation.Close();
        }
    }
}

internal static class WorldBackgroundWorkDispatcher
{
    private const int MaximumConcurrentOperations =
        WorldBackgroundOperationQueue.BackgroundWorkConcurrencyLimit;
    private const int MaximumOutstandingOperations =
        WorldBackgroundOperationQueue.BackgroundWorkOutstandingLimit;

    private static readonly ConcurrentQueue<WorkItem> Pending = new();

    private static readonly SemaphoreSlim PendingSignal =
        new(0, MaximumOutstandingOperations);

    private static readonly SemaphoreSlim OutstandingCapacity =
        new(
            MaximumOutstandingOperations,
            MaximumOutstandingOperations);

    private static readonly Task[] Workers = StartWorkers();

    public static Task Dispatch(Func<Task> operation)
    {
        if (!TryDispatch(operation, out var completion)
            || completion is null)
        {
            throw new InvalidOperationException(
                WorldBackgroundOperationReasonCodes.QueueAtCapacity);
        }

        return completion;
    }

    public static bool TryDispatch(
        Func<Task> operation,
        out Task? completion)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (!OutstandingCapacity.Wait(0))
        {
            completion = null;
            return false;
        }

        try
        {
            var item = new WorkItem(operation);
            Pending.Enqueue(item);
            PendingSignal.Release();
            completion = item.Completion;
            return true;
        }
        catch
        {
            OutstandingCapacity.Release();
            throw;
        }
    }

    private static Task[] StartWorkers()
    {
        var workers = new Task[MaximumConcurrentOperations];
        for (var index = 0; index < workers.Length; index++)
        {
            workers[index] = Task.Run(WorkerLoopAsync);
        }

        return workers;
    }

    private static async Task WorkerLoopAsync()
    {
        while (true)
        {
            await PendingSignal.WaitAsync().ConfigureAwait(false);
            if (Pending.TryDequeue(out var item))
            {
                await item.RunAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class WorkItem
    {
        private readonly Func<Task> _operation;
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<Task> operation)
        {
            _operation = operation;
        }

        public Task Completion => _completion.Task;

        public async Task RunAsync()
        {
            Exception? failure = null;
            try
            {
                await _operation().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                OutstandingCapacity.Release();
            }

            if (failure is null)
            {
                _completion.TrySetResult(null);
            }
            else
            {
                _completion.TrySetException(failure);
            }
        }
    }
}

internal static class WorldCancellationDispatcher
{
    private const int MaximumConcurrentCallbacks =
        WorldBackgroundOperationQueue
            .CancellationCallbackConcurrencyLimit;
    private const int MaximumOwners = 65_536;

    private static readonly SemaphoreSlim OwnerCapacity =
        new(MaximumOwners, MaximumOwners);

    private static readonly ConcurrentQueue<CancellationOwner> Pending =
        new();

    private static readonly SemaphoreSlim PendingSignal =
        new(0, MaximumOwners);

    private static readonly Thread[] Workers = StartWorkers();

    public static bool TryCreateOwner(
        out CancellationOwner? owner)
    {
        if (!OwnerCapacity.Wait(0))
        {
            owner = null;
            return false;
        }

        try
        {
            owner = new CancellationOwner();
            return true;
        }
        catch
        {
            OwnerCapacity.Release();
            throw;
        }
    }

    private static Thread[] StartWorkers()
    {
        var workers = new Thread[MaximumConcurrentCallbacks];
        for (var index = 0; index < workers.Length; index++)
        {
            var worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "GameAgent.World.Cancellation." + index
            };
            workers[index] = worker;
            worker.Start();
        }

        return workers;
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            PendingSignal.Wait();
            if (!Pending.TryDequeue(out var owner))
            {
                continue;
            }

            owner.ExecuteCancellation();
        }
    }

    internal sealed class CancellationOwner
    {
        private const int Open = 0;
        private const int QueuedOrRunning = 1;
        private const int CancellationDelivered = 2;
        private const int QueuedAndClosing = 3;
        private const int Closed = 4;

        private readonly CancellationTokenSource _source = new();
        private int _state;

        public CancellationToken Token => _source.Token;

        public bool Request()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                switch (state)
                {
                    case Open:
                        if (Interlocked.CompareExchange(
                                ref _state,
                                QueuedOrRunning,
                                Open) != Open)
                        {
                            continue;
                        }

                        Pending.Enqueue(this);
                        PendingSignal.Release();
                        return true;

                    case QueuedOrRunning:
                    case CancellationDelivered:
                    case QueuedAndClosing:
                        return true;

                    default:
                        return false;
                }
            }
        }

        public void Close()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                switch (state)
                {
                    case Open:
                    case CancellationDelivered:
                        if (Interlocked.CompareExchange(
                                ref _state,
                                Closed,
                                state) != state)
                        {
                            continue;
                        }

                        Release();
                        return;

                    case QueuedOrRunning:
                        if (Interlocked.CompareExchange(
                                ref _state,
                                QueuedAndClosing,
                                QueuedOrRunning)
                            != QueuedOrRunning)
                        {
                            continue;
                        }

                        return;

                    case QueuedAndClosing:
                    case Closed:
                        return;
                }
            }
        }

        internal void ExecuteCancellation()
        {
            try
            {
                _source.Cancel();
            }
            catch
            {
                // Host callbacks cannot escape an isolated fixed worker.
            }

            while (true)
            {
                var state = Volatile.Read(ref _state);
                if (state == QueuedOrRunning)
                {
                    if (Interlocked.CompareExchange(
                            ref _state,
                            CancellationDelivered,
                            QueuedOrRunning)
                        == QueuedOrRunning)
                    {
                        return;
                    }

                    continue;
                }

                if (state == QueuedAndClosing)
                {
                    if (Interlocked.CompareExchange(
                            ref _state,
                            Closed,
                            QueuedAndClosing)
                        == QueuedAndClosing)
                    {
                        Release();
                        return;
                    }

                    continue;
                }

                return;
            }
        }

        private void Release()
        {
            try
            {
                _source.Dispose();
            }
            finally
            {
                OwnerCapacity.Release();
            }
        }
    }
}

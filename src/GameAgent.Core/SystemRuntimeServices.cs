using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace GameAgent.Core;

public sealed class SystemRuntimeClock : IRuntimeClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class GuidRuntimeIdGenerator : IRuntimeIdGenerator
{
    public string NewId(string category)
    {
        if (string.IsNullOrWhiteSpace(category)
            || category.Length > 64)
        {
            throw new ArgumentException(
                "Runtime id category is invalid.",
                nameof(category));
        }

        return category + "-" + Guid.NewGuid().ToString("N");
    }
}

internal sealed class MonotonicDeadline
{
    private readonly long _startedAt;
    private readonly TimeSpan _duration;

    private MonotonicDeadline(TimeSpan duration)
    {
        _duration = duration > TimeSpan.Zero
            ? duration
            : TimeSpan.Zero;
        _startedAt = Stopwatch.GetTimestamp();
    }

    public TimeSpan Remaining
    {
        get
        {
            if (_duration <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var elapsedTicks = Stopwatch.GetTimestamp() - _startedAt;
            if (elapsedTicks <= 0)
            {
                return _duration;
            }

            var elapsedSeconds =
                (double)elapsedTicks / Stopwatch.Frequency;
            var remainingSeconds =
                _duration.TotalSeconds - elapsedSeconds;
            return remainingSeconds > 0
                ? TimeSpan.FromSeconds(remainingSeconds)
                : TimeSpan.Zero;
        }
    }

    public static MonotonicDeadline Start(TimeSpan duration)
    {
        return new MonotonicDeadline(duration);
    }
}

internal sealed class BoundedCancellationDispatcher
{
    internal const int DefaultCapacity = 64;

    private readonly SemaphoreSlim _capacity;
    private readonly CancellationWorkerClass _workerClass;
    private int _reservations;

    public BoundedCancellationDispatcher(
        int capacity = DefaultCapacity,
        CancellationWorkerClass workerClass = CancellationWorkerClass.DataPlane)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = new SemaphoreSlim(capacity, capacity);
        _workerClass = workerClass;
    }

    public static BoundedCancellationDispatcher Shared { get; } = new(
        workerClass: CancellationWorkerClass.DataPlane);

    public static BoundedCancellationDispatcher LifecycleShared { get; } =
        new(workerClass: CancellationWorkerClass.ControlPlane);

    public static BoundedCancellationDispatcher ExecutionPolicyShared
    {
        get;
    } = new(workerClass: CancellationWorkerClass.ExecutionPolicy);

    public static BoundedCancellationDispatcher AgentLifecycleShared
    {
        get;
    } = new(workerClass: CancellationWorkerClass.AgentLifecycle);

    public static BoundedCancellationDispatcher ConversationContextShared
    {
        get;
    } = new(workerClass: CancellationWorkerClass.ConversationContext);

    public static BoundedCancellationDispatcher MemoryExtensionShared
    {
        get;
    } = new(workerClass: CancellationWorkerClass.MemoryExtension);

    public static BoundedCancellationDispatcher SkillContentResolverShared
    {
        get;
    } = new(workerClass: CancellationWorkerClass.SkillContentResolver);

    public static BoundedCancellationDispatcher SimpleCompletionShared
    {
        get;
    } = new(workerClass: CancellationWorkerClass.SimpleCompletion);

    internal int ActiveReservations =>
        Volatile.Read(ref _reservations);

    public bool TryReserve(
        out CancellationDispatchReservation? reservation)
    {
        if (!_capacity.Wait(0))
        {
            reservation = null;
            return false;
        }

        Interlocked.Increment(ref _reservations);
        reservation = new CancellationDispatchReservation(this);
        return true;
    }

    public async ValueTask<CancellationDispatchReservation> ReserveAsync(
        CancellationToken cancellationToken = default)
    {
        await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _reservations);
        return new CancellationDispatchReservation(this);
    }

    private void Release()
    {
        Interlocked.Decrement(ref _reservations);
        _capacity.Release();
    }

    internal sealed class CancellationDispatchReservation :
        IDisposable
    {
        private readonly BoundedCancellationDispatcher _owner;
        private int _state;

        internal CancellationDispatchReservation(
            BoundedCancellationDispatcher owner)
        {
            _owner = owner;
        }

        public Task DispatchAsync(CancellationTokenSource cancellation)
        {
            if (!TryDispatch(cancellation, out var dispatch))
            {
                return Task.FromException(
                    new InvalidOperationException(
                        "Cancellation worker capacity is exhausted."));
            }

            return dispatch;
        }

        internal bool TryDispatch(
            CancellationTokenSource cancellation,
            out Task dispatch)
        {
            if (cancellation is null)
            {
                throw new ArgumentNullException(nameof(cancellation));
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "A cancellation reservation can be dispatched only once.");
            }

            try
            {
                if (!ProcessCancellationWorkerPool.TryQueue(
                        _owner._workerClass,
                        () =>
                        {
                            SafeCancel(cancellation);
                            return true;
                        },
                        out var queued))
                {
                    Dispose();
                    dispatch = Task.CompletedTask;
                    return false;
                }

                dispatch = queued;
                return true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Task<bool> DispatchAsync(Func<bool> cancellation)
        {
            if (cancellation is null)
            {
                throw new ArgumentNullException(nameof(cancellation));
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "A cancellation reservation can be dispatched only once.");
            }

            try
            {
                if (!ProcessCancellationWorkerPool.TryQueue(
                    _owner._workerClass,
                    () =>
                    {
                        try
                        {
                            return cancellation();
                        }
                        catch
                        {
                            return false;
                        }
                    },
                    out var dispatch))
                {
                    Dispose();
                    return Task.FromResult(false);
                }

                return dispatch;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref _state, 2);
            if (previous != 2)
            {
                _owner.Release();
            }
        }

        private static void SafeCancel(
            CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch
            {
                // Host callbacks cannot escape the isolated cancellation
                // worker or consume additional dispatcher reservations.
            }
        }
    }
}

internal sealed class BoundedPolicyExecutionDispatcher
{
    internal const int DefaultCapacity = 64;

    private readonly SemaphoreSlim _capacity;
    private int _activeExecutions;

    public BoundedPolicyExecutionDispatcher(
        int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = new SemaphoreSlim(capacity, capacity);
    }

    public static BoundedPolicyExecutionDispatcher Shared { get; } = new();

    internal int ActiveExecutions =>
        Volatile.Read(ref _activeExecutions);

    public bool TryExecute<TResult>(
        Func<ValueTask<TResult>> operation,
        [NotNullWhen(true)] out Task<TResult>? completion)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (!_capacity.Wait(0))
        {
            completion = null;
            return false;
        }

        Interlocked.Increment(ref _activeExecutions);
        try
        {
            completion = Task.Factory.StartNew(
                    () => ExecuteAndReleaseAsync(operation),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach
                    | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();
            return true;
        }
        catch
        {
            Release();
            throw;
        }
    }

    private async Task<TResult> ExecuteAndReleaseAsync<TResult>(
        Func<ValueTask<TResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    private void Release()
    {
        Interlocked.Decrement(ref _activeExecutions);
        _capacity.Release();
    }
}

internal enum CancellationWorkerClass
{
    ControlPlane,
    DataPlane,
    ExecutionPolicy,
    AgentLifecycle,
    ConversationContext,
    MemoryExtension,
    SkillContentResolver,
    SimpleCompletion
}

internal static class ProcessCancellationWorkerPool
{
    internal const int WorkersPerClass = 2;
    internal const int QueueCapacityPerClass = 64;
    private static readonly Lazy<WorkerPool> ControlPlane = new(
        () => new WorkerPool("control", QueueCapacityPerClass));
    private static readonly Lazy<WorkerPool> DataPlane = new(
        () => new WorkerPool("data", QueueCapacityPerClass));
    private static readonly Lazy<WorkerPool> ExecutionPolicy = new(
        () => new WorkerPool("policy", QueueCapacityPerClass));
    private static readonly Lazy<WorkerPool> AgentLifecycle = new(
        () => new WorkerPool("agent-lifecycle", QueueCapacityPerClass));
    private static readonly Lazy<WorkerPool> ConversationContext = new(
        () => new WorkerPool("context", QueueCapacityPerClass));
    private static readonly Lazy<WorkerPool> MemoryExtension = new(
        () => new WorkerPool("memory", QueueCapacityPerClass));
    private static readonly Lazy<WorkerPool> SkillContentResolver = new(
        () => new WorkerPool("skill-content", QueueCapacityPerClass));
    private static readonly Lazy<WorkerPool> SimpleCompletion = new(
        () => new WorkerPool("simple-completion", QueueCapacityPerClass));

    internal static bool TryQueue(
        CancellationWorkerClass workerClass,
        Func<bool> callback,
        out Task<bool> completion)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        return Pool(workerClass).TryQueue(callback, out completion);
    }

    private static WorkerPool Pool(CancellationWorkerClass workerClass) =>
        workerClass switch
        {
            CancellationWorkerClass.ControlPlane => ControlPlane.Value,
            CancellationWorkerClass.DataPlane => DataPlane.Value,
            CancellationWorkerClass.ExecutionPolicy => ExecutionPolicy.Value,
            CancellationWorkerClass.AgentLifecycle => AgentLifecycle.Value,
            CancellationWorkerClass.ConversationContext =>
                ConversationContext.Value,
            CancellationWorkerClass.MemoryExtension => MemoryExtension.Value,
            CancellationWorkerClass.SkillContentResolver =>
                SkillContentResolver.Value,
            CancellationWorkerClass.SimpleCompletion => SimpleCompletion.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(workerClass))
        };

    private sealed class WorkerPool
    {
        private readonly BlockingCollection<WorkItem> _queue;

        public WorkerPool(string name, int queueCapacity)
        {
            _queue = new BlockingCollection<WorkItem>(
                new ConcurrentQueue<WorkItem>(),
                queueCapacity);
            for (var index = 0; index < WorkersPerClass; index++)
            {
                var worker = new Thread(Work)
                {
                    IsBackground = true,
                    Name = $"game-agent-cancellation-{name}-{index}"
                };
                worker.Start();
            }
        }

        public bool TryQueue(
            Func<bool> callback,
            out Task<bool> completion)
        {
            var item = new WorkItem(callback);
            if (!_queue.TryAdd(item))
            {
                completion = Task.FromResult(false);
                return false;
            }

            completion = item.Completion;
            return true;
        }

        private void Work()
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Run();
            }
        }
    }

    private sealed class WorkItem
    {
        private readonly Func<bool> _callback;
        private readonly TaskCompletionSource<bool> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<bool> callback)
        {
            _callback = callback;
        }

        public Task<bool> Completion => _completion.Task;

        public void Run()
        {
            try
            {
                _completion.TrySetResult(_callback());
            }
            catch
            {
                _completion.TrySetResult(false);
            }
        }
    }
}

internal sealed class OperationDeadlineSignals : IDisposable
{
    private readonly CancellationTokenSource _timeoutStop = new();
    private readonly CancellationTokenRegistration _cancellationRegistration;

    public OperationDeadlineSignals(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Timeout = Task.Delay(timeout, _timeoutStop.Token);
        var cancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!)
                .TrySetResult(true),
            cancelled);
        Cancellation = cancelled.Task;
    }

    public Task Timeout { get; }

    public Task Cancellation { get; }

    public void Dispose()
    {
        _cancellationRegistration.Dispose();
        _timeoutStop.Cancel();
        _timeoutStop.Dispose();
    }
}

internal sealed class IsolatedCancellationLease : IAsyncDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly BoundedCancellationDispatcher _dispatcher;
    private readonly object _sync = new();
    private Task? _dispatch;
    private Task? _disposal;
    private bool _cancellationAttempted;

    private IsolatedCancellationLease(
        BoundedCancellationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public CancellationToken Token => _source.Token;

    public static IsolatedCancellationLease Create(
        BoundedCancellationDispatcher dispatcher)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        return new IsolatedCancellationLease(dispatcher);
    }

    public bool TryCancel()
    {
        lock (_sync)
        {
            if (_disposal is not null || _cancellationAttempted)
            {
                return false;
            }

            _cancellationAttempted = true;
            if (!_dispatcher.TryReserve(out var reservation))
            {
                _dispatch = Task.CompletedTask;
                return false;
            }

            try
            {
                var accepted = reservation!.TryDispatch(
                    _source,
                    out var dispatched);
                _dispatch = accepted
                    ? ReleaseWhenCompleteAsync(dispatched, reservation)
                    : Task.CompletedTask;
                return accepted;
            }
            catch
            {
                reservation!.Dispose();
                _dispatch = Task.CompletedTask;
                return false;
            }

        }
    }

    public async ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_sync)
        {
            _disposal ??= DisposeCoreAsync(_dispatch);
            disposal = _disposal;
        }

        await disposal.ConfigureAwait(false);
    }

    public void DisposeDetached()
    {
        var disposal = DisposeAsync().AsTask();
        _ = disposal.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task DisposeCoreAsync(Task? dispatch)
    {
        try
        {
            if (dispatch is not null)
            {
                await dispatch.ConfigureAwait(false);
            }
        }
        finally
        {
            _source.Dispose();
        }
    }

    private static async Task ReleaseWhenCompleteAsync(
        Task dispatch,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            reservation)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        finally
        {
            reservation.Dispose();
        }
    }
}

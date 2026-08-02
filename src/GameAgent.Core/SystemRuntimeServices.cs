using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

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

    public async Task DispatchWhenAvailableAsync(
        CancellationTokenSource cancellation)
    {
        if (cancellation is null)
        {
            throw new ArgumentNullException(nameof(cancellation));
        }

        while (true)
        {
            var reservation = await ReserveAsync().ConfigureAwait(false);
            try
            {
                if (!reservation.TryDispatch(
                        cancellation,
                        out var dispatch))
                {
                    continue;
                }

                await dispatch.ConfigureAwait(false);
                return;
            }
            finally
            {
                reservation.Dispose();
            }
        }
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
    internal const int DefaultCapacity =
        IsolatedCallbackExecutionDefaults.Capacity;

    private readonly SemaphoreSlim _capacity;
    private readonly BoundedCallbackExecutionDispatcher
        _callbackExecutionDispatcher;
    private int _activeExecutions;

    public BoundedPolicyExecutionDispatcher(
        int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = new SemaphoreSlim(capacity, capacity);
        _callbackExecutionDispatcher =
            new BoundedCallbackExecutionDispatcher(capacity);
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
            if (!_callbackExecutionDispatcher.TryExecute(
                    operation,
                    out var callbackCompletion))
            {
                Release();
                completion = null;
                return false;
            }

            completion = AwaitAndReleaseAsync(callbackCompletion);
            return true;
        }
        catch
        {
            Release();
            throw;
        }
    }

    private async Task<TResult> AwaitAndReleaseAsync<TResult>(
        Task<TResult> operation)
    {
        try
        {
            return await operation.ConfigureAwait(false);
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

internal sealed class BoundedCallbackExecutionDispatcher
{
    internal const int DefaultCapacity =
        IsolatedCallbackExecutionDefaults.Capacity;
    internal const int ProcessCapacity = DefaultCapacity;
    internal const int PendingCapacity = 256;

    private static readonly BoundedCallbackProcessLimiter ProcessLimiter =
        new(ProcessCapacity);
    private static readonly BoundedCallbackProcessLimiter PendingLimiter =
        new(PendingCapacity);

    private readonly SemaphoreSlim _slots;
    private readonly BoundedCallbackProcessLimiter _processLimiter;
    private readonly BoundedCallbackProcessLimiter _pendingLimiter;
    [ThreadStatic]
    private static BoundedCallbackExecutionDispatcher? _current;
    private int _activePrefixes;

    public BoundedCallbackExecutionDispatcher(
        int capacity = DefaultCapacity)
        : this(capacity, ProcessLimiter, PendingLimiter)
    {
    }

    internal BoundedCallbackExecutionDispatcher(
        int capacity,
        BoundedCallbackProcessLimiter processLimiter)
        : this(capacity, processLimiter, PendingLimiter)
    {
    }

    internal BoundedCallbackExecutionDispatcher(
        int capacity,
        BoundedCallbackProcessLimiter processLimiter,
        BoundedCallbackProcessLimiter pendingLimiter)
    {
        if (capacity < 1 || capacity > ProcessCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _processLimiter = processLimiter
                          ?? throw new ArgumentNullException(
                              nameof(processLimiter));
        _pendingLimiter = pendingLimiter
                          ?? throw new ArgumentNullException(
                              nameof(pendingLimiter));
        _slots = new SemaphoreSlim(capacity, capacity);
    }

    public static BoundedCallbackExecutionDispatcher AgentLifecycleShared
    {
        get;
    } = new();

    public static BoundedCallbackExecutionDispatcher ConversationContextShared
    {
        get;
    } = new();

    public static BoundedCallbackExecutionDispatcher ExecutionPolicyShared
    {
        get;
    } = new();

    public static BoundedCallbackExecutionDispatcher MemoryShared { get; } =
        new();

    public static BoundedCallbackExecutionDispatcher SkillResolverShared
    {
        get;
    } = new();

    public static BoundedCallbackExecutionDispatcher MultiActorLifecycleShared
    {
        get;
    } = new();

    public static BoundedCallbackExecutionDispatcher ProviderShared { get; } =
        new();

    internal int ActivePrefixes => Volatile.Read(ref _activePrefixes);

    public bool TryExecute<TResult>(
        Func<ValueTask<TResult>> operation,
        [NotNullWhen(true)] out Task<TResult>? completion)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (!TryReservePending(out var pendingReservation))
        {
            completion = null;
            return false;
        }

        if (ReferenceEquals(_current, this))
        {
            completion = BeginPrefix(operation, pendingReservation);
            return true;
        }

        if (!_slots.Wait(0))
        {
            pendingReservation.Dispose();
            completion = null;
            return false;
        }

        if (!_processLimiter.TryEnter())
        {
            _slots.Release();
            pendingReservation.Dispose();
            completion = null;
            return false;
        }

        Interlocked.Increment(ref _activePrefixes);
        try
        {
            completion = IsolatedCallbackTaskStarter.Start(
                () => BeginOwnedPrefix(operation, pendingReservation));
            return true;
        }
        catch
        {
            ReleasePrefix();
            pendingReservation.Dispose();
            throw;
        }
    }

    public bool TryExecute(
        Func<ValueTask> operation,
        [NotNullWhen(true)] out Task? completion)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (!TryExecute(
                () => NonGenericValueTaskAdapter.ToBoolean(operation()),
                out var typedCompletion))
        {
            completion = null;
            return false;
        }

        completion = typedCompletion;
        return true;
    }

    public async Task<TResult> ExecuteWhenAvailableAsync<TResult>(
        Func<ValueTask<TResult>> operation)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (!TryReservePending(out var pendingReservation))
        {
            throw new InvalidOperationException(
                "Callback pending-execution capacity is exhausted.");
        }

        if (ReferenceEquals(_current, this))
        {
            return await BeginPrefix(operation, pendingReservation)
                .ConfigureAwait(false);
        }

        var localSlotHeld = false;
        try
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            localSlotHeld = true;
            await _processLimiter.EnterAsync().ConfigureAwait(false);
        }
        catch
        {
            if (localSlotHeld)
            {
                _slots.Release();
            }

            pendingReservation.Dispose();
            throw;
        }

        Interlocked.Increment(ref _activePrefixes);
        Task<TResult> completion;
        try
        {
            completion = IsolatedCallbackTaskStarter.Start(
                () => BeginOwnedPrefix(operation, pendingReservation));
        }
        catch
        {
            ReleasePrefix();
            pendingReservation.Dispose();
            throw;
        }

        return await completion.ConfigureAwait(false);
    }

    public Task ExecuteWhenAvailableAsync(Func<ValueTask> operation)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        return ExecuteWhenAvailableAsync(
            () => NonGenericValueTaskAdapter.ToBoolean(operation()));
    }

    private Task<TResult> BeginOwnedPrefix<TResult>(
        Func<ValueTask<TResult>> operation,
        CallbackPendingReservation pendingReservation)
    {
        var previous = _current;
        _current = this;
        try
        {
            return BeginPrefix(operation, pendingReservation);
        }
        finally
        {
            _current = previous;
            ReleasePrefix();
        }
    }

    private Task<TResult> BeginPrefix<TResult>(
        Func<ValueTask<TResult>> operation,
        CallbackPendingReservation pendingReservation)
    {
        ConfiguredValueTaskAwaitable<TResult>.ConfiguredValueTaskAwaiter awaiter;
        try
        {
            awaiter = operation().ConfigureAwait(false).GetAwaiter();
        }
        catch
        {
            pendingReservation.Dispose();
            throw;
        }

        bool isCompleted;
        try
        {
            isCompleted = awaiter.IsCompleted;
        }
        catch
        {
            pendingReservation.Dispose();
            throw;
        }

        if (isCompleted)
        {
            try
            {
                return Task.FromResult(awaiter.GetResult());
            }
            finally
            {
                pendingReservation.Dispose();
            }
        }

        // Match ordinary await semantics: a custom status check can make
        // synchronous ExecutionContext changes that belong to the suspended
        // callback and must therefore be visible to its eventual GetResult.
        var callbackContext = ExecutionContext.Capture();

        var completion = new TaskCompletionSource<TResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // 0 = registering, 1 = registered, 2 = continuation owns the
        // reservation, 3 = registration failed and late signals are ignored.
        var registrationState = 0;
        try
        {
            awaiter.UnsafeOnCompleted(
                () =>
                {
                    while (true)
                    {
                        var observed = Volatile.Read(
                            ref registrationState);
                        if (observed is 2 or 3)
                        {
                            return;
                        }

                        if (Interlocked.CompareExchange(
                                ref registrationState,
                                2,
                                observed) == observed)
                        {
                            _ = CompleteAwaiterWhenAvailableAsync(
                                awaiter,
                                completion,
                                pendingReservation,
                                callbackContext);
                            return;
                        }
                    }
                });
            _ = Interlocked.CompareExchange(
                ref registrationState,
                1,
                comparand: 0);
            return completion.Task;
        }
        catch
        {
            if (Interlocked.CompareExchange(
                    ref registrationState,
                    3,
                    comparand: 0) == 0)
            {
                pendingReservation.Dispose();
            }

            throw;
        }
    }

    private async Task CompleteAwaiterWhenAvailableAsync<TResult>(
        ConfiguredValueTaskAwaitable<TResult>.ConfiguredValueTaskAwaiter awaiter,
        TaskCompletionSource<TResult> completion,
        CallbackPendingReservation pendingReservation,
        ExecutionContext? callbackContext)
    {
        var localSlotHeld = false;
        var prefixHeld = false;
        try
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            localSlotHeld = true;
            await _processLimiter.EnterAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _activePrefixes);
            prefixHeld = true;
            var result = await IsolatedCallbackTaskStarter.Start(
                    () => GetResultWithinPrefix(awaiter),
                    callbackContext)
                .ConfigureAwait(false);
            completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            pendingReservation.Dispose();
            if (prefixHeld)
            {
                ReleasePrefix();
            }
            else if (localSlotHeld)
            {
                _slots.Release();
            }
        }
    }

    private bool TryReservePending(
        out CallbackPendingReservation reservation)
    {
        if (!_pendingLimiter.TryEnter())
        {
            reservation = null!;
            return false;
        }

        reservation = new CallbackPendingReservation(_pendingLimiter);
        return true;
    }

    private Task<TResult> GetResultWithinPrefix<TResult>(
        ConfiguredValueTaskAwaitable<TResult>.ConfiguredValueTaskAwaiter awaiter)
    {
        var previous = _current;
        _current = this;
        try
        {
            return Task.FromResult(awaiter.GetResult());
        }
        finally
        {
            _current = previous;
        }
    }

    private void ReleasePrefix()
    {
        Interlocked.Decrement(ref _activePrefixes);
        _processLimiter.Exit();
        _slots.Release();
    }

    private sealed class CallbackPendingReservation : IDisposable
    {
        private readonly BoundedCallbackProcessLimiter _limiter;
        private int _released;

        public CallbackPendingReservation(
            BoundedCallbackProcessLimiter limiter)
        {
            _limiter = limiter;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _limiter.Exit();
            }
        }
    }

    private static class NonGenericValueTaskAdapter
    {
        public static ValueTask<bool> ToBoolean(ValueTask operation)
        {
            var awaiter = operation.ConfigureAwait(false).GetAwaiter();
            if (awaiter.IsCompleted)
            {
                awaiter.GetResult();
                return new ValueTask<bool>(true);
            }

            return new ValueTask<bool>(
                new Source(awaiter),
                token: 0);
        }

        private sealed class Source : IValueTaskSource<bool>
        {
            private readonly ConfiguredValueTaskAwaitable
                .ConfiguredValueTaskAwaiter _awaiter;

            public Source(
                ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter)
            {
                _awaiter = awaiter;
            }

            public ValueTaskSourceStatus GetStatus(short token)
            {
                _ = token;
                return _awaiter.IsCompleted
                    ? ValueTaskSourceStatus.Succeeded
                    : ValueTaskSourceStatus.Pending;
            }

            public void OnCompleted(
                Action<object?> continuation,
                object? state,
                short token,
                ValueTaskSourceOnCompletedFlags flags)
            {
                _ = token;
                _ = flags;
                _awaiter.UnsafeOnCompleted(() => continuation(state));
            }

            public bool GetResult(short token)
            {
                _ = token;
                _awaiter.GetResult();
                return true;
            }
        }
    }
}

internal static class IsolatedCallbackExecutionDefaults
{
    internal const int Capacity = 64;
}

internal static class IsolatedCallbackTaskStarter
{
    private static readonly DedicatedCallbackWorkerPool WorkerPool =
        new(IsolatedCallbackExecutionDefaults.Capacity);

    public static Task<TResult> Start<TResult>(
        Func<Task<TResult>> operation)
    {
        return Start(operation, ExecutionContext.Capture());
    }

    internal static Task<TResult> Start<TResult>(
        Func<Task<TResult>> operation,
        ExecutionContext? executionContext)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        return WorkerPool.Start(operation, executionContext);
    }

    internal sealed class DedicatedCallbackWorkerPool
    {
        private readonly ConcurrentQueue<IWorkItem> _tasks = new();
        private readonly SemaphoreSlim _availableTasks = new(0);
        private readonly object _workerGate = new();
        private readonly TaskCompletionSource<bool> _stopped = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _capacity;
        private int _pendingWorkItems;
        private int _stopping;
        private int _workerCount;
        private int _workerSequence;

        public DedicatedCallbackWorkerPool(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
        }

        public Task<TResult> Start<TResult>(
            Func<Task<TResult>> operation,
            ExecutionContext? executionContext)
        {
            var completion = new TaskCompletionSource<TResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var workItem = new WorkItem<TResult>(
                operation,
                completion,
                executionContext);
            lock (_workerGate)
            {
                if (_stopping != 0)
                {
                    throw new ObjectDisposedException(
                        nameof(DedicatedCallbackWorkerPool));
                }

                if (_workerCount < _capacity
                    && _workerCount < _pendingWorkItems + 1)
                {
                    StartWorker();
                }

                _tasks.Enqueue(workItem);
                _pendingWorkItems++;
                _availableTasks.Release();
            }

            return completion.Task;
        }

        internal Task StopAsync()
        {
            lock (_workerGate)
            {
                if (_stopping == 0)
                {
                    _stopping = 1;
                    if (_workerCount == 0)
                    {
                        _stopped.TrySetResult(true);
                    }
                    else
                    {
                        for (var index = 0; index < _workerCount; index++)
                        {
                            _tasks.Enqueue(StopWorkItem.Instance);
                        }

                        _availableTasks.Release(_workerCount);
                    }
                }

                return _stopped.Task;
            }
        }

        private void StartWorker()
        {
            var sequence = ++_workerSequence;
            var worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "game-agent-callback-" + sequence
            };
            _workerCount++;
            try
            {
                if (ExecutionContext.IsFlowSuppressed())
                {
                    worker.Start();
                }
                else
                {
                    using (ExecutionContext.SuppressFlow())
                    {
                        worker.Start();
                    }
                }
            }
            catch
            {
                _workerCount--;
                throw;
            }
        }

        private void WorkerLoop()
        {
            var cleanExecutionContext = ExecutionContext.Capture()
                                        ?? throw new InvalidOperationException(
                                            "The callback worker could not capture a clean execution context.");
            while (true)
            {
                _availableTasks.Wait();
                if (!_tasks.TryDequeue(out var task))
                {
                    continue;
                }

                if (ReferenceEquals(task, StopWorkItem.Instance))
                {
                    lock (_workerGate)
                    {
                        _workerCount--;
                        if (_workerCount == 0)
                        {
                            _stopped.TrySetResult(true);
                        }
                    }

                    return;
                }

                try
                {
                    task.Execute(cleanExecutionContext);
                }
                finally
                {
                    lock (_workerGate)
                    {
                        _pendingWorkItems--;
                    }
                }
            }
        }

        private interface IWorkItem
        {
            void Execute(ExecutionContext cleanExecutionContext);
        }

        private sealed class StopWorkItem : IWorkItem
        {
            public static readonly StopWorkItem Instance = new();

            private StopWorkItem()
            {
            }

            public void Execute(ExecutionContext cleanExecutionContext)
            {
                _ = cleanExecutionContext;
                throw new InvalidOperationException(
                    "A callback worker stop item cannot be executed.");
            }
        }

        private sealed class WorkItem<TResult> : IWorkItem
        {
            private readonly Func<Task<TResult>> _operation;
            private readonly TaskCompletionSource<TResult> _completion;
            private readonly ExecutionContext? _executionContext;

            public WorkItem(
                Func<Task<TResult>> operation,
                TaskCompletionSource<TResult> completion,
                ExecutionContext? executionContext)
            {
                _operation = operation;
                _completion = completion;
                _executionContext = executionContext;
            }

            public void Execute(ExecutionContext cleanExecutionContext)
            {
                ExecutionContext.Run(
                    (_executionContext ?? cleanExecutionContext).CreateCopy(),
                    static state => ((WorkItem<TResult>)state!).ExecuteCore(),
                    this);
            }

            private void ExecuteCore()
            {
                var previousSynchronizationContext =
                    SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    var operation = _operation()
                                    ?? throw new InvalidOperationException(
                                        "The isolated callback operation returned a null task.");
                    _ = CompleteAsync(operation, _completion);
                }
                catch (Exception exception)
                {
                    _completion.TrySetException(exception);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(
                        previousSynchronizationContext);
                }
            }

            private static async Task CompleteAsync(
                Task<TResult> operation,
                TaskCompletionSource<TResult> completion)
            {
                try
                {
                    completion.TrySetResult(
                        await operation.ConfigureAwait(false));
                }
                catch (OperationCanceledException exception)
                {
                    completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }
        }
    }
}

internal sealed class BoundedCallbackProcessLimiter
{
    private readonly SemaphoreSlim _slots;
    private int _active;

    public BoundedCallbackProcessLimiter(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _slots = new SemaphoreSlim(capacity, capacity);
    }

    internal int Active => Volatile.Read(ref _active);

    public bool TryEnter()
    {
        if (!_slots.Wait(0))
        {
            return false;
        }

        Interlocked.Increment(ref _active);
        return true;
    }

    public async ValueTask EnterAsync()
    {
        await _slots.WaitAsync().ConfigureAwait(false);
        Interlocked.Increment(ref _active);
    }

    public void Exit()
    {
        Interlocked.Decrement(ref _active);
        _slots.Release();
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

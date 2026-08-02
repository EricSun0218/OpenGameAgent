using System.Threading.Tasks.Sources;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class CallbackExecutionDispatcherTests
{
    [Fact]
    public async Task ShutdownCancellationWaitsForAdmissionAndIsNotDropped()
    {
        var dispatcher = new BoundedCancellationDispatcher(capacity: 1);
        var held = await dispatcher.ReserveAsync();
        using var cancellation = new CancellationTokenSource();

        var pending = dispatcher.DispatchWhenAvailableAsync(cancellation);
        Assert.False(pending.IsCompleted);
        Assert.False(cancellation.IsCancellationRequested);

        held.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, dispatcher.ActiveReservations);
    }

    [Fact]
    public async Task RequiredCleanupWaitsForProcessCapacityAndRunsOnce()
    {
        var limiter = new BoundedCallbackProcessLimiter(1);
        var cleanupDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        var blockerDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(
            blockerDispatcher.TryExecute(
                () =>
                {
                    entered.TrySetResult(true);
                    release.Wait();
                    return new ValueTask<int>(1);
                },
                out var blocker));
        Task<int>? cleanup = null;
        var calls = 0;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cleanup = cleanupDispatcher.ExecuteWhenAvailableAsync(
                () => new ValueTask<int>(Interlocked.Increment(ref calls)));
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
        }

        await blocker.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            1,
            await cleanup!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(0, limiter.Active);
    }

    [Fact]
    public async Task AwaitRegistrationRemainsInsideProcessCapacity()
    {
        var limiter = new BoundedCallbackProcessLimiter(1);
        var first = new BoundedCallbackExecutionDispatcher(1, limiter);
        var second = new BoundedCallbackExecutionDispatcher(1, limiter);
        var source = new HostileValueTaskSource(blockRegistration: true);

        Task<int>? pending = null;
        try
        {
            Assert.True(
                first.TryExecute(
                    () => new ValueTask<int>(source, token: 0),
                    out pending));
            await source.RegistrationEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            Assert.Equal(1, first.ActivePrefixes);
            Assert.Equal(1, limiter.Active);
            Assert.False(
                second.TryExecute(
                    () => new ValueTask<int>(9),
                    out _));
        }
        finally
        {
            source.ReleaseRegistration();
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => first.ActivePrefixes == 0 && limiter.Active == 0,
                TimeSpan.FromSeconds(2)));

        Assert.True(
            second.TryExecute(
                () => new ValueTask<int>(9),
                out var independent));
        Assert.Equal(9, await independent.WaitAsync(TimeSpan.FromSeconds(2)));

        source.Complete();
        Assert.Equal(7, await pending!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, source.RegistrationCount);
        Assert.Equal(1, source.ResultCount);
    }

    [Fact]
    public async Task AwaitResultReacquiresProcessCapacity()
    {
        var limiter = new BoundedCallbackProcessLimiter(1);
        var first = new BoundedCallbackExecutionDispatcher(1, limiter);
        var second = new BoundedCallbackExecutionDispatcher(1, limiter);
        var source = new HostileValueTaskSource(blockResult: true);

        Task<int>? pending = null;
        try
        {
            Assert.True(
                first.TryExecute(
                    () => new ValueTask<int>(source, token: 0),
                    out pending));
            await source.RegistrationEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            source.Complete();
            await source.ResultEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            Assert.Equal(1, first.ActivePrefixes);
            Assert.Equal(1, limiter.Active);
            Assert.False(
                second.TryExecute(
                    () => new ValueTask<int>(9),
                    out _));
        }
        finally
        {
            source.ReleaseResult();
        }

        Assert.Equal(7, await pending!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, source.RegistrationCount);
        Assert.Equal(1, source.ResultCount);
        Assert.True(
            SpinWait.SpinUntil(
                () => first.ActivePrefixes == 0 && limiter.Active == 0,
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task PendingCompletionsHaveAProcessWideBoundAndRecover()
    {
        var processLimiter = new BoundedCallbackProcessLimiter(1);
        var pendingLimiter = new BoundedCallbackProcessLimiter(2);
        var firstDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            processLimiter,
            pendingLimiter);
        var secondDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            processLimiter,
            pendingLimiter);
        var firstSource = new HostileValueTaskSource();
        var secondSource = new HostileValueTaskSource();

        Assert.True(
            firstDispatcher.TryExecute(
                () => new ValueTask<int>(firstSource, token: 0),
                out var first));
        await firstSource.RegistrationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.True(
            SpinWait.SpinUntil(
                () => firstDispatcher.ActivePrefixes == 0,
                TimeSpan.FromSeconds(2)));

        Assert.True(
            secondDispatcher.TryExecute(
                () => new ValueTask<int>(secondSource, token: 0),
                out var second));
        await secondSource.RegistrationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.True(
            SpinWait.SpinUntil(
                () => secondDispatcher.ActivePrefixes == 0,
                TimeSpan.FromSeconds(2)));

        Assert.Equal(2, pendingLimiter.Active);
        Assert.Equal(0, processLimiter.Active);
        Assert.False(
            firstDispatcher.TryExecute(
                () => new ValueTask<int>(9),
                out _));

        firstSource.Complete();
        Assert.Equal(7, await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(
            SpinWait.SpinUntil(
                () => pendingLimiter.Active == 1,
                TimeSpan.FromSeconds(2)));
        Assert.True(
            secondDispatcher.TryExecute(
                () => new ValueTask<int>(9),
                out var recovered));
        Assert.Equal(9, await recovered.WaitAsync(TimeSpan.FromSeconds(2)));

        secondSource.Complete();
        Assert.Equal(7, await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, pendingLimiter.Active);
        Assert.Equal(0, processLimiter.Active);
        Assert.Equal(1, firstSource.RegistrationCount);
        Assert.Equal(1, firstSource.ResultCount);
        Assert.Equal(1, secondSource.RegistrationCount);
        Assert.Equal(1, secondSource.ResultCount);
    }

    [Fact]
    public async Task RequiredCleanupPendingAdmissionFailsAndRecovers()
    {
        var processLimiter = new BoundedCallbackProcessLimiter(1);
        var pendingLimiter = new BoundedCallbackProcessLimiter(1);
        var first = new BoundedCallbackExecutionDispatcher(
            1,
            processLimiter,
            pendingLimiter);
        var cleanupDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            processLimiter,
            pendingLimiter);
        var source = new HostileValueTaskSource();

        Assert.True(
            first.TryExecute(
                () => new ValueTask<int>(source, token: 0),
                out var pending));
        await source.RegistrationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.True(
            SpinWait.SpinUntil(
                () => first.ActivePrefixes == 0,
                TimeSpan.FromSeconds(2)));

        var cleanupCalls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cleanupDispatcher.ExecuteWhenAvailableAsync(
                () =>
                {
                    Interlocked.Increment(ref cleanupCalls);
                    return default;
                }));
        Assert.Contains(
            "pending-execution capacity",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, cleanupCalls);

        source.Complete();
        Assert.Equal(7, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        await cleanupDispatcher.ExecuteWhenAvailableAsync(
            () =>
            {
                Interlocked.Increment(ref cleanupCalls);
                return default;
            });
        Assert.Equal(1, cleanupCalls);
        Assert.Equal(0, pendingLimiter.Active);
        Assert.Equal(0, processLimiter.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingAwaitRegistrationCannotEscapePendingBound(
        bool invokesBeforeThrow)
    {
        var processLimiter = new BoundedCallbackProcessLimiter(1);
        var pendingLimiter = new BoundedCallbackProcessLimiter(1);
        var dispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            processLimiter,
            pendingLimiter);
        var source = new ThrowingRegistrationValueTaskSource(
            invokesBeforeThrow);

        Assert.True(
            dispatcher.TryExecute(
                () => new ValueTask<int>(source, token: 0),
                out var completion));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => completion.WaitAsync(TimeSpan.FromSeconds(2)));

        if (!invokesBeforeThrow)
        {
            source.InvokeStoredContinuation();
            await Task.Delay(25);
            Assert.Equal(0, source.ResultCount);
        }
        else
        {
            Assert.True(
                SpinWait.SpinUntil(
                    () => source.ResultCount == 1,
                    TimeSpan.FromSeconds(2)));
        }

        Assert.True(
            SpinWait.SpinUntil(
                () => pendingLimiter.Active == 0
                      && processLimiter.Active == 0,
                TimeSpan.FromSeconds(2)));
        Assert.True(
            dispatcher.TryExecute(
                () => new ValueTask<int>(9),
                out var recovered));
        Assert.Equal(9, await recovered.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ThrowingAwaitStatusCannotLeakPendingCapacity()
    {
        var processLimiter = new BoundedCallbackProcessLimiter(1);
        var pendingLimiter = new BoundedCallbackProcessLimiter(1);
        var dispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            processLimiter,
            pendingLimiter);
        var source = new ThrowingStatusValueTaskSource();

        Assert.True(
            dispatcher.TryExecute(
                () => new ValueTask<int>(source, token: 0),
                out var completion));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => completion.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(
            SpinWait.SpinUntil(
                () => pendingLimiter.Active == 0
                      && processLimiter.Active == 0,
                TimeSpan.FromSeconds(2)));
        Assert.True(
            dispatcher.TryExecute(
                () => new ValueTask<int>(9),
                out var recovered));
        Assert.Equal(9, await recovered.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SuppressedFlowCannotObserveAWorkersPriorRequestContext()
    {
        var context = new AsyncLocal<string?>();
        var processLimiter = new BoundedCallbackProcessLimiter(1);
        var pendingLimiter = new BoundedCallbackProcessLimiter(2);
        var dispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            processLimiter,
            pendingLimiter);
        context.Value = "first-request";

        Assert.True(
            dispatcher.TryExecute(
                () => new ValueTask<string?>(context.Value),
                out var first));
        Assert.Equal(
            "first-request",
            await first.WaitAsync(TimeSpan.FromSeconds(2)));

        Task<string?>? second;
        using (ExecutionContext.SuppressFlow())
        {
            Assert.True(
                dispatcher.TryExecute(
                    () => new ValueTask<string?>(context.Value),
                    out second));
        }

        Assert.Null(await second!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("first-request", context.Value);
    }

    [Fact]
    public async Task NullContextWorkItemsCannotPolluteAReusableWorker()
    {
        var context = new AsyncLocal<string?>();
        var workerPool = new IsolatedCallbackTaskStarter
            .DedicatedCallbackWorkerPool(1);
        try
        {
            var first = workerPool.Start(
                () =>
                {
                    context.Value = "polluted-worker";
                    return Task.FromResult(context.Value);
                },
                executionContext: null);
            Assert.Equal(
                "polluted-worker",
                await first.WaitAsync(TimeSpan.FromSeconds(2)));

            var second = workerPool.Start(
                () => Task.FromResult(context.Value),
                executionContext: null);
            Assert.Null(await second.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(context.Value);
        }
        finally
        {
            await workerPool.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task AsyncResultUsesTheInvocationExecutionContext()
    {
        var context = new AsyncLocal<string?>();
        var dispatcher = new BoundedCallbackExecutionDispatcher(1);
        var source = new ContextObservingValueTaskSource(context);
        context.Value = "caller-context";

        Assert.True(
            dispatcher.TryExecute(
                () => new ValueTask<string>(source, token: 0),
                out var completion));
        await source.RegistrationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        Task sourceCompletion;
        using (ExecutionContext.SuppressFlow())
        {
            sourceCompletion = Task.Run(
                () =>
                {
                    context.Value = "source-context";
                    source.Complete();
                });
        }

        await sourceCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            "caller-context",
            await completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("caller-context", context.Value);
    }

    [Fact]
    public async Task AsyncResultIncludesStatusCheckContextChanges()
    {
        var context = new AsyncLocal<string?>();
        var dispatcher = new BoundedCallbackExecutionDispatcher(1);
        var source = new StatusMutatingValueTaskSource(context);
        context.Value = "caller-context";

        Assert.True(
            dispatcher.TryExecute(
                () => new ValueTask<string>(source, token: 0),
                out var completion));
        await source.RegistrationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        source.Complete();

        Assert.Equal(
            "status-context",
            await completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("caller-context", context.Value);
    }

    [Fact]
    public async Task ProcessCapacityCanRefillWithoutWorkerQueueFailures()
    {
        const int capacity = 64;
        var processLimiter = new BoundedCallbackProcessLimiter(capacity);
        var pendingLimiter = new BoundedCallbackProcessLimiter(256);
        var dispatcher = new BoundedCallbackExecutionDispatcher(
            capacity,
            processLimiter,
            pendingLimiter);

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(20));
        var workers = Enumerable.Range(0, capacity)
            .Select(
                worker => Task.Run(
                    async () =>
                    {
                        for (var iteration = 0; iteration < 32; iteration++)
                        {
                            Task<int>? completion;
                            while (!dispatcher.TryExecute(
                                       () => new ValueTask<int>(worker),
                                       out completion))
                            {
                                await Task.Delay(1, timeout.Token);
                            }

                            Assert.Equal(
                                worker,
                                await completion.WaitAsync(
                                    TimeSpan.FromSeconds(2)));
                        }
                    }))
            .ToArray();

        var allWorkers = Task.WhenAll(workers);
        try
        {
            await allWorkers.WaitAsync(timeout.Token);
        }
        finally
        {
            timeout.Cancel();
            try
            {
                await allWorkers;
            }
            catch (OperationCanceledException)
                when (timeout.IsCancellationRequested)
            {
            }
        }

        Assert.Equal(0, processLimiter.Active);
        Assert.Equal(0, pendingLimiter.Active);
    }

    [Fact]
    public async Task NonGenericAwaitResultReacquiresProcessCapacity()
    {
        var limiter = new BoundedCallbackProcessLimiter(1);
        var first = new BoundedCallbackExecutionDispatcher(1, limiter);
        var second = new BoundedCallbackExecutionDispatcher(1, limiter);
        var source = new HostileNonGenericValueTaskSource();
        Task? pending = null;
        try
        {
            Assert.True(
                first.TryExecute(
                    () => new ValueTask(source, token: 0),
                    out pending));
            await source.RegistrationEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            source.Complete();
            await source.ResultEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            Assert.Equal(1, limiter.Active);
            Assert.False(
                second.TryExecute(
                    () => new ValueTask<int>(9),
                    out _));
        }
        finally
        {
            source.ReleaseResult();
        }

        await pending!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, source.RegistrationCount);
        Assert.Equal(1, source.ResultCount);
        Assert.Equal(0, limiter.Active);
    }

    [Fact]
    public async Task BlockedDedicatedWorkerDoesNotStrandNextCallback()
    {
        var workerPool = new IsolatedCallbackTaskStarter
            .DedicatedCallbackWorkerPool(2);
        using var release = new ManualResetEventSlim(false);
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? first = null;
        Task<int>? second = null;
        try
        {
            first = workerPool.Start(
                () =>
                {
                    firstEntered.TrySetResult(true);
                    release.Wait();
                    return Task.FromResult(1);
                },
                ExecutionContext.Capture());
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            second = workerPool.Start(
                () =>
                {
                    secondEntered.TrySetResult(true);
                    release.Wait();
                    return Task.FromResult(2);
                },
                ExecutionContext.Capture());

            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();
            Assert.Equal(
                1,
                await first.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(
                2,
                await second.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
            await workerPool.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StopDrainsQueuedWorkAndTerminatesEveryWorker()
    {
        const int workerCount = 8;
        var workerPool = new IsolatedCallbackTaskStarter
            .DedicatedCallbackWorkerPool(workerCount);
        using var release = new ManualResetEventSlim(false);
        var entered = Enumerable.Range(0, workerCount)
            .Select(
                _ => new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var workers = entered
            .Select(
                signal => workerPool.Start(
                    () =>
                    {
                        signal.TrySetResult(true);
                        release.Wait();
                        return Task.FromResult(true);
                    },
                    ExecutionContext.Capture()))
            .ToArray();

        try
        {
            await Task.WhenAll(entered.Select(signal => signal.Task))
                .WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(2));

            var queued = workerPool.Start(
                () => Task.FromResult(42),
                ExecutionContext.Capture());
            var stopped = workerPool.StopAsync();

            Assert.Equal(42, await queued.WaitAsync(TimeSpan.FromSeconds(2)));
            await stopped.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            await workerPool.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class HostileValueTaskSource : IValueTaskSource<int>
    {
        private readonly ManualResetEventSlim _registrationRelease =
            new(initialState: false);
        private readonly ManualResetEventSlim _resultRelease =
            new(initialState: false);
        private readonly bool _blockRegistration;
        private readonly bool _blockResult;
        private Action<object?>? _continuation;
        private object? _continuationState;
        private int _completed;
        private int _registrationCount;
        private int _resultCount;

        public HostileValueTaskSource(
            bool blockRegistration = false,
            bool blockResult = false)
        {
            _blockRegistration = blockRegistration;
            _blockResult = blockResult;
        }

        public TaskCompletionSource<bool> RegistrationEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ResultEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int RegistrationCount => Volatile.Read(
            ref _registrationCount);

        public int ResultCount => Volatile.Read(ref _resultCount);

        public ValueTaskSourceStatus GetStatus(short token)
        {
            _ = token;
            return Volatile.Read(ref _completed) == 0
                ? ValueTaskSourceStatus.Pending
                : ValueTaskSourceStatus.Succeeded;
        }

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _ = token;
            _ = flags;
            if (Interlocked.Increment(ref _registrationCount) != 1)
            {
                throw new InvalidOperationException(
                    "The source registered more than one continuation.");
            }

            _continuation = continuation;
            _continuationState = state;
            RegistrationEntered.TrySetResult(true);
            if (_blockRegistration)
            {
                _registrationRelease.Wait();
            }
        }

        public int GetResult(short token)
        {
            _ = token;
            if (Interlocked.Increment(ref _resultCount) != 1)
            {
                throw new InvalidOperationException(
                    "The source result was consumed more than once.");
            }

            ResultEntered.TrySetResult(true);
            if (_blockResult)
            {
                _resultRelease.Wait();
            }

            return 7;
        }

        public void Complete()
        {
            Volatile.Write(ref _completed, 1);
            var continuation = Volatile.Read(ref _continuation)
                               ?? throw new InvalidOperationException(
                                   "The source has not registered a continuation.");
            continuation(_continuationState);
        }

        public void ReleaseRegistration()
        {
            _registrationRelease.Set();
        }

        public void ReleaseResult()
        {
            _resultRelease.Set();
        }
    }

    private sealed class ThrowingRegistrationValueTaskSource
        : IValueTaskSource<int>
    {
        private readonly bool _invokesBeforeThrow;
        private Action<object?>? _continuation;
        private object? _state;
        private int _resultCount;

        public ThrowingRegistrationValueTaskSource(bool invokesBeforeThrow)
        {
            _invokesBeforeThrow = invokesBeforeThrow;
        }

        public int ResultCount => Volatile.Read(ref _resultCount);

        public ValueTaskSourceStatus GetStatus(short token)
        {
            _ = token;
            return ValueTaskSourceStatus.Pending;
        }

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _ = token;
            _ = flags;
            _continuation = continuation;
            _state = state;
            if (_invokesBeforeThrow)
            {
                continuation(state);
            }

            throw new InvalidOperationException(
                "Controlled registration failure.");
        }

        public int GetResult(short token)
        {
            _ = token;
            Interlocked.Increment(ref _resultCount);
            return 7;
        }

        public void InvokeStoredContinuation()
        {
            (_continuation
             ?? throw new InvalidOperationException(
                 "No continuation was stored."))(_state);
        }
    }

    private sealed class ThrowingStatusValueTaskSource
        : IValueTaskSource<int>
    {
        public ValueTaskSourceStatus GetStatus(short token)
        {
            _ = token;
            throw new InvalidOperationException(
                "Controlled status failure.");
        }

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _ = continuation;
            _ = state;
            _ = token;
            _ = flags;
            throw new InvalidOperationException(
                "Status failure should precede registration.");
        }

        public int GetResult(short token)
        {
            _ = token;
            throw new InvalidOperationException(
                "Status failure should precede result consumption.");
        }
    }

    private sealed class ContextObservingValueTaskSource
        : IValueTaskSource<string>
    {
        private readonly AsyncLocal<string?> _context;
        private Action<object?>? _continuation;
        private object? _state;
        private int _completed;

        public ContextObservingValueTaskSource(
            AsyncLocal<string?> context)
        {
            _context = context;
        }

        public TaskCompletionSource<bool> RegistrationEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTaskSourceStatus GetStatus(short token)
        {
            _ = token;
            return Volatile.Read(ref _completed) == 0
                ? ValueTaskSourceStatus.Pending
                : ValueTaskSourceStatus.Succeeded;
        }

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _ = token;
            _ = flags;
            _continuation = continuation;
            _state = state;
            RegistrationEntered.TrySetResult(true);
        }

        public string GetResult(short token)
        {
            _ = token;
            return _context.Value ?? "missing-context";
        }

        public void Complete()
        {
            Volatile.Write(ref _completed, 1);
            (_continuation
             ?? throw new InvalidOperationException(
                 "No continuation was registered."))(_state);
        }
    }

    private sealed class StatusMutatingValueTaskSource
        : IValueTaskSource<string>
    {
        private readonly AsyncLocal<string?> _context;
        private Action<object?>? _continuation;
        private object? _state;

        public StatusMutatingValueTaskSource(
            AsyncLocal<string?> context)
        {
            _context = context;
        }

        public TaskCompletionSource<bool> RegistrationEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTaskSourceStatus GetStatus(short token)
        {
            _ = token;
            _context.Value = "status-context";
            return ValueTaskSourceStatus.Pending;
        }

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _ = token;
            _ = flags;
            _continuation = continuation;
            _state = state;
            RegistrationEntered.TrySetResult(true);
        }

        public string GetResult(short token)
        {
            _ = token;
            return _context.Value ?? "missing-context";
        }

        public void Complete()
        {
            (_continuation
             ?? throw new InvalidOperationException(
                 "No continuation was registered."))(_state);
        }
    }

    private sealed class HostileNonGenericValueTaskSource : IValueTaskSource
    {
        private readonly ManualResetEventSlim _resultRelease = new(false);
        private Action<object?>? _continuation;
        private object? _state;
        private int _completed;
        private int _registrationCount;
        private int _resultCount;

        public TaskCompletionSource<bool> RegistrationEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ResultEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int RegistrationCount => Volatile.Read(
            ref _registrationCount);

        public int ResultCount => Volatile.Read(ref _resultCount);

        public ValueTaskSourceStatus GetStatus(short token)
        {
            _ = token;
            return Volatile.Read(ref _completed) == 0
                ? ValueTaskSourceStatus.Pending
                : ValueTaskSourceStatus.Succeeded;
        }

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            _ = token;
            _ = flags;
            Interlocked.Increment(ref _registrationCount);
            _continuation = continuation;
            _state = state;
            RegistrationEntered.TrySetResult(true);
        }

        public void GetResult(short token)
        {
            _ = token;
            Interlocked.Increment(ref _resultCount);
            ResultEntered.TrySetResult(true);
            _resultRelease.Wait();
        }

        public void Complete()
        {
            Volatile.Write(ref _completed, 1);
            (_continuation
             ?? throw new InvalidOperationException(
                 "No continuation was registered."))(_state);
        }

        public void ReleaseResult()
        {
            _resultRelease.Set();
        }
    }
}

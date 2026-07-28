using System.Diagnostics;
using System.Threading.Channels;
using GameAgent.Core;

namespace GameAgent.Godot;

public sealed class GodotDispatcherQueueFullException : InvalidOperationException
{
    public GodotDispatcherQueueFullException(int capacity)
        : base($"The Godot main-thread dispatcher reached its capacity of {capacity}.")
    {
    }
}

public sealed class GodotDispatchCancelledBeforeExecutionException :
    OperationCanceledException
{
    public GodotDispatchCancelledBeforeExecutionException(
        string operationId,
        CancellationToken cancellationToken)
        : base(
            $"Godot operation '{operationId}' was cancelled before execution.",
            cancellationToken)
    {
    }
}

public sealed class GodotMainThreadDispatcher
{
    private readonly Channel<IDispatchCommand> _commands;
    private readonly int _capacity;
    private readonly ulong _mainThreadId;
    private readonly IRuntimeClock _clock;
    private readonly object _runningGate = new();
    private TaskCompletionSource<bool> _runningDrained = CompletedSignal();
    private int _accepting = 1;
    private int _pendingCount;
    private int _runningCount;

    public GodotMainThreadDispatcher(
        int capacity,
        IRuntimeClock? clock = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _mainThreadId = global::Godot.OS.GetMainThreadId();
        _clock = clock ?? new SystemRuntimeClock();
        _commands = Channel.CreateBounded<IDispatchCommand>(
            new BoundedChannelOptions(capacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
    }

    public int Capacity => _capacity;

    public int PendingCount => Volatile.Read(ref _pendingCount);

    public int RunningCount
    {
        get
        {
            lock (_runningGate)
            {
                return _runningCount;
            }
        }
    }

    public bool IsMainThread =>
        global::Godot.OS.GetThreadCallerId() == _mainThreadId;

    public ValueTask<T> InvokeAsync<T>(
        Func<T> callback,
        string operationId,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        return InvokeAsync(
            _ => new ValueTask<T>(callback()),
            operationId,
            deadline,
            cancellationToken);
    }

    public ValueTask<T> InvokeAsync<T>(
        Func<CancellationToken, ValueTask<T>> callback,
        string operationId,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException(
                "A stable operationId is required.",
                nameof(operationId));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromException<T>(
                new GodotDispatchCancelledBeforeExecutionException(
                    operationId,
                    cancellationToken));
        }

        if (Volatile.Read(ref _accepting) == 0)
        {
            return ValueTask.FromException<T>(
                new GodotDispatchCancelledBeforeExecutionException(
                    operationId,
                    cancellationToken));
        }

        var command = new DispatchCommand<T>(
            callback,
            operationId,
            deadline,
            cancellationToken,
            EndRunningWork);
        Interlocked.Increment(ref _pendingCount);
        if (!_commands.Writer.TryWrite(command))
        {
            Interlocked.Decrement(ref _pendingCount);
            if (Volatile.Read(ref _accepting) == 0)
            {
                command.CancelForShutdown();
                return new ValueTask<T>(command.Task);
            }

            command.Dispose();
            return ValueTask.FromException<T>(
                new GodotDispatcherQueueFullException(_capacity));
        }

        return new ValueTask<T>(command.Task);
    }

    public int Drain(int maxCommands, TimeSpan maxDuration)
    {
        if (!IsMainThread)
        {
            throw new InvalidOperationException(
                "Godot main-thread commands may only be drained on the main thread.");
        }

        if (maxCommands < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCommands));
        }

        if (maxDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDuration));
        }

        var processed = 0;
        var started = Stopwatch.GetTimestamp();
        while (processed < maxCommands
               && Stopwatch.GetElapsedTime(started) < maxDuration
               && TryTakeForExecution(out var command))
        {
            command.Execute(_clock.UtcNow);
            processed++;
        }

        return processed;
    }

    public ValueTask WaitForRunningWorkAsync(
        CancellationToken cancellationToken = default)
    {
        Task drain;
        lock (_runningGate)
        {
            drain = _runningDrained.Task;
        }

        return GodotShutdownWait.WaitAsync(drain, cancellationToken);
    }

    public void StopAccepting()
    {
        lock (_runningGate)
        {
            if (_accepting == 0)
            {
                return;
            }

            Volatile.Write(ref _accepting, 0);
            _commands.Writer.TryComplete();
            while (_commands.Reader.TryRead(out var command))
            {
                Interlocked.Decrement(ref _pendingCount);
                command.CancelForShutdown();
            }
        }
    }

    private bool TryTakeForExecution(out IDispatchCommand command)
    {
        lock (_runningGate)
        {
            if (!_commands.Reader.TryRead(out var next))
            {
                command = null!;
                return false;
            }

            command = next;
            Interlocked.Decrement(ref _pendingCount);
            if (!command.TryClaimForExecution())
            {
                return true;
            }

            if (_runningCount == 0)
            {
                _runningDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _runningCount++;
            command.RegisterRunningWork();
            return true;
        }
    }

    private void EndRunningWork()
    {
        TaskCompletionSource<bool>? completed = null;
        lock (_runningGate)
        {
            if (_runningCount <= 0)
            {
                throw new InvalidOperationException(
                    "The Godot dispatcher running-work count underflowed.");
            }

            _runningCount--;
            if (_runningCount == 0)
            {
                completed = _runningDrained;
            }
        }

        completed?.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completed.TrySetResult(true);
        return completed;
    }

    private interface IDispatchCommand
    {
        bool TryClaimForExecution();

        void RegisterRunningWork();

        void Execute(DateTimeOffset now);

        void CancelForShutdown();
    }

    private sealed class DispatchCommand<T> : IDispatchCommand, IDisposable
    {
        private readonly Func<CancellationToken, ValueTask<T>> _callback;
        private readonly string _operationId;
        private readonly DateTimeOffset? _deadline;
        private readonly CancellationToken _cancellationToken;
        private readonly Action _onCompleted;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _runningRegistered;
        private int _state;

        public DispatchCommand(
            Func<CancellationToken, ValueTask<T>> callback,
            string operationId,
            DateTimeOffset? deadline,
            CancellationToken cancellationToken,
            Action onCompleted)
        {
            _callback = callback;
            _operationId = operationId;
            _deadline = deadline;
            _cancellationToken = cancellationToken;
            _onCompleted = onCompleted;
            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.Register(
                    static state => ((DispatchCommand<T>)state!).Cancel(),
                    this);
            }
        }

        public Task<T> Task => _completion.Task;

        public bool TryClaimForExecution()
        {
            return Interlocked.CompareExchange(
                    ref _state,
                    value: 1,
                    comparand: 0) == 0;
        }

        public void RegisterRunningWork()
        {
            Volatile.Write(ref _runningRegistered, 1);
        }

        public void Execute(DateTimeOffset now)
        {
            if (Volatile.Read(ref _state) != 1)
            {
                return;
            }

            try
            {
                if (_deadline.HasValue && now >= _deadline.Value)
                {
                    CompleteWithException(
                        new TimeoutException(
                            $"Godot operation '{_operationId}' missed its deadline."));
                    return;
                }

                if (_cancellationToken.IsCancellationRequested)
                {
                    CompleteWithException(
                        new GodotDispatchCancelledBeforeExecutionException(
                            _operationId,
                            _cancellationToken));
                    return;
                }

                var pending = _callback(_cancellationToken);
                if (pending.IsCompleted)
                {
                    CompleteWithResult(pending.GetAwaiter().GetResult());
                }
                else
                {
                    _ = CompleteAsynchronously(pending);
                }
            }
            catch (OperationCanceledException)
            {
                CompleteWithCancellation();
            }
            catch (Exception exception)
            {
                CompleteWithException(exception);
            }
        }

        public void CancelForShutdown()
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    value: 2,
                    comparand: 0) != 0)
            {
                return;
            }

            _completion.TrySetException(
                new GodotDispatchCancelledBeforeExecutionException(
                    _operationId,
                    _cancellationToken));
            Cleanup();
        }

        public void Dispose()
        {
            _cancellationRegistration.Dispose();
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    value: 2,
                    comparand: 0) != 0)
            {
                return;
            }

            _completion.TrySetException(
                new GodotDispatchCancelledBeforeExecutionException(
                    _operationId,
                    _cancellationToken));
            Cleanup();
        }

        private async Task CompleteAsynchronously(ValueTask<T> pending)
        {
            try
            {
                CompleteWithResult(
                    await pending.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                CompleteWithCancellation();
            }
            catch (Exception exception)
            {
                CompleteWithException(exception);
            }
        }

        private void CompleteWithResult(T result)
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    value: 2,
                    comparand: 1) != 1)
            {
                return;
            }

            _completion.TrySetResult(result);
            Cleanup();
        }

        private void CompleteWithException(Exception exception)
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    value: 2,
                    comparand: 1) != 1)
            {
                return;
            }

            _completion.TrySetException(exception);
            Cleanup();
        }

        private void CompleteWithCancellation()
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    value: 2,
                    comparand: 1) != 1)
            {
                return;
            }

            _completion.TrySetCanceled(_cancellationToken);
            Cleanup();
        }

        private void Cleanup()
        {
            Dispose();
            if (Interlocked.Exchange(
                    ref _runningRegistered,
                    0) != 0)
            {
                _onCompleted();
            }
        }
    }
}

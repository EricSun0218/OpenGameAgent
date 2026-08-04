using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GameAgent.Unity
{
    public sealed class UnityDispatcherQueueFullException
        : InvalidOperationException
    {
        public UnityDispatcherQueueFullException(int capacity)
            : base(
                "The Unity main-thread queue reached its capacity of "
                + capacity + " items.")
        {
            Capacity = capacity;
        }

        public int Capacity { get; private set; }
    }

    public sealed class UnityDispatchCancelledBeforeExecutionException
        : OperationCanceledException
    {
        public UnityDispatchCancelledBeforeExecutionException(
            CancellationToken cancellationToken)
            : base(
                "The Unity main-thread action was cancelled before execution.",
                cancellationToken)
        {
        }
    }

    public sealed class UnityMainThreadDispatcher : IDisposable
    {
        private readonly ConcurrentQueue<IWorkItem> _queue =
            new ConcurrentQueue<IWorkItem>();
        private readonly CancellationTokenSource _shutdown =
            new CancellationTokenSource();
        private readonly CancellationToken _shutdownToken;
        private readonly object _runningSync = new object();
        private readonly Action _beforeWorkClaim;
        private readonly TaskCompletionSource<bool> _shutdownCompleted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _capacity;
        private readonly int _mainThreadId;
        private TaskCompletionSource<bool> _runningDrained =
            CompletedSignal();
        private int _queuedCount;
        private int _runningCount;
        private int _isShutdown;
        private int _shutdownSourceDisposed;

        public UnityMainThreadDispatcher(int capacity)
            : this(capacity, null)
        {
        }

        internal UnityMainThreadDispatcher(
            int capacity,
            Action beforeWorkClaim)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    "Dispatcher capacity must be positive.");
            }

            _capacity = capacity;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _shutdownToken = _shutdown.Token;
            _beforeWorkClaim = beforeWorkClaim;
        }

        public event Action<Exception> UnhandledException;

        public int Capacity
        {
            get { return _capacity; }
        }

        public int PendingCount
        {
            get { return Volatile.Read(ref _queuedCount); }
        }

        public int RunningCount
        {
            get
            {
                lock (_runningSync)
                {
                    return _runningCount;
                }
            }
        }

        public bool IsMainThread
        {
            get
            {
                return Thread.CurrentThread.ManagedThreadId == _mainThreadId;
            }
        }

        public bool IsShutdown
        {
            get { return Volatile.Read(ref _isShutdown) != 0; }
        }

        public bool TryPost(Action callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            var item = new FireAndForgetWorkItem(
                callback,
                ReportException,
                EndRunningWork);
            lock (_runningSync)
            {
                if (_isShutdown != 0
                    || Volatile.Read(ref _queuedCount) >= _capacity)
                {
                    return false;
                }

                _queue.Enqueue(item);
                Interlocked.Increment(ref _queuedCount);
            }

            return true;
        }

        public ValueTask<T> InvokeAsync<T>(
            Func<CancellationToken, ValueTask<T>> callback,
            CancellationToken cancellationToken)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return FromPreExecutionCancellation<T>(
                    cancellationToken);
            }

            if (IsShutdown)
            {
                return FromPreExecutionCancellation<T>(
                    _shutdownToken);
            }

            if (IsMainThread)
            {
                return InvokeDirectAsync(callback, cancellationToken);
            }

            AsyncWorkItem<T> item;
            lock (_runningSync)
            {
                if (_isShutdown != 0)
                {
                    return FromPreExecutionCancellation<T>(
                        _shutdownToken);
                }

                if (Volatile.Read(ref _queuedCount) >= _capacity)
                {
                    return new ValueTask<T>(
                        Task.FromException<T>(
                            new UnityDispatcherQueueFullException(
                                _capacity)));
                }

                item = new AsyncWorkItem<T>(
                    callback,
                    cancellationToken,
                    _shutdownToken,
                    EndRunningWork);
                _queue.Enqueue(item);
                Interlocked.Increment(ref _queuedCount);
            }

            return new ValueTask<T>(item.Task);
        }

        public int Pump(int maxItems, double maxMilliseconds)
        {
            if (!IsMainThread)
            {
                throw new InvalidOperationException(
                    "The Unity dispatcher can only be pumped by its "
                    + "creating thread.");
            }

            if (maxItems <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxItems));
            }

            if (maxMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxMilliseconds));
            }

            var processed = 0;
            var stopwatch = Stopwatch.StartNew();
            while (processed < maxItems
                   && stopwatch.Elapsed.TotalMilliseconds < maxMilliseconds
                   && _queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _queuedCount);
                if (TryClaimRunningWork(item))
                {
                    item.Execute();
                }
                else
                {
                    item.Cancel();
                }

                processed++;
            }

            return processed;
        }

        public void Shutdown()
        {
            lock (_runningSync)
            {
                if (_isShutdown != 0)
                {
                    return;
                }

                Volatile.Write(ref _isShutdown, 1);
            }

            Exception cancellationFailure = null;
            try
            {
                _shutdown.Cancel();
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }
            finally
            {
                try
                {
                    DrainQueue();
                }
                finally
                {
                    _shutdownCompleted.TrySetResult(true);
                }
            }

            if (cancellationFailure != null)
            {
                ReportException(cancellationFailure);
            }
        }

        public ValueTask WaitForRunningWorkAsync(
            CancellationToken cancellationToken)
        {
            Task drain;
            lock (_runningSync)
            {
                drain = _runningDrained.Task;
            }

            return cancellationToken.CanBeCanceled
                ? new ValueTask(
                    AwaitWithCancellationAsync(
                        drain,
                        cancellationToken))
                : new ValueTask(drain);
        }

        private void DrainQueue()
        {
            while (_queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _queuedCount);
                item.Cancel();
            }
        }

        public void Dispose()
        {
            Shutdown();
            Task drain;
            lock (_runningSync)
            {
                drain = _runningDrained.Task;
            }

            var disposalBarrier = Task.WhenAll(
                _shutdownCompleted.Task,
                drain);
            if (disposalBarrier.IsCompleted)
            {
                DisposeShutdownSource();
            }
            else
            {
                _ = DisposeShutdownSourceAfterBarrierAsync(
                    disposalBarrier);
            }
        }

        private async ValueTask<T> InvokeDirectAsync<T>(
            Func<CancellationToken, ValueTask<T>> callback,
            CancellationToken callerToken)
        {
            InvokeBeforeWorkClaim();
            if (!TryBeginRunningWork())
            {
                throw new UnityDispatchCancelledBeforeExecutionException(
                    _shutdownToken);
            }

            try
            {
                using (var linked =
                       CancellationTokenSource.CreateLinkedTokenSource(
                           callerToken,
                           _shutdownToken))
                {
                    if (linked.Token.IsCancellationRequested)
                    {
                        throw new UnityDispatchCancelledBeforeExecutionException(
                            linked.Token);
                    }

                    return await callback(linked.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                EndRunningWork();
            }
        }

        private bool TryClaimRunningWork(IWorkItem item)
        {
            InvokeBeforeWorkClaim();
            lock (_runningSync)
            {
                if (_isShutdown != 0 || !item.TryClaim())
                {
                    return false;
                }

                BeginRunningWorkLocked();
                return true;
            }
        }

        private bool TryBeginRunningWork()
        {
            lock (_runningSync)
            {
                if (_isShutdown != 0)
                {
                    return false;
                }

                BeginRunningWorkLocked();
                return true;
            }
        }

        private void BeginRunningWorkLocked()
        {
            if (_runningCount == 0)
            {
                _runningDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _runningCount++;
        }

        private void EndRunningWork()
        {
            TaskCompletionSource<bool> completed = null;
            lock (_runningSync)
            {
                if (_runningCount <= 0)
                {
                    throw new InvalidOperationException(
                        "The Unity dispatcher running-work count underflowed.");
                }

                _runningCount--;
                if (_runningCount == 0)
                {
                    completed = _runningDrained;
                }
            }

            if (completed != null)
            {
                completed.TrySetResult(true);
            }
        }

        private void InvokeBeforeWorkClaim()
        {
            if (_beforeWorkClaim != null)
            {
                _beforeWorkClaim();
            }
        }

        private async Task DisposeShutdownSourceAfterBarrierAsync(
            Task disposalBarrier)
        {
            await disposalBarrier.ConfigureAwait(false);
            DisposeShutdownSource();
        }

        private void DisposeShutdownSource()
        {
            if (Interlocked.Exchange(
                    ref _shutdownSourceDisposed,
                    1) == 0)
            {
                _shutdown.Dispose();
            }
        }

        private static TaskCompletionSource<bool> CompletedSignal()
        {
            var completed = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completed.TrySetResult(true);
            return completed;
        }

        private static async Task AwaitWithCancellationAsync(
            Task drain,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drain.IsCompleted)
            {
                await drain.ConfigureAwait(false);
                return;
            }

            var cancellationSignal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                       () => cancellationSignal.TrySetCanceled()))
            {
                var completed = await Task.WhenAny(
                        drain,
                        cancellationSignal.Task)
                    .ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
        }

        private void ReportException(Exception exception)
        {
            var handler = UnhandledException;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(exception);
            }
            catch
            {
                // An exception observer must never break the event pump.
            }
        }

        private static ValueTask<T> FromPreExecutionCancellation<T>(
            CancellationToken cancellationToken)
        {
            return new ValueTask<T>(
                Task.FromException<T>(
                    new UnityDispatchCancelledBeforeExecutionException(
                        cancellationToken)));
        }

        private interface IWorkItem
        {
            bool TryClaim();

            void Execute();

            void Cancel();
        }

        private sealed class FireAndForgetWorkItem : IWorkItem
        {
            private readonly Action _callback;
            private readonly Action<Exception> _reportException;
            private readonly Action _onCompleted;
            private int _state;

            public FireAndForgetWorkItem(
                Action callback,
                Action<Exception> reportException,
                Action onCompleted)
            {
                _callback = callback;
                _reportException = reportException;
                _onCompleted = onCompleted;
            }

            public bool TryClaim()
            {
                return Interlocked.CompareExchange(
                    ref _state,
                    value: 1,
                    comparand: 0) == 0;
            }

            public void Execute()
            {
                try
                {
                    _callback();
                }
                catch (Exception exception)
                {
                    _reportException(exception);
                }
                finally
                {
                    Volatile.Write(ref _state, 2);
                    _onCompleted();
                }
            }

            public void Cancel()
            {
                Interlocked.CompareExchange(
                    ref _state,
                    value: 2,
                    comparand: 0);
            }
        }

        private sealed class AsyncWorkItem<T> : IWorkItem
        {
            private readonly Func<CancellationToken, ValueTask<T>> _callback;
            private readonly CancellationTokenSource _linkedCancellation;
            private readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _registration;
            private readonly Action _onCompleted;
            private int _runningRegistered;
            private int _state;

            public AsyncWorkItem(
                Func<CancellationToken, ValueTask<T>> callback,
                CancellationToken callerToken,
                CancellationToken shutdownToken,
                Action onCompleted)
            {
                _callback = callback;
                _onCompleted = onCompleted;
                _linkedCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        callerToken,
                        shutdownToken);
                _registration = _linkedCancellation.Token.Register(
                    state => ((AsyncWorkItem<T>)state).Cancel(),
                    this);
            }

            public Task<T> Task
            {
                get { return _completion.Task; }
            }

            public bool TryClaim()
            {
                if (Interlocked.CompareExchange(
                        ref _state,
                        value: 1,
                        comparand: 0) != 0)
                {
                    return false;
                }

                Volatile.Write(ref _runningRegistered, 1);
                return true;
            }

            public void Execute()
            {
                if (_linkedCancellation.IsCancellationRequested)
                {
                    CancelBeforeCallback();
                    return;
                }

                try
                {
                    var pending = _callback(_linkedCancellation.Token);
                    if (pending.IsCompleted)
                    {
                        Complete(pending.GetAwaiter().GetResult());
                    }
                    else
                    {
                        _ = CompleteAsynchronously(pending);
                    }
                }
                catch (OperationCanceledException)
                {
                    CancelRunning();
                }
                catch (Exception exception)
                {
                    Fail(exception);
                }
            }

            public void Cancel()
            {
                if (Interlocked.CompareExchange(
                        ref _state,
                        value: 2,
                        comparand: 0) != 0)
                {
                    return;
                }

                _completion.TrySetException(
                    new UnityDispatchCancelledBeforeExecutionException(
                        _linkedCancellation.Token));
                Cleanup();
            }

            private void CancelBeforeCallback()
            {
                if (Interlocked.CompareExchange(
                        ref _state,
                        value: 2,
                        comparand: 1) != 1)
                {
                    return;
                }

                _completion.TrySetException(
                    new UnityDispatchCancelledBeforeExecutionException(
                        _linkedCancellation.Token));
                Cleanup();
            }

            private async Task CompleteAsynchronously(ValueTask<T> pending)
            {
                try
                {
                    Complete(await pending.ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    CancelRunning();
                }
                catch (Exception exception)
                {
                    Fail(exception);
                }
            }

            private void Complete(T result)
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

            private void Fail(Exception exception)
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

            private void CancelRunning()
            {
                if (Interlocked.CompareExchange(
                        ref _state,
                        value: 2,
                        comparand: 1) != 1)
                {
                    return;
                }

                _completion.TrySetCanceled();
                Cleanup();
            }

            private void Cleanup()
            {
                _registration.Dispose();
                _linkedCancellation.Dispose();
                if (Interlocked.Exchange(
                        ref _runningRegistered,
                        0) != 0)
                {
                    _onCompleted();
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;

namespace GameAgent.Unity
{
    internal sealed class UnityBoundedCancellationDispatcher
    {
        private readonly object _sync = new object();
        private readonly Queue<PendingCancellation> _pending =
            new Queue<PendingCancellation>();
        private readonly int _capacity;
        private readonly int _leaseCapacity;
        private readonly int _pendingCapacity;
        private readonly string _capacityMessage;
        private int _activeCount;
        private int _leaseCount;

        internal UnityBoundedCancellationDispatcher(
            int capacity,
            int pendingCapacity,
            string capacityMessage)
        {
            _capacity = capacity;
            _leaseCapacity = capacity + pendingCapacity;
            _pendingCapacity = pendingCapacity;
            _capacityMessage = capacityMessage;
        }

        internal int ActiveCount
        {
            get { return Volatile.Read(ref _activeCount); }
        }

        internal int PendingCount
        {
            get
            {
                lock (_sync)
                {
                    return _pending.Count;
                }
            }
        }

        internal int LeaseCount
        {
            get
            {
                lock (_sync)
                {
                    return _leaseCount;
                }
            }
        }

        internal bool TryAcquireLease(out Lease lease)
        {
            lock (_sync)
            {
                if (_leaseCount >= _leaseCapacity)
                {
                    lease = null;
                    return false;
                }

                _leaseCount++;
                lease = new Lease(this);
                return true;
            }
        }

        internal bool TryDispatch(
            Action cancellation,
            out Task<Exception> completion)
        {
            if (cancellation == null)
            {
                throw new ArgumentNullException(nameof(cancellation));
            }

            var pending = new PendingCancellation(cancellation);
            var schedule = false;
            lock (_sync)
            {
                if (_activeCount < _capacity)
                {
                    _activeCount++;
                    schedule = true;
                }
                else if (_pending.Count < _pendingCapacity)
                {
                    _pending.Enqueue(pending);
                }
                else
                {
                    completion = Task.FromResult<Exception>(
                        new InvalidOperationException(_capacityMessage));
                    return false;
                }
            }

            completion = pending.Completion.Task;
            if (schedule)
            {
                Schedule(pending);
            }

            return true;
        }

        internal bool TryDispatch(
            Lease lease,
            Action cancellation,
            out Task<Exception> completion)
        {
            if (lease == null
                || !ReferenceEquals(lease.Owner, this)
                || !lease.TryBeginDispatch())
            {
                completion = Task.FromResult<Exception>(
                    new InvalidOperationException(
                        "The lifecycle cancellation lease is invalid."));
                return false;
            }

            if (cancellation == null)
            {
                lease.CancelDispatch();
                throw new ArgumentNullException(nameof(cancellation));
            }

            var pending = new PendingCancellation(cancellation);
            var schedule = false;
            lock (_sync)
            {
                if (_activeCount < _capacity)
                {
                    _activeCount++;
                    schedule = true;
                }
                else if (_pending.Count < _pendingCapacity)
                {
                    _pending.Enqueue(pending);
                }
                else
                {
                    lease.CancelDispatch();
                    completion = Task.FromResult<Exception>(
                        new InvalidOperationException(_capacityMessage));
                    return false;
                }
            }

            completion = pending.Completion.Task;
            if (schedule)
            {
                Schedule(pending);
            }

            return true;
        }

        internal void ReleaseLease(Lease lease)
        {
            if (lease == null
                || !ReferenceEquals(lease.Owner, this)
                || !lease.TryRelease())
            {
                return;
            }

            lock (_sync)
            {
                _leaseCount--;
            }
        }

        internal void ReleaseLeaseAfter(
            Lease lease,
            Task completion)
        {
            if (completion == null || completion.IsCompleted)
            {
                ReleaseLease(lease);
                return;
            }

            _ = completion.ContinueWith(
                _ => ReleaseLease(lease),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void Schedule(PendingCancellation pending)
        {
            while (pending != null)
            {
                try
                {
                    _ = Task.Factory.StartNew(
                        () => Execute(pending),
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach
                        | TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);
                    return;
                }
                catch (Exception exception)
                {
                    var failed = pending;
                    pending = CompleteActive();
                    failed.Completion.TrySetResult(exception);
                }
            }
        }

        private void Execute(PendingCancellation pending)
        {
            Exception failure = null;
            try
            {
                pending.Cancellation();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            var next = CompleteActive();
            pending.Completion.TrySetResult(failure);
            if (next != null)
            {
                Schedule(next);
            }
        }

        private PendingCancellation CompleteActive()
        {
            lock (_sync)
            {
                _activeCount--;
                if (_pending.Count == 0)
                {
                    return null;
                }

                var next = _pending.Dequeue();
                _activeCount++;
                return next;
            }
        }

        private sealed class PendingCancellation
        {
            internal PendingCancellation(Action cancellation)
            {
                Cancellation = cancellation;
            }

            internal Action Cancellation { get; private set; }

            internal TaskCompletionSource<Exception> Completion
                { get; private set; } =
                new TaskCompletionSource<Exception>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal sealed class Lease
        {
            private int _dispatchStarted;
            private int _released;

            internal Lease(
                UnityBoundedCancellationDispatcher owner)
            {
                Owner = owner;
            }

            internal UnityBoundedCancellationDispatcher Owner
                { get; private set; }

            internal bool TryBeginDispatch()
            {
                return Interlocked.CompareExchange(
                    ref _dispatchStarted,
                    1,
                    0) == 0;
            }

            internal void CancelDispatch()
            {
                Volatile.Write(ref _dispatchStarted, 0);
            }

            internal bool TryRelease()
            {
                return Interlocked.CompareExchange(
                    ref _released,
                    1,
                    0) == 0;
            }
        }
    }

    internal static class UnityRunCancellationDispatcher
    {
        internal const int Capacity = 8;

        private static readonly UnityBoundedCancellationDispatcher Dispatcher =
            new UnityBoundedCancellationDispatcher(
                Capacity,
                0,
                "The process run cancellation dispatcher is at capacity.");

        internal static int ActiveCount
        {
            get { return Dispatcher.ActiveCount; }
        }

        internal static bool TryDispatch(
            Action cancellation,
            out Task<Exception> completion)
        {
            return Dispatcher.TryDispatch(cancellation, out completion);
        }
    }

    internal static class UnityLifecycleCancellationDispatcher
    {
        internal const int Capacity = 8;
        internal const int PendingCapacity = 64;

        private static readonly UnityBoundedCancellationDispatcher Dispatcher =
            new UnityBoundedCancellationDispatcher(
                Capacity,
                PendingCapacity,
                "The process lifecycle cancellation dispatcher queue is at capacity.");

        internal static int ActiveCount
        {
            get { return Dispatcher.ActiveCount; }
        }

        internal static int PendingCount
        {
            get { return Dispatcher.PendingCount; }
        }

        internal static int LeaseCount
        {
            get { return Dispatcher.LeaseCount; }
        }

        internal static bool TryAcquireLease(
            out UnityBoundedCancellationDispatcher.Lease lease)
        {
            return Dispatcher.TryAcquireLease(out lease);
        }

        internal static bool TryDispatch(
            UnityBoundedCancellationDispatcher.Lease lease,
            Action cancellation,
            out Task<Exception> completion)
        {
            return Dispatcher.TryDispatch(
                lease,
                cancellation,
                out completion);
        }

        internal static void ReleaseLease(
            UnityBoundedCancellationDispatcher.Lease lease)
        {
            Dispatcher.ReleaseLease(lease);
        }

        internal static void ReleaseLeaseAfter(
            UnityBoundedCancellationDispatcher.Lease lease,
            Task completion)
        {
            Dispatcher.ReleaseLeaseAfter(lease, completion);
        }
    }

    public sealed class UnityRunCapacityExceededException :
        InvalidOperationException
    {
        public UnityRunCapacityExceededException(int capacity)
            : base(
                "The Unity runtime host already has the maximum "
                + capacity
                + " active runs.")
        {
            Capacity = capacity;
        }

        public int Capacity { get; private set; }
    }

    public sealed class UnityAgentRuntimeFacade : IAsyncDisposable
    {
        private static readonly TimeSpan CancellationDrainTimeout =
            TimeSpan.FromSeconds(1);
        private static readonly TimeSpan CancellationAdmissionRetryDelay =
            TimeSpan.FromMilliseconds(10);

        private readonly object _sync = new object();
        private readonly IUnityAgentRuntimeBackend<
            HeadlessRunRequest,
            HeadlessRunOutcome> _headlessBackend;
        private readonly IUnityDurableAgentRuntimeBackend _durableBackend;
        private readonly ISessionStore _sessionStore;
        private readonly bool _ownsSessionStore;
        private readonly bool _ownsBackend;
        private readonly bool _flushSessionStore;
        private readonly int _maxActiveRuns;
        private readonly UnityBoundedCancellationDispatcher.Lease
            _shutdownLease;
        private readonly CancellationTokenSource _shutdown =
            new CancellationTokenSource();
        private readonly Dictionary<Task, CancellationTokenSource> _activeRuns =
            new Dictionary<Task, CancellationTokenSource>();
        private readonly HashSet<Task<Exception>> _activeCancellations =
            new HashSet<Task<Exception>>();
        private readonly TaskCompletionSource<bool> _shutdownSignalCompleted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _shutdownRequested;
        private bool _backendDisposed;
        private bool _sessionStoreFlushed;
        private bool _sessionStoreDisposed;
        private bool _shutdownSourceDisposed;
        private Exception _cancellationFailure;
        private int _shutdownRetryRequired;
        private Task<Exception> _shutdownCancellationTask;
        private Task _shutdownTask;

        public UnityAgentRuntimeFacade(
            IModelProvider modelProvider,
            ISessionStore sessionStore,
            UnityMainThreadDispatcher dispatcher,
            UnityActionHandler actionHandler,
            IRuntimeClock clock,
            IRuntimeIdGenerator idGenerator,
            bool ownsSessionStore,
            int maxActiveRuns = 32)
        {
            ValidateMaxActiveRuns(maxActiveRuns);
            if (modelProvider == null)
            {
                throw new ArgumentNullException(nameof(modelProvider));
            }

            _sessionStore = sessionStore
                ?? throw new ArgumentNullException(nameof(sessionStore));
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (actionHandler == null)
            {
                throw new ArgumentNullException(nameof(actionHandler));
            }

            _ownsSessionStore = ownsSessionStore;
            _flushSessionStore = true;
            _maxActiveRuns = maxActiveRuns;
            _headlessBackend = new HeadlessUnityAgentRuntimeBackend(
                new HeadlessAgentRuntime(
                    modelProvider,
                    new UnityMainThreadGameHost(
                        dispatcher,
                        actionHandler,
                        clock),
                    sessionStore,
                    clock,
                    idGenerator
                        ?? throw new ArgumentNullException(
                            nameof(idGenerator))));
            _shutdownLease = AcquireShutdownLease();
        }

        public UnityAgentRuntimeFacade(
            IUnityAgentRuntimeBackend<
                HeadlessRunRequest,
                HeadlessRunOutcome> backend,
            ISessionStore sessionStore,
            bool ownsSessionStore,
            bool ownsBackend = false,
            bool flushSessionStore = true,
            int maxActiveRuns = 32)
        {
            ValidateMaxActiveRuns(maxActiveRuns);
            _headlessBackend = backend
                ?? throw new ArgumentNullException(nameof(backend));
            _sessionStore = sessionStore
                ?? throw new ArgumentNullException(nameof(sessionStore));
            _ownsSessionStore = ownsSessionStore;
            _ownsBackend = ownsBackend;
            _flushSessionStore = flushSessionStore;
            _maxActiveRuns = maxActiveRuns;
            _shutdownLease = AcquireShutdownLease();
        }

        public UnityAgentRuntimeFacade(
            IUnityDurableAgentRuntimeBackend backend,
            ISessionStore sessionStore,
            bool ownsSessionStore,
            bool ownsBackend = false,
            bool flushSessionStore = true,
            int maxActiveRuns = 32)
        {
            ValidateMaxActiveRuns(maxActiveRuns);
            _durableBackend = backend
                ?? throw new ArgumentNullException(nameof(backend));
            _sessionStore = sessionStore
                ?? throw new ArgumentNullException(nameof(sessionStore));
            _ownsSessionStore = ownsSessionStore;
            _ownsBackend = ownsBackend;
            _flushSessionStore = flushSessionStore;
            _maxActiveRuns = maxActiveRuns;
            _shutdownLease = AcquireShutdownLease();
        }

        public UnityAgentRuntimeFacade(
            IDurableAgentRuntime runtime,
            ISessionStore sessionStore,
            bool ownsSessionStore,
            bool ownsRuntime = false,
            int maxActiveRuns = 32)
            : this(
                new DurableUnityAgentRuntimeBackend(
                    runtime,
                    ownsRuntime),
                sessionStore,
                ownsSessionStore,
                ownsBackend: true,
                maxActiveRuns: maxActiveRuns)
        {
        }

        public bool IsShutdownRequested
        {
            get
            {
                lock (_sync)
                {
                    return _shutdownRequested;
                }
            }
        }

        public int ActiveRunCount
        {
            get
            {
                lock (_sync)
                {
                    return _activeRuns.Count;
                }
            }
        }

        internal bool RequiresShutdownCancellationAdmission
        {
            get
            {
                lock (_sync)
                {
                    return _shutdownRequested
                        && _shutdownCancellationTask == null;
                }
            }
        }

        internal bool RequiresShutdownRetry
        {
            get
            {
                return Volatile.Read(ref _shutdownRetryRequired) != 0;
            }
        }

        public RuntimeControlPlane DurableControls
        {
            get
            {
                if (_durableBackend == null)
                {
                    throw new InvalidOperationException(
                        "This facade is not configured with a durable "
                        + "runtime backend.");
                }

                return _durableBackend.Controls;
            }
        }

        public Task<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (_headlessBackend == null)
            {
                throw new InvalidOperationException(
                    "This facade is not configured with a headless "
                    + "runtime backend.");
            }

            return RunTrackedAsync(
                token => _headlessBackend.RunAsync(request, token),
                cancellationToken);
        }

        public Task<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (_durableBackend == null)
            {
                throw new InvalidOperationException(
                    "This facade is not configured with a durable "
                    + "runtime backend.");
            }

            return RunTrackedAsync(
                token => _durableBackend.RunAsync(request, token),
                cancellationToken);
        }

        public Task<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation continuation = null,
            IGameOperationReconciler reconciler = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(runId))
            {
                throw new ArgumentException(
                    "A run id is required.",
                    nameof(runId));
            }

            if (_durableBackend == null)
            {
                throw new InvalidOperationException(
                    "This facade is not configured with a durable "
                    + "runtime backend.");
            }

            return RunTrackedAsync(
                token => _durableBackend.ResumeAsync(
                    runId,
                    continuation,
                    reconciler,
                    token),
                cancellationToken);
        }

        private Task<TOutcome> RunTrackedAsync<TOutcome>(
            Func<CancellationToken, ValueTask<TOutcome>> start,
            CancellationToken cancellationToken)
        {
            CancellationTokenSource linked;
            var completion = new TaskCompletionSource<TOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (_shutdownRequested)
                {
                    throw new ObjectDisposedException(
                        nameof(UnityAgentRuntimeFacade));
                }

                if (_activeRuns.Count >= _maxActiveRuns)
                {
                    throw new UnityRunCapacityExceededException(
                        _maxActiveRuns);
                }

                linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _shutdown.Token);
                _activeRuns.Add(completion.Task, linked);
            }

            try
            {
                var pending = start(linked.Token);
                _ = CompleteRunAsync(
                    pending,
                    completion,
                    linked);
            }
            catch
            {
                CompleteTrackedRun(completion.Task, linked);
                throw;
            }

            return completion.Task;
        }

        public void CancelActiveRuns()
        {
            Task<Exception> cancellation;
            var accepted = false;
            lock (_sync)
            {
                var active = _activeRuns.Values.ToArray();

                if (active.Length == 0)
                {
                    return;
                }

                accepted = UnityRunCancellationDispatcher.TryDispatch(
                    () => CancelSources(active),
                    out cancellation);
                if (accepted)
                {
                    _activeCancellations.Add(cancellation);
                }
            }

            if (!accepted)
            {
                throw cancellation.GetAwaiter().GetResult();
            }

            _ = ObserveCancellationAsync(cancellation);
        }

        private static void CancelSources(
            CancellationTokenSource[] active)
        {
            List<Exception> failures = null;
            foreach (var cancellation in active)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception exception)
                {
                    if (failures == null)
                    {
                        failures = new List<Exception>();
                    }

                    failures.Add(exception);
                }
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more run cancellation callbacks failed.",
                    failures);
            }
        }

        public void RequestShutdown()
        {
            lock (_sync)
            {
                _shutdownRequested = true;
            }

            TryDispatchShutdownCancellation(out _);
            _shutdownSignalCompleted.TrySetResult(true);
        }

        public ValueTask ShutdownAsync(
            CancellationToken cancellationToken)
        {
            RequestShutdown();

            Task shutdown;
            lock (_sync)
            {
                if (_shutdownTask == null
                    || (_shutdownTask.IsCompleted
                        && Volatile.Read(
                            ref _shutdownRetryRequired) != 0))
                {
                    Volatile.Write(ref _shutdownRetryRequired, 0);
                    _shutdownTask = ShutdownCoreAsync();
                }

                shutdown = _shutdownTask;
            }

            return cancellationToken.CanBeCanceled
                ? new ValueTask(
                    AwaitWithCancellationAsync(
                        shutdown,
                        cancellationToken))
                : new ValueTask(shutdown);
        }

        public ValueTask DisposeAsync()
        {
            return ShutdownAsync(CancellationToken.None);
        }

        private async Task ShutdownCoreAsync()
        {
            await _shutdownSignalCompleted.Task.ConfigureAwait(false);
            var failures = new List<Exception>();
            var admission = await EnsureShutdownCancellationDispatchedAsync()
                .ConfigureAwait(false);
            if (admission.Completion == null)
            {
                Volatile.Write(ref _shutdownRetryRequired, 1);
                throw new AggregateException(
                    "Runtime shutdown cancellation could not be dispatched.",
                    admission.Failure);
            }

            Task[] active;
            Task<Exception>[] cancellations;
            var shutdownCancellation = admission.Completion;
            lock (_sync)
            {
                active = _activeRuns.Keys.ToArray();
                cancellations = _activeCancellations.ToArray();
            }

            var cancellationWork = cancellations
                .Concat(new[] { shutdownCancellation })
                .ToArray();
            var drainWork = active
                .Concat(cancellationWork.Cast<Task>())
                .ToArray();
            Task drainBarrier = null;
            var ownerDrainCompleted = true;
            var backendShutdownCompleted = !_ownsBackend || _backendDisposed;
            if (drainWork.Length != 0)
            {
                var drain = Task.WhenAll(drainWork);
                drainBarrier = drain;
                if (await WaitForDrainAsync(drain).ConfigureAwait(false))
                {
                    try
                    {
                        await drain.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Run failures are delivered to their callers.
                    }
                }
                else
                {
                    ObserveLateFault(drain);
                    ownerDrainCompleted = false;
                    Volatile.Write(ref _shutdownRetryRequired, 1);
                    failures.Add(
                        new TimeoutException(
                            "Timed out while draining runtime cancellation."));
                }
            }

            foreach (var cancellation in cancellationWork)
            {
                if (cancellation.Status == TaskStatus.RanToCompletion
                    && cancellation.Result != null)
                {
                    RecordCancellationFailure(cancellation.Result);
                }
            }

            try
            {
                if (ownerDrainCompleted
                    && _ownsBackend
                    && !_backendDisposed)
                {
                    await DisposeBackendAsync().ConfigureAwait(false);
                    _backendDisposed = true;
                    backendShutdownCompleted = true;
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _shutdownRetryRequired, 1);
                failures.Add(exception);
            }

            var flushRequired = _flushSessionStore
                && _sessionStore is IDurableSessionStore;
            var flushCompleted = !flushRequired || _sessionStoreFlushed;
            try
            {
                if (ownerDrainCompleted
                    && backendShutdownCompleted
                    && flushRequired
                    && !_sessionStoreFlushed)
                {
                    await ((IDurableSessionStore)_sessionStore)
                        .FlushAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    _sessionStoreFlushed = true;
                    flushCompleted = true;
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _shutdownRetryRequired, 1);
                failures.Add(exception);
            }

            try
            {
                if (ownerDrainCompleted
                    && backendShutdownCompleted
                    && flushCompleted
                    && _ownsSessionStore
                    && !_sessionStoreDisposed)
                {
                    if (_sessionStore
                        is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync()
                            .ConfigureAwait(false);
                    }
                    else if (_sessionStore
                             is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    _sessionStoreDisposed = true;
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _shutdownRetryRequired, 1);
                failures.Add(exception);
            }

            try
            {
                var storeShutdownCompleted =
                    !_ownsSessionStore || _sessionStoreDisposed;
                if (ownerDrainCompleted
                    && backendShutdownCompleted
                    && flushCompleted
                    && storeShutdownCompleted
                    && !_shutdownSourceDisposed)
                {
                    DisposeShutdownSource(shutdownCancellation);
                    _shutdownSourceDisposed = true;
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _shutdownRetryRequired, 1);
                failures.Add(exception);
            }

            Exception cancellationFailure;
            lock (_sync)
            {
                cancellationFailure = _cancellationFailure;
            }

            if (cancellationFailure != null)
            {
                failures.Add(cancellationFailure);
            }

            UnityLifecycleCancellationDispatcher.ReleaseLeaseAfter(
                _shutdownLease,
                drainBarrier ?? shutdownCancellation);

            if (failures.Count != 0)
            {
                throw new AggregateException(
                        "One or more runtime shutdown operations failed.",
                        failures)
                    .Flatten();
            }
        }

        private bool TryDispatchShutdownCancellation(
            out Task<Exception> cancellation)
        {
            lock (_sync)
            {
                if (_shutdownCancellationTask != null)
                {
                    cancellation = _shutdownCancellationTask;
                    return true;
                }

                try
                {
                    if (!UnityLifecycleCancellationDispatcher.TryDispatch(
                            _shutdownLease,
                            () => _shutdown.Cancel(),
                            out cancellation))
                    {
                        return false;
                    }

                    _shutdownCancellationTask = cancellation;
                    return true;
                }
                catch (Exception exception)
                {
                    cancellation = Task.FromResult(exception);
                    return false;
                }
            }
        }

        private async Task<CancellationAdmissionResult>
            EnsureShutdownCancellationDispatchedAsync()
        {
            Exception failure = null;
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                if (TryDispatchShutdownCancellation(out var cancellation))
                {
                    return new CancellationAdmissionResult(
                        cancellation,
                        null);
                }

                if (cancellation.IsCompleted
                    && cancellation.Status == TaskStatus.RanToCompletion)
                {
                    failure = cancellation.Result;
                }

                if (elapsed.Elapsed >= CancellationDrainTimeout)
                {
                    return new CancellationAdmissionResult(
                        null,
                        failure
                        ?? new InvalidOperationException(
                            "Runtime cancellation admission failed."));
                }

                await Task.Delay(CancellationAdmissionRetryDelay)
                    .ConfigureAwait(false);
            }
        }

        private async Task ObserveCancellationAsync(
            Task<Exception> cancellation)
        {
            Exception failure;
            try
            {
                failure = await cancellation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            lock (_sync)
            {
                _activeCancellations.Remove(cancellation);
                if (failure != null && _cancellationFailure == null)
                {
                    _cancellationFailure = failure;
                }
            }
        }

        private void RecordCancellationFailure(Exception failure)
        {
            lock (_sync)
            {
                if (_cancellationFailure == null)
                {
                    _cancellationFailure = failure;
                }
            }
        }

        private static async Task<bool> WaitForDrainAsync(Task drain)
        {
            if (drain.IsCompleted)
            {
                return true;
            }

            var completed = await Task.WhenAny(
                    drain,
                    Task.Delay(CancellationDrainTimeout))
                .ConfigureAwait(false);
            return ReferenceEquals(completed, drain);
        }

        private static void ObserveLateFault(Task task)
        {
            _ = task.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private void DisposeShutdownSource(
            Task<Exception> cancellation)
        {
            if (cancellation.IsCompleted)
            {
                _shutdown.Dispose();
                return;
            }

            _ = cancellation.ContinueWith(
                (_, state) =>
                {
                    try
                    {
                        ((CancellationTokenSource)state).Dispose();
                    }
                    catch
                    {
                    }
                },
                _shutdown,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task AwaitWithCancellationAsync(
            Task shutdown,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shutdown.IsCompleted)
            {
                await shutdown.ConfigureAwait(false);
                return;
            }

            var cancellationSignal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                       () => cancellationSignal.TrySetCanceled()))
            {
                var completed = await Task.WhenAny(
                        shutdown,
                        cancellationSignal.Task)
                    .ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
        }

        private async Task DisposeBackendAsync()
        {
            object backend = _durableBackend ?? (object)_headlessBackend;
            if (backend is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (backend is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private async Task CompleteRunAsync<TOutcome>(
            ValueTask<TOutcome> pending,
            TaskCompletionSource<TOutcome> completion,
            CancellationTokenSource cancellation)
        {
            try
            {
                var result = await pending.ConfigureAwait(false);
                CompleteTrackedRun(completion.Task, cancellation);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                CompleteTrackedRun(completion.Task, cancellation);
                completion.TrySetCanceled();
            }
            catch (Exception exception)
            {
                CompleteTrackedRun(completion.Task, cancellation);
                completion.TrySetException(exception);
            }
        }

        private void CompleteTrackedRun(
            Task completed,
            CancellationTokenSource expectedCancellation)
        {
            CancellationTokenSource removed = null;
            lock (_sync)
            {
                if (_activeRuns.TryGetValue(
                        completed,
                        out var cancellation)
                    && ReferenceEquals(
                        cancellation,
                        expectedCancellation))
                {
                    _activeRuns.Remove(completed);
                    removed = cancellation;
                }
            }

            if (removed != null)
            {
                removed.Dispose();
            }
        }

        private static void ValidateMaxActiveRuns(int maxActiveRuns)
        {
            if (maxActiveRuns < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxActiveRuns));
            }
        }

        private static UnityBoundedCancellationDispatcher.Lease
            AcquireShutdownLease()
        {
            if (UnityLifecycleCancellationDispatcher.TryAcquireLease(
                    out var lease))
            {
                return lease;
            }

            throw new InvalidOperationException(
                "The process lifecycle cancellation capacity is exhausted.");
        }

        private sealed class CancellationAdmissionResult
        {
            internal CancellationAdmissionResult(
                Task<Exception> completion,
                Exception failure)
            {
                Completion = completion;
                Failure = failure;
            }

            internal Task<Exception> Completion { get; private set; }

            internal Exception Failure { get; private set; }
        }
    }
}

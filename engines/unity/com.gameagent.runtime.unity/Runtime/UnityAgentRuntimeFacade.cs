using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;

namespace GameAgent.Unity
{
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
        private readonly CancellationTokenSource _shutdown =
            new CancellationTokenSource();
        private readonly Dictionary<Task, CancellationTokenSource> _activeRuns =
            new Dictionary<Task, CancellationTokenSource>();
        private readonly TaskCompletionSource<bool> _shutdownSignalCompleted =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _shutdownRequested;
        private Exception _shutdownCancellationFailure;
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
            CancellationTokenSource[] active;
            lock (_sync)
            {
                active = _activeRuns.Values.ToArray();
            }

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
                if (_shutdownRequested)
                {
                    return;
                }

                _shutdownRequested = true;
            }

            try
            {
                _ = Task.Run(CancelShutdownToken);
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _shutdownCancellationFailure = exception;
                }

                _shutdownSignalCompleted.TrySetResult(true);
            }
        }

        private void CancelShutdownToken()
        {
            try
            {
                _shutdown.Cancel();
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _shutdownCancellationFailure = exception;
                }
            }
            finally
            {
                _shutdownSignalCompleted.TrySetResult(true);
            }
        }

        public ValueTask ShutdownAsync(
            CancellationToken cancellationToken)
        {
            RequestShutdown();

            Task shutdown;
            lock (_sync)
            {
                if (_shutdownTask == null)
                {
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

            Task[] active;
            lock (_sync)
            {
                active = _activeRuns.Keys.ToArray();
            }

            if (active.Length != 0)
            {
                try
                {
                    await Task.WhenAll(active).ConfigureAwait(false);
                }
                catch
                {
                    // Run failures are delivered to their callers. A failed
                    // run must not prevent the durable store flush.
                }
            }

            try
            {
                if (_flushSessionStore
                    && _sessionStore is IDurableSessionStore durable)
                {
                    await durable.FlushAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                if (_ownsBackend)
                {
                    await DisposeBackendAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                if (_ownsSessionStore)
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
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                _shutdown.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            Exception cancellationFailure;
            lock (_sync)
            {
                cancellationFailure = _shutdownCancellationFailure;
            }

            if (cancellationFailure != null)
            {
                failures.Add(cancellationFailure);
            }

            if (failures.Count != 0)
            {
                throw new AggregateException(
                        "One or more runtime shutdown operations failed.",
                        failures)
                    .Flatten();
            }
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
    }
}

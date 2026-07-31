using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Runtime;
using UnityEngine;
using UnityEngine.Scripting;

namespace GameAgent.Unity
{
    [Preserve]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-32000)]
    public sealed class UnityAgentRuntimeHost : MonoBehaviour
    {
        private static readonly TimeSpan LifecycleShutdownTimeout =
            TimeSpan.FromSeconds(1);
        private static readonly TimeSpan PlayerQuitShutdownTimeout =
            TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LifecycleAdmissionRetryDelay =
            TimeSpan.FromMilliseconds(10);
        private const int MaximumLifecycleRetryAttempts = 8;
        private const int InitialLifecycleRetryDelayMilliseconds = 50;
        private const int MaximumLifecycleRetryDelayMilliseconds = 1000;
        private static UnityAgentRuntimeHost _instance;
        private readonly object _shutdownSync = new object();

        [SerializeField]
        [Min(1)]
        private int dispatcherCapacity = 1024;

        [SerializeField]
        [Min(1)]
        private int maxDispatchesPerFrame = 64;

        [SerializeField]
        [Min(1)]
        private int runtimeEventCapacity = 1024;

        [SerializeField]
        [Min(1)]
        private int maxRuntimeEventsPerFrame = 128;

        [SerializeField]
        [Min(1)]
        private int maxActiveRuns = 32;

        [SerializeField]
        [Min(0.01f)]
        private float dispatchBudgetMilliseconds = 2.0f;

        [SerializeField]
        private bool persistAcrossScenes = true;

        private UnityMainThreadDispatcher _dispatcher;
        private UnityMainThreadDispatcher _runtimeEventDispatcher;
        private UnityRuntimeEventPublisher _eventPublisher;
        private UnityAgentRuntimeFacade _facade;
        private UnityBoundedCancellationDispatcher.Lease
            _dispatcherShutdownLease;
        private UnityBoundedCancellationDispatcher.Lease
            _runtimeEventDispatcherShutdownLease;
        private int _shutdownStarted;
        private int _shutdownRetryRequired;
        private int _lifecycleShutdownObserverStarted;
        private int _shutdownIncomplete;
        private Task<Exception> _dispatcherShutdownTask;
        private Task<Exception> _runtimeEventDispatcherShutdownTask;
        private Task _shutdownTask;

        public event Action<HeadlessRunOutcome> RunCompleted;

        public event Action<DurableRunOutcome> DurableRunCompleted;

        public event Action<RuntimeEvent> RuntimeEventPublished;

        public event Action<Exception> RunFaulted;

        public event Action<bool> ApplicationPauseChanged;

        public static UnityAgentRuntimeHost Instance
        {
            get { return _instance; }
        }

        public UnityMainThreadDispatcher Dispatcher
        {
            get
            {
                EnsureDispatcher();
                return _dispatcher;
            }
        }

        public bool IsConfigured
        {
            get { return _facade != null; }
        }

        public bool IsShutdownIncomplete
        {
            get { return Volatile.Read(ref _shutdownIncomplete) != 0; }
        }

        public IRuntimeEventPublisher EventPublisher
        {
            get
            {
                lock (_shutdownSync)
                {
                    ThrowIfShutdownStarted();
                    EnsureDispatcherLocked();
                    EnsureRuntimeEventDispatcherLocked();
                    if (_eventPublisher == null)
                    {
                        _eventPublisher = new UnityRuntimeEventPublisher(
                            _runtimeEventDispatcher,
                            PublishRuntimeEvent);
                    }

                    return _eventPublisher;
                }
            }
        }

        public long DroppedRuntimeEventCount
        {
            get
            {
                return _eventPublisher == null
                    ? 0
                    : _eventPublisher.DroppedEvents;
            }
        }

        public RuntimeControlPlane DurableControls
        {
            get
            {
                if (_facade == null)
                {
                    throw new InvalidOperationException(
                        "Configure the Unity runtime host before accessing "
                        + "durable run controls.");
                }

                return _facade.DurableControls;
            }
        }

        public static UnityAgentRuntimeHost EnsureCreated()
        {
            if (_instance != null)
            {
                return _instance;
            }

            var root = new GameObject("GameAgentRuntime");
            return root.AddComponent<UnityAgentRuntimeHost>();
        }

        public void Configure(
            IModelProvider modelProvider,
            ISessionStore sessionStore,
            UnityActionHandler actionHandler,
            IRuntimeClock clock = null,
            IRuntimeIdGenerator idGenerator = null,
            bool ownsSessionStore = false)
        {
            lock (_shutdownSync)
            {
                if (Volatile.Read(ref _shutdownStarted) != 0)
                {
                    throw new ObjectDisposedException(
                        nameof(UnityAgentRuntimeHost));
                }

                if (_facade != null)
                {
                    throw new InvalidOperationException(
                        "The Unity runtime host is already configured.");
                }

                EnsureDispatcher();
                _facade = new UnityAgentRuntimeFacade(
                    modelProvider,
                    sessionStore,
                    _dispatcher,
                    actionHandler,
                    clock ?? new SystemRuntimeClock(),
                    idGenerator ?? new GuidRuntimeIdGenerator(),
                    ownsSessionStore,
                    Math.Max(1, maxActiveRuns));
            }
        }

        public void Configure(
            IUnityDurableAgentRuntimeBackend backend,
            ISessionStore sessionStore,
            bool ownsSessionStore = false,
            bool ownsBackend = false)
        {
            ConfigureDurableFacade(
                () => new UnityAgentRuntimeFacade(
                    backend,
                    sessionStore,
                    ownsSessionStore,
                    ownsBackend,
                    maxActiveRuns: Math.Max(1, maxActiveRuns)));
        }

        public void Configure(
            IDurableAgentRuntime runtime,
            ISessionStore sessionStore,
            bool ownsSessionStore = false,
            bool ownsRuntime = false)
        {
            ConfigureDurableFacade(
                () => new UnityAgentRuntimeFacade(
                    runtime,
                    sessionStore,
                    ownsSessionStore,
                    ownsRuntime,
                    Math.Max(1, maxActiveRuns)));
        }

        public void Configure(
            BuiltGameAgentRuntime built,
            bool ownsBuiltRuntime = true)
        {
            if (built == null)
            {
                throw new ArgumentNullException(nameof(built));
            }

            ConfigureDurableFacade(
                () => new UnityAgentRuntimeFacade(
                    new BuiltUnityAgentRuntimeBackend(
                        built,
                        ownsBuiltRuntime),
                    built.SessionStore,
                    ownsSessionStore: false,
                    ownsBackend: true,
                    flushSessionStore: !ownsBuiltRuntime,
                    maxActiveRuns: Math.Max(1, maxActiveRuns)));
        }

        public Task<HeadlessRunOutcome> RunAsync(
            HeadlessRunRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before starting a run.");
            }

            var task = _facade.RunAsync(request, cancellationToken);
            _ = PublishHeadlessRunResultAsync(task);
            return task;
        }

        public Task<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before starting a run.");
            }

            var task = _facade.RunAsync(request, cancellationToken);
            _ = PublishDurableRunResultAsync(task);
            return task;
        }

        public Task<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation continuation = null,
            IGameOperationReconciler reconciler = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before resuming a run.");
            }

            var task = _facade.ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken);
            _ = PublishDurableRunResultAsync(task);
            return task;
        }

        public Task<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunResumeGuard guard,
            DurableRunContinuation continuation = null,
            IGameOperationReconciler reconciler = null,
            CancellationToken cancellationToken =
                default(CancellationToken))
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before resuming a run.");
            }

            var task = _facade.ResumeAsync(
                runId,
                guard,
                continuation,
                reconciler,
                cancellationToken);
            _ = PublishDurableRunResultAsync(task);
            return task;
        }

        /// <summary>
        /// Creates a multi-actor coordinator over this host's tracked durable
        /// runtime. Participant runs therefore share the host's capacity,
        /// cancellation, and shutdown lifecycle while Core retains ownership
        /// of batch concurrency and guarded participant recovery.
        /// </summary>
        public MultiActorDecisionCoordinator CreateMultiActorCoordinator(
            MultiActorCoordinatorOptions options = null,
            IMultiActorDecisionLifecycle lifecycle = null)
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before creating "
                    + "a multi-actor coordinator.");
            }

            return _facade.CreateMultiActorCoordinator(options, lifecycle);
        }

        public void CancelActiveRuns()
        {
            if (_facade != null)
            {
                _facade.CancelActiveRuns();
            }
        }

        public Task ShutdownAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return AwaitWithCancellationAsync(
                EnsureShutdownStarted(),
                cancellationToken);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            EnsureDispatcher();
            if (persistAcrossScenes)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        private void Update()
        {
            if (_dispatcher == null || _dispatcher.IsShutdown)
            {
                return;
            }

            _dispatcher.Pump(
                Math.Max(1, maxDispatchesPerFrame),
                Math.Max(0.01, dispatchBudgetMilliseconds));
            if (_runtimeEventDispatcher != null
                && !_runtimeEventDispatcher.IsShutdown)
            {
                _runtimeEventDispatcher.Pump(
                    Math.Max(1, maxRuntimeEventsPerFrame),
                    Math.Max(0.01, dispatchBudgetMilliseconds));
            }
        }

        private void OnApplicationPause(bool paused)
        {
            var handler = ApplicationPauseChanged;
            if (handler != null)
            {
                handler(paused);
            }
        }

        private void OnApplicationQuit()
        {
            BeginLifecycleShutdown();
            CompleteLifecycleShutdownBeforePlayerExit();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
                BeginLifecycleShutdown();
            }
        }

        private void BeginLifecycleShutdown()
        {
            if (Interlocked.Exchange(
                    ref _lifecycleShutdownObserverStarted,
                    1) == 0)
            {
                _ = ObserveLifecycleShutdownAsync();
            }
        }

        private async Task ObserveLifecycleShutdownAsync()
        {
            Exception finalException = null;
            for (var attempt = 0;
                 attempt < MaximumLifecycleRetryAttempts;
                 attempt++)
            {
                try
                {
                    await EnsureShutdownStarted().ConfigureAwait(false);
                    finalException = null;
                }
                catch (Exception exception)
                {
                    finalException = exception;
                }

                if (Volatile.Read(ref _shutdownRetryRequired) == 0)
                {
                    if (finalException != null)
                    {
                        Debug.LogException(finalException);
                    }

                    return;
                }

                var delayMilliseconds = Math.Min(
                    MaximumLifecycleRetryDelayMilliseconds,
                    InitialLifecycleRetryDelayMilliseconds
                    * (1 << Math.Min(attempt, 4)));
                await Task.Delay(
                        TimeSpan.FromMilliseconds(delayMilliseconds))
                    .ConfigureAwait(false);
            }

            Volatile.Write(ref _shutdownIncomplete, 1);
            Debug.LogException(
                finalException
                ?? new InvalidOperationException(
                    "The agent runtime exhausted bounded lifecycle shutdown retries."));
        }

        private void CompleteLifecycleShutdownBeforePlayerExit()
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Exception finalException = null;
            while (elapsed.Elapsed < PlayerQuitShutdownTimeout)
            {
                var shutdown = EnsureShutdownStarted();
                try
                {
                    var remaining =
                        PlayerQuitShutdownTimeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero
                        || !shutdown.Wait(remaining))
                    {
                        break;
                    }

                    finalException = null;
                }
                catch (Exception exception)
                {
                    finalException = exception;
                }

                if (Volatile.Read(ref _shutdownRetryRequired) == 0)
                {
                    if (finalException != null)
                    {
                        Debug.LogException(finalException);
                    }

                    return;
                }

                Thread.Sleep(LifecycleAdmissionRetryDelay);
            }

            Volatile.Write(ref _shutdownIncomplete, 1);
            Debug.LogException(
                new TimeoutException(
                    "The agent runtime did not finish bounded shutdown before player exit."));
        }

        private Task EnsureShutdownStarted()
        {
            lock (_shutdownSync)
            {
                if (_shutdownTask == null
                    || (_shutdownTask.IsCompleted
                        && Volatile.Read(
                            ref _shutdownRetryRequired) != 0))
                {
                    Volatile.Write(ref _shutdownStarted, 1);
                    Volatile.Write(ref _shutdownRetryRequired, 0);
                    Volatile.Write(ref _shutdownIncomplete, 0);
                    _shutdownTask = ShutdownCoreAsync();
                }

                return _shutdownTask;
            }
        }

        private async Task ShutdownCoreAsync()
        {
            var failures = new List<Exception>();
            var retryRequired = false;
            Task dispatcherDrain = null;
            Task runtimeEventDispatcherDrain = null;
            var dispatcherOwnerDrainCompleted = true;
            if (_facade != null)
            {
                _facade.RequestShutdown();
            }

            var admissionTasks =
                new List<Task<HostCancellationAdmissionResult>>();
            if (_dispatcher != null)
            {
                admissionTasks.Add(
                    EnsureDispatcherShutdownDispatchedAsync(
                        _dispatcher,
                        runtimeEvents: false));
            }

            if (_runtimeEventDispatcher != null)
            {
                admissionTasks.Add(
                    EnsureDispatcherShutdownDispatchedAsync(
                        _runtimeEventDispatcher,
                        runtimeEvents: true));
            }

            try
            {
                if (admissionTasks.Count != 0)
                {
                    var admissions = await Task.WhenAll(admissionTasks)
                        .ConfigureAwait(false);
                    var lifecycleCompletions =
                        new List<Task<Exception>>();
                    foreach (var admission in admissions)
                    {
                        if (admission.Completion == null)
                        {
                            failures.Add(admission.Failure);
                            retryRequired = true;
                        }
                        else
                        {
                            lifecycleCompletions.Add(
                                admission.Completion);
                        }
                    }

                    if (lifecycleCompletions.Count != 0)
                    {
                        var observations =
                            new Task<Exception>[
                                lifecycleCompletions.Count];
                        for (var index = 0;
                             index < lifecycleCompletions.Count;
                             index++)
                        {
                            observations[index] =
                                ObserveLifecycleCompletionAsync(
                                    lifecycleCompletions[index]);
                        }

                        var lifecycleFailures = await Task
                            .WhenAll(observations)
                            .ConfigureAwait(false);
                        for (var index = 0;
                             index < lifecycleFailures.Length;
                             index++)
                        {
                            if (lifecycleFailures[index] != null)
                            {
                                failures.Add(lifecycleFailures[index]);
                            }

                            if (!lifecycleCompletions[index].IsCompleted)
                            {
                                retryRequired = true;
                            }
                        }
                    }
                }

                var drains = new List<Task>();
                if (_dispatcher != null)
                {
                    dispatcherDrain = _dispatcher
                        .WaitForRunningWorkAsync(CancellationToken.None)
                        .AsTask();
                    drains.Add(dispatcherDrain);
                }

                if (_runtimeEventDispatcher != null)
                {
                    runtimeEventDispatcherDrain = _runtimeEventDispatcher
                        .WaitForRunningWorkAsync(CancellationToken.None)
                        .AsTask();
                    drains.Add(runtimeEventDispatcherDrain);
                }

                if (drains.Count != 0)
                {
                    var drain = Task.WhenAll(drains);
                    if (await WaitForLifecycleWorkAsync(drain)
                            .ConfigureAwait(false))
                    {
                        await drain.ConfigureAwait(false);
                    }
                    else
                    {
                        ObserveLateFault(drain);
                        dispatcherOwnerDrainCompleted = false;
                        failures.Add(
                            new TimeoutException(
                                "Timed out while draining Unity dispatcher work."));
                        retryRequired = true;
                    }
                }

                try
                {
                    if (dispatcherOwnerDrainCompleted && _facade != null)
                    {
                        await _facade
                            .ShutdownAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    if (_facade.RequiresShutdownRetry
                        || _facade.RequiresShutdownCancellationAdmission)
                    {
                        retryRequired = true;
                    }
                }
            }
            finally
            {
                try
                {
                    if (_dispatcher != null
                        && _dispatcher.IsShutdown)
                    {
                        _dispatcher.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                try
                {
                    if (_runtimeEventDispatcher != null
                        && _runtimeEventDispatcher.IsShutdown)
                    {
                        _runtimeEventDispatcher.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                ReleaseLifecycleLeaseAfterDrain(
                    _dispatcherShutdownLease,
                    _dispatcherShutdownTask,
                    dispatcherDrain);
                ReleaseLifecycleLeaseAfterDrain(
                    _runtimeEventDispatcherShutdownLease,
                    _runtimeEventDispatcherShutdownTask,
                    runtimeEventDispatcherDrain);

                Volatile.Write(
                    ref _shutdownRetryRequired,
                    retryRequired ? 1 : 0);
                Volatile.Write(
                    ref _shutdownIncomplete,
                    retryRequired || failures.Count != 0 ? 1 : 0);
            }

            if (failures.Count != 0)
            {
                throw new AggregateException(
                        "One or more Unity host shutdown operations failed.",
                        failures)
                    .Flatten();
            }
        }

        private async Task<HostCancellationAdmissionResult>
            EnsureDispatcherShutdownDispatchedAsync(
                UnityMainThreadDispatcher dispatcher,
                bool runtimeEvents)
        {
            Exception failure = null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (TryDispatchDispatcherShutdown(
                        dispatcher,
                        runtimeEvents,
                        out var completion))
                {
                    return new HostCancellationAdmissionResult(
                        completion,
                        null);
                }

                if (completion.IsCompleted
                    && completion.Status == TaskStatus.RanToCompletion)
                {
                    failure = completion.Result;
                }

                if (elapsed.Elapsed >= LifecycleShutdownTimeout)
                {
                    return new HostCancellationAdmissionResult(
                        null,
                        failure
                        ?? new InvalidOperationException(
                            "Unity dispatcher shutdown admission failed."));
                }

                await Task.Delay(LifecycleAdmissionRetryDelay)
                    .ConfigureAwait(false);
            }
        }

        private bool TryDispatchDispatcherShutdown(
            UnityMainThreadDispatcher dispatcher,
            bool runtimeEvents,
            out Task<Exception> completion)
        {
            lock (_shutdownSync)
            {
                var existing = runtimeEvents
                    ? _runtimeEventDispatcherShutdownTask
                    : _dispatcherShutdownTask;
                if (existing != null)
                {
                    completion = existing;
                    return true;
                }

                try
                {
                    var lease = runtimeEvents
                        ? _runtimeEventDispatcherShutdownLease
                        : _dispatcherShutdownLease;
                    if (!UnityLifecycleCancellationDispatcher.TryDispatch(
                            lease,
                            dispatcher.Shutdown,
                            out completion))
                    {
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    completion = Task.FromResult(exception);
                    return false;
                }

                if (runtimeEvents)
                {
                    _runtimeEventDispatcherShutdownTask = completion;
                }
                else
                {
                    _dispatcherShutdownTask = completion;
                }

                return true;
            }
        }

        private static async Task<Exception>
            ObserveLifecycleCompletionAsync(Task<Exception> completion)
        {
            if (!await WaitForLifecycleWorkAsync(completion)
                    .ConfigureAwait(false))
            {
                ObserveLateFault(completion);
                return new TimeoutException(
                    "Timed out while cancelling a Unity dispatcher.");
            }

            try
            {
                return await completion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static async Task<bool> WaitForLifecycleWorkAsync(
            Task work)
        {
            if (work.IsCompleted)
            {
                return true;
            }

            var completed = await Task.WhenAny(
                    work,
                    Task.Delay(LifecycleShutdownTimeout))
                .ConfigureAwait(false);
            return ReferenceEquals(completed, work);
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

        private static void ReleaseLifecycleLeaseAfterDrain(
            UnityBoundedCancellationDispatcher.Lease lease,
            Task cancellation,
            Task drain)
        {
            if (lease == null || cancellation == null)
            {
                return;
            }

            var barrier = drain == null
                ? cancellation
                : Task.WhenAll(cancellation, drain);
            UnityLifecycleCancellationDispatcher.ReleaseLeaseAfter(
                lease,
                barrier);
        }

        private static async Task AwaitWithCancellationAsync(
            Task shutdown,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cancellationToken.CanBeCanceled || shutdown.IsCompleted)
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

        private async Task PublishHeadlessRunResultAsync(
            Task<HeadlessRunOutcome> run)
        {
            try
            {
                var outcome = await run.ConfigureAwait(false);
                if (_dispatcher == null)
                {
                    return;
                }

                PostObserver(
                    () =>
                    {
                        var handler = RunCompleted;
                        if (handler != null)
                        {
                            handler(outcome);
                        }
                    });
            }
            catch (Exception exception)
            {
                if (_dispatcher == null)
                {
                    return;
                }

                PostFault(exception);
            }
        }

        private async Task PublishDurableRunResultAsync(
            Task<DurableRunOutcome> run)
        {
            try
            {
                var outcome = await run.ConfigureAwait(false);
                if (_dispatcher == null)
                {
                    return;
                }

                PostObserver(
                    () =>
                    {
                        var handler = DurableRunCompleted;
                        if (handler != null)
                        {
                            handler(outcome);
                        }
                    });
            }
            catch (Exception exception)
            {
                if (_dispatcher == null)
                {
                    return;
                }

                PostFault(exception);
            }
        }

        private void PostFault(Exception exception)
        {
            PostObserver(
                () =>
                {
                    var handler = RunFaulted;
                    if (handler != null)
                    {
                        handler(exception);
                    }
                });
        }

        private void PostObserver(Action observer)
        {
            if (!_dispatcher.TryPost(observer))
            {
                Debug.LogException(
                    new InvalidOperationException(
                        "A Unity runtime observer callback was rejected "
                        + "because the dispatcher is full or shutting down."));
            }
        }

        private void PublishRuntimeEvent(RuntimeEvent runtimeEvent)
        {
            var handler = RuntimeEventPublished;
            if (handler != null)
            {
                handler(runtimeEvent);
            }
        }

        private void ConfigureDurableFacade(
            Func<UnityAgentRuntimeFacade> createFacade)
        {
            if (createFacade == null)
            {
                throw new ArgumentNullException(nameof(createFacade));
            }

            lock (_shutdownSync)
            {
                if (Volatile.Read(ref _shutdownStarted) != 0)
                {
                    throw new ObjectDisposedException(
                        nameof(UnityAgentRuntimeHost));
                }

                if (_facade != null)
                {
                    throw new InvalidOperationException(
                        "The Unity runtime host is already configured.");
                }

                EnsureDispatcher();
                _facade = createFacade();
            }
        }

        private void EnsureDispatcher()
        {
            lock (_shutdownSync)
            {
                EnsureDispatcherLocked();
            }
        }

        private void EnsureDispatcherLocked()
        {
            if (_dispatcher == null)
            {
                ThrowIfShutdownStarted();
                var lease = AcquireLifecycleLease();
                try
                {
                    _dispatcher = new UnityMainThreadDispatcher(
                        Math.Max(1, dispatcherCapacity));
                    _dispatcherShutdownLease = lease;
                    _dispatcher.UnhandledException +=
                        exception => Debug.LogException(exception);
                }
                catch
                {
                    UnityLifecycleCancellationDispatcher.ReleaseLease(
                        lease);
                    throw;
                }
            }
        }

        private void EnsureRuntimeEventDispatcherLocked()
        {
            if (_runtimeEventDispatcher == null)
            {
                ThrowIfShutdownStarted();
                EnsureDispatcherLocked();
                if (!_dispatcher.IsMainThread)
                {
                    throw new InvalidOperationException(
                        "The Unity runtime event publisher must first be "
                        + "created on the Unity main thread.");
                }

                var lease = AcquireLifecycleLease();
                try
                {
                    _runtimeEventDispatcher =
                        new UnityMainThreadDispatcher(
                            Math.Max(1, runtimeEventCapacity));
                    _runtimeEventDispatcherShutdownLease = lease;
                    _runtimeEventDispatcher.UnhandledException +=
                        exception => Debug.LogException(exception);
                }
                catch
                {
                    UnityLifecycleCancellationDispatcher.ReleaseLease(
                        lease);
                    throw;
                }
            }
        }

        private static UnityBoundedCancellationDispatcher.Lease
            AcquireLifecycleLease()
        {
            if (UnityLifecycleCancellationDispatcher.TryAcquireLease(
                    out var lease))
            {
                return lease;
            }

            throw new InvalidOperationException(
                "The process lifecycle cancellation capacity is exhausted.");
        }

        private void ThrowIfShutdownStarted()
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(UnityAgentRuntimeHost));
            }
        }

        private sealed class HostCancellationAdmissionResult
        {
            internal HostCancellationAdmissionResult(
                Task<Exception> completion,
                Exception failure)
            {
                Completion = completion;
                Failure = failure;
            }

            internal Task<Exception> Completion { get; private set; }

            internal Exception Failure { get; private set; }
        }

        [RuntimeInitializeOnLoadMethod(
            RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _instance = null;
        }
    }
}

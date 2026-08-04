using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;
using GameAgent.Generation;
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
        private UnityTerminalObserverQueue _terminalObservers;
        private UnityRuntimeEventPublisher _eventPublisher;
        private UnityAgentRuntimeFacade _facade;
        private GenerationRuntime _generationRuntime;
        private CancellationTokenSource _generationLifetime;
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

        public event Action<RoutedExecutionOutcome> RoutedRunCompleted;

        public event Action<SimpleCompletionOutcome> CompletionCompleted;

        public event Action<RuntimeEvent> RuntimeEventPublished;

        public event Action<GenerationJob> GenerationUpdated;

        public event Action<Exception> GenerationFaulted;

        public event Action<Exception> RunFaulted;

        public event Action<UnityRunFault> RunFaultedDetailed;

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

        public bool IsGenerationConfigured
        {
            get { return _generationRuntime != null; }
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

        public int PendingTerminalObserverCount
        {
            get
            {
                return _terminalObservers == null
                    ? 0
                    : _terminalObservers.PendingCount;
            }
        }

        public int TerminalObserverReservationCount
        {
            get
            {
                return _terminalObservers == null
                    ? 0
                    : _terminalObservers.ReservedCount;
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

        public void ConfigureGeneration(GenerationRuntime runtime)
        {
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            lock (_shutdownSync)
            {
                ThrowIfShutdownStarted();
                if (_generationRuntime != null)
                {
                    throw new InvalidOperationException(
                        "The Unity generation runtime is already configured.");
                }

                EnsureDispatcher();
                _generationRuntime = runtime;
                _generationLifetime = new CancellationTokenSource();
            }
        }

        public Task<GenerationJob> SubmitGenerationAsync(
            GenerationRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var snapshot = GenerationRequestSnapshotter.Snapshot(request);
            return RunGenerationAsync(
                (runtime, token) => runtime.SubmitAsync(snapshot, token),
                cancellationToken);
        }

        public Task<GenerationJob> RefreshGenerationAsync(
            string operationId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunGenerationAsync(
                (runtime, token) => runtime.RefreshAsync(operationId, token),
                cancellationToken);
        }

        public Task<GenerationJob> WaitForGenerationAsync(
            string operationId,
            TimeSpan? timeout = null,
            TimeSpan? pollInterval = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunGenerationAsync(
                (runtime, token) => runtime.WaitForCompletionAsync(
                    operationId,
                    timeout,
                    pollInterval,
                    token),
                cancellationToken);
        }

        public Task<GenerationJob> CancelGenerationAsync(
            string operationId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunGenerationAsync(
                (runtime, token) => runtime.RequestCancellationAsync(
                    operationId,
                    token),
                cancellationToken);
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

            var terminal = ReserveTerminalObserver();
            try
            {
                var task = _facade.RunAsync(request, cancellationToken);
                _ = PublishHeadlessRunResultAsync(
                    task,
                    terminal,
                    request == null || request.Run == null
                        ? null
                        : request.Run.RunId);
                return task;
            }
            catch
            {
                terminal.Dispose();
                throw;
            }
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

            var terminal = ReserveTerminalObserver();
            try
            {
                var task = _facade.RunAsync(request, cancellationToken);
                _ = PublishDurableRunResultAsync(
                    task,
                    terminal,
                    "durable_run",
                    request == null || request.Run == null
                        ? null
                        : request.Run.RunId,
                    null,
                    null);
                return task;
            }
            catch
            {
                terminal.Dispose();
                throw;
            }
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

            var terminal = ReserveTerminalObserver();
            try
            {
                var task = _facade.ResumeAsync(
                    runId,
                    continuation,
                    reconciler,
                    cancellationToken);
                _ = PublishDurableRunResultAsync(
                    task,
                    terminal,
                    "durable_resume",
                    runId,
                    null,
                    null);
                return task;
            }
            catch
            {
                terminal.Dispose();
                throw;
            }
        }

        public Task<RoutedExecutionOutcome> RunRoutedAsync(
            RoutedExecutionRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before starting a routed run.");
            }

            var terminal = ReserveTerminalObserver();
            try
            {
                var task = _facade.RunRoutedAsync(request, cancellationToken);
                _ = PublishRoutedRunResultAsync(
                    task,
                    terminal,
                    request == null || request.Run == null
                        || request.Run.Run == null
                        ? null
                        : request.Run.Run.RunId,
                    request == null || request.Workflow == null
                        ? null
                        : request.Workflow.RunKey);
                return task;
            }
            catch
            {
                terminal.Dispose();
                throw;
            }
        }

        public Task<SimpleCompletionOutcome> CompleteAsync(
            SimpleCompletionRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before starting a completion.");
            }

            var terminal = ReserveTerminalObserver();
            try
            {
                var task = _facade.CompleteAsync(request, cancellationToken);
                _ = PublishCompletionResultAsync(
                    task,
                    terminal,
                    request == null ? null : request.OperationId);
                return task;
            }
            catch
            {
                terminal.Dispose();
                throw;
            }
        }

        public Task<ChildAgentRunResult> RunChildAsync(
            string parentRunId,
            DurableRunRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before starting a child run.");
            }

            var terminal = ReserveTerminalObserver();
            try
            {
                var task = _facade.RunChildAsync(
                    parentRunId,
                    request,
                    cancellationToken);
                _ = PublishChildRunResultAsync(
                    task,
                    terminal,
                    request == null || request.Run == null
                        ? null
                        : request.Run.RunId,
                    parentRunId);
                return task;
            }
            catch
            {
                terminal.Dispose();
                throw;
            }
        }

        public Task<ChildAgentRunResult> RunChildAsync(
            AgentRun parentRun,
            DurableRunRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before starting a child run.");
            }

            var terminal = ReserveTerminalObserver();
            try
            {
                var task = _facade.RunChildAsync(
                    parentRun,
                    request,
                    cancellationToken);
                _ = PublishChildRunResultAsync(
                    task,
                    terminal,
                    request == null || request.Run == null
                        ? null
                        : request.Run.RunId,
                    parentRun == null ? null : parentRun.RunId);
                return task;
            }
            catch
            {
                terminal.Dispose();
                throw;
            }
        }

        public int CancelChildren(string parentRunId)
        {
            if (_facade == null)
            {
                throw new InvalidOperationException(
                    "Configure the Unity runtime host before cancelling child runs.");
            }

            return _facade.CancelChildren(parentRunId);
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

            var terminal = ReserveTerminalObserver();
            try
            {
                var task = _facade.ResumeAsync(
                    runId,
                    guard,
                    continuation,
                    reconciler,
                    cancellationToken);
                _ = PublishDurableRunResultAsync(
                    task,
                    terminal,
                    "durable_resume",
                    runId,
                    null,
                    null);
                return task;
            }
            catch
            {
                terminal.Dispose();
                throw;
            }
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
            if (_dispatcher != null && !_dispatcher.IsShutdown)
            {
                _dispatcher.Pump(
                    Math.Max(1, maxDispatchesPerFrame),
                    Math.Max(0.01, dispatchBudgetMilliseconds));
            }

            if (_terminalObservers != null)
            {
                _terminalObservers.Pump(
                    Math.Max(1, maxDispatchesPerFrame),
                    Math.Max(0.01, dispatchBudgetMilliseconds),
                    Debug.LogException);
            }

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
                InvokeObserversIsolated(handler, paused);
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
            Task terminalPublisherDrain = null;
            Task generationCancellationDrain = null;
            var dispatcherOwnerDrainCompleted = true;
            if (_terminalObservers != null)
            {
                terminalPublisherDrain = _terminalObservers.StopAccepting();
            }

            if (_facade != null)
            {
                _facade.RequestShutdown();
            }

            var generationLifetime = _generationLifetime;
            if (generationLifetime != null)
            {
                generationCancellationDrain = Task.Run(
                    () =>
                    {
                        try
                        {
                            generationLifetime.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    });
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

                if (generationCancellationDrain != null)
                {
                    drains.Add(generationCancellationDrain);
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

                if (terminalPublisherDrain != null)
                {
                    if (await WaitForLifecycleWorkAsync(
                                terminalPublisherDrain)
                            .ConfigureAwait(false))
                    {
                        await terminalPublisherDrain.ConfigureAwait(false);
                    }
                    else
                    {
                        ObserveLateFault(terminalPublisherDrain);
                        failures.Add(
                            new TimeoutException(
                                "Timed out while publishing Unity terminal observers."));
                        retryRequired = true;
                    }
                }
            }
            finally
            {
                try
                {
                    if (_terminalObservers != null)
                    {
                        _ = _terminalObservers.StopAccepting();
                    }

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

                try
                {
                    if (generationLifetime != null
                        && generationCancellationDrain != null
                        && generationCancellationDrain.IsCompleted
                        && terminalPublisherDrain != null
                        && terminalPublisherDrain.IsCompleted
                        && ReferenceEquals(
                            Interlocked.CompareExchange(
                                ref _generationLifetime,
                                null,
                                generationLifetime),
                            generationLifetime))
                    {
                        generationLifetime.Dispose();
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

        private UnityTerminalObserverQueue.Reservation
            ReserveTerminalObserver()
        {
            var queue = _terminalObservers;
            if (queue == null || !queue.TryReserve(out var reservation))
            {
                throw new InvalidOperationException(
                    "The Unity terminal-observer capacity is exhausted. "
                    + "Pump the runtime host before starting more work.");
            }

            return reservation;
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

        private Task<GenerationJob> RunGenerationAsync(
            Func<GenerationRuntime, CancellationToken, ValueTask<GenerationJob>> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            GenerationRuntime runtime;
            CancellationTokenSource linked;
            lock (_shutdownSync)
            {
                ThrowIfShutdownStarted();
                runtime = _generationRuntime
                          ?? throw new InvalidOperationException(
                              "Configure generation before starting generation work.");
                linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _generationLifetime.Token);
            }

            UnityTerminalObserverQueue.Reservation terminal = null;
            try
            {
                terminal = ReserveTerminalObserver();
                var task = operation(runtime, linked.Token).AsTask();
                _ = PublishGenerationResultAsync(task, terminal, linked);
                return task;
            }
            catch
            {
                linked.Dispose();
                if (terminal != null)
                {
                    terminal.Dispose();
                }
                throw;
            }
        }

        private async Task PublishGenerationResultAsync(
            Task<GenerationJob> operation,
            UnityTerminalObserverQueue.Reservation terminal,
            CancellationTokenSource linked)
        {
            try
            {
                var job = await operation.ConfigureAwait(false);
                PostObserver(
                    terminal,
                    () =>
                    {
                        var handler = GenerationUpdated;
                        if (handler != null)
                        {
                            InvokeObserversIsolated(handler, job);
                        }
                    });
            }
            catch (Exception exception)
            {
                PostObserver(
                    terminal,
                    () =>
                    {
                        var handler = GenerationFaulted;
                        if (handler != null)
                        {
                            InvokeObserversIsolated(handler, exception);
                        }
                    });
            }
            finally
            {
                linked.Dispose();
                terminal.Dispose();
            }
        }

        private async Task PublishHeadlessRunResultAsync(
            Task<HeadlessRunOutcome> run,
            UnityTerminalObserverQueue.Reservation terminal,
            string runId)
        {
            try
            {
                var outcome = await run.ConfigureAwait(false);
                PostObserver(
                    terminal,
                    () =>
                    {
                        var handler = RunCompleted;
                        if (handler != null)
                        {
                            InvokeObserversIsolated(handler, outcome);
                        }
                    });
            }
            catch (Exception exception)
            {
                PostFault(
                    terminal,
                    "headless_run",
                    runId,
                    null,
                    null,
                    true,
                    exception);
            }
            finally
            {
                terminal.Dispose();
            }
        }

        private async Task PublishDurableRunResultAsync(
            Task<DurableRunOutcome> run,
            UnityTerminalObserverQueue.Reservation terminal,
            string operationKind,
            string runId,
            string operationId,
            string parentRunId)
        {
            try
            {
                var outcome = await run.ConfigureAwait(false);
                PostObserver(
                    terminal,
                    () =>
                    {
                        var handler = DurableRunCompleted;
                        if (handler != null)
                        {
                            InvokeObserversIsolated(handler, outcome);
                        }
                    });
            }
            catch (Exception exception)
            {
                PostFault(
                    terminal,
                    operationKind,
                    runId,
                    operationId,
                    parentRunId,
                    true,
                    exception);
            }
            finally
            {
                terminal.Dispose();
            }
        }

        private async Task PublishChildRunResultAsync(
            Task<ChildAgentRunResult> run,
            UnityTerminalObserverQueue.Reservation terminal,
            string runId,
            string parentRunId)
        {
            try
            {
                var result = await run.ConfigureAwait(false);
                PostObserver(
                    terminal,
                    () =>
                    {
                        var handler = DurableRunCompleted;
                        if (handler != null)
                        {
                            InvokeObserversIsolated(
                                handler,
                                result.Outcome);
                        }
                    });
            }
            catch (Exception exception)
            {
                PostFault(
                    terminal,
                    "child_run",
                    runId,
                    null,
                    parentRunId,
                    true,
                    exception);
            }
            finally
            {
                terminal.Dispose();
            }
        }

        private async Task PublishRoutedRunResultAsync(
            Task<RoutedExecutionOutcome> run,
            UnityTerminalObserverQueue.Reservation terminal,
            string runId,
            string operationId)
        {
            try
            {
                var outcome = await run.ConfigureAwait(false);
                PostObserver(
                    terminal,
                    () =>
                    {
                        var handler = RoutedRunCompleted;
                        if (handler != null)
                        {
                            InvokeObserversIsolated(handler, outcome);
                        }
                    });
            }
            catch (Exception exception)
            {
                PostFault(
                    terminal,
                    "routed_run",
                    runId,
                    operationId,
                    null,
                    true,
                    exception);
            }
            finally
            {
                terminal.Dispose();
            }
        }

        private async Task PublishCompletionResultAsync(
            Task<SimpleCompletionOutcome> run,
            UnityTerminalObserverQueue.Reservation terminal,
            string operationId)
        {
            try
            {
                var outcome = await run.ConfigureAwait(false);
                PostObserver(
                    terminal,
                    () =>
                    {
                        var handler = CompletionCompleted;
                        if (handler != null)
                        {
                            InvokeObserversIsolated(handler, outcome);
                        }
                    });
            }
            catch (Exception exception)
            {
                PostFault(
                    terminal,
                    "completion",
                    null,
                    operationId,
                    null,
                    false,
                    exception);
            }
            finally
            {
                terminal.Dispose();
            }
        }

        private void PostFault(
            UnityTerminalObserverQueue.Reservation terminal,
            string operationKind,
            string runId,
            string operationId,
            string parentRunId,
            bool reconciliationRequired,
            Exception exception)
        {
            var fault = new UnityRunFault(
                operationKind,
                runId,
                operationId,
                parentRunId,
                reconciliationRequired,
                exception);
            PostObserver(
                terminal,
                () =>
                {
                    var detailed = RunFaultedDetailed;
                    if (detailed != null)
                    {
                        InvokeObserversIsolated(detailed, fault);
                    }

                    var handler = RunFaulted;
                    if (handler != null)
                    {
                        InvokeObserversIsolated(handler, exception);
                    }
                });
        }

        private static void InvokeObserversIsolated<T>(
            Action<T> handlers,
            T value)
        {
            var invocationList = handlers.GetInvocationList();
            foreach (var candidate in invocationList)
            {
                try
                {
                    ((Action<T>)candidate)(value);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        private static void PostObserver(
            UnityTerminalObserverQueue.Reservation terminal,
            Action observer)
        {
            if (!terminal.Publish(observer))
            {
                Debug.LogException(
                    new InvalidOperationException(
                        "A Unity terminal observer was rejected because "
                        + "the host is shutting down."));
            }
        }

        private void PublishRuntimeEvent(RuntimeEvent runtimeEvent)
        {
            var handler = RuntimeEventPublished;
            if (handler != null)
            {
                InvokeObserversIsolated(handler, runtimeEvent);
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
                    _terminalObservers = new UnityTerminalObserverQueue(
                        Math.Max(1, maxActiveRuns));
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

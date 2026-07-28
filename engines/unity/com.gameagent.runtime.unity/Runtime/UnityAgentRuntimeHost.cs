using System;
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
        private int _shutdownStarted;
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
            _ = ObserveLifecycleShutdownAsync(
                EnsureShutdownStarted());
        }

        private static async Task ObserveLifecycleShutdownAsync(
            Task shutdown)
        {
            try
            {
                await shutdown.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private Task EnsureShutdownStarted()
        {
            lock (_shutdownSync)
            {
                if (_shutdownTask == null)
                {
                    Volatile.Write(ref _shutdownStarted, 1);
                    _shutdownTask = Task.Run(ShutdownCoreAsync);
                }

                return _shutdownTask;
            }
        }

        private async Task ShutdownCoreAsync()
        {
            if (_facade != null)
            {
                _facade.RequestShutdown();
            }

            if (_dispatcher != null)
            {
                _dispatcher.Shutdown();
            }
            if (_runtimeEventDispatcher != null)
            {
                _runtimeEventDispatcher.Shutdown();
            }

            try
            {
                if (_dispatcher != null)
                {
                    await _dispatcher.WaitForRunningWorkAsync(
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                if (_runtimeEventDispatcher != null)
                {
                    await _runtimeEventDispatcher.WaitForRunningWorkAsync(
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                if (_facade != null)
                {
                    await _facade.ShutdownAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (_dispatcher != null)
                {
                    _dispatcher.Dispose();
                }
                if (_runtimeEventDispatcher != null)
                {
                    _runtimeEventDispatcher.Dispose();
                }
            }
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
                _dispatcher = new UnityMainThreadDispatcher(
                    Math.Max(1, dispatcherCapacity));
                _dispatcher.UnhandledException +=
                    exception => Debug.LogException(exception);
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

                _runtimeEventDispatcher = new UnityMainThreadDispatcher(
                    Math.Max(1, runtimeEventCapacity));
                _runtimeEventDispatcher.UnhandledException +=
                    exception => Debug.LogException(exception);
            }
        }

        private void ThrowIfShutdownStarted()
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(UnityAgentRuntimeHost));
            }
        }

        [RuntimeInitializeOnLoadMethod(
            RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _instance = null;
        }
    }
}

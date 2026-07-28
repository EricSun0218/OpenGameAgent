using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using GameAgent.Core;
using GameAgent.Protocol;
using Godot;
using GodotArray = global::Godot.Collections.Array;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot;

internal static class GodotCancellationDispatcher
{
    internal const int Capacity = 8;
    internal const int PendingCapacity = 64;
    internal const int ReservationCapacity = Capacity + PendingCapacity;

    private static readonly object Sync = new();
    private static readonly Queue<PendingCancellation> Pending = new();
    private static int _activeCount;
    private static int _reservationCount;

    internal static int ActiveCount => Volatile.Read(ref _activeCount);

    internal static int ReservationCount
    {
        get
        {
            lock (Sync)
            {
                return _reservationCount;
            }
        }
    }

    internal static int PendingCount
    {
        get
        {
            lock (Sync)
            {
                return Pending.Count;
            }
        }
    }

    internal static bool TryReserve(out Reservation? reservation)
    {
        lock (Sync)
        {
            if (_reservationCount >= ReservationCapacity)
            {
                reservation = null;
                return false;
            }

            _reservationCount++;
            reservation = new Reservation();
            return true;
        }
    }

    internal static bool TryDispatchReserved(
        Reservation reservation,
        Action cancellation,
        out Task<Exception?> completion)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(cancellation);
        var pending = new PendingCancellation(cancellation);
        var schedule = false;
        lock (Sync)
        {
            if (!reservation.TryMarkDispatched())
            {
                completion = Task.FromResult<Exception?>(
                    new InvalidOperationException(
                        "The lifecycle cancellation reservation is not available."));
                return false;
            }

            if (_activeCount < Capacity)
            {
                _activeCount++;
                schedule = true;
            }
            else if (Pending.Count < PendingCapacity)
            {
                Pending.Enqueue(pending);
            }
            else
            {
                reservation.ResetDispatch();
                completion = Task.FromResult<Exception?>(
                    new InvalidOperationException(
                        "The lifecycle cancellation reservation invariant was violated."));
                return false;
            }
        }

        completion = pending.Completion.Task;
        if (schedule)
        {
            try
            {
                _ = Task.Factory.StartNew(
                    () => Execute(pending),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach
                    | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch (Exception exception)
            {
                lock (Sync)
                {
                    _activeCount--;
                }

                reservation.ResetDispatch();
                completion = Task.FromResult<Exception?>(exception);
                return false;
            }
        }

        return true;
    }

    private static void Execute(PendingCancellation pending)
    {
        PendingCancellation? current = pending;
        while (current is not null)
        {
            Exception? failure = null;
            try
            {
                current.Cancellation();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            current.Completion.TrySetResult(failure);
            lock (Sync)
            {
                if (Pending.Count == 0)
                {
                    _activeCount--;
                    current = null;
                }
                else
                {
                    current = Pending.Dequeue();
                }
            }
        }
    }

    private static void ReleaseReservation(Reservation reservation)
    {
        lock (Sync)
        {
            if (!reservation.TryMarkReleased())
            {
                return;
            }

            _reservationCount--;
        }
    }

    internal sealed class Reservation : IDisposable
    {
        private int _state;

        internal bool TryMarkDispatched() =>
            Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        internal void ResetDispatch()
        {
            _ = Interlocked.CompareExchange(ref _state, 0, 1);
        }

        internal bool TryMarkReleased()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if (state == 2)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(
                        ref _state,
                        2,
                        state) == state)
                {
                    return true;
                }
            }
        }

        public void Dispose()
        {
            ReleaseReservation(this);
        }
    }

    private sealed class PendingCancellation
    {
        internal PendingCancellation(Action cancellation)
        {
            Cancellation = cancellation;
        }

        internal Action Cancellation { get; }

        internal TaskCompletionSource<Exception?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

[global::Godot.GlobalClass]
public partial class GameAgentRuntimeNode : global::Godot.Node
{
    private const int MaximumLifecycleRetryAttempts = 8;
    private const int InitialLifecycleRetryDelayMilliseconds = 50;
    private const int MaximumLifecycleRetryDelayMilliseconds = 1000;
    [Signal]
    public delegate void RuntimeStartedEventHandler(GodotDictionary status);

    [Signal]
    public delegate void RuntimeEventPublishedEventHandler(GodotDictionary runtimeEvent);

    [Signal]
    public delegate void RunCompletedEventHandler(GodotDictionary outcome);

    [Signal]
    public delegate void RunFailedEventHandler(GodotDictionary error);

    [Signal]
    public delegate void RuntimeStoppedEventHandler(GodotDictionary summary);

    [Signal]
    public delegate void RuntimeErrorEventHandler(GodotDictionary error);

    private readonly ConcurrentDictionary<string, Task> _activeRuns =
        new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _lifetimeCancellation;
    private GodotMainThreadDispatcher? _dispatcher;
    private GodotEventPump? _eventPump;
    private GodotRuntimeEventPublisher? _runtimeEventPublisher;
    private IGodotRuntimeBackend? _backend;
    private IGodotDurableRuntimeBackend? _durableBackend;
    private GodotCancellationDispatcher.Reservation?
        _lifecycleCancellationReservation;
    private Task<Exception?>? _lifetimeCancellationTask;
    private Task? _stopTask;
    private Task? _stopEventPublishTask;
    private int _acceptingRuns;
    private int _exitStarted;
    private int _exitCleanupCompleted;
    private int _lifecycleReservationReleaseScheduled;
    private int _stopRetryRequired;
    private int _shutdownIncomplete;

    [Export(PropertyHint.Range, "1,4096,1")]
    public int DispatcherCapacity { get; set; } = 256;

    [Export(PropertyHint.Range, "1,1024,1")]
    public int MaxActiveRuns { get; set; } = 64;

    [Export(PropertyHint.Range, "1,1024,1")]
    public int MaxCommandsPerFrame { get; set; } = 64;

    [Export(PropertyHint.Range, "0.1,16,0.1")]
    public double CommandBudgetMilliseconds { get; set; } = 2;

    [Export(PropertyHint.Range, "2,8192,1")]
    public int EventCapacity { get; set; } = 512;

    [Export(PropertyHint.Range, "1,1024,1")]
    public int MaxEventsPerFrame { get; set; } = 128;

    [Export(PropertyHint.Range, "0.1,16,0.1")]
    public double EventBudgetMilliseconds { get; set; } = 2;

    [Export(PropertyHint.Range, "0.1,30,0.1")]
    public double ShutdownTimeoutSeconds { get; set; } = 5;

    public GodotRuntimeHost Typed { get; private set; } = null!;

    public bool IsShutdownIncomplete =>
        Volatile.Read(ref _shutdownIncomplete) != 0;

    public GodotMainThreadDispatcher Dispatcher =>
        _dispatcher
        ?? throw new InvalidOperationException(
            "The Godot runtime node has not entered the scene tree.");

    internal GodotEventPump EventPump =>
        _eventPump
        ?? throw new InvalidOperationException(
            "The Godot runtime node has not entered the scene tree.");

    internal GodotRuntimeEventPublisher RuntimeEventPublisher =>
        _runtimeEventPublisher
        ?? throw new InvalidOperationException(
            "The Godot runtime node has not entered the scene tree.");

    internal bool IsBackendConfigured =>
        Volatile.Read(ref _backend) is not null
        || Volatile.Read(ref _durableBackend) is not null;

    public override void _EnterTree()
    {
        lock (_lifecycleGate)
        {
            if (_lifetimeCancellation is not null
                || _stopTask is not null
                || Volatile.Read(ref _exitStarted) != 0) {
                throw new InvalidOperationException(
                    "A Godot runtime node instance can enter the scene tree only once.");
            }

            ValidateConfiguration();
            if (!GodotCancellationDispatcher.TryReserve(
                    out var lifecycleReservation))
            {
                throw new InvalidOperationException(
                    "The process lifecycle-cancellation capacity is exhausted.");
            }

            try
            {
                _lifecycleCancellationReservation =
                    lifecycleReservation;
                _lifetimeCancellation = new CancellationTokenSource();
                _dispatcher =
                    new GodotMainThreadDispatcher(DispatcherCapacity);
                _eventPump = new GodotEventPump(EventCapacity);
                _runtimeEventPublisher =
                    new GodotRuntimeEventPublisher(_eventPump);
                Typed = new GodotRuntimeHost(this);
                Volatile.Write(ref _acceptingRuns, 1);
            }
            catch
            {
                _lifecycleCancellationReservation = null;
                lifecycleReservation!.Dispose();
                throw;
            }
        }
    }

    public override void _Ready()
    {
        EventPump.TryPublish(new GodotEventMessage
        {
            Kind = GodotEventKinds.RuntimeStarted
        });
    }

    public override void _Process(double delta)
    {
        _ = delta;
        Dispatcher.Drain(
            MaxCommandsPerFrame,
            TimeSpan.FromMilliseconds(CommandBudgetMilliseconds));
        EventPump.Drain(
            MaxEventsPerFrame,
            TimeSpan.FromMilliseconds(EventBudgetMilliseconds),
            PublishOnMainThread);
    }

    public override void _ExitTree()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
        {
            return;
        }

        var timeout = TimeSpan.FromSeconds(ShutdownTimeoutSeconds);
        var retryBudget = timeout - TimeSpan.FromMilliseconds(
            Math.Min(250, timeout.TotalMilliseconds * 0.1));
        var stopTask = EnsureStopTask();
        var elapsed = Stopwatch.StartNew();
        var retryAttempt = 0;
        while (retryAttempt < MaximumLifecycleRetryAttempts
               && elapsed.Elapsed < retryBudget)
        {
            while (!stopTask.IsCompleted && elapsed.Elapsed < retryBudget)
            {
                Dispatcher.Drain(
                    MaxCommandsPerFrame,
                    TimeSpan.FromMilliseconds(CommandBudgetMilliseconds));
                EventPump.Drain(
                    MaxEventsPerFrame,
                    TimeSpan.FromMilliseconds(EventBudgetMilliseconds),
                    PublishOnMainThread);
                Thread.Sleep(1);
            }

            if (!stopTask.IsCompleted)
            {
                break;
            }

            try
            {
                stopTask.GetAwaiter().GetResult();
            }
            catch
            {
            }

            if (Volatile.Read(ref _stopRetryRequired) == 0)
            {
                break;
            }

            stopTask = EnsureStopTask();
            var delayMilliseconds = Math.Min(
                MaximumLifecycleRetryDelayMilliseconds,
                InitialLifecycleRetryDelayMilliseconds
                * (1 << Math.Min(retryAttempt, 4)));
            retryAttempt++;
            if (elapsed.Elapsed < retryBudget)
            {
                Thread.Sleep(
                    Math.Min(
                        delayMilliseconds,
                        Math.Max(
                            0,
                            (int)(retryBudget - elapsed.Elapsed)
                                .TotalMilliseconds)));
            }
        }

        if (!stopTask.IsCompleted
            || Volatile.Read(ref _stopRetryRequired) != 0)
        {
            Volatile.Write(ref _shutdownIncomplete, 1);
            var terminalPublish = EnsureStopEventPublishTask(
                CreateStoppedMessage(graceful: false));
            while (!terminalPublish.IsCompleted
                   && elapsed.Elapsed < timeout)
            {
                EventPump.Drain(
                    MaxEventsPerFrame,
                    TimeSpan.FromMilliseconds(EventBudgetMilliseconds),
                    PublishOnMainThread);
                Thread.Sleep(1);
            }

            if (terminalPublish.IsCompleted)
            {
                try
                {
                    terminalPublish.GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    global::Godot.GD.PushError(
                        "The runtime terminal shutdown event could not be queued: "
                        + exception.Message);
                }
            }
        }

        while (EventPump.PendingCount > 0
               && elapsed.Elapsed < timeout)
        {
            var drained = EventPump.Drain(
                MaxEventsPerFrame,
                TimeSpan.FromMilliseconds(EventBudgetMilliseconds),
                PublishOnMainThread);
            if (drained == 0)
            {
                Thread.Sleep(1);
            }
        }

        if (stopTask.IsCompleted
            && Volatile.Read(ref _stopRetryRequired) == 0)
        {
            CompleteExitCleanup();
        }
        else
        {
            _ = ObserveStopAsync();
        }
    }

    // Variant-compatible surface consumed by GDScript.
    public string start_run(
        GodotDictionary run,
        GodotArray observations,
        GodotArray tools)
    {
        HeadlessRunRequest request;
        try
        {
            request = GodotProtocolVariantMapper.ToRunRequest(
                run,
                observations,
                tools);
        }
        catch (Exception exception)
        {
            PublishFacadeError("invalid_run_request", exception.Message);
            return string.Empty;
        }

        try
        {
            return StartTypedRun(request);
        }
        catch (InvalidOperationException exception)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string start_agent_run(
        GodotDictionary run,
        GodotArray observations)
    {
        DurableRunRequest request;
        try
        {
            request = GodotProtocolVariantMapper.ToDurableRunRequest(
                run,
                observations);
        }
        catch (Exception exception)
        {
            PublishFacadeError("invalid_run_request", exception.Message);
            return string.Empty;
        }

        try
        {
            return StartTypedDurableRun(request);
        }
        catch (InvalidOperationException exception)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string resume_agent_run(string runId)
    {
        try
        {
            return ResumeTypedDurableRun(
                runId,
                continuation: null,
                reconciler: null);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public bool cancel_run(string runId) =>
        PostFacadeControl(
            runId,
            RunControlKinds.Cancel,
            observation: null);

    public bool interrupt_run(string runId) =>
        PostFacadeControl(
            runId,
            RunControlKinds.Interrupt,
            observation: null);

    public bool steer_run(
        string runId,
        GodotDictionary observation) =>
        PostFacadeObservationControl(
            runId,
            RunControlKinds.Steer,
            observation);

    public bool follow_up_run(
        string runId,
        GodotDictionary observation) =>
        PostFacadeObservationControl(
            runId,
            RunControlKinds.FollowUp,
            observation);

    public GodotDictionary get_runtime_status()
    {
        return new GodotDictionary
        {
            ["configured"] = IsBackendConfigured,
            ["backend"] = Volatile.Read(ref _durableBackend) is not null
                ? "durable"
                : Volatile.Read(ref _backend) is not null
                    ? "headless"
                    : "unconfigured",
            ["accepting_runs"] = Volatile.Read(ref _acceptingRuns) != 0,
            ["active_runs"] = _activeRuns.Count,
            ["max_active_runs"] = MaxActiveRuns,
            ["dispatcher_pending"] = Dispatcher.PendingCount,
            ["dispatcher_running"] = Dispatcher.RunningCount,
            ["event_pending"] = EventPump.PendingCount,
            ["event_dropped"] = EventPump.DroppedCount
        };
    }

    public void request_shutdown()
    {
        _ = ObserveStopAsync();
    }

    internal void ConfigureBackend(IGodotRuntimeBackend backend)
    {
        lock (_lifecycleGate)
        {
            if (_stopTask is not null)
            {
                throw new InvalidOperationException(
                    "The Godot runtime host is stopping.");
            }

            if (_backend is not null || _durableBackend is not null)
            {
                throw new InvalidOperationException(
                    "The Godot runtime backend is already configured.");
            }

            _backend = backend;
        }
    }

    internal void ConfigureDurableBackend(IGodotDurableRuntimeBackend backend)
    {
        lock (_lifecycleGate)
        {
            if (_stopTask is not null)
            {
                throw new InvalidOperationException(
                    "The Godot runtime host is stopping.");
            }

            if (_backend is not null || _durableBackend is not null)
            {
                throw new InvalidOperationException(
                    "The Godot runtime backend is already configured.");
            }

            _durableBackend = backend;
        }
    }

    internal string StartTypedRun(HeadlessRunRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        IGodotRuntimeBackend backend;
        CancellationToken cancellationToken;
        string requestId;
        Task runTask;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _acceptingRuns) == 0)
            {
                throw new InvalidOperationException(
                    "The Godot runtime host is not accepting new runs.");
            }

            if (_activeRuns.Count >= MaxActiveRuns)
            {
                throw new InvalidOperationException(
                    "The Godot runtime host reached its active-run limit.");
            }

            backend = Volatile.Read(ref _backend)
                ?? throw new InvalidOperationException(
                    "Configure a runtime backend before starting a run.");
            cancellationToken = _lifetimeCancellation?.Token
                ?? throw new InvalidOperationException(
                    "The Godot runtime node is not active.");
            var requestSnapshot = SnapshotRequest(request);
            requestId = Guid.NewGuid().ToString("N");
            runTask = Task.Run(
                () => ExecuteRunAsync(
                    backend,
                    requestId,
                    requestSnapshot,
                    cancellationToken),
                CancellationToken.None);

            if (!_activeRuns.TryAdd(requestId, runTask))
            {
                throw new InvalidOperationException(
                    "Unable to track the Godot runtime request.");
            }
        }

        AttachRunRemoval(runTask, requestId);
        return requestId;
    }

    internal string StartTypedDurableRun(DurableRunRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        IGodotDurableRuntimeBackend backend;
        CancellationToken cancellationToken;
        string requestId;
        Task runTask;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _durableBackend)
                ?? throw new InvalidOperationException(
                    "Configure a durable runtime backend before starting a durable run.");
            cancellationToken = _lifetimeCancellation?.Token
                ?? throw new InvalidOperationException(
                    "The Godot runtime node is not active.");
            var requestSnapshot = SnapshotDurableRequest(request);
            requestId = Guid.NewGuid().ToString("N");
            runTask = Task.Run(
                () => ExecuteDurableRunAsync(
                    backend,
                    requestId,
                    requestSnapshot,
                    cancellationToken),
                CancellationToken.None);

            if (!_activeRuns.TryAdd(requestId, runTask))
            {
                throw new InvalidOperationException(
                    "Unable to track the Godot runtime request.");
            }
        }

        AttachRunRemoval(runTask, requestId);
        return requestId;
    }

    internal string ResumeTypedDurableRun(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        IGodotDurableRuntimeBackend backend;
        CancellationToken cancellationToken;
        string requestId;
        Task runTask;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _durableBackend)
                ?? throw new InvalidOperationException(
                    "Configure a durable runtime backend before resuming a run.");
            cancellationToken = _lifetimeCancellation?.Token
                ?? throw new InvalidOperationException(
                    "The Godot runtime node is not active.");
            var continuationSnapshot = SnapshotContinuation(continuation);
            requestId = Guid.NewGuid().ToString("N");
            runTask = Task.Run(
                () => ExecuteDurableResumeAsync(
                    backend,
                    requestId,
                    runId,
                    continuationSnapshot,
                    reconciler,
                    cancellationToken),
                CancellationToken.None);

            if (!_activeRuns.TryAdd(requestId, runTask))
            {
                throw new InvalidOperationException(
                    "Unable to track the Godot runtime request.");
            }
        }

        AttachRunRemoval(runTask, requestId);
        return requestId;
    }

    internal bool TryPostTypedControl(
        string runId,
        RunControlCommand command)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        if (Volatile.Read(ref _acceptingRuns) == 0)
        {
            return false;
        }

        var backend = Volatile.Read(ref _durableBackend)
            ?? throw new InvalidOperationException(
                "Configure a durable runtime backend before posting controls.");
        return backend.TryPostControl(runId, command);
    }

    internal ValueTask StopAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return GodotShutdownWait.WaitAsync(
            EnsureStopTask(),
            timeout,
            cancellationToken);
    }

    private Task EnsureStopTask()
    {
        lock (_lifecycleGate)
        {
            if (_stopTask is null
                || (_stopTask.IsCompleted
                    && Volatile.Read(ref _stopRetryRequired) != 0))
            {
                Volatile.Write(ref _stopRetryRequired, 0);
                _stopTask = StopCoreAsync();
            }

            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref _acceptingRuns, 0);
        Dispatcher.StopAccepting();

        Exception? shutdownError = null;
        var shutdownTimeout =
            TimeSpan.FromSeconds(ShutdownTimeoutSeconds);
        var admission = await EnsureLifetimeCancellationDispatchedAsync(
                shutdownTimeout)
            .ConfigureAwait(false);
        if (admission.Completion is null)
        {
            Volatile.Write(ref _stopRetryRequired, 1);
            Volatile.Write(ref _shutdownIncomplete, 1);
            throw admission.Failure
                ?? new InvalidOperationException(
                    "Runtime lifetime cancellation admission failed.");
        }

        var cancellationTask = admission.Completion;
        var ownerWork = new List<Task> { cancellationTask };
        var ownerDrainCompleted = true;

        try
        {
            var active = _activeRuns.Values
                .Append(cancellationTask)
                .ToArray();
            var activeDrain = Task.WhenAll(active);
            ownerWork.Add(activeDrain);
            if (active.Length > 0)
            {
                await GodotShutdownWait
                    .WaitAsync(
                        activeDrain,
                        shutdownTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            if (ownerWork[^1].IsCompleted is false)
            {
                ownerDrainCompleted = false;
            }
            shutdownError = Combine(shutdownError, exception);
        }

        if (cancellationTask.IsCompleted)
        {
            var cancellationFailure = await cancellationTask
                .ConfigureAwait(false);
            if (cancellationFailure is not null)
            {
                shutdownError = Combine(
                    shutdownError,
                    cancellationFailure);
            }
        }

        try
        {
            var dispatcherDrain = Dispatcher
                .WaitForRunningWorkAsync(CancellationToken.None)
                .AsTask();
            ownerWork.Add(dispatcherDrain);
            await GodotShutdownWait
                .WaitAsync(
                    dispatcherDrain,
                    shutdownTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (ownerWork[^1].IsCompleted is false)
            {
                ownerDrainCompleted = false;
            }
            shutdownError = Combine(shutdownError, exception);
        }

        if (ownerDrainCompleted)
        {
            try
            {
                var durableBackend = Volatile.Read(ref _durableBackend);
                if (durableBackend is not null)
                {
                    var backendStop = durableBackend
                        .StopAsync(CancellationToken.None)
                        .AsTask();
                    ownerWork.Add(backendStop);
                    await GodotShutdownWait
                        .WaitAsync(
                            backendStop,
                            shutdownTimeout,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else
                {
                    var backend = Volatile.Read(ref _backend);
                    if (backend is not null)
                    {
                        var backendStop = backend
                            .StopAsync(CancellationToken.None)
                            .AsTask();
                        ownerWork.Add(backendStop);
                        await GodotShutdownWait
                            .WaitAsync(
                                backendStop,
                                shutdownTimeout,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _stopRetryRequired, 1);
                if (ownerWork[^1].IsCompleted is false)
                {
                    ownerDrainCompleted = false;
                }
                shutdownError = Combine(shutdownError, exception);
            }
        }
        else
        {
            Volatile.Write(ref _stopRetryRequired, 1);
        }

        if (Volatile.Read(ref _stopRetryRequired) == 0
            || Volatile.Read(ref _exitStarted) == 0)
        {
            try
            {
                var criticalPublish = EnsureStopEventPublishTask(
                    CreateStoppedMessage(shutdownError is null));
                ownerWork.Add(criticalPublish);
                await GodotShutdownWait
                    .WaitAsync(
                        criticalPublish,
                        shutdownTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _stopRetryRequired, 1);
                shutdownError = Combine(shutdownError, exception);
            }
        }

        Dispatcher.StopAccepting();
        if (ownerDrainCompleted
            && Volatile.Read(ref _stopRetryRequired) == 0)
        {
            EventPump.StopAccepting();
        }

        Volatile.Write(
            ref _shutdownIncomplete,
            shutdownError is not null
                || Volatile.Read(ref _stopRetryRequired) != 0
                    ? 1
                    : 0);
        ScheduleLifecycleReservationRelease(Task.WhenAll(ownerWork));

        if (shutdownError is not null)
        {
            ExceptionDispatchInfo.Capture(shutdownError).Throw();
        }
    }

    private async Task<CancellationAdmissionResult>
        EnsureLifetimeCancellationDispatchedAsync(TimeSpan timeout)
    {
        Exception? failure = null;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (TryDispatchLifetimeCancellation(out var cancellation))
            {
                return new CancellationAdmissionResult(
                    cancellation,
                    null);
            }

            if (cancellation.IsCompletedSuccessfully)
            {
                failure = cancellation.Result;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return new CancellationAdmissionResult(
                    null,
                    failure
                    ?? new InvalidOperationException(
                        "Runtime lifetime cancellation admission failed."));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10))
                .ConfigureAwait(false);
        }
    }

    private bool TryDispatchLifetimeCancellation(
        out Task<Exception?> cancellation)
    {
        lock (_lifecycleGate)
        {
            if (_lifetimeCancellationTask is not null)
            {
                cancellation = _lifetimeCancellationTask;
                return true;
            }

            var source = _lifetimeCancellation;
            if (source is null)
            {
                cancellation = Task.FromResult<Exception?>(null);
                _lifetimeCancellationTask = cancellation;
                return true;
            }

            var reservation = _lifecycleCancellationReservation;
            if (reservation is null)
            {
                cancellation = Task.FromResult<Exception?>(
                    new InvalidOperationException(
                        "The lifecycle cancellation reservation is unavailable."));
                return false;
            }

            try
            {
                if (!GodotCancellationDispatcher.TryDispatchReserved(
                        reservation,
                        source.Cancel,
                        out cancellation))
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                cancellation = Task.FromResult<Exception?>(exception);
                return false;
            }

            _lifetimeCancellationTask = cancellation;
            return true;
        }
    }

    private void ScheduleLifecycleReservationRelease(Task ownerDrain)
    {
        if (Interlocked.Exchange(
                ref _lifecycleReservationReleaseScheduled,
                1) != 0)
        {
            return;
        }

        _ = ownerDrain.ContinueWith(
            static (_, state) =>
                ((GameAgentRuntimeNode)state!)
                .ReleaseLifecycleCancellationReservation(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseLifecycleCancellationReservation()
    {
        GodotCancellationDispatcher.Reservation? reservation;
        lock (_lifecycleGate)
        {
            reservation = _lifecycleCancellationReservation;
            _lifecycleCancellationReservation = null;
        }

        reservation?.Dispose();
    }

    private void DisposeLifetimeCancellation()
    {
        CancellationTokenSource? source;
        Task<Exception?>? cancellation;
        lock (_lifecycleGate)
        {
            cancellation = _lifetimeCancellationTask;
            if (cancellation is null)
            {
                return;
            }

            source = _lifetimeCancellation;
            _lifetimeCancellation = null;
        }

        if (source is null)
        {
            return;
        }

        if (cancellation.IsCompleted)
        {
            source.Dispose();
            return;
        }

        _ = cancellation.ContinueWith(
            static (_, state) =>
                ((CancellationTokenSource)state!).Dispose(),
            source,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class CancellationAdmissionResult
    {
        internal CancellationAdmissionResult(
            Task<Exception?>? completion,
            Exception? failure)
        {
            Completion = completion;
            Failure = failure;
        }

        internal Task<Exception?>? Completion { get; }

        internal Exception? Failure { get; }
    }

    private async Task ExecuteRunAsync(
        IGodotRuntimeBackend backend,
        string requestId,
        HeadlessRunRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await backend
                .RunAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await EventPump
                .PublishCriticalAsync(
                    new GodotEventMessage
                    {
                        Kind = GodotEventKinds.RunCompleted,
                        RequestId = requestId,
                        Json = ProtocolJson.Serialize(outcome.Run),
                        SecondaryJson = outcome.FinalOutput?.GetRawText()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "run_cancelled",
                    "cancelled",
                    "The runtime request was cancelled.")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "runtime_backend_failed",
                    "runtime",
                    "The runtime backend failed.")
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteDurableRunAsync(
        IGodotDurableRuntimeBackend backend,
        string requestId,
        DurableRunRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await backend
                .RunAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await PublishDurableOutcomeAsync(requestId, outcome)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "run_cancelled",
                    "cancelled",
                    "The durable runtime request was cancelled.")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "runtime_backend_failed",
                    "runtime",
                    "The durable runtime backend failed.")
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteDurableResumeAsync(
        IGodotDurableRuntimeBackend backend,
        string requestId,
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await backend
                .ResumeAsync(
                    runId,
                    continuation,
                    reconciler,
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishDurableOutcomeAsync(requestId, outcome)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "resume_cancelled",
                    "cancelled",
                    "The durable resume request was cancelled.")
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "run_not_found",
                    "persistence",
                    "The requested run was not found in the durable journal.")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "runtime_resume_failed",
                    "runtime",
                    "The durable runtime could not resume the run.")
                .ConfigureAwait(false);
        }
    }

    private ValueTask PublishDurableOutcomeAsync(
        string requestId,
        DurableRunOutcome outcome)
    {
        return EventPump.PublishCriticalAsync(
            new GodotEventMessage
            {
                Kind = GodotEventKinds.RunCompleted,
                RequestId = requestId,
                Json = ProtocolJson.Serialize(outcome.Run),
                SecondaryJson = outcome.FinalOutput?.GetRawText(),
                Code = outcome.ErrorCode,
                Category = outcome.ErrorCategory,
                Message = outcome.SafeErrorMessage,
                ReconciliationRequired = outcome.ReconciliationRequired
            },
            CancellationToken.None);
    }

    private bool PostFacadeObservationControl(
        string runId,
        string kind,
        GodotDictionary observation)
    {
        ObservationEnvelope mapped;
        try
        {
            mapped = GodotProtocolVariantMapper.ToObservation(observation);
        }
        catch (Exception exception)
        {
            PublishFacadeError(
                "invalid_control_observation",
                exception.Message);
            return false;
        }

        return PostFacadeControl(runId, kind, mapped);
    }

    private bool PostFacadeControl(
        string runId,
        string kind,
        ObservationEnvelope? observation)
    {
        try
        {
            var delivered = TryPostTypedControl(
                runId,
                new RunControlCommand
                {
                    CommandId = Guid.NewGuid().ToString("N"),
                    Kind = kind,
                    Observation = observation,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            if (!delivered)
            {
                PublishFacadeError(
                    "run_not_active",
                    "The run is not active or no longer accepts controls.");
            }

            return delivered;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException)
        {
            PublishFacadeError("control_rejected", exception.Message);
            return false;
        }
    }

    private async ValueTask PublishRunFailureAsync(
        string requestId,
        string code,
        string category,
        string message)
    {
        try
        {
            await EventPump
                .PublishCriticalAsync(
                    new GodotEventMessage
                    {
                        Kind = GodotEventKinds.RunFailed,
                        RequestId = requestId,
                        Code = code,
                        Category = category,
                        Message = message
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown may close the bounded pump before a stale backend returns.
        }
    }

    private void EnsureCanStartRun()
    {
        if (Volatile.Read(ref _acceptingRuns) == 0)
        {
            throw new InvalidOperationException(
                "The Godot runtime host is not accepting new runs.");
        }

        if (_activeRuns.Count >= MaxActiveRuns)
        {
            throw new InvalidOperationException(
                "The Godot runtime host reached its active-run limit.");
        }
    }

    private void AttachRunRemoval(Task runTask, string requestId)
    {
        _ = runTask.ContinueWith(
            static (completedTask, state) =>
            {
                _ = completedTask;
                var removal = (RunRemoval)state!;
                removal.ActiveRuns.TryRemove(removal.RequestId, out _);
            },
            new RunRemoval(_activeRuns, requestId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishOnMainThread(GodotEventMessage message)
    {
        switch (message.Kind)
        {
            case GodotEventKinds.RuntimeStarted:
                EmitSignal(SignalName.RuntimeStarted, get_runtime_status());
                break;
            case GodotEventKinds.RuntimeEvent:
                EmitSignal(
                    SignalName.RuntimeEventPublished,
                    GodotProtocolVariantMapper.ParseDictionary(message.Json!));
                break;
            case GodotEventKinds.RunCompleted:
                EmitSignal(
                    SignalName.RunCompleted,
                    new GodotDictionary
                    {
                        ["request_id"] = message.RequestId ?? string.Empty,
                        ["run"] = GodotProtocolVariantMapper.ParseDictionary(message.Json!),
                        ["final_output"] =
                            GodotProtocolVariantMapper.ParseVariant(message.SecondaryJson),
                        ["error_code"] = message.Code ?? string.Empty,
                        ["error_category"] = message.Category ?? string.Empty,
                        ["safe_error_message"] = message.Message ?? string.Empty,
                        ["reconciliation_required"] =
                            message.ReconciliationRequired
                    });
                break;
            case GodotEventKinds.RunFailed:
                EmitSignal(SignalName.RunFailed, ToErrorDictionary(message));
                break;
            case GodotEventKinds.RuntimeStopped:
                EmitSignal(
                    SignalName.RuntimeStopped,
                    new GodotDictionary
                    {
                        ["status"] = message.Code ?? "shutdown_incomplete",
                        ["message"] = message.Message ?? string.Empty,
                        ["active_runs"] = message.Count
                    });
                break;
            case GodotEventKinds.RuntimeError:
            case GodotEventKinds.PumpOverflow:
                EmitSignal(SignalName.RuntimeError, ToErrorDictionary(message));
                break;
        }
    }

    private void PublishFacadeError(string code, string message)
    {
        EventPump.TryPublish(new GodotEventMessage
        {
            Kind = GodotEventKinds.RuntimeError,
            Code = code,
            Category = "input",
            Message = message
        });
    }

    private async Task ObserveStopAsync()
    {
        for (var attempt = 0;
             attempt < MaximumLifecycleRetryAttempts;
             attempt++)
        {
            try
            {
                await StopAsync(
                        TimeSpan.FromSeconds(ShutdownTimeoutSeconds),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // StopCoreAsync normalizes shutdown failures into a signal.
            }

            if (Volatile.Read(ref _stopRetryRequired) == 0)
            {
                if (Volatile.Read(ref _exitStarted) != 0)
                {
                    CompleteExitCleanup();
                }

                return;
            }

            var delayMilliseconds = Math.Min(
                MaximumLifecycleRetryDelayMilliseconds,
                InitialLifecycleRetryDelayMilliseconds
                * (1 << Math.Min(attempt, 4)));
            await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds))
                .ConfigureAwait(false);
        }

        Volatile.Write(ref _shutdownIncomplete, 1);
        try
        {
            await GodotShutdownWait
                .WaitAsync(
                    EnsureStopEventPublishTask(
                        CreateStoppedMessage(graceful: false)),
                    TimeSpan.FromSeconds(ShutdownTimeoutSeconds),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            global::Godot.GD.PushError(
                "The runtime terminal shutdown event could not be queued: "
                + exception.Message);
        }
        global::Godot.GD.PushError(
            "The agent runtime exhausted bounded lifecycle shutdown retries.");
        if (Volatile.Read(ref _exitStarted) != 0)
        {
            CompleteExitCleanup();
        }
    }

    private Task EnsureStopEventPublishTask(GodotEventMessage stopped)
    {
        lock (_lifecycleGate)
        {
            if (_stopEventPublishTask is null
                || (_stopEventPublishTask.IsCompleted
                    && !_stopEventPublishTask.IsCompletedSuccessfully))
            {
                _stopEventPublishTask = EventPump
                    .PublishCriticalAsync(
                        stopped,
                        CancellationToken.None)
                    .AsTask();
            }

            return _stopEventPublishTask;
        }
    }

    private GodotEventMessage CreateStoppedMessage(bool graceful)
    {
        return new GodotEventMessage
        {
            Kind = GodotEventKinds.RuntimeStopped,
            Code = graceful ? "graceful" : "shutdown_incomplete",
            Category = graceful ? "lifecycle" : "shutdown",
            Message = graceful
                ? "The Godot runtime stopped gracefully."
                : "The Godot runtime stopped before all work could be flushed.",
            Count = _activeRuns.Count
        };
    }

    private void CompleteExitCleanup()
    {
        if (Interlocked.Exchange(ref _exitCleanupCompleted, 1) != 0)
        {
            return;
        }

        EventPump.StopAccepting();
        Dispatcher.StopAccepting();
        DisposeLifetimeCancellation();
    }

    private static GodotDictionary ToErrorDictionary(GodotEventMessage message)
    {
        return new GodotDictionary
        {
            ["request_id"] = message.RequestId ?? string.Empty,
            ["code"] = message.Code ?? "runtime_error",
            ["category"] = message.Category ?? "runtime",
            ["message"] = message.Message ?? "The runtime operation failed.",
            ["count"] = message.Count
        };
    }

    private void ValidateConfiguration()
    {
        if (DispatcherCapacity < 1
            || MaxActiveRuns < 1
            || MaxCommandsPerFrame < 1
            || CommandBudgetMilliseconds <= 0
            || EventCapacity < 2
            || MaxEventsPerFrame < 1
            || EventBudgetMilliseconds <= 0
            || ShutdownTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                "The Game Agent Runtime Autoload has invalid queue or time budgets.");
        }
    }

    private static HeadlessRunRequest SnapshotRequest(HeadlessRunRequest request)
    {
        return new HeadlessRunRequest
        {
            Run = ProtocolJson.DeserializeAgentRun(
                ProtocolJson.Serialize(request.Run)),
            Observations = request.Observations
                .Select(item => ProtocolJson.DeserializeObservationEnvelope(
                    ProtocolJson.Serialize(item)))
                .ToArray(),
            Tools = request.Tools
                .Select(item => ProtocolJson.DeserializeToolDescriptor(
                    ProtocolJson.Serialize(item)))
                .ToArray()
        };
    }

    private static DurableRunRequest SnapshotDurableRequest(
        DurableRunRequest request)
    {
        return new DurableRunRequest
        {
            Run = ProtocolJson.DeserializeAgentRun(
                ProtocolJson.Serialize(request.Run)),
            Context = request.Context.Select(CloneContextCandidate).ToArray(),
            ActiveSkills = request.ActiveSkills
                .Select(item => new SkillReference(item.SkillId, item.Version))
                .ToArray(),
            InitialTranscript = request.InitialTranscript
                .Select(item => NormalizedMessageJournalCodec.Decode(
                    NormalizedMessageJournalCodec.Encode(item)))
                .ToArray(),
            LaneId = request.LaneId
        };
    }

    private static DurableRunContinuation? SnapshotContinuation(
        DurableRunContinuation? continuation)
    {
        if (continuation is null)
        {
            return null;
        }

        return new DurableRunContinuation
        {
            Context = continuation.Context
                .Select(CloneContextCandidate)
                .ToArray(),
            ActiveSkills = continuation.ActiveSkills
                .Select(item => new SkillReference(item.SkillId, item.Version))
                .ToArray(),
            ReplaceActiveSkills = continuation.ReplaceActiveSkills,
            LaneId = continuation.LaneId
        };
    }

    private static ContextCandidate CloneContextCandidate(
        ContextCandidate candidate)
    {
        if (candidate.Content.HasValue)
        {
            return new ContextCandidate(
                candidate.Id,
                candidate.Category,
                candidate.Content.Value,
                candidate.Priority,
                candidate.Required,
                candidate.CanDefer,
                candidate.EstimatedTokens,
                candidate.ExpiresAt,
                candidate.Provenance);
        }

        var resource = candidate.Resource
            ?? throw new InvalidOperationException(
                "A context candidate must contain content or a resource reference.");
        return new ContextCandidate(
            candidate.Id,
            candidate.Category,
            new ContextResourceReference(
                resource.Uri,
                resource.MediaType,
                resource.Digest,
                resource.SizeBytes),
            candidate.Priority,
            candidate.Required,
            candidate.CanDefer,
            candidate.EstimatedTokens,
            candidate.ExpiresAt,
            candidate.Provenance);
    }

    private static Exception Combine(Exception? first, Exception next) =>
        first is null ? next : new AggregateException(first, next);

    private sealed class RunRemoval
    {
        public RunRemoval(
            ConcurrentDictionary<string, Task> activeRuns,
            string requestId)
        {
            ActiveRuns = activeRuns;
            RequestId = requestId;
        }

        public ConcurrentDictionary<string, Task> ActiveRuns { get; }

        public string RequestId { get; }
    }
}

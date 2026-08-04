using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;
using GameAgent.Generation;
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

internal static class GodotRequestCancellationDispatcher
{
    internal const int Capacity = 8;
    internal const int PendingCapacity = 4088;
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
                        "The request cancellation reservation is not available."));
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
                        "The request cancellation reservation invariant was violated."));
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

                if (Interlocked.CompareExchange(ref _state, 2, state) == state)
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
    public delegate void RoutedRunCompletedEventHandler(
        GodotDictionary outcome);

    [Signal]
    public delegate void CompletionCompletedEventHandler(
        GodotDictionary outcome);

    [Signal]
    public delegate void RunFailedEventHandler(GodotDictionary error);

    [Signal]
    public delegate void BatchCompletedEventHandler(GodotDictionary outcome);

    [Signal]
    public delegate void BatchParticipantCompletedEventHandler(
        GodotDictionary result);

    [Signal]
    public delegate void BatchFailedEventHandler(GodotDictionary error);

    [Signal]
    public delegate void GenerationUpdatedEventHandler(GodotDictionary result);

    [Signal]
    public delegate void GenerationFailedEventHandler(GodotDictionary error);

    [Signal]
    public delegate void BatchStartedEventHandler(GodotDictionary manifest);

    [Signal]
    public delegate void ActorFinishedEventHandler(GodotDictionary result);

    [Signal]
    public delegate void BatchAbortedEventHandler(GodotDictionary error);

    [Signal]
    public delegate void RuntimeStoppedEventHandler(GodotDictionary summary);

    [Signal]
    public delegate void RuntimeErrorEventHandler(GodotDictionary error);

    private readonly ConcurrentDictionary<string, Task> _activeRuns =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RequestCancellationState>
        _activeRequestCancellations = new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _lifetimeCancellation;
    private GodotMainThreadDispatcher? _dispatcher;
    private GodotEventPump? _eventPump;
    private RuntimeMetricsEmitter? _eventMetrics;
    private IRuntimeMetricsSink? _metricsSink;
    private RuntimeMetricsOptions? _metricsOptions;
    private GodotRuntimeEventPublisher? _runtimeEventPublisher;
    private IGodotRuntimeBackend? _backend;
    private IGodotDurableRuntimeBackend? _durableBackend;
    private IGodotRoutedExecutionBackend? _routedBackend;
    private IGodotChildAgentBackend? _childBackend;
    private GenerationRuntime? _generationRuntime;
    private MultiActorDecisionCoordinator? _multiActorCoordinator;
    private SemaphoreSlim? _actorBatchSlots;
    private GodotCancellationDispatcher.Reservation?
        _lifecycleCancellationReservation;
    private Task<Exception?>? _lifetimeCancellationTask;
    private Task? _stopTask;
    private Task? _stopEventPublishTask;
    private int _acceptingRuns;
    private int _exitStarted;
    private int _exitCleanupCompleted;
    private int _exitCleanupScheduled;
    private int _lifecycleReservationReleaseScheduled;
    private int _stopRetryRequired;
    private int _shutdownIncomplete;
    private int _guardedParticipantResumeSupported;

    [Export(PropertyHint.Range, "1,4096,1")]
    public int DispatcherCapacity { get; set; } = 256;

    [Export(PropertyHint.Range, "1,1024,1")]
    public int MaxActiveRuns { get; set; } = 64;

    [Export(PropertyHint.Range, "1,1024,1")]
    public int MaxActorBatchSize { get; set; } = 256;

    [Export(PropertyHint.Range, "1,1024,1")]
    public int MaxConcurrentActorRuns { get; set; } = 32;

    [Export(PropertyHint.Range, "1,32,1")]
    public int MaxConcurrentActorBatches { get; set; } = 4;

    [Export(PropertyHint.Range, "1,1024,1")]
    public int MaxConcurrentParticipantOperations { get; set; } = 32;

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

    public RuntimeMetricsHealth? MetricsHealth => _eventMetrics?.Health;

    public void ConfigureMetrics(
        IRuntimeMetricsSink sink,
        RuntimeMetricsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lifecycleGate)
        {
            if (_lifetimeCancellation is not null
                || _stopTask is not null
                || Volatile.Read(ref _exitStarted) != 0)
            {
                throw new InvalidOperationException(
                    "Metrics must be configured before the node enters the scene tree.");
            }

            _metricsSink = sink;
            _metricsOptions = options;
        }
    }

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
                || Volatile.Read(ref _exitStarted) != 0)
            {
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
                _eventMetrics = new RuntimeMetricsEmitter(
                    _metricsSink,
                    _metricsOptions);
                _eventPump = new GodotEventPump(
                    EventCapacity,
                    _eventMetrics);
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
            var publicationBudget = TimeSpan.FromMilliseconds(
                Math.Min(250, timeout.TotalMilliseconds));
            var publicationElapsed = Stopwatch.StartNew();
            while (!terminalPublish.IsCompleted
                   && publicationElapsed.Elapsed < publicationBudget)
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

        var finalDrainBudget = TimeSpan.FromMilliseconds(
            Math.Min(250, timeout.TotalMilliseconds));
        var finalDrainElapsed = Stopwatch.StartNew();
        while (EventPump.PendingCount > 0
               && finalDrainElapsed.Elapsed < finalDrainBudget)
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
        try
        {
            return StartMappedRun(
                () => GodotProtocolVariantMapper.ToRunRequest(
                    run,
                    observations,
                    tools));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_run_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
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
        try
        {
            return StartMappedDurableRun(
                () => GodotProtocolVariantMapper.ToDurableRunRequest(
                    run,
                    observations));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_run_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string start_agent_run_with_options(
        GodotDictionary run,
        GodotArray observations,
        GodotDictionary options)
    {
        try
        {
            return StartMappedDurableRun(
                () => GodotProtocolVariantMapper.ToDurableRunRequest(
                    run,
                    observations,
                    options));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_run_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string start_routed_run(
        GodotDictionary route,
        GodotDictionary run,
        GodotArray observations,
        GodotDictionary runOptions,
        GodotDictionary workflow)
    {
        try
        {
            return StartMappedRoutedRun(
                () => GodotProtocolVariantMapper.ToRoutedExecutionRequest(
                    route,
                    run,
                    observations,
                    runOptions,
                    workflow));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_routed_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string start_completion(GodotDictionary options)
    {
        try
        {
            return StartMappedCompletion(
                () => GodotProtocolVariantMapper
                    .ToSimpleCompletionRequest(options));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_completion_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string start_generation(GodotDictionary request)
    {
        try
        {
            return StartMappedGeneration(
                () => GodotGenerationVariantMapper.ToGenerationRequest(request));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_generation_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or JsonException)
        {
            PublishFacadeError("invalid_generation_request", exception.Message);
            return string.Empty;
        }
    }

    public string refresh_generation(string operationId)
    {
        try
        {
            return StartGenerationOperation(
                (runtime, token) => runtime.RefreshAsync(operationId, token));
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            PublishFacadeError("generation_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string wait_generation(
        string operationId,
        double timeoutSeconds = 300)
    {
        try
        {
            if (double.IsNaN(timeoutSeconds)
                || double.IsInfinity(timeoutSeconds)
                || timeoutSeconds <= 0
                || timeoutSeconds > 86_400)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            }

            return StartGenerationOperation(
                (runtime, token) => runtime.WaitForCompletionAsync(
                    operationId,
                    TimeSpan.FromSeconds(timeoutSeconds),
                    cancellationToken: token));
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            PublishFacadeError("generation_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string cancel_generation(string operationId)
    {
        try
        {
            return StartGenerationOperation(
                (runtime, token) => runtime.RequestCancellationAsync(
                    operationId,
                    token));
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            PublishFacadeError("generation_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string start_child_agent_run(
        string parentRunId,
        GodotDictionary run,
        GodotArray observations,
        GodotDictionary options)
    {
        try
        {
            return StartMappedChildRun(
                parentRunId,
                () => GodotProtocolVariantMapper.ToDurableRunRequest(
                    run,
                    observations,
                    options));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_run_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException)
        {
            PublishFacadeError(
                "child_run_unavailable",
                exception.Message);
            return string.Empty;
        }
    }

    public string start_child_agent_run_with_parent(
        GodotDictionary parentRun,
        GodotDictionary run,
        GodotArray observations,
        GodotDictionary options)
    {
        try
        {
            return StartMappedChildRun(
                () => GodotProtocolVariantMapper.ToAgentRun(parentRun),
                () => GodotProtocolVariantMapper.ToDurableRunRequest(
                    run,
                    observations,
                    options));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_run_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException)
        {
            PublishFacadeError(
                "child_run_unavailable",
                exception.Message);
            return string.Empty;
        }
    }

    public int cancel_child_agent_runs(string parentRunId)
    {
        try
        {
            return CancelTypedChildren(parentRunId);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException)
        {
            PublishFacadeError(
                "child_cancel_unavailable",
                exception.Message);
            return 0;
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

    public string resume_agent_run_with_options(
        string runId,
        GodotDictionary options)
    {
        try
        {
            return ResumeMappedDurableRun(
                runId,
                () => GodotProtocolVariantMapper
                    .ToDurableResumeOptions(options));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_resume_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
        }
        catch (DurableRunResumeGuardException exception)
        {
            PublishFacadeError(exception.ReasonCode, exception.Message);
            return string.Empty;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string start_agent_batch(GodotDictionary batch)
    {
        try
        {
            return StartMappedActorBatch(
                () => GodotMultiActorVariantMapper.ToDecisionBatch(
                    batch,
                    MaxActorBatchSize));
        }
        catch (FacadeRequestMappingException exception)
        {
            PublishFacadeError(
                "invalid_batch_request",
                exception.InnerException?.Message ?? exception.Message);
            return string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string resume_agent_batch_participant(
        string batchId,
        GodotDictionary participant,
        GodotDictionary options)
    {
        MultiActorBatchParticipant mappedParticipant;
        GodotParticipantResumeOptions resumeOptions;
        try
        {
            mappedParticipant =
                GodotMultiActorVariantMapper.ToParticipant(participant);
            resumeOptions =
                GodotProtocolVariantMapper.ToParticipantResumeOptions(options);
        }
        catch (Exception exception)
        {
            PublishFacadeError(
                "invalid_batch_participant_request",
                exception.Message);
            return string.Empty;
        }

        try
        {
            return ResumeTypedActorBatchParticipant(
                batchId,
                mappedParticipant,
                resumeOptions.Continuation,
                resumeOptions.Reconciler,
                resumeOptions.SemanticExpectation);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException)
        {
            PublishFacadeError("runtime_unavailable", exception.Message);
            return string.Empty;
        }
    }

    public string abandon_agent_batch_participant(
        string batchId,
        GodotDictionary participant,
        string reasonCode)
    {
        MultiActorBatchParticipant mappedParticipant;
        try
        {
            mappedParticipant =
                GodotMultiActorVariantMapper.ToParticipant(participant);
            reasonCode =
                GodotMultiActorVariantMapper.ValidateReasonCode(reasonCode);
        }
        catch (Exception exception)
        {
            PublishFacadeError(
                "invalid_batch_participant_request",
                exception.Message);
            return string.Empty;
        }

        try
        {
            return AbandonTypedActorBatchParticipant(
                batchId,
                mappedParticipant,
                reasonCode,
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

    public bool cancel_request(string requestId)
    {
        try
        {
            return CancelTypedRequest(requestId);
        }
        catch (ArgumentException exception)
        {
            PublishFacadeError("invalid_request_id", exception.Message);
            return false;
        }
    }

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
            ["multi_actor_configured"] =
                Volatile.Read(ref _multiActorCoordinator) is not null,
            ["child_agents_configured"] =
                Volatile.Read(ref _childBackend) is not null,
            ["generation_configured"] =
                Volatile.Read(ref _generationRuntime) is not null,
            ["guarded_participant_resume"] =
                Volatile.Read(ref _guardedParticipantResumeSupported) != 0,
            ["max_actor_batch_size"] = MaxActorBatchSize,
            ["max_concurrent_actor_runs"] = MaxConcurrentActorRuns,
            ["max_concurrent_actor_batches"] =
                MaxConcurrentActorBatches,
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

    internal void ConfigureGenerationRuntime(GenerationRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_lifecycleGate)
        {
            if (_stopTask is not null || Volatile.Read(ref _exitStarted) != 0)
            {
                throw new InvalidOperationException(
                    "The Godot runtime host is stopping.");
            }

            if (_generationRuntime is not null)
            {
                throw new InvalidOperationException(
                    "The generation runtime is already configured.");
            }

            _generationRuntime = runtime;
        }
    }

    internal void ConfigureDurableBackend(IGodotDurableRuntimeBackend backend)
    {
        ConfigureDurableBackend(backend, multiActorRuntime: null);
    }

    internal void ConfigureDurableBackend(
        IGodotDurableRuntimeBackend backend,
        IDurableAgentRuntime? multiActorRuntime)
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
            _routedBackend = backend as IGodotRoutedExecutionBackend;
            _childBackend = backend as IGodotChildAgentBackend;
            if (multiActorRuntime is not null)
            {
                ConfigureMultiActorRuntimeCore(multiActorRuntime);
            }
        }
    }

    internal void ConfigureMultiActorRuntime(
        IDurableAgentRuntime runtime)
    {
        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        lock (_lifecycleGate)
        {
            if (_stopTask is not null)
            {
                throw new InvalidOperationException(
                    "The Godot runtime host is stopping.");
            }

            if (_durableBackend is null)
            {
                throw new InvalidOperationException(
                    "Configure a durable backend before multi-actor coordination.");
            }

            ConfigureMultiActorRuntimeCore(runtime);
        }
    }

    private void ConfigureMultiActorRuntimeCore(
        IDurableAgentRuntime runtime)
    {
        if (_multiActorCoordinator is not null)
        {
            throw new InvalidOperationException(
                "The multi-actor runtime is already configured.");
        }

        var lifecycleConcurrency = checked(
            MaxConcurrentActorRuns * MaxConcurrentActorBatches
            + MaxConcurrentParticipantOperations);
        _multiActorCoordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxBatchSize: MaxActorBatchSize,
                maxConcurrentRuns: MaxConcurrentActorRuns,
                maxDetachedLifecycleNotifications: lifecycleConcurrency,
                maxConcurrentParticipantResumes:
                    MaxConcurrentParticipantOperations),
            new GodotMultiActorLifecycle(this));
        _actorBatchSlots = new SemaphoreSlim(
            MaxConcurrentActorBatches,
            MaxConcurrentActorBatches);
        Volatile.Write(
            ref _guardedParticipantResumeSupported,
            runtime is IGuardedDurableAgentRuntime ? 1 : 0);
    }

    internal string StartTypedGeneration(GenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = GenerationRequestSnapshotter.Snapshot(request);
        return StartGenerationOperation(
            (runtime, token) => runtime.SubmitAsync(snapshot, token));
    }

    private string StartMappedGeneration(Func<GenerationRequest> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        GenerationRequest snapshot;
        try
        {
            snapshot = GenerationRequestSnapshotter.Snapshot(
                requestFactory()
                ?? throw new InvalidDataException(
                    "The generation request mapper returned null."));
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException
                and not StackOverflowException)
        {
            throw new FacadeRequestMappingException(exception);
        }

        return StartGenerationOperation(
            (runtime, token) => runtime.SubmitAsync(snapshot, token));
    }

    private string StartGenerationOperation(
        Func<GenerationRuntime, CancellationToken, ValueTask<GenerationJob>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        GenerationRuntime runtime;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            runtime = Volatile.Read(ref _generationRuntime)
                      ?? throw new InvalidOperationException(
                          "Configure generation before starting generation work.");
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: true,
                operationLabel: "generation request");
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteGenerationAsync(
                runtime,
                admission.RequestId,
                operation,
                admission.Token));
        return admission.RequestId;
    }

    internal string StartTypedRun(HeadlessRunRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        IGodotRuntimeBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _backend)
                ?? throw new InvalidOperationException(
                    "Configure a runtime backend before starting a run.");
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "runtime request");
        }

        HeadlessRunRequest snapshot;
        try
        {
            snapshot = DurableRunRequestSnapshotter.SnapshotForBackendBoundary(
                request,
                admission.Token);
        }
        catch
        {
            RollBackAdmission(admission);
            throw;
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteRunAsync(
                backend,
                admission.RequestId,
                snapshot,
                admission.Token));
        return admission.RequestId;
    }

    private string StartMappedRun(Func<HeadlessRunRequest> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        IGodotRuntimeBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _backend)
                ?? throw new InvalidOperationException(
                    "Configure a runtime backend before starting a run.");
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "runtime request");
        }

        HeadlessRunRequest snapshot;
        try
        {
            var request = requestFactory()
                ?? throw new InvalidDataException(
                    "The runtime request mapper returned null.");
            snapshot = DurableRunRequestSnapshotter
                .SnapshotForBackendBoundary(request, admission.Token);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            RollBackAdmission(admission);
            throw new FacadeRequestMappingException(exception);
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteRunAsync(
                backend,
                admission.RequestId,
                snapshot,
                admission.Token));
        return admission.RequestId;
    }

    internal string StartTypedDurableRun(DurableRunRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        IGodotDurableRuntimeBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _durableBackend)
                ?? throw new InvalidOperationException(
                    "Configure a durable runtime backend before starting a durable run.");
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "durable request");
        }

        DurableRunRequest snapshot;
        try
        {
            snapshot = DurableRunRequestSnapshotter.SnapshotForBackendBoundary(
                request,
                admission.Token);
        }
        catch
        {
            RollBackAdmission(admission);
            throw;
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteDurableRunAsync(
                backend,
                admission.RequestId,
                snapshot,
                admission.Token));
        return admission.RequestId;
    }

    private string StartMappedDurableRun(
        Func<DurableRunRequest> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        IGodotDurableRuntimeBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _durableBackend)
                ?? throw new InvalidOperationException(
                    "Configure a durable runtime backend before starting a durable run.");
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "durable request");
        }

        DurableRunRequest snapshot;
        try
        {
            var request = requestFactory()
                ?? throw new InvalidDataException(
                    "The durable request mapper returned null.");
            snapshot = DurableRunRequestSnapshotter
                .SnapshotForBackendBoundary(request, admission.Token);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            RollBackAdmission(admission);
            throw new FacadeRequestMappingException(exception);
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteDurableRunAsync(
                backend,
                admission.RequestId,
                snapshot,
                admission.Token));
        return admission.RequestId;
    }

    internal string StartTypedRoutedRun(
        RoutedExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        IGodotRoutedExecutionBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _routedBackend)
                ?? throw new InvalidOperationException(
                    "Configure a built runtime before starting routed execution.");
            admission = ReserveActiveRunLocked(
                cancellationToken,
                supportsRequestCancellation: true,
                operationLabel: "routed request");
        }

        RoutedExecutionRequest snapshot;
        try
        {
            snapshot = DurableRunRequestSnapshotter.SnapshotForBackendBoundary(
                request,
                admission.Token);
        }
        catch
        {
            RollBackAdmission(admission);
            throw;
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteRoutedRunAsync(
                backend,
                admission.RequestId,
                snapshot,
                admission.Token));
        return admission.RequestId;
    }

    private string StartMappedRoutedRun(
        Func<RoutedExecutionRequest> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        IGodotRoutedExecutionBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _routedBackend)
                ?? throw new InvalidOperationException(
                    "Configure a built runtime before starting routed execution.");
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: true,
                operationLabel: "routed request");
        }

        RoutedExecutionRequest snapshot;
        try
        {
            var request = requestFactory()
                ?? throw new InvalidDataException(
                    "The routed request mapper returned null.");
            snapshot = DurableRunRequestSnapshotter
                .SnapshotForBackendBoundary(request, admission.Token);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            RollBackAdmission(admission);
            throw new FacadeRequestMappingException(exception);
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteRoutedRunAsync(
                backend,
                admission.RequestId,
                snapshot,
                admission.Token));
        return admission.RequestId;
    }

    internal string StartTypedCompletion(
        SimpleCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        IGodotRoutedExecutionBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _routedBackend)
                ?? throw new InvalidOperationException(
                    "Configure a built runtime before starting a completion.");
            admission = ReserveActiveRunLocked(
                cancellationToken,
                supportsRequestCancellation: true,
                operationLabel: "completion request");
        }

        SimpleCompletionRequest snapshot;
        try
        {
            snapshot = DurableRunRequestSnapshotter.SnapshotForBackendBoundary(
                request,
                admission.Token);
        }
        catch
        {
            RollBackAdmission(admission);
            throw;
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteCompletionAsync(
                backend,
                admission.RequestId,
                snapshot,
                admission.Token));
        return admission.RequestId;
    }

    private string StartMappedCompletion(
        Func<SimpleCompletionRequest> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        IGodotRoutedExecutionBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _routedBackend)
                ?? throw new InvalidOperationException(
                    "Configure a built runtime before starting a completion.");
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: true,
                operationLabel: "completion request");
        }

        SimpleCompletionRequest snapshot;
        try
        {
            var request = requestFactory()
                ?? throw new InvalidDataException(
                    "The completion request mapper returned null.");
            snapshot = DurableRunRequestSnapshotter
                .SnapshotForBackendBoundary(request, admission.Token);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            RollBackAdmission(admission);
            throw new FacadeRequestMappingException(exception);
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteCompletionAsync(
                backend,
                admission.RequestId,
                snapshot,
                admission.Token));
        return admission.RequestId;
    }

    internal string StartTypedChildRun(
        string parentRunId,
        DurableRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(parentRunId))
        {
            throw new ArgumentException(
                "A parent run id is required.",
                nameof(parentRunId));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return StartTypedChildRunCore(parentRunId, parentRun: null, request);
    }

    internal string StartTypedChildRun(
        AgentRun parentRun,
        DurableRunRequest request)
    {
        if (parentRun is null)
        {
            throw new ArgumentNullException(nameof(parentRun));
        }

        var parentRunId = RequiredId(
            parentRun.RunId,
            nameof(parentRun));
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return StartTypedChildRunCore(
            parentRunId,
            parentRun,
            request);
    }

    private string StartTypedChildRunCore(
        string parentRunId,
        AgentRun? parentRun,
        DurableRunRequest request)
    {

        IGodotChildAgentBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _childBackend)
                ?? throw new InvalidOperationException(
                    "Configure a built runtime before starting a child run.");
            if (parentRun is not null
                && backend is not IGodotPersistentChildAgentBackend)
            {
                throw new InvalidOperationException(
                    "This child-agent backend does not support persisted parent runs.");
            }
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "child-agent request");
        }

        AgentRun? parentSnapshot;
        DurableRunRequest requestSnapshot;
        try
        {
            parentSnapshot = parentRun is null
                ? null
                : DurableRunRequestSnapshotter.SnapshotForBackendBoundary(
                    parentRun,
                    admission.Token);
            requestSnapshot = DurableRunRequestSnapshotter.SnapshotForBackendBoundary(
                request,
                admission.Token);
        }
        catch
        {
            RollBackAdmission(admission);
            throw;
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteChildRunAsync(
                backend,
                admission.RequestId,
                parentRunId,
                parentSnapshot,
                requestSnapshot,
                admission.Token));
        return admission.RequestId;
    }

    private string StartMappedChildRun(
        string parentRunId,
        Func<DurableRunRequest> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        IGodotChildAgentBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _childBackend)
                ?? throw new InvalidOperationException(
                    "Configure a built runtime before starting a child run.");
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "child-agent request");
        }

        DurableRunRequest requestSnapshot;
        try
        {
            parentRunId = RequiredId(parentRunId, nameof(parentRunId));
            var request = requestFactory()
                ?? throw new InvalidDataException(
                    "The child request mapper returned null.");
            requestSnapshot = DurableRunRequestSnapshotter
                .SnapshotForBackendBoundary(request, admission.Token);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            RollBackAdmission(admission);
            throw new FacadeRequestMappingException(exception);
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteChildRunAsync(
                backend,
                admission.RequestId,
                parentRunId,
                null,
                requestSnapshot,
                admission.Token));
        return admission.RequestId;
    }

    private string StartMappedChildRun(
        Func<AgentRun> parentFactory,
        Func<DurableRunRequest> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(parentFactory);
        ArgumentNullException.ThrowIfNull(requestFactory);
        IGodotChildAgentBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _childBackend)
                ?? throw new InvalidOperationException(
                    "Configure a built runtime before starting a child run.");
            if (backend is not IGodotPersistentChildAgentBackend)
            {
                throw new InvalidOperationException(
                    "This child-agent backend does not support persisted parent runs.");
            }
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "child-agent request");
        }

        string parentRunId;
        AgentRun parentSnapshot;
        DurableRunRequest requestSnapshot;
        try
        {
            var parent = parentFactory()
                ?? throw new InvalidDataException(
                    "The parent-run mapper returned null.");
            parentRunId = RequiredId(parent.RunId, nameof(parent.RunId));
            var request = requestFactory()
                ?? throw new InvalidDataException(
                    "The child request mapper returned null.");
            parentSnapshot = DurableRunRequestSnapshotter
                .SnapshotForBackendBoundary(parent, admission.Token);
            requestSnapshot = DurableRunRequestSnapshotter
                .SnapshotForBackendBoundary(request, admission.Token);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            RollBackAdmission(admission);
            throw new FacadeRequestMappingException(exception);
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteChildRunAsync(
                backend,
                admission.RequestId,
                parentRunId,
                parentSnapshot,
                requestSnapshot,
                admission.Token));
        return admission.RequestId;
    }

    internal int CancelTypedChildren(string parentRunId)
    {
        var backend = Volatile.Read(ref _childBackend)
            ?? throw new InvalidOperationException(
                "Configure a built runtime before cancelling child runs.");
        return backend.CancelChildren(parentRunId);
    }

    internal bool CancelTypedRequest(string requestId)
    {
        requestId = RequiredId(requestId, nameof(requestId));
        if (!_activeRequestCancellations.TryGetValue(
                requestId,
                out var cancellation))
        {
            return false;
        }

        return cancellation.TryDispatch();
    }

    internal string ResumeTypedDurableRun(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        DurableRunResumeGuard? guard = null)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        IGodotDurableRuntimeBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _durableBackend)
                ?? throw new InvalidOperationException(
                    "Configure a durable runtime backend before resuming a run.");
            if (guard is not null
                && (backend
                        is not IGodotGuardedDurableRuntimeBackend guardedBackend
                    || !guardedBackend.SupportsGuardedResume))
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.NotSupported);
            }

            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "durable resume request");
        }

        DurableRunContinuation? continuationSnapshot;
        DurableRunResumeGuard? guardSnapshot;
        try
        {
            continuationSnapshot = DurableRunRequestSnapshotter.Snapshot(
                continuation,
                admission.Token);
            guardSnapshot = DurableRunRequestSnapshotter.Snapshot(
                guard,
                admission.Token);
        }
        catch
        {
            RollBackAdmission(admission);
            throw;
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteDurableResumeAsync(
                backend,
                admission.RequestId,
                runId,
                continuationSnapshot,
                reconciler,
                guardSnapshot,
                admission.Token));
        return admission.RequestId;
    }

    private string ResumeMappedDurableRun(
        string runId,
        Func<GodotDurableResumeOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        IGodotDurableRuntimeBackend backend;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            backend = Volatile.Read(ref _durableBackend)
                ?? throw new InvalidOperationException(
                    "Configure a durable runtime backend before resuming a run.");
            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "durable resume request");
        }

        DurableRunContinuation? continuationSnapshot;
        IGameOperationReconciler? reconciler;
        DurableRunResumeGuard? guardSnapshot;
        try
        {
            runId = RequiredId(runId, nameof(runId));
            var options = optionsFactory()
                ?? throw new InvalidDataException(
                    "The durable resume mapper returned null.");
            if (options.Guard is not null
                && (backend
                        is not IGodotGuardedDurableRuntimeBackend guardedBackend
                    || !guardedBackend.SupportsGuardedResume))
            {
                throw new DurableRunResumeGuardException(
                    DurableRunResumeGuardReasonCodes.NotSupported);
            }

            continuationSnapshot = DurableRunRequestSnapshotter.Snapshot(
                options.Continuation,
                admission.Token);
            reconciler = options.Reconciler;
            guardSnapshot = DurableRunRequestSnapshotter.Snapshot(
                options.Guard,
                admission.Token);
        }
        catch (DurableRunResumeGuardException)
        {
            RollBackAdmission(admission);
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            RollBackAdmission(admission);
            throw new FacadeRequestMappingException(exception);
        }

        StartAdmittedOperation(
            admission,
            () => ExecuteDurableResumeAsync(
                backend,
                admission.RequestId,
                runId,
                continuationSnapshot,
                reconciler,
                guardSnapshot,
                admission.Token));
        return admission.RequestId;
    }

    internal string StartTypedActorBatch(MultiActorDecisionBatch batch)
    {
        if (batch is null)
        {
            throw new ArgumentNullException(nameof(batch));
        }

        return StartTrackedMultiActorOperation(
            requireGuardedResume: false,
            cancellationToken => DurableRunRequestSnapshotter.Snapshot(
                batch,
                MaxActorBatchSize,
                cancellationToken),
            (coordinator, requestId, snapshot, cancellationToken) =>
                ExecuteActorBatchAsync(
                    coordinator,
                    requestId,
                    snapshot,
                    cancellationToken));
    }

    private string StartMappedActorBatch(
        Func<MultiActorDecisionBatch> batchFactory)
    {
        ArgumentNullException.ThrowIfNull(batchFactory);
        return StartTrackedMultiActorOperation(
            requireGuardedResume: false,
            cancellationToken =>
            {
                try
                {
                    var batch = batchFactory()
                        ?? throw new InvalidDataException(
                            "The actor-batch mapper returned null.");
                    return DurableRunRequestSnapshotter.Snapshot(
                        batch,
                        MaxActorBatchSize,
                        cancellationToken);
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException
                          and not StackOverflowException)
                {
                    throw new FacadeRequestMappingException(exception);
                }
            },
            (coordinator, requestId, snapshot, cancellationToken) =>
                ExecuteActorBatchAsync(
                    coordinator,
                    requestId,
                    snapshot,
                    cancellationToken));
    }

    internal string ResumeTypedActorBatchParticipant(
        string batchId,
        MultiActorBatchParticipant participant,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        DurableRunSemanticExpectation? semanticExpectation = null)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            throw new ArgumentException(
                "Batch id is required.",
                nameof(batchId));
        }

        if (participant is null)
        {
            throw new ArgumentNullException(nameof(participant));
        }

        return StartTrackedMultiActorOperation(
            requireGuardedResume: true,
            cancellationToken => new ParticipantResumeSnapshot(
                new MultiActorBatchParticipant(
                    participant.InputIndex,
                    participant.AgentId,
                    participant.RunId,
                    participant.DecisionKey),
                DurableRunRequestSnapshotter.Snapshot(
                    continuation,
                    cancellationToken)),
            (coordinator, requestId, snapshot, cancellationToken) =>
                ExecuteActorParticipantResumeAsync(
                    coordinator,
                    requestId,
                    batchId,
                    snapshot.Participant,
                    snapshot.Continuation,
                    reconciler,
                    semanticExpectation,
                    cancellationToken));
    }

    internal string AbandonTypedActorBatchParticipant(
        string batchId,
        MultiActorBatchParticipant participant,
        string reasonCode,
        IGameOperationReconciler? reconciler)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            throw new ArgumentException(
                "Batch id is required.",
                nameof(batchId));
        }

        if (participant is null)
        {
            throw new ArgumentNullException(nameof(participant));
        }

        reasonCode =
            GodotMultiActorVariantMapper.ValidateReasonCode(reasonCode);
        return StartTrackedMultiActorOperation(
            requireGuardedResume: true,
            _ => new MultiActorBatchParticipant(
                participant.InputIndex,
                participant.AgentId,
                participant.RunId,
                participant.DecisionKey),
            (coordinator, requestId, snapshot, cancellationToken) =>
                ExecuteActorParticipantAbandonAsync(
                    coordinator,
                    requestId,
                    batchId,
                    snapshot,
                    reasonCode,
                    reconciler,
                    cancellationToken));
    }

    private string StartTrackedMultiActorOperation<TSnapshot>(
        bool requireGuardedResume,
        Func<CancellationToken, TSnapshot> snapshot,
        Func<
            MultiActorDecisionCoordinator,
            string,
            TSnapshot,
            CancellationToken,
            Task> operation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(operation);
        MultiActorDecisionCoordinator coordinator;
        RunAdmission admission;
        lock (_lifecycleGate)
        {
            EnsureCanStartRun();
            coordinator = Volatile.Read(ref _multiActorCoordinator)
                ?? throw new InvalidOperationException(
                    "Configure multi-actor coordination before starting a batch operation.");
            if (requireGuardedResume
                && Volatile.Read(
                    ref _guardedParticipantResumeSupported) == 0)
            {
                throw new InvalidOperationException(
                    "The configured durable runtime does not support guarded participant resume.");
            }

            admission = ReserveActiveRunLocked(
                callerCancellation: default,
                supportsRequestCancellation: false,
                operationLabel: "batch request");
        }

        TSnapshot prepared;
        try
        {
            prepared = snapshot(admission.Token);
        }
        catch
        {
            RollBackAdmission(admission);
            throw;
        }

        StartAdmittedOperation(
            admission,
            () => operation(
                coordinator,
                admission.RequestId,
                prepared,
                admission.Token));
        return admission.RequestId;
    }

    internal bool TryPostTypedControl(
        string runId,
        RunControlCommand command)
    {
        return TryPostTypedControl(runId, command, out _);
    }

    internal bool TryPostTypedControl(
        string runId,
        RunControlCommand command,
        out string? rejectionReason)
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
            rejectionReason = null;
            return false;
        }

        var backend = Volatile.Read(ref _durableBackend)
            ?? throw new InvalidOperationException(
                "Configure a durable runtime backend before posting controls.");
        if (backend is IGodotControlRejectionBackend detailedBackend)
        {
            return detailedBackend.TryPostControl(
                runId,
                command,
                out rejectionReason);
        }

        rejectionReason = null;
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
        Task[] admittedAtShutdown;
        lock (_lifecycleGate)
        {
            // Admission and the shutdown owner snapshot form one transaction.
            // A starter that won the gate is included; one that lost observes
            // accepting=false and cannot appear after this snapshot.
            Volatile.Write(ref _acceptingRuns, 0);
            admittedAtShutdown = _activeRuns.Values.ToArray();
        }
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
            var active = admittedAtShutdown
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
            if (_eventMetrics is not null)
            {
                _ = await _eventMetrics.StopAsync().ConfigureAwait(false);
            }
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

    private async Task ExecuteGenerationAsync(
        GenerationRuntime runtime,
        string requestId,
        Func<GenerationRuntime, CancellationToken, ValueTask<GenerationJob>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await operation(runtime, cancellationToken)
                .ConfigureAwait(false);
            await EventPump.PublishCriticalAsync(
                    new GodotEventMessage
                    {
                        Kind = GodotEventKinds.GenerationUpdated,
                        RequestId = requestId,
                        Json = GenerationJson.SerializeJob(job),
                        ReconciliationRequired =
                            job.Status is GenerationJobStatuses.Unknown
                                or GenerationJobStatuses.Materializing
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException
                and not StackOverflowException)
        {
            var generation = exception as GenerationOperationException;
            await EventPump.PublishCriticalAsync(
                    new GodotEventMessage
                    {
                        Kind = GodotEventKinds.GenerationFailed,
                        RequestId = requestId,
                        Code = generation?.ReasonCode ?? "generation_failed",
                        Category = "generation",
                        Message = exception is OperationCanceledException
                            ? "Generation observation was cancelled locally."
                            : exception.Message,
                        ReconciliationRequired = generation?.OutcomeUncertain ?? false,
                        Phase = "generation"
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
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
                    "The runtime request was cancelled.",
                    reconciliationRequired: true,
                    phase: "headless_execution")
                .ConfigureAwait(false);
        }
        catch (ObservationAdmissionException exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    exception.ReasonCode,
                    "validation",
                    "An observation was rejected by the active run boundary.")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "runtime_backend_failed",
                    "runtime",
                    "The runtime backend failed.",
                    reconciliationRequired: true,
                    phase: "headless_execution")
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
                    "The durable runtime request was cancelled.",
                    reconciliationRequired: true,
                    phase: "durable_execution")
                .ConfigureAwait(false);
        }
        catch (ObservationAdmissionException exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    exception.ReasonCode,
                    "validation",
                    "An observation was rejected by the active run boundary.")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "runtime_backend_failed",
                    "runtime",
                    "The durable runtime backend failed.",
                    reconciliationRequired: true,
                    phase: "durable_execution")
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteRoutedRunAsync(
        IGodotRoutedExecutionBackend backend,
        string requestId,
        RoutedExecutionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await backend
                .RunRoutedAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await PublishRoutedOutcomeAsync(requestId, outcome)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "routed_run_cancelled",
                    "cancelled",
                    "The routed runtime request was cancelled.",
                    reconciliationRequired: true,
                    phase: "routed_execution")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "routed_runtime_failed",
                    "runtime",
                    "The routed runtime request failed.",
                    reconciliationRequired: true,
                    phase: "routed_execution")
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteCompletionAsync(
        IGodotRoutedExecutionBackend backend,
        string requestId,
        SimpleCompletionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await backend
                .CompleteAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await PublishCompletionOutcomeAsync(requestId, outcome)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "completion_cancelled",
                    "cancelled",
                    "The stateless completion was cancelled.")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "completion_failed",
                    "runtime",
                    "The stateless completion failed.")
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteChildRunAsync(
        IGodotChildAgentBackend backend,
        string requestId,
        string parentRunId,
        AgentRun? parentRun,
        DurableRunRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = parentRun is null
                ? await backend
                    .RunChildAsync(
                        parentRunId,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await ((IGodotPersistentChildAgentBackend)backend)
                    .RunChildAsync(
                        parentRun,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            await PublishDurableOutcomeAsync(requestId, result.Outcome)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "child_run_cancelled",
                    "cancelled",
                    "The child-agent run was cancelled.",
                    reconciliationRequired: true,
                    phase: "child_execution")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    "child_run_failed",
                    "runtime",
                    "The child-agent run failed.",
                    reconciliationRequired: true,
                    phase: "child_execution")
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteDurableResumeAsync(
        IGodotDurableRuntimeBackend backend,
        string requestId,
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        DurableRunResumeGuard? guard,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = guard is null
                ? await backend
                    .ResumeAsync(
                        runId,
                        continuation,
                        reconciler,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await ((IGodotGuardedDurableRuntimeBackend)backend)
                    .ResumeAsync(
                        runId,
                        continuation,
                        reconciler,
                        cancellationToken,
                        guard)
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
                    "The durable resume request was cancelled.",
                    reconciliationRequired: true,
                    phase: "durable_resume")
                .ConfigureAwait(false);
        }
        catch (ObservationAdmissionException exception)
        {
            await PublishRunFailureAsync(
                    requestId,
                    exception.ReasonCode,
                    "validation",
                    "An observation was rejected by the active run boundary.")
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
                    "The durable runtime could not resume the run.",
                    reconciliationRequired: true,
                    phase: "durable_resume")
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteActorBatchAsync(
        MultiActorDecisionCoordinator coordinator,
        string requestId,
        MultiActorDecisionBatch batch,
        CancellationToken cancellationToken)
    {
        var batchSlots = Volatile.Read(ref _actorBatchSlots)
            ?? throw new InvalidOperationException(
                "The multi-actor batch admission is not configured.");
        var slotHeld = false;
        try
        {
            await batchSlots
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            slotHeld = true;
            var outcome = await coordinator
                .RunAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            await EventPump
                .PublishCriticalAsync(
                    new GodotEventMessage
                    {
                        Kind = GodotEventKinds.BatchCompleted,
                        RequestId = requestId,
                        Json =
                            GodotMultiActorVariantMapper
                                .SerializeBatchOutcome(outcome)
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "batch_cancelled",
                    "cancelled",
                    "The multi-actor batch was cancelled.",
                    reconciliationRequired: slotHeld,
                    phase: slotHeld ? "batch_execution" : "batch_admission",
                    batchId: batch.BatchId,
                    affectedRunIds: slotHeld
                        ? batch.Runs.Select(item => item.Run.RunId).ToArray()
                        : Array.Empty<string>())
                .ConfigureAwait(false);
        }
        catch (MultiActorBatchAbortUncertainException exception)
        {
            await PublishBatchUncertaintyAsync(
                    requestId,
                    new MultiActorUncertainty(
                        "batch_lifecycle_uncertain",
                        exception.ReasonCode,
                        exception.BatchId,
                        Array.Empty<string>()))
                .ConfigureAwait(false);
        }
        catch (MultiActorBatchExecutionUncertainException exception)
        {
            await PublishBatchUncertaintyAsync(
                    requestId,
                    new MultiActorUncertainty(
                        "batch_execution_uncertain",
                        "participant_execution",
                        exception.BatchId,
                        exception.RunIds))
                .ConfigureAwait(false);
        }
        catch (AggregateException exception)
            when (TryDescribeUncertainty(exception, out _))
        {
            _ = TryDescribeUncertainty(
                exception,
                out var uncertainty);
            await PublishBatchUncertaintyAsync(
                    requestId,
                    uncertainty!)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "batch_execution_failed",
                    "runtime",
                    "The multi-actor batch could not be completed.",
                    reconciliationRequired: slotHeld,
                    phase: slotHeld ? "batch_execution" : "batch_admission",
                    batchId: batch.BatchId,
                    affectedRunIds: slotHeld
                        ? batch.Runs.Select(item => item.Run.RunId).ToArray()
                        : Array.Empty<string>())
                .ConfigureAwait(false);
        }
        finally
        {
            if (slotHeld)
            {
                batchSlots.Release();
            }
        }
    }

    private async Task ExecuteActorParticipantResumeAsync(
        MultiActorDecisionCoordinator coordinator,
        string requestId,
        string batchId,
        MultiActorBatchParticipant participant,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        DurableRunSemanticExpectation? semanticExpectation,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = semanticExpectation is null
                ? await coordinator
                    .ResumeParticipantAsync(
                        batchId,
                        participant,
                        continuation,
                        reconciler,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await coordinator
                    .ResumeParticipantAsync(
                        batchId,
                        participant,
                        semanticExpectation,
                        continuation,
                        reconciler,
                        cancellationToken)
                    .ConfigureAwait(false);
            await PublishParticipantOutcomeAsync(
                    requestId,
                    "resume",
                    result)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "participant_resume_cancelled",
                    "cancelled",
                    "The participant resume was cancelled.",
                    reconciliationRequired: true,
                    phase: "participant_resume",
                    batchId: batchId,
                    participant: participant)
                .ConfigureAwait(false);
        }
        catch (MultiActorBatchAbortUncertainException exception)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "participant_lifecycle_uncertain",
                    "reconciliation",
                    "The participant lifecycle outcome is uncertain.",
                    reconciliationRequired: true,
                    phase: exception.ReasonCode,
                    batchId: exception.BatchId,
                    participant: participant)
                .ConfigureAwait(false);
        }
        catch (DurableRunResumeGuardException)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "participant_guard_failed",
                    "identity",
                    "The participant manifest does not match durable identity.")
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "run_not_found",
                    "persistence",
                    "The participant run was not found.")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "participant_resume_failed",
                    "runtime",
                    "The participant could not be resumed.",
                    reconciliationRequired: true,
                    phase: "participant_resume",
                    batchId: batchId,
                    participant: participant)
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteActorParticipantAbandonAsync(
        MultiActorDecisionCoordinator coordinator,
        string requestId,
        string batchId,
        MultiActorBatchParticipant participant,
        string reasonCode,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await coordinator
                .ReconcileAbandonedParticipantAsync(
                    batchId,
                    participant,
                    reasonCode,
                    reconciler,
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishParticipantOutcomeAsync(
                    requestId,
                    "abandon",
                    result)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "participant_abandon_cancelled",
                    "cancelled",
                    "The participant abandonment was cancelled.",
                    reconciliationRequired: true,
                    phase: "participant_abandon",
                    batchId: batchId,
                    participant: participant)
                .ConfigureAwait(false);
        }
        catch (MultiActorBatchAbortUncertainException exception)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "participant_lifecycle_uncertain",
                    "reconciliation",
                    "The participant lifecycle outcome is uncertain.",
                    reconciliationRequired: true,
                    phase: exception.ReasonCode,
                    batchId: exception.BatchId,
                    participant: participant)
                .ConfigureAwait(false);
        }
        catch (DurableRunResumeGuardException)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "participant_guard_failed",
                    "identity",
                    "The participant manifest does not match durable identity.")
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "run_not_found",
                    "persistence",
                    "The participant run was not found.")
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishBatchFailureAsync(
                    requestId,
                    "participant_abandon_failed",
                    "runtime",
                    "The participant could not be durably abandoned.",
                    reconciliationRequired: true,
                    phase: "participant_abandon",
                    batchId: batchId,
                    participant: participant)
                .ConfigureAwait(false);
        }
    }

    private ValueTask PublishParticipantOutcomeAsync(
        string requestId,
        string operation,
        MultiActorRunResult result)
    {
        return EventPump.PublishCriticalAsync(
            new GodotEventMessage
            {
                Kind = GodotEventKinds.BatchParticipantCompleted,
                RequestId = requestId,
                Code = operation,
                Json = GodotMultiActorVariantMapper.SerializeResult(result)
            },
            CancellationToken.None);
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

    private ValueTask PublishRoutedOutcomeAsync(
        string requestId,
        RoutedExecutionOutcome outcome)
    {
        var run = outcome.Run is null
            ? JsonArrayBuilder.Null()
            : JsonArrayBuilder.Object(
                ("run", ProtocolJson.ToElement(outcome.Run.Run)),
                ("finalOutput", outcome.Run.FinalOutput
                    ?? JsonArrayBuilder.Null()),
                ("errorCode", OptionalJson(outcome.Run.ErrorCode)),
                ("errorCategory", OptionalJson(outcome.Run.ErrorCategory)),
                ("safeErrorMessage",
                    OptionalJson(outcome.Run.SafeErrorMessage)),
                ("reconciliationRequired",
                    JsonArrayBuilder.Boolean(
                        outcome.Run.ReconciliationRequired)));
        var workflow = outcome.Workflow is null
            ? JsonArrayBuilder.Null()
            : JsonArrayBuilder.Object(
                ("runId", JsonArrayBuilder.String(outcome.Workflow.RunId)),
                ("workflowId",
                    JsonArrayBuilder.String(outcome.Workflow.WorkflowId)),
                ("status", JsonArrayBuilder.String(outcome.Workflow.Status)),
                ("reasonCode", OptionalJson(outcome.Workflow.ReasonCode)),
                ("output", outcome.Workflow.Output
                    ?? JsonArrayBuilder.Null()));
        var payload = JsonArrayBuilder.Object(
            ("path", JsonArrayBuilder.String(
                outcome.Decision.Path.ToString().ToLowerInvariant())),
            ("reasonCode",
                JsonArrayBuilder.String(outcome.Decision.ReasonCode)),
            ("policyId",
                JsonArrayBuilder.String(outcome.Decision.PolicyId)),
            ("policyVersion",
                JsonArrayBuilder.String(outcome.Decision.PolicyVersion)),
            ("run", run),
            ("workflow", workflow));
        return EventPump.PublishCriticalAsync(
            new GodotEventMessage
            {
                Kind = GodotEventKinds.RoutedRunCompleted,
                RequestId = requestId,
                Json = payload.GetRawText()
            },
            CancellationToken.None);
    }

    private ValueTask PublishCompletionOutcomeAsync(
        string requestId,
        SimpleCompletionOutcome outcome)
    {
        var route = outcome.RouteIdentity is null
            ? JsonArrayBuilder.Null()
            : JsonArrayBuilder.Object(
                ("providerId", JsonArrayBuilder.String(
                    outcome.RouteIdentity.ProviderId)),
                ("modelId", JsonArrayBuilder.String(
                    outcome.RouteIdentity.ModelId)),
                ("transportDialect", JsonArrayBuilder.String(
                    outcome.RouteIdentity.TransportDialect)),
                ("hasBoundDialectSemantics", JsonArrayBuilder.Boolean(
                    outcome.RouteIdentity.HasBoundDialectSemantics)),
                ("dialectSemanticDigest", JsonArrayBuilder.String(
                    outcome.RouteIdentity.DialectSemanticDigest)),
                ("capabilityDigest", JsonArrayBuilder.String(
                    outcome.RouteIdentity.CapabilityDigest)),
                ("routePolicyVersion", JsonArrayBuilder.String(
                    outcome.RouteIdentity.RoutePolicyVersion)),
                ("routePolicyDigest", JsonArrayBuilder.String(
                    outcome.RouteIdentity.RoutePolicyDigest)),
                ("routeDigest", JsonArrayBuilder.String(
                    outcome.RouteIdentity.RouteDigest)));
        var usage = JsonArrayBuilder.Object(
            ("inputTokens",
                JsonArrayBuilder.Number(outcome.Usage.InputTokens)),
            ("outputTokens",
                JsonArrayBuilder.Number(outcome.Usage.OutputTokens)),
            ("costUsd", JsonArrayBuilder.String(outcome.Usage.CostUsd)),
            ("cacheReadTokens", OptionalNumber(
                outcome.Usage.CacheReadTokens)),
            ("cacheWriteTokens", OptionalNumber(
                outcome.Usage.CacheWriteTokens)),
            ("cacheMissTokens", OptionalNumber(
                outcome.Usage.CacheMissTokens)),
            ("reasoningTokens", OptionalNumber(
                outcome.Usage.ReasoningTokens)),
            ("providerTotalTokens", OptionalNumber(
                outcome.Usage.ProviderTotalTokens)),
            ("samples", JsonArrayBuilder.Number(
                outcome.Usage.Samples)),
            ("availability", JsonArrayBuilder.String(
                outcome.Usage.Availability)));
        var payload = JsonArrayBuilder.Object(
            ("operationId",
                JsonArrayBuilder.String(outcome.OperationId)),
            ("providerId", JsonArrayBuilder.String(outcome.ProviderId)),
            ("routeIdentity", route),
            ("text", OptionalJson(outcome.Text)),
            ("reasoningContent", OptionalJson(outcome.ReasoningContent)),
            ("finishReason", OptionalJson(outcome.FinishReason)),
            ("usage", usage));
        return EventPump.PublishCriticalAsync(
            new GodotEventMessage
            {
                Kind = GodotEventKinds.CompletionCompleted,
                RequestId = requestId,
                Json = payload.GetRawText()
            },
            CancellationToken.None);
    }

    private static JsonElement OptionalJson(string? value) =>
        value is null
            ? JsonArrayBuilder.Null()
            : JsonArrayBuilder.String(value);

    private static JsonElement OptionalNumber(int? value) =>
        value.HasValue
            ? JsonArrayBuilder.Number(value.Value)
            : JsonArrayBuilder.Null();

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
                },
                out var rejectionReason);
            if (!delivered)
            {
                PublishFacadeError(
                    rejectionReason ?? "run_not_active",
                    rejectionReason is null
                        ? "The run is not active or no longer accepts controls."
                        : "The control observation was rejected by the active run boundary.");
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
        string message,
        bool reconciliationRequired = false,
        string? phase = null)
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
                        Message = message,
                        ReconciliationRequired = reconciliationRequired,
                        Phase = phase
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown may close the bounded pump before a stale backend returns.
        }
    }

    private async ValueTask PublishBatchFailureAsync(
        string requestId,
        string code,
        string category,
        string message,
        bool reconciliationRequired = false,
        string? phase = null,
        string? batchId = null,
        MultiActorBatchParticipant? participant = null,
        IReadOnlyList<string>? affectedRunIds = null)
    {
        try
        {
            await EventPump
                .PublishCriticalAsync(
                    new GodotEventMessage
                    {
                        Kind = GodotEventKinds.BatchFailed,
                        RequestId = requestId,
                        Code = code,
                        Category = category,
                        Message = message,
                        ReconciliationRequired = reconciliationRequired,
                        Phase = phase,
                        BatchId = batchId,
                        ParticipantRunId = participant?.RunId,
                        ParticipantAgentId = participant?.AgentId,
                        ParticipantDecisionKey = participant?.DecisionKey,
                        ParticipantInputIndex =
                            participant?.InputIndex ?? -1,
                        AffectedRunIds =
                            affectedRunIds?.ToArray()
                            ?? Array.Empty<string>()
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown may close the bounded pump before a stale batch returns.
        }
    }

    private ValueTask PublishBatchUncertaintyAsync(
        string requestId,
        MultiActorUncertainty uncertainty)
    {
        return PublishBatchFailureAsync(
            requestId,
            uncertainty.Code,
            "reconciliation",
            "The multi-actor lifecycle outcome is uncertain.",
            reconciliationRequired: true,
            phase: uncertainty.Phase,
            batchId: uncertainty.BatchId,
            affectedRunIds: uncertainty.RunIds);
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

    private RunAdmission ReserveActiveRunLocked(
        CancellationToken callerCancellation,
        bool supportsRequestCancellation,
        string operationLabel)
    {
        var lifetimeToken = _lifetimeCancellation?.Token
            ?? throw new InvalidOperationException(
                "The Godot runtime node is not active.");
        RequestCancellationState? requestCancellation = null;
        if (supportsRequestCancellation)
        {
            callerCancellation.ThrowIfCancellationRequested();
        }

        if (!GodotRequestCancellationDispatcher.TryReserve(
                out var reservation))
        {
            throw new InvalidOperationException(
                "The bounded operation-cancellation capacity is exhausted.");
        }

        try
        {
            // Every operation gets its own cancellation source. The lifetime
            // token only queues these short dispatches, so a blocking callback
            // owned by one backend cannot prevent another NPC/run from seeing
            // shutdown cancellation.
            requestCancellation = new RequestCancellationState(
                new CancellationTokenSource(),
                reservation!,
                lifetimeToken,
                supportsRequestCancellation
                    ? callerCancellation
                    : CancellationToken.None);
        }
        catch
        {
            reservation!.Dispose();
            throw;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (supportsRequestCancellation
                && !_activeRequestCancellations.TryAdd(
                    requestId,
                    requestCancellation!))
            {
                throw new InvalidOperationException(
                    $"Unable to track {operationLabel} cancellation.");
            }

            if (!_activeRuns.TryAdd(requestId, completion.Task))
            {
                throw new InvalidOperationException(
                    $"Unable to track the Godot {operationLabel}.");
            }
        }
        catch
        {
            _activeRequestCancellations.TryRemove(requestId, out _);
            requestCancellation?.Dispose();
            throw;
        }

        var admission = new RunAdmission(
            requestId,
            completion,
            requestCancellation.Token,
            requestCancellation);
        AttachRunRemoval(completion.Task, requestId);
        return admission;
    }

    private void StartAdmittedOperation(
        RunAdmission admission,
        Func<Task> operation)
    {
        Task running;
        try
        {
            running = Task.Run(operation, CancellationToken.None);
        }
        catch
        {
            RollBackAdmission(admission);
            throw;
        }

        _ = CompleteAdmittedOperationAsync(admission, running);
    }

    private async Task CompleteAdmittedOperationAsync(
        RunAdmission admission,
        Task running)
    {
        Exception? failure = null;
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (admission.RequestCancellation is not null)
        {
            var cancellationFailure = await admission.RequestCancellation
                .CloseAndGetOwner()
                .ConfigureAwait(false);
            if (cancellationFailure is not null)
            {
                failure = failure is null
                    ? cancellationFailure
                    : new AggregateException(failure, cancellationFailure);
            }

            admission.RequestCancellation.Dispose();
        }

        if (failure is null)
        {
            admission.Completion.TrySetResult(true);
        }
        else
        {
            admission.Completion.TrySetException(failure);
        }
    }

    private void RollBackAdmission(RunAdmission admission)
    {
        // Start methods are synchronous until the admitted operation is
        // scheduled. If snapshotting or scheduling fails, the rejected
        // request must stop consuming observable run capacity before the
        // exception returns to its caller. Resource ownership still drains
        // through the completion path below.
        _activeRuns.TryRemove(admission.RequestId, out _);

        if (admission.RequestCancellation is null)
        {
            admission.Completion.TrySetResult(true);
            return;
        }

        _ = CompleteRolledBackAdmissionAsync(admission);
    }

    private static async Task CompleteRolledBackAdmissionAsync(
        RunAdmission admission)
    {
        var cancellationFailure = await admission.RequestCancellation!
            .CloseAndGetOwner()
            .ConfigureAwait(false);
        admission.RequestCancellation.Dispose();
        if (cancellationFailure is null)
        {
            admission.Completion.TrySetResult(true);
        }
        else
        {
            admission.Completion.TrySetException(cancellationFailure);
        }
    }

    private void AttachRunRemoval(Task runTask, string requestId)
    {
        _ = runTask.ContinueWith(
            static (completedTask, state) =>
            {
                _ = completedTask.Exception;
                var removal = (RunRemoval)state!;
                removal.ActiveRuns.TryRemove(removal.RequestId, out _);
                removal.ActiveRequestCancellations.TryRemove(
                    removal.RequestId,
                    out _);
            },
            new RunRemoval(
                _activeRuns,
                _activeRequestCancellations,
                requestId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal async ValueTask PublishBatchStartedLifecycleAsync(
        MultiActorBatchManifest manifest,
        CancellationToken cancellationToken)
    {
        var json =
            GodotMultiActorVariantMapper.SerializeManifest(manifest);
        _ = await Dispatcher
            .InvokeAsync(
                () =>
                {
                    EmitSignal(
                        SignalName.BatchStarted,
                        GodotProtocolVariantMapper.ParseDictionary(json));
                    return true;
                },
                $"batch-started:{manifest.BatchId}",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask PublishActorFinishedLifecycleAsync(
        string batchId,
        MultiActorRunResult result,
        CancellationToken cancellationToken)
    {
        var json = GodotMultiActorVariantMapper.SerializeResult(result);
        _ = await Dispatcher
            .InvokeAsync(
                () =>
                {
                    var mapped =
                        GodotProtocolVariantMapper.ParseDictionary(json);
                    mapped["batch_id"] = batchId;
                    EmitSignal(SignalName.ActorFinished, mapped);
                    return true;
                },
                $"actor-finished:{batchId}:{result.InputIndex}",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask PublishBatchAbortedLifecycleAsync(
        string batchId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        _ = await Dispatcher
            .InvokeAsync(
                () =>
                {
                    EmitSignal(
                        SignalName.BatchAborted,
                        new GodotDictionary
                        {
                            ["batch_id"] = batchId,
                            ["reason_code"] = reasonCode
                        });
                    return true;
                },
                $"batch-aborted:{batchId}",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private void PublishOnMainThread(GodotEventMessage message)
    {
        try
        {
            PublishOnMainThreadCore(message);
        }
        catch (GodotJsonNumberMappingException exception)
        {
            EmitSignal(
                SignalName.RuntimeError,
                ToErrorDictionary(
                    new GodotEventMessage
                    {
                        RequestId = message.RequestId,
                        Code = exception.ReasonCode,
                        Category = "mapping",
                        Message = exception.Message,
                        ReconciliationRequired = message.Kind is
                            GodotEventKinds.RunCompleted
                            or GodotEventKinds.RoutedRunCompleted
                            or GodotEventKinds.BatchCompleted
                            or GodotEventKinds.BatchParticipantCompleted
                            or GodotEventKinds.GenerationUpdated,
                        Phase = "godot_variant_output"
                    }));
        }
    }

    private void PublishOnMainThreadCore(GodotEventMessage message)
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
            case GodotEventKinds.RoutedRunCompleted:
                {
                    var outcome =
                        GodotProtocolVariantMapper.ParseDictionary(
                            message.Json!);
                    outcome["request_id"] =
                        message.RequestId ?? string.Empty;
                    EmitSignal(SignalName.RoutedRunCompleted, outcome);
                    break;
                }
            case GodotEventKinds.CompletionCompleted:
                {
                    var outcome =
                        GodotProtocolVariantMapper.ParseDictionary(
                            message.Json!);
                    outcome["request_id"] =
                        message.RequestId ?? string.Empty;
                    EmitSignal(SignalName.CompletionCompleted, outcome);
                    break;
                }
            case GodotEventKinds.RunFailed:
                EmitSignal(SignalName.RunFailed, ToErrorDictionary(message));
                break;
            case GodotEventKinds.BatchCompleted:
                {
                    var outcome =
                        GodotProtocolVariantMapper.ParseDictionary(
                            message.Json!);
                    outcome["request_id"] =
                        message.RequestId ?? string.Empty;
                    EmitSignal(SignalName.BatchCompleted, outcome);
                    break;
                }
            case GodotEventKinds.BatchParticipantCompleted:
                {
                    var result =
                        GodotProtocolVariantMapper.ParseDictionary(
                            message.Json!);
                    result["request_id"] =
                        message.RequestId ?? string.Empty;
                    result["operation"] = message.Code ?? string.Empty;
                    EmitSignal(
                        SignalName.BatchParticipantCompleted,
                        result);
                    break;
                }
            case GodotEventKinds.BatchFailed:
                EmitSignal(
                    SignalName.BatchFailed,
                    ToErrorDictionary(message));
                break;
            case GodotEventKinds.GenerationUpdated:
                {
                    var job = GodotProtocolVariantMapper.ParseDictionary(
                        message.Json!);
                    job["request_id"] = message.RequestId ?? string.Empty;
                    job["reconciliation_required"] =
                        message.ReconciliationRequired;
                    EmitSignal(SignalName.GenerationUpdated, job);
                    break;
                }
            case GodotEventKinds.GenerationFailed:
                EmitSignal(
                    SignalName.GenerationFailed,
                    ToErrorDictionary(message));
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
            ScheduleExitCleanupAfterOwnerDrain();
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
        Interlocked.Exchange(ref _actorBatchSlots, null)?.Dispose();
        DisposeLifetimeCancellation();
    }

    private void ScheduleExitCleanupAfterOwnerDrain()
    {
        if (Interlocked.Exchange(ref _exitCleanupScheduled, 1) != 0)
        {
            return;
        }

        Task[] owners;
        lock (_lifecycleGate)
        {
            owners = _activeRuns.Values.ToArray();
        }
        var drain = owners.Length == 0
            ? Task.CompletedTask
            : Task.WhenAll(owners);
        _ = drain.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((GameAgentRuntimeNode)state!).CompleteExitCleanup();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static GodotDictionary ToErrorDictionary(GodotEventMessage message)
    {
        var affectedRunIds = new GodotArray();
        foreach (var runId in message.AffectedRunIds)
        {
            affectedRunIds.Add(runId);
        }

        return new GodotDictionary
        {
            ["request_id"] = message.RequestId ?? string.Empty,
            ["code"] = message.Code ?? "runtime_error",
            ["category"] = message.Category ?? "runtime",
            ["message"] = message.Message ?? "The runtime operation failed.",
            ["count"] = message.Count,
            ["reconciliation_required"] =
                message.ReconciliationRequired,
            ["phase"] = message.Phase ?? string.Empty,
            ["batch_id"] = message.BatchId ?? string.Empty,
            ["participant_run_id"] =
                message.ParticipantRunId ?? string.Empty,
            ["participant_agent_id"] =
                message.ParticipantAgentId ?? string.Empty,
            ["participant_decision_key"] =
                message.ParticipantDecisionKey ?? string.Empty,
            ["participant_input_index"] =
                message.ParticipantInputIndex,
            ["affected_run_ids"] = affectedRunIds
        };
    }

    private void ValidateConfiguration()
    {
        var lifecycleConcurrency =
            (long)MaxConcurrentActorRuns * MaxConcurrentActorBatches
            + MaxConcurrentParticipantOperations;
        if (DispatcherCapacity < 1
            || MaxActiveRuns is < 1
                or > GodotRequestCancellationDispatcher.ReservationCapacity
            || MaxActorBatchSize is < 1 or > 1_024
            || MaxConcurrentActorRuns is < 1 or > 1_024
            || MaxConcurrentActorBatches is < 1 or > 32
            || MaxConcurrentParticipantOperations is < 1 or > 1_024
            || lifecycleConcurrency > DispatcherCapacity
            || lifecycleConcurrency > 1_024
            || MaxCommandsPerFrame < 1
            || CommandBudgetMilliseconds <= 0
            || EventCapacity < 2
            || MaxEventsPerFrame < 1
            || EventBudgetMilliseconds <= 0
            || ShutdownTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                "The GameAgentRuntime Autoload has invalid queue or time budgets.");
        }
    }

    private static string RequiredId(
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException(
                "A non-empty identifier of at most 128 ASCII characters is required.",
                parameterName);
        }

        foreach (var character in value)
        {
            var allowed = character is >= 'A' and <= 'Z'
                          || character is >= 'a' and <= 'z'
                          || character is >= '0' and <= '9'
                          || character is '.' or '_' or ':' or '-';
            if (!allowed)
            {
                throw new ArgumentException(
                    "An identifier may contain only ASCII letters, digits, '.', '_', ':', and '-'.",
                    parameterName);
            }
        }

        return value;
    }

    private static Exception Combine(Exception? first, Exception next) =>
        first is null ? next : new AggregateException(first, next);

    private static bool TryDescribeUncertainty(
        Exception exception,
        out MultiActorUncertainty? uncertainty)
    {
        ArgumentNullException.ThrowIfNull(exception);

        IReadOnlyList<Exception> candidates =
            exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : new[] { exception };
        var execution = candidates
            .OfType<MultiActorBatchExecutionUncertainException>()
            .ToArray();
        if (execution.Length > 0)
        {
            var first = execution[0];
            uncertainty = new MultiActorUncertainty(
                "batch_execution_uncertain",
                "participant_execution",
                first.BatchId,
                execution
                    .SelectMany(item => item.RunIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
            return true;
        }

        var abort = candidates
            .OfType<MultiActorBatchAbortUncertainException>()
            .FirstOrDefault();
        if (abort is not null)
        {
            uncertainty = new MultiActorUncertainty(
                "batch_lifecycle_uncertain",
                abort.ReasonCode,
                abort.BatchId,
                Array.Empty<string>());
            return true;
        }

        uncertainty = null;
        return false;
    }

    private sealed record MultiActorUncertainty(
        string Code,
        string Phase,
        string BatchId,
        IReadOnlyList<string> RunIds);

    private sealed record ParticipantResumeSnapshot(
        MultiActorBatchParticipant Participant,
        DurableRunContinuation? Continuation);

    private sealed class RunAdmission
    {
        internal RunAdmission(
            string requestId,
            TaskCompletionSource<bool> completion,
            CancellationToken token,
            RequestCancellationState? requestCancellation)
        {
            RequestId = requestId;
            Completion = completion;
            Token = token;
            RequestCancellation = requestCancellation;
        }

        internal string RequestId { get; }

        internal TaskCompletionSource<bool> Completion { get; }

        internal CancellationToken Token { get; }

        internal RequestCancellationState? RequestCancellation { get; }
    }

    private sealed class RequestCancellationState : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _source;
        private readonly GodotRequestCancellationDispatcher.Reservation
            _reservation;
        private readonly CancellationTokenRegistration _lifetimeRegistration;
        private readonly CancellationTokenRegistration _callerRegistration;
        private Task<Exception?> _owner = Task.FromResult<Exception?>(null);
        private bool _dispatchStarted;
        private bool _closed;
        private bool _disposed;

        internal RequestCancellationState(
            CancellationTokenSource source,
            GodotRequestCancellationDispatcher.Reservation reservation,
            CancellationToken lifetimeToken,
            CancellationToken callerToken)
        {
            _source = source;
            _reservation = reservation;
            _lifetimeRegistration = lifetimeToken.Register(
                static state =>
                {
                    _ = ((RequestCancellationState)state!).TryDispatch();
                },
                this);
            _callerRegistration = callerToken.Register(
                static state =>
                {
                    _ = ((RequestCancellationState)state!).TryDispatch();
                },
                this);
        }

        internal CancellationToken Token => _source.Token;

        internal bool TryDispatch()
        {
            lock (_sync)
            {
                if (_closed || _dispatchStarted)
                {
                    return false;
                }

                if (!GodotRequestCancellationDispatcher.TryDispatchReserved(
                        _reservation,
                        _source.Cancel,
                        out var owner))
                {
                    return false;
                }

                _owner = owner;
                _dispatchStarted = true;
                return true;
            }
        }

        internal async Task<Exception?> CloseAndGetOwner()
        {
            Task<Exception?> owner;
            lock (_sync)
            {
                _closed = true;
                owner = _owner;
            }

            return await owner.ConfigureAwait(false);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _closed = true;
                _disposed = true;
            }

            _callerRegistration.Dispose();
            _lifetimeRegistration.Dispose();
            _source.Dispose();
            _reservation.Dispose();
        }
    }

    private sealed class FacadeRequestMappingException : Exception
    {
        internal FacadeRequestMappingException(Exception innerException)
            : base("The engine request could not be mapped.", innerException)
        {
        }
    }

    private sealed class RunRemoval
    {
        public RunRemoval(
            ConcurrentDictionary<string, Task> activeRuns,
            ConcurrentDictionary<string, RequestCancellationState>
                activeRequestCancellations,
            string requestId)
        {
            ActiveRuns = activeRuns;
            ActiveRequestCancellations = activeRequestCancellations;
            RequestId = requestId;
        }

        public ConcurrentDictionary<string, Task> ActiveRuns { get; }

        public ConcurrentDictionary<string, RequestCancellationState>
            ActiveRequestCancellations { get; }

        public string RequestId { get; }
    }
}

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using GameAgent.Core;
using GameAgent.Protocol;
using Godot;
using GodotArray = global::Godot.Collections.Array;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot;

[global::Godot.GlobalClass]
public partial class GameAgentRuntimeNode : global::Godot.Node
{
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
    private Task<Exception?>? _lifetimeCancellationTask;
    private Task? _stopTask;
    private int _acceptingRuns;
    private int _exitStarted;

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
        ValidateConfiguration();
        _lifetimeCancellation = new CancellationTokenSource();
        _dispatcher = new GodotMainThreadDispatcher(DispatcherCapacity);
        _eventPump = new GodotEventPump(EventCapacity);
        _runtimeEventPublisher = new GodotRuntimeEventPublisher(_eventPump);
        Typed = new GodotRuntimeHost(this);
        Volatile.Write(ref _acceptingRuns, 1);
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
        var stopTask = EnsureStopTask();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!stopTask.IsCompleted && DateTimeOffset.UtcNow < deadline)
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

        while (EventPump.PendingCount > 0
               && DateTimeOffset.UtcNow < deadline)
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

        EventPump.StopAccepting();
        Dispatcher.StopAccepting();
        DisposeLifetimeCancellation();
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
            _stopTask ??= StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref _acceptingRuns, 0);
        Dispatcher.StopAccepting();

        Exception? shutdownError = null;
        var cancellationTask = RequestLifetimeCancellation();

        try
        {
            var active = _activeRuns.Values
                .Append(cancellationTask)
                .ToArray();
            if (active.Length > 0)
            {
                await Task
                    .WhenAll(active)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
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
            await Dispatcher
                .WaitForRunningWorkAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            shutdownError = Combine(shutdownError, exception);
        }

        try
        {
            var durableBackend = Volatile.Read(ref _durableBackend);
            if (durableBackend is not null)
            {
                await durableBackend
                    .StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                var backend = Volatile.Read(ref _backend);
                if (backend is not null)
                {
                    await backend
                        .StopAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            shutdownError = Combine(shutdownError, exception);
        }

        var stopped = new GodotEventMessage
        {
            Kind = GodotEventKinds.RuntimeStopped,
            Code = shutdownError is null ? "graceful" : "shutdown_incomplete",
            Category = shutdownError is null ? "lifecycle" : "shutdown",
            Message = shutdownError is null
                ? "The Godot runtime stopped gracefully."
                : "The Godot runtime stopped before all work could be flushed.",
            Count = _activeRuns.Count
        };

        try
        {
            await EventPump
                .PublishCriticalAsync(stopped, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            shutdownError = Combine(shutdownError, exception);
        }
        finally
        {
            Dispatcher.StopAccepting();
            EventPump.StopAccepting();
        }

        if (shutdownError is not null)
        {
            ExceptionDispatchInfo.Capture(shutdownError).Throw();
        }
    }

    private Task<Exception?> RequestLifetimeCancellation()
    {
        var source = _lifetimeCancellation;
        Task<Exception?> cancellation;
        try
        {
            cancellation = source is null
                ? Task.FromResult<Exception?>(null)
                : Task.Run(
                    () =>
                    {
                        try
                        {
                            source.Cancel();
                            return null;
                        }
                        catch (Exception exception)
                        {
                            return exception;
                        }
                    });
        }
        catch (Exception exception)
        {
            cancellation = Task.FromResult<Exception?>(exception);
        }

        Volatile.Write(ref _lifetimeCancellationTask, cancellation);
        return cancellation;
    }

    private void DisposeLifetimeCancellation()
    {
        var source = Interlocked.Exchange(
            ref _lifetimeCancellation,
            null);
        if (source is null)
        {
            return;
        }

        var cancellation = Volatile.Read(ref _lifetimeCancellationTask);
        if (cancellation is null || cancellation.IsCompleted)
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

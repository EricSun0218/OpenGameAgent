using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameAgent.Core;
using GameAgent.Generation;
using GameAgent.Protocol;
using GameAgent.Runtime;

namespace GameAgent.Godot;

// Kept for source compatibility with the v0 headless adapter.
public interface IGodotRuntimeBackend
{
    ValueTask<HeadlessRunOutcome> RunAsync(
        HeadlessRunRequest request,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IGodotDurableRuntimeBackend
{
    ValueTask<DurableRunOutcome> RunAsync(
        DurableRunRequest request,
        CancellationToken cancellationToken);

    ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken);

    bool TryPostControl(string runId, RunControlCommand command);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IGodotRoutedExecutionBackend
{
    ValueTask<RoutedExecutionOutcome> RunRoutedAsync(
        RoutedExecutionRequest request,
        CancellationToken cancellationToken);

    ValueTask<SimpleCompletionOutcome> CompleteAsync(
        SimpleCompletionRequest request,
        CancellationToken cancellationToken);
}

public interface IGodotChildAgentBackend
{
    ValueTask<ChildAgentRunResult> RunChildAsync(
        string parentRunId,
        DurableRunRequest request,
        CancellationToken cancellationToken);

    int CancelChildren(string parentRunId);
}

public interface IGodotPersistentChildAgentBackend
{
    ValueTask<ChildAgentRunResult> RunChildAsync(
        AgentRun parentRun,
        DurableRunRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional capability for backends that can report a stable control
/// rejection reason.
/// </summary>
public interface IGodotControlRejectionBackend
{
    bool TryPostControl(
        string runId,
        RunControlCommand command,
        out string? rejectionReason);
}

/// <summary>
/// Optional durable-backend capability for a resume guard that is evaluated
/// before provider, reconciler, or game-host work.
/// </summary>
public interface IGodotGuardedDurableRuntimeBackend
    : IGodotDurableRuntimeBackend
{
    bool SupportsGuardedResume { get; }

    ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken,
        DurableRunResumeGuard guard);
}

public sealed class GodotDurableResumeOptions
{
    public DurableRunContinuation? Continuation { get; set; }

    public IGameOperationReconciler? Reconciler { get; set; }

    public DurableRunResumeGuard? Guard { get; set; }
}

public sealed class GodotParticipantResumeOptions
{
    public DurableRunContinuation? Continuation { get; set; }

    public IGameOperationReconciler? Reconciler { get; set; }

    public DurableRunSemanticExpectation? SemanticExpectation { get; set; }
}

internal static class GodotShutdownWait
{
    private static int _pendingTimeoutCount;

    internal static int PendingTimeoutCount =>
        Volatile.Read(ref _pendingTimeoutCount);

    public static ValueTask WaitAsync(
        Task shutdown,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return new ValueTask(
            AwaitWithTimeoutAsync(
                shutdown,
                timeout,
                cancellationToken,
                RegisterCancellation));
    }

    internal static ValueTask WaitAsync(
        Task shutdown,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<
            CancellationToken,
            Action,
            CancellationTokenRegistration> registerCancellation)
    {
        ArgumentNullException.ThrowIfNull(registerCancellation);
        return new ValueTask(
            AwaitWithTimeoutAsync(
                shutdown,
                timeout,
                cancellationToken,
                registerCancellation));
    }

    public static ValueTask WaitAsync(
        Task shutdown,
        CancellationToken cancellationToken)
    {
        return cancellationToken.CanBeCanceled
            ? new ValueTask(
                AwaitWithCancellationAsync(shutdown, cancellationToken))
            : new ValueTask(shutdown);
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

        var cancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => cancelled.TrySetCanceled(cancellationToken));
        var completed = await Task.WhenAny(shutdown, cancelled.Task)
            .ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }

    private static async Task AwaitWithTimeoutAsync(
        Task shutdown,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<
            CancellationToken,
            Action,
            CancellationTokenRegistration> registerCancellation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shutdown.IsCompleted)
        {
            await shutdown.ConfigureAwait(false);
            return;
        }

        var cancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = registerCancellation(
            cancellationToken,
            () => cancelled.TrySetCanceled(cancellationToken));
        using var timeoutCancellation = new CancellationTokenSource();
        var timedOut = Task.Delay(timeout, timeoutCancellation.Token);
        Interlocked.Increment(ref _pendingTimeoutCount);
        try
        {
            var completed = await Task
                .WhenAny(shutdown, timedOut, cancelled.Task)
                .ConfigureAwait(false);

            if (ReferenceEquals(completed, shutdown))
            {
                await shutdown.ConfigureAwait(false);
                return;
            }

            if (ReferenceEquals(completed, cancelled.Task))
            {
                await cancelled.Task.ConfigureAwait(false);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                "Timed out while waiting for the Godot runtime to stop.");
        }
        finally
        {
            timeoutCancellation.Cancel();
            try
            {
                await timedOut.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (timeoutCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _pendingTimeoutCount);
            }
        }
    }

    private static CancellationTokenRegistration RegisterCancellation(
        CancellationToken cancellationToken,
        Action callback) =>
        cancellationToken.Register(callback);
}

internal sealed class HeadlessGodotRuntimeBackend : IGodotRuntimeBackend
{
    private readonly HeadlessAgentRuntime _runtime;
    private readonly IDurableSessionStore? _durableStore;
    private readonly bool _disposeStoreOnShutdown;
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private bool _storeFlushed;
    private bool _storeDisposed;
    private bool _stopCompleted;

    public HeadlessGodotRuntimeBackend(
        HeadlessAgentRuntime runtime,
        ISessionStore store,
        bool disposeStoreOnShutdown)
    {
        _runtime = runtime;
        _durableStore = store as IDurableSessionStore;
        _disposeStoreOnShutdown = disposeStoreOnShutdown;
    }

    public ValueTask<HeadlessRunOutcome> RunAsync(
        HeadlessRunRequest request,
        CancellationToken cancellationToken) =>
        _runtime.RunAsync(request, cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_stopGate)
        {
            if (_stopTask is null || (_stopTask.IsCompleted && !_stopCompleted))
            {
                _stopTask = StopCoreAsync();
            }
            shutdown = _stopTask;
        }

        return GodotShutdownWait.WaitAsync(shutdown, cancellationToken);
    }

    private async Task StopCoreAsync()
    {
        if (_durableStore is null)
        {
            _stopCompleted = true;
            return;
        }

        if (!_storeFlushed)
        {
            await _durableStore
                .FlushAsync(CancellationToken.None)
                .ConfigureAwait(false);
            _storeFlushed = true;
        }

        if (_disposeStoreOnShutdown && !_storeDisposed)
        {
            await _durableStore.DisposeAsync().ConfigureAwait(false);
            _storeDisposed = true;
        }

        _stopCompleted = true;
    }
}

public sealed class GodotDurableRuntimeBackend
    : IGodotGuardedDurableRuntimeBackend,
      IGodotControlRejectionBackend
{
    private readonly IDurableAgentRuntime _runtime;
    private readonly IDurableSessionStore _store;
    private readonly bool _disposeRuntimeOnShutdown;
    private readonly bool _disposeStoreOnShutdown;
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private bool _runtimeDisposed;
    private bool _storeFlushed;
    private bool _storeDisposed;
    private bool _stopCompleted;

    public GodotDurableRuntimeBackend(
        IDurableAgentRuntime runtime,
        IDurableSessionStore store,
        bool disposeRuntimeOnShutdown = false,
        bool disposeStoreOnShutdown = false)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _disposeRuntimeOnShutdown = disposeRuntimeOnShutdown;
        _disposeStoreOnShutdown = disposeStoreOnShutdown;

        if (disposeRuntimeOnShutdown
            && runtime is not IDisposable
            && runtime is not IAsyncDisposable)
        {
            throw new ArgumentException(
                "An owned durable runtime must implement IDisposable or IAsyncDisposable.",
                nameof(runtime));
        }
    }

    public ValueTask<DurableRunOutcome> RunAsync(
        DurableRunRequest request,
        CancellationToken cancellationToken) =>
        _runtime.RunAsync(request, cancellationToken);

    public bool SupportsGuardedResume =>
        _runtime is IGuardedDurableAgentRuntime;

    public ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken) =>
        _runtime.ResumeAsync(
            runId,
            continuation,
            reconciler,
            cancellationToken);

    public ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken,
        DurableRunResumeGuard guard) =>
        _runtime.ResumeAsync(
            runId,
            continuation,
            reconciler,
            cancellationToken,
            guard);

    public bool TryPostControl(string runId, RunControlCommand command) =>
        _runtime.Controls.TryPost(runId, command);

    public bool TryPostControl(
        string runId,
        RunControlCommand command,
        out string? rejectionReason) =>
        _runtime.Controls.TryPost(runId, command, out rejectionReason);

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_stopGate)
        {
            if (_stopTask is null || (_stopTask.IsCompleted && !_stopCompleted))
            {
                _stopTask = StopCoreAsync();
            }
            shutdown = _stopTask;
        }

        return GodotShutdownWait.WaitAsync(shutdown, cancellationToken);
    }

    private async Task StopCoreAsync()
    {
        if (_disposeRuntimeOnShutdown && !_runtimeDisposed)
        {
            await DisposeRuntimeAsync().ConfigureAwait(false);
            _runtimeDisposed = true;
        }

        if (!_storeFlushed)
        {
            await _store
                .FlushAsync(CancellationToken.None)
                .ConfigureAwait(false);
            _storeFlushed = true;
        }

        if (_disposeStoreOnShutdown && !_storeDisposed)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _storeDisposed = true;
        }

        _stopCompleted = true;
    }

    private ValueTask DisposeRuntimeAsync()
    {
        if (_runtime is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }

        ((IDisposable)_runtime).Dispose();
        return default;
    }
}

public sealed class GodotBuiltRuntimeBackend
    : IGodotGuardedDurableRuntimeBackend,
      IGodotControlRejectionBackend,
      IGodotRoutedExecutionBackend,
      IGodotChildAgentBackend,
      IGodotPersistentChildAgentBackend
{
    private readonly BuiltGameAgentRuntime _built;
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private bool _stopCompleted;

    public GodotBuiltRuntimeBackend(BuiltGameAgentRuntime built)
    {
        _built = built ?? throw new ArgumentNullException(nameof(built));
    }

    public ValueTask<DurableRunOutcome> RunAsync(
        DurableRunRequest request,
        CancellationToken cancellationToken) =>
        _built.Runtime.RunAsync(request, cancellationToken);

    public bool SupportsGuardedResume => true;

    public ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken) =>
        _built.Runtime.ResumeAsync(
            runId,
            continuation,
            reconciler,
            cancellationToken);

    public ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken,
        DurableRunResumeGuard guard) =>
        _built.Runtime.ResumeAsync(
            runId,
            continuation,
            reconciler,
            cancellationToken,
            guard);

    public bool TryPostControl(string runId, RunControlCommand command) =>
        _built.Runtime.Controls.TryPost(runId, command);

    public bool TryPostControl(
        string runId,
        RunControlCommand command,
        out string? rejectionReason) =>
        _built.Runtime.Controls.TryPost(
            runId,
            command,
            out rejectionReason);

    public ValueTask<RoutedExecutionOutcome> RunRoutedAsync(
        RoutedExecutionRequest request,
        CancellationToken cancellationToken) =>
        _built.Execution.RunAsync(request, cancellationToken);

    public ValueTask<SimpleCompletionOutcome> CompleteAsync(
        SimpleCompletionRequest request,
        CancellationToken cancellationToken) =>
        _built.Completion.CompleteAsync(request, cancellationToken);

    public ValueTask<ChildAgentRunResult> RunChildAsync(
        string parentRunId,
        DurableRunRequest request,
        CancellationToken cancellationToken) =>
        _built.Children.RunChildAsync(
            parentRunId,
            request,
            cancellationToken);

    public ValueTask<ChildAgentRunResult> RunChildAsync(
        AgentRun parentRun,
        DurableRunRequest request,
        CancellationToken cancellationToken) =>
        _built.Children.RunChildAsync(
            parentRun,
            request,
            cancellationToken);

    public int CancelChildren(string parentRunId) =>
        _built.Children.CancelChildren(parentRunId);

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_stopGate)
        {
            if (_stopTask is null || (_stopTask.IsCompleted && !_stopCompleted))
            {
                _stopTask = StopCoreAsync();
            }
            shutdown = _stopTask;
        }

        return GodotShutdownWait.WaitAsync(shutdown, cancellationToken);
    }

    private async Task StopCoreAsync()
    {
        await _built.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopCompleted = true;
    }
}

internal sealed class GodotEventForwardingSessionStore : ISessionStore
{
    private readonly ISessionStore _inner;
    private readonly GodotEventPump _eventPump;

    public GodotEventForwardingSessionStore(
        ISessionStore inner,
        GodotEventPump eventPump)
    {
        _inner = inner;
        _eventPump = eventPump;
    }

    public async ValueTask AppendAsync(
        RuntimeEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        await _inner
            .AppendAsync(runtimeEvent, cancellationToken)
            .ConfigureAwait(false);

        _eventPump.TryPublish(new GodotEventMessage
        {
            Kind = GodotEventKinds.RuntimeEvent,
            Json = ProtocolJson.Serialize(runtimeEvent)
        });
    }

    public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
        string runId,
        CancellationToken cancellationToken) =>
        _inner.ReadRunAsync(runId, cancellationToken);
}

public sealed class GodotRuntimeEventPublisher :
    INonBlockingRuntimeEventPublisher
{
    private readonly GodotEventPump _eventPump;

    internal GodotRuntimeEventPublisher(GodotEventPump eventPump)
    {
        _eventPump = eventPump;
    }

    public void Publish(RuntimeEvent runtimeEvent)
    {
        if (runtimeEvent is null)
        {
            throw new ArgumentNullException(nameof(runtimeEvent));
        }

        _eventPump.TryPublish(new GodotEventMessage
        {
            Kind = GodotEventKinds.RuntimeEvent,
            Json = ProtocolJson.Serialize(runtimeEvent)
        });
    }
}

public sealed class GodotRuntimeHost
{
    private readonly GameAgentRuntimeNode _node;

    internal GodotRuntimeHost(GameAgentRuntimeNode node)
    {
        _node = node;
    }

    public GodotMainThreadDispatcher Dispatcher => _node.Dispatcher;

    public bool IsConfigured => _node.IsBackendConfigured;

    public IRuntimeEventPublisher EventPublisher => _node.RuntimeEventPublisher;

    public void ConfigureGeneration(GenerationRuntime runtime)
    {
        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        _node.ConfigureGenerationRuntime(runtime);
    }

    // Legacy headless backend registration.
    public void Configure(IGodotRuntimeBackend backend)
    {
        if (backend is null)
        {
            throw new ArgumentNullException(nameof(backend));
        }

        _node.ConfigureBackend(backend);
    }

    public void ConfigureDurable(IGodotDurableRuntimeBackend backend)
    {
        if (backend is null)
        {
            throw new ArgumentNullException(nameof(backend));
        }

        _node.ConfigureDurableBackend(backend);
    }

    public void ConfigureDurable(
        IDurableAgentRuntime runtime,
        IDurableSessionStore store,
        bool disposeRuntimeOnShutdown = false,
        bool disposeStoreOnShutdown = false)
    {
        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        _node.ConfigureDurableBackend(
            new GodotDurableRuntimeBackend(
                runtime,
                store,
                disposeRuntimeOnShutdown,
                disposeStoreOnShutdown),
            runtime);
    }

    public void ConfigureDurable(BuiltGameAgentRuntime built)
    {
        if (built is null)
        {
            throw new ArgumentNullException(nameof(built));
        }

        _node.ConfigureDurableBackend(
            new GodotBuiltRuntimeBackend(built),
            built.Runtime);
    }

    /// <summary>
    /// Enables Core multi-actor coordination for a custom durable backend.
    /// The supplied runtime must be the same durable identity owner used by
    /// that backend.
    /// </summary>
    public void ConfigureMultiActor(IDurableAgentRuntime runtime)
    {
        _node.ConfigureMultiActorRuntime(runtime);
    }

    public void ConfigureHeadless(
        IModelProvider provider,
        IGameHost gameHost,
        ISessionStore store,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        bool disposeStoreOnShutdown = false)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (gameHost is null)
        {
            throw new ArgumentNullException(nameof(gameHost));
        }

        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }

        if (ids is null)
        {
            throw new ArgumentNullException(nameof(ids));
        }

        var forwardingStore = new GodotEventForwardingSessionStore(
            store,
            _node.EventPump);
        var runtime = new HeadlessAgentRuntime(
            provider,
            gameHost,
            forwardingStore,
            clock,
            ids);
        Configure(
            new HeadlessGodotRuntimeBackend(
                runtime,
                store,
                disposeStoreOnShutdown));
    }

    public string StartRun(DurableRunRequest request) =>
        _node.StartTypedDurableRun(request);

    public string StartRoutedRun(
        RoutedExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        _node.StartTypedRoutedRun(request, cancellationToken);

    public string StartCompletion(
        SimpleCompletionRequest request,
        CancellationToken cancellationToken = default) =>
        _node.StartTypedCompletion(request, cancellationToken);

    public string StartGeneration(GenerationRequest request) =>
        _node.StartTypedGeneration(request);

    public string StartChildRun(
        string parentRunId,
        DurableRunRequest request) =>
        _node.StartTypedChildRun(parentRunId, request);

    public string StartChildRun(
        AgentRun parentRun,
        DurableRunRequest request) =>
        _node.StartTypedChildRun(parentRun, request);

    public int CancelChildren(string parentRunId) =>
        _node.CancelTypedChildren(parentRunId);

    public bool CancelRequest(string requestId) =>
        _node.CancelTypedRequest(requestId);

    public string ResumeRun(
        string runId,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null) =>
        _node.ResumeTypedDurableRun(runId, continuation, reconciler);

    public string ResumeRun(
        string runId,
        DurableRunResumeGuard guard,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null) =>
        _node.ResumeTypedDurableRun(
            runId,
            continuation,
            reconciler,
            guard);

    public string ResumeRun(
        string runId,
        GodotDurableResumeOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return _node.ResumeTypedDurableRun(
            runId,
            options.Continuation,
            options.Reconciler,
            options.Guard);
    }

    public string StartBatch(MultiActorDecisionBatch batch) =>
        _node.StartTypedActorBatch(batch);

    public string ResumeBatchParticipant(
        string batchId,
        MultiActorBatchParticipant participant,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null) =>
        _node.ResumeTypedActorBatchParticipant(
            batchId,
            participant,
            continuation,
            reconciler);

    public string ResumeBatchParticipant(
        string batchId,
        MultiActorBatchParticipant participant,
        DurableRunSemanticExpectation semanticExpectation,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null) =>
        _node.ResumeTypedActorBatchParticipant(
            batchId,
            participant,
            continuation,
            reconciler,
            semanticExpectation);

    public string ResumeBatchParticipant(
        string batchId,
        MultiActorBatchParticipant participant,
        GodotParticipantResumeOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return _node.ResumeTypedActorBatchParticipant(
            batchId,
            participant,
            options.Continuation,
            options.Reconciler,
            options.SemanticExpectation);
    }

    public string AbandonBatchParticipant(
        string batchId,
        MultiActorBatchParticipant participant,
        string reasonCode,
        IGameOperationReconciler? reconciler = null) =>
        _node.AbandonTypedActorBatchParticipant(
            batchId,
            participant,
            reasonCode,
            reconciler);

    public bool TryPostControl(string runId, RunControlCommand command) =>
        _node.TryPostTypedControl(runId, command);

    public bool CancelRun(
        string runId,
        string? commandId = null,
        DateTimeOffset? createdAt = null) =>
        PostControl(
            runId,
            RunControlKinds.Cancel,
            observation: null,
            commandId,
            createdAt);

    public bool InterruptRun(
        string runId,
        string? commandId = null,
        DateTimeOffset? createdAt = null) =>
        PostControl(
            runId,
            RunControlKinds.Interrupt,
            observation: null,
            commandId,
            createdAt);

    public bool SteerRun(
        string runId,
        ObservationEnvelope observation,
        string? commandId = null,
        DateTimeOffset? createdAt = null) =>
        PostControl(
            runId,
            RunControlKinds.Steer,
            observation,
            commandId,
            createdAt);

    public bool FollowUpRun(
        string runId,
        ObservationEnvelope observation,
        string? commandId = null,
        DateTimeOffset? createdAt = null) =>
        PostControl(
            runId,
            RunControlKinds.FollowUp,
            observation,
            commandId,
            createdAt);

    public string StartRun(HeadlessRunRequest request) =>
        _node.StartTypedRun(request);

    public ValueTask StopAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        _node.StopAsync(timeout, cancellationToken);

    private bool PostControl(
        string runId,
        string kind,
        ObservationEnvelope? observation,
        string? commandId,
        DateTimeOffset? createdAt)
    {
        return TryPostControl(
            runId,
            new RunControlCommand
            {
                CommandId = string.IsNullOrWhiteSpace(commandId)
                    ? Guid.NewGuid().ToString("N")
                    : commandId,
                Kind = kind,
                Observation = observation,
                CreatedAt = createdAt ?? DateTimeOffset.UtcNow
            });
    }
}

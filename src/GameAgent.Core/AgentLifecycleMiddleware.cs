using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public static class AgentLifecycleEventKinds
{
    public const string RunStarting = "run_starting";
    public const string RunCompleted = "run_completed";
    public const string ModelDispatching = "model_dispatching";
    public const string ModelCompleted = "model_completed";
    public const string ToolBatchDispatching = "tool_batch_dispatching";
    public const string ToolBatchCompleted = "tool_batch_completed";
}

public abstract class AgentLifecycleEvent
{
    protected AgentLifecycleEvent(
        string kind,
        string runId,
        string? turnId)
    {
        Kind = RuntimeGuard.RequiredUtf8(kind, 64, nameof(kind));
        RunId = RuntimeGuard.RequiredId(runId, nameof(runId));
        TurnId = turnId is null
            ? null
            : RuntimeGuard.RequiredId(turnId, nameof(turnId));
    }

    public string Kind { get; }

    public string RunId { get; }

    public string? TurnId { get; }
}

public sealed class RunStartingLifecycleEvent : AgentLifecycleEvent
{
    internal RunStartingLifecycleEvent(
        string runId,
        string? agentId,
        string? worldId,
        string? sessionId,
        bool isResume,
        GameContextCoordinate? gameContext = null)
        : base(AgentLifecycleEventKinds.RunStarting, runId, turnId: null)
    {
        AgentId = Optional(agentId, nameof(agentId));
        WorldId = Optional(worldId, nameof(worldId));
        SessionId = Optional(sessionId, nameof(sessionId));
        IsResume = isResume;
        GameContext = gameContext;
    }

    public string? AgentId { get; }

    public string? WorldId { get; }

    public string? SessionId { get; }

    public bool IsResume { get; }

    /// <summary>
    /// Runtime-validated game coordinate for admission decisions. The run's
    /// explicit <see cref="SessionId"/> remains authoritative when this
    /// optional coordinate inherits or omits a session. On resume both values
    /// are recovered from the durable run before middleware runs.
    /// </summary>
    public GameContextCoordinate? GameContext { get; }

    private static string? Optional(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : RuntimeGuard.RequiredUtf8(value, 128, parameterName);
}

public sealed class RunCompletedLifecycleEvent : AgentLifecycleEvent
{
    internal RunCompletedLifecycleEvent(
        string runId,
        bool isResume,
        DurableRunOutcome? outcome,
        Exception? exception)
        : base(AgentLifecycleEventKinds.RunCompleted, runId, turnId: null)
    {
        IsResume = isResume;
        State = outcome?.Run.State;
        TerminalReason = outcome?.Run.TerminalReason;
        ErrorCode = outcome?.ErrorCode;
        ReconciliationRequired = outcome?.ReconciliationRequired == true;
        Threw = exception is not null;
        ExceptionType = exception?.GetType().FullName;
    }

    public bool IsResume { get; }

    public string? State { get; }

    public string? TerminalReason { get; }

    public string? ErrorCode { get; }

    public bool ReconciliationRequired { get; }

    public bool Threw { get; }

    public string? ExceptionType { get; }
}

public sealed class ModelDispatchingLifecycleEvent : AgentLifecycleEvent
{
    internal ModelDispatchingLifecycleEvent(
        string runId,
        string turnId,
        string promptDigest,
        int messageCount,
        IReadOnlyList<string> providerIds,
        IReadOnlyList<string> toolNames,
        ModelInferenceOptions? inference)
        : base(
            AgentLifecycleEventKinds.ModelDispatching,
            runId,
            turnId)
    {
        PromptDigest = promptDigest;
        MessageCount = messageCount;
        ProviderIds = CopyStrings(providerIds, 64, nameof(providerIds));
        ToolNames = CopyStrings(toolNames, 4_096, nameof(toolNames));
        Inference = inference?.CloneValidated();
    }

    public string PromptDigest { get; }

    public int MessageCount { get; }

    public IReadOnlyList<string> ProviderIds { get; }

    public IReadOnlyList<string> ToolNames { get; }

    public ModelInferenceOptions? Inference { get; }

    private static IReadOnlyList<string> CopyStrings(
        IReadOnlyList<string> source,
        int maximum,
        string parameterName)
    {
        if (source is null || source.Count > maximum)
        {
            throw new ArgumentException(
                "The lifecycle collection is invalid.",
                parameterName);
        }

        return new ReadOnlyCollection<string>(
            source
                .Select(
                    value => RuntimeGuard.RequiredUtf8(
                        value,
                        256,
                        parameterName))
                .ToArray());
    }
}

public sealed class ModelCompletedLifecycleEvent : AgentLifecycleEvent
{
    internal ModelCompletedLifecycleEvent(
        string runId,
        string turnId,
        ProviderAttemptResult result)
        : base(AgentLifecycleEventKinds.ModelCompleted, runId, turnId)
    {
        ProviderId = RuntimeGuard.RequiredUtf8(
            result.ProviderId,
            128,
            nameof(result));
        FinishReason = result.FinishReason;
        ToolCallCount = result.ToolCalls.Count;
        HasText = result.Text is not null;
        TextUtf8Bytes = result.Text is null
            ? 0
            : System.Text.Encoding.UTF8.GetByteCount(result.Text);
        InputTokens = result.Usage.InputTokens;
        OutputTokens = result.Usage.OutputTokens;
    }

    public string ProviderId { get; }

    public string? FinishReason { get; }

    public int ToolCallCount { get; }

    public bool HasText { get; }

    public int TextUtf8Bytes { get; }

    public int InputTokens { get; }

    public int OutputTokens { get; }
}

public sealed class ToolLifecycleCall
{
    internal ToolLifecycleCall(
        string toolCallId,
        string toolName,
        string effect,
        JsonElement arguments)
    {
        ToolCallId = RuntimeGuard.RequiredId(
            toolCallId,
            nameof(toolCallId));
        ToolName = RuntimeGuard.RequiredUtf8(
            toolName,
            256,
            nameof(toolName));
        Effect = RuntimeGuard.RequiredUtf8(effect, 64, nameof(effect));
        JsonValueInspector.ValidateAndMeasure(
            arguments,
            new JsonValueLimits(maxUtf8Bytes: 1_048_576),
            nameof(arguments));
        Arguments = arguments.Clone();
    }

    public string ToolCallId { get; }

    public string ToolName { get; }

    public string Effect { get; }

    public JsonElement Arguments { get; }
}

public sealed class ToolBatchDispatchingLifecycleEvent : AgentLifecycleEvent
{
    internal ToolBatchDispatchingLifecycleEvent(
        string runId,
        string turnId,
        IReadOnlyList<ToolLifecycleCall> calls)
        : base(
            AgentLifecycleEventKinds.ToolBatchDispatching,
            runId,
            turnId)
    {
        Calls = SnapshotCalls(calls);
    }

    public IReadOnlyList<ToolLifecycleCall> Calls { get; }

    private static IReadOnlyList<ToolLifecycleCall> SnapshotCalls(
        IReadOnlyList<ToolLifecycleCall> source)
    {
        if (source is null || source.Count > 4_096)
        {
            throw new ArgumentException(
                "The tool lifecycle call list is invalid.",
                nameof(source));
        }

        return new ReadOnlyCollection<ToolLifecycleCall>(source.ToArray());
    }
}

public sealed class ToolLifecycleResult
{
    internal ToolLifecycleResult(
        string operationId,
        string toolCallId,
        string status,
        string? errorCode)
    {
        OperationId = RuntimeGuard.RequiredId(
            operationId,
            nameof(operationId));
        ToolCallId = RuntimeGuard.RequiredId(
            toolCallId,
            nameof(toolCallId));
        Status = RuntimeGuard.RequiredUtf8(status, 64, nameof(status));
        ErrorCode = string.IsNullOrWhiteSpace(errorCode)
            ? null
            : RuntimeGuard.RequiredUtf8(
                errorCode,
                256,
                nameof(errorCode));
    }

    public string OperationId { get; }

    public string ToolCallId { get; }

    public string Status { get; }

    public string? ErrorCode { get; }
}

public sealed class ToolBatchCompletedLifecycleEvent : AgentLifecycleEvent
{
    internal ToolBatchCompletedLifecycleEvent(
        string runId,
        string turnId,
        IReadOnlyList<ToolLifecycleResult> results)
        : base(AgentLifecycleEventKinds.ToolBatchCompleted, runId, turnId)
    {
        if (results is null || results.Count > 4_096)
        {
            throw new ArgumentException(
                "The tool lifecycle result list is invalid.",
                nameof(results));
        }

        Results = new ReadOnlyCollection<ToolLifecycleResult>(
            results.ToArray());
    }

    public IReadOnlyList<ToolLifecycleResult> Results { get; }
}

public sealed class AgentLifecycleDecision
{
    private AgentLifecycleDecision(
        bool continueExecution,
        string? reasonCode,
        string? safeMessage)
    {
        ContinueExecution = continueExecution;
        ReasonCode = reasonCode;
        SafeMessage = safeMessage;
    }

    public bool ContinueExecution { get; }

    public string? ReasonCode { get; }

    public string? SafeMessage { get; }

    public static AgentLifecycleDecision Continue { get; } =
        new(true, null, null);

    public static AgentLifecycleDecision Reject(
        string reasonCode,
        string? safeMessage = null)
    {
        reasonCode = RuntimeGuard.RequiredUtf8(
            reasonCode,
            128,
            nameof(reasonCode));
        safeMessage = string.IsNullOrWhiteSpace(safeMessage)
            ? null
            : RuntimeGuard.RequiredUtf8(
                safeMessage,
                1_024,
                nameof(safeMessage));
        return new AgentLifecycleDecision(
            false,
            reasonCode,
            safeMessage);
    }
}

public interface IAgentLifecycleMiddleware
{
    string MiddlewareId { get; }

    string Version { get; }

    ValueTask<AgentLifecycleDecision> HandleAsync(
        AgentLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken);
}

public sealed class AgentLifecycleMiddlewareRegistration
{
    public AgentLifecycleMiddlewareRegistration(
        IAgentLifecycleMiddleware middleware,
        bool required = true)
    {
        Middleware = middleware
                     ?? throw new ArgumentNullException(nameof(middleware));
        Required = required;
    }

    public IAgentLifecycleMiddleware Middleware { get; }

    public bool Required { get; }
}

public sealed class AgentLifecyclePipelineOptions
{
    public int MaxMiddlewares { get; set; } = 16;

    public int MaxConcurrentCalls { get; set; } = 32;

    public TimeSpan MiddlewareTimeout { get; set; } =
        TimeSpan.FromSeconds(2);

    /// <summary>
    /// Bounds the complete ordered middleware chain for one lifecycle event.
    /// </summary>
    public TimeSpan PipelineTimeout { get; set; } =
        TimeSpan.FromSeconds(5);

    public TimeSpan ShutdownTimeout { get; set; } =
        TimeSpan.FromSeconds(5);

    internal AgentLifecyclePipelineOptions Snapshot()
    {
        if (MaxMiddlewares is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMiddlewares));
        }

        if (MaxConcurrentCalls is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentCalls));
        }

        if (MiddlewareTimeout < TimeSpan.FromMilliseconds(1)
            || MiddlewareTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MiddlewareTimeout));
        }

        if (PipelineTimeout < TimeSpan.FromMilliseconds(1)
            || PipelineTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(PipelineTimeout));
        }

        if (ShutdownTimeout < TimeSpan.FromMilliseconds(1)
            || ShutdownTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        }

        return new AgentLifecyclePipelineOptions
        {
            MaxMiddlewares = MaxMiddlewares,
            MaxConcurrentCalls = MaxConcurrentCalls,
            MiddlewareTimeout = MiddlewareTimeout,
            PipelineTimeout = PipelineTimeout,
            ShutdownTimeout = ShutdownTimeout
        };
    }
}

public sealed class AgentLifecycleRejectedException : InvalidOperationException
{
    internal AgentLifecycleRejectedException(
        string middlewareId,
        string reasonCode,
        string? safeMessage)
        : base(safeMessage ?? "Agent lifecycle middleware rejected execution.")
    {
        MiddlewareId = middlewareId;
        ReasonCode = reasonCode;
        SafeMessage = safeMessage;
    }

    public string MiddlewareId { get; }

    public string ReasonCode { get; }

    public string? SafeMessage { get; }
}

public sealed class AgentLifecycleMiddlewareException : InvalidOperationException
{
    internal AgentLifecycleMiddlewareException(
        string middlewareId,
        string reasonCode,
        Exception? innerException = null)
        : base(
            "Required agent lifecycle middleware failed: " + reasonCode,
            innerException)
    {
        MiddlewareId = middlewareId;
        ReasonCode = reasonCode;
    }

    public string MiddlewareId { get; }

    public string ReasonCode { get; }
}

public sealed class AgentLifecyclePipeline : IDisposable
{
    private readonly IReadOnlyList<RegisteredMiddleware>
        _registrations;
    private readonly AgentLifecyclePipelineOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, Task> _detached = new();
    private readonly object _lifecycleSync = new();
    private TaskCompletionSource<bool>? _idle;
    private Task? _drainTask;
    private long _nextDetachedId;
    private int _activeInvocations;
    private int _closed;
    private int _resourcesDisposed;

    public AgentLifecyclePipeline(
        IEnumerable<AgentLifecycleMiddlewareRegistration>? registrations,
        AgentLifecyclePipelineOptions? options = null)
    {
        _options = (options ?? new AgentLifecyclePipelineOptions()).Snapshot();
        var snapshot = new List<RegisteredMiddleware>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations
                 ?? Array.Empty<AgentLifecycleMiddlewareRegistration>())
        {
            if (registration is null
                || snapshot.Count >= _options.MaxMiddlewares)
            {
                throw new ArgumentException(
                    "The lifecycle middleware list is invalid.",
                    nameof(registrations));
            }

            string id;
            string version;
            try
            {
                id = RuntimeGuard.RequiredUtf8(
                    registration.Middleware.MiddlewareId,
                    128,
                    nameof(registrations));
                version = RuntimeGuard.RequiredUtf8(
                    registration.Middleware.Version,
                    64,
                    nameof(registrations));
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException
                      and not StackOverflowException)
            {
                throw new ArgumentException(
                    "The lifecycle middleware identity is invalid.",
                    nameof(registrations),
                    exception);
            }

            if (!ids.Add(id))
            {
                throw new ArgumentException(
                    "Lifecycle middleware ids must be unique.",
                    nameof(registrations));
            }

            snapshot.Add(
                new RegisteredMiddleware(
                    registration.Middleware,
                    id,
                    version,
                    registration.Required));
        }

        _registrations =
            new ReadOnlyCollection<RegisteredMiddleware>(
                snapshot);
        _slots = new SemaphoreSlim(
            _options.MaxConcurrentCalls,
            _options.MaxConcurrentCalls);
    }

    public int Count => _registrations.Count;

    public int DetachedCallCount => _detached.Count;

    internal async ValueTask InvokeAsync(
        AgentLifecycleEvent lifecycleEvent,
        bool allowRejection,
        CancellationToken cancellationToken,
        bool enforceRequired = true)
    {
        if (lifecycleEvent is null)
        {
            throw new ArgumentNullException(nameof(lifecycleEvent));
        }

        EnterInvocation();
        try
        {
            var pipelineStarted = Stopwatch.StartNew();
            foreach (var registration in _registrations)
            {
                var remaining = _options.PipelineTimeout
                                - pipelineStarted.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    if (registration.Required && enforceRequired)
                    {
                        throw new AgentLifecycleMiddlewareException(
                            registration.MiddlewareId,
                            "middleware_pipeline_timeout");
                    }

                    continue;
                }

                AgentLifecycleDecision result;
                try
                {
                    result = await InvokeOneAsync(
                            registration,
                            lifecycleEvent,
                            cancellationToken,
                            remaining)
                        .ConfigureAwait(false);
                }
                catch (AgentLifecycleMiddlewareException)
                    when (!enforceRequired)
                {
                    continue;
                }
                if (!result.ContinueExecution)
                {
                    if (!allowRejection)
                    {
                        if (registration.Required && enforceRequired)
                        {
                            throw new AgentLifecycleMiddlewareException(
                                registration.MiddlewareId,
                                "invalid_after_event_rejection");
                        }

                        continue;
                    }

                    throw new AgentLifecycleRejectedException(
                        registration.MiddlewareId,
                        result.ReasonCode ?? "middleware_rejected",
                        result.SafeMessage);
                }
            }
        }
        finally
        {
            ExitInvocation();
        }
    }

    public ValueTask<bool> StopAsync()
    {
        Task drain;
        lock (_lifecycleSync)
        {
            if (_drainTask is null)
            {
                Volatile.Write(ref _closed, 1);
                _drainTask = BeginDrainLocked();
                _ = DisposeWhenDrainedAsync(_drainTask);
            }

            drain = _drainTask;
        }

        return new ValueTask<bool>(WaitForDrainAsync(drain));
    }

    private Task BeginDrainLocked()
    {
        var cancellation = Task.Run(
            () =>
            {
                try
                {
                    _shutdown.Cancel();
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException
                          and not StackOverflowException)
                {
                    // Extension cancellation callbacks are untrusted. The
                    // drain below still protects resource lifetime.
                }
            });
        var idle = _activeInvocations == 0 && _detached.IsEmpty
            ? Task.CompletedTask
            : (_idle ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        return Task.WhenAll(cancellation, idle);
    }

    private async Task<bool> WaitForDrainAsync(Task drain)
    {
        var completed = await Task.WhenAny(
                drain,
                Task.Delay(_options.ShutdownTimeout))
            .ConfigureAwait(false);
        var drained = ReferenceEquals(completed, drain);
        if (drained)
        {
            await drain.ConfigureAwait(false);
            DisposeResources();
        }

        return drained;
    }

    private async Task DisposeWhenDrainedAsync(Task drain)
    {
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            DisposeResources();
        }
    }

    public void Dispose()
    {
        _ = StopAsync().AsTask().ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task<AgentLifecycleDecision> InvokeOneAsync(
        RegisteredMiddleware registration,
        AgentLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken,
        TimeSpan remainingPipelineTime)
    {
        var pipelineLimited = remainingPipelineTime
                              <= _options.MiddlewareTimeout;
        var callTimeout = pipelineLimited
            ? remainingPipelineTime
            : _options.MiddlewareTimeout;
        var callStarted = Stopwatch.StartNew();
        bool entered;
        try
        {
            entered = await _slots.WaitAsync(
                    callTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (!entered)
        {
            return registration.Required
                ? throw new AgentLifecycleMiddlewareException(
                    registration.MiddlewareId,
                    pipelineLimited
                        ? "middleware_pipeline_timeout"
                        : "middleware_capacity_timeout")
                : AgentLifecycleDecision.Continue;
        }

        var operationTimeout = callTimeout - callStarted.Elapsed;
        if (operationTimeout <= TimeSpan.Zero)
        {
            _slots.Release();
            return registration.Required
                ? throw new AgentLifecycleMiddlewareException(
                    registration.MiddlewareId,
                    pipelineLimited
                        ? "middleware_pipeline_timeout"
                        : "middleware_timeout")
                : AgentLifecycleDecision.Continue;
        }

        var cancellation = IsolatedCancellationLease.Create(
            BoundedCancellationDispatcher.AgentLifecycleShared);
        if (cancellationToken.IsCancellationRequested
            || _shutdown.IsCancellationRequested)
        {
            await cancellation.DisposeAsync().ConfigureAwait(false);
            _slots.Release();
            cancellationToken.ThrowIfCancellationRequested();
            _shutdown.Token.ThrowIfCancellationRequested();
        }

        Task<AgentLifecycleDecision> operation;
        try
        {
            var middlewareToken = cancellation.Token;
            operation = Task.Run(
                async () => await registration.Middleware
                    .HandleAsync(lifecycleEvent, middlewareToken)
                    .ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            await cancellation.DisposeAsync().ConfigureAwait(false);
            _slots.Release();
            return registration.Required
                ? throw new AgentLifecycleMiddlewareException(
                    registration.MiddlewareId,
                    "middleware_start_failed",
                    exception)
                : AgentLifecycleDecision.Continue;
        }

        using var signals = new OperationDeadlineSignals(
            operationTimeout,
            cancellationToken);
        using var shutdownRegistration = _shutdown.Token.Register(
            () => cancellation.TryCancel());
        var completed = await Task.WhenAny(
                operation,
                signals.Timeout,
                signals.Cancellation)
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, operation))
        {
            _ = cancellation.TryCancel();
            TrackDetached(operation, cancellation);
            cancellationToken.ThrowIfCancellationRequested();
            return registration.Required
                ? throw new AgentLifecycleMiddlewareException(
                    registration.MiddlewareId,
                    pipelineLimited
                        ? "middleware_pipeline_timeout"
                        : "middleware_timeout")
                : AgentLifecycleDecision.Continue;
        }

        cancellation.DisposeDetached();
        _slots.Release();
        try
        {
            return await operation.ConfigureAwait(false)
                   ?? throw new InvalidOperationException(
                       "Lifecycle middleware returned null.");
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            return registration.Required
                ? throw new AgentLifecycleMiddlewareException(
                    registration.MiddlewareId,
                    "middleware_failed",
                    exception)
                : AgentLifecycleDecision.Continue;
        }
    }

    private void TrackDetached(
        Task operation,
        IsolatedCancellationLease cancellation)
    {
        long id;
        TaskCompletionSource<bool> start;
        Task cleanup;
        do
        {
            id = Interlocked.Increment(ref _nextDetachedId);
            start = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cleanup = CompleteDetachedAsync(
                id,
                operation,
                cancellation,
                start.Task);
        }
        while (!_detached.TryAdd(id, cleanup));

        start.TrySetResult(true);
        _ = cleanup.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task CompleteDetachedAsync(
        long id,
        Task operation,
        IsolatedCancellationLease cancellation,
        Task start)
    {
        await start.ConfigureAwait(false);
        try
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch
            {
                // The synchronous path reports required middleware failures.
            }
        }
        finally
        {
            cancellation.DisposeDetached();
            _slots.Release();
            _detached.TryRemove(id, out _);
            PulseIdle();
        }
    }

    private void EnterInvocation()
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(AgentLifecyclePipeline));
            }

            _activeInvocations++;
        }
    }

    private void ExitInvocation()
    {
        lock (_lifecycleSync)
        {
            _activeInvocations--;
            PulseIdleLocked();
        }
    }

    private void PulseIdle()
    {
        lock (_lifecycleSync)
        {
            PulseIdleLocked();
        }
    }

    private void PulseIdleLocked()
    {
        if (_activeInvocations == 0 && _detached.IsEmpty)
        {
            _idle?.TrySetResult(true);
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            _slots.Dispose();
            _shutdown.Dispose();
        }
    }

    private sealed class RegisteredMiddleware
    {
        internal RegisteredMiddleware(
            IAgentLifecycleMiddleware middleware,
            string middlewareId,
            string version,
            bool required)
        {
            Middleware = middleware;
            MiddlewareId = middlewareId;
            Version = version;
            Required = required;
        }

        internal IAgentLifecycleMiddleware Middleware { get; }

        internal string MiddlewareId { get; }

        internal string Version { get; }

        internal bool Required { get; }
    }
}

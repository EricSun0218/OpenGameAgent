using System.Collections.Concurrent;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Workflow;

public static class WorkflowAgentStepKinds
{
    public const string Run = "agent.run";
}

public static class WorkflowAgentReasonCodes
{
    public const string Failed = "workflow_agent_failed";

    public const string Cancelled = "workflow_agent_cancelled";

    public const string Interrupted = "workflow_agent_interrupted";

    public const string BudgetExhausted =
        "workflow_agent_budget_exhausted";

    public const string InvalidRunIdentity =
        "workflow_agent_run_identity_invalid";

    public const string InvalidOutcome =
        "workflow_agent_outcome_invalid";
}

/// <summary>
/// Bounds synchronous game-owned adapter callbacks without changing the
/// adapter interfaces. Timed-out callbacks remain owned until they return.
/// </summary>
public sealed class WorkflowAgentStepExecutorOptions
{
    /// <summary>
    /// Maximum callbacks that may be executing, including detached calls.
    /// </summary>
    public int MaxConcurrentAdapterCalls { get; set; } = 8;

    /// <summary>
    /// Maximum time to wait for admission and then for one callback result.
    /// </summary>
    public TimeSpan AdapterCallTimeout { get; set; } =
        TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum time one stop probe waits for detached callbacks to drain.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } =
        TimeSpan.FromSeconds(5);

    internal WorkflowAgentStepExecutorOptions Snapshot()
    {
        if (MaxConcurrentAdapterCalls is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentAdapterCalls));
        }

        if (AdapterCallTimeout < TimeSpan.FromMilliseconds(1)
            || AdapterCallTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(AdapterCallTimeout));
        }

        if (ShutdownTimeout < TimeSpan.FromMilliseconds(1)
            || ShutdownTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        }

        return new WorkflowAgentStepExecutorOptions
        {
            MaxConcurrentAdapterCalls = MaxConcurrentAdapterCalls,
            AdapterCallTimeout = AdapterCallTimeout,
            ShutdownTimeout = ShutdownTimeout
        };
    }
}

/// <summary>
/// Stable workflow coordinates supplied to a game-owned agent binding. The
/// nested agent run ID is independent of recovery attempt and lease owner.
/// </summary>
public sealed class WorkflowAgentInvocation
{
    internal WorkflowAgentInvocation(
        WorkflowStepContext context,
        string agentRunId)
    {
        WorkflowRunId = context.RunId;
        WorkflowId = context.WorkflowId;
        StageId = context.StageId;
        StageInstanceId = context.InstanceId;
        Attempt = context.Attempt;
        Generation = context.Generation;
        IsRecovery = context.IsRecovery;
        Settings = context.Settings.Clone();
        Checkpoint = context.Checkpoint?.Clone();
        AgentRunId = agentRunId;
    }

    public string WorkflowRunId { get; }

    public string WorkflowId { get; }

    public string StageId { get; }

    public string StageInstanceId { get; }

    public int Attempt { get; }

    public int Generation { get; }

    public bool IsRecovery { get; }

    public JsonElement Settings { get; }

    public JsonElement? Checkpoint { get; }

    public string AgentRunId { get; }
}

/// <summary>
/// Binds workflow JSON to a game-specific durable agent request. Implementors
/// must be deterministic for one workflow stage instance: recovery may invoke
/// these methods in a different process. Implementations should avoid external
/// side effects because a synchronous callback that exceeds its deadline may
/// continue running after the workflow invocation has failed closed.
/// </summary>
public interface IWorkflowAgentRunAdapter
{
    DurableRunRequest CreateRequest(
        WorkflowAgentInvocation invocation,
        JsonElement input);

    DurableRunContinuation? CreateContinuation(
        WorkflowAgentInvocation invocation,
        JsonElement input);

    DurableRunResumeGuard? CreateResumeGuard(
        WorkflowAgentInvocation invocation,
        JsonElement input);

    JsonElement ProjectOutcome(
        WorkflowAgentInvocation invocation,
        JsonElement input,
        DurableRunOutcome outcome);
}

/// <summary>
/// Optional game-owned policy for selected terminal agent outcomes. Returning
/// false preserves the default fail-closed workflow behavior. Returning true
/// converts the terminal outcome into a normal, schema-validated workflow
/// value, which is useful for explicitly optional simulation branches.
/// </summary>
public interface IWorkflowAgentTerminalOutcomeProjector
{
    bool TryProjectTerminalOutcome(
        WorkflowAgentInvocation invocation,
        JsonElement input,
        DurableRunOutcome outcome,
        out JsonElement output);
}

/// <summary>
/// Executes one durable agent run as a recoverable workflow step. The workflow
/// commits Started before dispatch; retries always address the same nested run
/// ID and use resume rather than replaying a completed side effect.
/// </summary>
public sealed class WorkflowAgentStepExecutor :
    IWorkflowStepExecutor,
    IAsyncDisposable
{
    private readonly IDurableAgentRuntime _runtime;
    private readonly IWorkflowAgentRunAdapter _adapter;
    private readonly IGameOperationReconciler? _reconciler;
    private readonly WorkflowAgentStepExecutorOptions _options;
    private readonly SemaphoreSlim _adapterSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, Task> _detachedCalls = new();
    private readonly object _lifecycleSync = new();
    private TaskCompletionSource<bool>? _idle;
    private Task? _shutdownCancellationTask;
    private Task? _eventualDisposeTask;
    private long _nextDetachedCallId;
    private int _activeCalls;
    private int _closed;
    private int _resourcesDisposed;

    public WorkflowAgentStepExecutor(
        IDurableAgentRuntime runtime,
        IWorkflowAgentRunAdapter adapter,
        IGameOperationReconciler? reconciler = null)
        : this(runtime, adapter, reconciler, options: null)
    {
    }

    public WorkflowAgentStepExecutor(
        IDurableAgentRuntime runtime,
        IWorkflowAgentRunAdapter adapter,
        IGameOperationReconciler? reconciler,
        WorkflowAgentStepExecutorOptions? options)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _reconciler = reconciler;
        _options = (options ?? new WorkflowAgentStepExecutorOptions())
            .Snapshot();
        _adapterSlots = new SemaphoreSlim(
            _options.MaxConcurrentAdapterCalls,
            _options.MaxConcurrentAdapterCalls);
    }

    public string Kind => WorkflowAgentStepKinds.Run;

    /// <summary>
    /// Number of timed-out or cancelled adapter callbacks still executing.
    /// </summary>
    public int DetachedAdapterCallCount => _detachedCalls.Count;

    public async ValueTask<WorkflowStepResult> ExecuteAsync(
        WorkflowStepContext context,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var invocation = CreateInvocation(context);
        DurableRunRequest request;
        try
        {
            request = await RequireRequestAsync(
                    invocation,
                    input,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowAgentBindingException exception)
        {
            return WorkflowStepResult.Failed(exception.ReasonCode);
        }

        try
        {
            var outcome = await _runtime
                .RunAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return await ProjectAsync(
                    invocation,
                    input,
                    outcome,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DuplicateRunException)
        {
            return await ResumeAsync(
                    invocation,
                    input,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<WorkflowStepResult> RecoverAsync(
        WorkflowStepContext context,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var invocation = CreateInvocation(context);
        try
        {
            return await ResumeAsync(
                    invocation,
                    input,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowAgentRunNotFoundException)
        {
            DurableRunRequest request;
            try
            {
                request = await RequireRequestAsync(
                        invocation,
                        input,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorkflowAgentBindingException exception)
            {
                return WorkflowStepResult.Failed(exception.ReasonCode);
            }

            try
            {
                var outcome = await _runtime
                    .RunAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return await ProjectAsync(
                        invocation,
                        input,
                        outcome,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DuplicateRunException)
            {
                throw Interrupted();
            }
        }
    }

    public static string CreateAgentRunId(
        string workflowRunId,
        string stageInstanceId)
    {
        var identity = WorkflowJson.CreateElement(writer =>
        {
            writer.WriteStartArray();
            writer.WriteStringValue(
                "gameagent.workflow.agent-run-identity.v1");
            writer.WriteStringValue(
                WorkflowValidation.RequiredIdentifier(
                    workflowRunId,
                    nameof(workflowRunId),
                    256,
                    allowSlash: true));
            writer.WriteStringValue(
                WorkflowValidation.RequiredIdentifier(
                    stageInstanceId,
                    nameof(stageInstanceId),
                    256,
                    allowSlash: true));
            writer.WriteEndArray();
        });
        return "wfa_" + WorkflowIdentity.ComputeJsonDigest(identity);
    }

    private WorkflowAgentInvocation CreateInvocation(
        WorkflowStepContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return new WorkflowAgentInvocation(
            context,
            CreateAgentRunId(context.RunId, context.InstanceId));
    }

    private async ValueTask<DurableRunRequest> RequireRequestAsync(
        WorkflowAgentInvocation invocation,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var inputSnapshot = input.Clone();
        var request = await InvokeAdapterAsync(
                () => _adapter.CreateRequest(
                    invocation,
                    inputSnapshot),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The workflow agent adapter returned no request.");
        if (request.Run is null
            || !string.Equals(
                request.Run.RunId,
                invocation.AgentRunId,
                StringComparison.Ordinal))
        {
            throw new WorkflowAgentBindingException(
                WorkflowAgentReasonCodes.InvalidRunIdentity);
        }

        return request;
    }

    private async ValueTask<WorkflowStepResult> ResumeAsync(
        WorkflowAgentInvocation invocation,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        try
        {
            var continuationInput = input.Clone();
            var continuation = await InvokeAdapterAsync(
                    () => _adapter.CreateContinuation(
                        invocation,
                        continuationInput),
                    cancellationToken)
                .ConfigureAwait(false);
            var guardInput = input.Clone();
            var guard = await InvokeAdapterAsync(
                    () => _adapter.CreateResumeGuard(
                        invocation,
                        guardInput),
                    cancellationToken)
                .ConfigureAwait(false);
            var outcome = await _runtime
                .ResumeAsync(
                    invocation.AgentRunId,
                    continuation,
                    _reconciler,
                    cancellationToken,
                    guard)
                .ConfigureAwait(false);
            return await ProjectAsync(
                    invocation,
                    input,
                    outcome,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            throw new WorkflowAgentRunNotFoundException(exception);
        }
        catch (DuplicateRunException)
        {
            throw Interrupted();
        }
    }

    private async ValueTask<WorkflowStepResult> ProjectAsync(
        WorkflowAgentInvocation invocation,
        JsonElement input,
        DurableRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome is null
            || outcome.Run is null
            || !string.Equals(
                outcome.Run.RunId,
                invocation.AgentRunId,
                StringComparison.Ordinal))
        {
            return WorkflowStepResult.Failed(
                WorkflowAgentReasonCodes.InvalidOutcome);
        }

        if (outcome.ReconciliationRequired || !outcome.IsTerminal)
        {
            throw Interrupted();
        }

        if (string.Equals(
                outcome.Run.State,
                RunStates.Completed,
                StringComparison.Ordinal))
        {
            var inputSnapshot = input.Clone();
            var output = await InvokeAdapterAsync(
                    () => _adapter.ProjectOutcome(
                        invocation,
                        inputSnapshot,
                        outcome),
                    cancellationToken)
                .ConfigureAwait(false);
            return output.ValueKind == JsonValueKind.Undefined
                ? WorkflowStepResult.Failed(
                    WorkflowAgentReasonCodes.InvalidOutcome)
                : WorkflowStepResult.Completed(output);
        }

        var failureReason = outcome.Run.State switch
        {
            RunStates.Cancelled => WorkflowAgentReasonCodes.Cancelled,
            RunStates.Interrupted => WorkflowAgentReasonCodes.Interrupted,
            RunStates.BudgetExhausted =>
                WorkflowAgentReasonCodes.BudgetExhausted,
            RunStates.Failed => WorkflowAgentReasonCodes.Failed,
            _ => WorkflowAgentReasonCodes.InvalidOutcome
        };
        if (!string.Equals(
                failureReason,
                WorkflowAgentReasonCodes.InvalidOutcome,
                StringComparison.Ordinal)
            && _adapter is IWorkflowAgentTerminalOutcomeProjector projector)
        {
            var inputSnapshot = input.Clone();
            var projection = await InvokeAdapterAsync(
                    () => TerminalProjection.Invoke(
                        projector,
                        invocation,
                        inputSnapshot,
                        outcome),
                    cancellationToken)
                .ConfigureAwait(false);
            if (projection.Handled)
            {
                return projection.Output.ValueKind == JsonValueKind.Undefined
                    ? WorkflowStepResult.Failed(
                        WorkflowAgentReasonCodes.InvalidOutcome)
                    : WorkflowStepResult.Completed(projection.Output);
            }
        }

        return WorkflowStepResult.Failed(failureReason);
    }

    public async ValueTask<bool> StopAsync()
    {
        Task cancellation;
        Task drain;
        lock (_lifecycleSync)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _shutdownCancellationTask = Task.Run(
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
                        }
                    });
            }

            cancellation = _shutdownCancellationTask
                           ?? Task.CompletedTask;
            drain = _activeCalls == 0
                ? Task.CompletedTask
                : (_idle ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        var all = Task.WhenAll(cancellation, drain);
        var completed = await Task.WhenAny(
                all,
                Task.Delay(_options.ShutdownTimeout))
            .ConfigureAwait(false);
        var drained = ReferenceEquals(completed, all);
        if (drained)
        {
            await all.ConfigureAwait(false);
            DisposeResources();
        }
        else
        {
            EnsureEventualDispose(all);
        }

        return drained;
    }

    public async ValueTask DisposeAsync()
    {
        _ = await StopAsync().ConfigureAwait(false);
        Task cancellation;
        Task drain;
        lock (_lifecycleSync)
        {
            cancellation = _shutdownCancellationTask
                           ?? Task.CompletedTask;
            drain = _activeCalls == 0
                ? Task.CompletedTask
                : (_idle ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await Task.WhenAll(cancellation, drain).ConfigureAwait(false);
        DisposeResources();
    }

    private async ValueTask<T> InvokeAdapterAsync<T>(
        Func<T> invocation,
        CancellationToken cancellationToken)
    {
        if (invocation is null)
        {
            throw new ArgumentNullException(nameof(invocation));
        }

        EnterAdapterCall();
        var enteredSlot = false;
        var ownershipTransferred = false;
        try
        {
            using var admission = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            enteredSlot = await _adapterSlots.WaitAsync(
                    _options.AdapterCallTimeout,
                    admission.Token)
                .ConfigureAwait(false);
            if (!enteredSlot)
            {
                throw AdapterTimeout();
            }

            var operation = Task.Run(invocation);
            admission.CancelAfter(_options.AdapterCallTimeout);
            var deadline = Task.Delay(
                Timeout.InfiniteTimeSpan,
                admission.Token);
            var completed = await Task.WhenAny(
                    operation,
                    deadline)
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, operation))
            {
                return await operation.ConfigureAwait(false);
            }

            TrackDetachedAdapterCall(operation);
            ownershipTransferred = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdown.IsCancellationRequested)
            {
                throw new OperationCanceledException(_shutdown.Token);
            }

            throw AdapterTimeout();
        }
        finally
        {
            if (!ownershipTransferred)
            {
                if (enteredSlot)
                {
                    _adapterSlots.Release();
                }

                ExitAdapterCall();
            }
        }
    }

    private void TrackDetachedAdapterCall(Task operation)
    {
        var id = Interlocked.Increment(ref _nextDetachedCallId);
        if (!_detachedCalls.TryAdd(id, operation))
        {
            throw new InvalidOperationException(
                "Unable to track a detached workflow adapter call.");
        }

        _ = ObserveDetachedAdapterCallAsync(id, operation);
    }

    private async Task ObserveDetachedAdapterCallAsync(
        long id,
        Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
        }
        finally
        {
            _detachedCalls.TryRemove(id, out _);
            _adapterSlots.Release();
            ExitAdapterCall();
        }
    }

    private void EnterAdapterCall()
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(WorkflowAgentStepExecutor));
            }

            _activeCalls++;
        }
    }

    private void ExitAdapterCall()
    {
        TaskCompletionSource<bool>? idle = null;
        lock (_lifecycleSync)
        {
            _activeCalls--;
            if (_activeCalls == 0)
            {
                idle = _idle;
                _idle = null;
            }
        }

        idle?.TrySetResult(true);
    }

    private void EnsureEventualDispose(Task drain)
    {
        lock (_lifecycleSync)
        {
            _eventualDisposeTask ??= DisposeWhenDrainedAsync(drain);
        }
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

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _adapterSlots.Dispose();
        _shutdown.Dispose();
    }

    private static TimeoutException AdapterTimeout() =>
        new("The workflow agent adapter call exceeded its bounded deadline.");

    private sealed class TerminalProjection
    {
        private TerminalProjection(bool handled, JsonElement output)
        {
            Handled = handled;
            Output = output;
        }

        public bool Handled { get; }

        public JsonElement Output { get; }

        public static TerminalProjection Invoke(
            IWorkflowAgentTerminalOutcomeProjector projector,
            WorkflowAgentInvocation invocation,
            JsonElement input,
            DurableRunOutcome outcome)
        {
            var handled = projector.TryProjectTerminalOutcome(
                invocation,
                input,
                outcome,
                out var output);
            return new TerminalProjection(handled, output);
        }
    }

    private static WorkflowExecutorInterruptedException Interrupted()
    {
        return new WorkflowExecutorInterruptedException(
            "The durable agent run is still owned, nonterminal, or awaiting reconciliation.");
    }

    private sealed class WorkflowAgentRunNotFoundException : Exception
    {
        public WorkflowAgentRunNotFoundException(Exception innerException)
            : base("The durable agent run does not exist.", innerException)
        {
        }
    }
}

public sealed class WorkflowAgentBindingException : InvalidOperationException
{
    public WorkflowAgentBindingException(string reasonCode)
        : base("The workflow agent adapter produced an invalid binding.")
    {
        ReasonCode = WorkflowValidation.RequiredIdentifier(
            reasonCode,
            nameof(reasonCode),
            128,
            allowSlash: false);
    }

    public string ReasonCode { get; }
}

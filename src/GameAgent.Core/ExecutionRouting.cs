using System.Text.Json;

namespace GameAgent.Core;

public enum ExecutionPath
{
    Direct = 0,
    Agent = 1,
    Workflow = 2
}

[Flags]
public enum ExecutionRequirements
{
    None = 0,
    Tools = 1 << 0,
    Skills = 1 << 1,
    DurableEffects = 1 << 2,
    MultipleModelTurns = 1 << 3,
    Workflow = 1 << 4,
    ParallelActors = 1 << 5
}

public sealed class ExecutionRouteRequest
{
    public string OperationKind { get; set; } = "game-operation";

    public ExecutionPath? ExplicitPath { get; set; }

    public ExecutionRequirements Requirements { get; set; }

    /// <summary>
    /// Optional bounded structured routing signal. It is supplied to custom
    /// policies as data and is never interpreted by the deterministic policy.
    /// </summary>
    public JsonElement? Signal { get; set; }
}

public static class ExecutionRouteReasonCodes
{
    public const string Explicit = "explicit_path";
    public const string WorkflowRequired = "workflow_required";
    public const string AgentCapabilitiesRequired =
        "agent_capabilities_required";
    public const string DirectSufficient = "direct_sufficient";
    public const string PolicyTimeoutFallback = "policy_timeout_fallback";
    public const string PolicyErrorFallback = "policy_error_fallback";
    public const string PolicyResultInvalidFallback =
        "policy_result_invalid_fallback";
}

public sealed class ExecutionRouteDecision
{
    public ExecutionRouteDecision(
        ExecutionPath path,
        string reasonCode,
        string policyId,
        string policyVersion)
    {
        Path = path;
        ReasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
        PolicyId = RuntimeGuard.RequiredUtf8(
            policyId,
            128,
            nameof(policyId));
        PolicyVersion = RuntimeGuard.RequiredUtf8(
            policyVersion,
            64,
            nameof(policyVersion));
    }

    public ExecutionPath Path { get; }

    public string ReasonCode { get; }

    public string PolicyId { get; }

    public string PolicyVersion { get; }
}

public interface IExecutionRoutePolicy
{
    string PolicyId { get; }

    string Version { get; }

    ValueTask<ExecutionRouteDecision> SelectAsync(
        ExecutionRouteRequest request,
        CancellationToken cancellationToken);
}

public sealed class DeterministicExecutionRoutePolicy : IExecutionRoutePolicy
{
    public string PolicyId => "deterministic-capability-router";

    public string Version => "1.0.0";

    public ValueTask<ExecutionRouteDecision> SelectAsync(
        ExecutionRouteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ExecutionRouteValidation.Snapshot(request);
        if (snapshot.ExplicitPath.HasValue)
        {
            EnsureExplicitPathCanSatisfy(
                snapshot.ExplicitPath.Value,
                snapshot.Requirements);
            return new ValueTask<ExecutionRouteDecision>(
                Decision(
                    snapshot.ExplicitPath.Value,
                    ExecutionRouteReasonCodes.Explicit));
        }

        if ((snapshot.Requirements
             & ExecutionRouteValidation.WorkflowRequirements) != 0)
        {
            return new ValueTask<ExecutionRouteDecision>(
                Decision(
                    ExecutionPath.Workflow,
                    ExecutionRouteReasonCodes.WorkflowRequired));
        }

        if ((snapshot.Requirements
             & ExecutionRouteValidation.AgentRequirements) != 0)
        {
            return new ValueTask<ExecutionRouteDecision>(
                Decision(
                    ExecutionPath.Agent,
                    ExecutionRouteReasonCodes.AgentCapabilitiesRequired));
        }

        return new ValueTask<ExecutionRouteDecision>(
            Decision(
                ExecutionPath.Direct,
                ExecutionRouteReasonCodes.DirectSufficient));
    }

    private ExecutionRouteDecision Decision(
        ExecutionPath path,
        string reasonCode) =>
        new(path, reasonCode, PolicyId, Version);

    private static void EnsureExplicitPathCanSatisfy(
        ExecutionPath path,
        ExecutionRequirements requirements)
    {
        if (!ExecutionRouteValidation.CanSatisfy(path, requirements))
        {
            throw new ArgumentException(
                "The explicit path cannot satisfy the requested capabilities.",
                nameof(ExecutionRouteRequest.ExplicitPath));
        }
    }
}

public sealed class ExecutionRouterOptions
{
    public TimeSpan PolicyTimeout { get; set; } =
        TimeSpan.FromSeconds(2);

    public int MaxConcurrentPolicyCalls { get; set; } = 4;

    public TimeSpan ShutdownTimeout { get; set; } =
        TimeSpan.FromSeconds(5);

    internal ExecutionRouterOptions Snapshot()
    {
        if (PolicyTimeout < TimeSpan.FromMilliseconds(10)
            || PolicyTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(PolicyTimeout));
        }

        if (MaxConcurrentPolicyCalls is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentPolicyCalls));
        }

        if (ShutdownTimeout < TimeSpan.FromMilliseconds(10)
            || ShutdownTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        }

        return new ExecutionRouterOptions
        {
            PolicyTimeout = PolicyTimeout,
            MaxConcurrentPolicyCalls = MaxConcurrentPolicyCalls,
            ShutdownTimeout = ShutdownTimeout
        };
    }
}

public sealed class RoutedWorkflowRequest
{
    public string WorkflowId { get; set; } = string.Empty;

    public string RunKey { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public JsonElement Input { get; set; }
}

public sealed class RoutedWorkflowOutcome
{
    public string RunId { get; set; } = string.Empty;

    public string WorkflowId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? ReasonCode { get; set; }

    public JsonElement? Output { get; set; }
}

public interface IRoutedWorkflowRuntime
{
    ValueTask<RoutedWorkflowOutcome> RunAsync(
        RoutedWorkflowRequest request,
        CancellationToken cancellationToken);
}

public sealed class RoutedExecutionRequest
{
    public ExecutionRouteRequest Route { get; set; } = new();

    public DurableRunRequest? Run { get; set; }

    public RoutedWorkflowRequest? Workflow { get; set; }
}

public sealed class RoutedExecutionOutcome
{
    public ExecutionRouteDecision Decision { get; set; } = null!;

    public DurableRunOutcome? Run { get; set; }

    public RoutedWorkflowOutcome? Workflow { get; set; }
}

/// <summary>
/// Selects and executes a durable direct turn, a full agent loop, or a
/// configured workflow. Route-policy failure is bounded and fails safe to the
/// least permissive path that still satisfies the caller's immutable
/// requirements; it never silently chooses a cheaper path.
/// </summary>
public sealed class RoutedExecutionRuntime : IAsyncDisposable
{
    private readonly IDurableAgentRuntime _agent;
    private readonly IRoutedWorkflowRuntime? _workflow;
    private readonly IExecutionRoutePolicy _policy;
    private readonly ExecutionRouterOptions _options;
    private readonly SemaphoreSlim _policySlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly BoundedCancellationDispatcher _shutdownDispatcher;
    private readonly object _lifecycleSync = new();
    private readonly string _policyId;
    private readonly string _policyVersion;
    private TaskCompletionSource<bool>? _idle;
    private Task? _shutdownCancellationTask;
    private Task? _shutdownAdmissionTask;
    private Task? _disposeTask;
    private CancellationTokenSource? _shutdownAdmissionCancellation;
    private int _activeOperations;
    private int _closed;
    private int _resourcesDisposed;

    public RoutedExecutionRuntime(
        IDurableAgentRuntime agent,
        IRoutedWorkflowRuntime? workflow = null,
        IExecutionRoutePolicy? policy = null,
        ExecutionRouterOptions? options = null)
        : this(
            agent,
            workflow,
            policy,
            options,
            BoundedCancellationDispatcher.LifecycleShared)
    {
    }

    internal RoutedExecutionRuntime(
        IDurableAgentRuntime agent,
        IRoutedWorkflowRuntime? workflow,
        IExecutionRoutePolicy? policy,
        ExecutionRouterOptions? options,
        BoundedCancellationDispatcher shutdownDispatcher)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _workflow = workflow;
        _policy = policy ?? new DeterministicExecutionRoutePolicy();
        _options = (options ?? new ExecutionRouterOptions()).Snapshot();
        _shutdownDispatcher = shutdownDispatcher
                              ?? throw new ArgumentNullException(
                                  nameof(shutdownDispatcher));
        try
        {
            _policyId = RuntimeGuard.RequiredUtf8(
                _policy.PolicyId,
                128,
                nameof(policy));
            _policyVersion = RuntimeGuard.RequiredUtf8(
                _policy.Version,
                64,
                nameof(policy));
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new ArgumentException(
                "The execution route policy identity is invalid.",
                nameof(policy),
                exception);
        }

        _policySlots = new SemaphoreSlim(
            _options.MaxConcurrentPolicyCalls,
            _options.MaxConcurrentPolicyCalls);
    }

    public async ValueTask<RoutedExecutionOutcome> RunAsync(
        RoutedExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var route = ExecutionRouteValidation.Snapshot(request.Route);
        var runRequest = request.Run is null
            ? null
            : DurableRunRequestSnapshotter.Snapshot(
                request.Run,
                cancellationToken);
        var workflowRequest = request.Workflow is null
            ? null
            : SnapshotWorkflowRequest(request.Workflow);
        using var active = EnterOperation(cancellationToken);
        var decision = await SelectBoundedAsync(route, active.Token)
            .ConfigureAwait(false);
        switch (decision.Path)
        {
            case ExecutionPath.Direct:
            case ExecutionPath.Agent:
                {
                    var run = runRequest
                              ?? throw new ArgumentException(
                                  "The selected path requires a durable run request.",
                                  nameof(request));
                    if (workflowRequest is not null)
                    {
                        throw new ArgumentException(
                            "A non-workflow route cannot include a workflow request.",
                            nameof(request));
                    }

                    var routed = CopyRunRequest(
                        run,
                        decision.Path == ExecutionPath.Direct
                            ? DurableExecutionModes.Direct
                            : DurableExecutionModes.Agent);
                    var outcome = await _agent
                        .RunAsync(routed, active.Token)
                        .ConfigureAwait(false);
                    return new RoutedExecutionOutcome
                    {
                        Decision = decision,
                        Run = outcome
                    };
                }
            case ExecutionPath.Workflow:
                {
                    if (runRequest is not null)
                    {
                        throw new ArgumentException(
                            "A workflow route cannot include a durable run request.",
                            nameof(request));
                    }

                    workflowRequest = workflowRequest
                                      ?? throw new ArgumentException(
                                          "The selected path requires a workflow request.",
                                          nameof(request));
                    var workflow = _workflow
                                   ?? throw new InvalidOperationException(
                                       "No routed workflow runtime is configured.");
                    var outcome = await workflow
                        .RunAsync(workflowRequest, active.Token)
                        .ConfigureAwait(false);
                    return new RoutedExecutionOutcome
                    {
                        Decision = decision,
                        Workflow = outcome
                    };
                }
            default:
                throw new InvalidDataException(
                    "The execution route policy returned an unknown path.");
        }
    }

    private async ValueTask<ExecutionRouteDecision> SelectBoundedAsync(
        ExecutionRouteRequest request,
        CancellationToken cancellationToken)
    {
        var validationRequest = ExecutionRouteValidation.Snapshot(request);
        var policyRequest = ExecutionRouteValidation.Snapshot(
            validationRequest);
        using var queueTimeout = new CancellationTokenSource(
            _options.PolicyTimeout);
        using var queue = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            queueTimeout.Token);
        try
        {
            await _policySlots.WaitAsync(queue.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return Fallback(
                ExecutionRouteReasonCodes.PolicyTimeoutFallback,
                validationRequest);
        }

        Task<ExecutionRouteDecision> evaluation;
        var policyDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        policyDeadline.CancelAfter(_options.PolicyTimeout);
        var nestedEntered = false;
        try
        {
            EnterNestedOperation();
            nestedEntered = true;
            evaluation = Task.Run(
                async () => await _policy
                    .SelectAsync(policyRequest, policyDeadline.Token)
                    .ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (nestedEntered)
            {
                ExitOperation();
            }
            policyDeadline.Dispose();
            _policySlots.Release();
            return Fallback(
                ExecutionRouteReasonCodes.PolicyErrorFallback,
                validationRequest);
        }

        var timeout = Task.Delay(_options.PolicyTimeout);
        var callerCancellation = Task.Delay(
            Timeout.InfiniteTimeSpan,
            cancellationToken);
        var completed = await Task.WhenAny(
                evaluation,
                timeout,
                callerCancellation)
            .ConfigureAwait(false);
        if (ReferenceEquals(completed, callerCancellation))
        {
            _ = ReleasePolicySlotWhenSettledAsync(
                evaluation,
                policyDeadline);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!ReferenceEquals(completed, evaluation))
        {
            _ = ReleasePolicySlotWhenSettledAsync(
                evaluation,
                policyDeadline);
            cancellationToken.ThrowIfCancellationRequested();
            return Fallback(
                ExecutionRouteReasonCodes.PolicyTimeoutFallback,
                validationRequest);
        }

        try
        {
            var decision = await evaluation.ConfigureAwait(false);
            return ValidateDecision(decision, validationRequest);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Fallback(
                ExecutionRouteReasonCodes.PolicyTimeoutFallback,
                validationRequest);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Fallback(
                ExecutionRouteReasonCodes.PolicyErrorFallback,
                validationRequest);
        }
        finally
        {
            policyDeadline.Dispose();
            _policySlots.Release();
            ExitOperation();
        }
    }

    private async Task ReleasePolicySlotWhenSettledAsync(
        Task<ExecutionRouteDecision> evaluation,
        CancellationTokenSource policyDeadline)
    {
        try
        {
            await evaluation.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            policyDeadline.Dispose();
            _policySlots.Release();
            ExitOperation();
        }
    }

    private ExecutionRouteDecision ValidateDecision(
        ExecutionRouteDecision? decision,
        ExecutionRouteRequest request)
    {
        if (decision is null || !Enum.IsDefined(typeof(ExecutionPath), decision.Path))
        {
            return Fallback(
                ExecutionRouteReasonCodes.PolicyResultInvalidFallback,
                request);
        }

        try
        {
            _ = new ExecutionRouteDecision(
                decision.Path,
                decision.ReasonCode,
                decision.PolicyId,
                decision.PolicyVersion);
            if (!string.Equals(
                    decision.PolicyId,
                    _policyId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    decision.PolicyVersion,
                    _policyVersion,
                    StringComparison.Ordinal))
            {
                return Fallback(
                    ExecutionRouteReasonCodes.PolicyResultInvalidFallback,
                    request);
            }

            if (request.ExplicitPath.HasValue
                && request.ExplicitPath.Value != decision.Path)
            {
                return Fallback(
                    ExecutionRouteReasonCodes.PolicyResultInvalidFallback,
                    request);
            }

            if (!ExecutionRouteValidation.CanSatisfy(
                    decision.Path,
                    request.Requirements))
            {
                return Fallback(
                    ExecutionRouteReasonCodes.PolicyResultInvalidFallback,
                    request);
            }

            return decision;
        }
        catch (ArgumentException)
        {
            return Fallback(
                ExecutionRouteReasonCodes.PolicyResultInvalidFallback,
                request);
        }
    }

    private ExecutionRouteDecision Fallback(
        string reasonCode,
        ExecutionRouteRequest request) =>
        new(
            request.ExplicitPath
            ?? ExecutionRouteValidation.MinimumPath(request.Requirements),
            reasonCode,
            _policyId,
            _policyVersion);

    public async ValueTask<bool> StopAsync()
    {
        Task cancellation;
        Task drain;
        lock (_lifecycleSync)
        {
            Interlocked.Exchange(ref _closed, 1);
            if (_shutdownCancellationTask is null)
            {
                if (!_shutdownDispatcher.TryReserve(out var reservation))
                {
                    return false;
                }

                try
                {
                    _shutdownCancellationTask =
                        reservation!.DispatchAsync(_shutdown);
                    _ = _shutdownCancellationTask.ContinueWith(
                        _ => reservation.Dispose(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch
                {
                    reservation!.Dispose();
                    throw;
                }
            }

            cancellation = _shutdownCancellationTask
                           ?? Task.CompletedTask;
            drain = _activeOperations == 0
                ? Task.CompletedTask
                : (_idle ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        var all = Task.WhenAll(cancellation, drain);
        var completed = await Task.WhenAny(
                all,
                Task.Delay(_options.ShutdownTimeout))
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, all))
        {
            return false;
        }

        await all.ConfigureAwait(false);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _ = await StopAsync().ConfigureAwait(false);
        Task? admission = null;
        Task initialDrain;
        lock (_lifecycleSync)
        {
            initialDrain = _activeOperations == 0
                ? Task.CompletedTask
                : (_idle ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            if (_shutdownCancellationTask is null
                && !initialDrain.IsCompleted)
            {
                _shutdownAdmissionCancellation ??=
                    new CancellationTokenSource();
                _shutdownAdmissionTask ??=
                    AdmitShutdownCancellationAsync(
                        _shutdownAdmissionCancellation.Token);
                admission = _shutdownAdmissionTask;
            }
        }

        if (admission is not null)
        {
            var admittedOrDrained = await Task.WhenAny(
                    admission,
                    initialDrain)
                .ConfigureAwait(false);
            if (ReferenceEquals(admittedOrDrained, initialDrain))
            {
                _shutdownAdmissionCancellation!.Cancel();
            }

            try
            {
                await admission.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (initialDrain.IsCompleted)
            {
                // Natural settlement made shutdown cancellation unnecessary.
            }
        }

        Task cancellation;
        Task drain;
        lock (_lifecycleSync)
        {
            cancellation = _shutdownCancellationTask
                           ?? Task.CompletedTask;
            drain = _activeOperations == 0
                ? Task.CompletedTask
                : (_idle ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await Task.WhenAll(cancellation, drain).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            _policySlots.Dispose();
            _shutdownAdmissionCancellation?.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task AdmitShutdownCancellationAsync(
        CancellationToken cancellationToken)
    {
        var reservation = await _shutdownDispatcher
            .ReserveAsync(cancellationToken)
            .ConfigureAwait(false);
        lock (_lifecycleSync)
        {
            if (_shutdownCancellationTask is not null)
            {
                reservation.Dispose();
                return;
            }

            try
            {
                _shutdownCancellationTask =
                    reservation.DispatchAsync(_shutdown);
                _ = _shutdownCancellationTask.ContinueWith(
                    _ => reservation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch
            {
                reservation.Dispose();
                throw;
            }
        }
    }

    private ActiveExecution EnterOperation(
        CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(RoutedExecutionRuntime));
            }

            _activeOperations = checked(_activeOperations + 1);
        }

        try
        {
            return new ActiveExecution(
                this,
                cancellationToken,
                _shutdown.Token);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    private void EnterNestedOperation()
    {
        lock (_lifecycleSync)
        {
            _activeOperations = checked(_activeOperations + 1);
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource<bool>? idle = null;
        lock (_lifecycleSync)
        {
            _activeOperations--;
            if (_activeOperations == 0)
            {
                idle = _idle;
                _idle = null;
            }
        }

        idle?.TrySetResult(true);
    }

    private sealed class ActiveExecution : IDisposable
    {
        private RoutedExecutionRuntime? _owner;
        private readonly CancellationTokenSource _linked;

        public ActiveExecution(
            RoutedExecutionRuntime owner,
            CancellationToken caller,
            CancellationToken shutdown)
        {
            _owner = owner;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(
                caller,
                shutdown);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            _linked.Dispose();
            owner.ExitOperation();
        }
    }

    private static DurableRunRequest CopyRunRequest(
        DurableRunRequest source,
        string executionMode)
    {
        return new DurableRunRequest
        {
            Run = source.Run,
            Context = source.Context,
            ActiveSkills = source.ActiveSkills,
            InitialTranscript = source.InitialTranscript,
            LaneId = source.LaneId,
            WorkloadClass = source.WorkloadClass,
            ExecutionMode = executionMode,
            Inference = source.Inference?.CloneValidated(),
            RoutePreference = source.RoutePreference?.CloneValidated(),
            FinalOutputContract = source.FinalOutputContract
        };
    }

    private static RoutedWorkflowRequest SnapshotWorkflowRequest(
        RoutedWorkflowRequest source)
    {
        JsonValueInspector.ValidateAndMeasure(
            source.Input,
            new JsonValueLimits(
                maxUtf8Bytes: 1_048_576,
                maxDepth: 64,
                maxNodes: 65_536,
                maxStringUtf8Bytes: 262_144,
                maxContainerItems: 16_384),
            nameof(source.Input));
        return new RoutedWorkflowRequest
        {
            WorkflowId = RuntimeGuard.RequiredUtf8(
                source.WorkflowId,
                128,
                nameof(source.WorkflowId)),
            RunKey = RuntimeGuard.RequiredUtf8(
                source.RunKey,
                256,
                nameof(source.RunKey)),
            OwnerId = RuntimeGuard.RequiredUtf8(
                source.OwnerId,
                256,
                nameof(source.OwnerId)),
            Input = source.Input.Clone()
        };
    }
}

internal static class ExecutionRouteValidation
{
    internal const ExecutionRequirements AgentRequirements =
        ExecutionRequirements.Tools
        | ExecutionRequirements.Skills
        | ExecutionRequirements.DurableEffects
        | ExecutionRequirements.MultipleModelTurns;

    internal const ExecutionRequirements WorkflowRequirements =
        ExecutionRequirements.Workflow
        | ExecutionRequirements.ParallelActors;

    private const ExecutionRequirements All =
        ExecutionRequirements.Tools
        | ExecutionRequirements.Skills
        | ExecutionRequirements.DurableEffects
        | ExecutionRequirements.MultipleModelTurns
        | ExecutionRequirements.Workflow
        | ExecutionRequirements.ParallelActors;

    internal static ExecutionRouteRequest Snapshot(
        ExecutionRouteRequest? request)
    {
        if (request is null)
        {
            throw new ArgumentException(
                "An execution route request is required.",
                nameof(request));
        }

        if ((request.Requirements & ~All) != 0)
        {
            throw new ArgumentException(
                "The execution requirements contain unknown flags.",
                nameof(request));
        }

        if (request.ExplicitPath.HasValue
            && !Enum.IsDefined(
                typeof(ExecutionPath),
                request.ExplicitPath.Value))
        {
            throw new ArgumentException(
                "The explicit execution path is unsupported.",
                nameof(request));
        }

        if (request.ExplicitPath.HasValue
            && !CanSatisfy(
                request.ExplicitPath.Value,
                request.Requirements))
        {
            throw new ArgumentException(
                "The explicit execution path cannot satisfy the requested capabilities.",
                nameof(request));
        }

        JsonElement? signal = null;
        if (request.Signal.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                request.Signal.Value,
                new JsonValueLimits(
                    maxUtf8Bytes: 32_768,
                    maxDepth: 16,
                    maxNodes: 1_024,
                    maxStringUtf8Bytes: 8_192,
                    maxContainerItems: 512),
                nameof(request.Signal));
            signal = request.Signal.Value.Clone();
        }

        return new ExecutionRouteRequest
        {
            OperationKind = RuntimeGuard.RequiredUtf8(
                request.OperationKind,
                128,
                nameof(request.OperationKind)),
            ExplicitPath = request.ExplicitPath,
            Requirements = request.Requirements,
            Signal = signal
        };
    }

    internal static bool CanSatisfy(
        ExecutionPath path,
        ExecutionRequirements requirements)
    {
        return path switch
        {
            ExecutionPath.Direct =>
                (requirements
                 & (AgentRequirements | WorkflowRequirements)) == 0,
            ExecutionPath.Agent =>
                (requirements & WorkflowRequirements) == 0,
            ExecutionPath.Workflow => true,
            _ => false
        };
    }

    internal static ExecutionPath MinimumPath(
        ExecutionRequirements requirements) =>
        (requirements & WorkflowRequirements) != 0
            ? ExecutionPath.Workflow
            : (requirements & AgentRequirements) != 0
                ? ExecutionPath.Agent
                : ExecutionPath.Direct;
}

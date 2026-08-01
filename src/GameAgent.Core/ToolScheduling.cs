using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class ToolSchedulerLimits
{
    public ToolSchedulerLimits(
        int maxBatchSize = 128,
        int maxParallelism = 8,
        int maxQueuedCalls = 512,
        int maxActiveConflictKeys = 4_096,
        int maxConflictKeysPerCall = ProtocolLimits.MaxActionExpectedEffects,
        int maxConflictKeyUtf8Bytes = 256,
        JsonValueLimits? argumentJsonLimits = null,
        JsonValueLimits? resultJsonLimits = null,
        int maxDetachedSnapshotItems = 128,
        int detachedShutdownDrainTimeoutMs = 1_000)
    {
        if (maxBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize));
        }

        if (maxParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParallelism));
        }

        if (maxQueuedCalls < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedCalls));
        }

        if (maxActiveConflictKeys < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActiveConflictKeys));
        }

        if (maxConflictKeysPerCall < 0
            || maxConflictKeysPerCall
            > ProtocolLimits.MaxActionExpectedEffects)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConflictKeysPerCall));
        }

        if (maxConflictKeyUtf8Bytes < 1
            || maxConflictKeyUtf8Bytes
            > ProtocolLimits.MaxActionExpectedEffectUnicodeScalars)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConflictKeyUtf8Bytes));
        }

        if (maxDetachedSnapshotItems < 1 || maxDetachedSnapshotItems > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDetachedSnapshotItems));
        }

        if (detachedShutdownDrainTimeoutMs is < 0 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(detachedShutdownDrainTimeoutMs));
        }

        MaxBatchSize = maxBatchSize;
        MaxParallelism = maxParallelism;
        MaxQueuedCalls = maxQueuedCalls;
        MaxActiveConflictKeys = maxActiveConflictKeys;
        MaxConflictKeysPerCall = maxConflictKeysPerCall;
        MaxConflictKeyUtf8Bytes = maxConflictKeyUtf8Bytes;
        MaxDetachedSnapshotItems = maxDetachedSnapshotItems;
        DetachedShutdownDrainTimeoutMs = detachedShutdownDrainTimeoutMs;
        ArgumentJsonLimits = argumentJsonLimits ?? new JsonValueLimits(maxUtf8Bytes: 131_072);
        ResultJsonLimits = resultJsonLimits ?? new JsonValueLimits(maxUtf8Bytes: 262_144);
    }

    public int MaxBatchSize { get; }

    public int MaxParallelism { get; }

    public int MaxQueuedCalls { get; }

    public int MaxActiveConflictKeys { get; }

    public int MaxConflictKeysPerCall { get; }

    public int MaxConflictKeyUtf8Bytes { get; }

    public int MaxDetachedSnapshotItems { get; }

    public int DetachedShutdownDrainTimeoutMs { get; }

    public JsonValueLimits ArgumentJsonLimits { get; }

    public JsonValueLimits ResultJsonLimits { get; }
}

public sealed class ToolExecutionRequest
{
    private DateTimeOffset? _executionDeadline;
    private MonotonicDeadline? _executionMonotonicDeadline;

    public ToolExecutionRequest(
        string agentId,
        ToolInvocation invocation,
        ToolCatalogEntry tool)
    {
        if (invocation is null)
        {
            throw new ArgumentNullException(nameof(invocation));
        }

        ProtocolValidator.EnsureValid(invocation);

        if (!string.Equals(
                invocation.ProtocolVersion,
                ProtocolConstants.ProtocolVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                invocation.SchemaVersion,
                ProtocolConstants.SchemaVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Tool invocation protocolVersion and schemaVersion must match the runtime.",
                nameof(invocation));
        }

        AgentId = RuntimeGuard.RequiredId(agentId, nameof(agentId));
        ToolCallId = RuntimeGuard.RequiredId(
            invocation.ToolCallId,
            nameof(invocation.ToolCallId));
        RunId = RuntimeGuard.RequiredId(invocation.RunId, nameof(invocation.RunId));
        TurnId = RuntimeGuard.RequiredId(invocation.TurnId, nameof(invocation.TurnId));
        AttemptId = RuntimeGuard.RequiredId(
            invocation.AttemptId,
            nameof(invocation.AttemptId));
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        if (!string.Equals(invocation.ToolName, tool.Name, StringComparison.Ordinal)
            || !string.Equals(invocation.ToolVersion, tool.Version, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The invocation tool name/version does not match the catalog entry.",
                nameof(invocation));
        }

        if (!string.Equals(invocation.Effect, tool.Effect, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The invocation effect does not match the catalog entry.",
                nameof(invocation));
        }

        if (invocation.Sequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invocation),
                "Tool invocation sequence cannot be negative.");
        }

        Arguments = invocation.Arguments.Clone();
        ResolvedConflictKeys = RuntimeGuard.CopyStrings(
            invocation.ResolvedConflictKeys,
            ProtocolLimits.MaxToolResolvedConflictKeys,
            ProtocolLimits.MaxToolResolvedConflictKeyUnicodeScalars,
            nameof(invocation.ResolvedConflictKeys),
            sort: true,
            requireUnique: true);
        Sequence = invocation.Sequence;
        CreatedAt = invocation.CreatedAt;
    }

    public string AgentId { get; }

    public string ToolCallId { get; }

    public string RunId { get; }

    public string TurnId { get; }

    public string AttemptId { get; }

    public ToolCatalogEntry Tool { get; }

    public JsonElement Arguments { get; }

    public IReadOnlyList<string> ResolvedConflictKeys { get; }

    public long Sequence { get; }

    public DateTimeOffset CreatedAt { get; }

    internal DateTimeOffset? ExecutionDeadline => _executionDeadline;

    internal MonotonicDeadline? ExecutionMonotonicDeadline =>
        _executionMonotonicDeadline;

    internal void BindExecutionDeadline(DateTimeOffset deadline)
    {
        BindExecutionDeadline(deadline, monotonicDeadline: null);
    }

    internal void BindExecutionDeadline(
        DateTimeOffset deadline,
        MonotonicDeadline? monotonicDeadline)
    {
        if (_executionDeadline.HasValue
            && _executionDeadline.Value != deadline)
        {
            throw new InvalidOperationException(
                "The tool execution deadline is already bound.");
        }
        if (_executionMonotonicDeadline is not null
            && monotonicDeadline is not null
            && !ReferenceEquals(
                _executionMonotonicDeadline,
                monotonicDeadline))
        {
            throw new InvalidOperationException(
                "The monotonic tool execution deadline is already bound.");
        }

        _executionDeadline = deadline;
        _executionMonotonicDeadline ??= monotonicDeadline;
    }
}

public sealed class ToolExecutionSegment
{
    internal ToolExecutionSegment(IReadOnlyList<ToolExecutionRequest> calls)
    {
        Calls = calls;
        CanRunConcurrently = calls.Count > 1;
    }

    public IReadOnlyList<ToolExecutionRequest> Calls { get; }

    public bool CanRunConcurrently { get; }
}

public sealed class ToolBatchPlan
{
    internal ToolBatchPlan(
        IReadOnlyList<ToolExecutionRequest> calls,
        IReadOnlyList<ToolExecutionSegment> segments)
    {
        Calls = calls;
        Segments = segments;
    }

    public IReadOnlyList<ToolExecutionRequest> Calls { get; }

    public IReadOnlyList<ToolExecutionSegment> Segments { get; }
}

public sealed class ToolBatchPlanner
{
    private readonly ToolSchedulerLimits _limits;

    public ToolBatchPlanner(ToolSchedulerLimits? limits = null)
    {
        _limits = limits ?? new ToolSchedulerLimits();
    }

    public ToolBatchPlan Plan(IEnumerable<ToolExecutionRequest> requests)
    {
        if (requests is null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        var calls = Materialize(requests);
        var segments = new List<ToolExecutionSegment>();
        var parallel = new List<ToolExecutionRequest>();
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var call in calls)
        {
            if (CanParallelize(call) && !Conflicts(call, activeKeys))
            {
                parallel.Add(call);
                activeKeys.UnionWith(call.ResolvedConflictKeys);
                continue;
            }

            FlushParallel(parallel, activeKeys, segments);
            if (CanParallelize(call))
            {
                parallel.Add(call);
                activeKeys.UnionWith(call.ResolvedConflictKeys);
            }
            else
            {
                segments.Add(new ToolExecutionSegment(
                    new ReadOnlyCollection<ToolExecutionRequest>(
                        new List<ToolExecutionRequest> { call })));
            }
        }

        FlushParallel(parallel, activeKeys, segments);
        return new ToolBatchPlan(
            new ReadOnlyCollection<ToolExecutionRequest>(calls),
            new ReadOnlyCollection<ToolExecutionSegment>(segments));
    }

    private List<ToolExecutionRequest> Materialize(IEnumerable<ToolExecutionRequest> requests)
    {
        var calls = new List<ToolExecutionRequest>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        foreach (var call in requests)
        {
            if (call is null)
            {
                throw new ArgumentException(
                    "A tool execution request cannot be null.",
                    nameof(requests));
            }

            if (calls.Count >= _limits.MaxBatchSize)
            {
                throw new RuntimeContentLimitException(
                    nameof(requests),
                    "tool_batch_size_exceeded",
                    $"Tool batch exceeds {_limits.MaxBatchSize} calls.");
            }

            if (!callIds.Add(call.ToolCallId))
            {
                throw new ArgumentException(
                    $"Tool call id '{call.ToolCallId}' appears more than once.",
                    nameof(requests));
            }

            if (!sequences.Add(call.Sequence))
            {
                throw new ArgumentException(
                    $"Tool sequence '{call.Sequence}' appears more than once.",
                    nameof(requests));
            }

            ValidateCall(call);
            calls.Add(call);
        }

        calls.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        return calls;
    }

    private void ValidateCall(ToolExecutionRequest call)
    {
        JsonValueInspector.ValidateAndMeasure(
            call.Arguments,
            _limits.ArgumentJsonLimits,
            nameof(call.Arguments));
        if (call.ResolvedConflictKeys.Count > _limits.MaxConflictKeysPerCall)
        {
            throw new RuntimeContentLimitException(
                nameof(call.ResolvedConflictKeys),
                "tool_conflict_key_count_exceeded",
                $"Tool call conflict keys exceed {_limits.MaxConflictKeysPerCall}.");
        }

        foreach (var key in call.ResolvedConflictKeys)
        {
            if (Encoding.UTF8.GetByteCount(key) > _limits.MaxConflictKeyUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(call.ResolvedConflictKeys),
                    "tool_conflict_key_size_exceeded",
                    $"A tool conflict key exceeds {_limits.MaxConflictKeyUtf8Bytes} UTF-8 bytes.");
            }
        }
    }

    private static bool CanParallelize(ToolExecutionRequest call)
    {
        return string.Equals(call.Tool.Effect, ToolEffects.PureRead, StringComparison.Ordinal)
               && !string.Equals(
                   call.Tool.ThreadAffinity,
                   ThreadAffinities.EngineMainThread,
                   StringComparison.Ordinal);
    }

    private static bool Conflicts(
        ToolExecutionRequest call,
        ISet<string> activeKeys)
    {
        return call.ResolvedConflictKeys.Any(activeKeys.Contains);
    }

    private static void FlushParallel(
        ICollection<ToolExecutionRequest> parallel,
        ISet<string> activeKeys,
        ICollection<ToolExecutionSegment> segments)
    {
        if (parallel.Count == 0)
        {
            return;
        }

        segments.Add(new ToolExecutionSegment(
            new ReadOnlyCollection<ToolExecutionRequest>(parallel.ToList())));
        parallel.Clear();
        activeKeys.Clear();
    }
}

public interface IToolCallExecutor
{
    ValueTask<JsonElement> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken cancellationToken);
}

public static class ToolExecutionStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed class ToolExecutionResult
{
    private ToolExecutionResult(
        ToolExecutionRequest request,
        string status,
        JsonElement? result,
        string? errorCode,
        string? errorMessage,
        bool mayHaveExecuted)
    {
        Request = request;
        Status = status;
        Result = result?.Clone();
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        MayHaveExecuted = mayHaveExecuted;
    }

    public ToolExecutionRequest Request { get; }

    public string Status { get; }

    public JsonElement? Result { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public bool MayHaveExecuted { get; }

    public bool IsSuccess =>
        string.Equals(Status, ToolExecutionStatuses.Succeeded, StringComparison.Ordinal);

    internal static ToolExecutionResult Succeeded(
        ToolExecutionRequest request,
        JsonElement result)
    {
        return new ToolExecutionResult(
            request,
            ToolExecutionStatuses.Succeeded,
            result,
            null,
            null,
            true);
    }

    internal static ToolExecutionResult Failed(
        ToolExecutionRequest request,
        string errorCode,
        string errorMessage,
        bool mayHaveExecuted = true)
    {
        return new ToolExecutionResult(
            request,
            ToolExecutionStatuses.Failed,
            null,
            errorCode,
            errorMessage,
            mayHaveExecuted);
    }
}

public sealed class ToolQueueCapacityExceededException : InvalidOperationException
{
    public ToolQueueCapacityExceededException(string capacityCode, string message)
        : base(message)
    {
        CapacityCode = capacityCode;
    }

    public string CapacityCode { get; }
}

public sealed class DetachedToolExecutionSnapshot
{
    internal DetachedToolExecutionSnapshot(
        string toolCallId,
        string toolName,
        string toolVersion,
        string effect,
        string reason,
        DateTimeOffset detachedAt,
        DateTimeOffset capturedAt)
    {
        ToolCallId = toolCallId;
        ToolName = toolName;
        ToolVersion = toolVersion;
        Effect = effect;
        Reason = reason;
        DetachedAt = detachedAt;
        CapturedAt = capturedAt;
        Age = capturedAt <= detachedAt
            ? TimeSpan.Zero
            : capturedAt - detachedAt;
    }

    public string ToolCallId { get; }

    public string ToolName { get; }

    public string ToolVersion { get; }

    public string Effect { get; }

    public string Reason { get; }

    public DateTimeOffset DetachedAt { get; }

    public DateTimeOffset CapturedAt { get; }

    public TimeSpan Age { get; }
}

public sealed class ToolBatchScheduler
{
    private const string EngineMainThreadKey = "\0engine_main_thread";
    private const string DetachedExecutionErrorCode =
        "tool_dispatch_blocked_by_detached_execution";
    private const string DetachedSideEffectErrorCode =
        "tool_dispatch_blocked_by_detached_side_effect";
    private static readonly TimeSpan CancellationCleanupGrace =
        TimeSpan.FromMilliseconds(50);
    private static readonly IRuntimeClock DefaultClock =
        new SystemRuntimeClock();
    private readonly ToolSchedulerLimits _limits;
    private readonly SemaphoreSlim _parallelism;
    private readonly BoundedKeyedGate _conflictGate;
    private readonly AsyncReaderWriterBarrier _effectBarrier = new();
    private readonly IRuntimeDelay _timeoutDelay;
    private readonly BoundedCancellationDispatcher _cancellationDispatcher;
    private readonly Func<
        CancellationToken,
        Action,
        CancellationTokenRegistration> _registerCancellation;
    private readonly object _detachedSync = new();
    private readonly Dictionary<long, DetachedExecution> _detached = new();
    private TaskCompletionSource<bool>? _detachedDrained;
    private long _nextDetachedRegistrationId;
    private int _queuedCalls;

    public ToolBatchScheduler(
        ToolSchedulerLimits? limits = null,
        IRuntimeDelay? timeoutDelay = null)
        : this(
            limits,
            timeoutDelay,
            BoundedCancellationDispatcher.Shared,
            cancellationRegistrar: null)
    {
    }

    internal ToolBatchScheduler(
        ToolSchedulerLimits? limits,
        IRuntimeDelay? timeoutDelay,
        BoundedCancellationDispatcher cancellationDispatcher,
        Func<CancellationToken, Action, CancellationTokenRegistration>?
            cancellationRegistrar = null)
    {
        _limits = limits ?? new ToolSchedulerLimits();
        _parallelism = new SemaphoreSlim(_limits.MaxParallelism, _limits.MaxParallelism);
        _conflictGate = new BoundedKeyedGate(_limits.MaxActiveConflictKeys);
        _timeoutDelay = timeoutDelay ?? new SystemRuntimeDelay();
        _cancellationDispatcher = cancellationDispatcher
                                   ?? throw new ArgumentNullException(
                                       nameof(cancellationDispatcher));
        _registerCancellation = cancellationRegistrar
                                ?? (static (token, callback) =>
                                    token.Register(callback));
    }

    public int QueuedCalls => Volatile.Read(ref _queuedCalls);

    public int DetachedExecutionCount
    {
        get
        {
            lock (_detachedSync)
            {
                return _detached.Count;
            }
        }
    }

    internal TimeSpan DetachedShutdownDrainTimeout =>
        TimeSpan.FromMilliseconds(_limits.DetachedShutdownDrainTimeoutMs);

    public IReadOnlyList<DetachedToolExecutionSnapshot> GetDetachedExecutionSnapshot()
    {
        return GetDetachedExecutionSnapshot(_limits.MaxDetachedSnapshotItems);
    }

    public IReadOnlyList<DetachedToolExecutionSnapshot> GetDetachedExecutionSnapshot(
        int maxItems)
    {
        if (maxItems < 1 || maxItems > _limits.MaxDetachedSnapshotItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems));
        }

        lock (_detachedSync)
        {
            var capturedAt = DateTimeOffset.UtcNow;
            var snapshot = _detached.Values
                .OrderBy(item => item.DetachedAt)
                .ThenBy(item => item.RegistrationId)
                .Take(maxItems)
                .Select(
                    item => new DetachedToolExecutionSnapshot(
                        item.ToolCallId,
                        item.ToolName,
                        item.ToolVersion,
                        item.Effect,
                        item.Reason,
                        item.DetachedAt,
                        capturedAt))
                .ToList();
            return new ReadOnlyCollection<DetachedToolExecutionSnapshot>(snapshot);
        }
    }

    public async ValueTask<bool> DrainDetachedExecutionsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Task drain;
        lock (_detachedSync)
        {
            if (_detached.Count == 0)
            {
                return true;
            }

            drain = _detachedDrained!.Task;
        }

        using var waitCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(timeout, waitCancellation.Token);
        var completed = await Task.WhenAny(drain, deadline).ConfigureAwait(false);
        if (ReferenceEquals(completed, drain))
        {
            waitCancellation.Cancel();
            await ObserveDetachedAsync(deadline).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await deadline.ConfigureAwait(false);
        return false;
    }

    public ValueTask<IReadOnlyList<ToolExecutionResult>> ExecuteAsync(
        ToolBatchPlan plan,
        IToolCallExecutor executor,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            plan,
            executor,
            DefaultClock,
            cancellationToken);
    }

    internal async ValueTask<IReadOnlyList<ToolExecutionResult>> ExecuteAsync(
        ToolBatchPlan plan,
        IToolCallExecutor executor,
        IRuntimeClock clock,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (executor is null)
        {
            throw new ArgumentNullException(nameof(executor));
        }
        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }

        using var reservation = ReserveExecution(plan);
        var results = await ExecuteReservedAsync(
                plan,
                executor,
                clock,
                reservation,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    internal ToolBatchReservation ReserveExecution(ToolBatchPlan plan)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        ValidatePlan(plan);
        ReserveQueue(plan.Calls.Count);
        return new ToolBatchReservation(this, plan, plan.Calls.Count);
    }

    internal async ValueTask<IReadOnlyList<ToolExecutionResult>>
        ExecuteReservedAsync(
            ToolBatchPlan plan,
            IToolCallExecutor executor,
            IRuntimeClock clock,
            ToolBatchReservation reservation,
            CancellationToken cancellationToken = default,
            Func<bool>? tryAcquireDispatchPermit = null)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (executor is null)
        {
            throw new ArgumentNullException(nameof(executor));
        }

        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }

        if (reservation is null)
        {
            throw new ArgumentNullException(nameof(reservation));
        }

        reservation.Begin(this, plan);
        try
        {
            var resultByCallId = new Dictionary<string, ToolExecutionResult>(
                plan.Calls.Count,
                StringComparer.Ordinal);
            foreach (var segment in plan.Segments)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    foreach (var call in plan.Calls)
                    {
                        if (resultByCallId.ContainsKey(call.ToolCallId))
                        {
                            continue;
                        }

                        resultByCallId.Add(
                            call.ToolCallId,
                            ToolExecutionResult.Failed(
                                call,
                                "tool_execution_cancelled",
                                "The tool execution was cancelled before "
                                + "host dispatch.",
                                mayHaveExecuted: false));
                    }

                    break;
                }

                var segmentResults = await ExecuteSegmentAsync(
                        segment,
                        executor,
                        clock,
                        cancellationToken,
                        tryAcquireDispatchPermit)
                    .ConfigureAwait(false);
                foreach (var result in segmentResults)
                {
                    resultByCallId.Add(result.Request.ToolCallId, result);
                }

                if (segmentResults.Any(IsUncertainSideEffect))
                {
                    foreach (var call in plan.Calls)
                    {
                        if (resultByCallId.ContainsKey(call.ToolCallId))
                        {
                            continue;
                        }

                        resultByCallId.Add(
                            call.ToolCallId,
                            ToolExecutionResult.Failed(
                                call,
                                "tool_dispatch_blocked_by_unknown",
                                "A previous side-effecting tool has an unknown outcome.",
                                mayHaveExecuted: false));
                    }
                    break;
                }
            }

            var ordered = new List<ToolExecutionResult>(plan.Calls.Count);
            foreach (var call in plan.Calls)
            {
                if (!resultByCallId.TryGetValue(call.ToolCallId, out var result))
                {
                    throw new InvalidOperationException(
                        $"Tool call '{call.ToolCallId}' did not produce a result.");
                }

                ordered.Add(result);
            }

            return new ReadOnlyCollection<ToolExecutionResult>(ordered);
        }
        finally
        {
            reservation.Dispose();
        }
    }

    private async ValueTask<IReadOnlyList<ToolExecutionResult>> ExecuteSegmentAsync(
        ToolExecutionSegment segment,
        IToolCallExecutor executor,
        IRuntimeClock clock,
        CancellationToken cancellationToken,
        Func<bool>? tryAcquireDispatchPermit)
    {
        if (!segment.CanRunConcurrently)
        {
            var only = await ExecuteOneAsync(
                    segment.Calls[0],
                    executor,
                    clock,
                    cancellationToken,
                    tryAcquireDispatchPermit)
            .ConfigureAwait(false);
            return new[] { only };
        }

        var results = new ToolExecutionResult[segment.Calls.Count];
        var nextIndex = -1;
        var workerCount = Math.Min(_limits.MaxParallelism, segment.Calls.Count);
        var workers = new Task[workerCount];
        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            workers[workerIndex] = RunWorkerAsync();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        return new ReadOnlyCollection<ToolExecutionResult>(results);

        async Task RunWorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= segment.Calls.Count)
                {
                    return;
                }

                results[index] = await ExecuteOneAsync(
                        segment.Calls[index],
                        executor,
                        clock,
                        cancellationToken,
                        tryAcquireDispatchPermit)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<ToolExecutionResult> ExecuteOneAsync(
        ToolExecutionRequest request,
        IToolCallExecutor executor,
        IRuntimeClock clock,
        CancellationToken cancellationToken,
        Func<bool>? tryAcquireDispatchPermit)
    {
        var quarantineFailure = GetDetachedQuarantineFailure(request);
        if (quarantineFailure is not null)
        {
            return quarantineFailure;
        }

        var effectiveDeadline = request.ExecutionDeadline
            ?? clock.UtcNow.AddMilliseconds(request.Tool.TimeoutMs);
        var monotonicDeadline = request.ExecutionMonotonicDeadline
            ?? MonotonicDeadline.Start(
                TimeSpan.FromMilliseconds(request.Tool.TimeoutMs));
        var admissionRemaining = Remaining(
            effectiveDeadline,
            clock.UtcNow,
            monotonicDeadline);
        if (admissionRemaining <= TimeSpan.Zero)
        {
            return DeadlineExpiredBeforeDispatch(request);
        }

        using var deadlineCancellation = new CancellationTokenSource();
        deadlineCancellation.CancelAfter(admissionRemaining);
        var wallDeadlineSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var wallDeadlineRegistration =
            deadlineCancellation.Token.Register(
                () => wallDeadlineSignal.TrySetResult(true));
        using var dispatchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCancellation.Token);
        IAsyncDisposable? effectLease = null;
        IDisposable? conflictLease = null;
        CancellationTokenSource? executionCancellation = null;
        BoundedCancellationDispatcher.CancellationDispatchReservation?
            timeoutCancellationReservation = null;
        BoundedCancellationDispatcher.CancellationDispatchReservation?
            executionCancellationReservation = null;
        Task<JsonElement>? execution = null;
        var executionStarted = false;
        var parallelismAcquired = false;
        var releaseParallelism = true;
        try
        {
            await _parallelism
                .WaitAsync(dispatchCancellation.Token)
                .ConfigureAwait(false);
            parallelismAcquired = true;
            var isBarrier = IsBarrier(request);
            effectLease = isBarrier
                ? await _effectBarrier
                    .AcquireWriterAsync(dispatchCancellation.Token)
                    .ConfigureAwait(false)
                : await _effectBarrier
                    .AcquireReaderAsync(dispatchCancellation.Token)
                    .ConfigureAwait(false);
            conflictLease = await _conflictGate.AcquireAsync(
                    CoordinationKeys(request),
                    dispatchCancellation.Token)
                .ConfigureAwait(false);
            executionCancellation = new CancellationTokenSource();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                quarantineFailure = GetDetachedQuarantineFailure(request);
                if (quarantineFailure is not null)
                {
                    return quarantineFailure;
                }
                if (Remaining(
                        effectiveDeadline,
                        clock.UtcNow,
                        monotonicDeadline)
                    <= TimeSpan.Zero)
                {
                    return DeadlineExpiredBeforeDispatch(request);
                }

                var executionRemaining = Remaining(
                    effectiveDeadline,
                    clock.UtcNow,
                    monotonicDeadline);
                if (executionRemaining <= TimeSpan.Zero)
                {
                    return DeadlineExpiredBeforeDispatch(request);
                }

                if (!_cancellationDispatcher.TryReserve(
                        out timeoutCancellationReservation)
                    || !_cancellationDispatcher.TryReserve(
                        out executionCancellationReservation))
                {
                    timeoutCancellationReservation?.Dispose();
                    timeoutCancellationReservation = null;
                    executionCancellationReservation?.Dispose();
                    executionCancellationReservation = null;
                    return ToolExecutionResult.Failed(
                        request,
                        "tool_cancellation_capacity_exceeded",
                        "Tool cancellation cleanup capacity is exhausted.",
                        mayHaveExecuted: false);
                }

                var waitCancellation = new CancellationTokenSource();
                Task deadline;
                try
                {
                    deadline = _timeoutDelay
                        .DelayAsync(
                            executionRemaining,
                            waitCancellation.Token)
                        .AsTask();
                }
                catch (Exception exception)
                {
                    waitCancellation.Dispose();
                    return ToolExecutionResult.Failed(
                        request,
                        "tool_timeout_infrastructure_exception",
                        LimitMessage(exception.Message),
                        mayHaveExecuted: false);
                }

                if (deadline.IsCompleted)
                {
                    try
                    {
                        await deadline.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        waitCancellation.Dispose();
                        return ToolExecutionResult.Failed(
                            request,
                            "tool_timeout_infrastructure_exception",
                            LimitMessage(exception.Message),
                            mayHaveExecuted: false);
                    }

                    waitCancellation.Dispose();
                    return DeadlineExpiredBeforeDispatch(request);
                }

                var cancellationSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationTokenRegistration callerCancellationRegistration;
                try
                {
                    callerCancellationRegistration = _registerCancellation(
                        cancellationToken,
                        () => cancellationSignal.TrySetResult(true));
                }
                catch
                {
                    var failedRegistrationCleanup =
                        CancelObserveAndDisposeAsync(
                            deadline,
                            waitCancellation,
                            timeoutCancellationReservation!);
                    timeoutCancellationReservation = null;
                    _ = await Task.WhenAny(
                            failedRegistrationCleanup,
                            Task.Delay(CancellationCleanupGrace))
                        .ConfigureAwait(false);
                    throw;
                }

                using var callerCancellationRegistrationScope =
                    callerCancellationRegistration;
                try
                {
                    dispatchCancellation.Token.ThrowIfCancellationRequested();
                    if (tryAcquireDispatchPermit is not null
                        && !tryAcquireDispatchPermit())
                    {
                        var blockedDispatchCleanup =
                            CancelObserveAndDisposeAsync(
                                deadline,
                                waitCancellation,
                                timeoutCancellationReservation!);
                        timeoutCancellationReservation = null;
                        _ = await Task.WhenAny(
                                blockedDispatchCleanup,
                                Task.Delay(CancellationCleanupGrace))
                            .ConfigureAwait(false);
                        return ToolExecutionResult.Failed(
                            request,
                            "tool_control_before_dispatch",
                            "A run-control command was accepted before "
                            + "host dispatch.",
                            mayHaveExecuted: false);
                    }

                    executionStarted = true;
                    execution = executor
                        .ExecuteAsync(request, executionCancellation.Token)
                        .AsTask();
                }
                catch
                {
                    var failedDispatchCleanup =
                        CancelObserveAndDisposeAsync(
                            deadline,
                            waitCancellation,
                            timeoutCancellationReservation!);
                    timeoutCancellationReservation = null;
                    _ = await Task.WhenAny(
                            failedDispatchCleanup,
                            Task.Delay(CancellationCleanupGrace))
                        .ConfigureAwait(false);
                    throw;
                }

                var completed = await Task.WhenAny(
                        execution,
                        deadline,
                        cancellationSignal.Task,
                        wallDeadlineSignal.Task)
                    .ConfigureAwait(false);
                bool executionWonBeforeDeadline;
                try
                {
                    executionWonBeforeDeadline =
                        ReferenceEquals(completed, execution)
                        && !wallDeadlineSignal.Task.IsCompleted
                        && Remaining(
                            effectiveDeadline,
                            clock.UtcNow,
                            monotonicDeadline)
                        > TimeSpan.Zero;
                }
                catch
                {
                    TransferExecutionOwnership(
                        execution,
                        "post_dispatch_failure");
                    var failedWinnerCleanup =
                        CancelObserveAndDisposeAsync(
                            deadline,
                            waitCancellation,
                            timeoutCancellationReservation!);
                    timeoutCancellationReservation = null;
                    _ = await Task.WhenAny(
                            failedWinnerCleanup,
                            Task.Delay(CancellationCleanupGrace))
                        .ConfigureAwait(false);
                    throw;
                }

                var timeoutCleanup = CancelObserveAndDisposeAsync(
                    deadline,
                    waitCancellation,
                    timeoutCancellationReservation!);
                timeoutCancellationReservation = null;
                _ = await Task.WhenAny(
                        timeoutCleanup,
                        Task.Delay(CancellationCleanupGrace))
                    .ConfigureAwait(false);

                if (!executionWonBeforeDeadline)
                {
                    var detachedReason = cancellationToken.IsCancellationRequested
                        ? "caller_cancelled"
                        : "timeout";
                    TransferExecutionOwnership(
                        execution,
                        detachedReason);
                    cancellationToken.ThrowIfCancellationRequested();

                    return ToolExecutionResult.Failed(
                        request,
                        "tool_timeout",
                        "The tool execution deadline elapsed.",
                        mayHaveExecuted: HasSideEffects(request));
                }

                var result = await execution.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                JsonValueInspector.ValidateAndMeasure(
                    result,
                    _limits.ResultJsonLimits,
                    nameof(result));
                return ToolExecutionResult.Succeeded(request, result);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                if (deadlineCancellation.IsCancellationRequested)
                {
                    return executionStarted
                        ? ToolExecutionResult.Failed(
                            request,
                            "tool_timeout",
                            "The tool execution deadline elapsed.",
                            mayHaveExecuted: HasSideEffects(request))
                        : DeadlineExpiredBeforeDispatch(request);
                }

                return ToolExecutionResult.Failed(
                    request,
                    "tool_executor_cancelled",
                    "The tool executor cancelled the call.",
                    mayHaveExecuted:
                        executionStarted && HasSideEffects(request));
            }
            catch (RuntimeContentLimitException exception)
            {
                return ToolExecutionResult.Failed(
                    request,
                    exception.LimitCode,
                    LimitMessage(exception.Message),
                    mayHaveExecuted:
                        executionStarted && HasSideEffects(request));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (execution is not null)
                {
                    TransferExecutionOwnership(
                        execution,
                        "post_dispatch_failure");
                }

                return ToolExecutionResult.Failed(
                    request,
                    "tool_executor_exception",
                    LimitMessage(exception.Message),
                    mayHaveExecuted:
                        executionStarted && HasSideEffects(request));
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && deadlineCancellation.IsCancellationRequested)
        {
            return DeadlineExpiredBeforeDispatch(request);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (execution is not null)
            {
                TransferExecutionOwnership(
                    execution,
                    "caller_cancelled");
            }

            return ToolExecutionResult.Failed(
                request,
                "tool_execution_cancelled",
                executionStarted
                    ? "The dispatched tool execution was cancelled."
                    : "The tool execution was cancelled before host dispatch.",
                mayHaveExecuted:
                    executionStarted && HasSideEffects(request));
        }
        finally
        {
            try
            {
                conflictLease?.Dispose();
            }
            finally
            {
                try
                {
                    if (effectLease is not null)
                    {
                        await effectLease.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    try
                    {
                        if (parallelismAcquired && releaseParallelism)
                        {
                            _parallelism.Release();
                        }
                    }
                    finally
                    {
                        try
                        {
                            executionCancellation?.Dispose();
                        }
                        finally
                        {
                            timeoutCancellationReservation?.Dispose();
                            executionCancellationReservation?.Dispose();
                        }
                    }
                }

            }
        }

        void TransferExecutionOwnership(
            Task<JsonElement> dispatchedExecution,
            string reason)
        {
            if (!releaseParallelism)
            {
                return;
            }

            if (dispatchedExecution.IsCompleted)
            {
                _ = ObserveDetachedAsync(dispatchedExecution);
                return;
            }

            var detachedEffectLease = effectLease!;
            var detachedConflictLease = conflictLease!;
            var detachedCancellation = executionCancellation!;
            effectLease = null;
            conflictLease = null;
            executionCancellation = null;
            var detachedCancellationReservation =
                executionCancellationReservation!;
            executionCancellationReservation = null;
            releaseParallelism = false;
            var detachedRegistration = AddDetachedExecution(
                request,
                reason);
            var cancellationCleanup =
                detachedCancellationReservation.DispatchAsync(
                    detachedCancellation);
            _ = ObserveDetachedAndReleaseAsync(
                dispatchedExecution,
                detachedRegistration,
                detachedConflictLease,
                detachedEffectLease,
                _parallelism,
                detachedCancellation,
                cancellationCleanup,
                detachedCancellationReservation);
        }
    }

    private static ToolExecutionResult DeadlineExpiredBeforeDispatch(
        ToolExecutionRequest request)
    {
        return ToolExecutionResult.Failed(
            request,
            "tool_deadline_expired",
            "The tool deadline elapsed before host dispatch.",
            mayHaveExecuted: false);
    }

    private static TimeSpan Remaining(
        DateTimeOffset deadline,
        DateTimeOffset now,
        MonotonicDeadline monotonicDeadline)
    {
        var utcRemaining = deadline - now;
        var monotonicRemaining = monotonicDeadline.Remaining;
        if (utcRemaining <= TimeSpan.Zero
            || monotonicRemaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var maximum = TimeSpan.FromMilliseconds(int.MaxValue);
        var remaining = utcRemaining < monotonicRemaining
            ? utcRemaining
            : monotonicRemaining;
        return remaining < maximum ? remaining : maximum;
    }

    private ToolExecutionResult? GetDetachedQuarantineFailure(
        ToolExecutionRequest request)
    {
        lock (_detachedSync)
        {
            return GetDetachedQuarantineFailureUnsafe(request);
        }
    }

    private ToolExecutionResult? GetDetachedQuarantineFailureUnsafe(
        ToolExecutionRequest request)
    {
        if (_detached.Count == 0)
        {
            return null;
        }

        var requestKeys = CoordinationKeys(request);
        foreach (var detached in _detached.Values)
        {
            if (string.Equals(
                    request.Tool.Name,
                    detached.ToolName,
                    StringComparison.Ordinal)
                && string.Equals(
                    request.Tool.Version,
                    detached.ToolVersion,
                    StringComparison.Ordinal))
            {
                return ToolExecutionResult.Failed(
                    request,
                    DetachedExecutionErrorCode,
                    "The same tool version still has a detached execution.",
                    mayHaveExecuted: false);
            }

            if (detached.IsGlobalBarrier)
            {
                return ToolExecutionResult.Failed(
                    request,
                    DetachedSideEffectErrorCode,
                    "A detached global side effect prevents safe tool dispatch.",
                    mayHaveExecuted: false);
            }

            if (detached.HasSideEffects
                && (HasSideEffects(request)
                    || KeysOverlap(requestKeys, detached.CoordinationKeys)))
            {
                return ToolExecutionResult.Failed(
                    request,
                    DetachedSideEffectErrorCode,
                    "A detached side effect may conflict with this tool call.",
                    mayHaveExecuted: false);
            }

            if (!detached.HasSideEffects
                && (IsBarrier(request)
                    || KeysOverlap(requestKeys, detached.CoordinationKeys)))
            {
                return ToolExecutionResult.Failed(
                    request,
                    DetachedExecutionErrorCode,
                    "A detached execution prevents safe tool dispatch.",
                    mayHaveExecuted: false);
            }
        }

        return null;
    }

    private long AddDetachedExecution(
        ToolExecutionRequest request,
        string reason)
    {
        var coordinationKeys = CoordinationKeys(request).ToArray();
        lock (_detachedSync)
        {
            if (_detached.Count == 0)
            {
                _detachedDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            var registrationId = ++_nextDetachedRegistrationId;
            _detached.Add(
                registrationId,
                new DetachedExecution(
                    registrationId,
                    request.ToolCallId,
                    request.Tool.Name,
                    request.Tool.Version,
                    request.Tool.Effect,
                    reason,
                    request.Tool.Effect is ToolEffects.WorldCommand
                        or ToolEffects.ExternalWrite,
                    HasSideEffects(request),
                    coordinationKeys,
                    DateTimeOffset.UtcNow));
            return registrationId;
        }
    }

    private void RemoveDetachedExecution(long registrationId)
    {
        lock (_detachedSync)
        {
            if (!_detached.Remove(registrationId) || _detached.Count != 0)
            {
                return;
            }

            _detachedDrained?.TrySetResult(true);
            _detachedDrained = null;
        }
    }

    private static bool KeysOverlap(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        var smaller = left.Count <= right.Count ? left : right;
        var larger = ReferenceEquals(smaller, left) ? right : left;
        var lookup = new HashSet<string>(smaller, StringComparer.Ordinal);
        return larger.Any(lookup.Contains);
    }

    private static async Task ObserveDetachedAsync(Task<JsonElement> execution)
    {
        try
        {
            _ = await execution.ConfigureAwait(false);
        }
        catch
        {
            // The scheduler has already returned a bounded timeout or
            // cancellation result. Observing the task prevents a late host
            // exception from becoming unobserved.
        }
    }

    private static async Task CancelObserveAndDisposeAsync(
        Task operation,
        CancellationTokenSource cancellation,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        Task cancellationTask;
        if (operation.IsCompleted)
        {
            cancellationTask = Task.CompletedTask;
        }
        else
        {
            cancellationTask =
                cancellationReservation.DispatchAsync(cancellation);
        }

        try
        {
            await ObserveDetachedAsync(operation).ConfigureAwait(false);
            try
            {
                await cancellationTask.ConfigureAwait(false);
            }
            catch
            {
                // The tool result is already fenced. Observe cancellation
                // dispatch rejection without replacing that result.
            }
        }
        finally
        {
            try
            {
                cancellation.Dispose();
            }
            finally
            {
                cancellationReservation.Dispose();
            }
        }
    }

    private static async Task ObserveDetachedAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            // The scheduler already selected the execution, timeout, or caller
            // cancellation winner. The losing task cannot replace that result.
        }
    }

    private async Task ObserveDetachedAndReleaseAsync(
        Task<JsonElement> execution,
        long detachedRegistration,
        IDisposable conflictLease,
        IAsyncDisposable effectLease,
        SemaphoreSlim parallelism,
        CancellationTokenSource cancellation,
        Task cancellationCleanup,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            await ObserveDetachedAsync(execution).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                try
                {
                    conflictLease.Dispose();
                }
                finally
                {
                    try
                    {
                        await effectLease.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        try
                        {
                            parallelism.Release();
                        }
                        finally
                        {
                            _ = DisposeCancellationWhenReadyAsync(
                                cancellation,
                                cancellationCleanup,
                                cancellationReservation);
                        }
                    }
                }
            }
            catch
            {
                // A detached execution is already fenced off. Cleanup faults
                // cannot be surfaced to the completed scheduler call.
            }
            finally
            {
                RemoveDetachedExecution(detachedRegistration);
            }
        }
    }

    private static async Task DisposeCancellationWhenReadyAsync(
        CancellationTokenSource cancellation,
        Task cancellationCleanup,
        BoundedCancellationDispatcher.CancellationDispatchReservation
            cancellationReservation)
    {
        try
        {
            await ObserveDetachedAsync(cancellationCleanup).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                cancellation.Dispose();
            }
            catch
            {
                // Cancellation cleanup is diagnostic-only after the execution
                // and all scheduler leases have completed.
            }
            finally
            {
                cancellationReservation.Dispose();
            }
        }
    }

    private static bool IsUncertainSideEffect(ToolExecutionResult result)
    {
        return !result.IsSuccess
               && result.MayHaveExecuted
               && HasSideEffects(result.Request);
    }

    private static bool HasSideEffects(ToolExecutionRequest request)
    {
        return !string.Equals(
            request.Tool.Effect,
            ToolEffects.PureRead,
            StringComparison.Ordinal);
    }

    internal sealed class ToolBatchReservation : IDisposable
    {
        private ToolBatchScheduler? _owner;
        private readonly ToolBatchPlan _plan;
        private readonly int _callCount;
        private int _state;

        internal ToolBatchReservation(
            ToolBatchScheduler owner,
            ToolBatchPlan plan,
            int callCount)
        {
            _owner = owner;
            _plan = plan;
            _callCount = callCount;
        }

        internal void Begin(
            ToolBatchScheduler owner,
            ToolBatchPlan plan)
        {
            if (!ReferenceEquals(_owner, owner)
                || !ReferenceEquals(_plan, plan))
            {
                throw new ArgumentException(
                    "The tool queue reservation belongs to a different plan.");
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "The tool queue reservation is no longer available.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, 2) == 2)
            {
                return;
            }

            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseQueue(_callCount);
        }
    }

    private sealed class DetachedExecution
    {
        public DetachedExecution(
            long registrationId,
            string toolCallId,
            string toolName,
            string toolVersion,
            string effect,
            string reason,
            bool isGlobalBarrier,
            bool hasSideEffects,
            IReadOnlyList<string> coordinationKeys,
            DateTimeOffset detachedAt)
        {
            RegistrationId = registrationId;
            ToolCallId = toolCallId;
            ToolName = toolName;
            ToolVersion = toolVersion;
            Effect = effect;
            Reason = reason;
            IsGlobalBarrier = isGlobalBarrier;
            HasSideEffects = hasSideEffects;
            CoordinationKeys = coordinationKeys.ToArray();
            DetachedAt = detachedAt;
        }

        public long RegistrationId { get; }

        public string ToolCallId { get; }

        public string ToolName { get; }

        public string ToolVersion { get; }

        public string Effect { get; }

        public string Reason { get; }

        public bool IsGlobalBarrier { get; }

        public bool HasSideEffects { get; }

        public IReadOnlyList<string> CoordinationKeys { get; }

        public DateTimeOffset DetachedAt { get; }
    }

    private void ValidatePlan(ToolBatchPlan plan)
    {
        if (plan.Calls.Count > _limits.MaxBatchSize)
        {
            throw new RuntimeContentLimitException(
                nameof(plan),
                "tool_batch_size_exceeded",
                $"Tool batch exceeds {_limits.MaxBatchSize} calls.");
        }

        var expectedIndex = 0;
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in plan.Segments)
        {
            if (segment.Calls.Count == 0)
            {
                throw new ArgumentException("Tool plan contains an empty segment.", nameof(plan));
            }

            foreach (var call in segment.Calls)
            {
                if (expectedIndex >= plan.Calls.Count
                    || !ReferenceEquals(call, plan.Calls[expectedIndex]))
                {
                    throw new ArgumentException(
                        "Tool plan segments do not match the ordered call list.",
                        nameof(plan));
                }

                if (!callIds.Add(call.ToolCallId))
                {
                    throw new ArgumentException(
                        "Tool plan contains a duplicate tool call id.",
                        nameof(plan));
                }

                JsonValueInspector.ValidateAndMeasure(
                    call.Arguments,
                    _limits.ArgumentJsonLimits,
                    nameof(call.Arguments));
                ValidateConflictKeys(call);
                expectedIndex++;
            }
        }

        if (expectedIndex != plan.Calls.Count)
        {
            throw new ArgumentException(
                "Tool plan segments do not cover every ordered call.",
                nameof(plan));
        }
    }

    private void ValidateConflictKeys(ToolExecutionRequest call)
    {
        if (call.ResolvedConflictKeys.Count > _limits.MaxConflictKeysPerCall)
        {
            throw new RuntimeContentLimitException(
                nameof(call.ResolvedConflictKeys),
                "tool_conflict_key_count_exceeded",
                $"Tool call conflict keys exceed {_limits.MaxConflictKeysPerCall}.");
        }

        foreach (var key in call.ResolvedConflictKeys)
        {
            if (Encoding.UTF8.GetByteCount(key) > _limits.MaxConflictKeyUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(call.ResolvedConflictKeys),
                    "tool_conflict_key_size_exceeded",
                    $"A tool conflict key exceeds {_limits.MaxConflictKeyUtf8Bytes} UTF-8 bytes.");
            }
        }
    }

    private void ReserveQueue(int callCount)
    {
        while (true)
        {
            var current = Volatile.Read(ref _queuedCalls);
            if ((long)current + callCount > _limits.MaxQueuedCalls)
            {
                throw new ToolQueueCapacityExceededException(
                    "tool_queue_capacity_exceeded",
                    $"Tool queue would exceed {_limits.MaxQueuedCalls} calls.");
            }

            if (Interlocked.CompareExchange(
                    ref _queuedCalls,
                    current + callCount,
                    current) == current)
            {
                return;
            }
        }
    }

    private void ReleaseQueue(int callCount)
    {
        Interlocked.Add(ref _queuedCalls, -callCount);
    }

    private static bool IsBarrier(ToolExecutionRequest request)
    {
        return string.Equals(
                   request.Tool.Effect,
                   ToolEffects.WorldCommand,
                   StringComparison.Ordinal)
               || string.Equals(
                   request.Tool.Effect,
                   ToolEffects.ExternalWrite,
                   StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> CoordinationKeys(ToolExecutionRequest request)
    {
        var keys = new List<string>(request.ResolvedConflictKeys.Count + 2);
        keys.AddRange(request.ResolvedConflictKeys.Select(key => $"resource\0{key}"));
        if (string.Equals(
                request.Tool.Effect,
                ToolEffects.AgentLocalWrite,
                StringComparison.Ordinal))
        {
            keys.Add($"agent\0{request.AgentId}");
        }

        if (string.Equals(
                request.Tool.ThreadAffinity,
                ThreadAffinities.EngineMainThread,
                StringComparison.Ordinal))
        {
            keys.Add(EngineMainThreadKey);
        }

        keys.Sort(StringComparer.Ordinal);
        return new ReadOnlyCollection<string>(keys);
    }

    private static string LimitMessage(string message)
    {
        const int maxBytes = 2_048;
        if (Encoding.UTF8.GetByteCount(message) <= maxBytes)
        {
            return message;
        }

        var output = new StringBuilder(message.Length);
        var bytes = 0;
        for (var index = 0; index < message.Length; index++)
        {
            var characterLength = char.IsHighSurrogate(message[index])
                                  && index + 1 < message.Length
                                  && char.IsLowSurrogate(message[index + 1])
                ? 2
                : 1;
            var characterBytes = Encoding.UTF8.GetByteCount(
                message.Substring(index, characterLength));
            if (bytes + characterBytes > maxBytes)
            {
                break;
            }

            output.Append(message, index, characterLength);
            bytes += characterBytes;
            index += characterLength - 1;
        }

        return output.ToString();
    }

    private sealed class BoundedKeyedGate
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly int _maxEntries;

        public BoundedKeyedGate(int maxEntries)
        {
            _maxEntries = maxEntries;
        }

        public async ValueTask<IDisposable> AcquireAsync(
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken)
        {
            if (keys.Count == 0)
            {
                return EmptyLease.Instance;
            }

            var acquired = new List<KeyLease>(keys.Count);
            try
            {
                foreach (var key in keys)
                {
                    var entry = Retain(key);
                    try
                    {
                        await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        acquired.Add(new KeyLease(this, key, entry));
                    }
                    catch
                    {
                        ReleaseReference(key, entry);
                        throw;
                    }
                }

                return new LeaseSet(acquired);
            }
            catch
            {
                for (var index = acquired.Count - 1; index >= 0; index--)
                {
                    acquired[index].Dispose();
                }

                throw;
            }
        }

        private Entry Retain(string key)
        {
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    if (_entries.Count >= _maxEntries)
                    {
                        throw new ToolQueueCapacityExceededException(
                            "tool_conflict_key_capacity_exceeded",
                            $"Active conflict key count exceeds {_maxEntries}.");
                    }

                    entry = new Entry();
                    _entries.Add(key, entry);
                }

                entry.ReferenceCount++;
                return entry;
            }
        }

        private void Release(string key, Entry entry)
        {
            entry.Semaphore.Release();
            ReleaseReference(key, entry);
        }

        private void ReleaseReference(string key, Entry entry)
        {
            lock (_sync)
            {
                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0)
                {
                    _entries.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class Entry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            public int ReferenceCount { get; set; }
        }

        private sealed class KeyLease : IDisposable
        {
            private BoundedKeyedGate? _owner;
            private readonly string _key;
            private readonly Entry _entry;

            public KeyLease(BoundedKeyedGate owner, string key, Entry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Release(_key, _entry);
            }
        }

        private sealed class LeaseSet : IDisposable
        {
            private IReadOnlyList<KeyLease>? _leases;

            public LeaseSet(IReadOnlyList<KeyLease> leases)
            {
                _leases = leases;
            }

            public void Dispose()
            {
                var leases = Interlocked.Exchange(ref _leases, null);
                if (leases is null)
                {
                    return;
                }

                for (var index = leases.Count - 1; index >= 0; index--)
                {
                    leases[index].Dispose();
                }
            }
        }

        private sealed class EmptyLease : IDisposable
        {
            public static EmptyLease Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class AsyncReaderWriterBarrier
    {
        private readonly SemaphoreSlim _turnstile = new(1, 1);
        private readonly SemaphoreSlim _roomEmpty = new(1, 1);
        private readonly SemaphoreSlim _readerMutex = new(1, 1);
        private int _readerCount;

        public async ValueTask<IAsyncDisposable> AcquireReaderAsync(
            CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
            _turnstile.Release();

            await _readerMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            var incremented = false;
            try
            {
                _readerCount++;
                incremented = true;
                if (_readerCount == 1)
                {
                    try
                    {
                        await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        _readerCount--;
                        incremented = false;
                        throw;
                    }
                }
            }
            finally
            {
                _readerMutex.Release();
            }

            if (!incremented)
            {
                throw new InvalidOperationException("Reader barrier acquisition failed.");
            }

            return new AsyncLease(ReleaseReaderAsync);
        }

        public async ValueTask<IAsyncDisposable> AcquireWriterAsync(
            CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _turnstile.Release();
                throw;
            }

            return new AsyncLease(ReleaseWriterAsync);
        }

        private async ValueTask ReleaseReaderAsync()
        {
            await _readerMutex.WaitAsync().ConfigureAwait(false);
            try
            {
                _readerCount--;
                if (_readerCount == 0)
                {
                    _roomEmpty.Release();
                }
            }
            finally
            {
                _readerMutex.Release();
            }
        }

        private ValueTask ReleaseWriterAsync()
        {
            _roomEmpty.Release();
            _turnstile.Release();
            return default;
        }

        private sealed class AsyncLease : IAsyncDisposable
        {
            private Func<ValueTask>? _release;

            public AsyncLease(Func<ValueTask> release)
            {
                _release = release;
            }

            public ValueTask DisposeAsync()
            {
                var release = Interlocked.Exchange(ref _release, null);
                return release is null ? default : release();
            }
        }
    }
}

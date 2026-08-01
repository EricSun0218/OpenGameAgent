using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class ChildAgentSupervisorOptions
{
    public int MaxDepth { get; set; } = 4;

    public int MaxConcurrentChildren { get; set; } = 8;

    public int MaxActiveChildrenPerParent { get; set; } = 8;

    public int MaxChildrenPerBatch { get; set; } = 32;

    public int MaxRememberedLineages { get; set; } = 4_096;

    public TimeSpan ChildTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal ChildAgentSupervisorOptions Snapshot()
    {
        if (MaxDepth is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        }

        if (MaxConcurrentChildren is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentChildren));
        }

        if (MaxActiveChildrenPerParent is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxActiveChildrenPerParent));
        }

        if (MaxChildrenPerBatch is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxChildrenPerBatch));
        }

        if (MaxRememberedLineages is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRememberedLineages));
        }

        if (ChildTimeout < TimeSpan.FromMilliseconds(1)
            || ChildTimeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(ChildTimeout));
        }

        if (ShutdownTimeout < TimeSpan.FromMilliseconds(1)
            || ShutdownTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
        }

        return new ChildAgentSupervisorOptions
        {
            MaxDepth = MaxDepth,
            MaxConcurrentChildren = MaxConcurrentChildren,
            MaxActiveChildrenPerParent = MaxActiveChildrenPerParent,
            MaxChildrenPerBatch = MaxChildrenPerBatch,
            MaxRememberedLineages = MaxRememberedLineages,
            ChildTimeout = ChildTimeout,
            ShutdownTimeout = ShutdownTimeout
        };
    }
}

public sealed class ChildAgentLineage
{
    public const string ExtensionName = "gameAgent.childLineage";

    public ChildAgentLineage(
        string rootRunId,
        string parentRunId,
        string childRunId,
        int depth)
    {
        RootRunId = RuntimeGuard.RequiredId(
            rootRunId,
            nameof(rootRunId));
        ParentRunId = RuntimeGuard.RequiredId(
            parentRunId,
            nameof(parentRunId));
        ChildRunId = RuntimeGuard.RequiredId(
            childRunId,
            nameof(childRunId));
        if (depth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        Depth = depth;
    }

    public string RootRunId { get; }

    public string ParentRunId { get; }

    public string ChildRunId { get; }

    public int Depth { get; }

    internal System.Text.Json.JsonElement ToJson() =>
        JsonArrayBuilder.Object(
            ("rootRunId", JsonArrayBuilder.String(RootRunId)),
            ("parentRunId", JsonArrayBuilder.String(ParentRunId)),
            ("childRunId", JsonArrayBuilder.String(ChildRunId)),
            ("depth", JsonArrayBuilder.Number(Depth)));

    public static ChildAgentLineage? Read(AgentRun run)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (!run.Extensions.TryGetValue(ExtensionName, out var value))
        {
            return null;
        }

        try
        {
            if (value.ValueKind
                != System.Text.Json.JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The child-agent lineage extension is invalid.");
            }

            var lineage = new ChildAgentLineage(
                value.GetProperty("rootRunId").GetString()!,
                value.GetProperty("parentRunId").GetString()!,
                value.GetProperty("childRunId").GetString()!,
                value.GetProperty("depth").GetInt32());
            if (!string.Equals(
                    lineage.ChildRunId,
                    run.RunId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The child-agent lineage does not match the run.");
            }

            return lineage;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                  or InvalidOperationException
                  or KeyNotFoundException
                  or FormatException
                  or OverflowException)
        {
            throw new InvalidDataException(
                "The child-agent lineage extension is invalid.",
                exception);
        }
    }
}

public sealed class ChildAgentRunResult
{
    public ChildAgentRunResult(
        ChildAgentLineage lineage,
        DurableRunOutcome outcome)
    {
        Lineage = lineage;
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
    }

    public ChildAgentLineage Lineage { get; }

    public DurableRunOutcome Outcome { get; }
}

public sealed class ChildAgentBatchItemResult
{
    internal ChildAgentBatchItemResult(
        int index,
        string childRunId,
        ChildAgentRunResult? result,
        string? errorType)
    {
        Index = index;
        ChildRunId = childRunId;
        Result = result;
        ErrorType = errorType;
    }

    public int Index { get; }

    public string ChildRunId { get; }

    public ChildAgentRunResult? Result { get; }

    public string? ErrorType { get; }

    public bool HasOutcome => Result is not null;

    public string? RunState => Result?.Outcome.Run.State;

    public bool ReconciliationRequired =>
        Result?.Outcome.ReconciliationRequired == true;

    public bool Succeeded =>
        Result is not null
        && string.Equals(
            Result.Outcome.Run.State,
            RunStates.Completed,
            StringComparison.Ordinal)
        && !Result.Outcome.ReconciliationRequired;
}

public sealed class ChildAgentBatchResult
{
    internal ChildAgentBatchResult(
        IReadOnlyList<ChildAgentBatchItemResult> items)
    {
        Items = items;
    }

    public IReadOnlyList<ChildAgentBatchItemResult> Items { get; }

    public bool AllSucceeded => Items.All(item => item.Succeeded);
}

/// <summary>
/// Runs bounded child-agent work on an existing durable runtime. It records
/// parent/root/depth lineage on every child run, propagates cancellation, and
/// keeps concurrent children isolated so one child failure does not erase
/// sibling outcomes.
/// </summary>
public sealed class ChildAgentSupervisor : IAsyncDisposable
{
    private readonly IDurableAgentRuntime _runtime;
    private readonly ChildAgentSupervisorOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, ChildAgentLineage>
        _activeLineages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveChildCancellation>
        _activeChildren = new(StringComparer.Ordinal);
    private readonly BoundedCancellationDispatcher
        _childCancellationDispatcher;
    private readonly Dictionary<string, int> _childrenByParent =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChildAgentLineage>
        _rememberedLineages = new(StringComparer.Ordinal);
    private readonly Queue<string> _rememberedLineageOrder = new();
    private readonly object _parentSync = new();
    private readonly object _lineageSync = new();
    private readonly object _lifecycleSync = new();
    private TaskCompletionSource<bool>? _idle;
    private Task? _shutdownCancellationTask;
    private int _activeOperations;
    private int _closed;
    private int _resourcesDisposed;

    public ChildAgentSupervisor(
        IDurableAgentRuntime runtime,
        ChildAgentSupervisorOptions? options = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = (options ?? new ChildAgentSupervisorOptions()).Snapshot();
        _slots = new SemaphoreSlim(
            _options.MaxConcurrentChildren,
            _options.MaxConcurrentChildren);
        _childCancellationDispatcher = new BoundedCancellationDispatcher(
            _options.MaxConcurrentChildren);
    }

    public int ActiveChildCount => _activeChildren.Count;

    public IReadOnlyList<ChildAgentLineage> ActiveLineages =>
        new ReadOnlyCollection<ChildAgentLineage>(
            _activeLineages.Values
                .OrderBy(item => item.ChildRunId, StringComparer.Ordinal)
                .ToArray());

    public async ValueTask<ChildAgentRunResult> RunChildAsync(
        string parentRunId,
        DurableRunRequest request,
        CancellationToken cancellationToken = default)
    {
        parentRunId = RuntimeGuard.RequiredId(
            parentRunId,
            nameof(parentRunId));
        if (request is null || request.Run is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var requestSnapshot = DurableRunRequestSnapshotter.Snapshot(
            request,
            cancellationToken);

        return await RunChildCoreAsync(
                parentRunId,
                explicitParentLineage: null,
                requestSnapshot,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a child using lineage persisted on a completed parent run. Use
    /// this overload when delegation continues after the parent is no longer
    /// active in this supervisor.
    /// </summary>
    public async ValueTask<ChildAgentRunResult> RunChildAsync(
        AgentRun parentRun,
        DurableRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (parentRun is null)
        {
            throw new ArgumentNullException(nameof(parentRun));
        }

        var parentRunId = RuntimeGuard.RequiredId(
            parentRun.RunId,
            nameof(parentRun));
        var parentLineage = ChildAgentLineage.Read(parentRun);
        if (request is null || request.Run is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var requestSnapshot = DurableRunRequestSnapshotter.Snapshot(
            request,
            cancellationToken);

        return await RunChildCoreAsync(
                parentRunId,
                parentLineage,
                requestSnapshot,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ChildAgentRunResult> RunChildCoreAsync(
        string parentRunId,
        ChildAgentLineage? explicitParentLineage,
        DurableRunRequest request,
        CancellationToken cancellationToken)
    {

        EnterOperation();
        try
        {
            using var timeout = new CancellationTokenSource(
                _options.ChildTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token,
                timeout.Token);
            await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                return await RunAdmittedChildAsync(
                        parentRunId,
                        explicitParentLineage,
                        request,
                        timeout.Token,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public async ValueTask<ChildAgentBatchResult> RunManyAsync(
        string parentRunId,
        IEnumerable<DurableRunRequest> requests,
        CancellationToken cancellationToken = default)
    {
        parentRunId = RuntimeGuard.RequiredId(
            parentRunId,
            nameof(parentRunId));
        if (requests is null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        return await RunManyCoreAsync(
                parentRunId,
                explicitParentLineage: null,
                requests,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a batch using lineage persisted on a completed parent run.
    /// </summary>
    public async ValueTask<ChildAgentBatchResult> RunManyAsync(
        AgentRun parentRun,
        IEnumerable<DurableRunRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (parentRun is null)
        {
            throw new ArgumentNullException(nameof(parentRun));
        }

        var parentRunId = RuntimeGuard.RequiredId(
            parentRun.RunId,
            nameof(parentRun));
        var parentLineage = ChildAgentLineage.Read(parentRun);
        if (requests is null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        return await RunManyCoreAsync(
                parentRunId,
                parentLineage,
                requests,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ChildAgentBatchResult> RunManyCoreAsync(
        string parentRunId,
        ChildAgentLineage? explicitParentLineage,
        IEnumerable<DurableRunRequest> requests,
        CancellationToken cancellationToken)
    {

        var snapshot = new List<DurableRunRequest>();
        foreach (var request in requests)
        {
            if (request is null
                || snapshot.Count >= _options.MaxChildrenPerBatch)
            {
                throw new ArgumentException(
                    "The child-agent request batch is invalid.",
                    nameof(requests));
            }

            snapshot.Add(
                DurableRunRequestSnapshotter.Snapshot(
                    request,
                    cancellationToken));
        }

        var tasks = snapshot
            .Select(
                (request, index) => RunBatchItemAsync(
                    parentRunId,
                    explicitParentLineage,
                    request,
                    index,
                    cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        Array.Sort(results, (left, right) => left.Index.CompareTo(right.Index));
        return new ChildAgentBatchResult(
            new ReadOnlyCollection<ChildAgentBatchItemResult>(results));
    }

    public int CancelChildren(string parentRunId)
    {
        parentRunId = RuntimeGuard.RequiredId(
            parentRunId,
            nameof(parentRunId));
        var cancelled = 0;
        foreach (var pair in _activeLineages)
        {
            if (string.Equals(
                    pair.Value.ParentRunId,
                    parentRunId,
                    StringComparison.Ordinal)
                && _activeChildren.TryGetValue(
                    pair.Key,
                    out var cancellation)
                && cancellation.TryCancel())
            {
                cancelled++;
            }
        }

        return cancelled;
    }

    public async ValueTask<bool> StopAsync()
    {
        Task drain;
        Task cancellation;
        lock (_lifecycleSync)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _shutdownCancellationTask =
                    CancelIsolatedAsync(_shutdown);
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
        var drained = ReferenceEquals(completed, all);
        if (drained)
        {
            await all.ConfigureAwait(false);
        }

        return drained;
    }

    private static async Task CancelIsolatedAsync(
        CancellationTokenSource cancellation)
    {
        await Task.Run(
                () =>
                {
                    try
                    {
                        cancellation.Cancel();
                    }
                    catch (Exception exception)
                        when (exception is not OutOfMemoryException
                              and not StackOverflowException)
                    {
                        // Cancellation callbacks belong to host/provider
                        // extensions and cannot be allowed to block callers.
                    }
                })
            .ConfigureAwait(false);
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
            drain = _activeOperations == 0
                ? Task.CompletedTask
                : (_idle ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await Task.WhenAll(cancellation, drain).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            _slots.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task<ChildAgentRunResult> RunAdmittedChildAsync(
        string parentRunId,
        ChildAgentLineage? explicitParentLineage,
        DurableRunRequest request,
        CancellationToken timeoutCancellation,
        CancellationToken callerCancellation)
    {
        var parentLineage = explicitParentLineage
                            ?? ResolveRememberedLineage(parentRunId);
        if (parentLineage is not null
            && !string.Equals(
                parentLineage.ChildRunId,
                parentRunId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The supplied parent lineage does not match the parent run.");
        }

        var depth = checked((parentLineage?.Depth ?? 0) + 1);
        if (depth > _options.MaxDepth)
        {
            throw new InvalidOperationException(
                "The child-agent depth limit was exceeded.");
        }

        var childRunId = RuntimeGuard.RequiredId(
            request.Run.RunId,
            nameof(request));
        if (IsRememberedLineage(childRunId))
        {
            throw new InvalidOperationException(
                "The child run was already supervised.");
        }

        if (!TryAdmitParent(parentRunId))
        {
            throw new InvalidOperationException(
                "The parent child-agent concurrency limit was exceeded.");
        }

        var lineage = new ChildAgentLineage(
            parentLineage?.RootRunId ?? parentRunId,
            parentRunId,
            childRunId,
            depth);
        if (!_childCancellationDispatcher.TryReserve(
                out var cancellationReservation))
        {
            DecrementParent(parentRunId);
            throw new InvalidOperationException(
                "Child-agent cancellation capacity is exhausted.");
        }

        using var executionCancellation = new CancellationTokenSource();
        var activeCancellation = new ActiveChildCancellation(
            executionCancellation,
            cancellationReservation!);
        var lineageAdded = _activeLineages.TryAdd(childRunId, lineage);
        var cancellationAdded = lineageAdded
                                && _activeChildren.TryAdd(
                                    childRunId,
                                    activeCancellation);
        if (!lineageAdded || !cancellationAdded)
        {
            if (lineageAdded)
            {
                ((ICollection<KeyValuePair<string, ChildAgentLineage>>)
                    _activeLineages).Remove(
                    new KeyValuePair<string, ChildAgentLineage>(
                        childRunId,
                        lineage));
            }

            if (cancellationAdded)
            {
                ((ICollection<KeyValuePair<string, ActiveChildCancellation>>)
                    _activeChildren).Remove(
                    new KeyValuePair<string, ActiveChildCancellation>(
                        childRunId,
                        activeCancellation));
            }

            await activeCancellation.DisposeAsync().ConfigureAwait(false);
            DecrementParent(parentRunId);
            throw new InvalidOperationException(
                "The child run is already supervised.");
        }

        using var callerRegistration = callerCancellation.Register(
            static state => ((ActiveChildCancellation)state!).TryCancel(),
            activeCancellation);
        using var shutdownRegistration = _shutdown.Token.Register(
            static state => ((ActiveChildCancellation)state!).TryCancel(),
            activeCancellation);
        using var timeoutRegistration = timeoutCancellation.Register(
            static state => ((ActiveChildCancellation)state!).TryCancel(),
            activeCancellation);
        try
        {
            var snapshot = SnapshotRequest(request, lineage);
            var outcome = await _runtime.RunAsync(
                    snapshot,
                    executionCancellation.Token)
                .ConfigureAwait(false);
            if (!TryRememberLineage(lineage))
            {
                throw new InvalidOperationException(
                    "The child run was already supervised.");
            }

            return new ChildAgentRunResult(lineage, outcome);
        }
        catch (OperationCanceledException)
            when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            ((ICollection<KeyValuePair<string, ActiveChildCancellation>>)
                _activeChildren).Remove(
                new KeyValuePair<string, ActiveChildCancellation>(
                    childRunId,
                    activeCancellation));
            _activeLineages.TryRemove(childRunId, out _);
            DecrementParent(parentRunId);
            await activeCancellation.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ActiveChildCancellation : IAsyncDisposable
    {
        private readonly CancellationTokenSource _source;
        private readonly BoundedCancellationDispatcher
            .CancellationDispatchReservation _reservation;
        private readonly object _sync = new();
        private Task? _dispatch;
        private bool _disposed;

        public ActiveChildCancellation(
            CancellationTokenSource source,
            BoundedCancellationDispatcher.CancellationDispatchReservation
                reservation)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _reservation = reservation
                ?? throw new ArgumentNullException(nameof(reservation));
        }

        public bool TryCancel()
        {
            lock (_sync)
            {
                if (_disposed || _dispatch is not null)
                {
                    return false;
                }

                var accepted = _reservation.TryDispatch(
                    _source,
                    out var dispatch);
                _dispatch = dispatch;
                return accepted;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task? dispatch;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                dispatch = _dispatch;
            }

            try
            {
                if (dispatch is not null)
                {
                    await dispatch.ConfigureAwait(false);
                }
            }
            finally
            {
                _reservation.Dispose();
            }
        }
    }

    private async Task<ChildAgentBatchItemResult> RunBatchItemAsync(
        string parentRunId,
        ChildAgentLineage? explicitParentLineage,
        DurableRunRequest request,
        int index,
        CancellationToken cancellationToken)
    {
        var runId = request.Run?.RunId ?? string.Empty;
        try
        {
            var result = await RunChildCoreAsync(
                    parentRunId,
                    explicitParentLineage,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChildAgentBatchItemResult(
                index,
                result.Lineage.ChildRunId,
                result,
                errorType: null);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            return new ChildAgentBatchItemResult(
                index,
                runId,
                result: null,
                exception.GetType().FullName);
        }
    }

    private static DurableRunRequest SnapshotRequest(
        DurableRunRequest source,
        ChildAgentLineage lineage)
    {
        var run = ProtocolJson.DeserializeAgentRun(
            ProtocolJson.Serialize(source.Run));
        run.Extensions[ChildAgentLineage.ExtensionName] = lineage.ToJson();
        return new DurableRunRequest
        {
            Run = run,
            Context = source.Context,
            ActiveSkills = source.ActiveSkills,
            InitialTranscript = source.InitialTranscript,
            LaneId = source.LaneId,
            WorkloadClass = source.WorkloadClass,
            ExecutionMode = source.ExecutionMode,
            Inference = source.Inference?.CloneValidated(),
            RoutePreference = source.RoutePreference?.CloneValidated(),
            FinalOutputContract = source.FinalOutputContract
        };
    }

    private void EnterOperation()
    {
        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(ChildAgentSupervisor));
            }

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

    private void DecrementParent(string parentRunId)
    {
        lock (_parentSync)
        {
            if (!_childrenByParent.TryGetValue(
                    parentRunId,
                    out var current))
            {
                return;
            }

            if (current <= 1)
            {
                _childrenByParent.Remove(parentRunId);
            }
            else
            {
                _childrenByParent[parentRunId] = current - 1;
            }
        }
    }

    private bool TryAdmitParent(string parentRunId)
    {
        lock (_parentSync)
        {
            _childrenByParent.TryGetValue(parentRunId, out var current);
            if (current >= _options.MaxActiveChildrenPerParent)
            {
                return false;
            }

            _childrenByParent[parentRunId] = checked(current + 1);
            return true;
        }
    }

    private ChildAgentLineage? ResolveRememberedLineage(string runId)
    {
        if (_activeLineages.TryGetValue(runId, out var active))
        {
            return active;
        }

        lock (_lineageSync)
        {
            return _rememberedLineages.TryGetValue(runId, out var remembered)
                ? remembered
                : null;
        }
    }

    private bool TryRememberLineage(ChildAgentLineage lineage)
    {
        lock (_lineageSync)
        {
            if (_rememberedLineages.ContainsKey(lineage.ChildRunId))
            {
                return false;
            }

            _rememberedLineages.Add(lineage.ChildRunId, lineage);
            _rememberedLineageOrder.Enqueue(lineage.ChildRunId);
            while (_rememberedLineages.Count
                   > _options.MaxRememberedLineages)
            {
                var expired = _rememberedLineageOrder.Dequeue();
                _rememberedLineages.Remove(expired);
            }

            return true;
        }
    }

    private bool IsRememberedLineage(string childRunId)
    {
        lock (_lineageSync)
        {
            return _rememberedLineages.ContainsKey(childRunId);
        }
    }
}

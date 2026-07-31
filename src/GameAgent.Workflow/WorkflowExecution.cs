using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.Workflow;

public interface IWorkflowClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemWorkflowClock : IWorkflowClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class WorkflowRunnerOptions
{
    public WorkflowRunnerOptions(
        TimeSpan? leaseDuration = null,
        int maxSchedulerPasses = 100_000)
    {
        LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(30);
        if (LeaseDuration < TimeSpan.FromMilliseconds(300)
            || LeaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (maxSchedulerPasses is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSchedulerPasses));
        }

        MaxSchedulerPasses = maxSchedulerPasses;
    }

    public TimeSpan LeaseDuration { get; }

    public int MaxSchedulerPasses { get; }

    internal TimeSpan HeartbeatInterval =>
        TimeSpan.FromMilliseconds(
            Math.Max(100, LeaseDuration.TotalMilliseconds / 3));
}

public sealed class WorkflowRunRequest
{
    public WorkflowRunRequest(
        string runKey,
        string ownerId,
        JsonElement input)
    {
        RunKey = WorkflowValidation.RequiredIdentifier(
            runKey,
            nameof(runKey),
            256,
            allowSlash: true);
        OwnerId = WorkflowValidation.RequiredIdentifier(
            ownerId,
            nameof(ownerId),
            128,
            allowSlash: true);
        Input = input.Clone();
    }

    public string RunKey { get; }

    public string OwnerId { get; }

    public JsonElement Input { get; }
}

public sealed class WorkflowStepResult
{
    private WorkflowStepResult(
        bool succeeded,
        JsonElement? output,
        string? reasonCode)
    {
        Succeeded = succeeded;
        Output = output?.Clone();
        ReasonCode = reasonCode;
    }

    public bool Succeeded { get; }

    public JsonElement? Output { get; }

    public string? ReasonCode { get; }

    public static WorkflowStepResult Completed(JsonElement output)
    {
        if (output.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "A completed step must return defined JSON.",
                nameof(output));
        }

        return new WorkflowStepResult(true, output, null);
    }

    public static WorkflowStepResult Failed(string reasonCode)
    {
        return new WorkflowStepResult(
            false,
            null,
            WorkflowValidation.RequiredIdentifier(
                reasonCode,
                nameof(reasonCode),
                128,
                allowSlash: false));
    }
}

public sealed class WorkflowExecutorInterruptedException : Exception
{
    public WorkflowExecutorInterruptedException(string message)
        : base(message)
    {
    }
}

public sealed class WorkflowStepContext
{
    private readonly Func<JsonElement, CancellationToken, ValueTask<bool>>
        _checkpoint;

    internal WorkflowStepContext(
        string runId,
        string workflowId,
        string stageId,
        string instanceId,
        int attempt,
        int generation,
        bool isRecovery,
        JsonElement settings,
        JsonElement? checkpoint,
        Func<JsonElement, CancellationToken, ValueTask<bool>> checkpointWriter)
    {
        RunId = runId;
        WorkflowId = workflowId;
        StageId = stageId;
        InstanceId = instanceId;
        Attempt = attempt;
        Generation = generation;
        IsRecovery = isRecovery;
        Settings = settings.Clone();
        Checkpoint = checkpoint?.Clone();
        _checkpoint = checkpointWriter;
    }

    public string RunId { get; }

    public string WorkflowId { get; }

    public string StageId { get; }

    public string InstanceId { get; }

    public int Attempt { get; }

    public int Generation { get; }

    public bool IsRecovery { get; }

    public JsonElement Settings { get; }

    public JsonElement? Checkpoint { get; }

    public ValueTask<bool> SaveCheckpointAsync(
        JsonElement checkpoint,
        CancellationToken cancellationToken = default)
    {
        if (checkpoint.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "A checkpoint must contain defined JSON.",
                nameof(checkpoint));
        }

        return _checkpoint(checkpoint.Clone(), cancellationToken);
    }
}

public interface IWorkflowStepExecutor
{
    string Kind { get; }

    ValueTask<WorkflowStepResult> ExecuteAsync(
        WorkflowStepContext context,
        JsonElement input,
        CancellationToken cancellationToken);

    ValueTask<WorkflowStepResult> RecoverAsync(
        WorkflowStepContext context,
        JsonElement input,
        CancellationToken cancellationToken);
}

public sealed class WorkflowStepExecutorRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IWorkflowStepExecutor> _executors =
        new(StringComparer.Ordinal);

    public WorkflowStepExecutorRegistry(
        IEnumerable<IWorkflowStepExecutor>? executors = null)
    {
        if (executors is null)
        {
            return;
        }

        foreach (var executor in WorkflowCollections.MaterializeBounded(
                     executors,
                     1_024,
                     nameof(executors)))
        {
            Register(executor);
        }
    }

    public void Register(IWorkflowStepExecutor executor)
    {
        if (executor is null)
        {
            throw new ArgumentNullException(nameof(executor));
        }

        var kind = WorkflowValidation.RequiredIdentifier(
            executor.Kind,
            nameof(executor),
            128,
            allowSlash: true);
        lock (_gate)
        {
            if (!_executors.TryAdd(kind, executor))
            {
                throw new ArgumentException(
                    $"Executor kind '{kind}' is already registered.",
                    nameof(executor));
            }
        }
    }

    public bool TryGet(string kind, out IWorkflowStepExecutor? executor)
    {
        lock (_gate)
        {
            return _executors.TryGetValue(kind, out executor);
        }
    }
}

public sealed class WorkflowRunner
{
    private readonly IWorkflowRunStore _store;
    private readonly WorkflowStepExecutorRegistry _executors;
    private readonly IWorkflowClock _clock;
    private readonly WorkflowRunnerOptions _options;

    public WorkflowRunner(
        IWorkflowRunStore store,
        WorkflowStepExecutorRegistry executors,
        IWorkflowClock? clock = null,
        WorkflowRunnerOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _executors = executors
            ?? throw new ArgumentNullException(nameof(executors));
        _clock = clock ?? new SystemWorkflowClock();
        _options = options ?? new WorkflowRunnerOptions();
    }

    public async ValueTask<WorkflowRunSnapshot> ExecuteAsync(
        CompiledWorkflow workflow,
        WorkflowRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!WorkflowSchema.TryValidateValue(
                workflow.Definition.InputSchema,
                request.Input,
                workflow.Definition.Limits.MaxInputBytes,
                out var inputReason))
        {
            throw new WorkflowRunException(
                inputReason,
                "Workflow input does not satisfy its closed schema.");
        }

        var inputDigest = WorkflowIdentity.ComputeJsonDigest(request.Input);
        var runId = WorkflowIdentity.CreateRunId(
            workflow.DefinitionDigest,
            inputDigest,
            request.RunKey);
        var timestamp = _clock.UtcNow;
        var rootInstances = workflow.Stages.Select(stage =>
            new WorkflowStageInstanceSnapshot(
                WorkflowIdentity.CreateStageInstanceId(
                    runId,
                    stage.Definition.Id),
                stage.Definition.Id,
                WorkflowInstanceKind.Stage,
                null,
                null,
                null,
                null,
                timestamp));
        var proposed = new WorkflowRunSnapshot(
            runId,
            workflow.Definition.Id,
            workflow.Definition.Version,
            workflow.DefinitionDigest,
            request.Input,
            inputDigest,
            timestamp,
            rootInstances);
        var created = await _store
            .CreateAsync(proposed, cancellationToken)
            .ConfigureAwait(false);
        if (created.Status == WorkflowCreateStatus.CapacityExceeded)
        {
            throw new WorkflowRunException(
                WorkflowReasonCodes.StoreCapacityExceeded,
                "The workflow run store is at capacity.");
        }

        var existing = created.Snapshot
                       ?? throw new WorkflowRunException(
                           WorkflowReasonCodes.RunNotFound,
                           "The workflow run could not be created.");
        EnsureRunMatches(workflow, existing, inputDigest);
        return await DriveOwnedAsync(
                workflow,
                existing.RunId,
                request.OwnerId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorkflowRunSnapshot> RecoverAsync(
        CompiledWorkflow workflow,
        string runId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        WorkflowValidation.RequiredIdentifier(
            runId,
            nameof(runId),
            80,
            allowSlash: false);
        WorkflowValidation.RequiredIdentifier(
            ownerId,
            nameof(ownerId),
            128,
            allowSlash: true);
        var run = await _store
            .ReadAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            throw new WorkflowRunException(
                WorkflowReasonCodes.RunNotFound,
                "The workflow run does not exist.");
        }

        EnsureRunMatches(workflow, run, run.InputDigest);
        if (run.IsTerminal)
        {
            return run;
        }

        return await DriveOwnedAsync(
                workflow,
                runId,
                ownerId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<WorkflowRunSnapshot> DriveOwnedAsync(
        CompiledWorkflow workflow,
        string runId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        var current = await _store
            .ReadAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new WorkflowRunException(
                WorkflowReasonCodes.RunNotFound,
                "The workflow run does not exist.");
        }

        if (current.IsTerminal)
        {
            return current;
        }

        var acquired = await _store
            .TryAcquireLeaseAsync(
                runId,
                ownerId,
                _options.LeaseDuration,
                _clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        if (acquired.Status == WorkflowLeaseAcquireStatus.Terminal)
        {
            return await RequireRunAsync(runId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (acquired.Status != WorkflowLeaseAcquireStatus.Acquired
            || acquired.Token is null)
        {
            throw new WorkflowRunException(
                WorkflowReasonCodes.LeaseUnavailable,
                "The workflow run is owned by another live executor.");
        }

        var lease = acquired.Token;
        try
        {
            return await DriveAsync(
                    workflow,
                    runId,
                    lease,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await _store
                .ReleaseLeaseAsync(runId, lease, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<WorkflowRunSnapshot> DriveAsync(
        CompiledWorkflow workflow,
        string runId,
        WorkflowLeaseToken lease,
        CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < _options.MaxSchedulerPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _store
                    .RenewLeaseAsync(
                        runId,
                        lease,
                        _options.LeaseDuration,
                        _clock.UtcNow,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                throw LeaseLost();
            }

            var snapshot = await RequireRunAsync(runId, cancellationToken)
                .ConfigureAwait(false);
            EnsureRunMatches(workflow, snapshot, snapshot.InputDigest);
            if (snapshot.IsTerminal)
            {
                return snapshot;
            }

            var now = _clock.UtcNow;
            if (snapshot.CancellationRequested)
            {
                var cancelled = snapshot.Clone();
                MarkCancelled(cancelled, now);
                var committed = await CommitMutationAsync(
                        snapshot,
                        cancelled,
                        lease,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (committed is not null)
                {
                    return committed;
                }

                continue;
            }

            if ((now - snapshot.CreatedAt).TotalMilliseconds
                >= workflow.Definition.Limits.MaxDurationMs)
            {
                var expired = snapshot.Clone();
                MarkRunFailed(
                    expired,
                    WorkflowReasonCodes.LimitExceeded,
                    now);
                var committed = await CommitMutationAsync(
                        snapshot,
                        expired,
                        lease,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (committed is not null)
                {
                    return committed;
                }

                continue;
            }

            var next = snapshot.Clone();
            if (next.Status == WorkflowRunStatus.Pending)
            {
                next.Status = WorkflowRunStatus.Running;
            }

            var changed = AdvanceCompositeStages(workflow, next, now);
            changed |= FinalizeRunIfPossible(workflow, next, now);
            if (next.IsTerminal)
            {
                var terminal = await CommitMutationAsync(
                        snapshot,
                        next,
                        lease,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (terminal is not null)
                {
                    return terminal;
                }

                continue;
            }

            changed |= MaterializeReadyCompositeStages(workflow, next, now);
            changed |= AdvanceCompositeStages(workflow, next, now);
            changed |= FinalizeRunIfPossible(workflow, next, now);
            if (next.IsTerminal)
            {
                var terminal = await CommitMutationAsync(
                        snapshot,
                        next,
                        lease,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (terminal is not null)
                {
                    return terminal;
                }

                continue;
            }

            var descriptors = PrepareInvocationBatch(
                workflow,
                next,
                now,
                out var invocationChanged);
            changed |= invocationChanged;
            if (!changed)
            {
                MarkRunFailed(
                    next,
                    WorkflowReasonCodes.DefinitionInvalid,
                    now);
                changed = true;
            }

            var prepared = await CommitMutationAsync(
                    snapshot,
                    next,
                    lease,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (prepared is null)
            {
                continue;
            }

            if (descriptors.Count == 0)
            {
                if (prepared.IsTerminal)
                {
                    return prepared;
                }

                continue;
            }

            var invocationResults = await InvokeBatchWithHeartbeatAsync(
                    workflow,
                    runId,
                    lease,
                    descriptors,
                    cancellationToken)
                .ConfigureAwait(false);
            await ApplyInvocationResultsAsync(
                    workflow,
                    runId,
                    lease,
                    invocationResults,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new WorkflowRunException(
            WorkflowReasonCodes.LimitExceeded,
            "The workflow scheduler pass limit was exhausted.");
    }

    private bool AdvanceCompositeStages(
        CompiledWorkflow workflow,
        WorkflowRunSnapshot run,
        DateTimeOffset now)
    {
        var changed = false;
        foreach (var compiledStage in workflow.Stages)
        {
            var definition = compiledStage.Definition;
            var root = Root(run, definition.Id);
            if (root.Status != WorkflowStageStatus.Started)
            {
                continue;
            }

            if (definition.Kind == WorkflowStageKind.Foreach)
            {
                changed |= AdvanceForeach(
                    workflow.Definition.Limits,
                    definition,
                    root,
                    run,
                    now);
            }
            else if (definition.Kind == WorkflowStageKind.Loop)
            {
                changed |= AdvanceLoop(
                    workflow.Definition.Limits,
                    definition,
                    root,
                    run,
                    now);
            }
        }

        return changed;
    }

    private static bool AdvanceForeach(
        WorkflowLimits limits,
        WorkflowStageDefinition definition,
        WorkflowStageInstanceSnapshot root,
        WorkflowRunSnapshot run,
        DateTimeOffset now)
    {
        var children = Children(run, root.InstanceId)
            .OrderBy(item => item.ItemOrdinal)
            .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToArray();
        var failed = children.FirstOrDefault(item =>
            item.Status == WorkflowStageStatus.Failed);
        if (failed is not null)
        {
            FailInstance(
                root,
                failed.ReasonCode ?? WorkflowReasonCodes.ExecutorFailed,
                now);
            return true;
        }

        if (children.Any(item =>
                item.Status is WorkflowStageStatus.Pending
                    or WorkflowStageStatus.Started))
        {
            return false;
        }

        var output = WorkflowJson.BuildOutputArray(
            children.Select(item => item.Output!.Value));
        if (!WorkflowSchema.TryValidateValue(
                definition.OutputSchema,
                output,
                limits.MaxStageOutputBytes,
                out var reasonCode))
        {
            FailInstance(root, reasonCode, now);
            return true;
        }

        return CompleteInstance(root, output, run, limits, now);
    }

    private static bool AdvanceLoop(
        WorkflowLimits limits,
        WorkflowStageDefinition definition,
        WorkflowStageInstanceSnapshot root,
        WorkflowRunSnapshot run,
        DateTimeOffset now)
    {
        var loop = definition.Loop!;
        var children = Children(run, root.InstanceId)
            .OrderBy(item => item.LoopIteration)
            .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToArray();
        var failed = children.FirstOrDefault(item =>
            item.Status == WorkflowStageStatus.Failed);
        if (failed is not null)
        {
            FailInstance(
                root,
                failed.ReasonCode ?? WorkflowReasonCodes.ExecutorFailed,
                now);
            return true;
        }

        var completed = children.Count(item =>
            item.Status == WorkflowStageStatus.Completed);
        var changed = false;
        if (root.Cursor != completed)
        {
            root.Cursor = completed;
            root.UpdatedAt = now;
            changed = true;
        }

        if (children.Any(item =>
                item.Status is WorkflowStageStatus.Pending
                    or WorkflowStageStatus.Started))
        {
            return changed;
        }

        if (completed > 0)
        {
            var last = children[completed - 1];
            if (!WorkflowJson.TryResolvePointer(
                    last.Output!.Value,
                    loop.UntilPointer,
                    out var condition)
                || condition.ValueKind is not (
                    JsonValueKind.True or JsonValueKind.False))
            {
                FailInstance(
                    root,
                    WorkflowReasonCodes.LoopConditionInvalid,
                    now);
                return true;
            }

            if (condition.ValueKind == JsonValueKind.True)
            {
                if (!WorkflowSchema.TryValidateValue(
                        definition.OutputSchema,
                        last.Output.Value,
                        limits.MaxStageOutputBytes,
                        out var reasonCode))
                {
                    FailInstance(root, reasonCode, now);
                    return true;
                }

                CompleteInstance(
                    root,
                    last.Output.Value,
                    run,
                    limits,
                    now);
                return true;
            }
        }

        if (completed >= loop.MaxIterations)
        {
            FailInstance(
                root,
                WorkflowReasonCodes.LoopIterationLimit,
                now);
            return true;
        }

        var iterationInput = completed == 0
            ? root.Input!.Value
            : children[completed - 1].Output!.Value;
        if (!WorkflowSchema.TryValidateValue(
                loop.IterationInputSchema,
                iterationInput,
                limits.MaxInputBytes,
                out var inputReason))
        {
            FailInstance(root, inputReason, now);
            return true;
        }

        var childId = WorkflowIdentity.CreateLoopChildId(
            root.InstanceId,
            completed);
        if (run.StageInstances.Any(item =>
                string.Equals(
                    item.InstanceId,
                    childId,
                    StringComparison.Ordinal)))
        {
            FailInstance(
                root,
                WorkflowReasonCodes.DefinitionInvalid,
                now);
            return true;
        }

        var child = new WorkflowStageInstanceSnapshot(
            childId,
            definition.Id,
            WorkflowInstanceKind.LoopIteration,
            root.InstanceId,
            null,
            null,
            completed,
            now)
        {
            Input = iterationInput.Clone(),
            InputDigest = WorkflowIdentity.ComputeJsonDigest(iterationInput)
        };
        run.MutableStageInstances.Add(child);
        run.Usage.LoopIterations++;
        changed = true;
        return changed;
    }

    private bool MaterializeReadyCompositeStages(
        CompiledWorkflow workflow,
        WorkflowRunSnapshot run,
        DateTimeOffset now)
    {
        var changed = false;
        foreach (var compiled in workflow.Stages)
        {
            var definition = compiled.Definition;
            if (definition.Kind is not (
                    WorkflowStageKind.Foreach or WorkflowStageKind.Loop))
            {
                continue;
            }

            var root = Root(run, definition.Id);
            if (root.Status != WorkflowStageStatus.Pending
                || !DependenciesCompleted(run, compiled))
            {
                continue;
            }

            var input = BuildStageInput(run, compiled);
            if (!WorkflowSchema.TryValidateValue(
                    definition.InputSchema,
                    input,
                    workflow.Definition.Limits.MaxInputBytes,
                    out var reasonCode))
            {
                FailInstance(root, reasonCode, now);
                changed = true;
                continue;
            }

            root.Status = WorkflowStageStatus.Started;
            root.Input = input.Clone();
            root.InputDigest = WorkflowIdentity.ComputeJsonDigest(input);
            root.UpdatedAt = now;
            changed = true;
            if (definition.Kind == WorkflowStageKind.Foreach)
            {
                changed |= MaterializeForeach(
                    workflow.Definition.Limits,
                    definition,
                    root,
                    run,
                    input,
                    now);
            }
        }

        return changed;
    }

    private static bool MaterializeForeach(
        WorkflowLimits limits,
        WorkflowStageDefinition definition,
        WorkflowStageInstanceSnapshot root,
        WorkflowRunSnapshot run,
        JsonElement input,
        DateTimeOffset now)
    {
        var forEach = definition.ForEach!;
        if (!WorkflowJson.TryResolvePointer(
                input,
                forEach.SourcePointer,
                out var source)
            || source.ValueKind != JsonValueKind.Array)
        {
            FailInstance(
                root,
                WorkflowReasonCodes.ForeachSourceInvalid,
                now);
            return true;
        }

        var count = source.GetArrayLength();
        if (count > forEach.MaxItems
            || count > limits.MaxForeachItems)
        {
            FailInstance(root, WorkflowReasonCodes.LimitExceeded, now);
            return true;
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var prepared = new List<WorkflowStageInstanceSnapshot>(count);
        var ordinal = 0;
        foreach (var item in source.EnumerateArray())
        {
            if (!WorkflowSchema.TryValidateValue(
                    forEach.ItemInputSchema,
                    item,
                    limits.MaxInputBytes,
                    out var itemReason))
            {
                FailInstance(root, itemReason, now);
                return true;
            }

            if (!WorkflowJson.TryResolvePointer(
                    item,
                    forEach.ItemIdentityPointer,
                    out var identity)
                || identity.ValueKind is not (
                    JsonValueKind.String
                    or JsonValueKind.Number
                    or JsonValueKind.True
                    or JsonValueKind.False))
            {
                FailInstance(
                    root,
                    WorkflowReasonCodes.ForeachIdentityInvalid,
                    now);
                return true;
            }

            var identityDigest = WorkflowIdentity.ComputeJsonDigest(identity);
            if (!identities.Add(identityDigest))
            {
                FailInstance(
                    root,
                    WorkflowReasonCodes.ForeachIdentityCollision,
                    now);
                return true;
            }

            var childId = WorkflowIdentity.CreateForeachChildId(
                root.InstanceId,
                identityDigest);
            prepared.Add(new WorkflowStageInstanceSnapshot(
                childId,
                definition.Id,
                WorkflowInstanceKind.ForeachItem,
                root.InstanceId,
                identityDigest,
                ordinal,
                null,
                now)
            {
                Input = item.Clone(),
                InputDigest = WorkflowIdentity.ComputeJsonDigest(item)
            });
            ordinal++;
        }

        run.MutableStageInstances.AddRange(prepared);
        run.Usage.ForeachItems = checked(
            run.Usage.ForeachItems + prepared.Count);
        return true;
    }

    private IReadOnlyList<InvocationDescriptor> PrepareInvocationBatch(
        CompiledWorkflow workflow,
        WorkflowRunSnapshot run,
        DateTimeOffset now,
        out bool changed)
    {
        changed = false;
        var candidates = new List<InvocationCandidate>();
        foreach (var compiled in workflow.Stages)
        {
            var definition = compiled.Definition;
            var root = Root(run, definition.Id);
            if (definition.Kind is WorkflowStageKind.Step
                or WorkflowStageKind.Reduce)
            {
                if (root.Status == WorkflowStageStatus.Pending
                    && DependenciesCompleted(run, compiled))
                {
                    candidates.Add(new InvocationCandidate(
                        root,
                        compiled,
                        definition.Kind == WorkflowStageKind.Step
                            ? definition.Step!
                            : definition.Reduce!.Reducer,
                        definition.InputSchema,
                        definition.OutputSchema));
                }
                else if (root.Status == WorkflowStageStatus.Started)
                {
                    candidates.Add(new InvocationCandidate(
                        root,
                        compiled,
                        definition.Kind == WorkflowStageKind.Step
                            ? definition.Step!
                            : definition.Reduce!.Reducer,
                        definition.InputSchema,
                        definition.OutputSchema));
                }
            }
            else if (definition.Kind == WorkflowStageKind.Foreach
                     && root.Status == WorkflowStageStatus.Started)
            {
                foreach (var child in Children(run, root.InstanceId)
                             .Where(item => item.Status
                                 is WorkflowStageStatus.Pending
                                 or WorkflowStageStatus.Started)
                             .OrderBy(item => item.ItemOrdinal)
                             .ThenBy(
                                 item => item.InstanceId,
                                 StringComparer.Ordinal))
                {
                    candidates.Add(new InvocationCandidate(
                        child,
                        compiled,
                        definition.ForEach!.Body,
                        definition.ForEach.ItemInputSchema,
                        definition.ForEach.ItemOutputSchema));
                }
            }
            else if (definition.Kind == WorkflowStageKind.Loop
                     && root.Status == WorkflowStageStatus.Started)
            {
                foreach (var child in Children(run, root.InstanceId)
                             .Where(item => item.Status
                                 is WorkflowStageStatus.Pending
                                 or WorkflowStageStatus.Started)
                             .OrderBy(item => item.LoopIteration)
                             .ThenBy(
                                 item => item.InstanceId,
                                 StringComparer.Ordinal))
                {
                    candidates.Add(new InvocationCandidate(
                        child,
                        compiled,
                        definition.Loop!.Body,
                        definition.Loop.IterationInputSchema,
                        definition.Loop.IterationOutputSchema));
                }
            }
        }

        var descriptors = new List<InvocationDescriptor>();
        foreach (var candidate in candidates
                     .OrderBy(item => item.Stage.Ordinal)
                     .ThenBy(
                         item => item.Instance.ItemOrdinal
                                 ?? item.Instance.LoopIteration
                                 ?? -1)
                     .ThenBy(
                         item => item.Instance.InstanceId,
                         StringComparer.Ordinal)
                     .Take(workflow.Definition.Limits.MaxParallelism))
        {
            var instance = candidate.Instance;
            var isRecovery = instance.Status == WorkflowStageStatus.Started;
            if (instance.Attempt >= workflow.Definition.Limits.MaxStageAttempts
                || run.Usage.StageExecutions
                >= workflow.Definition.Limits.MaxStageExecutions)
            {
                FailInstance(
                    instance,
                    WorkflowReasonCodes.LimitExceeded,
                    now);
                changed = true;
                continue;
            }

            JsonElement input;
            if (instance.Input.HasValue)
            {
                input = instance.Input.Value;
            }
            else
            {
                input = BuildStageInput(run, candidate.Stage);
                if (!WorkflowSchema.TryValidateValue(
                        candidate.InputSchema,
                        input,
                        workflow.Definition.Limits.MaxInputBytes,
                        out var inputReason))
                {
                    FailInstance(instance, inputReason, now);
                    changed = true;
                    continue;
                }

                instance.Input = input.Clone();
                instance.InputDigest =
                    WorkflowIdentity.ComputeJsonDigest(input);
            }

            instance.Status = WorkflowStageStatus.Started;
            instance.Attempt++;
            instance.Generation++;
            instance.UpdatedAt = now;
            run.Usage.StageExecutions++;
            changed = true;
            if (isRecovery)
            {
                instance.RecoveryAttempts++;
                run.Usage.RecoveryCalls++;
            }
            else
            {
                run.Usage.ExecuteCalls++;
            }

            descriptors.Add(new InvocationDescriptor(
                candidate.Stage.Definition.Id,
                instance.InstanceId,
                instance.Attempt,
                instance.Generation,
                isRecovery,
                candidate.Step,
                input.Clone(),
                candidate.OutputSchema.Clone(),
                instance.Checkpoint?.Clone()));
        }

        return new ReadOnlyCollection<InvocationDescriptor>(descriptors);
    }

    private async ValueTask<IReadOnlyList<InvocationResult>>
        InvokeBatchWithHeartbeatAsync(
            CompiledWorkflow workflow,
            string runId,
            WorkflowLeaseToken lease,
            IReadOnlyList<InvocationDescriptor> descriptors,
            CancellationToken cancellationToken)
    {
        using var executionSignal =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var invocationTask = Task.WhenAll(descriptors.Select(descriptor =>
            InvokeOneAsync(
                workflow,
                runId,
                lease,
                descriptor,
                executionSignal.Token)));
        while (!invocationTask.IsCompleted)
        {
            var delay = Task.Delay(
                _options.HeartbeatInterval,
                cancellationToken);
            var completed = await Task
                .WhenAny(invocationTask, delay)
                .ConfigureAwait(false);
            if (completed == invocationTask)
            {
                break;
            }

            if (!await _store
                    .RenewLeaseAsync(
                        runId,
                        lease,
                        _options.LeaseDuration,
                        _clock.UtcNow,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                executionSignal.Cancel();
                throw LeaseLost();
            }

            var latest = await RequireRunAsync(runId, cancellationToken)
                .ConfigureAwait(false);
            if (latest.CancellationRequested)
            {
                executionSignal.Cancel();
            }
        }

        try
        {
            return await invocationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<InvocationResult>();
        }
    }

    private async Task<InvocationResult> InvokeOneAsync(
        CompiledWorkflow workflow,
        string runId,
        WorkflowLeaseToken lease,
        InvocationDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (!_executors.TryGet(descriptor.Step.Kind, out var executor)
            || executor is null)
        {
            return InvocationResult.Failed(
                descriptor,
                WorkflowReasonCodes.ExecutorMissing);
        }

        var context = new WorkflowStepContext(
            runId,
            workflow.Definition.Id,
            descriptor.StageId,
            descriptor.InstanceId,
            descriptor.Attempt,
            descriptor.Generation,
            descriptor.IsRecovery,
            descriptor.Step.Settings,
            descriptor.Checkpoint,
            (checkpoint, token) => SaveCheckpointAsync(
                workflow,
                runId,
                lease,
                descriptor,
                checkpoint,
                token));
        try
        {
            var result = descriptor.IsRecovery
                ? await executor
                    .RecoverAsync(context, descriptor.Input, cancellationToken)
                    .ConfigureAwait(false)
                : await executor
                    .ExecuteAsync(context, descriptor.Input, cancellationToken)
                    .ConfigureAwait(false);
            if (result is null)
            {
                return InvocationResult.Failed(
                    descriptor,
                    WorkflowReasonCodes.ExecutorFailed);
            }

            if (!result.Succeeded)
            {
                return InvocationResult.Failed(
                    descriptor,
                    result.ReasonCode
                    ?? WorkflowReasonCodes.ExecutorFailed);
            }

            var outputReason = WorkflowReasonCodes.ExecutorFailed;
            if (!result.Output.HasValue
                || !WorkflowSchema.TryValidateValue(
                    descriptor.OutputSchema,
                    result.Output.Value,
                    workflow.Definition.Limits.MaxStageOutputBytes,
                    out outputReason))
            {
                return InvocationResult.Failed(
                    descriptor,
                    result.Output.HasValue
                        ? outputReason
                        : WorkflowReasonCodes.ExecutorFailed);
            }

            return InvocationResult.Completed(
                descriptor,
                result.Output.Value);
        }
        catch (WorkflowExecutorInterruptedException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InvocationResult.Failed(
                descriptor,
                WorkflowReasonCodes.ExecutorFailed);
        }
    }

    private async ValueTask<bool> SaveCheckpointAsync(
        CompiledWorkflow workflow,
        string runId,
        WorkflowLeaseToken lease,
        InvocationDescriptor descriptor,
        JsonElement checkpoint,
        CancellationToken cancellationToken)
    {
        if (WorkflowJson.MeasureUtf8(checkpoint)
            > workflow.Definition.Limits.MaxStageOutputBytes)
        {
            return false;
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var current = await RequireRunAsync(runId, cancellationToken)
                .ConfigureAwait(false);
            if (current.CancellationRequested || current.IsTerminal)
            {
                return false;
            }

            var instance = current.StageInstances.FirstOrDefault(item =>
                string.Equals(
                    item.InstanceId,
                    descriptor.InstanceId,
                    StringComparison.Ordinal));
            if (instance is null
                || instance.Status != WorkflowStageStatus.Started
                || instance.Generation != descriptor.Generation)
            {
                return false;
            }

            var next = current.Clone();
            var mutable = next.RequireInstance(descriptor.InstanceId);
            var previousBytes = mutable.Checkpoint.HasValue
                ? WorkflowJson.MeasureUtf8(mutable.Checkpoint.Value)
                : 0;
            var nextBytes = WorkflowJson.MeasureUtf8(checkpoint);
            var retainedDelta = nextBytes - previousBytes;
            if (retainedDelta > 0
                && next.Usage.RetainedOutputBytes
                > workflow.Definition.Limits.MaxRetainedOutputBytes
                - retainedDelta)
            {
                return false;
            }

            mutable.Checkpoint = checkpoint.Clone();
            mutable.CheckpointDigest =
                WorkflowIdentity.ComputeJsonDigest(checkpoint);
            mutable.UpdatedAt = _clock.UtcNow;
            next.Usage.RetainedOutputBytes += retainedDelta;
            var committed = await CommitMutationAsync(
                    current,
                    next,
                    lease,
                    _clock.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            if (committed is not null)
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask ApplyInvocationResultsAsync(
        CompiledWorkflow workflow,
        string runId,
        WorkflowLeaseToken lease,
        IReadOnlyList<InvocationResult> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var current = await RequireRunAsync(runId, cancellationToken)
                .ConfigureAwait(false);
            if (current.IsTerminal)
            {
                return;
            }

            var next = current.Clone();
            if (next.CancellationRequested)
            {
                MarkCancelled(next, _clock.UtcNow);
            }
            else
            {
                foreach (var result in results)
                {
                    var instance = next.StageInstances.FirstOrDefault(item =>
                        string.Equals(
                            item.InstanceId,
                            result.InstanceId,
                            StringComparison.Ordinal));
                    if (instance is null
                        || instance.Status != WorkflowStageStatus.Started
                        || instance.Generation != result.Generation)
                    {
                        continue;
                    }

                    var mutable = next.RequireInstance(result.InstanceId);
                    if (result.Succeeded && result.Output.HasValue)
                    {
                        CompleteInstance(
                            mutable,
                            result.Output.Value,
                            next,
                            workflow.Definition.Limits,
                            _clock.UtcNow);
                    }
                    else
                    {
                        FailInstance(
                            mutable,
                            result.ReasonCode
                            ?? WorkflowReasonCodes.ExecutorFailed,
                            _clock.UtcNow);
                    }
                }
            }

            var committed = await CommitMutationAsync(
                    current,
                    next,
                    lease,
                    _clock.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            if (committed is not null)
            {
                return;
            }
        }

        throw new WorkflowRunException(
            WorkflowReasonCodes.RevisionConflict,
            "Workflow results could not be committed after bounded retries.");
    }

    private static bool FinalizeRunIfPossible(
        CompiledWorkflow workflow,
        WorkflowRunSnapshot run,
        DateTimeOffset now)
    {
        var failed = run.StageInstances
            .Where(item => item.InstanceKind == WorkflowInstanceKind.Stage)
            .OrderBy(item => workflow.StagesById[item.StageId].Ordinal)
            .FirstOrDefault(item =>
                item.Status == WorkflowStageStatus.Failed);
        if (failed is not null)
        {
            MarkRunFailed(
                run,
                failed.ReasonCode ?? WorkflowReasonCodes.ExecutorFailed,
                now);
            return true;
        }

        var output = Root(run, workflow.Definition.OutputStageId);
        if (output.Status != WorkflowStageStatus.Completed
            || !output.Output.HasValue)
        {
            return false;
        }

        if (!WorkflowSchema.TryValidateValue(
                workflow.Definition.OutputSchema,
                output.Output.Value,
                workflow.Definition.Limits.MaxStageOutputBytes,
                out var reasonCode))
        {
            MarkRunFailed(run, reasonCode, now);
            return true;
        }

        var outputBytes = WorkflowJson.MeasureUtf8(output.Output.Value);
        if (run.Usage.RetainedOutputBytes
            > workflow.Definition.Limits.MaxRetainedOutputBytes
            - outputBytes)
        {
            MarkRunFailed(
                run,
                WorkflowReasonCodes.LimitExceeded,
                now);
            return true;
        }

        run.Output = output.Output.Value.Clone();
        run.OutputDigest =
            WorkflowIdentity.ComputeJsonDigest(output.Output.Value);
        run.Usage.RetainedOutputBytes += outputBytes;
        run.Status = WorkflowRunStatus.Completed;
        run.ReasonCode = null;
        run.UpdatedAt = now;
        return true;
    }

    private async ValueTask<WorkflowRunSnapshot?> CommitMutationAsync(
        WorkflowRunSnapshot current,
        WorkflowRunSnapshot replacement,
        WorkflowLeaseToken lease,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        replacement.Revision = checked(current.Revision + 1);
        replacement.UpdatedAt = now;
        var result = await _store
            .TryCommitAsync(
                current.RunId,
                current.Revision,
                lease,
                replacement,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Status switch
        {
            WorkflowCommitStatus.Committed => result.Snapshot,
            WorkflowCommitStatus.RevisionConflict => null,
            WorkflowCommitStatus.LeaseLost => throw LeaseLost(),
            WorkflowCommitStatus.NotFound => throw new WorkflowRunException(
                WorkflowReasonCodes.RunNotFound,
                "The workflow run disappeared from its store."),
            _ => throw new WorkflowRunException(
                WorkflowReasonCodes.DefinitionInvalid,
                "The workflow store rejected an invalid snapshot.")
        };
    }

    private static JsonElement BuildStageInput(
        WorkflowRunSnapshot run,
        CompiledWorkflowStage stage)
    {
        if (stage.DependencyOrder.Count == 0)
        {
            return run.Input.Clone();
        }

        if (stage.Definition.Kind == WorkflowStageKind.Reduce)
        {
            return WorkflowJson.BuildReduceInput(
                stage.DependencyOrder.Select(dependency =>
                {
                    var instance = Root(run, dependency);
                    return (
                        dependency,
                        instance.InstanceId,
                        instance.Output!.Value);
                }).ToArray());
        }

        if (stage.DependencyOrder.Count == 1)
        {
            return Root(run, stage.DependencyOrder[0])
                .Output!.Value
                .Clone();
        }

        return WorkflowJson.BuildDependencyObject(
            stage.DependencyOrder.Select(dependency =>
            {
                var instance = Root(run, dependency);
                return (dependency, instance.Output!.Value);
            }).ToArray());
    }

    private static bool DependenciesCompleted(
        WorkflowRunSnapshot run,
        CompiledWorkflowStage stage)
    {
        return stage.DependencyOrder.All(dependency =>
            Root(run, dependency).Status == WorkflowStageStatus.Completed);
    }

    private static WorkflowStageInstanceSnapshot Root(
        WorkflowRunSnapshot run,
        string stageId)
    {
        return run.StageInstances.First(item =>
            item.InstanceKind == WorkflowInstanceKind.Stage
            && string.Equals(
                item.StageId,
                stageId,
                StringComparison.Ordinal));
    }

    private static IEnumerable<WorkflowStageInstanceSnapshot> Children(
        WorkflowRunSnapshot run,
        string parentInstanceId)
    {
        return run.StageInstances.Where(item =>
            string.Equals(
                item.ParentInstanceId,
                parentInstanceId,
                StringComparison.Ordinal));
    }

    private static bool CompleteInstance(
        WorkflowStageInstanceSnapshot instance,
        JsonElement output,
        WorkflowRunSnapshot run,
        WorkflowLimits limits,
        DateTimeOffset now)
    {
        var bytes = WorkflowJson.MeasureUtf8(output);
        if (bytes > limits.MaxStageOutputBytes
            || run.Usage.RetainedOutputBytes
            > limits.MaxRetainedOutputBytes - bytes)
        {
            FailInstance(
                instance,
                WorkflowReasonCodes.LimitExceeded,
                now);
            return true;
        }

        instance.Output = output.Clone();
        instance.OutputDigest = WorkflowIdentity.ComputeJsonDigest(output);
        instance.Status = WorkflowStageStatus.Completed;
        instance.ReasonCode = null;
        instance.UpdatedAt = now;
        run.Usage.RetainedOutputBytes += bytes;
        return true;
    }

    private static void FailInstance(
        WorkflowStageInstanceSnapshot instance,
        string reasonCode,
        DateTimeOffset now)
    {
        instance.Status = WorkflowStageStatus.Failed;
        instance.ReasonCode = reasonCode;
        instance.UpdatedAt = now;
    }

    private static void MarkRunFailed(
        WorkflowRunSnapshot run,
        string reasonCode,
        DateTimeOffset now)
    {
        run.Status = WorkflowRunStatus.Failed;
        run.ReasonCode = reasonCode;
        run.UpdatedAt = now;
    }

    private static void MarkCancelled(
        WorkflowRunSnapshot run,
        DateTimeOffset now)
    {
        foreach (var instance in run.MutableStageInstances.Where(item =>
                     item.Status is WorkflowStageStatus.Pending
                         or WorkflowStageStatus.Started))
        {
            instance.Status = WorkflowStageStatus.Cancelled;
            instance.ReasonCode =
                WorkflowReasonCodes.CancellationRequested;
            instance.UpdatedAt = now;
        }

        run.Status = WorkflowRunStatus.Cancelled;
        run.ReasonCode = WorkflowReasonCodes.CancellationRequested;
        run.UpdatedAt = now;
    }

    private static void EnsureRunMatches(
        CompiledWorkflow workflow,
        WorkflowRunSnapshot run,
        string inputDigest)
    {
        if (!string.Equals(
                workflow.DefinitionDigest,
                run.DefinitionDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                workflow.Definition.Id,
                run.WorkflowId,
                StringComparison.Ordinal)
            || !string.Equals(
                workflow.Definition.Version,
                run.WorkflowVersion,
                StringComparison.Ordinal))
        {
            throw new WorkflowRunException(
                WorkflowReasonCodes.DefinitionMismatch,
                "The persisted run belongs to another workflow definition.");
        }

        if (!string.Equals(
                inputDigest,
                run.InputDigest,
                StringComparison.Ordinal))
        {
            throw new WorkflowRunException(
                WorkflowReasonCodes.InputMismatch,
                "The stable run identifier already has different input.");
        }
    }

    private async ValueTask<WorkflowRunSnapshot> RequireRunAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        return await _store
                   .ReadAsync(runId, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new WorkflowRunException(
                   WorkflowReasonCodes.RunNotFound,
                   "The workflow run does not exist.");
    }

    private static WorkflowRunException LeaseLost()
    {
        return new WorkflowRunException(
            WorkflowReasonCodes.LeaseLost,
            "The workflow run lease was lost or fenced.");
    }

    private sealed class InvocationCandidate
    {
        public InvocationCandidate(
            WorkflowStageInstanceSnapshot instance,
            CompiledWorkflowStage stage,
            WorkflowStepReference step,
            JsonElement inputSchema,
            JsonElement outputSchema)
        {
            Instance = instance;
            Stage = stage;
            Step = step;
            InputSchema = inputSchema;
            OutputSchema = outputSchema;
        }

        public WorkflowStageInstanceSnapshot Instance { get; }

        public CompiledWorkflowStage Stage { get; }

        public WorkflowStepReference Step { get; }

        public JsonElement InputSchema { get; }

        public JsonElement OutputSchema { get; }
    }

    private sealed class InvocationDescriptor
    {
        public InvocationDescriptor(
            string stageId,
            string instanceId,
            int attempt,
            int generation,
            bool isRecovery,
            WorkflowStepReference step,
            JsonElement input,
            JsonElement outputSchema,
            JsonElement? checkpoint)
        {
            StageId = stageId;
            InstanceId = instanceId;
            Attempt = attempt;
            Generation = generation;
            IsRecovery = isRecovery;
            Step = step;
            Input = input;
            OutputSchema = outputSchema;
            Checkpoint = checkpoint;
        }

        public string StageId { get; }

        public string InstanceId { get; }

        public int Attempt { get; }

        public int Generation { get; }

        public bool IsRecovery { get; }

        public WorkflowStepReference Step { get; }

        public JsonElement Input { get; }

        public JsonElement OutputSchema { get; }

        public JsonElement? Checkpoint { get; }
    }

    private sealed class InvocationResult
    {
        private InvocationResult(
            string instanceId,
            int generation,
            bool succeeded,
            JsonElement? output,
            string? reasonCode)
        {
            InstanceId = instanceId;
            Generation = generation;
            Succeeded = succeeded;
            Output = output?.Clone();
            ReasonCode = reasonCode;
        }

        public string InstanceId { get; }

        public int Generation { get; }

        public bool Succeeded { get; }

        public JsonElement? Output { get; }

        public string? ReasonCode { get; }

        public static InvocationResult Completed(
            InvocationDescriptor descriptor,
            JsonElement output)
        {
            return new InvocationResult(
                descriptor.InstanceId,
                descriptor.Generation,
                true,
                output,
                null);
        }

        public static InvocationResult Failed(
            InvocationDescriptor descriptor,
            string reasonCode)
        {
            return new InvocationResult(
                descriptor.InstanceId,
                descriptor.Generation,
                false,
                null,
                reasonCode);
        }
    }
}

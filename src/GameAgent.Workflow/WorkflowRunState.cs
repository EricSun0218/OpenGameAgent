using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Workflow;

public enum WorkflowRunStatus
{
    Pending = 0,
    Running = 1,
    CancelRequested = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

public enum WorkflowStageStatus
{
    Pending = 0,
    Started = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

public enum WorkflowInstanceKind
{
    Stage = 0,
    ForeachItem = 1,
    LoopIteration = 2
}

public sealed class WorkflowUsage
{
    public WorkflowUsage(
        int stageExecutions = 0,
        int executeCalls = 0,
        int recoveryCalls = 0,
        int foreachItems = 0,
        int loopIterations = 0,
        int retainedOutputBytes = 0)
    {
        StageExecutions = NonNegative(
            stageExecutions,
            nameof(stageExecutions));
        ExecuteCalls = NonNegative(executeCalls, nameof(executeCalls));
        RecoveryCalls = NonNegative(
            recoveryCalls,
            nameof(recoveryCalls));
        ForeachItems = NonNegative(foreachItems, nameof(foreachItems));
        LoopIterations = NonNegative(
            loopIterations,
            nameof(loopIterations));
        RetainedOutputBytes = NonNegative(
            retainedOutputBytes,
            nameof(retainedOutputBytes));
    }

    public int StageExecutions { get; internal set; }

    public int ExecuteCalls { get; internal set; }

    public int RecoveryCalls { get; internal set; }

    public int ForeachItems { get; internal set; }

    public int LoopIterations { get; internal set; }

    public int RetainedOutputBytes { get; internal set; }

    internal WorkflowUsage Clone()
    {
        return new WorkflowUsage(
            StageExecutions,
            ExecuteCalls,
            RecoveryCalls,
            ForeachItems,
            LoopIterations,
            RetainedOutputBytes);
    }

    private static int NonNegative(int value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }

        return value;
    }
}

public sealed class WorkflowLeaseToken
{
    public WorkflowLeaseToken(string ownerId, long fencingEpoch)
    {
        OwnerId = WorkflowValidation.RequiredIdentifier(
            ownerId,
            nameof(ownerId),
            128,
            allowSlash: true);
        if (fencingEpoch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fencingEpoch));
        }

        FencingEpoch = fencingEpoch;
    }

    public string OwnerId { get; }

    public long FencingEpoch { get; }
}

public sealed class WorkflowLeaseSnapshot
{
    public WorkflowLeaseSnapshot(
        string ownerId,
        long fencingEpoch,
        DateTimeOffset expiresAt)
    {
        OwnerId = WorkflowValidation.RequiredIdentifier(
            ownerId,
            nameof(ownerId),
            128,
            allowSlash: true);
        if (fencingEpoch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fencingEpoch));
        }

        FencingEpoch = fencingEpoch;
        ExpiresAt = expiresAt;
    }

    public string OwnerId { get; }

    public long FencingEpoch { get; }

    public DateTimeOffset ExpiresAt { get; internal set; }

    internal WorkflowLeaseSnapshot Clone()
    {
        return new WorkflowLeaseSnapshot(OwnerId, FencingEpoch, ExpiresAt);
    }

    internal bool Matches(WorkflowLeaseToken token)
    {
        return string.Equals(
                   OwnerId,
                   token.OwnerId,
                   StringComparison.Ordinal)
               && FencingEpoch == token.FencingEpoch;
    }
}

public sealed class WorkflowStageInstanceSnapshot
{
    internal WorkflowStageInstanceSnapshot(
        string instanceId,
        string stageId,
        WorkflowInstanceKind instanceKind,
        string? parentInstanceId,
        string? itemIdentityDigest,
        int? itemOrdinal,
        int? loopIteration,
        DateTimeOffset timestamp)
    {
        InstanceId = instanceId;
        StageId = stageId;
        InstanceKind = instanceKind;
        ParentInstanceId = parentInstanceId;
        ItemIdentityDigest = itemIdentityDigest;
        ItemOrdinal = itemOrdinal;
        LoopIteration = loopIteration;
        Status = WorkflowStageStatus.Pending;
        UpdatedAt = timestamp;
    }

    public string InstanceId { get; }

    public string StageId { get; }

    public WorkflowInstanceKind InstanceKind { get; }

    public string? ParentInstanceId { get; }

    public string? ItemIdentityDigest { get; }

    public int? ItemOrdinal { get; }

    public int? LoopIteration { get; }

    public WorkflowStageStatus Status { get; internal set; }

    public int Attempt { get; internal set; }

    public int Generation { get; internal set; }

    public int RecoveryAttempts { get; internal set; }

    public int Cursor { get; internal set; }

    public JsonElement? Input { get; internal set; }

    public string? InputDigest { get; internal set; }

    public JsonElement? Output { get; internal set; }

    public string? OutputDigest { get; internal set; }

    public JsonElement? Checkpoint { get; internal set; }

    public string? CheckpointDigest { get; internal set; }

    public string? ReasonCode { get; internal set; }

    public DateTimeOffset UpdatedAt { get; internal set; }

    public WorkflowStageInstanceSnapshot Copy()
    {
        return Clone();
    }

    public static WorkflowStageInstanceSnapshot Restore(
        string instanceId,
        string stageId,
        WorkflowInstanceKind instanceKind,
        string? parentInstanceId,
        string? itemIdentityDigest,
        int? itemOrdinal,
        int? loopIteration,
        WorkflowStageStatus status,
        int attempt,
        int generation,
        int recoveryAttempts,
        int cursor,
        JsonElement? input,
        string? inputDigest,
        JsonElement? output,
        string? outputDigest,
        JsonElement? checkpoint,
        string? checkpointDigest,
        string? reasonCode,
        DateTimeOffset updatedAt)
    {
        WorkflowValidation.RequiredIdentifier(
            instanceId,
            nameof(instanceId),
            80,
            allowSlash: false);
        WorkflowValidation.RequiredIdentifier(
            stageId,
            nameof(stageId),
            128,
            allowSlash: false);
        if (parentInstanceId is not null)
        {
            WorkflowValidation.RequiredIdentifier(
                parentInstanceId,
                nameof(parentInstanceId),
                80,
                allowSlash: false);
        }

        ValidateOptionalDigest(itemIdentityDigest, nameof(itemIdentityDigest));
        ValidateOptionalDigest(inputDigest, nameof(inputDigest));
        ValidateOptionalDigest(outputDigest, nameof(outputDigest));
        ValidateOptionalDigest(checkpointDigest, nameof(checkpointDigest));
        if (attempt < 0
            || generation < 0
            || recoveryAttempts < 0
            || cursor < 0
            || itemOrdinal < 0
            || loopIteration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        var shapeIsValid = instanceKind switch
        {
            WorkflowInstanceKind.Stage =>
                parentInstanceId is null
                && itemIdentityDigest is null
                && !itemOrdinal.HasValue
                && !loopIteration.HasValue,
            WorkflowInstanceKind.ForeachItem =>
                parentInstanceId is not null
                && itemIdentityDigest is not null
                && itemOrdinal.HasValue
                && !loopIteration.HasValue,
            WorkflowInstanceKind.LoopIteration =>
                parentInstanceId is not null
                && itemIdentityDigest is null
                && !itemOrdinal.HasValue
                && loopIteration.HasValue,
            _ => false
        };
        if (!shapeIsValid
            || status == WorkflowStageStatus.Completed && !output.HasValue
            || status == WorkflowStageStatus.Started && !input.HasValue)
        {
            throw new ArgumentException(
                "Persisted workflow instance metadata is inconsistent.");
        }

        if (input.HasValue != (inputDigest is not null)
            || output.HasValue != (outputDigest is not null)
            || checkpoint.HasValue != (checkpointDigest is not null))
        {
            throw new ArgumentException(
                "Persisted JSON values and their digests must be paired.");
        }

        if (input.HasValue
            && !string.Equals(
                CanonicalJsonDigest.ComputeSha256(input.Value),
                inputDigest,
                StringComparison.Ordinal)
            || output.HasValue
            && !string.Equals(
                CanonicalJsonDigest.ComputeSha256(output.Value),
                outputDigest,
                StringComparison.Ordinal)
            || checkpoint.HasValue
            && !string.Equals(
                CanonicalJsonDigest.ComputeSha256(checkpoint.Value),
                checkpointDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Persisted workflow instance digests do not match their JSON values.");
        }

        return new WorkflowStageInstanceSnapshot(
            instanceId,
            stageId,
            instanceKind,
            parentInstanceId,
            itemIdentityDigest,
            itemOrdinal,
            loopIteration,
            updatedAt)
        {
            Status = status,
            Attempt = attempt,
            Generation = generation,
            RecoveryAttempts = recoveryAttempts,
            Cursor = cursor,
            Input = input?.Clone(),
            InputDigest = inputDigest,
            Output = output?.Clone(),
            OutputDigest = outputDigest,
            Checkpoint = checkpoint?.Clone(),
            CheckpointDigest = checkpointDigest,
            ReasonCode = reasonCode,
            UpdatedAt = updatedAt
        };
    }

    internal WorkflowStageInstanceSnapshot Clone()
    {
        return new WorkflowStageInstanceSnapshot(
            InstanceId,
            StageId,
            InstanceKind,
            ParentInstanceId,
            ItemIdentityDigest,
            ItemOrdinal,
            LoopIteration,
            UpdatedAt)
        {
            Status = Status,
            Attempt = Attempt,
            Generation = Generation,
            RecoveryAttempts = RecoveryAttempts,
            Cursor = Cursor,
            Input = Input?.Clone(),
            InputDigest = InputDigest,
            Output = Output?.Clone(),
            OutputDigest = OutputDigest,
            Checkpoint = Checkpoint?.Clone(),
            CheckpointDigest = CheckpointDigest,
            ReasonCode = ReasonCode,
            UpdatedAt = UpdatedAt
        };
    }

    private static void ValidateOptionalDigest(string? value, string name)
    {
        if (value is not null && !CanonicalJsonDigest.IsSha256(value))
        {
            throw new ArgumentException(
                "The value must be a canonical SHA-256 digest.",
                name);
        }
    }
}

public sealed class WorkflowRunSnapshot
{
    private readonly List<WorkflowStageInstanceSnapshot> _stageInstances;
    private readonly ReadOnlyCollection<WorkflowStageInstanceSnapshot>
        _readOnlyStageInstances;

    internal WorkflowRunSnapshot(
        string runId,
        string workflowId,
        string workflowVersion,
        string definitionDigest,
        JsonElement input,
        string inputDigest,
        DateTimeOffset timestamp,
        IEnumerable<WorkflowStageInstanceSnapshot> stageInstances)
    {
        RunId = runId;
        WorkflowId = workflowId;
        WorkflowVersion = workflowVersion;
        DefinitionDigest = definitionDigest;
        Input = input.Clone();
        InputDigest = inputDigest;
        CreatedAt = timestamp;
        UpdatedAt = timestamp;
        Status = WorkflowRunStatus.Pending;
        Usage = new WorkflowUsage();
        _stageInstances = stageInstances
            .Select(item => item.Clone())
            .ToList();
        _readOnlyStageInstances =
            new ReadOnlyCollection<WorkflowStageInstanceSnapshot>(
                _stageInstances);
    }

    public string RunId { get; }

    public string WorkflowId { get; }

    public string WorkflowVersion { get; }

    public string DefinitionDigest { get; }

    public JsonElement Input { get; }

    public string InputDigest { get; }

    public long Revision { get; internal set; }

    public WorkflowRunStatus Status { get; internal set; }

    public string? ReasonCode { get; internal set; }

    public bool CancellationRequested { get; internal set; }

    public string? CancellationReason { get; internal set; }

    public JsonElement? Output { get; internal set; }

    public string? OutputDigest { get; internal set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; internal set; }

    public long FencingEpoch { get; internal set; }

    public WorkflowLeaseSnapshot? Lease { get; internal set; }

    public WorkflowUsage Usage { get; internal set; }

    public IReadOnlyList<WorkflowStageInstanceSnapshot> StageInstances =>
        _readOnlyStageInstances;

    internal List<WorkflowStageInstanceSnapshot> MutableStageInstances =>
        _stageInstances;

    public bool IsTerminal => Status is WorkflowRunStatus.Completed
        or WorkflowRunStatus.Failed
        or WorkflowRunStatus.Cancelled;

    public WorkflowRunSnapshot Copy()
    {
        return Clone();
    }

    public static WorkflowRunSnapshot Restore(
        string runId,
        string workflowId,
        string workflowVersion,
        string definitionDigest,
        JsonElement input,
        string inputDigest,
        long revision,
        WorkflowRunStatus status,
        string? reasonCode,
        bool cancellationRequested,
        string? cancellationReason,
        JsonElement? output,
        string? outputDigest,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long fencingEpoch,
        WorkflowLeaseSnapshot? lease,
        WorkflowUsage usage,
        IEnumerable<WorkflowStageInstanceSnapshot> stageInstances)
    {
        WorkflowValidation.RequiredIdentifier(
            runId,
            nameof(runId),
            80,
            allowSlash: false);
        WorkflowValidation.RequiredIdentifier(
            workflowId,
            nameof(workflowId),
            128,
            allowSlash: false);
        WorkflowValidation.RequiredIdentifier(
            workflowVersion,
            nameof(workflowVersion),
            64,
            allowSlash: false);
        RequireDigest(definitionDigest, nameof(definitionDigest));
        RequireDigest(inputDigest, nameof(inputDigest));
        if (!string.Equals(
                CanonicalJsonDigest.ComputeSha256(input),
                inputDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Persisted workflow input digest does not match its JSON value.",
                nameof(inputDigest));
        }

        if (output.HasValue != (outputDigest is not null))
        {
            throw new ArgumentException(
                "Persisted output and its digest must be paired.");
        }

        if (outputDigest is not null)
        {
            RequireDigest(outputDigest, nameof(outputDigest));
            if (!string.Equals(
                    CanonicalJsonDigest.ComputeSha256(output!.Value),
                    outputDigest,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Persisted workflow output digest does not match its JSON value.",
                    nameof(outputDigest));
            }
        }

        if (revision < 0
            || fencingEpoch < 0
            || updatedAt < createdAt
            || lease is not null
            && lease.FencingEpoch != fencingEpoch
            || status == WorkflowRunStatus.Completed && !output.HasValue
            || status != WorkflowRunStatus.Completed && output.HasValue)
        {
            throw new ArgumentException(
                "Persisted workflow run metadata is inconsistent.");
        }

        var instances = WorkflowCollections.MaterializeBounded(
            stageInstances
            ?? throw new ArgumentNullException(nameof(stageInstances)),
            1_000_000,
            nameof(stageInstances));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (instances.Any(item => item is null || !ids.Add(item.InstanceId)))
        {
            throw new ArgumentException(
                "Persisted workflow instance identifiers must be unique.",
                nameof(stageInstances));
        }

        if (instances.Any(item =>
                item.ParentInstanceId is not null
                && !ids.Contains(item.ParentInstanceId)))
        {
            throw new ArgumentException(
                "Persisted workflow child instances require a known parent.",
                nameof(stageInstances));
        }

        return new WorkflowRunSnapshot(
            runId,
            workflowId,
            workflowVersion,
            definitionDigest,
            input,
            inputDigest,
            createdAt,
            instances)
        {
            Revision = revision,
            Status = status,
            ReasonCode = reasonCode,
            CancellationRequested = cancellationRequested,
            CancellationReason = cancellationReason,
            Output = output?.Clone(),
            OutputDigest = outputDigest,
            UpdatedAt = updatedAt,
            FencingEpoch = fencingEpoch,
            Lease = lease?.Clone(),
            Usage = (usage
                     ?? throw new ArgumentNullException(nameof(usage))).Clone()
        };
    }

    internal WorkflowRunSnapshot Clone()
    {
        return new WorkflowRunSnapshot(
            RunId,
            WorkflowId,
            WorkflowVersion,
            DefinitionDigest,
            Input,
            InputDigest,
            CreatedAt,
            _stageInstances)
        {
            Revision = Revision,
            Status = Status,
            ReasonCode = ReasonCode,
            CancellationRequested = CancellationRequested,
            CancellationReason = CancellationReason,
            Output = Output?.Clone(),
            OutputDigest = OutputDigest,
            UpdatedAt = UpdatedAt,
            FencingEpoch = FencingEpoch,
            Lease = Lease?.Clone(),
            Usage = Usage.Clone()
        };
    }

    internal WorkflowStageInstanceSnapshot RequireInstance(string instanceId)
    {
        var result = _stageInstances.FirstOrDefault(item =>
            string.Equals(
                item.InstanceId,
                instanceId,
                StringComparison.Ordinal));
        return result
               ?? throw new InvalidOperationException(
                   $"Workflow instance '{instanceId}' is missing.");
    }

    private static void RequireDigest(string value, string name)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new ArgumentException(
                "The value must be a canonical SHA-256 digest.",
                name);
        }
    }
}

public sealed class WorkflowRunException : Exception
{
    public WorkflowRunException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

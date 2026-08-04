namespace GameAgent.Workflow;

public enum WorkflowCreateStatus
{
    Created = 0,
    AlreadyExists = 1,
    CapacityExceeded = 2
}

public sealed class WorkflowCreateResult
{
    public WorkflowCreateResult(
        WorkflowCreateStatus status,
        WorkflowRunSnapshot? snapshot)
    {
        if (status is WorkflowCreateStatus.Created
                or WorkflowCreateStatus.AlreadyExists
            ? snapshot is null
            : snapshot is not null)
        {
            throw new ArgumentException(
                "The create status and snapshot are inconsistent.",
                nameof(snapshot));
        }

        Status = status;
        Snapshot = snapshot;
    }

    public WorkflowCreateStatus Status { get; }

    public WorkflowRunSnapshot? Snapshot { get; }
}

public enum WorkflowCommitStatus
{
    Committed = 0,
    RevisionConflict = 1,
    LeaseLost = 2,
    NotFound = 3,
    InvalidSnapshot = 4
}

public sealed class WorkflowCommitResult
{
    public WorkflowCommitResult(
        WorkflowCommitStatus status,
        WorkflowRunSnapshot? snapshot)
    {
        if (status == WorkflowCommitStatus.NotFound
            ? snapshot is not null
            : snapshot is null)
        {
            throw new ArgumentException(
                "The commit status and snapshot are inconsistent.",
                nameof(snapshot));
        }

        Status = status;
        Snapshot = snapshot;
    }

    public WorkflowCommitStatus Status { get; }

    public WorkflowRunSnapshot? Snapshot { get; }
}

public enum WorkflowLeaseAcquireStatus
{
    Acquired = 0,
    Busy = 1,
    NotFound = 2,
    Terminal = 3
}

public sealed class WorkflowLeaseAcquireResult
{
    public WorkflowLeaseAcquireResult(
        WorkflowLeaseAcquireStatus status,
        WorkflowLeaseToken? token,
        DateTimeOffset? expiresAt)
    {
        if (status == WorkflowLeaseAcquireStatus.Acquired
            ? token is null || !expiresAt.HasValue
            : token is not null)
        {
            throw new ArgumentException(
                "The lease status and token are inconsistent.",
                nameof(token));
        }

        Status = status;
        Token = token;
        ExpiresAt = expiresAt;
    }

    public WorkflowLeaseAcquireStatus Status { get; }

    public WorkflowLeaseToken? Token { get; }

    public DateTimeOffset? ExpiresAt { get; }
}

public enum WorkflowCancelStatus
{
    Requested = 0,
    AlreadyRequested = 1,
    Terminal = 2,
    NotFound = 3
}

public sealed class WorkflowCancelResult
{
    public WorkflowCancelResult(
        WorkflowCancelStatus status,
        WorkflowRunSnapshot? snapshot)
    {
        if (status == WorkflowCancelStatus.NotFound
            ? snapshot is not null
            : snapshot is null)
        {
            throw new ArgumentException(
                "The cancellation status and snapshot are inconsistent.",
                nameof(snapshot));
        }

        Status = status;
        Snapshot = snapshot;
    }

    public WorkflowCancelStatus Status { get; }

    public WorkflowRunSnapshot? Snapshot { get; }
}

public interface IWorkflowRunStore
{
    ValueTask<WorkflowCreateResult> CreateAsync(
        WorkflowRunSnapshot snapshot,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowRunSnapshot?> ReadAsync(
        string runId,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowCommitResult> TryCommitAsync(
        string runId,
        long expectedRevision,
        WorkflowLeaseToken lease,
        WorkflowRunSnapshot replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowLeaseAcquireResult> TryAcquireLeaseAsync(
        string runId,
        string ownerId,
        TimeSpan duration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RenewLeaseAsync(
        string runId,
        WorkflowLeaseToken lease,
        TimeSpan duration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ReleaseLeaseAsync(
        string runId,
        WorkflowLeaseToken lease,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowCancelResult> RequestCancellationAsync(
        string runId,
        string reasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryWorkflowRunStore : IWorkflowRunStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkflowRunSnapshot> _runs =
        new(StringComparer.Ordinal);
    private readonly int _maxRuns;

    public InMemoryWorkflowRunStore(int maxRuns = 1_024)
    {
        if (maxRuns is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRuns));
        }

        _maxRuns = maxRuns;
    }

    public ValueTask<WorkflowCreateResult> CreateAsync(
        WorkflowRunSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        lock (_gate)
        {
            if (_runs.TryGetValue(snapshot.RunId, out var existing))
            {
                return Result(
                    new WorkflowCreateResult(
                        WorkflowCreateStatus.AlreadyExists,
                        existing.Clone()));
            }

            if (_runs.Count >= _maxRuns)
            {
                return Result(
                    new WorkflowCreateResult(
                        WorkflowCreateStatus.CapacityExceeded,
                        null));
            }

            var stored = snapshot.Clone();
            stored.Revision = 0;
            stored.FencingEpoch = 0;
            stored.Lease = null;
            _runs.Add(stored.RunId, stored);
            return Result(
                new WorkflowCreateResult(
                    WorkflowCreateStatus.Created,
                    stored.Clone()));
        }
    }

    public ValueTask<WorkflowRunSnapshot?> ReadAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRunId(runId);
        lock (_gate)
        {
            return new ValueTask<WorkflowRunSnapshot?>(
                _runs.TryGetValue(runId, out var value)
                    ? value.Clone()
                    : null);
        }
    }

    public ValueTask<WorkflowCommitResult> TryCommitAsync(
        string runId,
        long expectedRevision,
        WorkflowLeaseToken lease,
        WorkflowRunSnapshot replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRunId(runId);
        if (lease is null)
        {
            throw new ArgumentNullException(nameof(lease));
        }

        if (replacement is null)
        {
            throw new ArgumentNullException(nameof(replacement));
        }

        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var current))
            {
                return Commit(WorkflowCommitStatus.NotFound, null);
            }

            if (current.Revision != expectedRevision)
            {
                return Commit(
                    WorkflowCommitStatus.RevisionConflict,
                    current.Clone());
            }

            if (current.Lease is null
                || !current.Lease.Matches(lease)
                || current.Lease.ExpiresAt <= now)
            {
                return Commit(
                    WorkflowCommitStatus.LeaseLost,
                    current.Clone());
            }

            if (!HasSameIdentity(current, replacement)
                || replacement.Revision != expectedRevision + 1
                || !HasUniqueInstances(replacement))
            {
                return Commit(
                    WorkflowCommitStatus.InvalidSnapshot,
                    current.Clone());
            }

            var stored = replacement.Clone();
            stored.FencingEpoch = current.FencingEpoch;
            stored.Lease = current.Lease.Clone();
            _runs[runId] = stored;
            return Commit(
                WorkflowCommitStatus.Committed,
                stored.Clone());
        }
    }

    public ValueTask<WorkflowLeaseAcquireResult> TryAcquireLeaseAsync(
        string runId,
        string ownerId,
        TimeSpan duration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRunId(runId);
        WorkflowValidation.RequiredIdentifier(
            ownerId,
            nameof(ownerId),
            128,
            allowSlash: true);
        ValidateDuration(duration);

        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
            {
                return Lease(WorkflowLeaseAcquireStatus.NotFound, null, null);
            }

            if (run.IsTerminal)
            {
                return Lease(WorkflowLeaseAcquireStatus.Terminal, null, null);
            }

            if (run.Lease is not null
                && run.Lease.ExpiresAt > now
                && !string.Equals(
                    run.Lease.OwnerId,
                    ownerId,
                    StringComparison.Ordinal))
            {
                return Lease(
                    WorkflowLeaseAcquireStatus.Busy,
                    null,
                    run.Lease.ExpiresAt);
            }

            if (run.Lease is not null
                && run.Lease.ExpiresAt > now
                && string.Equals(
                    run.Lease.OwnerId,
                    ownerId,
                    StringComparison.Ordinal))
            {
                run.Lease.ExpiresAt = now + duration;
                var existingToken = new WorkflowLeaseToken(
                    ownerId,
                    run.Lease.FencingEpoch);
                return Lease(
                    WorkflowLeaseAcquireStatus.Acquired,
                    existingToken,
                    run.Lease.ExpiresAt);
            }

            run.FencingEpoch = checked(run.FencingEpoch + 1);
            run.Lease = new WorkflowLeaseSnapshot(
                ownerId,
                run.FencingEpoch,
                now + duration);
            var token = new WorkflowLeaseToken(ownerId, run.FencingEpoch);
            return Lease(
                WorkflowLeaseAcquireStatus.Acquired,
                token,
                run.Lease.ExpiresAt);
        }
    }

    public ValueTask<bool> RenewLeaseAsync(
        string runId,
        WorkflowLeaseToken lease,
        TimeSpan duration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRunId(runId);
        if (lease is null)
        {
            throw new ArgumentNullException(nameof(lease));
        }

        ValidateDuration(duration);
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run)
                || run.Lease is null
                || !run.Lease.Matches(lease)
                || run.Lease.ExpiresAt <= now
                || run.IsTerminal)
            {
                return new ValueTask<bool>(false);
            }

            run.Lease.ExpiresAt = now + duration;
            return new ValueTask<bool>(true);
        }
    }

    public ValueTask<bool> ReleaseLeaseAsync(
        string runId,
        WorkflowLeaseToken lease,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRunId(runId);
        if (lease is null)
        {
            throw new ArgumentNullException(nameof(lease));
        }

        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run)
                || run.Lease is null
                || !run.Lease.Matches(lease))
            {
                return new ValueTask<bool>(false);
            }

            run.Lease = null;
            return new ValueTask<bool>(true);
        }
    }

    public ValueTask<WorkflowCancelResult> RequestCancellationAsync(
        string runId,
        string reasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRunId(runId);
        WorkflowValidation.RequiredIdentifier(
            reasonCode,
            nameof(reasonCode),
            128,
            allowSlash: false);
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
            {
                return Cancel(WorkflowCancelStatus.NotFound, null);
            }

            if (run.IsTerminal)
            {
                return Cancel(
                    WorkflowCancelStatus.Terminal,
                    run.Clone());
            }

            if (run.CancellationRequested)
            {
                return Cancel(
                    WorkflowCancelStatus.AlreadyRequested,
                    run.Clone());
            }

            run.CancellationRequested = true;
            run.CancellationReason = reasonCode;
            run.Status = WorkflowRunStatus.CancelRequested;
            run.ReasonCode = WorkflowReasonCodes.CancellationRequested;
            run.UpdatedAt = now;
            run.Revision = checked(run.Revision + 1);
            return Cancel(
                WorkflowCancelStatus.Requested,
                run.Clone());
        }
    }

    private static bool HasSameIdentity(
        WorkflowRunSnapshot current,
        WorkflowRunSnapshot replacement)
    {
        return string.Equals(
                   current.RunId,
                   replacement.RunId,
                   StringComparison.Ordinal)
               && string.Equals(
                   current.WorkflowId,
                   replacement.WorkflowId,
                   StringComparison.Ordinal)
               && string.Equals(
                   current.WorkflowVersion,
                   replacement.WorkflowVersion,
                   StringComparison.Ordinal)
               && string.Equals(
                   current.DefinitionDigest,
                   replacement.DefinitionDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   current.InputDigest,
                   replacement.InputDigest,
                   StringComparison.Ordinal);
    }

    private static bool HasUniqueInstances(WorkflowRunSnapshot snapshot)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return snapshot.StageInstances.All(instance =>
            ids.Add(instance.InstanceId));
    }

    private static void RequireRunId(string runId)
    {
        WorkflowValidation.RequiredIdentifier(
            runId,
            nameof(runId),
            80,
            allowSlash: false);
    }

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(100)
            || duration > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
    }

    private static ValueTask<WorkflowCreateResult> Result(
        WorkflowCreateResult value)
    {
        return new ValueTask<WorkflowCreateResult>(value);
    }

    private static ValueTask<WorkflowCommitResult> Commit(
        WorkflowCommitStatus status,
        WorkflowRunSnapshot? snapshot)
    {
        return new ValueTask<WorkflowCommitResult>(
            new WorkflowCommitResult(status, snapshot));
    }

    private static ValueTask<WorkflowLeaseAcquireResult> Lease(
        WorkflowLeaseAcquireStatus status,
        WorkflowLeaseToken? token,
        DateTimeOffset? expiresAt)
    {
        return new ValueTask<WorkflowLeaseAcquireResult>(
            new WorkflowLeaseAcquireResult(status, token, expiresAt));
    }

    private static ValueTask<WorkflowCancelResult> Cancel(
        WorkflowCancelStatus status,
        WorkflowRunSnapshot? snapshot)
    {
        return new ValueTask<WorkflowCancelResult>(
            new WorkflowCancelResult(status, snapshot));
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace GameAgent.Workflow;

/// <summary>
/// Cross-process workflow store backed by one checksummed append-only log per
/// run. All writers take a root capacity lock followed by a per-run lock.
/// Confirmed frames are flushed to stable storage before acknowledgement.
/// </summary>
public sealed class FileWorkflowRunStore : IWorkflowRunStore
{
    private readonly FileWorkflowRunStoreOptions _options;
    private readonly string _rootLockPath;

    public FileWorkflowRunStore(FileWorkflowRunStoreOptions options)
    {
        _options = options
            ?? throw new ArgumentNullException(nameof(options));
        Directory.CreateDirectory(_options.RootDirectory);
        _rootLockPath = Path.Combine(
            _options.RootDirectory,
            ".workflow-root.lock");
    }

    public string GetRunFilePath(string runId)
    {
        RequireRunId(runId);
        return Path.Combine(
            _options.RootDirectory,
            "run-" + HashRunId(runId) + ".wfr");
    }

    public async ValueTask<WorkflowCreateResult> CreateAsync(
        WorkflowRunSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var runId = snapshot.RunId;
        RequireRunId(runId);
        using var locks = await AcquireMutationLocksAsync(
                runId,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRunFilePath(runId);
        FileStream? stream = null;
        try
        {
            var scan = new WorkflowRunLogScan(
                initialized: false,
                snapshot: null,
                committedLength: 0,
                frameSequence: 0,
                frameCount: 0,
                hasTornTail: false);
            if (File.Exists(path))
            {
                stream = OpenDataFile(path, FileMode.Open);
                scan = WorkflowRunLog.Scan(stream, _options);
                if (scan.Snapshot is not null)
                {
                    EnsureRunId(scan.Snapshot, runId);
                    return new WorkflowCreateResult(
                        WorkflowCreateStatus.AlreadyExists,
                        scan.Snapshot.Clone());
                }
            }

            var rootUsage = MeasureRoot();
            if (rootUsage.CommittedRuns >= _options.MaxRuns)
            {
                return new WorkflowCreateResult(
                    WorkflowCreateStatus.CapacityExceeded,
                    null);
            }

            var stored = snapshot.Clone();
            stored.Revision = 0;
            stored.FencingEpoch = 0;
            stored.Lease = null;
            var normalized = Normalize(stored);
            if (normalized.Snapshot.Revision != 0
                || normalized.Snapshot.FencingEpoch != 0
                || normalized.Snapshot.Lease is not null)
            {
                throw new ArgumentException(
                    "A new workflow snapshot has invalid initial metadata.",
                    nameof(snapshot));
            }

            EnsureAppendCapacity(
                stream?.Length ?? 0,
                scan,
                normalized.Payload.Length,
                rootUsage.TotalBytes);
            cancellationToken.ThrowIfCancellationRequested();
            stream ??= OpenDataFile(path, FileMode.OpenOrCreate);
            WorkflowRunLog.Append(
                stream,
                scan,
                WorkflowRunLogOperation.Create,
                normalized.Snapshot,
                normalized.Payload,
                _options);
            return new WorkflowCreateResult(
                WorkflowCreateStatus.Created,
                normalized.Snapshot.Clone());
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public async ValueTask<WorkflowRunSnapshot?> ReadAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRunId(runId);
        var path = GetRunFilePath(runId);
        if (!File.Exists(path))
        {
            return null;
        }

        using var runLock = await WorkflowFileLockManager
            .AcquireAsync(
                GetRunLockPath(runId),
                _options,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = OpenReadFile(path);
        var scan = WorkflowRunLog.Scan(stream, _options);
        if (scan.Snapshot is null)
        {
            return null;
        }

        EnsureRunId(scan.Snapshot, runId);
        return scan.Snapshot.Clone();
    }

    public async ValueTask<WorkflowCommitResult> TryCommitAsync(
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

        using var locks = await AcquireMutationLocksAsync(
                runId,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRunFilePath(runId);
        if (!File.Exists(path))
        {
            return new WorkflowCommitResult(
                WorkflowCommitStatus.NotFound,
                null);
        }

        using var stream = OpenDataFile(path, FileMode.Open);
        var scan = WorkflowRunLog.Scan(stream, _options);
        var current = scan.Snapshot;
        if (current is null)
        {
            return new WorkflowCommitResult(
                WorkflowCommitStatus.NotFound,
                null);
        }

        EnsureRunId(current, runId);
        if (current.Revision != expectedRevision)
        {
            return new WorkflowCommitResult(
                WorkflowCommitStatus.RevisionConflict,
                current.Clone());
        }

        if (current.Lease is null
            || !current.Lease.Matches(lease)
            || current.Lease.ExpiresAt <= now)
        {
            return new WorkflowCommitResult(
                WorkflowCommitStatus.LeaseLost,
                current.Clone());
        }

        if (!HasSameIdentity(current, replacement)
            || replacement.Revision != expectedRevision + 1)
        {
            return new WorkflowCommitResult(
                WorkflowCommitStatus.InvalidSnapshot,
                current.Clone());
        }

        var stored = replacement.Clone();
        stored.FencingEpoch = current.FencingEpoch;
        stored.Lease = current.Lease.Clone();
        NormalizedSnapshot normalized;
        try
        {
            normalized = Normalize(stored);
        }
        catch (WorkflowFileStoreCorruptionException)
        {
            return new WorkflowCommitResult(
                WorkflowCommitStatus.InvalidSnapshot,
                current.Clone());
        }

        var rootUsage = MeasureRoot();
        EnsureAppendCapacity(
            stream.Length,
            scan,
            normalized.Payload.Length,
            rootUsage.TotalBytes);
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowRunLog.Append(
            stream,
            scan,
            WorkflowRunLogOperation.Commit,
            normalized.Snapshot,
            normalized.Payload,
            _options);
        return new WorkflowCommitResult(
            WorkflowCommitStatus.Committed,
            normalized.Snapshot.Clone());
    }

    public async ValueTask<WorkflowLeaseAcquireResult>
        TryAcquireLeaseAsync(
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
        using var locks = await AcquireMutationLocksAsync(
                runId,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRunFilePath(runId);
        if (!File.Exists(path))
        {
            return new WorkflowLeaseAcquireResult(
                WorkflowLeaseAcquireStatus.NotFound,
                null,
                null);
        }

        using var stream = OpenDataFile(path, FileMode.Open);
        var scan = WorkflowRunLog.Scan(stream, _options);
        var current = scan.Snapshot;
        if (current is null)
        {
            return new WorkflowLeaseAcquireResult(
                WorkflowLeaseAcquireStatus.NotFound,
                null,
                null);
        }

        EnsureRunId(current, runId);
        if (current.IsTerminal)
        {
            return new WorkflowLeaseAcquireResult(
                WorkflowLeaseAcquireStatus.Terminal,
                null,
                null);
        }

        if (current.Lease is not null
            && current.Lease.ExpiresAt > now
            && !string.Equals(
                current.Lease.OwnerId,
                ownerId,
                StringComparison.Ordinal))
        {
            return new WorkflowLeaseAcquireResult(
                WorkflowLeaseAcquireStatus.Busy,
                null,
                current.Lease.ExpiresAt);
        }

        var next = current.Clone();
        WorkflowRunLogOperation operation;
        WorkflowLeaseToken token;
        var expiresAt = now + duration;
        if (current.Lease is not null
            && current.Lease.ExpiresAt > now
            && string.Equals(
                current.Lease.OwnerId,
                ownerId,
                StringComparison.Ordinal))
        {
            token = new WorkflowLeaseToken(
                ownerId,
                current.Lease.FencingEpoch);
            if (current.Lease.ExpiresAt == expiresAt)
            {
                return new WorkflowLeaseAcquireResult(
                    WorkflowLeaseAcquireStatus.Acquired,
                    token,
                    current.Lease.ExpiresAt);
            }

            next.Lease!.ExpiresAt = expiresAt;
            operation = WorkflowRunLogOperation.LeaseRenew;
        }
        else
        {
            next.FencingEpoch = checked(current.FencingEpoch + 1);
            next.Lease = new WorkflowLeaseSnapshot(
                ownerId,
                next.FencingEpoch,
                expiresAt);
            token = new WorkflowLeaseToken(
                ownerId,
                next.FencingEpoch);
            operation = WorkflowRunLogOperation.LeaseAcquire;
        }

        var persisted = PersistMutation(
            stream,
            scan,
            operation,
            next,
            cancellationToken);
        return new WorkflowLeaseAcquireResult(
            WorkflowLeaseAcquireStatus.Acquired,
            token,
            persisted.Lease!.ExpiresAt);
    }

    public async ValueTask<bool> RenewLeaseAsync(
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
        using var locks = await AcquireMutationLocksAsync(
                runId,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRunFilePath(runId);
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = OpenDataFile(path, FileMode.Open);
        var scan = WorkflowRunLog.Scan(stream, _options);
        var current = scan.Snapshot;
        if (current is null
            || current.Lease is null
            || !current.Lease.Matches(lease)
            || current.Lease.ExpiresAt <= now
            || current.IsTerminal)
        {
            return false;
        }

        EnsureRunId(current, runId);
        var expiresAt = now + duration;
        if (current.Lease.ExpiresAt == expiresAt)
        {
            return true;
        }

        var next = current.Clone();
        next.Lease!.ExpiresAt = expiresAt;
        _ = PersistMutation(
            stream,
            scan,
            WorkflowRunLogOperation.LeaseRenew,
            next,
            cancellationToken);
        return true;
    }

    public async ValueTask<bool> ReleaseLeaseAsync(
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

        using var locks = await AcquireMutationLocksAsync(
                runId,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRunFilePath(runId);
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = OpenDataFile(path, FileMode.Open);
        var scan = WorkflowRunLog.Scan(stream, _options);
        var current = scan.Snapshot;
        if (current is null
            || current.Lease is null
            || !current.Lease.Matches(lease))
        {
            return false;
        }

        EnsureRunId(current, runId);
        var next = current.Clone();
        next.Lease = null;
        _ = PersistMutation(
            stream,
            scan,
            WorkflowRunLogOperation.LeaseRelease,
            next,
            cancellationToken);
        return true;
    }

    public async ValueTask<WorkflowCancelResult>
        RequestCancellationAsync(
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
        using var locks = await AcquireMutationLocksAsync(
                runId,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRunFilePath(runId);
        if (!File.Exists(path))
        {
            return new WorkflowCancelResult(
                WorkflowCancelStatus.NotFound,
                null);
        }

        using var stream = OpenDataFile(path, FileMode.Open);
        var scan = WorkflowRunLog.Scan(stream, _options);
        var current = scan.Snapshot;
        if (current is null)
        {
            return new WorkflowCancelResult(
                WorkflowCancelStatus.NotFound,
                null);
        }

        EnsureRunId(current, runId);
        if (current.IsTerminal)
        {
            return new WorkflowCancelResult(
                WorkflowCancelStatus.Terminal,
                current.Clone());
        }

        if (current.CancellationRequested)
        {
            return new WorkflowCancelResult(
                WorkflowCancelStatus.AlreadyRequested,
                current.Clone());
        }

        var next = current.Clone();
        next.CancellationRequested = true;
        next.CancellationReason = reasonCode;
        next.Status = WorkflowRunStatus.CancelRequested;
        next.ReasonCode = WorkflowReasonCodes.CancellationRequested;
        next.UpdatedAt = now;
        next.Revision = checked(current.Revision + 1);
        var persisted = PersistMutation(
            stream,
            scan,
            WorkflowRunLogOperation.Cancel,
            next,
            cancellationToken);
        return new WorkflowCancelResult(
            WorkflowCancelStatus.Requested,
            persisted);
    }

    private WorkflowRunSnapshot PersistMutation(
        FileStream stream,
        WorkflowRunLogScan scan,
        WorkflowRunLogOperation operation,
        WorkflowRunSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(snapshot);
        var rootUsage = MeasureRoot();
        EnsureAppendCapacity(
            stream.Length,
            scan,
            normalized.Payload.Length,
            rootUsage.TotalBytes);
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowRunLog.Append(
            stream,
            scan,
            operation,
            normalized.Snapshot,
            normalized.Payload,
            _options);
        return normalized.Snapshot.Clone();
    }

    private NormalizedSnapshot Normalize(WorkflowRunSnapshot snapshot)
    {
        if (snapshot.StageInstances.Count
            > _options.MaxStageInstancesPerRun)
        {
            throw new WorkflowFileStoreCapacityException(
                "The workflow snapshot exceeds its stage-instance limit.");
        }

        var payload = WorkflowRunSnapshotCodec.Encode(
            snapshot,
            _options.MaxSnapshotBytes);
        var restored = WorkflowRunSnapshotCodec.Decode(
            payload,
            _options.MaxStageInstancesPerRun);
        return new NormalizedSnapshot(restored, payload);
    }

    private void EnsureAppendCapacity(
        long currentFileBytes,
        WorkflowRunLogScan scan,
        int payloadBytes,
        long currentRootBytes)
    {
        if (scan.FrameCount >= _options.MaxOperationsPerRun)
        {
            throw new WorkflowFileStoreCapacityException(
                "The workflow run operation limit is exhausted.");
        }

        var frameBytes = WorkflowRunLog.GetFrameLength(payloadBytes);
        if (frameBytes > _options.MaxFrameBytes)
        {
            throw new WorkflowFileStoreCapacityException(
                "The workflow frame exceeds its byte limit.");
        }

        var projectedFileBytes = checked(
            scan.CommittedLength
            + (scan.Initialized ? 0 : WorkflowRunLog.HeaderBytes)
            + frameBytes);
        if (projectedFileBytes > _options.MaxFileBytesPerRun)
        {
            throw new WorkflowFileStoreCapacityException(
                "The workflow run file capacity is exhausted.");
        }

        var projectedRootBytes = checked(
            currentRootBytes
            - currentFileBytes
            + projectedFileBytes);
        if (projectedRootBytes > _options.MaxRootBytes)
        {
            throw new WorkflowFileStoreCapacityException(
                "The workflow file-store root capacity is exhausted.");
        }
    }

    private RootUsage MeasureRoot()
    {
        var committedRuns = 0;
        var files = 0;
        var totalBytes = 0L;
        foreach (var path in Directory.EnumerateFiles(
                     _options.RootDirectory,
                     "run-*.wfr",
                     SearchOption.TopDirectoryOnly))
        {
            files++;
            if (files > _options.MaxRuns + 1_024)
            {
                throw new WorkflowFileStoreCapacityException(
                    "The workflow file-store contains too many run files.");
            }

            var info = new FileInfo(path);
            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > _options.MaxRootBytes
                + _options.MaxFileBytesPerRun)
            {
                throw new WorkflowFileStoreCapacityException(
                    "The workflow file-store root exceeds its byte limit.");
            }

            using var stream = OpenReadFile(path);
            var scan = WorkflowRunLog.Scan(stream, _options);
            if (scan.Snapshot is not null)
            {
                committedRuns++;
            }
        }

        return new RootUsage(committedRuns, totalBytes);
    }

    private async ValueTask<WorkflowMutationLocks>
        AcquireMutationLocksAsync(
            string runId,
            CancellationToken cancellationToken)
    {
        var root = await WorkflowFileLockManager
            .AcquireAsync(
                _rootLockPath,
                _options,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var run = await WorkflowFileLockManager
                .AcquireAsync(
                    GetRunLockPath(runId),
                    _options,
                    cancellationToken)
                .ConfigureAwait(false);
            return new WorkflowMutationLocks(root, run);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private FileStream OpenDataFile(string path, FileMode mode)
    {
        var fileOptions = FileOptions.SequentialScan;
        if (_options.UseWriteThrough)
        {
            fileOptions |= FileOptions.WriteThrough;
        }

        return new FileStream(
            path,
            mode,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            65_536,
            fileOptions);
    }

    private static FileStream OpenReadFile(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            65_536,
            FileOptions.SequentialScan);
    }

    private string GetRunLockPath(string runId)
    {
        var hash = HashRunId(runId);
        return Path.Combine(
            _options.RootDirectory,
            ".workflow-run-" + hash.Substring(0, 2) + ".lock");
    }

    private static string HashRunId(string runId)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(runId));
        var result = new StringBuilder(hash.Length * 2);
        foreach (var item in hash)
        {
            result.Append(item.ToString(
                "x2",
                System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
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

    private static void EnsureRunId(
        WorkflowRunSnapshot snapshot,
        string expectedRunId)
    {
        if (!string.Equals(
                snapshot.RunId,
                expectedRunId,
                StringComparison.Ordinal))
        {
            throw new WorkflowFileStoreCorruptionException(
                WorkflowFileStoreReasonCodes.InvalidSnapshot,
                "A workflow run file contains a different run identifier.");
        }
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

    private sealed class NormalizedSnapshot
    {
        public NormalizedSnapshot(
            WorkflowRunSnapshot snapshot,
            byte[] payload)
        {
            Snapshot = snapshot;
            Payload = payload;
        }

        public WorkflowRunSnapshot Snapshot { get; }

        public byte[] Payload { get; }
    }

    private sealed class RootUsage
    {
        public RootUsage(int committedRuns, long totalBytes)
        {
            CommittedRuns = committedRuns;
            TotalBytes = totalBytes;
        }

        public int CommittedRuns { get; }

        public long TotalBytes { get; }
    }
}

internal sealed class WorkflowMutationLocks : IDisposable
{
    private WorkflowFileLockLease? _root;
    private WorkflowFileLockLease? _run;

    public WorkflowMutationLocks(
        WorkflowFileLockLease root,
        WorkflowFileLockLease run)
    {
        _root = root;
        _run = run;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _run, null)?.Dispose();
        Interlocked.Exchange(ref _root, null)?.Dispose();
    }
}

internal sealed class WorkflowFileLockLease : IDisposable
{
    private FileStream? _stream;
    private SemaphoreSlim? _local;

    public WorkflowFileLockLease(
        FileStream stream,
        SemaphoreSlim local)
    {
        _stream = stream;
        _local = local;
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        try
        {
            if (stream is not null)
            {
                try
                {
                    stream.Unlock(0, 1);
                }
                finally
                {
                    stream.Dispose();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _local, null)?.Release();
        }
    }
}

internal static class WorkflowFileLockManager
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        LocalLocks = new(
            Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

    public static async ValueTask<WorkflowFileLockLease> AcquireAsync(
        string path,
        FileWorkflowRunStoreOptions options,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var local = LocalLocks.GetOrAdd(
            fullPath,
            _ => new SemaphoreSlim(1, 1));
        if (!await local
                .WaitAsync(options.LockTimeout, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Timeout(fullPath);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream? stream = null;
                try
                {
                    stream = new FileStream(
                        fullPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite,
                        4_096,
                        options.UseWriteThrough
                            ? FileOptions.WriteThrough
                            : FileOptions.None);
                    if (stream.Length == 0)
                    {
                        stream.SetLength(1);
                        stream.Flush(flushToDisk: true);
                    }

                    stream.Lock(0, 1);
                    return new WorkflowFileLockLease(stream, local);
                }
                catch (IOException)
                {
                    stream?.Dispose();
                    if (stopwatch.Elapsed >= options.LockTimeout)
                    {
                        throw Timeout(fullPath);
                    }
                }

                var remaining =
                    options.LockTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw Timeout(fullPath);
                }

                var delay = remaining < options.LockRetryDelay
                    ? remaining
                    : options.LockRetryDelay;
                await Task.Delay(delay, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            local.Release();
            throw;
        }
    }

    private static WorkflowFileStoreLockTimeoutException Timeout(
        string path)
    {
        return new WorkflowFileStoreLockTimeoutException(
            $"Timed out acquiring workflow file lock '{Path.GetFileName(path)}'.");
    }
}

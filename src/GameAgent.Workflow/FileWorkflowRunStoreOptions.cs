namespace GameAgent.Workflow;

public enum WorkflowFileStoreFaultPoint
{
    BeforeFrameWrite = 0,
    AfterFramePrefixWrite = 1,
    AfterFramePayloadWrite = 2,
    BeforePayloadFlush = 3,
    AfterPayloadFlushBeforeCommitMarker = 4,
    AfterCommitMarkerWriteBeforeFlush = 5,
    AfterCommitFlushBeforeAcknowledge = 6
}

public interface IWorkflowFileStoreFaultInjector
{
    void OnFaultPoint(
        WorkflowFileStoreFaultPoint point,
        string runId,
        long frameSequence);
}

public static class WorkflowFileStoreReasonCodes
{
    public const string CorruptHeader =
        "workflow_file_store_corrupt_header";
    public const string CorruptCommittedFrame =
        "workflow_file_store_corrupt_committed_frame";
    public const string UnsupportedVersion =
        "workflow_file_store_unsupported_version";
    public const string InvalidSnapshot =
        "workflow_file_store_invalid_snapshot";
    public const string CapacityExceeded =
        "workflow_file_store_capacity_exceeded";
    public const string LockTimeout =
        "workflow_file_store_lock_timeout";
    public const string IoFailure =
        "workflow_file_store_io_failure";
}

public class WorkflowFileStoreException : IOException
{
    public WorkflowFileStoreException(
        string reasonCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed class WorkflowFileStoreCorruptionException
    : WorkflowFileStoreException
{
    public WorkflowFileStoreCorruptionException(
        string reasonCode,
        string message,
        Exception? innerException = null)
        : base(reasonCode, message, innerException)
    {
    }
}

public sealed class WorkflowFileStoreCapacityException
    : WorkflowFileStoreException
{
    public WorkflowFileStoreCapacityException(string message)
        : base(
            WorkflowFileStoreReasonCodes.CapacityExceeded,
            message)
    {
    }
}

public sealed class WorkflowFileStoreLockTimeoutException
    : WorkflowFileStoreException
{
    public WorkflowFileStoreLockTimeoutException(string message)
        : base(WorkflowFileStoreReasonCodes.LockTimeout, message)
    {
    }
}

public sealed class FileWorkflowRunStoreOptions
{
    public FileWorkflowRunStoreOptions(
        string rootDirectory,
        int maxRuns = 4_096,
        int maxOperationsPerRun = 65_536,
        int maxSnapshotBytes = 8_388_608,
        int maxFrameBytes = 8_389_120,
        long maxFileBytesPerRun = 536_870_912,
        long maxRootBytes = 4_294_967_296,
        int maxStageInstancesPerRun = 100_000,
        TimeSpan? lockTimeout = null,
        TimeSpan? lockRetryDelay = null,
        bool useWriteThrough = true,
        IWorkflowFileStoreFaultInjector? faultInjector = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "A file-store root directory is required.",
                nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
        MaxRuns = InRange(maxRuns, 1, 1_000_000, nameof(maxRuns));
        MaxOperationsPerRun = InRange(
            maxOperationsPerRun,
            1,
            10_000_000,
            nameof(maxOperationsPerRun));
        MaxSnapshotBytes = InRange(
            maxSnapshotBytes,
            1_024,
            67_108_864,
            nameof(maxSnapshotBytes));
        MaxFrameBytes = InRange(
            maxFrameBytes,
            maxSnapshotBytes + 112,
            67_109_376,
            nameof(maxFrameBytes));
        if (maxFileBytesPerRun < maxFrameBytes + 48L
            || maxFileBytesPerRun > 1_099_511_627_776L)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFileBytesPerRun));
        }

        MaxFileBytesPerRun = maxFileBytesPerRun;
        if (maxRootBytes < maxFileBytesPerRun
            || maxRootBytes > 17_592_186_044_416L)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRootBytes));
        }

        MaxRootBytes = maxRootBytes;
        MaxStageInstancesPerRun = InRange(
            maxStageInstancesPerRun,
            1,
            1_000_000,
            nameof(maxStageInstancesPerRun));
        LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        if (LockTimeout < TimeSpan.FromMilliseconds(10)
            || LockTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }

        LockRetryDelay = lockRetryDelay
                         ?? TimeSpan.FromMilliseconds(10);
        if (LockRetryDelay < TimeSpan.FromMilliseconds(1)
            || LockRetryDelay > TimeSpan.FromSeconds(1)
            || LockRetryDelay > LockTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(lockRetryDelay));
        }

        UseWriteThrough = useWriteThrough;
        FaultInjector = faultInjector;
    }

    public string RootDirectory { get; }

    public int MaxRuns { get; }

    public int MaxOperationsPerRun { get; }

    public int MaxSnapshotBytes { get; }

    public int MaxFrameBytes { get; }

    public long MaxFileBytesPerRun { get; }

    public long MaxRootBytes { get; }

    public int MaxStageInstancesPerRun { get; }

    public TimeSpan LockTimeout { get; }

    public TimeSpan LockRetryDelay { get; }

    /// <summary>
    /// Adds <see cref="FileOptions.WriteThrough"/> to data-file handles.
    /// Confirmed mutations always call <c>Flush(true)</c> regardless.
    /// </summary>
    public bool UseWriteThrough { get; }

    public IWorkflowFileStoreFaultInjector? FaultInjector { get; }

    private static int InRange(
        int value,
        int minimum,
        int maximum,
        string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }

        return value;
    }
}

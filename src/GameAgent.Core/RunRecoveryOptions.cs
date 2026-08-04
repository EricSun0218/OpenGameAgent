namespace GameAgent.Core;

public sealed class RunRecoveryOptions
{
    public const int DefaultMaxEventsPerRun = 25_000;
    public const int DefaultMaxEventUtf8Bytes = 1_048_576;
    public const int DefaultMaxAggregateEventUtf8Bytes =
        64 * 1_048_576;

    public int MaxEventsPerRun { get; set; } =
        DefaultMaxEventsPerRun;

    public int MaxEventUtf8Bytes { get; set; } =
        DefaultMaxEventUtf8Bytes;

    public int MaxAggregateEventUtf8Bytes { get; set; } =
        DefaultMaxAggregateEventUtf8Bytes;
}

public sealed class RunRecoveryCapacityExceededException
    : InvalidOperationException
{
    public RunRecoveryCapacityExceededException(
        string runId,
        int limit,
        int attempted)
        : base(
            $"Run recovery for '{runId}' allows at most {limit} events, "
            + $"but the journal returned {attempted}. Increase "
            + $"{nameof(RunRecoveryOptions.MaxEventsPerRun)} only after "
            + "budgeting the additional recovery memory and time.")
    {
        RunId = runId;
        Limit = limit;
        Attempted = attempted;
    }

    public string RunId { get; }

    public int Limit { get; }

    public int Attempted { get; }
}

public sealed class RunRecoveryEventCapacityExceededException
    : InvalidOperationException
{
    public RunRecoveryEventCapacityExceededException(
        string runId,
        int limit)
        : base(
            $"Run recovery for '{runId}' allows at most {limit} UTF-8 "
            + "bytes per event. Increase "
            + $"{nameof(RunRecoveryOptions.MaxEventUtf8Bytes)} only after "
            + "budgeting the additional recovery memory and time.")
    {
        RunId = runId;
        Limit = limit;
    }

    public string RunId { get; }

    public int Limit { get; }
}

public sealed class RunRecoveryBytesCapacityExceededException
    : InvalidOperationException
{
    public RunRecoveryBytesCapacityExceededException(
        string runId,
        long limit,
        long attempted)
        : base(
            $"Run recovery for '{runId}' allows at most {limit} aggregate "
            + $"event UTF-8 bytes, but the journal returned at least "
            + $"{attempted}. Increase "
            + $"{nameof(RunRecoveryOptions.MaxAggregateEventUtf8Bytes)} "
            + "only after budgeting the additional recovery memory and time.")
    {
        RunId = runId;
        Limit = limit;
        Attempted = attempted;
    }

    public string RunId { get; }

    public long Limit { get; }

    public long Attempted { get; }
}

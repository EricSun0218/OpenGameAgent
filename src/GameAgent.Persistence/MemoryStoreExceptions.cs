namespace GameAgent.Persistence;

public sealed class MemoryStoreCapacityExceededException
    : InvalidOperationException
{
    public MemoryStoreCapacityExceededException(
        string limitName,
        long limit,
        long attempted)
        : base(
            $"Memory-store capacity '{limitName}' is {limit}, "
            + $"but the operation requires {attempted}. Increase the "
            + "configured limit or explicitly compact a stopped store.")
    {
        LimitName = limitName;
        Limit = limit;
        Attempted = attempted;
    }

    public string LimitName { get; }

    public long Limit { get; }

    public long Attempted { get; }
}

public sealed class MemoryStoreCorruptionException : IOException
{
    public MemoryStoreCorruptionException(
        string path,
        long offset,
        string message)
        : base($"Memory store '{path}' is corrupt at byte {offset}: {message}")
    {
        Path = path;
        Offset = offset;
    }

    public string Path { get; }

    public long Offset { get; }
}

public sealed class MemoryStoreFaultedException : IOException
{
    public MemoryStoreFaultedException(string path)
        : base(
            $"Memory store '{path}' previously failed during a mutation "
            + "or flush. Dispose and reopen it before continuing.")
    {
        Path = path;
    }

    public string Path { get; }
}

public sealed class MemoryStoreRevisionConflictException
    : InvalidOperationException
{
    public MemoryStoreRevisionConflictException(
        long expectedRevision,
        long actualRevision)
        : base(
            $"Expected memory-store revision {expectedRevision}, "
            + $"but the current revision is {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public long ExpectedRevision { get; }

    public long ActualRevision { get; }
}

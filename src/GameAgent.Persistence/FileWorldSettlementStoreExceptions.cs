namespace GameAgent.Persistence;

public sealed class FileWorldSettlementStoreCapacityException
    : IOException
{
    public FileWorldSettlementStoreCapacityException(
        string limitName,
        long limit,
        long attempted)
        : base(
            $"World settlement store limit '{limitName}' is {limit}, "
            + $"but the attempted value was {attempted}.")
    {
        LimitName = limitName;
        Limit = limit;
        Attempted = attempted;
    }

    public string LimitName { get; }

    public long Limit { get; }

    public long Attempted { get; }
}

public sealed class FileWorldSettlementStoreCorruptionException
    : IOException
{
    public FileWorldSettlementStoreCorruptionException(
        string path,
        long offset,
        string message,
        Exception? innerException = null)
        : base(
            $"World settlement store '{path}' is corrupt at byte "
            + $"{offset}: {message}",
            innerException)
    {
        Path = path;
        Offset = offset;
    }

    public string Path { get; }

    public long Offset { get; }
}

public sealed class FileWorldSettlementStoreFaultedException
    : IOException
{
    public FileWorldSettlementStoreFaultedException(string path)
        : base(
            $"World settlement store '{path}' encountered an uncertain "
            + "write and must be reopened before further use.")
    {
        Path = path;
    }

    public string Path { get; }
}

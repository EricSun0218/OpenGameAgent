namespace GameAgent.Persistence;

public sealed class FileWorldPresentationStoreCapacityException
    : InvalidOperationException
{
    public FileWorldPresentationStoreCapacityException(
        string limitName,
        long limit,
        long attempted)
        : base(
            $"World-presentation store capacity '{limitName}' is {limit}, "
            + $"but the operation requires {attempted}.")
    {
        LimitName = limitName;
        Limit = limit;
        Attempted = attempted;
    }

    public string LimitName { get; }

    public long Limit { get; }

    public long Attempted { get; }
}

public sealed class FileWorldPresentationStoreCorruptionException
    : IOException
{
    public FileWorldPresentationStoreCorruptionException(
        string path,
        long offset,
        string message,
        Exception? innerException = null)
        : base(
            $"World-presentation store '{path}' is corrupt at byte "
            + $"{offset}: {message}",
            innerException)
    {
        Path = path;
        Offset = offset;
    }

    public string Path { get; }

    public long Offset { get; }
}

public sealed class FileWorldPresentationStoreFaultedException
    : IOException
{
    public FileWorldPresentationStoreFaultedException(string path)
        : base(
            $"World-presentation store '{path}' previously failed during "
            + "a mutation or flush. Dispose and reopen it before "
            + "continuing.")
    {
        Path = path;
    }

    public string Path { get; }
}

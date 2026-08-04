namespace GameAgent.Persistence;

public sealed class FileGroupInteractionStoreCapacityException
    : InvalidOperationException
{
    public FileGroupInteractionStoreCapacityException(
        string limitName,
        long limit,
        long attempted)
        : base(
            $"Group-interaction store capacity '{limitName}' is {limit}, "
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

public sealed class FileGroupInteractionStoreCorruptionException
    : IOException
{
    public FileGroupInteractionStoreCorruptionException(
        string path,
        long offset,
        string message,
        Exception? innerException = null)
        : base(
            $"Group-interaction store '{path}' is corrupt at byte "
            + $"{offset}: {message}",
            innerException)
    {
        Path = path;
        Offset = offset;
    }

    public string Path { get; }

    public long Offset { get; }
}

public sealed class FileGroupInteractionStoreFaultedException
    : IOException
{
    public FileGroupInteractionStoreFaultedException(string path)
        : base(
            $"Group-interaction store '{path}' previously failed during "
            + "a mutation or flush. Dispose and reopen it before continuing.")
    {
        Path = path;
    }

    public string Path { get; }
}

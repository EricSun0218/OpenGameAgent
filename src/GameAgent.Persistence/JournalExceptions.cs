namespace GameAgent.Persistence;

public sealed class JournalCorruptionException : IOException
{
    public JournalCorruptionException(string path, long offset, string message)
        : base($"Journal '{path}' is corrupt at byte {offset}: {message}")
    {
        Path = path;
        Offset = offset;
    }

    public string Path { get; }

    public long Offset { get; }
}

public sealed class JournalFaultedException : IOException
{
    public JournalFaultedException(string path)
        : base(
            $"Journal '{path}' previously failed during an append or flush. "
            + "Dispose and reopen it before continuing.")
    {
        Path = path;
    }

    public string Path { get; }
}

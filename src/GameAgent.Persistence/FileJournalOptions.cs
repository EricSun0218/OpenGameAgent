namespace GameAgent.Persistence;

public sealed class FileJournalOptions
{
    public const int DefaultMaxFramePayloadBytes = 16 * 1024 * 1024;
    public const long DefaultMaxJournalBytes = 256L * 1024 * 1024;
    public const long DefaultMaxTotalCommittedEvents = 100_000;
    public const int DefaultMaxEventsPerRun = 25_000;

    public bool FlushToDiskOnAppend { get; set; } = true;

    public int MaxFramePayloadBytes { get; set; } =
        DefaultMaxFramePayloadBytes;

    public long MaxJournalBytes { get; set; } =
        DefaultMaxJournalBytes;

    public long MaxTotalCommittedEvents { get; set; } =
        DefaultMaxTotalCommittedEvents;

    public int MaxEventsPerRun { get; set; } =
        DefaultMaxEventsPerRun;

    public IJournalFaultInjector? FaultInjector { get; set; }
}

public enum JournalWriteStage
{
    BeforeWrite,
    AfterWrite,
    AfterFlush
}

public interface IJournalFaultInjector
{
    int GetWriteLength(int frameLength);

    void OnWriteStage(
        JournalWriteStage stage,
        int bytesWritten,
        int frameLength);
}

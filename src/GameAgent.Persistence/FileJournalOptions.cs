namespace GameAgent.Persistence;

public sealed class FileJournalOptions
{
    public const int DefaultMaxFramePayloadBytes = 16 * 1024 * 1024;

    public bool FlushToDiskOnAppend { get; set; } = true;

    public int MaxFramePayloadBytes { get; set; } =
        DefaultMaxFramePayloadBytes;

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

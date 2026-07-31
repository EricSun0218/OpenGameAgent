namespace GameAgent.Persistence;

public sealed class FileWorldSettlementStoreOptions
{
    public int MaxFramePayloadBytes { get; set; } = 36 * 1_048_576;

    public long MaxLogBytes { get; set; } = 4L * 1_073_741_824;

    public long MaxMutationFrames { get; set; } = 1_000_000;

    public int MaxRecords { get; set; } = 100_000;

    public int MaxFrameJsonTokens { get; set; } = 2_500_000;

    public long MaxResidentBytes { get; set; } = 2L * 1_073_741_824;

    public bool FlushToDiskOnMutation { get; set; } = true;

    public IJournalFaultInjector? FaultInjector { get; set; }
}

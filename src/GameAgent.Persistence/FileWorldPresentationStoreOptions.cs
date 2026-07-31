using GameAgent.Core;

namespace GameAgent.Persistence;

public sealed class FileWorldPresentationStoreOptions
{
    public const int DefaultMaxFramePayloadBytes = 8 * 1_048_576;

    public const long DefaultMaxLogBytes = 512L * 1_048_576;

    public const long DefaultMaxRecords = 100_000;

    public const int DefaultMaxFrameJsonTokens = 262_144;

    public const long DefaultMaxResidentBytes = 512L * 1_048_576;

    public WorldPresentationLimits Limits { get; set; } =
        new WorldPresentationLimits();

    public bool FlushToDiskOnMutation { get; set; } = true;

    public int MaxFramePayloadBytes { get; set; } =
        DefaultMaxFramePayloadBytes;

    public long MaxLogBytes { get; set; } = DefaultMaxLogBytes;

    public long MaxRecords { get; set; } = DefaultMaxRecords;

    public int MaxFrameJsonTokens { get; set; } =
        DefaultMaxFrameJsonTokens;

    public long MaxResidentBytes { get; set; } =
        DefaultMaxResidentBytes;

    public IJournalFaultInjector? FaultInjector { get; set; }
}

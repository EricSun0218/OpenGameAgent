using GameAgent.Core;

namespace GameAgent.Persistence;

public sealed class FileGroupInteractionStoreOptions
{
    public const int DefaultMaxFramePayloadBytes = 32 * 1_048_576;
    public const long DefaultMaxLogBytes = 512L * 1_048_576;
    public const long DefaultMaxMutationFrames = 100_000;
    public const int DefaultMaxSessions = 4_096;

    public GroupInteractionLimits Limits { get; set; } =
        new GroupInteractionLimits();

    public bool FlushToDiskOnMutation { get; set; } = true;

    public int MaxFramePayloadBytes { get; set; } =
        DefaultMaxFramePayloadBytes;

    public long MaxLogBytes { get; set; } = DefaultMaxLogBytes;

    public long MaxMutationFrames { get; set; } =
        DefaultMaxMutationFrames;

    public int MaxSessions { get; set; } = DefaultMaxSessions;

    public IJournalFaultInjector? FaultInjector { get; set; }
}

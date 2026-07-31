using GameAgent.Core;

namespace GameAgent.Persistence;

public enum FileMemorySearchMode
{
    DeterministicLexical = 0,
    Bm25 = 1
}

public sealed class FileMemoryStoreOptions
{
    // Leaves deterministic headroom for the runtime's default 512 KiB
    // aggregate content budget plus record metadata and the idempotency
    // envelope.
    public const int DefaultMaxFramePayloadBytes = 1024 * 1024;
    public const long DefaultMaxLogBytes = 256L * 1024 * 1024;
    public const long DefaultMaxMutationFrames = 100_000;

    public string ProviderId { get; set; } = "deterministic-file";

    public int Capacity { get; set; } = 10_000;

    public bool FlushToDiskOnMutation { get; set; } = true;

    public int MaxFramePayloadBytes { get; set; } =
        DefaultMaxFramePayloadBytes;

    public long MaxLogBytes { get; set; } = DefaultMaxLogBytes;

    public long MaxMutationFrames { get; set; } =
        DefaultMaxMutationFrames;

    /// <summary>
    /// Selects the in-process index rebuilt from verified journal frames.
    /// The default preserves the original deterministic lexical behavior.
    /// </summary>
    public FileMemorySearchMode SearchMode { get; set; } =
        FileMemorySearchMode.DeterministicLexical;

    /// <summary>
    /// Optional bounds and weights used only when
    /// <see cref="SearchMode"/> is <see cref="FileMemorySearchMode.Bm25"/>.
    /// </summary>
    public Bm25MemoryStoreOptions? Bm25Options { get; set; }

    public IJournalFaultInjector? FaultInjector { get; set; }
}

public sealed class MemoryStoreMutationResult
{
    public MemoryStoreMutationResult(long revision, bool changed)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Revision = revision;
        Changed = changed;
    }

    public long Revision { get; }

    public bool Changed { get; }
}

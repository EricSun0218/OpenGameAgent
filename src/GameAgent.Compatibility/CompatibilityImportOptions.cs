namespace GameAgent.Compatibility;

public sealed class CompatibilityImportOptions
{
    public const int DefaultMaxInputBytes = 16 * 1024 * 1024;
    public const int DefaultMaxDecodedPayloadBytes = 4 * 1024 * 1024;
    public const int DefaultMaxJsonDepth = 64;
    public const int DefaultMaxJsonNodes = 100_000;
    public const int DefaultMaxLoreBookEntries = 4096;
    public const int DefaultMaxCollectionItems = 4096;
    public const int DefaultMaxStringCharacters = 1_000_000;
    public const int DefaultMaxPngChunks = 4096;
    public const int DefaultMaxPngChunkBytes = 8 * 1024 * 1024;
    public const int DefaultMaxDirectivesPerEntry = 128;

    public CompatibilityImportOptions(
        int maxInputBytes = DefaultMaxInputBytes,
        int maxDecodedPayloadBytes = DefaultMaxDecodedPayloadBytes,
        int maxJsonDepth = DefaultMaxJsonDepth,
        int maxJsonNodes = DefaultMaxJsonNodes,
        int maxLoreBookEntries = DefaultMaxLoreBookEntries,
        int maxCollectionItems = DefaultMaxCollectionItems,
        int maxStringCharacters = DefaultMaxStringCharacters,
        int maxPngChunks = DefaultMaxPngChunks,
        int maxPngChunkBytes = DefaultMaxPngChunkBytes,
        int maxDirectivesPerEntry = DefaultMaxDirectivesPerEntry)
    {
        MaxInputBytes = RequirePositive(maxInputBytes, nameof(maxInputBytes));
        MaxDecodedPayloadBytes =
            RequirePositive(maxDecodedPayloadBytes, nameof(maxDecodedPayloadBytes));
        MaxJsonDepth = RequireRange(maxJsonDepth, 1, 128, nameof(maxJsonDepth));
        MaxJsonNodes = RequirePositive(maxJsonNodes, nameof(maxJsonNodes));
        MaxLoreBookEntries =
            RequirePositive(maxLoreBookEntries, nameof(maxLoreBookEntries));
        MaxCollectionItems =
            RequirePositive(maxCollectionItems, nameof(maxCollectionItems));
        MaxStringCharacters =
            RequirePositive(maxStringCharacters, nameof(maxStringCharacters));
        MaxPngChunks = RequirePositive(maxPngChunks, nameof(maxPngChunks));
        MaxPngChunkBytes = RequirePositive(maxPngChunkBytes, nameof(maxPngChunkBytes));
        MaxDirectivesPerEntry =
            RequirePositive(maxDirectivesPerEntry, nameof(maxDirectivesPerEntry));
    }

    public int MaxInputBytes { get; }

    public int MaxDecodedPayloadBytes { get; }

    public int MaxJsonDepth { get; }

    public int MaxJsonNodes { get; }

    public int MaxLoreBookEntries { get; }

    public int MaxCollectionItems { get; }

    public int MaxStringCharacters { get; }

    public int MaxPngChunks { get; }

    public int MaxPngChunkBytes { get; }

    public int MaxDirectivesPerEntry { get; }

    private static int RequirePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The limit must be positive.");
        }

        return value;
    }

    private static int RequireRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The limit must be between {minimum} and {maximum}.");
        }

        return value;
    }
}

using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;

namespace GameAgent.World;

/// <summary>
/// Derives named, order-independent entropy from portable world identities.
/// Calling one roll never consumes or shifts any other roll.
/// </summary>
public static class WorldDeterministicEntropy
{
    public const string Version1 = "game-agent.world-entropy.v1";

    public const int DigestByteCount = 32;

    public static string DeriveDigest(
        string entropyVersion,
        string worldSeed,
        string timelineId,
        string occurrenceId,
        string rollKey)
    {
        var identity = NormalizeIdentity(
            entropyVersion,
            worldSeed,
            timelineId,
            occurrenceId,
            rollKey);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in identity)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return GameAgent.Core.CanonicalJsonDigest.ComputeSha256(
            document.RootElement);
    }

    public static IReadOnlyList<byte> DeriveBytes(
        string entropyVersion,
        string worldSeed,
        string timelineId,
        string occurrenceId,
        string rollKey)
    {
        var digest = DeriveDigest(
            entropyVersion,
            worldSeed,
            timelineId,
            occurrenceId,
            rollKey);
        var bytes = new byte[DigestByteCount];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((HexValue(digest[index * 2]) << 4)
                                  | HexValue(digest[(index * 2) + 1]));
        }

        return new ReadOnlyCollection<byte>(bytes);
    }

    /// <summary>
    /// Returns a deterministic integer in [0, exclusiveUpperBound). The
    /// complete 256-bit digest participates in the reduction.
    /// </summary>
    public static long SampleInt64(
        string entropyVersion,
        string worldSeed,
        string timelineId,
        string occurrenceId,
        string rollKey,
        long exclusiveUpperBound)
    {
        if (exclusiveUpperBound <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exclusiveUpperBound));
        }

        var value = ToNonNegativeInteger(
            DeriveBytes(
                entropyVersion,
                worldSeed,
                timelineId,
                occurrenceId,
                rollKey));
        return (long)(value % exclusiveUpperBound);
    }

    /// <summary>
    /// Selects an index from positive integral weights without floating-point
    /// arithmetic. This keeps the result identical across supported engines.
    /// </summary>
    public static int SelectWeightedIndex(
        string entropyVersion,
        string worldSeed,
        string timelineId,
        string occurrenceId,
        string rollKey,
        IReadOnlyList<long> weights)
    {
        if (weights is null)
        {
            throw new ArgumentNullException(nameof(weights));
        }

        if (weights.Count is < 1 or > 65_536)
        {
            throw new ArgumentException(
                "Weights must contain 1 through 65536 entries.",
                nameof(weights));
        }

        long total = 0;
        for (var index = 0; index < weights.Count; index++)
        {
            var weight = weights[index];
            if (weight <= 0)
            {
                throw new ArgumentException(
                    "Every weight must be positive.",
                    nameof(weights));
            }

            try
            {
                total = checked(total + weight);
            }
            catch (OverflowException exception)
            {
                throw new ArgumentException(
                    "The total weight exceeds Int64 capacity.",
                    nameof(weights),
                    exception);
            }
        }

        var target = SampleInt64(
            entropyVersion,
            worldSeed,
            timelineId,
            occurrenceId,
            rollKey,
            total);
        long cursor = 0;
        for (var index = 0; index < weights.Count; index++)
        {
            cursor += weights[index];
            if (target < cursor)
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            "A weighted entropy selection did not resolve an index.");
    }

    private static string[] NormalizeIdentity(
        string entropyVersion,
        string worldSeed,
        string timelineId,
        string occurrenceId,
        string rollKey)
    {
        return new[]
        {
            WorldValidation.Required(
                entropyVersion,
                nameof(entropyVersion),
                96),
            WorldValidation.Required(worldSeed, nameof(worldSeed), 1_024),
            WorldValidation.Required(timelineId, nameof(timelineId)),
            WorldValidation.Required(occurrenceId, nameof(occurrenceId)),
            WorldValidation.Required(rollKey, nameof(rollKey))
        };
    }

    private static BigInteger ToNonNegativeInteger(
        IReadOnlyList<byte> bigEndianBytes)
    {
        var littleEndian = new byte[bigEndianBytes.Count + 1];
        for (var index = 0; index < bigEndianBytes.Count; index++)
        {
            littleEndian[index] =
                bigEndianBytes[bigEndianBytes.Count - index - 1];
        }

        return new BigInteger(littleEndian);
    }

    private static int HexValue(char value)
    {
        return value is >= '0' and <= '9'
            ? value - '0'
            : value - 'a' + 10;
    }
}

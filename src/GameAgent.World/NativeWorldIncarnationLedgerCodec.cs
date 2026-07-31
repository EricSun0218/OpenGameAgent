using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace GameAgent.World;

internal sealed class NativeWorldPackedIncarnationLedger(
    IReadOnlyDictionary<string, long> current,
    IReadOnlyList<WorldIssuedEntityIncarnation> issued)
{
    public IReadOnlyDictionary<string, long> Current { get; } = current;

    public IReadOnlyList<WorldIssuedEntityIncarnation> Issued { get; } = issued;
}

internal static class NativeWorldIncarnationLedgerCodec
{
    // Binary v1: 8-byte magic, uint32 issued count, then
    // [byte UTF-8 length, UTF-8 ID, uint64 incarnation] records, followed
    // by a uint16 current count and sorted uint16 indexes into those records.
    // Base85 uses only characters the default JSON encoder writes verbatim.
    internal const string Base85Alphabet =
        "!#$%()*,-./0123456789:;=?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_abcdefghijklmnopqrstuvwxyz{|}";
    private const int EncodedBlockLength = 5;
    private const int RawBlockLength = 4;
    private const int MaximumEncodedChunkCharacters = 4_194_300;
    private const int MaximumChunks = 8;
    private static readonly byte[] Magic =
        Encoding.ASCII.GetBytes("GAIIL001");
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
    private static readonly sbyte[] Digits = BuildDigits();

    public static IReadOnlyList<string> Encode(
        IReadOnlyDictionary<string, long> current,
        IReadOnlyList<WorldIssuedEntityIncarnation> issued,
        out int byteLength)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        if (issued is null)
        {
            throw new ArgumentNullException(nameof(issued));
        }

        if (issued.Count
            > WorldAuthoritativeStateSnapshot
                .MaximumIssuedIncarnationCount)
        {
            throw new ArgumentException(
                "The issued-incarnation ledger exceeds its item limit.",
                nameof(issued));
        }

        if (current.Count > WorldValidation.MaximumParticipants)
        {
            throw new ArgumentException(
                "The current entity-incarnation collection exceeds its item limit.",
                nameof(current));
        }

        using var raw = new MemoryStream();
        raw.Write(Magic, 0, Magic.Length);
        WriteUInt32(raw, checked((uint)issued.Count));
        var indexes = new Dictionary<string, ushort>(
            StringComparer.Ordinal);
        string? previousEntityId = null;
        long previousIncarnation = -1;
        for (var index = 0; index < issued.Count; index++)
        {
            var item = issued[index]
                       ?? throw new ArgumentException(
                           "The issued-incarnation ledger cannot contain null records.",
                           nameof(issued));
            var entityId = WorldValidation.Required(
                item.EntityId,
                nameof(issued));
            var comparison = previousEntityId is null
                ? -1
                : string.CompareOrdinal(
                    previousEntityId,
                    entityId);
            if (comparison > 0
                || (comparison == 0
                    && previousIncarnation >= item.Incarnation))
            {
                throw new ArgumentException(
                    "Issued entity incarnations must be unique and deterministically ordered.",
                    nameof(issued));
            }

            var utf8 = StrictUtf8.GetBytes(entityId);
            if (utf8.Length is < 1
                or > WorldValidation.MaximumIdentifierUtf8Bytes)
            {
                throw new ArgumentException(
                    "An entity identifier exceeds its UTF-8 byte limit.",
                    nameof(issued));
            }

            raw.WriteByte(checked((byte)utf8.Length));
            raw.Write(utf8, 0, utf8.Length);
            WriteUInt64(raw, checked((ulong)item.Incarnation));
            previousEntityId = entityId;
            previousIncarnation = item.Incarnation;
            indexes[entityId] = checked((ushort)index);
        }

        WriteUInt16(raw, checked((ushort)current.Count));
        foreach (var pair in current.OrderBy(
                     item => item.Key,
                     StringComparer.Ordinal))
        {
            if (!indexes.TryGetValue(pair.Key, out var index)
                || issued[index].Incarnation != pair.Value)
            {
                throw new ArgumentException(
                    "Every current entity incarnation must be present in the issued-incarnation ledger.",
                    nameof(current));
            }

            WriteUInt16(raw, index);
        }

        byteLength = checked((int)raw.Length);
        return EncodeBase85(raw.GetBuffer().AsSpan(0, byteLength));
    }

    public static NativeWorldPackedIncarnationLedger Decode(
        IReadOnlyList<string> chunks,
        int byteLength,
        int maximumIssuedCount,
        int maximumCurrentCount)
    {
        if (chunks is null)
        {
            throw new ArgumentNullException(nameof(chunks));
        }

        if (maximumIssuedCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumIssuedCount));
        }

        if (maximumCurrentCount is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCurrentCount));
        }

        var maximumByteLength = checked(
            Magic.Length
            + sizeof(uint)
            + maximumIssuedCount
            * (1
               + WorldValidation.MaximumIdentifierUtf8Bytes
               + sizeof(ulong))
            + sizeof(ushort)
            + maximumCurrentCount * sizeof(ushort));
        if (byteLength > maximumByteLength)
        {
            throw new InvalidDataException(
                "The packed incarnation ledger exceeds its structural byte limit.");
        }

        var raw = DecodeBase85(chunks, byteLength);
        var offset = 0;
        RequireRemaining(raw, offset, Magic.Length);
        if (!raw.AsSpan(offset, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                "The packed incarnation ledger header is invalid.");
        }

        offset += Magic.Length;
        var encodedIssuedCount = ReadUInt32(raw, ref offset);
        if (encodedIssuedCount > (uint)maximumIssuedCount)
        {
            throw new InvalidDataException(
                "The packed incarnation ledger exceeds its issued-lifetime limit.");
        }

        var issuedCount = (int)encodedIssuedCount;
        var issued = new List<WorldIssuedEntityIncarnation>(
            issuedCount);
        string? previousEntityId = null;
        long previousIncarnation = -1;
        for (var index = 0; index < issuedCount; index++)
        {
            RequireRemaining(raw, offset, 1);
            var idLength = raw[offset++];
            if (idLength == 0
                || idLength
                > WorldValidation.MaximumIdentifierUtf8Bytes)
            {
                throw new InvalidDataException(
                    "A packed entity identifier has an invalid length.");
            }

            RequireRemaining(raw, offset, idLength);
            string entityId;
            try
            {
                entityId = StrictUtf8.GetString(
                    raw,
                    offset,
                    idLength);
                _ = WorldValidation.Required(
                    entityId,
                    nameof(chunks));
            }
            catch (Exception exception) when (
                exception is DecoderFallbackException
                or ArgumentException)
            {
                throw new InvalidDataException(
                    "A packed entity identifier is invalid.",
                    exception);
            }

            offset += idLength;
            var encodedIncarnation = ReadUInt64(raw, ref offset);
            if (encodedIncarnation > long.MaxValue)
            {
                throw new InvalidDataException(
                    "A packed entity incarnation is out of range.");
            }

            var incarnation = checked((long)encodedIncarnation);
            var comparison = previousEntityId is null
                ? -1
                : string.CompareOrdinal(
                    previousEntityId,
                    entityId);
            if (comparison > 0
                || (comparison == 0
                    && previousIncarnation >= incarnation))
            {
                throw new InvalidDataException(
                    "Packed entity incarnations must be unique and deterministically ordered.");
            }

            issued.Add(
                new WorldIssuedEntityIncarnation(
                    entityId,
                    incarnation));
            previousEntityId = entityId;
            previousIncarnation = incarnation;
        }

        var currentCount = ReadUInt16(raw, ref offset);
        if (currentCount > maximumCurrentCount)
        {
            throw new InvalidDataException(
                "The packed current-incarnation map exceeds its item limit.");
        }

        var current = new Dictionary<string, long>(
            currentCount,
            StringComparer.Ordinal);
        var previousIndex = -1;
        for (var index = 0; index < currentCount; index++)
        {
            var issuedIndex = ReadUInt16(raw, ref offset);
            if (issuedIndex >= issued.Count
                || issuedIndex <= previousIndex)
            {
                throw new InvalidDataException(
                    "Packed current-incarnation indexes are invalid or out of order.");
            }

            var item = issued[issuedIndex];
            if (issuedIndex + 1 < issued.Count
                && string.Equals(
                    item.EntityId,
                    issued[issuedIndex + 1].EntityId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A packed current incarnation is not the latest issued lifetime.");
            }

            if (!current.TryAdd(item.EntityId, item.Incarnation))
            {
                throw new InvalidDataException(
                    "Packed current entity identifiers must be unique.");
            }

            previousIndex = issuedIndex;
        }

        if (offset != raw.Length)
        {
            throw new InvalidDataException(
                "The packed incarnation ledger contains trailing data.");
        }

        return new NativeWorldPackedIncarnationLedger(
            new ReadOnlyDictionary<string, long>(current),
            new ReadOnlyCollection<WorldIssuedEntityIncarnation>(
                issued));
    }

    private static IReadOnlyList<string> EncodeBase85(
        ReadOnlySpan<byte> raw)
    {
        var encodedLength = checked(
            ((raw.Length + RawBlockLength - 1)
             / RawBlockLength)
            * EncodedBlockLength);
        var chunks = new List<string>(
            Math.Max(
                1,
                (encodedLength
                 + MaximumEncodedChunkCharacters
                 - 1)
                / MaximumEncodedChunkCharacters));
        var chunk = new StringBuilder(
            Math.Min(
                encodedLength,
                MaximumEncodedChunkCharacters));
        Span<byte> block = stackalloc byte[RawBlockLength];
        Span<char> digits = stackalloc char[EncodedBlockLength];
        for (var offset = 0; offset < raw.Length;
             offset += RawBlockLength)
        {
            block.Clear();
            var count = Math.Min(
                RawBlockLength,
                raw.Length - offset);
            raw.Slice(offset, count).CopyTo(block);
            var value = BinaryPrimitives.ReadUInt32BigEndian(block);
            for (var index = EncodedBlockLength - 1;
                 index >= 0;
                 index--)
            {
                digits[index] = Base85Alphabet[(int)(value % 85)];
                value /= 85;
            }

            if (chunk.Length
                + EncodedBlockLength
                > MaximumEncodedChunkCharacters)
            {
                chunks.Add(chunk.ToString());
                chunk.Clear();
            }

            chunk.Append(digits);
        }

        if (chunk.Length > 0)
        {
            chunks.Add(chunk.ToString());
        }

        if (chunks.Count is < 1 or > MaximumChunks)
        {
            throw new InvalidOperationException(
                "The packed incarnation ledger exceeds its chunk limit.");
        }

        return new ReadOnlyCollection<string>(chunks);
    }

    private static byte[] DecodeBase85(
        IReadOnlyList<string> chunks,
        int byteLength)
    {
        if (byteLength < Magic.Length + sizeof(uint) + sizeof(ushort)
            || chunks.Count is < 1 or > MaximumChunks)
        {
            throw new InvalidDataException(
                "The packed incarnation ledger length is invalid.");
        }

        var expectedCharacters = checked(
            ((byteLength + RawBlockLength - 1)
             / RawBlockLength)
            * EncodedBlockLength);
        long actualCharacters = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            if (string.IsNullOrEmpty(chunk)
                || chunk.Length > MaximumEncodedChunkCharacters
                || index < chunks.Count - 1
                && chunk.Length
                != MaximumEncodedChunkCharacters
                || chunk.Length % EncodedBlockLength != 0)
            {
                throw new InvalidDataException(
                    "A packed incarnation ledger chunk is invalid.");
            }

            actualCharacters = checked(
                actualCharacters + chunk.Length);
        }

        if (actualCharacters != expectedCharacters)
        {
            throw new InvalidDataException(
                "The packed incarnation ledger encoded length is invalid.");
        }

        var encoded = new StringBuilder(expectedCharacters);
        foreach (var chunk in chunks)
        {
            encoded.Append(chunk);
        }

        var paddedLength = checked(
            (expectedCharacters / EncodedBlockLength)
            * RawBlockLength);
        var raw = new byte[paddedLength];
        var rawOffset = 0;
        for (var offset = 0; offset < encoded.Length;
             offset += EncodedBlockLength)
        {
            ulong value = 0;
            for (var index = 0; index < EncodedBlockLength; index++)
            {
                var character = encoded[offset + index];
                var digit = character < Digits.Length
                    ? Digits[character]
                    : (sbyte)-1;
                if (digit < 0)
                {
                    throw new InvalidDataException(
                        "The packed incarnation ledger contains an invalid base85 digit.");
                }

                value = checked(value * 85 + (uint)digit);
            }

            if (value > uint.MaxValue)
            {
                throw new InvalidDataException(
                    "The packed incarnation ledger contains an overflowing base85 block.");
            }

            BinaryPrimitives.WriteUInt32BigEndian(
                raw.AsSpan(rawOffset, RawBlockLength),
                (uint)value);
            rawOffset += RawBlockLength;
        }

        for (var index = byteLength; index < raw.Length; index++)
        {
            if (raw[index] != 0)
            {
                throw new InvalidDataException(
                    "The packed incarnation ledger has non-canonical padding.");
            }
        }

        return raw.AsSpan(0, byteLength).ToArray();
    }

    private static sbyte[] BuildDigits()
    {
        var result = Enumerable.Repeat(
                (sbyte)-1,
                char.MaxValue + 1)
            .ToArray();
        for (var index = 0;
             index < Base85Alphabet.Length;
             index++)
        {
            result[Base85Alphabet[index]] = checked((sbyte)index);
        }

        return result;
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static ushort ReadUInt16(
        byte[] bytes,
        ref int offset)
    {
        RequireRemaining(bytes, offset, sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        return value;
    }

    private static uint ReadUInt32(
        byte[] bytes,
        ref int offset)
    {
        RequireRemaining(bytes, offset, sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        return value;
    }

    private static ulong ReadUInt64(
        byte[] bytes,
        ref int offset)
    {
        RequireRemaining(bytes, offset, sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(offset, sizeof(ulong)));
        offset += sizeof(ulong);
        return value;
    }

    private static void RequireRemaining(
        byte[] bytes,
        int offset,
        int count)
    {
        if (offset < 0
            || count < 0
            || offset > bytes.Length - count)
        {
            throw new InvalidDataException(
                "The packed incarnation ledger is truncated.");
        }
    }
}

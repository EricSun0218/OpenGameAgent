[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string[]]$Path,

    [Alias('DenyRegex')]
    [string[]]$DeniedRegex = @(),

    [switch]$RequireInjectedDenyRegex
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maximumItemBytes = 16777216L
$maximumArchiveBytes = 268435456L
$maximumArchiveDepth = 4
$maximumArchiveEntries = 10000
$maximumInputFiles = 10000
$maximumInputBytes = 268435456L
$expandedArchiveBytes = 0L
$archiveEntryCount = 0
$canonicalValidationBytes = 0L
$inputFileCount = 0
$inputBytes = 0L
$regexTimeout = [TimeSpan]::FromSeconds(1)
$regexOptions = (
    [Text.RegularExpressions.RegexOptions]::CultureInvariant -bor
    [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
    [Text.RegularExpressions.RegexOptions]::Multiline)
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$binaryEncoding = [Text.Encoding]::GetEncoding(28591)
$utf32BigEndianEncoding = [Text.UTF32Encoding]::new($true, $false)

if ($null -eq ('GameAgentReleaseScannerSingleByteReadStream' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;

public sealed class GameAgentReleaseScannerSingleByteReadStream : Stream
{
    private readonly Stream inner;

    public GameAgentReleaseScannerSingleByteReadStream(Stream inner)
    {
        if (inner == null)
        {
            throw new ArgumentNullException("inner");
        }

        this.inner = inner;
    }

    public override bool CanRead { get { return inner.CanRead; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { return inner.Length; } }

    public override long Position
    {
        get { return inner.Position; }
        set { throw new NotSupportedException(); }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        return inner.Read(buffer, offset, 1);
    }

    public override int ReadByte()
    {
        return inner.ReadByte();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}

public static class GameAgentReleaseScannerZlib
{
    private const int MaximumCandidateCount = 4096;
    private const int MaximumDeflateBits = 15;

    private enum DeflateProbeResult
    {
        Invalid,
        Valid,
        WorkLimitExceeded
    }

    private sealed class ProbeBudget
    {
        public ProbeBudget(long remaining)
        {
            Remaining = remaining;
        }

        public long Remaining { get; private set; }
        public bool Exhausted { get; private set; }

        public bool TrySpend(long amount)
        {
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException("amount");
            }
            if (amount > Remaining)
            {
                Remaining = 0;
                Exhausted = true;
                return false;
            }

            Remaining -= amount;
            return true;
        }
    }

    private sealed class DeflateBitReader
    {
        private readonly byte[] bytes;
        private readonly int end;
        private readonly ProbeBudget budget;
        private int position;
        private uint bits;
        private int bitCount;

        public DeflateBitReader(
            byte[] bytes,
            int offset,
            ProbeBudget budget)
        {
            this.bytes = bytes;
            this.position = offset;
            this.end = bytes.Length;
            this.budget = budget;
        }

        public int Position { get { return position; } }
        public int RemainingBytes { get { return end - position; } }
        public bool WorkLimitExceeded { get { return budget.Exhausted; } }

        public bool TryReadBits(int count, out int value)
        {
            value = 0;
            if (count < 0 || count > MaximumDeflateBits)
            {
                return false;
            }

            while (bitCount < count)
            {
                if (position >= end || !budget.TrySpend(1))
                {
                    return false;
                }

                bits |= (uint)bytes[position++] << bitCount;
                bitCount += 8;
            }

            uint mask = count == 0 ? 0U : ((1U << count) - 1U);
            value = (int)(bits & mask);
            bits >>= count;
            bitCount -= count;
            return true;
        }

        public void AlignToByte()
        {
            bits = 0;
            bitCount = 0;
        }

        public bool TryReadUInt16(out int value)
        {
            value = 0;
            AlignToByte();
            if (position > end - 2 || !budget.TrySpend(2))
            {
                return false;
            }

            value = bytes[position] | (bytes[position + 1] << 8);
            position += 2;
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            value = 0;
            AlignToByte();
            if (position >= end || !budget.TrySpend(1))
            {
                return false;
            }

            value = bytes[position++];
            return true;
        }

        public bool TryReadUInt32BigEndian(out uint value)
        {
            value = 0;
            AlignToByte();
            if (position > end - 4 || !budget.TrySpend(4))
            {
                return false;
            }

            value =
                ((uint)bytes[position] << 24)
                | ((uint)bytes[position + 1] << 16)
                | ((uint)bytes[position + 2] << 8)
                | bytes[position + 3];
            position += 4;
            return true;
        }

        public bool TrySkipBytes(int count)
        {
            AlignToByte();
            if (count < 0
                || position > end - count
                || !budget.TrySpend(count))
            {
                return false;
            }

            position += count;
            return true;
        }
    }

    private sealed class DeflateHuffman
    {
        private readonly int[] counts;
        private readonly int[] symbols;

        private DeflateHuffman(int[] counts, int[] symbols)
        {
            this.counts = counts;
            this.symbols = symbols;
        }

        public static bool TryCreate(
            int[] lengths,
            bool allowSingleSymbol,
            bool allowEmpty,
            out DeflateHuffman huffman)
        {
            huffman = null;
            if (lengths == null)
            {
                return false;
            }

            var counts = new int[MaximumDeflateBits + 1];
            int nonzeroCount = 0;
            int maximumLength = 0;
            for (int index = 0; index < lengths.Length; index++)
            {
                int length = lengths[index];
                if (length < 0 || length > MaximumDeflateBits)
                {
                    return false;
                }
                counts[length]++;
                if (length != 0)
                {
                    nonzeroCount++;
                    maximumLength = Math.Max(maximumLength, length);
                }
            }

            int left = 1;
            for (int length = 1;
                 length <= MaximumDeflateBits;
                 length++)
            {
                left = (left << 1) - counts[length];
                if (left < 0)
                {
                    return false;
                }
            }
            if (left > 0
                && !(allowSingleSymbol
                     && nonzeroCount == 1
                     && maximumLength == 1)
                && !(allowEmpty && nonzeroCount == 0))
            {
                return false;
            }

            var offsets = new int[MaximumDeflateBits + 1];
            for (int length = 1;
                 length < MaximumDeflateBits;
                 length++)
            {
                offsets[length + 1] =
                    offsets[length] + counts[length];
            }

            var symbols = new int[lengths.Length - counts[0]];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length != 0)
                {
                    symbols[offsets[length]++] = symbol;
                }
            }

            huffman = new DeflateHuffman(counts, symbols);
            return true;
        }

        public bool TryDecode(
            DeflateBitReader reader,
            out int symbol)
        {
            symbol = 0;
            int code = 0;
            int first = 0;
            int index = 0;
            for (int length = 1;
                 length <= MaximumDeflateBits;
                 length++)
            {
                int bit;
                if (!reader.TryReadBits(1, out bit))
                {
                    return false;
                }

                code |= bit;
                int count = counts[length];
                if (code < first + count)
                {
                    int symbolIndex = index + (code - first);
                    if (symbolIndex < 0 || symbolIndex >= symbols.Length)
                    {
                        return false;
                    }

                    symbol = symbols[symbolIndex];
                    return true;
                }

                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }

            return false;
        }
    }

    private sealed class DeflateOutputTracker
    {
        private readonly byte[] values;
        private readonly bool[] known;
        private readonly int maximumExpandedBytes;
        private readonly ProbeBudget budget;
        private uint a = 1;
        private uint b;

        public DeflateOutputTracker(
            int windowSize,
            int maximumExpandedBytes,
            ProbeBudget budget)
        {
            values = new byte[windowSize];
            known = new bool[windowSize];
            this.maximumExpandedBytes = maximumExpandedBytes;
            this.budget = budget;
            AllKnown = true;
        }

        public long Length { get; private set; }

        public bool AllKnown { get; private set; }

        public uint Adler32 { get { return (b << 16) | a; } }

        public bool TryAppendLiteral(byte value)
        {
            return TryAppend(value, true);
        }

        public bool TryAppendStoredByte(byte value)
        {
            return TryAppend(value, true);
        }

        public bool TryCopy(int distance, int length)
        {
            for (int index = 0; index < length; index++)
            {
                long source = Length - distance;
                if (source < 0)
                {
                    if (!TryAppend(0, false))
                    {
                        return false;
                    }
                    continue;
                }

                int sourceIndex = (int)(source % values.Length);
                if (!TryAppend(
                        values[sourceIndex],
                        known[sourceIndex]))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryAppend(byte value, bool isKnown)
        {
            if (Length >= maximumExpandedBytes
                || !budget.TrySpend(1))
            {
                return false;
            }

            int destination = (int)(Length % values.Length);
            values[destination] = value;
            known[destination] = isKnown;
            Length++;
            if (!isKnown)
            {
                AllKnown = false;
                return true;
            }

            a = (a + value) % 65521;
            b = (b + a) % 65521;
            return true;
        }
    }

    private static readonly int[] LengthBases =
    {
        3, 4, 5, 6, 7, 8, 9, 10,
        11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115,
        131, 163, 195, 227, 258
    };

    private static readonly int[] LengthExtras =
    {
        0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4,
        5, 5, 5, 5, 0
    };

    private static readonly int[] DistanceBases =
    {
        1, 2, 3, 4, 5, 7, 9, 13,
        17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073,
        4097, 6145, 8193, 12289, 16385, 24577
    };

    private static readonly int[] DistanceExtras =
    {
        0, 0, 0, 0, 1, 1, 2, 2,
        3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10,
        11, 11, 12, 12, 13, 13
    };

    private static readonly int[] CodeLengthOrder =
    {
        16, 17, 18, 0, 8, 7, 9, 6, 10,
        5, 11, 4, 12, 3, 13, 2, 14, 1, 15
    };

    public static int IndexOf(byte[] bytes, byte[] magic, int startOffset)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException("bytes");
        }
        if (magic == null)
        {
            throw new ArgumentNullException("magic");
        }
        if (magic.Length == 0)
        {
            throw new ArgumentException("Magic cannot be empty.", "magic");
        }
        if (startOffset < 0 || startOffset > bytes.Length)
        {
            throw new ArgumentOutOfRangeException("startOffset");
        }

        int lastOffset = bytes.Length - magic.Length;
        for (int offset = startOffset; offset <= lastOffset; offset++)
        {
            if (bytes[offset] != magic[0])
            {
                continue;
            }

            int index = 1;
            while (index < magic.Length
                   && bytes[offset + index] == magic[index])
            {
                index++;
            }
            if (index == magic.Length)
            {
                return offset;
            }
        }

        return -1;
    }

    public static uint ComputeCrc32(byte[] bytes)
    {
        return ComputeCrc32(bytes, 0, bytes == null ? 0 : bytes.Length);
    }

    public static uint ComputeCrc32(byte[] bytes, int offset, int count)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException("bytes");
        }
        if (offset < 0 || count < 0 || offset > bytes.Length - count)
        {
            throw new ArgumentOutOfRangeException("offset");
        }

        uint crc = 0xffffffffU;
        int end = offset + count;
        for (int index = offset; index < end; index++)
        {
            crc ^= bytes[index];
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = 0U - (crc & 1U);
                crc = (crc >> 1) ^ (0xedb88320U & mask);
            }
        }

        return ~crc;
    }

    public static bool ContainsValidFrame(byte[] bytes, int maximumExpandedBytes)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException("bytes");
        }
        if (maximumExpandedBytes < 1)
        {
            throw new ArgumentOutOfRangeException("maximumExpandedBytes");
        }

        var budget = new ProbeBudget(maximumExpandedBytes);
        int candidateCount = 0;
        for (int offset = 0; offset <= bytes.Length - 6; offset++)
        {
            if (!HasHeader(bytes, offset))
            {
                continue;
            }

            candidateCount++;
            if (candidateCount > MaximumCandidateCount
                || !budget.TrySpend(1))
            {
                return true;
            }

            bool hasDictionary = (bytes[offset + 1] & 0x20) != 0;
            if (hasDictionary
                ? IsStructurallyValidDictionaryFrame(
                    bytes,
                    offset,
                    maximumExpandedBytes,
                    budget)
                : IsValidFrame(
                    bytes,
                    offset,
                    maximumExpandedBytes,
                    budget))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasHeader(byte[] bytes, int offset)
    {
        int cmf = bytes[offset];
        int flg = bytes[offset + 1];
        return (cmf & 0x0f) == 8
            && (cmf >> 4) <= 7
            && ((cmf * 256 + flg) % 31) == 0;
    }

    private static bool IsValidFrame(
        byte[] bytes,
        int offset,
        int maximumExpandedBytes,
        ProbeBudget budget)
    {
        int windowSize = 1 << ((bytes[offset] >> 4) + 8);
        var reader = new DeflateBitReader(bytes, offset + 2, budget);
        var result = ProbeDeflate(
            reader,
            windowSize,
            maximumExpandedBytes,
            budget,
            allowPresetDictionary: false,
            requirePayloadBoundary: false);
        return result != DeflateProbeResult.Invalid;
    }

    private static bool IsStructurallyValidDictionaryFrame(
        byte[] bytes,
        int offset,
        int maximumExpandedBytes,
        ProbeBudget budget)
    {
        if (offset > bytes.Length - 10)
        {
            return false;
        }
        if (!budget.TrySpend(4))
        {
            return true;
        }

        var reader = new DeflateBitReader(bytes, offset + 6, budget);
        int windowSize = 1 << ((bytes[offset] >> 4) + 8);
        var result = ProbeDeflate(
            reader,
            windowSize,
            maximumExpandedBytes,
            budget,
            allowPresetDictionary: true,
            requirePayloadBoundary: true);
        return result != DeflateProbeResult.Invalid;
    }

    private static DeflateProbeResult ProbeDeflate(
        DeflateBitReader reader,
        int windowSize,
        int maximumExpandedBytes,
        ProbeBudget budget,
        bool allowPresetDictionary,
        bool requirePayloadBoundary)
    {
        var output = new DeflateOutputTracker(
            windowSize,
            maximumExpandedBytes,
            budget);
        bool finalBlock = false;
        while (!finalBlock)
        {
            int final;
            int blockType;
            if (!reader.TryReadBits(1, out final)
                || !reader.TryReadBits(2, out blockType))
            {
                return ProbeFailure(reader);
            }
            finalBlock = final != 0;

            if (blockType == 0)
            {
                int length;
                int complement;
                if (!reader.TryReadUInt16(out length)
                    || !reader.TryReadUInt16(out complement))
                {
                    return ProbeFailure(reader);
                }
                if ((length ^ 0xffff) != complement)
                {
                    return DeflateProbeResult.Invalid;
                }
                for (int index = 0; index < length; index++)
                {
                    byte value;
                    if (!reader.TryReadByte(out value))
                    {
                        return ProbeFailure(reader);
                    }
                    if (!output.TryAppendStoredByte(value))
                    {
                        return DeflateProbeResult.WorkLimitExceeded;
                    }
                }
                continue;
            }

            DeflateHuffman literalLengthTree;
            DeflateHuffman distanceTree;
            if (blockType == 1)
            {
                if (!TryCreateFixedTrees(
                        out literalLengthTree,
                        out distanceTree))
                {
                    return DeflateProbeResult.Invalid;
                }
            }
            else if (blockType == 2)
            {
                var treeResult = TryReadDynamicTrees(
                    reader,
                    out literalLengthTree,
                    out distanceTree);
                if (treeResult != DeflateProbeResult.Valid)
                {
                    return treeResult;
                }
            }
            else
            {
                return DeflateProbeResult.Invalid;
            }

            while (true)
            {
                int symbol;
                if (!literalLengthTree.TryDecode(reader, out symbol))
                {
                    return ProbeFailure(reader);
                }
                if (symbol < 256)
                {
                    if (!output.TryAppendLiteral((byte)symbol))
                    {
                        return DeflateProbeResult.WorkLimitExceeded;
                    }
                    continue;
                }
                if (symbol == 256)
                {
                    break;
                }
                if (symbol < 257 || symbol > 285)
                {
                    return DeflateProbeResult.Invalid;
                }

                int lengthIndex = symbol - 257;
                int extraLength;
                if (!reader.TryReadBits(
                        LengthExtras[lengthIndex],
                        out extraLength))
                {
                    return ProbeFailure(reader);
                }
                int length = LengthBases[lengthIndex] + extraLength;

                int distanceSymbol;
                if (!distanceTree.TryDecode(reader, out distanceSymbol))
                {
                    return ProbeFailure(reader);
                }
                if (distanceSymbol < 0 || distanceSymbol > 29)
                {
                    return DeflateProbeResult.Invalid;
                }
                int extraDistance;
                if (!reader.TryReadBits(
                        DistanceExtras[distanceSymbol],
                        out extraDistance))
                {
                    return ProbeFailure(reader);
                }
                int distance =
                    DistanceBases[distanceSymbol] + extraDistance;
                if (distance < 1 || distance > windowSize)
                {
                    return DeflateProbeResult.Invalid;
                }
                if (!allowPresetDictionary
                    && distance > output.Length)
                {
                    return DeflateProbeResult.Invalid;
                }
                if (!output.TryCopy(distance, length))
                {
                    return DeflateProbeResult.WorkLimitExceeded;
                }
            }
        }

        reader.AlignToByte();
        // Without the preset dictionary the output checksum cannot be
        // authenticated. Requiring that form to end at this payload boundary
        // avoids classifying ordinary binary or source bytes as an archive;
        // enclosing archives are scanned entry by entry at their own boundary.
        if (requirePayloadBoundary && reader.RemainingBytes != 4)
        {
            return DeflateProbeResult.Invalid;
        }
        uint expectedAdler;
        if (!reader.TryReadUInt32BigEndian(out expectedAdler))
        {
            return ProbeFailure(reader);
        }
        if (output.AllKnown && output.Adler32 != expectedAdler)
        {
            return DeflateProbeResult.Invalid;
        }
        return DeflateProbeResult.Valid;
    }

    private static DeflateProbeResult TryReadDynamicTrees(
        DeflateBitReader reader,
        out DeflateHuffman literalLengthTree,
        out DeflateHuffman distanceTree)
    {
        literalLengthTree = null;
        distanceTree = null;
        int literalCountBits;
        int distanceCountBits;
        int codeLengthCountBits;
        if (!reader.TryReadBits(5, out literalCountBits)
            || !reader.TryReadBits(5, out distanceCountBits)
            || !reader.TryReadBits(4, out codeLengthCountBits))
        {
            return ProbeFailure(reader);
        }

        int literalCount = literalCountBits + 257;
        int distanceCount = distanceCountBits + 1;
        int codeLengthCount = codeLengthCountBits + 4;
        var codeLengthLengths = new int[19];
        for (int index = 0; index < codeLengthCount; index++)
        {
            int length;
            if (!reader.TryReadBits(3, out length))
            {
                return ProbeFailure(reader);
            }
            codeLengthLengths[CodeLengthOrder[index]] = length;
        }

        DeflateHuffman codeLengthTree;
        if (!DeflateHuffman.TryCreate(
                codeLengthLengths,
                false,
                false,
                out codeLengthTree))
        {
            return DeflateProbeResult.Invalid;
        }

        int total = literalCount + distanceCount;
        var lengths = new int[total];
        int position = 0;
        int previous = 0;
        while (position < total)
        {
            int symbol;
            if (!codeLengthTree.TryDecode(reader, out symbol))
            {
                return ProbeFailure(reader);
            }

            if (symbol <= 15)
            {
                previous = symbol;
                lengths[position++] = symbol;
                continue;
            }

            int repeat;
            int value;
            if (symbol == 16)
            {
                if (position == 0)
                {
                    return DeflateProbeResult.Invalid;
                }
                int repeatBits;
                if (!reader.TryReadBits(2, out repeatBits))
                {
                    return ProbeFailure(reader);
                }
                repeat = repeatBits + 3;
                value = previous;
            }
            else if (symbol == 17)
            {
                int repeatBits;
                if (!reader.TryReadBits(3, out repeatBits))
                {
                    return ProbeFailure(reader);
                }
                repeat = repeatBits + 3;
                value = 0;
                previous = 0;
            }
            else if (symbol == 18)
            {
                int repeatBits;
                if (!reader.TryReadBits(7, out repeatBits))
                {
                    return ProbeFailure(reader);
                }
                repeat = repeatBits + 11;
                value = 0;
                previous = 0;
            }
            else
            {
                return DeflateProbeResult.Invalid;
            }

            if (repeat > total - position)
            {
                return DeflateProbeResult.Invalid;
            }
            for (int index = 0; index < repeat; index++)
            {
                lengths[position++] = value;
            }
        }

        var literalLengths = new int[literalCount];
        var distanceLengths = new int[distanceCount];
        Array.Copy(lengths, 0, literalLengths, 0, literalCount);
        Array.Copy(
            lengths,
            literalCount,
            distanceLengths,
            0,
            distanceCount);
        if (literalLengths[256] == 0
            || !DeflateHuffman.TryCreate(
                literalLengths,
                true,
                false,
                out literalLengthTree)
            || !DeflateHuffman.TryCreate(
                distanceLengths,
                true,
                true,
                out distanceTree))
        {
            return DeflateProbeResult.Invalid;
        }

        return DeflateProbeResult.Valid;
    }

    private static bool TryCreateFixedTrees(
        out DeflateHuffman literalLengthTree,
        out DeflateHuffman distanceTree)
    {
        literalLengthTree = null;
        distanceTree = null;
        var literalLengths = new int[288];
        for (int symbol = 0; symbol <= 143; symbol++)
        {
            literalLengths[symbol] = 8;
        }
        for (int symbol = 144; symbol <= 255; symbol++)
        {
            literalLengths[symbol] = 9;
        }
        for (int symbol = 256; symbol <= 279; symbol++)
        {
            literalLengths[symbol] = 7;
        }
        for (int symbol = 280; symbol <= 287; symbol++)
        {
            literalLengths[symbol] = 8;
        }

        var distanceLengths = new int[32];
        for (int symbol = 0; symbol < distanceLengths.Length; symbol++)
        {
            distanceLengths[symbol] = 5;
        }

        if (!DeflateHuffman.TryCreate(
                literalLengths,
                false,
                false,
                out literalLengthTree))
        {
            return false;
        }
        return DeflateHuffman.TryCreate(
            distanceLengths,
            false,
            false,
            out distanceTree);
    }

    private static DeflateProbeResult ProbeFailure(
        DeflateBitReader reader)
    {
        return reader.WorkLimitExceeded
            ? DeflateProbeResult.WorkLimitExceeded
            : DeflateProbeResult.Invalid;
    }
}
'@
}

function Add-PrivatePathForms {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[string]]$Destination,

        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $fullPath = [IO.Path]::GetFullPath($Value).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
    if ($fullPath.Length -lt 4) {
        return
    }

    $forward = $fullPath.Replace('\', '/').TrimEnd('/')
    $backward = $fullPath.Replace('/', '\').TrimEnd('\')
    foreach ($form in @(
            ($forward + '/'),
            ($backward + '\'))) {
        if (-not $Destination.Contains($form)) {
            $Destination.Add($form)
        }
    }
}

function New-BoundedRegex {
    param(
        [Parameter(Mandatory)]
        [string]$Pattern,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    try {
        return [regex]::new(
            $Pattern,
            $script:regexOptions,
            $script:regexTimeout)
    }
    catch {
        throw $FailureMessage
    }
}

$privatePathForms = New-Object 'Collections.Generic.List[string]'
Add-PrivatePathForms -Destination $privatePathForms -Value $repositoryRoot
$userProfile = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::UserProfile)
if (-not [string]::Equals(
        $userProfile,
        '/root',
        [StringComparison]::Ordinal)) {
    Add-PrivatePathForms `
        -Destination $privatePathForms `
        -Value $userProfile
}

$privatePathRegexes = @(
    (New-BoundedRegex `
        -Pattern '[a-z]:[\\/]+users[\\/]+[^\x00\r\n\\/]{1,128}[\\/]' `
        -FailureMessage 'The release path scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '/(?:home|users)/[^/\x00\r\n]{1,128}/' `
        -FailureMessage 'The release path scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '/root/(?:work|documents|source|src|workspace)/' `
        -FailureMessage 'The release path scanner could not be initialized.'))

$credentialRegexes = @(
    (New-BoundedRegex `
        -Pattern '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])sk-[a-z0-9_-]{20,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])github_pat_[a-z0-9_]{20,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])gh[pousr]_[a-z0-9]{20,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])AIza[0-9A-Za-z_-]{30,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9_-])eyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '\bBearer\s+[a-z0-9._~+/-]{20,}={0,2}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])["'']?(?:[a-z0-9]+[_-]+)*(?:api[_-]?key|access[_-]?token|client[_-]?secret|authorization)["'']?\s*[:=]\s*["''][a-z0-9_./+=-]{20,}["'']' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '^\s*(?:export\s+)?["'']?(?:[a-z0-9]+[_-]+)*(?:api[_-]?key|access[_-]?token|client[_-]?secret|authorization)["'']?\s*[:=]\s*[a-z0-9_./+=-]{20,}\s*(?:[#;].*)?$' `
        -FailureMessage 'The credential scanner could not be initialized.'))

$deniedPatterns = New-Object 'Collections.Generic.List[string]'
foreach ($pattern in $DeniedRegex) {
    if (-not [string]::IsNullOrWhiteSpace($pattern)) {
        $deniedPatterns.Add($pattern)
    }
}
$injectedPatterns = [Environment]::GetEnvironmentVariable(
    'GAME_AGENT_RELEASE_DENY_REGEX')
if ($RequireInjectedDenyRegex -and
    [string]::IsNullOrWhiteSpace($injectedPatterns)) {
    throw 'The required release deny configuration is unavailable.'
}
if (-not [string]::IsNullOrWhiteSpace($injectedPatterns)) {
    foreach ($pattern in ($injectedPatterns -split '\r?\n')) {
        if (-not [string]::IsNullOrWhiteSpace($pattern)) {
            $deniedPatterns.Add($pattern)
        }
    }
}
$deniedRegexes = @(
    foreach ($pattern in $deniedPatterns) {
        New-BoundedRegex `
            -Pattern $pattern `
            -FailureMessage (
                'An externally supplied release deny expression is invalid.')
    }
)

function Get-ArtifactItemId {
    param([Parameter(Mandatory)][string]$Label)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Label)
        $hash = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $hex = [BitConverter]::ToString($hash) -replace '-', ''
    return $hex.Substring(0, 12).ToLowerInvariant()
}

function Test-RegexMatch {
    param(
        [Parameter(Mandatory)]
        [regex]$Regex,

        [Parameter(Mandatory)]
        [string]$Value
    )

    try {
        return $Regex.IsMatch($Value)
    }
    catch [Text.RegularExpressions.RegexMatchTimeoutException] {
        throw 'A release scan expression exceeded its safety limit.'
    }
}

function Assert-SafeText {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$ItemId
    )

    foreach ($privatePath in $script:privatePathForms) {
        if ($Value.IndexOf(
                $privatePath,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Release artifact item '$ItemId' contains a private build path."
        }
    }

    foreach ($pathRegex in $script:privatePathRegexes) {
        if (Test-RegexMatch -Regex $pathRegex -Value $Value) {
            throw "Release artifact item '$ItemId' contains a local user path."
        }
    }

    foreach ($credentialRegex in $script:credentialRegexes) {
        if (Test-RegexMatch -Regex $credentialRegex -Value $Value) {
            throw "Release artifact item '$ItemId' contains credential-like data."
        }
    }

    foreach ($deniedRegex in $script:deniedRegexes) {
        if (Test-RegexMatch -Regex $deniedRegex -Value $Value) {
            throw "Release artifact item '$ItemId' contains denied release data."
        }
    }
}

function Assert-ArtifactName {
    param([Parameter(Mandatory)][string]$Name)

    $itemId = Get-ArtifactItemId -Label $Name
    if ($Name.Length -gt 4096 -or $Name.IndexOf([char]0) -ge 0) {
        throw "Release artifact item '$ItemId' has an unsafe name."
    }

    $normalized = $Name.Replace('\', '/')
    $segments = @($normalized.Split('/') | Where-Object { $_.Length -gt 0 })
    if ($normalized.StartsWith('/', [StringComparison]::Ordinal) -or
        $normalized -match '^[a-z]:' -or
        $segments -contains '..') {
        throw "Release artifact item '$ItemId' has an unsafe rooted or traversal name."
    }

    Assert-SafeText -Value $Name -ItemId $itemId
}

function Assert-ArtifactBytes {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $itemId = Get-ArtifactItemId -Label $Label
    $views = New-Object 'Collections.Generic.List[string]'
    $views.Add($script:binaryEncoding.GetString($Bytes))
    $views.Add([Text.Encoding]::UTF8.GetString($Bytes))
    for ($offset = 0; $offset -lt 2; $offset++) {
        if ($offset -lt $Bytes.Length) {
            $count = $Bytes.Length - $offset
            $views.Add(
                [Text.Encoding]::Unicode.GetString($Bytes, $offset, $count))
            $views.Add(
                [Text.Encoding]::BigEndianUnicode.GetString(
                    $Bytes,
                    $offset,
                    $count))
        }
    }
    for ($offset = 0; $offset -lt 4; $offset++) {
        if ($offset -lt $Bytes.Length) {
            $count = $Bytes.Length - $offset
            $views.Add(
                [Text.Encoding]::UTF32.GetString($Bytes, $offset, $count))
            $views.Add(
                $script:utf32BigEndianEncoding.GetString(
                    $Bytes,
                    $offset,
                    $count))
        }
    }
    foreach ($view in $views) {
        Assert-SafeText -Value $view -ItemId $itemId
    }
}

function Find-ZipEndRecordOffset {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    if ($Bytes.Length -lt 22) {
        return -1
    }

    $minimumOffset = [Math]::Max(0, $Bytes.Length - 65557)
    for ($offset = $Bytes.Length - 22;
         $offset -ge $minimumOffset;
         $offset--) {
        if ($Bytes[$offset] -eq 0x50 -and
            $Bytes[$offset + 1] -eq 0x4b -and
            $Bytes[$offset + 2] -eq 0x05 -and
            $Bytes[$offset + 3] -eq 0x06 -and
            ($offset + 22 +
                [BitConverter]::ToUInt16($Bytes, $offset + 20)) -eq
                $Bytes.Length) {
            return $offset
        }
    }

    return -1
}

function Test-ZipPayload {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    if ($Bytes.Length -lt 4) {
        return $false
    }

    if ($Bytes[0] -eq 0x50 -and
        $Bytes[1] -eq 0x4b -and (
        ($Bytes[2] -eq 0x03 -and $Bytes[3] -eq 0x04) -or
        ($Bytes[2] -eq 0x05 -and $Bytes[3] -eq 0x06) -or
        ($Bytes[2] -eq 0x07 -and $Bytes[3] -eq 0x08))) {
        return $true
    }

    return (Find-ZipEndRecordOffset -Bytes $Bytes) -ge 0
}

function Test-ArchiveName {
    param([Parameter(Mandatory)][string]$Name)

    $lowerName = $Name.ToLowerInvariant()
    $extension = [IO.Path]::GetExtension($lowerName)
    return $lowerName.EndsWith(
            '.tar.gz',
            [StringComparison]::Ordinal) -or
        $lowerName.EndsWith(
            '.tar.zst',
            [StringComparison]::Ordinal) -or
        $extension -in @(
        '.zip',
        '.nupkg',
        '.snupkg',
        '.jar',
        '.apk',
        '.docx',
        '.xlsx',
        '.pptx',
        '.vsix',
        '.gz',
        '.tgz',
        '.tar',
        '.7z',
        '.rar',
        '.cab',
        '.bz2',
        '.xz',
        '.zlib',
        '.zz',
        '.zst',
        '.lz4')
}

function Read-BoundedStream {
    param(
        [Parameter(Mandatory)]
        [IO.Stream]$Stream,

        [Parameter(Mandatory)]
        [long]$MaximumBytes
    )

    $memory = New-Object IO.MemoryStream
    $buffer = New-Object byte[] 81920
    $total = 0L
    try {
        while ($true) {
            $read = $Stream.Read($buffer, 0, $buffer.Length)
            if ($read -eq 0) {
                return ,$memory.ToArray()
            }

            $total += $read
            if ($total -gt $MaximumBytes) {
                throw 'A release archive entry exceeds the privacy scanner limit.'
            }
            $memory.Write($buffer, 0, $read)
        }
    }
    finally {
        $memory.Dispose()
    }
}

function Find-MagicOffset {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [byte[]]$Magic,

        [int]$StartOffset = 0
    )

    if ($StartOffset -lt 0) {
        $StartOffset = 0
    }
    return [GameAgentReleaseScannerZlib]::IndexOf(
        $Bytes,
        $Magic,
        $StartOffset)
}

function Read-GzipMember {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset
    )

    if ($Offset -lt 0 -or $Offset + 18 -gt $Bytes.Length -or
        $Bytes[$Offset] -ne 0x1f -or
        $Bytes[$Offset + 1] -ne 0x8b -or
        $Bytes[$Offset + 2] -ne 0x08) {
        return $null
    }

    $flags = $Bytes[$Offset + 3]
    if ($flags -ne 0) {
        return $null
    }
    $cursor = $Offset + 10
    if ($cursor + 8 -gt $Bytes.Length) {
        return $null
    }

    Add-Type -AssemblyName System.IO.Compression
    $memory = [IO.MemoryStream]::new($Bytes, $false)
    $memory.Position = $cursor
    $limited = [GameAgentReleaseScannerSingleByteReadStream]::new($memory)
    $deflate = $null
    $trailerOffset = -1
    try {
        $deflate = [IO.Compression.DeflateStream]::new(
            $limited,
            [IO.Compression.CompressionMode]::Decompress,
            $true)
        $expanded = Read-BoundedStream `
            -Stream $deflate `
            -MaximumBytes $script:maximumItemBytes
        $trailerOffset = [int]$memory.Position
    }
    catch [IO.InvalidDataException] {
        return $null
    }
    catch [ArgumentException] {
        return $null
    }
    finally {
        if ($null -ne $deflate) {
            $deflate.Dispose()
        }
        $limited.Dispose()
        $memory.Dispose()
    }

    if ($trailerOffset + 8 -gt $Bytes.Length) {
        return $null
    }
    $expectedLength = [BitConverter]::ToUInt32(
        $Bytes,
        $trailerOffset + 4)
    $expectedCrc = [BitConverter]::ToUInt32(
        $Bytes,
        $trailerOffset)
    $actualLength = [uint32](
        $expanded.LongLength % ([int64][uint32]::MaxValue + 1L))
    $actualCrc = [GameAgentReleaseScannerZlib]::ComputeCrc32($expanded)
    if ($actualLength -ne $expectedLength -or
        $actualCrc -ne $expectedCrc) {
        return $null
    }

    return @{
        EndOffset = $trailerOffset + 8
        Expanded = $expanded
    }
}

function Get-Adler32 {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $a = [uint64]1
    $b = [uint64]0
    foreach ($value in $Bytes) {
        $a = ($a + $value) % 65521
        $b = ($b + $a) % 65521
    }

    return [uint32](($b -shl 16) -bor $a)
}

function Test-ZlibHeaderAtOffset {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset
    )

    if ($Offset -lt 0 -or $Offset + 2 -gt $Bytes.Length) {
        return $false
    }
    $cmf = [int]$Bytes[$Offset]
    $flg = [int]$Bytes[$Offset + 1]
    return ($cmf -band 0x0f) -eq 8 -and
        ($cmf -shr 4) -le 7 -and
        (($cmf * 256 + $flg) % 31) -eq 0
}

function Read-ZlibMember {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset
    )

    if (-not (Test-ZlibHeaderAtOffset -Bytes $Bytes -Offset $Offset) -or
        $Offset + 6 -gt $Bytes.Length) {
        return $null
    }
    if (($Bytes[$Offset + 1] -band 0x20) -ne 0) {
        throw 'A release zlib payload uses a preset dictionary.'
    }

    Add-Type -AssemblyName System.IO.Compression
    $memory = [IO.MemoryStream]::new($Bytes, $false)
    $memory.Position = $Offset + 2
    $limited = [GameAgentReleaseScannerSingleByteReadStream]::new($memory)
    $deflate = $null
    $trailerOffset = -1L
    try {
        $deflate = [IO.Compression.DeflateStream]::new(
            $limited,
            [IO.Compression.CompressionMode]::Decompress,
            $true)
        $expanded = Read-BoundedStream `
            -Stream $deflate `
            -MaximumBytes $script:maximumItemBytes
        $trailerOffset = $memory.Position
    }
    catch [IO.InvalidDataException] {
        return $null
    }
    catch [ArgumentException] {
        return $null
    }
    finally {
        if ($null -ne $deflate) {
            $deflate.Dispose()
        }
        $limited.Dispose()
        $memory.Dispose()
    }

    if ($trailerOffset + 4 -gt $Bytes.LongLength) {
        return $null
    }
    $expectedAdler = (
        ([uint32]$Bytes[[int]$trailerOffset] -shl 24) -bor
        ([uint32]$Bytes[[int]$trailerOffset + 1] -shl 16) -bor
        ([uint32]$Bytes[[int]$trailerOffset + 2] -shl 8) -bor
        [uint32]$Bytes[[int]$trailerOffset + 3])
    if ((Get-Adler32 -Bytes $expanded) -ne $expectedAdler) {
        return $null
    }

    return @{
        EndOffset = $trailerOffset + 4
        Expanded = $expanded
    }
}

function Assert-NoEmbeddedArchivePayload {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [switch]$AllowTarHeaderAt257
    )

    $signatures = @(
        ([byte[]](0x50, 0x4b, 0x03, 0x04)),
        ([byte[]](0x50, 0x4b, 0x05, 0x06)),
        ([byte[]](0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c)),
        ([byte[]](0x52, 0x61, 0x72, 0x21, 0x1a, 0x07)),
        ([byte[]](0x4d, 0x53, 0x43, 0x46)),
        ([byte[]](0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00)),
        ([byte[]](0x28, 0xb5, 0x2f, 0xfd)),
        ([byte[]](0x04, 0x22, 0x4d, 0x18)),
        ([byte[]](0x02, 0x21, 0x4c, 0x18)))
    foreach ($signature in $signatures) {
        if ((Find-MagicOffset -Bytes $Bytes -Magic $signature) -ge 0) {
            throw 'A release payload contains embedded archive data.'
        }
    }

    $bzipOffset = Find-MagicOffset `
        -Bytes $Bytes `
        -Magic ([byte[]](0x42, 0x5a, 0x68))
    while ($bzipOffset -ge 0) {
        if ($bzipOffset + 3 -lt $Bytes.Length -and
            $Bytes[$bzipOffset + 3] -ge 0x31 -and
            $Bytes[$bzipOffset + 3] -le 0x39) {
            throw 'A release payload contains embedded archive data.'
        }
        $bzipOffset = Find-MagicOffset `
            -Bytes $Bytes `
            -Magic ([byte[]](0x42, 0x5a, 0x68)) `
            -StartOffset ($bzipOffset + 1)
    }

    $gzipOffset = Find-MagicOffset `
        -Bytes $Bytes `
        -Magic ([byte[]](0x1f, 0x8b))
    while ($gzipOffset -ge 0) {
        if ($gzipOffset + 3 -lt $Bytes.Length -and
            $Bytes[$gzipOffset + 2] -eq 0x08 -and
            ($Bytes[$gzipOffset + 3] -band 0xe0) -eq 0) {
            throw 'A release payload contains embedded archive data.'
        }
        $gzipOffset = Find-MagicOffset `
            -Bytes $Bytes `
            -Magic ([byte[]](0x1f, 0x8b)) `
            -StartOffset ($gzipOffset + 1)
    }

    if ([GameAgentReleaseScannerZlib]::ContainsValidFrame(
            $Bytes,
            [int]$script:maximumItemBytes)) {
        throw 'A release payload contains embedded archive data.'
    }

    for ($offset = 0; $offset -le $Bytes.Length - 4; $offset++) {
        if ($Bytes[$offset] -ge 0x50 -and
            $Bytes[$offset] -le 0x5f -and
            $Bytes[$offset + 1] -eq 0x2a -and
            $Bytes[$offset + 2] -eq 0x4d -and
            $Bytes[$offset + 3] -eq 0x18) {
            throw 'A release payload contains embedded archive data.'
        }
    }

    $tarOffset = Find-MagicOffset `
        -Bytes $Bytes `
        -Magic ([byte[]](0x75, 0x73, 0x74, 0x61, 0x72))
    while ($tarOffset -ge 0) {
        if ($tarOffset -ge 257) {
            $headerOffset = $tarOffset - 257
            if ((-not $AllowTarHeaderAt257 -or $headerOffset -ne 0) -and
                (Test-ValidTarHeaderAtOffset `
                    -Bytes $Bytes `
                    -HeaderOffset $headerOffset)) {
                throw 'A release payload contains embedded archive data.'
            }
        }
        $tarOffset = Find-MagicOffset `
            -Bytes $Bytes `
            -Magic ([byte[]](0x75, 0x73, 0x74, 0x61, 0x72)) `
            -StartOffset ($tarOffset + 1)
    }
}

function Assert-ZipCanonicalCoverage {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$EndRecordOffset,

        [Parameter(Mandatory)]
        [uint32]$DirectoryOffset,

        [Parameter(Mandatory)]
        [uint32]$DirectoryBytes,

        [Parameter(Mandatory)]
        [uint16]$TotalEntries
    )

    if (([uint64]$DirectoryOffset + [uint64]$DirectoryBytes) -ne
        [uint64]$EndRecordOffset) {
        throw 'A release archive contains unreferenced directory data.'
    }
    if ([BitConverter]::ToUInt16($Bytes, $EndRecordOffset + 20) -ne 0) {
        throw 'A release archive comment is not supported.'
    }
    if ($TotalEntries -eq 0) {
        if ($DirectoryOffset -ne 0 -or
            $DirectoryBytes -ne 0 -or
            $EndRecordOffset -ne 0) {
            throw 'An empty release archive contains unreferenced data.'
        }
        return
    }
    if ($Bytes.Length -lt 4 -or
        $Bytes[0] -ne 0x50 -or
        $Bytes[1] -ne 0x4b -or
        $Bytes[2] -ne 0x03 -or
        $Bytes[3] -ne 0x04) {
        throw 'A release archive contains an unsupported prefix.'
    }

    $localRecords = New-Object 'Collections.Generic.List[object]'
    $declaredExpandedBytes = 0L
    $cursor = [int]$DirectoryOffset
    for ($entryIndex = 0; $entryIndex -lt $TotalEntries; $entryIndex++) {
        if ($cursor + 46 -gt $EndRecordOffset -or
            $Bytes[$cursor] -ne 0x50 -or
            $Bytes[$cursor + 1] -ne 0x4b -or
            $Bytes[$cursor + 2] -ne 0x01 -or
            $Bytes[$cursor + 3] -ne 0x02) {
            throw 'A release archive central directory is malformed.'
        }
        $flags = [BitConverter]::ToUInt16($Bytes, $cursor + 8)
        $method = [BitConverter]::ToUInt16($Bytes, $cursor + 10)
        $crc = [BitConverter]::ToUInt32($Bytes, $cursor + 16)
        $compressedSize = [BitConverter]::ToUInt32($Bytes, $cursor + 20)
        $uncompressedSize = [BitConverter]::ToUInt32($Bytes, $cursor + 24)
        $nameLength = [BitConverter]::ToUInt16($Bytes, $cursor + 28)
        $extraLength = [BitConverter]::ToUInt16($Bytes, $cursor + 30)
        $commentLength = [BitConverter]::ToUInt16($Bytes, $cursor + 32)
        $diskStart = [BitConverter]::ToUInt16($Bytes, $cursor + 34)
        $localOffset = [BitConverter]::ToUInt32($Bytes, $cursor + 42)
        if ($diskStart -ne 0 -or
            $compressedSize -eq [uint32]::MaxValue -or
            $uncompressedSize -eq [uint32]::MaxValue -or
            $localOffset -eq [uint32]::MaxValue -or
            ($flags -band 0xf7ff) -ne 0 -or
            $method -notin @(0, 8) -or
            $extraLength -ne 0 -or
            $commentLength -ne 0) {
            throw 'A release archive uses an unsupported ZIP feature.'
        }
        if ($uncompressedSize -gt $script:maximumItemBytes) {
            throw 'A release archive entry exceeds the privacy scanner limit.'
        }
        $nextCursor = [uint64]$cursor + 46L + $nameLength +
            $extraLength + $commentLength
        if ($nextCursor -gt [uint64]$EndRecordOffset) {
            throw 'A release archive central directory exceeds its boundary.'
        }
        $nameBytes = New-Object byte[] $nameLength
        if ($nameLength -gt 0) {
            [Buffer]::BlockCopy(
                $Bytes,
                $cursor + 46,
                $nameBytes,
                0,
                $nameLength)
            Assert-NoEmbeddedArchivePayload -Bytes $nameBytes
        }
        $localRecords.Add(@{
                CompressedSize = $compressedSize
                Crc = $crc
                Flags = $flags
                LocalOffset = $localOffset
                Method = $method
                NameBytes = $nameBytes
                UncompressedSize = $uncompressedSize
            })
        $declaredExpandedBytes += [int64]$uncompressedSize
        if ($declaredExpandedBytes -gt $script:maximumArchiveBytes -or
            $declaredExpandedBytes -gt (
                $script:maximumArchiveBytes -
                $script:expandedArchiveBytes)) {
            throw 'The expanded release archives exceed the privacy scanner limit.'
        }
        $cursor = [int]$nextCursor
    }
    if ($cursor -ne $EndRecordOffset) {
        throw 'A release archive central directory contains a gap.'
    }

    $ordered = @($localRecords | Sort-Object { [uint64]$_.LocalOffset })
    for ($entryIndex = 0; $entryIndex -lt $ordered.Count; $entryIndex++) {
        $record = $ordered[$entryIndex]
        $localOffset = [uint64]$record.LocalOffset
        $nextBoundary = if ($entryIndex + 1 -lt $ordered.Count) {
            [uint64]$ordered[$entryIndex + 1].LocalOffset
        }
        else {
            [uint64]$DirectoryOffset
        }
        if (($entryIndex -eq 0 -and $localOffset -ne 0) -or
            $localOffset + 30L -gt $nextBoundary -or
            $Bytes[[int]$localOffset] -ne 0x50 -or
            $Bytes[[int]$localOffset + 1] -ne 0x4b -or
            $Bytes[[int]$localOffset + 2] -ne 0x03 -or
            $Bytes[[int]$localOffset + 3] -ne 0x04) {
            throw 'A release archive local record is malformed.'
        }
        $localFlags = [BitConverter]::ToUInt16(
            $Bytes,
            [int]$localOffset + 6)
        $localMethod = [BitConverter]::ToUInt16(
            $Bytes,
            [int]$localOffset + 8)
        $localCrc = [BitConverter]::ToUInt32(
            $Bytes,
            [int]$localOffset + 14)
        $localCompressedSize = [BitConverter]::ToUInt32(
            $Bytes,
            [int]$localOffset + 18)
        $localUncompressedSize = [BitConverter]::ToUInt32(
            $Bytes,
            [int]$localOffset + 22)
        $nameLength = [BitConverter]::ToUInt16(
            $Bytes,
            [int]$localOffset + 26)
        $extraLength = [BitConverter]::ToUInt16(
            $Bytes,
            [int]$localOffset + 28)
        if ($localFlags -ne $record.Flags -or
            $localMethod -ne $record.Method -or
            $localCrc -ne $record.Crc -or
            $localCompressedSize -ne $record.CompressedSize -or
            $localUncompressedSize -ne $record.UncompressedSize -or
            $nameLength -ne $record.NameBytes.Length -or
            $extraLength -ne 0) {
            throw 'A release archive local record disagrees with its directory.'
        }
        for ($nameIndex = 0;
             $nameIndex -lt $nameLength;
             $nameIndex++) {
            if ($Bytes[[int]$localOffset + 30 + $nameIndex] -ne
                $record.NameBytes[$nameIndex]) {
                throw 'A release archive local name disagrees with its directory.'
            }
        }
        $dataOffset = $localOffset + 30L + $nameLength + $extraLength
        $dataEnd = $dataOffset + [uint64]$record.CompressedSize
        if ($dataEnd -ne $nextBoundary) {
            throw 'A release archive contains unreferenced local data.'
        }
        if ($record.Method -eq 0) {
            if ($record.CompressedSize -ne $record.UncompressedSize) {
                throw 'A stored release archive entry has inconsistent sizes.'
            }
            if ([GameAgentReleaseScannerZlib]::ComputeCrc32(
                    $Bytes,
                    [int]$dataOffset,
                    [int]$record.UncompressedSize) -ne $record.Crc) {
                throw 'A stored release archive entry has an invalid checksum.'
            }
        }
        else {
            Assert-ZipDeflateCoverage `
                -Bytes $Bytes `
                -DataOffset ([int64]$dataOffset) `
                -CompressedSize ([int64]$record.CompressedSize) `
                -UncompressedSize ([int64]$record.UncompressedSize) `
                -ExpectedCrc ([uint32]$record.Crc)
        }
    }
}

function Assert-ZipDeflateCoverage {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int64]$DataOffset,

        [Parameter(Mandatory)]
        [int64]$CompressedSize,

        [Parameter(Mandatory)]
        [int64]$UncompressedSize,

        [Parameter(Mandatory)]
        [uint32]$ExpectedCrc
    )

    if ($DataOffset -lt 0 -or
        $CompressedSize -lt 0 -or
        $UncompressedSize -lt 0 -or
        $DataOffset + $CompressedSize -gt $Bytes.LongLength) {
        throw 'A deflated release archive entry exceeds its boundary.'
    }

    Add-Type -AssemblyName System.IO.Compression
    $remainingValidationBytes = (
        $script:maximumArchiveBytes -
        $script:canonicalValidationBytes)
    if ($remainingValidationBytes -lt 1) {
        throw 'ZIP validation exceeded the privacy scanner work limit.'
    }
    $maximumValidationBytes = [Math]::Min(
        $script:maximumItemBytes,
        $remainingValidationBytes)
    $memory = [IO.MemoryStream]::new($Bytes, $false)
    $memory.Position = $DataOffset
    $limited = [GameAgentReleaseScannerSingleByteReadStream]::new($memory)
    $deflate = $null
    $consumedEnd = -1L
    try {
        $deflate = [IO.Compression.DeflateStream]::new(
            $limited,
            [IO.Compression.CompressionMode]::Decompress,
            $true)
        $expanded = Read-BoundedStream `
            -Stream $deflate `
            -MaximumBytes $maximumValidationBytes
        $consumedEnd = $memory.Position
    }
    catch [IO.InvalidDataException] {
        throw 'A deflated release archive entry could not be parsed safely.'
    }
    finally {
        if ($null -ne $deflate) {
            $deflate.Dispose()
        }
        $limited.Dispose()
        $memory.Dispose()
    }

    $script:canonicalValidationBytes += $expanded.LongLength
    if ($consumedEnd -ne $DataOffset + $CompressedSize) {
        throw 'A deflated release archive entry contains unconsumed data.'
    }
    if ($expanded.LongLength -ne $UncompressedSize) {
        throw 'A deflated release archive entry has an inconsistent length.'
    }
    if ([GameAgentReleaseScannerZlib]::ComputeCrc32($expanded) -ne
        $ExpectedCrc) {
        throw 'A deflated release archive entry has an invalid checksum.'
    }
}

function Test-MagicPrefix {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [byte[]]$Magic
    )

    if ($Bytes.Length -lt $Magic.Length) {
        return $false
    }

    for ($index = 0; $index -lt $Magic.Length; $index++) {
        if ($Bytes[$index] -ne $Magic[$index]) {
            return $false
        }
    }

    return $true
}

function Test-TarPayload {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    if ($Bytes.Length -lt 1536 -or ($Bytes.Length % 512) -ne 0) {
        return $false
    }

    try {
        if (-not (Test-ValidTarHeaderAtOffset `
                -Bytes $Bytes `
                -HeaderOffset 0)) {
            return $false
        }

        $size = Get-TarOctal -Bytes $Bytes -Offset 124 -Length 12
        $paddedSize = [int64](([Math]::Ceiling($size / 512.0)) * 512)
        return 512L + $paddedSize -le $Bytes.LongLength
    }
    catch {
        return $false
    }
}

function Test-ValidTarHeaderAtOffset {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$HeaderOffset
    )

    if ($HeaderOffset -lt 0 -or
        ([int64]$HeaderOffset + 512L) -gt $Bytes.LongLength) {
        return $false
    }

    try {
        $storedChecksum = Get-TarOctal `
            -Bytes $Bytes `
            -Offset ($HeaderOffset + 148) `
            -Length 8
        $unsignedChecksum = 0L
        $signedChecksum = 0L
        for ($index = 0; $index -lt 512; $index++) {
            $value = if ($index -ge 148 -and $index -lt 156) {
                0x20
            }
            else {
                $Bytes[$HeaderOffset + $index]
            }
            $unsignedChecksum += $value
            $signedChecksum += if ($value -ge 0x80) {
                [int]$value - 256
            }
            else {
                $value
            }
        }

        return $storedChecksum -eq $unsignedChecksum -or
            $storedChecksum -eq $signedChecksum
    }
    catch {
        return $false
    }
}

function Get-ArtifactPayloadKind {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    if (Test-ZipPayload -Bytes $Bytes) {
        return 'zip'
    }
    if (Test-MagicPrefix -Bytes $Bytes -Magic ([byte[]](0x1f, 0x8b))) {
        return 'gzip'
    }
    $zlib = Read-ZlibMember -Bytes $Bytes -Offset 0
    if ($null -ne $zlib -and $zlib.EndOffset -eq $Bytes.Length) {
        return 'zlib'
    }
    if (Test-MagicPrefix -Bytes $Bytes -Magic (
            [byte[]](0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c))) {
        return '7z'
    }
    if (Test-MagicPrefix -Bytes $Bytes -Magic (
            [byte[]](0x52, 0x61, 0x72, 0x21, 0x1a, 0x07))) {
        return 'rar'
    }
    if (Test-MagicPrefix -Bytes $Bytes -Magic (
            [byte[]](0x4d, 0x53, 0x43, 0x46))) {
        return 'cab'
    }
    if (Test-MagicPrefix -Bytes $Bytes -Magic (
            [byte[]](0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00))) {
        return 'xz'
    }
    if (Test-MagicPrefix -Bytes $Bytes -Magic (
            [byte[]](0x42, 0x5a, 0x68))) {
        return 'bzip2'
    }
    if ((Test-MagicPrefix -Bytes $Bytes -Magic (
                [byte[]](0x28, 0xb5, 0x2f, 0xfd))) -or
        ($Bytes.Length -ge 4 -and
            $Bytes[0] -ge 0x50 -and
            $Bytes[0] -le 0x5f -and
            $Bytes[1] -eq 0x2a -and
            $Bytes[2] -eq 0x4d -and
            $Bytes[3] -eq 0x18)) {
        return 'zstd'
    }
    if ((Test-MagicPrefix -Bytes $Bytes -Magic (
                [byte[]](0x04, 0x22, 0x4d, 0x18))) -or
        (Test-MagicPrefix -Bytes $Bytes -Magic (
                [byte[]](0x02, 0x21, 0x4c, 0x18)))) {
        return 'lz4'
    }
    if (Test-TarPayload -Bytes $Bytes) {
        return 'tar'
    }

    return 'plain'
}

function Get-TarText {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset,

        [Parameter(Mandatory)]
        [int]$Length
    )

    $count = 0
    while ($count -lt $Length -and $Bytes[$Offset + $count] -ne 0) {
        $value = $Bytes[$Offset + $count]
        if ($value -lt 0x20 -or $value -gt 0x7e) {
            throw 'A release tar archive contains an unsupported text field.'
        }
        $count++
    }
    if ($count -lt $Length) {
        for ($index = $count + 1; $index -lt $Length; $index++) {
            if ($Bytes[$Offset + $index] -ne 0) {
                throw 'A release tar text field contains non-zero slack.'
            }
        }
    }

    return [Text.Encoding]::ASCII.GetString($Bytes, $Offset, $count)
}

function Get-TarOctal {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset,

        [Parameter(Mandatory)]
        [int]$Length
    )

    $digits = New-Object Text.StringBuilder
    for ($index = 0; $index -lt $Length; $index++) {
        $value = $Bytes[$Offset + $index]
        if ($value -eq 0 -or $value -eq 0x20) {
            continue
        }
        if ($value -lt 0x30 -or $value -gt 0x37) {
            throw 'A release tar archive contains an unsupported numeric field.'
        }
        $null = $digits.Append([char]$value)
    }

    if ($digits.Length -eq 0) {
        return 0L
    }

    try {
        return [Convert]::ToInt64($digits.ToString(), 8)
    }
    catch {
        throw 'A release tar archive contains an invalid numeric field.'
    }
}

function Test-ZeroBlock {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset
    )

    for ($index = 0; $index -lt 512; $index++) {
        if ($Bytes[$Offset + $index] -ne 0) {
            return $false
        }
    }

    return $true
}

function Assert-TarPayload {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [int]$Depth
    )

    $offset = 0
    $endFound = $false
    while ($offset -le $Bytes.Length - 512) {
        if (Test-ZeroBlock -Bytes $Bytes -Offset $offset) {
            if (($Bytes.Length - $offset) -lt 1024) {
                throw 'A release tar archive has no complete end marker.'
            }
            for ($index = $offset; $index -lt $Bytes.Length; $index++) {
                if ($Bytes[$index] -ne 0) {
                    throw 'A release tar archive contains trailing data.'
                }
            }
            $endFound = $true
            break
        }

        if (-not (Test-ValidTarHeaderAtOffset `
                -Bytes $Bytes `
                -HeaderOffset $offset)) {
            throw 'A release tar archive has an invalid header checksum.'
        }

        $name = Get-TarText `
            -Bytes $Bytes `
            -Offset $offset `
            -Length 100
        $prefix = Get-TarText `
            -Bytes $Bytes `
            -Offset ($offset + 345) `
            -Length 155
        if (-not [string]::IsNullOrEmpty($prefix)) {
            $name = $prefix + '/' + $name
        }
        if ([string]::IsNullOrEmpty($name)) {
            throw 'A release tar archive contains an unnamed entry.'
        }
        Assert-ArtifactName -Name $name
        $metadataFields = @(
            (Get-TarText `
                -Bytes $Bytes `
                -Offset ($offset + 157) `
                -Length 100),
            (Get-TarText `
                -Bytes $Bytes `
                -Offset ($offset + 265) `
                -Length 32),
            (Get-TarText `
                -Bytes $Bytes `
                -Offset ($offset + 297) `
                -Length 32))
        foreach ($metadataField in $metadataFields) {
            if (-not [string]::IsNullOrEmpty([string]$metadataField)) {
                Assert-SafeText `
                    -Value ([string]$metadataField) `
                    -ItemId (Get-ArtifactItemId -Label (
                        $Label + '!/tar-metadata'))
            }
        }

        $size = Get-TarOctal `
            -Bytes $Bytes `
            -Offset ($offset + 124) `
            -Length 12
        if ($size -gt $script:maximumItemBytes) {
            throw 'A release tar entry exceeds the privacy scanner limit.'
        }
        $type = $Bytes[$offset + 156]
        if ($type -ne 0 -and $type -ne 0x30 -and $type -ne 0x35) {
            throw 'A release tar archive contains an unsupported entry type.'
        }
        if ($type -eq 0x35 -and $size -ne 0) {
            throw 'A release tar directory contains an invalid payload.'
        }

        $script:archiveEntryCount++
        if ($script:archiveEntryCount -gt $script:maximumArchiveEntries) {
            throw 'The release archives exceed the entry-count limit.'
        }
        $script:expandedArchiveBytes += $size
        if ($script:expandedArchiveBytes -gt $script:maximumArchiveBytes) {
            throw 'The expanded release archives exceed the privacy scanner limit.'
        }

        $contentOffset = $offset + 512
        $paddedSize = [int64](([Math]::Ceiling($size / 512.0)) * 512)
        $nextOffset = [int64]$contentOffset + $paddedSize
        if ($nextOffset -gt $Bytes.LongLength) {
            throw 'A release tar entry exceeds its containing archive.'
        }
        $scanEnd = [Math]::Min(
            [int64]$Bytes.LongLength,
            $nextOffset + 511L)
        $scanLength = $scanEnd - $offset
        $entryRegionBytes = New-Object byte[] ([int]$scanLength)
        [Buffer]::BlockCopy(
            $Bytes,
            $offset,
            $entryRegionBytes,
            0,
            [int]$scanLength)
        Assert-NoEmbeddedArchivePayload `
            -Bytes $entryRegionBytes `
            -AllowTarHeaderAt257
        for ($paddingOffset = [int64]$contentOffset + $size;
             $paddingOffset -lt $nextOffset;
             $paddingOffset++) {
            if ($Bytes[[int]$paddingOffset] -ne 0) {
                throw 'A release tar entry contains non-zero padding.'
            }
        }
        if ($size -gt 0 -and $type -ne 0x35) {
            $entryBytes = New-Object byte[] ([int]$size)
            [Buffer]::BlockCopy(
                $Bytes,
                $contentOffset,
                $entryBytes,
                0,
                [int]$size)
            Assert-ArtifactPayload `
                -Bytes $entryBytes `
                -Label ($Label + '!/' + $name) `
                -Depth ($Depth + 1)
        }

        $offset = [int]$nextOffset
    }

    if (-not $endFound) {
        throw 'A release tar archive has no valid end marker.'
    }
}

function Assert-ArtifactPayload {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [string]$Label,

        [int]$Depth = 0
    )

    if ($Bytes.Length -eq 0) {
        if (Test-ArchiveName -Name $Label) {
            throw 'A release archive has an empty payload.'
        }
        return
    }
    Assert-ArtifactBytes -Bytes $Bytes -Label $Label
    $kind = Get-ArtifactPayloadKind -Bytes $Bytes
    if ((Test-ArchiveName -Name $Label) -and $kind -eq 'plain') {
        throw 'A release archive has an invalid or unsupported payload.'
    }
    if ($kind -in @(
            '7z',
            'rar',
            'cab',
            'xz',
            'bzip2',
            'zlib',
            'zstd',
            'lz4')) {
        throw 'A release archive uses a recognized but unsupported compression format.'
    }
    if ($kind -eq 'plain') {
        Assert-NoEmbeddedArchivePayload -Bytes $Bytes
        return
    }
    if ($Depth -ge $script:maximumArchiveDepth) {
        throw 'A nested release archive exceeds the privacy scanner depth limit.'
    }
    if ($kind -eq 'gzip') {
        $member = Read-GzipMember -Bytes $Bytes -Offset 0
        if ($null -eq $member) {
            throw 'A release gzip payload could not be parsed safely.'
        }
        if ($member.EndOffset -ne $Bytes.Length) {
            throw 'A release gzip payload contains trailing or concatenated data.'
        }
        $expanded = [byte[]]$member.Expanded

        $script:expandedArchiveBytes += [int64]$expanded.LongLength
        if ($script:expandedArchiveBytes -gt $script:maximumArchiveBytes) {
            throw 'The expanded release archives exceed the privacy scanner limit.'
        }
        Assert-ArtifactPayload `
            -Bytes $expanded `
            -Label ($Label + '!/gzip') `
            -Depth ($Depth + 1)
        return
    }
    if ($kind -eq 'tar') {
        Assert-TarPayload -Bytes $Bytes -Label $Label -Depth $Depth
        return
    }

    Add-Type -AssemblyName System.IO.Compression
    $memory = [IO.MemoryStream]::new($Bytes, $false)
    $archive = $null
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $memory,
            [IO.Compression.ZipArchiveMode]::Read,
            $false)
        $endRecordOffset = Find-ZipEndRecordOffset -Bytes $Bytes
        if ($endRecordOffset -lt 0) {
            throw 'A release archive has no valid end record.'
        }
        $diskNumber = [BitConverter]::ToUInt16(
            $Bytes,
            $endRecordOffset + 4)
        $directoryDisk = [BitConverter]::ToUInt16(
            $Bytes,
            $endRecordOffset + 6)
        $diskEntries = [BitConverter]::ToUInt16(
            $Bytes,
            $endRecordOffset + 8)
        $totalEntries = [BitConverter]::ToUInt16(
            $Bytes,
            $endRecordOffset + 10)
        $directoryBytes = [BitConverter]::ToUInt32(
            $Bytes,
            $endRecordOffset + 12)
        $directoryOffset = [BitConverter]::ToUInt32(
            $Bytes,
            $endRecordOffset + 16)
        if ($diskNumber -ne 0 -or
            $directoryDisk -ne 0 -or
            $diskEntries -ne $totalEntries -or
            $totalEntries -eq [uint16]::MaxValue -or
            $directoryBytes -eq [uint32]::MaxValue -or
            $directoryOffset -eq [uint32]::MaxValue -or
            ([uint64]$directoryOffset + [uint64]$directoryBytes) -gt
                [uint64]$endRecordOffset -or
            $archive.Entries.Count -ne $totalEntries) {
            throw 'A release archive directory is inconsistent.'
        }
        if ([int64]$script:archiveEntryCount + $totalEntries -gt
            $script:maximumArchiveEntries) {
            throw 'The release archives exceed the entry-count limit.'
        }
        Assert-ZipCanonicalCoverage `
            -Bytes $Bytes `
            -EndRecordOffset $endRecordOffset `
            -DirectoryOffset $directoryOffset `
            -DirectoryBytes $directoryBytes `
            -TotalEntries $totalEntries
        foreach ($entry in $archive.Entries) {
            Assert-ArtifactName -Name $entry.FullName
            $script:archiveEntryCount++
            if ($script:archiveEntryCount -gt
                $script:maximumArchiveEntries) {
                throw 'The release archives exceed the entry-count limit.'
            }
            if ($entry.Length -gt $script:maximumItemBytes) {
                throw 'A release archive entry exceeds the privacy scanner limit.'
            }

            $script:expandedArchiveBytes += [int64]$entry.Length
            if ($script:expandedArchiveBytes -gt
                $script:maximumArchiveBytes) {
                throw 'The expanded release archives exceed the privacy scanner limit.'
            }
            $stream = $entry.Open()
            try {
                $entryBytes = Read-BoundedStream `
                    -Stream $stream `
                    -MaximumBytes $script:maximumItemBytes
            }
            finally {
                $stream.Dispose()
            }
            if ($entryBytes.LongLength -ne $entry.Length) {
                throw 'A release archive entry length is inconsistent.'
            }

            Assert-ArtifactPayload `
                -Bytes $entryBytes `
                -Label ($Label + '!/' + $entry.FullName) `
                -Depth ($Depth + 1)
        }
    }
    catch [IO.InvalidDataException] {
        throw 'A release archive could not be parsed safely.'
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        else {
            $memory.Dispose()
        }
    }
}

$files = New-Object 'Collections.Generic.List[object]'
foreach ($inputPath in $Path) {
    $resolved = Resolve-Path -LiteralPath $inputPath
    $item = Get-Item -LiteralPath $resolved
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Release inputs must not be filesystem links.'
    }

    if ($item.PSIsContainer) {
        $root = [IO.Path]::GetFullPath($item.FullName).TrimEnd(
            [char[]]@(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar))
        $prefixLength = $root.Length + 1
        foreach ($child in Get-ChildItem -LiteralPath $root -Recurse -Force) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Release inputs must not contain filesystem links.'
            }

            $fullName = [IO.Path]::GetFullPath($child.FullName)
            $relativeName = $fullName.Substring($prefixLength)
            Assert-ArtifactName -Name $relativeName
            if (-not $child.PSIsContainer) {
                $script:inputFileCount++
                if ($script:inputFileCount -gt
                    $script:maximumInputFiles) {
                    throw 'The release inputs exceed the file-count limit.'
                }
                $files.Add([pscustomobject]@{
                        File = $child
                        Label = $relativeName
                    })
            }
        }
    }
    else {
        Assert-ArtifactName -Name $item.Name
        $script:inputFileCount++
        if ($script:inputFileCount -gt $script:maximumInputFiles) {
            throw 'The release inputs exceed the file-count limit.'
        }
        $files.Add([pscustomobject]@{
                File = $item
                Label = $item.Name
            })
    }
}

foreach ($record in $files) {
    if ($record.File.Length -gt $maximumItemBytes) {
        throw 'A release file exceeds the privacy scanner limit.'
    }
    $inputBytes += [int64]$record.File.Length
    if ($inputBytes -gt $maximumInputBytes) {
        throw 'The release inputs exceed the aggregate size limit.'
    }

    $bytes = [IO.File]::ReadAllBytes($record.File.FullName)
    Assert-ArtifactPayload -Bytes $bytes -Label $record.Label
}

Write-Output 'RELEASE_ARTIFACT_PRIVACY_PASS'

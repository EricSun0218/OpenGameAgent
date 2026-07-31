using System.Text;

namespace GameAgent.Compatibility;

internal static class PngCharacterPayloadReader
{
    private static readonly byte[] Signature =
    {
        137,
        80,
        78,
        71,
        13,
        10,
        26,
        10,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static PngCharacterPayload? Read(
        ReadOnlyMemory<byte> input,
        CompatibilityImportOptions options,
        List<CompatibilityDiagnostic> diagnostics)
    {
        if (input.Length > options.MaxInputBytes)
        {
            AddError(
                diagnostics,
                "input_too_large",
                "The PNG input exceeds the configured byte limit.");
            return null;
        }

        var bytes = input.Span;
        if (bytes.Length < Signature.Length
            || !bytes.Slice(0, Signature.Length).SequenceEqual(Signature))
        {
            AddError(
                diagnostics,
                "invalid_png_signature",
                "The input does not have a valid PNG signature.");
            return null;
        }

        byte[]? encodedVersion2 = null;
        byte[]? encodedVersion3 = null;
        var offset = Signature.Length;
        var chunkCount = 0;
        var sawHeader = false;
        var sawImageData = false;
        var sawEnd = false;
        while (offset < bytes.Length)
        {
            chunkCount++;
            if (chunkCount > options.MaxPngChunks)
            {
                AddError(
                    diagnostics,
                    "png_chunk_limit_exceeded",
                    "The PNG exceeds the configured chunk limit.");
                return null;
            }

            if (bytes.Length - offset < 12)
            {
                AddError(
                    diagnostics,
                    "truncated_png_chunk",
                    "The PNG contains a truncated chunk.");
                return null;
            }

            var dataLengthValue = ReadUInt32BigEndian(bytes, offset);
            if (dataLengthValue > int.MaxValue)
            {
                AddError(
                    diagnostics,
                    "png_chunk_too_large",
                    "A PNG chunk exceeds the supported size.");
                return null;
            }

            var dataLength = (int)dataLengthValue;
            if (dataLength > options.MaxPngChunkBytes)
            {
                AddError(
                    diagnostics,
                    "png_chunk_too_large",
                    "A PNG chunk exceeds the configured size limit.");
                return null;
            }

            var completeLength = 12L + dataLength;
            if (completeLength > bytes.Length - offset)
            {
                AddError(
                    diagnostics,
                    "truncated_png_chunk",
                    "The PNG contains a truncated chunk.");
                return null;
            }

            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, dataLength);
            if (!IsValidChunkType(type))
            {
                AddError(
                    diagnostics,
                    "invalid_png_chunk_type",
                    "The PNG contains an invalid chunk type.");
                return null;
            }

            var expectedCrc = ReadUInt32BigEndian(bytes, offset + 8 + dataLength);
            if (Crc32.Compute(type, data) != expectedCrc)
            {
                AddError(
                    diagnostics,
                    "invalid_png_crc",
                    "The PNG contains a chunk with an invalid checksum.");
                return null;
            }

            if (chunkCount == 1)
            {
                if (!ChunkTypeEquals(type, "IHDR") || dataLength != 13)
                {
                    AddError(
                        diagnostics,
                        "invalid_png_header",
                        "The PNG header chunk is missing or invalid.");
                    return null;
                }

                sawHeader = true;
            }
            else if (ChunkTypeEquals(type, "IHDR"))
            {
                AddError(
                    diagnostics,
                    "duplicate_png_header",
                    "The PNG contains more than one header chunk.");
                return null;
            }

            if (ChunkTypeEquals(type, "tEXt"))
            {
                TryReadTextPayload(
                    data,
                    out var payloadKind,
                    out var encodedPayload);

                if (payloadKind == PngPayloadKind.Version3)
                {
                    if (encodedVersion3 is not null)
                    {
                        AddError(
                            diagnostics,
                            "duplicate_character_payload",
                            "The PNG contains an ambiguous duplicate character payload.");
                        return null;
                    }

                    encodedVersion3 = encodedPayload!;
                }
                else if (payloadKind == PngPayloadKind.Version2)
                {
                    if (encodedVersion2 is not null)
                    {
                        AddError(
                            diagnostics,
                            "duplicate_character_payload",
                            "The PNG contains an ambiguous duplicate character payload.");
                        return null;
                    }

                    encodedVersion2 = encodedPayload!;
                }
            }
            else if (ChunkTypeEquals(type, "IDAT") && dataLength > 0)
            {
                sawImageData = true;
            }

            offset += (int)completeLength;
            if (ChunkTypeEquals(type, "IEND"))
            {
                if (dataLength != 0 || offset != bytes.Length)
                {
                    AddError(
                        diagnostics,
                        "invalid_png_end",
                        "The PNG end chunk is invalid.");
                    return null;
                }

                sawEnd = true;
                break;
            }
        }

        if (!sawHeader || !sawImageData || !sawEnd)
        {
            AddError(
                diagnostics,
                "incomplete_png",
                "The PNG is missing a required structural chunk.");
            return null;
        }

        if (encodedVersion3 is not null)
        {
            if (encodedVersion2 is not null)
            {
                diagnostics.Add(
                    new CompatibilityDiagnostic(
                        "secondary_payload_ignored",
                        CompatibilityDiagnosticSeverity.Info,
                        "$",
                        "The newer character payload was selected over a compatibility payload."));
            }

            return TryDecodeBase64Json(
                encodedVersion3,
                options,
                diagnostics,
                out var version3)
                ? new PngCharacterPayload(version3!, isVersion3: true)
                : null;
        }

        if (encodedVersion2 is not null)
        {
            return TryDecodeBase64Json(
                encodedVersion2,
                options,
                diagnostics,
                out var version2)
                ? new PngCharacterPayload(version2!, isVersion3: false)
                : null;
        }

        AddError(
            diagnostics,
            "character_payload_missing",
            "The PNG does not contain a supported character payload.");
        return null;
    }

    private static void TryReadTextPayload(
        ReadOnlySpan<byte> data,
        out PngPayloadKind payloadKind,
        out byte[]? encodedPayload)
    {
        payloadKind = PngPayloadKind.None;
        encodedPayload = null;
        var separator = data.IndexOf((byte)0);
        if (separator <= 0 || separator > 79)
        {
            return;
        }

        var keyword = data.Slice(0, separator);
        if (keyword.SequenceEqual("ccv3"u8))
        {
            payloadKind = PngPayloadKind.Version3;
        }
        else if (keyword.SequenceEqual("chara"u8)
                 || keyword.SequenceEqual("Chara"u8))
        {
            payloadKind = PngPayloadKind.Version2;
        }
        else
        {
            return;
        }

        encodedPayload = data.Slice(separator + 1).ToArray();
    }

    private static bool TryDecodeBase64Json(
        ReadOnlySpan<byte> encoded,
        CompatibilityImportOptions options,
        List<CompatibilityDiagnostic> diagnostics,
        out byte[]? payload)
    {
        payload = null;
        if (encoded.Length == 0
            || encoded.Length % 4 != 0
            || encoded.Length > ((long)options.MaxDecodedPayloadBytes + 2L) / 3L * 4L)
        {
            AddError(
                diagnostics,
                "invalid_character_payload",
                "The encoded character payload is invalid or exceeds its configured limit.");
            return false;
        }

        var characters = new char[encoded.Length];
        for (var index = 0; index < encoded.Length; index++)
        {
            var value = encoded[index];
            if (value > 127 || !IsBase64Character((char)value, index, encoded.Length))
            {
                AddError(
                    diagnostics,
                    "invalid_character_payload",
                    "The encoded character payload is not valid base64.");
                return false;
            }

            characters[index] = (char)value;
        }

        try
        {
            payload = Convert.FromBase64CharArray(characters, 0, characters.Length);
        }
        catch (FormatException)
        {
            AddError(
                diagnostics,
                "invalid_character_payload",
                "The encoded character payload is not valid base64.");
            return false;
        }

        if (payload.Length > options.MaxDecodedPayloadBytes)
        {
            AddError(
                diagnostics,
                "decoded_payload_too_large",
                "The decoded character payload exceeds the configured limit.");
            payload = null;
            return false;
        }

        try
        {
            _ = StrictUtf8.GetCharCount(payload);
        }
        catch (DecoderFallbackException)
        {
            AddError(
                diagnostics,
                "invalid_character_payload_utf8",
                "The decoded character payload is not valid UTF-8.");
            payload = null;
            return false;
        }

        return true;
    }

    private static bool IsBase64Character(char value, int index, int length)
    {
        if ((value >= 'A' && value <= 'Z')
            || (value >= 'a' && value <= 'z')
            || (value >= '0' && value <= '9')
            || value is '+' or '/')
        {
            return true;
        }

        return value == '=' && index >= length - 2;
    }

    private static bool ChunkTypeEquals(ReadOnlySpan<byte> type, string expected)
    {
        return type.Length == 4
               && type[0] == expected[0]
               && type[1] == expected[1]
               && type[2] == expected[2]
               && type[3] == expected[3];
    }

    private static bool IsValidChunkType(ReadOnlySpan<byte> type)
    {
        if (type.Length != 4)
        {
            return false;
        }

        foreach (var value in type)
        {
            if (!((value >= (byte)'A' && value <= (byte)'Z')
                  || (value >= (byte)'a' && value <= (byte)'z')))
            {
                return false;
            }
        }

        return true;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value, int offset)
    {
        return ((uint)value[offset] << 24)
               | ((uint)value[offset + 1] << 16)
               | ((uint)value[offset + 2] << 8)
               | value[offset + 3];
    }

    private static void AddError(
        List<CompatibilityDiagnostic> diagnostics,
        string code,
        string message)
    {
        diagnostics.Add(
            new CompatibilityDiagnostic(
                code,
                CompatibilityDiagnosticSeverity.Error,
                "$",
                message));
    }

    private enum PngPayloadKind
    {
        None,
        Version2,
        Version3,
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        internal static uint Compute(
            ReadOnlySpan<byte> first,
            ReadOnlySpan<byte> second)
        {
            var crc = uint.MaxValue;
            foreach (var value in first)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            foreach (var value in second)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            return crc ^ uint.MaxValue;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < (uint)table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xedb88320U ^ (value >> 1)
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}

internal sealed class PngCharacterPayload
{
    internal PngCharacterPayload(byte[] json, bool isVersion3)
    {
        Json = json;
        IsVersion3 = isVersion3;
    }

    internal ReadOnlyMemory<byte> Json { get; }

    internal bool IsVersion3 { get; }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.World;

internal static class WorldLargeCanonicalJsonDigest
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    public static string Compute(
        JsonElement value,
        long maximumUtf8Bytes,
        string parameterName)
    {
        using var writer = new HashWriter(
            maximumUtf8Bytes,
            parameterName);
        WriteCanonical(writer, value);
        return writer.Complete();
    }

    private static void WriteCanonical(
        HashWriter writer,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.AppendAscii("{");
                var firstProperty = true;
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(
                                 item => item.Name,
                                 StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        writer.AppendAscii(",");
                    }

                    firstProperty = false;
                    writer.AppendJsonString(property.Name);
                    writer.AppendAscii(":");
                    WriteCanonical(writer, property.Value);
                }

                writer.AppendAscii("}");
                break;
            case JsonValueKind.Array:
                writer.AppendAscii("[");
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        writer.AppendAscii(",");
                    }

                    firstItem = false;
                    WriteCanonical(writer, item);
                }

                writer.AppendAscii("]");
                break;
            case JsonValueKind.String:
                writer.AppendJsonString(value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                writer.AppendUtf8(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.AppendAscii("true");
                break;
            case JsonValueKind.False:
                writer.AppendAscii("false");
                break;
            case JsonValueKind.Null:
                writer.AppendAscii("null");
                break;
            default:
                throw new ArgumentException(
                    "Undefined JSON cannot be canonicalized.",
                    nameof(value));
        }
    }

    private sealed class HashWriter : IDisposable
    {
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        private readonly byte[] _utf8Buffer = new byte[4_096];
        private readonly long _maximumUtf8Bytes;
        private readonly string _parameterName;
        private long _written;
        private bool _completed;

        public HashWriter(
            long maximumUtf8Bytes,
            string parameterName)
        {
            if (maximumUtf8Bytes < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumUtf8Bytes));
            }

            _maximumUtf8Bytes = maximumUtf8Bytes;
            _parameterName = parameterName;
        }

        public void AppendAscii(string value)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (value.Length > bytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character > 0x7f)
                {
                    throw new ArgumentException(
                        "The literal must be ASCII.",
                        nameof(value));
                }

                bytes[index] = (byte)character;
            }

            Append(bytes[..value.Length]);
        }

        public void AppendJsonString(string value)
        {
            AppendAscii("\"");
            var runStart = 0;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                string? escape = character switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => null
                };
                if (escape is null && character >= 0x20)
                {
                    continue;
                }

                AppendUtf8(value, runStart, index - runStart);
                if (escape is not null)
                {
                    AppendAscii(escape);
                }
                else
                {
                    AppendAscii(
                        "\\u"
                        + ((int)character).ToString(
                            "x4",
                            CultureInfo.InvariantCulture));
                }

                runStart = index + 1;
            }

            AppendUtf8(value, runStart, value.Length - runStart);
            AppendAscii("\"");
        }

        public void AppendUtf8(string value)
        {
            AppendUtf8(value, 0, value.Length);
        }

        public string Complete()
        {
            if (_completed)
            {
                throw new InvalidOperationException(
                    "The digest has already been completed.");
            }

            _completed = true;
            var digest = _hash.GetHashAndReset();
            var result = new StringBuilder(digest.Length * 2);
            foreach (var value in digest)
            {
                result.Append(
                    value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return result.ToString();
        }

        public void Dispose()
        {
            _hash.Dispose();
        }

        private void AppendUtf8(
            string value,
            int offset,
            int count)
        {
            while (count > 0)
            {
                var take = Math.Min(count, 1_024);
                if (take < count
                    && char.IsHighSurrogate(value[offset + take - 1]))
                {
                    take--;
                }

                var written = StrictUtf8.GetBytes(
                    value.AsSpan(offset, take),
                    _utf8Buffer);
                Append(_utf8Buffer.AsSpan(0, written));
                offset += take;
                count -= take;
            }
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            if (_completed)
            {
                throw new ObjectDisposedException(nameof(HashWriter));
            }

            try
            {
                _written = checked(_written + bytes.Length);
            }
            catch (OverflowException)
            {
                ThrowLimit();
            }

            if (_written > _maximumUtf8Bytes)
            {
                ThrowLimit();
            }

            _hash.AppendData(bytes);
        }

        private void ThrowLimit()
        {
            throw new ArgumentException(
                "Canonical JSON exceeds its configured UTF-8 byte limit.",
                _parameterName);
        }
    }
}

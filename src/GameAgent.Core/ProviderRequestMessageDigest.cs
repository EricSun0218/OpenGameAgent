using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

internal static class ProviderRequestMessageDigest
{
    public static string Compute(
        IReadOnlyList<NormalizedMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        using var digest = new IncrementalDigest(cancellationToken);
        digest.AppendString("provider-request-messages-v2");
        digest.AppendInt64(messages.Count);
        for (var messageIndex = 0;
             messageIndex < messages.Count;
             messageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = messages[messageIndex]
                          ?? throw new ArgumentException(
                              "Provider message lists cannot contain null entries.",
                              nameof(messages));
            digest.AppendByte(0x10);
            digest.AppendString(message.MessageId);
            digest.AppendString(message.Role);
            digest.AppendInt64(message.CreatedAt.Ticks);
            digest.AppendInt64(message.CreatedAt.Offset.Ticks);
            digest.AppendInt64(message.Parts.Count);
            for (var partIndex = 0;
                 partIndex < message.Parts.Count;
                 partIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = message.Parts[partIndex]
                           ?? throw new ArgumentException(
                               "Provider message parts cannot contain null entries.",
                               nameof(messages));
                digest.AppendByte(0x20);
                digest.AppendString(part.Type);
                digest.AppendNullableString(part.Text);
                digest.AppendNullableString(part.ToolCallId);
                digest.AppendNullableString(part.ToolName);
                digest.AppendNullableString(part.ToolVersion);
                digest.AppendNullableString(part.ToolEffect);
                digest.AppendNullableString(part.ToolDescriptorDigest);
                if (part.Json.HasValue
                    && part.Json.Value.ValueKind
                    != JsonValueKind.Undefined)
                {
                    digest.AppendByte(1);
                    ProviderRequestContentGuard.EnsureJsonDigestSafe(
                        part.Json.Value,
                        cancellationToken);
                    AppendJson(
                        digest,
                        part.Json.Value,
                        cancellationToken);
                }
                else
                {
                    digest.AppendByte(0);
                }
            }
        }

        return digest.Finish();
    }

    private static void AppendJson(
        IncrementalDigest digest,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                digest.AppendByte(0x30);
                var properties = new List<JsonPropertyEntry>();
                foreach (var property in value.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    properties.Add(
                        new JsonPropertyEntry(
                            property.Name,
                            property.Value));
                }

                properties.Sort(JsonPropertyEntryComparer.Instance);
                foreach (var property in properties)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    digest.AppendByte(0x31);
                    digest.AppendString(property.Name);
                    AppendJson(
                        digest,
                        property.Value,
                        cancellationToken);
                }

                digest.AppendByte(0x32);
                break;
            case JsonValueKind.Array:
                digest.AppendByte(0x40);
                foreach (var item in value.EnumerateArray())
                {
                    AppendJson(digest, item, cancellationToken);
                }

                digest.AppendByte(0x41);
                break;
            case JsonValueKind.String:
                digest.AppendByte(0x50);
                digest.AppendString(value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                digest.AppendByte(0x60);
                digest.AppendJsonNumber(value);
                break;
            case JsonValueKind.True:
                digest.AppendByte(0x70);
                break;
            case JsonValueKind.False:
                digest.AppendByte(0x71);
                break;
            case JsonValueKind.Null:
                digest.AppendByte(0x72);
                break;
            default:
                throw new InvalidDataException(
                    "Provider message JSON cannot be undefined.");
        }
    }

    private readonly struct JsonPropertyEntry
    {
        public JsonPropertyEntry(string name, JsonElement value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        public JsonElement Value { get; }
    }

    private sealed class JsonPropertyEntryComparer :
        IComparer<JsonPropertyEntry>
    {
        public static JsonPropertyEntryComparer Instance { get; } = new();

        public int Compare(JsonPropertyEntry left, JsonPropertyEntry right)
        {
            return string.CompareOrdinal(left.Name, right.Name);
        }
    }

    private sealed class IncrementalDigest : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);

        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _buffer = new byte[4_096];
        private readonly Encoder _encoder = StrictUtf8.GetEncoder();
        private readonly CancellationToken _cancellationToken;
        private bool _finished;

        public IncrementalDigest(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public void AppendByte(byte value)
        {
            _buffer[0] = value;
            _hash.AppendData(_buffer, 0, 1);
        }

        public void AppendInt64(long value)
        {
            _buffer[0] = (byte)(value >> 56);
            _buffer[1] = (byte)(value >> 48);
            _buffer[2] = (byte)(value >> 40);
            _buffer[3] = (byte)(value >> 32);
            _buffer[4] = (byte)(value >> 24);
            _buffer[5] = (byte)(value >> 16);
            _buffer[6] = (byte)(value >> 8);
            _buffer[7] = (byte)value;
            _hash.AppendData(_buffer, 0, 8);
        }

        public void AppendNullableString(string? value)
        {
            if (value is null)
            {
                AppendByte(0);
                return;
            }

            AppendByte(1);
            AppendString(value);
        }

        public void AppendString(string value)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            value ??= string.Empty;
            AppendInt64(StrictUtf8.GetByteCount(value));
            if (value.Length == 0)
            {
                return;
            }

            _encoder.Reset();
            var source = value.AsSpan();
            while (!source.IsEmpty)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _encoder.Convert(
                    source,
                    _buffer,
                    flush: true,
                    out var charsUsed,
                    out var bytesUsed,
                    out _);
                if (charsUsed == 0 && bytesUsed == 0)
                {
                    throw new InvalidDataException(
                        "Provider message text could not be encoded.");
                }

                _hash.AppendData(_buffer, 0, bytesUsed);
                source = source[charsUsed..];
            }
        }

        public void AppendJsonNumber(JsonElement value)
        {
            using var numberHash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var output = new HashingBufferWriter(
                numberHash,
                _cancellationToken);
            using (var writer = new Utf8JsonWriter(output))
            {
                value.WriteTo(writer);
            }

            var result = numberHash.GetHashAndReset();
            AppendInt64(result.Length);
            _hash.AppendData(result);
            Array.Clear(result, 0, result.Length);
        }

        public string Finish()
        {
            if (_finished)
            {
                throw new InvalidOperationException(
                    "The provider request digest is already complete.");
            }

            _finished = true;
            var bytes = _hash.GetHashAndReset();
            var characters = new char[bytes.Length * 2];
            const string alphabet = "0123456789abcdef";
            for (var index = 0; index < bytes.Length; index++)
            {
                characters[index * 2] = alphabet[bytes[index] >> 4];
                characters[index * 2 + 1] =
                    alphabet[bytes[index] & 0x0f];
            }

            Array.Clear(bytes, 0, bytes.Length);
            return new string(characters);
        }

        public void Dispose()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _hash.Dispose();
        }
    }

    private sealed class HashingBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private const int MaximumTokenBytes =
            ProviderRequestContentGuard.MaxJsonScalarUtf8Bytes;
        private const int MaximumBufferBytes = MaximumTokenBytes + 4_096;

        private readonly IncrementalHash _hash;
        private readonly CancellationToken _cancellationToken;
        private byte[]? _buffer;
        private int _written;

        public HashingBufferWriter(
            IncrementalHash hash,
            CancellationToken cancellationToken)
        {
            _hash = hash ?? throw new ArgumentNullException(nameof(hash));
            _cancellationToken = cancellationToken;
        }

        public void Advance(int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_buffer is null
                || count < 0
                || count > _buffer.Length
                || count > MaximumTokenBytes - _written)
            {
                throw new InvalidOperationException(
                    "The JSON digest writer advanced past its buffer.");
            }

            _hash.AppendData(_buffer, 0, count);
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer!;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer!;
        }

        public void Dispose()
        {
            if (_buffer is null)
            {
                return;
            }

            Array.Clear(_buffer, 0, _buffer.Length);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }

        private void EnsureBuffer(int sizeHint)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            var required = Math.Max(sizeHint, 256);
            if (required > MaximumBufferBytes)
            {
                throw new InvalidDataException(
                    "Provider message JSON exceeds the digest limit.");
            }

            if (_buffer is not null && _buffer.Length >= required)
            {
                return;
            }

            var replacement = ArrayPool<byte>.Shared.Rent(required);
            var previous = _buffer;
            _buffer = replacement;
            if (previous is not null)
            {
                Array.Clear(previous, 0, previous.Length);
                ArrayPool<byte>.Shared.Return(previous);
            }
        }
    }
}

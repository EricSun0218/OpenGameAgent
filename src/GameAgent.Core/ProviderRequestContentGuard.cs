using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using GameAgent.Protocol;

namespace GameAgent.Core;

internal static class ProviderRequestContentGuard
{
    internal const int MaxMessages = 4_096;
    internal const int MaxParts = 65_536;
    internal const int MaxTools = 4_096;
    internal const int MaxUtf8Bytes = 8 * 1_048_576;
    internal const int MaxJsonNodes = 131_072;
    internal const int MaxJsonScalarUtf8Bytes =
        ProtocolLimits.MaxProtocolJsonStringUtf8Bytes;
    private const int JsonWriterSlackBytes = 4_096;
    private const int MaxJsonTokenBufferBytes =
        (MaxJsonScalarUtf8Bytes * 6) + JsonWriterSlackBytes;
    private static readonly JsonValueLimits ProviderJsonLimits = new(
        maxUtf8Bytes: MaxUtf8Bytes,
        maxDepth: 64,
        maxNodes: MaxJsonNodes,
        maxStringUtf8Bytes: MaxJsonScalarUtf8Bytes,
        maxContainerItems: MaxJsonNodes);

    public static void EnsureInputWithinLimits(
        IReadOnlyList<NormalizedMessage>? messages,
        IReadOnlyList<ToolDescriptor>? tools,
        CancellationToken cancellationToken)
    {
        EnsureWithinLimits(
            messages,
            tools,
            cancellationToken,
            static () => new ProviderException(
                "provider_request_input_limit",
                "validation",
                "The provider request exceeds the runtime input limit.",
                false,
                usageKnownToBeZero: true));
    }

    public static void EnsurePreparedWithinLimits(
        IReadOnlyList<NormalizedMessage>? messages,
        IReadOnlyList<ToolDescriptor>? tools,
        string providerId,
        CancellationToken cancellationToken)
    {
        EnsureWithinLimits(
            messages,
            tools,
            cancellationToken,
            () => PreparedLimitExceeded(providerId));
    }

    public static ProviderException PreparedLimitExceeded(
        string providerId)
    {
        return new ProviderException(
            "provider_request_adapter_output_limit",
            "provider",
            $"Provider '{providerId}' returned an oversized prepared request.",
            false,
            usageKnownToBeZero: true);
    }

    internal static void EnsureJsonDigestSafe(
        System.Text.Json.JsonElement value,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureJsonCanBeWrittenWithBoundedTokens(
                value,
                cancellationToken);
            EnsureJsonNumbersWithinLimit(value, cancellationToken);
        }
        catch (Exception exception)
            when (IsJsonLimitException(exception))
        {
            throw new InvalidDataException(
                "Provider message JSON exceeds the digest limit.",
                exception);
        }
    }

    private static void EnsureWithinLimits(
        IReadOnlyList<NormalizedMessage>? messages,
        IReadOnlyList<ToolDescriptor>? tools,
        CancellationToken cancellationToken,
        Func<ProviderException> limitExceeded)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (messages is null || tools is null)
        {
            throw limitExceeded();
        }

        var messageCount = messages.Count;
        var toolCount = tools.Count;
        if (messageCount > MaxMessages || toolCount > MaxTools)
        {
            throw limitExceeded();
        }

        var parts = 0;
        var utf8Bytes = 0L;
        var jsonNodes = 0L;

        for (var messageIndex = 0;
             messageIndex < messageCount;
             messageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = messages[messageIndex];
            if (message is null || message.Parts is null)
            {
                throw limitExceeded();
            }

            AddString(message.MessageId);
            AddString(message.Role);
            if (message.Parts.Count > MaxParts - parts)
            {
                throw limitExceeded();
            }

            parts += message.Parts.Count;
            foreach (var part in message.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (part is null)
                {
                    throw limitExceeded();
                }

                AddString(part.Type);
                AddString(part.Text);
                AddString(part.ToolCallId);
                AddString(part.ToolName);
                AddString(part.ToolVersion);
                AddString(part.ToolEffect);
                AddString(part.ToolDescriptorDigest);
                if (part.Json.HasValue)
                {
                    AddJson(part.Json.Value);
                }

                EnsureBudget();
            }
        }

        for (var toolIndex = 0; toolIndex < toolCount; toolIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tool = tools[toolIndex];
            if (tool is null
                || tool.ConflictScopes is null
                || tool.Extensions is null
                || tool.ConflictScopes.Count
                > ProtocolLimits.MaxToolConflictScopes
                || tool.Extensions.Count > 4_096)
            {
                throw limitExceeded();
            }

            AddString(tool.ProtocolVersion);
            AddString(tool.SchemaVersion);
            AddString(tool.Name);
            AddString(tool.Version);
            AddString(tool.Description);
            AddString(tool.Effect);
            AddString(tool.ThreadAffinity);
            AddString(tool.RetryPolicy);
            AddString(tool.IdempotencyPolicy);
            AddString(tool.Toolset);
            AddString(tool.Visibility);
            foreach (var scope in tool.ConflictScopes)
            {
                AddString(scope);
            }

            AddJson(tool.ParametersSchema);
            if (tool.ResultSchema.HasValue)
            {
                AddJson(tool.ResultSchema.Value);
            }

            foreach (var extension in tool.Extensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddString(extension.Key);
                AddJson(extension.Value);
            }

            EnsureBudget();
        }

        return;

        void AddString(string? value)
        {
            var remaining = MaxUtf8Bytes - utf8Bytes;
            var source = (value ?? string.Empty).AsSpan();
            Span<char> encoded = stackalloc char[512];
            var encodedBytes = 2L;
            while (!source.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = JavaScriptEncoder.Default.Encode(
                    source,
                    encoded,
                    out var consumed,
                    out var written,
                    isFinalBlock: true);
                if (status is not OperationStatus.Done
                    and not OperationStatus.DestinationTooSmall
                    || consumed == 0 && written == 0)
                {
                    throw limitExceeded();
                }

                encodedBytes = checked(
                    encodedBytes
                    + Encoding.UTF8.GetByteCount(encoded[..written]));
                if (encodedBytes > remaining)
                {
                    throw limitExceeded();
                }

                source = source[consumed..];
            }

            utf8Bytes = checked(utf8Bytes + encodedBytes);
            EnsureBudget();
        }

        void AddJson(System.Text.Json.JsonElement value)
        {
            try
            {
                var measurement = ValidateAndMeasureJson(
                    value,
                    cancellationToken);
                utf8Bytes = checked(utf8Bytes + measurement.Utf8Bytes);
                jsonNodes = checked(jsonNodes + measurement.Nodes);
            }
            catch (Exception exception)
                when (IsJsonLimitException(exception))
            {
                throw limitExceeded();
            }

            EnsureBudget();
        }

        void EnsureBudget()
        {
            if (utf8Bytes > MaxUtf8Bytes || jsonNodes > MaxJsonNodes)
            {
                throw limitExceeded();
            }
        }
    }

    private static JsonValueMeasurement ValidateAndMeasureJson(
        System.Text.Json.JsonElement value,
        CancellationToken cancellationToken)
    {
        EnsureJsonCanBeWrittenWithBoundedTokens(
            value,
            cancellationToken);
        EnsureJsonNumbersWithinLimit(value, cancellationToken);
        var measurement = JsonValueInspector.ValidateAndMeasureDetailed(
            value,
            ProviderJsonLimits,
            "providerJson");
        cancellationToken.ThrowIfCancellationRequested();
        return measurement;
    }

    private static void EnsureJsonNumbersWithinLimit(
        System.Text.Json.JsonElement value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (value.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    EnsureJsonNumbersWithinLimit(
                        property.Value,
                        cancellationToken);
                }

                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    EnsureJsonNumbersWithinLimit(
                        item,
                        cancellationToken);
                }

                break;
            case System.Text.Json.JsonValueKind.Number:
                if (value.GetRawText().Length > MaxJsonScalarUtf8Bytes)
                {
                    throw new ProviderJsonTokenLimitException();
                }

                break;
        }
    }

    private static void EnsureJsonCanBeWrittenWithBoundedTokens(
        System.Text.Json.JsonElement value,
        CancellationToken cancellationToken)
    {
        using var buffer = new BoundedJsonTokenBufferWriter(
            cancellationToken);
        using var writer = new System.Text.Json.Utf8JsonWriter(
            buffer,
            new System.Text.Json.JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        value.WriteTo(writer);
        writer.Flush();
    }

    private static bool IsJsonLimitException(Exception exception)
    {
        return exception is RuntimeContentLimitException
            or ProviderJsonTokenLimitException
            or System.Text.Json.JsonException
            or InvalidOperationException;
    }

    private sealed class BoundedJsonTokenBufferWriter :
        IBufferWriter<byte>,
        IDisposable
    {
        private readonly CancellationToken _cancellationToken;
        private byte[]? _buffer;
        private int _written;

        public BoundedJsonTokenBufferWriter(
            CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public void Advance(int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_buffer is null
                || count < 0
                || count > _buffer.Length
                || count > MaxUtf8Bytes - _written)
            {
                throw new ProviderJsonTokenLimitException();
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var available = EnsureBuffer(sizeHint);
            return _buffer!.AsMemory(0, available);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            var available = EnsureBuffer(sizeHint);
            return _buffer!.AsSpan(0, available);
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    buffer,
                    clearArray: true);
            }
        }

        private int EnsureBuffer(int sizeHint)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            var remaining = MaxUtf8Bytes - _written;
            var available = (int)Math.Min(
                MaxJsonTokenBufferBytes,
                (long)remaining + JsonWriterSlackBytes);
            var required = sizeHint == 0 ? 256 : sizeHint;
            if (required > available)
            {
                throw new ProviderJsonTokenLimitException();
            }

            if (_buffer is not null
                && _buffer.Length >= required)
            {
                return Math.Min(_buffer.Length, available);
            }

            var replacement = ArrayPool<byte>.Shared.Rent(required);
            var previous = _buffer;
            _buffer = replacement;
            if (previous is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    previous,
                    clearArray: true);
            }

            return Math.Min(replacement.Length, available);
        }
    }

    private sealed class ProviderJsonTokenLimitException : Exception
    {
    }
}

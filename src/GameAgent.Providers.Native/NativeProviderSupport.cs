using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Providers.Native;

internal static class NativeProviderLimits
{
    internal const int MaxRequestBytes =
        ProviderWireRequestEvidence.MaximumPayloadBytes;
    internal const int MaxMessages = 4_096;
    internal const int MaxParts = 16_384;
    internal const int MaxTools = 128;
    internal const int MaxToolSchemaBytes = 1_048_576;
    internal const int MaxSseLineCharacters = 1_048_576;
    internal const int MaxSseEventCharacters = 4_194_304;
    internal const int MaxSseTotalCharacters = 268_435_456;
    internal const int MaxSseEvents = 1_000_000;

    internal static void ValidateRequest(
        StreamingModelRequest request,
        int configuredMaxTools,
        int configuredMaxOutputTokens,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RunId)
            || string.IsNullOrWhiteSpace(request.StreamAttemptId)
            || request.Messages is null
            || request.Tools is null
            || request.Messages.Count is < 1 or > MaxMessages
            || request.Tools.Count > Math.Min(MaxTools, configuredMaxTools)
            || request.MaxOutputTokens is < 1
            || request.MaxOutputTokens > configuredMaxOutputTokens
            || request.OpaqueContinuationState is not null)
        {
            throw InvalidRequest();
        }

        var approximateBytes = 0L;
        var parts = 0;
        foreach (var message in request.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message is null
                || message.Parts is null
                || !IsRole(message.Role))
            {
                throw InvalidRequest();
            }

            parts += message.Parts.Count;
            if (parts > MaxParts)
            {
                throw InvalidRequest();
            }

            approximateBytes += Utf8(message.MessageId) + Utf8(message.Role);
            foreach (var part in message.Parts)
            {
                if (part is null
                    || !IsPart(part.Type)
                    || !IsValidPart(part))
                {
                    throw InvalidRequest();
                }

                approximateBytes += Utf8(part.Type)
                                    + Utf8(part.Text)
                                    + Utf8(part.ToolCallId)
                                    + Utf8(part.ToolName);
                if (part.Json.HasValue)
                {
                    approximateBytes += Encoding.UTF8.GetByteCount(
                        part.Json.Value.GetRawText());
                }

                if (approximateBytes > MaxRequestBytes)
                {
                    throw RequestTooLarge();
                }
            }
        }

        foreach (var tool in request.Tools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tool is null
                || string.IsNullOrWhiteSpace(tool.Name)
                || string.IsNullOrWhiteSpace(tool.Description)
                || tool.ParametersSchema.ValueKind
                    is not JsonValueKind.Object)
            {
                throw InvalidRequest();
            }

            var schemaBytes = Encoding.UTF8.GetByteCount(
                tool.ParametersSchema.GetRawText());
            if (schemaBytes > MaxToolSchemaBytes)
            {
                throw new ProviderException(
                    "provider_tool_schema_limit",
                    "validation",
                    "A tool schema exceeds the provider request limit.",
                    false,
                    usageKnownToBeZero: true);
            }

            approximateBytes += Utf8(tool.Name)
                                + Utf8(tool.Description)
                                + schemaBytes;
            if (approximateBytes > MaxRequestBytes)
            {
                throw RequestTooLarge();
            }
        }

        _ = request.Inference?.CloneValidated();
    }

    internal static void ValidateSseLimits(
        int maxLineCharacters,
        int maxEventCharacters,
        int maxTotalCharacters,
        int maxEvents)
    {
        if (maxLineCharacters is < 1_024 or > MaxSseLineCharacters
            || maxEventCharacters < maxLineCharacters
            || maxEventCharacters > MaxSseEventCharacters
            || maxTotalCharacters < maxEventCharacters
            || maxTotalCharacters > MaxSseTotalCharacters
            || maxEvents is < 1 or > MaxSseEvents)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLineCharacters),
                "The SSE parser limits are invalid.");
        }
    }

    internal static bool ContainsReasoning(StreamingModelRequest request) =>
        request.Messages.Any(message => message.Parts.Any(part =>
            string.Equals(
                part.Type,
                NormalizedPartTypes.Reasoning,
                StringComparison.Ordinal)));

    internal static string Required(
        string? value,
        int maxUtf8Bytes,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        var result = value.Trim();
        if (Encoding.UTF8.GetByteCount(result) > maxUtf8Bytes
            || result.Any(char.IsControl))
        {
            throw new ArgumentException("The value is invalid.", parameterName);
        }

        return result;
    }

    private static int Utf8(string? value) =>
        value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private static bool IsRole(string value) =>
        string.Equals(value, NormalizedRoles.System, StringComparison.Ordinal)
        || string.Equals(value, NormalizedRoles.User, StringComparison.Ordinal)
        || string.Equals(value, NormalizedRoles.Assistant, StringComparison.Ordinal)
        || string.Equals(value, NormalizedRoles.Tool, StringComparison.Ordinal);

    private static bool IsPart(string value) =>
        string.Equals(value, NormalizedPartTypes.Text, StringComparison.Ordinal)
        || string.Equals(value, NormalizedPartTypes.Json, StringComparison.Ordinal)
        || string.Equals(value, NormalizedPartTypes.Reasoning, StringComparison.Ordinal)
        || string.Equals(value, NormalizedPartTypes.ToolCall, StringComparison.Ordinal)
        || string.Equals(value, NormalizedPartTypes.ToolResult, StringComparison.Ordinal);

    private static bool IsValidPart(NormalizedContentPart part)
    {
        if (string.Equals(part.Type, NormalizedPartTypes.Text, StringComparison.Ordinal)
            || string.Equals(part.Type, NormalizedPartTypes.Reasoning, StringComparison.Ordinal))
        {
            return part.Text is not null;
        }

        if (string.Equals(part.Type, NormalizedPartTypes.Json, StringComparison.Ordinal))
        {
            return part.Json.HasValue
                   && part.Json.Value.ValueKind != JsonValueKind.Undefined;
        }

        return !string.IsNullOrWhiteSpace(part.ToolCallId)
               && !string.IsNullOrWhiteSpace(part.ToolName)
               && part.Json.HasValue
               && part.Json.Value.ValueKind != JsonValueKind.Undefined
               && (!string.Equals(
                       part.Type,
                       NormalizedPartTypes.ToolCall,
                       StringComparison.Ordinal)
                   || part.Json.Value.ValueKind == JsonValueKind.Object);
    }

    private static ProviderException InvalidRequest() => new(
        "provider_request_invalid",
        "validation",
        "The native provider request is invalid or unsupported.",
        false,
        usageKnownToBeZero: true);

    private static ProviderException RequestTooLarge() => new(
        "provider_request_too_large",
        "validation",
        "The native provider request exceeds its size limit.",
        false,
        usageKnownToBeZero: true);
}

internal static class NativeProviderJson
{
    internal static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(8_192);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        if (buffer.WrittenCount is < 1 or > NativeProviderLimits.MaxRequestBytes)
        {
            throw new ProviderException(
                "provider_request_too_large",
                "validation",
                "The native provider request exceeds its size limit.",
                false,
                usageKnownToBeZero: true);
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal static string JsonText(JsonElement value) =>
        value.GetRawText();

    internal static void WriteContentText(
        Utf8JsonWriter writer,
        NormalizedContentPart part)
    {
        if (string.Equals(part.Type, NormalizedPartTypes.Text, StringComparison.Ordinal))
        {
            writer.WriteStringValue(part.Text ?? string.Empty);
        }
        else if (string.Equals(part.Type, NormalizedPartTypes.Json, StringComparison.Ordinal)
                 && part.Json.HasValue)
        {
            writer.WriteStringValue(part.Json.Value.GetRawText());
        }
    }

    internal static bool TryString(
        JsonElement value,
        string name,
        out string? result)
    {
        result = null;
        return value.ValueKind == JsonValueKind.Object
               && value.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.String
               && (result = property.GetString()) is not null;
    }

    internal static bool TryInt(
        JsonElement value,
        string name,
        out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Object
               && value.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out result)
               && result >= 0;
    }

    internal static JsonElement RequireObject(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw ProtocolError(
                "The provider emitted invalid JSON.",
                exception);
        }
    }

    internal static ProviderException ProtocolError(
        string message,
        Exception? exception = null) => new(
            "provider_stream_protocol_error",
            "provider",
            message,
            false,
            innerException: exception);
}

internal static class NativeProviderRoute
{
    internal static string PolicyDigest(params (string Name, string Value)[] values)
    {
        using var sha = SHA256.Create();
        var builder = new StringBuilder("native-provider-route.v1\n");
        foreach (var value in values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            builder.Append(value.Name)
                .Append('=')
                .Append(value.Value)
                .Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        try
        {
            return string.Concat(sha.ComputeHash(bytes).Select(
                value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    internal static void Validate(
        ProviderStreamPreparationContext context,
        string providerId,
        ProviderRouteMetadata metadata)
    {
        if (!string.Equals(context.ProviderId, providerId, StringComparison.Ordinal)
            || !string.Equals(context.RouteIdentity.ProviderId, providerId, StringComparison.Ordinal)
            || !string.Equals(context.RouteIdentity.ModelId, metadata.ModelId, StringComparison.Ordinal)
            || !string.Equals(context.RouteIdentity.RoutePolicyDigest, metadata.RoutePolicyDigest, StringComparison.Ordinal)
            || !string.Equals(context.RouteIdentity.DialectSemanticDigest, metadata.DialectContract.SemanticDigest, StringComparison.Ordinal))
        {
            throw new ProviderException(
                "provider_route_identity_mismatch",
                "provider",
                "The provider route identity does not match this adapter.",
                false,
                usageKnownToBeZero: true);
        }
    }
}

internal static class NativeProviderErrors
{
    internal static ProviderException Http(
        int statusCode,
        string? retryAfterHeader)
    {
        var retryAfter = RetryAfter(retryAfterHeader);
        return statusCode switch
        {
            400 or 413 or 422 => new ProviderException(
                "provider_invalid_request", "validation",
                "The provider rejected the request.", false,
                usageKnownToBeZero: true),
            401 or 403 => new ProviderException(
                "provider_auth_failed", "auth",
                "The provider rejected the credential or permissions.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            404 => new ProviderException(
                "provider_route_unavailable", "routing",
                "The configured provider route is unavailable.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            408 => new ProviderException(
                "provider_request_timeout", "network",
                "The provider timed out after accepting the request.",
                ProviderFailureDisposition.RetryThenFailover,
                retryAfter),
            409 or 425 or 429 => new ProviderException(
                statusCode == 429 ? "provider_throttled" : "provider_transient_error",
                statusCode == 429 ? "rate_limit" : "provider",
                "The provider temporarily rejected the request.",
                ProviderFailureDisposition.RetryThenFailover,
                retryAfter,
                usageKnownToBeZero: true),
            >= 500 and <= 599 => new ProviderException(
                "provider_unavailable", "overload",
                "The provider is temporarily unavailable.",
                ProviderFailureDisposition.RetryThenFailover,
                retryAfter),
            >= 300 and <= 399 => new ProviderException(
                "provider_redirect_rejected", "network",
                "The provider attempted an unsafe redirect.",
                ProviderFailureDisposition.Failover,
                usageKnownToBeZero: true),
            _ => new ProviderException(
                "provider_http_error", "provider",
                "The provider returned an unsupported HTTP status.", false)
        };
    }

    internal static TimeSpan? RetryAfter(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        if (int.TryParse(
                header,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds)
            && seconds is >= 0 and <= 86_400)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(
                header,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var date))
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero && delay <= TimeSpan.FromDays(1)
                ? delay
                : null;
        }

        return null;
    }

    internal static ProviderException Connect(Exception exception) => new(
        "provider_connect_failed",
        "network",
        "The provider connection failed.",
        true,
        innerException: exception);

    internal static ProviderException MissingCredential(Exception exception) => new(
        "provider_auth_missing",
        "auth",
        "The provider credential is unavailable.",
        ProviderFailureDisposition.Failover,
        innerException: exception,
        usageKnownToBeZero: true);
}

internal sealed class NativePreparedStream : PreparedProviderStream
{
    private readonly Func<byte[], CancellationToken, IAsyncEnumerable<ModelStreamEvent>>
        _stream;
    private byte[] _body;
    private int _started;

    internal NativePreparedStream(
        byte[] body,
        ProviderWireRequestEvidence evidence,
        Func<byte[], CancellationToken, IAsyncEnumerable<ModelStreamEvent>> stream)
        : base(evidence)
    {
        _body = body;
        _stream = stream;
    }

    public override IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "A prepared provider stream can be consumed only once.");
        }

        return _stream(_body, cancellationToken);
    }

    public override ValueTask DisposeAsync()
    {
        var body = Interlocked.Exchange(ref _body, Array.Empty<byte>());
        Array.Clear(body, 0, body.Length);
        return default;
    }
}

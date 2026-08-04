using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameAgent.Protocol;

public static class AgentTransportMessageTypes
{
    public const string Observation = "observation";
    public const string Run = "run";
    public const string Resume = "resume";
    public const string Control = "control";
    public const string RuntimeEvent = "runtime_event";
    public const string ActionRequest = "action_request";
    public const string ActionReceipt = "action_receipt";
    public const string Acknowledgement = "acknowledgement";
    public const string Error = "error";

    internal static bool IsKnown(string value) => value is
        Observation or Run or Resume or Control or RuntimeEvent
        or ActionRequest or ActionReceipt or Acknowledgement or Error;
}

public sealed class AgentTransportEnvelope
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1";

    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

public sealed class AgentTransportLimits
{
    public int MaxEnvelopeBytes { get; set; } = 1_048_576;

    public int MaxPayloadDepth { get; set; } = 64;

    public int MaxPayloadNodes { get; set; } = 65_536;

    internal AgentTransportLimits Snapshot()
    {
        if (MaxEnvelopeBytes is < 1_024 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEnvelopeBytes));
        }

        if (MaxPayloadDepth is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadDepth));
        }

        if (MaxPayloadNodes is < 16 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadNodes));
        }

        return new AgentTransportLimits
        {
            MaxEnvelopeBytes = MaxEnvelopeBytes,
            MaxPayloadDepth = MaxPayloadDepth,
            MaxPayloadNodes = MaxPayloadNodes
        };
    }
}

public sealed class AgentTransportValidationException : FormatException
{
    public AgentTransportValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class AgentTransportCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 128,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly AgentTransportLimits _limits;

    public AgentTransportCodec(AgentTransportLimits? limits = null)
    {
        _limits = (limits ?? new AgentTransportLimits()).Snapshot();
    }

    public byte[] Serialize(AgentTransportEnvelope envelope)
    {
        Validate(envelope);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length > _limits.MaxEnvelopeBytes)
        {
            throw Error("envelope_bytes_exceeded", "The transport envelope exceeds its byte limit.");
        }

        return bytes;
    }

    public AgentTransportEnvelope Deserialize(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 || utf8.Length > _limits.MaxEnvelopeBytes)
        {
            throw Error("envelope_bytes_invalid", "The transport envelope is empty or exceeds its byte limit.");
        }

        AgentTransportEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AgentTransportEnvelope>(utf8, JsonOptions)
                ?? throw Error("envelope_null", "The transport envelope is null.");
        }
        catch (JsonException exception)
        {
            throw new AgentTransportValidationException(
                "envelope_json_invalid",
                "The transport envelope is not valid bounded JSON: " + exception.Message);
        }

        Validate(envelope);
        return envelope;
    }

    public void Validate(AgentTransportEnvelope envelope)
    {
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }
        if (!string.Equals(envelope.Version, "1", StringComparison.Ordinal))
        {
            throw Error("version_unsupported", "The transport protocol version is not supported.");
        }

        ValidateId(envelope.MessageId, nameof(envelope.MessageId));
        ValidateId(envelope.TenantId, nameof(envelope.TenantId));
        ValidateId(envelope.WorldId, nameof(envelope.WorldId));
        ValidateOptionalId(envelope.RunId, nameof(envelope.RunId));
        ValidateOptionalId(envelope.CorrelationId, nameof(envelope.CorrelationId));
        if (!AgentTransportMessageTypes.IsKnown(envelope.Type))
        {
            throw Error("message_type_unsupported", "The transport message type is not supported.");
        }

        if (envelope.Sequence < 0)
        {
            throw Error("sequence_invalid", "The transport sequence cannot be negative.");
        }

        ValidatePayload(envelope.Payload);
    }

    private void ValidatePayload(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            throw Error("payload_missing", "The transport payload is required.");
        }

        var nodes = 0;
        var stack = new Stack<(JsonElement Value, int Depth)>();
        stack.Push((payload, 1));
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item.Depth > _limits.MaxPayloadDepth || ++nodes > _limits.MaxPayloadNodes)
            {
                throw Error("payload_shape_exceeded", "The transport payload exceeds its shape limit.");
            }

            if (item.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in item.Value.EnumerateObject())
                {
                    if (Encoding.UTF8.GetByteCount(property.Name) > 256)
                    {
                        throw Error("payload_property_bytes_exceeded", "A payload property name exceeds its byte limit.");
                    }
                    stack.Push((property.Value, item.Depth + 1));
                }
            }
            else if (item.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in item.Value.EnumerateArray())
                {
                    stack.Push((value, item.Depth + 1));
                }
            }
            else if (item.Value.ValueKind == JsonValueKind.String
                     && Encoding.UTF8.GetByteCount(item.Value.GetString() ?? string.Empty) > _limits.MaxEnvelopeBytes)
            {
                throw Error("payload_string_bytes_exceeded", "A payload string exceeds the envelope byte limit.");
            }
        }
    }

    private static void ValidateOptionalId(string? value, string name)
    {
        if (value is not null)
        {
            ValidateId(value, name);
        }
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw Error("id_invalid", $"{name} is empty or exceeds its character limit.");
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                  or >= 'A' and <= 'Z'
                  or >= '0' and <= '9'
                  or '.' or '_' or ':' or '-'))
            {
                throw Error("id_invalid", $"{name} contains an unsupported character.");
            }
        }
    }

    private static AgentTransportValidationException Error(string code, string message) => new(code, message);
}

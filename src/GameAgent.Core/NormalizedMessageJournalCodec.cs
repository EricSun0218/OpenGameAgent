using System.Buffers;
using System.Text.Json;

namespace GameAgent.Core;

public static class NormalizedMessageJournalCodec
{
    public static JsonElement Encode(NormalizedMessage message)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("messageId", message.MessageId);
            writer.WriteString("role", message.Role);
            writer.WriteString("createdAt", message.CreatedAt);
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            foreach (var part in message.Parts)
            {
                writer.WriteStartObject();
                writer.WriteString("type", part.Type);
                if (part.Text is not null)
                {
                    writer.WriteString("text", part.Text);
                }

                if (part.Json is not null)
                {
                    writer.WritePropertyName("json");
                    part.Json.Value.WriteTo(writer);
                }

                if (part.ToolCallId is not null)
                {
                    writer.WriteString("toolCallId", part.ToolCallId);
                }

                if (part.ToolName is not null)
                {
                    writer.WriteString("toolName", part.ToolName);
                }

                if (part.ToolVersion is not null)
                {
                    writer.WriteString("toolVersion", part.ToolVersion);
                }

                if (part.ToolEffect is not null)
                {
                    writer.WriteString("toolEffect", part.ToolEffect);
                }

                if (part.ToolDescriptorDigest is not null)
                {
                    writer.WriteString(
                        "toolDescriptorDigest",
                        part.ToolDescriptorDigest);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static NormalizedMessage Decode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A normalized message checkpoint must be an object.");
        }

        RejectUnknown(
            value,
            "messageId",
            "role",
            "createdAt",
            "parts");
        var message = new NormalizedMessage
        {
            MessageId = RequiredString(value, "messageId"),
            Role = RequiredString(value, "role"),
            CreatedAt = RequiredDate(value, "createdAt")
        };
        if (!value.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "A normalized message checkpoint has invalid parts.");
        }

        foreach (var item in parts.EnumerateArray())
        {
            RejectUnknown(
                item,
                "type",
                "text",
                "json",
                "toolCallId",
                "toolName",
                "toolVersion",
                "toolEffect",
                "toolDescriptorDigest");
            var type = RequiredString(item, "type");
            var part = new NormalizedContentPart
            {
                Type = type,
                Text = OptionalString(item, "text"),
                ToolCallId = OptionalString(item, "toolCallId"),
                ToolName = OptionalString(item, "toolName"),
                ToolVersion = OptionalString(item, "toolVersion"),
                ToolEffect = OptionalString(item, "toolEffect"),
                ToolDescriptorDigest =
                    OptionalString(item, "toolDescriptorDigest")
            };
            if (item.TryGetProperty("json", out var json))
            {
                part.Json = json.Clone();
            }

            ValidatePart(part);
            message.Parts.Add(part);
        }

        if (message.Parts.Count == 0)
        {
            throw new InvalidDataException(
                "A normalized message checkpoint cannot be empty.");
        }

        return message;
    }

    private static void ValidatePart(NormalizedContentPart part)
    {
        var valid = part.Type switch
        {
            NormalizedPartTypes.Text or NormalizedPartTypes.Reasoning =>
                part.Text is not null && part.Json is null,
            NormalizedPartTypes.Json =>
                part.Json is not null && part.Text is null,
            NormalizedPartTypes.ToolCall or NormalizedPartTypes.ToolResult =>
                part.Json is not null
                && !string.IsNullOrWhiteSpace(part.ToolCallId)
                && !string.IsNullOrWhiteSpace(part.ToolName),
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException(
                "A normalized message checkpoint contains an invalid part.");
        }

        var hasToolEvidence = part.ToolVersion is not null
                              || part.ToolEffect is not null
                              || part.ToolDescriptorDigest is not null;
        if (hasToolEvidence
            && !string.Equals(
                part.Type,
                NormalizedPartTypes.ToolCall,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Durable tool evidence is valid only on tool-call parts.");
        }

        if (part.ToolVersion is not null)
        {
            ValidateBoundedEvidence(part.ToolVersion, 32, "toolVersion");
        }

        if (part.ToolEffect is not null
            && !IsKnownToolEffect(part.ToolEffect))
        {
            throw new InvalidDataException(
                "A normalized message checkpoint has invalid 'toolEffect'.");
        }

        if (part.ToolDescriptorDigest is not null)
        {
            ValidateBoundedEvidence(
                part.ToolDescriptorDigest,
                256,
                "toolDescriptorDigest");
        }
    }

    private static bool IsKnownToolEffect(string effect)
    {
        return string.Equals(effect, GameAgent.Protocol.ToolEffects.PureRead, StringComparison.Ordinal)
               || string.Equals(effect, GameAgent.Protocol.ToolEffects.AgentLocalWrite, StringComparison.Ordinal)
               || string.Equals(effect, GameAgent.Protocol.ToolEffects.WorldCommand, StringComparison.Ordinal)
               || string.Equals(effect, GameAgent.Protocol.ToolEffects.ExternalWrite, StringComparison.Ordinal);
    }

    private static void ValidateBoundedEvidence(
        string value,
        int maximumUtf8Bytes,
        string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || System.Text.Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new InvalidDataException(
                $"A normalized message checkpoint has invalid '{field}'.");
        }
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"A normalized message checkpoint is missing '{name}'.");
        }

        return property.GetString()!;
    }

    private static string? OptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"A normalized message checkpoint has invalid '{name}'.");
        }

        return property.GetString();
    }

    private static DateTimeOffset RequiredDate(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || !property.TryGetDateTimeOffset(out var result))
        {
            throw new InvalidDataException(
                $"A normalized message checkpoint has invalid '{name}'.");
        }

        return result;
    }

    private static void RejectUnknown(
        JsonElement value,
        params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A normalized message checkpoint contains a non-object.");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unknown normalized message field '{property.Name}'.");
            }
        }
    }
}

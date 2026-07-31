using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public static class NormalizedRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

public static class NormalizedPartTypes
{
    public const string Text = "text";
    public const string Reasoning = "reasoning";
    public const string Json = "json";
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
}

public sealed class NormalizedContentPart
{
    public string Type { get; set; } = string.Empty;

    public string? Text { get; set; }

    public JsonElement? Json { get; set; }

    public string? ToolCallId { get; set; }

    public string? ToolName { get; set; }

    public string? ToolVersion { get; set; }

    public string? ToolEffect { get; set; }

    public string? ToolDescriptorDigest { get; set; }

    public static NormalizedContentPart FromText(string text)
    {
        return new NormalizedContentPart
        {
            Type = NormalizedPartTypes.Text,
            Text = text
        };
    }

    public static NormalizedContentPart FromJson(JsonElement json)
    {
        return new NormalizedContentPart
        {
            Type = NormalizedPartTypes.Json,
            Json = json.Clone()
        };
    }

    public static NormalizedContentPart FromReasoning(string reasoning)
    {
        return new NormalizedContentPart
        {
            Type = NormalizedPartTypes.Reasoning,
            Text = reasoning
        };
    }

    public static NormalizedContentPart FromToolCall(ModelToolCall toolCall)
    {
        return new NormalizedContentPart
        {
            Type = NormalizedPartTypes.ToolCall,
            ToolCallId = toolCall.ToolCallId,
            ToolName = toolCall.Name,
            Json = toolCall.Arguments.Clone()
        };
    }

    public static NormalizedContentPart FromToolResult(
        string toolCallId,
        string toolName,
        JsonElement result)
    {
        return new NormalizedContentPart
        {
            Type = NormalizedPartTypes.ToolResult,
            ToolCallId = toolCallId,
            ToolName = toolName,
            Json = result.Clone()
        };
    }
}

public sealed class NormalizedMessage
{
    public string MessageId { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public List<NormalizedContentPart> Parts { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }
}

public static class NormalizedTranscript
{
    public static NormalizedMessage ObservationMessage(
        string messageId,
        IReadOnlyList<ObservationEnvelope> observations,
        DateTimeOffset createdAt)
    {
        var observationElements = observations
            .Select(ProtocolJson.ToElement)
            .ToArray();

        var json = JsonArrayBuilder.Object(
            ("contentType", JsonArrayBuilder.String(
                "application/vnd.game-agent.observations+json")),
            ("observations", JsonArrayBuilder.Array(observationElements)));

        return new NormalizedMessage
        {
            MessageId = messageId,
            Role = NormalizedRoles.User,
            CreatedAt = createdAt,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromJson(json)
            }
        };
    }

    public static NormalizedMessage AssistantToolCalls(
        string messageId,
        IReadOnlyList<ModelToolCall> calls,
        DateTimeOffset createdAt)
    {
        return AssistantResponse(
            messageId,
            text: null,
            reasoningContent: null,
            calls,
            createdAt);
    }

    public static NormalizedMessage AssistantResponse(
        string messageId,
        string? text,
        string? reasoningContent,
        IReadOnlyList<ModelToolCall> calls,
        DateTimeOffset createdAt)
    {
        var parts = new List<NormalizedContentPart>();
        if (text is not null)
        {
            parts.Add(NormalizedContentPart.FromText(text));
        }

        if (reasoningContent is not null)
        {
            parts.Add(NormalizedContentPart.FromReasoning(reasoningContent));
        }

        parts.AddRange(calls.Select(NormalizedContentPart.FromToolCall));
        return new NormalizedMessage
        {
            MessageId = messageId,
            Role = NormalizedRoles.Assistant,
            CreatedAt = createdAt,
            Parts = parts
        };
    }

    public static NormalizedMessage ToolResult(
        string messageId,
        string toolCallId,
        string toolName,
        ActionReceipt receipt,
        DateTimeOffset createdAt)
    {
        return ToolResult(
            messageId,
            toolCallId,
            toolName,
            ProtocolJson.ToElement(receipt),
            createdAt);
    }

    internal static NormalizedMessage ToolResult(
        string messageId,
        string toolCallId,
        string toolName,
        JsonElement result,
        DateTimeOffset createdAt)
    {
        return new NormalizedMessage
        {
            MessageId = messageId,
            Role = NormalizedRoles.Tool,
            CreatedAt = createdAt,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromToolResult(
                    toolCallId,
                    toolName,
                    result)
            }
        };
    }
}

public static class JsonArrayBuilder
{
    public static JsonElement String(string value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    public static JsonElement Number(long value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteNumberValue(value);
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    public static JsonElement Boolean(bool value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteBooleanValue(value);
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    public static JsonElement Null()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    public static JsonElement Strings(IEnumerable<string> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        return Array(values.Select(String));
    }

    public static JsonElement Array(IEnumerable<JsonElement> values)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                value.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    public static JsonElement Object(params (string Name, JsonElement Value)[] properties)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in properties)
            {
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }
}

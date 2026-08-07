using System;
using System.Text.Json;

namespace OpenGameAgent.Kernel;

public enum AgentContentKind
{
    Text,
    Json,
    Resource,
    Reasoning,
    ToolCall,
}

public abstract class AgentContent
{
    protected AgentContent(AgentContentKind kind)
    {
        Kind = kind;
    }

    public AgentContentKind Kind { get; }
}

public sealed class TextContent : AgentContent
{
    public TextContent(string text)
        : base(AgentContentKind.Text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public string Text { get; }
}

public sealed class JsonContent : AgentContent
{
    public JsonContent(string json)
        : base(AgentContentKind.Json)
    {
        Json = JsonValue.RequireValid(json, nameof(json));
    }

    public string Json { get; }

    public JsonDocument Parse() => JsonDocument.Parse(Json);
}

public sealed class ResourceContent : AgentContent
{
    public ResourceContent(string uri, string mediaType, string? name = null)
        : base(AgentContentKind.Resource)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new ArgumentException("A resource URI is required.", nameof(uri));
        }

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException("A resource media type is required.", nameof(mediaType));
        }

        Uri = uri;
        MediaType = mediaType;
        Name = name;
    }

    public string Uri { get; }

    public string MediaType { get; }

    public string? Name { get; }
}

public sealed class ReasoningContent : AgentContent
{
    public ReasoningContent(string text, string? signature = null)
        : base(AgentContentKind.Reasoning)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Signature = signature;
    }

    public string Text { get; }

    public string? Signature { get; }
}

public sealed class ToolCallContent : AgentContent
{
    public ToolCallContent(string id, string name, string argumentsJson)
        : base(AgentContentKind.ToolCall)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A tool call ID is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A tool name is required.", nameof(name));
        }

        Id = id;
        Name = name;
        ArgumentsJson = JsonValue.RequireObject(argumentsJson, nameof(argumentsJson));
    }

    public string Id { get; }

    public string Name { get; }

    public string ArgumentsJson { get; }
}

internal static class JsonValue
{
    public static string RequireValid(string json, string parameterName)
    {
        if (json is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureUnambiguous(document.RootElement, parameterName);
            return document.RootElement.GetRawText();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The value must contain valid JSON.", parameterName, exception);
        }
    }

    public static string RequireObject(string json, string parameterName)
    {
        var valid = RequireValid(json, parameterName);
        using var document = JsonDocument.Parse(valid);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The value must contain a JSON object.", parameterName);
        }

        return valid;
    }

    private static void EnsureUnambiguous(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException("JSON objects cannot contain duplicate property names.", parameterName);
                }

                EnsureUnambiguous(property.Value, parameterName);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item, parameterName);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using GameAgent.Generation;
using GodotDictionary = global::Godot.Collections.Dictionary;

namespace GameAgent.Godot;

public static class GodotGenerationVariantMapper
{
    private const int MaximumRequestBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> Properties = new(
        new[]
        {
            "operation_id",
            "modality",
            "model",
            "input",
            "options",
            "metadata",
            "authority_id",
            "idempotency_key"
        },
        StringComparer.Ordinal);

    public static GenerationRequest ToGenerationRequest(GodotDictionary request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = GodotVariantInputGuard.StringifyAndNormalizeDictionary(
            request,
            "$.generation",
            MaximumRequestBytes);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var property in root.EnumerateObject())
        {
            if (!Properties.Contains(property.Name))
            {
                throw new JsonException(
                    $"Unknown generation property '{property.Name}'.");
            }
        }

        if (!root.TryGetProperty("input", out var input))
        {
            throw new JsonException("Generation request requires 'input'.");
        }

        return new GenerationRequest
        {
            OperationId = RequiredString(root, "operation_id"),
            Modality = RequiredString(root, "modality"),
            Model = OptionalString(root, "model"),
            Input = input.Clone(),
            Options = JsonMap(root, "options"),
            Metadata = StringMap(root, "metadata"),
            AuthorityId = OptionalString(root, "authority_id"),
            IdempotencyKey = OptionalString(root, "idempotency_key")
        };
    }

    private static Dictionary<string, JsonElement> JsonMap(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Generation '{name}' must be an object.");
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            result.Add(property.Name, property.Value.Clone());
        }

        return result;
    }

    private static Dictionary<string, string> StringMap(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Generation '{name}' must be an object.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new JsonException(
                    $"Generation '{name}' values must be strings.");
            }

            result.Add(property.Name, property.Value.GetString()!);
        }

        return result;
    }

    private static string RequiredString(JsonElement root, string name) =>
        OptionalString(root, name)
        ?? throw new JsonException($"Generation request requires '{name}'.");

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"Generation '{name}' must be a non-empty string.");
        }

        return value.GetString();
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace OpenGameAgent.Models;

[Flags]
public enum GameModelInputCapabilities
{
    None = 0,
    Text = 1 << 0,
    Image = 1 << 1,
    Audio = 1 << 2,
    Video = 1 << 3,
    StructuredData = 1 << 4,
}

[Flags]
public enum GameModelOutputCapabilities
{
    None = 0,
    Text = 1 << 0,
    Image = 1 << 1,
    Audio = 1 << 2,
    Video = 1 << 3,
    StructuredData = 1 << 4,
    ToolCalls = 1 << 5,
    Reasoning = 1 << 6,
}

public enum GameReasoningLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    ExtraHigh,
    Maximum,
}

public sealed class GameModelCost
{
    public GameModelCost(
        decimal inputPerMillionTokens = 0,
        decimal outputPerMillionTokens = 0,
        decimal cacheReadPerMillionTokens = 0,
        decimal cacheWritePerMillionTokens = 0)
    {
        InputPerMillionTokens = RequireCost(inputPerMillionTokens, nameof(inputPerMillionTokens));
        OutputPerMillionTokens = RequireCost(outputPerMillionTokens, nameof(outputPerMillionTokens));
        CacheReadPerMillionTokens = RequireCost(cacheReadPerMillionTokens, nameof(cacheReadPerMillionTokens));
        CacheWritePerMillionTokens = RequireCost(cacheWritePerMillionTokens, nameof(cacheWritePerMillionTokens));
    }

    public decimal InputPerMillionTokens { get; }

    public decimal OutputPerMillionTokens { get; }

    public decimal CacheReadPerMillionTokens { get; }

    public decimal CacheWritePerMillionTokens { get; }

    private static decimal RequireCost(decimal value, string parameterName) =>
        value is >= 0 and <= 1_000_000
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);
}

public sealed class GameModelDescriptor
{
    private static readonly GameReasoningLevel[] ReasoningOrder =
    {
        GameReasoningLevel.Off,
        GameReasoningLevel.Minimal,
        GameReasoningLevel.Low,
        GameReasoningLevel.Medium,
        GameReasoningLevel.High,
        GameReasoningLevel.ExtraHigh,
        GameReasoningLevel.Maximum,
    };

    public GameModelDescriptor(
        string providerId,
        string modelId,
        string? displayName = null,
        int contextWindowTokens = 0,
        int maximumOutputTokens = 0,
        GameModelInputCapabilities inputCapabilities = GameModelInputCapabilities.Text | GameModelInputCapabilities.StructuredData,
        GameModelOutputCapabilities outputCapabilities = GameModelOutputCapabilities.Text | GameModelOutputCapabilities.ToolCalls,
        IReadOnlyCollection<GameReasoningLevel>? reasoningLevels = null,
        GameModelCost? cost = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<GameReasoningLevel, string>? reasoningLevelValues = null)
    {
        ProviderId = RequireId(providerId, nameof(providerId));
        ModelId = RequireId(modelId, nameof(modelId));
        DisplayName = displayName is null ? ModelId : RequireId(displayName, nameof(displayName));
        if (contextWindowTokens < 0 || maximumOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextWindowTokens));
        }

        if (contextWindowTokens > 0 && maximumOutputTokens >= contextWindowTokens)
        {
            throw new ArgumentException("A model's maximum output must be smaller than its context window.");
        }

        ValidateFlags(inputCapabilities, nameof(inputCapabilities));
        ValidateFlags(outputCapabilities, nameof(outputCapabilities));
        var levels = (reasoningLevels ?? Array.Empty<GameReasoningLevel>())
            .Distinct()
            .OrderBy(level => Array.IndexOf(ReasoningOrder, level))
            .ToArray();
        if (levels.Any(level => !Enum.IsDefined(typeof(GameReasoningLevel), level)))
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningLevels));
        }

        if (levels.Length == 0)
        {
            levels = new[] { GameReasoningLevel.Off };
        }
        else if (!levels.Contains(GameReasoningLevel.Off))
        {
            levels = new[] { GameReasoningLevel.Off }.Concat(levels).ToArray();
        }

        if (levels.Any(level => level != GameReasoningLevel.Off)
            && !outputCapabilities.HasFlag(GameModelOutputCapabilities.Reasoning))
        {
            throw new ArgumentException("Reasoning levels require the reasoning output capability.", nameof(reasoningLevels));
        }

        ContextWindowTokens = contextWindowTokens;
        MaximumOutputTokens = maximumOutputTokens;
        InputCapabilities = inputCapabilities;
        OutputCapabilities = outputCapabilities;
        ReasoningLevels = Array.AsReadOnly(levels);
        var values = new Dictionary<GameReasoningLevel, string>();
        foreach (var pair in reasoningLevelValues ?? new Dictionary<GameReasoningLevel, string>())
        {
            if (!Enum.IsDefined(typeof(GameReasoningLevel), pair.Key)
                || pair.Key == GameReasoningLevel.Off
                || !levels.Contains(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)
                || pair.Value.Length > 128)
            {
                throw new ArgumentException("A reasoning-level value must target a supported non-off level and contain at most 128 characters.", nameof(reasoningLevelValues));
            }

            values.Add(pair.Key, pair.Value);
        }

        ReasoningLevelValues = new ReadOnlyDictionary<GameReasoningLevel, string>(values);
        Cost = cost ?? new GameModelCost();
        Metadata = CopyMetadata(metadata);
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public string DisplayName { get; }

    public int ContextWindowTokens { get; }

    public int MaximumOutputTokens { get; }

    public GameModelInputCapabilities InputCapabilities { get; }

    public GameModelOutputCapabilities OutputCapabilities { get; }

    public IReadOnlyList<GameReasoningLevel> ReasoningLevels { get; }

    public IReadOnlyDictionary<GameReasoningLevel, string> ReasoningLevelValues { get; }

    public GameModelCost Cost { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public GameReasoningLevel ClampReasoning(GameReasoningLevel requested)
    {
        if (!Enum.IsDefined(typeof(GameReasoningLevel), requested))
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }

        if (ReasoningLevels.Contains(requested))
        {
            return requested;
        }

        var requestedIndex = Array.IndexOf(ReasoningOrder, requested);
        for (var index = requestedIndex; index < ReasoningOrder.Length; index++)
        {
            if (ReasoningLevels.Contains(ReasoningOrder[index]))
            {
                return ReasoningOrder[index];
            }
        }

        for (var index = requestedIndex - 1; index >= 0; index--)
        {
            if (ReasoningLevels.Contains(ReasoningOrder[index]))
            {
                return ReasoningOrder[index];
            }
        }

        return GameReasoningLevel.Off;
    }

    public bool Supports(
        GameModelInputCapabilities requiredInput,
        GameModelOutputCapabilities requiredOutput)
    {
        ValidateFlags(requiredInput, nameof(requiredInput));
        ValidateFlags(requiredOutput, nameof(requiredOutput));
        return (InputCapabilities & requiredInput) == requiredInput
            && (OutputCapabilities & requiredOutput) == requiredOutput;
    }

    public string? GetReasoningValue(GameReasoningLevel level)
    {
        if (!Enum.IsDefined(typeof(GameReasoningLevel), level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (level == GameReasoningLevel.Off)
        {
            return null;
        }

        if (!ReasoningLevels.Contains(level))
        {
            throw new InvalidOperationException($"Reasoning level '{level}' is not supported by this model.");
        }

        if (ReasoningLevelValues.TryGetValue(level, out var configured))
        {
            return configured;
        }

        return level switch
        {
            GameReasoningLevel.Minimal => "minimal",
            GameReasoningLevel.Low => "low",
            GameReasoningLevel.Medium => "medium",
            GameReasoningLevel.High => "high",
            GameReasoningLevel.ExtraHigh => "xhigh",
            GameReasoningLevel.Maximum => "max",
            _ => throw new InvalidOperationException("The reasoning level is invalid."),
        };
    }

    internal static string RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            throw new ArgumentException("A non-empty identifier of at most 512 characters is required.", parameterName);
        }

        return value;
    }

    private static IReadOnlyDictionary<string, string> CopyMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is { Count: > 256 })
        {
            throw new ArgumentException("Model metadata cannot contain more than 256 entries.", nameof(metadata));
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata ?? new Dictionary<string, string>())
        {
            var key = RequireId(pair.Key, nameof(metadata));
            if (pair.Value is null || pair.Value.Length > 16_384 || !copy.TryAdd(key, pair.Value))
            {
                throw new ArgumentException("Model metadata is invalid or contains duplicate keys.", nameof(metadata));
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    internal static void ValidateFlags<T>(T value, string parameterName)
        where T : struct, Enum
    {
        var numeric = Convert.ToUInt64(value);
        var allowed = Enum.GetValues(typeof(T)).Cast<T>().Aggregate(0UL, (current, item) => current | Convert.ToUInt64(item));
        if ((numeric & ~allowed) != 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using OpenGameAgent.Kernel;

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
        decimal cacheWritePerMillionTokens = 0,
        IReadOnlyCollection<GameModelCostTier>? tiers = null,
        bool? isKnown = null)
    {
        InputPerMillionTokens = RequireCost(inputPerMillionTokens, nameof(inputPerMillionTokens));
        OutputPerMillionTokens = RequireCost(outputPerMillionTokens, nameof(outputPerMillionTokens));
        CacheReadPerMillionTokens = RequireCost(cacheReadPerMillionTokens, nameof(cacheReadPerMillionTokens));
        CacheWritePerMillionTokens = RequireCost(cacheWritePerMillionTokens, nameof(cacheWritePerMillionTokens));
        var copiedTiers = (tiers ?? Array.Empty<GameModelCostTier>())
            .OrderBy(tier => tier.InputTokensAbove)
            .ToArray();
        if (copiedTiers.Any(tier => tier is null)
            || copiedTiers.Select(tier => tier.InputTokensAbove).Distinct().Count() != copiedTiers.Length)
        {
            throw new ArgumentException("Cost tiers must be non-null and use unique thresholds.", nameof(tiers));
        }

        IsKnown = isKnown
            ?? (inputPerMillionTokens != 0
                || outputPerMillionTokens != 0
                || cacheReadPerMillionTokens != 0
                || cacheWritePerMillionTokens != 0
                || copiedTiers.Length != 0);
        Tiers = Array.AsReadOnly(copiedTiers);
    }

    public decimal InputPerMillionTokens { get; }

    public decimal OutputPerMillionTokens { get; }

    public decimal CacheReadPerMillionTokens { get; }

    public decimal CacheWritePerMillionTokens { get; }

    public bool IsKnown { get; }

    public IReadOnlyList<GameModelCostTier> Tiers { get; }

    public GameModelCost RatesForInput(long inputTokens)
    {
        if (inputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens));
        }

        var tier = Tiers.LastOrDefault(candidate => inputTokens > candidate.InputTokensAbove);
        return tier is null
            ? this
            : new GameModelCost(
                tier.InputPerMillionTokens,
                tier.OutputPerMillionTokens,
                tier.CacheReadPerMillionTokens,
                tier.CacheWritePerMillionTokens,
                tiers: null,
                isKnown: IsKnown);
    }

    public ModelCost Estimate(ModelUsage usage)
    {
        if (usage is null)
        {
            throw new ArgumentNullException(nameof(usage));
        }
        if (!IsKnown)
        {
            return new ModelCost();
        }

        const decimal scale = 1_000_000m;
        var inputVolume = checked(usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens);
        var rates = RatesForInput(inputVolume);
        var longCacheWrite = usage.CacheWriteOneHourTokens ?? 0;
        var shortCacheWrite = usage.CacheWriteTokens - longCacheWrite;
        return new ModelCost(
            DecimalToDouble(usage.InputTokens / scale * rates.InputPerMillionTokens),
            DecimalToDouble(usage.OutputTokens / scale * rates.OutputPerMillionTokens),
            DecimalToDouble(usage.CacheReadTokens / scale * rates.CacheReadPerMillionTokens),
            DecimalToDouble(
                shortCacheWrite / scale * rates.CacheWritePerMillionTokens
                + longCacheWrite / scale * rates.InputPerMillionTokens * 2),
            isKnown: true);
    }

    private static double DecimalToDouble(decimal value)
    {
        var result = (double)value;
        return double.IsNaN(result) || double.IsInfinity(result)
            ? throw new OverflowException("The estimated model cost is too large.")
            : result;
    }

    private static decimal RequireCost(decimal value, string parameterName) =>
        value is >= 0 and <= 1_000_000
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);
}

public sealed class GameModelCostTier
{
    public GameModelCostTier(
        long inputTokensAbove,
        decimal inputPerMillionTokens,
        decimal outputPerMillionTokens,
        decimal cacheReadPerMillionTokens = 0,
        decimal cacheWritePerMillionTokens = 0)
    {
        if (inputTokensAbove < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokensAbove));
        }

        InputTokensAbove = inputTokensAbove;
        var rates = new GameModelCost(
            inputPerMillionTokens,
            outputPerMillionTokens,
            cacheReadPerMillionTokens,
            cacheWritePerMillionTokens);
        InputPerMillionTokens = rates.InputPerMillionTokens;
        OutputPerMillionTokens = rates.OutputPerMillionTokens;
        CacheReadPerMillionTokens = rates.CacheReadPerMillionTokens;
        CacheWritePerMillionTokens = rates.CacheWritePerMillionTokens;
    }

    public long InputTokensAbove { get; }

    public decimal InputPerMillionTokens { get; }

    public decimal OutputPerMillionTokens { get; }

    public decimal CacheReadPerMillionTokens { get; }

    public decimal CacheWritePerMillionTokens { get; }
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
        IReadOnlyDictionary<GameReasoningLevel, string>? reasoningLevelValues = null,
        string api = "custom",
        Uri? baseUrl = null,
        string? samplingParametersJson = null,
        IReadOnlyDictionary<string, string?>? headers = null,
        string? compatibilityJson = null)
    {
        ProviderId = RequireId(providerId, nameof(providerId));
        ModelId = RequireId(modelId, nameof(modelId));
        DisplayName = displayName is null ? ModelId : RequireId(displayName, nameof(displayName));
        Api = RequireId(api, nameof(api));
        if (baseUrl is not null
            && (!baseUrl.IsAbsoluteUri
                || baseUrl.UserInfo.Length > 0
                || baseUrl.Fragment.Length > 0
                || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps)))
        {
            throw new ArgumentException("A model base URL must be an absolute HTTP or HTTPS URL without embedded credentials or a fragment.", nameof(baseUrl));
        }

        BaseUrl = baseUrl;
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
                || !levels.Contains(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)
                || pair.Value.Length > 128)
            {
                throw new ArgumentException("A reasoning-level value must target a supported level and contain at most 128 characters.", nameof(reasoningLevelValues));
            }

            values.Add(pair.Key, pair.Value);
        }

        ReasoningLevelValues = new ReadOnlyDictionary<GameReasoningLevel, string>(values);
        Cost = cost ?? new GameModelCost();
        Metadata = CopyMetadata(metadata);
        SamplingParametersJson = samplingParametersJson is null
            ? null
            : RequireObjectJson(samplingParametersJson, nameof(samplingParametersJson));
        Headers = CopyHeaders(headers);
        CompatibilityJson = compatibilityJson is null
            ? null
            : RequireObjectJson(compatibilityJson, nameof(compatibilityJson));
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public string DisplayName { get; }

    public string Api { get; }

    public Uri? BaseUrl { get; }

    public int ContextWindowTokens { get; }

    public int MaximumOutputTokens { get; }

    public GameModelInputCapabilities InputCapabilities { get; }

    public GameModelOutputCapabilities OutputCapabilities { get; }

    public IReadOnlyList<GameReasoningLevel> ReasoningLevels { get; }

    public IReadOnlyDictionary<GameReasoningLevel, string> ReasoningLevelValues { get; }

    public GameModelCost Cost { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public string? SamplingParametersJson { get; }

    public IReadOnlyDictionary<string, string?> Headers { get; }

    public string? CompatibilityJson { get; }

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

        throw new InvalidOperationException("The model does not expose any reasoning level.");
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
            GameReasoningLevel.Off => null,
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

    private static IReadOnlyDictionary<string, string?> CopyHeaders(
        IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is { Count: > 64 })
        {
            throw new ArgumentException("Model headers cannot contain more than 64 entries.", nameof(headers));
        }

        var copy = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers ?? new Dictionary<string, string?>())
        {
            if (!IsHeaderName(pair.Key)
                || pair.Value is { Length: > 16_384 }
                || pair.Value?.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                || !copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    "Model headers contain an invalid or case-insensitively duplicate entry.",
                    nameof(headers));
            }
        }

        return new ReadOnlyDictionary<string, string?>(copy);
    }

    private static bool IsHeaderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                  || character is >= 'A' and <= 'Z'
                  || character is >= '0' and <= '9'
                  || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static string RequireObjectJson(string value, string parameterName)
    {
        if (value.Length > 1_000_000)
        {
            throw new ArgumentException("Model JSON metadata is too large.", parameterName);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new ArgumentException("Model JSON metadata must be an object.", parameterName);
            }

            return document.RootElement.GetRawText();
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ArgumentException("Model JSON metadata must contain valid JSON.", parameterName, exception);
        }
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

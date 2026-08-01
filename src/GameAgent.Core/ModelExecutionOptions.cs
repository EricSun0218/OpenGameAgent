namespace GameAgent.Core;

public static class ModelReasoningEfforts
{
    public const string None = "none";
    public const string Minimal = "minimal";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string ExtraHigh = "xhigh";
    public const string Maximum = "max";

    internal static bool IsKnown(string value) =>
        string.Equals(value, None, StringComparison.Ordinal)
        || string.Equals(value, Minimal, StringComparison.Ordinal)
        || string.Equals(value, Low, StringComparison.Ordinal)
        || string.Equals(value, Medium, StringComparison.Ordinal)
        || string.Equals(value, High, StringComparison.Ordinal)
        || string.Equals(value, ExtraHigh, StringComparison.Ordinal)
        || string.Equals(value, Maximum, StringComparison.Ordinal);
}

public static class PromptCacheRetentions
{
    public const string FiveMinutes = "5m";
    public const string OneHour = "1h";

    internal static bool IsKnown(string value) =>
        string.Equals(value, FiveMinutes, StringComparison.Ordinal)
        || string.Equals(value, OneHour, StringComparison.Ordinal);
}

/// <summary>
/// Provider-neutral inference controls for one operation. Providers must
/// either map a requested control to their wire dialect or reject it with a
/// capability error; silently dropping a requested control is not allowed.
/// </summary>
public sealed class ModelInferenceOptions
{
    public bool? ReasoningEnabled { get; set; }

    public string? ReasoningEffort { get; set; }

    public int? ReasoningTokenBudget { get; set; }

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? Seed { get; set; }

    /// <summary>
    /// Requests provider prompt caching. Null leaves the provider default;
    /// false requests bypass where the selected dialect can guarantee it.
    /// </summary>
    public bool? PromptCachingEnabled { get; set; }

    /// <summary>
    /// Optional stable, non-secret cache bucketing key.
    /// </summary>
    public string? PromptCacheKey { get; set; }

    public string? PromptCacheRetention { get; set; }

    public ModelInferenceOptions CloneValidated()
    {
        if (ReasoningEffort is not null
            && !ModelReasoningEfforts.IsKnown(ReasoningEffort))
        {
            throw new ArgumentException(
                "The reasoning effort is unsupported.",
                nameof(ReasoningEffort));
        }

        if (ReasoningTokenBudget is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReasoningTokenBudget));
        }

        if (ReasoningEnabled == true
            && string.Equals(
                ReasoningEffort,
                ModelReasoningEfforts.None,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Enabled reasoning cannot use the none effort.",
                nameof(ReasoningEffort));
        }

        var reasoningDisabled = ReasoningEnabled == false
                                || string.Equals(
                                    ReasoningEffort,
                                    ModelReasoningEfforts.None,
                                    StringComparison.Ordinal);
        if (reasoningDisabled
            && (ReasoningTokenBudget.HasValue
                || ReasoningEnabled == false
                && ReasoningEffort is not null
                && !string.Equals(
                    ReasoningEffort,
                    ModelReasoningEfforts.None,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Disabled reasoning cannot carry a reasoning budget or effort.",
                nameof(ReasoningEnabled));
        }

        if (Temperature.HasValue
            && (!double.IsFinite(Temperature.Value)
                || Temperature.Value is < 0 or > 2))
        {
            throw new ArgumentOutOfRangeException(nameof(Temperature));
        }

        if (TopP.HasValue
            && (!double.IsFinite(TopP.Value)
                || TopP.Value is <= 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(TopP));
        }

        if (Temperature.HasValue && TopP.HasValue)
        {
            throw new ArgumentException(
                "Set temperature or top-p, not both.");
        }

        if (ReasoningEnabled == true
            && (Temperature.HasValue || TopP.HasValue))
        {
            throw new ArgumentException(
                "Sampling controls cannot be combined with explicit reasoning mode.");
        }

        if (PromptCacheKey is not null)
        {
            _ = RuntimeGuard.RequiredUtf8(
                PromptCacheKey,
                256,
                nameof(PromptCacheKey));
            if (PromptCachingEnabled == false)
            {
                throw new ArgumentException(
                    "A prompt-cache key cannot be used while caching is disabled.",
                    nameof(PromptCacheKey));
            }
        }

        if (PromptCacheRetention is not null
            && !PromptCacheRetentions.IsKnown(PromptCacheRetention))
        {
            throw new ArgumentException(
                "The prompt-cache retention is unsupported.",
                nameof(PromptCacheRetention));
        }

        if (PromptCacheRetention is not null
            && PromptCachingEnabled != true)
        {
            throw new ArgumentException(
                "Prompt-cache retention requires caching to be explicitly enabled.",
                nameof(PromptCacheRetention));
        }

        return new ModelInferenceOptions
        {
            ReasoningEnabled = ReasoningEnabled,
            ReasoningEffort = ReasoningEffort,
            ReasoningTokenBudget = ReasoningTokenBudget,
            Temperature = Temperature,
            TopP = TopP,
            Seed = Seed,
            PromptCachingEnabled = PromptCachingEnabled,
            PromptCacheKey = PromptCacheKey,
            PromptCacheRetention = PromptCacheRetention
        };
    }
}

/// <summary>
/// Selects a configured provider/model route for one operation. Provider IDs
/// are configuration identities whose route metadata binds the actual model.
/// </summary>
public sealed class ProviderRoutePreference
{
    public IReadOnlyList<string> ProviderIds { get; set; } =
        Array.Empty<string>();

    public bool AllowUnlistedFallback { get; set; }

    public ProviderRoutePreference CloneValidated()
    {
        if (ProviderIds is null)
        {
            throw new ArgumentNullException(nameof(ProviderIds));
        }

        if (ProviderIds.Count is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(ProviderIds));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new string[ProviderIds.Count];
        for (var index = 0; index < ids.Length; index++)
        {
            var id = RuntimeGuard.RequiredUtf8(
                ProviderIds[index],
                128,
                nameof(ProviderIds));
            if (!seen.Add(id))
            {
                throw new ArgumentException(
                    "Provider route preferences cannot contain duplicates.",
                    nameof(ProviderIds));
            }

            ids[index] = id;
        }

        return new ProviderRoutePreference
        {
            ProviderIds = ids,
            AllowUnlistedFallback = AllowUnlistedFallback
        };
    }
}

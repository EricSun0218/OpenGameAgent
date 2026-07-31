using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.Core;

public static class ProviderCacheBreakReasonCodes
{
    public const string ColdStart = "cold_start";

    public const string LayoutChanged = "layout_changed";

    public const string StablePrefixChanged = "stable_prefix_changed";

    public const string ToolCatalogChanged = "tool_catalog_changed";

    public const string SkillChanged = "skill_changed";

    public const string ProviderRouteChanged = "provider_route_changed";

    public const string ExplicitInvalidation = "explicit_invalidation";

    // Retained as aliases for callers that used the original constants.
    // These codes describe dynamic-tail changes and are not cache breaks.
    public const string MemoryChanged =
        ProviderCacheDynamicTailChangeCodes.MemoryChanged;

    public const string CompactionChanged =
        ProviderCacheDynamicTailChangeCodes.CompactionChanged;

    public const string DynamicRequestChanged =
        ProviderCacheDynamicTailChangeCodes.DynamicRequestChanged;
}

public static class ProviderCacheDynamicTailChangeCodes
{
    public const string MemoryChanged = "memory_changed";

    public const string CompactionChanged = "compaction_changed";

    public const string DynamicRequestChanged = "dynamic_request_changed";
}

public static class ProviderCacheUsageStates
{
    public const string Unknown = "unknown";

    public const string Hit = "hit";

    public const string Write = "write";

    public const string Miss = "miss";

    public const string NoActivity = "no_activity";
}

/// <summary>
/// Identifies the stable logical provider prefix separately from dynamic
/// request-tail diagnostics. Every digest is non-secret evidence, never raw
/// prompt data.
/// </summary>
public sealed class ProviderCacheKey
{
    public const string EvidenceVersion = "provider-cache-key.v1";

    public ProviderCacheKey(
        string layoutVersion,
        string stablePrefixDigest,
        string toolCatalogDigest,
        string skillDigest,
        string providerRouteDigest,
        string memoryDigest,
        string compactionDigest,
        string dynamicRequestDigest)
    {
        LayoutVersion = RuntimeGuard.RequiredUtf8(
            layoutVersion,
            128,
            nameof(layoutVersion));
        StablePrefixDigest = RequiredDigest(
            stablePrefixDigest,
            nameof(stablePrefixDigest));
        ToolCatalogDigest = RequiredDigest(
            toolCatalogDigest,
            nameof(toolCatalogDigest));
        SkillDigest = RequiredDigest(skillDigest, nameof(skillDigest));
        ProviderRouteDigest = RequiredDigest(
            providerRouteDigest,
            nameof(providerRouteDigest));
        MemoryDigest = RequiredDigest(memoryDigest, nameof(memoryDigest));
        CompactionDigest = RequiredDigest(
            compactionDigest,
            nameof(compactionDigest));
        DynamicRequestDigest = RequiredDigest(
            dynamicRequestDigest,
            nameof(dynamicRequestDigest));
    }

    public string LayoutVersion { get; }

    public string StablePrefixDigest { get; }

    public string ToolCatalogDigest { get; }

    public string SkillDigest { get; }

    /// <summary>
    /// Digest of the planned primary route captured for this request.
    /// </summary>
    public string ProviderRouteDigest { get; }

    /// <summary>
    /// Dynamic-tail diagnostic. It does not invalidate the stable prefix.
    /// </summary>
    public string MemoryDigest { get; }

    /// <summary>
    /// Dynamic-tail diagnostic. It does not invalidate the stable prefix.
    /// </summary>
    public string CompactionDigest { get; }

    /// <summary>
    /// Dynamic-tail diagnostic. It does not invalidate the stable prefix.
    /// </summary>
    public string DynamicRequestDigest { get; }

    public JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("evidenceVersion", JsonArrayBuilder.String(EvidenceVersion)),
            ("layoutVersion", JsonArrayBuilder.String(LayoutVersion)),
            ("stablePrefixDigest",
                JsonArrayBuilder.String(StablePrefixDigest)),
            ("toolCatalogDigest",
                JsonArrayBuilder.String(ToolCatalogDigest)),
            ("skillDigest", JsonArrayBuilder.String(SkillDigest)),
            ("providerRouteDigest",
                JsonArrayBuilder.String(ProviderRouteDigest)),
            ("memoryDigest", JsonArrayBuilder.String(MemoryDigest)),
            ("compactionDigest",
                JsonArrayBuilder.String(CompactionDigest)),
            ("dynamicRequestDigest",
                JsonArrayBuilder.String(DynamicRequestDigest)));
    }

    public static ProviderCacheKey FromJson(JsonElement value)
    {
        try
        {
            ProviderCacheJson.RequireExactObject(
                value,
                "evidenceVersion",
                "layoutVersion",
                "stablePrefixDigest",
                "toolCatalogDigest",
                "skillDigest",
                "providerRouteDigest",
                "memoryDigest",
                "compactionDigest",
                "dynamicRequestDigest");
            ProviderCacheJson.RequireVersion(value, EvidenceVersion);
            return new ProviderCacheKey(
                ProviderCacheJson.RequiredString(value, "layoutVersion"),
                ProviderCacheJson.RequiredString(
                    value,
                    "stablePrefixDigest"),
                ProviderCacheJson.RequiredString(
                    value,
                    "toolCatalogDigest"),
                ProviderCacheJson.RequiredString(value, "skillDigest"),
                ProviderCacheJson.RequiredString(
                    value,
                    "providerRouteDigest"),
                ProviderCacheJson.RequiredString(value, "memoryDigest"),
                ProviderCacheJson.RequiredString(
                    value,
                    "compactionDigest"),
                ProviderCacheJson.RequiredString(
                    value,
                    "dynamicRequestDigest"));
        }
        catch (Exception exception)
            when (ProviderCacheJson.IsInvalidEvidenceException(exception))
        {
            throw ProviderCacheJson.Invalid(
                "Provider cache key evidence is invalid.");
        }
    }

    internal bool HasSameStablePrefix(ProviderCacheKey other)
    {
        return string.Equals(
                   LayoutVersion,
                   other.LayoutVersion,
                   StringComparison.Ordinal)
               && string.Equals(
                   StablePrefixDigest,
                   other.StablePrefixDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   ToolCatalogDigest,
                   other.ToolCatalogDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   SkillDigest,
                   other.SkillDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   ProviderRouteDigest,
                   other.ProviderRouteDigest,
                   StringComparison.Ordinal);
    }

    internal bool ContentEquals(ProviderCacheKey other)
    {
        return HasSameStablePrefix(other)
               && string.Equals(
                   MemoryDigest,
                   other.MemoryDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   CompactionDigest,
                   other.CompactionDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   DynamicRequestDigest,
                   other.DynamicRequestDigest,
                   StringComparison.Ordinal);
    }

    private static string RequiredDigest(
        string value,
        string parameterName)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new ArgumentException(
                "Provider cache evidence requires lowercase SHA-256 digests.",
                parameterName);
        }

        return value;
    }
}

public sealed class ProviderCacheDecision
{
    public const string EvidenceVersion = "provider-cache-decision.v1";

    internal ProviderCacheDecision(
        IReadOnlyList<string> breakReasons,
        IReadOnlyList<string>? dynamicTailChanges = null)
    {
        BreakReasons = Snapshot(
            breakReasons,
            nameof(breakReasons));
        DynamicTailChanges = Snapshot(
            dynamicTailChanges ?? Array.Empty<string>(),
            nameof(dynamicTailChanges));
    }

    /// <summary>
    /// True only when the stable logical prefix can be reused.
    /// Dynamic-tail changes do not affect this value.
    /// </summary>
    public bool PrefixReusable => BreakReasons.Count == 0;

    public IReadOnlyList<string> BreakReasons { get; }

    public IReadOnlyList<string> DynamicTailChanges { get; }

    public JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("evidenceVersion", JsonArrayBuilder.String(EvidenceVersion)),
            ("prefixReusable", JsonArrayBuilder.Boolean(PrefixReusable)),
            ("breakReasons", JsonArrayBuilder.Strings(BreakReasons)),
            ("dynamicTailChanges",
                JsonArrayBuilder.Strings(DynamicTailChanges)));
    }

    public static ProviderCacheDecision FromJson(JsonElement value)
    {
        try
        {
            ProviderCacheJson.RequireExactObject(
                value,
                "evidenceVersion",
                "prefixReusable",
                "breakReasons",
                "dynamicTailChanges");
            ProviderCacheJson.RequireVersion(value, EvidenceVersion);
            var prefixReusable = ProviderCacheJson.RequiredBoolean(
                value,
                "prefixReusable");
            var breakReasons = ProviderCacheJson.ReadOrderedCodes(
                value,
                "breakReasons",
                ProviderCacheJson.StableBreakReasonOrder);
            var dynamicTailChanges =
                ProviderCacheJson.ReadOrderedCodes(
                    value,
                    "dynamicTailChanges",
                    ProviderCacheJson.DynamicTailChangeOrder);
            var restored = new ProviderCacheDecision(
                breakReasons,
                dynamicTailChanges);
            if (restored.PrefixReusable != prefixReusable)
            {
                throw ProviderCacheJson.Invalid(
                    "Provider cache decision evidence is inconsistent.");
            }

            return restored;
        }
        catch (Exception exception)
            when (ProviderCacheJson.IsInvalidEvidenceException(exception))
        {
            throw ProviderCacheJson.Invalid(
                "Provider cache decision evidence is invalid.");
        }
    }

    /// <summary>
    /// Ensures restored decision evidence exactly matches a recomputation.
    /// </summary>
    public void ValidateAgainst(
        ProviderCacheKey? previous,
        ProviderCacheKey current,
        bool explicitlyInvalidated = false)
    {
        var expected = ProviderCacheTelemetry.Evaluate(
            previous,
            current,
            explicitlyInvalidated);
        if (!BreakReasons.SequenceEqual(
                expected.BreakReasons,
                StringComparer.Ordinal)
            || !DynamicTailChanges.SequenceEqual(
                expected.DynamicTailChanges,
                StringComparer.Ordinal))
        {
            throw ProviderCacheJson.Invalid(
                "Provider cache decision evidence does not match its keys.");
        }
    }

    private static IReadOnlyList<string> Snapshot(
        IReadOnlyList<string> values,
        string parameterName)
    {
        if (values is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        var count = values.Count;
        if (count > 16)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        var result = new string[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = values[index]
                            ?? throw new ArgumentException(
                                "Provider cache codes cannot be null.",
                                parameterName);
        }

        return new ReadOnlyCollection<string>(result);
    }
}

public sealed class ProviderCacheUsageEvidence
{
    public const string EvidenceVersion = "provider-cache-usage.v1";

    private ProviderCacheUsageEvidence(
        string state,
        int? readTokens,
        int? writeTokens,
        int? missTokens)
    {
        State = state;
        ReadTokens = readTokens;
        WriteTokens = writeTokens;
        MissTokens = missTokens;
        ValidateShape();
    }

    public string State { get; }

    public int? ReadTokens { get; }

    public int? WriteTokens { get; }

    public int? MissTokens { get; }

    public static ProviderCacheUsageEvidence FromUsage(ProviderUsage usage)
    {
        if (usage is null)
        {
            throw new ArgumentNullException(nameof(usage));
        }

        ValidateTokenCount(
            usage.CacheReadTokens,
            nameof(usage.CacheReadTokens));
        ValidateTokenCount(
            usage.CacheWriteTokens,
            nameof(usage.CacheWriteTokens));
        ValidateTokenCount(
            usage.CacheMissTokens,
            nameof(usage.CacheMissTokens));
        return new ProviderCacheUsageEvidence(
            DeriveState(
                usage.CacheReadTokens,
                usage.CacheWriteTokens,
                usage.CacheMissTokens),
            usage.CacheReadTokens,
            usage.CacheWriteTokens,
            usage.CacheMissTokens);
    }

    public JsonElement ToJson()
    {
        return JsonArrayBuilder.Object(
            ("evidenceVersion", JsonArrayBuilder.String(EvidenceVersion)),
            ("state", JsonArrayBuilder.String(State)),
            ("readTokens", NullableNumber(ReadTokens)),
            ("writeTokens", NullableNumber(WriteTokens)),
            ("missTokens", NullableNumber(MissTokens)));
    }

    public static ProviderCacheUsageEvidence FromJson(JsonElement value)
    {
        try
        {
            ProviderCacheJson.RequireExactObject(
                value,
                "evidenceVersion",
                "state",
                "readTokens",
                "writeTokens",
                "missTokens");
            ProviderCacheJson.RequireVersion(value, EvidenceVersion);
            return new ProviderCacheUsageEvidence(
                ProviderCacheJson.RequiredString(value, "state"),
                ProviderCacheJson.RequiredNullableInt32(
                    value,
                    "readTokens"),
                ProviderCacheJson.RequiredNullableInt32(
                    value,
                    "writeTokens"),
                ProviderCacheJson.RequiredNullableInt32(
                    value,
                    "missTokens"));
        }
        catch (Exception exception)
            when (ProviderCacheJson.IsInvalidEvidenceException(exception))
        {
            throw ProviderCacheJson.Invalid(
                "Provider cache usage evidence is invalid.");
        }
    }

    /// <summary>
    /// Ensures restored evidence describes the supplied provider sample.
    /// </summary>
    public void ValidateAgainst(ProviderUsage usage)
    {
        var expected = FromUsage(
            usage ?? throw new ArgumentNullException(nameof(usage)));
        if (!string.Equals(State, expected.State, StringComparison.Ordinal)
            || ReadTokens != expected.ReadTokens
            || WriteTokens != expected.WriteTokens
            || MissTokens != expected.MissTokens)
        {
            throw ProviderCacheJson.Invalid(
                "Provider cache usage evidence does not match provider "
                + "usage.");
        }
    }

    private void ValidateShape()
    {
        ValidateTokenCount(ReadTokens, nameof(ReadTokens));
        ValidateTokenCount(WriteTokens, nameof(WriteTokens));
        ValidateTokenCount(MissTokens, nameof(MissTokens));
        if (!string.Equals(
                State,
                DeriveState(ReadTokens, WriteTokens, MissTokens),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Provider cache usage state does not match its token "
                + "counts.",
                nameof(State));
        }
    }

    private static string DeriveState(
        int? readTokens,
        int? writeTokens,
        int? missTokens)
    {
        if (readTokens > 0)
        {
            return ProviderCacheUsageStates.Hit;
        }

        if (writeTokens > 0)
        {
            return ProviderCacheUsageStates.Write;
        }

        if (missTokens > 0)
        {
            return ProviderCacheUsageStates.Miss;
        }

        return readTokens.HasValue
               && writeTokens.HasValue
               && missTokens.HasValue
            ? ProviderCacheUsageStates.NoActivity
            : ProviderCacheUsageStates.Unknown;
    }

    private static JsonElement NullableNumber(int? value)
    {
        return value.HasValue
            ? JsonArrayBuilder.Number(value.Value)
            : JsonArrayBuilder.Null();
    }

    private static void ValidateTokenCount(
        int? value,
        string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public static class ProviderCacheTelemetry
{
    public const string KeyExtensionName = "providerCacheKey";

    public const string DecisionExtensionName = "providerCacheDecision";

    public const string UsageExtensionName = "providerCacheUsage";

    public static ProviderCacheDecision Evaluate(
        ProviderCacheKey? previous,
        ProviderCacheKey current,
        bool explicitlyInvalidated = false)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        var breakReasons = new List<string>(7);
        var dynamicTailChanges = new List<string>(3);
        if (previous is null)
        {
            breakReasons.Add(ProviderCacheBreakReasonCodes.ColdStart);
        }
        else
        {
            AddIfChanged(
                previous.LayoutVersion,
                current.LayoutVersion,
                ProviderCacheBreakReasonCodes.LayoutChanged,
                breakReasons);
            AddIfChanged(
                previous.StablePrefixDigest,
                current.StablePrefixDigest,
                ProviderCacheBreakReasonCodes.StablePrefixChanged,
                breakReasons);
            AddIfChanged(
                previous.ToolCatalogDigest,
                current.ToolCatalogDigest,
                ProviderCacheBreakReasonCodes.ToolCatalogChanged,
                breakReasons);
            AddIfChanged(
                previous.SkillDigest,
                current.SkillDigest,
                ProviderCacheBreakReasonCodes.SkillChanged,
                breakReasons);
            AddIfChanged(
                previous.ProviderRouteDigest,
                current.ProviderRouteDigest,
                ProviderCacheBreakReasonCodes.ProviderRouteChanged,
                breakReasons);
            AddIfChanged(
                previous.MemoryDigest,
                current.MemoryDigest,
                ProviderCacheDynamicTailChangeCodes.MemoryChanged,
                dynamicTailChanges);
            AddIfChanged(
                previous.CompactionDigest,
                current.CompactionDigest,
                ProviderCacheDynamicTailChangeCodes.CompactionChanged,
                dynamicTailChanges);
            AddIfChanged(
                previous.DynamicRequestDigest,
                current.DynamicRequestDigest,
                ProviderCacheDynamicTailChangeCodes.DynamicRequestChanged,
                dynamicTailChanges);
        }

        if (explicitlyInvalidated)
        {
            breakReasons.Add(
                ProviderCacheBreakReasonCodes.ExplicitInvalidation);
        }

        return new ProviderCacheDecision(
            breakReasons,
            dynamicTailChanges);
    }

    /// <summary>
    /// Restores and verifies decision evidence against the durable keys.
    /// </summary>
    public static ProviderCacheDecision RestoreDecision(
        JsonElement evidence,
        ProviderCacheKey? previous,
        ProviderCacheKey current,
        bool explicitlyInvalidated = false)
    {
        var restored = ProviderCacheDecision.FromJson(evidence);
        restored.ValidateAgainst(
            previous,
            current,
            explicitlyInvalidated);
        return restored;
    }

    private static void AddIfChanged(
        string left,
        string right,
        string reason,
        ICollection<string> destination)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal))
        {
            destination.Add(reason);
        }
    }
}

internal static class ProviderCacheJson
{
    private static readonly JsonValueLimits EvidenceLimits = new(
        maxUtf8Bytes: 8_192,
        maxDepth: 4,
        maxNodes: 64,
        maxStringUtf8Bytes: 256,
        maxContainerItems: 16);

    internal static readonly IReadOnlyDictionary<string, int>
        StableBreakReasonOrder =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [ProviderCacheBreakReasonCodes.ColdStart] = 0,
                [ProviderCacheBreakReasonCodes.LayoutChanged] = 1,
                [ProviderCacheBreakReasonCodes.StablePrefixChanged] = 2,
                [ProviderCacheBreakReasonCodes.ToolCatalogChanged] = 3,
                [ProviderCacheBreakReasonCodes.SkillChanged] = 4,
                [ProviderCacheBreakReasonCodes.ProviderRouteChanged] = 5,
                [ProviderCacheBreakReasonCodes.ExplicitInvalidation] = 6
            };

    internal static readonly IReadOnlyDictionary<string, int>
        DynamicTailChangeOrder =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [ProviderCacheDynamicTailChangeCodes.MemoryChanged] = 0,
                [ProviderCacheDynamicTailChangeCodes.CompactionChanged] = 1,
                [ProviderCacheDynamicTailChangeCodes.DynamicRequestChanged] =
                    2
            };

    internal static void RequireExactObject(
        JsonElement value,
        params string[] names)
    {
        JsonValueInspector.ValidateAndMeasure(
            value,
            EvidenceLimits,
            nameof(value));
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Provider cache evidence must be an object.");
        }

        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !seen.Add(property.Name))
            {
                throw Invalid(
                    "Provider cache evidence contains an unknown or "
                    + "duplicate field.");
            }
        }

        if (seen.Count != allowed.Count)
        {
            throw Invalid(
                "Provider cache evidence is missing a required field.");
        }
    }

    internal static void RequireVersion(
        JsonElement value,
        string expected)
    {
        if (!string.Equals(
                RequiredString(value, "evidenceVersion"),
                expected,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "Provider cache evidence has an unsupported version.");
        }
    }

    internal static string RequiredString(
        JsonElement value,
        string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } result)
        {
            throw Invalid(
                "Provider cache evidence contains an invalid string.");
        }

        return result;
    }

    internal static bool RequiredBoolean(
        JsonElement value,
        string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(
                "Provider cache evidence contains an invalid boolean.");
        }

        return property.GetBoolean();
    }

    internal static int? RequiredNullableInt32(
        JsonElement value,
        string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            throw Invalid(
                "Provider cache evidence is missing a token count.");
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var result)
            || result < 0)
        {
            throw Invalid(
                "Provider cache evidence contains an invalid token count.");
        }

        return result;
    }

    internal static IReadOnlyList<string> ReadOrderedCodes(
        JsonElement value,
        string propertyName,
        IReadOnlyDictionary<string, int> order)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array
            || property.GetArrayLength() > order.Count)
        {
            throw Invalid(
                "Provider cache evidence contains an invalid code list.");
        }

        var result = new string[property.GetArrayLength()];
        var previousRank = -1;
        var index = 0;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not { } code
                || !order.TryGetValue(code, out var rank)
                || rank <= previousRank)
            {
                throw Invalid(
                    "Provider cache evidence contains an invalid, "
                    + "duplicate, or unordered code.");
            }

            previousRank = rank;
            result[index++] = code;
        }

        return new ReadOnlyCollection<string>(result);
    }

    internal static bool IsInvalidEvidenceException(Exception exception)
    {
        return exception is InvalidDataException
            or ArgumentException
            or InvalidOperationException
            or OverflowException;
    }

    internal static InvalidDataException Invalid(string message)
    {
        return new InvalidDataException(message);
    }
}

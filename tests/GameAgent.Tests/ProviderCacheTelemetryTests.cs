using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class ProviderCacheTelemetryTests
{
    [Fact]
    public void ExtensionNamesAreStablePublicContracts()
    {
        Assert.Equal(
            "providerCacheKey",
            ProviderCacheTelemetry.KeyExtensionName);
        Assert.Equal(
            "providerCacheDecision",
            ProviderCacheTelemetry.DecisionExtensionName);
        Assert.Equal(
            "providerCacheUsage",
            ProviderCacheTelemetry.UsageExtensionName);
    }

    [Fact]
    public void ColdStartAndExplicitInvalidationAreTyped()
    {
        var decision = ProviderCacheTelemetry.Evaluate(
            previous: null,
            Key("a"),
            explicitlyInvalidated: true);

        Assert.False(decision.PrefixReusable);
        Assert.Equal(
            new[]
            {
                ProviderCacheBreakReasonCodes.ColdStart,
                ProviderCacheBreakReasonCodes.ExplicitInvalidation
            },
            decision.BreakReasons);
        Assert.Empty(decision.DynamicTailChanges);
    }

    [Fact]
    public void EveryDynamicTailChangePreservesStablePrefixReuse()
    {
        var previous = Key("a");
        var dynamicOnly = new ProviderCacheKey(
            previous.LayoutVersion,
            previous.StablePrefixDigest,
            previous.ToolCatalogDigest,
            previous.SkillDigest,
            previous.ProviderRouteDigest,
            Digest("memory-b"),
            Digest("compaction-b"),
            Digest("dynamic-b"));

        var decision = ProviderCacheTelemetry.Evaluate(
            previous,
            dynamicOnly);

        Assert.True(decision.PrefixReusable);
        Assert.Empty(decision.BreakReasons);
        Assert.Equal(
            new[]
            {
                ProviderCacheDynamicTailChangeCodes.MemoryChanged,
                ProviderCacheDynamicTailChangeCodes.CompactionChanged,
                ProviderCacheDynamicTailChangeCodes.DynamicRequestChanged
            },
            decision.DynamicTailChanges);
    }

    [Fact]
    public void EveryStablePrefixFactorHasAStableBreakReason()
    {
        var previous = Key("a");
        var current = new ProviderCacheKey(
            "layout-b",
            Digest("prefix-b"),
            Digest("tools-b"),
            Digest("skills-b"),
            Digest("route-b"),
            previous.MemoryDigest,
            previous.CompactionDigest,
            previous.DynamicRequestDigest);

        var decision = ProviderCacheTelemetry.Evaluate(previous, current);

        Assert.False(decision.PrefixReusable);
        Assert.Equal(
            new[]
            {
                ProviderCacheBreakReasonCodes.LayoutChanged,
                ProviderCacheBreakReasonCodes.StablePrefixChanged,
                ProviderCacheBreakReasonCodes.ToolCatalogChanged,
                ProviderCacheBreakReasonCodes.SkillChanged,
                ProviderCacheBreakReasonCodes.ProviderRouteChanged
            },
            decision.BreakReasons);
        Assert.Empty(decision.DynamicTailChanges);
    }

    [Fact]
    public void UsageKeepsUnknownDistinctFromExplicitZero()
    {
        var unknown = ProviderCacheUsageEvidence.FromUsage(
            new ProviderUsage());
        var partialZero = ProviderCacheUsageEvidence.FromUsage(
            new ProviderUsage
            {
                CacheReadTokens = 0
            });
        var explicitZero = ProviderCacheUsageEvidence.FromUsage(
            new ProviderUsage
            {
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                CacheMissTokens = 0
            });

        Assert.Equal(ProviderCacheUsageStates.Unknown, unknown.State);
        Assert.Null(unknown.ReadTokens);
        Assert.Equal(
            ProviderCacheUsageStates.Unknown,
            partialZero.State);
        Assert.Equal(0, partialZero.ReadTokens);
        Assert.Equal(
            ProviderCacheUsageStates.NoActivity,
            explicitZero.State);
        Assert.Equal(0, explicitZero.ReadTokens);
        Assert.Equal(0, explicitZero.WriteTokens);
        Assert.Equal(0, explicitZero.MissTokens);
    }

    [Fact]
    public void UsageClassificationIsDeterministicForMixedCounters()
    {
        var hit = ProviderCacheUsageEvidence.FromUsage(
            new ProviderUsage
            {
                CacheReadTokens = 3,
                CacheWriteTokens = 2,
                CacheMissTokens = 1
            });
        var write = ProviderCacheUsageEvidence.FromUsage(
            new ProviderUsage
            {
                CacheReadTokens = 0,
                CacheWriteTokens = 2,
                CacheMissTokens = 1
            });
        var miss = ProviderCacheUsageEvidence.FromUsage(
            new ProviderUsage
            {
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                CacheMissTokens = 1
            });

        Assert.Equal(ProviderCacheUsageStates.Hit, hit.State);
        Assert.Equal(ProviderCacheUsageStates.Write, write.State);
        Assert.Equal(ProviderCacheUsageStates.Miss, miss.State);
    }

    [Fact]
    public void CacheKeyJsonRoundTripsAndRejectsNonExactEvidence()
    {
        var key = Key("a");
        var restored = ProviderCacheKey.FromJson(key.ToJson());

        Assert.True(key.ContentEquals(restored));
        var missing = JsonArrayBuilder.Object(
            ("evidenceVersion",
                JsonArrayBuilder.String(ProviderCacheKey.EvidenceVersion)),
            ("layoutVersion", JsonArrayBuilder.String("layout-a")));
        Assert.Throws<InvalidDataException>(
            () => ProviderCacheKey.FromJson(missing));

        var unknownVersion = ReplaceStringProperty(
            key.ToJson(),
            "evidenceVersion",
            "provider-cache-key.v2");
        Assert.Throws<InvalidDataException>(
            () => ProviderCacheKey.FromJson(unknownVersion));

        var duplicate = Parse(
            "{\"evidenceVersion\":\"provider-cache-key.v1\","
            + "\"evidenceVersion\":\"provider-cache-key.v1\","
            + "\"layoutVersion\":\"layout-a\","
            + $"\"stablePrefixDigest\":\"{Digest("prefix-a")}\","
            + $"\"toolCatalogDigest\":\"{Digest("tools-a")}\","
            + $"\"skillDigest\":\"{Digest("skills-a")}\","
            + $"\"providerRouteDigest\":\"{Digest("route-a")}\","
            + $"\"memoryDigest\":\"{Digest("memory-a")}\","
            + $"\"compactionDigest\":\"{Digest("compaction-a")}\","
            + $"\"dynamicRequestDigest\":\"{Digest("dynamic-a")}\""
            + "}");
        Assert.Throws<InvalidDataException>(
            () => ProviderCacheKey.FromJson(duplicate));
    }

    [Fact]
    public void DecisionJsonRoundTripsAndRejectsInconsistentClaim()
    {
        var previous = Key("a");
        var current = new ProviderCacheKey(
            previous.LayoutVersion,
            previous.StablePrefixDigest,
            previous.ToolCatalogDigest,
            previous.SkillDigest,
            previous.ProviderRouteDigest,
            Digest("memory-b"),
            previous.CompactionDigest,
            Digest("dynamic-b"));
        var decision = ProviderCacheTelemetry.Evaluate(previous, current);

        var restored = ProviderCacheDecision.FromJson(decision.ToJson());

        Assert.True(restored.PrefixReusable);
        Assert.Equal(
            decision.DynamicTailChanges,
            restored.DynamicTailChanges);
        restored.ValidateAgainst(previous, current);

        var inconsistent = JsonArrayBuilder.Object(
            ("evidenceVersion",
                JsonArrayBuilder.String(
                    ProviderCacheDecision.EvidenceVersion)),
            ("prefixReusable", JsonArrayBuilder.Boolean(false)),
            ("breakReasons", JsonArrayBuilder.Strings(
                Array.Empty<string>())),
            ("dynamicTailChanges", JsonArrayBuilder.Strings(
                decision.DynamicTailChanges)));
        Assert.Throws<InvalidDataException>(
            () => ProviderCacheDecision.FromJson(inconsistent));
    }

    [Fact]
    public void DecisionRestorationRejectsWrongOrUnorderedReasons()
    {
        var previous = Key("a");
        var current = new ProviderCacheKey(
            "layout-b",
            previous.StablePrefixDigest,
            previous.ToolCatalogDigest,
            previous.SkillDigest,
            previous.ProviderRouteDigest,
            previous.MemoryDigest,
            previous.CompactionDigest,
            previous.DynamicRequestDigest);
        var evidence = ProviderCacheTelemetry
            .Evaluate(previous, current)
            .ToJson();

        var restored = ProviderCacheTelemetry.RestoreDecision(
            evidence,
            previous,
            current);
        Assert.Equal(
            new[] { ProviderCacheBreakReasonCodes.LayoutChanged },
            restored.BreakReasons);
        Assert.Throws<InvalidDataException>(
            () => ProviderCacheTelemetry.RestoreDecision(
                evidence,
                previous,
                previous));

        var unordered = JsonArrayBuilder.Object(
            ("evidenceVersion",
                JsonArrayBuilder.String(
                    ProviderCacheDecision.EvidenceVersion)),
            ("prefixReusable", JsonArrayBuilder.Boolean(false)),
            ("breakReasons", JsonArrayBuilder.Strings(
                new[]
                {
                    ProviderCacheBreakReasonCodes.ProviderRouteChanged,
                    ProviderCacheBreakReasonCodes.LayoutChanged
                })),
            ("dynamicTailChanges", JsonArrayBuilder.Strings(
                Array.Empty<string>())));
        Assert.Throws<InvalidDataException>(
            () => ProviderCacheDecision.FromJson(unordered));
    }

    [Fact]
    public void UsageJsonRoundTripsAndValidatesAgainstProviderUsage()
    {
        var usage = new ProviderUsage
        {
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CacheMissTokens = 12
        };
        var evidence = ProviderCacheUsageEvidence.FromUsage(usage);

        var restored = ProviderCacheUsageEvidence.FromJson(
            evidence.ToJson());

        Assert.Equal(ProviderCacheUsageStates.Miss, restored.State);
        Assert.Equal(12, restored.MissTokens);
        restored.ValidateAgainst(usage);
        Assert.Throws<InvalidDataException>(
            () => restored.ValidateAgainst(
                new ProviderUsage
                {
                    CacheReadTokens = 12,
                    CacheWriteTokens = 0,
                    CacheMissTokens = 0
                }));
    }

    [Fact]
    public void UsageRestorationRejectsStateCountMismatchAndNegativeCount()
    {
        var mismatched = UsageJson(
            ProviderCacheUsageStates.Hit,
            readTokens: 0,
            writeTokens: 0,
            missTokens: 0);
        var negative = UsageJson(
            ProviderCacheUsageStates.Miss,
            readTokens: 0,
            writeTokens: 0,
            missTokens: -1);

        Assert.Throws<InvalidDataException>(
            () => ProviderCacheUsageEvidence.FromJson(mismatched));
        Assert.Throws<InvalidDataException>(
            () => ProviderCacheUsageEvidence.FromJson(negative));
    }

    private static JsonElement UsageJson(
        string state,
        int? readTokens,
        int? writeTokens,
        int? missTokens)
    {
        return JsonArrayBuilder.Object(
            ("evidenceVersion",
                JsonArrayBuilder.String(
                    ProviderCacheUsageEvidence.EvidenceVersion)),
            ("state", JsonArrayBuilder.String(state)),
            ("readTokens", NullableNumber(readTokens)),
            ("writeTokens", NullableNumber(writeTokens)),
            ("missTokens", NullableNumber(missTokens)));
    }

    private static JsonElement NullableNumber(int? value)
    {
        return value.HasValue
            ? JsonArrayBuilder.Number(value.Value)
            : JsonArrayBuilder.Null();
    }

    private static JsonElement ReplaceStringProperty(
        JsonElement source,
        string propertyName,
        string replacement)
    {
        var properties = source.EnumerateObject()
            .Select(property => (
                property.Name,
                string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.Ordinal)
                    ? JsonArrayBuilder.String(replacement)
                    : property.Value.Clone()))
            .ToArray();
        return JsonArrayBuilder.Object(properties);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ProviderCacheKey Key(string suffix)
    {
        return new ProviderCacheKey(
            "layout-" + suffix,
            Digest("prefix-" + suffix),
            Digest("tools-" + suffix),
            Digest("skills-" + suffix),
            Digest("route-" + suffix),
            Digest("memory-" + suffix),
            Digest("compaction-" + suffix),
            Digest("dynamic-" + suffix));
    }

    private static string Digest(string value)
    {
        return System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value))
            .Aggregate(
                new System.Text.StringBuilder(64),
                (builder, item) => builder.Append(item.ToString("x2")))
            .ToString();
    }
}

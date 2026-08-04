using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RuntimeRegistryTests
{
    [Fact]
    public void ToolSnapshotsAreCanonicalImmutableAndAdvanceOnlyWhenContentChanges()
    {
        var registry = new ToolCatalogRegistry();
        var firstDescriptor = CreateTool(
            "world.lookup",
            """
            {
              "type": "object",
              "properties": {
                "zone": { "type": "string" },
                "limit": { "type": "integer" }
              }
            }
            """);
        firstDescriptor.ConflictScopes.AddRange(new[] { "zone", "agent" });
        firstDescriptor.Extensions["metadata"] = Json("""{"b":2,"a":1}""");

        var first = registry.Replace(new[] { firstDescriptor });
        var capturedForTurn = first;

        var equivalent = CreateTool(
            "world.lookup",
            """
            {
              "properties": {
                "limit": { "type": "integer" },
                "zone": { "type": "string" }
              },
              "type": "object"
            }
            """);
        equivalent.ConflictScopes.AddRange(new[] { "agent", "zone" });
        equivalent.Extensions["metadata"] = Json("""{"a":1,"b":2}""");

        var unchanged = registry.Replace(new[] { equivalent });

        Assert.Same(first, unchanged);
        Assert.Equal(1, unchanged.Generation);
        Assert.Equal(first.Digest, unchanged.Digest);

        firstDescriptor.Name = "mutated.after.replace";
        firstDescriptor.ConflictScopes.Clear();
        firstDescriptor.Extensions["metadata"] = Json("""{"changed":true}""");
        Assert.Equal("world.lookup", capturedForTurn.Tools[0].Name);
        Assert.Equal(new[] { "agent", "zone" }, capturedForTurn.Tools[0].ConflictScopes);
        Assert.Equal(1, capturedForTurn.Tools[0].Extensions["metadata"].GetProperty("a").GetInt32());

        equivalent.Description = "A changed description.";
        var nextTurn = registry.Replace(new[] { equivalent });

        Assert.Equal(2, nextTurn.Generation);
        Assert.NotEqual(capturedForTurn.Digest, nextTurn.Digest);
        Assert.Equal("Reads a world value.", capturedForTurn.Tools[0].Description);
        Assert.Equal("A changed description.", nextTurn.Tools[0].Description);
    }

    [Fact]
    public void SkillDisclosureKeepsActivatedContentAndDefersOnlyUndisclosedCatalogEntries()
    {
        var registry = new SkillCatalogRegistry();
        var snapshot = registry.Replace(
            new[]
            {
                CreateSkill("gamma", "Gamma prompt."),
                CreateSkill("beta", "Beta prompt."),
                CreateSkill("alpha", "Alpha prompt.")
            });

        var disclosure = snapshot.CreateDisclosure(
            new[] { new SkillReference("beta", "1.0.0") },
            new SkillDisclosureBudget(
                maxCatalogItems: 1,
                maxCatalogUtf8Bytes: 10_000,
                maxActivatedSkills: 1,
                maxPromptFragments: 1,
                maxPromptUtf8Bytes: 1_000,
                maxReferences: 8));

        Assert.Equal("beta", Assert.Single(disclosure.Activated).SkillId);
        Assert.Equal("Beta prompt.", disclosure.Activated[0].PromptFragments[0]);
        Assert.Equal("alpha", Assert.Single(disclosure.Catalog).SkillId);
        Assert.Equal(new[] { "gamma@1.0.0" }, disclosure.DeferredReferences);
        Assert.True(disclosure.EstimatedUtf8Bytes > 0);

        var exception = Assert.Throws<RuntimeContentLimitException>(
            () => snapshot.CreateDisclosure(
                new[] { new SkillReference("beta", "1.0.0") },
                new SkillDisclosureBudget(
                    maxCatalogItems: 0,
                    maxCatalogUtf8Bytes: 0,
                    maxActivatedSkills: 1,
                    maxPromptFragments: 0,
                    maxPromptUtf8Bytes: 1_000,
                    maxReferences: 8)));
        Assert.Equal("skill_prompt_fragment_count_exceeded", exception.LimitCode);
    }

    [Fact]
    public void RegistryRejectsJsonBeyondConfiguredDepth()
    {
        var registry = new ToolCatalogRegistry(
            new RegistryLimits(
                jsonLimits: new JsonValueLimits(
                    maxUtf8Bytes: 4_096,
                    maxDepth: 2,
                    maxNodes: 64,
                    maxStringUtf8Bytes: 1_024,
                    maxContainerItems: 32)));
        var descriptor = CreateTool(
            "too.deep",
            """{"type":"object","properties":{"value":{"type":"string"}}}""");

        var exception = Assert.Throws<RuntimeContentLimitException>(
            () => registry.Replace(new[] { descriptor }));

        Assert.Equal("json_depth_exceeded", exception.LimitCode);
    }

    [Fact]
    public void CanonicalDigestFramesJsonFieldsWithoutLegacyBoundaryCollisions()
    {
        const string shortName = "extension:bbbbbbbbbbbbb";
        var longName = shortName + "\"" + new string('x', 98) + "\\";
        var firstFields = new[]
        {
            ("a", Json("1")),
            (longName, Json("\"\""))
        };
        var secondFields = new[]
        {
            ("a", Json("11")),
            (shortName, Json("\"" + new string('x', 98) + "\\\"\""))
        };

        Assert.Equal(
            LegacyJsonPreimage(firstFields),
            LegacyJsonPreimage(secondFields));

        var first = new CanonicalDigestBuilder();
        foreach (var field in firstFields)
        {
            first.Add(field.Item1, field.Item2);
        }

        var second = new CanonicalDigestBuilder();
        foreach (var field in secondFields)
        {
            second.Add(field.Item1, field.Item2);
        }

        Assert.NotEqual(first.Finish(), second.Finish());
    }

    [Fact]
    public void CanonicalDigestFramesFieldTypesAndNullPresence()
    {
        var text = new CanonicalDigestBuilder();
        text.Add("value", string.Empty);
        var list = new CanonicalDigestBuilder();
        list.Add("value", Array.Empty<string>());
        var absent = new CanonicalDigestBuilder();
        absent.Add("value", (string?)null);
        var integer = new CanonicalDigestBuilder();
        integer.Add("value", 0L);
        var json = new CanonicalDigestBuilder();
        json.Add("value", Json("\"\""));

        Assert.NotEqual(text.Finish(), list.Finish());
        Assert.NotEqual(text.Finish(), absent.Finish());
        Assert.NotEqual(list.Finish(), absent.Finish());
        Assert.Equal(
            5,
            new[]
            {
                text.Finish(),
                list.Finish(),
                absent.Finish(),
                integer.Finish(),
                json.Finish()
            }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SkillContentDigestsSeparateFormerJsonBoundaryCollision()
    {
        const string shortKey = "bbbbbbbbbbbbb";
        var longKey = shortKey + "\"" + new string('x', 98) + "\\";
        var firstManifest = CreateSkill("collision-proof", "Prompt.");
        firstManifest.Extensions["a"] = Json("1");
        firstManifest.Extensions[longKey] = Json("\"\"");
        var secondManifest = CreateSkill("collision-proof", "Prompt.");
        secondManifest.Extensions["a"] = Json("11");
        secondManifest.Extensions[shortKey] = Json(
            "\"" + new string('x', 98) + "\\\"\"");

        var first = new SkillCatalogRegistry()
            .Replace(new[] { firstManifest })
            .Skills[0];
        var second = new SkillCatalogRegistry()
            .Replace(new[] { secondManifest })
            .Skills[0];

        Assert.NotEqual(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public void RegistriesRejectIllFormedUnicodeBeforeComputingIdentity()
    {
        var firstTool = CreateTool(
            "invalid-unicode-tool",
            """{"type":"object"}""");
        firstTool.Description = "invalid-" + '\ud800';
        var secondTool = CreateTool(
            "invalid-unicode-tool",
            """{"type":"object"}""");
        secondTool.Description = "invalid-" + '\ud801';
        var firstSkill = CreateSkill("invalid-unicode-skill", "Prompt.");
        firstSkill.Description = "invalid-" + '\ud800';
        var secondSkill = CreateSkill("invalid-unicode-skill", "Prompt.");
        secondSkill.Description = "invalid-" + '\ud801';

        Assert.Throws<JsonException>(
            () => new ToolCatalogRegistry().Replace(new[] { firstTool }));
        Assert.Throws<JsonException>(
            () => new ToolCatalogRegistry().Replace(new[] { secondTool }));
        Assert.Throws<ArgumentException>(
            () => new SkillCatalogRegistry().Replace(new[] { firstSkill }));
        Assert.Throws<ArgumentException>(
            () => new SkillCatalogRegistry().Replace(new[] { secondSkill }));
        Assert.Throws<ArgumentException>(
            () => RuntimeGuard.RequiredUtf8(
                "\ud800",
                16,
                "value"));
        Assert.Throws<ArgumentException>(
            () => RuntimeGuard.RequiredUtf8(
                "\ud801",
                16,
                "value"));

        var firstDigest = new CanonicalDigestBuilder();
        var secondDigest = new CanonicalDigestBuilder();
        Assert.Throws<EncoderFallbackException>(
            () => firstDigest.Add("value", "\ud800"));
        Assert.Throws<EncoderFallbackException>(
            () => secondDigest.Add("value", "\ud801"));
    }

    [Theory]
    [InlineData(ToolDisclosureControlNames.Search)]
    [InlineData(ToolDisclosureControlNames.Activate)]
    public void RegistryRejectsReservedDisclosureControlNames(string name)
    {
        var registry = new ToolCatalogRegistry();

        var exception = Assert.Throws<ArgumentException>(
            () => registry.Replace(
                new[]
                {
                    CreateTool(
                        name,
                        """{"type":"object","additionalProperties":false}""")
                }));

        Assert.Contains("reserved", exception.Message);
        Assert.Empty(registry.Current.Tools);
    }

    private static ToolDescriptor CreateTool(string name, string parametersSchema)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = "1.0.0",
            Description = "Reads a world value.",
            ParametersSchema = Json(parametersSchema),
            Effect = ToolEffects.PureRead,
            ThreadAffinity = ThreadAffinities.AnyThread,
            TimeoutMs = 2_000,
            RetryPolicy = "never",
            IdempotencyPolicy = "none",
            Toolset = "world",
            Visibility = "direct"
        };
    }

    private static SkillManifest CreateSkill(string id, string prompt)
    {
        return new SkillManifest
        {
            SkillId = id,
            Version = "1.0.0",
            Digest = $"declared:{id}",
            Description = $"{id} description",
            PromptFragments = new List<string> { prompt },
            RequiredToolRefs = new List<string> { "world.lookup@1.0.0" },
            CapabilityRequirements = Json("""{"engine":"any"}"""),
            ActivationPolicy = Json("""{"mode":"explicit"}"""),
            Trust = "trusted"
        };
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string LegacyJsonPreimage(
        IEnumerable<(string Name, JsonElement Value)> fields)
    {
        var output = new StringBuilder();
        foreach (var field in fields)
        {
            output.Append(Encoding.UTF8.GetByteCount(field.Name));
            output.Append(':');
            output.Append(field.Name);
            CanonicalJsonDigest.AppendCanonical(output, field.Value);
        }

        return output.ToString();
    }
}

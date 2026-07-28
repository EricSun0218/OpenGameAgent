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
}

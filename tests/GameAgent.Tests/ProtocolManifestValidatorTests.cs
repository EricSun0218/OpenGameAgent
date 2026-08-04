using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class ProtocolManifestValidatorTests
{
    [Fact]
    public void AgentDefinitionAcceptsWireBoundariesAndRejectsOverflow()
    {
        var value = ValidAgentDefinition();
        value.Toolsets = Strings(
            "toolset",
            ProtocolLimits.MaxAgentDefinitionReferences);
        value.Skills = Strings(
            "skill",
            ProtocolLimits.MaxAgentDefinitionReferences);
        value.BehaviorPolicyRef = new string('b', 256);
        value.ContextPolicyRef = new string('c', 256);
        value.MemoryPolicyRef = new string('m', 256);
        value.ProviderPolicyRef = new string('p', 256);
        value.Extensions = Extensions(
            ProtocolLimits.MaxProtocolExtensions);

        Assert.Empty(ProtocolValidator.Validate(value));
        ProtocolValidator.EnsureValid(value);

        value.AgentDefinitionId = string.Empty;
        value.Version = new string('v', 33);
        value.Identity = default;
        value.BehaviorPolicyRef += "x";
        value.Toolsets.Add("toolset-overflow");
        value.Toolsets[1] = value.Toolsets[0];
        value.Skills.Add("skill-overflow");
        value.Skills[1] = value.Skills[0];
        value.Budgets = Json("[]");
        value.Extensions.Add(
            "overflow",
            Json("null"));

        var errors = ProtocolValidator.Validate(value);
        AssertError(errors, "$.agentDefinitionId", "required");
        AssertError(errors, "$.version", "out_of_range");
        AssertError(errors, "$.identity", "required");
        AssertError(errors, "$.behaviorPolicyRef", "out_of_range");
        AssertError(errors, "$.toolsets", "out_of_range");
        AssertError(errors, "$.toolsets[1]", "duplicate_value");
        AssertError(errors, "$.skills", "out_of_range");
        AssertError(errors, "$.skills[1]", "duplicate_value");
        AssertError(errors, "$.budgets", "invalid_type");
        AssertError(errors, "$.extensions", "out_of_range");
        Assert.Throws<JsonException>(
            () => ProtocolValidator.EnsureValid(value));
    }

    [Fact]
    public void SkillManifestAcceptsWireBoundariesAndRejectsOverflow()
    {
        var value = ValidSkillManifest();
        value.Version = new string('v', 32);
        value.Digest = new string('d', 256);
        value.Description = new string('x', 2_048);
        value.PromptFragments = Strings(
            "prompt",
            ProtocolLimits.MaxSkillManifestListItems);
        value.RequiredToolRefs = Strings(
            "required",
            ProtocolLimits.MaxSkillManifestListItems);
        value.OptionalToolRefs = Strings(
            "optional",
            ProtocolLimits.MaxSkillManifestListItems);
        value.ContextProviderRefs = Strings(
            "context",
            ProtocolLimits.MaxSkillManifestListItems);
        value.ResourceRefs = Resources(
            ProtocolLimits.MaxSkillManifestListItems);
        value.Extensions = Extensions(
            ProtocolLimits.MaxProtocolExtensions);

        Assert.Empty(ProtocolValidator.Validate(value));
        ProtocolValidator.EnsureValid(value);

        value.SkillId = string.Empty;
        value.Version += "v";
        value.Digest += "d";
        value.Description += "x";
        value.PromptFragments.Add("prompt-overflow");
        value.PromptFragments[0] = new string(
            'p',
            ProtocolLimits.MaxSkillPromptFragmentUnicodeScalars + 1);
        value.RequiredToolRefs.Add("required-overflow");
        value.RequiredToolRefs[1] = value.RequiredToolRefs[0];
        value.OptionalToolRefs.Add("optional-overflow");
        value.ContextProviderRefs.Add("context-overflow");
        value.ResourceRefs.Add(Resource("overflow"));
        value.ResourceRefs[1].Uri = value.ResourceRefs[0].Uri;
        value.ResourceRefs[2].Uri = "not valid uri";
        value.ResourceRefs[3].Digest = string.Empty;
        value.CapabilityRequirements = Json("[]");
        value.Trust = "implicit";
        value.ActivationPolicy = default;
        value.Extensions.Add("overflow", Json("null"));

        var errors = ProtocolValidator.Validate(value);
        AssertError(errors, "$.skillId", "required");
        AssertError(errors, "$.version", "out_of_range");
        AssertError(errors, "$.digest", "out_of_range");
        AssertError(errors, "$.description", "out_of_range");
        AssertError(errors, "$.promptFragments", "out_of_range");
        AssertError(
            errors,
            "$.promptFragments[0]",
            "out_of_range");
        AssertError(errors, "$.requiredToolRefs", "out_of_range");
        AssertError(
            errors,
            "$.requiredToolRefs[1]",
            "duplicate_value");
        AssertError(errors, "$.optionalToolRefs", "out_of_range");
        AssertError(errors, "$.contextProviderRefs", "out_of_range");
        AssertError(errors, "$.resourceRefs", "out_of_range");
        AssertError(
            errors,
            "$.resourceRefs[1].uri",
            "duplicate_value");
        AssertError(errors, "$.resourceRefs[2].uri", "invalid_uri");
        AssertError(errors, "$.resourceRefs[3].digest", "required");
        AssertError(
            errors,
            "$.capabilityRequirements",
            "invalid_type");
        AssertError(errors, "$.trust", "unknown_value");
        AssertError(errors, "$.activationPolicy", "invalid_type");
        AssertError(errors, "$.extensions", "out_of_range");
        Assert.Throws<JsonException>(
            () => ProtocolValidator.EnsureValid(value));
    }

    [Fact]
    public void CapabilityManifestAcceptsWireBoundariesAndRejectsOverflow()
    {
        var value = ValidCapabilityManifest();
        value.ProtocolRange = new string('r', 64);
        value.RuntimeVersion = new string('v', 32);
        value.EngineVersion = new string('e', 64);
        value.AdapterVersion = new string('a', 32);
        value.Platform = new string('p', 64);
        value.Backend = new string('b', 64);
        value.ContentTypes = Strings(
            "content",
            ProtocolLimits.MaxCapabilityManifestListItems);
        value.Codecs = Strings(
            "codec",
            ProtocolLimits.MaxCapabilityManifestListItems);
        value.Transports = Strings(
            "transport",
            ProtocolLimits.MaxCapabilityManifestListItems);
        value.ToolEffects = new List<string>
        {
            ToolEffects.PureRead,
            ToolEffects.AgentLocalWrite,
            ToolEffects.WorldCommand,
            ToolEffects.ExternalWrite
        };
        value.ThreadAffinities = new List<string>
        {
            ThreadAffinities.AnyThread,
            ThreadAffinities.EngineMainThread,
            ThreadAffinities.HostManaged
        };
        value.Extensions = Extensions(
            ProtocolLimits.MaxProtocolExtensions);

        Assert.Empty(ProtocolValidator.Validate(value));
        ProtocolValidator.EnsureValid(value);

        value.ProtocolRange += "x";
        value.RuntimeVersion += "x";
        value.Engine = "custom";
        value.EngineVersion += "x";
        value.AdapterVersion += "x";
        value.Platform += "x";
        value.Backend += "x";
        value.ContentTypes.Add("content-overflow");
        value.ContentTypes[1] = value.ContentTypes[0];
        value.Codecs.Add("codec-overflow");
        value.Transports.Add("transport-overflow");
        value.MaxMessageBytes = 0;
        value.MaxBatchSize = 0;
        value.PersistenceLevel = "temporary";
        value.ToolEffects = Enumerable.Repeat(
                ToolEffects.PureRead,
                ProtocolLimits.MaxCapabilityManifestListItems + 1)
            .ToList();
        value.ThreadAffinities = Enumerable.Repeat(
                ThreadAffinities.AnyThread,
                ProtocolLimits.MaxCapabilityManifestListItems + 1)
            .ToList();
        value.ThreadAffinities[0] = "worker";
        value.ProviderCapabilities = Json("[]");
        value.Extensions.Add("overflow", Json("null"));

        var errors = ProtocolValidator.Validate(value);
        AssertError(errors, "$.protocolRange", "out_of_range");
        AssertError(errors, "$.runtimeVersion", "out_of_range");
        AssertError(errors, "$.engine", "unknown_value");
        AssertError(errors, "$.engineVersion", "out_of_range");
        AssertError(errors, "$.adapterVersion", "out_of_range");
        AssertError(errors, "$.platform", "out_of_range");
        AssertError(errors, "$.backend", "out_of_range");
        AssertError(errors, "$.contentTypes", "out_of_range");
        AssertError(
            errors,
            "$.contentTypes[1]",
            "duplicate_value");
        AssertError(errors, "$.codecs", "out_of_range");
        AssertError(errors, "$.transports", "out_of_range");
        AssertError(errors, "$.maxMessageBytes", "out_of_range");
        AssertError(errors, "$.maxBatchSize", "out_of_range");
        AssertError(errors, "$.persistenceLevel", "unknown_value");
        AssertError(errors, "$.toolEffects", "out_of_range");
        AssertError(
            errors,
            "$.toolEffects[1]",
            "duplicate_value");
        AssertError(errors, "$.threadAffinities", "out_of_range");
        AssertError(
            errors,
            "$.threadAffinities[0]",
            "unknown_value");
        AssertError(
            errors,
            "$.providerCapabilities",
            "invalid_type");
        AssertError(errors, "$.extensions", "out_of_range");
        Assert.Throws<JsonException>(
            () => ProtocolValidator.EnsureValid(value));
    }

    [Fact]
    public void ContextBudgetReportAcceptsWireBoundariesAndRejectsOverflow()
    {
        var value = ValidContextBudgetReport();
        value.SelectedIds = Strings(
            "selected",
            ProtocolLimits.MaxContextBudgetReportItems);
        value.DeferredIds = Strings(
            "deferred",
            ProtocolLimits.MaxContextBudgetReportItems);
        value.Pruned = Enumerable.Range(
                0,
                ProtocolLimits.MaxContextBudgetReportItems)
            .Select(
                index => new PrunedContextItem
                {
                    Id = $"pruned-{index}",
                    Category = "optional",
                    ReasonCode = "budget"
                })
            .ToList();
        value.Externalized = Resources(
            ProtocolLimits.MaxContextBudgetReportItems);
        value.ReasonCodes = Strings(
            "reason",
            ProtocolLimits.MaxContextBudgetReportItems);
        value.Extensions = Extensions(
            ProtocolLimits.MaxProtocolExtensions);

        Assert.Empty(ProtocolValidator.Validate(value));
        ProtocolValidator.EnsureValid(value);

        value.RunId = string.Empty;
        value.InputCount = -1;
        value.SelectedIds.Add("selected-overflow");
        value.SelectedIds[1] = value.SelectedIds[0];
        value.DeferredIds.Add("deferred-overflow");
        value.Pruned.Add(
            new PrunedContextItem
            {
                Id = "pruned-overflow",
                Category = "optional",
                ReasonCode = "budget"
            });
        value.Pruned[1].Id = value.Pruned[0].Id;
        value.Externalized.Add(Resource("overflow"));
        value.Externalized[1].Uri = value.Externalized[0].Uri;
        value.EstimatedTokens = -1;
        value.ActualTokens = -1;
        value.BudgetLimit = 0;
        value.ReasonCodes.Add("reason-overflow");
        value.ReasonCodes[1] = value.ReasonCodes[0];
        value.Extensions.Add("overflow", Json("null"));

        var errors = ProtocolValidator.Validate(value);
        AssertError(errors, "$.runId", "required");
        AssertError(errors, "$.inputCount", "out_of_range");
        AssertError(errors, "$.selectedIds", "out_of_range");
        AssertError(
            errors,
            "$.selectedIds[1]",
            "duplicate_value");
        AssertError(errors, "$.deferredIds", "out_of_range");
        AssertError(errors, "$.pruned", "out_of_range");
        AssertError(errors, "$.pruned[1].id", "duplicate_value");
        AssertError(errors, "$.externalized", "out_of_range");
        AssertError(
            errors,
            "$.externalized[1].uri",
            "duplicate_value");
        AssertError(errors, "$.estimatedTokens", "out_of_range");
        AssertError(errors, "$.actualTokens", "out_of_range");
        AssertError(errors, "$.budgetLimit", "out_of_range");
        AssertError(errors, "$.reasonCodes", "out_of_range");
        AssertError(
            errors,
            "$.reasonCodes[1]",
            "duplicate_value");
        AssertError(errors, "$.extensions", "out_of_range");
        Assert.Throws<JsonException>(
            () => ProtocolValidator.EnsureValid(value));
    }

    [Fact]
    public void ValidatorsReturnStructuredErrorsForExplicitNullMembers()
    {
        var agent = ValidAgentDefinition();
        agent.Version = null!;
        agent.Toolsets = null!;
        agent.Skills = null!;
        agent.Extensions = null!;

        AssertError(
            ProtocolValidator.Validate(agent),
            "$.version",
            "required");
        AssertError(
            ProtocolValidator.Validate(agent),
            "$.toolsets",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(agent),
            "$.skills",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(agent),
            "$.extensions",
            "invalid_type");

        var skill = ValidSkillManifest();
        skill.Description = null!;
        skill.PromptFragments = null!;
        skill.ResourceRefs = null!;
        skill.Trust = null!;
        AssertError(
            ProtocolValidator.Validate(skill),
            "$.description",
            "required");
        AssertError(
            ProtocolValidator.Validate(skill),
            "$.promptFragments",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(skill),
            "$.resourceRefs",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(skill),
            "$.trust",
            "required");

        var capability = ValidCapabilityManifest();
        capability.Engine = null!;
        capability.ContentTypes = null!;
        capability.PersistenceLevel = null!;
        AssertError(
            ProtocolValidator.Validate(capability),
            "$.engine",
            "required");
        AssertError(
            ProtocolValidator.Validate(capability),
            "$.contentTypes",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(capability),
            "$.persistenceLevel",
            "required");

        var report = ValidContextBudgetReport();
        report.SelectedIds = null!;
        report.Pruned = null!;
        report.Externalized = null!;
        report.ReasonCodes = null!;
        AssertError(
            ProtocolValidator.Validate(report),
            "$.selectedIds",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(report),
            "$.pruned",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(report),
            "$.externalized",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(report),
            "$.reasonCodes",
            "invalid_type");
    }

    [Fact]
    public void ValidatorsRejectNullNestedItemsWithoutThrowing()
    {
        var agent = ValidAgentDefinition();
        agent.Toolsets = new List<string> { null! };
        agent.Skills = new List<string> { null! };
        agent.Extensions["undefined"] = default;
        AssertError(
            ProtocolValidator.Validate(agent),
            "$.toolsets[0]",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(agent),
            "$.skills[0]",
            "required");
        AssertError(
            ProtocolValidator.Validate(agent),
            "$.extensions.undefined",
            "required");

        var skill = ValidSkillManifest();
        skill.PromptFragments = new List<string> { null! };
        skill.ResourceRefs =
            new List<ResourceReference> { null! };
        AssertError(
            ProtocolValidator.Validate(skill),
            "$.promptFragments[0]",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(skill),
            "$.resourceRefs[0]",
            "invalid_type");

        var capability = ValidCapabilityManifest();
        capability.ContentTypes = new List<string> { null! };
        capability.ToolEffects = new List<string> { null! };
        AssertError(
            ProtocolValidator.Validate(capability),
            "$.contentTypes[0]",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(capability),
            "$.toolEffects[0]",
            "required");

        var report = ValidContextBudgetReport();
        report.Pruned = new List<PrunedContextItem> { null! };
        report.Externalized =
            new List<ResourceReference> { null! };
        AssertError(
            ProtocolValidator.Validate(report),
            "$.pruned[0]",
            "invalid_type");
        AssertError(
            ProtocolValidator.Validate(report),
            "$.externalized[0]",
            "invalid_type");
    }

    [Fact]
    public void ArbitraryJsonUsesStandardRuntimeBoundaries()
    {
        var value = ValidAgentDefinition();
        value.Identity = JsonString(
            ProtocolLimits.MaxProtocolJsonStringUtf8Bytes);
        Assert.Empty(ProtocolValidator.Validate(value));

        value.Identity = JsonString(
            ProtocolLimits.MaxProtocolJsonStringUtf8Bytes + 1);
        AssertError(
            ProtocolValidator.Validate(value),
            "$.identity",
            "json_string_bytes_exceeded");

        value.Identity = JsonArray(
            ProtocolLimits.MaxProtocolJsonContainerItems);
        Assert.Empty(ProtocolValidator.Validate(value));
        value.Identity = JsonArray(
            ProtocolLimits.MaxProtocolJsonContainerItems + 1);
        AssertError(
            ProtocolValidator.Validate(value),
            "$.identity",
            "json_container_items_exceeded");

        value.Identity = NestedJson(
            ProtocolLimits.MaxProtocolJsonDepth - 1);
        Assert.Empty(ProtocolValidator.Validate(value));
        value.Identity = NestedJson(
            ProtocolLimits.MaxProtocolJsonDepth);
        AssertError(
            ProtocolValidator.Validate(value),
            "$.identity",
            "json_depth_exceeded");

        value.Identity = JsonWithNodeCount(
            ProtocolLimits.MaxProtocolJsonNodes);
        Assert.Empty(ProtocolValidator.Validate(value));
        value.Identity = JsonWithNodeCount(
            ProtocolLimits.MaxProtocolJsonNodes + 1);
        AssertError(
            ProtocolValidator.Validate(value),
            "$.identity",
            "json_nodes_exceeded");

        value.Identity = JsonAtUtf8ByteLimit(extraBytes: 0);
        Assert.Empty(ProtocolValidator.Validate(value));
        value.Identity = JsonAtUtf8ByteLimit(extraBytes: 1);
        AssertError(
            ProtocolValidator.Validate(value),
            "$.identity",
            "json_bytes_exceeded");
    }

    [Theory]
    [InlineData(
        "agent-definition.schema.json",
        "toolsets",
        ProtocolLimits.MaxAgentDefinitionReferences)]
    [InlineData(
        "agent-definition.schema.json",
        "skills",
        ProtocolLimits.MaxAgentDefinitionReferences)]
    [InlineData(
        "skill-manifest.schema.json",
        "promptFragments",
        ProtocolLimits.MaxSkillManifestListItems)]
    [InlineData(
        "skill-manifest.schema.json",
        "requiredToolRefs",
        ProtocolLimits.MaxSkillManifestListItems)]
    [InlineData(
        "skill-manifest.schema.json",
        "optionalToolRefs",
        ProtocolLimits.MaxSkillManifestListItems)]
    [InlineData(
        "skill-manifest.schema.json",
        "contextProviderRefs",
        ProtocolLimits.MaxSkillManifestListItems)]
    [InlineData(
        "skill-manifest.schema.json",
        "resourceRefs",
        ProtocolLimits.MaxSkillManifestListItems)]
    [InlineData(
        "capability-manifest.schema.json",
        "contentTypes",
        ProtocolLimits.MaxCapabilityManifestListItems)]
    [InlineData(
        "capability-manifest.schema.json",
        "codecs",
        ProtocolLimits.MaxCapabilityManifestListItems)]
    [InlineData(
        "capability-manifest.schema.json",
        "transports",
        ProtocolLimits.MaxCapabilityManifestListItems)]
    [InlineData(
        "capability-manifest.schema.json",
        "toolEffects",
        ProtocolLimits.MaxCapabilityManifestListItems)]
    [InlineData(
        "capability-manifest.schema.json",
        "threadAffinities",
        ProtocolLimits.MaxCapabilityManifestListItems)]
    [InlineData(
        "context-budget-report.schema.json",
        "selectedIds",
        ProtocolLimits.MaxContextBudgetReportItems)]
    [InlineData(
        "context-budget-report.schema.json",
        "deferredIds",
        ProtocolLimits.MaxContextBudgetReportItems)]
    [InlineData(
        "context-budget-report.schema.json",
        "pruned",
        ProtocolLimits.MaxContextBudgetReportItems)]
    [InlineData(
        "context-budget-report.schema.json",
        "externalized",
        ProtocolLimits.MaxContextBudgetReportItems)]
    [InlineData(
        "context-budget-report.schema.json",
        "reasonCodes",
        ProtocolLimits.MaxContextBudgetReportItems)]
    public void SchemaCollectionLimitsMatchPublicConstants(
        string schemaFile,
        string property,
        int maximumItems)
    {
        using var schema = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    FixtureFiles.SchemaDirectory,
                    schemaFile)));

        Assert.Equal(
            maximumItems,
            schema.RootElement
                .GetProperty("properties")
                .GetProperty(property)
                .GetProperty("maxItems")
                .GetInt32());
        Assert.Equal(
            ProtocolLimits.MaxProtocolExtensions,
            schema.RootElement
                .GetProperty("properties")
                .GetProperty("extensions")
                .GetProperty("maxProperties")
                .GetInt32());
    }

    private static AgentDefinition ValidAgentDefinition()
    {
        return new AgentDefinition
        {
            AgentDefinitionId = "agent-definition",
            Version = "1",
            Identity = Json("null"),
            Toolsets = new List<string>(),
            Skills = new List<string>(),
            Budgets = Json("{}")
        };
    }

    private static SkillManifest ValidSkillManifest()
    {
        return new SkillManifest
        {
            SkillId = "skill",
            Version = "1",
            Digest = "digest",
            Description = "description",
            PromptFragments = new List<string>(),
            RequiredToolRefs = new List<string>(),
            OptionalToolRefs = new List<string>(),
            ContextProviderRefs = new List<string>(),
            ResourceRefs = new List<ResourceReference>(),
            CapabilityRequirements = Json("{}"),
            Trust = "trusted",
            ActivationPolicy = Json("{}")
        };
    }

    private static CapabilityManifest ValidCapabilityManifest()
    {
        return new CapabilityManifest
        {
            ProtocolRange = "0.2",
            RuntimeVersion = "1",
            Engine = "godot",
            EngineVersion = "4",
            AdapterVersion = "1",
            Platform = "windows",
            Backend = "mono",
            ContentTypes = new List<string>(),
            Codecs = new List<string>(),
            Transports = new List<string>(),
            MaxMessageBytes = 1,
            MaxBatchSize = 1,
            Streaming = true,
            PersistenceLevel = "durable",
            ToolEffects = new List<string>(),
            ThreadAffinities = new List<string>(),
            ReceiptReconciliation = true,
            ProviderCapabilities = Json("{}")
        };
    }

    private static ContextBudgetReport ValidContextBudgetReport()
    {
        return new ContextBudgetReport
        {
            RunId = "run",
            TurnId = "turn",
            InputCount = 0,
            SelectedIds = new List<string>(),
            DeferredIds = new List<string>(),
            Pruned = new List<PrunedContextItem>(),
            Externalized = new List<ResourceReference>(),
            EstimatedTokens = 0,
            ActualTokens = 0,
            BudgetLimit = 1,
            ReasonCodes = new List<string>()
        };
    }

    private static List<string> Strings(string prefix, int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => $"{prefix}-{index}")
            .ToList();
    }

    private static List<ResourceReference> Resources(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => Resource(index.ToString()))
            .ToList();
    }

    private static ResourceReference Resource(string suffix)
    {
        return new ResourceReference
        {
            Uri = $"game://resource/{suffix}",
            MediaType = "application/json"
        };
    }

    private static Dictionary<string, JsonElement> Extensions(int count)
    {
        return Enumerable.Range(0, count)
            .ToDictionary(
                index => $"extension-{index}",
                _ => Json("null"),
                StringComparer.Ordinal);
    }

    private static JsonElement Json(string json)
    {
        return ProtocolJson.ParseElement(json);
    }

    private static JsonElement JsonString(int length)
    {
        return Json("\"" + new string('x', length) + "\"");
    }

    private static JsonElement JsonArray(int count)
    {
        return Json(
            "["
            + string.Join(
                ",",
                Enumerable.Repeat("null", count))
            + "]");
    }

    private static JsonElement NestedJson(int containers)
    {
        return Json(
            new string('[', containers)
            + "null"
            + new string(']', containers));
    }

    private static JsonElement JsonWithNodeCount(int nodes)
    {
        var remaining = nodes - 1;
        var arrays = new List<string>();
        while (remaining > 0)
        {
            var itemCount = Math.Min(
                ProtocolLimits.MaxProtocolJsonContainerItems,
                remaining - 1);
            arrays.Add(
                "["
                + string.Join(
                    ",",
                    Enumerable.Repeat("null", itemCount))
                + "]");
            remaining -= itemCount + 1;
        }

        return Json("[" + string.Join(",", arrays) + "]");
    }

    private static JsonElement JsonAtUtf8ByteLimit(int extraBytes)
    {
        var first = ProtocolLimits.MaxProtocolJsonStringUtf8Bytes;
        var second = ProtocolLimits.MaxProtocolJsonStringUtf8Bytes;
        var third = ProtocolLimits.MaxProtocolJsonStringUtf8Bytes;
        const int punctuationBytes = 13;
        var fourth = ProtocolLimits.MaxProtocolJsonUtf8Bytes
            - punctuationBytes
            - first
            - second
            - third
            + extraBytes;
        var json = new StringBuilder(
            ProtocolLimits.MaxProtocolJsonUtf8Bytes + extraBytes);
        json.Append("[\"");
        json.Append('x', first);
        json.Append("\",\"");
        json.Append('x', second);
        json.Append("\",\"");
        json.Append('x', third);
        json.Append("\",\"");
        json.Append('x', fourth);
        json.Append("\"]");
        return Json(json.ToString());
    }

    private static void AssertError(
        IReadOnlyList<ProtocolValidationError> errors,
        string path,
        string code)
    {
        Assert.Contains(
            errors,
            error => error.Path == path && error.Code == code);
    }
}

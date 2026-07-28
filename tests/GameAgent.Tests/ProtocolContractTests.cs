using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class ProtocolContractTests
{
    [Fact]
    public void EverySchemaIsVersionedV02AndParsesAsJson()
    {
        var schemaFiles = Directory.GetFiles(
            FixtureFiles.SchemaDirectory,
            "*.schema.json",
            SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(schemaFiles);
        foreach (var schemaFile in schemaFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(schemaFile));
            var root = document.RootElement;
            Assert.Equal(
                "https://json-schema.org/draft/2020-12/schema",
                root.GetProperty("$schema").GetString());
            Assert.Equal(
                "https://raw.githubusercontent.com/EricSun0218/"
                + "game-agent-runtime/main/schemas/"
                + Path.GetFileName(schemaFile),
                root.GetProperty("$id").GetString());
        }
    }

    [Fact]
    public void ValidJsonOnlyFixturesRoundTripAndPassSemanticValidation()
    {
        var observation = ProtocolJson.DeserializeObservationEnvelope(
            FixtureFiles.Read("v0.2", "valid", "json-only-tool-loop", "observation.json"));
        var tool = ProtocolJson.DeserializeToolDescriptor(
            FixtureFiles.Read("v0.2", "valid", "json-only-tool-loop", "tool-descriptor.json"));
        var run = ProtocolJson.DeserializeAgentRun(
            FixtureFiles.Read("v0.2", "valid", "json-only-tool-loop", "agent-run.json"));
        var request = ProtocolJson.DeserializeActionRequest(
            FixtureFiles.Read("v0.2", "valid", "json-only-tool-loop", "action-request.json"));
        var receipt = ProtocolJson.DeserializeActionReceipt(
            FixtureFiles.Read("v0.2", "valid", "json-only-tool-loop", "action-receipt.json"));

        Assert.Empty(ProtocolValidator.Validate(observation));
        Assert.Empty(ProtocolValidator.Validate(tool));
        Assert.Empty(ProtocolValidator.Validate(run));
        Assert.Empty(ProtocolValidator.Validate(request));
        Assert.Empty(ProtocolValidator.Validate(receipt));

        var roundTripped = ProtocolJson.DeserializeObservationEnvelope(
            ProtocolJson.Serialize(observation));
        Assert.Equal(70, roundTripped.Payload!.Value.GetProperty("hunger").GetInt32());
        Assert.Equal("1", roundTripped.ContentSchemaVersion);
    }

    [Fact]
    public void InvalidFixturesFailTheRelevantSemanticInvariant()
    {
        var observation = ProtocolJson.DeserializeObservationEnvelope(
            FixtureFiles.Read("v0.2", "invalid", "observation-missing-content.json"));
        var tool = ProtocolJson.DeserializeToolDescriptor(
            FixtureFiles.Read("v0.2", "invalid", "world-command-no-idempotency.json"));
        var receipt = ProtocolJson.DeserializeActionReceipt(
            FixtureFiles.Read("v0.2", "invalid", "receipt-negative-revision.json"));

        Assert.Contains(
            ProtocolValidator.Validate(observation),
            error => error.Code == "exactly_one_content");
        Assert.Contains(
            ProtocolValidator.Validate(tool),
            error => error.Code == "side_effect_requires_idempotency");
        Assert.Contains(
            ProtocolValidator.Validate(receipt),
            error => error.Code == "out_of_range");
    }

    [Fact]
    public void ToolDescriptorValidatorMatchesSchemaEnumsAndTimeoutBound()
    {
        var tool = ProtocolJson.DeserializeToolDescriptor(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "tool-descriptor.json"));
        tool.RetryPolicy = "sometimes";
        tool.IdempotencyPolicy = "automatic";
        tool.Visibility = "secret";
        tool.TimeoutMs = 86_400_001;

        var errors = ProtocolValidator.Validate(tool);

        Assert.Contains(
            errors,
            item => item.Path == "$.retryPolicy"
                    && item.Code == "unknown_value");
        Assert.Contains(
            errors,
            item => item.Path == "$.idempotencyPolicy"
                    && item.Code == "unknown_value");
        Assert.Contains(
            errors,
            item => item.Path == "$.visibility"
                    && item.Code == "unknown_value");
        Assert.Contains(
            errors,
            item => item.Path == "$.timeoutMs"
                    && item.Code == "out_of_range");
    }

    [Fact]
    public void ToolDescriptorAcceptsSchemaTimeoutMaximum()
    {
        var tool = ProtocolJson.DeserializeToolDescriptor(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "tool-descriptor.json"));
        tool.TimeoutMs = 86_400_000;
        tool.RetryPolicy = ToolRetryPolicies.SafeRead;
        tool.IdempotencyPolicy = ToolIdempotencyPolicies.BestEffort;
        tool.Visibility = ToolVisibilities.Deferred;

        Assert.Empty(ProtocolValidator.Validate(tool));
    }

    [Fact]
    public void ObservationWithNullVisibilityReturnsStructuredValidationError()
    {
        var observation = ValidObservation();
        observation.Visibility = null!;

        var errors = ProtocolValidator.Validate(observation);

        Assert.Contains(
            errors,
            error => error.Path == "$.visibility"
                     && error.Code == "invalid_type");
        Assert.Throws<JsonException>(
            () => ProtocolValidator.EnsureValid(observation));
    }

    [Fact]
    public void ObservationRejectsNullNestedCollectionsAndResourceMembers()
    {
        var observation = ValidObservation();
        observation.SubjectIds = null!;
        observation.Extensions = null!;
        observation.Visibility.AudienceIds = null!;
        observation.Payload = null;
        observation.ResourceRef = new ResourceReference
        {
            Uri = null!,
            MediaType = null!,
            SizeBytes = -1
        };

        var errors = ProtocolValidator.Validate(observation);

        Assert.Contains(errors, error => error.Path == "$.subjectIds");
        Assert.Contains(
            errors,
            error => error.Path == "$.visibility.audienceIds");
        Assert.Contains(errors, error => error.Path == "$.extensions");
        Assert.Contains(errors, error => error.Path == "$.resourceRef.uri");
        Assert.Contains(
            errors,
            error => error.Path == "$.resourceRef.mediaType");
        Assert.Contains(
            errors,
            error => error.Path == "$.resourceRef.sizeBytes");
    }

    [Fact]
    public void AgentRunRejectsEveryNegativeUsageAndRevision()
    {
        var run = ProtocolJson.DeserializeAgentRun(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "agent-run.json"));

        run.Revision = -1;
        run.Usage.Turns = -1;
        run.Usage.DurationMs = -1;
        run.Usage.InputTokens = -1;
        run.Usage.OutputTokens = -1;
        run.Usage.Actions = -1;
        run.Usage.CostUsd = "-0.01";
        run.Usage.UnaccountedProviderAttempts = -1;
        run.Usage.HasUnaccountedUsage = true;

        var errors = ProtocolValidator.Validate(run);

        Assert.Contains(errors, error => error.Path == "$.revision");
        Assert.Contains(errors, error => error.Path == "$.usage.turns");
        Assert.Contains(errors, error => error.Path == "$.usage.durationMs");
        Assert.Contains(errors, error => error.Path == "$.usage.inputTokens");
        Assert.Contains(errors, error => error.Path == "$.usage.outputTokens");
        Assert.Contains(errors, error => error.Path == "$.usage.actions");
        Assert.Contains(errors, error => error.Path == "$.usage.costUsd");
        Assert.Contains(
            errors,
            error =>
                error.Path == "$.usage.unaccountedProviderAttempts");
        Assert.Contains(
            errors,
            error => error.Path == "$.usage.hasUnaccountedUsage");
    }

    [Fact]
    public void AgentUsageRequiresUnaccountedFlagAndCountToAgree()
    {
        var usage = new AgentUsage
        {
            HasUnaccountedUsage = false,
            UnaccountedProviderAttempts = 1
        };

        var error = Assert.Single(
            ProtocolValidator.Validate(usage),
            item => item.Path == "$.hasUnaccountedUsage");

        Assert.Equal("inconsistent_value", error.Code);
    }

    [Fact]
    public void AgentBudgetRejectsEveryOutOfRangeLimit()
    {
        var budget = new AgentBudget
        {
            MaxTurns = 0,
            MaxDurationMs = 0,
            MaxTokens = 0,
            MaxCostUsd = "-1",
            MaxActions = -1
        };

        var errors = ProtocolValidator.Validate(budget);

        Assert.Contains(errors, error => error.Path == "$.maxTurns");
        Assert.Contains(errors, error => error.Path == "$.maxDurationMs");
        Assert.Contains(errors, error => error.Path == "$.maxTokens");
        Assert.Contains(errors, error => error.Path == "$.maxCostUsd");
        Assert.Contains(errors, error => error.Path == "$.maxActions");
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1e2")]
    [InlineData("NaN")]
    public void AgentBudgetRejectsMalformedCostStrings(string value)
    {
        var budget = new AgentBudget { MaxCostUsd = value };

        Assert.Contains(
            ProtocolValidator.Validate(budget),
            error => error.Path == "$.maxCostUsd"
                     && error.Code == "invalid_decimal");
    }

    [Fact]
    public void AgentBudgetAndUsageAcceptSchemaBoundaries()
    {
        var budget = new AgentBudget
        {
            MaxTurns = 1,
            MaxDurationMs = 1,
            MaxTokens = 1,
            MaxCostUsd = "0",
            MaxActions = 0
        };
        var usage = new AgentUsage
        {
            Turns = 0,
            DurationMs = 0,
            InputTokens = 0,
            OutputTokens = 0,
            CostUsd = "0.0",
            Actions = 0
        };

        Assert.Empty(ProtocolValidator.Validate(budget));
        Assert.Empty(ProtocolValidator.Validate(usage));
    }

    private static ObservationEnvelope ValidObservation()
    {
        return ProtocolJson.DeserializeObservationEnvelope(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "observation.json"));
    }
}

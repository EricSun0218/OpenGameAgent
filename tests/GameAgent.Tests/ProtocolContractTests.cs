using System.Text.Json;
using System.Text.Json.Nodes;
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
                + "OpenGameAgent/main/schemas/"
                + Path.GetFileName(schemaFile),
                root.GetProperty("$id").GetString());
        }
    }

    [Fact]
    public void ResourceUriSchemaMatchesNonEmptyRuntimeContract()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    FixtureFiles.SchemaDirectory,
                    "common.schema.json")));
        var uri = document.RootElement
            .GetProperty("$defs")
            .GetProperty("resourceRef")
            .GetProperty("properties")
            .GetProperty("uri");

        Assert.Equal(1, uri.GetProperty("minLength").GetInt32());
        Assert.Equal(
            "uri-reference",
            uri.GetProperty("format").GetString());
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
        var resourceObservation =
            ProtocolJson.DeserializeObservationEnvelope(
                FixtureFiles.Read(
                    "v0.2",
                    "valid",
                    "json-only-tool-loop",
                    "resource-observation.json"));

        Assert.Empty(ProtocolValidator.Validate(observation));
        Assert.Empty(ProtocolValidator.Validate(tool));
        Assert.Empty(ProtocolValidator.Validate(run));
        Assert.Empty(ProtocolValidator.Validate(request));
        Assert.Empty(ProtocolValidator.Validate(receipt));
        Assert.Empty(ProtocolValidator.Validate(resourceObservation));

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
        var malformedResource =
            ProtocolJson.DeserializeObservationEnvelope(
                FixtureFiles.Read(
                    "v0.2",
                    "invalid",
                    "observation-resource-malformed-uri.json"));
        var emptyDigest =
            ProtocolJson.DeserializeObservationEnvelope(
                FixtureFiles.Read(
                    "v0.2",
                    "invalid",
                    "observation-resource-empty-digest.json"));
        var patch =
            ProtocolJson.DeserializeObservationEnvelope(
                FixtureFiles.Read(
                    "v0.2",
                    "invalid",
                    "observation-patch-missing-state-version.json"));
        var nestedPatch =
            ProtocolJson.DeserializeActionReceipt(
                FixtureFiles.Read(
                    "v0.2",
                    "invalid",
                    "receipt-patch-missing-state-version.json"));

        Assert.Contains(
            ProtocolValidator.Validate(observation),
            error => error.Code == "exactly_one_content");
        Assert.Contains(
            ProtocolValidator.Validate(tool),
            error => error.Code == "side_effect_requires_idempotency");
        Assert.Contains(
            ProtocolValidator.Validate(receipt),
            error => error.Code == "out_of_range");
        Assert.Contains(
            ProtocolValidator.Validate(malformedResource),
            error => error.Code == "invalid_uri");
        Assert.Contains(
            ProtocolValidator.Validate(emptyDigest),
            error => error.Path == "$.resourceRef.digest"
                     && error.Code == "required");
        Assert.Contains(
            ProtocolValidator.Validate(patch),
            error => error.Code == "patch_requires_state_version");
        Assert.Contains(
            ProtocolValidator.Validate(nestedPatch),
            error => error.Path
                         == "$.authoritativeObservations[0].stateVersion"
                     && error.Code == "patch_requires_state_version");
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
    public void ToolDescriptorValidatorEnforcesCanonicalStringAndShapeBounds()
    {
        var tool = ProtocolJson.DeserializeToolDescriptor(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "tool-descriptor.json"));
        tool.Version = string.Concat(
            Enumerable.Repeat("\U0001F642", 32));
        tool.Description = string.Concat(
            Enumerable.Repeat("\U0001F642", 2_048));
        tool.Toolset = string.Concat(
            Enumerable.Repeat("\U0001F642", 96));
        tool.ConflictScopes = new List<string>
        {
            string.Concat(Enumerable.Repeat("\U0001F642", 128))
        };
        Assert.DoesNotContain(
            ProtocolValidator.Validate(tool),
            error => error.Path is "$.version"
                or "$.description"
                or "$.toolset"
                or "$.conflictScopes[0]");

        tool.Name = "Invalid.Name";
        tool.Version += "\U0001F642";
        tool.Description += "\U0001F642";
        tool.Toolset += "\U0001F642";
        tool.ConflictScopes = new List<string>
        {
            string.Concat(Enumerable.Repeat("\U0001F642", 129)),
            "duplicate",
            "duplicate"
        };
        tool.ParametersSchema = default;

        var errors = ProtocolValidator.Validate(tool);
        Assert.Contains(errors, item => item.Path == "$.name");
        Assert.Contains(errors, item => item.Path == "$.version");
        Assert.Contains(errors, item => item.Path == "$.description");
        Assert.Contains(errors, item => item.Path == "$.toolset");
        Assert.Contains(
            errors,
            item => item.Path == "$.conflictScopes[0]");
        Assert.Contains(
            errors,
            item => item.Path == "$.conflictScopes[2]"
                    && item.Code == "duplicate_value");
        Assert.Contains(
            errors,
            item => item.Path == "$.parametersSchema"
                    && item.Code == "invalid_type");
    }

    [Fact]
    public void ToolDescriptorConflictScopeCountMatchesActionWireCapacity()
    {
        var tool = ProtocolJson.DeserializeToolDescriptor(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "tool-descriptor.json"));
        tool.ConflictScopes = Enumerable.Range(
                0,
                ProtocolLimits.MaxToolConflictScopes)
            .Select(index => $"scope:{index}")
            .ToList();

        Assert.DoesNotContain(
            ProtocolValidator.Validate(tool),
            error => error.Path == "$.conflictScopes");

        tool.ConflictScopes.Add("scope:overflow");

        Assert.Contains(
            ProtocolValidator.Validate(tool),
            error => error.Path == "$.conflictScopes"
                     && error.Code == "out_of_range");
    }

    [Fact]
    public void ToolInvocationValidatorEnforcesRequiredShapeAndWireBounds()
    {
        var invocation = new ToolInvocation
        {
            ToolCallId = "call-1",
            RunId = "run-1",
            TurnId = "turn-1",
            AttemptId = "attempt-1",
            ToolName = "inspect_state",
            ToolVersion = "1",
            Arguments = ProtocolJson.ParseElement("{}"),
            Effect = ToolEffects.PureRead,
            ResolvedConflictKeys = Enumerable.Range(
                    0,
                    ProtocolLimits.MaxToolResolvedConflictKeys)
                .Select(index => $"scope:{index}")
                .ToList(),
            Sequence = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
        invocation.ResolvedConflictKeys[0] = new string(
            'x',
            ProtocolLimits.MaxToolResolvedConflictKeyUnicodeScalars);

        Assert.Empty(ProtocolValidator.Validate(invocation));

        invocation.ToolCallId = string.Empty;
        invocation.RunId = string.Empty;
        invocation.TurnId = string.Empty;
        invocation.AttemptId = string.Empty;
        invocation.ToolName = "Invalid.Name";
        invocation.ToolVersion = string.Empty;
        invocation.Arguments = default;
        invocation.Effect = "unknown";
        invocation.ResolvedConflictKeys.Add("scope:overflow");
        invocation.ResolvedConflictKeys[0] = new string(
            'x',
            ProtocolLimits.MaxToolResolvedConflictKeyUnicodeScalars + 1);
        invocation.Sequence = -1;

        var errors = ProtocolValidator.Validate(invocation);
        Assert.Contains(errors, item => item.Path == "$.toolCallId");
        Assert.Contains(errors, item => item.Path == "$.runId");
        Assert.Contains(errors, item => item.Path == "$.turnId");
        Assert.Contains(errors, item => item.Path == "$.attemptId");
        Assert.Contains(errors, item => item.Path == "$.toolName");
        Assert.Contains(errors, item => item.Path == "$.toolVersion");
        Assert.Contains(errors, item => item.Path == "$.arguments");
        Assert.Contains(errors, item => item.Path == "$.effect");
        Assert.Contains(
            errors,
            item => item.Path == "$.resolvedConflictKeys"
                    && item.Code == "out_of_range");
        Assert.Contains(
            errors,
            item => item.Path == "$.resolvedConflictKeys[0]"
                    && item.Code == "out_of_range");
        Assert.Contains(errors, item => item.Path == "$.sequence");
    }

    [Fact]
    public void ActionRequestValidatorEnforcesCanonicalPublicBounds()
    {
        var action = ProtocolJson.DeserializeActionRequest(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "action-request.json"));
        action.ActionVersion = string.Concat(
            Enumerable.Repeat("\U0001F642", 32));
        action.BasedOnStateVersion = string.Concat(
            Enumerable.Repeat("\U0001F642", 128));
        action.ExpectedEffects = new List<string>
        {
            string.Concat(Enumerable.Repeat("\U0001F642", 256))
        };
        action.ReasonCode = string.Concat(
            Enumerable.Repeat("\U0001F642", 128));
        Assert.DoesNotContain(
            ProtocolValidator.Validate(action),
            error => error.Path is "$.actionVersion"
                or "$.basedOnStateVersion"
                or "$.expectedEffects[0]"
                or "$.reasonCode");

        action.ActionName = "Invalid.Name";
        action.ActionVersion += "\U0001F642";
        action.BasedOnStateVersion += "\U0001F642";
        action.ExpectedEffects = Enumerable.Repeat("effect", 33)
            .ToList();
        action.ExpectedEffects[0] = string.Concat(
            Enumerable.Repeat("\U0001F642", 257));
        action.ReasonCode += "\U0001F642";

        var errors = ProtocolValidator.Validate(action);
        Assert.Contains(errors, item => item.Path == "$.actionName");
        Assert.Contains(errors, item => item.Path == "$.actionVersion");
        Assert.Contains(
            errors,
            item => item.Path == "$.basedOnStateVersion");
        Assert.Contains(
            errors,
            item => item.Path == "$.expectedEffects"
                    && item.Code == "out_of_range");
        Assert.Contains(
            errors,
            item => item.Path == "$.expectedEffects[0]"
                    && item.Code == "out_of_range");
        Assert.Contains(errors, item => item.Path == "$.reasonCode");
    }

    [Fact]
    public void TurnSnapshotValidatorMatchesProviderAndPolicyRuntimeBounds()
    {
        var snapshot = new TurnSnapshot
        {
            TurnId = "turn-1",
            RunId = "run-1",
            RuntimeGeneration = 1,
            ProviderId = "gateway/provider",
            ModelId = "openai/gpt-4.1",
            PromptLayoutVersion = new string(
                'p',
                ProtocolLimits.MaxTurnPolicyVersionUnicodeScalars),
            StablePrefixHash = "stable",
            SkillGeneration = 0,
            SkillDigests = new List<string> { "skill-digest" },
            ToolCatalogGeneration = 0,
            DirectToolDigest = "tool-digest",
            ContextPolicyVersion = new string(
                'c',
                ProtocolLimits.MaxTurnPolicyVersionUnicodeScalars),
            BudgetPolicyVersion = new string(
                'b',
                ProtocolLimits.MaxTurnPolicyVersionUnicodeScalars),
            MaxSideEffectToolCallsPerTurn = 4_096,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Empty(ProtocolValidator.Validate(snapshot));

        snapshot.RuntimeGeneration = 0;
        snapshot.ProviderId = new string(
            'p',
            ProtocolLimits.MaxProviderIdUnicodeScalars + 1);
        snapshot.ModelId = new string(
            'm',
            ProtocolLimits.MaxModelIdUnicodeScalars + 1);
        snapshot.PromptLayoutVersion += "x";
        snapshot.StablePrefixHash = string.Empty;
        snapshot.SkillGeneration = -1;
        snapshot.SkillDigests[0] = new string('s', 257);
        snapshot.ToolCatalogGeneration = -1;
        snapshot.DirectToolDigest = string.Empty;
        snapshot.ContextPolicyVersion += "x";
        snapshot.BudgetPolicyVersion += "x";
        snapshot.MaxSideEffectToolCallsPerTurn = 4_097;

        var errors = ProtocolValidator.Validate(snapshot);
        Assert.Contains(errors, item => item.Path == "$.runtimeGeneration");
        Assert.Contains(errors, item => item.Path == "$.providerId");
        Assert.Contains(errors, item => item.Path == "$.modelId");
        Assert.Contains(errors, item => item.Path == "$.promptLayoutVersion");
        Assert.Contains(errors, item => item.Path == "$.stablePrefixHash");
        Assert.Contains(errors, item => item.Path == "$.skillGeneration");
        Assert.Contains(errors, item => item.Path == "$.skillDigests[0]");
        Assert.Contains(errors, item => item.Path == "$.toolCatalogGeneration");
        Assert.Contains(errors, item => item.Path == "$.directToolDigest");
        Assert.Contains(errors, item => item.Path == "$.contextPolicyVersion");
        Assert.Contains(errors, item => item.Path == "$.budgetPolicyVersion");
        Assert.Contains(
            errors,
            item => item.Path == "$.maxSideEffectToolCallsPerTurn");

        snapshot.ProviderId = null!;
        snapshot.ModelId = null!;
        snapshot.PromptLayoutVersion = null!;
        snapshot.StablePrefixHash = null!;
        snapshot.DirectToolDigest = null!;
        snapshot.ContextPolicyVersion = null!;
        snapshot.BudgetPolicyVersion = null!;
        var nullErrors = ProtocolValidator.Validate(snapshot);
        Assert.Contains(
            nullErrors,
            item => item.Path == "$.providerId"
                    && item.Code == "required");
        Assert.Contains(
            nullErrors,
            item => item.Path == "$.modelId"
                    && item.Code == "required");
        Assert.Contains(
            nullErrors,
            item => item.Path == "$.promptLayoutVersion"
                    && item.Code == "required");
        Assert.Contains(
            nullErrors,
            item => item.Path == "$.stablePrefixHash"
                    && item.Code == "required");
        Assert.Contains(
            nullErrors,
            item => item.Path == "$.directToolDigest"
                    && item.Code == "required");
        Assert.Contains(
            nullErrors,
            item => item.Path == "$.contextPolicyVersion"
                    && item.Code == "required");
        Assert.Contains(
            nullErrors,
            item => item.Path == "$.budgetPolicyVersion"
                    && item.Code == "required");
    }

    [Fact]
    public void RuntimeEventValidatorEnforcesPublicWireBounds()
    {
        var runtimeEvent = new RuntimeEvent
        {
            EventId = "event-1",
            RunId = "run-1",
            Sequence = 0,
            Kind = RuntimeEventKinds.ProviderDispatchStarted,
            Durability = EventDurabilities.Durable,
            RuntimeGeneration = 1,
            ProviderId = "gateway/provider",
            ModelId = "openai/gpt-4.1",
            ReasonCode = "started",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = ProtocolJson.ParseElement("{}")
        };

        Assert.Empty(ProtocolValidator.Validate(runtimeEvent));

        runtimeEvent.Sequence = -1;
        runtimeEvent.Kind = null!;
        runtimeEvent.Durability = "unknown";
        runtimeEvent.RuntimeGeneration = 0;
        runtimeEvent.ProviderId = new string(
            'p',
            ProtocolLimits.MaxProviderIdUnicodeScalars + 1);
        runtimeEvent.ModelId = new string(
            'm',
            ProtocolLimits.MaxModelIdUnicodeScalars + 1);
        runtimeEvent.ReasonCode = new string(
            'r',
            ProtocolLimits.MaxRuntimeEventReasonCodeUnicodeScalars + 1);
        runtimeEvent.Payload = default;

        var errors = ProtocolValidator.Validate(runtimeEvent);
        Assert.Contains(errors, item => item.Path == "$.sequence");
        Assert.Contains(
            errors,
            item => item.Path == "$.kind"
                    && item.Code == "required");
        Assert.Contains(errors, item => item.Path == "$.durability");
        Assert.Contains(errors, item => item.Path == "$.runtimeGeneration");
        Assert.Contains(errors, item => item.Path == "$.providerId");
        Assert.Contains(errors, item => item.Path == "$.modelId");
        Assert.Contains(errors, item => item.Path == "$.reasonCode");
        Assert.Contains(errors, item => item.Path == "$.payload");
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
    public void ObservationValidatorMatchesSchemaEnumsAndBounds()
    {
        var observation = ValidObservation();
        observation.Kind = "invented";
        observation.Trust = "implicit";
        observation.Visibility.Scope = "nearby";
        observation.TtlMs = -1;
        observation.Sequence = -1;
        observation.Priority = 1_001;

        var errors = ProtocolValidator.Validate(observation);

        Assert.Contains(
            errors,
            error => error.Path == "$.kind"
                     && error.Code == "unknown_value");
        Assert.Contains(
            errors,
            error => error.Path == "$.trust"
                     && error.Code == "unknown_value");
        Assert.Contains(
            errors,
            error => error.Path == "$.visibility.scope"
                     && error.Code == "unknown_value");
        Assert.Contains(
            errors,
            error => error.Path == "$.ttlMs"
                     && error.Code == "out_of_range");
        Assert.Contains(
            errors,
            error => error.Path == "$.sequence"
                     && error.Code == "out_of_range");
        Assert.Contains(
            errors,
            error => error.Path == "$.priority"
                     && error.Code == "out_of_range");
    }

    [Theory]
    [InlineData("source", 128)]
    [InlineData("contentType", 128)]
    [InlineData("contentSchemaVersion", 32)]
    [InlineData("stateVersion", 128)]
    [InlineData("cacheKey", 256)]
    public void ObservationStringBoundsCountUnicodeScalars(
        string propertyName,
        int maximumScalars)
    {
        var observation = ValidObservation();
        SetObservationString(
            observation,
            propertyName,
            string.Concat(
                Enumerable.Repeat("\U0001F642", maximumScalars)));

        Assert.DoesNotContain(
            ProtocolValidator.Validate(observation),
            error => error.Path == "$." + propertyName);

        SetObservationString(
            observation,
            propertyName,
            string.Concat(
                Enumerable.Repeat(
                    "\U0001F642",
                    maximumScalars + 1)));
        Assert.Contains(
            ProtocolValidator.Validate(observation),
            error => error.Path == "$." + propertyName
                     && error.Code == "out_of_range");
    }

    [Theory]
    [InlineData("protocolVersion")]
    [InlineData("schemaVersion")]
    [InlineData("observationId")]
    [InlineData("worldId")]
    [InlineData("source")]
    [InlineData("kind")]
    [InlineData("contentType")]
    [InlineData("observedAt")]
    [InlineData("trust")]
    [InlineData("visibility")]
    public void ObservationWireContractRejectsMissingRequiredProperty(
        string propertyName)
    {
        var document = JsonNode.Parse(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "observation.json"))!
            .AsObject();
        Assert.True(document.Remove(propertyName));

        Assert.Throws<JsonException>(
            () => ProtocolJson.DeserializeObservationEnvelope(
                document.ToJsonString()));
    }

    [Fact]
    public void ObservationWireContractRejectsMissingVisibilityScope()
    {
        var document = JsonNode.Parse(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "observation.json"))!
            .AsObject();
        var visibility = document["visibility"]!.AsObject();
        Assert.True(visibility.Remove("scope"));

        Assert.Throws<JsonException>(
            () => ProtocolJson.DeserializeObservationEnvelope(
                document.ToJsonString()));
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
    public void ResourceReferenceBoundsCountUnicodeScalars()
    {
        var observation = ValidObservation();
        observation.Payload = null;
        observation.ResourceRef = new ResourceReference
        {
            Uri = "game://state/actor-1",
            MediaType = string.Concat(
                Enumerable.Repeat("\U0001F642", 128)),
            Digest = string.Concat(
                Enumerable.Repeat("\U0001F642", 256))
        };

        Assert.DoesNotContain(
            ProtocolValidator.Validate(observation),
            error => error.Path is "$.resourceRef.mediaType"
                or "$.resourceRef.digest");

        observation.ResourceRef.MediaType += "\U0001F642";
        observation.ResourceRef.Digest += "\U0001F642";
        var errors = ProtocolValidator.Validate(observation);
        Assert.Contains(
            errors,
            error => error.Path == "$.resourceRef.mediaType"
                     && error.Code == "out_of_range");
        Assert.Contains(
            errors,
            error => error.Path == "$.resourceRef.digest"
                     && error.Code == "out_of_range");
    }

    [Fact]
    public void ActionReceiptRecursivelyValidatesAuthoritativeObservations()
    {
        var receipt = ProtocolJson.DeserializeActionReceipt(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "action-receipt.json"));
        var invalid = ValidObservation();
        invalid.WorldId = string.Empty;
        invalid.Visibility = null!;
        receipt.AuthoritativeObservations.Add(invalid);
        receipt.AuthoritativeObservations.Add(null!);

        var errors = ProtocolValidator.Validate(receipt);

        Assert.Contains(
            errors,
            error => error.Path
                         == "$.authoritativeObservations[0].worldId"
                     && error.Code == "required");
        Assert.Contains(
            errors,
            error => error.Path
                         == "$.authoritativeObservations[0].visibility"
                     && error.Code == "invalid_type");
        Assert.Contains(
            errors,
            error => error.Path == "$.authoritativeObservations[1]"
                     && error.Code == "invalid_type");
        Assert.Throws<JsonException>(
            () => ProtocolValidator.EnsureValid(receipt));
    }

    [Fact]
    public void ActionReceiptAuthoritativeObservationCountMatchesWireLimit()
    {
        var receipt = ProtocolJson.DeserializeActionReceipt(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "action-receipt.json"));
        receipt.AuthoritativeObservations = Enumerable.Range(
                0,
                ProtocolLimits.MaxAuthoritativeObservationsPerReceipt)
            .Select(
                index =>
                {
                    var observation = ValidObservation();
                    observation.ObservationId = $"observation-{index}";
                    return observation;
                })
            .ToList();

        Assert.DoesNotContain(
            ProtocolValidator.Validate(receipt),
            error => error.Path == "$.authoritativeObservations");

        var overflow = ValidObservation();
        overflow.ObservationId = "observation-overflow";
        receipt.AuthoritativeObservations.Add(overflow);

        Assert.Contains(
            ProtocolValidator.Validate(receipt),
            error => error.Path == "$.authoritativeObservations"
                     && error.Code == "out_of_range");
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
    public void DecisionKeyLimitCountsUnicodeScalarsAcrossProtocols()
    {
        var run = ProtocolJson.DeserializeAgentRun(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "agent-run.json"));
        var action = ProtocolJson.DeserializeActionRequest(
            FixtureFiles.Read(
                "v0.2",
                "valid",
                "json-only-tool-loop",
                "action-request.json"));
        var boundary = string.Concat(
            Enumerable.Repeat("\U0001F642", 256));
        run.DecisionKey = boundary;
        action.DecisionKey = boundary;

        Assert.DoesNotContain(
            ProtocolValidator.Validate(run),
            error => error.Path == "$.decisionKey");
        Assert.DoesNotContain(
            ProtocolValidator.Validate(action),
            error => error.Path == "$.decisionKey");

        run.DecisionKey = boundary + "界";
        action.DecisionKey = boundary + "界";
        Assert.Contains(
            ProtocolValidator.Validate(run),
            error => error.Path == "$.decisionKey"
                     && error.Code == "out_of_range");
        Assert.Contains(
            ProtocolValidator.Validate(action),
            error => error.Path == "$.decisionKey"
                     && error.Code == "out_of_range");
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
    public void AgentUsagePreservesExtendedProviderAccounting()
    {
        var usage = new AgentUsage
        {
            InputTokens = 12,
            OutputTokens = 4,
            CostUsd = "0.01",
            ProviderUsageSamples = 2,
            CacheReadTokens = 0,
            CacheWriteTokens = 3,
            CacheMissTokens = 12,
            ReasoningTokens = 2,
            ProviderTotalTokens = 16,
            Availability = UsageAvailabilityStates.CostAvailable
        };

        var roundTrip = ProtocolJson.DeserializeAgentUsage(
            ProtocolJson.Serialize(usage));

        Assert.Empty(ProtocolValidator.Validate(roundTrip));
        Assert.Equal(2, roundTrip.ProviderUsageSamples);
        Assert.Equal(0, roundTrip.CacheReadTokens);
        Assert.Equal(3, roundTrip.CacheWriteTokens);
        Assert.Equal(12, roundTrip.CacheMissTokens);
        Assert.Equal(2, roundTrip.ReasoningTokens);
        Assert.Equal(16, roundTrip.ProviderTotalTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostAvailable,
            roundTrip.Availability);
    }

    [Fact]
    public void LegacyAgentUsageDefaultsToAvailableCost()
    {
        var usage = ProtocolJson.DeserializeAgentUsage(
            """
            {"turns":1,"durationMs":2,"inputTokens":3,"outputTokens":4,"costUsd":"0.01","actions":0,"hasUnaccountedUsage":false,"unaccountedProviderAttempts":0}
            """);

        Assert.Empty(ProtocolValidator.Validate(usage));
        Assert.Equal(0, usage.ProviderUsageSamples);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.CacheWriteTokens);
        Assert.Null(usage.CacheMissTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostAvailable,
            usage.Availability);
    }

    [Fact]
    public void AgentUsageRejectsInvalidAvailabilityAndNegativeDetails()
    {
        var usage = new AgentUsage
        {
            CostUsd = "0.01",
            CacheReadTokens = -1,
            Availability = UsageAvailabilityStates.CostUnavailable
        };

        var errors = ProtocolValidator.Validate(usage);

        Assert.Contains(
            errors,
            error => error.Path == "$.cacheReadTokens"
                     && error.Code == "out_of_range");
        usage.CostUsd = "0";
        usage.CacheReadTokens = null;
        usage.Availability = "guessed";
        Assert.Contains(
            ProtocolValidator.Validate(usage),
            error => error.Path == "$.availability"
                     && error.Code == "unsupported_value");
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

    private static void SetObservationString(
        ObservationEnvelope observation,
        string propertyName,
        string value)
    {
        switch (propertyName)
        {
            case "source":
                observation.Source = value;
                break;
            case "contentType":
                observation.ContentType = value;
                break;
            case "contentSchemaVersion":
                observation.ContentSchemaVersion = value;
                break;
            case "stateVersion":
                observation.StateVersion = value;
                break;
            case "cacheKey":
                observation.CacheKey = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(propertyName));
        }
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class ProtocolAotTests
{
    public static IEnumerable<object[]> WireTypes()
    {
        yield return new object[] { typeof(ResourceReference) };
        yield return new object[] { typeof(VisibilityRule) };
        yield return new object[] { typeof(ObservationEnvelope) };
        yield return new object[] { typeof(ToolDescriptor) };
        yield return new object[] { typeof(ToolInvocation) };
        yield return new object[] { typeof(ActionRequest) };
        yield return new object[] { typeof(ActionReceipt) };
        yield return new object[] { typeof(AgentTrigger) };
        yield return new object[] { typeof(AgentBudget) };
        yield return new object[] { typeof(AgentUsage) };
        yield return new object[] { typeof(AgentRun) };
        yield return new object[] { typeof(AgentDefinition) };
        yield return new object[] { typeof(TurnSnapshot) };
        yield return new object[] { typeof(RuntimeEvent) };
        yield return new object[] { typeof(SkillManifest) };
        yield return new object[] { typeof(CapabilityManifest) };
        yield return new object[] { typeof(PrunedContextItem) };
        yield return new object[] { typeof(ContextBudgetReport) };
        yield return new object[] { typeof(ObservationBatchPayload) };
        yield return new object[] { typeof(RunStartedEventPayload) };
        yield return new object[] { typeof(TurnStartedEventPayload) };
        yield return new object[] { typeof(TurnCompletedEventPayload) };
        yield return new object[] { typeof(RunUsageEventPayload) };
        yield return new object[] { typeof(BudgetEventPayload) };
        yield return new object[] { typeof(ActionReconcilingEventPayload) };
        yield return new object[] { typeof(RuntimeErrorEventPayload) };
    }

    public static IEnumerable<object[]> SchemaDtoPairs()
    {
        yield return Pair("observation-envelope.schema.json", typeof(ObservationEnvelope));
        yield return Pair("tool-descriptor.schema.json", typeof(ToolDescriptor));
        yield return Pair("tool-invocation.schema.json", typeof(ToolInvocation));
        yield return Pair("action-request.schema.json", typeof(ActionRequest));
        yield return Pair("action-receipt.schema.json", typeof(ActionReceipt));
        yield return Pair("agent-run.schema.json", typeof(AgentRun));
        yield return Pair("agent-definition.schema.json", typeof(AgentDefinition));
        yield return Pair("turn-snapshot.schema.json", typeof(TurnSnapshot));
        yield return Pair("runtime-event.schema.json", typeof(RuntimeEvent));
        yield return Pair("skill-manifest.schema.json", typeof(SkillManifest));
        yield return Pair("capability-manifest.schema.json", typeof(CapabilityManifest));
        yield return Pair("context-budget-report.schema.json", typeof(ContextBudgetReport));
    }

    public static IEnumerable<object[]> NestedSchemaDtoPairs()
    {
        yield return NestedPair(
            "common.schema.json",
            typeof(ResourceReference),
            "$defs",
            "resourceRef");
        yield return NestedPair(
            "observation-envelope.schema.json",
            typeof(VisibilityRule),
            "properties",
            "visibility");
        yield return NestedPair(
            "agent-run.schema.json",
            typeof(AgentTrigger),
            "properties",
            "trigger");
        yield return NestedPair(
            "agent-run.schema.json",
            typeof(AgentBudget),
            "$defs",
            "budget");
        yield return NestedPair(
            "agent-run.schema.json",
            typeof(AgentUsage),
            "$defs",
            "usage");
        yield return NestedPair(
            "context-budget-report.schema.json",
            typeof(PrunedContextItem),
            "properties",
            "pruned",
            "items");
    }

    [Fact]
    public void ReflectionSerializationIsDisabledAndUnknownTypesHaveNoFallback()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Throws<InvalidOperationException>(
            () => JsonSerializer.Serialize(
                new UnknownWireType(),
                typeof(UnknownWireType),
                ProtocolJsonContext.Default));
    }

    [Fact]
    public void ProtocolJsonHasNoPublicGenericEscapeHatch()
    {
        var genericMethods = typeof(ProtocolJson)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.IsGenericMethodDefinition)
            .ToArray();

        Assert.Empty(genericMethods);
    }

    [Theory]
    [MemberData(nameof(WireTypes))]
    public void EveryPublicWireDtoHasGeneratedMetadataAndTypedEntryPoints(Type wireType)
    {
        Assert.NotNull(ProtocolJsonContext.Default.GetTypeInfo(wireType));

        var methods = typeof(ProtocolJson).GetMethods(BindingFlags.Public | BindingFlags.Static);
        Assert.Contains(
            methods,
            method => method.Name == nameof(ProtocolJson.Serialize)
                && HasSingleParameter(method, wireType));
        Assert.Contains(
            methods,
            method => method.Name == nameof(ProtocolJson.ToElement)
                && HasSingleParameter(method, wireType));
        Assert.Contains(
            methods,
            method => method.Name == $"Deserialize{wireType.Name}"
                && HasSingleParameter(method, typeof(string))
                && method.ReturnType == wireType);
    }

    [Theory]
    [MemberData(nameof(SchemaDtoPairs))]
    public void TopLevelDtoFieldsExactlyMatchItsSchema(string schemaFile, Type dtoType)
    {
        using var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureFiles.SchemaDirectory, schemaFile)));

        AssertSchemaFieldsMatchDto(
            schema.RootElement.GetProperty("properties"),
            dtoType);
    }

    [Theory]
    [MemberData(nameof(NestedSchemaDtoPairs))]
    public void NestedDtoFieldsExactlyMatchItsSchema(
        string schemaFile,
        Type dtoType,
        string[] fragmentPath)
    {
        using var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureFiles.SchemaDirectory, schemaFile)));
        var fragment = schema.RootElement;
        foreach (var segment in fragmentPath)
        {
            fragment = fragment.GetProperty(segment);
        }

        AssertSchemaFieldsMatchDto(fragment.GetProperty("properties"), dtoType);
    }

    private static void AssertSchemaFieldsMatchDto(
        JsonElement schemaProperties,
        Type dtoType)
    {
        var schemaFields = schemaProperties
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var dtoFields = dtoType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            schemaFields.SetEquals(dtoFields),
            $"{dtoType.Name}: schema-only [{string.Join(", ", schemaFields.Except(dtoFields))}], "
            + $"DTO-only [{string.Join(", ", dtoFields.Except(schemaFields))}]");
    }

    [Fact]
    public void ExtensionsAndContentSchemaVersionSurviveReflectionFreeRoundTrip()
    {
        var run = ProtocolJson.DeserializeAgentRun(
            FixtureFiles.Read("v0.2", "valid", "json-only-tool-loop", "agent-run.json"));
        var observation = ProtocolJson.DeserializeObservationEnvelope(
            FixtureFiles.Read("v0.2", "valid", "json-only-tool-loop", "observation.json"));

        var roundTrippedRun = ProtocolJson.DeserializeAgentRun(ProtocolJson.Serialize(run));
        var roundTrippedObservation = ProtocolJson.DeserializeObservationEnvelope(
            ProtocolJson.Serialize(observation));

        Assert.True(
            roundTrippedRun.Extensions["futurePolicy"].GetProperty("enabled").GetBoolean());
        Assert.Equal("1", roundTrippedObservation.ContentSchemaVersion);
    }

    [Fact]
    public void UnknownTopLevelFieldsAreRejectedInsteadOfSilentlyDropped()
    {
        var json = FixtureFiles.Read(
            "v0.2",
            "valid",
            "json-only-tool-loop",
            "agent-run.json");
        var withUnknownField = json.Replace(
            "\"extensions\": {",
            "\"unregisteredFutureField\": true, \"extensions\": {",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(
            () => ProtocolJson.DeserializeAgentRun(withUnknownField));
    }

    [Fact]
    public void LifecycleStatesAndCompletionIntentUseDistinctWireFields()
    {
        var run = ProtocolJson.DeserializeAgentRun(
            FixtureFiles.Read("v0.2", "valid", "json-only-tool-loop", "agent-run.json"));
        run.State = RunStates.Cancelling;
        run.CompletionIntent = CompletionIntents.Cancelled;

        var json = ProtocolJson.ToElement(run);

        Assert.Equal("cancelling", json.GetProperty("state").GetString());
        Assert.Equal("cancelled", json.GetProperty("completionIntent").GetString());
        Assert.Empty(ProtocolValidator.Validate(run));

        run.CompletionIntent = "invented";
        Assert.Contains(
            ProtocolValidator.Validate(run),
            error => error.Path == "$.completionIntent" && error.Code == "unknown_value");
    }

    private static object[] Pair(string schemaFile, Type dtoType) =>
        new object[] { schemaFile, dtoType };

    private static object[] NestedPair(
        string schemaFile,
        Type dtoType,
        params string[] fragmentPath) =>
        new object[] { schemaFile, dtoType, fragmentPath };

    private static bool HasSingleParameter(MethodInfo method, Type parameterType)
    {
        var parameters = method.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == parameterType;
    }

    private sealed class UnknownWireType
    {
        public string Value { get; set; } = "must-not-reflect";
    }
}

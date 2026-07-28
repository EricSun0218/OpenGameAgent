using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GameAgent.Protocol;

/// <summary>
/// Reflection-free serialization entry points for the protocol's closed wire type set.
/// Arbitrary POCO serialization is deliberately not part of this API.
/// </summary>
public static class ProtocolJson
{
    public static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static string Serialize(JsonElement value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.JsonElement);

    public static JsonElement ToElement(JsonElement value) => value.Clone();

    public static JsonElement DeserializeJsonElement(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.JsonElement);

    public static string Serialize(ResourceReference value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.ResourceReference);

    public static JsonElement ToElement(ResourceReference value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.ResourceReference);

    public static ResourceReference DeserializeResourceReference(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.ResourceReference);

    public static string Serialize(VisibilityRule value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.VisibilityRule);

    public static JsonElement ToElement(VisibilityRule value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.VisibilityRule);

    public static VisibilityRule DeserializeVisibilityRule(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.VisibilityRule);

    public static string Serialize(ObservationEnvelope value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.ObservationEnvelope);

    public static JsonElement ToElement(ObservationEnvelope value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.ObservationEnvelope);

    public static ObservationEnvelope DeserializeObservationEnvelope(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.ObservationEnvelope);

    public static string Serialize(ToolDescriptor value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.ToolDescriptor);

    public static JsonElement ToElement(ToolDescriptor value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.ToolDescriptor);

    public static ToolDescriptor DeserializeToolDescriptor(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.ToolDescriptor);

    public static string Serialize(ToolInvocation value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.ToolInvocation);

    public static JsonElement ToElement(ToolInvocation value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.ToolInvocation);

    public static ToolInvocation DeserializeToolInvocation(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.ToolInvocation);

    public static string Serialize(ActionRequest value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.ActionRequest);

    public static JsonElement ToElement(ActionRequest value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.ActionRequest);

    public static ActionRequest DeserializeActionRequest(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.ActionRequest);

    public static string Serialize(ActionReceipt value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.ActionReceipt);

    public static JsonElement ToElement(ActionReceipt value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.ActionReceipt);

    public static ActionReceipt DeserializeActionReceipt(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.ActionReceipt);

    public static string Serialize(AgentTrigger value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.AgentTrigger);

    public static JsonElement ToElement(AgentTrigger value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.AgentTrigger);

    public static AgentTrigger DeserializeAgentTrigger(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.AgentTrigger);

    public static string Serialize(AgentBudget value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.AgentBudget);

    public static JsonElement ToElement(AgentBudget value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.AgentBudget);

    public static AgentBudget DeserializeAgentBudget(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.AgentBudget);

    public static string Serialize(AgentUsage value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.AgentUsage);

    public static JsonElement ToElement(AgentUsage value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.AgentUsage);

    public static AgentUsage DeserializeAgentUsage(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.AgentUsage);

    public static string Serialize(AgentRun value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.AgentRun);

    public static JsonElement ToElement(AgentRun value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.AgentRun);

    public static AgentRun DeserializeAgentRun(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.AgentRun);

    public static string Serialize(AgentDefinition value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.AgentDefinition);

    public static JsonElement ToElement(AgentDefinition value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.AgentDefinition);

    public static AgentDefinition DeserializeAgentDefinition(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.AgentDefinition);

    public static string Serialize(TurnSnapshot value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.TurnSnapshot);

    public static JsonElement ToElement(TurnSnapshot value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.TurnSnapshot);

    public static TurnSnapshot DeserializeTurnSnapshot(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.TurnSnapshot);

    public static string Serialize(RuntimeEvent value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.RuntimeEvent);

    public static JsonElement ToElement(RuntimeEvent value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.RuntimeEvent);

    public static RuntimeEvent DeserializeRuntimeEvent(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.RuntimeEvent);

    public static string Serialize(SkillManifest value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.SkillManifest);

    public static JsonElement ToElement(SkillManifest value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.SkillManifest);

    public static SkillManifest DeserializeSkillManifest(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.SkillManifest);

    public static string Serialize(CapabilityManifest value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.CapabilityManifest);

    public static JsonElement ToElement(CapabilityManifest value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.CapabilityManifest);

    public static CapabilityManifest DeserializeCapabilityManifest(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.CapabilityManifest);

    public static string Serialize(PrunedContextItem value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.PrunedContextItem);

    public static JsonElement ToElement(PrunedContextItem value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.PrunedContextItem);

    public static PrunedContextItem DeserializePrunedContextItem(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.PrunedContextItem);

    public static string Serialize(ContextBudgetReport value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.ContextBudgetReport);

    public static JsonElement ToElement(ContextBudgetReport value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.ContextBudgetReport);

    public static ContextBudgetReport DeserializeContextBudgetReport(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.ContextBudgetReport);

    public static string Serialize(ObservationBatchPayload value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.ObservationBatchPayload);

    public static JsonElement ToElement(ObservationBatchPayload value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.ObservationBatchPayload);

    public static ObservationBatchPayload DeserializeObservationBatchPayload(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.ObservationBatchPayload);

    public static string Serialize(RunStartedEventPayload value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.RunStartedEventPayload);

    public static JsonElement ToElement(RunStartedEventPayload value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.RunStartedEventPayload);

    public static RunStartedEventPayload DeserializeRunStartedEventPayload(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.RunStartedEventPayload);

    public static string Serialize(TurnStartedEventPayload value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.TurnStartedEventPayload);

    public static JsonElement ToElement(TurnStartedEventPayload value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.TurnStartedEventPayload);

    public static TurnStartedEventPayload DeserializeTurnStartedEventPayload(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.TurnStartedEventPayload);

    public static string Serialize(TurnCompletedEventPayload value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.TurnCompletedEventPayload);

    public static JsonElement ToElement(TurnCompletedEventPayload value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.TurnCompletedEventPayload);

    public static TurnCompletedEventPayload DeserializeTurnCompletedEventPayload(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.TurnCompletedEventPayload);

    public static string Serialize(RunUsageEventPayload value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.RunUsageEventPayload);

    public static JsonElement ToElement(RunUsageEventPayload value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.RunUsageEventPayload);

    public static RunUsageEventPayload DeserializeRunUsageEventPayload(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.RunUsageEventPayload);

    public static string Serialize(BudgetEventPayload value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.BudgetEventPayload);

    public static JsonElement ToElement(BudgetEventPayload value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.BudgetEventPayload);

    public static BudgetEventPayload DeserializeBudgetEventPayload(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.BudgetEventPayload);

    public static string Serialize(ActionReconcilingEventPayload value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.ActionReconcilingEventPayload);

    public static JsonElement ToElement(ActionReconcilingEventPayload value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.ActionReconcilingEventPayload);

    public static ActionReconcilingEventPayload DeserializeActionReconcilingEventPayload(
        string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.ActionReconcilingEventPayload);

    public static string Serialize(RuntimeErrorEventPayload value) =>
        SerializeKnown(value, ProtocolJsonContext.Default.RuntimeErrorEventPayload);

    public static JsonElement ToElement(RuntimeErrorEventPayload value) =>
        ToElementKnown(value, ProtocolJsonContext.Default.RuntimeErrorEventPayload);

    public static RuntimeErrorEventPayload DeserializeRuntimeErrorEventPayload(string json) =>
        DeserializeKnown(json, ProtocolJsonContext.Default.RuntimeErrorEventPayload);

    private static string SerializeKnown<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        return JsonSerializer.Serialize(value, typeInfo);
    }

    private static JsonElement ToElementKnown<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        return JsonSerializer.SerializeToElement(value, typeInfo);
    }

    private static T DeserializeKnown<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        return JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new JsonException($"Unable to deserialize {typeInfo.Type.Name}.");
    }
}

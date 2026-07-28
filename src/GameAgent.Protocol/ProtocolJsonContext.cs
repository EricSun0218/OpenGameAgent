using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameAgent.Protocol;

/// <summary>
/// Closed-world JSON metadata for the public wire contract. Keeping this list explicit
/// prevents accidental reflection fallback under Unity IL2CPP and NativeAOT.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ResourceReference))]
[JsonSerializable(typeof(VisibilityRule))]
[JsonSerializable(typeof(ObservationEnvelope))]
[JsonSerializable(typeof(ToolDescriptor))]
[JsonSerializable(typeof(ToolInvocation))]
[JsonSerializable(typeof(ActionRequest))]
[JsonSerializable(typeof(ActionReceipt))]
[JsonSerializable(typeof(AgentTrigger))]
[JsonSerializable(typeof(AgentBudget))]
[JsonSerializable(typeof(AgentUsage))]
[JsonSerializable(typeof(AgentRun))]
[JsonSerializable(typeof(AgentDefinition))]
[JsonSerializable(typeof(TurnSnapshot))]
[JsonSerializable(typeof(RuntimeEvent))]
[JsonSerializable(typeof(SkillManifest))]
[JsonSerializable(typeof(CapabilityManifest))]
[JsonSerializable(typeof(PrunedContextItem))]
[JsonSerializable(typeof(ContextBudgetReport))]
[JsonSerializable(typeof(ObservationBatchPayload))]
[JsonSerializable(typeof(RunStartedEventPayload))]
[JsonSerializable(typeof(TurnStartedEventPayload))]
[JsonSerializable(typeof(TurnCompletedEventPayload))]
[JsonSerializable(typeof(RunUsageEventPayload))]
[JsonSerializable(typeof(BudgetEventPayload))]
[JsonSerializable(typeof(ActionReconcilingEventPayload))]
[JsonSerializable(typeof(RuntimeErrorEventPayload))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext
{
}

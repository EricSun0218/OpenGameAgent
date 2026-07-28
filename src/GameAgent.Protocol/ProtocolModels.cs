using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameAgent.Protocol;

public abstract class VersionedProtocolObject
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = ProtocolConstants.ProtocolVersion;

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = ProtocolConstants.SchemaVersion;

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement> Extensions { get; set; } = new();
}

public sealed class ResourceReference
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; set; }
}

public sealed class VisibilityRule
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "world";

    [JsonPropertyName("audienceIds")]
    public List<string> AudienceIds { get; set; } = new();
}

public sealed class ObservationEnvelope : VersionedProtocolObject
{
    [JsonPropertyName("observationId")]
    public string ObservationId { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "custom";

    [JsonPropertyName("subjectIds")]
    public List<string> SubjectIds { get; set; } = new();

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "application/json";

    [JsonPropertyName("schemaRef")]
    public string? SchemaRef { get; set; }

    [JsonPropertyName("contentSchemaVersion")]
    public string? ContentSchemaVersion { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    [JsonPropertyName("resourceRef")]
    public ResourceReference? ResourceRef { get; set; }

    [JsonPropertyName("observedAt")]
    public DateTimeOffset ObservedAt { get; set; }

    [JsonPropertyName("ttlMs")]
    public long? TtlMs { get; set; }

    [JsonPropertyName("sequence")]
    public long? Sequence { get; set; }

    [JsonPropertyName("stateVersion")]
    public string? StateVersion { get; set; }

    [JsonPropertyName("trust")]
    public string Trust { get; set; } = "untrusted";

    [JsonPropertyName("visibility")]
    public VisibilityRule Visibility { get; set; } = new();

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("cacheKey")]
    public string? CacheKey { get; set; }

}

public sealed class ToolDescriptor : VersionedProtocolObject
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parametersSchema")]
    public JsonElement ParametersSchema { get; set; }

    [JsonPropertyName("resultSchema")]
    public JsonElement? ResultSchema { get; set; }

    [JsonPropertyName("effect")]
    public string Effect { get; set; } = ToolEffects.PureRead;

    [JsonPropertyName("conflictScopes")]
    public List<string> ConflictScopes { get; set; } = new();

    [JsonPropertyName("threadAffinity")]
    public string ThreadAffinity { get; set; } = ThreadAffinities.AnyThread;

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 30_000;

    [JsonPropertyName("retryPolicy")]
    public string RetryPolicy { get; set; } = ToolRetryPolicies.Never;

    [JsonPropertyName("idempotencyPolicy")]
    public string IdempotencyPolicy { get; set; } =
        ToolIdempotencyPolicies.None;

    [JsonPropertyName("toolset")]
    public string Toolset { get; set; } = "default";

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = ToolVisibilities.Direct;

}

public sealed class ToolInvocation : VersionedProtocolObject
{
    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("turnId")]
    public string TurnId { get; set; } = string.Empty;

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; set; } = string.Empty;

    [JsonPropertyName("toolName")]
    public string ToolName { get; set; } = string.Empty;

    [JsonPropertyName("toolVersion")]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }

    [JsonPropertyName("effect")]
    public string Effect { get; set; } = ToolEffects.PureRead;

    [JsonPropertyName("resolvedConflictKeys")]
    public List<string> ResolvedConflictKeys { get; set; } = new();

    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ActionRequest : VersionedProtocolObject
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("turnId")]
    public string TurnId { get; set; } = string.Empty;

    [JsonPropertyName("toolCallId")]
    public string ToolCallId { get; set; } = string.Empty;

    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("actionName")]
    public string ActionName { get; set; } = string.Empty;

    [JsonPropertyName("actionVersion")]
    public string ActionVersion { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }

    [JsonPropertyName("basedOnStateVersion")]
    public string? BasedOnStateVersion { get; set; }

    [JsonPropertyName("expectedEffects")]
    public List<string> ExpectedEffects { get; set; } = new();

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; set; }

    [JsonPropertyName("deadline")]
    public DateTimeOffset? Deadline { get; set; }
}

public sealed class ActionReceipt : VersionedProtocolObject
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = ReceiptStatuses.Unknown;

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("stateDiff")]
    public JsonElement? StateDiff { get; set; }

    [JsonPropertyName("authoritativeObservations")]
    public List<ObservationEnvelope> AuthoritativeObservations { get; set; } = new();

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; }

    [JsonPropertyName("committedAt")]
    public DateTimeOffset? CommittedAt { get; set; }

    [JsonPropertyName("receivedAt")]
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class AgentTrigger
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "manual";

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("scheduledFor")]
    public DateTimeOffset? ScheduledFor { get; set; }
}

public sealed class AgentBudget
{
    [JsonPropertyName("maxTurns")]
    public int MaxTurns { get; set; } = 8;

    [JsonPropertyName("maxDurationMs")]
    public long MaxDurationMs { get; set; } = 30_000;

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 8_000;

    [JsonPropertyName("maxCostUsd")]
    public string MaxCostUsd { get; set; } = "1";

    [JsonPropertyName("maxActions")]
    public int MaxActions { get; set; } = 8;
}

public sealed class AgentUsage
{
    [JsonPropertyName("turns")]
    public int Turns { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("inputTokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("costUsd")]
    public string CostUsd { get; set; } = "0";

    [JsonPropertyName("actions")]
    public int Actions { get; set; }

    [JsonPropertyName("hasUnaccountedUsage")]
    public bool HasUnaccountedUsage { get; set; }

    [JsonPropertyName("unaccountedProviderAttempts")]
    public int UnaccountedProviderAttempts { get; set; }
}

public sealed class AgentRun : VersionedProtocolObject
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("trigger")]
    public AgentTrigger Trigger { get; set; } = new();

    [JsonPropertyName("triggerObservationIds")]
    public List<string> TriggerObservationIds { get; set; } = new();

    [JsonPropertyName("decisionKey")]
    public string? DecisionKey { get; set; }

    [JsonPropertyName("batchId")]
    public string? BatchId { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = RunStates.Queued;

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("currentTurnId")]
    public string? CurrentTurnId { get; set; }

    [JsonPropertyName("runtimeGeneration")]
    public long RuntimeGeneration { get; set; } = 1;

    [JsonPropertyName("budget")]
    public AgentBudget Budget { get; set; } = new();

    [JsonPropertyName("usage")]
    public AgentUsage Usage { get; set; } = new();

    [JsonPropertyName("pendingOperationIds")]
    public List<string> PendingOperationIds { get; set; } = new();

    [JsonPropertyName("terminalReason")]
    public string? TerminalReason { get; set; }

    [JsonPropertyName("completionIntent")]
    public string? CompletionIntent { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AgentDefinition : VersionedProtocolObject
{
    [JsonPropertyName("agentDefinitionId")]
    public string AgentDefinitionId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("identity")]
    public JsonElement Identity { get; set; }

    [JsonPropertyName("behaviorPolicyRef")]
    public string? BehaviorPolicyRef { get; set; }

    [JsonPropertyName("toolsets")]
    public List<string> Toolsets { get; set; } = new();

    [JsonPropertyName("skills")]
    public List<string> Skills { get; set; } = new();

    [JsonPropertyName("contextPolicyRef")]
    public string? ContextPolicyRef { get; set; }

    [JsonPropertyName("memoryPolicyRef")]
    public string? MemoryPolicyRef { get; set; }

    [JsonPropertyName("providerPolicyRef")]
    public string? ProviderPolicyRef { get; set; }

    [JsonPropertyName("budgets")]
    public JsonElement Budgets { get; set; }
}

public sealed class TurnSnapshot : VersionedProtocolObject
{
    [JsonPropertyName("turnId")]
    public string TurnId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("runtimeGeneration")]
    public long RuntimeGeneration { get; set; }

    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("promptLayoutVersion")]
    public string PromptLayoutVersion { get; set; } = string.Empty;

    [JsonPropertyName("stablePrefixHash")]
    public string StablePrefixHash { get; set; } = string.Empty;

    [JsonPropertyName("skillGeneration")]
    public long SkillGeneration { get; set; }

    [JsonPropertyName("skillDigests")]
    public List<string> SkillDigests { get; set; } = new();

    [JsonPropertyName("toolCatalogGeneration")]
    public long ToolCatalogGeneration { get; set; }

    [JsonPropertyName("directToolDigest")]
    public string DirectToolDigest { get; set; } = string.Empty;

    [JsonPropertyName("deferredCatalogDigest")]
    public string? DeferredCatalogDigest { get; set; }

    [JsonPropertyName("contextPolicyVersion")]
    public string ContextPolicyVersion { get; set; } = string.Empty;

    [JsonPropertyName("budgetPolicyVersion")]
    public string BudgetPolicyVersion { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RuntimeEvent : VersionedProtocolObject
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("turnId")]
    public string? TurnId { get; set; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("durability")]
    public string Durability { get; set; } = EventDurabilities.Durable;

    [JsonPropertyName("runtimeGeneration")]
    public long RuntimeGeneration { get; set; } = 1;

    [JsonPropertyName("attemptId")]
    public string? AttemptId { get; set; }

    [JsonPropertyName("streamAttemptId")]
    public string? StreamAttemptId { get; set; }

    [JsonPropertyName("providerId")]
    public string? ProviderId { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("transportDialect")]
    public string? TransportDialect { get; set; }

    [JsonPropertyName("providerCapabilityDigest")]
    public string? ProviderCapabilityDigest { get; set; }

    [JsonPropertyName("providerRouteDigest")]
    public string? ProviderRouteDigest { get; set; }

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

public sealed class SkillManifest : VersionedProtocolObject
{
    [JsonPropertyName("skillId")]
    public string SkillId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    public string Digest { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("promptFragments")]
    public List<string> PromptFragments { get; set; } = new();

    [JsonPropertyName("requiredToolRefs")]
    public List<string> RequiredToolRefs { get; set; } = new();

    [JsonPropertyName("optionalToolRefs")]
    public List<string> OptionalToolRefs { get; set; } = new();

    [JsonPropertyName("contextProviderRefs")]
    public List<string> ContextProviderRefs { get; set; } = new();

    [JsonPropertyName("resourceRefs")]
    public List<ResourceReference> ResourceRefs { get; set; } = new();

    [JsonPropertyName("capabilityRequirements")]
    public JsonElement CapabilityRequirements { get; set; }

    [JsonPropertyName("trust")]
    public string Trust { get; set; } = "untrusted";

    [JsonPropertyName("activationPolicy")]
    public JsonElement ActivationPolicy { get; set; }
}

public sealed class CapabilityManifest : VersionedProtocolObject
{
    [JsonPropertyName("protocolRange")]
    public string ProtocolRange { get; set; } = string.Empty;

    [JsonPropertyName("runtimeVersion")]
    public string RuntimeVersion { get; set; } = string.Empty;

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "headless";

    [JsonPropertyName("engineVersion")]
    public string EngineVersion { get; set; } = string.Empty;

    [JsonPropertyName("adapterVersion")]
    public string AdapterVersion { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = string.Empty;

    [JsonPropertyName("contentTypes")]
    public List<string> ContentTypes { get; set; } = new();

    [JsonPropertyName("codecs")]
    public List<string> Codecs { get; set; } = new();

    [JsonPropertyName("transports")]
    public List<string> Transports { get; set; } = new();

    [JsonPropertyName("maxMessageBytes")]
    public long MaxMessageBytes { get; set; }

    [JsonPropertyName("maxBatchSize")]
    public int MaxBatchSize { get; set; }

    [JsonPropertyName("streaming")]
    public bool Streaming { get; set; }

    [JsonPropertyName("persistenceLevel")]
    public string PersistenceLevel { get; set; } = "memory";

    [JsonPropertyName("toolEffects")]
    public List<string> ToolEffects { get; set; } = new();

    [JsonPropertyName("threadAffinities")]
    public List<string> ThreadAffinities { get; set; } = new();

    [JsonPropertyName("receiptReconciliation")]
    public bool ReceiptReconciliation { get; set; }

    [JsonPropertyName("providerCapabilities")]
    public JsonElement ProviderCapabilities { get; set; }
}

public sealed class PrunedContextItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("reasonCode")]
    public string ReasonCode { get; set; } = string.Empty;
}

public sealed class ContextBudgetReport : VersionedProtocolObject
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("turnId")]
    public string TurnId { get; set; } = string.Empty;

    [JsonPropertyName("inputCount")]
    public int InputCount { get; set; }

    [JsonPropertyName("selectedIds")]
    public List<string> SelectedIds { get; set; } = new();

    [JsonPropertyName("deferredIds")]
    public List<string> DeferredIds { get; set; } = new();

    [JsonPropertyName("pruned")]
    public List<PrunedContextItem> Pruned { get; set; } = new();

    [JsonPropertyName("externalized")]
    public List<ResourceReference> Externalized { get; set; } = new();

    [JsonPropertyName("estimatedTokens")]
    public int EstimatedTokens { get; set; }

    [JsonPropertyName("actualTokens")]
    public int? ActualTokens { get; set; }

    [JsonPropertyName("budgetLimit")]
    public int BudgetLimit { get; set; }

    [JsonPropertyName("reasonCodes")]
    public List<string> ReasonCodes { get; set; } = new();
}

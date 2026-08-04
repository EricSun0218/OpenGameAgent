using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameAgent.Protocol;

public abstract class VersionedProtocolObject
{
    [JsonPropertyName("protocolVersion")]
    [JsonRequired]
    public string ProtocolVersion { get; set; } = ProtocolConstants.ProtocolVersion;

    [JsonPropertyName("schemaVersion")]
    [JsonRequired]
    public string SchemaVersion { get; set; } = ProtocolConstants.SchemaVersion;

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement> Extensions { get; set; } = new();
}

public sealed class ResourceReference
{
    [JsonPropertyName("uri")]
    [JsonRequired]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("mediaType")]
    [JsonRequired]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; set; }
}

public sealed class VisibilityRule
{
    [JsonPropertyName("scope")]
    [JsonRequired]
    public string Scope { get; set; } = "world";

    [JsonPropertyName("audienceIds")]
    public List<string> AudienceIds { get; set; } = new();
}

public sealed class ObservationEnvelope : VersionedProtocolObject
{
    [JsonPropertyName("observationId")]
    [JsonRequired]
    public string ObservationId { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    [JsonRequired]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("source")]
    [JsonRequired]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    [JsonRequired]
    public string Kind { get; set; } = "custom";

    [JsonPropertyName("subjectIds")]
    public List<string> SubjectIds { get; set; } = new();

    [JsonPropertyName("contentType")]
    [JsonRequired]
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
    [JsonRequired]
    public DateTimeOffset ObservedAt { get; set; }

    [JsonPropertyName("ttlMs")]
    public long? TtlMs { get; set; }

    [JsonPropertyName("sequence")]
    public long? Sequence { get; set; }

    [JsonPropertyName("stateVersion")]
    public string? StateVersion { get; set; }

    [JsonPropertyName("trust")]
    [JsonRequired]
    public string Trust { get; set; } = "untrusted";

    [JsonPropertyName("visibility")]
    [JsonRequired]
    public VisibilityRule Visibility { get; set; } = new();

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("cacheKey")]
    public string? CacheKey { get; set; }

}

public sealed class ToolDescriptor : VersionedProtocolObject
{
    [JsonPropertyName("name")]
    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonRequired]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonRequired]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parametersSchema")]
    [JsonRequired]
    public JsonElement ParametersSchema { get; set; }

    [JsonPropertyName("resultSchema")]
    public JsonElement? ResultSchema { get; set; }

    [JsonPropertyName("effect")]
    [JsonRequired]
    public string Effect { get; set; } = ToolEffects.PureRead;

    [JsonPropertyName("conflictScopes")]
    [JsonRequired]
    public List<string> ConflictScopes { get; set; } = new();

    [JsonPropertyName("threadAffinity")]
    [JsonRequired]
    public string ThreadAffinity { get; set; } = ThreadAffinities.AnyThread;

    [JsonPropertyName("timeoutMs")]
    [JsonRequired]
    public int TimeoutMs { get; set; } = 30_000;

    [JsonPropertyName("retryPolicy")]
    [JsonRequired]
    public string RetryPolicy { get; set; } = ToolRetryPolicies.Never;

    [JsonPropertyName("idempotencyPolicy")]
    [JsonRequired]
    public string IdempotencyPolicy { get; set; } =
        ToolIdempotencyPolicies.None;

    [JsonPropertyName("toolset")]
    [JsonRequired]
    public string Toolset { get; set; } = "default";

    [JsonPropertyName("visibility")]
    [JsonRequired]
    public string Visibility { get; set; } = ToolVisibilities.Direct;

}

public sealed class ToolInvocation : VersionedProtocolObject
{
    [JsonPropertyName("toolCallId")]
    [JsonRequired]
    public string ToolCallId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    [JsonRequired]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("turnId")]
    [JsonRequired]
    public string TurnId { get; set; } = string.Empty;

    [JsonPropertyName("attemptId")]
    [JsonRequired]
    public string AttemptId { get; set; } = string.Empty;

    [JsonPropertyName("toolName")]
    [JsonRequired]
    public string ToolName { get; set; } = string.Empty;

    [JsonPropertyName("toolVersion")]
    [JsonRequired]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    [JsonRequired]
    public JsonElement Arguments { get; set; }

    [JsonPropertyName("effect")]
    [JsonRequired]
    public string Effect { get; set; } = ToolEffects.PureRead;

    [JsonPropertyName("resolvedConflictKeys")]
    [JsonRequired]
    public List<string> ResolvedConflictKeys { get; set; } = new();

    [JsonPropertyName("sequence")]
    [JsonRequired]
    public long Sequence { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonRequired]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ActionRequest : VersionedProtocolObject
{
    [JsonPropertyName("operationId")]
    [JsonRequired]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    [JsonRequired]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("turnId")]
    [JsonRequired]
    public string TurnId { get; set; } = string.Empty;

    [JsonPropertyName("toolCallId")]
    [JsonRequired]
    public string ToolCallId { get; set; } = string.Empty;

    [JsonPropertyName("agentId")]
    [JsonRequired]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    [JsonRequired]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("actionName")]
    [JsonRequired]
    public string ActionName { get; set; } = string.Empty;

    [JsonPropertyName("actionVersion")]
    [JsonRequired]
    public string ActionVersion { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    [JsonRequired]
    public JsonElement Arguments { get; set; }

    [JsonPropertyName("basedOnStateVersion")]
    public string? BasedOnStateVersion { get; set; }

    [JsonPropertyName("decisionKey")]
    public string? DecisionKey { get; set; }

    [JsonPropertyName("batchId")]
    public string? BatchId { get; set; }

    [JsonPropertyName("expectedEffects")]
    public List<string> ExpectedEffects { get; set; } = new();

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("requestedAt")]
    [JsonRequired]
    public DateTimeOffset RequestedAt { get; set; }

    [JsonPropertyName("deadline")]
    public DateTimeOffset? Deadline { get; set; }
}

public sealed class ActionReceipt : VersionedProtocolObject
{
    [JsonPropertyName("operationId")]
    [JsonRequired]
    public string OperationId { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    [JsonRequired]
    public long Revision { get; set; }

    [JsonPropertyName("status")]
    [JsonRequired]
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
    [JsonRequired]
    public bool Retryable { get; set; }

    [JsonPropertyName("committedAt")]
    public DateTimeOffset? CommittedAt { get; set; }

    [JsonPropertyName("receivedAt")]
    [JsonRequired]
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class AgentTrigger
{
    [JsonPropertyName("type")]
    [JsonRequired]
    public string Type { get; set; } = "manual";

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("scheduledFor")]
    public DateTimeOffset? ScheduledFor { get; set; }
}

public sealed class AgentBudget
{
    [JsonPropertyName("maxTurns")]
    [JsonRequired]
    public int MaxTurns { get; set; } = 8;

    [JsonPropertyName("maxDurationMs")]
    [JsonRequired]
    public long MaxDurationMs { get; set; } = 30_000;

    [JsonPropertyName("maxTokens")]
    [JsonRequired]
    public int MaxTokens { get; set; } = 8_000;

    [JsonPropertyName("maxCostUsd")]
    [JsonRequired]
    public string MaxCostUsd { get; set; } = "1";

    [JsonPropertyName("maxActions")]
    [JsonRequired]
    public int MaxActions { get; set; } = 8;
}

public sealed class AgentUsage
{
    [JsonPropertyName("turns")]
    [JsonRequired]
    public int Turns { get; set; }

    [JsonPropertyName("durationMs")]
    [JsonRequired]
    public long DurationMs { get; set; }

    [JsonPropertyName("inputTokens")]
    [JsonRequired]
    public int InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    [JsonRequired]
    public int OutputTokens { get; set; }

    [JsonPropertyName("costUsd")]
    [JsonRequired]
    public string CostUsd { get; set; } = "0";

    [JsonPropertyName("providerUsageSamples")]
    public int ProviderUsageSamples { get; set; }

    [JsonPropertyName("cacheReadTokens")]
    public int? CacheReadTokens { get; set; }

    [JsonPropertyName("cacheWriteTokens")]
    public int? CacheWriteTokens { get; set; }

    [JsonPropertyName("cacheMissTokens")]
    public int? CacheMissTokens { get; set; }

    [JsonPropertyName("reasoningTokens")]
    public int? ReasoningTokens { get; set; }

    [JsonPropertyName("providerTotalTokens")]
    public int? ProviderTotalTokens { get; set; }

    [JsonPropertyName("availability")]
    public string Availability { get; set; } =
        UsageAvailabilityStates.CostAvailable;

    [JsonPropertyName("actions")]
    [JsonRequired]
    public int Actions { get; set; }

    [JsonPropertyName("hasUnaccountedUsage")]
    [JsonRequired]
    public bool HasUnaccountedUsage { get; set; }

    [JsonPropertyName("unaccountedProviderAttempts")]
    [JsonRequired]
    public int UnaccountedProviderAttempts { get; set; }
}

public sealed class AgentRun : VersionedProtocolObject
{
    [JsonPropertyName("runId")]
    [JsonRequired]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("agentId")]
    [JsonRequired]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    [JsonRequired]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("trigger")]
    [JsonRequired]
    public AgentTrigger Trigger { get; set; } = new();

    [JsonPropertyName("triggerObservationIds")]
    [JsonRequired]
    public List<string> TriggerObservationIds { get; set; } = new();

    [JsonPropertyName("decisionKey")]
    public string? DecisionKey { get; set; }

    [JsonPropertyName("batchId")]
    public string? BatchId { get; set; }

    [JsonPropertyName("state")]
    [JsonRequired]
    public string State { get; set; } = RunStates.Queued;

    [JsonPropertyName("revision")]
    [JsonRequired]
    public long Revision { get; set; }

    [JsonPropertyName("currentTurnId")]
    public string? CurrentTurnId { get; set; }

    [JsonPropertyName("runtimeGeneration")]
    [JsonRequired]
    public long RuntimeGeneration { get; set; } = 1;

    [JsonPropertyName("budget")]
    [JsonRequired]
    public AgentBudget Budget { get; set; } = new();

    [JsonPropertyName("usage")]
    [JsonRequired]
    public AgentUsage Usage { get; set; } = new();

    [JsonPropertyName("pendingOperationIds")]
    [JsonRequired]
    public List<string> PendingOperationIds { get; set; } = new();

    [JsonPropertyName("terminalReason")]
    public string? TerminalReason { get; set; }

    [JsonPropertyName("completionIntent")]
    public string? CompletionIntent { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonRequired]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    [JsonRequired]
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AgentDefinition : VersionedProtocolObject
{
    [JsonPropertyName("agentDefinitionId")]
    [JsonRequired]
    public string AgentDefinitionId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonRequired]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("identity")]
    [JsonRequired]
    public JsonElement Identity { get; set; }

    [JsonPropertyName("behaviorPolicyRef")]
    public string? BehaviorPolicyRef { get; set; }

    [JsonPropertyName("toolsets")]
    [JsonRequired]
    public List<string> Toolsets { get; set; } = new();

    [JsonPropertyName("skills")]
    [JsonRequired]
    public List<string> Skills { get; set; } = new();

    [JsonPropertyName("contextPolicyRef")]
    public string? ContextPolicyRef { get; set; }

    [JsonPropertyName("memoryPolicyRef")]
    public string? MemoryPolicyRef { get; set; }

    [JsonPropertyName("providerPolicyRef")]
    public string? ProviderPolicyRef { get; set; }

    [JsonPropertyName("budgets")]
    [JsonRequired]
    public JsonElement Budgets { get; set; }
}

public sealed class TurnSnapshot : VersionedProtocolObject
{
    [JsonPropertyName("turnId")]
    [JsonRequired]
    public string TurnId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    [JsonRequired]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("runtimeGeneration")]
    [JsonRequired]
    public long RuntimeGeneration { get; set; }

    [JsonPropertyName("providerId")]
    [JsonRequired]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("modelId")]
    [JsonRequired]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("promptLayoutVersion")]
    [JsonRequired]
    public string PromptLayoutVersion { get; set; } = string.Empty;

    [JsonPropertyName("stablePrefixHash")]
    [JsonRequired]
    public string StablePrefixHash { get; set; } = string.Empty;

    [JsonPropertyName("skillGeneration")]
    [JsonRequired]
    public long SkillGeneration { get; set; }

    [JsonPropertyName("skillDigests")]
    [JsonRequired]
    public List<string> SkillDigests { get; set; } = new();

    [JsonPropertyName("toolCatalogGeneration")]
    [JsonRequired]
    public long ToolCatalogGeneration { get; set; }

    [JsonPropertyName("directToolDigest")]
    [JsonRequired]
    public string DirectToolDigest { get; set; } = string.Empty;

    [JsonPropertyName("deferredCatalogDigest")]
    public string? DeferredCatalogDigest { get; set; }

    [JsonPropertyName("contextPolicyVersion")]
    [JsonRequired]
    public string ContextPolicyVersion { get; set; } = string.Empty;

    [JsonPropertyName("budgetPolicyVersion")]
    [JsonRequired]
    public string BudgetPolicyVersion { get; set; } = string.Empty;

    [JsonPropertyName("maxSideEffectToolCallsPerTurn")]
    public int? MaxSideEffectToolCallsPerTurn { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonRequired]
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RuntimeEvent : VersionedProtocolObject
{
    [JsonPropertyName("eventId")]
    [JsonRequired]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("turnId")]
    public string? TurnId { get; set; }

    [JsonPropertyName("sequence")]
    [JsonRequired]
    public long Sequence { get; set; }

    [JsonPropertyName("kind")]
    [JsonRequired]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("durability")]
    [JsonRequired]
    public string Durability { get; set; } = EventDurabilities.Durable;

    [JsonPropertyName("runtimeGeneration")]
    [JsonRequired]
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
    [JsonRequired]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("payload")]
    [JsonRequired]
    public JsonElement Payload { get; set; }
}

public sealed class SkillManifest : VersionedProtocolObject
{
    [JsonPropertyName("skillId")]
    [JsonRequired]
    public string SkillId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonRequired]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    [JsonRequired]
    public string Digest { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonRequired]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("promptFragments")]
    [JsonRequired]
    public List<string> PromptFragments { get; set; } = new();

    [JsonPropertyName("requiredToolRefs")]
    [JsonRequired]
    public List<string> RequiredToolRefs { get; set; } = new();

    [JsonPropertyName("optionalToolRefs")]
    [JsonRequired]
    public List<string> OptionalToolRefs { get; set; } = new();

    [JsonPropertyName("contextProviderRefs")]
    [JsonRequired]
    public List<string> ContextProviderRefs { get; set; } = new();

    [JsonPropertyName("resourceRefs")]
    [JsonRequired]
    public List<ResourceReference> ResourceRefs { get; set; } = new();

    [JsonPropertyName("capabilityRequirements")]
    [JsonRequired]
    public JsonElement CapabilityRequirements { get; set; }

    [JsonPropertyName("trust")]
    [JsonRequired]
    public string Trust { get; set; } = "untrusted";

    [JsonPropertyName("activationPolicy")]
    [JsonRequired]
    public JsonElement ActivationPolicy { get; set; }
}

public sealed class CapabilityManifest : VersionedProtocolObject
{
    [JsonPropertyName("protocolRange")]
    [JsonRequired]
    public string ProtocolRange { get; set; } = string.Empty;

    [JsonPropertyName("runtimeVersion")]
    [JsonRequired]
    public string RuntimeVersion { get; set; } = string.Empty;

    [JsonPropertyName("engine")]
    [JsonRequired]
    public string Engine { get; set; } = "headless";

    [JsonPropertyName("engineVersion")]
    [JsonRequired]
    public string EngineVersion { get; set; } = string.Empty;

    [JsonPropertyName("adapterVersion")]
    [JsonRequired]
    public string AdapterVersion { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    [JsonRequired]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("backend")]
    [JsonRequired]
    public string Backend { get; set; } = string.Empty;

    [JsonPropertyName("contentTypes")]
    [JsonRequired]
    public List<string> ContentTypes { get; set; } = new();

    [JsonPropertyName("codecs")]
    [JsonRequired]
    public List<string> Codecs { get; set; } = new();

    [JsonPropertyName("transports")]
    [JsonRequired]
    public List<string> Transports { get; set; } = new();

    [JsonPropertyName("maxMessageBytes")]
    [JsonRequired]
    public long MaxMessageBytes { get; set; }

    [JsonPropertyName("maxBatchSize")]
    [JsonRequired]
    public int MaxBatchSize { get; set; }

    [JsonPropertyName("streaming")]
    [JsonRequired]
    public bool Streaming { get; set; }

    [JsonPropertyName("persistenceLevel")]
    [JsonRequired]
    public string PersistenceLevel { get; set; } = "memory";

    [JsonPropertyName("toolEffects")]
    [JsonRequired]
    public List<string> ToolEffects { get; set; } = new();

    [JsonPropertyName("threadAffinities")]
    [JsonRequired]
    public List<string> ThreadAffinities { get; set; } = new();

    [JsonPropertyName("receiptReconciliation")]
    [JsonRequired]
    public bool ReceiptReconciliation { get; set; }

    [JsonPropertyName("providerCapabilities")]
    [JsonRequired]
    public JsonElement ProviderCapabilities { get; set; }
}

public sealed class PrunedContextItem
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    [JsonRequired]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("reasonCode")]
    [JsonRequired]
    public string ReasonCode { get; set; } = string.Empty;
}

public sealed class ContextBudgetReport : VersionedProtocolObject
{
    [JsonPropertyName("runId")]
    [JsonRequired]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("turnId")]
    [JsonRequired]
    public string TurnId { get; set; } = string.Empty;

    [JsonPropertyName("inputCount")]
    [JsonRequired]
    public int InputCount { get; set; }

    [JsonPropertyName("selectedIds")]
    [JsonRequired]
    public List<string> SelectedIds { get; set; } = new();

    [JsonPropertyName("deferredIds")]
    [JsonRequired]
    public List<string> DeferredIds { get; set; } = new();

    [JsonPropertyName("pruned")]
    [JsonRequired]
    public List<PrunedContextItem> Pruned { get; set; } = new();

    [JsonPropertyName("externalized")]
    [JsonRequired]
    public List<ResourceReference> Externalized { get; set; } = new();

    [JsonPropertyName("estimatedTokens")]
    [JsonRequired]
    public int EstimatedTokens { get; set; }

    [JsonPropertyName("actualTokens")]
    public int? ActualTokens { get; set; }

    [JsonPropertyName("budgetLimit")]
    [JsonRequired]
    public int BudgetLimit { get; set; }

    [JsonPropertyName("reasonCodes")]
    [JsonRequired]
    public List<string> ReasonCodes { get; set; } = new();
}

using System.Text.Json.Serialization;

namespace GameAgent.Protocol;

/// <summary>
/// The structured observation input supplied to a model turn.
/// </summary>
public sealed class ObservationBatchPayload
{
    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } =
        "application/vnd.game-agent.observations+json";

    [JsonPropertyName("observations")]
    public List<ObservationEnvelope> Observations { get; set; } = new();
}

public sealed class RunStartedEventPayload
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;
}

public sealed class TurnStartedEventPayload
{
    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; set; }

    [JsonPropertyName("attemptId")]
    public string AttemptId { get; set; } = string.Empty;

    [JsonPropertyName("streamAttemptId")]
    public string StreamAttemptId { get; set; } = string.Empty;
}

public sealed class TurnCompletedEventPayload
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;
}

public sealed class RunUsageEventPayload
{
    [JsonPropertyName("usage")]
    public AgentUsage Usage { get; set; } = new();
}

public sealed class BudgetEventPayload
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ActionReconcilingEventPayload
{
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;
}

public sealed class RuntimeErrorEventPayload
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentUsage? Usage { get; set; }
}

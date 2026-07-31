using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.World;

namespace GameAgent.Runtime;

public enum WorldAgentEvolutionStage
{
    ActorManifestCommitted = 0,
    ActorsRunning = 1,
    Reducing = 2,
    Revalidating = 3,
    WorldCommitPending = 4,
    Completed = 5,
    Waiting = 6,
    ReconciliationRequired = 7,
    Rejected = 8,
    Failed = 9,
    Cancelled = 10
}

public enum WorldAgentEvolutionStatus
{
    Completed = 0,
    Replayed = 1,
    Waiting = 2,
    ReconciliationRequired = 3,
    Busy = 4,
    Rejected = 5,
    Failed = 6,
    Cancelled = 7
}

public sealed class WorldAgentRuntimeGeneration
{
    public WorldAgentRuntimeGeneration(
        long runtimeGeneration,
        string toolCatalogDigest,
        string skillCatalogDigest,
        string providerPolicyDigest,
        string modelPolicyDigest)
    {
        if (runtimeGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeGeneration));
        }

        RuntimeGeneration = runtimeGeneration;
        ToolCatalogDigest = EvolutionGuard.Digest(
            toolCatalogDigest,
            nameof(toolCatalogDigest));
        SkillCatalogDigest = EvolutionGuard.Digest(
            skillCatalogDigest,
            nameof(skillCatalogDigest));
        ProviderPolicyDigest = EvolutionGuard.Digest(
            providerPolicyDigest,
            nameof(providerPolicyDigest));
        ModelPolicyDigest = EvolutionGuard.Digest(
            modelPolicyDigest,
            nameof(modelPolicyDigest));
    }

    public long RuntimeGeneration { get; }

    public string ToolCatalogDigest { get; }

    public string SkillCatalogDigest { get; }

    public string ProviderPolicyDigest { get; }

    public string ModelPolicyDigest { get; }

    public static WorldAgentRuntimeGeneration FromExecutionPolicy(
        long runtimeGeneration,
        DurableExecutionPolicyIdentity executionPolicy)
    {
        if (executionPolicy is null)
        {
            throw new ArgumentNullException(nameof(executionPolicy));
        }

        return new WorldAgentRuntimeGeneration(
            runtimeGeneration,
            executionPolicy.ToolCatalogDigest,
            executionPolicy.SkillCatalogDigest,
            executionPolicy.ProviderPolicyDigest,
            executionPolicy.ModelPolicyDigest);
    }

    public bool Matches(WorldAgentRuntimeGeneration? other)
    {
        return other is not null
               && RuntimeGeneration == other.RuntimeGeneration
               && string.Equals(
                   ToolCatalogDigest,
                   other.ToolCatalogDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   SkillCatalogDigest,
                   other.SkillCatalogDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   ProviderPolicyDigest,
                   other.ProviderPolicyDigest,
                   StringComparison.Ordinal)
               && string.Equals(
                   ModelPolicyDigest,
                   other.ModelPolicyDigest,
                   StringComparison.Ordinal);
    }

    internal JsonElement ToEnvelope()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contract",
                "game-agent.world-agent-runtime-policy.v1");
            writer.WriteNumber(
                "runtimeGeneration",
                RuntimeGeneration);
            writer.WriteString(
                "toolCatalogDigest",
                ToolCatalogDigest);
            writer.WriteString(
                "skillCatalogDigest",
                SkillCatalogDigest);
            writer.WriteString(
                "providerPolicyDigest",
                ProviderPolicyDigest);
            writer.WriteString(
                "modelPolicyDigest",
                ModelPolicyDigest);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Captures the immutable tool, skill, provider, and model policy generation
/// that the injected runtime can execute now. Evolution recovery uses this
/// evidence to prevent a missing participant from starting under a different
/// runtime policy.
/// </summary>
public interface IWorldAgentRuntimePolicySnapshotSource
{
    WorldAgentRuntimeGeneration CapturePolicySnapshot();
}

/// <summary>
/// A fixed snapshot source for applications whose runtime policy is immutable
/// for the process lifetime. Hot-reload hosts should provide an atomic source
/// backed by their runtime registry generation.
/// </summary>
public sealed class FixedWorldAgentRuntimePolicySnapshotSource
    : IWorldAgentRuntimePolicySnapshotSource
{
    private readonly WorldAgentRuntimeGeneration _snapshot;

    public FixedWorldAgentRuntimePolicySnapshotSource(
        WorldAgentRuntimeGeneration snapshot)
    {
        _snapshot = snapshot
                    ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public WorldAgentRuntimeGeneration CapturePolicySnapshot()
    {
        return _snapshot;
    }
}

public sealed class WorldAgentEvolutionParticipant
{
    public WorldAgentEvolutionParticipant(
        WorldAgentDecisionDraft draft,
        WorldAgentJob job)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        Job = job ?? throw new ArgumentNullException(nameof(job));
        var validation = draft.ValidateJob(job);
        if (validation is not null)
        {
            throw new ArgumentException(
                $"The participant job is not valid for its draft ({validation}).",
                nameof(job));
        }
    }

    public WorldAgentDecisionDraft Draft { get; }

    public WorldAgentJob Job { get; }
}

/// <summary>
/// One immutable simultaneous decision boundary. The framework owns
/// concurrency, recovery, fencing, and idempotency. The reducer policy remains
/// game-owned and is identified by a content digest captured here.
/// </summary>
public sealed class WorldAgentEvolutionCommand
{
    private readonly JsonElement _envelope;

    public WorldAgentEvolutionCommand(
        string commandId,
        string operationId,
        string batchId,
        WorldAuthoritativeCoordinate expectedCoordinate,
        IEnumerable<WorldAgentEvolutionParticipant> participants,
        string reducerPolicyId,
        string reducerPolicyDigest,
        WorldAgentRuntimeGeneration runtimeGeneration,
        MultiActorBatchBudget? aggregateBudget = null)
    {
        CommandId = EvolutionGuard.Required(
            commandId,
            nameof(commandId),
            192);
        OperationId = EvolutionGuard.Required(
            operationId,
            nameof(operationId),
            192);
        BatchId = EvolutionGuard.Required(
            batchId,
            nameof(batchId),
            128);
        ExpectedCoordinate = expectedCoordinate
                             ?? throw new ArgumentNullException(
                                 nameof(expectedCoordinate));
        ReducerPolicyId = EvolutionGuard.Required(
            reducerPolicyId,
            nameof(reducerPolicyId),
            192);
        ReducerPolicyDigest = EvolutionGuard.Digest(
            reducerPolicyDigest,
            nameof(reducerPolicyDigest));
        RuntimeGeneration = runtimeGeneration
                            ?? throw new ArgumentNullException(
                                nameof(runtimeGeneration));
        AggregateBudget = aggregateBudget;
        Participants = CopyParticipants(participants);
        ValidateParticipants();
        _envelope = WriteEnvelope();
        SemanticDigest = CanonicalJsonDigest.ComputeSha256(_envelope);
    }

    public string CommandId { get; }

    public string OperationId { get; }

    public string BatchId { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }

    public IReadOnlyList<WorldAgentEvolutionParticipant> Participants { get; }

    public string ReducerPolicyId { get; }

    public string ReducerPolicyDigest { get; }

    public WorldAgentRuntimeGeneration RuntimeGeneration { get; }

    public MultiActorBatchBudget? AggregateBudget { get; }

    public string SemanticDigest { get; }

    public JsonElement ToEnvelope()
    {
        return _envelope.Clone();
    }

    private static IReadOnlyList<WorldAgentEvolutionParticipant>
        CopyParticipants(
            IEnumerable<WorldAgentEvolutionParticipant> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var copy = new List<WorldAgentEvolutionParticipant>(32);
        foreach (var value in values)
        {
            if (copy.Count >= 1_024)
            {
                throw new ArgumentException(
                    "An evolution batch cannot exceed 1024 participants.",
                    nameof(values));
            }

            copy.Add(
                value
                ?? throw new ArgumentException(
                    "Participants cannot contain null entries.",
                    nameof(values)));
        }

        if (copy.Count == 0)
        {
            throw new ArgumentException(
                "An evolution batch requires at least one participant.",
                nameof(values));
        }

        return new ReadOnlyCollection<WorldAgentEvolutionParticipant>(
            copy);
    }

    private void ValidateParticipants()
    {
        var jobs = new HashSet<string>(StringComparer.Ordinal);
        var runs = new HashSet<string>(StringComparer.Ordinal);
        var actors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var participant in Participants)
        {
            var draft = participant.Draft;
            var job = participant.Job;
            if (!ExpectedCoordinate.IsExactMatch(
                    draft.ExpectedCoordinate)
                || !string.Equals(
                    job.BatchId,
                    BatchId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    job.CatalogDigest,
                    ExpectedCoordinate.CatalogDigest,
                    StringComparison.Ordinal)
                || !jobs.Add(job.JobId)
                || !runs.Add(job.RunId)
                || !actors.Add(job.AgentId))
            {
                throw new ArgumentException(
                    "Participants must be unique and share the exact batch coordinate.",
                    nameof(Participants));
            }
        }
    }

    private JsonElement WriteEnvelope()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contract",
                "game-agent.world-agent-evolution-command.v1");
            writer.WriteString("commandId", CommandId);
            writer.WriteString("operationId", OperationId);
            writer.WriteString("batchId", BatchId);
            EvolutionGuard.WriteCoordinate(writer, ExpectedCoordinate);
            writer.WriteString("reducerPolicyId", ReducerPolicyId);
            writer.WriteString(
                "reducerPolicyDigest",
                ReducerPolicyDigest);
            writer.WritePropertyName("runtimeGeneration");
            writer.WriteStartObject();
            writer.WriteNumber(
                "generation",
                RuntimeGeneration.RuntimeGeneration);
            writer.WriteString(
                "toolCatalogDigest",
                RuntimeGeneration.ToolCatalogDigest);
            writer.WriteString(
                "skillCatalogDigest",
                RuntimeGeneration.SkillCatalogDigest);
            writer.WriteString(
                "providerPolicyDigest",
                RuntimeGeneration.ProviderPolicyDigest);
            writer.WriteString(
                "modelPolicyDigest",
                RuntimeGeneration.ModelPolicyDigest);
            writer.WriteEndObject();
            writer.WritePropertyName("aggregateBudget");
            if (AggregateBudget is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteNumber(
                    "maxTokens",
                    AggregateBudget.MaxTokens);
                writer.WriteNumber(
                    "maxActions",
                    AggregateBudget.MaxActions);
                writer.WriteNumber(
                    "maxDurationMs",
                    AggregateBudget.MaxDurationMs);
                writer.WriteString(
                    "maxCostUsd",
                    AggregateBudget.MaxCostUsd);
                writer.WriteEndObject();
            }

            writer.WritePropertyName("participants");
            writer.WriteStartArray();
            foreach (var participant in Participants)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "jobId",
                    participant.Job.JobId);
                writer.WriteString(
                    "runId",
                    participant.Job.RunId);
                writer.WriteString(
                    "agentId",
                    participant.Job.AgentId);
                writer.WriteString(
                    "jobSemanticDigest",
                    participant.Job.SemanticDigest);
                writer.WriteString(
                    "draftId",
                    participant.Draft.DraftId);
                writer.WriteString(
                    "draftDigest",
                    participant.Draft.Digest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }
}

public sealed class WorldAgentEvolutionActorResult
{
    internal WorldAgentEvolutionActorResult(
        int inputIndex,
        WorldAgentEvolutionParticipant participant,
        WorldAgentDecisionProposalResult proposalResult)
    {
        InputIndex = inputIndex;
        Participant = participant;
        ProposalResult = proposalResult;
    }

    public int InputIndex { get; }

    public WorldAgentEvolutionParticipant Participant { get; }

    public WorldAgentDecisionProposalResult ProposalResult { get; }
}

public sealed class WorldAgentEvolutionReductionContext
{
    internal WorldAgentEvolutionReductionContext(
        WorldAgentEvolutionCommand command,
        WorldAuthoritativeStateSnapshot capturedSnapshot,
        IReadOnlyList<WorldAgentEvolutionActorResult> actorResults)
    {
        Command = command;
        CapturedSnapshot = capturedSnapshot;
        ActorResults = actorResults;
    }

    public WorldAgentEvolutionCommand Command { get; }

    public WorldAuthoritativeStateSnapshot CapturedSnapshot { get; }

    /// <summary>
    /// Results are always in the command's participant order, regardless of
    /// provider completion order.
    /// </summary>
    public IReadOnlyList<WorldAgentEvolutionActorResult> ActorResults { get; }
}

public enum WorldAgentEvolutionReductionDisposition
{
    Commit = 0,
    NoChange = 1,
    Waiting = 2,
    Rejected = 3
}

public sealed class WorldAgentEvolutionReduction
{
    private static readonly JsonValueLimits EvidenceLimits = new(
        maxUtf8Bytes: 262_144,
        maxDepth: 32,
        maxNodes: 16_384,
        maxStringUtf8Bytes: 65_536,
        maxContainerItems: 4_096);

    public WorldAgentEvolutionReduction(
        WorldAgentEvolutionReductionDisposition disposition,
        string reasonCode,
        JsonElement evidence,
        WorldEventTransactionExecutionRequest? transaction = null)
    {
        if (!Enum.IsDefined(typeof(
                WorldAgentEvolutionReductionDisposition),
                disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        Disposition = disposition;
        ReasonCode = EvolutionGuard.Required(
            reasonCode,
            nameof(reasonCode),
            96);
        JsonValueInspector.ValidateAndMeasure(
            evidence,
            EvidenceLimits,
            nameof(evidence));
        Evidence = evidence.Clone();
        Transaction = transaction;
        if ((disposition == WorldAgentEvolutionReductionDisposition.Commit)
            != (transaction is not null))
        {
            throw new ArgumentException(
                "Only a commit reduction carries a transaction.",
                nameof(transaction));
        }
    }

    public WorldAgentEvolutionReductionDisposition Disposition { get; }

    public string ReasonCode { get; }

    public JsonElement Evidence { get; }

    public WorldEventTransactionExecutionRequest? Transaction { get; }

    public string EvidenceDigest =>
        CanonicalJsonDigest.ComputeSha256(Evidence);
}

public interface IWorldAgentEvolutionReducer
{
    ValueTask<WorldAgentEvolutionReduction> ReduceAsync(
        WorldAgentEvolutionReductionContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// A game-owned deterministic reducer plus the exact policy identity that its
/// implementation currently represents.
/// </summary>
public interface IWorldAgentEvolutionReducerDescriptor
    : IWorldAgentEvolutionReducer
{
    string PolicyId { get; }

    string PolicyDigest { get; }
}

public sealed class WorldAgentEvolutionResult
{
    internal WorldAgentEvolutionResult(
        string commandId,
        WorldAgentEvolutionStatus status,
        WorldAgentEvolutionStage stage,
        string reasonCode,
        long checkpointRevision,
        IReadOnlyList<WorldAgentEvolutionActorResult>? actorResults = null,
        WorldTransactionExecutionResult? transaction = null,
        WorldCommandReceipt? receipt = null)
    {
        CommandId = commandId;
        Status = status;
        Stage = stage;
        ReasonCode = reasonCode;
        CheckpointRevision = checkpointRevision;
        ActorResults = actorResults
                       ?? Array.Empty<WorldAgentEvolutionActorResult>();
        Transaction = transaction;
        Receipt = receipt ?? transaction?.Receipt;
    }

    public string CommandId { get; }

    public WorldAgentEvolutionStatus Status { get; }

    public WorldAgentEvolutionStage Stage { get; }

    public string ReasonCode { get; }

    public long CheckpointRevision { get; }

    public IReadOnlyList<WorldAgentEvolutionActorResult> ActorResults { get; }

    public WorldTransactionExecutionResult? Transaction { get; }

    public WorldCommandReceipt? Receipt { get; }

    public bool Succeeded =>
        Status is WorldAgentEvolutionStatus.Completed
            or WorldAgentEvolutionStatus.Replayed;
}

public sealed class WorldAgentEvolutionRunnerOptions
{
    public WorldAgentEvolutionRunnerOptions(
        int maxParticipants = 256,
        int maxConcurrentActors = 16,
        TimeSpan? ownerLeaseDuration = null,
        int maxBatchSnapshotUtf8Bytes = 64 * 1_048_576,
        int maxBatchSnapshotJsonNodes = 1_048_576)
    {
        if (maxParticipants is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParticipants));
        }

        if (maxConcurrentActors is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentActors));
        }

        if (maxBatchSnapshotUtf8Bytes is < 4_096
            or > 512 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBatchSnapshotUtf8Bytes));
        }

        if (maxBatchSnapshotJsonNodes is < 64
            or > 16 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBatchSnapshotJsonNodes));
        }

        var lease = ownerLeaseDuration ?? TimeSpan.FromMinutes(5);
        if (lease < TimeSpan.FromSeconds(1)
            || lease > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownerLeaseDuration));
        }

        MaxParticipants = maxParticipants;
        MaxConcurrentActors = maxConcurrentActors;
        MaxBatchSnapshotUtf8Bytes = maxBatchSnapshotUtf8Bytes;
        MaxBatchSnapshotJsonNodes = maxBatchSnapshotJsonNodes;
        OwnerLeaseDuration = lease;
    }

    public int MaxParticipants { get; }

    public int MaxConcurrentActors { get; }

    public int MaxBatchSnapshotUtf8Bytes { get; }

    public int MaxBatchSnapshotJsonNodes { get; }

    public TimeSpan OwnerLeaseDuration { get; }
}

internal static class EvolutionGuard
{
    public static string Required(
        string value,
        string parameterName,
        int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "A bounded non-empty value is required.",
                parameterName);
        }

        return value;
    }

    public static string Digest(string value, string parameterName)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new ArgumentException(
                "A lowercase SHA-256 digest is required.",
                parameterName);
        }

        return value;
    }

    public static void WriteCoordinate(
        Utf8JsonWriter writer,
        WorldAuthoritativeCoordinate coordinate)
    {
        writer.WritePropertyName("expectedCoordinate");
        writer.WriteStartObject();
        writer.WriteString("worldId", coordinate.WorldId);
        writer.WriteString("timelineId", coordinate.TimelineId);
        writer.WriteNumber("timelineEpoch", coordinate.TimelineEpoch);
        writer.WriteNumber("saveRevision", coordinate.SaveRevision);
        writer.WriteNumber("stateVersion", coordinate.StateVersion);
        writer.WriteString("catalogDigest", coordinate.CatalogDigest);
        writer.WriteEndObject();
    }
}

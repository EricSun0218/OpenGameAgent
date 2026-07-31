using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.World;

namespace GameAgent.Runtime;

public static class WorldAgentDecisionReasonCodes
{
    public const string ProposalReady = "world_agent_proposal_ready";
    public const string DraftBindingMissing =
        "world_agent_draft_binding_missing";
    public const string DraftBindingMismatch =
        "world_agent_draft_binding_mismatch";
    public const string UnsupportedJobKind =
        "world_agent_job_kind_not_authoritative";
    public const string OptionSchemaMismatch =
        "world_agent_option_schema_mismatch";
    public const string StaleCoordinate =
        "world_agent_authoritative_coordinate_stale";
    public const string OutputInvalid = "world_agent_proposal_output_invalid";
    public const string OptionNotDeclared =
        "world_agent_proposal_option_not_declared";
    public const string ProposalBindingMismatch =
        "world_agent_proposal_binding_mismatch";
    public const string FallbackBindingMismatch =
        "world_agent_fallback_binding_mismatch";
}

/// <summary>
/// One model-selectable identifier mapped to a mutation that was fully
/// constructed before model dispatch. The definition digest is supplied by
/// the trusted compiler and must cover path mapping, numeric schemas, and any
/// other behavior outside the portable mutation-set digest.
/// </summary>
public sealed class WorldAgentMutationOption
{
    public WorldAgentMutationOption(
        string optionId,
        WorldAtomicMutationEffect effect,
        string effectDefinitionDigest)
    {
        OptionId = Required(optionId, nameof(optionId), 192);
        Effect = effect ?? throw new ArgumentNullException(nameof(effect));
        if (!CanonicalJsonDigest.IsSha256(effectDefinitionDigest))
        {
            throw new ArgumentException(
                "Effect definition digest must be a lowercase SHA-256 digest.",
                nameof(effectDefinitionDigest));
        }

        EffectDefinitionDigest = effectDefinitionDigest;
    }

    public string OptionId { get; }

    public WorldAtomicMutationEffect Effect { get; }

    public string EffectDefinitionDigest { get; }

    private static string Required(
        string value,
        string parameterName,
        int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "A bounded non-empty identifier is required.",
                parameterName);
        }

        return value;
    }
}

/// <summary>
/// Immutable decision boundary captured before an agent is called. Model
/// output can select only an option ID; it cannot supply mutation paths,
/// values, numeric operands, resources, or effect handlers.
/// </summary>
public sealed class WorldAgentDecisionDraft
{
    private const int MaximumTotalIntents = 4_096;
    private const long MaximumPortableMutationBytes = 8 * 1024 * 1024;

    private readonly IReadOnlyDictionary<string, WorldAgentMutationOption>
        _options;

    private readonly JsonElement _envelope;

    public WorldAgentDecisionDraft(
        string draftId,
        WorldEventInstance occurrence,
        WorldAuthoritativeCoordinate expectedCoordinate,
        IEnumerable<WorldAgentMutationOption> options)
    {
        DraftId = Required(draftId, nameof(draftId), 192);
        Occurrence = occurrence
                     ?? throw new ArgumentNullException(nameof(occurrence));
        ExpectedCoordinate = expectedCoordinate
                             ?? throw new ArgumentNullException(
                                 nameof(expectedCoordinate));
        if (!string.Equals(
                occurrence.WorldId,
                expectedCoordinate.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                occurrence.TimelineId,
                expectedCoordinate.TimelineId,
                StringComparison.Ordinal)
            || occurrence.TimelineEpoch != expectedCoordinate.TimelineEpoch)
        {
            throw new ArgumentException(
                "The occurrence and draft coordinate must share one scope.",
                nameof(expectedCoordinate));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var collected = new List<WorldAgentMutationOption>(256);
        foreach (var option in options)
        {
            if (collected.Count >= 256)
            {
                throw new ArgumentException(
                    "A decision draft requires 1 through 256 options.",
                    nameof(options));
            }

            collected.Add(
                option
                ?? throw new ArgumentException(
                    "Options cannot contain null entries.",
                    nameof(options)));
        }

        var copy = collected
            .OrderBy(option => option.OptionId, StringComparer.Ordinal)
            .ToArray();
        if (copy.Length < 1)
        {
            throw new ArgumentException(
                "A decision draft requires 1 through 256 options.",
                nameof(options));
        }

        if (copy.Select(option => option.OptionId)
                .Distinct(StringComparer.Ordinal)
                .Count()
            != copy.Length)
        {
            throw new ArgumentException(
                "A decision draft contains duplicate option identifiers.",
                nameof(options));
        }

        CommandId = copy[0].Effect.MutationSet.CommandId;
        OperationId = copy[0].Effect.MutationSet.OperationId;
        long portableBytes = 0;
        var totalIntents = 0;
        foreach (var option in copy)
        {
            ValidateOption(option);
            totalIntents = checked(
                totalIntents
                + option.Effect.MutationSet.Intents.Count);
            portableBytes = checked(
                portableBytes
                + Encoding.UTF8.GetByteCount(
                    option.Effect.MutationSet.PortableJson.GetRawText()));
            if (totalIntents > MaximumTotalIntents
                || portableBytes > MaximumPortableMutationBytes)
            {
                throw new ArgumentException(
                    "The combined option mutations exceed the draft limit.",
                    nameof(options));
            }
        }

        _options = new ReadOnlyDictionary<string, WorldAgentMutationOption>(
            copy.ToDictionary(
                option => option.OptionId,
                option => option,
                StringComparer.Ordinal));
        _envelope = WriteEnvelope();
        Digest = CanonicalJsonDigest.ComputeSha256(_envelope);
        Binding = new WorldAgentAuthoritativeBinding(
            DraftId,
            Digest,
            Occurrence.InstanceId,
            ExpectedCoordinate);
    }

    public string DraftId { get; }

    public string Digest { get; }

    public WorldEventInstance Occurrence { get; }

    public string OccurrenceId => Occurrence.InstanceId;

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }

    public string CommandId { get; }

    public string OperationId { get; }

    public IReadOnlyList<string> OptionIds =>
        new ReadOnlyCollection<string>(
            _options.Keys
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());

    public WorldAgentAuthoritativeBinding Binding { get; }

    public JsonElement ToEnvelope()
    {
        return _envelope.Clone();
    }

    internal bool TryGetOption(
        string optionId,
        out WorldAgentMutationOption? option)
    {
        return _options.TryGetValue(optionId, out option);
    }

    internal string? ValidateJob(WorldAgentJob job)
    {
        if (job.Kind is not WorldAgentJobKind.Selection
            and not WorldAgentJobKind.Understanding)
        {
            return WorldAgentDecisionReasonCodes.UnsupportedJobKind;
        }

        var binding = job.AuthoritativeBinding;
        if (binding is null)
        {
            return WorldAgentDecisionReasonCodes.DraftBindingMissing;
        }

        if (!string.Equals(
                binding.DraftId,
                DraftId,
                StringComparison.Ordinal)
            || !string.Equals(
                binding.DraftDigest,
                Digest,
                StringComparison.Ordinal)
            || !string.Equals(
                binding.OccurrenceId,
                OccurrenceId,
                StringComparison.Ordinal)
            || !ExpectedCoordinate.IsExactMatch(
                binding.ExpectedCoordinate)
            || !string.Equals(
                job.OccurrenceId,
                OccurrenceId,
                StringComparison.Ordinal)
            || !string.Equals(
                job.CatalogDigest,
                ExpectedCoordinate.CatalogDigest,
                StringComparison.Ordinal)
            || (job.Coordinate.Observer is not null
                && !Occurrence.Participants.Any(
                    participant => string.Equals(
                                       participant.EntityId,
                                       job.Coordinate.Observer.EntityId,
                                       StringComparison.Ordinal)
                                   && participant.Incarnation
                                   == job.Coordinate.Observer.Incarnation)))
        {
            return WorldAgentDecisionReasonCodes.DraftBindingMismatch;
        }

        var expectedSchema = WorldAgentOutputSchemas.Selection(OptionIds);
        if (!string.Equals(
                CanonicalJsonDigest.ComputeSha256(job.OutputSchema),
                CanonicalJsonDigest.ComputeSha256(expectedSchema),
                StringComparison.Ordinal))
        {
            return WorldAgentDecisionReasonCodes.OptionSchemaMismatch;
        }

        return null;
    }

    private void ValidateOption(WorldAgentMutationOption option)
    {
        var mutation = option.Effect.MutationSet;
        if (!string.Equals(
                mutation.CommandId,
                CommandId,
                StringComparison.Ordinal)
            || !string.Equals(
                mutation.OperationId,
                OperationId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Every option must share one command and operation identity.",
                nameof(option));
        }

        var expectedStateVersion = ExpectedCoordinate.StateVersion.ToString(
            CultureInfo.InvariantCulture);
        if (!string.Equals(
                mutation.WorldId,
                ExpectedCoordinate.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                mutation.TimelineId,
                ExpectedCoordinate.TimelineId,
                StringComparison.Ordinal)
            || mutation.TimelineEpoch != ExpectedCoordinate.TimelineEpoch
            || mutation.ExpectedSaveRevision
            != ExpectedCoordinate.SaveRevision
            || !string.Equals(
                mutation.ExpectedStateVersion,
                expectedStateVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                mutation.CatalogDigest,
                ExpectedCoordinate.CatalogDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Every option must target the exact draft coordinate.",
                nameof(option));
        }

        var reads = new HashSet<string>(
            Occurrence.ReadResourceKeys,
            StringComparer.Ordinal);
        reads.UnionWith(Occurrence.WriteResourceKeys);
        var writes = new HashSet<string>(
            Occurrence.WriteResourceKeys,
            StringComparer.Ordinal);
        if (mutation.ReadResourceKeys.Any(key => !reads.Contains(key))
            || mutation.WriteResourceKeys.Any(key => !writes.Contains(key)))
        {
            throw new ArgumentException(
                "An option cannot access resources outside the occurrence.",
                nameof(option));
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
                "game-agent.world-agent-decision-draft.v1");
            writer.WriteString("draftId", DraftId);
            writer.WriteString("occurrenceId", OccurrenceId);
            writer.WriteString(
                "occurrencePlanFingerprint",
                Occurrence.PlanFingerprint);
            WriteCoordinate(writer, ExpectedCoordinate);
            writer.WriteString("commandId", CommandId);
            writer.WriteString("operationId", OperationId);
            writer.WritePropertyName("options");
            writer.WriteStartArray();
            foreach (var option in _options.Values.OrderBy(
                         value => value.OptionId,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("optionId", option.OptionId);
                writer.WriteString(
                    "mutationSetDigest",
                    option.Effect.MutationSet.Digest);
                writer.WriteString(
                    "effectDefinitionDigest",
                    option.EffectDefinitionDigest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteCoordinate(
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

    private static string Required(
        string value,
        string parameterName,
        int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "A bounded non-empty identifier is required.",
                parameterName);
        }

        return value;
    }
}

public sealed class WorldAgentAuthoritativeProposal
{
    private static readonly HashSet<string> EnvelopeProperties =
        new(StringComparer.Ordinal)
        {
            "contract",
            "jobId",
            "runId",
            "jobSemanticDigest",
            "jobKind",
            "occurrenceId",
            "draftId",
            "draftDigest",
            "optionId",
            "usedFallback",
            "selectedOutputDigest",
            "expectedCoordinate",
            "proposalDigest"
        };

    private readonly JsonElement _envelope;

    private WorldAgentAuthoritativeProposal(
        string jobId,
        string runId,
        string jobSemanticDigest,
        WorldAgentJobKind jobKind,
        string occurrenceId,
        string draftId,
        string draftDigest,
        string optionId,
        bool usedFallback,
        string selectedOutputDigest,
        WorldAuthoritativeCoordinate expectedCoordinate,
        string? expectedProposalDigest = null)
    {
        JobId = Required(jobId, nameof(jobId), 128);
        RunId = Required(runId, nameof(runId), 128);
        JobSemanticDigest = Digest(
            jobSemanticDigest,
            nameof(jobSemanticDigest));
        if (jobKind is not WorldAgentJobKind.Selection
            and not WorldAgentJobKind.Understanding)
        {
            throw new ArgumentOutOfRangeException(nameof(jobKind));
        }

        JobKind = jobKind;
        OccurrenceId = Required(
            occurrenceId,
            nameof(occurrenceId),
            192);
        DraftId = Required(draftId, nameof(draftId), 192);
        DraftDigest = Digest(draftDigest, nameof(draftDigest));
        OptionId = Required(optionId, nameof(optionId), 192);
        UsedFallback = usedFallback;
        SelectedOutputDigest = Digest(
            selectedOutputDigest,
            nameof(selectedOutputDigest));
        ExpectedCoordinate = expectedCoordinate
                             ?? throw new ArgumentNullException(
                                 nameof(expectedCoordinate));
        ProposalDigest = CanonicalJsonDigest.ComputeSha256(WriteBody());
        if (expectedProposalDigest is not null
            && !string.Equals(
                expectedProposalDigest,
                ProposalDigest,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Proposal digest does not match its content.",
                nameof(expectedProposalDigest));
        }

        _envelope = WriteEnvelope();
    }

    public string JobId { get; }

    public string RunId { get; }

    public string JobSemanticDigest { get; }

    public WorldAgentJobKind JobKind { get; }

    public string OccurrenceId { get; }

    public string DraftId { get; }

    public string DraftDigest { get; }

    public string OptionId { get; }

    public bool UsedFallback { get; }

    public string SelectedOutputDigest { get; }

    public WorldAuthoritativeCoordinate ExpectedCoordinate { get; }

    public string ProposalDigest { get; }

    public JsonElement ToEnvelope()
    {
        return _envelope.Clone();
    }

    public static WorldAgentAuthoritativeProposal FromEnvelope(
        JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Proposal envelope must be a JSON object.",
                nameof(envelope));
        }

        var names = envelope.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        if (names.Length != EnvelopeProperties.Count
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Any(name => !EnvelopeProperties.Contains(name)))
        {
            throw new ArgumentException(
                "Proposal envelope has an unsupported shape.",
                nameof(envelope));
        }

        try
        {
            if (!string.Equals(
                    envelope.GetProperty("contract").GetString(),
                    "game-agent.world-agent-authoritative-proposal.v1",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Proposal contract is unsupported.",
                    nameof(envelope));
            }

            var coordinate = ReadCoordinate(
                envelope.GetProperty("expectedCoordinate"));
            var proposalDigest = Digest(
                envelope.GetProperty("proposalDigest").GetString()!,
                "proposalDigest");
            return new WorldAgentAuthoritativeProposal(
                envelope.GetProperty("jobId").GetString()!,
                envelope.GetProperty("runId").GetString()!,
                envelope.GetProperty("jobSemanticDigest").GetString()!,
                (WorldAgentJobKind)envelope.GetProperty("jobKind")
                    .GetInt32(),
                envelope.GetProperty("occurrenceId").GetString()!,
                envelope.GetProperty("draftId").GetString()!,
                envelope.GetProperty("draftDigest").GetString()!,
                envelope.GetProperty("optionId").GetString()!,
                envelope.GetProperty("usedFallback").GetBoolean(),
                envelope.GetProperty("selectedOutputDigest").GetString()!,
                coordinate,
                proposalDigest);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException
                  or InvalidOperationException
                  or FormatException
                  or OverflowException)
        {
            throw new ArgumentException(
                "Proposal envelope is malformed.",
                nameof(envelope),
                exception);
        }
    }

    internal static WorldAgentAuthoritativeProposal Create(
        WorldAgentDecisionDraft draft,
        WorldAgentJob job,
        WorldAgentJobResult result,
        string optionId,
        JsonElement selectedOutput)
    {
        return new WorldAgentAuthoritativeProposal(
            job.JobId,
            job.RunId,
            job.SemanticDigest,
            job.Kind,
            draft.OccurrenceId,
            draft.DraftId,
            draft.Digest,
            optionId,
            result.UsedFallback,
            CanonicalJsonDigest.ComputeSha256(selectedOutput),
            draft.ExpectedCoordinate);
    }

    private JsonElement WriteEnvelope()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            WriteBodyProperties(writer);
            writer.WriteString("proposalDigest", ProposalDigest);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    private JsonElement WriteBody()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            WriteBodyProperties(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    private void WriteBodyProperties(Utf8JsonWriter writer)
    {
        writer.WriteString(
            "contract",
            "game-agent.world-agent-authoritative-proposal.v1");
        writer.WriteString("jobId", JobId);
        writer.WriteString("runId", RunId);
        writer.WriteString("jobSemanticDigest", JobSemanticDigest);
        writer.WriteNumber("jobKind", (int)JobKind);
        writer.WriteString("occurrenceId", OccurrenceId);
        writer.WriteString("draftId", DraftId);
        writer.WriteString("draftDigest", DraftDigest);
        writer.WriteString("optionId", OptionId);
        writer.WriteBoolean("usedFallback", UsedFallback);
        writer.WriteString("selectedOutputDigest", SelectedOutputDigest);
        writer.WritePropertyName("expectedCoordinate");
        writer.WriteStartObject();
        writer.WriteString("worldId", ExpectedCoordinate.WorldId);
        writer.WriteString("timelineId", ExpectedCoordinate.TimelineId);
        writer.WriteNumber(
            "timelineEpoch",
            ExpectedCoordinate.TimelineEpoch);
        writer.WriteNumber(
            "saveRevision",
            ExpectedCoordinate.SaveRevision);
        writer.WriteNumber(
            "stateVersion",
            ExpectedCoordinate.StateVersion);
        writer.WriteString(
            "catalogDigest",
            ExpectedCoordinate.CatalogDigest);
        writer.WriteEndObject();
    }

    private static WorldAuthoritativeCoordinate ReadCoordinate(
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Expected coordinate must be an object.",
                nameof(value));
        }

        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "worldId",
            "timelineId",
            "timelineEpoch",
            "saveRevision",
            "stateVersion",
            "catalogDigest"
        };
        var names = value.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        if (names.Length != expectedNames.Count
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Any(name => !expectedNames.Contains(name)))
        {
            throw new ArgumentException(
                "Expected coordinate has an unsupported shape.",
                nameof(value));
        }

        return new WorldAuthoritativeCoordinate(
            value.GetProperty("worldId").GetString()!,
            value.GetProperty("timelineId").GetString()!,
            value.GetProperty("timelineEpoch").GetInt64(),
            value.GetProperty("saveRevision").GetInt64(),
            value.GetProperty("stateVersion").GetInt64(),
            value.GetProperty("catalogDigest").GetString()!);
    }

    private static string Digest(string value, string parameterName)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new ArgumentException(
                "A lowercase SHA-256 digest is required.",
                parameterName);
        }

        return value;
    }

    private static string Required(
        string value,
        string parameterName,
        int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "A bounded non-empty identifier is required.",
                parameterName);
        }

        return value;
    }
}

public enum WorldAgentDecisionProposalStatus
{
    Proposed = 0,
    Rejected = 1,
    Waiting = 2,
    WaitingForInput = 3,
    ReconciliationRequired = 4,
    Skipped = 5,
    Failed = 6,
    Cancelled = 7
}

public sealed class WorldAgentDecisionProposalResult
{
    internal WorldAgentDecisionProposalResult(
        WorldAgentDecisionProposalStatus status,
        string reasonCode,
        WorldAgentJobResult? agentResult = null,
        WorldAgentAuthoritativeProposal? proposal = null)
    {
        Status = status;
        ReasonCode = Required(reasonCode, nameof(reasonCode));
        AgentResult = agentResult;
        Proposal = proposal;
    }

    public WorldAgentDecisionProposalStatus Status { get; }

    public string ReasonCode { get; }

    public WorldAgentJobResult? AgentResult { get; }

    public WorldAgentAuthoritativeProposal? Proposal { get; }

    public bool Succeeded =>
        Status == WorldAgentDecisionProposalStatus.Proposed
        && Proposal is not null;

    private static string Required(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > 128)
        {
            throw new ArgumentException(
                "A bounded reason code is required.",
                parameterName);
        }

        return value;
    }
}

public enum WorldAgentDecisionCommitStatus
{
    Committed = 0,
    Replayed = 1,
    Rejected = 2,
    Cancelled = 3,
    Busy = 4,
    ReconciliationRequired = 5,
    IdempotencyConflict = 6
}

public sealed class WorldAgentDecisionCommitResult
{
    internal WorldAgentDecisionCommitResult(
        WorldAgentDecisionCommitStatus status,
        string reasonCode,
        WorldTransactionExecutionResult? execution = null)
    {
        Status = status;
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
            ? throw new ArgumentException(
                "A reason code is required.",
                nameof(reasonCode))
            : reasonCode;
        Execution = execution;
    }

    public WorldAgentDecisionCommitStatus Status { get; }

    public string ReasonCode { get; }

    public WorldTransactionExecutionResult? Execution { get; }

    public bool Succeeded =>
        Status is WorldAgentDecisionCommitStatus.Committed
            or WorldAgentDecisionCommitStatus.Replayed;
}

/// <summary>
/// Runs a bounded agent proposal and then maps its single declared option ID
/// to a preconstructed typed mutation. Proposal creation never mutates world
/// state; commit always revalidates the authoritative store and reconciles an
/// existing operation before any dispatch.
/// </summary>
public sealed class WorldAgentAuthoritativeDecisionCoordinator
{
    private readonly WorldAgentRuntimeBridge _bridge;
    private readonly IWorldAuthoritativeTransactionStore _store;
    private readonly WorldEventTransactionExecutor _transactions;

    public WorldAgentAuthoritativeDecisionCoordinator(
        WorldAgentRuntimeBridge bridge,
        IWorldAuthoritativeTransactionStore store)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transactions = new WorldEventTransactionExecutor(store);
    }

    public async ValueTask<WorldAgentDecisionProposalResult> ProposeAsync(
        WorldAgentDecisionDraft draft,
        WorldAgentJob job,
        CancellationToken cancellationToken = default)
    {
        if (draft is null)
        {
            throw new ArgumentNullException(nameof(draft));
        }

        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        var validation = draft.ValidateJob(job);
        if (validation is not null)
        {
            return Proposal(
                WorldAgentDecisionProposalStatus.Rejected,
                validation);
        }

        var current = await _store.ReadAsync(
                draft.ExpectedCoordinate.Address,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null
            || !draft.ExpectedCoordinate.IsExactMatch(current.Coordinate))
        {
            return Proposal(
                WorldAgentDecisionProposalStatus.Rejected,
                WorldAgentDecisionReasonCodes.StaleCoordinate);
        }

        var agentResult = await _bridge.ExecuteAsync(
                job,
                job.Coordinate,
                cancellationToken)
            .ConfigureAwait(false);
        return BuildProposalResult(draft, job, agentResult);
    }

    public async ValueTask<WorldAgentDecisionProposalResult>
        ResumeProposalAsync(
            WorldAgentDecisionDraft draft,
            WorldAgentJob job,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
    {
        if (draft is null)
        {
            throw new ArgumentNullException(nameof(draft));
        }

        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        var validation = draft.ValidateJob(job);
        if (validation is not null)
        {
            return Proposal(
                WorldAgentDecisionProposalStatus.Rejected,
                validation);
        }

        var current = await _store.ReadAsync(
                draft.ExpectedCoordinate.Address,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is null
            || !draft.ExpectedCoordinate.IsExactMatch(current.Coordinate))
        {
            return Proposal(
                WorldAgentDecisionProposalStatus.Rejected,
                WorldAgentDecisionReasonCodes.StaleCoordinate);
        }

        var agentResult = await _bridge.ResumeAsync(
                job,
                job.Coordinate,
                continuation,
                reconciler,
                cancellationToken)
            .ConfigureAwait(false);
        return BuildProposalResult(draft, job, agentResult);
    }

    internal static WorldAgentDecisionProposalResult BuildProposalResult(
        WorldAgentDecisionDraft draft,
        WorldAgentJob job,
        WorldAgentJobResult agentResult)
    {
        if (agentResult.Status != WorldAgentJobStatus.Completed
            || !agentResult.IsAuthoritativeProposal
            || !agentResult.Output.HasValue)
        {
            return Proposal(
                MapProposalStatus(agentResult.Status),
                agentResult.ReasonCode,
                agentResult);
        }

        var output = agentResult.Output.Value;
        if (!TryReadOptionId(output, out var optionId))
        {
            return Proposal(
                WorldAgentDecisionProposalStatus.Rejected,
                WorldAgentDecisionReasonCodes.OutputInvalid,
                agentResult);
        }

        if (!draft.TryGetOption(optionId!, out _))
        {
            return Proposal(
                WorldAgentDecisionProposalStatus.Rejected,
                WorldAgentDecisionReasonCodes.OptionNotDeclared,
                agentResult);
        }

        return Proposal(
            WorldAgentDecisionProposalStatus.Proposed,
            WorldAgentDecisionReasonCodes.ProposalReady,
            agentResult,
            WorldAgentAuthoritativeProposal.Create(
                draft,
                job,
                agentResult,
                optionId!,
                output));
    }

    public async ValueTask<WorldAgentDecisionCommitResult> CommitAsync(
        WorldAgentDecisionDraft draft,
        WorldAgentJob job,
        WorldAgentAuthoritativeProposal proposal,
        object? hostContext = null,
        CancellationToken cancellationToken = default)
    {
        if (draft is null)
        {
            throw new ArgumentNullException(nameof(draft));
        }

        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        if (proposal is null)
        {
            throw new ArgumentNullException(nameof(proposal));
        }

        var validation = draft.ValidateJob(job);
        if (validation is not null)
        {
            return Rejected(validation);
        }

        validation = ValidateProposal(draft, job, proposal);
        if (validation is not null)
        {
            return Rejected(validation);
        }

        if (!draft.TryGetOption(proposal.OptionId, out var option)
            || option is null)
        {
            return Rejected(
                WorldAgentDecisionReasonCodes.OptionNotDeclared);
        }

        var request = new WorldEventTransactionExecutionRequest(
            draft.Occurrence,
            draft.ExpectedCoordinate,
            draft.CommandId,
            draft.OperationId,
            option.Effect,
            hostContext: hostContext);
        var reconciliation = await _transactions.ReconcileAsync(
                request.TransactionRequest,
                cancellationToken)
            .ConfigureAwait(false);
        var execution = reconciliation.Status
            == WorldTransactionExecutionStatus.NotFound
            ? await _transactions.ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false)
            : reconciliation;
        return MapCommit(execution);
    }

    internal static string? ValidateProposal(
        WorldAgentDecisionDraft draft,
        WorldAgentJob job,
        WorldAgentAuthoritativeProposal proposal)
    {
        if (!string.Equals(
                proposal.JobId,
                job.JobId,
                StringComparison.Ordinal)
            || !string.Equals(
                proposal.RunId,
                job.RunId,
                StringComparison.Ordinal)
            || !string.Equals(
                proposal.JobSemanticDigest,
                job.SemanticDigest,
                StringComparison.Ordinal)
            || proposal.JobKind != job.Kind
            || !string.Equals(
                proposal.OccurrenceId,
                draft.OccurrenceId,
                StringComparison.Ordinal)
            || !string.Equals(
                proposal.DraftId,
                draft.DraftId,
                StringComparison.Ordinal)
            || !string.Equals(
                proposal.DraftDigest,
                draft.Digest,
                StringComparison.Ordinal)
            || !proposal.ExpectedCoordinate.IsExactMatch(
                draft.ExpectedCoordinate))
        {
            return WorldAgentDecisionReasonCodes.ProposalBindingMismatch;
        }

        var selectedOutput = WriteSelectedOutput(proposal.OptionId);
        if (!string.Equals(
                proposal.SelectedOutputDigest,
                CanonicalJsonDigest.ComputeSha256(selectedOutput),
                StringComparison.Ordinal))
        {
            return WorldAgentDecisionReasonCodes.ProposalBindingMismatch;
        }

        if (proposal.UsedFallback)
        {
            if (job.FailurePolicy != WorldAgentFailurePolicy.UseFallback
                || !job.FallbackOutput.HasValue
                || !string.Equals(
                    proposal.SelectedOutputDigest,
                    CanonicalJsonDigest.ComputeSha256(
                        job.FallbackOutput.Value),
                    StringComparison.Ordinal))
            {
                return WorldAgentDecisionReasonCodes
                    .FallbackBindingMismatch;
            }
        }

        return null;
    }

    private static bool TryReadOptionId(
        JsonElement output,
        out string? optionId)
    {
        optionId = null;
        if (output.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        using var properties = output.EnumerateObject();
        if (!properties.MoveNext())
        {
            return false;
        }

        var property = properties.Current;
        if (!string.Equals(
                property.Name,
                "optionId",
                StringComparison.Ordinal)
            || property.Value.ValueKind != JsonValueKind.String
            || properties.MoveNext())
        {
            return false;
        }

        optionId = property.Value.GetString();
        return !string.IsNullOrWhiteSpace(optionId);
    }

    private static JsonElement WriteSelectedOutput(string optionId)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("optionId", optionId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    private static WorldAgentDecisionProposalStatus MapProposalStatus(
        WorldAgentJobStatus status)
    {
        return status switch
        {
            WorldAgentJobStatus.Waiting =>
                WorldAgentDecisionProposalStatus.Waiting,
            WorldAgentJobStatus.WaitingForInput =>
                WorldAgentDecisionProposalStatus.WaitingForInput,
            WorldAgentJobStatus.ReconciliationRequired =>
                WorldAgentDecisionProposalStatus.ReconciliationRequired,
            WorldAgentJobStatus.Skipped =>
                WorldAgentDecisionProposalStatus.Skipped,
            WorldAgentJobStatus.Cancelled =>
                WorldAgentDecisionProposalStatus.Cancelled,
            _ => WorldAgentDecisionProposalStatus.Failed
        };
    }

    private static WorldAgentDecisionProposalResult Proposal(
        WorldAgentDecisionProposalStatus status,
        string reasonCode,
        WorldAgentJobResult? agentResult = null,
        WorldAgentAuthoritativeProposal? proposal = null)
    {
        return new WorldAgentDecisionProposalResult(
            status,
            reasonCode,
            agentResult,
            proposal);
    }

    private static WorldAgentDecisionCommitResult Rejected(
        string reasonCode)
    {
        return new WorldAgentDecisionCommitResult(
            WorldAgentDecisionCommitStatus.Rejected,
            reasonCode);
    }

    private static WorldAgentDecisionCommitResult MapCommit(
        WorldTransactionExecutionResult execution)
    {
        var status = execution.Status switch
        {
            WorldTransactionExecutionStatus.Committed =>
                WorldAgentDecisionCommitStatus.Committed,
            WorldTransactionExecutionStatus.Replayed =>
                WorldAgentDecisionCommitStatus.Replayed,
            WorldTransactionExecutionStatus.Cancelled =>
                WorldAgentDecisionCommitStatus.Cancelled,
            WorldTransactionExecutionStatus.Busy =>
                WorldAgentDecisionCommitStatus.Busy,
            WorldTransactionExecutionStatus.ReconciliationRequired =>
                WorldAgentDecisionCommitStatus.ReconciliationRequired,
            WorldTransactionExecutionStatus.IdempotencyConflict =>
                WorldAgentDecisionCommitStatus.IdempotencyConflict,
            _ => WorldAgentDecisionCommitStatus.Rejected
        };
        return new WorldAgentDecisionCommitResult(
            status,
            execution.ReasonCode,
            execution);
    }
}

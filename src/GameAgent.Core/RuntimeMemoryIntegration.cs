using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public static class RuntimeMemoryIntegrationReasonCodes
{
    public const string PolicyError = "memory_policy_error";
    public const string PolicyResultInvalid = "memory_policy_result_invalid";
    public const string RecallFailed = "memory_recall_failed";
    public const string RecallIncomplete = "memory_recall_incomplete";
    public const string CommitFailed = "memory_commit_failed";
    public const string RecoveryPolicyMismatch =
        "memory_recovery_policy_mismatch";
    public const string RecoveryRecordInvalid =
        "memory_recovery_record_invalid";
}

public sealed class RuntimeMemoryIntegrationException
    : InvalidOperationException
{
    public RuntimeMemoryIntegrationException(
        string reasonCode,
        string message)
        : base(message)
    {
        ReasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
    }

    public string ReasonCode { get; }
}

public sealed class RuntimeMemoryIntegrationOptions
{
    public int MaxRecallContextCandidates { get; set; } = 64;

    public int MaxCommitMutations { get; set; } = 128;

    public int MaxCommitAggregateContentUtf8Bytes { get; set; } =
        512 * 1_024;

    internal RuntimeMemoryIntegrationOptions Snapshot()
    {
        if (MaxRecallContextCandidates is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRecallContextCandidates));
        }

        if (MaxCommitMutations is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCommitMutations));
        }

        if (MaxCommitAggregateContentUtf8Bytes is < 1
            or > 768 * 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCommitAggregateContentUtf8Bytes));
        }

        return new RuntimeMemoryIntegrationOptions
        {
            MaxRecallContextCandidates = MaxRecallContextCandidates,
            MaxCommitMutations = MaxCommitMutations,
            MaxCommitAggregateContentUtf8Bytes =
                MaxCommitAggregateContentUtf8Bytes
        };
    }
}

/// <summary>
/// Describes one bounded recall operation chosen by game policy.
/// Recalled values always enter the prompt as optional, derived, untrusted
/// context; this plan cannot elevate them to host authority.
/// </summary>
public sealed class RuntimeMemoryRecallPlan
{
    public RuntimeMemoryRecallPlan(
        MemoryQuery query,
        string? prefetchKey = null,
        bool allowPartialResults = false,
        int contextPriority = -100)
    {
        if (contextPriority is < -10_000 or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextPriority));
        }

        Query = query ?? throw new ArgumentNullException(nameof(query));
        PrefetchKey = prefetchKey is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                prefetchKey,
                256,
                nameof(prefetchKey));
        AllowPartialResults = allowPartialResults;
        ContextPriority = contextPriority;
    }

    public MemoryQuery Query { get; }

    /// <summary>
    /// Optional one-time key previously admitted through
    /// <see cref="RuntimeMemoryLifecycle.Prefetch"/>.
    /// A cache miss falls back to the plan's query.
    /// </summary>
    public string? PrefetchKey { get; }

    public bool AllowPartialResults { get; }

    public int ContextPriority { get; }
}

public sealed class RuntimeMemoryRecallContext
{
    internal RuntimeMemoryRecallContext(
        AgentRun run,
        string turnId,
        IReadOnlyList<NormalizedMessage> transcript,
        IReadOnlyList<ContextCandidate> pendingContext)
    {
        RunId = run.RunId;
        AgentId = run.AgentId;
        WorldId = run.WorldId;
        SessionId = run.SessionId;
        RuntimeGeneration = run.RuntimeGeneration;
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        Coordinate = GameContextEnvelope.TryRead(run, out var coordinate)
            ? coordinate
            : null;
        Transcript = SnapshotMessages(transcript);
        PendingContext = SnapshotContext(pendingContext);
    }

    public string RunId { get; }

    public string AgentId { get; }

    public string WorldId { get; }

    public string? SessionId { get; }

    public long RuntimeGeneration { get; }

    public string TurnId { get; }

    public GameContextCoordinate? Coordinate { get; }

    public IReadOnlyList<NormalizedMessage> Transcript { get; }

    public IReadOnlyList<ContextCandidate> PendingContext { get; }

    private static IReadOnlyList<NormalizedMessage> SnapshotMessages(
        IReadOnlyList<NormalizedMessage> source)
    {
        var count = source.Count;
        var snapshot = new NormalizedMessage[count];
        for (var index = 0; index < count; index++)
        {
            var item = source[index]
                       ?? throw new InvalidDataException(
                           "The runtime transcript contains a null message.");
            snapshot[index] = NormalizedMessageJournalCodec.Decode(
                NormalizedMessageJournalCodec.Encode(item));
        }

        return new ReadOnlyCollection<NormalizedMessage>(snapshot);
    }

    private static IReadOnlyList<ContextCandidate> SnapshotContext(
        IReadOnlyList<ContextCandidate> source)
    {
        var count = source.Count;
        var snapshot = new ContextCandidate[count];
        for (var index = 0; index < count; index++)
        {
            snapshot[index] = source[index]?.Clone()
                              ?? throw new InvalidDataException(
                                  "The runtime context contains a null item.");
        }

        return new ReadOnlyCollection<ContextCandidate>(snapshot);
    }
}

/// <summary>
/// Immutable evidence supplied after every action in the turn has a durable,
/// terminal receipt. Unknown receipts are never included.
/// </summary>
public sealed class RuntimeMemoryCommitContext
{
    internal RuntimeMemoryCommitContext(
        AgentRun run,
        string turnId,
        string commitId,
        IReadOnlyList<string> committedSourceEventIds,
        IReadOnlyList<ActionReceipt> receipts,
        IReadOnlyList<NormalizedMessage> committedTranscript,
        NormalizedMessage? assistantMessage,
        JsonElement? assistantOutput)
    {
        RunId = run.RunId;
        AgentId = run.AgentId;
        WorldId = run.WorldId;
        SessionId = run.SessionId;
        RuntimeGeneration = run.RuntimeGeneration;
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        CommitId = RuntimeGuard.RequiredUtf8(
            commitId,
            256,
            nameof(commitId));
        if (committedSourceEventIds is null
            || committedSourceEventIds.Count == 0
            || committedSourceEventIds.Count > 256)
        {
            throw new InvalidDataException(
                "Committed memory evidence requires bounded source events.");
        }

        CommittedSourceEventIds = new ReadOnlyCollection<string>(
            committedSourceEventIds
                .Select(
                    item => RuntimeGuard.RequiredUtf8(
                        item,
                        256,
                        nameof(committedSourceEventIds)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray());
        Coordinate = GameContextEnvelope.TryRead(run, out var coordinate)
            ? coordinate
            : null;

        var count = receipts.Count;
        var snapshot = new ActionReceipt[count];
        for (var index = 0; index < count; index++)
        {
            var receipt = receipts[index]
                          ?? throw new InvalidDataException(
                              "A committed receipt collection contains null.");
            var clone = ProtocolJson.DeserializeActionReceipt(
                ProtocolJson.Serialize(receipt));
            ProtocolValidator.EnsureValid(clone);
            if (string.Equals(
                    clone.Status,
                    ReceiptStatuses.Unknown,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Unknown action receipts cannot drive memory writes.");
            }

            snapshot[index] = clone;
        }

        Receipts = new ReadOnlyCollection<ActionReceipt>(snapshot);
        CommittedTranscript = SnapshotTranscript(committedTranscript);
        AssistantMessage = assistantMessage is null
            ? null
            : SnapshotAssistant(assistantMessage);
        if (assistantOutput.HasValue)
        {
            JsonValueInspector.ValidateAndMeasure(
                assistantOutput.Value,
                new JsonValueLimits(maxUtf8Bytes: 1_048_576),
                nameof(assistantOutput));
            AssistantOutput = assistantOutput.Value.Clone();
        }
    }

    public string RunId { get; }

    public string AgentId { get; }

    public string WorldId { get; }

    public string? SessionId { get; }

    public long RuntimeGeneration { get; }

    public string TurnId { get; }

    /// <summary>
    /// Stable idempotency identity for this derived memory batch.
    /// </summary>
    public string CommitId { get; }

    /// <summary>
    /// Durable runtime event identities that may be cited by an upsert's
    /// <see cref="MemoryProvenance.SourceEventId"/>.
    /// </summary>
    public IReadOnlyList<string> CommittedSourceEventIds { get; }

    public GameContextCoordinate? Coordinate { get; }

    public IReadOnlyList<ActionReceipt> Receipts { get; }

    /// <summary>
    /// Transcript state already acknowledged by the durable journal. It is
    /// derived evidence, never proof of a host-side action.
    /// </summary>
    public IReadOnlyList<NormalizedMessage> CommittedTranscript { get; }

    public NormalizedMessage? AssistantMessage { get; }

    public JsonElement? AssistantOutput { get; }

    private static IReadOnlyList<NormalizedMessage> SnapshotTranscript(
        IReadOnlyList<NormalizedMessage> transcript)
    {
        var count = transcript.Count;
        var snapshot = new NormalizedMessage[count];
        for (var index = 0; index < count; index++)
        {
            var message = transcript[index]
                          ?? throw new InvalidDataException(
                              "A committed transcript contains null.");
            snapshot[index] = NormalizedMessageJournalCodec.Decode(
                NormalizedMessageJournalCodec.Encode(message));
        }

        return new ReadOnlyCollection<NormalizedMessage>(snapshot);
    }

    private static NormalizedMessage SnapshotAssistant(
        NormalizedMessage message)
    {
        if (!string.Equals(
                message.Role,
                NormalizedRoles.Assistant,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A committed assistant outcome has the wrong role.");
        }

        return NormalizedMessageJournalCodec.Decode(
            NormalizedMessageJournalCodec.Encode(message));
    }
}

/// <summary>
/// Game-owned policy for querying and deriving memory. Implementations must be
/// deterministic, bounded, synchronous, and side-effect free. The runtime
/// performs provider I/O and atomic writes after validating policy output.
/// </summary>
public interface IRuntimeMemoryPolicy
{
    string PolicyId { get; }

    string Version { get; }

    RuntimeMemoryRecallPlan? PlanRecall(
        RuntimeMemoryRecallContext context);

    IReadOnlyList<MemoryMutation> SelectCommittedMutations(
        RuntimeMemoryCommitContext context);
}

internal sealed class RuntimeMemoryAgentLoop
{
    internal const string PolicySnapshotExtension = "memoryPolicy";
    internal const string RecallSnapshotExtension = "memoryRecall";
    private const string CandidateCategory = "recalled_memory";
    private const string CandidateProvenancePrefix =
        "memory:untrusted-derived:";

    private readonly RuntimeMemoryLifecycle _lifecycle;
    private readonly IRuntimeMemoryPolicy _policy;
    private readonly RuntimeMemoryIntegrationOptions _options;

    public RuntimeMemoryAgentLoop(
        RuntimeMemoryLifecycle lifecycle,
        IRuntimeMemoryPolicy policy,
        RuntimeMemoryIntegrationOptions? options)
    {
        _lifecycle = lifecycle
                     ?? throw new ArgumentNullException(nameof(lifecycle));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = (options ?? new RuntimeMemoryIntegrationOptions())
            .Snapshot();
        PolicyId = ReadPolicyIdentity(
            () => _policy.PolicyId,
            nameof(policy));
        PolicyVersion = ReadPolicyIdentity(
            () => _policy.Version,
            nameof(policy));
    }

    public string PolicyId { get; }

    public string PolicyVersion { get; }

    public JsonElement PolicyEvidence => JsonArrayBuilder.Object(
        ("policyId", JsonArrayBuilder.String(PolicyId)),
        ("version", JsonArrayBuilder.String(PolicyVersion)));

    public async ValueTask<RuntimeMemoryRecallSelection> RecallAsync(
        AgentRun run,
        string turnId,
        IReadOnlyList<NormalizedMessage> transcript,
        IReadOnlyList<ContextCandidate> pendingContext,
        int maximumContextCandidates,
        CancellationToken cancellationToken)
    {
        if (maximumContextCandidates < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumContextCandidates));
        }

        RuntimeMemoryRecallPlan? plan;
        try
        {
            plan = _policy.PlanRecall(
                new RuntimeMemoryRecallContext(
                    run,
                    turnId,
                    transcript,
                    pendingContext));
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.PolicyError,
                "The game memory policy failed while planning recall.");
        }

        if (plan is null)
        {
            return RuntimeMemoryRecallSelection.Empty(
                PolicyId,
                PolicyVersion);
        }

        var recallQuery = BindRecallQuery(run, plan.Query);
        var selectedLimit = Math.Min(
            maximumContextCandidates,
            Math.Min(
                _options.MaxRecallContextCandidates,
                recallQuery.MaxResults));
        if (selectedLimit == 0)
        {
            return new RuntimeMemoryRecallSelection(
                Array.Empty<ContextCandidate>(),
                JsonArrayBuilder.Object(
                    ("policyId", JsonArrayBuilder.String(PolicyId)),
                    ("version", JsonArrayBuilder.String(PolicyVersion)),
                    ("planned", JsonArrayBuilder.Boolean(true)),
                    ("reason", JsonArrayBuilder.String(
                        "context_capacity")),
                    ("resultCount", JsonArrayBuilder.Number(0)),
                    ("selectedCount", JsonArrayBuilder.Number(0))));
        }

        MemoryRecallReport report;
        var usedPrefetch = false;
        try
        {
            if (plan.PrefetchKey is not null)
            {
                var prefetched = await _lifecycle.TakePrefetchedAsync(
                        plan.PrefetchKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (prefetched is not null)
                {
                    report = FilterRecallReport(
                        prefetched,
                        recallQuery,
                        run.SessionId);
                    usedPrefetch = true;
                }
                else
                {
                    report = await _lifecycle.RecallAsync(
                            recallQuery,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                report = await _lifecycle.RecallAsync(
                        recallQuery,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!usedPrefetch)
            {
                report = FilterRecallReport(
                    report,
                    recallQuery,
                    run.SessionId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecallFailed,
                "Runtime-managed memory recall failed.");
        }

        if (report.IsPartial && !plan.AllowPartialResults)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecallIncomplete,
                "Runtime-managed memory recall was incomplete.");
        }

        var resultCount = report.Results.Count;
        var candidates = new List<ContextCandidate>(
            Math.Min(resultCount, selectedLimit));
        var memoryIds = new HashSet<string>(StringComparer.Ordinal);
        var candidateIds = new HashSet<string>(
            pendingContext.Select(item => item.Id),
            StringComparer.Ordinal);
        for (var index = 0;
             index < resultCount && candidates.Count < selectedLimit;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = report.Results[index]
                         ?? throw new RuntimeMemoryIntegrationException(
                             RuntimeMemoryIntegrationReasonCodes
                                 .PolicyResultInvalid,
                             "A memory provider returned a null result.");
            var record = result.Record
                         ?? throw new RuntimeMemoryIntegrationException(
                             RuntimeMemoryIntegrationReasonCodes
                                 .PolicyResultInvalid,
                             "A memory provider returned a result without "
                             + "a record.");
            if (!memoryIds.Add(record.MemoryId))
            {
                continue;
            }

            var candidateId = CandidateId(record.MemoryId);
            if (!candidateIds.Add(candidateId))
            {
                continue;
            }

            candidates.Add(
                new ContextCandidate(
                    candidateId,
                    CandidateCategory,
                    record.Content,
                    plan.ContextPriority,
                    required: false,
                    canDefer: false,
                    expiresAt: record.ExpiresAt,
                    provenance:
                        CandidateProvenancePrefix + record.MemoryId));
        }

        return new RuntimeMemoryRecallSelection(
            new ReadOnlyCollection<ContextCandidate>(candidates),
            RecallEvidence(
                plan,
                report,
                usedPrefetch,
                resultCount,
                candidates));
    }

    public PreparedRuntimeMemoryCommit PrepareCommit(
        AgentRun run,
        string turnId,
        IReadOnlyList<string> committedSourceEventIds,
        IReadOnlyList<ActionReceipt> receipts,
        IReadOnlyList<NormalizedMessage> committedTranscript,
        NormalizedMessage? assistantMessage = null,
        JsonElement? assistantOutput = null)
    {
        var commitId = CommitId(
            run.RunId,
            run.RuntimeGeneration,
            turnId);
        var context = new RuntimeMemoryCommitContext(
            run,
            turnId,
            commitId,
            committedSourceEventIds,
            receipts,
            committedTranscript,
            assistantMessage,
            assistantOutput);
        IReadOnlyList<MemoryMutation>? proposed;
        try
        {
            proposed = _policy.SelectCommittedMutations(
                context);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.PolicyError,
                "The game memory policy failed while deriving committed "
                + "mutations.");
        }

        if (proposed is null)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.PolicyResultInvalid,
                "The game memory policy returned a null mutation collection.");
        }

        MemoryMutation[] snapshot;
        try
        {
            var count = proposed.Count;
            if (count < 0 || count > _options.MaxCommitMutations)
            {
                throw new RuntimeContentLimitException(
                    nameof(proposed),
                    "memory_commit_mutation_count_exceeded",
                    "The runtime memory policy returned too many mutations.");
            }

            if (count == 0)
            {
                snapshot = Array.Empty<MemoryMutation>();
            }
            else
            {
                var bounded = new MemoryMutation[count];
                for (var index = 0; index < count; index++)
                {
                    bounded[index] = proposed[index]
                                     ?? throw new InvalidDataException(
                                         "A memory mutation is null.");
                }

                snapshot = MemoryBatchValidator.Snapshot(
                    bounded,
                    CancellationToken.None);
            }

            var aggregateBytes = 0L;
            for (var index = 0; index < snapshot.Length; index++)
            {
                var mutation = snapshot[index];
                var expected = mutation.ExpectedRecord;
                if (expected is not null
                    && !ExpectationBelongsToRun(
                        expected,
                        run,
                        context.Coordinate))
                {
                    throw new RuntimeMemoryIntegrationException(
                        RuntimeMemoryIntegrationReasonCodes.PolicyResultInvalid,
                        "A runtime-managed memory expectation targets another game context.");
                }

                if (mutation.Kind != MemoryMutationKind.Upsert)
                {
                    if (expected is null)
                    {
                        throw new RuntimeMemoryIntegrationException(
                            RuntimeMemoryIntegrationReasonCodes.PolicyResultInvalid,
                            "A runtime-managed memory delete requires an expected record.");
                    }

                    continue;
                }

                var record = mutation.Record
                             ?? throw new InvalidDataException(
                                 "An upsert mutation has no record.");
                var provenance = record.Provenance;
                if (provenance is null
                    || !provenance.Committed
                    || !string.Equals(
                        provenance.WorldId,
                        run.WorldId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        provenance.SourceRunId,
                        run.RunId,
                        StringComparison.Ordinal)
                    || !context.CommittedSourceEventIds.Contains(
                        provenance.SourceEventId,
                        StringComparer.Ordinal)
                    || provenance.SessionId is not null
                    && !string.Equals(
                        provenance.SessionId,
                        run.SessionId,
                        StringComparison.Ordinal)
                    || !AuthorityBelongsToRun(
                        MemoryRecordAuthorityEnvelope.FromRecord(record),
                        run,
                        context.Coordinate))
                {
                    throw new RuntimeMemoryIntegrationException(
                        RuntimeMemoryIntegrationReasonCodes
                            .PolicyResultInvalid,
                        "A runtime-managed memory upsert has provenance "
                        + "outside the committed run.");
                }

                aggregateBytes = checked(
                    aggregateBytes
                    + Encoding.UTF8.GetByteCount(
                        record.Content.GetRawText()));
                if (aggregateBytes
                    > _options.MaxCommitAggregateContentUtf8Bytes)
                {
                    throw new RuntimeContentLimitException(
                        nameof(proposed),
                        "memory_commit_content_bytes_exceeded",
                        "Runtime-managed memory commit content is too large.");
                }
            }
        }
        catch (RuntimeMemoryIntegrationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.PolicyResultInvalid,
                "The game memory policy returned invalid mutations.");
        }

        return new PreparedRuntimeMemoryCommit(
            commitId,
            turnId,
            PolicyId,
            PolicyVersion,
            RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(snapshot),
            snapshot);
    }

    public async ValueTask ApplyPreparedAsync(
        PreparedRuntimeMemoryCommit prepared,
        CancellationToken cancellationToken)
    {
        if (prepared.Mutations.Count == 0)
        {
            return;
        }

        try
        {
            if (prepared.MutationContractVersion
                == PreparedRuntimeMemoryCommit.LegacyMutationContractVersion)
            {
                await _lifecycle.ReplayLegacyIdempotentAtomicBatchAsync(
                        prepared.CommitId,
                        prepared.Mutations,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _lifecycle.CommitIdempotentAtomicBatchAsync(
                        prepared.CommitId,
                        prepared.Mutations,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MemoryLegacyReplayNotSupportedException)
        {
            // This is a permanent store-capability mismatch, not a transient
            // commit failure. Preserve the stable migration signal so a host
            // can stop retrying and configure an explicit legacy replay bridge.
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.CommitFailed,
                "The prepared runtime memory batch was not committed.");
        }
    }

    public void ValidatePreparedForRun(
        PreparedRuntimeMemoryCommit prepared,
        AgentRun run,
        IReadOnlyCollection<string> committedSourceEventIds,
        bool enforceConfiguredLimits = true,
        bool enforcePolicyIdentity = true)
    {
        if (prepared is null)
        {
            throw new ArgumentNullException(nameof(prepared));
        }

        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (committedSourceEventIds is null)
        {
            throw new ArgumentNullException(nameof(committedSourceEventIds));
        }

        if (!string.Equals(
                prepared.CommitId,
                CommitId(
                    run.RunId,
                    run.RuntimeGeneration,
                    prepared.TurnId),
                StringComparison.Ordinal)
            || enforcePolicyIdentity
            && (!string.Equals(
                    prepared.PolicyId,
                    PolicyId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    prepared.PolicyVersion,
                    PolicyVersion,
                    StringComparison.Ordinal))
            || prepared.Mutations.Count
            > (enforceConfiguredLimits
                ? _options.MaxCommitMutations
                : 256)
            || prepared.MutationContractVersion
            is not PreparedRuntimeMemoryCommit.LegacyMutationContractVersion
                and not RuntimeMemoryMutationContract.CurrentVersion)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                "A prepared memory commit has invalid identity or capacity.");
        }

        MemoryMutation[] snapshot;
        try
        {
            snapshot = prepared.Mutations.Count == 0
                ? Array.Empty<MemoryMutation>()
                : MemoryBatchValidator.Snapshot(
                    prepared.Mutations,
                    CancellationToken.None);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                "A prepared memory commit contains invalid mutations.");
        }

        if (!string.Equals(
                prepared.PayloadDigest,
                RuntimeMemoryCommitJournalCodec.ComputeMutationDigest(snapshot),
                StringComparison.Ordinal))
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                "A prepared memory commit has an invalid payload digest.");
        }

        var bytes = 0L;
        var coordinate = GameContextEnvelope.TryRead(run, out var current)
            ? current
            : null;
        foreach (var mutation in snapshot)
        {
            var expected = mutation.ExpectedRecord;
            var legacy = prepared.MutationContractVersion
                         == PreparedRuntimeMemoryCommit
                             .LegacyMutationContractVersion;
            if (legacy && expected is not null)
            {
                throw new RuntimeMemoryIntegrationException(
                    RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                    "A legacy prepared memory mutation has an unexpected "
                    + "record expectation.");
            }

            if (expected is not null
                && !ExpectationBelongsToRun(
                    expected,
                    run,
                    coordinate))
            {
                throw new RuntimeMemoryIntegrationException(
                    RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                    "A prepared memory expectation targets another game context.");
            }

            if (mutation.Kind != MemoryMutationKind.Upsert)
            {
                if (!legacy && expected is null)
                {
                    throw new RuntimeMemoryIntegrationException(
                        RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                        "A prepared memory delete has no expected record.");
                }

                continue;
            }

            var record = mutation.Record
                         ?? throw new RuntimeMemoryIntegrationException(
                             RuntimeMemoryIntegrationReasonCodes
                                 .RecoveryRecordInvalid,
                             "A prepared upsert has no memory record.");
            var provenance = record.Provenance;
            if (provenance is null
                || !provenance.Committed
                || !string.Equals(
                    provenance.WorldId,
                    run.WorldId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    provenance.SourceRunId,
                    run.RunId,
                    StringComparison.Ordinal)
                || !committedSourceEventIds.Contains(
                    provenance.SourceEventId,
                    StringComparer.Ordinal)
                || provenance.SessionId is not null
                && !string.Equals(
                    provenance.SessionId,
                    run.SessionId,
                    StringComparison.Ordinal)
                || !AuthorityBelongsToRun(
                    MemoryRecordAuthorityEnvelope.FromRecord(record),
                    run,
                    coordinate))
            {
                throw new RuntimeMemoryIntegrationException(
                    RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                    "A prepared memory record is outside the recovered run.");
            }

            bytes = checked(
                bytes
                + Encoding.UTF8.GetByteCount(
                    record.Content.GetRawText()));
            if (bytes
                > (enforceConfiguredLimits
                    ? _options.MaxCommitAggregateContentUtf8Bytes
                    : 768 * 1_024))
            {
                throw new RuntimeMemoryIntegrationException(
                    RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                    "A prepared memory commit exceeds its content bound.");
            }
        }
    }

    public void EnsureRecoveryPolicy(TurnSnapshot? snapshot, string turnId)
    {
        if (snapshot is null
            || !string.Equals(
                snapshot.TurnId,
                turnId,
                StringComparison.Ordinal)
            || !snapshot.Extensions.TryGetValue(
                PolicySnapshotExtension,
                out var evidence)
            || evidence.ValueKind != JsonValueKind.Object
            || !evidence.TryGetProperty("policyId", out var policyId)
            || !evidence.TryGetProperty("version", out var version)
            || policyId.ValueKind != JsonValueKind.String
            || version.ValueKind != JsonValueKind.String
            || !string.Equals(
                policyId.GetString(),
                PolicyId,
                StringComparison.Ordinal)
            || !string.Equals(
                version.GetString(),
                PolicyVersion,
                StringComparison.Ordinal))
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecoveryPolicyMismatch,
                "The current memory policy does not match the policy "
                + "captured for the recoverable turn.");
        }
    }

    private static MemoryQuery BindRecallQuery(
        AgentRun run,
        MemoryQuery requested)
    {
        if (requested.WorldId is not null
            && !string.Equals(
                requested.WorldId,
                run.WorldId,
                StringComparison.Ordinal))
        {
            throw InvalidRecallPlan(
                "The memory query targets a different world.");
        }
        if (requested.SessionId is not null
            && !string.Equals(
                requested.SessionId,
                run.SessionId,
                StringComparison.Ordinal))
        {
            throw InvalidRecallPlan(
                "The memory query targets a different session.");
        }

        var coordinate = GameContextEnvelope.TryRead(run, out var value)
            ? value
            : null;
        var maximumSaveRevision = requested.MaximumSaveRevision;
        var timelineId = requested.TimelineId;
        var timelineEpoch = requested.EnforceTimelineEpoch
            ? requested.TimelineEpoch
            : null;
        var observer = requested.Observer;
        var gameTime = requested.GameTime;
        var includeAllPerspectives = requested.IncludeAllPerspectives;
        if (coordinate is not null)
        {
            if (maximumSaveRevision.HasValue
                && maximumSaveRevision.Value > coordinate.SaveRevision)
            {
                throw InvalidRecallPlan(
                    "The memory query reads beyond the current save revision.");
            }
            maximumSaveRevision ??= coordinate.SaveRevision;

            if (timelineId is not null
                && !string.Equals(
                    timelineId,
                    coordinate.TimelineId,
                    StringComparison.Ordinal))
            {
                throw InvalidRecallPlan(
                    "The memory query targets a different timeline.");
            }
            timelineId = coordinate.TimelineId;

            if (coordinate.Observer is not null)
            {
                if (includeAllPerspectives
                    || observer is not null
                    && !coordinate.Observer.IsSameIncarnation(observer))
                {
                    throw InvalidRecallPlan(
                        "The memory query exceeds the current observer perspective.");
                }

                observer = coordinate.Observer;
                includeAllPerspectives = false;
            }

            if (coordinate.GameTime is not null)
            {
                if (timelineEpoch.HasValue
                    && timelineEpoch.Value != coordinate.GameTime.Epoch)
                {
                    throw InvalidRecallPlan(
                        "The memory query targets a different timeline epoch.");
                }

                if (gameTime is not null
                    && (!coordinate.GameTime.IsComparableTo(gameTime)
                        || gameTime.Tick > coordinate.GameTime.Tick))
                {
                    throw InvalidRecallPlan(
                        "The memory query reads beyond the current game time.");
                }

                gameTime ??= coordinate.GameTime;
            }
        }

        return new MemoryQuery(
            requested.Scope,
            requested.Query,
            requested.RequiredTags,
            requested.MaxResults,
            requested.MaxUtf8Bytes,
            requested.Now,
            run.WorldId,
            requested.SessionId,
            maximumSaveRevision,
            requested.RequireCommittedProvenance,
            timelineId,
            observer,
            gameTime,
            includeAllPerspectives,
            timelineEpoch);
    }

    private static RuntimeMemoryIntegrationException InvalidRecallPlan(
        string message)
    {
        return new RuntimeMemoryIntegrationException(
            RuntimeMemoryIntegrationReasonCodes.PolicyResultInvalid,
            message);
    }

    private static bool ExpectationBelongsToRun(
        MemoryRecordExpectation expectation,
        AgentRun run,
        GameContextCoordinate? coordinate)
    {
        return expectation.HasProvenance
               && expectation.Authority.Committed
               && string.Equals(
                   expectation.WorldId,
                   run.WorldId,
                   StringComparison.Ordinal)
               && (expectation.SessionId is null
                   || string.Equals(
                       expectation.SessionId,
                       run.SessionId,
                       StringComparison.Ordinal))
               && AuthorityBelongsToRun(
                   expectation.Authority,
                   run,
                   coordinate);
    }

    private static bool AuthorityBelongsToRun(
        MemoryRecordAuthorityEnvelope authority,
        AgentRun run,
        GameContextCoordinate? coordinate)
    {
        if (!authority.HasProvenance
            || !string.Equals(
                authority.WorldId,
                run.WorldId,
                StringComparison.Ordinal)
            || authority.SessionId is not null
            && !string.Equals(
                authority.SessionId,
                run.SessionId,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (coordinate is null)
        {
            return authority.TimelineId is null
                   && !authority.TimelineEpoch.HasValue
                   && !authority.HasPerspective
                   && !authority.HasGameTimeWindow;
        }

        if (!string.Equals(
                coordinate.WorldId,
                authority.WorldId,
                StringComparison.Ordinal)
            || authority.SessionId is not null
            && !string.Equals(
                coordinate.SessionId ?? run.SessionId,
                authority.SessionId,
                StringComparison.Ordinal)
            || authority.SaveRevision > coordinate.SaveRevision
            || authority.TimelineId is not null
            && !string.Equals(
                authority.TimelineId,
                coordinate.TimelineId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var gameTime = coordinate.GameTime;
        if (authority.TimelineEpoch.HasValue
            && (gameTime is null
                || authority.TimelineEpoch.Value != gameTime.Epoch))
        {
            return false;
        }

        if (authority.HasPerspective
            && (coordinate.Observer is null
                || !string.Equals(
                    authority.ObserverEntityId,
                    coordinate.Observer.EntityId,
                    StringComparison.Ordinal)
                || authority.ObserverIncarnation
                != coordinate.Observer.Incarnation))
        {
            return false;
        }

        return !authority.HasGameTimeWindow
               || gameTime is not null
               && string.Equals(
                   authority.GameTimeClockId,
                   gameTime.ClockId,
                   StringComparison.Ordinal)
               && string.Equals(
                   authority.GameTimeTimelineId,
                   gameTime.TimelineId,
                   StringComparison.Ordinal)
               && authority.GameTimeEpoch == gameTime.Epoch;
    }

    private static MemoryRecallReport FilterRecallReport(
        MemoryRecallReport report,
        MemoryQuery query,
        string? runSessionId)
    {
        var count = report.Results.Count;
        var results = new List<MemorySearchResult>(
            Math.Min(count, query.MaxResults));
        var bytes = 0L;
        for (var index = 0; index < count; index++)
        {
            var result = report.Results[index]
                         ?? throw new InvalidDataException(
                             "A prefetched memory report contains null.");
            if (!MemoryQueryFilter.Matches(result.Record, query))
            {
                continue;
            }
            var recordSessionId = result.Record.Provenance?.SessionId;
            if (recordSessionId is not null
                && !string.Equals(
                    recordSessionId,
                    runSessionId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var itemBytes = Encoding.UTF8.GetByteCount(
                result.Record.Content.GetRawText());
            if (results.Count >= query.MaxResults
                || bytes + itemBytes > query.MaxUtf8Bytes)
            {
                continue;
            }

            results.Add(result);
            bytes += itemBytes;
        }

        var failedCount = report.FailedProviderIds.Count;
        var failures = new string[failedCount];
        for (var index = 0; index < failedCount; index++)
        {
            failures[index] = RuntimeGuard.RequiredUtf8(
                report.FailedProviderIds[index],
                128,
                nameof(report));
        }

        if (!MemoryRankingModes.IsKnown(report.RankingMode))
        {
            throw new InvalidDataException(
                "A prefetched memory report has an invalid ranking mode.");
        }

        var evidenceByMemoryId = report.CandidateEvidence
            .GroupBy(item => item.MemoryId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        var evidence = new MemoryRecallCandidateEvidence[results.Count];
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            if (!evidenceByMemoryId.TryGetValue(
                    result.Record.MemoryId,
                    out var source))
            {
                evidence[index] = new MemoryRecallCandidateEvidence(
                    result.Record.MemoryId,
                    result.Score,
                    Array.Empty<MemoryRecallProviderEvidence>());
                continue;
            }

            var providers = new MemoryRecallProviderEvidence[
                source.Providers.Count];
            for (var providerIndex = 0;
                 providerIndex < providers.Length;
                 providerIndex++)
            {
                var provider = source.Providers[providerIndex];
                providers[providerIndex] = new MemoryRecallProviderEvidence(
                    RuntimeGuard.RequiredUtf8(
                        provider.ProviderId,
                        128,
                        nameof(report)),
                    provider.Rank,
                    provider.RawScore);
            }

            evidence[index] = new MemoryRecallCandidateEvidence(
                result.Record.MemoryId,
                result.Score,
                new ReadOnlyCollection<MemoryRecallProviderEvidence>(
                    providers));
        }

        return new MemoryRecallReport(
            new ReadOnlyCollection<MemorySearchResult>(results),
            new ReadOnlyCollection<string>(failures),
            new ReadOnlyCollection<MemoryRecallCandidateEvidence>(evidence),
            report.RankingMode);
    }

    private JsonElement RecallEvidence(
        RuntimeMemoryRecallPlan plan,
        MemoryRecallReport report,
        bool usedPrefetch,
        int resultCount,
        IReadOnlyList<ContextCandidate> selected)
    {
        const int maxEvidenceCandidates = 32;
        const int maxProviderContributions = 8;
        var evidenceByCandidateId = new Dictionary<
            string,
            MemoryRecallCandidateEvidence>(StringComparer.Ordinal);
        foreach (var item in report.CandidateEvidence)
        {
            evidenceByCandidateId.TryAdd(
                CandidateId(item.MemoryId),
                item);
        }

        var candidateEvidence = selected
            .Take(maxEvidenceCandidates)
            .Select(candidate =>
            {
                if (!evidenceByCandidateId.TryGetValue(
                        candidate.Id,
                        out var evidence))
                {
                    return JsonArrayBuilder.Object(
                        ("candidateId",
                            JsonArrayBuilder.String(candidate.Id)),
                        ("providerCount", JsonArrayBuilder.Number(0)),
                        ("providers", JsonArrayBuilder.Array(
                            Array.Empty<JsonElement>())));
                }

                var providers = evidence.Providers
                    .Take(maxProviderContributions)
                    .Select(provider => JsonArrayBuilder.Object(
                        ("providerId", JsonArrayBuilder.String(
                            provider.ProviderId)),
                        ("rank", JsonArrayBuilder.Number(provider.Rank)),
                        ("rawScore",
                            JsonArrayBuilder.Number(provider.RawScore))));
                return JsonArrayBuilder.Object(
                    ("candidateId",
                        JsonArrayBuilder.String(candidate.Id)),
                    ("finalScore",
                        JsonArrayBuilder.Number(evidence.FinalScore)),
                    ("providerCount",
                        JsonArrayBuilder.Number(evidence.Providers.Count)),
                    ("providers", JsonArrayBuilder.Array(providers)));
            });

        return JsonArrayBuilder.Object(
            ("policyId", JsonArrayBuilder.String(PolicyId)),
            ("version", JsonArrayBuilder.String(PolicyVersion)),
            ("scope", JsonArrayBuilder.String(plan.Query.Scope)),
            ("rankingMode", JsonArrayBuilder.String(report.RankingMode)),
            ("usedPrefetch", JsonArrayBuilder.Boolean(usedPrefetch)),
            ("partial", JsonArrayBuilder.Boolean(report.IsPartial)),
            ("resultCount", JsonArrayBuilder.Number(resultCount)),
            ("selectedCount", JsonArrayBuilder.Number(selected.Count)),
            ("candidateEvidenceTruncated", JsonArrayBuilder.Boolean(
                selected.Count > maxEvidenceCandidates)),
            ("failedProviderIds", JsonArrayBuilder.Array(
                report.FailedProviderIds.Select(JsonArrayBuilder.String))),
            ("selectedIds", JsonArrayBuilder.Array(
                selected.Select(
                    item => JsonArrayBuilder.String(item.Id)))),
            ("candidateEvidence", JsonArrayBuilder.Array(candidateEvidence)));
    }

    private static string ReadPolicyIdentity(
        Func<string> read,
        string parameterName)
    {
        try
        {
            return RuntimeGuard.RequiredUtf8(
                read(),
                128,
                parameterName);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new ArgumentException(
                "The runtime memory policy identity is invalid.",
                parameterName);
        }
    }

    private static string CandidateId(string memoryId)
    {
        return "memory:" + Sha256(memoryId);
    }

    internal static string CommitId(
        string runId,
        long runtimeGeneration,
        string turnId)
    {
        RuntimeGuard.RequiredId(runId, nameof(runId));
        if (runtimeGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeGeneration));
        }

        RuntimeGuard.RequiredId(turnId, nameof(turnId));
        return "memory:"
               + Sha256(
                   runId
                   + "\0"
                   + runtimeGeneration.ToString(
                       System.Globalization.CultureInfo.InvariantCulture)
                   + "\0"
                   + turnId);
    }

    private static string Sha256(string value)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var result = new StringBuilder(hash.Length * 2);
        foreach (var item in hash)
        {
            result.Append(
                item.ToString(
                    "x2",
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }
}

internal sealed class RuntimeMemoryRecallSelection
{
    public RuntimeMemoryRecallSelection(
        IReadOnlyList<ContextCandidate> candidates,
        JsonElement evidence)
    {
        Candidates = candidates;
        Evidence = evidence.Clone();
    }

    public IReadOnlyList<ContextCandidate> Candidates { get; }

    public JsonElement Evidence { get; }

    public static RuntimeMemoryRecallSelection Empty(
        string policyId,
        string policyVersion)
    {
        return new RuntimeMemoryRecallSelection(
            Array.Empty<ContextCandidate>(),
            JsonArrayBuilder.Object(
                ("policyId", JsonArrayBuilder.String(policyId)),
                ("version", JsonArrayBuilder.String(policyVersion)),
                ("planned", JsonArrayBuilder.Boolean(false)),
                ("resultCount", JsonArrayBuilder.Number(0)),
                ("selectedCount", JsonArrayBuilder.Number(0))));
    }
}

internal sealed class PreparedRuntimeMemoryCommit
{
    public const int LegacyMutationContractVersion = 0;

    public PreparedRuntimeMemoryCommit(
        string commitId,
        string turnId,
        string policyId,
        string policyVersion,
        string payloadDigest,
        IReadOnlyList<MemoryMutation> mutations,
        int mutationContractVersion =
            RuntimeMemoryMutationContract.CurrentVersion)
    {
        CommitId = RuntimeGuard.RequiredUtf8(
            commitId,
            256,
            nameof(commitId));
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        PolicyId = RuntimeGuard.RequiredUtf8(
            policyId,
            128,
            nameof(policyId));
        PolicyVersion = RuntimeGuard.RequiredUtf8(
            policyVersion,
            128,
            nameof(policyVersion));
        if (!CanonicalJsonDigest.IsSha256(payloadDigest))
        {
            throw new InvalidDataException(
                "A prepared memory commit has an invalid payload digest.");
        }

        PayloadDigest = payloadDigest;
        if (mutationContractVersion is not LegacyMutationContractVersion
            && mutationContractVersion
            != RuntimeMemoryMutationContract.CurrentVersion)
        {
            throw new InvalidDataException(
                "A prepared memory commit has an unsupported mutation "
                + "contract version.");
        }

        MutationContractVersion = mutationContractVersion;
        var count = mutations.Count;
        var snapshot = new MemoryMutation[count];
        for (var index = 0; index < count; index++)
        {
            snapshot[index] = mutations[index]
                              ?? throw new InvalidDataException(
                                  "A prepared memory mutation is null.");
        }

        Mutations = new ReadOnlyCollection<MemoryMutation>(snapshot);
    }

    public string CommitId { get; }

    public string TurnId { get; }

    public string PolicyId { get; }

    public string PolicyVersion { get; }

    public string PayloadDigest { get; }

    public int MutationContractVersion { get; }

    public IReadOnlyList<MemoryMutation> Mutations { get; }
}

internal static class RuntimeMemoryCommitJournalCodec
{
    // Leave bounded headroom for the enclosing RuntimeEvent so every accepted
    // payload remains recoverable under the default 1 MiB event limit.
    private const int MaxEncodedUtf8Bytes = 896 * 1_024;

    public static JsonElement EncodePrepared(
        PreparedRuntimeMemoryCommit prepared)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("commitId", prepared.CommitId);
            writer.WriteString("turnId", prepared.TurnId);
            writer.WriteString("policyId", prepared.PolicyId);
            writer.WriteString("policyVersion", prepared.PolicyVersion);
            writer.WriteString("payloadDigest", prepared.PayloadDigest);
            writer.WriteNumber(
                "mutationContractVersion",
                prepared.MutationContractVersion);
            writer.WritePropertyName("mutations");
            writer.WriteStartArray();
            foreach (var mutation in prepared.Mutations)
            {
                WriteMutation(writer, mutation);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > MaxEncodedUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                nameof(prepared),
                "memory_commit_journal_bytes_exceeded",
                "The prepared memory commit exceeds the durable event limit.");
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    public static PreparedRuntimeMemoryCommit DecodePrepared(
        JsonElement payload,
        string expectedTurnId,
        string expectedCommitId)
    {
        try
        {
            JsonValueInspector.ValidateAndMeasure(
                payload,
                new JsonValueLimits(maxUtf8Bytes: MaxEncodedUtf8Bytes),
                nameof(payload));
            var hasMutationContractVersion = payload.TryGetProperty(
                "mutationContractVersion",
                out var mutationContractVersionJson);
            RequireObject(payload, hasMutationContractVersion ? 7 : 6);
            var commitId = RequiredString(payload, "commitId", 256);
            var turnId = RequiredString(payload, "turnId", 128);
            var policyId = RequiredString(payload, "policyId", 128);
            var policyVersion = RequiredString(
                payload,
                "policyVersion",
                128);
            var payloadDigest = RequiredString(
                payload,
                "payloadDigest",
                64);
            var mutationContractVersion = hasMutationContractVersion
                && mutationContractVersionJson.TryGetInt32(
                    out var parsedMutationContractVersion)
                    ? parsedMutationContractVersion
                    : hasMutationContractVersion
                        ? throw new InvalidDataException(
                            "A prepared memory commit has an invalid "
                            + "mutation contract version.")
                        : PreparedRuntimeMemoryCommit
                            .LegacyMutationContractVersion;
            if (!string.Equals(
                    turnId,
                    expectedTurnId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    commitId,
                    expectedCommitId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A prepared memory commit has inconsistent identity.");
            }

            var mutationsJson = payload.GetProperty("mutations");
            if (mutationsJson.ValueKind != JsonValueKind.Array
                || mutationsJson.GetArrayLength() > 256)
            {
                throw new InvalidDataException(
                    "A prepared memory commit has invalid mutations.");
            }

            var mutations = new MemoryMutation[
                mutationsJson.GetArrayLength()];
            var index = 0;
            foreach (var item in mutationsJson.EnumerateArray())
            {
                mutations[index++] = ReadMutation(item);
            }

            if (mutations.Length > 0)
            {
                _ = MemoryBatchValidator.Snapshot(
                    mutations,
                    CancellationToken.None);
            }

            var computedDigest = ComputeMutationDigest(mutations);
            if (!CanonicalJsonDigest.IsSha256(payloadDigest)
                || !string.Equals(
                    payloadDigest,
                    computedDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A prepared memory commit payload digest is invalid.");
            }

            return new PreparedRuntimeMemoryCommit(
                commitId,
                turnId,
                policyId,
                policyVersion,
                payloadDigest,
                mutations,
                mutationContractVersion);
        }
        catch (RuntimeMemoryIntegrationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                "A durable memory outbox record is invalid.");
        }
    }

    public static JsonElement EncodeCompleted(
        PreparedRuntimeMemoryCommit prepared)
    {
        return JsonArrayBuilder.Object(
            ("commitId", JsonArrayBuilder.String(prepared.CommitId)),
            ("turnId", JsonArrayBuilder.String(prepared.TurnId)));
    }

    public static string DecodeCompleted(
        JsonElement payload,
        string expectedTurnId,
        string expectedCommitId)
    {
        try
        {
            RequireObject(payload, 2);
            var commitId = RequiredString(payload, "commitId", 256);
            var turnId = RequiredString(payload, "turnId", 128);
            if (!string.Equals(
                    turnId,
                    expectedTurnId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    commitId,
                    expectedCommitId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A completed memory commit has inconsistent identity.");
            }

            return commitId;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                "A durable memory completion record is invalid.");
        }
    }

    public static JsonElement EncodeSettled(
        PreparedRuntimeMemoryCommit prepared)
    {
        return JsonArrayBuilder.Object(
            ("commitId", JsonArrayBuilder.String(prepared.CommitId)),
            ("turnId", JsonArrayBuilder.String(prepared.TurnId)),
            ("policyId", JsonArrayBuilder.String(prepared.PolicyId)),
            ("policyVersion", JsonArrayBuilder.String(prepared.PolicyVersion)),
            ("payloadDigest", JsonArrayBuilder.String(prepared.PayloadDigest)));
    }

    public static PreparedRuntimeMemoryCommit DecodeSettled(
        JsonElement payload,
        string expectedTurnId,
        string expectedCommitId)
    {
        try
        {
            RequireObject(payload, 5);
            var commitId = RequiredString(payload, "commitId", 256);
            var turnId = RequiredString(payload, "turnId", 128);
            var policyId = RequiredString(payload, "policyId", 128);
            var policyVersion = RequiredString(
                payload,
                "policyVersion",
                128);
            var payloadDigest = RequiredString(
                payload,
                "payloadDigest",
                64);
            var empty = Array.Empty<MemoryMutation>();
            if (!string.Equals(
                    turnId,
                    expectedTurnId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    commitId,
                    expectedCommitId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    payloadDigest,
                    ComputeMutationDigest(empty),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A memory settlement has inconsistent identity.");
            }

            return new PreparedRuntimeMemoryCommit(
                commitId,
                turnId,
                policyId,
                policyVersion,
                payloadDigest,
                empty);
        }
        catch (RuntimeMemoryIntegrationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            throw new RuntimeMemoryIntegrationException(
                RuntimeMemoryIntegrationReasonCodes.RecoveryRecordInvalid,
                "A durable memory settlement record is invalid.");
        }
    }

    public static string ComputeMutationDigest(
        IReadOnlyList<MemoryMutation> mutations)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var mutation in mutations)
            {
                WriteMutation(writer, mutation);
            }

            writer.WriteEndArray();
        }

        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(buffer.WrittenSpan.ToArray());
        var result = new StringBuilder(digest.Length * 2);
        foreach (var item in digest)
        {
            result.Append(item.ToString(
                "x2",
                System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static void WriteMutation(
        Utf8JsonWriter writer,
        MemoryMutation mutation)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "kind",
            mutation.Kind == MemoryMutationKind.Upsert
                ? "upsert"
                : "delete");
        writer.WriteString("memoryId", mutation.MemoryId);
        if (mutation.Kind == MemoryMutationKind.Upsert)
        {
            writer.WritePropertyName("record");
            WriteRecord(
                writer,
                mutation.Record
                ?? throw new InvalidDataException(
                    "An upsert memory mutation has no record."));
        }

        if (mutation.ExpectedRecord is not null)
        {
            writer.WritePropertyName("expectedRecord");
            WriteExpectation(writer, mutation.ExpectedRecord);
        }

        writer.WriteEndObject();
    }

    private static MemoryMutation ReadMutation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A memory mutation is not an object.");
        }

        var kind = RequiredString(value, "kind", 16);
        var memoryId = RequiredString(value, "memoryId", 128);
        var expectation = value.TryGetProperty(
            "expectedRecord",
            out var expectationJson)
            ? ReadExpectation(expectationJson)
            : null;
        if (string.Equals(kind, "delete", StringComparison.Ordinal))
        {
            RequireObject(value, expectation is null ? 2 : 3);
            return MemoryMutation.Restore(
                MemoryMutationKind.Delete,
                memoryId,
                record: null,
                expectedRecord: expectation);
        }

        if (!string.Equals(kind, "upsert", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A memory mutation has an unknown kind.");
        }

        RequireObject(value, expectation is null ? 3 : 4);
        var record = ReadRecord(value.GetProperty("record"));
        if (!string.Equals(
                memoryId,
                record.MemoryId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A memory mutation id does not match its record.");
        }

        return MemoryMutation.Restore(
            MemoryMutationKind.Upsert,
            memoryId,
            record,
            expectation);
    }

    private static void WriteExpectation(
        Utf8JsonWriter writer,
        MemoryRecordExpectation expectation)
    {
        writer.WriteStartObject();
        writer.WriteString("memoryId", expectation.MemoryId);
        writer.WriteString("scope", expectation.Scope);
        writer.WriteBoolean("hasProvenance", expectation.HasProvenance);
        var authority = expectation.Authority;
        if (expectation.WorldId is not null)
        {
            writer.WriteString("worldId", expectation.WorldId);
        }

        if (expectation.SessionId is not null)
        {
            writer.WriteString("sessionId", expectation.SessionId);
        }

        if (authority.SaveRevision.HasValue)
        {
            writer.WriteNumber("saveRevision", authority.SaveRevision.Value);
        }

        writer.WriteBoolean("committed", authority.Committed);
        if (authority.TimelineId is not null)
        {
            writer.WriteString("timelineId", authority.TimelineId);
        }

        if (authority.TimelineEpoch.HasValue)
        {
            writer.WriteNumber("timelineEpoch", authority.TimelineEpoch.Value);
        }

        writer.WriteBoolean("hasPerspective", authority.HasPerspective);
        if (authority.ObserverEntityId is not null)
        {
            writer.WriteString(
                "observerEntityId",
                authority.ObserverEntityId);
        }

        if (authority.ObserverIncarnation.HasValue)
        {
            writer.WriteNumber(
                "observerIncarnation",
                authority.ObserverIncarnation.Value);
        }

        if (authority.PerspectiveKind is not null)
        {
            writer.WriteString(
                "perspectiveKind",
                authority.PerspectiveKind);
        }

        writer.WriteBoolean("hasSource", authority.HasSource);
        if (authority.SourceEntityId is not null)
        {
            writer.WriteString("sourceEntityId", authority.SourceEntityId);
        }

        if (authority.SourceIncarnation.HasValue)
        {
            writer.WriteNumber(
                "sourceIncarnation",
                authority.SourceIncarnation.Value);
        }

        writer.WriteBoolean(
            "hasGameTimeWindow",
            authority.HasGameTimeWindow);
        if (authority.GameTimeClockId is not null)
        {
            writer.WriteString(
                "gameTimeClockId",
                authority.GameTimeClockId);
        }

        if (authority.GameTimeTimelineId is not null)
        {
            writer.WriteString(
                "gameTimeTimelineId",
                authority.GameTimeTimelineId);
        }

        if (authority.GameTimeEpoch.HasValue)
        {
            writer.WriteNumber(
                "gameTimeEpoch",
                authority.GameTimeEpoch.Value);
        }

        writer.WriteString("recordDigest", expectation.RecordDigest);
        writer.WriteEndObject();
    }

    private static MemoryRecordExpectation ReadExpectation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() is < 8 or > 22
            || !value.TryGetProperty(
                "hasProvenance",
                out var hasProvenanceJson)
            || hasProvenanceJson.ValueKind is not JsonValueKind.True
                and not JsonValueKind.False
            || !TryRequiredBoolean(value, "committed", out var committed)
            || !TryRequiredBoolean(
                value,
                "hasPerspective",
                out var hasPerspective)
            || !TryRequiredBoolean(value, "hasSource", out var hasSource)
            || !TryRequiredBoolean(
                value,
                "hasGameTimeWindow",
                out var hasGameTimeWindow))
        {
            throw new InvalidDataException(
                "A memory record expectation is malformed.");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is not "memoryId"
                and not "scope"
                and not "hasProvenance"
                and not "worldId"
                and not "sessionId"
                and not "saveRevision"
                and not "committed"
                and not "timelineId"
                and not "timelineEpoch"
                and not "hasPerspective"
                and not "observerEntityId"
                and not "observerIncarnation"
                and not "perspectiveKind"
                and not "hasSource"
                and not "sourceEntityId"
                and not "sourceIncarnation"
                and not "hasGameTimeWindow"
                and not "gameTimeClockId"
                and not "gameTimeTimelineId"
                and not "gameTimeEpoch"
                and not "recordDigest")
            {
                throw new InvalidDataException(
                    "A memory record expectation contains an unknown property.");
            }
        }

        var authority = MemoryRecordAuthorityEnvelope.Restore(
            hasProvenanceJson.GetBoolean(),
            OptionalString(value, "worldId", 128),
            OptionalString(value, "sessionId", 128),
            OptionalInt64(value, "saveRevision"),
            committed,
            OptionalString(value, "timelineId", 128),
            OptionalInt64(value, "timelineEpoch"),
            hasPerspective,
            OptionalString(value, "observerEntityId", 128),
            OptionalInt64(value, "observerIncarnation"),
            OptionalString(value, "perspectiveKind", 128),
            hasSource,
            OptionalString(value, "sourceEntityId", 128),
            OptionalInt64(value, "sourceIncarnation"),
            hasGameTimeWindow,
            OptionalString(value, "gameTimeClockId", 128),
            OptionalString(value, "gameTimeTimelineId", 128),
            OptionalInt64(value, "gameTimeEpoch"));
        return MemoryRecordExpectation.Restore(
            RequiredString(value, "memoryId", 128),
            RequiredString(value, "scope", 256),
            authority,
            RequiredString(value, "recordDigest", 64));
    }

    private static void WriteRecord(Utf8JsonWriter writer, MemoryRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("memoryId", record.MemoryId);
        writer.WriteString("scope", record.Scope);
        writer.WritePropertyName("content");
        record.Content.WriteTo(writer);
        writer.WritePropertyName("tags");
        writer.WriteStartArray();
        foreach (var tag in record.Tags)
        {
            writer.WriteStringValue(tag);
        }

        writer.WriteEndArray();
        writer.WriteNumber("importance", record.Importance);
        writer.WriteString("createdAt", record.CreatedAt);
        writer.WriteString("updatedAt", record.UpdatedAt);
        if (record.ExpiresAt.HasValue)
        {
            writer.WriteString("expiresAt", record.ExpiresAt.Value);
        }

        if (record.Provenance is not null)
        {
            writer.WritePropertyName("provenance");
            WriteProvenance(writer, record.Provenance);
        }

        if (record.GameTimeWindow is not null)
        {
            writer.WritePropertyName("gameTimeWindow");
            writer.WriteStartObject();
            if (record.GameTimeWindow.ValidFrom is not null)
            {
                writer.WritePropertyName("validFrom");
                WriteGameTime(writer, record.GameTimeWindow.ValidFrom);
            }

            if (record.GameTimeWindow.ValidUntil is not null)
            {
                writer.WritePropertyName("validUntil");
                WriteGameTime(writer, record.GameTimeWindow.ValidUntil);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static MemoryRecord ReadRecord(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() is < 7 or > 10)
        {
            throw new InvalidDataException("A memory record is malformed.");
        }

        var memoryId = RequiredString(value, "memoryId", 128);
        var scope = RequiredString(value, "scope", 256);
        var content = value.GetProperty("content");
        var tagsJson = value.GetProperty("tags");
        if (tagsJson.ValueKind != JsonValueKind.Array
            || tagsJson.GetArrayLength() > 64)
        {
            throw new InvalidDataException("Memory tags are malformed.");
        }

        var tags = tagsJson
            .EnumerateArray()
            .Select(
                item => item.ValueKind == JsonValueKind.String
                    ? RuntimeGuard.RequiredUtf8(
                        item.GetString(),
                        128,
                        nameof(value))
                    : throw new InvalidDataException(
                        "A memory tag is not a string."))
            .ToArray();
        if (!value.TryGetProperty("importance", out var importanceJson)
            || !importanceJson.TryGetInt32(out var importance)
            || !TryDate(value, "createdAt", out var createdAt)
            || !TryDate(value, "updatedAt", out var updatedAt))
        {
            throw new InvalidDataException("Memory record metadata is invalid.");
        }

        DateTimeOffset? expiresAt = null;
        if (value.TryGetProperty("expiresAt", out var expiresJson))
        {
            expiresAt = ReadDate(expiresJson);
        }

        MemoryProvenance? provenance = null;
        if (value.TryGetProperty("provenance", out var provenanceJson))
        {
            provenance = ReadProvenance(provenanceJson);
        }

        GameTimeWindow? window = null;
        if (value.TryGetProperty("gameTimeWindow", out var windowJson))
        {
            window = ReadWindow(windowJson);
        }

        return new MemoryRecord(
            memoryId,
            scope,
            content,
            tags,
            importance,
            createdAt,
            updatedAt,
            expiresAt,
            provenance,
            window);
    }

    private static void WriteProvenance(
        Utf8JsonWriter writer,
        MemoryProvenance provenance)
    {
        writer.WriteStartObject();
        writer.WriteString("worldId", provenance.WorldId);
        if (provenance.SessionId is not null)
        {
            writer.WriteString("sessionId", provenance.SessionId);
        }

        writer.WriteNumber("saveRevision", provenance.SaveRevision);
        writer.WriteString("sourceRunId", provenance.SourceRunId);
        writer.WriteString("sourceEventId", provenance.SourceEventId);
        writer.WriteBoolean("committed", provenance.Committed);
        if (provenance.TimelineId is not null)
        {
            writer.WriteString("timelineId", provenance.TimelineId);
        }

        if (provenance.TimelineEpoch.HasValue)
        {
            writer.WriteNumber(
                "timelineEpoch",
                provenance.TimelineEpoch.Value);
        }

        if (provenance.Perspective is not null)
        {
            writer.WritePropertyName("perspective");
            WritePerspective(writer, provenance.Perspective);
        }

        writer.WriteEndObject();
    }

    private static MemoryProvenance ReadProvenance(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() is < 5 or > 9
            || !value.TryGetProperty(
                "saveRevision",
                out var revisionJson)
            || !revisionJson.TryGetInt64(out var revision)
            || !value.TryGetProperty("committed", out var committedJson)
            || committedJson.ValueKind is not JsonValueKind.True
                and not JsonValueKind.False)
        {
            throw new InvalidDataException("Memory provenance is malformed.");
        }

        var sessionId = OptionalString(value, "sessionId", 128);
        var timelineId = OptionalString(value, "timelineId", 128);
        long? timelineEpoch = null;
        if (value.TryGetProperty(
                "timelineEpoch",
                out var timelineEpochJson))
        {
            if (!timelineEpochJson.TryGetInt64(out var parsedEpoch)
                || parsedEpoch < 0)
            {
                throw new InvalidDataException(
                    "Memory provenance timeline epoch is malformed.");
            }

            timelineEpoch = parsedEpoch;
        }

        GameKnowledgePerspective? perspective = null;
        if (value.TryGetProperty("perspective", out var perspectiveJson))
        {
            perspective = ReadPerspective(perspectiveJson);
        }

        return new MemoryProvenance(
            RequiredString(value, "worldId", 128),
            sessionId,
            revision,
            RequiredString(value, "sourceRunId", 128),
            RequiredString(value, "sourceEventId", 128),
            committedJson.GetBoolean(),
            timelineId,
            perspective,
            timelineEpoch);
    }

    private static void WritePerspective(
        Utf8JsonWriter writer,
        GameKnowledgePerspective perspective)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("observer");
        WriteEntity(writer, perspective.Observer);
        writer.WriteString("knowledgeKind", perspective.KnowledgeKind);
        if (perspective.Source is not null)
        {
            writer.WritePropertyName("source");
            WriteEntity(writer, perspective.Source);
        }

        writer.WriteEndObject();
    }

    private static GameKnowledgePerspective ReadPerspective(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() is < 2 or > 3)
        {
            throw new InvalidDataException(
                "A memory perspective is malformed.");
        }

        var source = value.TryGetProperty("source", out var sourceJson)
            ? ReadEntity(sourceJson)
            : null;
        return new GameKnowledgePerspective(
            ReadEntity(value.GetProperty("observer")),
            RequiredString(value, "knowledgeKind", 128),
            source);
    }

    private static void WriteEntity(
        Utf8JsonWriter writer,
        GameEntityIdentity entity)
    {
        writer.WriteStartObject();
        writer.WriteString("entityId", entity.EntityId);
        writer.WriteNumber("incarnation", entity.Incarnation);
        writer.WriteEndObject();
    }

    private static GameEntityIdentity ReadEntity(JsonElement value)
    {
        RequireObject(value, 2);
        if (!value.TryGetProperty("incarnation", out var incarnationJson)
            || !incarnationJson.TryGetInt64(out var incarnation))
        {
            throw new InvalidDataException(
                "A memory entity identity is malformed.");
        }

        return new GameEntityIdentity(
            RequiredString(value, "entityId", 128),
            incarnation);
    }

    private static void WriteGameTime(
        Utf8JsonWriter writer,
        GameTimePoint point)
    {
        writer.WriteStartObject();
        writer.WriteString("clockId", point.ClockId);
        writer.WriteString("timelineId", point.TimelineId);
        writer.WriteNumber("epoch", point.Epoch);
        writer.WriteNumber("tick", point.Tick);
        writer.WriteEndObject();
    }

    private static GameTimePoint ReadGameTime(JsonElement value)
    {
        RequireObject(value, 4);
        if (!value.TryGetProperty("epoch", out var epochJson)
            || !epochJson.TryGetInt64(out var epoch)
            || !value.TryGetProperty("tick", out var tickJson)
            || !tickJson.TryGetInt64(out var tick))
        {
            throw new InvalidDataException("Game time is malformed.");
        }

        return new GameTimePoint(
            RequiredString(value, "clockId", 128),
            RequiredString(value, "timelineId", 128),
            epoch,
            tick);
    }

    private static GameTimeWindow ReadWindow(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() is < 1 or > 2)
        {
            throw new InvalidDataException(
                "A memory game-time window is malformed.");
        }

        var from = value.TryGetProperty("validFrom", out var fromJson)
            ? ReadGameTime(fromJson)
            : null;
        var until = value.TryGetProperty("validUntil", out var untilJson)
            ? ReadGameTime(untilJson)
            : null;
        return new GameTimeWindow(from, until);
    }

    private static void RequireObject(JsonElement value, int propertyCount)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != propertyCount)
        {
            throw new InvalidDataException(
                "A memory journal object has an invalid shape.");
        }
    }

    private static string RequiredString(
        JsonElement value,
        string propertyName,
        int maxUtf8Bytes)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Memory journal property '{propertyName}' is invalid.");
        }

        return RuntimeGuard.RequiredUtf8(
            property.GetString(),
            maxUtf8Bytes,
            propertyName);
    }

    private static string? OptionalString(
        JsonElement value,
        string propertyName,
        int maxUtf8Bytes)
    {
        return value.TryGetProperty(propertyName, out var property)
            ? property.ValueKind == JsonValueKind.String
                ? RuntimeGuard.RequiredUtf8(
                    property.GetString(),
                    maxUtf8Bytes,
                    propertyName)
                : throw new InvalidDataException(
                    $"Memory journal property '{propertyName}' is invalid.")
            : null;
    }

    private static long? OptionalInt64(
        JsonElement value,
        string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (!property.TryGetInt64(out var result))
        {
            throw new InvalidDataException(
                $"Memory journal property '{propertyName}' is invalid.");
        }

        return result;
    }

    private static bool TryRequiredBoolean(
        JsonElement value,
        string propertyName,
        out bool result)
    {
        result = false;
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not JsonValueKind.True
                and not JsonValueKind.False)
        {
            return false;
        }

        result = property.GetBoolean();
        return true;
    }

    private static bool TryDate(
        JsonElement value,
        string propertyName,
        out DateTimeOffset result)
    {
        result = default;
        return value.TryGetProperty(propertyName, out var property)
               && TryReadDate(property, out result);
    }

    private static DateTimeOffset ReadDate(JsonElement value)
    {
        if (!TryReadDate(value, out var result))
        {
            throw new InvalidDataException(
                "A memory journal timestamp is invalid.");
        }

        return result;
    }

    private static bool TryReadDate(
        JsonElement value,
        out DateTimeOffset result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.String
               && value.TryGetDateTimeOffset(out result);
    }
}

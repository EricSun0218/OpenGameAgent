using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.World;

namespace GameAgent.Runtime;

public sealed class WorldAgentRunInput
{
    // The bridge appends one required authoritative world-job candidate, so
    // user-supplied context must leave one durable input slot available.
    private const int MaximumContextItems = 511;
    private const int MaximumActiveSkills = 128;
    private const int MaximumTranscriptMessages = 2_048;
    private const int MaximumTranscriptUtf8Bytes = 4 * 1_048_576;

    public WorldAgentRunInput(
        DateTimeOffset createdAt,
        AgentBudget budget,
        string? sessionId = null,
        IEnumerable<ContextCandidate>? context = null,
        IEnumerable<SkillReference>? activeSkills = null,
        IEnumerable<NormalizedMessage>? initialTranscript = null,
        string? laneId = null,
        string workloadClass = ProviderWorkloadClasses.Interactive,
        long runtimeGeneration = 1)
    {
        if (runtimeGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimeGeneration));
        }

        CreatedAt = createdAt;
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        SessionId = sessionId;
        Context = Copy(
            context,
            MaximumContextItems,
            nameof(context),
            static item => item.Clone());
        ActiveSkills = Copy(
            activeSkills,
            MaximumActiveSkills,
            nameof(activeSkills),
            static item => new SkillReference(
                item.SkillId,
                item.Version));
        InitialTranscript = Copy(
            initialTranscript,
            MaximumTranscriptMessages,
            nameof(initialTranscript),
            static item => NormalizedMessageJournalCodec.Decode(
                NormalizedMessageJournalCodec.Encode(item)));
        LaneId = laneId;
        WorkloadClass = ProviderWorkloadClasses.Normalize(
            workloadClass,
            nameof(workloadClass));
        RuntimeGeneration = runtimeGeneration;
        _ = DurableRunInputJournalCodec.Encode(
            Context,
            ActiveSkills,
            WorkloadClass);
        var transcriptBytes = 0;
        foreach (var message in InitialTranscript)
        {
            transcriptBytes = checked(
                transcriptBytes
                + Encoding.UTF8.GetByteCount(
                    NormalizedMessageJournalCodec
                        .Encode(message)
                        .GetRawText()));
            if (transcriptBytes > MaximumTranscriptUtf8Bytes)
            {
                throw new RuntimeContentLimitException(
                    nameof(initialTranscript),
                    "world_agent_transcript_bytes_exceeded",
                    "World-agent transcript exceeds its aggregate byte "
                    + "limit.");
            }
        }
    }

    public DateTimeOffset CreatedAt { get; }

    public AgentBudget Budget { get; }

    public string? SessionId { get; }

    public IReadOnlyList<ContextCandidate> Context { get; }

    public IReadOnlyList<SkillReference> ActiveSkills { get; }

    public IReadOnlyList<NormalizedMessage> InitialTranscript { get; }

    public string? LaneId { get; }

    public string WorkloadClass { get; }

    public long RuntimeGeneration { get; }

    private static IReadOnlyList<T> Copy<T>(
        IEnumerable<T>? values,
        int maximumItems,
        string parameterName,
        Func<T, T> clone)
        where T : class
    {
        if (values is null)
        {
            return Array.Empty<T>();
        }

        var copy = new List<T>(Math.Min(maximumItems, 256));
        foreach (var value in values)
        {
            if (copy.Count >= maximumItems)
            {
                throw new ArgumentException(
                    "The collection exceeds its item limit.",
                    parameterName);
            }

            copy.Add(
                clone(
                    value
                    ?? throw new ArgumentException(
                        "Collections cannot contain null entries.",
                        parameterName)));
        }

        return new ReadOnlyCollection<T>(copy);
    }
}

public interface IWorldAgentRunInputFactory
{
    ValueTask<WorldAgentRunInput> CreateAsync(
        WorldAgentJob job,
        CancellationToken cancellationToken);
}

public enum WorldAgentJobStatus
{
    Completed = 0,
    Waiting = 1,
    WaitingForInput = 2,
    ReconciliationRequired = 3,
    Skipped = 4,
    Failed = 5,
    Cancelled = 6
}

public sealed class WorldAgentJobResult
{
    internal WorldAgentJobResult(
        string jobId,
        string runId,
        WorldAgentJobStatus status,
        string runState,
        string reasonCode,
        JsonElement? output,
        bool usedFallback,
        bool authoritative)
    {
        JobId = jobId;
        RunId = runId;
        Status = status;
        RunState = runState;
        ReasonCode = reasonCode;
        Output = output?.Clone();
        UsedFallback = usedFallback;
        IsAuthoritativeProposal = authoritative;
    }

    public string JobId { get; }

    public string RunId { get; }

    public WorldAgentJobStatus Status { get; }

    public string RunState { get; }

    public string ReasonCode { get; }

    public JsonElement? Output { get; }

    public bool UsedFallback { get; }

    /// <summary>
    /// True means the schema-valid output may be proposed to the world
    /// transaction. It still is not committed authoritative state.
    /// </summary>
    public bool IsAuthoritativeProposal { get; }
}

/// <summary>
/// Binds bounded world understanding, selection, and narration jobs to the
/// durable agent loop with strict final-output admission and guarded resume.
/// </summary>
public sealed class WorldAgentRuntimeBridge
{
    public const string JobExtensionName = "worldAgentJob";
    internal const string EvolutionBindingExtensionName =
        "worldAgentEvolutionBinding";

    private readonly IDurableAgentRuntime _runtime;
    private readonly IWorldAgentRunInputFactory _inputFactory;
    private readonly ToolArgumentValidator _outputValidator = new();

    public WorldAgentRuntimeBridge(
        IDurableAgentRuntime runtime,
        IWorldAgentRunInputFactory inputFactory)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _inputFactory = inputFactory
                        ?? throw new ArgumentNullException(
                            nameof(inputFactory));
    }

    public async ValueTask<WorldAgentJobResult> ExecuteAsync(
        WorldAgentJob job,
        GameContextCoordinate currentCoordinate,
        CancellationToken cancellationToken = default)
    {
        var request = await PrepareRequestAsync(
                job,
                currentCoordinate,
                cancellationToken)
            .ConfigureAwait(false);
        var outcome = await _runtime
            .RunAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return Interpret(job, outcome);
    }

    internal async ValueTask<DurableRunRequest> PrepareRequestAsync(
        WorldAgentJob job,
        GameContextCoordinate currentCoordinate,
        CancellationToken cancellationToken)
    {
        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        job.EnsureCurrentCoordinate(currentCoordinate);
        var contract = CreateContract(job);
        var input = await _inputFactory
            .CreateAsync(job, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "World agent input factory returned null.");
        cancellationToken.ThrowIfCancellationRequested();
        return CreateRequest(
            job,
            input,
            contract,
            input.RuntimeGeneration,
            evolutionBinding: null,
            executionPolicy: null);
    }

    internal async ValueTask<DurableRunRequest>
        PrepareEvolutionRequestAsync(
            WorldAgentJob job,
            GameContextCoordinate currentCoordinate,
            WorldAgentRuntimeGeneration runtimePolicy,
            CancellationToken cancellationToken)
    {
        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        if (runtimePolicy is null)
        {
            throw new ArgumentNullException(nameof(runtimePolicy));
        }

        job.EnsureCurrentCoordinate(currentCoordinate);
        var contract = CreateContract(job);
        var input = await _inputFactory
            .CreateAsync(job, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "World agent input factory returned null.");
        cancellationToken.ThrowIfCancellationRequested();
        return CreateRequest(
            job,
            input,
            contract,
            runtimePolicy.RuntimeGeneration,
            EvolutionBinding(job, runtimePolicy),
            runtimePolicy);
    }

    public async ValueTask<WorldAgentJobResult> ResumeAsync(
        WorldAgentJob job,
        GameContextCoordinate currentCoordinate,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default)
    {
        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        job.EnsureCurrentCoordinate(currentCoordinate);
        var contract = CreateContract(job);
        var source = continuation ?? new DurableRunContinuation();
        var guardedContinuation = new DurableRunContinuation
        {
            Context = source.Context,
            ActiveSkills = source.ActiveSkills,
            ReplaceActiveSkills = source.ReplaceActiveSkills,
            LaneId = source.LaneId,
            WorkloadClass = source.WorkloadClass,
            RequestCancellation = source.RequestCancellation,
            FinalOutputContract = contract
        };
        var guard = new DurableRunResumeGuard
        {
            ExpectedBatchId = job.BatchId,
            ExpectedAgentId = job.AgentId,
            ExpectedDecisionKey = job.JobId,
            SemanticExtensionName = JobExtensionName,
            ExpectedSemanticExtensionSha256 = job.SemanticDigest
        };
        var outcome = await _runtime.ResumeAsync(
                job.RunId,
                guardedContinuation,
                reconciler,
                cancellationToken,
                guard)
            .ConfigureAwait(false);
        return Interpret(job, outcome);
    }

    private static DurableRunRequest CreateRequest(
        WorldAgentJob job,
        WorldAgentRunInput input,
        FinalOutputContract contract,
        long runtimeGeneration,
        JsonElement? evolutionBinding,
        WorldAgentRuntimeGeneration? executionPolicy)
    {
        var run = new AgentRun
        {
            RunId = job.RunId,
            AgentId = job.AgentId,
            WorldId = job.Coordinate.WorldId,
            SessionId = input.SessionId,
            Trigger = new AgentTrigger
            {
                Type = "manual",
                SourceId = job.OccurrenceId
            },
            DecisionKey = job.JobId,
            BatchId = job.BatchId,
            State = RunStates.Queued,
            Revision = 0,
            RuntimeGeneration = runtimeGeneration,
            Budget = CloneBudget(input.Budget),
            Usage = new AgentUsage(),
            CreatedAt = input.CreatedAt,
            UpdatedAt = input.CreatedAt
        };
        run.Extensions[JobExtensionName] = job.ToEnvelope();
        if (evolutionBinding.HasValue)
        {
            run.Extensions[EvolutionBindingExtensionName] =
                evolutionBinding.Value.Clone();
        }
        if (executionPolicy is not null)
        {
            DurableExecutionPolicyBinding.Attach(
                run,
                new DurableExecutionPolicyIdentity(
                    executionPolicy.ToolCatalogDigest,
                    executionPolicy.SkillCatalogDigest,
                    executionPolicy.ProviderPolicyDigest,
                    executionPolicy.ModelPolicyDigest));
        }

        GameContextEnvelope.Attach(run, job.Coordinate);
        var context = input.Context.ToList();
        context.Add(
            new ContextCandidate(
                "world-job-" + job.SemanticDigest[..32],
                "world_agent_job",
                job.ToEnvelope(),
                priority: int.MaxValue,
                required: true,
                canDefer: false,
                provenance: "world:authoritative"));
        _ = DurableRunInputJournalCodec.Encode(
            context,
            input.ActiveSkills,
            input.WorkloadClass);
        return new DurableRunRequest
        {
            Run = run,
            Context = new ReadOnlyCollection<ContextCandidate>(
                context.ToArray()),
            ActiveSkills = input.ActiveSkills,
            InitialTranscript = input.InitialTranscript,
            LaneId = input.LaneId,
            WorkloadClass = input.WorkloadClass,
            FinalOutputContract = contract
        };
    }

    internal static DurableRunSemanticExpectation EvolutionExpectation(
        WorldAgentJob job,
        WorldAgentRuntimeGeneration runtimePolicy)
    {
        return DurableRunSemanticExpectation.FromJson(
            EvolutionBindingExtensionName,
            EvolutionBinding(job, runtimePolicy));
    }

    private static JsonElement EvolutionBinding(
        WorldAgentJob job,
        WorldAgentRuntimeGeneration runtimePolicy)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contract",
                "game-agent.world-agent-evolution-binding.v1");
            writer.WritePropertyName("job");
            job.ToEnvelope().WriteTo(writer);
            writer.WritePropertyName("runtimePolicy");
            runtimePolicy.ToEnvelope().WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    internal WorldAgentJobResult Interpret(
        WorldAgentJob job,
        DurableRunOutcome outcome)
    {
        if (outcome is null || outcome.Run is null)
        {
            throw new InvalidOperationException(
                "Durable runtime returned an invalid outcome.");
        }

        if (!string.Equals(
                outcome.Run.RunId,
                job.RunId,
                StringComparison.Ordinal)
            || !string.Equals(
                outcome.Run.AgentId,
                job.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                outcome.Run.BatchId,
                job.BatchId,
                StringComparison.Ordinal)
            || !string.Equals(
                outcome.Run.DecisionKey,
                job.JobId,
                StringComparison.Ordinal)
            || !string.Equals(
                outcome.Run.WorldId,
                job.Coordinate.WorldId,
                StringComparison.Ordinal)
            || !ReturnedCoordinateMatches(
                outcome.Run,
                job.Coordinate))
        {
            throw new InvalidOperationException(
                "Durable runtime returned a different run identity.");
        }

        if (outcome.ReconciliationRequired)
        {
            return Result(
                job,
                WorldAgentJobStatus.ReconciliationRequired,
                outcome.Run.State,
                "world_agent_reconciliation_required");
        }

        if (string.Equals(
                outcome.Run.State,
                RunStates.Completed,
                StringComparison.Ordinal))
        {
            if (outcome.FinalOutput.HasValue
                && _outputValidator.Validate(
                        job.OutputSchema,
                        outcome.FinalOutput.Value)
                    .IsValid)
            {
                return Result(
                    job,
                    WorldAgentJobStatus.Completed,
                    outcome.Run.State,
                    "world_agent_completed",
                    outcome.FinalOutput,
                    authoritative: job.IsAuthoritativeOutput);
            }

            return ApplyFailure(
                job,
                outcome.Run.State,
                "world_agent_output_invalid");
        }

        if (string.Equals(
                outcome.Run.State,
                RunStates.Cancelled,
                StringComparison.Ordinal))
        {
            return Result(
                job,
                WorldAgentJobStatus.Cancelled,
                outcome.Run.State,
                "world_agent_cancelled");
        }

        if (!outcome.IsTerminal)
        {
            return Result(
                job,
                WorldAgentJobStatus.Waiting,
                outcome.Run.State,
                "world_agent_waiting");
        }

        return ApplyFailure(
            job,
            outcome.Run.State,
            outcome.ErrorCode ?? "world_agent_runtime_failed");
    }

    private static bool ReturnedCoordinateMatches(
        AgentRun run,
        GameContextCoordinate coordinate)
    {
        if (coordinate.SessionId is not null
            && !string.Equals(
                coordinate.SessionId,
                run.SessionId,
                StringComparison.Ordinal)
            || !run.Extensions.TryGetValue(
                GameContextEnvelope.ExtensionName,
                out var actual))
        {
            return false;
        }

        var expected = new GameContextCoordinate(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.SaveRevision,
            coordinate.Observer,
            coordinate.SceneId,
            coordinate.RegionId,
            coordinate.StateVersion,
            coordinate.GameTime,
            coordinate.Causality,
            run.SessionId);
        return string.Equals(
            CanonicalJsonDigest.ComputeSha256(actual),
            CanonicalJsonDigest.ComputeSha256(
                GameContextEnvelope.ToJson(expected)),
            StringComparison.Ordinal);
    }

    private static WorldAgentJobResult ApplyFailure(
        WorldAgentJob job,
        string runState,
        string reasonCode)
    {
        return job.FailurePolicy switch
        {
            WorldAgentFailurePolicy.PauseForInput => Result(
                job,
                WorldAgentJobStatus.WaitingForInput,
                runState,
                reasonCode),
            WorldAgentFailurePolicy.UseFallback => Result(
                job,
                WorldAgentJobStatus.Completed,
                runState,
                reasonCode,
                job.FallbackOutput,
                usedFallback: true,
                authoritative: job.IsAuthoritativeOutput),
            WorldAgentFailurePolicy.Skip => Result(
                job,
                WorldAgentJobStatus.Skipped,
                runState,
                reasonCode),
            WorldAgentFailurePolicy.Fault => Result(
                job,
                WorldAgentJobStatus.Failed,
                runState,
                reasonCode),
            _ => throw new InvalidOperationException(
                "Unknown world agent failure policy.")
        };
    }

    private static WorldAgentJobResult Result(
        WorldAgentJob job,
        WorldAgentJobStatus status,
        string runState,
        string reasonCode,
        JsonElement? output = null,
        bool usedFallback = false,
        bool authoritative = false)
    {
        return new WorldAgentJobResult(
            job.JobId,
            job.RunId,
            status,
            runState,
            reasonCode,
            output,
            usedFallback,
            authoritative);
    }

    private static FinalOutputContract CreateContract(WorldAgentJob job)
    {
        return new FinalOutputContract(
            job.OutputSchemaId,
            job.OutputSchemaVersion,
            job.OutputSchema);
    }

    private static AgentBudget CloneBudget(AgentBudget source)
    {
        return new AgentBudget
        {
            MaxTurns = source.MaxTurns,
            MaxDurationMs = source.MaxDurationMs,
            MaxTokens = source.MaxTokens,
            MaxCostUsd = source.MaxCostUsd,
            MaxActions = source.MaxActions
        };
    }
}

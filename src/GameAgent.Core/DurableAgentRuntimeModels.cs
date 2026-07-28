using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class DurableAgentRuntimeOptions
{
    public int MaxConcurrentProviderCalls { get; set; } = 4;

    public int MaxTranscriptMessages { get; set; } = 2_048;

    public int MaxPromptUtf8Bytes { get; set; } = 1_048_576;

    public int EstimatedPromptBytesPerToken { get; set; } = 4;

    public string ModelId { get; set; } = "provider-managed";

    public string PromptLayoutVersion { get; set; } = "1";

    public string ContextPolicyVersion { get; set; } = "1";

    public string BudgetPolicyVersion { get; set; } = "1";

    public SkillDisclosureBudget SkillDisclosureBudget { get; set; } = new();

    public ToolDisclosureLimits ToolDisclosureLimits { get; set; } = new();

    public SemanticToolLoopGuardOptions ToolLoopGuard { get; set; } = new();

    internal void Validate()
    {
        if (MaxConcurrentProviderCalls < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentProviderCalls));
        }

        if (MaxTranscriptMessages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTranscriptMessages));
        }

        if (MaxPromptUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPromptUtf8Bytes));
        }

        if (EstimatedPromptBytesPerToken is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EstimatedPromptBytesPerToken));
        }

        RuntimeGuard.RequiredUtf8(ModelId, 256, nameof(ModelId));
        RuntimeGuard.RequiredUtf8(
            PromptLayoutVersion,
            64,
            nameof(PromptLayoutVersion));
        RuntimeGuard.RequiredUtf8(
            ContextPolicyVersion,
            64,
            nameof(ContextPolicyVersion));
        RuntimeGuard.RequiredUtf8(
            BudgetPolicyVersion,
            64,
            nameof(BudgetPolicyVersion));
        _ = SkillDisclosureBudget
            ?? throw new ArgumentNullException(nameof(SkillDisclosureBudget));
        _ = ToolDisclosureLimits
            ?? throw new ArgumentNullException(nameof(ToolDisclosureLimits));
        _ = ToolLoopGuard
            ?? throw new ArgumentNullException(nameof(ToolLoopGuard));
        ToolLoopGuard.Validate();
    }

    internal DurableAgentRuntimeOptions Snapshot()
    {
        Validate();
        return new DurableAgentRuntimeOptions
        {
            MaxConcurrentProviderCalls = MaxConcurrentProviderCalls,
            MaxTranscriptMessages = MaxTranscriptMessages,
            MaxPromptUtf8Bytes = MaxPromptUtf8Bytes,
            EstimatedPromptBytesPerToken = EstimatedPromptBytesPerToken,
            ModelId = ModelId,
            PromptLayoutVersion = PromptLayoutVersion,
            ContextPolicyVersion = ContextPolicyVersion,
            BudgetPolicyVersion = BudgetPolicyVersion,
            SkillDisclosureBudget = new SkillDisclosureBudget(
                SkillDisclosureBudget.MaxCatalogItems,
                SkillDisclosureBudget.MaxCatalogUtf8Bytes,
                SkillDisclosureBudget.MaxActivatedSkills,
                SkillDisclosureBudget.MaxPromptFragments,
                SkillDisclosureBudget.MaxPromptUtf8Bytes,
                SkillDisclosureBudget.MaxReferences),
            ToolDisclosureLimits = new ToolDisclosureLimits(
                ToolDisclosureLimits.MaxActivatedDeferredTools,
                ToolDisclosureLimits.MaxSearchResults,
                ToolDisclosureLimits.MaxControlCallsPerTurn,
                ToolDisclosureLimits.MaxSearchQueryUtf8Bytes),
            ToolLoopGuard = ToolLoopGuard.Snapshot()
        };
    }
}

public sealed class DurableRunRequest
{
    public AgentRun Run { get; set; } = new();

    public IReadOnlyList<ContextCandidate> Context { get; set; } =
        Array.Empty<ContextCandidate>();

    public IReadOnlyList<SkillReference> ActiveSkills { get; set; } =
        Array.Empty<SkillReference>();

    public IReadOnlyList<NormalizedMessage> InitialTranscript { get; set; } =
        Array.Empty<NormalizedMessage>();

    public string? LaneId { get; set; }
}

public sealed class DurableRunContinuation
{
    public IReadOnlyList<ContextCandidate> Context { get; set; } =
        Array.Empty<ContextCandidate>();

    /// <summary>
    /// Gets or sets the active-skill replacement. A non-empty collection is
    /// treated as an explicit replacement. An empty collection inherits the
    /// durable activation unless <see cref="ReplaceActiveSkills"/> is true.
    /// </summary>
    public IReadOnlyList<SkillReference> ActiveSkills { get; set; } =
        Array.Empty<SkillReference>();

    /// <summary>
    /// Gets or sets whether <see cref="ActiveSkills"/> explicitly replaces the
    /// durable activation, including replacing it with an empty collection.
    /// </summary>
    public bool ReplaceActiveSkills { get; set; }

    public string? LaneId { get; set; }
}

public sealed class DurableRunOutcome
{
    public AgentRun Run { get; set; } = new();

    public JsonElement? FinalOutput { get; set; }

    public IReadOnlyList<NormalizedMessage> Transcript { get; set; } =
        Array.Empty<NormalizedMessage>();

    public string? ErrorCode { get; set; }

    public string? ErrorCategory { get; set; }

    public string? SafeErrorMessage { get; set; }

    public bool ReconciliationRequired =>
        string.Equals(Run.State, RunStates.Reconciling, StringComparison.Ordinal)
        || Run.PendingOperationIds.Count > 0;

    public bool IsTerminal => RunStateMachine.IsTerminal(Run.State);
}

public interface IDurableAgentRuntime
{
    RuntimeControlPlane Controls { get; }

    ValueTask<DurableRunOutcome> RunAsync(
        DurableRunRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation = null,
        IGameOperationReconciler? reconciler = null,
        CancellationToken cancellationToken = default);
}

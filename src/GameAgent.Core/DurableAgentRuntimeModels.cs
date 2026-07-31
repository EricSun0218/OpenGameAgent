using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class DurableAgentRuntimeOptions
{
    public int MaxConcurrentProviderCalls { get; set; } = 4;

    public int? MaxConcurrentBackgroundProviderCalls { get; set; }

    public TimeSpan ShutdownDrainTimeout { get; set; } =
        TimeSpan.FromSeconds(10);

    public int MaxTranscriptMessages { get; set; } = 2_048;

    public int MaxPromptUtf8Bytes { get; set; } = 1_048_576;

    public int EstimatedPromptBytesPerToken { get; set; } = 4;

    public string ModelId { get; set; } = "provider-managed";

    public string PromptLayoutVersion { get; set; } = "1";

    public string ContextPolicyVersion { get; set; } = "1";

    public string BudgetPolicyVersion { get; set; } = "1";

    public SkillDisclosureBudget SkillDisclosureBudget { get; set; } = new();

    public SkillRuntimeLimits SkillRuntimeLimits { get; set; } = new();

    public ToolDisclosureLimits ToolDisclosureLimits { get; set; } = new();

    public SemanticToolLoopGuardOptions ToolLoopGuard { get; set; } = new();

    public ConversationContextOptions ConversationContext { get; set; } = new();

    /// <summary>
    /// Optional strict boundary for formal final output. Disabled preserves
    /// direct completion from a provider's final text.
    /// </summary>
    public FinalOutputAdmissionOptions FinalOutputAdmission { get; set; } =
        new();

    /// <summary>
    /// Optional maximum number of non-read-only tool calls admitted from one
    /// provider turn. When a response exceeds the limit, every side-effecting
    /// call in that response is rejected before write-ahead or host dispatch;
    /// valid pure reads may still run. Null preserves the unrestricted
    /// behavior.
    /// </summary>
    public int? MaxSideEffectToolCallsPerTurn { get; set; }

    /// <summary>
    /// Requires every nonterminal durable resume to supply a semantic
    /// extension expectation. Terminal outcome replay remains available
    /// without a semantic guard because it cannot re-enter the agent loop.
    /// </summary>
    public bool RequireSemanticResumeGuard { get; set; }

    /// <summary>
    /// Requires every audience-restricted observation to bind the active
    /// protocol audience ID to the exact observer entity incarnation carried
    /// by the run's game-context coordinate. Public world observations remain
    /// unaffected.
    /// </summary>
    public bool RequireAudienceIncarnationForRestrictedObservations
    {
        get;
        set;
    }

    /// <summary>
    /// Allows a provider-declared <c>DurableNonSecret</c> continuation
    /// envelope to be written to the run journal. The default is false.
    /// Ephemeral continuation state can still be used between turns in the
    /// same process.
    /// </summary>
    public bool AllowProviderDeclaredNonSecretContinuationPersistence
    {
        get;
        set;
    }

    internal void Validate()
    {
        if (MaxConcurrentProviderCalls < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentProviderCalls));
        }

        if (MaxConcurrentBackgroundProviderCalls.HasValue
            && (MaxConcurrentBackgroundProviderCalls.Value < 1
                || MaxConcurrentBackgroundProviderCalls.Value
                > MaxConcurrentProviderCalls))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentBackgroundProviderCalls));
        }

        if (ShutdownDrainTimeout < TimeSpan.FromMilliseconds(1)
            || ShutdownDrainTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ShutdownDrainTimeout));
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

        if (MaxSideEffectToolCallsPerTurn is < 0 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSideEffectToolCallsPerTurn));
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
        _ = SkillRuntimeLimits
            ?? throw new ArgumentNullException(nameof(SkillRuntimeLimits));
        _ = ToolDisclosureLimits
            ?? throw new ArgumentNullException(nameof(ToolDisclosureLimits));
        _ = ToolLoopGuard
            ?? throw new ArgumentNullException(nameof(ToolLoopGuard));
        ToolLoopGuard.Validate();
        _ = ConversationContext
            ?? throw new ArgumentNullException(nameof(ConversationContext));
        ConversationContext.Snapshot();
        _ = FinalOutputAdmission
            ?? throw new ArgumentNullException(nameof(FinalOutputAdmission));
        FinalOutputAdmission.Validate();
    }

    internal DurableAgentRuntimeOptions Snapshot()
    {
        Validate();
        return new DurableAgentRuntimeOptions
        {
            MaxConcurrentProviderCalls = MaxConcurrentProviderCalls,
            MaxConcurrentBackgroundProviderCalls =
                MaxConcurrentBackgroundProviderCalls,
            ShutdownDrainTimeout = ShutdownDrainTimeout,
            MaxTranscriptMessages = MaxTranscriptMessages,
            MaxPromptUtf8Bytes = MaxPromptUtf8Bytes,
            EstimatedPromptBytesPerToken = EstimatedPromptBytesPerToken,
            ModelId = ModelId,
            PromptLayoutVersion = PromptLayoutVersion,
            ContextPolicyVersion = ContextPolicyVersion,
            BudgetPolicyVersion = BudgetPolicyVersion,
            MaxSideEffectToolCallsPerTurn =
                MaxSideEffectToolCallsPerTurn,
            RequireSemanticResumeGuard = RequireSemanticResumeGuard,
            RequireAudienceIncarnationForRestrictedObservations =
                RequireAudienceIncarnationForRestrictedObservations,
            AllowProviderDeclaredNonSecretContinuationPersistence =
                AllowProviderDeclaredNonSecretContinuationPersistence,
            SkillDisclosureBudget = new SkillDisclosureBudget(
                SkillDisclosureBudget.MaxCatalogItems,
                SkillDisclosureBudget.MaxCatalogUtf8Bytes,
                SkillDisclosureBudget.MaxActivatedSkills,
                SkillDisclosureBudget.MaxPromptFragments,
                SkillDisclosureBudget.MaxPromptUtf8Bytes,
                SkillDisclosureBudget.MaxReferences),
            SkillRuntimeLimits = new SkillRuntimeLimits(
                SkillRuntimeLimits.MaxSearchResults,
                SkillRuntimeLimits.MaxControlCallsPerTurn,
                SkillRuntimeLimits.MaxSearchQueryUtf8Bytes,
                SkillRuntimeLimits.MaxResolvedItems,
                SkillRuntimeLimits.MaxResolvedItemUtf8Bytes,
                SkillRuntimeLimits.MaxResolvedUtf8Bytes,
                SkillRuntimeLimits.MaxReferenceDepth,
                SkillRuntimeLimits.MaxJsonDepth,
                SkillRuntimeLimits.MaxJsonNodesPerItem,
                SkillRuntimeLimits.ResolverTimeoutMilliseconds,
                SkillRuntimeLimits.MaxConcurrentResolverCalls,
                SkillRuntimeLimits.MaxSearchTokens,
                SkillRuntimeLimits.MaxSearchComparisons),
            ToolDisclosureLimits = new ToolDisclosureLimits(
                ToolDisclosureLimits.MaxActivatedDeferredTools,
                ToolDisclosureLimits.MaxSearchResults,
                ToolDisclosureLimits.MaxControlCallsPerTurn,
                ToolDisclosureLimits.MaxSearchQueryUtf8Bytes),
            ToolLoopGuard = ToolLoopGuard.Snapshot(),
            ConversationContext = ConversationContext.Snapshot(),
            FinalOutputAdmission = FinalOutputAdmission.Snapshot()
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

    public string WorkloadClass { get; set; } =
        ProviderWorkloadClasses.Interactive;

    /// <summary>
    /// Optional structured final-output schema. Strict final-output admission
    /// must be enabled when a contract is supplied.
    /// </summary>
    public FinalOutputContract? FinalOutputContract { get; set; }
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

    public string? WorkloadClass { get; set; }

    /// <summary>
    /// Gets or sets whether this resume request durably cancels the run.
    /// The runtime persists the cancellation intent while it owns the run and
    /// never enters the agent loop after accepting the request.
    /// </summary>
    public bool RequestCancellation { get; set; }

    /// <summary>
    /// Optional exact contract binding for resume. A supplied contract must
    /// match the contract durably captured when the run started; resume never
    /// replaces a run's output contract.
    /// </summary>
    public FinalOutputContract? FinalOutputContract { get; set; }
}

/// <summary>
/// Describes durable identity metadata that must match before a run can be
/// resumed. Guard evaluation is read-only and happens before run ownership is
/// acquired or any provider, reconciler, or game-host side effect.
/// </summary>
public sealed class DurableRunResumeGuard
{
    public string? ExpectedBatchId { get; set; }

    public string? ExpectedAgentId { get; set; }

    public string? ExpectedDecisionKey { get; set; }

    public string? RequiredInt32ExtensionName { get; set; }

    public int MinimumInt32ExtensionValue { get; set; } = int.MinValue;

    public int MaximumInt32ExtensionValue { get; set; } = int.MaxValue;

    public int? ExpectedInt32ExtensionValue { get; set; }

    /// <summary>
    /// Optional opaque run-extension name whose current value must match
    /// <see cref="ExpectedSemanticExtensionSha256"/> before resume. Set both
    /// semantic properties or neither.
    /// </summary>
    public string? SemanticExtensionName { get; set; }

    /// <summary>
    /// Expected lowercase SHA-256 digest produced by
    /// <see cref="CanonicalJsonDigest.ComputeSha256"/>.
    /// </summary>
    public string? ExpectedSemanticExtensionSha256 { get; set; }
}

/// <summary>
/// Caller-owned semantic coordinate used to fence a durable resume against
/// stale game state. Construct this from the game's current state, not from a
/// recovered run or an old batch manifest.
/// </summary>
public sealed class DurableRunSemanticExpectation
{
    public DurableRunSemanticExpectation(
        string extensionName,
        string expectedSha256)
    {
        ExtensionName = RuntimeGuard.RequiredUtf8(
            extensionName,
            128,
            nameof(extensionName));
        if (!CanonicalJsonDigest.IsSha256(expectedSha256))
        {
            throw new ArgumentException(
                "The expected digest must contain exactly 64 lowercase hexadecimal characters.",
                nameof(expectedSha256));
        }

        ExpectedSha256 = expectedSha256;
    }

    public string ExtensionName { get; }

    public string ExpectedSha256 { get; }

    public static DurableRunSemanticExpectation FromJson(
        string extensionName,
        JsonElement currentValue)
    {
        return new DurableRunSemanticExpectation(
            extensionName,
            CanonicalJsonDigest.ComputeSha256(currentValue));
    }
}

public static class DurableRunResumeGuardReasonCodes
{
    public const string NotSupported = "durable_resume_guard_not_supported";

    public const string RunIdMismatch = "durable_resume_guard_run_id_mismatch";

    public const string BatchIdMismatch =
        "durable_resume_guard_batch_id_mismatch";

    public const string AgentIdMismatch =
        "durable_resume_guard_agent_id_mismatch";

    public const string DecisionKeyMismatch =
        "durable_resume_guard_decision_key_mismatch";

    public const string ExtensionMissing =
        "durable_resume_guard_extension_missing";

    public const string ExtensionNotInt32 =
        "durable_resume_guard_extension_not_int32";

    public const string ExtensionOutOfRange =
        "durable_resume_guard_extension_out_of_range";

    public const string ExtensionValueMismatch =
        "durable_resume_guard_extension_value_mismatch";

    public const string SemanticExtensionMissing =
        "durable_resume_guard_semantic_extension_missing";

    public const string SemanticExtensionDigestMismatch =
        "durable_resume_guard_semantic_extension_digest_mismatch";

    public const string SemanticGuardRequired = "semantic_guard_required";
}

public sealed class DurableRunResumeGuardException : InvalidOperationException
{
    public DurableRunResumeGuardException(string reasonCode)
        : base(CreateMessage(reasonCode))
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }

    private static string CreateMessage(string reasonCode)
    {
        return reasonCode switch
        {
            DurableRunResumeGuardReasonCodes.NotSupported =>
                "The durable runtime does not support guarded resume.",
            DurableRunResumeGuardReasonCodes.RunIdMismatch =>
                "The recovered run id does not match the resume request.",
            DurableRunResumeGuardReasonCodes.BatchIdMismatch =>
                "The recovered run belongs to a different batch.",
            DurableRunResumeGuardReasonCodes.AgentIdMismatch =>
                "The recovered run belongs to a different agent.",
            DurableRunResumeGuardReasonCodes.DecisionKeyMismatch =>
                "The recovered run has a different decision key.",
            DurableRunResumeGuardReasonCodes.ExtensionMissing =>
                "The recovered run is missing required resume metadata.",
            DurableRunResumeGuardReasonCodes.ExtensionNotInt32 =>
                "The required resume metadata is not an Int32 value.",
            DurableRunResumeGuardReasonCodes.ExtensionOutOfRange =>
                "The required resume metadata is outside its allowed range.",
            DurableRunResumeGuardReasonCodes.ExtensionValueMismatch =>
                "The required resume metadata has an unexpected value.",
            DurableRunResumeGuardReasonCodes.SemanticExtensionMissing =>
                "The recovered run is missing required semantic metadata.",
            DurableRunResumeGuardReasonCodes.SemanticExtensionDigestMismatch =>
                "The recovered run semantic metadata has an unexpected digest.",
            DurableRunResumeGuardReasonCodes.SemanticGuardRequired =>
                "A semantic guard is required to resume this nonterminal run.",
            _ => throw new ArgumentException(
                "Unknown durable resume guard reason.",
                nameof(reasonCode))
        };
    }
}

/// <summary>
/// Indicates that a durable run identifier has no recoverable journal state.
/// Callers may use this exact exception to distinguish a genuinely absent run
/// from failures raised after recovery, such as missing tools or skills.
/// </summary>
public sealed class DurableRunNotFoundException : KeyNotFoundException
{
    public DurableRunNotFoundException(string runId)
        : base($"Run '{RequiredRunId(runId)}' does not exist in the durable journal.")
    {
        RunId = runId;
    }

    public string RunId { get; }

    private static string RequiredRunId(string runId)
    {
        return RuntimeGuard.RequiredId(runId, nameof(runId));
    }
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

/// <summary>
/// Optional durable-runtime capability for guarded resume.
/// </summary>
public interface IGuardedDurableAgentRuntime : IDurableAgentRuntime
{
    ValueTask<DurableRunOutcome> ResumeAsync(
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken,
        DurableRunResumeGuard? guard);
}

public static class DurableAgentRuntimeGuardedResumeExtensions
{
    /// <summary>
    /// Resumes through the guarded-resume capability without breaking runtime
    /// implementations that predate that optional capability.
    /// </summary>
    public static ValueTask<DurableRunOutcome> ResumeAsync(
        this IDurableAgentRuntime runtime,
        string runId,
        DurableRunContinuation? continuation,
        IGameOperationReconciler? reconciler,
        CancellationToken cancellationToken,
        DurableRunResumeGuard? guard)
    {
        if (runtime is null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        if (runtime is IGuardedDurableAgentRuntime guarded)
        {
            return guarded.ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken,
                guard);
        }

        if (guard is null)
        {
            return runtime.ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken);
        }

        throw new DurableRunResumeGuardException(
            DurableRunResumeGuardReasonCodes.NotSupported);
    }
}

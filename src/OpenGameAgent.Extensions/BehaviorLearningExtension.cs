using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameLearnedBehaviorStatus
{
    Proposed,
    Active,
    Rejected,
    Superseded,
    Demoted,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameLearnedBehaviorScope
{
    Actor,
    WorldGeneration,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameBehaviorLearningMode
{
    Disabled,
    ReviewRequired,
    ValidatedAutoActivate,
}

public sealed class GameBehaviorWorldBoundary
{
    public GameBehaviorWorldBoundary(string timelineId, string generation, long revision)
    {
        TimelineId = Require(timelineId, 512, nameof(timelineId));
        Generation = Require(generation, 512, nameof(generation));
        Revision = revision >= 0 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
    }

    public string TimelineId { get; }

    public string Generation { get; }

    public long Revision { get; }

    private static string Require(string value, int maximum, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException($"The value must contain 1 to {maximum} printable characters.", name)
            : value;
}

public sealed class GameBehaviorEvidence
{
    public GameBehaviorEvidence(string kind, string reference)
    {
        Kind = Require(kind, 128, nameof(kind));
        Reference = Require(reference, 2_048, nameof(reference));
    }

    public string Kind { get; }

    public string Reference { get; }

    private static string Require(string value, int maximum, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException($"The value must contain 1 to {maximum} printable characters.", name)
            : value;
}

public sealed class GameBehaviorEvaluationSnapshot
{
    internal GameBehaviorEvaluationSnapshot(BehaviorEvaluationDocument document)
    {
        Sequence = document.Sequence;
        Succeeded = document.Succeeded;
        EvidenceReference = document.EvidenceReference;
    }

    public long Sequence { get; }

    public bool Succeeded { get; }

    public string EvidenceReference { get; }
}

public sealed class GameLearnedBehaviorSnapshot
{
    internal GameLearnedBehaviorSnapshot(LearnedBehaviorDocument document)
    {
        BehaviorId = document.BehaviorId;
        Version = document.Version;
        Revision = document.Revision;
        Status = document.Status;
        Scope = document.Scope;
        Title = document.Title;
        Instructions = document.Instructions;
        InputTypes = Array.AsReadOnly(document.InputTypes.ToArray());
        ToolNames = Array.AsReadOnly(document.ToolNames.ToArray());
        Evidence = Array.AsReadOnly(document.Evidence
            .Select(value => new GameBehaviorEvidence(value.Kind, value.Reference))
            .ToArray());
        RecentEvaluations = Array.AsReadOnly(document.RecentEvaluations
            .Select(value => new GameBehaviorEvaluationSnapshot(value))
            .ToArray());
        CreatedInputId = document.CreatedInputId;
        CreatedRunId = document.CreatedRunId;
        TimelineId = document.TimelineId;
        WorldGeneration = document.WorldGeneration;
        WorldRevision = document.WorldRevision;
        SuccessfulEvaluations = document.SuccessfulEvaluations;
        FailedEvaluations = document.FailedEvaluations;
        ConsecutiveFailures = document.ConsecutiveFailures;
        TerminalSequence = document.TerminalSequence;
        LastReason = document.LastReason;
    }

    public string BehaviorId { get; }

    public int Version { get; }

    public long Revision { get; }

    public GameLearnedBehaviorStatus Status { get; }

    public GameLearnedBehaviorScope Scope { get; }

    public string Title { get; }

    public string Instructions { get; }

    public IReadOnlyList<string> InputTypes { get; }

    public IReadOnlyList<string> ToolNames { get; }

    public IReadOnlyList<GameBehaviorEvidence> Evidence { get; }

    public IReadOnlyList<GameBehaviorEvaluationSnapshot> RecentEvaluations { get; }

    public string CreatedInputId { get; }

    public string? CreatedRunId { get; }

    public string TimelineId { get; }

    public string WorldGeneration { get; }

    public long WorldRevision { get; }

    public int SuccessfulEvaluations { get; }

    public int FailedEvaluations { get; }

    public int ConsecutiveFailures { get; }

    public string? LastReason { get; }

    internal long TerminalSequence { get; }
}

public sealed class GameBehaviorLearningValidationRequest
{
    public GameBehaviorLearningValidationRequest(
        GameInput input,
        GameBehaviorWorldBoundary boundary,
        GameLearnedBehaviorSnapshot proposal)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
    }

    public GameInput Input { get; }

    public GameBehaviorWorldBoundary Boundary { get; }

    public GameLearnedBehaviorSnapshot Proposal { get; }
}

public delegate ValueTask<GameBehaviorWorldBoundary> GameBehaviorWorldBoundaryProvider(
    GameInput input,
    CancellationToken cancellationToken);

public delegate ValueTask<bool> GameBehaviorLearningValidator(
    GameBehaviorLearningValidationRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Lets a host opt selected inputs into the model-visible proposal tool. The default is disabled;
/// isolated post-run reviewers should normally use <see cref="BehaviorLearningExtension.ProposeAsync"/>.
/// </summary>
public delegate bool GameBehaviorLearningInRunPolicy(GameInput input);

/// <summary>
/// Typed candidate produced by an isolated host reviewer. It carries instructions and evidence,
/// but no authority to persist or activate itself.
/// </summary>
public sealed class GameBehaviorLearningProposal
{
    public GameBehaviorLearningProposal(
        string behaviorId,
        string title,
        string instructions,
        GameLearnedBehaviorScope scope,
        IEnumerable<GameBehaviorEvidence> evidence,
        IEnumerable<string>? inputTypes = null,
        IEnumerable<string>? toolNames = null)
    {
        BehaviorId = RequireBehaviorId(behaviorId, nameof(behaviorId));
        Title = RequireValue(title, 256, nameof(title));
        Instructions = RequireValue(instructions, 100_000, nameof(instructions));
        if (!Enum.IsDefined(typeof(GameLearnedBehaviorScope), scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        Scope = scope;
        Evidence = CopyEvidence(evidence);
        InputTypes = CopyIds(inputTypes, nameof(inputTypes));
        ToolNames = CopyIds(toolNames, nameof(toolNames));
    }

    public string BehaviorId { get; }

    public string Title { get; }

    public string Instructions { get; }

    public GameLearnedBehaviorScope Scope { get; }

    public IReadOnlyList<GameBehaviorEvidence> Evidence { get; }

    public IReadOnlyList<string> InputTypes { get; }

    public IReadOnlyList<string> ToolNames { get; }

    private static IReadOnlyList<GameBehaviorEvidence> CopyEvidence(IEnumerable<GameBehaviorEvidence> values)
    {
        var copied = (values ?? throw new ArgumentNullException(nameof(values))).ToArray();
        if (copied.Length == 0 || copied.Any(value => value is null))
        {
            throw new ArgumentException("At least one non-null evidence item is required.", nameof(values));
        }

        return Array.AsReadOnly(copied);
    }

    private static IReadOnlyList<string> CopyIds(IEnumerable<string>? values, string name)
    {
        var copied = (values ?? Array.Empty<string>())
            .Select(value => RequireValue(value, 256, name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Array.AsReadOnly(copied);
    }

    private static string RequireValue(string value, int maximum, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException($"The value must contain 1 to {maximum} printable characters.", name)
            : value;

    private static string RequireBehaviorId(string value, string name)
    {
        var validated = RequireValue(value, 128, name);
        if (!IsAsciiLetterOrDigit(validated[0])
            || !validated.All(character => IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                "Behavior IDs must begin with an ASCII letter or digit and may also contain dots, underscores, and hyphens.",
                name);
        }

        return validated;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}

public sealed class BehaviorLearningOptions
{
    public GameBehaviorLearningMode Mode { get; set; } = GameBehaviorLearningMode.ReviewRequired;

    public int MaximumBehaviors { get; set; } = 32;

    public int MaximumVersionsPerBehavior { get; set; } = 8;

    public int MaximumRetainedInactiveVersions { get; set; } = 64;

    public int MaximumInstructionCharacters { get; set; } = 8_192;

    public int MaximumEvidenceItems { get; set; } = 16;

    public int MaximumInputTypes { get; set; } = 16;

    public int MaximumToolNames { get; set; } = 32;

    public int ConsecutiveFailuresBeforeDemotion { get; set; } = 3;

    public int MaximumRetainedEvaluationsPerVersion { get; set; } = 32;

    public bool AllowActorScope { get; set; }

    internal BehaviorLearningOptions CopyAndValidate()
    {
        var copy = (BehaviorLearningOptions)MemberwiseClone();
        if (!Enum.IsDefined(typeof(GameBehaviorLearningMode), copy.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        RequireRange(copy.MaximumBehaviors, 1, 1_000, nameof(MaximumBehaviors));
        RequireRange(copy.MaximumVersionsPerBehavior, 1, 100, nameof(MaximumVersionsPerBehavior));
        RequireRange(copy.MaximumRetainedInactiveVersions, 0, 10_000, nameof(MaximumRetainedInactiveVersions));
        RequireRange(copy.MaximumInstructionCharacters, 64, 100_000, nameof(MaximumInstructionCharacters));
        RequireRange(copy.MaximumEvidenceItems, 1, 64, nameof(MaximumEvidenceItems));
        RequireRange(copy.MaximumInputTypes, 0, 64, nameof(MaximumInputTypes));
        RequireRange(copy.MaximumToolNames, 0, 128, nameof(MaximumToolNames));
        RequireRange(copy.ConsecutiveFailuresBeforeDemotion, 1, 100, nameof(ConsecutiveFailuresBeforeDemotion));
        RequireRange(copy.MaximumRetainedEvaluationsPerVersion, 1, 1_000, nameof(MaximumRetainedEvaluationsPerVersion));
        return copy;
    }

    private static void RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public enum GameBehaviorLearningMutationStatus
{
    Changed,
    SessionNotFound,
    BehaviorNotFound,
    VersionNotFound,
    RevisionConflict,
    InvalidStatus,
    WorldChanged,
    SessionConflict,
    InputNotCommitted,
    AlreadyExists,
    LimitExceeded,
    ScopeDenied,
    Disabled,
}

public sealed class GameBehaviorLearningMutationResult
{
    internal GameBehaviorLearningMutationResult(
        GameBehaviorLearningMutationStatus status,
        long sessionRevision,
        GameLearnedBehaviorSnapshot? behavior)
    {
        Status = status;
        SessionRevision = sessionRevision >= 0
            ? sessionRevision
            : throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        Behavior = behavior;
    }

    public GameBehaviorLearningMutationStatus Status { get; }

    public bool Changed => Status == GameBehaviorLearningMutationStatus.Changed;

    public long SessionRevision { get; }

    public GameLearnedBehaviorSnapshot? Behavior { get; }
}

public sealed class GameBehaviorLearningQueryResult
{
    internal GameBehaviorLearningQueryResult(
        GameSessionKey session,
        long sessionRevision,
        IEnumerable<GameLearnedBehaviorSnapshot> behaviors)
    {
        Session = new GameSessionKey(session.SessionId, session.ActorId);
        SessionRevision = sessionRevision >= 0
            ? sessionRevision
            : throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        var copied = (behaviors ?? throw new ArgumentNullException(nameof(behaviors))).ToArray();
        if (copied.Any(value => value is null))
        {
            throw new ArgumentException("Behavior query results cannot contain null values.", nameof(behaviors));
        }

        Behaviors = Array.AsReadOnly(copied);
    }

    public GameSessionKey Session { get; }

    public long SessionRevision { get; }

    public IReadOnlyList<GameLearnedBehaviorSnapshot> Behaviors { get; }
}

public sealed class GameLearnedBehaviorChanged
{
    public GameLearnedBehaviorChanged(
        GameSessionKey session,
        string? inputId,
        GameLearnedBehaviorSnapshot behavior,
        string reason)
    {
        Session = new GameSessionKey(session.SessionId, session.ActorId);
        InputId = inputId;
        Behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        Reason = reason ?? string.Empty;
    }

    public GameSessionKey Session { get; }

    public string? InputId { get; }

    public GameLearnedBehaviorSnapshot Behavior { get; }

    public string Reason { get; }
}

/// <summary>
/// Turns evidence-backed experience into immutable, versioned behavior candidates. Models may
/// only propose candidates. The trusted host selects whether validated candidates require an
/// explicit CAS activation or activate immediately under the configured policy.
/// </summary>
public sealed class BehaviorLearningExtension : IGameAgentExtension
{
    private const string ExtensionId = "opengameagent.behavior-learning";
    private const string BehaviorPrefix = "behavior/";
    private const string VersionHighWatermarkKey = "behavior-version-high-watermark";
    private const string ProposeSchema = """
        {
          "type":"object",
          "required":["behaviorId","title","instructions","scope","evidence"],
          "properties":{
            "behaviorId":{"type":"string","minLength":1,"maxLength":128,"pattern":"^[A-Za-z0-9][A-Za-z0-9._-]*$"},
            "title":{"type":"string","minLength":1,"maxLength":256},
            "instructions":{"type":"string","minLength":1,"maxLength":8192},
            "scope":{"type":"string","enum":["actor","world_generation"]},
            "inputTypes":{"type":"array","maxItems":16,"items":{"type":"string","minLength":1,"maxLength":256}},
            "toolNames":{"type":"array","maxItems":32,"items":{"type":"string","minLength":1,"maxLength":256}},
            "evidence":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","required":["kind","reference"],"properties":{"kind":{"type":"string","minLength":1,"maxLength":128},"reference":{"type":"string","minLength":1,"maxLength":2048}},"additionalProperties":false}}
          },
          "additionalProperties":false
        }
        """;

    private readonly GameBehaviorWorldBoundaryProvider _boundaryProvider;
    private readonly GameBehaviorLearningValidator _validator;
    private readonly GameBehaviorLearningInRunPolicy? _inRunPolicy;
    private readonly BehaviorLearningOptions _options;

    public BehaviorLearningExtension(
        GameBehaviorWorldBoundaryProvider boundaryProvider,
        GameBehaviorLearningValidator validator,
        BehaviorLearningOptions? options = null,
        GameBehaviorLearningInRunPolicy? inRunPolicy = null)
    {
        _boundaryProvider = boundaryProvider ?? throw new ArgumentNullException(nameof(boundaryProvider));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _options = (options ?? new BehaviorLearningOptions()).CopyAndValidate();
        _inRunPolicy = inRunPolicy;
    }

    public static GameAgentExtensionChannel<GameLearnedBehaviorChanged> BehaviorChanged { get; } =
        new("behavior-learning.changed");

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        ExtensionId,
        "1.0.0",
        "Evidence-backed, host-authorized, versioned behavior learning for game actors.",
        new[] { "behavior-learning", "skills", "audit", "rollback" });

    public void Configure(GameAgentExtensionApi api)
    {
        api.RegisterContextProvider(
            "behavior-learning-guidance",
            (context, _) => new ValueTask<IReadOnlyList<GameContextSlice>>(
                AllowsProposal(context)
                    ? new[]
                    {
                        new GameContextSlice(
                            "behavior-learning-guidance",
                            JsonSerializer.Serialize(
                                "After a complex task has actually succeeded, you may propose a reusable procedure with propose_behavior_learning. Cite durable, host-verifiable evidence. Do not propose one-off facts, preferences, transient environment failures, unresolved failures, guesses about tools, permissions, secrets, or world rules. Only the host-selected validation and activation policy can make a proposal active.")),
                    }
                    : Array.Empty<GameContextSlice>()));
        api.RegisterToolProvider(
            "behavior-learning-tools",
            (context, _) => new ValueTask<IReadOnlyList<AgentTool>>(
                AllowsProposal(context)
                    ? new[] { CreateProposalTool(api, context) }
                    : Array.Empty<AgentTool>()));
        api.RegisterSkillProvider("active-learned-behaviors", SelectSkillsAsync, priority: 350);
    }

    public static async ValueTask<GameBehaviorLearningQueryResult> ReadAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        if (sessionStore is null)
        {
            throw new ArgumentNullException(nameof(sessionStore));
        }

        var key = new GameSessionKey(session.SessionId, session.ActorId);
        var snapshot = await sessionStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new GameBehaviorLearningQueryResult(key, 0, Array.Empty<GameLearnedBehaviorSnapshot>());
        }

        EnsureKey(snapshot, key);
        var behaviors = ReadAll(StoredExtensionStateReader.Read(snapshot, ExtensionId))
            .Where(value => includeInactive || value.Status == GameLearnedBehaviorStatus.Active)
            .OrderBy(value => value.BehaviorId, StringComparer.Ordinal)
            .ThenBy(value => value.Version)
            .Select(value => new GameLearnedBehaviorSnapshot(value));
        return new GameBehaviorLearningQueryResult(key, snapshot.Revision, behaviors);
    }

    public ValueTask<GameBehaviorLearningMutationResult> ActivateAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        string behaviorId,
        int version,
        long expectedSessionRevision,
        GameBehaviorWorldBoundary boundary,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            sessionStore,
            session,
            behaviorId,
            version,
            expectedSessionRevision,
            boundary,
            "activate",
            null,
            null,
            cancellationToken);

    /// <summary>
    /// Persists a typed candidate from an isolated, lower-priority reviewer without adding the
    /// review prompt or response to the NPC transcript. The referenced input must already be
    /// committed. Validation and session CAS still apply; activation follows the configured mode.
    /// </summary>
    public async ValueTask<GameBehaviorLearningMutationResult> ProposeAsync(
        IGameSessionStore sessionStore,
        GameInput sourceInput,
        long expectedSessionRevision,
        GameBehaviorWorldBoundary boundary,
        GameBehaviorLearningProposal proposal,
        string reviewRunId,
        CancellationToken cancellationToken = default)
    {
        if (sessionStore is null)
        {
            throw new ArgumentNullException(nameof(sessionStore));
        }

        if (sourceInput is null)
        {
            throw new ArgumentNullException(nameof(sourceInput));
        }

        if (boundary is null)
        {
            throw new ArgumentNullException(nameof(boundary));
        }

        if (proposal is null)
        {
            throw new ArgumentNullException(nameof(proposal));
        }

        if (expectedSessionRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSessionRevision));
        }

        if (_options.Mode == GameBehaviorLearningMode.Disabled)
        {
            return Result(GameBehaviorLearningMutationStatus.Disabled, 0, null);
        }

        var runId = RequireBounded(reviewRunId, 512, nameof(reviewRunId));
        var key = new GameSessionKey(sourceInput.SessionId, sourceInput.ActorId);
        var snapshot = await sessionStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Result(GameBehaviorLearningMutationStatus.SessionNotFound, 0, null);
        }

        EnsureKey(snapshot, key);
        if (snapshot.Revision != expectedSessionRevision)
        {
            return Result(GameBehaviorLearningMutationStatus.RevisionConflict, snapshot.Revision, null);
        }

        if (snapshot.PendingInputId is not null
            || !snapshot.ProcessedInputIds.Contains(sourceInput.InputId, StringComparer.Ordinal))
        {
            return Result(GameBehaviorLearningMutationStatus.InputNotCommitted, snapshot.Revision, null);
        }

        if (!string.Equals(boundary.TimelineId, sourceInput.Moment.TimelineId, StringComparison.Ordinal))
        {
            return Result(GameBehaviorLearningMutationStatus.WorldChanged, snapshot.Revision, null);
        }

        var state = new Dictionary<string, string>(StoredExtensionStateReader.Read(snapshot, ExtensionId), StringComparer.Ordinal);
        var all = ReadAll(state).ToArray();
        var existing = all.FirstOrDefault(value =>
            string.Equals(value.BehaviorId, proposal.BehaviorId, StringComparison.Ordinal)
            && string.Equals(value.CreatedInputId, sourceInput.InputId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return Result(GameBehaviorLearningMutationStatus.AlreadyExists, snapshot.Revision, existing);
        }

        if (!MakeRoomForProposal(
                state,
                proposal.BehaviorId,
                _options.MaximumBehaviors,
                _options.MaximumVersionsPerBehavior))
        {
            return Result(GameBehaviorLearningMutationStatus.LimitExceeded, snapshot.Revision, null);
        }

        var version = TakeNextVersion(state, ReadAll(state));
        var document = CreateDocument(
            proposal.BehaviorId,
            proposal.Title,
            proposal.Instructions,
            proposal.Scope,
            proposal.InputTypes,
            proposal.ToolNames,
            proposal.Evidence,
            version,
            sourceInput.InputId,
            runId,
            boundary);
        if (document.Scope == GameLearnedBehaviorScope.Actor && !_options.AllowActorScope)
        {
            return Result(GameBehaviorLearningMutationStatus.ScopeDenied, snapshot.Revision, document);
        }

        ValidateDocument(document, _options);
        var accepted = await ValidateProposalAsync(sourceInput, boundary, document, cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            document.Status = GameLearnedBehaviorStatus.Rejected;
            document.LastReason = "host-validation-rejected";
            document.TerminalSequence = NextTerminalSequence(state);
        }
        else if (_options.Mode == GameBehaviorLearningMode.ValidatedAutoActivate)
        {
            ActivateValidated(state, document, "validated-auto-activated");
        }

        state[KeyFor(document)] = JsonSerializer.Serialize(document);
        PruneInactive(state, _options.MaximumRetainedInactiveVersions);
        var next = new GameSessionSnapshot(
            snapshot.Key,
            checked(snapshot.Revision + 1),
            snapshot.Messages,
            snapshot.ProcessedInputIds,
            snapshot.LastMoment,
            ReplaceStoredState(snapshot.ExtensionState, state),
            snapshot.PendingInputId,
            snapshot.UsageLedger);
        var save = await sessionStore.SaveAsync(next, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        if (!save.Saved)
        {
            return Result(GameBehaviorLearningMutationStatus.SessionConflict, save.Current.Revision, null);
        }

        return Result(GameBehaviorLearningMutationStatus.Changed, save.Current.Revision, document);
    }

    public ValueTask<GameBehaviorLearningMutationResult> RejectAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        string behaviorId,
        int version,
        long expectedSessionRevision,
        string reason,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            sessionStore,
            session,
            behaviorId,
            version,
            expectedSessionRevision,
            null,
            "reject",
            RequireBounded(reason, 2_048, nameof(reason)),
            null,
            cancellationToken);

    public ValueTask<GameBehaviorLearningMutationResult> DemoteAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        string behaviorId,
        int version,
        long expectedSessionRevision,
        string reason,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            sessionStore,
            session,
            behaviorId,
            version,
            expectedSessionRevision,
            null,
            "demote",
            RequireBounded(reason, 2_048, nameof(reason)),
            null,
            cancellationToken);

    public ValueTask<GameBehaviorLearningMutationResult> RecordEvaluationAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        string behaviorId,
        int version,
        long expectedSessionRevision,
        bool succeeded,
        string evidenceReference,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            sessionStore,
            session,
            behaviorId,
            version,
            expectedSessionRevision,
            null,
            "evaluate",
            RequireBounded(evidenceReference, 2_048, nameof(evidenceReference)),
            succeeded,
            cancellationToken);

    private bool AllowsProposal(GameAgentExtensionRunContext context)
    {
        if (!context.ExecutionScope.Allows(GameExecutionCapabilities.BehaviorLearning)
            || _options.Mode == GameBehaviorLearningMode.Disabled
            || _inRunPolicy is null)
        {
            return false;
        }

        try
        {
            return _inRunPolicy(context.Input);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private AgentTool CreateProposalTool(GameAgentExtensionApi api, GameAgentExtensionRunContext context) => new(
        new ToolDefinition(
            "propose_behavior_learning",
            "Propose an evidence-backed reusable procedure learned from a completed task. The proposal cannot activate itself or add tools.",
            SchemaForOptions()),
        async (arguments, execution, cancellationToken) =>
        {
            var behaviorId = arguments.GetProperty("behaviorId").GetString() ?? string.Empty;
            var existingForInput = ReadAll(context.State)
                .FirstOrDefault(value => string.Equals(value.BehaviorId, behaviorId, StringComparison.Ordinal)
                    && string.Equals(value.CreatedInputId, context.Input.InputId, StringComparison.Ordinal));
            if (existingForInput is not null)
            {
                return existingForInput.Status == GameLearnedBehaviorStatus.Rejected
                    ? ToolResult.Error("The host rejected this learning proposal.", ToolFailureCategory.RuleRejected)
                    : JsonResult(new GameLearnedBehaviorSnapshot(existingForInput));
            }

            var scopeText = arguments.GetProperty("scope").GetString();
            var scope = string.Equals(scopeText, "actor", StringComparison.Ordinal)
                ? GameLearnedBehaviorScope.Actor
                : GameLearnedBehaviorScope.WorldGeneration;
            if (scope == GameLearnedBehaviorScope.Actor && !_options.AllowActorScope)
            {
                return ToolResult.Error(
                    "Actor-wide learning is disabled; propose a world-generation-scoped behavior.",
                    ToolFailureCategory.RuleRejected);
            }

            GameBehaviorWorldBoundary boundary;
            try
            {
                boundary = await _boundaryProvider(context.Input, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The behavior boundary provider returned null.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return ToolResult.Error(
                    "The trusted world boundary is unavailable.",
                    ToolFailureCategory.RuleRejected);
            }
            if (!string.Equals(boundary.TimelineId, context.Input.Moment.TimelineId, StringComparison.Ordinal))
            {
                return ToolResult.Error("The trusted world boundary does not match this input timeline.", ToolFailureCategory.RuleRejected);
            }

            var document = CreateDocument(
                behaviorId,
                arguments.GetProperty("title").GetString() ?? string.Empty,
                arguments.GetProperty("instructions").GetString() ?? string.Empty,
                scope,
                ReadStrings(arguments, "inputTypes", _options.MaximumInputTypes),
                ReadStrings(arguments, "toolNames", _options.MaximumToolNames),
                ReadEvidence(arguments.GetProperty("evidence"))
                    .Select(value => new GameBehaviorEvidence(value.Kind, value.Reference)),
                1,
                context.Input.InputId,
                execution.RunId,
                boundary);
            ValidateDocument(document, _options);

            if (!MakeRoomForProposal(
                    context.State,
                    behaviorId,
                    _options.MaximumBehaviors,
                    _options.MaximumVersionsPerBehavior))
            {
                return ToolResult.Error(
                    "The behavior-learning bounds are occupied by active or pending candidates.",
                    ToolFailureCategory.RuleRejected);
            }

            document.Version = TakeNextVersion(context.State, ReadAll(context.State));
            var accepted = await ValidateProposalAsync(
                context.Input,
                boundary,
                document,
                cancellationToken).ConfigureAwait(false);

            if (!accepted)
            {
                document.Status = GameLearnedBehaviorStatus.Rejected;
                document.LastReason = "host-validation-rejected";
                document.TerminalSequence = NextTerminalSequence(context.State);
            }
            else if (_options.Mode == GameBehaviorLearningMode.ValidatedAutoActivate)
            {
                ActivateValidated(context.State, document, "validated-auto-activated");
            }

            Write(context.State, document);
            PruneInactive(context.State, _options.MaximumRetainedInactiveVersions);
            var result = new GameLearnedBehaviorSnapshot(document);
            await api.PublishAsync(
                BehaviorChanged,
                new GameLearnedBehaviorChanged(
                    new GameSessionKey(context.Input.SessionId, context.Input.ActorId),
                    context.Input.InputId,
                    result,
                    !accepted
                        ? "validation-rejected"
                        : document.Status == GameLearnedBehaviorStatus.Active
                            ? "validated-auto-activated"
                            : "proposed"),
                cancellationToken).ConfigureAwait(false);
            return accepted
                ? JsonResult(result)
                : ToolResult.Error("The host rejected this learning proposal.", ToolFailureCategory.RuleRejected);
        },
        ToolRisk.IdempotentWrite,
        ToolExecutionMode.Sequential,
        conflictKey: arguments => arguments.TryGetProperty("behaviorId", out var id) ? id.GetString() : null);

    private async ValueTask<IReadOnlyList<GameSkill>> SelectSkillsAsync(
        GameAgentExtensionRunContext context,
        IReadOnlyCollection<string> activeToolNames,
        int maximumSkills,
        CancellationToken cancellationToken)
    {
        if (maximumSkills <= 0)
        {
            return Array.Empty<GameSkill>();
        }

        if (_options.Mode == GameBehaviorLearningMode.Disabled)
        {
            return Array.Empty<GameSkill>();
        }

        var active = ReadAll(context.State)
            .Where(value => value.Status == GameLearnedBehaviorStatus.Active)
            .ToArray();
        if (active.Length == 0)
        {
            return Array.Empty<GameSkill>();
        }

        GameBehaviorWorldBoundary boundary;
        try
        {
            boundary = await _boundaryProvider(context.Input, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The behavior boundary provider returned null.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Array.Empty<GameSkill>();
        }

        var tools = new HashSet<string>(activeToolNames, StringComparer.Ordinal);
        return active
            .Where(value => value.Scope == GameLearnedBehaviorScope.Actor
                || (string.Equals(value.TimelineId, boundary.TimelineId, StringComparison.Ordinal)
                    && string.Equals(value.WorldGeneration, boundary.Generation, StringComparison.Ordinal)))
            .Where(value => value.InputTypes.Count == 0
                || value.InputTypes.Contains(context.Input.Type, StringComparer.Ordinal))
            .Where(value => value.ToolNames.All(tools.Contains))
            .OrderBy(value => value.BehaviorId, StringComparer.Ordinal)
            .Take(maximumSkills)
            .Select(value => new GameSkill(
                $"learned.{value.BehaviorId}.v{value.Version}",
                value.Title,
                "Host-validated behavior learned from prior game evidence.",
                value.Instructions,
                value.InputTypes,
                value.ToolNames,
                priority: 100,
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["behaviorId"] = value.BehaviorId,
                    ["version"] = value.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["provenance"] = "host-validated-learning",
                }))
            .ToArray();
    }

    private async ValueTask<GameBehaviorLearningMutationResult> MutateAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        string behaviorId,
        int version,
        long expectedSessionRevision,
        GameBehaviorWorldBoundary? boundary,
        string action,
        string? reason,
        bool? evaluationSucceeded,
        CancellationToken cancellationToken)
    {
        if (sessionStore is null)
        {
            throw new ArgumentNullException(nameof(sessionStore));
        }

        var key = new GameSessionKey(session.SessionId, session.ActorId);
        RequireId(behaviorId, nameof(behaviorId));
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        if (expectedSessionRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSessionRevision));
        }

        if (action == "activate" && _options.Mode == GameBehaviorLearningMode.Disabled)
        {
            return Result(GameBehaviorLearningMutationStatus.Disabled, 0, null);
        }

        var snapshot = await sessionStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Result(GameBehaviorLearningMutationStatus.SessionNotFound, 0, null);
        }

        EnsureKey(snapshot, key);
        if (snapshot.Revision != expectedSessionRevision)
        {
            return Result(GameBehaviorLearningMutationStatus.RevisionConflict, snapshot.Revision, null);
        }

        var state = new Dictionary<string, string>(StoredExtensionStateReader.Read(snapshot, ExtensionId), StringComparer.Ordinal);
        var matches = ReadAll(state).Where(value => string.Equals(value.BehaviorId, behaviorId, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
        {
            return Result(GameBehaviorLearningMutationStatus.BehaviorNotFound, snapshot.Revision, null);
        }

        var document = matches.SingleOrDefault(value => value.Version == version);
        if (document is null)
        {
            return Result(GameBehaviorLearningMutationStatus.VersionNotFound, snapshot.Revision, null);
        }

        switch (action)
        {
            case "activate":
                if (document.Status is GameLearnedBehaviorStatus.Rejected or GameLearnedBehaviorStatus.Active)
                {
                    return Result(GameBehaviorLearningMutationStatus.InvalidStatus, snapshot.Revision, document);
                }

                var firstActivation = document.Status == GameLearnedBehaviorStatus.Proposed;
                if (boundary is null
                    || !string.Equals(boundary.TimelineId, document.TimelineId, StringComparison.Ordinal)
                    || !string.Equals(boundary.Generation, document.WorldGeneration, StringComparison.Ordinal)
                    || (firstActivation && boundary.Revision != document.WorldRevision)
                    || (!firstActivation && boundary.Revision < document.WorldRevision))
                {
                    return Result(GameBehaviorLearningMutationStatus.WorldChanged, snapshot.Revision, document);
                }

                foreach (var active in matches.Where(value => value.Status == GameLearnedBehaviorStatus.Active && value.Version != version))
                {
                    active.Status = GameLearnedBehaviorStatus.Superseded;
                    active.Revision = checked(active.Revision + 1);
                    active.LastReason = $"superseded-by-v{version}";
                    active.TerminalSequence = NextTerminalSequence(state);
                    state[KeyFor(active)] = JsonSerializer.Serialize(active);
                }

                document.Status = GameLearnedBehaviorStatus.Active;
                document.ConsecutiveFailures = 0;
                document.LastReason = "host-activated";
                break;
            case "reject":
                if (document.Status != GameLearnedBehaviorStatus.Proposed)
                {
                    return Result(GameBehaviorLearningMutationStatus.InvalidStatus, snapshot.Revision, document);
                }

                document.Status = GameLearnedBehaviorStatus.Rejected;
                document.LastReason = reason;
                document.TerminalSequence = NextTerminalSequence(state);
                break;
            case "demote":
                if (document.Status != GameLearnedBehaviorStatus.Active)
                {
                    return Result(GameBehaviorLearningMutationStatus.InvalidStatus, snapshot.Revision, document);
                }

                document.Status = GameLearnedBehaviorStatus.Demoted;
                document.LastReason = reason;
                document.TerminalSequence = NextTerminalSequence(state);
                break;
            case "evaluate":
                if (document.Status != GameLearnedBehaviorStatus.Active || evaluationSucceeded is null)
                {
                    return Result(GameBehaviorLearningMutationStatus.InvalidStatus, snapshot.Revision, document);
                }

                if (evaluationSucceeded.Value)
                {
                    document.SuccessfulEvaluations = checked(document.SuccessfulEvaluations + 1);
                    document.ConsecutiveFailures = 0;
                    document.LastReason = "evaluation-succeeded:" + reason;
                }
                else
                {
                    document.FailedEvaluations = checked(document.FailedEvaluations + 1);
                    document.ConsecutiveFailures = checked(document.ConsecutiveFailures + 1);
                    document.LastReason = "evaluation-failed:" + reason;
                    if (document.ConsecutiveFailures >= _options.ConsecutiveFailuresBeforeDemotion)
                    {
                        document.Status = GameLearnedBehaviorStatus.Demoted;
                        document.TerminalSequence = NextTerminalSequence(state);
                    }
                }

                document.RecentEvaluations.Add(new BehaviorEvaluationDocument
                {
                    Sequence = checked(document.SuccessfulEvaluations + document.FailedEvaluations),
                    Succeeded = evaluationSucceeded.Value,
                    EvidenceReference = reason ?? string.Empty,
                });
                if (document.RecentEvaluations.Count > _options.MaximumRetainedEvaluationsPerVersion)
                {
                    document.RecentEvaluations.RemoveRange(
                        0,
                        document.RecentEvaluations.Count - _options.MaximumRetainedEvaluationsPerVersion);
                }

                break;
            default:
                throw new InvalidOperationException("Unknown behavior-learning mutation.");
        }

        document.Revision = checked(document.Revision + 1);
        state[KeyFor(document)] = JsonSerializer.Serialize(document);
        PruneInactive(state, _options.MaximumRetainedInactiveVersions);
        var storedState = ReplaceStoredState(snapshot.ExtensionState, state);
        var next = new GameSessionSnapshot(
            snapshot.Key,
            checked(snapshot.Revision + 1),
            snapshot.Messages,
            snapshot.ProcessedInputIds,
            snapshot.LastMoment,
            storedState,
            snapshot.PendingInputId,
            snapshot.UsageLedger);
        var save = await sessionStore.SaveAsync(next, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        if (!save.Saved)
        {
            return Result(GameBehaviorLearningMutationStatus.SessionConflict, save.Current.Revision, null);
        }

        return Result(GameBehaviorLearningMutationStatus.Changed, save.Current.Revision, document);
    }

    private static LearnedBehaviorDocument CreateDocument(
        string behaviorId,
        string title,
        string instructions,
        GameLearnedBehaviorScope scope,
        IEnumerable<string> inputTypes,
        IEnumerable<string> toolNames,
        IEnumerable<GameBehaviorEvidence> evidence,
        int version,
        string inputId,
        string runId,
        GameBehaviorWorldBoundary boundary) => new()
        {
            BehaviorId = behaviorId,
            Version = version,
            Revision = 1,
            Status = GameLearnedBehaviorStatus.Proposed,
            Scope = scope,
            Title = title,
            Instructions = instructions,
            InputTypes = inputTypes.ToList(),
            ToolNames = toolNames.ToList(),
            Evidence = evidence.Select(value => new BehaviorEvidenceDocument
            {
                Kind = value.Kind,
                Reference = value.Reference,
            }).ToList(),
            RecentEvaluations = new List<BehaviorEvaluationDocument>(),
            CreatedInputId = inputId,
            CreatedRunId = runId,
            TimelineId = boundary.TimelineId,
            WorldGeneration = boundary.Generation,
            WorldRevision = boundary.Revision,
        };

    private async ValueTask<bool> ValidateProposalAsync(
        GameInput input,
        GameBehaviorWorldBoundary boundary,
        LearnedBehaviorDocument document,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _validator(
                new GameBehaviorLearningValidationRequest(
                    input,
                    boundary,
                    new GameLearnedBehaviorSnapshot(document)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string SchemaForOptions() => ProposeSchema
        .Replace("\"maxLength\":8192", $"\"maxLength\":{_options.MaximumInstructionCharacters}", StringComparison.Ordinal)
        .Replace(
            "\"inputTypes\":{\"type\":\"array\",\"maxItems\":16",
            $"\"inputTypes\":{{\"type\":\"array\",\"maxItems\":{_options.MaximumInputTypes}",
            StringComparison.Ordinal)
        .Replace(
            "\"toolNames\":{\"type\":\"array\",\"maxItems\":32",
            $"\"toolNames\":{{\"type\":\"array\",\"maxItems\":{_options.MaximumToolNames}",
            StringComparison.Ordinal)
        .Replace(
            "\"evidence\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":16",
            $"\"evidence\":{{\"type\":\"array\",\"minItems\":1,\"maxItems\":{_options.MaximumEvidenceItems}",
            StringComparison.Ordinal);

    private static List<string> ReadStrings(JsonElement arguments, string name, int maximum)
    {
        if (!arguments.TryGetProperty(name, out var element))
        {
            return new List<string>();
        }

        var values = element.EnumerateArray()
            .Select(value => RequireBounded(value.GetString() ?? string.Empty, 256, name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (values.Count > maximum)
        {
            throw new InvalidOperationException($"Too many {name} values.");
        }

        return values;
    }

    private List<BehaviorEvidenceDocument> ReadEvidence(JsonElement element)
    {
        var evidence = element.EnumerateArray()
            .Select(value => new BehaviorEvidenceDocument
            {
                Kind = value.GetProperty("kind").GetString() ?? string.Empty,
                Reference = value.GetProperty("reference").GetString() ?? string.Empty,
            })
            .ToList();
        if (evidence.Count < 1 || evidence.Count > _options.MaximumEvidenceItems)
        {
            throw new InvalidOperationException("Learning evidence is outside the configured bounds.");
        }

        return evidence;
    }

    private static IReadOnlyList<LearnedBehaviorDocument> ReadAll(GameAgentExtensionState state) =>
        ReadAll(state.Snapshot());

    private static IReadOnlyList<LearnedBehaviorDocument> ReadAll(IReadOnlyDictionary<string, string> state)
    {
        var values = new List<LearnedBehaviorDocument>();
        foreach (var pair in state.Where(pair => pair.Key.StartsWith(BehaviorPrefix, StringComparison.Ordinal)))
        {
            var document = JsonSerializer.Deserialize<LearnedBehaviorDocument>(pair.Value)
                ?? throw new InvalidOperationException($"Learned behavior '{pair.Key}' is null.");
            ValidateStoredDocument(document, pair.Key);
            values.Add(document);
        }

        var duplicate = values.GroupBy(value => new { value.BehaviorId, value.Version }).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException("Learned behavior state contains duplicate versions.");
        }

        return values;
    }

    private static void ValidateStoredDocument(LearnedBehaviorDocument document, string key)
    {
        RequireId(document.BehaviorId, nameof(document.BehaviorId));
        if (document.Version < 1 || document.Revision < 1 || KeyFor(document) != key)
        {
            throw new InvalidOperationException($"Learned behavior '{key}' has invalid identity or revision data.");
        }

        if (!Enum.IsDefined(typeof(GameLearnedBehaviorStatus), document.Status)
            || !Enum.IsDefined(typeof(GameLearnedBehaviorScope), document.Scope)
            || document.InputTypes is null
            || document.ToolNames is null
            || document.Evidence is null
            || document.RecentEvaluations is null
            || document.RecentEvaluations.Count > 1_000
            || document.InputTypes.Count > 64
            || document.ToolNames.Count > 128
            || document.Evidence.Count is < 1 or > 64
            || !IsBounded(document.Title, 256)
            || !IsBounded(document.Instructions, 100_000)
            || !IsBounded(document.CreatedInputId, 2_048)
            || (document.CreatedRunId is not null && !IsBounded(document.CreatedRunId, 512))
            || !IsBounded(document.TimelineId, 512)
            || !IsBounded(document.WorldGeneration, 512)
            || document.WorldRevision < 0
            || document.SuccessfulEvaluations < 0
            || document.FailedEvaluations < 0
            || document.ConsecutiveFailures < 0
            || (document.LastReason is not null && !IsBounded(document.LastReason, 4_096)))
        {
            throw new InvalidOperationException($"Learned behavior '{key}' is invalid.");
        }

        if (document.InputTypes.Concat(document.ToolNames).Any(value => !IsBounded(value, 256))
            || document.Evidence.Any(value => value is null
                || !IsBounded(value.Kind, 128)
                || !IsBounded(value.Reference, 2_048)))
        {
            throw new InvalidOperationException($"Learned behavior '{key}' has invalid resource or evidence data.");
        }

        if (document.RecentEvaluations.Any(value =>
            value.Sequence < 1
            || string.IsNullOrWhiteSpace(value.EvidenceReference)
            || value.EvidenceReference.Length > 2_048
            || value.EvidenceReference.Any(char.IsControl)))
        {
            throw new InvalidOperationException($"Learned behavior '{key}' has invalid evaluation audit data.");
        }
    }

    private static void ValidateDocument(LearnedBehaviorDocument document, BehaviorLearningOptions options)
    {
        ValidateStoredDocument(document, KeyFor(document));
        RequireBounded(document.Title, 256, nameof(document.Title));
        RequireBounded(document.Instructions, options.MaximumInstructionCharacters, nameof(document.Instructions));
        if (document.InputTypes.Count > options.MaximumInputTypes
            || document.ToolNames.Count > options.MaximumToolNames
            || document.Evidence.Count < 1
            || document.Evidence.Count > options.MaximumEvidenceItems
            || document.RecentEvaluations.Count > options.MaximumRetainedEvaluationsPerVersion)
        {
            throw new InvalidOperationException("The learned behavior exceeds its configured collection bounds.");
        }

        foreach (var value in document.InputTypes.Concat(document.ToolNames))
        {
            RequireBounded(value, 256, "behavior resources");
        }

        foreach (var evidence in document.Evidence)
        {
            _ = new GameBehaviorEvidence(evidence.Kind, evidence.Reference);
        }
    }

    private static void Write(GameAgentExtensionState state, LearnedBehaviorDocument document) =>
        state.Set(KeyFor(document), JsonSerializer.Serialize(document));

    private static string KeyFor(LearnedBehaviorDocument document) =>
        BehaviorPrefix + document.BehaviorId + "/v" + document.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long NextTerminalSequence(GameAgentExtensionState state) => NextTerminalSequence(state.Snapshot());

    private static long NextTerminalSequence(IReadOnlyDictionary<string, string> state) => checked(
        ReadAll(state).Select(value => value.TerminalSequence).DefaultIfEmpty().Max() + 1);

    private static bool IsInactive(GameLearnedBehaviorStatus status) =>
        status is GameLearnedBehaviorStatus.Rejected
            or GameLearnedBehaviorStatus.Superseded
            or GameLearnedBehaviorStatus.Demoted;

    private static void ActivateValidated(
        GameAgentExtensionState state,
        LearnedBehaviorDocument document,
        string reason)
    {
        foreach (var active in ReadAll(state).Where(value =>
                     string.Equals(value.BehaviorId, document.BehaviorId, StringComparison.Ordinal)
                     && value.Status == GameLearnedBehaviorStatus.Active))
        {
            active.Status = GameLearnedBehaviorStatus.Superseded;
            active.Revision = checked(active.Revision + 1);
            active.LastReason = $"superseded-by-v{document.Version}";
            active.TerminalSequence = NextTerminalSequence(state);
            Write(state, active);
        }

        document.Status = GameLearnedBehaviorStatus.Active;
        document.ConsecutiveFailures = 0;
        document.LastReason = reason;
    }

    private static void ActivateValidated(
        IDictionary<string, string> state,
        LearnedBehaviorDocument document,
        string reason)
    {
        foreach (var active in ReadAll(new ReadOnlyDictionary<string, string>(state)).Where(value =>
                     string.Equals(value.BehaviorId, document.BehaviorId, StringComparison.Ordinal)
                     && value.Status == GameLearnedBehaviorStatus.Active))
        {
            active.Status = GameLearnedBehaviorStatus.Superseded;
            active.Revision = checked(active.Revision + 1);
            active.LastReason = $"superseded-by-v{document.Version}";
            active.TerminalSequence = NextTerminalSequence(new ReadOnlyDictionary<string, string>(state));
            state[KeyFor(active)] = JsonSerializer.Serialize(active);
        }

        document.Status = GameLearnedBehaviorStatus.Active;
        document.ConsecutiveFailures = 0;
        document.LastReason = reason;
    }

    private static bool MakeRoomForProposal(
        GameAgentExtensionState state,
        string behaviorId,
        int maximumBehaviors,
        int maximumVersions)
    {
        var values = ReadAll(state).ToArray();
        if (!MakeRoomForProposal(
                values,
                behaviorId,
                maximumBehaviors,
                maximumVersions,
                key => state.Remove(key)))
        {
            return false;
        }

        return true;
    }

    private static bool MakeRoomForProposal(
        IDictionary<string, string> state,
        string behaviorId,
        int maximumBehaviors,
        int maximumVersions)
    {
        var values = ReadAll(new ReadOnlyDictionary<string, string>(state)).ToArray();
        return MakeRoomForProposal(
            values,
            behaviorId,
            maximumBehaviors,
            maximumVersions,
            key => state.Remove(key));
    }

    private static bool MakeRoomForProposal(
        IReadOnlyCollection<LearnedBehaviorDocument> values,
        string behaviorId,
        int maximumBehaviors,
        int maximumVersions,
        Action<string> remove)
    {
        var remaining = values.ToList();
        var target = remaining
            .Where(value => string.Equals(value.BehaviorId, behaviorId, StringComparison.Ordinal))
            .ToList();
        while (target.Count >= maximumVersions)
        {
            var removable = target
                .Where(value => IsInactive(value.Status))
                .OrderBy(value => value.Version)
                .FirstOrDefault();
            if (removable is null)
            {
                return false;
            }

            remove(KeyFor(removable));
            target.Remove(removable);
            remaining.Remove(removable);
        }

        if (target.Count > 0)
        {
            return true;
        }

        while (remaining.Select(value => value.BehaviorId).Distinct(StringComparer.Ordinal).Count() >= maximumBehaviors)
        {
            var removableBehavior = remaining
                .GroupBy(value => value.BehaviorId, StringComparer.Ordinal)
                .Where(group => group.All(value => IsInactive(value.Status)))
                .OrderBy(group => group.Max(value => value.TerminalSequence))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (removableBehavior is null)
            {
                return false;
            }

            foreach (var removable in removableBehavior.ToArray())
            {
                remove(KeyFor(removable));
                remaining.Remove(removable);
            }
        }

        return true;
    }

    private static int TakeNextVersion(
        GameAgentExtensionState state,
        IReadOnlyCollection<LearnedBehaviorDocument> versions)
    {
        var next = ReadNextVersion(state.Get(VersionHighWatermarkKey), versions);
        state.Set(VersionHighWatermarkKey, JsonSerializer.Serialize(next));
        return next;
    }

    private static int TakeNextVersion(
        IDictionary<string, string> state,
        IReadOnlyCollection<LearnedBehaviorDocument> versions)
    {
        state.TryGetValue(VersionHighWatermarkKey, out var stored);
        var next = ReadNextVersion(stored, versions);
        state[VersionHighWatermarkKey] = JsonSerializer.Serialize(next);
        return next;
    }

    private static int ReadNextVersion(string? stored, IReadOnlyCollection<LearnedBehaviorDocument> versions)
    {
        var retainedMaximum = versions.Select(value => value.Version).DefaultIfEmpty().Max();
        var highWatermark = stored is null
            ? retainedMaximum
            : JsonSerializer.Deserialize<int>(stored);
        if (highWatermark < retainedMaximum || highWatermark < 0)
        {
            throw new InvalidOperationException("The learned-behavior version watermark is invalid.");
        }

        return checked(highWatermark + 1);
    }

    private static void PruneInactive(GameAgentExtensionState state, int maximum)
    {
        var remove = ReadAll(state)
            .Where(value => IsInactive(value.Status))
            .OrderByDescending(value => value.TerminalSequence)
            .ThenByDescending(value => value.Version)
            .Skip(maximum)
            .Select(KeyFor)
            .ToArray();
        foreach (var key in remove)
        {
            state.Remove(key);
        }
    }

    private static void PruneInactive(IDictionary<string, string> state, int maximum)
    {
        var remove = ReadAll(new ReadOnlyDictionary<string, string>(state))
            .Where(value => IsInactive(value.Status))
            .OrderByDescending(value => value.TerminalSequence)
            .ThenByDescending(value => value.Version)
            .Skip(maximum)
            .Select(KeyFor)
            .ToArray();
        foreach (var key in remove)
        {
            state.Remove(key);
        }
    }

    private static IReadOnlyDictionary<string, string> ReplaceStoredState(
        IReadOnlyDictionary<string, string> stored,
        IReadOnlyDictionary<string, string> extension)
    {
        var copy = new Dictionary<string, string>(stored, StringComparer.Ordinal);
        var prefix = Uri.EscapeDataString(ExtensionId) + ":";
        foreach (var key in copy.Keys.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            copy.Remove(key);
        }

        foreach (var pair in extension)
        {
            copy.Add(prefix + Uri.EscapeDataString(pair.Key), pair.Value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static GameBehaviorLearningMutationResult Result(
        GameBehaviorLearningMutationStatus status,
        long revision,
        LearnedBehaviorDocument? document) =>
        new(status, revision, document is null ? null : new GameLearnedBehaviorSnapshot(document));

    private static ToolResult JsonResult(object value) =>
        new(new AgentContent[] { new JsonContent(JsonSerializer.Serialize(value)) });

    private static void EnsureKey(GameSessionSnapshot snapshot, GameSessionKey key)
    {
        if (snapshot.Key != key)
        {
            throw new InvalidOperationException("The session store returned a different session key.");
        }
    }

    private static string RequireId(string value, string name)
    {
        var validated = RequireBounded(value, 128, name);
        if (!IsAsciiLetterOrDigit(validated[0])
            || !validated.All(character => IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                "Behavior IDs must begin with an ASCII letter or digit and may also contain dots, underscores, and hyphens.",
                name);
        }

        return validated;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsBounded(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum
        && !value.Any(char.IsControl);

    private static string RequireBounded(string value, int maximum, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException($"The value must contain 1 to {maximum} printable characters.", name)
            : value;
}

internal sealed class LearnedBehaviorDocument
{
    public string BehaviorId { get; set; } = string.Empty;

    public int Version { get; set; }

    public long Revision { get; set; }

    public GameLearnedBehaviorStatus Status { get; set; }

    public GameLearnedBehaviorScope Scope { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Instructions { get; set; } = string.Empty;

    public List<string> InputTypes { get; set; } = new();

    public List<string> ToolNames { get; set; } = new();

    public List<BehaviorEvidenceDocument> Evidence { get; set; } = new();

    public List<BehaviorEvaluationDocument> RecentEvaluations { get; set; } = new();

    public string CreatedInputId { get; set; } = string.Empty;

    public string? CreatedRunId { get; set; }

    public string TimelineId { get; set; } = string.Empty;

    public string WorldGeneration { get; set; } = string.Empty;

    public long WorldRevision { get; set; }

    public int SuccessfulEvaluations { get; set; }

    public int FailedEvaluations { get; set; }

    public int ConsecutiveFailures { get; set; }

    public long TerminalSequence { get; set; }

    public string? LastReason { get; set; }
}

internal sealed class BehaviorEvidenceDocument
{
    public string Kind { get; set; } = string.Empty;

    public string Reference { get; set; } = string.Empty;
}

internal sealed class BehaviorEvaluationDocument
{
    public long Sequence { get; set; }

    public bool Succeeded { get; set; }

    public string EvidenceReference { get; set; } = string.Empty;
}

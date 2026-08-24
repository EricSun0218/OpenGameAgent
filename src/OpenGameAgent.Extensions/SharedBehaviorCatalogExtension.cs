using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Extensions;

public delegate ValueTask<IReadOnlyList<GameSharedBehaviorAudience>> GameSharedBehaviorAudienceProvider(
    GameInput input,
    CancellationToken cancellationToken);

public sealed class GameSharedBehaviorPublicationValidationRequest
{
    public GameSharedBehaviorPublicationValidationRequest(
        GameSessionKey sourceSession,
        GameLearnedBehaviorSnapshot source,
        GameSharedBehaviorAudience audience,
        GameBehaviorWorldBoundary boundary,
        string publicationId,
        string behaviorFamilyId,
        int familyVersion)
    {
        SourceSession = new GameSessionKey(sourceSession.SessionId, sourceSession.ActorId);
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Audience = audience ?? throw new ArgumentNullException(nameof(audience));
        Boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        PublicationId = GameSharedBehaviorAudience.Require(publicationId, 256, nameof(publicationId));
        BehaviorFamilyId = GameSharedBehaviorPublication.RequireStableId(
            behaviorFamilyId,
            nameof(behaviorFamilyId));
        FamilyVersion = familyVersion >= 1
            ? familyVersion
            : throw new ArgumentOutOfRangeException(nameof(familyVersion));
    }

    public GameSessionKey SourceSession { get; }

    public GameLearnedBehaviorSnapshot Source { get; }

    public GameSharedBehaviorAudience Audience { get; }

    public GameBehaviorWorldBoundary Boundary { get; }

    public string PublicationId { get; }

    public string BehaviorFamilyId { get; }

    public int FamilyVersion { get; }
}

public delegate ValueTask<bool> GameSharedBehaviorPublicationValidator(
    GameSharedBehaviorPublicationValidationRequest request,
    CancellationToken cancellationToken);

public sealed class GameSharedBehaviorAdoptionValidationRequest
{
    public GameSharedBehaviorAdoptionValidationRequest(
        GameInput input,
        GameBehaviorWorldBoundary boundary,
        GameSharedBehaviorPublication publication)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        Publication = publication ?? throw new ArgumentNullException(nameof(publication));
    }

    public GameInput Input { get; }

    public GameBehaviorWorldBoundary Boundary { get; }

    public GameSharedBehaviorPublication Publication { get; }
}

public delegate ValueTask<bool> GameSharedBehaviorAdoptionValidator(
    GameSharedBehaviorAdoptionValidationRequest request,
    CancellationToken cancellationToken);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameSharedBehaviorAdoptionStatus
{
    Active,
    Suspended,
    Withdrawn,
    Superseded,
}

public sealed class GameSharedBehaviorAdoptionSnapshot
{
    internal GameSharedBehaviorAdoptionSnapshot(SharedBehaviorAdoptionDocument document)
    {
        PublicationId = document.PublicationId;
        ContentHash = document.ContentHash;
        BehaviorFamilyId = document.BehaviorFamilyId;
        FamilyVersion = document.FamilyVersion;
        SourceBehaviorId = document.SourceBehaviorId;
        SourceBehaviorVersion = document.SourceBehaviorVersion;
        Revision = document.Revision;
        Status = document.Status;
        AdoptedInputId = document.AdoptedInputId;
        TimelineId = document.TimelineId;
        WorldGeneration = document.WorldGeneration;
        WorldRevision = document.WorldRevision;
        SuccessfulEvaluations = document.SuccessfulEvaluations;
        FailedEvaluations = document.FailedEvaluations;
        ConsecutiveFailures = document.ConsecutiveFailures;
        TerminalSequence = document.TerminalSequence;
        LastReason = document.LastReason;
    }

    public string PublicationId { get; }

    public string ContentHash { get; }

    public string BehaviorFamilyId { get; }

    public int FamilyVersion { get; }

    public string SourceBehaviorId { get; }

    public int SourceBehaviorVersion { get; }

    public long Revision { get; }

    public GameSharedBehaviorAdoptionStatus Status { get; }

    public string AdoptedInputId { get; }

    public string TimelineId { get; }

    public string WorldGeneration { get; }

    public long WorldRevision { get; }

    public int SuccessfulEvaluations { get; }

    public int FailedEvaluations { get; }

    public int ConsecutiveFailures { get; }

    public long? TerminalSequence { get; }

    public string? LastReason { get; }
}

public sealed class GameSharedBehaviorAdoptionQueryResult
{
    internal GameSharedBehaviorAdoptionQueryResult(
        GameSessionKey session,
        long sessionRevision,
        IEnumerable<GameSharedBehaviorAdoptionSnapshot> adoptions)
    {
        Session = new GameSessionKey(session.SessionId, session.ActorId);
        SessionRevision = sessionRevision >= 0 ? sessionRevision : throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        Adoptions = Array.AsReadOnly((adoptions ?? throw new ArgumentNullException(nameof(adoptions))).ToArray());
    }

    public GameSessionKey Session { get; }

    public long SessionRevision { get; }

    public IReadOnlyList<GameSharedBehaviorAdoptionSnapshot> Adoptions { get; }
}

public enum GameSharedBehaviorMutationStatus
{
    Changed,
    AlreadyExists,
    SessionNotFound,
    PublicationNotFound,
    BehaviorNotFound,
    VersionNotFound,
    RevisionConflict,
    SessionConflict,
    StoreConflict,
    InvalidStatus,
    WorldChanged,
    AudienceDenied,
    ValidationRejected,
    LimitExceeded,
}

public sealed class GameSharedBehaviorMutationResult
{
    internal GameSharedBehaviorMutationResult(
        GameSharedBehaviorMutationStatus status,
        long? sessionRevision,
        long? publicationRevision,
        GameSharedBehaviorPublication? publication = null,
        GameSharedBehaviorAdoptionSnapshot? adoption = null)
    {
        Status = status;
        SessionRevision = sessionRevision is null or >= 0
            ? sessionRevision
            : throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        PublicationRevision = publicationRevision is null or >= 0
            ? publicationRevision
            : throw new ArgumentOutOfRangeException(nameof(publicationRevision));
        Publication = publication;
        Adoption = adoption;
    }

    public GameSharedBehaviorMutationStatus Status { get; }

    public bool Changed => Status == GameSharedBehaviorMutationStatus.Changed;

    public long? SessionRevision { get; }

    public long? PublicationRevision { get; }

    public GameSharedBehaviorPublication? Publication { get; }

    public GameSharedBehaviorAdoptionSnapshot? Adoption { get; }
}

public sealed class SharedBehaviorCatalogOptions
{
    public int MaximumDiscoverableBehaviors { get; set; } = 64;

    public int MaximumCatalogRecordsScannedPerDiscovery { get; set; } = 10_000;

    public int MaximumAdoptionsPerActor { get; set; } = 32;

    public int ConsecutiveFailuresBeforeSuspension { get; set; } = 3;

    public int MaximumRetainedInactiveAdoptions { get; set; } = 64;

    internal SharedBehaviorCatalogOptions CopyAndValidate()
    {
        var copy = (SharedBehaviorCatalogOptions)MemberwiseClone();
        Require(copy.MaximumDiscoverableBehaviors, 1, 1_000, nameof(MaximumDiscoverableBehaviors));
        Require(
            copy.MaximumCatalogRecordsScannedPerDiscovery,
            copy.MaximumDiscoverableBehaviors,
            1_000_000,
            nameof(MaximumCatalogRecordsScannedPerDiscovery));
        Require(copy.MaximumAdoptionsPerActor, 1, 256, nameof(MaximumAdoptionsPerActor));
        Require(copy.ConsecutiveFailuresBeforeSuspension, 1, 100, nameof(ConsecutiveFailuresBeforeSuspension));
        Require(copy.MaximumRetainedInactiveAdoptions, 0, 10_000, nameof(MaximumRetainedInactiveAdoptions));
        return copy;
    }

    private static void Require(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

/// <summary>
/// Optional shared behavior catalog. A host publishes validated immutable procedures, and each
/// actor explicitly adopts an eligible publication. Publication is discovery, never forced activation.
/// </summary>
public sealed class SharedBehaviorCatalogExtension : IGameAgentExtension
{
    private enum AdoptionMutationKind
    {
        Withdraw,
        EvaluationSucceeded,
        EvaluationFailed,
    }

    private const string ExtensionId = "opengameagent.shared-behavior-catalog";
    private const string AdoptionPrefix = "adoption/";
    private const string TerminalSequenceKey = "terminal-sequence";
    private readonly IGameSharedBehaviorStore _store;
    private readonly GameBehaviorWorldBoundaryProvider _boundaryProvider;
    private readonly GameSharedBehaviorAudienceProvider _audienceProvider;
    private readonly GameSharedBehaviorPublicationValidator _publicationValidator;
    private readonly GameSharedBehaviorAdoptionValidator _adoptionValidator;
    private readonly SharedBehaviorCatalogOptions _options;

    public SharedBehaviorCatalogExtension(
        IGameSharedBehaviorStore store,
        GameBehaviorWorldBoundaryProvider boundaryProvider,
        GameSharedBehaviorAudienceProvider audienceProvider,
        GameSharedBehaviorPublicationValidator publicationValidator,
        GameSharedBehaviorAdoptionValidator adoptionValidator,
        SharedBehaviorCatalogOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _boundaryProvider = boundaryProvider ?? throw new ArgumentNullException(nameof(boundaryProvider));
        _audienceProvider = audienceProvider ?? throw new ArgumentNullException(nameof(audienceProvider));
        _publicationValidator = publicationValidator ?? throw new ArgumentNullException(nameof(publicationValidator));
        _adoptionValidator = adoptionValidator ?? throw new ArgumentNullException(nameof(adoptionValidator));
        _options = (options ?? new SharedBehaviorCatalogOptions()).CopyAndValidate();
    }

    public GameAgentExtensionDescriptor Descriptor { get; } = new(
        ExtensionId,
        "1.0.0",
        "Host-published shared behavior discovery with explicit per-actor adoption and isolated evaluation.",
        new[] { "behavior-catalog", "behavior-adoption", "skills", "rollback" });

    public void Configure(GameAgentExtensionApi api) =>
        api.RegisterSkillProvider("adopted-shared-behaviors", SelectSkillsAsync, priority: 340);

    public async ValueTask<GameSharedBehaviorMutationResult> PublishAsync(
        IGameSessionStore sessionStore,
        GameSessionKey sourceSession,
        string behaviorId,
        int behaviorVersion,
        string behaviorFamilyId,
        int familyVersion,
        long expectedSessionRevision,
        string publicationId,
        GameSharedBehaviorAudience audience,
        GameBehaviorWorldBoundary boundary,
        string auditReference,
        CancellationToken cancellationToken = default)
    {
        if (sessionStore is null)
        {
            throw new ArgumentNullException(nameof(sessionStore));
        }

        var source = await BehaviorLearningExtension.ReadAsync(
            sessionStore,
            sourceSession,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (source.SessionRevision != expectedSessionRevision)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.RevisionConflict, source.SessionRevision);
        }

        var matches = source.Behaviors
            .Where(value => string.Equals(value.BehaviorId, behaviorId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.BehaviorNotFound, source.SessionRevision);
        }

        var behavior = matches.SingleOrDefault(value => value.Version == behaviorVersion);
        if (behavior is null)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.VersionNotFound, source.SessionRevision);
        }

        if (behavior.Status != GameLearnedBehaviorStatus.Active)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.InvalidStatus, source.SessionRevision);
        }

        if (!SourceBoundaryMatches(behavior, boundary))
        {
            return SessionResult(GameSharedBehaviorMutationStatus.WorldChanged, source.SessionRevision);
        }

        if (!await ValidatePublicationAsync(
                sourceSession,
                behavior,
                audience,
                boundary,
                publicationId,
                behaviorFamilyId,
                familyVersion,
                cancellationToken).ConfigureAwait(false))
        {
            return SessionResult(GameSharedBehaviorMutationStatus.ValidationRejected, source.SessionRevision);
        }

        var publication = new GameSharedBehaviorPublication(
            publicationId,
            behaviorFamilyId,
            familyVersion,
            1,
            GameSharedBehaviorPublicationStatus.Published,
            audience,
            GameSharedBehaviorDefinition.From(behavior),
            sourceSession,
            boundary.TimelineId,
            boundary.Generation,
            boundary.Revision,
            auditReference);
        var existing = await _store.LoadAsync(publication.PublicationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return GameSharedBehaviorStoreContract.HasSameImmutableIdentity(existing, publication)
                ? PublishResult(
                    GameSharedBehaviorMutationStatus.AlreadyExists,
                    source.SessionRevision,
                    existing.Revision,
                    existing)
                : PublishResult(
                    GameSharedBehaviorMutationStatus.StoreConflict,
                    source.SessionRevision,
                    existing.Revision,
                    existing);
        }

        var saved = await _store.SaveAsync(publication, 0, cancellationToken).ConfigureAwait(false);
        return saved.Saved
            ? PublishResult(
                GameSharedBehaviorMutationStatus.Changed,
                source.SessionRevision,
                publication.Revision,
                publication)
            : PublishResult(
                GameSharedBehaviorMutationStatus.StoreConflict,
                source.SessionRevision,
                saved.Current?.Revision ?? 0,
                saved.Current);
    }

    public async ValueTask<GameSharedBehaviorMutationResult> RevokeAsync(
        string publicationId,
        long expectedRevision,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var current = await _store.LoadAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return PublicationResult(GameSharedBehaviorMutationStatus.PublicationNotFound, 0);
        }

        if (current.Revision != expectedRevision)
        {
            return PublicationResult(GameSharedBehaviorMutationStatus.RevisionConflict, current.Revision, current);
        }

        if (current.Status != GameSharedBehaviorPublicationStatus.Published)
        {
            return PublicationResult(GameSharedBehaviorMutationStatus.InvalidStatus, current.Revision, current);
        }

        var revoked = new GameSharedBehaviorPublication(
            current.PublicationId,
            current.BehaviorFamilyId,
            current.FamilyVersion,
            checked(current.Revision + 1),
            GameSharedBehaviorPublicationStatus.Revoked,
            current.Audience,
            current.Behavior,
            current.SourceSession,
            current.TimelineId,
            current.WorldGeneration,
            current.WorldRevision,
            current.AuditReference,
            GameSharedBehaviorAudience.Require(reason, 2_048, nameof(reason)));
        var saved = await _store.SaveAsync(revoked, current.Revision, cancellationToken).ConfigureAwait(false);
        return saved.Saved
            ? PublicationResult(GameSharedBehaviorMutationStatus.Changed, revoked.Revision, revoked)
            : PublicationResult(
                GameSharedBehaviorMutationStatus.StoreConflict,
                saved.Current?.Revision ?? 0,
                saved.Current);
    }

    public async ValueTask<IReadOnlyList<GameSharedBehaviorPublication>> DiscoverAsync(
        GameInput input,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var boundary = await ReadBoundaryAsync(input, cancellationToken).ConfigureAwait(false);
        var audiences = await ReadAudiencesAsync(input, cancellationToken).ConfigureAwait(false);
        var discovered = new List<GameSharedBehaviorPublication>();
        string? cursor = null;
        var scanned = 0;
        while (discovered.Count < _options.MaximumDiscoverableBehaviors
               && scanned < _options.MaximumCatalogRecordsScannedPerDiscovery)
        {
            var remaining = _options.MaximumCatalogRecordsScannedPerDiscovery - scanned;
            var page = await _store.QueryAsync(
                new GameSharedBehaviorStoreQuery(
                    audiences,
                    Math.Min(256, remaining),
                    includeRevoked: true,
                    afterPublicationId: cursor),
                cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            scanned = checked(scanned + page.Count);
            cursor = page[^1].PublicationId;
            discovered.AddRange(page
                .Where(value => value.Status == GameSharedBehaviorPublicationStatus.Published)
                .Where(value => VisibleInBoundary(value, boundary))
                .Take(_options.MaximumDiscoverableBehaviors - discovered.Count));
        }

        return discovered;
    }

    public async ValueTask<GameSharedBehaviorMutationResult> AdoptAsync(
        IGameSessionStore sessionStore,
        GameInput input,
        long expectedSessionRevision,
        string publicationId,
        GameBehaviorWorldBoundary boundary,
        string auditReference,
        CancellationToken cancellationToken = default)
    {
        if (sessionStore is null)
        {
            throw new ArgumentNullException(nameof(sessionStore));
        }

        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (boundary is null)
        {
            throw new ArgumentNullException(nameof(boundary));
        }

        if (!string.Equals(input.Moment.TimelineId, boundary.TimelineId, StringComparison.Ordinal))
        {
            return SessionResult(GameSharedBehaviorMutationStatus.WorldChanged, 0);
        }

        GameBehaviorWorldBoundary trustedBoundary;
        try
        {
            trustedBoundary = await ReadBoundaryAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.WorldChanged, 0);
        }

        if (!string.Equals(trustedBoundary.TimelineId, boundary.TimelineId, StringComparison.Ordinal)
            || !string.Equals(trustedBoundary.Generation, boundary.Generation, StringComparison.Ordinal)
            || trustedBoundary.Revision != boundary.Revision)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.WorldChanged, 0);
        }

        var key = new GameSessionKey(input.SessionId, input.ActorId);
        var snapshot = await sessionStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.SessionNotFound, 0);
        }

        EnsureKey(snapshot, key);
        if (snapshot.Revision != expectedSessionRevision)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.RevisionConflict, snapshot.Revision);
        }

        var publication = await _store.LoadAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (publication is null)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.PublicationNotFound, snapshot.Revision);
        }

        if (publication.Status != GameSharedBehaviorPublicationStatus.Published)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.InvalidStatus, snapshot.Revision, publication);
        }

        var audiences = await ReadAudiencesAsync(input, cancellationToken).ConfigureAwait(false);
        if (!audiences.Contains(publication.Audience) || !VisibleInBoundary(publication, boundary))
        {
            return SessionResult(GameSharedBehaviorMutationStatus.AudienceDenied, snapshot.Revision, publication);
        }

        if (!await ValidateAdoptionAsync(input, boundary, publication, cancellationToken).ConfigureAwait(false))
        {
            return SessionResult(GameSharedBehaviorMutationStatus.ValidationRejected, snapshot.Revision, publication);
        }

        var state = new Dictionary<string, string>(StoredExtensionStateReader.Read(snapshot, ExtensionId), StringComparer.Ordinal);
        var all = ReadAll(state).ToArray();
        var existing = all.SingleOrDefault(value => string.Equals(value.PublicationId, publicationId, StringComparison.Ordinal));
        if (existing is not null && existing.Status == GameSharedBehaviorAdoptionStatus.Active)
        {
            return SessionResult(
                GameSharedBehaviorMutationStatus.AlreadyExists,
                snapshot.Revision,
                publication,
                new GameSharedBehaviorAdoptionSnapshot(existing));
        }

        var occupied = all.Count(value =>
            value.Status is GameSharedBehaviorAdoptionStatus.Active or GameSharedBehaviorAdoptionStatus.Suspended);
        var replaced = all.Count(value =>
            !string.Equals(value.PublicationId, publication.PublicationId, StringComparison.Ordinal)
            && string.Equals(value.BehaviorFamilyId, publication.BehaviorFamilyId, StringComparison.Ordinal)
            && value.Status is GameSharedBehaviorAdoptionStatus.Active or GameSharedBehaviorAdoptionStatus.Suspended);
        var targetAlreadyOccupies = existing is not null
            && existing.Status is GameSharedBehaviorAdoptionStatus.Active or GameSharedBehaviorAdoptionStatus.Suspended;
        if (occupied - replaced + (targetAlreadyOccupies ? 0 : 1) > _options.MaximumAdoptionsPerActor)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.LimitExceeded, snapshot.Revision, publication);
        }

        foreach (var active in all.Where(value =>
                     string.Equals(value.BehaviorFamilyId, publication.BehaviorFamilyId, StringComparison.Ordinal)
                     && value.Status is GameSharedBehaviorAdoptionStatus.Active or GameSharedBehaviorAdoptionStatus.Suspended
                     && !string.Equals(value.PublicationId, publication.PublicationId, StringComparison.Ordinal)))
        {
            active.Status = GameSharedBehaviorAdoptionStatus.Superseded;
            active.Revision = checked(active.Revision + 1);
            active.LastReason = "superseded-by:" + publication.PublicationId;
            active.TerminalSequence = NextTerminalSequence(state);
            state[KeyFor(active.PublicationId)] = JsonSerializer.Serialize(active);
        }

        var document = new SharedBehaviorAdoptionDocument
        {
            PublicationId = publication.PublicationId,
            ContentHash = publication.Behavior.ContentHash,
            BehaviorFamilyId = publication.BehaviorFamilyId,
            FamilyVersion = publication.FamilyVersion,
            SourceBehaviorId = publication.Behavior.SourceBehaviorId,
            SourceBehaviorVersion = publication.Behavior.SourceBehaviorVersion,
            Revision = existing is null ? 1 : checked(existing.Revision + 1),
            Status = GameSharedBehaviorAdoptionStatus.Active,
            AdoptedInputId = input.InputId,
            TimelineId = boundary.TimelineId,
            WorldGeneration = boundary.Generation,
            WorldRevision = boundary.Revision,
            TerminalSequence = null,
            LastReason = "host-adopted:" + GameSharedBehaviorAudience.Require(auditReference, 2_048, nameof(auditReference)),
        };
        Validate(document);
        state[KeyFor(document.PublicationId)] = JsonSerializer.Serialize(document);
        PruneInactive(state, _options.MaximumRetainedInactiveAdoptions);
        return await SaveAdoptionAsync(
            sessionStore,
            snapshot,
            state,
            publication,
            document,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<GameSharedBehaviorMutationResult> WithdrawAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        string publicationId,
        long expectedSessionRevision,
        string reason,
        CancellationToken cancellationToken = default) =>
        MutateAdoptionAsync(
            sessionStore,
            session,
            publicationId,
            expectedSessionRevision,
            AdoptionMutationKind.Withdraw,
            GameSharedBehaviorAudience.Require(reason, 2_048, nameof(reason)),
            cancellationToken);

    public ValueTask<GameSharedBehaviorMutationResult> RecordEvaluationAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        string publicationId,
        long expectedSessionRevision,
        bool succeeded,
        string evidenceReference,
        CancellationToken cancellationToken = default) =>
        MutateAdoptionAsync(
            sessionStore,
            session,
            publicationId,
            expectedSessionRevision,
            succeeded ? AdoptionMutationKind.EvaluationSucceeded : AdoptionMutationKind.EvaluationFailed,
            GameSharedBehaviorAudience.Require(evidenceReference, 2_048, nameof(evidenceReference)),
            cancellationToken);

    public static async ValueTask<GameSharedBehaviorAdoptionQueryResult> ReadAdoptionsAsync(
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
            return new GameSharedBehaviorAdoptionQueryResult(key, 0, Array.Empty<GameSharedBehaviorAdoptionSnapshot>());
        }

        EnsureKey(snapshot, key);
        var adoptions = ReadAll(StoredExtensionStateReader.Read(snapshot, ExtensionId))
            .Where(value => includeInactive || value.Status == GameSharedBehaviorAdoptionStatus.Active)
            .OrderBy(value => value.PublicationId, StringComparer.Ordinal)
            .Select(value => new GameSharedBehaviorAdoptionSnapshot(value));
        return new GameSharedBehaviorAdoptionQueryResult(key, snapshot.Revision, adoptions);
    }

    private async ValueTask<IReadOnlyList<GameSkill>> SelectSkillsAsync(
        GameAgentExtensionRunContext context,
        IReadOnlyCollection<string> activeToolNames,
        int maximumSkills,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (maximumSkills <= 0 || maximumCharacters <= 0)
        {
            return Array.Empty<GameSkill>();
        }

        var adopted = ReadAll(context.State)
            .Where(value => value.Status == GameSharedBehaviorAdoptionStatus.Active)
            .OrderBy(value => value.PublicationId, StringComparer.Ordinal)
            .ToArray();
        if (adopted.Length == 0)
        {
            return Array.Empty<GameSkill>();
        }

        GameBehaviorWorldBoundary boundary;
        IReadOnlyList<GameSharedBehaviorAudience> audiences;
        try
        {
            boundary = await ReadBoundaryAsync(context.Input, cancellationToken).ConfigureAwait(false);
            audiences = await ReadAudiencesAsync(context.Input, cancellationToken).ConfigureAwait(false);
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
        var skills = new List<GameSkill>();
        var characters = 0L;
        var publications = await Task.WhenAll(adopted
            .Select(value => LoadForProjectionAsync(value.PublicationId, cancellationToken)))
            .ConfigureAwait(false);
        for (var index = 0; index < adopted.Length; index++)
        {
            var adoption = adopted[index];
            var publication = publications[index];

            if (publication is null
                || !string.Equals(publication.PublicationId, adoption.PublicationId, StringComparison.Ordinal)
                || publication.Status != GameSharedBehaviorPublicationStatus.Published
                || !string.Equals(publication.Behavior.ContentHash, adoption.ContentHash, StringComparison.Ordinal)
                || !string.Equals(publication.BehaviorFamilyId, adoption.BehaviorFamilyId, StringComparison.Ordinal)
                || publication.FamilyVersion != adoption.FamilyVersion
                || !string.Equals(publication.Behavior.SourceBehaviorId, adoption.SourceBehaviorId, StringComparison.Ordinal)
                || publication.Behavior.SourceBehaviorVersion != adoption.SourceBehaviorVersion
                || !audiences.Contains(publication.Audience)
                || !VisibleInBoundary(publication, boundary)
                || (publication.Audience.Kind == GameSharedBehaviorAudienceKind.WorldGeneration
                    && (!string.Equals(adoption.TimelineId, boundary.TimelineId, StringComparison.Ordinal)
                        || !string.Equals(adoption.WorldGeneration, boundary.Generation, StringComparison.Ordinal)))
                || (publication.Behavior.InputTypes.Count > 0
                    && !publication.Behavior.InputTypes.Contains(context.Input.Type, StringComparer.Ordinal))
                || publication.Behavior.ToolNames.Any(value => !tools.Contains(value)))
            {
                continue;
            }

            var skill = ToSkill(publication);
            if (characters + skill.CharacterCount > maximumCharacters)
            {
                continue;
            }

            skills.Add(skill);
            characters += skill.CharacterCount;
            if (skills.Count >= maximumSkills)
            {
                break;
            }
        }

        return skills;
    }

    private async Task<GameSharedBehaviorPublication?> LoadForProjectionAsync(
        string publicationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.LoadAsync(publicationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static GameSkill ToSkill(GameSharedBehaviorPublication publication)
    {
        var definition = publication.Behavior;
        return new GameSkill(
            "shared." + publication.BehaviorFamilyId + ".v"
                + publication.FamilyVersion.ToString(CultureInfo.InvariantCulture)
                + "." + definition.ContentHash.Substring(0, 12),
            definition.Title,
            "Host-published behavior explicitly adopted by this actor.",
            BehaviorLearningExtension.FormatSkillInstructions(
                definition.Instructions,
                definition.Reflection,
                definition.Steps),
            definition.InputTypes,
            definition.ToolNames,
            priority: 90,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["publicationId"] = publication.PublicationId,
                ["behaviorFamilyId"] = publication.BehaviorFamilyId,
                ["familyVersion"] = publication.FamilyVersion.ToString(CultureInfo.InvariantCulture),
                ["sourceBehaviorId"] = definition.SourceBehaviorId,
                ["sourceBehaviorVersion"] = definition.SourceBehaviorVersion.ToString(CultureInfo.InvariantCulture),
                ["contentHash"] = definition.ContentHash,
                ["audience"] = publication.Audience.ToString(),
                ["provenance"] = "host-published-behavior",
            });
    }

    private async ValueTask<GameSharedBehaviorMutationResult> MutateAdoptionAsync(
        IGameSessionStore sessionStore,
        GameSessionKey session,
        string publicationId,
        long expectedSessionRevision,
        AdoptionMutationKind mutation,
        string reason,
        CancellationToken cancellationToken)
    {
        if (sessionStore is null)
        {
            throw new ArgumentNullException(nameof(sessionStore));
        }

        var key = new GameSessionKey(session.SessionId, session.ActorId);
        var snapshot = await sessionStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.SessionNotFound, 0);
        }

        EnsureKey(snapshot, key);
        if (snapshot.Revision != expectedSessionRevision)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.RevisionConflict, snapshot.Revision);
        }

        var state = new Dictionary<string, string>(StoredExtensionStateReader.Read(snapshot, ExtensionId), StringComparer.Ordinal);
        var document = ReadAll(state).SingleOrDefault(value => string.Equals(value.PublicationId, publicationId, StringComparison.Ordinal));
        if (document is null)
        {
            return SessionResult(GameSharedBehaviorMutationStatus.PublicationNotFound, snapshot.Revision);
        }

        var canWithdraw = mutation == AdoptionMutationKind.Withdraw
            && document.Status is GameSharedBehaviorAdoptionStatus.Active
                or GameSharedBehaviorAdoptionStatus.Suspended;
        if (!canWithdraw && document.Status != GameSharedBehaviorAdoptionStatus.Active)
        {
            return SessionResult(
                GameSharedBehaviorMutationStatus.InvalidStatus,
                snapshot.Revision,
                adoption: new GameSharedBehaviorAdoptionSnapshot(document));
        }

        if (mutation == AdoptionMutationKind.Withdraw)
        {
            document.Status = GameSharedBehaviorAdoptionStatus.Withdrawn;
            document.LastReason = reason;
            document.TerminalSequence = NextTerminalSequence(state);
        }
        else if (mutation == AdoptionMutationKind.EvaluationSucceeded)
        {
            document.SuccessfulEvaluations = checked(document.SuccessfulEvaluations + 1);
            document.ConsecutiveFailures = 0;
            document.LastReason = "evaluation-succeeded:" + reason;
        }
        else if (mutation == AdoptionMutationKind.EvaluationFailed)
        {
            document.FailedEvaluations = checked(document.FailedEvaluations + 1);
            document.ConsecutiveFailures = checked(document.ConsecutiveFailures + 1);
            document.LastReason = "evaluation-failed:" + reason;
            if (document.ConsecutiveFailures >= _options.ConsecutiveFailuresBeforeSuspension)
            {
                document.Status = GameSharedBehaviorAdoptionStatus.Suspended;
            }
        }
        else
        {
            throw new InvalidOperationException("The shared behavior adoption mutation is invalid.");
        }

        document.Revision = checked(document.Revision + 1);
        state[KeyFor(document.PublicationId)] = JsonSerializer.Serialize(document);
        PruneInactive(state, _options.MaximumRetainedInactiveAdoptions);
        return await SaveAdoptionAsync(
            sessionStore,
            snapshot,
            state,
            null,
            document,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<GameSharedBehaviorMutationResult> SaveAdoptionAsync(
        IGameSessionStore sessionStore,
        GameSessionSnapshot snapshot,
        IReadOnlyDictionary<string, string> state,
        GameSharedBehaviorPublication? publication,
        SharedBehaviorAdoptionDocument document,
        CancellationToken cancellationToken)
    {
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
        return save.Saved
            ? SessionResult(
                GameSharedBehaviorMutationStatus.Changed,
                save.Current.Revision,
                publication,
                new GameSharedBehaviorAdoptionSnapshot(document))
            : SessionResult(GameSharedBehaviorMutationStatus.SessionConflict, save.Current.Revision);
    }

    private async ValueTask<bool> ValidatePublicationAsync(
        GameSessionKey sourceSession,
        GameLearnedBehaviorSnapshot source,
        GameSharedBehaviorAudience audience,
        GameBehaviorWorldBoundary boundary,
        string publicationId,
        string behaviorFamilyId,
        int familyVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _publicationValidator(
                new GameSharedBehaviorPublicationValidationRequest(
                    sourceSession,
                    source,
                    audience,
                    boundary,
                    publicationId,
                    behaviorFamilyId,
                    familyVersion),
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

    private async ValueTask<bool> ValidateAdoptionAsync(
        GameInput input,
        GameBehaviorWorldBoundary boundary,
        GameSharedBehaviorPublication publication,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _adoptionValidator(
                new GameSharedBehaviorAdoptionValidationRequest(input, boundary, publication),
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

    private async ValueTask<GameBehaviorWorldBoundary> ReadBoundaryAsync(
        GameInput input,
        CancellationToken cancellationToken) =>
        await _boundaryProvider(input, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The behavior boundary provider returned null.");

    private async ValueTask<IReadOnlyList<GameSharedBehaviorAudience>> ReadAudiencesAsync(
        GameInput input,
        CancellationToken cancellationToken)
    {
        var audiences = await _audienceProvider(input, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The shared behavior audience provider returned null.");
        if (audiences.Count is < 1 or > 64 || audiences.Any(value => value is null))
        {
            throw new InvalidOperationException("The shared behavior audience provider exceeded its bounds.");
        }

        return Array.AsReadOnly(audiences.Distinct().ToArray());
    }

    private static bool SourceBoundaryMatches(
        GameLearnedBehaviorSnapshot behavior,
        GameBehaviorWorldBoundary boundary) =>
        string.Equals(behavior.TimelineId, boundary.TimelineId, StringComparison.Ordinal)
        && (behavior.Scope == GameLearnedBehaviorScope.Actor
            || (string.Equals(behavior.WorldGeneration, boundary.Generation, StringComparison.Ordinal)
                && boundary.Revision >= behavior.WorldRevision));

    private static bool VisibleInBoundary(
        GameSharedBehaviorPublication publication,
        GameBehaviorWorldBoundary boundary) =>
        publication.Audience.Kind != GameSharedBehaviorAudienceKind.WorldGeneration
        || (string.Equals(publication.TimelineId, boundary.TimelineId, StringComparison.Ordinal)
            && string.Equals(publication.WorldGeneration, boundary.Generation, StringComparison.Ordinal)
            && boundary.Revision >= publication.WorldRevision);

    private static IReadOnlyList<SharedBehaviorAdoptionDocument> ReadAll(GameAgentExtensionState state) =>
        ReadAll(state.Snapshot());

    private static IReadOnlyList<SharedBehaviorAdoptionDocument> ReadAll(IReadOnlyDictionary<string, string> state)
    {
        var values = state
            .Where(pair => pair.Key.StartsWith(AdoptionPrefix, StringComparison.Ordinal))
            .Select(pair =>
            {
                var document = JsonSerializer.Deserialize<SharedBehaviorAdoptionDocument>(pair.Value)
                    ?? throw new InvalidOperationException("A shared behavior adoption document is null.");
                Validate(document);
                if (!string.Equals(pair.Key, KeyFor(document.PublicationId), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A shared behavior adoption identity does not match its state key.");
                }

                return document;
            })
            .ToArray();
        if (values.Select(value => value.PublicationId).Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new InvalidOperationException("Shared behavior adoption state contains duplicate publications.");
        }

        return values;
    }

    private static void Validate(SharedBehaviorAdoptionDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.PublicationId)
            || document.PublicationId.Length > 256
            || document.PublicationId.Any(char.IsControl)
            || document.ContentHash.Length != 64
            || document.ContentHash.Any(value => !Uri.IsHexDigit(value))
            || string.IsNullOrWhiteSpace(document.BehaviorFamilyId)
            || document.BehaviorFamilyId.Length > 128
            || document.BehaviorFamilyId[0] is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
             || document.BehaviorFamilyId.Any(character => character is not (>= 'A' and <= 'Z')
                 and not (>= 'a' and <= 'z')
                 and not (>= '0' and <= '9')
                 && character is not '.' and not '_' and not '-')
            || document.FamilyVersion < 1
            || string.IsNullOrWhiteSpace(document.SourceBehaviorId)
            || document.SourceBehaviorId.Length > 128
            || document.SourceBehaviorId[0] is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
            || document.SourceBehaviorId.Any(character => character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                && character is not '.' and not '_' and not '-')
            || document.SourceBehaviorVersion < 1
            || document.Revision < 1
            || !Enum.IsDefined(typeof(GameSharedBehaviorAdoptionStatus), document.Status)
            || string.IsNullOrWhiteSpace(document.AdoptedInputId)
            || document.AdoptedInputId.Length > 2_048
            || string.IsNullOrWhiteSpace(document.TimelineId)
            || document.TimelineId.Length > 512
            || string.IsNullOrWhiteSpace(document.WorldGeneration)
            || document.WorldGeneration.Length > 512
            || document.WorldRevision < 0
            || document.SuccessfulEvaluations < 0
            || document.FailedEvaluations < 0
            || document.ConsecutiveFailures < 0
            || (document.Status is GameSharedBehaviorAdoptionStatus.Active or GameSharedBehaviorAdoptionStatus.Suspended
                ? document.TerminalSequence is not null
                : document.TerminalSequence is null or < 1)
            || (document.LastReason is not null
                && (document.LastReason.Length > 4_096 || document.LastReason.Any(char.IsControl))))
        {
            throw new InvalidOperationException("Shared behavior adoption state is invalid.");
        }
    }

    private static string KeyFor(string publicationId) => AdoptionPrefix + publicationId;

    private static long NextTerminalSequence(IDictionary<string, string> state)
    {
        var current = 0L;
        if (state.TryGetValue(TerminalSequenceKey, out var stored))
        {
            try
            {
                current = JsonSerializer.Deserialize<long>(stored);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("Shared behavior terminal sequence is invalid.", exception);
            }

            if (current < 0)
            {
                throw new InvalidOperationException("Shared behavior terminal sequence is invalid.");
            }
        }

        var next = checked(current + 1);
        state[TerminalSequenceKey] = JsonSerializer.Serialize(next);
        return next;
    }

    private static void PruneInactive(IDictionary<string, string> state, int maximumRetained)
    {
        var inactive = ReadAll(new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(state, StringComparer.Ordinal)))
            .Where(value => value.Status is GameSharedBehaviorAdoptionStatus.Withdrawn
                or GameSharedBehaviorAdoptionStatus.Superseded)
            .OrderBy(value => value.TerminalSequence)
            .ThenBy(value => value.PublicationId, StringComparer.Ordinal)
            .ToArray();
        foreach (var document in inactive.Take(Math.Max(0, inactive.Length - maximumRetained)))
        {
            state.Remove(KeyFor(document.PublicationId));
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

    private static void EnsureKey(GameSessionSnapshot snapshot, GameSessionKey key)
    {
        if (snapshot.Key != key)
        {
            throw new InvalidOperationException("The session store returned a different session key.");
        }
    }

    private static GameSharedBehaviorMutationResult SessionResult(
        GameSharedBehaviorMutationStatus status,
        long sessionRevision,
        GameSharedBehaviorPublication? publication = null,
        GameSharedBehaviorAdoptionSnapshot? adoption = null) =>
        new(status, sessionRevision, publication?.Revision, publication, adoption);

    private static GameSharedBehaviorMutationResult PublicationResult(
        GameSharedBehaviorMutationStatus status,
        long publicationRevision,
        GameSharedBehaviorPublication? publication = null) =>
        new(status, null, publicationRevision, publication);

    private static GameSharedBehaviorMutationResult PublishResult(
        GameSharedBehaviorMutationStatus status,
        long sessionRevision,
        long publicationRevision,
        GameSharedBehaviorPublication? publication = null) =>
        new(status, sessionRevision, publicationRevision, publication);
}

internal sealed class SharedBehaviorAdoptionDocument
{
    public string PublicationId { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public string BehaviorFamilyId { get; set; } = string.Empty;

    public int FamilyVersion { get; set; }

    public string SourceBehaviorId { get; set; } = string.Empty;

    public int SourceBehaviorVersion { get; set; }

    public long Revision { get; set; }

    public GameSharedBehaviorAdoptionStatus Status { get; set; }

    public string AdoptedInputId { get; set; } = string.Empty;

    public string TimelineId { get; set; } = string.Empty;

    public string WorldGeneration { get; set; } = string.Empty;

    public long WorldRevision { get; set; }

    public int SuccessfulEvaluations { get; set; }

    public int FailedEvaluations { get; set; }

    public int ConsecutiveFailures { get; set; }

    public long? TerminalSequence { get; set; }

    public string? LastReason { get; set; }
}

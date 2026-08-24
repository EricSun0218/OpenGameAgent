using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Extensions;

public enum GameSharedBehaviorAudienceKind
{
    Game,
    WorldGeneration,
    Role,
    Faction,
}

/// <summary>
/// A host-defined catalog audience. Publishing makes a behavior discoverable to matching actors;
/// it never activates the behavior for them.
/// </summary>
public sealed class GameSharedBehaviorAudience : IEquatable<GameSharedBehaviorAudience>
{
    public GameSharedBehaviorAudience(GameSharedBehaviorAudienceKind kind, string audienceId)
    {
        if (!Enum.IsDefined(typeof(GameSharedBehaviorAudienceKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        AudienceId = Require(audienceId, 256, nameof(audienceId));
    }

    public GameSharedBehaviorAudienceKind Kind { get; }

    public string AudienceId { get; }

    public bool Equals(GameSharedBehaviorAudience? other) =>
        other is not null && Kind == other.Kind && string.Equals(AudienceId, other.AudienceId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as GameSharedBehaviorAudience);

    public override int GetHashCode() => (StringComparer.Ordinal.GetHashCode(AudienceId) * 397) ^ (int)Kind;

    public override string ToString() => Kind.ToString() + ":" + AudienceId;

    internal static string Require(string value, int maximum, string name) =>
        string.IsNullOrWhiteSpace(value)
        || value.Length > maximum
        || ContainsInvalidCharacters(value)
            ? throw new ArgumentException(
                $"The value must contain 1 to {maximum} UTF-16 code units made of printable Unicode scalar values.",
                name)
            : value;

    private static bool ContainsInvalidCharacters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character) || char.IsLowSurrogate(character))
            {
                return true;
            }

            if (!char.IsHighSurrogate(character))
            {
                continue;
            }

            if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
            {
                return true;
            }

            index++;
        }

        return false;
    }
}

public sealed class GameSharedBehaviorDefinition
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public GameSharedBehaviorDefinition(
        string sourceBehaviorId,
        int sourceBehaviorVersion,
        string title,
        string instructions,
        GameBehaviorReflection reflection,
        IEnumerable<GameBehaviorStep>? steps = null,
        IEnumerable<string>? inputTypes = null,
        IEnumerable<string>? toolNames = null)
    {
        SourceBehaviorId = RequireId(sourceBehaviorId, nameof(sourceBehaviorId));
        SourceBehaviorVersion = sourceBehaviorVersion >= 1
            ? sourceBehaviorVersion
            : throw new ArgumentOutOfRangeException(nameof(sourceBehaviorVersion));
        Title = GameSharedBehaviorAudience.Require(title, 256, nameof(title));
        Instructions = GameSharedBehaviorAudience.Require(instructions, 100_000, nameof(instructions));
        Reflection = reflection ?? throw new ArgumentNullException(nameof(reflection));
        InputTypes = CopyIds(inputTypes, nameof(inputTypes), 64);
        ToolNames = CopyIds(toolNames, nameof(toolNames), 128);
        var copiedSteps = GameBehaviorCollection.CopyBounded(
            steps,
            128,
            nameof(steps),
            allowNullCollection: true);
        if (copiedSteps.Length > 128 || copiedSteps.Any(value => value is null))
        {
            throw new ArgumentException("A shared behavior can contain at most 128 non-null steps.", nameof(steps));
        }

        if (copiedSteps.Select(value => value.StepId).Distinct(StringComparer.Ordinal).Count() != copiedSteps.Length)
        {
            throw new ArgumentException("Shared behavior step IDs must be unique.", nameof(steps));
        }

        var tools = new HashSet<string>(ToolNames, StringComparer.Ordinal);
        if (copiedSteps.Any(value => !tools.Contains(value.ToolName))
            || (tools.Count > 0 && !tools.SetEquals(copiedSteps.Select(value => value.ToolName))))
        {
            throw new ArgumentException("Shared behavior steps must cover exactly the declared tool set.", nameof(steps));
        }

        Steps = Array.AsReadOnly(copiedSteps);
        ContentHash = ComputeHash(this);
    }

    public string SourceBehaviorId { get; }

    public int SourceBehaviorVersion { get; }

    public string Title { get; }

    public string Instructions { get; }

    public GameBehaviorReflection Reflection { get; }

    public IReadOnlyList<GameBehaviorStep> Steps { get; }

    public IReadOnlyList<string> InputTypes { get; }

    public IReadOnlyList<string> ToolNames { get; }

    public string ContentHash { get; }

    internal static GameSharedBehaviorDefinition From(GameLearnedBehaviorSnapshot behavior) => new(
        behavior.BehaviorId,
        behavior.Version,
        behavior.Title,
        behavior.Instructions,
        behavior.Reflection,
        behavior.Steps,
        behavior.InputTypes,
        behavior.ToolNames);

    private static IReadOnlyList<string> CopyIds(IEnumerable<string>? values, string name, int maximum)
    {
        var bounded = GameBehaviorCollection.CopyBounded(
            values,
            maximum,
            name,
            allowNullCollection: true);
        var copied = bounded
            .Select(value => GameSharedBehaviorAudience.Require(value, 256, name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (copied.Length > maximum)
        {
            throw new ArgumentException($"The collection cannot contain more than {maximum} values.", name);
        }

        return Array.AsReadOnly(copied);
    }

    private static string RequireId(string value, string name)
    {
        var result = GameSharedBehaviorAudience.Require(value, 128, name);
        if (result[0] is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9')
            || result.Any(character => character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Behavior IDs use ASCII letters, digits, dots, underscores, and hyphens.", name);
        }

        return result;
    }

    private static string ComputeHash(GameSharedBehaviorDefinition definition)
    {
        var builder = new StringBuilder();
        Add(builder, "opengameagent.shared-behavior.v1");
        AddField(builder, "source-behavior-id", definition.SourceBehaviorId);
        AddField(
            builder,
            "source-behavior-version",
            definition.SourceBehaviorVersion.ToString(CultureInfo.InvariantCulture));
        AddField(builder, "title", definition.Title);
        AddField(builder, "instructions", definition.Instructions);
        AddField(builder, "reflection.observation", definition.Reflection.Observation);
        AddField(builder, "reflection.strategy", definition.Reflection.Strategy);
        AddField(builder, "reflection.outcome", definition.Reflection.Outcome);
        AddField(builder, "reflection.applicability", definition.Reflection.Applicability);
        AddField(builder, "reflection.failure-mode-count", definition.Reflection.FailureModes.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var value in definition.Reflection.FailureModes.OrderBy(value => value, StringComparer.Ordinal))
        {
            AddField(builder, "reflection.failure-mode", value);
        }

        AddField(builder, "input-type-count", definition.InputTypes.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var value in definition.InputTypes.OrderBy(value => value, StringComparer.Ordinal))
        {
            AddField(builder, "input-type", value);
        }

        AddField(builder, "tool-name-count", definition.ToolNames.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var value in definition.ToolNames.OrderBy(value => value, StringComparer.Ordinal))
        {
            AddField(builder, "tool-name", value);
        }

        AddField(builder, "step-count", definition.Steps.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var step in definition.Steps)
        {
            AddField(builder, "step.id", step.StepId);
            AddField(builder, "step.tool", step.ToolName);
            AddField(builder, "step.instruction", step.Instruction);
        }

        using var algorithm = SHA256.Create();
        var hash = algorithm.ComputeHash(StrictUtf8.GetBytes(builder.ToString()));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void Add(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');

    private static void AddField(StringBuilder builder, string name, string value)
    {
        Add(builder, name);
        Add(builder, value);
    }
}

public enum GameSharedBehaviorPublicationStatus
{
    Published,
    Revoked,
}

public sealed class GameSharedBehaviorPublication
{
    public GameSharedBehaviorPublication(
        string publicationId,
        string behaviorFamilyId,
        int familyVersion,
        long revision,
        GameSharedBehaviorPublicationStatus status,
        GameSharedBehaviorAudience audience,
        GameSharedBehaviorDefinition behavior,
        GameSessionKey sourceSession,
        string timelineId,
        string worldGeneration,
        long worldRevision,
        string auditReference,
        string? lastReason = null)
    {
        PublicationId = GameSharedBehaviorAudience.Require(publicationId, 256, nameof(publicationId));
        BehaviorFamilyId = RequireStableId(behaviorFamilyId, nameof(behaviorFamilyId));
        FamilyVersion = familyVersion >= 1
            ? familyVersion
            : throw new ArgumentOutOfRangeException(nameof(familyVersion));
        Revision = revision >= 1 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(typeof(GameSharedBehaviorPublicationStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        Audience = audience ?? throw new ArgumentNullException(nameof(audience));
        Behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        SourceSession = new GameSessionKey(sourceSession.SessionId, sourceSession.ActorId);
        TimelineId = GameSharedBehaviorAudience.Require(timelineId, 512, nameof(timelineId));
        WorldGeneration = GameSharedBehaviorAudience.Require(worldGeneration, 512, nameof(worldGeneration));
        WorldRevision = worldRevision >= 0 ? worldRevision : throw new ArgumentOutOfRangeException(nameof(worldRevision));
        AuditReference = GameSharedBehaviorAudience.Require(auditReference, 2_048, nameof(auditReference));
        LastReason = lastReason is null
            ? null
            : GameSharedBehaviorAudience.Require(lastReason, 2_048, nameof(lastReason));
    }

    public string PublicationId { get; }

    /// <summary>
    /// Host-assigned catalog-wide identity used to group versions of one reusable behavior.
    /// It is independent of the source actor's session-local behavior ID.
    /// </summary>
    public string BehaviorFamilyId { get; }

    /// <summary>
    /// Host-assigned monotonic version inside <see cref="BehaviorFamilyId"/>. It is independent
    /// of the source actor's session-local behavior version and defines catalog rollback order.
    /// </summary>
    public int FamilyVersion { get; }

    public long Revision { get; }

    public GameSharedBehaviorPublicationStatus Status { get; }

    public GameSharedBehaviorAudience Audience { get; }

    public GameSharedBehaviorDefinition Behavior { get; }

    public GameSessionKey SourceSession { get; }

    public string TimelineId { get; }

    public string WorldGeneration { get; }

    public long WorldRevision { get; }

    public string AuditReference { get; }

    public string? LastReason { get; }

    internal static string RequireStableId(string value, string name)
    {
        var result = GameSharedBehaviorAudience.Require(value, 128, name);
        if (result[0] is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9')
            || result.Any(character => character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                "Shared behavior family IDs use ASCII letters, digits, dots, underscores, and hyphens.",
                name);
        }

        return result;
    }
}

public sealed class GameSharedBehaviorStoreQuery
{
    public GameSharedBehaviorStoreQuery(
        IEnumerable<GameSharedBehaviorAudience> audiences,
        int maximumResults = 64,
        bool includeRevoked = false,
        string? afterPublicationId = null)
    {
        var copied = GameBehaviorCollection.CopyBounded(
            audiences ?? throw new ArgumentNullException(nameof(audiences)),
            64,
            nameof(audiences));
        if (copied.Length is < 1 or > 64 || copied.Any(value => value is null))
        {
            throw new ArgumentException("A catalog query requires 1 to 64 non-null audiences.", nameof(audiences));
        }

        Audiences = Array.AsReadOnly(copied.Distinct().ToArray());
        MaximumResults = maximumResults is >= 1 and <= 1_000
            ? maximumResults
            : throw new ArgumentOutOfRangeException(nameof(maximumResults));
        IncludeRevoked = includeRevoked;
        AfterPublicationId = afterPublicationId is null
            ? null
            : GameSharedBehaviorAudience.Require(afterPublicationId, 256, nameof(afterPublicationId));
    }

    public IReadOnlyList<GameSharedBehaviorAudience> Audiences { get; }

    public int MaximumResults { get; }

    public bool IncludeRevoked { get; }

    /// <summary>
    /// Exclusive ordinal cursor used for deterministic bounded pagination.
    /// </summary>
    public string? AfterPublicationId { get; }
}

public sealed class GameSharedBehaviorStoreSaveResult
{
    public GameSharedBehaviorStoreSaveResult(bool saved, GameSharedBehaviorPublication? current)
    {
        Saved = saved;
        Current = current;
    }

    public bool Saved { get; }

    public GameSharedBehaviorPublication? Current { get; }
}

public interface IGameSharedBehaviorStore
{
    /// <summary>Loads one immutable publication record, or null when it does not exist.</summary>
    ValueTask<GameSharedBehaviorPublication?> LoadAsync(string publicationId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically creates or revokes a publication. Implementations return Saved=false on a CAS
    /// mismatch or a conflicting family-version reservation, and must enforce
    /// <see cref="GameSharedBehaviorStoreContract.ValidateTransition"/>. A
    /// (BehaviorFamilyId, FamilyVersion) pair identifies exactly one publication and content hash.
    /// </summary>
    ValueTask<GameSharedBehaviorStoreSaveResult> SaveAsync(
        GameSharedBehaviorPublication publication,
        long expectedRevision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns publications in ordinal PublicationId order, strictly after the optional cursor,
    /// filtered by audience/status and capped by MaximumResults.
    /// </summary>
    ValueTask<IReadOnlyList<GameSharedBehaviorPublication>> QueryAsync(
        GameSharedBehaviorStoreQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Canonical transition rules for shared behavior store implementations.
/// </summary>
public static class GameSharedBehaviorStoreContract
{
    public static void ValidateTransition(
        GameSharedBehaviorPublication? current,
        GameSharedBehaviorPublication next,
        long expectedRevision)
    {
        if (next is null)
        {
            throw new ArgumentNullException(nameof(next));
        }

        if (expectedRevision < 0 || next.Revision != checked(expectedRevision + 1))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        if ((current?.Revision ?? 0) != expectedRevision)
        {
            throw new InvalidOperationException("The current publication revision does not match the expected revision.");
        }

        if (current is null)
        {
            if (next.Status != GameSharedBehaviorPublicationStatus.Published || next.Revision != 1)
            {
                throw new InvalidOperationException("A publication must begin in the published state at revision one.");
            }

            return;
        }

        if (current.Status == GameSharedBehaviorPublicationStatus.Revoked)
        {
            throw new InvalidOperationException("A revoked shared behavior publication is immutable.");
        }

        if (next.Revision != checked(current.Revision + 1)
            || next.Status != GameSharedBehaviorPublicationStatus.Revoked
            || !HasSameImmutableIdentity(current, next))
        {
            throw new InvalidOperationException("A shared behavior publication can only transition from published to revoked.");
        }
    }

    public static bool HasSameImmutableIdentity(
        GameSharedBehaviorPublication left,
        GameSharedBehaviorPublication right)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        return string.Equals(left.PublicationId, right.PublicationId, StringComparison.Ordinal)
            && left.Audience.Equals(right.Audience)
            && string.Equals(left.BehaviorFamilyId, right.BehaviorFamilyId, StringComparison.Ordinal)
            && left.FamilyVersion == right.FamilyVersion
            && string.Equals(left.Behavior.ContentHash, right.Behavior.ContentHash, StringComparison.Ordinal)
            && left.SourceSession == right.SourceSession
            && string.Equals(left.TimelineId, right.TimelineId, StringComparison.Ordinal)
            && string.Equals(left.WorldGeneration, right.WorldGeneration, StringComparison.Ordinal)
            && left.WorldRevision == right.WorldRevision
            && string.Equals(left.AuditReference, right.AuditReference, StringComparison.Ordinal);
    }
}

public sealed class InMemoryGameSharedBehaviorStore : IGameSharedBehaviorStore
{
    private readonly ConcurrentDictionary<string, GameSharedBehaviorPublication> _records = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly int _capacity;

    public InMemoryGameSharedBehaviorStore(int capacity = 10_000)
    {
        _capacity = capacity is >= 1 and <= 1_000_000 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    public ValueTask<GameSharedBehaviorPublication?> LoadAsync(
        string publicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = GameSharedBehaviorAudience.Require(publicationId, 256, nameof(publicationId));
        return new ValueTask<GameSharedBehaviorPublication?>(_records.TryGetValue(id, out var value) ? value : null);
    }

    public ValueTask<GameSharedBehaviorStoreSaveResult> SaveAsync(
        GameSharedBehaviorPublication publication,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (publication is null)
        {
            throw new ArgumentNullException(nameof(publication));
        }

        if (expectedRevision < 0 || publication.Revision != checked(expectedRevision + 1))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _records.TryGetValue(publication.PublicationId, out var current);
            if ((current?.Revision ?? 0) != expectedRevision)
            {
                return new ValueTask<GameSharedBehaviorStoreSaveResult>(
                    new GameSharedBehaviorStoreSaveResult(false, current));
            }

            if (current is null && _records.Count >= _capacity)
            {
                throw new InvalidOperationException("The shared behavior catalog reached its configured capacity.");
            }

            if (current is null)
            {
                var familyConflict = _records.Values.FirstOrDefault(value =>
                    string.Equals(value.BehaviorFamilyId, publication.BehaviorFamilyId, StringComparison.Ordinal)
                    && value.FamilyVersion == publication.FamilyVersion);
                if (familyConflict is not null)
                {
                    return new ValueTask<GameSharedBehaviorStoreSaveResult>(
                        new GameSharedBehaviorStoreSaveResult(false, familyConflict));
                }
            }

            GameSharedBehaviorStoreContract.ValidateTransition(current, publication, expectedRevision);
            _records[publication.PublicationId] = publication;
            return new ValueTask<GameSharedBehaviorStoreSaveResult>(
                new GameSharedBehaviorStoreSaveResult(true, publication));
        }
    }

    public ValueTask<IReadOnlyList<GameSharedBehaviorPublication>> QueryAsync(
        GameSharedBehaviorStoreQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var audiences = new HashSet<GameSharedBehaviorAudience>(query.Audiences);
        IReadOnlyList<GameSharedBehaviorPublication> result = _records.Values
            .Where(value => audiences.Contains(value.Audience))
            .Where(value => query.IncludeRevoked || value.Status == GameSharedBehaviorPublicationStatus.Published)
            .Where(value => query.AfterPublicationId is null
                || string.CompareOrdinal(value.PublicationId, query.AfterPublicationId) > 0)
            .OrderBy(value => value.PublicationId, StringComparer.Ordinal)
            .Take(query.MaximumResults)
            .ToArray();
        return new ValueTask<IReadOnlyList<GameSharedBehaviorPublication>>(result);
    }

}

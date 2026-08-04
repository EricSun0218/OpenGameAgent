using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public sealed class MemoryProvenance
{
    public MemoryProvenance(
        string worldId,
        string? sessionId,
        long saveRevision,
        string sourceRunId,
        string sourceEventId,
        bool committed,
        string? timelineId = null,
        GameKnowledgePerspective? perspective = null,
        long? timelineEpoch = null)
    {
        WorldId = RuntimeGuard.RequiredUtf8(
            worldId,
            128,
            nameof(worldId));
        SessionId = sessionId is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                sessionId,
                128,
                nameof(sessionId));
        if (saveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveRevision));
        }

        SaveRevision = saveRevision;
        SourceRunId = RuntimeGuard.RequiredUtf8(
            sourceRunId,
            128,
            nameof(sourceRunId));
        SourceEventId = RuntimeGuard.RequiredUtf8(
            sourceEventId,
            128,
            nameof(sourceEventId));
        Committed = committed;
        TimelineId = timelineId is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                timelineId,
                128,
                nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        TimelineEpoch = timelineEpoch;
        Perspective = perspective;
    }

    public string WorldId { get; }

    public string? SessionId { get; }

    public long SaveRevision { get; }

    public string SourceRunId { get; }

    public string SourceEventId { get; }

    public bool Committed { get; }

    public string? TimelineId { get; }

    public long? TimelineEpoch { get; }

    public GameKnowledgePerspective? Perspective { get; }
}

public sealed class MemoryRecord
{
    /// <summary>
    /// Creates a memory record without game-specific provenance.
    /// </summary>
    /// <remarks>
    /// This overload preserves the original binary constructor contract.
    /// New code that needs provenance or game time can use the extended
    /// overload.
    /// </remarks>
    public MemoryRecord(
        string memoryId,
        string scope,
        JsonElement content,
        IEnumerable<string>? tags,
        int importance,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? expiresAt)
        : this(
            memoryId,
            scope,
            content,
            tags,
            importance,
            createdAt,
            updatedAt,
            expiresAt,
            provenance: null,
            gameTimeWindow: null)
    {
    }

    public MemoryRecord(
        string memoryId,
        string scope,
        JsonElement content,
        IEnumerable<string>? tags,
        int importance,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? expiresAt = null,
        MemoryProvenance? provenance = null,
        GameTimeWindow? gameTimeWindow = null)
    {
        MemoryId = RuntimeGuard.RequiredUtf8(memoryId, 128, nameof(memoryId));
        Scope = RuntimeGuard.RequiredUtf8(scope, 256, nameof(scope));
        JsonValueInspector.ValidateAndMeasure(
            content,
            new JsonValueLimits(maxUtf8Bytes: 131_072),
            nameof(content));
        if (importance is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(importance));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "Memory updatedAt cannot precede createdAt.",
                nameof(updatedAt));
        }

        Content = content.Clone();
        Tags = RuntimeGuard.CopyStrings(
            tags ?? Array.Empty<string>(),
            64,
            128,
            nameof(tags),
            sort: true,
            requireUnique: true);
        Importance = importance;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        ExpiresAt = expiresAt;
        Provenance = provenance;
        GameTimeWindow = gameTimeWindow;
        if (provenance?.TimelineId is not null
            && gameTimeWindow is not null)
        {
            var coordinate = gameTimeWindow.ValidFrom
                             ?? gameTimeWindow.ValidUntil!;
            if (!string.Equals(
                    provenance.TimelineId,
                    coordinate.TimelineId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Memory provenance and game-time timelines must match.",
                    nameof(gameTimeWindow));
            }
        }
    }

    public string MemoryId { get; }

    public string Scope { get; }

    public JsonElement Content { get; }

    public IReadOnlyList<string> Tags { get; }

    public int Importance { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public MemoryProvenance? Provenance { get; }

    public GameTimeWindow? GameTimeWindow { get; }
}

public sealed class MemoryQuery
{
    /// <summary>
    /// Creates a memory query without game-specific provenance filters.
    /// </summary>
    /// <remarks>
    /// This overload preserves the original binary constructor contract.
    /// New code that needs world, timeline, or perspective filtering can use
    /// the extended overload.
    /// </remarks>
    public MemoryQuery(
        string scope,
        JsonElement query,
        IEnumerable<string>? requiredTags,
        int maxResults,
        int maxUtf8Bytes,
        DateTimeOffset? now)
        : this(
            scope,
            query,
            requiredTags,
            maxResults,
            maxUtf8Bytes,
            now,
            worldId: null,
            sessionId: null,
            maximumSaveRevision: null,
            requireCommittedProvenance: false,
            timelineId: null,
            observer: null,
            gameTime: null,
            includeAllPerspectives: false,
            timelineEpoch: null)
    {
    }

    public MemoryQuery(
        string scope,
        JsonElement query,
        IEnumerable<string>? requiredTags = null,
        int maxResults = 8,
        int maxUtf8Bytes = 32_768,
        DateTimeOffset? now = null,
        string? worldId = null,
        string? sessionId = null,
        long? maximumSaveRevision = null,
        bool requireCommittedProvenance = false,
        string? timelineId = null,
        GameEntityIdentity? observer = null,
        GameTimePoint? gameTime = null,
        bool includeAllPerspectives = false,
        long? timelineEpoch = null)
    {
        Scope = RuntimeGuard.RequiredUtf8(scope, 256, nameof(scope));
        JsonValueInspector.ValidateAndMeasure(
            query,
            new JsonValueLimits(maxUtf8Bytes: 32_768),
            nameof(query));
        if (maxResults is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }

        if (maxUtf8Bytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        }

        Query = query.Clone();
        RequiredTags = RuntimeGuard.CopyStrings(
            requiredTags ?? Array.Empty<string>(),
            64,
            128,
            nameof(requiredTags),
            sort: true,
            requireUnique: true);
        MaxResults = maxResults;
        MaxUtf8Bytes = maxUtf8Bytes;
        Now = now ?? DateTimeOffset.UtcNow;
        WorldId = worldId is null
            ? null
            : RuntimeGuard.RequiredUtf8(worldId, 128, nameof(worldId));
        SessionId = sessionId is null
            ? null
            : RuntimeGuard.RequiredUtf8(sessionId, 128, nameof(sessionId));
        if (maximumSaveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSaveRevision));
        }

        MaximumSaveRevision = maximumSaveRevision;
        RequireCommittedProvenance = requireCommittedProvenance;
        var explicitTimelineId = timelineId is null
            ? null
            : RuntimeGuard.RequiredUtf8(
                timelineId,
                128,
                nameof(timelineId));
        Observer = observer;
        GameTime = gameTime;
        IncludeAllPerspectives = includeAllPerspectives;
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        if (gameTime is not null
            && explicitTimelineId is not null
            && !string.Equals(
                gameTime.TimelineId,
                explicitTimelineId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Memory query and game-time timelines must match.",
                nameof(gameTime));
        }

        TimelineId = explicitTimelineId ?? gameTime?.TimelineId;
        if (gameTime is not null
            && timelineEpoch.HasValue
            && timelineEpoch.Value != gameTime.Epoch)
        {
            throw new ArgumentException(
                "Memory query and game-time epochs must match.",
                nameof(timelineEpoch));
        }

        TimelineEpoch = timelineEpoch ?? gameTime?.Epoch;
        EnforceTimelineEpoch = timelineEpoch.HasValue;
    }

    public string Scope { get; }

    public JsonElement Query { get; }

    public IReadOnlyList<string> RequiredTags { get; }

    public int MaxResults { get; }

    public int MaxUtf8Bytes { get; }

    public DateTimeOffset Now { get; }

    public string? WorldId { get; }

    public string? SessionId { get; }

    public long? MaximumSaveRevision { get; }

    public bool RequireCommittedProvenance { get; }

    public string? TimelineId { get; }

    public long? TimelineEpoch { get; }

    internal bool EnforceTimelineEpoch { get; }

    public GameEntityIdentity? Observer { get; }

    public GameTimePoint? GameTime { get; }

    /// <summary>
    /// Gets whether this privileged query may read memories from every
    /// knowledge perspective. The default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Games should enable this only for trusted system-level operations, not
    /// for an actor's ordinary recall.
    /// </remarks>
    public bool IncludeAllPerspectives { get; }
}

public sealed class MemorySearchResult
{
    /// <summary>
    /// Creates a result returned by an <see cref="IMemoryProvider"/>.
    /// Higher scores rank before lower scores when a consumer combines
    /// provider results.
    /// </summary>
    public MemorySearchResult(MemoryRecord record, int score)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Score = score;
    }

    public MemoryRecord Record { get; }

    public int Score { get; }
}

public interface IMemoryProvider
{
    string ProviderId { get; }

    ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken);
}

public interface IMemoryStore : IMemoryProvider
{
    ValueTask UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(
        string memoryId,
        CancellationToken cancellationToken);
}

internal static class MemoryQueryFilter
{
    public static bool Matches(MemoryRecord record, MemoryQuery query)
    {
        return string.Equals(
                   record.Scope,
                   query.Scope,
                   StringComparison.Ordinal)
               && (record.ExpiresAt is null
                   || record.ExpiresAt > query.Now)
               && MatchesProvenance(
                   record.Provenance,
                   record.GameTimeWindow,
                   query)
               && MatchesGameTime(
                   record.GameTimeWindow,
                   query.GameTime)
               && query.RequiredTags.All(
                   tag => record.Tags.Contains(
                       tag,
                       StringComparer.Ordinal));
    }

    private static bool MatchesProvenance(
        MemoryProvenance? provenance,
        GameTimeWindow? gameTimeWindow,
        MemoryQuery query)
    {
        if (query.RequireCommittedProvenance
            && (provenance is null || !provenance.Committed))
        {
            return false;
        }

        if (query.WorldId is not null
            && (provenance is null
                || !string.Equals(
                    provenance.WorldId,
                    query.WorldId,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        if (query.SessionId is not null
            && (provenance is null
                || !string.Equals(
                    provenance.SessionId,
                    query.SessionId,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        if (query.TimelineId is not null)
        {
            var recordTimeline = provenance?.TimelineId
                                 ?? (gameTimeWindow?.ValidFrom
                                     ?? gameTimeWindow?.ValidUntil)
                                 ?.TimelineId;
            if (!string.Equals(
                    recordTimeline,
                    query.TimelineId,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        var recordWindowEpoch = (gameTimeWindow?.ValidFrom
                                 ?? gameTimeWindow?.ValidUntil)
            ?.Epoch;
        if (query.TimelineEpoch.HasValue
            && (query.EnforceTimelineEpoch
                || provenance?.TimelineEpoch.HasValue == true
                || recordWindowEpoch.HasValue))
        {
            var recordEpoch = provenance?.TimelineEpoch
                              ?? recordWindowEpoch;
            if (recordEpoch != query.TimelineEpoch)
            {
                return false;
            }
        }

        var perspective = provenance?.Perspective;
        if (perspective is not null
            && !query.IncludeAllPerspectives
            && (query.Observer is null
                || !query.Observer.IsSameIncarnation(
                    perspective.Observer)))
        {
            return false;
        }

        return !query.MaximumSaveRevision.HasValue
               || provenance is not null
               && provenance.SaveRevision
               <= query.MaximumSaveRevision.Value;
    }

    private static bool MatchesGameTime(
        GameTimeWindow? window,
        GameTimePoint? point)
    {
        return window is null
               || point is not null && window.Contains(point);
    }
}

/// <summary>
/// A bounded, deterministic baseline memory implementation. It intentionally
/// requires no embedding model: both natural-language and structured JSON
/// queries are reduced to invariant lexical terms. Production games can replace
/// it with a vector, graph, full-text, or service-backed provider through
/// IMemoryProvider.
/// </summary>
public sealed partial class DeterministicMemoryStore :
    IMemoryStore,
    IRuntimeAuthoritativeMemoryBatchStore,
    ILegacyRuntimeMemoryBatchReplayStore
{
    private readonly object _sync = new();
    private Dictionary<string, IndexedRecord> _records =
        new(StringComparer.Ordinal);
    private readonly int _capacity;

    public DeterministicMemoryStore(
        string providerId = "deterministic-local",
        int capacity = 10_000)
    {
        ProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public string ProviderId { get; }

    public int RuntimeMutationContractVersion =>
        RuntimeMemoryMutationContract.CurrentVersion;

    public ValueTask UpsertAsync(
        MemoryRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var indexed = new IndexedRecord(record, Tokenize(record.Content));
        lock (_sync)
        {
            _records.TryGetValue(record.MemoryId, out var existing);
            MemoryMutationAdmission.EnsureCanApplyUnconditionalUpsert(
                MemoryMutation.Upsert(record),
                existing?.Record);
            if (!_records.ContainsKey(record.MemoryId)
                && _records.Count >= _capacity)
            {
                throw new RuntimeContentLimitException(
                    nameof(record),
                    "memory_capacity_exceeded",
                    $"Memory capacity exceeds {_capacity} records.");
            }

            _records[record.MemoryId] = indexed;
        }

        return default;
    }

    public ValueTask<bool> DeleteAsync(
        string memoryId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            throw new ArgumentException("Memory id is required.", nameof(memoryId));
        }

        lock (_sync)
        {
            return new ValueTask<bool>(_records.Remove(memoryId));
        }
    }

    public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        var queryTerms = Tokenize(query.Query);
        IndexedRecord[] snapshot;
        lock (_sync)
        {
            snapshot = _records.Values.ToArray();
        }

        var ranked = new List<MemorySearchResult>();
        foreach (var indexed in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = indexed.Record;
            if (!MemoryQueryFilter.Matches(record, query))
            {
                continue;
            }

            var overlap = queryTerms.Count == 0
                ? 0
                : queryTerms.Count(indexed.Terms.Contains);
            var exactTagMatches = record.Tags.Count(queryTerms.Contains);
            var score = checked(
                overlap * 1_000
                + exactTagMatches * 2_000
                + record.Importance * 10);
            if (queryTerms.Count > 0 && overlap == 0 && exactTagMatches == 0)
            {
                continue;
            }

            ranked.Add(new MemorySearchResult(record, score));
        }

        ranked.Sort(
            (left, right) =>
            {
                var score = right.Score.CompareTo(left.Score);
                if (score != 0)
                {
                    return score;
                }

                var updated = right.Record.UpdatedAt.CompareTo(
                    left.Record.UpdatedAt);
                return updated != 0
                    ? updated
                    : StringComparer.Ordinal.Compare(
                        left.Record.MemoryId,
                        right.Record.MemoryId);
            });

        var selected = new List<MemorySearchResult>();
        var bytes = 0;
        foreach (var result in ranked)
        {
            if (selected.Count >= query.MaxResults)
            {
                break;
            }

            var itemBytes = Encoding.UTF8.GetByteCount(
                result.Record.Content.GetRawText());
            if (checked(bytes + itemBytes) > query.MaxUtf8Bytes)
            {
                continue;
            }

            selected.Add(result);
            bytes += itemBytes;
        }

        return new ValueTask<IReadOnlyList<MemorySearchResult>>(
            new ReadOnlyCollection<MemorySearchResult>(selected));
    }

    private static HashSet<string> Tokenize(JsonElement value)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        Visit(value, terms, depth: 0);
        return terms;
    }

    private static void Visit(
        JsonElement value,
        ISet<string> terms,
        int depth)
    {
        if (depth > 64)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    AddText(property.Name, terms);
                    Visit(property.Value, terms, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    Visit(item, terms, depth + 1);
                }

                break;
            case JsonValueKind.String:
                AddText(value.GetString() ?? string.Empty, terms);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                terms.Add(value.GetRawText().ToLowerInvariant());
                break;
        }
    }

    private static void AddText(string value, ISet<string> terms)
    {
        var word = new StringBuilder();
        var cjkRun = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (IsCjk(character))
            {
                FlushToken(word, terms);
                cjkRun.Append(character);
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                FlushCjkRun(cjkRun, terms);
                word.Append(char.ToLowerInvariant(character));
                continue;
            }

            FlushToken(word, terms);
            FlushCjkRun(cjkRun, terms);
        }

        FlushToken(word, terms);
        FlushCjkRun(cjkRun, terms);
    }

    private static bool IsCjk(char character)
    {
        return character is >= '\u3400' and <= '\u9fff'
            or >= '\uf900' and <= '\ufaff';
    }

    private static void FlushCjkRun(
        StringBuilder run,
        ISet<string> terms)
    {
        if (run.Length == 0)
        {
            return;
        }

        for (var index = 0; index < run.Length; index++)
        {
            terms.Add(run[index].ToString());
            if (index + 1 < run.Length)
            {
                terms.Add(run.ToString(index, 2));
            }
        }

        if (run.Length > 2)
        {
            terms.Add(run.ToString());
        }

        run.Clear();
    }

    private static void FlushToken(StringBuilder token, ISet<string> terms)
    {
        if (token.Length == 0)
        {
            return;
        }

        terms.Add(token.ToString());
        token.Clear();
    }

    private sealed class IndexedRecord
    {
        public IndexedRecord(MemoryRecord record, HashSet<string> terms)
        {
            Record = record;
            Terms = terms;
        }

        public MemoryRecord Record { get; }

        public HashSet<string> Terms { get; }
    }
}

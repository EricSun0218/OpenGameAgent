using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public sealed class GameHistoryLimits
{
    public int MaxSessions { get; set; } = 10_000;
    public int MaxEntriesPerSession { get; set; } = 100_000;
    public int MaxRecordsPerSession { get; set; } = 100_000;
    public int MaxMutationsPerSession { get; set; } = 250_000;
    public int MaxLanesPerSession { get; set; } = 256;
    public int DefaultQueryResults { get; set; } = 100;
    public int MaxQueryResults { get; set; } = 1_000;
    public int MaxSearchResults { get; set; } = 200;
    public int MaxSearchScannedEntries { get; set; } = 100_000;
    public int MaxIdentifierCharacters { get; set; } = 256;
    public int MaxTypeCharacters { get; set; } = 128;
    public int MaxPayloadCharacters { get; set; } = 1_000_000;
    public int MaxFactCharacters { get; set; } = 4_096;
    public int MaxSearchCharacters { get; set; } = 4_096;
    public int MaxContextMessages { get; set; } = 1_024;
    public int MaxContextStateCharacters { get; set; } = 1_000_000;

    internal GameHistoryLimits CopyAndValidate()
    {
        var copy = (GameHistoryLimits)MemberwiseClone();
        Range(copy.MaxSessions, 1, 1_000_000, nameof(MaxSessions));
        Range(copy.MaxEntriesPerSession, 1, 10_000_000, nameof(MaxEntriesPerSession));
        Range(copy.MaxRecordsPerSession, 0, 10_000_000, nameof(MaxRecordsPerSession));
        Range(copy.MaxMutationsPerSession, 1, 20_000_000, nameof(MaxMutationsPerSession));
        Range(copy.MaxLanesPerSession, 1, 100_000, nameof(MaxLanesPerSession));
        Range(copy.DefaultQueryResults, 1, 100_000, nameof(DefaultQueryResults));
        Range(copy.MaxQueryResults, copy.DefaultQueryResults, 1_000_000, nameof(MaxQueryResults));
        Range(copy.MaxSearchResults, 1, 100_000, nameof(MaxSearchResults));
        Range(copy.MaxSearchScannedEntries, 1, 10_000_000, nameof(MaxSearchScannedEntries));
        Range(copy.MaxIdentifierCharacters, 1, 16_384, nameof(MaxIdentifierCharacters));
        Range(copy.MaxTypeCharacters, 1, 16_384, nameof(MaxTypeCharacters));
        Range(copy.MaxPayloadCharacters, 2, 100_000_000, nameof(MaxPayloadCharacters));
        Range(copy.MaxFactCharacters, 1, 10_000_000, nameof(MaxFactCharacters));
        Range(copy.MaxSearchCharacters, 1, 1_000_000, nameof(MaxSearchCharacters));
        Range(copy.MaxContextMessages, 1, 100_000, nameof(MaxContextMessages));
        Range(copy.MaxContextStateCharacters, 2, 100_000_000, nameof(MaxContextStateCharacters));
        return copy;
    }

    private static void Range(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public enum GameHistoryErrorCode
{
    NotFound,
    AlreadyExists,
    InvalidInput,
    InvalidQuery,
    InvalidLane,
    InvalidForkTarget,
    Conflict,
    LimitExceeded,
    CorruptStorage,
    Storage,
}

public class GameHistoryException : Exception
{
    public GameHistoryException(GameHistoryErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public GameHistoryErrorCode Code { get; }
}

public sealed class GameHistoryConcurrencyException : GameHistoryException
{
    public GameHistoryConcurrencyException(long expectedSequence, long actualSequence)
        : base(GameHistoryErrorCode.Conflict, $"History sequence conflict: expected {expectedSequence}, found {actualSequence}.")
    {
        ExpectedSequence = expectedSequence;
        ActualSequence = actualSequence;
    }

    public long ExpectedSequence { get; }
    public long ActualSequence { get; }
}

public sealed class GameHistoryCommitException : GameHistoryException
{
    public GameHistoryCommitException(string mutationId, bool outcomeUnknown, string message, Exception innerException)
        : base(GameHistoryErrorCode.Storage, message, innerException)
    {
        MutationId = mutationId;
        OutcomeUnknown = outcomeUnknown;
    }

    public string MutationId { get; }
    public bool OutcomeUnknown { get; }
}

public sealed class GameHistoryMetadata
{
    public GameHistoryMetadata(
        string id,
        DateTimeOffset createdAt,
        string? parentSessionId = null,
        string? metadataJson = null,
        DateTimeOffset? modifiedAt = null)
    {
        Id = GameHistoryObjectValidation.Required(id, nameof(id));
        CreatedAt = createdAt;
        ParentSessionId = GameHistoryObjectValidation.Optional(parentSessionId, nameof(parentSessionId));
        MetadataJson = metadataJson is null ? null : GameHistoryObjectValidation.JsonObject(metadataJson, nameof(metadataJson));
        ModifiedAt = modifiedAt ?? createdAt;
    }

    public string Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public string? ParentSessionId { get; }
    public string? MetadataJson { get; }
    public DateTimeOffset ModifiedAt { get; }
}

public sealed class GameHistoryEntry
{
    public GameHistoryEntry(
        string id,
        long sequence,
        string? parentId,
        DateTimeOffset timestamp,
        string type,
        string payloadJson)
    {
        Id = GameHistoryObjectValidation.Required(id, nameof(id));
        Sequence = GameHistoryObjectValidation.Sequence(sequence, nameof(sequence));
        ParentId = GameHistoryObjectValidation.Optional(parentId, nameof(parentId));
        Timestamp = timestamp;
        Type = GameHistoryObjectValidation.Required(type, nameof(type));
        PayloadJson = GameHistoryObjectValidation.Json(payloadJson, nameof(payloadJson));
    }

    public string Id { get; }
    public long Sequence { get; }
    public string? ParentId { get; }
    public DateTimeOffset Timestamp { get; }
    public string Type { get; }
    public string PayloadJson { get; }
}

public sealed class GameHistoryRecord
{
    public GameHistoryRecord(
        string id,
        long sequence,
        DateTimeOffset timestamp,
        string lane,
        string type,
        string payloadJson)
    {
        Id = GameHistoryObjectValidation.Required(id, nameof(id));
        Sequence = GameHistoryObjectValidation.Sequence(sequence, nameof(sequence));
        Timestamp = timestamp;
        Lane = GameHistoryObjectValidation.Required(lane, nameof(lane));
        Type = GameHistoryObjectValidation.Required(type, nameof(type));
        PayloadJson = GameHistoryObjectValidation.Json(payloadJson, nameof(payloadJson));
    }

    public string Id { get; }
    public long Sequence { get; }
    public DateTimeOffset Timestamp { get; }
    public string Lane { get; }
    public string Type { get; }
    public string PayloadJson { get; }
}

public sealed class GameHistoryLane
{
    public GameHistoryLane(string name, string? leafEntryId)
    {
        Name = GameHistoryObjectValidation.Required(name, nameof(name));
        LeafEntryId = GameHistoryObjectValidation.Optional(leafEntryId, nameof(leafEntryId));
    }

    public string Name { get; }
    public string? LeafEntryId { get; }
}

public enum GameHistoryMutationKind
{
    Entry,
    Record,
    Lane,
    Name,
    Label,
}

public sealed class GameHistoryLogItem
{
    public GameHistoryLogItem(
        string mutationId,
        long sequence,
        GameHistoryMutationKind kind,
        GameHistoryEntry? entry = null,
        GameHistoryRecord? record = null,
        string? lane = null,
        string? leafEntryId = null,
        bool? createsLane = null,
        string? name = null,
        string? targetEntryId = null,
        string? label = null)
    {
        MutationId = GameHistoryObjectValidation.Required(mutationId, nameof(mutationId));
        Sequence = GameHistoryObjectValidation.Sequence(sequence, nameof(sequence));
        if (!Enum.IsDefined(typeof(GameHistoryMutationKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var valid = kind switch
        {
            GameHistoryMutationKind.Entry => entry is not null
                && entry.Sequence == sequence
                && record is null
                && createsLane is null
                && name is null
                && targetEntryId is null
                && label is null,
            GameHistoryMutationKind.Record => entry is null
                && record is not null
                && record.Sequence == sequence
                && string.Equals(lane, record.Lane, StringComparison.Ordinal)
                && leafEntryId is null
                && createsLane is null
                && name is null
                && targetEntryId is null
                && label is null,
            GameHistoryMutationKind.Lane => entry is null
                && record is null
                && lane is not null
                && createsLane is not null
                && name is null
                && targetEntryId is null
                && label is null,
            GameHistoryMutationKind.Name => entry is null
                && record is null
                && lane is null
                && leafEntryId is null
                && createsLane is null
                && name is not null
                && targetEntryId is null
                && label is null,
            GameHistoryMutationKind.Label => entry is null
                && record is null
                && lane is null
                && leafEntryId is null
                && createsLane is null
                && name is null
                && targetEntryId is not null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("The history log fields do not match its mutation kind.", nameof(kind));
        }

        Kind = kind;
        Entry = entry;
        Record = record;
        Lane = GameHistoryObjectValidation.Optional(lane, nameof(lane));
        LeafEntryId = GameHistoryObjectValidation.Optional(leafEntryId, nameof(leafEntryId));
        CreatesLane = createsLane;
        Name = GameHistoryObjectValidation.Optional(name, nameof(name));
        TargetEntryId = GameHistoryObjectValidation.Optional(targetEntryId, nameof(targetEntryId));
        Label = GameHistoryObjectValidation.Optional(label, nameof(label));
    }

    public string MutationId { get; }
    public long Sequence { get; }
    public GameHistoryMutationKind Kind { get; }
    public GameHistoryEntry? Entry { get; }
    public GameHistoryRecord? Record { get; }
    public string? Lane { get; }
    public string? LeafEntryId { get; }
    public bool? CreatesLane { get; }
    public string? Name { get; }
    public string? TargetEntryId { get; }
    public string? Label { get; }
}

public sealed class GameHistoryStats
{
    public GameHistoryStats(long entryCount, long recordCount, int laneCount, long mutationCount, long lastSequence)
    {
        if (entryCount < 0 || recordCount < 0 || laneCount < 1 || mutationCount < 0 || lastSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entryCount));
        }

        EntryCount = entryCount;
        RecordCount = recordCount;
        LaneCount = laneCount;
        MutationCount = mutationCount;
        LastSequence = lastSequence;
    }

    public long EntryCount { get; }
    public long RecordCount { get; }
    public int LaneCount { get; }
    public long MutationCount { get; }
    public long LastSequence { get; }
}

public sealed class GameHistoryPage<T>
{
    public GameHistoryPage(IEnumerable<T> items, long? nextSequence)
    {
        if (nextSequence is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(nextSequence));
        }

        var copied = (items ?? throw new ArgumentNullException(nameof(items))).ToArray();
        if (copied.Any(item => item is null))
        {
            throw new ArgumentException("A history page cannot contain null items.", nameof(items));
        }

        Items = Array.AsReadOnly(copied);
        NextSequence = nextSequence;
    }

    public IReadOnlyList<T> Items { get; }
    public long? NextSequence { get; }
}

public enum GameHistoryOrder
{
    NewestFirst,
    OldestFirst,
}

public sealed class GameHistoryEntryQuery
{
    public string? Type { get; set; }
    public GameHistoryOrder Order { get; set; } = GameHistoryOrder.NewestFirst;
    public int? Limit { get; set; }
    public long? CursorSequence { get; set; }
}

public sealed class GameHistoryBranchQuery
{
    public string? StartEntryId { get; set; }
    public string? StopAtEntryId { get; set; }
    public string? StopAtType { get; set; }
    public string? Type { get; set; }
    public GameHistoryOrder Order { get; set; } = GameHistoryOrder.NewestFirst;
    public int? Limit { get; set; }
    public long? CursorSequence { get; set; }
}

public sealed class GameHistoryRecordQuery
{
    public string? Lane { get; set; }
    public string? Type { get; set; }
    public GameHistoryOrder Order { get; set; } = GameHistoryOrder.NewestFirst;
    public int? Limit { get; set; }
    public long? CursorSequence { get; set; }
}

public sealed class GameHistoryLogQuery
{
    public long AfterSequence { get; set; }
    public int? Limit { get; set; }
}

public sealed class GameHistoryCreateOptions
{
    public string? Id { get; set; }
    public string? ParentSessionId { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class GameHistoryListQuery
{
    public int? Limit { get; set; }
    public string? AfterSessionId { get; set; }
}

public sealed class GameHistoryListPage
{
    public GameHistoryListPage(IEnumerable<GameHistoryMetadata> sessions, string? nextSessionId)
    {
        var copied = (sessions ?? throw new ArgumentNullException(nameof(sessions))).ToArray();
        if (copied.Any(session => session is null))
        {
            throw new ArgumentException("A history list cannot contain null sessions.", nameof(sessions));
        }

        Sessions = Array.AsReadOnly(copied);
        NextSessionId = nextSessionId;
    }

    public IReadOnlyList<GameHistoryMetadata> Sessions { get; }
    public string? NextSessionId { get; }
}

public enum GameHistoryForkScope
{
    Branch,
    Tree,
}

public enum GameHistoryForkPosition
{
    Before,
    At,
}

public sealed class GameHistoryForkOptions
{
    public string? Id { get; set; }
    public GameHistoryForkScope Scope { get; set; } = GameHistoryForkScope.Branch;
    public string? EntryId { get; set; }
    public GameHistoryForkPosition Position { get; set; } = GameHistoryForkPosition.At;
    public string? ParentSessionId { get; set; }
    public string? MetadataJson { get; set; }
    public long? ExpectedSourceSequence { get; set; }
}

public sealed class GameHistorySearchCursor
{
    public GameHistorySearchCursor(string sessionId, long entrySequence)
    {
        SessionId = GameHistoryObjectValidation.Required(sessionId, nameof(sessionId));
        EntrySequence = GameHistoryObjectValidation.Sequence(entrySequence, nameof(entrySequence));
    }

    public string SessionId { get; }
    public long EntrySequence { get; }
}

public sealed class GameHistorySearchQuery
{
    public GameHistorySearchQuery(string text)
    {
        Text = GameHistoryObjectValidation.Required(text, nameof(text));
    }

    public string Text { get; }
    public string? SessionId { get; set; }
    public string? EntryType { get; set; }
    public int? Limit { get; set; }
    public GameHistorySearchCursor? Cursor { get; set; }
}

public sealed class GameHistorySearchHit
{
    public GameHistorySearchHit(GameHistoryMetadata session, GameHistoryEntry entry, string snippet)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Snippet = snippet ?? throw new ArgumentNullException(nameof(snippet));
    }

    public GameHistoryMetadata Session { get; }
    public GameHistoryEntry Entry { get; }
    public string Snippet { get; }
}

public sealed class GameHistorySearchPage
{
    public GameHistorySearchPage(IEnumerable<GameHistorySearchHit> hits, GameHistorySearchCursor? nextCursor)
    {
        var copied = (hits ?? throw new ArgumentNullException(nameof(hits))).ToArray();
        if (copied.Any(hit => hit is null))
        {
            throw new ArgumentException("A search page cannot contain null hits.", nameof(hits));
        }

        Hits = Array.AsReadOnly(copied);
        NextCursor = nextCursor;
    }

    public IReadOnlyList<GameHistorySearchHit> Hits { get; }
    public GameHistorySearchCursor? NextCursor { get; }
}

public sealed class GameHistoryCommit
{
    public GameHistoryCommit(string mutationId, long sequence, bool replayed)
    {
        MutationId = GameHistoryObjectValidation.Required(mutationId, nameof(mutationId));
        Sequence = GameHistoryObjectValidation.Sequence(sequence, nameof(sequence));
        Replayed = replayed;
    }

    public string MutationId { get; }
    public long Sequence { get; }
    public bool Replayed { get; }
}

public sealed class GameHistoryEntryCommit
{
    public GameHistoryEntryCommit(GameHistoryEntry entry, GameHistoryCommit commit)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Commit = commit ?? throw new ArgumentNullException(nameof(commit));
    }

    public GameHistoryEntry Entry { get; }
    public GameHistoryCommit Commit { get; }
}

public sealed class GameHistoryRecordCommit
{
    public GameHistoryRecordCommit(GameHistoryRecord record, GameHistoryCommit commit)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Commit = commit ?? throw new ArgumentNullException(nameof(commit));
    }

    public GameHistoryRecord Record { get; }
    public GameHistoryCommit Commit { get; }
}

public sealed class GameHistoryContextProjection
{
    public GameHistoryContextProjection(IEnumerable<AgentMessage> messages, string? stateJson = null)
    {
        var copied = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();
        if (copied.Any(message => message is null))
        {
            throw new ArgumentException("A context projection cannot contain null messages.", nameof(messages));
        }

        Messages = Array.AsReadOnly(copied);
        StateJson = stateJson is null ? null : GameHistoryObjectValidation.Json(stateJson, nameof(stateJson));
    }

    public IReadOnlyList<AgentMessage> Messages { get; }
    public string? StateJson { get; }
}

public sealed class GameHistoryContextOptions
{
    public Func<IReadOnlyList<GameHistoryEntry>, IReadOnlyList<GameHistoryEntry>>? EntryTransform { get; set; }
    public Func<GameHistoryEntry, IReadOnlyList<AgentMessage>>? EntryProjector { get; set; }
    public Func<IReadOnlyList<GameHistoryEntry>, string?>? StateProjector { get; set; }
    public int? MaxMessages { get; set; }
    public TimeSpan CallbackTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public static class GameHistoryContextTransforms
{
    public static Func<IReadOnlyList<GameHistoryEntry>, IReadOnlyList<GameHistoryEntry>> AfterLatest(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("An entry type is required.", nameof(type));
        }

        return entries =>
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                if (string.Equals(entries[index].Type, type, StringComparison.Ordinal))
                {
                    return Array.AsReadOnly(entries.Skip(index).ToArray());
                }
            }

            return Array.AsReadOnly(entries.ToArray());
        };
    }
}

public interface IGameSessionHistoryRepository
{
    Task<GameSessionHistory> CreateAsync(GameHistoryCreateOptions? options = null, CancellationToken cancellationToken = default);
    Task<GameSessionHistory> OpenAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<GameHistoryListPage> ListAsync(GameHistoryListQuery? query = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<GameSessionHistory> ForkAsync(string sourceSessionId, GameHistoryForkOptions? options = null, CancellationToken cancellationToken = default);
    Task<GameHistorySearchPage> SearchAsync(GameHistorySearchQuery query, CancellationToken cancellationToken = default);
}

public interface IGameSessionHistoryStorage
{
    Task<GameHistoryMetadata> GetMetadataAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<GameHistoryLane>> GetLanesAsync(CancellationToken cancellationToken);
    Task<GameHistoryEntry?> GetEntryAsync(string id, CancellationToken cancellationToken);
    Task<GameHistoryPage<GameHistoryEntry>> FindEntriesAsync(GameHistoryEntryQuery query, CancellationToken cancellationToken);
    Task<GameHistoryPage<GameHistoryEntry>> FindBranchAsync(string lane, GameHistoryBranchQuery query, CancellationToken cancellationToken);
    Task<GameHistoryPage<GameHistoryRecord>> FindRecordsAsync(GameHistoryRecordQuery query, CancellationToken cancellationToken);
    Task<GameHistoryPage<GameHistoryLogItem>> GetLogAsync(GameHistoryLogQuery query, CancellationToken cancellationToken);
    Task<string?> GetNameAsync(CancellationToken cancellationToken);
    Task<string?> GetLabelAsync(string entryId, CancellationToken cancellationToken);
    Task<GameHistoryStats> GetStatsAsync(CancellationToken cancellationToken);
    Task<GameHistoryEntryCommit> AppendEntryAsync(string lane, string id, string type, string payloadJson, string mutationId, long? expectedSequence, CancellationToken cancellationToken);
    Task<GameHistoryRecordCommit> AppendRecordAsync(string lane, string id, string type, string payloadJson, string mutationId, long? expectedSequence, CancellationToken cancellationToken);
    Task<GameHistoryCommit> CreateLaneAsync(string lane, string? atEntryId, string mutationId, long? expectedSequence, CancellationToken cancellationToken);
    Task<GameHistoryCommit> MoveLaneAsync(string lane, string? toEntryId, string mutationId, long? expectedSequence, CancellationToken cancellationToken);
    Task<GameHistoryCommit> SetNameAsync(string name, string mutationId, long? expectedSequence, CancellationToken cancellationToken);
    Task<GameHistoryCommit> SetLabelAsync(string entryId, string? label, string mutationId, long? expectedSequence, CancellationToken cancellationToken);
}

public sealed class GameSessionHistory
{
    private readonly IGameSessionHistoryStorage _storage;
    private readonly GameHistoryLimits _limits;
    private readonly string _lane;

    public GameSessionHistory(IGameSessionHistoryStorage storage, GameHistoryLimits? limits = null)
        : this(storage, limits, "main")
    {
    }

    private GameSessionHistory(IGameSessionHistoryStorage storage, GameHistoryLimits? limits, string lane)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _limits = (limits ?? new GameHistoryLimits()).CopyAndValidate();
        GameHistoryValidation.Identifier(lane, nameof(lane), _limits);
        _lane = lane;
    }

    public GameSessionHistory View(string lane)
    {
        GameHistoryValidation.Identifier(lane, nameof(lane), _limits);
        return new GameSessionHistory(_storage, _limits, lane);
    }

    public async Task<GameHistoryMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await _storage.GetMetadataAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new GameHistoryException(GameHistoryErrorCode.Storage, "The history storage returned null metadata.");
        GameHistoryValidation.SessionId(metadata.Id, nameof(metadata.Id), _limits);
        GameHistoryValidation.OptionalIdentifier(metadata.ParentSessionId, nameof(metadata.ParentSessionId), _limits);
        if (metadata.MetadataJson is not null)
        {
            GameHistoryValidation.JsonObject(metadata.MetadataJson, nameof(metadata.MetadataJson), _limits.MaxPayloadCharacters);
        }

        return metadata;
    }

    public async Task<IReadOnlyList<GameHistoryLane>> GetLanesAsync(CancellationToken cancellationToken = default)
    {
        var lanes = await _storage.GetLanesAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new GameHistoryException(GameHistoryErrorCode.Storage, "The history storage returned null lanes.");
        if (lanes.Count < 1 || lanes.Count > _limits.MaxLanesPerSession)
        {
            throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history storage returned an invalid lane count.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lanes.Count; index++)
        {
            var lane = lanes[index] ?? throw new GameHistoryException(GameHistoryErrorCode.Storage, "The history storage returned a null lane.");
            GameHistoryValidation.Identifier(lane.Name, nameof(lane.Name), _limits);
            GameHistoryValidation.OptionalIdentifier(lane.LeafEntryId, nameof(lane.LeafEntryId), _limits);
            if (!names.Add(lane.Name))
            {
                throw new GameHistoryException(GameHistoryErrorCode.CorruptStorage, $"The history storage returned duplicate lane {lane.Name}.");
            }
        }

        return lanes;
    }

    public async Task<string?> GetLeafEntryIdAsync(CancellationToken cancellationToken = default)
    {
        var lanes = await GetLanesAsync(cancellationToken).ConfigureAwait(false);
        var lane = lanes.SingleOrDefault(candidate => string.Equals(candidate.Name, _lane, StringComparison.Ordinal));
        if (lane is null)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidLane, $"History lane not found: {_lane}.");
        }

        return lane.LeafEntryId;
    }

    public Task<GameHistoryEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.Identifier(id, nameof(id), _limits);
        return _storage.GetEntryAsync(id, cancellationToken);
    }

    public async Task<GameHistoryPage<GameHistoryEntry>> FindEntriesAsync(
        GameHistoryEntryQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new GameHistoryEntryQuery();
        GameHistoryValidation.EntryQuery(query, _limits);
        return await BoundedPageAsync(
            _storage.FindEntriesAsync(query, cancellationToken),
            query.Limit ?? _limits.DefaultQueryResults).ConfigureAwait(false);
    }

    public async Task<GameHistoryPage<GameHistoryEntry>> FindBranchAsync(
        string? lane = null,
        GameHistoryBranchQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        lane ??= _lane;
        GameHistoryValidation.Identifier(lane, nameof(lane), _limits);
        query ??= new GameHistoryBranchQuery();
        GameHistoryValidation.BranchQuery(query, _limits);
        return await BoundedPageAsync(
            _storage.FindBranchAsync(lane, query, cancellationToken),
            query.Limit ?? _limits.DefaultQueryResults).ConfigureAwait(false);
    }

    public async Task<GameHistoryPage<GameHistoryRecord>> FindRecordsAsync(
        GameHistoryRecordQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new GameHistoryRecordQuery();
        GameHistoryValidation.RecordQuery(query, _limits);
        return await BoundedPageAsync(
            _storage.FindRecordsAsync(query, cancellationToken),
            query.Limit ?? _limits.DefaultQueryResults).ConfigureAwait(false);
    }

    public async Task<GameHistoryPage<GameHistoryLogItem>> GetLogAsync(
        GameHistoryLogQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new GameHistoryLogQuery();
        GameHistoryValidation.LogQuery(query, _limits);
        return await BoundedPageAsync(
            _storage.GetLogAsync(query, cancellationToken),
            query.Limit ?? _limits.DefaultQueryResults).ConfigureAwait(false);
    }

    public async Task<string?> GetNameAsync(CancellationToken cancellationToken = default)
    {
        var name = await _storage.GetNameAsync(cancellationToken).ConfigureAwait(false);
        if (name is not null)
        {
            GameHistoryValidation.Fact(name, nameof(name), _limits);
        }

        return name;
    }

    public async Task<string?> GetLabelAsync(string entryId, CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.Identifier(entryId, nameof(entryId), _limits);
        var label = await _storage.GetLabelAsync(entryId, cancellationToken).ConfigureAwait(false);
        if (label is not null)
        {
            GameHistoryValidation.Fact(label, nameof(label), _limits);
        }

        return label;
    }

    public async Task<GameHistoryStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = await _storage.GetStatsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new GameHistoryException(GameHistoryErrorCode.Storage, "The history storage returned null statistics.");
        if (stats.EntryCount > _limits.MaxEntriesPerSession
            || stats.RecordCount > _limits.MaxRecordsPerSession
            || stats.LaneCount > _limits.MaxLanesPerSession
            || stats.MutationCount > _limits.MaxMutationsPerSession
            || stats.LastSequence != stats.MutationCount)
        {
            throw new GameHistoryException(GameHistoryErrorCode.CorruptStorage, "The history storage returned inconsistent statistics.");
        }

        return stats;
    }

    public Task<GameHistoryEntryCommit> AppendEntryAsync(
        string id,
        string type,
        string payloadJson,
        string? lane = null,
        string? mutationId = null,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default)
    {
        lane ??= _lane;
        GameHistoryValidation.Identifier(lane, nameof(lane), _limits);
        GameHistoryValidation.Identifier(id, nameof(id), _limits);
        GameHistoryValidation.Type(type, nameof(type), _limits);
        GameHistoryValidation.Json(payloadJson, nameof(payloadJson), _limits.MaxPayloadCharacters);
        var operation = GameHistoryValidation.MutationId(mutationId, _limits);
        GameHistoryValidation.ExpectedSequence(expectedSequence);
        return _storage.AppendEntryAsync(lane, id, type, payloadJson, operation, expectedSequence, cancellationToken);
    }

    public Task<GameHistoryRecordCommit> AppendRecordAsync(
        string id,
        string type,
        string payloadJson,
        string? lane = null,
        string? mutationId = null,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default)
    {
        lane ??= _lane;
        GameHistoryValidation.Identifier(lane, nameof(lane), _limits);
        GameHistoryValidation.Identifier(id, nameof(id), _limits);
        GameHistoryValidation.Type(type, nameof(type), _limits);
        GameHistoryValidation.Json(payloadJson, nameof(payloadJson), _limits.MaxPayloadCharacters);
        var operation = GameHistoryValidation.MutationId(mutationId, _limits);
        GameHistoryValidation.ExpectedSequence(expectedSequence);
        return _storage.AppendRecordAsync(lane, id, type, payloadJson, operation, expectedSequence, cancellationToken);
    }

    public Task<GameHistoryCommit> CreateLaneAsync(
        string lane,
        string? atEntryId = null,
        string? mutationId = null,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.Identifier(lane, nameof(lane), _limits);
        GameHistoryValidation.OptionalIdentifier(atEntryId, nameof(atEntryId), _limits);
        GameHistoryValidation.ExpectedSequence(expectedSequence);
        return _storage.CreateLaneAsync(lane, atEntryId, GameHistoryValidation.MutationId(mutationId, _limits), expectedSequence, cancellationToken);
    }

    public Task<GameHistoryCommit> MoveLaneAsync(
        string lane,
        string? toEntryId,
        string? mutationId = null,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.Identifier(lane, nameof(lane), _limits);
        GameHistoryValidation.OptionalIdentifier(toEntryId, nameof(toEntryId), _limits);
        GameHistoryValidation.ExpectedSequence(expectedSequence);
        return _storage.MoveLaneAsync(lane, toEntryId, GameHistoryValidation.MutationId(mutationId, _limits), expectedSequence, cancellationToken);
    }

    public Task<GameHistoryCommit> SetNameAsync(
        string name,
        string? mutationId = null,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.Fact(name, nameof(name), _limits);
        GameHistoryValidation.ExpectedSequence(expectedSequence);
        return _storage.SetNameAsync(name, GameHistoryValidation.MutationId(mutationId, _limits), expectedSequence, cancellationToken);
    }

    public Task<GameHistoryCommit> SetLabelAsync(
        string entryId,
        string? label,
        string? mutationId = null,
        long? expectedSequence = null,
        CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.Identifier(entryId, nameof(entryId), _limits);
        if (label is not null)
        {
            GameHistoryValidation.Fact(label, nameof(label), _limits);
        }

        GameHistoryValidation.ExpectedSequence(expectedSequence);
        return _storage.SetLabelAsync(entryId, label, GameHistoryValidation.MutationId(mutationId, _limits), expectedSequence, cancellationToken);
    }

    public async Task<GameHistoryContextProjection> BuildContextAsync(
        string lane,
        GameHistoryContextOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        GameHistoryValidation.Identifier(lane, nameof(lane), _limits);
        var maxMessages = options.MaxMessages ?? _limits.MaxContextMessages;
        if (maxMessages < 1 || maxMessages > _limits.MaxContextMessages)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The context message limit is invalid.");
        }

        if (options.CallbackTimeout < TimeSpan.FromMilliseconds(1)
            || options.CallbackTimeout > TimeSpan.FromMinutes(5))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The context callback timeout is invalid.");
        }

        var page = await _storage.FindBranchAsync(
            lane,
            new GameHistoryBranchQuery { Order = GameHistoryOrder.OldestFirst, Limit = _limits.MaxQueryResults },
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GameHistoryEntry> entries = page.Items;
        if (entries.Count > _limits.MaxQueryResults || page.NextSequence is not null)
        {
            throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The branch is too large for a single context projection.");
        }

        if (options.EntryTransform is not null)
        {
            entries = await InvokeCallbackAsync(
                () => options.EntryTransform(entries),
                options.CallbackTimeout,
                cancellationToken).ConfigureAwait(false) ?? throw new GameHistoryException(
                GameHistoryErrorCode.InvalidInput,
                "The context transform returned null.");
            if (entries.Count > _limits.MaxQueryResults)
            {
                throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The context transform returned too many entries.");
            }

            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index] is null)
                {
                    throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, "The context transform returned a null entry.");
                }
            }
        }

        var messages = new List<AgentMessage>();
        if (options.EntryProjector is not null)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projected = await InvokeCallbackAsync(
                    () => options.EntryProjector(entry),
                    options.CallbackTimeout,
                    cancellationToken).ConfigureAwait(false) ?? throw new GameHistoryException(
                    GameHistoryErrorCode.InvalidInput,
                    "The context projector returned null.");
                if (projected.Count > maxMessages - messages.Count)
                {
                    throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The context contains too many messages.");
                }

                for (var index = 0; index < projected.Count; index++)
                {
                    var message = projected[index];
                    if (message is null)
                    {
                        throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, "The context projector returned a null message.");
                    }

                    messages.Add(message);
                }
            }
        }

        var stateJson = options.StateProjector is null
            ? null
            : await InvokeCallbackAsync(
                () => options.StateProjector(entries),
                options.CallbackTimeout,
                cancellationToken).ConfigureAwait(false);
        if (stateJson is not null)
        {
            GameHistoryValidation.Json(stateJson, nameof(stateJson), _limits.MaxContextStateCharacters);
        }

        return new GameHistoryContextProjection(messages, stateJson);
    }

    public Task<GameHistoryContextProjection> BuildContextAsync(
        GameHistoryContextOptions options,
        CancellationToken cancellationToken = default) => BuildContextAsync(_lane, options, cancellationToken);

    private static async Task<T> InvokeCallbackAsync<T>(
        Func<T> callback,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var callbackTask = Task.Run(callback);
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(callbackTask, timeoutTask).ConfigureAwait(false);
        if (completed == callbackTask)
        {
            return await callbackTask.ConfigureAwait(false);
        }

        _ = callbackTask.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        cancellationToken.ThrowIfCancellationRequested();
        throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "A context callback exceeded its timeout.");
    }

    private static async Task<GameHistoryPage<T>> BoundedPageAsync<T>(Task<GameHistoryPage<T>> operation, int limit)
    {
        var page = await operation.ConfigureAwait(false)
            ?? throw new GameHistoryException(GameHistoryErrorCode.Storage, "The history storage returned a null page.");
        if (page.Items.Count > limit)
        {
            throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history storage exceeded the requested page size.");
        }

        return page;
    }
}

public sealed class InMemoryGameSessionHistoryRepository : IGameSessionHistoryRepository
{
    private readonly object _sync = new();
    private readonly Dictionary<string, InMemoryGameSessionHistoryStorage> _sessions = new(StringComparer.Ordinal);
    private readonly GameHistoryLimits _limits;

    public InMemoryGameSessionHistoryRepository(GameHistoryLimits? limits = null)
    {
        _limits = (limits ?? new GameHistoryLimits()).CopyAndValidate();
    }

    public Task<GameSessionHistory> CreateAsync(GameHistoryCreateOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new GameHistoryCreateOptions();
        var id = options.Id ?? Guid.NewGuid().ToString("N");
        GameHistoryValidation.SessionId(id, nameof(options.Id), _limits);
        GameHistoryValidation.OptionalIdentifier(options.ParentSessionId, nameof(options.ParentSessionId), _limits);
        if (options.MetadataJson is not null)
        {
            GameHistoryValidation.JsonObject(options.MetadataJson, nameof(options.MetadataJson), _limits.MaxPayloadCharacters);
        }

        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sessions.ContainsKey(id))
            {
                throw new GameHistoryException(GameHistoryErrorCode.AlreadyExists, $"History session already exists: {id}.");
            }

            if (_sessions.Count >= _limits.MaxSessions)
            {
                throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history repository is full.");
            }

            var now = DateTimeOffset.UtcNow;
            var storage = new InMemoryGameSessionHistoryStorage(
                new GameHistoryMetadata(id, now, options.ParentSessionId, options.MetadataJson, now),
                _limits);
            _sessions.Add(id, storage);
            return Task.FromResult(new GameSessionHistory(storage, _limits));
        }
    }

    public Task<GameSessionHistory> OpenAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.SessionId(sessionId, nameof(sessionId), _limits);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var storage))
            {
                throw new GameHistoryException(GameHistoryErrorCode.NotFound, $"History session not found: {sessionId}.");
            }

            return Task.FromResult(new GameSessionHistory(storage, _limits));
        }
    }

    public Task<GameHistoryListPage> ListAsync(GameHistoryListQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new GameHistoryListQuery();
        var limit = GameHistoryValidation.Limit(query.Limit, _limits);
        if (query.AfterSessionId is not null)
        {
            GameHistoryValidation.SessionId(query.AfterSessionId, nameof(query.AfterSessionId), _limits);
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var all = _sessions.Values
                .Select(storage => storage.Metadata)
                .OrderByDescending(metadata => metadata.ModifiedAt)
                .ThenBy(metadata => metadata.Id, StringComparer.Ordinal)
                .ToArray();
            var start = CursorStart(all, query.AfterSessionId);
            var items = all.Skip(start).Take(limit).ToArray();
            var next = start + items.Length < all.Length ? items.LastOrDefault()?.Id : null;
            return Task.FromResult(new GameHistoryListPage(items, next));
        }
    }

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.SessionId(sessionId, nameof(sessionId), _limits);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _sessions.Remove(sessionId);
            return Task.CompletedTask;
        }
    }

    public Task<GameSessionHistory> ForkAsync(
        string sourceSessionId,
        GameHistoryForkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.SessionId(sourceSessionId, nameof(sourceSessionId), _limits);
        options ??= new GameHistoryForkOptions();
        GameHistoryValidation.Fork(options, _limits);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sourceSessionId, out var source))
            {
                throw new GameHistoryException(GameHistoryErrorCode.NotFound, $"History session not found: {sourceSessionId}.");
            }

            var id = options.Id ?? Guid.NewGuid().ToString("N");
            GameHistoryValidation.SessionId(id, nameof(options.Id), _limits);
            if (_sessions.ContainsKey(id))
            {
                throw new GameHistoryException(GameHistoryErrorCode.AlreadyExists, $"History session already exists: {id}.");
            }

            if (_sessions.Count >= _limits.MaxSessions)
            {
                throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history repository is full.");
            }

            var state = source.CopyForFork(options);
            var now = DateTimeOffset.UtcNow;
            var metadata = new GameHistoryMetadata(
                id,
                now,
                options.ParentSessionId ?? sourceSessionId,
                options.MetadataJson,
                now);
            var target = new InMemoryGameSessionHistoryStorage(metadata, _limits, state);
            _sessions.Add(id, target);
            return Task.FromResult(new GameSessionHistory(target, _limits));
        }
    }

    public async Task<GameHistorySearchPage> SearchAsync(
        GameHistorySearchQuery query,
        CancellationToken cancellationToken = default)
    {
        GameHistoryValidation.Search(query, _limits);
        var limit = query.Limit ?? Math.Min(_limits.DefaultQueryResults, _limits.MaxSearchResults);
        GameHistoryMetadata[] sessions;
        lock (_sync)
        {
            sessions = _sessions.Values.Select(storage => storage.Metadata)
                .Where(metadata => query.SessionId is null || string.Equals(metadata.Id, query.SessionId, StringComparison.Ordinal))
                .OrderBy(metadata => metadata.Id, StringComparer.Ordinal)
                .ToArray();
        }

        var hits = new List<GameHistorySearchHit>();
        var scannedEntries = 0;
        var cursorPassed = query.Cursor is null;
        foreach (var metadata in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = await OpenAsync(metadata.Id, cancellationToken).ConfigureAwait(false);
            long? cursor = null;
            while (true)
            {
                var page = await history.FindEntriesAsync(
                    new GameHistoryEntryQuery
                    {
                        Type = query.EntryType,
                        Order = GameHistoryOrder.OldestFirst,
                        Limit = _limits.MaxQueryResults,
                        CursorSequence = cursor,
                    },
                    cancellationToken).ConfigureAwait(false);
                foreach (var entry in page.Items)
                {
                    if (++scannedEntries > _limits.MaxSearchScannedEntries)
                    {
                        throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The search scan limit was exceeded.");
                    }

                    if (!cursorPassed)
                    {
                        cursorPassed = string.CompareOrdinal(metadata.Id, query.Cursor!.SessionId) > 0
                            || (string.Equals(metadata.Id, query.Cursor.SessionId, StringComparison.Ordinal)
                                && entry.Sequence > query.Cursor.EntrySequence);
                        if (!cursorPassed)
                        {
                            continue;
                        }
                    }

                    if (!entry.Type.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                        && !entry.PayloadJson.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var snippet = entry.PayloadJson.Length <= 512 ? entry.PayloadJson : entry.PayloadJson.Substring(0, 512);
                    hits.Add(new GameHistorySearchHit(metadata, entry, snippet));
                    if (hits.Count == limit)
                    {
                        return new GameHistorySearchPage(hits, new GameHistorySearchCursor(metadata.Id, entry.Sequence));
                    }
                }

                if (page.NextSequence is null)
                {
                    break;
                }

                cursor = page.NextSequence;
            }
        }

        return new GameHistorySearchPage(hits, null);
    }

    private static int CursorStart(IReadOnlyList<GameHistoryMetadata> values, string? afterId)
    {
        if (afterId is null)
        {
            return 0;
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index].Id, afterId, StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The list cursor does not identify a visible session.");
    }
}

internal sealed class InMemoryGameSessionHistoryStorage : IGameSessionHistoryStorage
{
    private readonly object _sync = new();
    private readonly GameHistoryLimits _limits;
    private GameHistoryMetadata _metadata;
    private readonly GameHistoryState _state;

    internal InMemoryGameSessionHistoryStorage(
        GameHistoryMetadata metadata,
        GameHistoryLimits limits,
        GameHistoryState? state = null)
    {
        _metadata = metadata;
        _limits = limits;
        _state = state ?? new GameHistoryState(limits);
    }

    internal GameHistoryMetadata Metadata
    {
        get
        {
            lock (_sync)
            {
                return _metadata;
            }
        }
    }

    internal GameHistoryState CopyForFork(GameHistoryForkOptions options)
    {
        lock (_sync)
        {
            if (options.ExpectedSourceSequence is { } expected && expected != _state.Sequence)
            {
                throw new GameHistoryConcurrencyException(expected, _state.Sequence);
            }

            return _state.CopyForFork(options);
        }
    }

    public Task<GameHistoryMetadata> GetMetadataAsync(CancellationToken cancellationToken) =>
        Read(state => _metadata, cancellationToken);

    public Task<IReadOnlyList<GameHistoryLane>> GetLanesAsync(CancellationToken cancellationToken) =>
        Read(state => state.GetLanes(), cancellationToken);

    public Task<GameHistoryEntry?> GetEntryAsync(string id, CancellationToken cancellationToken) =>
        Read(state => state.GetEntry(id), cancellationToken);

    public Task<GameHistoryPage<GameHistoryEntry>> FindEntriesAsync(GameHistoryEntryQuery query, CancellationToken cancellationToken) =>
        Read(state => state.FindEntries(query), cancellationToken);

    public Task<GameHistoryPage<GameHistoryEntry>> FindBranchAsync(string lane, GameHistoryBranchQuery query, CancellationToken cancellationToken) =>
        Read(state => state.FindBranch(lane, query), cancellationToken);

    public Task<GameHistoryPage<GameHistoryRecord>> FindRecordsAsync(GameHistoryRecordQuery query, CancellationToken cancellationToken) =>
        Read(state => state.FindRecords(query), cancellationToken);

    public Task<GameHistoryPage<GameHistoryLogItem>> GetLogAsync(GameHistoryLogQuery query, CancellationToken cancellationToken) =>
        Read(state => state.GetLog(query), cancellationToken);

    public Task<string?> GetNameAsync(CancellationToken cancellationToken) => Read(state => state.Name, cancellationToken);

    public Task<string?> GetLabelAsync(string entryId, CancellationToken cancellationToken) =>
        Read(state => state.GetLabel(entryId), cancellationToken);

    public Task<GameHistoryStats> GetStatsAsync(CancellationToken cancellationToken) =>
        Read(state => state.GetStats(), cancellationToken);

    public Task<GameHistoryEntryCommit> AppendEntryAsync(string lane, string id, string type, string payloadJson, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        Write(state => state.AppendEntry(lane, id, type, payloadJson, mutationId, expectedSequence, DateTimeOffset.UtcNow), cancellationToken);

    public Task<GameHistoryRecordCommit> AppendRecordAsync(string lane, string id, string type, string payloadJson, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        Write(state => state.AppendRecord(lane, id, type, payloadJson, mutationId, expectedSequence, DateTimeOffset.UtcNow), cancellationToken);

    public Task<GameHistoryCommit> CreateLaneAsync(string lane, string? atEntryId, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        Write(state => state.CreateLane(lane, atEntryId, mutationId, expectedSequence), cancellationToken);

    public Task<GameHistoryCommit> MoveLaneAsync(string lane, string? toEntryId, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        Write(state => state.MoveLane(lane, toEntryId, mutationId, expectedSequence), cancellationToken);

    public Task<GameHistoryCommit> SetNameAsync(string name, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        Write(state => state.SetName(name, mutationId, expectedSequence), cancellationToken);

    public Task<GameHistoryCommit> SetLabelAsync(string entryId, string? label, string mutationId, long? expectedSequence, CancellationToken cancellationToken) =>
        Write(state => state.SetLabel(entryId, label, mutationId, expectedSequence), cancellationToken);

    private Task<T> Read<T>(Func<GameHistoryState, T> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(read(_state));
        }
    }

    private Task<T> Write<T>(Func<GameHistoryState, T> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = write(_state);
            _metadata = new GameHistoryMetadata(
                _metadata.Id,
                _metadata.CreatedAt,
                _metadata.ParentSessionId,
                _metadata.MetadataJson,
                DateTimeOffset.UtcNow);
            return Task.FromResult(value);
        }
    }
}

internal sealed class GameHistoryState
{
    private readonly GameHistoryLimits _limits;
    private readonly List<GameHistoryEntry> _entries = new();
    private readonly Dictionary<string, GameHistoryEntry> _entriesById = new(StringComparer.Ordinal);
    private readonly List<GameHistoryRecord> _records = new();
    private readonly HashSet<string> _entityIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _lanes = new(StringComparer.Ordinal) { ["main"] = null };
    private readonly List<GameHistoryLogItem> _log = new();
    private readonly Dictionary<string, GameHistoryLogItem> _logByMutation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);

    internal GameHistoryState(GameHistoryLimits limits)
    {
        _limits = limits;
    }

    internal long Sequence { get; private set; }
    internal string? Name { get; private set; }

    internal IReadOnlyList<GameHistoryLane> GetLanes() =>
        Array.AsReadOnly(_lanes.Select(pair => new GameHistoryLane(pair.Key, pair.Value)).ToArray());

    internal GameHistoryEntry? GetEntry(string id) => _entriesById.TryGetValue(id, out var entry) ? entry : null;

    internal string? GetLabel(string id) => _labels.TryGetValue(id, out var label) ? label : null;

    internal GameHistoryStats GetStats() => new(_entries.Count, _records.Count, _lanes.Count, _log.Count, Sequence);

    internal GameHistoryPage<GameHistoryEntry> FindEntries(GameHistoryEntryQuery query)
    {
        var limit = query.Limit ?? _limits.DefaultQueryResults;
        IEnumerable<GameHistoryEntry> source = query.Order == GameHistoryOrder.OldestFirst ? _entries : _entries.AsEnumerable().Reverse();
        source = source.Where(entry =>
            (query.Type is null || string.Equals(entry.Type, query.Type, StringComparison.Ordinal))
            && (query.CursorSequence is null
                || (query.Order == GameHistoryOrder.OldestFirst
                    ? entry.Sequence > query.CursorSequence
                    : entry.Sequence < query.CursorSequence)));
        return Page(source, limit, entry => entry.Sequence);
    }

    internal GameHistoryPage<GameHistoryEntry> FindBranch(string lane, GameHistoryBranchQuery query)
    {
        if (!_lanes.TryGetValue(lane, out var leaf))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidLane, $"History lane not found: {lane}.");
        }

        var start = query.StartEntryId ?? leaf;
        if (start is null)
        {
            return new GameHistoryPage<GameHistoryEntry>(Array.Empty<GameHistoryEntry>(), null);
        }

        if (!_entriesById.TryGetValue(start, out var current))
        {
            throw new GameHistoryException(GameHistoryErrorCode.NotFound, $"History entry not found: {start}.");
        }

        var path = new List<GameHistoryEntry>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(current.Id))
            {
                throw new GameHistoryException(GameHistoryErrorCode.CorruptStorage, $"History branch contains a cycle at {current.Id}.");
            }

            path.Add(current);
            if (string.Equals(current.Id, query.StopAtEntryId, StringComparison.Ordinal)
                || string.Equals(current.Type, query.StopAtType, StringComparison.Ordinal)
                || current.ParentId is null)
            {
                break;
            }

            if (!_entriesById.TryGetValue(current.ParentId, out current!))
            {
                throw new GameHistoryException(GameHistoryErrorCode.CorruptStorage, $"History parent is missing: {current.ParentId}.");
            }
        }

        IEnumerable<GameHistoryEntry> source = query.Order == GameHistoryOrder.OldestFirst ? path.AsEnumerable().Reverse() : path;
        source = source.Where(entry =>
            (query.Type is null || string.Equals(entry.Type, query.Type, StringComparison.Ordinal))
            && (query.CursorSequence is null
                || (query.Order == GameHistoryOrder.OldestFirst
                    ? entry.Sequence > query.CursorSequence
                    : entry.Sequence < query.CursorSequence)));
        return Page(source, query.Limit ?? _limits.DefaultQueryResults, entry => entry.Sequence);
    }

    internal GameHistoryPage<GameHistoryRecord> FindRecords(GameHistoryRecordQuery query)
    {
        IEnumerable<GameHistoryRecord> source = query.Order == GameHistoryOrder.OldestFirst ? _records : _records.AsEnumerable().Reverse();
        source = source.Where(record =>
            (query.Lane is null || string.Equals(record.Lane, query.Lane, StringComparison.Ordinal))
            && (query.Type is null || string.Equals(record.Type, query.Type, StringComparison.Ordinal))
            && (query.CursorSequence is null
                || (query.Order == GameHistoryOrder.OldestFirst
                    ? record.Sequence > query.CursorSequence
                    : record.Sequence < query.CursorSequence)));
        return Page(source, query.Limit ?? _limits.DefaultQueryResults, record => record.Sequence);
    }

    internal GameHistoryPage<GameHistoryLogItem> GetLog(GameHistoryLogQuery query)
    {
        var source = _log.Where(item => item.Sequence > query.AfterSequence);
        return Page(source, query.Limit ?? _limits.DefaultQueryResults, item => item.Sequence);
    }

    internal GameHistoryEntryCommit AppendEntry(
        string lane,
        string id,
        string type,
        string payloadJson,
        string mutationId,
        long? expectedSequence,
        DateTimeOffset timestamp)
    {
        if (TryReplay(mutationId, out var replay))
        {
            if (replay.Kind != GameHistoryMutationKind.Entry
                || replay.Entry is null
                || !string.Equals(replay.Lane, lane, StringComparison.Ordinal)
                || !string.Equals(replay.Entry.Id, id, StringComparison.Ordinal)
                || !string.Equals(replay.Entry.Type, type, StringComparison.Ordinal)
                || !string.Equals(replay.Entry.PayloadJson, payloadJson, StringComparison.Ordinal))
            {
                throw MutationMismatch(mutationId);
            }

            return new GameHistoryEntryCommit(replay.Entry, new GameHistoryCommit(mutationId, replay.Sequence, true));
        }

        CheckExpected(expectedSequence);
        CheckCapacity(_entries.Count, _limits.MaxEntriesPerSession, "entry");
        CheckMutationCapacity();
        if (!_lanes.TryGetValue(lane, out var parentId))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidLane, $"History lane not found: {lane}.");
        }

        CheckEntityId(id);
        var entry = new GameHistoryEntry(id, Sequence + 1, parentId, timestamp, type, payloadJson);
        var item = new GameHistoryLogItem(mutationId, entry.Sequence, GameHistoryMutationKind.Entry, entry: entry, lane: lane);
        Apply(item, replay: false);
        return new GameHistoryEntryCommit(entry, new GameHistoryCommit(mutationId, entry.Sequence, false));
    }

    internal GameHistoryRecordCommit AppendRecord(
        string lane,
        string id,
        string type,
        string payloadJson,
        string mutationId,
        long? expectedSequence,
        DateTimeOffset timestamp)
    {
        if (TryReplay(mutationId, out var replay))
        {
            if (replay.Kind != GameHistoryMutationKind.Record
                || replay.Record is null
                || !string.Equals(replay.Record.Lane, lane, StringComparison.Ordinal)
                || !string.Equals(replay.Record.Id, id, StringComparison.Ordinal)
                || !string.Equals(replay.Record.Type, type, StringComparison.Ordinal)
                || !string.Equals(replay.Record.PayloadJson, payloadJson, StringComparison.Ordinal))
            {
                throw MutationMismatch(mutationId);
            }

            return new GameHistoryRecordCommit(replay.Record, new GameHistoryCommit(mutationId, replay.Sequence, true));
        }

        CheckExpected(expectedSequence);
        CheckCapacity(_records.Count, _limits.MaxRecordsPerSession, "record");
        CheckMutationCapacity();
        if (!_lanes.ContainsKey(lane))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidLane, $"History lane not found: {lane}.");
        }

        CheckEntityId(id);
        var record = new GameHistoryRecord(id, Sequence + 1, timestamp, lane, type, payloadJson);
        var item = new GameHistoryLogItem(mutationId, record.Sequence, GameHistoryMutationKind.Record, record: record, lane: lane);
        Apply(item, replay: false);
        return new GameHistoryRecordCommit(record, new GameHistoryCommit(mutationId, record.Sequence, false));
    }

    internal GameHistoryCommit CreateLane(string lane, string? at, string mutationId, long? expectedSequence)
    {
        if (TryReplayLane(mutationId, lane, at, createsLane: true, out var replay))
        {
            return replay;
        }

        CheckExpected(expectedSequence);
        CheckMutationCapacity();
        if (_lanes.ContainsKey(lane))
        {
            throw new GameHistoryException(GameHistoryErrorCode.AlreadyExists, $"History lane already exists: {lane}.");
        }

        if (_lanes.Count >= _limits.MaxLanesPerSession)
        {
            throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history has too many lanes.");
        }

        RequireEntry(at);
        var item = new GameHistoryLogItem(mutationId, Sequence + 1, GameHistoryMutationKind.Lane, lane: lane, leafEntryId: at, createsLane: true);
        Apply(item, replay: false);
        return new GameHistoryCommit(mutationId, item.Sequence, false);
    }

    internal GameHistoryCommit MoveLane(string lane, string? to, string mutationId, long? expectedSequence)
    {
        if (TryReplayLane(mutationId, lane, to, createsLane: false, out var replay))
        {
            return replay;
        }

        CheckExpected(expectedSequence);
        CheckMutationCapacity();
        if (!_lanes.ContainsKey(lane))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidLane, $"History lane not found: {lane}.");
        }

        RequireEntry(to);
        var item = new GameHistoryLogItem(mutationId, Sequence + 1, GameHistoryMutationKind.Lane, lane: lane, leafEntryId: to, createsLane: false);
        Apply(item, replay: false);
        return new GameHistoryCommit(mutationId, item.Sequence, false);
    }

    internal GameHistoryCommit SetName(string name, string mutationId, long? expectedSequence)
    {
        if (TryReplayFact(mutationId, GameHistoryMutationKind.Name, name, null, out var replay))
        {
            return replay;
        }

        CheckExpected(expectedSequence);
        CheckMutationCapacity();
        var item = new GameHistoryLogItem(mutationId, Sequence + 1, GameHistoryMutationKind.Name, name: name);
        Apply(item, replay: false);
        return new GameHistoryCommit(mutationId, item.Sequence, false);
    }

    internal GameHistoryCommit SetLabel(string target, string? label, string mutationId, long? expectedSequence)
    {
        if (TryReplayFact(mutationId, GameHistoryMutationKind.Label, target, label, out var replay))
        {
            return replay;
        }

        CheckExpected(expectedSequence);
        CheckMutationCapacity();
        RequireEntry(target);
        var item = new GameHistoryLogItem(
            mutationId,
            Sequence + 1,
            GameHistoryMutationKind.Label,
            targetEntryId: target,
            label: label);
        Apply(item, replay: false);
        return new GameHistoryCommit(mutationId, item.Sequence, false);
    }

    internal void Replay(GameHistoryLogItem item)
    {
        CheckMutationCapacity();
        if (item.Kind == GameHistoryMutationKind.Entry)
        {
            CheckCapacity(_entries.Count, _limits.MaxEntriesPerSession, "entry");
        }
        else if (item.Kind == GameHistoryMutationKind.Record)
        {
            CheckCapacity(_records.Count, _limits.MaxRecordsPerSession, "record");
        }

        if (item.Sequence != Sequence + 1)
        {
            throw Corrupt($"History sequence {item.Sequence} is not consecutive.");
        }

        if (_logByMutation.ContainsKey(item.MutationId))
        {
            throw Corrupt($"History contains duplicate mutation ID {item.MutationId}.");
        }

        Apply(item, replay: true);
    }

    internal GameHistoryState CopyForFork(GameHistoryForkOptions options)
    {
        IReadOnlyList<GameHistoryEntry> entries;
        IReadOnlyList<GameHistoryLane> lanes;
        if (options.Scope == GameHistoryForkScope.Tree)
        {
            entries = _entries;
            lanes = GetLanes();
        }
        else
        {
            var selected = options.EntryId is null
                ? (_lanes.TryGetValue("main", out var leaf) ? leaf : null)
                : options.EntryId;
            string? target = null;
            if (selected is not null)
            {
                if (!_entriesById.TryGetValue(selected, out var entry))
                {
                    throw new GameHistoryException(GameHistoryErrorCode.InvalidForkTarget, $"Fork entry not found: {selected}.");
                }

                target = options.Position == GameHistoryForkPosition.At ? entry.Id : entry.ParentId;
            }

            entries = target is null
                ? Array.Empty<GameHistoryEntry>()
                : BuildPath(target).Reverse().ToArray();
            lanes = new[] { new GameHistoryLane("main", target) };
        }

        var copy = new GameHistoryState(_limits);
        foreach (var entry in entries)
        {
            var cloned = new GameHistoryEntry(entry.Id, copy.Sequence + 1, entry.ParentId, entry.Timestamp, entry.Type, entry.PayloadJson);
            copy.Replay(new GameHistoryLogItem($"fork-{cloned.Sequence}", cloned.Sequence, GameHistoryMutationKind.Entry, entry: cloned));
        }

        foreach (var lane in lanes)
        {
            copy.Replay(new GameHistoryLogItem($"fork-{copy.Sequence + 1}", copy.Sequence + 1, GameHistoryMutationKind.Lane, lane: lane.Name, leafEntryId: lane.LeafEntryId, createsLane: !string.Equals(lane.Name, "main", StringComparison.Ordinal)));
        }

        if (Name is not null)
        {
            copy.Replay(new GameHistoryLogItem($"fork-{copy.Sequence + 1}", copy.Sequence + 1, GameHistoryMutationKind.Name, name: Name));
        }

        foreach (var entry in entries)
        {
            if (_labels.TryGetValue(entry.Id, out var label))
            {
                copy.Replay(new GameHistoryLogItem($"fork-{copy.Sequence + 1}", copy.Sequence + 1, GameHistoryMutationKind.Label, targetEntryId: entry.Id, label: label));
            }
        }

        return copy;
    }

    internal IReadOnlyList<GameHistoryLogItem> ExportLog() => Array.AsReadOnly(_log.ToArray());

    private IReadOnlyList<GameHistoryEntry> BuildPath(string start)
    {
        var result = new List<GameHistoryEntry>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentId = start;
        while (true)
        {
            if (!visited.Add(currentId) || !_entriesById.TryGetValue(currentId, out var current))
            {
                throw Corrupt($"History branch is invalid at {currentId}.");
            }

            result.Add(current);
            if (current.ParentId is null)
            {
                return result;
            }

            currentId = current.ParentId;
        }
    }

    private void Apply(GameHistoryLogItem item, bool replay)
    {
        if (item.Sequence != Sequence + 1)
        {
            throw Corrupt($"History sequence {item.Sequence} is not consecutive.");
        }

        switch (item.Kind)
        {
            case GameHistoryMutationKind.Entry:
                if (item.Entry is null || item.Entry.Sequence != item.Sequence)
                {
                    throw Corrupt("History entry mutation is malformed.");
                }

                if (!_lanes.TryGetValue(item.Lane ?? string.Empty, out var expectedParent) && item.Lane is not null)
                {
                    throw Corrupt($"History entry references missing lane {item.Lane}.");
                }

                if (item.Entry.ParentId is not null && !_entriesById.ContainsKey(item.Entry.ParentId))
                {
                    throw Corrupt($"History entry references missing parent {item.Entry.ParentId}.");
                }

                if (item.Lane is not null && !string.Equals(expectedParent, item.Entry.ParentId, StringComparison.Ordinal))
                {
                    throw Corrupt("History entry does not chain to its lane leaf.");
                }

                if (!_entityIds.Add(item.Entry.Id))
                {
                    throw Corrupt($"History contains duplicate entity ID {item.Entry.Id}.");
                }

                _entries.Add(item.Entry);
                _entriesById.Add(item.Entry.Id, item.Entry);
                if (item.Lane is not null)
                {
                    _lanes[item.Lane] = item.Entry.Id;
                }

                break;
            case GameHistoryMutationKind.Record:
                if (item.Record is null || item.Record.Sequence != item.Sequence || !_lanes.ContainsKey(item.Record.Lane))
                {
                    throw Corrupt("History record mutation is malformed.");
                }

                if (!_entityIds.Add(item.Record.Id))
                {
                    throw Corrupt($"History contains duplicate entity ID {item.Record.Id}.");
                }

                _records.Add(item.Record);
                break;
            case GameHistoryMutationKind.Lane:
                if (item.Lane is null || (item.LeafEntryId is not null && !_entriesById.ContainsKey(item.LeafEntryId)))
                {
                    throw Corrupt("History lane mutation is malformed.");
                }

                if (item.CreatesLane is null)
                {
                    throw Corrupt("History lane mutation is missing its operation kind.");
                }

                if (item.CreatesLane.Value && _lanes.ContainsKey(item.Lane))
                {
                    throw Corrupt($"History creates duplicate lane {item.Lane}.");
                }

                if (!item.CreatesLane.Value && !_lanes.ContainsKey(item.Lane))
                {
                    throw Corrupt($"History moves missing lane {item.Lane}.");
                }

                _lanes[item.Lane] = item.LeafEntryId;
                break;
            case GameHistoryMutationKind.Name:
                if (item.Name is null)
                {
                    throw Corrupt("History name mutation is malformed.");
                }

                Name = item.Name;
                break;
            case GameHistoryMutationKind.Label:
                if (item.TargetEntryId is null || !_entriesById.ContainsKey(item.TargetEntryId))
                {
                    throw Corrupt("History label mutation is malformed.");
                }

                if (item.Label is null)
                {
                    _labels.Remove(item.TargetEntryId);
                }
                else
                {
                    _labels[item.TargetEntryId] = item.Label;
                }

                break;
            default:
                throw Corrupt("History mutation kind is invalid.");
        }

        Sequence = item.Sequence;
        _log.Add(item);
        _logByMutation.Add(item.MutationId, item);
        if (!replay && _log.Count > _limits.MaxMutationsPerSession)
        {
            throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, "The history mutation limit was exceeded.");
        }
    }

    private bool TryReplay(string mutationId, out GameHistoryLogItem item) => _logByMutation.TryGetValue(mutationId, out item!);

    private bool TryReplayFact(
        string mutationId,
        GameHistoryMutationKind kind,
        string first,
        string? second,
        out GameHistoryCommit commit)
    {
        if (!TryReplay(mutationId, out var item))
        {
            commit = null!;
            return false;
        }

        var matches = kind switch
        {
            GameHistoryMutationKind.Name => item.Kind == kind && string.Equals(item.Name, first, StringComparison.Ordinal),
            GameHistoryMutationKind.Label => item.Kind == kind
                && string.Equals(item.TargetEntryId, first, StringComparison.Ordinal)
                && string.Equals(item.Label, second, StringComparison.Ordinal),
            _ => false,
        };
        if (!matches)
        {
            throw MutationMismatch(mutationId);
        }

        commit = new GameHistoryCommit(mutationId, item.Sequence, true);
        return true;
    }

    private bool TryReplayLane(
        string mutationId,
        string lane,
        string? leafEntryId,
        bool createsLane,
        out GameHistoryCommit commit)
    {
        if (!TryReplay(mutationId, out var item))
        {
            commit = null!;
            return false;
        }

        if (item.Kind != GameHistoryMutationKind.Lane
            || item.CreatesLane != createsLane
            || !string.Equals(item.Lane, lane, StringComparison.Ordinal)
            || !string.Equals(item.LeafEntryId, leafEntryId, StringComparison.Ordinal))
        {
            throw MutationMismatch(mutationId);
        }

        commit = new GameHistoryCommit(mutationId, item.Sequence, true);
        return true;
    }

    private void CheckExpected(long? expected)
    {
        if (expected is not null && expected.Value != Sequence)
        {
            throw new GameHistoryConcurrencyException(expected.Value, Sequence);
        }
    }

    private void CheckMutationCapacity()
    {
        CheckCapacity(_log.Count, _limits.MaxMutationsPerSession, "mutation");
    }

    private static void CheckCapacity(int current, int limit, string kind)
    {
        if (current >= limit)
        {
            throw new GameHistoryException(GameHistoryErrorCode.LimitExceeded, $"The history {kind} limit was reached.");
        }
    }

    private void CheckEntityId(string id)
    {
        if (_entityIds.Contains(id))
        {
            throw new GameHistoryException(GameHistoryErrorCode.AlreadyExists, $"History entity ID already exists: {id}.");
        }
    }

    private void RequireEntry(string? id)
    {
        if (id is not null && !_entriesById.ContainsKey(id))
        {
            throw new GameHistoryException(GameHistoryErrorCode.NotFound, $"History entry not found: {id}.");
        }
    }

    private static GameHistoryException MutationMismatch(string id) => new(
        GameHistoryErrorCode.Conflict,
        $"Mutation ID {id} was already committed with different content.");

    private static GameHistoryException Corrupt(string message) => new(GameHistoryErrorCode.CorruptStorage, message);

    private static GameHistoryPage<T> Page<T>(IEnumerable<T> source, int limit, Func<T, long> sequence)
    {
        var values = source.Take(limit + 1).ToArray();
        var hasMore = values.Length > limit;
        var items = hasMore ? values.Take(limit).ToArray() : values;
        return new GameHistoryPage<T>(items, hasMore && items.Length > 0 ? sequence(items[^1]) : null);
    }
}

internal static class GameHistoryValidation
{
    internal static void SessionId(string value, string name, GameHistoryLimits limits)
    {
        Identifier(value, name, limits);
        if (!char.IsLetterOrDigit(value[0])
            || !char.IsLetterOrDigit(value[value.Length - 1])
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new GameHistoryException(
                GameHistoryErrorCode.InvalidInput,
                "A session ID must start and end with an alphanumeric character and contain only alphanumerics, '-', '_', and '.'.");
        }
    }

    internal static void Identifier(string value, string name, GameHistoryLimits limits)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > limits.MaxIdentifierCharacters
            || value.Any(character => char.IsControl(character)))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, $"{name} is not a valid bounded identifier.");
        }
    }

    internal static void OptionalIdentifier(string? value, string name, GameHistoryLimits limits)
    {
        if (value is not null)
        {
            Identifier(value, name, limits);
        }
    }

    internal static void Type(string value, string name, GameHistoryLimits limits)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limits.MaxTypeCharacters || value.Any(char.IsControl))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, $"{name} is not a valid bounded type.");
        }
    }

    internal static void Fact(string value, string name, GameHistoryLimits limits)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limits.MaxFactCharacters || value.IndexOf('\0') >= 0)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, $"{name} is not a valid bounded value.");
        }
    }

    internal static void Json(string value, string name, int maxCharacters)
    {
        if (value is null || value.Length > maxCharacters)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, $"{name} is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException exception)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, $"{name} is not valid JSON.", exception);
        }
    }

    internal static void JsonObject(string value, string name, int maxCharacters)
    {
        Json(value, name, maxCharacters);
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, $"{name} must be a JSON object.");
        }
    }

    internal static string MutationId(string? mutationId, GameHistoryLimits limits)
    {
        var value = mutationId ?? Guid.NewGuid().ToString("N");
        Identifier(value, nameof(mutationId), limits);
        return value;
    }

    internal static void ExpectedSequence(long? expected)
    {
        if (expected is < 0)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, "The expected sequence cannot be negative.");
        }
    }

    internal static int Limit(int? value, GameHistoryLimits limits)
    {
        var limit = value ?? limits.DefaultQueryResults;
        if (limit < 1 || limit > limits.MaxQueryResults)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The query limit is invalid.");
        }

        return limit;
    }

    internal static void EntryQuery(GameHistoryEntryQuery query, GameHistoryLimits limits)
    {
        Order(query.Order);
        Limit(query.Limit, limits);
        OptionalCursor(query.CursorSequence);
        if (query.Type is not null)
        {
            Type(query.Type, nameof(query.Type), limits);
        }
    }

    internal static void BranchQuery(GameHistoryBranchQuery query, GameHistoryLimits limits)
    {
        Order(query.Order);
        Limit(query.Limit, limits);
        OptionalCursor(query.CursorSequence);
        OptionalIdentifier(query.StartEntryId, nameof(query.StartEntryId), limits);
        OptionalIdentifier(query.StopAtEntryId, nameof(query.StopAtEntryId), limits);
        if (query.Type is not null) Type(query.Type, nameof(query.Type), limits);
        if (query.StopAtType is not null) Type(query.StopAtType, nameof(query.StopAtType), limits);
    }

    internal static void RecordQuery(GameHistoryRecordQuery query, GameHistoryLimits limits)
    {
        Order(query.Order);
        Limit(query.Limit, limits);
        OptionalCursor(query.CursorSequence);
        OptionalIdentifier(query.Lane, nameof(query.Lane), limits);
        if (query.Type is not null) Type(query.Type, nameof(query.Type), limits);
    }

    internal static void LogQuery(GameHistoryLogQuery query, GameHistoryLimits limits)
    {
        Limit(query.Limit, limits);
        if (query.AfterSequence < 0)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The log cursor cannot be negative.");
        }
    }

    internal static void Fork(GameHistoryForkOptions options, GameHistoryLimits limits)
    {
        OptionalIdentifier(options.Id, nameof(options.Id), limits);
        OptionalIdentifier(options.ParentSessionId, nameof(options.ParentSessionId), limits);
        OptionalIdentifier(options.EntryId, nameof(options.EntryId), limits);
        ExpectedSequence(options.ExpectedSourceSequence);
        if (options.MetadataJson is not null)
        {
            JsonObject(options.MetadataJson, nameof(options.MetadataJson), limits.MaxPayloadCharacters);
        }

        if (!Enum.IsDefined(typeof(GameHistoryForkScope), options.Scope)
            || !Enum.IsDefined(typeof(GameHistoryForkPosition), options.Position))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidInput, "The fork options are invalid.");
        }
    }

    internal static void Search(GameHistorySearchQuery query, GameHistoryLimits limits)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (string.IsNullOrWhiteSpace(query.Text) || query.Text.Length > limits.MaxSearchCharacters || query.Text.IndexOf('\0') >= 0)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The search text is invalid.");
        }

        OptionalIdentifier(query.SessionId, nameof(query.SessionId), limits);
        if (query.EntryType is not null) Type(query.EntryType, nameof(query.EntryType), limits);
        var limit = query.Limit ?? Math.Min(limits.DefaultQueryResults, limits.MaxSearchResults);
        if (limit < 1 || limit > limits.MaxSearchResults)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The search limit is invalid.");
        }

        if (query.Cursor is not null)
        {
            SessionId(query.Cursor.SessionId, nameof(query.Cursor.SessionId), limits);
            if (query.Cursor.EntrySequence < 1)
            {
                throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The search cursor is invalid.");
            }

            if (query.SessionId is not null
                && !string.Equals(query.SessionId, query.Cursor.SessionId, StringComparison.Ordinal))
            {
                throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The search cursor belongs to another session.");
            }
        }
    }

    private static void OptionalCursor(long? cursor)
    {
        if (cursor is < 0)
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The query cursor cannot be negative.");
        }
    }

    private static void Order(GameHistoryOrder order)
    {
        if (!Enum.IsDefined(typeof(GameHistoryOrder), order))
        {
            throw new GameHistoryException(GameHistoryErrorCode.InvalidQuery, "The query order is invalid.");
        }
    }
}

internal static class GameHistoryObjectValidation
{
    internal static string Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new ArgumentException("A non-empty value without control characters is required.", name);
        }

        return value;
    }

    internal static string? Optional(string? value, string name) => value is null ? null : Required(value, name);

    internal static long Sequence(long value, string name)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(name);
        }

        return value;
    }

    internal static string Json(string value, string name)
    {
        if (value is null)
        {
            throw new ArgumentNullException(name);
        }

        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
        return value;
    }

    internal static string JsonObject(string value, string name)
    {
        Json(value, name);
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A JSON object is required.", name);
        }

        return value;
    }
}

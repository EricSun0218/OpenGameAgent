using System;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Extensions;

namespace OpenGameAgent.Persistence;

public sealed class FileGameAgentDelegationStore : IGameAgentDelegationStore
{
    private const string Suffix = ".delegation.json";
    private readonly FileStore _files;

    public FileGameAgentDelegationStore(
        string directory,
        long maximumFileBytes = 4_000_000,
        int concurrencyStripes = 64)
    {
        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
    }

    public async ValueTask<GameAgentDelegationRecord?> LoadAsync(
        string sessionId,
        string actorId,
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(actorId)
            || string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Delegation IDs and owners are required.");
        }

        var storageKey = StorageKey(sessionId, actorId, id);
        var gate = _files.GateFor(storageKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(storageKey + Suffix, cancellationToken).ConfigureAwait(false);
            var document = await _files.ReadAsync<DelegationDocument>(
                _files.PathFor(storageKey, Suffix),
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return null;
            }

            var record = Decode(document);
            if (!string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(record.ActorId, actorId, StringComparison.Ordinal)
                || !string.Equals(record.Id, id, StringComparison.Ordinal))
            {
                throw new PersistenceException("The delegation identity does not match its storage key.");
            }

            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameAgentDelegationSaveResult> SaveAsync(
        GameAgentDelegationRecord record,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var storageKey = StorageKey(record.SessionId, record.ActorId, record.Id);
        var gate = _files.GateFor(storageKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(storageKey + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(storageKey, Suffix);
            var document = await _files.ReadAsync<DelegationDocument>(path, cancellationToken).ConfigureAwait(false);
            var current = document is null ? null : Decode(document);
            if (current is not null)
            {
                EnsureSameIdentity(current, record);
            }

            if ((current?.Revision ?? 0) != expectedRevision)
            {
                return new GameAgentDelegationSaveResult(
                    false,
                    current ?? new GameAgentDelegationRecord(
                        record.Id,
                        record.SessionId,
                        record.ActorId,
                        0,
                        GameAgentDelegationStatus.Pending,
                        record.TaskJson,
                        record.Depth,
                        record.CreatedAt));
            }

            if (current is not null && IsTerminal(current.Status))
            {
                throw new PersistenceException("A terminal delegation record is immutable.");
            }

            if (record.Revision != checked(expectedRevision + 1))
            {
                throw new ArgumentException("A delegation revision must advance by exactly one.", nameof(record));
            }

            await _files.WriteAtomicAsync(path, Encode(record), cancellationToken).ConfigureAwait(false);
            return new GameAgentDelegationSaveResult(true, record);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string StorageKey(string sessionId, string actorId, string id) =>
        string.Concat(sessionId, "\n", actorId, "\n", id);

    private static DelegationDocument Encode(GameAgentDelegationRecord record) => new()
    {
        FormatVersion = 1,
        Id = record.Id,
        SessionId = record.SessionId,
        ActorId = record.ActorId,
        Revision = record.Revision,
        Status = record.Status,
        TaskJson = record.TaskJson,
        Depth = record.Depth,
        TimelineId = record.CreatedAt.TimelineId,
        Tick = record.CreatedAt.Tick,
        CalendarJson = record.CreatedAt.CalendarJson,
        ResultJson = record.ResultJson,
        Error = record.Error,
    };

    private static GameAgentDelegationRecord Decode(DelegationDocument document)
    {
        if (document.FormatVersion != 1)
        {
            throw new PersistenceException("The delegation document has an unsupported format.");
        }

        return FileStore.DecodeDocument(
            "delegation document",
            () => new GameAgentDelegationRecord(
                document.Id,
                document.SessionId,
                document.ActorId,
                document.Revision,
                document.Status,
                document.TaskJson,
                document.Depth,
                new GameMoment(document.TimelineId, document.Tick, document.CalendarJson),
                document.ResultJson,
                document.Error));
    }

    private static void EnsureSameIdentity(GameAgentDelegationRecord current, GameAgentDelegationRecord next)
    {
        if (!string.Equals(current.Id, next.Id, StringComparison.Ordinal)
            || !string.Equals(current.SessionId, next.SessionId, StringComparison.Ordinal)
            || !string.Equals(current.ActorId, next.ActorId, StringComparison.Ordinal)
            || !string.Equals(current.TaskJson, next.TaskJson, StringComparison.Ordinal)
            || current.Depth != next.Depth
            || current.CreatedAt != next.CreatedAt)
        {
            throw new PersistenceException("A delegation cannot change its task identity or owner.");
        }
    }

    private static bool IsTerminal(GameAgentDelegationStatus status) =>
        status is GameAgentDelegationStatus.Completed
            or GameAgentDelegationStatus.Failed
            or GameAgentDelegationStatus.Cancelled;

    private sealed class DelegationDocument
    {
        public int FormatVersion { get; set; }

        public string Id { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public long Revision { get; set; }

        public GameAgentDelegationStatus Status { get; set; }

        public string TaskJson { get; set; } = "{}";

        public int Depth { get; set; }

        public string TimelineId { get; set; } = string.Empty;

        public long Tick { get; set; }

        public string? CalendarJson { get; set; }

        public string? ResultJson { get; set; }

        public string? Error { get; set; }
    }
}

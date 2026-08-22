using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent;
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

    public async ValueTask<IReadOnlyList<GameAgentDelegationRecord>> ListRecoverableAsync(
        DateTimeOffset now,
        int maximum,
        CancellationToken cancellationToken)
    {
        ValidateMaximum(maximum);
        var records = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return records
            .Where(record => record.IsRecoverable(now))
            .OrderBy(record => record.CreatedAt.TimelineId, StringComparer.Ordinal)
            .ThenBy(record => record.CreatedAt.Tick)
            .ThenBy(record => record.Id, StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<GameAgentDelegationRecord>> ListAsync(
        string sessionId,
        string actorId,
        string? rootDelegationId,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("Delegation owners are required.");
        }

        if (rootDelegationId is not null
            && (string.IsNullOrWhiteSpace(rootDelegationId)
                || rootDelegationId.Length > 256
                || rootDelegationId.Any(char.IsControl)))
        {
            throw new ArgumentException("A delegation lineage ID must be bounded and printable.", nameof(rootDelegationId));
        }

        ValidateMaximum(maximum);
        var records = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return records
            .Where(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)
                             && string.Equals(record.ActorId, actorId, StringComparison.Ordinal)
                             && (rootDelegationId is null
                                 || string.Equals(record.RootDelegationId, rootDelegationId, StringComparison.Ordinal)))
            .OrderBy(record => record.CreatedAt.Tick)
            .ThenBy(record => record.Id, StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();
    }

    private async ValueTask<IReadOnlyList<GameAgentDelegationRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var records = new List<GameAgentDelegationRecord>();
        foreach (var path in Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await _files.ReadAsync<DelegationDocument>(path, cancellationToken).ConfigureAwait(false)
                ?? throw new PersistenceException("A delegation file disappeared while it was being enumerated.");
            var record = Decode(document);
            _files.EnsurePathFor(
                path,
                StorageKey(record.SessionId, record.ActorId, record.Id),
                Suffix,
                "delegation document");
            records.Add(record);
        }

        return records;
    }

    private static string StorageKey(string sessionId, string actorId, string id) =>
        string.Concat(sessionId, "\n", actorId, "\n", id);

    private static DelegationDocument Encode(GameAgentDelegationRecord record) => new()
    {
        FormatVersion = 2,
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
        ParentDelegationId = record.ParentDelegationId,
        RootDelegationId = record.RootDelegationId,
        LeaseId = record.LeaseId,
        LeaseExpiresAt = record.LeaseExpiresAt,
        Attempt = record.Attempt,
        Request = record.Request is null ? null : new DelegationRequestDocument
        {
            ParentInputJson = GameAgentWire.SerializeInput(record.Request.ParentInput),
            MaximumTurns = record.Request.MaximumTurns,
            InheritContext = record.Request.InheritContext,
            ParentMessages = record.Request.ParentMessages.Select(AgentMessageCodec.Encode).ToList(),
            ExecutionScopeUnrestricted = record.Request.ExecutionScope.IsUnrestricted,
            GrantedCapabilities = record.Request.ExecutionScope.GrantedCapabilities.ToList(),
        },
    };

    private static GameAgentDelegationRecord Decode(DelegationDocument document)
    {
        if (document.FormatVersion is not 1 and not 2)
        {
            throw new PersistenceException("The delegation document has an unsupported format.");
        }

        return FileStore.DecodeDocument(
            "delegation document",
            () =>
            {
                var parentDelegationId = document.FormatVersion >= 2 ? document.ParentDelegationId : null;
                var rootDelegationId = document.FormatVersion >= 2 ? document.RootDelegationId : document.Id;
                var request = document.Request is null
                    ? null
                    : new GameAgentDelegateRequest(
                        document.Id,
                        GameAgentWire.ParseInput(document.Request.ParentInputJson),
                        document.TaskJson,
                        document.Depth,
                        document.Request.MaximumTurns,
                        document.Request.InheritContext,
                        (document.Request.ParentMessages
                         ?? throw new PersistenceException("Persisted delegation parent messages are missing."))
                        .Select(AgentMessageCodec.Decode)
                        .ToArray(),
                        document.Request.ExecutionScopeUnrestricted
                            ? GameExecutionScope.Unrestricted
                            : GameExecutionScope.Restricted(
                                document.Request.GrantedCapabilities is null
                                    ? Array.Empty<string>()
                                    : document.Request.GrantedCapabilities),
                        parentDelegationId,
                        rootDelegationId);
                return new GameAgentDelegationRecord(
                    document.Id,
                    document.SessionId,
                    document.ActorId,
                    document.Revision,
                    document.Status,
                    document.TaskJson,
                    document.Depth,
                    new GameMoment(document.TimelineId, document.Tick, document.CalendarJson),
                    document.ResultJson,
                    document.Error,
                    request,
                    parentDelegationId,
                    rootDelegationId,
                    document.LeaseId,
                    document.LeaseExpiresAt,
                    document.Attempt);
            });
    }

    private static void EnsureSameIdentity(GameAgentDelegationRecord current, GameAgentDelegationRecord next)
    {
        if (!string.Equals(current.Id, next.Id, StringComparison.Ordinal)
            || !string.Equals(current.SessionId, next.SessionId, StringComparison.Ordinal)
            || !string.Equals(current.ActorId, next.ActorId, StringComparison.Ordinal)
            || !string.Equals(current.TaskJson, next.TaskJson, StringComparison.Ordinal)
            || current.Depth != next.Depth
            || current.CreatedAt != next.CreatedAt
            || !string.Equals(current.ParentDelegationId, next.ParentDelegationId, StringComparison.Ordinal)
            || !string.Equals(current.RootDelegationId, next.RootDelegationId, StringComparison.Ordinal)
            || !SameRequestIdentity(current.Request, next.Request))
        {
            throw new PersistenceException("A delegation cannot change its task identity or owner.");
        }
    }

    private static bool IsTerminal(GameAgentDelegationStatus status) =>
        status is GameAgentDelegationStatus.Completed
            or GameAgentDelegationStatus.Failed
            or GameAgentDelegationStatus.Cancelled;

    private static bool SameRequestIdentity(GameAgentDelegateRequest? left, GameAgentDelegateRequest? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.MaximumTurns == right.MaximumTurns
               && left.InheritContext == right.InheritContext
               && GameAgentValueComparer.MessagesEqual(left.ParentMessages, right.ParentMessages)
               && string.Equals(GameAgentWire.SerializeInput(left.ParentInput), GameAgentWire.SerializeInput(right.ParentInput), StringComparison.Ordinal)
               && left.ExecutionScope.IsUnrestricted == right.ExecutionScope.IsUnrestricted
               && left.ExecutionScope.GrantedCapabilities.SequenceEqual(right.ExecutionScope.GrantedCapabilities, StringComparer.Ordinal);
    }

    private static void ValidateMaximum(int maximum)
    {
        if (maximum < 1 || maximum > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }
    }

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

        public string? ParentDelegationId { get; set; }

        public string? RootDelegationId { get; set; }

        public string? LeaseId { get; set; }

        public DateTimeOffset? LeaseExpiresAt { get; set; }

        public int Attempt { get; set; }

        public DelegationRequestDocument? Request { get; set; }
    }

    private sealed class DelegationRequestDocument
    {
        public string ParentInputJson { get; set; } = string.Empty;

        public int MaximumTurns { get; set; }

        public bool InheritContext { get; set; }

        public List<MessageDocument>? ParentMessages { get; set; }

        public bool ExecutionScopeUnrestricted { get; set; }

        public List<string>? GrantedCapabilities { get; set; }
    }
}

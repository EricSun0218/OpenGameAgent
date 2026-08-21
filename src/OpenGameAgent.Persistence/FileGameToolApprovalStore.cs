using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Extensions;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Persistence;

/// <summary>
/// Crash-tolerant approval storage grouped by authoritative session/actor owner. Reads never scan
/// another owner's files and corrupted state fails closed.
/// </summary>
public sealed class FileGameToolApprovalStore : IGameToolApprovalStore
{
    private const string Suffix = ".tool-approvals.json";
    private readonly FileStore _files;
    private readonly int _maximumRecordsPerOwner;

    public FileGameToolApprovalStore(
        string directory,
        int maximumRecordsPerOwner = 2_048,
        long maximumFileBytes = 16_000_000,
        int concurrencyStripes = 64)
    {
        if (maximumRecordsPerOwner < 1 || maximumRecordsPerOwner > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecordsPerOwner));
        }

        _maximumRecordsPerOwner = maximumRecordsPerOwner;
        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
    }

    public async ValueTask<GameToolApprovalRecord?> ReadAsync(
        GameSessionKey owner,
        string approvalId,
        CancellationToken cancellationToken)
    {
        RequireApprovalId(approvalId);
        var storageKey = StorageKey(owner);
        var gate = _files.GateFor(storageKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await _files.AcquireProcessLeaseAsync(storageKey + Suffix, cancellationToken).ConfigureAwait(false);
            var records = await ReadOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
            return records.FirstOrDefault(value => string.Equals(value.Request.ApprovalId, approvalId, StringComparison.Ordinal));
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<GameToolApprovalRecord>> ListAsync(
        GameSessionKey owner,
        GameToolApprovalStatus? status,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (maximum < 1 || maximum > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        var storageKey = StorageKey(owner);
        var gate = _files.GateFor(storageKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await _files.AcquireProcessLeaseAsync(storageKey + Suffix, cancellationToken).ConfigureAwait(false);
            var records = await ReadOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
            return Array.AsReadOnly(records
                .Where(value => status is null || value.Status == status)
                .OrderBy(value => value.Request.RequestedAt)
                .ThenBy(value => value.Request.ApprovalId, StringComparer.Ordinal)
                .Take(maximum)
                .ToArray());
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameToolApprovalRecord> SaveAsync(
        GameToolApprovalRecord record,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var owner = record.Request.Owner;
        var storageKey = StorageKey(owner);
        var gate = _files.GateFor(storageKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await _files.AcquireProcessLeaseAsync(storageKey + Suffix, cancellationToken).ConfigureAwait(false);
            var records = (await ReadOwnerAsync(owner, cancellationToken).ConfigureAwait(false)).ToList();
            var index = records.FindIndex(value =>
                string.Equals(value.Request.ApprovalId, record.Request.ApprovalId, StringComparison.Ordinal));
            var current = index < 0 ? null : records[index];
            if (expectedRevision is null)
            {
                if (current is not null)
                {
                    if (!GameToolApprovalBroker.EquivalentRequest(current.Request, record.Request))
                    {
                        throw new PersistenceException("The approval ID is already bound to a different request.");
                    }

                    return current;
                }

                if (record.Revision != 0 || record.Status != GameToolApprovalStatus.Pending)
                {
                    throw new ArgumentException("A new approval must start pending at revision zero.", nameof(record));
                }

                ReclaimTerminalRecords(records);
                if (records.Count >= _maximumRecordsPerOwner)
                {
                    throw new PersistenceException("The approval store is full of non-reclaimable records.");
                }

                records.Add(record);
            }
            else
            {
                if (current is null || current.Revision != expectedRevision.Value)
                {
                    throw new InvalidOperationException("The approval revision changed.");
                }

                if (!GameToolApprovalBroker.EquivalentRequest(current.Request, record.Request)
                    || record.Revision != checked(current.Revision + 1)
                    || !IsValidTransition(current.Status, record.Status))
                {
                    throw new PersistenceException("The approval update changes immutable identity or has an invalid transition.");
                }

                records[index] = record;
            }

            await WriteOwnerAsync(owner, records, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<GameToolApprovalRecord>> ReadOwnerAsync(
        GameSessionKey owner,
        CancellationToken cancellationToken)
    {
        var path = _files.PathFor(StorageKey(owner), Suffix);
        var document = await _files.ReadAsync<ApprovalOwnerDocument>(path, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return Array.Empty<GameToolApprovalRecord>();
        }

        if (document.FormatVersion != 1
            || !string.Equals(document.SessionId, owner.SessionId, StringComparison.Ordinal)
            || !string.Equals(document.ActorId, owner.ActorId, StringComparison.Ordinal)
            || document.Records is null
            || document.Records.Count > _maximumRecordsPerOwner)
        {
            throw new PersistenceException("The tool approval document is invalid or belongs to another owner.");
        }

        var records = document.Records.Select(Decode).ToArray();
        if (records.Any(value => !value.Request.Owner.Equals(owner))
            || records.Select(value => value.Request.ApprovalId).Distinct(StringComparer.Ordinal).Count() != records.Length)
        {
            throw new PersistenceException("The tool approval document contains conflicting identities.");
        }

        return records;
    }

    private ValueTask WriteOwnerAsync(
        GameSessionKey owner,
        IReadOnlyList<GameToolApprovalRecord> records,
        CancellationToken cancellationToken) =>
        _files.WriteAtomicAsync(
            _files.PathFor(StorageKey(owner), Suffix),
            new ApprovalOwnerDocument
            {
                FormatVersion = 1,
                SessionId = owner.SessionId,
                ActorId = owner.ActorId,
                Records = records.Select(Encode).ToList(),
            },
            cancellationToken);

    private void ReclaimTerminalRecords(List<GameToolApprovalRecord> records)
    {
        while (records.Count >= _maximumRecordsPerOwner)
        {
            var oldest = records
                .Where(value => IsTerminal(value.Status))
                .OrderBy(value => value.UpdatedAt)
                .ThenBy(value => value.Request.ApprovalId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (oldest is null)
            {
                return;
            }

            records.Remove(oldest);
        }
    }

    private static bool IsTerminal(GameToolApprovalStatus status) =>
        status is GameToolApprovalStatus.Denied
            or GameToolApprovalStatus.TimedOut
            or GameToolApprovalStatus.Cancelled
            or GameToolApprovalStatus.Consumed
            or GameToolApprovalStatus.Expired;

    private static bool IsValidTransition(GameToolApprovalStatus current, GameToolApprovalStatus next) =>
        current == GameToolApprovalStatus.Pending
            ? next is GameToolApprovalStatus.Approved
                or GameToolApprovalStatus.Denied
                or GameToolApprovalStatus.TimedOut
                or GameToolApprovalStatus.Cancelled
                or GameToolApprovalStatus.Expired
            : current == GameToolApprovalStatus.Approved
              && next is GameToolApprovalStatus.Consumed or GameToolApprovalStatus.Expired;

    private static string StorageKey(GameSessionKey owner) => owner.SessionId + "\n" + owner.ActorId;

    private static void RequireApprovalId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded approval ID is required.", nameof(value));
        }
    }

    private static ApprovalRecordDocument Encode(GameToolApprovalRecord record) => new()
    {
        ApprovalId = record.Request.ApprovalId,
        PolicyId = record.Request.PolicyId,
        SessionId = record.Request.SessionId,
        ActorId = record.Request.ActorId,
        InputId = record.Request.InputId,
        RunId = record.Request.RunId,
        Turn = record.Request.Turn,
        ToolCallId = record.Request.ToolCallId,
        ToolName = record.Request.ToolName,
        Risk = record.Request.Risk,
        CanonicalArgumentsJson = record.Request.CanonicalArgumentsJson,
        ArgumentsDigest = record.Request.ArgumentsDigest,
        TimelineId = record.Request.Moment.TimelineId,
        Tick = record.Request.Moment.Tick,
        CalendarJson = record.Request.Moment.CalendarJson,
        GenerationId = record.Request.World.GenerationId,
        WorldRevision = record.Request.World.Revision,
        TaskId = record.Request.TaskId,
        RequestedAt = record.Request.RequestedAt,
        ExpiresAt = record.Request.ExpiresAt,
        Status = record.Status,
        Revision = record.Revision,
        UpdatedAt = record.UpdatedAt,
        Reason = record.Reason,
        CredentialDigest = record.CredentialDigest,
    };

    private static GameToolApprovalRecord Decode(ApprovalRecordDocument document) => FileStore.DecodeDocument(
        "tool approval document",
        () => new GameToolApprovalRecord(
            new GameToolApprovalRequest(
                document.ApprovalId,
                document.PolicyId,
                document.SessionId,
                document.ActorId,
                document.InputId,
                document.RunId,
                document.Turn,
                document.ToolCallId,
                document.ToolName,
                document.Risk,
                document.CanonicalArgumentsJson,
                document.ArgumentsDigest,
                new GameMoment(document.TimelineId, document.Tick, document.CalendarJson),
                new GameToolApprovalWorldState(document.GenerationId, document.WorldRevision),
                document.TaskId,
                document.RequestedAt,
                document.ExpiresAt),
            document.Status,
            document.Revision,
            document.UpdatedAt,
            document.Reason,
            document.CredentialDigest));

    private sealed class ApprovalOwnerDocument
    {
        public int FormatVersion { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string ActorId { get; set; } = string.Empty;
        public List<ApprovalRecordDocument>? Records { get; set; }
    }

    private sealed class ApprovalRecordDocument
    {
        public string ApprovalId { get; set; } = string.Empty;
        public string PolicyId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string ActorId { get; set; } = string.Empty;
        public string InputId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public int Turn { get; set; }
        public string ToolCallId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public ToolRisk Risk { get; set; }
        public string CanonicalArgumentsJson { get; set; } = "{}";
        public string ArgumentsDigest { get; set; } = string.Empty;
        public string TimelineId { get; set; } = string.Empty;
        public long Tick { get; set; }
        public string? CalendarJson { get; set; }
        public string GenerationId { get; set; } = string.Empty;
        public long WorldRevision { get; set; }
        public string? TaskId { get; set; }
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public GameToolApprovalStatus Status { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? Reason { get; set; }
        public string? CredentialDigest { get; set; }
    }
}

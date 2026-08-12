using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Persistence;

public sealed class FileGameSessionStore : IGameSessionStore
{
    private const string Suffix = ".session.json";
    private readonly FileStore _files;

    public FileGameSessionStore(
        string directory,
        long maximumFileBytes = 64_000_000,
        int concurrencyStripes = 64)
    {
        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
    }

    public async ValueTask<GameSessionSnapshot?> LoadAsync(
        GameSessionKey key,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var identity = IdentityFor(key);
        var gate = _files.GateFor(identity);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(identity + Suffix, cancellationToken).ConfigureAwait(false);
            var session = Decode(await _files.ReadAsync<SessionDocument>(_files.PathFor(identity, Suffix), cancellationToken).ConfigureAwait(false));
            if (session is not null && !session.Key.Equals(key))
            {
                throw new PersistenceException("The session document identity does not match its storage key.");
            }

            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameSessionSaveResult> SaveAsync(
        GameSessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var identity = IdentityFor(snapshot.Key);
        var path = _files.PathFor(identity, Suffix);
        var gate = _files.GateFor(identity);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(identity + Suffix, cancellationToken).ConfigureAwait(false);
            var current = Decode(await _files.ReadAsync<SessionDocument>(path, cancellationToken).ConfigureAwait(false));
            if (current is not null && !current.Key.Equals(snapshot.Key))
            {
                throw new PersistenceException("The session document identity does not match its storage key.");
            }

            var currentRevision = current?.Revision ?? 0;
            if (currentRevision != expectedRevision)
            {
                return new GameSessionSaveResult(
                    saved: false,
                    current ?? new GameSessionSnapshot(snapshot.Key, 0));
            }

            if (snapshot.Revision != checked(expectedRevision + 1))
            {
                throw new ArgumentException("A saved snapshot revision must advance by exactly one.", nameof(snapshot));
            }

            if (current is not null)
            {
                snapshot.UsageLedger.EnsureExtends(current.UsageLedger);
            }

            await _files.WriteAtomicAsync(path, Encode(snapshot), cancellationToken).ConfigureAwait(false);
            return new GameSessionSaveResult(saved: true, snapshot);
        }
        finally
        {
            gate.Release();
        }
    }

    private static SessionDocument Encode(GameSessionSnapshot snapshot) => new()
    {
        FormatVersion = 4,
        SessionId = snapshot.Key.SessionId,
        ActorId = snapshot.Key.ActorId,
        Revision = snapshot.Revision,
        Messages = snapshot.Messages.Select(AgentMessageCodec.Encode).ToList(),
        ProcessedInputIds = snapshot.ProcessedInputIds.ToList(),
        PendingInputId = snapshot.PendingInputId,
        LastMoment = snapshot.LastMoment is null ? null : MomentDocument.Encode(snapshot.LastMoment.Value),
        ExtensionState = new Dictionary<string, string>(snapshot.ExtensionState, StringComparer.Ordinal),
        UsageRecords = snapshot.UsageLedger.Records.Select(UsageRecordDocument.Encode).ToList(),
        UsageRecentRecordCapacity = snapshot.UsageLedger.RecentRecordCapacity,
        UsageTotalRecordCount = snapshot.UsageLedger.TotalRecordCount,
        UsageTotals = snapshot.UsageLedger.TotalsByCause
            .Select(pair => UsageTotalsDocument.Encode(pair.Key, pair.Value))
            .ToList(),
    };

    private static void ValidateKey(GameSessionKey key)
    {
        if (string.IsNullOrWhiteSpace(key.SessionId) || string.IsNullOrWhiteSpace(key.ActorId))
        {
            throw new ArgumentException("A valid game session key is required.", nameof(key));
        }
    }

    private static string IdentityFor(GameSessionKey key) => string.Concat(
        key.SessionId.Length.ToString(CultureInfo.InvariantCulture),
        ":",
        key.SessionId,
        key.ActorId.Length.ToString(CultureInfo.InvariantCulture),
        ":",
        key.ActorId);

    private static GameSessionSnapshot? Decode(SessionDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        if (document.FormatVersion is not (1 or 2 or 3 or 4))
        {
            throw new PersistenceException($"Unsupported session format version '{document.FormatVersion}'.");
        }

        return FileStore.DecodeDocument(
            "session document",
            () => new GameSessionSnapshot(
                new GameSessionKey(document.SessionId, document.ActorId),
                document.Revision,
                (document.Messages ?? new List<MessageDocument>()).Select(AgentMessageCodec.Decode).ToArray(),
                document.ProcessedInputIds ?? new List<string>(),
                document.LastMoment?.Decode(),
                document.ExtensionState ?? new Dictionary<string, string>(StringComparer.Ordinal),
                document.FormatVersion >= 2 ? document.PendingInputId : null,
                document.FormatVersion >= 3
                    ? DecodeUsageLedger(document)
                    : null));
    }

    private static GameSessionUsageLedger DecodeUsageLedger(SessionDocument document)
    {
        var records = (document.UsageRecords ?? new List<UsageRecordDocument>())
            .Select(record => record.Decode())
            .ToArray();
        var capacity = document.UsageRecentRecordCapacity > 0
            ? document.UsageRecentRecordCapacity
            : GameSessionUsageLedger.DefaultRecentRecordCapacity;
        if (document.UsageTotals is null && document.UsageTotalRecordCount == 0)
        {
            // Early v3 previews persisted only raw records. Fold them into the bounded representation.
            return new GameSessionUsageLedger(records, capacity);
        }

        return GameSessionUsageLedger.Restore(
            records,
            DecodeUsageTotals(document.UsageTotals),
            document.UsageTotalRecordCount,
            capacity);
    }

    private static IReadOnlyDictionary<GameSessionUsageCause, GameSessionUsageTotals> DecodeUsageTotals(
        IReadOnlyList<UsageTotalsDocument>? documents)
    {
        var totals = new Dictionary<GameSessionUsageCause, GameSessionUsageTotals>();
        foreach (var document in documents ?? Array.Empty<UsageTotalsDocument>())
        {
            var cause = (GameSessionUsageCause)document.Cause;
            if (!totals.TryAdd(cause, document.Decode()))
            {
                throw new ArgumentException($"Duplicate cumulative usage cause '{cause}'.", nameof(documents));
            }
        }

        return totals;
    }

    private sealed class SessionDocument
    {
        public int FormatVersion { get; set; }

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public long Revision { get; set; }

        public List<MessageDocument>? Messages { get; set; }

        public List<string>? ProcessedInputIds { get; set; }

        public string? PendingInputId { get; set; }

        public MomentDocument? LastMoment { get; set; }

        public Dictionary<string, string>? ExtensionState { get; set; }

        public List<UsageRecordDocument>? UsageRecords { get; set; }

        public int UsageRecentRecordCapacity { get; set; }

        public long UsageTotalRecordCount { get; set; }

        public List<UsageTotalsDocument>? UsageTotals { get; set; }
    }

    private sealed class UsageRecordDocument
    {
        public string RecordId { get; set; } = string.Empty;

        public int Cause { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long CacheReadTokens { get; set; }

        public long CacheWriteTokens { get; set; }

        public long? ReasoningTokens { get; set; }

        public long? CacheWriteOneHourTokens { get; set; }

        public double InputCost { get; set; }

        public double OutputCost { get; set; }

        public double CacheReadCost { get; set; }

        public double CacheWriteCost { get; set; }

        public bool? CostKnown { get; set; }

        public string? RunId { get; set; }

        public string? InputId { get; set; }

        public string? DetailsJson { get; set; }

        public static UsageRecordDocument Encode(GameSessionUsageRecord record) => new()
        {
            RecordId = record.RecordId,
            Cause = (int)record.Cause,
            InputTokens = record.Usage.InputTokens,
            OutputTokens = record.Usage.OutputTokens,
            CacheReadTokens = record.Usage.CacheReadTokens,
            CacheWriteTokens = record.Usage.CacheWriteTokens,
            ReasoningTokens = record.Usage.ReasoningTokens,
            CacheWriteOneHourTokens = record.Usage.CacheWriteOneHourTokens,
            InputCost = record.Usage.Cost.Input,
            OutputCost = record.Usage.Cost.Output,
            CacheReadCost = record.Usage.Cost.CacheRead,
            CacheWriteCost = record.Usage.Cost.CacheWrite,
            CostKnown = record.Usage.Cost.IsKnown,
            RunId = record.RunId,
            InputId = record.InputId,
            DetailsJson = record.DetailsJson,
        };

        public GameSessionUsageRecord Decode() => new(
            RecordId,
            (GameSessionUsageCause)Cause,
            new ModelUsage(
                InputTokens,
                OutputTokens,
                CacheReadTokens,
                CacheWriteTokens,
                ReasoningTokens,
                CacheWriteOneHourTokens,
                CostKnown.HasValue
                    ? new ModelCost(InputCost, OutputCost, CacheReadCost, CacheWriteCost, CostKnown.Value)
                    : new ModelCost(InputCost, OutputCost, CacheReadCost, CacheWriteCost)),
            RunId,
            InputId,
            DetailsJson);
    }

    private sealed class UsageTotalsDocument
    {
        public int Cause { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long CacheReadTokens { get; set; }

        public long CacheWriteTokens { get; set; }

        public long ReasoningTokens { get; set; }

        public long CacheWriteOneHourTokens { get; set; }

        public double InputCost { get; set; }

        public double OutputCost { get; set; }

        public double CacheReadCost { get; set; }

        public double CacheWriteCost { get; set; }

        public bool? CostKnown { get; set; }

        public static UsageTotalsDocument Encode(
            GameSessionUsageCause cause,
            GameSessionUsageTotals totals) => new()
            {
                Cause = (int)cause,
                InputTokens = totals.InputTokens,
                OutputTokens = totals.OutputTokens,
                CacheReadTokens = totals.CacheReadTokens,
                CacheWriteTokens = totals.CacheWriteTokens,
                ReasoningTokens = totals.ReasoningTokens,
                CacheWriteOneHourTokens = totals.CacheWriteOneHourTokens,
                InputCost = totals.InputCost,
                OutputCost = totals.OutputCost,
                CacheReadCost = totals.CacheReadCost,
                CacheWriteCost = totals.CacheWriteCost,
                CostKnown = totals.CostKnown,
            };

        public GameSessionUsageTotals Decode() => new(
            InputTokens,
            OutputTokens,
            CacheReadTokens,
            CacheWriteTokens,
            ReasoningTokens,
            CacheWriteOneHourTokens,
            InputCost,
            OutputCost,
            CacheReadCost,
            CacheWriteCost,
            CostKnown
            ?? InputCost != 0
            || OutputCost != 0
            || CacheReadCost != 0
            || CacheWriteCost != 0);
    }
}

internal sealed class MomentDocument
{
    public string TimelineId { get; set; } = string.Empty;

    public long Tick { get; set; }

    public string? CalendarJson { get; set; }

    public static MomentDocument Encode(GameMoment moment) => new()
    {
        TimelineId = moment.TimelineId,
        Tick = moment.Tick,
        CalendarJson = moment.CalendarJson,
    };

    public GameMoment Decode() => new(TimelineId, Tick, CalendarJson);
}

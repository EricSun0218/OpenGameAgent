using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Persistence;

public sealed class FileGameActionJournal : IGameActionJournal
{
    private const string Suffix = ".action.json";
    private readonly FileStore _files;
    private readonly int _maximumEntries;
    private readonly SemaphoreSlim _capacityGate = new(1, 1);

    public FileGameActionJournal(
        string directory,
        int maximumEntries = 100_000,
        long maximumFileBytes = 4_000_000,
        int concurrencyStripes = 64)
    {
        if (maximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
        _maximumEntries = maximumEntries;
    }

    public async ValueTask<GameActionJournalEntry> ReserveAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        if (intent is null)
        {
            throw new ArgumentNullException(nameof(intent));
        }

        var gate = _files.GateFor(intent.OperationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(intent.OperationId + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(intent.OperationId, Suffix);
            var existing = await _files.ReadAsync<ActionDocument>(path, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var entry = Decode(existing);
                EnsureSameIntent(entry.Intent, intent);
                return new GameActionJournalEntry(
                    entry.Intent,
                    entry.Receipt,
                    created: false,
                    dispatched: entry.Dispatched);
            }

            await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var capacityLease = await _files.AcquireProcessLeaseAsync(
                    "action-journal-capacity",
                    cancellationToken).ConfigureAwait(false);
                var raced = await _files.ReadAsync<ActionDocument>(path, cancellationToken).ConfigureAwait(false);
                if (raced is not null)
                {
                    var entry = Decode(raced);
                    EnsureSameIntent(entry.Intent, intent);
                    return new GameActionJournalEntry(
                        entry.Intent,
                        entry.Receipt,
                        created: false,
                        dispatched: entry.Dispatched);
                }

                if (Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly).Take(_maximumEntries).Count() >= _maximumEntries)
                {
                    throw new GameRuntimeLimitException(nameof(_maximumEntries), "The file action journal reached its capacity.");
                }

                await _files.WriteAtomicAsync(
                    path,
                    Encode(intent, null, dispatched: false),
                    cancellationToken).ConfigureAwait(false);
                return new GameActionJournalEntry(intent, null, created: true, dispatched: false);
            }
            finally
            {
                _capacityGate.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameActionJournalEntry?> FindAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        }

        var gate = _files.GateFor(operationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(operationId + Suffix, cancellationToken).ConfigureAwait(false);
            var document = await _files.ReadAsync<ActionDocument>(
                _files.PathFor(operationId, Suffix),
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return null;
            }

            var entry = Decode(document);
            if (!string.Equals(entry.Intent.OperationId, operationId, StringComparison.Ordinal))
            {
                throw new PersistenceException("The action journal identity does not match the requested operation.");
            }

            return new GameActionJournalEntry(
                entry.Intent,
                entry.Receipt,
                created: false,
                dispatched: entry.Dispatched);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<bool> MarkDispatchedAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        }

        var gate = _files.GateFor(operationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(operationId + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(operationId, Suffix);
            var existing = await _files.ReadAsync<ActionDocument>(path, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Cannot dispatch an action without a matching intent.");
            var entry = Decode(existing);
            if (!string.Equals(entry.Intent.OperationId, operationId, StringComparison.Ordinal))
            {
                throw new PersistenceException("The action journal identity does not match the requested operation.");
            }

            if (entry.Receipt is not null || entry.Dispatched)
            {
                return false;
            }

            await _files.WriteAtomicAsync(
                path,
                Encode(entry.Intent, null, dispatched: true),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SaveReceiptAsync(GameActionReceipt receipt, CancellationToken cancellationToken)
    {
        if (receipt is null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }

        if (!receipt.IsFinal)
        {
            throw new ArgumentException("An uncertain receipt cannot close a journal entry.", nameof(receipt));
        }

        var gate = _files.GateFor(receipt.OperationId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(receipt.OperationId + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(receipt.OperationId, Suffix);
            var existing = await _files.ReadAsync<ActionDocument>(path, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Cannot save a receipt without a matching action intent.");
            var entry = Decode(existing);
            EnsureReceiptMatchesIntent(entry.Intent, receipt);
            if (entry.Receipt is not null && !Equivalent(entry.Receipt, receipt))
            {
                throw new InvalidOperationException("A final action receipt is immutable.");
            }

            if (!entry.Dispatched)
            {
                throw new InvalidOperationException("Cannot save a receipt before the action is marked as dispatched.");
            }

            await _files.WriteAtomicAsync(
                path,
                Encode(entry.Intent, receipt, dispatched: true),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<GameActionIntent>> ListPendingAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit < 0 || limit > _maximumEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (limit == 0)
        {
            return Array.Empty<GameActionIntent>();
        }

        var result = new List<GameActionIntent>();
        foreach (var path in Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal)
                     .Take(_maximumEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await _files.ReadAsync<ActionDocument>(path, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                continue;
            }

            var entry = Decode(document);
            _files.EnsurePathFor(path, entry.Intent.OperationId, Suffix, "action journal");
            if (entry.Receipt is null)
            {
                result.Add(entry.Intent);
                if (result.Count >= limit)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static ActionDocument Encode(
        GameActionIntent intent,
        GameActionReceipt? receipt,
        bool dispatched) => new()
        {
            FormatVersion = 2,
            Dispatched = dispatched,
            Intent = new IntentDocument
            {
                OperationId = intent.OperationId,
                InputId = intent.InputId,
                SessionId = intent.SessionId,
                ActorId = intent.ActorId,
                Action = intent.Action,
                ArgumentsJson = intent.ArgumentsJson,
                Moment = MomentDocument.Encode(intent.Moment),
                ExpectedRevision = intent.ExpectedRevision,
                GenerationId = intent.GenerationId,
            },
            Receipt = receipt is null ? null : new ReceiptDocument
            {
                OperationId = receipt.OperationId,
                Status = receipt.Status.ToString(),
                ResultJson = receipt.ResultJson,
                Moment = MomentDocument.Encode(receipt.Moment),
                StateRevision = receipt.StateRevision,
                Code = receipt.Code,
                Message = receipt.Message,
            },
        };

    private static GameActionJournalEntry Decode(ActionDocument document) =>
        FileStore.DecodeDocument("action journal document", () => DecodeCore(document));

    private static GameActionJournalEntry DecodeCore(ActionDocument document)
    {
        if (document.FormatVersion is not (1 or 2) || document.Intent is null)
        {
            throw new PersistenceException("The action journal document has an unsupported format.");
        }

        var intent = new GameActionIntent(
            document.Intent.OperationId,
            document.Intent.InputId,
            document.Intent.SessionId,
            document.Intent.ActorId,
            document.Intent.Action,
            document.Intent.ArgumentsJson,
            document.Intent.Moment?.Decode() ?? throw new PersistenceException("The action intent moment is missing."),
            document.Intent.ExpectedRevision,
            document.FormatVersion >= 2 ? document.Intent.GenerationId : null);
        GameActionReceipt? receipt = null;
        if (document.Receipt is not null)
        {
            if (!Enum.TryParse<GameActionStatus>(document.Receipt.Status, out var status)
                || !Enum.IsDefined(typeof(GameActionStatus), status))
            {
                throw new PersistenceException("The action receipt status is invalid.");
            }

            receipt = new GameActionReceipt(
                document.Receipt.OperationId,
                status,
                document.Receipt.ResultJson,
                document.Receipt.Moment?.Decode() ?? throw new PersistenceException("The action receipt moment is missing."),
                document.Receipt.StateRevision,
                document.Receipt.Code,
                document.Receipt.Message);
        }

        if (receipt is not null && !document.Dispatched)
        {
            throw new PersistenceException("A completed action journal entry was not marked as dispatched.");
        }

        if (receipt is not null
            && (!receipt.IsFinal
                || !string.Equals(intent.OperationId, receipt.OperationId, StringComparison.Ordinal)
                || intent.Moment != receipt.Moment))
        {
            throw new PersistenceException("The action receipt does not match its reserved intent.");
        }

        return new GameActionJournalEntry(intent, receipt, created: false, dispatched: document.Dispatched);
    }

    private static void EnsureSameIntent(GameActionIntent expected, GameActionIntent actual)
    {
        if (!string.Equals(expected.OperationId, actual.OperationId, StringComparison.Ordinal)
            || !string.Equals(expected.InputId, actual.InputId, StringComparison.Ordinal)
            || !string.Equals(expected.SessionId, actual.SessionId, StringComparison.Ordinal)
            || !string.Equals(expected.ActorId, actual.ActorId, StringComparison.Ordinal)
            || !string.Equals(expected.Action, actual.Action, StringComparison.Ordinal)
            || !string.Equals(expected.ArgumentsJson, actual.ArgumentsJson, StringComparison.Ordinal)
            || expected.Moment != actual.Moment
            || expected.ExpectedRevision != actual.ExpectedRevision
            || !string.Equals(expected.GenerationId, actual.GenerationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The operation ID is already reserved for a different action intent.");
        }
    }

    private static void EnsureReceiptMatchesIntent(GameActionIntent intent, GameActionReceipt receipt)
    {
        if (!string.Equals(intent.OperationId, receipt.OperationId, StringComparison.Ordinal)
            || intent.Moment != receipt.Moment)
        {
            throw new InvalidOperationException("The action receipt does not match its reserved intent.");
        }
    }

    private static bool Equivalent(GameActionReceipt left, GameActionReceipt right) =>
        left.Status == right.Status
        && left.Moment == right.Moment
        && left.StateRevision == right.StateRevision
        && string.Equals(left.ResultJson, right.ResultJson, StringComparison.Ordinal)
        && string.Equals(left.Code, right.Code, StringComparison.Ordinal)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal);

    private sealed class ActionDocument
    {
        public int FormatVersion { get; set; }

        public bool Dispatched { get; set; }

        public IntentDocument? Intent { get; set; }

        public ReceiptDocument? Receipt { get; set; }
    }

    private sealed class IntentDocument
    {
        public string OperationId { get; set; } = string.Empty;

        public string InputId { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string ArgumentsJson { get; set; } = "{}";

        public MomentDocument? Moment { get; set; }

        public long? ExpectedRevision { get; set; }

        public string? GenerationId { get; set; }
    }

    private sealed class ReceiptDocument
    {
        public string OperationId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string ResultJson { get; set; } = "{}";

        public MomentDocument? Moment { get; set; }

        public long? StateRevision { get; set; }

        public string? Code { get; set; }

        public string? Message { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Persistence;

public sealed class FileGameMailbox : IGameMailbox
{
    private const string Suffix = ".mailbox.json";
    private readonly FileStore _files;
    private readonly int _capacity;
    private readonly SemaphoreSlim _capacityGate = new(1, 1);

    public FileGameMailbox(
        string directory,
        int capacity = 100_000,
        long maximumFileBytes = 4_000_000,
        int concurrencyStripes = 64)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
        _capacity = capacity;
    }

    public async ValueTask<bool> EnqueueAsync(
        GameMailboxMessage message,
        CancellationToken cancellationToken)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var gate = _files.GateFor(message.MessageId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(message.MessageId + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(message.MessageId, Suffix);
            var existing = await _files.ReadAsync<MailboxDocument>(path, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureEquivalent(DecodeMessage(existing), message);
                return false;
            }

            await _capacityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var capacityLease = await _files.AcquireProcessLeaseAsync(
                    "mailbox-capacity",
                    cancellationToken).ConfigureAwait(false);
                var raced = await _files.ReadAsync<MailboxDocument>(path, cancellationToken).ConfigureAwait(false);
                if (raced is not null)
                {
                    EnsureEquivalent(DecodeMessage(raced), message);
                    return false;
                }

                if (Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly)
                    .Take(_capacity)
                    .Count() >= _capacity)
                {
                    throw new GameRuntimeLimitException(nameof(_capacity), "The file game mailbox reached its capacity.");
                }

                await _files.WriteAtomicAsync(
                    path,
                    Encode(message, DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
                return true;
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

    public async ValueTask<IReadOnlyList<GameMailboxDelivery>> ClaimAsync(
        string sessionId,
        string recipientId,
        int maximum,
        DateTimeOffset operationalNow,
        TimeSpan operationalLease,
        CancellationToken cancellationToken)
    {
        RequireId(sessionId, nameof(sessionId));
        RequireId(recipientId, nameof(recipientId));
        if (maximum < 0 || maximum > _capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        if (operationalLease <= TimeSpan.Zero || operationalLease > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(operationalLease));
        }

        DateTimeOffset leaseExpiresAt;
        try
        {
            leaseExpiresAt = operationalNow + operationalLease;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(operationalNow), exception.Message);
        }

        if (maximum == 0)
        {
            return Array.Empty<GameMailboxDelivery>();
        }

        var candidates = new List<(string Path, DateTimeOffset EnqueuedAt, string MessageId)>();
        foreach (var path in Directory.EnumerateFiles(_files.DirectoryPath, "*" + Suffix, SearchOption.TopDirectoryOnly)
                     .Take(_capacity))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await _files.ReadAsync<MailboxDocument>(path, cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                _files.EnsurePathFor(path, document.MessageId, Suffix, "mailbox message");
                ValidateDocument(document);
            }

            if (document is null
                || document.Completed
                || !string.Equals(document.SessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(document.RecipientId, recipientId, StringComparison.Ordinal)
                || (document.LeaseToken is not null && document.OperationalLeaseExpiresAt > operationalNow))
            {
                continue;
            }

            candidates.Add((path, document.EnqueuedAt, document.MessageId));
        }

        var deliveries = new List<GameMailboxDelivery>(Math.Min(maximum, candidates.Count));
        foreach (var candidate in candidates
                     .OrderBy(item => item.EnqueuedAt)
                     .ThenBy(item => item.MessageId, StringComparer.Ordinal))
        {
            if (deliveries.Count >= maximum)
            {
                break;
            }

            var gate = _files.GateFor(candidate.MessageId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var processLease = await _files.AcquireProcessLeaseAsync(candidate.MessageId + Suffix, cancellationToken).ConfigureAwait(false);
                var current = await _files.ReadAsync<MailboxDocument>(candidate.Path, cancellationToken).ConfigureAwait(false);
                if (current is not null)
                {
                    _files.EnsurePathFor(candidate.Path, current.MessageId, Suffix, "mailbox message");
                    ValidateDocument(current);
                }

                if (current is null
                    || current.Completed
                    || !string.Equals(current.SessionId, sessionId, StringComparison.Ordinal)
                    || !string.Equals(current.RecipientId, recipientId, StringComparison.Ordinal)
                    || (current.LeaseToken is not null && current.OperationalLeaseExpiresAt > operationalNow))
                {
                    continue;
                }

                current.Attempts = checked(current.Attempts + 1);
                current.LeaseToken = Guid.NewGuid().ToString("N");
                current.OperationalLeaseExpiresAt = leaseExpiresAt;
                await _files.WriteAtomicAsync(candidate.Path, current, cancellationToken).ConfigureAwait(false);
                deliveries.Add(new GameMailboxDelivery(
                    DecodeMessage(current),
                    current.LeaseToken,
                    current.Attempts,
                    current.OperationalLeaseExpiresAt));
            }
            finally
            {
                gate.Release();
            }
        }

        return deliveries;
    }

    public ValueTask CompleteAsync(string messageId, string leaseToken, CancellationToken cancellationToken) =>
        SettleAsync(messageId, leaseToken, complete: true, cancellationToken);

    public ValueTask AbandonAsync(string messageId, string leaseToken, CancellationToken cancellationToken) =>
        SettleAsync(messageId, leaseToken, complete: false, cancellationToken);

    private async ValueTask SettleAsync(
        string messageId,
        string leaseToken,
        bool complete,
        CancellationToken cancellationToken)
    {
        RequireId(messageId, nameof(messageId));
        RequireId(leaseToken, nameof(leaseToken));
        var gate = _files.GateFor(messageId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLease = await _files.AcquireProcessLeaseAsync(messageId + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(messageId, Suffix);
            var current = await _files.ReadAsync<MailboxDocument>(path, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The mailbox message does not exist.");
            ValidateDocument(current);
            if (!string.Equals(current.MessageId, messageId, StringComparison.Ordinal))
            {
                throw new PersistenceException("The mailbox message identity does not match its storage key.");
            }

            if (current.Completed)
            {
                throw new InvalidOperationException("The mailbox message is already complete.");
            }

            if (!string.Equals(current.LeaseToken, leaseToken, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The mailbox lease token is stale or invalid.");
            }

            current.Completed = complete;
            current.LeaseToken = null;
            current.OperationalLeaseExpiresAt = default;
            await _files.WriteAtomicAsync(path, current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static MailboxDocument Encode(GameMailboxMessage message, DateTimeOffset enqueuedAt) => new()
    {
        FormatVersion = 1,
        MessageId = message.MessageId,
        SessionId = message.SessionId,
        RecipientId = message.RecipientId,
        SenderId = message.SenderId,
        Kind = message.Kind,
        PayloadJson = message.PayloadJson,
        Moment = MomentDocument.Encode(message.Moment),
        CorrelationId = message.CorrelationId,
        EnqueuedAt = enqueuedAt,
    };

    private static GameMailboxMessage DecodeMessage(MailboxDocument document)
    {
        ValidateDocument(document);
        return FileStore.DecodeDocument(
            "mailbox document",
            () => new GameMailboxMessage(
                document.MessageId,
                document.SessionId,
                document.RecipientId,
                document.Kind,
                document.PayloadJson,
                document.Moment!.Decode(),
                document.SenderId,
                document.CorrelationId));
    }

    private static void ValidateDocument(MailboxDocument document)
    {
        if (document.FormatVersion != 1 || document.Moment is null)
        {
            throw new PersistenceException("The mailbox document has an unsupported format.");
        }

        if (document.Attempts < 0 || document.EnqueuedAt == default)
        {
            throw new PersistenceException("The mailbox document contains invalid delivery state.");
        }

        if (document.Completed && document.LeaseToken is not null)
        {
            throw new PersistenceException("A completed mailbox document cannot retain a lease.");
        }

        if (document.LeaseToken is null && document.OperationalLeaseExpiresAt != default
            || document.LeaseToken is not null
                && (string.IsNullOrWhiteSpace(document.LeaseToken)
                    || document.OperationalLeaseExpiresAt == default))
        {
            throw new PersistenceException("The mailbox document contains inconsistent lease state.");
        }
    }

    private static void EnsureEquivalent(GameMailboxMessage left, GameMailboxMessage right)
    {
        if (!string.Equals(left.MessageId, right.MessageId, StringComparison.Ordinal)
            || !string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
            || !string.Equals(left.RecipientId, right.RecipientId, StringComparison.Ordinal)
            || !string.Equals(left.SenderId, right.SenderId, StringComparison.Ordinal)
            || !string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
            || !string.Equals(left.PayloadJson, right.PayloadJson, StringComparison.Ordinal)
            || left.Moment != right.Moment
            || !string.Equals(left.CorrelationId, right.CorrelationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A mailbox message ID cannot be reused for different content.");
        }
    }

    private static void RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }
    }

    private sealed class MailboxDocument
    {
        public int FormatVersion { get; set; }

        public string MessageId { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string RecipientId { get; set; } = string.Empty;

        public string? SenderId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string PayloadJson { get; set; } = "{}";

        public MomentDocument? Moment { get; set; }

        public string? CorrelationId { get; set; }

        public DateTimeOffset EnqueuedAt { get; set; }

        public int Attempts { get; set; }

        public string? LeaseToken { get; set; }

        public DateTimeOffset OperationalLeaseExpiresAt { get; set; }

        public bool Completed { get; set; }
    }
}

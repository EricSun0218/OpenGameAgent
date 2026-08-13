using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent;

public sealed class GameMailboxMessage
{
    public GameMailboxMessage(
        string messageId,
        string sessionId,
        string recipientId,
        string kind,
        string payloadJson,
        GameMoment moment,
        string? senderId = null,
        string? correlationId = null)
    {
        MessageId = GameJson.RequireId(messageId, nameof(messageId));
        SessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        RecipientId = GameJson.RequireId(recipientId, nameof(recipientId));
        Kind = GameJson.RequireId(kind, nameof(kind));
        PayloadJson = GameJson.RequireValid(payloadJson, nameof(payloadJson));
        Moment = moment.EnsureValid(nameof(moment));
        SenderId = senderId is null ? null : GameJson.RequireId(senderId, nameof(senderId));
        CorrelationId = correlationId is null ? null : GameJson.RequireId(correlationId, nameof(correlationId));
    }

    public string MessageId { get; }

    public string SessionId { get; }

    public string RecipientId { get; }

    public string Kind { get; }

    public string PayloadJson { get; }

    public GameMoment Moment { get; }

    public string? SenderId { get; }

    public string? CorrelationId { get; }
}

public sealed class GameMailboxDelivery
{
    public GameMailboxDelivery(
        GameMailboxMessage message,
        string leaseToken,
        int attempt,
        DateTimeOffset operationalLeaseExpiresAt)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        LeaseToken = GameJson.RequireId(leaseToken, nameof(leaseToken));
        Attempt = attempt > 0 ? attempt : throw new ArgumentOutOfRangeException(nameof(attempt));
        OperationalLeaseExpiresAt = operationalLeaseExpiresAt;
    }

    public GameMailboxMessage Message { get; }

    public string LeaseToken { get; }

    public int Attempt { get; }

    public DateTimeOffset OperationalLeaseExpiresAt { get; }
}

public readonly struct GameMailboxRecipientKey : IEquatable<GameMailboxRecipientKey>
{
    public GameMailboxRecipientKey(string sessionId, string recipientId)
    {
        SessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        RecipientId = GameJson.RequireId(recipientId, nameof(recipientId));
    }

    public string SessionId { get; }

    public string RecipientId { get; }

    public bool Equals(GameMailboxRecipientKey other) =>
        string.Equals(SessionId, other.SessionId, StringComparison.Ordinal)
        && string.Equals(RecipientId, other.RecipientId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GameMailboxRecipientKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return ((SessionId is null ? 0 : StringComparer.Ordinal.GetHashCode(SessionId)) * 397)
                ^ (RecipientId is null ? 0 : StringComparer.Ordinal.GetHashCode(RecipientId));
        }
    }

    public override string ToString() => (SessionId ?? string.Empty) + ":" + (RecipientId ?? string.Empty);

    public static bool operator ==(GameMailboxRecipientKey left, GameMailboxRecipientKey right) =>
        left.Equals(right);

    public static bool operator !=(GameMailboxRecipientKey left, GameMailboxRecipientKey right) =>
        !left.Equals(right);

    internal GameMailboxRecipientKey EnsureValid(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(SessionId) || string.IsNullOrWhiteSpace(RecipientId))
        {
            throw new ArgumentException("A valid mailbox recipient key is required.", parameterName);
        }

        return this;
    }
}

public sealed class GameMailboxPendingStatus
{
    public GameMailboxPendingStatus(
        GameMailboxRecipientKey recipient,
        int readyCount,
        int leasedCount)
    {
        Recipient = recipient.EnsureValid(nameof(recipient));
        ReadyCount = readyCount >= 0 ? readyCount : throw new ArgumentOutOfRangeException(nameof(readyCount));
        LeasedCount = leasedCount >= 0 ? leasedCount : throw new ArgumentOutOfRangeException(nameof(leasedCount));
        IncompleteCount = checked(readyCount + leasedCount);
    }

    public GameMailboxRecipientKey Recipient { get; }

    public int ReadyCount { get; }

    public int LeasedCount { get; }

    public int IncompleteCount { get; }
}

public interface IGameMailbox
{
    ValueTask<bool> EnqueueAsync(GameMailboxMessage message, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameMailboxDelivery>> ClaimAsync(
        string sessionId,
        string recipientId,
        int maximum,
        DateTimeOffset operationalNow,
        TimeSpan operationalLease,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(string messageId, string leaseToken, CancellationToken cancellationToken);

    ValueTask AbandonAsync(string messageId, string leaseToken, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameMailboxPendingStatus>> GetPendingStatusAsync(
        IReadOnlyList<GameMailboxRecipientKey> recipients,
        DateTimeOffset operationalNow,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameMailbox : IGameMailbox
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _sequence;

    public InMemoryGameMailbox(int capacity = 100_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public ValueTask<bool> EnqueueAsync(GameMailboxMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(message.MessageId, out var existing))
            {
                EnsureEquivalent(existing.Message, message);
                return new ValueTask<bool>(false);
            }

            if (_entries.Count >= _capacity)
            {
                throw new GameRuntimeLimitException(nameof(_capacity), "The game mailbox reached its capacity.");
            }

            _entries.Add(message.MessageId, new Entry(message, checked(++_sequence)));
            return new ValueTask<bool>(true);
        }
    }

    public ValueTask<IReadOnlyList<GameMailboxDelivery>> ClaimAsync(
        string sessionId,
        string recipientId,
        int maximum,
        DateTimeOffset operationalNow,
        TimeSpan operationalLease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameJson.RequireId(sessionId, nameof(sessionId));
        GameJson.RequireId(recipientId, nameof(recipientId));
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

        lock (_gate)
        {
            var selected = _entries.Values
                .Where(entry => !entry.Completed)
                .Where(entry => entry.LeaseToken is null || entry.LeaseExpiresAt <= operationalNow)
                .Where(entry => string.Equals(entry.Message.SessionId, sessionId, StringComparison.Ordinal))
                .Where(entry => string.Equals(entry.Message.RecipientId, recipientId, StringComparison.Ordinal))
                .OrderBy(entry => entry.Sequence)
                .Take(maximum)
                .ToArray();
            var deliveries = new List<GameMailboxDelivery>(selected.Length);
            foreach (var entry in selected)
            {
                var nextAttempt = checked(entry.Attempts + 1);
                entry.LeaseToken = Guid.NewGuid().ToString("N");
                entry.LeaseExpiresAt = leaseExpiresAt;
                entry.Attempts = nextAttempt;
                deliveries.Add(new GameMailboxDelivery(
                    entry.Message,
                    entry.LeaseToken,
                    entry.Attempts,
                    entry.LeaseExpiresAt));
            }

            return new ValueTask<IReadOnlyList<GameMailboxDelivery>>(deliveries);
        }
    }

    public ValueTask CompleteAsync(string messageId, string leaseToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = RequireLease(messageId, leaseToken);
            entry.Completed = true;
            entry.LeaseToken = null;
            entry.LeaseExpiresAt = default;
        }

        return default;
    }

    public ValueTask AbandonAsync(string messageId, string leaseToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = RequireLease(messageId, leaseToken);
            entry.LeaseToken = null;
            entry.LeaseExpiresAt = default;
        }

        return default;
    }

    public ValueTask<IReadOnlyList<GameMailboxPendingStatus>> GetPendingStatusAsync(
        IReadOnlyList<GameMailboxRecipientKey> recipients,
        DateTimeOffset operationalNow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = GameMailboxPendingQuery.Validate(recipients);
        var counts = new Dictionary<GameMailboxRecipientKey, GameMailboxPendingQuery.Counts>();
        foreach (var recipient in requested)
        {
            counts[recipient] = default;
        }

        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Completed)
                {
                    continue;
                }

                var recipient = new GameMailboxRecipientKey(
                    entry.Message.SessionId,
                    entry.Message.RecipientId);
                if (!counts.TryGetValue(recipient, out var count))
                {
                    continue;
                }

                if (entry.LeaseToken is not null && entry.LeaseExpiresAt > operationalNow)
                {
                    count.Leased = checked(count.Leased + 1);
                }
                else
                {
                    count.Ready = checked(count.Ready + 1);
                }

                counts[recipient] = count;
            }
        }

        return new ValueTask<IReadOnlyList<GameMailboxPendingStatus>>(
            GameMailboxPendingQuery.Materialize(requested, counts));
    }

    private Entry RequireLease(string messageId, string leaseToken)
    {
        GameJson.RequireId(messageId, nameof(messageId));
        GameJson.RequireId(leaseToken, nameof(leaseToken));
        if (!_entries.TryGetValue(messageId, out var entry))
        {
            throw new InvalidOperationException("The mailbox message does not exist.");
        }

        if (entry.Completed)
        {
            throw new InvalidOperationException("The mailbox message is already complete.");
        }

        if (!string.Equals(entry.LeaseToken, leaseToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The mailbox lease token is stale or invalid.");
        }

        return entry;
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

    private sealed class Entry
    {
        public Entry(GameMailboxMessage message, long sequence)
        {
            Message = message;
            Sequence = sequence;
        }

        public GameMailboxMessage Message { get; }

        public long Sequence { get; }

        public int Attempts { get; set; }

        public string? LeaseToken { get; set; }

        public DateTimeOffset LeaseExpiresAt { get; set; }

        public bool Completed { get; set; }
    }
}

internal static class GameMailboxPendingQuery
{
    internal const int MaximumRecipients = 4_096;

    internal static GameMailboxRecipientKey[] Validate(
        IReadOnlyList<GameMailboxRecipientKey> recipients)
    {
        if (recipients is null)
        {
            throw new ArgumentNullException(nameof(recipients));
        }

        if (recipients.Count > MaximumRecipients)
        {
            throw new GameRuntimeLimitException(
                nameof(MaximumRecipients),
                "A mailbox pending query contains too many recipients.");
        }

        var copy = new GameMailboxRecipientKey[recipients.Count];
        for (var index = 0; index < recipients.Count; index++)
        {
            copy[index] = recipients[index].EnsureValid(nameof(recipients));
        }

        return copy;
    }

    internal static IReadOnlyList<GameMailboxPendingStatus> Materialize(
        IReadOnlyList<GameMailboxRecipientKey> requested,
        IReadOnlyDictionary<GameMailboxRecipientKey, Counts> counts)
    {
        var result = new GameMailboxPendingStatus[requested.Count];
        for (var index = 0; index < requested.Count; index++)
        {
            var recipient = requested[index];
            var count = counts[recipient];
            result[index] = new GameMailboxPendingStatus(recipient, count.Ready, count.Leased);
        }

        return Array.AsReadOnly(result);
    }

    internal struct Counts
    {
        public int Ready;

        public int Leased;
    }
}

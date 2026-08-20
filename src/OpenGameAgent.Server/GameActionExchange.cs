using System.Runtime.CompilerServices;

namespace OpenGameAgent.Server;

public sealed class GameActionExchangeOptions
{
    public int MaximumClaimsPerRequest { get; set; } = 256;

    public int MaximumJournalScan { get; set; } = 10_000;

    public int PollIntervalMilliseconds { get; set; } = 250;

    internal GameActionExchangeOptions CopyAndValidate()
    {
        if (MaximumClaimsPerRequest is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumClaimsPerRequest));
        }

        if (MaximumJournalScan < MaximumClaimsPerRequest || MaximumJournalScan > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumJournalScan));
        }

        if (PollIntervalMilliseconds is < 10 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(PollIntervalMilliseconds));
        }

        return new GameActionExchangeOptions
        {
            MaximumClaimsPerRequest = MaximumClaimsPerRequest,
            MaximumJournalScan = MaximumJournalScan,
            PollIntervalMilliseconds = PollIntervalMilliseconds,
        };
    }
}

public sealed class GameActionDelivery
{
    internal GameActionDelivery(GameActionIntent intent)
    {
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
    }

    public GameActionIntent Intent { get; }

    /// <summary>
    /// A durable external action is marked dispatched before it is exposed. The engine must reconcile
    /// the operation ID against its authoritative operation log before executing or resuming it.
    /// </summary>
    public bool RequiresReconciliation => true;
}

public enum GameActionExchangeStatus
{
    Prepared = 0,
    Dispatched = 1,
    Completed = 2,
}

public sealed class GameActionExchangeState
{
    internal GameActionExchangeState(GameActionJournalEntry entry)
    {
        Intent = entry.Intent;
        Receipt = entry.Receipt;
        Status = entry.Receipt is not null
            ? GameActionExchangeStatus.Completed
            : entry.Dispatched
                ? GameActionExchangeStatus.Dispatched
                : GameActionExchangeStatus.Prepared;
    }

    public GameActionExchangeStatus Status { get; }

    public GameActionIntent Intent { get; }

    public GameActionReceipt? Receipt { get; }

    public bool RequiresReconciliation => Status == GameActionExchangeStatus.Dispatched;
}

/// <summary>
/// Bridges the durable action dispatcher to an external authoritative game process. The same instance
/// is used as the dispatcher's handler and by the HTTP action endpoints.
/// </summary>
public sealed class GameActionExchange : IGameActionHandler
{
    private readonly IGameActionJournal _journal;
    private readonly GameActionExchangeOptions _options;
    private readonly object _changeGate = new();
    private TaskCompletionSource<object?> _changed = NewChangeSource();

    public GameActionExchange(
        IGameActionJournal journal,
        GameActionExchangeOptions? options = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _options = (options ?? new GameActionExchangeOptions()).CopyAndValidate();
    }

    public async ValueTask<GameActionReceipt> ExecuteAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var entry = await _journal.FindAsync(intent.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The external action was not reserved before dispatch.");
        EnsureIntentIdentity(entry.Intent, intent);
        if (!entry.Dispatched)
        {
            throw new InvalidOperationException("The external action must be marked dispatched before delivery.");
        }

        if (entry.Receipt is not null)
        {
            return entry.Receipt;
        }

        SignalChanged();
        while (true)
        {
            entry = await _journal.FindAsync(intent.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The external action disappeared after dispatch.");
            EnsureIntentIdentity(entry.Intent, intent);
            if (entry.Receipt is not null)
            {
                return entry.Receipt;
            }

            if (!entry.Dispatched)
            {
                throw new InvalidOperationException("The external action lost its durable dispatch marker.");
            }

            await WaitForChangeOrPollAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<GameActionReceipt?> RecoverAsync(
        GameActionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var entry = await _journal.FindAsync(intent.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The external action does not exist.");
        EnsureIntentIdentity(entry.Intent, intent);
        if (entry.Receipt is not null)
        {
            return entry.Receipt;
        }

        if (entry.Dispatched)
        {
            SignalChanged();
        }

        return null;
    }

    public async ValueTask<IReadOnlyList<GameActionDelivery>> ClaimPendingAsync(
        GameSessionKey key,
        int limit,
        CancellationToken cancellationToken)
    {
        key = ValidateKey(key);
        if (limit < 1 || limit > _options.MaximumClaimsPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var pending = await _journal.ListPendingAsync(
            _options.MaximumJournalScan,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The action journal returned no pending collection.");
        var result = new List<GameActionDelivery>(Math.Min(limit, pending.Count));
        foreach (var intent in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(intent.SessionId, key.SessionId, StringComparison.Ordinal)
                || !string.Equals(intent.ActorId, key.ActorId, StringComparison.Ordinal))
            {
                continue;
            }

            var entry = await _journal.FindAsync(intent.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A listed action disappeared from its journal.");
            EnsureIntentIdentity(entry.Intent, intent);
            if (entry.Dispatched && entry.Receipt is null)
            {
                result.Add(new GameActionDelivery(entry.Intent));
                if (result.Count == limit)
                {
                    break;
                }
            }
        }

        return result;
    }

    public async IAsyncEnumerable<GameActionDelivery> StreamPendingAsync(
        GameSessionKey key,
        int batchLimit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        key = ValidateKey(key);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var delivery in await ClaimPendingAsync(key, batchLimit, cancellationToken).ConfigureAwait(false))
            {
                if (emitted.Add(delivery.Intent.OperationId))
                {
                    if (emitted.Count > _options.MaximumJournalScan)
                    {
                        throw new GameRuntimeLimitException(
                            nameof(_options.MaximumJournalScan),
                            "The action stream exceeded its bounded operation history.");
                    }

                    yield return delivery;
                }
            }

            await WaitForChangeOrPollAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<GameActionExchangeState?> ReconcileAsync(
        GameSessionKey key,
        string operationId,
        CancellationToken cancellationToken)
    {
        key = ValidateKey(key);
        var entry = await _journal.FindAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        EnsureOwnedBy(entry.Intent, key);
        return new GameActionExchangeState(entry);
    }

    public async ValueTask<GameActionReceipt> SubmitReceiptAsync(
        GameSessionKey key,
        long? expectedRevision,
        string? generationId,
        GameActionReceipt receipt,
        CancellationToken cancellationToken)
    {
        key = ValidateKey(key);
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.IsFinal)
        {
            throw new ArgumentException("An external action receipt must be final.", nameof(receipt));
        }

        var entry = await _journal.FindAsync(receipt.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The external action operation does not exist.");
        EnsureOwnedBy(entry.Intent, key);
        if (!entry.Dispatched)
        {
            throw new InvalidOperationException("A receipt cannot be submitted before durable dispatch.");
        }

        if (entry.Intent.ExpectedRevision != expectedRevision)
        {
            throw new InvalidOperationException("The receipt expected revision does not match the reserved intent.");
        }

        if (!string.Equals(entry.Intent.GenerationId, generationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The receipt generation does not match the reserved intent.");
        }

        if (entry.Intent.Moment != receipt.Moment)
        {
            throw new InvalidOperationException("The receipt game moment does not match the reserved intent.");
        }

        if (receipt.Status == GameActionStatus.Committed
            && entry.Intent.ExpectedRevision is { } minimumRevision
            && (receipt.StateRevision is null || receipt.StateRevision < minimumRevision))
        {
            throw new InvalidOperationException("A committed receipt must report a state revision at or after its expected revision.");
        }

        await _journal.SaveReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
        var stored = await _journal.FindAsync(receipt.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The action journal lost the submitted receipt.");
        if (stored.Receipt is null)
        {
            throw new InvalidOperationException("The action journal did not retain the submitted receipt.");
        }

        SignalChanged();
        return stored.Receipt;
    }

    private async Task WaitForChangeOrPollAsync(CancellationToken cancellationToken)
    {
        Task changed;
        lock (_changeGate)
        {
            changed = _changed.Task;
        }

        var poll = Task.Delay(_options.PollIntervalMilliseconds, cancellationToken);
        await Task.WhenAny(changed, poll).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void SignalChanged()
    {
        TaskCompletionSource<object?> changed;
        lock (_changeGate)
        {
            changed = _changed;
            _changed = NewChangeSource();
        }

        changed.TrySetResult(null);
    }

    private static TaskCompletionSource<object?> NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static GameSessionKey ValidateKey(GameSessionKey key) =>
        new(key.SessionId, key.ActorId);

    private static void EnsureOwnedBy(GameActionIntent intent, GameSessionKey key)
    {
        if (!string.Equals(intent.SessionId, key.SessionId, StringComparison.Ordinal)
            || !string.Equals(intent.ActorId, key.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The action operation belongs to a different session or actor.");
        }
    }

    private static void EnsureIntentIdentity(GameActionIntent expected, GameActionIntent actual)
    {
        if (!string.Equals(expected.OperationId, actual.OperationId, StringComparison.Ordinal)
            || !string.Equals(expected.InputId, actual.InputId, StringComparison.Ordinal)
            || !string.Equals(expected.SessionId, actual.SessionId, StringComparison.Ordinal)
            || !string.Equals(expected.ActorId, actual.ActorId, StringComparison.Ordinal)
            || !string.Equals(expected.Action, actual.Action, StringComparison.Ordinal)
            || !string.Equals(expected.ArgumentsJson, actual.ArgumentsJson, StringComparison.Ordinal)
            || expected.Moment != actual.Moment
            || expected.ExpectedRevision != actual.ExpectedRevision
            || !string.Equals(expected.GenerationId, actual.GenerationId, StringComparison.Ordinal)
            || !string.Equals(expected.ConflictKey, actual.ConflictKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The external action intent does not match its durable reservation.");
        }
    }
}

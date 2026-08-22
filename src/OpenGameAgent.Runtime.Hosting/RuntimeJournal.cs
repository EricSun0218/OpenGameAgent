using OpenGameAgent.Runtime.Protocol;

namespace OpenGameAgent.Runtime.Hosting;

public sealed class InMemoryGameRuntimeEventJournal
{
    private readonly object _gate = new();
    private readonly int _maximumSessions;
    private readonly int _maximumEventsPerSession;
    private readonly int _maximumOpenItemsPerSession;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<GameSessionKey, SessionState> _sessions = new();

    public InMemoryGameRuntimeEventJournal(
        int maximumSessions = 10_000,
        int maximumEventsPerSession = 10_000,
        int maximumOpenItemsPerSession = 1_024,
        Func<DateTimeOffset>? clock = null)
    {
        if (maximumSessions is < 1 or > 1_000_000
            || maximumEventsPerSession is < 16 or > 1_000_000
            || maximumOpenItemsPerSession is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSessions));
        }

        _maximumSessions = maximumSessions;
        _maximumEventsPerSession = maximumEventsPerSession;
        _maximumOpenItemsPerSession = maximumOpenItemsPerSession;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<GameRuntimeEventEnvelope> Publish(GameRuntimeEventDraft draft)
    {
        if (draft is null)
        {
            throw new ArgumentNullException(nameof(draft));
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(draft.Key, out var state))
            {
                if (_sessions.Count >= _maximumSessions)
                {
                    throw new InvalidOperationException("The runtime event journal reached its session capacity.");
                }

                state = new SessionState();
                _sessions.Add(draft.Key, state);
            }

            var published = new List<GameRuntimeEventEnvelope>();
            if (draft.Terminal)
            {
                foreach (var open in state.OpenItems.Values
                             .Where(value => string.Equals(value.RunId, draft.RunId, StringComparison.Ordinal))
                             .OrderBy(value => value.ItemId, StringComparer.Ordinal)
                             .ToArray())
                {
                    var reconciliation = new GameRuntimeEventDraft(
                        draft.Key,
                        draft.InputId,
                        GameRuntimeEventKind.Item,
                        GameRuntimeLifecycle.Completed,
                        "item_interrupted",
                        "{\"status\":\"interrupted\",\"reason\":\"run_terminal_reconciliation\"}",
                        open.RunId,
                        open.Turn,
                        open.TurnId,
                        open.ItemId,
                        open.ItemKind);
                    published.Add(Append(state, reconciliation));
                }
            }

            published.Add(Append(state, draft));
            var previous = state.Signal;
            state.Signal = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult(state.LastSequence);
            return published.AsReadOnly();
        }
    }

    public GameRuntimeEventPage Read(
        GameSessionKey key,
        long afterSequence,
        int maximum)
    {
        key = new GameSessionKey(key.SessionId, key.ActorId);
        if (afterSequence < 0 || maximum is < 1 or > GameRuntimeProtocol.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var state))
            {
                return new GameRuntimeEventPage(
                    key.SessionId,
                    key.ActorId,
                    afterSequence,
                    1,
                    0,
                    afterSequence,
                    gap: false,
                    Array.Empty<GameRuntimeEventEnvelope>());
            }

            var first = state.Events.Count == 0 ? state.LastSequence + 1 : state.Events[0].Sequence;
            var gap = afterSequence + 1 < first;
            var effectiveAfter = gap ? first - 1 : afterSequence;
            var values = state.Events
                .Where(value => value.Sequence > effectiveAfter)
                .Take(maximum)
                .ToArray();
            var next = values.Length == 0 ? afterSequence : values[^1].Sequence;
            return new GameRuntimeEventPage(
                key.SessionId,
                key.ActorId,
                afterSequence,
                first,
                state.LastSequence,
                next,
                gap,
                values);
        }
    }

    public bool IsKnownEvent(GameSessionKey key, string eventId)
        => IsKnownEvent(key, eventId, inputId: null);

    public bool IsKnownEvent(GameSessionKey key, string eventId, string? inputId)
    {
        key = new GameSessionKey(key.SessionId, key.ActorId);
        if (!GameRuntimeIds.TryReadEventSequence(eventId, out var sequence))
        {
            return false;
        }

        lock (_gate)
        {
            return _sessions.TryGetValue(key, out var state)
                   && state.Events.Any(value => value.Sequence == sequence
                                                && string.Equals(value.EventId, eventId, StringComparison.Ordinal)
                                                && (inputId is null
                                                    || string.Equals(value.InputId, inputId, StringComparison.Ordinal)));
        }
    }

    public async ValueTask<long> WaitForChangeAsync(
        GameSessionKey key,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        key = new GameSessionKey(key.SessionId, key.ActorId);
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        Task<long> signal;
        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out var state) && state.LastSequence > afterSequence)
            {
                return state.LastSequence;
            }

            if (!_sessions.TryGetValue(key, out state))
            {
                if (_sessions.Count >= _maximumSessions)
                {
                    throw new InvalidOperationException("The runtime event journal reached its session capacity.");
                }

                state = new SessionState();
                _sessions.Add(key, state);
            }

            signal = state.Signal.Task;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            return await signal.ConfigureAwait(false);
        }

        var cancelled = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<long>)state!).TrySetCanceled(),
            cancelled);
        return await await Task.WhenAny(signal, cancelled.Task).ConfigureAwait(false);
    }

    private GameRuntimeEventEnvelope Append(SessionState state, GameRuntimeEventDraft draft)
    {
        if (draft.EventKind == GameRuntimeEventKind.Item)
        {
            UpdateItemState(state, draft);
        }

        var sequence = checked(++state.LastSequence);
        var value = new GameRuntimeEventEnvelope(
            GameRuntimeProtocol.Version,
            GameRuntimeIds.Event(draft.Key, sequence, draft.InputId),
            sequence,
            _clock(),
            draft.Key.SessionId,
            draft.Key.ActorId,
            draft.InputId,
            draft.EventKind,
            draft.Lifecycle,
            draft.Name,
            draft.PayloadJson,
            draft.RunId,
            draft.Turn,
            draft.TurnId,
            draft.ItemId,
            draft.ItemKind,
            draft.Terminal);
        state.Events.Add(value);
        if (state.Events.Count > _maximumEventsPerSession)
        {
            state.Events.RemoveRange(0, state.Events.Count - _maximumEventsPerSession);
        }

        return value;
    }

    private void UpdateItemState(SessionState state, GameRuntimeEventDraft draft)
    {
        var id = draft.ItemId!;
        if (draft.Lifecycle == GameRuntimeLifecycle.Started)
        {
            if (state.OpenItems.ContainsKey(id))
            {
                throw new InvalidOperationException("A runtime item cannot start twice.");
            }

            if (state.OpenItems.Count >= _maximumOpenItemsPerSession)
            {
                throw new InvalidOperationException("The runtime event journal reached its open-item capacity.");
            }

            state.OpenItems.Add(id, new OpenItem(draft));
            return;
        }

        if (!state.OpenItems.TryGetValue(id, out var open)
            || open.ItemKind != draft.ItemKind
            || !string.Equals(open.RunId, draft.RunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A runtime item delta or completion has no matching start event.");
        }

        if (draft.Lifecycle == GameRuntimeLifecycle.Completed)
        {
            state.OpenItems.Remove(id);
        }
    }

    private sealed class SessionState
    {
        internal List<GameRuntimeEventEnvelope> Events { get; } = new();
        internal Dictionary<string, OpenItem> OpenItems { get; } = new(StringComparer.Ordinal);
        internal long LastSequence { get; set; }
        internal TaskCompletionSource<long> Signal { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class OpenItem
    {
        internal OpenItem(GameRuntimeEventDraft draft)
        {
            RunId = draft.RunId!;
            Turn = draft.Turn;
            TurnId = draft.TurnId;
            ItemId = draft.ItemId!;
            ItemKind = draft.ItemKind!.Value;
        }

        internal string RunId { get; }
        internal int? Turn { get; }
        internal string? TurnId { get; }
        internal string ItemId { get; }
        internal GameRuntimeItemKind ItemKind { get; }
    }
}

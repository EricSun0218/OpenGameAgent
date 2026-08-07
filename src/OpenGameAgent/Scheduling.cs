using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent;

public sealed class GameSignal
{
    public GameSignal(
        string signalId,
        string sessionId,
        string kind,
        string payloadJson,
        GameMoment moment,
        IReadOnlyCollection<string>? subjects = null,
        IReadOnlyCollection<string>? causes = null)
    {
        SignalId = GameJson.RequireId(signalId, nameof(signalId));
        SessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        Kind = GameJson.RequireId(kind, nameof(kind));
        PayloadJson = GameJson.RequireValid(payloadJson, nameof(payloadJson));
        Moment = moment.EnsureValid(nameof(moment));
        Subjects = CopyIds(subjects, nameof(subjects));
        Causes = CopyIds(causes, nameof(causes));
    }

    public string SignalId { get; }

    public string SessionId { get; }

    public string Kind { get; }

    public string PayloadJson { get; }

    public GameMoment Moment { get; }

    public IReadOnlyCollection<string> Subjects { get; }

    public IReadOnlyCollection<string> Causes { get; }

    private static IReadOnlyCollection<string> CopyIds(IReadOnlyCollection<string>? values, string parameterName)
    {
        var copied = (values ?? Array.Empty<string>())
            .Select(value => GameJson.RequireId(value, parameterName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Array.AsReadOnly(copied);
    }
}

public sealed class ScheduledGameTrigger
{
    public ScheduledGameTrigger(
        string triggerId,
        string sessionId,
        string kind,
        string payloadJson,
        GameMoment due,
        long? intervalTicks = null,
        int? maximumOccurrences = null)
    {
        if (intervalTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalTicks));
        }

        if (maximumOccurrences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOccurrences));
        }

        TriggerId = GameJson.RequireId(triggerId, nameof(triggerId));
        SessionId = GameJson.RequireId(sessionId, nameof(sessionId));
        Kind = GameJson.RequireId(kind, nameof(kind));
        PayloadJson = GameJson.RequireValid(payloadJson, nameof(payloadJson));
        Due = due.EnsureValid(nameof(due));
        IntervalTicks = intervalTicks;
        MaximumOccurrences = maximumOccurrences;
    }

    public string TriggerId { get; }

    public string SessionId { get; }

    public string Kind { get; }

    public string PayloadJson { get; }

    public GameMoment Due { get; }

    public long? IntervalTicks { get; }

    public int? MaximumOccurrences { get; }
}

public sealed class ScheduledGameOccurrence
{
    public ScheduledGameOccurrence(ScheduledGameTrigger trigger, int occurrence, GameMoment due)
    {
        Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        Occurrence = occurrence > 0 ? occurrence : throw new ArgumentOutOfRangeException(nameof(occurrence));
        Due = due.EnsureValid(nameof(due));
        if (!string.Equals(trigger.Due.TimelineId, Due.TimelineId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A scheduled occurrence cannot change timelines.", nameof(due));
        }

        long expectedTick;
        try
        {
            expectedTick = trigger.IntervalTicks is null
                ? trigger.Due.Tick
                : checked(trigger.Due.Tick + checked(trigger.IntervalTicks.Value * (occurrence - 1L)));
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrence), exception.Message);
        }

        if ((trigger.IntervalTicks is null && occurrence != 1) || Due.Tick != expectedTick)
        {
            throw new ArgumentException("The scheduled occurrence is inconsistent with its trigger.", nameof(due));
        }
    }

    public ScheduledGameTrigger Trigger { get; }

    public int Occurrence { get; }

    public GameMoment Due { get; }
}

public sealed class ScheduledGameTriggerState
{
    public ScheduledGameTriggerState(
        ScheduledGameTrigger trigger,
        GameMoment nextDue,
        int occurrences)
    {
        Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        NextDue = nextDue.EnsureValid(nameof(nextDue));
        if (!string.Equals(trigger.Due.TimelineId, nextDue.TimelineId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A restored trigger cannot change timelines.", nameof(nextDue));
        }

        if (occurrences < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrences));
        }

        if (trigger.IntervalTicks is null)
        {
            if (occurrences != 0 || nextDue.Tick != trigger.Due.Tick)
            {
                throw new ArgumentException("An active one-shot trigger cannot have prior occurrences.", nameof(occurrences));
            }
        }
        else
        {
            if (trigger.MaximumOccurrences is { } maximum && occurrences >= maximum)
            {
                throw new ArgumentException("A completed recurring trigger cannot be restored as active.", nameof(occurrences));
            }

            long expectedNextTick;
            try
            {
                expectedNextTick = checked(trigger.Due.Tick + checked(trigger.IntervalTicks.Value * occurrences));
            }
            catch (OverflowException exception)
            {
                throw new ArgumentOutOfRangeException(nameof(occurrences), exception.Message);
            }

            if (nextDue.Tick != expectedNextTick)
            {
                throw new ArgumentException("The restored trigger position is inconsistent with its interval.", nameof(nextDue));
            }
        }

        Occurrences = occurrences;
    }

    public ScheduledGameTrigger Trigger { get; }

    public GameMoment NextDue { get; }

    public int Occurrences { get; }
}

public sealed class GameTimeScheduler
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TriggerState> _triggers = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public GameTimeScheduler(int capacity = 100_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public GameTimeScheduler(IEnumerable<ScheduledGameTriggerState> state, int capacity = 100_000)
        : this(capacity)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        var copied = state.ToArray();
        if (copied.Length > _capacity)
        {
            throw new GameRuntimeLimitException(nameof(capacity), "The restored game-time schedule exceeds its capacity.");
        }

        foreach (var item in copied)
        {
            if (item is null)
            {
                throw new ArgumentException("A restored schedule cannot contain null state.", nameof(state));
            }

            if (_triggers.ContainsKey(item.Trigger.TriggerId))
            {
                throw new ArgumentException($"Duplicate restored trigger ID '{item.Trigger.TriggerId}'.", nameof(state));
            }

            _triggers.Add(item.Trigger.TriggerId, new TriggerState(item.Trigger, item.NextDue, item.Occurrences));
        }
    }

    public IReadOnlyList<ScheduledGameTriggerState> CaptureState()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_triggers.Values
                .OrderBy(state => state.NextDue.Tick)
                .ThenBy(state => state.Trigger.TriggerId, StringComparer.Ordinal)
                .Select(state => new ScheduledGameTriggerState(
                    state.Trigger,
                    state.NextDue,
                    state.Occurrences))
                .ToArray());
        }
    }

    public void Schedule(ScheduledGameTrigger trigger)
    {
        if (trigger is null)
        {
            throw new ArgumentNullException(nameof(trigger));
        }

        lock (_gate)
        {
            if (_triggers.ContainsKey(trigger.TriggerId))
            {
                throw new InvalidOperationException("The trigger ID is already scheduled.");
            }

            if (_triggers.Count >= _capacity)
            {
                throw new GameRuntimeLimitException(nameof(_capacity), "The game-time scheduler reached its capacity.");
            }

            _triggers.Add(trigger.TriggerId, new TriggerState(trigger));
        }
    }

    public bool Cancel(string triggerId)
    {
        lock (_gate)
        {
            return _triggers.Remove(GameJson.RequireId(triggerId, nameof(triggerId)));
        }
    }

    public IReadOnlyList<ScheduledGameOccurrence> Advance(
        string sessionId,
        GameMoment fromExclusive,
        GameMoment toInclusive,
        int maximumOccurrences)
    {
        GameJson.RequireId(sessionId, nameof(sessionId));
        fromExclusive.EnsureValid(nameof(fromExclusive));
        toInclusive.EnsureValid(nameof(toInclusive));
        if (!string.Equals(fromExclusive.TimelineId, toInclusive.TimelineId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A time advance cannot cross timelines.", nameof(toInclusive));
        }

        if (toInclusive.Tick < fromExclusive.Tick)
        {
            throw new ArgumentException("Game time cannot advance backwards.", nameof(toInclusive));
        }

        if (maximumOccurrences < 0 || maximumOccurrences > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOccurrences));
        }

        if (maximumOccurrences == 0)
        {
            return Array.Empty<ScheduledGameOccurrence>();
        }

        lock (_gate)
        {
            var due = new List<ScheduledGameOccurrence>();
            var advances = new List<TriggerAdvance>();
            foreach (var state in _triggers.Values
                         .Where(state => string.Equals(state.Trigger.SessionId, sessionId, StringComparison.Ordinal))
                         .Where(state => string.Equals(state.Trigger.Due.TimelineId, toInclusive.TimelineId, StringComparison.Ordinal))
                         .OrderBy(state => state.NextDue.Tick)
                         .ThenBy(state => state.Trigger.TriggerId, StringComparer.Ordinal))
            {
                var nextDue = state.NextDue;
                var occurrences = state.Occurrences;
                var complete = false;
                while (nextDue.Tick <= toInclusive.Tick)
                {
                    if (due.Count >= maximumOccurrences)
                    {
                        throw new GameRuntimeLimitException(nameof(maximumOccurrences), "The time advance produced too many occurrences.");
                    }

                    occurrences = checked(occurrences + 1);
                    var occurrenceDue = nextDue.CalendarJson is null && nextDue.Tick == toInclusive.Tick
                        ? new GameMoment(nextDue.TimelineId, nextDue.Tick, toInclusive.CalendarJson)
                        : nextDue;
                    due.Add(new ScheduledGameOccurrence(state.Trigger, occurrences, occurrenceDue));

                    if (state.Trigger.IntervalTicks is null
                        || (state.Trigger.MaximumOccurrences is { } maximum && occurrences >= maximum))
                    {
                        complete = true;
                        break;
                    }

                    nextDue = new GameMoment(
                        nextDue.TimelineId,
                        checked(nextDue.Tick + state.Trigger.IntervalTicks.Value));
                }

                advances.Add(new TriggerAdvance(state, nextDue, occurrences, complete));
            }

            foreach (var advance in advances)
            {
                advance.State.NextDue = advance.NextDue;
                advance.State.Occurrences = advance.Occurrences;
                advance.State.Complete = advance.Complete;
            }

            foreach (var completed in _triggers.Where(pair => pair.Value.Complete).Select(pair => pair.Key).ToArray())
            {
                _triggers.Remove(completed);
            }

            return Array.AsReadOnly(due.ToArray());
        }
    }

    private sealed class TriggerState
    {
        public TriggerState(ScheduledGameTrigger trigger)
            : this(trigger, trigger.Due, 0)
        {
        }

        public TriggerState(ScheduledGameTrigger trigger, GameMoment nextDue, int occurrences)
        {
            Trigger = trigger;
            NextDue = nextDue;
            Occurrences = occurrences;
        }

        public ScheduledGameTrigger Trigger { get; }

        public GameMoment NextDue { get; set; }

        public int Occurrences { get; set; }

        public bool Complete { get; set; }
    }

    private sealed class TriggerAdvance
    {
        public TriggerAdvance(TriggerState state, GameMoment nextDue, int occurrences, bool complete)
        {
            State = state;
            NextDue = nextDue;
            Occurrences = occurrences;
            Complete = complete;
        }

        public TriggerState State { get; }

        public GameMoment NextDue { get; }

        public int Occurrences { get; }

        public bool Complete { get; }
    }
}

public sealed class MultiActorScheduler
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ActorLane> _lanes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _concurrency;
    private TaskCompletionSource<object?>? _idleWaiter;
    private readonly int _maximumActors;
    private readonly int _maximumQueuedPerActor;

    public MultiActorScheduler(int maximumConcurrentActors, int maximumActors, int maximumQueuedPerActor)
    {
        if (maximumConcurrentActors <= 0 || maximumConcurrentActors > maximumActors)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentActors));
        }

        if (maximumActors <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActors));
        }

        if (maximumQueuedPerActor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedPerActor));
        }

        _concurrency = new SemaphoreSlim(maximumConcurrentActors, maximumConcurrentActors);
        _maximumActors = maximumActors;
        _maximumQueuedPerActor = maximumQueuedPerActor;
    }

    public Task<T> EnqueueAsync<T>(
        string actorId,
        Func<CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken = default)
    {
        GameJson.RequireId(actorId, nameof(actorId));
        if (work is null)
        {
            throw new ArgumentNullException(nameof(work));
        }

        var item = new ActorWorkItem<T>(work, cancellationToken);
        ActorLane lane;
        var startRunner = false;
        lock (_gate)
        {
            if (!_lanes.TryGetValue(actorId, out lane!))
            {
                if (_lanes.Count >= _maximumActors)
                {
                    throw new GameRuntimeLimitException(nameof(_maximumActors), "Too many actors have queued work.");
                }

                lane = new ActorLane();
                _lanes.Add(actorId, lane);
            }

            if (lane.Queue.Count >= _maximumQueuedPerActor)
            {
                throw new GameRuntimeLimitException(nameof(_maximumQueuedPerActor), "The actor work queue reached its capacity.");
            }

            lane.Queue.Enqueue(item);
            if (!lane.Running)
            {
                lane.Running = true;
                startRunner = true;
            }
        }

        if (startRunner)
        {
            _ = Task.Run(() => RunLaneAsync(actorId, lane), CancellationToken.None);
        }

        return item.Task;
    }

    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            if (_lanes.Count == 0)
            {
                return Task.CompletedTask;
            }

            _idleWaiter ??= new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idleWaiter.Task;
        }
    }

    private async Task RunLaneAsync(string actorId, ActorLane lane)
    {
        while (true)
        {
            ActorWorkItem item;
            TaskCompletionSource<object?>? idleWaiter = null;
            lock (_gate)
            {
                if (lane.Queue.Count == 0)
                {
                    lane.Running = false;
                    if (_lanes.TryGetValue(actorId, out var current) && ReferenceEquals(current, lane))
                    {
                        _lanes.Remove(actorId);
                    }

                    if (_lanes.Count == 0)
                    {
                        idleWaiter = _idleWaiter;
                        _idleWaiter = null;
                    }

                    item = null!;
                }
                else
                {
                    item = lane.Queue.Dequeue();
                }
            }

            if (idleWaiter is not null || item is null)
            {
                idleWaiter?.TrySetResult(null);

                return;
            }

            if (item.IsCanceled)
            {
                item.Cancel();
                continue;
            }

            try
            {
                await _concurrency.WaitAsync(item.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                item.Cancel();
                continue;
            }

            try
            {
                if (!item.TryStart())
                {
                    item.Cancel();
                }
                else
                {
                    await item.ExecuteAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _concurrency.Release();
            }
        }
    }

    private sealed class ActorLane
    {
        public Queue<ActorWorkItem> Queue { get; } = new();

        public bool Running { get; set; }
    }

    private abstract class ActorWorkItem
    {
        protected ActorWorkItem(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get; }

        public bool IsCanceled => CancellationToken.IsCancellationRequested;

        public abstract Task ExecuteAsync();

        public abstract bool TryStart();

        public abstract void Cancel();
    }

    private sealed class ActorWorkItem<T> : ActorWorkItem
    {
        private readonly Func<CancellationToken, ValueTask<T>> _work;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _started;

        public ActorWorkItem(Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken)
            : base(cancellationToken)
        {
            _work = work;
            _cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() =>
                {
                    if (Volatile.Read(ref _started) == 0)
                    {
                        _completion.TrySetCanceled(cancellationToken);
                    }
                })
                : default;
        }

        public Task<T> Task => _completion.Task;

        public override bool TryStart()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                return false;
            }

            return !_completion.Task.IsCompleted;
        }

        public override async Task ExecuteAsync()
        {
            try
            {
                _completion.TrySetResult(await _work(CancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                Cancel();
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                _cancellationRegistration.Dispose();
            }
        }

        public override void Cancel()
        {
            _completion.TrySetCanceled(CancellationToken);
            _cancellationRegistration.Dispose();
        }
    }
}

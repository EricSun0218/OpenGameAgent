using GameAgent.Protocol;

namespace GameAgent.Core;

public interface IRuntimeEventPublisher
{
    void Publish(RuntimeEvent runtimeEvent);
}

public interface INonBlockingRuntimeEventPublisher : IRuntimeEventPublisher
{
}

public sealed class NullRuntimeEventPublisher :
    INonBlockingRuntimeEventPublisher
{
    public static NullRuntimeEventPublisher Instance { get; } = new();

    private NullRuntimeEventPublisher()
    {
    }

    public void Publish(RuntimeEvent runtimeEvent)
    {
    }
}

public sealed class BoundedRuntimeEventBus :
    INonBlockingRuntimeEventPublisher,
    IDisposable
{
    private readonly object _sync = new();
    private readonly LinkedList<RuntimeEvent> _events = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _capacity;
    private long _droppedEphemeral;
    private long _droppedDurable;
    private int _disposed;

    public BoundedRuntimeEventBus(int capacity = 1024)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public long DroppedEphemeralEvents =>
        Interlocked.Read(ref _droppedEphemeral);

    public long DroppedDurableNotifications =>
        Interlocked.Read(ref _droppedDurable);

    public void Publish(RuntimeEvent runtimeEvent)
    {
        if (runtimeEvent is null)
        {
            throw new ArgumentNullException(nameof(runtimeEvent));
        }

        var cloned = Clone(runtimeEvent);
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            if (_events.Count >= _capacity)
            {
                if (string.Equals(
                        cloned.Durability,
                        EventDurabilities.Durable,
                        StringComparison.Ordinal)
                    && TryReplaceEphemeral(cloned))
                {
                    return;
                }

                CountDrop(cloned);
                return;
            }

            _events.AddLast(cloned);
            _available.Release();
        }
    }

    public bool TryRead(out RuntimeEvent? runtimeEvent)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BoundedRuntimeEventBus));
        }

        if (!_available.Wait(0))
        {
            runtimeEvent = null;
            return false;
        }

        lock (_sync)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(
                    nameof(BoundedRuntimeEventBus));
            }

            if (_events.Count > 0)
            {
                runtimeEvent = RemoveFirst();
                return true;
            }
        }

        throw new InvalidOperationException(
            "The runtime event signal had no queued event.");
    }

    public async ValueTask<RuntimeEvent> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BoundedRuntimeEventBus));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _available.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(BoundedRuntimeEventBus));
        }

        lock (_sync)
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(
                    nameof(BoundedRuntimeEventBus));
            }

            if (_events.Count > 0)
            {
                return RemoveFirst();
            }
        }

        throw new InvalidOperationException(
            "The runtime event signal had no queued event.");
    }

    public IReadOnlyList<RuntimeEvent> Drain(int maximum)
    {
        if (maximum < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        var results = new List<RuntimeEvent>(Math.Min(maximum, _capacity));
        while (results.Count < maximum
               && TryRead(out var runtimeEvent)
               && runtimeEvent is not null)
        {
            results.Add(runtimeEvent);
        }

        return results;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            _events.Clear();
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (AggregateException)
        {
            // Only the bus's linked reader registrations observe this token.
            // Keep waking any other pending readers if a callback misbehaves.
        }
    }

    private void CountDrop(RuntimeEvent runtimeEvent)
    {
        if (string.Equals(
                runtimeEvent.Durability,
                EventDurabilities.Ephemeral,
                StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _droppedEphemeral);
        }
        else
        {
            Interlocked.Increment(ref _droppedDurable);
        }
    }

    private RuntimeEvent RemoveFirst()
    {
        var first = _events.First
                    ?? throw new InvalidOperationException(
                        "The event queue was unexpectedly empty.");
        _events.RemoveFirst();
        return first.Value;
    }

    private bool TryReplaceEphemeral(RuntimeEvent durable)
    {
        var node = _events.First;
        while (node is not null)
        {
            if (string.Equals(
                    node.Value.Durability,
                    EventDurabilities.Ephemeral,
                    StringComparison.Ordinal))
            {
                CountDrop(node.Value);
                _events.Remove(node);
                _events.AddLast(durable);
                return true;
            }

            node = node.Next;
        }

        return false;
    }

    private static RuntimeEvent Clone(RuntimeEvent runtimeEvent)
    {
        return ProtocolJson.DeserializeRuntimeEvent(
            ProtocolJson.Serialize(runtimeEvent));
    }
}

internal sealed class BoundedObserverWorkerDispatcher
{
    internal const int DefaultCapacity = 64;

    private readonly SemaphoreSlim _capacity;
    private int _reservations;

    public BoundedObserverWorkerDispatcher(
        int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = new SemaphoreSlim(capacity, capacity);
    }

    public static BoundedObserverWorkerDispatcher Shared { get; } = new();

    internal int ActiveReservations =>
        Volatile.Read(ref _reservations);

    public bool TryReserve(out ObserverWorkerReservation? reservation)
    {
        if (!_capacity.Wait(0))
        {
            reservation = null;
            return false;
        }

        Interlocked.Increment(ref _reservations);
        reservation = new ObserverWorkerReservation(this);
        return true;
    }

    private void Release()
    {
        Interlocked.Decrement(ref _reservations);
        _capacity.Release();
    }

    internal sealed class ObserverWorkerReservation : IDisposable
    {
        private readonly BoundedObserverWorkerDispatcher _owner;
        private int _state;

        internal ObserverWorkerReservation(
            BoundedObserverWorkerDispatcher owner)
        {
            _owner = owner;
        }

        public void Dispatch(Action worker)
        {
            if (worker is null)
            {
                throw new ArgumentNullException(nameof(worker));
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "An observer worker reservation can be dispatched only once.");
            }

            try
            {
                _ = Task.Run(worker);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref _state, 2);
            if (previous != 2)
            {
                _owner.Release();
            }
        }
    }
}

public sealed class BufferedRuntimeEventPublisher :
    INonBlockingRuntimeEventPublisher,
    IDisposable
{
    private readonly object _sync = new();
    private readonly LinkedList<RuntimeEvent> _events = new();
    private readonly IRuntimeEventPublisher _inner;
    private readonly BoundedObserverWorkerDispatcher _workerDispatcher;
    private readonly int _capacity;
    private bool _workerRunning;
    private bool _disposed;
    private long _droppedEphemeral;
    private long _droppedDurable;
    private long _publisherFailures;
    private long _workerRejections;

    public BufferedRuntimeEventPublisher(
        IRuntimeEventPublisher inner,
        int capacity = 1024)
        : this(
            inner,
            capacity,
            BoundedObserverWorkerDispatcher.Shared)
    {
    }

    internal BufferedRuntimeEventPublisher(
        IRuntimeEventPublisher inner,
        int capacity,
        BoundedObserverWorkerDispatcher workerDispatcher)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _workerDispatcher = workerDispatcher
            ?? throw new ArgumentNullException(nameof(workerDispatcher));
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public long DroppedEphemeralEvents =>
        Interlocked.Read(ref _droppedEphemeral);

    public long DroppedDurableNotifications =>
        Interlocked.Read(ref _droppedDurable);

    public long PublisherFailures =>
        Interlocked.Read(ref _publisherFailures);

    public long WorkerRejections =>
        Interlocked.Read(ref _workerRejections);

    public void Publish(RuntimeEvent runtimeEvent)
    {
        if (runtimeEvent is null)
        {
            throw new ArgumentNullException(nameof(runtimeEvent));
        }

        RuntimeEvent snapshot;
        try
        {
            snapshot = ProtocolJson.DeserializeRuntimeEvent(
                ProtocolJson.Serialize(runtimeEvent));
        }
        catch
        {
            CountDrop(runtimeEvent);
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                CountDrop(snapshot);
                return;
            }

            if (_events.Count >= _capacity)
            {
                if (!string.Equals(
                        snapshot.Durability,
                        EventDurabilities.Durable,
                        StringComparison.Ordinal)
                    || !TryReplaceEphemeral(snapshot))
                {
                    CountDrop(snapshot);
                }

                return;
            }

            _events.AddLast(snapshot);
            if (!_workerRunning)
            {
                if (!_workerDispatcher.TryReserve(
                        out var workerReservation))
                {
                    Interlocked.Increment(ref _workerRejections);
                    DropQueuedEvents();
                    return;
                }

                _workerRunning = true;
                try
                {
                    var reservation = workerReservation!;
                    reservation.Dispatch(
                        () => Drain(reservation));
                }
                catch
                {
                    _workerRunning = false;
                    Interlocked.Increment(ref _workerRejections);
                    DropQueuedEvents();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var runtimeEvent in _events)
            {
                CountDrop(runtimeEvent);
            }

            _events.Clear();
        }
    }

    private void Drain(
        BoundedObserverWorkerDispatcher.ObserverWorkerReservation
            workerReservation)
    {
        var workerCompleted = false;
        try
        {
            while (true)
            {
                RuntimeEvent runtimeEvent;
                lock (_sync)
                {
                    if (_events.First is null)
                    {
                        workerReservation.Dispose();
                        _workerRunning = false;
                        workerCompleted = true;
                        return;
                    }

                    runtimeEvent = _events.First.Value;
                    _events.RemoveFirst();
                }

                try
                {
                    _inner.Publish(runtimeEvent);
                }
                catch
                {
                    Interlocked.Increment(ref _publisherFailures);
                }
            }
        }
        finally
        {
            if (!workerCompleted)
            {
                lock (_sync)
                {
                    workerReservation.Dispose();
                    _workerRunning = false;
                    if (_events.Count > 0)
                    {
                        DropQueuedEvents();
                    }
                }
            }
        }
    }

    private bool TryReplaceEphemeral(RuntimeEvent durable)
    {
        for (var node = _events.First; node is not null; node = node.Next)
        {
            if (!string.Equals(
                    node.Value.Durability,
                    EventDurabilities.Ephemeral,
                    StringComparison.Ordinal))
            {
                continue;
            }

            CountDrop(node.Value);
            _events.Remove(node);
            _events.AddLast(durable);
            return true;
        }

        return false;
    }

    private void DropQueuedEvents()
    {
        foreach (var runtimeEvent in _events)
        {
            CountDrop(runtimeEvent);
        }

        _events.Clear();
    }

    private void CountDrop(RuntimeEvent runtimeEvent)
    {
        if (string.Equals(
                runtimeEvent.Durability,
                EventDurabilities.Ephemeral,
                StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _droppedEphemeral);
        }
        else
        {
            Interlocked.Increment(ref _droppedDurable);
        }
    }
}

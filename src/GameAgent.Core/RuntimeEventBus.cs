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

public sealed class BufferedRuntimeEventPublisher :
    INonBlockingRuntimeEventPublisher,
    IDisposable
{
    private readonly object _sync = new();
    private readonly LinkedList<RuntimeEvent> _events = new();
    private readonly IRuntimeEventPublisher _inner;
    private readonly int _capacity;
    private bool _workerRunning;
    private bool _disposed;
    private long _droppedEphemeral;
    private long _droppedDurable;
    private long _publisherFailures;

    public BufferedRuntimeEventPublisher(
        IRuntimeEventPublisher inner,
        int capacity = 1024)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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

        var startWorker = false;
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
                _workerRunning = true;
                startWorker = true;
            }
        }

        if (startWorker)
        {
            _ = Task.Run(DrainAsync);
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

    private Task DrainAsync()
    {
        while (true)
        {
            RuntimeEvent runtimeEvent;
            lock (_sync)
            {
                if (_events.First is null)
                {
                    _workerRunning = false;
                    return Task.CompletedTask;
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

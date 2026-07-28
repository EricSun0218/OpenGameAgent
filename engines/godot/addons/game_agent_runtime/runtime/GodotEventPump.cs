using System.Diagnostics;
using System.Threading.Channels;

namespace GameAgent.Godot;

internal static class GodotEventKinds
{
    public const string RuntimeStarted = "runtime_started";
    public const string RuntimeEvent = "runtime_event";
    public const string RunCompleted = "run_completed";
    public const string RunFailed = "run_failed";
    public const string RuntimeStopped = "runtime_stopped";
    public const string RuntimeError = "runtime_error";
    public const string PumpOverflow = "pump_overflow";
}

internal sealed class GodotEventMessage
{
    public string Kind { get; init; } = string.Empty;

    public string? RequestId { get; init; }

    public string? Json { get; init; }

    public string? SecondaryJson { get; init; }

    public string? Code { get; init; }

    public string? Category { get; init; }

    public string? Message { get; init; }

    public int Count { get; init; }

    public bool ReconciliationRequired { get; init; }
}

internal sealed class GodotEventPump
{
    private readonly Channel<GodotEventMessage> _events;
    private int _accepting = 1;
    private int _droppedCount;
    private int _pendingCount;

    public GodotEventPump(int capacity)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _events = Channel.CreateBounded<GodotEventMessage>(
            new BoundedChannelOptions(capacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
    }

    public int Capacity { get; }

    public int PendingCount => Volatile.Read(ref _pendingCount);

    public int DroppedCount => Volatile.Read(ref _droppedCount);

    public bool TryPublish(GodotEventMessage message)
    {
        if (Volatile.Read(ref _accepting) == 0)
        {
            return false;
        }

        Interlocked.Increment(ref _pendingCount);
        if (_events.Writer.TryWrite(message))
        {
            return true;
        }

        Interlocked.Decrement(ref _pendingCount);
        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public async ValueTask PublishCriticalAsync(
        GodotEventMessage message,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _accepting) == 0)
        {
            throw new OperationCanceledException(
                "The Godot event pump is shutting down.",
                cancellationToken);
        }

        Interlocked.Increment(ref _pendingCount);
        try
        {
            await _events.Writer
                .WriteAsync(message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingCount);
            throw;
        }
    }

    public int Drain(
        int maxEvents,
        TimeSpan maxDuration,
        Action<GodotEventMessage> publish)
    {
        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        }

        if (maxDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDuration));
        }

        var processed = 0;
        var started = Stopwatch.GetTimestamp();

        var dropped = Interlocked.Exchange(ref _droppedCount, 0);
        if (dropped > 0 && processed < maxEvents)
        {
            publish(new GodotEventMessage
            {
                Kind = GodotEventKinds.PumpOverflow,
                Count = dropped,
                Code = "event_pump_overflow",
                Category = "backpressure",
                Message = "Runtime event notifications were dropped; durable journal data remains available."
            });
            processed++;
        }

        while (processed < maxEvents
               && Stopwatch.GetElapsedTime(started) < maxDuration
               && _events.Reader.TryRead(out var message))
        {
            Interlocked.Decrement(ref _pendingCount);
            publish(message);
            processed++;
        }

        return processed;
    }

    public void StopAccepting()
    {
        if (Interlocked.Exchange(ref _accepting, 0) == 0)
        {
            return;
        }

        _events.Writer.TryComplete();
    }
}

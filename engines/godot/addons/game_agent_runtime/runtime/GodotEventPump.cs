using System.Diagnostics;
using System.Threading.Channels;
using GameAgent.Core;

namespace GameAgent.Godot;

internal static class GodotEventKinds
{
    public const string RuntimeStarted = "runtime_started";
    public const string RuntimeEvent = "runtime_event";
    public const string RunCompleted = "run_completed";
    public const string RunFailed = "run_failed";
    public const string BatchCompleted = "batch_completed";
    public const string BatchParticipantCompleted =
        "batch_participant_completed";
    public const string BatchFailed = "batch_failed";
    public const string RuntimeStopped = "runtime_stopped";
    public const string RuntimeError = "runtime_error";
    public const string PumpOverflow = "pump_overflow";
}

internal sealed class GodotEventMessage
{
    internal long EnqueuedTimestamp { get; set; }

    public string Kind { get; init; } = string.Empty;

    public string? RequestId { get; init; }

    public string? Json { get; init; }

    public string? SecondaryJson { get; init; }

    public string? Code { get; init; }

    public string? Category { get; init; }

    public string? Message { get; init; }

    public int Count { get; init; }

    public bool ReconciliationRequired { get; init; }

    public string? Phase { get; init; }

    public string? BatchId { get; init; }

    public string? ParticipantRunId { get; init; }

    public string? ParticipantAgentId { get; init; }

    public string? ParticipantDecisionKey { get; init; }

    public int ParticipantInputIndex { get; init; } = -1;

    public IReadOnlyList<string> AffectedRunIds { get; init; } =
        Array.Empty<string>();
}

internal sealed class GodotEventPump
{
    private readonly Channel<GodotEventMessage> _events;
    private readonly RuntimeMetricsEmitter? _metrics;
    private int _accepting = 1;
    private int _droppedCount;
    private int _pendingCount;

    public GodotEventPump(
        int capacity,
        RuntimeMetricsEmitter? metrics = null)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _metrics = metrics;
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

        message.EnqueuedTimestamp = Stopwatch.GetTimestamp();
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

        message.EnqueuedTimestamp = Stopwatch.GetTimestamp();
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
            _metrics?.Record(
                RuntimeMetricNames.EventPumpDropped,
                RuntimeMetricKind.Counter,
                dropped,
                outcome: RuntimeMetricOutcomes.Dropped,
                engine: "godot");
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
            _metrics?.Record(
                RuntimeMetricNames.EventPumpDispatchLatencyMilliseconds,
                RuntimeMetricKind.Histogram,
                RuntimeMetricsEmitter.ElapsedMilliseconds(
                    message.EnqueuedTimestamp),
                outcome: RuntimeMetricOutcomes.Success,
                engine: "godot");
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

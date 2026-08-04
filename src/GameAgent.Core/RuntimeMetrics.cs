using System.Collections.Concurrent;
using System.Diagnostics;

namespace GameAgent.Core;

public enum RuntimeMetricKind
{
    Counter,
    Gauge,
    Histogram
}

public static class RuntimeMetricNames
{
    public const string WorkloadQueueDepth =
        "runtime.workload.queue_depth";
    public const string WorkloadQueueWaitMilliseconds =
        "runtime.workload.queue_wait_ms";
    public const string PromptAssemblyMilliseconds =
        "runtime.prompt.assembly_ms";
    public const string PromptUtf8Bytes =
        "runtime.prompt.utf8_bytes";
    public const string MemoryRecallMilliseconds =
        "runtime.memory.recall_ms";
    public const string MemoryCommitMilliseconds =
        "runtime.memory.commit_ms";
    public const string CompactionDurationMilliseconds =
        "runtime.compaction.duration_ms";
    public const string CompactionReclaimedMessages =
        "runtime.compaction.reclaimed_messages";
    public const string ToolQueueDepth =
        "runtime.tool.queue_depth";
    public const string ToolQueueWaitMilliseconds =
        "runtime.tool.queue_wait_ms";
    public const string ToolExecutionMilliseconds =
        "runtime.tool.execution_ms";
    public const string ProviderTimeToFirstTokenMilliseconds =
        "runtime.provider.ttft_ms";
    public const string ProviderStreamDurationMilliseconds =
        "runtime.provider.stream_duration_ms";
    public const string EventPumpDropped =
        "runtime.event_pump.dropped";
    public const string EventPumpDispatchLatencyMilliseconds =
        "runtime.event_pump.dispatch_latency_ms";
}

public static class RuntimeMetricOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Canceled = "canceled";
    public const string Timeout = "timeout";
    public const string Dropped = "dropped";
    public const string Rejected = "rejected";
}

/// <summary>
/// Fixed, low-cardinality dimensions emitted by the runtime. Per-run,
/// per-agent, per-tool, model, and user-provided values are intentionally not
/// represented here.
/// </summary>
public sealed class RuntimeMetricDimensions
{
    internal RuntimeMetricDimensions(
        string? workloadClass,
        string? outcome,
        string? engine)
    {
        WorkloadClass = workloadClass;
        Outcome = outcome;
        Engine = engine;
    }

    public string? WorkloadClass { get; }

    public string? Outcome { get; }

    public string? Engine { get; }
}

public sealed class RuntimeMetric
{
    internal RuntimeMetric(
        string name,
        RuntimeMetricKind kind,
        double value,
        RuntimeMetricDimensions dimensions)
    {
        Name = name;
        Kind = kind;
        Value = value;
        Dimensions = dimensions;
    }

    public string Name { get; }

    public RuntimeMetricKind Kind { get; }

    public double Value { get; }

    public RuntimeMetricDimensions Dimensions { get; }
}

/// <summary>
/// Receives metrics away from runtime and engine hot paths. Implementations
/// may batch or export records, but should still honor cancellation during
/// shutdown.
/// </summary>
public interface IRuntimeMetricsSink
{
    ValueTask RecordAsync(
        RuntimeMetric metric,
        CancellationToken cancellationToken = default);
}

public sealed class RuntimeMetricsOptions
{
    public int Capacity { get; set; } = 4_096;

    public TimeSpan ShutdownDrainTimeout { get; set; } =
        TimeSpan.FromMilliseconds(100);

    internal RuntimeMetricsOptions Snapshot()
    {
        if (Capacity is < 16 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(Capacity));
        }

        if (ShutdownDrainTimeout < TimeSpan.Zero
            || ShutdownDrainTimeout == Timeout.InfiniteTimeSpan
            || ShutdownDrainTimeout > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ShutdownDrainTimeout));
        }

        return new RuntimeMetricsOptions
        {
            Capacity = Capacity,
            ShutdownDrainTimeout = ShutdownDrainTimeout
        };
    }
}

public sealed class RuntimeMetricsHealth
{
    internal RuntimeMetricsHealth(
        int pending,
        long delivered,
        long dropped,
        long sinkFailures)
    {
        Pending = pending;
        Delivered = delivered;
        Dropped = dropped;
        SinkFailures = sinkFailures;
    }

    public int Pending { get; }

    public long Delivered { get; }

    public long Dropped { get; }

    public long SinkFailures { get; }
}

public sealed class RuntimeMetricsEmitter
{
    private static readonly RuntimeMetricDimensions EmptyDimensions =
        new(null, null, null);
    private readonly IRuntimeMetricsSink? _sink;
    private readonly RuntimeMetricsOptions _options;
    private readonly ConcurrentQueue<RuntimeMetric> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _pending;
    private int _stopping;
    private int _signalsDisposed;
    private long _delivered;
    private long _dropped;
    private long _sinkFailures;

    public RuntimeMetricsEmitter(
        IRuntimeMetricsSink? sink,
        RuntimeMetricsOptions? options = null)
    {
        _sink = sink;
        _options = (options ?? new RuntimeMetricsOptions()).Snapshot();
        _worker = sink is null
            ? Task.CompletedTask
            : Task.Run(DrainAsync);
    }

    public RuntimeMetricsHealth Health => new(
        Volatile.Read(ref _pending),
        Interlocked.Read(ref _delivered),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _sinkFailures));

    public bool Record(
        string name,
        RuntimeMetricKind kind,
        double value,
        string? workloadClass = null,
        string? outcome = null,
        string? engine = null)
    {
        if (_sink is null || Volatile.Read(ref _stopping) != 0)
        {
            return false;
        }

        ValidateMetric(name, value);
        var dimensions = workloadClass is null
                         && outcome is null
                         && engine is null
            ? EmptyDimensions
            : new RuntimeMetricDimensions(
                NormalizeWorkloadClass(workloadClass),
                NormalizeOutcome(outcome),
                NormalizeEngine(engine));
        if (Interlocked.Increment(ref _pending) > _options.Capacity)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
            return false;
        }

        if (Volatile.Read(ref _stopping) != 0)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
            return false;
        }

        _queue.Enqueue(new RuntimeMetric(name, kind, value, dimensions));
        try
        {
            _available.Release();
            return true;
        }
        catch (ObjectDisposedException)
        {
            if (_queue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _pending);
            }
            Interlocked.Increment(ref _dropped);
            return false;
        }
    }

    public async ValueTask<bool> StopAsync()
    {
        if (_sink is null)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            _available.Release();
        }

        if (_worker.IsCompleted)
        {
            await ObserveWorkerAsync(_worker).ConfigureAwait(false);
            DisposeSignals();
            return true;
        }

        if (_options.ShutdownDrainTimeout == TimeSpan.Zero)
        {
            _shutdown.Cancel();
            return false;
        }

        var timeout = Task.Delay(_options.ShutdownDrainTimeout);
        var completed = await Task.WhenAny(_worker, timeout)
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, _worker))
        {
            _shutdown.Cancel();
            _ = _worker.ContinueWith(
                static (_, state) =>
                    ((RuntimeMetricsEmitter)state!).DisposeSignals(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        }

        await ObserveWorkerAsync(_worker).ConfigureAwait(false);
        DisposeSignals();
        return true;
    }

    public static double ElapsedMilliseconds(long startedTimestamp)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        return elapsedTicks * 1_000d / Stopwatch.Frequency;
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            while (_queue.TryDequeue(out var metric))
            {
                Interlocked.Decrement(ref _pending);
                try
                {
                    await _sink!
                        .RecordAsync(metric, _shutdown.Token)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref _delivered);
                }
                catch (OperationCanceledException)
                    when (_shutdown.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _dropped);
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException
                          and not StackOverflowException)
                {
                    Interlocked.Increment(ref _sinkFailures);
                }
            }

            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            try
            {
                await _available.WaitAsync(_shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (_shutdown.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static async Task ObserveWorkerAsync(Task worker)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException
                  and not StackOverflowException)
        {
            _ = exception;
        }
    }

    private void DisposeSignals()
    {
        if (Interlocked.Exchange(ref _signalsDisposed, 1) != 0)
        {
            return;
        }

        _available.Dispose();
        _shutdown.Dispose();
    }

    private static void ValidateMetric(string name, double value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "A metric name is required.",
                nameof(name));
        }

        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static string? NormalizeWorkloadClass(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return string.Equals(
            value,
            ProviderWorkloadClasses.Background,
            StringComparison.Ordinal)
            ? ProviderWorkloadClasses.Background
            : ProviderWorkloadClasses.Interactive;
    }

    private static string? NormalizeOutcome(string? value)
    {
        return value switch
        {
            null => null,
            RuntimeMetricOutcomes.Success =>
                RuntimeMetricOutcomes.Success,
            RuntimeMetricOutcomes.Canceled =>
                RuntimeMetricOutcomes.Canceled,
            RuntimeMetricOutcomes.Timeout =>
                RuntimeMetricOutcomes.Timeout,
            RuntimeMetricOutcomes.Dropped =>
                RuntimeMetricOutcomes.Dropped,
            RuntimeMetricOutcomes.Rejected =>
                RuntimeMetricOutcomes.Rejected,
            _ => RuntimeMetricOutcomes.Failure
        };
    }

    private static string? NormalizeEngine(string? value)
    {
        return value switch
        {
            null => null,
            "godot" => "godot",
            "unity" => "unity",
            _ => "core"
        };
    }
}

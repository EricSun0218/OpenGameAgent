using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using GameAgent.Core;

namespace GameAgent.Observability.OpenTelemetry;

public sealed class OpenTelemetryRuntimeMetricsSink : IRuntimeMetricsSink, IDisposable
{
    public const string MeterName = "GameAgent.Runtime";
    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, Counter<double>> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<GaugeKey, double> _gauges = new();
    private readonly ObservableGauge<double>[] _gaugeInstruments;
    private int _disposed;

    public OpenTelemetryRuntimeMetricsSink()
    {
        _meter = new Meter(MeterName);
        _gaugeInstruments = new[]
        {
            _meter.CreateObservableGauge(
                RuntimeMetricNames.WorkloadQueueDepth,
                () => Observe(RuntimeMetricNames.WorkloadQueueDepth),
                "{work}",
                "Queued provider work."),
            _meter.CreateObservableGauge(
                RuntimeMetricNames.ToolQueueDepth,
                () => Observe(RuntimeMetricNames.ToolQueueDepth),
                "{call}",
                "Queued tool calls.")
        };
    }

    public ValueTask RecordAsync(RuntimeMetric metric, CancellationToken cancellationToken = default)
    {
        if (metric is null) throw new ArgumentNullException(nameof(metric));
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var expected = ExpectedKind(metric.Name);
        if (metric.Kind != expected)
        {
            throw new InvalidOperationException($"Metric '{metric.Name}' used the wrong instrument kind.");
        }
        var tags = Tags(metric.Dimensions);
        switch (expected)
        {
            case RuntimeMetricKind.Counter:
                _counters.GetOrAdd(metric.Name, name => _meter.CreateCounter<double>(name, Unit(name)))
                    .Add(metric.Value, tags);
                break;
            case RuntimeMetricKind.Histogram:
                _histograms.GetOrAdd(metric.Name, name => _meter.CreateHistogram<double>(name, Unit(name)))
                    .Record(metric.Value, tags);
                break;
            case RuntimeMetricKind.Gauge:
                _gauges[new GaugeKey(metric.Name, metric.Dimensions)] = metric.Value;
                break;
            default:
                throw new InvalidOperationException("The runtime metric kind is unsupported.");
        }
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gauges.Clear();
        _meter.Dispose();
        GC.KeepAlive(_gaugeInstruments);
    }

    private IEnumerable<Measurement<double>> Observe(string name)
    {
        foreach (var pair in _gauges)
        {
            if (pair.Key.Name == name)
            {
                yield return new Measurement<double>(pair.Value, pair.Key.Tags);
            }
        }
    }

    private static RuntimeMetricKind ExpectedKind(string name) => name switch
    {
        RuntimeMetricNames.WorkloadQueueDepth or RuntimeMetricNames.ToolQueueDepth => RuntimeMetricKind.Gauge,
        RuntimeMetricNames.EventPumpDropped => RuntimeMetricKind.Counter,
        RuntimeMetricNames.WorkloadQueueWaitMilliseconds
            or RuntimeMetricNames.PromptAssemblyMilliseconds
            or RuntimeMetricNames.PromptUtf8Bytes
            or RuntimeMetricNames.MemoryRecallMilliseconds
            or RuntimeMetricNames.MemoryCommitMilliseconds
            or RuntimeMetricNames.CompactionDurationMilliseconds
            or RuntimeMetricNames.CompactionReclaimedMessages
            or RuntimeMetricNames.ToolQueueWaitMilliseconds
            or RuntimeMetricNames.ToolExecutionMilliseconds
            or RuntimeMetricNames.ProviderTimeToFirstTokenMilliseconds
            or RuntimeMetricNames.ProviderStreamDurationMilliseconds
            or RuntimeMetricNames.EventPumpDispatchLatencyMilliseconds => RuntimeMetricKind.Histogram,
        _ => throw new InvalidOperationException($"Metric '{name}' is not in the runtime's closed metric set.")
    };

    private static string Unit(string name) => name.EndsWith("_ms", StringComparison.Ordinal)
        ? "ms"
        : name.EndsWith("_bytes", StringComparison.Ordinal)
            ? "By"
            : "{item}";

    private static KeyValuePair<string, object?>[] Tags(RuntimeMetricDimensions dimensions)
    {
        var tags = new List<KeyValuePair<string, object?>>(3);
        if (dimensions.WorkloadClass is not null) tags.Add(new("workload.class", dimensions.WorkloadClass));
        if (dimensions.Outcome is not null) tags.Add(new("outcome", dimensions.Outcome));
        if (dimensions.Engine is not null) tags.Add(new("engine", dimensions.Engine));
        return tags.ToArray();
    }

    private readonly struct GaugeKey : IEquatable<GaugeKey>
    {
        public GaugeKey(string name, RuntimeMetricDimensions dimensions)
        {
            Name = name;
            WorkloadClass = dimensions.WorkloadClass;
            Outcome = dimensions.Outcome;
            Engine = dimensions.Engine;
            Tags = OpenTelemetryRuntimeMetricsSink.Tags(dimensions);
        }

        public string Name { get; }
        public string? WorkloadClass { get; }
        public string? Outcome { get; }
        public string? Engine { get; }
        public KeyValuePair<string, object?>[] Tags { get; }

        public bool Equals(GaugeKey other) =>
            Name == other.Name && WorkloadClass == other.WorkloadClass
            && Outcome == other.Outcome && Engine == other.Engine;

        public override bool Equals(object? obj) => obj is GaugeKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Name, StringComparer.Ordinal);
            hash.Add(WorkloadClass, StringComparer.Ordinal);
            hash.Add(Outcome, StringComparer.Ordinal);
            hash.Add(Engine, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}

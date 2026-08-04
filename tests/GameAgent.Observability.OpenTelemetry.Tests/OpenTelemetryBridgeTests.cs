using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using GameAgent.Core;
using GameAgent.Observability.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;

namespace GameAgent.Observability.OpenTelemetry.Tests;

public sealed class OpenTelemetryBridgeTests
{
    [Fact]
    public async Task MetricBridgeExportsOnlyClosedLowCardinalityTags()
    {
        var measurements = new ConcurrentQueue<(string Name, double Value, string[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == OpenTelemetryRuntimeMetricsSink.MeterName) current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, tags.ToArray().Select(static tag => tag.Key).ToArray())));
        listener.Start();
        using var sink = new OpenTelemetryRuntimeMetricsSink();
        var emitter = new RuntimeMetricsEmitter(sink);
        Assert.True(emitter.Record(RuntimeMetricNames.ToolExecutionMilliseconds,
            RuntimeMetricKind.Histogram, 12, ProviderWorkloadClasses.Interactive,
            RuntimeMetricOutcomes.Success, "unity"));
        Assert.True(await emitter.StopAsync());

        var measurement = Assert.Single(measurements);
        Assert.Equal(12, measurement.Value);
        Assert.Equal(new[] { "workload.class", "outcome", "engine" }, measurement.Tags);
        Assert.DoesNotContain(measurement.Tags, tag => tag.Contains("run", StringComparison.OrdinalIgnoreCase)
                                                       || tag.Contains("actor", StringComparison.OrdinalIgnoreCase)
                                                       || tag.Contains("world", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GaugeReportsLatestValueInsteadOfAccumulating()
    {
        var values = new ConcurrentQueue<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Name == RuntimeMetricNames.ToolQueueDepth) current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == RuntimeMetricNames.ToolQueueDepth) values.Enqueue(value);
        });
        listener.Start();
        using var sink = new OpenTelemetryRuntimeMetricsSink();
        var emitter = new RuntimeMetricsEmitter(sink);
        emitter.Record(RuntimeMetricNames.ToolQueueDepth, RuntimeMetricKind.Gauge, 9);
        emitter.Record(RuntimeMetricNames.ToolQueueDepth, RuntimeMetricKind.Gauge, 2);
        Assert.True(await emitter.StopAsync());
        listener.RecordObservableInstruments();
        Assert.Equal(2, Assert.Single(values));
    }

    [Fact]
    public void TraceBridgeNeverAcceptsIdentityTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GameAgentTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = GameAgentTelemetry.StartActivity("run", ProviderWorkloadClasses.Background, "godot");
        Assert.NotNull(activity);
        Assert.Equal(new[] { "workload.class", "engine" }, activity!.Tags.Select(static tag => tag.Key));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameAgentTelemetry.StartActivity("player-123"));
    }

    [Fact]
    public void DependencyInjectionExposesRuntimeSink()
    {
        var services = new ServiceCollection();
        services.AddGameAgentOpenTelemetryMetrics();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<OpenTelemetryRuntimeMetricsSink>(provider.GetRequiredService<IRuntimeMetricsSink>());
    }
}

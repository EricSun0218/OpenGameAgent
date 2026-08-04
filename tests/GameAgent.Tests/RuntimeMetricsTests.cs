using System.Collections.Concurrent;
using System.Diagnostics;
using GameAgent.Core;
using GameAgent.Testing;

namespace GameAgent.Tests;

public sealed class RuntimeMetricsTests
{
    [Fact]
    public async Task ConcurrentProducersAreBoundedAndDeliveredOffPath()
    {
        var sink = new CollectingSink();
        var metrics = new RuntimeMetricsEmitter(
            sink,
            new RuntimeMetricsOptions
            {
                Capacity = 4_096,
                ShutdownDrainTimeout = TimeSpan.FromSeconds(1)
            });

        var producers = Enumerable.Range(0, 16)
            .Select(
                _ => Task.Run(
                    () =>
                    {
                        for (var index = 0; index < 200; index++)
                        {
                            Assert.True(
                                metrics.Record(
                                    RuntimeMetricNames
                                        .WorkloadQueueWaitMilliseconds,
                                    RuntimeMetricKind.Histogram,
                                    index,
                                    ProviderWorkloadClasses.Background,
                                    RuntimeMetricOutcomes.Success));
                        }
                    }))
            .ToArray();
        await Task.WhenAll(producers);

        Assert.True(await metrics.StopAsync());
        Assert.Equal(3_200, sink.Records.Count);
        Assert.All(
            sink.Records,
            record =>
            {
                Assert.Equal(
                    ProviderWorkloadClasses.Background,
                    record.Dimensions.WorkloadClass);
                Assert.Equal(
                    RuntimeMetricOutcomes.Success,
                    record.Dimensions.Outcome);
                Assert.Null(record.Dimensions.Engine);
            });
        Assert.Equal(0, metrics.Health.Pending);
        Assert.Equal(0, metrics.Health.SinkFailures);
    }

    [Fact]
    public async Task FailingSinkNeverEscapesIntoProducer()
    {
        var metrics = new RuntimeMetricsEmitter(
            new FailingSink(),
            new RuntimeMetricsOptions
            {
                Capacity = 64,
                ShutdownDrainTimeout = TimeSpan.FromSeconds(1)
            });

        for (var index = 0; index < 32; index++)
        {
            _ = metrics.Record(
                RuntimeMetricNames.MemoryRecallMilliseconds,
                RuntimeMetricKind.Histogram,
                index,
                outcome: RuntimeMetricOutcomes.Success);
        }

        Assert.True(await metrics.StopAsync());
        Assert.Equal(32, metrics.Health.SinkFailures);
        Assert.Equal(0, metrics.Health.Pending);
    }

    [Fact]
    public async Task BlockingSinkCannotBlockProducerOrShutdownPastBudget()
    {
        var sink = new BlockingSink();
        var metrics = new RuntimeMetricsEmitter(
            sink,
            new RuntimeMetricsOptions
            {
                Capacity = 16,
                ShutdownDrainTimeout = TimeSpan.FromMilliseconds(25)
            });
        var producer = Stopwatch.StartNew();
        for (var index = 0; index < 1_000; index++)
        {
            _ = metrics.Record(
                RuntimeMetricNames.ToolExecutionMilliseconds,
                RuntimeMetricKind.Histogram,
                index);
        }
        producer.Stop();

        Assert.True(producer.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken));
        var shutdown = Stopwatch.StartNew();
        Assert.False(await metrics.StopAsync());
        shutdown.Stop();
        Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(metrics.Health.Dropped > 0);

        sink.Release.TrySetResult(true);
    }

    [Fact]
    public async Task DimensionsNormalizeToFiniteFrameworkValues()
    {
        var sink = new CollectingSink();
        var metrics = new RuntimeMetricsEmitter(sink);

        Assert.True(
            metrics.Record(
                RuntimeMetricNames.EventPumpDropped,
                RuntimeMetricKind.Counter,
                1,
                workloadClass: "run-123",
                outcome: "npc-456",
                engine: "custom-engine"));
        Assert.True(await metrics.StopAsync());

        var record = Assert.Single(sink.Records);
        Assert.Equal(
            ProviderWorkloadClasses.Interactive,
            record.Dimensions.WorkloadClass);
        Assert.Equal(
            RuntimeMetricOutcomes.Failure,
            record.Dimensions.Outcome);
        Assert.Equal("core", record.Dimensions.Engine);
    }

    [Fact]
    public async Task ProviderAdmissionReportsQueueDepthAndWait()
    {
        var sink = new CollectingSink();
        var metrics = new RuntimeMetricsEmitter(sink);
        using var admission = new ProviderWorkloadAdmission(
            maximumConcurrentCalls: 1,
            maximumConcurrentBackgroundCalls: 1,
            metrics: metrics);
        using var first = await admission.AcquireAsync(
            ProviderWorkloadClasses.Interactive,
            CancellationToken.None);
        var secondTask = admission.AcquireAsync(
                ProviderWorkloadClasses.Background,
                CancellationToken.None)
            .AsTask();

        await Task.Delay(20, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(secondTask.IsCompleted);
        first.Dispose();
        using var second = await secondTask;
        second.Dispose();
        Assert.True(await metrics.StopAsync());

        Assert.Contains(
            sink.Records,
            item => item.Name == RuntimeMetricNames.WorkloadQueueDepth
                    && item.Value >= 1);
        Assert.Contains(
            sink.Records,
            item => item.Name
                    == RuntimeMetricNames.WorkloadQueueWaitMilliseconds
                    && item.Dimensions.WorkloadClass
                    == ProviderWorkloadClasses.Background
                    && item.Value >= 10);
    }

    [Fact]
    public async Task ConversationCompactionReportsDurationAndReclaim()
    {
        var sink = new CollectingSink();
        var metrics = new RuntimeMetricsEmitter(sink);
        var manager = new ConversationContextManager(
            new ConversationContextOptions
            {
                MaxRequestMessages = 8,
                MaxRequestUtf8Bytes = 4_096,
                RecentMessagesToKeep = 2,
                MaxSummaryUtf8Bytes = 3_072
            },
            new ExtractiveConversationCompactor(),
            new FakeRuntimeClock(),
            BoundedCancellationDispatcher.LifecycleShared,
            metrics: metrics);
        var messages = new List<NormalizedMessage>
        {
            Message("system", NormalizedRoles.System, "game rules")
        };
        for (var index = 0; index < 12; index++)
        {
            messages.Add(
                Message(
                    "old-" + index,
                    index % 2 == 0
                        ? NormalizedRoles.User
                        : NormalizedRoles.Assistant,
                    new string((char)('a' + index), 180)));
        }
        messages.Add(
            Message("latest", NormalizedRoles.User, "latest command"));
        messages.Add(
            Message("answer", NormalizedRoles.Assistant, "latest answer"));

        var view = await manager.PrepareAsync(
            "run",
            "turn",
            messages, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(view.Report.Compacted);
        Assert.True(await manager.StopAsync());
        Assert.True(await metrics.StopAsync());

        Assert.Contains(
            sink.Records,
            item => item.Name
                    == RuntimeMetricNames.CompactionDurationMilliseconds
                    && item.Dimensions.Outcome
                    == RuntimeMetricOutcomes.Success);
        Assert.Contains(
            sink.Records,
            item => item.Name
                    == RuntimeMetricNames.CompactionReclaimedMessages
                    && item.Value > 0);
    }

    private static NormalizedMessage Message(
        string id,
        string role,
        string text)
    {
        return new NormalizedMessage
        {
            MessageId = id,
            Role = role,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText(text)
            }
        };
    }

    private sealed class CollectingSink : IRuntimeMetricsSink
    {
        public ConcurrentQueue<RuntimeMetric> Records { get; } = new();

        public ValueTask RecordAsync(
            RuntimeMetric metric,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Enqueue(metric);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingSink : IRuntimeMetricsSink
    {
        public ValueTask RecordAsync(
            RuntimeMetric metric,
            CancellationToken cancellationToken = default)
        {
            _ = metric;
            _ = cancellationToken;
            throw new InvalidOperationException("sink failed");
        }
    }

    private sealed class BlockingSink : IRuntimeMetricsSink
    {
        public TaskCompletionSource<bool> Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask RecordAsync(
            RuntimeMetric metric,
            CancellationToken cancellationToken = default)
        {
            _ = metric;
            _ = cancellationToken;
            Entered.TrySetResult(true);
            await Release.Task.ConfigureAwait(false);
        }
    }
}

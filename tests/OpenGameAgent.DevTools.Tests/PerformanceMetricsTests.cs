using OpenGameAgent.Extensions;
using Xunit;

namespace OpenGameAgent.DevTools.Tests;

public sealed class PerformanceMetricsTests
{
    [Fact]
    public void PerformanceSummarySeparatesLatencyFailuresUsageAndDurableWrites()
    {
        var recording = new GameAgentTraceRecording(new[]
        {
            Entry(1, "input.received", "{\"queueMilliseconds\":5,\"inputPreparationMilliseconds\":2,\"sessionLoadMilliseconds\":3}", 0),
            Entry(2, "context.collected", "{\"durationMilliseconds\":4}", 1),
            Entry(3, "tools.collected", "{\"durationMilliseconds\":2}", 2),
            Entry(4, "route.selected", "{\"route\":\"Agent\",\"durationMilliseconds\":6}", 3),
            Entry(5, "skills.selected", "{\"durationMilliseconds\":1}", 4),
            Entry(6, "model.request.started", "{\"runId\":\"run\",\"turn\":1,\"model\":\"requested\"}", 5),
            Entry(7, "kernel.messagestarted", "{\"runId\":\"run\",\"turn\":1}", 15),
            Entry(8, "kernel.messageended", "{\"runId\":\"run\",\"turn\":1,\"provider\":\"tool-provider\",\"responseModel\":\"tool-model\",\"providerAttempts\":{\"retry\":{\"retries\":1},\"fallback\":{\"fallbacks\":1}}}", 18),
            Entry(9, "kernel.toolstarted", "{\"runId\":\"run\",\"turn\":1,\"tool\":\"build\",\"toolCallId\":\"call-1\"}", 20),
            Entry(10, "kernel.toolended", "{\"runId\":\"run\",\"turn\":1,\"tool\":\"build\",\"toolCallId\":\"call-1\",\"toolError\":false,\"failureCategory\":\"None\",\"outcomeUncertain\":true,\"action\":{\"operationId\":\"op-1\",\"status\":\"uncertain\",\"hostMilliseconds\":3,\"frameworkMilliseconds\":2,\"duplicateExecutionPrevented\":true,\"recovered\":false}}", 25),
            Entry(11, "kernel.toolstarted", "{\"runId\":\"run\",\"turn\":1,\"tool\":\"manage_task_plan\",\"toolCallId\":\"call-2\",\"operation\":\"replace_remaining\"}", 26),
            Entry(12, "kernel.toolended", "{\"runId\":\"run\",\"turn\":1,\"tool\":\"manage_task_plan\",\"toolCallId\":\"call-2\",\"operation\":\"replace_remaining\",\"toolError\":true,\"failureCategory\":\"Timeout\",\"outcomeUncertain\":true}", 31),
            Entry(13, "model.request.started", "{\"runId\":\"run\",\"turn\":2,\"model\":\"requested\"}", 32),
            Entry(14, "kernel.messagestarted", "{\"runId\":\"run\",\"turn\":2}", 35),
            Entry(15, "kernel.messageended", "{\"runId\":\"run\",\"turn\":2,\"provider\":\"provider\",\"responseModel\":\"resolved\"}", 40),
            Entry(16, "run.completed", "{\"status\":\"Completed\",\"usage\":{\"totalTokens\":12,\"cost\":{\"known\":true,\"total\":0.25}}}", 50),
        });

        var summary = GameAgentPerformanceSummary.Create(recording);

        var run = Assert.Single(summary.Runs);
        Assert.Equal("Agent", run.Route);
        Assert.Equal("provider", run.Provider);
        Assert.Equal("resolved", run.Model);
        Assert.Equal(5, run.Latency.QueueMilliseconds);
        Assert.Equal(15, run.Latency.TimeToFirstResponseMilliseconds);
        Assert.Equal(10, run.Latency.ProviderTimeToFirstResponseMilliseconds);
        Assert.Equal(21, run.Latency.ModelRequestMilliseconds);
        Assert.Equal(10, run.Latency.ToolExecutionMilliseconds);
        Assert.Equal(3, run.Latency.HostActionMilliseconds);
        Assert.Equal(2, run.Latency.DurableActionFrameworkMilliseconds);
        Assert.Equal(19, run.Latency.FrameworkOverheadMilliseconds);
        Assert.Equal(55, run.Latency.TotalMilliseconds);
        Assert.Equal(2, summary.ToolCalls);
        Assert.Equal(0.5, summary.ToolSuccessRate);
        Assert.Equal(1, summary.TimedOutTools);
        Assert.Equal(0.5, summary.ToolTimeoutRate);
        Assert.Equal(1, summary.WorldWrites);
        Assert.Equal(1, summary.UncertainWriteRate);
        Assert.Equal(1, summary.UncertainWrites);
        Assert.Equal(1, summary.DuplicateWritesPrevented);
        Assert.Equal(1, summary.ProviderRetries);
        Assert.Equal(1, summary.ProviderFallbacks);
        Assert.Equal(1, summary.Replans);
        Assert.All(run.Tools, tool =>
        {
            Assert.Equal("tool-provider", tool.Provider);
            Assert.Equal("tool-model", tool.Model);
        });
        Assert.Equal(12, summary.TotalTokens);
        Assert.Equal(0.25, summary.TotalCost);
        Assert.Contains("\"toolSuccessRate\"", summary.ToJson(), StringComparison.Ordinal);
        Assert.Contains("route=Agent", summary.ToText(), StringComparison.Ordinal);
        Assert.Single(summary.ToJsonLines().Split(Environment.NewLine));
    }

    [Fact]
    public async Task BenchmarkRunnerSupportsConcurrencyFaultsExportsAndConfigurableThresholds()
    {
        var active = 0;
        var maximumActive = 0;
        var scenario = new GameAgentBenchmarkScenario(
            "fixed-fake-provider-and-tool",
            async (iteration, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                try
                {
                    await Task.Delay(5, cancellationToken);
                    if (iteration == 1)
                    {
                        throw new InvalidOperationException("injected fault");
                    }

                    return new GameAgentTraceRecording(new[]
                    {
                        Entry(1, "input.received", "{\"queueMilliseconds\":0}", 0, "input-" + iteration),
                        Entry(2, "route.selected", "{\"route\":\"QuickResponse\"}", 1, "input-" + iteration),
                        Entry(3, "run.completed", "{\"status\":\"Completed\",\"usage\":{\"totalTokens\":2,\"cost\":{\"known\":false,\"total\":null}}}", 3, "input-" + iteration),
                    });
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        var report = await GameAgentBenchmarkRunner.RunAsync(
            new[] { scenario },
            new GameAgentBenchmarkOptions
            {
                Iterations = 3,
                WarmupIterations = 0,
                MaximumConcurrency = 2,
                IterationTimeout = TimeSpan.FromSeconds(1),
            },
            new GameAgentBenchmarkThresholds
            {
                MaximumFailureRate = 0.5,
                MaximumP95TotalMilliseconds = 100,
            },
            TestContext.Current.CancellationToken);

        Assert.True(maximumActive >= 2);
        Assert.Equal(3, report.Iterations.Count);
        Assert.Equal(1, report.Failures);
        Assert.True(report.Passed);
        Assert.Contains("fixed-fake-provider-and-tool", report.ToJsonLines(), StringComparison.Ordinal);
        Assert.Contains("Iterations: 3", report.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedStableInputIdCreatesSeparateAttemptMetrics()
    {
        var recording = new GameAgentTraceRecording(new[]
        {
            Entry(1, "input.received", "{}", 0),
            Entry(2, "route.selected", "{\"route\":\"QuickResponse\"}", 1),
            Entry(3, "run.completed", "{\"status\":\"SessionConflict\",\"usage\":{\"totalTokens\":1}}", 2),
            Entry(4, "input.received", "{}", 3),
            Entry(5, "route.selected", "{\"route\":\"QuickResponse\"}", 4),
            Entry(6, "run.completed", "{\"status\":\"Completed\",\"usage\":{\"totalTokens\":2}}", 6),
        });

        var summary = GameAgentPerformanceSummary.Create(recording);

        Assert.Collection(
            summary.Runs,
            attempt =>
            {
                Assert.Equal("SessionConflict", attempt.Status);
                Assert.Equal(1, attempt.TotalTokens);
            },
            attempt =>
            {
                Assert.Equal("Completed", attempt.Status);
                Assert.Equal(2, attempt.TotalTokens);
            });
    }

    private static GameAgentTraceEntry Entry(
        long sequence,
        string kind,
        string details,
        int milliseconds,
        string inputId = "input") =>
        new(
            sequence,
            kind,
            "session",
            "actor",
            inputId,
            new GameMoment("world", 1),
            DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds),
            details);

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current
                || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}

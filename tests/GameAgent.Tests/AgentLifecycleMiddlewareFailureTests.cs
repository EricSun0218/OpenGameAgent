using GameAgent.Core;

namespace GameAgent.Tests;

public sealed class AgentLifecycleMiddlewareFailureTests
{
    [Fact]
    public async Task RequiredTimeoutFailsClosedWithStableReason()
    {
        var middleware = new BlockingMiddleware("required-timeout");
        using var pipeline = Pipeline(middleware, required: true);

        var error = await Assert.ThrowsAsync<AgentLifecycleMiddlewareException>(
            () => pipeline.InvokeAsync(
                    Event("required-timeout-run"),
                    allowRejection: true,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("middleware_timeout", error.ReasonCode);
        Assert.Equal(1, pipeline.DetachedCallCount);
        middleware.Release.TrySetResult(true);
        Assert.True(await pipeline.StopAsync());
    }

    [Fact]
    public async Task RequiredCapacityTimeoutFailsClosedWithStableReason()
    {
        var middleware = new BlockingMiddleware("required-capacity");
        using var pipeline = Pipeline(middleware, required: true);
        var first = pipeline.InvokeAsync(
                Event("capacity-first"),
                allowRejection: true,
                CancellationToken.None)
            .AsTask();
        await middleware.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var firstError = await Assert.ThrowsAsync<
            AgentLifecycleMiddlewareException>(() => first);
        Assert.Equal("middleware_timeout", firstError.ReasonCode);

        var secondError = await Assert.ThrowsAsync<
            AgentLifecycleMiddlewareException>(
            () => pipeline.InvokeAsync(
                    Event("capacity-second"),
                    allowRejection: true,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("middleware_capacity_timeout", secondError.ReasonCode);
        middleware.Release.TrySetResult(true);
        Assert.True(await pipeline.StopAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredFailureOrNullFailsClosedWithStableReason(
        bool returnNull)
    {
        using var pipeline = Pipeline(
            new FailingMiddleware(returnNull),
            required: true);

        var error = await Assert.ThrowsAsync<AgentLifecycleMiddlewareException>(
            () => pipeline.InvokeAsync(
                    Event("required-failure-run"),
                    allowRejection: true,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("middleware_failed", error.ReasonCode);
        Assert.True(await pipeline.StopAsync());
    }

    [Fact]
    public async Task OptionalTimeoutIsSkippedAndDetachedCallDrains()
    {
        var middleware = new BlockingMiddleware("optional-timeout");
        using var pipeline = Pipeline(middleware, required: false);

        await pipeline.InvokeAsync(
            Event("optional-timeout-run"),
            allowRejection: true,
            CancellationToken.None);

        Assert.Equal(1, pipeline.DetachedCallCount);
        Assert.False(await pipeline.StopAsync());
        middleware.Release.TrySetResult(true);
        await WaitUntilAsync(
            () => pipeline.DetachedCallCount == 0,
            TimeSpan.FromSeconds(2));
        Assert.True(await pipeline.StopAsync());
    }

    [Fact]
    public async Task TimeoutDispatchesMiddlewareCancellationAndDrainsSlot()
    {
        var middleware = new CancellationAwareMiddleware();
        using var pipeline = Pipeline(middleware, required: true);

        var error = await Assert.ThrowsAsync<AgentLifecycleMiddlewareException>(
            () => pipeline.InvokeAsync(
                    Event("cancellation-aware-timeout"),
                    allowRejection: true,
                    CancellationToken.None)
                .AsTask());

        Assert.Equal("middleware_timeout", error.ReasonCode);
        await middleware.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => pipeline.DetachedCallCount == 0,
            TimeSpan.FromSeconds(2));
        Assert.True(await pipeline.StopAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalFailureOrNullIsSkipped(bool returnNull)
    {
        using var pipeline = Pipeline(
            new FailingMiddleware(returnNull),
            required: false);

        await pipeline.InvokeAsync(
            Event("optional-failure-run"),
            allowRejection: true,
            CancellationToken.None);

        Assert.True(await pipeline.StopAsync());
    }

    [Fact]
    public async Task PipelineBudgetBoundsOptionalChainAndFailsLaterRequiredGate()
    {
        var blocking = new BlockingMiddleware("pipeline-blocking");
        var required = new FailingMiddleware(returnNull: false);
        using var pipeline = new AgentLifecyclePipeline(
            new[]
            {
                new AgentLifecycleMiddlewareRegistration(
                    blocking,
                    required: false),
                new AgentLifecycleMiddlewareRegistration(
                    required,
                    required: true)
            },
            new AgentLifecyclePipelineOptions
            {
                MaxConcurrentCalls = 1,
                MiddlewareTimeout = TimeSpan.FromMilliseconds(250),
                PipelineTimeout = TimeSpan.FromMilliseconds(40),
                ShutdownTimeout = TimeSpan.FromMilliseconds(25)
            });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<AgentLifecycleMiddlewareException>(
            () => pipeline.InvokeAsync(
                    Event("pipeline-budget-run"),
                    allowRejection: true,
                    CancellationToken.None)
                .AsTask());

        stopwatch.Stop();
        Assert.Equal("middleware_pipeline_timeout", error.ReasonCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        blocking.Release.TrySetResult(true);
        Assert.True(await pipeline.StopAsync());
    }

    private static AgentLifecyclePipeline Pipeline(
        IAgentLifecycleMiddleware middleware,
        bool required) =>
        new(
            new[]
            {
                new AgentLifecycleMiddlewareRegistration(
                    middleware,
                    required)
            },
            new AgentLifecyclePipelineOptions
            {
                MaxConcurrentCalls = 1,
                MiddlewareTimeout = TimeSpan.FromMilliseconds(25),
                ShutdownTimeout = TimeSpan.FromMilliseconds(25)
            });

    private static RunStartingLifecycleEvent Event(string runId) =>
        new(
            runId,
            agentId: null,
            worldId: null,
            sessionId: null,
            isResume: false);

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class BlockingMiddleware : IAgentLifecycleMiddleware
    {
        public BlockingMiddleware(string id)
        {
            MiddlewareId = id;
        }

        public string MiddlewareId { get; }

        public string Version => "1";

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentLifecycleDecision> HandleAsync(
            AgentLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            _ = lifecycleEvent;
            _ = cancellationToken;
            Entered.TrySetResult(true);
            await Release.Task;
            return AgentLifecycleDecision.Continue;
        }
    }

    private sealed class FailingMiddleware : IAgentLifecycleMiddleware
    {
        private readonly bool _returnNull;

        public FailingMiddleware(bool returnNull)
        {
            _returnNull = returnNull;
        }

        public string MiddlewareId => _returnNull
            ? "null-result"
            : "throwing-result";

        public string Version => "1";

        public ValueTask<AgentLifecycleDecision> HandleAsync(
            AgentLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            _ = lifecycleEvent;
            cancellationToken.ThrowIfCancellationRequested();
            if (_returnNull)
            {
                return new ValueTask<AgentLifecycleDecision>(
                    (AgentLifecycleDecision)null!);
            }

            throw new InvalidOperationException("middleware failed");
        }
    }

    private sealed class CancellationAwareMiddleware
        : IAgentLifecycleMiddleware
    {
        public string MiddlewareId => "cancellation-aware";

        public string Version => "1";

        public TaskCompletionSource<bool> Cancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentLifecycleDecision> HandleAsync(
            AgentLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            _ = lifecycleEvent;
            using var registration = cancellationToken.Register(
                () => Cancelled.TrySetResult(true));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}

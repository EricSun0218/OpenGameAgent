using System.Collections.Concurrent;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Testing;

namespace GameAgent.Tests;

public sealed class ToolSchedulingTests
{
    [Fact]
    public void PlannerBuildsEffectAndConflictAwareSegmentsInModelCallOrder()
    {
        var tools = CreateTools();
        var planner = new ToolBatchPlanner();
        var calls = new[]
        {
            Request(tools["read"], "call-4", 4, "agent-a", "world"),
            Request(tools["read"], "call-0", 0, "agent-a", "zone:a"),
            Request(tools["read"], "call-1", 1, "agent-a", "zone:b"),
            Request(tools["read"], "call-2", 2, "agent-a", "zone:a"),
            Request(tools["local"], "call-3", 3, "agent-a", "agent:a"),
            Request(tools["main"], "call-5", 5, "agent-a", "ui")
        };

        var plan = planner.Plan(calls);

        Assert.Equal(Enumerable.Range(0, 6).Select(value => (long)value), plan.Calls.Select(
            call => call.Sequence));
        Assert.Equal(new[] { 2, 1, 1, 1, 1 }, plan.Segments.Select(
            segment => segment.Calls.Count));
        Assert.True(plan.Segments[0].CanRunConcurrently);
        Assert.All(plan.Segments.Skip(1), segment => Assert.False(segment.CanRunConcurrently));
        Assert.Equal(
            new long[] { 0, 1 },
            plan.Segments[0].Calls.Select(call => call.Sequence));
        Assert.Equal(2, plan.Segments[1].Calls[0].Sequence);
        Assert.Equal(3, plan.Segments[2].Calls[0].Sequence);
        Assert.Equal(4, plan.Segments[3].Calls[0].Sequence);
        Assert.Equal(5, plan.Segments[4].Calls[0].Sequence);
    }

    [Fact]
    public async Task SchedulerAllowsUnorderedCompletionButReturnsModelCallOrder()
    {
        var tool = CreateTools()["read"];
        var plan = new ToolBatchPlanner().Plan(
            new[]
            {
                Request(tool, "call-0", 0, "agent-a", "resource:0"),
                Request(tool, "call-1", 1, "agent-a", "resource:1"),
                Request(tool, "call-2", 2, "agent-a", "resource:2")
            });
        var executor = new OutOfOrderExecutor();
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(maxParallelism: 3));

        var results = await scheduler.ExecuteAsync(plan, executor);

        Assert.Equal(new long[] { 0, 1, 2 }, results.Select(
            result => result.Request.Sequence));
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(new long[] { 2, 1, 0 }, executor.CompletionOrder);
        Assert.True(executor.MaxConcurrency >= 2);
        Assert.Equal(0, scheduler.QueuedCalls);
    }

    [Fact]
    public async Task WorldCommandIsAGlobalBarrierAcrossConcurrentBatches()
    {
        var tools = CreateTools();
        var planner = new ToolBatchPlanner();
        var readPlan = planner.Plan(
            new[] { Request(tools["read"], "read", 0, "agent-a", "world") });
        var worldPlan = planner.Plan(
            new[] { Request(tools["world"], "write", 0, "agent-b", "world") });
        var executor = new BarrierExecutor();
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(maxParallelism: 4));

        var readTask = scheduler.ExecuteAsync(readPlan, executor).AsTask();
        await executor.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var worldTask = scheduler.ExecuteAsync(worldPlan, executor).AsTask();

        await Task.Delay(50);
        Assert.False(executor.WorldEntered.Task.IsCompleted);

        executor.ReleaseRead.TrySetResult();
        await Task.WhenAll(readTask, worldTask);
        Assert.True(executor.WorldEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task SameAgentLocalWritesAreSerializedAcrossConcurrentBatches()
    {
        var tool = CreateTools()["local"];
        var planner = new ToolBatchPlanner();
        var firstPlan = planner.Plan(
            new[] { Request(tool, "local-1", 0, "agent-a", "state:one") });
        var secondPlan = planner.Plan(
            new[] { Request(tool, "local-2", 0, "agent-a", "state:two") });
        var executor = new ConcurrencyExecutor(delayMs: 60);
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(maxParallelism: 4));

        await Task.WhenAll(
            scheduler.ExecuteAsync(firstPlan, executor).AsTask(),
            scheduler.ExecuteAsync(secondPlan, executor).AsTask());

        Assert.Equal(1, executor.MaxConcurrency);
    }

    [Fact]
    public async Task QueueAndJsonLimitsFailClosed()
    {
        var tool = CreateTools()["read"];
        var planner = new ToolBatchPlanner();
        var firstPlan = planner.Plan(
            new[] { Request(tool, "held", 0, "agent-a", "held") });
        var secondPlan = planner.Plan(
            new[] { Request(tool, "rejected", 0, "agent-b", "other") });
        var blockingExecutor = new BlockingExecutor();
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(maxParallelism: 2, maxQueuedCalls: 1));

        var firstTask = scheduler.ExecuteAsync(firstPlan, blockingExecutor).AsTask();
        await blockingExecutor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queueException = await Assert.ThrowsAsync<ToolQueueCapacityExceededException>(
            () => scheduler.ExecuteAsync(secondPlan, blockingExecutor).AsTask());
        Assert.Equal("tool_queue_capacity_exceeded", queueException.CapacityCode);
        blockingExecutor.Release.TrySetResult();
        await firstTask;

        var strictPlanner = new ToolBatchPlanner(
            new ToolSchedulerLimits(
                argumentJsonLimits: new JsonValueLimits(
                    maxUtf8Bytes: 4_096,
                    maxDepth: 2,
                    maxNodes: 64,
                    maxStringUtf8Bytes: 1_024,
                    maxContainerItems: 32)));
        var deepRequest = Request(
            tool,
            "deep",
            0,
            "agent-a",
            "deep",
            Json("""{"a":{"b":{"c":1}}}"""));
        var depthException = Assert.Throws<RuntimeContentLimitException>(
            () => strictPlanner.Plan(new[] { deepRequest }));
        Assert.Equal("json_depth_exceeded", depthException.LimitCode);
    }

    [Fact]
    public async Task OversizedToolResultBecomesABoundedFailure()
    {
        var tool = CreateTools()["read"];
        var plan = new ToolBatchPlanner().Plan(
            new[] { Request(tool, "result", 0, "agent-a", "result") });
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(
                resultJsonLimits: new JsonValueLimits(
                    maxUtf8Bytes: 4_096,
                    maxDepth: 2,
                    maxNodes: 64,
                    maxStringUtf8Bytes: 1_024,
                    maxContainerItems: 32)));

        var results = await scheduler.ExecuteAsync(plan, new DeepResultExecutor());

        var result = Assert.Single(results);
        Assert.False(result.IsSuccess);
        Assert.Equal("json_depth_exceeded", result.ErrorCode);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ExpiredAbsoluteDeadlineNeverDispatchesTheExecutor()
    {
        var clock = new FakeRuntimeClock();
        var request = Request(
            CreateTools()["read"],
            "already-expired",
            0,
            "agent-a",
            "resource:expired");
        request.BindExecutionDeadline(clock.UtcNow);
        var executor = new CountingImmediateExecutor();
        var scheduler = new ToolBatchScheduler();

        var result = Assert.Single(
            await scheduler.ExecuteAsync(
                new ToolBatchPlanner().Plan(new[] { request }),
                executor,
                clock));

        Assert.False(result.IsSuccess);
        Assert.Equal("tool_deadline_expired", result.ErrorCode);
        Assert.False(result.MayHaveExecuted);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task BoundDeadlineIncludesPreSchedulerTimeDespiteUtcRollback()
    {
        var clock = new FakeRuntimeClock();
        var request = Request(
            CreateTools()["read"],
            "rollback-expired",
            0,
            "agent-a",
            "resource:expired");
        request.BindExecutionDeadline(
            clock.UtcNow.AddMilliseconds(25),
            MonotonicDeadline.Start(TimeSpan.FromMilliseconds(25)));
        clock.Advance(TimeSpan.FromHours(-1));
        await Task.Delay(75);
        var executor = new CountingImmediateExecutor();
        var scheduler = new ToolBatchScheduler();

        var result = Assert.Single(
            await scheduler.ExecuteAsync(
                new ToolBatchPlanner().Plan(new[] { request }),
                executor,
                clock));

        Assert.False(result.IsSuccess);
        Assert.Equal("tool_deadline_expired", result.ErrorCode);
        Assert.False(result.MayHaveExecuted);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task AbsoluteDeadlineIncludesEarlierSerialSegments()
    {
        var clock = new FakeRuntimeClock();
        var tool = CreateTools()["world"];
        var first = Request(
            tool,
            "first",
            0,
            "agent-a",
            "world");
        var second = Request(
            tool,
            "second",
            1,
            "agent-a",
            "world");
        first.BindExecutionDeadline(
            clock.UtcNow.AddMilliseconds(1_000));
        second.BindExecutionDeadline(
            clock.UtcNow.AddMilliseconds(100));
        var executor = new AdvancingFirstExecutor(
            clock,
            TimeSpan.FromMilliseconds(150));
        var scheduler = new ToolBatchScheduler();

        var results = await scheduler.ExecuteAsync(
            new ToolBatchPlanner().Plan(new[] { first, second }),
            executor,
            clock);

        Assert.True(results[0].IsSuccess);
        Assert.Equal("tool_deadline_expired", results[1].ErrorCode);
        Assert.False(results[1].MayHaveExecuted);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task CompletionObservedAfterAbsoluteDeadlineIsQuarantined()
    {
        var clock = new FakeRuntimeClock();
        var request = Request(
            CreateTools()["world"],
            "late-completion",
            0,
            "agent-a",
            "world");
        request.BindExecutionDeadline(
            clock.UtcNow.AddSeconds(1));
        var executor = new AdvancingFirstExecutor(
            clock,
            TimeSpan.FromSeconds(2));
        var scheduler = new ToolBatchScheduler();

        var result = Assert.Single(
            await scheduler.ExecuteAsync(
                new ToolBatchPlanner().Plan(new[] { request }),
                executor,
                clock));

        Assert.False(result.IsSuccess);
        Assert.Equal("tool_timeout", result.ErrorCode);
        Assert.True(result.MayHaveExecuted);
        Assert.Equal(1, executor.CallCount);
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SideEffectTimeoutFailsFastUntilExecutorStops()
    {
        var descriptor = Descriptor(
            "blocking",
            ToolEffects.WorldCommand,
            ThreadAffinities.HostManaged);
        descriptor.TimeoutMs = 20;
        var registry = new ToolCatalogRegistry();
        var tool = Assert.Single(registry.Replace(new[] { descriptor }).Tools);
        var plan = new ToolBatchPlanner().Plan(
            new[] { Request(tool, "blocking", 0, "agent-a", "world") });
        var executor = new CancellationIgnoringExecutor();
        var scheduler = new ToolBatchScheduler();

        var result = Assert.Single(
            await scheduler.ExecuteAsync(plan, executor).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.False(result.IsSuccess);
        Assert.Equal("tool_timeout", result.ErrorCode);
        Assert.True(result.MayHaveExecuted);
        Assert.Equal(0, scheduler.QueuedCalls);
        Assert.Equal(1, scheduler.DetachedExecutionCount);

        var conflictingPlan = new ToolBatchPlanner().Plan(
            new[] { Request(tool, "conflicting", 0, "agent-b", "world") });
        var conflictingResult = Assert.Single(
            await scheduler
                .ExecuteAsync(conflictingPlan, executor)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(
            "tool_dispatch_blocked_by_detached_execution",
            conflictingResult.ErrorCode);
        Assert.False(conflictingResult.MayHaveExecuted);
        Assert.Equal(1, executor.CallCount);

        executor.Release.TrySetResult();
        await executor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
        Assert.Equal(0, scheduler.DetachedExecutionCount);

        var afterRelease = Assert.Single(
            await scheduler.ExecuteAsync(
                new ToolBatchPlanner().Plan(
                    new[] { Request(tool, "after-release", 0, "agent-b", "world") }),
                executor));
        Assert.True(afterRelease.IsSuccess);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task PureReadTimeoutQuarantinesTheSameVersionUntilExecutorStops()
    {
        var descriptor = Descriptor(
            "blocking-read",
            ToolEffects.PureRead,
            ThreadAffinities.AnyThread);
        descriptor.TimeoutMs = 20;
        var registry = new ToolCatalogRegistry();
        var tool = Assert.Single(registry.Replace(new[] { descriptor }).Tools);
        var planner = new ToolBatchPlanner();
        var executor = new CancellationIgnoringExecutor();
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(maxParallelism: 1));

        var first = Assert.Single(
            await scheduler.ExecuteAsync(
                    planner.Plan(
                        new[]
                        {
                            Request(
                                tool,
                                "blocking-read",
                                0,
                                "agent-a",
                                "resource:a")
                        }),
                    executor)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.False(first.IsSuccess);
        Assert.Equal("tool_timeout", first.ErrorCode);
        Assert.False(first.MayHaveExecuted);

        var second = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "next-read",
                            0,
                            "agent-b",
                            "resource:b")
                    }),
                executor));
        Assert.Equal(
            "tool_dispatch_blocked_by_detached_execution",
            second.ErrorCode);
        Assert.False(second.MayHaveExecuted);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(1, scheduler.DetachedExecutionCount);

        executor.Release.TrySetResult();
        await executor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
        var secondResult = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "after-release",
                            0,
                            "agent-b",
                            "resource:b")
                    }),
                executor));
        Assert.True(secondResult.IsSuccess);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task DetachedPureReadDoesNotQuarantineADifferentVersionWithDisjointScope()
    {
        var versionOne = Descriptor(
            "versioned-read",
            ToolEffects.PureRead,
            ThreadAffinities.AnyThread);
        versionOne.TimeoutMs = 20;
        var versionTwo = Descriptor(
            "versioned-read",
            ToolEffects.PureRead,
            ThreadAffinities.AnyThread);
        versionTwo.Version = "2.0.0";
        var firstTool = Assert.Single(
            new ToolCatalogRegistry().Replace(new[] { versionOne }).Tools);
        var secondTool = Assert.Single(
            new ToolCatalogRegistry().Replace(new[] { versionTwo }).Tools);
        var executor = new SelectiveCancellationIgnoringExecutor(
            "versioned-read",
            "1.0.0");
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(maxParallelism: 2));
        var planner = new ToolBatchPlanner();

        var first = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            firstTool,
                            "version-one",
                            0,
                            "agent-a",
                            "resource:a")
                    }),
                executor));
        Assert.Equal("tool_timeout", first.ErrorCode);

        var second = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            secondTool,
                            "version-two",
                            0,
                            "agent-b",
                            "resource:b")
                    }),
                executor));
        Assert.True(second.IsSuccess);
        Assert.Equal(1, executor.BlockedCallCount);
        Assert.Equal(1, executor.ImmediateCallCount);

        executor.Release.TrySetResult();
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ThrowingCancellationCallbackCannotReleaseTimeoutLease()
    {
        var descriptor = Descriptor(
            "throwing-read",
            ToolEffects.PureRead,
            ThreadAffinities.AnyThread);
        descriptor.TimeoutMs = 20;
        var registry = new ToolCatalogRegistry();
        var tool = Assert.Single(registry.Replace(new[] { descriptor }).Tools);
        var planner = new ToolBatchPlanner();
        var executor = new ThrowingCancellationExecutor();
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(maxParallelism: 1));

        var first = Assert.Single(
            await scheduler.ExecuteAsync(
                    planner.Plan(
                        new[]
                        {
                            Request(
                                tool,
                                "throwing-read",
                                0,
                                "agent-a",
                                "resource:a")
                        }),
                    executor)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));
        await executor.CallbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("tool_timeout", first.ErrorCode);
        var second = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "next-read",
                            0,
                            "agent-b",
                            "resource:b")
                    }),
                executor));
        Assert.Equal(
            "tool_dispatch_blocked_by_detached_execution",
            second.ErrorCode);
        Assert.False(second.MayHaveExecuted);
        Assert.Equal(1, executor.CallCount);

        executor.Release.TrySetResult();
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
        var secondResult = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "after-release",
                            0,
                            "agent-b",
                            "resource:b")
                    }),
                executor));
        Assert.True(secondResult.IsSuccess);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task BlockingCancellationCallbackCannotDefeatToolTimeout()
    {
        var descriptor = Descriptor(
            "blocking-cancel-read",
            ToolEffects.PureRead,
            ThreadAffinities.AnyThread);
        descriptor.TimeoutMs = 20;
        var tool = Assert.Single(
            new ToolCatalogRegistry()
                .Replace(new[] { descriptor })
                .Tools);
        var executor = new BlockingCancellationCallbackExecutor();
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(maxParallelism: 1));

        try
        {
            var result = Assert.Single(
                await scheduler.ExecuteAsync(
                        new ToolBatchPlanner().Plan(
                            new[]
                            {
                                Request(
                                    tool,
                                    "blocking-cancel",
                                    0,
                                    "agent-a",
                                    "resource:a")
                            }),
                        executor)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal("tool_timeout", result.ErrorCode);
            await executor.CallbackInvoked.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            var second = Assert.Single(
                await scheduler.ExecuteAsync(
                    new ToolBatchPlanner().Plan(
                        new[]
                        {
                            Request(
                                tool,
                                "after-blocked-cancel",
                                0,
                                "agent-b",
                                "resource:b")
                        }),
                    executor));
            Assert.Equal(
                "tool_dispatch_blocked_by_detached_execution",
                second.ErrorCode);
            Assert.False(second.MayHaveExecuted);
            Assert.Equal(1, executor.CallCount);

            executor.Release.TrySetResult();
            Assert.True(
                await scheduler.DrainDetachedExecutionsAsync(
                    TimeSpan.FromSeconds(2)));
            var secondResult = Assert.Single(
                await scheduler.ExecuteAsync(
                    new ToolBatchPlanner().Plan(
                        new[]
                        {
                            Request(
                                tool,
                                "after-release",
                                0,
                                "agent-b",
                                "resource:b")
                        }),
                    executor));
            Assert.True(secondResult.IsSuccess);
            Assert.Equal(2, executor.CallCount);
        }
        finally
        {
            executor.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task HighFrequencyFastToolsCancelEveryTimeoutWait()
    {
        const int callCount = 2_048;
        const int maxParallelism = 8;
        var tool = CreateTools()["read"];
        var plan = new ToolBatchPlanner(
                new ToolSchedulerLimits(
                    maxBatchSize: callCount,
                    maxParallelism: maxParallelism,
                    maxQueuedCalls: callCount))
            .Plan(
                Enumerable.Range(0, callCount)
                    .Select(
                        index => Request(
                            tool,
                            $"fast-{index}",
                            index,
                            "agent-a",
                            $"resource:{index}"))
                    .ToArray());
        var timeoutDelay = new TrackingTimeoutDelay();
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(
                maxBatchSize: callCount,
                maxParallelism: maxParallelism,
                maxQueuedCalls: callCount),
            timeoutDelay);

        var results = await scheduler.ExecuteAsync(
            plan,
            new ImmediateExecutor());

        Assert.Equal(callCount, results.Count);
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(callCount, timeoutDelay.Started);
        Assert.True(
            SpinWait.SpinUntil(
                () => timeoutDelay.Cancelled == timeoutDelay.Started
                      && timeoutDelay.Active == 0,
                TimeSpan.FromSeconds(5)),
            "Timeout wait cancellation cleanup did not drain.");
        Assert.Equal(timeoutDelay.Started, timeoutDelay.Cancelled);
        Assert.Equal(0, timeoutDelay.Active);
        Assert.InRange(
            timeoutDelay.PeakActive,
            1,
            BoundedCancellationDispatcher.DefaultCapacity);
    }

    [Fact]
    public async Task TimeoutWaitCancellationCallbackCannotChangeToolResult()
    {
        var tool = CreateTools()["read"];
        var plan = new ToolBatchPlanner().Plan(
            new[]
            {
                Request(
                    tool,
                    "fast",
                    0,
                    "agent-a",
                    "resource:fast")
            });
        var timeoutDelay = new TrackingTimeoutDelay(
            throwOnCancellation: true);
        var scheduler = new ToolBatchScheduler(
            timeoutDelay: timeoutDelay);

        var result = Assert.Single(
            await scheduler.ExecuteAsync(plan, new ImmediateExecutor()));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, timeoutDelay.Cancelled);
        Assert.Equal(0, timeoutDelay.Active);
    }

    [Fact]
    public async Task BlockingTimeoutWaitCancellationDoesNotDelayToolResult()
    {
        var timeoutDelay = new BlockingTimeoutDelay();
        var scheduler = new ToolBatchScheduler(timeoutDelay: timeoutDelay);
        var descriptor = Descriptor(
            "fast",
            ToolEffects.PureRead,
            ThreadAffinities.AnyThread);
        descriptor.TimeoutMs = 20;
        var tool = Assert.Single(
            new ToolCatalogRegistry().Replace(new[] { descriptor }).Tools);

        try
        {
            var result = Assert.Single(
                await scheduler.ExecuteAsync(
                        new ToolBatchPlanner().Plan(
                            new[]
                            {
                                Request(
                                    tool,
                                    "fast",
                                    0,
                                    "agent-a",
                                    "resource:fast")
                            }),
                        new ImmediateExecutor())
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.True(result.IsSuccess);
            await timeoutDelay.CallbackInvoked.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            timeoutDelay.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task SynchronousTimeoutInfrastructureFailureDoesNotDispatch()
    {
        var descriptor = Descriptor(
            "guarded",
            ToolEffects.WorldCommand,
            ThreadAffinities.HostManaged);
        var tool = Assert.Single(
            new ToolCatalogRegistry().Replace(new[] { descriptor }).Tools);
        var executor = new CancellationIgnoringExecutor();
        var scheduler = new ToolBatchScheduler(
            timeoutDelay: new ThrowingTimeoutDelay());

        var result = Assert.Single(
            await scheduler.ExecuteAsync(
                new ToolBatchPlanner().Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "guarded",
                            0,
                            "agent-a",
                            "world")
                    }),
                executor));

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "tool_timeout_infrastructure_exception",
            result.ErrorCode);
        Assert.False(result.MayHaveExecuted);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, scheduler.DetachedExecutionCount);
    }

    [Fact]
    public async Task CancellationRegistrationFailureCleansDelayWithoutDispatch()
    {
        var tool = CreateTools()["read"];
        var timeoutDelay = new TrackingTimeoutDelay();
        var dispatcher = new BoundedCancellationDispatcher(capacity: 2);
        var scheduler = new ToolBatchScheduler(
            limits: null,
            timeoutDelay: timeoutDelay,
            cancellationDispatcher: dispatcher,
            cancellationRegistrar: (_, _) =>
                throw new ObjectDisposedException("callerCancellation"));
        var executor = new CountingImmediateExecutor();

        var result = Assert.Single(
            await scheduler.ExecuteAsync(
                    new ToolBatchPlanner().Plan(
                        new[]
                        {
                            Request(
                                tool,
                                "disposed-caller",
                                0,
                                "agent-a",
                                "resource:a")
                        }),
                    executor)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.False(result.IsSuccess);
        Assert.Equal("tool_executor_exception", result.ErrorCode);
        Assert.False(result.MayHaveExecuted);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(1, timeoutDelay.Started);
        Assert.True(
            SpinWait.SpinUntil(
                () => timeoutDelay.Active == 0
                      && dispatcher.ActiveReservations == 0,
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task BlockingTimeoutCleanupExhaustsBoundedCapacityBeforeDispatch()
    {
        var tool = CreateTools()["read"];
        var delay = new BlockingTimeoutDelay();
        var dispatcher = new BoundedCancellationDispatcher(capacity: 2);
        var scheduler = new ToolBatchScheduler(
            limits: null,
            timeoutDelay: delay,
            cancellationDispatcher: dispatcher);
        var executor = new CountingImmediateExecutor();
        var planner = new ToolBatchPlanner();

        var first = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "first",
                            0,
                            "agent-a",
                            "resource:a")
                    }),
                executor));

        Assert.True(first.IsSuccess);
        await delay.CallbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, dispatcher.ActiveReservations);

        var rejected = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "rejected",
                            1,
                            "agent-b",
                            "resource:b")
                    }),
                executor));

        Assert.Equal(
            "tool_cancellation_capacity_exceeded",
            rejected.ErrorCode);
        Assert.False(rejected.MayHaveExecuted);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(1, dispatcher.ActiveReservations);

        delay.Release.TrySetResult();
        Assert.True(
            SpinWait.SpinUntil(
                () => dispatcher.ActiveReservations == 0,
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task FaultedTimeoutInfrastructureDoesNotDispatch()
    {
        var descriptor = Descriptor(
            "guarded",
            ToolEffects.WorldCommand,
            ThreadAffinities.HostManaged);
        var tool = Assert.Single(
            new ToolCatalogRegistry().Replace(new[] { descriptor }).Tools);
        var executor = new CancellationIgnoringExecutor();
        var scheduler = new ToolBatchScheduler(
            timeoutDelay: new FaultedTimeoutDelay());

        var result = Assert.Single(
            await scheduler.ExecuteAsync(
                new ToolBatchPlanner().Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "guarded-faulted",
                            0,
                            "agent-a",
                            "world")
                    }),
                executor));

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "tool_timeout_infrastructure_exception",
            result.ErrorCode);
        Assert.False(result.MayHaveExecuted);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, scheduler.DetachedExecutionCount);
    }

    [Fact]
    public async Task DetachedCensusIsBoundedImmutableAndDrainsAfterAllExecutionsFinish()
    {
        var descriptors = new[]
        {
            Descriptor("blocked-a", ToolEffects.PureRead, ThreadAffinities.AnyThread),
            Descriptor("blocked-b", ToolEffects.PureRead, ThreadAffinities.AnyThread)
        };
        foreach (var descriptor in descriptors)
        {
            descriptor.TimeoutMs = 20;
        }

        var tools = new ToolCatalogRegistry()
            .Replace(descriptors)
            .Tools
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var executor = new CancellationIgnoringExecutor();
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(
                maxParallelism: 2,
                maxDetachedSnapshotItems: 1));
        var plan = new ToolBatchPlanner().Plan(
            new[]
            {
                Request(
                    tools["blocked-a"],
                    "detached-a",
                    0,
                    "agent-a",
                    "resource:a",
                    Json("""{"secret":"must-not-appear"}""")),
                Request(
                    tools["blocked-b"],
                    "detached-b",
                    1,
                    "agent-b",
                    "resource:b")
            });

        var results = await scheduler.ExecuteAsync(plan, executor)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(results, result => Assert.Equal("tool_timeout", result.ErrorCode));
        Assert.Equal(2, scheduler.DetachedExecutionCount);
        var firstSnapshot = scheduler.GetDetachedExecutionSnapshot();
        var item = Assert.Single(firstSnapshot);
        Assert.Equal("timeout", item.Reason);
        Assert.Equal("pure_read", item.Effect);
        Assert.True(item.CapturedAt >= item.DetachedAt);
        Assert.True(item.Age >= TimeSpan.Zero);
        Assert.Equal(
            new[]
            {
                "Age",
                "CapturedAt",
                "DetachedAt",
                "Effect",
                "Reason",
                "ToolCallId",
                "ToolName",
                "ToolVersion"
            },
            typeof(DetachedToolExecutionSnapshot)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => scheduler.GetDetachedExecutionSnapshot(2));
        Assert.False(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromMilliseconds(10)));

        var concurrentReaders = Enumerable.Range(0, 16)
            .Select(
                _ => Task.Run(
                    () =>
                    {
                        for (var index = 0; index < 100; index++)
                        {
                            Assert.InRange(scheduler.DetachedExecutionCount, 0, 2);
                            Assert.InRange(
                                scheduler.GetDetachedExecutionSnapshot().Count,
                                0,
                                1);
                        }
                    }))
            .ToArray();
        executor.Release.TrySetResult();
        await Task.WhenAll(concurrentReaders);
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
        Assert.Equal(0, scheduler.DetachedExecutionCount);
        Assert.Empty(scheduler.GetDetachedExecutionSnapshot());
        Assert.Equal(item.ToolCallId, firstSnapshot[0].ToolCallId);
        Assert.Equal(item.CapturedAt, firstSnapshot[0].CapturedAt);
    }

    [Fact]
    public async Task DetachedLocalWriteBlocksWritesAndConflictsButNotUnrelatedReads()
    {
        var descriptors = new[]
        {
            Descriptor(
                "blocked-local",
                ToolEffects.AgentLocalWrite,
                ThreadAffinities.AnyThread),
            Descriptor("safe-read", ToolEffects.PureRead, ThreadAffinities.AnyThread),
            Descriptor("conflicting-read", ToolEffects.PureRead, ThreadAffinities.AnyThread),
            Descriptor(
                "other-write",
                ToolEffects.AgentLocalWrite,
                ThreadAffinities.AnyThread)
        };
        descriptors[0].TimeoutMs = 20;
        var tools = new ToolCatalogRegistry()
            .Replace(descriptors)
            .Tools
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var executor = new SelectiveCancellationIgnoringExecutor("blocked-local");
        var scheduler = new ToolBatchScheduler(
            new ToolSchedulerLimits(maxParallelism: 4));
        var planner = new ToolBatchPlanner();

        var timedOut = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tools["blocked-local"],
                            "blocked-local",
                            0,
                            "agent-a",
                            "resource:a")
                    }),
                executor));
        Assert.Equal("tool_timeout", timedOut.ErrorCode);
        Assert.Equal(1, scheduler.DetachedExecutionCount);

        var safeRead = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tools["safe-read"],
                            "safe-read",
                            0,
                            "agent-b",
                            "resource:b")
                    }),
                executor));
        Assert.True(safeRead.IsSuccess);

        var conflictingRead = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tools["conflicting-read"],
                            "conflicting-read",
                            0,
                            "agent-b",
                            "resource:a")
                    }),
                executor));
        Assert.Equal(
            "tool_dispatch_blocked_by_detached_side_effect",
            conflictingRead.ErrorCode);
        Assert.False(conflictingRead.MayHaveExecuted);

        var write = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tools["other-write"],
                            "other-write",
                            0,
                            "agent-c",
                            "resource:c")
                    }),
                executor));
        Assert.Equal(
            "tool_dispatch_blocked_by_detached_side_effect",
            write.ErrorCode);
        Assert.False(write.MayHaveExecuted);
        Assert.Equal(1, executor.BlockedCallCount);
        Assert.Equal(1, executor.ImmediateCallCount);
        Assert.Equal(0, scheduler.QueuedCalls);

        executor.Release.TrySetResult();
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CallerCancellationRegistersDetachedExecutionAndDrainHonorsCancellation()
    {
        var descriptor = Descriptor(
            "caller-cancelled",
            ToolEffects.PureRead,
            ThreadAffinities.AnyThread);
        descriptor.TimeoutMs = 2_000;
        var tool = Assert.Single(
            new ToolCatalogRegistry().Replace(new[] { descriptor }).Tools);
        var executor = new EnteredCancellationIgnoringExecutor();
        var scheduler = new ToolBatchScheduler();
        using var callerCancellation = new CancellationTokenSource();
        var execution = scheduler.ExecuteAsync(
                new ToolBatchPlanner().Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "caller-cancelled",
                            0,
                            "agent-a",
                            "resource:a")
                    }),
                executor,
                callerCancellation.Token)
            .AsTask();

        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, scheduler.DetachedExecutionCount);
        Assert.Equal(
            "caller_cancelled",
            Assert.Single(scheduler.GetDetachedExecutionSnapshot()).Reason);

        using var drainCancellation = new CancellationTokenSource();
        drainCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scheduler
                .DrainDetachedExecutionsAsync(
                    TimeSpan.FromSeconds(10),
                    drainCancellation.Token)
                .AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => scheduler
                .DrainDetachedExecutionsAsync(Timeout.InfiniteTimeSpan)
                .AsTask());

        executor.Release.TrySetResult();
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60_001)]
    public void ShutdownDrainTimeoutIsStrictlyBounded(int timeoutMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ToolSchedulerLimits(
                detachedShutdownDrainTimeoutMs: timeoutMs));
    }

    [Fact]
    public async Task LateExecutorFaultIsObservedAndCannotLeaveQuarantineBehind()
    {
        var descriptor = Descriptor(
            "late-fault",
            ToolEffects.PureRead,
            ThreadAffinities.AnyThread);
        descriptor.TimeoutMs = 20;
        var tool = Assert.Single(
            new ToolCatalogRegistry().Replace(new[] { descriptor }).Tools);
        var executor = new LateFaultExecutor();
        var scheduler = new ToolBatchScheduler();

        var timedOut = Assert.Single(
            await scheduler.ExecuteAsync(
                new ToolBatchPlanner().Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "late-fault",
                            0,
                            "agent-a",
                            "resource:a")
                    }),
                executor));
        Assert.Equal("tool_timeout", timedOut.ErrorCode);
        Assert.Equal(1, scheduler.DetachedExecutionCount);

        executor.Completion.TrySetException(
            new InvalidOperationException("late executor failure"));
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
        Assert.Equal(0, scheduler.DetachedExecutionCount);
    }

    [Fact]
    public async Task UnknownSideEffectStopsTheRemainingBatch()
    {
        var descriptor = Descriptor(
            "blocking",
            ToolEffects.WorldCommand,
            ThreadAffinities.HostManaged);
        descriptor.TimeoutMs = 20;
        var registry = new ToolCatalogRegistry();
        var tool = Assert.Single(registry.Replace(new[] { descriptor }).Tools);
        var plan = new ToolBatchPlanner().Plan(
            new[]
            {
                Request(tool, "blocking", 0, "agent-a", "world"),
                Request(tool, "must-not-run", 1, "agent-a", "other")
            });
        var executor = new CancellationIgnoringExecutor();
        var scheduler = new ToolBatchScheduler();

        var results = await scheduler
            .ExecuteAsync(plan, executor)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, results.Count);
        Assert.Equal("tool_timeout", results[0].ErrorCode);
        Assert.True(results[0].MayHaveExecuted);
        Assert.Equal(
            "tool_dispatch_blocked_by_unknown",
            results[1].ErrorCode);
        Assert.False(results[1].MayHaveExecuted);
        Assert.Equal(1, executor.CallCount);

        executor.Release.TrySetResult();
        await executor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PostDispatchClockFailureIsReturnedAfterExecutionCompletes()
    {
        var descriptor = Descriptor(
            "post-dispatch-clock",
            ToolEffects.WorldCommand,
            ThreadAffinities.HostManaged);
        descriptor.TimeoutMs = 20;
        var tool = Assert.Single(
            new ToolCatalogRegistry().Replace(new[] { descriptor }).Tools);
        var clock = new ArmedThrowingClock();
        var executor = new ClockArmingExecutor(clock);
        var scheduler = new ToolBatchScheduler();
        var planner = new ToolBatchPlanner();
        var request = Request(
            tool,
            "clock-failure",
            0,
            "agent-a",
            "world");

        var result = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(new[] { request }),
                executor,
                clock));

        Assert.Equal("tool_executor_exception", result.ErrorCode);
        Assert.True(result.MayHaveExecuted);
        Assert.Equal(0, scheduler.DetachedExecutionCount);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task CallerSourceDisposedDuringDispatchCannotLoseExecutionOwnership()
    {
        var descriptor = Descriptor(
            "dispose-caller-source",
            ToolEffects.WorldCommand,
            ThreadAffinities.HostManaged);
        descriptor.TimeoutMs = 20;
        var tool = Assert.Single(
            new ToolCatalogRegistry().Replace(new[] { descriptor }).Tools);
        var callerSource = new CancellationTokenSource();
        var callerToken = callerSource.Token;
        var executor = new CallerSourceDisposingExecutor(callerSource);
        var scheduler = new ToolBatchScheduler();
        var planner = new ToolBatchPlanner();

        var result = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "dispose-source",
                            0,
                            "agent-a",
                            "world")
                    }),
                executor,
                callerToken));

        Assert.Equal("tool_timeout", result.ErrorCode);
        Assert.True(result.MayHaveExecuted);
        Assert.Equal(1, scheduler.DetachedExecutionCount);

        var blocked = Assert.Single(
            await scheduler.ExecuteAsync(
                planner.Plan(
                    new[]
                    {
                        Request(
                            tool,
                            "must-not-overlap",
                            1,
                            "agent-b",
                            "world")
                    }),
                executor));
        Assert.Equal(
            "tool_dispatch_blocked_by_detached_execution",
            blocked.ErrorCode);
        Assert.Equal(1, executor.CallCount);

        executor.Release.TrySetResult();
        Assert.True(
            await scheduler.DrainDetachedExecutionsAsync(
                TimeSpan.FromSeconds(2)));
    }

    private static IReadOnlyDictionary<string, ToolCatalogEntry> CreateTools()
    {
        var registry = new ToolCatalogRegistry();
        var snapshot = registry.Replace(
            new[]
            {
                Descriptor("read", ToolEffects.PureRead, ThreadAffinities.AnyThread),
                Descriptor("local", ToolEffects.AgentLocalWrite, ThreadAffinities.AnyThread),
                Descriptor("world", ToolEffects.WorldCommand, ThreadAffinities.HostManaged),
                Descriptor("main", ToolEffects.PureRead, ThreadAffinities.EngineMainThread)
            });
        return snapshot.Tools.ToDictionary(
            tool => tool.Name,
            StringComparer.Ordinal);
    }

    private static ToolDescriptor Descriptor(
        string name,
        string effect,
        string affinity)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = "1.0.0",
            Description = $"{name} tool",
            ParametersSchema = Json("""{"type":"object"}"""),
            Effect = effect,
            ThreadAffinity = affinity,
            TimeoutMs = 2_000,
            RetryPolicy = "never",
            IdempotencyPolicy =
                effect is ToolEffects.WorldCommand or ToolEffects.ExternalWrite
                    ? ToolIdempotencyPolicies.Required
                    : ToolIdempotencyPolicies.None,
            Toolset = "tests",
            Visibility = "direct"
        };
    }

    private static ToolExecutionRequest Request(
        ToolCatalogEntry tool,
        string callId,
        long sequence,
        string agentId,
        string conflictKey,
        JsonElement? arguments = null)
    {
        return new ToolExecutionRequest(
            agentId,
            new ToolInvocation
            {
                ToolCallId = callId,
                RunId = $"run-{agentId}",
                TurnId = "turn-1",
                AttemptId = "attempt-1",
                ToolName = tool.Name,
                ToolVersion = tool.Version,
                Arguments = arguments ?? Json("{}"),
                Effect = tool.Effect,
                ResolvedConflictKeys = new List<string> { conflictKey },
                Sequence = sequence,
                CreatedAt = DateTimeOffset.UtcNow
            },
            tool);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class OutOfOrderExecutor : IToolCallExecutor
    {
        private readonly ConcurrentQueue<long> _completionOrder = new();
        private int _active;
        private int _maxConcurrency;

        public IReadOnlyList<long> CompletionOrder => _completionOrder.ToArray();

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maxConcurrency, active);
            try
            {
                var delay = request.Sequence switch
                {
                    0 => 180,
                    1 => 100,
                    _ => 20
                };
                await Task.Delay(delay, cancellationToken);
                _completionOrder.Enqueue(request.Sequence);
                return Json($$"""{"sequence":{{request.Sequence}}}""");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class ImmediateExecutor : IToolCallExecutor
    {
        public ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<JsonElement>(Json("{}"));
        }
    }

    private sealed class CountingImmediateExecutor : IToolCallExecutor
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return new ValueTask<JsonElement>(Json("{}"));
        }
    }

    private sealed class AdvancingFirstExecutor : IToolCallExecutor
    {
        private readonly FakeRuntimeClock _clock;
        private readonly TimeSpan _elapsed;
        private int _callCount;

        public AdvancingFirstExecutor(
            FakeRuntimeClock clock,
            TimeSpan elapsed)
        {
            _clock = clock;
            _elapsed = elapsed;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                _clock.Advance(_elapsed);
            }

            return new ValueTask<JsonElement>(Json("{}"));
        }
    }

    private sealed class TrackingTimeoutDelay : IRuntimeDelay
    {
        private readonly bool _throwOnCancellation;
        private int _started;
        private int _cancelled;
        private int _active;
        private int _peakActive;

        public TrackingTimeoutDelay(bool throwOnCancellation = false)
        {
            _throwOnCancellation = throwOnCancellation;
        }

        public int Started => Volatile.Read(ref _started);

        public int Cancelled => Volatile.Read(ref _cancelled);

        public int Active => Volatile.Read(ref _active);

        public int PeakActive => Volatile.Read(ref _peakActive);

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            Interlocked.Increment(ref _started);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _peakActive, active);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = cancellationToken.Register(
                () =>
                {
                    Interlocked.Increment(ref _cancelled);
                    Interlocked.Decrement(ref _active);
                    completion.TrySetCanceled(cancellationToken);
                    if (_throwOnCancellation)
                    {
                        throw new InvalidOperationException(
                            "The timeout wait cancellation callback failed.");
                    }
                });
            return new ValueTask(completion.Task);
        }
    }

    private sealed class BlockingTimeoutDelay : IRuntimeDelay
    {
        public TaskCompletionSource CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = cancellationToken.Register(
                () =>
                {
                    CallbackInvoked.TrySetResult();
                    Release.Task.GetAwaiter().GetResult();
                    completion.TrySetCanceled(cancellationToken);
                });
            return new ValueTask(completion.Task);
        }
    }

    private sealed class ThrowingTimeoutDelay : IRuntimeDelay
    {
        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            _ = cancellationToken;
            throw new InvalidOperationException(
                "The timeout infrastructure failed synchronously.");
        }
    }

    private sealed class FaultedTimeoutDelay : IRuntimeDelay
    {
        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            _ = cancellationToken;
            return new ValueTask(
                Task.FromException(
                    new InvalidOperationException(
                        "The timeout infrastructure faulted.")));
        }
    }

    private sealed class BarrierExecutor : IToolCallExecutor
    {
        public TaskCompletionSource ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WorldEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Tool.Effect == ToolEffects.PureRead)
            {
                ReadEntered.TrySetResult();
                await ReleaseRead.Task.WaitAsync(cancellationToken);
            }
            else
            {
                WorldEntered.TrySetResult();
            }

            return Json("{}");
        }
    }

    private sealed class ConcurrencyExecutor : IToolCallExecutor
    {
        private readonly int _delayMs;
        private int _active;
        private int _maxConcurrency;

        public ConcurrencyExecutor(int delayMs)
        {
            _delayMs = delayMs;
        }

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maxConcurrency, active);
            try
            {
                await Task.Delay(_delayMs, cancellationToken);
                return Json("{}");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class BlockingExecutor : IToolCallExecutor
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Json("{}");
        }
    }

    private sealed class DeepResultExecutor : IToolCallExecutor
    {
        public ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return ValueTask.FromResult(Json("""{"a":{"b":{"c":1}}}"""));
        }
    }

    private sealed class CancellationIgnoringExecutor : IToolCallExecutor
    {
        private int _callCount;

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            await Release.Task;
            Completed.TrySetResult();
            return Json("{}");
        }
    }

    private sealed class SelectiveCancellationIgnoringExecutor : IToolCallExecutor
    {
        private readonly string _blockedToolName;
        private readonly string? _blockedToolVersion;
        private int _blockedCallCount;
        private int _immediateCallCount;

        public SelectiveCancellationIgnoringExecutor(
            string blockedToolName,
            string? blockedToolVersion = null)
        {
            _blockedToolName = blockedToolName;
            _blockedToolVersion = blockedToolVersion;
        }

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BlockedCallCount => Volatile.Read(ref _blockedCallCount);

        public int ImmediateCallCount => Volatile.Read(ref _immediateCallCount);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (string.Equals(
                    request.Tool.Name,
                    _blockedToolName,
                    StringComparison.Ordinal)
                && (_blockedToolVersion is null
                    || string.Equals(
                        request.Tool.Version,
                        _blockedToolVersion,
                        StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref _blockedCallCount);
                await Release.Task;
            }
            else
            {
                Interlocked.Increment(ref _immediateCallCount);
            }

            return Json("{}");
        }
    }

    private sealed class EnteredCancellationIgnoringExecutor : IToolCallExecutor
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Entered.TrySetResult();
            await Release.Task;
            return Json("{}");
        }
    }

    private sealed class LateFaultExecutor : IToolCallExecutor
    {
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return new ValueTask<JsonElement>(Completion.Task);
        }
    }

    private sealed class ArmedThrowingClock : IRuntimeClock
    {
        private int _armed;

        public DateTimeOffset UtcNow
        {
            get
            {
                if (Volatile.Read(ref _armed) != 0)
                {
                    throw new InvalidOperationException(
                        "The injected clock failed after host dispatch.");
                }

                return new DateTimeOffset(
                    2026,
                    7,
                    28,
                    9,
                    0,
                    0,
                    TimeSpan.Zero);
            }
        }

        public void Arm()
        {
            Volatile.Write(ref _armed, 1);
        }
    }

    private sealed class ClockArmingExecutor : IToolCallExecutor
    {
        private readonly ArmedThrowingClock _clock;
        private int _callCount;

        public ClockArmingExecutor(ArmedThrowingClock clock)
        {
            _clock = clock;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            _clock.Arm();
            return ValueTask.FromResult(Json("{}"));
        }
    }

    private sealed class CallerSourceDisposingExecutor : IToolCallExecutor
    {
        private readonly CancellationTokenSource _callerSource;
        private int _callCount;

        public CallerSourceDisposingExecutor(
            CancellationTokenSource callerSource)
        {
            _callerSource = callerSource;
        }

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            _callerSource.Dispose();
            await Release.Task;
            return Json("{}");
        }
    }

    private sealed class ThrowingCancellationExecutor : IToolCallExecutor
    {
        private int _callCount;

        public TaskCompletionSource CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            Interlocked.Increment(ref _callCount);
            using var registration = cancellationToken.Register(
                () =>
                {
                    CallbackInvoked.TrySetResult();
                    throw new InvalidOperationException(
                        "tool cancellation callback failed");
                });
            await Release.Task;
            return Json("{}");
        }
    }

    private sealed class BlockingCancellationCallbackExecutor :
        IToolCallExecutor
    {
        private int _callCount;

        public TaskCompletionSource CallbackInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<JsonElement> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            Interlocked.Increment(ref _callCount);
            using var registration = cancellationToken.Register(
                () =>
                {
                    CallbackInvoked.TrySetResult();
                    Release.Task.GetAwaiter().GetResult();
                });
            await Release.Task;
            return Json("{}");
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}

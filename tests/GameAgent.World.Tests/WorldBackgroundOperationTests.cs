using System.Diagnostics;

namespace GameAgent.World.Tests;

public sealed class WorldBackgroundOperationTests
{
    [Fact]
    public async Task ControlledShutdownReturnsCooperativeCancellation()
    {
        var queue = new WorldBackgroundOperationQueue(capacity: 1);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(
            queue.TrySchedule(
                "cooperative",
                WorldBackgroundOperationKind.InteractionPlanning,
                async token =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token)
                        .ConfigureAwait(false);
                    return new object();
                },
                out var rejection),
            rejection);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var results = await queue.ShutdownAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        var result = Assert.Single(results);
        Assert.Equal("cooperative", result.OperationId);
        Assert.True(result.IsCanceled);
        Assert.False(result.Succeeded);
        Assert.Equal(0, queue.OutstandingCount);
        Assert.Equal(0, queue.CompletedCount);
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task ControlledShutdownPreservesLateAuthoritativeResult()
    {
        var queue = new WorldBackgroundOperationQueue(capacity: 1);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var authoritative = new WorldPlanExecutionResult("committed");
        Assert.True(
            queue.TrySchedule(
                "late-authority",
                WorldBackgroundOperationKind.PlanExecution,
                async token =>
                {
                    _ = token;
                    started.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    return authoritative;
                },
                out var rejection),
            rejection);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var shutdown = queue.ShutdownAsync().AsTask();
        await Task.Delay(50);
        Assert.False(shutdown.IsCompleted);
        release.TrySetResult();
        var results = await shutdown.WaitAsync(TimeSpan.FromSeconds(2));

        var result = Assert.Single(results);
        Assert.True(result.Succeeded);
        Assert.False(result.IsCanceled);
        Assert.Same(authoritative, result.Value);
        Assert.Equal(0, queue.OutstandingCount);
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task ControlledShutdownTimeoutReportsAuthorityForReconciliation()
    {
        var queue = new WorldBackgroundOperationQueue(capacity: 1);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(
            queue.TrySchedule(
                "unsettled-authority",
                WorldBackgroundOperationKind.PlanExecution,
                async token =>
                {
                    _ = token;
                    started.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    return new WorldPlanExecutionResult("committed");
                },
                out var rejection),
            rejection);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        var incomplete =
            await Assert.ThrowsAsync<
                WorldBackgroundShutdownIncompleteException>(
                async () => await queue.ShutdownAsync(timeout.Token));

        Assert.Contains(
            "unsettled-authority",
            incomplete.OutstandingOperationIds);
        Assert.Contains(
            "unsettled-authority",
            incomplete.AuthoritativeOperationIds);
        Assert.False(
            queue.TrySchedule(
                "too-late",
                WorldBackgroundOperationKind.TriggerPlanning,
                _ => new ValueTask<object?>(new object()),
                out var stoppedReason));
        Assert.Equal(
            WorldBackgroundOperationReasonCodes.QueueStopped,
            stoppedReason);

        release.TrySetResult();
        var results = await queue.ShutdownAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(results);
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task DisposeDetachesAnOperationThatIgnoresCancellation()
    {
        var queue = new WorldBackgroundOperationQueue(capacity: 2);
        var never = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(
            queue.TrySchedule(
                "uncooperative",
                WorldBackgroundOperationKind.PlanExecution,
                _ => new ValueTask<object?>(never.Task),
                out var rejection),
            rejection);

        var first = queue.DisposeAsync().AsTask();
        var second = queue.DisposeAsync().AsTask();

        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, queue.OutstandingCount);

        never.SetResult(new object());
    }

    [Fact]
    public async Task CancelNeverRunsHostCallbacksOnTheCallingThread()
    {
        var queue = new WorldBackgroundOperationQueue(capacity: 2);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new ManualResetEventSlim();
        Assert.True(
            queue.TrySchedule(
                "blocking-callback",
                WorldBackgroundOperationKind.PlanExecution,
                token =>
                {
                    token.Register(
                        () =>
                        {
                            callbackEntered.TrySetResult();
                            releaseCallback.Wait();
                        });
                    started.TrySetResult();
                    return new ValueTask<object?>(
                        Task.Delay(Timeout.Infinite, token)
                            .ContinueWith<object?>(
                                _ => new object(),
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default));
                },
                out var rejection),
            rejection);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();
        Assert.True(queue.TryCancel("blocking-callback"));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseCallback.Set();
        await queue.DisposeAsync();
        releaseCallback.Dispose();
    }

    [Fact]
    public async Task ExternalCancellationSurvivesAFullCallbackLane()
    {
        const int workerCount =
            WorldBackgroundOperationQueue
                .CancellationCallbackConcurrencyLimit;
        var queue = new WorldBackgroundOperationQueue(
            capacity: workerCount + 1);
        var started = Enumerable.Range(0, workerCount + 1)
            .Select(
                _ => new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var completions = Enumerable.Range(0, workerCount + 1)
            .Select(
                _ => new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var callbackGates = Enumerable.Range(0, workerCount + 1)
            .Select(_ => new ManualResetEventSlim())
            .ToArray();
        var saturated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finalCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        using var external = new CancellationTokenSource();
        try
        {
            for (var index = 0; index < workerCount + 1; index++)
            {
                var captured = index;
                Assert.True(
                    queue.TrySchedule(
                        "operation-" + captured,
                        WorldBackgroundOperationKind.PlanExecution,
                        token =>
                        {
                            token.Register(
                                () =>
                                {
                                    if (captured == workerCount)
                                    {
                                        finalCallback.TrySetResult();
                                    }
                                    else if (Interlocked.Increment(
                                                 ref entered)
                                             == workerCount)
                                    {
                                        saturated.TrySetResult();
                                    }

                                    callbackGates[captured].Wait();
                                });
                            started[captured].TrySetResult();
                            return new ValueTask<object?>(
                                completions[captured].Task);
                        },
                        out var rejection,
                        captured == workerCount
                            ? external.Token
                            : default),
                    rejection);
            }

            await Task.WhenAll(started.Select(item => item.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < workerCount; index++)
            {
                Assert.True(queue.TryCancel("operation-" + index));
            }

            await saturated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopwatch = Stopwatch.StartNew();
            external.Cancel();
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            await Task.Delay(100);
            Assert.False(finalCallback.Task.IsCompleted);

            callbackGates[0].Set();
            await finalCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (var gate in callbackGates)
            {
                gate.Set();
            }

            foreach (var completion in completions)
            {
                completion.TrySetResult(new object());
            }

            await queue.DisposeAsync();
            foreach (var gate in callbackGates)
            {
                gate.Dispose();
            }
        }
    }

    [Fact]
    public async Task MassDisposeKeepsCancellationCallbacksFixedBounded()
    {
        const int queueCount = 4;
        const int operationsPerQueue =
            WorldBackgroundOperationQueue
                .BackgroundWorkConcurrencyLimit / queueCount;
        const int operationCount = operationsPerQueue * queueCount;
        const int workerCount =
            WorldBackgroundOperationQueue
                .CancellationCallbackConcurrencyLimit;
        var queues = Enumerable.Range(0, queueCount)
            .Select(
                _ => new WorldBackgroundOperationQueue(
                    capacity: operationsPerQueue))
            .ToArray();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allCallbacksEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = Enumerable.Range(0, operationCount)
            .Select(
                _ => new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        using var callbackGate = new ManualResetEventSlim();
        var startedCount = 0;
        var enteredCount = 0;
        var activeCallbacks = 0;
        var maximumActive = 0;
        try
        {
            for (var index = 0; index < operationCount; index++)
            {
                var captured = index;
                var queue = queues[index / operationsPerQueue];
                Assert.True(
                    queue.TrySchedule(
                        "mass-" + captured,
                        WorldBackgroundOperationKind.PlanExecution,
                        token =>
                        {
                            token.Register(
                                () =>
                                {
                                    var active = Interlocked.Increment(
                                        ref activeCallbacks);
                                    UpdateMaximum(
                                        ref maximumActive,
                                        active);
                                    if (Interlocked.Increment(
                                            ref enteredCount)
                                        == operationCount)
                                    {
                                        allCallbacksEntered.TrySetResult();
                                    }

                                    callbackGate.Wait();
                                    Interlocked.Decrement(
                                        ref activeCallbacks);
                                });
                            if (Interlocked.Increment(ref startedCount)
                                == operationCount)
                            {
                                started.TrySetResult();
                            }

                            return new ValueTask<object?>(
                                completions[captured].Task);
                        },
                        out var rejection),
                    rejection);
            }

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(
                    queues.Select(queue => queue.DisposeAsync().AsTask()))
                .WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => Volatile.Read(ref activeCallbacks) == workerCount,
                TimeSpan.FromSeconds(5));
            Assert.InRange(
                Volatile.Read(ref maximumActive),
                1,
                workerCount);

            callbackGate.Set();
            await allCallbacksEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.InRange(
                Volatile.Read(ref maximumActive),
                1,
                workerCount);
        }
        finally
        {
            callbackGate.Set();
            foreach (var completion in completions)
            {
                completion.TrySetResult(new object());
            }

            foreach (var queue in queues)
            {
                await queue.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ScheduledWorkNeverExceedsTheGlobalExecutionBound()
    {
        const int workerCount =
            WorldBackgroundOperationQueue
                .BackgroundWorkConcurrencyLimit;
        var queue = new WorldBackgroundOperationQueue(
            capacity: workerCount + 1);
        var saturated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var active = 0;
        var maximumActive = 0;
        try
        {
            for (var index = 0; index < workerCount + 1; index++)
            {
                var captured = index;
                Assert.True(
                    queue.TrySchedule(
                        "bounded-work-" + captured,
                        WorldBackgroundOperationKind.PlanExecution,
                        async _ =>
                        {
                            var current = Interlocked.Increment(ref active);
                            UpdateMaximum(ref maximumActive, current);
                            if (captured == workerCount)
                            {
                                finalStarted.TrySetResult();
                            }
                            else if (Interlocked.Increment(ref started)
                                     == workerCount)
                            {
                                saturated.TrySetResult();
                            }

                            await release.Task.ConfigureAwait(false);
                            Interlocked.Decrement(ref active);
                            return new object();
                        },
                        out var rejection),
                    rejection);
            }

            await saturated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.False(finalStarted.Task.IsCompleted);
            Assert.Equal(
                workerCount,
                Volatile.Read(ref maximumActive));

            release.TrySetResult();
            await finalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => queue.CompletedCount == workerCount + 1,
                TimeSpan.FromSeconds(5));
            Assert.Equal(
                workerCount,
                Volatile.Read(ref maximumActive));
            Assert.Equal(
                workerCount + 1,
                queue.Drain(workerCount + 1, _ => { }));
        }
        finally
        {
            release.TrySetResult();
            await queue.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompletionAndCancellationHaveOneLinearOutcome()
    {
        var queue = new WorldBackgroundOperationQueue(capacity: 1);
        try
        {
            for (var iteration = 0; iteration < 256; iteration++)
            {
                var operationId = "race-" + iteration;
                var started = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var callback = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(
                    queue.TrySchedule(
                        operationId,
                        WorldBackgroundOperationKind.TriggerPlanning,
                        async token =>
                        {
                            token.Register(
                                () => callback.TrySetResult());
                            started.TrySetResult();
                            await release.Task.ConfigureAwait(false);
                            return new object();
                        },
                        out var rejection),
                    rejection);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

                using var raceGate = new ManualResetEventSlim();
                var cancelReady = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var cancellation = Task.Run(
                    () =>
                    {
                        cancelReady.TrySetResult();
                        raceGate.Wait();
                        return queue.TryCancel(operationId);
                    });
                await cancelReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if ((iteration & 1) == 0)
                {
                    raceGate.Set();
                    release.TrySetResult();
                }
                else
                {
                    release.TrySetResult();
                    raceGate.Set();
                }

                var cancellationWon = await cancellation.WaitAsync(
                    TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => queue.CompletedCount == 1,
                    TimeSpan.FromSeconds(2));
                WorldBackgroundOperationResult? result = null;
                Assert.Equal(
                    1,
                    queue.Drain(1, value => result = value));
                Assert.NotNull(result);
                if (cancellationWon)
                {
                    Assert.True(result!.IsCanceled);
                    Assert.False(result.Succeeded);
                    await callback.Task.WaitAsync(
                        TimeSpan.FromSeconds(2));
                }
                else
                {
                    Assert.True(result!.Succeeded);
                    Assert.False(result.IsCanceled);
                    Assert.False(callback.Task.IsCompleted);
                }
            }
        }
        finally
        {
            await queue.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlanExecutionKeepsReturnedAuthorityAfterCancellation()
    {
        var queue = new WorldBackgroundOperationQueue(capacity: 1);
        try
        {
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var authoritative = new WorldPlanExecutionResult("committed");
            Assert.True(
                queue.TrySchedule(
                    "authoritative-race",
                    WorldBackgroundOperationKind.PlanExecution,
                    async token =>
                    {
                        token.Register(
                            () => cancellationObserved.TrySetResult());
                        started.TrySetResult();
                        await release.Task.ConfigureAwait(false);
                        return authoritative;
                    },
                    out var rejection),
                rejection);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(queue.TryCancel("authoritative-race"));
            await cancellationObserved.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            release.TrySetResult();
            await WaitUntilAsync(
                () => queue.CompletedCount == 1,
                TimeSpan.FromSeconds(2));
            WorldBackgroundOperationResult? result = null;
            Assert.Equal(1, queue.Drain(1, item => result = item));

            Assert.NotNull(result);
            Assert.True(result!.Succeeded);
            Assert.False(result.IsCanceled);
            Assert.Same(authoritative, result.Value);
            Assert.Null(result.Exception);
        }
        finally
        {
            await queue.DisposeAsync();
        }
    }

    [Fact]
    public async Task GlobalWorkCapacityRejectsWithoutGhostExecution()
    {
        const int limit =
            WorldBackgroundOperationQueue
                .BackgroundWorkOutstandingLimit;
        var queue = new WorldBackgroundOperationQueue(
            capacity: limit + 1);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CapacityProbe? rejectedProbe = null;
        var accepted = 0;
        string? capacityReason = null;
        try
        {
            for (var index = 0; index < limit + 1; index++)
            {
                var probe = new CapacityProbe();
                if (!queue.TrySchedule(
                        "capacity-" + index,
                        WorldBackgroundOperationKind.PlanExecution,
                        async _ =>
                        {
                            Interlocked.Increment(ref probe.Ran);
                            await release.Task.ConfigureAwait(false);
                            return new object();
                        },
                        out capacityReason))
                {
                    rejectedProbe = probe;
                    break;
                }

                accepted++;
            }

            Assert.NotNull(rejectedProbe);
            Assert.InRange(accepted, 1, limit);
            Assert.Equal(
                WorldBackgroundOperationReasonCodes.QueueAtCapacity,
                capacityReason);
            Assert.Equal(0, Volatile.Read(ref rejectedProbe!.Ran));

            release.TrySetResult();
            await WaitUntilAsync(
                () => queue.CompletedCount == accepted,
                TimeSpan.FromSeconds(10));
            Assert.Equal(0, Volatile.Read(ref rejectedProbe.Ran));
            Assert.Equal(
                accepted,
                queue.Drain(accepted, _ => { }));
            await Task.Delay(50);
            Assert.Equal(0, Volatile.Read(ref rejectedProbe.Ran));
        }
        finally
        {
            release.TrySetResult();
            await queue.DisposeAsync();
        }
    }

    private sealed class CapacityProbe
    {
        public int Ran;
    }

    private static void UpdateMaximum(
        ref int maximum,
        int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current
                || Interlocked.CompareExchange(
                    ref maximum,
                    candidate,
                    current) == current)
            {
                return;
            }
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException(
                    "The expected callback state was not reached.");
            }

            await Task.Delay(10);
        }
    }
}

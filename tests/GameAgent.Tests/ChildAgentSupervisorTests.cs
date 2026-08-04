using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class ChildAgentSupervisorTests
{
    [Fact]
    public async Task ChildRunCarriesDurableParentRootAndDepthLineage()
    {
        var runtime = new DelegateRuntime(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                request.Run.State = RunStates.Completed;
                return new ValueTask<DurableRunOutcome>(
                    new DurableRunOutcome { Run = request.Run });
            });
        await using var supervisor = new ChildAgentSupervisor(runtime);

        var result = await supervisor.RunChildAsync(
            "parent-run",
            Request("child-run"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("parent-run", result.Lineage.RootRunId);
        Assert.Equal("parent-run", result.Lineage.ParentRunId);
        Assert.Equal(1, result.Lineage.Depth);
        var durable = ChildAgentLineage.Read(result.Outcome.Run);
        Assert.NotNull(durable);
        Assert.Equal(result.Lineage.ChildRunId, durable!.ChildRunId);
        Assert.Equal(result.Lineage.Depth, durable.Depth);
    }

    [Fact]
    public async Task NestedChildrenAreBoundedBySupervisedDepth()
    {
        var firstEntered = NewSignal();
        var secondEntered = NewSignal();
        var release = NewSignal();
        var runtime = new DelegateRuntime(
            async (request, cancellationToken) =>
            {
                if (request.Run.RunId == "child-1")
                {
                    firstEntered.TrySetResult(true);
                }
                else if (request.Run.RunId == "child-2")
                {
                    secondEntered.TrySetResult(true);
                }

                await release.Task.WaitAsync(cancellationToken);
                request.Run.State = RunStates.Completed;
                return new DurableRunOutcome { Run = request.Run };
            });
        await using var supervisor = new ChildAgentSupervisor(
            runtime,
            new ChildAgentSupervisorOptions
            {
                MaxDepth = 2,
                MaxConcurrentChildren = 3,
                MaxActiveChildrenPerParent = 2
            });

        var first = supervisor.RunChildAsync(
                "root-run",
                Request("child-1"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        var second = supervisor.RunChildAsync(
                "child-1",
                Request("child-2"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.RunChildAsync(
                    "child-2",
                    Request("child-3"), cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        release.TrySetResult(true);
        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, results[0].Lineage.Depth);
        Assert.Equal(2, results[1].Lineage.Depth);
    }

    [Fact]
    public async Task CompletedParentCarriesLineageIntoLaterDelegation()
    {
        var runtime = new DelegateRuntime(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                request.Run.State = RunStates.Completed;
                return new ValueTask<DurableRunOutcome>(
                    new DurableRunOutcome { Run = request.Run });
            });
        await using var supervisor = new ChildAgentSupervisor(
            runtime,
            new ChildAgentSupervisorOptions { MaxDepth = 2 });

        var first = await supervisor.RunChildAsync(
            "root-run",
            Request("completed-parent"), cancellationToken: TestContext.Current.CancellationToken);
        var second = await supervisor.RunChildAsync(
            "completed-parent",
            Request("later-child"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("root-run", second.Lineage.RootRunId);
        Assert.Equal("completed-parent", second.Lineage.ParentRunId);
        Assert.Equal(2, second.Lineage.Depth);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.RunChildAsync(
                    second.Outcome.Run,
                    Request("too-deep"), cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
    }

    [Fact]
    public async Task DuplicateChildAdmissionDoesNotDetachOriginalChild()
    {
        var entered = NewSignal();
        var runtime = new DelegateRuntime(
            async (request, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
        await using var supervisor = new ChildAgentSupervisor(runtime);
        var original = supervisor.RunChildAsync(
                "original-parent",
                Request("duplicate-child"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.RunChildAsync(
                    "other-parent",
                    Request("duplicate-child"), cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(1, supervisor.ActiveChildCount);
        Assert.Equal(1, supervisor.CancelChildren("original-parent"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => original);
    }

    [Fact]
    public async Task CompletedChildIdentityIsRejectedBeforeSecondDispatch()
    {
        var calls = 0;
        var runtime = new DelegateRuntime(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref calls);
                request.Run.State = RunStates.Completed;
                return new ValueTask<DurableRunOutcome>(
                    new DurableRunOutcome { Run = request.Run });
            });
        await using var supervisor = new ChildAgentSupervisor(runtime);

        _ = await supervisor.RunChildAsync(
            "first-parent",
            Request("completed-child"), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.RunChildAsync(
                    "second-parent",
                    Request("completed-child"), cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task BatchPreservesOrderAndIsolatesChildFailure()
    {
        var runtime = new DelegateRuntime(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Run.RunId == "bad")
                {
                    throw new InvalidOperationException("expected");
                }

                if (request.Run.RunId == "failed-outcome")
                {
                    request.Run.State = RunStates.Failed;
                    return new ValueTask<DurableRunOutcome>(
                        new DurableRunOutcome
                        {
                            Run = request.Run,
                            ErrorCode = "expected_failure"
                        });
                }

                request.Run.State = RunStates.Completed;
                return new ValueTask<DurableRunOutcome>(
                    new DurableRunOutcome { Run = request.Run });
            });
        await using var supervisor = new ChildAgentSupervisor(runtime);

        var batch = await supervisor.RunManyAsync(
            "parent-run",
            new[]
            {
                Request("first"),
                Request("bad"),
                Request("failed-outcome"),
                Request("last")
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(batch.AllSucceeded);
        Assert.Equal(
            new[] { "first", "bad", "failed-outcome", "last" },
            batch.Items.Select(item => item.ChildRunId));
        Assert.True(batch.Items[0].Succeeded);
        Assert.False(batch.Items[1].Succeeded);
        Assert.Equal(
            typeof(InvalidOperationException).FullName,
            batch.Items[1].ErrorType);
        Assert.True(batch.Items[2].HasOutcome);
        Assert.False(batch.Items[2].Succeeded);
        Assert.Equal(RunStates.Failed, batch.Items[2].RunState);
        Assert.True(batch.Items[3].Succeeded);
    }

    [Fact]
    public async Task ExplicitParentCancellationCancelsItsActiveChildren()
    {
        var entered = NewSignal();
        var runtime = new DelegateRuntime(
            async (request, cancellationToken) =>
            {
                _ = request;
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
        await using var supervisor = new ChildAgentSupervisor(runtime);
        var child = supervisor.RunChildAsync(
                "parent-run",
                Request("cancelled-child"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, supervisor.CancelChildren("parent-run"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => child);
        Assert.Equal(0, supervisor.ActiveChildCount);
    }

    [Fact]
    public async Task RepeatedParentCancellationIsAtomicallyCoalescedPerChild()
    {
        var entered = NewSignal();
        var cancellationObserved = NewSignal();
        var release = NewSignal();
        var runtime = new DelegateRuntime(
            async (request, cancellationToken) =>
            {
                _ = request;
                entered.TrySetResult(true);
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult(true);
                    await release.Task;
                    throw;
                }
            });
        var supervisor = new ChildAgentSupervisor(runtime);
        var child = supervisor.RunChildAsync(
                "repeated-cancel-parent",
                Request("repeated-cancel-child"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        var accepted = 0;
        try
        {
            Parallel.For(
                0,
                10_000,
                _ => Interlocked.Add(
                    ref accepted,
                    supervisor.CancelChildren("repeated-cancel-parent")));
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, accepted);
            Assert.Equal(1, supervisor.ActiveChildCount);
        }
        finally
        {
            release.TrySetResult(true);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child);
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task QueuedChildSnapshotsMutableRequestBeforeAdmission()
    {
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var runtime = new DelegateRuntime(
            async (request, cancellationToken) =>
            {
                if (request.Run.RunId == "first-child")
                {
                    firstEntered.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                request.Run.State = RunStates.Completed;
                return new DurableRunOutcome { Run = request.Run };
            });
        await using var supervisor = new ChildAgentSupervisor(
            runtime,
            new ChildAgentSupervisorOptions
            {
                MaxConcurrentChildren = 1
            });
        var first = supervisor.RunChildAsync(
                "parent-run",
                Request("first-child"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        var queuedRequest = Request("queued-child");
        var queued = supervisor.RunChildAsync(
                "parent-run",
                queuedRequest, cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();

        queuedRequest.Run.RunId = "mutated-child";
        queuedRequest.Run.AgentId = "mutated-agent";
        releaseFirst.TrySetResult(true);
        await first;
        var result = await queued;

        Assert.Equal("queued-child", result.Lineage.ChildRunId);
        Assert.Equal("queued-child", result.Outcome.Run.RunId);
        Assert.Equal("agent-queued-child", result.Outcome.Run.AgentId);
    }

    [Fact]
    public async Task PreDispatchFailureDoesNotPoisonChildIdentityOrLineage()
    {
        var fail = true;
        var runtime = new DelegateRuntime(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fail && request.Run.RunId == "retry-child")
                {
                    fail = false;
                    throw new ArgumentException("pre-dispatch");
                }

                request.Run.State = RunStates.Completed;
                return new ValueTask<DurableRunOutcome>(
                    new DurableRunOutcome { Run = request.Run });
            });
        await using var supervisor = new ChildAgentSupervisor(runtime);

        await Assert.ThrowsAsync<ArgumentException>(
            () => supervisor.RunChildAsync(
                    "root-run",
                    Request("retry-child"), cancellationToken: TestContext.Current.CancellationToken)
                .AsTask());
        var unrelated = await supervisor.RunChildAsync(
            "retry-child",
            Request("not-a-grandchild"), cancellationToken: TestContext.Current.CancellationToken);
        var retried = await supervisor.RunChildAsync(
            "root-run",
            Request("retry-child"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, unrelated.Lineage.Depth);
        Assert.Equal("retry-child", unrelated.Lineage.RootRunId);
        Assert.Equal(1, retried.Lineage.Depth);
        Assert.Equal("root-run", retried.Lineage.RootRunId);
    }

    [Fact]
    public async Task ShutdownAndParentCancellationDoNotRunCallbacksInline()
    {
        var entered = NewSignal();
        var callbackEntered = NewSignal();
        using var callbackRelease = new ManualResetEventSlim(false);
        var runtime = new DelegateRuntime(
            async (request, cancellationToken) =>
            {
                _ = request;
                var cancellation = Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                using var registration = cancellationToken.Register(
                    () =>
                    {
                        callbackEntered.TrySetResult(true);
                        callbackRelease.Wait();
                    });
                entered.TrySetResult(true);
                await cancellation;
                throw new InvalidOperationException("unreachable");
            });
        var supervisor = new ChildAgentSupervisor(
            runtime,
            new ChildAgentSupervisorOptions
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(25)
            });
        var child = supervisor.RunChildAsync(
                "blocking-callback-parent",
                Request("blocking-callback-child"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(
                1,
                supervisor.CancelChildren("blocking-callback-parent"));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(await supervisor.StopAsync());
        }
        finally
        {
            callbackRelease.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child);
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task CallerCancellationDoesNotRunChildCallbacksInline()
    {
        var entered = NewSignal();
        using var callbackRelease = new ManualResetEventSlim(false);
        using var callerCancellation = new CancellationTokenSource();
        var runtime = new DelegateRuntime(
            async (request, cancellationToken) =>
            {
                _ = request;
                using var registration = cancellationToken.Register(
                    () => callbackRelease.Wait());
                entered.TrySetResult(true);
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
        var supervisor = new ChildAgentSupervisor(runtime);
        var child = supervisor.RunChildAsync(
                "caller-cancel-parent",
                Request("caller-cancel-child"),
                callerCancellation.Token)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        callerCancellation.Cancel();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.False(child.IsCompleted);
        callbackRelease.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child);
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task DisposeWaitsForNonCooperativeChildAfterBoundedStopProbe()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var runtime = new DelegateRuntime(
            async (request, cancellationToken) =>
            {
                _ = cancellationToken;
                entered.TrySetResult(true);
                await release.Task;
                request.Run.State = RunStates.Completed;
                return new DurableRunOutcome { Run = request.Run };
            });
        var supervisor = new ChildAgentSupervisor(
            runtime,
            new ChildAgentSupervisorOptions
            {
                ShutdownTimeout = TimeSpan.FromMilliseconds(20)
            });
        var child = supervisor.RunChildAsync(
                "dispose-parent",
                Request("dispose-child"), cancellationToken: TestContext.Current.CancellationToken)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        var disposing = supervisor.DisposeAsync().AsTask();
        await Task.Delay(50, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(disposing.IsCompleted);

        release.TrySetResult(true);
        await child.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
        await disposing.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
    }

    private static DurableRunRequest Request(string runId)
    {
        var now = DateTimeOffset.UtcNow;
        return new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = runId,
                AgentId = "agent-" + runId,
                WorldId = "world",
                State = RunStates.Queued,
                CreatedAt = now,
                UpdatedAt = now
            }
        };
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegateRuntime : IDurableAgentRuntime
    {
        private readonly Func<
            DurableRunRequest,
            CancellationToken,
            ValueTask<DurableRunOutcome>> _run;

        public DelegateRuntime(
            Func<
                DurableRunRequest,
                CancellationToken,
                ValueTask<DurableRunOutcome>> run)
        {
            _run = run;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default) =>
            _run(request, cancellationToken);

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}

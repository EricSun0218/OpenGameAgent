using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RuntimePrimitiveTests
{
    [Fact]
    public void DefaultOwnershipLimitsAreFiniteAndVisibleWithoutIdentifiers()
    {
        var diagnostics = new RunOwnershipRegistry().GetDiagnostics();

        Assert.Equal(
            RunOwnershipLimits.DefaultMaxActiveRuns,
            diagnostics.Limits.MaxActiveRuns);
        Assert.Equal(
            RunOwnershipLimits.DefaultMaxLanes,
            diagnostics.Limits.MaxLanes);
        Assert.Equal(
            RunOwnershipLimits.DefaultMaxWaitersPerLane,
            diagnostics.Limits.MaxWaitersPerLane);
        Assert.Equal(0, diagnostics.ActiveRunCount);
        Assert.Equal(0, diagnostics.WaitingRunCount);
        Assert.Equal(0, diagnostics.LaneCount);
    }

    [Fact]
    public async Task DuplicateRunCannotAcquireExecutorOwnership()
    {
        var ownership = new RunOwnershipRegistry();
        await using var first = await ownership.AcquireAsync(
            "run-1",
            "agent-1",
            CancellationToken.None);

        await Assert.ThrowsAsync<DuplicateRunException>(
            () => ownership
                .AcquireAsync("run-1", "agent-1", CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public async Task DuplicateRunTakesPriorityOverWorkloadCapacity()
    {
        var ownership = new RunOwnershipRegistry(
            new RunOwnershipLimits(
                maxActiveRuns: 1,
                maxLanes: 1,
                maxWaitersPerLane: 1));
        await using var first = await ownership.AcquireAsync(
            "run-duplicate-priority",
            "lane-1",
            CancellationToken.None);

        var duplicate = await Assert.ThrowsAsync<DuplicateRunException>(
            () => ownership
                .AcquireAsync(
                    "run-duplicate-priority",
                    "lane-2",
                    CancellationToken.None)
                .AsTask());

        Assert.Equal(
            DuplicateRunException.StableReasonCode,
            duplicate.ReasonCode);
    }

    [Fact]
    public async Task ActiveRunCapacityFailsFastAndRecoversAfterRelease()
    {
        var ownership = new RunOwnershipRegistry(
            new RunOwnershipLimits(
                maxActiveRuns: 1,
                maxLanes: 2,
                maxWaitersPerLane: 1));
        var first = await ownership.AcquireAsync(
            "run-active-first",
            "lane-active-first",
            CancellationToken.None);

        var capacity = await Assert.ThrowsAsync<
            RunWorkloadCapacityExceededException>(
            () => ownership
                .AcquireAsync(
                    "run-active-second",
                    "lane-active-second",
                    CancellationToken.None)
                .AsTask());

        Assert.Equal(
            RunWorkloadCapacityReasonCodes.MaxActiveRuns,
            capacity.ReasonCode);
        Assert.Equal(1, capacity.Limit);
        Assert.Equal(1, ownership.ActiveRunCount);

        await first.DisposeAsync();
        await using var admitted = await ownership.AcquireAsync(
            "run-active-second",
            "lane-active-second",
            CancellationToken.None);
        Assert.Equal(1, ownership.ActiveRunCount);
    }

    [Fact]
    public async Task LaneCapacityDoesNotBlockAnExistingLane()
    {
        var ownership = new RunOwnershipRegistry(
            new RunOwnershipLimits(
                maxActiveRuns: 3,
                maxLanes: 1,
                maxWaitersPerLane: 1));
        var holder = await ownership.AcquireAsync(
            "run-lane-holder",
            "lane-shared",
            CancellationToken.None);
        using var waitingCancellation = new CancellationTokenSource();
        var waiting = ownership
            .AcquireAsync(
                "run-lane-waiting",
                "lane-shared",
                waitingCancellation.Token)
            .AsTask();

        var capacity = await Assert.ThrowsAsync<
            RunWorkloadCapacityExceededException>(
            () => ownership
                .AcquireAsync(
                    "run-other-lane",
                    "lane-other",
                    CancellationToken.None)
                .AsTask());

        Assert.Equal(
            RunWorkloadCapacityReasonCodes.MaxLanes,
            capacity.ReasonCode);
        Assert.Equal(1, ownership.LaneCount);
        Assert.Equal(1, ownership.WaitingRunCount);

        waitingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiting);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task PerLaneWaiterCapacityFailsFast()
    {
        var ownership = new RunOwnershipRegistry(
            new RunOwnershipLimits(
                maxActiveRuns: 4,
                maxLanes: 1,
                maxWaitersPerLane: 1));
        var holder = await ownership.AcquireAsync(
            "run-waiter-holder",
            "lane-waiter",
            CancellationToken.None);
        using var waitingCancellation = new CancellationTokenSource();
        var waiting = ownership
            .AcquireAsync(
                "run-waiter-first",
                "lane-waiter",
                waitingCancellation.Token)
            .AsTask();

        var capacity = await Assert.ThrowsAsync<
            RunWorkloadCapacityExceededException>(
            () => ownership
                .AcquireAsync(
                    "run-waiter-overflow",
                    "lane-waiter",
                    CancellationToken.None)
                .AsTask());

        Assert.Equal(
            RunWorkloadCapacityReasonCodes.MaxWaitersPerLane,
            capacity.ReasonCode);
        Assert.Equal(1, capacity.Limit);
        var diagnostics = ownership.GetDiagnostics();
        Assert.Equal(2, diagnostics.ActiveRunCount);
        Assert.Equal(1, diagnostics.WaitingRunCount);
        Assert.Equal(1, diagnostics.LaneCount);

        waitingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiting);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task IdleLaneEntriesAreReclaimedAfterLeaseRelease()
    {
        var ownership = new RunOwnershipRegistry();

        for (var index = 0; index < 256; index++)
        {
            await using var lease = await ownership.AcquireAsync(
                $"run-{index}",
                $"agent-{index}",
                CancellationToken.None);
        }

        Assert.Equal(0, ownership.LaneCount);
    }

    [Fact]
    public async Task CancelledLaneWaitReleasesItsLaneReference()
    {
        var ownership = new RunOwnershipRegistry();
        var first = await ownership.AcquireAsync(
            "run-first",
            "agent-shared",
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = ownership
            .AcquireAsync(
                "run-waiting",
                "agent-shared",
                cancellation.Token)
            .AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, ownership.LaneCount);
        Assert.Equal(1, ownership.ActiveRunCount);
        Assert.Equal(0, ownership.WaitingRunCount);

        await first.DisposeAsync();
        Assert.Equal(0, ownership.LaneCount);
        Assert.Equal(0, ownership.ActiveRunCount);
    }

    [Fact]
    public async Task LaneRemainsExclusiveWhileEntriesAreReclaimed()
    {
        var ownership = new RunOwnershipRegistry();
        var first = await ownership.AcquireAsync(
            "run-first",
            "agent-shared",
            CancellationToken.None);
        var secondAcquired = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(
            async () =>
            {
                await using var lease = await ownership.AcquireAsync(
                    "run-second",
                    "agent-shared",
                    CancellationToken.None);
                secondAcquired.TrySetResult(true);
            });

        await Task.Yield();
        Assert.False(secondAcquired.Task.IsCompleted);

        await first.DisposeAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondAcquired.Task.IsCompletedSuccessfully);
        Assert.Equal(0, ownership.LaneCount);
    }

    [Fact]
    public async Task ConcurrentLeaseDisposalIsIdempotentAndRestoresEveryCounter()
    {
        var limits = new RunOwnershipLimits(
            maxActiveRuns: 1,
            maxLanes: 1,
            maxWaitersPerLane: 1);
        var ownership = new RunOwnershipRegistry(limits);
        var lease = await ownership.AcquireAsync(
            "run-dispose",
            "lane-dispose",
            CancellationToken.None);

        await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => lease.DisposeAsync().AsTask()));

        var diagnostics = ownership.GetDiagnostics();
        Assert.Equal(0, diagnostics.ActiveRunCount);
        Assert.Equal(0, diagnostics.WaitingRunCount);
        Assert.Equal(0, diagnostics.LaneCount);
        Assert.Same(limits, diagnostics.Limits);
        Assert.DoesNotContain(
            typeof(RunOwnershipDiagnostics).GetProperties(),
            property => property.Name.Contains(
                "Id",
                StringComparison.OrdinalIgnoreCase));

        await using var reused = await ownership.AcquireAsync(
            "run-dispose",
            "lane-dispose",
            CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentOwnershipStressRestoresBoundedDiagnostics()
    {
        const int laneCount = 8;
        const int runsPerLane = 12;
        var limits = new RunOwnershipLimits(
            maxActiveRuns: laneCount * runsPerLane,
            maxLanes: laneCount,
            maxWaitersPerLane: runsPerLane - 1);
        var ownership = new RunOwnershipRegistry(limits);
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, laneCount * runsPerLane)
            .Select(
                index => Task.Run(
                    async () =>
                    {
                        await start.Task;
                        await using var lease =
                            await ownership.AcquireAsync(
                                $"stress-run-{index}",
                                $"stress-lane-{index % laneCount}",
                                CancellationToken.None);
                        var snapshot = ownership.GetDiagnostics();
                        Assert.InRange(
                            snapshot.ActiveRunCount,
                            1,
                            limits.MaxActiveRuns);
                        Assert.InRange(
                            snapshot.WaitingRunCount,
                            0,
                            snapshot.ActiveRunCount);
                        Assert.InRange(
                            snapshot.LaneCount,
                            1,
                            limits.MaxLanes);
                        await Task.Yield();
                    }))
            .ToArray();

        start.TrySetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        var final = ownership.GetDiagnostics();
        Assert.Equal(0, final.ActiveRunCount);
        Assert.Equal(0, final.WaitingRunCount);
        Assert.Equal(0, final.LaneCount);
    }

    [Fact]
    public async Task ConcurrentOwnershipOverflowNeverExceedsConfiguredLimit()
    {
        const int maxActiveRuns = 8;
        var limits = new RunOwnershipLimits(
            maxActiveRuns,
            maxLanes: maxActiveRuns,
            maxWaitersPerLane: 1);
        var ownership = new RunOwnershipRegistry(limits);
        var admitted = await Task.WhenAll(
            Enumerable.Range(0, maxActiveRuns)
                .Select(
                    index => ownership
                        .AcquireAsync(
                            $"overflow-holder-{index}",
                            $"overflow-lane-{index}",
                            CancellationToken.None)
                        .AsTask()));

        Assert.Equal(maxActiveRuns, ownership.ActiveRunCount);
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var overflowAttempts = Enumerable.Range(0, 64)
            .Select(
                index => Task.Run(
                    async () =>
                    {
                        await start.Task;
                        var exception = await Assert.ThrowsAsync<
                            RunWorkloadCapacityExceededException>(
                            () => ownership
                                .AcquireAsync(
                                    $"overflow-rejected-{index}",
                                    $"overflow-extra-lane-{index}",
                                    CancellationToken.None)
                                .AsTask());
                        Assert.InRange(
                            ownership.ActiveRunCount,
                            1,
                            maxActiveRuns);
                        return exception;
                    }))
            .ToArray();
        start.TrySetResult();
        var rejected = await Task.WhenAll(overflowAttempts);

        Assert.All(
            rejected,
            exception => Assert.Equal(
                RunWorkloadCapacityReasonCodes.MaxActiveRuns,
                exception.ReasonCode));
        Assert.Equal(maxActiveRuns, ownership.ActiveRunCount);
        await Task.WhenAll(
            admitted.Select(lease => lease.DisposeAsync().AsTask()));

        var final = ownership.GetDiagnostics();
        Assert.Equal(0, final.ActiveRunCount);
        Assert.Equal(0, final.WaitingRunCount);
        Assert.Equal(0, final.LaneCount);
    }

    [Fact]
    public void BudgetTrackerEnforcesDurationTokensCostTurnsAndActions()
    {
        var started = DateTimeOffset.UtcNow;
        var budget = new AgentBudget
        {
            MaxTurns = 2,
            MaxActions = 1,
            MaxDurationMs = 100,
            MaxTokens = 10,
            MaxCostUsd = "0.01"
        };
        var tracker = new BudgetTracker(budget, started);

        Assert.Equal(
            "max_turns",
            tracker.CanStartTurn(
                new AgentUsage { Turns = 2 },
                started).Reason);
        Assert.Equal(
            "max_actions",
            tracker.CanDispatchAction(
                new AgentUsage { Actions = 1 },
                started).Reason);
        Assert.Equal(
            "max_duration",
            tracker.CheckShared(
                new AgentUsage(),
                started.AddMilliseconds(100)).Reason);
        Assert.Equal(
            "max_tokens",
            tracker.CheckShared(
                new AgentUsage { InputTokens = 6, OutputTokens = 4 },
                started).Reason);
        Assert.Equal(
            "max_cost",
            tracker.CheckShared(
                new AgentUsage { CostUsd = "0.01" },
                started).Reason);
        Assert.True(
            tracker.CheckAfterCharge(
                new AgentUsage
                {
                    InputTokens = 6,
                    OutputTokens = 4,
                    CostUsd = "0.01"
                },
                started).Allowed);
        Assert.Equal(
            "max_tokens",
            tracker.CheckAfterCharge(
                new AgentUsage
                {
                    InputTokens = 6,
                    OutputTokens = 5
                },
                started).Reason);
        Assert.Equal(
            "max_cost",
            tracker.CheckAfterCharge(
                new AgentUsage { CostUsd = "0.0100001" },
                started).Reason);
    }

    [Fact]
    public void BudgetTrackerUsesWideTokenArithmetic()
    {
        var started = DateTimeOffset.UtcNow;
        var tracker = new BudgetTracker(
            new AgentBudget
            {
                MaxTurns = 1,
                MaxActions = 1,
                MaxDurationMs = 1_000,
                MaxTokens = int.MaxValue,
                MaxCostUsd = "1"
            },
            started);

        var decision = tracker.CheckShared(
            new AgentUsage
            {
                InputTokens = int.MaxValue,
                OutputTokens = int.MaxValue
            },
            started);

        Assert.False(decision.Allowed);
        Assert.Equal("max_tokens", decision.Reason);
    }

    [Theory]
    [InlineData("invalid", "1")]
    [InlineData("-1", "1")]
    [InlineData("0", "invalid")]
    [InlineData("0", "-1")]
    [InlineData("0", "999999999999999999999999999999999999999999")]
    public void BudgetTrackerFailsClosedWhenCostCannotBeParsed(
        string usageCost,
        string maximumCost)
    {
        var started = DateTimeOffset.UtcNow;
        var tracker = new BudgetTracker(
            new AgentBudget
            {
                MaxTurns = 1,
                MaxActions = 1,
                MaxDurationMs = 1_000,
                MaxTokens = 1,
                MaxCostUsd = maximumCost
            },
            started);

        var decision = tracker.CheckShared(
            new AgentUsage { CostUsd = usageCost },
            started);

        Assert.False(decision.Allowed);
        Assert.Equal("invalid_cost", decision.Reason);
    }
}

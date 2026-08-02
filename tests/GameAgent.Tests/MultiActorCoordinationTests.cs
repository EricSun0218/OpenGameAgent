using System.Collections;
using System.Collections.Concurrent;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class MultiActorCoordinationTests
{
    [Fact]
    public async Task BatchBoundsConcurrencyAndReturnsInputOrder()
    {
        var runtime = new RecordingRuntime(delayMs: 30);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxBatchSize: 16,
                maxConcurrentRuns: 2));
        var sourceRuns = Enumerable.Range(0, 6)
            .Select(Request)
            .ToArray();

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "tick-12",
                Coordinate(),
                sourceRuns));

        Assert.InRange(runtime.PeakConcurrency, 2, 2);
        Assert.Equal(
            Enumerable.Range(0, 6),
            outcome.Results.Select(item => item.InputIndex));
        Assert.All(outcome.Results, item => Assert.True(item.Succeeded));
        Assert.All(
            runtime.Received,
            request =>
            {
                Assert.Equal("tick-12", request.Run.BatchId);
                Assert.True(
                    GameContextEnvelope.TryRead(
                        request.Run,
                        out var coordinate));
                Assert.Equal("world-v12", coordinate!.StateVersion);
            });
        Assert.All(
            sourceRuns,
            request => Assert.Null(request.Run.BatchId));
    }

    [Fact]
    public async Task MaxConcurrentRunsIsCoordinatorWideAcrossBatches()
    {
        var runtime = new ReleaseGatedRuntime();
        var lifecycle = new CountingBatchStartLifecycle(expectedStarts: 2);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxBatchSize: 4,
                maxConcurrentRuns: 1),
            lifecycle);

        var first = coordinator.RunAsync(
                new MultiActorDecisionBatch(
                    "coordinator-wide-first",
                    Coordinate(),
                    new[] { Request(1) }))
            .AsTask();
        var second = coordinator.RunAsync(
                new MultiActorDecisionBatch(
                    "coordinator-wide-second",
                    Coordinate(),
                    new[] { Request(2) }))
            .AsTask();

        await lifecycle.AllStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.FirstEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runtime.Active);
        Assert.Equal(1, runtime.Peak);

        runtime.Release();
        var outcomes = await Task.WhenAll(first, second);

        Assert.All(
            outcomes,
            outcome => Assert.True(Assert.Single(outcome.Results).Succeeded));
        Assert.Equal(0, runtime.Active);
        Assert.Equal(1, runtime.Peak);
    }

    [Fact]
    public async Task ConcurrentRetryDoesNotAbortActivePreparedBatch()
    {
        var runtime = new ReleaseGatedRuntime();
        var lifecycle = new RecordingLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "same-prepared-batch",
                Coordinate(),
                new[] { Request(1) }));
        var first = coordinator.RunPreparedBatchAsync(prepared).AsTask();
        await runtime.FirstEntered.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var error = await Assert.ThrowsAsync<
                MultiActorBatchAlreadyActiveException>(
                () => coordinator.RunPreparedBatchAsync(prepared)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("same-prepared-batch", error.BatchId);
            Assert.Null(lifecycle.AbortedBatchId);
        }
        finally
        {
            runtime.Release();
        }

        var completed = await first;
        Assert.True(Assert.Single(completed.Results).Succeeded);
        Assert.Null(lifecycle.AbortedBatchId);
    }

    [Fact]
    public async Task SharedRuntimeFencesBatchAcrossCoordinators()
    {
        var runtime = new ReleaseGatedRuntime();
        var lifecycle = new RecordingLifecycle();
        var firstCoordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var secondCoordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var firstPrepared = firstCoordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "shared-runtime-batch",
                Coordinate(),
                new[] { Request(1) }));
        var secondPrepared = secondCoordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "shared-runtime-batch",
                Coordinate(),
                new[] { Request(1) }));
        var first = firstCoordinator
            .RunPreparedBatchAsync(firstPrepared)
            .AsTask();
        await runtime.FirstEntered.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var error = await Assert.ThrowsAsync<
                MultiActorBatchAlreadyActiveException>(
                () => secondCoordinator
                    .RunPreparedBatchAsync(secondPrepared)
                    .AsTask());
            Assert.Equal("shared-runtime-batch", error.BatchId);
            Assert.Null(lifecycle.AbortedBatchId);
        }
        finally
        {
            runtime.Release();
        }

        Assert.True(Assert.Single((await first).Results).Succeeded);
        Assert.Null(lifecycle.AbortedBatchId);
    }

    [Fact]
    public async Task BatchCapacityRejectsBeforeLifecycle()
    {
        var runtime = new ReleaseGatedRuntime();
        var lifecycle = new RecordingLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxBatchSize: 2,
                maxConcurrentBatches: 1,
                maxQueuedParticipants: 2),
            lifecycle);
        var first = coordinator.RunAsync(
                new MultiActorDecisionBatch(
                    "capacity-first",
                    Coordinate(),
                    new[] { Request(1) }))
            .AsTask();
        await runtime.FirstEntered.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var error = await Assert.ThrowsAsync<
                MultiActorBatchCapacityExceededException>(
                () => coordinator.RunAsync(
                        new MultiActorDecisionBatch(
                            "capacity-second",
                            Coordinate(),
                            new[] { Request(2) }))
                    .AsTask());
            Assert.Equal(1, error.Limit);
            Assert.Equal(1, lifecycle.StartedCount);
            Assert.Null(lifecycle.AbortedBatchId);
        }
        finally
        {
            runtime.Release();
        }

        _ = await first;
        Assert.Equal(0, coordinator.ActiveBatchOperationCount);
        Assert.Equal(0, coordinator.QueuedParticipantCount);
    }

    [Fact]
    public async Task QueuedParticipantCapacityRejectsBeforeLifecycle()
    {
        var runtime = new ReleaseGatedRuntime();
        var lifecycle = new RecordingLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxBatchSize: 2,
                maxConcurrentBatches: 2,
                maxQueuedParticipants: 2),
            lifecycle);
        var first = coordinator.RunAsync(
                new MultiActorDecisionBatch(
                    "queued-capacity-first",
                    Coordinate(),
                    new[] { Request(1), Request(2) }))
            .AsTask();
        await runtime.FirstEntered.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var error = await Assert.ThrowsAsync<
                MultiActorQueuedParticipantCapacityExceededException>(
                () => coordinator.RunAsync(
                        new MultiActorDecisionBatch(
                            "queued-capacity-second",
                            Coordinate(),
                            new[] { Request(3) }))
                    .AsTask());
            Assert.Equal(2, error.Limit);
            Assert.Equal(1, lifecycle.StartedCount);
            Assert.Null(lifecycle.AbortedBatchId);
        }
        finally
        {
            runtime.Release();
        }

        var outcome = await first;
        Assert.All(outcome.Results, result => Assert.True(result.Succeeded));
        Assert.Equal(0, coordinator.ActiveBatchOperationCount);
        Assert.Equal(0, coordinator.QueuedParticipantCount);
    }

    [Fact]
    public async Task PreCancelledPreparedBatchDoesNotTouchLifecycleOrRuntime()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var lifecycle = new RecordingLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "pre-cancelled-prepared-batch",
                Coordinate(),
                new[] { Request(1) }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RunPreparedBatchAsync(
                    prepared,
                    cancellation.Token)
                .AsTask());

        Assert.False(lifecycle.Started);
        Assert.Null(lifecycle.AbortedBatchId);
        Assert.Empty(runtime.Received);
    }

    [Fact]
    public async Task LargeBatchKeepsPrivateActorContextIsolated()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new RecordingRuntime(
            delayMs: 10,
            admissionCheck: () => lifecycle.Started);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxBatchSize: 64,
                maxConcurrentRuns: 8),
            lifecycle);
        var sourceRuns = Enumerable.Range(0, 64)
            .Select(
                index =>
                {
                    var request = Request(index);
                    request.Context = new[]
                    {
                        new ContextCandidate(
                            "private-context-" + index,
                            "actor-private",
                            ProtocolJson.ParseElement(
                                $$"""{"agentId":"npc-{{index}}","secret":{{index}}}"""),
                            required: true)
                    };
                    request.WorkloadClass =
                        ProviderWorkloadClasses.Background;
                    return request;
                })
            .ToArray();

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "world-tick-64",
                Coordinate(),
                sourceRuns));

        Assert.Equal(8, runtime.PeakConcurrency);
        Assert.Equal(
            Enumerable.Range(0, 64),
            outcome.Results.Select(item => item.InputIndex));
        Assert.Equal(
            Enumerable.Range(0, 64).Select(index => "npc-" + index),
            outcome.Results.Select(item => item.AgentId));
        Assert.Equal(64, lifecycle.Manifest!.Participants.Count);
        Assert.Equal(64, lifecycle.FinishedAgentIds.Count);
        Assert.All(
            runtime.Received,
            request =>
            {
                var privateContext = Assert.Single(request.Context);
                Assert.Equal(
                    request.Run.AgentId,
                    privateContext.Content!.Value
                        .GetProperty("agentId")
                        .GetString());
                Assert.True(
                    GameContextEnvelope.TryRead(
                        request.Run,
                        out var coordinate));
                Assert.Equal("world-v12", coordinate!.StateVersion);
                Assert.Equal(
                    ProviderWorkloadClasses.Background,
                    request.WorkloadClass);
            });
    }

    [Fact]
    public async Task BatchPreservesEachActorsObserverIncarnation()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var requests = Enumerable.Range(1, 2)
            .Select(
                index =>
                {
                    var request = Request(index);
                    GameContextEnvelope.Attach(
                        request.Run,
                        new GameContextCoordinate(
                            "world",
                            "prime",
                            saveRevision: 12,
                            new GameEntityIdentity(
                                request.Run.AgentId,
                                incarnation: index),
                            sceneId: "scene-" + index,
                            stateVersion: "world-v12",
                            gameTime: new GameTimePoint(
                                "simulation",
                                "prime",
                                epoch: 1,
                                tick: 12)));
                    return request;
                })
            .ToArray();

        await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "observer-batch",
                Coordinate(),
                requests));

        Assert.Equal(2, runtime.Received.Count);
        foreach (var received in runtime.Received)
        {
            Assert.True(
                GameContextEnvelope.TryRead(
                    received.Run,
                    out var coordinate));
            var index = int.Parse(
                received.Run.AgentId.Substring("npc-".Length),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(received.Run.AgentId, coordinate!.Observer!.EntityId);
            Assert.Equal(index, coordinate.Observer.Incarnation);
            Assert.Equal("scene-" + index, coordinate.SceneId);
            Assert.Equal("world-v12", coordinate.StateVersion);
        }
    }

    [Fact]
    public async Task BatchPropagatesTheExactSharedSessionToParticipants()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "session-bound-batch",
                Coordinate("session-1"),
                new[]
                {
                    Request(1, "session-1"),
                    Request(2, "session-1")
                }));

        Assert.Equal("session-1", outcome.Manifest.Coordinate.SessionId);
        Assert.All(
            runtime.Received,
            request =>
            {
                Assert.Equal("session-1", request.Run.SessionId);
                Assert.True(
                    GameContextEnvelope.TryRead(
                        request.Run,
                        out var coordinate));
                Assert.Equal("session-1", coordinate!.SessionId);
            });
    }

    [Fact]
    public async Task BatchRejectsACrossSessionParticipantBeforeDispatch()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(runtime);

        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "cross-session-batch",
                        Coordinate("session-a"),
                        new[] { Request(1, "session-b") }))
                .AsTask());

        Assert.Empty(runtime.Received);
    }

    [Fact]
    public async Task BatchRejectsContradictoryOrSharedObserverCoordinates()
    {
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 0));
        var contradictory = Request(1);
        GameContextEnvelope.Attach(
            contradictory.Run,
            new GameContextCoordinate(
                "world",
                "branch",
                saveRevision: 12,
                new GameEntityIdentity("npc-1", incarnation: 1)));

        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "contradictory-batch",
                        Coordinate(),
                        new[] { contradictory }))
                .AsTask());

        var sharedObserver = new GameContextCoordinate(
            "world",
            "prime",
            saveRevision: 12,
            new GameEntityIdentity("not-all-actors", incarnation: 1),
            stateVersion: "world-v12",
            gameTime: new GameTimePoint(
                "simulation",
                "prime",
                epoch: 1,
                tick: 12));
        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "shared-observer-batch",
                        sharedObserver,
                        new[] { Request(1), Request(2) }))
                .AsTask());
    }

    [Fact]
    public async Task BatchSnapshotHasAnAggregateByteBudget()
    {
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 0),
            new MultiActorCoordinatorOptions(
                maxBatchSize: 2,
                maxConcurrentRuns: 1,
                maxSnapshotUtf8BytesPerRun: 8_192,
                maxBatchSnapshotUtf8Bytes: 10_000));
        var requests = new[] { Request(1), Request(2) };
        foreach (var request in requests)
        {
            request.Context = new[]
            {
                new ContextCandidate(
                    "large-" + request.Run.AgentId,
                    "actor-private",
                    ProtocolJson.ParseElement(
                        $$"""{"text":"{{new string('x', 5_000)}}"}"""))
            };
        }

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "bounded-snapshot-batch",
                        Coordinate(),
                        requests))
                .AsTask());

        Assert.Equal(
            "multi_actor_batch_snapshot_bytes_exceeded",
            error.LimitCode);
    }

    [Theory]
    [InlineData(
        15_999L,
        16L,
        60_000L,
        "2",
        "multi_actor_batch_token_budget_exceeded")]
    [InlineData(
        16_000L,
        15L,
        60_000L,
        "2",
        "multi_actor_batch_action_budget_exceeded")]
    [InlineData(
        16_000L,
        16L,
        59_999L,
        "2",
        "multi_actor_batch_duration_budget_exceeded")]
    [InlineData(
        16_000L,
        16L,
        60_000L,
        "1.999",
        "multi_actor_batch_cost_budget_exceeded")]
    public async Task AggregateRunBudgetRejectsBeforeLifecycleOrActorStart(
        long maxTokens,
        long maxActions,
        long maxDurationMs,
        string maxCostUsd,
        string expectedCode)
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "aggregate-budget-rejected",
                        Coordinate(),
                        new[] { Request(1), Request(2) },
                        new MultiActorBatchBudget(
                            maxTokens,
                            maxActions,
                            maxDurationMs,
                            maxCostUsd)))
                .AsTask());

        Assert.Equal(expectedCode, error.LimitCode);
        Assert.Empty(runtime.Received);
        Assert.Null(lifecycle.Manifest);
        Assert.Null(lifecycle.AbortedBatchId);
    }

    [Fact]
    public async Task InjectedExtensionsAreCapacityCheckedBeforeLifecycle()
    {
        var request = Request(1);
        for (var index = 0;
             index < ProtocolLimits.MaxProtocolExtensions - 1;
             index++)
        {
            request.Run.Extensions["extension-" + index] =
                ProtocolJson.ParseElement("true");
        }
        var lifecycle = new RecordingLifecycle();
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "extension-capacity",
                        Coordinate(),
                        new[] { request }))
                .AsTask());

        Assert.Equal("multi_actor_run_extensions_exceeded", error.LimitCode);
        Assert.Empty(runtime.Received);
        Assert.Null(lifecycle.Manifest);
        Assert.Null(lifecycle.AbortedBatchId);
    }

    [Fact]
    public async Task AggregateRunBudgetExactReservationIsManifested()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var limit = new MultiActorBatchBudget(
            maxTokens: 16_000,
            maxActions: 16,
            maxDurationMs: 60_000,
            maxCostUsd: "2");

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "aggregate-budget-exact",
                Coordinate(),
                new[] { Request(1), Request(2) },
                limit));

        Assert.Equal(2, outcome.Results.Count);
        var reservation = Assert.IsType<MultiActorBatchBudgetReservation>(
            outcome.Manifest.BudgetReservation);
        Assert.Same(limit, reservation.Limit);
        Assert.Equal(16_000, reservation.ReservedTokens);
        Assert.Equal(16, reservation.ReservedActions);
        Assert.Equal(60_000, reservation.ReservedDurationMs);
        Assert.Equal("2", reservation.ReservedCostUsd);
        Assert.Same(
            reservation,
            lifecycle.Manifest!.BudgetReservation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1e2")]
    [InlineData(" 1")]
    public void AggregateRunBudgetRejectsNonCanonicalCost(string cost)
    {
        Assert.Throws<ArgumentException>(
            () => new MultiActorBatchBudget(
                maxTokens: 1,
                maxActions: 0,
                maxDurationMs: 1,
                maxCostUsd: cost));
    }

    [Fact]
    public async Task OversizedTranscriptIsRejectedBeforeSnapshotEncoding()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxSnapshotUtf8BytesPerRun: 4 * 1_048_576,
                maxBatchSnapshotUtf8Bytes: 4 * 1_048_576));
        var request = Request(1);
        request.InitialTranscript = new[]
        {
            new NormalizedMessage
            {
                MessageId = "oversized",
                Role = NormalizedRoles.User,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText(
                        new string('x', 5 * 1_048_576))
                }
            }
        };

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "bounded-encoding-batch",
                        Coordinate(),
                        new[] { request }))
                .AsTask());

        Assert.Equal("multi_actor_snapshot_bytes_exceeded", error.LimitCode);
        Assert.Empty(runtime.Received);
    }

    [Fact]
    public async Task OneActorFailureDoesNotCancelOtherActors()
    {
        var runtime = new RecordingRuntime(
            delayMs: 1,
            failingAgentId: "npc-2");
        var coordinator = new MultiActorDecisionCoordinator(runtime);

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "tick-12",
                Coordinate(),
                Enumerable.Range(0, 4).Select(Request)));

        Assert.Equal(3, outcome.Results.Count(item => item.Succeeded));
        var failed = Assert.Single(
            outcome.Results,
            item => !item.Succeeded);
        Assert.Equal("npc-2", failed.AgentId);
        Assert.IsType<InvalidOperationException>(failed.Error);
    }

    [Fact]
    public async Task LifecycleAbortsWhenActorHasNoDurableOutcome()
    {
        var lifecycle = new RecordingLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(
                delayMs: 1,
                failingAgentId: "npc-2"),
            lifecycle: lifecycle);

        var error = await Assert.ThrowsAsync<
            MultiActorBatchExecutionUncertainException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "uncertain-actor",
                        Coordinate(),
                        Enumerable.Range(0, 4).Select(Request)))
                .AsTask());

        Assert.Equal("uncertain-actor", error.BatchId);
        Assert.Equal(new[] { "run-2" }, error.RunIds);
        Assert.DoesNotContain("npc-2", lifecycle.FinishedAgentIds);
        Assert.Equal("uncertain-actor", lifecycle.AbortedBatchId);
        Assert.Equal(
            "batch_execution_failed",
            lifecycle.AbortReasonCode);
    }

    [Fact]
    public async Task FailedDurableOutcomeIsNotReportedAsActorSuccess()
    {
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(
                delayMs: 0,
                failedOutcomeAgentId: "npc-2"));

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "tick-12",
                Coordinate(),
                Enumerable.Range(0, 4).Select(Request)));

        Assert.Equal(3, outcome.Results.Count(item => item.Succeeded));
        var failed = Assert.Single(
            outcome.Results,
            item => !item.Succeeded);
        Assert.Equal("npc-2", failed.AgentId);
        Assert.Null(failed.Error);
        Assert.Equal(RunStates.Failed, failed.Outcome!.Run.State);
        Assert.Equal("provider_failed", failed.Outcome.ErrorCode);
    }

    [Fact]
    public async Task DuplicateActorOrDecisionKeyIsRejected()
    {
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 0));
        var duplicateActor = new[] { Request(1), Request(1) };
        duplicateActor[1].Run.RunId = "run-other";
        duplicateActor[1].Run.DecisionKey = "decision-other";

        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "batch",
                        Coordinate(),
                        duplicateActor))
                .AsTask());

        var duplicateDecision = new[] { Request(1), Request(2) };
        duplicateDecision[1].Run.DecisionKey =
            duplicateDecision[0].Run.DecisionKey;
        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "batch",
                        Coordinate(),
                        duplicateDecision))
                .AsTask());
    }

    [Fact]
    public async Task LifecycleReceivesManifestBeforeActorsAndEveryFinish()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new RecordingRuntime(
            delayMs: 1,
            admissionCheck: () => lifecycle.Started);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "tick-12",
                Coordinate(),
                Enumerable.Range(0, 4).Select(Request)));

        Assert.Equal("tick-12", lifecycle.Manifest!.BatchId);
        Assert.Equal(4, lifecycle.Manifest.Participants.Count);
        Assert.Equal(
            outcome.Results.Select(item => item.AgentId).OrderBy(
                value => value,
                StringComparer.Ordinal),
            lifecycle.FinishedAgentIds.OrderBy(
                value => value,
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task PausedActorResumesAfterCoordinatorRecreation()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new PausingRuntime();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);

        var batch = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "paused-batch",
                Coordinate(),
                new[] { Request(1) }));

        var paused = Assert.Single(batch.Results);
        Assert.False(paused.Outcome!.IsTerminal);
        Assert.Empty(lifecycle.FinishedAgentIds);
        Assert.Equal(0, coordinator.ActiveParticipantOperationCount);
        Assert.Equal(0, lifecycle.Manifest!.Participants[0].InputIndex);

        var recoveredCoordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var resumed = await recoveredCoordinator.ResumeParticipantAsync(
            "paused-batch",
            Assert.Single(batch.Manifest.Participants));

        Assert.True(resumed.Outcome!.IsTerminal);
        Assert.Equal(0, resumed.InputIndex);
        Assert.Equal("npc-1", resumed.AgentId);
        Assert.Equal("decision-1", resumed.DecisionKey);
        Assert.Equal(
            new[] { "npc-1" },
            lifecycle.FinishedAgentIds);
        Assert.Equal(
            0,
            recoveredCoordinator.ActiveParticipantOperationCount);
    }

    [Fact]
    public async Task ParticipantResumeMergesCallerSemanticExpectation()
    {
        var runtime = new PausingRuntime();
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var batch = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "semantic-batch",
                Coordinate(),
                new[] { Request(1) }));
        var currentValue = ProtocolJson.ParseElement(
            """{"revision":13,"timeline":"prime"}""");
        var expectation = DurableRunSemanticExpectation.FromJson(
            "game.currentCoordinate",
            currentValue);

        var result = await coordinator.ResumeParticipantAsync(
            "semantic-batch",
            Assert.Single(batch.Manifest.Participants),
            expectation);

        Assert.True(result.Outcome!.IsTerminal);
        Assert.Equal(
            expectation.ExtensionName,
            runtime.LastResumeGuard!.SemanticExtensionName);
        Assert.Equal(
            expectation.ExpectedSha256,
            runtime.LastResumeGuard.ExpectedSemanticExtensionSha256);
    }

    [Fact]
    public async Task ParticipantResumeAdmissionIsBoundedAndReleased()
    {
        var runtime = new BlockingPausingRuntime();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxConcurrentParticipantResumes: 1));
        var batch = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "bounded-resumes",
                Coordinate(),
                new[] { Request(1), Request(2) }));
        Assert.All(
            batch.Results,
            result => Assert.False(result.Outcome!.IsTerminal));
        Assert.Equal(0, coordinator.ActiveParticipantOperationCount);

        var first = coordinator.ResumeParticipantAsync(
                "bounded-resumes",
                batch.Manifest.Participants[0])
            .AsTask();
        await runtime.ResumeEntered.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, coordinator.ActiveParticipantOperationCount);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ResumeParticipantAsync(
                    "bounded-resumes",
                    batch.Manifest.Participants[0])
                .AsTask());
        var capacity = await Assert.ThrowsAsync<
            MultiActorParticipantResumeCapacityExceededException>(
            () => coordinator.ResumeParticipantAsync(
                    "bounded-resumes",
                    batch.Manifest.Participants[1])
                .AsTask());
        Assert.Equal(1, capacity.Limit);

        runtime.Release();
        Assert.True((await first).Outcome!.IsTerminal);
        Assert.Equal(0, coordinator.ActiveParticipantOperationCount);
        Assert.True(
            (await coordinator.ResumeParticipantAsync(
                "bounded-resumes",
                batch.Manifest.Participants[1])).Outcome!.IsTerminal);
        Assert.Equal(0, coordinator.ActiveParticipantOperationCount);
    }

    [Fact]
    public async Task ResumeFailureLeavesParticipantRetriable()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new FailingOncePausingRuntime();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var batch = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "retry-resume",
                Coordinate(),
                new[] { Request(1) }));
        var participant = Assert.Single(batch.Manifest.Participants);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ResumeParticipantAsync(
                    "retry-resume",
                    participant)
                .AsTask());
        Assert.Empty(lifecycle.FinishedAgentIds);
        Assert.Equal(0, coordinator.ActiveParticipantOperationCount);

        var resumed = await coordinator.ResumeParticipantAsync(
            "retry-resume",
            participant);
        Assert.True(resumed.Outcome!.IsTerminal);
        Assert.Equal(
            new[] { "npc-1" },
            lifecycle.FinishedAgentIds);
    }

    [Fact]
    public async Task ResumeFinishFailureCanReplayIdempotentFinish()
    {
        var lifecycle = new FailingFinishLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            new PausingRuntime(),
            lifecycle: lifecycle);
        var batch = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "resume-finish-failed",
                Coordinate(),
                new[] { Request(1) }));
        var participant = Assert.Single(batch.Manifest.Participants);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ResumeParticipantAsync(
                    "resume-finish-failed",
                    participant)
                .AsTask());

        var replayed = await coordinator.ResumeParticipantAsync(
            "resume-finish-failed",
            participant);

        Assert.True(replayed.Outcome!.IsTerminal);
        Assert.Equal(2, lifecycle.FinishAttempts);
        Assert.Equal(1, lifecycle.SuccessfulFinishes);
        Assert.Null(lifecycle.AbortedBatchId);
        Assert.Equal(0, coordinator.ActiveParticipantOperationCount);
    }

    [Fact]
    public async Task AbandonedParticipantClosesHostLifecycleWindow()
    {
        var lifecycle = new RecordingLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            new PausingRuntime(),
            lifecycle: lifecycle);
        var batch = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "abandoned-participant",
                Coordinate(),
                new[] { Request(1) }));
        Assert.False(Assert.Single(batch.Results).Outcome!.IsTerminal);
        var participant = Assert.Single(batch.Manifest.Participants);

        var abandoned =
            await coordinator.ReconcileAbandonedParticipantAsync(
                "abandoned-participant",
                participant,
                "game_removed_actor");

        var error = Assert.IsType<
            MultiActorParticipantAbandonedException>(abandoned.Error);
        Assert.Equal(participant.RunId, error.RunId);
        Assert.Equal("game_removed_actor", error.ReasonCode);
        Assert.Equal(
            new[] { participant.AgentId },
            lifecycle.FinishedAgentIds);
        Assert.Equal(0, coordinator.ActiveParticipantOperationCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlockingLifecycleCallbackCannotWedgeBatch(bool blockStart)
    {
        var lifecycle = new SynchronouslyBlockingLifecycle(blockStart);
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 0),
            new MultiActorCoordinatorOptions(
                lifecycleSettlementTimeout: TimeSpan.FromMilliseconds(25),
                maxDetachedLifecycleNotifications: 1,
                batchAbortSettlementTimeout: TimeSpan.FromMilliseconds(25)),
            lifecycle);

        try
        {
            var error = await Assert.ThrowsAsync<
                MultiActorBatchAbortUncertainException>(
                () => coordinator.RunAsync(
                        new MultiActorDecisionBatch(
                            "blocked-lifecycle",
                            Coordinate(),
                            new[] { Request(1) }))
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal("blocked-lifecycle", error.BatchId);
            Assert.Equal(
                blockStart
                    ? "batch_start_uncertain"
                    : "actor_finish_uncertain",
                error.ReasonCode);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    [Fact]
    public async Task LifecycleCallbackCapacityIsUncertainAndRecovers()
    {
        var limiter = new BoundedCallbackProcessLimiter(1);
        var lifecycleDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        var blockerDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        var lifecycle = new RecordingLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 0),
            options: null,
            lifecycle: lifecycle,
            callbackExecutionDispatcher: lifecycleDispatcher);
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(
            blockerDispatcher.TryExecute(
                () =>
                {
                    entered.TrySetResult(true);
                    release.Wait();
                    return new ValueTask<int>(1);
                },
                out var blocker));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var error = await Assert.ThrowsAsync<
                MultiActorBatchAbortUncertainException>(
                () => coordinator.RunAsync(
                        new MultiActorDecisionBatch(
                            "lifecycle-capacity",
                            Coordinate(),
                            new[] { Request(1) }))
                    .AsTask());
            Assert.Equal("lifecycle-capacity", error.BatchId);
            Assert.Equal("batch_start_uncertain", error.ReasonCode);
            Assert.Equal(0, lifecycle.StartedCount);
        }
        finally
        {
            release.Set();
            await blocker.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var recovered = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "lifecycle-capacity-recovered",
                Coordinate(),
                new[] { Request(2) }));
        Assert.True(Assert.Single(recovered.Results).Succeeded);
        Assert.Equal("lifecycle-capacity-recovered", lifecycle.Manifest!.BatchId);
    }

    [Fact]
    public async Task LifecycleStartFailureAbortsTheUncertainStagingBatch()
    {
        var lifecycle = new FailingStartLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 0),
            lifecycle: lifecycle);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "tick-12",
                        Coordinate(),
                        new[] { Request(1) }))
                .AsTask());

        Assert.Equal("tick-12", lifecycle.AbortedBatchId);
        Assert.Equal(
            "batch_execution_failed",
            lifecycle.AbortReasonCode);
    }

    [Fact]
    public async Task LateBatchStartIsNotRacedByAbort()
    {
        var lifecycle = new LateStartLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 0),
            new MultiActorCoordinatorOptions(
                lifecycleSettlementTimeout: TimeSpan.FromMilliseconds(25),
                maxDetachedLifecycleNotifications: 1),
            lifecycle);

        var error = await Assert.ThrowsAsync<
            MultiActorBatchAbortUncertainException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "late-start",
                        Coordinate(),
                        new[] { Request(1) }))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("batch_start_uncertain", error.ReasonCode);
        Assert.Equal(0, lifecycle.AbortCalls);
        lifecycle.Release();
        await lifecycle.Landed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, lifecycle.AbortCalls);
    }

    [Fact]
    public void BatchIdUsesTheProtocolIdentifierContract()
    {
        Assert.Throws<ArgumentException>(
            () => new MultiActorDecisionBatch(
                "invalid batch/id",
                Coordinate(),
                new[] { Request(1) }));
    }

    [Fact]
    public async Task CancellationAbortsAStartedStagingBatch()
    {
        var lifecycle = new RecordingLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 30_000),
            lifecycle: lifecycle);
        using var cancellation = new CancellationTokenSource();

        var pending = coordinator.RunAsync(
                new MultiActorDecisionBatch(
                    "tick-12",
                    Coordinate(),
                    Enumerable.Range(0, 4).Select(Request)),
                cancellation.Token)
            .AsTask();
        await Task.Delay(20);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pending);
        Assert.Equal("tick-12", lifecycle.AbortedBatchId);
        Assert.Equal("cancelled", lifecycle.AbortReasonCode);
    }

    [Fact]
    public async Task AbortCallbackCapacityFailureIsUncertainAndRecovers()
    {
        var limiter = new BoundedCallbackProcessLimiter(1);
        var lifecycleDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        var blockerDispatcher = new BoundedCallbackExecutionDispatcher(
            1,
            limiter);
        var runtime = new AbortCapacityRuntime(blockerDispatcher);
        var lifecycle = new RecordingLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            options: null,
            lifecycle: lifecycle,
            callbackExecutionDispatcher: lifecycleDispatcher);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        Task? first = null;
        Task? second = null;
        try
        {
            first = coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "abort-capacity",
                        Coordinate(),
                        new[] { Request(1) }),
                    firstCancellation.Token)
                .AsTask();
            await runtime.FirstEntered.WaitAsync(TimeSpan.FromSeconds(2));
            firstCancellation.Cancel();

            var aggregate = await Assert.ThrowsAsync<AggregateException>(
                () => first.WaitAsync(TimeSpan.FromSeconds(2)));
            var uncertain = Assert.Single(
                aggregate.InnerExceptions,
                item => item is MultiActorBatchAbortUncertainException);
            Assert.Equal(
                "abort-capacity",
                ((MultiActorBatchAbortUncertainException)uncertain).BatchId);
            Assert.Equal(
                "cancelled",
                ((MultiActorBatchAbortUncertainException)uncertain).ReasonCode);
            Assert.Null(lifecycle.AbortedBatchId);

            runtime.ReleaseBlocker();
            await runtime.StartedBlocker!.WaitAsync(TimeSpan.FromSeconds(2));

            second = coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "abort-capacity-recovered",
                        Coordinate(),
                        new[] { Request(2) }),
                    secondCancellation.Token)
                .AsTask();
            await runtime.SecondEntered.WaitAsync(TimeSpan.FromSeconds(2));
            secondCancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => second.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(
                "abort-capacity-recovered",
                lifecycle.AbortedBatchId);
            Assert.Equal("cancelled", lifecycle.AbortReasonCode);
        }
        finally
        {
            firstCancellation.Cancel();
            secondCancellation.Cancel();
            runtime.ReleaseBlocker();
            if (runtime.StartedBlocker is { } blocker)
            {
                await blocker.WaitAsync(TimeSpan.FromSeconds(2));
            }

            foreach (var pending in new[] { first, second })
            {
                if (pending is null)
                {
                    continue;
                }

                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception) when (pending.IsCompleted)
                {
                }
            }
        }
    }

    [Fact]
    public async Task BlockingAbortNotificationReturnsUncertainForReconciliation()
    {
        var lifecycle = new BlockingAbortLifecycle();
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 0),
            new MultiActorCoordinatorOptions(
                batchAbortSettlementTimeout: TimeSpan.FromMilliseconds(25),
                maxDetachedAbortNotifications: 1),
            lifecycle);

        try
        {
            var error = await Assert.ThrowsAsync<AggregateException>(
                () => coordinator.RunAsync(
                        new MultiActorDecisionBatch(
                            "uncertain-abort",
                            Coordinate(),
                            new[] { Request(1) }))
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)));

            var uncertain = Assert.Single(
                error.InnerExceptions,
                item => item is MultiActorBatchAbortUncertainException);
            Assert.Equal(
                "uncertain-abort",
                ((MultiActorBatchAbortUncertainException)uncertain).BatchId);
            Assert.True(lifecycle.AbortEntered.IsCompleted);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    [Fact]
    public async Task DecisionKeysMayUseGameSemanticText()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var request = Request(1);
        request.Run.DecisionKey = "npc 1 / world tick 12 / forage";

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "tick-12",
                Coordinate(),
                new[] { request }));

        Assert.True(Assert.Single(outcome.Results).Succeeded);
        Assert.Equal(
            "npc 1 / world tick 12 / forage",
            Assert.Single(runtime.Received).Run.DecisionKey);
    }

    [Fact]
    public async Task DecisionKeyUsesUnicodeScalarsInsteadOfUtf8Bytes()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var request = Request(1);
        request.Run.DecisionKey = new string('界', 256);

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "unicode-key",
                Coordinate(),
                new[] { request }));

        Assert.True(Assert.Single(outcome.Results).Succeeded);
        Assert.Equal(
            request.Run.DecisionKey,
            Assert.Single(runtime.Received).Run.DecisionKey);
    }

    [Fact]
    public async Task BatchDeepSnapshotsEveryRunBeforeFirstAwait()
    {
        var lifecycle = new BlockingStartLifecycle();
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var request = Request(1);
        request.Context = new[]
        {
            new ContextCandidate(
                "context-original",
                "state",
                ProtocolJson.ParseElement("""{"value":1}"""))
        };
        request.ActiveSkills = new[]
        {
            new SkillReference("skill-original", "1")
        };
        request.InitialTranscript = new[]
        {
            new NormalizedMessage
            {
                MessageId = "message-original",
                Role = NormalizedRoles.User,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText("original")
                }
            }
        };
        request.WorkloadClass = ProviderWorkloadClasses.Background;

        var pending = coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "tick-12",
                Coordinate(),
                new[] { request }));
        await lifecycle.Entered;
        request.Run.AgentId = "caller-mutated";
        request.Context = Array.Empty<ContextCandidate>();
        request.ActiveSkills = Array.Empty<SkillReference>();
        request.InitialTranscript = Array.Empty<NormalizedMessage>();
        request.WorkloadClass = ProviderWorkloadClasses.Interactive;
        lifecycle.Release();

        await pending;

        var received = Assert.Single(runtime.Received);
        Assert.Equal("npc-1", received.Run.AgentId);
        Assert.Equal(
            "context-original",
            Assert.Single(received.Context).Id);
        Assert.Equal(
            "skill-original",
            Assert.Single(received.ActiveSkills).SkillId);
        Assert.Equal(
            "message-original",
            Assert.Single(received.InitialTranscript).MessageId);
        Assert.Equal(
            ProviderWorkloadClasses.Background,
            received.WorkloadClass);
    }

    [Fact]
    public async Task BatchSnapshotsNestedInputsByIndexWithoutEnumeration()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var request = Request(1);
        var context = new IndexedOnlyReadOnlyList<ContextCandidate>(
            new ContextCandidate(
                "context-1",
                "state",
                ProtocolJson.ParseElement("""{"value":1}""")));
        var skills = new IndexedOnlyReadOnlyList<SkillReference>(
            new SkillReference("skill-1", "1"));
        var transcript = new IndexedOnlyReadOnlyList<NormalizedMessage>(
            new NormalizedMessage
            {
                MessageId = "message-1",
                Role = NormalizedRoles.User,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText("hello")
                }
            });
        request.Context = context;
        request.ActiveSkills = skills;
        request.InitialTranscript = transcript;

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "indexed-inputs",
                Coordinate(),
                new[] { request }));

        Assert.True(Assert.Single(outcome.Results).Succeeded);
        Assert.Equal(1, context.CountReads);
        Assert.Equal(1, skills.CountReads);
        Assert.Equal(1, transcript.CountReads);
        Assert.False(context.EnumeratorAccessed);
        Assert.False(skills.EnumeratorAccessed);
        Assert.False(transcript.EnumeratorAccessed);
    }

    [Fact]
    public async Task BatchRejectsNestedInputCountIndexMismatch()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var request = Request(1);
        request.Context = new MismatchedReadOnlyList<ContextCandidate>(
            reportedCount: 2,
            new ContextCandidate(
                "context-1",
                "state",
                ProtocolJson.ParseElement("""{"value":1}""")));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => coordinator.RunAsync(
                    new MultiActorDecisionBatch(
                        "mismatched-inputs",
                        Coordinate(),
                        new[] { request }))
                .AsTask());

        Assert.Empty(runtime.Received);
    }

    [Fact]
    public async Task PreparedBatchIsADeepImmutableRequestSnapshot()
    {
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var context = new List<ContextCandidate>
        {
            ObservationContext(
                scope: ObservationVisibilityScopes.Agent,
                audienceId: "npc-1",
                sessionId: "session-1",
                incarnation: 7)
        };
        var skills = new List<SkillReference>
        {
            new("skill-original", "1")
        };
        var message = new NormalizedMessage
        {
            MessageId = "message-original",
            Role = NormalizedRoles.User,
            CreatedAt = DateTimeOffset.Parse(
                "2026-01-02T03:04:05Z",
                System.Globalization.CultureInfo.InvariantCulture),
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText("original")
            }
        };
        var transcript = new List<NormalizedMessage> { message };
        var request = Request(1, "session-1");
        request.Context = context;
        request.ActiveSkills = skills;
        request.InitialTranscript = transcript;
        request.LaneId = "lane-original";
        request.WorkloadClass = ProviderWorkloadClasses.Background;
        request.FinalOutputContract = new FinalOutputContract(
            "decision-output",
            "1",
            ProtocolJson.ParseElement(
                """
                {
                  "type":"object",
                  "properties":{"intent":{"type":"string"}},
                  "required":["intent"],
                  "additionalProperties":false
                }
                """));
        request.Run.Extensions["caller.extension"] =
            ProtocolJson.ParseElement("""{"value":"original"}""");

        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "prepared-snapshot",
                Coordinate("session-1"),
                new[] { request }));
        var originalDigest = Assert.Single(prepared.RequestDigests);

        request.Run.AgentId = "caller-mutated";
        request.Run.Extensions.Clear();
        context.Clear();
        skills.Clear();
        transcript.Clear();
        message.MessageId = "caller-mutated";
        message.Parts[0].Text = "caller-mutated";
        request.Context = Array.Empty<ContextCandidate>();
        request.ActiveSkills = Array.Empty<SkillReference>();
        request.InitialTranscript = Array.Empty<NormalizedMessage>();
        request.LaneId = "caller-mutated";
        request.WorkloadClass = ProviderWorkloadClasses.Interactive;
        request.FinalOutputContract = null;

        var outcome = await coordinator.RunPreparedBatchAsync(prepared);

        Assert.Same(prepared.Manifest, outcome.Manifest);
        Assert.Equal(originalDigest, Assert.Single(prepared.RequestDigests));
        var received = Assert.Single(runtime.Received);
        Assert.Equal("npc-1", received.Run.AgentId);
        Assert.Equal(
            "original",
            received.Run.Extensions["caller.extension"]
                .GetProperty("value")
                .GetString());
        var receivedContext = Assert.Single(received.Context);
        Assert.Equal("observation-1", receivedContext.Id);
        Assert.Equal(
            "session-1",
            receivedContext.ObservationAdmissionMetadata!.SessionId);
        Assert.Equal(
            7,
            Assert.Single(
                    receivedContext.ObservationAdmissionMetadata.Bindings)
                .Entity.Incarnation);
        Assert.Equal(
            "skill-original",
            Assert.Single(received.ActiveSkills).SkillId);
        var receivedMessage = Assert.Single(received.InitialTranscript);
        Assert.Equal("message-original", receivedMessage.MessageId);
        Assert.Equal(
            "original",
            Assert.Single(receivedMessage.Parts).Text);
        Assert.Equal("lane-original", received.LaneId);
        Assert.Equal(
            ProviderWorkloadClasses.Background,
            received.WorkloadClass);
        Assert.Equal(
            "decision-output",
            received.FinalOutputContract!.SchemaId);
    }

    [Fact]
    public void PreparedRequestDigestBindsObservationAdmissionSemantics()
    {
        var baseline = PreparedObservationDigest(
            ObservationVisibilityScopes.Agent,
            "npc-1",
            "session-1",
            incarnation: 7);
        var digests = new HashSet<string>(StringComparer.Ordinal)
        {
            baseline,
            PreparedObservationDigest(
                ObservationVisibilityScopes.Private,
                "npc-1",
                "session-1",
                incarnation: 7),
            PreparedObservationDigest(
                ObservationVisibilityScopes.Agent,
                "audience-other",
                "session-1",
                incarnation: 7),
            PreparedObservationDigest(
                ObservationVisibilityScopes.Agent,
                "npc-1",
                "session-other",
                incarnation: 7),
            PreparedObservationDigest(
                ObservationVisibilityScopes.Agent,
                "npc-1",
                "session-1",
                incarnation: 8)
        };

        Assert.Equal(5, digests.Count);
        Assert.All(digests, digest => Assert.True(
            CanonicalJsonDigest.IsSha256(digest)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareBatchRejectsPreflightBeforeAnyRuntimeDispatch(
        bool aggregateBudgetFailure)
    {
        var runtime = new SelectiveResumeRuntime();
        var options = aggregateBudgetFailure
            ? null
            : new MultiActorCoordinatorOptions(
                maxSnapshotUtf8BytesPerRun: 4_096,
                maxBatchSnapshotUtf8Bytes: 8_192);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            options);
        var request = Request(1);
        MultiActorBatchBudget? budget = null;
        string expectedCode;
        if (aggregateBudgetFailure)
        {
            budget = new MultiActorBatchBudget(
                maxTokens: request.Run.Budget.MaxTokens - 1,
                maxActions: request.Run.Budget.MaxActions,
                maxDurationMs: request.Run.Budget.MaxDurationMs,
                maxCostUsd: request.Run.Budget.MaxCostUsd);
            expectedCode = "multi_actor_batch_token_budget_exceeded";
        }
        else
        {
            request.Context = new[]
            {
                new ContextCandidate(
                    "oversized",
                    "state",
                    ProtocolJson.ParseElement(
                        $$"""{"text":"{{new string('x', 10_000)}}"}"""))
            };
            expectedCode = "json_bytes_exceeded";
        }

        var error = Assert.Throws<RuntimeContentLimitException>(
            () => coordinator.PrepareBatch(
                new MultiActorDecisionBatch(
                    "prepared-preflight",
                    Coordinate(),
                    new[] { request },
                    budget)));

        Assert.Equal(expectedCode, error.LimitCode);
        Assert.Empty(runtime.Started);
        Assert.Empty(runtime.ResumeAttempts);
    }

    [Fact]
    public void PrepareBatchRejectsOversizedLaneBeforeLifecycleStarts()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new SelectiveResumeRuntime();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var request = Request(1);
        request.LaneId = new string('x', 257);

        _ = Assert.Throws<RuntimeContentLimitException>(
            () => coordinator.PrepareBatch(
                new MultiActorDecisionBatch(
                    "oversized-lane",
                    Coordinate(),
                    new[] { request })));

        Assert.False(lifecycle.Started);
        Assert.Empty(runtime.Started);
        Assert.Empty(runtime.ResumeAttempts);
    }

    [Fact]
    public async Task PreparedResumeRejectsOversizedContinuationLaneBeforeStart()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new SelectiveResumeRuntime();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "oversized-continuation-lane",
                Coordinate(),
                new[] { Request(1) }));
        var continuations = new Dictionary<
            string,
            DurableRunContinuation>(StringComparer.Ordinal)
        {
            ["run-1"] = new DurableRunContinuation
            {
                LaneId = new string('x', 257)
            }
        };

        await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => coordinator.ResumeOrStartPreparedBatchAsync(
                    prepared,
                    continuations)
                .AsTask());

        Assert.False(lifecycle.Started);
        Assert.Empty(runtime.Started);
        Assert.Empty(runtime.ResumeAttempts);
    }

    [Fact]
    public async Task PreparedResumeRequiresGuardedRuntimeBeforeLifecycle()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new RecordingRuntime(delayMs: 0);
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "guarded-resume-required",
                Coordinate(),
                new[] { Request(1) }));

        var error = await Assert.ThrowsAsync<DurableRunResumeGuardException>(
            () => coordinator.ResumeOrStartPreparedBatchAsync(prepared)
                .AsTask());

        Assert.Equal(
            DurableRunResumeGuardReasonCodes.NotSupported,
            error.ReasonCode);
        Assert.False(lifecycle.Started);
        Assert.Null(lifecycle.AbortedBatchId);
        Assert.Empty(runtime.Received);
        Assert.Equal(0, coordinator.ActiveBatchOperationCount);
        Assert.Equal(0, coordinator.QueuedParticipantCount);
    }

    [Fact]
    public async Task InitialRunRejectsChangedDurableParticipantIdentity()
    {
        var coordinator = new MultiActorDecisionCoordinator(
            new ChangedIdentityRuntime());

        var outcome = await coordinator.RunAsync(
            new MultiActorDecisionBatch(
                "changed-initial-identity",
                Coordinate(),
                new[] { Request(1) }));

        var result = Assert.Single(outcome.Results);
        Assert.Null(result.Outcome);
        _ = Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Fact]
    public async Task MissingRunRejectsChangedDurableParticipantIdentity()
    {
        var coordinator = new MultiActorDecisionCoordinator(
            new ChangedIdentityRuntime());
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "changed-missing-identity",
                Coordinate(),
                new[] { Request(1) }));

        var outcome = await coordinator.ResumeOrStartPreparedBatchAsync(
            prepared);

        var result = Assert.Single(outcome.Results);
        Assert.Null(result.Outcome);
        _ = Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Fact]
    public async Task ResumePreparedBatchStartsOnlyMissingRunsWithExactMetadata()
    {
        var runtime = new SelectiveResumeRuntime();
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var requests = Enumerable.Range(1, 3)
            .Select(Request)
            .ToArray();
        requests[1].Context = new[]
        {
            ObservationContext(
                scope: ObservationVisibilityScopes.Private,
                audienceId: "npc-2",
                sessionId: "session-admission",
                incarnation: 22)
        };
        requests[1].InitialTranscript = new[]
        {
            new NormalizedMessage
            {
                MessageId = "missing-run-message",
                Role = NormalizedRoles.User,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText("decide")
                }
            }
        };
        requests[1].LaneId = "world-evolution";
        requests[1].WorkloadClass =
            ProviderWorkloadClasses.Background;
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "resume-prepared",
                Coordinate(),
                requests));
        runtime.Seed(prepared.Requests[0].Run);
        runtime.Seed(prepared.Requests[2].Run);

        requests[1].Run.AgentId = "caller-mutated";
        requests[1].Context = Array.Empty<ContextCandidate>();
        requests[1].InitialTranscript =
            Array.Empty<NormalizedMessage>();
        requests[1].LaneId = "caller-mutated";
        requests[1].WorkloadClass =
            ProviderWorkloadClasses.Interactive;

        var outcome = await coordinator.ResumeOrStartPreparedBatchAsync(
            prepared);

        Assert.Equal(
            new[] { 0, 1, 2 },
            outcome.Results.Select(item => item.InputIndex));
        Assert.All(outcome.Results, item => Assert.True(item.Succeeded));
        Assert.Equal(
            new[] { "run-1", "run-2", "run-3" },
            runtime.ResumeAttempts.OrderBy(
                value => value,
                StringComparer.Ordinal));
        var started = Assert.Single(runtime.Started);
        Assert.Equal("run-2", started.Run.RunId);
        Assert.Equal("npc-2", started.Run.AgentId);
        Assert.Equal("resume-prepared", started.Run.BatchId);
        Assert.Equal(
            1,
            started.Run.Extensions[
                "gameAgent.multiActorInputIndex"].GetInt32());
        Assert.True(
            GameContextEnvelope.TryRead(
                started.Run,
                out var coordinate));
        Assert.Equal("world-v12", coordinate!.StateVersion);
        Assert.Null(coordinate.Observer);
        var context = Assert.Single(started.Context);
        Assert.Equal("observation-1", context.Id);
        Assert.Equal(
            ObservationVisibilityScopes.Private,
            context.ObservationAdmissionMetadata!.Scope);
        Assert.Equal(
            "session-admission",
            context.ObservationAdmissionMetadata.SessionId);
        Assert.Equal(
            22,
            Assert.Single(
                    context.ObservationAdmissionMetadata.Bindings)
                .Entity.Incarnation);
        Assert.Equal(
            "missing-run-message",
            Assert.Single(started.InitialTranscript).MessageId);
        Assert.Equal("world-evolution", started.LaneId);
        Assert.Equal(
            ProviderWorkloadClasses.Background,
            started.WorkloadClass);
        Assert.Equal(
            new[] { 0, 2 },
            runtime.ResumeGuards.Values
                .Select(item => item.ExpectedInt32ExtensionValue!.Value)
                .OrderBy(value => value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MissingPreparedRunIsNeverStartedWithoutPermission(
        bool startMissing,
        bool requestCancellation)
    {
        var runtime = new SelectiveResumeRuntime();
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "missing-not-started",
                Coordinate(),
                new[] { Request(1) }));
        var continuations = new Dictionary<
            string,
            DurableRunContinuation>(StringComparer.Ordinal)
        {
            ["run-1"] = new DurableRunContinuation
            {
                RequestCancellation = requestCancellation
            }
        };

        var outcome = await coordinator.ResumeOrStartPreparedBatchAsync(
            prepared,
            continuations,
            startMissing: startMissing);

        var result = Assert.Single(outcome.Results);
        Assert.Null(result.Outcome);
        var error = Assert.IsType<
            MultiActorParticipantNotStartedException>(result.Error);
        Assert.Equal("run-1", error.RunId);
        Assert.Empty(runtime.Started);
        Assert.Equal(new[] { "run-1" }, runtime.ResumeAttempts);
    }

    [Fact]
    public async Task UnknownPreparedResumeErrorKeepsOutcomeUnresolved()
    {
        var runtime = new SelectiveResumeRuntime(
            resumeError: new InvalidOperationException(
                "unknown durable store failure"));
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "unknown-resume",
                Coordinate(),
                new[] { Request(1) }));

        var outcome = await coordinator.ResumeOrStartPreparedBatchAsync(
            prepared);

        var result = Assert.Single(outcome.Results);
        Assert.Null(result.Outcome);
        var error = Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal("unknown durable store failure", error.Message);
        Assert.Empty(runtime.Started);
        Assert.Equal(new[] { "run-1" }, runtime.ResumeAttempts);
    }

    [Fact]
    public async Task ResumeOrStartDoesNotStartForUnrelatedKeyNotFound()
    {
        var runtime = new SelectiveResumeRuntime(
            resumeError: new KeyNotFoundException("missing active skill"));
        var coordinator = new MultiActorDecisionCoordinator(runtime);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "unrelated-key-not-found",
                Coordinate(),
                new[] { Request(1) }));

        var outcome = await coordinator.ResumeOrStartPreparedBatchAsync(
            prepared);

        var result = Assert.Single(outcome.Results);
        Assert.Null(result.Outcome);
        var error = Assert.IsType<KeyNotFoundException>(result.Error);
        Assert.Equal("missing active skill", error.Message);
        Assert.Empty(runtime.Started);
        Assert.Equal(new[] { "run-1" }, runtime.ResumeAttempts);
    }

    [Fact]
    public async Task PreparedResumeAbortsLifecycleWhenOutcomeIsUncertain()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new SelectiveResumeRuntime(
            resumeError: new InvalidOperationException(
                "unknown durable store failure"));
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "uncertain-resume-lifecycle",
                Coordinate(),
                new[] { Request(1) }));

        var error = await Assert.ThrowsAsync<
            MultiActorBatchExecutionUncertainException>(
            () => coordinator.ResumeOrStartPreparedBatchAsync(
                    prepared)
                .AsTask());

        Assert.Equal("uncertain-resume-lifecycle", error.BatchId);
        Assert.True(lifecycle.Started);
        Assert.Equal(
            "uncertain-resume-lifecycle",
            lifecycle.AbortedBatchId);
        Assert.Equal(
            "batch_execution_failed",
            lifecycle.AbortReasonCode);
        Assert.Empty(runtime.Started);
    }

    [Fact]
    public async Task LatePreparedResumeFinishIsNotRacedByAbort()
    {
        var lifecycle = new LateFinishLifecycle();
        var runtime = new SelectiveResumeRuntime();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                lifecycleSettlementTimeout:
                    TimeSpan.FromMilliseconds(20),
                maxDetachedLifecycleNotifications: 1),
            lifecycle);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "late-resume-finish",
                Coordinate(),
                new[] { Request(1) }));

        var error = await Assert.ThrowsAsync<
            MultiActorBatchAbortUncertainException>(
            () => coordinator.ResumeOrStartPreparedBatchAsync(prepared)
                .AsTask());

        Assert.Equal("actor_finish_uncertain", error.ReasonCode);
        Assert.Equal(0, lifecycle.AbortCalls);
        lifecycle.Release();
        await lifecycle.Landed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, lifecycle.AbortCalls);
    }

    [Fact]
    public async Task PreparedResumeRejectsLyingContinuationEnumeration()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new SelectiveResumeRuntime();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "bounded-continuations",
                Coordinate(),
                new[] { Request(1) }));
        var continuations = new MismatchedReadOnlyDictionary<
            string,
            DurableRunContinuation>(
            reportedCount: 0,
            new KeyValuePair<string, DurableRunContinuation>(
                "run-1",
                new DurableRunContinuation()),
            new KeyValuePair<string, DurableRunContinuation>(
                "run-extra",
                new DurableRunContinuation()));

        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.ResumeOrStartPreparedBatchAsync(
                    prepared,
                    continuations)
                .AsTask());

        Assert.False(lifecycle.Started);
        Assert.Empty(runtime.Started);
        Assert.Empty(runtime.ResumeAttempts);
    }

    [Fact]
    public async Task PreparedResumeEnforcesAggregateContinuationBytes()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new SelectiveResumeRuntime();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            new MultiActorCoordinatorOptions(
                maxBatchSize: 2,
                maxSnapshotUtf8BytesPerRun: 16_384,
                maxBatchSnapshotUtf8Bytes: 20_000),
            lifecycle);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "bounded-continuation-bytes",
                Coordinate(),
                new[] { Request(1), Request(2) }));
        var continuations = prepared.Manifest.Participants.ToDictionary(
            participant => participant.RunId,
            participant => new DurableRunContinuation
            {
                Context = new[]
                {
                    new ContextCandidate(
                        "large-" + participant.RunId,
                        "test",
                        ProtocolJson.ParseElement(
                            "{\"value\":\""
                            + new string('x', 10_000)
                            + "\"}"))
                }
            },
            StringComparer.Ordinal);

        var error = await Assert.ThrowsAsync<RuntimeContentLimitException>(
            () => coordinator.ResumeOrStartPreparedBatchAsync(
                    prepared,
                    continuations)
                .AsTask());

        Assert.Equal(
            "multi_actor_batch_snapshot_bytes_exceeded",
            error.LimitCode);
        Assert.False(lifecycle.Started);
        Assert.Empty(runtime.Started);
        Assert.Empty(runtime.ResumeAttempts);
    }

    [Fact]
    public async Task PreparedResumeRejectsLyingExpectationEnumeration()
    {
        var lifecycle = new RecordingLifecycle();
        var runtime = new SelectiveResumeRuntime();
        var coordinator = new MultiActorDecisionCoordinator(
            runtime,
            lifecycle: lifecycle);
        var prepared = coordinator.PrepareBatch(
            new MultiActorDecisionBatch(
                "bounded-expectations",
                Coordinate(),
                new[] { Request(1) }));
        var expectation = new DurableRunSemanticExpectation(
            "game.coordinate",
            new string('a', 64));
        var expectations = new MismatchedReadOnlyDictionary<
            string,
            DurableRunSemanticExpectation>(
            reportedCount: 0,
            new KeyValuePair<
                string,
                DurableRunSemanticExpectation>(
                "run-1",
                expectation),
            new KeyValuePair<
                string,
                DurableRunSemanticExpectation>(
                "run-extra",
                expectation));

        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.ResumeOrStartPreparedBatchAsync(
                    prepared,
                    semanticExpectations: expectations)
                .AsTask());

        Assert.False(lifecycle.Started);
        Assert.Empty(runtime.Started);
        Assert.Empty(runtime.ResumeAttempts);
    }

    private static string PreparedObservationDigest(
        string scope,
        string audienceId,
        string? sessionId,
        long incarnation)
    {
        var coordinator = new MultiActorDecisionCoordinator(
            new RecordingRuntime(delayMs: 0));
        var request = Request(1, "session-1");
        request.Context = new[]
        {
            ObservationContext(
                scope,
                audienceId,
                sessionId,
                incarnation)
        };
        return Assert.Single(
            coordinator.PrepareBatch(
                    new MultiActorDecisionBatch(
                        "admission-digest",
                        Coordinate("session-1"),
                        new[] { request }))
                .RequestDigests);
    }

    private static ContextCandidate ObservationContext(
        string scope,
        string audienceId,
        string? sessionId,
        long incarnation)
    {
        return new ContextCandidate(
            id: "observation-1",
            category: "state",
            content: ProtocolJson.ParseElement(
                """{"value":"stable"}"""),
            resource: null,
            priority: 3,
            required: true,
            canDefer: false,
            estimatedTokens: 5,
            expiresAt: DateTimeOffset.Parse(
                "2026-12-31T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            provenance: "host:trusted",
            observationAdmission: new ObservationAdmissionSnapshot(
                "observation-1",
                "world",
                sessionId,
                scope,
                new[] { audienceId },
                AudienceIncarnationBindingState.Valid,
                new[]
                {
                    new ObservationAudienceIncarnationBinding(
                        audienceId,
                        new GameEntityIdentity(
                            "npc-1",
                            incarnation))
                }));
    }

    private static DurableRunRequest Request(int index)
    {
        return Request(index, sessionId: null);
    }

    private static DurableRunRequest Request(
        int index,
        string? sessionId)
    {
        return new DurableRunRequest
        {
            Run = new AgentRun
            {
                RunId = $"run-{index}",
                AgentId = $"npc-{index}",
                WorldId = "world",
                SessionId = sessionId,
                DecisionKey = $"decision-{index}",
                State = RunStates.Queued
            }
        };
    }

    private static GameContextCoordinate Coordinate(
        string? sessionId = null)
    {
        return new GameContextCoordinate(
            "world",
            "prime",
            saveRevision: 12,
            stateVersion: "world-v12",
            gameTime: new GameTimePoint(
                "simulation",
                "prime",
                epoch: 1,
                tick: 12),
            sessionId: sessionId);
    }

    private static void AssertResumeGuard(
        DurableRunResumeGuard? guard,
        AgentRun run)
    {
        Assert.NotNull(guard);
        Assert.Equal(run.BatchId, guard.ExpectedBatchId);
        Assert.Equal(run.AgentId, guard.ExpectedAgentId);
        Assert.Equal(run.DecisionKey, guard.ExpectedDecisionKey);
        Assert.Equal(
            "gameAgent.multiActorInputIndex",
            guard.RequiredInt32ExtensionName);
        Assert.Equal(0, guard.MinimumInt32ExtensionValue);
        Assert.Equal(16_383, guard.MaximumInt32ExtensionValue);
        var extensionName = Assert.IsType<string>(
            guard.RequiredInt32ExtensionName);
        var expectedIndex = run.Extensions[
            extensionName].GetInt32();
        Assert.Equal(expectedIndex, guard.ExpectedInt32ExtensionValue);
    }

    private sealed class SelectiveResumeRuntime
        : IGuardedDurableAgentRuntime
    {
        private readonly ConcurrentDictionary<string, AgentRun> _runs =
            new(StringComparer.Ordinal);
        private readonly Exception? _resumeError;

        public SelectiveResumeRuntime(Exception? resumeError = null)
        {
            _resumeError = resumeError;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public ConcurrentBag<DurableRunRequest> Started { get; } = new();

        public ConcurrentBag<string> ResumeAttempts { get; } = new();

        public ConcurrentDictionary<string, DurableRunResumeGuard>
            ResumeGuards
        { get; } = new(StringComparer.Ordinal);

        public void Seed(AgentRun run)
        {
            var snapshot = ProtocolJson.DeserializeAgentRun(
                ProtocolJson.Serialize(run));
            snapshot.State = RunStates.WaitingForAction;
            Assert.True(_runs.TryAdd(snapshot.RunId, snapshot));
        }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Started.Add(request);
            request.Run.State = RunStates.Completed;
            Assert.True(_runs.TryAdd(request.Run.RunId, request.Run));
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome
                {
                    Run = request.Run,
                    FinalOutput = ProtocolJson.ParseElement(
                        """{"intent":"wait"}""")
                });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            return ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken,
                guard: null);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard? guard)
        {
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            ResumeAttempts.Add(runId);
            if (_resumeError is not null)
            {
                throw _resumeError;
            }

            if (!_runs.TryGetValue(runId, out var run))
            {
                throw new DurableRunNotFoundException(runId);
            }

            AssertResumeGuard(guard, run);
            ResumeGuards[runId] = guard!;
            run.State = continuation?.RequestCancellation == true
                ? RunStates.Cancelled
                : RunStates.Completed;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome
                {
                    Run = run,
                    FinalOutput = ProtocolJson.ParseElement(
                        """{"intent":"wait"}""")
                });
        }
    }

    private sealed class RecordingRuntime : IDurableAgentRuntime
    {
        private readonly int _delayMs;
        private readonly string? _failingAgentId;
        private readonly string? _failedOutcomeAgentId;
        private readonly Func<bool>? _admissionCheck;
        private int _active;
        private int _peak;

        public RecordingRuntime(
            int delayMs,
            string? failingAgentId = null,
            string? failedOutcomeAgentId = null,
            Func<bool>? admissionCheck = null)
        {
            _delayMs = delayMs;
            _failingAgentId = failingAgentId;
            _failedOutcomeAgentId = failedOutcomeAgentId;
            _admissionCheck = admissionCheck;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public ConcurrentBag<DurableRunRequest> Received { get; } = new();

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_admissionCheck is not null && !_admissionCheck())
            {
                throw new InvalidOperationException(
                    "Actor started before its batch manifest.");
            }

            Received.Add(request);
            var active = Interlocked.Increment(ref _active);
            UpdatePeak(active);
            try
            {
                if (_delayMs > 0)
                {
                    await Task.Delay(_delayMs, cancellationToken);
                }

                if (string.Equals(
                        request.Run.AgentId,
                        _failingAgentId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("expected");
                }

                if (string.Equals(
                        request.Run.AgentId,
                        _failedOutcomeAgentId,
                        StringComparison.Ordinal))
                {
                    request.Run.State = RunStates.Failed;
                    return new DurableRunOutcome
                    {
                        Run = request.Run,
                        ErrorCode = "provider_failed",
                        ErrorCategory = "provider"
                    };
                }

                request.Run.State = RunStates.Completed;
                return new DurableRunOutcome
                {
                    Run = request.Run,
                    FinalOutput = ProtocolJson.ParseElement(
                        """{"intent":"wait"}""")
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        private void UpdatePeak(int observed)
        {
            while (true)
            {
                var current = Volatile.Read(ref _peak);
                if (observed <= current
                    || Interlocked.CompareExchange(
                        ref _peak,
                        observed,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ReleaseGatedRuntime : IDurableAgentRuntime
    {
        private readonly TaskCompletionSource _firstEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _peak;

        public RuntimeControlPlane Controls { get; } = new();

        public Task FirstEntered => _firstEntered.Task;

        public int Active => Volatile.Read(ref _active);

        public int Peak => Volatile.Read(ref _peak);

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdatePeak(active);
            _firstEntered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                request.Run.State = RunStates.Completed;
                return new DurableRunOutcome
                {
                    Run = request.Run,
                    FinalOutput = ProtocolJson.ParseElement(
                        """{"intent":"wait"}""")
                };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        private void UpdatePeak(int observed)
        {
            while (true)
            {
                var current = Volatile.Read(ref _peak);
                if (observed <= current
                    || Interlocked.CompareExchange(
                        ref _peak,
                        observed,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ChangedIdentityRuntime : IGuardedDurableAgentRuntime
    {
        public RuntimeControlPlane Controls { get; } = new();

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Run.AgentId = "different-agent";
            request.Run.State = RunStates.Completed;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = request.Run });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            return ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken,
                guard: null);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard? guard)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DurableRunNotFoundException(runId);
        }
    }

    private sealed class PausingRuntime : IGuardedDurableAgentRuntime
    {
        private AgentRun? _run;

        public RuntimeControlPlane Controls { get; } = new();

        public DurableRunResumeGuard? LastResumeGuard { get; private set; }

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Run.State = RunStates.WaitingForAction;
            _run = request.Run;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = request.Run });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            return ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken,
                guard: null);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard? guard)
        {
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_run!.RunId, runId);
            AssertResumeGuard(guard, _run);
            LastResumeGuard = guard;
            _run.State = continuation?.RequestCancellation == true
                ? RunStates.Cancelled
                : RunStates.Completed;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = _run });
        }
    }

    private sealed class BlockingPausingRuntime
        : IGuardedDurableAgentRuntime
    {
        private readonly ConcurrentDictionary<string, AgentRun> _runs =
            new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _resumeEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeControlPlane Controls { get; } = new();

        public Task ResumeEntered => _resumeEntered.Task;

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Run.State = RunStates.WaitingForAction;
            Assert.True(_runs.TryAdd(request.Run.RunId, request.Run));
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = request.Run });
        }

        public async ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            return await ResumeAsync(
                    runId,
                    continuation,
                    reconciler,
                    cancellationToken,
                    guard: null)
                .ConfigureAwait(false);
        }

        public async ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard? guard)
        {
            _ = reconciler;
            var run = _runs[runId];
            AssertResumeGuard(guard, run);
            _resumeEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            run.State = continuation?.RequestCancellation == true
                ? RunStates.Cancelled
                : RunStates.Completed;
            return new DurableRunOutcome { Run = run };
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class FailingOncePausingRuntime
        : IGuardedDurableAgentRuntime
    {
        private AgentRun? _run;
        private int _remainingFailures = 1;

        public RuntimeControlPlane Controls { get; } = new();

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Run.State = RunStates.WaitingForAction;
            _run = request.Run;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = request.Run });
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            return ResumeAsync(
                runId,
                continuation,
                reconciler,
                cancellationToken,
                guard: null);
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation,
            IGameOperationReconciler? reconciler,
            CancellationToken cancellationToken,
            DurableRunResumeGuard? guard)
        {
            _ = continuation;
            _ = reconciler;
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_run!.RunId, runId);
            AssertResumeGuard(guard, _run);
            if (Interlocked.Exchange(ref _remainingFailures, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Simulated transient resume failure.");
            }

            _run.State = RunStates.Completed;
            return new ValueTask<DurableRunOutcome>(
                new DurableRunOutcome { Run = _run });
        }
    }

    private sealed class AbortCapacityRuntime : IDurableAgentRuntime
    {
        private readonly BoundedCallbackExecutionDispatcher
            _blockerDispatcher;
        private readonly ManualResetEventSlim _releaseBlocker = new();
        private readonly TaskCompletionSource<bool> _firstEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<int>? _blocker;
        private int _calls;

        public AbortCapacityRuntime(
            BoundedCallbackExecutionDispatcher blockerDispatcher)
        {
            _blockerDispatcher = blockerDispatcher;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public Task FirstEntered => _firstEntered.Task;

        public Task SecondEntered => _secondEntered.Task;

        public Task? StartedBlocker => _blocker;

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                var blockerEntered = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_blockerDispatcher.TryExecute(
                        () =>
                        {
                            blockerEntered.TrySetResult(true);
                            _releaseBlocker.Wait();
                            return new ValueTask<int>(1);
                        },
                        out _blocker))
                {
                    throw new InvalidOperationException(
                        "The capacity blocker was not admitted.");
                }

                await blockerEntered.Task.ConfigureAwait(false);
                _firstEntered.TrySetResult(true);
            }
            else
            {
                _secondEntered.TrySetResult(true);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask<DurableRunOutcome> ResumeAsync(
            string runId,
            DurableRunContinuation? continuation = null,
            IGameOperationReconciler? reconciler = null,
            CancellationToken cancellationToken = default)
        {
            _ = runId;
            _ = continuation;
            _ = reconciler;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public void ReleaseBlocker()
        {
            _releaseBlocker.Set();
        }
    }

    private sealed class RecordingLifecycle
        : IMultiActorDecisionLifecycle
    {
        private int _started;

        public bool Started => Volatile.Read(ref _started) != 0;

        public int StartedCount => Volatile.Read(ref _started);

        public MultiActorBatchManifest? Manifest { get; private set; }

        public ConcurrentBag<string> FinishedAgentIds { get; } = new();

        public string? AbortedBatchId { get; private set; }

        public string? AbortReasonCode { get; private set; }

        public ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Manifest = manifest;
            Interlocked.Increment(ref _started);
            return default;
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Manifest!.BatchId, batchId);
            FinishedAgentIds.Add(result.AgentId);
            return default;
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AbortedBatchId = batchId;
            AbortReasonCode = reasonCode;
            return default;
        }
    }

    private sealed class CountingBatchStartLifecycle
        : IMultiActorDecisionLifecycle
    {
        private readonly int _expectedStarts;
        private readonly TaskCompletionSource _allStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _starts;

        public CountingBatchStartLifecycle(int expectedStarts)
        {
            _expectedStarts = expectedStarts;
        }

        public Task AllStarted => _allStarted.Task;

        public ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            _ = manifest;
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _starts) == _expectedStarts)
            {
                _allStarted.TrySetResult();
            }

            return default;
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = result;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = reasonCode;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }
    }

    private sealed class LateStartLifecycle
        : IMultiActorDecisionLifecycle
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _landed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _abortCalls;

        public Task Landed => _landed.Task;

        public int AbortCalls => Volatile.Read(ref _abortCalls);

        public async ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            _ = manifest;
            _ = cancellationToken;
            await _release.Task;
            _landed.TrySetResult();
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = result;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = reasonCode;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _abortCalls);
            return default;
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class LateFinishLifecycle
        : IMultiActorDecisionLifecycle
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _landed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _abortCalls;

        public Task Landed => _landed.Task;

        public int AbortCalls => Volatile.Read(ref _abortCalls);

        public ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            _ = manifest;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public async ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = result;
            _ = cancellationToken;
            await _release.Task;
            _landed.TrySetResult();
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = reasonCode;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _abortCalls);
            return default;
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class FailingFinishLifecycle
        : IMultiActorDecisionLifecycle
    {
        private int _finishAttempts;
        private int _successfulFinishes;

        public int FinishAttempts => Volatile.Read(ref _finishAttempts);

        public int SuccessfulFinishes =>
            Volatile.Read(ref _successfulFinishes);

        public string? AbortedBatchId { get; private set; }

        public string? AbortReasonCode { get; private set; }

        public ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            _ = manifest;
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = result;
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _finishAttempts) == 1)
            {
                throw new InvalidOperationException(
                    "Simulated finish notification failure.");
            }

            Interlocked.Increment(ref _successfulFinishes);
            return default;
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AbortedBatchId = batchId;
            AbortReasonCode = reasonCode;
            return default;
        }
    }

    private sealed class BlockingStartLifecycle
        : IMultiActorDecisionLifecycle
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            return default;
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            return default;
        }

        public void Release()
        {
            _release.TrySetResult(true);
        }
    }

    private sealed class FailingStartLifecycle
        : IMultiActorDecisionLifecycle
    {
        public string? AbortedBatchId { get; private set; }

        public string? AbortReasonCode { get; private set; }

        public ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            _ = manifest;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("expected");
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AbortedBatchId = batchId;
            AbortReasonCode = reasonCode;
            return default;
        }
    }

    private sealed class BlockingAbortLifecycle
        : IMultiActorDecisionLifecycle
    {
        private readonly TaskCompletionSource _abortEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AbortEntered => _abortEntered.Task;

        public ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            _ = manifest;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("expected");
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = reasonCode;
            _ = cancellationToken;
            _abortEntered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
            return default;
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class SynchronouslyBlockingLifecycle
        : IMultiActorDecisionLifecycle
    {
        private readonly bool _blockStart;
        private readonly ManualResetEventSlim _release = new();

        public SynchronouslyBlockingLifecycle(bool blockStart)
        {
            _blockStart = blockStart;
        }

        public ValueTask BatchStartedAsync(
            MultiActorBatchManifest manifest,
            CancellationToken cancellationToken)
        {
            _ = manifest;
            _ = cancellationToken;
            if (_blockStart)
            {
                _release.Wait();
            }

            return default;
        }

        public ValueTask ActorFinishedAsync(
            string batchId,
            MultiActorRunResult result,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = result;
            _ = cancellationToken;
            if (!_blockStart)
            {
                _release.Wait();
            }

            return default;
        }

        public ValueTask BatchAbortedAsync(
            string batchId,
            string reasonCode,
            CancellationToken cancellationToken)
        {
            _ = batchId;
            _ = reasonCode;
            _ = cancellationToken;
            return default;
        }

        public void Release()
        {
            _release.Set();
        }
    }

    private sealed class IndexedOnlyReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly T[] _items;

        public IndexedOnlyReadOnlyList(params T[] items)
        {
            _items = items;
        }

        public int CountReads { get; private set; }

        public bool EnumeratorAccessed { get; private set; }

        public int Count
        {
            get
            {
                CountReads++;
                return _items.Length;
            }
        }

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator()
        {
            EnumeratorAccessed = true;
            throw new InvalidOperationException(
                "Enumeration is not supported.");
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class MismatchedReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly T[] _items;
        private readonly int _reportedCount;

        public MismatchedReadOnlyList(
            int reportedCount,
            params T[] items)
        {
            _reportedCount = reportedCount;
            _items = items;
        }

        public int Count => _reportedCount;

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator()
        {
            throw new InvalidOperationException(
                "Enumeration is not supported.");
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class MismatchedReadOnlyDictionary<TKey, TValue>
        : IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly KeyValuePair<TKey, TValue>[] _items;
        private readonly int _reportedCount;

        public MismatchedReadOnlyDictionary(
            int reportedCount,
            params KeyValuePair<TKey, TValue>[] items)
        {
            _reportedCount = reportedCount;
            _items = items;
        }

        public int Count => _reportedCount;

        public IEnumerable<TKey> Keys =>
            _items.Select(item => item.Key);

        public IEnumerable<TValue> Values =>
            _items.Select(item => item.Value);

        public TValue this[TKey key] =>
            _items.Single(
                item => EqualityComparer<TKey>.Default.Equals(
                    item.Key,
                    key)).Value;

        public bool ContainsKey(TKey key)
        {
            return _items.Any(
                item => EqualityComparer<TKey>.Default.Equals(
                    item.Key,
                    key));
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            foreach (var item in _items)
            {
                if (EqualityComparer<TKey>.Default.Equals(
                        item.Key,
                        key))
                {
                    value = item.Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return ((IEnumerable<KeyValuePair<TKey, TValue>>)_items)
                .GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}

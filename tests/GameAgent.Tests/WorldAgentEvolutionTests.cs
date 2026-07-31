using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;
using GameAgent.Runtime;
using GameAgent.World;

namespace GameAgent.Tests;

public sealed partial class WorldAgentEvolutionTests
{
    private const string CatalogDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string PolicyDigest =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task ConcurrentCompletionOrderDoesNotChangeSettlement()
    {
        var first = await FixtureAsync();
        var second = await FixtureAsync();
        var firstRuntime = new RecordingRuntime(
            new Dictionary<string, int>
            {
                ["npc-a-agent"] = 40,
                ["npc-b-agent"] = 1
            });
        var secondRuntime = new RecordingRuntime(
            new Dictionary<string, int>
            {
                ["npc-a-agent"] = 1,
                ["npc-b-agent"] = 40
            });
        var firstRunner = Runner(
            firstRuntime,
            first.Store,
            new InMemoryWorldAgentEvolutionStore(),
            first);
        var secondRunner = Runner(
            secondRuntime,
            second.Store,
            new InMemoryWorldAgentEvolutionStore(),
            second);

        var firstResult = await firstRunner.ExecuteAsync(first.Command);
        var secondResult = await secondRunner.ExecuteAsync(second.Command);
        var firstState = await first.Store.ReadAsync(
            first.Coordinate.Address,
            default);
        var secondState = await second.Store.ReadAsync(
            second.Coordinate.Address,
            default);

        Assert.Equal(WorldAgentEvolutionStatus.Completed, firstResult.Status);
        Assert.Equal(WorldAgentEvolutionStatus.Completed, secondResult.Status);
        Assert.Equal("npc-a", Winner(firstState!));
        Assert.Equal("npc-a", Winner(secondState!));
        Assert.Equal(firstState!.StateDigest, secondState!.StateDigest);
        Assert.Equal(2, firstRuntime.RunCalls);
        Assert.Equal(2, secondRuntime.RunCalls);
        Assert.All(
            firstRuntime.ExecutionPolicies,
            identity => Assert.True(
                identity.Matches(
                    new DurableExecutionPolicyIdentity(
                        first.Command.RuntimeGeneration
                            .ToolCatalogDigest,
                        first.Command.RuntimeGeneration
                            .SkillCatalogDigest,
                        first.Command.RuntimeGeneration
                            .ProviderPolicyDigest,
                        first.Command.RuntimeGeneration
                            .ModelPolicyDigest))));

        var replay = await firstRunner.ExecuteAsync(first.Command);

        Assert.Equal(WorldAgentEvolutionStatus.Replayed, replay.Status);
        Assert.NotNull(replay.Receipt);
        Assert.Equal(2, firstRuntime.RunCalls);
    }

    [Fact]
    public async Task CrashAfterActorRunsResumesWithoutRedispatch()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var durable = new InMemoryWorldAgentEvolutionStore();
        var faulting = new ThrowOnWriteStore(durable, throwOnWrite: 3);
        var now = DateTimeOffset.Parse(
            "2030-01-01T00:00:00Z",
            CultureInfo.InvariantCulture);
        var firstRunner = Runner(
            runtime,
            fixture.Store,
            faulting,
            fixture,
            () => now,
            TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InjectedCrashException>(
            async () => await firstRunner.ExecuteAsync(fixture.Command));
        Assert.Equal(2, runtime.RunCalls);
        now = now.AddSeconds(2);
        var resumed = Runner(
            runtime,
            fixture.Store,
            durable,
            fixture,
            () => now,
            TimeSpan.FromSeconds(1));

        var result = await resumed.ResumeAsync(fixture.Command);
        var state = await fixture.Store.ReadAsync(
            fixture.Coordinate.Address,
            default);

        Assert.Equal(WorldAgentEvolutionStatus.Completed, result.Status);
        Assert.Equal("npc-a", Winner(state!));
        Assert.Equal(2, runtime.RunCalls);
        Assert.Equal(2, runtime.ResumeCalls);
    }

    [Fact]
    public async Task InitialDuplicateCheckpointContinuesAsItsOwner()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var store = new DuplicateAfterInitialWriteStore(
            new InMemoryWorldAgentEvolutionStore());
        var runner = Runner(runtime, fixture.Store, store, fixture);

        var result = await runner.ExecuteAsync(fixture.Command);

        Assert.Equal(WorldAgentEvolutionStatus.Completed, result.Status);
        Assert.Equal(2, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task SameCommandIdWithDifferentDigestReturnsConflict()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var store = new InMemoryWorldAgentEvolutionStore();
        var runner = Runner(runtime, fixture.Store, store, fixture);
        var completed = await runner.ExecuteAsync(fixture.Command);
        Assert.Equal(WorldAgentEvolutionStatus.Completed, completed.Status);
        var conflicting = new WorldAgentEvolutionCommand(
            fixture.Command.CommandId,
            "different-operation",
            fixture.Command.BatchId,
            fixture.Command.ExpectedCoordinate,
            fixture.Command.Participants,
            fixture.Command.ReducerPolicyId,
            fixture.Command.ReducerPolicyDigest,
            fixture.Command.RuntimeGeneration,
            fixture.Command.AggregateBudget);
        var runCalls = runtime.RunCalls;

        var result = await runner.ExecuteAsync(conflicting);

        Assert.Equal(WorldAgentEvolutionStatus.Rejected, result.Status);
        Assert.Equal(
            "world_evolution_idempotency_conflict",
            result.ReasonCode);
        Assert.Equal(runCalls, runtime.RunCalls);
    }

    [Fact]
    public async Task TerminalReplayRejectsReceiptFromAnotherEvolution()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var evolution = new InMemoryWorldAgentEvolutionStore();
        var runner = Runner(runtime, fixture.Store, evolution, fixture);
        var completed = await runner.ExecuteAsync(fixture.Command);
        Assert.Equal(WorldAgentEvolutionStatus.Completed, completed.Status);
        var foreignRequest = new WorldTransactionRequest(
            "foreign-operation",
            "foreign-command",
            new string('8', 64),
            fixture.Coordinate);
        var foreignReceipt = new WorldCommandReceipt(
            foreignRequest,
            WorldCommandReceiptStatus.Rejected,
            "foreign_rejected",
            resultingCoordinate: null,
            resultingStateDigest: null,
            effect: null,
            eventInstanceId: null);
        var forgedWorld = new ReconcileOverrideWorldStore(
            fixture.Store,
            foreignReceipt);
        var replayRunner = Runner(
            runtime,
            forgedWorld,
            evolution,
            fixture);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await replayRunner.ExecuteAsync(fixture.Command));
    }

    [Fact]
    public async Task ResumeDoesNotRestartSettledActorWhenRunIsMissing()
    {
        var fixture = await FixtureAsync();
        var runtime = new PartiallySettlingRuntime();
        var durable = new InMemoryWorldAgentEvolutionStore();
        var firstRunner = Runner(
            runtime,
            fixture.Store,
            durable,
            fixture);

        var waiting = await firstRunner.ExecuteAsync(fixture.Command);
        Assert.Equal(WorldAgentEvolutionStatus.Waiting, waiting.Status);
        Assert.Equal(2, runtime.RunCalls);
        runtime.Forget("run-npc-a");
        var runCallsBeforeResume = runtime.RunCalls;
        var resumedRunner = Runner(
            runtime,
            fixture.Store,
            durable,
            fixture);

        var resumed = await resumedRunner.ResumeAsync(fixture.Command);

        Assert.Equal(WorldAgentEvolutionStatus.Completed, resumed.Status);
        Assert.Equal(runCallsBeforeResume, runtime.RunCalls);
        Assert.Contains("run-npc-a", runtime.ResumeAttempts);
        Assert.Contains("run-npc-b", runtime.ResumeAttempts);
        Assert.Equal(
            WorldAgentDecisionProposalStatus.Proposed,
            resumed.ActorResults[0].ProposalResult.Status);
        Assert.Equal(
            WorldAgentDecisionProposalStatus.Proposed,
            resumed.ActorResults[1].ProposalResult.Status);
    }

    [Fact]
    public async Task WorldChangeDuringActorBatchRejectsLateResults()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime(blockRuns: true);
        var runner = Runner(
            runtime,
            fixture.Store,
            new InMemoryWorldAgentEvolutionStore(),
            fixture);
        var pending = runner.ExecuteAsync(fixture.Command).AsTask();
        await runtime.AllRunsStarted;
        await CommitWinnerAsync(
            fixture,
            "external",
            "external-command",
            "external-operation");
        runtime.ReleaseRuns();

        var result = await pending;
        var state = await fixture.Store.ReadAsync(
            fixture.Coordinate.Address,
            default);

        Assert.Equal(WorldAgentEvolutionStatus.Rejected, result.Status);
        Assert.Equal(
            "world_evolution_coordinate_stale",
            result.ReasonCode);
        Assert.Equal("external", Winner(state!));
        Assert.Equal(1, state!.Coordinate.StateVersion);
    }

    [Fact]
    public async Task JournalStoreReopensAndEnforcesCompareExchange()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "evolution.journal");
        try
        {
            var payload = Json("""{"stage":1}""");
            var first = new WorldAgentEvolutionCheckpoint(
                "command",
                1,
                PolicyDigest,
                payload);
            await using (var journal = new FileSessionStore(path))
            {
                var store = new JournalWorldAgentEvolutionStore(journal);
                var written = await store.CompareExchangeAsync(first, 0);
                Assert.Equal(
                    WorldAgentEvolutionStoreWriteStatus.Written,
                    written.Status);
            }

            await using (var journal = new FileSessionStore(path))
            {
                var store = new JournalWorldAgentEvolutionStore(journal);
                var reopened = await store.ReadAsync("command");
                Assert.Equal(first.PayloadDigest, reopened!.PayloadDigest);
                var stale = new WorldAgentEvolutionCheckpoint(
                    "command",
                    1,
                    PolicyDigest,
                    Json("""{"stage":2}"""));
                var conflict = await store.CompareExchangeAsync(stale, 0);
                Assert.Equal(
                    WorldAgentEvolutionStoreWriteStatus.Conflict,
                    conflict.Status);
                Assert.Equal(first.PayloadDigest, conflict.Current.PayloadDigest);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JournalStoreRejectsLyingCheckpointEnumeration()
    {
        await using var journal = new LyingEvolutionJournal();
        var store = new JournalWorldAgentEvolutionStore(
            journal,
            maximumCheckpointEventsPerCommand: 2);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.ReadAsync("command"));

        Assert.Equal(3, journal.Enumerated);
    }

    [Fact]
    public async Task ActiveLeaseReturnsBusyWithoutDispatch()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime(blockRuns: true);
        var store = new InMemoryWorldAgentEvolutionStore();
        var runner = Runner(runtime, fixture.Store, store, fixture);
        var active = runner.ExecuteAsync(fixture.Command).AsTask();
        await runtime.AllRunsStarted;
        var competing = Runner(
            runtime,
            fixture.Store,
            store,
            fixture);

        var busy = await competing.ResumeAsync(fixture.Command);
        runtime.ReleaseRuns();
        var completed = await active;

        Assert.Equal(WorldAgentEvolutionStatus.Busy, busy.Status);
        Assert.Equal(WorldAgentEvolutionStatus.Completed, completed.Status);
        Assert.Equal(2, runtime.RunCalls);
    }

    [Fact]
    public async Task SlowActorBatchRenewsLeaseAndPreventsTakeover()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime(blockRuns: true);
        var store = new InMemoryWorldAgentEvolutionStore();
        var startedAt = DateTimeOffset.Parse(
            "2030-01-01T00:00:00Z",
            CultureInfo.InvariantCulture);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var runner = Runner(
            runtime,
            fixture.Store,
            store,
            fixture,
            () => startedAt.Add(elapsed.Elapsed),
            TimeSpan.FromSeconds(1));
        var active = runner.ExecuteAsync(fixture.Command).AsTask();
        await runtime.AllRunsStarted;
        await Task.Delay(1_200);

        WorldAgentEvolutionCheckpoint? checkpoint = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            checkpoint = await store.ReadAsync(
                fixture.Command.CommandId);
            if (checkpoint?.Revision >= 4)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.Revision >= 4);
        var competing = Runner(
            runtime,
            fixture.Store,
            store,
            fixture,
            () => startedAt.Add(elapsed.Elapsed),
            TimeSpan.FromSeconds(1));
        var busy = await competing.ResumeAsync(fixture.Command);
        runtime.ReleaseRuns();
        var completed = await active;

        Assert.Equal(WorldAgentEvolutionStatus.Busy, busy.Status);
        Assert.Equal(WorldAgentEvolutionStatus.Completed, completed.Status);
        Assert.Equal(2, runtime.RunCalls);
    }

    [Fact]
    public async Task ReducerFailureAfterHeartbeatUsesLatestRevision()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var store = new InMemoryWorldAgentEvolutionStore();
        var reducer = new BlockingThrowingReducer();
        var startedAt = DateTimeOffset.Parse(
            "2030-01-01T00:00:00Z",
            CultureInfo.InvariantCulture);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var runner = new WorldAgentEvolutionRunner(
            runtime,
            new FixedInputFactory(),
            fixture.Store,
            store,
            reducer,
            new FixedWorldAgentRuntimePolicySnapshotSource(
                fixture.Command.RuntimeGeneration),
            new WorldAgentEvolutionRunnerOptions(
                maxParticipants: 8,
                maxConcurrentActors: 2,
                ownerLeaseDuration: TimeSpan.FromSeconds(1)),
            () => startedAt.Add(elapsed.Elapsed));
        var active = runner.ExecuteAsync(fixture.Command).AsTask();
        await reducer.Started;
        await Task.Delay(1_200);
        reducer.Release();

        var result = await active;

        Assert.Equal(WorldAgentEvolutionStatus.Failed, result.Status);
        Assert.Equal(
            "world_evolution_reducer_failed",
            result.ReasonCode);
        Assert.NotEqual(
            "world_evolution_ownership_lost",
            result.ReasonCode);
        Assert.Equal(2, runtime.RunCalls);
    }

    [Fact]
    public async Task CancelBeforeDispatchPersistsWithoutStartingActors()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var store = new InMemoryWorldAgentEvolutionStore();
        var runner = Runner(runtime, fixture.Store, store, fixture);

        var cancelled = await runner.CancelAsync(
            fixture.Command,
            "cancelled_by_game");
        var replay = await runner.ExecuteAsync(fixture.Command);

        Assert.Equal(
            WorldAgentEvolutionStatus.Cancelled,
            cancelled.Status);
        Assert.Equal("cancelled_by_game", cancelled.ReasonCode);
        Assert.All(
            cancelled.ActorResults,
            item => Assert.Equal(
                WorldAgentDecisionProposalStatus.Cancelled,
                item.ProposalResult.Status));
        Assert.Equal(WorldAgentEvolutionStatus.Cancelled, replay.Status);
        Assert.Equal(0, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task CallerCancellationReleasesLatestOwnerImmediately()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime(blockRuns: true);
        var store = new InMemoryWorldAgentEvolutionStore();
        var runner = Runner(runtime, fixture.Store, store, fixture);
        using var cancellation = new CancellationTokenSource();
        var active = runner.ExecuteAsync(
                fixture.Command,
                cancellation.Token)
            .AsTask();
        await runtime.AllRunsStarted;

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => active);

        var takeover = await Runner(
                runtime,
                fixture.Store,
                store,
                fixture)
            .CancelAsync(
                fixture.Command,
                "cancelled_after_caller_exit");

        Assert.NotEqual(WorldAgentEvolutionStatus.Busy, takeover.Status);
        Assert.Equal(
            WorldAgentEvolutionStatus.Cancelled,
            takeover.Status);
    }

    [Fact]
    public async Task SettlementCannotChangeCapturedCommandIdentity()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var runner = new WorldAgentEvolutionRunner(
            runtime,
            new FixedInputFactory(),
            fixture.Store,
            new InMemoryWorldAgentEvolutionStore(),
            new InvalidReducer(fixture),
            new FixedWorldAgentRuntimePolicySnapshotSource(
                fixture.Command.RuntimeGeneration),
            new WorldAgentEvolutionRunnerOptions());

        var result = await runner.ExecuteAsync(fixture.Command);
        var replay = await runner.ExecuteAsync(fixture.Command);
        var state = await fixture.Store.ReadAsync(
            fixture.Coordinate.Address,
            default);

        Assert.Equal(WorldAgentEvolutionStatus.Rejected, result.Status);
        Assert.Equal(
            "world_evolution_settlement_invalid",
            result.ReasonCode);
        Assert.Equal(WorldAgentEvolutionStatus.Rejected, replay.Status);
        Assert.Equal(
            "world_evolution_settlement_invalid",
            replay.ReasonCode);
        Assert.Equal("", Winner(state!));
        Assert.Equal(0, state!.Coordinate.StateVersion);
    }

    [Fact]
    public async Task NewCommandRejectsRuntimePolicyMismatchBeforeDispatch()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var stalePolicy = new WorldAgentRuntimeGeneration(
            2,
            new string('c', 64),
            new string('d', 64),
            new string('e', 64),
            new string('f', 64));
        var runner = new WorldAgentEvolutionRunner(
            runtime,
            new FixedInputFactory(),
            fixture.Store,
            new InMemoryWorldAgentEvolutionStore(),
            new WinnerReducer(fixture),
            new FixedWorldAgentRuntimePolicySnapshotSource(stalePolicy));

        var result = await runner.ExecuteAsync(fixture.Command);

        Assert.Equal(WorldAgentEvolutionStatus.Rejected, result.Status);
        Assert.Equal(
            "world_evolution_runtime_policy_stale",
            result.ReasonCode);
        Assert.Equal(0, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task RuntimePolicyRacePausesWithoutDispatchAndResumesSameRuns()
    {
        var fixture = await FixtureAsync();
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-world-evolution-policy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tools = new ToolCatalogRegistry();
        tools.Replace(new[] { EvolutionTool("initial_tool") });
        var provider = new SelectionProvider();
        var host = new CountingRejectingHost();
        try
        {
            await using var built = new GameAgentRuntimeBuilder(host)
                .UseFileJournal(Path.Combine(directory, "runtime.journal"))
                .AddProvider(provider)
                .WithToolRegistry(tools)
                .EnableWorldAgentJobs()
                .Build();
            var expectedPolicy =
                WorldAgentRuntimeGeneration.FromExecutionPolicy(
                    fixture.Command.RuntimeGeneration.RuntimeGeneration,
                    built.Runtime.CaptureExecutionPolicyIdentity());
            var command = WithRuntimePolicy(
                fixture.Command,
                expectedPolicy,
                new MultiActorBatchBudget(
                    maxTokens: 200_000,
                    maxActions: 4,
                    maxDurationMs: 60_000,
                    maxCostUsd: "1"));
            var evolutionStore = new InMemoryWorldAgentEvolutionStore();
            var runner = new WorldAgentEvolutionRunner(
                built.Runtime,
                new FixedInputFactory(maxTokens: 100_000),
                fixture.Store,
                evolutionStore,
                new WinnerReducer(fixture),
                new FixedWorldAgentRuntimePolicySnapshotSource(
                    expectedPolicy));

            tools.Replace(new[] { EvolutionTool("reloaded_tool") });
            var paused = await runner.ExecuteAsync(command);

            Assert.Equal(
                WorldAgentEvolutionStatus.ReconciliationRequired,
                paused.Status);
            Assert.Equal(0, provider.CallCount);
            Assert.Equal(0, host.CallCount);
            foreach (var participant in command.Participants)
            {
                Assert.Empty(
                    await built.SessionStore.ReadRunAsync(
                        participant.Job.RunId,
                        default));
            }

            tools.Replace(new[] { EvolutionTool("initial_tool") });
            Assert.True(
                expectedPolicy.Matches(
                    WorldAgentRuntimeGeneration.FromExecutionPolicy(
                        expectedPolicy.RuntimeGeneration,
                        built.Runtime
                            .CaptureExecutionPolicyIdentity())));
            var resumed = await runner.ResumeAsync(command);
            var state = await fixture.Store.ReadAsync(
                fixture.Coordinate.Address,
                default);

            Assert.Equal(WorldAgentEvolutionStatus.Completed, resumed.Status);
            Assert.Equal(command.Participants.Count, provider.CallCount);
            Assert.Equal(0, host.CallCount);
            Assert.Equal("npc-a", Winner(state!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NewCommandRejectsReducerPolicyMismatchBeforeDispatch()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var runner = new WorldAgentEvolutionRunner(
            runtime,
            new FixedInputFactory(),
            fixture.Store,
            new InMemoryWorldAgentEvolutionStore(),
            new WrongPolicyReducer(),
            new FixedWorldAgentRuntimePolicySnapshotSource(
                fixture.Command.RuntimeGeneration));

        var result = await runner.ExecuteAsync(fixture.Command);

        Assert.Equal(WorldAgentEvolutionStatus.Rejected, result.Status);
        Assert.Equal(
            "world_evolution_reducer_policy_stale",
            result.ReasonCode);
        Assert.Equal(0, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task RecoveryDoesNotStartMissingRunsUnderStaleRuntimePolicy()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var durable = new InMemoryWorldAgentEvolutionStore();
        var now = DateTimeOffset.Parse(
            "2030-01-01T00:00:00Z",
            CultureInfo.InvariantCulture);
        var first = Runner(
            runtime,
            fixture.Store,
            new ThrowOnWriteStore(durable, throwOnWrite: 2),
            fixture,
            () => now,
            TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InjectedCrashException>(
            async () => await first.ExecuteAsync(fixture.Command));
        Assert.Equal(0, runtime.RunCalls);

        now = now.AddSeconds(2);
        var stalePolicy = new WorldAgentRuntimeGeneration(
            2,
            new string('c', 64),
            new string('d', 64),
            new string('e', 64),
            new string('f', 64));
        var resumed = new WorldAgentEvolutionRunner(
            runtime,
            new FixedInputFactory(),
            fixture.Store,
            durable,
            new WinnerReducer(fixture),
            new FixedWorldAgentRuntimePolicySnapshotSource(stalePolicy),
            new WorldAgentEvolutionRunnerOptions(
                maxParticipants: 8,
                maxConcurrentActors: 2,
                ownerLeaseDuration: TimeSpan.FromSeconds(1)),
            () => now);

        var result = await resumed.ResumeAsync(fixture.Command);

        Assert.Equal(
            WorldAgentEvolutionStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            "world_evolution_runtime_policy_stale",
            result.ReasonCode);
        Assert.Equal(0, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task RecoveryDoesNotStartMissingRunsUnderStaleReducerPolicy()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var durable = new InMemoryWorldAgentEvolutionStore();
        var now = DateTimeOffset.Parse(
            "2030-01-01T00:00:00Z",
            CultureInfo.InvariantCulture);
        var first = Runner(
            runtime,
            fixture.Store,
            new ThrowOnWriteStore(durable, throwOnWrite: 2),
            fixture,
            () => now,
            TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InjectedCrashException>(
            async () => await first.ExecuteAsync(fixture.Command));
        now = now.AddSeconds(2);
        var resumed = new WorldAgentEvolutionRunner(
            runtime,
            new FixedInputFactory(),
            fixture.Store,
            durable,
            new WrongPolicyReducer(),
            new FixedWorldAgentRuntimePolicySnapshotSource(
                fixture.Command.RuntimeGeneration),
            new WorldAgentEvolutionRunnerOptions(
                maxParticipants: 8,
                maxConcurrentActors: 2,
                ownerLeaseDuration: TimeSpan.FromSeconds(1)),
            () => now);

        var result = await resumed.ResumeAsync(fixture.Command);

        Assert.Equal(
            WorldAgentEvolutionStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            "world_evolution_reducer_policy_stale",
            result.ReasonCode);
        Assert.Equal(0, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task RecoveryRejectsChangedPreparedInputWithoutDispatch()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var durable = new InMemoryWorldAgentEvolutionStore();
        var input = new MutableInputFactory();
        var now = DateTimeOffset.Parse(
            "2030-01-01T00:00:00Z",
            CultureInfo.InvariantCulture);
        var first = new WorldAgentEvolutionRunner(
            runtime,
            input,
            fixture.Store,
            new ThrowOnWriteStore(durable, throwOnWrite: 2),
            new WinnerReducer(fixture),
            new FixedWorldAgentRuntimePolicySnapshotSource(
                fixture.Command.RuntimeGeneration),
            new WorldAgentEvolutionRunnerOptions(
                maxParticipants: 8,
                maxConcurrentActors: 2,
                ownerLeaseDuration: TimeSpan.FromSeconds(1)),
            () => now);
        await Assert.ThrowsAsync<InjectedCrashException>(
            async () => await first.ExecuteAsync(fixture.Command));
        input.Revision = 2;
        now = now.AddSeconds(2);
        var resumed = new WorldAgentEvolutionRunner(
            runtime,
            input,
            fixture.Store,
            durable,
            new WinnerReducer(fixture),
            new FixedWorldAgentRuntimePolicySnapshotSource(
                fixture.Command.RuntimeGeneration),
            new WorldAgentEvolutionRunnerOptions(
                maxParticipants: 8,
                maxConcurrentActors: 2,
                ownerLeaseDuration: TimeSpan.FromSeconds(1)),
            () => now);

        var result = await resumed.ResumeAsync(fixture.Command);

        Assert.Equal(
            WorldAgentEvolutionStatus.ReconciliationRequired,
            result.Status);
        Assert.Equal(
            "world_agent_resume_input_changed",
            result.ReasonCode);
        Assert.Equal(0, runtime.RunCalls);
        Assert.Equal(0, runtime.ResumeCalls);
    }

    [Fact]
    public async Task ForgedSettledFlagCannotBypassActorEvidence()
    {
        var fixture = await FixtureAsync();
        var runtime = new RecordingRuntime();
        var durable = new InMemoryWorldAgentEvolutionStore();
        var faulting = new ThrowOnWriteStore(durable, throwOnWrite: 2);
        var runner = Runner(runtime, fixture.Store, faulting, fixture);
        await Assert.ThrowsAsync<InjectedCrashException>(
            async () => await runner.ExecuteAsync(fixture.Command));
        var valid = await durable.ReadAsync(fixture.Command.CommandId);
        var node = JsonNode.Parse(valid!.Payload.GetRawText())!.AsObject();
        node["hasSettledActors"] = true;
        using var forgedDocument = JsonDocument.Parse(node.ToJsonString());
        var forged = new WorldAgentEvolutionCheckpoint(
            fixture.Command.CommandId,
            1,
            fixture.Command.SemanticDigest,
            forgedDocument.RootElement);
        var forgedStore = new InMemoryWorldAgentEvolutionStore();
        _ = await forgedStore.CompareExchangeAsync(forged, 0);
        var reopened = Runner(
            runtime,
            fixture.Store,
            forgedStore,
            fixture);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await reopened.ResumeAsync(fixture.Command));
    }

    private static WorldAgentEvolutionRunner Runner(
        IDurableAgentRuntime runtime,
        IWorldAuthoritativeTransactionStore world,
        IWorldAgentEvolutionStore evolution,
        Fixture fixture,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? lease = null)
    {
        return new WorldAgentEvolutionRunner(
            runtime,
            new FixedInputFactory(),
            world,
            evolution,
            new WinnerReducer(fixture),
            new FixedWorldAgentRuntimePolicySnapshotSource(
                fixture.Command.RuntimeGeneration),
            new WorldAgentEvolutionRunnerOptions(
                maxParticipants: 8,
                maxConcurrentActors: 2,
                ownerLeaseDuration: lease ?? TimeSpan.FromMinutes(5)),
            utcNow);
    }

    private static async Task<Fixture> FixtureAsync()
    {
        var coordinate = new WorldAuthoritativeCoordinate(
            "world",
            "timeline",
            4,
            0,
            0,
            CatalogDigest);
        var store = new InMemoryWorldAuthoritativeTransactionStore(
            new WorldAuthoritativeStateSnapshot(
                coordinate,
                Json(
                    """
                    {
                      "entities": {
                        "npc-a": { "choice": "" },
                        "npc-b": { "choice": "" },
                        "resource": { "winner": "" }
                      },
                      "relationships": {}
                    }
                    """),
                new Dictionary<string, long>
                {
                    ["npc-a"] = 1,
                    ["npc-b"] = 1,
                    ["resource"] = 1
                }));
        var occurrence = await OccurrenceAsync(store);
        var participants = new[]
        {
            Participant(
                "npc-a",
                occurrence,
                coordinate,
                "batch"),
            Participant(
                "npc-b",
                occurrence,
                coordinate,
                "batch")
        };
        var command = new WorldAgentEvolutionCommand(
            "evolution-command",
            "evolution-operation",
            "batch",
            coordinate,
            participants,
            "first-valid-claim",
            PolicyDigest,
            new WorldAgentRuntimeGeneration(
                1,
                new string('c', 64),
                new string('d', 64),
                new string('e', 64),
                new string('f', 64)),
            new MultiActorBatchBudget(
                maxTokens: 10_000,
                maxActions: 8,
                maxDurationMs: 120_000,
                maxCostUsd: "2"));
        return new Fixture(
            coordinate,
            store,
            occurrence,
            command);
    }

    private static WorldAgentEvolutionParticipant Participant(
        string actorId,
        WorldEventInstance occurrence,
        WorldAuthoritativeCoordinate coordinate,
        string batchId)
    {
        var options = new[]
        {
            DraftOption(
                actorId,
                "claim",
                "claim",
                occurrence,
                coordinate),
            DraftOption(
                actorId,
                "wait",
                "wait",
                occurrence,
                coordinate)
        };
        var draft = new WorldAgentDecisionDraft(
            "draft-" + actorId,
            occurrence,
            coordinate,
            options);
        var job = new WorldAgentJob(
            "job-" + actorId,
            "run-" + actorId,
            actorId + "-agent",
            occurrence.InstanceId,
            WorldAgentJobKind.Selection,
            GameCoordinate(coordinate, actorId),
            Json("""{"event":1}"""),
            "selection",
            "1",
            WorldAgentOutputSchemas.Selection(draft.OptionIds),
            WorldAgentFailurePolicy.Fault,
            coordinate.CatalogDigest,
            batchId,
            authoritativeBinding: draft.Binding);
        return new WorldAgentEvolutionParticipant(draft, job);
    }

    private static WorldAgentEvolutionCommand WithRuntimePolicy(
        WorldAgentEvolutionCommand source,
        WorldAgentRuntimeGeneration runtimePolicy,
        MultiActorBatchBudget? aggregateBudget = null)
    {
        return new WorldAgentEvolutionCommand(
            source.CommandId,
            source.OperationId,
            source.BatchId,
            source.ExpectedCoordinate,
            source.Participants,
            source.ReducerPolicyId,
            source.ReducerPolicyDigest,
            runtimePolicy,
            aggregateBudget ?? source.AggregateBudget);
    }

    private static ToolDescriptor EvolutionTool(string name)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = "1",
            Description = "Evolution policy identity probe.",
            ParametersSchema = Json(
                """
                {
                  "type":"object",
                  "additionalProperties":false
                }
                """),
            Effect = ToolEffects.WorldCommand,
            ConflictScopes = new List<string> { "world" },
            IdempotencyPolicy = ToolIdempotencyPolicies.BestEffort,
            TimeoutMs = 1_000
        };
    }

    private static WorldAgentMutationOption DraftOption(
        string actorId,
        string optionId,
        string value,
        WorldEventInstance occurrence,
        WorldAuthoritativeCoordinate coordinate)
    {
        var intent = new WorldValueMutationIntent(
            "draft-" + actorId + "-" + optionId,
            new GameEntityIdentity(actorId, 1),
            "/choice",
            actorId + ":choice",
            WorldValueMutationKind.Set,
            Json("\"" + value + "\""));
        var mutation = new WorldAtomicMutationSet(
            "draft-command-" + actorId,
            "draft-operation-" + actorId,
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.TimelineEpoch,
            coordinate.SaveRevision,
            coordinate.StateVersion.ToString(CultureInfo.InvariantCulture),
            coordinate.CatalogDigest,
            new[] { intent });
        return new WorldAgentMutationOption(
            optionId,
            new WorldAtomicMutationEffect(
                mutation,
                Array.Empty<WorldNumericSchema>(),
                new WorldEntityMutationPathResolver(
                    "/entities",
                    "/relationships")),
            optionId == "claim"
                ? new string('1', 64)
                : new string('2', 64));
    }

    private static async Task<WorldEventInstance> OccurrenceAsync(
        IWorldEventHistory history)
    {
        var handlers = new WorldEventHandlerRegistryBuilder()
            .AddCondition("condition", new Condition())
            .AddParticipantSelector("selector", new Selector())
            .AddResolver("resolver", new Resolver())
            .AddEffect("effect", new PlanningEffect())
            .Build();
        var definition = new WorldEventDefinition(
            "monthly-contest",
            "1",
            "month",
            10,
            "condition",
            "selector",
            "resolver",
            "effect",
            writeResourceKeys: new[]
            {
                "npc-a:choice",
                "npc-b:choice",
                "resource:winner"
            },
            agentInvocationPolicy:
            WorldAgentInvocationPolicy.OncePerParticipant);
        var plan = await new WorldEventPlanner(handlers, history)
            .PlanAsync(
                new WorldEventPlanningRequest(
                    new WorldEvolutionTrigger(
                        "month-20",
                        "month",
                        "world",
                        "timeline",
                        4,
                        new GameTimePoint(
                            "calendar",
                            "timeline",
                            4,
                            20)),
                    new[] { definition }));
        return Assert.Single(plan.Instances);
    }

    private static WorldEventTransactionExecutionRequest WinnerRequest(
        Fixture fixture,
        string winner,
        string commandId,
        string operationId)
    {
        var intent = new WorldValueMutationIntent(
            "set-winner",
            new GameEntityIdentity("resource", 1),
            "/winner",
            "resource:winner",
            WorldValueMutationKind.Set,
            Json("\"" + winner + "\""));
        var mutation = new WorldAtomicMutationSet(
            commandId,
            operationId,
            fixture.Coordinate.WorldId,
            fixture.Coordinate.TimelineId,
            fixture.Coordinate.TimelineEpoch,
            fixture.Coordinate.SaveRevision,
            fixture.Coordinate.StateVersion.ToString(
                CultureInfo.InvariantCulture),
            fixture.Coordinate.CatalogDigest,
            new[] { intent });
        var effect = new WorldAtomicMutationEffect(
            mutation,
            Array.Empty<WorldNumericSchema>(),
            new WorldEntityMutationPathResolver(
                "/entities",
                "/relationships"));
        return new WorldEventTransactionExecutionRequest(
            fixture.Occurrence,
            fixture.Coordinate,
            commandId,
            operationId,
            effect);
    }

    private static async Task CommitWinnerAsync(
        Fixture fixture,
        string winner,
        string commandId,
        string operationId)
    {
        var result = await new WorldEventTransactionExecutor(fixture.Store)
            .ExecuteAsync(
                WinnerRequest(
                    fixture,
                    winner,
                    commandId,
                    operationId),
                default);
        Assert.Equal(
            WorldTransactionExecutionStatus.Committed,
            result.Status);
    }

    private static GameContextCoordinate GameCoordinate(
        WorldAuthoritativeCoordinate coordinate,
        string actorId)
    {
        return new GameContextCoordinate(
            coordinate.WorldId,
            coordinate.TimelineId,
            coordinate.SaveRevision,
            new GameEntityIdentity(actorId, 1),
            stateVersion: coordinate.StateVersion.ToString(
                CultureInfo.InvariantCulture),
            gameTime: new GameTimePoint(
                "calendar",
                coordinate.TimelineId,
                coordinate.TimelineEpoch,
                20));
    }

    private static string Winner(WorldAuthoritativeStateSnapshot state)
    {
        return state.State
            .GetProperty("entities")
            .GetProperty("resource")
            .GetProperty("winner")
            .GetString()!;
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-evolution-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed record Fixture(
        WorldAuthoritativeCoordinate Coordinate,
        InMemoryWorldAuthoritativeTransactionStore Store,
        WorldEventInstance Occurrence,
        WorldAgentEvolutionCommand Command);

    private sealed class WinnerReducer
        : IWorldAgentEvolutionReducerDescriptor
    {
        private readonly Fixture _fixture;

        public WinnerReducer(Fixture fixture)
        {
            _fixture = fixture;
        }

        public string PolicyId => "first-valid-claim";

        public string PolicyDigest => WorldAgentEvolutionTests.PolicyDigest;

        public ValueTask<WorldAgentEvolutionReduction> ReduceAsync(
            WorldAgentEvolutionReductionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var winner = context.ActorResults
                .Where(item => item.ProposalResult.Succeeded)
                .FirstOrDefault(
                    item => string.Equals(
                        item.ProposalResult.Proposal!.OptionId,
                        "claim",
                        StringComparison.Ordinal));
            if (winner is null)
            {
                return new ValueTask<WorldAgentEvolutionReduction>(
                    new WorldAgentEvolutionReduction(
                        WorldAgentEvolutionReductionDisposition.NoChange,
                        "no_claim",
                        Json("""{"winner":null}""")));
            }

            var actorId = winner.Participant.Job.AgentId
                .Replace("-agent", string.Empty, StringComparison.Ordinal);
            return new ValueTask<WorldAgentEvolutionReduction>(
                new WorldAgentEvolutionReduction(
                    WorldAgentEvolutionReductionDisposition.Commit,
                    "claim_settled",
                    Json("{\"winner\":\"" + actorId + "\"}"),
                    WinnerRequest(
                        _fixture,
                        actorId,
                        context.Command.CommandId,
                        context.Command.OperationId)));
        }
    }

    private sealed class InvalidReducer
        : IWorldAgentEvolutionReducerDescriptor
    {
        private readonly Fixture _fixture;

        public InvalidReducer(Fixture fixture)
        {
            _fixture = fixture;
        }

        public string PolicyId => "first-valid-claim";

        public string PolicyDigest => WorldAgentEvolutionTests.PolicyDigest;

        public ValueTask<WorldAgentEvolutionReduction> ReduceAsync(
            WorldAgentEvolutionReductionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldAgentEvolutionReduction>(
                new WorldAgentEvolutionReduction(
                    WorldAgentEvolutionReductionDisposition.Commit,
                    "invalid",
                    Json("""{"invalid":true}"""),
                    WinnerRequest(
                        _fixture,
                        "npc-a",
                        "different-command",
                        "different-operation")));
        }
    }

    private sealed class WrongPolicyReducer
        : IWorldAgentEvolutionReducerDescriptor
    {
        public string PolicyId => "different-policy";

        public string PolicyDigest => new string('9', 64);

        public ValueTask<WorldAgentEvolutionReduction> ReduceAsync(
            WorldAgentEvolutionReductionContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "A stale reducer must not be invoked.");
        }
    }

    private sealed class BlockingThrowingReducer
        : IWorldAgentEvolutionReducerDescriptor
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string PolicyId => "first-valid-claim";

        public string PolicyDigest => WorldAgentEvolutionTests.PolicyDigest;

        public Task Started => _started.Task;

        public void Release()
        {
            _release.TrySetResult();
        }

        public async ValueTask<WorldAgentEvolutionReduction> ReduceAsync(
            WorldAgentEvolutionReductionContext context,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("injected reducer failure");
        }
    }

    private sealed class MutableInputFactory : IWorldAgentRunInputFactory
    {
        public int Revision { get; set; } = 1;

        public ValueTask<WorldAgentRunInput> CreateAsync(
            WorldAgentJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldAgentRunInput>(
                new WorldAgentRunInput(
                    DateTimeOffset.Parse(
                        "2030-01-01T00:00:00Z",
                        CultureInfo.InvariantCulture),
                    new AgentBudget
                    {
                        MaxTurns = 2,
                        MaxDurationMs = 30_000,
                        MaxTokens = 2_000,
                        MaxCostUsd = "0.5",
                        MaxActions = 2
                    },
                    context: new[]
                    {
                        new ContextCandidate(
                            "mutable-" + job.AgentId,
                            "private_state",
                            Json(
                                "{\"revision\":"
                                + Revision.ToString(
                                    CultureInfo.InvariantCulture)
                                + "}"),
                            required: true)
                    }));
        }
    }

    private sealed class FixedInputFactory : IWorldAgentRunInputFactory
    {
        private readonly int _maxTokens;

        public FixedInputFactory(int maxTokens = 2_000)
        {
            _maxTokens = maxTokens;
        }

        public ValueTask<WorldAgentRunInput> CreateAsync(
            WorldAgentJob job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldAgentRunInput>(
                new WorldAgentRunInput(
                    DateTimeOffset.Parse(
                        "2030-01-01T00:00:00Z",
                        CultureInfo.InvariantCulture),
                    new AgentBudget
                    {
                        MaxTurns = 2,
                        MaxDurationMs = 30_000,
                        MaxTokens = _maxTokens,
                        MaxCostUsd = "0.5",
                        MaxActions = 2
                    },
                    context: new[]
                    {
                        new ContextCandidate(
                            "private-" + job.AgentId,
                            "private_state",
                            Json("{\"actor\":\"" + job.AgentId + "\"}"),
                            required: true)
                    }));
        }
    }

    private sealed class RecordingRuntime : IGuardedDurableAgentRuntime
    {
        private readonly IReadOnlyDictionary<string, int> _delays;
        private readonly bool _blockRuns;
        private readonly Dictionary<string, DurableRunOutcome> _outcomes =
            new(StringComparer.Ordinal);
        private readonly List<DurableExecutionPolicyIdentity>
            _executionPolicies = new();
        private readonly object _sync = new();
        private readonly TaskCompletionSource _allStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCalls;
        private int _resumeCalls;
        private int _started;

        public RecordingRuntime(
            IReadOnlyDictionary<string, int>? delays = null,
            bool blockRuns = false)
        {
            _delays = delays
                      ?? new Dictionary<string, int>(
                          StringComparer.Ordinal);
            _blockRuns = blockRuns;
        }

        public RuntimeControlPlane Controls { get; } = new();

        public int RunCalls => Volatile.Read(ref _runCalls);

        public int ResumeCalls => Volatile.Read(ref _resumeCalls);

        public IReadOnlyList<DurableExecutionPolicyIdentity>
            ExecutionPolicies
        {
            get
            {
                lock (_sync)
                {
                    return _executionPolicies.ToArray();
                }
            }
        }

        public Task AllRunsStarted => _allStarted.Task;

        public void ReleaseRuns()
        {
            _release.TrySetResult();
        }

        public async ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runCalls);
            if (Interlocked.Increment(ref _started) == 2)
            {
                _allStarted.TrySetResult();
            }

            if (_blockRuns)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            if (_delays.TryGetValue(request.Run.AgentId, out var delay))
            {
                await Task.Delay(delay, cancellationToken);
            }

            request.Run.State = RunStates.Completed;
            var outcome = new DurableRunOutcome
            {
                Run = request.Run,
                FinalOutput = Json("""{"optionId":"claim"}""")
            };
            lock (_sync)
            {
                _executionPolicies.Add(
                    DurableExecutionPolicyBinding.Read(request.Run)
                    ?? throw new InvalidOperationException(
                        "An evolution request omitted its execution policy."));
                _outcomes[request.Run.RunId] = outcome;
            }

            return outcome;
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
            Interlocked.Increment(ref _resumeCalls);
            lock (_sync)
            {
                if (!_outcomes.TryGetValue(runId, out var outcome))
                {
                    throw new DurableRunNotFoundException(runId);
                }

                return new ValueTask<DurableRunOutcome>(outcome);
            }
        }
    }

    private sealed class SelectionProvider : IStreamingModelProvider
    {
        private int _callCount;

        public string ProviderId => "world-evolution-selection";

        public int CallCount => Volatile.Read(ref _callCount);

        public ProviderCapabilities Capabilities { get; } = new()
        {
            Streaming = true,
            ToolCalling = true,
            JsonOutput = true
        };

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            StreamingModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 0,
                Kind = ModelStreamEventKinds.ToolCallDelta,
                ToolCallId = "submit-" + call,
                ToolNameDelta =
                    FinalOutputAdmissionControl.SubmitToolName,
                ArgumentsJsonDelta =
                    """
                    {"output":{"optionId":"claim"},"evidence":[]}
                    """
            };
            await Task.Yield();
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 1,
                Kind = ModelStreamEventKinds.Usage,
                Usage = new ProviderUsage
                {
                    InputTokens = 10,
                    OutputTokens = 5,
                    CostUsd = "0"
                }
            };
            yield return new ModelStreamEvent
            {
                StreamAttemptId = request.StreamAttemptId,
                Ordinal = 2,
                Kind = ModelStreamEventKinds.Completed,
                FinishReason = "tool_calls"
            };
        }
    }

    private sealed class CountingRejectingHost : IGameHost
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<ActionReceipt> SubmitActionAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException("No host action expected.");
        }
    }

    private sealed class PartiallySettlingRuntime
        : IGuardedDurableAgentRuntime
    {
        private readonly Dictionary<string, DurableRunOutcome> _outcomes =
            new(StringComparer.Ordinal);
        private readonly object _sync = new();
        private int _runCalls;

        public RuntimeControlPlane Controls { get; } = new();

        public int RunCalls => Volatile.Read(ref _runCalls);

        public List<string> ResumeAttempts { get; } = new();

        public ValueTask<DurableRunOutcome> RunAsync(
            DurableRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _runCalls);
            var completed = string.Equals(
                request.Run.RunId,
                "run-npc-a",
                StringComparison.Ordinal);
            request.Run.State = completed
                ? RunStates.Completed
                : RunStates.WaitingForAction;
            var outcome = new DurableRunOutcome
            {
                Run = request.Run,
                FinalOutput = completed
                    ? Json("""{"optionId":"claim"}""")
                    : null
            };
            lock (_sync)
            {
                _outcomes[request.Run.RunId] = outcome;
            }

            return new ValueTask<DurableRunOutcome>(outcome);
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
            _ = guard;
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ResumeAttempts.Add(runId);
                if (!_outcomes.TryGetValue(runId, out var outcome))
                {
                    throw new DurableRunNotFoundException(runId);
                }

                outcome.Run.State =
                    continuation?.RequestCancellation == true
                        ? RunStates.Cancelled
                        : RunStates.Completed;
                if (continuation?.RequestCancellation != true)
                {
                    outcome.FinalOutput =
                        Json("""{"optionId":"claim"}""");
                }

                return new ValueTask<DurableRunOutcome>(outcome);
            }
        }

        public void Forget(string runId)
        {
            lock (_sync)
            {
                Assert.True(_outcomes.Remove(runId));
            }
        }
    }

    private sealed class ReconcileOverrideWorldStore
        : IWorldAuthoritativeTransactionStore
    {
        private readonly IWorldAuthoritativeTransactionStore _inner;
        private readonly WorldCommandReceipt _receipt;

        public ReconcileOverrideWorldStore(
            IWorldAuthoritativeTransactionStore inner,
            WorldCommandReceipt receipt)
        {
            _inner = inner;
            _receipt = receipt;
        }

        public ValueTask<WorldAuthoritativeStateSnapshot?> ReadAsync(
            WorldTimelineAddress address,
            CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(address, cancellationToken);
        }

        public ValueTask<WorldTransactionBeginResult> BeginAsync(
            WorldTransactionRequest request,
            CancellationToken cancellationToken)
        {
            return _inner.BeginAsync(request, cancellationToken);
        }

        public ValueTask<WorldTransactionInspectionResult> InspectAsync(
            WorldTransactionScope scope,
            string operationId,
            CancellationToken cancellationToken)
        {
            return _inner.InspectAsync(
                scope,
                operationId,
                cancellationToken);
        }

        public ValueTask<WorldTransactionReconciliationResult> ReconcileAsync(
            WorldTransactionScope scope,
            string operationId,
            string requestFingerprint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldTransactionReconciliationResult>(
                WorldTransactionReconciliationResult.Terminal(_receipt));
        }

        public ValueTask<WorldTransactionReconciliationResult>
            CancelPendingAsync(
                WorldTransactionScope scope,
                string operationId,
                string requestFingerprint,
                string outcomeCode,
                CancellationToken cancellationToken)
        {
            return _inner.CancelPendingAsync(
                scope,
                operationId,
                requestFingerprint,
                outcomeCode,
                cancellationToken);
        }
    }

    private sealed class LyingEvolutionJournal : IDurableSessionStore
    {
        private int _enumerated;

        public int Enumerated => Volatile.Read(ref _enumerated);

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(
                new LyingCheckpointList(this));
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        private RuntimeEvent Event(long revision)
        {
            Interlocked.Increment(ref _enumerated);
            var checkpoint = new WorldAgentEvolutionCheckpoint(
                "command",
                revision,
                PolicyDigest,
                Json("""{"stage":1}"""));
            return new RuntimeEvent
            {
                EventId = "event-" + revision,
                RunId = "command",
                Sequence = revision,
                Kind = JournalWorldAgentEvolutionStore.EventKind,
                Timestamp = DateTimeOffset.UnixEpoch,
                Payload = checkpoint.ToEnvelope()
            };
        }

        private sealed class LyingCheckpointList
            : IReadOnlyList<RuntimeEvent>
        {
            private readonly LyingEvolutionJournal _owner;

            public LyingCheckpointList(LyingEvolutionJournal owner)
            {
                _owner = owner;
            }

            public int Count => 1;

            public RuntimeEvent this[int index] =>
                throw new NotSupportedException();

            public IEnumerator<RuntimeEvent> GetEnumerator()
            {
                yield return _owner.Event(1);
                yield return _owner.Event(2);
                yield return _owner.Event(3);
            }

            System.Collections.IEnumerator
                System.Collections.IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }

    private sealed class ThrowOnWriteStore : IWorldAgentEvolutionStore
    {
        private readonly IWorldAgentEvolutionStore _inner;
        private readonly int _throwOnWrite;
        private int _writes;

        public ThrowOnWriteStore(
            IWorldAgentEvolutionStore inner,
            int throwOnWrite)
        {
            _inner = inner;
            _throwOnWrite = throwOnWrite;
        }

        public ValueTask<WorldAgentEvolutionCheckpoint?> ReadAsync(
            string commandId,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReadAsync(commandId, cancellationToken);
        }

        public ValueTask<WorldAgentEvolutionStoreWriteResult>
            CompareExchangeAsync(
                WorldAgentEvolutionCheckpoint checkpoint,
                long expectedRevision,
                CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == _throwOnWrite)
            {
                throw new InjectedCrashException();
            }

            return _inner.CompareExchangeAsync(
                checkpoint,
                expectedRevision,
                cancellationToken);
        }
    }

    private sealed class DuplicateAfterInitialWriteStore
        : IWorldAgentEvolutionStore
    {
        private readonly IWorldAgentEvolutionStore _inner;
        private int _writes;

        public DuplicateAfterInitialWriteStore(
            IWorldAgentEvolutionStore inner)
        {
            _inner = inner;
        }

        public ValueTask<WorldAgentEvolutionCheckpoint?> ReadAsync(
            string commandId,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReadAsync(commandId, cancellationToken);
        }

        public async ValueTask<WorldAgentEvolutionStoreWriteResult>
            CompareExchangeAsync(
                WorldAgentEvolutionCheckpoint checkpoint,
                long expectedRevision,
                CancellationToken cancellationToken = default)
        {
            var result = await _inner.CompareExchangeAsync(
                checkpoint,
                expectedRevision,
                cancellationToken);
            return Interlocked.Increment(ref _writes) == 1
                   && result.Status
                   == WorldAgentEvolutionStoreWriteStatus.Written
                ? new WorldAgentEvolutionStoreWriteResult(
                    WorldAgentEvolutionStoreWriteStatus.Duplicate,
                    result.Current)
                : result;
        }
    }

    private sealed class InjectedCrashException : Exception
    {
    }

    private sealed class Condition : IWorldEventCondition
    {
        public ValueTask<bool> EvaluateAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<bool>(true);
        }
    }

    private sealed class Selector : IWorldEventParticipantSelector
    {
        public ValueTask<IReadOnlyList<WorldEventParticipant>> SelectAsync(
            WorldEventEvaluationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WorldEventParticipant> result = new[]
            {
                new WorldEventParticipant("npc-a", 1, "actor"),
                new WorldEventParticipant("npc-b", 1, "actor"),
                new WorldEventParticipant("resource", 1, "resource")
            };
            return new ValueTask<IReadOnlyList<WorldEventParticipant>>(
                result);
        }
    }

    private sealed class Resolver : IWorldEventResolver
    {
        public ValueTask<IReadOnlyList<WorldEventResolution>> ResolveAsync(
            WorldEventEvaluationContext context,
            IReadOnlyList<WorldEventParticipant> participants,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WorldEventResolution> result = new[]
            {
                new WorldEventResolution(
                    "contest",
                    participants)
            };
            return new ValueTask<IReadOnlyList<WorldEventResolution>>(
                result);
        }
    }

    private sealed class PlanningEffect : IWorldEventEffectHandler
    {
        public ValueTask<WorldEventEffectResult> ApplyAsync(
            WorldEventEffectContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<WorldEventEffectResult>(
                new WorldEventEffectResult(true, "planned"));
        }
    }
}

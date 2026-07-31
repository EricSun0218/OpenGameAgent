using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class RunCheckpointLifecycleTests
{
    private const uint FrameMagic = 0x314A4147;
    private const uint CommitMagic = 0x54494D43;
    private const int HeaderSize = 12;
    private const int FooterSize = 4;

    [Fact]
    public async Task StoreRejectsTerminalCheckpointResurrectionBeforeWrite()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var completed = Clone(running);
            completed.State = RunStates.Completed;
            completed.Revision = 2;
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    completed,
                    RuntimeEventKinds.RunCompleted,
                    "run-completed"),
                expectedRunRevision: 1);
            var resurrected = Clone(completed);
            resurrected.State = RunStates.Running;
            resurrected.Revision = 3;

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            resurrected,
                            RuntimeEventKinds.RunCheckpoint,
                            "run-resurrected"),
                        expectedRunRevision: 2)
                    .AsTask());

            var cursor = await store.GetRunCursorAsync(
                running.RunId,
                default);
            Assert.Equal(2, cursor.NextSequence);
            Assert.Equal(2, cursor.Revision);
            Assert.Equal(
                2,
                (await store.ReadRunAsync(running.RunId, default)).Count);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task StoreRejectsRunStartRevisionOutsideAppendPosition(
        long aggregateRevision)
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var run = Run(RunStates.Running, aggregateRevision);

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            run,
                            RuntimeEventKinds.RunStarted,
                            "run-started"),
                        expectedRunRevision: 0)
                    .AsTask());

            var cursor = await store.GetRunCursorAsync(run.RunId, default);
            Assert.Equal(0, cursor.NextSequence);
            Assert.Equal(0, cursor.Revision);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task StoreRejectsLaterCheckpointRevisionOutsideAppendPosition()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var unanchored = Clone(running);
            unanchored.Revision = 99;

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            unanchored,
                            RuntimeEventKinds.RunCheckpoint,
                            "unanchored-checkpoint"),
                        expectedRunRevision: 1)
                    .AsTask());

            var cursor = await store.GetRunCursorAsync(
                running.RunId,
                default);
            Assert.Equal(1, cursor.NextSequence);
            Assert.Equal(1, cursor.Revision);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Theory]
    [InlineData(RuntimeEventKinds.RunCheckpoint, RunStates.Completed)]
    [InlineData(RuntimeEventKinds.TurnStarted, RunStates.WaitingForAction)]
    [InlineData(RuntimeEventKinds.TurnCompleted, RunStates.WaitingForAction)]
    [InlineData(RuntimeEventKinds.ActionReconciling, RunStates.Running)]
    [InlineData(RuntimeEventKinds.RunCompleted, RunStates.Failed)]
    [InlineData(RuntimeEventKinds.RunCancelled, RunStates.Interrupted)]
    [InlineData(RuntimeEventKinds.RunCheckpoint, RunStates.Preparing)]
    public async Task StoreRejectsInvalidCheckpointKindOrStateProgression(
        string kind,
        string state)
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var invalid = Clone(running);
            invalid.State = state;
            invalid.Revision = 2;

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(invalid, kind, "invalid-checkpoint"),
                        expectedRunRevision: 1)
                    .AsTask());
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Theory]
    [InlineData(
        RuntimeEventKinds.BudgetUpdated,
        RunStates.Running,
        RunStates.WaitingForAction)]
    [InlineData(
        RuntimeEventKinds.ProviderDispatchStarted,
        RunStates.Preparing,
        RunStates.Running)]
    public async Task StoreRejectsMetadataCheckpointStateTransition(
        string kind,
        string initialState,
        string changedState)
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var initial = Run(initialState, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    initial,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var changed = Clone(initial);
            changed.State = changedState;
            changed.Revision = 2;

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            changed,
                            kind,
                            "metadata-state-transition"),
                        expectedRunRevision: 1)
                    .AsTask());

            var cursor = await store.GetRunCursorAsync(
                initial.RunId,
                default);
            Assert.Equal(1, cursor.Revision);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("decision")]
    [InlineData("budget")]
    [InlineData("created_at")]
    [InlineData("trigger")]
    [InlineData("trigger_observations")]
    public async Task StoreRejectsImmutableRunMetadataMutation(
        string mutation)
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            running.BatchId = "batch-1";
            running.DecisionKey = "decision-1";
            running.Trigger.SourceId = "source-1";
            running.TriggerObservationIds.Add("observation-1");
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var changed = Clone(running);
            changed.Revision = 2;
            switch (mutation)
            {
                case "batch":
                    changed.BatchId = "batch-2";
                    break;
                case "decision":
                    changed.DecisionKey = "decision-2";
                    break;
                case "budget":
                    changed.Budget.MaxActions++;
                    break;
                case "created_at":
                    changed.CreatedAt = changed.CreatedAt.AddSeconds(1);
                    break;
                case "trigger":
                    changed.Trigger.SourceId = "source-2";
                    break;
                case "trigger_observations":
                    changed.TriggerObservationIds.Add("observation-2");
                    break;
            }

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            changed,
                            RuntimeEventKinds.RunCheckpoint,
                            "immutable-metadata-change"),
                        expectedRunRevision: 1)
                    .AsTask());

            var cursor = await store.GetRunCursorAsync(
                running.RunId,
                default);
            Assert.Equal(1, cursor.Revision);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Theory]
    [InlineData("completion_intent")]
    [InlineData("current_turn")]
    [InlineData("updated_at")]
    [InlineData("game_context")]
    [InlineData("participant_index")]
    [InlineData("custom_extension")]
    public async Task StoreRejectsForgedAggregateMetadata(
        string mutation)
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            running.Extensions[GameContextEnvelope.ExtensionName] =
                ProtocolJson.ParseElement("""{"coordinate":"original"}""");
            running.Extensions["gameAgent.multiActorInputIndex"] =
                ProtocolJson.ParseElement("0");
            running.Extensions["game.custom"] =
                ProtocolJson.ParseElement("""{"value":1}""");
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var forged = Clone(running);
            forged.Revision = 2;
            switch (mutation)
            {
                case "completion_intent":
                    forged.CompletionIntent = CompletionIntents.Failed;
                    break;
                case "current_turn":
                    forged.CurrentTurnId = "forged-turn";
                    break;
                case "updated_at":
                    forged.UpdatedAt = forged.UpdatedAt.AddSeconds(-1);
                    break;
                case "game_context":
                    forged.Extensions[GameContextEnvelope.ExtensionName] =
                        ProtocolJson.ParseElement(
                            """{"coordinate":"forged"}""");
                    break;
                case "participant_index":
                    forged.Extensions["gameAgent.multiActorInputIndex"] =
                        ProtocolJson.ParseElement("1");
                    break;
                case "custom_extension":
                    forged.Extensions["game.custom"] =
                        ProtocolJson.ParseElement("""{"value":2}""");
                    break;
            }

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            forged,
                            RuntimeEventKinds.RunCheckpoint,
                            "forged-aggregate"),
                        expectedRunRevision: 1)
                    .AsTask());

            var cursor = await store.GetRunCursorAsync(
                running.RunId,
                default);
            Assert.Equal(1, cursor.Revision);
            Assert.Single(
                await store.ReadRunAsync(running.RunId, default));
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task StoreRequiresRunStartedAsFirstCheckpoint()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var run = Run(RunStates.Running, revision: 1);

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            run,
                            RuntimeEventKinds.ProviderDispatchStarted,
                            "provider-dispatch"),
                        expectedRunRevision: 0)
                    .AsTask());
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Theory]
    [InlineData(RunStates.Queued)]
    [InlineData(RunStates.WaitingForAction)]
    public async Task StoreRejectsInvalidRunStartedState(string state)
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var run = Run(state, revision: 1);

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            run,
                            RuntimeEventKinds.RunStarted,
                            "run-started"),
                        expectedRunRevision: 0)
                    .AsTask());
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task StoreRejectsSecondRunStartedCheckpoint()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var duplicateStart = Clone(running);
            duplicateStart.Revision = 2;

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            duplicateStart,
                            RuntimeEventKinds.RunStarted,
                            "run-started-again"),
                        expectedRunRevision: 1)
                    .AsTask());
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task RunStartBatchStagesItsPreparingCheckpoint()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var run = Run(RunStates.Queued, revision: 0);
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());

            await journal.CommitRunStartAsync(
                run,
                Array.Empty<NormalizedMessage>(),
                default);

            var events = await store.ReadRunAsync(run.RunId, default);
            Assert.Collection(
                events,
                started =>
                {
                    Assert.Equal(
                        RuntimeEventKinds.RunStarted,
                        started.Kind);
                    var checkpoint = ReadRun(started);
                    Assert.Equal(RunStates.Preparing, checkpoint.State);
                    Assert.Equal(1, checkpoint.Revision);
                },
                ready =>
                {
                    Assert.Equal(
                        RuntimeEventKinds.RunCheckpoint,
                        ready.Kind);
                    var checkpoint = ReadRun(ready);
                    Assert.Equal(RunStates.Running, checkpoint.State);
                    Assert.Equal(2, checkpoint.Revision);
                });
            Assert.Equal(RunStates.Running, run.State);
            Assert.Equal(2, run.Revision);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task TerminalProviderUsageSettlementPreservesTerminalRun()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var dispatch = Clone(running);
            dispatch.Revision = 2;
            _ = await store.AppendAtomicAsync(
                ProviderCheckpoint(
                    dispatch,
                    RuntimeEventKinds.ProviderDispatchStarted,
                    "provider-dispatch"),
                expectedRunRevision: 1);
            var completed = Clone(dispatch);
            completed.State = RunStates.Completed;
            completed.Revision = 3;
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    completed,
                    RuntimeEventKinds.RunCompleted,
                    "run-completed"),
                expectedRunRevision: 2);
            var settled = Clone(completed);
            settled.Revision = 4;
            settled.Usage.HasUnaccountedUsage = true;
            settled.Usage.UnaccountedProviderAttempts = 1;
            _ = await store.AppendAtomicAsync(
                ProviderCheckpoint(
                    settled,
                    RuntimeEventKinds.ProviderUsageUncertain,
                    "provider-usage-uncertain"),
                expectedRunRevision: 3);
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());

            var recovered = await new RunRecovery(
                    store,
                    store,
                    journal)
                .LoadAsync(running.RunId, default);

            Assert.NotNull(recovered);
            Assert.Equal(RunStates.Completed, recovered.Run.State);
            Assert.Equal(4, recovered.Run.Revision);
            Assert.True(recovered.Run.Usage.HasUnaccountedUsage);
            Assert.Empty(recovered.UnsettledProviderDispatches);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task StoreRejectsNewProviderDispatchAfterTerminalCheckpoint()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var completed = Clone(running);
            completed.State = RunStates.Completed;
            completed.Revision = 2;
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    completed,
                    RuntimeEventKinds.RunCompleted,
                    "run-completed"),
                expectedRunRevision: 1);
            var dispatch = Clone(completed);
            dispatch.Revision = 3;

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        ProviderCheckpoint(
                            dispatch,
                            RuntimeEventKinds.ProviderDispatchStarted,
                            "late-provider-dispatch"),
                        expectedRunRevision: 2)
                    .AsTask());
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task StoreRejectsSecondTerminalEventWithNewIdentity()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var completed = Clone(running);
            completed.State = RunStates.Completed;
            completed.Revision = 2;
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    completed,
                    RuntimeEventKinds.RunCompleted,
                    "run-completed"),
                expectedRunRevision: 1);
            var repeated = Clone(completed);
            repeated.Revision = 3;

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            repeated,
                            RuntimeEventKinds.RunCompleted,
                            "run-completed-again"),
                        expectedRunRevision: 2)
                    .AsTask());
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task StoreRejectsTerminalMetadataMutationDuringSettlement()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var completed = Clone(running);
            completed.State = RunStates.Completed;
            completed.Revision = 2;
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    completed,
                    RuntimeEventKinds.RunCompleted,
                    "run-completed"),
                expectedRunRevision: 1);
            var tampered = Clone(completed);
            tampered.Revision = 3;
            tampered.TerminalReason = "tampered";

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        ProviderCheckpoint(
                            tampered,
                            RuntimeEventKinds.ProviderUsageUncertain,
                            "provider-usage-uncertain"),
                        expectedRunRevision: 2)
                    .AsTask());
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Theory]
    [InlineData("turns")]
    [InlineData("duration")]
    [InlineData("input_tokens")]
    [InlineData("output_tokens")]
    [InlineData("cost")]
    [InlineData("actions")]
    [InlineData("unaccounted")]
    public async Task StoreRejectsUsageAccountingRegressionBeforeWrite(
        string field)
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            running.Usage.Turns = 2;
            running.Usage.DurationMs = 100;
            running.Usage.InputTokens = 10;
            running.Usage.OutputTokens = 20;
            running.Usage.CostUsd = "0.25";
            running.Usage.Actions = 3;
            running.Usage.HasUnaccountedUsage = true;
            running.Usage.UnaccountedProviderAttempts = 1;
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var regressed = Clone(running);
            regressed.Revision = 2;
            switch (field)
            {
                case "turns":
                    regressed.Usage.Turns--;
                    break;
                case "duration":
                    regressed.Usage.DurationMs--;
                    break;
                case "input_tokens":
                    regressed.Usage.InputTokens--;
                    break;
                case "output_tokens":
                    regressed.Usage.OutputTokens--;
                    break;
                case "cost":
                    regressed.Usage.CostUsd = "0.24";
                    break;
                case "actions":
                    regressed.Usage.Actions--;
                    break;
                default:
                    regressed.Usage.HasUnaccountedUsage = false;
                    regressed.Usage.UnaccountedProviderAttempts = 0;
                    break;
            }

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.AppendAtomicAsync(
                        Checkpoint(
                            regressed,
                            RuntimeEventKinds.BudgetUpdated,
                            "usage-regression"),
                        expectedRunRevision: 1)
                    .AsTask());

            var cursor = await store.GetRunCursorAsync(
                running.RunId,
                default);
            Assert.Equal(1, cursor.Revision);
            Assert.Single(
                await store.ReadRunAsync(running.RunId, default));
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task ReplaySafeTurnAbandonmentMayDecrementOneTurn()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var running = Run(RunStates.Running, revision: 1);
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started"),
                expectedRunRevision: 0);
            var started = Clone(running);
            started.Revision = 2;
            started.CurrentTurnId = "turn-1";
            started.Usage.Turns = 1;
            _ = await store.AppendAtomicAsync(
                Checkpoint(
                    started,
                    RuntimeEventKinds.TurnStarted,
                    "turn-started"),
                expectedRunRevision: 1);
            var abandoned = Clone(started);
            abandoned.Revision = 3;
            abandoned.CurrentTurnId = null;
            abandoned.Usage.Turns = 0;
            var completed = Checkpoint(
                abandoned,
                RuntimeEventKinds.TurnCompleted,
                "turn-abandoned");
            completed.ReasonCode =
                RunRecovery.ReplaySafeTurnAbandonedReason;

            var append = await store.AppendAtomicAsync(
                completed,
                expectedRunRevision: 2);

            Assert.Equal(3, append.Revision);
            Assert.Equal(
                3,
                (await store.ReadRunAsync(running.RunId, default)).Count);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("terminal_resurrection")]
    [InlineData("usage_regression")]
    public void RestartRejectsCorruptCheckpointLifecycle(string corruption)
    {
        var path = JournalPath();
        try
        {
            var running = Run(
                RunStates.Running,
                revision: corruption == "revision" ? 2 : 1);
            if (corruption == "usage_regression")
            {
                running.Usage.InputTokens = 10;
            }

            var events = new List<RuntimeEvent>
            {
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started",
                    sequence: 0)
            };
            if (corruption == "terminal_resurrection")
            {
                var completed = Clone(running);
                completed.State = RunStates.Completed;
                completed.Revision = 2;
                events.Add(
                    Checkpoint(
                        completed,
                        RuntimeEventKinds.RunCompleted,
                        "run-completed",
                        sequence: 1));
                var resurrected = Clone(completed);
                resurrected.State = RunStates.Running;
                resurrected.Revision = 3;
                events.Add(
                    Checkpoint(
                        resurrected,
                        RuntimeEventKinds.RunCheckpoint,
                        "run-resurrected",
                        sequence: 2));
            }
            else if (corruption == "usage_regression")
            {
                var regressed = Clone(running);
                regressed.Revision = 2;
                regressed.Usage.InputTokens = 9;
                events.Add(
                    Checkpoint(
                        regressed,
                        RuntimeEventKinds.BudgetUpdated,
                        "usage-regression",
                        sequence: 1));
            }

            WriteCommittedFrames(path, events);

            Assert.Throws<JournalCorruptionException>(
                () => new FileSessionStore(path));
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task RestartAcceptsOnlyPreviousFormatDurationCheckpoint()
    {
        var legacyPath = JournalPath();
        var currentPath = JournalPath();
        try
        {
            var running = Run(RunStates.Running, revision: 1);
            var turnStarted = Clone(running);
            turnStarted.Revision = 2;
            turnStarted.CurrentTurnId = "turn-1";
            turnStarted.Usage.Turns = 1;
            var waiting = Clone(turnStarted);
            waiting.Revision = 3;
            waiting.State = RunStates.WaitingForAction;
            waiting.PendingOperationIds.Add("operation-1");
            var reconciling = Clone(waiting);
            reconciling.Revision = 4;
            reconciling.State = RunStates.Reconciling;
            reconciling.CompletionIntent = CompletionIntents.Cancelled;
            var durationReached = Clone(reconciling);
            durationReached.Revision = 5;
            durationReached.CompletionIntent = null;
            durationReached.TerminalReason = "max_duration";
            durationReached.Usage.DurationMs = 100;
            var events = new[]
            {
                Checkpoint(
                    running,
                    RuntimeEventKinds.RunStarted,
                    "run-started",
                    sequence: 0),
                Checkpoint(
                    turnStarted,
                    RuntimeEventKinds.TurnStarted,
                    "turn-started",
                    sequence: 1),
                Checkpoint(
                    waiting,
                    RuntimeEventKinds.RunCheckpoint,
                    "waiting",
                    sequence: 2),
                Checkpoint(
                    reconciling,
                    RuntimeEventKinds.ActionReconciling,
                    "reconciling",
                    sequence: 3),
                Checkpoint(
                    durationReached,
                    RuntimeEventKinds.RunCheckpoint,
                    "legacy-duration",
                    sequence: 4)
            };
            events[2].TurnId = "turn-1";
            events[3].TurnId = "turn-1";
            events[4].TurnId = "turn-1";

            WriteCommittedFrames(
                legacyPath,
                events,
                formatVersion: 2);
            await using (var recovered = new FileSessionStore(legacyPath))
            {
                var cursor = await recovered.GetRunCursorAsync(
                    running.RunId);
                Assert.Equal(5, cursor.Revision);
                var recoveredEvents = await recovered.ReadRunAsync(
                    running.RunId,
                    default);
                Assert.Equal(
                    "max_duration",
                    ReadRun(recoveredEvents[^1]).TerminalReason);
            }

            WriteCommittedFrames(
                currentPath,
                events,
                formatVersion: 3);
            Assert.Throws<JournalCorruptionException>(
                () => new FileSessionStore(currentPath));
        }
        finally
        {
            DeleteJournal(legacyPath);
            DeleteJournal(currentPath);
        }
    }

    [Fact]
    public async Task FormatOneCompletedHistoryCoexistsWithNewFormatRuns()
    {
        var path = JournalPath();
        try
        {
            var legacyRunning = Run(RunStates.Running, revision: 1);
            var legacyCompleted = Clone(legacyRunning);
            legacyCompleted.State = RunStates.Completed;
            legacyCompleted.Revision = 2;
            WriteCommittedFrames(
                path,
                new[]
                {
                    Checkpoint(
                        legacyRunning,
                        RuntimeEventKinds.RunStarted,
                        "legacy-run-started",
                        sequence: 0),
                    Checkpoint(
                        legacyCompleted,
                        RuntimeEventKinds.RunCompleted,
                        "legacy-run-completed",
                        sequence: 1)
                },
                formatVersion: 1);

            var currentRun = Run(RunStates.Running, revision: 1);
            currentRun.RunId = "run-2";
            await using (var store = new FileSessionStore(path))
            {
                var legacy = await store.ReadRunAsync(
                    legacyRunning.RunId,
                    default);
                Assert.Equal(2, legacy.Count);
                Assert.Equal(
                    RunStates.Completed,
                    ReadRun(legacy[^1]).State);

                var appended = await store.AppendAtomicAsync(
                    Checkpoint(
                        currentRun,
                        RuntimeEventKinds.RunStarted,
                        "current-run-started"),
                    expectedRunRevision: 0);
                Assert.Equal(0, appended.Sequence);
                Assert.Equal(1, appended.Revision);
            }

            var journalText = Encoding.UTF8.GetString(
                File.ReadAllBytes(path));
            Assert.Contains("\"formatVersion\":1", journalText);
            Assert.Contains("\"formatVersion\":3", journalText);

            await using var recovered = new FileSessionStore(path);
            Assert.Equal(
                RunStates.Completed,
                ReadRun(
                    (await recovered.ReadRunAsync(
                        legacyRunning.RunId,
                        default))[^1]).State);
            Assert.Equal(
                "current-run-started",
                Assert.Single(
                    await recovered.ReadRunAsync(
                        currentRun.RunId,
                        default)).EventId);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    private static AgentRun Run(string state, long revision)
    {
        return new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = state,
            Revision = revision,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static AgentRun Clone(AgentRun run)
    {
        return ProtocolJson.DeserializeAgentRun(
            ProtocolJson.Serialize(run));
    }

    private static AgentRun ReadRun(RuntimeEvent runtimeEvent)
    {
        return ProtocolJson.DeserializeAgentRun(
            runtimeEvent.Payload.GetRawText());
    }

    private static RuntimeEvent Checkpoint(
        AgentRun run,
        string kind,
        string eventId,
        long sequence = 0)
    {
        return new RuntimeEvent
        {
            EventId = eventId,
            RunId = run.RunId,
            TurnId = kind is RuntimeEventKinds.TurnStarted
                or RuntimeEventKinds.TurnCompleted
                ? "turn-1"
                : null,
            Kind = kind,
            Durability = EventDurabilities.Durable,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            Sequence = sequence,
            RuntimeGeneration = run.RuntimeGeneration,
            Payload = ProtocolJson.ToElement(run)
        };
    }

    private static RuntimeEvent ProviderCheckpoint(
        AgentRun run,
        string kind,
        string eventId)
    {
        var runtimeEvent = Checkpoint(run, kind, eventId);
        runtimeEvent.TurnId = "turn-1";
        runtimeEvent.AttemptId = "provider-attempt-1";
        runtimeEvent.StreamAttemptId = "stream-attempt-1";
        runtimeEvent.ProviderId = "provider-1";
        runtimeEvent.ReasonCode =
            kind == RuntimeEventKinds.ProviderUsageUncertain
                ? "terminal_provider_dispatch_unknown"
                : null;
        return runtimeEvent;
    }

    private static string JournalPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "game-agent-checkpoint-lifecycle-tests",
            Guid.NewGuid().ToString("N"),
            "runtime.journal");
    }

    private static void DeleteJournal(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteCommittedFrames(
        string path,
        IReadOnlyList<RuntimeEvent> events,
        int formatVersion = 3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        for (var index = 0; index < events.Count; index++)
        {
            var frame = new RawJournalFrame
            {
                FormatVersion = formatVersion,
                StreamId = events[index].RunId!,
                RunSequence = index,
                RunRevision = index + 1,
                RuntimeEvent = events[index]
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                frame,
                RawJsonOptions);
            var bytes = BuildFrame(payload);
            stream.Write(bytes, 0, bytes.Length);
        }

        stream.Flush(flushToDisk: true);
    }

    private static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[
            checked(HeaderSize + payload.Length + FooterSize)];
        WriteUInt32(frame, 0, FrameMagic);
        WriteUInt32(frame, 4, checked((uint)payload.Length));
        WriteUInt32(frame, 8, ComputeCrc32(payload));
        Buffer.BlockCopy(
            payload,
            0,
            frame,
            HeaderSize,
            payload.Length);
        WriteUInt32(
            frame,
            HeaderSize + payload.Length,
            CommitMagic);
        return frame;
    }

    private static void WriteUInt32(
        byte[] buffer,
        int offset,
        uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static uint ComputeCrc32(byte[] value)
    {
        var checksum = uint.MaxValue;
        foreach (var item in value)
        {
            checksum ^= item;
            for (var bit = 0; bit < 8; bit++)
            {
                checksum = (checksum & 1) == 0
                    ? checksum >> 1
                    : checksum >> 1 ^ 0xEDB88320;
            }
        }

        return ~checksum;
    }

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class RawJournalFrame
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; set; }

        [JsonPropertyName("streamId")]
        public string StreamId { get; set; } = string.Empty;

        [JsonPropertyName("runSequence")]
        public long RunSequence { get; set; }

        [JsonPropertyName("runRevision")]
        public long RunRevision { get; set; }

        [JsonPropertyName("runtimeEvent")]
        public RuntimeEvent RuntimeEvent { get; set; } = new();
    }
}

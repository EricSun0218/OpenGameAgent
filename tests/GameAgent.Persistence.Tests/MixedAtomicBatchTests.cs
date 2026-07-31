using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class MixedAtomicBatchTests
{
    [Fact]
    public async Task EventIdDuplicateMixedWithFreshActionIsRejectedAtomically()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            using var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var first = Request(run, "operation-a", "call-a");
            var second = Request(run, "operation-b", "call-b");
            await journal.AppendActionRequestAsync(
                run,
                first,
                "attempt-1",
                default);
            var before = await SnapshotAsync(store, run, first);

            await Assert.ThrowsAsync<JournalEntryConflictException>(
                () => journal.AppendActionRequestsAsync(
                        run,
                        new[] { first, second },
                        "attempt-2",
                        default)
                    .AsTask());

            await AssertUnchangedAsync(
                store,
                run,
                first,
                second,
                before);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task OperationDuplicateMixedWithFreshActionIsRejectedAtomically()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            using var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var first = Request(run, "operation-a", "call-a");
            var second = Request(run, "operation-b", "call-b");
            var rawAppend = await store.AppendAtomicAsync(
                new RuntimeEvent
                {
                    EventId = "legacy-action-request:operation-a",
                    RunId = run.RunId,
                    TurnId = first.TurnId,
                    Kind = RuntimeEventKinds.ActionRequested,
                    Durability = EventDurabilities.Durable,
                    Timestamp = clock.UtcNow,
                    RuntimeGeneration = run.RuntimeGeneration,
                    Payload = ProtocolJson.ToElement(first)
                },
                expectedRunRevision: run.Revision);
            run.Revision = rawAppend.Revision;
            run.UpdatedAt = clock.UtcNow;
            run.PendingOperationIds.Add(first.OperationId);
            var before = await SnapshotAsync(store, run, first);

            await Assert.ThrowsAsync<JournalEntryConflictException>(
                () => journal.AppendActionRequestsAsync(
                        run,
                        new[] { first, second },
                        "attempt-2",
                        default)
                    .AsTask());

            await AssertUnchangedAsync(
                store,
                run,
                first,
                second,
                before);
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    [Fact]
    public async Task AllFreshAndAllDuplicateActionBatchesRemainSupported()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            using var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var first = Request(run, "operation-a", "call-a");
            var second = Request(run, "operation-b", "call-b");

            await journal.AppendActionRequestsAsync(
                run,
                new[] { first, second },
                "attempt-1",
                default);

            Assert.Equal(3, run.Revision);
            Assert.Equal(
                new[] { first.OperationId, second.OperationId },
                run.PendingOperationIds);
            var cursor = await store.GetRunCursorAsync(
                run.RunId,
                default);
            var eventCount = (await store.ReadRunAsync(
                    run.RunId,
                    default))
                .Count;

            await journal.AppendActionRequestsAsync(
                run,
                new[] { first, second },
                "attempt-retry",
                default);

            var after = await store.GetRunCursorAsync(run.RunId, default);
            Assert.Equal(cursor.NextSequence, after.NextSequence);
            Assert.Equal(cursor.Revision, after.Revision);
            Assert.Equal(3, run.Revision);
            Assert.Equal(
                eventCount,
                (await store.ReadRunAsync(run.RunId, default)).Count);
            Assert.NotNull(
                await store.GetOperationAsync(first.OperationId, default));
            Assert.NotNull(
                await store.GetOperationAsync(second.OperationId, default));
        }
        finally
        {
            DeleteJournal(path);
        }
    }

    private static async ValueTask<BatchSnapshot> SnapshotAsync(
        FileSessionStore store,
        AgentRun run,
        ActionRequest existing)
    {
        return new BatchSnapshot(
            run.Revision,
            run.UpdatedAt,
            run.PendingOperationIds.ToArray(),
            await store.GetRunCursorAsync(run.RunId, default),
            (await store.ReadRunAsync(run.RunId, default)).Count,
            await store.GetOperationAsync(existing.OperationId, default)
            ?? throw new InvalidDataException(
                "The setup operation was not persisted."));
    }

    private static async ValueTask AssertUnchangedAsync(
        FileSessionStore store,
        AgentRun run,
        ActionRequest existing,
        ActionRequest fresh,
        BatchSnapshot before)
    {
        Assert.Equal(before.RunRevision, run.Revision);
        Assert.Equal(before.RunUpdatedAt, run.UpdatedAt);
        Assert.Equal(before.PendingOperationIds, run.PendingOperationIds);
        var cursor = await store.GetRunCursorAsync(run.RunId, default);
        Assert.Equal(before.Cursor.NextSequence, cursor.NextSequence);
        Assert.Equal(before.Cursor.Revision, cursor.Revision);
        Assert.Equal(
            before.EventCount,
            (await store.ReadRunAsync(run.RunId, default)).Count);
        var operation = await store.GetOperationAsync(
            existing.OperationId,
            default);
        Assert.NotNull(operation);
        Assert.Equal(
            before.ExistingOperation.RequestSequence,
            operation.RequestSequence);
        Assert.Equal(
            before.ExistingOperation.RequestRunRevision,
            operation.RequestRunRevision);
        Assert.Equal(
            ProtocolJson.Serialize(before.ExistingOperation.Request),
            ProtocolJson.Serialize(operation.Request));
        Assert.Null(
            await store.GetOperationAsync(fresh.OperationId, default));
    }

    private static AgentRun Run()
    {
        return new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Queued,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static ActionRequest Request(
        AgentRun run,
        string operationId,
        string toolCallId)
    {
        return new ActionRequest
        {
            OperationId = operationId,
            RunId = run.RunId,
            TurnId = "turn-1",
            ToolCallId = toolCallId,
            AgentId = run.AgentId,
            WorldId = run.WorldId,
            ActionName = "read_state",
            ActionVersion = "1",
            Arguments = ProtocolJson.ParseElement(
                """{"entityId":"npc-1"}"""),
            RequestedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static string JournalPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "game-agent-mixed-batch-tests",
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

    private sealed class Clock : IRuntimeClock
    {
        public DateTimeOffset UtcNow =>
            new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Ids : IRuntimeIdGenerator
    {
        private int _value;

        public string NewId(string category)
        {
            return category + "-" + Interlocked.Increment(ref _value);
        }
    }

    private sealed record BatchSnapshot(
        long RunRevision,
        DateTimeOffset RunUpdatedAt,
        IReadOnlyList<string> PendingOperationIds,
        RunJournalCursor Cursor,
        int EventCount,
        OperationLedgerEntry ExistingOperation);
}

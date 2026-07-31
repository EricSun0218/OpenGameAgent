using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class TerminalReceiptBatchTests
{
    [Fact]
    public async Task FreshTerminalBatchUsesItsStagedReceivedReceipt()
    {
        var path = CreateJournalPath();
        try
        {
            await using (var store = new FileSessionStore(path))
            {
                using var journal = new JournalCoordinator(
                    store,
                    store,
                    new SystemRuntimeClock(),
                    new GuidRuntimeIdGenerator());
                var run = CreateRun();
                await journal.CommitTransitionAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.RunStarted);
                var request = CreateRequest(run);
                await journal.AppendActionRequestAsync(
                    run,
                    request,
                    "attempt-1",
                    CancellationToken.None);

                var received = CreateReceipt(
                    request.OperationId,
                    DateTimeOffset.UnixEpoch.AddSeconds(1),
                    result: """{"moved":true}""");
                var terminal = CloneReceipt(received);
                terminal.ReceivedAt = received.ReceivedAt.AddSeconds(1);
                var append = await store.AppendAtomicBatchAsync(
                    new[]
                    {
                        CreateReceiptEvent(
                            run,
                            request,
                            "receipt-event",
                            RuntimeEventKinds.ActionReceived,
                            received),
                        CreateReceiptEvent(
                            run,
                            request,
                            "terminal-event",
                            RuntimeEventKinds.ToolCompleted,
                            terminal)
                    },
                    expectedRunRevision: run.Revision);

                Assert.Equal(new long[] { 3, 4 }, append.Select(x => x.Revision));
                var operation = await store.GetOperationAsync(
                    request.OperationId);
                Assert.NotNull(operation);
                Assert.False(operation.IsPending);
                Assert.Equal(received.ReceivedAt, operation.LatestReceipt!.ReceivedAt);
            }

            await using var recovered = new FileSessionStore(path);
            var recoveredOperation = await recovered.GetOperationAsync(
                "operation-1");
            Assert.NotNull(recoveredOperation);
            Assert.False(recoveredOperation.IsPending);
            Assert.Equal(
                4,
                (await recovered.GetRunCursorAsync("run-1")).Revision);
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task FreshTerminalBatchRejectsAuthoritativeMismatchAtomically()
    {
        var path = CreateJournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            using var journal = new JournalCoordinator(
                store,
                store,
                new SystemRuntimeClock(),
                new GuidRuntimeIdGenerator());
            var run = CreateRun();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var request = CreateRequest(run);
            await journal.AppendActionRequestAsync(
                run,
                request,
                "attempt-1",
                CancellationToken.None);

            var received = CreateReceipt(
                request.OperationId,
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                result: """{"moved":true}""");
            var mismatchedTerminal = CreateReceipt(
                request.OperationId,
                received.ReceivedAt.AddSeconds(1),
                result: """{"moved":false}""");

            await Assert.ThrowsAsync<OperationLedgerConflictException>(
                () => store.AppendAtomicBatchAsync(
                        new[]
                        {
                            CreateReceiptEvent(
                                run,
                                request,
                                "receipt-event",
                                RuntimeEventKinds.ActionReceived,
                                received),
                            CreateReceiptEvent(
                                run,
                                request,
                                "terminal-event",
                                RuntimeEventKinds.ToolCompleted,
                                mismatchedTerminal)
                        },
                        expectedRunRevision: run.Revision)
                    .AsTask());

            Assert.Equal(
                run.Revision,
                (await store.GetRunCursorAsync(run.RunId)).Revision);
            var operation = await store.GetOperationAsync(request.OperationId);
            Assert.NotNull(operation);
            Assert.True(operation.IsPending);
            Assert.Null(operation.LatestReceipt);
            Assert.DoesNotContain(
                await store.ReadRunAsync(run.RunId, CancellationToken.None),
                item => item.EventId is "receipt-event" or "terminal-event");
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    private static AgentRun CreateRun()
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

    private static ActionRequest CreateRequest(AgentRun run)
    {
        return new ActionRequest
        {
            OperationId = "operation-1",
            RunId = run.RunId,
            TurnId = "turn-1",
            ToolCallId = "call-1",
            AgentId = run.AgentId,
            WorldId = run.WorldId,
            ActionName = "move",
            ActionVersion = "1",
            Arguments = Json("""{"destination":"gate"}"""),
            RequestedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static ActionReceipt CreateReceipt(
        string operationId,
        DateTimeOffset receivedAt,
        string result)
    {
        return new ActionReceipt
        {
            OperationId = operationId,
            Revision = 0,
            Status = ReceiptStatuses.Succeeded,
            Result = Json(result),
            CommittedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
            ReceivedAt = receivedAt
        };
    }

    private static ActionReceipt CloneReceipt(ActionReceipt receipt)
    {
        return ProtocolJson.DeserializeActionReceipt(
            ProtocolJson.Serialize(receipt));
    }

    private static RuntimeEvent CreateReceiptEvent(
        AgentRun run,
        ActionRequest request,
        string eventId,
        string kind,
        ActionReceipt receipt)
    {
        return new RuntimeEvent
        {
            EventId = eventId,
            RunId = run.RunId,
            TurnId = request.TurnId,
            Kind = kind,
            Durability = EventDurabilities.Durable,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(2),
            RuntimeGeneration = run.RuntimeGeneration,
            Payload = ProtocolJson.ToElement(receipt)
        };
    }

    private static JsonElement Json(string value)
    {
        return ProtocolJson.ParseElement(value);
    }

    private static string CreateJournalPath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "game-agent-terminal-receipt-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "sessions.gaj");
    }

    private static void DeleteJournalDirectory(string journalPath)
    {
        var directory = Path.GetDirectoryName(journalPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

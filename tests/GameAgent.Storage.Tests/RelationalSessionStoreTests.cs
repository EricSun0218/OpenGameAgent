using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Storage.Postgres;
using GameAgent.Storage.Relational;
using GameAgent.Storage.Sqlite;

namespace GameAgent.Storage.Tests;

public sealed class RelationalSessionStoreTests
{
    [Fact]
    public async Task SqlitePersistsCursorAndIdempotentEventsAcrossRestart()
    {
        var path = TempDatabase();
        try
        {
            var connection = ConnectionString(path);
            await using (var owner = new SqliteSessionStore(connection))
            {
                var first = await owner.AppendAtomicAsync(Event("event-1", "run"), 0, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(0, first.Sequence);
                Assert.Equal(1, first.Revision);
            }
            await using (var reopened = new SqliteSessionStore(connection))
            {
                var duplicate = await reopened.AppendAtomicAsync(Event("event-1", "run"), 999, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(duplicate.WasDuplicate);
                var second = await reopened.AppendAtomicAsync(Event("event-2", "run"), 1, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(2, second.Revision);
                Assert.Equal(2, (await reopened.GetRunCursorAsync("run", cancellationToken: TestContext.Current.CancellationToken)).NextSequence);
                Assert.Equal(2, (await reopened.ReadRunAsync("run", TestContext.Current.CancellationToken)).Count);
            }
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task SqliteRejectsMixedDuplicateAndNewAtomicBatch()
    {
        var path = TempDatabase();
        try
        {
            await using var store = new SqliteSessionStore(ConnectionString(path));
            await store.AppendAtomicAsync(Event("event-1", "run"), cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<JournalEntryConflictException>(
                async () => await store.AppendAtomicBatchAsync(
                    new[] { Event("event-1", "run"), Event("event-2", "run") }, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Single(await store.ReadRunAsync("run", TestContext.Current.CancellationToken));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task SqliteMaintainsOperationLedgerAndReconciliation()
    {
        var path = TempDatabase();
        try
        {
            await using var store = new SqliteSessionStore(ConnectionString(path));
            await store.AppendAtomicAsync(Started("run"), 0, cancellationToken: TestContext.Current.CancellationToken);
            await store.AppendAtomicAsync(ActionEvent("request", RuntimeEventKinds.ActionRequested, Request("operation")), 1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(await store.ReadPendingOperationsAsync("run", cancellationToken: TestContext.Current.CancellationToken));

            var receiptEvent = ActionEvent(
                "receipt",
                RuntimeEventKinds.ActionReceived,
                Receipt("operation", ReceiptStatuses.Succeeded));
            var reconciled = await store.ReconcileReceiptAsync(receiptEvent, 2, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ReceiptStatuses.Succeeded, reconciled.Operation.LatestReceipt!.Status);
            Assert.Empty(await store.ReadPendingOperationsAsync("run", cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task SqliteDeduplicatesEquivalentOperationsAcrossEventIds()
    {
        var path = TempDatabase();
        try
        {
            await using var store = new SqliteSessionStore(ConnectionString(path));
            await store.AppendAtomicAsync(Started("run"), 0, cancellationToken: TestContext.Current.CancellationToken);
            var request = Request("operation");
            var firstRequest = await store.AppendAtomicAsync(
                ActionEvent("request-1", RuntimeEventKinds.ActionRequested, request), 1, cancellationToken: TestContext.Current.CancellationToken);
            var duplicateRequest = await store.AppendAtomicAsync(
                ActionEvent("request-2", RuntimeEventKinds.ActionRequested, request), 1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(duplicateRequest.WasDuplicate);
            Assert.Equal(firstRequest.Sequence, duplicateRequest.Sequence);
            Assert.Equal(firstRequest.Revision, duplicateRequest.Revision);

            var receipt = Receipt("operation", ReceiptStatuses.Succeeded);
            var firstReceipt = await store.ReconcileReceiptAsync(
                ActionEvent("receipt-1", RuntimeEventKinds.ActionReceived, receipt), 2, cancellationToken: TestContext.Current.CancellationToken);
            receipt.ReceivedAt = receipt.ReceivedAt.AddMinutes(1);
            var duplicateReceipt = await store.ReconcileReceiptAsync(
                ActionEvent("receipt-2", RuntimeEventKinds.ActionReceived, receipt), 2, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(duplicateReceipt.Append.WasDuplicate);
            Assert.Equal(firstReceipt.Append.Sequence, duplicateReceipt.Append.Sequence);
            Assert.Equal(3, (await store.GetRunCursorAsync("run", cancellationToken: TestContext.Current.CancellationToken)).Revision);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task SqliteCompareAndSwapRejectsStaleWriter()
    {
        var path = TempDatabase();
        try
        {
            var connection = ConnectionString(path);
            await using var first = new SqliteSessionStore(connection);
            await using var second = new SqliteSessionStore(connection);
            await first.AppendAtomicAsync(Event("event-1", "run"), 0, cancellationToken: TestContext.Current.CancellationToken);

            var attempts = new[]
            {
                first.AppendAtomicAsync(Event("event-2a", "run"), 1, cancellationToken: TestContext.Current.CancellationToken).AsTask(),
                second.AppendAtomicAsync(Event("event-2b", "run"), 1, cancellationToken: TestContext.Current.CancellationToken).AsTask()
            };
            try { await Task.WhenAll(attempts); } catch { }
            Assert.Equal(1, attempts.Count(static task => task.Status == TaskStatus.RanToCompletion));
            Assert.Equal(2, (await first.GetRunCursorAsync("run", cancellationToken: TestContext.Current.CancellationToken)).Revision);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task PostgresContractWhenConfigured()
    {
        var connection = Environment.GetEnvironmentVariable("GAME_AGENT_POSTGRES_TEST");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var namespaceId = "test-" + Guid.NewGuid().ToString("N");
        var options = new RelationalSessionStoreOptions { NamespaceId = namespaceId };
        await using (var first = new PostgresSessionStore(connection, options))
        await using (var second = new PostgresSessionStore(connection, options))
        {
            await first.AppendAtomicAsync(Event("event-1", "run"), 0, cancellationToken: TestContext.Current.CancellationToken);
            var attempts = new[]
            {
                first.AppendAtomicAsync(Event("event-2a", "run"), 1, cancellationToken: TestContext.Current.CancellationToken).AsTask(),
                second.AppendAtomicAsync(Event("event-2b", "run"), 1, cancellationToken: TestContext.Current.CancellationToken).AsTask()
            };
            try { await Task.WhenAll(attempts); } catch { }
            Assert.Equal(1, attempts.Count(static task => task.Status == TaskStatus.RanToCompletion));
            Assert.Equal(2, (await first.GetRunCursorAsync("run", cancellationToken: TestContext.Current.CancellationToken)).Revision);

            await first.AppendAtomicAsync(Started("ledger-run"), 0, cancellationToken: TestContext.Current.CancellationToken);
            var request = Request("postgres-operation", "ledger-run");
            await first.AppendAtomicAsync(
                ActionEvent("postgres-request", RuntimeEventKinds.ActionRequested, request), 1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(await second.ReadPendingOperationsAsync("ledger-run", cancellationToken: TestContext.Current.CancellationToken));
            var receipt = Receipt("postgres-operation", ReceiptStatuses.Succeeded);
            await second.ReconcileReceiptAsync(
                ActionEvent("postgres-receipt", RuntimeEventKinds.ActionReceived, receipt, "ledger-run"), 2, cancellationToken: TestContext.Current.CancellationToken);
        }
        await using var reopened = new PostgresSessionStore(connection, options);
        Assert.Equal(2, (await reopened.GetRunCursorAsync("run", cancellationToken: TestContext.Current.CancellationToken)).Revision);
        Assert.Empty(await reopened.ReadPendingOperationsAsync("ledger-run", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            ReceiptStatuses.Succeeded,
            (await reopened.GetOperationAsync("postgres-operation", cancellationToken: TestContext.Current.CancellationToken))!.LatestReceipt!.Status);

        var writers = Enumerable.Range(0, 8)
            .Select(_ => new PostgresSessionStore(connection, options))
            .ToArray();
        try
        {
            var scaleAttempts = writers.Select((writer, index) =>
                writer.AppendAtomicAsync(Event("scale-" + index, "run"), 2).AsTask()).ToArray();
            try { await Task.WhenAll(scaleAttempts); } catch { }
            Assert.Equal(1, scaleAttempts.Count(static task => task.Status == TaskStatus.RanToCompletion));
            Assert.Equal(3, (await reopened.GetRunCursorAsync("run", cancellationToken: TestContext.Current.CancellationToken)).Revision);
        }
        finally
        {
            foreach (var writer in writers) await writer.DisposeAsync();
        }
    }

    private static RuntimeEvent Event(string eventId, string runId) => new()
    {
        EventId = eventId,
        RunId = runId,
        Sequence = 999,
        Kind = "test.event",
        Durability = EventDurabilities.Durable,
        Timestamp = DateTimeOffset.UnixEpoch,
        Payload = Json("""{"value":1}""")
    };

    private static RuntimeEvent Started(string runId)
    {
        var run = new AgentRun
        {
            RunId = runId,
            AgentId = "agent",
            WorldId = "world",
            State = RunStates.Preparing,
            Revision = 1,
            RuntimeGeneration = 1,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
        return new RuntimeEvent
        {
            EventId = "started",
            RunId = runId,
            Sequence = 999,
            Kind = RuntimeEventKinds.RunStarted,
            Durability = EventDurabilities.Durable,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = ProtocolJson.ToElement(run)
        };
    }

    private static RuntimeEvent ActionEvent(string id, string kind, ActionRequest request) => new()
    {
        EventId = id,
        RunId = request.RunId,
        TurnId = request.TurnId,
        Sequence = 999,
        Kind = kind,
        Durability = EventDurabilities.Durable,
        Timestamp = DateTimeOffset.UnixEpoch,
        Payload = ProtocolJson.ToElement(request)
    };

    private static RuntimeEvent ActionEvent(
        string id,
        string kind,
        ActionReceipt receipt,
        string runId = "run") => new()
        {
            EventId = id,
            RunId = runId,
            TurnId = "turn",
            Sequence = 999,
            Kind = kind,
            Durability = EventDurabilities.Durable,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = ProtocolJson.ToElement(receipt)
        };

    private static ActionRequest Request(string operationId, string runId = "run") => new()
    {
        OperationId = operationId,
        RunId = runId,
        TurnId = "turn",
        ToolCallId = "call",
        AgentId = "agent",
        WorldId = "world",
        ActionName = "game.action",
        ActionVersion = "1",
        Arguments = Json("{}"),
        RequestedAt = DateTimeOffset.UnixEpoch
    };

    private static ActionReceipt Receipt(string operationId, string status) => new()
    {
        OperationId = operationId,
        Revision = 1,
        Status = status,
        Result = Json("""{"ok":true}"""),
        Retryable = false,
        ReceivedAt = DateTimeOffset.UnixEpoch.AddSeconds(1)
    };

    private static JsonElement Json(string json) => ProtocolJson.ParseElement(json);

    private static string TempDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "game-agent-relational-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "journal.db");
    }

    private static string ConnectionString(string path) =>
        "Data Source=" + path + ";Pooling=False";

    private static void Delete(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class FileSessionStoreTests
{
    [Fact]
    public async Task AppendAssignsPerRunSequenceAndRevisionAndRecoversThem()
    {
        var path = CreateJournalPath();
        try
        {
            await using (var store = new FileSessionStore(path))
            {
                var first = await store.AppendAtomicAsync(
                    CreateEvent("event-a1", "run-a"),
                    expectedRunRevision: 0);
                var otherRun = await store.AppendAtomicAsync(
                    CreateEvent("event-b1", "run-b"),
                    expectedRunRevision: 0);
                var second = await store.AppendAtomicAsync(
                    CreateEvent("event-a2", "run-a"),
                    expectedRunRevision: 1);

                Assert.Equal(0, first.Sequence);
                Assert.Equal(1, first.Revision);
                Assert.Equal(0, otherRun.Sequence);
                Assert.Equal(1, otherRun.Revision);
                Assert.Equal(1, second.Sequence);
                Assert.Equal(2, second.Revision);

                var events = await store.ReadRunAsync(
                    "run-a",
                    CancellationToken.None);
                Assert.Equal(new long[] { 0, 1 }, events.Select(x => x.Sequence));
            }

            await using (var recovered = new FileSessionStore(path))
            {
                var cursor = await recovered.GetRunCursorAsync("run-a");
                Assert.Equal(2, cursor.NextSequence);
                Assert.Equal(2, cursor.Revision);

                await Assert.ThrowsAsync<RunRevisionConflictException>(
                    () => recovered.AppendAtomicAsync(
                            CreateEvent("event-a3", "run-a"),
                            expectedRunRevision: 1)
                        .AsTask());

                var third = await recovered.AppendAtomicAsync(
                    CreateEvent("event-a3", "run-a"),
                    expectedRunRevision: 2);
                Assert.Equal(2, third.Sequence);
                Assert.Equal(3, third.Revision);
            }
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task ConcurrentAppendsAreSerializedWithoutSequenceGaps()
    {
        var path = CreateJournalPath();
        try
        {
            await using (var store = new FileSessionStore(path))
            {
                var appends = Enumerable.Range(0, 24)
                    .Select(index => store.AppendAtomicAsync(
                            CreateEvent($"event-{index}", "run-1"))
                        .AsTask())
                    .ToArray();
                var results = await Task.WhenAll(appends);

                Assert.Equal(
                    Enumerable.Range(0, results.Length)
                        .Select(value => (long)value),
                    results.Select(item => item.Sequence).OrderBy(x => x));
                Assert.Equal(
                    Enumerable.Range(1, results.Length)
                        .Select(value => (long)value),
                    results.Select(item => item.Revision).OrderBy(x => x));
            }

            await using (var recovered = new FileSessionStore(path))
            {
                var cursor = await recovered.GetRunCursorAsync("run-1");
                Assert.Equal(24, cursor.NextSequence);
                Assert.Equal(24, cursor.Revision);
                Assert.Equal(
                    Enumerable.Range(0, 24).Select(value => (long)value),
                    (await recovered.ReadRunAsync(
                            "run-1",
                            CancellationToken.None))
                        .Select(item => item.Sequence));
            }
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task AppendSnapshotsEventBeforeWaitingForTheWriterGate()
    {
        var path = CreateJournalPath();
        var injector = new BlockingBeforeWriteFaultInjector();
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions { FaultInjector = injector });
            injector.Arm();
            var blocker = Task.Run(
                async () => await store.AppendAtomicAsync(
                    CreateEvent("blocker", "blocking-run")));
            await injector.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            var candidate = CreateEvent(
                "snapshot-event",
                "snapshot-run",
                payload: ProtocolJson.ParseElement("""{"value":7}"""));
            var pending = store.AppendAtomicAsync(
                    candidate,
                    expectedRunRevision: 0)
                .AsTask();
            candidate.EventId = "mutated-event";
            candidate.RunId = "mutated-run";
            candidate.Payload = ProtocolJson.ParseElement("""{"value":99}""");

            injector.Release();
            await blocker;
            await pending;

            var stored = Assert.Single(
                await store.ReadRunAsync(
                    "snapshot-run",
                    CancellationToken.None));
            Assert.Equal("snapshot-event", stored.EventId);
            Assert.Equal(7, stored.Payload.GetProperty("value").GetInt32());
            Assert.Empty(
                await store.ReadRunAsync(
                    "mutated-run",
                    CancellationToken.None));
        }
        finally
        {
            injector.Release();
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task BatchAppendSnapshotsListAndEventsBeforeWaitingForTheWriterGate()
    {
        var path = CreateJournalPath();
        var injector = new BlockingBeforeWriteFaultInjector();
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions { FaultInjector = injector });
            injector.Arm();
            var blocker = Task.Run(
                async () => await store.AppendAtomicAsync(
                    CreateEvent("blocker", "blocking-run")));
            await injector.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            var first = CreateEvent(
                "batch-first",
                "batch-run",
                payload: ProtocolJson.ParseElement("""{"value":1}"""));
            var second = CreateEvent(
                "batch-second",
                "batch-run",
                payload: ProtocolJson.ParseElement("""{"value":2}"""));
            var batch = new[] { first, second };
            var pending = store.AppendAtomicBatchAsync(
                    batch,
                    expectedRunRevision: 0)
                .AsTask();
            batch[0] = CreateEvent("replacement", "mutated-run");
            first.EventId = "mutated-first";
            first.Payload = ProtocolJson.ParseElement("""{"value":91}""");
            second.EventId = "mutated-second";
            second.Payload = ProtocolJson.ParseElement("""{"value":92}""");

            injector.Release();
            await blocker;
            await pending;

            var stored = await store.ReadRunAsync(
                "batch-run",
                CancellationToken.None);
            Assert.Equal(
                new[] { "batch-first", "batch-second" },
                stored.Select(item => item.EventId));
            Assert.Equal(
                new[] { 1, 2 },
                stored.Select(
                    item => item.Payload.GetProperty("value").GetInt32()));
            Assert.Empty(
                await store.ReadRunAsync(
                    "mutated-run",
                    CancellationToken.None));
        }
        finally
        {
            injector.Release();
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task ReceiptReconcileSnapshotsEventBeforeWaitingForTheWriterGate()
    {
        var path = CreateJournalPath();
        var injector = new BlockingBeforeWriteFaultInjector();
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions { FaultInjector = injector });
            var request = CreateActionRequest("snapshot-operation", "receipt-run");
            await store.AppendAtomicAsync(
                CreateEvent(
                    "request-event",
                    "receipt-run",
                    RuntimeEventKinds.ActionRequested,
                    ProtocolJson.ToElement(request)),
                expectedRunRevision: 0);

            injector.Arm();
            var blocker = Task.Run(
                async () => await store.AppendAtomicAsync(
                    CreateEvent("blocker", "blocking-run")));
            await injector.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            var receipt = CreateReceipt(
                request.OperationId,
                revision: 1,
                ReceiptStatuses.Succeeded);
            var candidate = CreateEvent(
                "receipt-event",
                "receipt-run",
                RuntimeEventKinds.ActionReceived,
                ProtocolJson.ToElement(receipt));
            var pending = store.ReconcileReceiptAsync(
                    candidate,
                    expectedRunRevision: 1)
                .AsTask();
            candidate.EventId = "mutated-receipt-event";
            candidate.RunId = "mutated-run";
            candidate.Payload = ProtocolJson.ToElement(
                CreateReceipt(
                    "missing-operation",
                    revision: 9,
                    ReceiptStatuses.Failed));

            injector.Release();
            await blocker;
            var reconciled = await pending;

            Assert.Equal(
                request.OperationId,
                reconciled.Operation.OperationId);
            Assert.Equal(
                ReceiptStatuses.Succeeded,
                reconciled.Operation.LatestReceipt!.Status);
            var stored = await store.ReadRunAsync(
                "receipt-run",
                CancellationToken.None);
            Assert.Contains(
                stored,
                item => item.EventId == "receipt-event");
            Assert.DoesNotContain(
                stored,
                item => item.EventId == "mutated-receipt-event");
        }
        finally
        {
            injector.Release();
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task TornTailIsTruncatedAndTheLastCommittedCursorIsRecovered()
    {
        var path = CreateJournalPath();
        try
        {
            await using (var initial = new FileSessionStore(path))
            {
                _ = await initial.AppendAtomicAsync(
                    CreateEvent("event-1", "run-1"),
                    expectedRunRevision: 0);
            }

            var faultOptions = new FileJournalOptions
            {
                FaultInjector = new PartialFrameFaultInjector()
            };
            await using (var faulted = new FileSessionStore(path, faultOptions))
            {
                await Assert.ThrowsAsync<IOException>(
                    () => faulted.AppendAtomicAsync(
                            CreateEvent("event-2", "run-1"),
                            expectedRunRevision: 1)
                        .AsTask());
                await Assert.ThrowsAsync<JournalFaultedException>(
                    () => faulted.GetRunCursorAsync("run-1").AsTask());
            }

            var tornLength = new FileInfo(path).Length;
            await using (var recovered = new FileSessionStore(path))
            {
                Assert.True(new FileInfo(path).Length < tornLength);
                var cursor = await recovered.GetRunCursorAsync("run-1");
                Assert.Equal(1, cursor.NextSequence);
                Assert.Equal(1, cursor.Revision);

                var retry = await recovered.AppendAtomicAsync(
                    CreateEvent("event-2", "run-1"),
                    expectedRunRevision: 1);
                Assert.False(retry.WasDuplicate);
                Assert.Equal(1, retry.Sequence);
                Assert.Equal(2, retry.Revision);
            }
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task CompleteFrameWithUnknownCommitOutcomeIsIdempotentAfterRecovery()
    {
        var path = CreateJournalPath();
        try
        {
            await using (var initial = new FileSessionStore(path))
            {
                _ = await initial.AppendAtomicAsync(
                    CreateEvent("event-1", "run-1"),
                    expectedRunRevision: 0);
            }

            var uncertainEvent = CreateEvent("event-2", "run-1");
            var faultOptions = new FileJournalOptions
            {
                FaultInjector = new ThrowAfterFullWriteFaultInjector()
            };
            await using (var uncertain = new FileSessionStore(path, faultOptions))
            {
                await Assert.ThrowsAsync<InjectedJournalException>(
                    () => uncertain.AppendAtomicAsync(
                            uncertainEvent,
                            expectedRunRevision: 1)
                        .AsTask());
            }

            await using (var recovered = new FileSessionStore(path))
            {
                var cursor = await recovered.GetRunCursorAsync("run-1");
                Assert.Equal(2, cursor.NextSequence);
                Assert.Equal(2, cursor.Revision);

                var retry = await recovered.AppendAtomicAsync(
                    uncertainEvent,
                    expectedRunRevision: 1);
                Assert.True(retry.WasDuplicate);
                Assert.Equal(1, retry.Sequence);
                Assert.Equal(2, retry.Revision);
            }
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task TornAtomicBatchRecoversNoneOfItsEventsOrOperations()
    {
        var path = CreateJournalPath();
        try
        {
            var firstRequest = CreateActionRequest("operation-1", "run-1");
            var secondRequest = CreateActionRequest("operation-2", "run-1");
            secondRequest.ToolCallId = "call-2";
            var batch = new[]
            {
                CreateEvent(
                    "request-event-1",
                    "run-1",
                    RuntimeEventKinds.ActionRequested,
                    ProtocolJson.ToElement(firstRequest)),
                CreateEvent(
                    "request-event-2",
                    "run-1",
                    RuntimeEventKinds.ActionRequested,
                    ProtocolJson.ToElement(secondRequest))
            };

            await using (var faulted = new FileSessionStore(
                             path,
                             new FileJournalOptions
                             {
                                 FaultInjector = new PartialFrameFaultInjector()
                             }))
            {
                await Assert.ThrowsAsync<IOException>(
                    () => faulted.AppendAtomicBatchAsync(
                            batch,
                            expectedRunRevision: 0)
                        .AsTask());
            }

            await using (var recovered = new FileSessionStore(path))
            {
                Assert.Empty(await recovered.ReadRunAsync("run-1", default));
                Assert.Empty(
                    await recovered.ReadPendingOperationsAsync(
                        "run-1",
                        default));
                var cursor = await recovered.GetRunCursorAsync("run-1");
                Assert.Equal(0, cursor.NextSequence);
                Assert.Equal(0, cursor.Revision);
            }
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task AtomicBatchRecoversEveryEventWithContiguousCursors()
    {
        var path = CreateJournalPath();
        try
        {
            var firstRequest = CreateActionRequest("operation-1", "run-1");
            var secondRequest = CreateActionRequest("operation-2", "run-1");
            secondRequest.ToolCallId = "call-2";
            await using (var store = new FileSessionStore(path))
            {
                var results = await store.AppendAtomicBatchAsync(
                    new[]
                    {
                        CreateEvent(
                            "request-event-1",
                            "run-1",
                            RuntimeEventKinds.ActionRequested,
                            ProtocolJson.ToElement(firstRequest)),
                        CreateEvent(
                            "request-event-2",
                            "run-1",
                            RuntimeEventKinds.ActionRequested,
                            ProtocolJson.ToElement(secondRequest))
                    },
                    expectedRunRevision: 0);
                Assert.Equal(new long[] { 0, 1 }, results.Select(x => x.Sequence));
                Assert.Equal(new long[] { 1, 2 }, results.Select(x => x.Revision));
            }

            await using (var recovered = new FileSessionStore(path))
            {
                Assert.Equal(
                    new long[] { 0, 1 },
                    (await recovered.ReadRunAsync("run-1", default))
                    .Select(item => item.Sequence));
                Assert.Equal(
                    new[] { "operation-1", "operation-2" },
                    (await recovered.ReadPendingOperationsAsync(
                            "run-1",
                            default))
                    .Select(item => item.OperationId)
                    .OrderBy(item => item, StringComparer.Ordinal));
                Assert.Equal(
                    2,
                    (await recovered.GetRunCursorAsync("run-1")).Revision);
            }
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task FullyWrittenAtomicBatchIsIdempotentAfterUnknownOutcome()
    {
        var path = CreateJournalPath();
        try
        {
            var firstRequest = CreateActionRequest("operation-1", "run-1");
            var secondRequest = CreateActionRequest("operation-2", "run-1");
            secondRequest.ToolCallId = "call-2";
            var batch = new[]
            {
                CreateEvent(
                    "request-event-1",
                    "run-1",
                    RuntimeEventKinds.ActionRequested,
                    ProtocolJson.ToElement(firstRequest)),
                CreateEvent(
                    "request-event-2",
                    "run-1",
                    RuntimeEventKinds.ActionRequested,
                    ProtocolJson.ToElement(secondRequest))
            };
            await using (var uncertain = new FileSessionStore(
                             path,
                             new FileJournalOptions
                             {
                                 FaultInjector =
                                     new ThrowAfterFullWriteFaultInjector()
                             }))
            {
                await Assert.ThrowsAsync<InjectedJournalException>(
                    () => uncertain.AppendAtomicBatchAsync(
                            batch,
                            expectedRunRevision: 0)
                        .AsTask());
            }

            await using (var recovered = new FileSessionStore(path))
            {
                var retry = await recovered.AppendAtomicBatchAsync(
                    batch,
                    expectedRunRevision: 0);
                Assert.All(retry, item => Assert.True(item.WasDuplicate));
                Assert.Equal(
                    new long[] { 0, 1 },
                    retry.Select(item => item.Sequence));
                Assert.Equal(
                    new long[] { 1, 2 },
                    retry.Select(item => item.Revision));
                Assert.Equal(
                    2,
                    (await recovered.GetRunCursorAsync("run-1")).Revision);
                Assert.Equal(
                    2,
                    (await recovered.ReadPendingOperationsAsync(
                            "run-1",
                            default)).Count);
            }
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task OperationLedgerDeduplicatesRequestsAndReconcilesReceipts()
    {
        var path = CreateJournalPath();
        try
        {
            var request = CreateActionRequest("operation-1", "run-1");
            await using (var store = new FileSessionStore(path))
            {
                var first = await store.AppendAtomicAsync(
                    CreateEvent(
                        "request-event-1",
                        "run-1",
                        RuntimeEventKinds.ActionRequested,
                        ProtocolJson.ToElement(request)),
                    expectedRunRevision: 0);
                Assert.False(first.WasDuplicate);

                var duplicate = await store.AppendAtomicAsync(
                    CreateEvent(
                        "request-event-2",
                        "run-1",
                        RuntimeEventKinds.ActionRequested,
                        ProtocolJson.ToElement(request)),
                    expectedRunRevision: 0);
                Assert.True(duplicate.WasDuplicate);
                Assert.Equal(first.Sequence, duplicate.Sequence);
                Assert.Equal(first.Revision, duplicate.Revision);

                var pending = Assert.Single(
                    await store.ReadPendingOperationsAsync("run-1"));
                Assert.Equal(request.OperationId, pending.OperationId);
                Assert.Null(pending.LatestReceipt);

                var unknown = CreateReceipt(
                    request.OperationId,
                    revision: 0,
                    ReceiptStatuses.Unknown);
                var unknownResult = await store.ReconcileReceiptAsync(
                    CreateEvent(
                        "receipt-event-0",
                        "run-1",
                        RuntimeEventKinds.ActionReceived,
                        ProtocolJson.ToElement(unknown)),
                    expectedRunRevision: 1);
                Assert.True(unknownResult.Operation.IsPending);

                var duplicateUnknown = await store.ReconcileReceiptAsync(
                    CreateEvent(
                        "receipt-event-0",
                        "run-1",
                        RuntimeEventKinds.ActionReceived,
                        ProtocolJson.ToElement(
                            CopyReceiptWithLaterReceiveTime(unknown))),
                    expectedRunRevision: 1);
                Assert.True(duplicateUnknown.Append.WasDuplicate);

                var succeeded = CreateReceipt(
                    request.OperationId,
                    revision: 1,
                    ReceiptStatuses.Succeeded);
                var final = await store.ReconcileReceiptAsync(
                    CreateEvent(
                        "receipt-event-1",
                        "run-1",
                        RuntimeEventKinds.ActionReceived,
                        ProtocolJson.ToElement(succeeded)),
                    expectedRunRevision: 2);
                Assert.False(final.Operation.IsPending);
                Assert.Equal(3, final.Append.Revision);
                Assert.Empty(
                    await store.ReadPendingOperationsAsync("run-1"));
            }

            await using (var recovered = new FileSessionStore(path))
            {
                var operation = await recovered.GetOperationAsync(
                    request.OperationId);
                Assert.NotNull(operation);
                Assert.False(operation.IsPending);
                Assert.Equal(
                    ReceiptStatuses.Succeeded,
                    operation.LatestReceipt!.Status);
                Assert.Equal(1, operation.LatestReceipt.Revision);
                Assert.Equal(3, (await recovered.GetRunCursorAsync("run-1")).Revision);
            }
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task ReceiptLifecycleBatchDeduplicatesOnlyReceiveTimeChanges()
    {
        var path = CreateJournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var request = CreateActionRequest("operation-1", "run-1");
            _ = await store.AppendAtomicAsync(
                CreateEvent(
                    "request-event",
                    "run-1",
                    RuntimeEventKinds.ActionRequested,
                    ProtocolJson.ToElement(request)),
                expectedRunRevision: 0);
            var receipt = CreateReceipt(
                request.OperationId,
                revision: 0,
                ReceiptStatuses.Succeeded);
            var original = new[]
            {
                CreateEvent(
                    "receipt-event",
                    "run-1",
                    RuntimeEventKinds.ActionReceived,
                    ProtocolJson.ToElement(receipt)),
                CreateEvent(
                    "tool-completed-event",
                    "run-1",
                    RuntimeEventKinds.ToolCompleted,
                    ProtocolJson.ToElement(receipt))
            };

            var first = await store.AppendAtomicBatchAsync(
                original,
                expectedRunRevision: 1);

            Assert.All(first, item => Assert.False(item.WasDuplicate));
            var receivedLater = CopyReceiptWithLaterReceiveTime(receipt);
            var duplicate = await store.AppendAtomicBatchAsync(
                new[]
                {
                    CreateEvent(
                        "receipt-event",
                        "run-1",
                        RuntimeEventKinds.ActionReceived,
                        ProtocolJson.ToElement(receivedLater)),
                    CreateEvent(
                        "tool-completed-event",
                        "run-1",
                        RuntimeEventKinds.ToolCompleted,
                        ProtocolJson.ToElement(receivedLater))
                },
                expectedRunRevision: 1);

            Assert.All(duplicate, item => Assert.True(item.WasDuplicate));
            Assert.Equal(
                first.Select(item => item.Sequence),
                duplicate.Select(item => item.Sequence));
            Assert.Equal(
                first.Select(item => item.Revision),
                duplicate.Select(item => item.Revision));

            var conflicting = CopyReceiptWithLaterReceiveTime(receipt);
            conflicting.Result = ProtocolJson.ParseElement("""{"ok":false}""");
            await Assert.ThrowsAsync<JournalEntryConflictException>(
                () => store.AppendAtomicBatchAsync(
                        new[]
                        {
                            CreateEvent(
                                "receipt-event",
                                "run-1",
                                RuntimeEventKinds.ActionReceived,
                                ProtocolJson.ToElement(conflicting)),
                            CreateEvent(
                                "tool-completed-event",
                                "run-1",
                                RuntimeEventKinds.ToolCompleted,
                                ProtocolJson.ToElement(conflicting))
                        },
                        expectedRunRevision: 3)
                    .AsTask());
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task ConflictingDuplicateOperationAndReceiptRevisionAreRejected()
    {
        var path = CreateJournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var request = CreateActionRequest("operation-1", "run-1");
            _ = await store.AppendAtomicAsync(
                CreateEvent(
                    "request-event",
                    "run-1",
                    RuntimeEventKinds.ActionRequested,
                    ProtocolJson.ToElement(request)),
                expectedRunRevision: 0);

            var conflictingRequest = CreateActionRequest(
                "operation-1",
                "run-1",
                """{"target":"other"}""");
            await Assert.ThrowsAsync<OperationLedgerConflictException>(
                () => store.AppendAtomicAsync(
                        CreateEvent(
                            "conflicting-request",
                            "run-1",
                            RuntimeEventKinds.ActionRequested,
                            ProtocolJson.ToElement(conflictingRequest)),
                        expectedRunRevision: 1)
                    .AsTask());

            var unknown = CreateReceipt(
                "operation-1",
                revision: 0,
                ReceiptStatuses.Unknown);
            _ = await store.ReconcileReceiptAsync(
                CreateEvent(
                    "receipt-event",
                    "run-1",
                    RuntimeEventKinds.ActionReceived,
                    ProtocolJson.ToElement(unknown)),
                expectedRunRevision: 1);

            var conflictingReceipt = CreateReceipt(
                "operation-1",
                revision: 0,
                ReceiptStatuses.Failed);
            await Assert.ThrowsAsync<OperationLedgerConflictException>(
                () => store.ReconcileReceiptAsync(
                        CreateEvent(
                            "receipt-event",
                            "run-1",
                            RuntimeEventKinds.ActionReceived,
                            ProtocolJson.ToElement(conflictingReceipt)),
                        expectedRunRevision: 2)
                    .AsTask());

            await Assert.ThrowsAsync<OperationLedgerConflictException>(
                () => store.ReconcileReceiptAsync(
                        CreateEvent(
                            "conflicting-receipt",
                            "run-1",
                            RuntimeEventKinds.ActionReceived,
                            ProtocolJson.ToElement(conflictingReceipt)),
                        expectedRunRevision: 2)
                    .AsTask());

            var succeeded = CreateReceipt(
                "operation-1",
                revision: 1,
                ReceiptStatuses.Succeeded);
            _ = await store.ReconcileReceiptAsync(
                CreateEvent(
                    "terminal-receipt",
                    "run-1",
                    RuntimeEventKinds.ActionReceived,
                    ProtocolJson.ToElement(succeeded)),
                expectedRunRevision: 2);

            var regressing = CreateReceipt(
                "operation-1",
                revision: 2,
                ReceiptStatuses.Unknown);
            await Assert.ThrowsAsync<OperationLedgerConflictException>(
                () => store.ReconcileReceiptAsync(
                        CreateEvent(
                            "regressing-receipt",
                            "run-1",
                            RuntimeEventKinds.ActionReceived,
                            ProtocolJson.ToElement(regressing)),
                        expectedRunRevision: 3)
                    .AsTask());
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("stream")]
    [InlineData("provider")]
    [InlineData("turn")]
    public async Task ProviderLifecycleDuplicateRequiresFullIdentity(
        string changedIdentity)
    {
        var path = CreateJournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var original = CreateEvent(
                "provider-dispatch-1",
                "run-1",
                RuntimeEventKinds.ProviderDispatchStarted);
            original.AttemptId = "attempt-1";
            original.StreamAttemptId = "stream-1";
            original.ProviderId = "provider-1";
            original.TurnId = "turn-1";
            _ = await store.AppendAtomicAsync(
                original,
                expectedRunRevision: 0);

            var conflicting = ProtocolJson.DeserializeRuntimeEvent(
                ProtocolJson.Serialize(original));
            switch (changedIdentity)
            {
                case "attempt":
                    conflicting.AttemptId = "attempt-2";
                    break;
                case "stream":
                    conflicting.StreamAttemptId = "stream-2";
                    break;
                case "provider":
                    conflicting.ProviderId = "provider-2";
                    break;
                case "turn":
                    conflicting.TurnId = "turn-2";
                    break;
            }

            await Assert.ThrowsAsync<JournalEntryConflictException>(
                () => store.AppendAtomicAsync(
                        conflicting,
                        expectedRunRevision: 1)
                    .AsTask());
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task TranscriptDuplicateAllowsAttemptIdentityRebinding()
    {
        var path = CreateJournalPath();
        try
        {
            await using var store = new FileSessionStore(path);
            var original = CreateEvent(
                "transcript-1",
                "run-1",
                RuntimeEventKinds.TranscriptMessage);
            original.AttemptId = "attempt-1";
            original.StreamAttemptId = "stream-1";
            var first = await store.AppendAtomicAsync(
                original,
                expectedRunRevision: 0);

            var rebound = ProtocolJson.DeserializeRuntimeEvent(
                ProtocolJson.Serialize(original));
            rebound.AttemptId = "attempt-2";
            rebound.StreamAttemptId = "stream-2";
            var duplicate = await store.AppendAtomicAsync(
                rebound,
                expectedRunRevision: 1);

            Assert.True(duplicate.WasDuplicate);
            Assert.Equal(first.Sequence, duplicate.Sequence);
            Assert.Equal(first.Revision, duplicate.Revision);
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    [Fact]
    public async Task ChecksumFailureInCommittedFrameIsNotSilentlyTruncated()
    {
        var path = CreateJournalPath();
        try
        {
            await using (var store = new FileSessionStore(path))
            {
                _ = await store.AppendAtomicAsync(
                    CreateEvent("event-1", "run-1"),
                    expectedRunRevision: 0);
                _ = await store.AppendAtomicAsync(
                    CreateEvent("event-2", "run-1"),
                    expectedRunRevision: 1);
            }

            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                stream.Position = 20;
                var original = stream.ReadByte();
                Assert.NotEqual(-1, original);
                stream.Position = 20;
                stream.WriteByte((byte)(original ^ 0x01));
                stream.Flush(flushToDisk: true);
            }

            Assert.Throws<JournalCorruptionException>(
                () => new FileSessionStore(path));
        }
        finally
        {
            DeleteJournalDirectory(path);
        }
    }

    private static RuntimeEvent CreateEvent(
        string eventId,
        string runId,
        string kind = "test.event",
        JsonElement? payload = null)
    {
        return new RuntimeEvent
        {
            EventId = eventId,
            RunId = runId,
            Sequence = 999,
            Kind = kind,
            Durability = EventDurabilities.Durable,
            Timestamp = new DateTimeOffset(
                2026,
                7,
                28,
                0,
                0,
                0,
                TimeSpan.Zero),
            Payload = payload?.Clone()
                ?? ProtocolJson.ParseElement("""{"value":1}""")
        };
    }

    private static ActionRequest CreateActionRequest(
        string operationId,
        string runId,
        string arguments = """{"target":"berries"}""")
    {
        return new ActionRequest
        {
            OperationId = operationId,
            RunId = runId,
            TurnId = "turn-1",
            ToolCallId = "call-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            ActionName = "gather",
            ActionVersion = "1.0.0",
            Arguments = ProtocolJson.ParseElement(arguments),
            RequestedAt = new DateTimeOffset(
                2026,
                7,
                28,
                0,
                0,
                0,
                TimeSpan.Zero)
        };
    }

    private static ActionReceipt CreateReceipt(
        string operationId,
        long revision,
        string status)
    {
        return new ActionReceipt
        {
            OperationId = operationId,
            Revision = revision,
            Status = status,
            Result = ProtocolJson.ParseElement("""{"ok":true}"""),
            Retryable = false,
            ReceivedAt = new DateTimeOffset(
                2026,
                7,
                28,
                0,
                0,
                1,
                TimeSpan.Zero)
        };
    }

    private static ActionReceipt CopyReceiptWithLaterReceiveTime(
        ActionReceipt receipt)
    {
        var copy = ProtocolJson.DeserializeActionReceipt(
            ProtocolJson.Serialize(receipt));
        copy.ReceivedAt = copy.ReceivedAt.AddSeconds(5);
        return copy;
    }

    private static string CreateJournalPath()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-persistence-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return System.IO.Path.Combine(directory, "sessions.gaj");
    }

    private static void DeleteJournalDirectory(string journalPath)
    {
        var directory = System.IO.Path.GetDirectoryName(journalPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class PartialFrameFaultInjector : IJournalFaultInjector
    {
        public int GetWriteLength(int frameLength)
        {
            return Math.Max(1, frameLength / 2);
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
        }
    }

    private sealed class BlockingBeforeWriteFaultInjector
        : IJournalFaultInjector
    {
        private readonly ManualResetEventSlim _release =
            new(initialState: false);
        private TaskCompletionSource<bool> _entered =
            NewSignal();
        private int _armed;

        public Task Entered => _entered.Task;

        public void Arm()
        {
            _entered = NewSignal();
            _release.Reset();
            Volatile.Write(ref _armed, 1);
        }

        public void Release()
        {
            _release.Set();
        }

        public int GetWriteLength(int frameLength)
        {
            return frameLength;
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
            _ = bytesWritten;
            _ = frameLength;
            if (stage != JournalWriteStage.BeforeWrite
                || Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            _entered.TrySetResult(true);
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "The writer-gate test did not release the blocked write.");
            }
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class ThrowAfterFullWriteFaultInjector
        : IJournalFaultInjector
    {
        public int GetWriteLength(int frameLength)
        {
            return frameLength;
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
            if (stage == JournalWriteStage.AfterWrite)
            {
                throw new InjectedJournalException();
            }
        }
    }

    private sealed class InjectedJournalException : IOException
    {
    }
}

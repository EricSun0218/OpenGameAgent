using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class PersistenceCapacityTests
{
    [Fact]
    public async Task JournalEventLimitsAllowExactBoundaryAndRejectNextWrite()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions
                {
                    MaxTotalCommittedEvents = 2,
                    MaxEventsPerRun = 2
                });
            await store.AppendAtomicAsync(Event("event-1", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
            await store.AppendAtomicAsync(Event("event-2", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
            var beforeLength = new FileInfo(path).Length;
            var beforeCursor = await store.GetRunCursorAsync("run-1", cancellationToken: TestContext.Current.CancellationToken);

            var error = await Assert.ThrowsAsync<
                JournalCapacityExceededException>(
                () => store.AppendAtomicAsync(
                        Event("event-3", "run-1"), cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask());

            Assert.Equal(
                nameof(FileJournalOptions.MaxTotalCommittedEvents),
                error.LimitName);
            Assert.Equal(2, error.Limit);
            Assert.Equal(3, error.Attempted);
            Assert.Equal(beforeLength, new FileInfo(path).Length);
            await AssertCursorAsync(store, "run-1", beforeCursor);
            Assert.Equal(
                2,
                (await store.ReadRunAsync("run-1", TestContext.Current.CancellationToken)).Count);

            var duplicate = await store.AppendAtomicAsync(
                Event("event-2", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(duplicate.WasDuplicate);
            Assert.Equal(beforeLength, new FileInfo(path).Length);
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task JournalTotalLimitCountsEventsAcrossRuns()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions
                {
                    MaxTotalCommittedEvents = 3,
                    MaxEventsPerRun = 2
                });
            await store.AppendAtomicAsync(Event("event-a1", "run-a"), cancellationToken: TestContext.Current.CancellationToken);
            await store.AppendAtomicAsync(Event("event-a2", "run-a"), cancellationToken: TestContext.Current.CancellationToken);
            await store.AppendAtomicAsync(Event("event-b1", "run-b"), cancellationToken: TestContext.Current.CancellationToken);
            var beforeLength = new FileInfo(path).Length;
            var runACursor = await store.GetRunCursorAsync("run-a", cancellationToken: TestContext.Current.CancellationToken);
            var runBCursor = await store.GetRunCursorAsync("run-b", cancellationToken: TestContext.Current.CancellationToken);

            var error = await Assert.ThrowsAsync<
                JournalCapacityExceededException>(
                () => store.AppendAtomicAsync(
                        Event("event-b2", "run-b"), cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask());

            Assert.Equal(
                nameof(FileJournalOptions.MaxTotalCommittedEvents),
                error.LimitName);
            Assert.Equal(beforeLength, new FileInfo(path).Length);
            await AssertCursorAsync(store, "run-a", runACursor);
            await AssertCursorAsync(store, "run-b", runBCursor);
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task JournalPerRunLimitDoesNotConsumeOtherRunCapacity()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions
                {
                    MaxTotalCommittedEvents = 4,
                    MaxEventsPerRun = 2
                });
            await store.AppendAtomicAsync(Event("event-a1", "run-a"), cancellationToken: TestContext.Current.CancellationToken);
            await store.AppendAtomicAsync(Event("event-a2", "run-a"), cancellationToken: TestContext.Current.CancellationToken);
            var beforeLength = new FileInfo(path).Length;

            var error = await Assert.ThrowsAsync<
                JournalCapacityExceededException>(
                () => store.AppendAtomicAsync(
                        Event("event-a3", "run-a"), cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask());

            Assert.Equal(
                nameof(FileJournalOptions.MaxEventsPerRun),
                error.LimitName);
            Assert.Equal(beforeLength, new FileInfo(path).Length);

            await store.AppendAtomicAsync(Event("event-b1", "run-b"), cancellationToken: TestContext.Current.CancellationToken);
            await store.AppendAtomicAsync(Event("event-b2", "run-b"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(
                2,
                (await store.ReadRunAsync("run-a", TestContext.Current.CancellationToken)).Count);
            Assert.Equal(
                2,
                (await store.ReadRunAsync("run-b", TestContext.Current.CancellationToken)).Count);
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task JournalBatchCapacityFailureHasNoDurableSideEffects()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions
                {
                    MaxTotalCommittedEvents = 2,
                    MaxEventsPerRun = 3
                });
            await store.AppendAtomicAsync(Event("event-1", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
            var beforeLength = new FileInfo(path).Length;
            var beforeCursor = await store.GetRunCursorAsync("run-1", cancellationToken: TestContext.Current.CancellationToken);

            var error = await Assert.ThrowsAsync<
                JournalCapacityExceededException>(
                () => store.AppendAtomicBatchAsync(
                        new[]
                        {
                            Event("event-2", "run-1"),
                            Event("event-3", "run-1")
                        },
                        expectedRunRevision: 1, cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask());

            Assert.Equal(
                nameof(FileJournalOptions.MaxTotalCommittedEvents),
                error.LimitName);
            Assert.Equal(beforeLength, new FileInfo(path).Length);
            await AssertCursorAsync(store, "run-1", beforeCursor);
            Assert.Single(await store.ReadRunAsync("run-1", TestContext.Current.CancellationToken));

            await store.AppendAtomicAsync(
                Event("event-2", "run-1"),
                expectedRunRevision: 1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(
                2,
                (await store.ReadRunAsync("run-1", TestContext.Current.CancellationToken)).Count);
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task JournalBatchPayloadIsBoundedBeforeFrameMaterialization()
    {
        var path = JournalPath();
        var first = Event("event-a", "run-1");
        var second = Event("event-b", "run-1");
        var serializedEventBytes = Encoding.UTF8.GetByteCount(
            ProtocolJson.Serialize(first));
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions
                {
                    MaxFramePayloadBytes =
                        checked(serializedEventBytes * 2 - 1)
                });

            var error = await Assert.ThrowsAsync<
                JournalCapacityExceededException>(
                () => store.AppendAtomicBatchAsync(
                        new[] { first, second },
                        expectedRunRevision: 0, cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask());

            Assert.Equal(
                nameof(FileJournalOptions.MaxFramePayloadBytes),
                error.LimitName);
            Assert.Equal(0, new FileInfo(path).Length);
            var cursor = await store.GetRunCursorAsync("run-1", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, cursor.NextSequence);
            Assert.Equal(0, cursor.Revision);
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task JournalFramePayloadLimitAllowsExactBoundaryAndRejectsLowerReopen()
    {
        var measurementPath = JournalPath();
        var path = JournalPath();
        var rejectedWritePath = JournalPath();
        try
        {
            await using (var measurement = new FileSessionStore(
                             measurementPath))
            {
                await measurement.AppendAtomicAsync(
                    Event("event-a", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
            }

            var payloadLength = ReadFirstFramePayloadLength(
                measurementPath);
            await using (var store = new FileSessionStore(
                             path,
                             new FileJournalOptions
                             {
                                 MaxFramePayloadBytes = payloadLength
                             }))
            {
                await store.AppendAtomicAsync(Event("event-a", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
            }

            var beforeLength = new FileInfo(path).Length;
            Assert.Equal(payloadLength, ReadFirstFramePayloadLength(path));
            var reopenError =
                Assert.Throws<JournalCapacityExceededException>(
                    () => new FileSessionStore(
                        path,
                        new FileJournalOptions
                        {
                            MaxFramePayloadBytes = payloadLength - 1
                        }));
            Assert.Equal(
                nameof(FileJournalOptions.MaxFramePayloadBytes),
                reopenError.LimitName);
            Assert.Equal(payloadLength - 1, reopenError.Limit);
            Assert.Equal(payloadLength, reopenError.Attempted);
            Assert.Equal(beforeLength, new FileInfo(path).Length);

            await using (var rejectedStore = new FileSessionStore(
                             rejectedWritePath,
                             new FileJournalOptions
                             {
                                 MaxFramePayloadBytes = payloadLength - 1
                             }))
            {
                var writeError = await Assert.ThrowsAsync<
                    JournalCapacityExceededException>(
                    () => rejectedStore.AppendAtomicAsync(
                            Event("event-a", "run-1"), cancellationToken: TestContext.Current.CancellationToken)
                        .AsTask());
                Assert.Equal(
                    nameof(FileJournalOptions.MaxFramePayloadBytes),
                    writeError.LimitName);
                Assert.True(writeError.Attempted > writeError.Limit);
                Assert.Equal(
                    0,
                    new FileInfo(rejectedWritePath).Length);
            }

            await using var recovered = new FileSessionStore(path);
            Assert.Single(await recovered.ReadRunAsync("run-1", TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteContainingDirectory(measurementPath);
            DeleteContainingDirectory(path);
            DeleteContainingDirectory(rejectedWritePath);
        }
    }

    [Fact]
    public async Task OversizedJournalSnapshotsAreRejectedBeforeFullMaterialization()
    {
        const int maximumFramePayloadBytes = 1_024;
        const long maximumAppendAllocationBytes = 192 * 1_024;
        var path = JournalPath();
        var largePayload = Json(
            "{\"value\":\"" + new string('x', 120_000) + "\"}");
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions
                {
                    MaxFramePayloadBytes = maximumFramePayloadBytes
                });

            _ = await Assert.ThrowsAsync<
                JournalCapacityExceededException>(
                () => store.AppendAtomicAsync(
                        EventWithPayload(
                            "warm-event",
                            "warm-run",
                            largePayload), cancellationToken: TestContext.Current.CancellationToken)
                    .AsTask());

            var singleEvent = EventWithPayload(
                "single-event",
                "single-run",
                largePayload);
            var beforeSingle = GC.GetAllocatedBytesForCurrentThread();
            var singleAppend = store.AppendAtomicAsync(singleEvent, cancellationToken: TestContext.Current.CancellationToken);
            var singleAllocation =
                GC.GetAllocatedBytesForCurrentThread() - beforeSingle;
            Assert.True(singleAppend.IsCompleted);
            var singleError = await Assert.ThrowsAsync<
                JournalCapacityExceededException>(
                () => singleAppend.AsTask());
            Assert.Equal(
                nameof(FileJournalOptions.MaxFramePayloadBytes),
                singleError.LimitName);
            Assert.InRange(
                singleAllocation,
                0,
                maximumAppendAllocationBytes);

            var batch = new[]
            {
                EventWithPayload(
                    "batch-event-a",
                    "batch-run",
                    largePayload),
                Event("batch-event-b", "batch-run")
            };
            var beforeBatch = GC.GetAllocatedBytesForCurrentThread();
            var batchAppend = store.AppendAtomicBatchAsync(batch, cancellationToken: TestContext.Current.CancellationToken);
            var batchAllocation =
                GC.GetAllocatedBytesForCurrentThread() - beforeBatch;
            Assert.True(batchAppend.IsCompleted);
            var batchError = await Assert.ThrowsAsync<
                JournalCapacityExceededException>(
                () => batchAppend.AsTask());
            Assert.Equal(
                nameof(FileJournalOptions.MaxFramePayloadBytes),
                batchError.LimitName);
            Assert.InRange(
                batchAllocation,
                0,
                maximumAppendAllocationBytes);

            Assert.Equal(0, new FileInfo(path).Length);
            Assert.Equal(0, RegisteredRunStreamCount(store));
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task FailedJournalWritesDoNotRegisterEmptyRunStreams()
    {
        var path = JournalPath();
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions
                {
                    MaxTotalCommittedEvents = 2,
                    MaxEventsPerRun = 2
                });
            await store.AppendAtomicBatchAsync(
                new[]
                {
                    Event("committed-a", "committed-run"),
                    Event("committed-b", "committed-run")
                }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, RegisteredRunStreamCount(store));

            for (var index = 0; index < 8; index++)
            {
                var runId = $"rejected-run-{index}";
                _ = await Assert.ThrowsAsync<
                    JournalCapacityExceededException>(
                    () => store.AppendAtomicAsync(
                            Event($"rejected-single-{index}", runId), cancellationToken: TestContext.Current.CancellationToken)
                        .AsTask());
                _ = await Assert.ThrowsAsync<
                    JournalCapacityExceededException>(
                    () => store.AppendAtomicBatchAsync(
                            new[]
                            {
                                Event($"rejected-batch-a-{index}", runId),
                                Event($"rejected-batch-b-{index}", runId)
                            }, cancellationToken: TestContext.Current.CancellationToken)
                        .AsTask());
                _ = await Assert.ThrowsAsync<
                    OperationLedgerConflictException>(
                    () => store.ReconcileReceiptAsync(
                            MissingReceiptEvent(
                                $"rejected-receipt-{index}",
                                runId,
                                $"missing-operation-{index}"), cancellationToken: TestContext.Current.CancellationToken)
                        .AsTask());
            }

            Assert.Equal(1, RegisteredRunStreamCount(store));
            Assert.Equal(
                2,
                (await store.ReadRunAsync(
                    "committed-run",
                    TestContext.Current.CancellationToken)).Count);
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task JournalByteLimitAllowsExactFrameAndRejectsNextFrame()
    {
        var measurementPath = JournalPath();
        var path = JournalPath();
        try
        {
            await using (var measurement = new FileSessionStore(measurementPath))
            {
                await measurement.AppendAtomicAsync(
                    Event("event-a", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
            }

            var exactFrameLength = new FileInfo(measurementPath).Length;
            await using (var store = new FileSessionStore(
                             path,
                             new FileJournalOptions
                             {
                                 MaxJournalBytes = exactFrameLength
                             }))
            {
                await store.AppendAtomicAsync(Event("event-a", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(exactFrameLength, new FileInfo(path).Length);
                var beforeCursor = await store.GetRunCursorAsync("run-1", cancellationToken: TestContext.Current.CancellationToken);

                var error = await Assert.ThrowsAsync<
                    JournalCapacityExceededException>(
                    () => store.AppendAtomicAsync(
                            Event("event-b", "run-1"), cancellationToken: TestContext.Current.CancellationToken)
                        .AsTask());

                Assert.Equal(
                    nameof(FileJournalOptions.MaxJournalBytes),
                    error.LimitName);
                Assert.Equal(exactFrameLength, error.Limit);
                Assert.True(error.Attempted > exactFrameLength);
                Assert.Equal(exactFrameLength, new FileInfo(path).Length);
                await AssertCursorAsync(store, "run-1", beforeCursor);
            }

            await using var recovered = new FileSessionStore(
                path,
                new FileJournalOptions
                {
                    MaxJournalBytes = exactFrameLength
                });
            Assert.Single(await recovered.ReadRunAsync("run-1", TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteContainingDirectory(measurementPath);
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task JournalRestartRejectsEachExceededCapacityWithoutRewriting()
    {
        var path = JournalPath();
        try
        {
            await using (var store = new FileSessionStore(path))
            {
                await store.AppendAtomicAsync(Event("event-1", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
                await store.AppendAtomicAsync(Event("event-2", "run-1"), cancellationToken: TestContext.Current.CancellationToken);
            }

            var beforeLength = new FileInfo(path).Length;
            var totalError = Assert.Throws<JournalCapacityExceededException>(
                () => new FileSessionStore(
                    path,
                    new FileJournalOptions
                    {
                        MaxTotalCommittedEvents = 1,
                        MaxEventsPerRun = 2
                    }));
            Assert.Equal(
                nameof(FileJournalOptions.MaxTotalCommittedEvents),
                totalError.LimitName);

            var runError = Assert.Throws<JournalCapacityExceededException>(
                () => new FileSessionStore(
                    path,
                    new FileJournalOptions
                    {
                        MaxTotalCommittedEvents = 2,
                        MaxEventsPerRun = 1
                    }));
            Assert.Equal(
                nameof(FileJournalOptions.MaxEventsPerRun),
                runError.LimitName);

            var byteError = Assert.Throws<JournalCapacityExceededException>(
                () => new FileSessionStore(
                    path,
                    new FileJournalOptions
                    {
                        MaxJournalBytes = beforeLength - 1
                    }));
            Assert.Equal(
                nameof(FileJournalOptions.MaxJournalBytes),
                byteError.LimitName);
            Assert.Equal(beforeLength, new FileInfo(path).Length);

            await using var recovered = new FileSessionStore(path);
            Assert.Equal(
                2,
                (await recovered.ReadRunAsync("run-1", TestContext.Current.CancellationToken)).Count);
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task RunRecoveryLimitRejectsBeforeLedgerOrJournalSideEffects()
    {
        var events = new[]
        {
            Event("event-1", "run-1", sequence: 0),
            Event("event-2", "run-1", sequence: 1)
        };
        await using var store = new CapacityStore(events);
        using var journal = new JournalCoordinator(
            store,
            store,
            new SystemRuntimeClock(),
            new GuidRuntimeIdGenerator());
        var recovery = new RunRecovery(
            store,
            store,
            journal,
            new RunRecoveryOptions { MaxEventsPerRun = 1 });

        var error = await Assert.ThrowsAsync<
            RunRecoveryCapacityExceededException>(
            () => recovery.LoadAsync("run-1", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("run-1", error.RunId);
        Assert.Equal(1, error.Limit);
        Assert.Equal(2, error.Attempted);
        Assert.Equal(0, store.PendingReadCount);
        Assert.Equal(0, store.AppendCount);
    }

    [Fact]
    public async Task RunRecoveryAllowsExactConfiguredEventBoundary()
    {
        var run = new AgentRun
        {
            RunId = "run-1",
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Running,
            Revision = 1,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
        var checkpoint = new RuntimeEvent
        {
            EventId = "checkpoint-1",
            RunId = run.RunId,
            Sequence = 0,
            Kind = RuntimeEventKinds.RunStarted,
            Durability = EventDurabilities.Durable,
            Timestamp = DateTimeOffset.UnixEpoch,
            RuntimeGeneration = run.RuntimeGeneration,
            Payload = ProtocolJson.ToElement(run)
        };
        await using var store = new CapacityStore(new[] { checkpoint });
        using var journal = new JournalCoordinator(
            store,
            store,
            new SystemRuntimeClock(),
            new GuidRuntimeIdGenerator());
        var recovery = new RunRecovery(
            store,
            store,
            journal,
            new RunRecoveryOptions { MaxEventsPerRun = 1 });

        var recovered = await recovery.LoadAsync("run-1", TestContext.Current.CancellationToken);

        Assert.NotNull(recovered);
        Assert.Equal(1, recovered.Run.Revision);
        Assert.Equal(1, store.PendingReadCount);
        Assert.Equal(0, store.AppendCount);
    }

    [Fact]
    public async Task MemoryMutationLimitAllowsExactBoundaryAndRejectsNextFrame()
    {
        var path = MemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(
                             path,
                             new FileMemoryStoreOptions
                             {
                                 MaxMutationFrames = 2
                             }))
            {
                await store.UpsertAsync(
                    Record("memory-a"),
                    CancellationToken.None);
                Assert.True(
                    await store.DeleteAsync(
                        "memory-a",
                        CancellationToken.None));
                var beforeLength = new FileInfo(path).Length;

                var unchanged = await store.DeleteAtomicAsync(
                    "missing",
                    expectedRevision: 2, cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(unchanged.Changed);
                Assert.Equal(2, unchanged.Revision);

                var error = await Assert.ThrowsAsync<
                    MemoryStoreCapacityExceededException>(
                    () => store.UpsertAsync(
                            Record("memory-b"),
                            CancellationToken.None)
                        .AsTask());

                Assert.Equal(
                    nameof(FileMemoryStoreOptions.MaxMutationFrames),
                    error.LimitName);
                Assert.Equal(2, error.Limit);
                Assert.Equal(3, error.Attempted);
                Assert.Equal(2, store.Revision);
                Assert.Equal(beforeLength, new FileInfo(path).Length);
                Assert.Empty(
                    await store.SearchAsync(
                        new MemoryQuery("shared", Json("{}")),
                        TestContext.Current.CancellationToken));
            }

            await using var recovered = new FileMemoryStore(
                path,
                new FileMemoryStoreOptions
                {
                    MaxMutationFrames = 2
                });
            Assert.Equal(2, recovered.Revision);
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task MemoryByteLimitAllowsExactFrameAndRejectsNextFrame()
    {
        var measurementPath = MemoryPath();
        var path = MemoryPath();
        try
        {
            await using (var measurement = new FileMemoryStore(
                             measurementPath))
            {
                await measurement.UpsertAsync(
                    Record("memory-a"),
                    CancellationToken.None);
            }

            var exactFrameLength = new FileInfo(measurementPath).Length;
            await using (var store = new FileMemoryStore(
                             path,
                             new FileMemoryStoreOptions
                             {
                                 MaxLogBytes = exactFrameLength
                             }))
            {
                await store.UpsertAsync(
                    Record("memory-a"),
                    CancellationToken.None);
                Assert.Equal(exactFrameLength, new FileInfo(path).Length);

                var error = await Assert.ThrowsAsync<
                    MemoryStoreCapacityExceededException>(
                    () => store.UpsertAsync(
                            Record("memory-b"),
                            CancellationToken.None)
                        .AsTask());

                Assert.Equal(
                    nameof(FileMemoryStoreOptions.MaxLogBytes),
                    error.LimitName);
                Assert.Equal(exactFrameLength, error.Limit);
                Assert.True(error.Attempted > exactFrameLength);
                Assert.Equal(1, store.Revision);
                Assert.Equal(exactFrameLength, new FileInfo(path).Length);
                Assert.Equal(
                    "memory-a",
                    Assert.Single(
                            await store.SearchAsync(
                                new MemoryQuery("shared", Json("{}")),
                                TestContext.Current.CancellationToken))
                        .Record.MemoryId);
            }

            await using var recovered = new FileMemoryStore(
                path,
                new FileMemoryStoreOptions
                {
                    MaxLogBytes = exactFrameLength
                });
            Assert.Equal(1, recovered.Revision);
        }
        finally
        {
            DeleteContainingDirectory(measurementPath);
            DeleteContainingDirectory(path);
        }
    }

    [Fact]
    public async Task MemoryFramePayloadLimitAllowsExactBoundaryAndRejectsLowerReopen()
    {
        var measurementPath = MemoryPath();
        var path = MemoryPath();
        var rejectedWritePath = MemoryPath();
        try
        {
            await using (var measurement = new FileMemoryStore(
                             measurementPath))
            {
                await measurement.UpsertAsync(
                    Record("memory-a"),
                    CancellationToken.None);
            }

            var payloadLength = ReadFirstFramePayloadLength(
                measurementPath);
            await using (var store = new FileMemoryStore(
                             path,
                             new FileMemoryStoreOptions
                             {
                                 MaxFramePayloadBytes = payloadLength
                             }))
            {
                await store.UpsertAsync(
                    Record("memory-a"),
                    CancellationToken.None);
            }

            var beforeLength = new FileInfo(path).Length;
            Assert.Equal(payloadLength, ReadFirstFramePayloadLength(path));
            var reopenError =
                Assert.Throws<MemoryStoreCapacityExceededException>(
                    () => new FileMemoryStore(
                        path,
                        new FileMemoryStoreOptions
                        {
                            MaxFramePayloadBytes = payloadLength - 1
                        }));
            Assert.Equal(
                nameof(FileMemoryStoreOptions.MaxFramePayloadBytes),
                reopenError.LimitName);
            Assert.Equal(payloadLength - 1, reopenError.Limit);
            Assert.Equal(payloadLength, reopenError.Attempted);
            Assert.Equal(beforeLength, new FileInfo(path).Length);

            await using (var rejectedStore = new FileMemoryStore(
                             rejectedWritePath,
                             new FileMemoryStoreOptions
                             {
                                 MaxFramePayloadBytes = payloadLength - 1
                             }))
            {
                var writeError = await Assert.ThrowsAsync<
                    MemoryStoreCapacityExceededException>(
                    () => rejectedStore.UpsertAsync(
                            Record("memory-a"),
                            CancellationToken.None)
                        .AsTask());
                Assert.Equal(
                    nameof(FileMemoryStoreOptions.MaxFramePayloadBytes),
                    writeError.LimitName);
                Assert.True(writeError.Attempted > writeError.Limit);
                Assert.Equal(
                    0,
                    new FileInfo(rejectedWritePath).Length);
            }

            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(
                "memory-a",
                Assert.Single(
                        await recovered.SearchAsync(
                            new MemoryQuery("shared", Json("{}")),
                            TestContext.Current.CancellationToken))
                    .Record.MemoryId);
        }
        finally
        {
            DeleteContainingDirectory(measurementPath);
            DeleteContainingDirectory(path);
            DeleteContainingDirectory(rejectedWritePath);
        }
    }

    [Fact]
    public async Task MemoryRestartRejectsEachExceededCapacityWithoutRewriting()
    {
        var path = MemoryPath();
        try
        {
            await using (var store = new FileMemoryStore(path))
            {
                await store.UpsertAsync(
                    Record("memory-a"),
                    CancellationToken.None);
                await store.UpsertAsync(
                    Record("memory-b"),
                    CancellationToken.None);
            }

            var beforeLength = new FileInfo(path).Length;
            var frameError =
                Assert.Throws<MemoryStoreCapacityExceededException>(
                    () => new FileMemoryStore(
                        path,
                        new FileMemoryStoreOptions
                        {
                            MaxMutationFrames = 1
                        }));
            Assert.Equal(
                nameof(FileMemoryStoreOptions.MaxMutationFrames),
                frameError.LimitName);

            var byteError =
                Assert.Throws<MemoryStoreCapacityExceededException>(
                    () => new FileMemoryStore(
                        path,
                        new FileMemoryStoreOptions
                        {
                            MaxLogBytes = beforeLength - 1
                        }));
            Assert.Equal(
                nameof(FileMemoryStoreOptions.MaxLogBytes),
                byteError.LimitName);
            Assert.Equal(beforeLength, new FileInfo(path).Length);

            await using var recovered = new FileMemoryStore(path);
            Assert.Equal(2, recovered.Revision);
            Assert.Equal(
                2,
                (await recovered.SearchAsync(
                    new MemoryQuery("shared", Json("{}")),
                    TestContext.Current.CancellationToken)).Count);
        }
        finally
        {
            DeleteContainingDirectory(path);
        }
    }

    private static async ValueTask AssertCursorAsync(
        FileSessionStore store,
        string runId,
        RunJournalCursor expected)
    {
        var actual = await store.GetRunCursorAsync(runId);
        Assert.Equal(expected.NextSequence, actual.NextSequence);
        Assert.Equal(expected.Revision, actual.Revision);
    }

    private static RuntimeEvent Event(
        string eventId,
        string runId,
        long sequence = 999)
    {
        return new RuntimeEvent
        {
            EventId = eventId,
            RunId = runId,
            Sequence = sequence,
            Kind = "test.event",
            Durability = EventDurabilities.Durable,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = Json("""{"value":1}""")
        };
    }

    private static RuntimeEvent EventWithPayload(
        string eventId,
        string runId,
        JsonElement payload)
    {
        var runtimeEvent = Event(eventId, runId);
        runtimeEvent.Payload = payload;
        return runtimeEvent;
    }

    private static RuntimeEvent MissingReceiptEvent(
        string eventId,
        string runId,
        string operationId)
    {
        return new RuntimeEvent
        {
            EventId = eventId,
            RunId = runId,
            TurnId = "turn-1",
            Sequence = 999,
            Kind = RuntimeEventKinds.ActionReceived,
            Durability = EventDurabilities.Durable,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = ProtocolJson.ToElement(
                new ActionReceipt
                {
                    OperationId = operationId,
                    Revision = 0,
                    Status = ReceiptStatuses.Succeeded,
                    Result = Json("""{"ok":true}"""),
                    Retryable = false,
                    ReceivedAt = DateTimeOffset.UnixEpoch
                })
        };
    }

    private static int RegisteredRunStreamCount(FileSessionStore store)
    {
        var field = typeof(FileSessionStore).GetField(
                        "_runStreams",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                        "FileSessionStore._runStreams was not found.");
        return ((System.Collections.IDictionary)field.GetValue(store)!)
            .Count;
    }

    private static int ReadFirstFramePayloadLength(string path)
    {
        var frame = File.ReadAllBytes(path);
        Assert.True(frame.Length >= 12);
        return BinaryPrimitives.ReadInt32LittleEndian(
            frame.AsSpan(4, sizeof(int)));
    }

    private static MemoryRecord Record(string memoryId)
    {
        return new MemoryRecord(
            memoryId,
            "shared",
            Json("""{"value":1}"""),
            Array.Empty<string>(),
            50,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static JsonElement Json(string value)
    {
        return ProtocolJson.ParseElement(value);
    }

    private static string JournalPath()
    {
        return TemporaryPath("sessions.gaj");
    }

    private static string MemoryPath()
    {
        return TemporaryPath("memories.gam");
    }

    private static string TemporaryPath(string fileName)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "game-agent-capacity-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return System.IO.Path.Combine(directory, fileName);
    }

    private static void DeleteContainingDirectory(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CapacityStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly IReadOnlyList<RuntimeEvent> _events;

        public CapacityStore(IReadOnlyList<RuntimeEvent> events)
        {
            _events = events;
        }

        public int AppendCount { get; private set; }

        public int PendingReadCount { get; private set; }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken)
        {
            AppendCount++;
            return default;
        }

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            AppendCount++;
            return new ValueTask<JournalAppendResult>(
                new JournalAppendResult(0, 1, wasDuplicate: false));
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            AppendCount += runtimeEvents.Count;
            return new ValueTask<IReadOnlyList<JournalAppendResult>>(
                runtimeEvents.Select(
                        (_, index) => new JournalAppendResult(
                            index,
                            index + 1,
                            wasDuplicate: false))
                    .ToArray());
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            return new ValueTask<IReadOnlyList<RuntimeEvent>>(_events);
        }

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<RunJournalCursor>(
                new RunJournalCursor(
                    runId,
                    _events.Count,
                    _events.Count));
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return default;
        }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<OperationLedgerEntry?>(
                (OperationLedgerEntry?)null);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            PendingReadCount++;
            return new ValueTask<IReadOnlyList<OperationLedgerEntry>>(
                Array.Empty<OperationLedgerEntry>());
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
    }
}

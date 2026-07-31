using System.Text.Json;
using GameAgent.Core;
using GameAgent.Persistence;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class JournalCoordinatorTests
{
    [Fact]
    public async Task RecoversTranscriptAndReconcilesUnknownOperation()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var message = new NormalizedMessage
            {
                MessageId = "message-1",
                Role = NormalizedRoles.User,
                CreatedAt = clock.UtcNow,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromJson(Json("""{"kind":"tick","value":9}"""))
                }
            };
            await journal.AppendTranscriptAsync(
                run,
                message,
                "turn-1",
                "attempt-1",
                default);
            var request = new ActionRequest
            {
                OperationId = "operation-1",
                RunId = run.RunId,
                TurnId = "turn-1",
                ToolCallId = "call-1",
                AgentId = run.AgentId,
                WorldId = run.WorldId,
                ActionName = "move_to",
                ActionVersion = "1",
                Arguments = Json("""{"x":2}"""),
                RequestedAt = clock.UtcNow
            };
            await journal.AppendActionRequestAsync(
                run,
                request,
                "attempt-1",
                default);
            await journal.AppendActionReceiptAsync(
                run,
                "turn-1",
                "attempt-1",
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 0,
                    Status = ReceiptStatuses.Unknown,
                    ReceivedAt = clock.UtcNow
                },
                default);

            var recovery = new RunRecovery(store, store, journal);
            var recovered = await recovery.LoadAsync(run.RunId, default);

            Assert.NotNull(recovered);
            Assert.Equal(RunStates.Reconciling, recovered!.Run.State);
            Assert.Equal(run.Revision, recovered.Run.Revision);
            Assert.Single(recovered.PendingOperations);
            var recoveredMessage = Assert.Single(recovered.Transcript);
            Assert.Equal("tick", recoveredMessage.Parts[0].Json!.Value
                .GetProperty("kind")
                .GetString());

            await recovery.ReconcileAsync(
                recovered,
                new Reconciler(clock.UtcNow),
                "recovery-attempt-1",
                default);
            Assert.Empty(recovered.Run.PendingOperationIds);
            Assert.Empty(recovered.PendingOperations);
            await journal.CommitTransitionAsync(
                recovered.Run,
                RunStates.Completed,
                RuntimeEventKinds.RunCompleted);
            Assert.Equal(RunStates.Completed, recovered.Run.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DetachedReconciliationQueryPreventsDuplicateReentry()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        BlockingReconciler? reconciler = null;
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var request = Request(
                run,
                "operation-detached",
                "call-detached",
                clock.UtcNow);
            await journal.AppendActionRequestAsync(
                run,
                request,
                "attempt-1",
                default);
            await journal.AppendActionReceiptAsync(
                run,
                request.TurnId,
                "attempt-1",
                Receipt(
                    request,
                    0,
                    ReceiptStatuses.Unknown,
                    clock.UtcNow),
                default);
            var registry = new ReconciliationQueryRegistry(capacity: 1);
            var recovery = new RunRecovery(
                store,
                store,
                journal,
                registry);
            var recovered = await recovery.LoadAsync(run.RunId, default);
            Assert.NotNull(recovered);
            reconciler = new BlockingReconciler(clock.UtcNow);
            using var cancellation = new CancellationTokenSource();

            var first = recovery.ReconcileAsync(
                    recovered!,
                    reconciler,
                    "recovery-attempt-1",
                    cancellation.Token)
                .AsTask();
            await reconciler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => first);

            Assert.Equal(1, reconciler.CallCount);
            Assert.Equal(1, registry.ActiveCount);
            var blocked = await recovery.ReconcileAsync(
                recovered!,
                reconciler,
                "recovery-attempt-2",
                default);
            Assert.Same(recovered, blocked);
            Assert.Single(blocked.PendingOperations);
            Assert.Equal(1, reconciler.CallCount);
            Assert.Equal(1, registry.ActiveCount);

            reconciler.Release.TrySetResult();
            var drainWait = System.Diagnostics.Stopwatch.StartNew();
            while (registry.ActiveCount != 0
                   && drainWait.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }

            Assert.Equal(0, registry.ActiveCount);

            var completed = await recovery.ReconcileAsync(
                recovered!,
                reconciler,
                "recovery-attempt-3",
                default);

            Assert.Equal(2, reconciler.CallCount);
            Assert.Empty(completed.PendingOperations);
            Assert.Empty(completed.Run.PendingOperationIds);
            Assert.Equal(0, registry.ActiveCount);
        }
        finally
        {
            reconciler?.Release.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedAppendDoesNotMutateRunAggregate()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        try
        {
            await using var store = new FileSessionStore(
                path,
                new FileJournalOptions
                {
                    FaultInjector = new ThrowBeforeWrite()
                });
            var journal = new JournalCoordinator(
                store,
                store,
                new Clock(),
                new Ids());
            var run = Run();

            await Assert.ThrowsAsync<IOException>(
                async () =>
                    await journal.CommitTransitionAsync(
                        run,
                        RunStates.Running,
                        RuntimeEventKinds.RunStarted));

            Assert.Equal(RunStates.Queued, run.State);
            Assert.Equal(0, run.Revision);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateOperationAppendsNeverRollBackTheRunCursor()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var first = Request(run, "operation-1", "call-1", clock.UtcNow);
            var second = Request(run, "operation-2", "call-2", clock.UtcNow);
            await journal.AppendActionRequestsAsync(
                run,
                new[] { first, second },
                "attempt-1",
                default);
            Assert.Equal(3, run.Revision);

            await journal.AppendActionRequestAsync(
                run,
                first,
                "attempt-retry",
                default);
            Assert.Equal(3, run.Revision);
            Assert.Equal(2, run.PendingOperationIds.Count);

            var failedReceipt = Receipt(
                second,
                0,
                ReceiptStatuses.Failed,
                clock.UtcNow);
            await journal.AppendActionReceiptAsync(
                run,
                second.TurnId,
                "attempt-1",
                failedReceipt,
                default);
            Assert.Equal(5, run.Revision);
            Assert.Equal(new[] { first.OperationId }, run.PendingOperationIds);
            Assert.Contains(
                await store.ReadRunAsync(run.RunId, default),
                item => item.Kind == RuntimeEventKinds.ToolFailed
                        && item.EventId
                            == RuntimeEventIdDerivation.Derive(
                                run.RunId,
                                "tool-result-event:operation-2:0"));

            var eventCount = (await store.ReadRunAsync(run.RunId, default))
                .Count;
            failedReceipt.ReceivedAt = failedReceipt.ReceivedAt.AddSeconds(1);
            await journal.AppendActionReceiptAsync(
                run,
                second.TurnId,
                "attempt-retry",
                failedReceipt,
                default);
            Assert.Equal(5, run.Revision);
            Assert.Equal(new[] { first.OperationId }, run.PendingOperationIds);
            Assert.Equal(
                eventCount,
                (await store.ReadRunAsync(run.RunId, default)).Count);

            await journal.AppendActionRequestAsync(
                run,
                second,
                "attempt-terminal-retry",
                default);
            Assert.Equal(5, run.Revision);
            Assert.Equal(new[] { first.OperationId }, run.PendingOperationIds);

            await journal.AppendActionRequestAsync(
                run,
                first,
                "attempt-late-retry",
                default);
            Assert.Equal(5, run.Revision);

            await journal.AppendActionReceiptAsync(
                run,
                first.TurnId,
                "attempt-1",
                Receipt(first, 0, ReceiptStatuses.Succeeded, clock.UtcNow),
                default);
            Assert.Equal(7, run.Revision);
            Assert.Empty(run.PendingOperationIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedActionBatchResultHasNoCoordinatorSideEffects()
    {
        var directory = TempDirectory();
        try
        {
            await using var inner = new FileSessionStore(
                Path.Combine(directory, "session.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var run = Run();
            using (var setup = new JournalCoordinator(
                       inner,
                       inner,
                       clock,
                       ids))
            {
                await setup.CommitTransitionAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.RunStarted);
            }

            var publisher = new RecordingPublisher();
            var store = new MalformedBatchResultStore(
                inner,
                RuntimeEventKinds.ActionRequested);
            using var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids,
                publisher);
            var beforeRevision = run.Revision;

            await Assert.ThrowsAsync<InvalidDataException>(
                () => journal.AppendActionRequestsAsync(
                        run,
                        new[]
                        {
                            Request(
                                run,
                                "malformed-operation-1",
                                "malformed-call-1",
                                clock.UtcNow),
                            Request(
                                run,
                                "malformed-operation-2",
                                "malformed-call-2",
                                clock.UtcNow)
                        },
                        "malformed-attempt",
                        default)
                    .AsTask());

            Assert.Equal(beforeRevision, run.Revision);
            Assert.Empty(run.PendingOperationIds);
            Assert.Empty(publisher.Events);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedReceiptBatchResultHasNoCoordinatorSideEffects()
    {
        var directory = TempDirectory();
        try
        {
            await using var inner = new FileSessionStore(
                Path.Combine(directory, "session.journal"));
            var clock = new Clock();
            var ids = new Ids();
            var run = Run();
            var request = Request(
                run,
                "malformed-receipt-operation",
                "malformed-receipt-call",
                clock.UtcNow);
            using (var setup = new JournalCoordinator(
                       inner,
                       inner,
                       clock,
                       ids))
            {
                await setup.CommitTransitionAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.RunStarted);
                await setup.AppendActionRequestAsync(
                    run,
                    request,
                    "setup-attempt",
                    default);
            }

            var publisher = new RecordingPublisher();
            var store = new MalformedBatchResultStore(
                inner,
                RuntimeEventKinds.ActionReceived);
            using var journal = new JournalCoordinator(
                store,
                store,
                clock,
                ids,
                publisher);
            var beforeRevision = run.Revision;

            await Assert.ThrowsAsync<InvalidDataException>(
                () => journal.AppendActionReceiptAsync(
                        run,
                        request.TurnId,
                        "malformed-receipt-attempt",
                        Receipt(
                            request,
                            0,
                            ReceiptStatuses.Succeeded,
                            clock.UtcNow),
                        default)
                    .AsTask());

            Assert.Equal(beforeRevision, run.Revision);
            Assert.Equal(
                new[] { request.OperationId },
                run.PendingOperationIds);
            Assert.Empty(publisher.Events);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryUsesLatestBudgetAggregateSnapshot()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var budgetSnapshot = ProtocolJson.DeserializeAgentRun(
                ProtocolJson.Serialize(run));
            budgetSnapshot.Revision = run.Revision + 1;
            budgetSnapshot.Usage.InputTokens = 101;
            budgetSnapshot.Usage.OutputTokens = 23;
            budgetSnapshot.Usage.CostUsd = "0.125";
            budgetSnapshot.Usage.DurationMs = 4_321;
            await journal.AppendBuiltInDurableAsync(
                run,
                RuntimeEventKinds.BudgetUpdated,
                ProtocolJson.ToElement(budgetSnapshot),
                turnId: null,
                attemptId: null);
            await journal.AppendBuiltInDurableAsync(
                run,
                RuntimeEventKinds.ToolStarted,
                Json("""{"call":"later"}"""),
                "turn-1",
                "attempt-1");

            var recovered = await new RunRecovery(store, store, journal)
                .LoadAsync(run.RunId, default);

            Assert.NotNull(recovered);
            Assert.Equal(101, recovered!.Run.Usage.InputTokens);
            Assert.Equal(23, recovered.Run.Usage.OutputTokens);
            Assert.Equal("0.125", recovered.Run.Usage.CostUsd);
            Assert.Equal(4_321, recovered.Run.Usage.DurationMs);
            Assert.Equal(run.Revision, recovered.Run.Revision);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CompletionBatchIsAbsentAfterATornWrite()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        var fault = new ArmablePartialWrite();
        try
        {
            await using (var store = new FileSessionStore(
                             path,
                             new FileJournalOptions { FaultInjector = fault }))
            {
                var clock = new Clock();
                var journal = new JournalCoordinator(
                    store,
                    store,
                    clock,
                    new Ids());
                var run = Run();
                await journal.CommitTransitionAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.RunStarted);
                var assistant = NormalizedTranscript.AssistantResponse(
                    "assistant-final",
                    """{"value":"done"}""",
                    reasoningContent: null,
                    Array.Empty<ModelToolCall>(),
                    clock.UtcNow);
                fault.Armed = true;

                await Assert.ThrowsAsync<IOException>(
                    () => journal.CommitFinalCompletionAsync(
                            run,
                            assistant,
                            Json("""{"value":"done"}"""),
                            "turn-1",
                            "provider-1",
                            "attempt-1",
                            "stream-1",
                            clock.UtcNow,
                            default)
                        .AsTask());
                Assert.Equal(RunStates.Running, run.State);
            }

            await using (var recoveredStore = new FileSessionStore(path))
            {
                var events = await recoveredStore.ReadRunAsync("run-1", default);
                Assert.DoesNotContain(
                    events,
                    item => item.Kind == RuntimeEventKinds.TranscriptMessage
                            && item.EventId
                                == RuntimeEventIdDerivation.Derive(
                                    "run-1",
                                    "transcript:assistant-final"));
                Assert.DoesNotContain(
                    events,
                    item => item.Kind == RuntimeEventKinds.AssistantCompleted);
                Assert.DoesNotContain(
                    events,
                    item => item.Kind == RuntimeEventKinds.RunCompleted);
                var journal = new JournalCoordinator(
                    recoveredStore,
                    recoveredStore,
                    new Clock(),
                    new Ids());
                var recovered = await new RunRecovery(
                        recoveredStore,
                        recoveredStore,
                        journal)
                    .LoadAsync("run-1", default);
                Assert.Equal(RunStates.Running, recovered!.Run.State);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReconciliationAppliesPersistedResultSchemaAndContentLimits()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var request = Request(
                run,
                "operation-1",
                "call-1",
                clock.UtcNow);
            request.Extensions["resultSchema"] = Json(
                """
                {
                  "type":"object",
                  "properties":{"count":{"type":"integer"}},
                  "required":["count"],
                  "additionalProperties":false
                }
                """);
            await journal.AppendActionRequestAsync(
                run,
                request,
                "attempt-1",
                default);
            await journal.AppendActionReceiptAsync(
                run,
                request.TurnId,
                "attempt-1",
                Receipt(request, 0, ReceiptStatuses.Unknown, clock.UtcNow),
                default);
            var recovery = new RunRecovery(store, store, journal);
            var recovered = await recovery.LoadAsync(run.RunId, default);

            await recovery.ReconcileAsync(
                recovered!,
                new ResultReconciler(
                    request.OperationId,
                    Json("""{"count":"not-an-integer"}"""),
                    clock.UtcNow),
                "recovery-attempt",
                default);

            var events = await store.ReadRunAsync(run.RunId, default);
            var receiptEvent = events.Last(
                item => item.Kind == RuntimeEventKinds.ActionReceived);
            Assert.Equal(
                "tool_result_schema_invalid",
                receiptEvent.Payload.GetProperty("errorCode").GetString());
            Assert.False(
                receiptEvent.Payload.TryGetProperty(
                    "result",
                    out var sanitizedResult)
                && sanitizedResult.ValueKind != JsonValueKind.Null);
            Assert.Contains(
                events,
                item => item.Kind == RuntimeEventKinds.ToolCompleted);
            Assert.Empty(recovered!.PendingOperations);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcilerCannotMutateJournaledActionRequest()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var request = Request(
                run,
                "operation-1",
                "call-1",
                clock.UtcNow);
            request.Extensions["resultSchema"] = Json(
                """
                {
                  "type":"object",
                  "properties":{"count":{"type":"integer"}},
                  "required":["count"],
                  "additionalProperties":false
                }
                """);
            await journal.AppendActionRequestAsync(
                run,
                request,
                "attempt-1",
                default);
            await journal.AppendActionReceiptAsync(
                run,
                request.TurnId,
                "attempt-1",
                Receipt(request, 0, ReceiptStatuses.Unknown, clock.UtcNow),
                default);
            var recovery = new RunRecovery(store, store, journal);
            var recovered = await recovery.LoadAsync(run.RunId, default);
            var authoritative = Assert.Single(
                recovered!.PendingOperations).Request;
            var reconciler = new MutatingResultReconciler(
                request.OperationId,
                clock.UtcNow);

            await recovery.ReconcileAsync(
                recovered,
                reconciler,
                "recovery-attempt",
                default);

            Assert.NotNull(reconciler.ReceivedRequest);
            Assert.NotSame(authoritative, reconciler.ReceivedRequest);
            Assert.Equal("read_state", authoritative.ActionName);
            Assert.True(
                authoritative.Extensions.ContainsKey("resultSchema"));
            var events = await store.ReadRunAsync(run.RunId, default);
            var receiptEvent = events.Last(
                item => item.Kind == RuntimeEventKinds.ActionReceived);
            Assert.Equal(
                "tool_result_schema_invalid",
                receiptEvent.Payload.GetProperty("errorCode").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReconciliationRejectsInvalidOrOversizedReceiptsBeforeJournal()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            await journal.CommitTransitionAsync(
                run,
                RunStates.Running,
                RuntimeEventKinds.RunStarted);
            var request = Request(
                run,
                "operation-1",
                "call-1",
                clock.UtcNow);
            await journal.AppendActionRequestAsync(
                run,
                request,
                "attempt-1",
                default);
            var recovery = new RunRecovery(store, store, journal);
            var recovered = await recovery.LoadAsync(run.RunId, default);
            var before = (await store.ReadRunAsync(run.RunId, default)).Count;

            await Assert.ThrowsAsync<OperationLedgerConflictException>(
                () => recovery.ReconcileAsync(
                        recovered!,
                        new InvalidReceiptReconciler(clock.UtcNow),
                        "invalid-attempt",
                        default)
                    .AsTask());
            Assert.Equal(
                before,
                (await store.ReadRunAsync(run.RunId, default)).Count);

            var oversized = new string('x', 300_000);
            await Assert.ThrowsAsync<RuntimeContentLimitException>(
                () => recovery.ReconcileAsync(
                        recovered!,
                        new ResultReconciler(
                            request.OperationId,
                            Json(
                                JsonSerializer.Serialize(
                                    new { value = oversized })),
                            clock.UtcNow),
                        "oversized-attempt",
                        default)
                    .AsTask());
            Assert.Equal(
                before,
                (await store.ReadRunAsync(run.RunId, default)).Count);

            await Assert.ThrowsAsync<OperationLedgerConflictException>(
                () => recovery.ReconcileAsync(
                        recovered!,
                        new ReceiptFactoryReconciler(
                            pendingRequest =>
                            {
                                var receipt = Receipt(
                                    pendingRequest,
                                    1,
                                    ReceiptStatuses.Succeeded,
                                    clock.UtcNow);
                                receipt.AuthoritativeObservations.Add(
                                    Observation("world-other", includePayload: true));
                                return receipt;
                            }),
                        "cross-world-attempt",
                        default)
                    .AsTask());
            Assert.Equal(
                before,
                (await store.ReadRunAsync(run.RunId, default)).Count);

            await Assert.ThrowsAsync<JsonException>(
                () => recovery.ReconcileAsync(
                        recovered!,
                        new ReceiptFactoryReconciler(
                            pendingRequest =>
                            {
                                var receipt = Receipt(
                                    pendingRequest,
                                    1,
                                    ReceiptStatuses.Succeeded,
                                    clock.UtcNow);
                                receipt.AuthoritativeObservations.Add(
                                    Observation(
                                        pendingRequest.WorldId,
                                        includePayload: false));
                                return receipt;
                            }),
                        "invalid-observation-attempt",
                        default)
                    .AsTask());
            Assert.Equal(
                before,
                (await store.ReadRunAsync(run.RunId, default)).Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryBackfillsToolTerminalEventAfterFallbackCrashWindow()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        var fault = new ArmableNthPartialWrite();
        try
        {
            await using (var inner = new FileSessionStore(
                             path,
                             new FileJournalOptions { FaultInjector = fault }))
            {
                var store = new SequentialBatchStore(inner);
                var clock = new Clock();
                var journal = new JournalCoordinator(
                    store,
                    store,
                    clock,
                    new Ids());
                var run = Run();
                await journal.CommitTransitionAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.RunStarted);
                var request = Request(
                    run,
                    "operation-1",
                    "call-1",
                    clock.UtcNow);
                await journal.AppendActionRequestAsync(
                    run,
                    request,
                    "attempt-1",
                    default);
                fault.Arm(partialWriteNumber: 2);

                await Assert.ThrowsAsync<IOException>(
                    () => journal.AppendActionReceiptAsync(
                            run,
                            request.TurnId,
                            "attempt-1",
                            Receipt(
                                request,
                                0,
                                ReceiptStatuses.Succeeded,
                                clock.UtcNow),
                            default)
                        .AsTask());
            }

            await using (var recoveredStore = new FileSessionStore(path))
            {
                var journal = new JournalCoordinator(
                    recoveredStore,
                    recoveredStore,
                    new Clock(),
                    new Ids());
                var recovered = await new RunRecovery(
                        recoveredStore,
                        recoveredStore,
                        journal)
                    .LoadAsync("run-1", default);
                Assert.NotNull(recovered);
                Assert.Empty(recovered!.PendingOperations);
                Assert.Single(
                    await recoveredStore.ReadRunAsync("run-1", default),
                    item => item.Kind == RuntimeEventKinds.ToolCompleted);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryIdempotentlyFinishesFallbackCompletionWindow()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        var fault = new ArmableNthPartialWrite();
        try
        {
            await using (var inner = new FileSessionStore(
                             path,
                             new FileJournalOptions { FaultInjector = fault }))
            {
                var store = new SequentialBatchStore(inner);
                var clock = new Clock();
                var journal = new JournalCoordinator(
                    store,
                    store,
                    clock,
                    new Ids());
                var run = Run();
                await journal.CommitTransitionAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.RunStarted);
                fault.Arm(partialWriteNumber: 2);

                await Assert.ThrowsAsync<IOException>(
                    () => journal.CommitCompletionAsync(
                            run,
                            Json("""{"value":"done"}"""),
                            "turn-1",
                            "attempt-1",
                            "stream-1",
                            clock.UtcNow,
                            default)
                        .AsTask());
            }

            await using (var recoveredStore = new FileSessionStore(path))
            {
                var clock = new Clock();
                var journal = new JournalCoordinator(
                    recoveredStore,
                    recoveredStore,
                    clock,
                    new Ids());
                var recovered = await new RunRecovery(
                        recoveredStore,
                        recoveredStore,
                        journal)
                    .LoadAsync("run-1", default);
                Assert.NotNull(recovered);
                Assert.True(recovered!.FinalOutput.HasValue);
                Assert.Equal(RunStates.Running, recovered.Run.State);

                await journal.CommitRecoveredCompletionAsync(
                    recovered.Run,
                    "turn-1",
                    "attempt-recovery",
                    clock.UtcNow,
                    default);
                Assert.Equal(RunStates.Completed, recovered.Run.State);
                Assert.Single(
                    await recoveredStore.ReadRunAsync("run-1", default),
                    item => item.Kind == RuntimeEventKinds.AssistantCompleted);
                Assert.Single(
                    await recoveredStore.ReadRunAsync("run-1", default),
                    item => item.Kind == RuntimeEventKinds.RunCompleted);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryClosesEveryUncommittedToolCallAfterTornActionBatch()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        var fault = new ArmablePartialWrite();
        try
        {
            await using (var store = new FileSessionStore(
                             path,
                             new FileJournalOptions { FaultInjector = fault }))
            {
                var clock = new Clock();
                var journal = new JournalCoordinator(
                    store,
                    store,
                    clock,
                    new Ids());
                var run = Run();
                await journal.CommitTransitionAsync(
                    run,
                    RunStates.Running,
                    RuntimeEventKinds.RunStarted);
                var assistant = NormalizedTranscript.AssistantToolCalls(
                    "assistant-1",
                    new[]
                    {
                        new ModelToolCall
                        {
                            ToolCallId = "call-1",
                            Name = "read_state",
                            Arguments = Json("""{"entityId":"npc-1"}""")
                        },
                        new ModelToolCall
                        {
                            ToolCallId = "call-2",
                            Name = "read_state",
                            Arguments = Json("""{"entityId":"npc-2"}""")
                        }
                    },
                    clock.UtcNow);
                await journal.CommitRunMutationAsync(
                    run,
                    RuntimeEventKinds.ProviderDispatchStarted,
                    _ => { },
                    "turn-1",
                    "attempt-1",
                    default,
                    eventId: "provider-dispatch:stream-1",
                    streamAttemptId: "stream-1",
                    providerId: "provider-1");
                await journal.CommitRunMutationAsync(
                    run,
                    RuntimeEventKinds.BudgetUpdated,
                    _ => { },
                    "turn-1",
                    "attempt-1",
                    default,
                    eventId: "provider-usage:stream-1",
                    streamAttemptId: "stream-1",
                    providerId: "provider-1");
                await journal.CommitProviderResultAsync(
                    run,
                    assistant,
                    "turn-1",
                    "provider-1",
                    "attempt-1",
                    "stream-1",
                    default);
                await journal.CommitTransitionAsync(
                    run,
                    RunStates.WaitingForAction,
                    RuntimeEventKinds.RunCheckpoint,
                    turnId: "turn-1",
                    attemptId: "attempt-1");
                fault.Armed = true;

                await Assert.ThrowsAsync<IOException>(
                    () => journal.AppendActionRequestsAsync(
                            run,
                            new[]
                            {
                                Request(
                                    run,
                                    "operation-1",
                                    "call-1",
                                    clock.UtcNow),
                                Request(
                                    run,
                                    "operation-2",
                                    "call-2",
                                    clock.UtcNow)
                            },
                            "attempt-1",
                            default)
                        .AsTask());
            }

            await using (var recoveredStore = new FileSessionStore(path))
            {
                var journal = new JournalCoordinator(
                    recoveredStore,
                    recoveredStore,
                    new Clock(),
                    new Ids());
                var recovered = await new RunRecovery(
                        recoveredStore,
                        recoveredStore,
                        journal)
                    .LoadAsync("run-1", default);

                Assert.NotNull(recovered);
                Assert.Empty(recovered!.PendingOperations);
                var recoveryResults = recovered.Transcript
                    .Where(item => item.Role == NormalizedRoles.Tool)
                    .ToArray();
                Assert.Equal(2, recoveryResults.Length);
                Assert.Equal(
                    new[] { "call-1", "call-2" },
                    recoveryResults
                        .Select(item => item.Parts[0].ToolCallId)
                        .OrderBy(item => item, StringComparer.Ordinal));
                Assert.All(
                    recoveryResults,
                    item => Assert.Equal(
                        "action_dispatch_not_committed",
                        item.Parts[0].Json!.Value
                            .GetProperty("code")
                            .GetString()));
                Assert.Empty(
                    await recoveredStore.ReadPendingOperationsAsync(
                        "run-1",
                        default));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DerivedEventIdsAreBoundedStableAndCollisionResistant()
    {
        var runId = new string('r', 128);
        var semanticId = new string('s', 128);
        var candidate = "provider-result-committed:" + semanticId;

        var derived = RuntimeEventIdDerivation.Derive(runId, candidate);

        Assert.Equal(
            derived,
            RuntimeEventIdDerivation.Derive(runId, candidate));
        Assert.StartsWith(
            "provider-result-committed:sha256:",
            derived,
            StringComparison.Ordinal);
        Assert.InRange(
            derived.Length,
            1,
            RuntimeEventIdDerivation.MaximumLength);
        Assert.Matches("^[A-Za-z0-9._:-]+$", derived);
        Assert.Equal(64, derived[(derived.LastIndexOf(':') + 1)..].Length);
        Assert.NotEqual(
            derived,
            RuntimeEventIdDerivation.Derive(
                runId,
                "provider-result-committed:"
                + new string('s', 127)
                + "t"));
        Assert.NotEqual(
            derived,
            RuntimeEventIdDerivation.Derive(
                new string('q', 128),
                candidate));

        var arbitrarySemanticId = RuntimeEventIdDerivation.Derive(
            runId,
            "transcript:消息/slot 1");
        Assert.StartsWith(
            "transcript:sha256:",
            arbitrarySemanticId,
            StringComparison.Ordinal);
        Assert.Matches("^[A-Za-z0-9._:-]+$", arbitrarySemanticId);
    }

    [Fact]
    public async Task LongActionRequestIdRemainsStableAcrossIdempotentReplay()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var run = Run();
            var operationId = new string('o', 128);
            var request = Request(
                run,
                operationId,
                "call-1",
                clock.UtcNow);

            await journal.CommitRunStartAsync(
                run,
                Array.Empty<NormalizedMessage>(),
                default);
            await journal.AppendActionRequestAsync(
                run,
                request,
                "attempt-1",
                default);
            var firstRevision = run.Revision;
            await journal.AppendActionRequestAsync(
                run,
                request,
                "attempt-retry",
                default);

            Assert.Equal(firstRevision, run.Revision);
            var runtimeEvent = Assert.Single(
                await store.ReadRunAsync(run.RunId, default),
                item => item.Kind == RuntimeEventKinds.ActionRequested);
            Assert.Equal(
                RuntimeEventIdDerivation.Derive(
                    run.RunId,
                    "action-request:" + operationId),
                runtimeEvent.EventId);
            Assert.InRange(
                runtimeEvent.EventId.Length,
                1,
                RuntimeEventIdDerivation.MaximumLength);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunScopedEventIdsAllowSharedMessageAndToolCallIds()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "session.journal");
        try
        {
            await using var store = new FileSessionStore(path);
            var clock = new Clock();
            var journal = new JournalCoordinator(
                store,
                store,
                clock,
                new Ids());
            var firstRun = Run("run-1");
            var secondRun = Run("run-2");
            var sharedMessage = new NormalizedMessage
            {
                MessageId = "shared/message 消息",
                Role = NormalizedRoles.User,
                CreatedAt = clock.UtcNow,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromText("hello")
                }
            };

            await journal.CommitRunStartAsync(
                firstRun,
                new[] { sharedMessage },
                default);
            await journal.CommitRunStartAsync(
                secondRun,
                new[] { sharedMessage },
                default);

            const string sharedToolCallId = "shared/tool call 工具";
            await journal.AppendBuiltInDurableAsync(
                firstRun,
                RuntimeEventKinds.ToolStarted,
                Json("""{"toolCallId":"shared"}"""),
                "turn-1",
                "attempt-1",
                eventId: "tool-start:" + sharedToolCallId,
                cancellationToken: default);
            await journal.AppendBuiltInDurableAsync(
                secondRun,
                RuntimeEventKinds.ToolStarted,
                Json("""{"toolCallId":"shared"}"""),
                "turn-1",
                "attempt-1",
                eventId: "tool-start:" + sharedToolCallId,
                cancellationToken: default);

            var firstEvents = await store.ReadRunAsync(
                firstRun.RunId,
                default);
            var secondEvents = await store.ReadRunAsync(
                secondRun.RunId,
                default);
            var firstTranscript = Assert.Single(
                firstEvents,
                item => item.Kind == RuntimeEventKinds.TranscriptMessage);
            var secondTranscript = Assert.Single(
                secondEvents,
                item => item.Kind == RuntimeEventKinds.TranscriptMessage);
            var firstTool = Assert.Single(
                firstEvents,
                item => item.Kind == RuntimeEventKinds.ToolStarted);
            var secondTool = Assert.Single(
                secondEvents,
                item => item.Kind == RuntimeEventKinds.ToolStarted);

            Assert.NotEqual(firstTranscript.EventId, secondTranscript.EventId);
            Assert.NotEqual(firstTool.EventId, secondTool.EventId);
            Assert.StartsWith(
                "transcript:sha256:",
                firstTranscript.EventId,
                StringComparison.Ordinal);
            Assert.StartsWith(
                "tool-start:sha256:",
                firstTool.EventId,
                StringComparison.Ordinal);
            Assert.All(
                new[]
                {
                    firstTranscript.EventId,
                    secondTranscript.EventId,
                    firstTool.EventId,
                    secondTool.EventId
                },
                eventId =>
                {
                    Assert.InRange(
                        eventId.Length,
                        1,
                        RuntimeEventIdDerivation.MaximumLength);
                    Assert.Matches("^[A-Za-z0-9._:-]+$", eventId);
                });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BoundedBusDropsIncomingNotificationWithoutBlockingPublisher()
    {
        using var bus = new BoundedRuntimeEventBus(capacity: 2);
        bus.Publish(Event("one"));
        bus.Publish(Event("two"));
        bus.Publish(Event("three"));

        var drained = bus.Drain(10);

        Assert.Equal(new[] { "one", "two" }, drained.Select(item => item.EventId));
        Assert.Equal(1, bus.DroppedEphemeralEvents);
    }

    [Fact]
    public void NormalizedCodecRejectsUnknownFields()
    {
        var invalid = Json(
            """
            {
              "messageId":"m",
              "role":"user",
              "createdAt":"2026-07-28T00:00:00Z",
              "parts":[{"type":"text","text":"x","surprise":true}]
            }
            """);

        Assert.Throws<InvalidDataException>(
            () => NormalizedMessageJournalCodec.Decode(invalid));
    }

    private static AgentRun Run(string runId = "run-1")
    {
        return new AgentRun
        {
            RunId = runId,
            AgentId = "agent-1",
            WorldId = "world-1",
            SessionId = "session-1",
            State = RunStates.Queued,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static RuntimeEvent Event(string id)
    {
        return new RuntimeEvent
        {
            EventId = id,
            Kind = RuntimeEventKinds.AssistantDelta,
            Durability = EventDurabilities.Ephemeral,
            Timestamp = DateTimeOffset.UnixEpoch,
            Payload = Json("""{"delta":"x"}""")
        };
    }

    private static ActionRequest Request(
        AgentRun run,
        string operationId,
        string callId,
        DateTimeOffset now)
    {
        return new ActionRequest
        {
            OperationId = operationId,
            RunId = run.RunId,
            TurnId = "turn-1",
            ToolCallId = callId,
            AgentId = run.AgentId,
            WorldId = run.WorldId,
            ActionName = "read_state",
            ActionVersion = "1",
            Arguments = Json("""{"entityId":"npc-1"}"""),
            RequestedAt = now
        };
    }

    private static ActionReceipt Receipt(
        ActionRequest request,
        long revision,
        string status,
        DateTimeOffset now)
    {
        return new ActionReceipt
        {
            OperationId = request.OperationId,
            Revision = revision,
            Status = status,
            Result = Json("""{"ok":true}"""),
            ReceivedAt = now
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ObservationEnvelope Observation(
        string worldId,
        bool includePayload)
    {
        return new ObservationEnvelope
        {
            ObservationId = "observation-1",
            WorldId = worldId,
            Source = "game",
            Kind = "custom",
            ContentType = "application/json",
            Payload = includePayload ? Json("""{"value":1}""") : null,
            ObservedAt = DateTimeOffset.UnixEpoch,
            Trust = "authoritative"
        };
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "game-agent-journal-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class Clock : IRuntimeClock
    {
        public DateTimeOffset UtcNow =>
            new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class Ids : IRuntimeIdGenerator
    {
        private int _value;

        public string NewId(string category)
        {
            return category + "-" + Interlocked.Increment(ref _value);
        }
    }

    private sealed class Reconciler : IGameOperationReconciler
    {
        private readonly DateTimeOffset _now;

        public Reconciler(DateTimeOffset now)
        {
            _now = now;
        }

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = request.OperationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = Json("""{"moved":true}"""),
                    ReceivedAt = _now,
                    CommittedAt = _now
                });
        }
    }

    private sealed class BlockingReconciler : IGameOperationReconciler
    {
        private readonly DateTimeOffset _now;
        private int _callCount;

        public BlockingReconciler(DateTimeOffset now)
        {
            _now = now;
        }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 1,
                Status = ReceiptStatuses.Succeeded,
                Result = Json("""{"reconciled":true}"""),
                ReceivedAt = _now,
                CommittedAt = _now
            };
        }
    }

    private sealed class ResultReconciler : IGameOperationReconciler
    {
        private readonly string _operationId;
        private readonly JsonElement _result;
        private readonly DateTimeOffset _now;

        public ResultReconciler(
            string operationId,
            JsonElement result,
            DateTimeOffset now)
        {
            _operationId = operationId;
            _result = result;
            _now = now;
        }

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = _operationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = _result.Clone(),
                    ReceivedAt = _now,
                    CommittedAt = _now
                });
        }
    }

    private sealed class MutatingResultReconciler :
        IGameOperationReconciler
    {
        private readonly string _operationId;
        private readonly DateTimeOffset _now;

        public MutatingResultReconciler(
            string operationId,
            DateTimeOffset now)
        {
            _operationId = operationId;
            _now = now;
        }

        public ActionRequest? ReceivedRequest { get; private set; }

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedRequest = request;
            request.ActionName = "mutated_action";
            request.Extensions.Clear();
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = _operationId,
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    Result = Json("""{"count":"not-an-integer"}"""),
                    ReceivedAt = _now,
                    CommittedAt = _now
                });
        }
    }

    private sealed class InvalidReceiptReconciler : IGameOperationReconciler
    {
        private readonly DateTimeOffset _now;

        public InvalidReceiptReconciler(DateTimeOffset now)
        {
            _now = now;
        }

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ActionReceipt>(
                new ActionReceipt
                {
                    OperationId = "invalid operation id",
                    Revision = 1,
                    Status = ReceiptStatuses.Succeeded,
                    ReceivedAt = _now
                });
        }
    }

    private sealed class ReceiptFactoryReconciler : IGameOperationReconciler
    {
        private readonly Func<ActionRequest, ActionReceipt> _factory;

        public ReceiptFactoryReconciler(
            Func<ActionRequest, ActionReceipt> factory)
        {
            _factory = factory;
        }

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ActionReceipt>(_factory(request));
        }
    }

    private sealed class ThrowBeforeWrite : IJournalFaultInjector
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
            if (stage == JournalWriteStage.BeforeWrite)
            {
                throw new IOException("Injected write failure.");
            }
        }
    }

    private sealed class ArmablePartialWrite : IJournalFaultInjector
    {
        public bool Armed { get; set; }

        public int GetWriteLength(int frameLength)
        {
            return Armed ? Math.Max(1, frameLength / 2) : frameLength;
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
        }
    }

    private sealed class ArmableNthPartialWrite : IJournalFaultInjector
    {
        private int _writeCount;
        private int _partialWriteNumber;

        public void Arm(int partialWriteNumber)
        {
            _writeCount = 0;
            _partialWriteNumber = partialWriteNumber;
        }

        public int GetWriteLength(int frameLength)
        {
            if (_partialWriteNumber > 0
                && Interlocked.Increment(ref _writeCount)
                == _partialWriteNumber)
            {
                return Math.Max(1, frameLength / 2);
            }

            return frameLength;
        }

        public void OnWriteStage(
            JournalWriteStage stage,
            int bytesWritten,
            int frameLength)
        {
        }
    }

    private sealed class RecordingPublisher :
        INonBlockingRuntimeEventPublisher
    {
        public List<RuntimeEvent> Events { get; } = new();

        public void Publish(RuntimeEvent runtimeEvent)
        {
            Events.Add(runtimeEvent);
        }
    }

    private sealed class MalformedBatchResultStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;
        private readonly string _targetKind;
        private int _malformed;

        public MalformedBatchResultStore(
            FileSessionStore inner,
            string targetKind)
        {
            _inner = inner;
            _targetKind = targetKind;
        }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken) =>
            _inner.AppendAsync(runtimeEvent, cancellationToken);

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);

        public async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            var results = await _inner.AppendAtomicBatchAsync(
                    runtimeEvents,
                    expectedRunRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runtimeEvents.Any(
                    item => string.Equals(
                        item.Kind,
                        _targetKind,
                        StringComparison.Ordinal))
                && Interlocked.CompareExchange(
                    ref _malformed,
                    value: 1,
                    comparand: 0) == 0)
            {
                return results
                    .Select(
                        (item, index) => index == results.Count - 1
                            ? new JournalAppendResult(
                                checked(item.Sequence + 1),
                                item.Revision,
                                item.WasDuplicate)
                            : item)
                    .ToArray();
            }

            return results;
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken) =>
            _inner.ReadRunAsync(runId, cancellationToken);

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default) =>
            _inner.GetRunCursorAsync(runId, cancellationToken);

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default) =>
            _inner.FlushAsync(cancellationToken);

        public ValueTask DisposeAsync() => default;

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default) =>
            _inner.GetOperationAsync(operationId, cancellationToken);

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default) =>
            _inner.ReadPendingOperationsAsync(runId, cancellationToken);

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default) =>
            _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
    }

    private sealed class SequentialBatchStore :
        IDurableSessionStore,
        IOperationLedger
    {
        private readonly FileSessionStore _inner;

        public SequentialBatchStore(FileSessionStore inner)
        {
            _inner = inner;
        }

        public ValueTask AppendAsync(
            RuntimeEvent runtimeEvent,
            CancellationToken cancellationToken) =>
            _inner.AppendAsync(runtimeEvent, cancellationToken);

        public ValueTask<JournalAppendResult> AppendAtomicAsync(
            RuntimeEvent runtimeEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAtomicAsync(
                runtimeEvent,
                expectedRunRevision,
                cancellationToken);

        public async ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            var results = new List<JournalAppendResult>(
                runtimeEvents.Count);
            var revision = expectedRunRevision;
            foreach (var runtimeEvent in runtimeEvents)
            {
                var result = await _inner.AppendAtomicAsync(
                    runtimeEvent,
                    revision,
                    cancellationToken);
                results.Add(result);
                revision = result.Revision;
            }

            return results;
        }

        public ValueTask<IReadOnlyList<RuntimeEvent>> ReadRunAsync(
            string runId,
            CancellationToken cancellationToken) =>
            _inner.ReadRunAsync(runId, cancellationToken);

        public ValueTask<RunJournalCursor> GetRunCursorAsync(
            string runId,
            CancellationToken cancellationToken = default) =>
            _inner.GetRunCursorAsync(runId, cancellationToken);

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default) =>
            _inner.FlushAsync(cancellationToken);

        public ValueTask DisposeAsync() => default;

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default) =>
            _inner.GetOperationAsync(operationId, cancellationToken);

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default) =>
            _inner.ReadPendingOperationsAsync(runId, cancellationToken);

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default) =>
            _inner.ReconcileReceiptAsync(
                receiptEvent,
                expectedRunRevision,
                cancellationToken);
    }
}

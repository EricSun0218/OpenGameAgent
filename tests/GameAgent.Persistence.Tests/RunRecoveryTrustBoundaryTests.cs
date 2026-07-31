using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence.Tests;

public sealed class RunRecoveryTrustBoundaryTests
{
    [Fact]
    public async Task LoadSnapshotsIndexedEventsWithoutEnumeratingSource()
    {
        var run = CreateRun();
        var source = new DeclaredReadOnlyList<RuntimeEvent>(
            declaredCount: 1,
            index => index == 0
                ? CheckpointEvent(run, sequence: 0)
                : throw new ArgumentOutOfRangeException(nameof(index)));
        var store = new TrustStore(
            source,
            new RunJournalCursor(run.RunId, 1, 1));
        var ledger = new ScriptedLedger();
        using var journal = Journal(store, ledger);

        var recovered = await new RunRecovery(store, ledger, journal)
            .LoadAsync(run.RunId, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(run.RunId, recovered.Run.RunId);
        Assert.Equal(1, source.CountReads);
        Assert.Equal(1, source.IndexReads);
        Assert.Equal(0, source.EnumerationAttempts);
    }

    [Fact]
    public async Task LoadRejectsEventCountIndexMismatchBeforeLedgerRead()
    {
        var source = new DeclaredReadOnlyList<RuntimeEvent>(
            declaredCount: 1,
            _ => throw new ArgumentOutOfRangeException("index"));
        var store = new TrustStore(
            source,
            new RunJournalCursor("run-1", 1, 1));
        var ledger = new ScriptedLedger();
        using var journal = Journal(store, ledger);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new RunRecovery(store, ledger, journal)
                .LoadAsync("run-1", CancellationToken.None)
                .AsTask());

        Assert.Equal(0, ledger.ReadCount);
        Assert.Equal(0, store.AppendCount);
        Assert.Equal(0, source.EnumerationAttempts);
    }

    [Fact]
    public async Task LoadRejectsAggregateEventBytesBeforeLedgerRead()
    {
        var run = CreateRun();
        var events = new[] { CheckpointEvent(run, sequence: 0) };
        var store = new TrustStore(
            events,
            new RunJournalCursor(run.RunId, 1, 1));
        var ledger = new ScriptedLedger();
        using var journal = Journal(store, ledger);
        var recovery = new RunRecovery(
            store,
            ledger,
            journal,
            new RunRecoveryOptions
            {
                MaxEventsPerRun = 1,
                MaxAggregateEventUtf8Bytes = 1
            });

        await Assert.ThrowsAsync<RunRecoveryBytesCapacityExceededException>(
            () => recovery.LoadAsync(run.RunId, CancellationToken.None)
                .AsTask());

        Assert.Equal(0, ledger.ReadCount);
        Assert.Equal(0, store.AppendCount);
    }

    [Fact]
    public async Task LoadRejectsAssistantCompletionWithoutCommittedTurnEvidence()
    {
        var run = CreateRun();
        var events = new[]
        {
            CheckpointEvent(run, sequence: 0),
            Event(
                "injected-assistant-completion",
                RuntimeEventKinds.AssistantCompleted,
                Json("""{"forged":true}"""),
                sequence: 1,
                turnId: "turn-1")
        };
        var store = new TrustStore(
            events,
            new RunJournalCursor(run.RunId, events.Length, events.Length));
        var ledger = new ScriptedLedger();
        using var journal = Journal(store, ledger);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new RunRecovery(store, ledger, journal)
                .LoadAsync(run.RunId, CancellationToken.None)
                .AsTask());

        Assert.Equal(0, store.AppendCount);
        Assert.Equal(0, ledger.ReadCount);
    }

    [Fact]
    public async Task ReconcileSnapshotsIndexedPendingCollection()
    {
        var run = CreateRun(
            state: RunStates.Reconciling,
            revision: 10);
        var pending = PendingEntry(
            CreateRequest(run, "operation-1", "call-1"));
        var source = new DeclaredReadOnlyList<OperationLedgerEntry>(
            declaredCount: 1,
            index => index == 0
                ? pending
                : throw new ArgumentOutOfRangeException(nameof(index)));
        var store = new TrustStore(
            Array.Empty<RuntimeEvent>(),
            new RunJournalCursor(run.RunId, 10, 10));
        var ledger = new ScriptedLedger(
            new[] { Array.Empty<OperationLedgerEntry>() },
            pending.Request);
        using var journal = Journal(store, ledger);
        var recovered = new RecoveredRun
        {
            Run = run,
            PendingOperations = source
        };
        var reconciler = new CountingReconciler(
            request => CreateReceipt(
                request.OperationId,
                ReceiptStatuses.Unknown,
                revision: 1,
                receivedAt: DateTimeOffset.UnixEpoch));

        await new RunRecovery(store, ledger, journal)
            .ReconcileAsync(
                recovered,
                reconciler,
                "attempt-1",
                CancellationToken.None);

        Assert.Equal(1, reconciler.QueryCount);
        Assert.Equal(1, source.CountReads);
        Assert.Equal(1, source.IndexReads);
        Assert.Equal(0, source.EnumerationAttempts);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("cross_run")]
    [InlineData("cross_agent")]
    [InlineData("cross_world")]
    [InlineData("cross_session")]
    [InlineData("generation")]
    [InlineData("terminal_state")]
    [InlineData("terminal_resurrection")]
    [InlineData("metadata_state_transition")]
    [InlineData("immutable_batch")]
    [InlineData("immutable_decision")]
    [InlineData("immutable_budget")]
    [InlineData("immutable_created_at")]
    public async Task LoadRejectsUntrustedCheckpointWithoutSideEffects(
        string mutation)
    {
        var events = CheckpointEvents(mutation);
        var store = new TrustStore(
            events,
            new RunJournalCursor("run-1", events.Count, events.Count));
        var ledger = new ScriptedLedger();
        using var journal = Journal(store, ledger);
        var recovery = new RunRecovery(store, ledger, journal);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => recovery.LoadAsync("run-1", CancellationToken.None)
                .AsTask());

        Assert.Equal(0, store.AppendCount);
        Assert.Equal(0, ledger.ReadCount);
        Assert.Equal(0, ledger.ReconcileCount);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("wrong_run")]
    [InlineData("negative")]
    [InlineData("wrong_count")]
    public async Task LoadRejectsInvalidCursorBeforeLedgerRead(
        string mutation)
    {
        var events = new[] { CheckpointEvent(CreateRun(), sequence: 0) };
        var cursor = mutation switch
        {
            "null" => null,
            "empty" => new RunJournalCursor(string.Empty, 0, 0),
            "wrong_run" => new RunJournalCursor("run-other", 1, 1),
            "negative" => new RunJournalCursor("run-1", -1, -1),
            _ => new RunJournalCursor("run-1", 2, 2)
        };
        var store = new TrustStore(events, cursor);
        var ledger = new ScriptedLedger();
        using var journal = Journal(store, ledger);
        var recovery = new RunRecovery(store, ledger, journal);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => recovery.LoadAsync("run-1", CancellationToken.None)
                .AsTask());

        Assert.Equal(0, store.AppendCount);
        Assert.Equal(0, ledger.ReadCount);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("message")]
    [InlineData("assistant_tool_call")]
    [InlineData("request_tool_call")]
    [InlineData("terminal_receipt")]
    public async Task LoadRejectsDuplicateSemanticIdentity(
        string duplicateKind)
    {
        var events = DuplicateIdentityEvents(duplicateKind);
        var store = new TrustStore(
            events,
            new RunJournalCursor("run-1", events.Count, events.Count));
        var ledger = new ScriptedLedger();
        using var journal = Journal(store, ledger);
        var recovery = new RunRecovery(store, ledger, journal);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => recovery.LoadAsync("run-1", CancellationToken.None)
                .AsTask());

        Assert.Equal(0, store.AppendCount);
        Assert.Equal(0, ledger.ReadCount);
    }

    [Fact]
    public async Task TerminalReceiptRecoveryIgnoresOnlyReceiveTimestamp()
    {
        var run = CreateRun();
        var request = CreateRequest(run, "operation-1", "call-1");
        var received = CreateReceipt(
            request.OperationId,
            ReceiptStatuses.Succeeded,
            revision: 0,
            receivedAt: DateTimeOffset.UnixEpoch.AddSeconds(1));
        var terminal = CloneReceipt(received);
        terminal.ReceivedAt = terminal.ReceivedAt.AddSeconds(1);
        var events = new[]
        {
            CheckpointEvent(run, sequence: 0),
            RequestEvent(run, request, sequence: 1),
            ReceiptEvent(
                run,
                request,
                received,
                RuntimeEventKinds.ActionReceived,
                sequence: 2),
            ReceiptEvent(
                run,
                request,
                terminal,
                RuntimeEventKinds.ToolCompleted,
                sequence: 3)
        };
        var store = new TrustStore(
            events,
            new RunJournalCursor("run-1", 4, 4));
        var ledger = new ScriptedLedger(
            new[] { Array.Empty<OperationLedgerEntry>() });
        using var journal = Journal(store, ledger);

        var recovered = await new RunRecovery(store, ledger, journal)
            .LoadAsync("run-1", CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Empty(recovered.PendingOperations);
        Assert.Equal(0, store.AppendCount);
    }

    [Theory]
    [InlineData("cross_run")]
    [InlineData("cross_agent")]
    [InlineData("cross_world")]
    [InlineData("cross_session")]
    [InlineData("duplicate")]
    [InlineData("non_pending")]
    [InlineData("request_revision")]
    [InlineData("receipt_metadata")]
    [InlineData("receipt_revision")]
    [InlineData("receipt_operation")]
    public async Task ReconcileRejectsUntrustedPendingBeforeHostQuery(
        string mutation)
    {
        var run = CreateRun(
            state: RunStates.Reconciling,
            revision: 10);
        var store = new TrustStore(
            Array.Empty<RuntimeEvent>(),
            new RunJournalCursor(run.RunId, 10, 10));
        var ledger = new ScriptedLedger();
        using var journal = Journal(store, ledger);
        var recovery = new RunRecovery(store, ledger, journal);
        var recovered = new RecoveredRun
        {
            Run = run,
            PendingOperations = MutatedPendingOperations(
                run,
                mutation,
                afterReceipt: false)
        };
        var reconciler = new CountingReconciler(
            request => CreateReceipt(
                request.OperationId,
                ReceiptStatuses.Unknown,
                revision: 1,
                receivedAt: DateTimeOffset.UnixEpoch));

        var error = await Record.ExceptionAsync(
            () => recovery.ReconcileAsync(
                    recovered,
                    reconciler,
                    "attempt-1",
                    CancellationToken.None)
                .AsTask());

        Assert.NotNull(error);
        Assert.Equal(0, reconciler.QueryCount);
        Assert.Equal(0, ledger.ReconcileCount);
        Assert.Equal(0, store.AppendCount);
    }

    [Theory]
    [InlineData("cross_run")]
    [InlineData("cross_agent")]
    [InlineData("cross_world")]
    [InlineData("cross_session")]
    [InlineData("duplicate")]
    [InlineData("non_pending")]
    [InlineData("request_revision")]
    [InlineData("receipt_metadata")]
    [InlineData("receipt_revision")]
    [InlineData("receipt_operation")]
    public async Task ReconcileRevalidatesUntrustedSecondLedgerRead(
        string mutation)
    {
        var run = CreateRun(
            state: RunStates.Reconciling,
            revision: 10);
        var request = CreateRequest(run, "operation-1", "call-1");
        var initial = PendingEntry(request);
        var secondRead = MutatedPendingOperations(
            run,
            mutation,
            afterReceipt: true);
        var store = new TrustStore(
            Array.Empty<RuntimeEvent>(),
            new RunJournalCursor(run.RunId, 10, 10));
        var ledger = new ScriptedLedger(
            new[] { secondRead },
            request);
        using var journal = Journal(store, ledger);
        var recovery = new RunRecovery(store, ledger, journal);
        var recovered = new RecoveredRun
        {
            Run = run,
            PendingOperations = new[] { initial }
        };
        var reconciler = new CountingReconciler(
            pending => CreateReceipt(
                pending.OperationId,
                ReceiptStatuses.Unknown,
                revision: 1,
                receivedAt: DateTimeOffset.UnixEpoch.AddSeconds(1)));

        var error = await Record.ExceptionAsync(
            () => recovery.ReconcileAsync(
                    recovered,
                    reconciler,
                    "attempt-1",
                    CancellationToken.None)
                .AsTask());

        Assert.NotNull(error);
        Assert.Equal(1, reconciler.QueryCount);
        Assert.Equal(1, ledger.ReconcileCount);
        Assert.Equal(1, ledger.ReadCount);
        Assert.Equal(0, store.AppendCount);
    }

    private static IReadOnlyList<RuntimeEvent> CheckpointEvents(
        string mutation)
    {
        var run = CreateRun();
        if (mutation == "malformed")
        {
            return new[]
            {
                Event(
                    "checkpoint-malformed",
                    RuntimeEventKinds.RunStarted,
                    Json("""{"not":"an agent run"}"""),
                    sequence: 0)
            };
        }

        if (mutation is "cross_run" or "generation")
        {
            var candidate = CloneRun(run);
            if (mutation == "cross_run")
            {
                candidate.RunId = "run-other";
            }
            else
            {
                candidate.RuntimeGeneration = 2;
            }

            return new[]
            {
                CheckpointEvent(
                    candidate,
                    sequence: 0,
                    runtimeGeneration: 1)
            };
        }

        if (mutation == "terminal_resurrection")
        {
            var completed = CloneRun(run);
            completed.State = RunStates.Completed;
            return new[]
            {
                CheckpointEvent(run, sequence: 0),
                CheckpointEvent(
                    completed,
                    sequence: 1,
                    kind: RuntimeEventKinds.RunCompleted),
                CheckpointEvent(
                    run,
                    sequence: 2,
                    kind: RuntimeEventKinds.RunCheckpoint)
            };
        }

        if (mutation == "metadata_state_transition")
        {
            var waiting = CloneRun(run);
            waiting.State = RunStates.WaitingForAction;
            return new[]
            {
                CheckpointEvent(run, sequence: 0),
                CheckpointEvent(
                    waiting,
                    sequence: 1,
                    kind: RuntimeEventKinds.BudgetUpdated)
            };
        }

        var changed = CloneRun(run);
        switch (mutation)
        {
            case "cross_agent":
                changed.AgentId = "agent-other";
                break;
            case "cross_world":
                changed.WorldId = "world-other";
                break;
            case "cross_session":
                changed.SessionId = "session-other";
                break;
            case "terminal_state":
                changed.State = RunStates.Running;
                break;
            case "immutable_batch":
                changed.BatchId = "batch-other";
                break;
            case "immutable_decision":
                changed.DecisionKey = "decision-other";
                break;
            case "immutable_budget":
                changed.Budget.MaxTurns++;
                break;
            case "immutable_created_at":
                changed.CreatedAt = changed.CreatedAt.AddSeconds(1);
                break;
        }

        return new[]
        {
            CheckpointEvent(run, sequence: 0),
            CheckpointEvent(
                changed,
                sequence: 1,
                kind: mutation == "terminal_state"
                    ? RuntimeEventKinds.RunCompleted
                    : RuntimeEventKinds.RunCheckpoint)
        };
    }

    private static IReadOnlyList<RuntimeEvent> DuplicateIdentityEvents(
        string duplicateKind)
    {
        var run = CreateRun();
        var events = new List<RuntimeEvent>
        {
            CheckpointEvent(run, sequence: 0)
        };
        switch (duplicateKind)
        {
            case "operation":
                events.Add(RequestEvent(
                    run,
                    CreateRequest(run, "operation-1", "call-1"),
                    sequence: 1));
                events.Add(RequestEvent(
                    run,
                    CreateRequest(run, "operation-1", "call-2"),
                    sequence: 2));
                break;
            case "message":
                events.Add(MessageEvent(
                    run,
                    TextMessage("message-1"),
                    sequence: 1));
                events.Add(MessageEvent(
                    run,
                    TextMessage("message-1"),
                    sequence: 2));
                break;
            case "assistant_tool_call":
                events.Add(MessageEvent(
                    run,
                    ToolCallMessage("message-1", "call-1"),
                    sequence: 1));
                events.Add(MessageEvent(
                    run,
                    ToolCallMessage("message-2", "call-1"),
                    sequence: 2));
                break;
            case "request_tool_call":
                events.Add(RequestEvent(
                    run,
                    CreateRequest(run, "operation-1", "call-1"),
                    sequence: 1));
                events.Add(RequestEvent(
                    run,
                    CreateRequest(run, "operation-2", "call-1"),
                    sequence: 2));
                break;
            default:
                var request = CreateRequest(
                    run,
                    "operation-1",
                    "call-1");
                var receipt = CreateReceipt(
                    request.OperationId,
                    ReceiptStatuses.Succeeded,
                    revision: 0,
                    receivedAt: DateTimeOffset.UnixEpoch);
                events.Add(RequestEvent(run, request, sequence: 1));
                events.Add(ReceiptEvent(
                    run,
                    request,
                    receipt,
                    RuntimeEventKinds.ActionReceived,
                    sequence: 2));
                events.Add(ReceiptEvent(
                    run,
                    request,
                    receipt,
                    RuntimeEventKinds.ToolCompleted,
                    sequence: 3));
                events.Add(ReceiptEvent(
                    run,
                    request,
                    receipt,
                    RuntimeEventKinds.ToolCompleted,
                    sequence: 4));
                break;
        }

        return events;
    }

    private static IReadOnlyList<OperationLedgerEntry>
        MutatedPendingOperations(
            AgentRun run,
            string mutation,
            bool afterReceipt)
    {
        var request = CreateRequest(run, "operation-1", "call-1");
        switch (mutation)
        {
            case "cross_run":
                request.RunId = "run-other";
                break;
            case "cross_agent":
                request.AgentId = "agent-other";
                break;
            case "cross_world":
                request.WorldId = "world-other";
                break;
        }

        var receiptSequence = afterReceipt ? 10L : 2L;
        var receiptRunRevision = receiptSequence + 1;
        ActionReceipt? receipt = afterReceipt
            ? CreateReceipt(
                request.OperationId,
                ReceiptStatuses.Unknown,
                revision: 1,
                receivedAt: DateTimeOffset.UnixEpoch)
            : null;
        long? latestSequence = afterReceipt ? receiptSequence : null;
        long? latestRevision = afterReceipt ? receiptRunRevision : null;

        switch (mutation)
        {
            case "cross_session":
                receipt = CreateReceipt(
                    request.OperationId,
                    ReceiptStatuses.Unknown,
                    revision: afterReceipt ? 1 : 0,
                    receivedAt: DateTimeOffset.UnixEpoch);
                receipt.AuthoritativeObservations.Add(
                    SessionObservation(run, "session-other"));
                latestSequence = receiptSequence;
                latestRevision = receiptRunRevision;
                break;
            case "non_pending":
                receipt = CreateReceipt(
                    request.OperationId,
                    ReceiptStatuses.Succeeded,
                    revision: afterReceipt ? 1 : 0,
                    receivedAt: DateTimeOffset.UnixEpoch);
                latestSequence = receiptSequence;
                latestRevision = receiptRunRevision;
                break;
            case "request_revision":
                return new[]
                {
                    PendingEntry(
                        request,
                        receipt,
                        requestRunRevision: 3,
                        latestSequence,
                        latestRevision)
                };
            case "receipt_metadata":
                receipt ??= CreateReceipt(
                    request.OperationId,
                    ReceiptStatuses.Unknown,
                    revision: 0,
                    receivedAt: DateTimeOffset.UnixEpoch);
                latestSequence = null;
                latestRevision = receiptRunRevision;
                break;
            case "receipt_revision":
                receipt ??= CreateReceipt(
                    request.OperationId,
                    ReceiptStatuses.Unknown,
                    revision: 0,
                    receivedAt: DateTimeOffset.UnixEpoch);
                latestSequence = receiptSequence;
                latestRevision = receiptRunRevision + 1;
                break;
            case "receipt_operation":
                receipt = CreateReceipt(
                    "operation-other",
                    ReceiptStatuses.Unknown,
                    revision: 0,
                    receivedAt: DateTimeOffset.UnixEpoch);
                latestSequence = receiptSequence;
                latestRevision = receiptRunRevision;
                break;
        }

        var entry = PendingEntry(
            request,
            receipt,
            requestRunRevision: 2,
            latestSequence,
            latestRevision);
        return mutation == "duplicate"
            ? new[] { entry, entry }
            : new[] { entry };
    }

    private static OperationLedgerEntry PendingEntry(
        ActionRequest request,
        ActionReceipt? receipt = null,
        long requestRunRevision = 2,
        long? latestReceiptSequence = null,
        long? latestReceiptRunRevision = null)
    {
        return new OperationLedgerEntry(
            request,
            receipt,
            requestSequence: 1,
            requestRunRevision,
            latestReceiptSequence,
            latestReceiptRunRevision);
    }

    private static AgentRun CreateRun(
        string state = RunStates.Running,
        long revision = 0)
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

    private static AgentRun CloneRun(AgentRun run)
    {
        return ProtocolJson.DeserializeAgentRun(
            ProtocolJson.Serialize(run));
    }

    private static ActionRequest CreateRequest(
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
            ActionName = "move",
            ActionVersion = "1",
            Arguments = Json("""{"destination":"gate"}"""),
            RequestedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static ActionReceipt CreateReceipt(
        string operationId,
        string status,
        long revision,
        DateTimeOffset receivedAt)
    {
        return new ActionReceipt
        {
            OperationId = operationId,
            Revision = revision,
            Status = status,
            Result = status == ReceiptStatuses.Unknown
                ? null
                : Json("""{"moved":true}"""),
            CommittedAt = status == ReceiptStatuses.Unknown
                ? null
                : DateTimeOffset.UnixEpoch,
            ReceivedAt = receivedAt
        };
    }

    private static ActionReceipt CloneReceipt(ActionReceipt receipt)
    {
        return ProtocolJson.DeserializeActionReceipt(
            ProtocolJson.Serialize(receipt));
    }

    private static ObservationEnvelope SessionObservation(
        AgentRun run,
        string sessionId)
    {
        return new ObservationEnvelope
        {
            ObservationId = "observation-1",
            WorldId = run.WorldId,
            SessionId = sessionId,
            Source = "host",
            Kind = "state",
            ContentType = "application/json",
            Payload = Json("""{"value":1}"""),
            ObservedAt = DateTimeOffset.UnixEpoch,
            Trust = "authoritative",
            Visibility = new VisibilityRule
            {
                Scope = ObservationVisibilityScopes.World
            }
        };
    }

    private static RuntimeEvent CheckpointEvent(
        AgentRun run,
        long sequence,
        string kind = RuntimeEventKinds.RunStarted,
        long? runtimeGeneration = null)
    {
        var checkpoint = CloneRun(run);
        checkpoint.Revision = checked(sequence + 1);
        return Event(
            $"checkpoint-{sequence}",
            kind,
            ProtocolJson.ToElement(checkpoint),
            sequence,
            runId: "run-1",
            runtimeGeneration:
                runtimeGeneration ?? checkpoint.RuntimeGeneration);
    }

    private static RuntimeEvent RequestEvent(
        AgentRun run,
        ActionRequest request,
        long sequence)
    {
        return Event(
            $"request-{sequence}",
            RuntimeEventKinds.ActionRequested,
            ProtocolJson.ToElement(request),
            sequence,
            run.RunId,
            run.RuntimeGeneration,
            request.TurnId);
    }

    private static RuntimeEvent ReceiptEvent(
        AgentRun run,
        ActionRequest request,
        ActionReceipt receipt,
        string kind,
        long sequence)
    {
        return Event(
            $"receipt-{sequence}",
            kind,
            ProtocolJson.ToElement(receipt),
            sequence,
            run.RunId,
            run.RuntimeGeneration,
            request.TurnId);
    }

    private static RuntimeEvent MessageEvent(
        AgentRun run,
        NormalizedMessage message,
        long sequence)
    {
        return Event(
            $"message-{sequence}",
            RuntimeEventKinds.TranscriptMessage,
            NormalizedMessageJournalCodec.Encode(message),
            sequence,
            run.RunId,
            run.RuntimeGeneration,
            "turn-1");
    }

    private static RuntimeEvent Event(
        string eventId,
        string kind,
        JsonElement payload,
        long sequence,
        string runId = "run-1",
        long runtimeGeneration = 1,
        string? turnId = null)
    {
        return new RuntimeEvent
        {
            EventId = eventId,
            RunId = runId,
            TurnId = turnId,
            Kind = kind,
            Durability = EventDurabilities.Durable,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            Sequence = sequence,
            RuntimeGeneration = runtimeGeneration,
            Payload = payload.Clone()
        };
    }

    private static NormalizedMessage TextMessage(string messageId)
    {
        return new NormalizedMessage
        {
            MessageId = messageId,
            Role = NormalizedRoles.User,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                NormalizedContentPart.FromText("hello")
            }
        };
    }

    private static NormalizedMessage ToolCallMessage(
        string messageId,
        string toolCallId)
    {
        return new NormalizedMessage
        {
            MessageId = messageId,
            Role = NormalizedRoles.Assistant,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = new List<NormalizedContentPart>
            {
                new()
                {
                    Type = NormalizedPartTypes.ToolCall,
                    ToolCallId = toolCallId,
                    ToolName = "move",
                    Json = Json("""{"destination":"gate"}""")
                }
            }
        };
    }

    private static JournalCoordinator Journal(
        IDurableSessionStore store,
        IOperationLedger ledger)
    {
        return new JournalCoordinator(
            store,
            ledger,
            new SystemRuntimeClock(),
            new GuidRuntimeIdGenerator());
    }

    private static JsonElement Json(string value)
    {
        return ProtocolJson.ParseElement(value);
    }

    private sealed class TrustStore : IDurableSessionStore
    {
        private readonly IReadOnlyList<RuntimeEvent> _events;
        private readonly RunJournalCursor? _cursor;

        public TrustStore(
            IReadOnlyList<RuntimeEvent> events,
            RunJournalCursor? cursor)
        {
            _events = events;
            _cursor = cursor;
        }

        public int AppendCount { get; private set; }

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
            var sequence = expectedRunRevision ?? 0;
            return new ValueTask<JournalAppendResult>(
                new JournalAppendResult(
                    sequence,
                    checked(sequence + 1),
                    wasDuplicate: false));
        }

        public ValueTask<IReadOnlyList<JournalAppendResult>>
            AppendAtomicBatchAsync(
                IReadOnlyList<RuntimeEvent> runtimeEvents,
                long? expectedRunRevision = null,
                CancellationToken cancellationToken = default)
        {
            AppendCount += runtimeEvents.Count;
            var start = expectedRunRevision ?? 0;
            return new ValueTask<IReadOnlyList<JournalAppendResult>>(
                runtimeEvents.Select(
                        (_, index) => new JournalAppendResult(
                            checked(start + index),
                            checked(start + index + 1),
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
            return new ValueTask<RunJournalCursor>(_cursor!);
        }

        public ValueTask FlushAsync(
            CancellationToken cancellationToken = default)
        {
            return default;
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }
    }

    private sealed class DeclaredReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly int _declaredCount;
        private readonly Func<int, T> _read;

        public DeclaredReadOnlyList(
            int declaredCount,
            Func<int, T> read)
        {
            _declaredCount = declaredCount;
            _read = read;
        }

        public int Count
        {
            get
            {
                CountReads++;
                return _declaredCount;
            }
        }

        public T this[int index]
        {
            get
            {
                IndexReads++;
                return _read(index);
            }
        }

        public int CountReads { get; private set; }

        public int IndexReads { get; private set; }

        public int EnumerationAttempts { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationAttempts++;
            throw new NotSupportedException(
                "This trust-boundary collection cannot be enumerated.");
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class ScriptedLedger : IOperationLedger
    {
        private readonly Queue<IReadOnlyList<OperationLedgerEntry>> _reads;
        private readonly ActionRequest? _request;

        public ScriptedLedger(
            IEnumerable<IReadOnlyList<OperationLedgerEntry>>? reads = null,
            ActionRequest? request = null)
        {
            _reads = new Queue<IReadOnlyList<OperationLedgerEntry>>(
                reads ?? Array.Empty<IReadOnlyList<OperationLedgerEntry>>());
            _request = request;
        }

        public int ReadCount { get; private set; }

        public int ReconcileCount { get; private set; }

        public ValueTask<OperationLedgerEntry?> GetOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<OperationLedgerEntry?>((OperationLedgerEntry?)null);
        }

        public ValueTask<IReadOnlyList<OperationLedgerEntry>>
            ReadPendingOperationsAsync(
                string? runId = null,
                CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return new ValueTask<IReadOnlyList<OperationLedgerEntry>>(
                _reads.Count == 0
                    ? Array.Empty<OperationLedgerEntry>()
                    : _reads.Dequeue());
        }

        public ValueTask<ReceiptReconcileResult> ReconcileReceiptAsync(
            RuntimeEvent receiptEvent,
            long? expectedRunRevision = null,
            CancellationToken cancellationToken = default)
        {
            ReconcileCount++;
            var request = _request
                          ?? throw new InvalidOperationException(
                              "No reconciliation request was configured.");
            var receipt = ProtocolJson.DeserializeActionReceipt(
                receiptEvent.Payload.GetRawText());
            var sequence = expectedRunRevision
                           ?? throw new InvalidOperationException(
                               "Expected revision is required.");
            var append = new JournalAppendResult(
                sequence,
                checked(sequence + 1),
                wasDuplicate: false);
            return new ValueTask<ReceiptReconcileResult>(
                new ReceiptReconcileResult(
                    append,
                    new OperationLedgerEntry(
                        request,
                        receipt,
                        requestSequence: 1,
                        requestRunRevision: 2,
                        latestReceiptSequence: append.Sequence,
                        latestReceiptRunRevision: append.Revision)));
        }
    }

    private sealed class CountingReconciler : IGameOperationReconciler
    {
        private readonly Func<ActionRequest, ActionReceipt> _receipt;

        public CountingReconciler(
            Func<ActionRequest, ActionReceipt> receipt)
        {
            _receipt = receipt;
        }

        public int QueryCount { get; private set; }

        public ValueTask<ActionReceipt> QueryOperationAsync(
            ActionRequest request,
            CancellationToken cancellationToken)
        {
            QueryCount++;
            return new ValueTask<ActionReceipt>(_receipt(request));
        }
    }
}

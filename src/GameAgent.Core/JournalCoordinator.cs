using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class JournalCoordinator : IDisposable
{
    private readonly IDurableSessionStore _store;
    private readonly IOperationLedger _operations;
    private readonly IRuntimeClock _clock;
    private readonly IRuntimeIdGenerator _ids;
    private readonly IRuntimeEventPublisher _publisher;
    private readonly BufferedRuntimeEventPublisher? _ownedPublisher;

    public JournalCoordinator(
        IDurableSessionStore store,
        IOperationLedger operations,
        IRuntimeClock clock,
        IRuntimeIdGenerator ids,
        IRuntimeEventPublisher? publisher = null)
    {
        _store = store;
        _operations = operations;
        _clock = clock;
        _ids = ids;
        _publisher = publisher switch
        {
            null => NullRuntimeEventPublisher.Instance,
            INonBlockingRuntimeEventPublisher => publisher,
            _ => _ownedPublisher = new BufferedRuntimeEventPublisher(publisher)
        };
    }

    public void Dispose()
    {
        _ownedPublisher?.Dispose();
    }

    internal ValueTask CommitRunStartAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> initialTranscript,
        CancellationToken cancellationToken)
    {
        return CommitRunStartAsync(
            run,
            initialTranscript,
            Array.Empty<ContextCandidate>(),
            Array.Empty<SkillReference>(),
            cancellationToken);
    }

    internal async ValueTask CommitRunStartAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> initialTranscript,
        IReadOnlyList<ContextCandidate> initialContext,
        IReadOnlyList<SkillReference> activeSkills,
        CancellationToken cancellationToken)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (initialTranscript is null)
        {
            throw new ArgumentNullException(nameof(initialTranscript));
        }

        if (initialContext is null)
        {
            throw new ArgumentNullException(nameof(initialContext));
        }

        if (activeSkills is null)
        {
            throw new ArgumentNullException(nameof(activeSkills));
        }

        var preparingTransition = RunStateMachine.Plan(
            run,
            RunStates.Preparing,
            _clock.UtcNow);
        var preparing = CloneRun(run);
        preparing.State = preparingTransition.ToState;
        preparing.TerminalReason = preparingTransition.TerminalReason;
        preparing.CompletionIntent = preparingTransition.CompletionIntent;
        preparing.Revision = checked(run.Revision + 1);
        preparing.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(preparing);

        var runtimeEvents = new List<RuntimeEvent>(
            checked(
                initialTranscript.Count
                + 2
                + (initialContext.Count > 0 || activeSkills.Count > 0
                    ? 1
                    : 0)))
        {
            CreateEvent(
                run,
                RuntimeEventKinds.RunStarted,
                EventDurabilities.Durable,
                ProtocolJson.ToElement(preparing),
                turnId: null,
                attemptId: null,
                streamAttemptId: null,
                eventId: "run-started:" + run.RunId)
        };
        if (initialContext.Count > 0 || activeSkills.Count > 0)
        {
            runtimeEvents.Add(
                CreateEvent(
                    run,
                    RuntimeEventKinds.RunInputCaptured,
                    EventDurabilities.Durable,
                    DurableRunInputJournalCodec.Encode(
                        initialContext,
                        activeSkills),
                    turnId: null,
                    attemptId: null,
                    streamAttemptId: null,
                    eventId: "run-input:" + run.RunId));
        }

        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in initialTranscript)
        {
            if (message is null)
            {
                throw new ArgumentException(
                    "An initial transcript cannot contain null messages.",
                    nameof(initialTranscript));
            }

            if (!messageIds.Add(message.MessageId))
            {
                throw new ArgumentException(
                    "An initial transcript cannot contain duplicate message ids.",
                    nameof(initialTranscript));
            }

            runtimeEvents.Add(
                CreateEvent(
                    run,
                    RuntimeEventKinds.TranscriptMessage,
                    EventDurabilities.Durable,
                    NormalizedMessageJournalCodec.Encode(message),
                    turnId: "initial",
                    attemptId: null,
                    streamAttemptId: null,
                    eventId: "transcript:" + message.MessageId));
        }

        var runningTransition = RunStateMachine.Plan(
            preparing,
            RunStates.Running,
            _clock.UtcNow);
        var running = CloneRun(preparing);
        running.State = runningTransition.ToState;
        running.TerminalReason = runningTransition.TerminalReason;
        running.CompletionIntent = runningTransition.CompletionIntent;
        running.Revision = checked(
            run.Revision + runtimeEvents.Count + 1);
        running.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(running);
        runtimeEvents.Add(
            CreateEvent(
                run,
                RuntimeEventKinds.RunCheckpoint,
                EventDurabilities.Durable,
                ProtocolJson.ToElement(running),
                turnId: null,
                attemptId: null,
                streamAttemptId: null,
                eventId: "run-ready:" + run.RunId));

        var appends = await _store.AppendAtomicBatchAsync(
                runtimeEvents,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshBatchResults(
            appends,
            runtimeEvents.Count,
            run.Revision,
            "run-start");

        for (var index = 0; index < appends.Count; index++)
        {
            runtimeEvents[index].Sequence = appends[index].Sequence;
        }

        CopyRun(running, run);
        foreach (var runtimeEvent in runtimeEvents)
        {
            Publish(runtimeEvent);
        }
    }

    public ValueTask CommitTransitionAsync(
        AgentRun run,
        string targetState,
        string eventKind,
        string? terminalReason = null,
        string? completionIntent = null,
        string? turnId = null,
        string? attemptId = null,
        CancellationToken cancellationToken = default)
    {
        return CommitTransitionAndMutationAsync(
            run,
            targetState,
            eventKind,
            mutation: null,
            terminalReason,
            completionIntent,
            turnId,
            attemptId,
            cancellationToken);
    }

    internal ValueTask CommitTransitionAndMutationAsync(
        AgentRun run,
        string targetState,
        string eventKind,
        Action<AgentRun>? mutation = null,
        string? terminalReason = null,
        string? completionIntent = null,
        string? turnId = null,
        string? attemptId = null,
        CancellationToken cancellationToken = default)
    {
        var transition = RunStateMachine.Plan(
            run,
            targetState,
            _clock.UtcNow,
            terminalReason,
            completionIntent);
        return CommitRunMutationAsync(
            run,
            eventKind,
            next =>
            {
                next.State = transition.ToState;
                next.TerminalReason = transition.TerminalReason;
                next.CompletionIntent = transition.CompletionIntent;
                mutation?.Invoke(next);
            },
            turnId,
            attemptId,
            cancellationToken);
    }

    internal async ValueTask CommitRunMutationAsync(
        AgentRun run,
        string eventKind,
        Action<AgentRun> mutation,
        string? turnId = null,
        string? attemptId = null,
        CancellationToken cancellationToken = default,
        string? eventId = null,
        string? streamAttemptId = null,
        string? providerId = null,
        string? reasonCode = null,
        string? modelId = null,
        string? transportDialect = null,
        string? providerCapabilityDigest = null,
        string? providerRouteDigest = null)
    {
        var next = CloneRun(run);
        mutation(next);
        next.Revision = checked(run.Revision + 1);
        next.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(next);

        var runtimeEvent = CreateEvent(
            run,
            eventKind,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(next),
            turnId,
            attemptId,
            streamAttemptId,
            eventId,
            providerId,
            reasonCode,
            modelId,
            transportDialect,
            providerCapabilityDigest,
            providerRouteDigest);
        var append = await _store.AppendAtomicAsync(
                runtimeEvent,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshAppendResult(
            append,
            next.Revision,
            "run mutation");

        runtimeEvent.Sequence = append.Sequence;
        CopyRun(next, run);
        Publish(runtimeEvent);
    }

    public async ValueTask AppendTranscriptAsync(
        AgentRun run,
        NormalizedMessage message,
        string turnId,
        string? attemptId,
        CancellationToken cancellationToken)
    {
        var runtimeEvent = CreateEvent(
            run,
            RuntimeEventKinds.TranscriptMessage,
            EventDurabilities.Durable,
            NormalizedMessageJournalCodec.Encode(message),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId: "transcript:" + message.MessageId);
        var append = await _store.AppendAtomicAsync(
                runtimeEvent,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshAppendResult(
            append,
            checked(run.Revision + 1),
            "transcript append");
        run.Revision = append.Revision;
        run.UpdatedAt = _clock.UtcNow;
        runtimeEvent.Sequence = append.Sequence;
        Publish(runtimeEvent);
    }

    public async ValueTask AppendDurableAsync(
        AgentRun run,
        string kind,
        JsonElement payload,
        string? turnId,
        string? attemptId,
        string? streamAttemptId = null,
        string? eventId = null,
        CancellationToken cancellationToken = default)
    {
        var runtimeEvent = CreateEvent(
            run,
            kind,
            EventDurabilities.Durable,
            payload,
            turnId,
            attemptId,
            streamAttemptId,
            eventId);
        var append = await _store.AppendAtomicAsync(
                runtimeEvent,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshAppendResult(
            append,
            checked(run.Revision + 1),
            "durable append");
        run.Revision = append.Revision;
        run.UpdatedAt = _clock.UtcNow;
        runtimeEvent.Sequence = append.Sequence;
        Publish(runtimeEvent);
    }

    public async ValueTask AppendTurnSnapshotAsync(
        AgentRun run,
        TurnSnapshot snapshot,
        string attemptId,
        CancellationToken cancellationToken)
    {
        var runtimeEvent = CreateEvent(
            run,
            RuntimeEventKinds.TurnSnapshot,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(snapshot),
            snapshot.TurnId,
            attemptId,
            streamAttemptId: null,
            eventId: "turn-snapshot:" + snapshot.TurnId);
        var append = await _store.AppendAtomicAsync(
                runtimeEvent,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshAppendResult(
            append,
            checked(run.Revision + 1),
            "turn snapshot append");
        run.Revision = append.Revision;
        run.UpdatedAt = _clock.UtcNow;
        runtimeEvent.Sequence = append.Sequence;
        Publish(runtimeEvent);
    }

    internal async ValueTask CommitTurnPreparationAsync(
        AgentRun run,
        string turnId,
        string attemptId,
        IReadOnlyList<NormalizedMessage> promptMessages,
        TurnSnapshot snapshot,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken,
        ToolDisclosureJournalRecord? toolDisclosure = null)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (promptMessages is null)
        {
            throw new ArgumentNullException(nameof(promptMessages));
        }

        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (!string.Equals(snapshot.RunId, run.RunId, StringComparison.Ordinal)
            || !string.Equals(snapshot.TurnId, turnId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The turn snapshot does not match the prepared run and turn.",
                nameof(snapshot));
        }

        var turnCheckpoint = CloneRun(run);
        turnCheckpoint.CurrentTurnId = turnId;
        turnCheckpoint.Usage.Turns =
            checked(turnCheckpoint.Usage.Turns + 1);
        turnCheckpoint.Usage.DurationMs = Math.Max(
            turnCheckpoint.Usage.DurationMs,
            (long)(_clock.UtcNow - startedAt).TotalMilliseconds);
        turnCheckpoint.Revision = checked(run.Revision + 1);
        turnCheckpoint.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(turnCheckpoint);

        var runtimeEvents = new List<RuntimeEvent>(
            checked(promptMessages.Count + 2
                    + (toolDisclosure is null ? 0 : 1)))
        {
            CreateEvent(
                run,
                RuntimeEventKinds.TurnStarted,
                EventDurabilities.Durable,
                ProtocolJson.ToElement(turnCheckpoint),
                turnId,
                attemptId,
                streamAttemptId: null,
                eventId: "turn-started:" + turnId)
        };
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in promptMessages)
        {
            if (message is null)
            {
                throw new ArgumentException(
                    "Prepared prompt messages cannot contain null entries.",
                    nameof(promptMessages));
            }

            if (!messageIds.Add(message.MessageId))
            {
                throw new ArgumentException(
                    "Prepared prompt messages cannot contain duplicate ids.",
                    nameof(promptMessages));
            }

            runtimeEvents.Add(
                CreateEvent(
                    run,
                    RuntimeEventKinds.TranscriptMessage,
                    EventDurabilities.Durable,
                    NormalizedMessageJournalCodec.Encode(message),
                    turnId,
                    attemptId,
                    streamAttemptId: null,
                    eventId: "transcript:" + message.MessageId));
        }

        if (toolDisclosure is not null)
        {
            runtimeEvents.Add(
                CreateEvent(
                    run,
                    RuntimeEventKinds.ToolDisclosureChanged,
                    EventDurabilities.Durable,
                    ToolDisclosureJournalCodec.Encode(toolDisclosure),
                    turnId,
                    attemptId,
                    streamAttemptId: null,
                    eventId: "tool-disclosure-turn:" + turnId,
                    reasonCode:
                    toolDisclosure.ReasonCodes.FirstOrDefault()));
        }

        runtimeEvents.Add(
            CreateEvent(
                run,
                RuntimeEventKinds.TurnSnapshot,
                EventDurabilities.Durable,
                ProtocolJson.ToElement(snapshot),
                turnId,
                attemptId,
                streamAttemptId: null,
                eventId: "turn-snapshot:" + turnId));

        var finalRun = CloneRun(turnCheckpoint);
        finalRun.Revision = checked(run.Revision + runtimeEvents.Count);
        finalRun.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(finalRun);
        var appends = await _store.AppendAtomicBatchAsync(
                runtimeEvents,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshBatchResults(
            appends,
            runtimeEvents.Count,
            run.Revision,
            "turn-preparation");
        for (var index = 0; index < appends.Count; index++)
        {
            runtimeEvents[index].Sequence = appends[index].Sequence;
        }

        CopyRun(finalRun, run);
        foreach (var runtimeEvent in runtimeEvents)
        {
            Publish(runtimeEvent);
        }
    }

    internal async ValueTask CommitToolDisclosureResultAsync(
        AgentRun run,
        string turnId,
        string attemptId,
        string toolCallId,
        ToolDisclosureJournalRecord toolDisclosure,
        NormalizedMessage resultMessage,
        CancellationToken cancellationToken)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        RuntimeGuard.RequiredId(turnId, nameof(turnId));
        RuntimeGuard.RequiredId(attemptId, nameof(attemptId));
        RuntimeGuard.RequiredId(toolCallId, nameof(toolCallId));
        _ = toolDisclosure
            ?? throw new ArgumentNullException(nameof(toolDisclosure));
        if (resultMessage is null)
        {
            throw new ArgumentNullException(nameof(resultMessage));
        }

        if (!string.Equals(
                resultMessage.Role,
                NormalizedRoles.Tool,
                StringComparison.Ordinal)
            || !resultMessage.Parts.Any(
                part => string.Equals(
                            part.Type,
                            NormalizedPartTypes.ToolResult,
                            StringComparison.Ordinal)
                        && string.Equals(
                            part.ToolCallId,
                            toolCallId,
                            StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A disclosure result must contain the matching tool result.",
                nameof(resultMessage));
        }

        var stateEvent = CreateEvent(
            run,
            RuntimeEventKinds.ToolDisclosureChanged,
            EventDurabilities.Durable,
            ToolDisclosureJournalCodec.Encode(toolDisclosure),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId: "tool-disclosure-control:" + toolCallId,
            reasonCode: toolDisclosure.ReasonCodes.FirstOrDefault());
        var transcriptEvent = CreateEvent(
            run,
            RuntimeEventKinds.TranscriptMessage,
            EventDurabilities.Durable,
            NormalizedMessageJournalCodec.Encode(resultMessage),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId: "transcript:" + resultMessage.MessageId);
        var runtimeEvents = new[] { stateEvent, transcriptEvent };
        var appends = await _store.AppendAtomicBatchAsync(
                runtimeEvents,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshBatchResults(
            appends,
            runtimeEvents.Length,
            run.Revision,
            "tool-disclosure-result");
        for (var index = 0; index < runtimeEvents.Length; index++)
        {
            runtimeEvents[index].Sequence = appends[index].Sequence;
        }

        run.Revision = appends[^1].Revision;
        run.UpdatedAt = _clock.UtcNow;
        foreach (var runtimeEvent in runtimeEvents)
        {
            Publish(runtimeEvent);
        }
    }

    public async ValueTask AppendActionRequestAsync(
        AgentRun run,
        ActionRequest request,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await AppendActionRequestsAsync(
                run,
                new[] { request },
                attemptId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask AppendActionRequestsAsync(
        AgentRun run,
        IReadOnlyList<ActionRequest> requests,
        string attemptId,
        CancellationToken cancellationToken)
    {
        if (requests is null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        if (requests.Count == 0)
        {
            return;
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var runtimeEvents = new List<RuntimeEvent>(requests.Count);
        foreach (var request in requests)
        {
            ValidateActionRequest(run, request);
            if (!operationIds.Add(request.OperationId))
            {
                throw new ArgumentException(
                    "An action batch cannot contain duplicate operation ids.",
                    nameof(requests));
            }

            runtimeEvents.Add(
                CreateEvent(
                    run,
                    RuntimeEventKinds.ActionRequested,
                    EventDurabilities.Durable,
                    ProtocolJson.ToElement(request),
                    request.TurnId,
                    attemptId,
                    streamAttemptId: null,
                    eventId: "action-request:" + request.OperationId));
        }

        IReadOnlyList<JournalAppendResult> appends;
        if (runtimeEvents.Count == 1)
        {
            appends = new[]
            {
                await _store.AppendAtomicAsync(
                        runtimeEvents[0],
                        run.Revision,
                        cancellationToken)
                    .ConfigureAwait(false)
            };
        }
        else
        {
            appends = await _store.AppendAtomicBatchAsync(
                    runtimeEvents,
                    run.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var duplicate = ValidateFreshOrIdempotentBatchResults(
            appends,
            runtimeEvents.Count,
            run.Revision,
            "action request");
        var operations = new OperationLedgerEntry[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            var operation = await _operations.GetOperationAsync(
                    requests[index].OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateActionOperation(
                run,
                requests[index],
                appends[index],
                operation);
            operations[index] = operation!;
        }

        for (var index = 0; index < runtimeEvents.Count; index++)
        {
            var request = requests[index];
            var append = appends[index];
            if (!operations[index].IsPending)
            {
                run.PendingOperationIds.RemoveAll(
                    id => string.Equals(
                        id,
                        request.OperationId,
                        StringComparison.Ordinal));
            }
            else if (!run.PendingOperationIds.Contains(
                         request.OperationId,
                         StringComparer.Ordinal))
            {
                run.PendingOperationIds.Add(request.OperationId);
            }

            runtimeEvents[index].Sequence = append.Sequence;
        }

        if (!duplicate)
        {
            run.Revision = appends[^1].Revision;
            run.UpdatedAt = _clock.UtcNow;
            foreach (var runtimeEvent in runtimeEvents)
            {
                Publish(runtimeEvent);
            }
        }
    }

    private static void ValidateActionRequest(
        AgentRun run,
        ActionRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ProtocolValidator.EnsureValid(request);
        if (!string.Equals(run.RunId, request.RunId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The action request belongs to a different run.",
                nameof(request));
        }
    }

    public async ValueTask AppendActionReceiptAsync(
        AgentRun run,
        string turnId,
        string attemptId,
        ActionReceipt receipt,
        CancellationToken cancellationToken)
    {
        ProtocolValidator.EnsureValid(receipt);
        var runtimeEvent = CreateEvent(
            run,
            RuntimeEventKinds.ActionReceived,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(receipt),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId:
                "action-receipt:"
                + receipt.OperationId
                + ":"
                + receipt.Revision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        ReceiptReconcileResult result;
        RuntimeEvent? completionEvent = null;
        var duplicate = false;
        if (string.Equals(
                receipt.Status,
                ReceiptStatuses.Unknown,
                StringComparison.Ordinal))
        {
            result = await _operations.ReconcileReceiptAsync(
                    runtimeEvent,
                    run.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
            {
                throw new InvalidDataException(
                    "The operation ledger returned no receipt result.");
            }

            if (result.Append is null)
            {
                throw new InvalidDataException(
                    "The operation ledger returned no receipt append result.");
            }

            duplicate = result.Append.WasDuplicate;
            if (duplicate)
            {
                ValidateIdempotentAppendResult(
                    result.Append,
                    run.Revision,
                    "action receipt");
            }
            else
            {
                ValidateFreshAppendResult(
                    result.Append,
                    checked(run.Revision + 1),
                    "action receipt");
            }

            ValidateReceiptOperation(
                run,
                receipt,
                result.Append,
                result.Operation);
        }
        else
        {
            completionEvent = CreateEvent(
                run,
                string.Equals(
                    receipt.Status,
                    ReceiptStatuses.Failed,
                    StringComparison.Ordinal)
                    ? RuntimeEventKinds.ToolFailed
                    : RuntimeEventKinds.ToolCompleted,
                EventDurabilities.Durable,
                ProtocolJson.ToElement(receipt),
                turnId,
                attemptId,
                streamAttemptId: null,
                eventId:
                    "tool-result-event:"
                    + receipt.OperationId
                    + ":"
                    + receipt.Revision.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
            var appends = await _store.AppendAtomicBatchAsync(
                    new[] { runtimeEvent, completionEvent },
                    run.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            duplicate = ValidateFreshOrIdempotentBatchResults(
                appends,
                expectedCount: 2,
                run.Revision,
                "action receipt");

            var operation = await _operations.GetOperationAsync(
                    receipt.OperationId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The committed receipt has no operation-ledger entry.");
            result = new ReceiptReconcileResult(appends[0], operation);
            ValidateReceiptOperation(
                run,
                receipt,
                result.Append,
                result.Operation);
            completionEvent.Sequence = appends[1].Sequence;
        }

        if (!duplicate)
        {
            run.Revision = completionEvent is null
                ? result.Append.Revision
                : checked(result.Append.Revision + 1);
            run.UpdatedAt = _clock.UtcNow;
        }

        if (!result.Operation.IsPending)
        {
            run.PendingOperationIds.RemoveAll(
                id => string.Equals(
                    id,
                    receipt.OperationId,
                    StringComparison.Ordinal));
        }
        else if (!run.PendingOperationIds.Contains(
                     receipt.OperationId,
                     StringComparer.Ordinal))
        {
            run.PendingOperationIds.Add(receipt.OperationId);
        }

        runtimeEvent.Sequence = result.Append.Sequence;
        if (!duplicate)
        {
            Publish(runtimeEvent);
            if (completionEvent is not null)
            {
                Publish(completionEvent);
            }
        }
    }

    public async ValueTask CommitFinalCompletionAsync(
        AgentRun run,
        NormalizedMessage assistantMessage,
        JsonElement finalOutput,
        string turnId,
        string providerId,
        string providerAttemptId,
        string streamAttemptId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (assistantMessage is null)
        {
            throw new ArgumentNullException(nameof(assistantMessage));
        }

        if (!string.Equals(
                assistantMessage.Role,
                NormalizedRoles.Assistant,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A final completion requires an assistant transcript message.",
                nameof(assistantMessage));
        }

        var transition = RunStateMachine.Plan(
            run,
            RunStates.Completed,
            _clock.UtcNow);
        var next = CloneRun(run);
        next.State = transition.ToState;
        next.TerminalReason = transition.TerminalReason;
        next.CompletionIntent = transition.CompletionIntent;
        next.CurrentTurnId = null;
        next.Usage.DurationMs = Math.Max(
            next.Usage.DurationMs,
            (long)(_clock.UtcNow - startedAt).TotalMilliseconds);
        next.Revision = checked(run.Revision + 4);
        next.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(next);
        var resultNext = CloneRun(run);
        resultNext.Revision = checked(run.Revision + 2);
        resultNext.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(resultNext);

        var transcriptEvent = CreateEvent(
            run,
            RuntimeEventKinds.TranscriptMessage,
            EventDurabilities.Durable,
            NormalizedMessageJournalCodec.Encode(assistantMessage),
            turnId,
            providerAttemptId,
            streamAttemptId: null,
            eventId: "transcript:" + assistantMessage.MessageId);
        var resultEvent = CreateEvent(
            run,
            RuntimeEventKinds.ProviderResultCommitted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(resultNext),
            turnId,
            providerAttemptId,
            streamAttemptId,
            eventId: "provider-result-committed:" + streamAttemptId,
            providerId: providerId);
        var assistantEvent = CreateEvent(
            run,
            RuntimeEventKinds.AssistantCompleted,
            EventDurabilities.Durable,
            finalOutput,
            turnId,
            providerAttemptId,
            streamAttemptId,
            eventId: "assistant-completed:" + turnId);
        var completionEvent = CreateEvent(
            run,
            RuntimeEventKinds.RunCompleted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(next),
            turnId,
            providerAttemptId,
            streamAttemptId: null,
            eventId: "run-completed:" + run.RunId);
        var appends = await _store.AppendAtomicBatchAsync(
                new[]
                {
                    transcriptEvent,
                    resultEvent,
                    assistantEvent,
                    completionEvent
                },
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshBatchResults(
            appends,
            expectedCount: 4,
            run.Revision,
            "final completion");

        transcriptEvent.Sequence = appends[0].Sequence;
        resultEvent.Sequence = appends[1].Sequence;
        assistantEvent.Sequence = appends[2].Sequence;
        completionEvent.Sequence = appends[3].Sequence;
        CopyRun(next, run);
        Publish(transcriptEvent);
        Publish(resultEvent);
        Publish(assistantEvent);
        Publish(completionEvent);
    }

    public async ValueTask CommitProviderResultAsync(
        AgentRun run,
        NormalizedMessage assistantMessage,
        string turnId,
        string providerId,
        string providerAttemptId,
        string streamAttemptId,
        CancellationToken cancellationToken)
    {
        var next = CloneRun(run);
        next.Revision = checked(run.Revision + 2);
        next.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(next);
        var transcriptEvent = CreateEvent(
            run,
            RuntimeEventKinds.TranscriptMessage,
            EventDurabilities.Durable,
            NormalizedMessageJournalCodec.Encode(assistantMessage),
            turnId,
            providerAttemptId,
            streamAttemptId: null,
            eventId: "transcript:" + assistantMessage.MessageId);
        var resultEvent = CreateEvent(
            run,
            RuntimeEventKinds.ProviderResultCommitted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(next),
            turnId,
            providerAttemptId,
            streamAttemptId,
            eventId: "provider-result-committed:" + streamAttemptId,
            providerId: providerId);
        var appends = await _store.AppendAtomicBatchAsync(
                new[] { transcriptEvent, resultEvent },
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshBatchResults(
            appends,
            expectedCount: 2,
            run.Revision,
            "provider result");

        transcriptEvent.Sequence = appends[0].Sequence;
        resultEvent.Sequence = appends[1].Sequence;
        CopyRun(next, run);
        Publish(transcriptEvent);
        Publish(resultEvent);
    }

    public async ValueTask CommitCompletionAsync(
        AgentRun run,
        JsonElement finalOutput,
        string turnId,
        string attemptId,
        string streamAttemptId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var transition = RunStateMachine.Plan(
            run,
            RunStates.Completed,
            _clock.UtcNow);
        var next = CloneRun(run);
        next.State = transition.ToState;
        next.TerminalReason = transition.TerminalReason;
        next.CompletionIntent = transition.CompletionIntent;
        next.CurrentTurnId = null;
        next.Usage.DurationMs = Math.Max(
            next.Usage.DurationMs,
            (long)(_clock.UtcNow - startedAt).TotalMilliseconds);
        next.Revision = checked(run.Revision + 2);
        next.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(next);

        var assistantEvent = CreateEvent(
            run,
            RuntimeEventKinds.AssistantCompleted,
            EventDurabilities.Durable,
            finalOutput,
            turnId,
            attemptId,
            streamAttemptId,
            eventId: "assistant-completed:" + turnId);
        var completionEvent = CreateEvent(
            run,
            RuntimeEventKinds.RunCompleted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(next),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId: "run-completed:" + run.RunId);
        var appends = await _store.AppendAtomicBatchAsync(
                new[] { assistantEvent, completionEvent },
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshBatchResults(
            appends,
            expectedCount: 2,
            run.Revision,
            "completion");

        assistantEvent.Sequence = appends[0].Sequence;
        completionEvent.Sequence = appends[1].Sequence;
        CopyRun(next, run);
        Publish(assistantEvent);
        Publish(completionEvent);
    }

    public async ValueTask CommitRecoveredCompletionAsync(
        AgentRun run,
        string turnId,
        string? attemptId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var transition = RunStateMachine.Plan(
            run,
            RunStates.Completed,
            _clock.UtcNow);
        var next = CloneRun(run);
        next.State = transition.ToState;
        next.TerminalReason = transition.TerminalReason;
        next.CompletionIntent = transition.CompletionIntent;
        next.CurrentTurnId = null;
        next.Usage.DurationMs = Math.Max(
            next.Usage.DurationMs,
            (long)(_clock.UtcNow - startedAt).TotalMilliseconds);
        next.Revision = checked(run.Revision + 1);
        next.UpdatedAt = _clock.UtcNow;
        EnsureRunInvariant(next);
        var completionEvent = CreateEvent(
            run,
            RuntimeEventKinds.RunCompleted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(next),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId: "run-completed:" + run.RunId);
        var append = await _store.AppendAtomicAsync(
                completionEvent,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateFreshAppendResult(
            append,
            next.Revision,
            "recovered completion");

        completionEvent.Sequence = append.Sequence;
        CopyRun(next, run);
        Publish(completionEvent);
    }

    public void PublishEphemeral(
        AgentRun run,
        string kind,
        JsonElement payload,
        string? turnId,
        string? attemptId,
        string? streamAttemptId,
        long sequence)
    {
        var runtimeEvent = CreateEvent(
            run,
            kind,
            EventDurabilities.Ephemeral,
            payload,
            turnId,
            attemptId,
            streamAttemptId);
        runtimeEvent.Sequence = sequence;
        Publish(runtimeEvent);
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        return _store.FlushAsync(cancellationToken);
    }

    private static void ValidateFreshAppendResult(
        JournalAppendResult? append,
        long expectedRevision,
        string operation)
    {
        if (append is null
            || append.WasDuplicate
            || append.Revision != expectedRevision
            || append.Sequence < 0)
        {
            throw new InvalidDataException(
                $"The journal returned an invalid {operation} result.");
        }
    }

    private static void ValidateFreshBatchResults(
        IReadOnlyList<JournalAppendResult>? appends,
        int expectedCount,
        long baseRevision,
        string operation)
    {
        if (appends is null
            || appends.Count != expectedCount
            || expectedCount <= 0)
        {
            throw new InvalidDataException(
                $"The journal returned an invalid {operation} batch result.");
        }

        var first = appends[0];
        if (first is null
            || first.Sequence < 0
            || first.Sequence > long.MaxValue - (expectedCount - 1))
        {
            throw new InvalidDataException(
                $"The journal returned an invalid {operation} batch result.");
        }

        for (var index = 0; index < expectedCount; index++)
        {
            var append = appends[index];
            if (append is null
                || append.WasDuplicate
                || append.Revision
                != checked(baseRevision + index + 1)
                || append.Sequence != first.Sequence + index)
            {
                throw new InvalidDataException(
                    $"The journal returned an invalid {operation} batch result.");
            }
        }
    }

    private static bool ValidateFreshOrIdempotentBatchResults(
        IReadOnlyList<JournalAppendResult>? appends,
        int expectedCount,
        long baseRevision,
        string operation)
    {
        if (appends is null
            || appends.Count != expectedCount
            || expectedCount <= 0)
        {
            throw new InvalidDataException(
                $"The journal returned an invalid {operation} batch result.");
        }

        var duplicateCount = appends.Count(
            append => append is not null && append.WasDuplicate);
        if (duplicateCount == 0)
        {
            ValidateFreshBatchResults(
                appends,
                expectedCount,
                baseRevision,
                operation);
            return false;
        }

        if (duplicateCount != expectedCount)
        {
            throw new InvalidDataException(
                $"The journal returned a mixed {operation} batch result.");
        }

        var first = appends[0];
        ValidateIdempotentAppendResult(
            first,
            baseRevision,
            operation);
        if (first.Sequence > long.MaxValue - (expectedCount - 1)
            || first.Revision > long.MaxValue - (expectedCount - 1))
        {
            throw new InvalidDataException(
                $"The journal returned an invalid duplicate {operation} batch.");
        }

        for (var index = 0; index < expectedCount; index++)
        {
            var append = appends[index];
            ValidateIdempotentAppendResult(
                append,
                baseRevision,
                operation);
            if (append.Sequence != first.Sequence + index
                || append.Revision != first.Revision + index)
            {
                throw new InvalidDataException(
                    $"The journal returned an invalid duplicate {operation} batch.");
            }
        }

        return true;
    }

    private static void ValidateIdempotentAppendResult(
        JournalAppendResult? append,
        long baseRevision,
        string operation)
    {
        if (append is null
            || !append.WasDuplicate
            || append.Sequence < 0
            || append.Revision <= 0
            || append.Revision > baseRevision)
        {
            throw new InvalidDataException(
                $"The journal returned an invalid duplicate {operation} result.");
        }
    }

    private static void ValidateActionOperation(
        AgentRun run,
        ActionRequest request,
        JournalAppendResult append,
        OperationLedgerEntry? operation)
    {
        if (operation is null
            || !string.Equals(
                operation.OperationId,
                request.OperationId,
                StringComparison.Ordinal)
            || !string.Equals(
                operation.RunId,
                run.RunId,
                StringComparison.Ordinal)
            || operation.RequestSequence != append.Sequence
            || operation.RequestRunRevision != append.Revision
            || !string.Equals(
                ProtocolJson.Serialize(operation.Request),
                ProtocolJson.Serialize(request),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The operation ledger returned an invalid action request entry.");
        }
    }

    private static void ValidateReceiptOperation(
        AgentRun run,
        ActionReceipt receipt,
        JournalAppendResult append,
        OperationLedgerEntry? operation)
    {
        if (operation is null
            || !string.Equals(
                operation.OperationId,
                receipt.OperationId,
                StringComparison.Ordinal)
            || !string.Equals(
                operation.RunId,
                run.RunId,
                StringComparison.Ordinal)
            || operation.LatestReceipt is null
            || operation.LatestReceiptSequence != append.Sequence
            || operation.LatestReceiptRunRevision != append.Revision
            || !ReceiptsAreEquivalent(
                operation.LatestReceipt,
                receipt))
        {
            throw new InvalidDataException(
                "The operation ledger returned an invalid receipt entry.");
        }
    }

    private static bool ReceiptsAreEquivalent(
        ActionReceipt left,
        ActionReceipt right)
    {
        var canonical = ProtocolJson.DeserializeActionReceipt(
            ProtocolJson.Serialize(left));
        canonical.ReceivedAt = right.ReceivedAt;
        return string.Equals(
            ProtocolJson.Serialize(canonical),
            ProtocolJson.Serialize(right),
            StringComparison.Ordinal);
    }

    private RuntimeEvent CreateEvent(
        AgentRun run,
        string kind,
        string durability,
        JsonElement payload,
        string? turnId,
        string? attemptId,
        string? streamAttemptId,
        string? eventId = null,
        string? providerId = null,
        string? reasonCode = null,
        string? modelId = null,
        string? transportDialect = null,
        string? providerCapabilityDigest = null,
        string? providerRouteDigest = null)
    {
        return new RuntimeEvent
        {
            EventId = eventId ?? _ids.NewId("event"),
            RunId = run.RunId,
            TurnId = turnId,
            Kind = kind,
            Durability = durability,
            RuntimeGeneration = run.RuntimeGeneration,
            AttemptId = attemptId,
            StreamAttemptId = streamAttemptId,
            ProviderId = providerId,
            ModelId = modelId,
            TransportDialect = transportDialect,
            ProviderCapabilityDigest = providerCapabilityDigest,
            ProviderRouteDigest = providerRouteDigest,
            ReasonCode = reasonCode,
            Timestamp = _clock.UtcNow,
            Payload = payload.Clone()
        };
    }

    private void Publish(RuntimeEvent runtimeEvent)
    {
        try
        {
            _publisher.Publish(runtimeEvent);
        }
        catch
        {
            // Runtime events are notifications. The durable journal remains
            // authoritative if an observer violates the publisher contract.
        }
    }

    private static void EnsureRunInvariant(AgentRun run)
    {
        ProtocolValidator.EnsureValid(run);

        if (RunStateMachine.IsTerminal(run.State)
            && run.PendingOperationIds.Count > 0)
        {
            throw new InvalidRunTransitionException(
                run.State,
                run.State,
                "a terminal run cannot retain pending operations");
        }

        if (run.PendingOperationIds.Count
            != run.PendingOperationIds.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException(
                "A run cannot contain duplicate pending operations.");
        }
    }

    internal static AgentRun CloneRun(AgentRun run)
    {
        return ProtocolJson.DeserializeAgentRun(ProtocolJson.Serialize(run));
    }

    internal static void CopyRun(AgentRun source, AgentRun target)
    {
        var clone = CloneRun(source);
        target.ProtocolVersion = clone.ProtocolVersion;
        target.SchemaVersion = clone.SchemaVersion;
        target.Extensions = clone.Extensions;
        target.RunId = clone.RunId;
        target.AgentId = clone.AgentId;
        target.WorldId = clone.WorldId;
        target.SessionId = clone.SessionId;
        target.Trigger = clone.Trigger;
        target.TriggerObservationIds = clone.TriggerObservationIds;
        target.DecisionKey = clone.DecisionKey;
        target.BatchId = clone.BatchId;
        target.State = clone.State;
        target.Revision = clone.Revision;
        target.CurrentTurnId = clone.CurrentTurnId;
        target.RuntimeGeneration = clone.RuntimeGeneration;
        target.Budget = clone.Budget;
        target.Usage = clone.Usage;
        target.PendingOperationIds = clone.PendingOperationIds;
        target.TerminalReason = clone.TerminalReason;
        target.CompletionIntent = clone.CompletionIntent;
        target.CreatedAt = clone.CreatedAt;
        target.UpdatedAt = clone.UpdatedAt;
    }
}

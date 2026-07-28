using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

public interface IGameOperationReconciler
{
    ValueTask<ActionReceipt> QueryOperationAsync(
        ActionRequest request,
        CancellationToken cancellationToken);
}

public sealed class RecoveredRun
{
    public AgentRun Run { get; set; } = new();

    public IReadOnlyList<NormalizedMessage> Transcript { get; set; } =
        Array.Empty<NormalizedMessage>();

    public TurnSnapshot? LastTurnSnapshot { get; set; }

    public JsonElement? FinalOutput { get; set; }

    public IReadOnlyList<OperationLedgerEntry> PendingOperations { get; set; } =
        Array.Empty<OperationLedgerEntry>();

    public IReadOnlyList<RecoveredProviderDispatch>
        UnsettledProviderDispatches
    { get; set; } =
            Array.Empty<RecoveredProviderDispatch>();

    public string? ReplaySafeTurnId { get; set; }

    public IReadOnlyList<ContextCandidate> RecoveryContext { get; set; } =
        Array.Empty<ContextCandidate>();

    public IReadOnlyList<SkillReference> RecoveryActiveSkills { get; set; } =
        Array.Empty<SkillReference>();

    public IReadOnlyList<ToolActivationRecord> RecoveryToolActivations
    { get; set; } = Array.Empty<ToolActivationRecord>();
}

public sealed class RecoveredProviderDispatch
{
    public string ProviderId { get; set; } = string.Empty;

    public string ProviderAttemptId { get; set; } = string.Empty;

    public string StreamAttemptId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public bool UsageSettled { get; set; }
}

public sealed class RunRecovery
{
    internal const string ResultSchemaExtension = "resultSchema";
    internal const string ReplaySafeTurnAbandonedReason =
        "provider_safe_turn_abandoned";

    private readonly IDurableSessionStore _store;
    private readonly IOperationLedger _operations;
    private readonly JournalCoordinator _journal;

    public RunRecovery(
        IDurableSessionStore store,
        IOperationLedger operations,
        JournalCoordinator journal)
    {
        _store = store;
        _operations = operations;
        _journal = journal;
    }

    public async ValueTask<RecoveredRun?> LoadAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var events = await _store.ReadRunAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        if (events.Count == 0)
        {
            return null;
        }

        AgentRun? run = null;
        TurnSnapshot? lastSnapshot = null;
        ToolDisclosureJournalRecord? lastToolDisclosure = null;
        DurableRunInputSnapshot? initialInput = null;
        JsonElement? finalOutput = null;
        var transcript = new List<NormalizedMessage>();
        var transcriptTurnIds = new Dictionary<string, string?>(
            StringComparer.Ordinal);
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);
        var requests = new Dictionary<string, ActionRequest>(
            StringComparer.Ordinal);
        var terminalReceipts = new Dictionary<string, RecoveredReceipt>(
            StringComparer.Ordinal);
        var terminalReceiptEvents = new HashSet<string>(StringComparer.Ordinal);
        var assistantToolCalls = new Dictionary<string, RecoveredToolCall>(
            StringComparer.Ordinal);
        var completedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var durableRequestCallIds = new HashSet<string>(StringComparer.Ordinal);
        var unsettledProviderDispatches =
            new Dictionary<string, RecoveredProviderDispatch>(
                StringComparer.Ordinal);
        var providerUnsafeTurnIds = new HashSet<string>(
            StringComparer.Ordinal);
        var startedTurnIds = new HashSet<string>(StringComparer.Ordinal);
        var abandonedPreProviderTurnIds = new HashSet<string>(
            StringComparer.Ordinal);
        var actionCount = 0;
        var turnCount = 0;
        foreach (var runtimeEvent in events.OrderBy(item => item.Sequence))
        {
            if (IsRunCheckpointKind(runtimeEvent.Kind))
            {
                try
                {
                    var candidate = ProtocolJson.DeserializeAgentRun(
                        runtimeEvent.Payload.GetRawText());
                    if (string.Equals(
                            candidate.RunId,
                            runId,
                            StringComparison.Ordinal))
                    {
                        ProtocolValidator.EnsureValid(candidate);
                        run = candidate;
                    }
                }
                catch (Exception exception) when (
                    exception is System.Text.Json.JsonException
                    or InvalidOperationException)
                {
                    // Some legacy event kinds used a smaller payload. The latest
                    // valid full aggregate remains authoritative.
                }
            }

            if (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ProviderDispatchStarted,
                    StringComparison.Ordinal))
            {
                var dispatch = ReadProviderDispatch(runtimeEvent);
                if (!unsettledProviderDispatches.TryAdd(
                        dispatch.StreamAttemptId,
                        dispatch))
                {
                    throw new InvalidDataException(
                        "The journal contains duplicate provider dispatch "
                        + "identities.");
                }
            }
            else if (IsProviderDispatchSettlement(runtimeEvent.Kind)
                     && !string.IsNullOrWhiteSpace(runtimeEvent.StreamAttemptId))
            {
                if (unsettledProviderDispatches.TryGetValue(
                        runtimeEvent.StreamAttemptId,
                        out var dispatch)
                    && !string.Equals(
                        runtimeEvent.Kind,
                        RuntimeEventKinds.ProviderDispatchKnownZero,
                        StringComparison.Ordinal))
                {
                    providerUnsafeTurnIds.Add(dispatch.TurnId);
                }

                ApplyProviderDispatchSettlement(
                    unsettledProviderDispatches,
                    runtimeEvent);
            }

            if (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.TranscriptMessage,
                    StringComparison.Ordinal))
            {
                var message = NormalizedMessageJournalCodec.Decode(
                    runtimeEvent.Payload);
                if (seenMessages.Add(message.MessageId))
                {
                    transcript.Add(message);
                    transcriptTurnIds[message.MessageId] =
                        runtimeEvent.TurnId;
                }

                foreach (var part in message.Parts)
                {
                    if (string.Equals(
                            part.Type,
                            NormalizedPartTypes.ToolCall,
                            StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(part.ToolCallId)
                        && !string.IsNullOrWhiteSpace(part.ToolName))
                    {
                        assistantToolCalls[part.ToolCallId] =
                            new RecoveredToolCall(
                                part.ToolCallId,
                                part.ToolName,
                                runtimeEvent.TurnId ?? "recovered-turn",
                                runtimeEvent.AttemptId,
                                runtimeEvent.Timestamp);
                    }
                    else if (string.Equals(
                                 part.Type,
                                 NormalizedPartTypes.ToolResult,
                                 StringComparison.Ordinal)
                             && !string.IsNullOrWhiteSpace(part.ToolCallId))
                    {
                        completedToolCallIds.Add(part.ToolCallId);
                    }
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.RunInputCaptured,
                         StringComparison.Ordinal))
            {
                if (initialInput is not null)
                {
                    throw new InvalidDataException(
                        "The journal contains more than one initial "
                        + "run-input snapshot.");
                }

                initialInput = DurableRunInputJournalCodec.Decode(
                    runtimeEvent.Payload);
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.TurnSnapshot,
                         StringComparison.Ordinal))
            {
                lastSnapshot = ProtocolJson.DeserializeTurnSnapshot(
                    runtimeEvent.Payload.GetRawText());
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ToolDisclosureChanged,
                         StringComparison.Ordinal))
            {
                lastToolDisclosure = ToolDisclosureJournalCodec.Decode(
                    runtimeEvent.Payload,
                    maximumActivations: 128);
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ActionRequested,
                         StringComparison.Ordinal))
            {
                actionCount++;
                var request = ProtocolJson.DeserializeActionRequest(
                    runtimeEvent.Payload.GetRawText());
                requests[request.OperationId] = request;
                durableRequestCallIds.Add(request.ToolCallId);
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ActionReceived,
                         StringComparison.Ordinal))
            {
                var parsedReceipt = ProtocolJson.DeserializeActionReceipt(
                    runtimeEvent.Payload.GetRawText());
                if (!requests.TryGetValue(
                        parsedReceipt.OperationId,
                        out var request))
                {
                    throw new InvalidDataException(
                        "A recovered receipt has no preceding action request.");
                }

                var receipt = ActionReceiptIngressValidator.ValidateAndClone(
                    request,
                    parsedReceipt);
                if (!string.Equals(
                        receipt.Status,
                        ReceiptStatuses.Unknown,
                        StringComparison.Ordinal))
                {
                    terminalReceipts[ReceiptKey(receipt)] =
                        new RecoveredReceipt(
                            receipt,
                            runtimeEvent.TurnId ?? request.TurnId,
                            runtimeEvent.AttemptId);
                    var message = NormalizedTranscript.ToolResult(
                        ToolResultMessageId(receipt),
                        request.ToolCallId,
                        request.ActionName,
                        receipt,
                        receipt.ReceivedAt);
                    if (seenMessages.Add(message.MessageId))
                    {
                        transcript.Add(message);
                        transcriptTurnIds[message.MessageId] =
                            request.TurnId;
                    }

                    completedToolCallIds.Add(request.ToolCallId);
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ToolCompleted,
                         StringComparison.Ordinal)
                     || string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.ToolFailed,
                         StringComparison.Ordinal))
            {
                var parsedReceipt = ProtocolJson.DeserializeActionReceipt(
                    runtimeEvent.Payload.GetRawText());
                if (!requests.TryGetValue(
                        parsedReceipt.OperationId,
                        out var request))
                {
                    throw new InvalidDataException(
                        "A recovered terminal receipt event has no "
                        + "preceding action request.");
                }

                var receipt = ActionReceiptIngressValidator.ValidateAndClone(
                    request,
                    parsedReceipt);
                terminalReceiptEvents.Add(ReceiptKey(receipt));
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.TurnCompleted,
                         StringComparison.Ordinal)
                     && string.Equals(
                         runtimeEvent.ReasonCode,
                         ReplaySafeTurnAbandonedReason,
                         StringComparison.Ordinal)
                     && !string.IsNullOrWhiteSpace(runtimeEvent.TurnId))
            {
                abandonedPreProviderTurnIds.Add(runtimeEvent.TurnId);
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.TurnStarted,
                         StringComparison.Ordinal))
            {
                turnCount++;
                if (!string.IsNullOrWhiteSpace(runtimeEvent.TurnId))
                {
                    startedTurnIds.Add(runtimeEvent.TurnId);
                }
            }
            else if (string.Equals(
                         runtimeEvent.Kind,
                         RuntimeEventKinds.AssistantCompleted,
                         StringComparison.Ordinal))
            {
                finalOutput = runtimeEvent.Payload.Clone();
            }
        }

        if (run is null)
        {
            throw new InvalidDataException(
                $"Run '{runId}' has journal entries but no recoverable checkpoint.");
        }

        var cursor = await _store.GetRunCursorAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        run.Revision = cursor.Revision;
        run.Usage.Actions = Math.Max(run.Usage.Actions, actionCount);
        var abandonedStartedTurns = abandonedPreProviderTurnIds.Count(
            startedTurnIds.Contains);
        run.Usage.Turns = Math.Max(
            run.Usage.Turns,
            Math.Max(0, turnCount - abandonedStartedTurns));

        var pending = await _operations.ReadPendingOperationsAsync(
                runId,
                cancellationToken)
            .ConfigureAwait(false);
        run.PendingOperationIds = pending
            .Select(item => item.OperationId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
        if (run.PendingOperationIds.Count > 0
            && run.State != RunStates.Reconciling)
        {
            run.State = RunStates.Reconciling;
        }

        string? replaySafeTurnId = null;
        if (string.Equals(
                run.State,
                RunStates.Running,
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(run.CurrentTurnId)
            && startedTurnIds.Contains(run.CurrentTurnId)
            && !abandonedPreProviderTurnIds.Contains(run.CurrentTurnId)
            && !providerUnsafeTurnIds.Contains(run.CurrentTurnId)
            && !unsettledProviderDispatches.Values.Any(
                dispatch => string.Equals(
                    dispatch.TurnId,
                    run.CurrentTurnId,
                    StringComparison.Ordinal)))
        {
            replaySafeTurnId = run.CurrentTurnId;
        }

        var abandonedTurnIds = new HashSet<string>(
            abandonedPreProviderTurnIds,
            StringComparer.Ordinal);
        if (replaySafeTurnId is not null)
        {
            abandonedTurnIds.Add(replaySafeTurnId);
        }

        transcript.RemoveAll(
            message => transcriptTurnIds.TryGetValue(
                           message.MessageId,
                           out var turnId)
                       && turnId is not null
                       && abandonedTurnIds.Contains(turnId)
                       && IsTurnOutputMessage(message));
        foreach (var toolCallId in assistantToolCalls
                     .Where(
                         pair => abandonedTurnIds.Contains(
                             pair.Value.TurnId))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            assistantToolCalls.Remove(toolCallId);
        }

        IReadOnlyList<ContextCandidate> recoveryContext =
            Array.Empty<ContextCandidate>();
        IReadOnlyList<SkillReference> recoveryActiveSkills =
            Array.Empty<SkillReference>();
        if (replaySafeTurnId is not null)
        {
            recoveryActiveSkills = ReadActivatedSkills(
                transcript,
                transcriptTurnIds,
                replaySafeTurnId);
        }
        else if (lastSnapshot is not null)
        {
            recoveryActiveSkills = ReadActivatedSkills(
                transcript,
                transcriptTurnIds,
                lastSnapshot.TurnId);
        }
        else if (lastSnapshot is null && initialInput is not null)
        {
            recoveryContext = initialInput.Context;
            recoveryActiveSkills = initialInput.ActiveSkills;
        }

        ProtocolValidator.EnsureValid(run);

        foreach (var toolCall in assistantToolCalls.Values)
        {
            if (RunStateMachine.IsTerminal(run.State)
                || durableRequestCallIds.Contains(toolCall.ToolCallId)
                || completedToolCallIds.Contains(toolCall.ToolCallId))
            {
                continue;
            }

            var message = new NormalizedMessage
            {
                MessageId = "tool-dispatch-aborted:" + toolCall.ToolCallId,
                Role = NormalizedRoles.Tool,
                CreatedAt = toolCall.Timestamp,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromToolResult(
                        toolCall.ToolCallId,
                        toolCall.ToolName,
                        RuntimePromptBuilder.ErrorPayload(
                            ToolDisclosureControlNames.IsReserved(
                                toolCall.ToolName)
                                ? "tool_control_not_committed"
                                : "action_dispatch_not_committed",
                            "recovery",
                            ToolDisclosureControlNames.IsReserved(
                                toolCall.ToolName)
                                ? "The runtime control was not committed. "
                                  + "Replan the call."
                                : "The action batch was not committed and no "
                                  + "game action was dispatched. Replan the "
                                  + "call."))
                }
            };
            await _journal.AppendTranscriptAsync(
                    run,
                    message,
                    toolCall.TurnId,
                    toolCall.AttemptId,
                    cancellationToken)
                .ConfigureAwait(false);
            transcript.Add(message);
        }

        foreach (var pair in terminalReceipts)
        {
            if (terminalReceiptEvents.Contains(pair.Key))
            {
                continue;
            }

            var recoveredReceipt = pair.Value;
            await _journal.AppendDurableAsync(
                    run,
                    string.Equals(
                        recoveredReceipt.Receipt.Status,
                        ReceiptStatuses.Failed,
                        StringComparison.Ordinal)
                        ? RuntimeEventKinds.ToolFailed
                        : RuntimeEventKinds.ToolCompleted,
                    ProtocolJson.ToElement(recoveredReceipt.Receipt),
                    recoveredReceipt.TurnId,
                    recoveredReceipt.AttemptId,
                    eventId:
                        "tool-result-event:"
                        + recoveredReceipt.Receipt.OperationId
                        + ":"
                        + recoveredReceipt.Receipt.Revision.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return new RecoveredRun
        {
            Run = run,
            Transcript = transcript,
            LastTurnSnapshot = lastSnapshot,
            FinalOutput = finalOutput,
            PendingOperations = pending,
            ReplaySafeTurnId = replaySafeTurnId,
            RecoveryContext = recoveryContext,
            RecoveryActiveSkills = recoveryActiveSkills,
            RecoveryToolActivations = lastToolDisclosure?.Activations
                .Select(item => item.Clone())
                .ToArray()
                ?? Array.Empty<ToolActivationRecord>(),
            UnsettledProviderDispatches =
                unsettledProviderDispatches.Values
                    .OrderBy(item => item.StreamAttemptId, StringComparer.Ordinal)
                    .ToArray()
        };
    }

    public async ValueTask<RecoveredRun> ReconcileAsync(
        RecoveredRun recovered,
        IGameOperationReconciler reconciler,
        string attemptId,
        CancellationToken cancellationToken)
    {
        foreach (var pending in recovered.PendingOperations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProtocolValidator.EnsureValid(pending.Request);
            JsonValueInspector.ValidateAndMeasure(
                ProtocolJson.ToElement(pending.Request),
                new JsonValueLimits(),
                nameof(pending.Request));
            var hostReceipt = await QueryOperationWithDeadlineAsync(
                    reconciler,
                    pending.Request,
                    cancellationToken)
                .ConfigureAwait(false);
            var receipt = ValidateReconciledReceipt(
                pending.Request,
                hostReceipt);
            if (!string.Equals(
                    receipt.OperationId,
                    pending.OperationId,
                    StringComparison.Ordinal))
            {
                throw new OperationLedgerConflictException(
                    pending.OperationId,
                    "the host returned a receipt for a different operation.");
            }

            await _journal.AppendActionReceiptAsync(
                    recovered.Run,
                    pending.Request.TurnId,
                    attemptId,
                    receipt,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!string.Equals(
                    receipt.Status,
                    ReceiptStatuses.Unknown,
                    StringComparison.Ordinal))
            {
                var message = NormalizedTranscript.ToolResult(
                    ToolResultMessageId(receipt),
                    pending.Request.ToolCallId,
                    pending.Request.ActionName,
                    receipt,
                    receipt.ReceivedAt);
                await _journal.AppendTranscriptAsync(
                        recovered.Run,
                        message,
                        pending.Request.TurnId,
                        attemptId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!recovered.Transcript.Any(
                        item => string.Equals(
                            item.MessageId,
                            message.MessageId,
                            StringComparison.Ordinal)))
                {
                    recovered.Transcript = recovered.Transcript
                        .Concat(new[] { message })
                        .ToArray();
                }
            }
        }

        var remaining = await _operations.ReadPendingOperationsAsync(
                recovered.Run.RunId,
                CancellationToken.None)
            .ConfigureAwait(false);
        recovered.PendingOperations = remaining;
        recovered.Run.PendingOperationIds = remaining
            .Select(item => item.OperationId)
            .ToList();
        return recovered;
    }

    private static async ValueTask<ActionReceipt>
        QueryOperationWithDeadlineAsync(
            IGameOperationReconciler reconciler,
            ActionRequest request,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queryRequest = ProtocolJson.DeserializeActionRequest(
            ProtocolJson.Serialize(request));
        var query = reconciler.QueryOperationAsync(
                queryRequest,
                cancellationToken)
            .AsTask();
        if (!cancellationToken.CanBeCanceled)
        {
            return await query.ConfigureAwait(false);
        }

        var cancellation = Task.Delay(
            Timeout.InfiniteTimeSpan,
            cancellationToken);
        var completed = await Task.WhenAny(query, cancellation)
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, query) && !query.IsCompleted)
        {
            _ = ObserveDetachedQueryAsync(query);
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }

        return await query.ConfigureAwait(false);
    }

    private static async Task ObserveDetachedQueryAsync(
        Task<ActionReceipt> query)
    {
        try
        {
            _ = await query.ConfigureAwait(false);
        }
        catch
        {
            // The run has already crossed its deadline. Observing a late
            // reconciliation failure prevents an unobserved task exception.
        }
    }

    private static ActionReceipt ValidateReconciledReceipt(
        ActionRequest request,
        ActionReceipt hostReceipt)
    {
        if (hostReceipt is null)
        {
            throw new InvalidDataException(
                "The operation reconciler returned a null receipt.");
        }

        var receipt = ActionReceiptIngressValidator.ValidateAndClone(
            request,
            hostReceipt);
        var limits = new JsonValueLimits();

        if (string.Equals(
                receipt.Status,
                ReceiptStatuses.Succeeded,
                StringComparison.Ordinal)
            && request.Extensions.TryGetValue(
                ResultSchemaExtension,
                out var resultSchema))
        {
            JsonValueInspector.ValidateAndMeasure(
                resultSchema,
                limits,
                ResultSchemaExtension);
            var result = receipt.Result
                         ?? ProtocolJson.ParseElement("null");
            var validation = new ToolArgumentValidator().Validate(
                resultSchema,
                result);
            if (!validation.IsValid)
            {
                receipt.Result = null;
                receipt.ErrorCode = "tool_result_schema_invalid";
                receipt.Retryable = false;
            }
        }

        return receipt;
    }

    internal static string ToolResultMessageId(ActionReceipt receipt)
    {
        return "tool-result:"
               + receipt.OperationId
               + ":"
               + receipt.Revision.ToString(
                   System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReceiptKey(ActionReceipt receipt)
    {
        return receipt.OperationId
               + "\0"
               + receipt.Revision.ToString(
                   System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsRunCheckpointKind(string kind)
    {
        return kind == RuntimeEventKinds.RunStarted
            || kind == RuntimeEventKinds.RunCompleted
            || kind == RuntimeEventKinds.RunInterrupted
            || kind == RuntimeEventKinds.RunFailed
            || kind == RuntimeEventKinds.RunCancelled
            || kind == RuntimeEventKinds.RunBudgetExhausted
            || kind == RuntimeEventKinds.RunCheckpoint
            || kind == RuntimeEventKinds.TurnStarted
            || kind == RuntimeEventKinds.TurnCompleted
            || kind == RuntimeEventKinds.BudgetUpdated
            || kind == RuntimeEventKinds.ProviderDispatchStarted
            || kind == RuntimeEventKinds.ProviderDispatchKnownZero
            || kind == RuntimeEventKinds.ProviderUsageUncertain
            || kind == RuntimeEventKinds.ProviderResultCommitted
            || kind == RuntimeEventKinds.ProviderResultDiscarded
            || kind == RuntimeEventKinds.ActionReconciling;
    }

    private static bool IsProviderDispatchSettlement(string kind)
    {
        return kind == RuntimeEventKinds.BudgetUpdated
            || kind == RuntimeEventKinds.ProviderDispatchKnownZero
            || kind == RuntimeEventKinds.ProviderResultCommitted
            || kind == RuntimeEventKinds.ProviderResultDiscarded
            || kind == RuntimeEventKinds.ProviderUsageUncertain;
    }

    private static bool IsTurnOutputMessage(NormalizedMessage message)
    {
        return string.Equals(
                   message.Role,
                   NormalizedRoles.Assistant,
                   StringComparison.Ordinal)
               || string.Equals(
                   message.Role,
                   NormalizedRoles.Tool,
                   StringComparison.Ordinal);
    }

    private static IReadOnlyList<SkillReference> ReadActivatedSkills(
        IReadOnlyList<NormalizedMessage> transcript,
        IReadOnlyDictionary<string, string?> transcriptTurnIds,
        string turnId)
    {
        List<SkillReference>? latest = null;
        List<SkillReference>? matchingTurn = null;
        foreach (var message in transcript)
        {
            if (!string.Equals(
                    message.Role,
                    NormalizedRoles.System,
                    StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var part in message.Parts)
            {
                if (!part.Json.HasValue
                    || part.Json.Value.ValueKind != JsonValueKind.Object
                    || !part.Json.Value.TryGetProperty(
                        "contentType",
                        out var contentType)
                    || !string.Equals(
                        contentType.GetString(),
                        "application/vnd.game-agent.skills+json",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!part.Json.Value.TryGetProperty(
                        "activated",
                        out var activatedElement)
                    || activatedElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException(
                        "A durable skill disclosure has no activated-skill "
                        + "array.");
                }

                var next = new List<SkillReference>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in activatedElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("skillId", out var skillId)
                        || skillId.ValueKind != JsonValueKind.String
                        || !item.TryGetProperty("version", out var version)
                        || version.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            "A durable activated-skill entry is malformed.");
                    }

                    var reference = new SkillReference(
                        skillId.GetString()!,
                        version.GetString()!);
                    if (!seen.Add(reference.Value))
                    {
                        throw new InvalidDataException(
                            "A durable skill disclosure contains duplicate "
                            + "activated skills.");
                    }

                    next.Add(reference);
                }

                latest = next;
                if (transcriptTurnIds.TryGetValue(
                        message.MessageId,
                        out var messageTurnId)
                    && string.Equals(
                        messageTurnId,
                        turnId,
                        StringComparison.Ordinal))
                {
                    matchingTurn = next;
                }
            }
        }

        return (matchingTurn ?? latest)?.ToArray()
               ?? Array.Empty<SkillReference>();
    }

    private static void ApplyProviderDispatchSettlement(
        IDictionary<string, RecoveredProviderDispatch> dispatches,
        RuntimeEvent runtimeEvent)
    {
        if (!dispatches.TryGetValue(
                runtimeEvent.StreamAttemptId!,
                out var dispatch))
        {
            throw new InvalidDataException(
                "A provider settlement has no preceding dispatch.");
        }

        if (!string.Equals(
                runtimeEvent.ProviderId,
                dispatch.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                runtimeEvent.AttemptId,
                dispatch.ProviderAttemptId,
                StringComparison.Ordinal)
            || !string.Equals(
                runtimeEvent.TurnId,
                dispatch.TurnId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A provider settlement does not match its dispatch identity.");
        }

        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.BudgetUpdated,
                StringComparison.Ordinal))
        {
            if (dispatch.UsageSettled)
            {
                throw new InvalidDataException(
                    "A provider dispatch has more than one usage settlement.");
            }

            dispatch.UsageSettled = true;
            return;
        }

        if ((string.Equals(
                 runtimeEvent.Kind,
                 RuntimeEventKinds.ProviderResultCommitted,
                 StringComparison.Ordinal)
             || string.Equals(
                 runtimeEvent.Kind,
                 RuntimeEventKinds.ProviderResultDiscarded,
                 StringComparison.Ordinal))
            && !dispatch.UsageSettled)
        {
            throw new InvalidDataException(
                "A provider result settled before its usage.");
        }

        if (dispatch.UsageSettled
            && (string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ProviderDispatchKnownZero,
                    StringComparison.Ordinal)
                || string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.ProviderUsageUncertain,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A provider usage settlement is contradictory.");
        }

        dispatches.Remove(runtimeEvent.StreamAttemptId!);
    }

    private static RecoveredProviderDispatch ReadProviderDispatch(
        RuntimeEvent runtimeEvent)
    {
        if (string.IsNullOrWhiteSpace(runtimeEvent.ProviderId)
            || string.IsNullOrWhiteSpace(runtimeEvent.AttemptId)
            || string.IsNullOrWhiteSpace(runtimeEvent.StreamAttemptId)
            || string.IsNullOrWhiteSpace(runtimeEvent.TurnId))
        {
            throw new InvalidDataException(
                "A provider dispatch checkpoint is missing its identity.");
        }

        return new RecoveredProviderDispatch
        {
            ProviderId = runtimeEvent.ProviderId,
            ProviderAttemptId = runtimeEvent.AttemptId,
            StreamAttemptId = runtimeEvent.StreamAttemptId,
            TurnId = runtimeEvent.TurnId
        };
    }

    private sealed class RecoveredReceipt
    {
        public RecoveredReceipt(
            ActionReceipt receipt,
            string turnId,
            string? attemptId)
        {
            Receipt = receipt;
            TurnId = turnId;
            AttemptId = attemptId;
        }

        public ActionReceipt Receipt { get; }

        public string TurnId { get; }

        public string? AttemptId { get; }
    }

    private sealed class RecoveredToolCall
    {
        public RecoveredToolCall(
            string toolCallId,
            string toolName,
            string turnId,
            string? attemptId,
            DateTimeOffset timestamp)
        {
            ToolCallId = toolCallId;
            ToolName = toolName;
            TurnId = turnId;
            AttemptId = attemptId;
            Timestamp = timestamp;
        }

        public string ToolCallId { get; }

        public string ToolName { get; }

        public string TurnId { get; }

        public string? AttemptId { get; }

        public DateTimeOffset Timestamp { get; }
    }
}

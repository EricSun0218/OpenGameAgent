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
            ProviderWorkloadClasses.Interactive,
            DurableExecutionModes.Agent,
            cancellationToken);
    }

    internal async ValueTask CommitRunStartAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> initialTranscript,
        IReadOnlyList<ContextCandidate> initialContext,
        IReadOnlyList<SkillReference> activeSkills,
        CancellationToken cancellationToken)
    {
        await CommitRunStartAsync(
                run,
                initialTranscript,
                initialContext,
                activeSkills,
                ProviderWorkloadClasses.Interactive,
                DurableExecutionModes.Agent,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask CommitRunStartAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> initialTranscript,
        IReadOnlyList<ContextCandidate> initialContext,
        IReadOnlyList<SkillReference> activeSkills,
        string workloadClass,
        CancellationToken cancellationToken)
    {
        await CommitRunStartAsync(
                run,
                initialTranscript,
                initialContext,
                activeSkills,
                workloadClass,
                DurableExecutionModes.Agent,
                inference: null,
                routePreference: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask CommitRunStartAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> initialTranscript,
        IReadOnlyList<ContextCandidate> initialContext,
        IReadOnlyList<SkillReference> activeSkills,
        string workloadClass,
        string executionMode,
        CancellationToken cancellationToken)
    {
        await CommitRunStartAsync(
                run,
                initialTranscript,
                initialContext,
                activeSkills,
                workloadClass,
                executionMode,
                inference: null,
                routePreference: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask CommitRunStartAsync(
        AgentRun run,
        IReadOnlyList<NormalizedMessage> initialTranscript,
        IReadOnlyList<ContextCandidate> initialContext,
        IReadOnlyList<SkillReference> activeSkills,
        string workloadClass,
        string executionMode,
        ModelInferenceOptions? inference,
        ProviderRoutePreference? routePreference,
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

        workloadClass = ProviderWorkloadClasses.Normalize(
            workloadClass,
            nameof(workloadClass));
        executionMode = DurableExecutionModes.Normalize(
            executionMode,
            nameof(executionMode));
        var preparingTimestamp = MonotonicNow(run);
        var preparingTransition = RunStateMachine.Plan(
            run,
            RunStates.Preparing,
            preparingTimestamp);
        var preparing = CloneRun(run);
        preparing.State = preparingTransition.ToState;
        preparing.TerminalReason = preparingTransition.TerminalReason;
        preparing.CompletionIntent = preparingTransition.CompletionIntent;
        preparing.Revision = checked(run.Revision + 1);
        preparing.UpdatedAt = preparingTimestamp;
        EnsureRunInvariant(preparing);

        var runtimeEvents = new List<RuntimeEvent>(
            checked(
                initialTranscript.Count
                + 2
                + (initialContext.Count > 0
                   || activeSkills.Count > 0
                   || !string.Equals(
                       workloadClass,
                       ProviderWorkloadClasses.Interactive,
                       StringComparison.Ordinal)
                   || !string.Equals(
                       executionMode,
                       DurableExecutionModes.Agent,
                       StringComparison.Ordinal)
                   || inference is not null
                   || routePreference is not null
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
        if (initialContext.Count > 0
            || activeSkills.Count > 0
            || !string.Equals(
                workloadClass,
                ProviderWorkloadClasses.Interactive,
                StringComparison.Ordinal)
            || !string.Equals(
                executionMode,
                DurableExecutionModes.Agent,
                StringComparison.Ordinal)
            || inference is not null
            || routePreference is not null)
        {
            runtimeEvents.Add(
                CreateEvent(
                    run,
                    RuntimeEventKinds.RunInputCaptured,
                    EventDurabilities.Durable,
                    DurableRunInputJournalCodec.Encode(
                        initialContext,
                        activeSkills,
                        workloadClass,
                        executionMode,
                        inference,
                        routePreference),
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

        var runningTimestamp = MonotonicNow(preparing);
        var runningTransition = RunStateMachine.Plan(
            preparing,
            RunStates.Running,
            runningTimestamp);
        var running = CloneRun(preparing);
        running.State = runningTransition.ToState;
        running.TerminalReason = runningTransition.TerminalReason;
        running.CompletionIntent = runningTransition.CompletionIntent;
        running.Revision = checked(
            run.Revision + runtimeEvents.Count + 1);
        running.UpdatedAt = runningTimestamp;
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

        var rawAppends = await _store.AppendAtomicBatchAsync(
                runtimeEvents,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        var appends = SnapshotBatchResults(
            rawAppends,
            runtimeEvents.Count,
            "run-start");
        ValidateFreshBatchResults(
            appends,
            runtimeEvents.Count,
            run.Revision,
            "run-start");

        for (var index = 0; index < appends.Length; index++)
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
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, JsonElement>? eventExtensions = null)
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
            cancellationToken,
            eventExtensions);
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
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, JsonElement>? eventExtensions = null)
    {
        ValidateTransitionEventKind(targetState, eventKind);
        var transitionTimestamp = MonotonicNow(run);
        var transition = RunStateMachine.Plan(
            run,
            targetState,
            transitionTimestamp,
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
            cancellationToken,
            eventExtensions: eventExtensions);
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
        string? providerRouteDigest = null,
        IReadOnlyDictionary<string, JsonElement>? eventExtensions = null)
    {
        var next = CloneRun(run);
        mutation(next);
        next.Revision = checked(run.Revision + 1);
        next.UpdatedAt = MonotonicNow(run);
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
        if (eventExtensions is not null)
        {
            runtimeEvent.Extensions = eventExtensions.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal);
        }

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

    internal ValueTask CommitGameContextAdvancementAsync(
        AgentRun run,
        string turnId,
        string? attemptId,
        GameContextAdvancementPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var current = GameContextEnvelope.ValidateForRun(
            run,
            nameof(run))
            ?? throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.TransitionConflict,
                "A game-context advancement requires an active "
                + "coordinate.");
        if (!GameContextAdvancementPlanner.Equivalent(
                current,
                plan.Previous))
        {
            throw new GameContextAdvancementException(
                GameContextAdvancementReasonCodes.TransitionConflict,
                "The run coordinate changed before its advancement could "
                + "be committed.");
        }

        return CommitRunMutationAsync(
            run,
            RuntimeEventKinds.GameContextAdvanced,
            next => GameContextEnvelope.Attach(next, plan.Resulting),
            turnId,
            attemptId,
            cancellationToken,
            eventId: GameContextAdvancementJournalCodec.EventIdSuffix(
                turnId,
                plan),
            eventExtensions:
                GameContextAdvancementJournalCodec.EncodeExtensions(plan));
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
        run.UpdatedAt = MonotonicNow(run);
        runtimeEvent.Sequence = append.Sequence;
        Publish(runtimeEvent);
    }

    public ValueTask AppendDurableAsync(
        AgentRun run,
        string kind,
        JsonElement payload,
        string? turnId,
        string? attemptId,
        string? streamAttemptId = null,
        string? eventId = null,
        CancellationToken cancellationToken = default)
    {
        if (IsReservedRuntimeEventKind(kind))
        {
            throw new ArgumentException(
                "Built-in runtime event kinds require a typed journal API.",
                nameof(kind));
        }

        return AppendBuiltInDurableAsync(
            run,
            kind,
            payload,
            turnId,
            attemptId,
            streamAttemptId,
            eventId,
            cancellationToken);
    }

    internal async ValueTask AppendBuiltInDurableAsync(
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
        run.UpdatedAt = MonotonicNow(run);
        runtimeEvent.Sequence = append.Sequence;
        Publish(runtimeEvent);
    }

    public async ValueTask AppendTurnSnapshotAsync(
        AgentRun run,
        TurnSnapshot snapshot,
        string attemptId,
        CancellationToken cancellationToken)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        ProtocolValidator.EnsureValid(snapshot);
        if (!string.Equals(
                snapshot.RunId,
                run.RunId,
                StringComparison.Ordinal)
            || snapshot.RuntimeGeneration != run.RuntimeGeneration)
        {
            throw new ArgumentException(
                "The turn snapshot does not match the run identity.",
                nameof(snapshot));
        }

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
        run.UpdatedAt = MonotonicNow(run);
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
        ToolDisclosureJournalRecord? toolDisclosure = null,
        string? checkpointReasonCode = null)
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
            || !string.Equals(snapshot.TurnId, turnId, StringComparison.Ordinal)
            || snapshot.RuntimeGeneration != run.RuntimeGeneration)
        {
            throw new ArgumentException(
                "The turn snapshot does not match the prepared run and turn.",
                nameof(snapshot));
        }

        ProtocolValidator.EnsureValid(snapshot);

        var checkpointTimestamp = MonotonicNow(run);
        var turnCheckpoint = CloneRun(run);
        turnCheckpoint.CurrentTurnId = turnId;
        turnCheckpoint.Usage.Turns =
            checked(turnCheckpoint.Usage.Turns + 1);
        turnCheckpoint.Usage.DurationMs = Math.Max(
            turnCheckpoint.Usage.DurationMs,
            ElapsedMilliseconds(checkpointTimestamp, startedAt));
        turnCheckpoint.Revision = checked(run.Revision + 1);
        turnCheckpoint.UpdatedAt = checkpointTimestamp;
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
                eventId: "turn-started:" + turnId,
                reasonCode: checkpointReasonCode)
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
        finalRun.UpdatedAt = MonotonicNow(turnCheckpoint);
        EnsureRunInvariant(finalRun);
        var rawAppends = await _store.AppendAtomicBatchAsync(
                runtimeEvents,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        var appends = SnapshotBatchResults(
            rawAppends,
            runtimeEvents.Count,
            "turn-preparation");
        ValidateFreshBatchResults(
            appends,
            runtimeEvents.Count,
            run.Revision,
            "turn-preparation");
        for (var index = 0; index < appends.Length; index++)
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
        var rawAppends = await _store.AppendAtomicBatchAsync(
                runtimeEvents,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        var appends = SnapshotBatchResults(
            rawAppends,
            runtimeEvents.Length,
            "tool-disclosure-result");
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
        run.UpdatedAt = MonotonicNow(run);
        foreach (var runtimeEvent in runtimeEvents)
        {
            Publish(runtimeEvent);
        }
    }

    internal async ValueTask CommitSkillActivationResultAsync(
        AgentRun run,
        string turnId,
        string attemptId,
        string toolCallId,
        IReadOnlyList<SkillActivationStateRecord> skillActivations,
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
        _ = skillActivations
            ?? throw new ArgumentNullException(nameof(skillActivations));
        _ = toolDisclosure
            ?? throw new ArgumentNullException(nameof(toolDisclosure));
        if (resultMessage is null
            || !string.Equals(
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
                "A skill activation result must contain the matching tool result.",
                nameof(resultMessage));
        }

        var checkpoint = CloneRun(run);
        SkillActivationStateCodec.Attach(checkpoint, skillActivations);
        checkpoint.Revision = checked(run.Revision + 1);
        checkpoint.UpdatedAt = MonotonicNow(run);
        EnsureRunInvariant(checkpoint);

        var checkpointEvent = CreateEvent(
            run,
            RuntimeEventKinds.RunCheckpoint,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(checkpoint),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId: "skill-activation-checkpoint:" + toolCallId,
            reasonCode: SkillRuntimeReasonCodes.ActivatedByModel);
        var toolStateEvent = CreateEvent(
            run,
            RuntimeEventKinds.ToolDisclosureChanged,
            EventDurabilities.Durable,
            ToolDisclosureJournalCodec.Encode(toolDisclosure),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId: "skill-tool-disclosure:" + toolCallId,
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
        var runtimeEvents = new[]
        {
            checkpointEvent,
            toolStateEvent,
            transcriptEvent
        };
        var rawAppends = await _store.AppendAtomicBatchAsync(
                runtimeEvents,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        var appends = SnapshotBatchResults(
            rawAppends,
            runtimeEvents.Length,
            "skill-activation-result");
        ValidateFreshBatchResults(
            appends,
            runtimeEvents.Length,
            run.Revision,
            "skill-activation-result");
        for (var index = 0; index < runtimeEvents.Length; index++)
        {
            runtimeEvents[index].Sequence = appends[index].Sequence;
        }

        checkpoint.Revision = appends[^1].Revision;
        checkpoint.UpdatedAt = MonotonicNow(checkpoint);
        CopyRun(checkpoint, run);
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

        appends = SnapshotBatchResults(
            appends,
            runtimeEvents.Count,
            "action request");
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
            run.UpdatedAt = MonotonicNow(run);
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
        if (!string.Equals(run.RunId, request.RunId, StringComparison.Ordinal)
            || !string.Equals(
                run.AgentId,
                request.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                run.WorldId,
                request.WorldId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The action request belongs to a different run identity.",
                nameof(request));
        }
    }

    public async ValueTask<string> AppendActionUncertaintyAsync(
        AgentRun run,
        string turnId,
        string attemptId,
        ActionReceipt uncertainty,
        CancellationToken cancellationToken)
    {
        if (run is null)
        {
            throw new ArgumentNullException(nameof(run));
        }

        ProtocolValidator.EnsureValid(uncertainty);
        if (!string.Equals(
                uncertainty.Status,
                ReceiptStatuses.Unknown,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A dispatch-uncertainty event requires unknown status.",
                nameof(uncertainty));
        }

        var operation = await _operations.GetOperationAsync(
                uncertainty.OperationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The uncertain action has no operation-ledger entry.");
        ValidateActionUncertaintyOperation(
            run,
            turnId,
            uncertainty,
            operation);

        var runtimeEvent = CreateEvent(
            run,
            RuntimeEventKinds.ActionOutcomeUncertain,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(uncertainty),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId:
                "action-uncertainty:"
                + uncertainty.OperationId
                + ":"
                + uncertainty.Revision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        var append = await _store.AppendAtomicAsync(
                runtimeEvent,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        if (append.WasDuplicate)
        {
            ValidateIdempotentAppendResult(
                append,
                run.Revision,
                "action uncertainty");
        }
        else
        {
            ValidateFreshAppendResult(
                append,
                checked(run.Revision + 1),
                "action uncertainty");
            run.Revision = append.Revision;
            run.UpdatedAt = MonotonicNow(run);
        }

        operation = await _operations.GetOperationAsync(
                uncertainty.OperationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The uncertain action lost its operation-ledger entry.");
        ValidateActionUncertaintyOperation(
            run,
            turnId,
            uncertainty,
            operation);
        if (!run.PendingOperationIds.Contains(
                uncertainty.OperationId,
                StringComparer.Ordinal))
        {
            run.PendingOperationIds.Add(uncertainty.OperationId);
        }

        runtimeEvent.Sequence = append.Sequence;
        if (!append.WasDuplicate)
        {
            Publish(runtimeEvent);
        }

        return runtimeEvent.EventId;
    }

    public async ValueTask<string> AppendActionReceiptAsync(
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
            var rawAppends = await _store.AppendAtomicBatchAsync(
                    new[] { runtimeEvent, completionEvent },
                    run.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            var appends = SnapshotBatchResults(
                rawAppends,
                expectedCount: 2,
                "action receipt");
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
            run.UpdatedAt = MonotonicNow(run);
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

        return runtimeEvent.EventId;
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
        CancellationToken cancellationToken,
        ProviderRouteIdentity? routeIdentity = null,
        JsonElement? finalOutputAdmissionEvidence = null)
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

        var checkpointTimestamp = MonotonicNow(run);

        var transition = RunStateMachine.Plan(
            run,
            RunStates.Completed,
            checkpointTimestamp);
        var next = CloneRun(run);
        next.State = transition.ToState;
        next.TerminalReason = transition.TerminalReason;
        next.CompletionIntent = transition.CompletionIntent;
        next.CurrentTurnId = null;
        next.Usage.DurationMs = Math.Max(
            next.Usage.DurationMs,
            ElapsedMilliseconds(checkpointTimestamp, startedAt));
        next.Revision = checked(run.Revision + 4);
        next.UpdatedAt = checkpointTimestamp;
        EnsureRunInvariant(next);
        var resultNext = CloneRun(run);
        resultNext.Revision = checked(run.Revision + 2);
        resultNext.UpdatedAt = checkpointTimestamp;
        EnsureRunInvariant(resultNext);

        var transcriptEvent = CreateEvent(
            run,
            RuntimeEventKinds.TranscriptMessage,
            EventDurabilities.Durable,
            NormalizedMessageJournalCodec.Encode(assistantMessage),
            turnId,
            providerAttemptId,
            streamAttemptId: null,
            eventId: "transcript:" + assistantMessage.MessageId,
            providerId: providerId);
        var resultEvent = CreateEvent(
            run,
            RuntimeEventKinds.ProviderResultCommitted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(resultNext),
            turnId,
            providerAttemptId,
            streamAttemptId,
            eventId: "provider-result-committed:" + streamAttemptId,
            providerId: providerId,
            modelId: routeIdentity?.ModelId,
            transportDialect: routeIdentity?.TransportDialect,
            providerCapabilityDigest: routeIdentity?.CapabilityDigest,
            providerRouteDigest: routeIdentity?.RouteDigest);
        AttachProviderResultEvidence(
            resultEvent,
            providerId,
            routeIdentity,
            opaqueContinuationState: null);
        var assistantEvent = CreateEvent(
            run,
            RuntimeEventKinds.AssistantCompleted,
            EventDurabilities.Durable,
            finalOutput,
            turnId,
            providerAttemptId,
            streamAttemptId,
            eventId: "assistant-completed:" + turnId,
            providerId: providerId);
        if (finalOutputAdmissionEvidence.HasValue)
        {
            var presentation =
                FinalOutputAdmissionCodec.CreatePresentation(
                    FinalOutputAdmissionCodec
                        .AdmittedPresentationState,
                    ReadAdmissionReasonCode(
                        finalOutputAdmissionEvidence.Value),
                    finalOutputAdmissionEvidence.Value);
            resultEvent.Extensions[
                    FinalOutputAdmissionControl
                        .PresentationExtensionName] =
                presentation;
            assistantEvent.Extensions[
                    FinalOutputAdmissionCodec.EvidenceExtensionName] =
                finalOutputAdmissionEvidence.Value.Clone();
            assistantEvent.Extensions[
                    FinalOutputAdmissionControl
                        .PresentationExtensionName] =
                presentation;
            ProtocolValidator.EnsureValid(resultEvent);
            ProtocolValidator.EnsureValid(assistantEvent);
        }
        var completionEvent = CreateEvent(
            run,
            RuntimeEventKinds.RunCompleted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(next),
            turnId,
            providerAttemptId,
            streamAttemptId: null,
            eventId: "run-completed:" + run.RunId);
        var rawAppends = await _store.AppendAtomicBatchAsync(
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
        var appends = SnapshotBatchResults(
            rawAppends,
            expectedCount: 4,
            "final completion");
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

    public ValueTask CommitProviderResultAsync(
        AgentRun run,
        NormalizedMessage assistantMessage,
        string turnId,
        string providerId,
        string providerAttemptId,
        string streamAttemptId,
        CancellationToken cancellationToken,
        ProviderRouteIdentity? routeIdentity = null,
        ProviderOpaqueContinuationState? opaqueContinuationState = null,
        JsonElement? finalOutputPresentation = null)
    {
        return CommitProviderResultCoreAsync(
            run,
            assistantMessage,
            turnId,
            providerId,
            providerAttemptId,
            streamAttemptId,
            cancellationToken,
            routeIdentity,
            opaqueContinuationState,
            finalOutputPresentation,
            Array.Empty<NormalizedMessage>());
    }

    internal ValueTask CommitProviderResultWithFeedbackAsync(
        AgentRun run,
        NormalizedMessage assistantMessage,
        IReadOnlyList<NormalizedMessage> feedbackMessages,
        string turnId,
        string providerId,
        string providerAttemptId,
        string streamAttemptId,
        CancellationToken cancellationToken,
        ProviderRouteIdentity? routeIdentity = null,
        ProviderOpaqueContinuationState? opaqueContinuationState = null,
        JsonElement? finalOutputPresentation = null)
    {
        if (feedbackMessages is null)
        {
            throw new ArgumentNullException(nameof(feedbackMessages));
        }

        if (feedbackMessages.Count > ProviderRequestContentGuard.MaxTools)
        {
            throw new RuntimeContentLimitException(
                nameof(feedbackMessages),
                "provider_feedback_count_exceeded",
                "The provider feedback batch exceeds the runtime limit.");
        }

        var snapshot = new NormalizedMessage[feedbackMessages.Count];
        for (var index = 0; index < snapshot.Length; index++)
        {
            snapshot[index] = feedbackMessages[index]
                              ?? throw new ArgumentException(
                                  "A feedback message cannot be null.",
                                  nameof(feedbackMessages));
        }

        return CommitProviderResultCoreAsync(
            run,
            assistantMessage,
            turnId,
            providerId,
            providerAttemptId,
            streamAttemptId,
            cancellationToken,
            routeIdentity,
            opaqueContinuationState,
            finalOutputPresentation,
            snapshot);
    }

    private async ValueTask CommitProviderResultCoreAsync(
        AgentRun run,
        NormalizedMessage assistantMessage,
        string turnId,
        string providerId,
        string providerAttemptId,
        string streamAttemptId,
        CancellationToken cancellationToken,
        ProviderRouteIdentity? routeIdentity,
        ProviderOpaqueContinuationState? opaqueContinuationState,
        JsonElement? finalOutputPresentation,
        IReadOnlyList<NormalizedMessage> additionalMessages)
    {
        var resultNext = CloneRun(run);
        resultNext.Revision = checked(run.Revision + 2);
        resultNext.UpdatedAt = MonotonicNow(run);
        EnsureRunInvariant(resultNext);
        var next = CloneRun(run);
        next.Revision = checked(
            run.Revision + 2 + additionalMessages.Count);
        next.UpdatedAt = resultNext.UpdatedAt;
        EnsureRunInvariant(next);
        var transcriptEvent = CreateEvent(
            run,
            RuntimeEventKinds.TranscriptMessage,
            EventDurabilities.Durable,
            NormalizedMessageJournalCodec.Encode(assistantMessage),
            turnId,
            providerAttemptId,
            streamAttemptId: null,
            eventId: "transcript:" + assistantMessage.MessageId,
            providerId: providerId);
        var resultEvent = CreateEvent(
            run,
            RuntimeEventKinds.ProviderResultCommitted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(resultNext),
            turnId,
            providerAttemptId,
            streamAttemptId,
            eventId: "provider-result-committed:" + streamAttemptId,
            providerId: providerId,
            modelId: routeIdentity?.ModelId,
            transportDialect: routeIdentity?.TransportDialect,
            providerCapabilityDigest: routeIdentity?.CapabilityDigest,
            providerRouteDigest: routeIdentity?.RouteDigest);
        AttachProviderResultEvidence(
            resultEvent,
            providerId,
            routeIdentity,
            opaqueContinuationState);
        if (finalOutputPresentation.HasValue)
        {
            _ = FinalOutputAdmissionCodec.ValidatePresentation(
                finalOutputPresentation.Value,
                admissionEvidence: null);
            resultEvent.Extensions[
                    FinalOutputAdmissionControl
                        .PresentationExtensionName] =
                finalOutputPresentation.Value.Clone();
            ProtocolValidator.EnsureValid(resultEvent);
        }
        var additionalTranscriptEvents =
            new RuntimeEvent[additionalMessages.Count];
        for (var index = 0; index < additionalMessages.Count; index++)
        {
            var additionalMessage = additionalMessages[index]
                                    ?? throw new ArgumentException(
                                        "An additional transcript message "
                                        + "cannot be null.",
                                        nameof(
                                            additionalMessages));
            additionalTranscriptEvents[index] = CreateEvent(
                run,
                RuntimeEventKinds.TranscriptMessage,
                EventDurabilities.Durable,
                NormalizedMessageJournalCodec.Encode(
                    additionalMessage),
                turnId,
                providerAttemptId,
                streamAttemptId: null,
                eventId:
                    "transcript:"
                    + additionalMessage.MessageId);
        }
        var batch = new[]
            {
                transcriptEvent,
                resultEvent
            }
            .Concat(additionalTranscriptEvents)
            .ToArray();
        var rawAppends = await _store.AppendAtomicBatchAsync(
                batch,
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        var appends = SnapshotBatchResults(
            rawAppends,
            expectedCount: batch.Length,
            "provider result");
        ValidateFreshBatchResults(
            appends,
            expectedCount: batch.Length,
            run.Revision,
            "provider result");

        transcriptEvent.Sequence = appends[0].Sequence;
        resultEvent.Sequence = appends[1].Sequence;
        for (var index = 0;
             index < additionalTranscriptEvents.Length;
             index++)
        {
            additionalTranscriptEvents[index].Sequence =
                appends[index + 2].Sequence;
        }
        CopyRun(next, run);
        Publish(transcriptEvent);
        Publish(resultEvent);
        foreach (var additionalTranscriptEvent in
                 additionalTranscriptEvents)
        {
            Publish(additionalTranscriptEvent);
        }
    }

    public async ValueTask CommitProviderResultAndOutputAsync(
        AgentRun run,
        NormalizedMessage assistantMessage,
        JsonElement finalOutput,
        string turnId,
        string providerId,
        string providerAttemptId,
        string streamAttemptId,
        CancellationToken cancellationToken,
        ProviderRouteIdentity? routeIdentity = null,
        JsonElement? finalOutputAdmissionEvidence = null)
    {
        if (assistantMessage is null
            || !string.Equals(
                assistantMessage.Role,
                NormalizedRoles.Assistant,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A provider output requires an assistant transcript message.",
                nameof(assistantMessage));
        }

        var timestamp = MonotonicNow(run);
        var resultNext = CloneRun(run);
        resultNext.Revision = checked(run.Revision + 2);
        resultNext.UpdatedAt = timestamp;
        EnsureRunInvariant(resultNext);
        var cursorNext = CloneRun(resultNext);
        cursorNext.Revision = checked(run.Revision + 3);
        EnsureRunInvariant(cursorNext);
        var transcriptEvent = CreateEvent(
            run,
            RuntimeEventKinds.TranscriptMessage,
            EventDurabilities.Durable,
            NormalizedMessageJournalCodec.Encode(assistantMessage),
            turnId,
            providerAttemptId,
            streamAttemptId: null,
            eventId: "transcript:" + assistantMessage.MessageId,
            providerId: providerId);
        var resultEvent = CreateEvent(
            run,
            RuntimeEventKinds.ProviderResultCommitted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(resultNext),
            turnId,
            providerAttemptId,
            streamAttemptId,
            eventId: "provider-result-committed:" + streamAttemptId,
            providerId: providerId,
            modelId: routeIdentity?.ModelId,
            transportDialect: routeIdentity?.TransportDialect,
            providerCapabilityDigest: routeIdentity?.CapabilityDigest,
            providerRouteDigest: routeIdentity?.RouteDigest);
        AttachProviderResultEvidence(
            resultEvent,
            providerId,
            routeIdentity,
            opaqueContinuationState: null);
        var outputEvent = CreateEvent(
            run,
            RuntimeEventKinds.AssistantCompleted,
            EventDurabilities.Durable,
            finalOutput,
            turnId,
            providerAttemptId,
            streamAttemptId,
            eventId: "assistant-completed:" + turnId,
            providerId: providerId);
        if (finalOutputAdmissionEvidence.HasValue)
        {
            var presentation =
                FinalOutputAdmissionCodec.CreatePresentation(
                    FinalOutputAdmissionCodec
                        .AdmittedPresentationState,
                    ReadAdmissionReasonCode(
                        finalOutputAdmissionEvidence.Value),
                    finalOutputAdmissionEvidence.Value);
            resultEvent.Extensions[
                    FinalOutputAdmissionControl
                        .PresentationExtensionName] =
                presentation;
            outputEvent.Extensions[
                    FinalOutputAdmissionCodec.EvidenceExtensionName] =
                finalOutputAdmissionEvidence.Value.Clone();
            outputEvent.Extensions[
                    FinalOutputAdmissionControl
                        .PresentationExtensionName] =
                presentation;
            ProtocolValidator.EnsureValid(resultEvent);
            ProtocolValidator.EnsureValid(outputEvent);
        }
        var rawAppends = await _store.AppendAtomicBatchAsync(
                new[] { transcriptEvent, resultEvent, outputEvent },
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        var appends = SnapshotBatchResults(
            rawAppends,
            expectedCount: 3,
            "provider output");
        ValidateFreshBatchResults(
            appends,
            expectedCount: 3,
            run.Revision,
            "provider output");

        transcriptEvent.Sequence = appends[0].Sequence;
        resultEvent.Sequence = appends[1].Sequence;
        outputEvent.Sequence = appends[2].Sequence;
        CopyRun(cursorNext, run);
        Publish(transcriptEvent);
        Publish(resultEvent);
        Publish(outputEvent);
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
        var checkpointTimestamp = MonotonicNow(run);
        var transition = RunStateMachine.Plan(
            run,
            RunStates.Completed,
            checkpointTimestamp);
        var next = CloneRun(run);
        next.State = transition.ToState;
        next.TerminalReason = transition.TerminalReason;
        next.CompletionIntent = transition.CompletionIntent;
        next.CurrentTurnId = null;
        next.Usage.DurationMs = Math.Max(
            next.Usage.DurationMs,
            ElapsedMilliseconds(checkpointTimestamp, startedAt));
        next.Revision = checked(run.Revision + 2);
        next.UpdatedAt = checkpointTimestamp;
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
        assistantEvent.Extensions[RuntimeCompletionEvidence.ExtensionName] =
            RuntimeCompletionEvidence.Create(
                run,
                turnId,
                attemptId,
                streamAttemptId,
                finalOutput);
        var completionEvent = CreateEvent(
            run,
            RuntimeEventKinds.RunCompleted,
            EventDurabilities.Durable,
            ProtocolJson.ToElement(next),
            turnId,
            attemptId,
            streamAttemptId: null,
            eventId: "run-completed:" + run.RunId);
        var rawAppends = await _store.AppendAtomicBatchAsync(
                new[] { assistantEvent, completionEvent },
                run.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        var appends = SnapshotBatchResults(
            rawAppends,
            expectedCount: 2,
            "completion");
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
        var checkpointTimestamp = MonotonicNow(run);
        var transition = RunStateMachine.Plan(
            run,
            RunStates.Completed,
            checkpointTimestamp);
        var next = CloneRun(run);
        next.State = transition.ToState;
        next.TerminalReason = transition.TerminalReason;
        next.CompletionIntent = transition.CompletionIntent;
        next.CurrentTurnId = null;
        next.Usage.DurationMs = Math.Max(
            next.Usage.DurationMs,
            ElapsedMilliseconds(checkpointTimestamp, startedAt));
        next.Revision = checked(run.Revision + 1);
        next.UpdatedAt = checkpointTimestamp;
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
            || expectedRevision <= 0
            || append.Revision != expectedRevision
            || append.Sequence != expectedRevision - 1)
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
            || baseRevision < 0
            || first.Sequence != baseRevision
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

        var duplicateCount = 0;
        for (var index = 0; index < expectedCount; index++)
        {
            var append = appends[index];
            if (append is not null && append.WasDuplicate)
            {
                duplicateCount++;
            }
        }
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

    private static JournalAppendResult[] SnapshotBatchResults(
        IReadOnlyList<JournalAppendResult>? appends,
        int expectedCount,
        string operation)
    {
        if (appends is null || expectedCount <= 0)
        {
            throw new InvalidDataException(
                $"The journal returned an invalid {operation} batch result.");
        }

        int count;
        try
        {
            count = appends.Count;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                $"The journal returned an unreadable {operation} batch "
                + "result.",
                exception);
        }

        if (count != expectedCount)
        {
            throw new InvalidDataException(
                $"The journal returned an invalid {operation} batch result.");
        }

        var snapshots = new JournalAppendResult[expectedCount];
        for (var index = 0; index < expectedCount; index++)
        {
            JournalAppendResult? append;
            try
            {
                append = appends[index];
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                throw new InvalidDataException(
                    $"The journal returned an unreadable {operation} batch "
                    + "result.",
                    exception);
            }

            if (append is null)
            {
                throw new InvalidDataException(
                    $"The journal returned an invalid {operation} batch "
                    + "result.");
            }

            snapshots[index] = new JournalAppendResult(
                append.Sequence,
                append.Revision,
                append.WasDuplicate);
        }

        return snapshots;
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
            || append.Sequence != append.Revision - 1
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

    private static void ValidateActionUncertaintyOperation(
        AgentRun run,
        string turnId,
        ActionReceipt uncertainty,
        OperationLedgerEntry operation)
    {
        if (!operation.IsPending
            || operation.LatestReceipt is not null
            || !string.Equals(
                operation.OperationId,
                uncertainty.OperationId,
                StringComparison.Ordinal)
            || !string.Equals(
                operation.RunId,
                run.RunId,
                StringComparison.Ordinal)
            || !string.Equals(
                operation.Request.TurnId,
                turnId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The operation ledger returned an invalid uncertain action.");
        }

        _ = ActionReceiptIngressValidator.ValidateAndClone(
            operation.Request,
            uncertainty,
            run);
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

    private static void ValidateTransitionEventKind(
        string targetState,
        string eventKind)
    {
        var valid = eventKind switch
        {
            RuntimeEventKinds.RunStarted =>
                string.Equals(
                    targetState,
                    RunStates.Preparing,
                    StringComparison.Ordinal)
                || string.Equals(
                    targetState,
                    RunStates.Running,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunCheckpoint =>
                !RunStateMachine.IsTerminal(targetState),
            RuntimeEventKinds.TurnCompleted =>
                string.Equals(
                    targetState,
                    RunStates.Running,
                    StringComparison.Ordinal),
            RuntimeEventKinds.ActionReconciling =>
                string.Equals(
                    targetState,
                    RunStates.Reconciling,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunCompleted =>
                string.Equals(
                    targetState,
                    RunStates.Completed,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunInterrupted =>
                string.Equals(
                    targetState,
                    RunStates.Interrupted,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunFailed =>
                string.Equals(
                    targetState,
                    RunStates.Failed,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunCancelled =>
                string.Equals(
                    targetState,
                    RunStates.Cancelled,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunBudgetExhausted =>
                string.Equals(
                    targetState,
                    RunStates.BudgetExhausted,
                    StringComparison.Ordinal),
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The transition state and runtime event kind do not match.",
                nameof(eventKind));
        }
    }

    private static bool IsReservedRuntimeEventKind(string kind)
    {
        return kind is RuntimeEventKinds.RunStarted
            or RuntimeEventKinds.RunCompleted
            or RuntimeEventKinds.RunInterrupted
            or RuntimeEventKinds.RunFailed
            or RuntimeEventKinds.RunCancelled
            or RuntimeEventKinds.RunBudgetExhausted
            or RuntimeEventKinds.RunCheckpoint
            or RuntimeEventKinds.RunInputCaptured
            or RuntimeEventKinds.TurnStarted
            or RuntimeEventKinds.TurnCompleted
            or RuntimeEventKinds.TurnSnapshot
            or RuntimeEventKinds.TranscriptMessage
            or RuntimeEventKinds.AssistantDelta
            or RuntimeEventKinds.AssistantCompleted
            or RuntimeEventKinds.ToolStarted
            or RuntimeEventKinds.ToolCompleted
            or RuntimeEventKinds.ToolFailed
            or RuntimeEventKinds.ToolDisclosureChanged
            or RuntimeEventKinds.ActionRequested
            or RuntimeEventKinds.ActionReceived
            or RuntimeEventKinds.ActionOutcomeUncertain
            or RuntimeEventKinds.ActionReconciling
            or RuntimeEventKinds.GameContextAdvanced
            or RuntimeEventKinds.ProviderRetry
            or RuntimeEventKinds.ProviderFallback
            or RuntimeEventKinds.ProviderDispatchStarted
            or RuntimeEventKinds.ProviderDispatchKnownZero
            or RuntimeEventKinds.ProviderUsageUncertain
            or RuntimeEventKinds.ProviderResultCommitted
            or RuntimeEventKinds.ProviderResultDiscarded
            or RuntimeEventKinds.MemoryCommitPrepared
            or RuntimeEventKinds.MemoryCommitCompleted
            or RuntimeEventKinds.MemoryCommitSettled
            or RuntimeEventKinds.ControlReceived
            or RuntimeEventKinds.BudgetUpdated;
    }

    private static void AttachProviderResultEvidence(
        RuntimeEvent runtimeEvent,
        string providerId,
        ProviderRouteIdentity? routeIdentity,
        ProviderOpaqueContinuationState? opaqueContinuationState)
    {
        if (routeIdentity is null)
        {
            if (opaqueContinuationState is not null)
            {
                throw new ArgumentException(
                    "Durable provider continuation state requires a route identity.",
                    nameof(opaqueContinuationState));
            }

            return;
        }

        if (!string.Equals(
                providerId,
                routeIdentity.ProviderId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The provider result route does not match the provider id.",
                nameof(routeIdentity));
        }

        runtimeEvent.Extensions[
                ProviderRouteJournalExtensions.PolicyVersion] =
            JsonArrayBuilder.String(routeIdentity.RoutePolicyVersion);
        runtimeEvent.Extensions[
                ProviderRouteJournalExtensions.PolicyDigest] =
            JsonArrayBuilder.String(routeIdentity.RoutePolicyDigest);
        runtimeEvent.Extensions[
                ProviderWireRequestEvidence
                    .DialectSemanticDigestJournalExtensionName] =
            JsonArrayBuilder.String(routeIdentity.DialectSemanticDigest);

        if (opaqueContinuationState is not null)
        {
            if (!opaqueContinuationState.Matches(routeIdentity)
                || !opaqueContinuationState.TryCreateDurableEnvelope(
                    out var envelope))
            {
                throw new ArgumentException(
                    "Only matching provider-declared non-secret state can be journaled.",
                    nameof(opaqueContinuationState));
            }

            runtimeEvent.Extensions[
                    ProviderOpaqueContinuationState.JournalExtensionName] =
                envelope;
        }

        ProtocolValidator.EnsureValid(runtimeEvent);
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
        var runtimeEvent = new RuntimeEvent
        {
            EventId = RuntimeEventIdDerivation.Derive(
                run.RunId,
                eventId ?? _ids.NewId("event")),
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
            Timestamp = MonotonicNow(run),
            Payload = payload.Clone()
        };
        ProtocolValidator.EnsureValid(runtimeEvent);
        return runtimeEvent;
    }

    private void Publish(RuntimeEvent runtimeEvent)
    {
        ProtocolValidator.EnsureValid(runtimeEvent);
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

    private DateTimeOffset MonotonicNow(AgentRun run)
    {
        var timestamp = _clock.UtcNow;
        return timestamp < run.UpdatedAt
            ? run.UpdatedAt
            : timestamp;
    }

    private static long ElapsedMilliseconds(
        DateTimeOffset timestamp,
        DateTimeOffset startedAt)
    {
        return Math.Max(
            0,
            (long)(timestamp - startedAt).TotalMilliseconds);
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

    private static string ReadAdmissionReasonCode(JsonElement evidence)
    {
        if (evidence.ValueKind != JsonValueKind.Object
            || !evidence.TryGetProperty(
                "decisionReasonCode",
                out var reason)
            || reason.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                "Final-output admission evidence has no decision reason.",
                nameof(evidence));
        }

        return RuntimeGuard.RequiredReasonCode(
            reason.GetString(),
            nameof(evidence));
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

using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

internal static class RunCheckpointLifecycleValidator
{
    public static bool IsCheckpointKind(string kind)
    {
        return kind is RuntimeEventKinds.RunStarted
            or RuntimeEventKinds.RunCompleted
            or RuntimeEventKinds.RunInterrupted
            or RuntimeEventKinds.RunFailed
            or RuntimeEventKinds.RunCancelled
            or RuntimeEventKinds.RunBudgetExhausted
            or RuntimeEventKinds.RunCheckpoint
            or RuntimeEventKinds.TurnStarted
            or RuntimeEventKinds.TurnCompleted
            or RuntimeEventKinds.BudgetUpdated
            or RuntimeEventKinds.ProviderDispatchStarted
            or RuntimeEventKinds.ProviderDispatchKnownZero
            or RuntimeEventKinds.ProviderUsageUncertain
            or RuntimeEventKinds.ProviderResultCommitted
            or RuntimeEventKinds.ProviderResultDiscarded
            or RuntimeEventKinds.ActionReconciling
            or RuntimeEventKinds.GameContextAdvanced;
    }

    public static AgentRun ValidateAndClone(
        RuntimeEvent runtimeEvent,
        AgentRun? previousCheckpoint,
        long projectedSequence,
        long projectedRevision,
        bool allowLegacyReconcilingDurationCheckpoint = false)
    {
        if (runtimeEvent is null)
        {
            throw new ArgumentNullException(nameof(runtimeEvent));
        }

        if (!IsCheckpointKind(runtimeEvent.Kind))
        {
            throw new ArgumentException(
                "The runtime event is not a run checkpoint.",
                nameof(runtimeEvent));
        }

        if (projectedSequence < 0
            || projectedSequence == long.MaxValue
            || projectedRevision != projectedSequence + 1)
        {
            throw new InvalidDataException(
                "A run checkpoint has an invalid projected journal position.");
        }

        AgentRun checkpoint;
        try
        {
            checkpoint = ProtocolJson.DeserializeAgentRun(
                runtimeEvent.Payload.GetRawText());
            ProtocolValidator.EnsureValid(checkpoint);
        }
        catch (Exception exception) when (
            exception is JsonException
            or InvalidOperationException)
        {
            throw new InvalidDataException(
                "A run checkpoint has an invalid aggregate payload.",
                exception);
        }

        if (!string.Equals(
                checkpoint.RunId,
                runtimeEvent.RunId,
                StringComparison.Ordinal)
            || checkpoint.RuntimeGeneration
            != runtimeEvent.RuntimeGeneration)
        {
            throw new InvalidDataException(
                "A run checkpoint does not match its journal event.");
        }

        if (checkpoint.Revision != projectedRevision)
        {
            throw new InvalidDataException(
                "A run checkpoint revision does not match its journal "
                + "position.");
        }

        ValidateKindState(runtimeEvent.Kind, checkpoint.State);
        if (previousCheckpoint is null)
        {
            if (!string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.RunStarted,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The first run checkpoint must be run.started.");
            }

            ValidateCompletionIntentForState(checkpoint);
            ValidateTerminalReasonForState(checkpoint);
            return checkpoint;
        }

        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.RunStarted,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "run.started can only be the first run checkpoint.");
        }

        ValidateStableIdentity(
            runtimeEvent,
            previousCheckpoint,
            checkpoint,
            allowLegacyReconcilingDurationCheckpoint);
        ValidateUsageProgression(
            runtimeEvent,
            previousCheckpoint,
            checkpoint);
        ValidateStateProgression(
            previousCheckpoint,
            checkpoint,
            runtimeEvent.Kind,
            runtimeEvent.ReasonCode,
            allowLegacyReconcilingDurationCheckpoint);
        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.GameContextAdvanced,
                StringComparison.Ordinal))
        {
            GameContextAdvancementJournalCodec.ValidateCheckpoint(
                runtimeEvent,
                previousCheckpoint,
                checkpoint);
        }

        return checkpoint;
    }

    private static void ValidateKindState(
        string eventKind,
        string state)
    {
        var valid = eventKind switch
        {
            RuntimeEventKinds.RunStarted =>
                string.Equals(
                    state,
                    RunStates.Preparing,
                    StringComparison.Ordinal)
                || string.Equals(
                    state,
                    RunStates.Running,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunCheckpoint =>
                !RunStateMachine.IsTerminal(state),
            RuntimeEventKinds.TurnStarted
                or RuntimeEventKinds.TurnCompleted =>
                string.Equals(
                    state,
                    RunStates.Running,
                    StringComparison.Ordinal),
            RuntimeEventKinds.ActionReconciling =>
                string.Equals(
                    state,
                    RunStates.Reconciling,
                    StringComparison.Ordinal),
            RuntimeEventKinds.GameContextAdvanced =>
                state is RunStates.WaitingForAction
                    or RunStates.Reconciling,
            RuntimeEventKinds.RunCompleted =>
                string.Equals(
                    state,
                    RunStates.Completed,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunInterrupted =>
                string.Equals(
                    state,
                    RunStates.Interrupted,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunFailed =>
                string.Equals(
                    state,
                    RunStates.Failed,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunCancelled =>
                string.Equals(
                    state,
                    RunStates.Cancelled,
                    StringComparison.Ordinal),
            RuntimeEventKinds.RunBudgetExhausted =>
                string.Equals(
                    state,
                    RunStates.BudgetExhausted,
                    StringComparison.Ordinal),
            _ => true
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"Checkpoint event '{eventKind}' is incompatible with "
                + $"run state '{state}'.");
        }
    }

    private static void ValidateStableIdentity(
        RuntimeEvent runtimeEvent,
        AgentRun previous,
        AgentRun candidate,
        bool allowLegacyReconcilingDurationCheckpoint)
    {
        if (!string.Equals(
                candidate.RunId,
                previous.RunId,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.AgentId,
                previous.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.WorldId,
                previous.WorldId,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.SessionId,
                previous.SessionId,
                StringComparison.Ordinal)
            || candidate.RuntimeGeneration
            != previous.RuntimeGeneration)
        {
            throw new InvalidDataException(
                "A run checkpoint changes the durable run identity.");
        }

        if (!string.Equals(
                candidate.ProtocolVersion,
                previous.ProtocolVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.SchemaVersion,
                previous.SchemaVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                ProtocolJson.Serialize(candidate.Trigger),
                ProtocolJson.Serialize(previous.Trigger),
                StringComparison.Ordinal)
            || !candidate.TriggerObservationIds.SequenceEqual(
                previous.TriggerObservationIds,
                StringComparer.Ordinal)
            || !string.Equals(
                candidate.DecisionKey,
                previous.DecisionKey,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.BatchId,
                previous.BatchId,
                StringComparison.Ordinal)
            || !string.Equals(
                ProtocolJson.Serialize(candidate.Budget),
                ProtocolJson.Serialize(previous.Budget),
                StringComparison.Ordinal)
            || candidate.CreatedAt != previous.CreatedAt)
        {
            throw new InvalidDataException(
                "A run checkpoint changes immutable run metadata.");
        }

        if (candidate.UpdatedAt < previous.UpdatedAt)
        {
            throw new InvalidDataException(
                "A run checkpoint moves its aggregate timestamp backwards.");
        }

        ValidateAggregateMetadataProgression(
            runtimeEvent,
            previous,
            candidate,
            allowLegacyReconcilingDurationCheckpoint);
        ValidateExtensionProgression(
            runtimeEvent,
            previous.Extensions,
            candidate.Extensions);
    }

    private static void ValidateAggregateMetadataProgression(
        RuntimeEvent runtimeEvent,
        AgentRun previous,
        AgentRun candidate,
        bool allowLegacyReconcilingDurationCheckpoint)
    {
        var stateChanged = !string.Equals(
            previous.State,
            candidate.State,
            StringComparison.Ordinal);
        var deadlineReached =
            !stateChanged
            && string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.BudgetUpdated,
                StringComparison.Ordinal)
            && string.Equals(
                runtimeEvent.ReasonCode,
                "max_duration",
                StringComparison.Ordinal)
            && string.Equals(
                previous.State,
                RunStates.Reconciling,
                StringComparison.Ordinal)
            && candidate.PendingOperationIds.Count > 0
            && candidate.CompletionIntent is null
            && string.Equals(
                candidate.TerminalReason,
                "max_duration",
                StringComparison.Ordinal)
            && (previous.TerminalReason is null
                || string.Equals(
                    previous.TerminalReason,
                    "max_duration",
                    StringComparison.Ordinal));
        var legacyDeadlineReached =
            IsLegacyReconcilingDurationCheckpoint(
                runtimeEvent.Kind,
                runtimeEvent.ReasonCode,
                previous,
                candidate,
                allowLegacyReconcilingDurationCheckpoint);
        if (!stateChanged
            && !deadlineReached
            && !legacyDeadlineReached
            && (!string.Equals(
                    previous.CompletionIntent,
                    candidate.CompletionIntent,
                    StringComparison.Ordinal)
                || !string.Equals(
                    previous.TerminalReason,
                    candidate.TerminalReason,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A same-state checkpoint changes completion metadata.");
        }

        var currentTurnChanged = !string.Equals(
            previous.CurrentTurnId,
            candidate.CurrentTurnId,
            StringComparison.Ordinal);
        if (currentTurnChanged
            && runtimeEvent.Kind is not RuntimeEventKinds.TurnStarted
                and not RuntimeEventKinds.TurnCompleted
                and not RuntimeEventKinds.RunCompleted
                and not RuntimeEventKinds.RunInterrupted
                and not RuntimeEventKinds.RunFailed
                and not RuntimeEventKinds.RunCancelled
                and not RuntimeEventKinds.RunBudgetExhausted
                )
        {
            throw new InvalidDataException(
                "A checkpoint kind cannot change the current turn.");
        }

        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.TurnStarted,
                StringComparison.Ordinal)
            && (!string.IsNullOrEmpty(previous.CurrentTurnId)
                || string.IsNullOrEmpty(runtimeEvent.TurnId)
                || !string.Equals(
                    candidate.CurrentTurnId,
                    runtimeEvent.TurnId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "turn.started must establish its event turn as the current turn.");
        }

        if (string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.TurnCompleted,
                StringComparison.Ordinal)
            && (!string.Equals(
                    previous.CurrentTurnId,
                    runtimeEvent.TurnId,
                    StringComparison.Ordinal)
                || candidate.CurrentTurnId is not null))
        {
            throw new InvalidDataException(
                "turn.completed must clear its matching current turn.");
        }

        ValidateCompletionIntentForState(candidate);
        ValidateTerminalReasonForState(candidate);
    }

    private static void ValidateCompletionIntentForState(AgentRun candidate)
    {
        var intentValid = candidate.State switch
        {
            RunStates.Queued
                or RunStates.Preparing
                or RunStates.Running
                or RunStates.WaitingForAction
                or RunStates.Completed
                or RunStates.BudgetExhausted =>
                candidate.CompletionIntent is null,
            RunStates.Cancelling or RunStates.Cancelled =>
                string.Equals(
                    candidate.CompletionIntent,
                    CompletionIntents.Cancelled,
                    StringComparison.Ordinal),
            RunStates.Interrupting or RunStates.Interrupted =>
                string.Equals(
                    candidate.CompletionIntent,
                    CompletionIntents.Interrupted,
                    StringComparison.Ordinal),
            RunStates.Reconciling =>
                candidate.CompletionIntent is null
                || candidate.CompletionIntent
                is CompletionIntents.Cancelled
                    or CompletionIntents.Interrupted
                    or CompletionIntents.Failed,
            RunStates.Failed =>
                string.Equals(
                    candidate.CompletionIntent,
                    CompletionIntents.Failed,
                    StringComparison.Ordinal),
            _ => false
        };
        if (!intentValid)
        {
            throw new InvalidDataException(
                "A run checkpoint has completion metadata incompatible "
                + "with its state.");
        }
    }

    private static void ValidateTerminalReasonForState(AgentRun candidate)
    {
        var valid = candidate.State switch
        {
            RunStates.Completed
                or RunStates.Queued
                or RunStates.Preparing
                or RunStates.Running
                or RunStates.WaitingForAction
                or RunStates.Cancelling
                or RunStates.Interrupting =>
                candidate.TerminalReason is null,
            RunStates.Reconciling =>
                candidate.TerminalReason is null
                || string.Equals(
                    candidate.TerminalReason,
                    "max_duration",
                    StringComparison.Ordinal),
            RunStates.BudgetExhausted
                or RunStates.Interrupted
                or RunStates.Cancelled
                or RunStates.Failed =>
                !string.IsNullOrWhiteSpace(candidate.TerminalReason),
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException(
                "A run checkpoint has a terminal reason incompatible "
                + "with its state.");
        }
    }

    private static void ValidateExtensionProgression(
        RuntimeEvent runtimeEvent,
        IReadOnlyDictionary<string, JsonElement> previous,
        IReadOnlyDictionary<string, JsonElement> candidate)
    {
        var keys = previous.Keys
            .Concat(candidate.Keys)
            .Distinct(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var mutableForEvent =
                string.Equals(
                    key,
                    "turnOutcome",
                    StringComparison.Ordinal)
                && string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.TurnCompleted,
                    StringComparison.Ordinal)
                || string.Equals(
                    key,
                    "toolLoopGuard",
                    StringComparison.Ordinal)
                && string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.RunFailed,
                    StringComparison.Ordinal)
                || string.Equals(
                    key,
                    GameContextEnvelope.ExtensionName,
                    StringComparison.Ordinal)
                && string.Equals(
                    runtimeEvent.Kind,
                    RuntimeEventKinds.GameContextAdvanced,
                    StringComparison.Ordinal)
                || string.Equals(
                    key,
                    SkillActivationStateCodec.ExtensionName,
                    StringComparison.Ordinal)
                && IsAuthorizedSkillStateProgression(
                    runtimeEvent,
                    previous,
                    candidate);
            if (mutableForEvent)
            {
                continue;
            }

            if (!previous.TryGetValue(key, out var previousValue)
                || !candidate.TryGetValue(key, out var candidateValue)
                || !string.Equals(
                    ProtocolJson.Serialize(previousValue),
                    ProtocolJson.Serialize(candidateValue),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A run checkpoint changes immutable extension metadata.");
            }
        }
    }

    private static bool IsAuthorizedSkillStateProgression(
        RuntimeEvent runtimeEvent,
        IReadOnlyDictionary<string, JsonElement> previous,
        IReadOnlyDictionary<string, JsonElement> candidate)
    {
        var modelActivation =
            string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.RunCheckpoint,
                StringComparison.Ordinal)
            && string.Equals(
                runtimeEvent.ReasonCode,
                SkillRuntimeReasonCodes.ActivatedByModel,
                StringComparison.Ordinal);
        var continuationReplacement =
            string.Equals(
                runtimeEvent.Kind,
                RuntimeEventKinds.TurnStarted,
                StringComparison.Ordinal)
            && string.Equals(
                runtimeEvent.ReasonCode,
                SkillRuntimeReasonCodes.ReplacedByContinuation,
                StringComparison.Ordinal);
        if ((!modelActivation && !continuationReplacement)
            || !candidate.TryGetValue(
                SkillActivationStateCodec.ExtensionName,
                out var candidateValue))
        {
            return false;
        }

        var hasPreviousState = previous.TryGetValue(
            SkillActivationStateCodec.ExtensionName,
            out var previousValue);
        var previousState = hasPreviousState
            ? SkillActivationStateCodec.Decode(previousValue, 4_096)
            : Array.Empty<SkillActivationStateRecord>();
        var candidateState = SkillActivationStateCodec.Decode(
            candidateValue,
            4_096);
        if (continuationReplacement)
        {
            return hasPreviousState
                ? !string.Equals(
                    ProtocolJson.Serialize(previousValue),
                    ProtocolJson.Serialize(candidateValue),
                    StringComparison.Ordinal)
                : candidateState.Count > 0;
        }

        if (candidateState.Count != previousState.Count + 1)
        {
            return false;
        }

        var candidateByReference = candidateState.ToDictionary(
            value => value.Reference,
            StringComparer.Ordinal);
        return previousState.All(
            value => candidateByReference.TryGetValue(
                         value.Reference,
                         out var current)
                     && string.Equals(
                         value.ContentDigest,
                         current.ContentDigest,
                         StringComparison.Ordinal));
    }

    private static void ValidateStateProgression(
        AgentRun previous,
        AgentRun candidate,
        string eventKind,
        string? reasonCode,
        bool allowLegacyReconcilingDurationCheckpoint)
    {
        var previousState = previous.State;
        var candidateState = candidate.State;
        ValidateEventStateProgression(
            previous,
            candidate,
            eventKind,
            reasonCode,
            allowLegacyReconcilingDurationCheckpoint);
        if (RequiresSameState(eventKind)
            && !string.Equals(
                candidateState,
                previousState,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Checkpoint event '{eventKind}' cannot change run state.");
        }

        if (RunStateMachine.IsTerminal(previousState))
        {
            if (!string.Equals(
                    candidateState,
                    previousState,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A terminal run checkpoint cannot change state.");
            }

            if (!IsAllowedAfterTerminal(eventKind))
            {
                throw new InvalidDataException(
                    $"Checkpoint event '{eventKind}' is not allowed after "
                    + "a terminal run checkpoint.");
            }

            ValidateTerminalMetadata(previous, candidate);
            return;
        }

        if (!string.Equals(
                candidateState,
                previousState,
                StringComparison.Ordinal)
            && !RunStateMachine.IsStateTransitionAllowed(
                previousState,
                candidateState))
        {
            throw new InvalidDataException(
                $"Run checkpoint state transition '{previousState}' -> "
                + $"'{candidateState}' is not allowed.");
        }

        if (RunStateMachine.IsTerminal(candidateState)
            && !TerminalKindMatches(eventKind, candidateState))
        {
            throw new InvalidDataException(
                "A terminal run state requires its matching terminal event.");
        }
    }

    private static void ValidateEventStateProgression(
        AgentRun previous,
        AgentRun candidate,
        string eventKind,
        string? reasonCode,
        bool allowLegacyReconcilingDurationCheckpoint)
    {
        if (string.Equals(
                eventKind,
                RuntimeEventKinds.RunCheckpoint,
                StringComparison.Ordinal))
        {
            ValidateRunCheckpointProgression(
                previous,
                candidate,
                reasonCode,
                allowLegacyReconcilingDurationCheckpoint);
            return;
        }

        if (string.Equals(
                eventKind,
                RuntimeEventKinds.ActionReconciling,
                StringComparison.Ordinal))
        {
            ValidateActionReconciliationProgression(previous, candidate);
            return;
        }

        if (string.Equals(
                eventKind,
                RuntimeEventKinds.TurnStarted,
                StringComparison.Ordinal)
            && (!string.Equals(
                    previous.State,
                    RunStates.Running,
                    StringComparison.Ordinal)
                || !string.Equals(
                    candidate.State,
                    RunStates.Running,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "turn.started requires a running-to-running checkpoint.");
        }

        if (string.Equals(
                eventKind,
                RuntimeEventKinds.TurnCompleted,
                StringComparison.Ordinal))
        {
            var sourceAllowed = previous.State is RunStates.Running
                or RunStates.WaitingForAction
                or RunStates.Reconciling;
            if (!sourceAllowed
                || !string.Equals(
                    candidate.State,
                    RunStates.Running,
                    StringComparison.Ordinal)
                || candidate.CompletionIntent is not null
                || candidate.TerminalReason is not null)
            {
                throw new InvalidDataException(
                    "turn.completed has an invalid run progression.");
            }
        }
    }

    private static void ValidateRunCheckpointProgression(
        AgentRun previous,
        AgentRun candidate,
        string? reasonCode,
        bool allowLegacyReconcilingDurationCheckpoint)
    {
        var from = previous.State;
        var to = candidate.State;
        var neutral =
            candidate.CompletionIntent is null
            && candidate.TerminalReason is null;
        var valid =
            from == RunStates.Preparing
            && to == RunStates.Running
            && neutral
            || from == RunStates.Running
            && to == RunStates.WaitingForAction
            && neutral
            || from == RunStates.WaitingForAction
            && to == RunStates.Running
            && neutral
            || from == RunStates.Running
            && to == RunStates.Running
            && neutral
            && string.Equals(
                reasonCode,
                SkillRuntimeReasonCodes.ActivatedByModel,
                StringComparison.Ordinal)
            || (from == RunStates.Running
                || from == RunStates.WaitingForAction
                || from == RunStates.Reconciling)
            && to == RunStates.Cancelling
            && string.Equals(
                candidate.CompletionIntent,
                CompletionIntents.Cancelled,
                StringComparison.Ordinal)
            && candidate.TerminalReason is null
            || (from == RunStates.Running
                || from == RunStates.WaitingForAction)
            && to == RunStates.Interrupting
            && string.Equals(
                candidate.CompletionIntent,
                CompletionIntents.Interrupted,
                StringComparison.Ordinal)
            && candidate.TerminalReason is null;
        valid = valid
                || IsLegacyReconcilingDurationCheckpoint(
                    RuntimeEventKinds.RunCheckpoint,
                    reasonCode,
                    previous,
                    candidate,
                    allowLegacyReconcilingDurationCheckpoint);
        if (!valid)
        {
            throw new InvalidDataException(
                $"run.checkpoint cannot represent '{from}' -> '{to}'.");
        }
    }

    private static bool IsLegacyReconcilingDurationCheckpoint(
        string eventKind,
        string? reasonCode,
        AgentRun previous,
        AgentRun candidate,
        bool allowed)
    {
        return allowed
               && string.Equals(
                   eventKind,
                   RuntimeEventKinds.RunCheckpoint,
                   StringComparison.Ordinal)
               && string.IsNullOrEmpty(reasonCode)
               && string.Equals(
                   previous.State,
                   RunStates.Reconciling,
                   StringComparison.Ordinal)
               && string.Equals(
                   candidate.State,
                   RunStates.Reconciling,
                   StringComparison.Ordinal)
               && previous.PendingOperationIds.Count > 0
               && candidate.PendingOperationIds.SequenceEqual(
                   previous.PendingOperationIds,
                   StringComparer.Ordinal)
               && string.Equals(
                   candidate.CurrentTurnId,
                   previous.CurrentTurnId,
                   StringComparison.Ordinal)
               && candidate.CompletionIntent is null
               && string.Equals(
                   candidate.TerminalReason,
                   "max_duration",
                   StringComparison.Ordinal)
               && (previous.TerminalReason is null
                   || string.Equals(
                       previous.TerminalReason,
                       "max_duration",
                       StringComparison.Ordinal));
    }

    private static void ValidateActionReconciliationProgression(
        AgentRun previous,
        AgentRun candidate)
    {
        var from = previous.State;
        var sourceAllowed = from is RunStates.WaitingForAction
            or RunStates.Cancelling
            or RunStates.Interrupting;
        var intentAllowed = from switch
        {
            RunStates.Cancelling =>
                string.Equals(
                    candidate.CompletionIntent,
                    CompletionIntents.Cancelled,
                    StringComparison.Ordinal),
            RunStates.Interrupting =>
                string.Equals(
                    candidate.CompletionIntent,
                    CompletionIntents.Interrupted,
                    StringComparison.Ordinal),
            _ => candidate.CompletionIntent is null
                 || candidate.CompletionIntent
                 is CompletionIntents.Cancelled
                     or CompletionIntents.Interrupted
                     or CompletionIntents.Failed
        };
        if (!sourceAllowed
            || !string.Equals(
                candidate.State,
                RunStates.Reconciling,
                StringComparison.Ordinal)
            || candidate.PendingOperationIds.Count == 0
            || candidate.TerminalReason is not null
            || !intentAllowed)
        {
            throw new InvalidDataException(
                "action.reconciling has an invalid run progression.");
        }
    }

    private static void ValidateUsageProgression(
        RuntimeEvent runtimeEvent,
        AgentRun previous,
        AgentRun candidate)
    {
        var previousUsage = previous.Usage;
        var candidateUsage = candidate.Usage;
        var turnsRegressed =
            candidateUsage.Turns < previousUsage.Turns;
        if (turnsRegressed
            && !IsReplaySafeTurnAbandonment(
                runtimeEvent,
                previous,
                candidate))
        {
            throw new InvalidDataException(
                "A run checkpoint decreases accounted turns.");
        }

        if (candidateUsage.DurationMs < previousUsage.DurationMs
            || candidateUsage.InputTokens < previousUsage.InputTokens
            || candidateUsage.OutputTokens < previousUsage.OutputTokens
            || candidateUsage.ProviderUsageSamples
            < previousUsage.ProviderUsageSamples
            || !NullableUsageCounterProgresses(
                previousUsage,
                previousUsage.CacheReadTokens,
                candidateUsage.CacheReadTokens)
            || !NullableUsageCounterProgresses(
                previousUsage,
                previousUsage.CacheWriteTokens,
                candidateUsage.CacheWriteTokens)
            || !NullableUsageCounterProgresses(
                previousUsage,
                previousUsage.CacheMissTokens,
                candidateUsage.CacheMissTokens)
            || !NullableUsageCounterProgresses(
                previousUsage,
                previousUsage.ReasoningTokens,
                candidateUsage.ReasoningTokens)
            || !NullableUsageCounterProgresses(
                previousUsage,
                previousUsage.ProviderTotalTokens,
                candidateUsage.ProviderTotalTokens)
            || CompareNonNegativeDecimals(
                candidateUsage.CostUsd,
                previousUsage.CostUsd) < 0
            || candidateUsage.Actions < previousUsage.Actions
            || candidateUsage.UnaccountedProviderAttempts
            < previousUsage.UnaccountedProviderAttempts
            || previousUsage.HasUnaccountedUsage
            && !candidateUsage.HasUnaccountedUsage
            || string.Equals(
                   previousUsage.Availability,
                   UsageAvailabilityStates.CostUnavailable,
                   StringComparison.Ordinal)
               && !string.Equals(
                   candidateUsage.Availability,
                   UsageAvailabilityStates.CostUnavailable,
                   StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A run checkpoint decreases durable usage accounting.");
        }
    }

    private static bool NullableUsageCounterProgresses(
        AgentUsage previousUsage,
        int? previous,
        int? candidate)
    {
        if (previous.HasValue)
        {
            return !candidate.HasValue
                   || candidate.Value >= previous.Value;
        }

        var alreadySampled =
            previousUsage.ProviderUsageSamples > 0
            || previousUsage.InputTokens > 0
            || previousUsage.OutputTokens > 0
            || !string.Equals(
                previousUsage.CostUsd,
                "0",
                StringComparison.Ordinal);
        return !alreadySampled || !candidate.HasValue;
    }

    private static bool IsReplaySafeTurnAbandonment(
        RuntimeEvent runtimeEvent,
        AgentRun previous,
        AgentRun candidate)
    {
        return string.Equals(
                   runtimeEvent.Kind,
                   RuntimeEventKinds.TurnCompleted,
                   StringComparison.Ordinal)
               && string.Equals(
                   runtimeEvent.ReasonCode,
                   RunRecovery.ReplaySafeTurnAbandonedReason,
                   StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(runtimeEvent.TurnId)
               && string.Equals(
                   previous.CurrentTurnId,
                   runtimeEvent.TurnId,
                   StringComparison.Ordinal)
               && candidate.CurrentTurnId is null
               && previous.Usage.Turns > 0
               && candidate.Usage.Turns
               == previous.Usage.Turns - 1;
    }

    private static int CompareNonNegativeDecimals(
        string left,
        string right)
    {
        var leftDecimal = left.IndexOf('.');
        var rightDecimal = right.IndexOf('.');
        var leftIntegerLength =
            leftDecimal < 0 ? left.Length : leftDecimal;
        var rightIntegerLength =
            rightDecimal < 0 ? right.Length : rightDecimal;
        if (leftIntegerLength != rightIntegerLength)
        {
            return leftIntegerLength.CompareTo(rightIntegerLength);
        }

        var integerComparison = string.CompareOrdinal(
            left,
            0,
            right,
            0,
            leftIntegerLength);
        if (integerComparison != 0)
        {
            return integerComparison;
        }

        var leftFractionLength =
            leftDecimal < 0 ? 0 : left.Length - leftDecimal - 1;
        var rightFractionLength =
            rightDecimal < 0 ? 0 : right.Length - rightDecimal - 1;
        var maximumFractionLength = Math.Max(
            leftFractionLength,
            rightFractionLength);
        for (var index = 0; index < maximumFractionLength; index++)
        {
            var leftDigit = index < leftFractionLength
                ? left[leftDecimal + index + 1]
                : '0';
            var rightDigit = index < rightFractionLength
                ? right[rightDecimal + index + 1]
                : '0';
            if (leftDigit != rightDigit)
            {
                return leftDigit.CompareTo(rightDigit);
            }
        }

        return 0;
    }

    private static bool RequiresSameState(string eventKind)
    {
        return eventKind is RuntimeEventKinds.BudgetUpdated
            or RuntimeEventKinds.ProviderDispatchStarted
            or RuntimeEventKinds.ProviderDispatchKnownZero
            or RuntimeEventKinds.ProviderUsageUncertain
            or RuntimeEventKinds.ProviderResultCommitted
            or RuntimeEventKinds.ProviderResultDiscarded
            or RuntimeEventKinds.GameContextAdvanced;
    }

    private static bool IsAllowedAfterTerminal(string eventKind)
    {
        return eventKind is RuntimeEventKinds.BudgetUpdated
            or RuntimeEventKinds.ProviderDispatchKnownZero
            or RuntimeEventKinds.ProviderUsageUncertain
            or RuntimeEventKinds.ProviderResultCommitted
            or RuntimeEventKinds.ProviderResultDiscarded;
    }

    private static void ValidateTerminalMetadata(
        AgentRun previous,
        AgentRun candidate)
    {
        if (!string.Equals(
                candidate.CurrentTurnId,
                previous.CurrentTurnId,
                StringComparison.Ordinal)
            || !candidate.PendingOperationIds.SequenceEqual(
                previous.PendingOperationIds,
                StringComparer.Ordinal)
            || !string.Equals(
                candidate.TerminalReason,
                previous.TerminalReason,
                StringComparison.Ordinal)
            || !string.Equals(
                candidate.CompletionIntent,
                previous.CompletionIntent,
                StringComparison.Ordinal)
            || !ExtensionsAreEquivalent(
                candidate.Extensions,
                previous.Extensions))
        {
            throw new InvalidDataException(
                "A post-terminal checkpoint changes terminal run metadata.");
        }
    }

    private static bool ExtensionsAreEquivalent(
        IReadOnlyDictionary<string, JsonElement> left,
        IReadOnlyDictionary<string, JsonElement> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value)
                || !string.Equals(
                    ProtocolJson.Serialize(pair.Value),
                    ProtocolJson.Serialize(value),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TerminalKindMatches(
        string eventKind,
        string state)
    {
        return state switch
        {
            RunStates.Completed =>
                eventKind == RuntimeEventKinds.RunCompleted,
            RunStates.Interrupted =>
                eventKind == RuntimeEventKinds.RunInterrupted,
            RunStates.Failed =>
                eventKind == RuntimeEventKinds.RunFailed,
            RunStates.Cancelled =>
                eventKind == RuntimeEventKinds.RunCancelled,
            RunStates.BudgetExhausted =>
                eventKind == RuntimeEventKinds.RunBudgetExhausted,
            _ => false
        };
    }
}

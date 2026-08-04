using GameAgent.Protocol;

namespace GameAgent.Core;

public sealed class InvalidRunTransitionException : InvalidOperationException
{
    public InvalidRunTransitionException(string from, string to, string reason)
        : base($"Run transition '{from}' -> '{to}' is invalid: {reason}")
    {
        From = from;
        To = to;
        Reason = reason;
    }

    public string From { get; }

    public string To { get; }

    public string Reason { get; }
}

public sealed class RunTransition
{
    public string RunId { get; set; } = string.Empty;

    public long ExpectedRevision { get; set; }

    public string FromState { get; set; } = string.Empty;

    public string ToState { get; set; } = string.Empty;

    public string? TerminalReason { get; set; }

    public string? CompletionIntent { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}

public static class RunStateMachine
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedTransitions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [RunStates.Queued] = Set(
                RunStates.Preparing,
                RunStates.Running,
                RunStates.Cancelled,
                RunStates.Failed),
            [RunStates.Preparing] = Set(
                RunStates.Running,
                RunStates.Cancelled,
                RunStates.Failed),
            [RunStates.Running] = Set(
                RunStates.WaitingForAction,
                RunStates.Completed,
                RunStates.BudgetExhausted,
                RunStates.Cancelling,
                RunStates.Interrupting,
                RunStates.Failed),
            [RunStates.WaitingForAction] = Set(
                RunStates.Running,
                RunStates.Reconciling,
                RunStates.Cancelling,
                RunStates.Interrupting),
            [RunStates.Cancelling] = Set(
                RunStates.Cancelled,
                RunStates.Reconciling),
            [RunStates.Interrupting] = Set(
                RunStates.Interrupted,
                RunStates.Reconciling),
            [RunStates.Reconciling] = Set(
                RunStates.Running,
                RunStates.Cancelling,
                RunStates.Completed,
                RunStates.Cancelled,
                RunStates.Interrupted,
                RunStates.Failed),
            [RunStates.Completed] = Set(),
            [RunStates.BudgetExhausted] = Set(),
            [RunStates.Interrupted] = Set(),
            [RunStates.Cancelled] = Set(),
            [RunStates.Failed] = Set()
        };

    public static RunTransition Plan(
        AgentRun run,
        string targetState,
        DateTimeOffset timestamp,
        string? terminalReason = null,
        string? completionIntent = null)
    {
        if (!AllowedTransitions.TryGetValue(run.State, out var targets))
        {
            throw new InvalidRunTransitionException(
                run.State,
                targetState,
                "the source state is unknown");
        }

        if (!targets.Contains(targetState))
        {
            throw new InvalidRunTransitionException(
                run.State,
                targetState,
                "the transition is not in the state graph");
        }

        if (IsTerminal(targetState) && run.PendingOperationIds.Count > 0)
        {
            throw new InvalidRunTransitionException(
                run.State,
                targetState,
                "a terminal run cannot retain pending operations");
        }

        if (targetState == RunStates.Reconciling
            && run.PendingOperationIds.Count == 0)
        {
            throw new InvalidRunTransitionException(
                run.State,
                targetState,
                "reconciling requires at least one pending operation");
        }

        return new RunTransition
        {
            RunId = run.RunId,
            ExpectedRevision = run.Revision,
            FromState = run.State,
            ToState = targetState,
            TerminalReason = terminalReason,
            CompletionIntent = completionIntent,
            Timestamp = timestamp
        };
    }

    public static void ApplyCommitted(AgentRun run, RunTransition transition)
    {
        if (!string.Equals(run.RunId, transition.RunId, StringComparison.Ordinal)
            || run.Revision != transition.ExpectedRevision
            || !string.Equals(run.State, transition.FromState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The committed transition no longer matches the in-memory aggregate.");
        }

        run.State = transition.ToState;
        run.Revision++;
        run.TerminalReason = transition.TerminalReason;
        run.CompletionIntent = transition.CompletionIntent;
        run.UpdatedAt = transition.Timestamp;
    }

    public static bool IsTerminal(string state)
    {
        return state == RunStates.Completed
            || state == RunStates.BudgetExhausted
            || state == RunStates.Interrupted
            || state == RunStates.Cancelled
            || state == RunStates.Failed;
    }

    internal static bool IsStateTransitionAllowed(
        string fromState,
        string toState)
    {
        return AllowedTransitions.TryGetValue(
                   fromState,
                   out var targets)
               && targets.Contains(toState);
    }

    private static HashSet<string> Set(params string[] states)
    {
        return new HashSet<string>(states, StringComparer.Ordinal);
    }
}

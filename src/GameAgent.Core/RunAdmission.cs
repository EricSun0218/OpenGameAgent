using GameAgent.Protocol;

namespace GameAgent.Core;

internal static class RunAdmission
{
    public static void EnsureNewRun(AgentRun run, string parameterName)
    {
        ProtocolValidator.EnsureValid(run);
        if (!string.Equals(
                run.State,
                RunStates.Queued,
                StringComparison.Ordinal)
            || run.Revision != 0
            || run.CurrentTurnId is not null
            || run.PendingOperationIds.Count != 0
            || run.TerminalReason is not null
            || run.CompletionIntent is not null
            || run.Usage.HasUnaccountedUsage
            || run.Usage.UnaccountedProviderAttempts != 0)
        {
            throw new ArgumentException(
                "A new run must use a fresh queued snapshot.",
                parameterName);
        }
    }
}

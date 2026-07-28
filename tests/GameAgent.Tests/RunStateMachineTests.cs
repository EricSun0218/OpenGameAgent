using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Tests;

public sealed class RunStateMachineTests
{
    [Fact]
    public void TerminalTransitionRejectsPendingWorldOperation()
    {
        var run = CreateRun(RunStates.Running);
        run.PendingOperationIds.Add("operation-1");

        var error = Assert.Throws<InvalidRunTransitionException>(
            () => RunStateMachine.Plan(
                run,
                RunStates.Completed,
                DateTimeOffset.UtcNow));

        Assert.Contains("pending operations", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationWithPendingOperationMustReconcileFirst()
    {
        var run = CreateRun("cancelling");
        run.PendingOperationIds.Add("operation-1");

        var transition = RunStateMachine.Plan(
            run,
            RunStates.Reconciling,
            DateTimeOffset.UtcNow,
            completionIntent: RunStates.Cancelled);
        RunStateMachine.ApplyCommitted(run, transition);

        Assert.Equal(RunStates.Reconciling, run.State);
        Assert.Equal(1, run.Revision);
    }

    [Fact]
    public void CommittedTransitionRequiresMatchingRevision()
    {
        var run = CreateRun(RunStates.Running);
        var transition = RunStateMachine.Plan(
            run,
            RunStates.Completed,
            DateTimeOffset.UtcNow);
        run.Revision++;

        Assert.Throws<InvalidOperationException>(
            () => RunStateMachine.ApplyCommitted(run, transition));
    }

    private static AgentRun CreateRun(string state)
    {
        return new AgentRun
        {
            RunId = "run-state-machine",
            AgentId = "agent",
            WorldId = "world",
            State = state,
            Revision = 0,
            RuntimeGeneration = 1
        };
    }
}

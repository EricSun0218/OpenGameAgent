using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Workflow;

public static class WorkflowAgentStepKinds
{
    public const string Run = "agent.run";
}

public static class WorkflowAgentReasonCodes
{
    public const string Failed = "workflow_agent_failed";

    public const string Cancelled = "workflow_agent_cancelled";

    public const string Interrupted = "workflow_agent_interrupted";

    public const string BudgetExhausted =
        "workflow_agent_budget_exhausted";

    public const string InvalidRunIdentity =
        "workflow_agent_run_identity_invalid";

    public const string InvalidOutcome =
        "workflow_agent_outcome_invalid";
}

/// <summary>
/// Stable workflow coordinates supplied to a game-owned agent binding. The
/// nested agent run ID is independent of recovery attempt and lease owner.
/// </summary>
public sealed class WorkflowAgentInvocation
{
    internal WorkflowAgentInvocation(
        WorkflowStepContext context,
        string agentRunId)
    {
        WorkflowRunId = context.RunId;
        WorkflowId = context.WorkflowId;
        StageId = context.StageId;
        StageInstanceId = context.InstanceId;
        Attempt = context.Attempt;
        Generation = context.Generation;
        IsRecovery = context.IsRecovery;
        Settings = context.Settings.Clone();
        Checkpoint = context.Checkpoint?.Clone();
        AgentRunId = agentRunId;
    }

    public string WorkflowRunId { get; }

    public string WorkflowId { get; }

    public string StageId { get; }

    public string StageInstanceId { get; }

    public int Attempt { get; }

    public int Generation { get; }

    public bool IsRecovery { get; }

    public JsonElement Settings { get; }

    public JsonElement? Checkpoint { get; }

    public string AgentRunId { get; }
}

/// <summary>
/// Binds workflow JSON to a game-specific durable agent request. Implementors
/// must be deterministic for one workflow stage instance: recovery may invoke
/// these methods in a different process.
/// </summary>
public interface IWorkflowAgentRunAdapter
{
    DurableRunRequest CreateRequest(
        WorkflowAgentInvocation invocation,
        JsonElement input);

    DurableRunContinuation? CreateContinuation(
        WorkflowAgentInvocation invocation,
        JsonElement input);

    DurableRunResumeGuard? CreateResumeGuard(
        WorkflowAgentInvocation invocation,
        JsonElement input);

    JsonElement ProjectOutcome(
        WorkflowAgentInvocation invocation,
        JsonElement input,
        DurableRunOutcome outcome);
}

/// <summary>
/// Executes one durable agent run as a recoverable workflow step. The workflow
/// commits Started before dispatch; retries always address the same nested run
/// ID and use resume rather than replaying a completed side effect.
/// </summary>
public sealed class WorkflowAgentStepExecutor : IWorkflowStepExecutor
{
    private readonly IDurableAgentRuntime _runtime;
    private readonly IWorkflowAgentRunAdapter _adapter;
    private readonly IGameOperationReconciler? _reconciler;

    public WorkflowAgentStepExecutor(
        IDurableAgentRuntime runtime,
        IWorkflowAgentRunAdapter adapter,
        IGameOperationReconciler? reconciler = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _reconciler = reconciler;
    }

    public string Kind => WorkflowAgentStepKinds.Run;

    public async ValueTask<WorkflowStepResult> ExecuteAsync(
        WorkflowStepContext context,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var invocation = CreateInvocation(context);
        DurableRunRequest request;
        try
        {
            request = RequireRequest(invocation, input);
        }
        catch (WorkflowAgentBindingException exception)
        {
            return WorkflowStepResult.Failed(exception.ReasonCode);
        }

        try
        {
            var outcome = await _runtime
                .RunAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return Project(invocation, input, outcome);
        }
        catch (DuplicateRunException)
        {
            return await ResumeAsync(
                    invocation,
                    input,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<WorkflowStepResult> RecoverAsync(
        WorkflowStepContext context,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var invocation = CreateInvocation(context);
        try
        {
            return await ResumeAsync(
                    invocation,
                    input,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowAgentRunNotFoundException)
        {
            DurableRunRequest request;
            try
            {
                request = RequireRequest(invocation, input);
            }
            catch (WorkflowAgentBindingException exception)
            {
                return WorkflowStepResult.Failed(exception.ReasonCode);
            }

            try
            {
                var outcome = await _runtime
                    .RunAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return Project(invocation, input, outcome);
            }
            catch (DuplicateRunException)
            {
                throw Interrupted();
            }
        }
    }

    public static string CreateAgentRunId(
        string workflowRunId,
        string stageInstanceId)
    {
        var identity = WorkflowJson.CreateElement(writer =>
        {
            writer.WriteStartArray();
            writer.WriteStringValue(
                "gameagent.workflow.agent-run-identity.v1");
            writer.WriteStringValue(
                WorkflowValidation.RequiredIdentifier(
                    workflowRunId,
                    nameof(workflowRunId),
                    256,
                    allowSlash: true));
            writer.WriteStringValue(
                WorkflowValidation.RequiredIdentifier(
                    stageInstanceId,
                    nameof(stageInstanceId),
                    256,
                    allowSlash: true));
            writer.WriteEndArray();
        });
        return "wfa_" + WorkflowIdentity.ComputeJsonDigest(identity);
    }

    private WorkflowAgentInvocation CreateInvocation(
        WorkflowStepContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return new WorkflowAgentInvocation(
            context,
            CreateAgentRunId(context.RunId, context.InstanceId));
    }

    private DurableRunRequest RequireRequest(
        WorkflowAgentInvocation invocation,
        JsonElement input)
    {
        var request = _adapter.CreateRequest(invocation, input.Clone())
                      ?? throw new InvalidOperationException(
                          "The workflow agent adapter returned no request.");
        if (request.Run is null
            || !string.Equals(
                request.Run.RunId,
                invocation.AgentRunId,
                StringComparison.Ordinal))
        {
            throw new WorkflowAgentBindingException(
                WorkflowAgentReasonCodes.InvalidRunIdentity);
        }

        return request;
    }

    private async ValueTask<WorkflowStepResult> ResumeAsync(
        WorkflowAgentInvocation invocation,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        try
        {
            var continuation = _adapter.CreateContinuation(
                invocation,
                input.Clone());
            var guard = _adapter.CreateResumeGuard(
                invocation,
                input.Clone());
            var outcome = await _runtime
                .ResumeAsync(
                    invocation.AgentRunId,
                    continuation,
                    _reconciler,
                    cancellationToken,
                    guard)
                .ConfigureAwait(false);
            return Project(invocation, input, outcome);
        }
        catch (KeyNotFoundException exception)
        {
            throw new WorkflowAgentRunNotFoundException(exception);
        }
        catch (DuplicateRunException)
        {
            throw Interrupted();
        }
    }

    private WorkflowStepResult Project(
        WorkflowAgentInvocation invocation,
        JsonElement input,
        DurableRunOutcome outcome)
    {
        if (outcome is null
            || outcome.Run is null
            || !string.Equals(
                outcome.Run.RunId,
                invocation.AgentRunId,
                StringComparison.Ordinal))
        {
            return WorkflowStepResult.Failed(
                WorkflowAgentReasonCodes.InvalidOutcome);
        }

        if (outcome.ReconciliationRequired || !outcome.IsTerminal)
        {
            throw Interrupted();
        }

        if (string.Equals(
                outcome.Run.State,
                RunStates.Completed,
                StringComparison.Ordinal))
        {
            var output = _adapter.ProjectOutcome(
                invocation,
                input.Clone(),
                outcome);
            return output.ValueKind == JsonValueKind.Undefined
                ? WorkflowStepResult.Failed(
                    WorkflowAgentReasonCodes.InvalidOutcome)
                : WorkflowStepResult.Completed(output);
        }

        return outcome.Run.State switch
        {
            RunStates.Cancelled => WorkflowStepResult.Failed(
                WorkflowAgentReasonCodes.Cancelled),
            RunStates.Interrupted => WorkflowStepResult.Failed(
                WorkflowAgentReasonCodes.Interrupted),
            RunStates.BudgetExhausted => WorkflowStepResult.Failed(
                WorkflowAgentReasonCodes.BudgetExhausted),
            RunStates.Failed => WorkflowStepResult.Failed(
                WorkflowAgentReasonCodes.Failed),
            _ => WorkflowStepResult.Failed(
                WorkflowAgentReasonCodes.InvalidOutcome)
        };
    }

    private static WorkflowExecutorInterruptedException Interrupted()
    {
        return new WorkflowExecutorInterruptedException(
            "The durable agent run is still owned, nonterminal, or awaiting reconciliation.");
    }

    private sealed class WorkflowAgentRunNotFoundException : Exception
    {
        public WorkflowAgentRunNotFoundException(Exception innerException)
            : base("The durable agent run does not exist.", innerException)
        {
        }
    }
}

public sealed class WorkflowAgentBindingException : InvalidOperationException
{
    public WorkflowAgentBindingException(string reasonCode)
        : base("The workflow agent adapter produced an invalid binding.")
    {
        ReasonCode = WorkflowValidation.RequiredIdentifier(
            reasonCode,
            nameof(reasonCode),
            128,
            allowSlash: false);
    }

    public string ReasonCode { get; }
}

using System.Collections.ObjectModel;
using GameAgent.Core;

namespace GameAgent.Workflow;

/// <summary>
/// Binds named compiled workflows to the common execution router. Replacing
/// the catalog affects only later dispatches; each run captures one immutable
/// compiled workflow instance before execution.
/// </summary>
public sealed class RoutedWorkflowRuntime : IRoutedWorkflowRuntime
{
    private readonly WorkflowRunner _runner;
    private IReadOnlyDictionary<string, CompiledWorkflow> _workflows;

    public RoutedWorkflowRuntime(
        WorkflowRunner runner,
        IEnumerable<CompiledWorkflow> workflows)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _workflows = Snapshot(workflows);
    }

    public IReadOnlyList<string> WorkflowIds =>
        Volatile.Read(ref _workflows).Keys.ToArray();

    public void Replace(IEnumerable<CompiledWorkflow> workflows)
    {
        Volatile.Write(ref _workflows, Snapshot(workflows));
    }

    public async ValueTask<RoutedWorkflowOutcome> RunAsync(
        RoutedWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var workflowId = WorkflowValidation.RequiredIdentifier(
            request.WorkflowId,
            nameof(request.WorkflowId),
            128,
            allowSlash: true);
        var workflows = Volatile.Read(ref _workflows);
        if (!workflows.TryGetValue(workflowId, out var workflow))
        {
            throw new KeyNotFoundException(
                $"Workflow '{workflowId}' is not registered.");
        }

        var snapshot = await _runner.ExecuteAsync(
                workflow,
                new WorkflowRunRequest(
                    request.RunKey,
                    request.OwnerId,
                    request.Input),
                cancellationToken)
            .ConfigureAwait(false);
        return new RoutedWorkflowOutcome
        {
            RunId = snapshot.RunId,
            WorkflowId = snapshot.WorkflowId,
            Status = snapshot.Status.ToString().ToLowerInvariant(),
            ReasonCode = snapshot.ReasonCode,
            Output = snapshot.Output?.Clone()
        };
    }

    private static IReadOnlyDictionary<string, CompiledWorkflow> Snapshot(
        IEnumerable<CompiledWorkflow>? workflows)
    {
        if (workflows is null)
        {
            throw new ArgumentNullException(nameof(workflows));
        }

        var result = new Dictionary<string, CompiledWorkflow>(
            StringComparer.Ordinal);
        foreach (var workflow in WorkflowCollections.MaterializeBounded(
                     workflows,
                     1_024,
                     nameof(workflows)))
        {
            if (workflow is null)
            {
                throw new ArgumentException(
                    "Workflow catalogs cannot contain null entries.",
                    nameof(workflows));
            }

            var id = workflow.Definition.Id;
            if (!result.TryAdd(id, workflow))
            {
                throw new ArgumentException(
                    $"Workflow '{id}' is registered more than once.",
                    nameof(workflows));
            }
        }

        return new ReadOnlyDictionary<string, CompiledWorkflow>(result);
    }
}

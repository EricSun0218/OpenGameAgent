using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public enum GameWorkflowStepStatus
{
    Continue,
    Wait,
    Complete,
    Failed,
}

public sealed class GameWorkflowStepResult
{
    public GameWorkflowStepResult(
        GameWorkflowStepStatus status,
        string stateJson,
        IReadOnlyList<AgentMessage>? messages = null,
        string? error = null)
    {
        if (!Enum.IsDefined(typeof(GameWorkflowStepStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (status == GameWorkflowStepStatus.Failed && string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A failed workflow step requires an error.", nameof(error));
        }

        if (status != GameWorkflowStepStatus.Failed && error is not null)
        {
            throw new ArgumentException("Only a failed workflow step can carry an error.", nameof(error));
        }

        if (messages?.Any(message => message is null) == true)
        {
            throw new ArgumentException("Workflow step output cannot contain null messages.", nameof(messages));
        }

        Status = status;
        StateJson = GameJson.RequireValid(stateJson, nameof(stateJson));
        Messages = Array.AsReadOnly((messages ?? Array.Empty<AgentMessage>()).ToArray());
        Error = error;
    }

    public GameWorkflowStepStatus Status { get; }

    public string StateJson { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public string? Error { get; }

    public static GameWorkflowStepResult Next(string stateJson, params AgentMessage[] messages) =>
        new(GameWorkflowStepStatus.Continue, stateJson, messages);

    public static GameWorkflowStepResult Wait(string stateJson, params AgentMessage[] messages) =>
        new(GameWorkflowStepStatus.Wait, stateJson, messages);

    public static GameWorkflowStepResult Complete(string stateJson, params AgentMessage[] messages) =>
        new(GameWorkflowStepStatus.Complete, stateJson, messages);

    public static GameWorkflowStepResult Fail(string stateJson, string error, params AgentMessage[] messages) =>
        new(GameWorkflowStepStatus.Failed, stateJson, messages, error);
}

public sealed class GameWorkflowStepContext
{
    internal GameWorkflowStepContext(
        GameWorkflowContext run,
        string instanceId,
        string stepId,
        int stepIndex,
        string stateJson)
    {
        Run = run;
        InstanceId = instanceId;
        StepId = stepId;
        StepIndex = stepIndex;
        StateJson = stateJson;
    }

    public GameWorkflowContext Run { get; }

    public string InstanceId { get; }

    public string StepId { get; }

    public int StepIndex { get; }

    public string StateJson { get; }

    public string CreateOperationId(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            throw new ArgumentException("An operation suffix is required.", nameof(suffix));
        }

        return GameJson.JoinIds(InstanceId, StepId, suffix);
    }
}

public delegate ValueTask<GameWorkflowStepResult> GameWorkflowStepHandler(
    GameWorkflowStepContext context,
    CancellationToken cancellationToken);

public sealed class GameWorkflowStep
{
    public GameWorkflowStep(string stepId, GameWorkflowStepHandler handler)
    {
        StepId = GameJson.RequireId(stepId, nameof(stepId));
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public string StepId { get; }

    public GameWorkflowStepHandler Handler { get; }
}

public sealed class GameWorkflowCheckpoint
{
    public GameWorkflowCheckpoint(
        string instanceId,
        string workflow,
        long revision,
        int nextStep,
        string stateJson,
        bool completed = false,
        string? error = null,
        GameWorkflowInvocationResult? invocation = null)
    {
        if (revision < 0 || nextStep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        InstanceId = GameJson.RequireId(instanceId, nameof(instanceId));
        Workflow = GameJson.RequireId(workflow, nameof(workflow));
        Revision = revision;
        NextStep = nextStep;
        StateJson = GameJson.RequireValid(stateJson, nameof(stateJson));
        if (!completed && error is not null)
        {
            throw new ArgumentException("Only a completed workflow checkpoint can carry an error.", nameof(error));
        }

        if (error is not null && string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A workflow checkpoint error cannot be empty.", nameof(error));
        }

        Completed = completed;
        Error = error;
        Invocation = invocation;
    }

    public string InstanceId { get; }

    public string Workflow { get; }

    public long Revision { get; }

    public int NextStep { get; }

    public string StateJson { get; }

    public bool Completed { get; }

    public string? Error { get; }

    public GameWorkflowInvocationResult? Invocation { get; }
}

public sealed class GameWorkflowInvocationResult
{
    public GameWorkflowInvocationResult(
        string inputId,
        IReadOnlyList<AgentMessage> messages,
        bool complete,
        bool succeeded = false,
        string? error = null)
    {
        InputId = GameJson.RequireId(inputId, nameof(inputId));
        var copied = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();
        if (copied.Any(message => message is null))
        {
            throw new ArgumentException("Workflow invocation messages cannot contain null entries.", nameof(messages));
        }

        if (succeeded && (!complete || error is not null))
        {
            throw new ArgumentException("Only a completed successful invocation can be marked successful.", nameof(succeeded));
        }

        if (complete && !succeeded && string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A completed failed invocation requires an error.", nameof(error));
        }

        if (!complete && error is not null)
        {
            throw new ArgumentException("An incomplete invocation cannot carry an error.", nameof(error));
        }

        if (error is { Length: > 65_536 })
        {
            throw new ArgumentException("A workflow invocation error cannot exceed 65,536 characters.", nameof(error));
        }

        Messages = Array.AsReadOnly(copied);
        Complete = complete;
        Succeeded = succeeded;
        Error = error;
    }

    public string InputId { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public bool Complete { get; }

    public bool Succeeded { get; }

    public string? Error { get; }
}

public sealed class GameWorkflowCheckpointSaveResult
{
    public GameWorkflowCheckpointSaveResult(bool saved, GameWorkflowCheckpoint current)
    {
        Saved = saved;
        Current = current ?? throw new ArgumentNullException(nameof(current));
    }

    public bool Saved { get; }

    public GameWorkflowCheckpoint Current { get; }
}

public interface IGameWorkflowCheckpointStore
{
    ValueTask<GameWorkflowCheckpoint?> LoadAsync(string instanceId, CancellationToken cancellationToken);

    ValueTask<GameWorkflowCheckpointSaveResult> SaveAsync(
        GameWorkflowCheckpoint checkpoint,
        long expectedRevision,
        CancellationToken cancellationToken);
}

public sealed class InMemoryGameWorkflowCheckpointStore : IGameWorkflowCheckpointStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameWorkflowCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public InMemoryGameWorkflowCheckpointStore(int capacity = 100_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public ValueTask<GameWorkflowCheckpoint?> LoadAsync(string instanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return new ValueTask<GameWorkflowCheckpoint?>(
                _checkpoints.TryGetValue(GameJson.RequireId(instanceId, nameof(instanceId)), out var checkpoint)
                    ? checkpoint
                    : null);
        }
    }

    public ValueTask<GameWorkflowCheckpointSaveResult> SaveAsync(
        GameWorkflowCheckpoint checkpoint,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (checkpoint is null)
        {
            throw new ArgumentNullException(nameof(checkpoint));
        }

        lock (_gate)
        {
            if (_checkpoints.TryGetValue(checkpoint.InstanceId, out var current))
            {
                EnsureMutableIdentity(current, checkpoint);
                if (current.Revision != expectedRevision)
                {
                    return new ValueTask<GameWorkflowCheckpointSaveResult>(
                        new GameWorkflowCheckpointSaveResult(false, current));
                }
            }
            else
            {
                if (expectedRevision != 0)
                {
                    return new ValueTask<GameWorkflowCheckpointSaveResult>(
                        new GameWorkflowCheckpointSaveResult(
                            false,
                            new GameWorkflowCheckpoint(checkpoint.InstanceId, checkpoint.Workflow, 0, 0, "{}")));
                }

                if (_checkpoints.Count >= _capacity)
                {
                    throw new GameRuntimeLimitException(nameof(_capacity), "The workflow checkpoint store reached its capacity.");
                }
            }

            if (checkpoint.Revision != checked(expectedRevision + 1))
            {
                throw new ArgumentException("A workflow checkpoint revision must advance by exactly one.", nameof(checkpoint));
            }

            _checkpoints[checkpoint.InstanceId] = checkpoint;
            return new ValueTask<GameWorkflowCheckpointSaveResult>(
                new GameWorkflowCheckpointSaveResult(true, checkpoint));
        }
    }

    private static void EnsureMutableIdentity(
        GameWorkflowCheckpoint current,
        GameWorkflowCheckpoint next)
    {
        if (!string.Equals(current.Workflow, next.Workflow, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A workflow checkpoint instance cannot change workflows.");
        }

        if (current.Completed)
        {
            throw new InvalidOperationException("A completed workflow checkpoint is immutable.");
        }
    }
}

public sealed class DurableGameWorkflow : IGameWorkflow
{
    private readonly IReadOnlyList<GameWorkflowStep> _steps;
    private readonly IGameWorkflowCheckpointStore _checkpoints;
    private readonly int _maximumStepsPerRun;
    private readonly string _initialStateJson;

    public DurableGameWorkflow(
        string name,
        IEnumerable<GameWorkflowStep> steps,
        IGameWorkflowCheckpointStore checkpoints,
        string initialStateJson = "{}",
        int maximumStepsPerRun = 64)
    {
        Name = GameJson.RequireId(name, nameof(name));
        if (steps is null)
        {
            throw new ArgumentNullException(nameof(steps));
        }

        var copied = steps.ToArray();
        if (copied.Length == 0 || copied.Any(step => step is null))
        {
            throw new ArgumentException("A workflow requires at least one non-null step.", nameof(steps));
        }

        var duplicate = copied.GroupBy(step => step.StepId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate workflow step ID '{duplicate.Key}'.", nameof(steps));
        }

        if (maximumStepsPerRun < 1 || maximumStepsPerRun > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStepsPerRun));
        }

        _steps = new ReadOnlyCollection<GameWorkflowStep>(copied);
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _initialStateJson = GameJson.RequireValid(initialStateJson, nameof(initialStateJson));
        _maximumStepsPerRun = maximumStepsPerRun;
    }

    public string Name { get; }

    public async ValueTask<GameWorkflowResult> RunAsync(
        GameWorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var externalInstance = context.Input.Metadata.TryGetValue("agent.workflow_instance", out var configured)
            ? configured
            : context.Input.InputId;
        var instanceId = GameJson.JoinIds(context.Input.SessionId, context.Input.ActorId, Name, externalInstance);
        var checkpoint = await _checkpoints.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? new GameWorkflowCheckpoint(instanceId, Name, 0, 0, _initialStateJson);
        if (!string.Equals(checkpoint.InstanceId, instanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The workflow checkpoint store returned a different workflow instance.");
        }

        if (!string.Equals(checkpoint.Workflow, Name, StringComparison.Ordinal))
        {
            return new GameWorkflowResult(Array.Empty<AgentMessage>(), false, "The workflow checkpoint belongs to a different workflow.");
        }

        if (checkpoint.NextStep > _steps.Count)
        {
            throw new InvalidOperationException("The workflow checkpoint points past the end of the workflow.");
        }

        var invocation = checkpoint.Invocation;
        if (invocation is not null
            && !string.Equals(invocation.InputId, context.Input.InputId, StringComparison.Ordinal)
            && !context.Session.ProcessedInputIds.Contains(invocation.InputId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workflow input '{invocation.InputId}' has durable progress that must be replayed before another input can continue this instance.");
        }

        if (invocation is { Complete: true }
            && string.Equals(invocation.InputId, context.Input.InputId, StringComparison.Ordinal))
        {
            context.ValidateOutput(invocation.Messages);
            return new GameWorkflowResult(invocation.Messages, invocation.Succeeded, invocation.Error);
        }

        if (checkpoint.Completed)
        {
            return new GameWorkflowResult(Array.Empty<AgentMessage>(), checkpoint.Error is null, checkpoint.Error);
        }

        if (invocation is null
            || !string.Equals(invocation.InputId, context.Input.InputId, StringComparison.Ordinal))
        {
            invocation = new GameWorkflowInvocationResult(
                context.Input.InputId,
                Array.Empty<AgentMessage>(),
                complete: false);
        }

        var messages = invocation.Messages.ToList();
        context.ValidateOutput(messages);
        for (var executed = 0; executed < _maximumStepsPerRun; executed++)
        {
            if (checkpoint.NextStep >= _steps.Count)
            {
                invocation = CompleteInvocation(context.Input.InputId, messages, succeeded: true, error: null);
                checkpoint = await SaveAsync(
                    checkpoint,
                    checkpoint.NextStep,
                    checkpoint.StateJson,
                    completed: true,
                    error: null,
                    invocation: invocation,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return new GameWorkflowResult(messages, true);
            }

            var step = _steps[checkpoint.NextStep];
            var result = await step.Handler(
                new GameWorkflowStepContext(context, instanceId, step.StepId, checkpoint.NextStep, checkpoint.StateJson),
                cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                throw new InvalidOperationException($"Workflow step '{step.StepId}' returned null.");
            }

            var nextMessages = messages.Concat(result.Messages).ToArray();
            context.ValidateOutput(nextMessages);
            messages.AddRange(result.Messages);
            var nextStep = result.Status == GameWorkflowStepStatus.Wait
                ? checkpoint.NextStep
                : checkpoint.NextStep + 1;
            var completed = result.Status is GameWorkflowStepStatus.Complete or GameWorkflowStepStatus.Failed;
            invocation = result.Status is GameWorkflowStepStatus.Wait
                or GameWorkflowStepStatus.Complete
                or GameWorkflowStepStatus.Failed
                ? CompleteInvocation(
                    context.Input.InputId,
                    messages,
                    succeeded: result.Status != GameWorkflowStepStatus.Failed,
                    error: result.Error)
                : new GameWorkflowInvocationResult(
                    context.Input.InputId,
                    messages,
                    complete: false);
            checkpoint = await SaveAsync(
                checkpoint,
                nextStep,
                result.StateJson,
                completed,
                result.Error,
                invocation,
                cancellationToken).ConfigureAwait(false);

            if (result.Status == GameWorkflowStepStatus.Wait)
            {
                return new GameWorkflowResult(messages, true);
            }

            if (result.Status == GameWorkflowStepStatus.Complete)
            {
                return new GameWorkflowResult(messages, true);
            }

            if (result.Status == GameWorkflowStepStatus.Failed)
            {
                return new GameWorkflowResult(messages, false, result.Error);
            }
        }

        const string limitError = "The workflow reached its per-run step limit.";
        invocation = CompleteInvocation(context.Input.InputId, messages, succeeded: false, error: limitError);
        _ = await SaveAsync(
            checkpoint,
            checkpoint.NextStep,
            checkpoint.StateJson,
            completed: false,
            error: null,
            invocation: invocation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new GameWorkflowResult(messages, false, limitError);
    }

    private async ValueTask<GameWorkflowCheckpoint> SaveAsync(
        GameWorkflowCheckpoint current,
        int nextStep,
        string stateJson,
        bool completed,
        string? error,
        GameWorkflowInvocationResult invocation,
        CancellationToken cancellationToken)
    {
        var next = new GameWorkflowCheckpoint(
            current.InstanceId,
            Name,
            checked(current.Revision + 1),
            nextStep,
            stateJson,
            completed,
            error,
            invocation);
        var save = await _checkpoints.SaveAsync(next, current.Revision, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The workflow checkpoint store returned null.");
        if (!save.Saved)
        {
            throw new InvalidOperationException("The workflow checkpoint changed concurrently.");
        }

        if (!string.Equals(save.Current.InstanceId, next.InstanceId, StringComparison.Ordinal)
            || !string.Equals(save.Current.Workflow, next.Workflow, StringComparison.Ordinal)
            || save.Current.Revision != next.Revision
            || save.Current.NextStep != next.NextStep
            || save.Current.Completed != next.Completed
            || !string.Equals(save.Current.StateJson, next.StateJson, StringComparison.Ordinal)
            || !string.Equals(save.Current.Error, next.Error, StringComparison.Ordinal)
            || !GameAgentValueComparer.WorkflowInvocationEquals(save.Current.Invocation, next.Invocation))
        {
            throw new InvalidOperationException("The workflow checkpoint store returned a different saved checkpoint.");
        }

        return save.Current;
    }

    private static GameWorkflowInvocationResult CompleteInvocation(
        string inputId,
        IReadOnlyList<AgentMessage> messages,
        bool succeeded,
        string? error) =>
        new(inputId, messages, complete: true, succeeded, error);

}

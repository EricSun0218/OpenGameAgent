using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Extensions;

public enum GameWorkflowNodeStatus
{
    Completed,
    Wait,
    Failed,
}

public sealed class GameWorkflowNodeResult
{
    public GameWorkflowNodeResult(
        GameWorkflowNodeStatus status,
        string outputJson,
        IReadOnlyList<AgentMessage>? messages = null,
        string? error = null)
    {
        if (!Enum.IsDefined(typeof(GameWorkflowNodeStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (status == GameWorkflowNodeStatus.Failed && string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A failed workflow node requires an error.", nameof(error));
        }

        if (status != GameWorkflowNodeStatus.Failed && error is not null)
        {
            throw new ArgumentException("Only a failed workflow node can carry an error.", nameof(error));
        }

        OutputJson = RequireJson(outputJson, nameof(outputJson));
        var copied = (messages ?? Array.Empty<AgentMessage>()).ToArray();
        if (copied.Any(message => message is null))
        {
            throw new ArgumentException("Workflow node output cannot contain null messages.", nameof(messages));
        }

        Status = status;
        Messages = Array.AsReadOnly(copied);
        Error = error;
    }

    public GameWorkflowNodeStatus Status { get; }

    public string OutputJson { get; }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public string? Error { get; }

    public static GameWorkflowNodeResult Complete(string outputJson, params AgentMessage[] messages) =>
        new(GameWorkflowNodeStatus.Completed, outputJson, messages);

    public static GameWorkflowNodeResult Wait(string outputJson, params AgentMessage[] messages) =>
        new(GameWorkflowNodeStatus.Wait, outputJson, messages);

    public static GameWorkflowNodeResult Fail(string outputJson, string error, params AgentMessage[] messages) =>
        new(GameWorkflowNodeStatus.Failed, outputJson, messages, error);

    private static string RequireJson(string value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        try
        {
            return new JsonContent(value).Json;
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The value must contain valid JSON.", parameterName, exception);
        }
    }
}

public sealed class GameWorkflowNodeContext
{
    internal GameWorkflowNodeContext(
        GameWorkflowContext run,
        string instanceId,
        string nodeId,
        string previousOutputJson,
        IReadOnlyDictionary<string, string> dependencyOutputs)
    {
        Run = run;
        InstanceId = instanceId;
        NodeId = nodeId;
        PreviousOutputJson = previousOutputJson;
        DependencyOutputs = dependencyOutputs;
    }

    public GameWorkflowContext Run { get; }

    public string InstanceId { get; }

    public string NodeId { get; }

    public string PreviousOutputJson { get; }

    public IReadOnlyDictionary<string, string> DependencyOutputs { get; }

    public string CreateOperationId(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            throw new ArgumentException("An operation suffix is required.", nameof(suffix));
        }

        return string.Join(":", new[] { InstanceId, NodeId, suffix }.Select(Uri.EscapeDataString));
    }
}

public delegate ValueTask<GameWorkflowNodeResult> GameWorkflowNodeHandler(
    GameWorkflowNodeContext context,
    CancellationToken cancellationToken);

public sealed class GameWorkflowNode
{
    public GameWorkflowNode(
        string nodeId,
        GameWorkflowNodeHandler handler,
        IReadOnlyCollection<string>? dependencies = null)
    {
        NodeId = RequireId(nodeId, nameof(nodeId));
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        var copied = (dependencies ?? Array.Empty<string>())
            .Select(value => RequireId(value, nameof(dependencies)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (copied.Contains(NodeId, StringComparer.Ordinal))
        {
            throw new ArgumentException("A workflow node cannot depend on itself.", nameof(dependencies));
        }

        Dependencies = Array.AsReadOnly(copied);
    }

    public string NodeId { get; }

    public IReadOnlyList<string> Dependencies { get; }

    public GameWorkflowNodeHandler Handler { get; }

    private static string RequireId(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 512
            ? throw new ArgumentException("A non-empty identifier of at most 512 characters is required.", parameterName)
            : value;
}

/// <summary>
/// A checkpointed acyclic workflow whose independent nodes run concurrently and whose
/// outputs are joined through explicit dependencies. Node handlers remain game-owned.
/// </summary>
public sealed class DurableGameWorkflowGraph : IGameWorkflow
{
    private static readonly JsonSerializerOptions StateSerializerOptions = new() { MaxDepth = 128 };
    private readonly IReadOnlyList<GameWorkflowNode> _nodes;
    private readonly IReadOnlyDictionary<string, GameWorkflowNode> _nodesById;
    private readonly IReadOnlyDictionary<string, int> _nodeOrder;
    private readonly IGameWorkflowCheckpointStore _checkpoints;
    private readonly int _maximumConcurrentNodes;
    private readonly int _maximumNodesPerRun;
    private readonly int _maximumStateCharacters;
    private readonly string _definition;

    public DurableGameWorkflowGraph(
        string name,
        IEnumerable<GameWorkflowNode> nodes,
        IGameWorkflowCheckpointStore checkpoints,
        int maximumConcurrentNodes = 4,
        int maximumNodesPerRun = 256,
        int maximumStateCharacters = 1_000_000)
    {
        Name = string.IsNullOrWhiteSpace(name) || name.Length > 512
            ? throw new ArgumentException("A workflow name of at most 512 characters is required.", nameof(name))
            : name;
        var copied = (nodes ?? throw new ArgumentNullException(nameof(nodes))).ToArray();
        if (copied.Length == 0 || copied.Length > 1_024 || copied.Any(node => node is null))
        {
            throw new ArgumentException("A workflow graph requires between 1 and 1,024 nodes.", nameof(nodes));
        }

        var duplicate = copied.GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate workflow node ID '{duplicate.Key}'.", nameof(nodes));
        }

        var ids = copied.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        var missing = copied.SelectMany(node => node.Dependencies)
            .FirstOrDefault(dependency => !ids.Contains(dependency));
        if (missing is not null)
        {
            throw new ArgumentException($"Workflow dependency '{missing}' does not exist.", nameof(nodes));
        }

        EnsureAcyclic(copied);
        if (maximumConcurrentNodes < 1 || maximumConcurrentNodes > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentNodes));
        }

        if (maximumNodesPerRun < 1 || maximumNodesPerRun > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNodesPerRun));
        }

        if (maximumStateCharacters < 1_024 || maximumStateCharacters > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStateCharacters));
        }

        _nodes = Array.AsReadOnly(copied);
        _nodesById = new ReadOnlyDictionary<string, GameWorkflowNode>(
            copied.ToDictionary(node => node.NodeId, StringComparer.Ordinal));
        _nodeOrder = new ReadOnlyDictionary<string, int>(copied
            .Select((node, index) => (node.NodeId, index))
            .ToDictionary(value => value.NodeId, value => value.index, StringComparer.Ordinal));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _maximumConcurrentNodes = Math.Min(maximumConcurrentNodes, copied.Length);
        _maximumNodesPerRun = maximumNodesPerRun;
        _maximumStateCharacters = maximumStateCharacters;
        _definition = ComputeDefinition(copied);
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
        var instanceId = string.Join(":", new[]
        {
            context.Input.SessionId,
            context.Input.ActorId,
            Name,
            externalInstance,
        }.Select(Uri.EscapeDataString));
        var checkpoint = await _checkpoints.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? new GameWorkflowCheckpoint(instanceId, Name, 0, 0, Serialize(CreateInitialState()));
        ValidateCheckpoint(checkpoint, instanceId);
        var state = Deserialize(checkpoint.StateJson);
        ValidateState(state, checkpoint);
        var invocation = checkpoint.Invocation;
        if (invocation is { } previousInvocation
            && !string.Equals(previousInvocation.InputId, context.Input.InputId, StringComparison.Ordinal)
            && !context.Session.ProcessedInputIds.Contains(previousInvocation.InputId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workflow input '{previousInvocation.InputId}' has a durable result that must be replayed before another input can continue this instance.");
        }

        if (invocation is not null
            && string.Equals(invocation.InputId, context.Input.InputId, StringComparison.Ordinal)
            && invocation.Complete)
        {
            var replay = invocation.Messages;
            context.ValidateOutput(replay);
            return new GameWorkflowResult(replay, invocation.Succeeded, invocation.Error);
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
        var waited = new HashSet<string>(StringComparer.Ordinal);
        var executed = 0;
        while (executed < _maximumNodesPerRun)
        {
            var ready = _nodes
                .Where(node => state.Nodes[node.NodeId].Status == NodeState.Pending)
                .Where(node => !waited.Contains(node.NodeId))
                .Where(node => node.Dependencies.All(dependency =>
                    state.Nodes[dependency].Status == NodeState.Completed))
                .Take(Math.Min(_maximumConcurrentNodes, _maximumNodesPerRun - executed))
                .ToArray();
            if (ready.Length == 0)
            {
                if (state.Nodes.Values.All(node => node.Status == NodeState.Completed))
                {
                    invocation = CompleteInvocation(context.Input.InputId, messages, succeeded: true, error: null);
                    checkpoint = await SaveAsync(
                        checkpoint,
                        state,
                        completed: true,
                        error: null,
                        invocation: invocation,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    return new GameWorkflowResult(messages, true);
                }

                if (waited.Count > 0)
                {
                    invocation = CompleteInvocation(context.Input.InputId, messages, succeeded: true, error: null);
                    checkpoint = await SaveAsync(
                        checkpoint,
                        state,
                        completed: false,
                        error: null,
                        invocation: invocation,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    return new GameWorkflowResult(messages, true);
                }

                throw new InvalidOperationException("The workflow graph has unfinished nodes but no runnable node.");
            }

            var tasks = ready.Select(node => ExecuteAsync(node, state, context, instanceId, cancellationToken)).ToArray();
            var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
            executed = checked(executed + outcomes.Length);
            string? failure = null;
            foreach (var outcome in outcomes.OrderBy(value => _nodeOrder[value.Node.NodeId]))
            {
                messages.AddRange(outcome.Result.Messages);
                context.ValidateOutput(messages);
                var nodeState = state.Nodes[outcome.Node.NodeId];
                nodeState.OutputJson = outcome.Result.OutputJson;
                switch (outcome.Result.Status)
                {
                    case GameWorkflowNodeStatus.Completed:
                        nodeState.Status = NodeState.Completed;
                        break;
                    case GameWorkflowNodeStatus.Wait:
                        waited.Add(outcome.Node.NodeId);
                        break;
                    case GameWorkflowNodeStatus.Failed:
                        nodeState.Status = NodeState.Failed;
                        nodeState.Error = BoundError(outcome.Result.Error!);
                        failure ??= BoundError($"Workflow node '{outcome.Node.NodeId}' failed: {nodeState.Error}");
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported workflow node status.");
                }
            }

            var graphCompleted = failure is null
                && state.Nodes.Values.All(node => node.Status == NodeState.Completed);
            invocation = failure is not null || graphCompleted
                ? CompleteInvocation(context.Input.InputId, messages, failure is null, failure)
                : new GameWorkflowInvocationResult(
                    context.Input.InputId,
                    messages,
                    complete: false);

            checkpoint = await SaveAsync(
                checkpoint,
                state,
                completed: failure is not null || graphCompleted,
                error: failure,
                invocation: invocation,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return new GameWorkflowResult(messages, false, failure);
            }

            if (graphCompleted)
            {
                return new GameWorkflowResult(messages, true);
            }
        }

        const string limitError = "The workflow graph reached its per-run node limit.";
        invocation = CompleteInvocation(context.Input.InputId, messages, succeeded: false, error: limitError);
        _ = await SaveAsync(
            checkpoint,
            state,
            completed: false,
            error: null,
            invocation: invocation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new GameWorkflowResult(messages, false, limitError);
    }

    private async Task<NodeOutcome> ExecuteAsync(
        GameWorkflowNode node,
        GraphState state,
        GameWorkflowContext run,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var dependencies = new ReadOnlyDictionary<string, string>(node.Dependencies.ToDictionary(
            dependency => dependency,
            dependency => state.Nodes[dependency].OutputJson,
            StringComparer.Ordinal));
        try
        {
            var result = await node.Handler(
                new GameWorkflowNodeContext(
                    run,
                    instanceId,
                    node.NodeId,
                    state.Nodes[node.NodeId].OutputJson,
                    dependencies),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Workflow node '{node.NodeId}' returned null.");
            return new NodeOutcome(node, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;
            return new NodeOutcome(
                node,
                GameWorkflowNodeResult.Fail(
                    state.Nodes[node.NodeId].OutputJson,
                    BoundError(error)));
        }
    }

    private async ValueTask<GameWorkflowCheckpoint> SaveAsync(
        GameWorkflowCheckpoint current,
        GraphState state,
        bool completed,
        string? error,
        GameWorkflowInvocationResult invocation,
        CancellationToken cancellationToken)
    {
        var stateJson = Serialize(state);
        var completedNodes = state.Nodes.Values.Count(node => node.Status == NodeState.Completed);
        var next = new GameWorkflowCheckpoint(
            current.InstanceId,
            Name,
            checked(current.Revision + 1),
            completedNodes,
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

        if (!Equivalent(save.Current, next))
        {
            throw new InvalidOperationException("The workflow checkpoint store returned a different saved checkpoint.");
        }

        return save.Current;
    }

    private void ValidateCheckpoint(GameWorkflowCheckpoint checkpoint, string instanceId)
    {
        if (!string.Equals(checkpoint.InstanceId, instanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The workflow checkpoint store returned a different workflow instance.");
        }

        if (!string.Equals(checkpoint.Workflow, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The workflow checkpoint belongs to a different workflow.");
        }

        if (checkpoint.NextStep < 0 || checkpoint.NextStep > _nodes.Count)
        {
            throw new InvalidOperationException("The workflow checkpoint contains an invalid completed-node count.");
        }
    }

    private void ValidateState(GraphState state, GameWorkflowCheckpoint checkpoint)
    {
        if (!string.Equals(state.Definition, _definition, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The workflow graph definition changed after this instance was checkpointed.");
        }

        if (state.Nodes is null
            || state.Nodes.Count != _nodes.Count
            || state.Nodes.Keys.Any(id => !_nodesById.ContainsKey(id)))
        {
            throw new InvalidOperationException("The workflow graph checkpoint contains an invalid node set.");
        }

        foreach (var pair in state.Nodes)
        {
            if (pair.Value is null
                || !Enum.IsDefined(typeof(NodeState), pair.Value.Status)
                || pair.Value.OutputJson is null
                || pair.Value.Error is { Length: > 65_536 }
                || pair.Value.Status == NodeState.Failed
                || (pair.Value.Status != NodeState.Failed && pair.Value.Error is not null))
            {
                throw new InvalidOperationException("The workflow graph checkpoint contains invalid node state.");
            }

            _ = GameWorkflowNodeResult.Complete(pair.Value.OutputJson);
            if (pair.Value.Status == NodeState.Completed
                && _nodesById[pair.Key].Dependencies.Any(dependency =>
                    state.Nodes[dependency].Status != NodeState.Completed))
            {
                throw new InvalidOperationException(
                    "The workflow graph checkpoint completed a node before its dependencies.");
            }
        }

        var completed = state.Nodes.Values.Count(node => node.Status == NodeState.Completed);
        if (completed != checkpoint.NextStep)
        {
            throw new InvalidOperationException("The workflow graph checkpoint completed-node count is inconsistent.");
        }

    }

    private GraphState CreateInitialState() => new()
    {
        Definition = _definition,
        Nodes = _nodes.ToDictionary(
            node => node.NodeId,
            _ => new NodeStateDocument(),
            StringComparer.Ordinal),
    };

    private string Serialize(GraphState state)
    {
        var json = JsonSerializer.Serialize(state);
        if (json.Length > _maximumStateCharacters)
        {
            throw new InvalidOperationException("The workflow graph state exceeded its configured size limit.");
        }

        return json;
    }

    private GraphState Deserialize(string json)
    {
        if (json.Length > _maximumStateCharacters)
        {
            throw new InvalidOperationException("The workflow graph checkpoint exceeded its configured size limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
            EnsureUnambiguous(document.RootElement);
            return JsonSerializer.Deserialize<GraphState>(
                       document.RootElement.GetRawText(),
                       StateSerializerOptions)
                   ?? throw new InvalidOperationException("The workflow graph checkpoint is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The workflow graph checkpoint is invalid.", exception);
        }
    }

    private static void EnsureAcyclic(IReadOnlyList<GameWorkflowNode> nodes)
    {
        var remaining = nodes.ToDictionary(node => node.NodeId, node => node.Dependencies.Count, StringComparer.Ordinal);
        var dependents = nodes.SelectMany(node => node.Dependencies.Select(dependency => (dependency, node.NodeId)))
            .GroupBy(value => value.dependency, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(value => value.NodeId).ToArray(), StringComparer.Ordinal);
        var ready = new Queue<string>(remaining.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            visited++;
            if (!dependents.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var dependent in next)
            {
                remaining[dependent]--;
                if (remaining[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }

        if (visited != nodes.Count)
        {
            throw new ArgumentException("The workflow graph cannot contain dependency cycles.", nameof(nodes));
        }
    }

    private static string ComputeDefinition(IReadOnlyList<GameWorkflowNode> nodes)
    {
        var canonical = JsonSerializer.Serialize(nodes.Select(node => new
        {
            node.NodeId,
            node.Dependencies,
        }));
        using var hash = SHA256.Create();
        return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(canonical))
            .Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string BoundError(string value) =>
        value.Length <= 65_536 ? value : value.Substring(0, 65_536);

    private static GameWorkflowInvocationResult CompleteInvocation(
        string inputId,
        IReadOnlyList<AgentMessage> messages,
        bool succeeded,
        string? error) =>
        new(inputId, messages, complete: true, succeeded, error);

    private static void EnsureUnambiguous(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException("The workflow graph checkpoint contains duplicate JSON properties.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }

    private static bool Equivalent(GameWorkflowCheckpoint left, GameWorkflowCheckpoint right) =>
        string.Equals(left.InstanceId, right.InstanceId, StringComparison.Ordinal)
        && string.Equals(left.Workflow, right.Workflow, StringComparison.Ordinal)
        && left.Revision == right.Revision
        && left.NextStep == right.NextStep
        && string.Equals(left.StateJson, right.StateJson, StringComparison.Ordinal)
        && left.Completed == right.Completed
        && string.Equals(left.Error, right.Error, StringComparison.Ordinal)
        && GameAgentValueComparer.WorkflowInvocationEquals(left.Invocation, right.Invocation);

    private enum NodeState
    {
        Pending,
        Completed,
        Failed,
    }

    private sealed class GraphState
    {
        public string Definition { get; set; } = string.Empty;

        public Dictionary<string, NodeStateDocument> Nodes { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class NodeStateDocument
    {
        public NodeState Status { get; set; }

        public string OutputJson { get; set; } = "{}";

        public string? Error { get; set; }
    }

    private sealed class NodeOutcome
    {
        public NodeOutcome(GameWorkflowNode node, GameWorkflowNodeResult result)
        {
            Node = node;
            Result = result;
        }

        public GameWorkflowNode Node { get; }

        public GameWorkflowNodeResult Result { get; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

public interface IGameContextProvider
{
    ValueTask<IReadOnlyList<GameContextSlice>> GetContextAsync(
        GameInput input,
        CancellationToken cancellationToken);
}

public delegate ValueTask<IReadOnlyList<AgentTool>> GameToolProvider(
    GameInput input,
    CancellationToken cancellationToken);

public delegate ValueTask<bool> GamePendingWorkProvider(
    GameInput input,
    CancellationToken cancellationToken);

public delegate ValueTask GameAgentEventHandler(
    GameInput input,
    AgentEvent agentEvent,
    CancellationToken cancellationToken);

public enum GameAgentRunStatus
{
    Completed,
    Failed,
    Duplicate,
    SessionConflict,
    WorkflowNotFound,
}

public sealed class GameAgentRunResult
{
    public GameAgentRunResult(
        GameAgentRunStatus status,
        GameRouteDecision route,
        long sessionRevision,
        AgentRunResult? agentResult = null,
        string? error = null)
    {
        if (!Enum.IsDefined(typeof(GameAgentRunStatus), status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (sessionRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionRevision));
        }

        Status = status;
        Route = route ?? throw new ArgumentNullException(nameof(route));
        SessionRevision = sessionRevision;
        AgentResult = agentResult;
        Error = error;
    }

    public GameAgentRunStatus Status { get; }

    public GameRouteDecision Route { get; }

    public long SessionRevision { get; }

    public AgentRunResult? AgentResult { get; }

    public string? Error { get; }

    public bool Succeeded => Status == GameAgentRunStatus.Completed;
}

public sealed class GameWorkflowContext
{
    private readonly Action<IReadOnlyList<AgentMessage>> _validateOutput;

    public GameWorkflowContext(
        GameInput input,
        IReadOnlyList<GameContextSlice> context,
        IReadOnlyList<AgentTool> tools,
        GameSessionSnapshot session)
        : this(input, context, tools, session, ValidateNonNullOutput)
    {
    }

    internal GameWorkflowContext(
        GameInput input,
        IReadOnlyList<GameContextSlice> context,
        IReadOnlyList<AgentTool> tools,
        GameSessionSnapshot session,
        Action<IReadOnlyList<AgentMessage>> validateOutput)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        var copiedContext = (context ?? throw new ArgumentNullException(nameof(context))).ToArray();
        var copiedTools = (tools ?? throw new ArgumentNullException(nameof(tools))).ToArray();
        if (copiedContext.Any(slice => slice is null) || copiedTools.Any(tool => tool is null))
        {
            throw new ArgumentException("Workflow context collections cannot contain null values.");
        }

        Context = Array.AsReadOnly(copiedContext);
        Tools = Array.AsReadOnly(copiedTools);
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _validateOutput = validateOutput ?? throw new ArgumentNullException(nameof(validateOutput));
    }

    public GameInput Input { get; }

    public IReadOnlyList<GameContextSlice> Context { get; }

    public IReadOnlyList<AgentTool> Tools { get; }

    public GameSessionSnapshot Session { get; }

    internal void ValidateOutput(IReadOnlyList<AgentMessage> messages) => _validateOutput(messages);

    private static void ValidateNonNullOutput(IReadOnlyList<AgentMessage> messages)
    {
        if (messages is null || messages.Any(message => message is null))
        {
            throw new ArgumentException("Workflow output cannot contain null messages.", nameof(messages));
        }
    }
}

public sealed class GameWorkflowResult
{
    public GameWorkflowResult(IReadOnlyList<AgentMessage> messages, bool succeeded, string? error = null)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (messages.Any(message => message is null))
        {
            throw new ArgumentException("Workflow output cannot contain null messages.", nameof(messages));
        }

        if (succeeded && error is not null)
        {
            throw new ArgumentException("A successful workflow result cannot carry an error.", nameof(error));
        }

        if (!succeeded && string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("A failed workflow result requires an error.", nameof(error));
        }

        Messages = Array.AsReadOnly(messages.ToArray());
        Succeeded = succeeded;
        Error = error;
    }

    public IReadOnlyList<AgentMessage> Messages { get; }

    public bool Succeeded { get; }

    public string? Error { get; }
}

public interface IGameWorkflow
{
    string Name { get; }

    ValueTask<GameWorkflowResult> RunAsync(GameWorkflowContext context, CancellationToken cancellationToken);
}

public sealed class GameAgentRuntimeOptions
{
    public GameAgentRuntimeOptions(IModelProvider provider, string model)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Model = GameJson.RequireId(model, nameof(model));
    }

    public IModelProvider Provider { get; }

    public string Model { get; set; }

    public string Instructions { get; set; } = string.Empty;

    public IGameRoutePolicy RoutePolicy { get; set; } = new AutomaticGameRoutePolicy();

    public IGameSessionStore SessionStore { get; set; } = new InMemoryGameSessionStore();

    public IGameContextProvider? ContextProvider { get; set; }

    public IGameSkillSource? SkillSource { get; set; }

    public GameToolProvider? ToolProvider { get; set; }

    public GamePendingWorkProvider? PendingWorkProvider { get; set; }

    public IList<IGameWorkflow> Workflows { get; } = new List<IGameWorkflow>();

    public GameRuntimeLimits Limits { get; set; } = new();

    public AgentLimits AgentLimits { get; set; } = new();

    public ModelParameters ModelParameters { get; set; } = new();

    public IGameTranscriptCompactor? TranscriptCompactor { get; set; }

    public AgentHooks AgentHooks { get; set; } = new();

    public bool RefreshContextAfterToolTurns { get; set; } = true;

    public ToolExecutionMode ToolExecution { get; set; } = ToolExecutionMode.SafeParallel;

    public int RecentProcessedInputCapacity { get; set; } = 256;
}

public sealed class GameAgentRuntime
{
    private readonly object _activeAgentsGate = new();
    private readonly Dictionary<GameSessionKey, Agent> _activeAgents = new();
    private readonly IModelProvider _provider;
    private readonly string _model;
    private readonly string _instructions;
    private readonly IGameRoutePolicy _routePolicy;
    private readonly IGameSessionStore _sessionStore;
    private readonly IGameContextProvider? _contextProvider;
    private readonly IGameSkillSource? _skillSource;
    private readonly GameToolProvider? _toolProvider;
    private readonly GamePendingWorkProvider? _pendingWorkProvider;
    private readonly IReadOnlyDictionary<string, IGameWorkflow> _workflows;
    private readonly GameRuntimeLimits _limits;
    private readonly AgentLimits _agentLimits;
    private readonly ModelParameters _modelParameters;
    private readonly IGameTranscriptCompactor? _transcriptCompactor;
    private readonly AgentHooks _agentHooks;
    private readonly bool _refreshContextAfterToolTurns;
    private readonly ToolExecutionMode _toolExecution;
    private readonly MultiActorScheduler _actors;
    private readonly int _recentProcessedInputCapacity;

    public GameAgentRuntime(GameAgentRuntimeOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _provider = options.Provider;
        _model = GameJson.RequireId(options.Model, nameof(options.Model));
        _instructions = options.Instructions ?? throw new ArgumentNullException(nameof(options.Instructions));
        _routePolicy = options.RoutePolicy ?? throw new ArgumentNullException(nameof(options.RoutePolicy));
        _sessionStore = options.SessionStore ?? throw new ArgumentNullException(nameof(options.SessionStore));
        _contextProvider = options.ContextProvider;
        _skillSource = options.SkillSource;
        _toolProvider = options.ToolProvider;
        _pendingWorkProvider = options.PendingWorkProvider;
        _limits = options.Limits?.CopyAndValidate() ?? throw new ArgumentNullException(nameof(options.Limits));
        _agentLimits = CopyAgentLimits(options.AgentLimits ?? throw new ArgumentNullException(nameof(options.AgentLimits)));
        _modelParameters = options.ModelParameters?.Clone() ?? throw new ArgumentNullException(nameof(options.ModelParameters));
        _transcriptCompactor = options.TranscriptCompactor;
        _agentHooks = CopyHooks(options.AgentHooks ?? throw new ArgumentNullException(nameof(options.AgentHooks)));
        _refreshContextAfterToolTurns = options.RefreshContextAfterToolTurns;
        if (!Enum.IsDefined(typeof(ToolExecutionMode), options.ToolExecution))
        {
            throw new ArgumentOutOfRangeException(nameof(options.ToolExecution));
        }

        _toolExecution = options.ToolExecution;
        if (options.RecentProcessedInputCapacity <= 0 || options.RecentProcessedInputCapacity > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RecentProcessedInputCapacity));
        }

        _recentProcessedInputCapacity = options.RecentProcessedInputCapacity;
        _actors = new MultiActorScheduler(
            _limits.MaxConcurrentActors,
            maximumActors: checked(_limits.MaxConcurrentActors * 16),
            _limits.MaxQueuedInputsPerActor);

        var workflows = options.Workflows.ToArray();
        if (workflows.Any(workflow => workflow is null || string.IsNullOrWhiteSpace(workflow.Name)))
        {
            throw new ArgumentException("Every workflow must have a name.", nameof(options.Workflows));
        }

        var duplicate = workflows.GroupBy(workflow => workflow.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate workflow name '{duplicate.Key}'.", nameof(options.Workflows));
        }

        _workflows = workflows.ToDictionary(workflow => workflow.Name, StringComparer.Ordinal);
    }

    public Task<GameAgentRunResult> RunAsync(GameInput input, CancellationToken cancellationToken = default)
        => RunAsync(input, observer: null, cancellationToken);

    public Task<GameAgentRunResult> RunAsync(
        GameInput input,
        GameAgentEventHandler? observer,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        _limits.Validate(input);
        return _actors.EnqueueAsync(
            GameJson.JoinIds(input.SessionId, input.ActorId),
            token => RunCoreAsync(input, observer, token),
            cancellationToken);
    }

    /// <summary>
    /// Queues a bounded message for an actor that is currently running.
    /// Returns false when that actor has no active model/tool loop.
    /// </summary>
    public bool TrySteer(GameSessionKey key, AgentMessage message)
    {
        key.EnsureValid(nameof(key));
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        Agent? agent;
        lock (_activeAgentsGate)
        {
            _activeAgents.TryGetValue(key, out agent);
        }

        if (agent is null)
        {
            return false;
        }

        return agent.TrySteer(message);
    }

    /// <summary>
    /// Requests cancellation for an actor that is currently running.
    /// Returns false when the actor is idle.
    /// </summary>
    public bool TryAbort(GameSessionKey key)
    {
        key.EnsureValid(nameof(key));
        Agent? agent;
        lock (_activeAgentsGate)
        {
            _activeAgents.TryGetValue(key, out agent);
        }

        if (agent is null)
        {
            return false;
        }

        return agent.TryAbort();
    }

    private async ValueTask<GameAgentRunResult> RunCoreAsync(
        GameInput input,
        GameAgentEventHandler? observer,
        CancellationToken cancellationToken)
    {
        var key = new GameSessionKey(input.SessionId, input.ActorId);
        var loaded = await _sessionStore.LoadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? new GameSessionSnapshot(key, 0);
        if (!loaded.Key.Equals(key))
        {
            throw new InvalidOperationException("The game session store returned a snapshot for a different session key.");
        }

        if (loaded.ProcessedInputIds.Count > _recentProcessedInputCapacity)
        {
            throw new InvalidOperationException("The game session store returned more processed input IDs than the configured retention capacity.");
        }

        AgentValidation.ValidateTranscript(loaded.Messages, _agentLimits);
        if (loaded.ProcessedInputIds.Contains(input.InputId, StringComparer.Ordinal))
        {
            return new GameAgentRunResult(
                GameAgentRunStatus.Duplicate,
                GameRouteDecision.Quick("duplicate-input"),
                loaded.Revision);
        }

        var context = _contextProvider is null
            ? Array.Empty<GameContextSlice>()
            : (await _contextProvider.GetContextAsync(input, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game context provider returned null.")).ToArray();
        _limits.Validate(context);

        var tools = _toolProvider is null
            ? Array.Empty<AgentTool>()
            : (await _toolProvider(input, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game tool provider returned null.")).ToArray();
        if (tools.Any(tool => tool is null))
        {
            throw new InvalidOperationException("The game tool provider returned a null tool.");
        }

        var hasPendingWork = _pendingWorkProvider is not null
            && await _pendingWorkProvider(input, cancellationToken).ConfigureAwait(false);
        var route = await _routePolicy.RouteAsync(
            new GameRouteContext(input, tools.Length, hasPendingWork),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The game route policy returned null.");

        if (route.Route == GameRouteKind.Workflow)
        {
            return await RunWorkflowAsync(input, loaded, context, tools, route, cancellationToken).ConfigureAwait(false);
        }

        var activeTools = route.Route == GameRouteKind.QuickResponse ? Array.Empty<AgentTool>() : tools;
        var skills = _skillSource is null
            ? Array.Empty<GameSkill>()
            : (await _skillSource.SelectAsync(
                new GameSkillQuery(input, activeTools.Select(tool => tool.Definition.Name).ToArray(), _limits.MaxSkillsPerRun),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game skill source returned null.")).ToArray();
        _limits.Validate(skills);

        var agentLimits = CopyAgentLimits(_agentLimits);
        IReadOnlyList<AgentMessage> initialMessages = loaded.Messages;
        var minimumMessageReserve = 2;
        var preferredMessageReserve = activeTools.Length == 0
            ? minimumMessageReserve
            : checked(agentLimits.MaxToolCallsPerTurn + 3);
        if (initialMessages.Count + preferredMessageReserve > agentLimits.MaxMessages
            && _transcriptCompactor is not null)
        {
            var target = Math.Max(1, agentLimits.MaxMessages - preferredMessageReserve);
            initialMessages = await _transcriptCompactor.CompactAsync(
                new GameTranscriptCompactionContext(loaded.Key, initialMessages, target),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The transcript compactor returned null.");
            if (initialMessages.Count > target)
            {
                throw new InvalidOperationException("The transcript compactor exceeded its requested target.");
            }
        }

        if (initialMessages.Count + minimumMessageReserve > agentLimits.MaxMessages)
        {
            return new GameAgentRunResult(
                GameAgentRunStatus.Failed,
                route,
                loaded.Revision,
                error: "The session transcript cannot reserve space for the next input and model response.");
        }

        var options = new AgentOptions(_provider, _model)
        {
            SystemPrompt = ComposeSystemPrompt(context, skills),
            SessionId = input.SessionId,
            Limits = agentLimits,
            Parameters = _modelParameters.Clone(),
            Hooks = CreateRunHooks(route.Route, input),
            ToolExecution = _toolExecution,
        };
        foreach (var message in initialMessages)
        {
            options.InitialMessages.Add(message);
        }

        foreach (var tool in activeTools)
        {
            options.Tools.Add(tool);
        }

        var agent = new Agent(options);
        using var subscription = observer is null
            ? null
            : agent.Subscribe((agentEvent, token) => observer(input, agentEvent, token));
        RegisterActiveAgent(key, agent);
        AgentRunResult run;
        try
        {
            run = await agent.RunAsync(CreateInputMessage(input), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UnregisterActiveAgent(key, agent);
        }

        var save = await SaveAsync(input, loaded, agent.State.Messages, cancellationToken).ConfigureAwait(false);
        if (!save.Saved)
        {
            return new GameAgentRunResult(
                GameAgentRunStatus.SessionConflict,
                route,
                save.Current.Revision,
                run,
                "The session changed while this input was running. Committed game actions must be reconciled before retrying.");
        }

        return new GameAgentRunResult(
            run.Succeeded ? GameAgentRunStatus.Completed : GameAgentRunStatus.Failed,
            route,
            save.Current.Revision,
            run,
            run.Error);
    }

    private void RegisterActiveAgent(GameSessionKey key, Agent agent)
    {
        lock (_activeAgentsGate)
        {
            if (_activeAgents.ContainsKey(key))
            {
                throw new InvalidOperationException("The actor already has an active agent run.");
            }

            _activeAgents.Add(key, agent);
        }
    }

    private void UnregisterActiveAgent(GameSessionKey key, Agent agent)
    {
        lock (_activeAgentsGate)
        {
            if (_activeAgents.TryGetValue(key, out var current) && ReferenceEquals(current, agent))
            {
                _activeAgents.Remove(key);
            }
        }
    }

    private async ValueTask<GameAgentRunResult> RunWorkflowAsync(
        GameInput input,
        GameSessionSnapshot loaded,
        IReadOnlyList<GameContextSlice> context,
        IReadOnlyList<AgentTool> tools,
        GameRouteDecision route,
        CancellationToken cancellationToken)
    {
        if (route.Workflow is null || !_workflows.TryGetValue(route.Workflow, out var workflow))
        {
            return new GameAgentRunResult(
                GameAgentRunStatus.WorkflowNotFound,
                route,
                loaded.Revision,
                error: $"Workflow '{route.Workflow}' is not registered.");
        }

        void ValidateWorkflowOutput(IReadOnlyList<AgentMessage> output)
        {
            var candidate = loaded.Messages
                .Concat(new[] { CreateInputMessage(input) })
                .Concat(output ?? throw new ArgumentNullException(nameof(output)))
                .ToArray();
            AgentValidation.ValidateTranscript(candidate, _agentLimits);
        }

        var workflowContext = new GameWorkflowContext(
            input,
            context,
            tools,
            loaded,
            ValidateWorkflowOutput);
        var result = await workflow.RunAsync(
            workflowContext,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow '{workflow.Name}' returned null.");
        workflowContext.ValidateOutput(result.Messages);
        var messages = loaded.Messages.Concat(new[] { CreateInputMessage(input) }).Concat(result.Messages).ToArray();
        var save = await SaveAsync(input, loaded, messages, cancellationToken).ConfigureAwait(false);
        return !save.Saved
            ? new GameAgentRunResult(GameAgentRunStatus.SessionConflict, route, save.Current.Revision, error: "The session changed while the workflow was running.")
            : new GameAgentRunResult(
                result.Succeeded ? GameAgentRunStatus.Completed : GameAgentRunStatus.Failed,
                route,
                save.Current.Revision,
                error: result.Error);
    }

    private async ValueTask<GameSessionSaveResult> SaveAsync(
        GameInput input,
        GameSessionSnapshot loaded,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken)
    {
        var processed = loaded.ProcessedInputIds
            .Where(id => !string.Equals(id, input.InputId, StringComparison.Ordinal))
            .Concat(new[] { input.InputId })
            .TakeLast(_recentProcessedInputCapacity)
            .ToArray();
        var snapshot = new GameSessionSnapshot(
            loaded.Key,
            checked(loaded.Revision + 1),
            messages,
            processed,
            input.Moment);
        var save = await _sessionStore.SaveAsync(snapshot, loaded.Revision, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The game session store returned null.");
        if (!save.Current.Key.Equals(loaded.Key))
        {
            throw new InvalidOperationException("The game session store returned a result for a different session key.");
        }

        if (save.Saved && !SessionSnapshotEquals(save.Current, snapshot))
        {
            throw new InvalidOperationException("The game session store returned a different saved snapshot.");
        }

        if (!save.Saved && save.Current.Revision <= loaded.Revision)
        {
            throw new InvalidOperationException("The game session store reported a conflict without a newer revision.");
        }

        return save;
    }

    private static bool SessionSnapshotEquals(GameSessionSnapshot left, GameSessionSnapshot right)
    {
        if (!left.Key.Equals(right.Key)
            || left.Revision != right.Revision
            || left.LastMoment != right.LastMoment
            || !left.ProcessedInputIds.SequenceEqual(right.ProcessedInputIds, StringComparer.Ordinal)
            || left.Messages.Count != right.Messages.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Messages.Count; index++)
        {
            if (!MessageEquals(left.Messages[index], right.Messages[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MessageEquals(AgentMessage left, AgentMessage right)
    {
        if (left.Role != right.Role
            || left.Timestamp != right.Timestamp
            || !string.Equals(left.CustomRole, right.CustomRole, StringComparison.Ordinal)
            || !string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal)
            || !string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
            || left.IsError != right.IsError
            || !string.Equals(left.DetailsJson, right.DetailsJson, StringComparison.Ordinal)
            || !string.Equals(left.Model, right.Model, StringComparison.Ordinal)
            || left.StopReason != right.StopReason
            || !string.Equals(left.ErrorMessage, right.ErrorMessage, StringComparison.Ordinal)
            || !UsageEquals(left.Usage, right.Usage)
            || left.Metadata.Count != right.Metadata.Count
            || left.Content.Count != right.Content.Count)
        {
            return false;
        }

        foreach (var pair in left.Metadata)
        {
            if (!right.Metadata.TryGetValue(pair.Key, out var value)
                || !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        for (var index = 0; index < left.Content.Count; index++)
        {
            if (!ContentEquals(left.Content[index], right.Content[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool UsageEquals(ModelUsage? left, ModelUsage? right) =>
        left is null
            ? right is null
            : right is not null
                && left.InputTokens == right.InputTokens
                && left.OutputTokens == right.OutputTokens
                && left.CacheReadTokens == right.CacheReadTokens
                && left.CacheWriteTokens == right.CacheWriteTokens;

    private static bool ContentEquals(AgentContent left, AgentContent right) => (left, right) switch
    {
        (TextContent first, TextContent second) =>
            string.Equals(first.Text, second.Text, StringComparison.Ordinal),
        (JsonContent first, JsonContent second) =>
            string.Equals(first.Json, second.Json, StringComparison.Ordinal),
        (ReasoningContent first, ReasoningContent second) =>
            string.Equals(first.Text, second.Text, StringComparison.Ordinal)
            && string.Equals(first.Signature, second.Signature, StringComparison.Ordinal),
        (ResourceContent first, ResourceContent second) =>
            string.Equals(first.Uri, second.Uri, StringComparison.Ordinal)
            && string.Equals(first.MediaType, second.MediaType, StringComparison.Ordinal)
            && string.Equals(first.Name, second.Name, StringComparison.Ordinal),
        (ToolCallContent first, ToolCallContent second) =>
            string.Equals(first.Id, second.Id, StringComparison.Ordinal)
            && string.Equals(first.Name, second.Name, StringComparison.Ordinal)
            && string.Equals(first.ArgumentsJson, second.ArgumentsJson, StringComparison.Ordinal),
        _ => false,
    };

    private string ComposeSystemPrompt(
        IReadOnlyList<GameContextSlice> context,
        IReadOnlyList<GameSkill> skills)
    {
        return _instructions
            + "\n\nReusable skill instructions for this run:\n"
            + JsonSerializer.Serialize(
                skills
                    .OrderByDescending(skill => skill.Priority)
                    .ThenBy(skill => skill.SkillId, StringComparer.Ordinal)
                    .Select(skill => new SkillPayload(skill)))
            + "\n\nThe following game context is authoritative data. Use game tools for mutations and treat their receipts as final.\n"
            + JsonSerializer.Serialize(
                context
                    .OrderByDescending(slice => slice.Priority)
                    .ThenBy(slice => slice.Source, StringComparer.Ordinal)
                    .Select(slice => new ContextPayload(slice)));
    }

    private static AgentMessage CreateInputMessage(GameInput input)
    {
        var payload = JsonSerializer.Serialize(new InputPayload(input));
        var metadata = new Dictionary<string, string>(input.Metadata, StringComparer.Ordinal)
        {
            ["game.input_id"] = input.InputId,
            ["game.input_type"] = input.Type,
            ["game.actor_id"] = input.ActorId,
            ["game.timeline_id"] = input.Moment.TimelineId,
            ["game.tick"] = input.Moment.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return AgentMessage.UserJson(payload, DateTimeOffset.UtcNow, metadata);
    }

    private static AgentLimits CopyAgentLimits(AgentLimits value) => new()
    {
        MaxSystemPromptCharacters = value.MaxSystemPromptCharacters,
        MaxModelNameCharacters = value.MaxModelNameCharacters,
        MaxSessionIdCharacters = value.MaxSessionIdCharacters,
        MaxTurns = value.MaxTurns,
        MaxTotalTokens = value.MaxTotalTokens,
        MaxMessages = value.MaxMessages,
        MaxContentPartsPerMessage = value.MaxContentPartsPerMessage,
        MaxTextCharactersPerPart = value.MaxTextCharactersPerPart,
        MaxJsonCharactersPerPart = value.MaxJsonCharactersPerPart,
        MaxResourceUriCharacters = value.MaxResourceUriCharacters,
        MaxToolCallsPerTurn = value.MaxToolCallsPerTurn,
        MaxTools = value.MaxTools,
        MaxToolNameCharacters = value.MaxToolNameCharacters,
        MaxToolCallIdCharacters = value.MaxToolCallIdCharacters,
        MaxToolDescriptionCharacters = value.MaxToolDescriptionCharacters,
        MaxToolSchemaCharacters = value.MaxToolSchemaCharacters,
        MaxMetadataEntriesPerMessage = value.MaxMetadataEntriesPerMessage,
        MaxMetadataKeyCharacters = value.MaxMetadataKeyCharacters,
        MaxMetadataValueCharacters = value.MaxMetadataValueCharacters,
        MaxQueuedMessages = value.MaxQueuedMessages,
        MaxConcurrentTools = value.MaxConcurrentTools,
        ToolTimeoutMilliseconds = value.ToolTimeoutMilliseconds,
        MaxProgressEventsPerTool = value.MaxProgressEventsPerTool,
        MaxSubscribers = value.MaxSubscribers,
    };

    private AgentHooks CreateRunHooks(GameRouteKind route, GameInput input)
    {
        var hooks = CopyHooks(_agentHooks);
        if (route == GameRouteKind.QuickResponse)
        {
            var configured = hooks.ShouldStopAfterTurnAsync;
            hooks.ShouldStopAfterTurnAsync = async (context, cancellationToken) =>
            {
                if (configured is not null)
                {
                    _ = await configured(context, cancellationToken).ConfigureAwait(false);
                }

                return true;
            };
        }

        if (route == GameRouteKind.Agent && _refreshContextAfterToolTurns)
        {
            var configured = hooks.PrepareNextTurnAsync;
            hooks.PrepareNextTurnAsync = async (context, cancellationToken) =>
            {
                var update = configured is null
                    ? null
                    : await configured(context, cancellationToken).ConfigureAwait(false);
                if (update?.Context is not null
                    || !context.Response.Content.OfType<ToolCallContent>().Any())
                {
                    return update;
                }

                var refreshed = await RefreshTurnContextAsync(
                    input,
                    context.Context.Messages,
                    cancellationToken).ConfigureAwait(false);
                return new NextTurnUpdate
                {
                    Context = refreshed,
                    Model = update?.Model,
                    Parameters = update?.Parameters,
                };
            };
        }

        return hooks;
    }

    private async ValueTask<AgentContext> RefreshTurnContextAsync(
        GameInput input,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken)
    {
        var context = _contextProvider is null
            ? Array.Empty<GameContextSlice>()
            : (await _contextProvider.GetContextAsync(input, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game context provider returned null.")).ToArray();
        _limits.Validate(context);

        var tools = _toolProvider is null
            ? Array.Empty<AgentTool>()
            : (await _toolProvider(input, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game tool provider returned null.")).ToArray();
        if (tools.Any(tool => tool is null))
        {
            throw new InvalidOperationException("The game tool provider returned a null tool.");
        }

        var skills = _skillSource is null
            ? Array.Empty<GameSkill>()
            : (await _skillSource.SelectAsync(
                new GameSkillQuery(input, tools.Select(tool => tool.Definition.Name).ToArray(), _limits.MaxSkillsPerRun),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game skill source returned null.")).ToArray();
        _limits.Validate(skills);
        return new AgentContext(ComposeSystemPrompt(context, skills), messages, tools);
    }

    private static AgentHooks CopyHooks(AgentHooks value) => new()
    {
        TransformContextAsync = value.TransformContextAsync,
        BeforeModelRequestAsync = value.BeforeModelRequestAsync,
        ShouldStopAfterTurnAsync = value.ShouldStopAfterTurnAsync,
        PrepareNextTurnAsync = value.PrepareNextTurnAsync,
        BeforeToolCallAsync = value.BeforeToolCallAsync,
        AfterToolCallAsync = value.AfterToolCallAsync,
    };

    private sealed class InputPayload
    {
        public InputPayload(GameInput input)
        {
            InputId = input.InputId;
            Type = input.Type;
            ActorId = input.ActorId;
            TimelineId = input.Moment.TimelineId;
            Tick = input.Moment.Tick;
            Calendar = input.Moment.CalendarJson is null
                ? (JsonElement?)null
                : GameJson.ParseElement(input.Moment.CalendarJson);
            Payload = GameJson.ParseElement(input.PayloadJson);
        }

        public string InputId { get; }

        public string Type { get; }

        public string ActorId { get; }

        public string TimelineId { get; }

        public long Tick { get; }

        public JsonElement? Calendar { get; }

        public JsonElement Payload { get; }
    }

    private sealed class ContextPayload
    {
        public ContextPayload(GameContextSlice slice)
        {
            Source = slice.Source;
            Version = slice.Version;
            Data = GameJson.ParseElement(slice.PayloadJson);
        }

        public string Source { get; }

        public string? Version { get; }

        public JsonElement Data { get; }
    }

    private sealed class SkillPayload
    {
        public SkillPayload(GameSkill skill)
        {
            SkillId = skill.SkillId;
            Name = skill.Name;
            Description = skill.Description;
            Instructions = skill.Instructions;
            ToolNames = skill.ToolNames;
        }

        public string SkillId { get; }

        public string Name { get; }

        public string Description { get; }

        public string Instructions { get; }

        public IReadOnlyCollection<string> ToolNames { get; }
    }
}

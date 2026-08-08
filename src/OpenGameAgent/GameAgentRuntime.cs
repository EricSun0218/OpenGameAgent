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

public sealed class GameModelSelection
{
    private readonly ModelParameters? _parameters;

    public GameModelSelection(
        string model,
        string? registeredProviderName = null,
        ModelParameters? parameters = null,
        IModelProvider? provider = null,
        int contextWindowTokens = 0,
        int maximumOutputTokens = 0)
    {
        if (registeredProviderName is not null && provider is not null)
        {
            throw new ArgumentException("A model selection cannot specify both a registered provider name and a direct provider.");
        }

        Model = GameJson.RequireId(model, nameof(model));
        RegisteredProviderName = registeredProviderName is null
            ? null
            : GameJson.RequireId(registeredProviderName, nameof(registeredProviderName));
        if (contextWindowTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextWindowTokens));
        }

        if (maximumOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        }

        if (contextWindowTokens > 0 && maximumOutputTokens >= contextWindowTokens)
        {
            throw new ArgumentException("The model output limit must be smaller than its context window.");
        }

        _parameters = parameters?.Clone();
        Provider = provider;
        ContextWindowTokens = contextWindowTokens;
        MaximumOutputTokens = maximumOutputTokens;
    }

    public string Model { get; }

    public string? RegisteredProviderName { get; }

    public ModelParameters? Parameters => _parameters?.Clone();

    public IModelProvider? Provider { get; }

    public int ContextWindowTokens { get; }

    public int MaximumOutputTokens { get; }
}

public delegate ValueTask<GameModelSelection?> GameModelSelector(
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

    /// <summary>
    /// Validates cumulative workflow output against the active runtime transcript limits.
    /// Long-running workflows should call this before committing a node or step checkpoint.
    /// </summary>
    public void ValidateOutput(IReadOnlyList<AgentMessage> messages) =>
        _validateOutput(messages ?? throw new ArgumentNullException(nameof(messages)));

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

    public GameModelSelector? ModelSelector { get; set; }

    public IList<IGameWorkflow> Workflows { get; } = new List<IGameWorkflow>();

    /// <summary>
    /// Runtime extensions. Extensions are configured in list order and all features register
    /// through the same public extension API.
    /// </summary>
    public IList<IGameAgentExtension> Extensions { get; } = new List<IGameAgentExtension>();

    public GameRuntimeLimits Limits { get; set; } = new();

    public AgentLimits AgentLimits { get; set; } = new();

    public ModelParameters ModelParameters { get; set; } = new();

    public IGameTranscriptCompactor? TranscriptCompactor { get; set; }

    /// <summary>
    /// Model context window used when a selector does not provide model metadata. Zero disables
    /// request-size admission and leaves message-count compaction as the only transcript budget.
    /// </summary>
    public int ContextWindowTokens { get; set; }

    /// <summary>
    /// Tokens reserved for model output when the active model or request does not provide an output limit.
    /// </summary>
    public int ContextWindowReserveTokens { get; set; } = 16_384;

    public GameModelRequestTokenEstimator RequestTokenEstimator { get; set; } =
        ApproximateGameTokenEstimator.EstimateRequest;

    public GameTranscriptTokenEstimator TranscriptTokenEstimator { get; set; } =
        ApproximateGameTokenEstimator.EstimateMessages;

    public AgentHooks AgentHooks { get; set; } = new();

    public bool RefreshContextAfterToolTurns { get; set; } = true;

    public ToolExecutionMode ToolExecution { get; set; } = ToolExecutionMode.SafeParallel;

    public int RecentProcessedInputCapacity { get; set; } = 256;

    /// <summary>
    /// Maximum time allowed to durably settle a completed or aborted agent run after execution has begun.
    /// This commit is intentionally independent from the caller's cancellation token so tool receipts and
    /// terminal transcript state are not lost when cancellation stops model or tool work.
    /// </summary>
    public int SessionCommitTimeoutMilliseconds { get; set; } = 10_000;

    /// <summary>
    /// Persists a canonical checkpoint after each fully settled tool turn. This bounds crash recovery
    /// without writing partial model streams or marking the input complete before the run finishes.
    /// </summary>
    public bool PersistToolTurnCheckpoints { get; set; } = true;
}

public sealed class GameAgentRuntime : IDisposable, IAsyncDisposable
{
    private readonly object _activeAgentsGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
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
    private readonly GameModelSelector? _modelSelector;
    private readonly IReadOnlyDictionary<string, IGameWorkflow> _workflows;
    private readonly GameRuntimeLimits _limits;
    private readonly AgentLimits _agentLimits;
    private readonly ModelParameters _modelParameters;
    private readonly IGameTranscriptCompactor? _transcriptCompactor;
    private readonly int _contextWindowTokens;
    private readonly int _contextWindowReserveTokens;
    private readonly GameModelRequestTokenEstimator _requestTokenEstimator;
    private readonly GameTranscriptTokenEstimator _transcriptTokenEstimator;
    private readonly AgentHooks _agentHooks;
    private readonly bool _refreshContextAfterToolTurns;
    private readonly ToolExecutionMode _toolExecution;
    private readonly MultiActorScheduler _actors;
    private readonly int _recentProcessedInputCapacity;
    private readonly int _sessionCommitTimeoutMilliseconds;
    private readonly bool _persistToolTurnCheckpoints;
    private readonly GameAgentExtensionHost _extensions;
    private int _disposed;

    public GameAgentRuntime(GameAgentRuntimeOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _provider = options.Provider;
        _model = GameJson.RequireId(options.Model, nameof(options.Model));
        _instructions = options.Instructions
            ?? throw new ArgumentException("Runtime instructions are required.", nameof(options));
        _routePolicy = options.RoutePolicy
            ?? throw new ArgumentException("A route policy is required.", nameof(options));
        _sessionStore = options.SessionStore
            ?? throw new ArgumentException("A session store is required.", nameof(options));
        _contextProvider = options.ContextProvider;
        _skillSource = options.SkillSource;
        _toolProvider = options.ToolProvider;
        _pendingWorkProvider = options.PendingWorkProvider;
        _modelSelector = options.ModelSelector;
        _limits = options.Limits?.CopyAndValidate()
            ?? throw new ArgumentException("Runtime limits are required.", nameof(options));
        _agentLimits = CopyAgentLimits(options.AgentLimits
            ?? throw new ArgumentException("Agent limits are required.", nameof(options)));
        _modelParameters = options.ModelParameters?.Clone()
            ?? throw new ArgumentException("Model parameters are required.", nameof(options));
        _transcriptCompactor = options.TranscriptCompactor;
        if (options.ContextWindowTokens < 0 || options.ContextWindowTokens > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The context-window size is invalid.");
        }

        if (options.ContextWindowReserveTokens <= 0
            || options.ContextWindowReserveTokens > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The context-window reserve is invalid.");
        }

        if (options.ContextWindowTokens > 0
            && options.ContextWindowReserveTokens >= options.ContextWindowTokens)
        {
            throw new ArgumentException("The context-window reserve must be smaller than the configured context window.");
        }

        if (options.ContextWindowTokens > 0
            && _modelParameters.MaxOutputTokens is { } configuredOutput
            && configuredOutput >= options.ContextWindowTokens)
        {
            throw new ArgumentException("The configured model output limit must be smaller than the context window.");
        }

        _contextWindowTokens = options.ContextWindowTokens;
        _contextWindowReserveTokens = options.ContextWindowReserveTokens;
        _requestTokenEstimator = options.RequestTokenEstimator
            ?? throw new ArgumentException("A model-request token estimator is required.", nameof(options));
        _transcriptTokenEstimator = options.TranscriptTokenEstimator
            ?? throw new ArgumentException("A transcript token estimator is required.", nameof(options));
        _agentHooks = CopyHooks(options.AgentHooks
            ?? throw new ArgumentException("Agent hooks are required.", nameof(options)));
        _refreshContextAfterToolTurns = options.RefreshContextAfterToolTurns;
        if (!Enum.IsDefined(typeof(ToolExecutionMode), options.ToolExecution))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The tool execution mode is invalid.");
        }

        _toolExecution = options.ToolExecution;
        if (options.RecentProcessedInputCapacity <= 0 || options.RecentProcessedInputCapacity > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The processed-input retention capacity is invalid.");
        }

        _recentProcessedInputCapacity = options.RecentProcessedInputCapacity;
        if (options.SessionCommitTimeoutMilliseconds < 100 || options.SessionCommitTimeoutMilliseconds > 300_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The session commit timeout is invalid.");
        }

        _sessionCommitTimeoutMilliseconds = options.SessionCommitTimeoutMilliseconds;
        _persistToolTurnCheckpoints = options.PersistToolTurnCheckpoints;
        var extensions = new GameAgentExtensionHost(options.Extensions, _limits);
        try
        {
            var workflows = options.Workflows.Concat(extensions.GetWorkflows()).ToArray();
            if (workflows.Any(workflow => workflow is null || string.IsNullOrWhiteSpace(workflow.Name)))
            {
                throw new ArgumentException("Every workflow must have a name.", nameof(options));
            }

            var duplicate = workflows.GroupBy(workflow => workflow.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new ArgumentException($"Duplicate workflow name '{duplicate.Key}'.", nameof(options));
            }

            _workflows = workflows.ToDictionary(workflow => workflow.Name, StringComparer.Ordinal);
            _actors = new MultiActorScheduler(
                _limits.MaxConcurrentActors,
                maximumActors: _limits.MaxScheduledActors,
                _limits.MaxQueuedInputsPerActor);
            _extensions = extensions;
        }
        catch
        {
            try
            {
                GameAgentAsyncBridge.Run(extensions.DisposeAsync);
            }
            catch
            {
                // Preserve the runtime construction failure. Cleanup is best effort here.
            }

            throw;
        }
    }

    public Task<GameAgentRunResult> RunAsync(GameInput input, CancellationToken cancellationToken = default)
        => RunAsync(input, observer: null, cancellationToken);

    public IReadOnlyList<GameAgentExtensionResource> ExtensionResources => _extensions.GetResources();

    public IReadOnlyList<GameAgentExtensionDiagnostic> ExtensionDiagnostics => _extensions.GetDiagnostics();

    public Task<GameAgentRunResult> RunAsync(
        GameInput input,
        GameAgentEventHandler? observer,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(GameAgentRuntime));
        }

        _limits.Validate(input);
        return EnqueueRunAsync(
            input,
            observer,
            cancellationToken);
    }

    private async Task<GameAgentRunResult> EnqueueRunAsync(
        GameInput input,
        GameAgentEventHandler? observer,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        return await _actors.EnqueueAsync(
            GameJson.JoinIds(input.SessionId, input.ActorId),
            token => RunCoreAsync(input, observer, token),
            linkedCancellation.Token).ConfigureAwait(false);
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
        GameAgentExtensionRunContext? failureContext = null;
        try
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

            if (loaded.ProcessedInputIds.Any(id => id.Length > _limits.MaxIdentifierCharacters)
                || (loaded.PendingInputId?.Length ?? 0) > _limits.MaxIdentifierCharacters
                || (loaded.LastMoment?.TimelineId.Length ?? 0) > _limits.MaxIdentifierCharacters
                || (loaded.LastMoment?.CalendarJson?.Length ?? 0) > _limits.MaxCalendarJsonCharacters)
            {
                throw new InvalidOperationException("The game session store returned state that exceeds the configured runtime limits.");
            }

            AgentValidation.ValidateTranscript(loaded.Messages, _agentLimits);
            var extensionState = new GameAgentSessionState(loaded.ExtensionState, _limits);
            var extensionContext = _extensions.CreateRunContext(input, loaded, extensionState);
            failureContext = extensionContext;
            await _extensions.PublishAsync(
                GameAgentExtensionEvents.InputReceived,
                new GameAgentInputEvent(input),
                extensionContext,
                cancellationToken).ConfigureAwait(false);
            await _extensions.PublishAsync(
                GameAgentExtensionEvents.SessionLoaded,
                new GameAgentSessionEvent(loaded),
                extensionContext,
                cancellationToken).ConfigureAwait(false);
            if (loaded.ProcessedInputIds.Contains(input.InputId, StringComparer.Ordinal))
            {
                var duplicate = new GameAgentRunResult(
                    GameAgentRunStatus.Duplicate,
                    GameRouteDecision.Quick("duplicate-input"),
                    loaded.Revision);
                await PublishCompletedAsync(duplicate, extensionContext, cancellationToken).ConfigureAwait(false);
                return duplicate;
            }

            if (loaded.PendingInputId is not null
                && !string.Equals(loaded.PendingInputId, input.InputId, StringComparison.Ordinal))
            {
                var pending = new GameAgentRunResult(
                    GameAgentRunStatus.SessionConflict,
                    GameRouteDecision.Quick("pending-input"),
                    loaded.Revision,
                    error: $"Input '{loaded.PendingInputId}' has a durable tool-turn checkpoint and must be resumed before another input can run.");
                await PublishCompletedAsync(pending, extensionContext, cancellationToken).ConfigureAwait(false);
                return pending;
            }

            var resumingCheckpoint = loaded.PendingInputId is not null;
            if (resumingCheckpoint)
            {
                ValidatePendingInput(loaded, input);
            }

            var baseContext = _contextProvider is null
                ? Array.Empty<GameContextSlice>()
                : (await _contextProvider.GetContextAsync(input, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The game context provider returned null.")).ToArray();
            var context = await _extensions.CollectContextAsync(
                extensionContext,
                baseContext,
                cancellationToken).ConfigureAwait(false);
            _limits.Validate(context);
            await _extensions.PublishAsync(
                GameAgentExtensionEvents.ContextCollected,
                new GameAgentContextEvent(context),
                extensionContext,
                cancellationToken).ConfigureAwait(false);

            var baseTools = _toolProvider is null
                ? Array.Empty<AgentTool>()
                : (await _toolProvider(input, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The game tool provider returned null.")).ToArray();
            if (baseTools.Any(tool => tool is null))
            {
                throw new InvalidOperationException("The game tool provider returned a null tool.");
            }

            var tools = await _extensions.CollectToolsAsync(
                extensionContext,
                baseTools,
                cancellationToken).ConfigureAwait(false);
            await _extensions.PublishAsync(
                GameAgentExtensionEvents.ToolsCollected,
                new GameAgentToolsEvent(tools),
                extensionContext,
                cancellationToken).ConfigureAwait(false);

            var basePendingWork = _pendingWorkProvider is not null
                && await _pendingWorkProvider(input, cancellationToken).ConfigureAwait(false);
            var hasPendingWork = await _extensions.HasPendingWorkAsync(
                extensionContext,
                basePendingWork,
                cancellationToken).ConfigureAwait(false);
            var route = resumingCheckpoint
                ? GameRouteDecision.Agent("durable-tool-checkpoint")
                : await _extensions.SelectRouteAsync(
                        extensionContext,
                        tools.Count,
                        hasPendingWork,
                        cancellationToken).ConfigureAwait(false)
                    ?? await _routePolicy.RouteAsync(
                        new GameRouteContext(input, tools.Count, hasPendingWork),
                        cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The game route policy returned null.");
            var routeEvent = new GameAgentRouteEvent(route);
            await _extensions.PublishAsync(
                GameAgentExtensionEvents.RouteSelected,
                routeEvent,
                extensionContext,
                cancellationToken).ConfigureAwait(false);
            route = routeEvent.Decision ?? throw new InvalidOperationException("An extension cleared the route decision.");
            if (resumingCheckpoint && route.Route != GameRouteKind.Agent)
            {
                throw new InvalidOperationException("A durable tool-turn checkpoint must resume through the agent route.");
            }

            if (route.Route == GameRouteKind.Workflow)
            {
                var workflowRun = await RunWorkflowAsync(
                    input,
                    loaded,
                    context,
                    tools,
                    route,
                    extensionState,
                    extensionContext,
                    cancellationToken).ConfigureAwait(false);
                await PublishCompletedAsync(workflowRun, extensionContext, CancellationToken.None).ConfigureAwait(false);
                return workflowRun;
            }

            var activeTools = route.Route == GameRouteKind.QuickResponse
                ? (IReadOnlyList<AgentTool>)Array.Empty<AgentTool>()
                : tools;
            var baseSkills = _skillSource is null
                ? Array.Empty<GameSkill>()
                : (await _skillSource.SelectAsync(
                    new GameSkillQuery(input, activeTools.Select(tool => tool.Definition.Name).ToArray(), _limits.MaxSkillsPerRun),
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The game skill source returned null.")).ToArray();
            var skills = await _extensions.CollectSkillsAsync(
                extensionContext,
                baseSkills,
                activeTools.Select(tool => tool.Definition.Name).ToArray(),
                _limits.MaxSkillsPerRun,
                cancellationToken).ConfigureAwait(false);
            _limits.Validate(skills);
            await _extensions.PublishAsync(
                GameAgentExtensionEvents.SkillsSelected,
                new GameAgentSkillsEvent(skills),
                extensionContext,
                cancellationToken).ConfigureAwait(false);

            var selection = _modelSelector is null
                ? null
                : await _modelSelector(input, cancellationToken).ConfigureAwait(false);
            var provider = selection?.Provider
                ?? _extensions.ResolveModelProvider(selection?.RegisteredProviderName, _provider);
            var model = selection?.Model ?? _model;
            var parameters = selection?.Parameters?.Clone() ?? _modelParameters.Clone();
            var contextWindowTokens = selection?.ContextWindowTokens > 0
                ? selection.ContextWindowTokens
                : _contextWindowTokens;
            var maximumOutputTokens = selection?.MaximumOutputTokens ?? 0;
            var systemPrompt = ComposeSystemPrompt(context, skills);
            var agentLimits = CopyAgentLimits(_agentLimits);
            var usageAccounting = new RunUsageAccounting(input.InputId, agentLimits.MaxTotalTokens);
            var legacyUsageRecords = CreateLegacyUsageRecords(loaded);
            var baseUsageLedger = loaded.UsageLedger.Append(legacyUsageRecords);
            IReadOnlyList<AgentMessage> initialMessages = loaded.Messages;
            var minimumMessageReserve = resumingCheckpoint ? 1 : 2;
            var preferredMessageReserve = activeTools.Count == 0
                ? minimumMessageReserve
                : checked(agentLimits.MaxToolCallsPerTurn + (resumingCheckpoint ? 2 : 3));
            var additionalMessages = resumingCheckpoint
                ? Array.Empty<AgentMessage>()
                : new[] { CreateInputMessage(input) };
            try
            {
                initialMessages = await FitTranscriptAsync(
                    loaded.Key,
                    initialMessages,
                    Math.Max(1, agentLimits.MaxMessages - preferredMessageReserve),
                    additionalMessages,
                    model,
                    systemPrompt,
                    activeTools.Select(tool => tool.Definition).ToArray(),
                    parameters,
                    contextWindowTokens,
                    maximumOutputTokens,
                    usageAccounting,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GameTranscriptCompactionException exception)
            {
                var usageRecords = usageAccounting.RecordsBetween(0, usageAccounting.Count);
                GameSessionSnapshot settled;
                using (var settlementCancellation = new CancellationTokenSource(_sessionCommitTimeoutMilliseconds))
                {
                    settled = await SaveUsageOnlyAsync(
                        loaded,
                        legacyUsageRecords.Concat(usageRecords).ToArray(),
                        baseUsageLedger.Append(usageRecords),
                        settlementCancellation.Token).ConfigureAwait(false);
                }

                var failed = new GameAgentRunResult(
                    GameAgentRunStatus.Failed,
                    route,
                    settled.Revision,
                    error: exception.Message);
                await PublishCompletedAsync(failed, extensionContext, CancellationToken.None).ConfigureAwait(false);
                return failed;
            }

            if (resumingCheckpoint)
            {
                ValidatePendingInput(loaded, input, initialMessages);
            }

            if (initialMessages.Count + minimumMessageReserve > agentLimits.MaxMessages)
            {
                var exhausted = new GameAgentRunResult(
                    GameAgentRunStatus.Failed,
                    route,
                    loaded.Revision,
                    error: "The session transcript cannot reserve space for the next input and model response.");
                await PublishCompletedAsync(exhausted, extensionContext, cancellationToken).ConfigureAwait(false);
                return exhausted;
            }

            var commitBase = loaded;
            var committedUsageRecordCount = 0;
            GameSessionSaveResult? checkpointConflict = null;
            IReadOnlyList<GameSessionUsageRecord>? checkpointConflictUsageRecords = null;
            GameSessionUsageLedger? checkpointConflictUsageLedger = null;
            var recoverySafety = new GameModelRecoverySafety(resumingCheckpoint);
            Func<IModelProvider, IModelProvider>? wrapRecoveryProvider = null;
            if (_transcriptCompactor is not null && contextWindowTokens > 0)
            {
                wrapRecoveryProvider = candidate => new ContextOverflowRecoveryModelProvider(
                        candidate,
                        recoverySafety,
                        contextWindowTokens,
                        (request, token) => CompactOverflowRequestAsync(
                            loaded.Key,
                            request,
                            contextWindowTokens,
                            maximumOutputTokens,
                            usageAccounting,
                            token),
                        usageAccounting.RecordRecoveryAttemptAndSuppress,
                        usageAccounting.Record,
                        usageAccounting.Record,
                        usageAccounting.ClearAssistantSuppression);
                provider = wrapRecoveryProvider(provider);
            }

            var runHooks = CreateRunHooks(
                route.Route,
                input,
                extensionContext,
                model,
                parameters,
                contextWindowTokens,
                maximumOutputTokens,
                usageAccounting);
            if (wrapRecoveryProvider is not null)
            {
                var configured = runHooks.PrepareNextTurnAsync;
                runHooks.PrepareNextTurnAsync = async (turnContext, token) =>
                {
                    var update = configured is null
                        ? null
                        : await configured(turnContext, token).ConfigureAwait(false);
                    if (update?.Provider is not null)
                    {
                        update.Provider = wrapRecoveryProvider(update.Provider);
                    }

                    return update;
                };
            }

            if (_persistToolTurnCheckpoints && route.Route == GameRouteKind.Agent)
            {
                var configured = runHooks.PrepareNextTurnAsync;
                runHooks.PrepareNextTurnAsync = async (turnContext, token) =>
                {
                    if (!turnContext.Response.Content.OfType<ToolCallContent>().Any())
                    {
                        return configured is null
                            ? null
                            : await configured(turnContext, token).ConfigureAwait(false);
                    }

                    var usageEndIndex = usageAccounting.Count;
                    var usageRecords = usageAccounting.RecordsBetween(
                        committedUsageRecordCount,
                        usageEndIndex);
                    var checkpoint = new GameSessionSnapshot(
                        commitBase.Key,
                        checked(commitBase.Revision + 1),
                        turnContext.Context.Messages,
                        commitBase.ProcessedInputIds,
                        commitBase.LastMoment,
                        extensionState.SnapshotAll(),
                        input.InputId,
                        (commitBase.Revision == loaded.Revision
                            ? baseUsageLedger
                            : commitBase.UsageLedger).Append(usageRecords));
                    var checkpointSave = await _sessionStore.SaveAsync(
                        checkpoint,
                        commitBase.Revision,
                        token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The game session store returned null.");
                    ValidateSaveResult(commitBase, checkpoint, checkpointSave);
                    if (!checkpointSave.Saved)
                    {
                        checkpointConflict = checkpointSave;
                        checkpointConflictUsageRecords = commitBase.Revision == loaded.Revision
                            ? legacyUsageRecords.Concat(usageRecords).ToArray()
                            : usageRecords;
                        checkpointConflictUsageLedger = checkpoint.UsageLedger;
                        throw new InvalidOperationException(
                            "The session changed while a tool turn was being checkpointed.");
                    }

                    commitBase = checkpointSave.Current;
                    committedUsageRecordCount = usageEndIndex;
                    return configured is null
                        ? null
                        : await configured(turnContext, token).ConfigureAwait(false);
                };
            }

            var options = new AgentOptions(provider, model)
            {
                SystemPrompt = systemPrompt,
                SessionId = input.SessionId,
                Limits = agentLimits,
                Parameters = parameters,
                Hooks = runHooks,
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
            using var subscription = agent.Subscribe(async (agentEvent, token) =>
            {
                recoverySafety.Record(agentEvent);
                usageAccounting.Record(agentEvent);
                if (observer is not null)
                {
                    await observer(input, agentEvent, token).ConfigureAwait(false);
                }

                await _extensions.PublishAsync(
                    GameAgentExtensionEvents.KernelEvent,
                    new GameAgentKernelEvent(agentEvent),
                    extensionContext,
                    token).ConfigureAwait(false);
            });
            RegisterActiveAgent(key, agent);
            AgentRunResult run;
            try
            {
                run = resumingCheckpoint
                    ? await agent.ContinueAsync(cancellationToken).ConfigureAwait(false)
                    : await agent.RunAsync(CreateInputMessage(input), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                UnregisterActiveAgent(key, agent);
            }

            if (checkpointConflict is not null)
            {
                GameSessionSnapshot settledConflict;
                using (var settlementCancellation = new CancellationTokenSource(_sessionCommitTimeoutMilliseconds))
                {
                    settledConflict = await SettleUsageAfterConflictAsync(
                        checkpointConflict.Current,
                        checkpointConflictUsageRecords
                            ?? throw new InvalidOperationException("Checkpoint usage settlement state is missing."),
                        checkpointConflictUsageLedger
                            ?? throw new InvalidOperationException("Checkpoint usage ledger state is missing."),
                        settlementCancellation.Token).ConfigureAwait(false);
                }

                var conflict = new GameAgentRunResult(
                    GameAgentRunStatus.SessionConflict,
                    route,
                    settledConflict.Revision,
                    run,
                    "The session changed while this input was running. Committed game actions must be reconciled before retrying.");
                await PublishCompletedAsync(conflict, extensionContext, CancellationToken.None).ConfigureAwait(false);
                return conflict;
            }

            GameSessionSaveResult save;
            GameSessionSnapshot? settledSaveConflict = null;
            using (var settlementCancellation = new CancellationTokenSource(_sessionCommitTimeoutMilliseconds))
            {
                var finalUsageRecords = usageAccounting.RecordsBetween(
                    committedUsageRecordCount,
                    usageAccounting.Count);
                var usageLedger = (commitBase.Revision == loaded.Revision
                    ? baseUsageLedger
                    : commitBase.UsageLedger).Append(finalUsageRecords);
                save = await SaveAsync(
                    input,
                    commitBase,
                    agent.State.Messages,
                    extensionState,
                    extensionContext,
                    usageLedger,
                    settlementCancellation.Token).ConfigureAwait(false);
                if (!save.Saved)
                {
                    settledSaveConflict = await SettleUsageAfterConflictAsync(
                        save.Current,
                        commitBase.Revision == loaded.Revision
                            ? legacyUsageRecords.Concat(finalUsageRecords).ToArray()
                            : finalUsageRecords,
                        usageLedger,
                        settlementCancellation.Token).ConfigureAwait(false);
                }
            }
            if (!save.Saved)
            {
                var conflict = new GameAgentRunResult(
                    GameAgentRunStatus.SessionConflict,
                    route,
                    settledSaveConflict!.Revision,
                    run,
                    "The session changed while this input was running. Committed game actions must be reconciled before retrying.");
                await PublishCompletedAsync(conflict, extensionContext, CancellationToken.None).ConfigureAwait(false);
                return conflict;
            }

            var usageExceeded = usageAccounting.Exceeded;
            var completed = new GameAgentRunResult(
                run.Succeeded && !usageExceeded ? GameAgentRunStatus.Completed : GameAgentRunStatus.Failed,
                route,
                save.Current.Revision,
                run,
                usageExceeded
                    ? $"The run exceeded the maximum of {agentLimits.MaxTotalTokens} total tokens, including transcript compaction."
                    : run.Error);
            await PublishCompletedAsync(completed, extensionContext, CancellationToken.None).ConfigureAwait(false);
            return completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (failureContext is not null)
            {
                await _extensions.PublishAsync(
                    GameAgentExtensionEvents.RunFailed,
                    new GameAgentFailureEvent(exception),
                    failureContext,
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            failureContext?.Invalidate();
        }
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
        GameAgentSessionState extensionState,
        GameAgentExtensionRunContext extensionContext,
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
        GameSessionSaveResult save;
        using (var settlementCancellation = new CancellationTokenSource(_sessionCommitTimeoutMilliseconds))
        {
            save = await SaveAsync(
                input,
                loaded,
                messages,
                extensionState,
                extensionContext,
                loaded.UsageLedger.Append(CreateLegacyUsageRecords(loaded)),
                settlementCancellation.Token).ConfigureAwait(false);
        }
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
        GameAgentSessionState extensionState,
        GameAgentExtensionRunContext extensionContext,
        GameSessionUsageLedger usageLedger,
        CancellationToken cancellationToken)
    {
        await _extensions.PublishAsync(
            GameAgentExtensionEvents.SessionSaving,
            new GameAgentSessionEvent(loaded),
            extensionContext,
            cancellationToken).ConfigureAwait(false);
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
            input.Moment,
            extensionState.SnapshotAll(),
            pendingInputId: null,
            usageLedger);
        var save = await _sessionStore.SaveAsync(snapshot, loaded.Revision, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The game session store returned null.");
        ValidateSaveResult(loaded, snapshot, save);

        if (save.Saved)
        {
            await _extensions.PublishAsync(
                GameAgentExtensionEvents.SessionSaved,
                new GameAgentSessionEvent(save.Current),
                extensionContext,
                CancellationToken.None).ConfigureAwait(false);
        }

        return save;
    }

    private async ValueTask<GameSessionSnapshot> SaveUsageOnlyAsync(
        GameSessionSnapshot current,
        IReadOnlyList<GameSessionUsageRecord> usageRecords,
        GameSessionUsageLedger attemptedLedger,
        CancellationToken cancellationToken)
    {
        if (UsageLedgerEquals(current.UsageLedger, attemptedLedger))
        {
            return current;
        }

        var candidate = new GameSessionSnapshot(
            current.Key,
            checked(current.Revision + 1),
            current.Messages,
            current.ProcessedInputIds,
            current.LastMoment,
            current.ExtensionState,
            current.PendingInputId,
            attemptedLedger);
        var save = await _sessionStore.SaveAsync(
            candidate,
            current.Revision,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The game session store returned null.");
        ValidateSaveResult(current, candidate, save);
        return save.Saved
            ? save.Current
            : await SettleUsageAfterConflictAsync(
                save.Current,
                usageRecords,
                attemptedLedger,
                cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GameSessionSnapshot> SettleUsageAfterConflictAsync(
        GameSessionSnapshot current,
        IReadOnlyList<GameSessionUsageRecord> usageRecords,
        GameSessionUsageLedger attemptedLedger,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 8;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (UsageLedgerEquals(current.UsageLedger, attemptedLedger))
            {
                return current;
            }

            var merged = current.UsageLedger.Append(usageRecords);
            if (ReferenceEquals(merged, current.UsageLedger))
            {
                return current;
            }

            var candidate = new GameSessionSnapshot(
                current.Key,
                checked(current.Revision + 1),
                current.Messages,
                current.ProcessedInputIds,
                current.LastMoment,
                current.ExtensionState,
                current.PendingInputId,
                merged);
            var save = await _sessionStore.SaveAsync(
                candidate,
                current.Revision,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game session store returned null.");
            ValidateSaveResult(current, candidate, save);
            if (save.Saved)
            {
                return save.Current;
            }

            attemptedLedger = merged;
            current = save.Current;
        }

        throw new InvalidOperationException(
            $"The session usage ledger could not be settled after {maximumAttempts} compare-and-swap attempts.");
    }

    private static void ValidateSaveResult(
        GameSessionSnapshot expectedBase,
        GameSessionSnapshot candidate,
        GameSessionSaveResult save)
    {
        if (!save.Current.Key.Equals(expectedBase.Key))
        {
            throw new InvalidOperationException("The game session store returned a result for a different session key.");
        }

        if (save.Saved && !SessionSnapshotEquals(save.Current, candidate))
        {
            throw new InvalidOperationException("The game session store returned a different saved snapshot.");
        }

        if (!save.Saved && save.Current.Revision <= expectedBase.Revision)
        {
            throw new InvalidOperationException("The game session store reported a conflict without a newer revision.");
        }
    }

    private static bool SessionSnapshotEquals(GameSessionSnapshot left, GameSessionSnapshot right)
    {
        if (!left.Key.Equals(right.Key)
            || left.Revision != right.Revision
            || left.LastMoment != right.LastMoment
            || !string.Equals(left.PendingInputId, right.PendingInputId, StringComparison.Ordinal)
            || !left.ProcessedInputIds.SequenceEqual(right.ProcessedInputIds, StringComparer.Ordinal)
            || !left.ExtensionState.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(right.ExtensionState.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            || left.Messages.Count != right.Messages.Count
            || !UsageLedgerEquals(left.UsageLedger, right.UsageLedger))
        {
            return false;
        }

        for (var index = 0; index < left.Messages.Count; index++)
        {
            if (!GameAgentValueComparer.MessageEquals(left.Messages[index], right.Messages[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool UsageLedgerEquals(GameSessionUsageLedger left, GameSessionUsageLedger right) =>
        left.Records.Count == right.Records.Count
        && left.TotalRecordCount == right.TotalRecordCount
        && left.RecentRecordCapacity == right.RecentRecordCapacity
        && left.TotalsByCause.Count == right.TotalsByCause.Count
        && left.TotalsByCause.All(pair =>
            right.TotalsByCause.TryGetValue(pair.Key, out var total)
            && GameSessionUsageTotals.ValueEquals(pair.Value, total))
        && left.Records.Zip(right.Records, GameSessionUsageRecord.ValueEquals).All(equal => equal);

    private static IReadOnlyList<GameSessionUsageRecord> CreateLegacyUsageRecords(GameSessionSnapshot session)
    {
        if (session.UsageLedger.TotalRecordCount != 0)
        {
            return Array.Empty<GameSessionUsageRecord>();
        }

        var records = session.Messages
            .Select((message, index) => new { Message = message, Index = index })
            .Where(item => item.Message.Usage is not null
                && (item.Message.Usage.TotalTokens > 0 || item.Message.Usage.Cost.Total > 0)
                && item.Message.Role is AgentRole.Assistant or AgentRole.Tool)
            .Select(item => new GameSessionUsageRecord(
                $"legacy-message-{item.Index}",
                item.Message.Role == AgentRole.Assistant
                    ? GameSessionUsageCause.Assistant
                    : GameSessionUsageCause.Tool,
                item.Message.Usage!,
                inputId: item.Message.Metadata.TryGetValue("game.input_id", out var inputId)
                    && !string.IsNullOrWhiteSpace(inputId)
                        ? inputId
                        : null))
            .ToArray();
        return Array.AsReadOnly(records);
    }

    private string ComposeSystemPrompt(
        IReadOnlyList<GameContextSlice> context,
        IReadOnlyList<GameSkill> skills)
    {
        return _extensions.ComposePrompt(_instructions)
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
        var content = new List<AgentContent>(input.Resources.Count + 1)
        {
            new JsonContent(payload),
        };
        content.AddRange(input.Resources);
        return new AgentMessage(AgentRole.User, content, DateTimeOffset.UtcNow, metadata: metadata);
    }

    private static void ValidatePendingInput(
        GameSessionSnapshot loaded,
        GameInput input,
        IReadOnlyList<AgentMessage>? transcript = null)
    {
        var messages = transcript ?? loaded.Messages;
        var matches = messages.Where(message =>
                message.Role == AgentRole.User
                && message.Metadata.TryGetValue("game.input_id", out var value)
                && string.Equals(value, input.InputId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("The pending input checkpoint does not contain exactly one matching input message.");
        }

        var expected = CreateInputMessage(input);
        if (!InputMessageEquals(matches[0], expected))
        {
            throw new InvalidOperationException("The resubmitted input does not match its durable checkpoint.");
        }

        if (messages.Count == 0 || messages[messages.Count - 1].Role == AgentRole.Assistant)
        {
            throw new InvalidOperationException("The pending input checkpoint is not resumable.");
        }

    }

    private static bool InputMessageEquals(AgentMessage left, AgentMessage right) =>
        left.Role == right.Role
        && string.Equals(left.CustomRole, right.CustomRole, StringComparison.Ordinal)
        && left.Metadata.Count == right.Metadata.Count
        && left.Metadata.All(pair => right.Metadata.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal))
        && left.Content.Count == right.Content.Count
        && left.Content.Zip(right.Content, GameAgentValueComparer.ContentEquals).All(equal => equal);

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
        ModelTimeoutMilliseconds = value.ModelTimeoutMilliseconds,
        MaxProgressEventsPerTool = value.MaxProgressEventsPerTool,
        MaxSubscribers = value.MaxSubscribers,
    };

    private AgentHooks CreateRunHooks(
        GameRouteKind route,
        GameInput input,
        GameAgentExtensionRunContext extensionContext,
        string model,
        ModelParameters parameters,
        int contextWindowTokens,
        int maximumOutputTokens,
        RunUsageAccounting usageAccounting)
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

        hooks = _extensions.ComposeHooks(extensionContext, hooks);

        if (route == GameRouteKind.Agent && _refreshContextAfterToolTurns)
        {
            var configured = hooks.PrepareNextTurnAsync;
            hooks.PrepareNextTurnAsync = async (context, cancellationToken) =>
            {
                var update = configured is null
                    ? null
                    : await configured(context, cancellationToken).ConfigureAwait(false);
                if (!context.Response.Content.OfType<ToolCallContent>().Any())
                {
                    return update;
                }

                var nextModel = update?.Model ?? model;
                var nextParameters = update?.Parameters ?? parameters;
                if (update?.Context is { } replacement)
                {
                    var preferredMessageReserve = replacement.Tools.Count == 0
                        ? 1
                        : checked(_agentLimits.MaxToolCallsPerTurn + 2);
                    var compacted = await FitTranscriptAsync(
                        new GameSessionKey(input.SessionId, input.ActorId),
                        replacement.Messages,
                        Math.Max(1, _agentLimits.MaxMessages - preferredMessageReserve),
                        Array.Empty<AgentMessage>(),
                        nextModel,
                        replacement.SystemPrompt,
                        replacement.Tools.Select(tool => tool.Definition).ToArray(),
                        nextParameters,
                        contextWindowTokens,
                        maximumOutputTokens,
                        usageAccounting,
                        cancellationToken).ConfigureAwait(false);
                    return new NextTurnUpdate
                    {
                        Context = new AgentContext(replacement.SystemPrompt, compacted, replacement.Tools),
                        Provider = update.Provider,
                        Model = update.Model,
                        Parameters = update.Parameters,
                    };
                }

                var refreshed = await RefreshTurnContextAsync(
                    input,
                    context.Context.Messages,
                    extensionContext,
                    nextModel,
                    nextParameters,
                    contextWindowTokens,
                    maximumOutputTokens,
                    usageAccounting,
                    cancellationToken).ConfigureAwait(false);
                return new NextTurnUpdate
                {
                    Context = refreshed,
                    Provider = update?.Provider,
                    Model = update?.Model,
                    Parameters = update?.Parameters,
                };
            };
        }

        if (contextWindowTokens > 0)
        {
            var configured = hooks.BeforeModelRequestAsync;
            hooks.BeforeModelRequestAsync = async (request, cancellationToken) =>
            {
                var prepared = configured is null
                    ? request
                    : await configured(request, cancellationToken).ConfigureAwait(false);
                var available = GetAvailableInputTokens(
                    prepared.Parameters,
                    contextWindowTokens,
                    maximumOutputTokens);
                if (EstimateRequestTokens(
                        prepared.Model,
                        prepared.SystemPrompt,
                        prepared.Messages,
                        prepared.Tools) > available)
                {
                    throw new GameRuntimeLimitException(
                        nameof(GameAgentRuntimeOptions.ContextWindowTokens),
                        "The prepared model request exceeds the active context window.");
                }

                return prepared;
            };
        }

        var configuredBeforeModelRequest = hooks.BeforeModelRequestAsync;
        hooks.BeforeModelRequestAsync = async (request, cancellationToken) =>
        {
            if (usageAccounting.Exceeded)
            {
                throw usageAccounting.CreateLimitException();
            }

            return configuredBeforeModelRequest is null
                ? request
                : await configuredBeforeModelRequest(request, cancellationToken).ConfigureAwait(false);
        };

        var configuredBeforeToolCall = hooks.BeforeToolCallAsync;
        hooks.BeforeToolCallAsync = async (context, cancellationToken) =>
        {
            if (usageAccounting.Exceeded)
            {
                return ToolCallDecision.Block(
                    usageAccounting.CreateLimitException().Message,
                    terminate: true);
            }

            return configuredBeforeToolCall is null
                ? null
                : await configuredBeforeToolCall(context, cancellationToken).ConfigureAwait(false);
        };

        var configuredShouldStop = hooks.ShouldStopAfterTurnAsync;
        hooks.ShouldStopAfterTurnAsync = async (context, cancellationToken) =>
        {
            var configuredStop = configuredShouldStop is not null
                && await configuredShouldStop(context, cancellationToken).ConfigureAwait(false);
            return configuredStop || usageAccounting.Exceeded;
        };

        return hooks;
    }

    private async ValueTask<AgentContext> RefreshTurnContextAsync(
        GameInput input,
        IReadOnlyList<AgentMessage> messages,
        GameAgentExtensionRunContext extensionContext,
        string model,
        ModelParameters parameters,
        int contextWindowTokens,
        int maximumOutputTokens,
        RunUsageAccounting usageAccounting,
        CancellationToken cancellationToken)
    {
        var baseContext = _contextProvider is null
            ? Array.Empty<GameContextSlice>()
            : (await _contextProvider.GetContextAsync(input, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game context provider returned null.")).ToArray();
        var context = await _extensions.CollectContextAsync(
            extensionContext,
            baseContext,
            cancellationToken).ConfigureAwait(false);
        _limits.Validate(context);

        var baseTools = _toolProvider is null
            ? Array.Empty<AgentTool>()
            : (await _toolProvider(input, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game tool provider returned null.")).ToArray();
        if (baseTools.Any(tool => tool is null))
        {
            throw new InvalidOperationException("The game tool provider returned a null tool.");
        }

        var tools = await _extensions.CollectToolsAsync(
            extensionContext,
            baseTools,
            cancellationToken).ConfigureAwait(false);

        var baseSkills = _skillSource is null
            ? Array.Empty<GameSkill>()
            : (await _skillSource.SelectAsync(
                new GameSkillQuery(input, tools.Select(tool => tool.Definition.Name).ToArray(), _limits.MaxSkillsPerRun),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The game skill source returned null.")).ToArray();
        var skills = await _extensions.CollectSkillsAsync(
            extensionContext,
            baseSkills,
            tools.Select(tool => tool.Definition.Name).ToArray(),
            _limits.MaxSkillsPerRun,
            cancellationToken).ConfigureAwait(false);
        _limits.Validate(skills);
        var systemPrompt = ComposeSystemPrompt(context, skills);
        var preferredMessageReserve = tools.Count == 0
            ? 1
            : checked(_agentLimits.MaxToolCallsPerTurn + 2);
        var compacted = await FitTranscriptAsync(
            new GameSessionKey(input.SessionId, input.ActorId),
            messages,
            Math.Max(1, _agentLimits.MaxMessages - preferredMessageReserve),
            Array.Empty<AgentMessage>(),
            model,
            systemPrompt,
            tools.Select(tool => tool.Definition).ToArray(),
            parameters,
            contextWindowTokens,
            maximumOutputTokens,
            usageAccounting,
            cancellationToken).ConfigureAwait(false);
        return new AgentContext(systemPrompt, compacted, tools);
    }

    private async ValueTask<IReadOnlyList<AgentMessage>> FitTranscriptAsync(
        GameSessionKey session,
        IReadOnlyList<AgentMessage> messages,
        int targetMessageCount,
        IReadOnlyList<AgentMessage> additionalMessages,
        string model,
        string systemPrompt,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters,
        int contextWindowTokens,
        int maximumOutputTokens,
        RunUsageAccounting usageAccounting,
        CancellationToken cancellationToken)
    {
        var tokenTarget = GetTranscriptTokenTarget(
            model,
            systemPrompt,
            additionalMessages,
            tools,
            parameters,
            contextWindowTokens,
            maximumOutputTokens);
        var messageCompactionRequired = messages.Count > targetMessageCount;
        var tokenCompactionRequired = tokenTarget is { } target
            && EstimateTranscriptTokens(messages) > target;
        IReadOnlyList<AgentMessage> fitted = messages;
        if ((messageCompactionRequired || tokenCompactionRequired) && _transcriptCompactor is not null)
        {
            try
            {
                var compaction = await _transcriptCompactor.CompactAsync(
                    new GameTranscriptCompactionContext(
                        session,
                        messages,
                        targetMessageCount,
                        tokenTarget,
                        tokenTarget is null ? null : _transcriptTokenEstimator,
                        usageAccounting.RemainingTokens),
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The transcript compactor returned null.");
                usageAccounting.Record(compaction);
                fitted = compaction.Messages;
            }
            catch (GameTranscriptCompactionException exception)
            {
                usageAccounting.Record(exception);
                throw;
            }
        }

        if (fitted.Count > targetMessageCount)
        {
            throw new GameRuntimeLimitException(
                nameof(AgentLimits.MaxMessages),
                _transcriptCompactor is null
                    ? "The session transcript requires compaction before another model turn."
                    : "The transcript compactor exceeded its requested message target.");
        }

        AgentValidation.ValidateTranscript(fitted, _agentLimits);
        var requestMessages = fitted.Concat(additionalMessages).ToArray();
        if (contextWindowTokens > 0)
        {
            var available = GetAvailableInputTokens(parameters, contextWindowTokens, maximumOutputTokens);
            var estimate = EstimateRequestTokens(model, systemPrompt, requestMessages, tools);
            if (estimate > available)
            {
                throw new GameRuntimeLimitException(
                    nameof(GameAgentRuntimeOptions.ContextWindowTokens),
                    _transcriptCompactor is null
                        ? "The estimated model request exceeds the context window and no transcript compactor is configured."
                        : "The compacted model request still exceeds the configured context window.");
            }
        }

        return fitted;
    }

    private async ValueTask<GameModelRecoveryCompaction?> CompactOverflowRequestAsync(
        GameSessionKey session,
        ModelRequest request,
        int contextWindowTokens,
        int maximumOutputTokens,
        RunUsageAccounting usageAccounting,
        CancellationToken cancellationToken)
    {
        if (_transcriptCompactor is null || usageAccounting.Exceeded)
        {
            return null;
        }

        var protectedStart = -1;
        for (var index = request.Messages.Count - 1; index >= 0; index--)
        {
            if (request.Messages[index].Role == AgentRole.User)
            {
                protectedStart = index;
                break;
            }
        }

        if (protectedStart < 2)
        {
            return null;
        }

        var history = request.Messages.Take(protectedStart).ToArray();
        var protectedTail = request.Messages.Skip(protectedStart).ToArray();
        GameTranscriptStructure.ValidateToolExchanges(history);
        var available = GetAvailableInputTokens(
            request.Parameters,
            contextWindowTokens,
            maximumOutputTokens);
        var safetyMargin = Math.Max(256L, contextWindowTokens / 20L);
        var recoveryAvailable = available - safetyMargin;
        if (recoveryAvailable <= 0)
        {
            return null;
        }

        var fixedTokens = EstimateRequestTokens(
            request.Model,
            request.SystemPrompt,
            protectedTail,
            request.Tools);
        var historyTarget = recoveryAvailable - fixedTokens;
        if (historyTarget <= 0)
        {
            return null;
        }

        var summaryUsageBudget = usageAccounting.RemainingTokens;
        if (summaryUsageBudget <= 0)
        {
            return null;
        }

        var compaction = await _transcriptCompactor.CompactAsync(
            new GameTranscriptCompactionContext(
                session,
                history,
                Math.Max(1, history.Length - 1),
                historyTarget,
                _transcriptTokenEstimator,
                summaryUsageBudget),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The transcript compactor returned null.");
        if (compaction.Usage.TotalTokens > usageAccounting.RemainingTokens)
        {
            throw new GameTranscriptCompactionException(
                "recovery_usage_limit_exceeded",
                "The failed provider attempt and recovery compaction exhausted the run token budget.",
                compaction.Usage,
                compaction.Details);
        }

        var recoveredMessages = compaction.Messages.Concat(protectedTail).ToArray();
        AgentValidation.ValidateTranscript(recoveredMessages, _agentLimits);
        if (EstimateRequestTokens(
                request.Model,
                request.SystemPrompt,
                recoveredMessages,
                request.Tools) > recoveryAvailable)
        {
            throw new GameTranscriptCompactionException(
                "recovery_target_exceeded",
                "The recovered transcript still exceeds the conservative context-window target.",
                compaction.Usage,
                compaction.Details);
        }

        return new GameModelRecoveryCompaction(
            new ModelRequest(
                request.Model,
                request.SystemPrompt,
                recoveredMessages,
                request.Tools,
                request.Parameters,
                request.SessionId,
                request.RunId,
                request.Turn),
            compaction);
    }

    private long? GetTranscriptTokenTarget(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> additionalMessages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters,
        int contextWindowTokens,
        int maximumOutputTokens)
    {
        if (contextWindowTokens == 0)
        {
            return null;
        }

        var available = GetAvailableInputTokens(parameters, contextWindowTokens, maximumOutputTokens);
        var fixedTokens = EstimateRequestTokens(model, systemPrompt, additionalMessages, tools);
        if (fixedTokens >= available)
        {
            throw new GameRuntimeLimitException(
                nameof(GameAgentRuntimeOptions.ContextWindowTokens),
                "The system prompt, tools, and new input leave no context budget for the session transcript.");
        }

        return available - fixedTokens;
    }

    private long GetAvailableInputTokens(
        ModelParameters parameters,
        int contextWindowTokens,
        int maximumOutputTokens)
    {
        var reserve = parameters.MaxOutputTokens is > 0
            ? parameters.MaxOutputTokens.Value
            : maximumOutputTokens > 0
                ? maximumOutputTokens
                : _contextWindowReserveTokens;
        if (reserve >= contextWindowTokens)
        {
            throw new GameRuntimeLimitException(
                nameof(GameAgentRuntimeOptions.ContextWindowReserveTokens),
                "The output-token reserve must be smaller than the active model context window.");
        }

        return contextWindowTokens - reserve;
    }

    private long EstimateRequestTokens(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        var estimate = _requestTokenEstimator(model, systemPrompt, messages, tools);
        return ValidateTokenEstimate(estimate, "request");
    }

    private long EstimateTranscriptTokens(IReadOnlyList<AgentMessage> messages)
    {
        var estimate = _transcriptTokenEstimator(messages);
        return ValidateTokenEstimate(estimate, "transcript");
    }

    private static long ValidateTokenEstimate(long estimate, string kind) =>
        estimate is >= 0 and <= 10_000_000_000
            ? estimate
            : throw new InvalidOperationException($"The {kind} token estimator returned an invalid value.");

    private sealed class RunUsageAccounting
    {
        private readonly object _gate = new();
        private readonly string _attemptId = Guid.NewGuid().ToString("N");
        private readonly string _inputId;
        private readonly long _maximumTokens;
        private readonly List<GameSessionUsageRecord> _records = new();
        private readonly HashSet<string> _suppressedAssistantRuns = new(StringComparer.Ordinal);
        private long _totalTokens;
        private int _sequence;

        public RunUsageAccounting(string inputId, long maximumTokens)
        {
            _inputId = GameJson.RequireId(inputId, nameof(inputId));
            _maximumTokens = maximumTokens;
        }

        public bool Exceeded
        {
            get
            {
                lock (_gate)
                {
                    return _totalTokens > _maximumTokens;
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _records.Count;
                }
            }
        }

        public long RemainingTokens
        {
            get
            {
                lock (_gate)
                {
                    return Math.Max(0, _maximumTokens - _totalTokens);
                }
            }
        }

        public void Record(GameTranscriptCompactionResult result)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            Add(
                GameSessionUsageCause.Compaction,
                result.Usage,
                _attemptId,
                JsonSerializer.Serialize(result.Details));
        }

        public void Record(GameTranscriptCompactionException exception)
        {
            if (exception is null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            if (exception.Usage.TotalTokens == 0 && exception.Usage.Cost.Total == 0)
            {
                return;
            }

            Add(
                GameSessionUsageCause.Compaction,
                exception.Usage,
                _attemptId,
                JsonSerializer.Serialize(exception.Details));
        }

        public void Record(AgentEvent agentEvent)
        {
            if (agentEvent is null)
            {
                throw new ArgumentNullException(nameof(agentEvent));
            }

            if (agentEvent.Kind == AgentEventKind.MessageEnded
                && agentEvent.Message?.Role == AgentRole.Assistant
                && agentEvent.Message.Usage is not null)
            {
                lock (_gate)
                {
                    if (_suppressedAssistantRuns.Remove(agentEvent.RunId))
                    {
                        return;
                    }
                }

                Add(GameSessionUsageCause.Assistant, agentEvent.Message.Usage, agentEvent.RunId, detailsJson: null);
            }
            else if (agentEvent.Kind == AgentEventKind.ToolEnded
                && agentEvent.ToolResult?.Usage is not null)
            {
                Add(GameSessionUsageCause.Tool, agentEvent.ToolResult.Usage, agentEvent.RunId, detailsJson: null);
            }
        }

        public void ClearAssistantSuppression(string runId)
        {
            runId = GameJson.RequireId(runId, nameof(runId));
            lock (_gate)
            {
                _suppressedAssistantRuns.Remove(runId);
            }
        }

        public void RecordRecoveryAttemptAndSuppress(ModelUsage usage, string runId, string kind)
        {
            if (usage is null)
            {
                throw new ArgumentNullException(nameof(usage));
            }

            runId = GameJson.RequireId(runId, nameof(runId));
            lock (_gate)
            {
                if (!_suppressedAssistantRuns.Add(runId))
                {
                    throw new InvalidOperationException("An assistant usage suppression is already active for this run.");
                }

                Add(
                    GameSessionUsageCause.Assistant,
                    usage,
                    runId,
                    JsonSerializer.Serialize(new
                    {
                        category = "context_overflow_recovery",
                        attempt = 1,
                        outcome = kind,
                    }));
            }
        }

        public IReadOnlyList<GameSessionUsageRecord> RecordsBetween(int startIndex, int endIndex)
        {
            lock (_gate)
            {
                if (startIndex < 0 || endIndex < startIndex || endIndex > _records.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(startIndex));
                }

                return Array.AsReadOnly(_records.Skip(startIndex).Take(endIndex - startIndex).ToArray());
            }
        }

        public GameRuntimeLimitException CreateLimitException() => new(
            nameof(AgentLimits.MaxTotalTokens),
            $"The run exceeded the maximum of {_maximumTokens} total tokens, including transcript compaction.");

        private void Add(
            GameSessionUsageCause cause,
            ModelUsage usage,
            string runId,
            string? detailsJson)
        {
            lock (_gate)
            {
                var sequence = checked(_sequence++);
                _records.Add(new GameSessionUsageRecord(
                    $"{_attemptId}-{sequence}",
                    cause,
                    usage,
                    runId,
                    _inputId,
                    detailsJson));
                _totalTokens = checked(_totalTokens + usage.TotalTokens);
            }
        }
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

    private ValueTask PublishCompletedAsync(
        GameAgentRunResult result,
        GameAgentExtensionRunContext extensionContext,
        CancellationToken cancellationToken) =>
        _extensions.PublishAsync(
            GameAgentExtensionEvents.RunCompleted,
            new GameAgentRunEvent(result),
            extensionContext,
            cancellationToken);

    public void Dispose()
    {
        GameAgentAsyncBridge.Run(DisposeAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _lifetimeCancellation.Cancel();
        }
        catch (AggregateException)
        {
            // User cancellation callbacks cannot prevent runtime shutdown.
        }

        Agent[] active;
        lock (_activeAgentsGate)
        {
            active = _activeAgents.Values.ToArray();
        }

        foreach (var agent in active)
        {
            agent.TryAbort();
        }

        try
        {
            await _actors.WaitForIdleAsync().ConfigureAwait(false);
            await _extensions.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetimeCancellation.Dispose();
        }
    }

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

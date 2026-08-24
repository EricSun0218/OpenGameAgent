using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Attachments;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

/// <summary>
/// Describes a runtime extension without coupling it to an engine or deployment model.
/// </summary>
public sealed class GameAgentExtensionDescriptor
{
    public GameAgentExtensionDescriptor(
        string id,
        string version,
        string? description = null,
        IEnumerable<string>? capabilities = null)
    {
        Id = GameJson.RequireId(id, nameof(id));
        Version = GameJson.RequireId(version, nameof(version));
        Description = description ?? string.Empty;
        var copied = (capabilities ?? Array.Empty<string>())
            .Select(value => GameJson.RequireId(value, nameof(capabilities)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Capabilities = Array.AsReadOnly(copied);
    }

    public string Id { get; }

    public string Version { get; }

    public string Description { get; }

    public IReadOnlyList<string> Capabilities { get; }
}

/// <summary>
/// A package-level extension. First-party and third-party features use this same contract.
/// </summary>
public interface IGameAgentExtension
{
    GameAgentExtensionDescriptor Descriptor { get; }

    void Configure(GameAgentExtensionApi api);
}

public sealed class DelegateGameAgentExtension : IGameAgentExtension
{
    private readonly Action<GameAgentExtensionApi> _configure;

    public DelegateGameAgentExtension(
        GameAgentExtensionDescriptor descriptor,
        Action<GameAgentExtensionApi> configure)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    public GameAgentExtensionDescriptor Descriptor { get; }

    public void Configure(GameAgentExtensionApi api) =>
        _configure(api ?? throw new ArgumentNullException(nameof(api)));
}

public enum GameAgentExtensionResourceKind
{
    ContextProvider,
    Tool,
    ToolProvider,
    ToolVisibilityPolicy,
    SkillProvider,
    RouteRule,
    PendingWorkProvider,
    Workflow,
    AgentHooks,
    PromptFragment,
    ModelProvider,
    Service,
    EventHandler,
}

public sealed class GameAgentExtensionResource
{
    internal GameAgentExtensionResource(
        string extensionId,
        string name,
        GameAgentExtensionResourceKind kind,
        int priority,
        long sequence)
    {
        ExtensionId = extensionId;
        Name = name;
        Kind = kind;
        Priority = priority;
        Sequence = sequence;
    }

    public string ExtensionId { get; }

    public string Name { get; }

    public GameAgentExtensionResourceKind Kind { get; }

    public int Priority { get; }

    internal long Sequence { get; }
}

public enum GameAgentExtensionDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed class GameAgentExtensionDiagnostic
{
    public GameAgentExtensionDiagnostic(
        GameAgentExtensionDiagnosticSeverity severity,
        string code,
        string message,
        string? extensionId = null,
        string? resourceName = null)
    {
        if (!Enum.IsDefined(typeof(GameAgentExtensionDiagnosticSeverity), severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Severity = severity;
        Code = GameJson.RequireId(code, nameof(code));
        Message = string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("A diagnostic message is required.", nameof(message))
            : message;
        ExtensionId = extensionId;
        ResourceName = resourceName;
    }

    public GameAgentExtensionDiagnosticSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public string? ExtensionId { get; }

    public string? ResourceName { get; }
}

public interface IGameAgentExtensionRegistration : IDisposable
{
    GameAgentExtensionResource Resource { get; }

    bool IsActive { get; }
}

/// <summary>
/// A typed lifecycle event key. Matching is by object identity so unrelated extensions cannot
/// accidentally reuse a textual channel with an incompatible payload type.
/// </summary>
public sealed class GameAgentExtensionEvent<TEvent>
{
    public GameAgentExtensionEvent(string name)
    {
        Name = GameJson.RequireId(name, nameof(name));
    }

    public string Name { get; }
}

/// <summary>
/// Typed cross-extension channel. Channels are explicit objects, avoiding string-only payload contracts.
/// </summary>
public sealed class GameAgentExtensionChannel<TMessage>
{
    public GameAgentExtensionChannel(string name)
    {
        Name = GameJson.RequireId(name, nameof(name));
    }

    public string Name { get; }
}

public static class GameAgentExtensionEvents
{
    public static GameAgentExtensionEvent<GameAgentInputEvent> InputReceived { get; } = new("input.received");

    public static GameAgentExtensionEvent<GameAgentSessionEvent> SessionLoaded { get; } = new("session.loaded");

    public static GameAgentExtensionEvent<GameAgentContextEvent> ContextCollected { get; } = new("context.collected");

    public static GameAgentExtensionEvent<GameAgentContextProviderEvent> ContextProviderCompleted { get; } = new("context.provider.completed");

    public static GameAgentExtensionEvent<GameAgentToolsEvent> ToolsCollected { get; } = new("tools.collected");

    public static GameAgentExtensionEvent<GameAgentRouteEvent> RouteSelected { get; } = new("route.selected");

    public static GameAgentExtensionEvent<GameAgentSkillsEvent> SkillsSelected { get; } = new("skills.selected");

    public static GameAgentExtensionEvent<GameAgentImagesProjectedEvent> ImagesProjected { get; } = new("images.projected");

    public static GameAgentExtensionEvent<GameAgentKernelEvent> KernelEvent { get; } = new("kernel.event");

    public static GameAgentExtensionEvent<GameAgentRunEvent> RunCompleted { get; } = new("run.completed");

    public static GameAgentExtensionEvent<GameAgentSessionEvent> SessionSaving { get; } = new("session.saving");

    public static GameAgentExtensionEvent<GameAgentSessionEvent> SessionSaved { get; } = new("session.saved");

    public static GameAgentExtensionEvent<GameAgentFailureEvent> RunFailed { get; } = new("run.failed");
}

public sealed class GameAgentInputEvent
{
    public GameAgentInputEvent(
        GameInput input,
        TimeSpan? queueDuration = null,
        TimeSpan? inputPreparationDuration = null,
        TimeSpan? sessionLoadDuration = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        QueueDuration = RequireDuration(queueDuration, nameof(queueDuration));
        InputPreparationDuration = RequireDuration(inputPreparationDuration, nameof(inputPreparationDuration));
        SessionLoadDuration = RequireDuration(sessionLoadDuration, nameof(sessionLoadDuration));
    }

    public GameInput Input { get; }

    public TimeSpan? QueueDuration { get; }

    public TimeSpan? InputPreparationDuration { get; }

    public TimeSpan? SessionLoadDuration { get; }

    private static TimeSpan? RequireDuration(TimeSpan? value, string name) =>
        value is { } duration && (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(1))
            ? throw new ArgumentOutOfRangeException(name)
            : value;
}

public sealed class GameAgentSessionEvent
{
    public GameAgentSessionEvent(GameSessionSnapshot session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public GameSessionSnapshot Session { get; }
}

public sealed class GameAgentContextEvent
{
    public GameAgentContextEvent(IReadOnlyList<GameContextSlice> context, TimeSpan? duration = null)
    {
        var copy = (context ?? throw new ArgumentNullException(nameof(context))).ToArray();
        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("Context events cannot contain null slices.", nameof(context));
        }

        Context = Array.AsReadOnly(copy);
        Duration = RequireDuration(duration, nameof(duration));
    }

    public IReadOnlyList<GameContextSlice> Context { get; }

    public TimeSpan? Duration { get; }

    private static TimeSpan? RequireDuration(TimeSpan? value, string name) =>
        value is { } duration && (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(1))
            ? throw new ArgumentOutOfRangeException(name)
            : value;
}

public sealed class GameAgentContextProviderEvent
{
    public GameAgentContextProviderEvent(
        string providerName,
        string phase,
        int sliceCount,
        TimeSpan duration,
        string? extensionId = null)
    {
        ProviderName = GameJson.RequireId(providerName, nameof(providerName));
        Phase = GameJson.RequireId(phase, nameof(phase));
        if (sliceCount < 0 || sliceCount > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sliceCount));
        }

        if (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        ExtensionId = extensionId is null ? null : GameJson.RequireId(extensionId, nameof(extensionId));
        SliceCount = sliceCount;
        Duration = duration;
    }

    public string ProviderName { get; }

    public string Phase { get; }

    public int SliceCount { get; }

    public TimeSpan Duration { get; }

    public string? ExtensionId { get; }
}

public sealed class GameAgentToolsEvent
{
    public GameAgentToolsEvent(IReadOnlyList<AgentTool> tools, TimeSpan? duration = null)
    {
        var copy = (tools ?? throw new ArgumentNullException(nameof(tools))).ToArray();
        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("Tool events cannot contain null tools.", nameof(tools));
        }

        Tools = Array.AsReadOnly(copy);
        Duration = RequireDuration(duration, nameof(duration));
    }

    public IReadOnlyList<AgentTool> Tools { get; }

    public TimeSpan? Duration { get; }

    private static TimeSpan? RequireDuration(TimeSpan? value, string name) =>
        value is { } duration && (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(1))
            ? throw new ArgumentOutOfRangeException(name)
            : value;
}

public sealed class GameAgentRouteEvent
{
    public GameAgentRouteEvent(
        GameRouteDecision decision,
        TimeSpan? duration = null,
        TimeSpan? modelDuration = null)
    {
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));
        if (duration is { } value && (value < TimeSpan.Zero || value > TimeSpan.FromDays(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        Duration = duration;
        if (modelDuration is { } modelValue
            && (modelValue < TimeSpan.Zero || modelValue > TimeSpan.FromDays(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(modelDuration));
        }

        ModelDuration = modelDuration;
    }

    public GameRouteDecision Decision { get; set; }

    public TimeSpan? Duration { get; }

    public TimeSpan? ModelDuration { get; }
}

public sealed class GameAgentSkillsEvent
{
    public GameAgentSkillsEvent(IReadOnlyList<GameSkill> skills, TimeSpan? duration = null)
    {
        var copy = (skills ?? throw new ArgumentNullException(nameof(skills))).ToArray();
        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("Skill events cannot contain null skills.", nameof(skills));
        }

        Skills = Array.AsReadOnly(copy);
        Duration = RequireDuration(duration, nameof(duration));
    }

    public IReadOnlyList<GameSkill> Skills { get; }

    public TimeSpan? Duration { get; }

    private static TimeSpan? RequireDuration(TimeSpan? value, string name) =>
        value is { } duration && (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(1))
            ? throw new ArgumentOutOfRangeException(name)
            : value;
}

public sealed class GameAgentImageProjectionRecord
{
    public GameAgentImageProjectionRecord(
        int ordinal,
        string sourceAttachmentId,
        string? requestAttachmentId,
        GameImageProjectionDisposition disposition,
        string? transformId,
        int width,
        int height,
        int bytes)
    {
        Ordinal = ordinal >= 0 ? ordinal : throw new ArgumentOutOfRangeException(nameof(ordinal));
        SourceAttachmentId = GameJson.RequireId(sourceAttachmentId, nameof(sourceAttachmentId));
        RequestAttachmentId = requestAttachmentId is null
            ? null
            : GameJson.RequireId(requestAttachmentId, nameof(requestAttachmentId));
        if (!Enum.IsDefined(typeof(GameImageProjectionDisposition), disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        if (disposition == GameImageProjectionDisposition.Replaced)
        {
            if (requestAttachmentId is not null || width != 0 || height != 0 || bytes != 0)
            {
                throw new ArgumentException("A replaced image cannot carry projected attachment metadata.");
            }
        }
        else if (requestAttachmentId is null || width <= 0 || height <= 0 || bytes <= 0)
        {
            throw new ArgumentException("A retained or derived image requires projected attachment metadata.");
        }

        if (transformId is { Length: > 256 })
        {
            throw new ArgumentException("The transform ID exceeds its contract bound.", nameof(transformId));
        }

        Disposition = disposition;
        TransformId = transformId;
        Width = width;
        Height = height;
        Bytes = bytes;
    }

    public int Ordinal { get; }

    public string SourceAttachmentId { get; }

    public string? RequestAttachmentId { get; }

    public GameImageProjectionDisposition Disposition { get; }

    public string? TransformId { get; }

    public int Width { get; }

    public int Height { get; }

    public int Bytes { get; }
}

public sealed class GameAgentImagesProjectedEvent
{
    public GameAgentImagesProjectedEvent(
        string model,
        string runId,
        int turn,
        IReadOnlyList<GameAgentImageProjectionRecord> images)
    {
        Model = GameJson.RequireId(model, nameof(model));
        RunId = GameJson.RequireId(runId, nameof(runId));
        Turn = turn > 0 ? turn : throw new ArgumentOutOfRangeException(nameof(turn));
        var copy = (images ?? throw new ArgumentNullException(nameof(images))).ToArray();
        if (copy.Any(value => value is null)
            || copy.Select(value => value.Ordinal).Distinct().Count() != copy.Length)
        {
            throw new ArgumentException("Projected image records must have unique ordinals.", nameof(images));
        }

        Images = Array.AsReadOnly(copy);
    }

    public string Model { get; }

    public string RunId { get; }

    public int Turn { get; }

    public IReadOnlyList<GameAgentImageProjectionRecord> Images { get; }
}

public sealed class GameAgentKernelEvent
{
    public GameAgentKernelEvent(AgentEvent value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public AgentEvent Value { get; }
}

public sealed class GameAgentRunEvent
{
    public GameAgentRunEvent(GameAgentRunResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public GameAgentRunResult Result { get; }
}

public sealed class GameAgentFailureEvent
{
    public GameAgentFailureEvent(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public Exception Exception { get; }
}

/// <summary>
/// Mutable, namespaced session state for one extension. Values are JSON and are not added to
/// model context unless the owning extension explicitly contributes them.
/// </summary>
public sealed class GameAgentExtensionState
{
    private readonly GameAgentSessionState _state;
    private readonly string _extensionId;
    private readonly GameAgentExtensionRunLease _lease;

    internal GameAgentExtensionState(
        GameAgentSessionState state,
        string extensionId,
        GameAgentExtensionRunLease lease)
    {
        _state = state;
        _extensionId = extensionId;
        _lease = lease;
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        _lease.EnsureActive();
        return _state.Snapshot(_extensionId);
    }

    public bool TryGet(string key, out string json)
    {
        _lease.EnsureActive();
        return _state.TryGet(_extensionId, key, out json);
    }

    public string? Get(string key) => TryGet(key, out var json) ? json : null;

    public void Set(string key, string json)
    {
        _lease.EnsureActive();
        _state.Set(_extensionId, key, json);
    }

    public bool Remove(string key)
    {
        _lease.EnsureActive();
        return _state.Remove(_extensionId, key);
    }
}

public interface IGameAgentServiceProvider
{
    bool TryGet<T>(string name, out T service) where T : class;

    T GetRequired<T>(string name) where T : class;
}

public sealed class GameAgentExtensionRunContext
{
    internal GameAgentExtensionRunContext(
        GameInput input,
        GameSessionSnapshot session,
        GameExecutionScope executionScope,
        GameAgentSessionState sessionState,
        GameAgentExtensionState state,
        GameAgentExtensionRunLease lease,
        IGameAgentServiceProvider services,
        IReadOnlyList<GameAgentExtensionResource> resources)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ExecutionScope = executionScope ?? throw new ArgumentNullException(nameof(executionScope));
        SessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        State = state ?? throw new ArgumentNullException(nameof(state));
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Resources = Array.AsReadOnly(
            (resources ?? throw new ArgumentNullException(nameof(resources))).ToArray());
    }

    public GameInput Input { get; }

    public GameSessionSnapshot Session { get; }

    /// <summary>
    /// Host-derived optional capability grants for this input. Extensions must enforce this scope
    /// before contributing any capability that requires an explicit grant.
    /// </summary>
    public GameExecutionScope ExecutionScope { get; }

    public GameAgentExtensionState State { get; }

    public IGameAgentServiceProvider Services { get; }

    public IReadOnlyList<GameAgentExtensionResource> Resources { get; }

    public bool IsActive => Lease.IsActive;

    internal GameAgentSessionState SessionState { get; }

    internal GameAgentExtensionRunLease Lease { get; }

    internal void EnsureActive() => Lease.EnsureActive();

    internal void Invalidate() => Lease.Invalidate();
}

public delegate ValueTask<IReadOnlyList<GameContextSlice>> GameExtensionContextProvider(
    GameAgentExtensionRunContext context,
    CancellationToken cancellationToken);

public delegate ValueTask<IReadOnlyList<AgentTool>> GameExtensionToolProvider(
    GameAgentExtensionRunContext context,
    CancellationToken cancellationToken);

/// <summary>
/// Describes one collected tool while its visibility is being resolved for the current input.
/// The run context belongs to the extension that registered the visibility policy, while
/// <see cref="ToolSourceId"/> identifies the tool contributor.
/// </summary>
public sealed class GameToolVisibilityContext
{
    internal GameToolVisibilityContext(
        GameAgentExtensionRunContext runContext,
        ToolDefinition tool,
        ToolRisk risk,
        string toolSourceId)
    {
        RunContext = runContext ?? throw new ArgumentNullException(nameof(runContext));
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        if (!Enum.IsDefined(typeof(ToolRisk), risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        Risk = risk;
        ToolSourceId = GameJson.RequireId(toolSourceId, nameof(toolSourceId));
    }

    public GameAgentExtensionRunContext RunContext { get; }

    public GameInput Input => RunContext.Input;

    public ToolDefinition Tool { get; }

    public ToolRisk Risk { get; }

    public string ToolSourceId { get; }
}

/// <summary>
/// Returns whether a collected tool is visible to the model for the current game input.
/// All registered policies must allow a tool. Policies run before the model request and do not
/// replace execution-time authorization.
/// </summary>
public delegate ValueTask<bool> GameExtensionToolVisibilityPolicy(
    GameToolVisibilityContext context,
    CancellationToken cancellationToken);

public delegate ValueTask<IReadOnlyList<GameSkill>> GameExtensionSkillProvider(
    GameAgentExtensionRunContext context,
    IReadOnlyCollection<string> activeToolNames,
    int maximumSkills,
    int maximumCharacters,
    CancellationToken cancellationToken);

public delegate ValueTask<GameRouteDecision?> GameExtensionRouteRule(
    GameAgentExtensionRunContext context,
    int availableToolCount,
    bool hasPendingWork,
    CancellationToken cancellationToken);

public delegate ValueTask<bool> GameExtensionPendingWorkProvider(
    GameAgentExtensionRunContext context,
    CancellationToken cancellationToken);

public delegate AgentHooks GameExtensionHookFactory(GameAgentExtensionRunContext context);

public delegate ValueTask GameAgentExtensionEventHandler<TEvent>(
    TEvent value,
    GameAgentExtensionRunContext context,
    CancellationToken cancellationToken);

public delegate ValueTask GameAgentExtensionChannelHandler<TMessage>(
    TMessage message,
    CancellationToken cancellationToken);

/// <summary>
/// API exposed to extensions. Registrations remain live until their returned handle is disposed.
/// </summary>
public sealed class GameAgentExtensionApi
{
    private readonly GameAgentExtensionHost _host;
    private readonly string _extensionId;

    internal GameAgentExtensionApi(GameAgentExtensionHost host, string extensionId)
    {
        _host = host;
        _extensionId = extensionId;
    }

    public string ExtensionId => _extensionId;

    public IReadOnlyList<GameAgentExtensionResource> GetResources() => _host.GetResources();

    public IReadOnlyList<GameAgentExtensionDiagnostic> GetDiagnostics() => _host.GetDiagnostics();

    public IGameAgentExtensionRegistration RegisterContextProvider(
        string name,
        GameExtensionContextProvider provider,
        int priority = 0) =>
        _host.Register(_extensionId, name, GameAgentExtensionResourceKind.ContextProvider, provider, priority, unique: true);

    public IGameAgentExtensionRegistration RegisterTool(
        AgentTool tool,
        int priority = 0)
    {
        if (tool is null)
        {
            throw new ArgumentNullException(nameof(tool));
        }

        return _host.Register(
            _extensionId,
            tool.Definition.Name,
            GameAgentExtensionResourceKind.Tool,
            tool,
            priority,
            unique: true);
    }

    public IGameAgentExtensionRegistration RegisterToolProvider(
        string name,
        GameExtensionToolProvider provider,
        int priority = 0) =>
        _host.Register(_extensionId, name, GameAgentExtensionResourceKind.ToolProvider, provider, priority, unique: true);

    public IGameAgentExtensionRegistration RegisterToolVisibilityPolicy(
        string name,
        GameExtensionToolVisibilityPolicy policy,
        int priority = 0) =>
        _host.Register(
            _extensionId,
            name,
            GameAgentExtensionResourceKind.ToolVisibilityPolicy,
            policy,
            priority,
            unique: true);

    public IGameAgentExtensionRegistration RegisterSkillProvider(
        string name,
        GameExtensionSkillProvider provider,
        int priority = 0) =>
        _host.Register(_extensionId, name, GameAgentExtensionResourceKind.SkillProvider, provider, priority, unique: true);

    public IGameAgentExtensionRegistration RegisterRouteRule(
        string name,
        GameExtensionRouteRule rule,
        int priority = 0) =>
        _host.Register(_extensionId, name, GameAgentExtensionResourceKind.RouteRule, rule, priority, unique: true);

    public IGameAgentExtensionRegistration RegisterPendingWorkProvider(
        string name,
        GameExtensionPendingWorkProvider provider,
        int priority = 0) =>
        _host.Register(_extensionId, name, GameAgentExtensionResourceKind.PendingWorkProvider, provider, priority, unique: true);

    public IGameAgentExtensionRegistration RegisterWorkflow(IGameWorkflow workflow, int priority = 0)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        return _host.Register(
            _extensionId,
            workflow.Name,
            GameAgentExtensionResourceKind.Workflow,
            workflow,
            priority,
            unique: true);
    }

    public IGameAgentExtensionRegistration RegisterAgentHooks(
        string name,
        GameExtensionHookFactory factory,
        int priority = 0) =>
        _host.Register(_extensionId, name, GameAgentExtensionResourceKind.AgentHooks, factory, priority, unique: true);

    public IGameAgentExtensionRegistration RegisterPromptFragment(
        string name,
        string instructions,
        int priority = 0)
    {
        if (instructions is null)
        {
            throw new ArgumentNullException(nameof(instructions));
        }

        return _host.Register(
            _extensionId,
            name,
            GameAgentExtensionResourceKind.PromptFragment,
            instructions,
            priority,
            unique: true);
    }

    public IGameAgentExtensionRegistration RegisterModelProvider(
        string name,
        IModelProvider provider,
        int priority = 0)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        return _host.Register(
            _extensionId,
            name,
            GameAgentExtensionResourceKind.ModelProvider,
            provider,
            priority,
            unique: true);
    }

    public IGameAgentExtensionRegistration RegisterService<T>(
        string name,
        T service,
        int priority = 0)
        where T : class
    {
        if (service is null)
        {
            throw new ArgumentNullException(nameof(service));
        }

        return _host.RegisterService(_extensionId, name, typeof(T), service, priority);
    }

    public IGameAgentExtensionRegistration On<TEvent>(
        GameAgentExtensionEvent<TEvent> eventKey,
        GameAgentExtensionEventHandler<TEvent> handler,
        int priority = 0)
    {
        if (eventKey is null)
        {
            throw new ArgumentNullException(nameof(eventKey));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return _host.RegisterEvent(_extensionId, eventKey, handler, priority);
    }

    public IGameAgentExtensionRegistration Subscribe<TMessage>(
        GameAgentExtensionChannel<TMessage> channel,
        GameAgentExtensionChannelHandler<TMessage> handler,
        int priority = 0)
    {
        if (channel is null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return _host.RegisterChannel(_extensionId, channel, handler, priority);
    }

    public ValueTask PublishAsync<TMessage>(
        GameAgentExtensionChannel<TMessage> channel,
        TMessage message,
        CancellationToken cancellationToken = default) =>
        _host.PublishChannelAsync(
            channel ?? throw new ArgumentNullException(nameof(channel)),
            message,
            cancellationToken);
}

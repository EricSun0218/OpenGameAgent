using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

internal static class GameAgentAsyncBridge
{
    public static void Run(Func<ValueTask> operation)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        Task.Run(async () => await operation().ConfigureAwait(false)).GetAwaiter().GetResult();
    }
}

internal sealed class GameAgentExtensionRunLease
{
    private int _active = 1;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public void EnsureActive()
    {
        if (!IsActive)
        {
            throw new ObjectDisposedException(nameof(GameAgentExtensionRunContext), "The extension run context is no longer active.");
        }
    }

    public void Invalidate() => Interlocked.Exchange(ref _active, 0);
}

internal sealed class GameAgentSessionState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _entries;
    private readonly int _maximumEntries;
    private readonly int _maximumKeyCharacters;
    private readonly int _maximumValueCharacters;
    private readonly int _maximumTotalCharacters;

    public GameAgentSessionState(
        IReadOnlyDictionary<string, string> entries,
        GameRuntimeLimits limits)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        _maximumEntries = limits.MaxExtensionStateEntries;
        _maximumKeyCharacters = limits.MaxExtensionStateKeyCharacters;
        _maximumValueCharacters = limits.MaxExtensionStateValueCharacters;
        _maximumTotalCharacters = limits.MaxExtensionStateCharacters;
        _entries = new Dictionary<string, string>(entries, StringComparer.Ordinal);
        ValidateSnapshot(_entries);
    }

    public IReadOnlyDictionary<string, string> Snapshot(string extensionId)
    {
        var prefix = Prefix(extensionId);
        lock (_gate)
        {
            return new ReadOnlyDictionary<string, string>(
                _entries
                    .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(
                        pair => Uri.UnescapeDataString(pair.Key.Substring(prefix.Length)),
                        pair => pair.Value,
                        StringComparer.Ordinal));
        }
    }

    public IReadOnlyDictionary<string, string> SnapshotAll()
    {
        lock (_gate)
        {
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(_entries, StringComparer.Ordinal));
        }
    }

    public bool TryGet(string extensionId, string key, out string json)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(NamespacedKey(extensionId, key), out json!);
        }
    }

    public void Set(string extensionId, string key, string json)
    {
        var namespaced = NamespacedKey(extensionId, key);
        var valid = GameJson.RequireValid(json, nameof(json));
        if (namespaced.Length > _maximumKeyCharacters)
        {
            throw new GameRuntimeLimitException(
                nameof(GameRuntimeLimits.MaxExtensionStateKeyCharacters),
                "An extension state key is too large.");
        }

        if (valid.Length > _maximumValueCharacters)
        {
            throw new GameRuntimeLimitException(
                nameof(GameRuntimeLimits.MaxExtensionStateValueCharacters),
                "An extension state value is too large.");
        }

        lock (_gate)
        {
            if (!_entries.ContainsKey(namespaced) && _entries.Count >= _maximumEntries)
            {
                throw new GameRuntimeLimitException(
                    nameof(GameRuntimeLimits.MaxExtensionStateEntries),
                    "The session has too many extension state entries.");
            }

            var total = _entries.Sum(pair => (long)pair.Key.Length + pair.Value.Length);
            if (_entries.TryGetValue(namespaced, out var previous))
            {
                total -= namespaced.Length + previous.Length;
            }

            total += namespaced.Length + valid.Length;
            if (total > _maximumTotalCharacters)
            {
                throw new GameRuntimeLimitException(
                    nameof(GameRuntimeLimits.MaxExtensionStateCharacters),
                    "The combined extension state is too large.");
            }

            _entries[namespaced] = valid;
        }
    }

    public bool Remove(string extensionId, string key)
    {
        lock (_gate)
        {
            return _entries.Remove(NamespacedKey(extensionId, key));
        }
    }

    private void ValidateSnapshot(IReadOnlyDictionary<string, string> entries)
    {
        if (entries.Count > _maximumEntries)
        {
            throw new GameRuntimeLimitException(
                nameof(GameRuntimeLimits.MaxExtensionStateEntries),
                "The loaded session has too many extension state entries.");
        }

        var total = 0L;
        foreach (var pair in entries)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > _maximumKeyCharacters)
            {
                throw new GameRuntimeLimitException(
                    nameof(GameRuntimeLimits.MaxExtensionStateKeyCharacters),
                    "The loaded session has an invalid extension state key.");
            }

            if (pair.Value is null || pair.Value.Length > _maximumValueCharacters)
            {
                throw new GameRuntimeLimitException(
                    nameof(GameRuntimeLimits.MaxExtensionStateValueCharacters),
                    "The loaded session has an invalid extension state value.");
            }

            GameJson.RequireValid(pair.Value, nameof(entries));
            total += pair.Key.Length + pair.Value.Length;
            if (total > _maximumTotalCharacters)
            {
                throw new GameRuntimeLimitException(
                    nameof(GameRuntimeLimits.MaxExtensionStateCharacters),
                    "The loaded session extension state is too large.");
            }
        }
    }

    private static string Prefix(string extensionId) =>
        Uri.EscapeDataString(GameJson.RequireId(extensionId, nameof(extensionId))) + ":";

    private static string NamespacedKey(string extensionId, string key) =>
        Prefix(extensionId) + Uri.EscapeDataString(GameJson.RequireId(key, nameof(key)));
}

internal sealed class GameAgentExtensionHost : IGameAgentServiceProvider, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<Registration> _registrations = new();
    private readonly List<GameAgentExtensionDiagnostic> _diagnostics = new();
    private readonly List<IGameAgentExtension> _extensions = new();
    private readonly HashSet<string> _extensionIds = new(StringComparer.Ordinal);
    private readonly int _maximumExtensions;
    private readonly int _maximumResources;
    private readonly int _maximumDiagnostics;
    private readonly int _maximumDiagnosticCharacters;
    private long _nextSequence;
    private bool _disposed;

    public GameAgentExtensionHost(
        IEnumerable<IGameAgentExtension> extensions,
        GameRuntimeLimits? limits = null)
    {
        if (extensions is null)
        {
            throw new ArgumentNullException(nameof(extensions));
        }

        var validatedLimits = (limits ?? new GameRuntimeLimits()).CopyAndValidate();
        _maximumExtensions = validatedLimits.MaxExtensions;
        _maximumResources = validatedLimits.MaxExtensionResources;
        _maximumDiagnostics = validatedLimits.MaxExtensionDiagnostics;
        _maximumDiagnosticCharacters = validatedLimits.MaxExtensionDiagnosticCharacters;

        try
        {
            foreach (var extension in extensions)
            {
                AddExtension(extension ?? throw new ArgumentException("An extension cannot be null.", nameof(extensions)));
            }
        }
        catch
        {
            try
            {
                GameAgentAsyncBridge.Run(DisposeAsync);
            }
            catch
            {
                // Preserve the extension configuration failure. Cleanup is best effort here.
            }

            throw;
        }
    }

    public bool HasExtensions
    {
        get
        {
            lock (_gate)
            {
                return _extensions.Count > 0;
            }
        }
    }

    public IReadOnlyList<GameAgentExtensionResource> GetResources()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(SnapshotEntriesLocked().Select(entry => entry.Resource).ToArray());
        }
    }

    public IReadOnlyList<GameAgentExtensionDiagnostic> GetDiagnostics()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_diagnostics.ToArray());
        }
    }

    public IGameAgentExtensionRegistration Register<T>(
        string extensionId,
        string name,
        GameAgentExtensionResourceKind kind,
        T value,
        int priority,
        bool unique)
        where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        lock (_gate)
        {
            EnsureActive();
            EnsureKnownExtension(extensionId);
            var resourceName = GameJson.RequireId(name, nameof(name));
            if (unique)
            {
                var conflict = _registrations.FirstOrDefault(
                    entry => entry.Active
                             && entry.Resource.Kind == kind
                             && string.Equals(entry.Resource.Name, resourceName, StringComparison.Ordinal));
                if (conflict is not null)
                {
                    var message = $"{kind} '{resourceName}' is already registered by extension '{conflict.Resource.ExtensionId}'.";
                    AddDiagnosticLocked(new GameAgentExtensionDiagnostic(
                        GameAgentExtensionDiagnosticSeverity.Error,
                        "extension.resource_conflict",
                        message,
                        extensionId,
                        resourceName));
                    throw new InvalidOperationException(message);
                }
            }

            EnsureResourceCapacityLocked();

            var resource = new GameAgentExtensionResource(
                extensionId,
                resourceName,
                kind,
                priority,
                checked(++_nextSequence));
            var registration = new Registration(this, resource, value, serviceType: null, eventKey: null);
            _registrations.Add(registration);
            return registration;
        }
    }

    public IGameAgentExtensionRegistration RegisterService(
        string extensionId,
        string name,
        Type serviceType,
        object service,
        int priority)
    {
        if (serviceType is null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        lock (_gate)
        {
            EnsureActive();
            EnsureKnownExtension(extensionId);
            var resourceName = GameJson.RequireId(name, nameof(name));
            var conflict = _registrations.FirstOrDefault(
                entry => entry.Active
                         && entry.Resource.Kind == GameAgentExtensionResourceKind.Service
                         && entry.ServiceType == serviceType
                         && string.Equals(entry.Resource.Name, resourceName, StringComparison.Ordinal));
            if (conflict is not null)
            {
                var message = $"Service '{resourceName}' for '{serviceType.FullName}' is already registered by extension '{conflict.Resource.ExtensionId}'.";
                AddDiagnosticLocked(new GameAgentExtensionDiagnostic(
                    GameAgentExtensionDiagnosticSeverity.Error,
                    "extension.service_conflict",
                    message,
                    extensionId,
                    resourceName));
                throw new InvalidOperationException(message);
            }

            EnsureResourceCapacityLocked();

            var resource = new GameAgentExtensionResource(
                extensionId,
                resourceName,
                GameAgentExtensionResourceKind.Service,
                priority,
                checked(++_nextSequence));
            var registration = new Registration(this, resource, service, serviceType, eventKey: null);
            _registrations.Add(registration);
            return registration;
        }
    }

    public IGameAgentExtensionRegistration RegisterEvent<TEvent>(
        string extensionId,
        GameAgentExtensionEvent<TEvent> eventKey,
        GameAgentExtensionEventHandler<TEvent> handler,
        int priority)
    {
        lock (_gate)
        {
            EnsureActive();
            EnsureKnownExtension(extensionId);
            EnsureResourceCapacityLocked();
            var resource = new GameAgentExtensionResource(
                extensionId,
                eventKey.Name,
                GameAgentExtensionResourceKind.EventHandler,
                priority,
                checked(++_nextSequence));
            var registration = new Registration(this, resource, handler, serviceType: null, eventKey);
            _registrations.Add(registration);
            return registration;
        }
    }

    public IGameAgentExtensionRegistration RegisterChannel<TMessage>(
        string extensionId,
        GameAgentExtensionChannel<TMessage> channel,
        GameAgentExtensionChannelHandler<TMessage> handler,
        int priority)
    {
        lock (_gate)
        {
            EnsureActive();
            EnsureKnownExtension(extensionId);
            EnsureResourceCapacityLocked();
            var resource = new GameAgentExtensionResource(
                extensionId,
                channel.Name,
                GameAgentExtensionResourceKind.EventHandler,
                priority,
                checked(++_nextSequence));
            var registration = new Registration(this, resource, handler, serviceType: null, channel);
            _registrations.Add(registration);
            return registration;
        }
    }

    public bool TryGet<T>(string name, out T service) where T : class
    {
        var resourceName = GameJson.RequireId(name, nameof(name));
        lock (_gate)
        {
            var match = SnapshotEntriesLocked().FirstOrDefault(
                entry => entry.Resource.Kind == GameAgentExtensionResourceKind.Service
                         && entry.ServiceType == typeof(T)
                         && string.Equals(entry.Resource.Name, resourceName, StringComparison.Ordinal));
            service = match?.Value as T ?? null!;
            return match is not null;
        }
    }

    public T GetRequired<T>(string name) where T : class =>
        TryGet<T>(name, out var service)
            ? service
            : throw new KeyNotFoundException($"Service '{name}' for '{typeof(T).FullName}' is not registered.");

    public IReadOnlyList<IGameWorkflow> GetWorkflows() =>
        GetValues<IGameWorkflow>(GameAgentExtensionResourceKind.Workflow);

    public string ComposePrompt(string instructions)
    {
        var fragments = GetValues<string>(GameAgentExtensionResourceKind.PromptFragment)
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join("\n\n", new[] { instructions ?? string.Empty }.Concat(fragments).Where(value => value.Length > 0));
    }

    public IModelProvider ResolveModelProvider(string? providerName, IModelProvider fallback)
    {
        if (providerName is null)
        {
            return fallback;
        }

        var entries = GetEntries(GameAgentExtensionResourceKind.ModelProvider);
        var match = entries.FirstOrDefault(
            entry => string.Equals(entry.Resource.Name, providerName, StringComparison.Ordinal));
        return match?.Value as IModelProvider
            ?? throw new KeyNotFoundException($"Model provider '{providerName}' is not registered.");
    }

    public async ValueTask<IReadOnlyList<GameContextSlice>> CollectContextAsync(
        GameAgentExtensionRunContext baseContext,
        IReadOnlyList<GameContextSlice> initial,
        CancellationToken cancellationToken)
    {
        var values = new List<GameContextSlice>(initial);
        foreach (var entry in GetEntries(GameAgentExtensionResourceKind.ContextProvider))
        {
            var provider = (GameExtensionContextProvider)entry.Value;
            var contributed = await provider(ForOwner(baseContext, entry.Resource.ExtensionId), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Context provider '{entry.Resource.Name}' returned null.");
            if (contributed.Any(value => value is null))
            {
                throw new InvalidOperationException($"Context provider '{entry.Resource.Name}' returned a null slice.");
            }

            values.AddRange(contributed);
        }

        return Array.AsReadOnly(values.ToArray());
    }

    public async ValueTask<IReadOnlyList<AgentTool>> CollectToolsAsync(
        GameAgentExtensionRunContext baseContext,
        IReadOnlyList<AgentTool> initial,
        CancellationToken cancellationToken)
    {
        var values = new List<(AgentTool Tool, string Owner)>(
            initial.Select(tool => (tool, "runtime")));
        foreach (var entry in GetEntries(GameAgentExtensionResourceKind.Tool))
        {
            values.Add(((AgentTool)entry.Value, entry.Resource.ExtensionId));
        }

        foreach (var entry in GetEntries(GameAgentExtensionResourceKind.ToolProvider))
        {
            var provider = (GameExtensionToolProvider)entry.Value;
            var contributed = await provider(ForOwner(baseContext, entry.Resource.ExtensionId), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tool provider '{entry.Resource.Name}' returned null.");
            if (contributed.Any(value => value is null))
            {
                throw new InvalidOperationException($"Tool provider '{entry.Resource.Name}' returned a null tool.");
            }

            values.AddRange(contributed.Select(tool => (tool, entry.Resource.ExtensionId)));
        }

        var duplicate = values.GroupBy(value => value.Tool.Definition.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            var owners = string.Join(", ", duplicate.Select(value => value.Owner).Distinct(StringComparer.Ordinal));
            var message = $"Tool '{duplicate.Key}' was contributed more than once ({owners}).";
            AddDiagnostic(new GameAgentExtensionDiagnostic(
                GameAgentExtensionDiagnosticSeverity.Error,
                "extension.tool_conflict",
                message,
                resourceName: duplicate.Key));
            throw new InvalidOperationException(message);
        }

        var policies = GetEntries(GameAgentExtensionResourceKind.ToolVisibilityPolicy);
        if (policies.Count == 0)
        {
            return Array.AsReadOnly(values.Select(value => value.Tool).ToArray());
        }

        var visible = new List<AgentTool>(values.Count);
        foreach (var value in values)
        {
            var accepted = true;
            foreach (var entry in policies)
            {
                var policy = (GameExtensionToolVisibilityPolicy)entry.Value;
                var policyContext = new GameToolVisibilityContext(
                    ForOwner(baseContext, entry.Resource.ExtensionId),
                    value.Tool.Definition,
                    value.Tool.Risk,
                    value.Owner);
                if (!await policy(policyContext, cancellationToken).ConfigureAwait(false))
                {
                    accepted = false;
                    break;
                }
            }

            if (accepted)
            {
                visible.Add(value.Tool);
            }
        }

        return Array.AsReadOnly(visible.ToArray());
    }

    public async ValueTask<IReadOnlyList<GameSkill>> CollectSkillsAsync(
        GameAgentExtensionRunContext baseContext,
        IReadOnlyList<GameSkill> initial,
        IReadOnlyCollection<string> activeToolNames,
        int maximumSkills,
        CancellationToken cancellationToken)
    {
        var values = new List<GameSkill>(initial);
        foreach (var entry in GetEntries(GameAgentExtensionResourceKind.SkillProvider))
        {
            var provider = (GameExtensionSkillProvider)entry.Value;
            var remaining = Math.Max(0, maximumSkills - values.Count);
            if (remaining == 0)
            {
                break;
            }

            var contributed = await provider(
                ForOwner(baseContext, entry.Resource.ExtensionId),
                activeToolNames,
                remaining,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Skill provider '{entry.Resource.Name}' returned null.");
            if (contributed.Any(value => value is null))
            {
                throw new InvalidOperationException($"Skill provider '{entry.Resource.Name}' returned a null skill.");
            }

            values.AddRange(contributed);
        }

        var duplicate = values.GroupBy(value => value.SkillId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Skill '{duplicate.Key}' was contributed more than once.");
        }

        return Array.AsReadOnly(values.ToArray());
    }

    public async ValueTask<bool> HasPendingWorkAsync(
        GameAgentExtensionRunContext baseContext,
        bool initial,
        CancellationToken cancellationToken)
    {
        if (initial)
        {
            return true;
        }

        foreach (var entry in GetEntries(GameAgentExtensionResourceKind.PendingWorkProvider))
        {
            if (await ((GameExtensionPendingWorkProvider)entry.Value)(
                    ForOwner(baseContext, entry.Resource.ExtensionId),
                    cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask<GameRouteDecision?> SelectRouteAsync(
        GameAgentExtensionRunContext baseContext,
        int availableToolCount,
        bool hasPendingWork,
        CancellationToken cancellationToken)
    {
        foreach (var entry in GetEntries(GameAgentExtensionResourceKind.RouteRule))
        {
            var decision = await ((GameExtensionRouteRule)entry.Value)(
                ForOwner(baseContext, entry.Resource.ExtensionId),
                availableToolCount,
                hasPendingWork,
                cancellationToken).ConfigureAwait(false);
            if (decision is not null)
            {
                return decision;
            }
        }

        return null;
    }

    public AgentHooks ComposeHooks(GameAgentExtensionRunContext baseContext, AgentHooks baseline)
    {
        var hooks = GetEntries(GameAgentExtensionResourceKind.AgentHooks)
            .Select(entry => ((GameExtensionHookFactory)entry.Value)(ForOwner(baseContext, entry.Resource.ExtensionId)))
            .ToList();
        if (baseline is not null)
        {
            hooks.Add(baseline);
        }

        if (hooks.Any(value => value is null))
        {
            throw new InvalidOperationException("An extension hook factory returned null.");
        }

        return AgentHookComposer.Compose(hooks);
    }

    public async ValueTask PublishAsync<TEvent>(
        GameAgentExtensionEvent<TEvent> eventKey,
        TEvent value,
        GameAgentExtensionRunContext baseContext,
        CancellationToken cancellationToken)
    {
        var handlers = GetEntries(GameAgentExtensionResourceKind.EventHandler)
            .Where(entry => ReferenceEquals(entry.EventKey, eventKey))
            .ToArray();
        foreach (var entry in handlers)
        {
            try
            {
                await ((GameAgentExtensionEventHandler<TEvent>)entry.Value)(
                    value,
                    ForOwner(baseContext, entry.Resource.ExtensionId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                AddDiagnostic(new GameAgentExtensionDiagnostic(
                    GameAgentExtensionDiagnosticSeverity.Error,
                    "extension.event_handler_failed",
                    $"Handler for '{eventKey.Name}' failed: {exception.Message}",
                    entry.Resource.ExtensionId,
                    eventKey.Name));
            }
        }
    }

    public async ValueTask PublishChannelAsync<TMessage>(
        GameAgentExtensionChannel<TMessage> channel,
        TMessage message,
        CancellationToken cancellationToken)
    {
        if (channel is null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        Registration[] handlers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            handlers = SnapshotEntriesLocked()
                .Where(entry => entry.Resource.Kind == GameAgentExtensionResourceKind.EventHandler)
                .Where(entry => ReferenceEquals(entry.EventKey, channel))
                .ToArray();
        }
        foreach (var entry in handlers)
        {
            try
            {
                await ((GameAgentExtensionChannelHandler<TMessage>)entry.Value)(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                AddDiagnostic(new GameAgentExtensionDiagnostic(
                    GameAgentExtensionDiagnosticSeverity.Error,
                    "extension.channel_handler_failed",
                    $"Handler for channel '{channel.Name}' failed: {exception.Message}",
                    entry.Resource.ExtensionId,
                    channel.Name));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        IGameAgentExtension[] extensions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var registration in _registrations)
            {
                registration.Deactivate();
            }

            extensions = _extensions.AsEnumerable().Reverse().ToArray();
        }

        var failures = new List<Exception>();
        foreach (var extension in extensions)
        {
            try
            {
                if (extension is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (extension is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("One or more game agent extensions failed during disposal.", failures);
        }
    }

    private void AddExtension(IGameAgentExtension extension)
    {
        var descriptor = extension.Descriptor
            ?? throw new InvalidOperationException("An extension returned a null descriptor.");
        lock (_gate)
        {
            EnsureActive();
            if (_extensions.Count >= _maximumExtensions)
            {
                throw new GameRuntimeLimitException(
                    nameof(GameRuntimeLimits.MaxExtensions),
                    "The runtime reached its extension limit.");
            }

            if (!_extensionIds.Add(descriptor.Id))
            {
                throw new InvalidOperationException($"Extension '{descriptor.Id}' is registered more than once.");
            }

            _extensions.Add(extension);
        }

        try
        {
            extension.Configure(new GameAgentExtensionApi(this, descriptor.Id));
        }
        catch
        {
            lock (_gate)
            {
                foreach (var registration in _registrations.Where(
                             value => string.Equals(value.Resource.ExtensionId, descriptor.Id, StringComparison.Ordinal)))
                {
                    registration.Deactivate();
                }

                _registrations.RemoveAll(value =>
                    string.Equals(value.Resource.ExtensionId, descriptor.Id, StringComparison.Ordinal));

                _extensions.Remove(extension);
                _extensionIds.Remove(descriptor.Id);
            }

            try
            {
                if (extension is IAsyncDisposable asyncDisposable)
                {
                    GameAgentAsyncBridge.Run(asyncDisposable.DisposeAsync);
                }
                else if (extension is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // Preserve the extension configuration failure. Cleanup is best effort here.
            }

            throw;
        }
    }

    private GameAgentExtensionRunContext ForOwner(GameAgentExtensionRunContext context, string extensionId) =>
        CreateOwnerContext(context, extensionId);

    private GameAgentExtensionRunContext CreateOwnerContext(
        GameAgentExtensionRunContext context,
        string extensionId)
    {
        context.EnsureActive();
        return new GameAgentExtensionRunContext(
            context.Input,
            context.Session,
            context.ExecutionScope,
            context.SessionState,
            new GameAgentExtensionState(context.SessionState, extensionId, context.Lease),
            context.Lease,
            this,
            GetResources());
    }

    internal GameAgentExtensionRunContext CreateRunContext(
        GameInput input,
        GameSessionSnapshot session,
        GameAgentSessionState state,
        GameExecutionScope executionScope)
    {
        var lease = new GameAgentExtensionRunLease();
        return new GameAgentExtensionRunContext(
            input,
            session,
            executionScope,
            state,
            new GameAgentExtensionState(state, "runtime", lease),
            lease,
            this,
            GetResources());
    }

    private IReadOnlyList<T> GetValues<T>(GameAgentExtensionResourceKind kind) where T : class =>
        Array.AsReadOnly(GetEntries(kind).Select(entry => (T)entry.Value).ToArray());

    private IReadOnlyList<Registration> GetEntries(GameAgentExtensionResourceKind kind)
    {
        lock (_gate)
        {
            return Array.AsReadOnly(SnapshotEntriesLocked().Where(entry => entry.Resource.Kind == kind).ToArray());
        }
    }

    private List<Registration> SnapshotEntriesLocked() =>
        _registrations
            .Where(entry => entry.Active)
            .OrderByDescending(entry => entry.Resource.Priority)
            .ThenBy(entry => entry.Resource.Sequence)
            .ToList();

    private void AddDiagnostic(GameAgentExtensionDiagnostic diagnostic)
    {
        lock (_gate)
        {
            AddDiagnosticLocked(diagnostic);
        }
    }

    private void AddDiagnosticLocked(GameAgentExtensionDiagnostic diagnostic)
    {
        if (_maximumDiagnostics == 0)
        {
            return;
        }

        var bounded = diagnostic.Message.Length <= _maximumDiagnosticCharacters
            ? diagnostic
            : new GameAgentExtensionDiagnostic(
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Message.Substring(0, _maximumDiagnosticCharacters),
                diagnostic.ExtensionId,
                diagnostic.ResourceName);
        if (_diagnostics.Count >= _maximumDiagnostics)
        {
            _diagnostics.RemoveAt(0);
        }

        _diagnostics.Add(bounded);
    }

    private void EnsureResourceCapacityLocked()
    {
        if (_registrations.Count(entry => entry.Active) >= _maximumResources)
        {
            throw new GameRuntimeLimitException(
                nameof(GameRuntimeLimits.MaxExtensionResources),
                "The runtime reached its extension resource limit.");
        }
    }

    private void Remove(Registration registration)
    {
        lock (_gate)
        {
            registration.Deactivate();
            _registrations.Remove(registration);
        }
    }

    private void EnsureKnownExtension(string extensionId)
    {
        if (!_extensionIds.Contains(extensionId))
        {
            throw new InvalidOperationException($"Extension '{extensionId}' is not active.");
        }
    }

    private void EnsureActive()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GameAgentExtensionHost));
        }
    }

    private sealed class Registration : IGameAgentExtensionRegistration
    {
        private readonly GameAgentExtensionHost _owner;
        private int _active = 1;

        public Registration(
            GameAgentExtensionHost owner,
            GameAgentExtensionResource resource,
            object value,
            Type? serviceType,
            object? eventKey)
        {
            _owner = owner;
            Resource = resource;
            Value = value;
            ServiceType = serviceType;
            EventKey = eventKey;
        }

        public GameAgentExtensionResource Resource { get; }

        public object Value { get; }

        public Type? ServiceType { get; }

        public object? EventKey { get; }

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public bool Active => IsActive;

        public void Dispose() => _owner.Remove(this);

        public void Deactivate() => Interlocked.Exchange(ref _active, 0);
    }
}

internal static class AgentHookComposer
{
    public static AgentHooks Compose(IReadOnlyList<AgentHooks> hooks)
    {
        if (hooks is null)
        {
            throw new ArgumentNullException(nameof(hooks));
        }

        return new AgentHooks
        {
            TransformContextAsync = hooks.Any(hook => hook.TransformContextAsync is not null)
                ? async (messages, cancellationToken) =>
                {
                    var current = messages;
                    foreach (var hook in hooks)
                    {
                        if (hook.TransformContextAsync is not null)
                        {
                            current = await hook.TransformContextAsync(current, cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("An extension context transform returned null.");
                        }
                    }

                    return current;
                }
            : null,
            BeforeModelRequestAsync = hooks.Any(hook => hook.BeforeModelRequestAsync is not null)
                ? async (request, cancellationToken) =>
                {
                    var current = request;
                    foreach (var hook in hooks)
                    {
                        if (hook.BeforeModelRequestAsync is not null)
                        {
                            current = await hook.BeforeModelRequestAsync(current, cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("An extension model request transform returned null.");
                        }
                    }

                    return current;
                }
            : null,
            ShouldStopAfterTurnAsync = hooks.Any(hook => hook.ShouldStopAfterTurnAsync is not null)
                ? async (context, cancellationToken) =>
                {
                    foreach (var hook in hooks)
                    {
                        if (hook.ShouldStopAfterTurnAsync is not null
                            && await hook.ShouldStopAfterTurnAsync(context, cancellationToken).ConfigureAwait(false))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            : null,
            PrepareNextTurnAsync = hooks.Any(hook => hook.PrepareNextTurnAsync is not null)
                ? async (context, cancellationToken) =>
                {
                    NextTurnUpdate? combined = null;
                    var modelTargetClaimed = false;
                    foreach (var hook in hooks)
                    {
                        if (hook.PrepareNextTurnAsync is null)
                        {
                            continue;
                        }

                        var update = await hook.PrepareNextTurnAsync(context, cancellationToken).ConfigureAwait(false);
                        if (update is null)
                        {
                            continue;
                        }

                        combined ??= new NextTurnUpdate();
                        combined.Context ??= update.Context;
                        if (!modelTargetClaimed && (update.Provider is not null || update.Model is not null))
                        {
                            combined.Provider = update.Provider;
                            combined.Model = update.Model;
                            modelTargetClaimed = true;
                        }

                        combined.Parameters ??= update.Parameters;
                    }

                    return combined;
                }
            : null,
            BeforeToolCallAsync = hooks.Any(hook => hook.BeforeToolCallAsync is not null)
                ? async (hookContext, cancellationToken) =>
                {
                    var current = hookContext.ToolCall;
                    var replaced = false;
                    foreach (var hook in hooks)
                    {
                        if (hook.BeforeToolCallAsync is null)
                        {
                            continue;
                        }

                        using var argumentsDocument = JsonDocument.Parse(current.ArgumentsJson);
                        var decision = await hook.BeforeToolCallAsync(
                            new BeforeToolCallContext(
                                hookContext.RunId,
                                hookContext.Turn,
                                hookContext.AssistantMessage,
                                current,
                                argumentsDocument.RootElement,
                                hookContext.Context),
                            cancellationToken).ConfigureAwait(false);
                        if (decision?.Blocked == true)
                        {
                            return decision;
                        }

                        if (decision?.ReplacementArgumentsJson is not null)
                        {
                            var tool = hookContext.Context.Tools.FirstOrDefault(candidate =>
                                string.Equals(candidate.Definition.Name, current.Name, StringComparison.Ordinal))
                                ?? throw new InvalidOperationException($"Tool '{current.Name}' is not available during hook composition.");
                            var validationError = tool.ValidateArguments(decision.ReplacementArgumentsJson);
                            if (validationError is not null)
                            {
                                throw new InvalidOperationException("Invalid tool arguments: " + validationError);
                            }

                            current = new ToolCallContent(current.Id, current.Name, decision.ReplacementArgumentsJson);
                            replaced = true;
                        }
                    }

                    return replaced ? ToolCallDecision.Allow(current.ArgumentsJson) : null;
                }
            : null,
            AuthorizeToolCallAsync = hooks.Any(hook => hook.AuthorizeToolCallAsync is not null)
                ? async (hookContext, cancellationToken) =>
                {
                    foreach (var hook in hooks)
                    {
                        if (hook.AuthorizeToolCallAsync is null)
                        {
                            continue;
                        }

                        var decision = await hook.AuthorizeToolCallAsync(hookContext, cancellationToken).ConfigureAwait(false);
                        if (decision?.ReplacementArgumentsJson is not null)
                        {
                            throw new InvalidOperationException("Final tool authorizers cannot rewrite arguments.");
                        }

                        if (decision?.Blocked == true)
                        {
                            return decision;
                        }
                    }

                    return null;
                }
            : null,
            AfterToolCallAsync = hooks.Any(hook => hook.AfterToolCallAsync is not null)
                ? async (hookContext, cancellationToken) =>
                {
                    var current = hookContext.Result;
                    foreach (var hook in hooks)
                    {
                        if (hook.AfterToolCallAsync is not null)
                        {
                            current = await hook.AfterToolCallAsync(
                                    new AfterToolCallContext(
                                        hookContext.RunId,
                                        hookContext.Turn,
                                        hookContext.AssistantMessage,
                                        hookContext.ToolCall,
                                        hookContext.Arguments,
                                        current,
                                        hookContext.Context),
                                    cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("An extension tool result transform returned null.");
                        }
                    }

                    return current;
                }
            : null,
        };
    }
}

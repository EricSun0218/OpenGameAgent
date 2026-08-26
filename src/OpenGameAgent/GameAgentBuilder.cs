using System;
using OpenGameAgent.Kernel;

namespace OpenGameAgent;

/// <summary>
/// Primary composition API for an in-engine or server-hosted runtime.
/// </summary>
public sealed class GameAgentBuilder
{
    private readonly GameAgentRuntimeOptions _options;
    private bool _built;

    public GameAgentBuilder(IModelProvider provider, string model)
    {
        _options = new GameAgentRuntimeOptions(
            provider ?? throw new ArgumentNullException(nameof(provider)),
            model);
    }

    public GameAgentBuilder Configure(Action<GameAgentRuntimeOptions> configure)
    {
        EnsureMutable();
        (configure ?? throw new ArgumentNullException(nameof(configure)))(_options);
        return this;
    }

    public GameAgentBuilder UseInstructions(string instructions)
    {
        EnsureMutable();
        _options.Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        return this;
    }

    public GameAgentBuilder UseSessionStore(IGameSessionStore store)
    {
        EnsureMutable();
        _options.SessionStore = store ?? throw new ArgumentNullException(nameof(store));
        return this;
    }

    public GameAgentBuilder UseModelSelector(GameModelSelector selector)
    {
        EnsureMutable();
        _options.ModelSelector = selector ?? throw new ArgumentNullException(nameof(selector));
        return this;
    }

    public GameAgentBuilder UseExecutionScope(GameExecutionScope scope)
    {
        EnsureMutable();
        var configured = scope ?? throw new ArgumentNullException(nameof(scope));
        _options.ExecutionScopeProvider = (_, _) => new ValueTask<GameExecutionScope>(configured);
        return this;
    }

    public GameAgentBuilder UseExecutionScope(GameExecutionScopeProvider provider)
    {
        EnsureMutable();
        _options.ExecutionScopeProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        return this;
    }

    public GameAgentBuilder UseExtension(IGameAgentExtension extension)
    {
        EnsureMutable();
        _options.Extensions.Add(extension ?? throw new ArgumentNullException(nameof(extension)));
        return this;
    }

    public GameAgentBuilder UseExtension(
        string id,
        string version,
        Action<GameAgentExtensionApi> configure,
        string? description = null)
    {
        return UseExtension(new DelegateGameAgentExtension(
            new GameAgentExtensionDescriptor(id, version, description),
            configure));
    }

    public GameAgentRuntime Build()
    {
        EnsureMutable();
        _built = true;
        return new GameAgentRuntime(_options);
    }

    private void EnsureMutable()
    {
        if (_built)
        {
            throw new InvalidOperationException("A game agent builder can build only one runtime.");
        }
    }
}

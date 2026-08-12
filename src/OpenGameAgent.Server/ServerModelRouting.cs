using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.OpenAICompatible;

namespace OpenGameAgent.Server;

public delegate ValueTask<string> GameAgentServerModelRouteSelector(
    GameInput input,
    CancellationToken cancellationToken);

/// <summary>
/// A server-owned model target. Provider transports and credentials are supplied by the host and
/// are never read from a game request.
/// </summary>
public sealed class GameAgentServerModelTarget
{
    public GameAgentServerModelTarget(
        string providerId,
        string model,
        IModelProvider provider,
        string? apiId = null)
    {
        ProviderId = RequireIdentifier(providerId, nameof(providerId));
        Model = RequireIdentifier(model, nameof(model));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ApiId = apiId is null ? null : RequireIdentifier(apiId, nameof(apiId));
    }

    public string ProviderId { get; }

    public string Model { get; }

    public IModelProvider Provider { get; }

    public string? ApiId { get; }

    private static string RequireIdentifier(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded model target identifier is required.", parameterName)
            : value;
}

public sealed class GameAgentServerModelRoute
{
    public GameAgentServerModelRoute(string name, IEnumerable<GameAgentServerModelTarget> targets)
    {
        Name = ServerModelRoutingValidation.RequireIdentifier(name, nameof(name));
        var copy = targets?.ToArray() ?? throw new ArgumentNullException(nameof(targets));
        if (copy.Length == 0 || copy.Length > 16 || copy.Any(target => target is null))
        {
            throw new ArgumentException("A model route requires between one and sixteen targets.", nameof(targets));
        }

        if (copy.Select(target => target.ProviderId).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("A model route cannot repeat a provider target.", nameof(targets));
        }

        Targets = Array.AsReadOnly(copy);
    }

    public string Name { get; }

    public IReadOnlyList<GameAgentServerModelTarget> Targets { get; }
}

/// <summary>
/// Resolves only host-registered routes. The selector may inspect trusted game input, but its
/// result is always checked against the immutable route allowlist before a provider is called.
/// </summary>
public sealed class TrustedGameAgentServerModelRouter
{
    private readonly IReadOnlyDictionary<string, RouteRuntime> _routes;
    private readonly GameAgentServerModelRouteSelector _selector;

    public TrustedGameAgentServerModelRouter(
        IEnumerable<GameAgentServerModelRoute> routes,
        string defaultRouteName,
        GameAgentServerModelRouteSelector? selector = null)
    {
        var copy = routes?.ToArray() ?? throw new ArgumentNullException(nameof(routes));
        if (copy.Length == 0 || copy.Length > 128 || copy.Any(route => route is null))
        {
            throw new ArgumentException("Between one and 128 trusted model routes are required.", nameof(routes));
        }

        var duplicate = copy.GroupBy(route => route.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate trusted model route '{duplicate.Key}'.", nameof(routes));
        }

        DefaultRouteName = ServerModelRoutingValidation.RequireIdentifier(defaultRouteName, nameof(defaultRouteName));
        _routes = new ReadOnlyDictionary<string, RouteRuntime>(copy.ToDictionary(
            route => route.Name,
            route => new RouteRuntime(route),
            StringComparer.Ordinal));
        if (!_routes.ContainsKey(DefaultRouteName))
        {
            throw new ArgumentException("The default model route is not registered.", nameof(defaultRouteName));
        }

        _selector = selector ?? ((_, _) => new ValueTask<string>(DefaultRouteName));
    }

    public string DefaultRouteName { get; }

    public IReadOnlyCollection<string> RouteNames => Array.AsReadOnly(_routes.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());

    public string DefaultModel => _routes[DefaultRouteName].Model;

    public IModelProvider DefaultProvider => _routes[DefaultRouteName].Provider;

    public async ValueTask<GameModelSelection?> SelectAsync(
        GameInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = await _selector(input, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selected)
            || !_routes.TryGetValue(selected, out var route))
        {
            throw new InvalidOperationException("The server model policy selected an unregistered route.");
        }

        return new GameModelSelection(route.Model, provider: route.Provider);
    }

    private sealed class RouteRuntime
    {
        public RouteRuntime(GameAgentServerModelRoute route)
        {
            Model = route.Targets[0].Model;
            var candidates = route.Targets.Select(target => (IModelProvider)new BoundTargetProvider(target)).ToArray();
            Provider = candidates.Length == 1 ? candidates[0] : new FallbackModelProvider(candidates);
        }

        public string Model { get; }

        public IModelProvider Provider { get; }
    }

    private sealed class BoundTargetProvider : IModelProvider
    {
        private readonly GameAgentServerModelTarget _target;

        public BoundTargetProvider(GameAgentServerModelTarget target)
        {
            _target = target;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var routed = new ModelRequest(
                _target.Model,
                request.SystemPrompt,
                request.Messages,
                request.Tools,
                request.Parameters,
                request.SessionId,
                request.RunId,
                request.Turn);
            await foreach (var streamEvent in _target.Provider.StreamAsync(routed, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (streamEvent is null)
                {
                    throw new InvalidOperationException("A routed model provider emitted a null event.");
                }

                yield return Project(streamEvent);
            }
        }

        private ModelStreamEvent Project(ModelStreamEvent streamEvent)
        {
            if (streamEvent.IsTerminal)
            {
                return ModelStreamEvent.Terminal(Project(streamEvent.Response
                    ?? throw new InvalidOperationException("A terminal model event is missing its response.")));
            }

            return ModelStreamEvent.Update(
                streamEvent.Kind,
                Project(streamEvent.Partial
                    ?? throw new InvalidOperationException("A model update is missing its partial response.")),
                streamEvent.Delta,
                streamEvent.ContentIndex,
                streamEvent.ToolCallId,
                streamEvent.ToolName,
                streamEvent.ToolCall,
                streamEvent.Content);
        }

        private ModelResponse Project(ModelResponse response) => new(
            response.Content,
            response.StopReason,
            response.Usage,
            response.ErrorMessage,
            _target.ProviderId,
            response.Api ?? _target.ApiId,
            response.ResponseModel ?? _target.Model,
            response.ResponseId,
            response.RawStopReason,
            response.EndTurn,
            response.Diagnostics,
            response.Deferred);
    }
}

/// <summary>
/// Builds the stock server's OpenAI-compatible route allowlist from configuration. All endpoint,
/// header, and credential values come from server configuration; requests can select only through
/// the configured input-type policy.
/// </summary>
public static class StockGameAgentModelRouting
{
    public static TrustedGameAgentServerModelRouter Create(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        var section = configuration.GetSection("OpenGameAgent:ModelRoutes");
        var routeSections = section.GetChildren().ToArray();
        if (routeSections.Length == 0 || routeSections.Length > 128)
        {
            throw new InvalidOperationException("Configure between one and 128 OpenGameAgent:ModelRoutes entries.");
        }

        var targets = new Dictionary<string, GameAgentServerModelTarget>(StringComparer.Ordinal);
        var fallbackNames = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var routeSection in routeSections)
        {
            var name = ServerModelRoutingValidation.RequireIdentifier(routeSection.Key, "model route name");
            var endpointText = routeSection["Endpoint"]
                ?? throw new InvalidOperationException($"Model route '{name}' requires Endpoint.");
            var model = routeSection["Model"]
                ?? throw new InvalidOperationException($"Model route '{name}' requires Model.");
            var providerId = routeSection["ProviderId"] ?? name;
            var apiId = routeSection["ApiId"] ?? "openai-completions";
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            {
                throw new InvalidOperationException($"Model route '{name}' has an invalid Endpoint.");
            }

            var providerOptions = new OpenAICompatibleProviderOptions(
                httpClientFactory.CreateClient("model:" + name),
                endpoint)
            {
                ApiKey = routeSection["ApiKey"],
                ProviderId = providerId,
                ApiId = apiId,
                AllowInsecureHttp = routeSection.GetValue("AllowInsecureHttp", false),
            };
            targets.Add(name, new GameAgentServerModelTarget(
                providerId,
                model,
                new OpenAICompatibleProvider(providerOptions),
                apiId));
            fallbackNames.Add(
                name,
                routeSection.GetSection("Fallbacks").GetChildren()
                    .Select(item => item.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray());
        }

        var routes = new List<GameAgentServerModelRoute>(targets.Count);
        foreach (var pair in targets)
        {
            var chain = new List<GameAgentServerModelTarget> { pair.Value };
            foreach (var fallbackName in fallbackNames[pair.Key])
            {
                if (!targets.TryGetValue(fallbackName, out var fallback))
                {
                    throw new InvalidOperationException(
                        $"Model route '{pair.Key}' references unknown fallback '{fallbackName}'.");
                }

                chain.Add(fallback);
            }

            routes.Add(new GameAgentServerModelRoute(pair.Key, chain));
        }

        var defaultRoute = configuration["OpenGameAgent:DefaultModelRoute"]
            ?? throw new InvalidOperationException("Configure OpenGameAgent:DefaultModelRoute.");
        var inputRoutes = configuration.GetSection("OpenGameAgent:InputModelRoutes").GetChildren()
            .ToDictionary(
                child => ServerModelRoutingValidation.RequireIdentifier(child.Key, "input model route type"),
                child => ServerModelRoutingValidation.RequireIdentifier(
                    child.Value ?? throw new InvalidOperationException($"Input route '{child.Key}' has no model route."),
                    "input model route"),
                StringComparer.Ordinal);
        foreach (var route in inputRoutes.Values)
        {
            if (!targets.ContainsKey(route))
            {
                throw new InvalidOperationException($"An input type references unknown model route '{route}'.");
            }
        }

        return new TrustedGameAgentServerModelRouter(
            routes,
            defaultRoute,
            (input, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<string>(inputRoutes.TryGetValue(input.Type, out var selected)
                    ? selected
                    : defaultRoute);
            });
    }
}

internal static class ServerModelRoutingValidation
{
    public static string RequireIdentifier(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || value.Length > 512
        || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            ? throw new ArgumentException("A bounded identifier is required.", parameterName)
            : value;
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Models;

namespace OpenGameAgent.Media;

public enum GameMediaModelGenerationStatus
{
    Completed,
    Failed,
    Canceled,
}

public sealed class GameMediaModelGenerationResult
{
    internal GameMediaModelGenerationResult(
        string providerId,
        string modelId,
        GameMediaKind kind,
        GameMediaModelGenerationStatus status,
        GameMediaGenerationResult? result,
        string? errorCode,
        string? errorMessage)
    {
        ProviderId = providerId;
        ModelId = modelId;
        Kind = kind;
        Status = status;
        Result = result;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public GameMediaKind Kind { get; }

    public GameMediaModelGenerationStatus Status { get; }

    public GameMediaGenerationResult? Result { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }
}

public enum GameMediaModelRefreshStatus
{
    Updated,
    Unchanged,
    SkippedStatic,
    SkippedUnconfigured,
    StaleRegistration,
    Failed,
    Canceled,
}

public sealed class GameMediaModelRefreshResult
{
    internal GameMediaModelRefreshResult(
        string providerId,
        GameMediaModelRefreshStatus status,
        int modelCount,
        string? errorMessage = null)
    {
        ProviderId = providerId;
        Status = status;
        ModelCount = modelCount;
        ErrorMessage = errorMessage;
    }

    public string ProviderId { get; }

    public GameMediaModelRefreshStatus Status { get; }

    public int ModelCount { get; }

    public string? ErrorMessage { get; }
}

public sealed class GameMediaModelRegistryOptions
{
    public int MaxProviders { get; set; } = 128;

    public int MaxModelsPerProvider { get; set; } = 100_000;

    public int MaxSources { get; set; } = 128;

    public int MaxOutputs { get; set; } = 32;

    public int MaxPromptBytes { get; set; } = 1_000_000;

    public int MaxJsonBytes { get; set; } = 1_000_000;

    public int MaxResourceUriBytes { get; set; } = 8_000_000;

    public int MaxResourceNameBytes { get; set; } = 16_384;

    public int MaxAggregateResourceBytes { get; set; } = 16_000_000;

    public int MaxProgressEvents { get; set; } = 10_000;

    public int MaxErrorCharacters { get; set; } = 65_536;

    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ProgressCallbackTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class GameMediaModelRefreshContext
{
    internal GameMediaModelRefreshContext(
        GameProviderDescriptor provider,
        IReadOnlyList<GameModelDescriptor> currentModels,
        GameProviderAuthResolution? authentication)
    {
        Provider = provider;
        CurrentModels = currentModels;
        Authentication = authentication;
    }

    public GameProviderDescriptor Provider { get; }

    public IReadOnlyList<GameModelDescriptor> CurrentModels { get; }

    public GameProviderAuthResolution? Authentication { get; }
}

public sealed class GameMediaGenerationInvocation
{
    internal GameMediaGenerationInvocation(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        GameProviderAuthResolution? authentication,
        GameMediaGenerationRequest request)
    {
        Provider = provider;
        Model = model;
        Authentication = authentication;
        Request = request;
        Endpoint = authentication?.BaseUrl ?? model.BaseUrl ?? provider.Endpoint;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in model.Headers)
        {
            if (pair.Value is not null)
            {
                headers[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in authentication?.Headers ?? new Dictionary<string, string?>())
        {
            if (pair.Value is null)
            {
                headers.Remove(pair.Key);
            }
            else
            {
                headers[pair.Key] = pair.Value;
            }
        }

        Headers = new ReadOnlyDictionary<string, string>(headers);
        Configuration = authentication?.Configuration
            ?? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    }

    public GameProviderDescriptor Provider { get; }

    public GameModelDescriptor Model { get; }

    public GameProviderAuthResolution? Authentication { get; }

    public GameMediaGenerationRequest Request { get; }

    public Uri? Endpoint { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public IReadOnlyDictionary<string, string> Configuration { get; }
}

public delegate ValueTask<IReadOnlyList<GameModelDescriptor>> GameMediaModelRefresh(
    GameMediaModelRefreshContext context,
    CancellationToken cancellationToken);

public delegate IGameMediaGenerator GameMediaGeneratorFactory(GameMediaGenerationInvocation invocation);

public sealed class GameMediaProviderRegistration
{
    public GameMediaProviderRegistration(
        GameProviderDescriptor descriptor,
        IGameProviderAuthentication authentication,
        GameMediaGeneratorFactory generatorFactory,
        IReadOnlyList<GameModelDescriptor>? models = null,
        GameMediaModelRefresh? refreshModels = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        GeneratorFactory = generatorFactory ?? throw new ArgumentNullException(nameof(generatorFactory));
        if (refreshModels is not null && !descriptor.SupportsDynamicModels)
        {
            throw new ArgumentException(
                "A provider with model refresh must declare dynamic model support.",
                nameof(refreshModels));
        }

        Models = ValidateModels(descriptor.ProviderId, models ?? Array.Empty<GameModelDescriptor>(), 100_000);
        RefreshModels = refreshModels;
    }

    public GameProviderDescriptor Descriptor { get; }

    public IGameProviderAuthentication Authentication { get; }

    public GameMediaGeneratorFactory GeneratorFactory { get; }

    public IReadOnlyList<GameModelDescriptor> Models { get; }

    public GameMediaModelRefresh? RefreshModels { get; }

    internal static IReadOnlyList<GameModelDescriptor> ValidateModels(
        string providerId,
        IReadOnlyList<GameModelDescriptor> models,
        int maximum)
    {
        if (models is null)
        {
            throw new ArgumentNullException(nameof(models));
        }

        var copy = models.ToArray();
        if (copy.Length > maximum)
        {
            throw new ArgumentException("The media provider exposes too many models.", nameof(models));
        }

        if (copy.Any(model => model is null))
        {
            throw new ArgumentException("A media model catalog cannot contain null entries.", nameof(models));
        }

        if (copy.Any(model => !string.Equals(model.ProviderId, providerId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Every media model must belong to its registered provider.", nameof(models));
        }

        if (copy.Any(model => (model.OutputCapabilities & MediaOutputCapabilities) == 0))
        {
            throw new ArgumentException("Every media model must declare image, audio, or video output.", nameof(models));
        }

        var duplicate = copy.GroupBy(model => model.ModelId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException("Media model IDs must be unique within a provider.", nameof(models));
        }

        return Array.AsReadOnly(copy);
    }

    private const GameModelOutputCapabilities MediaOutputCapabilities =
        GameModelOutputCapabilities.Image |
        GameModelOutputCapabilities.Audio |
        GameModelOutputCapabilities.Video;
}

public sealed class GameMediaModelRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _providers = new(StringComparer.Ordinal);
    private readonly int _maxProviders;
    private readonly int _maxModelsPerProvider;
    private readonly int _maxSources;
    private readonly int _maxOutputs;
    private readonly int _maxPromptBytes;
    private readonly int _maxJsonBytes;
    private readonly int _maxResourceUriBytes;
    private readonly int _maxResourceNameBytes;
    private readonly int _maxAggregateResourceBytes;
    private readonly int _maxProgressEvents;
    private readonly int _maxErrorCharacters;
    private readonly TimeSpan _generationTimeout;
    private readonly TimeSpan _refreshTimeout;
    private readonly TimeSpan _progressCallbackTimeout;
    private bool _disposed;

    public GameMediaModelRegistry(GameMediaModelRegistryOptions? options = null)
    {
        options ??= new GameMediaModelRegistryOptions();
        _maxProviders = RequireRange(options.MaxProviders, 1, 100_000, nameof(options.MaxProviders));
        _maxModelsPerProvider = RequireRange(
            options.MaxModelsPerProvider,
            1,
            100_000,
            nameof(options.MaxModelsPerProvider));
        _maxSources = RequireRange(options.MaxSources, 0, 10_000, nameof(options.MaxSources));
        _maxOutputs = RequireRange(options.MaxOutputs, 1, 10_000, nameof(options.MaxOutputs));
        _maxPromptBytes = RequireRange(options.MaxPromptBytes, 1, 100_000_000, nameof(options.MaxPromptBytes));
        _maxJsonBytes = RequireRange(options.MaxJsonBytes, 2, 100_000_000, nameof(options.MaxJsonBytes));
        _maxResourceUriBytes = RequireRange(
            options.MaxResourceUriBytes,
            1,
            100_000_000,
            nameof(options.MaxResourceUriBytes));
        _maxResourceNameBytes = RequireRange(
            options.MaxResourceNameBytes,
            1,
            1_000_000,
            nameof(options.MaxResourceNameBytes));
        _maxAggregateResourceBytes = RequireRange(
            options.MaxAggregateResourceBytes,
            1,
            200_000_000,
            nameof(options.MaxAggregateResourceBytes));
        _maxProgressEvents = RequireRange(
            options.MaxProgressEvents,
            0,
            1_000_000,
            nameof(options.MaxProgressEvents));
        _maxErrorCharacters = RequireRange(
            options.MaxErrorCharacters,
            1,
            65_536,
            nameof(options.MaxErrorCharacters));
        _generationTimeout = RequireDuration(
            options.GenerationTimeout,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromHours(24),
            nameof(options.GenerationTimeout));
        _refreshTimeout = RequireDuration(
            options.RefreshTimeout,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromHours(1),
            nameof(options.RefreshTimeout));
        _progressCallbackTimeout = RequireDuration(
            options.ProgressCallbackTimeout,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(5),
            nameof(options.ProgressCallbackTimeout));
    }

    public void Register(GameMediaProviderRegistration registration, bool replace = false)
    {
        if (registration is null)
        {
            throw new ArgumentNullException(nameof(registration));
        }

        _ = GameMediaProviderRegistration.ValidateModels(
            registration.Descriptor.ProviderId,
            registration.Models,
            _maxModelsPerProvider);
        Entry? replaced = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            var id = registration.Descriptor.ProviderId;
            if (_providers.TryGetValue(id, out var current) && !replace)
            {
                throw new InvalidOperationException($"Media provider '{id}' is already registered.");
            }

            if (current is null && _providers.Count >= _maxProviders)
            {
                throw new InvalidOperationException("The media provider registry reached its capacity.");
            }

            replaced = current;
            _providers[id] = new Entry(registration);
        }

        replaced?.Cancel();
    }

    public bool Unregister(string providerId)
    {
        var id = RequireId(providerId, nameof(providerId));
        Entry? removed;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_providers.Remove(id, out removed))
            {
                return false;
            }
        }

        removed.Cancel();
        return true;
    }

    public IReadOnlyList<GameMediaProviderRegistration> GetProviders()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return Array.AsReadOnly(_providers.Values
                .OrderBy(entry => entry.Registration.Descriptor.ProviderId, StringComparer.Ordinal)
                .Select(entry => entry.Registration)
                .ToArray());
        }
    }

    public GameMediaProviderRegistration? GetProvider(string providerId)
    {
        var id = RequireId(providerId, nameof(providerId));
        lock (_gate)
        {
            ThrowIfDisposed();
            return _providers.TryGetValue(id, out var entry) ? entry.Registration : null;
        }
    }

    public IReadOnlyList<GameModelDescriptor> GetModels(string? providerId = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (providerId is not null)
            {
                var id = RequireId(providerId, nameof(providerId));
                return _providers.TryGetValue(id, out var entry)
                    ? Array.AsReadOnly(entry.CurrentModels.ToArray())
                    : Array.Empty<GameModelDescriptor>();
            }

            return Array.AsReadOnly(_providers.Values
                .OrderBy(entry => entry.Registration.Descriptor.ProviderId, StringComparer.Ordinal)
                .SelectMany(entry => entry.CurrentModels)
                .ToArray());
        }
    }

    public GameModelDescriptor? GetModel(string providerId, string modelId)
    {
        var provider = RequireId(providerId, nameof(providerId));
        var model = RequireId(modelId, nameof(modelId));
        lock (_gate)
        {
            ThrowIfDisposed();
            return _providers.TryGetValue(provider, out var entry)
                ? entry.CurrentModels.FirstOrDefault(candidate =>
                    string.Equals(candidate.ModelId, model, StringComparison.Ordinal))
                : null;
        }
    }

    public ValueTask<GameMediaModelRefreshResult> RefreshAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var safeId = SafeId(providerId);
        if (!TryRequireId(providerId, out var id))
        {
            return new ValueTask<GameMediaModelRefreshResult>(new GameMediaModelRefreshResult(
                safeId,
                GameMediaModelRefreshStatus.Failed,
                0,
                "A valid media provider ID is required."));
        }

        Entry? entry;
        lock (_gate)
        {
            if (_disposed)
            {
                return new ValueTask<GameMediaModelRefreshResult>(new GameMediaModelRefreshResult(
                    id,
                    GameMediaModelRefreshStatus.Failed,
                    0,
                    "The media model registry is disposed."));
            }

            if (!_providers.TryGetValue(id, out entry))
            {
                return new ValueTask<GameMediaModelRefreshResult>(new GameMediaModelRefreshResult(
                    id,
                    GameMediaModelRefreshStatus.Failed,
                    0,
                    "The media provider is not registered."));
            }

            if (entry.Registration.RefreshModels is null)
            {
                return new ValueTask<GameMediaModelRefreshResult>(new GameMediaModelRefreshResult(
                    id,
                    GameMediaModelRefreshStatus.SkippedStatic,
                    entry.CurrentModels.Count));
            }
        }

        var refresh = entry.GetOrStartRefresh(this);
        return new ValueTask<GameMediaModelRefreshResult>(ObserveRefreshAsync(entry, refresh, cancellationToken));
    }

    public async ValueTask<IReadOnlyList<GameMediaModelRefreshResult>> RefreshAsync(
        IReadOnlyCollection<string>? providerIds = null,
        CancellationToken cancellationToken = default)
    {
        string[] ids;
        lock (_gate)
        {
            ThrowIfDisposed();
            ids = (providerIds ?? _providers.Keys.ToArray())
                .Where(id => TryRequireId(id, out _))
                .Distinct(StringComparer.Ordinal)
                .Where(id => _providers.ContainsKey(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .Take(_maxProviders)
                .ToArray();
        }

        var tasks = ids.Select(id => RefreshAsync(id, cancellationToken).AsTask()).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    public async ValueTask<GameMediaModelGenerationResult> GenerateAsync(
        string providerId,
        string modelId,
        GameMediaGenerationRequest request,
        GameMediaProgressHandler? progress = null,
        CancellationToken cancellationToken = default)
    {
        var safeProvider = SafeId(providerId);
        var safeModel = SafeId(modelId);
        var kind = request?.Kind ?? GameMediaKind.Image;
        if (!TryRequireId(providerId, out var provider) || !TryRequireId(modelId, out var model))
        {
            return Failed(safeProvider, safeModel, kind, "invalid_request", "Valid provider and model IDs are required.");
        }

        if (request is null)
        {
            return Failed(provider, model, kind, "invalid_request", "A media generation request is required.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Canceled(provider, model, kind);
        }

        Entry? entry;
        GameModelDescriptor? descriptor;
        lock (_gate)
        {
            if (_disposed)
            {
                return Failed(provider, model, kind, "registry_disposed", "The media model registry is disposed.");
            }

            if (!_providers.TryGetValue(provider, out entry))
            {
                return Failed(provider, model, kind, "provider_not_found", "The media provider is not registered.");
            }

            descriptor = entry.CurrentModels.FirstOrDefault(candidate =>
                string.Equals(candidate.ModelId, model, StringComparison.Ordinal));
            if (descriptor is null)
            {
                return Failed(provider, model, kind, "model_not_found", "The media model is not registered.");
            }
        }

        var capabilityError = ValidateCapabilities(descriptor, request);
        if (capabilityError is not null)
        {
            return Failed(provider, model, kind, "capability_mismatch", capabilityError);
        }

        var requestError = ValidateRequest(request);
        if (requestError is not null)
        {
            return Failed(provider, model, kind, "request_limit", requestError);
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            entry.LifetimeToken);
        operation.CancelAfter(_generationTimeout);
        try
        {
            var authStatus = await AwaitWithCancellation(
                    entry.Registration.Authentication.CheckAsync(operation.Token).AsTask(),
                    operation.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The authentication provider returned no status.");
            if (!authStatus.Configured)
            {
                return Failed(
                    provider,
                    model,
                    kind,
                    "authentication_unconfigured",
                    authStatus.Error ?? "The media provider is not configured.");
            }

            var authentication = await AwaitWithCancellation(
                entry.Registration.Authentication.ResolveAsync(operation.Token).AsTask(),
                operation.Token).ConfigureAwait(false);
            var invocation = new GameMediaGenerationInvocation(
                entry.Registration.Descriptor,
                descriptor,
                authentication,
                request);
            var generator = entry.Registration.GeneratorFactory(invocation)
                ?? throw new InvalidOperationException("The media generator factory returned no generator.");
            var progressCount = 0;
            long progressResourceBytes = 0;
            var result = await AwaitWithCancellation(
                    generator.GenerateAsync(
                        request,
                        progress is null
                            ? null
                            : async (update, _) =>
                            {
                                if (update is null)
                                {
                                    throw new InvalidOperationException("The media generator reported null progress.");
                                }

                                if (Interlocked.Increment(ref progressCount) > _maxProgressEvents)
                                {
                                    throw new InvalidOperationException("The media generator exceeded the progress event limit.");
                                }

                                if (update.DetailsJson is { } details
                                    && Encoding.UTF8.GetByteCount(details) > _maxJsonBytes)
                                {
                                    throw new InvalidOperationException("Media progress details exceeded the JSON size limit.");
                                }

                                if (update.Preview is { } preview)
                                {
                                    if (MediaKind(preview.MediaType) != request.Kind)
                                    {
                                        throw new InvalidOperationException(
                                            "Media progress returned a preview of the wrong media kind.");
                                    }

                                    var uriBytes = Encoding.UTF8.GetByteCount(preview.Uri);
                                    var mediaTypeBytes = Encoding.UTF8.GetByteCount(preview.MediaType);
                                    var nameBytes = preview.Name is null
                                        ? 0
                                        : Encoding.UTF8.GetByteCount(preview.Name);
                                    if (ContainsForbiddenResourceCharacter(preview.Uri)
                                        || ContainsForbiddenResourceCharacter(preview.MediaType)
                                        || ContainsForbiddenResourceCharacter(preview.Name)
                                        || uriBytes > _maxResourceUriBytes
                                        || mediaTypeBytes > 512
                                        || nameBytes > _maxResourceNameBytes)
                                    {
                                        throw new InvalidOperationException(
                                            "A media progress preview exceeded its size limit.");
                                    }

                                    var resourceBytes = checked((long)uriBytes + mediaTypeBytes + nameBytes);
                                    if (Interlocked.Add(ref progressResourceBytes, resourceBytes)
                                        > _maxAggregateResourceBytes)
                                    {
                                        throw new InvalidOperationException(
                                            "Media progress previews exceeded their aggregate size limit.");
                                    }
                                }

                                using var callback = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                                callback.CancelAfter(_progressCallbackTimeout);
                                try
                                {
                                    await AwaitWithCancellation(
                                        progress(update, callback.Token).AsTask(),
                                        callback.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (!operation.IsCancellationRequested)
                                {
                                    throw new TimeoutException("The media progress callback timed out.");
                                }
                            },
                        operation.Token).AsTask(),
                    operation.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The media generator returned no result.");
            var resultError = ValidateResult(request.Kind, result);
            if (resultError is not null)
            {
                return Failed(provider, model, kind, "invalid_result", resultError);
            }

            return new GameMediaModelGenerationResult(
                provider,
                model,
                kind,
                GameMediaModelGenerationStatus.Completed,
                result,
                null,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || entry.LifetimeToken.IsCancellationRequested)
        {
            return Canceled(provider, model, kind);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return Failed(provider, model, kind, "timeout", "The media generation operation timed out.");
        }
        catch (Exception exception)
        {
            return Failed(provider, model, kind, "generation_failed", exception.Message);
        }
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _providers.Values.ToArray();
            _providers.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Cancel();
        }
    }

    private async Task<GameMediaModelRefreshResult> RunRefreshAsync(
        Entry entry)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(entry.LifetimeToken);
        operation.CancelAfter(_refreshTimeout);
        var token = operation.Token;
        var providerId = entry.Registration.Descriptor.ProviderId;
        try
        {
            IReadOnlyList<GameModelDescriptor> currentModels;
            lock (_gate)
            {
                if (_disposed || !_providers.TryGetValue(providerId, out var current) || !ReferenceEquals(current, entry))
                {
                    return new GameMediaModelRefreshResult(
                        providerId,
                        GameMediaModelRefreshStatus.StaleRegistration,
                        entry.CurrentModels.Count);
                }

                currentModels = Array.AsReadOnly(entry.CurrentModels.ToArray());
            }

            var authStatus = await AwaitWithCancellation(
                    entry.Registration.Authentication.CheckAsync(token).AsTask(),
                    token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The authentication provider returned no status.");
            if (!authStatus.Configured)
            {
                return new GameMediaModelRefreshResult(
                    providerId,
                    GameMediaModelRefreshStatus.SkippedUnconfigured,
                    currentModels.Count,
                    Bound(authStatus.Error));
            }

            var authentication = await AwaitWithCancellation(
                entry.Registration.Authentication.ResolveAsync(token).AsTask(),
                token).ConfigureAwait(false);
            var refreshed = await AwaitWithCancellation(
                    entry.Registration.RefreshModels!(
                        new GameMediaModelRefreshContext(
                            entry.Registration.Descriptor,
                            currentModels,
                            authentication),
                        token).AsTask(),
                    token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The media model refresh returned no models.");
            var validated = GameMediaProviderRegistration.ValidateModels(
                providerId,
                refreshed,
                _maxModelsPerProvider);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_disposed || !_providers.TryGetValue(providerId, out var current) || !ReferenceEquals(current, entry))
                {
                    return new GameMediaModelRefreshResult(
                        providerId,
                        GameMediaModelRefreshStatus.StaleRegistration,
                        currentModels.Count);
                }

                var changed = !ModelsEquivalent(entry.CurrentModels, validated);
                entry.CurrentModels = validated;
                return new GameMediaModelRefreshResult(
                    providerId,
                    changed ? GameMediaModelRefreshStatus.Updated : GameMediaModelRefreshStatus.Unchanged,
                    validated.Count);
            }
        }
        catch (OperationCanceledException) when (entry.LifetimeToken.IsCancellationRequested)
        {
            return new GameMediaModelRefreshResult(
                providerId,
                GameMediaModelRefreshStatus.StaleRegistration,
                entry.CurrentModels.Count);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return new GameMediaModelRefreshResult(
                providerId,
                GameMediaModelRefreshStatus.Failed,
                entry.CurrentModels.Count,
                Bound("The media model refresh timed out."));
        }
        catch (OperationCanceledException exception)
        {
            return new GameMediaModelRefreshResult(
                providerId,
                GameMediaModelRefreshStatus.Failed,
                entry.CurrentModels.Count,
                Bound(exception.Message));
        }
        catch (Exception exception)
        {
            return new GameMediaModelRefreshResult(
                providerId,
                GameMediaModelRefreshStatus.Failed,
                entry.CurrentModels.Count,
                Bound(exception.Message));
        }
    }

    private async Task<GameMediaModelRefreshResult> ObserveRefreshAsync(
        Entry entry,
        Task<GameMediaModelRefreshResult> refresh,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AwaitWithCancellation(refresh, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new GameMediaModelRefreshResult(
                entry.Registration.Descriptor.ProviderId,
                GameMediaModelRefreshStatus.Canceled,
                entry.CurrentModels.Count);
        }
    }

    private string? ValidateRequest(GameMediaGenerationRequest request)
    {
        try
        {
            if (request.Sources.Count > _maxSources)
            {
                return "The media generation request contains too many sources.";
            }

            if (request.Prompt is { } prompt && Encoding.UTF8.GetByteCount(prompt) > _maxPromptBytes)
            {
                return "The media generation prompt exceeded the size limit.";
            }

            if (!ValidateJson(request.ContextJson) || !ValidateJson(request.ParametersJson))
            {
                return "Media generation JSON contains duplicate property names or exceeds its size limit.";
            }

            long aggregate = 0;
            foreach (var source in request.Sources)
            {
                var uriBytes = Encoding.UTF8.GetByteCount(source.Uri);
                var mediaTypeBytes = Encoding.UTF8.GetByteCount(source.MediaType);
                var nameBytes = source.Name is null ? 0 : Encoding.UTF8.GetByteCount(source.Name);
                if (ContainsForbiddenResourceCharacter(source.Uri)
                    || ContainsForbiddenResourceCharacter(source.MediaType)
                    || ContainsForbiddenResourceCharacter(source.Name)
                    || uriBytes > _maxResourceUriBytes
                    || mediaTypeBytes > 512
                    || nameBytes > _maxResourceNameBytes)
                {
                    return "A media source exceeded its size limit.";
                }

                aggregate = checked(aggregate + uriBytes + mediaTypeBytes + nameBytes);
                if (aggregate > _maxAggregateResourceBytes)
                {
                    return "The media sources exceeded their aggregate size limit.";
                }
            }

            return null;
        }
        catch (OverflowException)
        {
            return "The media generation request exceeded its aggregate size limit.";
        }
    }

    private bool ValidateJson(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > _maxJsonBytes)
        {
            return false;
        }

        using var document = JsonDocument.Parse(value);
        return HasUniqueProperties(document.RootElement);
    }

    private static bool HasUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || !HasUniqueProperties(property.Value))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (!HasUniqueProperties(item))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private string? ValidateResult(GameMediaKind kind, GameMediaGenerationResult result)
    {
        if (result.Outputs.Count == 0 || result.Outputs.Count > _maxOutputs)
        {
            return "The media generator returned an invalid number of outputs.";
        }

        if (Encoding.UTF8.GetByteCount(result.MetadataJson) > _maxJsonBytes || !ValidateJson(result.MetadataJson))
        {
            return "The media generator returned invalid or oversized metadata.";
        }

        long aggregate = 0;
        try
        {
            foreach (var output in result.Outputs)
            {
                if (MediaKind(output.MediaType) != kind)
                {
                    return "The media generator returned an output of the wrong media kind.";
                }

                var uriBytes = Encoding.UTF8.GetByteCount(output.Uri);
                var mediaTypeBytes = Encoding.UTF8.GetByteCount(output.MediaType);
                var nameBytes = output.Name is null ? 0 : Encoding.UTF8.GetByteCount(output.Name);
                if (ContainsForbiddenResourceCharacter(output.Uri)
                    || ContainsForbiddenResourceCharacter(output.MediaType)
                    || ContainsForbiddenResourceCharacter(output.Name)
                    || uriBytes > _maxResourceUriBytes
                    || mediaTypeBytes > 512
                    || nameBytes > _maxResourceNameBytes)
                {
                    return "A media output exceeded its size limit.";
                }

                aggregate = checked(aggregate + uriBytes + mediaTypeBytes + nameBytes);
                if (aggregate > _maxAggregateResourceBytes)
                {
                    return "The media outputs exceeded their aggregate size limit.";
                }
            }
        }
        catch (OverflowException)
        {
            return "The media outputs exceeded their aggregate size limit.";
        }

        return null;
    }

    private static bool ContainsForbiddenResourceCharacter(string? value) =>
        value?.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0;

    private static string? ValidateCapabilities(
        GameModelDescriptor model,
        GameMediaGenerationRequest request)
    {
        var requiredOutput = request.Kind switch
        {
            GameMediaKind.Image => GameModelOutputCapabilities.Image,
            GameMediaKind.Audio => GameModelOutputCapabilities.Audio,
            GameMediaKind.Video => GameModelOutputCapabilities.Video,
            _ => GameModelOutputCapabilities.None,
        };
        if ((model.OutputCapabilities & requiredOutput) != requiredOutput)
        {
            return "The selected model cannot generate the requested media kind.";
        }

        if (!string.IsNullOrEmpty(request.Prompt)
            && !model.InputCapabilities.HasFlag(GameModelInputCapabilities.Text))
        {
            return "The selected model does not accept text prompts.";
        }

        foreach (var source in request.Sources)
        {
            var requiredInput = MediaKind(source.MediaType) switch
            {
                GameMediaKind.Image => GameModelInputCapabilities.Image,
                GameMediaKind.Audio => GameModelInputCapabilities.Audio,
                GameMediaKind.Video => GameModelInputCapabilities.Video,
                _ => GameModelInputCapabilities.None,
            };
            if (requiredInput == GameModelInputCapabilities.None)
            {
                return "A supplied source has an unsupported media type.";
            }

            if (!model.InputCapabilities.HasFlag(requiredInput))
            {
                return "The selected model does not accept one of the supplied media kinds.";
            }
        }

        return null;
    }

    private GameMediaModelGenerationResult Failed(
        string providerId,
        string modelId,
        GameMediaKind kind,
        string code,
        string message) =>
        new(
            providerId,
            modelId,
            kind,
            GameMediaModelGenerationStatus.Failed,
            null,
            code,
            Bound(message) ?? "Media generation failed.");

    private GameMediaModelGenerationResult Canceled(
        string providerId,
        string modelId,
        GameMediaKind kind) =>
        new(
            providerId,
            modelId,
            kind,
            GameMediaModelGenerationStatus.Canceled,
            null,
            "canceled",
            Bound("The media generation operation was canceled."));

    private string? Bound(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= _maxErrorCharacters ? value : value.Substring(0, _maxErrorCharacters);
    }

    private static GameMediaKind? MediaKind(string mediaType)
    {
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return GameMediaKind.Image;
        }

        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return GameMediaKind.Audio;
        }

        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return GameMediaKind.Video;
        }

        return null;
    }

    private static async Task<T> AwaitWithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellation);
        if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
        {
            ObserveLateFault(task);
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
    }

    private static async Task AwaitWithCancellation(Task task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellation);
        if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
        {
            ObserveLateFault(task);
            throw new OperationCanceledException(cancellationToken);
        }

        await task.ConfigureAwait(false);
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static bool ModelsEquivalent(
        IReadOnlyList<GameModelDescriptor> left,
        IReadOnlyList<GameModelDescriptor> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (!string.Equals(first.ProviderId, second.ProviderId, StringComparison.Ordinal)
                || !string.Equals(first.ModelId, second.ModelId, StringComparison.Ordinal)
                || !string.Equals(first.DisplayName, second.DisplayName, StringComparison.Ordinal)
                || !string.Equals(first.Api, second.Api, StringComparison.Ordinal)
                || !Equals(first.BaseUrl, second.BaseUrl)
                || first.ContextWindowTokens != second.ContextWindowTokens
                || first.MaximumOutputTokens != second.MaximumOutputTokens
                || first.InputCapabilities != second.InputCapabilities
                || first.OutputCapabilities != second.OutputCapabilities
                || !first.ReasoningLevels.SequenceEqual(second.ReasoningLevels)
                || !ReasoningValuesEquivalent(first.ReasoningLevelValues, second.ReasoningLevelValues)
                || !CostEquivalent(first.Cost, second.Cost)
                || !DictionaryEquivalent(first.Metadata, second.Metadata)
                || !NullableDictionaryEquivalent(first.Headers, second.Headers)
                || !string.Equals(first.SamplingParametersJson, second.SamplingParametersJson, StringComparison.Ordinal)
                || !string.Equals(first.CompatibilityJson, second.CompatibilityJson, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReasoningValuesEquivalent(
        IReadOnlyDictionary<GameReasoningLevel, string> left,
        IReadOnlyDictionary<GameReasoningLevel, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool CostEquivalent(GameModelCost left, GameModelCost right) =>
        left.InputPerMillionTokens == right.InputPerMillionTokens
        && left.OutputPerMillionTokens == right.OutputPerMillionTokens
        && left.CacheReadPerMillionTokens == right.CacheReadPerMillionTokens
        && left.CacheWritePerMillionTokens == right.CacheWritePerMillionTokens
        && left.Tiers.Count == right.Tiers.Count
        && left.Tiers.Zip(right.Tiers, CostTierEquivalent).All(equivalent => equivalent);

    private static bool CostTierEquivalent(GameModelCostTier left, GameModelCostTier right) =>
        left.InputTokensAbove == right.InputTokensAbove
        && left.InputPerMillionTokens == right.InputPerMillionTokens
        && left.OutputPerMillionTokens == right.OutputPerMillionTokens
        && left.CacheReadPerMillionTokens == right.CacheReadPerMillionTokens
        && left.CacheWritePerMillionTokens == right.CacheWritePerMillionTokens;

    private static bool DictionaryEquivalent(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool NullableDictionaryEquivalent(
        IReadOnlyDictionary<string, string?> left,
        IReadOnlyDictionary<string, string?> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static int RequireRange(int value, int minimum, int maximum, string parameterName) =>
        value >= minimum && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);

    private static TimeSpan RequireDuration(
        TimeSpan value,
        TimeSpan minimum,
        TimeSpan maximum,
        string parameterName) =>
        value >= minimum && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);

    private static string RequireId(string? value, string parameterName) =>
        TryRequireId(value, out var valid)
            ? valid
            : throw new ArgumentException("A non-empty identifier of at most 512 characters is required.", parameterName);

    private static bool TryRequireId(string? value, out string valid)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            valid = string.Empty;
            return false;
        }

        valid = value;
        return true;
    }

    private static string SafeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "invalid";
        }

        return value.Length <= 512 ? value : value.Substring(0, 512);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GameMediaModelRegistry));
        }
    }

    private sealed class Entry
    {
        private readonly object _refreshGate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly CancellationToken _lifetimeToken;
        private Task<GameMediaModelRefreshResult>? _inflightRefresh;
        private int _canceled;

        public Entry(GameMediaProviderRegistration registration)
        {
            Registration = registration;
            CurrentModels = registration.Models;
            _lifetimeToken = _lifetime.Token;
        }

        public GameMediaProviderRegistration Registration { get; }

        public IReadOnlyList<GameModelDescriptor> CurrentModels { get; set; }

        public CancellationToken LifetimeToken => _lifetimeToken;

        public Task<GameMediaModelRefreshResult> GetOrStartRefresh(
            GameMediaModelRegistry owner)
        {
            lock (_refreshGate)
            {
                if (_inflightRefresh is not null)
                {
                    return _inflightRefresh;
                }

                var refresh = owner.RunRefreshAsync(this);
                _inflightRefresh = refresh;
                _ = refresh.ContinueWith(
                    (_, state) => ((Entry)state!).ClearRefresh(refresh),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return refresh;
            }
        }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _canceled, 1) != 0)
            {
                return;
            }

            try
            {
                _lifetime.Cancel();
            }
            catch (AggregateException)
            {
                // A provider callback cannot block replacement, removal, or disposal.
            }
            finally
            {
                _lifetime.Dispose();
            }
        }

        private void ClearRefresh(Task<GameMediaModelRefreshResult> completed)
        {
            lock (_refreshGate)
            {
                if (ReferenceEquals(_inflightRefresh, completed))
                {
                    _inflightRefresh = null;
                }
            }
        }
    }
}

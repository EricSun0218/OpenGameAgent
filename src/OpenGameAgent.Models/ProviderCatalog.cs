using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Models;

public sealed class GameProviderDescriptor
{
    public GameProviderDescriptor(
        string providerId,
        string? displayName = null,
        Uri? endpoint = null,
        bool isLocal = false,
        bool supportsDynamicModels = false,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ProviderId = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        DisplayName = displayName is null ? ProviderId : GameModelDescriptor.RequireId(displayName, nameof(displayName));
        if (endpoint is not null
            && (!endpoint.IsAbsoluteUri
                || endpoint.UserInfo.Length > 0
                || endpoint.Fragment.Length > 0
                || endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "A provider endpoint must be an absolute HTTP or HTTPS URL without embedded credentials or a fragment.",
                nameof(endpoint));
        }

        Endpoint = endpoint;
        IsLocal = isLocal;
        SupportsDynamicModels = supportsDynamicModels;
        if (metadata is { Count: > 256 })
        {
            throw new ArgumentException("Provider metadata cannot contain more than 256 entries.", nameof(metadata));
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata ?? new Dictionary<string, string>())
        {
            var key = GameModelDescriptor.RequireId(pair.Key, nameof(metadata));
            if (pair.Value is null || pair.Value.Length > 16_384 || !copy.TryAdd(key, pair.Value))
            {
                throw new ArgumentException("Provider metadata is invalid or contains duplicate keys.", nameof(metadata));
            }
        }

        Metadata = new ReadOnlyDictionary<string, string>(copy);
    }

    public string ProviderId { get; }

    public string DisplayName { get; }

    public Uri? Endpoint { get; }

    public bool IsLocal { get; }

    public bool SupportsDynamicModels { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }
}

public sealed class GameModelRefreshContext
{
    internal GameModelRefreshContext(
        GameProviderDescriptor provider,
        IReadOnlyList<GameModelDescriptor> currentModels,
        GameProviderAuthResolution? authentication,
        bool allowNetwork,
        bool force)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        CurrentModels = Array.AsReadOnly(
            (currentModels ?? throw new ArgumentNullException(nameof(currentModels))).ToArray());
        Authentication = authentication;
        AllowNetwork = allowNetwork;
        Force = allowNetwork && force;
    }

    public GameProviderDescriptor Provider { get; }

    public IReadOnlyList<GameModelDescriptor> CurrentModels { get; }

    public GameProviderAuthResolution? Authentication { get; }

    public bool AllowNetwork { get; }

    public bool Force { get; }
}

public delegate ValueTask<IReadOnlyList<GameModelDescriptor>> GameModelRefresh(
    GameModelRefreshContext context,
    CancellationToken cancellationToken);

public delegate ValueTask<IReadOnlyList<GameModelDescriptor>> GameModelAvailabilityFilter(
    IReadOnlyList<GameModelDescriptor> models,
    GameProviderAuthResolution? authentication,
    CancellationToken cancellationToken);

public delegate IAsyncEnumerable<ModelStreamEvent> GameModelStream(
    ModelRequest request,
    GameProviderAuthResolution? authentication,
    CancellationToken cancellationToken);

public delegate IAsyncEnumerable<ModelStreamEvent> GameModelDeferredFetch(
    DeferredModelHandle handle,
    TimeSpan wait,
    GameProviderAuthResolution? authentication,
    CancellationToken cancellationToken);

public delegate ValueTask GameModelDeferredCancel(
    DeferredModelHandle handle,
    GameProviderAuthResolution? authentication,
    CancellationToken cancellationToken);

public sealed class GameModelProviderRegistration
{
    public GameModelProviderRegistration(
        GameProviderDescriptor descriptor,
        IModelProvider provider,
        IGameProviderAuthentication authentication,
        IReadOnlyList<GameModelDescriptor>? models = null,
        GameModelRefresh? refreshModels = null,
        GameModelAvailabilityFilter? filterModels = null,
        GameModelStream? stream = null,
        GameModelDeferredFetch? fetchDeferred = null,
        GameModelDeferredCancel? cancelDeferred = null,
        string catalogVersion = "1")
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        Models = ValidateModels(descriptor.ProviderId, models ?? Array.Empty<GameModelDescriptor>());
        if (refreshModels is not null && !descriptor.SupportsDynamicModels)
        {
            throw new ArgumentException("A provider with model refresh must declare dynamic model support.", nameof(refreshModels));
        }

        RefreshModels = refreshModels;
        FilterModels = filterModels;
        Stream = stream ?? ((request, _, cancellationToken) => provider.StreamAsync(request, cancellationToken));
        FetchDeferred = fetchDeferred
                        ?? (provider is IDeferredModelProvider deferred
                            ? (handle, wait, _, cancellationToken) =>
                                deferred.FetchDeferredAsync(handle, wait, cancellationToken)
                            : null);
        CancelDeferred = cancelDeferred
                         ?? (provider is IDeferredModelProvider deferredCancel
                             ? (handle, _, cancellationToken) =>
                                 deferredCancel.CancelDeferredAsync(handle, cancellationToken)
                             : null);
        CatalogVersion = GameModelDescriptor.RequireId(catalogVersion, nameof(catalogVersion));
    }

    public GameProviderDescriptor Descriptor { get; }

    public IModelProvider Provider { get; }

    public IGameProviderAuthentication Authentication { get; }

    public IReadOnlyList<GameModelDescriptor> Models { get; }

    public GameModelRefresh? RefreshModels { get; }

    public GameModelAvailabilityFilter? FilterModels { get; }

    public GameModelStream Stream { get; }

    public GameModelDeferredFetch? FetchDeferred { get; }

    public GameModelDeferredCancel? CancelDeferred { get; }

    public string CatalogVersion { get; }

    internal static IReadOnlyList<GameModelDescriptor> ValidateModels(
        string providerId,
        IReadOnlyList<GameModelDescriptor> models)
    {
        if (models is null)
        {
            throw new ArgumentNullException(nameof(models));
        }

        var copy = models.ToArray();
        if (copy.Length > 100_000)
        {
            throw new ArgumentException("A provider cannot expose more than 100,000 models.", nameof(models));
        }
        if (copy.Any(model => model is null))
        {
            throw new ArgumentException("A model catalog cannot contain null entries.", nameof(models));
        }

        if (copy.Any(model => !string.Equals(model.ProviderId, providerId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Every model must belong to its registered provider.", nameof(models));
        }

        var duplicate = copy.GroupBy(model => model.ModelId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate model ID '{duplicate.Key}'.", nameof(models));
        }

        return Array.AsReadOnly(copy);
    }
}

public enum GameModelRefreshStatus
{
    Updated,
    Unchanged,
    SkippedStatic,
    SkippedUnconfigured,
    StaleRegistration,
    StoreConflict,
    Failed,
    Canceled,
}

public sealed class GameModelRefreshResult
{
    internal GameModelRefreshResult(
        string providerId,
        GameModelRefreshStatus status,
        int modelCount,
        Exception? error = null)
    {
        ProviderId = providerId;
        Status = status;
        ModelCount = modelCount;
        Error = error;
    }

    public string ProviderId { get; }

    public GameModelRefreshStatus Status { get; }

    public int ModelCount { get; }

    public Exception? Error { get; }
}

public sealed class GameModelResolution
{
    internal GameModelResolution(
        GameModelProviderRegistration registration,
        GameModelDescriptor model,
        GameReasoningLevel reasoning)
    {
        Registration = registration;
        Model = model;
        Reasoning = reasoning;
    }

    public GameModelProviderRegistration Registration { get; }

    public IModelProvider Provider => Registration.Provider;

    public GameModelDescriptor Model { get; }

    public GameReasoningLevel Reasoning { get; }

    public ModelParameters CreateParameters(ModelParameters? baseline = null)
    {
        var parameters = baseline?.Clone() ?? new ModelParameters();
        parameters.ReasoningLevel = Model.GetReasoningValue(Reasoning);
        if (parameters.MaxOutputTokens is { } outputLimit
            && Model.MaximumOutputTokens > 0
            && outputLimit > Model.MaximumOutputTokens)
        {
            parameters.MaxOutputTokens = Model.MaximumOutputTokens;
        }

        return parameters;
    }

    public decimal EstimateCost(ModelUsage usage)
    {
        if (usage is null)
        {
            throw new ArgumentNullException(nameof(usage));
        }

        const decimal scale = 1_000_000m;
        var inputVolume = checked(usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens);
        var rates = Model.Cost.RatesForInput(inputVolume);
        var longCacheWrite = usage.CacheWriteOneHourTokens ?? 0;
        var shortCacheWrite = usage.CacheWriteTokens - longCacheWrite;
        return usage.InputTokens / scale * rates.InputPerMillionTokens
            + usage.OutputTokens / scale * rates.OutputPerMillionTokens
            + usage.CacheReadTokens / scale * rates.CacheReadPerMillionTokens
            + shortCacheWrite / scale * rates.CacheWritePerMillionTokens
            + longCacheWrite / scale * rates.InputPerMillionTokens * 2;
    }
}

public sealed class GameModelCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _providers = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly IGameModelCatalogStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private long _generation;

    public GameModelCatalog(
        int capacity = 128,
        IGameModelCatalogStore? store = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (capacity <= 0 || capacity > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _store = store ?? new InMemoryGameModelCatalogStore(capacity);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Register(GameModelProviderRegistration registration, bool replace = false)
    {
        if (registration is null)
        {
            throw new ArgumentNullException(nameof(registration));
        }

        lock (_gate)
        {
            var id = registration.Descriptor.ProviderId;
            if (_providers.TryGetValue(id, out var existing) && !replace)
            {
                throw new InvalidOperationException($"Provider '{id}' is already registered.");
            }

            if (existing is null && _providers.Count >= _capacity)
            {
                throw new InvalidOperationException("The provider catalog reached its capacity.");
            }

            existing?.Supersede();
            _providers[id] = new Entry(registration, checked(++_generation));
        }
    }

    public bool Unregister(string providerId)
    {
        var id = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        lock (_gate)
        {
            if (!_providers.Remove(id, out var removed))
            {
                return false;
            }

            removed.Supersede();
            _generation = checked(_generation + 1);
            return true;
        }
    }

    public IReadOnlyList<GameModelProviderRegistration> GetProviders()
    {
        lock (_gate)
        {
            return Array.AsReadOnly(_providers.Values
                .OrderBy(entry => entry.Registration.Descriptor.ProviderId, StringComparer.Ordinal)
                .Select(entry => entry.Registration)
                .ToArray());
        }
    }

    public GameModelProviderRegistration? GetProvider(string providerId)
    {
        var id = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        lock (_gate)
        {
            return _providers.TryGetValue(id, out var entry) ? entry.Registration : null;
        }
    }

    public IReadOnlyList<GameModelDescriptor> GetModels(string? providerId = null)
    {
        lock (_gate)
        {
            if (providerId is not null)
            {
                var id = GameModelDescriptor.RequireId(providerId, nameof(providerId));
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
        var provider = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        var model = GameModelDescriptor.RequireId(modelId, nameof(modelId));
        lock (_gate)
        {
            return _providers.TryGetValue(provider, out var entry)
                ? entry.CurrentModels.FirstOrDefault(candidate => string.Equals(candidate.ModelId, model, StringComparison.Ordinal))
                : null;
        }
    }

    public GameModelResolution Resolve(
        string providerId,
        string modelId,
        GameReasoningLevel reasoning = GameReasoningLevel.Off,
        GameModelInputCapabilities requiredInput = GameModelInputCapabilities.None,
        GameModelOutputCapabilities requiredOutput = GameModelOutputCapabilities.None)
    {
        var provider = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        var model = GameModelDescriptor.RequireId(modelId, nameof(modelId));
        lock (_gate)
        {
            if (!_providers.TryGetValue(provider, out var entry))
            {
                throw new KeyNotFoundException($"Provider '{provider}' is not registered.");
            }

            var descriptor = entry.CurrentModels.FirstOrDefault(
                candidate => string.Equals(candidate.ModelId, model, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"Model '{provider}/{model}' is not registered.");
            if (!descriptor.Supports(requiredInput, requiredOutput))
            {
                throw new InvalidOperationException($"Model '{provider}/{model}' does not satisfy the required capabilities.");
            }

            return new GameModelResolution(entry.Registration, descriptor, descriptor.ClampReasoning(reasoning));
        }
    }

    public async ValueTask<IReadOnlyList<GameModelDescriptor>> GetAvailableModelsAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        var selectedProvider = providerId is null
            ? null
            : GameModelDescriptor.RequireId(providerId, nameof(providerId));
        EntrySnapshot[] entries;
        lock (_gate)
        {
            entries = _providers.Values
                .Where(entry => selectedProvider is null
                    || string.Equals(entry.Registration.Descriptor.ProviderId, selectedProvider, StringComparison.Ordinal))
                .Select(entry => new EntrySnapshot(entry))
                .ToArray();
        }

        var checks = entries.Select(entry => GetAvailableModelsAsync(entry, cancellationToken).AsTask()).ToArray();
        var available = await Task.WhenAll(checks).ConfigureAwait(false);
        return Array.AsReadOnly(available.SelectMany(models => models).ToArray());
    }

    private static async ValueTask<IReadOnlyList<GameModelDescriptor>> GetAvailableModelsAsync(
        EntrySnapshot entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await CancellableOperation.WaitAsync(
                entry.Registration.Authentication.CheckAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The provider authentication check returned null.");
        if (!status.Configured)
        {
            return Array.Empty<GameModelDescriptor>();
        }

        var auth = await CancellableOperation.WaitAsync(
            entry.Registration.Authentication.ResolveAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var models = entry.Models;
        if (entry.Registration.FilterModels is null)
        {
            return models;
        }

        models = await CancellableOperation.WaitAsync(
                entry.Registration.FilterModels(models, auth, cancellationToken),
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The provider model filter returned null.");
        return GameModelProviderRegistration.ValidateModels(
            entry.Registration.Descriptor.ProviderId,
            models);
    }

    public IModelProvider CreateProvider(string providerId) =>
        new CatalogDispatchProvider(this, GameModelDescriptor.RequireId(providerId, nameof(providerId)));

    public async ValueTask<GameProviderAuthStatus> CheckAuthenticationAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var registration = RequireRegistration(providerId);
        try
        {
            return await CancellableOperation.WaitAsync(
                       registration.Authentication.CheckAsync(cancellationToken),
                       cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException(
                       $"Provider '{registration.Descriptor.ProviderId}' returned no authentication status.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Authentication check failed for provider '{registration.Descriptor.ProviderId}'.",
                exception);
        }
    }

    public async ValueTask<GameProviderAuthResolution?> ResolveAuthenticationAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var registration = RequireRegistration(providerId);
        try
        {
            return await CancellableOperation.WaitAsync(
                registration.Authentication.ResolveAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Authentication resolution failed for provider '{registration.Descriptor.ProviderId}'.",
                exception);
        }
    }

    public async ValueTask<GameCredential> LoginAsync(
        string providerId,
        string scheme,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var registration = RequireRegistration(providerId);
        var requestedScheme = GameModelDescriptor.RequireId(scheme, nameof(scheme));
        if (interaction is null)
        {
            throw new ArgumentNullException(nameof(interaction));
        }

        try
        {
            return await CancellableOperation.WaitAsync(
                       registration.Authentication.LoginAsync(
                           requestedScheme,
                           interaction,
                           cancellationToken),
                       cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException(
                       $"Provider '{registration.Descriptor.ProviderId}' returned no login credential.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Authentication login failed for provider '{registration.Descriptor.ProviderId}'.",
                exception);
        }
    }

    public async ValueTask LogoutAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var registration = RequireRegistration(providerId);
        try
        {
            await CancellableOperation.WaitAsync(
                registration.Authentication.LogoutAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Authentication logout failed for provider '{registration.Descriptor.ProviderId}'.",
                exception);
        }
    }

    public IAsyncEnumerable<ModelStreamEvent> FetchDeferredAsync(
        DeferredModelHandle handle,
        TimeSpan wait,
        CancellationToken cancellationToken = default) =>
        FetchDeferredCoreAsync(
            handle ?? throw new ArgumentNullException(nameof(handle)),
            wait,
            cancellationToken);

    public async ValueTask CancelDeferredAsync(
        DeferredModelHandle handle,
        CancellationToken cancellationToken = default)
    {
        if (handle is null)
        {
            throw new ArgumentNullException(nameof(handle));
        }

        var registration = RequireDeferredRegistration(handle, requireCancel: true);
        var authentication = await ResolveConfiguredAuthenticationAsync(registration, cancellationToken)
            .ConfigureAwait(false);
        await CancellableOperation.WaitAsync(
            registration.CancelDeferred!(handle, authentication, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<ModelStreamEvent> FetchDeferredCoreAsync(
        DeferredModelHandle handle,
        TimeSpan wait,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registration = RequireDeferredRegistration(handle, requireCancel: false);
        var authentication = await ResolveConfiguredAuthenticationAsync(registration, cancellationToken)
            .ConfigureAwait(false);
        await foreach (var streamEvent in registration.FetchDeferred!(
                           handle,
                           wait,
                           authentication,
                           cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return streamEvent
                ?? throw new InvalidOperationException("A deferred model provider emitted a null stream event.");
        }
    }

    private GameModelProviderRegistration RequireRegistration(string providerId)
    {
        var id = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        lock (_gate)
        {
            return _providers.TryGetValue(id, out var entry)
                ? entry.Registration
                : throw new KeyNotFoundException($"Provider '{id}' is not registered.");
        }
    }

    private GameModelProviderRegistration RequireDeferredRegistration(
        DeferredModelHandle handle,
        bool requireCancel)
    {
        lock (_gate)
        {
            if (!_providers.TryGetValue(handle.Provider, out var entry))
            {
                throw new ModelProviderException(
                    $"Provider '{handle.Provider}' is no longer registered.",
                    isTransient: false);
            }

            var model = entry.CurrentModels.FirstOrDefault(candidate =>
                string.Equals(candidate.ModelId, handle.Model, StringComparison.Ordinal));
            if (model is null)
            {
                throw new ModelProviderException(
                    $"Model '{handle.Provider}/{handle.Model}' is no longer registered.",
                    isTransient: false);
            }

            if (!string.Equals(model.Api, handle.Api, StringComparison.Ordinal))
            {
                throw new ModelProviderException(
                    $"Deferred handle API '{handle.Api}' does not match model API '{model.Api}'.",
                    isTransient: false);
            }

            var supported = requireCancel
                ? entry.Registration.CancelDeferred is not null
                : entry.Registration.FetchDeferred is not null;
            if (!supported)
            {
                throw new ModelProviderException(
                    requireCancel
                        ? $"Provider '{handle.Provider}' cannot cancel deferred responses for API '{handle.Api}'."
                        : $"Provider '{handle.Provider}' does not support deferred responses for API '{handle.Api}'.",
                    isTransient: false);
            }

            return entry.Registration;
        }
    }

    private static async ValueTask<GameProviderAuthStatus> ResolveAuthenticationStatusAsync(
        GameModelProviderRegistration registration,
        CancellationToken cancellationToken) =>
        await CancellableOperation.WaitAsync(
                registration.Authentication.CheckAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false)
            ?? throw new ModelProviderException(
                $"Provider '{registration.Descriptor.ProviderId}' returned no authentication status.",
                isTransient: false);

    private static async ValueTask<GameProviderAuthResolution?> ResolveConfiguredAuthenticationAsync(
        GameModelProviderRegistration registration,
        CancellationToken cancellationToken)
    {
        var status = await ResolveAuthenticationStatusAsync(registration, cancellationToken).ConfigureAwait(false);
        if (!status.Configured)
        {
            throw new ModelProviderException(
                status.Error ?? $"Provider '{registration.Descriptor.ProviderId}' is not configured.",
                isTransient: false);
        }

        return await CancellableOperation.WaitAsync(
            registration.Authentication.ResolveAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        string providerId,
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameModelProviderRegistration registration;
        lock (_gate)
        {
            if (!_providers.TryGetValue(providerId, out var entry))
            {
                throw new ModelProviderException(
                    $"Provider '{providerId}' is no longer registered.",
                    isTransient: false);
            }

            if (!entry.CurrentModels.Any(model => string.Equals(model.ModelId, request.Model, StringComparison.Ordinal)))
            {
                throw new ModelProviderException(
                    $"Model '{providerId}/{request.Model}' is no longer registered.",
                    isTransient: false);
            }

            registration = entry.Registration;
        }

        var status = await ResolveAuthenticationStatusAsync(registration, cancellationToken).ConfigureAwait(false);
        if (!status.Configured)
        {
            throw new ModelProviderException(
                status.Error ?? $"Provider '{providerId}' is not configured.",
                isTransient: false);
        }

        var authentication = await CancellableOperation.WaitAsync(
            registration.Authentication.ResolveAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await foreach (var streamEvent in registration.Stream(
                           request,
                           authentication,
                           cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return streamEvent
                ?? throw new InvalidOperationException("A registered model provider emitted a null stream event.");
        }
    }

    public async ValueTask<GameModelRefreshResult> RefreshAsync(
        string providerId,
        bool allowNetwork = true,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var id = GameModelDescriptor.RequireId(providerId, nameof(providerId));
        EntrySnapshot snapshot;
        lock (_gate)
        {
            if (!_providers.TryGetValue(id, out var entry))
            {
                throw new KeyNotFoundException($"Provider '{id}' is not registered.");
            }

            snapshot = entry.Registration.RefreshModels is null
                ? new EntrySnapshot(entry)
                : entry.BeginRefresh(cancellationToken);
        }

        if (snapshot.Registration.RefreshModels is null)
        {
            return new GameModelRefreshResult(id, GameModelRefreshStatus.SkippedStatic, snapshot.Models.Count);
        }

        var refreshGateAcquired = false;
        try
        {
            await snapshot.RefreshGate.WaitAsync(snapshot.RefreshToken).ConfigureAwait(false);
            refreshGateAcquired = true;
            var stored = await CancellableOperation.WaitAsync(
                _store.LoadAsync(id, snapshot.RefreshToken),
                snapshot.RefreshToken).ConfigureAwait(false);
            var currentModels = snapshot.Models;
            if (stored is not null
                && string.Equals(
                    stored.CatalogVersion,
                    snapshot.Registration.CatalogVersion,
                    StringComparison.Ordinal))
            {
                var restored = GameModelProviderRegistration.ValidateModels(id, stored.Models);
                lock (_gate)
                {
                    if (!_providers.TryGetValue(id, out var current)
                        || current.Generation != snapshot.Generation
                        || current.RefreshGeneration != snapshot.RefreshGeneration
                        || !ReferenceEquals(current.Registration, snapshot.Registration))
                    {
                        return new GameModelRefreshResult(
                            id,
                            GameModelRefreshStatus.StaleRegistration,
                            snapshot.Models.Count);
                    }

                    current.DynamicModels = restored;
                    current.CurrentModels = Merge(current.Registration.Models, restored);
                    currentModels = current.CurrentModels;
                }
            }

            var status = await CancellableOperation.WaitAsync(
                    snapshot.Registration.Authentication.CheckAsync(snapshot.RefreshToken),
                    snapshot.RefreshToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The provider authentication check returned null.");
            if (!status.Configured)
            {
                return new GameModelRefreshResult(id, GameModelRefreshStatus.SkippedUnconfigured, currentModels.Count);
            }

            var authentication = await CancellableOperation.WaitAsync(
                snapshot.Registration.Authentication.ResolveAsync(snapshot.RefreshToken),
                snapshot.RefreshToken).ConfigureAwait(false);
            var refreshed = await CancellableOperation.WaitAsync(
                    snapshot.Registration.RefreshModels(
                        new GameModelRefreshContext(
                            snapshot.Registration.Descriptor,
                            currentModels,
                            authentication,
                            allowNetwork,
                            force),
                        snapshot.RefreshToken),
                    snapshot.RefreshToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The provider model refresh returned null.");
            var dynamicModels = GameModelProviderRegistration.ValidateModels(id, refreshed);
            lock (_gate)
            {
                if (!_providers.TryGetValue(id, out var current)
                    || current.Generation != snapshot.Generation
                    || current.RefreshGeneration != snapshot.RefreshGeneration
                    || !ReferenceEquals(current.Registration, snapshot.Registration))
                {
                    return new GameModelRefreshResult(
                        id,
                        GameModelRefreshStatus.StaleRegistration,
                        snapshot.Models.Count);
                }
            }

            var save = await CancellableOperation.WaitAsync(
                _store.SaveAsync(
                    new GameStoredModelCatalog(
                        id,
                        snapshot.Registration.CatalogVersion,
                        dynamicModels,
                        _clock()),
                    stored?.Revision ?? 0,
                    snapshot.RefreshToken),
                snapshot.RefreshToken).ConfigureAwait(false);
            if (save.Status == GameModelCatalogSaveStatus.Conflict)
            {
                return new GameModelRefreshResult(id, GameModelRefreshStatus.StoreConflict, currentModels.Count);
            }

            lock (_gate)
            {
                if (!_providers.TryGetValue(id, out var current)
                    || current.Generation != snapshot.Generation
                    || current.RefreshGeneration != snapshot.RefreshGeneration
                    || !ReferenceEquals(current.Registration, snapshot.Registration))
                {
                    return new GameModelRefreshResult(id, GameModelRefreshStatus.StaleRegistration, snapshot.Models.Count);
                }

                var merged = Merge(current.Registration.Models, dynamicModels);
                var changed = !Equivalent(current.CurrentModels, merged);
                current.DynamicModels = dynamicModels;
                current.CurrentModels = merged;
                return new GameModelRefreshResult(
                    id,
                    changed ? GameModelRefreshStatus.Updated : GameModelRefreshStatus.Unchanged,
                    merged.Count);
            }
        }
        catch (OperationCanceledException) when (snapshot.RefreshToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                var stale = !_providers.TryGetValue(id, out var current)
                    || current.Generation != snapshot.Generation
                    || current.RefreshGeneration != snapshot.RefreshGeneration;
                return new GameModelRefreshResult(
                    id,
                    stale ? GameModelRefreshStatus.StaleRegistration : GameModelRefreshStatus.Canceled,
                    snapshot.Models.Count);
            }
        }
        catch (Exception exception)
        {
            return new GameModelRefreshResult(id, GameModelRefreshStatus.Failed, snapshot.Models.Count, exception);
        }
        finally
        {
            if (refreshGateAcquired)
            {
                snapshot.RefreshGate.Release();
            }

            lock (_gate)
            {
                if (_providers.TryGetValue(id, out var current)
                    && current.Generation == snapshot.Generation)
                {
                    current.CompleteRefresh(snapshot.RefreshGeneration);
                }
            }
        }
    }

    public async ValueTask<IReadOnlyList<GameModelRefreshResult>> RefreshAsync(
        IReadOnlyCollection<string>? providerIds = null,
        bool allowNetwork = true,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        string[] ids;
        lock (_gate)
        {
            ids = (providerIds ?? _providers.Keys.ToArray())
                .Select(id => GameModelDescriptor.RequireId(id, nameof(providerIds)))
                .Distinct(StringComparer.Ordinal)
                .Where(id => _providers.ContainsKey(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }

        var tasks = ids.Select(id => RefreshAsync(id, allowNetwork, force, cancellationToken).AsTask()).ToArray();
        return Array.AsReadOnly(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private static IReadOnlyList<GameModelDescriptor> Merge(
        IReadOnlyList<GameModelDescriptor> baseline,
        IReadOnlyList<GameModelDescriptor> dynamicModels)
    {
        var merged = baseline.ToList();
        foreach (var model in dynamicModels)
        {
            var index = merged.FindIndex(candidate => string.Equals(candidate.ModelId, model.ModelId, StringComparison.Ordinal));
            if (index >= 0)
            {
                merged[index] = model;
            }
            else
            {
                merged.Add(model);
            }
        }

        return Array.AsReadOnly(merged.ToArray());
    }

    private static bool Equivalent(
        IReadOnlyList<GameModelDescriptor> left,
        IReadOnlyList<GameModelDescriptor> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!Equivalent(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Equivalent(GameModelDescriptor left, GameModelDescriptor right) =>
        string.Equals(left.ProviderId, right.ProviderId, StringComparison.Ordinal)
        && string.Equals(left.ModelId, right.ModelId, StringComparison.Ordinal)
        && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
        && string.Equals(left.Api, right.Api, StringComparison.Ordinal)
        && Equals(left.BaseUrl, right.BaseUrl)
        && left.ContextWindowTokens == right.ContextWindowTokens
        && left.MaximumOutputTokens == right.MaximumOutputTokens
        && left.InputCapabilities == right.InputCapabilities
        && left.OutputCapabilities == right.OutputCapabilities
        && left.ReasoningLevels.SequenceEqual(right.ReasoningLevels)
        && left.ReasoningLevelValues.Count == right.ReasoningLevelValues.Count
        && left.ReasoningLevelValues.All(pair => right.ReasoningLevelValues.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal))
        && Equivalent(left.Cost, right.Cost)
        && Equivalent(left.Metadata, right.Metadata)
        && string.Equals(left.SamplingParametersJson, right.SamplingParametersJson, StringComparison.Ordinal)
        && Equivalent(left.Headers, right.Headers)
        && string.Equals(left.CompatibilityJson, right.CompatibilityJson, StringComparison.Ordinal);

    private static bool Equivalent(GameModelCost left, GameModelCost right) =>
        left.InputPerMillionTokens == right.InputPerMillionTokens
        && left.OutputPerMillionTokens == right.OutputPerMillionTokens
        && left.CacheReadPerMillionTokens == right.CacheReadPerMillionTokens
        && left.CacheWritePerMillionTokens == right.CacheWritePerMillionTokens
        && left.Tiers.Count == right.Tiers.Count
        && left.Tiers.Zip(right.Tiers, Equivalent).All(equivalent => equivalent);

    private static bool Equivalent(GameModelCostTier left, GameModelCostTier right) =>
        left.InputTokensAbove == right.InputTokensAbove
        && left.InputPerMillionTokens == right.InputPerMillionTokens
        && left.OutputPerMillionTokens == right.OutputPerMillionTokens
        && left.CacheReadPerMillionTokens == right.CacheReadPerMillionTokens
        && left.CacheWritePerMillionTokens == right.CacheWritePerMillionTokens;

    private static bool Equivalent<TValue>(
        IReadOnlyDictionary<string, TValue> left,
        IReadOnlyDictionary<string, TValue> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && EqualityComparer<TValue>.Default.Equals(pair.Value, value));

    private sealed class CatalogDispatchProvider : IModelProvider
    {
        private readonly GameModelCatalog _catalog;
        private readonly string _providerId;

        public CatalogDispatchProvider(GameModelCatalog catalog, string providerId)
        {
            _catalog = catalog;
            _providerId = providerId;
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            CancellationToken cancellationToken) =>
            _catalog.StreamAsync(_providerId, request, cancellationToken);
    }

    private sealed class Entry
    {
        private CancellationTokenSource? _refreshCancellation;

        public Entry(GameModelProviderRegistration registration, long generation)
        {
            Registration = registration;
            Generation = generation;
            DynamicModels = Array.Empty<GameModelDescriptor>();
            CurrentModels = registration.Models;
        }

        public GameModelProviderRegistration Registration { get; }

        public long Generation { get; }

        public long RefreshGeneration { get; private set; }

        public IReadOnlyList<GameModelDescriptor> DynamicModels { get; set; }

        public IReadOnlyList<GameModelDescriptor> CurrentModels { get; set; }

        public SemaphoreSlim RefreshGate { get; } = new(1, 1);

        public EntrySnapshot BeginRefresh(CancellationToken cancellationToken)
        {
            SupersedeRefresh();
            RefreshGeneration = checked(RefreshGeneration + 1);
            _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return new EntrySnapshot(this, RefreshGeneration, _refreshCancellation.Token);
        }

        public void CompleteRefresh(long refreshGeneration)
        {
            if (RefreshGeneration != refreshGeneration)
            {
                return;
            }

            _refreshCancellation?.Dispose();
            _refreshCancellation = null;
        }

        public void Supersede()
        {
            RefreshGeneration = checked(RefreshGeneration + 1);
            SupersedeRefresh();
        }

        private void SupersedeRefresh()
        {
            var cancellation = _refreshCancellation;
            _refreshCancellation = null;
            if (cancellation is null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // A refresh callback cannot block provider replacement or removal.
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private sealed class EntrySnapshot
    {
        public EntrySnapshot(Entry entry)
            : this(entry, entry.RefreshGeneration, default)
        {
        }

        public EntrySnapshot(Entry entry, long refreshGeneration, CancellationToken refreshToken)
        {
            Registration = entry.Registration;
            Generation = entry.Generation;
            RefreshGeneration = refreshGeneration;
            RefreshToken = refreshToken;
            RefreshGate = entry.RefreshGate;
            Models = entry.CurrentModels.ToArray();
        }

        public GameModelProviderRegistration Registration { get; }

        public long Generation { get; }

        public long RefreshGeneration { get; }

        public CancellationToken RefreshToken { get; }

        public SemaphoreSlim RefreshGate { get; }

        public IReadOnlyList<GameModelDescriptor> Models { get; }
    }
}

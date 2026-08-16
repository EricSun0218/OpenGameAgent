using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.Anthropic;
using OpenGameAgent.Providers.Bedrock;
using OpenGameAgent.Providers.Google;
using OpenGameAgent.Providers.Mistral;
using OpenGameAgent.Providers.OpenAI;
using OpenGameAgent.Providers.OpenAICompatible;
using OpenGameAgent.ProviderTransport;

namespace OpenGameAgent.Models.BuiltIn;

public sealed class BuiltInGameModelRuntime
{
    private static readonly IReadOnlyCollection<string> ApiIds = Array.AsReadOnly(new[]
    {
        BuiltInGameModelApis.AnthropicMessages,
        BuiltInGameModelApis.AzureOpenAiResponses,
        BuiltInGameModelApis.BedrockConverseStream,
        BuiltInGameModelApis.GoogleGenerativeAi,
        BuiltInGameModelApis.GoogleVertex,
        BuiltInGameModelApis.MistralConversations,
        BuiltInGameModelApis.OpenAiCodexResponses,
        BuiltInGameModelApis.OpenAiCompletions,
        BuiltInGameModelApis.OpenAiResponses,
    });

    private static readonly HashSet<string> SupportedApiIds = new(ApiIds, StringComparer.Ordinal);
    private readonly BuiltInGameModelRuntimeOptions _options;

    public BuiltInGameModelRuntime(BuiltInGameModelRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Directory = options.Directory ?? throw new ArgumentException("A model directory is required.", nameof(options));
        Catalog = options.Catalog ?? throw new ArgumentException("A model catalog is required.", nameof(options));
        if (options.GetEnvironmentVariable is null)
        {
            throw new ArgumentException("An environment-variable resolver is required.", nameof(options));
        }

        if (options.ResponseObserverTimeoutMilliseconds is < 1 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The response observer timeout must be between 1 and 30000 milliseconds.");
        }

        RegisterDirectory();
    }

    public GameModelDirectorySnapshot Directory { get; }

    public GameModelCatalog Catalog { get; }

    public static IReadOnlyCollection<string> SupportedApis => ApiIds;

    public IModelProvider CreateProvider(string providerId) =>
        Catalog.CreateProvider(RequireId(providerId, nameof(providerId)));

    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        string providerId,
        ModelRequest request,
        CancellationToken cancellationToken = default) =>
        StreamWithBoundaryAsync(
            RequireId(providerId, nameof(providerId)),
            request ?? throw new ArgumentNullException(nameof(request)),
            cancellationToken);

    public async ValueTask<ModelResponse> CompleteAsync(
        string providerId,
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in StreamAsync(providerId, request, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (streamEvent.IsTerminal)
            {
                return streamEvent.Response
                       ?? throw new InvalidOperationException("A terminal model event omitted its response.");
            }
        }

        return Failure(
            providerId,
            api: null,
            request.Model,
            "The model stream ended without a terminal response.").Response!;
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamWithBoundaryAsync(
        string providerId,
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requestedApi = Catalog.GetModel(providerId, request.Model)?.Api;

        IAsyncEnumerator<ModelStreamEvent>? enumerator = null;
        Exception? enumerationSetupError = null;
        try
        {
            enumerator = Catalog.CreateProvider(providerId).StreamAsync(request, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            enumerationSetupError = exception;
        }

        if (enumerationSetupError is not null)
        {
            yield return Failure(providerId, requestedApi, request.Model, ErrorMessage(enumerationSetupError));
            yield break;
        }

        var terminal = false;
        var failed = false;
        try
        {
            while (!terminal && !failed)
            {
                var moved = false;
                ModelStreamEvent? current = null;
                Exception? moveError = null;
                try
                {
                    moved = await enumerator!.MoveNextAsync().ConfigureAwait(false);
                    if (moved)
                    {
                        current = enumerator.Current;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    moveError = exception;
                }

                if (moveError is not null)
                {
                    failed = true;
                    yield return Failure(providerId, requestedApi, request.Model, ErrorMessage(moveError));
                    continue;
                }

                if (!moved)
                {
                    failed = true;
                    yield return Failure(
                        providerId,
                        requestedApi,
                        request.Model,
                        "The provider stream ended without a terminal response.");
                    continue;
                }

                if (current is null)
                {
                    failed = true;
                    yield return Failure(providerId, requestedApi, request.Model, "The provider emitted a null event.");
                    continue;
                }

                terminal = current.IsTerminal;
                yield return current;
            }
        }
        finally
        {
            try
            {
                await enumerator!.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Cleanup is best-effort. It must not replace a terminal response,
                // an in-band provider failure, or the caller's cancellation.
            }
        }
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamRegisteredAsync(
        string providerId,
        ModelRequest request,
        GameProviderAuthResolution? authentication,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = Catalog.GetProvider(providerId)?.Descriptor
                       ?? throw new InvalidOperationException($"Unknown model provider '{providerId}'.");
        var model = Catalog.GetModel(providerId, request.Model)
                    ?? throw new InvalidOperationException($"Unknown model '{providerId}/{request.Model}'.");
        if (!SupportedApiIds.Contains(model.Api))
        {
            throw new InvalidOperationException(
                $"Model '{providerId}/{request.Model}' uses unsupported API '{model.Api}'.");
        }

        var configuration = EnvironmentTransportConfiguration(model)
            .Overlay(AuthenticationTransportConfiguration(authentication));
        if (_options.ProviderConfigurations.TryGetValue(providerId, out var configured))
        {
            configuration = configuration.Overlay(ResolvedGameModelTransportConfiguration.Snapshot(configured));
        }

        if (_options.ResolveConfigurationAsync is not null)
        {
            var context = new GameModelTransportConfigurationContext(provider, model, request);
            var resolved = await ProviderCallbackRunner.RunAsync(
                    token => _options.ResolveConfigurationAsync(context, token),
                    cancellationToken)
                .ConfigureAwait(false);
            configuration = configuration.Overlay(ResolvedGameModelTransportConfiguration.Snapshot(resolved));
        }

        ValidateConfiguration(configuration);
        var compatibility = ModelCompatibility.Parse(model.CompatibilityJson);
        var normalizedRequest = ApplyApiRequestConfiguration(
            NormalizeRequest(request, model, compatibility),
            model,
            configuration);
        var headers = MergeHeaders(model.Headers, configuration.Headers);
        ValidateHeaders(model.Api, headers);
        var implementation = CreateImplementation(
            provider,
            model,
            configuration,
            authentication,
            compatibility,
            headers);
        await foreach (var streamEvent in implementation.StreamAsync(normalizedRequest, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return ApplyCatalogCost(streamEvent, model.Cost);
        }
    }

    private static ModelStreamEvent ApplyCatalogCost(ModelStreamEvent streamEvent, GameModelCost pricing)
    {
        if (streamEvent is null)
        {
            throw new InvalidOperationException("A model provider emitted a null event.");
        }

        if (streamEvent.IsTerminal)
        {
            return ModelStreamEvent.Terminal(ApplyCatalogCost(
                streamEvent.Response
                ?? throw new InvalidOperationException("A terminal model event is missing its response."),
                pricing));
        }

        return ModelStreamEvent.Update(
            streamEvent.Kind,
            ApplyCatalogCost(
                streamEvent.Partial
                ?? throw new InvalidOperationException("A model update is missing its partial response."),
                pricing),
            streamEvent.Delta,
            streamEvent.ContentIndex,
            streamEvent.ToolCallId,
            streamEvent.ToolName,
            streamEvent.ToolCall,
            streamEvent.Content);
    }

    private static ModelResponse ApplyCatalogCost(ModelResponse response, GameModelCost pricing)
    {
        if (response.Usage.Cost.IsKnown)
        {
            return response;
        }

        var usage = response.Usage;
        var pricedUsage = new ModelUsage(
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheReadTokens,
            usage.CacheWriteTokens,
            usage.ReasoningTokens,
            usage.CacheWriteOneHourTokens,
            pricing.Estimate(usage));
        return new ModelResponse(
            response.Content,
            response.StopReason,
            pricedUsage,
            response.ErrorMessage,
            response.Provider,
            response.Api,
            response.ResponseModel,
            response.ResponseId,
            response.RawStopReason,
            response.EndTurn,
            response.Diagnostics,
            response.Deferred);
    }

    private static ResolvedGameModelTransportConfiguration AuthenticationTransportConfiguration(
        GameProviderAuthResolution? authentication)
    {
        if (authentication is null)
        {
            return ResolvedGameModelTransportConfiguration.Empty;
        }

        var configuration = new GameModelProviderTransportConfiguration
        {
            BaseUrl = authentication.BaseUrl,
        };
        foreach (var pair in authentication.Headers)
        {
            configuration.Headers[pair.Key] = pair.Value;
        }

        foreach (var pair in authentication.Configuration)
        {
            configuration.Options[pair.Key] = pair.Value;
        }

        return ResolvedGameModelTransportConfiguration.Snapshot(configuration);
    }

    private void RegisterDirectory()
    {
        var providerIds = Directory.Providers
            .Select(provider => provider.ProviderId)
            .ToHashSet(StringComparer.Ordinal);
        var unknownAuthentication = _options.Authentications.Keys.FirstOrDefault(id => !providerIds.Contains(id));
        if (unknownAuthentication is not null)
        {
            throw new ArgumentException(
                $"Authentication was configured for unknown provider '{unknownAuthentication}'.",
                nameof(_options.Authentications));
        }

        var unknownTransport = _options.ProviderConfigurations.Keys.FirstOrDefault(id => !providerIds.Contains(id));
        if (unknownTransport is not null)
        {
            throw new ArgumentException(
                $"Transport was configured for unknown provider '{unknownTransport}'.",
                nameof(_options.ProviderConfigurations));
        }

        foreach (var provider in Directory.Providers)
        {
            var models = Array.AsReadOnly(Directory.GetModels(provider.ProviderId)
                .Where(IsExecutableModel)
                .Select(ExecutableDescriptor)
                .ToArray());
            var authentication = _options.Authentications.TryGetValue(provider.ProviderId, out var configured)
                ? configured
                : _options.CreateAuthentication?.Invoke(provider, models)
                  ?? CreateDefaultAuthentication(provider, models);
            if (authentication is null)
            {
                throw new InvalidOperationException(
                    $"The authentication factory returned null for provider '{provider.ProviderId}'.");
            }

            var registration = new GameModelProviderRegistration(
                provider,
                new DirectoryDispatchProvider(this, provider.ProviderId),
                authentication,
                models,
                stream: (request, resolution, cancellationToken) => StreamRegisteredAsync(
                    provider.ProviderId,
                    request,
                    resolution,
                    cancellationToken),
                catalogVersion: Directory.Version);
            Catalog.Register(registration, _options.ReplaceExistingProviders);
        }
    }

    private static GameModelDescriptor ExecutableDescriptor(GameModelDescriptor model)
    {
        const GameModelInputCapabilities executableInput =
            GameModelInputCapabilities.Text
            | GameModelInputCapabilities.Image
            | GameModelInputCapabilities.StructuredData;
        const GameModelOutputCapabilities executableOutput =
            GameModelOutputCapabilities.Text
            | GameModelOutputCapabilities.StructuredData
            | GameModelOutputCapabilities.ToolCalls
            | GameModelOutputCapabilities.Reasoning;
        var input = model.InputCapabilities & executableInput;
        var output = model.OutputCapabilities & executableOutput;
        if (input == model.InputCapabilities && output == model.OutputCapabilities)
        {
            return model;
        }

        return new GameModelDescriptor(
            model.ProviderId,
            model.ModelId,
            model.DisplayName,
            model.ContextWindowTokens,
            model.MaximumOutputTokens,
            input,
            output,
            model.ReasoningLevels,
            model.Cost,
            model.Metadata,
            model.ReasoningLevelValues,
            model.Api,
            model.BaseUrl,
            model.SamplingParametersJson,
            model.Headers,
            model.CompatibilityJson);
    }

    private static bool IsExecutableModel(GameModelDescriptor model) =>
        !(model.Api == BuiltInGameModelApis.OpenAiResponses
          && model.ModelId.Contains("realtime", StringComparison.OrdinalIgnoreCase));

    private IGameProviderAuthentication CreateDefaultAuthentication(
        GameProviderDescriptor provider,
        IReadOnlyList<GameModelDescriptor> models)
    {
        var apiIds = models.Select(model => model.Api).Distinct(StringComparer.Ordinal).ToArray();
        var onlyCodex = apiIds.Length > 0
                        && apiIds.All(api => api == BuiltInGameModelApis.OpenAiCodexResponses);
        var directorySources = onlyCodex
            ? Array.Empty<KeyValuePair<string, GameCredentialKind>>()
            : DirectoryCredentialSources(provider, apiIds);
        if (provider.IsLocal && !onlyCodex
            || apiIds.Length > 0
            && apiIds.All(api => api == BuiltInGameModelApis.BedrockConverseStream))
        {
            return new StaticGameProviderAuthentication(configured: true, source: "ambient");
        }

        var variables = directorySources
            .Concat(apiIds.SelectMany(api => DefaultCredentialSources(provider.ProviderId, api)))
            .Concat(CodexCredentialSources(provider.ProviderId, apiIds))
            .GroupBy(source => source.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return new EnvironmentChainAuthentication(
            variables,
            _options.GetEnvironmentVariable,
            apiIds.Contains(BuiltInGameModelApis.GoogleVertex, StringComparer.Ordinal)
                ? () => HasVertexAmbientConfiguration(provider.ProviderId)
                : null);
    }

    private static IReadOnlyList<KeyValuePair<string, GameCredentialKind>> DirectoryCredentialSources(
        GameProviderDescriptor provider,
        IReadOnlyCollection<string> apiIds)
    {
        if (!provider.Metadata.TryGetValue(
                BuiltInGameModelConfigurationKeys.EnvironmentVariablesMetadata,
                out var declared))
        {
            return Array.Empty<KeyValuePair<string, GameCredentialKind>>();
        }

        var variables = declared.Split(',').Select(value => value.Trim()).ToArray();
        if (variables.Length is < 1 or > 256
            || variables.Any(variable => !IsEnvironmentVariableName(variable)))
        {
            throw new ArgumentException(
                $"Provider '{provider.ProviderId}' declares invalid environment variable metadata.");
        }

        var kind = apiIds.All(api => api == BuiltInGameModelApis.GoogleVertex)
            ? GameCredentialKind.BearerToken
            : GameCredentialKind.ApiKey;
        return Array.AsReadOnly(variables
            .Where(IsCredentialEnvironmentVariable)
            .Distinct(StringComparer.Ordinal)
            .Select(variable => new KeyValuePair<string, GameCredentialKind>(variable, kind))
            .ToArray());
    }

    private static bool IsEnvironmentVariableName(string value) =>
        value.Length is > 0 and <= 256
        && (IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => IsAsciiLetter(character) || character is >= '0' and <= '9' || character == '_');

    private static bool IsAsciiLetter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsCredentialEnvironmentVariable(string value) =>
        value.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith("_API_TOKEN", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith("_ACCESS_TOKEN", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith("_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase)
        || value.Contains("_BEARER_TOKEN", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<KeyValuePair<string, GameCredentialKind>> DefaultCredentialSources(
        string providerId,
        string api) => api switch
        {
            BuiltInGameModelApis.AnthropicMessages =>
                Sources(GameCredentialKind.ApiKey, "ANTHROPIC_API_KEY"),
            BuiltInGameModelApis.AzureOpenAiResponses =>
                Sources(GameCredentialKind.ApiKey, "AZURE_OPENAI_API_KEY"),
            BuiltInGameModelApis.GoogleGenerativeAi =>
                Sources(GameCredentialKind.ApiKey, "GEMINI_API_KEY", "GOOGLE_API_KEY"),
            BuiltInGameModelApis.GoogleVertex =>
                Sources(GameCredentialKind.ApiKey, "GOOGLE_CLOUD_API_KEY")
                    .Concat(Sources(
                        GameCredentialKind.BearerToken,
                        "GOOGLE_VERTEX_ACCESS_TOKEN",
                        "GOOGLE_OAUTH_ACCESS_TOKEN")),
            BuiltInGameModelApis.MistralConversations =>
                Sources(GameCredentialKind.ApiKey, "MISTRAL_API_KEY"),
            BuiltInGameModelApis.OpenAiResponses =>
                Sources(GameCredentialKind.ApiKey, "OPENAI_API_KEY"),
            BuiltInGameModelApis.OpenAiCodexResponses =>
                Array.Empty<KeyValuePair<string, GameCredentialKind>>(),
            BuiltInGameModelApis.OpenAiCompletions =>
                Sources(
                    GameCredentialKind.ApiKey,
                    EnvironmentPrefix(providerId) + "_API_KEY",
                    "OPENAI_COMPATIBLE_API_KEY"),
            _ => Array.Empty<KeyValuePair<string, GameCredentialKind>>(),
        };

    private static IReadOnlyList<KeyValuePair<string, GameCredentialKind>> Sources(
        GameCredentialKind kind,
        params string[] variables) =>
        Array.AsReadOnly(variables
            .Select(variable => new KeyValuePair<string, GameCredentialKind>(variable, kind))
            .ToArray());

    private IReadOnlyList<KeyValuePair<string, GameCredentialKind>> CodexCredentialSources(
        string providerId,
        IReadOnlyCollection<string> apiIds)
    {
        if (!apiIds.Contains(BuiltInGameModelApis.OpenAiCodexResponses, StringComparer.Ordinal)
            || !_options.ProviderConfigurations.TryGetValue(providerId, out var configured)
            || !configured.Options.TryGetValue(
                BuiltInGameModelConfigurationKeys.OpenAiCodexEnvironmentVariable,
                out var variable)
            || string.IsNullOrWhiteSpace(variable))
        {
            return Array.Empty<KeyValuePair<string, GameCredentialKind>>();
        }

        variable = variable.Trim();
        if (!IsEnvironmentVariableName(variable))
        {
            throw new ArgumentException(
                $"Provider '{providerId}' declares an invalid Codex environment variable.",
                nameof(_options.ProviderConfigurations));
        }

        return Sources(GameCredentialKind.OAuth, variable);
    }

    private ResolvedGameModelTransportConfiguration EnvironmentTransportConfiguration(
        GameModelDescriptor model)
    {
        var configuration = new GameModelProviderTransportConfiguration();
        if (model.Api == BuiltInGameModelApis.GoogleVertex)
        {
            AddEnvironmentOption(
                configuration,
                BuiltInGameModelConfigurationKeys.GoogleProject,
                "GOOGLE_VERTEX_PROJECT",
                "GOOGLE_CLOUD_PROJECT",
                "GCLOUD_PROJECT");
            AddEnvironmentOption(
                configuration,
                BuiltInGameModelConfigurationKeys.GoogleLocation,
                "GOOGLE_VERTEX_LOCATION",
                "GOOGLE_CLOUD_LOCATION",
                "GOOGLE_CLOUD_REGION");
        }
        else if (model.Api == BuiltInGameModelApis.AzureOpenAiResponses)
        {
            AddEnvironmentBaseUrl(configuration, "AZURE_OPENAI_BASE_URL", "AZURE_OPENAI_ENDPOINT");
            AddEnvironmentOption(
                configuration,
                BuiltInGameModelConfigurationKeys.AzureApiVersion,
                "AZURE_OPENAI_API_VERSION");
            AddEnvironmentOption(
                configuration,
                BuiltInGameModelConfigurationKeys.AzureDeploymentName,
                "AZURE_OPENAI_DEPLOYMENT_NAME");
            AddEnvironmentOption(
                configuration,
                BuiltInGameModelConfigurationKeys.AzureResourceName,
                "AZURE_OPENAI_RESOURCE_NAME");
        }
        else if (model.Api == BuiltInGameModelApis.BedrockConverseStream)
        {
            AddEnvironmentOption(
                configuration,
                BuiltInGameModelConfigurationKeys.AwsRegion,
                "AWS_REGION",
                "AWS_DEFAULT_REGION");
            AddEnvironmentOption(
                configuration,
                BuiltInGameModelConfigurationKeys.AwsProfile,
                "AWS_PROFILE");
        }

        return ResolvedGameModelTransportConfiguration.Snapshot(configuration);
    }

    private void AddEnvironmentBaseUrl(
        GameModelProviderTransportConfiguration configuration,
        params string[] environmentNames)
    {
        var value = EnvironmentValue(environmentNames);
        if (value is null)
        {
            return;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("An Azure OpenAI endpoint environment variable is invalid.");
        }

        configuration.BaseUrl = endpoint;
    }

    private bool HasVertexAmbientConfiguration(string providerId)
    {
        if (_options.VertexApplicationDefaultCredential is null)
        {
            return false;
        }

        if (_options.ProviderConfigurations.TryGetValue(providerId, out var configured)
            && (configured.BaseUrl is not null
                || HasOption(configured, BuiltInGameModelConfigurationKeys.GoogleProject, "GOOGLE_VERTEX_PROJECT", "GOOGLE_CLOUD_PROJECT")
                && HasOption(configured, BuiltInGameModelConfigurationKeys.GoogleLocation, "GOOGLE_VERTEX_LOCATION", "GOOGLE_CLOUD_LOCATION")))
        {
            return true;
        }

        return EnvironmentValue("GOOGLE_VERTEX_PROJECT", "GOOGLE_CLOUD_PROJECT", "GCLOUD_PROJECT") is not null
               && EnvironmentValue("GOOGLE_VERTEX_LOCATION", "GOOGLE_CLOUD_LOCATION", "GOOGLE_CLOUD_REGION") is not null;
    }

    private static bool HasOption(
        GameModelProviderTransportConfiguration configuration,
        params string[] keys) =>
        keys.Any(key => configuration.Options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value));

    private IModelProvider CreateImplementation(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration,
        GameProviderAuthResolution? authentication,
        ModelCompatibility compatibility,
        IReadOnlyDictionary<string, string?> headers)
    {
        return model.Api switch
        {
            BuiltInGameModelApis.AnthropicMessages => CreateAnthropic(provider, model, configuration, authentication, compatibility, headers),
            BuiltInGameModelApis.AzureOpenAiResponses => CreateAzureOpenAi(provider, model, configuration, authentication, compatibility, headers),
            BuiltInGameModelApis.BedrockConverseStream => CreateBedrock(provider, model, configuration, authentication, compatibility, headers),
            BuiltInGameModelApis.GoogleGenerativeAi => CreateGoogle(provider, model, configuration, authentication, compatibility, headers, GoogleApiFlavor.Gemini),
            BuiltInGameModelApis.GoogleVertex => CreateGoogle(provider, model, configuration, authentication, compatibility, headers, GoogleApiFlavor.Vertex),
            BuiltInGameModelApis.MistralConversations => CreateMistral(provider, model, configuration, authentication, compatibility, headers),
            BuiltInGameModelApis.OpenAiCodexResponses => CreateOpenAiCodex(provider, model, configuration, authentication, compatibility, headers),
            BuiltInGameModelApis.OpenAiCompletions => CreateOpenAiCompatible(provider, model, configuration, authentication, compatibility, headers),
            BuiltInGameModelApis.OpenAiResponses => CreateOpenAi(provider, model, configuration, authentication, compatibility, headers),
            _ => throw new InvalidOperationException($"Unsupported model API '{model.Api}'."),
        };
    }

    private IModelProvider CreateAnthropic(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration,
        GameProviderAuthResolution? authentication,
        ModelCompatibility compatibility,
        IReadOnlyDictionary<string, string?> headers)
    {
        var endpoint = Endpoint(
            ResolveBaseUrl(model, configuration),
            "https://api.anthropic.com/v1",
            "messages");
        var credential = authentication?.Credential;
        var providerHeaders = new Dictionary<string, string?>(headers, StringComparer.OrdinalIgnoreCase);
        var apiKey = credential?.Secret;
        if (HasHeaderValue(providerHeaders, "Authorization") || HasHeaderValue(providerHeaders, "x-api-key"))
        {
            apiKey = null;
        }
        else if (credential?.Kind is GameCredentialKind.BearerToken
                     or GameCredentialKind.OAuth
                     or GameCredentialKind.DeveloperHostedToken)
        {
            providerHeaders["Authorization"] = "Bearer " + credential.Secret;
            apiKey = null;
        }

        var options = new AnthropicMessagesProviderOptions(_options.HttpClient, endpoint)
        {
            ApiKey = apiKey,
            ProviderId = provider.ProviderId,
            ApiId = model.Api,
            AllowInsecureHttp = AllowsInsecureEndpoint(endpoint),
            SupportsEagerToolInputStreaming = compatibility.SupportsEagerToolInputStreaming ?? true,
            SupportsLongCacheRetention = compatibility.SupportsLongCacheRetention ?? true,
            SendSessionAffinityHeaders = compatibility.SendSessionAffinityHeaders ?? false,
            SupportsCacheControlOnTools = compatibility.SupportsCacheControlOnTools ?? true,
            SupportsTemperature = compatibility.SupportsTemperature ?? true,
            ForceAdaptiveThinking = compatibility.ForceAdaptiveThinking ?? false,
            AllowEmptyThinkingSignature = compatibility.AllowEmptySignature ?? false,
            SupportsStrictTools = compatibility.SupportsStrictTools ?? false,
            SupportsToolReferences = compatibility.SupportsToolReferences ?? false,
            InterleavedThinking = compatibility.Interleaved ?? true,
            ResponseObserver = _options.ResponseObserver,
            ResponseObserverTimeoutMilliseconds = _options.ResponseObserverTimeoutMilliseconds,
        };
        foreach (var pair in providerHeaders)
        {
            options.Headers[pair.Key] = pair.Value;
        }
        return new AnthropicMessagesProvider(options);
    }

    private IModelProvider CreateAzureOpenAi(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration,
        GameProviderAuthResolution? authentication,
        ModelCompatibility compatibility,
        IReadOnlyDictionary<string, string?> headers)
    {
        var hasExplicitAuthentication = HasHeaderValue(headers, "api-key")
                                        || HasHeaderValue(headers, "Authorization");
        var credential = authentication?.Credential;
        if (!hasExplicitAuthentication
            && credential?.Kind is not (GameCredentialKind.ApiKey or GameCredentialKind.DeveloperHostedToken))
        {
            throw new InvalidOperationException(
                "Azure OpenAI Responses requires an API key or an explicit authentication header.");
        }

        var apiVersion = RequireBoundedConfigurationValue(
            Option(configuration, BuiltInGameModelConfigurationKeys.AzureApiVersion) ?? "v1",
            256,
            "Azure OpenAI API version");
        var resourceName = Option(configuration, BuiltInGameModelConfigurationKeys.AzureResourceName);
        var configuredEndpoint = ResolveBaseUrl(model, configuration);
        OpenAIResponsesProviderOptions options;
        if (configuredEndpoint is not null)
        {
            options = AzureOpenAIResponses.CreateOptions(
                _options.HttpClient,
                configuredEndpoint.OriginalString,
                hasExplicitAuthentication ? null : credential!.Secret,
                apiVersion);
        }
        else if (resourceName is not null)
        {
            resourceName = RequireBoundedConfigurationValue(
                resourceName,
                256,
                "Azure OpenAI resource name");
            options = AzureOpenAIResponses.CreateOptionsForResource(
                _options.HttpClient,
                resourceName,
                hasExplicitAuthentication ? null : credential!.Secret,
                apiVersion);
        }
        else
        {
            throw new InvalidOperationException(
                "Azure OpenAI Responses requires a base URL or Azure resource name.");
        }

        options.ProviderId = provider.ProviderId;
        options.ApiId = model.Api;
        options.AllowInsecureHttp = AllowsInsecureEndpoint(options.Endpoint);
        options.SupportsDeveloperRole = compatibility.SupportsDeveloperRole ?? options.SupportsDeveloperRole;
        options.SupportsStrictTools = compatibility.SupportsStrictMode ?? options.SupportsStrictTools;
        options.SupportsGrammarTools = compatibility.SupportsOpenAiGrammarTools ?? false;
        options.SupportsAdditionalTools = compatibility.SupportsAdditionalTools ?? false;
        options.SupportsToolSearch = compatibility.SupportsToolSearch ?? false;
        options.SupportsExplicitPromptCacheMode = compatibility.SupportsExplicitPromptCacheMode ?? false;
        options.SupportsLongCacheRetention = false;
        options.SessionAffinityFormat = ParseOpenAiSessionAffinityFormat(compatibility.SessionAffinityFormat);
        options.ResponseObserver = _options.ResponseObserver;
        options.ResponseObserverTimeoutMilliseconds = _options.ResponseObserverTimeoutMilliseconds;
        CopyNullableHeaders(headers, options.Headers);
        return new OpenAIResponsesProvider(options);
    }

    private IModelProvider CreateBedrock(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration,
        GameProviderAuthResolution? authentication,
        ModelCompatibility compatibility,
        IReadOnlyDictionary<string, string?> headers)
    {
        var credential = authentication?.Credential;
        string? secretAccessKey = null;
        var hasSecretAccessKey = credential is not null
                                 && credential.Metadata.TryGetValue(
                                     BuiltInGameModelConfigurationKeys.AwsSecretAccessKeyMetadata,
                                     out secretAccessKey);
        var options = new BedrockConverseProviderOptions
        {
            ProviderId = provider.ProviderId,
            ApiId = model.Api,
            Transport = configuration.BedrockTransport,
            Region = Option(configuration, BuiltInGameModelConfigurationKeys.AwsRegion),
            Profile = Option(configuration, BuiltInGameModelConfigurationKeys.AwsProfile),
            ServiceUrl = ResolveBaseUrl(model, configuration)?.OriginalString,
            AllowInsecureHttp = _options.AllowInsecureHttp,
            AccessKeyId = hasSecretAccessKey ? credential!.Secret : null,
            SecretAccessKey = hasSecretAccessKey ? secretAccessKey : null,
            SessionToken = credential?.Metadata.TryGetValue(
                BuiltInGameModelConfigurationKeys.AwsSessionTokenMetadata,
                out var sessionToken) == true
                ? sessionToken
                : null,
            BearerToken = credential is not null && !hasSecretAccessKey ? credential.Secret : null,
            SkipAuthentication = BooleanOption(
                configuration,
                BuiltInGameModelConfigurationKeys.AwsSkipAuthentication,
                defaultValue: false),
            SupportsStrictTools = compatibility.SupportsStrictMode ?? false,
            InterleavedThinking = compatibility.Interleaved ?? true,
            ResponseObserver = _options.ResponseObserver,
            ResponseObserverTimeoutMilliseconds = _options.ResponseObserverTimeoutMilliseconds,
        };
        CopyNullableHeaders(headers, options.Headers);
        return new BedrockConverseProvider(options);
    }

    private IModelProvider CreateGoogle(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration,
        GameProviderAuthResolution? authentication,
        ModelCompatibility compatibility,
        IReadOnlyDictionary<string, string?> headers,
        GoogleApiFlavor flavor)
    {
        var endpoint = flavor == GoogleApiFlavor.Vertex
            ? VertexEndpoint(model, configuration)
            : GoogleEndpoint(ResolveBaseUrl(model, configuration));
        var credential = authentication?.Credential;
        var hasExplicitAuthentication = HasHeaderValue(headers, "Authorization")
                                        || HasHeaderValue(headers, "x-goog-api-key");
        var placement = hasExplicitAuthentication || credential is null
            ? flavor == GoogleApiFlavor.Vertex
              && !hasExplicitAuthentication
              && _options.VertexApplicationDefaultCredential is not null
                ? GoogleCredentialPlacement.BearerToken
                : GoogleCredentialPlacement.None
            : credential.Kind is GameCredentialKind.BearerToken
                    or GameCredentialKind.OAuth
                    or GameCredentialKind.DeveloperHostedToken
                ? GoogleCredentialPlacement.BearerToken
                : GoogleCredentialPlacement.ApiKeyHeader;
        var options = new GoogleGenerativeProviderOptions(_options.HttpClient, endpoint, flavor)
        {
            ProviderId = provider.ProviderId,
            ApiId = model.Api,
            Credential = hasExplicitAuthentication ? null : credential?.Secret,
            GetCredentialAsync = flavor == GoogleApiFlavor.Vertex
                                 && !hasExplicitAuthentication
                                 && credential is null
                ? _options.VertexApplicationDefaultCredential
                : null,
            CredentialPlacement = placement,
            SupportsImages = model.InputCapabilities.HasFlag(GameModelInputCapabilities.Image),
            UseLegacyOpenApiToolSchemas = compatibility.UseLegacyOpenApiToolSchemas ?? false,
            AllowInsecureHttp = AllowsInsecureEndpoint(endpoint),
            ResponseObserver = _options.ResponseObserver,
            ResponseObserverTimeoutMilliseconds = _options.ResponseObserverTimeoutMilliseconds,
        };
        CopyNullableHeaders(headers, options.Headers);
        return new GoogleGenerativeProvider(options);
    }

    private IModelProvider CreateMistral(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration,
        GameProviderAuthResolution? authentication,
        ModelCompatibility compatibility,
        IReadOnlyDictionary<string, string?> headers)
    {
        var endpoint = Endpoint(
            ResolveBaseUrl(model, configuration),
            "https://api.mistral.ai/v1",
            "chat/completions");
        var credential = BearerValue(headers) ?? authentication?.Credential?.Secret;
        var options = new MistralConversationsProviderOptions(_options.HttpClient, endpoint)
        {
            ApiKey = credential,
            ProviderId = provider.ProviderId,
            ApiId = model.Api,
            SupportsImages = model.InputCapabilities.HasFlag(GameModelInputCapabilities.Image),
            ReasoningMode = ParseMistralReasoningMode(compatibility.ReasoningMode),
            AllowInsecureHttp = AllowsInsecureEndpoint(endpoint),
            ResponseObserver = _options.ResponseObserver,
            ResponseObserverTimeoutMilliseconds = _options.ResponseObserverTimeoutMilliseconds,
        };
        CopyNullableHeaders(headers, options.Headers);
        return new MistralConversationsProvider(options);
    }

    private IModelProvider CreateOpenAiCompatible(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration,
        GameProviderAuthResolution? authentication,
        ModelCompatibility compatibility,
        IReadOnlyDictionary<string, string?> headers)
    {
        var endpoint = Endpoint(
            ResolveBaseUrl(model, configuration),
            "https://api.openai.com/v1",
            "chat/completions");
        var apiKeyHeader = Option(configuration, BuiltInGameModelConfigurationKeys.AuthenticationHeader)
                           ?? (string.Equals(provider.ProviderId, "cloudflare-ai-gateway", StringComparison.Ordinal)
                               ? "cf-aig-authorization"
                               : "Authorization");
        var options = new OpenAICompatibleProviderOptions(_options.HttpClient, endpoint)
        {
            ApiKey = HasHeaderValue(headers, apiKeyHeader) ? null : authentication?.Credential?.Secret,
            ProviderId = provider.ProviderId,
            ApiId = model.Api,
            ApiKeyHeader = apiKeyHeader,
            ApiKeyScheme = Option(configuration, BuiltInGameModelConfigurationKeys.AuthenticationScheme)
                           ?? "Bearer",
            AllowInsecureHttp = AllowsInsecureEndpoint(endpoint),
            ResponseObserver = _options.ResponseObserver,
            ResponseObserverTimeoutMilliseconds = _options.ResponseObserverTimeoutMilliseconds,
        };
        if (!string.IsNullOrWhiteSpace(compatibility.InterleavedField)
            && !options.ReasoningDeltaFields.Contains(compatibility.InterleavedField!, StringComparer.Ordinal))
        {
            options.ReasoningDeltaFields.Insert(0, compatibility.InterleavedField!);
        }

        options.Protocol.SupportsStore = compatibility.SupportsStore ?? true;
        options.Protocol.SupportsDeveloperRole = compatibility.SupportsDeveloperRole ?? true;
        options.Protocol.SupportsReasoningEffort = compatibility.SupportsReasoningEffort ?? true;
        options.Protocol.SupportsUsageInStreaming = compatibility.SupportsUsageInStreaming ?? true;
        options.Protocol.SupportsFinishReason = compatibility.SupportsFinishReason ?? true;
        options.Protocol.MaxTokensField = ParseMaxTokensField(compatibility.MaxTokensField);
        options.Protocol.RequiresToolResultName = compatibility.RequiresToolResultName ?? false;
        options.Protocol.RequiresAssistantAfterToolResult = compatibility.RequiresAssistantAfterToolResult ?? false;
        options.Protocol.RequiresThinkingAsText = compatibility.RequiresThinkingAsText ?? false;
        options.Protocol.RequiresReasoningContentOnAssistantMessages =
            compatibility.RequiresReasoningContentOnAssistantMessages ?? false;
        options.Protocol.ThinkingFormat = ParseThinkingFormat(compatibility.ThinkingFormat);
        options.Protocol.ZaiToolStream = compatibility.ZaiToolStream ?? false;
        options.Protocol.SupportsThinkingTokenBudget = compatibility.SupportsThinkingTokenBudget ?? false;
        options.Protocol.SupportsStrictMode = compatibility.SupportsStrictMode ?? true;
        options.Protocol.SupportsGrammarTools = compatibility.SupportsOpenAiGrammarTools ?? false;
        options.Protocol.CacheControlFormat = ParseCacheControlFormat(compatibility.CacheControlFormat);
        options.Protocol.SendSessionAffinityHeaders = compatibility.SendSessionAffinityHeaders ?? false;
        options.Protocol.SessionAffinityFormat = ParseCompatibleSessionAffinityFormat(
            compatibility.SessionAffinityFormat);
        options.Protocol.DeferredToolsMode = ParseDeferredToolsMode(compatibility.DeferredToolsMode);
        options.Protocol.SupportsLongCacheRetention = compatibility.SupportsLongCacheRetention ?? true;
        options.Protocol.ChatTemplateArgumentsJson = compatibility.ChatTemplateArgumentsJson;
        options.Protocol.ChatTemplateKeywordArgumentsJson = compatibility.ChatTemplateKeywordArgumentsJson;

        CopyNullableHeaders(headers, options.Headers);
        return new OpenAICompatibleProvider(options);
    }

    private IModelProvider CreateOpenAiCodex(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration,
        GameProviderAuthResolution? authentication,
        ModelCompatibility compatibility,
        IReadOnlyDictionary<string, string?> headers)
    {
        var explicitBearer = BearerValue(headers);
        var credential = authentication?.Credential;
        if (explicitBearer is null
            && credential?.Kind is not (GameCredentialKind.OAuth
                or GameCredentialKind.BearerToken
                or GameCredentialKind.DeveloperHostedToken))
        {
            throw new InvalidOperationException(
                "OpenAI Codex Responses requires an OAuth or bearer access token; API keys are not accepted.");
        }

        var endpoint = Endpoint(
            ResolveBaseUrl(model, configuration),
            "https://chatgpt.com/backend-api/codex",
            "responses");
        var options = OpenAICodexResponses.CreateOptions(
            _options.HttpClient,
            explicitBearer ?? credential!.Secret,
            endpoint,
            compatibility.SupportsAdditionalTools ?? true,
            compatibility.SupportsToolSearch ?? true);
        options.ProviderId = provider.ProviderId;
        options.ApiId = model.Api;
        options.AllowInsecureHttp = AllowsInsecureEndpoint(endpoint);
        options.SupportsStrictTools = compatibility.SupportsStrictMode ?? options.SupportsStrictTools;
        options.SupportsGrammarTools = compatibility.SupportsOpenAiGrammarTools ?? options.SupportsGrammarTools;
        options.SupportsExplicitPromptCacheMode = compatibility.SupportsExplicitPromptCacheMode ?? false;
        options.SupportsLongCacheRetention = false;
        options.ResponseObserver = _options.ResponseObserver;
        options.ResponseObserverTimeoutMilliseconds = _options.ResponseObserverTimeoutMilliseconds;
        CopyNullableHeaders(headers, options.Headers);

        var configuredAccountId = headers.TryGetValue("chatgpt-account-id", out var accountHeader)
                                  && accountHeader is not null
            ? accountHeader
            : Option(configuration, BuiltInGameModelConfigurationKeys.OpenAiCodexAccountId)
              ?? (credential?.Metadata.TryGetValue(
                  BuiltInGameModelConfigurationKeys.OpenAiCodexAccountId,
                  out var accountMetadata) == true
                  ? accountMetadata
                  : null);
        if (configuredAccountId is not null)
        {
            options.Headers["chatgpt-account-id"] = RequireBoundedConfigurationValue(
                configuredAccountId,
                512,
                "OpenAI Codex account ID");
        }

        return new OpenAIResponsesProvider(options);
    }

    private IModelProvider CreateOpenAi(
        GameProviderDescriptor provider,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration,
        GameProviderAuthResolution? authentication,
        ModelCompatibility compatibility,
        IReadOnlyDictionary<string, string?> headers)
    {
        var credential = authentication?.Credential;
        var authenticationStyle = ParseOpenAiAuthenticationStyle(configuration, credential, headers);
        var endpoint = Endpoint(
            ResolveBaseUrl(model, configuration),
            "https://api.openai.com/v1",
            "responses");
        var options = new OpenAIResponsesProviderOptions(_options.HttpClient, endpoint)
        {
            ApiKey = credential?.Secret,
            ProviderId = provider.ProviderId,
            ApiId = model.Api,
            AuthenticationStyle = authenticationStyle,
            ApiKeyHeaderName = Option(configuration, BuiltInGameModelConfigurationKeys.AuthenticationHeader)
                               ?? "api-key",
            AllowInsecureHttp = AllowsInsecureEndpoint(endpoint),
            SupportsDeveloperRole = compatibility.SupportsDeveloperRole ?? true,
            SupportsStrictTools = compatibility.SupportsStrictMode ?? false,
            SupportsGrammarTools = compatibility.SupportsOpenAiGrammarTools ?? false,
            SupportsAdditionalTools = compatibility.SupportsAdditionalTools ?? false,
            SupportsToolSearch = compatibility.SupportsToolSearch ?? false,
            SupportsExplicitPromptCacheMode = compatibility.SupportsExplicitPromptCacheMode ?? false,
            SupportsLongCacheRetention = compatibility.SupportsLongCacheRetention ?? true,
            SessionAffinityFormat = ParseOpenAiSessionAffinityFormat(compatibility.SessionAffinityFormat),
            ResponseObserver = _options.ResponseObserver,
            ResponseObserverTimeoutMilliseconds = _options.ResponseObserverTimeoutMilliseconds,
        };
        CopyNullableHeaders(headers, options.Headers);
        return new OpenAIResponsesProvider(options);
    }

    private string? EnvironmentValue(params string[] names)
    {
        foreach (var name in names)
        {
            var value = _options.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private void AddEnvironmentOption(
        GameModelProviderTransportConfiguration configuration,
        string key,
        params string[] environmentNames)
    {
        var value = EnvironmentValue(environmentNames);
        if (value is not null)
        {
            configuration.Options[key] = value;
        }
    }

    private static ModelRequest NormalizeRequest(
        ModelRequest request,
        GameModelDescriptor model,
        ModelCompatibility compatibility)
    {
        var parameters = request.Parameters.Clone();
        parameters.SamplingParametersJson ??= model.SamplingParametersJson;
        if (model.MaximumOutputTokens > 0)
        {
            parameters.MaxOutputTokens = parameters.MaxOutputTokens is { } requested
                ? Math.Min(requested, model.MaximumOutputTokens)
                : model.MaximumOutputTokens;
        }

        if (compatibility.SupportsTemperature == false)
        {
            parameters.Temperature = null;
        }

        if (model.Api == BuiltInGameModelApis.BedrockConverseStream)
        {
            ApplyBedrockSamplingExtensions(parameters);
        }

        return new ModelRequest(
            request.Model,
            request.SystemPrompt,
            NormalizeMessages(request.Messages, model.InputCapabilities),
            request.Tools,
            parameters,
            request.SessionId,
            request.RunId,
            request.Turn);
    }

    private static ModelRequest ApplyApiRequestConfiguration(
        ModelRequest request,
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration)
    {
        if (model.Api != BuiltInGameModelApis.AzureOpenAiResponses)
        {
            return request;
        }

        var deploymentName = Option(configuration, BuiltInGameModelConfigurationKeys.AzureDeploymentName);
        if (deploymentName is null)
        {
            return request;
        }

        deploymentName = RequireBoundedConfigurationValue(
            deploymentName,
            512,
            "Azure OpenAI deployment name");
        return new ModelRequest(
            deploymentName,
            request.SystemPrompt,
            request.Messages,
            request.Tools,
            request.Parameters,
            request.SessionId,
            request.RunId,
            request.Turn);
    }

    private static IReadOnlyList<AgentMessage> NormalizeMessages(
        IReadOnlyList<AgentMessage> messages,
        GameModelInputCapabilities capabilities)
    {
        var result = new AgentMessage[messages.Count];
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role is not AgentRole.User and not AgentRole.Tool)
            {
                result[index] = message;
                continue;
            }

            var content = message.Content.Select(part => NormalizeInputPart(part, capabilities)).ToArray();
            result[index] = content.SequenceEqual(message.Content)
                ? message
                : CopyMessage(message, content);
        }

        return Array.AsReadOnly(result);
    }

    private static AgentContent NormalizeInputPart(
        AgentContent content,
        GameModelInputCapabilities capabilities)
    {
        if (content is BinaryContent binary && !Supports(binary.MediaKind, capabilities))
        {
            throw UnsupportedMedia(binary.MediaKind);
        }

        if (content is ResourceContent resource
            && MediaKind(resource.MediaType) is { } mediaKind
            && !Supports(mediaKind, capabilities))
        {
            throw UnsupportedMedia(mediaKind);
        }

        if (content is JsonContent json
            && !capabilities.HasFlag(GameModelInputCapabilities.StructuredData))
        {
            return new TextContent(json.Json);
        }

        return content;
    }

    private static bool Supports(AgentMediaKind kind, GameModelInputCapabilities capabilities) => kind switch
    {
        AgentMediaKind.Image => capabilities.HasFlag(GameModelInputCapabilities.Image),
        AgentMediaKind.Audio => capabilities.HasFlag(GameModelInputCapabilities.Audio),
        AgentMediaKind.Video => capabilities.HasFlag(GameModelInputCapabilities.Video),
        AgentMediaKind.File => false,
        _ => false,
    };

    private static AgentMediaKind? MediaKind(string mediaType)
    {
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return AgentMediaKind.Image;
        }

        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return AgentMediaKind.Audio;
        }

        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return AgentMediaKind.Video;
        }

        return null;
    }

    private static ModelProviderException UnsupportedMedia(AgentMediaKind kind) =>
        new(
            $"The selected model does not declare {kind.ToString().ToLowerInvariant()} input support.",
            isTransient: false);

    private static AgentMessage CopyMessage(AgentMessage message, IReadOnlyList<AgentContent> content) => new(
        message.Role,
        content,
        message.Timestamp,
        customRole: message.CustomRole,
        toolCallId: message.ToolCallId,
        toolName: message.ToolName,
        isError: message.IsError,
        detailsJson: message.DetailsJson,
        metadata: message.Metadata,
        model: message.Model,
        stopReason: message.StopReason,
        usage: message.Usage,
        errorMessage: message.ErrorMessage,
        provider: message.Provider,
        api: message.Api,
        responseModel: message.ResponseModel,
        responseId: message.ResponseId,
        rawStopReason: message.RawStopReason,
        endTurn: message.EndTurn,
        diagnostics: message.Role == AgentRole.Assistant ? message.Diagnostics : null,
        deferred: message.Deferred,
        addedToolNames: message.Role == AgentRole.Tool ? message.AddedToolNames : null);

    private static void ApplyBedrockSamplingExtensions(ModelParameters parameters)
    {
        if (parameters.SamplingParametersJson is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(parameters.SamplingParametersJson);
        var extensions = new Dictionary<string, string>(parameters.Extensions, StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is "topP" or "top_p" or "stopSequences" or "stop_sequences")
            {
                continue;
            }

            if (!extensions.ContainsKey(property.Name))
            {
                extensions[property.Name] = property.Value.GetRawText();
            }
        }

        parameters.Extensions = new ReadOnlyDictionary<string, string>(extensions);
    }

    private static IReadOnlyDictionary<string, string?> MergeHeaders(
        IReadOnlyDictionary<string, string?> modelHeaders,
        IReadOnlyDictionary<string, string?> configurationHeaders)
    {
        var result = new Dictionary<string, string?>(modelHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in configurationHeaders)
        {
            result[pair.Key] = pair.Value;
        }

        return new ReadOnlyDictionary<string, string?>(result);
    }

    private static void CopyNullableHeaders(
        IReadOnlyDictionary<string, string?> source,
        IDictionary<string, string?> destination)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value;
        }
    }

    private static void ValidateConfiguration(ResolvedGameModelTransportConfiguration configuration)
    {
        if (configuration.BaseUrl is { } baseUrl
            && (!baseUrl.IsAbsoluteUri
                || baseUrl.UserInfo.Length > 0
                || baseUrl.Fragment.Length > 0
                || baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "A model provider base URL must be an absolute HTTP or HTTPS URL without embedded credentials.");
        }

        if (configuration.Headers.Count > 64 || configuration.Options.Count > 256)
        {
            throw new InvalidOperationException("A model provider configuration contains too many entries.");
        }

        ValidateHeaderValues(configuration.Headers);

        foreach (var pair in configuration.Options)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key.Length > 256
                || pair.Key.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
                || pair.Value is null
                || pair.Value.Length > 1_000_000
                || pair.Value.IndexOf('\0') >= 0)
            {
                throw new InvalidOperationException("A model provider option is invalid.");
            }
        }
    }

    private static void ValidateHeaders(string api, IReadOnlyDictionary<string, string?> headers)
    {
        ValidateHeaderValues(headers);
        foreach (var pair in headers)
        {
            if (ModelApiHasProtectedHeader(api, pair.Key))
            {
                throw new InvalidOperationException(
                    $"Header '{pair.Key}' is controlled by the provider transport and cannot be configured or deleted.");
            }
        }
    }

    private static void ValidateHeaderValues(IReadOnlyDictionary<string, string?> headers)
    {
        try
        {
            ProviderHeaderGuard.ValidateMerge(headers, nameof(headers));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("A model provider header is invalid.", exception);
        }
    }

    private static bool ModelApiHasProtectedHeader(string api, string name) =>
        string.Equals(api, BuiltInGameModelApis.BedrockConverseStream, StringComparison.Ordinal)
        && (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase));

    private static Uri Endpoint(Uri? configured, string defaultBaseUrl, string suffix)
    {
        var baseUri = configured ?? new Uri(defaultBaseUrl, UriKind.Absolute);
        var path = baseUri.AbsolutePath.TrimEnd('/');
        var expected = "/" + suffix.TrimStart('/');
        if (!path.EndsWith(expected, StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(baseUri)
            {
                Path = path + expected,
            };
            baseUri = builder.Uri;
        }

        return baseUri;
    }

    private bool AllowsInsecureEndpoint(Uri endpoint) =>
        _options.AllowInsecureHttp || endpoint.IsLoopback;

    private static Uri GoogleEndpoint(Uri? configured)
    {
        if (configured is null)
        {
            return new Uri(
                "https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent",
                UriKind.Absolute);
        }

        if (configured.OriginalString.Contains("{model}", StringComparison.Ordinal))
        {
            return configured;
        }

        return GoogleTemplate(configured);
    }

    private Uri VertexEndpoint(
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration)
    {
        var configured = ResolveBaseUrl(model, configuration);
        if (configured is not null)
        {
            return configured.OriginalString.Contains("{model}", StringComparison.Ordinal)
                ? configured
                : GoogleTemplate(configured);
        }

        var project = Option(configuration, BuiltInGameModelConfigurationKeys.GoogleProject)
                      ?? Option(configuration, "GOOGLE_VERTEX_PROJECT")
                      ?? Option(configuration, "GOOGLE_CLOUD_PROJECT")
                      ?? Option(configuration, "GCLOUD_PROJECT")
                      ?? throw new InvalidOperationException("A Google Cloud project is required for Vertex AI.");
        var location = Option(configuration, BuiltInGameModelConfigurationKeys.GoogleLocation)
                       ?? Option(configuration, "GOOGLE_VERTEX_LOCATION")
                       ?? Option(configuration, "GOOGLE_CLOUD_LOCATION")
                       ?? throw new InvalidOperationException("A Google Cloud location is required for Vertex AI.");
        return GoogleVertexCredentials.Endpoint(project, location);
    }

    private Uri? ResolveBaseUrl(
        GameModelDescriptor model,
        ResolvedGameModelTransportConfiguration configuration)
    {
        var value = configuration.BaseUrl ?? model.BaseUrl;
        if (value is null || !value.OriginalString.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        var expanded = value.OriginalString;
        var substitutions = 0;
        while (true)
        {
            var start = expanded.IndexOf("${", StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = expanded.IndexOf('}', start + 2);
            if (end < 0 || ++substitutions > 32)
            {
                throw new InvalidOperationException("A model provider base URL contains an invalid environment placeholder.");
            }

            var name = expanded.Substring(start + 2, end - start - 2);
            if (!IsEnvironmentVariableName(name))
            {
                throw new InvalidOperationException("A model provider base URL contains an invalid environment placeholder.");
            }

            var replacement = Option(configuration, name) ?? EnvironmentValue(name);
            if (replacement is null)
            {
                throw new InvalidOperationException(
                    $"Model provider '{model.ProviderId}' requires environment setting '{name}'.");
            }

            expanded = expanded.Substring(0, start)
                       + Uri.EscapeDataString(replacement)
                       + expanded.Substring(end + 1);
        }

        return new Uri(expanded, UriKind.Absolute);
    }

    private static Uri GoogleTemplate(Uri baseUri) =>
        new(
            baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            + "/models/{model}:streamGenerateContent"
            + baseUri.Query,
            UriKind.Absolute);

    private static OpenAIAuthenticationStyle ParseOpenAiAuthenticationStyle(
        ResolvedGameModelTransportConfiguration configuration,
        GameCredential? credential,
        IReadOnlyDictionary<string, string?> headers)
    {
        var value = Option(configuration, BuiltInGameModelConfigurationKeys.AuthenticationStyle);
        if (value is null)
        {
            return credential is null
                   || HasHeaderValue(headers, "Authorization")
                   || HasHeaderValue(headers, Option(
                       configuration,
                       BuiltInGameModelConfigurationKeys.AuthenticationHeader) ?? "api-key")
                ? OpenAIAuthenticationStyle.None
                : OpenAIAuthenticationStyle.Bearer;
        }

        return value.ToLowerInvariant() switch
        {
            "bearer" => OpenAIAuthenticationStyle.Bearer,
            "api-key" or "api-key-header" => OpenAIAuthenticationStyle.ApiKeyHeader,
            "none" => OpenAIAuthenticationStyle.None,
            _ => throw new InvalidOperationException($"Unknown OpenAI authentication style '{value}'."),
        };
    }

    private static OpenAICompatibleMaxTokensField ParseMaxTokensField(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "max_completion_tokens" => OpenAICompatibleMaxTokensField.MaxCompletionTokens,
            "max_tokens" => OpenAICompatibleMaxTokensField.MaxTokens,
            _ => throw new InvalidOperationException($"Unknown maximum-token field '{value}'."),
        };

    private static OpenAICompatibleThinkingFormat ParseThinkingFormat(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "openai" => OpenAICompatibleThinkingFormat.OpenAI,
            "openrouter" => OpenAICompatibleThinkingFormat.OpenRouter,
            "deepseek" => OpenAICompatibleThinkingFormat.DeepSeek,
            "together" => OpenAICompatibleThinkingFormat.Together,
            "baseten" => OpenAICompatibleThinkingFormat.Baseten,
            "zai" => OpenAICompatibleThinkingFormat.Zai,
            "qwen" => OpenAICompatibleThinkingFormat.Qwen,
            "chat-template" => OpenAICompatibleThinkingFormat.ChatTemplate,
            "qwen-chat-template" => OpenAICompatibleThinkingFormat.QwenChatTemplate,
            "string-thinking" => OpenAICompatibleThinkingFormat.StringThinking,
            "ant-ling" => OpenAICompatibleThinkingFormat.AntLing,
            _ => throw new InvalidOperationException($"Unknown thinking format '{value}'."),
        };

    private static OpenAICompatibleCacheControlFormat ParseCacheControlFormat(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "none" => OpenAICompatibleCacheControlFormat.None,
            "anthropic" => OpenAICompatibleCacheControlFormat.Anthropic,
            _ => throw new InvalidOperationException($"Unknown cache-control format '{value}'."),
        };

    private static OpenAICompatibleSessionAffinityFormat ParseCompatibleSessionAffinityFormat(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "openai" => OpenAICompatibleSessionAffinityFormat.OpenAI,
            "openai-nosession" => OpenAICompatibleSessionAffinityFormat.OpenAIWithoutSessionHeader,
            "openrouter" => OpenAICompatibleSessionAffinityFormat.OpenRouter,
            _ => throw new InvalidOperationException($"Unknown session-affinity format '{value}'."),
        };

    private static OpenAISessionAffinityFormat ParseOpenAiSessionAffinityFormat(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "openai" => OpenAISessionAffinityFormat.OpenAI,
            "openai-nosession" => OpenAISessionAffinityFormat.OpenAIWithoutSessionHeader,
            "openrouter" => OpenAISessionAffinityFormat.OpenRouter,
            "codex" => OpenAISessionAffinityFormat.Codex,
            _ => throw new InvalidOperationException($"Unknown session-affinity format '{value}'."),
        };

    private static OpenAICompatibleDeferredToolsMode ParseDeferredToolsMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "none" => OpenAICompatibleDeferredToolsMode.None,
            "kimi" => OpenAICompatibleDeferredToolsMode.Kimi,
            _ => throw new InvalidOperationException($"Unknown deferred-tools mode '{value}'."),
        };

    private static MistralReasoningMode ParseMistralReasoningMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "auto" => MistralReasoningMode.Auto,
            "prompt" or "prompt-mode" => MistralReasoningMode.PromptMode,
            "effort" => MistralReasoningMode.Effort,
            _ => throw new InvalidOperationException($"Unknown Mistral reasoning mode '{value}'."),
        };

    private static string? Option(ResolvedGameModelTransportConfiguration configuration, string key) =>
        configuration.Options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string RequireBoundedConfigurationValue(
        string value,
        int maximumLength,
        string label)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)
            || normalized.Length > maximumLength
            || normalized.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new InvalidOperationException(
                $"{label} must be a non-empty value of at most {maximumLength} characters without whitespace or controls.");
        }

        return normalized;
    }

    private static bool BooleanOption(
        ResolvedGameModelTransportConfiguration configuration,
        string key,
        bool defaultValue)
    {
        var value = Option(configuration, key);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException($"Model provider option '{key}' must be true or false.");
    }

    private static string? BearerValue(IReadOnlyDictionary<string, string?> headers)
    {
        if (!headers.TryGetValue("Authorization", out var value)
            || value is null
            || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var credential = value.Substring("Bearer ".Length).Trim();
        return credential.Length == 0 ? null : credential;
    }

    private static bool HasHeaderValue(IReadOnlyDictionary<string, string?> headers, string name) =>
        headers.TryGetValue(name, out var value) && value is not null;

    private static string EnvironmentPrefix(string providerId)
    {
        var result = new StringBuilder(providerId.Length);
        foreach (var character in providerId)
        {
            result.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
        }

        return result.ToString();
    }

    private static ModelStreamEvent Failure(string provider, string? api, string model, string message)
    {
        var response = new ModelResponse(
            Array.Empty<AgentContent>(),
            ModelStopReason.Error,
            errorMessage: message,
            provider: provider,
            api: api,
            responseModel: model,
            diagnostics: new[]
            {
                new ModelDiagnostic("model_runtime_error", message, ModelDiagnosticSeverity.Error),
            });
        return ModelStreamEvent.Terminal(response);
    }

    private static string ErrorMessage(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return message.Length <= 4096 ? message : message.Substring(0, 4096);
    }

    private static string RequireId(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 512
            ? throw new ArgumentException("A non-empty identifier of at most 512 characters is required.", parameterName)
            : value;

    private sealed class DirectoryDispatchProvider : IModelProvider
    {
        private readonly BuiltInGameModelRuntime _runtime;
        private readonly string _providerId;

        public DirectoryDispatchProvider(BuiltInGameModelRuntime runtime, string providerId)
        {
            _runtime = runtime;
            _providerId = providerId;
        }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            CancellationToken cancellationToken) =>
            _runtime.StreamWithBoundaryAsync(
                _providerId,
                request ?? throw new ArgumentNullException(nameof(request)),
                cancellationToken);
    }

    private sealed class EnvironmentChainAuthentication : IGameProviderAuthentication
    {
        private readonly IReadOnlyList<KeyValuePair<string, GameCredentialKind>> _variables;
        private readonly Func<string, string?> _read;
        private readonly Func<bool>? _isAmbientConfigured;

        public EnvironmentChainAuthentication(
            IReadOnlyList<KeyValuePair<string, GameCredentialKind>> variables,
            Func<string, string?> read,
            Func<bool>? isAmbientConfigured = null)
        {
            _variables = Array.AsReadOnly((variables ?? throw new ArgumentNullException(nameof(variables))).ToArray());
            if (_variables.Any(variable => string.IsNullOrWhiteSpace(variable.Key)))
            {
                throw new ArgumentException("Environment variable names must be non-empty.", nameof(variables));
            }

            if (_variables.Any(variable => !Enum.IsDefined(typeof(GameCredentialKind), variable.Value)))
            {
                throw new ArgumentOutOfRangeException(nameof(variables));
            }

            _read = read ?? throw new ArgumentNullException(nameof(read));
            _isAmbientConfigured = isAmbientConfigured;
        }

        public IReadOnlyCollection<string> Schemes { get; } = Array.Empty<string>();

        public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var credential = ReadCredential();
                var configured = credential is not null || _isAmbientConfigured?.Invoke() == true;
                return new ValueTask<GameProviderAuthStatus>(new GameProviderAuthStatus(
                    configured,
                    credential is null && configured ? "application-default" : "environment",
                    credential?.Kind,
                    error: configured ? null : "No supported environment credential is configured."));
            }
            catch (ArgumentException)
            {
                return new ValueTask<GameProviderAuthStatus>(new GameProviderAuthStatus(
                    false,
                    "environment",
                    error: "A supported environment variable contains an invalid credential."));
            }
        }

        public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var credential = ReadCredential();
            return new ValueTask<GameProviderAuthResolution?>(credential is null
                ? null
                : new GameProviderAuthResolution(credential, "environment"));
        }

        public ValueTask<GameCredential> LoginAsync(
            string scheme,
            GameAuthInteraction interaction,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Environment authentication does not expose a login flow.");

        public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Environment authentication cannot modify the process environment.");

        private GameCredential? ReadCredential()
        {
            foreach (var variable in _variables)
            {
                var secret = _read(variable.Key);
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    return new GameCredential(variable.Value, secret);
                }
            }

            return null;
        }
    }

    private sealed class ModelCompatibility
    {
        private ModelCompatibility()
        {
        }

        public bool? SupportsTemperature { get; private set; }

        public bool? Interleaved { get; private set; }

        public string? InterleavedField { get; private set; }

        public bool? SupportsStore { get; private set; }

        public bool? SupportsDeveloperRole { get; private set; }

        public bool? SupportsReasoningEffort { get; private set; }

        public bool? SupportsUsageInStreaming { get; private set; }

        public bool? SupportsFinishReason { get; private set; }

        public string? MaxTokensField { get; private set; }

        public bool? RequiresToolResultName { get; private set; }

        public bool? RequiresAssistantAfterToolResult { get; private set; }

        public bool? RequiresThinkingAsText { get; private set; }

        public bool? RequiresReasoningContentOnAssistantMessages { get; private set; }

        public string? ThinkingFormat { get; private set; }

        public bool? ZaiToolStream { get; private set; }

        public bool? SupportsThinkingTokenBudget { get; private set; }

        public bool? SupportsStrictMode { get; private set; }

        public bool? SupportsOpenAiGrammarTools { get; private set; }

        public string? CacheControlFormat { get; private set; }

        public bool? SendSessionAffinityHeaders { get; private set; }

        public string? SessionAffinityFormat { get; private set; }

        public string? DeferredToolsMode { get; private set; }

        public bool? SupportsLongCacheRetention { get; private set; }

        public string? ChatTemplateArgumentsJson { get; private set; }

        public string? ChatTemplateKeywordArgumentsJson { get; private set; }

        public bool? SupportsEagerToolInputStreaming { get; private set; }

        public bool? SupportsCacheControlOnTools { get; private set; }

        public bool? ForceAdaptiveThinking { get; private set; }

        public bool? AllowEmptySignature { get; private set; }

        public bool? SupportsStrictTools { get; private set; }

        public bool? SupportsToolReferences { get; private set; }

        public bool? SupportsAdditionalTools { get; private set; }

        public bool? SupportsToolSearch { get; private set; }

        public bool? SupportsExplicitPromptCacheMode { get; private set; }

        public bool? UseLegacyOpenApiToolSchemas { get; private set; }

        public string? ReasoningMode { get; private set; }

        public static ModelCompatibility Parse(string? json)
        {
            if (json is null)
            {
                return new ModelCompatibility();
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var compatibility = new ModelCompatibility
            {
                SupportsTemperature = OptionalBoolean(root, "supportsTemperature"),
                SupportsStore = OptionalBoolean(root, "supportsStore"),
                SupportsDeveloperRole = OptionalBoolean(root, "supportsDeveloperRole"),
                SupportsReasoningEffort = OptionalBoolean(root, "supportsReasoningEffort"),
                SupportsUsageInStreaming = OptionalBoolean(root, "supportsUsageInStreaming"),
                SupportsFinishReason = OptionalBoolean(root, "supportsFinishReason"),
                MaxTokensField = OptionalString(root, "maxTokensField"),
                RequiresToolResultName = OptionalBoolean(root, "requiresToolResultName"),
                RequiresAssistantAfterToolResult = OptionalBoolean(root, "requiresAssistantAfterToolResult"),
                RequiresThinkingAsText = OptionalBoolean(root, "requiresThinkingAsText"),
                RequiresReasoningContentOnAssistantMessages =
                    OptionalBoolean(root, "requiresReasoningContentOnAssistantMessages"),
                ThinkingFormat = OptionalString(root, "thinkingFormat"),
                ZaiToolStream = OptionalBoolean(root, "zaiToolStream"),
                SupportsThinkingTokenBudget = OptionalBoolean(root, "supportsThinkingTokenBudget"),
                SupportsStrictMode = OptionalBoolean(root, "supportsStrictMode"),
                SupportsOpenAiGrammarTools = OptionalBoolean(root, "supportsOpenAIGrammarTools"),
                CacheControlFormat = OptionalString(root, "cacheControlFormat"),
                SendSessionAffinityHeaders = OptionalBoolean(root, "sendSessionAffinityHeaders"),
                SessionAffinityFormat = OptionalString(root, "sessionAffinityFormat"),
                DeferredToolsMode = OptionalString(root, "deferredToolsMode"),
                SupportsLongCacheRetention = OptionalBoolean(root, "supportsLongCacheRetention"),
                ChatTemplateArgumentsJson = OptionalObjectJson(root, "chatTemplateArgs"),
                ChatTemplateKeywordArgumentsJson = OptionalObjectJson(root, "chatTemplateKwargs"),
                SupportsEagerToolInputStreaming = OptionalBoolean(root, "supportsEagerToolInputStreaming"),
                SupportsCacheControlOnTools = OptionalBoolean(root, "supportsCacheControlOnTools"),
                ForceAdaptiveThinking = OptionalBoolean(root, "forceAdaptiveThinking"),
                AllowEmptySignature = OptionalBoolean(root, "allowEmptySignature"),
                SupportsStrictTools = OptionalBoolean(root, "supportsStrictTools"),
                SupportsToolReferences = OptionalBoolean(root, "supportsToolReferences"),
                SupportsAdditionalTools = OptionalBoolean(root, "supportsAdditionalTools"),
                SupportsToolSearch = OptionalBoolean(root, "supportsToolSearch"),
                SupportsExplicitPromptCacheMode = OptionalBoolean(root, "supportsExplicitPromptCacheMode"),
                UseLegacyOpenApiToolSchemas = OptionalBoolean(root, "useLegacyOpenApiToolSchemas"),
                ReasoningMode = OptionalString(root, "reasoningMode"),
            };
            if (root.TryGetProperty("interleaved", out var value))
            {
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    compatibility.Interleaved = value.GetBoolean();
                }
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    if (!value.TryGetProperty("field", out var fieldElement)
                        || fieldElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(fieldElement.GetString()))
                    {
                        throw new InvalidOperationException("A model interleaved-reasoning field is invalid.");
                    }

                    compatibility.InterleavedField = fieldElement.GetString();
                    compatibility.Interleaved = true;
                }
                else
                {
                    throw new InvalidOperationException("A model interleaved-reasoning setting is invalid.");
                }
            }

            return compatibility;
        }

        private static bool? OptionalBoolean(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidOperationException($"Model compatibility field '{name}' must be true or false."),
            };
        }

        private static string? OptionalString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString())
                || value.GetString()!.Length > 128)
            {
                throw new InvalidOperationException($"Model compatibility field '{name}' must be a short string.");
            }

            return value.GetString();
        }

        private static string? OptionalObjectJson(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Model compatibility field '{name}' must be an object.");
            }

            return value.GetRawText();
        }
    }
}

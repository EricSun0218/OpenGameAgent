using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.OpenAICompatible;

namespace OpenGameAgent.Providers.Local;

public enum LocalGameModelEndpointKind
{
    OpenAICompatible,
    Ollama,
    LmStudio,
    LocalAi,
    LlamaCpp,
    Vllm,
}

public enum LocalGameEndpointHealth
{
    Available,
    Unavailable,
    InvalidResponse,
    Unauthorized,
    TimedOut,
}

public sealed class LocalGameEndpointProbeResult
{
    internal LocalGameEndpointProbeResult(
        LocalGameEndpointHealth health,
        IReadOnlyList<GameModelDescriptor> models,
        TimeSpan elapsed,
        string? errorCategory = null,
        HttpStatusCode? statusCode = null)
    {
        Health = health;
        Models = models;
        Elapsed = elapsed;
        ErrorCategory = errorCategory;
        StatusCode = statusCode;
    }

    public LocalGameEndpointHealth Health { get; }

    public IReadOnlyList<GameModelDescriptor> Models { get; }

    public TimeSpan Elapsed { get; }

    public string? ErrorCategory { get; }

    public HttpStatusCode? StatusCode { get; }
}

public sealed class LocalGameModelEndpointOptions
{
    public LocalGameModelEndpointOptions(HttpClient httpClient, Uri endpoint)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; set; }

    public LocalGameModelEndpointKind Kind { get; set; }

    public string ProviderId { get; set; } = "local";

    public string DisplayName { get; set; } = "Local models";

    public IGameProviderAuthentication Authentication { get; set; } =
        new StaticGameProviderAuthentication();

    public GameModelInputCapabilities DefaultInputCapabilities { get; set; } =
        GameModelInputCapabilities.Text | GameModelInputCapabilities.StructuredData;

    public GameModelOutputCapabilities DefaultOutputCapabilities { get; set; } =
        GameModelOutputCapabilities.Text;

    public IReadOnlyDictionary<string, GameModelInputCapabilities> InputCapabilityOverrides { get; set; } =
        new ReadOnlyDictionary<string, GameModelInputCapabilities>(
            new Dictionary<string, GameModelInputCapabilities>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, GameModelOutputCapabilities> OutputCapabilityOverrides { get; set; } =
        new ReadOnlyDictionary<string, GameModelOutputCapabilities>(
            new Dictionary<string, GameModelOutputCapabilities>(StringComparer.Ordinal));

    public bool AllowRemoteEndpoint { get; set; }

    public bool AllowInsecureRemoteHttp { get; set; }

    public int RequestTimeoutMilliseconds { get; set; } = 5_000;

    public int MaximumResponseBytes { get; set; } = 4_000_000;

    public int MaximumModels { get; set; } = 4_096;

    public Action<OpenAICompatibleProtocolOptions>? ConfigureProtocol { get; set; }
}

public static class LocalGameModelPresets
{
    public static LocalGameModelEndpointOptions Ollama(HttpClient httpClient, Uri? endpoint = null) => new(
        httpClient,
        endpoint ?? new Uri("http://127.0.0.1:11434"))
    {
        Kind = LocalGameModelEndpointKind.Ollama,
        ProviderId = "ollama",
        DisplayName = "Ollama",
    };

    public static LocalGameModelEndpointOptions LmStudio(HttpClient httpClient, Uri? endpoint = null) => new(
        httpClient,
        endpoint ?? new Uri("http://127.0.0.1:1234/v1"))
    {
        Kind = LocalGameModelEndpointKind.LmStudio,
        ProviderId = "lm-studio",
        DisplayName = "LM Studio",
    };

    public static LocalGameModelEndpointOptions LocalAi(HttpClient httpClient, Uri? endpoint = null) => new(
        httpClient,
        endpoint ?? new Uri("http://127.0.0.1:8080/v1"))
    {
        Kind = LocalGameModelEndpointKind.LocalAi,
        ProviderId = "localai",
        DisplayName = "LocalAI",
    };

    public static LocalGameModelEndpointOptions LlamaCpp(HttpClient httpClient, Uri? endpoint = null) => new(
        httpClient,
        endpoint ?? new Uri("http://127.0.0.1:8080/v1"))
    {
        Kind = LocalGameModelEndpointKind.LlamaCpp,
        ProviderId = "llama-cpp",
        DisplayName = "llama.cpp",
    };

    public static LocalGameModelEndpointOptions Vllm(HttpClient httpClient, Uri? endpoint = null) => new(
        httpClient,
        endpoint ?? new Uri("http://127.0.0.1:8000/v1"))
    {
        Kind = LocalGameModelEndpointKind.Vllm,
        ProviderId = "vllm",
        DisplayName = "vLLM",
    };
}

public sealed class LocalGameModelEndpoint
{
    private readonly Settings _settings;

    public LocalGameModelEndpoint(LocalGameModelEndpointOptions options)
    {
        _settings = Settings.Create(options);
    }

    public GameProviderDescriptor Descriptor => _settings.Descriptor;

    public GameModelProviderRegistration CreateRegistration()
    {
        var provider = CreateProvider(authentication: null);
        return new GameModelProviderRegistration(
            _settings.Descriptor,
            provider,
            _settings.Authentication,
            refreshModels: async (context, cancellationToken) =>
            {
                if (!context.AllowNetwork)
                {
                    return context.CurrentModels;
                }

                var result = await ProbeAsync(context.Authentication, cancellationToken).ConfigureAwait(false);
                if (result.Health != LocalGameEndpointHealth.Available)
                {
                    throw new InvalidOperationException(
                        $"Local model discovery failed with category '{result.ErrorCategory ?? result.Health.ToString()}'.");
                }

                return result.Models;
            },
            stream: (request, authentication, cancellationToken) =>
                CreateProvider(authentication).StreamAsync(request, cancellationToken));
    }

    public ValueTask<LocalGameEndpointProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
        ProbeAsync(authentication: null, cancellationToken);

    private async ValueTask<LocalGameEndpointProbeResult> ProbeAsync(
        GameProviderAuthResolution? authentication,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.RequestTimeoutMilliseconds);
        try
        {
            var discoveryEndpoint = DiscoveryEndpoint(EffectiveEndpoint(authentication), _settings.Kind);
            using var request = new HttpRequestMessage(HttpMethod.Get, discoveryEndpoint);
            ApplyAuthentication(request, authentication);
            using var response = await _settings.HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalEndpoint
                && !SameOrigin(discoveryEndpoint, finalEndpoint))
            {
                return Result(LocalGameEndpointHealth.InvalidResponse, "cross-origin-redirect", response.StatusCode);
            }

            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                return Result(LocalGameEndpointHealth.InvalidResponse, "redirect", response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Result(LocalGameEndpointHealth.Unauthorized, "unauthorized", response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Result(LocalGameEndpointHealth.Unavailable, "http", response.StatusCode);
            }

            var bytes = await ReadBoundedAsync(response, _settings.MaximumResponseBytes, timeout.Token)
                .ConfigureAwait(false);
            var models = ParseModels(bytes);
            return new LocalGameEndpointProbeResult(
                LocalGameEndpointHealth.Available,
                models,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Result(LocalGameEndpointHealth.TimedOut, "timeout");
        }
        catch (HttpRequestException)
        {
            return Result(LocalGameEndpointHealth.Unavailable, "transport");
        }
        catch (InvalidDataException)
        {
            return Result(LocalGameEndpointHealth.InvalidResponse, "invalid-response");
        }
        catch (JsonException)
        {
            return Result(LocalGameEndpointHealth.InvalidResponse, "invalid-json");
        }

        LocalGameEndpointProbeResult Result(
            LocalGameEndpointHealth health,
            string category,
            HttpStatusCode? statusCode = null) => new(
                health,
                Array.Empty<GameModelDescriptor>(),
                stopwatch.Elapsed,
                category,
                statusCode);
    }

    private OpenAICompatibleProvider CreateProvider(GameProviderAuthResolution? authentication)
    {
        var endpoint = ChatEndpoint(EffectiveEndpoint(authentication), _settings.Kind);
        var options = new OpenAICompatibleProviderOptions(_settings.HttpClient, endpoint)
        {
            ProviderId = _settings.Descriptor.ProviderId,
            ApiId = "openai-completions",
            ApiKey = authentication?.Credential?.Secret,
            AllowInsecureHttp = _settings.AllowInsecureRemoteHttp,
            AllowDoneWithoutFinishReason = true,
        };
        options.Protocol.SupportsReasoningEffort = false;
        options.Protocol.SupportsStrictMode = false;
        options.Protocol.SupportsLongCacheRetention = false;
        options.Protocol.SupportsUsageInStreaming = _settings.Kind != LocalGameModelEndpointKind.Ollama;
        foreach (var pair in authentication?.Headers ?? new Dictionary<string, string?>())
        {
            options.Headers[pair.Key] = pair.Value;
        }

        _settings.ConfigureProtocol?.Invoke(options.Protocol);
        return new OpenAICompatibleProvider(options);
    }

    private IReadOnlyList<GameModelDescriptor> ParseModels(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var items = _settings.Kind == LocalGameModelEndpointKind.Ollama
            ? RequiredArray(root, "models")
            : RequiredArray(root, "data");
        var models = new List<GameModelDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (models.Count >= _settings.MaximumModels)
            {
                throw new InvalidDataException("The local model catalog exceeded its configured limit.");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("A local model catalog item must be an object.");
            }

            var id = OptionalString(item, "id")
                ?? OptionalString(item, "model")
                ?? OptionalString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || !seen.Add(id))
            {
                continue;
            }

            var input = _settings.InputOverrides.TryGetValue(id, out var inputOverride)
                ? inputOverride
                : _settings.DefaultInputCapabilities;
            var output = _settings.OutputOverrides.TryGetValue(id, out var outputOverride)
                ? outputOverride
                : _settings.DefaultOutputCapabilities;
            ApplyAdvertisedCapabilities(item, ref input, ref output);
            if (_settings.Kind == LocalGameModelEndpointKind.LocalAi)
            {
                if (!HasCapability(item, "chat"))
                {
                    continue;
                }
            }

            var levels = output.HasFlag(GameModelOutputCapabilities.Reasoning)
                ? new[] { GameReasoningLevel.Off, GameReasoningLevel.Low, GameReasoningLevel.Medium, GameReasoningLevel.High }
                : new[] { GameReasoningLevel.Off };
            models.Add(new GameModelDescriptor(
                _settings.Descriptor.ProviderId,
                id,
                inputCapabilities: input,
                outputCapabilities: output,
                reasoningLevels: levels,
                cost: new GameModelCost(isKnown: false),
                api: "openai-completions",
                baseUrl: ChatEndpoint(_settings.Endpoint, _settings.Kind)));
        }

        return Array.AsReadOnly(models.ToArray());
    }

    private static void ApplyAdvertisedCapabilities(
        JsonElement item,
        ref GameModelInputCapabilities input,
        ref GameModelOutputCapabilities output)
    {
        if (HasCapability(item, "vision") || HasModality(item, "input_modalities", "image"))
        {
            input |= GameModelInputCapabilities.Image;
        }

        if (HasModality(item, "input_modalities", "audio"))
        {
            input |= GameModelInputCapabilities.Audio;
        }

        if (HasCapability(item, "tools") || HasCapability(item, "tool_calling"))
        {
            output |= GameModelOutputCapabilities.ToolCalls;
        }

        if (HasCapability(item, "thinking"))
        {
            output |= GameModelOutputCapabilities.Reasoning;
        }
    }

    private static bool HasCapability(JsonElement item, string value) =>
        HasModality(item, "capabilities", value);

    private static bool HasModality(JsonElement item, string propertyName, string value)
    {
        if (!item.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return array.EnumerateArray().Any(element =>
            element.ValueKind == JsonValueKind.String
            && string.Equals(element.GetString(), value, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyAuthentication(HttpRequestMessage request, GameProviderAuthResolution? authentication)
    {
        if (authentication?.Credential is { } credential)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
        }

        foreach (var pair in authentication?.Headers ?? new Dictionary<string, string?>())
        {
            if (pair.Value is not null && !request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                throw new InvalidOperationException("A local provider authentication header was invalid.");
            }
        }
    }

    private Uri EffectiveEndpoint(GameProviderAuthResolution? authentication)
    {
        var endpoint = authentication?.BaseUrl ?? _settings.Endpoint;
        ValidateEndpoint(
            endpoint,
            _settings.AllowRemoteEndpoint,
            _settings.AllowInsecureRemoteHttp,
            nameof(authentication));
        return endpoint;
    }

    private static JsonElement RequiredArray(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidDataException("The local model catalog did not contain its model array.");

    private static string? OptionalString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
        {
            throw new InvalidDataException("The local model catalog response exceeded its configured limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + count > maximumBytes)
            {
                throw new InvalidDataException("The local model catalog response exceeded its configured limit.");
            }

            await buffer.WriteAsync(chunk, 0, count, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Uri ChatEndpoint(Uri endpoint, LocalGameModelEndpointKind kind) =>
        Append(endpoint, kind == LocalGameModelEndpointKind.Ollama ? "v1/chat/completions" : "chat/completions");

    private static Uri DiscoveryEndpoint(Uri endpoint, LocalGameModelEndpointKind kind) =>
        Append(endpoint, kind switch
        {
            LocalGameModelEndpointKind.Ollama => "api/tags",
            LocalGameModelEndpointKind.LocalAi => "models/capabilities",
            _ => "models",
        });

    private static Uri Append(Uri endpoint, string suffix)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (suffix.StartsWith("v1/", StringComparison.Ordinal) && path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            suffix = suffix.Substring(3);
        }

        if (suffix.StartsWith("api/", StringComparison.Ordinal) && path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Substring(0, path.Length - 3).TrimEnd('/');
        }

        return new UriBuilder(endpoint) { Path = path + "/" + suffix }.Uri;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static void ValidateEndpoint(
        Uri endpoint,
        bool allowRemoteEndpoint,
        bool allowInsecureRemoteHttp,
        string parameterName)
    {
        if (!endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length > 0
            || endpoint.Fragment.Length > 0
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "A local model endpoint must be an absolute HTTP URL without credentials or a fragment.",
                parameterName);
        }

        if (!endpoint.IsLoopback && !allowRemoteEndpoint)
        {
            throw new ArgumentException(
                "Local model endpoints are loopback-only unless remote access is explicitly enabled.",
                parameterName);
        }

        if (!endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp && !allowInsecureRemoteHttp)
        {
            throw new ArgumentException(
                "A remote local-model endpoint must use HTTPS unless insecure transport is explicitly enabled.",
                parameterName);
        }
    }

    private sealed class Settings
    {
        private Settings(
            HttpClient httpClient,
            Uri endpoint,
            LocalGameModelEndpointKind kind,
            GameProviderDescriptor descriptor,
            IGameProviderAuthentication authentication,
            GameModelInputCapabilities defaultInputCapabilities,
            GameModelOutputCapabilities defaultOutputCapabilities,
            IReadOnlyDictionary<string, GameModelInputCapabilities> inputOverrides,
            IReadOnlyDictionary<string, GameModelOutputCapabilities> outputOverrides,
            bool allowRemoteEndpoint,
            bool allowInsecureRemoteHttp,
            int requestTimeoutMilliseconds,
            int maximumResponseBytes,
            int maximumModels,
            Action<OpenAICompatibleProtocolOptions>? configureProtocol)
        {
            HttpClient = httpClient;
            Endpoint = endpoint;
            Kind = kind;
            Descriptor = descriptor;
            Authentication = authentication;
            DefaultInputCapabilities = defaultInputCapabilities;
            DefaultOutputCapabilities = defaultOutputCapabilities;
            InputOverrides = inputOverrides;
            OutputOverrides = outputOverrides;
            AllowRemoteEndpoint = allowRemoteEndpoint;
            AllowInsecureRemoteHttp = allowInsecureRemoteHttp;
            RequestTimeoutMilliseconds = requestTimeoutMilliseconds;
            MaximumResponseBytes = maximumResponseBytes;
            MaximumModels = maximumModels;
            ConfigureProtocol = configureProtocol;
        }

        public HttpClient HttpClient { get; }
        public Uri Endpoint { get; }
        public LocalGameModelEndpointKind Kind { get; }
        public GameProviderDescriptor Descriptor { get; }
        public IGameProviderAuthentication Authentication { get; }
        public GameModelInputCapabilities DefaultInputCapabilities { get; }
        public GameModelOutputCapabilities DefaultOutputCapabilities { get; }
        public IReadOnlyDictionary<string, GameModelInputCapabilities> InputOverrides { get; }
        public IReadOnlyDictionary<string, GameModelOutputCapabilities> OutputOverrides { get; }
        public bool AllowRemoteEndpoint { get; }
        public bool AllowInsecureRemoteHttp { get; }
        public int RequestTimeoutMilliseconds { get; }
        public int MaximumResponseBytes { get; }
        public int MaximumModels { get; }
        public Action<OpenAICompatibleProtocolOptions>? ConfigureProtocol { get; }

        public static Settings Create(LocalGameModelEndpointOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (!Enum.IsDefined(typeof(LocalGameModelEndpointKind), options.Kind))
            {
                throw new ArgumentOutOfRangeException(nameof(options.Kind));
            }

            var endpoint = options.Endpoint;
            ValidateEndpoint(
                endpoint,
                options.AllowRemoteEndpoint,
                options.AllowInsecureRemoteHttp,
                nameof(options.Endpoint));

            if (options.RequestTimeoutMilliseconds is < 100 or > 120_000
                || options.MaximumResponseBytes is < 2 or > 100_000_000
                || options.MaximumModels is < 1 or > 100_000)
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }

            ValidateFlags(options.DefaultInputCapabilities, nameof(options.DefaultInputCapabilities));
            ValidateFlags(options.DefaultOutputCapabilities, nameof(options.DefaultOutputCapabilities));
            return new Settings(
                options.HttpClient,
                endpoint,
                options.Kind,
                new GameProviderDescriptor(
                    options.ProviderId,
                    options.DisplayName,
                    endpoint,
                    isLocal: endpoint.IsLoopback,
                    supportsDynamicModels: true,
                    metadata: new Dictionary<string, string>
                    {
                        ["endpointKind"] = options.Kind.ToString(),
                    }),
                options.Authentication ?? throw new ArgumentNullException(nameof(options.Authentication)),
                options.DefaultInputCapabilities,
                options.DefaultOutputCapabilities,
                Copy(options.InputCapabilityOverrides),
                Copy(options.OutputCapabilityOverrides),
                options.AllowRemoteEndpoint,
                options.AllowInsecureRemoteHttp,
                options.RequestTimeoutMilliseconds,
                options.MaximumResponseBytes,
                options.MaximumModels,
                options.ConfigureProtocol);
        }

        private static IReadOnlyDictionary<string, T> Copy<T>(IReadOnlyDictionary<string, T> source)
            where T : struct, Enum
        {
            if (source is null || source.Count > 100_000)
            {
                throw new ArgumentException("A local capability override map is invalid.");
            }

            var copy = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 512 || !copy.TryAdd(pair.Key, pair.Value))
                {
                    throw new ArgumentException("A local capability override is invalid.");
                }

                ValidateFlags(pair.Value, nameof(source));
            }

            return new ReadOnlyDictionary<string, T>(copy);
        }

        private static void ValidateFlags<T>(T value, string parameterName)
            where T : struct, Enum
        {
            var numeric = Convert.ToUInt64(value);
            var allowed = Enum.GetValues(typeof(T))
                .Cast<T>()
                .Aggregate(0UL, (current, item) => current | Convert.ToUInt64(item));
            if ((numeric & ~allowed) != 0)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}

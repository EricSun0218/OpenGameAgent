using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Memory;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.Images.Internal;

namespace OpenGameAgent.Providers.Local;

public sealed class LocalOpenAIEmbeddingProviderOptions
{
    public LocalOpenAIEmbeddingProviderOptions(
        HttpClient httpClient,
        Uri endpoint,
        string providerId,
        string modelId,
        string modelVersion,
        int dimensions)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Identity = new MemoryEmbeddingIdentity(providerId, modelId, modelVersion, dimensions);
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; set; }

    public MemoryEmbeddingIdentity Identity { get; }

    public IGameProviderAuthentication Authentication { get; set; } =
        new StaticGameProviderAuthentication();

    public string QueryPrefix { get; set; } = string.Empty;

    public string DocumentPrefix { get; set; } = string.Empty;

    public bool AllowRemoteEndpoint { get; set; }

    public bool AllowInsecureRemoteHttp { get; set; }

    public int MaximumTextsPerRequest { get; set; } = 256;

    public int MaximumCharactersPerText { get; set; } = 100_000;

    public int MaximumRequestBytes { get; set; } = 8_000_000;

    public int MaximumResponseBytes { get; set; } = 64_000_000;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Supplies memory vectors through a bounded OpenAI-compatible embeddings endpoint.
/// The host declares the stable model identity so vector indexes can detect rebuilds.
/// </summary>
public sealed class LocalOpenAIEmbeddingProvider : IMemoryEmbeddingProvider
{
    private readonly Settings _settings;

    public LocalOpenAIEmbeddingProvider(LocalOpenAIEmbeddingProviderOptions options)
    {
        _settings = Settings.Create(options);
    }

    public MemoryEmbeddingIdentity Identity => _settings.Identity;

    public async ValueTask<ReadOnlyMemory<float>> EmbedQueryAsync(
        string text,
        CancellationToken cancellationToken)
    {
        var result = await EmbedAsync(
            new[] { Prefix(_settings.QueryPrefix, text) },
            cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (texts is null)
        {
            throw new ArgumentNullException(nameof(texts));
        }

        if (texts.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<ReadOnlyMemory<float>>>(
                Array.Empty<ReadOnlyMemory<float>>());
        }

        if (texts.Count > _settings.MaximumTextsPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(texts), "The embedding batch exceeded its configured limit.");
        }

        var projected = texts.Select(text => Prefix(_settings.DocumentPrefix, text)).ToArray();
        return EmbedAsync(projected, cancellationToken);
    }

    public ValueTask DisposeAsync() => default;

    private async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        foreach (var text in texts)
        {
            ValidateText(text);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = _settings.Identity.ModelId,
            input = texts,
            encoding_format = "float",
        });
        if (payload.Length > _settings.MaximumRequestBytes)
        {
            throw new InvalidDataException("The local embedding request exceeded its configured byte limit.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.Timeout);
        var authentication = await _settings.Authentication.ResolveAsync(timeout.Token).ConfigureAwait(false);
        var endpoint = EffectiveEndpoint(authentication);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (authentication?.Credential is { } credential)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
        }

        foreach (var pair in authentication?.Headers ?? new Dictionary<string, string?>())
        {
            if (pair.Value is not null && !request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                throw new InvalidOperationException("A local embedding authentication header was invalid.");
            }
        }

        using var response = await _settings.HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } finalEndpoint && !SameOrigin(endpoint, finalEndpoint))
        {
            throw new InvalidDataException("The local embedding service redirected across origins.");
        }

        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            throw new InvalidDataException("The local embedding service refused a redirect response.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "The local embedding service returned HTTP "
                + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                + ".");
        }

        using var document = await ImageProviderCommon.ReadJsonAsync(
            response,
            _settings.MaximumResponseBytes,
            timeout.Token).ConfigureAwait(false);
        return Parse(document.RootElement, texts.Count);
    }

    private IReadOnlyList<ReadOnlyMemory<float>> Parse(JsonElement root, int expectedCount)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() != expectedCount)
        {
            throw new InvalidDataException("The local embedding response had an invalid envelope.");
        }

        var ordered = new ReadOnlyMemory<float>?[expectedCount];
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("index", out var indexElement)
                || !indexElement.TryGetInt32(out var index)
                || index < 0
                || index >= expectedCount
                || ordered[index] is not null
                || !item.TryGetProperty("embedding", out var embedding)
                || embedding.ValueKind != JsonValueKind.Array
                || embedding.GetArrayLength() != Identity.Dimensions)
            {
                throw new InvalidDataException("The local embedding response contained an invalid item.");
            }

            var vector = new float[Identity.Dimensions];
            var offset = 0;
            foreach (var value in embedding.EnumerateArray())
            {
                if (!value.TryGetDouble(out var number)
                    || double.IsNaN(number)
                    || double.IsInfinity(number)
                    || number < -float.MaxValue
                    || number > float.MaxValue)
                {
                    throw new InvalidDataException("The local embedding response contained an invalid vector value.");
                }

                vector[offset++] = (float)number;
            }

            ordered[index] = vector;
        }

        if (ordered.Any(value => value is null))
        {
            throw new InvalidDataException("The local embedding response omitted a vector.");
        }

        return new ReadOnlyCollection<ReadOnlyMemory<float>>(
            ordered.Select(value => value!.Value).ToArray());
    }

    private void ValidateText(string text)
    {
        if (text is null
            || text.Length > _settings.MaximumCharactersPerText
            || Encoding.UTF8.GetByteCount(text) > _settings.MaximumRequestBytes)
        {
            throw new InvalidDataException("Local embedding input exceeded its configured limit.");
        }
    }

    private Uri EffectiveEndpoint(GameProviderAuthResolution? authentication)
    {
        var baseUrl = authentication?.BaseUrl ?? _settings.Endpoint;
        ValidateEndpoint(baseUrl, _settings.AllowRemoteEndpoint, _settings.AllowInsecureRemoteHttp);
        var path = baseUrl.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            path = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? path + "/embeddings"
                : path + "/v1/embeddings";
        }

        return new UriBuilder(baseUrl) { Path = path }.Uri;
    }

    private string Prefix(string prefix, string? text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return prefix.Length == 0 ? text : prefix + text;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static void ValidateEndpoint(Uri endpoint, bool allowRemote, bool allowInsecureRemoteHttp)
    {
        if (!endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length > 0
            || endpoint.Fragment.Length > 0
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A valid local embedding HTTP endpoint is required.", nameof(endpoint));
        }

        if (!endpoint.IsLoopback && !allowRemote)
        {
            throw new ArgumentException("Local embedding endpoints are loopback-only unless remote access is enabled.", nameof(endpoint));
        }

        if (!endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp && !allowInsecureRemoteHttp)
        {
            throw new ArgumentException("Remote embedding endpoints must use HTTPS.", nameof(endpoint));
        }
    }

    private sealed class Settings
    {
        private Settings(LocalOpenAIEmbeddingProviderOptions options)
        {
            HttpClient = options.HttpClient;
            Endpoint = options.Endpoint;
            Identity = options.Identity;
            Authentication = options.Authentication;
            QueryPrefix = options.QueryPrefix;
            DocumentPrefix = options.DocumentPrefix;
            AllowRemoteEndpoint = options.AllowRemoteEndpoint;
            AllowInsecureRemoteHttp = options.AllowInsecureRemoteHttp;
            MaximumTextsPerRequest = options.MaximumTextsPerRequest;
            MaximumCharactersPerText = options.MaximumCharactersPerText;
            MaximumRequestBytes = options.MaximumRequestBytes;
            MaximumResponseBytes = options.MaximumResponseBytes;
            Timeout = options.Timeout;
        }

        public HttpClient HttpClient { get; }
        public Uri Endpoint { get; }
        public MemoryEmbeddingIdentity Identity { get; }
        public IGameProviderAuthentication Authentication { get; }
        public string QueryPrefix { get; }
        public string DocumentPrefix { get; }
        public bool AllowRemoteEndpoint { get; }
        public bool AllowInsecureRemoteHttp { get; }
        public int MaximumTextsPerRequest { get; }
        public int MaximumCharactersPerText { get; }
        public int MaximumRequestBytes { get; }
        public int MaximumResponseBytes { get; }
        public TimeSpan Timeout { get; }

        public static Settings Create(LocalOpenAIEmbeddingProviderOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            ValidateEndpoint(options.Endpoint, options.AllowRemoteEndpoint, options.AllowInsecureRemoteHttp);
            if (options.Authentication is null
                || options.QueryPrefix is null
                || options.DocumentPrefix is null
                || options.QueryPrefix.Length > 10_000
                || options.DocumentPrefix.Length > 10_000
                || options.MaximumTextsPerRequest is < 1 or > 4_096
                || options.MaximumCharactersPerText is < 1 or > 1_000_000
                || options.MaximumRequestBytes is < 2 or > 100_000_000
                || options.MaximumResponseBytes is < 2 or > 200_000_000
                || options.Timeout < TimeSpan.FromMilliseconds(100)
                || options.Timeout > TimeSpan.FromMinutes(30))
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }

            return new Settings(options);
        }
    }
}

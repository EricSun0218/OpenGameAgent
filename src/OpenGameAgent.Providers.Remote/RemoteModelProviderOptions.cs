using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;

namespace OpenGameAgent.Providers.Remote;

public sealed class RemoteModelProviderOptions
{
    public const int DefaultMaximumRequestBytes = 8_000_000;
    public const int DefaultMaximumResponseBytes = 32_000_000;
    public const int DefaultMaximumEventBytes = 8_000_000;
    public const int DefaultMaximumEvents = 100_000;
    public const int DefaultMaximumJsonDepth = 128;

    public RemoteModelProviderOptions(HttpClient httpClient, Uri endpoint)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; }

    public bool AllowInsecureHttp { get; set; }

    public string? ApiKey { get; set; }

    public string ApiKeyHeader { get; set; } = "Authorization";

    public string ApiKeyScheme { get; set; } = "Bearer";

    public IReadOnlyDictionary<string, string> Headers { get; set; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public int MaximumRequestBytes { get; set; } = DefaultMaximumRequestBytes;

    public int MaximumResponseBytes { get; set; } = DefaultMaximumResponseBytes;

    public int MaximumEventBytes { get; set; } = DefaultMaximumEventBytes;

    public int MaximumEvents { get; set; } = DefaultMaximumEvents;

    public int MaximumJsonDepth { get; set; } = DefaultMaximumJsonDepth;

    internal RemoteModelProviderSettings Validate()
    {
        if (!Endpoint.IsAbsoluteUri
            || Endpoint.UserInfo.Length > 0
            || Endpoint.Fragment.Length > 0
            || (!string.Equals(Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The remote provider endpoint must be an absolute HTTP or HTTPS URI.", nameof(Endpoint));
        }

        if (string.Equals(Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !Endpoint.IsLoopback
            && !AllowInsecureHttp)
        {
            throw new ArgumentException(
                "Remote provider endpoints must use HTTPS unless insecure HTTP is explicitly enabled.",
                nameof(Endpoint));
        }

        ValidateLimit(MaximumRequestBytes, nameof(MaximumRequestBytes));
        ValidateLimit(MaximumResponseBytes, nameof(MaximumResponseBytes));
        ValidateLimit(MaximumEventBytes, nameof(MaximumEventBytes));
        if (MaximumEvents < 2 || MaximumEvents > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents));
        }

        if (MaximumJsonDepth < 1 || MaximumJsonDepth > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumJsonDepth));
        }

        ValidateCredential(ApiKey, nameof(ApiKey), 65_536);
        ValidateCredential(ApiKeyHeader, nameof(ApiKeyHeader), 256);
        ValidateCredential(ApiKeyScheme, nameof(ApiKeyScheme), 256, allowEmpty: true);
        if (!IsValidHeaderName(ApiKeyHeader))
        {
            throw new ArgumentException("A valid API key header name is required.", nameof(ApiKeyHeader));
        }

        var headers = Headers is null
            ? throw new ArgumentNullException(nameof(Headers))
            : new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase);
        if (headers.Count != Headers.Count
            || headers.Any(pair => !IsValidHeaderName(pair.Key)
                                   || !IsValidHeaderValue(pair.Value)
                                   || string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(pair.Key, "Accept", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(pair.Key, ApiKeyHeader, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Remote provider headers contain an invalid, duplicate, or reserved entry.", nameof(Headers));
        }

        return new RemoteModelProviderSettings(
            HttpClient,
            Endpoint,
            ApiKey,
            ApiKeyHeader,
            ApiKeyScheme,
            new ReadOnlyDictionary<string, string>(headers),
            MaximumRequestBytes,
            MaximumResponseBytes,
            MaximumEventBytes,
            MaximumEvents,
            MaximumJsonDepth);
    }

    private static void ValidateLimit(int value, string name)
    {
        if (value < 2 || value > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    internal static bool IsValidHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage();
            return request.Headers.TryAddWithoutValidation(name, "value");
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static bool IsValidHeaderValue(string? value) =>
        value is not null
        && value.Length <= 65_536
        && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;

    internal static void ValidateCredential(string? value, string name, int maximumLength, bool allowEmpty = false)
    {
        if (value is null)
        {
            return;
        }

        if ((!allowEmpty && string.IsNullOrWhiteSpace(value))
            || value.Length > maximumLength
            || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException("A remote provider credential or header value is invalid.", name);
        }
    }
}

internal sealed class RemoteModelProviderSettings
{
    public RemoteModelProviderSettings(
        HttpClient httpClient,
        Uri endpoint,
        string? apiKey,
        string apiKeyHeader,
        string apiKeyScheme,
        IReadOnlyDictionary<string, string> headers,
        int maximumRequestBytes,
        int maximumResponseBytes,
        int maximumEventBytes,
        int maximumEvents,
        int maximumJsonDepth)
    {
        HttpClient = httpClient;
        Endpoint = endpoint;
        ApiKey = apiKey;
        ApiKeyHeader = apiKeyHeader;
        ApiKeyScheme = apiKeyScheme;
        Headers = headers;
        MaximumRequestBytes = maximumRequestBytes;
        MaximumResponseBytes = maximumResponseBytes;
        MaximumEventBytes = maximumEventBytes;
        MaximumEvents = maximumEvents;
        MaximumJsonDepth = maximumJsonDepth;
    }

    public HttpClient HttpClient { get; }
    public Uri Endpoint { get; }
    public string? ApiKey { get; }
    public string ApiKeyHeader { get; }
    public string ApiKeyScheme { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public int MaximumRequestBytes { get; }
    public int MaximumResponseBytes { get; }
    public int MaximumEventBytes { get; }
    public int MaximumEvents { get; }
    public int MaximumJsonDepth { get; }
}

public sealed class ModelProviderProxyServerOptions
{
    public string? ApiKey { get; set; }

    public string ApiKeyHeader { get; set; } = "Authorization";

    public string ApiKeyScheme { get; set; } = "Bearer";

    public int MaximumRequestBytes { get; set; } = RemoteModelProviderOptions.DefaultMaximumRequestBytes;

    public int MaximumResponseBytes { get; set; } = RemoteModelProviderOptions.DefaultMaximumResponseBytes;

    public int MaximumEventBytes { get; set; } = RemoteModelProviderOptions.DefaultMaximumEventBytes;

    public int MaximumEvents { get; set; } = RemoteModelProviderOptions.DefaultMaximumEvents;

    public int MaximumJsonDepth { get; set; } = RemoteModelProviderOptions.DefaultMaximumJsonDepth;
}

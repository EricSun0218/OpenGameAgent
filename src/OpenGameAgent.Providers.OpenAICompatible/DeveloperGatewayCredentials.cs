using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Providers.OpenAICompatible;

/// <summary>
/// A short-lived credential issued by a game developer's model gateway. This is not the
/// upstream model-provider key and may be scoped, rate-limited, revoked, and rotated by the gateway.
/// </summary>
public sealed class DeveloperGatewayCredential
{
    public DeveloperGatewayCredential(
        string accessToken,
        DateTimeOffset expiresAt,
        string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken)
            || accessToken.Length > 65_536
            || accessToken.Contains('\r')
            || accessToken.Contains('\n')
            || accessToken.Contains('\0'))
        {
            throw new ArgumentException("A non-empty single-line access token is required.", nameof(accessToken));
        }

        if (scope?.Length > 4_096)
        {
            throw new ArgumentException("A credential scope cannot exceed 4096 characters.", nameof(scope));
        }

        AccessToken = accessToken;
        ExpiresAt = expiresAt;
        Scope = scope;
    }

    public string AccessToken { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string? Scope { get; }
}

/// <summary>
/// Implemented by the game's account/login layer. It exchanges the player's authenticated game
/// session for a short-lived model-gateway credential without exposing the upstream provider key.
/// </summary>
public interface IDeveloperGatewayCredentialSource
{
    ValueTask<DeveloperGatewayCredential> GetCredentialAsync(
        bool forceRefresh,
        CancellationToken cancellationToken);
}

public delegate ValueTask<IReadOnlyDictionary<string, string>> DeveloperGatewayHeaderProvider(
    bool forceRefresh,
    CancellationToken cancellationToken);

/// <summary>
/// Exchanges the game's existing authenticated player session for a short-lived gateway token.
/// The endpoint is developer-controlled and never returns the upstream model-provider key.
/// </summary>
public sealed class HttpDeveloperGatewayCredentialSource : IDeveloperGatewayCredentialSource
{
    private static readonly char[] InvalidHeaderNameCharacters = { '\r', '\n', '\0' };
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly DeveloperGatewayHeaderProvider _headers;
    private readonly int _maximumResponseBytes;

    public HttpDeveloperGatewayCredentialSource(
        HttpClient client,
        Uri endpoint,
        DeveloperGatewayHeaderProvider headers,
        int maximumResponseBytes = 65_536,
        bool allowInsecureHttp = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _headers = headers ?? throw new ArgumentNullException(nameof(headers));
        if (!_endpoint.IsAbsoluteUri
            || _endpoint.UserInfo.Length > 0
            || (_endpoint.Scheme != Uri.UriSchemeHttps
                && !(allowInsecureHttp && _endpoint.Scheme == Uri.UriSchemeHttp)))
        {
            throw new ArgumentException(
                "An absolute HTTPS endpoint without embedded credentials is required unless insecure HTTP is explicitly enabled.",
                nameof(endpoint));
        }

        if (maximumResponseBytes < 256 || maximumResponseBytes > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        _maximumResponseBytes = maximumResponseBytes;
    }

    public async ValueTask<DeveloperGatewayCredential> GetCredentialAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { forceRefresh }),
                Encoding.UTF8,
                "application/json"),
        };
        var headers = await _headers(forceRefresh, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The developer gateway header provider returned null.");
        if (headers.Count > 64)
        {
            throw new InvalidOperationException("The developer gateway header provider returned too many headers.");
        }

        foreach (var header in new List<KeyValuePair<string, string>>(headers))
        {
            if (string.IsNullOrWhiteSpace(header.Key)
                || header.Key.Length > 256
                || header.Key.IndexOfAny(InvalidHeaderNameCharacters) >= 0
                || header.Value is null
                || header.Value.Length > 65_536
                || header.Value.Contains('\r')
                || header.Value.Contains('\n')
                || header.Value.Contains('\0')
                || !request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException("The developer gateway header provider returned an invalid header.");
            }
        }

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length > _maximumResponseBytes)
        {
            throw new InvalidDataException("The developer gateway credential response is too large.");
        }

        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The developer gateway credential endpoint returned HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        EnsureUnambiguous(root);
        return new DeveloperGatewayCredential(
            root.GetProperty("accessToken").GetString() ?? string.Empty,
            root.GetProperty("expiresAt").GetDateTimeOffset(),
            root.TryGetProperty("scope", out var scope) && scope.ValueKind != JsonValueKind.Null
                ? scope.GetString()
                : null);
    }

    private static void EnsureUnambiguous(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "The developer gateway credential response contains duplicate JSON properties.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > _maximumResponseBytes)
            {
                throw new InvalidDataException("The developer gateway credential response is too large.");
            }

            output.Write(buffer, 0, read);
        }
    }
}

/// <summary>
/// Thread-safe cache and refresh coordinator for a developer gateway credential source.
/// </summary>
public sealed class CachedDeveloperGatewayCredentialSource : IDisposable
{
    private readonly IDeveloperGatewayCredentialSource _source;
    private readonly TimeSpan _refreshBeforeExpiry;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private DeveloperGatewayCredential? _cached;
    private long _invalidationGeneration;
    private long _resolvedGeneration;
    private int _disposed;

    public CachedDeveloperGatewayCredentialSource(
        IDeveloperGatewayCredentialSource source,
        TimeSpan? refreshBeforeExpiry = null,
        Func<DateTimeOffset>? clock = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _refreshBeforeExpiry = refreshBeforeExpiry ?? TimeSpan.FromMinutes(1);
        if (_refreshBeforeExpiry < TimeSpan.Zero || _refreshBeforeExpiry > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(refreshBeforeExpiry));
        }

        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ThrowIfDisposed();
            var cached = Volatile.Read(ref _cached);
            if (IsUsable(cached))
            {
                return cached!.AccessToken;
            }

            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                cached = _cached;
                if (IsUsable(cached))
                {
                    return cached!.AccessToken;
                }

                var generation = Volatile.Read(ref _invalidationGeneration);
                var credential = await _source.GetCredentialAsync(
                    forceRefresh: cached is not null || generation != Volatile.Read(ref _resolvedGeneration),
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The developer gateway credential source returned null.");
                if (credential.ExpiresAt <= _clock())
                {
                    throw new InvalidOperationException("The developer gateway returned an expired credential.");
                }

                lock (_stateGate)
                {
                    if (_disposed != 0)
                    {
                        throw new ObjectDisposedException(nameof(CachedDeveloperGatewayCredentialSource));
                    }

                    if (generation != _invalidationGeneration)
                    {
                        continue;
                    }

                    Volatile.Write(ref _cached, credential);
                    Volatile.Write(ref _resolvedGeneration, generation);
                    return credential.AccessToken;
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }
    }

    /// <summary>
    /// Invalidates the local credential after logout, revocation, or an authentication failure.
    /// The next model request obtains a fresh credential.
    /// </summary>
    public void Invalidate()
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            ThrowIfDisposed();
            _invalidationGeneration = checked(_invalidationGeneration + 1);
            Volatile.Write(ref _cached, null);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_stateGate)
        {
            Volatile.Write(ref _cached, null);
        }
    }

    private bool IsUsable(DeveloperGatewayCredential? credential)
    {
        if (credential is null)
        {
            return false;
        }

        var now = _clock();
        return credential.ExpiresAt > now
            && credential.ExpiresAt - now > _refreshBeforeExpiry;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(CachedDeveloperGatewayCredentialSource));
        }
    }
}

public static class DeveloperGatewayProvider
{
    /// <summary>
    /// Configures an OpenAI-compatible developer gateway. The access-token callback is evaluated
    /// for every request; only the short-lived token reaches the game client.
    /// </summary>
    public static OpenAICompatibleProvider Create(
        System.Net.Http.HttpClient httpClient,
        Uri endpoint,
        CachedDeveloperGatewayCredentialSource credentials,
        Action<OpenAICompatibleProviderOptions>? configure = null)
    {
        if (credentials is null)
        {
            throw new ArgumentNullException(nameof(credentials));
        }

        var options = new OpenAICompatibleProviderOptions(
            httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
            endpoint ?? throw new ArgumentNullException(nameof(endpoint)))
        {
            GetApiKeyAsync = credentials.GetAccessTokenAsync,
            OnAuthenticationFailure = _ => credentials.Invalidate(),
        };
        configure?.Invoke(options);
        if (options.ApiKey is not null)
        {
            throw new InvalidOperationException("A developer gateway provider cannot also contain a static upstream API key.");
        }

        return new OpenAICompatibleProvider(options);
    }
}

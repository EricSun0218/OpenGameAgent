using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenGameAgent.Models;

public sealed class GameOAuthAuthorizationCodeOptions
{
    public GameOAuthAuthorizationCodeOptions(
        HttpClient httpClient,
        Uri authorizationEndpoint,
        Uri tokenEndpoint,
        string clientId,
        Uri redirectUri)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        AuthorizationEndpoint = RequireEndpoint(authorizationEndpoint, nameof(authorizationEndpoint));
        TokenEndpoint = RequireEndpoint(tokenEndpoint, nameof(tokenEndpoint));
        ClientId = RequireValue(clientId, nameof(clientId));
        RedirectUri = RequireEndpoint(redirectUri, nameof(redirectUri), allowLoopbackHttp: true);
    }

    public HttpClient HttpClient { get; }

    public Uri AuthorizationEndpoint { get; }

    public Uri TokenEndpoint { get; }

    public string ClientId { get; }

    public Uri RedirectUri { get; }

    public IList<string> Scopes { get; } = new List<string>();

    public IDictionary<string, string> AuthorizationParameters { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, string> TokenParameters { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    internal static Uri RequireEndpoint(Uri value, string parameterName, bool allowLoopbackHttp = false)
    {
        if (value is null
            || !value.IsAbsoluteUri
            || value.UserInfo.Length > 0
            || (value.Scheme != Uri.UriSchemeHttps
                && !(allowLoopbackHttp && value.Scheme == Uri.UriSchemeHttp && value.IsLoopback)))
        {
            throw new ArgumentException("An absolute HTTPS endpoint without embedded credentials is required.", parameterName);
        }

        return value;
    }

    internal static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 4096
            || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException("A bounded non-empty OAuth value is required.", parameterName);
        }

        return value;
    }
}

public sealed class GameOAuthDeviceCodeOptions
{
    public GameOAuthDeviceCodeOptions(
        HttpClient httpClient,
        Uri deviceAuthorizationEndpoint,
        Uri tokenEndpoint,
        string clientId)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        DeviceAuthorizationEndpoint = GameOAuthAuthorizationCodeOptions.RequireEndpoint(
            deviceAuthorizationEndpoint,
            nameof(deviceAuthorizationEndpoint));
        TokenEndpoint = GameOAuthAuthorizationCodeOptions.RequireEndpoint(tokenEndpoint, nameof(tokenEndpoint));
        ClientId = GameOAuthAuthorizationCodeOptions.RequireValue(clientId, nameof(clientId));
    }

    public HttpClient HttpClient { get; }

    public Uri DeviceAuthorizationEndpoint { get; }

    public Uri TokenEndpoint { get; }

    public string ClientId { get; }

    public IList<string> Scopes { get; } = new List<string>();

    public IDictionary<string, string> DeviceParameters { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, string> TokenParameters { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;
}

public static class GameOAuth
{
    private const int MaximumResponseCharacters = 1_000_000;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(365);

    public static StoredGameProviderAuthentication CreateAuthorizationCodeAuthentication(
        string providerId,
        IGameCredentialStore store,
        GameOAuthAuthorizationCodeOptions options,
        string profile = "default",
        Func<DateTimeOffset>? clock = null,
        TimeSpan? refreshSkew = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return new StoredGameProviderAuthentication(
            providerId,
            store,
            new[] { "oauth-authorization-code" },
            (_, interaction, cancellationToken) =>
                LoginAuthorizationCodeAsync(options, interaction, cancellationToken),
            (credential, cancellationToken) => RefreshAsync(
                options.HttpClient,
                options.TokenEndpoint,
                options.ClientId,
                credential,
                new Dictionary<string, string>(options.TokenParameters, StringComparer.Ordinal),
                cancellationToken),
            profile,
            clock,
            refreshSkew);
    }

    public static StoredGameProviderAuthentication CreateDeviceCodeAuthentication(
        string providerId,
        IGameCredentialStore store,
        GameOAuthDeviceCodeOptions options,
        string profile = "default",
        Func<DateTimeOffset>? clock = null,
        TimeSpan? refreshSkew = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return new StoredGameProviderAuthentication(
            providerId,
            store,
            new[] { "oauth-device-code" },
            (_, interaction, cancellationToken) =>
                LoginDeviceCodeAsync(options, interaction, cancellationToken),
            (credential, cancellationToken) => RefreshAsync(
                options.HttpClient,
                options.TokenEndpoint,
                options.ClientId,
                credential,
                new Dictionary<string, string>(options.TokenParameters, StringComparer.Ordinal),
                cancellationToken),
            profile,
            clock,
            refreshSkew);
    }

    public static async ValueTask<GameCredential> LoginAuthorizationCodeAsync(
        GameOAuthAuthorizationCodeOptions options,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (interaction is null)
        {
            throw new ArgumentNullException(nameof(interaction));
        }

        ValidateCollections(options.Scopes, options.AuthorizationParameters, options.TokenParameters);
        var verifier = RandomUrlToken(64);
        var state = RandomUrlToken(32);
        var challenge = Base64Url(Sha256(Encoding.ASCII.GetBytes(verifier)));
        var authorizationFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = options.RedirectUri.AbsoluteUri,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var scopes = NormalizeScopes(options.Scopes);
        if (scopes.Count > 0)
        {
            authorizationFields["scope"] = string.Join(" ", scopes);
        }

        var authorization = BuildUri(
            options.AuthorizationEndpoint,
            Merge(options.AuthorizationParameters, authorizationFields));
        if (interaction.OpenBrowserAsync is not null)
        {
            await interaction.OpenBrowserAsync(authorization, cancellationToken).ConfigureAwait(false);
        }
        else if (interaction.NotifyAsync is not null)
        {
            await interaction.NotifyAsync(authorization.AbsoluteUri, cancellationToken).ConfigureAwait(false);
        }

        var prompt = interaction.PromptAsync
            ?? throw new InvalidOperationException("The OAuth interaction must accept the authorization code or callback URL.");
        var response = await prompt(
            "Paste the OAuth callback URL or authorization code.",
            true,
            cancellationToken).ConfigureAwait(false);
        var code = ParseAuthorizationResponse(response, state);
        var fields = Merge(
            options.TokenParameters,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = options.ClientId,
                ["code"] = code,
                ["redirect_uri"] = options.RedirectUri.AbsoluteUri,
                ["code_verifier"] = verifier,
            });
        return await ExchangeAsync(options.HttpClient, options.TokenEndpoint, fields, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async ValueTask<GameCredential> LoginDeviceCodeAsync(
        GameOAuthDeviceCodeOptions options,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (interaction is null)
        {
            throw new ArgumentNullException(nameof(interaction));
        }

        ValidateCollections(options.Scopes, options.DeviceParameters, options.TokenParameters);
        if (options.DelayAsync is null)
        {
            throw new ArgumentException("A device-code delay strategy is required.", nameof(options));
        }

        var requiredDeviceFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = options.ClientId,
        };
        var deviceScopes = NormalizeScopes(options.Scopes);
        if (deviceScopes.Count > 0)
        {
            requiredDeviceFields["scope"] = string.Join(" ", deviceScopes);
        }

        var requestFields = Merge(options.DeviceParameters, requiredDeviceFields);
        var deviceResponse = await PostFormAsync(
            options.HttpClient,
            options.DeviceAuthorizationEndpoint,
            requestFields,
            cancellationToken).ConfigureAwait(false);
        using var document = ParseObject(deviceResponse.Body, "The device authorization response is invalid.");
        if (!deviceResponse.Success)
        {
            throw OAuthFailure("Device authorization failed", document.RootElement, deviceResponse.StatusCode);
        }

        var root = document.RootElement;
        var deviceCode = RequiredString(root, "device_code", 65_536);
        var userCode = RequiredString(root, "user_code", 4096);
        var verification = OptionalString(root, "verification_uri_complete")
            ?? OptionalString(root, "verification_uri")
            ?? throw new InvalidOperationException("The device authorization response omitted its verification URI.");
        var verificationUri = GameOAuthAuthorizationCodeOptions.RequireEndpoint(
            new Uri(verification, UriKind.Absolute),
            "verification_uri");
        var expiresIn = ReadSeconds(root, "expires_in", TimeSpan.FromMinutes(15));
        var interval = ReadSeconds(root, "interval", TimeSpan.FromSeconds(5));
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("The device authorization polling interval is invalid.");
        }

        if (interaction.NotifyAsync is not null)
        {
            await interaction.NotifyAsync($"Enter device code {userCode} at {verificationUri}", cancellationToken)
                .ConfigureAwait(false);
        }

        if (interaction.OpenBrowserAsync is not null)
        {
            await interaction.OpenBrowserAsync(verificationUri, cancellationToken).ConfigureAwait(false);
        }

        var deadline = DateTimeOffset.UtcNow + expiresIn;
        var currentInterval = interval;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await options.DelayAsync(currentInterval, cancellationToken).ConfigureAwait(false);
            var fields = Merge(
                options.TokenParameters,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = options.ClientId,
                    ["device_code"] = deviceCode,
                });
            var tokenResponse = await PostFormAsync(
                options.HttpClient,
                options.TokenEndpoint,
                fields,
                cancellationToken).ConfigureAwait(false);
            using var tokenDocument = ParseObject(tokenResponse.Body, "The device token response is invalid.");
            if (tokenResponse.Success)
            {
                return ParseCredential(tokenDocument.RootElement, null);
            }

            var error = OptionalString(tokenDocument.RootElement, "error") ?? string.Empty;
            if (string.Equals(error, "authorization_pending", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(error, "slow_down", StringComparison.Ordinal))
            {
                currentInterval = currentInterval + TimeSpan.FromSeconds(5);
                if (currentInterval > TimeSpan.FromMinutes(5))
                {
                    currentInterval = TimeSpan.FromMinutes(5);
                }

                continue;
            }

            throw OAuthFailure("Device authorization failed", tokenDocument.RootElement, tokenResponse.StatusCode);
        }

        throw new TimeoutException("The device authorization code expired before login completed.");
    }

    public static ValueTask<GameCredential> RefreshAsync(
        HttpClient httpClient,
        Uri tokenEndpoint,
        string clientId,
        GameCredential credential,
        IReadOnlyDictionary<string, string>? tokenParameters = null,
        CancellationToken cancellationToken = default)
    {
        if (httpClient is null)
        {
            throw new ArgumentNullException(nameof(httpClient));
        }

        GameOAuthAuthorizationCodeOptions.RequireEndpoint(tokenEndpoint, nameof(tokenEndpoint));
        GameOAuthAuthorizationCodeOptions.RequireValue(clientId, nameof(clientId));
        if (credential is null || credential.Kind != GameCredentialKind.OAuth)
        {
            throw new ArgumentException("An OAuth credential is required.", nameof(credential));
        }

        if (!credential.Metadata.TryGetValue("refresh_token", out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("The OAuth credential does not contain a refresh token.");
        }

        var fields = Merge(
            tokenParameters ?? new Dictionary<string, string>(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
            });
        return ExchangeAsync(httpClient, tokenEndpoint, fields, refreshToken, cancellationToken);
    }

    private static async ValueTask<GameCredential> ExchangeAsync(
        HttpClient client,
        Uri endpoint,
        IReadOnlyDictionary<string, string> fields,
        string? previousRefreshToken,
        CancellationToken cancellationToken)
    {
        var response = await PostFormAsync(client, endpoint, fields, cancellationToken).ConfigureAwait(false);
        using var document = ParseObject(response.Body, "The OAuth token response is invalid.");
        if (!response.Success)
        {
            throw OAuthFailure("OAuth token exchange failed", document.RootElement, response.StatusCode);
        }

        return ParseCredential(document.RootElement, previousRefreshToken);
    }

    private static GameCredential ParseCredential(JsonElement root, string? previousRefreshToken)
    {
        var accessToken = RequiredString(root, "access_token", 65_536);
        var refreshToken = OptionalString(root, "refresh_token") ?? previousRefreshToken;
        var tokenType = OptionalString(root, "token_type");
        var scope = OptionalString(root, "scope");
        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expires_in", out var expiresElement))
        {
            var lifetime = ReadSeconds(root, "expires_in", TimeSpan.Zero);
            if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime)
            {
                throw new InvalidOperationException("The OAuth token lifetime is invalid.");
            }

            expiresAt = DateTimeOffset.UtcNow + lifetime;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            metadata["refresh_token"] = Bound(refreshToken!, 65_536, "refresh_token");
        }

        if (!string.IsNullOrWhiteSpace(tokenType))
        {
            metadata["token_type"] = Bound(tokenType!, 256, "token_type");
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            metadata["scope"] = Bound(scope!, 16_384, "scope");
        }

        return new GameCredential(GameCredentialKind.OAuth, accessToken, expiresAt, metadata);
    }

    private static string ParseAuthorizationResponse(string value, string expectedState)
    {
        var input = GameOAuthAuthorizationCodeOptions.RequireValue(value?.Trim() ?? string.Empty, nameof(value));
        if (!Uri.TryCreate(input, UriKind.Absolute, out var callback))
        {
            return input;
        }

        var query = ParseQuery(callback.Query);
        if (query.TryGetValue("error", out var error))
        {
            throw new InvalidOperationException("OAuth authorization failed: " + Bound(error, 4096, "error"));
        }

        if (!query.TryGetValue("state", out var state)
            || !FixedTimeEquals(state, expectedState))
        {
            throw new InvalidOperationException("The OAuth callback state did not match the active login request.");
        }

        return query.TryGetValue("code", out var code)
            ? GameOAuthAuthorizationCodeOptions.RequireValue(code, "code")
            : throw new InvalidOperationException("The OAuth callback omitted the authorization code.");
    }

    private static async ValueTask<FormResponse> PostFormAsync(
        HttpClient client,
        Uri endpoint,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        ValidateParameters(fields);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (body.Length > MaximumResponseCharacters)
        {
            throw new InvalidOperationException("The OAuth response exceeded the configured safety bound.");
        }

        return new FormResponse(response.IsSuccessStatusCode, response.StatusCode, body);
    }

    private static JsonDocument ParseObject(string body, string error)
    {
        try
        {
            var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidOperationException(error);
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(error, exception);
        }
    }

    private static Exception OAuthFailure(string prefix, JsonElement root, HttpStatusCode statusCode)
    {
        var code = OptionalString(root, "error");
        var description = OptionalString(root, "error_description") ?? OptionalString(root, "message");
        var details = string.Join(": ", new[] { code, description }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new InvalidOperationException(
            $"{prefix} with HTTP {(int)statusCode}{(details.Length == 0 ? string.Empty : ": " + Bound(details, 8192, "error"))}.");
    }

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> parameters)
    {
        var query = ParseQuery(endpoint.Query);
        foreach (var pair in parameters)
        {
            query[pair.Key] = pair.Value;
        }

        var builder = new UriBuilder(endpoint)
        {
            Query = string.Join("&", query.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
        };
        return builder.Uri;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(new[] { '=' }, 2);
            result[Uri.UnescapeDataString(pieces[0])] = pieces.Length == 2
                ? Uri.UnescapeDataString(pieces[1])
                : string.Empty;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IEnumerable<KeyValuePair<string, string>> configured,
        IReadOnlyDictionary<string, string> required)
    {
        var configuredValues = configured.ToArray();
        ValidateParameters(configuredValues);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in configuredValues)
        {
            result[pair.Key] = pair.Value;
        }
        foreach (var pair in required)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static void ValidateCollections(
        IEnumerable<string> scopes,
        IEnumerable<KeyValuePair<string, string>> first,
        IEnumerable<KeyValuePair<string, string>> second)
    {
        _ = NormalizeScopes(scopes);
        ValidateParameters(first);
        ValidateParameters(second);
    }

    private static IReadOnlyList<string> NormalizeScopes(IEnumerable<string> scopes)
    {
        var result = scopes
            .Select(scope => GameOAuthAuthorizationCodeOptions.RequireValue(scope, nameof(scopes)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (result.Length > 256)
        {
            throw new ArgumentException("At most 256 OAuth scopes are supported.", nameof(scopes));
        }

        return result;
    }

    private static void ValidateParameters(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var values = parameters.ToArray();
        if (values.Length > 256)
        {
            throw new ArgumentException("At most 256 OAuth parameters are supported.", nameof(parameters));
        }

        foreach (var pair in values)
        {
            GameOAuthAuthorizationCodeOptions.RequireValue(pair.Key, nameof(parameters));
            GameOAuthAuthorizationCodeOptions.RequireValue(pair.Value, nameof(parameters));
        }
    }

    private static string RequiredString(JsonElement root, string property, int maximum)
    {
        var value = OptionalString(root, property);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"The OAuth response omitted '{property}'.")
            : Bound(value!, maximum, property);
    }

    private static string? OptionalString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static TimeSpan ReadSeconds(JsonElement root, string property, TimeSpan fallback)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return fallback;
        }

        double seconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out seconds))
        {
        }
        else if (value.ValueKind == JsonValueKind.String
                 && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
        }
        else
        {
            throw new InvalidOperationException($"The OAuth response field '{property}' is invalid.");
        }

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > MaximumLifetime.TotalSeconds)
        {
            throw new InvalidOperationException($"The OAuth response field '{property}' is outside its allowed range.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string Bound(string value, int maximum, string name) =>
        value.Length <= maximum
            ? value
            : throw new InvalidOperationException($"The OAuth field '{name}' exceeded its safety bound.");

    private static string RandomUrlToken(int bytes)
    {
        var data = new byte[bytes];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(data);
        return Base64Url(data);
    }

    private static byte[] Sha256(byte[] value)
    {
        using var hash = SHA256.Create();
        return hash.ComputeHash(value);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed class FormResponse
    {
        public FormResponse(bool success, HttpStatusCode statusCode, string body)
        {
            Success = success;
            StatusCode = statusCode;
            Body = body;
        }

        public bool Success { get; }

        public HttpStatusCode StatusCode { get; }

        public string Body { get; }
    }
}

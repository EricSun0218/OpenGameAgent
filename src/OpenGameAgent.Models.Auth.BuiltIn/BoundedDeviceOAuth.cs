using System.Text.Json;

namespace OpenGameAgent.Models.Auth.BuiltIn;

internal sealed class DeviceOAuthOptions
{
    public DeviceOAuthOptions(
        Uri deviceEndpoint,
        Uri tokenEndpoint,
        string clientId,
        IReadOnlyCollection<string> allowedVerificationHosts,
        OAuthRuntimeSettings runtime)
    {
        DeviceEndpoint = BoundedOAuthHttp.RequireHttps(deviceEndpoint, nameof(deviceEndpoint));
        TokenEndpoint = BoundedOAuthHttp.RequireHttps(tokenEndpoint, nameof(tokenEndpoint));
        ClientId = RequireValue(clientId, nameof(clientId));
        AllowedVerificationHosts = Array.AsReadOnly(
            (allowedVerificationHosts ?? throw new ArgumentNullException(nameof(allowedVerificationHosts)))
            .Select(host => RequireValue(host, nameof(allowedVerificationHosts)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        if (AllowedVerificationHosts.Count == 0 || AllowedVerificationHosts.Count > 16)
        {
            throw new ArgumentException("At least one bounded verification host is required.", nameof(allowedVerificationHosts));
        }

        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Uri DeviceEndpoint { get; }

    public Uri TokenEndpoint { get; }

    public string ClientId { get; }

    public IReadOnlyCollection<string> AllowedVerificationHosts { get; }

    public OAuthRuntimeSettings Runtime { get; }

    public IList<string> Scopes { get; } = new List<string>();

    public IDictionary<string, string> DeviceParameters { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, string> TokenParameters { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public TimeSpan DefaultTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    private static string RequireValue(string value, string parameterName)
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

internal static class BoundedDeviceOAuth
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumPollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumDeviceLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumTokenLifetime = TimeSpan.FromDays(365);

    public static async ValueTask<GameCredential> LoginAsync(
        DeviceOAuthOptions options,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (interaction is null)
        {
            throw new ArgumentNullException(nameof(interaction));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var deviceFields = Merge(options.DeviceParameters, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = options.ClientId,
        });
        var scopes = NormalizeScopes(options.Scopes);
        if (scopes.Count > 0)
        {
            deviceFields["scope"] = string.Join(" ", scopes);
        }

        using var deviceResponse = await BoundedOAuthHttp.PostFormAsync(
            options.Runtime.HttpClient,
            options.DeviceEndpoint,
            deviceFields,
            options.Runtime.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!deviceResponse.IsSuccess)
        {
            throw BoundedOAuthHttp.Failure("Device authorization", deviceResponse);
        }

        var root = deviceResponse.Root;
        var deviceCode = BoundedOAuthHttp.RequiredString(root, "device_code");
        var userCode = BoundedOAuthHttp.RequiredString(root, "user_code", 4096);
        var verificationText = BoundedOAuthHttp.OptionalString(root, "verification_uri_complete", 16_384)
                               ?? BoundedOAuthHttp.RequiredString(root, "verification_uri", 16_384);
        var verificationUri = ValidateVerificationUri(verificationText, options.AllowedVerificationHosts);
        var expiresIn = BoundedOAuthHttp.ReadSeconds(
            root,
            "expires_in",
            TimeSpan.FromMinutes(15),
            TimeSpan.FromSeconds(1),
            MaximumDeviceLifetime);
        var interval = ReadPollInterval(root, "interval", DefaultPollInterval);

        if (interaction.NotifyAsync is not null)
        {
            await interaction.NotifyAsync(
                $"Enter device code {userCode} at {verificationUri}",
                cancellationToken).ConfigureAwait(false);
        }

        if (interaction.OpenBrowserAsync is not null)
        {
            await interaction.OpenBrowserAsync(verificationUri, cancellationToken).ConfigureAwait(false);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var totalLifetime = expiresIn < options.Runtime.LoginTimeout ? expiresIn : options.Runtime.LoginTimeout;
        lifetime.CancelAfter(totalLifetime);
        var pollInterval = interval;
        try
        {
            while (true)
            {
                await BoundedOAuthHttp.WaitAsync(
                    options.Runtime.DelayAsync(pollInterval, lifetime.Token),
                    lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                var fields = Merge(options.TokenParameters, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = options.ClientId,
                    ["device_code"] = deviceCode,
                });
                using var response = await BoundedOAuthHttp.PostFormAsync(
                    options.Runtime.HttpClient,
                    options.TokenEndpoint,
                    fields,
                    options.Runtime.RequestTimeout,
                    lifetime.Token).ConfigureAwait(false);
                if (response.IsSuccess)
                {
                    var credential = ParseCredential(
                        response.Root,
                        options.Runtime.Clock,
                        options.DefaultTokenLifetime,
                        previousRefreshToken: null,
                        requireRefreshToken: true);
                    cancellationToken.ThrowIfCancellationRequested();
                    return credential;
                }

                var error = BoundedOAuthHttp.OptionalString(response.Root, "error", 4096);
                if (string.Equals(error, "authorization_pending", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(error, "slow_down", StringComparison.Ordinal))
                {
                    var serverInterval = TryReadPollInterval(response.Root, "interval");
                    pollInterval = serverInterval is { } provided && provided > pollInterval
                        ? provided
                        : pollInterval + TimeSpan.FromSeconds(5);
                    if (pollInterval > MaximumPollInterval)
                    {
                        pollInterval = MaximumPollInterval;
                    }

                    continue;
                }

                if (error is "access_denied" or "authorization_denied")
                {
                    throw new InvalidOperationException("Device authorization was denied.");
                }

                if (string.Equals(error, "expired_token", StringComparison.Ordinal))
                {
                    throw new TimeoutException("The device authorization code expired.");
                }

                throw BoundedOAuthHttp.Failure("Device token polling", response);
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("The device authorization flow timed out.", exception);
        }
    }

    public static async ValueTask<GameCredential> RefreshAsync(
        DeviceOAuthOptions options,
        GameCredential credential,
        CancellationToken cancellationToken)
    {
        if (credential is null || credential.Kind != GameCredentialKind.OAuth)
        {
            throw new ArgumentException("An OAuth credential is required.", nameof(credential));
        }

        if (!credential.Metadata.TryGetValue("refresh_token", out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("The OAuth credential has no refresh token.");
        }

        var fields = Merge(options.TokenParameters, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId,
            ["refresh_token"] = refreshToken,
        });
        using var response = await BoundedOAuthHttp.PostFormAsync(
            options.Runtime.HttpClient,
            options.TokenEndpoint,
            fields,
            options.Runtime.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw BoundedOAuthHttp.Failure("OAuth token refresh", response);
        }

        return ParseCredential(
            response.Root,
            options.Runtime.Clock,
            options.DefaultTokenLifetime,
            refreshToken,
            requireRefreshToken: false);
    }

    internal static GameCredential ParseCredential(
        JsonElement root,
        Func<DateTimeOffset> clock,
        TimeSpan defaultLifetime,
        string? previousRefreshToken,
        bool requireRefreshToken)
    {
        var accessToken = BoundedOAuthHttp.RequiredString(root, "access_token");
        var refreshToken = BoundedOAuthHttp.OptionalString(root, "refresh_token") ?? previousRefreshToken;
        if (requireRefreshToken && string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("The OAuth response omitted 'refresh_token'.");
        }

        var lifetime = BoundedOAuthHttp.ReadSeconds(
            root,
            "expires_in",
            defaultLifetime,
            TimeSpan.FromSeconds(1),
            MaximumTokenLifetime);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            metadata["refresh_token"] = refreshToken!;
        }

        var tokenType = BoundedOAuthHttp.OptionalString(root, "token_type", 256);
        if (!string.IsNullOrWhiteSpace(tokenType))
        {
            metadata["token_type"] = tokenType!;
        }

        var scope = BoundedOAuthHttp.OptionalString(root, "scope", 16_384);
        if (!string.IsNullOrWhiteSpace(scope))
        {
            metadata["scope"] = scope!;
        }

        return new GameCredential(GameCredentialKind.OAuth, accessToken, clock() + lifetime, metadata);
    }

    private static Uri ValidateVerificationUri(
        string raw,
        IReadOnlyCollection<string> allowedHosts)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
            || !allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The device authorization response contained an untrusted verification URL.");
        }

        return uri;
    }

    private static TimeSpan ReadPollInterval(JsonElement root, string name, TimeSpan fallback) =>
        TryReadPollInterval(root, name) ?? fallback;

    private static TimeSpan? TryReadPollInterval(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out _))
        {
            return null;
        }

        try
        {
            return BoundedOAuthHttp.ReadSeconds(
                root,
                name,
                DefaultPollInterval,
                MinimumPollInterval,
                MaximumPollInterval);
        }
        catch (InvalidOperationException)
        {
            return DefaultPollInterval;
        }
    }

    private static IReadOnlyList<string> NormalizeScopes(IList<string> scopes)
    {
        if (scopes.Count > 64)
        {
            throw new ArgumentException("At most 64 OAuth scopes are supported.", nameof(scopes));
        }

        return scopes
            .Select(scope => RequireField(scope, 4096))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, string> Merge(
        IEnumerable<KeyValuePair<string, string>> configured,
        IReadOnlyDictionary<string, string> required)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in configured)
        {
            result.Add(RequireField(pair.Key, 256), RequireField(pair.Value, 65_536));
        }

        foreach (var pair in required)
        {
            result[pair.Key] = pair.Value;
        }

        if (result.Count > 64)
        {
            throw new ArgumentException("At most 64 OAuth parameters are supported.", nameof(configured));
        }

        return result;
    }

    private static string RequireField(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximum
            || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException("An OAuth parameter is invalid.");
        }

        return value;
    }
}

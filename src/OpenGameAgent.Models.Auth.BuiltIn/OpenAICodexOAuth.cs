using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenGameAgent.Models.Auth.BuiltIn;

internal static class OpenAICodexOAuth
{
    private const string Scope = "openid profile email offline_access";
    private const string AccountIdMetadata = "openai-codex.account-id";
    private static readonly Uri AuthorizationEndpoint = new("https://auth.openai.com/oauth/authorize");
    private static readonly Uri TokenEndpoint = new("https://auth.openai.com/oauth/token");
    private static readonly Uri DeviceStartEndpoint =
        new("https://auth.openai.com/api/accounts/deviceauth/usercode");
    private static readonly Uri DevicePollEndpoint =
        new("https://auth.openai.com/api/accounts/deviceauth/token");
    private static readonly Uri DeviceVerificationUri = new("https://auth.openai.com/codex/device");
    private static readonly Uri DeviceRedirectUri = new("https://auth.openai.com/deviceauth/callback");

    public static ValueTask<GameCredential> LoginBrowserAsync(
        OAuthRuntimeSettings runtime,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken) =>
        SecureLoopbackOAuth.LoginAsync(
            new SecureLoopbackOAuthOptions(
                AuthorizationEndpoint,
                "localhost",
                "/auth/callback",
                runtime.LoginTimeout,
                (redirectUri, challenge, state) => BuildUri(
                    AuthorizationEndpoint,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["response_type"] = "code",
                        ["client_id"] = runtime.OpenAICodexClientId!,
                        ["redirect_uri"] = redirectUri.AbsoluteUri,
                        ["scope"] = Scope,
                        ["code_challenge"] = challenge,
                        ["code_challenge_method"] = "S256",
                        ["state"] = state,
                        ["id_token_add_organizations"] = "true",
                        ["codex_cli_simplified_flow"] = "true",
                        ["originator"] = runtime.OpenAICodexOriginator,
                    }),
                (code, verifier, _, redirectUri, token) => ExchangeCodeAsync(
                    runtime,
                    code,
                    verifier,
                    redirectUri,
                    token),
                port: 1455,
                statePlacement: LoopbackStatePlacement.Query),
            interaction,
            cancellationToken);

    public static async ValueTask<GameCredential> LoginDeviceAsync(
        OAuthRuntimeSettings runtime,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        if (interaction is null)
        {
            throw new ArgumentNullException(nameof(interaction));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var start = await BoundedOAuthHttp.PostJsonAsync(
            runtime.HttpClient,
            DeviceStartEndpoint,
            new Dictionary<string, string> { ["client_id"] = runtime.OpenAICodexClientId! },
            runtime.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!start.IsSuccess)
        {
            throw BoundedOAuthHttp.Failure("Device authorization", start);
        }

        var deviceAuthId = BoundedOAuthHttp.RequiredString(start.Root, "device_auth_id");
        var userCode = BoundedOAuthHttp.RequiredString(start.Root, "user_code", 4096);
        var interval = ReadInterval(start.Root);
        if (interaction.NotifyAsync is not null)
        {
            await interaction.NotifyAsync(
                $"Enter device code {userCode} at {DeviceVerificationUri}",
                cancellationToken).ConfigureAwait(false);
        }

        if (interaction.OpenBrowserAsync is not null)
        {
            await interaction.OpenBrowserAsync(DeviceVerificationUri, cancellationToken).ConfigureAwait(false);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(runtime.LoginTimeout < TimeSpan.FromMinutes(15)
            ? runtime.LoginTimeout
            : TimeSpan.FromMinutes(15));
        var currentInterval = interval;
        var firstPoll = true;
        try
        {
            while (true)
            {
                if (!firstPoll)
                {
                    await BoundedOAuthHttp.WaitAsync(
                        runtime.DelayAsync(currentInterval, lifetime.Token),
                        lifetime.Token).ConfigureAwait(false);
                }

                firstPoll = false;
                using var poll = await BoundedOAuthHttp.PostJsonAsync(
                    runtime.HttpClient,
                    DevicePollEndpoint,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["device_auth_id"] = deviceAuthId,
                        ["user_code"] = userCode,
                    },
                    runtime.RequestTimeout,
                    lifetime.Token).ConfigureAwait(false);
                if (poll.IsSuccess)
                {
                    var code = BoundedOAuthHttp.RequiredString(poll.Root, "authorization_code");
                    var verifier = BoundedOAuthHttp.RequiredString(poll.Root, "code_verifier");
                    var credential = await ExchangeCodeAsync(
                        runtime,
                        code,
                        verifier,
                        DeviceRedirectUri,
                        lifetime.Token).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return credential;
                }

                if (poll.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    continue;
                }

                var error = ReadErrorCode(poll.Root);
                if (string.Equals(error, "authorization_pending", StringComparison.Ordinal)
                    || string.Equals(error, "deviceauth_authorization_pending", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(error, "slow_down", StringComparison.Ordinal))
                {
                    currentInterval += TimeSpan.FromSeconds(5);
                    if (currentInterval > TimeSpan.FromMinutes(5))
                    {
                        currentInterval = TimeSpan.FromMinutes(5);
                    }

                    continue;
                }

                throw BoundedOAuthHttp.Failure("Device authorization polling", poll);
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("The device authorization flow timed out.", exception);
        }
    }

    public static async ValueTask<GameCredential> RefreshAsync(
        OAuthRuntimeSettings runtime,
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

        using var response = await BoundedOAuthHttp.PostFormAsync(
            runtime.HttpClient,
            TokenEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = runtime.OpenAICodexClientId!,
            },
            runtime.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw BoundedOAuthHttp.Failure("OAuth token refresh", response);
        }

        return CredentialFromResponse(runtime, response.Root, refreshToken);
    }

    private static async ValueTask<GameCredential> ExchangeCodeAsync(
        OAuthRuntimeSettings runtime,
        string code,
        string verifier,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        using var response = await BoundedOAuthHttp.PostFormAsync(
            runtime.HttpClient,
            TokenEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = runtime.OpenAICodexClientId!,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri.AbsoluteUri,
            },
            runtime.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw BoundedOAuthHttp.Failure("OAuth token exchange", response);
        }

        return CredentialFromResponse(runtime, response.Root, previousRefreshToken: null);
    }

    private static GameCredential CredentialFromResponse(
        OAuthRuntimeSettings runtime,
        JsonElement root,
        string? previousRefreshToken)
    {
        var credential = BoundedDeviceOAuth.ParseCredential(
            root,
            runtime.Clock,
            TimeSpan.FromHours(1),
            previousRefreshToken,
            requireRefreshToken: true);
        var metadata = new Dictionary<string, string>(credential.Metadata, StringComparer.Ordinal)
        {
            [AccountIdMetadata] = ExtractAccountId(credential.Secret),
        };
        return new GameCredential(
            GameCredentialKind.OAuth,
            credential.Secret,
            credential.ExpiresAt,
            metadata);
    }

    private static string ExtractAccountId(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length != 3 || parts[1].Length > 1_400_000)
            {
                throw new FormatException();
            }

            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > 1_000_000)
            {
                throw new FormatException();
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var accountId = document.RootElement
                .GetProperty("https://api.openai.com/auth")
                .GetProperty("chatgpt_account_id")
                .GetString();
            if (string.IsNullOrWhiteSpace(accountId)
                || accountId.Length > 512
                || accountId.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new FormatException();
            }

            return accountId;
        }
        catch (Exception exception) when (exception is FormatException
                                          or JsonException
                                          or KeyNotFoundException
                                          or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "The OAuth access token did not contain a valid account identifier.",
                exception);
        }
    }

    private static TimeSpan ReadInterval(JsonElement root)
    {
        if (!root.TryGetProperty("interval", out _))
        {
            return TimeSpan.FromSeconds(5);
        }

        try
        {
            return BoundedOAuthHttp.ReadSeconds(
                root,
                "interval",
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5));
        }
        catch (InvalidOperationException)
        {
            return TimeSpan.FromSeconds(5);
        }
    }

    private static string? ReadErrorCode(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            var value = error.GetString();
            return value is { Length: <= 4096 } ? value : null;
        }

        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String)
        {
            var value = code.GetString();
            return value is { Length: <= 4096 } ? value : null;
        }

        return null;
    }

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> fields) =>
        new UriBuilder(endpoint)
        {
            Query = string.Join("&", fields.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
        }.Uri;
}

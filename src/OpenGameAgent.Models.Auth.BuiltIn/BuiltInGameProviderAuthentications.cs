using System.Net;

namespace OpenGameAgent.Models.Auth.BuiltIn;

public static class BuiltInGameProviderAuthentications
{
    public const string AnthropicProviderId = "anthropic";
    public const string OpenRouterProviderId = "openrouter";
    public const string XaiProviderId = "xai";
    public const string KimiForCodingProviderId = "kimi-for-coding";
    public const string OpenAICodexProviderId = "openai-codex";

    private const string AnthropicScopes =
        "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    private const string XaiScopes = "openid profile email offline_access grok-cli:access api:access";

    private static readonly Uri AnthropicAuthorizationEndpoint =
        new("https://claude.ai/oauth/authorize", UriKind.Absolute);
    private static readonly Uri AnthropicTokenEndpoint =
        new("https://platform.claude.com/v1/oauth/token", UriKind.Absolute);
    private static readonly Uri OpenRouterAuthorizationEndpoint =
        new("https://openrouter.ai/auth", UriKind.Absolute);
    private static readonly Uri OpenRouterTokenEndpoint =
        new("https://openrouter.ai/api/v1/auth/keys", UriKind.Absolute);
    private static readonly Uri XaiDeviceEndpoint =
        new("https://auth.x.ai/oauth2/device/code", UriKind.Absolute);
    private static readonly Uri XaiTokenEndpoint =
        new("https://auth.x.ai/oauth2/token", UriKind.Absolute);
    private static readonly Uri KimiDeviceEndpoint =
        new("https://auth.kimi.com/api/oauth/device_authorization", UriKind.Absolute);
    private static readonly Uri KimiTokenEndpoint =
        new("https://auth.kimi.com/api/oauth/token", UriKind.Absolute);

    public static IGameProviderAuthentication CreateAnthropic(BuiltInGameOAuthOptions options)
    {
        var runtime = Require(options).Snapshot();
        if (runtime.AnthropicClientId is null)
        {
            return ClientRegistrationRequired(
                AnthropicProviderId,
                ApiKeyEnvironment("ANTHROPIC_API_KEY"));
        }

        var stored = new StoredGameProviderAuthentication(
            AnthropicProviderId,
            runtime.CredentialStore,
            new[] { "oauth-anthropic-subscription" },
            (_, interaction, cancellationToken) => LoginAnthropicAsync(runtime, interaction, cancellationToken),
            (credential, cancellationToken) => RefreshAnthropicAsync(runtime, credential, cancellationToken),
            runtime.Profile,
            runtime.Clock,
            runtime.RefreshSkew,
            RefreshTimeoutMilliseconds(runtime));
        return WithApiKeyFallback(
            new ResolutionOverlayAuthentication(
                stored,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["anthropic-beta"] = "oauth-2025-04-20",
                }),
            "ANTHROPIC_API_KEY");
    }

    public static IGameProviderAuthentication CreateOpenRouter(BuiltInGameOAuthOptions options)
    {
        var runtime = Require(options).Snapshot();
        var stored = new StoredGameProviderAuthentication(
            OpenRouterProviderId,
            runtime.CredentialStore,
            new[] { "oauth-openrouter" },
            (_, interaction, cancellationToken) => LoginOpenRouterAsync(runtime, interaction, cancellationToken),
            refresh: null,
            runtime.Profile,
            runtime.Clock,
            runtime.RefreshSkew,
            RefreshTimeoutMilliseconds(runtime));
        return WithApiKeyFallback(stored, "OPENROUTER_API_KEY");
    }

    public static IGameProviderAuthentication CreateXai(BuiltInGameOAuthOptions options)
    {
        var runtime = Require(options).Snapshot();
        if (runtime.XaiClientId is null)
        {
            return ClientRegistrationRequired(XaiProviderId, ApiKeyEnvironment("XAI_API_KEY"));
        }

        var device = XaiDeviceOptions(runtime);
        var stored = new StoredGameProviderAuthentication(
            XaiProviderId,
            runtime.CredentialStore,
            new[] { "oauth-xai-device-code" },
            (_, interaction, cancellationToken) =>
                BoundedDeviceOAuth.LoginAsync(device, interaction, cancellationToken),
            (credential, cancellationToken) =>
                BoundedDeviceOAuth.RefreshAsync(device, credential, cancellationToken),
            runtime.Profile,
            runtime.Clock,
            runtime.RefreshSkew,
            RefreshTimeoutMilliseconds(runtime));
        return WithApiKeyFallback(stored, "XAI_API_KEY");
    }

    public static IGameProviderAuthentication CreateKimiForCoding(BuiltInGameOAuthOptions options)
    {
        var runtime = Require(options).Snapshot();
        if (runtime.KimiForCodingClientId is null)
        {
            return ClientRegistrationRequired(
                KimiForCodingProviderId,
                ApiKeyEnvironment("KIMI_API_KEY"));
        }

        var device = KimiDeviceOptions(runtime);
        var stored = new StoredGameProviderAuthentication(
            KimiForCodingProviderId,
            runtime.CredentialStore,
            new[] { "oauth-kimi-device-code" },
            (_, interaction, cancellationToken) =>
                BoundedDeviceOAuth.LoginAsync(device, interaction, cancellationToken),
            (credential, cancellationToken) =>
                RefreshKimiWithRetryAsync(device, credential, cancellationToken),
            runtime.Profile,
            runtime.Clock,
            runtime.RefreshSkew,
            RefreshTimeoutMilliseconds(runtime));
        return WithApiKeyFallback(stored, "KIMI_API_KEY");
    }

    public static IGameProviderAuthentication CreateOpenAICodex(BuiltInGameOAuthOptions options)
    {
        var runtime = Require(options).Snapshot();
        if (runtime.OpenAICodexClientId is null)
        {
            return ClientRegistrationRequired(
                OpenAICodexProviderId,
                new EnvironmentGameProviderAuthentication(
                    "OPENAI_CODEX_ACCESS_TOKEN",
                    GameCredentialKind.BearerToken));
        }

        var stored = new StoredGameProviderAuthentication(
            OpenAICodexProviderId,
            runtime.CredentialStore,
            new[] { "oauth-openai-codex-browser", "oauth-openai-codex-device-code" },
            (scheme, interaction, cancellationToken) => scheme switch
            {
                "oauth-openai-codex-browser" =>
                    OpenAICodexOAuth.LoginBrowserAsync(runtime, interaction, cancellationToken),
                "oauth-openai-codex-device-code" =>
                    OpenAICodexOAuth.LoginDeviceAsync(runtime, interaction, cancellationToken),
                _ => throw new InvalidOperationException("The requested OAuth login scheme is not supported."),
            },
            (credential, cancellationToken) =>
                OpenAICodexOAuth.RefreshAsync(runtime, credential, cancellationToken),
            runtime.Profile,
            runtime.Clock,
            runtime.RefreshSkew,
            RefreshTimeoutMilliseconds(runtime));
        return new FallbackGameProviderAuthentication(
            stored,
            new EnvironmentGameProviderAuthentication(
                "OPENAI_CODEX_ACCESS_TOKEN",
                GameCredentialKind.BearerToken));
    }

    private static ValueTask<GameCredential> LoginAnthropicAsync(
        OAuthRuntimeSettings runtime,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken) =>
        SecureLoopbackOAuth.LoginAsync(
            new SecureLoopbackOAuthOptions(
                AnthropicAuthorizationEndpoint,
                "localhost",
                "/callback",
                runtime.LoginTimeout,
                (redirectUri, challenge, state) => BuildUri(
                    AnthropicAuthorizationEndpoint,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["code"] = "true",
                        ["client_id"] = runtime.AnthropicClientId!,
                        ["response_type"] = "code",
                        ["redirect_uri"] = redirectUri.AbsoluteUri,
                        ["scope"] = AnthropicScopes,
                        ["code_challenge"] = challenge,
                        ["code_challenge_method"] = "S256",
                        ["state"] = state,
                    }),
                (code, verifier, state, redirectUri, token) => ExchangeAnthropicAsync(
                    runtime,
                    code,
                    verifier,
                    state,
                    redirectUri,
                    token),
                port: 53_692,
                statePlacement: LoopbackStatePlacement.Query),
            interaction,
            cancellationToken);

    private static async ValueTask<GameCredential> ExchangeAnthropicAsync(
        OAuthRuntimeSettings runtime,
        string code,
        string verifier,
        string state,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        using var response = await BoundedOAuthHttp.PostJsonAsync(
            runtime.HttpClient,
            AnthropicTokenEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = runtime.AnthropicClientId!,
                ["code"] = code,
                ["state"] = state,
                ["redirect_uri"] = redirectUri.AbsoluteUri,
                ["code_verifier"] = verifier,
            },
            runtime.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw BoundedOAuthHttp.Failure("Anthropic token exchange", response);
        }

        return BoundedDeviceOAuth.ParseCredential(
            response.Root,
            runtime.Clock,
            TimeSpan.FromHours(1),
            previousRefreshToken: null,
            requireRefreshToken: true);
    }

    private static async ValueTask<GameCredential> RefreshAnthropicAsync(
        OAuthRuntimeSettings runtime,
        GameCredential credential,
        CancellationToken cancellationToken)
    {
        var refreshToken = RequireRefreshToken(credential);
        using var response = await BoundedOAuthHttp.PostJsonAsync(
            runtime.HttpClient,
            AnthropicTokenEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = runtime.AnthropicClientId!,
                ["refresh_token"] = refreshToken,
            },
            runtime.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw BoundedOAuthHttp.Failure("Anthropic token refresh", response);
        }

        return BoundedDeviceOAuth.ParseCredential(
            response.Root,
            runtime.Clock,
            TimeSpan.FromHours(1),
            refreshToken,
            requireRefreshToken: false);
    }

    private static ValueTask<GameCredential> LoginOpenRouterAsync(
        OAuthRuntimeSettings runtime,
        GameAuthInteraction interaction,
        CancellationToken cancellationToken) =>
        SecureLoopbackOAuth.LoginAsync(
            new SecureLoopbackOAuthOptions(
                OpenRouterAuthorizationEndpoint,
                "127.0.0.1",
                "/oauth/callback",
                runtime.LoginTimeout,
                (redirectUri, challenge, _) => BuildUri(
                    OpenRouterAuthorizationEndpoint,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["callback_url"] = redirectUri.AbsoluteUri,
                        ["code_challenge"] = challenge,
                        ["code_challenge_method"] = "S256",
                    }),
                (code, verifier, _, _, token) => ExchangeOpenRouterAsync(
                    runtime,
                    code,
                    verifier,
                    token),
                statePlacement: LoopbackStatePlacement.CallbackPath),
            interaction,
            cancellationToken);

    private static async ValueTask<GameCredential> ExchangeOpenRouterAsync(
        OAuthRuntimeSettings runtime,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using var response = await BoundedOAuthHttp.PostJsonAsync(
            runtime.HttpClient,
            OpenRouterTokenEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["code_challenge_method"] = "S256",
            },
            runtime.RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw BoundedOAuthHttp.Failure("OpenRouter key exchange", response);
        }

        var key = BoundedOAuthHttp.RequiredString(response.Root, "key");
        return new GameCredential(
            GameCredentialKind.OAuth,
            key,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["credential_type"] = "user-controlled-api-key",
            });
    }

    private static DeviceOAuthOptions XaiDeviceOptions(OAuthRuntimeSettings runtime)
    {
        var options = new DeviceOAuthOptions(
            XaiDeviceEndpoint,
            XaiTokenEndpoint,
            runtime.XaiClientId!,
            new[] { "accounts.x.ai", "auth.x.ai" },
            runtime);
        foreach (var scope in XaiScopes.Split(' '))
        {
            options.Scopes.Add(scope);
        }

        options.DeviceParameters["referrer"] = "opengameagent";
        options.DefaultTokenLifetime = TimeSpan.FromHours(1);
        return options;
    }

    private static DeviceOAuthOptions KimiDeviceOptions(OAuthRuntimeSettings runtime) =>
        new(
            KimiDeviceEndpoint,
            KimiTokenEndpoint,
            runtime.KimiForCodingClientId!,
            new[] { "auth.kimi.com" },
            runtime)
        {
            DefaultTokenLifetime = TimeSpan.FromHours(1),
        };

    private static async ValueTask<GameCredential> RefreshKimiWithRetryAsync(
        DeviceOAuthOptions options,
        GameCredential credential,
        CancellationToken cancellationToken)
    {
        var refreshToken = RequireRefreshToken(credential);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await BoundedOAuthHttp.PostFormAsync(
                options.Runtime.HttpClient,
                options.TokenEndpoint,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = options.ClientId,
                    ["refresh_token"] = refreshToken,
                },
                options.Runtime.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccess)
            {
                return BoundedDeviceOAuth.ParseCredential(
                    response.Root,
                    options.Runtime.Clock,
                    options.DefaultTokenLifetime,
                    refreshToken,
                    requireRefreshToken: false);
            }

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                            || (int)response.StatusCode is >= 500 and <= 599;
            if (!retryable || attempt == 2)
            {
                throw BoundedOAuthHttp.Failure("Kimi token refresh", response);
            }

            await BoundedOAuthHttp.WaitAsync(
                options.Runtime.DelayAsync(TimeSpan.FromMilliseconds(250 * (1 << attempt)), cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Kimi token refresh failed.");
    }

    private static IGameProviderAuthentication WithApiKeyFallback(
        IGameProviderAuthentication stored,
        string environmentVariable) =>
        new FallbackGameProviderAuthentication(
            stored,
            ApiKeyEnvironment(environmentVariable));

    private static IGameProviderAuthentication ApiKeyEnvironment(string environmentVariable) =>
        new EnvironmentGameProviderAuthentication(environmentVariable);

    private static IGameProviderAuthentication ClientRegistrationRequired(
        string providerId,
        IGameProviderAuthentication fallback) =>
        new OAuthClientRegistrationRequiredAuthentication(providerId, fallback);

    private static BuiltInGameOAuthOptions Require(BuiltInGameOAuthOptions options) =>
        options ?? throw new ArgumentNullException(nameof(options));

    private static string RequireRefreshToken(GameCredential credential)
    {
        if (credential is null || credential.Kind != GameCredentialKind.OAuth)
        {
            throw new ArgumentException("An OAuth credential is required.", nameof(credential));
        }

        return credential.Metadata.TryGetValue("refresh_token", out var refreshToken)
               && !string.IsNullOrWhiteSpace(refreshToken)
            ? refreshToken
            : throw new InvalidOperationException("The OAuth credential has no refresh token.");
    }

    private static int RefreshTimeoutMilliseconds(OAuthRuntimeSettings runtime) =>
        checked((int)Math.Max(100, Math.Min(300_000, runtime.RequestTimeout.TotalMilliseconds)));

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> fields)
    {
        var query = string.Join("&", fields.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }
}

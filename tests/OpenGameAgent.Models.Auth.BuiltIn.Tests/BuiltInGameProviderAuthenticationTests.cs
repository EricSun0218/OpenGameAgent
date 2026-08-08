using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Models;
using OpenGameAgent.Models.Auth.BuiltIn;
using OpenGameAgent.Models.BuiltIn;
using Xunit;

namespace OpenGameAgent.Models.Auth.BuiltIn.Tests;

public sealed class BuiltInGameProviderAuthenticationTests
{
    [Fact]
    public async Task OpenRouterUsesOneShotLoopbackPathStateAndPkceBeforeStoringTheKey()
    {
        string? exchangeBody = null;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            exchangeBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json(HttpStatusCode.OK, "{\"key\":\"router-key\"}");
        });
        var store = new InMemoryGameCredentialStore();
        var authentication = BuiltInGameProviderAuthentications.CreateOpenRouter(
            Options(handler, store));
        Uri? authorizationUri = null;
        Task<HttpResponseMessage>? callback = null;
        using var callbackClient = new HttpClient();
        var interaction = new GameAuthInteraction
        {
            OpenBrowserAsync = (uri, _) =>
            {
                authorizationUri = uri;
                var fields = ParseQuery(uri.Query);
                var callbackUri = new Uri(fields["callback_url"] + "?code=authorization-code");
                callback = callbackClient.GetAsync(callbackUri);
                return default;
            },
        };

        var credential = await authentication.LoginAsync(
            "oauth-openrouter",
            interaction,
            TestContext.Current.CancellationToken);

        using var callbackResponse = await callback!;
        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        Assert.Equal(GameCredentialKind.OAuth, credential.Kind);
        Assert.Equal("router-key", credential.Secret);
        Assert.NotNull(authorizationUri);
        var authorization = ParseQuery(authorizationUri!.Query);
        var callbackUrl = new Uri(authorization["callback_url"]);
        Assert.Equal("127.0.0.1", callbackUrl.Host);
        Assert.Matches("^/oauth/callback/[A-Za-z0-9_-]+$", callbackUrl.AbsolutePath);
        Assert.Equal("S256", authorization["code_challenge_method"]);

        using var exchange = JsonDocument.Parse(exchangeBody!);
        var verifier = exchange.RootElement.GetProperty("code_verifier").GetString()!;
        Assert.Equal(
            authorization["code_challenge"],
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        var stored = await store.GetAsync(
            new GameCredentialKey(BuiltInGameProviderAuthentications.OpenRouterProviderId),
            TestContext.Current.CancellationToken);
        Assert.Equal("router-key", stored?.Secret);
    }

    [Fact]
    public async Task LoopbackRejectsWrongHostAndStateThenAcceptsTheExactCallback()
    {
        var callbacks = new List<HttpStatusCode>();
        Task? callbackSequence = null;
        Uri? redirect = null;
        var options = new SecureLoopbackOAuthOptions(
            new Uri("https://authorization.example/login"),
            "127.0.0.1",
            "/callback",
            TimeSpan.FromSeconds(20),
            (redirectUri, challenge, state) =>
            {
                redirect = redirectUri;
                return BuildUri(
                    new Uri("https://authorization.example/login"),
                    new Dictionary<string, string>
                    {
                        ["redirect_uri"] = redirectUri.AbsoluteUri,
                        ["code_challenge"] = challenge,
                        ["state"] = state,
                    });
            },
            (code, _, _, _, _) => new ValueTask<GameCredential>(
                new GameCredential(GameCredentialKind.OAuth, "access-" + code)),
            statePlacement: LoopbackStatePlacement.Query);
        var interaction = new GameAuthInteraction
        {
            OpenBrowserAsync = (authorization, _) =>
            {
                var state = ParseQuery(authorization.Query)["state"];
                callbackSequence = Task.Run(async () =>
                {
                    callbacks.Add(await SendRawCallbackAsync(redirect!, "evil.example", "?code=ignored&state=" + state));
                    using var client = new HttpClient();
                    using var wrongState = await client.GetAsync(new Uri(redirect + "?code=ignored&state=wrong"));
                    callbacks.Add(wrongState.StatusCode);
                    using var valid = await client.GetAsync(new Uri(
                        redirect + "?code=accepted&state=" + Uri.EscapeDataString(state)));
                    callbacks.Add(valid.StatusCode);
                });
                return default;
            },
        };

        var credential = await SecureLoopbackOAuth.LoginAsync(
            options,
            interaction,
            TestContext.Current.CancellationToken);
        await callbackSequence!;

        Assert.Equal("access-accepted", credential.Secret);
        Assert.Equal(
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK },
            callbacks);
    }

    [Fact]
    public async Task XaiDeviceFlowHonorsPendingSlowDownAndVerificationHostBounds()
    {
        var requests = new List<string>();
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.OK, """
                {"device_code":"device","user_code":"ABCD","verification_uri":"https://accounts.x.ai/oauth2/device","expires_in":900,"interval":5}
                """),
            Json(HttpStatusCode.BadRequest, "{\"error\":\"authorization_pending\"}"),
            Json(HttpStatusCode.BadRequest, "{\"error\":\"slow_down\",\"interval\":9}"),
            Json(HttpStatusCode.OK, "{\"access_token\":\"xai-access\",\"refresh_token\":\"xai-refresh\",\"expires_in\":3600}"),
        });
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
        });
        var delays = new List<TimeSpan>();
        var store = new InMemoryGameCredentialStore();
        var options = Options(handler, store);
        options.DelayAsync = (delay, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(delay);
            return Task.CompletedTask;
        };
        var authentication = BuiltInGameProviderAuthentications.CreateXai(options);
        Uri? opened = null;
        string? notice = null;

        var credential = await authentication.LoginAsync(
            "oauth-xai-device-code",
            new GameAuthInteraction
            {
                NotifyAsync = (message, _) =>
                {
                    notice = message;
                    return default;
                },
                OpenBrowserAsync = (uri, _) =>
                {
                    opened = uri;
                    return default;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("xai-access", credential.Secret);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9) }, delays);
        Assert.Contains("ABCD", notice, StringComparison.Ordinal);
        Assert.Equal("accounts.x.ai", opened?.Host);
        Assert.Contains("referrer=opengameagent", requests[0], StringComparison.Ordinal);
        Assert.Equal(4, requests.Count);
    }

    [Fact]
    public async Task DeviceFlowRejectsAnUntrustedVerificationHostBeforePolling()
    {
        var calls = 0;
        var handler = new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK, """
                {"device_code":"device","user_code":"ABCD","verification_uri":"https://attacker.example/device","expires_in":900,"interval":5}
                """));
        });
        var authentication = BuiltInGameProviderAuthentications.CreateXai(
            Options(handler, new InMemoryGameCredentialStore()));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await authentication.LoginAsync(
                "oauth-xai-device-code",
                new GameAuthInteraction(),
                TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task KimiRefreshRetriesOnlyTransientFailuresAndPreservesTheRotatedCredential()
    {
        var now = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);
        var store = new InMemoryGameCredentialStore();
        await store.SetAsync(
            new GameCredentialKey(BuiltInGameProviderAuthentications.KimiForCodingProviderId),
            new GameCredential(
                GameCredentialKind.OAuth,
                "old-access",
                now.AddMinutes(-1),
                new Dictionary<string, string> { ["refresh_token"] = "old-refresh" }),
            TestContext.Current.CancellationToken);
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.InternalServerError, "{\"error\":\"server_error\"}"),
            Json(HttpStatusCode.TooManyRequests, "{\"error\":\"slow_down\"}"),
            Json(HttpStatusCode.OK, "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}"),
        });
        var handler = new DelegateHandler((_, _) => Task.FromResult(responses.Dequeue()));
        var delays = new List<TimeSpan>();
        var options = Options(handler, store);
        options.Clock = () => now;
        options.DelayAsync = (delay, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(delay);
            return Task.CompletedTask;
        };
        var authentication = BuiltInGameProviderAuthentications.CreateKimiForCoding(options);

        var resolution = await authentication.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("new-access", resolution?.Credential?.Secret);
        Assert.Equal("new-refresh", resolution?.Credential?.Metadata["refresh_token"]);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500) }, delays);
    }

    [Fact]
    public async Task CancelingDeviceLoginPreventsALateCredentialCommit()
    {
        var tokenRequestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTokenResponse = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var handler = new DelegateHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """
                    {"device_code":"device","user_code":"ABCD","verification_uri":"https://accounts.x.ai/oauth2/device","expires_in":900,"interval":1}
                    """));
            }

            tokenRequestStarted.TrySetResult(true);
            return releaseTokenResponse.Task;
        });
        var store = new InMemoryGameCredentialStore();
        var options = Options(handler, store);
        options.DelayAsync = (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var authentication = BuiltInGameProviderAuthentications.CreateXai(options);
        using var cancellation = new CancellationTokenSource();
        var login = authentication.LoginAsync(
            "oauth-xai-device-code",
            new GameAuthInteraction(),
            cancellation.Token).AsTask();
        await tokenRequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        releaseTokenResponse.TrySetResult(Json(
            HttpStatusCode.OK,
            "{\"access_token\":\"late-access\",\"refresh_token\":\"late-refresh\",\"expires_in\":3600}"));
        await Task.Yield();

        var stored = await store.GetAsync(
            new GameCredentialKey(BuiltInGameProviderAuthentications.XaiProviderId),
            TestContext.Current.CancellationToken);
        Assert.Null(stored);
    }

    [Fact]
    public async Task AnthropicUsesJsonExchangeAndRefreshWhileRetainingApiKeyFallback()
    {
        var now = new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);
        var bodies = new List<string>();
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}"),
            Json(HttpStatusCode.OK, "{\"access_token\":\"next\",\"refresh_token\":\"next-refresh\",\"expires_in\":3600}"),
        });
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
        });
        var store = new InMemoryGameCredentialStore();
        var options = Options(handler, store);
        options.Clock = () => now;
        var authentication = BuiltInGameProviderAuthentications.CreateAnthropic(options);
        Uri? authorization = null;
        var credential = await authentication.LoginAsync(
            "oauth-anthropic-subscription",
            new GameAuthInteraction
            {
                OpenBrowserAsync = (uri, _) =>
                {
                    authorization = uri;
                    return default;
                },
                PromptAsync = (_, _, _) =>
                {
                    var query = ParseQuery(authorization!.Query);
                    return new ValueTask<string>(
                        "http://localhost:53692/callback?code=authorization-code&state="
                        + Uri.EscapeDataString(query["state"]));
                },
            },
            TestContext.Current.CancellationToken);
        Assert.Equal("access", credential.Secret);

        await store.SetAsync(
            new GameCredentialKey(BuiltInGameProviderAuthentications.AnthropicProviderId),
            new GameCredential(
                GameCredentialKind.OAuth,
                credential.Secret,
                now.AddMinutes(-1),
                credential.Metadata),
            TestContext.Current.CancellationToken);
        var refreshed = await authentication.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("next", refreshed?.Credential?.Secret);
        Assert.Equal("oauth-2025-04-20", refreshed?.Headers["anthropic-beta"]);
        using var exchange = JsonDocument.Parse(bodies[0]);
        Assert.Equal("authorization_code", exchange.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal(
            ParseQuery(authorization!.Query)["state"],
            exchange.RootElement.GetProperty("state").GetString());
        using var refresh = JsonDocument.Parse(bodies[1]);
        Assert.Equal("refresh_token", refresh.RootElement.GetProperty("grant_type").GetString());
    }

    [Fact]
    public async Task OpenAICodexDeviceFlowPollsImmediatelyAndStoresTheAccountIdentifier()
    {
        var accountToken = JwtWithAccountId("account-123");
        var requests = new List<(Uri Uri, string Body)>();
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.OK, """
                {"device_auth_id":"device-auth","user_code":"ABCD-EFGH","interval":"1"}
                """),
            Json(HttpStatusCode.BadRequest, """
                {"error":{"code":"deviceauth_authorization_pending"}}
                """),
            Json(HttpStatusCode.OK, """
                {"authorization_code":"authorization-code","code_verifier":"device-verifier"}
                """),
            Json(HttpStatusCode.OK, $$"""
                {"access_token":"{{accountToken}}","refresh_token":"refresh-token","expires_in":3600}
                """),
        });
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return responses.Dequeue();
        });
        var delays = new List<TimeSpan>();
        var store = new InMemoryGameCredentialStore();
        var options = Options(handler, store);
        options.DelayAsync = (delay, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(delay);
            return Task.CompletedTask;
        };
        var authentication = BuiltInGameProviderAuthentications.CreateOpenAICodex(options);
        Uri? opened = null;

        var credential = await authentication.LoginAsync(
            "oauth-openai-codex-device-code",
            new GameAuthInteraction
            {
                OpenBrowserAsync = (uri, _) =>
                {
                    opened = uri;
                    return default;
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(accountToken, credential.Secret);
        Assert.Equal("account-123", credential.Metadata["openai-codex.account-id"]);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        Assert.Equal("https://auth.openai.com/codex/device", opened?.AbsoluteUri);
        Assert.Equal(4, requests.Count);
        Assert.Equal("/api/accounts/deviceauth/usercode", requests[0].Uri.AbsolutePath);
        Assert.Equal("/api/accounts/deviceauth/token", requests[1].Uri.AbsolutePath);
        Assert.Equal("/api/accounts/deviceauth/token", requests[2].Uri.AbsolutePath);
        Assert.Equal("/oauth/token", requests[3].Uri.AbsolutePath);
        var exchange = ParseQuery(requests[3].Body);
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("authorization-code", exchange["code"]);
        Assert.Equal("device-verifier", exchange["code_verifier"]);
        Assert.Equal("https://auth.openai.com/deviceauth/callback", exchange["redirect_uri"]);
        var stored = await store.GetAsync(
            new GameCredentialKey(BuiltInGameProviderAuthentications.OpenAICodexProviderId),
            TestContext.Current.CancellationToken);
        Assert.Equal("account-123", stored?.Metadata["openai-codex.account-id"]);
    }

    [Fact]
    public void RegistrationAddsOnlySupportedDirectoryProvidersAndPreservesExplicitOverrides()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, "{}"))));
        var runtimeOptions = new BuiltInGameModelRuntimeOptions(client)
        {
            GetEnvironmentVariable = _ => null,
        };
        var explicitAuthentication = new StaticGameProviderAuthentication(
            credential: new GameCredential(GameCredentialKind.ApiKey, "explicit-key"));
        runtimeOptions.Authentications.Add(
            BuiltInGameProviderAuthentications.AnthropicProviderId,
            explicitAuthentication);
        var authenticationOptions = new BuiltInGameOAuthOptions(
            client,
            new InMemoryGameCredentialStore());

        var registered = runtimeOptions.RegisterBuiltInOAuth(authenticationOptions);

        Assert.Equal(3, registered);
        Assert.Same(
            explicitAuthentication,
            runtimeOptions.Authentications[BuiltInGameProviderAuthentications.AnthropicProviderId]);
        Assert.Contains(BuiltInGameProviderAuthentications.OpenRouterProviderId, runtimeOptions.Authentications.Keys);
        Assert.Contains(BuiltInGameProviderAuthentications.XaiProviderId, runtimeOptions.Authentications.Keys);
        Assert.Contains(BuiltInGameProviderAuthentications.KimiForCodingProviderId, runtimeOptions.Authentications.Keys);
        Assert.DoesNotContain(BuiltInGameProviderAuthentications.OpenAICodexProviderId, runtimeOptions.Authentications.Keys);
        Assert.Equal(5, BuiltInGameOAuthRegistration.SupportedProviderIds.Count);

        var runtime = new BuiltInGameModelRuntime(runtimeOptions);
        foreach (var pair in runtimeOptions.Authentications)
        {
            Assert.Same(pair.Value, runtime.Catalog.GetProvider(pair.Key)?.Authentication);
        }
    }

    [Fact]
    public async Task RegisteredCodexAuthenticationCarriesStoredAccountMetadataToTheWire()
    {
        var token = JwtWithAccountId("account-through-runtime");
        var store = new InMemoryGameCredentialStore();
        await store.SetAsync(
            new GameCredentialKey(BuiltInGameProviderAuthentications.OpenAICodexProviderId),
            new GameCredential(
                GameCredentialKind.OAuth,
                token,
                DateTimeOffset.UtcNow.AddHours(1),
                new Dictionary<string, string>
                {
                    ["refresh_token"] = "refresh-token",
                    ["openai-codex.account-id"] = "account-through-runtime",
                }),
            TestContext.Current.CancellationToken);
        var handler = new CodexRecordingHandler();
        using var client = new HttpClient(handler);
        var runtimeOptions = new BuiltInGameModelRuntimeOptions(client)
        {
            Directory = CodexDirectory(),
            GetEnvironmentVariable = _ => null,
        };

        Assert.Equal(1, runtimeOptions.RegisterBuiltInOAuth(new BuiltInGameOAuthOptions(client, store)
        {
            OpenAICodexClientId = "test-codex-client",
        }));
        var runtime = new BuiltInGameModelRuntime(runtimeOptions);
        var terminal = await runtime.CompleteAsync(
            BuiltInGameProviderAuthentications.OpenAICodexProviderId,
            new ModelRequest(
                "gpt-codex",
                "system",
                Array.Empty<AgentMessage>(),
                Array.Empty<ToolDefinition>(),
                new ModelParameters(),
                "session",
                "run",
                1),
            TestContext.Current.CancellationToken);

        Assert.Null(terminal.ErrorMessage);
        Assert.Equal("Bearer " + token, handler.Header("Authorization"));
        Assert.Equal("account-through-runtime", handler.Header("chatgpt-account-id"));
        Assert.Equal("opengameagent", handler.Header("originator"));
    }

    [Fact]
    public async Task MissingProviderClientIdsExposeNoOAuthSchemesAndNeverReachTheNetwork()
    {
        var calls = 0;
        var handler = new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        var options = new BuiltInGameOAuthOptions(
            new HttpClient(handler),
            new InMemoryGameCredentialStore());
        var authentications = new[]
        {
            BuiltInGameProviderAuthentications.CreateAnthropic(options),
            BuiltInGameProviderAuthentications.CreateXai(options),
            BuiltInGameProviderAuthentications.CreateKimiForCoding(options),
            BuiltInGameProviderAuthentications.CreateOpenAICodex(options),
        };

        foreach (var authentication in authentications)
        {
            Assert.Empty(authentication.Schemes);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await authentication.LoginAsync(
                    "oauth-unconfigured",
                    new GameAuthInteraction(),
                    TestContext.Current.CancellationToken));
            Assert.Contains("OAuth client ID", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, calls);
    }

    private static BuiltInGameOAuthOptions Options(
        HttpMessageHandler handler,
        IGameCredentialStore store) =>
        new(new HttpClient(handler), store)
        {
            LoginTimeout = TimeSpan.FromSeconds(20),
            RequestTimeout = TimeSpan.FromSeconds(5),
            AnthropicClientId = "test-anthropic-client",
            XaiClientId = "test-xai-client",
            KimiForCodingClientId = "test-kimi-client",
            OpenAICodexClientId = "test-codex-client",
        };

    private static async Task<HttpStatusCode> SendRawCallbackAsync(
        Uri redirect,
        string host,
        string query)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, redirect.Port);
        var request = Encoding.ASCII.GetBytes(
            $"GET {redirect.AbsolutePath}{query} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        await client.GetStream().WriteAsync(request);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync();
        return (HttpStatusCode)int.Parse(statusLine!.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> fields) =>
        new UriBuilder(endpoint)
        {
            Query = string.Join("&", fields.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
        }.Uri;

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part.Length == 2 ? part[1] : string.Empty),
                StringComparer.Ordinal);

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string JwtWithAccountId(string accountId)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, string>
            {
                ["chatgpt_account_id"] = accountId,
            },
        }));
        return header + "." + payload + ".signature";
    }

    private static GameModelDirectorySnapshot CodexDirectory() => GameModelDirectory.ParseJson("""
        {
          "version": "test",
          "generatedAt": "2026-08-08T00:00:00Z",
          "providers": [{
            "id": "openai-codex",
            "name": "OpenAI Codex",
            "endpoint": "https://chatgpt.com/backend-api/codex",
            "models": [{
              "id": "gpt-codex",
              "name": "GPT Codex",
              "api": "openai-codex-responses",
              "contextWindow": 8192,
              "maximumOutput": 512,
              "input": ["text"],
              "output": ["text", "tools"]
            }]
          }]
        }
        """);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _send(request, cancellationToken);
    }

    private sealed class CodexRecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers)
            {
                _headers[header.Key] = string.Join(",", header.Value);
            }

            if (request.Content is not null)
            {
                _ = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"model\":\"gpt-codex\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0}}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }
}

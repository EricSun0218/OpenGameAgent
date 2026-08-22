using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenGameAgent.Attachments;
using OpenGameAgent.Client;
using OpenGameAgent.Kernel;
using OpenGameAgent.Persistence;
using OpenGameAgent.Runtime.Protocol;
using Xunit;

namespace OpenGameAgent.Server.Tests;

public sealed class ServerTests
{
    [Fact]
    public void ClientAndMiddlewareRejectInvalidAuthenticationHeaders()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler("{}"));
        var options = new ServerGameAgentClientOptions(httpClient, new Uri("https://agent.test/"))
        {
            ApiKeyHeader = "Bad:Name",
        };
        Assert.Throws<ArgumentException>(() => new ServerGameAgentClient(options));

        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        Assert.Throws<ArgumentException>(() => app.UseOpenGameAgentApiKey("secret", "Bad:Name"));
        Assert.Throws<ArgumentException>(() => app.UseOpenGameAgentApiKey("secret\0value"));
        Assert.Throws<ArgumentException>(() => app.UseOpenGameAgentApiKey("secret", scheme: new string('s', 257)));
    }

    [Fact]
    public void ClientRequiresTlsForRemoteServersUnlessExplicitlyOverridden()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler("{}"));
        Assert.Throws<ArgumentException>(() => new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            new Uri("http://agent.test/"))));

        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            new Uri("http://agent.test/"))
        {
            AllowInsecureHttp = true,
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void MiddlewareAllowsNullSchemeAsRawCredential()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        var configured = app.UseOpenGameAgentApiKey("secret", scheme: null!);

        Assert.Same(app, configured);
    }

    [Fact]
    public async Task JsonEndpointRunsSharedRuntime()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        using var content = new StringContent(RequestJson("json-input"), Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/v1/run", content, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Completed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("QuickResponse", document.RootElement.GetProperty("route").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("sessionRevision").GetInt64());
        var messages = document.RootElement.GetProperty("agent").GetProperty("newMessages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("hello", messages[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ServerRunPreservesResourceReferencesFromTheEngineWireFormat()
    {
        var provider = new ResourceCaptureProvider();
        await using var app = await CreateAppAsync(
            new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")));
        using var client = app.GetTestClient();
        var input = new GameInput(
            "session",
            "resource-actor",
            "observe",
            "{}",
            new GameMoment("world", 1),
            "resource-input",
            content: new AgentContent[]
            {
                new ResourceContent("game://capture/frame", "image/png", "frame"),
            });
        using var content = new StringContent(
            GameAgentWire.SerializeInput(input),
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/v1/run", content, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var resource = Assert.Single(Assert.Single(provider.Requests).Messages.SelectMany(message => message.Content).OfType<ResourceContent>());
        Assert.Equal("game://capture/frame", resource.Uri);
        Assert.Equal("image/png", resource.MediaType);
    }

    [Fact]
    public async Task TrustedModelRoutesCanSwitchProvidersWithinOneSession()
    {
        var local = new RoutedProvider("local-answer", "local-response");
        var cloud = new RoutedProvider("cloud-answer", "cloud-response");
        var router = new TrustedGameAgentServerModelRouter(
            new[]
            {
                new GameAgentServerModelRoute("local", new[]
                {
                    new GameAgentServerModelTarget("local-provider", "local-model", local, "test-api"),
                }),
                new GameAgentServerModelRoute("cloud", new[]
                {
                    new GameAgentServerModelTarget("cloud-provider", "cloud-model", cloud, "test-api"),
                }),
            },
            "local",
            (input, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<string>(input.Type == "complex" ? "cloud" : "local");
            });
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            router.DefaultProvider,
            router.DefaultModel)
        {
            ModelSelector = router.SelectAsync,
        });

        var first = await runtime.RunAsync(new GameInput(
            "routing-session",
            "actor",
            "chat",
            "{\"endpoint\":\"https://attacker.invalid\",\"apiKey\":\"attacker-key\"}",
            new GameMoment("world", 1),
            "route-local"), TestContext.Current.CancellationToken);
        var second = await runtime.RunAsync(new GameInput(
            "routing-session",
            "actor",
            "complex",
            "{}",
            new GameMoment("world", 2),
            "route-cloud"), TestContext.Current.CancellationToken);

        Assert.Equal("local-model", Assert.Single(local.Requests).Model);
        Assert.Equal("cloud-model", Assert.Single(cloud.Requests).Model);
        var firstAssistant = Assert.Single(first.AgentResult!.NewMessages, message => message.Role == AgentRole.Assistant);
        var secondAssistant = Assert.Single(second.AgentResult!.NewMessages, message => message.Role == AgentRole.Assistant);
        Assert.Equal("local-provider", firstAssistant.Provider);
        Assert.Equal("local-model", firstAssistant.ResponseModel);
        Assert.Equal("cloud-provider", secondAssistant.Provider);
        Assert.Equal("cloud-model", secondAssistant.ResponseModel);
    }

    [Fact]
    public async Task TrustedFallbackReportsTheProviderModelAndResponseThatActuallyCompleted()
    {
        var failing = new TransientFailureProvider();
        var fallback = new RoutedProvider("fallback-answer", "fallback-response");
        var router = new TrustedGameAgentServerModelRouter(
            new[]
            {
                new GameAgentServerModelRoute("balanced", new[]
                {
                    new GameAgentServerModelTarget("primary-provider", "primary-model", failing, "primary-api"),
                    new GameAgentServerModelTarget("fallback-provider", "fallback-model", fallback, "fallback-api"),
                }),
            },
            "balanced");
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            router.DefaultProvider,
            router.DefaultModel)
        {
            ModelSelector = router.SelectAsync,
        });

        var result = await runtime.RunAsync(new GameInput(
            "fallback-session",
            "actor",
            "chat",
            "{}",
            new GameMoment("world", 1),
            "fallback-input"), TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(GameAgentWire.SerializeResult(result));
        var messages = document.RootElement.GetProperty("agent").GetProperty("newMessages");
        var assistant = messages.EnumerateArray().Single(message => message.GetProperty("role").GetString() == "Assistant");

        Assert.Equal(1, failing.Calls);
        Assert.Equal("fallback-model", Assert.Single(fallback.Requests).Model);
        Assert.Equal("fallback-provider", assistant.GetProperty("provider").GetString());
        Assert.Equal("fallback-api", assistant.GetProperty("api").GetString());
        Assert.Equal("fallback-model", assistant.GetProperty("responseModel").GetString());
        Assert.Equal("fallback-response", assistant.GetProperty("responseId").GetString());
    }

    [Fact]
    public async Task TrustedModelRouterRejectsPolicyNamesOutsideTheAllowlistBeforeProviderUse()
    {
        var provider = new RoutedProvider("unused", "unused-response");
        var router = new TrustedGameAgentServerModelRouter(
            new[]
            {
                new GameAgentServerModelRoute("allowed", new[]
                {
                    new GameAgentServerModelTarget("allowed-provider", "allowed-model", provider),
                }),
            },
            "allowed",
            (_, _) => new ValueTask<string>("https://attacker.invalid/v1"));
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(
            router.DefaultProvider,
            router.DefaultModel)
        {
            ModelSelector = router.SelectAsync,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunAsync(new GameInput(
            "unknown-route-session",
            "actor",
            "chat",
            "{\"apiKey\":\"attacker-key\"}",
            new GameMoment("world", 1),
            "unknown-route-input"), TestContext.Current.CancellationToken));

        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task StockModelRoutingUsesOnlyConfiguredNamedTargetsAndInputTypePolicy()
    {
        const string serverSecret = "server-only-secret";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenGameAgent:DefaultModelRoute"] = "local",
                ["OpenGameAgent:ModelRoutes:local:ProviderId"] = "local-provider",
                ["OpenGameAgent:ModelRoutes:local:Endpoint"] = "http://127.0.0.1:11434/v1/chat/completions",
                ["OpenGameAgent:ModelRoutes:local:Model"] = "local-model",
                ["OpenGameAgent:ModelRoutes:cloud:ProviderId"] = "cloud-provider",
                ["OpenGameAgent:ModelRoutes:cloud:Endpoint"] = "https://cloud.invalid/v1/chat/completions",
                ["OpenGameAgent:ModelRoutes:cloud:Model"] = "cloud-model",
                ["OpenGameAgent:ModelRoutes:cloud:ApiKey"] = serverSecret,
                ["OpenGameAgent:InputModelRoutes:complex"] = "cloud",
            })
            .Build();
        var routing = StockGameAgentModelRouting.Create(
            configuration,
            new StaticHttpClientFactory(new StaticResponseHandler(string.Empty)));
        var hostileInput = new GameInput(
            "configured-route-session",
            "actor",
            "complex",
            "{\"endpoint\":\"https://attacker.invalid\",\"apiKey\":\"attacker-key\"}",
            new GameMoment("world", 1),
            "configured-route-input",
            new Dictionary<string, string>
            {
                ["modelRoute"] = "https://attacker.invalid/v1",
            });

        var selection = await routing.SelectAsync(hostileInput, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "cloud", "local" }, routing.RouteNames);
        Assert.Equal("cloud-model", selection!.Model);
        Assert.NotNull(selection.Provider);
        Assert.DoesNotContain(serverSecret, GameAgentWire.SerializeInput(hostileInput), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingEndpointEmitsAgentEventsAndTerminalResult()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        using var content = new StringContent(RequestJson("stream-input"), Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/v1/run/stream", content, TestContext.Current.CancellationToken);
        var stream = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("event: agent", stream, StringComparison.Ordinal);
        Assert.Contains("TextDelta", stream, StringComparison.Ordinal);
        Assert.Contains("event: result", stream, StringComparison.Ordinal);
        Assert.Contains("Completed", stream, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityAndHealthEndpointsAreAvailableWithoutRunningModel()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();

        var health = await client.GetStringAsync("/healthz", TestContext.Current.CancellationToken);
        var capabilities = await client.GetStringAsync("/v1/capabilities", TestContext.Current.CancellationToken);

        Assert.Contains("healthy", health, StringComparison.Ordinal);
        Assert.Contains("in-process", capabilities, StringComparison.Ordinal);
        Assert.Contains("server", capabilities, StringComparison.Ordinal);
        Assert.Contains("session-ledger", capabilities, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidInputsReturnBadRequestBeforeStartingJsonOrSseRuns()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();

        foreach (var path in new[] { "/v1/run", "/v1/run/stream" })
        {
            using var content = new StringContent(
                "{\"sessionId\":\"\",\"actorId\":\"actor\",\"type\":\"chat\",\"payload\":{}}",
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("invalid_request", body, StringComparison.Ordinal);
            Assert.DoesNotContain("event: agent", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ServerRejectsAmbiguousTopLevelAndNestedRequestProperties()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        var requests = new[]
        {
            "{\"sessionId\":\"first\",\"SessionId\":\"second\",\"actorId\":\"actor\",\"type\":\"chat\",\"payload\":{}}",
            "{\"sessionId\":\"session\",\"actorId\":\"actor\",\"type\":\"chat\",\"payload\":{\"value\":1,\"value\":2}}",
        };

        foreach (var request in requests)
        {
            using var content = new StringContent(request, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/v1/run", content, TestContext.Current.CancellationToken);

            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public void WireParserRejectsAmbiguousPropertiesWithoutTreatingGameKeysAsCaseInsensitive()
    {
        Assert.Throws<ArgumentException>(() => GameAgentWire.ParseInput(
            "{\"sessionId\":\"first\",\"SessionId\":\"second\",\"actorId\":\"actor\",\"type\":\"chat\",\"payload\":{}}"));
        var input = GameAgentWire.ParseInput(
            "{\"sessionId\":\"session\",\"actorId\":\"actor\",\"type\":\"chat\",\"payload\":{\"HP\":1,\"hp\":2}}");

        Assert.Contains("\"HP\":1", input.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"hp\":2", input.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WireRoundTripPreservesAbsentCalendarAndStreamsToolIdentity()
    {
        var input = new GameInput(
            "session",
            "actor",
            "event",
            "{}",
            new GameMoment("world", 1),
            content: new AgentContent[]
            {
                new ResourceContent("https://assets.example.test/frame.png", "image/png", "frame"),
            });
        var roundTrip = GameAgentWire.ParseInput(GameAgentWire.SerializeInput(input));
        Assert.Null(roundTrip.Moment.CalendarJson);
        var roundTripResource = Assert.Single(roundTrip.Content.OfType<ResourceContent>());
        Assert.Equal("https://assets.example.test/frame.png", roundTripResource.Uri);
        Assert.Equal("image/png", roundTripResource.MediaType);

        string? json = null;
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new ToolIdentityProvider(), "test")
        {
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                new AgentTool(
                    new ToolDefinition("move", "move", "{\"type\":\"object\"}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(Array.Empty<AgentContent>()))),
            }),
        });
        _ = await runtime.RunAsync(
            input,
            (_, agentEvent, _) =>
            {
                if (agentEvent.ModelEvent?.Kind == ModelStreamEventKind.ToolCallDelta)
                {
                    json = GameAgentWire.SerializeEvent(agentEvent);
                }

                return default;
            },
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.IsType<string>(json));
        var projected = document.RootElement.GetProperty("modelEvent");
        Assert.Equal("call-2", projected.GetProperty("toolCallId").GetString());
        Assert.Equal("move", projected.GetProperty("toolName").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("message").ValueKind);
    }

    [Fact]
    public async Task RuntimeLimitViolationsReturnBadRequestForJsonAndSse()
    {
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new StreamingProvider(), "test")
        {
            Limits = new GameRuntimeLimits { MaxInputJsonCharacters = 8 },
        });
        await using var app = await CreateAppAsync(runtime);
        using var client = app.GetTestClient();

        foreach (var path in new[] { "/v1/run", "/v1/run/stream" })
        {
            using var content = new StringContent(RequestJson("oversized"), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
            Assert.Contains("invalid_request", body, StringComparison.Ordinal);
            Assert.DoesNotContain("event: agent", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ServerRejectsOversizedBodiesBeforeEndpointParsing()
    {
        await using var app = await CreateAppAsync(maximumRequestBodyBytes: 128);
        using var client = app.GetTestClient();
        var oversized = "{\"padding\":\"" + new string('x', 256) + "\"}";

        foreach (var path in new[]
                 {
                     "/v1/run",
                     "/v1/run/stream",
                     "/v1/control/steer",
                     "/v1/control/abort",
                     "/v1/usage",
                 })
        {
            using var content = new StringContent(oversized, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Contains("request_too_large", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ServerRejectsNonJsonRequestBodies()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        using var content = new StringContent(RequestJson("plain"), Encoding.UTF8, "text/plain");

        using var response = await client.PostAsync("/v1/run", content, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("unsupported_media_type", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineCompatibleClientConsumesServerSse()
    {
        await using var app = await CreateAppAsync();
        using var httpClient = app.GetTestClient();
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            httpClient.BaseAddress ?? new Uri("http://localhost/")));
        var events = new List<RemoteGameAgentEvent>();
        var input = new GameInput("session", "remote-actor", "chat", "{\"value\":2.5}", new GameMoment("world", 3), "remote-input");

        var result = await client.StreamAsync(
            input,
            (agentEvent, _) =>
            {
                events.Add(agentEvent);
                return default;
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(events, item => item.Name == "agent" && item.Json.Contains("TextDelta", StringComparison.Ordinal));
        Assert.Equal("result", events.Last().Name);
    }

    [Fact]
    public async Task EngineClientCanSteerAnActiveServerActor()
    {
        var provider = new BlockingServerProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["autonomous"] = GameRouteDecision.Agent("typed"),
            }),
        });
        await using var app = await CreateAppAsync(runtime);
        using var httpClient = app.GetTestClient();
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            httpClient.BaseAddress ?? new Uri("http://localhost/")));
        var input = new GameInput(
            "session",
            "remote-steered",
            "autonomous",
            "{}",
            new GameMoment("world", 3),
            "remote-steer-input");

        var run = client.StreamAsync(input, (_, _) => default, TestContext.Current.CancellationToken);
        await provider.FirstRequestStarted.Task;
        var accepted = await client.SteerAsync(
            new GameSessionKey("session", "remote-steered"),
            "{\"threat\":true}",
            TestContext.Current.CancellationToken);
        provider.ReleaseFirstResponse.SetResult();
        var result = await run;

        Assert.True(accepted);
        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(
            provider.Requests.ElementAt(1).Messages,
            message => message.Content.OfType<JsonContent>().Any(content => content.Json.Contains("\"threat\":true", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EngineClientCanAbortAnActiveServerActor()
    {
        var provider = new BlockingServerProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["autonomous"] = GameRouteDecision.Agent("typed"),
            }),
        });
        await using var app = await CreateAppAsync(runtime);
        using var httpClient = app.GetTestClient();
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            httpClient.BaseAddress ?? new Uri("http://localhost/")));
        var key = new GameSessionKey("session", "remote-aborted");
        var input = new GameInput(
            key.SessionId,
            key.ActorId,
            "autonomous",
            "{}",
            new GameMoment("world", 3),
            "remote-abort-input");

        var run = client.StreamAsync(input, (_, _) => default, TestContext.Current.CancellationToken);
        await provider.FirstRequestStarted.Task;
        Assert.True(await client.AbortAsync(key, TestContext.Current.CancellationToken));
        var result = await run;

        Assert.False(result.Succeeded);
        using var document = JsonDocument.Parse(result.Json);
        Assert.Equal("Aborted", document.RootElement.GetProperty("agent").GetProperty("status").GetString());
        Assert.False(await client.AbortAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OptionalApiKeyProtectsRunEndpointsButNotHealth()
    {
        await using var app = await CreateAppAsync("secret");
        using var client = app.GetTestClient();

        using var deniedContent = new StringContent(RequestJson("denied"), Encoding.UTF8, "application/json");
        using var denied = await client.PostAsync("/v1/run", deniedContent, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);

        using var deniedControlContent = new StringContent(
            "{\"sessionId\":\"session\",\"actorId\":\"actor\",\"payload\":{}}",
            Encoding.UTF8,
            "application/json");
        using var deniedControl = await client.PostAsync(
            "/v1/control/steer",
            deniedControlContent,
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, deniedControl.StatusCode);

        using var deniedUsageContent = new StringContent(
            ControlJson(new GameSessionKey("session", "actor")),
            Encoding.UTF8,
            "application/json");
        using var deniedUsage = await client.PostAsync(
            "/v1/usage",
            deniedUsageContent,
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, deniedUsage.StatusCode);

        using var allowedContent = new StringContent(RequestJson("allowed"), Encoding.UTF8, "application/json");
        using var allowedRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/run") { Content = allowedContent };
        allowedRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
        using var allowed = await client.SendAsync(allowedRequest, TestContext.Current.CancellationToken);
        allowed.EnsureSuccessStatusCode();

        using var health = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        health.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task OwnerAuthorizationRejectsAnonymousAndCrossOwnerRunsBeforeRuntimeStateIsTouched()
    {
        var provider = new ResourceCaptureProvider();
        var sessionStore = new CountingGameSessionStore();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            SessionStore = sessionStore,
        });
        var authorizer = new TestOwnerAuthorizer((subject, key, _) =>
            subject == "owner-a" && key == new GameSessionKey("session", "actor"));
        await using var app = await CreateAppAsync(runtime, authorizer: authorizer);
        using var client = app.GetTestClient();

        foreach (var path in new[] { "/v1/run", "/v1/run/stream" })
        {
            using var anonymousContent = new StringContent(RequestJson("anonymous"), Encoding.UTF8, "application/json");
            using var anonymous = await client.PostAsync(path, anonymousContent, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var denied = CreateOwnedRequest(
                HttpMethod.Post,
                path,
                "owner-a",
                RequestJson("cross-owner", "other-session", "actor"));
            using var deniedResponse = await client.SendAsync(denied, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        }

        Assert.DoesNotContain(authorizer.Calls, call => call.SubjectId.Length == 0);
        Assert.Equal(0, sessionStore.LoadCalls);
        Assert.Equal(0, sessionStore.SaveCalls);
        Assert.Empty(provider.Requests);

        using var allowed = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/run",
            "owner-a",
            RequestJson("allowed"));
        using var allowedResponse = await client.SendAsync(allowed, TestContext.Current.CancellationToken);

        allowedResponse.EnsureSuccessStatusCode();
        Assert.True(sessionStore.LoadCalls > 0);
        Assert.True(sessionStore.SaveCalls > 0);
        Assert.Single(provider.Requests);
        Assert.Contains(authorizer.Calls, call =>
            call.SubjectId == "owner-a"
            && call.Key == new GameSessionKey("session", "actor")
            && call.Operation == GameAgentServerOperation.Run);
        Assert.Contains(authorizer.Calls, call => call.Operation == GameAgentServerOperation.Stream);
    }

    [Fact]
    public async Task ApiKeyAuthenticationProducesAStablePrincipalForOwnerAuthorization()
    {
        var authorizer = new TestOwnerAuthorizer((subject, key, operation) =>
            subject == "server-api-key"
            && key == new GameSessionKey("session", "actor")
            && operation == GameAgentServerOperation.Run);
        await using var app = await CreateAppAsync(
            new GameAgentRuntime(new GameAgentRuntimeOptions(new StreamingProvider(), "test")),
            apiKey: "secret",
            authorizer: authorizer);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/run")
        {
            Content = new StringContent(RequestJson("api-key-owner"), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains(authorizer.Calls, call => call.SubjectId == "server-api-key");
    }

    [Fact]
    public async Task UsageEndpointIsOwnerAuthorizedAndReturnsCompleteDurableCauseTotals()
    {
        var key = new GameSessionKey("usage-session", "usage-actor");
        var sessionStore = new CountingGameSessionStore();
        await sessionStore.SeedAsync(new GameSessionSnapshot(
            key,
            1,
            usageLedger: new GameSessionUsageLedger(new[]
            {
                new GameSessionUsageRecord(
                    "usage-assistant",
                    GameSessionUsageCause.Assistant,
                    new ModelUsage(
                        10,
                        4,
                        3,
                        2,
                        reasoningTokens: 2,
                        cacheWriteOneHourTokens: 1,
                        cost: new ModelCost(0.1, 0.2, 0.03, 0.04, isKnown: true)),
                    "run-1",
                    "input-1"),
                new GameSessionUsageRecord(
                    "usage-tool",
                    GameSessionUsageCause.Tool,
                    new ModelUsage(2, 1, cost: new ModelCost(0.02, 0.01, 0, 0, isKnown: true)),
                    "run-1",
                    "input-1"),
                new GameSessionUsageRecord(
                    "usage-compaction",
                    GameSessionUsageCause.Compaction,
                    new ModelUsage(5, 2, cost: new ModelCost(0.05, 0.02, 0, 0, isKnown: true)),
                    "run-1",
                    "input-1"),
            })));
        sessionStore.ResetCounters();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new StreamingProvider(), "test")
        {
            SessionStore = sessionStore,
        });
        var authorizer = new TestOwnerAuthorizer((subject, resource, operation) =>
            subject == "owner-a"
            && resource == key
            && operation == GameAgentServerOperation.ReadUsage);
        await using var app = await CreateAppAsync(runtime, authorizer: authorizer);
        using var client = app.GetTestClient();
        var requestJson = ControlJson(key);

        using var deniedRequest = CreateOwnedRequest(HttpMethod.Post, "/v1/usage", "owner-b", requestJson);
        using var denied = await client.SendAsync(deniedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, sessionStore.LoadCalls);

        using var allowedRequest = CreateOwnedRequest(HttpMethod.Post, "/v1/usage", "owner-a", requestJson);
        using var allowed = await client.SendAsync(allowedRequest, TestContext.Current.CancellationToken);
        var json = await allowed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        allowed.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("totalRecordCount").GetInt64());
        Assert.Equal(29, root.GetProperty("total").GetProperty("totalTokens").GetInt64());
        Assert.Equal(2, root.GetProperty("total").GetProperty("reasoningTokens").GetInt64());
        Assert.Equal(1, root.GetProperty("total").GetProperty("cacheWriteOneHourTokens").GetInt64());
        Assert.True(root.GetProperty("total").GetProperty("cost").GetProperty("known").GetBoolean());
        Assert.Equal(3, root.GetProperty("byCause").GetArrayLength());
        Assert.Equal(3, root.GetProperty("recentRecords").GetArrayLength());
        Assert.Contains(root.GetProperty("byCause").EnumerateArray(), item =>
            item.GetProperty("cause").GetString() == "Compaction"
            && item.GetProperty("usage").GetProperty("totalTokens").GetInt64() == 7);
        Assert.Equal(1, sessionStore.LoadCalls);
    }

    [Fact]
    public void UsageWireDistinguishesUnknownCostFromKnownFreeCost()
    {
        var key = new GameSessionKey("cost-session", "actor");
        var unknown = new GameSessionUsageSnapshot(
            key,
            1,
            new GameSessionUsageLedger(new[]
            {
                new GameSessionUsageRecord("unknown", GameSessionUsageCause.Assistant, new ModelUsage(1, 1)),
            }));
        var free = new GameSessionUsageSnapshot(
            key,
            1,
            new GameSessionUsageLedger(new[]
            {
                new GameSessionUsageRecord(
                    "free",
                    GameSessionUsageCause.Assistant,
                    new ModelUsage(1, 1, cost: new ModelCost(isKnown: true))),
            }));

        using var unknownDocument = JsonDocument.Parse(GameAgentWire.SerializeUsage(unknown));
        using var freeDocument = JsonDocument.Parse(GameAgentWire.SerializeUsage(free));

        var unknownCost = unknownDocument.RootElement.GetProperty("total").GetProperty("cost");
        Assert.False(unknownCost.GetProperty("known").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unknownCost.GetProperty("total").ValueKind);
        var freeCost = freeDocument.RootElement.GetProperty("total").GetProperty("cost");
        Assert.True(freeCost.GetProperty("known").GetBoolean());
        Assert.Equal(0, freeCost.GetProperty("total").GetDouble());
    }

    [Fact]
    public async Task AudienceProjectionProtectsReasoningAndToolDetailsForOwnerAndPublicViewers()
    {
        var key = new GameSessionKey("audience-session", "audience-actor");
        var authorizer = new TestOwnerAuthorizer((subject, resource, _) =>
            resource == key && subject is "owner-a" or "internal-viewer");
        var ownerPolicy = CreateAudiencePolicy(defaultAudience: GameAgentAudience.Owner);
        await using var ownerApp = await CreateAppAsync(
            CreateAudienceRuntime(),
            authorizer: authorizer,
            audiencePolicy: ownerPolicy);
        using var ownerClient = ownerApp.GetTestClient();

        using var ownerRequest = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/run",
            "owner-a",
            AudienceInputJson(key, "owner-input"));
        using var ownerResponse = await ownerClient.SendAsync(ownerRequest, TestContext.Current.CancellationToken);
        var ownerJson = await ownerResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ownerResponse.EnsureSuccessStatusCode();
        Assert.Contains("visible-answer", ownerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-reasoning", ownerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning-signature", ownerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("text-signature", ownerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-tool-result", ownerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-tool-details", ownerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-argument", ownerJson, StringComparison.Ordinal);

        using var deniedRequest = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/run",
            "owner-b",
            AudienceInputJson(key, "denied-input"));
        using var deniedResponse = await ownerClient.SendAsync(deniedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, deniedResponse.StatusCode);

        using var internalRequest = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/run/stream",
            "internal-viewer",
            AudienceInputJson(key, "internal-input"));
        internalRequest.Headers.Add("X-Test-Internal", "true");
        using var internalResponse = await ownerClient.SendAsync(internalRequest, TestContext.Current.CancellationToken);
        var internalStream = await internalResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        internalResponse.EnsureSuccessStatusCode();
        Assert.Contains("private-reasoning", internalStream, StringComparison.Ordinal);
        Assert.Contains("reasoning-signature", internalStream, StringComparison.Ordinal);
        Assert.Contains("text-signature", internalStream, StringComparison.Ordinal);
        Assert.Contains("private-tool-result", internalStream, StringComparison.Ordinal);
        Assert.Contains("private-tool-details", internalStream, StringComparison.Ordinal);
        Assert.Contains("secret-argument", internalStream, StringComparison.Ordinal);

        var publicAuthorizer = new TestOwnerAuthorizer((subject, resource, _) =>
            subject == "public-viewer" && resource == key);
        await using var publicApp = await CreateAppAsync(
            CreateAudienceRuntime(),
            authorizer: publicAuthorizer,
            audiencePolicy: CreateAudiencePolicy(defaultAudience: GameAgentAudience.Public));
        using var publicClient = publicApp.GetTestClient();
        using var publicRequest = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/run/stream",
            "public-viewer",
            AudienceInputJson(key, "public-input"));
        using var publicResponse = await publicClient.SendAsync(publicRequest, TestContext.Current.CancellationToken);
        var publicStream = await publicResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        publicResponse.EnsureSuccessStatusCode();
        Assert.Contains("visible-answer", publicStream, StringComparison.Ordinal);
        Assert.DoesNotContain("private-reasoning", publicStream, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning-signature", publicStream, StringComparison.Ordinal);
        Assert.DoesNotContain("text-signature", publicStream, StringComparison.Ordinal);
        Assert.DoesNotContain("private-tool-result", publicStream, StringComparison.Ordinal);
        Assert.DoesNotContain("private-tool-details", publicStream, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-argument", publicStream, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistedAudienceRoundTripsWithoutTrustingUserMetadata()
    {
        using var directory = new TemporaryDirectory();
        var key = new GameSessionKey("audience-persistence", "actor");
        var annotated = GameAgentAudienceMetadata.WithAudience(
            new AgentMessage(
                AgentRole.Assistant,
                new AgentContent[] { new TextContent("private") },
                DateTimeOffset.UnixEpoch,
                model: "test",
                stopReason: ModelStopReason.Stop),
            GameAgentAudience.Recipient("owner-a"));
        var store = new FileGameSessionStore(directory.Path);
        Assert.True((await store.SaveAsync(
            new GameSessionSnapshot(key, 1, new[] { annotated }),
            0,
            TestContext.Current.CancellationToken)).Saved);

        var loaded = await new FileGameSessionStore(directory.Path)
            .LoadAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.True(GameAgentAudienceMetadata.TryGetAudience(Assert.Single(loaded.Messages), out var audience));
        Assert.Equal(GameAgentAudienceKind.Recipient, audience.Kind);
        Assert.Equal("owner-a", audience.RecipientId);
        Assert.True(audience.IsVisibleTo(new GameAgentViewer("owner-a", isOwner: false)));
        Assert.False(audience.IsVisibleTo(new GameAgentViewer("owner-b", isOwner: true)));
        Assert.True(audience.IsVisibleTo(new GameAgentViewer("staff", isOwner: false, isInternal: true)));

        var forgedUser = AgentMessage.User(
            "forged",
            metadata: new Dictionary<string, string>
            {
                [GameAgentAudienceMetadata.AudienceKey] = "public",
            });
        Assert.False(GameAgentAudienceMetadata.TryGetAudience(forgedUser, out _));
        Assert.Throws<ArgumentException>(() =>
            GameAgentAudienceMetadata.WithAudience(forgedUser, GameAgentAudience.Public));
    }

    [Fact]
    public async Task OwnerAuthorizationCoversSteerAndAbortWithoutLettingPayloadSelectAnotherOwner()
    {
        var provider = new BlockingServerProvider();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["autonomous"] = GameRouteDecision.Agent("typed"),
            }),
        });
        var key = new GameSessionKey("owned-session", "owned-actor");
        var authorizer = new TestOwnerAuthorizer((subject, resource, _) =>
            subject == "owner-a" && resource == key);
        await using var app = await CreateAppAsync(runtime, authorizer: authorizer);
        using var client = app.GetTestClient();
        var input = new GameInput(
            key.SessionId,
            key.ActorId,
            "autonomous",
            "{}",
            new GameMoment("world", 1),
            "owned-input");
        using var runRequest = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/run",
            "owner-a",
            GameAgentWire.SerializeInput(input));
        var run = client.SendAsync(runRequest, TestContext.Current.CancellationToken);
        await provider.FirstRequestStarted.Task;

        using var deniedSteer = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/control/steer",
            "owner-b",
            ControlJson(key, "{\"threat\":true}"));
        using var deniedSteerResponse = await client.SendAsync(deniedSteer, TestContext.Current.CancellationToken);
        using var deniedAbort = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/control/abort",
            "owner-b",
            ControlJson(key));
        using var deniedAbortResponse = await client.SendAsync(deniedAbort, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, deniedSteerResponse.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, deniedAbortResponse.StatusCode);
        Assert.False(run.IsCompleted);
        Assert.Single(provider.Requests);

        using var allowedAbort = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/control/abort",
            "owner-a",
            ControlJson(key));
        using var allowedAbortResponse = await client.SendAsync(allowedAbort, TestContext.Current.CancellationToken);
        allowedAbortResponse.EnsureSuccessStatusCode();
        using var runResponse = await run;

        Assert.Contains(authorizer.Calls, call => call.SubjectId == "owner-b" && call.Operation == GameAgentServerOperation.Steer);
        Assert.Contains(authorizer.Calls, call => call.SubjectId == "owner-b" && call.Operation == GameAgentServerOperation.Abort);
        Assert.Contains(authorizer.Calls, call => call.SubjectId == "owner-a" && call.Operation == GameAgentServerOperation.Abort);
    }

    [Fact]
    public async Task EngineClientRejectsOversizedStreamingLineBeforeDispatchingEvent()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(
            "event: agent\ndata: " + new string('x', 128)));
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            new Uri("https://agent.test/"))
        {
            MaxEventCharacters = 32,
        });
        var dispatched = 0;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.StreamAsync(
            new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1)),
            (_, _) =>
            {
                dispatched++;
                return default;
            },
            TestContext.Current.CancellationToken));

        Assert.Contains("event line", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, dispatched);
    }

    [Fact]
    public async Task EngineClientRejectsOversizedSteeringPayloadBeforeParsingOrSending()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler("{}"));
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            new Uri("https://agent.test/"))
        {
            MaxRequestCharacters = 32,
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.SteerAsync(
            new GameSessionKey("session", "actor"),
            new string('x', 33),
            TestContext.Current.CancellationToken));

        Assert.Contains("steering payload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EngineClientRejectsEndpointPathsThatCouldRedirectCredentials()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler("{}"));
        var options = new ServerGameAgentClientOptions(httpClient, new Uri("https://agent.test/"))
        {
            ApiKey = "secret",
            RunPath = "https://other.test/v1/run",
        };

        var exception = Assert.Throws<ArgumentException>(() => new ServerGameAgentClient(options));

        Assert.Contains("relative", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineClientRejectsAmbiguousServerResults()
    {
        const string response = "{\"status\":\"Completed\",\"status\":\"Failed\",\"route\":\"QuickResponse\",\"sessionRevision\":1}";
        using var httpClient = new HttpClient(new StaticResponseHandler(response));
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            new Uri("https://agent.test/")));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.RunAsync(
            new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken));

        Assert.Contains("duplicate property", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"status\":1,\"route\":\"QuickResponse\",\"sessionRevision\":1}")]
    [InlineData("{\"status\":\"Completed\",\"route\":\"QuickResponse\",\"sessionRevision\":1.5}")]
    public async Task EngineClientRejectsMalformedServerResultShapes(string response)
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(response));
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            new Uri("https://agent.test/")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.RunAsync(
            new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1)),
            TestContext.Current.CancellationToken));

        Assert.Contains("response shape", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineClientRejectsEventsAfterTerminalServerResult()
    {
        const string response = """
            event: result
            data: {"status":"Completed","route":"QuickResponse","sessionRevision":1}

            event: agent
            data: {"kind":"late"}

            """;
        using var httpClient = new HttpClient(new StaticResponseHandler(response));
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            new Uri("https://agent.test/")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.StreamAsync(
            new GameInput("session", "actor", "chat", "{}", new GameMoment("world", 1)),
            (_, _) => default,
            TestContext.Current.CancellationToken));

        Assert.Contains("after its terminal result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineClientRejectsMalformedControlResponse()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler("{}"));
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            new Uri("https://agent.test/")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.SteerAsync(
            new GameSessionKey("session", "actor"),
            "{}",
            TestContext.Current.CancellationToken));

        Assert.Contains("boolean 'accepted'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineClientRejectsAmbiguousSteeringPayloadBeforeSending()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler("{}"));
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            httpClient,
            new Uri("https://agent.test/")));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.SteerAsync(
            new GameSessionKey("session", "actor"),
            "{\"threat\":false,\"threat\":true}",
            TestContext.Current.CancellationToken));

        Assert.Contains("duplicate property", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteResultRejectsInvalidContractValues()
    {
        Assert.Throws<ArgumentException>(() => new RemoteGameAgentResult("", "QuickResponse", 0, "{}"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RemoteGameAgentResult("Completed", "QuickResponse", -1, "{}"));
        Assert.Throws<ArgumentException>(() => new RemoteGameAgentResult("Completed", "QuickResponse", 0, "{broken"));
    }

    [Fact]
    public void ServerRejectsWhitespaceOnlyApiKeyConfiguration()
    {
        var app = WebApplication.CreateBuilder().Build();

        Assert.Throws<ArgumentException>(() => app.UseOpenGameAgentApiKey("   "));
        using var client = new HttpClient(new StaticResponseHandler("{}"));
        Assert.Throws<ArgumentException>(() => new ServerGameAgentClient(new ServerGameAgentClientOptions(
            client,
            new Uri("https://agent.test/?tenant=unsafe"))));
        Assert.Throws<ArgumentException>(() => new ServerGameAgentClient(new ServerGameAgentClientOptions(
            client,
            new Uri("https://agent.test/"))
        {
            ApiKey = "   ",
        }));
    }

    [Fact]
    public async Task AttachmentReadIsSessionAuthorizedBeforeTheRuntimeOrStoreIsTouched()
    {
        var attachments = new ServerAttachmentStore();
        var key = new GameSessionKey("image-session", "image-actor");
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new StreamingProvider(), "vision-model")
        {
            ImageAttachments = attachments,
        });
        var authorizer = new TestOwnerAuthorizer((subject, requested, _) =>
            string.Equals(subject, "owner-a", StringComparison.Ordinal)
            && requested.Equals(key));
        await using var app = await CreateAppAsync(runtime, authorizer: authorizer);
        using var client = app.GetTestClient();
        var input = new GameInput(
            key.SessionId,
            key.ActorId,
            "observe",
            "{}",
            new GameMoment("world", 1),
            "image-input",
            content: new AgentContent[]
            {
                new BinaryContent(AgentMediaKind.Image, "AQID", GameImageMediaTypes.Png, "frame.png"),
            });
        using var run = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/run",
            "owner-a",
            GameAgentWire.SerializeInput(input));
        using var runResponse = await client.SendAsync(run, TestContext.Current.CancellationToken);
        runResponse.EnsureSuccessStatusCode();
        var attachment = Assert.IsType<GameImageAttachment>(attachments.LastAttachment);
        var readsAfterRun = attachments.ReadCount;
        var requestJson = JsonSerializer.Serialize(new
        {
            sessionId = key.SessionId,
            actorId = key.ActorId,
            attachmentId = attachment.AttachmentId,
        });

        using var forbidden = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/attachments/read",
            "owner-b",
            requestJson);
        using var forbiddenResponse = await client.SendAsync(forbidden, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        Assert.Equal(readsAfterRun, attachments.ReadCount);
        Assert.Contains(authorizer.Calls, call => call.Operation == GameAgentServerOperation.ReadAttachment);

        using var allowed = CreateOwnedRequest(
            HttpMethod.Post,
            "/v1/attachments/read",
            "owner-a",
            requestJson);
        using var allowedResponse = await client.SendAsync(allowed, TestContext.Current.CancellationToken);
        var allowedJson = await allowedResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        allowedResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(allowedJson);
        Assert.Equal("AQID", document.RootElement.GetProperty("data").GetString());
        Assert.Equal(attachment.AttachmentId, document.RootElement
            .GetProperty("attachment")
            .GetProperty("attachmentId")
            .GetString());
    }

    [Fact]
    public async Task ServerClientReadsOnlyAttachmentsReferencedByTheSession()
    {
        var attachments = new ServerAttachmentStore();
        var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new StreamingProvider(), "vision-model")
        {
            ImageAttachments = attachments,
        });
        await using var app = await CreateAppAsync(runtime);
        using var http = app.GetTestClient();
        var remote = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            http,
            new Uri("http://localhost/")));
        var input = new GameInput(
            "client-image-session",
            "client-image-actor",
            "observe",
            "{}",
            new GameMoment("world", 1),
            "client-image-input",
            content: new AgentContent[]
            {
                new BinaryContent(AgentMediaKind.Image, "BAUG", GameImageMediaTypes.Png),
            });

        var result = await remote.RunAsync(input, TestContext.Current.CancellationToken);
        var attachment = Assert.IsType<GameImageAttachment>(attachments.LastAttachment);
        var stored = await remote.ReadImageAttachmentAsync(
            new GameSessionKey(input.SessionId, input.ActorId),
            attachment.AttachmentId,
            TestContext.Current.CancellationToken);
        var missing = await remote.ReadImageAttachmentAsync(
            new GameSessionKey(input.SessionId, "another-actor"),
            attachment.AttachmentId,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(stored);
        Assert.Equal(new byte[] { 4, 5, 6 }, stored.Data.ToArray());
        Assert.Null(missing);
    }

    [Fact]
    public async Task TranscriptReadIsAuthorizedPagedAndProjectsAttachmentMetadataWithoutPrivateParts()
    {
        var key = new GameSessionKey("transcript-session", "npc-a");
        var store = new CountingGameSessionStore();
        var attachment = new GameImageAttachment("sha256:frame", "image/png", 123, 640, 360, "frame");
        await store.SeedAsync(new GameSessionSnapshot(
            key,
            1,
            new AgentMessage[]
            {
                new(AgentRole.User, new AgentContent[] { new TextContent("first") }, DateTimeOffset.UnixEpoch),
                new(
                    AgentRole.Assistant,
                    new AgentContent[]
                    {
                        new ReasoningContent("private reasoning", redacted: false),
                        new TextContent("visible answer", signature: "private-signature"),
                        new ImageAttachmentContent(attachment),
                    },
                    DateTimeOffset.UnixEpoch),
                new(AgentRole.User, new AgentContent[] { new TextContent("third") }, DateTimeOffset.UnixEpoch),
            }));
        store.ResetCounters();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new StreamingProvider(), "test")
        {
            SessionStore = store,
        });
        var authorizer = new TestOwnerAuthorizer((subject, requested, operation) =>
            subject == "owner-a"
            && requested.Equals(key)
            && operation == GameAgentServerOperation.ReadTranscript);
        await using var app = await CreateAppAsync(
            runtime,
            authorizer: authorizer,
            audiencePolicy: CreateAudiencePolicy(GameAgentAudience.Owner));
        using var client = app.GetTestClient();

        using var denied = await client.SendAsync(
            CreateOwnedRequest(
                HttpMethod.Post,
                "/v1/transcript",
                "owner-b",
                "{\"sessionId\":\"transcript-session\",\"actorId\":\"npc-a\",\"pageSize\":2}"),
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, store.LoadCalls);

        using var firstResponse = await client.SendAsync(
            CreateOwnedRequest(
                HttpMethod.Post,
                "/v1/transcript",
                "owner-a",
                "{\"sessionId\":\"transcript-session\",\"actorId\":\"npc-a\",\"pageSize\":2}"),
            TestContext.Current.CancellationToken);
        var firstJson = await firstResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        firstResponse.EnsureSuccessStatusCode();
        using var first = JsonDocument.Parse(firstJson);
        var messages = first.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        var assistant = messages[1];
        Assert.DoesNotContain(
            assistant.GetProperty("content").EnumerateArray(),
            content => content.GetProperty("kind").GetString() == "reasoning");
        Assert.Null(assistant.GetProperty("details").GetString());
        var image = Assert.Single(
            assistant.GetProperty("content").EnumerateArray(),
            content => content.GetProperty("kind").GetString() == "image");
        Assert.Equal("sha256:frame", image.GetProperty("attachment").GetProperty("attachmentId").GetString());
        Assert.False(image.GetProperty("attachment").TryGetProperty("data", out _));
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();

        using var remoteHttp = app.GetTestClient();
        remoteHttp.DefaultRequestHeaders.Add("X-Test-Subject", "owner-a");
        var remote = new ServerGameAgentClient(new ServerGameAgentClientOptions(remoteHttp, new Uri("http://localhost")));
        var second = await remote.ReadTranscriptAsync(
            key,
            pageSize: 2,
            cursor: cursor,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        Assert.Equal(1, second.MessageCount);
        Assert.Null(second.NextCursor);
        Assert.Contains(authorizer.Calls, call => call.Operation == GameAgentServerOperation.ReadTranscript);
    }

    [Fact]
    public async Task TranscriptReadRejectsInvalidKeyAndOversizedPageBeforeLoadingTheSession()
    {
        var store = new CountingGameSessionStore();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new StreamingProvider(), "test")
        {
            SessionStore = store,
        });
        await using var app = await CreateAppAsync(runtime);
        using var client = app.GetTestClient();
        using var response = await client.PostAsync(
            "/v1/transcript",
            new StringContent(
                "{\"sessionId\":\"session\",\"actorId\":\"actor\",\"pageSize\":257}",
                Encoding.UTF8,
                "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, store.LoadCalls);

        using var invalidKey = await client.PostAsync(
            "/v1/transcript",
            new StringContent(
                "{\"sessionId\":\"session\",\"actorId\":\"\",\"pageSize\":1}",
                Encoding.UTF8,
                "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidKey.StatusCode);
        Assert.Equal(0, store.LoadCalls);
    }

    [Fact]
    public async Task TranscriptReadRejectsASingleMessageThatExceedsTheWireLimit()
    {
        var key = new GameSessionKey("large-transcript", "actor");
        var store = new CountingGameSessionStore();
        await store.SeedAsync(new GameSessionSnapshot(
            key,
            1,
            new[]
            {
                new AgentMessage(
                    AgentRole.User,
                    new AgentContent[]
                    {
                        new TextContent(new string('x', GameAgentWire.MaximumTranscriptPageUtf8Bytes)),
                    },
                    DateTimeOffset.UnixEpoch),
            }));
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(new StreamingProvider(), "test")
        {
            SessionStore = store,
        });
        await using var app = await CreateAppAsync(runtime);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            "/v1/transcript",
            new StringContent(
                "{\"sessionId\":\"large-transcript\",\"actorId\":\"actor\",\"pageSize\":1}",
                Encoding.UTF8,
                "application/json"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("transcript_message_too_large", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeClientNegotiatesStreamsAndReplaysWithoutRerunning()
    {
        var provider = new StreamingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test"));
        await using var app = await CreateAppAsync(runtime);
        using var http = app.GetTestClient();
        var client = new GameRuntimeServerClient(new GameRuntimeServerClientOptions(
            http,
            http.BaseAddress ?? new Uri("http://localhost/")));
        var negotiated = await client.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GameRuntimeProtocol.Version, negotiated.Version);
        Assert.Contains("events.cursor.v1", negotiated.Capabilities);

        var input = new GameInput(
            "runtime-session",
            "runtime-actor",
            "chat",
            "{\"text\":\"hello\"}",
            new GameMoment("world", 1),
            "runtime-input");
        var firstEvents = new List<GameRuntimeEventEnvelope>();
        var first = await client.StreamAsync(
            input,
            "request-1",
            (value, _) =>
            {
                firstEvents.Add(value);
                return default;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.Terminal);
        Assert.NotNull(first.LastEventId);
        Assert.True(firstEvents.Count > 3);
        Assert.True(firstEvents[^1].Terminal);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(firstEvents.Count, firstEvents.Select(value => value.EventId).Distinct().Count());

        var replayed = new List<GameRuntimeEventEnvelope>();
        var replay = await client.StreamAsync(
            input,
            "request-1",
            (value, _) =>
            {
                replayed.Add(value);
                return default;
            },
            lastEventId: firstEvents[1].EventId,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(replay.Terminal);
        Assert.Equal(firstEvents.Skip(2).Select(value => value.EventId), replayed.Select(value => value.EventId));
        Assert.Equal(1, provider.Calls);

        var page = await client.ReadEventsAsync(
            new GameSessionKey(input.SessionId, input.ActorId),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(page.Gap);
        Assert.Equal(page.LastSequence, page.NextAfterSequence);
        Assert.True(page.Events[^1].Terminal);
    }

    [Fact]
    public async Task TypedClientReadsServerCapabilitiesAndCompleteUsage()
    {
        await using var app = await CreateAppAsync();
        using var http = app.GetTestClient();
        var client = new ServerGameAgentClient(new ServerGameAgentClientOptions(
            http,
            http.BaseAddress ?? new Uri("http://localhost/")));
        var capabilities = await client.ReadCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.Equal("OpenGameAgent", capabilities.Name);
        Assert.Equal("1", capabilities.ProtocolVersion);

        var input = new GameInput(
            "usage-client-session",
            "usage-client-actor",
            "chat",
            "{}",
            new GameMoment("world", 1),
            "usage-client-input");
        Assert.True((await client.RunAsync(input, TestContext.Current.CancellationToken)).Succeeded);
        var usage = await client.ReadUsageAsync(
            new GameSessionKey(input.SessionId, input.ActorId),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, usage.TotalTokens);
        Assert.Equal(1, usage.TotalRecordCount);
        Assert.False(usage.CostKnown);
        Assert.Null(usage.TotalCost);
    }

    [Fact]
    public async Task RuntimeStreamForANewInputDoesNotReplayAnOlderRunTerminal()
    {
        var provider = new StreamingProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test"));
        await using var app = await CreateAppAsync(runtime);
        using var http = app.GetTestClient();
        var client = new GameRuntimeServerClient(new GameRuntimeServerClientOptions(
            http,
            http.BaseAddress ?? new Uri("http://localhost/")));
        var first = new GameInput(
            "shared-session",
            "shared-actor",
            "chat",
            "{}",
            new GameMoment("world", 1),
            "first-input");
        Assert.True((await client.StreamAsync(
            first,
            "first-request",
            (_, _) => default,
            cancellationToken: TestContext.Current.CancellationToken)).Terminal);

        var secondEvents = new List<GameRuntimeEventEnvelope>();
        var second = new GameInput(
            first.SessionId,
            first.ActorId,
            "chat",
            "{}",
            new GameMoment("world", 2),
            "second-input");
        var result = await client.StreamAsync(
            second,
            "second-request",
            (value, _) =>
            {
                secondEvents.Add(value);
                return default;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Terminal);
        Assert.NotEmpty(secondEvents);
        Assert.All(secondEvents, value => Assert.Equal(second.InputId, value.InputId));
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task RuntimeExactInterruptRejectsStaleCoordinatesAndSettlesTheRun()
    {
        var provider = new BlockingServerProvider();
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["autonomous"] = GameRouteDecision.Agent("typed"),
            }),
        });
        await using var app = await CreateAppAsync(runtime);
        using var http = app.GetTestClient();
        var client = new GameRuntimeServerClient(new GameRuntimeServerClientOptions(
            http,
            http.BaseAddress ?? new Uri("http://localhost/")));
        var coordinate = new TaskCompletionSource<(string RunId, int Turn, string TurnId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new GameInput(
            "interrupt-session",
            "interrupt-actor",
            "autonomous",
            "{}",
            new GameMoment("world", 1),
            "interrupt-input");
        var stream = client.StreamAsync(
            input,
            "interrupt-request",
            (value, _) =>
            {
                if (value.RunId is not null && value.Turn is > 0 && value.TurnId is not null)
                {
                    coordinate.TrySetResult((value.RunId, value.Turn.Value, value.TurnId));
                }

                return default;
            },
            cancellationToken: TestContext.Current.CancellationToken);
        await provider.FirstRequestStarted.Task;
        var active = await coordinate.Task;

        var stale = await client.InterruptAsync(
            new GameRuntimeControlRequest(
                input.SessionId,
                input.ActorId,
                active.RunId + "-stale",
                "stale-turn",
                active.Turn),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GameRuntimeControlStatus.RunMismatch, stale.Status);

        var staleTurn = await client.InterruptAsync(
            new GameRuntimeControlRequest(
                input.SessionId,
                input.ActorId,
                active.RunId,
                active.TurnId + "-stale",
                active.Turn),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GameRuntimeControlStatus.TurnMismatch, staleTurn.Status);

        var accepted = await client.InterruptAsync(
            new GameRuntimeControlRequest(
                input.SessionId,
                input.ActorId,
                active.RunId,
                active.TurnId,
                active.Turn),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(accepted.Accepted);
        var result = await stream;
        Assert.True(result.Terminal);
    }

    [Fact]
    public async Task RuntimeAuthorizationRunsBeforeRuntimeStateAndPayloadCannotSelectAnotherOwner()
    {
        var provider = new StreamingProvider();
        var store = new CountingGameSessionStore();
        var key = new GameSessionKey("owned-runtime", "actor");
        await using var runtime = new GameAgentRuntime(new GameAgentRuntimeOptions(provider, "test")
        {
            SessionStore = store,
        });
        var authorizer = new TestOwnerAuthorizer((subject, resource, operation) =>
            subject == "owner-a"
            && resource == key
            && operation == GameAgentServerOperation.Stream);
        await using var app = await CreateAppAsync(runtime, authorizer: authorizer);
        using var http = app.GetTestClient();
        var deniedInput = new GameInput(
            "other-session",
            key.ActorId,
            "chat",
            "{}",
            new GameMoment("world", 1),
            "denied-input");
        var deniedBody = JsonSerializer.Serialize(new
        {
            requestId = "denied-request",
            inputJson = GameAgentWire.SerializeInput(deniedInput),
        });
        using var denied = CreateOwnedRequest(
            HttpMethod.Post,
            "/runtime/v1/run/stream",
            "owner-a",
            deniedBody);
        using var deniedResponse = await http.SendAsync(denied, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, provider.Calls);

        http.DefaultRequestHeaders.Add("X-Test-Subject", "owner-a");
        var client = new GameRuntimeServerClient(new GameRuntimeServerClientOptions(
            http,
            http.BaseAddress ?? new Uri("http://localhost/")));
        var allowed = await client.StreamAsync(
            new GameInput(
                key.SessionId,
                key.ActorId,
                "chat",
                "{}",
                new GameMoment("world", 1),
                "allowed-input"),
            "allowed-request",
            (_, _) => default,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(allowed.Terminal);
        Assert.True(store.LoadCalls > 0);
        Assert.True(store.SaveCalls > 0);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task RuntimeProjectionNeverLeaksReasoningOrToolDetailsToOwner()
    {
        var key = new GameSessionKey("runtime-audience", "actor");
        var authorizer = new TestOwnerAuthorizer((subject, resource, _) =>
            subject == "owner-a" && resource == key);
        await using var app = await CreateAppAsync(
            CreateAudienceRuntime(),
            authorizer: authorizer,
            audiencePolicy: CreateAudiencePolicy(GameAgentAudience.Owner));
        using var http = app.GetTestClient();
        var input = new GameInput(
            key.SessionId,
            key.ActorId,
            "autonomous",
            "{}",
            new GameMoment("world", 1),
            "runtime-audience-input");
        var body = JsonSerializer.Serialize(new
        {
            requestId = "audience-request",
            inputJson = GameAgentWire.SerializeInput(input),
        });
        using var request = CreateOwnedRequest(
            HttpMethod.Post,
            "/runtime/v1/run/stream",
            "owner-a",
            body);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        var stream = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("visible-answer", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("private-reasoning", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning-signature", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("text-signature", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("private-tool-result", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("private-tool-details", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-argument", stream, StringComparison.Ordinal);
    }

    private static async Task<WebApplication> CreateAppAsync(
        string? apiKey = null,
        int maximumRequestBodyBytes = ServerEndpoints.DefaultMaximumRequestBodyBytes)
    {
        return await CreateAppAsync(
            new GameAgentRuntime(new GameAgentRuntimeOptions(new StreamingProvider(), "test")),
            apiKey,
            maximumRequestBodyBytes);
    }

    private static async Task<WebApplication> CreateAppAsync(
        GameAgentRuntime runtime,
        string? apiKey = null,
        int maximumRequestBodyBytes = ServerEndpoints.DefaultMaximumRequestBodyBytes,
        IGameAgentOwnerAuthorizer? authorizer = null,
        IGameAgentAudiencePolicy? audiencePolicy = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(runtime);
        if (authorizer is not null)
        {
            builder.Services.AddSingleton(authorizer);
        }

        if (audiencePolicy is not null)
        {
            builder.Services.AddSingleton(audiencePolicy);
        }

        var app = builder.Build();
        app.UseOpenGameAgentApiKey(apiKey);
        if (authorizer is not null)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Subject", out var values)
                    && values.Count == 1
                    && !string.IsNullOrWhiteSpace(values[0]))
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, values[0]!),
                            new Claim(
                                "opengameagent.internal",
                                context.Request.Headers["X-Test-Internal"].ToString()),
                        },
                        "test"));
                }

                await next(context);
            });
        }

        app.MapOpenGameAgent(maximumRequestBodyBytes);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static string RequestJson(
        string inputId,
        string sessionId = "session",
        string actorId = "actor") => $$"""
        {
          "inputId": "{{inputId}}",
          "sessionId": "{{sessionId}}",
          "actorId": "{{actorId}}",
          "type": "chat",
          "payload": { "text": "hello", "weight": 1.5 },
          "timelineId": "world",
          "tick": 42
        }
        """;

    private static HttpRequestMessage CreateOwnedRequest(
        HttpMethod method,
        string path,
        string subjectId,
        string json)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Test-Subject", subjectId);
        return request;
    }

    private static string ControlJson(GameSessionKey key, string payloadJson = "{}") => $$"""
        {
          "sessionId": "{{key.SessionId}}",
          "actorId": "{{key.ActorId}}",
          "payload": {{payloadJson}}
        }
        """;

    private static string AudienceInputJson(GameSessionKey key, string inputId) =>
        GameAgentWire.SerializeInput(new GameInput(
            key.SessionId,
            key.ActorId,
            "autonomous",
            "{}",
            new GameMoment("world", 1),
            inputId));

    private static MetadataGameAgentAudiencePolicy CreateAudiencePolicy(GameAgentAudience defaultAudience) =>
        new(
            (principal, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                var isInternal = string.Equals(
                    principal.FindFirstValue("opengameagent.internal"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                return new ValueTask<GameAgentViewer>(new GameAgentViewer(
                    id,
                    isOwner: string.Equals(id, "owner-a", StringComparison.Ordinal),
                    isInternal));
            },
            defaultAudience);

    private static GameAgentRuntime CreateAudienceRuntime() =>
        new(new GameAgentRuntimeOptions(new AudienceProvider(), "test")
        {
            RoutePolicy = new AutomaticGameRoutePolicy(new Dictionary<string, GameRouteDecision>
            {
                ["autonomous"] = GameRouteDecision.Agent("audience-test"),
            }),
            ToolProvider = (_, _) => new ValueTask<IReadOnlyList<AgentTool>>(new[]
            {
                new AgentTool(
                    new ToolDefinition(
                        "private_tool",
                        "Returns private tool data.",
                        "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"],\"additionalProperties\":false}"),
                    (_, _, _) => new ValueTask<ToolResult>(new ToolResult(
                        new AgentContent[] { new TextContent("private-tool-result") },
                        detailsJson: "{\"secret\":\"private-tool-details\"}")),
                    ToolRisk.ReadOnly),
            }),
        });

    private sealed class TestOwnerAuthorizer : IGameAgentOwnerAuthorizer
    {
        private readonly Func<string, GameSessionKey, GameAgentServerOperation, bool> _authorize;

        public TestOwnerAuthorizer(Func<string, GameSessionKey, GameAgentServerOperation, bool> authorize)
        {
            _authorize = authorize;
        }

        public System.Collections.Concurrent.ConcurrentQueue<AuthorizationCall> Calls { get; } = new();

        public ValueTask<bool> AuthorizeAsync(
            GameAgentAuthorizationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subjectId = context.Principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            Calls.Enqueue(new AuthorizationCall(subjectId, context.Key, context.Operation));
            return new ValueTask<bool>(_authorize(subjectId, context.Key, context.Operation));
        }
    }

    private sealed record AuthorizationCall(
        string SubjectId,
        GameSessionKey Key,
        GameAgentServerOperation Operation);

    private sealed class CountingGameSessionStore : IGameSessionStore
    {
        private readonly InMemoryGameSessionStore _inner = new();

        public int LoadCalls { get; private set; }

        public int SaveCalls { get; private set; }

        public async Task SeedAsync(GameSessionSnapshot snapshot)
        {
            var result = await _inner.SaveAsync(snapshot, 0, TestContext.Current.CancellationToken);
            Assert.True(result.Saved);
        }

        public void ResetCounters()
        {
            LoadCalls = 0;
            SaveCalls = 0;
        }

        public ValueTask<GameSessionSnapshot?> LoadAsync(
            GameSessionKey key,
            CancellationToken cancellationToken)
        {
            LoadCalls++;
            return _inner.LoadAsync(key, cancellationToken);
        }

        public ValueTask<GameSessionSaveResult> SaveAsync(
            GameSessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            return _inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }
    }

    private sealed class StreamingProvider : IModelProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.Started,
                new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending));
            yield return ModelStreamEvent.Update(
                ModelStreamEventKind.TextDelta,
                new ModelResponse(new AgentContent[] { new TextContent("hel") }, ModelStopReason.Pending),
                "hel");
            yield return ModelStreamEvent.Terminal(
                new ModelResponse(new AgentContent[] { new TextContent("hello") }, ModelStopReason.Stop, new ModelUsage(2, 1)));
        }
    }

    private sealed class ServerAttachmentStore : IGameImageAttachmentStore
    {
        private readonly Dictionary<string, StoredGameImageAttachment> _objects = new(StringComparer.Ordinal);
        private int _readCount;

        public GameImageAttachmentLimits ImageLimits { get; } = new();

        public GameImageAttachment? LastAttachment { get; private set; }

        public int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask ValidateImageAsync(
            SaveGameImageAttachment input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask<GameImageAttachment> SaveImageAsync(
            SaveGameImageAttachment input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = input.Data.ToArray();
            var id = "sha256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
            var attachment = new GameImageAttachment(id, input.MediaType, input.Data.Length, 1, 1, input.Name);
            _objects[id] = new StoredGameImageAttachment(attachment, data);
            LastAttachment = attachment;
            return new ValueTask<GameImageAttachment>(attachment);
        }

        public ValueTask<StoredGameImageAttachment> ReadImageAsync(
            GameImageAttachment attachment,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            return new ValueTask<StoredGameImageAttachment>(_objects[attachment.AttachmentId]);
        }
    }

    private sealed class ResourceCaptureProvider : IModelProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(
                new ModelResponse(new AgentContent[] { new TextContent("ok") }, ModelStopReason.Stop));
        }
    }

    private sealed class RoutedProvider : IModelProvider
    {
        private readonly string _text;
        private readonly string _responseId;

        public RoutedProvider(string text, string responseId)
        {
            _text = text;
            _responseId = responseId;
        }

        public System.Collections.Concurrent.ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            await Task.Yield();
            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent(_text) },
                ModelStopReason.Stop,
                responseId: _responseId));
        }
    }

    private sealed class TransientFailureProvider : IModelProvider
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            await Task.Yield();
            throw new ModelProviderException("temporary failure", isTransient: true);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class ToolIdentityProvider : IModelProvider
    {
        private int _calls;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                yield return ModelStreamEvent.Update(
                    ModelStreamEventKind.Started,
                    new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending));
                yield return ModelStreamEvent.Update(
                    ModelStreamEventKind.ToolCallDelta,
                    new ModelResponse(
                        new AgentContent[]
                        {
                            new TextContent("prefix"),
                            new ReasoningContent("plan"),
                            new ToolCallContent("call-2", "move", "{}"),
                        },
                        ModelStopReason.Pending),
                    "{}",
                    contentIndex: 2,
                    toolCallId: "call-2",
                    toolName: "move");
                yield return ModelStreamEvent.Terminal(new ModelResponse(
                    new AgentContent[] { new ToolCallContent("call-2", "move", "{}") },
                    ModelStopReason.ToolUse));
                yield break;
            }

            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("done") },
                ModelStopReason.Stop));
        }
    }

    private sealed class BlockingServerProvider : IModelProvider
    {
        private int _calls;

        public System.Collections.Concurrent.ConcurrentQueue<ModelRequest> Requests { get; } = new();

        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstResponse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstRequestStarted.SetResult();
                await ReleaseFirstResponse.Task.WaitAsync(cancellationToken);
                yield return ModelStreamEvent.Terminal(
                    new ModelResponse(new AgentContent[] { new TextContent("working") }, ModelStopReason.Stop));
                yield break;
            }

            yield return ModelStreamEvent.Terminal(
                new ModelResponse(new AgentContent[] { new TextContent("updated") }, ModelStopReason.Stop));
        }
    }

    private sealed class AudienceProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (request.Turn == 1)
            {
                var reasoning = new ReasoningContent(
                    "private-reasoning",
                    "reasoning-signature",
                    redacted: true);
                yield return ModelStreamEvent.Update(
                    ModelStreamEventKind.Started,
                    new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Pending));
                yield return ModelStreamEvent.Update(
                    ModelStreamEventKind.ReasoningDelta,
                    new ModelResponse(new AgentContent[] { reasoning }, ModelStopReason.Pending),
                    "private-reasoning");
                yield return ModelStreamEvent.Terminal(new ModelResponse(
                    new AgentContent[]
                    {
                        reasoning,
                        new ToolCallContent(
                            "private-call",
                            "private_tool",
                            "{\"value\":\"secret-argument\"}",
                            "tool-thought-signature"),
                    },
                    ModelStopReason.ToolUse));
                yield break;
            }

            yield return ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("visible-answer", "text-signature") },
                ModelStopReason.Stop));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenGameAgent.Server.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenGameAgent.Server.Tests"));
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the test root.");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StaticResponseHandler(string body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StaticHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            _ = name;
            return new HttpClient(_handler, disposeHandler: false);
        }
    }
}

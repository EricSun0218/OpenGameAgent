using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenGameAgent.Client;
using OpenGameAgent.Kernel;
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
            resources: new[]
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
            resources: new[]
            {
                new ResourceContent("https://assets.example.test/frame.png", "image/png", "frame"),
            });
        var roundTrip = GameAgentWire.ParseInput(GameAgentWire.SerializeInput(input));
        Assert.Null(roundTrip.Moment.CalendarJson);
        var roundTripResource = Assert.Single(roundTrip.Resources);
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

        foreach (var path in new[] { "/v1/run", "/v1/run/stream", "/v1/control/steer", "/v1/control/abort" })
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

        Assert.True(result.Succeeded);
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

        using var allowedContent = new StringContent(RequestJson("allowed"), Encoding.UTF8, "application/json");
        using var allowedRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/run") { Content = allowedContent };
        allowedRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
        using var allowed = await client.SendAsync(allowedRequest, TestContext.Current.CancellationToken);
        allowed.EnsureSuccessStatusCode();

        using var health = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        health.EnsureSuccessStatusCode();
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
        int maximumRequestBodyBytes = ServerEndpoints.DefaultMaximumRequestBodyBytes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(runtime);
        var app = builder.Build();
        app.UseOpenGameAgentApiKey(apiKey);
        app.MapOpenGameAgent(maximumRequestBodyBytes);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static string RequestJson(string inputId) => $$"""
        {
          "inputId": "{{inputId}}",
          "sessionId": "session",
          "actorId": "actor",
          "type": "chat",
          "payload": { "text": "hello", "weight": 1.5 },
          "timelineId": "world",
          "tick": 42
        }
        """;

    private sealed class StreamingProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
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
}

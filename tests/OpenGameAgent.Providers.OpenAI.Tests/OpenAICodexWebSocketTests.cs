using System.Net;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.ProviderTransport;
using Xunit;

namespace OpenGameAgent.Providers.OpenAI.Tests;

public sealed class OpenAICodexWebSocketTests
{
    [Fact]
    public async Task NewWebSocketHandshakePublishesOnlySanitizedResponseMetadata()
    {
        ProviderResponseObservation? observed = null;
        var http = new CountingHandler();
        var connection = new ScriptedConnection(
            (_, _) => new object[] { Completed("resp_1") },
            new Dictionary<string, string>
            {
                ["x-request-id"] = "ws-request",
                ["set-cookie"] = "credential=secret",
            });
        var factory = new QueueFactory(connection);
        var options = OpenAICodexResponses.CreateOptions(new HttpClient(http), Token("account-one"));
        options.WebSocketConnectionFactory = factory;
        options.ResponseObserver = (observation, _) =>
        {
            observed = observation;
            return default;
        };
        using var provider = new OpenAIResponsesProvider(options);

        var events = await CollectAsync(provider.StreamAsync(
            Request(
                new[] { AgentMessage.User("hello", DateTimeOffset.UnixEpoch) },
                ModelTransport.WebSocket,
                ModelCacheRetention.None,
                null),
            TestContext.Current.CancellationToken));

        Assert.True(events[^1].IsTerminal);
        Assert.NotNull(observed);
        Assert.Equal(101, observed.StatusCode);
        Assert.Equal("ws-request", observed.Metadata["x-request-id"]);
        Assert.Single(observed.Metadata);
    }

    [Fact]
    public async Task AutoUsesOneShotWebSocketAndCodexHeadersWhenCachingIsDisabled()
    {
        var http = new CountingHandler();
        var connection = new ScriptedConnection((_, _) => new object[] { Completed("resp_1") });
        var factory = new QueueFactory(connection);
        using var provider = Provider(http, factory, "account-one");
        var request = Request(
            new[] { AgentMessage.User("hello", DateTimeOffset.UnixEpoch) },
            ModelTransport.Auto,
            ModelCacheRetention.None,
            "ignored-session");

        var events = await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.True(events[^1].IsTerminal);
        Assert.Equal(0, http.Calls);
        Assert.Equal(1, factory.Calls);
        Assert.Equal("responses_websockets=2026-02-06", factory.Requests[0].Headers["OpenAI-Beta"]);
        Assert.Equal("account-one", factory.Requests[0].Headers["chatgpt-account-id"]);
        Assert.NotEqual("ignored-session", factory.Requests[0].Headers["session-id"]);
        Assert.Equal(1, connection.DisposeCalls);
        using var body = JsonDocument.Parse(connection.SentBodies[0]);
        Assert.Equal("response.create", body.RootElement.GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("prompt_cache_key", out _));
    }

    [Fact]
    public async Task CachedWebSocketReusesAccountConnectionAndSendsOnlyInputDelta()
    {
        var http = new CountingHandler();
        var connection = new ScriptedConnection((send, _) => send == 1
            ? TextResponse("resp_1", "msg_1", "hello")
            : new object[] { Completed("resp_2") });
        var factory = new QueueFactory(connection);
        var observerCalls = 0;
        using var provider = Provider(
            http,
            factory,
            "account-one",
            (_, _) =>
            {
                Interlocked.Increment(ref observerCalls);
                return default;
            });
        var firstUser = AgentMessage.User("start", DateTimeOffset.UnixEpoch);
        var firstRequest = Request(
            new[] { firstUser },
            ModelTransport.CachedWebSocket,
            ModelCacheRetention.Short,
            "session-one");

        var firstEvents = await CollectAsync(
            provider.StreamAsync(firstRequest, TestContext.Current.CancellationToken));
        var firstResponse = firstEvents[^1].Response!;
        var assistant = Assistant(firstResponse, firstRequest.Model);
        var secondRequest = Request(
            new[]
            {
                firstUser,
                assistant,
                AgentMessage.User("finish", DateTimeOffset.UnixEpoch.AddSeconds(1)),
            },
            ModelTransport.CachedWebSocket,
            ModelCacheRetention.Short,
            "session-one");

        await CollectAsync(provider.StreamAsync(secondRequest, TestContext.Current.CancellationToken));

        Assert.Equal(0, http.Calls);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(2, connection.SentBodies.Count);
        using var secondBody = JsonDocument.Parse(connection.SentBodies[1]);
        var root = secondBody.RootElement;
        Assert.Equal("resp_1", root.GetProperty("previous_response_id").GetString());
        var delta = root.GetProperty("input");
        Assert.Single(delta.EnumerateArray());
        Assert.Equal("finish", delta[0].GetProperty("content")[0].GetProperty("text").GetString());
        var statistics = provider.GetWebSocketStatistics("session-one")!;
        Assert.Equal(2, statistics.Requests);
        Assert.Equal(1, statistics.ConnectionsCreated);
        Assert.Equal(1, statistics.ConnectionsReused);
        Assert.Equal(1, statistics.FullContextRequests);
        Assert.Equal(1, statistics.DeltaRequests);
        Assert.Equal(1, observerCalls);
    }

    [Fact]
    public async Task FailureBeforeOutputFallsBackAndPinsSessionToSse()
    {
        var http = new CountingHandler();
        var connection = new ScriptedConnection((_, _) => new object[] { new IOException("connect path failed") });
        var factory = new QueueFactory(connection);
        using var provider = Provider(http, factory, "account-one");
        var request = Request(
            new[] { AgentMessage.User("hello", DateTimeOffset.UnixEpoch) },
            ModelTransport.Auto,
            ModelCacheRetention.Short,
            "fallback-session");

        var first = await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));
        var second = await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(1, factory.Calls);
        Assert.Equal(2, http.Calls);
        Assert.Contains(first[^1].Response!.Diagnostics, value => value.Code == "provider_transport_fallback");
        Assert.Contains(second[^1].Response!.Diagnostics, value => value.Code == "provider_transport_fallback");
        var statistics = provider.GetWebSocketStatistics("fallback-session")!;
        Assert.Equal(1, statistics.Failures);
        Assert.Equal(2, statistics.SseFallbacks);
        Assert.True(statistics.FallbackActive);
    }

    [Fact]
    public async Task FailureAfterOutputNeverReplaysOverSse()
    {
        var http = new CountingHandler();
        var connection = new ScriptedConnection((_, _) => new object[]
        {
            JsonSerializer.Serialize(new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = new { type = "message", id = "msg_1", role = "assistant", status = "in_progress" },
            }),
            new IOException("stream failed"),
        });
        var factory = new QueueFactory(connection);
        using var provider = Provider(http, factory, "account-one");
        var request = Request(
            new[] { AgentMessage.User("hello", DateTimeOffset.UnixEpoch) },
            ModelTransport.Auto,
            ModelCacheRetention.Short,
            "started-session");

        var error = await Assert.ThrowsAnyAsync<IOException>(async () =>
            await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken)));

        Assert.Equal("stream failed", error.Message);
        Assert.Equal(0, http.Calls);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task ConnectionLimitBeforeOutputReconnectsExactlyOnce()
    {
        var http = new CountingHandler();
        var limited = new ScriptedConnection((_, _) => new object[]
        {
            JsonSerializer.Serialize(new
            {
                type = "error",
                error = new { code = "websocket_connection_limit_reached", message = "limit" },
            }),
        });
        var succeeding = new ScriptedConnection((_, _) => new object[] { Completed("resp_1") });
        var factory = new QueueFactory(limited, succeeding);
        using var provider = Provider(http, factory, "account-one");
        var request = Request(
            Array.Empty<AgentMessage>(),
            ModelTransport.WebSocket,
            ModelCacheRetention.None,
            sessionId: null);

        var events = await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.True(events[^1].IsTerminal);
        Assert.Equal(2, factory.Calls);
        Assert.Equal(0, http.Calls);
        Assert.Equal(1, limited.DisposeCalls);
        Assert.Equal(1, succeeding.DisposeCalls);
    }

    [Fact]
    public async Task CachedConnectionsAreScopedToTheAuthenticatedAccount()
    {
        var http = new CountingHandler();
        var firstAccount = new ScriptedConnection((send, _) => new object[] { Completed("a-" + send) });
        var secondAccount = new ScriptedConnection((send, _) => new object[] { Completed("b-" + send) });
        var factory = new QueueFactory(firstAccount, secondAccount);
        var credentialCall = 0;
        var options = OpenAICodexResponses.CreateOptions(
            new HttpClient(http),
            _ => new ValueTask<OpenAIRequestCredential?>(new OpenAIRequestCredential(
                Token(credentialCall++ == 1 ? "account-two" : "account-one"))));
        options.WebSocketConnectionFactory = factory;
        using var provider = new OpenAIResponsesProvider(options);
        var request = Request(
            Array.Empty<AgentMessage>(),
            ModelTransport.CachedWebSocket,
            ModelCacheRetention.Short,
            "shared-session");

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));
        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));
        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(2, factory.Calls);
        Assert.Equal("account-one", factory.Requests[0].Headers["chatgpt-account-id"]);
        Assert.Equal("account-two", factory.Requests[1].Headers["chatgpt-account-id"]);
        Assert.Equal(2, firstAccount.SentBodies.Count);
        Assert.Single(secondAccount.SentBodies);
        var statistics = provider.GetWebSocketStatistics("shared-session")!;
        Assert.Equal(2, statistics.ConnectionsCreated);
        Assert.Equal(1, statistics.ConnectionsReused);
    }

    [Fact]
    public async Task MissingCachedContinuationRetriesWithFullContext()
    {
        var http = new CountingHandler();
        var cached = new ScriptedConnection((send, _) => send == 1
            ? TextResponse("resp_1", "msg_1", "hello")
            : new object[]
            {
                JsonSerializer.Serialize(new
                {
                    type = "error",
                    error = new { code = "previous_response_not_found", message = "missing" },
                }),
            });
        var recovered = new ScriptedConnection((_, _) => new object[] { Completed("resp_2") });
        var factory = new QueueFactory(cached, recovered);
        using var provider = Provider(http, factory, "account-one");
        var firstUser = AgentMessage.User("start", DateTimeOffset.UnixEpoch);
        var firstRequest = Request(
            new[] { firstUser },
            ModelTransport.CachedWebSocket,
            ModelCacheRetention.Short,
            "recovery-session");
        var first = (await CollectAsync(provider.StreamAsync(
            firstRequest,
            TestContext.Current.CancellationToken)))[^1].Response!;
        var secondRequest = Request(
            new[]
            {
                firstUser,
                Assistant(first, firstRequest.Model),
                AgentMessage.User("finish", DateTimeOffset.UnixEpoch.AddSeconds(1)),
            },
            ModelTransport.CachedWebSocket,
            ModelCacheRetention.Short,
            "recovery-session");

        var second = await CollectAsync(provider.StreamAsync(
            secondRequest,
            TestContext.Current.CancellationToken));

        Assert.True(second[^1].IsTerminal);
        Assert.Equal(2, factory.Calls);
        Assert.Equal(2, cached.SentBodies.Count);
        Assert.Single(recovered.SentBodies);
        using var delta = JsonDocument.Parse(cached.SentBodies[1]);
        Assert.Equal("resp_1", delta.RootElement.GetProperty("previous_response_id").GetString());
        using var full = JsonDocument.Parse(recovered.SentBodies[0]);
        Assert.False(full.RootElement.TryGetProperty("previous_response_id", out _));
        Assert.Equal(3, full.RootElement.GetProperty("input").GetArrayLength());
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task StoppingEnumerationEarlyReleasesTheCachedConnection()
    {
        var http = new CountingHandler();
        var connection = new ScriptedConnection((_, _) => TextResponse("resp_1", "msg_1", "hello"));
        var factory = new QueueFactory(connection);
        using var provider = Provider(http, factory, "account-one");
        var request = Request(
            new[] { AgentMessage.User("hello", DateTimeOffset.UnixEpoch) },
            ModelTransport.CachedWebSocket,
            ModelCacheRetention.Short,
            "early-stop");

        await using (var enumerator = provider.StreamAsync(
                         request,
                         TestContext.Current.CancellationToken)
                     .GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(ModelStreamEventKind.Started, enumerator.Current.Kind);
        }

        Assert.Equal(1, connection.DisposeCalls);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task ProviderProtocolFailureBeforeOutputDoesNotReplayOverSse()
    {
        var http = new CountingHandler();
        var connection = new ScriptedConnection((_, _) => new object[]
        {
            JsonSerializer.Serialize(new
            {
                type = "response.failed",
                response = new
                {
                    status = "failed",
                    error = new { code = "usage_limit_reached", message = "quota exhausted" },
                },
            }),
        });
        var factory = new QueueFactory(connection);
        using var provider = Provider(http, factory, "account-one");
        var request = Request(
            Array.Empty<AgentMessage>(),
            ModelTransport.Auto,
            ModelCacheRetention.Short,
            "protocol-failure");

        var error = await Assert.ThrowsAnyAsync<IOException>(async () =>
            await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken)));

        Assert.Contains("usage_limit_reached", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, http.Calls);
        Assert.Equal(1, factory.Calls);
        var statistics = provider.GetWebSocketStatistics("protocol-failure")!;
        Assert.Equal(0, statistics.Failures);
        Assert.False(statistics.FallbackActive);
    }

    [Fact]
    public async Task NonCooperativeConnectTimesOutAndDisposesLateConnection()
    {
        var http = new CountingHandler();
        var pending = new TaskCompletionSource<IOpenAIWebSocketConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateConnection = new ScriptedConnection((_, _) => new object[] { Completed("late") });
        var options = OpenAICodexResponses.CreateOptions(new HttpClient(http), Token("account-one"));
        options.WebSocketConnectionFactory = (_, _) => new ValueTask<IOpenAIWebSocketConnection>(pending.Task);
        using var provider = new OpenAIResponsesProvider(options);
        var request = Request(
            Array.Empty<AgentMessage>(),
            ModelTransport.Auto,
            ModelCacheRetention.Short,
            "connect-timeout");
        request.Parameters.WebSocketConnectTimeoutMilliseconds = 20;

        var events = await CollectAsync(provider.StreamAsync(
            request,
            TestContext.Current.CancellationToken));
        pending.SetResult(lateConnection);
        for (var attempt = 0; attempt < 100 && lateConnection.DisposeCalls == 0; attempt++)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.True(events[^1].IsTerminal);
        Assert.Equal(1, http.Calls);
        Assert.Equal(1, lateConnection.DisposeCalls);
        Assert.Contains(events[^1].Response!.Diagnostics, value => value.Code == "provider_transport_fallback");
    }

    private static OpenAIResponsesProvider Provider(
        CountingHandler handler,
        OpenAIWebSocketConnectionFactory factory,
        string accountId,
        ProviderResponseObserver? responseObserver = null)
    {
        var options = OpenAICodexResponses.CreateOptions(new HttpClient(handler), Token(accountId));
        options.WebSocketConnectionFactory = factory;
        options.WebSocketIdleTimeoutMilliseconds = 1_000;
        options.ResponseObserver = responseObserver;
        return new OpenAIResponsesProvider(options);
    }

    private static ModelRequest Request(
        IReadOnlyList<AgentMessage> messages,
        ModelTransport transport,
        ModelCacheRetention retention,
        string? sessionId) =>
        new(
            "model",
            string.Empty,
            messages,
            Array.Empty<ToolDefinition>(),
            new ModelParameters
            {
                Transport = transport,
                CacheRetention = retention,
                WebSocketConnectTimeoutMilliseconds = 1_000,
            },
            sessionId,
            Guid.NewGuid().ToString("N"),
            1);

    private static AgentMessage Assistant(ModelResponse response, string model) =>
        new(
            AgentRole.Assistant,
            response.Content,
            DateTimeOffset.UnixEpoch,
            model: model,
            stopReason: response.StopReason,
            usage: response.Usage,
            provider: response.Provider,
            api: response.Api,
            responseModel: response.ResponseModel,
            responseId: response.ResponseId,
            rawStopReason: response.RawStopReason,
            endTurn: response.EndTurn,
            diagnostics: response.Diagnostics);

    private static IReadOnlyList<object> TextResponse(string responseId, string messageId, string text) =>
        new object[]
        {
            JsonSerializer.Serialize(new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = new { type = "message", id = messageId, role = "assistant", status = "in_progress" },
            }),
            JsonSerializer.Serialize(new
            {
                type = "response.output_text.delta",
                output_index = 0,
                delta = text,
            }),
            JsonSerializer.Serialize(new
            {
                type = "response.output_item.done",
                output_index = 0,
                item = new
                {
                    type = "message",
                    id = messageId,
                    role = "assistant",
                    status = "completed",
                    content = new[] { new { type = "output_text", text } },
                },
            }),
            Completed(responseId),
        };

    private static string Completed(string responseId) => JsonSerializer.Serialize(new
    {
        type = "response.completed",
        response = new
        {
            id = responseId,
            model = "model",
            status = "completed",
            usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 },
        },
    });

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in stream)
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static string Token(string accountId)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = accountId,
            },
        });
        return Base64Url("{\"alg\":\"none\"}") + "." + Base64Url(payload) + ".signature";
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class QueueFactory
    {
        private readonly Queue<IOpenAIWebSocketConnection> _connections;

        public QueueFactory(params IOpenAIWebSocketConnection[] connections)
        {
            _connections = new Queue<IOpenAIWebSocketConnection>(connections);
        }

        public int Calls { get; private set; }

        public List<OpenAIWebSocketConnectRequest> Requests { get; } = new();

        public async ValueTask<IOpenAIWebSocketConnection> ConnectAsync(
            OpenAIWebSocketConnectRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Requests.Add(request);
            return _connections.Dequeue();
        }

        public static implicit operator OpenAIWebSocketConnectionFactory(QueueFactory factory) =>
            factory.ConnectAsync;
    }

    private sealed class ScriptedConnection :
        IOpenAIWebSocketConnection,
        IOpenAIWebSocketResponseMetadata
    {
        private readonly Func<int, string, IReadOnlyList<object>> _script;
        private readonly Queue<object> _events = new();
        private bool _open = true;
        private int _sendCount;

        public ScriptedConnection(
            Func<int, string, IReadOnlyList<object>> script,
            IReadOnlyDictionary<string, string>? handshakeHeaders = null)
        {
            _script = script;
            HandshakeHeaders = handshakeHeaders
                               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public bool IsOpen => _open;

        public int HandshakeStatusCode => 101;

        public IReadOnlyDictionary<string, string> HandshakeHeaders { get; }

        public int DisposeCalls { get; private set; }

        public List<string> SentBodies { get; } = new();

        public ValueTask SendTextAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentBodies.Add(text);
            foreach (var item in _script(++_sendCount, text))
            {
                _events.Enqueue(item);
            }

            return default;
        }

        public ValueTask<string> ReceiveTextAsync(
            int maximumCharacters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = _events.Dequeue();
            if (item is Exception exception)
            {
                return ValueTask.FromException<string>(exception);
            }

            var text = Assert.IsType<string>(item);
            Assert.True(text.Length <= maximumCharacters);
            return new ValueTask<string>(text);
        }

        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _open = false;
            return default;
        }

        public void Dispose()
        {
            DisposeCalls++;
            _open = false;
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: " + Completed("sse-response") + "\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            });
        }
    }
}

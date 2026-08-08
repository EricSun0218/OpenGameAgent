using System.Net;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.ProviderTransport;
using Xunit;

namespace OpenGameAgent.Providers.Anthropic.Tests;

public sealed class AnthropicMessagesProviderTests
{
    [Fact]
    public async Task StreamsThinkingTextToolCallsAndDetailedUsage()
    {
        const string stream = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","model":"served-model","usage":{"input_tokens":10,"output_tokens":1,"cache_read_input_tokens":2,"cache_creation_input_tokens":3,"cache_creation":{"ephemeral_1h_input_tokens":1}}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"plan"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"opaque"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"hello"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: content_block_start
            data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"tool_1","name":"move","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"x\":1}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":2}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":4,"output_tokens_details":{"thinking_tokens":1}}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var provider = Create(new StubHandler(_ => Response(stream)));

        var events = await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        Assert.Equal(ModelStopReason.ToolUse, response.StopReason);
        Assert.Equal("msg_1", response.ResponseId);
        Assert.Equal("served-model", response.ResponseModel);
        Assert.Equal("tool_use", response.RawStopReason);
        Assert.Equal("plan", Assert.IsType<ReasoningContent>(response.Content[0]).Text);
        Assert.Equal("opaque", Assert.IsType<ReasoningContent>(response.Content[0]).Signature);
        Assert.Equal("hello", Assert.IsType<TextContent>(response.Content[1]).Text);
        Assert.Equal("{\"x\":1}", Assert.IsType<ToolCallContent>(response.Content[2]).ArgumentsJson);
        Assert.Equal(10, response.Usage.InputTokens);
        Assert.Equal(2, response.Usage.CacheReadTokens);
        Assert.Equal(3, response.Usage.CacheWriteTokens);
        Assert.Equal(1, response.Usage.CacheWriteOneHourTokens);
        Assert.Equal(1, response.Usage.ReasoningTokens);

        var terminalReasoning = Assert.IsType<ReasoningContent>(response.Content[0]);
        var reasoningEnded = Assert.Single(events, item => item.Kind == ModelStreamEventKind.ReasoningEnded);
        Assert.Equal(terminalReasoning.Text, reasoningEnded.Content);
        var endedReasoning = Assert.IsType<ReasoningContent>(reasoningEnded.Partial!.Content[reasoningEnded.ContentIndex]);
        Assert.Equal(terminalReasoning.Text, endedReasoning.Text);
        Assert.Equal(terminalReasoning.Signature, endedReasoning.Signature);

        var textEnded = Assert.Single(events, item => item.Kind == ModelStreamEventKind.TextEnded);
        Assert.Equal(Assert.IsType<TextContent>(response.Content[1]).Text, textEnded.Content);
        Assert.Equal(
            Assert.IsType<TextContent>(response.Content[1]).Text,
            Assert.IsType<TextContent>(textEnded.Partial!.Content[textEnded.ContentIndex]).Text);

        var toolStarted = Assert.Single(events, item => item.Kind == ModelStreamEventKind.ToolCallStarted);
        var toolDeltas = events.Where(item => item.Kind == ModelStreamEventKind.ToolCallDelta).ToArray();
        Assert.NotEmpty(toolDeltas);
        var toolEnded = Assert.Single(events, item => item.Kind == ModelStreamEventKind.ToolCallEnded);
        var toolEvents = events.Where(item => item.Kind is
            ModelStreamEventKind.ToolCallStarted or
            ModelStreamEventKind.ToolCallDelta or
            ModelStreamEventKind.ToolCallEnded);
        Assert.All(toolEvents, item =>
        {
            Assert.Equal(toolStarted.ContentIndex, item.ContentIndex);
            var partialToolCall = Assert.IsType<ToolCallContent>(item.Partial!.Content[item.ContentIndex]);
            AssertJsonObject(partialToolCall.ArgumentsJson);
        });

        var terminalToolCall = Assert.IsType<ToolCallContent>(response.Content[2]);
        var endedToolCall = Assert.IsType<ToolCallContent>(toolEnded.ToolCall);
        var endedPartialToolCall = Assert.IsType<ToolCallContent>(toolEnded.Partial!.Content[toolEnded.ContentIndex]);
        Assert.Equal(terminalToolCall.Id, endedToolCall.Id);
        Assert.Equal(terminalToolCall.Name, endedToolCall.Name);
        Assert.Equal("{\"x\":1}", endedToolCall.ArgumentsJson);
        Assert.Equal(terminalToolCall.ArgumentsJson, endedToolCall.ArgumentsJson);
        Assert.Equal(terminalToolCall.ThoughtSignature, endedToolCall.ThoughtSignature);
        Assert.Equal(terminalToolCall.Namespace, endedToolCall.Namespace);
        Assert.Equal(endedToolCall.Id, toolEnded.ToolCallId);
        Assert.Equal(endedToolCall.Name, toolEnded.ToolName);
        AssertToolCallEqual(endedToolCall, endedPartialToolCall);
    }

    [Fact]
    public async Task SerializesCacheAdaptiveThinkingImagesStrictAndReferencedTools()
    {
        var handler = new StubHandler(_ => Response("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","model":"model","usage":{"input_tokens":0,"output_tokens":0}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":0}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        var options = Options(new HttpClient(handler));
        options.SupportsToolReferences = true;
        options.SupportsStrictTools = true;
        options.ForceAdaptiveThinking = true;
        var provider = new AnthropicMessagesProvider(options);
        var inspect = new ToolDefinition("inspect", "Inspect", "{\"type\":\"object\"}");
        var move = new ToolDefinition(
            "move",
            "Move",
            "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"}},\"required\":[\"x\"]}",
            ToolConstrainedSampling.JsonSchema(ToolSchemaStrictness.Require));
        var call = new ToolCallContent("tool_1", "inspect", "{}");
        var request = new ModelRequest(
            "model",
            "rules",
            new AgentMessage[]
            {
                new(
                    AgentRole.User,
                    new AgentContent[]
                    {
                        new TextContent("look"),
                        new BinaryContent(AgentMediaKind.Image, "aW1hZ2U=", "image/png"),
                    },
                    DateTimeOffset.UnixEpoch),
                new(
                    AgentRole.Assistant,
                    new AgentContent[] { call },
                    DateTimeOffset.UnixEpoch,
                    model: "model",
                    stopReason: ModelStopReason.ToolUse,
                    provider: "anthropic",
                    api: "anthropic-messages"),
                AgentMessage.ToolResult(
                    call,
                    new ToolResult(new AgentContent[] { new TextContent("clear") }, addedToolNames: new[] { "move" }),
                    DateTimeOffset.UnixEpoch),
            },
            new[] { inspect, move },
            new ModelParameters
            {
                ReasoningLevel = "high",
                CacheRetention = ModelCacheRetention.Long,
            },
            "session",
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("1h", root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        Assert.Contains("aW1hZ2U=", handler.RequestBody, StringComparison.Ordinal);
        var tools = root.GetProperty("tools");
        Assert.False(tools[0].TryGetProperty("defer_loading", out _));
        Assert.True(tools[1].GetProperty("strict").GetBoolean());
        Assert.True(tools[1].GetProperty("defer_loading").GetBoolean());
        Assert.Contains("tool_reference", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMismatchedSseAndJsonEventTypes()
    {
        var provider = Create(new StubHandler(_ => Response("""
            event: message_start
            data: {"type":"message_stop"}

            """)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsStreamWithoutMessageStop()
    {
        var provider = Create(new StubHandler(_ => Response("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","model":"model","usage":{"input_tokens":0,"output_tokens":0}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":0}}

            """)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Contains("message_stop", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObservesSanitizedFailureMetadataAndHonorsRetryDirective()
    {
        ProviderResponseObservation? observed = null;
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited", Encoding.UTF8, "text/plain"),
        };
        response.Headers.TryAddWithoutValidation("x-request-id", "request-1");
        response.Headers.TryAddWithoutValidation("x-should-retry", "false");
        response.Headers.TryAddWithoutValidation("retry-after-ms", "2000");
        response.Headers.TryAddWithoutValidation("set-cookie", "secret=value");
        var options = Options(new HttpClient(new StubHandler(_ => response)));
        options.ResponseObserver = (value, _) =>
        {
            observed = value;
            return default;
        };
        var provider = new AnthropicMessagesProvider(options);

        var failure = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.NotNull(observed);
        Assert.Equal(429, observed!.StatusCode);
        Assert.Equal("request-1", observed.Metadata["x-request-id"]);
        Assert.DoesNotContain(observed.Metadata.Keys, key => key.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        Assert.False(failure.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(2), failure.RetryAfter);
        Assert.Equal(429, failure.StatusCode);
    }

    [Fact]
    public async Task TombstoneSuppressesOptionalAffinityButCannotDeleteRequiredVersionOrCredential()
    {
        var handler = new StubHandler(_ => Response("""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","model":"model","usage":{"input_tokens":0,"output_tokens":0}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":0}}

            event: message_stop
            data: {"type":"message_stop"}

            """));
        var options = Options(new HttpClient(handler));
        options.ApiKey = "test-key";
        options.SendSessionAffinityHeaders = true;
        options.Headers["x-session-affinity"] = null;
        options.Headers["anthropic-version"] = null;
        options.Headers["x-api-key"] = null;
        var request = new ModelRequest(
            "model",
            "rules",
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters { CacheRetention = ModelCacheRetention.Short },
            "session",
            "run",
            1);

        await CollectAsync(new AnthropicMessagesProvider(options).StreamAsync(
            request,
            TestContext.Current.CancellationToken));

        Assert.Null(handler.Header("x-session-affinity"));
        Assert.Equal(options.ApiVersion, handler.Header("anthropic-version"));
        Assert.Equal("test-key", handler.Header("x-api-key"));
    }

    private static AnthropicMessagesProvider Create(HttpMessageHandler handler) =>
        new(Options(new HttpClient(handler)));

    private static AnthropicMessagesProviderOptions Options(HttpClient client) =>
        new(client, new Uri("https://api.example.test/v1/messages"));

    private static ModelRequest Request() =>
        new("model", "rules", Array.Empty<AgentMessage>(), Array.Empty<ToolDefinition>(), new ModelParameters(), null, "run", 1);

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        return events;
    }

    private static void AssertJsonObject(string value)
    {
        using var document = JsonDocument.Parse(value);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    private static void AssertToolCallEqual(ToolCallContent expected, ToolCallContent actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.ArgumentsJson, actual.ArgumentsJson);
        Assert.Equal(expected.ThoughtSignature, actual.ThoughtSignature);
        Assert.Equal(expected.Namespace, actual.Namespace);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        public string? RequestBody { get; private set; }

        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers)
            {
                _headers[header.Key] = string.Join(",", header.Value);
            }

            return _response(request);
        }
    }
}

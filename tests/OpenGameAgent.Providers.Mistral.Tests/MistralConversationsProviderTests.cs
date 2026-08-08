using System.Net;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Providers.Mistral.Tests;

public sealed class MistralConversationsProviderTests
{
    [Fact]
    public async Task StreamsThinkingTextIncrementalToolsAndCachedUsage()
    {
        const string stream = """
            data: {"id":"response-1","model":"served-model","choices":[{"delta":{"content":[{"type":"thinking","thinking":[{"type":"text","text":"plan"}]},{"type":"text","text":"hello"}],"tool_calls":[{"index":0,"id":"D681PevKs","function":{"name":"move","arguments":"{\"x\":"}}]}}]}

            data: {"id":"response-1","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":12,"completion_tokens":4,"total_tokens":16,"prompt_tokens_details":{"cached_tokens":2}}}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(stream)));

        var events = await CollectAsync(provider.StreamAsync(Request("devstral-medium-latest"), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        Assert.Equal(ModelStopReason.ToolUse, response.StopReason);
        Assert.Equal("response-1", response.ResponseId);
        Assert.Equal("served-model", response.ResponseModel);
        Assert.Equal("tool_calls", response.RawStopReason);
        Assert.Equal("plan", Assert.IsType<ReasoningContent>(response.Content[0]).Text);
        Assert.Equal("hello", Assert.IsType<TextContent>(response.Content[1]).Text);
        var call = Assert.IsType<ToolCallContent>(response.Content[2]);
        Assert.Equal("D681PevKs", call.Id);
        Assert.Equal("move", call.Name);
        Assert.Equal("{\"x\":1}", call.ArgumentsJson);
        Assert.Equal(10, response.Usage.InputTokens);
        Assert.Equal(2, response.Usage.CacheReadTokens);
        Assert.Equal(4, response.Usage.OutputTokens);
        Assert.Contains(events, value => value.Kind == ModelStreamEventKind.ToolCallEnded);

        var reasoningEnded = Assert.Single(events, item => item.Kind == ModelStreamEventKind.ReasoningEnded);
        Assert.Equal(Assert.IsType<ReasoningContent>(response.Content[0]).Text, reasoningEnded.Content);
        Assert.Equal(
            Assert.IsType<ReasoningContent>(response.Content[0]).Text,
            Assert.IsType<ReasoningContent>(reasoningEnded.Partial!.Content[reasoningEnded.ContentIndex]).Text);

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
    public async Task SerializesCachingStrictToolsImagesAndCrossProviderIds()
    {
        var handler = new StubHandler(_ => Response(StopStream()));
        var options = Options(new HttpClient(handler));
        options.ToolChoice = MistralToolChoice.Function;
        options.RequiredToolName = "inspect";
        var provider = new MistralConversationsProvider(options);
        var tool = new ToolDefinition(
            "inspect",
            "Inspect",
            "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"}}}",
            ToolConstrainedSampling.JsonSchema(ToolSchemaStrictness.Require));
        var call = new ToolCallContent("foreign|long|call", "inspect", "{\"x\":1}");
        var request = new ModelRequest(
            "mistral-large-latest",
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
                    new AgentContent[] { new ReasoningContent("plan"), call },
                    DateTimeOffset.UnixEpoch,
                    model: "other-model",
                    stopReason: ModelStopReason.ToolUse,
                    provider: "other",
                    api: "other-api"),
                AgentMessage.ToolResult(
                    call,
                    new ToolResult(new AgentContent[]
                    {
                        new TextContent("clear"),
                        new BinaryContent(AgentMediaKind.Image, "dG9vbA==", "image/png"),
                    }),
                    DateTimeOffset.UnixEpoch),
            },
            new[] { tool },
            new ModelParameters { ReasoningLevel = "high", CacheRetention = ModelCacheRetention.Short },
            "session-123",
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("session-123", handler.Affinity);
        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.Equal("session-123", root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal("reasoning", root.GetProperty("prompt_mode").GetString());
        Assert.Equal("inspect", root.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
        Assert.True(root.GetProperty("tools")[0].GetProperty("function").GetProperty("strict").GetBoolean());
        Assert.Contains("aW1hZ2U=", handler.RequestBody, StringComparison.Ordinal);
        var normalized = root.GetProperty("messages")[2].GetProperty("tool_calls")[0].GetProperty("id").GetString();
        Assert.NotNull(normalized);
        Assert.Equal(9, normalized!.Length);
        Assert.DoesNotContain("|", normalized, StringComparison.Ordinal);
        Assert.Equal(normalized, root.GetProperty("messages")[3].GetProperty("tool_call_id").GetString());
    }

    [Theory]
    [InlineData("mistral-small-latest", "reasoning_effort")]
    [InlineData("mistral-medium-3.5", "reasoning_effort")]
    [InlineData("magistral-medium-latest", "prompt_mode")]
    public async Task SelectsModelSpecificReasoningControl(string model, string expectedProperty)
    {
        var handler = new StubHandler(_ => Response(StopStream()));
        var provider = Create(handler);
        var request = new ModelRequest(
            model,
            string.Empty,
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters { ReasoningLevel = "medium" },
            null,
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(document.RootElement.TryGetProperty(expectedProperty, out _));
    }

    [Fact]
    public async Task PreservesUnknownFinishReasonAsFailedTerminal()
    {
        var provider = Create(new StubHandler(_ => Response("""
            data: {"id":"response-1","choices":[{"delta":{},"finish_reason":"unmapped_error"}]}

            """)));

        var events = await CollectAsync(provider.StreamAsync(Request("model"), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        Assert.Equal(ModelStreamEventKind.Failed, events.Last().Kind);
        Assert.Equal(ModelStopReason.Error, response.StopReason);
        Assert.Equal("unmapped_error", response.RawStopReason);
        Assert.Equal("Provider stopped with: unmapped_error", response.ErrorMessage);
    }

    [Fact]
    public async Task RejectsStreamWithoutFinishReason()
    {
        var provider = Create(new StubHandler(_ => Response("""
            data: {"id":"response-1","choices":[{"delta":{"content":"hello"}}]}

            """)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request("model"), TestContext.Current.CancellationToken)));
        Assert.Contains("finish reason", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadResponseObserverCannotBreakSuccessfulStream()
    {
        var options = Options(new HttpClient(new StubHandler(_ => Response(StopStream()))));
        options.ResponseObserver = (_, _) => throw new InvalidOperationException("observer failed");
        var provider = new MistralConversationsProvider(options);

        var events = await CollectAsync(provider.StreamAsync(Request("model"), TestContext.Current.CancellationToken));

        Assert.Equal(ModelStopReason.Stop, events.Last().Response!.StopReason);
    }

    [Fact]
    public async Task TombstoneSuppressesOptionalAffinityButCannotDeleteCredential()
    {
        var handler = new StubHandler(_ => Response(StopStream()));
        var options = Options(new HttpClient(handler));
        options.Headers["x-affinity"] = null;
        options.Headers["Authorization"] = null;
        var request = new ModelRequest(
            "model",
            string.Empty,
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters { CacheRetention = ModelCacheRetention.Short },
            "session",
            "run",
            1);

        await CollectAsync(new MistralConversationsProvider(options).StreamAsync(
            request,
            TestContext.Current.CancellationToken));

        Assert.Null(handler.Affinity);
        Assert.Equal("Bearer test-key", handler.Authorization);
    }

    [Fact]
    public async Task ResponseObserverIsSnapshottedAtConstruction()
    {
        var handler = new StubHandler(_ => Response(StopStream()));
        var options = Options(new HttpClient(handler));
        var original = 0;
        var replacement = 0;
        options.ResponseObserver = (_, _) =>
        {
            Interlocked.Increment(ref original);
            return default;
        };
        var provider = new MistralConversationsProvider(options);
        options.ResponseObserver = (_, _) =>
        {
            Interlocked.Increment(ref replacement);
            return default;
        };

        await CollectAsync(provider.StreamAsync(Request("model"), TestContext.Current.CancellationToken));

        Assert.Equal(1, original);
        Assert.Equal(0, replacement);
    }

    private static MistralConversationsProvider Create(HttpMessageHandler handler) =>
        new(Options(new HttpClient(handler)));

    private static MistralConversationsProviderOptions Options(HttpClient client) =>
        new(client, new Uri("https://api.mistral.ai/v1/chat/completions")) { ApiKey = "test-key" };

    private static ModelRequest Request(string model) =>
        new(model, string.Empty, Array.Empty<AgentMessage>(), Array.Empty<ToolDefinition>(), new ModelParameters(), null, "run", 1);

    private static string StopStream() => """
        data: {"id":"response-1","choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}

        """;

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

        public string? Affinity { get; private set; }

        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Affinity = request.Headers.TryGetValues("x-affinity", out var values) ? values.Single() : null;
            Authorization = request.Headers.Authorization?.ToString();
            return _response(request);
        }
    }
}

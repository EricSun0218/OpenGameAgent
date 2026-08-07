using System.Net;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Providers.OpenAICompatible.Tests;

public sealed class ProviderTests
{
    [Fact]
    public async Task StreamsReasoningTextToolArgumentsAndUsage()
    {
        const string stream = """
            data: {"choices":[{"delta":{"role":"assistant"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"reasoning_content":"think"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"hello"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"move","arguments":"{\"speed\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1.5}"}}]},"finish_reason":null}]}

            data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":4,"prompt_tokens_details":{"cached_tokens":3}}}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream"));
        var provider = Create(handler);

        var events = await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        Assert.Equal(ModelStopReason.ToolUse, response.StopReason);
        Assert.Equal("think", Assert.IsType<ReasoningContent>(response.Content[0]).Text);
        Assert.Equal("hello", Assert.IsType<TextContent>(response.Content[1]).Text);
        var call = Assert.IsType<ToolCallContent>(response.Content[2]);
        Assert.Equal("move", call.Name);
        Assert.Equal("{\"speed\":1.5}", call.ArgumentsJson);
        Assert.Equal(7, response.Usage.InputTokens);
        Assert.Equal(4, response.Usage.OutputTokens);
        Assert.Equal(3, response.Usage.CacheReadTokens);
        Assert.Equal(14, response.Usage.TotalTokens);
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.TextDelta && item.Delta == "hello");
        var toolDelta = Assert.Single(events, item => item.Kind == ModelStreamEventKind.ToolCallDelta && item.Delta == "{\"speed\":");
        Assert.Equal("call-1", toolDelta.ToolCallId);
        Assert.Equal("move", toolDelta.ToolName);
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.ReasoningEnded);
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.TextEnded);
        var toolEnded = Assert.Single(events, item => item.Kind == ModelStreamEventKind.ToolCallEnded);
        Assert.Equal("call-1", toolEnded.ToolCallId);
        Assert.Equal("move", toolEnded.ToolName);
    }

    [Fact]
    public async Task SendsToolsExtensionsAndRotatingAuthorizationWithoutLeakingItIntoBody()
    {
        var handler = new StubHandler(_ => Response(
            HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            "text/event-stream"));
        var options = new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions"))
        {
            GetApiKeyAsync = _ => new ValueTask<string?>("secret-key"),
        };
        var provider = new OpenAICompatibleProvider(options);
        var parameters = new ModelParameters
        {
            Temperature = 0.25,
            MaxOutputTokens = 321,
            ReasoningLevel = "high",
            Extensions = new Dictionary<string, string>
            {
                ["top_p"] = "0.8",
            },
        };
        var request = new ModelRequest(
            "model",
            "rules",
            new AgentMessage[]
            {
                AgentMessage.UserJson("{\"hp\":2.5}"),
                new(
                    AgentRole.Assistant,
                    new AgentContent[] { new ReasoningContent("private-plan"), new TextContent("public-answer") },
                    DateTimeOffset.UnixEpoch,
                    model: "model",
                    stopReason: ModelStopReason.Stop),
            },
            new[] { new ToolDefinition("move", "Move", "{\"type\":\"object\"}") },
            parameters,
            "session",
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("Bearer secret-key", handler.Authorization);
        Assert.DoesNotContain("secret-key", handler.RequestBody, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(0.25, document.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(321, document.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("high", document.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0.8, document.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal("move", document.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Contains("2.5", document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("public-answer", document.RootElement.GetProperty("messages")[2].GetProperty("content").GetString());
        Assert.DoesNotContain("private-plan", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureBecomesProviderFailureWithoutIncludingApiKey()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.TooManyRequests, "rate limited", "text/plain"));
        var options = new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions"))
        {
            ApiKey = "do-not-expose",
        };
        var provider = new OpenAICompatibleProvider(options);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("429", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-expose", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedStreamWithoutFinishReasonFails()
    {
        var handler = new StubHandler(_ => Response(
            HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n",
            "text/event-stream"));
        var provider = Create(handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("ended before", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestBodyIsBoundedBeforeTransport()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("transport must not run"));
        var options = new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions"))
        {
            MaxRequestBytes = 100,
        };
        var provider = new OpenAICompatibleProvider(options);
        var request = new ModelRequest(
            "model",
            new string('x', 200),
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            null,
            "run",
            1);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken)));

        Assert.Contains("byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.RequestBody);
    }

    [Fact]
    public async Task AccumulatedStreamingResponseIsBoundedAcrossEvents()
    {
        const string stream = """
            data: {"choices":[{"delta":{"content":"abcd"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"efgh"},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream"));
        var provider = new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions"))
        {
            MaxResponseCharacters = 6,
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("accumulated", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DistinctStreamingToolCallsAreBoundedEvenWhenTheyContainNoText()
    {
        const string stream = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0},{"index":1},{"index":2}]} ,"finish_reason":null}]}

            data: [DONE]

            """;
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream"));
        var provider = new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions"))
        {
            MaxToolCallsPerResponse = 2,
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("tool call limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeStreamingToolCallIndexIsRejected()
    {
        const string stream = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":-1}]} ,"finish_reason":null}]}

            data: [DONE]

            """;
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream"));
        var provider = Create(handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("negative", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LengthTruncatedToolArgumentsStillProduceAClosableToolCall()
    {
        const string stream = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call","function":{"name":"move","arguments":"{\"x\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"length"}]}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream")));

        var events = await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        Assert.Equal(ModelStopReason.Length, response.StopReason);
        Assert.Equal("{}", Assert.IsType<ToolCallContent>(Assert.Single(response.Content)).ArgumentsJson);
    }

    [Fact]
    public async Task StreamingToolCallCannotChangeIdentity()
    {
        const string stream = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"first","function":{"name":"move","arguments":"{"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"second","function":{"arguments":"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("changed its ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedOrInconsistentUsageIsRejected()
    {
        const string stream = """
            data: {"choices":[],"usage":{"prompt_tokens":2,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":3}}}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("cannot exceed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateStreamingPropertiesAreRejected()
    {
        const string stream = """
            data: {"choices":[],"choices":[]}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"choices\":{}}", "choices")]
    [InlineData("{\"choices\":[{\"delta\":[],\"finish_reason\":null}]}", "delta")]
    [InlineData("{\"choices\":[{\"delta\":{\"content\":42},\"finish_reason\":null}]}", "content")]
    [InlineData("{\"choices\":[{\"delta\":{\"tool_calls\":{}},\"finish_reason\":null}]}", "tool calls")]
    [InlineData("{\"choices\":[],\"usage\":1}", "usage")]
    public async Task MalformedStreamingShapesAreRejectedAsProtocolErrors(string payload, string expected)
    {
        var stream = "data: " + payload + "\n\ndata: [DONE]\n\n";
        var provider = Create(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultipleStreamingChoicesAreRejectedInsteadOfSilentlyDiscarded()
    {
        const string stream = """
            data: {"choices":[{"delta":{"content":"first"},"finish_reason":"stop"},{"delta":{"content":"second"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("multiple choices", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompletedToolCallRequiresProviderIdentityAndName()
    {
        const string stream = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"move","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("missing its ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderLineBreaksAreRejectedBeforeTransport()
    {
        var options = new OpenAICompatibleProviderOptions(
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("transport must not run"))),
            new Uri("https://example.test/v1/chat/completions"));
        options.Headers["X-Test"] = "safe\r\ninjected: value";

        Assert.Throws<ArgumentException>(() => new OpenAICompatibleProvider(options));
    }

    [Fact]
    public void InvalidOrDuplicateAuthenticationHeadersAreRejectedBeforeTransport()
    {
        var invalid = new OpenAICompatibleProviderOptions(
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("transport must not run"))),
            new Uri("https://example.test/v1/chat/completions"));
        invalid.Headers["Bad:Name"] = "value";
        Assert.Throws<ArgumentException>(() => new OpenAICompatibleProvider(invalid));

        var duplicate = new OpenAICompatibleProviderOptions(
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("transport must not run"))),
            new Uri("https://example.test/v1/chat/completions"))
        {
            ApiKey = "secret",
        };
        duplicate.Headers["Authorization"] = "other";
        Assert.Throws<ArgumentException>(() => new OpenAICompatibleProvider(duplicate));
    }

    [Fact]
    public async Task DynamicApiKeyLineBreaksAreRejectedBeforeTransport()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("transport must not run"));
        var provider = new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions"))
        {
            GetApiKeyAsync = _ => new ValueTask<string?>("safe\r\ninjected: value"),
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Null(handler.RequestBody);
    }

    [Fact]
    public async Task ChoiceAfterFinishReasonIsRejected()
    {
        const string stream = """
            data: {"choices":[{"delta":{"content":"done"},"finish_reason":"stop"}]}

            data: {"choices":[{"delta":{"content":"late"},"finish_reason":null}]}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("after its finish reason", exception.Message, StringComparison.Ordinal);
    }

    private static OpenAICompatibleProvider Create(HttpMessageHandler handler) =>
        new(new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions")));

    private static ModelRequest Request() =>
        new("model", "rules", Array.Empty<AgentMessage>(), Array.Empty<ToolDefinition>(), new ModelParameters(), null, "run", 1);

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string mediaType) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        return events;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        public string? RequestBody { get; private set; }

        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.TryGetValues("Authorization", out var values)
                ? Assert.Single(values)
                : null;
            return _response(request);
        }
    }
}

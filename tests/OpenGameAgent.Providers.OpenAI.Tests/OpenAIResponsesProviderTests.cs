using System.Net;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.ProviderTransport;
using Xunit;

namespace OpenGameAgent.Providers.OpenAI.Tests;

public sealed class OpenAIResponsesProviderTests
{
    [Fact]
    public async Task PreservesInterleavedCommentaryReasoningAndFinalAnswerBlocks()
    {
        const string stream = """
            data: {"type":"response.created","response":{"id":"resp_interleaved","model":"served-model"}}

            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_commentary","role":"assistant","status":"in_progress","content":[]}}

            data: {"type":"response.output_text.delta","output_index":0,"delta":"I will inspect that."}

            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_commentary","role":"assistant","status":"completed","phase":"commentary","content":[{"type":"output_text","text":"I will inspect that.","annotations":[]}]}}

            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"reasoning","id":"rs_1","summary":[]}}

            data: {"type":"response.reasoning_summary_text.delta","output_index":1,"delta":"private step"}

            data: {"type":"response.output_item.done","output_index":1,"item":{"type":"reasoning","id":"rs_1","summary":[{"text":"private step"}],"encrypted_content":"opaque"}}

            data: {"type":"response.output_item.added","output_index":2,"item":{"type":"message","id":"msg_final","role":"assistant","status":"in_progress","content":[]}}

            data: {"type":"response.output_text.delta","output_index":2,"delta":"Done."}

            data: {"type":"response.output_item.done","output_index":2,"item":{"type":"message","id":"msg_final","role":"assistant","status":"completed","phase":"final_answer","content":[{"type":"output_text","text":"Done.","annotations":[]}]}}

            data: {"type":"response.completed","response":{"id":"resp_interleaved","model":"served-model","status":"completed","output":[],"usage":{"input_tokens":1,"output_tokens":3,"total_tokens":4}}}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(stream)));

        var events = await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        Assert.Collection(
            response.Content,
            content => Assert.Equal(AgentTextPhase.Commentary, Assert.IsType<TextContent>(content).Phase),
            content => Assert.Equal("private step", Assert.IsType<ReasoningContent>(content).Text),
            content => Assert.Equal(AgentTextPhase.FinalAnswer, Assert.IsType<TextContent>(content).Phase));
        Assert.Equal(
            new[]
            {
                ModelStreamEventKind.TextDelta,
                ModelStreamEventKind.ReasoningDelta,
                ModelStreamEventKind.TextDelta,
            },
            events.Where(item => item.Kind is ModelStreamEventKind.TextDelta or ModelStreamEventKind.ReasoningDelta)
                .Select(item => item.Kind));
    }

    [Fact]
    public async Task StreamsReasoningTextToolCallsIdentityAndDetailedUsage()
    {
        const string stream = """
            data: {"type":"response.created","response":{"id":"resp_1","model":"served-model"}}

            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[]}}

            data: {"type":"response.reasoning_summary_text.delta","output_index":0,"delta":"plan"}

            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[{"text":"plan"}],"encrypted_content":"opaque"}}

            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"message","id":"msg_1","role":"assistant","status":"in_progress","content":[]}}

            data: {"type":"response.output_text.delta","output_index":1,"delta":"hello"}

            data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"msg_1","role":"assistant","status":"completed","phase":"final_answer","content":[{"type":"output_text","text":"hello","annotations":[]}]}}

            data: {"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"move","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"x\":1}"}

            data: {"type":"response.function_call_arguments.done","output_index":2,"arguments":"{\"x\":1}"}

            data: {"type":"response.output_item.done","output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"move","arguments":"{\"x\":1}"}}

            data: {"type":"response.completed","response":{"id":"resp_1","model":"served-model","status":"completed","output":[{"type":"reasoning","id":"rs_1","summary":[{"text":"plan"}],"encrypted_content":"opaque"}],"usage":{"input_tokens":10,"output_tokens":4,"total_tokens":14,"input_tokens_details":{"cached_tokens":2,"cache_write_tokens":3},"output_tokens_details":{"reasoning_tokens":1}}}}

            data: [DONE]

            """;
        var provider = Create(new StubHandler(_ => Response(stream)));

        var events = await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        Assert.Equal(ModelStopReason.ToolUse, response.StopReason);
        Assert.Equal("openai", response.Provider);
        Assert.Equal("openai-responses", response.Api);
        Assert.Equal("resp_1", response.ResponseId);
        Assert.Equal("served-model", response.ResponseModel);
        Assert.Equal("completed", response.RawStopReason);
        Assert.True(response.EndTurn);
        var reasoning = Assert.IsType<ReasoningContent>(response.Content[0]);
        Assert.Equal("plan", reasoning.Text);
        Assert.Contains("opaque", reasoning.Signature, StringComparison.Ordinal);
        var text = Assert.IsType<TextContent>(response.Content[1]);
        Assert.Equal("hello", text.Text);
        Assert.Equal(AgentTextPhase.FinalAnswer, text.Phase);
        var call = Assert.IsType<ToolCallContent>(response.Content[2]);
        Assert.Equal("call_1|fc_1", call.Id);
        Assert.Equal("{\"x\":1}", call.ArgumentsJson);
        Assert.Equal(5, response.Usage.InputTokens);
        Assert.Equal(2, response.Usage.CacheReadTokens);
        Assert.Equal(3, response.Usage.CacheWriteTokens);
        Assert.Equal(1, response.Usage.ReasoningTokens);
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.ReasoningDelta && item.Delta == "plan");
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.TextDelta && item.Delta == "hello");
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.ReasoningEnded && item.Content == "plan");
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.TextEnded && item.Content == "hello");
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.ToolCallDelta && item.Delta == "{\"x\":1}");
        var toolEnded = Assert.Single(events, item => item.Kind == ModelStreamEventKind.ToolCallEnded);
        Assert.Equal(call.Id, toolEnded.ToolCallId);
        Assert.Equal(call.Name, toolEnded.ToolName);
        Assert.Equal(call.ArgumentsJson, toolEnded.ToolCall!.ArgumentsJson);
    }

    [Fact]
    public async Task SerializesNativeInputCacheStrictToolsAndDeferredToolLoading()
    {
        var handler = new StubHandler(_ => Response("""
            data: {"type":"response.completed","response":{"id":"resp_1","model":"model","status":"completed","output":[],"usage":{"input_tokens":0,"output_tokens":0,"total_tokens":0}}}

            """));
        var options = Options(new HttpClient(handler));
        options.SupportsStrictTools = true;
        options.SupportsAdditionalTools = true;
        var provider = new OpenAIResponsesProvider(options);
        var initial = new ToolDefinition("inspect", "Inspect", "{\"type\":\"object\"}");
        var loaded = new ToolDefinition("move", "Move", "{\"type\":\"object\"}");
        var call = new ToolCallContent("call_1|fc_1", "inspect", "{}");
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
                    provider: "openai",
                    api: "openai-responses"),
                AgentMessage.ToolResult(
                    call,
                    new ToolResult(new AgentContent[] { new TextContent("clear") }, addedToolNames: new[] { "move" }),
                    DateTimeOffset.UnixEpoch),
            },
            new[] { initial, loaded },
            new ModelParameters
            {
                MaxOutputTokens = 1,
                ReasoningLevel = "high",
                CacheRetention = ModelCacheRetention.Long,
            },
            "session-1",
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.Equal(16, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("24h", root.GetProperty("prompt_cache_retention").GetString());
        Assert.Equal("session-1", root.GetProperty("prompt_cache_key").GetString());
        Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.False(root.GetProperty("tools")[0].GetProperty("strict").GetBoolean());
        Assert.Contains("data:image/png;base64,aW1hZ2U=", handler.RequestBody, StringComparison.Ordinal);
        var additional = Assert.Single(root.GetProperty("input").EnumerateArray(), item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "additional_tools");
        Assert.Equal("move", additional.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(true, true, "additional_tools")]
    [InlineData(false, true, "tool_search_output")]
    [InlineData(false, false, "top_level")]
    public async Task DeferredToolsSelectNativeThenSearchThenTopLevelFallback(
        bool supportsAdditionalTools,
        bool supportsToolSearch,
        string expectedMode)
    {
        var handler = new StubHandler(_ => EmptyCompletedResponse());
        var options = Options(new HttpClient(handler));
        options.SupportsAdditionalTools = supportsAdditionalTools;
        options.SupportsToolSearch = supportsToolSearch;
        var provider = new OpenAIResponsesProvider(options);

        await CollectAsync(provider.StreamAsync(
            DeferredRequest(includeLoadedCall: false),
            TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        var input = root.GetProperty("input").EnumerateArray().ToArray();
        var topLevelNames = root.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();
        var additional = input.Where(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "additional_tools").ToArray();
        var searches = input.Where(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "tool_search_output").ToArray();

        if (expectedMode == "additional_tools")
        {
            Assert.Equal(new[] { "inspect" }, topLevelNames);
            Assert.Equal("move", Assert.Single(additional).GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Empty(searches);
        }
        else if (expectedMode == "tool_search_output")
        {
            Assert.Equal(new[] { "inspect" }, topLevelNames);
            Assert.Empty(additional);
            var search = Assert.Single(searches);
            Assert.Equal("move", search.GetProperty("tools")[0].GetProperty("name").GetString());
            var searchCall = Assert.Single(input, item =>
                item.TryGetProperty("type", out var type) && type.GetString() == "tool_search_call");
            Assert.Equal(searchCall.GetProperty("call_id").GetString(), search.GetProperty("call_id").GetString());
        }
        else
        {
            Assert.Equal(new[] { "inspect", "move" }, topLevelNames);
            Assert.Empty(additional);
            Assert.Empty(searches);
        }
    }

    [Fact]
    public async Task DeferredToolMarkerPrecedesReplayAndIsNotDuplicated()
    {
        var handler = new StubHandler(_ => EmptyCompletedResponse());
        var options = Options(new HttpClient(handler));
        options.SupportsAdditionalTools = true;
        options.SupportsToolSearch = true;
        var provider = new OpenAIResponsesProvider(options);

        await CollectAsync(provider.StreamAsync(
            DeferredRequest(includeLoadedCall: true),
            TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToArray();
        var markerIndexes = input.Select((item, index) => (item, index))
            .Where(value => value.item.TryGetProperty("type", out var type) && type.GetString() == "additional_tools")
            .Select(value => value.index)
            .ToArray();
        var loadedCallIndex = Array.FindIndex(input, item =>
            item.TryGetProperty("type", out var type)
            && type.GetString() == "function_call"
            && item.GetProperty("name").GetString() == "move");

        Assert.Single(markerIndexes);
        Assert.True(markerIndexes[0] < loadedCallIndex);
        Assert.Equal(new[] { "inspect" }, document.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray());
    }

    [Fact]
    public async Task StreamsGrammarCustomToolAsJsonArguments()
    {
        const string stream = """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"custom_tool_call","id":"ctc_1","call_id":"call_1","name":"choose","input":""}}

            data: {"type":"response.custom_tool_call_input.delta","output_index":0,"delta":"ab"}

            data: {"type":"response.custom_tool_call_input.done","output_index":0,"input":"ab"}

            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"custom_tool_call","id":"ctc_1","call_id":"call_1","name":"choose","input":"ab"}}

            data: {"type":"response.completed","response":{"id":"resp_1","model":"model","status":"completed","output":[],"usage":{"input_tokens":0,"output_tokens":0,"total_tokens":0}}}

            """;
        var options = Options(new HttpClient(new StubHandler(_ => Response(stream))));
        options.SupportsGrammarTools = true;
        var provider = new OpenAIResponsesProvider(options);
        var request = new ModelRequest(
            "model",
            "rules",
            Array.Empty<AgentMessage>(),
            new[]
            {
                new ToolDefinition(
                    "choose",
                    "Choose",
                    "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]}",
                    ToolConstrainedSampling.Grammar(openAiRegex: "[a-z]+")),
            },
            new ModelParameters(),
            null,
            "run",
            1);

        var result = (await CollectAsync(provider.StreamAsync(
            request,
            TestContext.Current.CancellationToken))).Last().Response!;

        Assert.Equal(ModelStopReason.ToolUse, result.StopReason);
        Assert.Equal("{\"value\":\"ab\"}", Assert.IsType<ToolCallContent>(Assert.Single(result.Content)).ArgumentsJson);
    }

    [Fact]
    public async Task RejectsStreamWithoutTerminalResponse()
    {
        var provider = Create(new StubHandler(_ => Response("""
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_1","role":"assistant","status":"in_progress","content":[]}}

            data: {"type":"response.output_text.delta","output_index":0,"delta":"partial"}

            """)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Contains("terminal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResponseObserverReceivesOnlySanitizedMetadataAndFailureIsIsolated()
    {
        ProviderResponseObservation? observed = null;
        var response = EmptyCompletedResponse();
        response.Headers.TryAddWithoutValidation("x-request-id", "request-1\r\nforged");
        response.Headers.TryAddWithoutValidation("set-cookie", "credential=secret");
        var options = Options(new HttpClient(new StubHandler(_ => response)));
        options.ResponseObserver = (observation, _) =>
        {
            observed = observation;
            throw new InvalidOperationException("observer failure");
        };

        var events = await CollectAsync(new OpenAIResponsesProvider(options).StreamAsync(
            Request(),
            TestContext.Current.CancellationToken));

        Assert.True(events[^1].IsTerminal);
        Assert.NotNull(observed);
        Assert.Equal(200, observed.StatusCode);
        Assert.Equal("request-1  forged", observed.Metadata["x-request-id"]);
        Assert.Single(observed.Metadata);
    }

    [Fact]
    public async Task FailureRejectsServerRetryDelayAboveSafetyLimit()
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(3);
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("retry", Encoding.UTF8, "text/plain"),
        };
        response.Headers.TryAddWithoutValidation("x-should-retry", "true");
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAt);
        var provider = Create(new StubHandler(_ => response));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.False(exception.IsTransient);
        Assert.Equal(400, exception.StatusCode);
        Assert.InRange(exception.RetryAfter!.Value, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3.1));
    }

    [Fact]
    public async Task NullHeaderSuppressesOptionalSessionDefaultAndTransportHeadersAreRejected()
    {
        var handler = new StubHandler(_ => EmptyCompletedResponse());
        var options = Options(new HttpClient(handler));
        options.Headers["session_id"] = null;
        var provider = new OpenAIResponsesProvider(options);
        var request = new ModelRequest(
            "model",
            "rules",
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters { CacheRetention = ModelCacheRetention.Short },
            "session-one",
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("session_id", handler.RequestHeaders.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("session-one", handler.RequestHeaders["x-client-request-id"]);

        var malicious = Options(new HttpClient(new StubHandler(_ => EmptyCompletedResponse())));
        malicious.Headers["Host"] = "attacker.example";
        Assert.Throws<ArgumentException>(() => new OpenAIResponsesProvider(malicious));

        var credentialHeader = Options(new HttpClient(new StubHandler(_ => EmptyCompletedResponse())));
        credentialHeader.AuthenticationStyle = OpenAIAuthenticationStyle.ApiKeyHeader;
        credentialHeader.ApiKeyHeaderName = "Host";
        credentialHeader.ApiKey = "secret";
        Assert.Throws<ArgumentException>(() => new OpenAIResponsesProvider(credentialHeader));

        credentialHeader.ApiKeyHeaderName = null!;
        Assert.Throws<ArgumentException>(() => new OpenAIResponsesProvider(credentialHeader));
    }

    private static OpenAIResponsesProvider Create(HttpMessageHandler handler) =>
        new(Options(new HttpClient(handler)));

    private static HttpResponseMessage EmptyCompletedResponse() => Response("""
        data: {"type":"response.completed","response":{"id":"resp_1","model":"model","status":"completed","output":[],"usage":{"input_tokens":0,"output_tokens":0,"total_tokens":0}}}

        """);

    private static ModelRequest DeferredRequest(bool includeLoadedCall)
    {
        var inspect = new ToolCallContent("call_inspect|fc_inspect", "inspect", "{}");
        var move = new ToolCallContent("call_move|fc_move", "move", "{}");
        var messages = new List<AgentMessage>
        {
            AgentMessage.User("start", DateTimeOffset.UnixEpoch),
            new(
                AgentRole.Assistant,
                new AgentContent[] { inspect },
                DateTimeOffset.UnixEpoch,
                model: "model",
                stopReason: ModelStopReason.ToolUse,
                provider: "openai",
                api: "openai-responses"),
            AgentMessage.ToolResult(
                inspect,
                new ToolResult(new AgentContent[] { new TextContent("loaded") }, addedToolNames: new[] { "move" }),
                DateTimeOffset.UnixEpoch),
        };
        if (includeLoadedCall)
        {
            messages.Add(new AgentMessage(
                AgentRole.Assistant,
                new AgentContent[] { move },
                DateTimeOffset.UnixEpoch,
                model: "model",
                stopReason: ModelStopReason.ToolUse,
                provider: "openai",
                api: "openai-responses"));
            messages.Add(AgentMessage.ToolResult(
                move,
                new ToolResult(new AgentContent[] { new TextContent("done") }, addedToolNames: new[] { "move" }),
                DateTimeOffset.UnixEpoch));
        }

        messages.Add(AgentMessage.User("continue", DateTimeOffset.UnixEpoch));
        return new ModelRequest(
            "model",
            "rules",
            messages,
            new[]
            {
                new ToolDefinition("inspect", "Inspect", "{\"type\":\"object\"}"),
                new ToolDefinition("move", "Move", "{\"type\":\"object\"}"),
            },
            new ModelParameters(),
            "session",
            "run",
            1);
    }

    private static OpenAIResponsesProviderOptions Options(HttpClient client) =>
        new(client, new Uri("https://api.example.test/v1/responses"));

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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        public string? RequestBody { get; private set; }

        public IReadOnlyDictionary<string, string> RequestHeaders { get; private set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestHeaders = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            return _response(request);
        }
    }
}

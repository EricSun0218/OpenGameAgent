using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using Xunit;

namespace GameAgent.Providers.Anthropic.Tests;

public sealed class AnthropicMessagesStreamingProviderTests
{
    [Fact]
    public async Task StreamsTextAndAccountsCumulativeCacheUsageExactly()
    {
        var provider = CreateProvider(
            new FakeTransport(TextStream(cacheFields: true)),
            new AnthropicProviderOptions
            {
                Model = "claude-test",
                InputUsdPerMillionTokens = "1",
                CacheReadUsdPerMillionTokens = "0.1",
                CacheWrite5mUsdPerMillionTokens = "1.25",
                CacheWrite1hUsdPerMillionTokens = "2",
                OutputUsdPerMillionTokens = "2"
            });

        var events = await CollectAsync(provider.StreamAsync(Request()));

        Assert.Equal(
            new[]
            {
                ModelStreamEventKinds.TextDelta,
                ModelStreamEventKinds.Usage,
                ModelStreamEventKinds.Completed
            },
            events.Select(item => item.Kind));
        Assert.Equal("hello", events[0].TextDelta);
        var usage = Assert.IsType<ProviderUsage>(events[1].Usage);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(3, usage.CacheReadTokens);
        Assert.Equal(2, usage.CacheWriteTokens);
        Assert.Equal(7, usage.CacheMissTokens);
        Assert.Equal(14, usage.ProviderTotalTokens);
        Assert.Equal("0.0000158", usage.CostUsd);
        Assert.Equal(
            UsageAvailabilityStates.CostAvailable,
            usage.Availability);
        Assert.Equal("stop", events[2].FinishReason);
        Assert.Equal(new long[] { 0, 1, 2 }, events.Select(item => item.Ordinal));
    }

    [Fact]
    public async Task ConcatenatesIncrementalToolInputAndMapsToolStop()
    {
        var provider = CreateProvider(
            new FakeTransport(ToolStream(
                "{\"city\":",
                "\"Paris\"}")));

        var events = await CollectAsync(provider.StreamAsync(Request()));
        var toolEvents = events
            .Where(item => item.Kind == ModelStreamEventKinds.ToolCallDelta)
            .ToArray();

        Assert.Equal(3, toolEvents.Length);
        Assert.Equal("toolu_1", toolEvents[0].ToolCallId);
        Assert.Equal("weather", toolEvents[0].ToolNameDelta);
        Assert.Null(toolEvents[0].ArgumentsJsonDelta);
        Assert.Equal(
            """{"city":"Paris"}""",
            string.Concat(
                toolEvents
                    .Select(item => item.ArgumentsJsonDelta)
                    .Where(item => item is not null)));
        Assert.Equal("tool_calls", events[^1].FinishReason);
    }

    [Fact]
    public async Task EncodesNativeMessageAndToolBlocksInRequiredOrder()
    {
        var transport = new FakeTransport(TextStream(cacheFields: true));
        var provider = CreateProvider(transport);
        var toolInput = Json("""{"city":"Paris"}""");
        var toolResult = Json("""{"temperature":18}""");
        var request = Request(
            Message(
                NormalizedRoles.System,
                NormalizedContentPart.FromText("system\nprompt")),
            Message(
                NormalizedRoles.User,
                NormalizedContentPart.FromText("weather?")),
            Message(
                NormalizedRoles.Assistant,
                NormalizedContentPart.FromToolCall(
                    new ModelToolCall
                    {
                        ToolCallId = "toolu_1",
                        Name = "weather",
                        Arguments = toolInput
                    })),
            Message(
                NormalizedRoles.Tool,
                NormalizedContentPart.FromToolResult(
                    "toolu_1",
                    "weather",
                    toolResult)),
            Message(
                NormalizedRoles.User,
                NormalizedContentPart.FromText("briefly")));
        request.Tools = new[]
        {
            new ToolDescriptor
            {
                Name = "weather",
                Version = "1",
                Description = "Get weather",
                ParametersSchema = Json(
                    """{"type":"object","properties":{"city":{"type":"string"}}}""")
            }
        };

        _ = await CollectAsync(provider.StreamAsync(request));

        var sent = Assert.IsType<AnthropicStreamingHttpRequest>(
            transport.LastRequest);
        Assert.Equal(
            "https://api.anthropic.com/v1/messages",
            sent.Uri.AbsoluteUri);
        Assert.Equal("test-secret", sent.ApiKey);
        Assert.Equal("2023-06-01", sent.ApiVersion);
        using var document = JsonDocument.Parse(sent.Body);
        var root = document.RootElement;
        Assert.Equal("claude-test", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(
            "system\nprompt",
            root.GetProperty("system").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var assistantBlock = messages[1]
            .GetProperty("content")[0];
        Assert.Equal(
            "tool_use",
            assistantBlock.GetProperty("type").GetString());
        Assert.Equal(
            JsonValueKind.Object,
            assistantBlock.GetProperty("input").ValueKind);
        var continuation = messages[2].GetProperty("content");
        Assert.Equal(
            "tool_result",
            continuation[0].GetProperty("type").GetString());
        Assert.Equal(
            "text",
            continuation[1].GetProperty("type").GetString());
        Assert.Equal(
            "input_schema",
            root.GetProperty("tools")[0]
                .EnumerateObject()
                .Last()
                .Name);
    }

    [Fact]
    public async Task HonorsPerRequestOutputTokenLimit()
    {
        var transport = new FakeTransport(
            TextStream(cacheFields: true));
        var provider = CreateProvider(
            transport,
            new AnthropicProviderOptions
            {
                Model = "claude-test",
                MaxOutputTokens = 1_000
            });
        var request = Request();
        request.MaxOutputTokens = 37;

        _ = await CollectAsync(provider.StreamAsync(request));

        using var document = JsonDocument.Parse(
            Assert.IsType<AnthropicStreamingHttpRequest>(
                    transport.LastRequest)
                .Body);
        Assert.Equal(
            37,
            document.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task RejectsDuplicatePropertiesInRequestJson()
    {
        var transport = new FakeTransport(
            TextStream(cacheFields: true));
        var provider = CreateProvider(transport);
        var request = Request();
        request.Tools = new[]
        {
            new ToolDescriptor
            {
                Name = "duplicate_schema",
                Version = "1",
                ParametersSchema = Json(
                    """{"type":"object","type":"array"}""")
            }
        };

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(request)));

        Assert.Equal("provider_request_invalid", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task RejectsUnsupportedOpaqueContinuationState()
    {
        var provider = CreateProvider(
            new FakeTransport(TextStream(cacheFields: true)));
        var stateDialect = new ProviderDialectContract(
            "test.state.dialect.v1",
            ProviderRequestFamily.Custom,
            "test.request.v1",
            ProviderStreamFraming.Custom,
            "test.stream.v1",
            "test.tools.v1",
            "test.usage.v1",
            "test.reasoning.v1",
            "application/json",
            "test.state.v1");
        var stateRoute = new ProviderRouteIdentity(
            "state-provider",
            new ProviderRouteMetadata("state-model", stateDialect),
            new ProviderCapabilities());
        var request = Request();
        request.OpaqueContinuationState =
            ProviderOpaqueContinuationState.Bind(
                stateRoute,
                new ProviderOpaqueContinuationUpdate(
                    "test.state.v1",
                    Json("""{"cursor":"next"}""")));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(request)));

        Assert.Equal("provider_request_invalid", error.Code);
        Assert.True(error.UsageKnownToBeZero);
    }

    [Fact]
    public async Task MissingCacheCountersRemainUnavailableInsteadOfBecomingZero()
    {
        var provider = CreateProvider(
            new FakeTransport(TextStream(cacheFields: false)));

        var events = await CollectAsync(provider.StreamAsync(Request()));
        var usage = Assert.IsType<ProviderUsage>(
            events.Single(item => item.Kind == ModelStreamEventKinds.Usage)
                .Usage);

        Assert.Equal(5, usage.InputTokens);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.CacheWriteTokens);
        Assert.Null(usage.CacheMissTokens);
        Assert.Null(usage.ProviderTotalTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostUnavailable,
            usage.Availability);
    }

    [Fact]
    public async Task CacheWriteCostRequiresTtlBreakdown()
    {
        var stream = Event(
                         "message_start",
                         """
                         {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":5,"cache_creation_input_tokens":2,"cache_read_input_tokens":3,"output_tokens":1}}}
                         """)
                     + EmptyTextBlock()
                     + FinalDelta("end_turn", 4)
                     + Event("message_stop", """{"type":"message_stop"}""");
        var provider = CreateProvider(
            new FakeTransport(stream),
            new AnthropicProviderOptions
            {
                Model = "claude-test",
                InputUsdPerMillionTokens = "1",
                CacheReadUsdPerMillionTokens = "0.1",
                CacheWrite5mUsdPerMillionTokens = "1.25",
                CacheWrite1hUsdPerMillionTokens = "2",
                OutputUsdPerMillionTokens = "2"
            });

        var events = await CollectAsync(provider.StreamAsync(Request()));
        var usage = Assert.IsType<ProviderUsage>(
            events.Single(item => item.Kind == ModelStreamEventKinds.Usage)
                .Usage);

        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(2, usage.CacheWriteTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostUnavailable,
            usage.Availability);
        Assert.Equal("0", usage.CostUsd);
    }

    [Fact]
    public async Task IgnoresUnknownBoundedEvents()
    {
        var stream = TextStream(cacheFields: true).Replace(
            "event: content_block_start",
            "event: future_notice\n"
            + """data: {"type":"future_notice","value":1}"""
            + "\n\n"
            + "event: content_block_start",
            StringComparison.Ordinal);
        var provider = CreateProvider(new FakeTransport(stream));

        var events = await CollectAsync(provider.StreamAsync(Request()));

        Assert.Equal("hello", events[0].TextDelta);
    }

    [Theory]
    [InlineData(
        "event: message_start\n"
        + "data: {\"type\":\"ping\"}\n\n",
        "provider_protocol_invalid")]
    [InlineData(
        "event: content_block_start\n"
        + "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"x\",\"signature\":\"y\"}}\n\n",
        "provider_content_block_unsupported")]
    public async Task RejectsMaliciousEventShapes(
        string maliciousEvent,
        string expectedCode)
    {
        var stream = maliciousEvent.StartsWith(
            "event: message_start",
            StringComparison.Ordinal)
            ? maliciousEvent
            : MessageStart(cacheFields: true) + maliciousEvent;
        var provider = CreateProvider(new FakeTransport(stream));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public async Task RejectsUnsupportedContentDialectExplicitly()
    {
        var stream = MessageStart(cacheFields: true)
                     + Event(
                         "content_block_start",
                         """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"x","signature":"y"}}""");
        var provider = CreateProvider(new FakeTransport(stream));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_content_block_unsupported", error.Code);
        Assert.Equal(ProviderFailureDisposition.Failover, error.Disposition);
    }

    [Fact]
    public async Task RejectsTruncatedStreamWithoutMessageStop()
    {
        var provider = CreateProvider(
            new FakeTransport(MessageStart(cacheFields: true)));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_sse_message_stop_missing", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task RejectsEventTruncatedBeforeBlankBoundary()
    {
        var provider = CreateProvider(
            new FakeTransport(
                "event: message_start\n"
                + "data: "
                + MessageStartJson(cacheFields: true)));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_sse_truncated_event", error.Code);
    }

    [Fact]
    public async Task AccountsFinalUsageBeforeTruncatedMessageStop()
    {
        var provider = CreateProvider(
            new FakeTransport(
                MessageStart(cacheFields: true)
                + EmptyTextBlock()
                + FinalDelta("end_turn", 4)));
        await using var enumerator = provider
            .StreamAsync(Request())
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(ModelStreamEventKinds.Usage, enumerator.Current.Kind);
        Assert.Equal(10, enumerator.Current.Usage!.InputTokens);
        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => _ = await enumerator.MoveNextAsync());

        Assert.Equal("provider_sse_message_stop_missing", error.Code);
    }

    [Fact]
    public async Task RejectsInvalidFinalToolJson()
    {
        var provider = CreateProvider(
            new FakeTransport(ToolStream("""{"city":""")));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_tool_input_invalid_json", error.Code);
    }

    [Fact]
    public async Task RejectsDuplicateJsonPropertiesInToolInput()
    {
        var provider = CreateProvider(
            new FakeTransport(
                ToolStream("""{"city":"Paris","city":"London"}""")));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_protocol_invalid", error.Code);
    }

    [Fact]
    public async Task RejectsDuplicateToolUseIdentifiers()
    {
        var stream = MessageStart(cacheFields: true)
                     + ToolBlock(0, "toolu_same", "first")
                     + ToolBlock(1, "toolu_same", "second");
        var provider = CreateProvider(new FakeTransport(stream));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_protocol_invalid", error.Code);
    }

    [Fact]
    public async Task RejectsDecreasingCumulativeUsage()
    {
        var stream = MessageStart(cacheFields: true)
                     + Event(
                         "message_delta",
                         """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":0}}""");
        var provider = CreateProvider(new FakeTransport(stream));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_protocol_invalid", error.Code);
    }

    [Fact]
    public async Task RejectsIncompleteCacheCounterPair()
    {
        var stream = Event(
                         "message_start",
                         """
                         {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":5,"cache_read_input_tokens":3,"output_tokens":1}}}
                         """)
                     + EmptyTextBlock()
                     + FinalDelta("end_turn", 4)
                     + Event("message_stop", """{"type":"message_stop"}""");
        var provider = CreateProvider(new FakeTransport(stream));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_protocol_invalid", error.Code);
    }

    [Fact]
    public async Task BoundsSseLinesAndTotalStream()
    {
        var options = new AnthropicProviderOptions
        {
            Model = "claude-test",
            MaxSseLineCharacters = 32,
            MaxSseEventCharacters = 64,
            MaxStreamCharacters = 128
        };
        var provider = CreateProvider(
            new FakeTransport(
                "event: future\n"
                + "data: "
                + new string('x', 64)
                + "\n\n"),
            options);

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_sse_line_too_large", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task EnforcesTotalStreamLimitWhileReadingLines()
    {
        var options = new AnthropicProviderOptions
        {
            Model = "claude-test",
            MaxSseLineCharacters = 32,
            MaxSseEventCharacters = 64,
            MaxStreamCharacters = 64
        };
        var provider = CreateProvider(
            new FakeTransport(
                "event: future\n"
                + "data: {\"type\":\"future\"}\n\n"
                + "event: future\n"
                + "data: {\"type\":\"future\"}\n\n"),
            options);

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_sse_stream_too_large", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task MapsInStreamOverloadWithoutEchoingProviderMessage()
    {
        const string secretMessage = "provider-secret-message";
        var provider = CreateProvider(
            new FakeTransport(
                Event(
                    "error",
                    "{\"type\":\"error\",\"error\":{\"type\":"
                    + "\"overloaded_error\",\"message\":\""
                    + secretMessage
                    + "\"}}")));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_stream_error", error.Code);
        Assert.True(error.Retryable);
        Assert.DoesNotContain(
            secretMessage,
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, "provider_invalid_request", false, true)]
    [InlineData(401, "provider_auth_failed", false, true)]
    [InlineData(429, "provider_throttled", true, true)]
    [InlineData(529, "provider_unavailable", true, false)]
    [InlineData(307, "provider_redirect_rejected", false, true)]
    public async Task ClassifiesHttpErrors(
        int status,
        string code,
        bool retryable,
        bool knownZero)
    {
        var provider = CreateProvider(
            new FakeTransport(string.Empty, status, "2"));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.Equal(knownZero, error.UsageKnownToBeZero);
        if (status == 429)
        {
            Assert.Equal(TimeSpan.FromSeconds(2), error.RetryAfter);
        }
    }

    [Fact]
    public async Task PropagatesCallerCancellation()
    {
        var transport = new FakeTransport(TextStream(cacheFields: true));
        var provider = CreateProvider(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectAsync(
                provider.StreamAsync(Request(), cancellation.Token)));
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task SanitizesCredentialFailures()
    {
        const string secret = "secret-from-credential-source";
        var provider = new AnthropicMessagesStreamingProvider(
            Options(),
            new ThrowingCredentialSource(secret),
            new FakeTransport(TextStream(cacheFields: true)));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_auth_missing", error.Code);
        Assert.DoesNotContain(
            secret,
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifiesCredentialSelfCancellationAsKnownZero()
    {
        var provider = new AnthropicMessagesStreamingProvider(
            Options(),
            new SelfCancellingCredentialSource(),
            new FakeTransport(TextStream(cacheFields: true)));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_auth_missing", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Equal(ProviderFailureDisposition.Failover, error.Disposition);
    }

    [Fact]
    public async Task ClassifiesTransportSelfCancellationAsAmbiguous()
    {
        var provider = CreateProvider(
            new SelfCancellingTransport());

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_connect_failed", error.Code);
        Assert.True(error.Retryable);
        Assert.False(error.UsageKnownToBeZero);
    }

    [Fact]
    public async Task RejectsMalformedUtf8WithoutReflectingBytes()
    {
        var prefix = Encoding.ASCII.GetBytes(
            "event: message_start\ndata: ");
        var suffix = Encoding.ASCII.GetBytes("\n\n");
        var bytes = prefix
            .Concat(new byte[] { 0xff })
            .Concat(suffix)
            .ToArray();
        var provider = CreateProvider(new FakeTransport(bytes));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(Request())));

        Assert.Equal("provider_stream_read_failed", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public void RejectsUnsafeEndpointsAndHeaderInjection()
    {
        Assert.Throws<ArgumentException>(
            () => new AnthropicMessagesStreamingProvider(
                new AnthropicProviderOptions
                {
                    Model = "claude-test",
                    Endpoint = new Uri(
                        "https://api.anthropic.com/v1/messages?token=x")
                },
                new StaticAnthropicApiKeySource("key"),
                new FakeTransport(string.Empty)));
        Assert.Throws<ArgumentException>(
            () => new AnthropicMessagesStreamingProvider(
                new AnthropicProviderOptions
                {
                    Model = "claude-test",
                    Endpoint = new Uri(
                        "https://api.anthropic.com/v1/other")
                },
                new StaticAnthropicApiKeySource("key"),
                new FakeTransport(string.Empty)));
        Assert.Throws<ArgumentException>(
            () => new AnthropicMessagesStreamingProvider(
                new AnthropicProviderOptions
                {
                    Model = "claude-test",
                    ApiVersion = "2024-01-01"
                },
                new StaticAnthropicApiKeySource("key"),
                new FakeTransport(string.Empty)));
        Assert.Throws<ArgumentException>(
            () => new AnthropicMessagesStreamingProvider(
                new AnthropicProviderOptions
                {
                    Model = "claude-test",
                    MaxSseLineCharacters = 65,
                    MaxSseEventCharacters = 64,
                    MaxStreamCharacters = 64
                },
                new StaticAnthropicApiKeySource("key"),
                new FakeTransport(string.Empty)));
        Assert.Throws<ArgumentException>(
            () => new StaticAnthropicApiKeySource("key\r\nx-evil: 1"));
    }

    [Fact]
    public async Task PreparedEvidenceAndRequestBytesAreDeterministic()
    {
        var provider = CreateProvider(
            new FakeTransport(TextStream(cacheFields: true)));
        var route = new ProviderRouteIdentity(
            provider.ProviderId,
            provider.RouteMetadata,
            provider.Capabilities);
        var request = Request();

        var first = await provider.PrepareStreamAsync(
            new ProviderStreamPreparationContext(
                provider.ProviderId,
                route,
                request),
            CancellationToken.None);
        var second = await provider.PrepareStreamAsync(
            new ProviderStreamPreparationContext(
                provider.ProviderId,
                route,
                request),
            CancellationToken.None);
        try
        {
            Assert.True(first.Evidence.IsAvailable);
            Assert.Equal(
                first.Evidence.PayloadSha256,
                second.Evidence.PayloadSha256);
            Assert.Equal(
                first.Evidence.PayloadByteLength,
                second.Evidence.PayloadByteLength);
            Assert.Equal(
                "anthropic.messages.sse.2023-06-01.v1",
                route.TransportDialect);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClearsOwnedRequestBodyAfterTransportConsumesIt()
    {
        var transport = new RetainingTransport(
            TextStream(cacheFields: true));
        var provider = CreateProvider(transport);

        _ = await CollectAsync(provider.StreamAsync(Request()));

        Assert.NotNull(transport.ObservedBody);
        Assert.All(
            transport.ObservedBody!,
            value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task RealTransportUsesAnthropicHeadersAndNeverAuthorization()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var captureTask = CaptureRequestAndRespondAsync(
            listener,
            TextStream(cacheFields: true),
            timeout.Token);
        using var transport = new HttpClientAnthropicStreamingTransport();
        var provider = new AnthropicMessagesStreamingProvider(
            new AnthropicProviderOptions
            {
                Model = "claude-test",
                Endpoint = new Uri(
                    $"http://127.0.0.1:{endpoint.Port}/v1/messages"),
                AllowInsecureLoopback = true
            },
            new StaticAnthropicApiKeySource("test-secret"),
            transport);

        _ = await CollectAsync(
            provider.StreamAsync(Request(), timeout.Token));
        var headers = await captureTask;

        Assert.Contains(
            "x-api-key: test-secret\r\n",
            headers,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "anthropic-version: 2023-06-01\r\n",
            headers,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "authorization:",
            headers,
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            "POST /v1/messages HTTP/",
            headers,
            StringComparison.Ordinal);
    }

    private static AnthropicMessagesStreamingProvider CreateProvider(
        IAnthropicStreamingHttpTransport transport,
        AnthropicProviderOptions? options = null)
    {
        return new AnthropicMessagesStreamingProvider(
            options ?? Options(),
            new StaticAnthropicApiKeySource("test-secret"),
            transport);
    }

    private static AnthropicProviderOptions Options()
    {
        return new AnthropicProviderOptions
        {
            Model = "claude-test"
        };
    }

    private static StreamingModelRequest Request(
        params NormalizedMessage[] messages)
    {
        return new StreamingModelRequest
        {
            RunId = "run-1",
            RunAttemptId = "run-attempt-1",
            TurnId = "turn-1",
            ProviderAttemptId = "provider-attempt-1",
            StreamAttemptId = "stream-attempt-1",
            Messages = messages.Length == 0
                ? new[]
                {
                    Message(
                        NormalizedRoles.User,
                        NormalizedContentPart.FromText("hello"))
                }
                : messages
        };
    }

    private static NormalizedMessage Message(
        string role,
        params NormalizedContentPart[] parts)
    {
        return new NormalizedMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Role = role,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Parts = parts.ToList()
        };
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string TextStream(bool cacheFields)
    {
        return MessageStart(cacheFields)
               + Event(
                   "content_block_start",
                   """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""")
               + Event("ping", """{"type":"ping"}""")
               + Event(
                   "content_block_delta",
                   """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}""")
               + Event(
                   "content_block_stop",
                   """{"type":"content_block_stop","index":0}""")
               + FinalDelta("end_turn", 4)
               + Event("message_stop", """{"type":"message_stop"}""");
    }

    private static string EmptyTextBlock()
    {
        return Event(
                   "content_block_start",
                   """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""")
               + Event(
                   "content_block_stop",
                   """{"type":"content_block_stop","index":0}""");
    }

    private static string ToolStream(params string[] fragments)
    {
        var builder = new StringBuilder();
        builder.Append(MessageStart(cacheFields: true));
        builder.Append(
            Event(
                "content_block_start",
                """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"weather","input":{}}}"""));
        foreach (var fragment in fragments)
        {
            var encoded = JsonEncodedText.Encode(fragment).ToString();
            builder.Append(
                Event(
                    "content_block_delta",
                    "{\"type\":\"content_block_delta\",\"index\":0,"
                    + "\"delta\":{\"type\":\"input_json_delta\","
                    + "\"partial_json\":\""
                    + encoded
                    + "\"}}"));
        }

        builder.Append(
            Event(
                "content_block_stop",
                """{"type":"content_block_stop","index":0}"""));
        builder.Append(FinalDelta("tool_use", 5));
        builder.Append(Event("message_stop", """{"type":"message_stop"}"""));
        return builder.ToString();
    }

    private static string ToolBlock(
        int index,
        string id,
        string name)
    {
        return Event(
                   "content_block_start",
                   "{\"type\":\"content_block_start\",\"index\":"
                   + index
                   + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\""
                   + id
                   + "\",\"name\":\""
                   + name
                   + "\",\"input\":{}}}")
               + Event(
                   "content_block_stop",
                   "{\"type\":\"content_block_stop\",\"index\":"
                   + index
                   + "}");
    }

    private static string MessageStart(bool cacheFields)
    {
        return Event(
            "message_start",
            MessageStartJson(cacheFields));
    }

    private static string MessageStartJson(bool cacheFields)
    {
        var cache = cacheFields
            ? """
              "cache_creation_input_tokens":2,"cache_read_input_tokens":3,"cache_creation":{"ephemeral_5m_input_tokens":2,"ephemeral_1h_input_tokens":0},
              """
            : string.Empty;
        return "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\","
               + "\"type\":\"message\",\"role\":\"assistant\","
               + "\"model\":\"claude-test\",\"content\":[],"
               + "\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{"
               + "\"input_tokens\":5,"
               + cache
               + "\"output_tokens\":1}}}";
    }

    private static string FinalDelta(string stopReason, int outputTokens)
    {
        return Event(
            "message_delta",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\""
            + stopReason
            + "\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":"
            + outputTokens
            + "}}");
    }

    private static string Event(string name, string json)
    {
        return "event: "
               + name
               + "\n"
               + "data: "
               + json.ReplaceLineEndings(string.Empty)
               + "\n\n";
    }

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelStreamEvent> source)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in source)
        {
            events.Add(item);
        }

        return events;
    }

    private static async Task<string> CaptureRequestAndRespondAsync(
        TcpListener listener,
        string sse,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(
            cancellationToken);
        var stream = client.GetStream();
        var buffer = new byte[2_048];
        var received = new List<byte>();
        while (received.Count < 64 * 1024)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            received.AddRange(buffer.AsSpan(0, count).ToArray());
            var text = Encoding.ASCII.GetString(received.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var body = Encoding.UTF8.GetBytes(sse);
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: text/event-stream\r\n"
                    + "Content-Length: "
                    + body.Length
                    + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
                return text;
            }
        }

        throw new InvalidDataException(
            "The local Anthropic request headers were incomplete.");
    }

    private sealed class FakeTransport : IAnthropicStreamingHttpTransport
    {
        private readonly byte[] _response;
        private readonly int _statusCode;
        private readonly string? _retryAfter;

        internal FakeTransport(
            string response,
            int statusCode = 200,
            string? retryAfter = null)
        {
            _response = Encoding.UTF8.GetBytes(response);
            _statusCode = statusCode;
            _retryAfter = retryAfter;
        }

        internal FakeTransport(byte[] response)
        {
            _response = response.ToArray();
            _statusCode = 200;
        }

        internal AnthropicStreamingHttpRequest? LastRequest { get; private set; }

        public ValueTask<IAnthropicStreamingHttpResponse> SendAsync(
            AnthropicStreamingHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = new AnthropicStreamingHttpRequest
            {
                Uri = new Uri(request.Uri.AbsoluteUri),
                ApiKey = request.ApiKey,
                ApiVersion = request.ApiVersion,
                Body = request.Body.ToArray(),
                ContentType = request.ContentType
            };
            return new ValueTask<IAnthropicStreamingHttpResponse>(
                new FakeResponse(
                    _response.ToArray(),
                    _statusCode,
                    _retryAfter));
        }
    }

    private sealed class RetainingTransport :
        IAnthropicStreamingHttpTransport
    {
        private readonly string _response;

        internal RetainingTransport(string response)
        {
            _response = response;
        }

        internal byte[]? ObservedBody { get; private set; }

        public ValueTask<IAnthropicStreamingHttpResponse> SendAsync(
            AnthropicStreamingHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedBody = request.Body;
            return new ValueTask<IAnthropicStreamingHttpResponse>(
                new FakeResponse(
                    Encoding.UTF8.GetBytes(_response),
                    200,
                    retryAfter: null));
        }
    }

    private sealed class SelfCancellingTransport :
        IAnthropicStreamingHttpTransport
    {
        public ValueTask<IAnthropicStreamingHttpResponse> SendAsync(
            AnthropicStreamingHttpRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return ValueTask.FromException<IAnthropicStreamingHttpResponse>(
                new TaskCanceledException(
                    "The transport cancelled itself."));
        }
    }

    private sealed class FakeResponse : IAnthropicStreamingHttpResponse
    {
        private readonly string? _retryAfter;

        internal FakeResponse(
            byte[] response,
            int statusCode,
            string? retryAfter)
        {
            StatusCode = statusCode;
            Content = new MemoryStream(response);
            _retryAfter = retryAfter;
        }

        public int StatusCode { get; }

        public Stream Content { get; }

        public string? GetHeader(string name)
        {
            return string.Equals(
                name,
                "Retry-After",
                StringComparison.OrdinalIgnoreCase)
                ? _retryAfter
                : null;
        }

        public void Dispose()
        {
            Content.Dispose();
        }
    }

    private sealed class ThrowingCredentialSource : IAnthropicApiKeySource
    {
        private readonly string _secret;

        internal ThrowingCredentialSource(string secret)
        {
            _secret = secret;
        }

        public ValueTask<string> GetApiKeyAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<string>(
                new InvalidOperationException(
                    "credential failure " + _secret));
        }
    }

    private sealed class SelfCancellingCredentialSource :
        IAnthropicApiKeySource
    {
        public ValueTask<string> GetApiKeyAsync(
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromException<string>(
                new TaskCanceledException(
                    "The credential source cancelled itself."));
        }
    }
}

using System.Collections.Concurrent;
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
        var reasoning = Assert.IsType<ReasoningContent>(response.Content[0]);
        Assert.Equal("think", reasoning.Text);
        Assert.Equal("reasoning_content", reasoning.Signature);
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
    public async Task PreservesAlternateReasoningFieldAndMatchesToolDeltasByIdWhenIndexIsMissing()
    {
        const string stream = """
            data: {"choices":[{"delta":{"reasoning":"plan"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"id":"call-1","function":{"name":"move","arguments":"{\"x\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"id":"call-1","function":{"arguments":"1}"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream"));
        var provider = Create(handler);

        var events = await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        var reasoning = Assert.IsType<ReasoningContent>(response.Content[0]);
        Assert.Equal("reasoning", reasoning.Signature);
        var call = Assert.IsType<ToolCallContent>(response.Content[1]);
        Assert.Equal("{\"x\":1}", call.ArgumentsJson);
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
    public async Task ProjectsImageAndProviderSpecificMediaPartsWithoutFlatteningThemToText()
    {
        var handler = new StubHandler(_ => Response(
            HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            "text/event-stream"));
        var options = new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions"))
        {
            ProjectResourcePart = resource => resource.MediaType switch
            {
                "audio/wav" => "{\"type\":\"input_audio\",\"input_audio\":{\"data\":\"audio-data\",\"format\":\"wav\"}}",
                "video/mp4" => "{\"type\":\"video_url\",\"video_url\":{\"url\":\"" + resource.Uri + "\"}}",
                _ => null,
            },
        };
        var provider = new OpenAICompatibleProvider(options);
        var request = new ModelRequest(
            "model",
            "rules",
            new[]
            {
                new AgentMessage(
                    AgentRole.User,
                    new AgentContent[]
                    {
                        new JsonContent("{\"question\":\"what changed?\"}"),
                        new ResourceContent("https://assets.example.test/frame.png", "image/png", "frame"),
                        new ResourceContent("game://capture.wav", "audio/wav", "voice"),
                        new ResourceContent("https://assets.example.test/clip.mp4", "video/mp4", "clip"),
                    },
                    DateTimeOffset.UnixEpoch),
            },
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            null,
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var content = document.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("https://assets.example.test/frame.png", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("input_audio", content[2].GetProperty("type").GetString());
        Assert.Equal("video_url", content[3].GetProperty("type").GetString());
    }

    [Fact]
    public async Task ProjectsToolReturnedImagesAfterTheCompleteToolResultBatch()
    {
        var handler = new StubHandler(_ => Response(
            HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            "text/event-stream"));
        var provider = new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions")));
        var firstCall = new ToolCallContent("capture", "capture_view", "{}");
        var secondCall = new ToolCallContent("inspect", "inspect_state", "{}");
        var request = new ModelRequest(
            "model",
            "rules",
            new AgentMessage[]
            {
                AgentMessage.User("look"),
                new(
                    AgentRole.Assistant,
                    new AgentContent[] { firstCall, secondCall },
                    DateTimeOffset.UnixEpoch,
                    model: "model",
                    stopReason: ModelStopReason.ToolUse),
                AgentMessage.ToolResult(
                    firstCall,
                    new ToolResult(new AgentContent[]
                    {
                        new ResourceContent("https://assets.example.test/capture.png", "image/png", "capture"),
                    }),
                    DateTimeOffset.UnixEpoch),
                AgentMessage.ToolResult(
                    secondCall,
                    new ToolResult(new AgentContent[] { new TextContent("clear") }),
                    DateTimeOffset.UnixEpoch),
            },
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            null,
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("tool", messages[4].GetProperty("role").GetString());
        Assert.Equal("user", messages[5].GetProperty("role").GetString());
        var attachments = messages[5].GetProperty("content");
        Assert.Equal("image_url", attachments[1].GetProperty("type").GetString());
        Assert.Equal(
            "https://assets.example.test/capture.png",
            attachments[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task ReplaysSignedReasoningForToolContinuationButKeepsUnsignedReasoningPrivate()
    {
        var handler = new StubHandler(_ => Response(
            HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            "text/event-stream"));
        var provider = new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://example.test/v1/chat/completions")));
        var request = new ModelRequest(
            "model",
            "rules",
            new[]
            {
                new AgentMessage(
                    AgentRole.Assistant,
                    new AgentContent[]
                    {
                        new ReasoningContent("provider-state", "reasoning_content"),
                        new ReasoningContent("private-state"),
                        new TextContent("answer"),
                    },
                    DateTimeOffset.UnixEpoch,
                    model: "model",
                    stopReason: ModelStopReason.Stop),
            },
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            null,
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var assistant = document.RootElement.GetProperty("messages")[1];
        Assert.Equal("provider-state", assistant.GetProperty("reasoning_content").GetString());
        Assert.DoesNotContain("private-state", handler.RequestBody, StringComparison.Ordinal);
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

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Contains("429", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.IsTransient);
        Assert.Equal(429, exception.StatusCode);
        Assert.DoesNotContain("do-not-expose", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureHonorsRetryDirectivesAndBoundedServerDelay()
    {
        var handler = new StubHandler(_ =>
        {
            var response = Response(HttpStatusCode.BadRequest, "retry", "text/plain");
            response.Headers.TryAddWithoutValidation("x-should-retry", "true");
            response.Headers.TryAddWithoutValidation("retry-after-ms", "1250");
            return response;
        });
        var provider = Create(handler);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.True(exception.IsTransient);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), exception.RetryAfter);
        Assert.Equal(400, exception.StatusCode);
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
    public async Task DoneMarkerWithoutFinishReasonIsStrictByDefaultAndCanBeEnabledExplicitly()
    {
        const string stream = "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\ndata: [DONE]\n\n";
        var strict = Create(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(strict.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Contains("finish reason", exception.Message, StringComparison.Ordinal);

        var compatible = new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
            new HttpClient(new StubHandler(_ => Response(HttpStatusCode.OK, stream, "text/event-stream"))),
            new Uri("https://example.test/v1/chat/completions"))
        {
            AllowDoneWithoutFinishReason = true,
        });
        var events = await CollectAsync(compatible.StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(ModelStopReason.Stop, events.Last().Response!.StopReason);
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
    public void RemoteHttpEndpointsRequireAnExplicitDevelopmentOverride()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("transport must not run")));
        Assert.Throws<ArgumentException>(() => new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
            client,
            new Uri("http://model.test/v1/chat/completions"))));

        var provider = new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
            client,
            new Uri("http://model.test/v1/chat/completions"))
        {
            AllowInsecureHttp = true,
        });

        Assert.NotNull(provider);
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

        var embeddedNull = new OpenAICompatibleProviderOptions(
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("transport must not run"))),
            new Uri("https://example.test/v1/chat/completions"))
        {
            ApiKey = "secret\0suffix",
        };
        Assert.Throws<ArgumentException>(() => new OpenAICompatibleProvider(embeddedNull));
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

    [Fact]
    public async Task DeveloperGatewayCredentialsAreSingleFlightCachedAndRefreshedBeforeExpiry()
    {
        var now = DateTimeOffset.Parse("2026-08-07T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var source = new GatewayCredentialSource(() => now);
        using var cache = new CachedDeveloperGatewayCredentialSource(
            source,
            TimeSpan.FromMinutes(1),
            () => now);

        var first = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => cache.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask()));
        now = now.AddMinutes(9).AddSeconds(1);
        var refreshed = await cache.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.All(first, token => Assert.Equal("session-token-1", token));
        Assert.Equal("session-token-2", refreshed);
        Assert.Equal(2, source.CallCount);
        Assert.False(source.ForceRefreshValues.First());
        Assert.True(source.ForceRefreshValues.Last());
    }

    [Fact]
    public async Task DeveloperGatewayCredentialBoundariesRejectControlCharactersAndAvoidClockOverflow()
    {
        Assert.Throws<ArgumentException>(() => new DeveloperGatewayCredential(
            "token\0suffix",
            DateTimeOffset.UtcNow.AddMinutes(1)));
        var now = DateTimeOffset.MinValue;
        var source = new FixedGatewayCredentialSource(
            new DeveloperGatewayCredential("short-lived", now.AddMinutes(1)));
        using var cache = new CachedDeveloperGatewayCredentialSource(
            source,
            TimeSpan.FromHours(1),
            () => now);

        Assert.Equal("short-lived", await cache.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Equal("short-lived", await cache.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task DeveloperGatewayProviderSendsOnlyShortLivedAccessToken()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new GatewayCredentialSource(() => now);
        using var credentials = new CachedDeveloperGatewayCredentialSource(source, clock: () => now);
        var handler = new StubHandler(_ => Response(
            HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            "text/event-stream"));
        var provider = DeveloperGatewayProvider.Create(
            new HttpClient(handler),
            new Uri("https://gateway.example.test/v1/chat/completions"),
            credentials);

        await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("Bearer session-token-1", handler.Authorization);
        Assert.DoesNotContain("session-token-1", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DeveloperGatewayProviderRejectsStaticProviderKey()
    {
        using var credentials = new CachedDeveloperGatewayCredentialSource(
            new GatewayCredentialSource(() => DateTimeOffset.UtcNow));

        Assert.Throws<InvalidOperationException>(() => DeveloperGatewayProvider.Create(
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("transport must not run"))),
            new Uri("https://gateway.example.test/v1/chat/completions"),
            credentials,
            options => options.ApiKey = "static-key-is-forbid"));
    }

    [Fact]
    public async Task HttpDeveloperGatewayExchangesPlayerSessionForScopedToken()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var handler = new StubHandler(_ => Response(
            HttpStatusCode.OK,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                accessToken = "scoped-client-token",
                expiresAt,
                scope = "model:chat tenant:player-1",
            }),
            "application/json"));
        var source = new HttpDeveloperGatewayCredentialSource(
            new HttpClient(handler),
            new Uri("https://game.example.test/v1/model-token"),
            (forceRefresh, _) => new ValueTask<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer player-session-token",
                    ["X-Force-Refresh"] = forceRefresh.ToString(),
                }));

        var credential = await source.GetCredentialAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal("scoped-client-token", credential.AccessToken);
        Assert.Equal(expiresAt, credential.ExpiresAt);
        Assert.Equal("model:chat tenant:player-1", credential.Scope);
        Assert.Equal("Bearer player-session-token", handler.Authorization);
        Assert.Contains("\"forceRefresh\":true", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpDeveloperGatewayRejectsAmbiguousCredentialResponses()
    {
        var handler = new StubHandler(_ => Response(
            HttpStatusCode.OK,
            "{\"accessToken\":\"first\",\"accessToken\":\"second\",\"expiresAt\":\"2030-01-01T00:00:00Z\"}",
            "application/json"));
        var source = new HttpDeveloperGatewayCredentialSource(
            new HttpClient(handler),
            new Uri("https://game.example.test/v1/model-token"),
            (_, _) => new ValueTask<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>()));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await source.GetCredentialAsync(false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuthenticationFailureInvalidatesGatewayCredentialForNextRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new GatewayCredentialSource(() => now);
        using var credentials = new CachedDeveloperGatewayCredentialSource(source, clock: () => now);
        var calls = 0;
        var handler = new StubHandler(_ => Interlocked.Increment(ref calls) == 1
            ? Response(HttpStatusCode.Unauthorized, "expired", "text/plain")
            : Response(
                HttpStatusCode.OK,
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                "text/event-stream"));
        var provider = DeveloperGatewayProvider.Create(
            new HttpClient(handler),
            new Uri("https://gateway.example.test/v1/chat/completions"),
            credentials);

        await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(2, source.CallCount);
        Assert.True(source.ForceRefreshValues.Last());
        Assert.Equal("Bearer session-token-2", handler.Authorization);
    }

    [Fact]
    public async Task AuthenticationFailureCallbackCannotMaskTheProviderError()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.Unauthorized, "expired", "text/plain"));
        var options = new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://gateway.example.test/v1/chat/completions"))
        {
            OnAuthenticationFailure = _ => throw new InvalidOperationException("cache invalidation failed"),
        };
        var provider = new OpenAICompatibleProvider(options);

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Equal(401, exception.StatusCode);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task InvalidationDuringCredentialRefreshCannotReinstallTheRevokedToken()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new RacingCredentialSource(() => now);
        using var credentials = new CachedDeveloperGatewayCredentialSource(source, clock: () => now);

        var pending = credentials.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask();
        await source.FirstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        credentials.Invalidate();
        source.ReleaseFirst.TrySetResult();

        Assert.Equal("session-token-2", await pending);
        Assert.Equal(2, source.CallCount);
        Assert.True(source.ForceRefreshValues.Last());
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

    private sealed class GatewayCredentialSource : IDeveloperGatewayCredentialSource
    {
        private readonly Func<DateTimeOffset> _clock;
        private int _calls;

        public GatewayCredentialSource(Func<DateTimeOffset> clock)
        {
            _clock = clock;
        }

        public int CallCount => Volatile.Read(ref _calls);

        public ConcurrentQueue<bool> ForceRefreshValues { get; } = new();

        public ValueTask<DeveloperGatewayCredential> GetCredentialAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForceRefreshValues.Enqueue(forceRefresh);
            var call = Interlocked.Increment(ref _calls);
            return new ValueTask<DeveloperGatewayCredential>(new DeveloperGatewayCredential(
                "session-token-" + call.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _clock().AddMinutes(10),
                "model:chat"));
        }
    }

    private sealed class FixedGatewayCredentialSource : IDeveloperGatewayCredentialSource
    {
        private readonly DeveloperGatewayCredential _credential;
        private int _calls;

        public FixedGatewayCredentialSource(DeveloperGatewayCredential credential)
        {
            _credential = credential;
        }

        public int CallCount => Volatile.Read(ref _calls);

        public ValueTask<DeveloperGatewayCredential> GetCredentialAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return new ValueTask<DeveloperGatewayCredential>(_credential);
        }
    }

    private sealed class RacingCredentialSource : IDeveloperGatewayCredentialSource
    {
        private readonly Func<DateTimeOffset> _clock;
        private int _calls;

        public RacingCredentialSource(Func<DateTimeOffset> clock)
        {
            _clock = clock;
        }

        public int CallCount => Volatile.Read(ref _calls);

        public ConcurrentQueue<bool> ForceRefreshValues { get; } = new();

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DeveloperGatewayCredential> GetCredentialAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            ForceRefreshValues.Enqueue(forceRefresh);
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }

            return new DeveloperGatewayCredential(
                "session-token-" + call.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _clock().AddMinutes(10),
                "model:chat");
        }
    }
}

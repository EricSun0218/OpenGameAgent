using System.Net;
using System.Text;
using System.Text.Json;
using OpenGameAgent;
using OpenGameAgent.Kernel;
using OpenGameAgent.ProviderTransport;
using Xunit;

namespace OpenGameAgent.Providers.MessageGateway.Tests;

public sealed class MessageGatewayProviderTests
{
    [Fact]
    public async Task ProjectsContextOptionsAndDecodesTheCompleteEventProtocol()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}",
            "{\"type\":\"thinking_start\",\"contentIndex\":0}",
            "{\"type\":\"thinking_delta\",\"contentIndex\":0,\"delta\":\"plan\"}",
            "{\"type\":\"thinking_end\",\"contentIndex\":0,\"content\":\"plan\",\"contentSignature\":\"reason-signature\"}",
            "{\"type\":\"text_start\",\"contentIndex\":1}",
            "{\"type\":\"text_delta\",\"contentIndex\":1,\"delta\":\"hello\"}",
            "{\"type\":\"text_end\",\"contentIndex\":1,\"content\":\"hello\",\"contentSignature\":\"text-signature\"}",
            "{\"type\":\"toolcall_start\",\"contentIndex\":2,\"id\":\"call-1\",\"toolName\":\"move\"}",
            "{\"type\":\"toolcall_delta\",\"contentIndex\":2,\"delta\":\"{\\\"x\\\":1}\"}",
            "{\"type\":\"toolcall_end\",\"contentIndex\":2,\"toolCall\":{\"type\":\"toolCall\",\"id\":\"call-1\",\"name\":\"move\",\"arguments\":{\"x\":1},\"thoughtSignature\":\"thought\",\"namespace\":\"world\"}}",
            Done("toolUse", responseId: "response-1", rewrite: true))));
        var options = Options(handler);
        options.AccessToken = "access-token";
        options.Headers["X-Game-Id"] = "game-1";
        options.Debug = true;
        options.ToolChoice = MessageGatewayToolChoiceMode.Function;
        options.ToolName = "move";
        var provider = new MessageGatewayProvider(options);
        var parameters = new ModelParameters
        {
            Temperature = 0.4,
            MaxOutputTokens = 321,
            ReasoningLevel = "high",
            CacheRetention = ModelCacheRetention.Long,
            Transport = ModelTransport.ServerSentEvents,
        };
        var request = new ModelRequest(
            "world-model",
            "You simulate a world.",
            new[] { AgentMessage.User("advance", DateTimeOffset.UnixEpoch) },
            new[]
            {
                new ToolDefinition(
                    "move",
                    "Move an actor.",
                    "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\"}}}",
                    ToolConstrainedSampling.JsonSchema(ToolSchemaStrictness.Require)),
            },
            parameters,
            "session-1",
            "run-1",
            1);

        var events = await CollectAsync(provider, request, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                ModelStreamEventKind.Started,
                ModelStreamEventKind.ReasoningStarted,
                ModelStreamEventKind.ReasoningDelta,
                ModelStreamEventKind.ReasoningEnded,
                ModelStreamEventKind.TextStarted,
                ModelStreamEventKind.TextDelta,
                ModelStreamEventKind.TextEnded,
                ModelStreamEventKind.ToolCallStarted,
                ModelStreamEventKind.ToolCallDelta,
                ModelStreamEventKind.ToolCallEnded,
                ModelStreamEventKind.Completed,
            },
            events.Select(item => item.Kind));
        var terminal = Assert.Single(events, item => item.IsTerminal).Response!;
        Assert.Equal(ModelStopReason.ToolUse, terminal.StopReason);
        Assert.Equal("response-1", terminal.ResponseId);
        Assert.Equal("message-gateway", terminal.Api);
        Assert.Equal("world-model", terminal.ResponseModel);
        Assert.Equal(10, terminal.Usage.TotalTokens);
        Assert.Equal("plan", Assert.IsType<ReasoningContent>(terminal.Content[0]).Text);
        Assert.Equal("reason-signature", Assert.IsType<ReasoningContent>(terminal.Content[0]).Signature);
        Assert.Equal("text-signature", Assert.IsType<TextContent>(terminal.Content[1]).Signature);
        var call = Assert.IsType<ToolCallContent>(terminal.Content[2]);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("{\"x\":1}", call.ArgumentsJson);
        Assert.Equal("thought", call.ThoughtSignature);
        Assert.Equal("world", call.Namespace);
        Assert.Equal("message_gateway_rewrite", Assert.Single(terminal.Diagnostics).Code);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://gateway.example/v1/messages?debug=1", handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer access-token", handler.Authorization);
        Assert.Equal("game-1", handler.Headers["X-Game-Id"]);
        Assert.Equal("text/event-stream", handler.Accept);
        Assert.Equal("application/json", handler.ContentType);
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("world-model", root.GetProperty("model").GetString());
        var context = root.GetProperty("context");
        Assert.Equal("You simulate a world.", context.GetProperty("systemPrompt").GetString());
        Assert.Equal("advance", context.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(0, context.GetProperty("messages")[0].GetProperty("timestamp").GetInt64());
        var tool = context.GetProperty("tools")[0];
        Assert.Equal("move", tool.GetProperty("name").GetString());
        Assert.Equal("json_schema", tool.GetProperty("constrainedSampling").GetProperty("type").GetString());
        Assert.Equal("require", tool.GetProperty("constrainedSampling").GetProperty("strict").GetString());
        var projectedOptions = root.GetProperty("options");
        Assert.Equal(0.4, projectedOptions.GetProperty("temperature").GetDouble());
        Assert.Equal(321, projectedOptions.GetProperty("maxTokens").GetInt32());
        Assert.Equal("high", projectedOptions.GetProperty("reasoning").GetString());
        Assert.Equal("long", projectedOptions.GetProperty("cacheRetention").GetString());
        Assert.Equal("session-1", projectedOptions.GetProperty("sessionId").GetString());
        Assert.Equal(
            "move",
            projectedOptions.GetProperty("toolChoice").GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ProjectsRicherContentWithStableLossyPlaceholders()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(Done("stop"))));
        var provider = Provider(handler);
        var user = new AgentMessage(
            AgentRole.User,
            new AgentContent[]
            {
                new TextContent("look"),
                new BinaryContent(AgentMediaKind.Image, "aW1hZ2U=", "image/png"),
                new BinaryContent(AgentMediaKind.Audio, "YXVkaW8=", "audio/wav"),
                new BinaryContent(AgentMediaKind.Video, "dmlkZW8=", "video/mp4"),
                new BinaryContent(AgentMediaKind.File, "ZmlsZQ==", "application/octet-stream"),
                new ResourceContent("game://asset/tree", "application/json", "tree"),
                new JsonContent("{\"state\":1}"),
            },
            DateTimeOffset.UnixEpoch);

        var events = await CollectAsync(
            provider,
            Request(messages: new[] { user }),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelStopReason.Stop, Assert.Single(events).Response!.StopReason);
        using var body = JsonDocument.Parse(handler.Body!);
        var content = body.RootElement.GetProperty("context").GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("aW1hZ2U=", content[1].GetProperty("data").GetString());
        Assert.Equal("[audio omitted: message gateway supports only text and images]", content[2].GetProperty("text").GetString());
        Assert.Equal("[video omitted: message gateway supports only text and images]", content[3].GetProperty("text").GetString());
        Assert.Equal("[file omitted: message gateway supports only text and images]", content[4].GetProperty("text").GetString());
        Assert.Equal("[resource omitted: inline data required]", content[5].GetProperty("text").GetString());
        Assert.Equal("{\"state\":1}", content[6].GetProperty("text").GetString());
    }

    [Fact]
    public async Task NormalizesForeignTranscriptStateAndRepairsMissingToolResults()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(Done("stop"))));
        var provider = Provider(handler);
        var assistant = new AgentMessage(
            AgentRole.Assistant,
            new AgentContent[]
            {
                new ReasoningContent("visible reasoning", "foreign-reason-signature"),
                new ReasoningContent("secret reasoning", "foreign-redacted", redacted: true),
                new TextContent("answer", "foreign-text-signature"),
                new ToolCallContent("call-foreign", "inspect", "{}", "foreign-thought", "foreign-namespace"),
            },
            DateTimeOffset.UnixEpoch,
            model: "other-model",
            stopReason: ModelStopReason.ToolUse,
            usage: new ModelUsage(),
            provider: "other-provider",
            api: "other-api");
        var messages = new[]
        {
            assistant,
            AgentMessage.User("continue", DateTimeOffset.UnixEpoch.AddSeconds(1)),
        };

        await CollectAsync(provider, Request(messages: messages), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret reasoning", handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign-reason-signature", handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign-text-signature", handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign-thought", handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign-namespace", handler.Body!, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.Body!);
        var projected = body.RootElement.GetProperty("context").GetProperty("messages");
        Assert.Equal(3, projected.GetArrayLength());
        var projectedAssistant = projected[0];
        Assert.Equal("text", projectedAssistant.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("visible reasoning", projectedAssistant.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("answer", projectedAssistant.GetProperty("content")[1].GetProperty("text").GetString());
        Assert.False(projectedAssistant.GetProperty("content")[1].TryGetProperty("textSignature", out _));
        var projectedCall = projectedAssistant.GetProperty("content")[2];
        Assert.False(projectedCall.TryGetProperty("thoughtSignature", out _));
        Assert.False(projectedCall.TryGetProperty("namespace", out _));
        Assert.Equal("toolResult", projected[1].GetProperty("role").GetString());
        Assert.Equal("call-foreign", projected[1].GetProperty("toolCallId").GetString());
        Assert.Equal("No result provided", projected[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(projected[1].GetProperty("isError").GetBoolean());
        Assert.Equal("continue", projected[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task UsesExplicitAuthorizationAndReportsOnlySanitizedResponseMetadata()
    {
        ProviderResponseObservation? observed = null;
        var handler = new CaptureHandler((_, _) =>
        {
            var response = SseResponse(Done("stop"));
            response.Headers.TryAddWithoutValidation("X-Request-Id", "request-7");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "private=value");
            response.Headers.TryAddWithoutValidation("Authorization", "Bearer response-secret");
            return Task.FromResult(response);
        });
        var options = Options(handler);
        options.Headers["Authorization"] = "Bearer configured-token";
        options.ResponseObserver = (observation, _) =>
        {
            observed = observation;
            return ValueTask.CompletedTask;
        };
        var provider = new MessageGatewayProvider(options);
        var parameters = new ModelParameters
        {
            Extensions = new Dictionary<string, string>
            {
                [MessageGatewayParameterKeys.Debug] = "true",
            },
        };

        await CollectAsync(provider, Request(parameters: parameters), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer configured-token", handler.Authorization);
        Assert.Equal("https://gateway.example/v1/messages?debug=1", handler.Uri!.AbsoluteUri);
        Assert.NotNull(observed);
        Assert.Equal(200, observed!.StatusCode);
        Assert.Equal("request-7", observed.Metadata["x-request-id"]);
        Assert.DoesNotContain("set-cookie", observed.Metadata.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", observed.Metadata.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("response-secret", JsonSerializer.Serialize(observed.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReturnsServerErrorAndRewriteAsInBandTerminalEvents()
    {
        var errorHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"error\",\"reason\":\"error\",\"usage\":" + Usage() + ",\"errorMessage\":\"route failed\",\"responseId\":\"response-error\"}")));
        var errorEvents = await CollectAsync(
            Provider(errorHandler),
            Request(),
            TestContext.Current.CancellationToken);

        var failure = Assert.Single(errorEvents);
        Assert.Equal(ModelStreamEventKind.Failed, failure.Kind);
        Assert.Equal(ModelStopReason.Error, failure.Response!.StopReason);
        Assert.Equal("route failed", failure.Response.ErrorMessage);
        Assert.Equal("response-error", failure.Response.ResponseId);

        var abortedHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"error\",\"reason\":\"aborted\",\"usage\":" + Usage() + "}")));
        var abortedEvents = await CollectAsync(
            Provider(abortedHandler),
            Request(),
            TestContext.Current.CancellationToken);
        Assert.Equal(ModelStopReason.Aborted, Assert.Single(abortedEvents).Response!.StopReason);
    }

    [Fact]
    public async Task PreservesTypedHttpFailuresWithBoundedDiagnostics()
    {
        var handler = new CaptureHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                ReasonPhrase = "Rate Limited",
                Content = new StringContent(
                    "{\"error\":{\"message\":\"try later\",\"code\":\"rate_limit\",\"details\":\"private\"}}",
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation("X-Request-Id", "request-http-error");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "private=value");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return Task.FromResult(response);
        });

        var failure = await Assert.ThrowsAsync<ModelProviderException>(() =>
            CollectAsync(Provider(handler), Request(), TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 429", failure.Message, StringComparison.Ordinal);
        Assert.Contains("try later", failure.Message, StringComparison.Ordinal);
        Assert.Contains("rate_limit", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.IsTransient);
        Assert.Equal(TimeSpan.FromSeconds(3), failure.RetryAfter);
        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("message_gateway_response_failure", diagnostic.Code);
        Assert.DoesNotContain("private=value", diagnostic.DataJson!, StringComparison.Ordinal);
        Assert.Contains("request-http-error", diagnostic.DataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedactsDynamicCredentialsFromHttpErrorsAndObservedMetadata()
    {
        const string secret = "dynamic-test-secret";
        ProviderResponseObservation? observed = null;
        var handler = new CaptureHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"Bearer " + secret + "\",\"code\":\"" + secret + "\",\"details\":\"" + secret + "\"}}",
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation("X-Request-Id", secret);
            return Task.FromResult(response);
        });
        var options = Options(handler);
        options.GetAccessTokenAsync = _ => new ValueTask<string?>(secret);
        options.ResponseObserver = (observation, _) =>
        {
            observed = observation;
            return ValueTask.CompletedTask;
        };

        var failure = await Assert.ThrowsAsync<ModelProviderException>(() =>
            CollectAsync(
                new MessageGatewayProvider(options),
                Request(),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(failure.Diagnostics), StringComparison.Ordinal);
        Assert.NotNull(observed);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(observed!.Metadata), StringComparison.Ordinal);
        Assert.Contains("redacted", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotExposeStructuredErrorDetailsWhenSafeFieldsAreInvalid()
    {
        const string privateDetail = "private-upstream-detail";
        var handler = new CaptureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":7,\"code\":9,\"details\":\"" + privateDetail + "\"}}",
                Encoding.UTF8,
                "application/json"),
        }));

        var failure = await Assert.ThrowsAsync<ModelProviderException>(() =>
            CollectAsync(Provider(handler), Request(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(privateDetail, failure.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 400", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SanitizesCredentialEchoesInMeaningfulStreamErrors()
    {
        const string secret = "stream-test-secret";
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"error\",\"reason\":\"error\",\"usage\":" + Usage()
            + ",\"errorMessage\":\"Bearer " + secret + "\\r\\nfailed\",\"responseId\":\"" + secret + "\"}")));
        var options = Options(handler);
        options.AccessToken = secret;

        var events = await CollectAsync(
            new MessageGatewayProvider(options),
            Request(),
            TestContext.Current.CancellationToken);

        var terminal = Assert.Single(events).Response!;
        Assert.Equal(ModelStopReason.Error, terminal.StopReason);
        Assert.DoesNotContain(secret, terminal.ErrorMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', terminal.ErrorMessage!);
        Assert.DoesNotContain('\n', terminal.ErrorMessage!);
        Assert.Equal("[redacted]", terminal.ResponseId);
    }

    [Fact]
    public async Task RetryWrapperRetriesTypedRateLimitBeforeAnyMeaningfulOutput()
    {
        var attempt = 0;
        var handler = new CaptureHandler((_, _) =>
        {
            attempt++;
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"try again\",\"code\":\"rate_limit\"}}",
                        Encoding.UTF8,
                        "application/json"),
                };
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            }

            return Task.FromResult(SseResponse(Done("stop")));
        });
        var retrying = new RetryingModelProvider(
            Provider(handler),
            maximumAttempts: 2,
            delay: _ => TimeSpan.Zero);

        var events = await CollectAsync(retrying, Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events).Kind);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RetryWrapperRetriesTransportFailureBeforeConnection()
    {
        var attempt = 0;
        var handler = new CaptureHandler((_, _) =>
        {
            attempt++;
            return attempt == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection unavailable"))
                : Task.FromResult(SseResponse(Done("stop")));
        });
        var retrying = new RetryingModelProvider(
            Provider(handler),
            maximumAttempts: 2,
            delay: _ => TimeSpan.Zero);

        var events = await CollectAsync(retrying, Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events).Kind);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RetryWrapperRetriesEmptyStreamErrorBeforeAnyMeaningfulOutput()
    {
        var attempt = 0;
        var handler = new CaptureHandler((_, _) =>
        {
            attempt++;
            return Task.FromResult(attempt == 1
                ? SseResponse(
                    "{\"type\":\"start\"}",
                    "{\"type\":\"error\",\"reason\":\"error\",\"usage\":" + EmptyUsage()
                    + ",\"errorMessage\":\"route unavailable\"}")
                : SseResponse(Done("stop")));
        });
        var retrying = new RetryingModelProvider(
            Provider(handler),
            maximumAttempts: 2,
            delay: _ => TimeSpan.Zero);

        var events = await CollectAsync(retrying, Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events).Kind);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RetryWrapperNeverReplaysAfterMeaningfulPartialOutput()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}",
            "{\"type\":\"text_start\",\"contentIndex\":0}",
            "{\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"visible\"}",
            "{\"type\":\"text_end\",\"contentIndex\":0,\"content\":\"visible\"}",
            "{\"type\":\"error\",\"reason\":\"error\",\"usage\":" + EmptyUsage()
            + ",\"errorMessage\":\"failed after output\"}")));
        var retrying = new RetryingModelProvider(
            Provider(handler),
            maximumAttempts: 3,
            delay: _ => TimeSpan.Zero);

        var events = await CollectAsync(retrying, Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Failed, events[^1].Kind);
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.TextDelta);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RetryWrapperNeverReplaysAfterReportedUsage()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}",
            "{\"type\":\"error\",\"reason\":\"error\",\"usage\":" + Usage()
            + ",\"errorMessage\":\"failed after billing\"}")));
        var retrying = new RetryingModelProvider(
            Provider(handler),
            maximumAttempts: 3,
            delay: _ => TimeSpan.Zero);

        var events = await CollectAsync(retrying, Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Failed, events[^1].Kind);
        Assert.Equal(10, events[^1].Response!.Usage.TotalTokens);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RetryWrapperPreservesHttpStatusWhenErrorBodyCannotBeDecoded()
    {
        var attempt = 0;
        var handler = new CaptureHandler((_, _) =>
        {
            attempt++;
            if (attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new ByteArrayContent(new byte[] { 0xff }),
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            }

            return Task.FromResult(SseResponse(Done("stop")));
        });
        var retrying = new RetryingModelProvider(
            Provider(handler),
            maximumAttempts: 2,
            delay: _ => TimeSpan.Zero);

        var events = await CollectAsync(retrying, Request(), TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events).Kind);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task MissingCredentialAndOversizedRequestFailBeforeNetworkIo()
    {
        var missingHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(Done("stop"))));
        var missingProvider = new MessageGatewayProvider(Options(missingHandler));

        var missingEvents = await CollectAsync(
            missingProvider,
            Request(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Failed, Assert.Single(missingEvents).Kind);
        Assert.Equal(0, missingHandler.RequestCount);

        var largeHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(Done("stop"))));
        var largeOptions = Options(largeHandler);
        largeOptions.AccessToken = "token";
        largeOptions.MaxRequestBytes = 128;
        var largeEvents = await CollectAsync(
            new MessageGatewayProvider(largeOptions),
            Request(messages: new[] { AgentMessage.User(new string('x', 1_000), DateTimeOffset.UnixEpoch) }),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Failed, Assert.Single(largeEvents).Kind);
        Assert.Equal(0, largeHandler.RequestCount);
    }

    [Fact]
    public async Task StrictlyRejectsMalformedJsonUnfinishedStreamsAndMismatchedToolPartials()
    {
        var malformedHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}",
            "{\"type\":\"text_start\",\"type\":\"done\",\"contentIndex\":0}")));
        var malformedEvents = await CollectAsync(
            Provider(malformedHandler),
            Request(),
            TestContext.Current.CancellationToken);
        Assert.Equal(ModelStreamEventKind.Started, malformedEvents[0].Kind);
        Assert.Equal(ModelStreamEventKind.Failed, malformedEvents[^1].Kind);
        Assert.Single(malformedEvents, item => item.IsTerminal);

        var unfinishedHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}")));
        var unfinishedEvents = await CollectAsync(
            Provider(unfinishedHandler),
            Request(),
            TestContext.Current.CancellationToken);
        Assert.Equal(ModelStreamEventKind.Started, unfinishedEvents[0].Kind);
        Assert.Equal(ModelStreamEventKind.Failed, unfinishedEvents[^1].Kind);
        Assert.Contains("without a terminal event", unfinishedEvents[^1].Response!.ErrorMessage, StringComparison.Ordinal);

        var mismatchHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}",
            "{\"type\":\"toolcall_start\",\"contentIndex\":0,\"id\":\"call-1\",\"toolName\":\"move\"}",
            "{\"type\":\"toolcall_delta\",\"contentIndex\":0,\"delta\":\"{\\\"x\\\":1}\"}",
            "{\"type\":\"toolcall_end\",\"contentIndex\":0,\"toolCall\":{\"type\":\"toolCall\",\"id\":\"call-1\",\"name\":\"move\",\"arguments\":{\"x\":2}}}")));
        var mismatchEvents = await CollectAsync(
            Provider(mismatchHandler),
            Request(),
            TestContext.Current.CancellationToken);
        Assert.Equal(ModelStreamEventKind.Failed, mismatchEvents[^1].Kind);
        Assert.Single(mismatchEvents, item => item.IsTerminal);

        var invalidCost = Done("stop").Replace("\"total\":1.0", "\"total\":99.0", StringComparison.Ordinal);
        var costHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(invalidCost)));
        var costEvents = await CollectAsync(
            Provider(costHandler),
            Request(),
            TestContext.Current.CancellationToken);
        Assert.Equal(ModelStreamEventKind.Failed, Assert.Single(costEvents).Kind);
        Assert.Contains("cost totals", costEvents[0].Response!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnforcesSseEventAndResponseBounds()
    {
        var eventHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}",
            "{\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"" + new string('x', 256) + "\"}")));
        var eventOptions = Options(eventHandler);
        eventOptions.AccessToken = "token";
        eventOptions.MaxEventBytes = 96;
        var eventResults = await CollectAsync(
            new MessageGatewayProvider(eventOptions),
            Request(),
            TestContext.Current.CancellationToken);
        Assert.Equal(ModelStreamEventKind.Failed, eventResults[^1].Kind);
        Assert.Single(eventResults, item => item.IsTerminal);

        var responseHandler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}",
            "{\"type\":\"text_start\",\"contentIndex\":0}",
            "{\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"" + new string('y', 2_000) + "\"}")));
        var responseOptions = Options(responseHandler);
        responseOptions.AccessToken = "token";
        responseOptions.MaxResponseBytes = 512;
        responseOptions.MaxEventBytes = 512;
        var responseResults = await CollectAsync(
            new MessageGatewayProvider(responseOptions),
            Request(),
            TestContext.Current.CancellationToken);
        Assert.Equal(ModelStreamEventKind.Failed, responseResults[^1].Kind);
        Assert.Single(responseResults, item => item.IsTerminal);
    }

    [Fact]
    public async Task BoundsCumulativePartialSnapshotMaterialization()
    {
        var frames = new List<string>
        {
            "{\"type\":\"start\"}",
            "{\"type\":\"text_start\",\"contentIndex\":0}",
        };
        frames.AddRange(Enumerable.Range(0, 20).Select(_ =>
            "{\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"abcdefghij\"}"));
        frames.Add("{\"type\":\"text_end\",\"contentIndex\":0,\"content\":\""
                   + new string('a', 200) + "\"}");
        frames.Add(Done("stop"));
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(frames.ToArray())));
        var options = Options(handler);
        options.AccessToken = "token";
        options.MaxPartialSnapshotWork = 100;

        var events = await CollectAsync(
            new MessageGatewayProvider(options),
            Request(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Failed, events[^1].Kind);
        Assert.Contains("partial-snapshot", events[^1].Response!.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(events, item => item.IsTerminal);
    }

    [Fact]
    public async Task AcceptsStructurallyEquivalentReorderedToolArguments()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}",
            "{\"type\":\"toolcall_start\",\"contentIndex\":0,\"id\":\"call-1\",\"toolName\":\"move\"}",
            "{\"type\":\"toolcall_delta\",\"contentIndex\":0,\"delta\":\"{\\\"x\\\":1,\\\"nested\\\":{\\\"a\\\":true,\\\"b\\\":2}}\"}",
            "{\"type\":\"toolcall_end\",\"contentIndex\":0,\"toolCall\":{\"type\":\"toolCall\",\"id\":\"call-1\",\"name\":\"move\",\"arguments\":{\"nested\":{\"b\":2.0,\"a\":true},\"x\":1.0}}}",
            Done("toolUse"))));

        var events = await CollectAsync(
            Provider(handler),
            Request(),
            TestContext.Current.CancellationToken);

        var terminal = Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(ModelStreamEventKind.Completed, terminal.Kind);
        var call = Assert.IsType<ToolCallContent>(Assert.Single(terminal.Response!.Content));
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal(1, arguments.RootElement.GetProperty("x").GetDouble());
        Assert.True(arguments.RootElement.GetProperty("nested").GetProperty("a").GetBoolean());
    }

    [Fact]
    public async Task RejectsFiniteCostPartsWhoseSumOverflows()
    {
        var usage = "{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0,"
                    + "\"cost\":{\"input\":1e308,\"output\":1e308,\"cacheRead\":1e308,\"cacheWrite\":1e308,\"total\":1e308}}";
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"done\",\"reason\":\"stop\",\"usage\":" + usage + "}")));

        var events = await CollectAsync(
            Provider(handler),
            Request(),
            TestContext.Current.CancellationToken);

        var terminal = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, terminal.Kind);
        Assert.Contains("cost total", terminal.Response!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupportsMultilineSseCommentsAndDoneMarkers()
    {
        var raw = ": keepalive\n\n"
                  + "data: [DONE]\n\n"
                  + "event: message\n"
                  + "id: 1\n"
                  + "data: {\"type\":\"done\",\n"
                  + "data: \"reason\":\"stop\",\"usage\":" + Usage() + "}\n\n";
        var handler = new CaptureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(raw, Encoding.UTF8, "text/event-stream"),
        }));

        var events = await CollectAsync(
            Provider(handler),
            Request(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events).Kind);
    }

    [Fact]
    public async Task RejectsControlCharactersInProtocolIdentifiers()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(
            "{\"type\":\"start\"}",
            "{\"type\":\"toolcall_start\",\"contentIndex\":0,\"id\":\"call\\r1\",\"toolName\":\"move\"}")));

        var events = await CollectAsync(
            Provider(handler),
            Request(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Failed, events[^1].Kind);
        Assert.Contains("field 'id'", events[^1].Response!.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(events, item => item.IsTerminal);
    }

    [Fact]
    public async Task CallerCancellationInterruptsANonCooperativeCredentialCallback()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(Done("stop"))));
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(handler);
        options.GetAccessTokenAsync = _ =>
        {
            entered.TrySetResult(true);
            return new ValueTask<string?>(tokenResult.Task);
        };
        var provider = new MessageGatewayProvider(options);
        using var cancellation = new CancellationTokenSource();
        var operation = CollectAsync(provider, Request(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await operation.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        tokenResult.TrySetResult("late-token");
        await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CallerCancellationInterruptsANonCooperativeHttpHandlerAndDisposesItsLateResponse()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CaptureHandler((_, _) =>
        {
            entered.TrySetResult(true);
            return pendingResponse.Task;
        });
        var provider = Provider(handler);
        using var cancellation = new CancellationTokenSource();
        var operation = CollectAsync(provider, Request(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await operation.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        var content = new TrackingContent();
        pendingResponse.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await content.DisposedTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task CallerCancellationInterruptsANonCooperativeResponseRead()
    {
        var stream = new NonCooperativeReadStream();
        var handler = new CaptureHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        });
        using var cancellation = new CancellationTokenSource();
        var operation = CollectAsync(Provider(handler), Request(), cancellation.Token);
        await stream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await operation.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(stream.Disposed);
        stream.Release();
    }

    [Fact]
    public async Task CompletesAtTerminalEventWithoutWaitingForConnectionClose()
    {
        var prefix = Encoding.UTF8.GetBytes(Event(Done("stop")));
        var stream = new PrefixThenWaitStream(prefix);
        var handler = new CaptureHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        });

        var events = await CollectAsync(
                Provider(handler),
                Request(),
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Completed, Assert.Single(events).Kind);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public void ValidatesCapabilitiesEndpointsHeadersAndToolChoice()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(Done("stop"))));
        var provider = Provider(handler);
        var capabilities = Assert.IsAssignableFrom<IModelProviderCapabilities>(provider);
        Assert.Equal(new[] { "message-gateway" }, capabilities.SupportedApis);
        Assert.False(capabilities.SupportsDeferredResponses);
        Assert.False(capabilities.SupportsNativeDeferredTools);

        Assert.Throws<ArgumentException>(() =>
            new MessageGatewayProvider(new MessageGatewayProviderOptions(
                new HttpClient(handler),
                new Uri("http://remote.example/v1"))));

        var insecure = new MessageGatewayProviderOptions(
            new HttpClient(handler),
            new Uri("http://remote.example/v1"))
        {
            AccessToken = "token",
            AllowInsecureHttp = true,
        };
        _ = new MessageGatewayProvider(insecure);

        var invalidHeader = Options(handler);
        invalidHeader.Headers["Bad Header"] = "value";
        Assert.Throws<ArgumentException>(() => new MessageGatewayProvider(invalidHeader));

        var invalidAuthorization = Options(handler);
        invalidAuthorization.Headers["Authorization"] = " ";
        Assert.Throws<ArgumentException>(() => new MessageGatewayProvider(invalidAuthorization));

        var invalidToolChoice = Options(handler);
        invalidToolChoice.ToolChoice = MessageGatewayToolChoiceMode.Function;
        Assert.Throws<ArgumentException>(() => new MessageGatewayProvider(invalidToolChoice));

        var invalidSnapshotBudget = Options(handler);
        invalidSnapshotBudget.MaxPartialSnapshotWork = 0;
        Assert.Throws<ArgumentException>(() => new MessageGatewayProvider(invalidSnapshotBudget));
    }

    [Fact]
    public async Task UnsupportedTransportAndDeferredModeFailInBand()
    {
        var handler = new CaptureHandler((_, _) => Task.FromResult(SseResponse(Done("stop"))));
        var provider = Provider(handler);
        var websocket = new ModelParameters { Transport = ModelTransport.WebSocket };
        var deferred = new ModelParameters { Deferred = true };

        var websocketEvents = await CollectAsync(
            provider,
            Request(parameters: websocket),
            TestContext.Current.CancellationToken);
        var deferredEvents = await CollectAsync(
            provider,
            Request(parameters: deferred),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelStreamEventKind.Failed, Assert.Single(websocketEvents).Kind);
        Assert.Equal(ModelStreamEventKind.Failed, Assert.Single(deferredEvents).Kind);
        Assert.Equal(0, handler.RequestCount);
    }

    private static MessageGatewayProvider Provider(CaptureHandler handler)
    {
        var options = Options(handler);
        options.AccessToken = "token";
        return new MessageGatewayProvider(options);
    }

    private static MessageGatewayProviderOptions Options(CaptureHandler handler) => new(
        new HttpClient(handler),
        new Uri("https://gateway.example/v1"));

    private static ModelRequest Request(
        IReadOnlyList<AgentMessage>? messages = null,
        ModelParameters? parameters = null) => new(
        "world-model",
        string.Empty,
        messages ?? Array.Empty<AgentMessage>(),
        Array.Empty<ToolDefinition>(),
        parameters ?? new ModelParameters(),
        null,
        "run-1",
        1);

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(
        IModelProvider provider,
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in provider.StreamAsync(request, cancellationToken))
        {
            events.Add(item);
        }

        return events;
    }

    private static HttpResponseMessage SseResponse(params string[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            string.Concat(events.Select(Event)),
            Encoding.UTF8,
            "text/event-stream"),
    };

    private static string Event(string value) => "data: " + value + "\n\n";

    private static string Done(string reason, string? responseId = null, bool rewrite = false)
    {
        var response = responseId is null ? string.Empty : ",\"responseId\":\"" + responseId + "\"";
        var rewriteValue = rewrite
            ? ",\"rewrite\":{\"policyId\":\"context-policy\",\"policyVersion\":2,\"changed\":true,\"tokenCountChange\":-4,\"messageCountChange\":-1,\"systemPromptChanged\":false}"
            : string.Empty;
        return "{\"type\":\"done\",\"reason\":\"" + reason + "\",\"usage\":" + Usage() + response + rewriteValue + "}";
    }

    private static string Usage() =>
        "{\"input\":1,\"output\":2,\"cacheRead\":3,\"cacheWrite\":4,\"reasoning\":1,\"cacheWrite1h\":2,\"totalTokens\":10,\"cost\":{\"input\":0.1,\"output\":0.2,\"cacheRead\":0.3,\"cacheWrite\":0.4,\"total\":1.0}}";

    private static string EmptyUsage() =>
        "{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}";

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public int RequestCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? Accept { get; private set; }

        public string? ContentType { get; private set; }

        public string? Body { get; private set; }

        public IReadOnlyDictionary<string, string> Headers { get; private set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.TryGetValues("Authorization", out var authorization)
                ? authorization.Single()
                : null;
            Accept = request.Headers.Accept.SingleOrDefault()?.MediaType;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Headers = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => string.Join(",", pair.Value),
                StringComparer.OrdinalIgnoreCase);
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _respond(request, cancellationToken);
        }
    }

    private sealed class PrefixThenWaitStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly CancellationTokenSource _disposed = new();
        private int _offset;

        public PrefixThenWaitStream(byte[] prefix)
        {
            _prefix = prefix;
        }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < _prefix.Length)
            {
                var copied = Math.Min(count, _prefix.Length - _offset);
                Array.Copy(_prefix, _offset, buffer, offset, copied);
                _offset += copied;
                return copied;
            }

            _disposed.Token.WaitHandle.WaitOne();
            return 0;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (_offset < _prefix.Length)
            {
                return Read(buffer, offset, count);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposed.Token);
            try
            {
                await Task.Delay(Timeout.Infinite, linked.Token);
            }
            catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
            {
                return 0;
            }

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !Disposed)
            {
                Disposed = true;
                _disposed.Cancel();
                _disposed.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        private readonly TaskCompletionSource<bool> _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public Task DisposedTask => _disposed.Task;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !Disposed)
            {
                Disposed = true;
                _disposed.TrySetResult(true);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NonCooperativeReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _read =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _started.Task;

        public bool Disposed { get; private set; }

        public void Release() => _read.TrySetResult(0);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult(true);
            return _read.Task;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}

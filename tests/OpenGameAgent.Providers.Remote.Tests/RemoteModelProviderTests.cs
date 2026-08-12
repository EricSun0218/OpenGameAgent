using System.Net;
using System.Text;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Providers.Remote.Tests;

public sealed class RemoteModelProviderTests
{
    [Fact]
    public async Task RoundTripsNormalizedRequestStreamTerminalHeadersAndCredentials()
    {
        var upstream = new ScriptedProvider(FullStream());
        var server = new ModelProviderProxyServer(
            upstream,
            new ModelProviderProxyServerOptions { ApiKey = "server-secret" });
        var handler = new LoopbackHandler(server);
        var remote = CreateRemote(handler, options =>
        {
            options.ApiKey = "server-secret";
            options.Headers = new Dictionary<string, string> { ["x-game-build"] = "42" };
        });
        var request = ComplexRequest();

        var events = await CollectAsync(remote.StreamAsync(request, TestContext.Current.CancellationToken));
        Assert.False(
            events.Count == 1 && events[0].Kind == ModelStreamEventKind.Failed,
            events.Count == 1 ? events[0].Response?.ErrorMessage : null);

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
            events.Select(value => value.Kind));

        var reasoningEnded = Assert.Single(events, value => value.Kind == ModelStreamEventKind.ReasoningEnded);
        Assert.Equal("plan", reasoningEnded.Content);
        var streamedReasoning = Assert.IsType<ReasoningContent>(
            reasoningEnded.Partial!.Content[reasoningEnded.ContentIndex]);
        Assert.Equal("reason-signature", streamedReasoning.Signature);

        var textEnded = Assert.Single(events, value => value.Kind == ModelStreamEventKind.TextEnded);
        Assert.Equal("hello", textEnded.Content);
        var streamedText = Assert.IsType<TextContent>(textEnded.Partial!.Content[textEnded.ContentIndex]);
        Assert.Equal("text-signature", streamedText.Signature);
        Assert.Equal(AgentTextPhase.Commentary, streamedText.Phase);

        var toolStarted = Assert.Single(events, value => value.Kind == ModelStreamEventKind.ToolCallStarted);
        var toolDelta = Assert.Single(events, value => value.Kind == ModelStreamEventKind.ToolCallDelta);
        var toolEnded = Assert.Single(events, value => value.Kind == ModelStreamEventKind.ToolCallEnded);
        Assert.Equal(toolStarted.ContentIndex, toolDelta.ContentIndex);
        Assert.Equal(toolStarted.ContentIndex, toolEnded.ContentIndex);
        Assert.Equal("move", toolStarted.ToolName);
        Assert.Equal("move", toolDelta.ToolName);
        Assert.Equal("{\"x\":1}", Assert.IsType<ToolCallContent>(toolDelta.Partial!.Content[2]).ArgumentsJson);
        var streamedCall = Assert.IsType<ToolCallContent>(toolEnded.ToolCall);
        Assert.Equal("call-1", streamedCall.Id);
        Assert.Equal("move", streamedCall.Name);
        Assert.Equal("{\"x\":1}", streamedCall.ArgumentsJson);
        Assert.Equal("tool-signature", streamedCall.ThoughtSignature);
        Assert.Equal("world", streamedCall.Namespace);

        var terminal = events[^1];
        var response = terminal.Response!;
        Assert.Equal(ModelStopReason.ToolUse, response.StopReason);
        Assert.Equal("upstream", response.Provider);
        Assert.Equal("native-api", response.Api);
        Assert.Equal("served-model", response.ResponseModel);
        Assert.Equal("response-1", response.ResponseId);
        Assert.Equal("tool_use", response.RawStopReason);
        Assert.False(response.EndTurn);
        Assert.Equal(11, response.Usage.InputTokens);
        Assert.Equal(7, response.Usage.OutputTokens);
        Assert.Equal(2, response.Usage.CacheReadTokens);
        Assert.Equal(3, response.Usage.CacheWriteTokens);
        Assert.Equal(4, response.Usage.ReasoningTokens);
        Assert.Equal(1, response.Usage.CacheWriteOneHourTokens);
        Assert.True(response.Usage.Cost.IsKnown);
        Assert.Equal(0.11, response.Usage.Cost.Input);
        Assert.Equal(0.07, response.Usage.Cost.Output);
        Assert.Equal(0.02, response.Usage.Cost.CacheRead);
        Assert.Equal(0.03, response.Usage.Cost.CacheWrite);
        var diagnostic = Assert.Single(response.Diagnostics);
        Assert.Equal("route", diagnostic.Code);
        Assert.Equal(ModelDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("{\"region\":\"test\"}", diagnostic.DataJson);
        AssertToolCallEqual(streamedCall, Assert.IsType<ToolCallContent>(response.Content[2]));

        Assert.Equal("Bearer server-secret", handler.Authorization);
        Assert.Equal("42", handler.GameBuild);
        var captured = Assert.IsType<ModelRequest>(upstream.CapturedRequest);
        AssertRequestEqual(request, captured);
    }

    [Fact]
    public async Task UnknownCostRemainsUnknownAcrossTheRemoteWire()
    {
        var upstream = new ScriptedProvider(new[]
        {
            ModelStreamEvent.Update(ModelStreamEventKind.Started, Pending()),
            ModelStreamEvent.Terminal(new ModelResponse(
                new AgentContent[] { new TextContent("ok") },
                ModelStopReason.Stop,
                new ModelUsage(2, 1))),
        });
        var remote = CreateRemote(new LoopbackHandler(new ModelProviderProxyServer(upstream)));

        var events = await CollectAsync(remote.StreamAsync(
            SimpleRequest(),
            TestContext.Current.CancellationToken));

        var usage = Assert.Single(events, item => item.IsTerminal).Response!.Usage;
        Assert.False(usage.Cost.IsKnown);
        Assert.Null(usage.Cost.TotalIfKnown);
    }

    [Fact]
    public async Task PreservesDeferredTerminalHandle()
    {
        var deferred = new DeferredModelHandle(
            "upstream",
            "model",
            "batch-api",
            "job-1",
            DateTimeOffset.UnixEpoch.AddHours(1),
            250,
            "{\"queue\":2}");
        var response = new ModelResponse(
            Array.Empty<AgentContent>(),
            ModelStopReason.Deferred,
            new ModelUsage(1),
            provider: "upstream",
            api: "batch-api",
            responseModel: "model",
            responseId: "response-2",
            rawStopReason: "deferred",
            deferred: deferred);
        var upstream = new ScriptedProvider(new[]
        {
            ModelStreamEvent.Update(ModelStreamEventKind.Started, Pending()),
            ModelStreamEvent.Terminal(response),
        });
        var remote = CreateRemote(new LoopbackHandler(new ModelProviderProxyServer(upstream)));

        var events = await CollectAsync(remote.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken));

        var terminal = events[^1].Response!;
        Assert.Equal(ModelStopReason.Deferred, terminal.StopReason);
        Assert.Equal("job-1", terminal.Deferred!.Id);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(1), terminal.Deferred.ExpiresAt);
        Assert.Equal(250, terminal.Deferred.PollAfterMilliseconds);
        Assert.Equal("{\"queue\":2}", terminal.Deferred.DataJson);
    }

    [Fact]
    public async Task AuthenticationFailureIsAnInBandTerminalAndSkipsProvider()
    {
        var upstream = new ScriptedProvider(FullStream());
        var server = new ModelProviderProxyServer(
            upstream,
            new ModelProviderProxyServerOptions { ApiKey = "correct" });
        var remote = CreateRemote(
            new LoopbackHandler(server),
            options => options.ApiKey = "wrong");

        var events = await CollectAsync(remote.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken));

        var terminal = Assert.Single(events);
        Assert.Equal(ModelStreamEventKind.Failed, terminal.Kind);
        Assert.Equal(ModelStopReason.Error, terminal.Response!.StopReason);
        Assert.Contains("Unauthorized", terminal.Response.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task InvalidUpstreamOrderFailsClosedWithOneErrorTerminal()
    {
        var pending = Pending();
        var invalid = new[]
        {
            ModelStreamEvent.Update(ModelStreamEventKind.Started, pending),
            ModelStreamEvent.Update(
                ModelStreamEventKind.TextDelta,
                new ModelResponse(new AgentContent[] { new TextContent("orphan") }, ModelStopReason.Pending),
                "orphan",
                0),
        };
        var remote = CreateRemote(
            new LoopbackHandler(new ModelProviderProxyServer(new ScriptedProvider(invalid))));

        var events = await CollectAsync(remote.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Started, events[0].Kind);
        var terminal = Assert.Single(events, value => value.IsTerminal);
        Assert.Equal(ModelStreamEventKind.Failed, terminal.Kind);
        Assert.Contains("missing or ended", terminal.Response!.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(events, value => value.Kind == ModelStreamEventKind.TextDelta);
    }

    [Fact]
    public async Task EventAfterBufferedTerminalReplacesSuccessWithOneErrorTerminal()
    {
        var pending = Pending();
        var invalid = new[]
        {
            ModelStreamEvent.Update(ModelStreamEventKind.Started, pending),
            ModelStreamEvent.Terminal(new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Stop)),
            ModelStreamEvent.Update(ModelStreamEventKind.Started, pending),
        };
        var remote = CreateRemote(
            new LoopbackHandler(new ModelProviderProxyServer(new ScriptedProvider(invalid))));

        var events = await CollectAsync(remote.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(2, events.Count);
        Assert.Equal(ModelStreamEventKind.Started, events[0].Kind);
        Assert.Equal(ModelStreamEventKind.Failed, events[1].Kind);
        Assert.Contains("after its terminal", events[1].Response!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientRejectsWireDataAfterTerminal()
    {
        var server = new ModelProviderProxyServer(new ScriptedProvider(new[]
        {
            ModelStreamEvent.Update(ModelStreamEventKind.Started, Pending()),
            ModelStreamEvent.Terminal(new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Stop)),
        }));
        var remote = CreateRemote(new AppendAfterTerminalHandler(server));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(remote.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken)));

        Assert.Contains("after its terminal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientEnforcesRequestEventResponseAndDepthLimits()
    {
        var server = new ModelProviderProxyServer(new ScriptedProvider(new[]
        {
            ModelStreamEvent.Update(ModelStreamEventKind.Started, Pending()),
            ModelStreamEvent.Terminal(new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Stop)),
        }));

        var requestLimited = CreateRemote(
            new LoopbackHandler(server),
            options => options.MaximumRequestBytes = 32);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(requestLimited.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken)));

        var eventLimited = CreateRemote(
            new LoopbackHandler(server),
            options => options.MaximumEvents = 2);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(eventLimited.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken)));

        var responseLimited = CreateRemote(
            new LoopbackHandler(server),
            options => options.MaximumResponseBytes = 64);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(responseLimited.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken)));

        var depthLimited = CreateRemote(
            new RawSseHandler("data:{\"t\":\"s\",\"v\":1,\"r\":{\"unknown\":[[[[[0]]]]]}}\n\n"),
            options => options.MaximumJsonDepth = 4);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(depthLimited.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ServerRequestDepthAndEventLimitsFailInBand()
    {
        var requestLimitedUpstream = new ScriptedProvider(FullStream());
        var requestLimitedServer = new ModelProviderProxyServer(
            requestLimitedUpstream,
            new ModelProviderProxyServerOptions { MaximumRequestBytes = 64 });
        var requestLimited = CreateRemote(new LoopbackHandler(requestLimitedServer));

        var requestEvents = await CollectAsync(
            requestLimited.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Failed, Assert.Single(requestEvents).Kind);
        Assert.Contains("request exceeded", requestEvents[0].Response!.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, requestLimitedUpstream.Calls);

        var depthLimitedUpstream = new ScriptedProvider(FullStream());
        var depthLimitedServer = new ModelProviderProxyServer(
            depthLimitedUpstream,
            new ModelProviderProxyServerOptions { MaximumJsonDepth = 4 });
        var depthLimited = CreateRemote(
            new ReplaceRequestBodyHandler(
                depthLimitedServer,
                "{\"v\":1,\"r\":{\"m\":\"model\",\"deep\":[[[[[0]]]]]}}"));

        var depthEvents = await CollectAsync(
            depthLimited.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Failed, Assert.Single(depthEvents).Kind);
        Assert.Contains("valid JSON", depthEvents[0].Response!.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, depthLimitedUpstream.Calls);

        var eventLimitedUpstream = new ScriptedProvider(FullStream());
        var eventLimitedServer = new ModelProviderProxyServer(
            eventLimitedUpstream,
            new ModelProviderProxyServerOptions { MaximumEvents = 2 });
        var eventLimited = CreateRemote(new LoopbackHandler(eventLimitedServer));

        var eventResults = await CollectAsync(
            eventLimited.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModelStreamEventKind.Started, eventResults[0].Kind);
        Assert.Equal(ModelStreamEventKind.ReasoningStarted, eventResults[1].Kind);
        Assert.Equal(ModelStreamEventKind.Failed, eventResults[2].Kind);
        Assert.Contains("event limit", eventResults[2].Response!.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationReachesWrappedProvider()
    {
        var upstream = new BlockingProvider();
        var remote = CreateRemote(
            new LoopbackHandler(new ModelProviderProxyServer(upstream)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CollectAsync(remote.StreamAsync(SimpleRequest(), cancellation.Token)));

        Assert.True(upstream.CancellationObserved);
    }

    [Fact]
    public async Task MultilineSseEventUsesIncrementalUtf8LimitAccounting()
    {
        const int exactEventBytes = 8192;
        var stream = new[]
        {
            ModelStreamEvent.Update(ModelStreamEventKind.Started, Pending()),
            ModelStreamEvent.Terminal(new ModelResponse(Array.Empty<AgentContent>(), ModelStopReason.Stop)),
        };
        var acceptedHandler = new MultilineSetupHandler(
            new ModelProviderProxyServer(new ScriptedProvider(stream)),
            exactEventBytes);
        var accepted = CreateRemote(
            acceptedHandler,
            options => options.MaximumEventBytes = exactEventBytes);

        var events = await CollectAsync(
            accepted.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(exactEventBytes, acceptedHandler.ReconstructedEventBytes);
        Assert.Equal(ModelStreamEventKind.Completed, events[^1].Kind);

        var rejectedHandler = new MultilineSetupHandler(
            new ModelProviderProxyServer(new ScriptedProvider(stream)),
            exactEventBytes);
        var rejected = CreateRemote(
            rejectedHandler,
            options => options.MaximumEventBytes = exactEventBytes - 1);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(rejected.StreamAsync(SimpleRequest(), TestContext.Current.CancellationToken)));
        Assert.Contains("event exceeded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationObservesReadStreamFaultThatArrivesLater()
    {
        var marker = "late-remote-stream-fault-" + Guid.NewGuid().ToString("N");
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> listener = (_, eventArgs) =>
        {
            if (eventArgs.Exception.Flatten().InnerExceptions.Any(value => value.Message == marker))
            {
                Interlocked.Exchange(ref unobserved, 1);
                eventArgs.SetObserved();
            }
        };
        TaskScheduler.UnobservedTaskException += listener;
        try
        {
            var pendingTask = await CancelBeforeReadStreamFaultAsync(marker);
            for (var attempt = 0; attempt < 10 && pendingTask.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.False(pendingTask.IsAlive);
            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= listener;
        }
    }

    [Fact]
    public async Task DisposingStreamingContentSuppressesProviderCancellationCallbackFailures()
    {
        var provider = new ThrowingCancellationCallbackProvider();
        var server = new ModelProviderProxyServer(provider);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://proxy.example.test/v1/model-stream")
        {
            Content = new StringContent(
                "{\"v\":1,\"r\":{\"m\":\"model\",\"s\":\"\",\"g\":[],\"o\":[],\"p\":{},\"r\":\"run\",\"n\":1}}",
                Encoding.UTF8,
                "application/json"),
        };
        using var response = await server.HandleAsync(request, TestContext.Current.CancellationToken);
        var copyTask = response.Content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
        await provider.CallbackRegistered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var exception = Record.Exception(response.Dispose);

        Assert.Null(exception);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copyTask);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CancelBeforeReadStreamFaultAsync(string marker)
    {
        LateFaultingReadContent? content = new();
        LateFaultingReadHandler? handler = new(content);
        RemoteModelProvider? remote = CreateRemote(handler);
        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyList<ModelStreamEvent>>? pending = CollectAsync(
            remote.StreamAsync(SimpleRequest(), cancellation.Token));
        await content.ReadRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var weakTask = new WeakReference(content.PendingStreamTask);
        content.Fail(new InvalidOperationException(marker));
        await Task.Delay(25);

        pending = null;
        remote = null;
        handler = null;
        content = null;
        return weakTask;
    }

    private static RemoteModelProvider CreateRemote(
        HttpMessageHandler handler,
        Action<RemoteModelProviderOptions>? configure = null)
    {
        var options = new RemoteModelProviderOptions(
            new HttpClient(handler),
            new Uri("https://proxy.example.test/v1/model-stream"));
        configure?.Invoke(options);
        return new RemoteModelProvider(options);
    }

    private static ModelRequest SimpleRequest() => new(
        "model",
        string.Empty,
        Array.Empty<AgentMessage>(),
        Array.Empty<ToolDefinition>(),
        new ModelParameters(),
        null,
        "run",
        1);

    private static ModelRequest ComplexRequest()
    {
        var priorCall = new ToolCallContent("prior-call", "inspect", "{\"target\":\"npc\"}", "prior-signature", "world");
        var usage = new ModelUsage(2, 1, cost: new ModelCost(0.02, 0.01));
        var messages = new AgentMessage[]
        {
            new(
                AgentRole.User,
                new AgentContent[]
                {
                    new TextContent("look", "user-signature", AgentTextPhase.FinalAnswer),
                    new JsonContent("{\"tick\":1.5}"),
                    new ResourceContent("game://npc/1", "application/json", "npc"),
                    new BinaryContent(AgentMediaKind.Image, "aW1hZ2U=", "image/png", "portrait"),
                },
                DateTimeOffset.UnixEpoch,
                metadata: new Dictionary<string, string> { ["scene"] = "village" }),
            new(
                AgentRole.Custom,
                new AgentContent[] { new JsonContent("{\"weather\":\"rain\"}") },
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                customRole: "world_state"),
            new(
                AgentRole.Assistant,
                new AgentContent[]
                {
                    new ReasoningContent("prior plan", "reasoning-signature"),
                    priorCall,
                },
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                model: "old-model",
                stopReason: ModelStopReason.ToolUse,
                usage: usage,
                provider: "old-provider",
                api: "old-api",
                responseModel: "served-old-model",
                responseId: "old-response",
                rawStopReason: "tool_use",
                endTurn: false,
                diagnostics: new[] { new ModelDiagnostic("old", "Old route") }),
            AgentMessage.ToolResult(
                priorCall,
                new ToolResult(
                    new AgentContent[] { new TextContent("clear"), new ResourceContent("game://result/1", "text/plain") },
                    detailsJson: "{\"latency\":5}",
                    usage: usage,
                    addedToolNames: new[] { "move" }),
                DateTimeOffset.UnixEpoch.AddSeconds(3)),
        };
        return new ModelRequest(
            "game-model",
            "rules",
            messages,
            new[]
            {
                new ToolDefinition(
                    "move",
                    "Move in the world",
                    "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"}}}",
                    ToolConstrainedSampling.Grammar(openAiRegex: "[0-9]+")),
            },
            new ModelParameters
            {
                Temperature = 0.2,
                MaxOutputTokens = 123,
                ReasoningLevel = "high",
                ReasoningBudgets = new Dictionary<string, int> { ["high"] = 4096 },
                SamplingParametersJson = "{\"top_p\":0.9}",
                Transport = ModelTransport.ServerSentEvents,
                CacheRetention = ModelCacheRetention.Long,
                WebSocketConnectTimeoutMilliseconds = 500,
                Deferred = true,
                DeferredWindow = ModelDeferredWindow.OneHour,
                MetadataJson = "{\"trace\":true}",
                Extensions = new Dictionary<string, string> { ["route"] = "fast" },
            },
            "session-1",
            "run-1",
            2);
    }

    private static IReadOnlyList<ModelStreamEvent> FullStream()
    {
        var reasoning = new ReasoningContent("plan", "reason-signature");
        var text = new TextContent("hello", "text-signature", AgentTextPhase.Commentary);
        var call = new ToolCallContent("call-1", "move", "{\"x\":1}", "tool-signature", "world");
        var content = new AgentContent[] { reasoning, text, call };
        var setup = Pending();
        var reasoningStarted = Pending(new ReasoningContent(string.Empty, "reason-signature"));
        var reasoningComplete = Pending(reasoning);
        var textStarted = Pending(reasoning, new TextContent(string.Empty, "text-signature", AgentTextPhase.Commentary));
        var textComplete = Pending(reasoning, text);
        var toolStarted = Pending(reasoning, text, new ToolCallContent("call-1", "move", "{}", "tool-signature", "world"));
        var allComplete = Pending(content);
        var usage = new ModelUsage(
            11,
            7,
            2,
            3,
            4,
            1,
            new ModelCost(0.11, 0.07, 0.02, 0.03));
        var response = new ModelResponse(
            content,
            ModelStopReason.ToolUse,
            usage,
            provider: "upstream",
            api: "native-api",
            responseModel: "served-model",
            responseId: "response-1",
            rawStopReason: "tool_use",
            endTurn: false,
            diagnostics: new[]
            {
                new ModelDiagnostic("route", "Fallback route", ModelDiagnosticSeverity.Warning, "{\"region\":\"test\"}"),
            });
        return new[]
        {
            ModelStreamEvent.Update(ModelStreamEventKind.Started, setup),
            ModelStreamEvent.Update(ModelStreamEventKind.ReasoningStarted, reasoningStarted, contentIndex: 0),
            ModelStreamEvent.Update(ModelStreamEventKind.ReasoningDelta, reasoningComplete, "plan", 0),
            ModelStreamEvent.Update(ModelStreamEventKind.ReasoningEnded, reasoningComplete, contentIndex: 0, content: "plan"),
            ModelStreamEvent.Update(ModelStreamEventKind.TextStarted, textStarted, contentIndex: 1),
            ModelStreamEvent.Update(ModelStreamEventKind.TextDelta, textComplete, "hello", 1),
            ModelStreamEvent.Update(ModelStreamEventKind.TextEnded, textComplete, contentIndex: 1, content: "hello"),
            ModelStreamEvent.Update(ModelStreamEventKind.ToolCallStarted, toolStarted, contentIndex: 2, toolCallId: "call-1"),
            ModelStreamEvent.Update(ModelStreamEventKind.ToolCallDelta, allComplete, "{\"x\":1}", 2, "call-1", "move"),
            ModelStreamEvent.Update(ModelStreamEventKind.ToolCallEnded, allComplete, contentIndex: 2, toolCall: call),
            ModelStreamEvent.Terminal(response),
        };
    }

    private static ModelResponse Pending(params AgentContent[] content) => new(
        content,
        ModelStopReason.Pending,
        provider: "upstream",
        api: "native-api",
        responseModel: "served-model",
        responseId: "response-1");

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in stream)
        {
            result.Add(item);
        }

        return result;
    }

    private static void AssertToolCallEqual(ToolCallContent expected, ToolCallContent actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.ArgumentsJson, actual.ArgumentsJson);
        Assert.Equal(expected.ThoughtSignature, actual.ThoughtSignature);
        Assert.Equal(expected.Namespace, actual.Namespace);
    }

    private static void AssertRequestEqual(ModelRequest expected, ModelRequest actual)
    {
        Assert.Equal(expected.Model, actual.Model);
        Assert.Equal(expected.SystemPrompt, actual.SystemPrompt);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.Messages.Count, actual.Messages.Count);
        Assert.Equal(expected.Messages[0].Timestamp, actual.Messages[0].Timestamp);
        Assert.Equal("village", actual.Messages[0].Metadata["scene"]);
        Assert.Equal("{\"tick\":1.5}", Assert.IsType<JsonContent>(actual.Messages[0].Content[1]).Json);
        Assert.Equal(AgentMediaKind.Image, Assert.IsType<BinaryContent>(actual.Messages[0].Content[3]).MediaKind);
        Assert.Equal("world_state", actual.Messages[1].CustomRole);
        Assert.Equal("old-response", actual.Messages[2].ResponseId);
        Assert.Equal("move", Assert.Single(actual.Messages[3].AddedToolNames));
        var tool = Assert.Single(actual.Tools);
        Assert.Equal("move", tool.Name);
        Assert.Equal(ToolConstrainedSamplingKind.Grammar, tool.ConstrainedSampling!.Kind);
        Assert.Equal("[0-9]+", tool.ConstrainedSampling.OpenAiRegex);
        Assert.Equal(0.2, actual.Parameters.Temperature);
        Assert.Equal(123, actual.Parameters.MaxOutputTokens);
        Assert.Equal(4096, actual.Parameters.ReasoningBudgets["high"]);
        Assert.Equal(ModelTransport.ServerSentEvents, actual.Parameters.Transport);
        Assert.Equal(ModelCacheRetention.Long, actual.Parameters.CacheRetention);
        Assert.True(actual.Parameters.Deferred);
        Assert.Equal(ModelDeferredWindow.OneHour, actual.Parameters.DeferredWindow);
        Assert.Equal("fast", actual.Parameters.Extensions["route"]);
    }

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelStreamEvent> _events;

        public ScriptedProvider(IReadOnlyList<ModelStreamEvent> events)
        {
            _events = events;
        }

        public int Calls { get; private set; }

        public ModelRequest? CapturedRequest { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            CapturedRequest = request;
            foreach (var item in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        public bool CancellationObserved { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, Pending());
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class ThrowingCancellationCallbackProvider : IModelProvider
    {
        public TaskCompletionSource<bool> CallbackRegistered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                static () => throw new InvalidOperationException("hostile cancellation callback"));
            CallbackRegistered.TrySetResult(true);
            yield return ModelStreamEvent.Update(ModelStreamEventKind.Started, Pending());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class LoopbackHandler : HttpMessageHandler
    {
        private readonly ModelProviderProxyServer _server;

        public LoopbackHandler(ModelProviderProxyServer server)
        {
            _server = server;
        }

        public string? Authorization { get; private set; }

        public string? GameBuild { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.TryGetValues("Authorization", out var authorization)
                ? authorization.Single()
                : null;
            GameBuild = request.Headers.TryGetValues("x-game-build", out var gameBuild)
                ? gameBuild.Single()
                : null;
            return _server.HandleAsync(request, cancellationToken);
        }
    }

    private sealed class AppendAfterTerminalHandler : HttpMessageHandler
    {
        private readonly ModelProviderProxyServer _server;

        public AppendAfterTerminalHandler(ModelProviderProxyServer server)
        {
            _server = server;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var original = await _server.HandleAsync(request, cancellationToken);
            var body = await original.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    body + "data:{\"t\":\"e\",\"k\":0}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }

    private sealed class RawSseHandler : HttpMessageHandler
    {
        private readonly string _body;

        public RawSseHandler(string body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream"),
            });
    }

    private sealed class MultilineSetupHandler : HttpMessageHandler
    {
        private const int MiddleLines = 128;
        private readonly ModelProviderProxyServer _server;
        private readonly int _targetBytes;

        public MultilineSetupHandler(ModelProviderProxyServer server, int targetBytes)
        {
            _server = server;
            _targetBytes = targetBytes;
        }

        public int ReconstructedEventBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var original = await _server.HandleAsync(request, cancellationToken);
            var body = await original.Content.ReadAsStringAsync(cancellationToken);
            var eventEnd = body.IndexOf("\n\n", StringComparison.Ordinal);
            if (eventEnd < 0 || !body.StartsWith("data:", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The generated proxy response did not contain a setup frame.");
            }

            var json = body.Substring(5, eventEnd - 5)
                .Replace("upstream", "上游", StringComparison.Ordinal);
            var split = json.IndexOf(',', StringComparison.Ordinal) + 1;
            if (split <= 0)
            {
                throw new InvalidDataException("The generated setup frame cannot be split safely.");
            }

            var jsonBytes = Encoding.UTF8.GetByteCount(json);
            var newlineBytes = MiddleLines + 1;
            var paddingBytes = _targetBytes - jsonBytes - newlineBytes;
            if (paddingBytes < MiddleLines)
            {
                throw new InvalidDataException("The target setup frame size is too small for the multiline fixture.");
            }

            var transformed = new StringBuilder(body.Length + paddingBytes + (MiddleLines * 6));
            transformed.Append("data:").Append(json, 0, split).Append('\n');
            var remaining = paddingBytes;
            for (var index = 0; index < MiddleLines; index++)
            {
                var count = remaining / (MiddleLines - index);
                transformed.Append("data:").Append('\t', count).Append('\n');
                remaining -= count;
            }

            transformed.Append("data:").Append(json, split, json.Length - split).Append("\n\n");
            transformed.Append(body, eventEnd + 2, body.Length - eventEnd - 2);
            ReconstructedEventBytes = jsonBytes + newlineBytes + paddingBytes;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(transformed.ToString(), Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class LateFaultingReadHandler : HttpMessageHandler
    {
        private readonly LateFaultingReadContent _content;

        public LateFaultingReadHandler(LateFaultingReadContent content)
        {
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _content });
    }

    private sealed class LateFaultingReadContent : HttpContent
    {
        private readonly TaskCompletionSource<Stream> _stream =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LateFaultingReadContent()
        {
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        }

        public TaskCompletionSource<bool> ReadRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Stream> PendingStreamTask => _stream.Task;

        public void Fail(Exception exception) => _stream.TrySetException(exception);

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            ReadRequested.TrySetResult(true);
            return _stream.Task;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class ReplaceRequestBodyHandler : HttpMessageHandler
    {
        private readonly ModelProviderProxyServer _server;
        private readonly string _body;

        public ReplaceRequestBodyHandler(ModelProviderProxyServer server, string body)
        {
            _server = server;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var replacement = new HttpRequestMessage(HttpMethod.Post, request.RequestUri)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            foreach (var header in request.Headers)
            {
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return await _server.HandleAsync(replacement, cancellationToken);
        }
    }
}

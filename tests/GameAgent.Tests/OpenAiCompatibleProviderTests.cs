using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;
using GameAgent.Providers.OpenAICompatible;

namespace GameAgent.Tests;

public sealed class OpenAiCompatibleProviderTests
{
    [Fact]
    public void ExposesStableModelAndTransportRouteMetadata()
    {
        var provider = CreateProvider(new FakeTransport(string.Empty));
        var source = Assert.IsAssignableFrom<IProviderRouteMetadataSource>(
            provider);

        Assert.Equal("deepseek-v4-pro", source.RouteMetadata.ModelId);
        Assert.Equal(
            "openai.chat-completions.sse.v1",
            source.RouteMetadata.TransportDialect);
        Assert.Equal(
            "openai-compatible.route-policy.v4",
            source.RouteMetadata.RoutePolicyVersion);
        Assert.Equal(
            ProviderRequestFamily.ChatCompletions,
            source.RouteMetadata.DialectContract.RequestFamily);
        Assert.True(source.RouteMetadata.HasBoundDialectSemantics);
        Assert.Equal(
            ProviderStreamFraming.ServerSentEvents,
            source.RouteMetadata.DialectContract.StreamFraming);
        Assert.False(
            source.RouteMetadata.DialectContract
                .SupportsOpaqueContinuationState);
        Assert.Equal(
            "application/json; charset=utf-8",
            source.RouteMetadata.DialectContract.RequestContentType);
        Assert.True(
            CanonicalJsonDigest.IsSha256(
                source.RouteMetadata.DialectContract.SemanticDigest));
        Assert.True(
            CanonicalJsonDigest.IsSha256(
                source.RouteMetadata.RoutePolicyDigest));
        var estimator =
            Assert.IsAssignableFrom<IProviderPromptTokenEstimator>(provider);
        Assert.Equal("calibrating:script-aware", estimator.EstimatorId);
        Assert.Equal("1:1", estimator.Version);
        Assert.Equal(128, provider.Capabilities.MaxTools);
    }

    [Fact]
    public void RoutePolicyDigestIncludesPromptEstimatorIdentityAndVersion()
    {
        static string Digest(string estimatorId, string version)
        {
            var provider = new OpenAiCompatibleStreamingProvider(
                new OpenAiCompatibleProviderOptions(),
                new StaticBearerTokenSource("never-persisted"),
                new FakeTransport(string.Empty),
                new FixedProviderPromptEstimator(
                    estimatorId,
                    version,
                    8));
            return provider.RouteMetadata.RoutePolicyDigest;
        }

        var baseline = Digest("model-tokenizer", "1");

        Assert.NotEqual(baseline, Digest("other-tokenizer", "1"));
        Assert.NotEqual(baseline, Digest("model-tokenizer", "2"));
        Assert.Equal(baseline, Digest("model-tokenizer", "1"));
    }

    [Fact]
    public async Task CompletedUsageCalibratesDefaultPromptEstimator()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                """
                {"choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":1,"total_tokens":1001}}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var estimator =
            Assert.IsAssignableFrom<IProviderPromptTokenEstimator>(provider);
        var messages = new[]
        {
            Message(
                NormalizedRoles.User,
                NormalizedContentPart.FromText(
                    "\u6c5f\u6e56\u4eba\u7269\u6b63\u5728\u884c\u52a8"))
        };
        var before = estimator.EstimatePromptTokens(
            messages,
            Array.Empty<ToolDescriptor>());
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new SystemRuntimeDelay(),
            new StableIds());

        _ = await runner.RunAsync(
            "calibration-run",
            "calibration-attempt",
            "calibration-turn",
            messages,
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None);

        Assert.True(
            estimator.EstimatePromptTokens(
                messages,
                Array.Empty<ToolDescriptor>()) > before);
    }

    [Fact]
    public async Task DispatchEvidenceMatchesExactBytesReceivedByTransport()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                """
                {"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new SystemRuntimeDelay(),
            new StableIds());
        ProviderDispatchNotice? dispatch = null;

        _ = await runner.RunAsync(
            "run-1",
            "attempt-1",
            "turn-1",
            new[]
            {
                Message(
                    NormalizedRoles.User,
                    NormalizedContentPart.FromText("wire evidence"))
            },
            Array.Empty<ToolDescriptor>(),
            new AttemptFence(),
            null,
            CancellationToken.None,
            onDispatch: notice =>
            {
                dispatch = notice;
                return default;
            });

        Assert.NotNull(dispatch);
        Assert.True(dispatch!.WireRequestEvidence.IsAvailable);
        Assert.Equal(
            transport.LastRequest!.Body.Length,
            dispatch.WireRequestEvidence.PayloadByteLength);
        Assert.Equal(
            transport.LastRequest.ContentType,
            dispatch.WireRequestEvidence.ContentType);
        Assert.Equal(
            LowerSha256(transport.LastRequest.Body),
            dispatch.WireRequestEvidence.PayloadSha256);
        Assert.Equal(
            dispatch.RouteIdentity.RouteDigest,
            dispatch.WireRequestEvidence.ProviderRouteDigest);
        Assert.Equal(
            dispatch.RouteIdentity.DialectSemanticDigest,
            dispatch.WireRequestEvidence.DialectSemanticDigest);
    }

    [Fact]
    public async Task AnyFinalBodyChangeChangesWireDigest()
    {
        async Task<string> DigestAsync(string prompt)
        {
            var transport = new FakeTransport(
                Sse(
                    """
                    {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                    """,
                    """
                    {"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                    """,
                    "[DONE]"));
            var provider = CreateProvider(transport);
            var runner = new ProviderAttemptRunner(
                new[] { provider },
                new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
                new SystemRuntimeDelay(),
                new StableIds());
            string? digest = null;
            _ = await runner.RunAsync(
                "run-1",
                "attempt-1",
                "turn-1",
                new[]
                {
                    Message(
                        NormalizedRoles.User,
                        NormalizedContentPart.FromText(prompt))
                },
                Array.Empty<ToolDescriptor>(),
                new AttemptFence(),
                null,
                CancellationToken.None,
                onDispatch: notice =>
                {
                    digest =
                        notice.WireRequestEvidence.PayloadSha256;
                    return default;
                });
            return Assert.IsType<string>(digest);
        }

        Assert.NotEqual(
            await DigestAsync("body-A"),
            await DigestAsync("body-B"));
    }

    [Fact]
    public async Task PreparedBodyIsClearedAfterTransportConsumesIt()
    {
        var transport = new RetainingBodyTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);

        _ = await CollectAsync(
            provider.StreamAsync(Request(), CancellationToken.None));

        Assert.NotNull(transport.ObservedBody);
        Assert.NotEmpty(transport.ObservedBody!);
        Assert.All(
            transport.ObservedBody!,
            value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void RoutePolicyDigestChangesWithEndpointLayoutAndPricing()
    {
        static ProviderRouteIdentity Identity(
            Uri baseUri,
            string path = "/chat/completions",
            int maxOutputTokens = 32_768,
            string cacheMissPrice = "0.435",
            int maxSseEventCharacters = 2 * 1024 * 1024,
            int maxSseLineCharacters = 512 * 1024)
        {
            var provider = new OpenAiCompatibleStreamingProvider(
                new OpenAiCompatibleProviderOptions
                {
                    ProviderId = "stable-provider",
                    BaseUri = baseUri,
                    ChatCompletionsPath = path,
                    Model = "stable-model",
                    MaxOutputTokens = maxOutputTokens,
                    InputCacheMissUsdPerMillionTokens =
                        cacheMissPrice,
                    MaxSseEventCharacters = maxSseEventCharacters,
                    MaxSseLineCharacters = maxSseLineCharacters
                },
                new StaticBearerTokenSource("never-persisted"),
                new FakeTransport(string.Empty));
            return new ProviderRouteIdentity(
                provider.ProviderId,
                provider.RouteMetadata,
                provider.Capabilities);
        }

        var baseline = Identity(new Uri("https://api.example.com"));
        var equivalentPrice = Identity(
            new Uri("https://api.example.com"),
            cacheMissPrice: "0.4350");

        Assert.Equal(
            baseline.RoutePolicyDigest,
            equivalentPrice.RoutePolicyDigest);
        Assert.Equal(baseline.RouteDigest, equivalentPrice.RouteDigest);
        Assert.NotEqual(
            baseline.RouteDigest,
            Identity(new Uri("https://other.example.com")).RouteDigest);
        Assert.NotEqual(
            baseline.RouteDigest,
            Identity(new Uri("https://api.example.com/v2/")).RouteDigest);
        Assert.NotEqual(
            baseline.RouteDigest,
            Identity(
                new Uri("https://api.example.com"),
                "/v2/chat/completions").RouteDigest);
        Assert.NotEqual(
            baseline.RouteDigest,
            Identity(
                new Uri("https://api.example.com"),
                maxOutputTokens: 16_384).RouteDigest);
        Assert.NotEqual(
            baseline.RouteDigest,
            Identity(
                new Uri("https://api.example.com"),
                cacheMissPrice: "0.5").RouteDigest);
        Assert.NotEqual(
            baseline.RouteDigest,
            Identity(
                new Uri("https://api.example.com"),
                maxSseEventCharacters: 1_048_576).RouteDigest);
        Assert.NotEqual(
            baseline.RouteDigest,
            Identity(
                new Uri("https://api.example.com"),
                maxSseLineCharacters: 262_144).RouteDigest);
        Assert.DoesNotContain(
            "example.com",
            baseline.RoutePolicyDigest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RoutePolicyDigestCoversEveryConfigurableDialectField()
    {
        static string Digest(Action<OpenAiCompatibleProviderOptions> change)
        {
            var options = new OpenAiCompatibleProviderOptions
            {
                ProviderId = "stable-provider",
                BaseUri = new Uri("https://api.example.com"),
                Model = "stable-model"
            };
            change(options);
            var provider = new OpenAiCompatibleStreamingProvider(
                options,
                new StaticBearerTokenSource("never-persisted"),
                new FakeTransport(string.Empty));
            return provider.RouteMetadata.RoutePolicyDigest;
        }

        var baseline = Digest(_ => { });
        Assert.NotEqual(
            baseline,
            Digest(options => options.MaxOutputTokensField =
                "max_completion_tokens"));
        Assert.NotEqual(
            baseline,
            Digest(options =>
                options.ReasoningEffortRequiresThinkingMode = false));
        Assert.NotEqual(
            baseline,
            Digest(options => options.ToolChoice = "auto"));
        Assert.NotEqual(
            baseline,
            Digest(options => options.ParallelToolCalls = true));
        Assert.NotEqual(
            baseline,
            Digest(options => options.StrictToolSchemas = true));
        Assert.NotEqual(
            baseline,
            Digest(options =>
                options.ReasoningContentReplayRequiresThinkingMode = false));
    }

    [Theory]
    [InlineData(true, "enabled", true, true, true)]
    [InlineData(false, "enabled", false, true, false)]
    [InlineData(true, "disabled", false, true, false)]
    [InlineData(false, "disabled", false, true, false)]
    [InlineData(true, null, false, false, false)]
    [InlineData(false, null, false, false, false)]
    public async Task ReasoningInputCapabilityMatchesEncodedWireCondition(
        bool replayReasoningContent,
        string? thinkingMode,
        bool expectedCapability,
        bool expectedThinking,
        bool expectedReasoning)
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ReplayReasoningContent = replayReasoningContent,
                ThinkingMode = thinkingMode
            },
            new StaticBearerTokenSource("test-secret"),
            transport);
        var request = Request(
            NormalizedTranscript.AssistantResponse(
                "assistant-1",
                text: "visible",
                reasoningContent: "private reasoning",
                Array.Empty<ModelToolCall>(),
                DateTimeOffset.UnixEpoch));

        _ = await CollectAsync(provider.StreamAsync(request, default));

        Assert.Equal(
            expectedCapability,
            provider.Capabilities.ReasoningInput);
        using var body = JsonDocument.Parse(transport.LastRequest!.Body);
        Assert.Equal(
            expectedThinking,
            body.RootElement.TryGetProperty("thinking", out _));
        var encodedMessage =
            body.RootElement.GetProperty("messages")[0];
        Assert.Equal(
            expectedReasoning,
            encodedMessage.TryGetProperty(
                "reasoning_content",
                out var reasoning));
        if (expectedReasoning)
        {
            Assert.Equal("private reasoning", reasoning.GetString());
        }
    }

    [Fact]
    public async Task CanReplayReasoningWithoutVendorThinkingToggle()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ThinkingMode = null,
                ReasoningEffort = null,
                ReplayReasoningContent = true,
                ReasoningContentReplayRequiresThinkingMode = false
            },
            new StaticBearerTokenSource("test-secret"),
            transport);
        var request = Request(
            NormalizedTranscript.AssistantResponse(
                "assistant-1",
                text: "visible",
                reasoningContent: "reasoning-state",
                Array.Empty<ModelToolCall>(),
                DateTimeOffset.UnixEpoch));

        _ = await CollectAsync(provider.StreamAsync(request, default));

        Assert.True(provider.Capabilities.ReasoningInput);
        using var body = JsonDocument.Parse(transport.LastRequest!.Body);
        Assert.Equal(
            "reasoning-state",
            body.RootElement.GetProperty("messages")[0]
                .GetProperty("reasoning_content")
                .GetString());
    }

    [Fact]
    public async Task EncodesTypedInputAndDeepSeekToolHistory()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var request = Request(
            Message(
                NormalizedRoles.User,
                NormalizedContentPart.FromJson(Json("""{"hp":7,"alarm":true}"""))),
            NormalizedTranscript.AssistantResponse(
                "assistant-1",
                text: string.Empty,
                reasoningContent: "private reasoning",
                new[]
                {
                    new ModelToolCall
                    {
                        ToolCallId = "call-1",
                        Name = "inspect_state",
                        Arguments = Json("""{"entityId":"npc-1"}""")
                    }
                },
                DateTimeOffset.UnixEpoch),
            new NormalizedMessage
            {
                MessageId = "tool-1",
                Role = NormalizedRoles.Tool,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Parts = new List<NormalizedContentPart>
                {
                    NormalizedContentPart.FromToolResult(
                        "call-1",
                        "inspect_state",
                        Json("""{"ok":true}"""))
                }
            });
        request.Tools = new[]
        {
            Tool(
                "inspect_state",
                """
                {"type":"object","properties":{"entityId":{"type":"string"}},"required":["entityId"],"additionalProperties":false}
                """)
        };
        request.MaxOutputTokens = 17;

        _ = await CollectAsync(provider.StreamAsync(request, default));

        using var body = JsonDocument.Parse(transport.LastRequest!.Body);
        var root = body.RootElement;
        Assert.Equal("deepseek-v4-pro", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(17, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(
            "enabled",
            root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("tool_choice", out _));
        Assert.Contains(
            "\"hp\":7",
            root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(
            "private reasoning",
            root.GetProperty("messages")[1]
                .GetProperty("reasoning_content")
                .GetString());
        Assert.Equal(
            string.Empty,
            root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(
            "call-1",
            root.GetProperty("messages")[2]
                .GetProperty("tool_call_id")
                .GetString());
        Assert.Equal(
            JsonValueKind.Object,
            root.GetProperty("tools")[0]
                .GetProperty("function")
                .GetProperty("parameters")
                .ValueKind);
        Assert.DoesNotContain("test-secret", Encoding.UTF8.GetString(
            transport.LastRequest.Body));
    }

    [Fact]
    public async Task EncodesConfigurableStandardChatDialectFields()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                BaseUri = new Uri("https://api.example.com"),
                Model = "reasoning-model",
                MaxOutputTokensField = "max_completion_tokens",
                ThinkingMode = null,
                ReasoningEffort = "low",
                ReasoningEffortRequiresThinkingMode = false,
                ReplayReasoningContent = false,
                ToolChoice = "auto",
                ParallelToolCalls = true,
                StrictToolSchemas = true
            },
            new StaticBearerTokenSource("test-secret"),
            transport);
        var request = Request(Message(
            NormalizedRoles.User,
            NormalizedContentPart.FromText("inspect")));
        request.MaxOutputTokens = 23;
        request.Tools = new[]
        {
            Tool(
                "inspect_state",
                """
                {"type":"object","properties":{},"additionalProperties":false}
                """)
        };

        _ = await CollectAsync(provider.StreamAsync(request, default));

        using var body = JsonDocument.Parse(transport.LastRequest!.Body);
        var root = body.RootElement;
        Assert.Equal(
            23,
            root.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.True(
            root.GetProperty("tools")[0]
                .GetProperty("function")
                .GetProperty("strict")
                .GetBoolean());
    }

    [Fact]
    public async Task EncodesValidatedPerOperationInferenceControls()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                SupportsSeed = true,
                SupportsPromptCacheKey = true,
                SupportsPromptCacheRetention = true
            },
            new StaticBearerTokenSource("test-secret"),
            transport);
        var request = Request();
        request.Inference = new ModelInferenceOptions
        {
            ReasoningEnabled = false,
            Temperature = 0.25,
            Seed = 17,
            PromptCachingEnabled = true,
            PromptCacheKey = "world-prime",
            PromptCacheRetention = PromptCacheRetentions.OneHour
        };

        _ = await CollectAsync(provider.StreamAsync(request, default));

        using var body = JsonDocument.Parse(transport.LastRequest!.Body);
        var root = body.RootElement;
        Assert.Equal(
            "disabled",
            root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(0.25, root.GetProperty("temperature").GetDouble());
        Assert.Equal(17, root.GetProperty("seed").GetInt32());
        Assert.Equal(
            "world-prime",
            root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal(
            "1h",
            root.GetProperty("prompt_cache_retention").GetString());
    }

    [Fact]
    public async Task ExplicitReasoningDisableSuppressesDefaultEffort()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ReasoningEffort = ModelReasoningEfforts.High,
                ReasoningEffortRequiresThinkingMode = false
            },
            new StaticBearerTokenSource("test-secret"),
            transport);
        var request = Request();
        request.Inference = new ModelInferenceOptions
        {
            ReasoningEnabled = false
        };

        _ = await CollectAsync(provider.StreamAsync(request, default));

        using var body = JsonDocument.Parse(transport.LastRequest!.Body);
        var root = body.RootElement;
        Assert.Equal(
            "disabled",
            root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task NoneEffortWithoutBooleanIsAnExplicitDisable()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ReasoningEffortRequiresThinkingMode = false
            },
            new StaticBearerTokenSource("test-secret"),
            transport);
        var request = Request();
        request.Inference = new ModelInferenceOptions
        {
            ReasoningEffort = ModelReasoningEfforts.None
        };

        _ = await CollectAsync(provider.StreamAsync(request, default));

        using var body = JsonDocument.Parse(transport.LastRequest!.Body);
        var root = body.RootElement;
        Assert.Equal(
            "disabled",
            root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(
            ModelReasoningEfforts.None,
            root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task UnsupportedPerOperationControlFailsBeforeTransport()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = CreateProvider(transport);
        var request = Request();
        request.Inference = new ModelInferenceOptions
        {
            ReasoningEnabled = true,
            ReasoningTokenBudget = 2_048
        };

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(request, default)));

        Assert.Equal(
            "provider_inference_control_unsupported",
            error.Code);
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task PromptCacheBypassWithoutWireMappingFailsBeforeTransport()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = CreateProvider(transport);
        var request = Request();
        request.Inference = new ModelInferenceOptions
        {
            PromptCachingEnabled = false
        };

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => CollectAsync(provider.StreamAsync(request, default)));

        Assert.Equal(
            "provider_inference_control_unsupported",
            error.Code);
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task RequiredToolChoiceWithoutToolsFailsBeforeTransport()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ToolChoice = "required"
            },
            new StaticBearerTokenSource("test-secret"),
            transport);

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await CollectAsync(
                provider.StreamAsync(Request(), default)));

        Assert.Equal(
            "provider_tool_choice_requires_tools",
            error.Code);
        Assert.Equal("validation", error.Category);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Null(transport.LastRequest);
    }

    [Theory]
    [InlineData("tokens")]
    [InlineData("Max_Tokens")]
    public void RejectsUnknownOutputTokenField(string field)
    {
        Assert.Throws<ArgumentException>(
            () => new OpenAiCompatibleStreamingProvider(
                new OpenAiCompatibleProviderOptions
                {
                    MaxOutputTokensField = field
                },
                new StaticBearerTokenSource("test-secret"),
                new FakeTransport(string.Empty)));
    }

    [Fact]
    public async Task EncodesStrictReceiptEvidenceWrapperOnTheWire()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var receipt = new ActionReceipt
        {
            OperationId = "operation-1",
            Revision = 2,
            Status = ReceiptStatuses.Succeeded,
            Result = Json("""{"changed":true}"""),
            Retryable = false,
            CommittedAt = DateTimeOffset.UnixEpoch,
            ReceivedAt = DateTimeOffset.UnixEpoch
        };
        var presentation =
            FinalOutputAdmissionCodec.ForModelPresentation(
                receipt,
                "action-receipt:operation-1:2");
        var request = Request(
            NormalizedTranscript.AssistantToolCalls(
                "assistant-1",
                new[]
                {
                    new ModelToolCall
                    {
                        ToolCallId = "call-1",
                        Name = "set_flag",
                        Arguments = Json("""{"value":true}""")
                    }
                },
                DateTimeOffset.UnixEpoch),
            Message(
                NormalizedRoles.Tool,
                NormalizedContentPart.FromToolResult(
                    "call-1",
                    "set_flag",
                    presentation)));

        _ = await CollectAsync(provider.StreamAsync(request, default));

        using var body = JsonDocument.Parse(transport.LastRequest!.Body);
        var content = body.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
        using var encoded = JsonDocument.Parse(content);
        Assert.Equal(
            FinalOutputAdmissionControl.EvidencePresentationContentType,
            encoded.RootElement
                .GetProperty("contentType")
                .GetString());
        Assert.Equal(
            "operation-1",
            encoded.RootElement
                .GetProperty("receipt")
                .GetProperty("operationId")
                .GetString());
        var reference = encoded.RootElement.GetProperty(
            "evidenceReference");
        Assert.Equal(2, reference.GetProperty("revision").GetInt64());
        Assert.Equal(
            "action-receipt:operation-1:2",
            reference.GetProperty(
                    FinalOutputAdmissionControl
                        .EvidenceSourceEventIdPropertyName)
                .GetString());
    }

    [Fact]
    public async Task ParsesReasoningFragmentedToolsUsageAndCompletion()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"reasoning_content":"plan "},"finish_reason":null}],"usage":null}
                """,
                """
                {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-7","type":"function","function":{"name":"move_","arguments":"{\"x\":"}}]},"finish_reason":null}],"usage":null}
                """,
                """
                {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"to","arguments":"3}"}}]},"finish_reason":"tool_calls"}],"usage":null}
                """,
                """
                {"choices":[],"usage":{"prompt_tokens":41,"completion_tokens":9,"total_tokens":50}}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new SystemRuntimeDelay(),
            new StableIds());

        var result = await runner.RunAsync(
            "run-1",
            "attempt-1",
            "turn-1",
            new[] { Message(NormalizedRoles.User, NormalizedContentPart.FromText("go")) },
            new[] { Tool("move_to", """{"type":"object"}""") },
            new AttemptFence(),
            onCurrentEvent: null,
            default);

        Assert.Equal("plan ", result.ReasoningContent);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call-7", call.ToolCallId);
        Assert.Equal("move_to", call.Name);
        Assert.Equal(3, call.Arguments.GetProperty("x").GetInt32());
        Assert.Equal(41, result.Usage.InputTokens);
        Assert.Equal(9, result.Usage.OutputTokens);
        Assert.Null(result.Usage.CacheReadTokens);
        Assert.Null(result.Usage.CacheMissTokens);
        Assert.Null(result.Usage.CacheWriteTokens);
        Assert.Equal(50, result.Usage.ProviderTotalTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostUnavailable,
            result.Usage.Availability);
        Assert.Equal("0", result.Usage.CostUsd);
        Assert.Equal("tool_calls", result.FinishReason);
    }

    [Fact]
    public async Task ComputesConfiguredCacheAwareUsageCost()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                """
                {"choices":[],"usage":{"prompt_tokens":1000,"prompt_cache_hit_tokens":750,"prompt_cache_miss_tokens":250,"completion_tokens":100}}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var events = await CollectAsync(provider.StreamAsync(Request(), default));

        var usage = Assert.Single(
            events,
            item => item.Kind == ModelStreamEventKinds.Usage).Usage!;

        Assert.Equal(750, usage.CacheReadTokens);
        Assert.Equal(250, usage.CacheMissTokens);
        Assert.Null(usage.CacheWriteTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostAvailable,
            usage.Availability);
        Assert.Equal("0.00019846875", usage.CostUsd);
    }

    [Fact]
    public async Task PreservesExplicitMissWriteReasoningAndProviderTotal()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                """
                {"choices":[],"usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":0,"prompt_cache_miss_tokens":100,"prompt_cache_write_tokens":0,"completion_tokens":20,"completion_tokens_details":{"reasoning_tokens":7},"total_tokens":120}}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var events = await CollectAsync(provider.StreamAsync(Request(), default));

        var usage = Assert.Single(
            events,
            item => item.Kind == ModelStreamEventKinds.Usage).Usage!;

        Assert.Equal(0, usage.CacheReadTokens);
        Assert.Equal(100, usage.CacheMissTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
        Assert.Equal(7, usage.ReasoningTokens);
        Assert.Equal(120, usage.ProviderTotalTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostAvailable,
            usage.Availability);
        Assert.Equal("0.0000609", usage.CostUsd);
    }

    [Fact]
    public async Task PartialCacheBreakdownStaysPartialAndUnpriced()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                """
                {"choices":[],"usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":25,"completion_tokens":20,"total_tokens":120}}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var events = await CollectAsync(provider.StreamAsync(Request(), default));

        var usage = Assert.Single(
            events,
            item => item.Kind == ModelStreamEventKinds.Usage).Usage!;

        Assert.Equal(25, usage.CacheReadTokens);
        Assert.Null(usage.CacheMissTokens);
        Assert.Equal(
            UsageAvailabilityStates.CostUnavailable,
            usage.Availability);
        Assert.Equal("0", usage.CostUsd);
    }

    [Fact]
    public async Task FailsClosedWhenDoneSentinelIsMissing()
    {
        var transport = new FakeTransport(
            SseWithoutDone(
                """
                {"choices":[{"index":0,"delta":{"content":"partial"},"finish_reason":"stop"}]}
                """));
        var provider = CreateProvider(transport);

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () =>
                await CollectAsync(provider.StreamAsync(Request(), default)));

        Assert.Equal("provider_sse_done_missing", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task RejectsAnOversizedLineBeforeReadingItIntoOneString()
    {
        var transport = new FakeTransport("data: " + new string('x', 128));
        var provider = CreateProvider(transport, maxSseLineCharacters: 32);

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () =>
                await CollectAsync(provider.StreamAsync(Request(), default)));

        Assert.Equal("provider_sse_line_too_large", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task HttpFailureIsClassifiedWithoutEchoingBodyOrCredential()
    {
        var transport = new FakeTransport(
            "server says test-secret",
            statusCode: 429,
            retryAfter: "2");
        var provider = CreateProvider(transport);

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () =>
                await CollectAsync(provider.StreamAsync(Request(), default)));

        Assert.Equal("provider_throttled", error.Code);
        Assert.Equal("rate_limit", error.Category);
        Assert.True(error.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(2), error.RetryAfter);
        Assert.DoesNotContain("test-secret", error.Message);
    }

    [Theory]
    [InlineData(401, "provider_auth_failed")]
    [InlineData(402, "provider_balance_exhausted")]
    [InlineData(403, "provider_auth_failed")]
    [InlineData(404, "provider_route_unavailable")]
    [InlineData(405, "provider_route_unavailable")]
    [InlineData(410, "provider_route_unavailable")]
    public async Task RouteScopedHttpRejectionAllowsFailover(
        int statusCode,
        string expectedCode)
    {
        var provider = CreateProvider(
            new FakeTransport(string.Empty, statusCode: statusCode));

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () =>
                await CollectAsync(provider.StreamAsync(Request(), default)));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(
            ProviderFailureDisposition.Failover,
            error.Disposition);
        Assert.False(error.Retryable);
        Assert.True(error.FallbackEligible);
        Assert.True(error.UsageKnownToBeZero);
    }

    [Fact]
    public async Task InvalidRequestHttpRejectionAbortsTheRun()
    {
        var provider = CreateProvider(
            new FakeTransport(string.Empty, statusCode: 400));

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () =>
                await CollectAsync(provider.StreamAsync(Request(), default)));

        Assert.Equal("provider_invalid_request", error.Code);
        Assert.Equal(
            ProviderFailureDisposition.AbortRun,
            error.Disposition);
        Assert.False(error.FallbackEligible);
        Assert.True(error.UsageKnownToBeZero);
    }

    [Fact]
    public async Task ExplicitHttpRejectionDoesNotReadErrorBody()
    {
        var transport = new ThrowingErrorBodyTransport(429);
        var provider = CreateProvider(transport);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new SystemRuntimeDelay(),
            new StableIds());
        var uncertain = new List<ProviderUsageUncertainNotice>();

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "attempt-1",
                    "turn-1",
                    new[]
                    {
                        Message(
                            NormalizedRoles.User,
                            NormalizedContentPart.FromText("hello"))
                    },
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onUsageUncertain: notice =>
                    {
                        uncertain.Add(notice);
                        return default;
                    })
                .AsTask());

        Assert.Equal("provider_throttled", error.Code);
        Assert.Equal("rate_limit", error.Category);
        Assert.True(error.UsageKnownToBeZero);
        Assert.False(transport.Body.ReadAttempted);
        Assert.Empty(uncertain);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    public async Task AmbiguousHttpFailureFailsClosedWithoutReadingErrorBody(
        int statusCode)
    {
        var transport = new ThrowingErrorBodyTransport(statusCode);
        var provider = CreateProvider(transport);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 2 },
            new SystemRuntimeDelay(),
            new StableIds());
        var uncertain = new List<ProviderUsageUncertainNotice>();

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "attempt-1",
                    "turn-1",
                    new[]
                    {
                        Message(
                            NormalizedRoles.User,
                            NormalizedContentPart.FromText("hello"))
                    },
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onUsageUncertain: notice =>
                    {
                        uncertain.Add(notice);
                        return default;
                    })
                .AsTask());

        Assert.Equal("provider_usage_unknown", error.Code);
        Assert.Equal("provider", error.Category);
        Assert.False(error.UsageKnownToBeZero);
        Assert.False(transport.Body.ReadAttempted);
        Assert.Single(uncertain);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task CredentialSourceSelfCancellationBeforeDispatchIsKnownZero()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ProviderId = "self-cancelling-credential",
                BaseUri = new Uri("https://api.deepseek.com"),
                Model = "test-model"
            },
            new SelfCancellingCredentialSource(),
            transport);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new SystemRuntimeDelay(),
            new StableIds());
        var uncertain = new List<ProviderUsageUncertainNotice>();

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "attempt-1",
                    "turn-1",
                    new[]
                    {
                        Message(
                            NormalizedRoles.User,
                            NormalizedContentPart.FromText("hello"))
                    },
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onUsageUncertain: notice =>
                    {
                        uncertain.Add(notice);
                        return default;
                    })
                .AsTask());

        Assert.Equal("provider_auth_missing", error.Code);
        Assert.Equal("auth", error.Category);
        Assert.Equal(
            ProviderFailureDisposition.Failover,
            error.Disposition);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Null(transport.LastRequest);
        Assert.Empty(uncertain);
    }

    [Fact]
    public async Task CredentialLookupPropagatesCallerCancellationBeforeDispatch()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = CreateProvider(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
                await CollectAsync(
                    provider.StreamAsync(Request(), cancellation.Token)));

        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task CredentialFailureDoesNotRetainSecretTaintedException()
    {
        const string secret = "credential-exception-secret-canary";
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions(),
            new ThrowingSecretCredentialSource(secret),
            new FakeTransport(string.Empty));

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await CollectAsync(
                provider.StreamAsync(Request(), CancellationToken.None)));

        Assert.Equal("provider_auth_missing", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(
            secret,
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailureDoesNotRetainSecretTaintedException()
    {
        const string secret = "transport-exception-secret-canary";
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions(),
            new StaticBearerTokenSource(secret),
            new SecretEchoingFailureTransport());

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await CollectAsync(
                provider.StreamAsync(Request(), CancellationToken.None)));

        Assert.Equal("provider_connect_failed", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(
            secret,
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectProviderRejectsOversizedInputBeforeTransport()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = CreateProvider(transport);
        var request = Request(
            Message(
                NormalizedRoles.User,
                NormalizedContentPart.FromText(
                    new string('x', 8 * 1_048_576))));

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await CollectAsync(
                provider.StreamAsync(request, CancellationToken.None)));

        Assert.Equal("provider_request_input_limit", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task RequestSnapshotSurvivesMutationDuringCredentialLookup()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var credentials =
            new BlockingCredentialSource("snapshot-secret");
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions(),
            credentials,
            transport);
        var message = Message(
            NormalizedRoles.User,
            NormalizedContentPart.FromText("original-message"));
        message.MessageId = "original-message-id";
        var tool = Tool(
            "original_tool",
            """{"type":"object","properties":{"original":{"type":"string"}}}""");
        var request = Request(message);
        request.RunId = "original-run";
        request.RunAttemptId = "original-run-attempt";
        request.TurnId = "original-turn";
        request.ProviderAttemptId = "original-provider-attempt";
        request.StreamAttemptId = "original-stream-attempt";
        request.Tools = new[] { tool };
        request.MaxOutputTokens = 19;

        var collection = CollectAsync(
            provider.StreamAsync(request, CancellationToken.None));
        await credentials.Started.WaitAsync(TimeSpan.FromSeconds(5));

        request.RunId = string.Empty;
        request.RunAttemptId = "mutated-run-attempt";
        request.TurnId = "mutated-turn";
        request.ProviderAttemptId = "mutated-provider-attempt";
        request.StreamAttemptId = string.Empty;
        request.MaxOutputTokens = 1;
        request.Messages = Array.Empty<NormalizedMessage>();
        request.Tools = Array.Empty<ToolDescriptor>();
        message.MessageId = "mutated-message-id";
        message.Role = "mutated-role";
        message.Parts[0].Text = "mutated-message";
        message.Parts.Add(
            NormalizedContentPart.FromText("mutated-extra-part"));
        tool.Name = "mutated_tool";
        tool.Version = "mutated-version";
        tool.Description = "mutated-description";
        tool.ParametersSchema =
            Json("""{"type":"object","properties":{"mutated":true}}""");
        credentials.Release();

        var events = await collection;

        Assert.All(
            events,
            item => Assert.Equal(
                "original-stream-attempt",
                item.StreamAttemptId));
        using var body = JsonDocument.Parse(transport.LastRequest!.Body);
        Assert.Equal(
            19,
            body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(
            "original-message",
            body.RootElement.GetProperty("messages")[0]
                .GetProperty("content")
                .GetString());
        Assert.Equal(
            "original_tool",
            body.RootElement.GetProperty("tools")[0]
                .GetProperty("function")
                .GetProperty("name")
                .GetString());
        Assert.True(
            body.RootElement.GetProperty("tools")[0]
                .GetProperty("function")
                .GetProperty("parameters")
                .GetProperty("properties")
                .TryGetProperty("original", out _));
    }

    [Fact]
    public async Task PreparedWireAndEvidenceIgnoreMutationDuringCredentialWait()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                """
                {"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """,
                "[DONE]"));
        var credentials =
            new BlockingCredentialSource("snapshot-secret");
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions(),
            credentials,
            transport);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 1 },
            new SystemRuntimeDelay(),
            new StableIds());
        var message = Message(
            NormalizedRoles.User,
            NormalizedContentPart.FromText("immutable-wire"));
        ProviderWireRequestEvidence? evidence = null;

        var run = runner.RunAsync(
                "run-1",
                "attempt-1",
                "turn-1",
                new[] { message },
                Array.Empty<ToolDescriptor>(),
                new AttemptFence(),
                null,
                CancellationToken.None,
                onDispatch: notice =>
                {
                    evidence = notice.WireRequestEvidence;
                    return default;
                })
            .AsTask();
        await credentials.Started.WaitAsync(TimeSpan.FromSeconds(5));

        message.Parts[0].Text = "mutated-after-dispatch";
        message.Parts.Add(
            NormalizedContentPart.FromText("extra-mutation"));
        credentials.Release();
        _ = await run;

        Assert.NotNull(evidence);
        Assert.Equal(
            LowerSha256(transport.LastRequest!.Body),
            evidence!.PayloadSha256);
        using var body = JsonDocument.Parse(transport.LastRequest.Body);
        Assert.Equal(
            "immutable-wire",
            body.RootElement.GetProperty("messages")[0]
                .GetProperty("content")
                .GetString());
    }

    [Fact]
    public async Task RequestSnapshotUsesIndexedListsWithoutEnumeration()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var request = Request();
        request.Messages =
            new IndexedOnlyReadOnlyList<NormalizedMessage>(
                request.Messages.ToArray());
        request.Tools =
            new IndexedOnlyReadOnlyList<ToolDescriptor>(
                new[] { Tool("inspect", """{"type":"object"}""") });

        _ = await CollectAsync(
            provider.StreamAsync(request, CancellationToken.None));

        Assert.NotNull(transport.LastRequest);
    }

    [Fact]
    public async Task RequestSnapshotFailsClosedOnCountIndexMismatch()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = CreateProvider(transport);
        var request = Request();
        request.Messages =
            new CountIndexMismatchReadOnlyList<NormalizedMessage>(
                request.Messages[0]);

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await CollectAsync(
                provider.StreamAsync(request, CancellationToken.None)));

        Assert.Equal("provider_request_input_limit", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task RequestSnapshotFailsClosedOnToolCountIndexMismatch()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = CreateProvider(transport);
        var request = Request();
        request.Tools =
            new CountIndexMismatchReadOnlyList<ToolDescriptor>(
                Tool("inspect", """{"type":"object"}"""));

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await CollectAsync(
                provider.StreamAsync(request, CancellationToken.None)));

        Assert.Equal("provider_request_input_limit", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task EncodedRequestBodyAcceptsTheExactHardByteLimit()
    {
        const int maximumBodyBytes = 8 * 1_048_576;
        const int emptyUserBodyBytes = 198;
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = CreateProvider(transport);
        var request = Request(
            Message(
                NormalizedRoles.User,
                NormalizedContentPart.FromText(
                    new string(
                        'x',
                        maximumBodyBytes - emptyUserBodyBytes))));

        _ = await CollectAsync(
            provider.StreamAsync(request, CancellationToken.None));

        Assert.NotNull(transport.LastRequest);
        Assert.Equal(maximumBodyBytes, transport.LastRequest.Body.Length);
    }

    [Fact]
    public async Task EncodedRequestBodyRejectsOneByteOverTheHardLimit()
    {
        const int maximumBodyBytes = 8 * 1_048_576;
        const int emptyUserBodyBytes = 198;
        var transport = new FakeTransport(string.Empty);
        var provider = CreateProvider(transport);
        var request = Request(
            Message(
                NormalizedRoles.User,
                NormalizedContentPart.FromText(
                    new string(
                        'x',
                        maximumBodyBytes - emptyUserBodyBytes + 1))));

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await CollectAsync(
                provider.StreamAsync(request, CancellationToken.None)));

        Assert.Equal("provider_request_body_limit", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task TransportCancellationWithoutRunCancellationFailsProvider()
    {
        var transport = new SelfCancellingTransport();
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ProviderId = "self-cancelling-http",
                BaseUri = new Uri("https://api.deepseek.com"),
                Model = "test-model"
            },
            new StaticBearerTokenSource("test-secret"),
            transport);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 2 },
            new SystemRuntimeDelay(),
            new StableIds());
        var uncertain = new List<ProviderUsageUncertainNotice>();

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "attempt-1",
                    "turn-1",
                    new[]
                    {
                        Message(
                            NormalizedRoles.User,
                            NormalizedContentPart.FromText("hello"))
                    },
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onUsageUncertain: notice =>
                    {
                        uncertain.Add(notice);
                        return default;
                    })
                .AsTask());

        Assert.Equal("provider_usage_unknown", error.Code);
        Assert.False(error.Retryable);
        Assert.False(error.UsageKnownToBeZero);
        Assert.Equal(1, transport.CallCount);
        Assert.Single(uncertain);
    }

    [Fact]
    public async Task EncodingFailureBeforeTransportIsKnownZeroUsage()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = CreateProvider(transport);
        var runner = new ProviderAttemptRunner(
            new[] { provider },
            new ProviderRetryPolicy { MaxAttemptsPerProvider = 2 },
            new SystemRuntimeDelay(),
            new StableIds());
        var uncertain = new List<ProviderUsageUncertainNotice>();
        var invalidMessage = Message(
            "unsupported-role",
            NormalizedContentPart.FromText("never sent"));

        var error = await Assert.ThrowsAsync<ProviderException>(
            () => runner.RunAsync(
                    "run-1",
                    "attempt-1",
                    "turn-1",
                    new[] { invalidMessage },
                    Array.Empty<ToolDescriptor>(),
                    new AttemptFence(),
                    null,
                    CancellationToken.None,
                    onUsageUncertain: notice =>
                    {
                        uncertain.Add(notice);
                        return default;
                    })
                .AsTask());

        Assert.Equal("provider_role_unsupported", error.Code);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Null(transport.LastRequest);
        Assert.Empty(uncertain);
    }

    [Fact]
    public async Task ConstructorSnapshotsCallerOwnedOptions()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                """
                {"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """,
                "[DONE]"));
        var options = new OpenAiCompatibleProviderOptions
        {
            ProviderId = "stable-provider",
            BaseUri = new Uri("https://api.deepseek.com"),
            ChatCompletionsPath = "/chat/completions",
            Model = "stable-model",
            MaxOutputTokens = 64,
            MaxContextTokens = 4096,
            InputCacheHitUsdPerMillionTokens = "1",
            InputCacheMissUsdPerMillionTokens = "1",
            OutputUsdPerMillionTokens = "1"
        };
        var provider = new OpenAiCompatibleStreamingProvider(
            options,
            new StaticBearerTokenSource("test-secret"),
            transport);

        options.ProviderId = "mutated-provider";
        options.BaseUri = new Uri("https://mutated.invalid");
        options.ChatCompletionsPath = "/mutated";
        options.Model = "mutated-model";
        options.MaxOutputTokens = 1;
        options.MaxContextTokens = 1;
        options.IncludeUsage = false;
        options.InputCacheHitUsdPerMillionTokens = "999";
        options.InputCacheMissUsdPerMillionTokens = "999";
        options.OutputUsdPerMillionTokens = "999";

        var events = await CollectAsync(
            provider.StreamAsync(Request(), default));

        Assert.Equal("stable-provider", provider.ProviderId);
        Assert.Equal(4096, provider.Capabilities.MaxContextTokens);
        Assert.Equal(
            "https://api.deepseek.com/chat/completions",
            transport.LastRequest!.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(transport.LastRequest.Body);
        Assert.Equal(
            "stable-model",
            body.RootElement.GetProperty("model").GetString());
        Assert.True(
            body.RootElement.GetProperty("stream_options")
                .GetProperty("include_usage")
                .GetBoolean());
        var usage = Assert.Single(
            events,
            item => item.Kind == ModelStreamEventKinds.Usage).Usage!;
        Assert.Equal("0.000002", usage.CostUsd);
    }

    [Fact]
    public void RejectsInsecureRemoteEndpointAndInvalidToken68()
    {
        Assert.Throws<ArgumentException>(
            () => new OpenAiCompatibleStreamingProvider(
                new OpenAiCompatibleProviderOptions
                {
                    BaseUri = new Uri("http://api.example.com")
                },
                new StaticBearerTokenSource("token"),
                new FakeTransport(string.Empty)));
        Assert.Throws<ArgumentException>(
            () => new StaticBearerTokenSource("token\r\nX-Evil: 1"));
        Assert.Throws<ArgumentException>(
            () => new StaticBearerTokenSource("token\0suffix"));
        Assert.Throws<ArgumentException>(
            () => new StaticBearerTokenSource("token suffix"));
        Assert.Throws<ArgumentException>(
            () => new StaticBearerTokenSource("token=more"));
        Assert.Throws<ArgumentException>(
            () => new StaticBearerTokenSource("==="));
    }

    [Fact]
    public async Task DynamicInvalidBearerTokenFailsBeforeTransportWithKnownZeroUsage()
    {
        var transport = new FakeTransport(string.Empty);
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions(),
            new FixedCredentialSource("token\0suffix"),
            transport);

        var error = await Assert.ThrowsAsync<ProviderException>(
            async () => await CollectAsync(
                provider.StreamAsync(
                    Request(),
                    CancellationToken.None)));

        Assert.Equal("provider_auth_missing", error.Code);
        Assert.Equal(
            ProviderFailureDisposition.Failover,
            error.Disposition);
        Assert.True(error.UsageKnownToBeZero);
        Assert.Null(transport.LastRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("chat/completions")]
    [InlineData("//attacker.invalid/chat/completions")]
    [InlineData("///attacker.invalid/chat/completions")]
    [InlineData("/ //attacker.invalid/chat/completions")]
    [InlineData("/https://attacker.invalid")]
    [InlineData("/https:attacker.invalid")]
    [InlineData("/file:payload")]
    [InlineData("/custom+v1:payload")]
    [InlineData("/C:/payload")]
    [InlineData("/C|/payload")]
    [InlineData("/\\attacker.invalid")]
    [InlineData("/chat\\completions")]
    [InlineData("/chat/completions#fragment")]
    [InlineData("/chat/completions\0suffix")]
    [InlineData("/chat/completions\tsuffix")]
    [InlineData("/chat/completions\r\nX-Evil: 1")]
    [InlineData("/chat/completions\u007fsuffix")]
    [InlineData("/chat/completions with-space")]
    [InlineData("/../admin")]
    [InlineData("/safe/../../admin")]
    [InlineData("/%2e%2e/admin")]
    [InlineData("/.%2E/admin")]
    [InlineData("/%252e%252e/admin")]
    [InlineData("/safe%2f..%2fadmin")]
    [InlineData("/%2F%2Fattacker.invalid")]
    [InlineData("/%252F%252Fattacker.invalid")]
    [InlineData("/chat/%2")]
    [InlineData("/chat/%GG")]
    public void RejectsUnsafeChatCompletionsPath(string? path)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new OpenAiCompatibleStreamingProvider(
                new OpenAiCompatibleProviderOptions
                {
                    ChatCompletionsPath = path!
                },
                new StaticBearerTokenSource("token"),
                new FakeTransport(string.Empty)));

        Assert.Equal(
            nameof(OpenAiCompatibleProviderOptions.ChatCompletionsPath),
            error.ParamName);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/v1/a:b")]
    [InlineData("/chat?url=https://example.com")]
    public void AcceptsSafeRootedRelativeChatCompletionsPath(string path)
    {
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ChatCompletionsPath = path
            },
            new StaticBearerTokenSource("token"),
            new FakeTransport(string.Empty));

        Assert.Equal("openai-compatible", provider.ProviderId);
    }

    [Fact]
    public async Task PreservesQueryOnRootedRelativeChatCompletionsPath()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                BaseUri = new Uri("https://api.openai.com/v1"),
                ChatCompletionsPath =
                    "/chat/completions?api-version=2026-07-01"
            },
            new StaticBearerTokenSource("token"),
            transport);

        _ = await CollectAsync(provider.StreamAsync(Request(), default));

        Assert.Equal(
            "https://api.openai.com/v1/chat/completions?api-version=2026-07-01",
            transport.LastRequest!.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("/https%3A%2F%2Fattacker.invalid")]
    public async Task EncodedPathContentsCannotChangeOrigin(string path)
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ChatCompletionsPath = path
            },
            new StaticBearerTokenSource("token"),
            transport);

        _ = await CollectAsync(provider.StreamAsync(Request(), default));

        var endpoint = transport.LastRequest!.Uri;
        Assert.Equal(Uri.UriSchemeHttps, endpoint.Scheme);
        Assert.Equal("api.deepseek.com", endpoint.IdnHost);
        Assert.Equal(443, endpoint.Port);
        Assert.Empty(endpoint.UserInfo);
        Assert.Empty(endpoint.Fragment);
    }

    [Fact]
    public async Task EndpointCannotEscapeConfiguredBasePath()
    {
        var transport = new FakeTransport(
            Sse(
                """
                {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                "[DONE]"));
        var provider = new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                BaseUri = new Uri(
                    "https://gateway.example.com/tenant/deployment"),
                ChatCompletionsPath = "/chat/completions"
            },
            new StaticBearerTokenSource("token"),
            transport);

        _ = await CollectAsync(provider.StreamAsync(Request(), default));

        Assert.Equal(
            "/tenant/deployment/chat/completions",
            transport.LastRequest!.Uri.AbsolutePath);
    }

    [Fact]
    public void SecureTransportCannotBeConstructedWithAnOpaqueHttpClient()
    {
        Assert.DoesNotContain(
            typeof(HttpClientStreamingTransport).GetConstructors(),
            constructor => constructor
                .GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(HttpClient)));
    }

    [Fact]
    public async Task SecureTransportRejectsPlaintextRemoteEndpointBeforeSend()
    {
        using var transport = new HttpClientStreamingTransport();

        await Assert.ThrowsAsync<ArgumentException>(
            () => transport.SendAsync(
                    new StreamingHttpRequest
                    {
                        Uri = new Uri("http://example.invalid/chat"),
                        BearerToken = "test-secret",
                        Body = Encoding.UTF8.GetBytes("{}")
                    },
                    CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public async Task SecureTransportDoesNotForwardRedirectedRequest()
    {
        var redirectListener = new TcpListener(IPAddress.Loopback, 0);
        var targetListener = new TcpListener(IPAddress.Loopback, 0);
        redirectListener.Start();
        targetListener.Start();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var targetAccepted = 0;
        var targetTask = AcceptAndRespondAsync(
            targetListener,
            """
            HTTP/1.1 200 OK
            Content-Type: text/event-stream
            Content-Length: 16
            Connection: close

            data: [DONE]

            """,
            () => Interlocked.Exchange(ref targetAccepted, 1),
            timeout.Token);
        var targetEndpoint =
            (IPEndPoint)targetListener.LocalEndpoint;
        var redirectTask = AcceptAndRespondAsync(
            redirectListener,
            "HTTP/1.1 307 Temporary Redirect\r\n"
            + "Location: http://127.0.0.1:"
            + targetEndpoint.Port
            + "/capture\r\n"
            + "Content-Length: 0\r\n"
            + "Connection: close\r\n\r\n",
            static () => { },
            timeout.Token);
        var redirectEndpoint =
            (IPEndPoint)redirectListener.LocalEndpoint;

        try
        {
            using var transport = new HttpClientStreamingTransport();
            using var response = await transport.SendAsync(
                new StreamingHttpRequest
                {
                    Uri = new Uri(
                        "http://127.0.0.1:"
                        + redirectEndpoint.Port
                        + "/chat/completions"),
                    BearerToken = "redirect-secret",
                    Body = Encoding.UTF8.GetBytes(
                        """{"private_game_context":true}""")
                },
                timeout.Token);

            Assert.Equal(307, response.StatusCode);
            await redirectTask.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.Equal(0, Volatile.Read(ref targetAccepted));
        }
        finally
        {
            timeout.Cancel();
            redirectListener.Stop();
            targetListener.Stop();
            try
            {
                await targetTask;
            }
            catch (Exception exception)
                when (exception is OperationCanceledException
                      or SocketException)
            {
            }
        }
    }

    private static OpenAiCompatibleStreamingProvider CreateProvider(
        IStreamingHttpTransport transport,
        int maxSseLineCharacters = 512 * 1024)
    {
        return new OpenAiCompatibleStreamingProvider(
            new OpenAiCompatibleProviderOptions
            {
                ProviderId = "deepseek",
                BaseUri = new Uri("https://api.deepseek.com"),
                Model = "deepseek-v4-pro",
                MaxSseLineCharacters = maxSseLineCharacters
            },
            new StaticBearerTokenSource("test-secret"),
            transport);
    }

    private static StreamingModelRequest Request(
        params NormalizedMessage[] messages)
    {
        return new StreamingModelRequest
        {
            RunId = "run-1",
            RunAttemptId = "attempt-1",
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

    private static ToolDescriptor Tool(string name, string schema)
    {
        return new ToolDescriptor
        {
            Name = name,
            Version = "1",
            Description = name,
            ParametersSchema = Json(schema)
        };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string LowerSha256(byte[] value)
    {
        return Convert.ToHexString(SHA256.HashData(value))
            .ToLowerInvariant();
    }

    private static string Sse(params string[] payloads)
    {
        return string.Join(
            string.Empty,
            payloads.Select(payload => "data: " + payload + "\n\n"));
    }

    private static string SseWithoutDone(params string[] payloads)
    {
        return string.Join(
            string.Empty,
            payloads.Select(payload => "data: " + payload + "\n\n"));
    }

    private static async Task AcceptAndRespondAsync(
        TcpListener listener,
        string response,
        Action accepted,
        CancellationToken cancellationToken)
    {
        using var client = await listener
            .AcceptTcpClientAsync(cancellationToken);
        accepted();
        await ReadRequestHeadersAsync(
            client.GetStream(),
            cancellationToken);
        var bytes = Encoding.ASCII.GetBytes(
            response.ReplaceLineEndings("\r\n"));
        await client.GetStream().WriteAsync(bytes, cancellationToken);
    }

    private static async Task ReadRequestHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var received = new List<byte>();
        while (received.Count < 32 * 1024)
        {
            var count = await stream.ReadAsync(
                buffer,
                cancellationToken);
            if (count == 0)
            {
                return;
            }

            received.AddRange(buffer.AsSpan(0, count).ToArray());
            if (Encoding.ASCII
                .GetString(received.ToArray())
                .Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidDataException(
            "The local HTTP test request headers exceeded the limit.");
    }

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelStreamEvent> source)
    {
        var results = new List<ModelStreamEvent>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }

    private sealed class FakeTransport : IStreamingHttpTransport
    {
        private readonly string _response;
        private readonly int _statusCode;
        private readonly string? _retryAfter;

        public FakeTransport(
            string response,
            int statusCode = 200,
            string? retryAfter = null)
        {
            _response = response;
            _statusCode = statusCode;
            _retryAfter = retryAfter;
        }

        public StreamingHttpRequest? LastRequest { get; private set; }

        public ValueTask<IStreamingHttpResponse> SendAsync(
            StreamingHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = new StreamingHttpRequest
            {
                Uri = new Uri(request.Uri.AbsoluteUri, UriKind.Absolute),
                BearerToken = request.BearerToken,
                Body = request.Body.ToArray(),
                ContentType = request.ContentType
            };
            return new ValueTask<IStreamingHttpResponse>(
                new FakeResponse(_response, _statusCode, _retryAfter));
        }
    }

    private sealed class FakeResponse : IStreamingHttpResponse
    {
        private readonly string? _retryAfter;

        public FakeResponse(
            string response,
            int statusCode,
            string? retryAfter)
        {
            StatusCode = statusCode;
            _retryAfter = retryAfter;
            Content = new MemoryStream(Encoding.UTF8.GetBytes(response));
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

    private sealed class RetainingBodyTransport : IStreamingHttpTransport
    {
        private readonly string _response;

        public RetainingBodyTransport(string response)
        {
            _response = response;
        }

        public byte[]? ObservedBody { get; private set; }

        public ValueTask<IStreamingHttpResponse> SendAsync(
            StreamingHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedBody = request.Body;
            return new ValueTask<IStreamingHttpResponse>(
                new FakeResponse(_response, 200, retryAfter: null));
        }
    }

    private sealed class ThrowingErrorBodyTransport : IStreamingHttpTransport
    {
        private readonly int _statusCode;
        private int _callCount;

        public ThrowingErrorBodyTransport(int statusCode)
        {
            _statusCode = statusCode;
        }

        public ThrowingReadStream Body { get; } = new();

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<IStreamingHttpResponse> SendAsync(
            StreamingHttpRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return new ValueTask<IStreamingHttpResponse>(
                new ThrowingErrorResponse(_statusCode, Body));
        }
    }

    private sealed class ThrowingErrorResponse : IStreamingHttpResponse
    {
        public ThrowingErrorResponse(int statusCode, Stream content)
        {
            StatusCode = statusCode;
            Content = content;
        }

        public int StatusCode { get; }

        public Stream Content { get; }

        public string? GetHeader(string name)
        {
            _ = name;
            return null;
        }

        public void Dispose()
        {
            Content.Dispose();
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public bool ReadAttempted { get; private set; }

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
            _ = buffer;
            _ = offset;
            _ = count;
            ReadAttempted = true;
            throw new IOException("The error body stream cannot be read.");
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _ = buffer;
            _ = offset;
            _ = count;
            _ = cancellationToken;
            ReadAttempted = true;
            return Task.FromException<int>(
                new IOException("The error body stream cannot be read."));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _ = offset;
            _ = origin;
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            _ = value;
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _ = buffer;
            _ = offset;
            _ = count;
            throw new NotSupportedException();
        }
    }

    private sealed class FixedProviderPromptEstimator :
        IProviderPromptTokenEstimator
    {
        private readonly int _tokens;

        public FixedProviderPromptEstimator(
            string estimatorId,
            string version,
            int tokens)
        {
            EstimatorId = estimatorId;
            Version = version;
            _tokens = tokens;
        }

        public string EstimatorId { get; }

        public string Version { get; }

        public int EstimatePromptTokens(
            IReadOnlyList<NormalizedMessage> messages,
            IReadOnlyList<ToolDescriptor> tools)
        {
            _ = messages ?? throw new ArgumentNullException(nameof(messages));
            _ = tools ?? throw new ArgumentNullException(nameof(tools));
            return _tokens;
        }
    }

    private sealed class SelfCancellingCredentialSource :
        IProviderCredentialSource
    {
        public ValueTask<string> GetBearerTokenAsync(
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromException<string>(
                new TaskCanceledException(
                    "The credential source cancelled itself."));
        }
    }

    private sealed class ThrowingSecretCredentialSource :
        IProviderCredentialSource
    {
        private readonly string _secret;

        public ThrowingSecretCredentialSource(string secret)
        {
            _secret = secret;
        }

        public ValueTask<string> GetBearerTokenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exception = new InvalidOperationException(
                "credential source exposed " + _secret);
            exception.Data["credential"] = _secret;
            return ValueTask.FromException<string>(exception);
        }
    }

    private sealed class BlockingCredentialSource :
        IProviderCredentialSource
    {
        private readonly string _token;
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingCredentialSource(string token)
        {
            _token = token;
        }

        public Task Started => _started.Task;

        public void Release()
        {
            _release.TrySetResult(true);
        }

        public async ValueTask<string> GetBearerTokenAsync(
            CancellationToken cancellationToken)
        {
            _started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return _token;
        }
    }

    private sealed class FixedCredentialSource : IProviderCredentialSource
    {
        private readonly string _token;

        public FixedCredentialSource(string token)
        {
            _token = token;
        }

        public ValueTask<string> GetBearerTokenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<string>(_token);
        }
    }

    private sealed class SecretEchoingFailureTransport :
        IStreamingHttpTransport
    {
        public ValueTask<IStreamingHttpResponse> SendAsync(
            StreamingHttpRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exception = new InvalidOperationException(
                "transport exposed " + request.BearerToken);
            exception.Data["authorization"] = request.BearerToken;
            return ValueTask.FromException<IStreamingHttpResponse>(
                exception);
        }
    }

    private sealed class IndexedOnlyReadOnlyList<T> :
        IReadOnlyList<T>
    {
        private readonly T[] _items;

        public IndexedOnlyReadOnlyList(T[] items)
        {
            _items = items;
        }

        public int Count => _items.Length;

        public T this[int index] => _items[index];

        public IEnumerator<T> GetEnumerator()
        {
            throw new InvalidOperationException(
                "The caller-owned list must not be enumerated.");
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class CountIndexMismatchReadOnlyList<T> :
        IReadOnlyList<T>
    {
        private readonly T _onlyItem;

        public CountIndexMismatchReadOnlyList(T onlyItem)
        {
            _onlyItem = onlyItem;
        }

        public int Count => 2;

        public T this[int index] => index == 0
            ? _onlyItem
            : throw new InvalidOperationException(
                "The declared count cannot be satisfied.");

        public IEnumerator<T> GetEnumerator()
        {
            throw new InvalidOperationException(
                "The caller-owned list must not be enumerated.");
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class SelfCancellingTransport : IStreamingHttpTransport
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<IStreamingHttpResponse> SendAsync(
            StreamingHttpRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromException<IStreamingHttpResponse>(
                new TaskCanceledException(
                    "The transport cancelled its own request."));
        }
    }

    private sealed class StableIds : IRuntimeIdGenerator
    {
        private int _value;

        public string NewId(string category)
        {
            return category + "-" + Interlocked.Increment(ref _value);
        }
    }
}

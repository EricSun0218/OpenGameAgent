using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class ProtocolContractsTests
{
    [Fact]
    public void ContentContractsPreserveProviderContinuityMetadata()
    {
        var text = new TextContent("answer", "text-signature", AgentTextPhase.FinalAnswer);
        var reasoning = new ReasoningContent("", "encrypted-thinking", redacted: true);
        var image = new BinaryContent(AgentMediaKind.Image, "aGVsbG8=", "image/png", "preview");
        var call = new ToolCallContent("call-1", "act", "{}", "thought-signature", "world");

        Assert.Equal("text-signature", text.Signature);
        Assert.Equal(AgentTextPhase.FinalAnswer, text.Phase);
        Assert.True(reasoning.Redacted);
        Assert.Equal(AgentMediaKind.Image, image.MediaKind);
        Assert.Equal("thought-signature", call.ThoughtSignature);
        Assert.Equal("world", call.Namespace);
    }

    [Fact]
    public void UsagePreservesReasoningLongCacheAndItemizedCost()
    {
        var usage = new ModelUsage(
            inputTokens: 10,
            outputTokens: 8,
            cacheReadTokens: 3,
            cacheWriteTokens: 4,
            reasoningTokens: 5,
            cacheWriteOneHourTokens: 2,
            cost: new ModelCost(input: 0.1, output: 0.2, cacheRead: 0.03, cacheWrite: 0.04));

        Assert.Equal(25, usage.TotalTokens);
        Assert.Equal(5, usage.ReasoningTokens);
        Assert.Equal(2, usage.CacheWriteOneHourTokens);
        Assert.Equal(0.37, usage.Cost.Total, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelUsage(outputTokens: 1, reasoningTokens: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelUsage(cacheWriteTokens: 1, cacheWriteOneHourTokens: 2));
    }

    [Fact]
    public void CostDistinguishesUnavailablePricingFromKnownFreePricing()
    {
        var unknown = new ModelCost();
        var free = new ModelCost(isKnown: true);

        Assert.False(unknown.IsKnown);
        Assert.Null(unknown.TotalIfKnown);
        Assert.True(free.IsKnown);
        Assert.Equal(0, free.TotalIfKnown);
    }

    [Fact]
    public async Task DeferredResponseIdentitySurvivesTheAgentLoop()
    {
        var handle = new DeferredModelHandle(
            "provider",
            "model",
            "responses",
            "response-1",
            pollAfterMilliseconds: 250,
            dataJson: "{\"cursor\":1}");
        var response = new ModelResponse(
            Array.Empty<AgentContent>(),
            ModelStopReason.Deferred,
            provider: "provider",
            api: "responses",
            responseModel: "model-2026",
            responseId: "response-1",
            rawStopReason: "background",
            diagnostics: new[] { new ModelDiagnostic("queued", "The response is queued.") },
            deferred: handle);
        var agent = new Agent(new AgentOptions(ScriptedProvider.FromResponses(response), "model"));

        var run = await agent.RunAsync(AgentMessage.User("start"), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        var assistant = Assert.Single(run.NewMessages, message => message.Role == AgentRole.Assistant);
        Assert.Equal(ModelStopReason.Deferred, assistant.StopReason);
        Assert.Equal("provider", assistant.Provider);
        Assert.Equal("responses", assistant.Api);
        Assert.Equal("model-2026", assistant.ResponseModel);
        Assert.Equal("response-1", assistant.ResponseId);
        Assert.Equal("background", assistant.RawStopReason);
        Assert.Same(handle, assistant.Deferred);
        Assert.Equal("queued", Assert.Single(assistant.Diagnostics).Code);
    }

    [Fact]
    public void ToolResultsPreserveNewlyAvailableToolNames()
    {
        var call = new ToolCallContent("call", "load", "{}");
        var result = new ToolResult(
            new AgentContent[] { new TextContent("loaded") },
            addedToolNames: new[] { "build", "inspect" });

        var message = AgentMessage.ToolResult(call, result, DateTimeOffset.UnixEpoch);

        Assert.Equal(new[] { "build", "inspect" }, message.AddedToolNames);
        Assert.Throws<ArgumentException>(() => new ToolResult(
            Array.Empty<AgentContent>(),
            addedToolNames: new[] { "duplicate", "duplicate" }));
    }

    [Fact]
    public void ModelParametersCloneProviderNeutralRequestOptions()
    {
        var parameters = new ModelParameters
        {
            ReasoningLevel = "high",
            ReasoningBudgets = new Dictionary<string, int> { ["high"] = 8192 },
            SamplingParametersJson = "{\"top_p\":0.9}",
            MetadataJson = "{\"user_id\":\"player\"}",
            Transport = ModelTransport.WebSocket,
            CacheRetention = ModelCacheRetention.Long,
            WebSocketConnectTimeoutMilliseconds = 5000,
            Deferred = true,
            DeferredWindow = ModelDeferredWindow.OneHour,
        };

        var clone = parameters.Clone();

        Assert.NotSame(parameters, clone);
        Assert.Equal(8192, clone.ReasoningBudgets["high"]);
        Assert.Equal("{\"top_p\":0.9}", clone.SamplingParametersJson);
        Assert.Equal(ModelTransport.WebSocket, clone.Transport);
        Assert.Equal(ModelCacheRetention.Long, clone.CacheRetention);
        Assert.True(clone.Deferred);
        Assert.Equal(ModelDeferredWindow.OneHour, clone.DeferredWindow);
    }

    [Fact]
    public void ConstrainedSamplingContractsAreExplicit()
    {
        var schema = new ToolDefinition(
            "generate",
            "Generate a value.",
            "{\"type\":\"object\"}",
            ToolConstrainedSampling.JsonSchema(ToolSchemaStrictness.Require));
        var grammar = ToolConstrainedSampling.Grammar(openAiRegex: "[a-z]+");

        Assert.Equal(ToolSchemaStrictness.Require, schema.ConstrainedSampling?.Strictness);
        Assert.Equal("[a-z]+", grammar.OpenAiRegex);
        Assert.Throws<ArgumentException>(() => ToolConstrainedSampling.Grammar());
    }

    [Fact]
    public void ProviderTranscriptRemovesForeignOpaqueStateAndRepairsOrphanedToolCalls()
    {
        var sourceCall = new ToolCallContent("foreign|id", "move", "{\"x\":1}", "opaque-thought", "private");
        var assistant = new AgentMessage(
            AgentRole.Assistant,
            new AgentContent[]
            {
                new ReasoningContent("visible-plan", "opaque-reasoning"),
                new ReasoningContent("redacted", "opaque-redacted", redacted: true),
                new TextContent("answer", "foreign-text-signature"),
                sourceCall,
            },
            DateTimeOffset.UnixEpoch,
            model: "source-model",
            stopReason: ModelStopReason.ToolUse,
            provider: "source-provider",
            api: "source-api");

        var normalized = ProviderTranscript.Normalize(
            new[] { assistant, AgentMessage.User("interrupt", DateTimeOffset.UnixEpoch.AddSeconds(1)) },
            "target-provider",
            "target-api",
            "target-model",
            (id, _, _, _) => "normalized-id");

        Assert.Equal(3, normalized.Count);
        var replayedAssistant = normalized[0];
        Assert.Equal("visible-plan", Assert.IsType<TextContent>(replayedAssistant.Content[0]).Text);
        Assert.Equal("answer", Assert.IsType<TextContent>(replayedAssistant.Content[1]).Text);
        Assert.Null(Assert.IsType<TextContent>(replayedAssistant.Content[1]).Signature);
        var replayedCall = Assert.IsType<ToolCallContent>(replayedAssistant.Content[2]);
        Assert.Equal("normalized-id", replayedCall.Id);
        Assert.Null(replayedCall.ThoughtSignature);
        Assert.Null(replayedCall.Namespace);
        Assert.True(normalized[1].IsError);
        Assert.Equal("normalized-id", normalized[1].ToolCallId);
        Assert.Equal(AgentRole.User, normalized[2].Role);
    }

    [Fact]
    public void ProviderTranscriptKeepsContinuityDataForExactSameModel()
    {
        var assistant = new AgentMessage(
            AgentRole.Assistant,
            new AgentContent[]
            {
                new ReasoningContent(string.Empty, "opaque", redacted: true),
                new TextContent("answer", "signature", AgentTextPhase.FinalAnswer),
            },
            DateTimeOffset.UnixEpoch,
            model: "model",
            stopReason: ModelStopReason.Stop,
            provider: "provider",
            api: "api");

        var normalized = ProviderTranscript.Normalize(new[] { assistant }, "provider", "api", "model");

        Assert.Same(assistant.Content[0], normalized[0].Content[0]);
        Assert.Same(assistant.Content[1], normalized[0].Content[1]);
    }

    public static IEnumerable<object[]> ProviderHandoffPairs()
    {
        var protocols = new[]
        {
            (Provider: "anthropic", Api: "anthropic-messages", Model: "claude"),
            (Provider: "amazon-bedrock", Api: "bedrock-converse-stream", Model: "claude"),
            (Provider: "google", Api: "google-generative-ai", Model: "gemini"),
            (Provider: "mistral", Api: "mistral-conversations", Model: "mistral"),
            (Provider: "openai", Api: "openai-responses", Model: "gpt"),
            (Provider: "openai-compatible", Api: "openai-completions", Model: "compatible"),
        };

        foreach (var source in protocols)
        {
            foreach (var target in protocols)
            {
                if (source != target)
                {
                    yield return new object[]
                    {
                        source.Provider,
                        source.Api,
                        source.Model,
                        target.Provider,
                        target.Api,
                        target.Model,
                    };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ProviderHandoffPairs))]
    public void ForeignProviderTranscriptsAreSafeAcrossEveryBuiltInProtocol(
        string sourceProvider,
        string sourceApi,
        string sourceModel,
        string targetProvider,
        string targetApi,
        string targetModel)
    {
        var originalCall = new ToolCallContent(
            "call|with/foreign:symbols",
            "inspect",
            "{\"path\":\"README.md\"}",
            "opaque-tool-state",
            "source-only-namespace");
        var assistant = new AgentMessage(
            AgentRole.Assistant,
            new AgentContent[]
            {
                new ReasoningContent("visible reasoning", "opaque-reasoning-state"),
                new ReasoningContent("hidden reasoning", "opaque-redacted-state", redacted: true),
                new TextContent("answer", "opaque-text-state", AgentTextPhase.FinalAnswer),
                originalCall,
            },
            DateTimeOffset.UnixEpoch,
            model: sourceModel,
            stopReason: ModelStopReason.ToolUse,
            provider: sourceProvider,
            api: sourceApi);
        var result = AgentMessage.ToolResult(
            originalCall,
            new ToolResult(new AgentContent[] { new TextContent("done") }),
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        var normalized = ProviderTranscript.Normalize(
            new AgentMessage[] { assistant, result, AgentMessage.User("continue", DateTimeOffset.UnixEpoch.AddSeconds(2)) },
            targetProvider,
            targetApi,
            targetModel,
            static (id, _, _, _) => id.Replace('|', '_').Replace('/', '_').Replace(':', '_'));

        Assert.Equal(3, normalized.Count);
        var replayedAssistant = normalized[0];
        Assert.DoesNotContain(replayedAssistant.Content, content => content is ReasoningContent);
        Assert.Collection(
            replayedAssistant.Content,
            content =>
            {
                var text = Assert.IsType<TextContent>(content);
                Assert.Equal("visible reasoning", text.Text);
                Assert.Null(text.Signature);
            },
            content =>
            {
                var text = Assert.IsType<TextContent>(content);
                Assert.Equal("answer", text.Text);
                Assert.Null(text.Signature);
                Assert.Null(text.Phase);
            },
            content =>
            {
                var call = Assert.IsType<ToolCallContent>(content);
                Assert.Equal("call_with_foreign_symbols", call.Id);
                Assert.Null(call.ThoughtSignature);
                Assert.Null(call.Namespace);
            });
        Assert.Equal("call_with_foreign_symbols", normalized[1].ToolCallId);
        Assert.Equal(AgentRole.User, normalized[2].Role);
    }
}

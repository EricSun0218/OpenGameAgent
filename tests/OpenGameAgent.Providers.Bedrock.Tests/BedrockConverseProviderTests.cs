using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using System.Net;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Providers.Bedrock.Tests;

public sealed class BedrockConverseProviderTests
{
    [Fact]
    public async Task StreamsReasoningTextToolCallsAndUsage()
    {
        var provider = new BedrockConverseProvider(new BedrockConverseProviderOptions
        {
            Transport = (_, token) => SuccessfulStream(token),
        });

        var events = await CollectAsync(provider.StreamAsync(Request("anthropic.claude-sonnet-4-5"), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        Assert.Equal(ModelStopReason.ToolUse, response.StopReason);
        Assert.Equal("tool_use", response.RawStopReason);
        var reasoning = Assert.IsType<ReasoningContent>(response.Content[0]);
        Assert.Equal("plan", reasoning.Text);
        Assert.Equal("signature", reasoning.Signature);
        Assert.Equal("hello", Assert.IsType<TextContent>(response.Content[1]).Text);
        var tool = Assert.IsType<ToolCallContent>(response.Content[2]);
        Assert.Equal("tool-1", tool.Id);
        Assert.Equal("{\"x\":1}", tool.ArgumentsJson);
        Assert.Equal(10, response.Usage.InputTokens);
        Assert.Equal(2, response.Usage.OutputTokens);
        Assert.Equal(3, response.Usage.CacheReadTokens);
        Assert.Equal(4, response.Usage.CacheWriteTokens);
    }

    [Fact]
    public async Task SerializesCacheImagesStrictToolsAndBudgetThinking()
    {
        ConverseStreamRequest? captured = null;
        var options = new BedrockConverseProviderOptions
        {
            SupportsStrictTools = true,
            ToolChoice = BedrockToolChoice.Tool,
            RequiredToolName = "inspect",
            Transport = (request, token) => Capture(request, token),
        };
        var provider = new BedrockConverseProvider(options);
        var tool = new ToolDefinition(
            "inspect",
            "Inspect",
            "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"}}}",
            ToolConstrainedSampling.JsonSchema(ToolSchemaStrictness.Require));
        var call = new ToolCallContent("tool-1", "inspect", "{\"x\":1}");
        var request = new ModelRequest(
            "anthropic.claude-sonnet-4-5",
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
                    new AgentContent[] { new ReasoningContent("plan", "opaque"), call },
                    DateTimeOffset.UnixEpoch,
                    model: "anthropic.claude-sonnet-4-5",
                    stopReason: ModelStopReason.ToolUse,
                    provider: "amazon-bedrock",
                    api: "bedrock-converse-stream"),
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
            new ModelParameters
            {
                ReasoningLevel = "medium",
                CacheRetention = ModelCacheRetention.Long,
                ReasoningBudgets = new Dictionary<string, int> { ["medium"] = 9000 },
            },
            "session",
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.NotNull(captured);
        Assert.Equal(CacheTTL.ONE_HOUR, captured!.System[1].CachePoint.Ttl);
        Assert.Equal(ImageFormat.Png, captured.Messages[0].Content[1].Image.Format);
        Assert.Equal("opaque", captured.Messages[1].Content[0].ReasoningContent.ReasoningText.Signature);
        Assert.Equal("tool-1", captured.Messages[2].Content[0].ToolResult.ToolUseId);
        Assert.Equal(ImageFormat.Png, captured.Messages[2].Content[0].ToolResult.Content[1].Image.Format);
        Assert.True(captured.ToolConfig.Tools[0].ToolSpec.Strict);
        Assert.Equal("inspect", captured.ToolConfig.ToolChoice.Tool.Name);
        var fields = captured.AdditionalModelRequestFields.AsDictionary();
        Assert.Equal(9000, fields["thinking"].AsDictionary()["budget_tokens"].AsInt());
        Assert.Equal("summarized", fields["thinking"].AsDictionary()["display"].AsString());
        Assert.Equal("interleaved-thinking-2025-05-14", fields["anthropic_beta"].AsList()[0].AsString());

        async IAsyncEnumerable<BedrockProtocolEvent> Capture(
            ConverseStreamRequest value,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            captured = value;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return BedrockProtocolEvent.MessageStart("assistant");
            yield return BedrockProtocolEvent.MessageStop("end_turn");
        }
    }

    [Fact]
    public async Task UsesAdaptiveThinkingForNewClaudeModels()
    {
        ConverseStreamRequest? captured = null;
        var provider = new BedrockConverseProvider(new BedrockConverseProviderOptions
        {
            Transport = (request, token) => Capture(request, token),
        });
        var request = new ModelRequest(
            "anthropic.claude-opus-4-6-v1:0",
            string.Empty,
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters { ReasoningLevel = "high" },
            null,
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        var fields = captured!.AdditionalModelRequestFields.AsDictionary();
        Assert.Equal("adaptive", fields["thinking"].AsDictionary()["type"].AsString());
        Assert.Equal("high", fields["output_config"].AsDictionary()["effort"].AsString());

        async IAsyncEnumerable<BedrockProtocolEvent> Capture(
            ConverseStreamRequest value,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            captured = value;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return BedrockProtocolEvent.MessageStart("assistant");
            yield return BedrockProtocolEvent.MessageStop("end_turn");
        }
    }

    [Fact]
    public async Task PreservesUnknownStopAsFailedTerminal()
    {
        var provider = new BedrockConverseProvider(new BedrockConverseProviderOptions
        {
            Transport = (_, token) => UnknownStop(token),
        });

        var events = await CollectAsync(provider.StreamAsync(Request("model"), TestContext.Current.CancellationToken));

        var terminal = events.Last();
        Assert.Equal(ModelStreamEventKind.Failed, terminal.Kind);
        Assert.Equal("guardrail_intervened", terminal.Response!.RawStopReason);
        Assert.Equal("Provider stopped with: guardrail_intervened", terminal.Response.ErrorMessage);
    }

    [Fact]
    public async Task RejectsMissingMessageStop()
    {
        var provider = new BedrockConverseProvider(new BedrockConverseProviderOptions
        {
            Transport = (_, token) => MissingStop(token),
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request("model"), TestContext.Current.CancellationToken)));
        Assert.Contains("message_stop", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertsMessagesWithoutInvalidEmptyBlocks()
    {
        var provider = new BedrockConverseProvider(new BedrockConverseProviderOptions
        {
            Transport = (_, token) => MissingStop(token),
        });
        var firstCall = new ToolCallContent("one", "inspect", "{}");
        var secondCall = new ToolCallContent("two", "inspect", "{}");
        var request = new ModelRequest(
            "anthropic.claude-sonnet-4-5",
            string.Empty,
            new AgentMessage[]
            {
                new(AgentRole.User, new AgentContent[] { new TextContent("\ud83d") }, DateTimeOffset.UnixEpoch),
                new(
                    AgentRole.Assistant,
                    new AgentContent[] { new TextContent("\ud83d") },
                    DateTimeOffset.UnixEpoch,
                    model: "anthropic.claude-sonnet-4-5",
                    stopReason: ModelStopReason.Stop,
                    provider: "amazon-bedrock",
                    api: "bedrock-converse-stream"),
                AgentMessage.ToolResult(firstCall, new ToolResult(new AgentContent[] { new TextContent(" ") }), DateTimeOffset.UnixEpoch),
                AgentMessage.ToolResult(
                    secondCall,
                    new ToolResult(new AgentContent[] { new TextContent("done") }),
                    DateTimeOffset.UnixEpoch),
            },
            Array.Empty<ToolDefinition>(),
            new ModelParameters { CacheRetention = ModelCacheRetention.None },
            null,
            "run",
            1);

        var payload = provider.BuildRequest(request);

        Assert.Equal(
            "user:1|user:2",
            string.Join("|", payload.Messages.Select(value => value.Role.Value + ":" + value.Content.Count)));
        Assert.Equal("<empty>", payload.Messages[0].Content[0].Text);
        Assert.Equal(2, payload.Messages[1].Content.Count);
        Assert.Equal("<empty>", payload.Messages[1].Content[0].ToolResult.Content[0].Text);
        Assert.Equal("done", payload.Messages[1].Content[1].ToolResult.Content[0].Text);
    }

    [Fact]
    public void ReplaysReasoningOnlyWhenWireFormatAcceptsIt()
    {
        var provider = new BedrockConverseProvider(new BedrockConverseProviderOptions
        {
            Transport = (_, token) => MissingStop(token),
        });
        var unsignedClaude = AssistantRequest(
            "anthropic.claude-sonnet-4-5",
            new ReasoningContent("plan"));
        var signedClaude = AssistantRequest(
            "anthropic.claude-sonnet-4-5",
            new ReasoningContent("plan", "opaque"));
        var unsignedOther = AssistantRequest(
            "amazon.nova-lite-v1:0",
            new ReasoningContent("plan", "foreign"));

        Assert.Equal("plan", provider.BuildRequest(unsignedClaude).Messages[0].Content[0].Text);
        Assert.Equal("opaque", provider.BuildRequest(signedClaude).Messages[0].Content[0].ReasoningContent.ReasoningText.Signature);
        Assert.Null(provider.BuildRequest(unsignedOther).Messages[0].Content[0].ReasoningContent.ReasoningText.Signature);
    }

    [Fact]
    public void FiltersReservedHeadersBeforeSigning()
    {
        var headers = BedrockConverseProvider.NormalizeHeaders(new Dictionary<string, string>
        {
            ["Authorization"] = "bad",
            ["HOST"] = "bad",
            ["x-amz-date"] = "bad",
            ["x-game-session"] = "session",
        });
        var outgoing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = "signed",
            ["host"] = "service",
        };

        AwsBedrockTransport.ApplyHeaders(outgoing, headers);

        Assert.Single(headers);
        Assert.Equal(3, outgoing.Count);
        Assert.Equal("signed", outgoing["authorization"]);
        Assert.Equal("service", outgoing["host"]);
        Assert.Equal("session", outgoing["x-game-session"]);
        Assert.DoesNotContain(outgoing.Keys, key => key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CapturesBoundedAwsFailureMetadata()
    {
        var source = new AmazonBedrockRuntimeException(
            "invalid model",
            ErrorType.Sender,
            "ValidationException",
            "request-1",
            HttpStatusCode.BadRequest);

        var failure = AwsBedrockTransport.CreateProviderFailure(source, null, null);

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("bedrock_response_failure", diagnostic.Code);
        Assert.Equal(
            "{\"status\":400,\"errorCode\":\"ValidationException\",\"requestId\":\"request-1\"}",
            diagnostic.DataJson);
        Assert.Same(source, failure.InnerException);
    }

    [Fact]
    public void OmitsUntrustedOversizedAwsMetadata()
    {
        var source = new AmazonBedrockRuntimeException(
            "invalid model",
            ErrorType.Sender,
            new string('E', 300),
            new string('R', 1100),
            HttpStatusCode.Forbidden);

        var diagnostic = Assert.Single(AwsBedrockTransport.CreateProviderFailure(source, null, null).Diagnostics);

        Assert.Equal("{\"status\":403}", diagnostic.DataJson);
    }

    [Fact]
    public void ResolvesArnConfiguredEnvironmentAndEndpointRegionsInOrder()
    {
        Assert.Equal(
            "us-gov-west-1",
            BedrockConverseProvider.ResolveRegion(
                "arn:aws-us-gov:bedrock:us-gov-west-1:123:application-inference-profile/test",
                "eu-west-1",
                "https://bedrock-runtime.eu-central-1.amazonaws.com",
                "us-east-2",
                "ap-south-1"));
        Assert.Equal(
            "eu-west-1",
            BedrockConverseProvider.ResolveRegion("model", "eu-west-1", null, "us-east-2", null));
        Assert.Equal(
            "us-east-2",
            BedrockConverseProvider.ResolveRegion("model", null, null, "us-east-2", "ap-south-1"));
        Assert.Equal(
            "eu-central-1",
            BedrockConverseProvider.ResolveRegion(
                "model",
                null,
                "https://bedrock-runtime.eu-central-1.amazonaws.com",
                null,
                null));
    }

    [Fact]
    public void UsesResolvedModelNameForInferenceProfileCapabilities()
    {
        var provider = new BedrockConverseProvider(new BedrockConverseProviderOptions
        {
            ModelDisplayNameResolver = _ => "Claude Opus 4.6",
            Transport = (_, token) => MissingStop(token),
        });
        var request = new ModelRequest(
            "arn:aws:bedrock:us-east-1:123:application-inference-profile/custom",
            "rules",
            new[]
            {
                new AgentMessage(AgentRole.User, new[] { new TextContent("hello") }, DateTimeOffset.UnixEpoch),
            },
            Array.Empty<ToolDefinition>(),
            new ModelParameters { ReasoningLevel = "high" },
            null,
            "run",
            1);

        var payload = provider.BuildRequest(request);
        var fields = payload.AdditionalModelRequestFields.AsDictionary();

        Assert.Equal("adaptive", fields["thinking"].AsDictionary()["type"].AsString());
        Assert.Equal(CachePointType.Default, payload.System[1].CachePoint.Type);
        Assert.Equal(CachePointType.Default, payload.Messages[0].Content[^1].CachePoint.Type);
    }

    private static ModelRequest AssistantRequest(string model, AgentContent content) =>
        new(
            model,
            string.Empty,
            new[]
            {
                new AgentMessage(
                    AgentRole.Assistant,
                    new[] { content },
                    DateTimeOffset.UnixEpoch,
                    model: model,
                    stopReason: ModelStopReason.Stop,
                    provider: "amazon-bedrock",
                    api: "bedrock-converse-stream"),
            },
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            null,
            "run",
            1);

    private static async IAsyncEnumerable<BedrockProtocolEvent> SuccessfulStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return BedrockProtocolEvent.MessageStart("assistant");
        yield return BedrockProtocolEvent.ReasoningDelta(0, "plan", "signature");
        yield return BedrockProtocolEvent.ContentStop(0);
        yield return BedrockProtocolEvent.TextDelta(1, "hello");
        yield return BedrockProtocolEvent.ContentStop(1);
        yield return BedrockProtocolEvent.ContentStart(2, "tool-1", "move");
        yield return BedrockProtocolEvent.ToolDelta(2, "{\"x\":");
        yield return BedrockProtocolEvent.ToolDelta(2, "1}");
        yield return BedrockProtocolEvent.ContentStop(2);
        yield return BedrockProtocolEvent.MessageStop("tool_use");
        yield return BedrockProtocolEvent.Usage(10, 2, 3, 4);
    }

    private static async IAsyncEnumerable<BedrockProtocolEvent> UnknownStop(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return BedrockProtocolEvent.MessageStart("assistant");
        yield return BedrockProtocolEvent.MessageStop("guardrail_intervened");
    }

    private static async IAsyncEnumerable<BedrockProtocolEvent> MissingStop(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return BedrockProtocolEvent.MessageStart("assistant");
    }

    private static ModelRequest Request(string model) =>
        new(model, string.Empty, Array.Empty<AgentMessage>(), Array.Empty<ToolDefinition>(), new ModelParameters(), null, "run", 1);

    private static async Task<IReadOnlyList<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
            result.Add(item);
        }

        return result;
    }
}

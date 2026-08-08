using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Providers.Google.Tests;

public sealed class GoogleGenerativeProviderTests
{
    private const string ValidSignature = "AAAAAAAAAAAAAAAAAAAAAA==";

    [Fact]
    public async Task StreamsReasoningTextToolsSignaturesAndDetailedUsage()
    {
        const string stream = """
            data: {"responseId":"response-1","candidates":[{"content":{"parts":[{"thought":true,"text":"plan","thoughtSignature":"AAAAAAAAAAAAAAAAAAAAAA=="}]}}]}

            data: {"candidates":[{"content":{"parts":[{"thought":true,"text":" more"},{"text":"hello","thoughtSignature":"AAAAAAAAAAAAAAAAAAAAAA=="},{"functionCall":{"id":"call-1","name":"move","args":{"x":1}},"thoughtSignature":"AAAAAAAAAAAAAAAAAAAAAA=="}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":13,"cachedContentTokenCount":3,"candidatesTokenCount":4,"thoughtsTokenCount":2,"totalTokenCount":19}}

            """;
        var provider = Create(new StubHandler(_ => Response(stream)));

        var events = await CollectAsync(provider.StreamAsync(Request("gemini-3-pro-preview"), TestContext.Current.CancellationToken));

        var response = events.Last().Response!;
        Assert.Equal(ModelStopReason.ToolUse, response.StopReason);
        Assert.Equal("response-1", response.ResponseId);
        Assert.Equal("STOP", response.RawStopReason);
        var reasoning = Assert.IsType<ReasoningContent>(response.Content[0]);
        Assert.Equal("plan more", reasoning.Text);
        Assert.Equal(ValidSignature, reasoning.Signature);
        var text = Assert.IsType<TextContent>(response.Content[1]);
        Assert.Equal("hello", text.Text);
        Assert.Equal(ValidSignature, text.Signature);
        var tool = Assert.IsType<ToolCallContent>(response.Content[2]);
        Assert.Equal("call-1", tool.Id);
        Assert.Equal("{\"x\":1}", tool.ArgumentsJson);
        Assert.Equal(ValidSignature, tool.ThoughtSignature);
        Assert.Equal(10, response.Usage.InputTokens);
        Assert.Equal(3, response.Usage.CacheReadTokens);
        Assert.Equal(6, response.Usage.OutputTokens);
        Assert.Equal(2, response.Usage.ReasoningTokens);
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.ReasoningStarted);
        Assert.Contains(events, item => item.Kind == ModelStreamEventKind.ToolCallEnded);
    }

    [Fact]
    public async Task SerializesGemini3HistoryImagesStrictToolsAndThinking()
    {
        var handler = new StubHandler(_ => Response(StopStream()));
        var options = Options(new HttpClient(handler));
        options.ToolChoice = GoogleToolChoice.Auto;
        var provider = new GoogleGenerativeProvider(options);
        var tool = new ToolDefinition(
            "inspect",
            "Inspect",
            "{\"$schema\":\"draft\",\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"}},\"required\":[\"x\"]}",
            ToolConstrainedSampling.JsonSchema(ToolSchemaStrictness.Require));
        var call = new ToolCallContent("call-1", "inspect", "{\"x\":1}", ValidSignature);
        var messages = new AgentMessage[]
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
                new AgentContent[]
                {
                    new ReasoningContent(string.Empty, ValidSignature),
                    new TextContent(string.Empty, ValidSignature),
                    call,
                },
                DateTimeOffset.UnixEpoch,
                model: "gemini-3-pro-preview",
                stopReason: ModelStopReason.ToolUse,
                provider: "google",
                api: "google-generative-ai"),
            AgentMessage.ToolResult(
                call,
                new ToolResult(new AgentContent[]
                {
                    new TextContent("clear"),
                    new BinaryContent(AgentMediaKind.Image, "dG9vbA==", "image/png"),
                }),
                DateTimeOffset.UnixEpoch),
        };
        var request = new ModelRequest(
            "gemini-3-pro-preview",
            "rules",
            messages,
            new[] { tool },
            new ModelParameters { ReasoningLevel = "high", Temperature = 0.2, MaxOutputTokens = 50 },
            "session",
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("test-key", handler.ApiKey);
        Assert.Contains("gemini-3-pro-preview", handler.RequestUri, StringComparison.Ordinal);
        Assert.Contains("alt=sse", handler.RequestUri, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.Equal("rules", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("HIGH", root.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.True(root.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("includeThoughts").GetBoolean());
        Assert.Equal("VALIDATED", root.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        var assistantParts = root.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(ValidSignature, assistantParts[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal(ValidSignature, assistantParts[1].GetProperty("thoughtSignature").GetString());
        Assert.Equal("call-1", assistantParts[2].GetProperty("functionCall").GetProperty("id").GetString());
        var functionResponse = root.GetProperty("contents")[2].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("call-1", functionResponse.GetProperty("id").GetString());
        Assert.Equal("dG9vbA==", functionResponse.GetProperty("parts")[0].GetProperty("inlineData").GetProperty("data").GetString());
    }

    [Fact]
    public async Task Gemini2UsesSeparateToolImageTurnAndCanDisableThinking()
    {
        var handler = new StubHandler(_ => Response(StopStream()));
        var options = Options(new HttpClient(handler));
        options.UseLegacyOpenApiToolSchemas = true;
        var provider = new GoogleGenerativeProvider(options);
        var call = new ToolCallContent("call-1", "read", "{}");
        var request = new ModelRequest(
            "gemini-2.5-flash",
            string.Empty,
            new AgentMessage[]
            {
                new(
                    AgentRole.Assistant,
                    new AgentContent[] { call },
                    DateTimeOffset.UnixEpoch,
                    model: "gemini-2.5-flash",
                    stopReason: ModelStopReason.ToolUse,
                    provider: "google",
                    api: "google-generative-ai"),
                AgentMessage.ToolResult(
                    call,
                    new ToolResult(new AgentContent[] { new BinaryContent(AgentMediaKind.Image, "aW1hZ2U=", "image/png") }),
                    DateTimeOffset.UnixEpoch),
            },
            new[]
            {
                new ToolDefinition(
                    "read",
                    "Read",
                    "{\"$schema\":\"draft\",\"type\":\"object\",\"properties\":{\"path\":{\"$id\":\"nested\",\"type\":\"string\"}}}"),
            },
            new ModelParameters { ReasoningLevel = "off" },
            null,
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.Equal(0, root.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
        var assistantCall = root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("functionCall");
        Assert.False(assistantCall.TryGetProperty("id", out _));
        Assert.Equal("Tool result image:", root.GetProperty("contents")[2].GetProperty("parts")[0].GetProperty("text").GetString());
        var parameters = root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parameters");
        Assert.False(parameters.TryGetProperty("$schema", out _));
        Assert.False(parameters.GetProperty("properties").GetProperty("path").TryGetProperty("$id", out _));
    }

    [Fact]
    public async Task CrossModelReplayDropsOpaqueSignaturesAndNormalizesToolIds()
    {
        var handler = new StubHandler(_ => Response(StopStream()));
        var provider = Create(handler);
        var call = new ToolCallContent("foreign|call/1", "move", "{}", ValidSignature);
        var request = new ModelRequest(
            "gemini-3-flash-preview",
            string.Empty,
            new AgentMessage[]
            {
                new(
                    AgentRole.Assistant,
                    new AgentContent[]
                    {
                        new ReasoningContent("foreign plan", ValidSignature),
                        new TextContent("answer", ValidSignature),
                        call,
                    },
                    DateTimeOffset.UnixEpoch,
                    model: "other-model",
                    stopReason: ModelStopReason.ToolUse,
                    provider: "other",
                    api: "other-api"),
                AgentMessage.ToolResult(call, new ToolResult(new[] { new TextContent("done") }), DateTimeOffset.UnixEpoch),
            },
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            null,
            "run",
            1);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var contents = document.RootElement.GetProperty("contents");
        var modelParts = contents[0].GetProperty("parts");
        Assert.Equal("foreign plan", modelParts[0].GetProperty("text").GetString());
        Assert.False(modelParts[0].TryGetProperty("thought", out _));
        Assert.False(modelParts[0].TryGetProperty("thoughtSignature", out _));
        Assert.False(modelParts[1].TryGetProperty("thoughtSignature", out _));
        Assert.Equal("foreign_call_1", modelParts[2].GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("foreign_call_1", contents[1].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("id").GetString());
    }

    [Fact]
    public async Task PreservesProviderSafetyStopAsFailedTerminal()
    {
        var provider = Create(new StubHandler(_ => Response("""
            data: {"responseId":"response-1","candidates":[{"finishReason":"SAFETY"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":0,"totalTokenCount":1}}

            """)));

        var events = await CollectAsync(provider.StreamAsync(Request("gemini-2.5-flash"), TestContext.Current.CancellationToken));

        var terminal = events.Last();
        Assert.Equal(ModelStreamEventKind.Failed, terminal.Kind);
        Assert.Equal(ModelStopReason.Error, terminal.Response!.StopReason);
        Assert.Equal("SAFETY", terminal.Response.RawStopReason);
        Assert.Equal("Provider stopped with: SAFETY", terminal.Response.ErrorMessage);
    }

    [Fact]
    public async Task RejectsStreamWithoutFinishReason()
    {
        var provider = Create(new StubHandler(_ => Response("""
            data: {"responseId":"response-1","candidates":[{"content":{"parts":[{"text":"hello"}]}}]}

            """)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(provider.StreamAsync(Request("gemini-2.5-flash"), TestContext.Current.CancellationToken)));
        Assert.Contains("finish reason", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VertexUsesBearerCredential()
    {
        var handler = new StubHandler(_ => Response(StopStream()));
        var options = new GoogleGenerativeProviderOptions(
            new HttpClient(handler),
            GoogleVertexCredentials.Endpoint("project", "us-central1"),
            GoogleApiFlavor.Vertex)
        {
            Credential = "access-token",
        };
        var provider = new GoogleGenerativeProvider(options);

        await CollectAsync(provider.StreamAsync(Request("gemini-3-flash-preview"), TestContext.Current.CancellationToken));

        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("access-token", handler.Authorization?.Parameter);
        Assert.Contains("models/gemini-3-flash-preview:streamGenerateContent", handler.RequestUri, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildsRegionalVertexEndpointWithoutLosingModelPlaceholder()
    {
        var endpoint = GoogleVertexCredentials.Endpoint("my project", "us-central1");

        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/my%20project/locations/us-central1/publishers/google/models/%7Bmodel%7D:streamGenerateContent",
            endpoint.AbsoluteUri);
    }

    private static GoogleGenerativeProvider Create(HttpMessageHandler handler) =>
        new(Options(new HttpClient(handler)));

    private static GoogleGenerativeProviderOptions Options(HttpClient client) =>
        new(client, new Uri("https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent"))
        {
            Credential = "test-key",
        };

    private static ModelRequest Request(string model) =>
        new(model, string.Empty, Array.Empty<AgentMessage>(), Array.Empty<ToolDefinition>(), new ModelParameters(), null, "run", 1);

    private static string StopStream() => """
        data: {"responseId":"response-1","candidates":[{"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}

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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        public string? RequestBody { get; private set; }

        public string RequestUri { get; private set; } = string.Empty;

        public string? ApiKey { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestUri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            ApiKey = request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null;
            Authorization = request.Headers.Authorization;
            return _response(request);
        }
    }
}

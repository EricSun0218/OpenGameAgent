using System.Net;
using System.Text;
using OpenGameAgent.Kernel;
using OpenGameAgent.Providers.OpenAICompatible;
using Xunit;

namespace OpenGameAgent.Tests;

public sealed class RoutingProviderIntegrationTests
{
    [Fact]
    public async Task OpenAiCompatibleTextFenceSelectsQuickWithoutAdvertisingGameTools()
    {
        const string stream = """
            data: {"choices":[{"delta":{"content":"```json\n{\"route\":\"quick\",\"reason\":\"ordinary-question\"}\n```"},"finish_reason":null}]}

            data: {"choices":[],"usage":{"prompt_tokens":8,"completion_tokens":6}}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAICompatibleProvider(new OpenAICompatibleProviderOptions(
            new HttpClient(handler),
            new Uri("https://provider.example/v1/chat/completions")));
        var classifier = new ModelGameRouteClassifier(provider, "deepseek-chat");

        var decision = await classifier.ClassifyAsync(
            new GameRouteContext(
                new GameInput("session", "actor", "chat", "{\"message\":\"hello\"}", new GameMoment("world", 1)),
                availableToolCount: 4),
            TestContext.Current.CancellationToken);

        Assert.NotNull(decision);
        Assert.Equal(GameRouteKind.QuickResponse, decision.Route);
        Assert.Equal("ordinary-question", decision.Reason);
        Assert.True(decision.Classification!.Selected);
        Assert.DoesNotContain("\"tools\"", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepSeekReasoningOnlyIsDiagnosedWithoutParsingPrivateReasoning()
    {
        const string stream = """
            data: {"choices":[{"delta":{"reasoning_content":"{\"route\":\"quick\"}"},"finish_reason":null}]}

            data: {"choices":[],"usage":{"prompt_tokens":8,"completion_tokens":128,"completion_tokens_details":{"reasoning_tokens":128}}}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var handler = Handler(stream);
        var options = Options(handler);
        options.Protocol.ThinkingFormat = OpenAICompatibleThinkingFormat.DeepSeek;
        var classifier = new ModelGameRouteClassifier(new OpenAICompatibleProvider(options), "deepseek-chat");
        var context = Context();

        var decision = await new AutomaticGameRoutePolicy(classifier: classifier.ClassifyAsync)
            .RouteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.Agent, decision.Route);
        Assert.Equal(GameRouteClassificationFailure.ReasoningOnly, decision.Classification!.Failure);
        Assert.Equal(new[] { AgentContentKind.Reasoning }, decision.Classification.ResponseContentKinds);
        Assert.Equal(0, decision.Classification.VisibleContentCharacters);
        Assert.Equal(17, decision.Classification.ReasoningCharacters);
        using var request = System.Text.Json.JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("disabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(request.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task DeepSeekReasoningBudgetExhaustionRemainsBoundedAndDoesNotUseReasoningAsDecision()
    {
        const string stream = """
            data: {"choices":[{"delta":{"reasoning_content":"{\"route\":\"quick\"}"},"finish_reason":null}]}

            data: {"choices":[],"usage":{"prompt_tokens":8,"completion_tokens":128,"completion_tokens_details":{"reasoning_tokens":128}}}

            data: {"choices":[{"delta":{},"finish_reason":"length"}]}

            data: [DONE]

            """;
        var handler = Handler(stream);
        var options = Options(handler);
        options.Protocol.ThinkingFormat = OpenAICompatibleThinkingFormat.DeepSeek;
        var classifier = new ModelGameRouteClassifier(new OpenAICompatibleProvider(options), "deepseek-chat");
        var context = Context();

        var decision = await new AutomaticGameRoutePolicy(classifier: classifier.ClassifyAsync)
            .RouteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(GameRouteKind.Agent, decision.Route);
        Assert.Equal(GameRouteClassificationFailure.BudgetExhausted, decision.Classification!.Failure);
        Assert.Equal(new[] { AgentContentKind.Reasoning }, decision.Classification.ResponseContentKinds);
        Assert.Equal(0, decision.Classification.VisibleContentCharacters);
        Assert.Equal(17, decision.Classification.ReasoningCharacters);
    }

    [Fact]
    public async Task ConfiguredReasoningCanFinishWithVisibleContentWithoutExposingReasoning()
    {
        const string stream = """
            data: {"choices":[{"delta":{"reasoning_content":"private route analysis"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"{\"route\":\"quick\",\"reason\":\"visible-final\"}"},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var handler = Handler(stream);
        var options = Options(handler);
        options.Protocol.ThinkingFormat = OpenAICompatibleThinkingFormat.DeepSeek;
        var classifier = new ModelGameRouteClassifier(
            new OpenAICompatibleProvider(options),
            "deepseek-reasoner",
            options: new ModelGameRouteClassifierOptions { ReasoningLevel = "minimal" });

        var decision = await classifier.ClassifyAsync(Context(), TestContext.Current.CancellationToken);

        Assert.NotNull(decision);
        Assert.Equal(GameRouteKind.QuickResponse, decision.Route);
        Assert.Equal("visible-final", decision.Reason);
        Assert.Equal(new[] { AgentContentKind.Text, AgentContentKind.Reasoning }, decision.Classification!.ResponseContentKinds);
        Assert.Equal(42, decision.Classification.VisibleContentCharacters);
        Assert.Equal(22, decision.Classification.ReasoningCharacters);
        using var request = System.Text.Json.JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("enabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("minimal", request.RootElement.GetProperty("reasoning_effort").GetString());
    }

    private static GameRouteContext Context() => new(
        new GameInput("session", "actor", "chat", "{\"message\":\"hello\"}", new GameMoment("world", 1)),
        availableToolCount: 4);

    private static OpenAICompatibleProviderOptions Options(RecordingHandler handler) => new(
        new HttpClient(handler),
        new Uri("https://provider.example/v1/chat/completions"));

    private static RecordingHandler Handler(string stream) => new(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
    });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}

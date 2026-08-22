using System.Net;
using System.Text;
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

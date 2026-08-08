using System.Net;
using System.Text;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Providers.OpenAI.Tests;

public sealed class AzureOpenAIResponsesTests
{
    [Theory]
    [InlineData("https://example.openai.azure.com", "https://example.openai.azure.com/openai/v1/responses?api-version=v1")]
    [InlineData("https://example.cognitiveservices.azure.com/openai", "https://example.cognitiveservices.azure.com/openai/v1/responses?api-version=v1")]
    [InlineData("https://example.ai.azure.com/openai/v1/responses?old=true", "https://example.ai.azure.com/openai/v1/responses?api-version=v1")]
    [InlineData("https://proxy.example.test/v1?custom=true", "https://proxy.example.test/v1/responses?custom=true")]
    public void NormalizesHostedAndProxyEndpoints(string input, string expected)
    {
        Assert.Equal(expected, AzureOpenAIResponses.BuildResponsesEndpoint(input).AbsoluteUri);
    }

    [Fact]
    public async Task SendsApiKeyHeaderAndDeploymentModel()
    {
        var handler = new CaptureHandler();
        var options = AzureOpenAIResponses.CreateOptions(
            new HttpClient(handler),
            "https://example.openai.azure.com",
            "secret",
            "2025-04-01-preview");
        var provider = new OpenAIResponsesProvider(options);
        var request = new ModelRequest(
            "deployment-name",
            string.Empty,
            Array.Empty<AgentMessage>(),
            Array.Empty<ToolDefinition>(),
            new ModelParameters(),
            null,
            "run",
            1);

        await foreach (var _ in provider.StreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal("secret", handler.ApiKey);
        Assert.Null(handler.Authorization);
        Assert.Contains("\"model\":\"deployment-name\"", handler.Body, StringComparison.Ordinal);
        Assert.Equal("2025-04-01-preview", ParseQuery(handler.RequestUri!).Single(value => value.Key == "api-version").Value);
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseQuery(Uri uri)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pieces = part.Split(new[] { '=' }, 2);
            yield return new KeyValuePair<string, string>(
                Uri.UnescapeDataString(pieces[0]),
                Uri.UnescapeDataString(pieces[1]));
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? ApiKey { get; private set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"response\",\"model\":\"model\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0}}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }
}

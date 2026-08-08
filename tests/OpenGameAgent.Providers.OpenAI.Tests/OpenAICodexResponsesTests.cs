using System.Net;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Providers.OpenAI.Tests;

public sealed class OpenAICodexResponsesTests
{
    [Fact]
    public async Task SendsAccountScopedHeadersAndCodexRequestShape()
    {
        var handler = new CaptureHandler();
        var token = Token("account-one");
        var provider = new OpenAIResponsesProvider(OpenAICodexResponses.CreateOptions(
            new HttpClient(handler),
            token));
        var request = new ModelRequest(
            "game-model",
            "Act as the world simulation agent.",
            new[] { AgentMessage.User("advance", DateTimeOffset.UnixEpoch) },
            Array.Empty<ToolDefinition>(),
            new ModelParameters
            {
                ReasoningLevel = "high",
                Transport = ModelTransport.ServerSentEvents,
            },
            "session-one",
            "run",
            1);

        await foreach (var _ in provider.StreamAsync(request, TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal("Bearer " + token, handler.Authorization);
        Assert.Equal("account-one", handler.AccountId);
        Assert.Equal("responses=experimental", handler.Beta);
        Assert.Equal("opengameagent", handler.Originator);
        Assert.Equal("session-one", handler.SessionId);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal("Act as the world simulation agent.", root.GetProperty("instructions").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("low", root.GetProperty("text").GetProperty("verbosity").GetString());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("reasoning.encrypted_content", root.GetProperty("include")[0].GetString());
        Assert.DoesNotContain(root.GetProperty("input").EnumerateArray(), item =>
            item.TryGetProperty("role", out var role)
            && role.GetString() is "system" or "developer");
    }

    [Fact]
    public async Task ResolvesTokenAndAccountTogetherForEveryRequest()
    {
        var handler = new CaptureHandler();
        var calls = 0;
        var options = OpenAICodexResponses.CreateOptions(
            new HttpClient(handler),
            _ => new ValueTask<OpenAIRequestCredential?>(new OpenAIRequestCredential(
                Token(calls++ == 0 ? "account-one" : "account-two"))));
        var provider = new OpenAIResponsesProvider(options);

        await DrainAsync(provider, Request("one"));
        Assert.Equal("account-one", handler.AccountId);
        await DrainAsync(provider, Request("two"));
        Assert.Equal("account-two", handler.AccountId);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RejectsTokensWithoutTheAccountClaim()
    {
        var token = TokenPayload(new Dictionary<string, object?> { ["sub"] = "user" });

        Assert.Throws<ArgumentException>(() => OpenAICodexResponses.ExtractAccountId(token));
    }

    private static ModelRequest Request(string runId) => new(
        "model",
        string.Empty,
        Array.Empty<AgentMessage>(),
        Array.Empty<ToolDefinition>(),
        new ModelParameters { Transport = ModelTransport.ServerSentEvents },
        null,
        runId,
        1);

    private static async Task DrainAsync(IModelProvider provider, ModelRequest request)
    {
        await foreach (var _ in provider.StreamAsync(request, TestContext.Current.CancellationToken))
        {
        }
    }

    private static string Token(string accountId) => TokenPayload(new Dictionary<string, object?>
    {
        ["https://api.openai.com/auth"] = new Dictionary<string, object?>
        {
            ["chatgpt_account_id"] = accountId,
        },
    });

    private static string TokenPayload(IReadOnlyDictionary<string, object?> payload) =>
        Base64Url("{\"alg\":\"none\"}") + "."
        + Base64Url(JsonSerializer.Serialize(payload)) + ".signature";

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        public string? AccountId { get; private set; }

        public string? Beta { get; private set; }

        public string? Originator { get; private set; }

        public string? SessionId { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            AccountId = Header(request, "chatgpt-account-id");
            Beta = Header(request, "OpenAI-Beta");
            Originator = Header(request, "originator");
            SessionId = Header(request, "session-id");
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"response\",\"model\":\"model\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0}}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    }
}

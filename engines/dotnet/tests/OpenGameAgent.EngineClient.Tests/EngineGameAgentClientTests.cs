using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenGameAgent.EngineClient;
using Xunit;

namespace OpenGameAgent.EngineClient.Tests;

public sealed class EngineGameAgentClientTests
{
    [Fact]
    public void RejectsPlaintextRemoteServer()
    {
        var options = new EngineGameAgentClientOptions(new Uri("http://agent.example"));
        Assert.Throws<ArgumentException>(() => new EngineGameAgentClient(options));
    }

    [Fact]
    public async Task StreamsSplitEventsAndKeepsAuthenticationOutsideInput()
    {
        string? requestBody = null;
        var handler = new FakeHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedStream(
                    "id: event-1\r\nevent: run.started\r\ndata: {\"type\":\"run.started\"}\r\n\r\n",
                    7)),
                RequestMessage = request,
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        });
        using var client = new EngineGameAgentClient(new EngineGameAgentClientOptions(new Uri("http://127.0.0.1:4317"))
        {
            MessageHandler = handler,
            AuthenticationJsonProvider = _ => ValueTask.FromResult<string?>("{\"pairingToken\":\"bounded\"}"),
        });
        var events = new List<EngineGameAgentEvent>();
        await client.RunAsync(
            "{\"id\":\"input\",\"session\":{}}",
            (item, _) =>
            {
                events.Add(item);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(events);
        Assert.Equal("event-1", events[0].Id);
        Assert.Equal("run.started", events[0].Name);
        using JsonDocument body = JsonDocument.Parse(requestBody!);
        Assert.Equal("input", body.RootElement.GetProperty("input").GetProperty("id").GetString());
        Assert.Equal("bounded", body.RootElement.GetProperty("authentication").GetProperty("pairingToken").GetString());
        Assert.False(body.RootElement.GetProperty("input").TryGetProperty("authentication", out _));
    }

    [Fact]
    public async Task UsesExactRunCoordinatesForControl()
    {
        string? requestBody = null;
        var handler = new FakeHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(request, HttpStatusCode.OK, "{\"accepted\":true}");
        });
        using var client = new EngineGameAgentClient(new EngineGameAgentClientOptions(new Uri("https://agent.example"))
        {
            MessageHandler = handler,
        });
        Assert.True(await client.AbortAsync(
            "{\"actorId\":\"a\"}",
            "{\"runId\":\"r\",\"turn\":3}",
            TestContext.Current.CancellationToken));
        using JsonDocument body = JsonDocument.Parse(requestBody!);
        Assert.Equal("r", body.RootElement.GetProperty("expected").GetProperty("runId").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("expected").GetProperty("turn").GetInt32());
    }

    [Fact]
    public async Task ReportsSafeErrorWithoutReflectingResponseBody()
    {
        const string secret = "must-not-escape";
        var handler = new FakeHandler(request => Task.FromResult(JsonResponse(
            request,
            HttpStatusCode.Forbidden,
            $"{{\"error\":\"forbidden\",\"detail\":\"{secret}\"}}")));
        using var client = new EngineGameAgentClient(new EngineGameAgentClientOptions(new Uri("https://agent.example"))
        {
            MessageHandler = handler,
        });
        EngineGameAgentClientException error = await Assert.ThrowsAsync<EngineGameAgentClientException>(
            () => client.ReadUsageAsync("{}", TestContext.Current.CancellationToken));
        Assert.Equal("forbidden", error.Category);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsOversizedIncompleteEvent()
    {
        var handler = new FakeHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"value\":\"" + new string('x', 2048)),
            RequestMessage = request,
        }));
        handler.ResponseContentType = "text/event-stream";
        using var client = new EngineGameAgentClient(new EngineGameAgentClientOptions(new Uri("https://agent.example"))
        {
            MessageHandler = handler,
            MaximumEventBytes = 1024,
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RunAsync(
            "{}",
            (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private static HttpResponseMessage JsonResponse(HttpRequestMessage request, HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public string? ResponseContentType { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Task<HttpResponseMessage> pending = send(request);
            if (ResponseContentType is null) return pending;
            return ApplyContentTypeAsync(pending, ResponseContentType);
        }

        private static async Task<HttpResponseMessage> ApplyContentTypeAsync(
            Task<HttpResponseMessage> pending,
            string contentType)
        {
            HttpResponseMessage response = await pending;
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return response;
        }
    }

    private sealed class ChunkedStream(string value, int chunkSize) : MemoryStream(Encoding.UTF8.GetBytes(value))
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => base.ReadAsync(buffer, offset, Math.Min(count, chunkSize), cancellationToken);
    }
}

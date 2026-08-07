using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenGameAgent.Providers.MediaHttp.Tests;

public sealed class MediaHttpTests
{
    [Fact]
    public void InvalidAuthenticationHeaderIsRejectedBeforeTransport()
    {
        var options = new HttpMediaGeneratorOptions(
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("transport must not run"))),
            new Uri("https://media.test/generate"))
        {
            ApiKeyHeader = "Bad:Name",
        };

        Assert.Throws<ArgumentException>(() => new HttpMediaGenerator(options));
    }

    [Fact]
    public void RemotePlainHttpRequiresExplicitOptInWhileLoopbackRemainsAvailable()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("transport must not run")));

        Assert.Throws<ArgumentException>(() => new HttpMediaGenerator(
            new HttpMediaGeneratorOptions(client, new Uri("http://media.test/generate"))));
        _ = new HttpMediaGenerator(
            new HttpMediaGeneratorOptions(client, new Uri("http://127.0.0.1:8080/generate")));
        _ = new HttpMediaGenerator(
            new HttpMediaGeneratorOptions(client, new Uri("http://media.test/generate"))
            {
                AllowInsecureHttp = true,
            });
    }

    [Fact]
    public async Task SynchronousImageResultPreservesStructuredParameters()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            {
              "status": "completed",
              "requestId": "provider-1",
              "outputs": [
                { "uri": "https://assets.test/image.png", "mediaType": "image/png", "name": "portrait" }
              ],
              "metadata": { "seed": 42 }
            }
            """));
        var generator = Create(handler);
        var request = new GameMediaGenerationRequest(
            "request",
            GameMediaKind.Image,
            "{\"character\":{\"age\":20}}",
            "{\"guidance\":7.5}",
            "portrait");

        var result = await generator.GenerateAsync(request, null, TestContext.Current.CancellationToken);

        Assert.Equal("image/png", Assert.Single(result.Outputs).MediaType);
        Assert.Equal("provider-1", result.ProviderRequestId);
        using var metadata = JsonDocument.Parse(result.MetadataJson);
        Assert.Equal(42, metadata.RootElement.GetProperty("seed").GetInt32());
        using var sent = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal(7.5, sent.RootElement.GetProperty("parameters").GetProperty("guidance").GetDouble());
    }

    [Fact]
    public async Task AsynchronousVideoJobPollsAndReportsProgress()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            JsonResponse("{\"status\":\"queued\",\"statusUrl\":\"/jobs/1\",\"retryAfterMs\":0}"),
            JsonResponse("{\"status\":\"running\",\"statusUrl\":\"/jobs/1\",\"retryAfterMs\":0,\"progress\":{\"stage\":\"rendering\",\"fraction\":0.5}}"),
            JsonResponse("{\"status\":\"completed\",\"outputs\":[{\"uri\":\"file:///generated/video.mp4\",\"mediaType\":\"video/mp4\"}]}"),
        });
        var handler = new StubHandler(_ => responses.Dequeue());
        var generator = Create(handler);
        var progress = new List<GameMediaGenerationProgress>();

        var result = await generator.GenerateAsync(
            new GameMediaGenerationRequest("video", GameMediaKind.Video, "{}"),
            (item, _) =>
            {
                progress.Add(item);
                return default;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("video/mp4", Assert.Single(result.Outputs).MediaType);
        Assert.Equal(0.5, Assert.Single(progress).Fraction);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
    }

    [Fact]
    public async Task PollAttemptLimitCountsPollRequestsRatherThanInitialSubmission()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            JsonResponse("{\"status\":\"pending\",\"statusUrl\":\"/jobs/1\",\"retryAfterMs\":0}"),
            JsonResponse("{\"status\":\"completed\",\"outputs\":[{\"uri\":\"https://assets.test/image.png\",\"mediaType\":\"image/png\"}]}"),
        });
        var generator = new HttpMediaGenerator(new HttpMediaGeneratorOptions(
            new HttpClient(new StubHandler(_ => responses.Dequeue())),
            new Uri("https://media.test/generate"))
        {
            MaxPollAttempts = 1,
            PollInterval = TimeSpan.Zero,
        });

        var result = await generator.GenerateAsync(
            new GameMediaGenerationRequest("image", GameMediaKind.Image, "{}"),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("image/png", Assert.Single(result.Outputs).MediaType);
    }

    [Theory]
    [InlineData("{\"status\":\"completed\",\"status\":\"failed\"}", "duplicate")]
    [InlineData("{\"status\":\"completed\",\"outputs\":[{\"uri\":\"\",\"mediaType\":\"image/png\"}]}", "invalid output")]
    [InlineData("{\"status\":\"pending\",\"statusUrl\":\"/job\",\"retryAfterMs\":\"soon\"}", "integer")]
    public async Task AmbiguousOrMalformedMediaDocumentsFailWithProviderException(string json, string expected)
    {
        var generator = Create(new StubHandler(_ => JsonResponse(json)));

        var exception = await Assert.ThrowsAsync<MediaGenerationException>(async () =>
            await generator.GenerateAsync(
                new GameMediaGenerationRequest("image", GameMediaKind.Image, "{}"),
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrossOriginStatusUrlIsRejectedByDefault()
    {
        var handler = new StubHandler(_ => JsonResponse(
            "{\"status\":\"queued\",\"statusUrl\":\"https://untrusted.test/jobs/1\",\"retryAfterMs\":0}"));
        var generator = Create(handler);

        var exception = await Assert.ThrowsAsync<MediaGenerationException>(async () =>
            await generator.GenerateAsync(
                new GameMediaGenerationRequest("audio", GameMediaKind.Audio, "{}"),
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("different origin", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResponseIsRejectedWhileStreamingPastConfiguredLimit()
    {
        var handler = new StubHandler(_ => JsonResponse("{\"status\":\"completed\",\"padding\":\"1234567890\"}"));
        var generator = new HttpMediaGenerator(new HttpMediaGeneratorOptions(
            new HttpClient(handler),
            new Uri("https://media.test/generate"))
        {
            MaxResponseBytes = 16,
        });

        var exception = await Assert.ThrowsAsync<MediaGenerationException>(async () =>
            await generator.GenerateAsync(
                new GameMediaGenerationRequest("image", GameMediaKind.Image, "{}"),
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("size limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidProgressShapeProducesProviderSpecificFailure()
    {
        var handler = new StubHandler(_ => JsonResponse(
            "{\"status\":\"running\",\"statusUrl\":\"/jobs/1\",\"progress\":{\"fraction\":\"half\"}}"));
        var generator = Create(handler);

        var exception = await Assert.ThrowsAsync<MediaGenerationException>(async () =>
            await generator.GenerateAsync(
                new GameMediaGenerationRequest("video", GameMediaKind.Video, "{}"),
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("invalid job document", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedRequestIsRejectedBeforeNetworkDispatch()
    {
        var handler = new StubHandler(_ => JsonResponse("{}"));
        var generator = new HttpMediaGenerator(new HttpMediaGeneratorOptions(
            new HttpClient(handler),
            new Uri("https://media.test/generate"))
        {
            MaxRequestBytes = 64,
        });

        var exception = await Assert.ThrowsAsync<MediaGenerationException>(async () =>
            await generator.GenerateAsync(
                new GameMediaGenerationRequest("image", GameMediaKind.Image, "{\"value\":\"" + new string('x', 128) + "\"}"),
                null,
                TestContext.Current.CancellationToken));

        Assert.Contains("request exceeded", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DynamicCredentialsCanRotateAcrossLongRunningJobs()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            JsonResponse("{\"status\":\"queued\",\"statusUrl\":\"/jobs/1\",\"retryAfterMs\":0}"),
            JsonResponse("{\"status\":\"completed\",\"outputs\":[{\"uri\":\"https://assets.test/audio.mp3\",\"mediaType\":\"audio/mpeg\"}]}"),
        });
        var handler = new StubHandler(_ => responses.Dequeue());
        var key = 0;
        var generator = new HttpMediaGenerator(new HttpMediaGeneratorOptions(
            new HttpClient(handler),
            new Uri("https://media.test/generate"))
        {
            PollInterval = TimeSpan.Zero,
            GetApiKeyAsync = _ => new ValueTask<string?>("key-" + Interlocked.Increment(ref key)),
        });

        await generator.GenerateAsync(
            new GameMediaGenerationRequest("audio", GameMediaKind.Audio, "{}"),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Bearer key-1", "Bearer key-2" }, handler.Authorizations);
    }

    [Fact]
    public async Task OversizedDynamicCredentialIsRejectedBeforeTransport()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("transport must not run"));
        var generator = new HttpMediaGenerator(new HttpMediaGeneratorOptions(
            new HttpClient(handler),
            new Uri("https://media.test/generate"))
        {
            GetApiKeyAsync = _ => new ValueTask<string?>(new string('x', 65_537)),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await generator.GenerateAsync(
                new GameMediaGenerationRequest("image", GameMediaKind.Image, "{}"),
                null,
                TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CrossOriginPollingDoesNotForwardCredentialsByDefault()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            JsonResponse("{\"status\":\"queued\",\"statusUrl\":\"https://jobs.test/1\",\"retryAfterMs\":0}"),
            JsonResponse("{\"status\":\"completed\",\"outputs\":[{\"uri\":\"https://assets.test/video.mp4\",\"mediaType\":\"video/mp4\"}]}"),
        });
        var handler = new StubHandler(_ => responses.Dequeue());
        var generator = new HttpMediaGenerator(new HttpMediaGeneratorOptions(
            new HttpClient(handler),
            new Uri("https://media.test/generate"))
        {
            ApiKey = "secret",
            PollInterval = TimeSpan.Zero,
            RestrictStatusUrlToEndpointOrigin = false,
        });

        await generator.GenerateAsync(
            new GameMediaGenerationRequest("video", GameMediaKind.Video, "{}"),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { "Bearer secret", null }, handler.Authorizations);
    }

    private static HttpMediaGenerator Create(HttpMessageHandler handler) =>
        new(new HttpMediaGeneratorOptions(
            new HttpClient(handler),
            new Uri("https://media.test/generate"))
        {
            PollInterval = TimeSpan.Zero,
        });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        public List<string> Bodies { get; } = new();

        public List<string?> Authorizations { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));
            Authorizations.Add(request.Headers.TryGetValues("Authorization", out var values)
                ? values.Single()
                : null);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return _handler(request);
        }
    }
}

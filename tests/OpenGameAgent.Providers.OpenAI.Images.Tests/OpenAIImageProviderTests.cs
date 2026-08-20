using System.Net;
using System.Text;
using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.OpenAI.Images;
using Xunit;

namespace OpenGameAgent.Providers.OpenAI.Images.Tests;

public sealed class OpenAIImageProviderTests
{
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task EditUsesMultipartImageArrayAndReturnsValidatedImage()
    {
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://images.test/v1/images/edits", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-secret", request.Headers.Authorization!.ToString());
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = multipart.ToArray();
            Assert.Equal(7, parts.Length);
            Assert.Equal(2, parts.Count(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "image[]"));
            Assert.All(
                parts.Where(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "image[]"),
                part => Assert.Equal("image/png", part.Headers.ContentType!.MediaType));
            Assert.Contains(parts, part => part.Headers.ContentDisposition!.Name!.Trim('"') == "model"
                                          && part.ReadAsStringAsync(cancellationToken).Result == "gpt-image-1");
            Assert.Contains(parts, part => part.Headers.ContentDisposition!.Name!.Trim('"') == "size"
                                          && part.ReadAsStringAsync(cancellationToken).Result == "1024x1536");
            await Task.Yield();
            return Json(HttpStatusCode.OK, $"{{\"created\":12,\"data\":[{{\"b64_json\":\"{Pixel}\"}}]}}", "openai-request");
        });
        using var registry = Registry(handler);
        var source = new ResourceContent("data:image/png;base64," + Pixel, "image/png");

        var result = await registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest(
                "edit",
                GameMediaKind.Image,
                "{}",
                "{\"size\":\"1024x1536\",\"output_format\":\"png\"}",
                "Private prompt",
                new[] { source, source }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Status == GameMediaModelGenerationStatus.Completed, result.ErrorMessage);
        Assert.Equal("openai-request", result.Result!.ProviderRequestId);
        Assert.Equal("data:image/png;base64," + Pixel, Assert.Single(result.Result.Outputs).Uri);
    }

    [Fact]
    public async Task GenerationUsesJsonAndRegistryAuthenticationBoundary()
    {
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://images.test/v1/images/generations", request.RequestUri!.AbsoluteUri);
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain("response_format", json, StringComparison.Ordinal);
            Assert.Contains("\"output_format\":\"png\"", json, StringComparison.Ordinal);
            Assert.Contains("\"prompt\":\"Landscape\"", json, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, $"{{\"data\":[{{\"b64_json\":\"{Pixel}\"}}]}}");
        });
        using var registry = Registry(handler);

        var result = await registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest("generate", GameMediaKind.Image, "{}", prompt: "Landscape"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Completed, result.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{\"size\":\"2048x1152\"}")]
    [InlineData("{\"model\":\"override\"}")]
    [InlineData("{\"n\":11}")]
    public async Task UnsupportedOrOutOfBoundsParametersFailBeforeNetwork(string parameters)
    {
        var handler = new RecordingHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)
            ((_, _) => throw new InvalidOperationException("must not dispatch")));
        using var registry = Registry(handler);

        var result = await registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest("bad", GameMediaKind.Image, "{}", parameters, "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ConfiguredOutputLimitFailsBeforeNetwork()
    {
        var handler = new RecordingHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)
            ((_, _) => throw new InvalidOperationException("must not dispatch")));
        var options = Options(handler);
        options.MaxOutputs = 1;
        using var registry = Registry(handler, options);

        var result = await registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest("bounded", GameMediaKind.Image, "{}", "{\"n\":2}", "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InvalidSourceMimeAndByteLimitsFailBeforeNetwork()
    {
        var handler = new RecordingHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)
            ((_, _) => throw new InvalidOperationException("must not dispatch")));
        var options = Options(handler);
        options.MaxReferenceBytes = 8;
        using var registry = Registry(handler, options);

        var result = await registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest(
                "bad-source",
                GameMediaKind.Image,
                "{}",
                prompt: "Prompt",
                sources: new[] { new ResourceContent("data:image/png;base64," + Pixel, "image/png") }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WrongContentTypeAndOversizedResponseFailClosed()
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("not json", Encoding.UTF8, "text/plain"),
                }
                : Json(HttpStatusCode.OK, "{\"data\":[{\"b64_json\":\"" + Pixel + "\"}]}");
        });
        var options = Options(handler);
        options.MaxResponseBytes = 32;
        using var registry = Registry(handler, options);

        var wrongType = await registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest("type", GameMediaKind.Image, "{}", prompt: "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);
        var tooLarge = await registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest("large", GameMediaKind.Image, "{}", prompt: "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, wrongType.Status);
        Assert.Contains("content type", wrongType.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GameMediaModelGenerationStatus.Failed, tooLarge.Status);
        Assert.Contains("byte limit", tooLarge.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ErrorsNeverExposeCredentialPromptOrReference()
    {
        var handler = new RecordingHandler((_, _) => Json(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"bad_request\",\"message\":\"test-secret Private prompt " + Pixel + "\"}}"));
        using var registry = Registry(handler);

        var result = await registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest("error", GameMediaKind.Image, "{}", prompt: "Private prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, result.Status);
        Assert.Contains("400", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("bad_request", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("test-secret", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Private prompt", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Pixel, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationReturnsCanceledAndConcurrentRequestsStayIsolated()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            var text = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (text.Contains("first", StringComparison.Ordinal))
            {
                firstStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            Assert.DoesNotContain(text.Contains("first", StringComparison.Ordinal) ? "second" : "first", text, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, $"{{\"data\":[{{\"b64_json\":\"{Pixel}\"}}]}}");
        });
        using var registry = Registry(handler);
        using var cancellation = new CancellationTokenSource();
        var first = registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest("one", GameMediaKind.Image, "{}", prompt: "first"),
            cancellationToken: cancellation.Token).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest("two", GameMediaKind.Image, "{}", prompt: "second"),
            cancellationToken: TestContext.Current.CancellationToken).AsTask();
        cancellation.Cancel();
        release.TrySetResult();

        Assert.Equal(GameMediaModelGenerationStatus.Canceled, (await first).Status);
        Assert.Equal(GameMediaModelGenerationStatus.Completed, (await second).Status);
    }

    [Fact]
    public async Task CancellationWhileReadingANonCooperativeStreamReturnsCanceled()
    {
        var stream = new BlockingReadStream();
        var handler = new RecordingHandler((_, _) =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var registry = Registry(handler);
        using var cancellation = new CancellationTokenSource();

        var generation = registry.GenerateAsync(
            OpenAIImageProvider.ProviderId,
            "gpt-image-1",
            new GameMediaGenerationRequest("cancel-read", GameMediaKind.Image, "{}", prompt: "Prompt"),
            cancellationToken: cancellation.Token).AsTask();
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Equal(GameMediaModelGenerationStatus.Canceled, (await generation).Status);
    }

    [Fact]
    public void RemoteHttpAndCredentialBearingRedirectsAreRejected()
    {
        var insecure = new OpenAIImageProviderOptions
        {
            Endpoint = new Uri("http://images.example/v1/images"),
            HttpMessageHandler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, "{}")),
        };
        Assert.Throws<ArgumentException>(() => OpenAIImageProvider.CreateRegistration(
            insecure,
            Authentication(),
            new[] { OpenAIImageProvider.CreateModel("gpt-image-1") }));
    }

    private static GameMediaModelRegistry Registry(
        RecordingHandler handler,
        OpenAIImageProviderOptions? options = null)
    {
        var registry = new GameMediaModelRegistry();
        registry.Register(OpenAIImageProvider.CreateRegistration(
            options ?? Options(handler),
            Authentication(),
            new[] { OpenAIImageProvider.CreateModel("gpt-image-1") }));
        return registry;
    }

    private static OpenAIImageProviderOptions Options(RecordingHandler handler) => new()
    {
        Endpoint = new Uri("https://images.test/v1/images"),
        HttpMessageHandler = handler,
    };

    private static IGameProviderAuthentication Authentication() =>
        new StaticGameProviderAuthentication(
            credential: new GameCredential(GameCredentialKind.ApiKey, "test-secret"));

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string? requestId = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation("x-request-id", requestId);
        }

        return response;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send)
        {
            _send = (request, cancellationToken) => Task.FromResult(send(request, cancellationToken));
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _send(request, cancellationToken);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _read =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            return _read.Task;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _read.TrySetException(new ObjectDisposedException(nameof(BlockingReadStream)));
            }

            base.Dispose(disposing);
        }
    }
}

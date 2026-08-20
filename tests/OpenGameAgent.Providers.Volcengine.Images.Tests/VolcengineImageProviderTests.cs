using System.Net;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.Volcengine.Images;
using Xunit;

namespace OpenGameAgent.Providers.Volcengine.Images.Tests;

public sealed class VolcengineImageProviderTests
{
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task SeedreamRequestUsesImageArrayExplicitLandscapeSizeAndNoWatermark()
    {
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://ark.test/api/v3/images/generations", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer ark-secret", request.Headers.Authorization!.ToString());
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var root = document.RootElement;
            Assert.Equal("seedream-model", root.GetProperty("model").GetString());
            Assert.Equal("2048x1152", root.GetProperty("size").GetString());
            Assert.Equal("b64_json", root.GetProperty("response_format").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.False(root.GetProperty("watermark").GetBoolean());
            var images = root.GetProperty("image").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Equal(2, images.Length);
            Assert.All(images, image => Assert.Equal("data:image/png;base64," + Pixel, image));
            return Json(HttpStatusCode.OK, $"{{\"data\":[{{\"b64_json\":\"{Pixel}\"}}]}}", "volc-request");
        });
        using var registry = Registry(handler);
        var source = new ResourceContent("data:image/png;base64," + Pixel, "image/png");

        var result = await registry.GenerateAsync(
            VolcengineImageProvider.ProviderId,
            "seedream-model",
            new GameMediaGenerationRequest(
                "seedream",
                GameMediaKind.Image,
                "{}",
                "{\"size\":\"2048x1152\",\"output_format\":\"png\"}",
                "Private prompt",
                new[] { source, source }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Completed, result.Status);
        Assert.Equal("volc-request", result.Result!.ProviderRequestId);
        Assert.Equal("data:image/png;base64," + Pixel, Assert.Single(result.Result.Outputs).Uri);
    }

    [Fact]
    public async Task WatermarkCanBeExplicitlyEnabledWithoutChangingProviderDefault()
    {
        var values = new List<bool>();
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            values.Add(document.RootElement.GetProperty("watermark").GetBoolean());
            return Json(HttpStatusCode.OK, $"{{\"data\":[{{\"b64_json\":\"{Pixel}\"}}]}}");
        });
        using var registry = Registry(handler);

        var first = await registry.GenerateAsync(
            VolcengineImageProvider.ProviderId,
            "seedream-model",
            new GameMediaGenerationRequest("default", GameMediaKind.Image, "{}", prompt: "First"),
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await registry.GenerateAsync(
            VolcengineImageProvider.ProviderId,
            "seedream-model",
            new GameMediaGenerationRequest(
                "override",
                GameMediaKind.Image,
                "{}",
                "{\"watermark\":true}",
                "Second"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Completed, first.Status);
        Assert.Equal(GameMediaModelGenerationStatus.Completed, second.Status);
        Assert.Equal(new[] { false, true }, values);
    }

    [Theory]
    [InlineData("{\"stream\":true}")]
    [InlineData("{\"size\":\"40000x1\"}")]
    [InlineData("{\"watermark\":\"false\"}")]
    public async Task ReservedInvalidAndOversizedParametersFailBeforeNetwork(string parameters)
    {
        var handler = new RecordingHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)
            ((_, _) => throw new InvalidOperationException("must not dispatch")));
        using var registry = Registry(handler);

        var result = await registry.GenerateAsync(
            VolcengineImageProvider.ProviderId,
            "seedream-model",
            new GameMediaGenerationRequest("bad", GameMediaKind.Image, "{}", parameters, "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MimeMismatchAndInvalidProviderBytesFailClosed()
    {
        var handler = new RecordingHandler((_, _) => Json(
            HttpStatusCode.OK,
            "{\"data\":[{\"b64_json\":\"bm90LWFuLWltYWdl\"}]}"));
        using var registry = Registry(handler);

        var result = await registry.GenerateAsync(
            VolcengineImageProvider.ProviderId,
            "seedream-model",
            new GameMediaGenerationRequest("bad-output", GameMediaKind.Image, "{}", prompt: "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, result.Status);
        Assert.Contains("image", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpJsonAndSecretEchoErrorsAreBoundedAndRedactedByConstruction()
    {
        var handler = new RecordingHandler((_, _) => Json(
            HttpStatusCode.TooManyRequests,
            "{\"error\":{\"code\":\"rate_limited\",\"message\":\"ark-secret Private prompt " + Pixel + "\"}}"));
        using var registry = Registry(handler);

        var result = await registry.GenerateAsync(
            VolcengineImageProvider.ProviderId,
            "seedream-model",
            new GameMediaGenerationRequest("error", GameMediaKind.Image, "{}", prompt: "Private prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, result.Status);
        Assert.Contains("429", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("rate_limited", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("ark-secret", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Private prompt", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Pixel, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationAndConcurrentPromptsRemainIsolated()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (body.Contains("blocked", StringComparison.Ordinal))
            {
                blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Assert.DoesNotContain("blocked", body, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, $"{{\"data\":[{{\"b64_json\":\"{Pixel}\"}}]}}");
        });
        using var registry = Registry(handler);
        using var cancellation = new CancellationTokenSource();
        var first = registry.GenerateAsync(
            VolcengineImageProvider.ProviderId,
            "seedream-model",
            new GameMediaGenerationRequest("blocked", GameMediaKind.Image, "{}", prompt: "blocked"),
            cancellationToken: cancellation.Token).AsTask();
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = registry.GenerateAsync(
            VolcengineImageProvider.ProviderId,
            "seedream-model",
            new GameMediaGenerationRequest("normal", GameMediaKind.Image, "{}", prompt: "normal"),
            cancellationToken: TestContext.Current.CancellationToken).AsTask();
        cancellation.Cancel();

        Assert.Equal(GameMediaModelGenerationStatus.Canceled, (await first).Status);
        Assert.Equal(GameMediaModelGenerationStatus.Completed, (await second).Status);
    }

    [Fact]
    public void RemoteHttpIsRejectedButExplicitLoopbackHttpIsSupported()
    {
        var remote = Options(new RecordingHandler((_, _) => Json(HttpStatusCode.OK, "{}")));
        remote.Endpoint = new Uri("http://ark.example/api/v3/images/generations");
        Assert.Throws<ArgumentException>(() => VolcengineImageProvider.CreateRegistration(
            remote,
            Authentication(),
            new[] { VolcengineImageProvider.CreateModel("seedream-model") }));

        remote.Endpoint = new Uri("http://127.0.0.1:8080/api/v3/images/generations");
        remote.AllowInsecureLoopbackHttp = true;
        var registration = VolcengineImageProvider.CreateRegistration(
            remote,
            Authentication(),
            new[] { VolcengineImageProvider.CreateModel("seedream-model") });
        Assert.Equal(VolcengineImageProvider.ProviderId, registration.Descriptor.ProviderId);
    }

    private static GameMediaModelRegistry Registry(RecordingHandler handler)
    {
        var registry = new GameMediaModelRegistry();
        registry.Register(VolcengineImageProvider.CreateRegistration(
            Options(handler),
            Authentication(),
            new[] { VolcengineImageProvider.CreateModel("seedream-model") }));
        return registry;
    }

    private static VolcengineImageProviderOptions Options(RecordingHandler handler) => new()
    {
        Endpoint = new Uri("https://ark.test/api/v3/images/generations"),
        HttpMessageHandler = handler,
    };

    private static IGameProviderAuthentication Authentication() =>
        new StaticGameProviderAuthentication(
            credential: new GameCredential(GameCredentialKind.ApiKey, "ark-secret"));

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string? requestId = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation("x-tt-logid", requestId);
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
}

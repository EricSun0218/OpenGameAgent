using System.Net;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using Xunit;

namespace OpenGameAgent.Providers.OpenRouter.Tests;

public sealed class OpenRouterImageProviderTests
{
    [Fact]
    public async Task RefreshDiscoversExecutableImageModelsThroughUnifiedAuthentication()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://openrouter.test/api/v1/images/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
            return Json(HttpStatusCode.OK, """
                {
                  "data": [
                    {
                      "id": "vendor/image-model",
                      "name": "Image Model",
                      "description": "Creates images",
                      "architecture": {
                        "input_modalities": ["text", "image"],
                        "output_modalities": ["image"]
                      },
                      "supported_parameters": { "quality": { "type": "enum" } },
                      "supports_streaming": true
                    },
                    {
                      "id": "vendor/text-model",
                      "architecture": {
                        "input_modalities": ["text"],
                        "output_modalities": ["text"]
                      }
                    }
                  ]
                }
                """);
        });
        using var client = new HttpClient(handler);
        var options = Options(client);
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(options, Authentication()));

        var refreshed = await registry.RefreshAsync(
            OpenRouterImageProvider.ProviderId,
            TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelRefreshStatus.Updated, refreshed.Status);
        var model = Assert.Single(registry.GetModels(OpenRouterImageProvider.ProviderId));
        Assert.Equal("vendor/image-model", model.ModelId);
        Assert.Equal(OpenRouterImageProvider.ApiId, model.Api);
        Assert.True(model.InputCapabilities.HasFlag(GameModelInputCapabilities.Text));
        Assert.True(model.InputCapabilities.HasFlag(GameModelInputCapabilities.Image));
        Assert.Equal(GameModelOutputCapabilities.Image, model.OutputCapabilities);
        Assert.Equal("true", model.Metadata["supportsStreaming"]);
    }

    [Fact]
    public async Task BufferedGenerationMapsParametersReferencesAuthenticationAndOutputs()
    {
        JsonDocument? captured = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://openrouter.test/api/v1/images", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
            Assert.Equal("game", request.Headers.GetValues("X-Game").Single());
            captured = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return Json(HttpStatusCode.OK, """
                {
                  "created": 123,
                  "data": [
                    { "b64_json": "aW1hZ2U=", "media_type": "image/webp" }
                  ],
                  "usage": { "prompt_tokens": 5, "completion_tokens": 7, "cost": 0.01 }
                }
                """, requestId: "request-1");
        });
        using var client = new HttpClient(handler);
        var options = Options(client);
        options.Headers["X-Game"] = "game";
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(
            options,
            Authentication(),
            new[] { ImageModel() }));
        var reference = "data:image/png;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("reference"));
        var request = new GameMediaGenerationRequest(
            "request",
            GameMediaKind.Image,
            "{\"privateContext\":\"must-not-leave-the-game\"}",
            "{\"n\":1,\"aspect_ratio\":\"16:9\",\"quality\":\"high\"}",
            "A mountain village",
            new[] { new ResourceContent(reference, "image/png") });

        var generated = await registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            request,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Completed, generated.Status);
        var result = Assert.IsType<GameMediaGenerationResult>(generated.Result);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("image/webp", output.MediaType);
        Assert.Equal("data:image/webp;base64,aW1hZ2U=", output.Uri);
        Assert.Equal("request-1", result.ProviderRequestId);
        Assert.Contains("\"cost\":0.01", result.MetadataJson, StringComparison.Ordinal);
        var root = captured!.RootElement;
        Assert.Equal("vendor/image-model", root.GetProperty("model").GetString());
        Assert.Equal("A mountain village", root.GetProperty("prompt").GetString());
        Assert.Equal("16:9", root.GetProperty("aspect_ratio").GetString());
        Assert.Equal(reference, root.GetProperty("input_references")[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.DoesNotContain("must-not-leave-the-game", root.GetRawText(), StringComparison.Ordinal);
        captured.Dispose();
    }

    [Fact]
    public async Task StreamingGenerationReportsPartialProgressAndReturnsCompletedImage()
    {
        var body = string.Join("\n\n", new[]
        {
            "data: {\"type\":\"image_generation.partial_image\",\"partial_image_index\":2,\"b64_json\":\"cGFydGlhbA==\"}",
            "data: {\"type\":\"image_generation.completed\",\"b64_json\":\"ZmluYWw=\",\"media_type\":\"image/png\",\"created\":456,\"usage\":{\"cost\":0.02}}",
            "data: [DONE]",
            string.Empty,
        });
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
        using var client = new HttpClient(handler);
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(
            Options(client),
            Authentication(),
            new[] { ImageModel() }));
        var progress = new List<GameMediaGenerationProgress>();

        var generated = await registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            new GameMediaGenerationRequest(
                "stream",
                GameMediaKind.Image,
                "{}",
                "{\"stream\":true}",
                "A castle"),
            (update, _) =>
            {
                progress.Add(update);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Completed, generated.Status);
        Assert.Equal("data:image/png;base64,ZmluYWw=", Assert.Single(generated.Result!.Outputs).Uri);
        var update = Assert.Single(progress);
        Assert.Equal("partial_image", update.Stage);
        Assert.Contains("\"index\":2", update.DetailsJson, StringComparison.Ordinal);
        Assert.Equal("data:image/png;base64,cGFydGlhbA==", update.Preview!.Uri);
        Assert.Contains("\"created\":456", generated.Result.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationHeaderTombstonesSuppressConfiguredDefaultsAndCredential()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("X-Game"));
            return request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, """
                    {"data":[{"id":"vendor/image-model","architecture":{"input_modalities":["text"],"output_modalities":["image"]}}]}
                    """)
                : Json(HttpStatusCode.OK, "{\"data\":[{\"b64_json\":\"aW1hZ2U=\"}]}");
        });
        using var client = new HttpClient(handler);
        var options = Options(client);
        options.Headers["Authorization"] = "Bearer configured";
        options.Headers["X-Game"] = "configured";
        var authentication = new ResolutionAuthentication(new GameProviderAuthResolution(
            new GameCredential(GameCredentialKind.ApiKey, "secret"),
            "test",
            headers: new Dictionary<string, string?>
            {
                ["Authorization"] = null,
                ["X-Game"] = null,
            }));
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(
            options,
            authentication,
            new[] { ImageModel() }));

        var refreshed = await registry.RefreshAsync(
            OpenRouterImageProvider.ProviderId,
            TestContext.Current.CancellationToken);
        var generated = await registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            new GameMediaGenerationRequest("headers", GameMediaKind.Image, "{}", prompt: "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelRefreshStatus.Updated, refreshed.Status);
        Assert.Equal(GameMediaModelGenerationStatus.Completed, generated.Status);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ReservedParametersAndInvalidReferencesFailBeforeNetworkDispatch()
    {
        var handler = new RecordingHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)
            ((_, _) => throw new InvalidOperationException("must not dispatch")));
        using var client = new HttpClient(handler);
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(
            Options(client),
            Authentication(),
            new[] { ImageModel() }));

        var reserved = await registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            new GameMediaGenerationRequest(
                "reserved",
                GameMediaKind.Image,
                "{}",
                "{\"model\":\"override\"}",
                "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);
        var invalidReference = await registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            new GameMediaGenerationRequest(
                "reference",
                GameMediaKind.Image,
                "{}",
                prompt: "Prompt",
                sources: new[] { new ResourceContent("file:///private/image.png", "image/png") }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, reserved.Status);
        Assert.Contains("reserved", reserved.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GameMediaModelGenerationStatus.Failed, invalidReference.Status);
        Assert.Contains("HTTP(S)", invalidReference.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HttpFailuresAreBoundedAndNeverExposeTheCredential()
    {
        var handler = new RecordingHandler((_, _) => Json(
            HttpStatusCode.TooManyRequests,
            "{\"error\":{\"code\":\"rate_limited\",\"message\":\"secret prompt private-reference\"}}"));
        using var client = new HttpClient(handler);
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(
            Options(client),
            Authentication(),
            new[] { ImageModel() }));

        var generated = await registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            new GameMediaGenerationRequest("error", GameMediaKind.Image, "{}", prompt: "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, generated.Status);
        Assert.Contains("429", generated.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("rate_limited", generated.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", generated.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-reference", generated.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingErrorsNeverExposeProviderEchoedSecrets()
    {
        var body = string.Join("\n\n", new[]
        {
            "data: {\"type\":\"error\",\"error\":{\"code\":\"policy_blocked\",\"message\":\"secret prompt\"}}",
            "data: [DONE]",
            string.Empty,
        });
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
        using var client = new HttpClient(handler);
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(
            Options(client),
            Authentication(),
            new[] { ImageModel() }));

        var generated = await registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            new GameMediaGenerationRequest(
                "stream-error",
                GameMediaKind.Image,
                "{}",
                "{\"stream\":true}",
                "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, generated.Status);
        Assert.Contains("policy_blocked", generated.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", generated.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", generated.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TruncatedStreamingResponsesFailInBandAfterPreservingTheirSafetyBoundary()
    {
        var body = "data: {\"type\":\"image_generation.completed\",\"b64_json\":\"ZmluYWw=\"}\n\n";
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
        using var client = new HttpClient(handler);
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(
            Options(client),
            Authentication(),
            new[] { ImageModel() }));

        var generated = await registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            new GameMediaGenerationRequest(
                "truncated",
                GameMediaKind.Image,
                "{}",
                "{\"stream\":true}",
                "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, generated.Status);
        Assert.Contains("terminal marker", generated.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidBase64OutputsFailClosed()
    {
        var handler = new RecordingHandler((_, _) => Json(
            HttpStatusCode.OK,
            "{\"data\":[{\"b64_json\":\"not base64\",\"media_type\":\"image/png\"}]}"));
        using var client = new HttpClient(handler);
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(
            Options(client),
            Authentication(),
            new[] { ImageModel() }));

        var generated = await registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            new GameMediaGenerationRequest("invalid", GameMediaKind.Image, "{}", prompt: "Prompt"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Failed, generated.Status);
        Assert.Contains("base64", generated.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationDuringResponseStreamAcquisitionReturnsPromptlyAndDisposesLateStream()
    {
        var content = new BlockingStreamContent();
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var client = new HttpClient(handler);
        using var registry = new GameMediaModelRegistry();
        registry.Register(OpenRouterImageProvider.CreateRegistration(
            Options(client),
            Authentication(),
            new[] { ImageModel() }));
        using var cancellation = new CancellationTokenSource();

        var generation = registry.GenerateAsync(
            OpenRouterImageProvider.ProviderId,
            "vendor/image-model",
            new GameMediaGenerationRequest("cancel", GameMediaKind.Image, "{}", prompt: "Prompt"),
            cancellationToken: cancellation.Token).AsTask();
        await content.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var result = await generation.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(GameMediaModelGenerationStatus.Canceled, result.Status);

        var late = new TrackingStream(Encoding.UTF8.GetBytes("{\"data\":[]}"));
        content.Release(late);
        await late.Disposed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
    }

    private static OpenRouterImageProviderOptions Options(HttpClient client) => new(client)
    {
        Endpoint = new Uri("https://openrouter.test/api/v1/images"),
    };

    private static IGameProviderAuthentication Authentication() =>
        new StaticGameProviderAuthentication(
            credential: new GameCredential(GameCredentialKind.ApiKey, "secret"));

    private static GameModelDescriptor ImageModel() => new(
        OpenRouterImageProvider.ProviderId,
        "vendor/image-model",
        inputCapabilities: GameModelInputCapabilities.Text | GameModelInputCapabilities.Image,
        outputCapabilities: GameModelOutputCapabilities.Image,
        api: OpenRouterImageProvider.ApiId,
        baseUrl: new Uri("https://openrouter.test/api/v1/images"));

    private static HttpResponseMessage Json(HttpStatusCode status, string content, string? requestId = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation("x-request-id", requestId);
        }

        return response;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, ValueTask<HttpResponseMessage>> _send;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send)
        {
            _send = (request, cancellationToken) => new ValueTask<HttpResponseMessage>(send(request, cancellationToken));
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = async (request, cancellationToken) => await send(request, cancellationToken);
        }

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return await _send(request, cancellationToken);
        }
    }

    private sealed class ResolutionAuthentication : IGameProviderAuthentication
    {
        private readonly GameProviderAuthResolution _resolution;

        public ResolutionAuthentication(GameProviderAuthResolution resolution)
        {
            _resolution = resolution;
        }

        public IReadOnlyCollection<string> Schemes { get; } = Array.Empty<string>();

        public ValueTask<GameProviderAuthStatus> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameProviderAuthStatus>(new GameProviderAuthStatus(
                true,
                "test",
                _resolution.Credential?.Kind));
        }

        public ValueTask<GameProviderAuthResolution?> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<GameProviderAuthResolution?>(_resolution);
        }

        public ValueTask<GameCredential> LoginAsync(
            string scheme,
            GameAuthInteraction interaction,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Test authentication does not support login.");

        public ValueTask LogoutAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Test authentication does not support logout.");
    }

    private sealed class BlockingStreamContent : HttpContent
    {
        private readonly TaskCompletionSource<Stream> _stream =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(Stream stream) => _stream.TrySetResult(stream);

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            Started.TrySetResult();
            return _stream.Task;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] bytes)
            : base(bytes)
        {
        }

        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Disposed.TrySetResult();
            }
        }
    }
}

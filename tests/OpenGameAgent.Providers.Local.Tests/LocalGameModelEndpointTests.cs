using System.Net;
using System.Text;
using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.OpenAI.Realtime;
using Xunit;

namespace OpenGameAgent.Providers.Local.Tests;

public sealed class LocalGameModelEndpointTests
{
    [Fact]
    public async Task LocalAiDiscoveryPublishesExactCapabilitiesAndUnknownCost()
    {
        var handler = new RecordingHandler(_ => Json("""
            {
              "object":"list",
              "data":[
                {
                  "id":"qwen-local",
                  "capabilities":["chat","vision","tools","thinking"],
                  "input_modalities":["text","image"],
                  "output_modalities":["text"]
                },
                {"id":"whisper-local","capabilities":["transcript"]}
              ]
            }
            """));
        var endpoint = new LocalGameModelEndpoint(
            LocalGameModelPresets.LocalAi(new HttpClient(handler)));
        var catalog = new GameModelCatalog();
        catalog.Register(endpoint.CreateRegistration());

        var refresh = await catalog.RefreshAsync(
            "localai",
            allowNetwork: true,
            force: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(GameModelRefreshStatus.Updated, refresh.Status);
        var model = Assert.Single(catalog.GetModels("localai"));
        Assert.Equal("qwen-local", model.ModelId);
        Assert.True(model.InputCapabilities.HasFlag(GameModelInputCapabilities.Image));
        Assert.True(model.OutputCapabilities.HasFlag(GameModelOutputCapabilities.ToolCalls));
        Assert.True(model.OutputCapabilities.HasFlag(GameModelOutputCapabilities.Reasoning));
        Assert.False(model.Cost.IsKnown);
        Assert.Equal("/v1/models/capabilities", Assert.Single(handler.Requests).AbsolutePath);
    }

    [Fact]
    public async Task OllamaUsesNativeInventoryAndStreamsThroughExistingAgentProtocolWithoutAKey()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => Json("{\"models\":[{\"name\":\"qwen2.5:latest\"}]}"),
            "/v1/chat/completions" => EventStream("""
                data: {"choices":[{"delta":{"role":"assistant"},"finish_reason":null}]}

                data: {"choices":[{"delta":{"content":"hello locally"},"finish_reason":null}]}

                data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var options = LocalGameModelPresets.Ollama(new HttpClient(handler));
        options.OutputCapabilityOverrides = new Dictionary<string, GameModelOutputCapabilities>
        {
            ["qwen2.5:latest"] = GameModelOutputCapabilities.Text | GameModelOutputCapabilities.ToolCalls,
        };
        var endpoint = new LocalGameModelEndpoint(options);
        var catalog = new GameModelCatalog();
        catalog.Register(endpoint.CreateRegistration());
        await catalog.RefreshAsync(
            "ollama",
            allowNetwork: true,
            force: true,
            TestContext.Current.CancellationToken);

        var events = new List<ModelStreamEvent>();
        await foreach (var item in catalog.CreateProvider("ollama").StreamAsync(
                           Request("qwen2.5:latest"),
                           TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        Assert.Equal("hello locally", Assert.IsType<TextContent>(events.Last().Response!.Content.Single()).Text);
        Assert.Equal(new[] { "/api/tags", "/v1/chat/completions" }, handler.Requests.Select(value => value.AbsolutePath));
        Assert.All(handler.Authorizations, value => Assert.Null(value));
    }

    [Fact]
    public async Task ProbeIsBoundedAndReturnsStructuredSafeFailure()
    {
        var handler = new RecordingHandler(_ => Json("{\"data\":[{\"id\":\"model\"}]}"));
        var options = LocalGameModelPresets.LmStudio(new HttpClient(handler));
        options.MaximumResponseBytes = 4;
        var endpoint = new LocalGameModelEndpoint(options);

        var result = await endpoint.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocalGameEndpointHealth.InvalidResponse, result.Health);
        Assert.Equal("invalid-response", result.ErrorCategory);
        Assert.Empty(result.Models);
    }

    [Fact]
    public void RemoteAndAnonymousEndpointPoliciesFailClosed()
    {
        Assert.Throws<ArgumentException>(() => new LocalGameModelEndpoint(
            new LocalGameModelEndpointOptions(
                new HttpClient(new RecordingHandler(_ => Json("{}"))),
                new Uri("https://models.example/v1"))));
        Assert.Throws<ArgumentException>(() => new LocalGameModelEndpoint(
            new LocalGameModelEndpointOptions(
                new HttpClient(new RecordingHandler(_ => Json("{}"))),
                new Uri("http://models.example/v1"))
            {
                AllowRemoteEndpoint = true,
            }));
    }

    [Fact]
    public void RealtimePresetsAreAnonymousOnlyOnKnownLoopbackEndpoints()
    {
        var localAi = LocalRealtimePresets.LocalAi();
        var speaches = LocalRealtimePresets.Speaches();

        Assert.True(localAi.AllowAnonymousLoopback);
        Assert.Equal(new Uri("ws://127.0.0.1:8080/v1/realtime"), localAi.Endpoint);
        Assert.True(speaches.AllowAnonymousLoopback);
        Assert.Equal(new Uri("ws://127.0.0.1:8000/v1/realtime"), speaches.Endpoint);
        Assert.Throws<ArgumentException>(() =>
            new OpenAIRealtimeTransport(
                LocalRealtimePresets.Speaches(new Uri("wss://speech.example/v1/realtime"))));
    }

    [Fact]
    public async Task LocalAiMediaGeneratesImageVideoAndSpeechThroughTheSharedRegistry()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var mp4 = new byte[] { 0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' };
        var wav = new byte[]
        {
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E',
        };
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/images/generations" => Json("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}]}"),
            "/video" => Json("{\"id\":\"video-request\",\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(mp4) + "\"}]}"),
            "/v1/audio/speech" => Binary(wav, "audio/x-wav"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var registration = LocalAiMediaProvider.CreateRegistration(
            new LocalAiMediaProviderOptions { HttpMessageHandler = handler },
            new StaticGameProviderAuthentication(),
            new[]
            {
                LocalAiMediaProvider.CreateImageModel("image-model"),
                LocalAiMediaProvider.CreateVideoModel("video-model"),
                LocalAiMediaProvider.CreateSpeechModel("speech-model"),
            });
        using var registry = new GameMediaModelRegistry();
        registry.Register(registration);

        var image = await registry.GenerateAsync(
            "localai",
            "image-model",
            new GameMediaGenerationRequest("image", GameMediaKind.Image, "{}", "{\"size\":\"256x256\"}", "draw"),
            cancellationToken: TestContext.Current.CancellationToken);
        var video = await registry.GenerateAsync(
            "localai",
            "video-model",
            new GameMediaGenerationRequest("video", GameMediaKind.Video, "{}", "{\"width\":512,\"height\":512}", "animate"),
            cancellationToken: TestContext.Current.CancellationToken);
        var speech = await registry.GenerateAsync(
            "localai",
            "speech-model",
            new GameMediaGenerationRequest("speech", GameMediaKind.Audio, "{}", "{\"voice\":\"alloy\"}", "hello"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Completed, image.Status);
        Assert.Equal("image/png", image.Result!.Outputs.Single().MediaType);
        Assert.Equal(GameMediaModelGenerationStatus.Completed, video.Status);
        Assert.Equal("video/mp4", video.Result!.Outputs.Single().MediaType);
        Assert.Equal(GameMediaModelGenerationStatus.Completed, speech.Status);
        Assert.Equal("audio/wav", speech.Result!.Outputs.Single().MediaType);
        Assert.Contains(handler.Bodies, value => value.Contains("\"response_format\":\"b64_json\"", StringComparison.Ordinal));
        Assert.All(handler.Authorizations, value => Assert.Null(value));
    }

    [Fact]
    public async Task ComfyUiUsesTrustedWorkflowFactoryAndReturnsBoundedMedia()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/upload/image" => Json("{\"name\":\"source-1.png\",\"subfolder\":\"\",\"type\":\"input\"}"),
            "/api/prompt" => Json("{\"prompt_id\":\"job-1\"}"),
            "/api/jobs/job-1" => Json("""
                {
                  "id": "job-1",
                  "status": "completed",
                  "outputs": {
                    "9": {
                      "images": [
                        { "filename": "result.png", "subfolder": "", "type": "output" }
                      ]
                    }
                  }
                }
                """),
            "/api/view" => Binary(png, "image/png"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var factoryCalls = 0;
        var registration = ComfyUiMediaProvider.CreateRegistration(
            new ComfyUiMediaProviderOptions((context, _) =>
            {
                factoryCalls++;
                Assert.Equal("draw safely", context.Request.Prompt);
                Assert.Equal("source-1.png", Assert.Single(context.Sources).FileName);
                return ValueTask.FromResult(new ComfyUiWorkflowDefinition("""
                    {
                      "1": {
                        "class_type": "LoadImage",
                        "inputs": { "image": "source-1.png" }
                      }
                    }
                    """, "oga-client"));
            })
            {
                HttpMessageHandler = handler,
            },
            new StaticGameProviderAuthentication(),
            new[]
            {
                ComfyUiMediaProvider.CreateModel(
                    "trusted-image-workflow",
                    GameModelOutputCapabilities.Image),
            });
        using var registry = new GameMediaModelRegistry();
        registry.Register(registration);
        var source = new ResourceContent(
            "data:image/png;base64," + Convert.ToBase64String(png),
            "image/png",
            "reference.png");

        var result = await registry.GenerateAsync(
            "comfyui",
            "trusted-image-workflow",
            new GameMediaGenerationRequest(
                "image",
                GameMediaKind.Image,
                "{}",
                "{}",
                "draw safely",
                new[] { source }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GameMediaModelGenerationStatus.Completed, result.Status);
        Assert.Equal("job-1", result.Result!.ProviderRequestId);
        Assert.Equal("image/png", Assert.Single(result.Result.Outputs).MediaType);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(
            new[] { "/api/upload/image", "/api/prompt", "/api/jobs/job-1", "/api/view" },
            handler.Requests.Select(value => value.AbsolutePath));
        Assert.Contains(handler.Bodies, value => value.Contains("\"class_type\":\"LoadImage\"", StringComparison.Ordinal));
        Assert.All(handler.Authorizations, value => Assert.Null(value));
    }

    [Fact]
    public async Task ComfyUiCancellationTargetsOnlyTheSubmittedJob()
    {
        var handler = new CancelAwareComfyHandler();
        var registration = ComfyUiMediaProvider.CreateRegistration(
            new ComfyUiMediaProviderOptions((_, _) =>
                ValueTask.FromResult(new ComfyUiWorkflowDefinition("{}")))
            {
                HttpMessageHandler = handler,
            },
            new StaticGameProviderAuthentication(),
            new[]
            {
                ComfyUiMediaProvider.CreateModel("workflow", GameModelOutputCapabilities.Image),
            });
        using var registry = new GameMediaModelRegistry();
        registry.Register(registration);
        using var cancellation = new CancellationTokenSource();
        var generation = registry.GenerateAsync(
            "comfyui",
            "workflow",
            new GameMediaGenerationRequest("image", GameMediaKind.Image, "{}", prompt: "draw"),
            cancellationToken: cancellation.Token).AsTask();

        await handler.PollStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await generation;

        Assert.Equal(GameMediaModelGenerationStatus.Canceled, result.Status);
        Assert.True(await handler.TargetedCancel.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(handler.Paths, path => path == "/api/interrupt");
    }

    [Fact]
    public async Task LocalEmbeddingProviderPreservesBatchOrderPrefixesAndStableIdentity()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/embeddings" => Json("""
                {
                  "data": [
                    { "index": 1, "embedding": [0.0, 1.0] },
                    { "index": 0, "embedding": [1.0, 0.0] }
                  ]
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        await using var embeddings = new LocalOpenAIEmbeddingProvider(
            new LocalOpenAIEmbeddingProviderOptions(
                new HttpClient(handler),
                new Uri("http://127.0.0.1:8080/v1"),
                "localai",
                "bge-m3",
                "weights-v1",
                dimensions: 2)
            {
                DocumentPrefix = "passage: ",
            });

        var result = await embeddings.EmbedDocumentsAsync(
            new[] { "first", "second" },
            TestContext.Current.CancellationToken);

        Assert.Equal("localai/bge-m3@weights-v1:2", embeddings.Identity.ToString());
        Assert.Equal(new float[] { 1, 0 }, result[0].ToArray());
        Assert.Equal(new float[] { 0, 1 }, result[1].ToArray());
        Assert.Equal("/v1/embeddings", Assert.Single(handler.Requests).AbsolutePath);
        Assert.Contains(
            handler.Bodies,
            body => body.Contains("\"input\":[\"passage: first\",\"passage: second\"]", StringComparison.Ordinal));
        Assert.All(handler.Authorizations, value => Assert.Null(value));
    }

    private static ModelRequest Request(string model) => new(
        model,
        "rules",
        new[] { AgentMessage.User("hello") },
        Array.Empty<ToolDefinition>(),
        new ModelParameters(),
        "session",
        "run",
        1);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage EventStream(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Binary(byte[] body, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) },
        },
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<Uri> Requests { get; } = new();

        public List<string?> Authorizations { get; } = new();

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var response = _respond(request);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class CancelAwareComfyHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> PollStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> TargetedCancel { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Paths { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            HttpResponseMessage response;
            if (path == "/api/prompt")
            {
                response = Json("{\"prompt_id\":\"job-cancel\"}");
            }
            else if (path == "/api/jobs/job-cancel")
            {
                PollStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The canceled poll unexpectedly resumed.");
            }
            else if (path == "/api/jobs/job-cancel/cancel")
            {
                TargetedCancel.TrySetResult(true);
                response = Json("{\"cancelled\":true}");
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            response.RequestMessage = request;
            return response;
        }
    }
}

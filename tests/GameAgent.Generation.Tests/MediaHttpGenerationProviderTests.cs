using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GameAgent.Providers.MediaHttp;
using Xunit;

namespace GameAgent.Generation.Tests;

public sealed class MediaHttpGenerationProviderTests
{
    [Fact]
    public async Task Structured_poll_uses_structured_paths_and_does_not_invent_video_artifact()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            "{\"id\":\"structured-1\",\"status\":\"succeeded\",\"output\":{\"scene\":1}}"));
        using var provider = Provider(
            handler,
            options =>
            {
                options.StructuredContentPath = "/v1/content";
                options.StructuredContentStatusPathTemplate = "/v1/content/{id}";
                options.StructuredContentContentPathTemplate = null;
            });

        var result = await provider.GetAsync(
            "structured-1",
            GenerationModalities.StructuredContent,
            CancellationToken.None);

        Assert.Equal("/v1/content/structured-1", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Empty(result.Artifacts);
        Assert.Equal(1, result.Output!.Value.GetProperty("scene").GetInt32());
    }

    [Fact]
    public async Task Video_poll_attaches_modality_specific_content_endpoint()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            "{\"id\":\"video one\",\"status\":\"completed\",\"media_type\":\"video/mp4\"}"));
        using var provider = Provider(handler);

        var result = await provider.GetAsync(
            "video one",
            GenerationModalities.Video,
            CancellationToken.None);

        Assert.Equal("/v1/videos/video%20one", handler.LastRequest!.RequestUri!.AbsolutePath);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(
            "https://media.example/v1/videos/video%20one/content",
            artifact.RemoteUri!.AbsoluteUri);
        Assert.Equal("video/mp4", artifact.MediaType);
    }

    [Fact]
    public async Task Structured_cancel_uses_its_own_cancel_template()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        using var provider = Provider(
            handler,
            options =>
            {
                options.StructuredContentPath = "/v1/content";
                options.StructuredContentStatusPathTemplate = "/v1/content/{id}";
                options.StructuredContentCancelPathTemplate = "/v1/content/{id}/cancel";
            });

        var result = await provider.CancelAsync(
            "content-1",
            GenerationModalities.StructuredContent,
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("/v1/content/content-1/cancel", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Speech_submission_accepts_bounded_binary_response()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            return response;
        });
        using var provider = Provider(handler);

        var submission = await provider.SubmitAsync(
            Request(GenerationModalities.Speech),
            CancellationToken.None);

        Assert.Equal(GenerationJobStatuses.Succeeded, submission.Result.Status);
        Assert.Equal(4, Assert.Single(submission.Result.Artifacts).InlineData.Length);
    }

    [Fact]
    public async Task Inline_image_bytes_are_checkpointed_as_artifacts_not_duplicated_in_output()
    {
        var encoded = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var handler = new RecordingHandler(_ => JsonResponse(
            "{\"created\":1,\"data\":[{\"b64_json\":\"" + encoded
            + "\",\"revised_prompt\":\"tree\"}]}"));
        using var provider = Provider(handler);

        var submission = await provider.SubmitAsync(
            Request(GenerationModalities.Image),
            CancellationToken.None);

        Assert.Equal(4, Assert.Single(submission.Result.Artifacts).InlineData.Length);
        var output = submission.Result.Output!.Value.GetRawText();
        Assert.DoesNotContain("b64_json", output, StringComparison.Ordinal);
        Assert.Contains("revised_prompt", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_is_rejected_without_following_it()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://other.example/result") }
        });
        using var provider = Provider(handler);

        var exception = await Assert.ThrowsAsync<GenerationProviderException>(
            async () => await provider.SubmitAsync(
                Request(GenerationModalities.Image),
                CancellationToken.None));

        Assert.Equal("generation_redirect_rejected", exception.ReasonCode);
        Assert.Equal(GenerationAcceptance.NotAccepted, exception.Acceptance);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void Plain_http_is_allowed_only_for_explicit_loopback()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        Assert.Throws<ArgumentException>(() => Provider(
            handler,
            options => options.BaseUri = new Uri("http://localhost:8080/")));

        using var accepted = Provider(
            handler,
            options =>
            {
                options.BaseUri = new Uri("http://127.0.0.1:8080/");
                options.AllowInsecureLoopback = true;
            });
        Assert.Contains(GenerationModalities.Image, accepted.Capabilities.Modalities);
    }

    private static MediaHttpGenerationProvider Provider(
        HttpMessageHandler handler,
        Action<MediaHttpProviderOptions>? configure = null)
    {
        var options = new MediaHttpProviderOptions
        {
            BaseUri = new Uri("https://media.example/")
        };
        configure?.Invoke(options);
        return new MediaHttpGenerationProvider(
            options,
            httpClient: new HttpClient(handler, disposeHandler: false));
    }

    private static GenerationRequest Request(string modality) => new()
    {
        OperationId = "operation-1",
        Modality = modality,
        Input = Json("{\"prompt\":\"hello\"}"),
        IdempotencyKey = "operation-1"
    };

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        public int CallCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(_response(request));
        }
    }
}

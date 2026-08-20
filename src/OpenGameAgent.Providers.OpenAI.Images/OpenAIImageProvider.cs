using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.Images.Internal;

namespace OpenGameAgent.Providers.OpenAI.Images;

public sealed class OpenAIImageProviderOptions
{
    public Uri Endpoint { get; set; } = new("https://api.openai.com/v1/images");

    public HttpMessageHandler? HttpMessageHandler { get; set; }

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool AllowInsecureLoopbackHttp { get; set; }

    public int MaxReferences { get; set; } = 16;

    public int MaxReferenceBytes { get; set; } = 20_000_000;

    public int MaxAggregateReferenceBytes { get; set; } = 50_000_000;

    public int MaxRequestBytes { get; set; } = 80_000_000;

    public int MaxResponseBytes { get; set; } = 100_000_000;

    public int MaxOutputBytes { get; set; } = 30_000_000;

    public int MaxOutputs { get; set; } = 10;

    public int MaxPromptBytes { get; set; } = 1_000_000;

    public long MaxPixels { get; set; } = 67_108_864;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}

public static class OpenAIImageProvider
{
    public const string ProviderId = "openai";
    public const string ApiId = "openai-images";

    public static GameMediaProviderRegistration CreateRegistration(
        OpenAIImageProviderOptions options,
        IGameProviderAuthentication authentication,
        IReadOnlyList<GameModelDescriptor> models)
    {
        if (authentication is null)
        {
            throw new ArgumentNullException(nameof(authentication));
        }

        var settings = Settings.Create(options);
        var checkedModels = ValidateModels(models);
        return new GameMediaProviderRegistration(
            new GameProviderDescriptor(ProviderId, "OpenAI Images", settings.Endpoint),
            authentication,
            invocation => new Generator(settings, invocation),
            checkedModels);
    }

    public static GameModelDescriptor CreateModel(
        string modelId,
        string? displayName = null,
        Uri? endpoint = null) =>
        new(
            ProviderId,
            modelId,
            displayName,
            inputCapabilities: GameModelInputCapabilities.Text | GameModelInputCapabilities.Image,
            outputCapabilities: GameModelOutputCapabilities.Image,
            api: ApiId,
            baseUrl: endpoint);

    private static IReadOnlyList<GameModelDescriptor> ValidateModels(IReadOnlyList<GameModelDescriptor> models)
    {
        if (models is null || models.Count == 0)
        {
            throw new ArgumentException("At least one OpenAI image model is required.", nameof(models));
        }

        if (models.Any(model => model is null
                                || !string.Equals(model.ProviderId, ProviderId, StringComparison.Ordinal)
                                || !string.Equals(model.Api, ApiId, StringComparison.Ordinal)
                                || !model.OutputCapabilities.HasFlag(GameModelOutputCapabilities.Image)))
        {
            throw new ArgumentException("Every model must be an OpenAI image model.", nameof(models));
        }

        return Array.AsReadOnly(models.ToArray());
    }

    private sealed class Generator : IGameMediaGenerator
    {
        private readonly Settings _settings;
        private readonly GameMediaGenerationInvocation _invocation;

        public Generator(Settings settings, GameMediaGenerationInvocation invocation)
        {
            _settings = settings;
            _invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
            if (!string.Equals(invocation.Model.ProviderId, ProviderId, StringComparison.Ordinal)
                || !string.Equals(invocation.Model.Api, ApiId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The selected model is not an OpenAI image model.");
            }
        }

        public async ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            if (request.Kind != GameMediaKind.Image)
            {
                throw new InvalidOperationException("This provider only supports image generation.");
            }

            var prompt = ImageProviderCommon.RequirePrompt(request.Prompt, _settings.Limits.MaxPromptBytes);
            var sources = ImageProviderCommon.DecodeSources(request.Sources, _settings.Limits);
            var parameters = Parameters.Parse(
                request.ParametersJson,
                sources.Count > 0,
                _settings.Limits.MaxPixels,
                _settings.Limits.MaxOutputs);
            var endpoint = AppendOperation(
                ImageProviderCommon.ResolveEndpoint(
                    _invocation.Endpoint ?? _settings.Endpoint,
                    _invocation.Authentication,
                    _settings.AllowInsecureLoopbackHttp),
                sources.Count > 0 ? "edits" : "generations");
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            ImageProviderCommon.ApplyHeaders(
                message,
                _settings.Headers,
                _invocation.Headers,
                _invocation.Authentication);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Content = sources.Count == 0
                ? BuildJsonContent(prompt, _invocation.Model.ModelId, parameters)
                : BuildMultipartContent(prompt, _invocation.Model.ModelId, parameters, sources);
            ImageProviderCommon.EnsureRequestSize(message, _settings.Limits.MaxRequestBytes);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_settings.Limits.Timeout);
            using var response = await _settings.HttpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            ImageProviderCommon.ValidateResponseOrigin(response, endpoint);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                throw new InvalidDataException("The image provider refused a redirect response.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await ImageProviderCommon.CreateResponseExceptionAsync(
                    response,
                    _settings.Limits.MaxResponseBytes,
                    timeout.Token).ConfigureAwait(false);
            }

            using var document = await ImageProviderCommon.ReadJsonAsync(
                response,
                _settings.Limits.MaxResponseBytes,
                timeout.Token).ConfigureAwait(false);
            return ImageProviderCommon.ParseResult(
                document,
                ImageProviderCommon.OutputMediaType(parameters.OutputFormat),
                _settings.Limits,
                ImageProviderCommon.RequestId(response));
        }

        private static HttpContent BuildJsonContent(string prompt, string model, Parameters parameters)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                model,
                prompt,
                n = parameters.Count,
                size = parameters.Size,
                output_format = parameters.OutputFormat,
                quality = parameters.Quality,
                background = parameters.Background,
            }, JsonOptions);
            return new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
            };
        }

        private static HttpContent BuildMultipartContent(
            string prompt,
            string model,
            Parameters parameters,
            IReadOnlyList<DecodedImage> sources)
        {
            var content = new MultipartFormDataContent("oga-" + Guid.NewGuid().ToString("N"));
            content.Add(new StringContent(model, Encoding.UTF8), "model");
            content.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
            content.Add(new StringContent(parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), "n");
            content.Add(new StringContent(parameters.Size, Encoding.UTF8), "size");
            content.Add(new StringContent(parameters.OutputFormat, Encoding.UTF8), "output_format");
            if (parameters.Quality is not null)
            {
                content.Add(new StringContent(parameters.Quality, Encoding.UTF8), "quality");
            }

            if (parameters.Background is not null)
            {
                content.Add(new StringContent(parameters.Background, Encoding.UTF8), "background");
            }

            for (var index = 0; index < sources.Count; index++)
            {
                var source = sources[index];
                var image = new ByteArrayContent(source.Bytes);
                image.Headers.ContentType = new MediaTypeHeaderValue(source.MediaType);
                content.Add(image, "image[]", $"reference-{index + 1}.{source.Extension}");
            }

            return content;
        }

        private static Uri AppendOperation(Uri endpoint, string operation)
        {
            var path = endpoint.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/generations", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/edits", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(0, path.LastIndexOf('/'));
            }

            if (!path.EndsWith("/images", StringComparison.OrdinalIgnoreCase))
            {
                path += "/images";
            }

            return new UriBuilder(endpoint) { Path = path + "/" + operation }.Uri;
        }
    }

    private sealed class Parameters
    {
        private static readonly HashSet<string> GenerationSizes = new(StringComparer.Ordinal)
        {
            "auto", "1024x1024", "1024x1536", "1536x1024",
        };

        private Parameters(string size, string outputFormat, int count, string? quality, string? background)
        {
            Size = size;
            OutputFormat = outputFormat;
            Count = count;
            Quality = quality;
            Background = background;
        }

        public string Size { get; }
        public string OutputFormat { get; }
        public int Count { get; }
        public string? Quality { get; }
        public string? Background { get; }

        public static Parameters Parse(string json, bool editing, long maximumPixels, int maximumOutputs)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("OpenAI image parameters must be a JSON object.");
            }

            var allowed = new HashSet<string>(new[] { "size", "output_format", "n", "quality", "background" }, StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new InvalidDataException("The OpenAI image request contained an unsupported or reserved parameter.");
                }
            }

            var size = String(document.RootElement, "size") ?? "1024x1024";
            if (size != "auto")
            {
                ImageProviderCommon.ParseSize(size, maximumPixels);
            }

            if (!GenerationSizes.Contains(size))
            {
                throw new InvalidDataException("The requested size is not supported by the OpenAI image API.");
            }

            var format = String(document.RootElement, "output_format") ?? "png";
            _ = ImageProviderCommon.OutputMediaType(format);
            var count = Int32(document.RootElement, "n") ?? 1;
            if (count is < 1 or > 10 || count > maximumOutputs)
            {
                throw new InvalidDataException("The image output count exceeds the configured provider limit.");
            }

            var quality = String(document.RootElement, "quality");
            var background = String(document.RootElement, "background");
            if (quality is not null && quality is not ("auto" or "low" or "medium" or "high"))
            {
                throw new InvalidDataException("The OpenAI image quality is invalid.");
            }

            if (background is not null && background is not ("auto" or "transparent" or "opaque"))
            {
                throw new InvalidDataException("The OpenAI image background is invalid.");
            }

            return new Parameters(size, format, count, quality, background);
        }

        private static string? String(JsonElement root, string name) =>
            !root.TryGetProperty(name, out var value)
                ? null
                : value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                    ? text
                    : throw new InvalidDataException("An OpenAI image parameter had the wrong type.");

        private static int? Int32(JsonElement root, string name) =>
            !root.TryGetProperty(name, out var value)
                ? null
                : value.TryGetInt32(out var number)
                    ? number
                    : throw new InvalidDataException("An OpenAI image parameter had the wrong type.");
    }

    private sealed class Settings
    {
        private Settings(
            Uri endpoint,
            HttpClient httpClient,
            IReadOnlyDictionary<string, string> headers,
            bool allowInsecureLoopbackHttp,
            ImageProviderLimits limits)
        {
            Endpoint = endpoint;
            HttpClient = httpClient;
            Headers = headers;
            AllowInsecureLoopbackHttp = allowInsecureLoopbackHttp;
            Limits = limits;
        }

        public Uri Endpoint { get; }
        public HttpClient HttpClient { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public bool AllowInsecureLoopbackHttp { get; }
        public ImageProviderLimits Limits { get; }

        public static Settings Create(OpenAIImageProviderOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var limits = new ImageProviderLimits
            {
                MaxReferences = options.MaxReferences,
                MaxReferenceBytes = options.MaxReferenceBytes,
                MaxAggregateReferenceBytes = options.MaxAggregateReferenceBytes,
                MaxRequestBytes = options.MaxRequestBytes,
                MaxResponseBytes = options.MaxResponseBytes,
                MaxOutputBytes = options.MaxOutputBytes,
                MaxOutputs = options.MaxOutputs,
                MaxPromptBytes = options.MaxPromptBytes,
                MaxPixels = options.MaxPixels,
                Timeout = options.Timeout,
            };
            limits.Validate();
            return new Settings(
                ImageProviderCommon.ValidateEndpoint(options.Endpoint, options.AllowInsecureLoopbackHttp),
                ImageProviderCommon.CreateClient(options.HttpMessageHandler),
                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase)),
                options.AllowInsecureLoopbackHttp,
                limits);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

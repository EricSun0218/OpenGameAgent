using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.Images.Internal;

namespace OpenGameAgent.Providers.Volcengine.Images;

public sealed class VolcengineImageProviderOptions
{
    public Uri Endpoint { get; set; } = new("https://ark.cn-beijing.volces.com/api/v3/images/generations");

    public HttpMessageHandler? HttpMessageHandler { get; set; }

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool AllowInsecureLoopbackHttp { get; set; }

    public bool Watermark { get; set; }

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

public static class VolcengineImageProvider
{
    public const string ProviderId = "volcengine";
    public const string ApiId = "volcengine-ark-images";

    public static GameMediaProviderRegistration CreateRegistration(
        VolcengineImageProviderOptions options,
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
            new GameProviderDescriptor(ProviderId, "Volcengine Ark Images", settings.Endpoint),
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
            throw new ArgumentException("At least one Volcengine image model is required.", nameof(models));
        }

        if (models.Any(model => model is null
                                || !string.Equals(model.ProviderId, ProviderId, StringComparison.Ordinal)
                                || !string.Equals(model.Api, ApiId, StringComparison.Ordinal)
                                || !model.OutputCapabilities.HasFlag(GameModelOutputCapabilities.Image)))
        {
            throw new ArgumentException("Every model must be a Volcengine image model.", nameof(models));
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
                throw new InvalidOperationException("The selected model is not a Volcengine image model.");
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
                _settings.Watermark,
                _settings.Limits.MaxPixels,
                _settings.Limits.MaxOutputs);
            var endpoint = ResolveGenerationEndpoint(ImageProviderCommon.ResolveEndpoint(
                _invocation.Endpoint ?? _settings.Endpoint,
                _invocation.Authentication,
                _settings.AllowInsecureLoopbackHttp));
            var dataUrls = sources.Select(source =>
                "data:" + source.MediaType + ";base64," + Convert.ToBase64String(source.Bytes)).ToArray();
            var body = BuildRequest(prompt, _invocation.Model.ModelId, parameters, dataUrls);
            if (body.Length > _settings.Limits.MaxRequestBytes)
            {
                throw new InvalidDataException("The image request exceeded the configured byte limit.");
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            ImageProviderCommon.ApplyHeaders(
                message,
                _settings.Headers,
                _invocation.Headers,
                _invocation.Authentication);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

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
                parameters.OutputFormat is null
                    ? null
                    : ImageProviderCommon.OutputMediaType(parameters.OutputFormat),
                _settings.Limits,
                ImageProviderCommon.RequestId(response));
        }

        private static byte[] BuildRequest(
            string prompt,
            string model,
            Parameters parameters,
            IReadOnlyList<string> images)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("model", model);
                writer.WriteString("prompt", prompt);
                if (images.Count > 0)
                {
                    writer.WritePropertyName("image");
                    writer.WriteStartArray();
                    foreach (var image in images)
                    {
                        writer.WriteStringValue(image);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteString("response_format", "b64_json");
                writer.WriteBoolean("stream", false);
                writer.WriteBoolean("watermark", parameters.Watermark);
                writer.WriteString("size", parameters.Size);
                if (parameters.OutputFormat is not null)
                {
                    writer.WriteString("output_format", parameters.OutputFormat);
                }
                if (parameters.Count > 1)
                {
                    writer.WriteNumber("n", parameters.Count);
                }

                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }

        private static Uri ResolveGenerationEndpoint(Uri endpoint)
        {
            var path = endpoint.AbsolutePath.TrimEnd('/');
            if (!path.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
            {
                path += "/images/generations";
            }

            return new UriBuilder(endpoint) { Path = path }.Uri;
        }
    }

    private sealed class Parameters
    {
        private Parameters(string size, string? outputFormat, int count, bool watermark)
        {
            Size = size;
            OutputFormat = outputFormat;
            Count = count;
            Watermark = watermark;
        }

        public string Size { get; }
        public string? OutputFormat { get; }
        public int Count { get; }
        public bool Watermark { get; }

        public static Parameters Parse(string json, bool defaultWatermark, long maximumPixels, int maximumOutputs)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Volcengine image parameters must be a JSON object.");
            }

            var allowed = new HashSet<string>(new[] { "size", "output_format", "n", "watermark" }, StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new InvalidDataException("The Volcengine image request contained an unsupported or reserved parameter.");
                }
            }

            var size = String(document.RootElement, "size") ?? "2048x2048";
            if (size is not ("1K" or "2K" or "4K"))
            {
                _ = ImageProviderCommon.ParseSize(size, maximumPixels);
            }

            var format = String(document.RootElement, "output_format");
            if (format is not null)
            {
                _ = ImageProviderCommon.OutputMediaType(format);
            }
            var count = Int32(document.RootElement, "n") ?? 1;
            if (count < 1 || count > Math.Min(maximumOutputs, 10))
            {
                throw new InvalidDataException("The image output count exceeded the configured limit.");
            }

            var watermark = Boolean(document.RootElement, "watermark") ?? defaultWatermark;
            return new Parameters(size, format, count, watermark);
        }

        private static string? String(JsonElement root, string name) =>
            !root.TryGetProperty(name, out var value)
                ? null
                : value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                    ? text
                    : throw new InvalidDataException("A Volcengine image parameter had the wrong type.");

        private static int? Int32(JsonElement root, string name) =>
            !root.TryGetProperty(name, out var value)
                ? null
                : value.TryGetInt32(out var number)
                    ? number
                    : throw new InvalidDataException("A Volcengine image parameter had the wrong type.");

        private static bool? Boolean(JsonElement root, string name) =>
            !root.TryGetProperty(name, out var value)
                ? null
                : value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : throw new InvalidDataException("A Volcengine image parameter had the wrong type.");
    }

    private sealed class Settings
    {
        private Settings(
            Uri endpoint,
            HttpClient httpClient,
            IReadOnlyDictionary<string, string> headers,
            bool allowInsecureLoopbackHttp,
            bool watermark,
            ImageProviderLimits limits)
        {
            Endpoint = endpoint;
            HttpClient = httpClient;
            Headers = headers;
            AllowInsecureLoopbackHttp = allowInsecureLoopbackHttp;
            Watermark = watermark;
            Limits = limits;
        }

        public Uri Endpoint { get; }
        public HttpClient HttpClient { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public bool AllowInsecureLoopbackHttp { get; }
        public bool Watermark { get; }
        public ImageProviderLimits Limits { get; }

        public static Settings Create(VolcengineImageProviderOptions options)
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
                options.Watermark,
                limits);
        }
    }
}

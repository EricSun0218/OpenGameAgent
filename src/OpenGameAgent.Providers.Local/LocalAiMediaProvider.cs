using System.Buffers;
using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.Images.Internal;

namespace OpenGameAgent.Providers.Local;

public sealed class LocalAiMediaProviderOptions
{
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:8080/v1");

    public HttpMessageHandler? HttpMessageHandler { get; set; }

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int MaxSources { get; set; } = 3;

    public int MaxSourceBytes { get; set; } = 20_000_000;

    public int MaxAggregateSourceBytes { get; set; } = 50_000_000;

    public int MaxRequestBytes { get; set; } = 80_000_000;

    public int MaxResponseBytes { get; set; } = 100_000_000;

    public int MaxOutputBytes { get; set; } = 100_000_000;

    public int MaxOutputs { get; set; } = 8;

    public int MaxPromptBytes { get; set; } = 1_000_000;

    public long MaxPixels { get; set; } = 67_108_864;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);
}

public static class LocalAiMediaProvider
{
    public const string ProviderId = "localai";
    public const string ApiId = "localai-media";

    public static GameMediaProviderRegistration CreateRegistration(
        LocalAiMediaProviderOptions options,
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
            new GameProviderDescriptor(
                ProviderId,
                "LocalAI media",
                settings.Endpoint,
                isLocal: settings.Endpoint.IsLoopback),
            authentication,
            invocation => new Generator(settings, invocation),
            checkedModels);
    }

    public static GameModelDescriptor CreateImageModel(string modelId, string? displayName = null) =>
        CreateModel(
            modelId,
            displayName,
            GameModelInputCapabilities.Text,
            GameModelOutputCapabilities.Image);

    public static GameModelDescriptor CreateVideoModel(
        string modelId,
        string? displayName = null,
        bool acceptsImage = true,
        bool acceptsAudio = false) =>
        CreateModel(
            modelId,
            displayName,
            GameModelInputCapabilities.Text
            | (acceptsImage ? GameModelInputCapabilities.Image : GameModelInputCapabilities.None)
            | (acceptsAudio ? GameModelInputCapabilities.Audio : GameModelInputCapabilities.None),
            GameModelOutputCapabilities.Video);

    public static GameModelDescriptor CreateSpeechModel(string modelId, string? displayName = null) =>
        CreateModel(
            modelId,
            displayName,
            GameModelInputCapabilities.Text,
            GameModelOutputCapabilities.Audio);

    private static GameModelDescriptor CreateModel(
        string modelId,
        string? displayName,
        GameModelInputCapabilities input,
        GameModelOutputCapabilities output) => new(
            ProviderId,
            modelId,
            displayName,
            inputCapabilities: input,
            outputCapabilities: output,
            cost: new GameModelCost(isKnown: false),
            api: ApiId);

    private static IReadOnlyList<GameModelDescriptor> ValidateModels(IReadOnlyList<GameModelDescriptor> models)
    {
        if (models is null || models.Count == 0)
        {
            throw new ArgumentException("At least one LocalAI media model is required.", nameof(models));
        }

        if (models.Any(model => model is null
                                || !string.Equals(model.ProviderId, ProviderId, StringComparison.Ordinal)
                                || !string.Equals(model.Api, ApiId, StringComparison.Ordinal)
                                || (model.OutputCapabilities
                                    & (GameModelOutputCapabilities.Image
                                       | GameModelOutputCapabilities.Audio
                                       | GameModelOutputCapabilities.Video)) == 0))
        {
            throw new ArgumentException("Every model must be a LocalAI media model.", nameof(models));
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
                throw new InvalidOperationException("The selected model is not a LocalAI media model.");
            }
        }

        public ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.Sources.Count > _settings.MaxSources)
            {
                throw new InvalidDataException("The local media request has too many sources.");
            }

            return request.Kind switch
            {
                GameMediaKind.Image => GenerateImageAsync(request, progress, cancellationToken),
                GameMediaKind.Video => GenerateVideoAsync(request, progress, cancellationToken),
                GameMediaKind.Audio => GenerateSpeechAsync(request, progress, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Kind)),
            };
        }

        private async ValueTask<GameMediaGenerationResult> GenerateImageAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            RequireCapability(GameModelOutputCapabilities.Image);
            if (request.Sources.Count != 0)
            {
                throw new InvalidDataException(
                    "LocalAI image generation does not define unlabelled reference-image semantics; use a workflow adapter for editing or inpainting.");
            }

            var prompt = RequirePrompt(request.Prompt);
            using var parameters = ParseParameters(
                request.ParametersJson,
                new HashSet<string>(new[] { "size", "n", "negative_prompt", "step", "seed", "cfg_scale" }, StringComparer.Ordinal));
            var payload = CopyParameters(parameters.RootElement);
            payload["model"] = _invocation.Model.ModelId;
            payload["prompt"] = prompt;
            payload["response_format"] = "b64_json";
            ValidateImageParameters(payload, _settings.ImageLimits.MaxPixels);
            var endpoint = ResolveEndpoint("images/generations");
            using var response = await SendJsonAsync(endpoint, payload, progress, cancellationToken).ConfigureAwait(false);
            using var document = await ImageProviderCommon.ReadJsonAsync(
                response,
                _settings.MaxResponseBytes,
                cancellationToken).ConfigureAwait(false);
            return ImageProviderCommon.ParseResult(
                document,
                expectedMediaType: null,
                _settings.ImageLimits,
                ImageProviderCommon.RequestId(response));
        }

        private async ValueTask<GameMediaGenerationResult> GenerateVideoAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            RequireCapability(GameModelOutputCapabilities.Video);
            var prompt = RequirePrompt(request.Prompt);
            using var parameters = ParseParameters(
                request.ParametersJson,
                new HashSet<string>(new[]
                {
                    "negative_prompt", "width", "height", "num_frames", "fps", "seconds", "size",
                    "seed", "cfg_scale", "step",
                }, StringComparer.Ordinal));
            var payload = CopyParameters(parameters.RootElement);
            payload["model"] = _invocation.Model.ModelId;
            payload["prompt"] = prompt;
            payload["response_format"] = "b64_json";
            AddVideoSources(payload, request.Sources);
            ValidateVideoParameters(payload, _settings.ImageLimits.MaxPixels);
            using var response = await SendJsonAsync(
                ResolveEndpointOutsideV1("video"),
                payload,
                progress,
                cancellationToken).ConfigureAwait(false);
            return await ParseBase64EnvelopeAsync(
                response,
                GameMediaKind.Video,
                cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<GameMediaGenerationResult> GenerateSpeechAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            RequireCapability(GameModelOutputCapabilities.Audio);
            if (request.Sources.Count != 0)
            {
                throw new InvalidDataException("Local text-to-speech does not accept source resources.");
            }

            var input = RequirePrompt(request.Prompt);
            using var parameters = ParseParameters(
                request.ParametersJson,
                new HashSet<string>(new[] { "voice", "response_format", "speed", "language", "instructions", "sample_rate" }, StringComparer.Ordinal));
            var payload = CopyParameters(parameters.RootElement);
            payload["model"] = _invocation.Model.ModelId;
            payload["input"] = input;
            payload["voice"] = ReadString(payload, "voice") ?? "default";
            payload["response_format"] = ReadString(payload, "response_format") ?? "wav";
            ValidateSpeechParameters(payload);
            using var response = await SendJsonAsync(
                ResolveEndpoint("audio/speech"),
                payload,
                progress,
                cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(response, _settings.MaxOutputBytes, cancellationToken)
                .ConfigureAwait(false);
            var mediaType = DetectMediaType(bytes, GameMediaKind.Audio);
            var declared = response.Content.Headers.ContentType?.MediaType;
            if (declared is not null
                && !declared.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                && !EquivalentAudioMediaType(declared, mediaType))
            {
                throw new InvalidDataException("The local speech response type did not match its bytes.");
            }

            return Result(bytes, mediaType, ImageProviderCommon.RequestId(response));
        }

        private async ValueTask<HttpResponseMessage> SendJsonAsync(
            Uri endpoint,
            IReadOnlyDictionary<string, object?> payload,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            if (bytes.Length > _settings.MaxRequestBytes)
            {
                throw new InvalidDataException("The local media request exceeded its configured limit.");
            }

            if (progress is not null)
            {
                await progress(new GameMediaGenerationProgress("submitted", 0), cancellationToken)
                    .ConfigureAwait(false);
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(bytes),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            ImageProviderCommon.ApplyHeaders(
                message,
                _settings.Headers,
                _invocation.Headers,
                _invocation.Authentication);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_settings.Timeout);
            var response = await _settings.HttpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            try
            {
                ImageProviderCommon.ValidateResponseOrigin(response, endpoint);
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    throw new InvalidDataException("The local media service refused a redirect response.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw await ImageProviderCommon.CreateResponseExceptionAsync(
                        response,
                        _settings.MaxResponseBytes,
                        timeout.Token).ConfigureAwait(false);
                }

                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        private async ValueTask<GameMediaGenerationResult> ParseBase64EnvelopeAsync(
            HttpResponseMessage response,
            GameMediaKind kind,
            CancellationToken cancellationToken)
        {
            using var document = await ImageProviderCommon.ReadJsonAsync(
                response,
                _settings.MaxResponseBytes,
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The local media service returned an invalid response envelope.");
            }

            var outputs = new List<ResourceContent>();
            foreach (var item in data.EnumerateArray())
            {
                if (outputs.Count >= _settings.MaxOutputs
                    || item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("b64_json", out var encodedElement)
                    || encodedElement.ValueKind != JsonValueKind.String
                    || encodedElement.GetString() is not { Length: > 0 } encoded
                    || encoded.Any(char.IsWhiteSpace))
                {
                    throw new InvalidDataException("The local media service returned invalid or excessive output data.");
                }

                var bytes = DecodeBounded(encoded, _settings.MaxOutputBytes);
                var mediaType = DetectMediaType(bytes, kind);
                outputs.Add(new ResourceContent("data:" + mediaType + ";base64," + encoded, mediaType));
            }

            if (outputs.Count == 0)
            {
                throw new InvalidDataException("The local media service returned no outputs.");
            }

            var requestId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? SafeId(id.GetString())
                : ImageProviderCommon.RequestId(response);
            return new GameMediaGenerationResult(
                new ReadOnlyCollection<ResourceContent>(outputs),
                JsonSerializer.Serialize(new { outputCount = outputs.Count }),
                requestId);
        }

        private void AddVideoSources(IDictionary<string, object?> payload, IReadOnlyList<ResourceContent> sources)
        {
            long total = 0;
            var imageIndex = 0;
            foreach (var source in sources)
            {
                var (bytes, mediaType) = DecodeSource(source);
                total = checked(total + bytes.Length);
                if (total > _settings.MaxAggregateSourceBytes)
                {
                    throw new InvalidDataException("The local media sources exceeded their aggregate byte limit.");
                }

                if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    if (imageIndex > 1)
                    {
                        throw new InvalidDataException("A local video request supports at most a start and end image.");
                    }

                    payload[imageIndex++ == 0 ? "start_image" : "end_image"] = source.Uri;
                }
                else if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    if (payload.ContainsKey("audio"))
                    {
                        throw new InvalidDataException("A local video request supports at most one audio source.");
                    }

                    payload["audio"] = source.Uri;
                }
                else
                {
                    throw new InvalidDataException("A local video source must be an inline image or audio data URL.");
                }
            }
        }

        private (byte[] Bytes, string MediaType) DecodeSource(ResourceContent source)
        {
            if (source is null
                || string.IsNullOrWhiteSpace(source.MediaType)
                || !source.Uri.StartsWith("data:" + source.MediaType + ";base64,", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A local media source must be a matching inline base64 data URL.");
            }

            var separator = source.Uri.IndexOf(',');
            var bytes = DecodeBounded(source.Uri.Substring(separator + 1), _settings.MaxSourceBytes);
            var detected = DetectMediaType(bytes, source.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? GameMediaKind.Image
                : GameMediaKind.Audio);
            if (!detected.Equals(source.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A local media source type did not match its bytes.");
            }

            return (bytes, detected);
        }

        private string RequirePrompt(string? value) =>
            string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > _settings.MaxPromptBytes
                ? throw new InvalidDataException("The local media prompt is missing or exceeded its configured limit.")
                : value;

        private void RequireCapability(GameModelOutputCapabilities capability)
        {
            if (!_invocation.Model.OutputCapabilities.HasFlag(capability))
            {
                throw new InvalidOperationException("The selected local media model does not support the requested output kind.");
            }
        }

        private Uri ResolveEndpoint(string suffix)
        {
            var endpoint = _invocation.Endpoint ?? _settings.Endpoint;
            return Append(endpoint, suffix);
        }

        private Uri ResolveEndpointOutsideV1(string suffix)
        {
            var endpoint = _invocation.Endpoint ?? _settings.Endpoint;
            var path = endpoint.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(0, path.Length - 3).TrimEnd('/');
            }

            return new UriBuilder(endpoint) { Path = path + "/" + suffix }.Uri;
        }

        private static Uri Append(Uri endpoint, string suffix) =>
            new UriBuilder(endpoint)
            {
                Path = endpoint.AbsolutePath.TrimEnd('/') + "/" + suffix,
            }.Uri;
    }

    private static JsonDocument ParseParameters(string json, HashSet<string> allowed)
    {
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("Local media parameters must be a JSON object.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                document.Dispose();
                throw new InvalidDataException("The local media request contained an unsupported or reserved parameter.");
            }
        }

        return document;
    }

    private static Dictionary<string, object?> CopyParameters(JsonElement root)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number when property.Value.TryGetDouble(out var number) => number,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException("A local media parameter had an unsupported value type."),
            };
        }

        return result;
    }

    private static void ValidateImageParameters(
        IReadOnlyDictionary<string, object?> values,
        long maximumPixels)
    {
        if (ReadString(values, "size") is { } size)
        {
            _ = ImageProviderCommon.ParseSize(size, maximumPixels);
        }

        RequireInteger(values, "n", 1, 8);
        RequireInteger(values, "step", 1, 1_000);
        RequireInteger(values, "seed", int.MinValue, int.MaxValue);
        RequireNumber(values, "cfg_scale", 0, 100);
    }

    private static void ValidateVideoParameters(
        IReadOnlyDictionary<string, object?> values,
        long maximumPixels)
    {
        RequireInteger(values, "width", 64, 16_384);
        RequireInteger(values, "height", 64, 16_384);
        RequireInteger(values, "num_frames", 1, 100_000);
        RequireInteger(values, "fps", 1, 240);
        RequireInteger(values, "step", 1, 10_000);
        RequireInteger(values, "seed", int.MinValue, int.MaxValue);
        RequireNumber(values, "cfg_scale", 0, 100);
        RequireNumber(values, "seconds", 0.01, 3_600);
        if (ReadString(values, "size") is { } size)
        {
            _ = ImageProviderCommon.ParseSize(size, maximumPixels);
        }

        if (values.TryGetValue("width", out var widthValue)
            && values.TryGetValue("height", out var heightValue)
            && widthValue is long width
            && heightValue is long height
            && checked(width * height) > maximumPixels)
        {
            throw new InvalidDataException("The local video dimensions exceeded the configured pixel limit.");
        }
    }

    private static void ValidateSpeechParameters(IReadOnlyDictionary<string, object?> values)
    {
        var voice = ReadString(values, "voice");
        if (voice is null || voice.Length > 512)
        {
            throw new InvalidDataException("The local speech voice is invalid.");
        }

        var format = ReadString(values, "response_format");
        if (format is not ("wav" or "mp3" or "ogg" or "flac"))
        {
            throw new InvalidDataException("The local speech output format is invalid.");
        }

        RequireNumber(values, "speed", 0.25, 4);
        RequireInteger(values, "sample_rate", 8_000, 192_000);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> values, string name) =>
        !values.TryGetValue(name, out var value)
            ? null
            : value as string ?? throw new InvalidDataException("A local media parameter had the wrong type.");

    private static void RequireInteger(
        IReadOnlyDictionary<string, object?> values,
        string name,
        long minimum,
        long maximum)
    {
        if (!values.TryGetValue(name, out var value))
        {
            return;
        }

        if (value is not long number || number < minimum || number > maximum)
        {
            throw new InvalidDataException("A local media integer parameter was outside its allowed range.");
        }
    }

    private static void RequireNumber(
        IReadOnlyDictionary<string, object?> values,
        string name,
        double minimum,
        double maximum)
    {
        if (!values.TryGetValue(name, out var value))
        {
            return;
        }

        var number = value switch
        {
            long integer => integer,
            double floating => floating,
            _ => double.NaN,
        };
        if (double.IsNaN(number) || double.IsInfinity(number) || number < minimum || number > maximum)
        {
            throw new InvalidDataException("A local media numeric parameter was outside its allowed range.");
        }
    }

    private static byte[] DecodeBounded(string encoded, int maximumBytes)
    {
        if (encoded.Length == 0 || encoded.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Local media base64 was invalid.");
        }

        var maximum = checked((encoded.Length / 4 + 1) * 3);
        if (maximum > maximumBytes + 2)
        {
            throw new InvalidDataException("Local media base64 exceeded its configured byte limit.");
        }

        var rented = ArrayPool<byte>.Shared.Rent(maximum);
        try
        {
            if (!Convert.TryFromBase64String(encoded, rented, out var written) || written > maximumBytes)
            {
                throw new InvalidDataException("Local media base64 was invalid or oversized.");
            }

            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static string DetectMediaType(byte[] bytes, GameMediaKind kind)
    {
        if (kind == GameMediaKind.Image)
        {
            if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                return "image/png";
            }

            if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            {
                return "image/jpeg";
            }

            if (bytes.Length >= 12
                && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP")
            {
                return "image/webp";
            }
        }
        else if (kind == GameMediaKind.Audio)
        {
            if (bytes.Length >= 12
                && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                && Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE")
            {
                return "audio/wav";
            }

            if (bytes.Length >= 4 && Encoding.ASCII.GetString(bytes, 0, 4) == "OggS")
            {
                return "audio/ogg";
            }

            if (bytes.Length >= 4 && Encoding.ASCII.GetString(bytes, 0, 4) == "fLaC")
            {
                return "audio/flac";
            }

            if ((bytes.Length >= 3 && Encoding.ASCII.GetString(bytes, 0, 3) == "ID3")
                || (bytes.Length >= 2 && bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0))
            {
                return "audio/mpeg";
            }
        }
        else if (kind == GameMediaKind.Video)
        {
            if (bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 4, 4) == "ftyp")
            {
                return "video/mp4";
            }

            if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }))
            {
                return "video/webm";
            }
        }

        throw new InvalidDataException("The local media service returned unsupported bytes.");
    }

    private static bool EquivalentAudioMediaType(string declared, string detected) =>
        declared.Equals(detected, StringComparison.OrdinalIgnoreCase)
        || detected == "audio/wav"
           && declared.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase)
        || detected == "audio/mpeg"
           && declared.Equals("audio/mp3", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
        {
            throw new InvalidDataException("The local media response exceeded its configured limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToArray();
            }

            if (output.Length + count > maximumBytes)
            {
                throw new InvalidDataException("The local media response exceeded its configured limit.");
            }

            await output.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
        }
    }

    private static GameMediaGenerationResult Result(byte[] bytes, string mediaType, string? requestId) => new(
        new[] { new ResourceContent("data:" + mediaType + ";base64," + Convert.ToBase64String(bytes), mediaType) },
        JsonSerializer.Serialize(new { outputCount = 1 }),
        requestId);

    private static string? SafeId(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)
            ? null
            : value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Settings
    {
        private Settings(
            Uri endpoint,
            HttpClient httpClient,
            IReadOnlyDictionary<string, string> headers,
            int maxSources,
            int maxSourceBytes,
            int maxAggregateSourceBytes,
            int maxRequestBytes,
            int maxResponseBytes,
            int maxOutputBytes,
            int maxOutputs,
            int maxPromptBytes,
            TimeSpan timeout,
            ImageProviderLimits imageLimits)
        {
            Endpoint = endpoint;
            HttpClient = httpClient;
            Headers = headers;
            MaxSources = maxSources;
            MaxSourceBytes = maxSourceBytes;
            MaxAggregateSourceBytes = maxAggregateSourceBytes;
            MaxRequestBytes = maxRequestBytes;
            MaxResponseBytes = maxResponseBytes;
            MaxOutputBytes = maxOutputBytes;
            MaxOutputs = maxOutputs;
            MaxPromptBytes = maxPromptBytes;
            Timeout = timeout;
            ImageLimits = imageLimits;
        }

        public Uri Endpoint { get; }
        public HttpClient HttpClient { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public int MaxSources { get; }
        public int MaxSourceBytes { get; }
        public int MaxAggregateSourceBytes { get; }
        public int MaxRequestBytes { get; }
        public int MaxResponseBytes { get; }
        public int MaxOutputBytes { get; }
        public int MaxOutputs { get; }
        public int MaxPromptBytes { get; }
        public TimeSpan Timeout { get; }
        public ImageProviderLimits ImageLimits { get; }

        public static Settings Create(LocalAiMediaProviderOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var limits = new ImageProviderLimits
            {
                MaxReferences = 0,
                MaxReferenceBytes = options.MaxSourceBytes,
                MaxAggregateReferenceBytes = options.MaxAggregateSourceBytes,
                MaxRequestBytes = options.MaxRequestBytes,
                MaxResponseBytes = options.MaxResponseBytes,
                MaxOutputBytes = options.MaxOutputBytes,
                MaxOutputs = options.MaxOutputs,
                MaxPromptBytes = options.MaxPromptBytes,
                MaxPixels = options.MaxPixels,
                Timeout = options.Timeout,
            };
            limits.Validate();
            if (options.MaxSources is < 0 or > 128
                || options.MaxSourceBytes is < 1 or > 100_000_000
                || options.MaxAggregateSourceBytes is < 1 or > 200_000_000
                || options.MaxOutputBytes is < 1 or > 200_000_000)
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }

            var endpoint = ImageProviderCommon.ValidateEndpoint(options.Endpoint, allowInsecureHttp: true);
            if (!endpoint.IsLoopback)
            {
                throw new ArgumentException("The LocalAI media provider is limited to loopback endpoints.", nameof(options.Endpoint));
            }

            return new Settings(
                endpoint,
                ImageProviderCommon.CreateClient(options.HttpMessageHandler),
                new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase)),
                options.MaxSources,
                options.MaxSourceBytes,
                options.MaxAggregateSourceBytes,
                options.MaxRequestBytes,
                options.MaxResponseBytes,
                options.MaxOutputBytes,
                options.MaxOutputs,
                options.MaxPromptBytes,
                options.Timeout,
                limits);
        }
    }
}

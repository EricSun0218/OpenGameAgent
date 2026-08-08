using System.Buffers;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using OpenGameAgent.Models;

namespace OpenGameAgent.Providers.OpenRouter;

public sealed class OpenRouterImageProviderOptions
{
    public OpenRouterImageProviderOptions(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; set; } = new("https://openrouter.ai/api/v1/images");

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public int MaxRequestBytes { get; set; } = 16_000_000;

    public int MaxResponseBytes { get; set; } = 32_000_000;

    public int MaxErrorCharacters { get; set; } = 65_536;

    public int MaxModels { get; set; } = 100_000;

    public int MaxOutputs { get; set; } = 10;

    public bool AllowInsecureHttp { get; set; }
}

public static class OpenRouterImageProvider
{
    public const string ProviderId = "openrouter";
    public const string ApiId = "openrouter-images";

    public static GameMediaProviderRegistration CreateRegistration(
        OpenRouterImageProviderOptions options,
        IGameProviderAuthentication authentication,
        IReadOnlyList<GameModelDescriptor>? initialModels = null)
    {
        if (authentication is null)
        {
            throw new ArgumentNullException(nameof(authentication));
        }

        var settings = Settings.Create(options);
        var models = (initialModels ?? Array.Empty<GameModelDescriptor>()).ToArray();
        if (models.Any(model => model is null
                                || !string.Equals(model.ProviderId, ProviderId, StringComparison.Ordinal)
                                || !string.Equals(model.Api, ApiId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Every initial image model must use the OpenRouter provider and image API.",
                nameof(initialModels));
        }

        var descriptor = new GameProviderDescriptor(
            ProviderId,
            "OpenRouter",
            settings.Endpoint,
            supportsDynamicModels: true);
        return new GameMediaProviderRegistration(
            descriptor,
            authentication,
            invocation => new OpenRouterImageGenerator(settings, invocation),
            Array.AsReadOnly(models),
            (context, cancellationToken) => ListModelsAsync(settings, context, cancellationToken));
    }

    private static async ValueTask<IReadOnlyList<GameModelDescriptor>> ListModelsAsync(
        Settings settings,
        GameMediaModelRefreshContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(settings.Endpoint, context.Authentication?.BaseUrl);
        settings.ValidateResolvedEndpoint(endpoint);
        endpoint = ModelsEndpoint(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyHeaders(request, settings.Headers, context.Authentication, credentialAsBearer: true);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await settings.HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await ResponseExceptionAsync(response, settings, cancellationToken).ConfigureAwait(false);
        }

        using var document = await ReadJsonAsync(response, settings.MaxResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The image model directory response was invalid.");
        }

        var models = new List<GameModelDescriptor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var inspected = 0;
        foreach (var item in data.EnumerateArray())
        {
            inspected++;
            if (inspected > settings.MaxModels)
            {
                throw new InvalidDataException("The image model directory exceeded the configured model limit.");
            }

            if (item.ValueKind != JsonValueKind.Object
                || !TryString(item, "id", out var id)
                || id.Length > 512
                || id.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                || !ids.Add(id))
            {
                throw new InvalidDataException("The image model directory contained an invalid or duplicate model.");
            }

            var input = GameModelInputCapabilities.None;
            var output = GameModelOutputCapabilities.None;
            if (item.TryGetProperty("architecture", out var architecture)
                && architecture.ValueKind == JsonValueKind.Object)
            {
                input = ParseInputModalities(architecture);
                output = ParseOutputModalities(architecture);
            }

            if (!output.HasFlag(GameModelOutputCapabilities.Image))
            {
                continue;
            }

            if (input == GameModelInputCapabilities.None)
            {
                input = GameModelInputCapabilities.Text;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (TryString(item, "description", out var description) && description.Length <= 16_384)
            {
                metadata["description"] = description;
            }

            if (item.TryGetProperty("supports_streaming", out var supportsStreaming)
                && supportsStreaming.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                metadata["supportsStreaming"] = supportsStreaming.GetBoolean() ? "true" : "false";
            }

            if (item.TryGetProperty("supported_parameters", out var supportedParameters)
                && supportedParameters.ValueKind == JsonValueKind.Object)
            {
                var raw = supportedParameters.GetRawText();
                if (raw.Length <= 16_384)
                {
                    metadata["supportedParameters"] = raw;
                }
            }

            var name = TryString(item, "name", out var displayName)
                       && displayName.Length <= 512
                       && displayName.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0
                ? displayName
                : id;
            models.Add(new GameModelDescriptor(
                ProviderId,
                id,
                name,
                inputCapabilities: input,
                outputCapabilities: GameModelOutputCapabilities.Image,
                metadata: metadata,
                api: ApiId,
                baseUrl: settings.Endpoint));
        }

        return Array.AsReadOnly(models.OrderBy(model => model.ModelId, StringComparer.Ordinal).ToArray());
    }

    private static GameModelInputCapabilities ParseInputModalities(JsonElement architecture)
    {
        if (!architecture.TryGetProperty("input_modalities", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return GameModelInputCapabilities.None;
        }

        var result = GameModelInputCapabilities.None;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            result |= value.GetString() switch
            {
                "text" => GameModelInputCapabilities.Text,
                "image" => GameModelInputCapabilities.Image,
                "audio" => GameModelInputCapabilities.Audio,
                "video" => GameModelInputCapabilities.Video,
                _ => GameModelInputCapabilities.None,
            };
        }

        return result;
    }

    private static GameModelOutputCapabilities ParseOutputModalities(JsonElement architecture)
    {
        if (!architecture.TryGetProperty("output_modalities", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return GameModelOutputCapabilities.None;
        }

        var result = GameModelOutputCapabilities.None;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            result |= value.GetString() switch
            {
                "text" => GameModelOutputCapabilities.Text,
                "image" => GameModelOutputCapabilities.Image,
                "audio" => GameModelOutputCapabilities.Audio,
                "video" => GameModelOutputCapabilities.Video,
                _ => GameModelOutputCapabilities.None,
            };
        }

        return result;
    }

    private static Uri ResolveEndpoint(Uri configured, Uri? authentication)
    {
        var endpoint = authentication ?? configured;
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/images", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(endpoint)
            {
                Path = path + "/images",
            };
            endpoint = builder.Uri;
        }

        return endpoint;
    }

    private static Uri ModelsEndpoint(Uri imageEndpoint)
    {
        var builder = new UriBuilder(imageEndpoint)
        {
            Path = imageEndpoint.AbsolutePath.TrimEnd('/') + "/models",
        };
        return builder.Uri;
    }

    private static void ApplyHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> configured,
        GameProviderAuthResolution? authentication,
        bool credentialAsBearer)
    {
        var headers = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in authentication?.Headers ?? new Dictionary<string, string?>())
        {
            if (pair.Value is null)
            {
                headers.Remove(pair.Key);
                suppressed.Add(pair.Key);
            }
            else
            {
                headers[pair.Key] = pair.Value;
                suppressed.Remove(pair.Key);
            }
        }

        if (credentialAsBearer
            && authentication?.Credential is { } credential
            && !headers.ContainsKey("Authorization")
            && !suppressed.Contains("Authorization"))
        {
            headers["Authorization"] = "Bearer " + credential.Secret;
        }

        foreach (var pair in headers)
        {
            if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || !request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                throw new InvalidOperationException("The image provider configuration contained an invalid header.");
            }
        }
    }

    private static async ValueTask<Exception> ResponseExceptionAsync(
        HttpResponseMessage response,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var body = await ReadTextAsync(response.Content, settings.MaxErrorCharacters, cancellationToken)
            .ConfigureAwait(false);
        var code = TryProviderErrorCode(body);
        var suffix = code is null ? string.Empty : " (" + code + ")";
        return new InvalidOperationException(
            $"The image provider returned HTTP {(int)response.StatusCode}.{suffix}");
    }

    private static string? TryProviderErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                if (TryString(error, "code", out var nestedCode))
                {
                    return SafeErrorCode(nestedCode);
                }

                if (TryString(error, "type", out var nestedType))
                {
                    return SafeErrorCode(nestedType);
                }
            }

            return TryString(root, "code", out var code) ? SafeErrorCode(code) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SafeErrorCode(string value)
    {
        if (value.Length is < 1 or > 256
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return null;
        }

        return value;
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var stream = await ReadResponseStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBytesAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The image provider returned invalid JSON.", exception);
        }
    }

    private static async ValueTask<string> ReadTextAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        using var stream = await ReadResponseStreamAsync(content, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBytesAsync(stream, checked(maximumCharacters * 4), cancellationToken)
            .ConfigureAwait(false);
        var value = new UTF8Encoding(false, true).GetString(bytes);
        return value.Length <= maximumCharacters ? value : value.Substring(0, maximumCharacters);
    }

    private static async Task<Stream> ReadResponseStreamAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var streamTask = content.ReadAsStreamAsync();
        if (!streamTask.IsCompleted)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                canceled);
            if (streamTask != await Task.WhenAny(streamTask, canceled.Task).ConfigureAwait(false))
            {
                _ = streamTask.ContinueWith(
                    completed =>
                    {
                        if (completed.Status == TaskStatus.RanToCompletion)
                        {
                            completed.Result.Dispose();
                        }
                        else
                        {
                            _ = completed.Exception;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }
        }

        var stream = await streamTask.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            stream.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }

        return stream;
    }

    private static async ValueTask<byte[]> ReadBytesAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(stream.Dispose);
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(rented, 0, rented.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > maximumBytes)
                {
                    throw new InvalidDataException("The image provider response exceeded the configured size limit.");
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryString(JsonElement value, string name, out string result)
    {
        if (value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { Length: > 0 } text)
        {
            result = text;
            return true;
        }

        result = string.Empty;
        return false;
    }

    private sealed class OpenRouterImageGenerator : IGameMediaGenerator
    {
        private readonly Settings _settings;
        private readonly GameMediaGenerationInvocation _invocation;

        public OpenRouterImageGenerator(Settings settings, GameMediaGenerationInvocation invocation)
        {
            _settings = settings;
            _invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
            if (!string.Equals(invocation.Model.ProviderId, ProviderId, StringComparison.Ordinal)
                || !string.Equals(invocation.Model.Api, ApiId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The selected model is not an OpenRouter image model.");
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

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                throw new InvalidOperationException("The image provider requires a non-empty prompt.");
            }

            var endpoint = ResolveEndpoint(_settings.Endpoint, _invocation.Endpoint);
            _settings.ValidateResolvedEndpoint(endpoint);
            var body = BuildRequest(request, _invocation.Model.ModelId, out var streaming);
            if (body.Length > _settings.MaxRequestBytes)
            {
                throw new InvalidDataException("The image generation request exceeded the configured size limit.");
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            ApplyHeaders(message, _settings.Headers, _invocation.Authentication, credentialAsBearer: true);
            foreach (var pair in _invocation.Headers)
            {
                if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The image model contained a reserved header.");
                }

                message.Headers.Remove(pair.Key);
                if (!message.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                {
                    throw new InvalidOperationException("The image model contained an invalid header.");
                }
            }

            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
            using var response = await _settings.HttpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await ResponseExceptionAsync(response, _settings, cancellationToken).ConfigureAwait(false);
            }

            var requestId = RequestId(response);
            return streaming
                ? await ReadStreamingResultAsync(response, progress, requestId, cancellationToken).ConfigureAwait(false)
                : await ReadBufferedResultAsync(response, requestId, cancellationToken).ConfigureAwait(false);
        }

        private byte[] BuildRequest(
            GameMediaGenerationRequest request,
            string model,
            out bool streaming)
        {
            using var parameters = JsonDocument.Parse(request.ParametersJson, new JsonDocumentOptions { MaxDepth = 128 });
            if (parameters.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Image generation parameters must be a JSON object.");
            }

            streaming = false;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("model", model);
                writer.WriteString("prompt", request.Prompt);
                foreach (var property in parameters.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("model")
                        || property.NameEquals("prompt")
                        || property.NameEquals("input_references"))
                    {
                        throw new InvalidOperationException(
                            $"Image generation parameter '{property.Name}' is reserved by the provider adapter.");
                    }

                    ValidateParameter(property);
                    if (property.NameEquals("stream"))
                    {
                        streaming = property.Value.GetBoolean();
                    }

                    property.WriteTo(writer);
                }

                if (request.Sources.Count > 0)
                {
                    writer.WritePropertyName("input_references");
                    writer.WriteStartArray();
                    foreach (var source in request.Sources)
                    {
                        ValidateReference(source);
                        writer.WriteStartObject();
                        writer.WriteString("type", "image_url");
                        writer.WritePropertyName("image_url");
                        writer.WriteStartObject();
                        writer.WriteString("url", source.Uri);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }

        private static void ValidateParameter(JsonProperty property)
        {
            if (property.NameEquals("stream") && property.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new InvalidOperationException("The image generation stream parameter must be boolean.");
            }

            if (property.NameEquals("n")
                && (!property.Value.TryGetInt32(out var count) || count is < 1 or > 10))
            {
                throw new InvalidOperationException("The image generation count must be between 1 and 10.");
            }

            if (property.NameEquals("output_compression")
                && (!property.Value.TryGetInt32(out var compression) || compression is < 0 or > 100))
            {
                throw new InvalidOperationException("Image output compression must be between 0 and 100.");
            }
        }

        private static void ValidateReference(ResourceContent source)
        {
            if (!IsImageMediaType(source.MediaType))
            {
                throw new InvalidOperationException("OpenRouter image references must use an image media type.");
            }

            if (Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                if (uri.UserInfo.Length > 0)
                {
                    throw new InvalidOperationException("Image reference URLs cannot contain embedded credentials.");
                }

                return;
            }

            var prefix = "data:" + source.MediaType + ";base64,";
            if (!source.Uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Image references must be HTTP(S) URLs or matching base64 data URLs.");
            }

            ValidateBase64(source.Uri.Substring(prefix.Length));
        }

        private async ValueTask<GameMediaGenerationResult> ReadBufferedResultAsync(
            HttpResponseMessage response,
            string? requestId,
            CancellationToken cancellationToken)
        {
            using var document = await ReadJsonAsync(response, _settings.MaxResponseBytes, cancellationToken)
                .ConfigureAwait(false);
            var outputs = ParseBufferedOutputs(document.RootElement);
            return new GameMediaGenerationResult(
                outputs,
                Metadata(document.RootElement),
                requestId);
        }

        private async ValueTask<GameMediaGenerationResult> ReadStreamingResultAsync(
            HttpResponseMessage response,
            GameMediaProgressHandler? progress,
            string? requestId,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "text/event-stream",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The image provider streaming response must use text/event-stream.");
            }

            using var source = await ReadResponseStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var stream = new BoundedReadStream(source, _settings.MaxResponseBytes);
            using var registration = cancellationToken.Register(stream.Dispose);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, leaveOpen: false);
            var outputs = new List<ResourceContent>();
            JsonElement? usage = null;
            JsonElement? created = null;
            var done = false;
            var data = new StringBuilder();
            while (!done)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        var value = data.ToString();
                        data.Clear();
                        if (value == "[DONE]")
                        {
                            done = true;
                            continue;
                        }

                        var eventMetadata = await ProcessStreamingEventAsync(
                            value,
                            outputs,
                            progress,
                            cancellationToken).ConfigureAwait(false);
                        if (eventMetadata?.Usage is { } eventUsage)
                        {
                            usage = eventUsage;
                        }

                        if (eventMetadata?.Created is { } eventCreated)
                        {
                            created = eventCreated;
                        }
                    }

                    continue;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    var value = line.Substring(5);
                    data.Append(value.StartsWith(" ", StringComparison.Ordinal) ? value.Substring(1) : value);
                }
            }

            if (data.Length > 0)
            {
                if (data.ToString() == "[DONE]")
                {
                    done = true;
                }
                else
                {
                    var eventMetadata = await ProcessStreamingEventAsync(
                        data.ToString(),
                        outputs,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    if (eventMetadata?.Usage is { } eventUsage)
                    {
                        usage = eventUsage;
                    }

                    if (eventMetadata?.Created is { } eventCreated)
                    {
                        created = eventCreated;
                    }
                }
            }

            if (!done)
            {
                throw new InvalidDataException("The image provider stream ended without its terminal marker.");
            }

            if (outputs.Count == 0)
            {
                throw new InvalidDataException("The image provider stream ended without a completed image.");
            }

            return new GameMediaGenerationResult(
                outputs,
                StreamingMetadata(created, usage),
                requestId);
        }

        private async ValueTask<StreamingEventMetadata?> ProcessStreamingEventAsync(
            string value,
            ICollection<ResourceContent> outputs,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (!TryString(root, "type", out var type))
            {
                throw new InvalidDataException("The image provider stream returned an event without a type.");
            }

            if (type == "image_generation.partial_image")
            {
                if (progress is not null)
                {
                    var index = root.TryGetProperty("partial_image_index", out var indexValue)
                                && indexValue.TryGetInt32(out var parsed)
                        ? parsed
                        : 0;
                    if (index < 0 || index >= _settings.MaxOutputs)
                    {
                        throw new InvalidDataException("The image provider returned an invalid partial-image index.");
                    }

                    var preview = TryString(root, "b64_json", out _)
                        ? ParseImage(root, index)
                        : null;
                    await progress(
                        new GameMediaGenerationProgress(
                            "partial_image",
                            detailsJson: JsonSerializer.Serialize(new { index }),
                            preview: preview),
                        cancellationToken).ConfigureAwait(false);
                }

                return null;
            }

            if (type == "image_generation.completed")
            {
                if (outputs.Count >= _settings.MaxOutputs)
                {
                    throw new InvalidDataException("The image provider returned too many outputs.");
                }

                outputs.Add(ParseImage(root, outputs.Count));
                var usage = root.TryGetProperty("usage", out var usageValue) ? usageValue.Clone() : default(JsonElement?);
                var created = root.TryGetProperty("created", out var createdValue)
                    ? createdValue.Clone()
                    : default(JsonElement?);
                return new StreamingEventMetadata(created, usage);
            }

            if (type == "error")
            {
                throw new InvalidOperationException(BoundProviderError(root));
            }

            throw new InvalidDataException("The image provider stream returned an unsupported event type.");
        }

        private static string StreamingMetadata(JsonElement? created, JsonElement? usage)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (created is { } createdValue)
                {
                    writer.WritePropertyName("created");
                    createdValue.WriteTo(writer);
                }

                if (usage is { } usageValue)
                {
                    writer.WritePropertyName("usage");
                    usageValue.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private sealed class StreamingEventMetadata
        {
            public StreamingEventMetadata(JsonElement? created, JsonElement? usage)
            {
                Created = created;
                Usage = usage;
            }

            public JsonElement? Created { get; }

            public JsonElement? Usage { get; }
        }

        private IReadOnlyList<ResourceContent> ParseBufferedOutputs(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The image provider returned an invalid result.");
            }

            var outputs = new List<ResourceContent>();
            foreach (var item in data.EnumerateArray())
            {
                if (outputs.Count >= _settings.MaxOutputs)
                {
                    throw new InvalidDataException("The image provider returned too many outputs.");
                }

                outputs.Add(ParseImage(item, outputs.Count));
            }

            if (outputs.Count == 0)
            {
                throw new InvalidDataException("The image provider did not return an image.");
            }

            return Array.AsReadOnly(outputs.ToArray());
        }

        private static ResourceContent ParseImage(JsonElement value, int index)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !TryString(value, "b64_json", out var data))
            {
                throw new InvalidDataException("The image provider returned an invalid image.");
            }

            ValidateBase64(data);
            var mediaType = TryString(value, "media_type", out var reported) ? reported : "image/png";
            if (!IsImageMediaType(mediaType))
            {
                throw new InvalidDataException("The image provider returned an invalid media type.");
            }

            return new ResourceContent(
                "data:" + mediaType + ";base64," + data,
                mediaType,
                "image-" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void ValidateBase64(string value)
        {
            if (value.Length == 0
                || value.Length > 32_000_000
                || value.Any(char.IsWhiteSpace))
            {
                throw new InvalidDataException("The image payload was empty, oversized, or invalid base64.");
            }

            var maximumBytes = checked((value.Length / 4 + 1) * 3);
            var rented = ArrayPool<byte>.Shared.Rent(maximumBytes);
            try
            {
                if (!Convert.TryFromBase64String(value, rented, out _))
                {
                    throw new InvalidDataException("The image payload was not valid base64.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static bool IsImageMediaType(string value) =>
            value.Length <= 512
            && value.IndexOfAny(new[] { '\r', '\n', '\0', ';' }) < 0
            && MediaTypeHeaderValue.TryParse(value, out var parsed)
            && parsed.Parameters.Count == 0
            && parsed.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

        private static string Metadata(JsonElement root)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (root.TryGetProperty("created", out var created))
                {
                    writer.WritePropertyName("created");
                    created.WriteTo(writer);
                }

                if (root.TryGetProperty("usage", out var usage))
                {
                    writer.WritePropertyName("usage");
                    usage.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string BoundProviderError(JsonElement root)
        {
            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                if (TryString(error, "code", out var code) && SafeErrorCode(code) is { } safeCode)
                {
                    return "The image provider stream failed (" + safeCode + ").";
                }

                if (TryString(error, "type", out var type) && SafeErrorCode(type) is { } safeType)
                {
                    return "The image provider stream failed (" + safeType + ").";
                }
            }

            return "The image provider stream failed.";
        }

        private static string? RequestId(HttpResponseMessage response)
        {
            foreach (var name in new[] { "x-request-id", "x-openrouter-request-id" })
            {
                if (response.Headers.TryGetValues(name, out var values))
                {
                    var value = values.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(value)
                        && value.Length <= 512
                        && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0)
                    {
                        return value;
                    }
                }
            }

            return null;
        }
    }

    private sealed class Settings
    {
        private Settings(
            HttpClient httpClient,
            Uri endpoint,
            IReadOnlyDictionary<string, string> headers,
            int maxRequestBytes,
            int maxResponseBytes,
            int maxErrorCharacters,
            int maxModels,
            int maxOutputs,
            bool allowInsecureHttp)
        {
            HttpClient = httpClient;
            Endpoint = endpoint;
            Headers = headers;
            MaxRequestBytes = maxRequestBytes;
            MaxResponseBytes = maxResponseBytes;
            MaxErrorCharacters = maxErrorCharacters;
            MaxModels = maxModels;
            MaxOutputs = maxOutputs;
            AllowInsecureHttp = allowInsecureHttp;
        }

        public HttpClient HttpClient { get; }

        public Uri Endpoint { get; }

        public IReadOnlyDictionary<string, string> Headers { get; }

        public int MaxRequestBytes { get; }

        public int MaxResponseBytes { get; }

        public int MaxErrorCharacters { get; }

        public int MaxModels { get; }

        public int MaxOutputs { get; }

        public bool AllowInsecureHttp { get; }

        public static Settings Create(OpenRouterImageProviderOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            ValidateEndpoint(options.Endpoint, options.AllowInsecureHttp);
            if (options.MaxRequestBytes is < 2 or > 100_000_000
                || options.MaxResponseBytes is < 2 or > 100_000_000
                || options.MaxErrorCharacters is < 1 or > 65_536
                || options.MaxModels is < 1 or > 100_000
                || options.MaxOutputs is < 1 or > 10)
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }

            if (options.Headers.Count > 256)
            {
                throw new ArgumentException("The image provider has too many headers.", nameof(options));
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in options.Headers)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)
                    || pair.Key.Length > 256
                    || pair.Value is null
                    || pair.Value.Length > 65_536
                    || pair.Key.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                    || pair.Value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                    || !headers.TryAdd(pair.Key, pair.Value))
                {
                    throw new ArgumentException("The image provider contains an invalid header.", nameof(options));
                }
            }

            return new Settings(
                options.HttpClient,
                options.Endpoint,
                new ReadOnlyDictionary<string, string>(headers),
                options.MaxRequestBytes,
                options.MaxResponseBytes,
                options.MaxErrorCharacters,
                options.MaxModels,
                options.MaxOutputs,
                options.AllowInsecureHttp);
        }

        public void ValidateResolvedEndpoint(Uri endpoint) =>
            ValidateEndpoint(endpoint, AllowInsecureHttp);

        private static void ValidateEndpoint(Uri endpoint, bool allowInsecureHttp)
        {
            if (endpoint is null
                || !endpoint.IsAbsoluteUri
                || endpoint.UserInfo.Length > 0
                || endpoint.Fragment.Length > 0
                || (endpoint.Scheme != Uri.UriSchemeHttps
                    && (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback && !allowInsecureHttp)))
            {
                throw new ArgumentException(
                    "The image provider endpoint must be an absolute HTTPS URL without credentials or a fragment.",
                    nameof(endpoint));
            }
        }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maximumBytes;
        private long _read;

        public BoundedReadStream(Stream inner, long maximumBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _maximumBytes = maximumBytes;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            Record(_inner.Read(buffer, offset, count));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Record(await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Record(int count)
        {
            _read = checked(_read + count);
            if (_read > _maximumBytes)
            {
                throw new InvalidDataException("The image provider response exceeded the configured size limit.");
            }

            return count;
        }
    }
}

using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Media;
using OpenGameAgent.Models;
using OpenGameAgent.Providers.Images.Internal;

namespace OpenGameAgent.Providers.Local;

public sealed class ComfyUiUploadedSource
{
    internal ComfyUiUploadedSource(string fileName, string subfolder, string type, string mediaType)
    {
        FileName = fileName;
        Subfolder = subfolder;
        Type = type;
        MediaType = mediaType;
    }

    public string FileName { get; }
    public string Subfolder { get; }
    public string Type { get; }
    public string MediaType { get; }
}

public sealed class ComfyUiWorkflowContext
{
    internal ComfyUiWorkflowContext(
        GameModelDescriptor model,
        GameMediaGenerationRequest request,
        IReadOnlyList<ComfyUiUploadedSource> sources)
    {
        Model = model;
        Request = request;
        Sources = sources;
    }

    public GameModelDescriptor Model { get; }
    public GameMediaGenerationRequest Request { get; }
    public IReadOnlyList<ComfyUiUploadedSource> Sources { get; }
}

public sealed class ComfyUiWorkflowDefinition
{
    public ComfyUiWorkflowDefinition(string promptJson, string? clientId = null)
    {
        if (string.IsNullOrWhiteSpace(promptJson) || Encoding.UTF8.GetByteCount(promptJson) > 8_000_000)
        {
            throw new ArgumentException("A bounded ComfyUI prompt graph is required.", nameof(promptJson));
        }

        using var document = JsonDocument.Parse(promptJson, new JsonDocumentOptions { MaxDepth = 128 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A ComfyUI prompt graph must be a JSON object.", nameof(promptJson));
        }

        PromptJson = document.RootElement.GetRawText();
        ClientId = clientId is null ? null : RequireId(clientId, nameof(clientId));
    }

    public string PromptJson { get; }
    public string? ClientId { get; }

    private static string RequireId(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded identifier is required.", parameterName)
            : value;
}

public delegate ValueTask<ComfyUiWorkflowDefinition> ComfyUiWorkflowFactory(
    ComfyUiWorkflowContext context,
    CancellationToken cancellationToken);

public sealed class ComfyUiMediaProviderOptions
{
    public ComfyUiMediaProviderOptions(ComfyUiWorkflowFactory createWorkflowAsync)
    {
        CreateWorkflowAsync = createWorkflowAsync
            ?? throw new ArgumentNullException(nameof(createWorkflowAsync));
    }

    public Uri Endpoint { get; set; } = new("http://127.0.0.1:8188");
    public HttpMessageHandler? HttpMessageHandler { get; set; }
    public ComfyUiWorkflowFactory CreateWorkflowAsync { get; }
    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxSources { get; set; } = 16;
    public int MaxSourceBytes { get; set; } = 20_000_000;
    public int MaxAggregateSourceBytes { get; set; } = 50_000_000;
    public int MaxWorkflowBytes { get; set; } = 8_000_000;
    public int MaxResponseBytes { get; set; } = 100_000_000;
    public int MaxOutputBytes { get; set; } = 100_000_000;
    public int MaxOutputs { get; set; } = 32;
    public int MaxPollAttempts { get; set; } = 20_000;
}

public static class ComfyUiMediaProvider
{
    public const string ProviderId = "comfyui";
    public const string ApiId = "comfyui-workflow";

    public static GameMediaProviderRegistration CreateRegistration(
        ComfyUiMediaProviderOptions options,
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
                "ComfyUI",
                settings.Endpoint,
                isLocal: true),
            authentication,
            invocation => new Generator(settings, invocation),
            checkedModels);
    }

    public static GameModelDescriptor CreateModel(
        string modelId,
        GameModelOutputCapabilities outputs,
        GameModelInputCapabilities inputs = GameModelInputCapabilities.Text | GameModelInputCapabilities.Image,
        string? displayName = null)
    {
        var media = outputs
                    & (GameModelOutputCapabilities.Image
                       | GameModelOutputCapabilities.Audio
                       | GameModelOutputCapabilities.Video);
        if (media == 0 || media != outputs)
        {
            throw new ArgumentException("A ComfyUI workflow model must declare only media outputs.", nameof(outputs));
        }

        const GameModelInputCapabilities supportedInputs =
            GameModelInputCapabilities.Text
            | GameModelInputCapabilities.StructuredData
            | GameModelInputCapabilities.Image;
        if (inputs == GameModelInputCapabilities.None || (inputs & ~supportedInputs) != 0)
        {
            throw new ArgumentException(
                "A ComfyUI workflow model can accept only text, structured data, and image inputs.",
                nameof(inputs));
        }

        return new GameModelDescriptor(
            ProviderId,
            modelId,
            displayName,
            inputCapabilities: inputs,
            outputCapabilities: outputs,
            cost: new GameModelCost(isKnown: false),
            api: ApiId);
    }

    private static IReadOnlyList<GameModelDescriptor> ValidateModels(IReadOnlyList<GameModelDescriptor> models)
    {
        if (models is null || models.Count == 0
            || models.Any(model => model is null
                                   || !string.Equals(model.ProviderId, ProviderId, StringComparison.Ordinal)
                                   || !string.Equals(model.Api, ApiId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("At least one valid ComfyUI workflow model is required.", nameof(models));
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
        }

        public async ValueTask<GameMediaGenerationResult> GenerateAsync(
            GameMediaGenerationRequest request,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            RequireOutput(request.Kind);
            if (request.Sources.Count > _settings.MaxSources)
            {
                throw new InvalidDataException("The ComfyUI request has too many source assets.");
            }

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(_settings.Timeout);
            var uploaded = await UploadSourcesAsync(request.Sources, progress, lifetime.Token).ConfigureAwait(false);
            var workflow = await _settings.CreateWorkflowAsync(
                    new ComfyUiWorkflowContext(_invocation.Model, request, uploaded),
                    lifetime.Token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The ComfyUI workflow factory returned null.");
            if (Encoding.UTF8.GetByteCount(workflow.PromptJson) > _settings.MaxWorkflowBytes)
            {
                throw new InvalidDataException("The ComfyUI workflow exceeded its configured limit.");
            }

            var promptId = await SubmitAsync(workflow, lifetime.Token).ConfigureAwait(false);
            try
            {
                if (progress is not null)
                {
                    await progress(
                        new GameMediaGenerationProgress(
                            "queued",
                            detailsJson: JsonSerializer.Serialize(new { providerRequestId = promptId })),
                        lifetime.Token).ConfigureAwait(false);
                }

                return await PollAsync(promptId, request.Kind, progress, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await CancelBestEffortAsync(promptId).ConfigureAwait(false);
                throw;
            }
        }

        private async ValueTask<IReadOnlyList<ComfyUiUploadedSource>> UploadSourcesAsync(
            IReadOnlyList<ResourceContent> sources,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            var uploaded = new List<ComfyUiUploadedSource>();
            long aggregate = 0;
            for (var index = 0; index < sources.Count; index++)
            {
                var source = sources[index];
                if (source is null
                    || !source.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    || !source.Uri.StartsWith("data:" + source.MediaType + ";base64,", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("ComfyUI source assets must be inline PNG, JPEG, or WebP images.");
                }

                var separator = source.Uri.IndexOf(',');
                var bytes = DecodeBounded(source.Uri.Substring(separator + 1), _settings.MaxSourceBytes);
                aggregate = checked(aggregate + bytes.Length);
                if (aggregate > _settings.MaxAggregateSourceBytes)
                {
                    throw new InvalidDataException("ComfyUI source assets exceeded their aggregate byte limit.");
                }

                ValidateImageType(bytes, source.MediaType);
                var extension = source.MediaType.ToLowerInvariant() switch
                {
                    "image/png" => "png",
                    "image/jpeg" => "jpg",
                    "image/webp" => "webp",
                    _ => throw new InvalidDataException("ComfyUI source assets must be PNG, JPEG, or WebP."),
                };
                using var form = new MultipartFormDataContent("oga-" + Guid.NewGuid().ToString("N"));
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(source.MediaType);
                form.Add(content, "image", "source-" + (index + 1).ToString(CultureInfo.InvariantCulture) + "." + extension);
                form.Add(new StringContent("input", Encoding.UTF8), "type");
                form.Add(new StringContent("false", Encoding.UTF8), "overwrite");
                using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint("api/upload/image"))
                {
                    Content = form,
                };
                ApplyHeaders(message);
                using var response = await _settings.HttpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                ValidateResponse(response, message.RequestUri!);
                using var document = await ImageProviderCommon.ReadJsonAsync(
                    response,
                    _settings.MaxResponseBytes,
                    cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                uploaded.Add(new ComfyUiUploadedSource(
                    RequiredString(root, "name"),
                    OptionalString(root, "subfolder") ?? string.Empty,
                    OptionalString(root, "type") ?? "input",
                    source.MediaType));
                if (progress is not null)
                {
                    await progress(
                        new GameMediaGenerationProgress(
                            "uploading",
                            sources.Count == 0 ? 1 : (double)(index + 1) / sources.Count),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return new ReadOnlyCollection<ComfyUiUploadedSource>(uploaded);
        }

        private async ValueTask<string> SubmitAsync(
            ComfyUiWorkflowDefinition workflow,
            CancellationToken cancellationToken)
        {
            using var prompt = JsonDocument.Parse(workflow.PromptJson, new JsonDocumentOptions { MaxDepth = 128 });
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                prompt = prompt.RootElement,
                client_id = workflow.ClientId,
            }, JsonOptions);
            if (body.Length > _settings.MaxWorkflowBytes)
            {
                throw new InvalidDataException("The ComfyUI submission exceeded its configured limit.");
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint("api/prompt"))
            {
                Content = new ByteArrayContent(body),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            ApplyHeaders(message);
            using var response = await _settings.HttpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            ValidateResponse(response, message.RequestUri!);
            using var document = await ImageProviderCommon.ReadJsonAsync(
                response,
                _settings.MaxResponseBytes,
                cancellationToken).ConfigureAwait(false);
            return RequiredString(document.RootElement, "prompt_id");
        }

        private async ValueTask<GameMediaGenerationResult> PollAsync(
            string promptId,
            GameMediaKind kind,
            GameMediaProgressHandler? progress,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < _settings.MaxPollAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var message = new HttpRequestMessage(
                    HttpMethod.Get,
                    Endpoint("api/jobs/" + Uri.EscapeDataString(promptId)));
                ApplyHeaders(message);
                using var response = await _settings.HttpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                ValidateResponse(response, message.RequestUri!);
                using var document = await ImageProviderCommon.ReadJsonAsync(
                    response,
                    _settings.MaxResponseBytes,
                    cancellationToken).ConfigureAwait(false);
                if (TryGetCompletedHistory(document.RootElement, promptId, out var completed))
                {
                    return await ReadOutputsAsync(completed, promptId, kind, cancellationToken).ConfigureAwait(false);
                }

                if (progress is not null)
                {
                    await progress(
                        new GameMediaGenerationProgress(
                            "running",
                            detailsJson: JsonSerializer.Serialize(new { providerRequestId = promptId, poll = attempt + 1 })),
                        cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(_settings.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The ComfyUI job exceeded its bounded poll count.");
        }

        private async ValueTask<GameMediaGenerationResult> ReadOutputsAsync(
            JsonElement completed,
            string promptId,
            GameMediaKind kind,
            CancellationToken cancellationToken)
        {
            if (!completed.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The ComfyUI history entry had no outputs.");
            }

            var result = new List<ResourceContent>();
            foreach (var node in outputs.EnumerateObject())
            {
                if (node.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var collection in node.Value.EnumerateObject())
                {
                    if (collection.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var item in collection.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object
                            || !item.TryGetProperty("filename", out var fileNameElement)
                            || fileNameElement.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        if (result.Count >= _settings.MaxOutputs)
                        {
                            throw new InvalidDataException("The ComfyUI job returned too many outputs.");
                        }

                        var fileName = fileNameElement.GetString()!;
                        var subfolder = OptionalString(item, "subfolder") ?? string.Empty;
                        var type = OptionalString(item, "type") ?? "output";
                        var uri = Endpoint("api/view?filename=" + Uri.EscapeDataString(fileName)
                            + "&subfolder=" + Uri.EscapeDataString(subfolder)
                            + "&type=" + Uri.EscapeDataString(type));
                        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                        ApplyHeaders(message);
                        using var response = await _settings.HttpClient.SendAsync(
                            message,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken).ConfigureAwait(false);
                        ValidateResponse(response, uri);
                        var bytes = await ReadBoundedAsync(
                            response,
                            _settings.MaxOutputBytes,
                            cancellationToken).ConfigureAwait(false);
                        var mediaType = DetectMediaType(bytes, kind);
                        result.Add(new ResourceContent(
                            "data:" + mediaType + ";base64," + Convert.ToBase64String(bytes),
                            mediaType,
                            SafeName(fileName)));
                    }
                }
            }

            if (result.Count == 0)
            {
                throw new InvalidDataException("The ComfyUI job returned no matching media outputs.");
            }

            return new GameMediaGenerationResult(
                new ReadOnlyCollection<ResourceContent>(result),
                JsonSerializer.Serialize(new { outputCount = result.Count }),
                promptId);
        }

        private async ValueTask CancelBestEffortAsync(string promptId)
        {
            try
            {
                using var message = new HttpRequestMessage(
                    HttpMethod.Post,
                    Endpoint("api/jobs/" + Uri.EscapeDataString(promptId) + "/cancel"));
                ApplyHeaders(message);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var response = await _settings.HttpClient.SendAsync(message, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Cancellation is already authoritative; provider cancellation is best effort.
            }
        }

        private void RequireOutput(GameMediaKind kind)
        {
            var capability = kind switch
            {
                GameMediaKind.Image => GameModelOutputCapabilities.Image,
                GameMediaKind.Audio => GameModelOutputCapabilities.Audio,
                GameMediaKind.Video => GameModelOutputCapabilities.Video,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            if (!_invocation.Model.OutputCapabilities.HasFlag(capability))
            {
                throw new InvalidOperationException("The selected ComfyUI workflow does not support this media kind.");
            }
        }

        private void ApplyHeaders(HttpRequestMessage request) =>
            ImageProviderCommon.ApplyHeaders(
                request,
                _settings.Headers,
                _invocation.Headers,
                _invocation.Authentication);

        private void ValidateResponse(HttpResponseMessage response, Uri requested)
        {
            ImageProviderCommon.ValidateResponseOrigin(response, requested);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                throw new InvalidDataException("The ComfyUI service refused a redirect response.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "The ComfyUI service returned HTTP "
                    + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                    + ".");
            }
        }

        private Uri Endpoint(string pathAndQuery)
        {
            var separator = pathAndQuery.IndexOf('?');
            var suffix = separator < 0 ? pathAndQuery : pathAndQuery.Substring(0, separator);
            var query = separator < 0 ? string.Empty : pathAndQuery.Substring(separator + 1);
            var endpoint = _invocation.Endpoint ?? _settings.Endpoint;
            return new UriBuilder(endpoint)
            {
                Path = endpoint.AbsolutePath.TrimEnd('/') + "/" + suffix,
                Query = query,
            }.Uri;
        }
    }

    private static bool TryGetCompletedHistory(JsonElement root, string promptId, out JsonElement completed)
    {
        _ = promptId;
        completed = root;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The ComfyUI job response was not an object.");
        }

        var status = OptionalString(root, "status");
        if (status is "pending" or "in_progress")
        {
            return false;
        }

        if (status is "failed" or "cancelled")
        {
            throw new InvalidOperationException("The ComfyUI workflow did not complete successfully.");
        }

        if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The ComfyUI job response had an unknown status.");
        }

        if (!root.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The completed ComfyUI job had no outputs.");
        }

        return true;
    }

    private static string RequiredString(JsonElement root, string name) =>
        OptionalString(root, name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException("A required ComfyUI response field was missing.");

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || text.Length > 4_096
            || text.Any(character => character is '\0' or '\r' or '\n'))
        {
            throw new InvalidDataException("A ComfyUI response field was invalid.");
        }

        return text;
    }

    private static byte[] DecodeBounded(string encoded, int maximumBytes)
    {
        if (encoded.Length == 0 || encoded.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("A ComfyUI source contained invalid base64.");
        }

        var maximum = checked((encoded.Length / 4 + 1) * 3);
        if (maximum > maximumBytes + 2)
        {
            throw new InvalidDataException("A ComfyUI source exceeded its byte limit.");
        }

        var rented = ArrayPool<byte>.Shared.Rent(maximum);
        try
        {
            if (!Convert.TryFromBase64String(encoded, rented, out var written) || written > maximumBytes)
            {
                throw new InvalidDataException("A ComfyUI source contained invalid or oversized base64.");
            }

            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void ValidateImageType(byte[] bytes, string mediaType)
    {
        var actual = DetectMediaType(bytes, GameMediaKind.Image);
        if (!actual.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A ComfyUI source media type did not match its bytes.");
        }
    }

    private static string DetectMediaType(byte[] bytes, GameMediaKind expected)
    {
        var actual = bytes.Length >= 8
                     && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            ? "image/png"
            : bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff
                ? "image/jpeg"
                : bytes.Length >= 12
                  && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                  && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP"
                    ? "image/webp"
                    : bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 4, 4) == "ftyp"
                        ? "video/mp4"
                        : bytes.Length >= 4
                          && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })
                            ? "video/webm"
                            : bytes.Length >= 12
                              && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                              && Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE"
                                ? "audio/wav"
                                : bytes.Length >= 4 && Encoding.ASCII.GetString(bytes, 0, 4) == "OggS"
                                    ? "audio/ogg"
                                    : bytes.Length >= 4 && Encoding.ASCII.GetString(bytes, 0, 4) == "fLaC"
                                        ? "audio/flac"
                                        : null;
        var valid = expected switch
        {
            GameMediaKind.Image => actual?.StartsWith("image/", StringComparison.Ordinal) == true,
            GameMediaKind.Audio => actual?.StartsWith("audio/", StringComparison.Ordinal) == true,
            GameMediaKind.Video => actual?.StartsWith("video/", StringComparison.Ordinal) == true,
            _ => false,
        };
        return valid ? actual! : throw new InvalidDataException("The ComfyUI output did not match the requested media kind.");
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
        {
            throw new InvalidDataException("The ComfyUI output exceeded its byte limit.");
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
                throw new InvalidDataException("The ComfyUI output exceeded its byte limit.");
            }

            await output.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? SafeName(string value) =>
        value.Length > 512 || value.Any(char.IsControl) ? null : value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Settings
    {
        private Settings(ComfyUiMediaProviderOptions options, Uri endpoint)
        {
            Endpoint = endpoint;
            HttpClient = ImageProviderCommon.CreateClient(options.HttpMessageHandler);
            CreateWorkflowAsync = options.CreateWorkflowAsync;
            Headers = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase));
            PollInterval = options.PollInterval;
            Timeout = options.Timeout;
            MaxSources = options.MaxSources;
            MaxSourceBytes = options.MaxSourceBytes;
            MaxAggregateSourceBytes = options.MaxAggregateSourceBytes;
            MaxWorkflowBytes = options.MaxWorkflowBytes;
            MaxResponseBytes = options.MaxResponseBytes;
            MaxOutputBytes = options.MaxOutputBytes;
            MaxOutputs = options.MaxOutputs;
            MaxPollAttempts = options.MaxPollAttempts;
        }

        public Uri Endpoint { get; }
        public HttpClient HttpClient { get; }
        public ComfyUiWorkflowFactory CreateWorkflowAsync { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public TimeSpan PollInterval { get; }
        public TimeSpan Timeout { get; }
        public int MaxSources { get; }
        public int MaxSourceBytes { get; }
        public int MaxAggregateSourceBytes { get; }
        public int MaxWorkflowBytes { get; }
        public int MaxResponseBytes { get; }
        public int MaxOutputBytes { get; }
        public int MaxOutputs { get; }
        public int MaxPollAttempts { get; }

        public static Settings Create(ComfyUiMediaProviderOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var endpoint = ImageProviderCommon.ValidateEndpoint(options.Endpoint, allowInsecureHttp: true);
            if (!endpoint.IsLoopback)
            {
                throw new ArgumentException("The ComfyUI provider is limited to loopback endpoints.", nameof(options.Endpoint));
            }

            if (options.PollInterval < TimeSpan.FromMilliseconds(10)
                || options.PollInterval > TimeSpan.FromMinutes(1)
                || options.Timeout < TimeSpan.FromSeconds(1) || options.Timeout > TimeSpan.FromHours(24)
                || options.MaxSources is < 0 or > 128
                || options.MaxSourceBytes is < 1 or > 100_000_000
                || options.MaxAggregateSourceBytes is < 1 or > 200_000_000
                || options.MaxWorkflowBytes is < 2 or > 8_000_000
                || options.MaxResponseBytes is < 2 or > 200_000_000
                || options.MaxOutputBytes is < 1 or > 200_000_000
                || options.MaxOutputs is < 1 or > 1_000
                || options.MaxPollAttempts is < 1 or > 100_000)
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }

            return new Settings(options, endpoint);
        }
    }
}

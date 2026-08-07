using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.MediaHttp;

public delegate ValueTask<string?> MediaApiKeyProvider(CancellationToken cancellationToken);

public sealed class HttpMediaGeneratorOptions
{
    public HttpMediaGeneratorOptions(HttpClient httpClient, Uri endpoint)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The media endpoint must be an absolute URI.", nameof(endpoint));
        }


        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The media endpoint must use HTTP or HTTPS.", nameof(endpoint));
        }
    }

    public HttpClient HttpClient { get; }

    public Uri Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public MediaApiKeyProvider? GetApiKeyAsync { get; set; }

    public string ApiKeyHeader { get; set; } = "Authorization";

    public string ApiKeyScheme { get; set; } = "Bearer";

    public int MaxResponseBytes { get; set; } = 8_000_000;

    public int MaxRequestBytes { get; set; } = 8_000_000;

    public int MaxSources { get; set; } = 128;

    public int MaxOutputs { get; set; } = 32;

    public int MaxPollAttempts { get; set; } = 1_000;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public bool RestrictStatusUrlToEndpointOrigin { get; set; } = true;

    public bool SendAuthorizationToCrossOriginStatusUrls { get; set; }
}

public sealed class HttpMediaGenerator : IGameMediaGenerator
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;
    private readonly MediaApiKeyProvider? _getApiKey;
    private readonly string _apiKeyHeader;
    private readonly string _apiKeyScheme;
    private readonly int _maxResponseBytes;
    private readonly int _maxRequestBytes;
    private readonly int _maxSources;
    private readonly int _maxOutputs;
    private readonly int _maxPollAttempts;
    private readonly TimeSpan _pollInterval;
    private readonly bool _restrictStatusOrigin;
    private readonly bool _sendCrossOriginAuthorization;

    public HttpMediaGenerator(HttpMediaGeneratorOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.MaxResponseBytes < 2 || options.MaxResponseBytes > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxResponseBytes));
        }

        if (options.MaxRequestBytes < 2 || options.MaxRequestBytes > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxRequestBytes));
        }

        if (options.MaxSources < 0 || options.MaxSources > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxSources));
        }

        if (options.MaxOutputs < 1 || options.MaxOutputs > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxOutputs));
        }

        if (options.MaxPollAttempts < 1 || options.MaxPollAttempts > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxPollAttempts));
        }

        if (options.PollInterval < TimeSpan.Zero || options.PollInterval > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(options.PollInterval));
        }


        if (options.Endpoint is null
            || !options.Endpoint.IsAbsoluteUri
            || options.Endpoint.UserInfo.Length > 0
            || (!string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The media endpoint must be an absolute HTTP or HTTPS URI.", nameof(options.Endpoint));
        }

        if (!IsValidHeaderName(options.ApiKeyHeader))
        {
            throw new ArgumentException("A valid media API key header is required.", nameof(options.ApiKeyHeader));
        }

        if ((options.ApiKey?.Contains('\r') ?? false)
            || (options.ApiKey?.Contains('\n') ?? false)
            || (options.ApiKeyScheme?.Contains('\r') ?? false)
            || (options.ApiKeyScheme?.Contains('\n') ?? false))
        {
            throw new ArgumentException("Media API credentials cannot contain line breaks.", nameof(options.ApiKey));
        }

        if (options.ApiKey is { Length: > 0 } && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("A configured media API key cannot contain only whitespace.", nameof(options.ApiKey));
        }

        _httpClient = options.HttpClient;
        _endpoint = options.Endpoint;
        _apiKey = options.ApiKey;
        _getApiKey = options.GetApiKeyAsync;
        _apiKeyHeader = string.IsNullOrWhiteSpace(options.ApiKeyHeader)
            ? throw new ArgumentException("A media API key header is required.", nameof(options.ApiKeyHeader))
            : options.ApiKeyHeader;
        _apiKeyScheme = options.ApiKeyScheme ?? string.Empty;
        _maxResponseBytes = options.MaxResponseBytes;
        _maxRequestBytes = options.MaxRequestBytes;
        _maxSources = options.MaxSources;
        _maxOutputs = options.MaxOutputs;
        _maxPollAttempts = options.MaxPollAttempts;
        _pollInterval = options.PollInterval;
        _restrictStatusOrigin = options.RestrictStatusUrlToEndpointOrigin;
        _sendCrossOriginAuthorization = options.SendAuthorizationToCrossOriginStatusUrls;
    }

    private static bool IsValidHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage();
            return request.Headers.TryAddWithoutValidation(name, "value");
        }
        catch (FormatException)
        {
            return false;
        }
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


        if (request.Sources.Count > _maxSources)
        {
            throw new MediaGenerationException("The media generation request has too many source resources.");
        }


        EnsureRequestCanFit(request);

        byte[] requestBytes;
        try
        {
            requestBytes = JsonSerializer.SerializeToUtf8Bytes(new RequestDocument(request), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new MediaGenerationException("The media generation request could not be serialized.", exception);
        }

        if (requestBytes.Length > _maxRequestBytes)
        {
            throw new MediaGenerationException("The media generation request exceeded the configured size limit.");
        }

        using var submit = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        await ApplyAuthorizationAsync(submit, allowAuthorization: true, cancellationToken).ConfigureAwait(false);
        submit.Content = new ByteArrayContent(requestBytes);
        submit.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _httpClient.SendAsync(
            submit,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var current = ParseStatusDocument(document.RootElement, response.StatusCode);

        for (var attempt = 0; ; attempt++)
        {
            if (current.Progress is not null && progress is not null)
            {
                await progress(current.Progress, cancellationToken).ConfigureAwait(false);
            }

            if (current.Status == MediaJobStatus.Completed)
            {
                return ParseResult(current);
            }

            if (current.Status == MediaJobStatus.Failed)
            {
                throw new MediaGenerationException(current.Error ?? "The media generation job failed.");
            }

            if (attempt >= _maxPollAttempts)
            {
                throw new MediaGenerationException("The media generation job exceeded its polling limit.");
            }

            if (current.StatusUrl is null)
            {
                throw new MediaGenerationException("The media service returned a pending job without a status URL.");
            }

            ValidateStatusUrl(current.StatusUrl);
            var delay = current.RetryAfter ?? _pollInterval;
            if (delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(5))
            {
                throw new MediaGenerationException("The media service returned an invalid retry interval.");
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            using var poll = new HttpRequestMessage(HttpMethod.Get, current.StatusUrl);
            await ApplyAuthorizationAsync(
                poll,
                allowAuthorization: IsSameOrigin(current.StatusUrl) || _sendCrossOriginAuthorization,
                cancellationToken).ConfigureAwait(false);
            using var pollResponse = await _httpClient.SendAsync(
                poll,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            using var pollDocument = await ReadDocumentAsync(pollResponse, cancellationToken).ConfigureAwait(false);
            current = ParseStatusDocument(pollDocument.RootElement, pollResponse.StatusCode);
        }
    }

    private void EnsureRequestCanFit(GameMediaGenerationRequest request)
    {
        var lowerBound = 48L;

        void AddBytes(long value)
        {
            lowerBound = checked(lowerBound + value);
            if (lowerBound > _maxRequestBytes)
            {
                throw new MediaGenerationException("The media generation request exceeded the configured size limit.");
            }
        }

        void AddString(string? value)
        {
            if (value is not null)
            {
                AddBytes(System.Text.Encoding.UTF8.GetByteCount(value));
            }
        }

        AddString(request.RequestId);
        AddString(request.Prompt);
        AddString(request.ContextJson);
        AddString(request.ParametersJson);
        foreach (var source in request.Sources)
        {
            AddBytes(32);
            AddString(source.Uri);
            AddString(source.MediaType);
            AddString(source.Name);
        }
    }

    private JobStatus ParseStatusDocument(JsonElement root, HttpStatusCode httpStatus)
    {
        try
        {
            return ParseStatus(root, httpStatus);
        }
        catch (MediaGenerationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidOperationException
                                          or FormatException
                                          or ArgumentException
                                          or OverflowException)
        {
            throw new MediaGenerationException("The media endpoint returned an invalid job document.", exception);
        }
    }

    private GameMediaGenerationResult ParseResult(JobStatus status)
    {
        try
        {
            if (status.Outputs is null || status.Outputs.Count == 0)
            {
                throw new MediaGenerationException("The completed media job did not return outputs.");
            }

            if (status.Outputs.Count > _maxOutputs)
            {
                throw new MediaGenerationException("The media service returned too many outputs.");
            }

            var resources = status.Outputs.Select(output => new ResourceContent(
                output.Uri ?? throw new MediaGenerationException("A media output URI is missing."),
                output.MediaType ?? throw new MediaGenerationException("A media output type is missing."),
                output.Name)).ToArray();
            return new GameMediaGenerationResult(
                resources,
                status.MetadataJson ?? "{}",
                status.ProviderRequestId);
        }
        catch (MediaGenerationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
        {
            throw new MediaGenerationException("The completed media job contains invalid output data.", exception);
        }
    }

    private JobStatus ParseStatus(JsonElement root, HttpStatusCode httpStatus)
    {
        if ((int)httpStatus < 200 || (int)httpStatus >= 300)
        {
            var httpError = root.TryGetProperty("error", out var errorElement)
                ? errorElement.ToString()
                : root.GetRawText();
            throw new MediaGenerationException($"The media endpoint returned HTTP {(int)httpStatus}. {httpError}");
        }

        var statusText = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : "completed";
        var status = statusText?.ToLowerInvariant() switch
        {
            "queued" or "pending" or "running" => MediaJobStatus.Pending,
            "completed" or "succeeded" => MediaJobStatus.Completed,
            "failed" or "error" => MediaJobStatus.Failed,
            _ => throw new MediaGenerationException($"Unsupported media job status '{statusText}'."),
        };
        Uri? statusUrl = null;
        if (root.TryGetProperty("statusUrl", out var statusUrlElement)
            && statusUrlElement.GetString() is { } statusUrlText)
        {
            statusUrl = new Uri(_endpoint, statusUrlText);
        }

        TimeSpan? retry = null;
        if (root.TryGetProperty("retryAfterMs", out var retryElement))
        {
            if (retryElement.ValueKind != JsonValueKind.Number
                || !retryElement.TryGetInt32(out var retryMilliseconds))
            {
                throw new MediaGenerationException("The media retry interval must be an integer number of milliseconds.");
            }

            retry = TimeSpan.FromMilliseconds(retryMilliseconds);
        }

        GameMediaGenerationProgress? progress = null;
        if (root.TryGetProperty("progress", out var progressElement) && progressElement.ValueKind == JsonValueKind.Object)
        {
            progress = new GameMediaGenerationProgress(
                progressElement.TryGetProperty("stage", out var stage) ? stage.GetString() ?? "running" : "running",
                progressElement.TryGetProperty("fraction", out var fraction) ? fraction.GetDouble() : (double?)null,
                progressElement.TryGetProperty("details", out var details) ? details.GetRawText() : null);
        }

        List<OutputDocument>? outputs = null;
        if (root.TryGetProperty("outputs", out var outputElement))
        {
            if (outputElement.ValueKind != JsonValueKind.Array)
            {
                throw new MediaGenerationException("Media outputs must be an array.");
            }

            if (outputElement.GetArrayLength() > _maxOutputs)
            {
                throw new MediaGenerationException("The media service returned too many outputs.");
            }

            outputs = JsonSerializer.Deserialize<List<OutputDocument>>(outputElement.GetRawText(), JsonOptions);
        }

        return new JobStatus(
            status,
            statusUrl,
            retry,
            progress,
            outputs,
            root.TryGetProperty("metadata", out var metadata) ? metadata.GetRawText() : "{}",
            root.TryGetProperty("requestId", out var requestId) ? requestId.GetString() : null,
            root.TryGetProperty("error", out var error) ? error.ToString() : null);
    }

    private void ValidateStatusUrl(Uri statusUrl)
    {
        if (!statusUrl.IsAbsoluteUri)
        {
            throw new MediaGenerationException("The media status URL must be absolute.");
        }

        if (statusUrl.UserInfo.Length > 0)
        {
            throw new MediaGenerationException("The media status URL cannot contain user information.");
        }

        if (!string.Equals(statusUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(statusUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new MediaGenerationException("The media status URL must use HTTP or HTTPS.");
        }

        if (_restrictStatusOrigin && !IsSameOrigin(statusUrl))
        {
            throw new MediaGenerationException("The media status URL points to a different origin.");
        }
    }

    private bool IsSameOrigin(Uri value) =>
        string.Equals(value.Scheme, _endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(value.Host, _endpoint.Host, StringComparison.OrdinalIgnoreCase)
        && value.Port == _endpoint.Port;

    private async ValueTask ApplyAuthorizationAsync(
        HttpRequestMessage request,
        bool allowAuthorization,
        CancellationToken cancellationToken)
    {
        if (!allowAuthorization)
        {
            return;
        }

        var apiKey = _getApiKey is null
            ? _apiKey
            : await _getApiKey(cancellationToken).ConfigureAwait(false);
        if ((apiKey?.Contains('\r') ?? false) || (apiKey?.Contains('\n') ?? false))
        {
            throw new InvalidOperationException("The media API key provider returned a credential containing line breaks.");
        }

        if (apiKey is { Length: > 0 } && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("The media API key provider returned a whitespace-only credential.");
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        var value = string.IsNullOrWhiteSpace(_apiKeyScheme) ? apiKey : _apiKeyScheme + " " + apiKey;
        if (!request.Headers.TryAddWithoutValidation(_apiKeyHeader, value))
        {
            throw new InvalidOperationException("The configured media API key header is invalid.");
        }
    }

    private async ValueTask<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var cancellationRegistration = cancellationToken.Register(source.Dispose);
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(rented, 0, rented.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > _maxResponseBytes)
                {
                    throw new MediaGenerationException("The media endpoint response exceeded the configured size limit.");
                }

                await buffer.WriteAsync(rented, 0, read, cancellationToken).ConfigureAwait(false);
            }

            buffer.Position = 0;
            var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureUnambiguous(document.RootElement);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (JsonException exception)
        {
            throw new MediaGenerationException("The media endpoint returned invalid JSON.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void EnsureUnambiguous(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new MediaGenerationException("The media endpoint returned duplicate JSON property names.");
                }

                EnsureUnambiguous(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUnambiguous(item);
            }
        }
    }

    private sealed class RequestDocument
    {
        public RequestDocument(GameMediaGenerationRequest request)
        {
            RequestId = request.RequestId;
            Kind = request.Kind.ToString().ToLowerInvariant();
            Prompt = request.Prompt;
            Context = ParseElement(request.ContextJson);
            Parameters = ParseElement(request.ParametersJson);
            Sources = request.Sources.Select(source => new OutputDocument
            {
                Uri = source.Uri,
                MediaType = source.MediaType,
                Name = source.Name,
            }).ToArray();
        }

        public string RequestId { get; }

        public string Kind { get; }

        public string? Prompt { get; }

        public JsonElement Context { get; }

        public JsonElement Parameters { get; }

        public IReadOnlyList<OutputDocument> Sources { get; }

        private static JsonElement ParseElement(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class OutputDocument
    {
        public string? Uri { get; set; }

        public string? MediaType { get; set; }

        public string? Name { get; set; }
    }

    private sealed class JobStatus
    {
        public JobStatus(
            MediaJobStatus status,
            Uri? statusUrl,
            TimeSpan? retryAfter,
            GameMediaGenerationProgress? progress,
            List<OutputDocument>? outputs,
            string? metadataJson,
            string? providerRequestId,
            string? error)
        {
            Status = status;
            StatusUrl = statusUrl;
            RetryAfter = retryAfter;
            Progress = progress;
            Outputs = outputs;
            MetadataJson = metadataJson;
            ProviderRequestId = providerRequestId;
            Error = error;
        }

        public MediaJobStatus Status { get; }

        public Uri? StatusUrl { get; }

        public TimeSpan? RetryAfter { get; }

        public GameMediaGenerationProgress? Progress { get; }

        public List<OutputDocument>? Outputs { get; }

        public string? MetadataJson { get; }

        public string? ProviderRequestId { get; }

        public string? Error { get; }
    }

    private enum MediaJobStatus
    {
        Pending,
        Completed,
        Failed,
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

public sealed class MediaGenerationException : Exception
{
    public MediaGenerationException(string message)
        : base(message)
    {
    }

    public MediaGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

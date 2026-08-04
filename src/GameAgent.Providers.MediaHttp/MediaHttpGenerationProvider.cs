using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GameAgent.Generation;

namespace GameAgent.Providers.MediaHttp;

public sealed class MediaHttpGenerationProvider
    : IGenerationProvider,
      IStreamingSpeechProvider,
      IDisposable
{
    private readonly MediaHttpProviderOptions _options;
    private readonly IGenerationCredentialSource? _credentials;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly GenerationProviderCapabilities _capabilities;

    public MediaHttpGenerationProvider(
        MediaHttpProviderOptions options,
        IGenerationCredentialSource? credentials = null,
        HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _credentials = credentials;
        if (httpClient is null)
        {
            _httpClient = new HttpClient(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.GZip
                                             | DecompressionMethods.Deflate
                },
                disposeHandler: true);
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }

        var modalities = new List<string>();
        if (_options.ImagePath is not null)
        {
            modalities.Add(GenerationModalities.Image);
        }

        if (_options.VideoPath is not null)
        {
            modalities.Add(GenerationModalities.Video);
        }

        if (_options.SpeechPath is not null)
        {
            modalities.Add(GenerationModalities.Speech);
        }

        if (_options.StructuredContentPath is not null)
        {
            modalities.Add(GenerationModalities.StructuredContent);
        }

        _capabilities = new GenerationProviderCapabilities
        {
            Modalities = modalities,
            SupportsPolling = _options.VideoPath is not null
                              && _options.VideoStatusPathTemplate is not null
                              || _options.StructuredContentPath is not null
                              && _options.StructuredContentStatusPathTemplate is not null,
            SupportsCancellation = _options.VideoPath is not null
                                   && _options.VideoCancelPathTemplate is not null
                                   || _options.StructuredContentPath is not null
                                   && _options.StructuredContentCancelPathTemplate is not null,
            SupportsStreamingSpeech = _options.SpeechPath is not null
        };
    }

    public string Name => _options.Name;

    public GenerationProviderCapabilities Capabilities => _capabilities;

    public async ValueTask<GenerationSubmission> SubmitAsync(
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        if (!_capabilities.Modalities.Contains(request.Modality))
        {
            throw new GenerationProviderException(
                "generation_modality_not_supported",
                $"Provider '{Name}' does not support '{request.Modality}'.",
                GenerationAcceptance.NotAccepted);
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            _options.Resolve(_options.PathForModality(request.Modality)));
        await AuthorizeAsync(message, cancellationToken).ConfigureAwait(false);
        if (request.IdempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation(
                "Idempotency-Key",
                request.IdempotencyKey);
        }

        message.Headers.TryAddWithoutValidation(
            "X-Client-Operation-Id",
            request.OperationId);
        message.Content = new ByteArrayContent(WriteRequest(request));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json");

        using var response = await SendAsync(message, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        if (request.Modality == GenerationModalities.Speech)
        {
            var bytes = await ReadBoundedAsync(
                    response.Content,
                    _options.MaxInlineArtifactBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType
                            ?? "audio/mpeg";
            return Completed(
                new GenerationArtifactSource
                {
                    InlineData = bytes,
                    MediaType = mediaType,
                    SizeBytes = bytes.Length
                });
        }

        var metadata = await ReadBoundedAsync(
                response.Content,
                _options.MaxMetadataResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(metadata);
            var submission = request.Modality == GenerationModalities.Image
                ? ParseImage(document.RootElement)
                : ParseJob(document.RootElement, request.Modality);
            AddImplicitContentArtifact(
                submission.Result,
                request.Modality,
                document.RootElement);
            return submission;
        }
        catch (Exception exception) when (
            exception is JsonException
                or FormatException
                or InvalidOperationException)
        {
            throw new GenerationProviderException(
                "generation_response_invalid",
                "The media API returned invalid JSON metadata.",
                GenerationAcceptance.Accepted,
                innerException: exception);
        }
    }

    public async ValueTask<GenerationProviderResult> GetAsync(
        string providerJobId,
        string modality,
        CancellationToken cancellationToken)
    {
        var id = EncodeJobId(providerJobId);
        var template = _options.StatusPathTemplateForModality(modality)
                       ?? throw new GenerationProviderException(
                           "generation_polling_not_supported",
                           $"Provider '{Name}' does not expose polling for '{modality}'.",
                           GenerationAcceptance.Accepted);
        var path = template.Replace(
            "{id}",
            id,
            StringComparison.Ordinal);
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            _options.Resolve(path));
        await AuthorizeAsync(message, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(message, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        var metadata = await ReadBoundedAsync(
                response.Content,
                _options.MaxMetadataResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(metadata);
        var result = ParseProviderResult(document.RootElement, modality);
        AddImplicitContentArtifact(result, modality, document.RootElement);

        return result;
    }

    public async ValueTask<GenerationCancelResult> CancelAsync(
        string providerJobId,
        string modality,
        CancellationToken cancellationToken)
    {
        var id = EncodeJobId(providerJobId);
        var template = _options.CancelPathTemplateForModality(modality)
                       ?? throw new GenerationProviderException(
                           "generation_cancellation_not_supported",
                           $"Provider '{Name}' does not expose cancellation for '{modality}'.",
                           GenerationAcceptance.NotAccepted);
        var path = template.Replace(
            "{id}",
            id,
            StringComparison.Ordinal);
        using var message = new HttpRequestMessage(
            HttpMethod.Delete,
            _options.Resolve(path));
        await AuthorizeAsync(message, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(message, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.Conflict)
        {
            return new GenerationCancelResult
            {
                Accepted = false,
                Status = GenerationJobStatuses.Unknown
            };
        }

        EnsureSuccess(response);
        return new GenerationCancelResult
        {
            Accepted = true,
            Status = GenerationJobStatuses.CancelRequested
        };
    }

    public async IAsyncEnumerable<SpeechStreamEvent> StreamSpeechAsync(
        GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_options.SpeechPath is null
            || request.Modality != GenerationModalities.Speech)
        {
            throw new GenerationProviderException(
                "speech_stream_not_supported",
                "This provider does not expose streaming speech.",
                GenerationAcceptance.NotAccepted);
        }

        var stopwatch = Stopwatch.StartNew();
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            _options.Resolve(_options.SpeechPath));
        await AuthorizeAsync(message, cancellationToken).ConfigureAwait(false);
        if (request.IdempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation(
                "Idempotency-Key",
                request.IdempotencyKey);
        }

        message.Content = new ByteArrayContent(WriteRequest(request));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json");
        using var response = await SendAsync(message, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        var mediaType = response.Content.Headers.ContentType?.MediaType
                        ?? "audio/mpeg";
        yield return new SpeechStreamEvent
        {
            Kind = SpeechStreamEventKinds.Started,
            MediaType = mediaType,
            Sequence = 0,
            Elapsed = stopwatch.Elapsed
        };

        await using var stream = await response.Content
            .ReadAsStreamAsync()
            .ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        long total = 0;
        long sequence = 1;
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream
                        .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException)
                {
                    throw new GenerationProviderException(
                        "speech_stream_read_failed",
                        "The speech stream ended unexpectedly.",
                        GenerationAcceptance.Accepted,
                        innerException: exception);
                }

                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > _options.MaxInlineArtifactBytes)
                {
                    throw new GenerationProviderException(
                        "speech_stream_too_large",
                        "The speech stream exceeded the configured byte limit.",
                        GenerationAcceptance.Accepted);
                }

                var bytes = new byte[read];
                Buffer.BlockCopy(buffer, 0, bytes, 0, read);
                yield return new SpeechStreamEvent
                {
                    Kind = SpeechStreamEventKinds.Audio,
                    MediaType = mediaType,
                    Audio = bytes,
                    Sequence = sequence++,
                    Elapsed = stopwatch.Elapsed
                };
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        yield return new SpeechStreamEvent
        {
            Kind = SpeechStreamEventKinds.Completed,
            MediaType = mediaType,
            Sequence = sequence,
            Elapsed = stopwatch.Elapsed
        };
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async ValueTask<HttpResponseMessage> SendAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            return await _httpClient
                .SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new GenerationProviderException(
                "generation_transport_cancelled_uncertain",
                "The media API request was interrupted; provider acceptance is unknown.",
                GenerationAcceptance.Unknown,
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new GenerationProviderException(
                "generation_transport_failed_uncertain",
                "The media API connection failed; provider acceptance is unknown.",
                GenerationAcceptance.Unknown,
                retryable: true,
                innerException: exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new GenerationProviderException(
                "generation_redirect_rejected",
                "Media API redirects are disabled.",
                GenerationAcceptance.NotAccepted);
        }

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var notAccepted = status is >= 400 and < 500 && status != 408;
        throw new GenerationProviderException(
            "generation_http_" + status.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            $"The media API returned HTTP {status}.",
            notAccepted
                ? GenerationAcceptance.NotAccepted
                : GenerationAcceptance.Unknown,
            retryable: status is 408 or 429 or >= 500);
    }

    private async ValueTask AuthorizeAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        if (_credentials is null)
        {
            return;
        }

        var value = await _credentials
            .GetCredentialAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new GenerationProviderException(
                "generation_credential_unavailable",
                "The media API credential source returned no credential.",
                GenerationAcceptance.NotAccepted);
        }

        if (value.Length > 16_384 || value.Any(char.IsControl))
        {
            throw new GenerationProviderException(
                "generation_credential_invalid",
                "The media API credential is invalid.",
                GenerationAcceptance.NotAccepted);
        }

        if (_options.AuthorizationHeader == "Authorization")
        {
            message.Headers.Authorization = new AuthenticationHeaderValue(
                _options.AuthorizationScheme,
                value);
        }
        else
        {
            message.Headers.TryAddWithoutValidation("x-api-key", value);
        }
    }

    private static byte[] WriteRequest(GenerationRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (request.Model is not null)
            {
                writer.WriteString("model", request.Model);
            }

            if (request.Input.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in request.Input.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    if (property.NameEquals("model"))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }
            else
            {
                writer.WritePropertyName("input");
                request.Input.WriteTo(writer);
            }

            foreach (var pair in request.Options.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                if (pair.Key == "model"
                    || request.Input.ValueKind == JsonValueKind.Object
                    && request.Input.TryGetProperty(pair.Key, out _))
                {
                    continue;
                }

                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private GenerationSubmission ParseImage(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() is < 1 or > 1_024)
        {
            throw new JsonException("Image response is missing bounded data.");
        }

        var artifacts = new List<GenerationArtifactSource>();
        foreach (var item in data.EnumerateArray())
        {
            var mediaType = ReadString(item, "media_type", "image/png");
            if (item.TryGetProperty("b64_json", out var encoded)
                && encoded.ValueKind == JsonValueKind.String)
            {
                var value = encoded.GetString()!;
                if (value.Length > checked(_options.MaxInlineArtifactBytes * 2))
                {
                    throw new GenerationProviderException(
                        "generation_inline_artifact_too_large",
                        "An inline image exceeds the configured byte limit.",
                        GenerationAcceptance.Accepted);
                }

                var bytes = Convert.FromBase64String(value);
                if (bytes.Length > _options.MaxInlineArtifactBytes)
                {
                    throw new GenerationProviderException(
                        "generation_inline_artifact_too_large",
                        "An inline image exceeds the configured byte limit.",
                        GenerationAcceptance.Accepted);
                }

                artifacts.Add(new GenerationArtifactSource
                {
                    InlineData = bytes,
                    MediaType = mediaType,
                    SizeBytes = bytes.Length
                });
            }
            else if (item.TryGetProperty("url", out var url)
                     && url.ValueKind == JsonValueKind.String)
            {
                artifacts.Add(new GenerationArtifactSource
                {
                    RemoteUri = new Uri(url.GetString()!, UriKind.Absolute),
                    MediaType = mediaType,
                    ExpiresAt = ReadOptionalDate(item, "expires_at")
                });
            }
            else
            {
                throw new JsonException("Image item has no artifact source.");
            }
        }

        return new GenerationSubmission
        {
            Acceptance = GenerationAcceptance.Accepted,
            Result = new GenerationProviderResult
            {
                Status = GenerationJobStatuses.Succeeded,
                Progress = 1,
                Output = ImageOutputWithoutInlineBytes(root),
                Artifacts = artifacts
            }
        };
    }

    private static JsonElement ImageOutputWithoutInlineBytes(JsonElement root)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject().OrderBy(
                         property => property.Name,
                         StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals("data")
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        foreach (var itemProperty in item.EnumerateObject().OrderBy(
                                     value => value.Name,
                                     StringComparer.Ordinal))
                        {
                            if (itemProperty.NameEquals("b64_json"))
                            {
                                continue;
                            }

                            writer.WritePropertyName(itemProperty.Name);
                            itemProperty.Value.WriteTo(writer);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    private static GenerationSubmission ParseJob(
        JsonElement root,
        string modality) =>
        new()
        {
            Acceptance = GenerationAcceptance.Accepted,
            Result = ParseProviderResult(root, modality)
        };

    private static GenerationProviderResult ParseProviderResult(
        JsonElement root,
        string? modality)
    {
        var rawStatus = ReadString(root, "status", "queued");
        var status = rawStatus.ToLowerInvariant() switch
        {
            "queued" or "pending" => GenerationJobStatuses.Queued,
            "running" or "processing" or "in_progress" =>
                GenerationJobStatuses.Running,
            "completed" or "succeeded" or "success" =>
                GenerationJobStatuses.Succeeded,
            "failed" or "error" => GenerationJobStatuses.Failed,
            "cancelled" or "canceled" => GenerationJobStatuses.Cancelled,
            _ => GenerationJobStatuses.Unknown
        };
        var artifacts = ExtractArtifacts(root, modality);
        return new GenerationProviderResult
        {
            Status = status,
            ProviderJobId = ReadOptionalString(root, "id")
                            ?? ReadOptionalString(root, "job_id"),
            Progress = ReadProgress(root),
            Output = root.TryGetProperty("output", out var output)
                ? output.Clone()
                : root.Clone(),
            Artifacts = artifacts,
            ErrorCode = ReadOptionalString(root, "error_code"),
            ErrorMessage = ReadError(root),
            Retryable = root.TryGetProperty("retryable", out var retryable)
                        && retryable.ValueKind is JsonValueKind.True,
            CostUsd = ReadOptionalString(root, "cost_usd")
        };
    }

    private static IReadOnlyList<GenerationArtifactSource> ExtractArtifacts(
        JsonElement root,
        string? modality)
    {
        var sources = new List<GenerationArtifactSource>();
        if (root.TryGetProperty("artifacts", out var artifacts)
            && artifacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var artifact in artifacts.EnumerateArray().Take(1_024))
            {
                if (artifact.TryGetProperty("url", out var url)
                    && url.ValueKind == JsonValueKind.String)
                {
                    sources.Add(new GenerationArtifactSource
                    {
                        RemoteUri = new Uri(url.GetString()!, UriKind.Absolute),
                        MediaType = ReadString(
                            artifact,
                            "media_type",
                            DefaultMediaType(modality)),
                        Sha256 = ReadOptionalString(artifact, "sha256"),
                        SizeBytes = artifact.TryGetProperty("size_bytes", out var size)
                                    && size.TryGetInt64(out var parsedSize)
                            ? parsedSize
                            : null,
                        ExpiresAt = ReadOptionalDate(artifact, "expires_at")
                    });
                }
            }
        }

        return sources;
    }

    private static string DefaultMediaType(string? modality) => modality switch
    {
        GenerationModalities.Image => "image/png",
        GenerationModalities.Video => "video/mp4",
        GenerationModalities.Speech => "audio/mpeg",
        _ => "application/octet-stream"
    };

    private void AddImplicitContentArtifact(
        GenerationProviderResult result,
        string modality,
        JsonElement metadata)
    {
        if (result.Status != GenerationJobStatuses.Succeeded
            || result.Artifacts.Count != 0
            || result.ProviderJobId is null)
        {
            return;
        }

        var template = _options.ContentPathTemplateForModality(modality);
        if (template is null)
        {
            return;
        }

        var contentPath = template.Replace(
            "{id}",
            EncodeJobId(result.ProviderJobId),
            StringComparison.Ordinal);
        result.Artifacts = new[]
        {
            new GenerationArtifactSource
            {
                RemoteUri = _options.Resolve(contentPath),
                MediaType = ReadString(
                    metadata,
                    "media_type",
                    DefaultMediaType(modality)),
                AuthorizationReference = _options.ArtifactAuthorizationReference
            }
        };
    }

    private static double? ReadProgress(JsonElement root)
    {
        if (!root.TryGetProperty("progress", out var progress)
            || progress.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var value = progress.GetDouble();
        return value > 1 && value <= 100 ? value / 100 : value;
    }

    private static string? ReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        return null;
    }

    private static string ReadString(
        JsonElement root,
        string name,
        string fallback) =>
        ReadOptionalString(root, name) ?? fallback;

    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadOptionalDate(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out var date))
        {
            return date;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return null;
    }

    private static GenerationSubmission Completed(
        GenerationArtifactSource artifact) =>
        new()
        {
            Acceptance = GenerationAcceptance.Accepted,
            Result = new GenerationProviderResult
            {
                Status = GenerationJobStatuses.Succeeded,
                Progress = 1,
                Artifacts = new[] { artifact }
            }
        };

    private static async ValueTask<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new GenerationProviderException(
                "generation_response_too_large",
                $"The media API response exceeds {maximumBytes} bytes.",
                GenerationAcceptance.Accepted);
        }

        await using var stream = await content.ReadAsStreamAsync()
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (true)
            {
                var read = await stream
                    .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > maximumBytes)
                {
                    throw new GenerationProviderException(
                        "generation_response_too_large",
                        $"The media API response exceeds {maximumBytes} bytes.",
                        GenerationAcceptance.Accepted);
                }

                destination.Write(buffer, 0, read);
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string EncodeJobId(string providerJobId)
    {
        if (string.IsNullOrWhiteSpace(providerJobId)
            || providerJobId.Length > 256)
        {
            throw new ArgumentException(
                "Provider job identity is invalid.",
                nameof(providerJobId));
        }

        return Uri.EscapeDataString(providerJobId);
    }
}

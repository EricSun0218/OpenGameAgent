using System.Buffers;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Kernel;
using OpenGameAgent.Models;

namespace OpenGameAgent.Providers.Images.Internal;

internal sealed class ImageProviderLimits
{
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

    public void Validate()
    {
        Range(MaxReferences, 0, 128, nameof(MaxReferences));
        Range(MaxReferenceBytes, 1, 100_000_000, nameof(MaxReferenceBytes));
        Range(MaxAggregateReferenceBytes, 1, 200_000_000, nameof(MaxAggregateReferenceBytes));
        Range(MaxRequestBytes, 1, 300_000_000, nameof(MaxRequestBytes));
        Range(MaxResponseBytes, 1, 300_000_000, nameof(MaxResponseBytes));
        Range(MaxOutputBytes, 1, 100_000_000, nameof(MaxOutputBytes));
        Range(MaxOutputs, 1, 32, nameof(MaxOutputs));
        Range(MaxPromptBytes, 1, 16_000_000, nameof(MaxPromptBytes));
        if (MaxPixels is < 1 or > 268_435_456)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPixels));
        }

        if (Timeout < TimeSpan.FromMilliseconds(100) || Timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }
    }

    private static void Range(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

internal readonly struct DecodedImage
{
    public DecodedImage(string mediaType, string extension, byte[] bytes, int width, int height)
    {
        MediaType = mediaType;
        Extension = extension;
        Bytes = bytes;
        Width = width;
        Height = height;
    }

    public string MediaType { get; }
    public string Extension { get; }
    public byte[] Bytes { get; }
    public int Width { get; }
    public int Height { get; }
}

internal static class ImageProviderCommon
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HttpClient SharedHttpClient = CreateDefaultClient();

    public static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        if (handler is not null)
        {
            return new HttpClient(handler, disposeHandler: false)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
        }

        return SharedHttpClient;
    }

    private static HttpClient CreateDefaultClient() =>
        new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    public static Uri ValidateEndpoint(Uri endpoint, bool allowInsecureHttp)
    {
        if (endpoint is null
            || !endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length > 0
            || endpoint.Fragment.Length > 0
            || endpoint.OriginalString.Length > 4096
            || endpoint.OriginalString.Any(char.IsControl)
            || endpoint.Scheme != Uri.UriSchemeHttps
               && (endpoint.Scheme != Uri.UriSchemeHttp
                   || !allowInsecureHttp
                   || !IsLoopback(endpoint.Host)))
        {
            throw new ArgumentException(
                "The image endpoint must be an absolute HTTPS URI without credentials or a fragment; HTTP is limited to explicitly enabled loopback endpoints.",
                nameof(endpoint));
        }

        return endpoint;
    }

    public static Uri ResolveEndpoint(Uri configured, GameProviderAuthResolution? authentication, bool allowHttp) =>
        ValidateEndpoint(authentication?.BaseUrl ?? configured, allowHttp);

    public static void ApplyHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> configured,
        IReadOnlyDictionary<string, string> model,
        GameProviderAuthResolution? authentication)
    {
        var headers = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in model)
        {
            headers[pair.Key] = pair.Value;
        }

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

        if (authentication?.Credential is { } credential
            && !headers.ContainsKey("Authorization")
            && !suppressed.Contains("Authorization"))
        {
            headers["Authorization"] = "Bearer " + credential.Secret;
        }

        foreach (var pair in headers)
        {
            if (IsReservedHeader(pair.Key)
                || pair.Value.Length > 16_384
                || pair.Value.Any(character => character is '\r' or '\n' or '\0')
                || !request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                throw new InvalidOperationException("The image provider configuration contained an invalid or reserved header.");
            }
        }
    }

    public static IReadOnlyList<DecodedImage> DecodeSources(
        IReadOnlyList<ResourceContent> sources,
        ImageProviderLimits limits)
    {
        if (sources.Count > limits.MaxReferences)
        {
            throw new InvalidDataException("The image request exceeded the reference image count limit.");
        }

        var decoded = new List<DecodedImage>(sources.Count);
        long aggregate = 0;
        foreach (var source in sources)
        {
            if (source is null || !TryNormalizeMediaType(source.MediaType, out var mediaType, out var extension))
            {
                throw new InvalidDataException("Every image reference must use PNG, JPEG, or WebP.");
            }

            var prefix = "data:" + source.MediaType + ";base64,";
            if (!source.Uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Image references must be matching inline base64 data URLs resolved by the host.");
            }

            var encoded = source.Uri.Substring(prefix.Length);
            if (encoded.Length == 0 || encoded.Any(char.IsWhiteSpace))
            {
                throw new InvalidDataException("An image reference contained invalid base64.");
            }

            var maximum = checked((encoded.Length / 4 + 1) * 3);
            if (maximum > limits.MaxReferenceBytes + 2)
            {
                throw new InvalidDataException("An image reference exceeded the configured byte limit.");
            }

            var rented = ArrayPool<byte>.Shared.Rent(maximum);
            try
            {
                if (!Convert.TryFromBase64String(encoded, rented, out var written)
                    || written > limits.MaxReferenceBytes)
                {
                    throw new InvalidDataException("An image reference contained invalid or oversized base64.");
                }

                aggregate = checked(aggregate + written);
                if (aggregate > limits.MaxAggregateReferenceBytes)
                {
                    throw new InvalidDataException("The image references exceeded the aggregate byte limit.");
                }

                var bytes = rented.AsSpan(0, written).ToArray();
                var dimensions = ValidateImage(bytes, mediaType, limits.MaxPixels);
                decoded.Add(new DecodedImage(mediaType, extension, bytes, dimensions.Width, dimensions.Height));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        return new ReadOnlyCollection<DecodedImage>(decoded);
    }

    public static GameMediaGenerationResult ParseResult(
        JsonDocument document,
        string? expectedMediaType,
        ImageProviderLimits limits,
        string? requestId)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The image provider returned an invalid response envelope.");
        }

        var outputs = new List<ResourceContent>();
        foreach (var item in data.EnumerateArray())
        {
            if (outputs.Count >= limits.MaxOutputs)
            {
                throw new InvalidDataException("The image provider returned too many outputs.");
            }

            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("b64_json", out var encodedElement)
                || encodedElement.ValueKind != JsonValueKind.String
                || encodedElement.GetString() is not { Length: > 0 } encoded
                || encoded.Any(char.IsWhiteSpace))
            {
                throw new InvalidDataException("The image provider returned an invalid base64 output.");
            }

            var maximum = checked((encoded.Length / 4 + 1) * 3);
            if (maximum > limits.MaxOutputBytes + 2)
            {
                throw new InvalidDataException("An image output exceeded the configured byte limit.");
            }

            var rented = ArrayPool<byte>.Shared.Rent(maximum);
            try
            {
                if (!Convert.TryFromBase64String(encoded, rented, out var written)
                    || written > limits.MaxOutputBytes)
                {
                    throw new InvalidDataException("The image provider returned invalid or oversized base64.");
                }

                var bytes = rented.AsSpan(0, written).ToArray();
                var actual = DetectMediaType(bytes);
                if (actual is null)
                {
                    throw new InvalidDataException("The image provider returned unsupported image bytes.");
                }

                ValidateImage(bytes, actual, limits.MaxPixels);
                if (expectedMediaType is not null
                    && !string.Equals(expectedMediaType, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The image output format did not match the requested format.");
                }

                outputs.Add(new ResourceContent("data:" + actual + ";base64," + encoded, actual));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        if (outputs.Count == 0)
        {
            throw new InvalidDataException("The image provider returned no outputs.");
        }

        using var metadataBuffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(metadataBuffer))
        {
            writer.WriteStartObject();
            if (root.TryGetProperty("created", out var created) && created.TryGetInt64(out var createdValue))
            {
                writer.WriteNumber("created", createdValue);
            }

            writer.WriteNumber("outputCount", outputs.Count);
            writer.WriteEndObject();
        }

        return new GameMediaGenerationResult(
            new ReadOnlyCollection<ResourceContent>(outputs),
            StrictUtf8.GetString(metadataBuffer.ToArray()),
            SafeIdentifier(requestId));
    }

    public static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            && !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The image provider response must use a JSON content type.");
        }

        using var stream = await AwaitStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBytesAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The image provider returned invalid JSON.", exception);
        }
    }

    public static async ValueTask<Exception> CreateResponseExceptionAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        string? code = null;
        try
        {
            using var document = await ReadJsonAsync(
                response,
                Math.Min(maximumBytes, 65_536),
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                code = ReadErrorCode(error);
            }

            code ??= root.ValueKind == JsonValueKind.Object ? ReadErrorCode(root) : null;
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException)
        {
            // Provider bodies are intentionally not reflected into public errors.
        }

        var suffix = code is null ? string.Empty : " (" + code + ")";
        return new InvalidOperationException(
            $"The image provider returned HTTP {(int)response.StatusCode}.{suffix}");
    }

    public static void ValidateResponseOrigin(HttpResponseMessage response, Uri requested)
    {
        var actual = response.RequestMessage?.RequestUri;
        if (actual is not null && !SameOrigin(actual, requested))
        {
            throw new InvalidDataException("The image provider refused a cross-origin redirected response.");
        }
    }

    public static string? RequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "x-request-id", "request-id", "x-tt-logid" })
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                return SafeIdentifier(values.FirstOrDefault());
            }
        }

        return null;
    }

    public static string RequirePrompt(string? prompt, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(prompt)
            || Encoding.UTF8.GetByteCount(prompt) > maximumBytes)
        {
            throw new InvalidDataException("The image request requires a non-empty prompt within the configured limit.");
        }

        return prompt;
    }

    public static (int Width, int Height) ParseSize(string value, long maximumPixels)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32)
        {
            throw new InvalidDataException("The image size is invalid.");
        }

        var separator = value.IndexOf('x');
        if (separator <= 0
            || !int.TryParse(value.Substring(0, separator), out var width)
            || !int.TryParse(value.Substring(separator + 1), out var height)
            || width is < 64 or > 16_384
            || height is < 64 or > 16_384
            || checked((long)width * height) > maximumPixels)
        {
            throw new InvalidDataException("The image size is outside the configured dimensions or pixel limit.");
        }

        return (width, height);
    }

    public static string OutputMediaType(string format) => format.ToLowerInvariant() switch
    {
        "png" => "image/png",
        "jpeg" or "jpg" => "image/jpeg",
        "webp" => "image/webp",
        _ => throw new InvalidDataException("The image output format must be png, jpeg, or webp."),
    };

    public static void EnsureRequestSize(HttpRequestMessage request, int maximumBytes)
    {
        if (request.Content?.Headers.ContentLength is { } length && length > maximumBytes)
        {
            throw new InvalidDataException("The image request exceeded the configured byte limit.");
        }
    }

    private static async Task<Stream> AwaitStreamAsync(HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = content.ReadAsStreamAsync();
        if (!task.IsCompleted)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                canceled);
            if (task != await Task.WhenAny(task, canceled.Task).ConfigureAwait(false))
            {
                _ = task.ContinueWith(
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

        var stream = await task.ConfigureAwait(false);
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
        using var registration = cancellationToken.Register(
            static state =>
            {
                try
                {
                    ((Stream)state!).Dispose();
                }
                catch (Exception)
                {
                }
            },
            stream);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    cancellationToken.IsCancellationRequested
                    && exception is ObjectDisposedException or IOException)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException("The image provider response exceeded the configured byte limit.");
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string? ReadErrorCode(JsonElement value)
    {
        foreach (var name in new[] { "code", "type" })
        {
            if (value.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                return SafeIdentifier(property.GetString());
            }
        }

        return null;
    }

    private static string? SafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return null;
        }

        return value;
    }

    private static bool IsReservedHeader(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static bool TryNormalizeMediaType(string value, out string mediaType, out string extension)
    {
        switch (value.ToLowerInvariant())
        {
            case "image/png":
                mediaType = "image/png";
                extension = "png";
                return true;
            case "image/jpeg":
            case "image/jpg":
                mediaType = "image/jpeg";
                extension = "jpg";
                return true;
            case "image/webp":
                mediaType = "image/webp";
                extension = "webp";
                return true;
            default:
                mediaType = string.Empty;
                extension = string.Empty;
                return false;
        }
    }

    private static string? DetectMediaType(byte[] bytes)
    {
        if (bytes.Length >= 24
            && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return "image/png";
        }

        if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 16
            && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
            && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP")
        {
            return "image/webp";
        }

        return null;
    }

    private static (int Width, int Height) ValidateImage(byte[] bytes, string mediaType, long maxPixels)
    {
        var actual = DetectMediaType(bytes);
        if (!string.Equals(actual, mediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Image MIME metadata did not match the encoded bytes.");
        }

        var dimensions = mediaType switch
        {
            "image/png" => PngDimensions(bytes),
            "image/jpeg" => JpegDimensions(bytes),
            "image/webp" => WebpDimensions(bytes),
            _ => throw new InvalidDataException("The image format is unsupported."),
        };
        if (dimensions.Width <= 0
            || dimensions.Height <= 0
            || checked((long)dimensions.Width * dimensions.Height) > maxPixels)
        {
            throw new InvalidDataException("The image dimensions exceeded the configured pixel limit.");
        }

        return dimensions;
    }

    private static (int Width, int Height) PngDimensions(byte[] bytes)
    {
        if (bytes.Length < 24)
        {
            throw new InvalidDataException("The PNG image was truncated.");
        }

        return (ReadBigEndianInt32(bytes, 16), ReadBigEndianInt32(bytes, 20));
    }

    private static (int Width, int Height) JpegDimensions(byte[] bytes)
    {
        var offset = 2;
        while (offset + 8 < bytes.Length)
        {
            if (bytes[offset++] != 0xff)
            {
                continue;
            }

            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            if (offset + 2 > bytes.Length)
            {
                break;
            }

            var length = bytes[offset] << 8 | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length)
            {
                break;
            }

            if (marker is >= 0xc0 and <= 0xc3
                || marker is >= 0xc5 and <= 0xc7
                || marker is >= 0xc9 and <= 0xcb
                || marker is >= 0xcd and <= 0xcf)
            {
                if (length < 7)
                {
                    break;
                }

                return (bytes[offset + 5] << 8 | bytes[offset + 6], bytes[offset + 3] << 8 | bytes[offset + 4]);
            }

            offset += length;
        }

        throw new InvalidDataException("The JPEG image dimensions could not be read.");
    }

    private static (int Width, int Height) WebpDimensions(byte[] bytes)
    {
        var kind = Encoding.ASCII.GetString(bytes, 12, 4);
        if (kind == "VP8X" && bytes.Length >= 30)
        {
            return (1 + ReadLittleEndian24(bytes, 24), 1 + ReadLittleEndian24(bytes, 27));
        }

        if (kind == "VP8L" && bytes.Length >= 25 && bytes[20] == 0x2f)
        {
            var bits = BitConverter.ToUInt32(bytes, 21);
            return ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
        }

        if (kind == "VP8 " && bytes.Length >= 30 && bytes[23] == 0x9d && bytes[24] == 0x01 && bytes[25] == 0x2a)
        {
            return ((bytes[26] | bytes[27] << 8) & 0x3fff, (bytes[28] | bytes[29] << 8) & 0x3fff);
        }

        throw new InvalidDataException("The WebP image dimensions could not be read.");
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset) =>
        bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];

    private static int ReadLittleEndian24(byte[] bytes, int offset) =>
        bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;
}

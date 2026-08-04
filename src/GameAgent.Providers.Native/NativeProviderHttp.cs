using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using GameAgent.Core;

namespace GameAgent.Providers.Native;

public interface INativeApiCredentialSource
{
    ValueTask<string> GetCredentialAsync(
        CancellationToken cancellationToken);
}

public sealed class StaticNativeApiCredentialSource :
    INativeApiCredentialSource
{
    private readonly string _credential;

    public StaticNativeApiCredentialSource(string credential)
    {
        _credential = NativeCredential.Validate(
            credential,
            nameof(credential));
    }

    public ValueTask<string> GetCredentialAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<string>(_credential);
    }
}

public sealed class NativeProviderHttpRequest
{
    public Uri Uri { get; set; } = new("https://localhost");

    public string CredentialHeaderName { get; set; } = string.Empty;

    public string CredentialHeaderValue { get; set; } = string.Empty;

    public byte[] Body { get; set; } = Array.Empty<byte>();

    public string ContentType { get; set; } =
        "application/json; charset=utf-8";
}

public interface INativeProviderHttpResponse : IDisposable
{
    int StatusCode { get; }

    Stream Content { get; }

    string? GetHeader(string name);
}

public interface INativeProviderHttpTransport
{
    ValueTask<INativeProviderHttpResponse> SendAsync(
        NativeProviderHttpRequest request,
        CancellationToken cancellationToken);
}

public sealed class HttpClientNativeProviderTransport :
    INativeProviderHttpTransport,
    IDisposable
{
    private readonly HttpClient _client;

    public HttpClientNativeProviderTransport()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _client = new HttpClient(handler, disposeHandler: true);
    }

    public async ValueTask<INativeProviderHttpResponse> SendAsync(
        NativeProviderHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        NativeEndpoint.ValidateDispatchUri(request.Uri, nameof(request));
        if (request.Body is null)
        {
            throw new ArgumentException(
                "The request body cannot be null.",
                nameof(request));
        }

        var headerName = request.CredentialHeaderName;
        if (!string.Equals(
                headerName,
                "Authorization",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                headerName,
                "x-goog-api-key",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The credential header is unsupported.",
                nameof(request));
        }

        MediaTypeHeaderValue contentType;
        try
        {
            contentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The provider content type is invalid.",
                nameof(request),
                exception);
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            request.Uri);
        message.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (string.Equals(
                headerName,
                "Authorization",
                StringComparison.OrdinalIgnoreCase))
        {
            const string prefix = "Bearer ";
            var headerValue = request.CredentialHeaderValue;
            if (headerValue is null
                || !headerValue.StartsWith(
                    prefix,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Authorization credentials must use Bearer authentication.",
                    nameof(request));
            }

            message.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                NativeCredential.Validate(
                    headerValue.Substring(prefix.Length),
                    nameof(request)));
        }
        else
        {
            var credential = NativeCredential.ValidateHeaderValue(
                request.CredentialHeaderValue,
                nameof(request));
            message.Headers.Add(headerName, credential);
        }

        message.Content = new ByteArrayContent(request.Body);
        message.Content.Headers.ContentType = contentType;
        var response = await _client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var content = await response.Content
                .ReadAsStreamAsync()
                .ConfigureAwait(false);
            return new Response(response, content);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public void Dispose() => _client.Dispose();

    private sealed class Response : INativeProviderHttpResponse
    {
        private readonly HttpResponseMessage _response;

        public Response(HttpResponseMessage response, Stream content)
        {
            _response = response;
            Content = content;
        }

        public int StatusCode => (int)_response.StatusCode;

        public Stream Content { get; }

        public string? GetHeader(string name)
        {
            if (_response.Headers.TryGetValues(name, out var values)
                || _response.Content.Headers.TryGetValues(name, out values))
            {
                return values.FirstOrDefault();
            }

            return null;
        }

        public void Dispose() => _response.Dispose();
    }
}

internal static class NativeCredential
{
    public static string Validate(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A provider credential is required.",
                parameterName);
        }

        var result = value.Trim();
        if (result.Length > 8_192
            || result.Any(character => char.IsControl(character)
                                       || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException(
                "The provider credential is invalid.",
                parameterName);
        }

        return result;
    }

    public static string ValidateHeaderValue(
        string? value,
        string parameterName)
    {
        var result = Validate(value, parameterName);
        if (result.IndexOf('\r') >= 0 || result.IndexOf('\n') >= 0)
        {
            throw new ArgumentException(
                "The provider credential header is invalid.",
                parameterName);
        }

        return result;
    }
}

internal static class NativeEndpoint
{
    public static Uri Build(
        Uri baseUri,
        string path,
        bool allowInsecureLoopback,
        string parameterName)
    {
        if (baseUri is null || !baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The provider base URI must be absolute.",
                parameterName);
        }

        if (!string.Equals(
                baseUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            && (!allowInsecureLoopback
                || !string.Equals(
                    baseUri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                || !baseUri.IsLoopback))
        {
            throw new ArgumentException(
                "Remote provider endpoints must use HTTPS.",
                parameterName);
        }

        if (!string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.IndexOf('\\') >= 0
            || path.IndexOf('?') >= 0
            || path.IndexOf('#') >= 0
            || path.Any(char.IsControl)
            || HasTraversal(path))
        {
            throw new ArgumentException(
                "The provider endpoint boundary is invalid.",
                parameterName);
        }

        var normalizedBase = new Uri(
            baseUri.AbsoluteUri.TrimEnd('/') + "/",
            UriKind.Absolute);
        var endpoint = new Uri(normalizedBase, path.Substring(1));
        if (!string.Equals(
                endpoint.Scheme,
                normalizedBase.Scheme,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                endpoint.IdnHost,
                normalizedBase.IdnHost,
                StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != normalizedBase.Port
            || !endpoint.AbsolutePath.StartsWith(
                normalizedBase.AbsolutePath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The provider path escapes its base URI.",
                parameterName);
        }

        return endpoint;
    }

    public static void ValidateDispatchUri(Uri? uri, string parameterName)
    {
        if (uri is null
            || !uri.IsAbsoluteUri
            || (!string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(
                        uri.Scheme,
                        Uri.UriSchemeHttp,
                        StringComparison.OrdinalIgnoreCase)
                    || !uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "Provider transports require HTTPS or HTTP loopback.",
                parameterName);
        }
    }

    private static bool HasTraversal(string path)
    {
        var value = path;
        for (var pass = 0; pass < 5; pass++)
        {
            var pathOnly = value.Split('?')[0];
            if (pathOnly.Split('/').Any(
                    segment => string.Equals(segment, ".", StringComparison.Ordinal)
                               || string.Equals(segment, "..", StringComparison.Ordinal)))
            {
                return true;
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return true;
            }

            if (string.Equals(decoded, value, StringComparison.Ordinal))
            {
                return false;
            }

            value = decoded;
        }

        return true;
    }
}

internal sealed class NativeSseRecord
{
    public NativeSseRecord(string? eventName, string data)
    {
        EventName = eventName;
        Data = data;
    }

    public string? EventName { get; }

    public string Data { get; }
}

internal static class NativeSseReader
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(false, true);

    public static async IAsyncEnumerable<NativeSseRecord> ReadAsync(
        Stream stream,
        int maxLineCharacters,
        int maxEventCharacters,
        int maxTotalCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var decoder = StrictUtf8.GetDecoder();
        var bytes = new byte[8_192];
        var chars = new char[8_192];
        var line = new StringBuilder();
        var data = new StringBuilder();
        string? eventName = null;
        var eventNameSeen = false;
        var dataSeen = false;
        long totalCharacters = 0;
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream
                        .ReadAsync(
                            bytes.AsMemory(0, bytes.Length),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new ProviderException(
                        "provider_stream_read_failed",
                        "network",
                        "The provider stream could not be read.",
                        true,
                        innerException: exception);
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException
                          and not StackOverflowException
                          and not ProviderException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new ProviderException(
                        "provider_stream_read_failed",
                        "network",
                        "The provider stream could not be read.",
                        true,
                        innerException: exception);
                }
                var flush = read == 0;
                var offset = 0;
                do
                {
                    int bytesUsed;
                    int charsUsed;
                    try
                    {
                        decoder.Convert(
                            bytes,
                            offset,
                            read - offset,
                            chars,
                            0,
                            chars.Length,
                            flush,
                            out bytesUsed,
                            out charsUsed,
                            out _);
                    }
                    catch (DecoderFallbackException exception)
                    {
                        throw new ProviderException(
                            "provider_sse_utf8_invalid",
                            "provider",
                            "The provider emitted invalid UTF-8.",
                            false,
                            innerException: exception);
                    }
                    offset += bytesUsed;
                    totalCharacters += charsUsed;
                    if (totalCharacters > maxTotalCharacters)
                    {
                        throw Invalid(
                            "provider_sse_stream_too_large",
                            "The provider stream exceeded its character limit.");
                    }

                    for (var index = 0; index < charsUsed; index++)
                    {
                        var character = chars[index];
                        if (character != '\n')
                        {
                            line.Append(character);
                            if (line.Length > maxLineCharacters)
                            {
                                throw Invalid(
                                    "provider_sse_line_too_large",
                                    "The provider emitted an oversized SSE line.");
                            }

                            continue;
                        }

                        var value = line.ToString();
                        line.Clear();
                        if (value.EndsWith("\r", StringComparison.Ordinal))
                        {
                            value = value.Substring(0, value.Length - 1);
                        }

                        if (value.Length == 0)
                        {
                            if (eventNameSeen || dataSeen)
                            {
                                yield return new NativeSseRecord(
                                    eventName,
                                    data.ToString());
                                data.Clear();
                            }

                            eventName = null;
                            eventNameSeen = false;
                            dataSeen = false;
                            continue;
                        }

                        if (value.StartsWith(":", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var colon = value.IndexOf(':');
                        var field = colon < 0
                            ? value
                            : value.Substring(0, colon);
                        var fieldValue = colon < 0
                            ? string.Empty
                            : value.Substring(colon + 1);
                        if (fieldValue.StartsWith(" ", StringComparison.Ordinal))
                        {
                            fieldValue = fieldValue.Substring(1);
                        }

                        if (string.Equals(field, "event", StringComparison.Ordinal))
                        {
                            if (eventNameSeen)
                            {
                                throw Invalid(
                                    "provider_sse_event_invalid",
                                    "The provider repeated an SSE event field.");
                            }

                            eventNameSeen = true;
                            eventName = fieldValue;
                        }
                        else if (string.Equals(field, "data", StringComparison.Ordinal))
                        {
                            if (dataSeen)
                            {
                                data.Append('\n');
                            }

                            dataSeen = true;
                            data.Append(fieldValue);
                            if (data.Length > maxEventCharacters)
                            {
                                throw Invalid(
                                    "provider_sse_event_too_large",
                                    "The provider emitted an oversized SSE event.");
                            }
                        }
                    }
                }
                while (offset < read);

                if (flush)
                {
                    break;
                }
            }

            if (line.Length > 0 || eventNameSeen || dataSeen)
            {
                throw Invalid(
                    "provider_sse_truncated_event",
                    "The provider stream ended during an SSE event.",
                    retryable: true);
            }
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
            Array.Clear(chars, 0, chars.Length);
        }
    }

    private static ProviderException Invalid(
        string code,
        string message,
        bool retryable = false)
    {
        return new ProviderException(
            code,
            retryable ? "network" : "provider",
            message,
            retryable);
    }
}

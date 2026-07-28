using System.Net;
using System.Net.Http.Headers;

namespace GameAgent.Providers.OpenAICompatible;

public sealed class StreamingHttpRequest
{
    public Uri Uri { get; set; } = new("https://localhost");

    public string BearerToken { get; set; } = string.Empty;

    public byte[] Body { get; set; } = Array.Empty<byte>();
}

public interface IStreamingHttpResponse : IDisposable
{
    int StatusCode { get; }

    Stream Content { get; }

    string? GetHeader(string name);
}

public interface IStreamingHttpTransport
{
    ValueTask<IStreamingHttpResponse> SendAsync(
        StreamingHttpRequest request,
        CancellationToken cancellationToken);
}

internal static class BearerTokenValidator
{
    private const int MaximumLength = 8192;

    public static string ValidateAndTrim(
        string? token,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException(
                "A non-empty bearer token is required.",
                parameterName);
        }

        var value = token.Trim();
        if (value.Length > MaximumLength)
        {
            throw new ArgumentException(
                "The bearer token exceeds the supported length.",
                parameterName);
        }

        var paddingStarted = false;
        var dataCharacters = 0;
        foreach (var character in value)
        {
            if (character == '=')
            {
                paddingStarted = true;
                continue;
            }

            if (paddingStarted || !IsToken68Character(character))
            {
                throw new ArgumentException(
                    "The bearer token is not valid token68 data.",
                    parameterName);
            }

            dataCharacters++;
        }
        if (dataCharacters == 0)
        {
            throw new ArgumentException(
                "The bearer token is not valid token68 data.",
                parameterName);
        }

        return value;
    }

    private static bool IsToken68Character(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '.' or '_' or '~' or '+' or '/';
}

public sealed class HttpClientStreamingTransport :
    IStreamingHttpTransport,
    IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpClientStreamingTransport()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _client = new HttpClient(handler, disposeHandler: true);
        _ownsClient = true;
    }

    public async ValueTask<IStreamingHttpResponse> SendAsync(
        StreamingHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Uri is null
            || !request.Uri.IsAbsoluteUri
            || (!string.Equals(
                    request.Uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.Ordinal)
                && (!string.Equals(
                        request.Uri.Scheme,
                        Uri.UriSchemeHttp,
                        StringComparison.Ordinal)
                    || !request.Uri.IsLoopback))
            || !string.IsNullOrEmpty(request.Uri.UserInfo))
        {
            throw new ArgumentException(
                "Transport endpoints must use HTTPS, except for HTTP loopback tests.",
                nameof(request));
        }

        var bearerToken = BearerTokenValidator.ValidateAndTrim(
            request.BearerToken,
            nameof(request));

        if (request.Body is null)
        {
            throw new ArgumentException(
                "The request body cannot be null.",
                nameof(request));
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Uri);
        message.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", bearerToken);
        message.Content = new ByteArrayContent(request.Body);
        message.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };

        var response = await _client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var content = await response.Content.ReadAsStreamAsync()
                .ConfigureAwait(false);
            return new HttpClientStreamingResponse(response, content);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed class HttpClientStreamingResponse : IStreamingHttpResponse
    {
        private readonly HttpResponseMessage _response;

        public HttpClientStreamingResponse(
            HttpResponseMessage response,
            Stream content)
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

        public void Dispose()
        {
            _response.Dispose();
        }
    }
}

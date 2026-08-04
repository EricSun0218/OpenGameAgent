using System.Net;
using System.Net.Http.Headers;

namespace GameAgent.Providers.Anthropic;

public sealed class AnthropicStreamingHttpRequest
{
    public Uri Uri { get; set; } =
        new("https://api.anthropic.com/v1/messages");

    public string ApiKey { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "2023-06-01";

    public byte[] Body { get; set; } = Array.Empty<byte>();

    public string ContentType { get; set; } =
        "application/json; charset=utf-8";
}

public interface IAnthropicStreamingHttpResponse : IDisposable
{
    int StatusCode { get; }

    Stream Content { get; }

    string? GetHeader(string name);
}

public interface IAnthropicStreamingHttpTransport
{
    /// <summary>
    /// The implementation must finish consuming request.Body before this
    /// operation completes. The provider clears its owned buffer immediately
    /// afterward.
    /// </summary>
    ValueTask<IAnthropicStreamingHttpResponse> SendAsync(
        AnthropicStreamingHttpRequest request,
        CancellationToken cancellationToken);
}

public sealed class HttpClientAnthropicStreamingTransport :
    IAnthropicStreamingHttpTransport,
    IDisposable
{
    private readonly HttpClient _client;

    public HttpClientAnthropicStreamingTransport()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _client = new HttpClient(handler, disposeHandler: true);
    }

    public async ValueTask<IAnthropicStreamingHttpResponse> SendAsync(
        AnthropicStreamingHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateEndpoint(request.Uri);
        var apiKey = AnthropicApiKeyValidator.ValidateAndTrim(
            request.ApiKey,
            nameof(request));
        if (!string.Equals(
                request.ApiVersion,
                "2023-06-01",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Anthropic API version is invalid.",
                nameof(request));
        }

        if (request.Body is null)
        {
            throw new ArgumentException(
                "The request body cannot be null.",
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
                "The transport content type is invalid.",
                nameof(request),
                exception);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Uri);
        message.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", request.ApiVersion);
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

    public void Dispose()
    {
        _client.Dispose();
    }

    private static void ValidateEndpoint(Uri? endpoint)
    {
        if (endpoint is null
            || !endpoint.IsAbsoluteUri
            || (!string.Equals(
                    endpoint.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(
                        endpoint.Scheme,
                        Uri.UriSchemeHttp,
                        StringComparison.OrdinalIgnoreCase)
                    || !endpoint.IsLoopback))
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.Equals(
                endpoint.AbsolutePath,
                "/v1/messages",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Transport endpoints must use the bounded Messages path over HTTPS, except for HTTP loopback tests.",
                nameof(endpoint));
        }
    }

    private sealed class Response : IAnthropicStreamingHttpResponse
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

        public void Dispose()
        {
            _response.Dispose();
        }
    }
}

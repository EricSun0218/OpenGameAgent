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

    public HttpClientStreamingTransport(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async ValueTask<IStreamingHttpResponse> SendAsync(
        StreamingHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.BearerToken.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new ArgumentException(
                "Bearer tokens cannot contain line breaks.",
                nameof(request));
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Uri);
        message.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", request.BearerToken);
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

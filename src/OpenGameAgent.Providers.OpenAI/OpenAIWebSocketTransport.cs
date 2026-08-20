using System.Buffers;
using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Text;

namespace OpenGameAgent.Providers.OpenAI;

public sealed class OpenAIWebSocketConnectRequest
{
    public OpenAIWebSocketConnectRequest(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        int? timeoutMilliseconds = null)
    {
        if (endpoint is null
            || !endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length > 0
            || (endpoint.Scheme != "ws" && endpoint.Scheme != "wss"))
        {
            throw new ArgumentException(
                "The WebSocket endpoint must be an absolute ws or wss URI without embedded credentials.",
                nameof(endpoint));
        }

        if (timeoutMilliseconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }

        Endpoint = endpoint;
        Headers = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                headers ?? throw new ArgumentNullException(nameof(headers)),
                StringComparer.OrdinalIgnoreCase));
        TimeoutMilliseconds = timeoutMilliseconds;
    }

    public Uri Endpoint { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public int? TimeoutMilliseconds { get; }
}

public interface IOpenAIWebSocketConnection : IDisposable
{
    bool IsOpen { get; }

    ValueTask SendTextAsync(string text, CancellationToken cancellationToken);

    ValueTask<string> ReceiveTextAsync(int maximumCharacters, CancellationToken cancellationToken);

    ValueTask CloseAsync(string reason, CancellationToken cancellationToken);
}

public interface IOpenAIWebSocketResponseMetadata
{
    int HandshakeStatusCode { get; }

    IReadOnlyDictionary<string, string> HandshakeHeaders { get; }
}

public delegate ValueTask<IOpenAIWebSocketConnection> OpenAIWebSocketConnectionFactory(
    OpenAIWebSocketConnectRequest request,
    CancellationToken cancellationToken);

public static class OpenAIWebSocketConnections
{
    public static ValueTask<IOpenAIWebSocketConnection> ConnectAsync(
        OpenAIWebSocketConnectRequest request,
        CancellationToken cancellationToken = default) =>
        ClientOpenAIWebSocketConnection.ConnectAsync(request, cancellationToken);
}

public sealed class OpenAIWebSocketStatistics
{
    internal OpenAIWebSocketStatistics(
        long requests,
        long connectionsCreated,
        long connectionsReused,
        long fullContextRequests,
        long deltaRequests,
        long failures,
        long sseFallbacks,
        bool fallbackActive,
        string? lastError)
    {
        Requests = requests;
        ConnectionsCreated = connectionsCreated;
        ConnectionsReused = connectionsReused;
        FullContextRequests = fullContextRequests;
        DeltaRequests = deltaRequests;
        Failures = failures;
        SseFallbacks = sseFallbacks;
        FallbackActive = fallbackActive;
        LastError = lastError;
    }

    public long Requests { get; }

    public long ConnectionsCreated { get; }

    public long ConnectionsReused { get; }

    public long FullContextRequests { get; }

    public long DeltaRequests { get; }

    public long Failures { get; }

    public long SseFallbacks { get; }

    public bool FallbackActive { get; }

    public string? LastError { get; }
}

internal sealed class ClientOpenAIWebSocketConnection :
    IOpenAIWebSocketConnection,
    IOpenAIWebSocketResponseMetadata
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ClientWebSocket _socket;
    private bool _disposed;

    private ClientOpenAIWebSocketConnection(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public bool IsOpen => !_disposed && _socket.State == WebSocketState.Open;

    public int HandshakeStatusCode => 101;

    public IReadOnlyDictionary<string, string> HandshakeHeaders { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static async ValueTask<IOpenAIWebSocketConnection> ConnectAsync(
        OpenAIWebSocketConnectRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var socket = new ClientWebSocket();
        try
        {
            foreach (var header in request.Headers)
            {
                socket.Options.SetRequestHeader(header.Key, header.Value);
            }

            using var timeout = request.TimeoutMilliseconds is { } milliseconds
                ? new CancellationTokenSource(milliseconds)
                : null;
            using var linked = timeout is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await socket.ConnectAsync(
                        request.Endpoint,
                        linked?.Token ?? cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                timeout?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"WebSocket connect timeout after {request.TimeoutMilliseconds}ms.",
                    exception);
            }

            return new ClientOpenAIWebSocketConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask SendTextAsync(string text, CancellationToken cancellationToken)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        ThrowIfDisposed();
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string> ReceiveTextAsync(
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (maximumCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        ThrowIfDisposed();
        var maximumBytes = checked((long)maximumCharacters * 4L);
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(rented),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var suffix = string.IsNullOrWhiteSpace(result.CloseStatusDescription)
                        ? string.Empty
                        : " " + result.CloseStatusDescription;
                    throw new IOException(
                        $"WebSocket closed with status {result.CloseStatus?.ToString() ?? "unknown"}.{suffix}".TrimEnd());
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException(
                        "The WebSocket response event was not a text message.");
                }

                if (buffer.Length + result.Count > maximumBytes)
                {
                    throw new InvalidDataException(
                        "The WebSocket response event exceeded the configured character limit.");
                }

                buffer.Write(rented, 0, result.Count);
            }
            while (!result.EndOfMessage);

            string text;
            try
            {
                text = StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The WebSocket response was not valid UTF-8.", exception);
            }

            if (text.Length > maximumCharacters)
            {
                throw new InvalidDataException(
                    "The WebSocket response event exceeded the configured character limit.");
            }

            return text;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask CloseAsync(string reason, CancellationToken cancellationToken)
    {
        if (_disposed || _socket.State is WebSocketState.Closed or WebSocketState.Aborted)
        {
            return;
        }

        var boundedReason = string.IsNullOrEmpty(reason)
            ? "done"
            : reason.Length <= 123 ? reason : reason.Substring(0, 123);
        try
        {
            await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    boundedReason,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            _socket.Abort();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _socket.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ClientOpenAIWebSocketConnection));
        }
    }
}

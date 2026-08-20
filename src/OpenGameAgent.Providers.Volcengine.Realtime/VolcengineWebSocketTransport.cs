using System.Buffers;
using System.Collections.ObjectModel;
using System.Net.WebSockets;

namespace OpenGameAgent.Providers.Volcengine.Realtime;

public sealed class VolcengineWebSocketConnectRequest
{
    public VolcengineWebSocketConnectRequest(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        int timeoutMilliseconds)
    {
        if (endpoint is null
            || !endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length > 0
            || (endpoint.Scheme != "wss" && endpoint.Scheme != "ws"))
        {
            throw new ArgumentException(
                "The WebSocket endpoint must be an absolute ws or wss URI without credentials.",
                nameof(endpoint));
        }

        if (endpoint.Scheme == "ws" && !endpoint.IsLoopback)
        {
            throw new ArgumentException(
                "Plaintext WebSockets are allowed only for loopback endpoints.",
                nameof(endpoint));
        }

        if (timeoutMilliseconds is < 100 or > 120_000)
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

    public int TimeoutMilliseconds { get; }
}

public interface IVolcengineWebSocketConnection : IDisposable
{
    bool IsOpen { get; }

    ValueTask SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    ValueTask<ReadOnlyMemory<byte>> ReceiveBinaryAsync(
        int maximumBytes,
        CancellationToken cancellationToken);

    ValueTask CloseAsync(string reason, CancellationToken cancellationToken);
}

public delegate ValueTask<IVolcengineWebSocketConnection> VolcengineWebSocketConnectionFactory(
    VolcengineWebSocketConnectRequest request,
    CancellationToken cancellationToken);

public static class VolcengineWebSocketConnections
{
    public static ValueTask<IVolcengineWebSocketConnection> ConnectAsync(
        VolcengineWebSocketConnectRequest request,
        CancellationToken cancellationToken = default) =>
        ClientVolcengineWebSocketConnection.ConnectAsync(request, cancellationToken);
}

internal sealed class ClientVolcengineWebSocketConnection : IVolcengineWebSocketConnection
{
    private readonly ClientWebSocket _socket;
    private bool _disposed;

    private ClientVolcengineWebSocketConnection(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public bool IsOpen => !_disposed && _socket.State == WebSocketState.Open;

    public static async ValueTask<IVolcengineWebSocketConnection> ConnectAsync(
        VolcengineWebSocketConnectRequest request,
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

            using var timeout = new CancellationTokenSource(request.TimeoutMilliseconds);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                await socket.ConnectAsync(request.Endpoint, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"WebSocket connect timeout after {request.TimeoutMilliseconds}ms.",
                    exception);
            }

            return new ClientVolcengineWebSocketConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask SendBinaryAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (payload.IsEmpty)
        {
            throw new ArgumentException("A non-empty WebSocket payload is required.", nameof(payload));
        }

        var copy = payload.ToArray();
        await _socket.SendAsync(
                new ArraySegment<byte>(copy),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveBinaryAsync(
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(8192, maximumBytes));
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
                    throw new IOException(
                        $"WebSocket closed with status {result.CloseStatus?.ToString() ?? "unknown"}.");
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    throw new InvalidDataException("The provider returned a non-binary WebSocket frame.");
                }

                if (buffer.Length + result.Count > maximumBytes)
                {
                    throw new InvalidDataException("The provider frame exceeded its configured limit.");
                }

                buffer.Write(rented, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (buffer.Length == 0)
            {
                throw new InvalidDataException("The provider returned an empty WebSocket frame.");
            }

            return buffer.ToArray();
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

        var bounded = string.IsNullOrWhiteSpace(reason)
            ? "closing"
            : new string(reason.Where(character => !char.IsControl(character)).Take(120).ToArray());
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    bounded,
                    cancellationToken)
                .ConfigureAwait(false);
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
            throw new ObjectDisposedException(nameof(ClientVolcengineWebSocketConnection));
        }
    }
}

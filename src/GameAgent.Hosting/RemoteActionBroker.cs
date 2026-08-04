using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using GameAgent.Protocol;

namespace GameAgent.Hosting;

public sealed class RemoteActionBrokerOptions
{
    public int MaxConnections { get; set; } = 4_096;
    public int MaxPendingActionsPerConnection { get; set; } = 256;
    public int MaxEnvelopeBytes { get; set; } = 1_048_576;
    public int MaxEnvelopeFragments { get; set; } = 1_024;
    public TimeSpan ReceiptTimeout { get; set; } = TimeSpan.FromMinutes(2);

    internal RemoteActionBrokerOptions Snapshot()
    {
        if (MaxConnections is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        if (MaxPendingActionsPerConnection is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaxPendingActionsPerConnection));
        if (MaxEnvelopeBytes is < 1_024 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(MaxEnvelopeBytes));
        if (MaxEnvelopeFragments is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaxEnvelopeFragments));
        if (ReceiptTimeout <= TimeSpan.Zero || ReceiptTimeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(ReceiptTimeout));
        return new RemoteActionBrokerOptions
        {
            MaxConnections = MaxConnections,
            MaxPendingActionsPerConnection = MaxPendingActionsPerConnection,
            MaxEnvelopeBytes = MaxEnvelopeBytes,
            MaxEnvelopeFragments = MaxEnvelopeFragments,
            ReceiptTimeout = ReceiptTimeout
        };
    }
}

public sealed class RemoteTransportIdentity
{
    public RemoteTransportIdentity(string tenantId, string worldId)
    {
        Validate(tenantId, nameof(tenantId));
        Validate(worldId, nameof(worldId));
        TenantId = tenantId;
        WorldId = worldId;
    }

    public string TenantId { get; }
    public string WorldId { get; }
    internal string RouteKey => TenantId + "\n" + WorldId;

    private static void Validate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new ArgumentException("A bounded identity is required.", name);
        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                  or >= 'A' and <= 'Z'
                  or >= '0' and <= '9'
                  or '.' or '_' or ':' or '-'))
            {
                throw new ArgumentException("The identity contains an unsupported character.", name);
            }
        }
    }
}

public sealed class RemoteActionChannelUnavailableException : IOException
{
    public RemoteActionChannelUnavailableException(string message) : base(message) { }
}

public sealed class RemoteActionBroker : IAsyncDisposable
{
    private readonly AgentTransportCodec _codec;
    private readonly RemoteActionBrokerOptions _options;
    private readonly GameAgentKillSwitch? _killSwitch;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectionSlots;
    private int _disposed;

    public RemoteActionBroker(
        AgentTransportCodec codec,
        RemoteActionBrokerOptions? options = null,
        GameAgentKillSwitch? killSwitch = null)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _options = (options ?? new RemoteActionBrokerOptions()).Snapshot();
        _killSwitch = killSwitch;
        _connectionSlots = new SemaphoreSlim(_options.MaxConnections, _options.MaxConnections);
    }

    public int ConnectionCount => _connections.Count;

    public IRemoteActionChannel CreateChannel(RemoteTransportIdentity identity)
    {
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _killSwitch?.EnsureAllowed(identity.TenantId);
        return new BrokerChannel(this, identity);
    }

    public async Task RunConnectionAsync(
        RemoteTransportIdentity identity,
        WebSocket socket,
        CancellationToken cancellationToken = default)
    {
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (socket is null) throw new ArgumentNullException(nameof(socket));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _killSwitch?.EnsureAllowed(identity.TenantId);
        if (!_connectionSlots.Wait(0))
        {
            throw new TenantCapacityExceededException("max_remote_connections", "The remote action connection limit is full.");
        }
        var connection = new Connection(identity, socket, _codec, _options);
        try
        {
            if (!_connections.TryAdd(identity.RouteKey, connection))
            {
                throw new TenantCapacityExceededException("remote_route_connected", "The tenant-world route already has an active game host.");
            }
            await connection.ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(new KeyValuePair<string, Connection>(identity.RouteKey, connection));
            await connection.DisposeAsync().ConfigureAwait(false);
            _connectionSlots.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var connections = _connections.ToArray();
        _connections.Clear();
        foreach (var pair in connections)
        {
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<ActionReceipt> SubmitAsync(
        RemoteTransportIdentity identity,
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        _killSwitch?.EnsureAllowed(identity.TenantId);
        if (!_connections.TryGetValue(identity.RouteKey, out var connection))
        {
            throw new RemoteActionChannelUnavailableException("No remote game host is connected for this tenant-world route.");
        }
        return await connection.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private sealed class BrokerChannel : IRemoteActionChannel
    {
        private readonly RemoteActionBroker _owner;
        private readonly RemoteTransportIdentity _identity;
        public BrokerChannel(RemoteActionBroker owner, RemoteTransportIdentity identity)
        {
            _owner = owner;
            _identity = identity;
        }
        public ValueTask<ActionReceipt> SubmitAsync(ActionRequest request, CancellationToken cancellationToken = default) =>
            _owner.SubmitAsync(_identity, request, cancellationToken);
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly RemoteTransportIdentity _identity;
        private readonly WebSocket _socket;
        private readonly AgentTransportCodec _codec;
        private readonly RemoteActionBrokerOptions _options;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ActionReceipt>> _pending = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _pendingSlots;
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly CancellationTokenSource _closed = new();
        private int _disposed;

        public Connection(RemoteTransportIdentity identity, WebSocket socket, AgentTransportCodec codec, RemoteActionBrokerOptions options)
        {
            _identity = identity;
            _socket = socket;
            _codec = codec;
            _options = options;
            _pendingSlots = new SemaphoreSlim(options.MaxPendingActionsPerConnection, options.MaxPendingActionsPerConnection);
        }

        public async ValueTask<ActionReceipt> SubmitAsync(ActionRequest request, CancellationToken cancellationToken)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (!string.Equals(request.WorldId, _identity.WorldId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The action world does not match the remote route.");
            }
            if (_socket.State != WebSocketState.Open || Volatile.Read(ref _disposed) != 0)
            {
                throw new RemoteActionChannelUnavailableException("The remote game host connection is not open.");
            }
            if (!_pendingSlots.Wait(0))
            {
                throw new TenantCapacityExceededException("max_pending_remote_actions", "The remote route has reached its pending action limit.");
            }
            var messageId = "action:" + Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<ActionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deliveryAttempted = false;
            try
            {
                if (!_pending.TryAdd(messageId, completion)) throw new InvalidOperationException("A transport message ID collision occurred.");
                var envelope = new AgentTransportEnvelope
                {
                    MessageId = messageId,
                    Type = AgentTransportMessageTypes.ActionRequest,
                    TenantId = _identity.TenantId,
                    WorldId = _identity.WorldId,
                    RunId = request.RunId,
                    Payload = ProtocolJson.ToElement(request)
                };
                var bytes = _codec.Serialize(envelope);
                if (bytes.Length > _options.MaxEnvelopeBytes)
                {
                    throw new AgentTransportValidationException("envelope_bytes_exceeded", "The outbound WebSocket message exceeds its byte limit.");
                }
                await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    deliveryAttempted = true;
                    await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _send.Release();
                }
                using var timeout = new CancellationTokenSource(_options.ReceiptTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _closed.Token);
                var receipt = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                if (!string.Equals(receipt.OperationId, request.OperationId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The remote game host returned a receipt for another operation.");
                }
                return receipt;
            }
            catch (Exception exception) when (deliveryAttempted && exception is OperationCanceledException or WebSocketException or IOException)
            {
                throw new RemoteActionOutcomeUnknownException("The remote connection ended after action delivery; the game outcome is unknown.", exception);
            }
            finally
            {
                _pending.TryRemove(messageId, out _);
                _pendingSlots.Release();
            }
        }

        public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    var fragments = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        if (++fragments > _options.MaxEnvelopeFragments)
                        {
                            throw new AgentTransportValidationException("envelope_fragments_exceeded", "The WebSocket message exceeds its fragment limit.");
                        }
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        if (result.MessageType != WebSocketMessageType.Text) throw new AgentTransportValidationException("message_binary_unsupported", "Only JSON text messages are supported.");
                        if (message.Length > _options.MaxEnvelopeBytes - result.Count)
                        {
                            throw new AgentTransportValidationException("envelope_bytes_exceeded", "The WebSocket message exceeds its byte limit.");
                        }
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    var envelope = _codec.Deserialize(message.GetBuffer().AsSpan(0, checked((int)message.Length)));
                    if (envelope.Type != AgentTransportMessageTypes.ActionReceipt
                        || envelope.CorrelationId is null
                        || envelope.TenantId != _identity.TenantId
                        || envelope.WorldId != _identity.WorldId)
                    {
                        throw new AgentTransportValidationException("receipt_route_invalid", "The remote receipt does not match its pending route.");
                    }
                    var receipt = ProtocolJson.DeserializeActionReceipt(envelope.Payload.GetRawText());
                    ProtocolValidator.EnsureValid(receipt);
                    if (_pending.TryGetValue(envelope.CorrelationId, out var completion))
                    {
                        completion.TrySetResult(receipt);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                _closed.Cancel();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _closed.Cancel();
            foreach (var completion in _pending.Values) completion.TrySetCanceled(_closed.Token);
            _pending.Clear();
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "host_shutdown", CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException) { }
            }
            _socket.Dispose();
        }
    }
}

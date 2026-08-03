using System.Net.WebSockets;
using GameAgent.Hosting;
using GameAgent.Protocol;

namespace GameAgent.Hosting.Tests;

public sealed class RemoteBrokerCapacityTests
{
    [Fact]
    public async Task ConnectionLimitIsAtomicAcrossRoutes()
    {
        await using var broker = new RemoteActionBroker(
            new AgentTransportCodec(),
            new RemoteActionBrokerOptions { MaxConnections = 1 });
        using var shutdown = new CancellationTokenSource();
        using var firstSocket = new ControlledWebSocket();
        using var secondSocket = new ControlledWebSocket();
        var first = broker.RunConnectionAsync(
            new RemoteTransportIdentity("tenant-a", "world"),
            firstSocket,
            shutdown.Token);
        await WaitUntilAsync(() => broker.ConnectionCount == 1);

        var error = await Assert.ThrowsAsync<TenantCapacityExceededException>(
            () => broker.RunConnectionAsync(
                new RemoteTransportIdentity("tenant-b", "world"),
                secondSocket,
                shutdown.Token));
        Assert.Equal("max_remote_connections", error.ReasonCode);

        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task PendingActionLimitCannotBeOversubscribed()
    {
        await using var broker = new RemoteActionBroker(
            new AgentTransportCodec(),
            new RemoteActionBrokerOptions
            {
                MaxConnections = 1,
                MaxPendingActionsPerConnection = 1
            });
        using var shutdown = new CancellationTokenSource();
        using var socket = new ControlledWebSocket();
        var connection = broker.RunConnectionAsync(
            new RemoteTransportIdentity("tenant", "world"),
            socket,
            shutdown.Token);
        await WaitUntilAsync(() => broker.ConnectionCount == 1);
        var channel = broker.CreateChannel(new RemoteTransportIdentity("tenant", "world"));
        var first = channel.SubmitAsync(Request("first")).AsTask();
        await socket.Sent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<TenantCapacityExceededException>(
            async () => await channel.SubmitAsync(Request("second")));
        Assert.Equal("max_pending_remote_actions", error.ReasonCode);

        shutdown.Cancel();
        await Assert.ThrowsAsync<RemoteActionOutcomeUnknownException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
    }

    [Fact]
    public async Task ReceiptForAnotherOperationIsRejected()
    {
        var codec = new AgentTransportCodec();
        await using var broker = new RemoteActionBroker(codec);
        using var shutdown = new CancellationTokenSource();
        using var socket = new WrongReceiptWebSocket(codec);
        var connection = broker.RunConnectionAsync(
            new RemoteTransportIdentity("tenant", "world"),
            socket,
            shutdown.Token);
        await WaitUntilAsync(() => broker.ConnectionCount == 1);
        var channel = broker.CreateChannel(new RemoteTransportIdentity("tenant", "world"));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await channel.SubmitAsync(Request("expected")));

        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static ActionRequest Request(string operationId) => new()
    {
        OperationId = operationId,
        RunId = "run",
        TurnId = "turn",
        ToolCallId = "call:" + operationId,
        AgentId = "agent",
        WorldId = "world",
        ActionName = "game.action",
        ActionVersion = "1",
        Arguments = ProtocolJson.ParseElement("{}"),
        RequestedAt = DateTimeOffset.UnixEpoch
    };

    private sealed class ControlledWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public TaskCompletionSource<bool> Sent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            _ = buffer;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The controlled socket receive should only end through cancellation.");
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(buffer.Count > 0);
            Assert.Equal(WebSocketMessageType.Text, messageType);
            Assert.True(endOfMessage);
            Sent.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class WrongReceiptWebSocket : WebSocket
    {
        private readonly AgentTransportCodec _codec;
        private readonly TaskCompletionSource<byte[]> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private int _received;

        public WrongReceiptWebSocket(AgentTransportCodec codec) => _codec = codec;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _received, 1) != 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The socket should stop through cancellation.");
            }
            var bytes = await _response.Task.WaitAsync(cancellationToken);
            Assert.True(bytes.Length <= buffer.Count);
            bytes.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WebSocketMessageType.Text, messageType);
            Assert.True(endOfMessage);
            var request = _codec.Deserialize(buffer.AsSpan());
            var receipt = new ActionReceipt
            {
                OperationId = "another-operation",
                Revision = 1,
                Status = ReceiptStatuses.Succeeded,
                ReceivedAt = DateTimeOffset.UnixEpoch
            };
            _response.TrySetResult(_codec.Serialize(new AgentTransportEnvelope
            {
                MessageId = "receipt:wrong",
                Type = AgentTransportMessageTypes.ActionReceipt,
                TenantId = "tenant",
                WorldId = "world",
                RunId = request.RunId,
                CorrelationId = request.MessageId,
                Payload = ProtocolJson.ToElement(receipt)
            }));
            return Task.CompletedTask;
        }
    }
}

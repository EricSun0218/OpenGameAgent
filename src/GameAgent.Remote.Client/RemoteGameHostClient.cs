using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Remote.Client;

public sealed class RemoteGameHostClientOptions
{
    public Uri? Endpoint { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string? BearerToken { get; set; }
    public int MaxConcurrentActions { get; set; } = 16;
    public int MaxCachedReceipts { get; set; } = 4_096;
    public int MaxEnvelopeBytes { get; set; } = 1_048_576;
    public int MaxEnvelopeFragments { get; set; } = 1_024;

    internal RemoteGameHostClientOptions Snapshot()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri) throw new ArgumentException("An absolute WebSocket endpoint is required.", nameof(Endpoint));
        if (Endpoint.Scheme != "wss" && !(Endpoint.Scheme == "ws" && Endpoint.IsLoopback))
        {
            throw new ArgumentException("Remote game-host connections require wss, except on loopback.", nameof(Endpoint));
        }
        ValidateId(TenantId, nameof(TenantId));
        ValidateId(WorldId, nameof(WorldId));
        if (BearerToken is { Length: > 8192 } || BearerToken?.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new ArgumentException("The bearer token is invalid.", nameof(BearerToken));
        }
        if (MaxConcurrentActions is < 1 or > 4_096) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentActions));
        if (MaxCachedReceipts is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaxCachedReceipts));
        if (MaxEnvelopeBytes is < 1_024 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(MaxEnvelopeBytes));
        if (MaxEnvelopeFragments is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaxEnvelopeFragments));
        return new RemoteGameHostClientOptions
        {
            Endpoint = Endpoint,
            TenantId = TenantId,
            WorldId = WorldId,
            BearerToken = BearerToken,
            MaxConcurrentActions = MaxConcurrentActions,
            MaxCachedReceipts = MaxCachedReceipts,
            MaxEnvelopeBytes = MaxEnvelopeBytes,
            MaxEnvelopeFragments = MaxEnvelopeFragments
        };
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new ArgumentException("A bounded ID is required.", name);
        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                  or >= 'A' and <= 'Z'
                  or >= '0' and <= '9'
                  or '.' or '_' or ':' or '-'))
            {
                throw new ArgumentException("The ID contains an unsupported character.", name);
            }
        }
    }
}

public sealed class RemoteGameHostClient
{
    private readonly RemoteGameHostClientOptions _options;
    private readonly AgentTransportCodec _codec;

    public RemoteGameHostClient(RemoteGameHostClientOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
        _codec = new AgentTransportCodec(new AgentTransportLimits { MaxEnvelopeBytes = _options.MaxEnvelopeBytes });
    }

    public async Task RunAsync(IGameHost gameHost, CancellationToken cancellationToken = default)
    {
        if (gameHost is null) throw new ArgumentNullException(nameof(gameHost));
        using var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + _options.BearerToken);
        }
        await socket.ConnectAsync(_options.Endpoint!, cancellationToken).ConfigureAwait(false);
        using var sendGate = new SemaphoreSlim(1, 1);
        using var actionGate = new SemaphoreSlim(_options.MaxConcurrentActions, _options.MaxConcurrentActions);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operations = new ConcurrentDictionary<string, Lazy<Task<ActionReceipt>>>(StringComparer.Ordinal);
        var cacheOrder = new ConcurrentQueue<string>();
        var active = new HashSet<Task>();
        var activeSync = new object();
        Exception? failure = null;
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var envelope = await ReceiveAsync(socket, stop.Token).ConfigureAwait(false);
                if (envelope is null) break;
                if (envelope.Type == AgentTransportMessageTypes.Acknowledgement) continue;
                if (envelope.Type == AgentTransportMessageTypes.Error)
                {
                    throw new InvalidDataException("The remote Agent host rejected the connection message.");
                }
                if (envelope.Type != AgentTransportMessageTypes.ActionRequest
                    || envelope.TenantId != _options.TenantId
                    || envelope.WorldId != _options.WorldId)
                {
                    throw new AgentTransportValidationException("action_route_invalid", "The action request does not match this game-host route.");
                }
                await actionGate.WaitAsync(stop.Token).ConfigureAwait(false);
                var task = HandleAsync(envelope);
                lock (activeSync) active.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    if (completed.IsFaulted)
                    {
                        Interlocked.CompareExchange(ref failure, completed.Exception!.GetBaseException(), null);
                        stop.Cancel();
                    }
                    lock (activeSync) active.Remove(completed);
                    actionGate.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (failure is not null)
        {
        }
        finally
        {
            stop.Cancel();
            Task[] remaining;
            lock (activeSync) remaining = active.ToArray();
            try { await Task.WhenAll(remaining).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch when (failure is not null) { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client_shutdown", CancellationToken.None).ConfigureAwait(false); }
                catch (WebSocketException) { }
            }
        }
        if (failure is not null) throw new IOException("The remote game-host action loop failed.", failure);

        async Task HandleAsync(AgentTransportEnvelope envelope)
        {
            var request = ProtocolJson.DeserializeActionRequest(envelope.Payload.GetRawText());
            ProtocolValidator.EnsureValid(request);
            if (request.WorldId != _options.WorldId || request.RunId != envelope.RunId)
            {
                throw new InvalidDataException("The action payload does not match its transport envelope.");
            }
            var created = new Lazy<Task<ActionReceipt>>(
                () => ExecuteLocalAsync(request, gameHost, stop.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var operation = operations.GetOrAdd(request.OperationId, created);
            if (ReferenceEquals(created, operation))
            {
                cacheOrder.Enqueue(request.OperationId);
            }
            var receipt = await operation.Value.ConfigureAwait(false);
            TrimCompletedReceipts();
            var response = new AgentTransportEnvelope
            {
                MessageId = "receipt:" + Guid.NewGuid().ToString("N"),
                Type = AgentTransportMessageTypes.ActionReceipt,
                TenantId = _options.TenantId,
                WorldId = _options.WorldId,
                RunId = request.RunId,
                CorrelationId = envelope.MessageId,
                Payload = ProtocolJson.ToElement(receipt)
            };
            var bytes = _codec.Serialize(response);
            await sendGate.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, stop.Token).ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        void TrimCompletedReceipts()
        {
            var attempts = cacheOrder.Count;
            while (operations.Count > _options.MaxCachedReceipts
                   && attempts-- > 0
                   && cacheOrder.TryDequeue(out var expired))
            {
                if (operations.TryGetValue(expired, out var candidate)
                    && candidate.IsValueCreated
                    && candidate.Value.IsCompleted)
                {
                    ((ICollection<KeyValuePair<string, Lazy<Task<ActionReceipt>>>>)operations)
                        .Remove(new KeyValuePair<string, Lazy<Task<ActionReceipt>>>(expired, candidate));
                }
                else
                {
                    cacheOrder.Enqueue(expired);
                }
            }
        }
    }

    private static async Task<ActionReceipt> ExecuteLocalAsync(ActionRequest request, IGameHost gameHost, CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await gameHost.SubmitActionAsync(request, cancellationToken).ConfigureAwait(false);
            ProtocolValidator.EnsureValid(receipt);
            if (receipt.OperationId != request.OperationId) throw new InvalidDataException("The local game host returned a receipt for another operation.");
            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new ActionReceipt
            {
                OperationId = request.OperationId,
                Revision = 1,
                Status = ReceiptStatuses.Failed,
                ErrorCode = "remote_game_host_exception",
                Retryable = false,
                ReceivedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private async Task<AgentTransportEnvelope?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
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
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text) throw new AgentTransportValidationException("message_binary_unsupported", "Only JSON text messages are supported.");
                if (message.Length > _options.MaxEnvelopeBytes - result.Count)
                {
                    throw new AgentTransportValidationException("envelope_bytes_exceeded", "The WebSocket message exceeds its byte limit.");
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return _codec.Deserialize(message.GetBuffer().AsSpan(0, checked((int)message.Length)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

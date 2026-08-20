using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Realtime;

namespace OpenGameAgent.Providers.Volcengine.Realtime;

public enum VolcengineRealtimeInputMode
{
    Dialogue,
    Disabled,
}

public sealed class VolcengineRealtimeTransportOptions
{
    public Uri DialogueEndpoint { get; set; } =
        new("wss://openspeech.bytedance.com/api/v3/realtime/dialogue");

    public Uri TtsEndpoint { get; set; } =
        new("wss://openspeech.bytedance.com/api/v3/tts/bidirection");

    public VolcengineRealtimeInputMode InputMode { get; set; } = VolcengineRealtimeInputMode.Dialogue;

    public string DialogueResourceId { get; set; } = "volc.speech.dialog";

    public string TtsResourceId { get; set; } = "seed-tts-2.0";

    public string DialogueModel { get; set; } = "2.2.0.0";

    public string TtsModel { get; set; } = "seed-tts-2.0-standard";

    public string Speaker { get; set; } = "zh_female_gaolengyujie_uranus_bigtts";

    public string? AppId { get; set; }

    public string? ApiKey { get; set; }

    public Func<CancellationToken, ValueTask<string?>>? GetApiKeyAsync { get; set; }

    public IReadOnlyDictionary<string, string> Headers { get; set; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public VolcengineWebSocketConnectionFactory ConnectionFactory { get; set; } =
        VolcengineWebSocketConnections.ConnectAsync;

    public int InputSampleRate { get; set; } = 16_000;

    public int OutputSampleRate { get; set; } = 24_000;

    public int ConnectTimeoutMilliseconds { get; set; } = 15_000;

    public int WireOperationTimeoutMilliseconds { get; set; } = 15_000;

    public int ShutdownTimeoutMilliseconds { get; set; } = 10_000;

    public int MaximumWireFrameBytes { get; set; } = 8_388_608;

    public int MaximumPayloadBytes { get; set; } = 4_194_304;

    public int MaximumTextCharacters { get; set; } = 65_536;

    public int EventQueueCapacity { get; set; } = 512;

    internal VolcengineRealtimeTransportOptions Snapshot()
    {
        ValidateEndpoint(DialogueEndpoint, nameof(DialogueEndpoint));
        ValidateEndpoint(TtsEndpoint, nameof(TtsEndpoint));
        if (!Enum.IsDefined(typeof(VolcengineRealtimeInputMode), InputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(InputMode));
        }

        ValidateId(DialogueResourceId, nameof(DialogueResourceId), 512);
        ValidateId(TtsResourceId, nameof(TtsResourceId), 512);
        ValidateId(DialogueModel, nameof(DialogueModel), 256);
        ValidateId(TtsModel, nameof(TtsModel), 256);
        ValidateId(Speaker, nameof(Speaker), 512);
        if (AppId is not null)
        {
            ValidateId(AppId, nameof(AppId), 512);
        }

        ValidateRange(InputSampleRate, 8_000, 48_000, nameof(InputSampleRate));
        ValidateRange(OutputSampleRate, 8_000, 48_000, nameof(OutputSampleRate));
        ValidateRange(ConnectTimeoutMilliseconds, 100, 120_000, nameof(ConnectTimeoutMilliseconds));
        ValidateRange(WireOperationTimeoutMilliseconds, 100, 120_000, nameof(WireOperationTimeoutMilliseconds));
        ValidateRange(ShutdownTimeoutMilliseconds, 100, 120_000, nameof(ShutdownTimeoutMilliseconds));
        ValidateRange(MaximumWireFrameBytes, 1_024, 32_000_000, nameof(MaximumWireFrameBytes));
        ValidateRange(MaximumPayloadBytes, 1_024, 16_000_000, nameof(MaximumPayloadBytes));
        ValidateRange(MaximumTextCharacters, 1, 1_000_000, nameof(MaximumTextCharacters));
        ValidateRange(EventQueueCapacity, 1, 16_384, nameof(EventQueueCapacity));
        if (MaximumPayloadBytes > MaximumWireFrameBytes)
        {
            throw new ArgumentException("The payload limit cannot exceed the wire-frame limit.");
        }

        if (ConnectionFactory is null)
        {
            throw new ArgumentNullException(nameof(ConnectionFactory));
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Headers ?? throw new ArgumentNullException(nameof(Headers)))
        {
            ValidateHeader(pair.Key, pair.Value);
            if (IsControlledHeader(pair.Key))
            {
                throw new ArgumentException(
                    $"The transport-controlled header '{pair.Key}' cannot be configured.",
                    nameof(Headers));
            }

            headers.Add(pair.Key, pair.Value);
        }

        return new VolcengineRealtimeTransportOptions
        {
            DialogueEndpoint = DialogueEndpoint,
            TtsEndpoint = TtsEndpoint,
            InputMode = InputMode,
            DialogueResourceId = DialogueResourceId,
            TtsResourceId = TtsResourceId,
            DialogueModel = DialogueModel,
            TtsModel = TtsModel,
            Speaker = Speaker,
            AppId = AppId,
            ApiKey = ApiKey,
            GetApiKeyAsync = GetApiKeyAsync,
            Headers = new ReadOnlyDictionary<string, string>(headers),
            ConnectionFactory = ConnectionFactory,
            InputSampleRate = InputSampleRate,
            OutputSampleRate = OutputSampleRate,
            ConnectTimeoutMilliseconds = ConnectTimeoutMilliseconds,
            WireOperationTimeoutMilliseconds = WireOperationTimeoutMilliseconds,
            ShutdownTimeoutMilliseconds = ShutdownTimeoutMilliseconds,
            MaximumWireFrameBytes = MaximumWireFrameBytes,
            MaximumPayloadBytes = MaximumPayloadBytes,
            MaximumTextCharacters = MaximumTextCharacters,
            EventQueueCapacity = EventQueueCapacity,
        };
    }

    private static void ValidateEndpoint(Uri value, string name)
    {
        if (value is null
            || !value.IsAbsoluteUri
            || value.UserInfo.Length > 0
            || (value.Scheme != "wss" && value.Scheme != "ws")
            || (value.Scheme == "ws" && !value.IsLoopback))
        {
            throw new ArgumentException(
                "A credential-free secure WebSocket endpoint is required; plaintext is loopback-only.",
                name);
        }
    }

    private static void ValidateId(string value, string name, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximum
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded identifier is required.", name);
        }
    }

    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static bool IsControlledHeader(string value) =>
        value.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
        || value.Equals("X-Api-App-ID", StringComparison.OrdinalIgnoreCase)
        || value.Equals("X-Api-Resource-Id", StringComparison.OrdinalIgnoreCase)
        || value.Equals("X-Api-Connect-Id", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static void ValidateHeader(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 256
            || name.Any(character => char.IsControl(character) || character is ':' or ' ' or '\t')
            || value is null
            || value.Length > 8_192
            || value.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new ArgumentException("A WebSocket header is invalid.");
        }
    }
}

public sealed class VolcengineRealtimeTransport : IRealtimeTransport, IRealtimeTransportCapabilities
{
    private readonly VolcengineRealtimeTransportOptions _options;

    public VolcengineRealtimeTransport(VolcengineRealtimeTransportOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
    }

    public RealtimeTransportFeatures Features =>
        RealtimeTransportFeatures.AudioOutput
        | RealtimeTransportFeatures.OutputTranscription
        | RealtimeTransportFeatures.ResponseCancellation
        | (_options.InputMode == VolcengineRealtimeInputMode.Dialogue
            ? RealtimeTransportFeatures.AudioInput
              | RealtimeTransportFeatures.InputTranscription
              | RealtimeTransportFeatures.SpeechBoundaries
              | RealtimeTransportFeatures.Handoff
            : RealtimeTransportFeatures.None);

    public async ValueTask<IRealtimeTransportSession> ConnectAsync(
        RealtimeConversationOptions options,
        CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        using var timeout = new CancellationTokenSource(_options.ConnectTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var apiKey = await ResolveApiKeyAsync(linked.Token).ConfigureAwait(false);
        var redactor = new VolcengineSecretRedactor(
            new[] { apiKey }.Concat(_options.Headers.Values));
        IVolcengineWebSocketConnection? dialogue = null;
        IVolcengineWebSocketConnection? tts = null;
        string? dialogueSessionId = null;
        try
        {
            if (_options.InputMode == VolcengineRealtimeInputMode.Dialogue)
            {
                dialogue = await ConnectOneAsync(
                        _options.DialogueEndpoint,
                        _options.DialogueResourceId,
                        apiKey,
                        linked.Token)
                    .ConfigureAwait(false);
                await InitializeConnectionAsync(dialogue, redactor, linked.Token).ConfigureAwait(false);
                dialogueSessionId = await InitializeDialogueSessionAsync(dialogue, linked.Token)
                    .ConfigureAwait(false);
            }

            tts = await ConnectOneAsync(
                    _options.TtsEndpoint,
                    _options.TtsResourceId,
                    apiKey,
                    linked.Token)
                .ConfigureAwait(false);
            await InitializeConnectionAsync(tts, redactor, linked.Token).ConfigureAwait(false);
            return new VolcengineRealtimeTransportSession(
                dialogue,
                tts,
                dialogueSessionId,
                _options,
                redactor);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            dialogue?.Dispose();
            tts?.Dispose();
            throw new TimeoutException("Volcengine realtime connection timed out.", exception);
        }
        catch (Exception exception)
        {
            dialogue?.Dispose();
            tts?.Dispose();
            throw new InvalidOperationException(
                redactor.Sanitize("Volcengine realtime setup failed: " + exception.Message));
        }
    }

    private async ValueTask<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        var task = _options.GetApiKeyAsync is null
            ? Task.FromResult(_options.ApiKey)
            : _options.GetApiKeyAsync(cancellationToken).AsTask();
        var value = await VolcengineAsync.AwaitWithCancellationAsync(task, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 16_384
            || value.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new InvalidOperationException("A bounded Volcengine API key is required.");
        }

        return value;
    }

    private async ValueTask<IVolcengineWebSocketConnection> ConnectOneAsync(
        Uri endpoint,
        string resourceId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(_options.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["X-Api-Key"] = apiKey,
            ["X-Api-Resource-Id"] = resourceId,
            ["X-Api-Connect-Id"] = Guid.NewGuid().ToString("D"),
        };
        if (_options.AppId is not null)
        {
            headers["X-Api-App-ID"] = _options.AppId;
        }

        var task = _options.ConnectionFactory(
                new VolcengineWebSocketConnectRequest(
                    endpoint,
                    headers,
                    _options.ConnectTimeoutMilliseconds),
                cancellationToken)
            .AsTask();
        try
        {
            return await VolcengineAsync.AwaitWithCancellationAsync(task, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (!task.IsCompleted)
            {
                _ = task.ContinueWith(
                    static completed =>
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
            }

            throw;
        }
    }

    private async ValueTask InitializeConnectionAsync(
        IVolcengineWebSocketConnection connection,
        VolcengineSecretRedactor redactor,
        CancellationToken cancellationToken)
    {
        await SendAsync(
                connection,
                VolcengineMessageType.FullClientRequest,
                VolcengineEvents.StartConnection,
                null,
                Encoding.UTF8.GetBytes("{}"),
                VolcengineSerialization.Json,
                VolcengineCompression.None,
                cancellationToken)
            .ConfigureAwait(false);
        var response = await ReceiveAsync(connection, cancellationToken).ConfigureAwait(false);
        EnsureExpected(response, VolcengineEvents.ConnectionStarted, redactor);
    }

    private async ValueTask<string> InitializeDialogueSessionAsync(
        IVolcengineWebSocketConnection connection,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            asr = new
            {
                extra = new
                {
                    end_smooth_window_ms = 800,
                    enable_custom_vad = false,
                },
            },
            tts = new
            {
                audio_config = new
                {
                    channel = 1,
                    format = "pcm_s16le",
                    sample_rate = _options.OutputSampleRate,
                },
            },
            dialog = new
            {
                character_manifest = "Transcribe the speaker accurately. Game decisions are handled by the host.",
                extra = new
                {
                    strict_audit = false,
                    recv_timeout = 120,
                    input_mod = "audio",
                    model = _options.DialogueModel,
                },
            },
        });
        await SendAsync(
                connection,
                VolcengineMessageType.FullClientRequest,
                VolcengineEvents.StartSession,
                sessionId,
                payload,
                VolcengineSerialization.Json,
                VolcengineCompression.Gzip,
                cancellationToken)
            .ConfigureAwait(false);
        var response = await ReceiveAsync(connection, cancellationToken).ConfigureAwait(false);
        EnsureExpected(response, VolcengineEvents.SessionStarted, new VolcengineSecretRedactor(Array.Empty<string>()));
        return sessionId;
    }

    private async ValueTask SendAsync(
        IVolcengineWebSocketConnection connection,
        VolcengineMessageType messageType,
        int eventType,
        string? sessionId,
        ReadOnlyMemory<byte> payload,
        VolcengineSerialization serialization,
        VolcengineCompression compression,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.WireOperationTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await VolcengineAsync.AwaitWithCancellationAsync(
                connection.SendBinaryAsync(
                        VolcengineWireProtocol.Encode(
                            messageType,
                            eventType,
                            sessionId,
                            payload,
                            serialization,
                            compression),
                        linked.Token)
                    .AsTask(),
                linked.Token)
            .ConfigureAwait(false);
    }

    private async ValueTask<VolcengineWireMessage> ReceiveAsync(
        IVolcengineWebSocketConnection connection,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.WireOperationTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var frame = await VolcengineAsync.AwaitWithCancellationAsync(
                connection.ReceiveBinaryAsync(_options.MaximumWireFrameBytes, linked.Token).AsTask(),
                linked.Token)
            .ConfigureAwait(false);
        return VolcengineWireProtocol.Decode(frame, _options.MaximumPayloadBytes);
    }

    private static void EnsureExpected(
        VolcengineWireMessage message,
        int expectedEvent,
        VolcengineSecretRedactor redactor)
    {
        if (message.MessageType == VolcengineMessageType.Error)
        {
            throw new InvalidOperationException(
                redactor.Sanitize($"Volcengine rejected the request ({message.ErrorCode?.ToString() ?? "unknown"})."));
        }

        if (message.EventType != expectedEvent)
        {
            throw new InvalidDataException(
                $"Volcengine returned event {message.EventType?.ToString() ?? "none"}; expected {expectedEvent}.");
        }
    }

}

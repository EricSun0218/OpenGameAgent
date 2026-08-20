using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Realtime;

namespace OpenGameAgent.Providers.OpenAI.Realtime;

public sealed class OpenAIRealtimeTransportOptions
{
    public Uri Endpoint { get; set; } = new("wss://api.openai.com/v1/realtime");

    public string? ApiKey { get; set; }

    public Func<CancellationToken, ValueTask<string?>>? GetApiKeyAsync { get; set; }

    public IReadOnlyDictionary<string, string> Headers { get; set; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public OpenAIWebSocketConnectionFactory ConnectionFactory { get; set; } =
        OpenAIWebSocketConnections.ConnectAsync;

    public int ConnectTimeoutMilliseconds { get; set; } = 15_000;

    public int MaximumWireEventCharacters { get; set; } = 1_000_000;

    public int MaximumDecodedAudioBytes { get; set; } = 4_194_304;

    public int WireOperationTimeoutMilliseconds { get; set; } = 15_000;

    internal OpenAIRealtimeTransportOptions Snapshot()
    {
        if (Endpoint is null
            || !Endpoint.IsAbsoluteUri
            || Endpoint.UserInfo.Length > 0
            || (Endpoint.Scheme != "wss" && Endpoint.Scheme != "ws"))
        {
            throw new ArgumentException("A credential-free WebSocket endpoint is required.", nameof(Endpoint));
        }

        if (Endpoint.Scheme == "ws" && !Endpoint.IsLoopback)
        {
            throw new ArgumentException("Plaintext WebSockets are allowed only for loopback endpoints.", nameof(Endpoint));
        }

        if (ConnectTimeoutMilliseconds is < 100 or > 120_000)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeoutMilliseconds));
        }

        if (MaximumWireEventCharacters is < 1 or > 8_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumWireEventCharacters));
        }

        if (MaximumDecodedAudioBytes is < 2 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDecodedAudioBytes));
        }

        if (WireOperationTimeoutMilliseconds is < 100 or > 120_000)
        {
            throw new ArgumentOutOfRangeException(nameof(WireOperationTimeoutMilliseconds));
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
                throw new ArgumentException($"The transport-controlled header '{pair.Key}' cannot be configured.", nameof(Headers));
            }

            headers.Add(pair.Key, pair.Value);
        }

        return new OpenAIRealtimeTransportOptions
        {
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            GetApiKeyAsync = GetApiKeyAsync,
            Headers = new ReadOnlyDictionary<string, string>(headers),
            ConnectionFactory = ConnectionFactory,
            ConnectTimeoutMilliseconds = ConnectTimeoutMilliseconds,
            MaximumWireEventCharacters = MaximumWireEventCharacters,
            MaximumDecodedAudioBytes = MaximumDecodedAudioBytes,
            WireOperationTimeoutMilliseconds = WireOperationTimeoutMilliseconds,
        };
    }

    private static bool IsControlledHeader(string value) =>
        value.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
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

public sealed class OpenAIRealtimeTransport : IRealtimeTransport
{
    private readonly OpenAIRealtimeTransportOptions _options;

    public OpenAIRealtimeTransport(OpenAIRealtimeTransportOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
    }

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
        string? apiKey;
        try
        {
            apiKey = _options.GetApiKeyAsync is null
                ? _options.ApiKey
                : await AwaitWithCancellationAsync(
                        _options.GetApiKeyAsync(linked.Token).AsTask(),
                        linked.Token)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Realtime authentication exceeded the configured connect timeout.", exception);
        }
        if (string.IsNullOrWhiteSpace(apiKey)
            || apiKey.Length > 16_384
            || apiKey.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new InvalidOperationException("A bounded OpenAI API key is required.");
        }

        var endpoint = AddModel(_options.Endpoint, options.Model);
        var headers = new Dictionary<string, string>(_options.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + apiKey,
        };
        IOpenAIWebSocketConnection connection;
        try
        {
            connection = await AwaitConnectionWithCancellationAsync(
                    _options.ConnectionFactory(
                            new OpenAIWebSocketConnectRequest(
                                endpoint,
                                new ReadOnlyDictionary<string, string>(headers),
                                _options.ConnectTimeoutMilliseconds),
                            linked.Token)
                        .AsTask(),
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Realtime connection exceeded the configured timeout.", exception);
        }
        try
        {
            var session = new OpenAIRealtimeTransportSession(
                connection,
                options,
                _options.MaximumWireEventCharacters,
                _options.MaximumDecodedAudioBytes,
                _options.WireOperationTimeoutMilliseconds);
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static Uri AddModel(Uri endpoint, string model)
    {
        var builder = new UriBuilder(endpoint);
        var modelPair = "model=" + Uri.EscapeDataString(model);
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? modelPair
            : builder.Query.TrimStart('?') + "&" + modelPair;
        return builder.Uri;
    }

    private static async Task<T> AwaitWithCancellationAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
    {
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellation);
        if (await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false) != task)
        {
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new OperationCanceledException(cancellationToken);
        }

        return await task.ConfigureAwait(false);
    }

    private static async Task<IOpenAIWebSocketConnection> AwaitConnectionWithCancellationAsync(
        Task<IOpenAIWebSocketConnection> task,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AwaitWithCancellationAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = task.ContinueWith(
                static completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        completed.Result.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }
}

internal sealed class OpenAIRealtimeTransportSession : IRealtimeTransportSession
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IOpenAIWebSocketConnection _connection;
    private readonly RealtimeConversationOptions _options;
    private readonly int _maximumWireEventCharacters;
    private readonly int _maximumDecodedAudioBytes;
    private readonly int _wireOperationTimeoutMilliseconds;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly StringBuilder _inputTranscript = new();
    private int _disposed;

    public OpenAIRealtimeTransportSession(
        IOpenAIWebSocketConnection connection,
        RealtimeConversationOptions options,
        int maximumWireEventCharacters,
        int maximumDecodedAudioBytes,
        int wireOperationTimeoutMilliseconds)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options;
        _maximumWireEventCharacters = maximumWireEventCharacters;
        _maximumDecodedAudioBytes = maximumDecodedAudioBytes;
        _wireOperationTimeoutMilliseconds = wireOperationTimeoutMilliseconds;
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken) => SendJsonAsync(new
    {
        type = "session.update",
        session = new
        {
            type = "realtime",
            instructions = _options.StartupContextJson is null
                ? _options.Instructions
                : _options.Instructions + "\n\n<startup_context>\n" + _options.StartupContextJson + "\n</startup_context>",
            output_modalities = new[]
            {
                _options.OutputModality == RealtimeOutputModality.Audio ? "audio" : "text",
            },
            audio = new
            {
                input = new
                {
                    format = new
                    {
                        type = "audio/pcm",
                        rate = 24_000,
                    },
                    noise_reduction = new
                    {
                        type = "near_field",
                    },
                    transcription = new
                    {
                        model = "gpt-4o-mini-transcribe",
                    },
                    turn_detection = new
                    {
                        type = "server_vad",
                        create_response = true,
                        interrupt_response = true,
                        silence_duration_ms = 500,
                    },
                },
                output = new
                {
                    format = new
                    {
                        type = "audio/pcm",
                        rate = 24_000,
                    },
                    voice = _options.Voice,
                },
            },
            tools = new object[]
            {
                new
                {
                    type = "function",
                    name = "handoff",
                    description = "Delegate planning, tool use, or authoritative game actions to the game agent.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            transcript = new { type = "string" },
                            context = new { type = "object", additionalProperties = true },
                        },
                        required = new[] { "transcript" },
                        additionalProperties = false,
                    },
                },
                new
                {
                    type = "function",
                    name = "behavior",
                    description = "Start a reversible presentation behavior such as gaze, gesture, expression, or locomotion intent. Never use for authoritative world mutations.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            channel = new { type = "string" },
                            behavior = new { type = "string" },
                            arguments = new { type = "object", additionalProperties = true },
                            priority = new { type = "integer" },
                        },
                        required = new[] { "channel", "behavior", "arguments" },
                        additionalProperties = false,
                    },
                },
            },
            tool_choice = "auto",
        },
    }, cancellationToken);

    public async IAsyncEnumerable<RealtimeConversationEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _connection.IsOpen)
        {
            var payload = await AwaitWithCancellationAsync(
                    _connection.ReceiveTextAsync(
                            _maximumWireEventCharacters,
                            cancellationToken)
                        .AsTask(),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var value in Parse(payload))
            {
                yield return value;
            }
        }
    }

    public ValueTask SendAudioAsync(RealtimeAudioFrame frame, CancellationToken cancellationToken)
    {
        if (frame.SampleRate != 24_000 || frame.Channels != 1)
        {
            throw new ArgumentException("The OpenAI realtime transport requires 24 kHz mono PCM16 input.", nameof(frame));
        }

        return SendJsonAsync(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(frame.Pcm16.ToArray()),
        }, cancellationToken);
    }

    public ValueTask SendTextAsync(
        string text,
        RealtimeTextRole role,
        CancellationToken cancellationToken) => SendConversationItemAsync(
        role switch
        {
            RealtimeTextRole.User => "user",
            RealtimeTextRole.Assistant => "assistant",
            RealtimeTextRole.Developer => "developer",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        },
        text,
        cancellationToken);

    public async ValueTask SendHandoffAsync(
        string handoffId,
        string text,
        RealtimeHandoffPhase phase,
        bool completed,
        CancellationToken cancellationToken)
    {
        if (completed)
        {
            await SendJsonAsync(new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "function_call_output",
                    call_id = handoffId,
                    output = text,
                },
            }, cancellationToken).ConfigureAwait(false);
            await SendJsonAsync(new { type = "response.create" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendConversationItemAsync(
                phase == RealtimeHandoffPhase.Final ? "assistant" : "developer",
                text,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SendBehaviorResultAsync(
        RealtimeBehaviorResult result,
        CancellationToken cancellationToken)
    {
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = result.BehaviorId,
                output = JsonSerializer.Serialize(new
                {
                    disposition = result.Disposition.ToString().ToLowerInvariant(),
                    detailsJson = result.DetailsJson,
                }),
            },
        }, cancellationToken).ConfigureAwait(false);
        await SendJsonAsync(new { type = "response.create" }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CancelResponseAsync(CancellationToken cancellationToken) =>
        SendJsonAsync(new { type = "response.cancel" }, cancellationToken);

    public ValueTask TruncateAudioAsync(
        string itemId,
        int audioEndMilliseconds,
        CancellationToken cancellationToken) => SendJsonAsync(new
        {
            type = "conversation.item.truncate",
            item_id = itemId,
            content_index = 0,
            audio_end_ms = Math.Max(0, audioEndMilliseconds),
        }, cancellationToken);

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await AwaitWithCancellationAsync(
                _connection.CloseAsync("done", cancellationToken).AsTask(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _connection.Dispose();
            _sendGate.Dispose();
        }

        return default;
    }

    private ValueTask SendConversationItemAsync(
        string role,
        string text,
        CancellationToken cancellationToken) => SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role,
                content = new[]
            {
                new
                {
                    type = role == "assistant" ? "output_text" : "input_text",
                    text,
                },
            },
            },
        }, cancellationToken);

    private async ValueTask SendJsonAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        if (json.Length > _maximumWireEventCharacters)
        {
            throw new InvalidDataException("The realtime outbound event exceeded the configured limit.");
        }

        using var timeout = new CancellationTokenSource(_wireOperationTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await AwaitWithCancellationAsync(
                    _connection.SendTextAsync(json, linked.Token).AsTask(),
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("A realtime wire operation exceeded its configured timeout.", exception);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private IEnumerable<RealtimeConversationEvent> Parse(string payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A realtime event must be a JSON object.");
        }

        var type = RequiredString(root, "type", 256);
        switch (type)
        {
            case "session.created":
            case "session.updated":
                yield return new RealtimeConversationEvent(
                    RealtimeConversationEventKind.SessionUpdated,
                    itemId: OptionalNestedString(root, "session", "id", 256));
                yield break;
            case "input_audio_buffer.speech_started":
                yield return new RealtimeConversationEvent(
                    RealtimeConversationEventKind.InputSpeechStarted,
                    itemId: OptionalString(root, "item_id", 256));
                yield break;
            case "conversation.item.input_audio_transcription.delta":
                yield return new RealtimeConversationEvent(
                    RealtimeConversationEventKind.InputTranscriptDelta,
                    text: RequiredString(root, "delta", _options.MaximumTextCharacters),
                    itemId: OptionalString(root, "item_id", 256));
                yield break;
            case "conversation.item.input_audio_transcription.completed":
                {
                    var transcript = RequiredString(root, "transcript", _options.MaximumTextCharacters);
                    if (_inputTranscript.Length > 0)
                    {
                        _inputTranscript.Append('\n');
                    }

                    _inputTranscript.Append(transcript);
                    if (_inputTranscript.Length > _options.MaximumTextCharacters)
                    {
                        _inputTranscript.Remove(
                            0,
                            _inputTranscript.Length - _options.MaximumTextCharacters);
                    }

                    yield return new RealtimeConversationEvent(
                        RealtimeConversationEventKind.InputTranscriptDone,
                        text: transcript,
                        itemId: OptionalString(root, "item_id", 256));
                    yield break;
                }
            case "response.audio_transcript.delta":
            case "response.output_audio_transcript.delta":
            case "response.output_text.delta":
                yield return new RealtimeConversationEvent(
                    RealtimeConversationEventKind.OutputTranscriptDelta,
                    text: RequiredString(root, "delta", _options.MaximumTextCharacters),
                    itemId: OptionalString(root, "item_id", 256),
                    responseId: OptionalString(root, "response_id", 256));
                yield break;
            case "response.audio_transcript.done":
            case "response.output_audio_transcript.done":
            case "response.output_text.done":
                yield return new RealtimeConversationEvent(
                    RealtimeConversationEventKind.OutputTranscriptDone,
                    text: OptionalString(
                        root,
                        type == "response.output_text.done" ? "text" : "transcript",
                        _options.MaximumTextCharacters),
                    itemId: OptionalString(root, "item_id", 256),
                    responseId: OptionalString(root, "response_id", 256));
                yield break;
            case "response.audio.delta":
            case "response.output_audio.delta":
                {
                    var bytes = DecodeBase64(RequiredString(root, "delta", _maximumWireEventCharacters));
                    var sampleRate = OptionalInt32(root, "sample_rate") ?? 24_000;
                    var channels = OptionalInt32(root, "channels")
                        ?? OptionalInt32(root, "num_channels")
                        ?? 1;
                    yield return new RealtimeConversationEvent(
                        RealtimeConversationEventKind.AudioOutput,
                        audio: new RealtimeAudioFrame(
                            bytes,
                            sampleRate,
                            channels,
                            itemId: OptionalString(root, "item_id", 256)),
                        itemId: OptionalString(root, "item_id", 256),
                        responseId: OptionalString(root, "response_id", 256));
                    yield break;
                }
            case "response.created":
                yield return new RealtimeConversationEvent(
                    RealtimeConversationEventKind.ResponseStarted,
                    responseId: OptionalNestedString(root, "response", "id", 256));
                yield break;
            case "response.done":
                {
                    var status = OptionalNestedString(root, "response", "status", 256);
                    yield return new RealtimeConversationEvent(
                        string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                            ? RealtimeConversationEventKind.ResponseCancelled
                            : RealtimeConversationEventKind.ResponseDone,
                        responseId: OptionalNestedString(root, "response", "id", 256));
                    yield break;
                }
            case "response.cancelled":
                yield return new RealtimeConversationEvent(
                    RealtimeConversationEventKind.ResponseCancelled,
                    responseId: OptionalString(root, "response_id", 256)
                        ?? OptionalNestedString(root, "response", "id", 256));
                yield break;
            case "response.function_call_arguments.done":
            case "response.output_item.done":
            case "conversation.item.done":
                {
                    var item = type is "response.output_item.done" or "conversation.item.done"
                               && root.TryGetProperty("item", out var outputItem)
                               && outputItem.ValueKind == JsonValueKind.Object
                        ? outputItem
                        : root;
                    if (type is "response.output_item.done" or "conversation.item.done"
                        && item.TryGetProperty("type", out var itemType)
                        && itemType.ValueKind == JsonValueKind.String
                        && itemType.GetString() != "function_call")
                    {
                        yield break;
                    }

                    var name = RequiredString(item, "name", 256);
                    var callId = RequiredString(item, "call_id", 256);
                    var arguments = RequiredString(item, "arguments", _options.MaximumTextCharacters);
                    if (name == "handoff")
                    {
                        yield return ParseHandoff(callId, arguments);
                    }
                    else if (name == "behavior")
                    {
                        yield return ParseBehavior(callId, arguments);
                    }

                    yield break;
                }
            case "error":
                yield return new RealtimeConversationEvent(
                    RealtimeConversationEventKind.Error,
                    error: Sanitize(
                        OptionalNestedString(root, "error", "message", 4_096)
                            ?? "The realtime provider returned an error."));
                yield break;
            default:
                yield break;
        }
    }

    private RealtimeConversationEvent ParseHandoff(string callId, string arguments)
    {
        using var document = JsonDocument.Parse(arguments, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var transcript = OptionalString(root, "transcript", _options.MaximumTextCharacters);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            transcript = _inputTranscript.ToString();
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            throw new InvalidDataException("A handoff requires a transcript.");
        }

        _inputTranscript.Clear();

        string? context = null;
        if (root.TryGetProperty("context", out var contextElement)
            && contextElement.ValueKind != JsonValueKind.Null)
        {
            context = contextElement.GetRawText();
        }

        return new RealtimeConversationEvent(
            RealtimeConversationEventKind.HandoffRequested,
            handoff: new RealtimeHandoffRequest(callId, transcript, context));
    }

    private RealtimeConversationEvent ParseBehavior(string callId, string arguments)
    {
        using var document = JsonDocument.Parse(arguments, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var argumentsJson = root.TryGetProperty("arguments", out var behaviorArguments)
            ? behaviorArguments.GetRawText()
            : "{}";
        var priority = root.TryGetProperty("priority", out var priorityElement)
                       && priorityElement.TryGetInt32(out var parsedPriority)
            ? parsedPriority
            : 0;
        return new RealtimeConversationEvent(
            RealtimeConversationEventKind.BehaviorRequested,
            behavior: new RealtimeBehaviorRequest(
                callId,
                RequiredString(root, "channel", 256),
                RequiredString(root, "behavior", 256),
                argumentsJson,
                priority));
    }

    private byte[] DecodeBase64(string value)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Realtime audio was not valid base64.", exception);
        }

        if (bytes.Length == 0 || bytes.Length > _maximumDecodedAudioBytes || bytes.Length % 2 != 0)
        {
            throw new InvalidDataException("Realtime audio exceeded its limit or was not PCM16.");
        }

        return bytes;
    }

    private static string RequiredString(JsonElement element, string name, int maximum)
    {
        var value = OptionalString(element, name, maximum);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Realtime field '{name}' is required.")
            : value;
    }

    private static string? OptionalString(JsonElement element, string name, int maximum)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Realtime field '{name}' must be a string.");
        }

        var value = property.GetString()!;
        if (value.Length > maximum || value.Any(character => character == '\0'))
        {
            throw new InvalidDataException($"Realtime field '{name}' exceeded its limit.");
        }

        return value;
    }

    private static string? OptionalNestedString(
        JsonElement element,
        string container,
        string name,
        int maximum) =>
        element.TryGetProperty(container, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? OptionalString(nested, name, maximum)
            : null;

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 4_096));
        foreach (var character in value.Take(4_096))
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }

    private static int? OptionalInt32(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : throw new InvalidDataException($"Realtime field '{property}' must be an integer.");
    }

    private static async Task<T> AwaitWithCancellationAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
    {
        await AwaitWithCancellationAsync((Task)task, cancellationToken).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    private static async Task AwaitWithCancellationAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellation);
        if (await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false) != task)
        {
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new OperationCanceledException(cancellationToken);
        }

        await task.ConfigureAwait(false);
    }
}

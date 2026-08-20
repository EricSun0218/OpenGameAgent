using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenGameAgent.Realtime;

namespace OpenGameAgent.Providers.Volcengine.Realtime;

internal sealed class VolcengineRealtimeTransportSession : IRealtimeTransportSession
{
    private readonly IVolcengineWebSocketConnection? _dialogue;
    private readonly IVolcengineWebSocketConnection _tts;
    private readonly VolcengineRealtimeTransportOptions _options;
    private readonly string _speaker;
    private readonly VolcengineSecretRedactor _redactor;
    private readonly VolcengineBoundedQueue<RealtimeConversationEvent> _events;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _dialogueSendGate = new(1, 1);
    private readonly SemaphoreSlim _ttsSendGate = new(1, 1);
    private readonly SemaphoreSlim _ttsCommandGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _lifecycleGate = new();
    private readonly StringBuilder _inputTranscript = new();
    private readonly Dictionary<string, string> _ttsHandoffs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<bool>> _ttsStartAcks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _ttsTranscripts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignoredTtsSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedTtsSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedTtsSessions = new(StringComparer.Ordinal);
    private readonly Task? _dialoguePump;
    private readonly Task _ttsPump;
    private readonly string? _dialogueSessionId;
    private string? _activeTtsSessionId;
    private string? _activeHandoffId;
    private Task? _closeTask;
    private Task? _disposeTask;
    private int _inputSequence;
    private int _closed;
    private int _eventsCompleted;

    internal VolcengineRealtimeTransportSession(
        IVolcengineWebSocketConnection? dialogue,
        IVolcengineWebSocketConnection tts,
        string? dialogueSessionId,
        string speaker,
        VolcengineRealtimeTransportOptions options,
        VolcengineSecretRedactor redactor)
    {
        _dialogue = dialogue;
        _tts = tts ?? throw new ArgumentNullException(nameof(tts));
        _speaker = !string.IsNullOrWhiteSpace(speaker)
            ? speaker
            : throw new ArgumentException("A speaker is required.", nameof(speaker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _events = new VolcengineBoundedQueue<RealtimeConversationEvent>(options.EventQueueCapacity);
        _dialogueSessionId = dialogue is null
            ? null
            : dialogueSessionId
                ?? throw new ArgumentNullException(nameof(dialogueSessionId));
        _dialoguePump = dialogue is null ? null : Task.Run(ReadDialogueAsync);
        _ttsPump = Task.Run(ReadTtsAsync);
    }

    public async IAsyncEnumerable<RealtimeConversationEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var value = await _events.DequeueAsync(cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                yield break;
            }

            yield return value;
        }
    }

    public async ValueTask SendAudioAsync(
        RealtimeAudioFrame frame,
        CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        if (_dialogue is null || _dialogueSessionId is null)
        {
            throw new NotSupportedException("This Volcengine transport was configured without audio input.");
        }

        if (frame is null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (frame.SampleRate != _options.InputSampleRate || frame.Channels != 1)
        {
            throw new ArgumentException(
                $"Volcengine dialogue input requires mono PCM16 at {_options.InputSampleRate} Hz.",
                nameof(frame));
        }

        if (frame.Pcm16.Length > _options.MaximumPayloadBytes)
        {
            throw new ArgumentException("The audio frame exceeded the provider payload limit.", nameof(frame));
        }

        await SendDialogueAsync(
                VolcengineMessageType.AudioOnlyClient,
                VolcengineEvents.TaskRequest,
                _dialogueSessionId,
                frame.Pcm16,
                VolcengineSerialization.Raw,
                VolcengineCompression.Gzip,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SendTextAsync(
        string text,
        RealtimeTextRole role,
        CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        var bounded = RequireText(text, nameof(text));
        if (role == RealtimeTextRole.User)
        {
            var handoffId = "volc-text-" + Interlocked.Increment(ref _inputSequence).ToString();
            await _events.EnqueueAsync(
                    new RealtimeConversationEvent(
                        RealtimeConversationEventKind.InputTranscriptDone,
                        text: bounded,
                        itemId: handoffId),
                    cancellationToken)
                .ConfigureAwait(false);
            await _events.EnqueueAsync(
                    new RealtimeConversationEvent(
                        RealtimeConversationEventKind.HandoffRequested,
                        handoff: new RealtimeHandoffRequest(handoffId, bounded)),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (role == RealtimeTextRole.Assistant)
        {
            await SendHandoffAsync(
                    "volc-output-" + Guid.NewGuid().ToString("N"),
                    bounded,
                    RealtimeHandoffPhase.Final,
                    completed: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        throw new NotSupportedException(
            "Developer messages are not sent to the speech provider; keep game instructions in the agent runtime.");
    }

    public async ValueTask SendHandoffAsync(
        string handoffId,
        string text,
        RealtimeHandoffPhase phase,
        bool completed,
        CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        RequireId(handoffId, nameof(handoffId));
        if (!Enum.IsDefined(typeof(RealtimeHandoffPhase), phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        var boundedText = text.Length == 0 && completed
            ? string.Empty
            : RequireText(text, nameof(text));
        await _ttsCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeHandoffId is not null
                && !string.Equals(_activeHandoffId, handoffId, StringComparison.Ordinal))
            {
                await CancelActiveTtsAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_activeTtsSessionId is null)
            {
                await StartTtsSessionAsync(handoffId, cancellationToken).ConfigureAwait(false);
            }

            var sessionId = _activeTtsSessionId
                ?? throw new InvalidOperationException("The TTS session was not initialized.");
            if (boundedText.Length > 0)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    req_params = new { text = boundedText },
                });
                await SendTtsAsync(
                        VolcengineMessageType.FullClientRequest,
                        VolcengineEvents.TaskRequest,
                        sessionId,
                        payload,
                        VolcengineSerialization.Json,
                        VolcengineCompression.None,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (completed)
            {
                await SendTtsAsync(
                        VolcengineMessageType.FullClientRequest,
                        VolcengineEvents.FinishSession,
                        sessionId,
                        Encoding.UTF8.GetBytes("{}"),
                        VolcengineSerialization.Json,
                        VolcengineCompression.None,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _ttsCommandGate.Release();
        }
    }

    public ValueTask SendBehaviorResultAsync(
        RealtimeBehaviorResult result,
        CancellationToken cancellationToken)
    {
        _ = result ?? throw new ArgumentNullException(nameof(result));
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        return default;
    }

    public async ValueTask CancelResponseAsync(CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        await _ttsCommandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CancelActiveTtsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ttsCommandGate.Release();
        }
    }

    public ValueTask TruncateAudioAsync(
        string itemId,
        int audioEndMilliseconds,
        CancellationToken cancellationToken)
    {
        RequireId(itemId, nameof(itemId));
        if (audioEndMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioEndMilliseconds));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        // The provider stores no authoritative OGA conversation item. Canceling
        // the TTS sub-session is sufficient; there is no remote item to truncate.
        return default;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        var task = GetOrStartClose();
        return new ValueTask(VolcengineAsync.AwaitWithCancellationAsync(task, cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_lifecycleGate)
        {
            task = _disposeTask ??= DisposeCoreAsync();
        }

        return new ValueTask(task);
    }

    private Task GetOrStartClose()
    {
        lock (_lifecycleGate)
        {
            if (_closeTask is null)
            {
                Volatile.Write(ref _closed, 1);
                _closeTask = CloseCoreAsync();
            }

            return _closeTask;
        }
    }

    private async Task CloseCoreAsync()
    {
        using var timeout = new CancellationTokenSource(_options.ShutdownTimeoutMilliseconds);
        try
        {
            await _ttsCommandGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await CancelActiveTtsAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _ttsCommandGate.Release();
            }

            if (_dialogue is not null && _dialogueSessionId is not null)
            {
                await SendDialogueAsync(
                        VolcengineMessageType.FullClientRequest,
                        VolcengineEvents.FinishSession,
                        _dialogueSessionId,
                        Encoding.UTF8.GetBytes("{}"),
                        VolcengineSerialization.Json,
                        VolcengineCompression.Gzip,
                        timeout.Token)
                    .ConfigureAwait(false);
                await SendDialogueAsync(
                        VolcengineMessageType.FullClientRequest,
                        VolcengineEvents.FinishConnection,
                        null,
                        Encoding.UTF8.GetBytes("{}"),
                        VolcengineSerialization.Json,
                        VolcengineCompression.Gzip,
                        timeout.Token)
                    .ConfigureAwait(false);
            }

            await SendTtsAsync(
                    VolcengineMessageType.FullClientRequest,
                    VolcengineEvents.FinishConnection,
                    null,
                    Encoding.UTF8.GetBytes("{}"),
                    VolcengineSerialization.Json,
                    VolcengineCompression.None,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            _lifetime.Cancel();
            await CloseSocketAsync(_dialogue, timeout.Token).ConfigureAwait(false);
            await CloseSocketAsync(_tts, timeout.Token).ConfigureAwait(false);
            await AwaitPumpsAsync().ConfigureAwait(false);
            CompleteEvents();
        }
    }

    private async Task DisposeCoreAsync()
    {
        await GetOrStartClose().ConfigureAwait(false);
        _dialogue?.Dispose();
        _tts.Dispose();
        _dialogueSendGate.Dispose();
        _ttsSendGate.Dispose();
        _ttsCommandGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ReadDialogueAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && _dialogue is { IsOpen: true })
            {
                var message = await ReceiveAsync(_dialogue, _lifetime.Token).ConfigureAwait(false);
                await HandleDialogueMessageAsync(message, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await EmitErrorAsync(exception, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                CompleteEvents();
            }
        }
    }

    private async Task ReadTtsAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && _tts.IsOpen)
            {
                var message = await ReceiveAsync(_tts, _lifetime.Token).ConfigureAwait(false);
                await HandleTtsMessageAsync(message, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await EmitErrorAsync(exception, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                CompleteEvents();
            }
        }
    }

    private async ValueTask HandleDialogueMessageAsync(
        VolcengineWireMessage message,
        CancellationToken cancellationToken)
    {
        if (message.MessageType == VolcengineMessageType.Error)
        {
            await EmitProviderErrorAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (message.EventType)
        {
            case VolcengineEvents.AsrInfo:
                _inputTranscript.Clear();
                await _events.EnqueueAsync(
                        new RealtimeConversationEvent(RealtimeConversationEventKind.InputSpeechStarted),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case VolcengineEvents.AsrResponse:
                {
                    var text = ExtractText(message.Payload);
                    if (text.Length == 0)
                    {
                        break;
                    }

                    _inputTranscript.Clear();
                    _inputTranscript.Append(text);
                    await _events.EnqueueAsync(
                            new RealtimeConversationEvent(
                                RealtimeConversationEventKind.InputTranscriptDelta,
                                text: text,
                                itemId: message.SessionId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
            case VolcengineEvents.AsrEnded:
                await _events.EnqueueAsync(
                        new RealtimeConversationEvent(RealtimeConversationEventKind.InputSpeechStopped),
                        cancellationToken)
                    .ConfigureAwait(false);
                await FinalizeInputAsync(message.SessionId, cancellationToken).ConfigureAwait(false);
                break;
            case VolcengineEvents.SessionFailed:
            case VolcengineEvents.ConnectionFailed:
                await EmitProviderErrorAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case VolcengineEvents.ConnectionFinished:
                CompleteEvents();
                break;
        }
    }

    private async ValueTask FinalizeInputAsync(
        string? providerSessionId,
        CancellationToken cancellationToken)
    {
        var transcript = _inputTranscript.ToString().Trim();
        _inputTranscript.Clear();
        if (transcript.Length == 0)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _inputSequence);
        var handoffId = "volc-" + sequence.ToString() + "-" + Guid.NewGuid().ToString("N");
        await _events.EnqueueAsync(
                new RealtimeConversationEvent(
                    RealtimeConversationEventKind.InputTranscriptDone,
                    text: transcript,
                    itemId: providerSessionId ?? handoffId),
                cancellationToken)
            .ConfigureAwait(false);
        await _events.EnqueueAsync(
                new RealtimeConversationEvent(
                    RealtimeConversationEventKind.HandoffRequested,
                    handoff: new RealtimeHandoffRequest(handoffId, transcript)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask HandleTtsMessageAsync(
        VolcengineWireMessage message,
        CancellationToken cancellationToken)
    {
        if (message.MessageType == VolcengineMessageType.Error)
        {
            await EmitProviderErrorAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        var sessionId = message.SessionId;
        if (sessionId is not null && IsIgnored(sessionId))
        {
            if (message.EventType is VolcengineEvents.SessionCanceled
                or VolcengineEvents.SessionFinished
                or VolcengineEvents.SessionFailed)
            {
                CompleteIgnored(sessionId);
            }

            return;
        }

        switch (message.EventType)
        {
            case VolcengineEvents.SessionStarted:
                if (sessionId is not null)
                {
                    CompleteStartAck(sessionId);
                }

                break;
            case VolcengineEvents.TtsSentenceStart:
                if (sessionId is not null)
                {
                    await EmitResponseStartedAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }

                break;
            case VolcengineEvents.TtsResponse:
                if (sessionId is not null)
                {
                    await EmitAudioAsync(sessionId, message.Payload, cancellationToken).ConfigureAwait(false);
                }

                break;
            case VolcengineEvents.TtsSubtitle:
                if (sessionId is not null)
                {
                    await EmitSubtitlesAsync(sessionId, message.Payload, cancellationToken).ConfigureAwait(false);
                }

                break;
            case VolcengineEvents.TtsEnded:
            case VolcengineEvents.SessionFinished:
                if (sessionId is not null)
                {
                    await CompleteTtsAsync(sessionId, cancelled: false, cancellationToken).ConfigureAwait(false);
                }

                break;
            case VolcengineEvents.SessionCanceled:
                if (sessionId is not null)
                {
                    await CompleteTtsAsync(sessionId, cancelled: true, cancellationToken).ConfigureAwait(false);
                }

                break;
            case VolcengineEvents.SessionFailed:
            case VolcengineEvents.ConnectionFailed:
                await EmitProviderErrorAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case VolcengineEvents.ConnectionFinished:
                CompleteEvents();
                break;
            default:
                if (message.MessageType == VolcengineMessageType.AudioOnlyServer
                    && !message.Payload.IsEmpty)
                {
                    var audioSessionId = sessionId;
                    if (audioSessionId is null)
                    {
                        lock (_stateGate)
                        {
                            audioSessionId = _activeTtsSessionId;
                        }
                    }

                    if (audioSessionId is not null)
                    {
                        await EmitAudioAsync(audioSessionId, message.Payload, cancellationToken).ConfigureAwait(false);
                    }
                }

                break;
        }
    }

    private async ValueTask EmitResponseStartedAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        string? handoff;
        lock (_stateGate)
        {
            if (!_startedTtsSessions.Add(sessionId))
            {
                return;
            }

            _ttsHandoffs.TryGetValue(sessionId, out handoff);
        }

        await _events.EnqueueAsync(
                new RealtimeConversationEvent(
                    RealtimeConversationEventKind.ResponseStarted,
                    itemId: sessionId,
                    responseId: handoff ?? sessionId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask EmitAudioAsync(
        string sessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.IsEmpty
            || payload.Length > _options.MaximumPayloadBytes
            || payload.Length % 2 != 0)
        {
            throw new InvalidDataException("Volcengine returned invalid PCM16 audio.");
        }

        await EmitResponseStartedAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string responseId;
        lock (_stateGate)
        {
            responseId = _ttsHandoffs.TryGetValue(sessionId, out var value) ? value : sessionId;
        }

        await _events.EnqueueAsync(
                new RealtimeConversationEvent(
                    RealtimeConversationEventKind.AudioOutput,
                    audio: new RealtimeAudioFrame(
                        payload.ToArray(),
                        _options.OutputSampleRate,
                        1,
                        sessionId),
                    itemId: sessionId,
                    responseId: responseId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask EmitSubtitlesAsync(
        string sessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var document = ParseJson(payload);
        var root = document.RootElement;
        if (TryGetArray(root, "words", out var words))
        {
            foreach (var word in words.EnumerateArray().Take(4096))
            {
                if (word.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var text = OptionalString(word, "word") ?? OptionalString(word, "text");
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var timing = ParseTiming(word, wordLevel: true);
                await EmitSubtitleAsync(sessionId, text, timing, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var fallback = ExtractText(root);
        if (fallback.Length > 0)
        {
            await EmitSubtitleAsync(
                    sessionId,
                    fallback,
                    ParseTiming(root, wordLevel: false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask EmitSubtitleAsync(
        string sessionId,
        string text,
        RealtimeTranscriptTiming? timing,
        CancellationToken cancellationToken)
    {
        string responseId;
        lock (_stateGate)
        {
            responseId = _ttsHandoffs.TryGetValue(sessionId, out var value) ? value : sessionId;
            if (!_ttsTranscripts.TryGetValue(sessionId, out var transcript))
            {
                transcript = new StringBuilder();
                _ttsTranscripts.Add(sessionId, transcript);
            }

            if (transcript.Length + text.Length <= _options.MaximumTextCharacters)
            {
                transcript.Append(text);
            }
        }

        await _events.EnqueueAsync(
                new RealtimeConversationEvent(
                    RealtimeConversationEventKind.OutputTranscriptDelta,
                    text: text,
                    itemId: sessionId,
                    responseId: responseId,
                    timing: timing),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask CompleteTtsAsync(
        string sessionId,
        bool cancelled,
        CancellationToken cancellationToken)
    {
        string responseId;
        string transcript;
        lock (_stateGate)
        {
            if (!_completedTtsSessions.Add(sessionId))
            {
                return;
            }

            responseId = _ttsHandoffs.TryGetValue(sessionId, out var value) ? value : sessionId;
            transcript = _ttsTranscripts.TryGetValue(sessionId, out var builder)
                ? builder.ToString()
                : string.Empty;
            _ttsHandoffs.Remove(sessionId);
            _ttsTranscripts.Remove(sessionId);
            _ttsStartAcks.Remove(sessionId);
            if (string.Equals(_activeTtsSessionId, sessionId, StringComparison.Ordinal))
            {
                _activeTtsSessionId = null;
                _activeHandoffId = null;
            }
        }

        if (transcript.Length > 0)
        {
            await _events.EnqueueAsync(
                    new RealtimeConversationEvent(
                        RealtimeConversationEventKind.OutputTranscriptDone,
                        text: transcript,
                        itemId: sessionId,
                        responseId: responseId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _events.EnqueueAsync(
                new RealtimeConversationEvent(
                    cancelled
                        ? RealtimeConversationEventKind.ResponseCancelled
                        : RealtimeConversationEventKind.ResponseDone,
                    itemId: sessionId,
                    responseId: responseId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask StartTtsSessionAsync(
        string handoffId,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateGate)
        {
            _activeTtsSessionId = sessionId;
            _activeHandoffId = handoffId;
            _ttsHandoffs.Add(sessionId, handoffId);
            _ttsStartAcks.Add(sessionId, acknowledgement);
        }

        var additions = JsonSerializer.Serialize(new
        {
            disable_markdown_filter = false,
            disable_emoji_filter = false,
        });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            req_params = new
            {
                model = _options.TtsModel,
                speaker = _speaker,
                audio_params = new
                {
                    format = "pcm",
                    sample_rate = _options.OutputSampleRate,
                    enable_subtitle = true,
                },
                additions,
            },
        });
        try
        {
            await SendTtsAsync(
                    VolcengineMessageType.FullClientRequest,
                    VolcengineEvents.StartSession,
                    sessionId,
                    payload,
                    VolcengineSerialization.Json,
                    VolcengineCompression.None,
                    cancellationToken)
                .ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(_options.WireOperationTimeoutMilliseconds);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await VolcengineAsync.AwaitWithCancellationAsync(acknowledgement.Task, linked.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_stateGate)
            {
                _ttsHandoffs.Remove(sessionId);
                _ttsStartAcks.Remove(sessionId);
                if (string.Equals(_activeTtsSessionId, sessionId, StringComparison.Ordinal))
                {
                    _activeTtsSessionId = null;
                    _activeHandoffId = null;
                }
            }

            throw;
        }
    }

    private async ValueTask CancelActiveTtsAsync(CancellationToken cancellationToken)
    {
        string? sessionId;
        string? responseId;
        lock (_stateGate)
        {
            sessionId = _activeTtsSessionId;
            responseId = _activeHandoffId;
            if (sessionId is not null)
            {
                _ignoredTtsSessions.Add(sessionId);
                _activeTtsSessionId = null;
                _activeHandoffId = null;
            }
        }

        if (sessionId is null)
        {
            return;
        }

        await SendTtsAsync(
                VolcengineMessageType.FullClientRequest,
                VolcengineEvents.CancelSession,
                sessionId,
                Encoding.UTF8.GetBytes("{}"),
                VolcengineSerialization.Json,
                VolcengineCompression.None,
                cancellationToken)
            .ConfigureAwait(false);
        await _events.EnqueueAsync(
                new RealtimeConversationEvent(
                    RealtimeConversationEventKind.ResponseCancelled,
                    itemId: sessionId,
                    responseId: responseId ?? sessionId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void CompleteStartAck(string sessionId)
    {
        lock (_stateGate)
        {
            if (_ttsStartAcks.TryGetValue(sessionId, out var acknowledgement))
            {
                acknowledgement.TrySetResult(true);
            }
        }
    }

    private bool IsIgnored(string sessionId)
    {
        lock (_stateGate)
        {
            return _ignoredTtsSessions.Contains(sessionId);
        }
    }

    private void CompleteIgnored(string sessionId)
    {
        lock (_stateGate)
        {
            _ignoredTtsSessions.Remove(sessionId);
            _ttsHandoffs.Remove(sessionId);
            _ttsTranscripts.Remove(sessionId);
            if (_ttsStartAcks.Remove(sessionId, out var acknowledgement))
            {
                acknowledgement.TrySetCanceled();
            }
        }
    }

    private async ValueTask SendDialogueAsync(
        VolcengineMessageType type,
        int eventType,
        string? sessionId,
        ReadOnlyMemory<byte> payload,
        VolcengineSerialization serialization,
        VolcengineCompression compression,
        CancellationToken cancellationToken)
    {
        var dialogue = _dialogue ?? throw new NotSupportedException("Dialogue input is disabled.");
        await SendAsync(
                dialogue,
                _dialogueSendGate,
                type,
                eventType,
                sessionId,
                payload,
                serialization,
                compression,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask SendTtsAsync(
        VolcengineMessageType type,
        int eventType,
        string? sessionId,
        ReadOnlyMemory<byte> payload,
        VolcengineSerialization serialization,
        VolcengineCompression compression,
        CancellationToken cancellationToken) =>
        SendAsync(
            _tts,
            _ttsSendGate,
            type,
            eventType,
            sessionId,
            payload,
            serialization,
            compression,
            cancellationToken);

    private async ValueTask SendAsync(
        IVolcengineWebSocketConnection connection,
        SemaphoreSlim gate,
        VolcengineMessageType type,
        int eventType,
        string? sessionId,
        ReadOnlyMemory<byte> payload,
        VolcengineSerialization serialization,
        VolcengineCompression compression,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.WireOperationTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var frame = VolcengineWireProtocol.Encode(
                type,
                eventType,
                sessionId,
                payload,
                serialization,
                compression);
            if (frame.Length > _options.MaximumWireFrameBytes)
            {
                throw new InvalidDataException("The outbound provider frame exceeded its limit.");
            }

            await VolcengineAsync.AwaitWithCancellationAsync(
                    connection.SendBinaryAsync(frame, linked.Token).AsTask(),
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("A Volcengine wire operation timed out.", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<VolcengineWireMessage> ReceiveAsync(
        IVolcengineWebSocketConnection connection,
        CancellationToken cancellationToken)
    {
        var frame = await VolcengineAsync.AwaitWithCancellationAsync(
                connection.ReceiveBinaryAsync(_options.MaximumWireFrameBytes, cancellationToken).AsTask(),
                cancellationToken)
            .ConfigureAwait(false);
        return VolcengineWireProtocol.Decode(frame, _options.MaximumPayloadBytes);
    }

    private async ValueTask EmitProviderErrorAsync(
        VolcengineWireMessage message,
        CancellationToken cancellationToken)
    {
        var providerMessage = ExtractError(message.Payload);
        var error = $"Volcengine realtime error {message.ErrorCode?.ToString() ?? message.EventType?.ToString() ?? "unknown"}";
        if (providerMessage.Length > 0)
        {
            error += ": " + providerMessage;
        }

        await _events.EnqueueAsync(
                new RealtimeConversationEvent(
                    RealtimeConversationEventKind.Error,
                    error: _redactor.Sanitize(error)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask EmitErrorAsync(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return default;
        }

        return _events.EnqueueAsync(
            new RealtimeConversationEvent(
                RealtimeConversationEventKind.Error,
                error: _redactor.Sanitize("Volcengine realtime transport failed: " + exception.Message)),
            cancellationToken);
    }

    private string ExtractText(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return string.Empty;
        }

        using var document = ParseJson(payload);
        return ExtractText(document.RootElement);
    }

    private string ExtractText(JsonElement root)
    {
        var value = FindText(root, 0, new[] { "text", "transcript", "sentence", "utterance" });
        if (value is null)
        {
            return string.Empty;
        }

        return value.Length <= _options.MaximumTextCharacters
            ? value
            : throw new InvalidDataException("A provider transcript exceeded its configured limit.");
    }

    private string ExtractError(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            using var document = ParseJson(payload);
            return FindText(document.RootElement, 0, new[] { "message", "error_message", "error" }) ?? string.Empty;
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }
    }

    private JsonDocument ParseJson(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > _options.MaximumPayloadBytes)
        {
            throw new InvalidDataException("The provider JSON payload exceeded its limit.");
        }

        try
        {
            return JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                MaxDepth = 64,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The provider returned invalid JSON.", exception);
        }
    }

    private static string? FindText(JsonElement value, int depth, IReadOnlyList<string> names)
    {
        if (depth > 8)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (value.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
                {
                    return direct.GetString();
                }
            }

            foreach (var property in value.EnumerateObject().Take(256))
            {
                var nested = FindText(property.Value, depth + 1, names);
                if (!string.IsNullOrEmpty(nested))
                {
                    return nested;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray().Take(256))
            {
                var nested = FindText(item, depth + 1, names);
                if (!string.IsNullOrEmpty(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool TryGetArray(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject().Take(256))
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && TryGetArray(property.Value, name, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new InvalidDataException($"Provider field '{name}' must be a string.");
    }

    private static RealtimeTranscriptTiming? ParseTiming(JsonElement element, bool wordLevel)
    {
        var start = OptionalDouble(element, "startTime") ?? OptionalDouble(element, "start_time");
        var end = OptionalDouble(element, "endTime") ?? OptionalDouble(element, "end_time");
        if (start is null || end is null)
        {
            return null;
        }

        if (!double.IsFinite(start.Value)
            || !double.IsFinite(end.Value)
            || start.Value < 0
            || end.Value < start.Value
            || end.Value > int.MaxValue / 1000d)
        {
            throw new InvalidDataException("Provider subtitle timing is invalid.");
        }

        var confidence = OptionalDouble(element, "confidence");
        return new RealtimeTranscriptTiming(
            checked((int)Math.Round(start.Value * 1000d)),
            checked((int)Math.Round(end.Value * 1000d)),
            confidence,
            wordLevel);
    }

    private static double? OptionalDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed)
            ? parsed
            : throw new InvalidDataException($"Provider field '{name}' must be a number.");
    }

    private string RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > _options.MaximumTextCharacters
            || value.Any(character => character == '\0'))
        {
            throw new ArgumentException("A bounded text value is required.", name);
        }

        return value;
    }

    private static void RequireId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded identifier is required.", name);
        }
    }

    private async ValueTask CloseSocketAsync(
        IVolcengineWebSocketConnection? connection,
        CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            await VolcengineAsync.AwaitWithCancellationAsync(
                    connection.CloseAsync("closing", cancellationToken).AsTask(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async ValueTask AwaitPumpsAsync()
    {
        var tasks = _dialoguePump is null ? new[] { _ttsPump } : new[] { _dialoguePump, _ttsPump };
        try
        {
            using var timeout = new CancellationTokenSource(_options.ShutdownTimeoutMilliseconds);
            await VolcengineAsync.AwaitWithCancellationAsync(Task.WhenAll(tasks), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            throw new ObjectDisposedException(nameof(VolcengineRealtimeTransportSession));
        }
    }

    private void CompleteEvents()
    {
        if (Interlocked.Exchange(ref _eventsCompleted, 1) != 0)
        {
            return;
        }

        _events.TryEnqueue(new RealtimeConversationEvent(RealtimeConversationEventKind.Closed));
        _events.Complete();
    }
}

internal sealed class VolcengineSecretRedactor
{
    private readonly string[] _secrets;

    internal VolcengineSecretRedactor(IEnumerable<string> secrets)
    {
        _secrets = secrets
            .Where(static value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
            .SelectMany(static value => new[]
            {
                value,
                Uri.EscapeDataString(value),
                JsonEncodedText.Encode(value).ToString(),
            })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static value => value.Length)
            .ToArray();
    }

    internal string Sanitize(string value)
    {
        var bounded = value.Length <= 4096 ? value : value.Substring(0, 4096);
        foreach (var secret in _secrets)
        {
            bounded = bounded.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        var builder = new StringBuilder(bounded.Length);
        foreach (var character in bounded)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }
}

internal static class VolcengineAsync
{
    internal static async Task<T> AwaitWithCancellationAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken)
    {
        await AwaitWithCancellationAsync((Task)task, cancellationToken).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    internal static async Task AwaitWithCancellationAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancelled);
        if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
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

internal sealed class VolcengineBoundedQueue<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly Queue<T> _queue = new();
    private readonly SemaphoreSlim _items = new(0);
    private readonly SemaphoreSlim _slots;
    private bool _completed;

    internal VolcengineBoundedQueue(int capacity)
    {
        _slots = new SemaphoreSlim(capacity, capacity);
    }

    internal bool TryEnqueue(T value)
    {
        if (!_slots.Wait(0))
        {
            return false;
        }

        lock (_gate)
        {
            if (_completed)
            {
                _slots.Release();
                return false;
            }

            _queue.Enqueue(value);
        }

        _items.Release();
        return true;
    }

    internal async ValueTask EnqueueAsync(T value, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_completed)
            {
                _slots.Release();
                throw new InvalidOperationException("The provider event queue is closed.");
            }

            _queue.Enqueue(value);
        }

        _items.Release();
    }

    internal async ValueTask<T?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _items.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_queue.Count > 0)
                {
                    var value = _queue.Dequeue();
                    _slots.Release();
                    return value;
                }

                if (_completed)
                {
                    return null;
                }
            }
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        _items.Release();
    }
}

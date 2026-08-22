using System.Runtime.CompilerServices;

namespace OpenGameAgent.Realtime;

public readonly struct GameVoiceActivityDecision
{
    public GameVoiceActivityDecision(bool containsSpeech, bool speechStarted, bool speechStopped)
    {
        if (speechStarted && speechStopped)
        {
            throw new ArgumentException("A voice-activity decision cannot start and stop one utterance at once.");
        }

        ContainsSpeech = containsSpeech;
        SpeechStarted = speechStarted;
        SpeechStopped = speechStopped;
    }

    public bool ContainsSpeech { get; }

    public bool SpeechStarted { get; }

    public bool SpeechStopped { get; }
}

/// <summary>
/// Detects speech boundaries for one conversation. Implementations are session-scoped and may keep state.
/// </summary>
public interface IGameVoiceActivityDetector : IAsyncDisposable
{
    ValueTask<GameVoiceActivityDecision> AnalyzeAsync(
        RealtimeAudioFrame frame,
        CancellationToken cancellationToken);

    void Reset();
}

public sealed class EnergyGameVoiceActivityDetectorOptions
{
    public double RootMeanSquareThreshold { get; set; } = 0.02;

    public int MinimumSpeechMilliseconds { get; set; } = 40;

    public int StopAfterSilenceMilliseconds { get; set; } = 320;
}

/// <summary>
/// A deterministic PCM16 energy gate intended as a bounded default and testable fallback.
/// Hosts can replace it with an ONNX or platform VAD through <see cref="IGameVoiceActivityDetector"/>.
/// </summary>
public sealed class EnergyGameVoiceActivityDetector : IGameVoiceActivityDetector
{
    private readonly double _threshold;
    private readonly int _minimumSpeechMilliseconds;
    private readonly int _stopAfterSilenceMilliseconds;
    private int _candidateSpeechMilliseconds;
    private int _silenceMilliseconds;
    private bool _active;

    public EnergyGameVoiceActivityDetector(EnergyGameVoiceActivityDetectorOptions? options = null)
    {
        var settings = options ?? new EnergyGameVoiceActivityDetectorOptions();
        if (!double.IsFinite(settings.RootMeanSquareThreshold)
            || settings.RootMeanSquareThreshold is <= 0 or > 1
            || settings.MinimumSpeechMilliseconds is < 0 or > 5_000
            || settings.StopAfterSilenceMilliseconds is < 10 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _threshold = settings.RootMeanSquareThreshold;
        _minimumSpeechMilliseconds = settings.MinimumSpeechMilliseconds;
        _stopAfterSilenceMilliseconds = settings.StopAfterSilenceMilliseconds;
    }

    public ValueTask<GameVoiceActivityDecision> AnalyzeAsync(
        RealtimeAudioFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame is null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var containsSpeech = RootMeanSquare(frame.Pcm16.Span) >= _threshold;
        var duration = Math.Max(1, frame.DurationMilliseconds);
        var started = false;
        var stopped = false;
        if (_active)
        {
            _silenceMilliseconds = containsSpeech
                ? 0
                : checked(_silenceMilliseconds + duration);
            if (_silenceMilliseconds >= _stopAfterSilenceMilliseconds)
            {
                _active = false;
                _silenceMilliseconds = 0;
                _candidateSpeechMilliseconds = 0;
                stopped = true;
            }
        }
        else if (containsSpeech)
        {
            _candidateSpeechMilliseconds = checked(_candidateSpeechMilliseconds + duration);
            if (_candidateSpeechMilliseconds >= _minimumSpeechMilliseconds)
            {
                _active = true;
                _candidateSpeechMilliseconds = 0;
                started = true;
            }
        }
        else
        {
            _candidateSpeechMilliseconds = 0;
        }

        return new ValueTask<GameVoiceActivityDecision>(
            new GameVoiceActivityDecision(containsSpeech, started, stopped));
    }

    public void Reset()
    {
        _candidateSpeechMilliseconds = 0;
        _silenceMilliseconds = 0;
        _active = false;
    }

    public ValueTask DisposeAsync() => default;

    private static double RootMeanSquare(ReadOnlySpan<byte> bytes)
    {
        double sum = 0;
        var samples = bytes.Length / 2;
        for (var offset = 0; offset < bytes.Length; offset += 2)
        {
            var sample = (short)(bytes[offset] | bytes[offset + 1] << 8);
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / samples);
    }
}

public sealed class GameSpeechRecognitionRequest
{
    public GameSpeechRecognitionRequest(
        string utteranceId,
        ReadOnlyMemory<byte> pcm16,
        int sampleRate,
        int channels,
        string? language = null)
    {
        UtteranceId = RequireId(utteranceId, nameof(utteranceId));
        if (pcm16.IsEmpty || pcm16.Length % 2 != 0)
        {
            throw new ArgumentException("A non-empty PCM16 utterance is required.", nameof(pcm16));
        }

        if (sampleRate is < 8_000 or > 192_000 || channels is < 1 or > 8 || pcm16.Length % (2 * channels) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (language is { Length: > 64 } || language?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("The recognition language is invalid.", nameof(language));
        }

        Pcm16 = pcm16.ToArray();
        SampleRate = sampleRate;
        Channels = channels;
        Language = language;
    }

    public string UtteranceId { get; }

    public ReadOnlyMemory<byte> Pcm16 { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public string? Language { get; }

    public int DurationMilliseconds => checked((int)((long)Pcm16.Length * 1_000L / (2L * Channels * SampleRate)));

    private static string RequireId(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded identifier is required.", name)
            : value;
}

public sealed class GameSpeechRecognitionResult
{
    public GameSpeechRecognitionResult(string text, double? confidence = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4_000_000)
        {
            throw new ArgumentException("A bounded transcript is required.", nameof(text));
        }

        if (confidence is { } value && (!double.IsFinite(value) || value is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        Text = text;
        Confidence = confidence;
    }

    public string Text { get; }

    public double? Confidence { get; }
}

/// <summary>Transcribes bounded PCM16 utterances. Implementations must be safe for concurrent conversations.</summary>
public interface IGameSpeechRecognizer
{
    ValueTask<GameSpeechRecognitionResult> TranscribeAsync(
        GameSpeechRecognitionRequest request,
        CancellationToken cancellationToken);
}

public sealed class GameSpeechSynthesisRequest
{
    public GameSpeechSynthesisRequest(
        string responseId,
        string itemId,
        string text,
        string voice)
    {
        ResponseId = Require(responseId, 256, nameof(responseId));
        ItemId = Require(itemId, 256, nameof(itemId));
        Text = Require(text, 4_000_000, nameof(text));
        Voice = Require(voice, 256, nameof(voice));
    }

    public string ResponseId { get; }

    public string ItemId { get; }

    public string Text { get; }

    public string Voice { get; }

    private static string Require(string value, int maximum, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded value is required.", name)
            : value;
}

/// <summary>Streams PCM16 speech for bounded text segments. Implementations must honor cancellation.</summary>
public interface IGameSpeechSynthesizer
{
    IAsyncEnumerable<RealtimeAudioFrame> SynthesizeAsync(
        GameSpeechSynthesisRequest request,
        CancellationToken cancellationToken);
}

public sealed class ComposableRealtimeTransportOptions
{
    public Func<IGameVoiceActivityDetector> VoiceActivityDetectorFactory { get; set; } =
        static () => new EnergyGameVoiceActivityDetector();

    public string? RecognitionLanguage { get; set; }

    public int PreRollMilliseconds { get; set; } = 240;

    public int MaximumUtteranceMilliseconds { get; set; } = 120_000;

    public int MaximumUtteranceBytes { get; set; } = 32_000_000;

    public int UtteranceQueueCapacity { get; set; } = 8;

    public int SynthesisQueueCapacity { get; set; } = 64;

    public int EventQueueCapacity { get; set; } = 512;

    public int MaximumTranscriptCharacters { get; set; } = 1_000_000;

    public int MaximumSynthesisSegmentCharacters { get; set; } = 65_536;

    public int ProviderOperationTimeoutMilliseconds { get; set; } = 120_000;

    internal ComposableRealtimeTransportOptions Snapshot()
    {
        if (VoiceActivityDetectorFactory is null
            || RecognitionLanguage is { Length: > 64 }
            || RecognitionLanguage?.Any(char.IsControl) == true
            || PreRollMilliseconds is < 0 or > 5_000
            || MaximumUtteranceMilliseconds is < 100 or > 600_000
            || MaximumUtteranceBytes is < 2 or > 256_000_000
            || UtteranceQueueCapacity is < 1 or > 1_024
            || SynthesisQueueCapacity is < 1 or > 4_096
            || EventQueueCapacity is < 8 or > 16_384
            || MaximumTranscriptCharacters is < 1 or > 4_000_000
            || MaximumSynthesisSegmentCharacters is < 1 or > 1_000_000
            || ProviderOperationTimeoutMilliseconds is < 100 or > 600_000)
        {
            throw new ArgumentOutOfRangeException(nameof(ComposableRealtimeTransportOptions));
        }

        return (ComposableRealtimeTransportOptions)MemberwiseClone();
    }
}

/// <summary>
/// Composes local or platform VAD, speech recognition, the existing realtime handoff bridge, and
/// streaming speech synthesis behind the normal realtime transport contract. It never runs a
/// second model/tool loop and never mutates authoritative game state itself.
/// </summary>
public sealed class ComposableRealtimeTransport : IRealtimeTransport, IRealtimeTransportCapabilities
{
    private readonly IGameSpeechRecognizer _recognizer;
    private readonly IGameSpeechSynthesizer _synthesizer;
    private readonly ComposableRealtimeTransportOptions _options;

    public ComposableRealtimeTransport(
        IGameSpeechRecognizer recognizer,
        IGameSpeechSynthesizer synthesizer,
        ComposableRealtimeTransportOptions? options = null)
    {
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _options = (options ?? new ComposableRealtimeTransportOptions()).Snapshot();
    }

    public RealtimeTransportFeatures Features =>
        RealtimeTransportFeatures.AudioInput
        | RealtimeTransportFeatures.InputTranscription
        | RealtimeTransportFeatures.AudioOutput
        | RealtimeTransportFeatures.OutputTranscription
        | RealtimeTransportFeatures.SpeechBoundaries
        | RealtimeTransportFeatures.ResponseCancellation
        | RealtimeTransportFeatures.AudioTruncation
        | RealtimeTransportFeatures.Handoff;

    public ValueTask<IRealtimeTransportSession> ConnectAsync(
        RealtimeConversationOptions options,
        CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var detector = _options.VoiceActivityDetectorFactory()
            ?? throw new InvalidOperationException("The voice-activity detector factory returned null.");
        return new ValueTask<IRealtimeTransportSession>(new Session(
            detector,
            _recognizer,
            _synthesizer,
            _options,
            options));
    }

    private sealed class Session : IRealtimeTransportSession
    {
        private readonly object _audioGate = new();
        private readonly object _responseGate = new();
        private readonly object _closeGate = new();
        private readonly IGameVoiceActivityDetector _detector;
        private readonly IGameSpeechRecognizer _recognizer;
        private readonly IGameSpeechSynthesizer _synthesizer;
        private readonly ComposableRealtimeTransportOptions _settings;
        private readonly RealtimeConversationOptions _conversation;
        private readonly BoundedAsyncQueue<Utterance> _utterances;
        private readonly BoundedAsyncQueue<SynthesisSegment> _segments;
        private readonly BoundedAsyncQueue<RealtimeConversationEvent> _events;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Queue<RealtimeAudioFrame> _preRoll = new();
        private readonly List<RealtimeAudioFrame> _speech = new();
        private readonly Task _recognitionPump;
        private readonly Task _synthesisPump;
        private CancellationTokenSource? _activeSynthesis;
        private string? _activeResponseId;
        private long _preRollSamples;
        private int _speechBytes;
        private int _speechMilliseconds;
        private int _utteranceSequence;
        private int _segmentSequence;
        private int _responseEpoch;
        private int _closed;
        private int _disposed;
        private Task? _closeTask;
        private bool _speechActive;
        private bool _speechOversized;

        public Session(
            IGameVoiceActivityDetector detector,
            IGameSpeechRecognizer recognizer,
            IGameSpeechSynthesizer synthesizer,
            ComposableRealtimeTransportOptions settings,
            RealtimeConversationOptions conversation)
        {
            _detector = detector;
            _recognizer = recognizer;
            _synthesizer = synthesizer;
            _settings = settings;
            _conversation = conversation;
            _utterances = new BoundedAsyncQueue<Utterance>(settings.UtteranceQueueCapacity);
            _segments = new BoundedAsyncQueue<SynthesisSegment>(settings.SynthesisQueueCapacity);
            _events = new BoundedAsyncQueue<RealtimeConversationEvent>(settings.EventQueueCapacity);
            _events.TryEnqueue(new RealtimeConversationEvent(RealtimeConversationEventKind.SessionUpdated));
            _recognitionPump = Task.Run(PumpRecognitionAsync);
            _synthesisPump = Task.Run(PumpSynthesisAsync);
        }

        public async IAsyncEnumerable<RealtimeConversationEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await _events.DequeueAsync(cancellationToken).ConfigureAwait(false) is { } value)
            {
                yield return value;
            }
        }

        public async ValueTask SendAudioAsync(
            RealtimeAudioFrame frame,
            CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (frame is null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            var decision = await _detector.AnalyzeAsync(frame, cancellationToken).ConfigureAwait(false);
            RealtimeConversationEvent? boundary = null;
            Utterance? completed = null;
            var cancelOutput = false;
            lock (_audioGate)
            {
                if (!_speechActive)
                {
                    AddPreRoll(frame);
                    if (decision.SpeechStarted)
                    {
                        _speechActive = true;
                        foreach (var buffered in _preRoll)
                        {
                            AddSpeech(buffered);
                        }

                        ClearPreRoll();
                        boundary = new RealtimeConversationEvent(
                            RealtimeConversationEventKind.InputSpeechStarted,
                            itemId: CurrentUtteranceId());
                        cancelOutput = true;
                    }
                }
                else
                {
                    AddSpeech(frame);
                }

                if (_speechActive && decision.SpeechStopped)
                {
                    completed = CompleteUtterance();
                    boundary = new RealtimeConversationEvent(
                        RealtimeConversationEventKind.InputSpeechStopped,
                        itemId: completed?.Id ?? CurrentUtteranceId());
                }
            }

            if (boundary is not null)
            {
                await _events.EnqueueAsync(boundary, cancellationToken).ConfigureAwait(false);
            }

            if (cancelOutput)
            {
                await CancelResponseAsync(cancellationToken).ConfigureAwait(false);
            }

            if (completed is not null)
            {
                await _utterances.EnqueueAsync(completed, cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask SendTextAsync(
            string text,
            RealtimeTextRole role,
            CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            ValidateText(text, _conversation.MaximumTextCharacters);
            return role switch
            {
                RealtimeTextRole.User => EnqueueUserTextAsync(text, cancellationToken),
                RealtimeTextRole.Assistant => EnqueueSynthesisAsync(
                    "assistant-" + Guid.NewGuid().ToString("N"), text, completed: true, cancellationToken),
                _ => throw new NotSupportedException("The composable speech transport does not accept developer text after connection."),
            };
        }

        public ValueTask SendHandoffAsync(
            string handoffId,
            string text,
            RealtimeHandoffPhase phase,
            bool completed,
            CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            return EnqueueSynthesisAsync(handoffId, text, completed, cancellationToken);
        }

        public ValueTask SendBehaviorResultAsync(
            RealtimeBehaviorResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException("The composable speech transport does not advertise provider-owned behavior requests.");
        }

        public ValueTask CancelResponseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? responseId;
            lock (_responseGate)
            {
                checked { _responseEpoch++; }
                responseId = _activeResponseId;
                _activeResponseId = null;
                _activeSynthesis?.Cancel();
            }

            return responseId is null
                ? default
                : _events.EnqueueAsync(
                    new RealtimeConversationEvent(
                        RealtimeConversationEventKind.ResponseCancelled,
                        responseId: responseId),
                    cancellationToken);
        }

        public ValueTask TruncateAudioAsync(
            string itemId,
            int audioEndMilliseconds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(itemId) || itemId.Length > 256 || audioEndMilliseconds < 0)
            {
                throw new ArgumentException("The audio truncation coordinate is invalid.", nameof(itemId));
            }

            return default;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            Task task;
            lock (_closeGate)
            {
                task = _closeTask ??= CloseCoreAsync();
            }

            return new ValueTask(WaitWithCancellationAsync(task, cancellationToken));
        }

        private async Task CloseCoreAsync()
        {
            Interlocked.Exchange(ref _closed, 1);
            Utterance? tail;
            lock (_audioGate)
            {
                tail = _speechActive ? CompleteUtterance() : null;
                ClearPreRoll();
            }

            if (tail is not null)
            {
                await _utterances.EnqueueAsync(tail, _lifetime.Token).ConfigureAwait(false);
            }

            _utterances.Complete();
            _segments.Complete();
            try
            {
                await WaitWithCancellationAsync(
                        Task.WhenAll(_recognitionPump, _synthesisPump),
                        _lifetime.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _lifetime.Cancel();
                await _detector.DisposeAsync().ConfigureAwait(false);
                _events.TryEnqueue(new RealtimeConversationEvent(RealtimeConversationEventKind.Closed));
                _events.Complete();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_responseGate)
            {
                _activeSynthesis?.Dispose();
                _activeSynthesis = null;
            }

            _lifetime.Dispose();
        }

        private async Task PumpRecognitionAsync()
        {
            try
            {
                while (await _utterances.DequeueAsync(_lifetime.Token).ConfigureAwait(false) is { } utterance)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(_settings.ProviderOperationTimeoutMilliseconds);
                    try
                    {
                        var result = await _recognizer.TranscribeAsync(
                                new GameSpeechRecognitionRequest(
                                    utterance.Id,
                                    utterance.Pcm16,
                                    utterance.SampleRate,
                                    utterance.Channels,
                                    _settings.RecognitionLanguage),
                                timeout.Token)
                            .ConfigureAwait(false);
                        if (result.Text.Length > _settings.MaximumTranscriptCharacters)
                        {
                            throw new InvalidDataException("The speech transcript exceeded its configured limit.");
                        }

                        var timing = new RealtimeTranscriptTiming(
                            0,
                            utterance.DurationMilliseconds,
                            result.Confidence);
                        await _events.EnqueueAsync(
                                new RealtimeConversationEvent(
                                    RealtimeConversationEventKind.InputTranscriptDelta,
                                    text: result.Text,
                                    itemId: utterance.Id,
                                    timing: timing),
                                _lifetime.Token)
                            .ConfigureAwait(false);
                        await _events.EnqueueAsync(
                                new RealtimeConversationEvent(
                                    RealtimeConversationEventKind.InputTranscriptDone,
                                    text: result.Text,
                                    itemId: utterance.Id,
                                    timing: timing),
                                _lifetime.Token)
                            .ConfigureAwait(false);
                        await _events.EnqueueAsync(
                                new RealtimeConversationEvent(
                                    RealtimeConversationEventKind.HandoffRequested,
                                    handoff: new RealtimeHandoffRequest(utterance.Id, result.Text),
                                    itemId: utterance.Id),
                                _lifetime.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        await EmitProviderErrorAsync(
                            "speech-recognition",
                            new TimeoutException("Speech recognition timed out."))
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await EmitProviderErrorAsync("speech-recognition", exception).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        private async Task PumpSynthesisAsync()
        {
            var transcript = new System.Text.StringBuilder();
            try
            {
                while (await _segments.DequeueAsync(_lifetime.Token).ConfigureAwait(false) is { } segment)
                {
                    if (segment.Epoch != Volatile.Read(ref _responseEpoch))
                    {
                        continue;
                    }

                    var started = false;
                    lock (_responseGate)
                    {
                        if (segment.Epoch == _responseEpoch)
                        {
                            started = !string.Equals(_activeResponseId, segment.ResponseId, StringComparison.Ordinal);
                            if (started)
                            {
                                _activeResponseId = segment.ResponseId;
                                transcript.Clear();
                            }
                        }
                    }

                    if (started)
                    {
                        await _events.EnqueueAsync(
                                new RealtimeConversationEvent(
                                    RealtimeConversationEventKind.ResponseStarted,
                                    responseId: segment.ResponseId),
                                _lifetime.Token)
                            .ConfigureAwait(false);
                    }

                    if (segment.Text.Length > 0)
                    {
                        if (transcript.Length + segment.Text.Length > _settings.MaximumTranscriptCharacters)
                        {
                            await EmitProviderErrorAsync(
                                "speech-synthesis",
                                new InvalidDataException("The output transcript exceeded its configured limit."))
                                .ConfigureAwait(false);
                            await CancelResponseAsync(_lifetime.Token).ConfigureAwait(false);
                            continue;
                        }

                        transcript.Append(segment.Text);
                        await _events.EnqueueAsync(
                                new RealtimeConversationEvent(
                                    RealtimeConversationEventKind.OutputTranscriptDelta,
                                    text: segment.Text,
                                    itemId: segment.ItemId,
                                    responseId: segment.ResponseId),
                                _lifetime.Token)
                            .ConfigureAwait(false);
                        if (_conversation.OutputModality == RealtimeOutputModality.Audio)
                        {
                            await SynthesizeSegmentAsync(segment).ConfigureAwait(false);
                        }
                    }

                    if (segment.Completed && segment.Epoch == Volatile.Read(ref _responseEpoch))
                    {
                        await _events.EnqueueAsync(
                                new RealtimeConversationEvent(
                                    RealtimeConversationEventKind.OutputTranscriptDone,
                                    text: transcript.ToString(),
                                    itemId: segment.ItemId,
                                    responseId: segment.ResponseId),
                                _lifetime.Token)
                            .ConfigureAwait(false);
                        await _events.EnqueueAsync(
                                new RealtimeConversationEvent(
                                    RealtimeConversationEventKind.ResponseDone,
                                    responseId: segment.ResponseId),
                                _lifetime.Token)
                            .ConfigureAwait(false);
                        lock (_responseGate)
                        {
                            if (segment.Epoch == _responseEpoch
                                && string.Equals(_activeResponseId, segment.ResponseId, StringComparison.Ordinal))
                            {
                                _activeResponseId = null;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        private async Task SynthesizeSegmentAsync(SynthesisSegment segment)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(_settings.ProviderOperationTimeoutMilliseconds);
            lock (_responseGate)
            {
                _activeSynthesis?.Dispose();
                _activeSynthesis = timeout;
            }

            try
            {
                var request = new GameSpeechSynthesisRequest(
                    segment.ResponseId,
                    segment.ItemId,
                    segment.Text,
                    _conversation.Voice);
                await foreach (var frame in _synthesizer.SynthesizeAsync(request, timeout.Token)
                                   .WithCancellation(timeout.Token)
                                   .ConfigureAwait(false))
                {
                    if (segment.Epoch != Volatile.Read(ref _responseEpoch))
                    {
                        break;
                    }

                    if (frame.Pcm16.Length > _conversation.MaximumAudioFrameBytes)
                    {
                        throw new InvalidDataException("A synthesized audio frame exceeded the conversation limit.");
                    }

                    var normalized = new RealtimeAudioFrame(
                        frame.Pcm16.ToArray(),
                        frame.SampleRate,
                        frame.Channels,
                        segment.ItemId);
                    await _events.EnqueueAsync(
                            new RealtimeConversationEvent(
                                RealtimeConversationEventKind.AudioOutput,
                                audio: normalized,
                                itemId: segment.ItemId,
                                responseId: segment.ResponseId),
                            timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (
                _lifetime.IsCancellationRequested || segment.Epoch != Volatile.Read(ref _responseEpoch))
            {
            }
            catch (OperationCanceledException)
            {
                await EmitProviderErrorAsync(
                    "speech-synthesis",
                    new TimeoutException("Speech synthesis timed out."))
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await EmitProviderErrorAsync("speech-synthesis", exception).ConfigureAwait(false);
            }
            finally
            {
                lock (_responseGate)
                {
                    if (ReferenceEquals(_activeSynthesis, timeout))
                    {
                        _activeSynthesis = null;
                    }
                }
            }
        }

        private async ValueTask EnqueueUserTextAsync(string text, CancellationToken cancellationToken)
        {
            var id = "local-text-" + Guid.NewGuid().ToString("N");
            await _events.EnqueueAsync(
                    new RealtimeConversationEvent(
                        RealtimeConversationEventKind.InputTranscriptDone,
                        text: text,
                        itemId: id),
                    cancellationToken)
                .ConfigureAwait(false);
            await _events.EnqueueAsync(
                    new RealtimeConversationEvent(
                        RealtimeConversationEventKind.HandoffRequested,
                        handoff: new RealtimeHandoffRequest(id, text),
                        itemId: id),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private ValueTask EnqueueSynthesisAsync(
            string responseId,
            string text,
            bool completed,
            CancellationToken cancellationToken)
        {
            ValidateId(responseId, nameof(responseId));
            if (text is null || text.Length > _settings.MaximumSynthesisSegmentCharacters)
            {
                throw new ArgumentException("The synthesis segment exceeded its configured limit.", nameof(text));
            }

            var sequence = Interlocked.Increment(ref _segmentSequence);
            var itemId = responseId.Length <= 220
                ? responseId + "-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "speech-item-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return _segments.EnqueueAsync(
                new SynthesisSegment(
                    responseId,
                    itemId,
                    text,
                    completed,
                    Volatile.Read(ref _responseEpoch)),
                cancellationToken);
        }

        private void AddPreRoll(RealtimeAudioFrame frame)
        {
            _preRoll.Enqueue(frame);
            _preRollSamples += frame.SamplesPerChannel;
            while (_preRoll.Count > 0)
            {
                var first = _preRoll.Peek();
                var milliseconds = _preRollSamples * 1_000L / first.SampleRate;
                if (milliseconds <= _settings.PreRollMilliseconds)
                {
                    break;
                }

                _preRollSamples -= _preRoll.Dequeue().SamplesPerChannel;
            }
        }

        private void ClearPreRoll()
        {
            _preRoll.Clear();
            _preRollSamples = 0;
        }

        private void AddSpeech(RealtimeAudioFrame frame)
        {
            if (_speech.Count > 0
                && (_speech[0].SampleRate != frame.SampleRate || _speech[0].Channels != frame.Channels))
            {
                _speechOversized = true;
                return;
            }

            _speechBytes = checked(_speechBytes + frame.Pcm16.Length);
            _speechMilliseconds = checked(_speechMilliseconds + Math.Max(1, frame.DurationMilliseconds));
            if (_speechBytes > _settings.MaximumUtteranceBytes
                || _speechMilliseconds > _settings.MaximumUtteranceMilliseconds)
            {
                _speechOversized = true;
                return;
            }

            _speech.Add(frame);
        }

        private Utterance? CompleteUtterance()
        {
            _speechActive = false;
            _detector.Reset();
            var id = CurrentUtteranceId();
            Interlocked.Increment(ref _utteranceSequence);
            Utterance? result = null;
            if (!_speechOversized && _speech.Count > 0)
            {
                var bytes = new byte[_speech.Sum(frame => frame.Pcm16.Length)];
                var offset = 0;
                foreach (var frame in _speech)
                {
                    frame.Pcm16.CopyTo(bytes.AsMemory(offset));
                    offset += frame.Pcm16.Length;
                }

                result = new Utterance(id, bytes, _speech[0].SampleRate, _speech[0].Channels);
            }
            else if (_speechOversized)
            {
                _events.TryEnqueue(new RealtimeConversationEvent(
                    RealtimeConversationEventKind.Error,
                    error: "speech-utterance-limit"));
            }

            _speech.Clear();
            _speechBytes = 0;
            _speechMilliseconds = 0;
            _speechOversized = false;
            return result;
        }

        private string CurrentUtteranceId() =>
            "local-speech-" + (_utteranceSequence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private async ValueTask EmitProviderErrorAsync(string component, Exception exception)
        {
            var category = exception switch
            {
                OperationCanceledException or TimeoutException => "timeout",
                InvalidDataException => "invalid-data",
                HttpRequestException => "transport",
                _ => "provider",
            };
            try
            {
                await _events.EnqueueAsync(
                        new RealtimeConversationEvent(
                            RealtimeConversationEventKind.Error,
                            error: component + ":" + category),
                        _lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        private void ThrowIfClosed()
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                throw new ObjectDisposedException(nameof(ComposableRealtimeTransport));
            }
        }

        private static void ValidateId(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            {
                throw new ArgumentException("A bounded identifier is required.", name);
            }
        }

        private static void ValidateText(string value, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            {
                throw new ArgumentException("Bounded realtime text is required.", nameof(value));
            }
        }

        private static async Task WaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                cancelled);
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await task.ConfigureAwait(false);
        }

        private sealed class Utterance
        {
            public Utterance(string id, byte[] pcm16, int sampleRate, int channels)
            {
                Id = id;
                Pcm16 = pcm16;
                SampleRate = sampleRate;
                Channels = channels;
            }

            public string Id { get; }
            public byte[] Pcm16 { get; }
            public int SampleRate { get; }
            public int Channels { get; }
            public int DurationMilliseconds => checked((int)((long)Pcm16.Length * 1_000L / (2L * Channels * SampleRate)));
        }

        private sealed class SynthesisSegment
        {
            public SynthesisSegment(
                string responseId,
                string itemId,
                string text,
                bool completed,
                int epoch)
            {
                ResponseId = responseId;
                ItemId = itemId;
                Text = text;
                Completed = completed;
                Epoch = epoch;
            }

            public string ResponseId { get; }
            public string ItemId { get; }
            public string Text { get; }
            public bool Completed { get; }
            public int Epoch { get; }
        }
    }
}

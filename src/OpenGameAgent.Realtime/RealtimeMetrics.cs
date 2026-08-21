using System.Collections.ObjectModel;

namespace OpenGameAgent.Realtime;

public enum RealtimeLatencyKind
{
    FirstInputTranscript,
    FinalInputTranscript,
    FirstOutputAudio,
    CompleteOutputAudio,
    BargeInCancellation,
}

public sealed class RealtimeLatencySample
{
    internal RealtimeLatencySample(
        RealtimeLatencyKind kind,
        double durationMilliseconds,
        string? itemId,
        string? responseId)
    {
        Kind = kind;
        DurationMilliseconds = durationMilliseconds;
        ItemId = itemId;
        ResponseId = responseId;
    }

    public RealtimeLatencyKind Kind { get; }
    public double DurationMilliseconds { get; }
    public string? ItemId { get; }
    public string? ResponseId { get; }
}

/// <summary>
/// Optional, bounded observer for provider-neutral STT, TTS, and barge-in latency. Register
/// <see cref="HandleAsync"/> with <see cref="RealtimeConversationManager.RegisterHandler"/> and call
/// <see cref="MarkBargeInRequested"/> immediately before requesting response cancellation.
/// </summary>
public sealed class RealtimeMetricsCollector
{
    private readonly object _gate = new();
    private readonly Queue<RealtimeLatencySample> _samples;
    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset? _speechStartedAt;
    private bool _firstInputTranscriptSeen;
    private readonly Dictionary<string, DateTimeOffset> _responses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _audioStarted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _bargeIns = new(StringComparer.Ordinal);

    public RealtimeMetricsCollector(int capacity = 1_024, Func<DateTimeOffset>? clock = null)
    {
        if (capacity is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _samples = new Queue<RealtimeLatencySample>(Math.Min(capacity, 1_024));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void MarkBargeInRequested(string responseId)
    {
        responseId = RequireId(responseId, nameof(responseId));
        lock (_gate)
        {
            MakeRoom(_bargeIns, responseId);
            _bargeIns[responseId] = _clock();
        }
    }

    public ValueTask HandleAsync(RealtimeConversationEvent value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        var now = _clock();
        lock (_gate)
        {
            switch (value.Kind)
            {
                case RealtimeConversationEventKind.InputSpeechStarted:
                    _speechStartedAt = now;
                    _firstInputTranscriptSeen = false;
                    break;
                case RealtimeConversationEventKind.InputTranscriptDelta when !_firstInputTranscriptSeen && _speechStartedAt is { } speech:
                    _firstInputTranscriptSeen = true;
                    Add(RealtimeLatencyKind.FirstInputTranscript, speech, now, value.ItemId, value.ResponseId);
                    break;
                case RealtimeConversationEventKind.InputTranscriptDone when _speechStartedAt is { } speech:
                    Add(RealtimeLatencyKind.FinalInputTranscript, speech, now, value.ItemId, value.ResponseId);
                    _speechStartedAt = null;
                    break;
                case RealtimeConversationEventKind.ResponseStarted when value.ResponseId is { } responseId:
                    MakeRoomForResponse(responseId);
                    _responses[responseId] = now;
                    _audioStarted.Remove(responseId);
                    break;
                case RealtimeConversationEventKind.AudioOutput when value.ResponseId is { } responseId:
                    if (_responses.TryGetValue(responseId, out var audioResponseStarted) && _audioStarted.Add(responseId))
                    {
                        Add(RealtimeLatencyKind.FirstOutputAudio, audioResponseStarted, now, value.ItemId, responseId);
                    }

                    break;
                case RealtimeConversationEventKind.ResponseDone when value.ResponseId is { } responseId:
                    if (_responses.Remove(responseId, out var completedResponseStarted)
                        && _audioStarted.Contains(responseId))
                    {
                        Add(RealtimeLatencyKind.CompleteOutputAudio, completedResponseStarted, now, value.ItemId, responseId);
                    }

                    _audioStarted.Remove(responseId);
                    _bargeIns.Remove(responseId);
                    break;
                case RealtimeConversationEventKind.ResponseCancelled when value.ResponseId is { } responseId:
                    if (_bargeIns.Remove(responseId, out var bargeInStarted))
                    {
                        Add(RealtimeLatencyKind.BargeInCancellation, bargeInStarted, now, value.ItemId, responseId);
                    }

                    _responses.Remove(responseId);
                    _audioStarted.Remove(responseId);
                    break;
            }
        }

        return default;
    }

    public IReadOnlyList<RealtimeLatencySample> Snapshot()
    {
        lock (_gate)
        {
            return new ReadOnlyCollection<RealtimeLatencySample>(_samples.ToArray());
        }
    }

    private void Add(
        RealtimeLatencyKind kind,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? itemId,
        string? responseId)
    {
        while (_samples.Count >= _capacity)
        {
            _samples.Dequeue();
        }

        _samples.Enqueue(new RealtimeLatencySample(
            kind,
            Math.Max(0, (endedAt - startedAt).TotalMilliseconds),
            itemId,
            responseId));
    }

    private void MakeRoomForResponse(string responseId)
    {
        var removed = MakeRoom(_responses, responseId);
        if (removed is not null)
        {
            _audioStarted.Remove(removed);
            _bargeIns.Remove(removed);
        }
    }

    private string? MakeRoom(Dictionary<string, DateTimeOffset> values, string key)
    {
        if (values.ContainsKey(key) || values.Count < _capacity)
        {
            return null;
        }

        string? oldestKey = null;
        var oldest = DateTimeOffset.MaxValue;
        foreach (var candidate in values)
        {
            if (candidate.Value < oldest)
            {
                oldest = candidate.Value;
                oldestKey = candidate.Key;
            }
        }

        if (oldestKey is not null)
        {
            values.Remove(oldestKey);
        }

        return oldestKey;
    }

    private static string RequireId(string value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)
            ? throw new ArgumentException("A bounded identifier is required.", name)
            : value;
}

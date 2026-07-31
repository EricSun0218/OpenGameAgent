using System.Collections.ObjectModel;
using System.Text;

namespace GameAgent.Core;

public sealed class StreamingPresentationOptions
{
    public int TargetChunkUtf8Bytes { get; set; } = 512;

    public int MaximumBufferedUtf8Bytes { get; set; } = 4_096;

    public TimeSpan IdleFlushInterval { get; set; } =
        TimeSpan.FromMilliseconds(80);

    public bool FlushParagraphs { get; set; } = true;

    public int MaxInputDeltaUtf8Bytes { get; set; } = 1_048_576;

    public int MaxFinalTextUtf8Bytes { get; set; } = 4 * 1_048_576;

    public int MaxChunksPerCall { get; set; } = 8_192;

    public int MaxReplayChunks { get; set; } = 512;

    public int MaxReplayUtf8Bytes { get; set; } = 256 * 1_024;

    internal StreamingPresentationOptions Snapshot()
    {
        if (TargetChunkUtf8Bytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TargetChunkUtf8Bytes));
        }

        if (MaximumBufferedUtf8Bytes < TargetChunkUtf8Bytes
            || MaximumBufferedUtf8Bytes > 4 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumBufferedUtf8Bytes));
        }

        if (IdleFlushInterval <= TimeSpan.Zero
            || IdleFlushInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(IdleFlushInterval));
        }

        if (MaxInputDeltaUtf8Bytes < TargetChunkUtf8Bytes
            || MaxInputDeltaUtf8Bytes > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxInputDeltaUtf8Bytes));
        }

        if (MaxFinalTextUtf8Bytes < TargetChunkUtf8Bytes
            || MaxFinalTextUtf8Bytes > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxFinalTextUtf8Bytes));
        }

        if (MaxChunksPerCall is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxChunksPerCall));
        }

        if (MaxReplayChunks is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReplayChunks));
        }

        if (MaxReplayUtf8Bytes < TargetChunkUtf8Bytes
            || MaxReplayUtf8Bytes > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxReplayUtf8Bytes));
        }

        return new StreamingPresentationOptions
        {
            TargetChunkUtf8Bytes = TargetChunkUtf8Bytes,
            MaximumBufferedUtf8Bytes = MaximumBufferedUtf8Bytes,
            IdleFlushInterval = IdleFlushInterval,
            FlushParagraphs = FlushParagraphs,
            MaxInputDeltaUtf8Bytes = MaxInputDeltaUtf8Bytes,
            MaxFinalTextUtf8Bytes = MaxFinalTextUtf8Bytes,
            MaxChunksPerCall = MaxChunksPerCall,
            MaxReplayChunks = MaxReplayChunks,
            MaxReplayUtf8Bytes = MaxReplayUtf8Bytes
        };
    }
}

public sealed class StreamingPresentationChunk
{
    internal StreamingPresentationChunk(
        long sequence,
        string text,
        bool isFinal,
        bool replacesPriorText)
    {
        Sequence = sequence;
        Text = text;
        IsFinal = isFinal;
        ReplacesPriorText = replacesPriorText;
    }

    public long Sequence { get; }

    public string Text { get; }

    public bool IsFinal { get; }

    public bool ReplacesPriorText { get; }
}

public enum StreamingPresentationReplayStatus
{
    Available,
    CursorExpired,
    CursorAhead
}

public sealed class StreamingPresentationReplay
{
    internal StreamingPresentationReplay(
        StreamingPresentationReplayStatus status,
        long requestedSequence,
        long earliestAvailableSequence,
        long continuationSequence,
        long producedSequenceExclusive,
        bool isComplete,
        IReadOnlyList<StreamingPresentationChunk> chunks)
    {
        Status = status;
        RequestedSequence = requestedSequence;
        EarliestAvailableSequence = earliestAvailableSequence;
        ContinuationSequence = continuationSequence;
        ProducedSequenceExclusive = producedSequenceExclusive;
        IsComplete = isComplete;
        Chunks = chunks;
    }

    public StreamingPresentationReplayStatus Status { get; }

    public long RequestedSequence { get; }

    public long EarliestAvailableSequence { get; }

    public long ContinuationSequence { get; }

    public long ProducedSequenceExclusive { get; }

    public bool IsComplete { get; }

    public IReadOnlyList<StreamingPresentationChunk> Chunks { get; }
}

/// <summary>
/// Converts token-sized deltas into frame-friendly text chunks. One instance
/// owns one stream and can be called from network and engine threads.
/// </summary>
public sealed class StreamingTextCoalescer
{
    private readonly object _sync = new();
    private readonly StreamingPresentationOptions _options;
    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _presented = new();
    private readonly LinkedList<StreamingPresentationChunk> _replay = new();
    private int _bufferUtf8Bytes;
    private int _presentedUtf8Bytes;
    private int _replayUtf8Bytes;
    private bool _presentedOverflowed;
    private DateTimeOffset? _lastDeltaAt;
    private char? _pendingHighSurrogate;
    private long _nextSequence;
    private bool _completed;

    public StreamingTextCoalescer(
        StreamingPresentationOptions? options = null)
    {
        _options = (options ?? new StreamingPresentationOptions()).Snapshot();
    }

    public IReadOnlyList<StreamingPresentationChunk> Push(
        string delta,
        DateTimeOffset receivedAt)
    {
        if (delta is null)
        {
            throw new ArgumentNullException(nameof(delta));
        }

        EnsureCallBounds(
            Encoding.UTF8.GetByteCount(delta),
            _options.MaxInputDeltaUtf8Bytes,
            nameof(delta),
            "stream_delta_bytes_exceeded");

        lock (_sync)
        {
            ThrowIfCompleted();
            EnsurePushOutputBounds(
                Encoding.UTF8.GetByteCount(delta),
                receivedAt);
            var output = new List<StreamingPresentationChunk>();
            if (_buffer.Length > 0
                && _lastDeltaAt.HasValue
                && receivedAt - _lastDeltaAt.Value
                >= _options.IdleFlushInterval)
            {
                EmitAll(output, isFinal: false);
            }

            _lastDeltaAt = receivedAt;
            var safeDelta = PrepareDelta(delta);
            if (safeDelta.Length > 0)
            {
                AppendBounded(safeDelta, output);
            }

            if (_options.FlushParagraphs
                && EndsAtParagraphBoundary(_buffer))
            {
                EmitAll(output, isFinal: false);
            }
            else if (_bufferUtf8Bytes >= _options.TargetChunkUtf8Bytes)
            {
                EmitAll(output, isFinal: false);
            }

            return new ReadOnlyCollection<StreamingPresentationChunk>(output);
        }
    }

    public IReadOnlyList<StreamingPresentationChunk> FlushIdle(
        DateTimeOffset now)
    {
        lock (_sync)
        {
            ThrowIfCompleted();
            var output = new List<StreamingPresentationChunk>();
            if (_buffer.Length > 0
                && _lastDeltaAt.HasValue
                && now - _lastDeltaAt.Value
                >= _options.IdleFlushInterval)
            {
                EmitAll(output, isFinal: false);
            }

            return new ReadOnlyCollection<StreamingPresentationChunk>(output);
        }
    }

    public IReadOnlyList<StreamingPresentationChunk> Complete(
        string finalText)
    {
        if (finalText is null)
        {
            throw new ArgumentNullException(nameof(finalText));
        }

        EnsureCallBounds(
            Encoding.UTF8.GetByteCount(finalText),
            _options.MaxFinalTextUtf8Bytes,
            nameof(finalText),
            "stream_final_text_bytes_exceeded");

        lock (_sync)
        {
            ThrowIfCompleted();
            _completed = true;
            _pendingHighSurrogate = null;
            finalText = NormalizeUtf16(finalText);
            var output = new List<StreamingPresentationChunk>();
            if (_presentedOverflowed)
            {
                _buffer.Clear();
                _bufferUtf8Bytes = 0;
                EmitReplacement(finalText, output);
                return new ReadOnlyCollection<StreamingPresentationChunk>(
                    output);
            }

            var observed = _presented.ToString() + _buffer.ToString();
            if (string.Equals(observed, finalText, StringComparison.Ordinal))
            {
                if (_buffer.Length > 0)
                {
                    EmitAll(output, isFinal: true);
                }
                else
                {
                    AddChunk(output, Chunk(string.Empty, isFinal: true));
                }

                return new ReadOnlyCollection<StreamingPresentationChunk>(
                    output);
            }

            if (finalText.StartsWith(observed, StringComparison.Ordinal))
            {
                var suffix = finalText.Substring(observed.Length);
                var suffixBytes = Encoding.UTF8.GetByteCount(suffix);
                if (suffixBytes
                    <= _options.MaximumBufferedUtf8Bytes
                    - _bufferUtf8Bytes)
                {
                    _buffer.Append(suffix);
                    _bufferUtf8Bytes += suffixBytes;
                }
                else
                {
                    AppendBounded(suffix, output);
                }

                if (_buffer.Length > 0)
                {
                    EmitAll(output, isFinal: true);
                }
                else
                {
                    AddChunk(output, Chunk(string.Empty, isFinal: true));
                }

                return new ReadOnlyCollection<StreamingPresentationChunk>(
                    output);
            }

            _buffer.Clear();
            _bufferUtf8Bytes = 0;
            EmitReplacement(finalText, output);
            return new ReadOnlyCollection<StreamingPresentationChunk>(output);
        }
    }

    /// <summary>
    /// Replays retained presentation chunks beginning at the consumer's next
    /// expected sequence. A cursor older than the bounded replay window fails
    /// closed so the caller can replace its view from authoritative state.
    /// </summary>
    public StreamingPresentationReplay ReplayFrom(long nextSequence)
    {
        return ReplayFrom(nextSequence, _options.MaxReplayChunks);
    }

    public StreamingPresentationReplay ReplayFrom(
        long nextSequence,
        int maximumChunks)
    {
        if (nextSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextSequence));
        }

        if (maximumChunks < 1
            || maximumChunks > _options.MaxReplayChunks)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChunks));
        }

        lock (_sync)
        {
            var earliest = _replay.First?.Value.Sequence ?? _nextSequence;
            if (nextSequence < earliest)
            {
                return ReplayResult(
                    StreamingPresentationReplayStatus.CursorExpired,
                    nextSequence,
                    earliest,
                    nextSequence,
                    Array.Empty<StreamingPresentationChunk>());
            }

            if (nextSequence > _nextSequence)
            {
                return ReplayResult(
                    StreamingPresentationReplayStatus.CursorAhead,
                    nextSequence,
                    earliest,
                    nextSequence,
                    Array.Empty<StreamingPresentationChunk>());
            }

            var output = new List<StreamingPresentationChunk>(
                Math.Min(maximumChunks, _replay.Count));
            var node = _replay.First;
            while (node is not null
                   && node.Value.Sequence < nextSequence)
            {
                node = node.Next;
            }

            while (node is not null && output.Count < maximumChunks)
            {
                output.Add(node.Value);
                node = node.Next;
            }

            var continuation = output.Count == 0
                ? nextSequence
                : checked(output[^1].Sequence + 1);
            return ReplayResult(
                StreamingPresentationReplayStatus.Available,
                nextSequence,
                earliest,
                continuation,
                new ReadOnlyCollection<StreamingPresentationChunk>(output));
        }
    }

    private void AppendBounded(
        string value,
        List<StreamingPresentationChunk> output)
    {
        var offset = 0;
        while (offset < value.Length)
        {
            var available =
                _options.TargetChunkUtf8Bytes - _bufferUtf8Bytes;
            if (available <= 0)
            {
                EmitAll(output, isFinal: false);
                continue;
            }

            var length = Utf16LengthWithinUtf8(
                value,
                offset,
                available);
            if (length == 0)
            {
                if (_buffer.Length > 0)
                {
                    EmitAll(output, isFinal: false);
                    continue;
                }

                length = char.IsHighSurrogate(value[offset])
                         && offset + 1 < value.Length
                         && char.IsLowSurrogate(value[offset + 1])
                    ? 2
                    : 1;
            }

            _buffer.Append(value, offset, length);
            _bufferUtf8Bytes += Encoding.UTF8.GetByteCount(
                value,
                offset,
                length);
            offset += length;
            if (_bufferUtf8Bytes >= _options.TargetChunkUtf8Bytes
                && offset < value.Length)
            {
                EmitAll(output, isFinal: false);
            }
        }
    }

    private string PrepareDelta(string delta)
    {
        if (delta.Length == 0)
        {
            return string.Empty;
        }

        var output = new StringBuilder(delta.Length + 1);
        var offset = 0;
        if (_pendingHighSurrogate.HasValue)
        {
            if (char.IsLowSurrogate(delta[0]))
            {
                output.Append(_pendingHighSurrogate.Value);
                output.Append(delta[0]);
                offset = 1;
            }
            else
            {
                output.Append('\uFFFD');
            }

            _pendingHighSurrogate = null;
        }

        while (offset < delta.Length)
        {
            var character = delta[offset];
            if (char.IsHighSurrogate(character))
            {
                if (offset + 1 >= delta.Length)
                {
                    _pendingHighSurrogate = character;
                    break;
                }

                if (char.IsLowSurrogate(delta[offset + 1]))
                {
                    output.Append(character);
                    output.Append(delta[offset + 1]);
                    offset += 2;
                    continue;
                }

                output.Append('\uFFFD');
            }
            else if (char.IsLowSurrogate(character))
            {
                output.Append('\uFFFD');
            }
            else
            {
                output.Append(character);
            }

            offset++;
        }

        return output.ToString();
    }

    private static string NormalizeUtf16(string value)
    {
        var output = new StringBuilder(value.Length);
        var offset = 0;
        while (offset < value.Length)
        {
            var character = value[offset];
            if (char.IsHighSurrogate(character)
                && offset + 1 < value.Length
                && char.IsLowSurrogate(value[offset + 1]))
            {
                output.Append(character);
                output.Append(value[offset + 1]);
                offset += 2;
                continue;
            }

            output.Append(
                char.IsSurrogate(character)
                    ? '\uFFFD'
                    : character);
            offset++;
        }

        return output.ToString();
    }

    private void EmitAll(
        List<StreamingPresentationChunk> output,
        bool isFinal)
    {
        var text = _buffer.ToString();
        _buffer.Clear();
        _bufferUtf8Bytes = 0;
        RetainPresentedEvidence(text);
        AddChunk(output, Chunk(text, isFinal));
    }

    private void EmitReplacement(
        string text,
        List<StreamingPresentationChunk> output)
    {
        if (text.Length == 0)
        {
            AddChunk(
                output,
                new StreamingPresentationChunk(
                    _nextSequence++,
                    string.Empty,
                    isFinal: true,
                    replacesPriorText: true));
            return;
        }

        var offset = 0;
        var first = true;
        while (offset < text.Length)
        {
            var length = Utf16LengthWithinUtf8(
                text,
                offset,
                _options.TargetChunkUtf8Bytes);
            if (length == 0)
            {
                length = char.IsHighSurrogate(text[offset])
                         && offset + 1 < text.Length
                         && char.IsLowSurrogate(text[offset + 1])
                    ? 2
                    : 1;
            }

            var isFinal = offset + length == text.Length;
            AddChunk(
                output,
                new StreamingPresentationChunk(
                    _nextSequence++,
                    text.Substring(offset, length),
                    isFinal,
                    replacesPriorText: first));
            first = false;
            offset += length;
        }
    }

    private void AddChunk(
        List<StreamingPresentationChunk> output,
        StreamingPresentationChunk chunk)
    {
        if (output.Count >= _options.MaxChunksPerCall)
        {
            throw new RuntimeContentLimitException(
                nameof(output),
                "stream_chunks_per_call_exceeded",
                "One presentation call produced too many chunks.");
        }

        output.Add(chunk);
        RetainReplayChunk(chunk);
    }

    private void RetainReplayChunk(StreamingPresentationChunk chunk)
    {
        var chunkBytes = Encoding.UTF8.GetByteCount(chunk.Text);
        _replay.AddLast(chunk);
        _replayUtf8Bytes = checked(_replayUtf8Bytes + chunkBytes);
        while (_replay.Count > _options.MaxReplayChunks
               || _replayUtf8Bytes > _options.MaxReplayUtf8Bytes)
        {
            var first = _replay.First!;
            _replayUtf8Bytes -= Encoding.UTF8.GetByteCount(first.Value.Text);
            _replay.RemoveFirst();
        }
    }

    private StreamingPresentationReplay ReplayResult(
        StreamingPresentationReplayStatus status,
        long requestedSequence,
        long earliestAvailableSequence,
        long continuationSequence,
        IReadOnlyList<StreamingPresentationChunk> chunks)
    {
        return new StreamingPresentationReplay(
            status,
            requestedSequence,
            earliestAvailableSequence,
            continuationSequence,
            _nextSequence,
            _completed,
            chunks);
    }

    private void RetainPresentedEvidence(string text)
    {
        if (_presentedOverflowed)
        {
            return;
        }

        var textBytes = Encoding.UTF8.GetByteCount(text);
        if (textBytes > _options.MaximumBufferedUtf8Bytes
                        - _presentedUtf8Bytes)
        {
            _presentedOverflowed = true;
            _presented.Clear();
            _presentedUtf8Bytes = 0;
            return;
        }

        _presented.Append(text);
        _presentedUtf8Bytes += textBytes;
    }

    private StreamingPresentationChunk Chunk(string text, bool isFinal)
    {
        return new StreamingPresentationChunk(
            _nextSequence++,
            text,
            isFinal,
            replacesPriorText: false);
    }

    private static bool EndsAtParagraphBoundary(StringBuilder buffer)
    {
        if (buffer.Length < 2)
        {
            return false;
        }

        return buffer[buffer.Length - 1] == '\n'
               && (buffer[buffer.Length - 2] == '\n'
                   || buffer.Length >= 4
                   && buffer[buffer.Length - 2] == '\r'
                   && buffer[buffer.Length - 3] == '\n'
                   && buffer[buffer.Length - 4] == '\r');
    }

    private static int Utf16LengthWithinUtf8(
        string value,
        int offset,
        int maximumUtf8Bytes)
    {
        var length = 0;
        var bytes = 0;
        while (offset + length < value.Length)
        {
            var character = value[offset + length];
            int characterLength;
            int characterBytes;
            if (character <= '\u007f')
            {
                characterLength = 1;
                characterBytes = 1;
            }
            else if (character <= '\u07ff')
            {
                characterLength = 1;
                characterBytes = 2;
            }
            else if (char.IsHighSurrogate(character)
                     && offset + length + 1 < value.Length
                     && char.IsLowSurrogate(
                         value[offset + length + 1]))
            {
                characterLength = 2;
                characterBytes = 4;
            }
            else
            {
                characterLength = 1;
                characterBytes = 3;
            }

            if (bytes + characterBytes > maximumUtf8Bytes)
            {
                break;
            }

            bytes += characterBytes;
            length += characterLength;
        }

        return length;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                "The presentation stream is already complete.");
        }
    }

    private void EnsureCallBounds(
        int utf8Bytes,
        int maximumUtf8Bytes,
        string parameterName,
        string byteLimitCode)
    {
        if (utf8Bytes > maximumUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                byteLimitCode,
                "Presentation input exceeds its UTF-8 byte limit.");
        }

        var minimumChunkBytes = Math.Max(1, _options.TargetChunkUtf8Bytes);
        var maximumPossibleChunks =
            checked((utf8Bytes + minimumChunkBytes - 1)
                    / minimumChunkBytes);
        if (maximumPossibleChunks > _options.MaxChunksPerCall)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "stream_chunks_per_call_exceeded",
                "Presentation input would produce too many chunks.");
        }
    }

    private void EnsurePushOutputBounds(
        int deltaUtf8Bytes,
        DateTimeOffset receivedAt)
    {
        var flushesExisting = _buffer.Length > 0
                              && _lastDeltaAt.HasValue
                              && receivedAt - _lastDeltaAt.Value
                              >= _options.IdleFlushInterval;
        var normalizedDeltaUpperBound = (long)deltaUtf8Bytes
                                        + (_pendingHighSurrogate.HasValue
                                            ? 3
                                            : 0);
        var bytesAfterIdle = normalizedDeltaUpperBound
                             + (flushesExisting
                                 ? 0
                                 : _bufferUtf8Bytes);
        var chunks = flushesExisting ? 1L : 0L;
        if (bytesAfterIdle > 0)
        {
            chunks +=
                (bytesAfterIdle + _options.TargetChunkUtf8Bytes - 1)
                / _options.TargetChunkUtf8Bytes;
        }

        if (chunks > _options.MaxChunksPerCall)
        {
            throw new RuntimeContentLimitException(
                nameof(deltaUtf8Bytes),
                "stream_chunks_per_call_exceeded",
                "Presentation input and pending text would produce too many chunks.");
        }
    }
}

public static class AttemptStreamingPresentationChunkKinds
{
    public const string Reset = "reset";

    public const string Delta = "delta";

    public const string Superseded = "superseded";

    public const string Final = "final";
}

/// <summary>
/// Stable identity for one provider stream. A presentation attempt is scoped
/// to one run and turn; provider retries and fallbacks always receive a new
/// provider-attempt and stream-attempt identity.
/// </summary>
public sealed class StreamingPresentationAttemptIdentity :
    IEquatable<StreamingPresentationAttemptIdentity>
{
    public StreamingPresentationAttemptIdentity(
        string runId,
        string turnId,
        string providerId,
        string providerAttemptId,
        string streamAttemptId)
    {
        RunId = RuntimeGuard.RequiredId(runId, nameof(runId));
        TurnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        ProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));
        ProviderAttemptId = RuntimeGuard.RequiredId(
            providerAttemptId,
            nameof(providerAttemptId));
        StreamAttemptId = RuntimeGuard.RequiredId(
            streamAttemptId,
            nameof(streamAttemptId));
    }

    public string RunId { get; }

    public string TurnId { get; }

    public string ProviderId { get; }

    public string ProviderAttemptId { get; }

    public string StreamAttemptId { get; }

    public bool Equals(StreamingPresentationAttemptIdentity? other)
    {
        return other is not null
               && string.Equals(RunId, other.RunId, StringComparison.Ordinal)
               && string.Equals(TurnId, other.TurnId, StringComparison.Ordinal)
               && string.Equals(
                   ProviderId,
                   other.ProviderId,
                   StringComparison.Ordinal)
               && string.Equals(
                   ProviderAttemptId,
                   other.ProviderAttemptId,
                   StringComparison.Ordinal)
               && string.Equals(
                   StreamAttemptId,
                   other.StreamAttemptId,
                   StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as StreamingPresentationAttemptIdentity);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            RunId,
            TurnId,
            ProviderId,
            ProviderAttemptId,
            StreamAttemptId);
    }
}

/// <summary>
/// An engine-facing presentation update. Sequence is monotonic within one
/// run/turn pair and never resets when a provider attempt is replaced.
/// </summary>
public sealed class AttemptStreamingPresentationChunk
{
    internal AttemptStreamingPresentationChunk(
        StreamingPresentationAttemptIdentity identity,
        long sequence,
        string kind,
        string text,
        bool isFinal,
        bool replacesPriorText,
        string? supersededStreamAttemptId,
        string? reasonCode)
    {
        Identity = identity;
        Sequence = sequence;
        Kind = kind;
        Text = text;
        IsFinal = isFinal;
        ReplacesPriorText = replacesPriorText;
        SupersededStreamAttemptId = supersededStreamAttemptId;
        ReasonCode = reasonCode;
    }

    public StreamingPresentationAttemptIdentity Identity { get; }

    public long Sequence { get; }

    public string Kind { get; }

    public string Text { get; }

    public bool IsFinal { get; }

    public bool ReplacesPriorText { get; }

    public string? SupersededStreamAttemptId { get; }

    public string? ReasonCode { get; }
}

public sealed class AttemptStreamingPresentationReplay
{
    internal AttemptStreamingPresentationReplay(
        StreamingPresentationReplayStatus status,
        string runId,
        string turnId,
        long requestedSequence,
        long earliestAvailableSequence,
        long continuationSequence,
        long producedSequenceExclusive,
        bool isComplete,
        IReadOnlyList<AttemptStreamingPresentationChunk> chunks)
    {
        Status = status;
        RunId = runId;
        TurnId = turnId;
        RequestedSequence = requestedSequence;
        EarliestAvailableSequence = earliestAvailableSequence;
        ContinuationSequence = continuationSequence;
        ProducedSequenceExclusive = producedSequenceExclusive;
        IsComplete = isComplete;
        Chunks = chunks;
    }

    public StreamingPresentationReplayStatus Status { get; }

    public string RunId { get; }

    public string TurnId { get; }

    public long RequestedSequence { get; }

    public long EarliestAvailableSequence { get; }

    public long ContinuationSequence { get; }

    public long ProducedSequenceExclusive { get; }

    public bool IsComplete { get; }

    public IReadOnlyList<AttemptStreamingPresentationChunk> Chunks { get; }
}

public sealed class AttemptSafeStreamingPresentationOptions
{
    public StreamingPresentationOptions Stream { get; set; } = new();

    public int MaxTrackedTurns { get; set; } = 256;

    public int MaxReplayChunksPerTurn { get; set; } = 512;

    public int MaxReplayUtf8BytesPerTurn { get; set; } = 256 * 1_024;

    public int MaxRetiredAttemptsPerTurn { get; set; } = 256;

    internal AttemptSafeStreamingPresentationOptions Snapshot()
    {
        if (Stream is null)
        {
            throw new ArgumentNullException(nameof(Stream));
        }

        if (MaxTrackedTurns is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTrackedTurns));
        }

        if (MaxReplayChunksPerTurn is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxReplayChunksPerTurn));
        }

        if (MaxReplayUtf8BytesPerTurn is < 1 or > 64 * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxReplayUtf8BytesPerTurn));
        }

        if (MaxRetiredAttemptsPerTurn is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetiredAttemptsPerTurn));
        }

        return new AttemptSafeStreamingPresentationOptions
        {
            Stream = Stream.Snapshot(),
            MaxTrackedTurns = MaxTrackedTurns,
            MaxReplayChunksPerTurn = MaxReplayChunksPerTurn,
            MaxReplayUtf8BytesPerTurn = MaxReplayUtf8BytesPerTurn,
            MaxRetiredAttemptsPerTurn = MaxRetiredAttemptsPerTurn
        };
    }
}

/// <summary>
/// Coordinates presentation across provider retries and fallbacks. It retains
/// a bounded replay tail per run/turn, rejects stale deltas from superseded
/// attempts, and marks the first text from every attempt as a replacement.
/// Calls are synchronous and bounded; the coordinator never owns an unbounded
/// producer queue or waits for an engine consumer.
/// </summary>
public sealed class AttemptSafeStreamingPresentationCoordinator
{
    private readonly object _sync = new();
    private readonly AttemptSafeStreamingPresentationOptions _options;
    private readonly Dictionary<TurnKey, TurnState> _turns = new();
    private readonly LinkedList<TurnKey> _turnOrder = new();

    public AttemptSafeStreamingPresentationCoordinator(
        AttemptSafeStreamingPresentationOptions? options = null)
    {
        _options =
            (options ?? new AttemptSafeStreamingPresentationOptions())
            .Snapshot();
    }

    public IReadOnlyList<AttemptStreamingPresentationChunk> BeginAttempt(
        StreamingPresentationAttemptIdentity identity)
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        lock (_sync)
        {
            var state = GetOrCreateState(identity.RunId, identity.TurnId);
            var attemptKey = AttemptKey.From(identity);
            if (state.Completed
                || state.RetiredAttempts.Contains(attemptKey)
                || state.Identity is not null
                && state.Identity.Equals(identity))
            {
                return Array.Empty<AttemptStreamingPresentationChunk>();
            }

            var output = new List<AttemptStreamingPresentationChunk>(2);
            string? supersededStreamAttemptId = null;
            if (state.Active && state.Identity is not null)
            {
                supersededStreamAttemptId =
                    state.Identity.StreamAttemptId;
                AddSuperseded(
                    state,
                    state.Identity,
                    "stream_attempt_replaced",
                    output);
            }

            state.Identity = identity;
            state.Coalescer = new StreamingTextCoalescer(_options.Stream);
            state.Active = true;
            state.Completed = false;
            state.FirstTextPending = true;
            AddChunk(
                state,
                output,
                new AttemptStreamingPresentationChunk(
                    identity,
                    state.NextSequence++,
                    AttemptStreamingPresentationChunkKinds.Reset,
                    string.Empty,
                    isFinal: false,
                    replacesPriorText: true,
                    supersededStreamAttemptId,
                    reasonCode: null));
            Touch(state);
            return ReadOnly(output);
        }
    }

    public IReadOnlyList<AttemptStreamingPresentationChunk> Push(
        StreamingPresentationAttemptIdentity identity,
        string delta,
        DateTimeOffset receivedAt)
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        if (delta is null)
        {
            throw new ArgumentNullException(nameof(delta));
        }

        lock (_sync)
        {
            if (!TryGetActive(identity, out var state))
            {
                return Array.Empty<AttemptStreamingPresentationChunk>();
            }

            var chunks = state.Coalescer!.Push(delta, receivedAt);
            var output = Map(state, identity, chunks);
            Touch(state);
            return output;
        }
    }

    public IReadOnlyList<AttemptStreamingPresentationChunk> FlushIdle(
        StreamingPresentationAttemptIdentity identity,
        DateTimeOffset now)
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        lock (_sync)
        {
            if (!TryGetActive(identity, out var state))
            {
                return Array.Empty<AttemptStreamingPresentationChunk>();
            }

            var chunks = state.Coalescer!.FlushIdle(now);
            var output = Map(state, identity, chunks);
            Touch(state);
            return output;
        }
    }

    public IReadOnlyList<AttemptStreamingPresentationChunk> Complete(
        StreamingPresentationAttemptIdentity identity,
        string finalText)
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        if (finalText is null)
        {
            throw new ArgumentNullException(nameof(finalText));
        }

        lock (_sync)
        {
            if (!TryGetActive(identity, out var state))
            {
                return Array.Empty<AttemptStreamingPresentationChunk>();
            }

            EnsureCanRetire(state, identity);
            var chunks = state.Coalescer!.Complete(finalText);
            var output = Map(state, identity, chunks);
            state.RetiredAttempts.Add(AttemptKey.From(identity));
            state.Active = false;
            state.Completed = true;
            state.Coalescer = null;
            Touch(state);
            return output;
        }
    }

    public IReadOnlyList<AttemptStreamingPresentationChunk> Supersede(
        StreamingPresentationAttemptIdentity identity,
        string reasonCode)
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        reasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
        lock (_sync)
        {
            var key = new TurnKey(identity.RunId, identity.TurnId);
            if (!_turns.TryGetValue(key, out var state))
            {
                state = GetOrCreateState(
                    identity.RunId,
                    identity.TurnId);
                state.Identity = identity;
                var firstOutput =
                    new List<AttemptStreamingPresentationChunk>(1);
                AddSuperseded(
                    state,
                    identity,
                    reasonCode,
                    firstOutput);
                Touch(state);
                return ReadOnly(firstOutput);
            }

            var attemptKey = AttemptKey.From(identity);
            if (state.RetiredAttempts.Contains(attemptKey))
            {
                return Array.Empty<AttemptStreamingPresentationChunk>();
            }

            if (!state.Active
                || state.Identity is null
                || !state.Identity.Equals(identity))
            {
                EnsureCanRetire(state, identity);
                state.RetiredAttempts.Add(attemptKey);
                Touch(state);
                return Array.Empty<AttemptStreamingPresentationChunk>();
            }

            var output = new List<AttemptStreamingPresentationChunk>(1);
            AddSuperseded(state, identity, reasonCode, output);
            Touch(state);
            return ReadOnly(output);
        }
    }

    public IReadOnlyList<AttemptStreamingPresentationChunk> ApplyLifecycle(
        string runId,
        string turnId,
        ProviderAttemptNotice notice)
    {
        runId = RuntimeGuard.RequiredId(runId, nameof(runId));
        turnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        if (notice is null)
        {
            throw new ArgumentNullException(nameof(notice));
        }

        if (!string.Equals(
                notice.Kind,
                ProviderAttemptNoticeKinds.Retry,
                StringComparison.Ordinal)
            && !string.Equals(
                notice.Kind,
                ProviderAttemptNoticeKinds.Fallback,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only retry and fallback lifecycle notices are supported.",
                nameof(notice));
        }

        if (string.IsNullOrEmpty(notice.ProviderAttemptId)
            || string.IsNullOrEmpty(notice.StreamAttemptId))
        {
            return Array.Empty<AttemptStreamingPresentationChunk>();
        }

        var identity = new StreamingPresentationAttemptIdentity(
            runId,
            turnId,
            notice.ProviderId,
            notice.ProviderAttemptId,
            notice.StreamAttemptId);
        return Supersede(identity, notice.ErrorCode);
    }

    public IReadOnlyList<AttemptStreamingPresentationChunk> ApplyDiscard(
        string runId,
        string turnId,
        ProviderResultDiscardedNotice notice)
    {
        runId = RuntimeGuard.RequiredId(runId, nameof(runId));
        turnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        if (notice is null)
        {
            throw new ArgumentNullException(nameof(notice));
        }

        return Supersede(
            new StreamingPresentationAttemptIdentity(
                runId,
                turnId,
                notice.ProviderId,
                notice.ProviderAttemptId,
                notice.StreamAttemptId),
            notice.ReasonCode);
    }

    /// <summary>
    /// Closes a turn that terminated without authoritative final text. Any
    /// active partial stream is superseded before the turn becomes evictable.
    /// </summary>
    public IReadOnlyList<AttemptStreamingPresentationChunk> CloseTurn(
        string runId,
        string turnId,
        string reasonCode)
    {
        runId = RuntimeGuard.RequiredId(runId, nameof(runId));
        turnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        reasonCode = RuntimeGuard.RequiredReasonCode(
            reasonCode,
            nameof(reasonCode));
        lock (_sync)
        {
            var key = new TurnKey(runId, turnId);
            if (!_turns.TryGetValue(key, out var state))
            {
                return Array.Empty<AttemptStreamingPresentationChunk>();
            }

            var output = new List<AttemptStreamingPresentationChunk>(1);
            if (state.Active && state.Identity is not null)
            {
                AddSuperseded(
                    state,
                    state.Identity,
                    reasonCode,
                    output);
            }

            state.Active = false;
            state.Completed = true;
            state.Coalescer = null;
            Touch(state);
            return ReadOnly(output);
        }
    }

    public AttemptStreamingPresentationReplay ReplayFrom(
        string runId,
        string turnId,
        long nextSequence)
    {
        return ReplayFrom(
            runId,
            turnId,
            nextSequence,
            _options.MaxReplayChunksPerTurn);
    }

    public AttemptStreamingPresentationReplay ReplayFrom(
        string runId,
        string turnId,
        long nextSequence,
        int maximumChunks)
    {
        runId = RuntimeGuard.RequiredId(runId, nameof(runId));
        turnId = RuntimeGuard.RequiredId(turnId, nameof(turnId));
        if (nextSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextSequence));
        }

        if (maximumChunks < 1
            || maximumChunks > _options.MaxReplayChunksPerTurn)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChunks));
        }

        lock (_sync)
        {
            var key = new TurnKey(runId, turnId);
            if (!_turns.TryGetValue(key, out var state))
            {
                return new AttemptStreamingPresentationReplay(
                    StreamingPresentationReplayStatus.CursorExpired,
                    runId,
                    turnId,
                    nextSequence,
                    0,
                    nextSequence,
                    0,
                    isComplete: false,
                    Array.Empty<AttemptStreamingPresentationChunk>());
            }

            var earliest =
                state.Replay.First?.Value.Sequence ?? state.NextSequence;
            if (nextSequence < earliest)
            {
                return Replay(
                    state,
                    StreamingPresentationReplayStatus.CursorExpired,
                    nextSequence,
                    earliest,
                    nextSequence,
                    Array.Empty<AttemptStreamingPresentationChunk>());
            }

            if (nextSequence > state.NextSequence)
            {
                return Replay(
                    state,
                    StreamingPresentationReplayStatus.CursorAhead,
                    nextSequence,
                    earliest,
                    nextSequence,
                    Array.Empty<AttemptStreamingPresentationChunk>());
            }

            var output = new List<AttemptStreamingPresentationChunk>(
                Math.Min(maximumChunks, state.Replay.Count));
            var node = state.Replay.First;
            while (node is not null
                   && node.Value.Sequence < nextSequence)
            {
                node = node.Next;
            }

            while (node is not null && output.Count < maximumChunks)
            {
                output.Add(node.Value);
                node = node.Next;
            }

            var continuation = output.Count == 0
                ? nextSequence
                : checked(output[^1].Sequence + 1);
            Touch(state);
            return Replay(
                state,
                StreamingPresentationReplayStatus.Available,
                nextSequence,
                earliest,
                continuation,
                ReadOnly(output));
        }
    }

    private TurnState GetOrCreateState(string runId, string turnId)
    {
        var key = new TurnKey(runId, turnId);
        if (_turns.TryGetValue(key, out var existing))
        {
            Touch(existing);
            return existing;
        }

        if (_turns.Count >= _options.MaxTrackedTurns)
        {
            var candidate = _turnOrder.First;
            while (candidate is not null
                   && !_turns[candidate.Value].Completed)
            {
                candidate = candidate.Next;
            }

            if (candidate is null)
            {
                throw new RuntimeContentLimitException(
                    nameof(runId),
                    "stream_tracked_turns_exceeded",
                    "The presentation coordinator has no terminal turn "
                    + "capacity to evict.");
            }

            var evicted = _turns[candidate.Value];
            _turnOrder.Remove(candidate);
            _turns.Remove(evicted.Key);
        }

        var state = new TurnState(key);
        state.OrderNode = _turnOrder.AddLast(key);
        _turns.Add(key, state);
        return state;
    }

    private bool TryGetActive(
        StreamingPresentationAttemptIdentity identity,
        out TurnState state)
    {
        var key = new TurnKey(identity.RunId, identity.TurnId);
        if (_turns.TryGetValue(key, out state!)
            && state.Active
            && state.Identity is not null
            && state.Identity.Equals(identity))
        {
            return true;
        }

        state = null!;
        return false;
    }

    private IReadOnlyList<AttemptStreamingPresentationChunk> Map(
        TurnState state,
        StreamingPresentationAttemptIdentity identity,
        IReadOnlyList<StreamingPresentationChunk> chunks)
    {
        if (chunks.Count == 0)
        {
            return Array.Empty<AttemptStreamingPresentationChunk>();
        }

        var output =
            new List<AttemptStreamingPresentationChunk>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var item = chunks[index];
            var replaces = item.ReplacesPriorText
                           || state.FirstTextPending;
            state.FirstTextPending = false;
            AddChunk(
                state,
                output,
                new AttemptStreamingPresentationChunk(
                    identity,
                    state.NextSequence++,
                    item.IsFinal
                        ? AttemptStreamingPresentationChunkKinds.Final
                        : AttemptStreamingPresentationChunkKinds.Delta,
                    item.Text,
                    item.IsFinal,
                    replaces,
                    supersededStreamAttemptId: null,
                    reasonCode: null));
        }

        return ReadOnly(output);
    }

    private void AddSuperseded(
        TurnState state,
        StreamingPresentationAttemptIdentity identity,
        string reasonCode,
        List<AttemptStreamingPresentationChunk> output)
    {
        EnsureCanRetire(state, identity);
        AddChunk(
            state,
            output,
            new AttemptStreamingPresentationChunk(
                identity,
                state.NextSequence++,
                AttemptStreamingPresentationChunkKinds.Superseded,
                string.Empty,
                isFinal: false,
                replacesPriorText: true,
                identity.StreamAttemptId,
                reasonCode));
        state.RetiredAttempts.Add(AttemptKey.From(identity));
        state.Active = false;
        state.Completed = false;
        state.Coalescer = null;
        state.FirstTextPending = true;
    }

    private void EnsureCanRetire(
        TurnState state,
        StreamingPresentationAttemptIdentity identity)
    {
        var attemptKey = AttemptKey.From(identity);
        if (!state.RetiredAttempts.Contains(attemptKey)
            && state.RetiredAttempts.Count
            >= _options.MaxRetiredAttemptsPerTurn)
        {
            throw new RuntimeContentLimitException(
                nameof(identity),
                "stream_retired_attempts_exceeded",
                "The presentation turn exceeded its retired-attempt "
                + "capacity.");
        }
    }

    private void AddChunk(
        TurnState state,
        List<AttemptStreamingPresentationChunk> output,
        AttemptStreamingPresentationChunk chunk)
    {
        output.Add(chunk);
        var chunkBytes = Encoding.UTF8.GetByteCount(chunk.Text);
        state.Replay.AddLast(chunk);
        state.ReplayUtf8Bytes = checked(
            state.ReplayUtf8Bytes + chunkBytes);
        while (state.Replay.Count > _options.MaxReplayChunksPerTurn
               || state.ReplayUtf8Bytes
               > _options.MaxReplayUtf8BytesPerTurn)
        {
            var first = state.Replay.First!;
            state.ReplayUtf8Bytes -= Encoding.UTF8.GetByteCount(
                first.Value.Text);
            state.Replay.RemoveFirst();
        }
    }

    private void Touch(TurnState state)
    {
        if (state.OrderNode is null
            || ReferenceEquals(state.OrderNode, _turnOrder.Last))
        {
            return;
        }

        _turnOrder.Remove(state.OrderNode);
        state.OrderNode = _turnOrder.AddLast(state.Key);
    }

    private static AttemptStreamingPresentationReplay Replay(
        TurnState state,
        StreamingPresentationReplayStatus status,
        long requestedSequence,
        long earliestAvailableSequence,
        long continuationSequence,
        IReadOnlyList<AttemptStreamingPresentationChunk> chunks)
    {
        return new AttemptStreamingPresentationReplay(
            status,
            state.Key.RunId,
            state.Key.TurnId,
            requestedSequence,
            earliestAvailableSequence,
            continuationSequence,
            state.NextSequence,
            state.Completed,
            chunks);
    }

    private static IReadOnlyList<AttemptStreamingPresentationChunk> ReadOnly(
        List<AttemptStreamingPresentationChunk> chunks)
    {
        return new ReadOnlyCollection<AttemptStreamingPresentationChunk>(
            chunks);
    }

    private readonly struct TurnKey : IEquatable<TurnKey>
    {
        public TurnKey(string runId, string turnId)
        {
            RunId = runId;
            TurnId = turnId;
        }

        public string RunId { get; }

        public string TurnId { get; }

        public bool Equals(TurnKey other)
        {
            return string.Equals(
                       RunId,
                       other.RunId,
                       StringComparison.Ordinal)
                   && string.Equals(
                       TurnId,
                       other.TurnId,
                       StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is TurnKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(RunId, TurnId);
        }
    }

    private readonly struct AttemptKey : IEquatable<AttemptKey>
    {
        public AttemptKey(
            string providerAttemptId,
            string streamAttemptId)
        {
            ProviderAttemptId = providerAttemptId;
            StreamAttemptId = streamAttemptId;
        }

        public string ProviderAttemptId { get; }

        public string StreamAttemptId { get; }

        public static AttemptKey From(
            StreamingPresentationAttemptIdentity identity)
        {
            return new AttemptKey(
                identity.ProviderAttemptId,
                identity.StreamAttemptId);
        }

        public bool Equals(AttemptKey other)
        {
            return string.Equals(
                       ProviderAttemptId,
                       other.ProviderAttemptId,
                       StringComparison.Ordinal)
                   && string.Equals(
                       StreamAttemptId,
                       other.StreamAttemptId,
                       StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is AttemptKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                ProviderAttemptId,
                StreamAttemptId);
        }
    }

    private sealed class TurnState
    {
        public TurnState(TurnKey key)
        {
            Key = key;
        }

        public TurnKey Key { get; }

        public StreamingPresentationAttemptIdentity? Identity { get; set; }

        public StreamingTextCoalescer? Coalescer { get; set; }

        public bool Active { get; set; }

        public bool Completed { get; set; }

        public bool FirstTextPending { get; set; }

        public long NextSequence { get; set; }

        public LinkedList<AttemptStreamingPresentationChunk> Replay { get; } =
            new();

        public int ReplayUtf8Bytes { get; set; }

        public HashSet<AttemptKey> RetiredAttempts { get; } = new();

        public LinkedListNode<TurnKey>? OrderNode { get; set; }
    }
}

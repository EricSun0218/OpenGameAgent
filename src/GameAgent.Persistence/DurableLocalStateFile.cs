using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GameAgent.Persistence;

public sealed class DurableLocalStateFileOptions
{
    public int MaxFrameBytes { get; set; } = 64 * 1024 * 1024;

    public long MaxFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public int MaxFrames { get; set; } = 100_000;

    internal void Validate()
    {
        if (MaxFrameBytes is < 4_096 or > 256 * 1024 * 1024
            || MaxFileBytes < MaxFrameBytes
            || MaxFileBytes > 16L * 1024 * 1024 * 1024
            || MaxFrames is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(DurableLocalStateFileOptions));
        }
    }
}

public sealed class DurableLocalStateFileException : IOException
{
    public DurableLocalStateFileException(
        string reasonCode,
        string path,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
        Path = path;
    }

    public string ReasonCode { get; }

    public string Path { get; }
}

internal readonly struct DurableStateMutation<TState, TResult>
{
    public DurableStateMutation(bool commit, TState state, TResult result)
    {
        Commit = commit;
        State = state;
        Result = result;
    }

    public bool Commit { get; }

    public TState State { get; }

    public TResult Result { get; }
}

internal sealed class DurableLocalStateFile<TState> : IAsyncDisposable
    where TState : class
{
    private const uint Magic = 0x46534147;
    private const int FormatVersion = 1;
    private const int HeaderBytes = 16;

    private readonly string _path;
    private readonly DurableLocalStateFileOptions _options;
    private readonly JsonTypeInfo<TState> _jsonTypeInfo;
    private readonly Func<TState, TState> _clone;
    private readonly Func<TState, long> _revision;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ExclusiveFileWriterLease? _writerLease;
    private TState _state;
    private int _frameCount;
    private int _faulted;
    private int _disposeState;

    public DurableLocalStateFile(
        string path,
        TState initialState,
        JsonTypeInfo<TState> jsonTypeInfo,
        Func<TState, TState> clone,
        Func<TState, long> revision,
        DurableLocalStateFileOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A durable state path is required.", nameof(path));
        }

        _path = System.IO.Path.GetFullPath(path);
        _options = options ?? new DurableLocalStateFileOptions();
        _options.Validate();
        _jsonTypeInfo = jsonTypeInfo ?? throw new ArgumentNullException(nameof(jsonTypeInfo));
        _clone = clone ?? throw new ArgumentNullException(nameof(clone));
        _revision = revision ?? throw new ArgumentNullException(nameof(revision));
        _state = _clone(initialState ?? throw new ArgumentNullException(nameof(initialState)));
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writerLease = ExclusiveFileWriterLease.Acquire(_path);
        try
        {
            Recover();
        }
        catch
        {
            _writerLease.Dispose();
            _writerLease = null;
            throw;
        }
    }

    public async ValueTask<TState> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            return _clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<TResult> MutateAsync<TResult>(
        Func<TState, DurableStateMutation<TState, TResult>> mutation,
        CancellationToken cancellationToken)
    {
        if (mutation is null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }

        ThrowIfUnavailable();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var current = _clone(_state);
            var change = mutation(current);
            if (!change.Commit)
            {
                return change.Result;
            }

            if (change.State is null
                || _revision(change.State) != checked(_revision(_state) + 1))
            {
                throw new InvalidOperationException(
                    "A durable state mutation must advance its aggregate revision exactly once.");
            }

            Append(change.State, cancellationToken);
            _state = _clone(change.State);
            return change.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Recover()
    {
        var stream = Stream;
        stream.Position = 0;
        var header = new byte[HeaderBytes];
        long committedLength = 0;
        long previousRevision = _revision(_state);
        while (stream.Position < stream.Length)
        {
            var frameOffset = stream.Position;
            var remaining = stream.Length - frameOffset;
            if (remaining < HeaderBytes)
            {
                TruncateTornTail(committedLength);
                break;
            }

            ReadExactly(stream, header, HeaderBytes);
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            if (magic != Magic || version != FormatVersion
                || payloadLength < 2 || payloadLength > _options.MaxFrameBytes)
            {
                throw Corrupt(frameOffset, "The durable state frame header is invalid.");
            }

            if (stream.Length - stream.Position < payloadLength)
            {
                TruncateTornTail(committedLength);
                break;
            }

            var payload = new byte[payloadLength];
            ReadExactly(stream, payload, payloadLength);
            if (Crc32.Compute(payload) != checksum)
            {
                throw Corrupt(frameOffset, "The durable state frame checksum is invalid.");
            }

            TState recovered;
            try
            {
                recovered = JsonSerializer.Deserialize(payload, _jsonTypeInfo)
                    ?? throw new JsonException("The durable state payload is null.");
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw Corrupt(frameOffset, "The durable state payload is invalid.", exception);
            }

            var recoveredRevision = _revision(recovered);
            if (recoveredRevision != checked(previousRevision + 1))
            {
                throw Corrupt(frameOffset, "The durable state revision sequence is invalid.");
            }

            _state = _clone(recovered);
            previousRevision = recoveredRevision;
            _frameCount++;
            if (_frameCount > _options.MaxFrames)
            {
                throw Capacity("The durable state frame limit was exceeded during recovery.");
            }

            committedLength = stream.Position;
        }

        stream.Position = stream.Length;
    }

    private void Append(TState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_frameCount >= _options.MaxFrames)
        {
            throw Capacity("The durable state frame limit is full.");
        }

        using var payload = BoundedJsonPayload.Serialize(
            state,
            _jsonTypeInfo,
            _options.MaxFrameBytes,
            attempted => Capacity($"A durable state frame attempted {attempted} bytes."));
        var attemptedLength = checked(Stream.Length + HeaderBytes + payload.WrittenCount);
        if (attemptedLength > _options.MaxFileBytes)
        {
            throw Capacity("The durable state file byte limit is full.");
        }

        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), payload.WrittenCount);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(12, 4),
            Crc32.Compute(payload.WrittenSpan));
        var originalLength = Stream.Length;
        try
        {
            Stream.Position = originalLength;
            Stream.Write(header, 0, header.Length);
            var bytes = payload.WrittenSpan.ToArray();
            Stream.Write(bytes, 0, bytes.Length);
            Stream.Flush(flushToDisk: true);
            _frameCount++;
        }
        catch
        {
            try
            {
                Stream.SetLength(originalLength);
                Stream.Position = originalLength;
                Stream.Flush(flushToDisk: true);
            }
            catch
            {
                Volatile.Write(ref _faulted, 1);
            }

            throw;
        }
    }

    private void TruncateTornTail(long committedLength)
    {
        Stream.SetLength(committedLength);
        Stream.Position = committedLength;
        Stream.Flush(flushToDisk: true);
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private FileStream Stream =>
        _writerLease?.Stream
        ?? throw new ObjectDisposedException(nameof(DurableLocalStateFile<TState>));

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _disposeState) != 0 || _writerLease is null)
        {
            throw new ObjectDisposedException(nameof(DurableLocalStateFile<TState>));
        }

        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new DurableLocalStateFileException(
                "durable_state_faulted",
                _path,
                "The durable state writer is faulted and must be reopened.");
        }
    }

    private DurableLocalStateFileException Corrupt(
        long offset,
        string message,
        Exception? inner = null) =>
        new(
            "durable_state_corrupt",
            _path,
            $"{message} Offset: {offset}.",
            inner);

    private DurableLocalStateFileException Capacity(string message) =>
        new("durable_state_capacity", _path, message);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _writerLease, null)?.Dispose();
        }
        finally
        {
            _gate.Release();
        }

        Volatile.Write(ref _disposeState, 2);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var checksum = uint.MaxValue;
            foreach (var item in data)
            {
                checksum = Table[(checksum ^ item) & 0xFF] ^ (checksum >> 8);
            }

            return ~checksum;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xEDB88320U ^ (value >> 1)
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GameAgent.Persistence;

internal sealed class BoundedJsonPayload :
    IBufferWriter<byte>,
    IDisposable
{
    private const int DefaultSizeHint = 256;
    private const int MaximumJsonExpansionFactor = 6;
    private const int WriterSlackBytes = 4_096;

    private readonly int _maximumBytes;
    private readonly int _maximumScratchBytes;
    private readonly Func<long, Exception> _capacityExceptionFactory;
    private readonly ArrayBufferWriter<byte> _output = new();
    private byte[]? _scratch;

    private BoundedJsonPayload(
        int maximumBytes,
        Func<long, Exception> capacityExceptionFactory)
    {
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _maximumBytes = maximumBytes;
        _maximumScratchBytes = (int)Math.Min(
            int.MaxValue,
            (long)maximumBytes * MaximumJsonExpansionFactor
            + WriterSlackBytes);
        _capacityExceptionFactory =
            capacityExceptionFactory
            ?? throw new ArgumentNullException(
                nameof(capacityExceptionFactory));
    }

    public int WrittenCount => _output.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _output.WrittenSpan;

    public static BoundedJsonPayload Serialize<T>(
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        int maximumBytes,
        Func<long, Exception> capacityExceptionFactory)
    {
        var payload = new BoundedJsonPayload(
            maximumBytes,
            capacityExceptionFactory);
        try
        {
            using var writer = new Utf8JsonWriter(payload);
            JsonSerializer.Serialize(writer, value, jsonTypeInfo);
            writer.Flush();
            return payload;
        }
        catch
        {
            payload.Dispose();
            throw;
        }
    }

    public static BoundedJsonPayload Write(
        int maximumBytes,
        Func<long, Exception> capacityExceptionFactory,
        Action<Utf8JsonWriter> write)
    {
        if (write is null)
        {
            throw new ArgumentNullException(nameof(write));
        }

        var payload = new BoundedJsonPayload(
            maximumBytes,
            capacityExceptionFactory);
        try
        {
            using var writer = new Utf8JsonWriter(payload);
            write(writer);
            writer.Flush();
            return payload;
        }
        catch
        {
            payload.Dispose();
            throw;
        }
    }

    public void Advance(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (_scratch is null || count > _scratch.Length)
        {
            throw new InvalidOperationException(
                "The JSON writer advanced beyond its requested buffer.");
        }

        var attempted = (long)_output.WrittenCount + count;
        if (attempted > _maximumBytes)
        {
            throw _capacityExceptionFactory(attempted);
        }

        var destination = _output.GetSpan(count);
        _scratch.AsSpan(0, count).CopyTo(destination);
        _output.Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureScratch(sizeHint);
        return _scratch!;
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureScratch(sizeHint);
        return _scratch!;
    }

    public void Dispose()
    {
        var scratch = _scratch;
        _scratch = null;
        if (scratch is not null)
        {
            ArrayPool<byte>.Shared.Return(
                scratch,
                clearArray: true);
        }
    }

    private void EnsureScratch(int sizeHint)
    {
        if (sizeHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }

        var required = sizeHint == 0
            ? DefaultSizeHint
            : sizeHint;
        if (required > _maximumScratchBytes)
        {
            throw _capacityExceptionFactory(
                Math.Max(
                    (long)_maximumBytes + 1,
                    (long)_output.WrittenCount + required));
        }

        if (_scratch is not null && _scratch.Length >= required)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(required);
        var previous = _scratch;
        _scratch = replacement;
        if (previous is not null)
        {
            ArrayPool<byte>.Shared.Return(
                previous,
                clearArray: true);
        }
    }
}

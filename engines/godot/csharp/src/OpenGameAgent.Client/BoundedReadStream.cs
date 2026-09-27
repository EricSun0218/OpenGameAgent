namespace OpenGameAgent.Client;

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream inner;
    private readonly long maxBytes;
    private readonly bool resetAtSseBoundary;
    private long bytesRead;
    private bool lineHasContent;
    private bool pendingCarriageReturn;
    private bool pendingLineIsBlank;

    public BoundedReadStream(Stream inner, long maxBytes, bool resetAtSseBoundary)
    {
        this.inner = inner;
        this.maxBytes = maxBytes;
        this.resetAtSseBoundary = resetAtSseBoundary;
    }

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Observe(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        Observe(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return ReadArrayAsync(buffer, offset, count, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task<int> ReadArrayAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        int read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Observe(buffer.AsSpan(offset, read));
        return read;
    }

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        foreach (byte current in bytes)
        {
            if (pendingCarriageReturn)
            {
                if (current == (byte)'\n')
                {
                    Count();
                    FinishLine(pendingLineIsBlank);
                    continue;
                }
                FinishLine(pendingLineIsBlank);
            }

            Count();
            if (current == (byte)'\r')
            {
                pendingCarriageReturn = true;
                pendingLineIsBlank = !lineHasContent;
            }
            else if (current == (byte)'\n')
            {
                FinishLine(!lineHasContent);
            }
            else
            {
                lineHasContent = true;
            }
        }
    }

    private void Count()
    {
        bytesRead = checked(bytesRead + 1);
        if (bytesRead > maxBytes)
        {
            throw new RuntimeResponseLimitException(
                resetAtSseBoundary ? "SSE event" : "HTTP response");
        }
    }

    private void FinishLine(bool isBlank)
    {
        pendingCarriageReturn = false;
        lineHasContent = false;
        if (resetAtSseBoundary && isBlank) bytesRead = 0;
    }
}

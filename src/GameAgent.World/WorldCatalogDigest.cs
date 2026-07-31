using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.World;

internal static class WorldCatalogDigest
{
    private const long MaximumCanonicalBytes = 64L * 1024 * 1024;

    public static string Compute(
        Action<Utf8JsonWriter> write,
        string parameterName)
    {
        if (write is null)
        {
            throw new ArgumentNullException(nameof(write));
        }

        using var stream = new BoundedHashingWriteStream(
            MaximumCanonicalBytes,
            parameterName);
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
            writer.Flush();
        }

        return stream.GetDigest();
    }

    private sealed class BoundedHashingWriteStream : Stream
    {
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        private readonly long _maximumBytes;
        private readonly string _parameterName;
        private long _written;
        private bool _completed;

        public BoundedHashingWriteStream(
            long maximumBytes,
            string parameterName)
        {
            _maximumBytes = maximumBytes;
            _parameterName = parameterName;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_completed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            Reserve(count);
            _hash.AppendData(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            _hash.AppendData(buffer);
        }

        public string GetDigest()
        {
            if (_completed)
            {
                throw new InvalidOperationException(
                    "The digest has already been completed.");
            }

            _completed = true;
            var digest = _hash.GetHashAndReset();
            var text = new StringBuilder(digest.Length * 2);
            foreach (var value in digest)
            {
                text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Reserve(int count)
        {
            if (_completed)
            {
                throw new ObjectDisposedException(
                    nameof(BoundedHashingWriteStream));
            }

            try
            {
                _written = checked(_written + count);
            }
            catch (OverflowException)
            {
                ThrowLimitExceeded();
            }

            if (_written > _maximumBytes)
            {
                ThrowLimitExceeded();
            }
        }

        private void ThrowLimitExceeded()
        {
            throw new ArgumentException(
                "The catalog canonical representation exceeds its byte "
                + "limit.",
                _parameterName);
        }
    }
}

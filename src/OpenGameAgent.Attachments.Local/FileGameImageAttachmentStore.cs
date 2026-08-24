using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Attachments;
using SkiaSharp;

namespace OpenGameAgent.Attachments.Local;

public sealed class FileGameImageAttachmentStore : IGameImageAttachmentStore
{
    private const string IdPrefix = "sha256:";
    private const int ConcurrentPublishReadAttempts = 8;
    private readonly string _root;

    public FileGameImageAttachmentStore(string rootDirectory, GameImageAttachmentLimits? imageLimits = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("An attachment storage directory is required.", nameof(rootDirectory));
        }

        _root = Path.GetFullPath(rootDirectory);
        ImageLimits = imageLimits ?? new GameImageAttachmentLimits();
    }

    public GameImageAttachmentLimits ImageLimits { get; }

    public async ValueTask ValidateImageAsync(
        SaveGameImageAttachment input,
        CancellationToken cancellationToken = default)
    {
        _ = await InspectAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GameImageAttachment> SaveImageAsync(
        SaveGameImageAttachment input,
        CancellationToken cancellationToken = default)
    {
        var data = input.Data.ToArray();
        var metadata = await InspectAsync(input, data, cancellationToken).ConfigureAwait(false);
        var hash = ComputeSha256(data);
        var bucket = Path.Combine(_root, "objects", hash.Substring(0, 2));
        var staging = Path.Combine(_root, "tmp");
        EnsurePrivateDirectory(bucket);
        EnsurePrivateDirectory(staging);
        var target = Path.Combine(bucket, hash);
        var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            await WriteDurablyAsync(temporary, data, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                var existing = await ReadAfterConcurrentPublishAsync(
                    target,
                    data.Length,
                    cancellationToken).ConfigureAwait(false);
                if (!FixedEquals(hash, ComputeSha256(existing)))
                {
                    throw new GameAttachmentException(
                        "ATTACHMENT_CORRUPT",
                        "The stored attachment failed integrity verification.");
                }
            }

            FilePermissions.TryRestrictFile(target);
            DirectoryDurability.TrySync(bucket);
            DirectoryDurability.TrySync(Path.Combine(_root, "objects"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GameAttachmentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GameAttachmentException(
                "ATTACHMENT_WRITE_FAILED",
                "Unable to persist the image attachment.",
                exception);
        }
        finally
        {
            TryDelete(temporary);
        }

        return new GameImageAttachment(
            IdPrefix + hash,
            metadata.MediaType,
            data.Length,
            metadata.Width,
            metadata.Height,
            SanitizeName(input.Name));
    }

    public async ValueTask<StoredGameImageAttachment> ReadImageAsync(
        GameImageAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        if (attachment is null)
        {
            throw new ArgumentNullException(nameof(attachment));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(attachment);
        var hash = ParseAttachmentId(attachment.AttachmentId);
        var path = Path.Combine(_root, "objects", hash.Substring(0, 2), hash);
        byte[] data;
        try
        {
            data = await ReadBoundedAsync(path, attachment.Bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new GameAttachmentException("ATTACHMENT_NOT_FOUND", "The attachment object is missing.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new GameAttachmentException("ATTACHMENT_NOT_FOUND", "The attachment object is missing.", exception);
        }
        catch (GameAttachmentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GameAttachmentException("ATTACHMENT_READ_FAILED", "Unable to read the image attachment.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!FixedEquals(hash, ComputeSha256(data)))
        {
            throw new GameAttachmentException("ATTACHMENT_CORRUPT", "The stored attachment failed integrity verification.");
        }

        var metadata = Probe(data);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(metadata.MediaType, attachment.MediaType, StringComparison.Ordinal)
            || data.Length != attachment.Bytes
            || metadata.Width != attachment.Width
            || metadata.Height != attachment.Height)
        {
            throw new GameAttachmentException("ATTACHMENT_CORRUPT", "Stored attachment metadata does not match its reference.");
        }

        return new StoredGameImageAttachment(attachment, data);
    }

    private async ValueTask<ImageMetadata> InspectAsync(
        SaveGameImageAttachment input,
        CancellationToken cancellationToken)
    {
        var data = input?.Data.ToArray() ?? throw new ArgumentNullException(nameof(input));
        return await InspectAsync(input, data, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ImageMetadata> InspectAsync(
        SaveGameImageAttachment input,
        byte[] data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length == 0)
        {
            throw new GameAttachmentException("INVALID_IMAGE", "The image is empty.");
        }

        if (data.Length > ImageLimits.MaxImageBytes)
        {
            throw new GameAttachmentException("IMAGE_TOO_LARGE", "The image exceeds the configured byte limit.");
        }

        if (!ImageLimits.MediaTypes.Contains(input.MediaType, StringComparer.Ordinal))
        {
            throw new GameAttachmentException("UNSUPPORTED_IMAGE_TYPE", "The image media type is not accepted by this deployment.");
        }

        var metadata = Probe(data);
        if (!string.Equals(metadata.MediaType, input.MediaType, StringComparison.Ordinal))
        {
            throw new GameAttachmentException("IMAGE_TYPE_MISMATCH", "The declared image type does not match its bytes.");
        }

        if ((long)metadata.Width * metadata.Height > ImageLimits.MaxImagePixels)
        {
            throw new GameAttachmentException("IMAGE_TOO_MANY_PIXELS", "The image exceeds the configured decoded-pixel limit.");
        }

        await Task.Run(() => DecodeFully(data, metadata), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return metadata;
    }

    private void ValidateReference(GameImageAttachment attachment)
    {
        if (attachment.Bytes > ImageLimits.MaxImageBytes
            || (long)attachment.Width * attachment.Height > ImageLimits.MaxImagePixels
            || !ImageLimits.MediaTypes.Contains(attachment.MediaType, StringComparer.Ordinal))
        {
            throw new GameAttachmentException(
                "INVALID_ATTACHMENT_REF",
                "The attachment reference exceeds this store's admission policy.");
        }
    }

    private static ImageMetadata Probe(byte[] data)
    {
        try
        {
            using var skData = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(skData);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                throw new GameAttachmentException("INVALID_IMAGE", "Unsupported or malformed image data.");
            }

            return new ImageMetadata(ToMediaType(codec.EncodedFormat), codec.Info.Width, codec.Info.Height);
        }
        catch (GameAttachmentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GameAttachmentException("INVALID_IMAGE", "Unsupported or malformed image data.", exception);
        }
    }

    private static void DecodeFully(byte[] data, ImageMetadata metadata)
    {
        try
        {
            using var skData = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(skData);
            if (codec is null)
            {
                throw new GameAttachmentException("INVALID_IMAGE", "Unsupported or malformed image data.");
            }

            var info = new SKImageInfo(metadata.Width, metadata.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var byteCount = checked(info.RowBytes * metadata.Height);
            var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    var result = codec.GetPixels(
                        info,
                        handle.AddrOfPinnedObject(),
                        info.RowBytes,
                        new SKCodecOptions());
                    if (result != SKCodecResult.Success)
                    {
                        throw new GameAttachmentException("INVALID_IMAGE", "Unsupported or malformed image data.");
                    }
                }
                finally
                {
                    handle.Free();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels, clearArray: true);
            }
        }
        catch (GameAttachmentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GameAttachmentException("INVALID_IMAGE", "Unsupported or malformed image data.", exception);
        }
    }

    private static string ToMediaType(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => GameImageMediaTypes.Png,
        SKEncodedImageFormat.Jpeg => GameImageMediaTypes.Jpeg,
        SKEncodedImageFormat.Webp => GameImageMediaTypes.WebP,
        SKEncodedImageFormat.Gif => GameImageMediaTypes.Gif,
        _ => throw new GameAttachmentException("INVALID_IMAGE", "Unsupported or malformed image data."),
    };

    private static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            _ = builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool FixedEquals(string expected, string actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        var difference = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            difference |= expected[index] ^ actual[index];
        }

        return difference == 0;
    }

    private static string ParseAttachmentId(string value)
    {
        if (!value.StartsWith(IdPrefix, StringComparison.Ordinal) || value.Length != IdPrefix.Length + 64)
        {
            throw new GameAttachmentException("INVALID_ATTACHMENT_REF", "The attachment reference is invalid.");
        }

        var hash = value.Substring(IdPrefix.Length);
        foreach (var character in hash)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                throw new GameAttachmentException("INVALID_ATTACHMENT_REF", "The attachment reference is invalid.");
            }
        }

        return hash;
    }

    private static async Task WriteDurablyAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(data, 0, data.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        FilePermissions.TryRestrictFile(path);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int expectedBytes, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedBytes || stream.Length <= 0 || stream.Length > int.MaxValue)
        {
            throw new GameAttachmentException("ATTACHMENT_CORRUPT", "Stored attachment length does not match its reference.");
        }

        var data = new byte[expectedBytes];
        var offset = 0;
        while (offset < data.Length)
        {
            var read = await stream.ReadAsync(data, offset, data.Length - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new GameAttachmentException("ATTACHMENT_CORRUPT", "The stored attachment ended unexpectedly.");
            }

            offset += read;
        }

        return data;
    }

    private static async Task<byte[]> ReadAfterConcurrentPublishAsync(
        string path,
        int expectedBytes,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReadBoundedAsync(path, expectedBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt + 1 < ConcurrentPublishReadAttempts && File.Exists(path))
            {
                // A concurrent content-addressed publish can make the final path visible
                // just before Windows releases the move handle. Retry only this verification
                // read; the staged write and atomic publish are never repeated.
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        FilePermissions.TryRestrictDirectory(path);
    }

    private static string? SanitizeName(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        var leaf = slash >= 0 ? value.Substring(slash + 1) : value;
        var builder = new StringBuilder(Math.Min(leaf.Length, 255));
        foreach (var character in leaf)
        {
            if (!char.IsControl(character) && builder.Length < 255)
            {
                _ = builder.Append(character);
            }
        }

        var clean = builder.ToString().Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale private staging object is harmless and can be removed by host maintenance.
        }
    }

    private readonly struct ImageMetadata
    {
        public ImageMetadata(string mediaType, int width, int height)
        {
            MediaType = mediaType;
            Width = width;
            Height = height;
        }

        public string MediaType { get; }

        public int Width { get; }

        public int Height { get; }
    }

    private static class FilePermissions
    {
        public static void TryRestrictDirectory(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _ = Chmod(path, Convert.ToUInt32("700", 8));
            }
        }

        public static void TryRestrictFile(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _ = Chmod(path, Convert.ToUInt32("600", 8));
            }
        }

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int Chmod(string path, uint mode);
    }

    private static class DirectoryDurability
    {
        private const int ReadOnly = 0;

        public static void TrySync(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !Directory.Exists(path))
            {
                return;
            }

            var descriptor = Open(path, ReadOnly);
            if (descriptor < 0)
            {
                return;
            }

            try
            {
                _ = Fsync(descriptor);
            }
            finally
            {
                _ = Close(descriptor);
            }
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        private static extern int Fsync(int descriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int descriptor);
    }
}

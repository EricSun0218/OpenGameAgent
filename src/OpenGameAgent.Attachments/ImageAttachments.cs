using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenGameAgent.Attachments;

public static class GameImageMediaTypes
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string WebP = "image/webp";
    public const string Gif = "image/gif";

    public static IReadOnlyList<string> Raster { get; } = Array.AsReadOnly(new[]
    {
        Png,
        Jpeg,
        WebP,
        Gif,
    });

    public static bool IsRaster(string mediaType) =>
        string.Equals(mediaType, Png, StringComparison.Ordinal)
        || string.Equals(mediaType, Jpeg, StringComparison.Ordinal)
        || string.Equals(mediaType, WebP, StringComparison.Ordinal)
        || string.Equals(mediaType, Gif, StringComparison.Ordinal);
}

public sealed class GameImageAttachmentLimits
{
    public const int DefaultMaxImageBytes = 5 * 1024 * 1024;
    public const int DefaultMaxImagesPerMessage = 20;
    public const int DefaultMaxMessageImageBytes = 100 * 1024 * 1024;
    public const long DefaultMaxImagePixels = 40_000_000;

    public GameImageAttachmentLimits(
        int maxImageBytes = DefaultMaxImageBytes,
        int maxImagesPerMessage = DefaultMaxImagesPerMessage,
        int maxMessageImageBytes = DefaultMaxMessageImageBytes,
        long maxImagePixels = DefaultMaxImagePixels,
        IReadOnlyList<string>? mediaTypes = null)
    {
        if (maxImageBytes <= 0 || maxImageBytes > 512 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxImageBytes));
        }

        if (maxImagesPerMessage <= 0 || maxImagesPerMessage > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxImagesPerMessage));
        }

        if (maxMessageImageBytes < maxImageBytes || maxMessageImageBytes > 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessageImageBytes));
        }

        if (maxImagePixels <= 0 || maxImagePixels > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxImagePixels));
        }

        var accepted = mediaTypes is null ? GameImageMediaTypes.Raster : new ReadOnlyCollection<string>(CopyMediaTypes(mediaTypes));
        MaxImageBytes = maxImageBytes;
        MaxImagesPerMessage = maxImagesPerMessage;
        MaxMessageImageBytes = maxMessageImageBytes;
        MaxImagePixels = maxImagePixels;
        MediaTypes = accepted;
    }

    public int MaxImageBytes { get; }

    public int MaxImagesPerMessage { get; }

    public int MaxMessageImageBytes { get; }

    public long MaxImagePixels { get; }

    public IReadOnlyList<string> MediaTypes { get; }

    private static string[] CopyMediaTypes(IReadOnlyList<string> values)
    {
        if (values.Count == 0 || values.Count > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(values));
        }

        var copy = new string[values.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (!GameImageMediaTypes.IsRaster(value) || !seen.Add(value))
            {
                throw new ArgumentException("Image media types must be unique supported raster values.", nameof(values));
            }

            copy[index] = value;
        }

        return copy;
    }
}

public sealed class GameImageAttachment
{
    public GameImageAttachment(
        string attachmentId,
        string mediaType,
        int bytes,
        int width,
        int height,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || attachmentId.Length > 256 || ContainsControl(attachmentId))
        {
            throw new ArgumentException("A bounded opaque attachment ID is required.", nameof(attachmentId));
        }

        if (!GameImageMediaTypes.IsRaster(mediaType))
        {
            throw new ArgumentException("The image media type is unsupported.", nameof(mediaType));
        }

        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (name is { Length: > 255 } || ContainsControl(name))
        {
            throw new ArgumentException("The attachment name is invalid.", nameof(name));
        }

        AttachmentId = attachmentId;
        MediaType = mediaType;
        Bytes = bytes;
        Width = width;
        Height = height;
        Name = string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public string AttachmentId { get; }

    public string MediaType { get; }

    public int Bytes { get; }

    public int Width { get; }

    public int Height { get; }

    public string? Name { get; }

    private static bool ContainsControl(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class SaveGameImageAttachment
{
    private readonly byte[] _data;

    public SaveGameImageAttachment(byte[] data, string mediaType, string? name = null)
    {
        _data = data is null ? throw new ArgumentNullException(nameof(data)) : (byte[])data.Clone();
        if (!GameImageMediaTypes.IsRaster(mediaType))
        {
            throw new ArgumentException("The image media type is unsupported.", nameof(mediaType));
        }

        if (name is { Length: > 4096 } || name?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("The source image name is invalid.", nameof(name));
        }

        MediaType = mediaType;
        Name = name;
    }

    public ReadOnlyMemory<byte> Data => _data;

    public string MediaType { get; }

    public string? Name { get; }
}

public sealed class StoredGameImageAttachment
{
    private readonly byte[] _data;

    public StoredGameImageAttachment(GameImageAttachment attachment, byte[] data)
    {
        Attachment = attachment ?? throw new ArgumentNullException(nameof(attachment));
        _data = data is null ? throw new ArgumentNullException(nameof(data)) : (byte[])data.Clone();
    }

    public GameImageAttachment Attachment { get; }

    public ReadOnlyMemory<byte> Data => _data;
}

public interface IGameImageAttachmentStore
{
    GameImageAttachmentLimits ImageLimits { get; }

    ValueTask ValidateImageAsync(SaveGameImageAttachment input, CancellationToken cancellationToken = default);

    ValueTask<GameImageAttachment> SaveImageAsync(SaveGameImageAttachment input, CancellationToken cancellationToken = default);

    ValueTask<StoredGameImageAttachment> ReadImageAsync(GameImageAttachment attachment, CancellationToken cancellationToken = default);
}

public sealed class GameAttachmentException : Exception
{
    public GameAttachmentException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128)
        {
            throw new ArgumentException("A bounded attachment error code is required.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }
}

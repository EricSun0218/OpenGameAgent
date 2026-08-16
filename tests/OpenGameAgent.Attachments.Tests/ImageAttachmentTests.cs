using OpenGameAgent.Attachments;
using OpenGameAgent.Attachments.Local;
using SkiaSharp;
using Xunit;

namespace OpenGameAgent.Attachments.Tests;

public sealed class ImageAttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oga-attachments-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SavesReadsDeduplicatesAndSanitizesNames()
    {
        var store = new FileGameImageAttachmentStore(_root);
        var png = CreateImage(SKEncodedImageFormat.Png, 3, 2);
        var token = TestContext.Current.CancellationToken;
        var first = await store.SaveImageAsync(new SaveGameImageAttachment(
            png,
            GameImageMediaTypes.Png,
            "C:\\private\\capture.png"), token);
        var second = await store.SaveImageAsync(new SaveGameImageAttachment(
            png,
            GameImageMediaTypes.Png), token);

        Assert.Equal(first.AttachmentId, second.AttachmentId);
        Assert.Matches("^sha256:[0-9a-f]{64}$", first.AttachmentId);
        Assert.Equal("capture.png", first.Name);
        Assert.Equal(3, first.Width);
        Assert.Equal(2, first.Height);
        Assert.Equal(png.Length, first.Bytes);
        var stored = await store.ReadImageAsync(first, token);
        Assert.Equal(png, stored.Data.ToArray());
        Assert.Same(first, stored.Attachment);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, GameImageMediaTypes.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg, GameImageMediaTypes.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp, GameImageMediaTypes.WebP)]
    public async Task FullyDecodesSupportedEncodedFormats(SKEncodedImageFormat format, string mediaType)
    {
        var store = new FileGameImageAttachmentStore(_root);
        var data = CreateImage(format, 4, 3);
        var token = TestContext.Current.CancellationToken;
        var attachment = await store.SaveImageAsync(new SaveGameImageAttachment(data, mediaType), token);

        Assert.Equal(mediaType, attachment.MediaType);
        Assert.Equal(4, attachment.Width);
        Assert.Equal(3, attachment.Height);
        Assert.Equal(data, (await store.ReadImageAsync(attachment, token)).Data.ToArray());
    }

    [Fact]
    public async Task FullyDecodesGif()
    {
        var store = new FileGameImageAttachmentStore(_root);
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
        var token = TestContext.Current.CancellationToken;
        var attachment = await store.SaveImageAsync(new SaveGameImageAttachment(gif, GameImageMediaTypes.Gif), token);

        Assert.Equal(1, attachment.Width);
        Assert.Equal(1, attachment.Height);
        Assert.Equal(gif, (await store.ReadImageAsync(attachment, token)).Data.ToArray());
    }

    [Fact]
    public async Task RejectsMalformedMismatchedOversizedAndPixelBombImages()
    {
        var png = CreateImage(SKEncodedImageFormat.Png, 3, 3);
        var store = new FileGameImageAttachmentStore(
            _root,
            new GameImageAttachmentLimits(
                maxImageBytes: png.Length,
                maxImagesPerMessage: 1,
                maxMessageImageBytes: png.Length,
                maxImagePixels: 4));
        var token = TestContext.Current.CancellationToken;

        await AssertCodeAsync("INVALID_IMAGE", () => store.SaveImageAsync(
            new SaveGameImageAttachment(new byte[] { 1, 2, 3 }, GameImageMediaTypes.Png), token).AsTask());
        await AssertCodeAsync("IMAGE_TYPE_MISMATCH", () => store.SaveImageAsync(
            new SaveGameImageAttachment(png, GameImageMediaTypes.Jpeg), token).AsTask());
        await AssertCodeAsync("IMAGE_TOO_LARGE", () => new FileGameImageAttachmentStore(
            _root,
            new GameImageAttachmentLimits(
                maxImageBytes: png.Length - 1,
                maxImagesPerMessage: 1,
                maxMessageImageBytes: png.Length - 1,
                maxImagePixels: 100)).SaveImageAsync(
                    new SaveGameImageAttachment(png, GameImageMediaTypes.Png), token).AsTask());
        await AssertCodeAsync("IMAGE_TOO_MANY_PIXELS", () => store.SaveImageAsync(
            new SaveGameImageAttachment(png, GameImageMediaTypes.Png), token).AsTask());
    }

    [Fact]
    public async Task ValidationDoesNotCreateStorage()
    {
        var store = new FileGameImageAttachmentStore(_root);
        await store.ValidateImageAsync(new SaveGameImageAttachment(
            CreateImage(SKEncodedImageFormat.Png, 1, 1),
            GameImageMediaTypes.Png), TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ReadsFailClosedForMissingCorruptAndMismatchedReferences()
    {
        var store = new FileGameImageAttachmentStore(_root);
        var data = CreateImage(SKEncodedImageFormat.Png, 2, 2);
        var token = TestContext.Current.CancellationToken;
        var reference = await store.SaveImageAsync(new SaveGameImageAttachment(data, GameImageMediaTypes.Png), token);
        var hash = reference.AttachmentId.Substring("sha256:".Length);
        var path = Path.Combine(_root, "objects", hash.Substring(0, 2), hash);
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }, token);

        await AssertCodeAsync("ATTACHMENT_CORRUPT", () => store.ReadImageAsync(reference, token).AsTask());
        await AssertCodeAsync("INVALID_ATTACHMENT_REF", () => store.ReadImageAsync(new GameImageAttachment(
            "opaque-but-not-content-addressed",
            GameImageMediaTypes.Png,
            1,
            1,
            1), token).AsTask());
        var missing = new GameImageAttachment(
            "sha256:" + new string('a', 64),
            GameImageMediaTypes.Png,
            1,
            1,
            1);
        await AssertCodeAsync("ATTACHMENT_NOT_FOUND", () => store.ReadImageAsync(missing, token).AsTask());
        var oversized = new GameImageAttachment(
            "sha256:" + new string('b', 64),
            GameImageMediaTypes.Png,
            store.ImageLimits.MaxImageBytes + 1,
            1,
            1);
        await AssertCodeAsync("INVALID_ATTACHMENT_REF", () => store.ReadImageAsync(oversized, token).AsTask());
    }

    [Fact]
    public async Task ConcurrentEqualWritesPublishOneVerifiedObject()
    {
        var store = new FileGameImageAttachmentStore(_root);
        var data = CreateImage(SKEncodedImageFormat.Png, 8, 8);
        var token = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => store.SaveImageAsync(new SaveGameImageAttachment(data, GameImageMediaTypes.Png), token).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Single(results.Select(value => value.AttachmentId).Distinct(StringComparer.Ordinal));
        Assert.Equal(data, (await store.ReadImageAsync(results[0], token)).Data.ToArray());
    }

    [Fact]
    public async Task CancellationIsPreserved()
    {
        var store = new FileGameImageAttachmentStore(_root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ValidateImageAsync(
            new SaveGameImageAttachment(CreateImage(SKEncodedImageFormat.Png, 1, 1), GameImageMediaTypes.Png),
            cancellation.Token).AsTask());
    }

    [Fact]
    public void PublicContractsRejectUnsupportedFormatsAndUnsafeSourceNames()
    {
        Assert.Throws<ArgumentException>(() => new GameImageAttachmentLimits(
            mediaTypes: new[] { "image/svg+xml" }));
        Assert.Throws<ArgumentException>(() => new SaveGameImageAttachment(
            new byte[] { 1 },
            GameImageMediaTypes.Png,
            "bad\nname.png"));
    }

    [Fact]
    public void BytePayloadsAreDefensivelyCopiedAndExposedReadOnly()
    {
        var source = new byte[] { 1, 2, 3 };
        var pending = new SaveGameImageAttachment(source, GameImageMediaTypes.Png);
        var stored = new StoredGameImageAttachment(
            new GameImageAttachment("sha256:" + new string('c', 64), GameImageMediaTypes.Png, 3, 1, 1),
            source);

        source[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, pending.Data.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, stored.Data.ToArray());
    }

    private static byte[] CreateImage(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }

    private static async Task AssertCodeAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<GameAttachmentException>(action);
        Assert.Equal(code, exception.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

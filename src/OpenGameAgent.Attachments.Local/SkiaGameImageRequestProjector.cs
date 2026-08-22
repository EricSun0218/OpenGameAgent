using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Attachments;
using SkiaSharp;

namespace OpenGameAgent.Attachments.Local;

/// <summary>
/// Deterministically projects immutable source attachments into a bounded model request. Newest
/// images win admission, oversized images are aspect-preservingly resized, and omitted images are
/// represented by stable text. Source objects are never modified.
/// </summary>
public sealed class SkiaGameImageRequestProjector : IGameImageRequestProjector, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<DerivedImage>>> _cache = new(StringComparer.Ordinal);
    private readonly int _maximumCacheEntries;
    private int _disposed;

    public SkiaGameImageRequestProjector(int maximumCacheEntries = 128)
    {
        if (maximumCacheEntries < 0 || maximumCacheEntries > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCacheEntries));
        }

        _maximumCacheEntries = maximumCacheEntries;
    }

    public async ValueTask<GameImageProjectionResult> ProjectAsync(
        GameImageProjectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(SkiaGameImageRequestProjector));
        }
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new Candidate[request.Images.Count];
        for (var index = 0; index < request.Images.Count; index++)
        {
            var source = request.Images[index];
            candidates[index] = await PrepareAsync(source, request.Budget.MaximumEdgePixels, cancellationToken)
                .ConfigureAwait(false);
        }

        var decisions = new GameImageProjectionDecision[candidates.Length];
        long acceptedPixels = 0;
        long acceptedBytes = 0;
        var acceptedImages = 0;
        for (var index = candidates.Length - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var pixels = checked((long)candidate.Width * candidate.Height);
            if (acceptedImages >= request.Budget.MaximumImages
                || pixels > request.Budget.MaximumTotalPixels - acceptedPixels
                || candidate.Bytes > request.Budget.MaximumEncodedBytes - acceptedBytes)
            {
                decisions[index] = new GameImageProjectionDecision(
                    candidate.Source.Ordinal,
                    candidate.Source.Image.Attachment.AttachmentId,
                    GameImageProjectionDisposition.Replaced,
                    replacementText: "[image omitted by the bounded model-request budget]",
                    transformId: "budget-replaced-v1");
                continue;
            }

            acceptedImages++;
            acceptedPixels += pixels;
            acceptedBytes += candidate.Bytes;
            decisions[index] = candidate.Derived is null
                ? new GameImageProjectionDecision(
                    candidate.Source.Ordinal,
                    candidate.Source.Image.Attachment.AttachmentId,
                    GameImageProjectionDisposition.Retained,
                    transformId: "identity-v1")
                : new GameImageProjectionDecision(
                    candidate.Source.Ordinal,
                    candidate.Source.Image.Attachment.AttachmentId,
                    GameImageProjectionDisposition.Derived,
                    candidate.Derived.Data,
                    candidate.Derived.MediaType,
                    candidate.Derived.Width,
                    candidate.Derived.Height,
                    transformId: candidate.Derived.TransformId);
        }

        return new GameImageProjectionResult(decisions);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cache.Clear();
        }
    }

    private async Task<Candidate> PrepareAsync(
        GameImageProjectionSource source,
        int maximumEdge,
        CancellationToken cancellationToken)
    {
        var attachment = source.Image.Attachment;
        if (attachment.Width <= maximumEdge && attachment.Height <= maximumEdge)
        {
            return new Candidate(source, attachment.Width, attachment.Height, attachment.Bytes, null);
        }

        var key = attachment.AttachmentId + "|fit:" + maximumEdge;
        DerivedImage derived;
        if (_maximumCacheEntries == 0)
        {
            derived = await DeriveAsync(source.Image, maximumEdge, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            TrimCache();
            var lazy = _cache.GetOrAdd(
                key,
                _ => new Lazy<Task<DerivedImage>>(
                    () => DeriveAsync(source.Image, maximumEdge, CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                derived = await WaitWithCancellationAsync(lazy.Value, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _cache.TryRemove(key, out _);
                throw;
            }
        }

        return new Candidate(source, derived.Width, derived.Height, derived.Data.Length, derived);
    }

    private void TrimCache()
    {
        while (_cache.Count >= _maximumCacheEntries)
        {
            var key = _cache.Keys.OrderBy(value => value, StringComparer.Ordinal).FirstOrDefault();
            if (key is null || !_cache.TryRemove(key, out _))
            {
                break;
            }
        }
    }

    private static Task<DerivedImage> DeriveAsync(
        StoredGameImageAttachment source,
        int maximumEdge,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var data = SKData.CreateCopy(source.Data.ToArray());
            using var codec = SKCodec.Create(data)
                ?? throw new GameAttachmentException("IMAGE_PROJECTION_FAILED", "The image could not be decoded for projection.");
            var scale = Math.Min(
                (double)maximumEdge / codec.Info.Width,
                (double)maximumEdge / codec.Info.Height);
            var width = Math.Max(1, (int)Math.Floor(codec.Info.Width * scale));
            var height = Math.Max(1, (int)Math.Floor(codec.Info.Height * scale));
            using var decoded = SKBitmap.Decode(codec)
                ?? throw new GameAttachmentException("IMAGE_PROJECTION_FAILED", "The image could not be decoded for projection.");
            using var resized = decoded.Resize(
                new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
                ?? throw new GameAttachmentException("IMAGE_PROJECTION_FAILED", "The image could not be resized for projection.");
            cancellationToken.ThrowIfCancellationRequested();
            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, 90)
                ?? throw new GameAttachmentException("IMAGE_PROJECTION_FAILED", "The projected image could not be encoded.");
            var bytes = encoded.ToArray();
            return new DerivedImage(bytes, GameImageMediaTypes.WebP, width, height, "fit-webp90-v1");
        }
        catch (GameAttachmentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GameAttachmentException(
                "IMAGE_PROJECTION_FAILED",
                "The image could not be projected for the model request.",
                exception);
        }
    }, cancellationToken);

    private static async Task<T> WaitWithCancellationAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellation);
        if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await task.ConfigureAwait(false);
    }

    private sealed class Candidate
    {
        public Candidate(
            GameImageProjectionSource source,
            int width,
            int height,
            int bytes,
            DerivedImage? derived)
        {
            Source = source;
            Width = width;
            Height = height;
            Bytes = bytes;
            Derived = derived;
        }

        public GameImageProjectionSource Source { get; }

        public int Width { get; }

        public int Height { get; }

        public int Bytes { get; }

        public DerivedImage? Derived { get; }
    }

    private sealed class DerivedImage
    {
        public DerivedImage(byte[] data, string mediaType, int width, int height, string transformId)
        {
            Data = data;
            MediaType = mediaType;
            Width = width;
            Height = height;
            TransformId = transformId;
        }

        public byte[] Data { get; }

        public string MediaType { get; }

        public int Width { get; }

        public int Height { get; }

        public string TransformId { get; }
    }
}

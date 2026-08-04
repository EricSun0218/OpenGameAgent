using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GameAgent.Generation;

public sealed class ArtifactAuthorization
{
    public string Scheme { get; set; } = "Bearer";

    public string Parameter { get; set; } = string.Empty;
}

public interface IArtifactAuthorizationProvider
{
    ValueTask<ArtifactAuthorization?> ResolveAsync(
        string reference,
        CancellationToken cancellationToken);
}

public sealed class FileGenerationArtifactStoreOptions
{
    public string RootDirectory { get; set; } = string.Empty;

    public long MaxArtifactBytes { get; set; } = 512L * 1024 * 1024;

    public IReadOnlyList<string> AllowedRemoteHosts { get; set; } =
        Array.Empty<string>();

    public bool AllowLoopbackHttp { get; set; }

    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootDirectory))
        {
            throw new ArgumentException(
                "An artifact root directory is required.",
                nameof(RootDirectory));
        }

        if (MaxArtifactBytes is < 1 or > 4L * 1024 * 1024 * 1024
            || DownloadTimeout < TimeSpan.FromSeconds(1)
            || DownloadTimeout > TimeSpan.FromHours(1)
            || AllowedRemoteHosts is null
            || AllowedRemoteHosts.Count > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FileGenerationArtifactStoreOptions),
                "Artifact store limits are outside supported bounds.");
        }

        foreach (var host in AllowedRemoteHosts)
        {
            if (string.IsNullOrWhiteSpace(host)
                || host.Length > 253
                || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                throw new ArgumentException(
                    $"Artifact host '{host}' is invalid.",
                    nameof(AllowedRemoteHosts));
            }
        }
    }
}

public sealed class FileGenerationArtifactStore : IGenerationArtifactStore, IDisposable
{
    private readonly FileGenerationArtifactStoreOptions _options;
    private readonly HashSet<string> _allowedHosts;
    private readonly IArtifactAuthorizationProvider? _authorization;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _root;

    public FileGenerationArtifactStore(
        FileGenerationArtifactStoreOptions options,
        IArtifactAuthorizationProvider? authorization = null,
        HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _root = Path.GetFullPath(_options.RootDirectory);
        Directory.CreateDirectory(_root);
        _allowedHosts = new HashSet<string>(
            _options.AllowedRemoteHosts,
            StringComparer.OrdinalIgnoreCase);
        _authorization = authorization;
        if (httpClient is null)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip
                                         | DecompressionMethods.Deflate
            };
            _httpClient = new HttpClient(handler, disposeHandler: true);
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    public async ValueTask<GenerationArtifact> ImportAsync(
        string operationId,
        int ordinal,
        GenerationArtifactSource source,
        CancellationToken cancellationToken)
    {
        GenerationValidation.Identifier(operationId, nameof(operationId), 128);
        if (ordinal is < 0 or > 1_023)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        ValidateSource(source);
        if (source.SizeBytes > _options.MaxArtifactBytes)
        {
            throw TooLarge();
        }

        var temporary = Path.Combine(
            _root,
            ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var destination = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             useAsync: true))
            {
                if (!source.InlineData.IsEmpty)
                {
                    if (source.InlineData.Length > _options.MaxArtifactBytes)
                    {
                        throw TooLarge();
                    }

                    await destination
                        .WriteAsync(source.InlineData, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await DownloadAsync(source, destination, cancellationToken)
                        .ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(temporary);
            if (info.Length == 0 || info.Length > _options.MaxArtifactBytes)
            {
                throw TooLarge();
            }

            if (source.SizeBytes.HasValue && source.SizeBytes.Value != info.Length)
            {
                throw new GenerationOperationException(
                    "generation_artifact_size_mismatch",
                    "The generated artifact size did not match provider metadata.");
            }

            var sha256 = await ComputeSha256Async(temporary, cancellationToken)
                .ConfigureAwait(false);
            if (source.Sha256 is not null
                && !FixedTimeEquals(source.Sha256, sha256))
            {
                throw new GenerationOperationException(
                    "generation_artifact_digest_mismatch",
                    "The generated artifact digest did not match provider metadata.");
            }

            await ValidateMagicAsync(temporary, source.MediaType, cancellationToken)
                .ConfigureAwait(false);
            var extension = ExtensionFor(source.MediaType);
            var targetDirectory = Path.Combine(_root, sha256[..2]);
            Directory.CreateDirectory(targetDirectory);
            var target = Path.Combine(targetDirectory, sha256 + extension);
            try
            {
                File.Move(temporary, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                File.Delete(temporary);
            }

            return new GenerationArtifact
            {
                ArtifactId = operationId + ":" + ordinal.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Uri = new Uri(target).AbsoluteUri,
                MediaType = source.MediaType,
                Sha256 = sha256,
                SizeBytes = info.Length,
                FileName = NormalizeFileName(source.FileName),
                SourceExpiresAt = source.ExpiresAt
            };
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    public async ValueTask VerifyAsync(
        GenerationArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        if (artifact is null)
        {
            throw new ArgumentNullException(nameof(artifact));
        }

        if (!Uri.TryCreate(artifact.Uri, UriKind.Absolute, out var uri)
            || !uri.IsFile
            || string.IsNullOrWhiteSpace(artifact.Sha256)
            || artifact.Sha256.Length != 64
            || artifact.SizeBytes < 1)
        {
            throw new GenerationOperationException(
                "generation_artifact_record_invalid",
                "The generated artifact record is invalid.");
        }

        var path = Path.GetFullPath(uri.LocalPath);
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar.ToString(),
            StringComparison.Ordinal)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison) || !File.Exists(path))
        {
            throw new GenerationOperationException(
                "generation_artifact_unavailable",
                "The generated artifact is missing or outside its artifact root.");
        }

        var info = new FileInfo(path);
        if (info.Length != artifact.SizeBytes)
        {
            throw new GenerationOperationException(
                "generation_artifact_integrity_mismatch",
                "The generated artifact size changed after import.");
        }

        var digest = await ComputeSha256Async(path, cancellationToken)
            .ConfigureAwait(false);
        if (!FixedTimeEquals(artifact.Sha256, digest))
        {
            throw new GenerationOperationException(
                "generation_artifact_integrity_mismatch",
                "The generated artifact digest changed after import.");
        }

        await ValidateMagicAsync(path, artifact.MediaType, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task DownloadAsync(
        GenerationArtifactSource source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var uri = source.RemoteUri!;
        ValidateRemoteUri(uri);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (source.AuthorizationReference is not null)
        {
            if (_authorization is null)
            {
                throw new GenerationOperationException(
                    "generation_artifact_authorization_unavailable",
                    "The artifact requires authorization but no resolver is configured.");
            }

            var authorization = await _authorization
                .ResolveAsync(source.AuthorizationReference, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new GenerationOperationException(
                    "generation_artifact_authorization_unavailable",
                    "The artifact authorization reference could not be resolved.");
            if (authorization.Parameter.Length is < 1 or > 16_384
                || authorization.Parameter.Any(char.IsControl)
                || authorization.Scheme.Length is < 1 or > 64
                || authorization.Scheme.Any(character =>
                    !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            {
                throw new GenerationOperationException(
                    "generation_artifact_authorization_invalid",
                    "The resolved artifact authorization value is invalid.");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue(
                authorization.Scheme,
                authorization.Parameter);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.DownloadTimeout);
        using var response = await _httpClient
            .SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new GenerationOperationException(
                "generation_artifact_redirect_rejected",
                "Artifact redirects are disabled; allow the final host explicitly.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new GenerationOperationException(
                "generation_artifact_download_failed",
                $"Artifact download failed with HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > 0
            && response.Content.Headers.ContentLength > _options.MaxArtifactBytes)
        {
            throw TooLarge();
        }

        var responseType = response.Content.Headers.ContentType?.MediaType;
        if (responseType is not null
            && source.MediaType != "application/octet-stream"
            && !string.Equals(responseType, source.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new GenerationOperationException(
                "generation_artifact_media_type_mismatch",
                $"Artifact response type '{responseType}' did not match '{source.MediaType}'.");
        }

        await using var body = await response.Content
            .ReadAsStreamAsync()
            .ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await body
                    .ReadAsync(buffer, 0, buffer.Length, timeout.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > _options.MaxArtifactBytes)
                {
                    throw TooLarge();
                }

                await destination
                    .WriteAsync(buffer, 0, read, timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ValidateSource(GenerationArtifactSource source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var hasInline = !source.InlineData.IsEmpty;
        if (hasInline == (source.RemoteUri is not null))
        {
            throw new GenerationOperationException(
                "generation_artifact_source_invalid",
                "An artifact must contain exactly one inline or remote source.");
        }

        if (source.MediaType.Length is < 1 or > 255
            || source.MediaType.Any(char.IsControl)
            || source.SizeBytes is < 1
            || source.ExpiresAt.HasValue && source.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new GenerationOperationException(
                "generation_artifact_metadata_invalid",
                "Artifact media type, size, or expiration metadata is invalid.");
        }

        if (source.Sha256 is not null
            && (source.Sha256.Length != 64
                || source.Sha256.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new GenerationOperationException(
                "generation_artifact_digest_invalid",
                "Artifact SHA-256 metadata must contain 64 hexadecimal characters.");
        }
    }

    private void ValidateRemoteUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new GenerationOperationException(
                "generation_artifact_uri_invalid",
                "Artifact URI must be absolute and cannot contain user-info or a fragment.");
        }

        var loopback = uri.IsLoopback
                       || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        var loopbackAllowed = loopback && _options.AllowLoopbackHttp;
        var schemeAllowed = uri.Scheme == Uri.UriSchemeHttps
                            || loopbackAllowed
                            && uri.Scheme == Uri.UriSchemeHttp;
        var hostAllowed = _allowedHosts.Contains(uri.Host) || loopbackAllowed;
        if (!schemeAllowed || !hostAllowed)
        {
            throw new GenerationOperationException(
                "generation_artifact_host_not_allowed",
                "Artifact URI scheme or host is not allowlisted.");
        }
    }

    private static async ValueTask<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            useAsync: true);
        using var algorithm = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream
                    .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                algorithm.TransformBlock(buffer, 0, read, null, 0);
            }

            algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Hex(algorithm.Hash!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask ValidateMagicAsync(
        string path,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var expected = ExpectedMagic(mediaType);
        if (expected is null)
        {
            return;
        }

        var buffer = new byte[Math.Max(expected.MinimumLength, 12)];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            useAsync: true);
        var read = await stream
            .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
            .ConfigureAwait(false);
        if (!expected.Matches(buffer, read))
        {
            throw new GenerationOperationException(
                "generation_artifact_signature_mismatch",
                $"Artifact bytes do not match declared media type '{mediaType}'.");
        }
    }

    private static MagicSignature? ExpectedMagic(string mediaType) =>
        mediaType.ToLowerInvariant() switch
        {
            "image/png" => new MagicSignature(
                8,
                (bytes, length) => length >= 8
                                   && bytes.AsSpan(0, 8).SequenceEqual(
                                       new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })),
            "image/jpeg" => new MagicSignature(
                3,
                (bytes, length) => length >= 3
                                   && bytes[0] == 0xff
                                   && bytes[1] == 0xd8
                                   && bytes[2] == 0xff),
            "audio/wav" => new MagicSignature(
                12,
                (bytes, length) => length >= 12
                                   && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                                   && Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE"),
            "audio/mpeg" => new MagicSignature(
                3,
                (bytes, length) => length >= 3
                                   && (Encoding.ASCII.GetString(bytes, 0, 3) == "ID3"
                                       || bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0)),
            "video/mp4" => new MagicSignature(
                8,
                (bytes, length) => length >= 8
                                   && Encoding.ASCII.GetString(bytes, 4, 4) == "ftyp"),
            "video/webm" => new MagicSignature(
                4,
                (bytes, length) => length >= 4
                                   && bytes[0] == 0x1a
                                   && bytes[1] == 0x45
                                   && bytes[2] == 0xdf
                                   && bytes[3] == 0xa3),
            _ => null
        };

    private static string ExtensionFor(string mediaType) =>
        mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "audio/wav" => ".wav",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "application/json" => ".json",
            _ => ".bin"
        };

    private static string? NormalizeFileName(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var name = Path.GetFileName(value);
        if (name.Length is < 1 or > 255
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new GenerationOperationException(
                "generation_artifact_filename_invalid",
                "Artifact filename is invalid.");
        }

        return name;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.ASCII.GetBytes(expected.ToLowerInvariant());
        var right = Encoding.ASCII.GetBytes(actual);
        return left.Length == right.Length
               && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string Hex(byte[] bytes)
    {
        var characters = new char[bytes.Length * 2];
        const string alphabet = "0123456789abcdef";
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = alphabet[bytes[index] >> 4];
            characters[index * 2 + 1] = alphabet[bytes[index] & 15];
        }

        return new string(characters);
    }

    private GenerationOperationException TooLarge() =>
        new(
            "generation_artifact_too_large",
            $"Artifact exceeds {_options.MaxArtifactBytes} bytes.");

    private sealed class MagicSignature
    {
        private readonly Func<byte[], int, bool> _match;

        public MagicSignature(int minimumLength, Func<byte[], int, bool> match)
        {
            MinimumLength = minimumLength;
            _match = match;
        }

        public int MinimumLength { get; }

        public bool Matches(byte[] bytes, int length) => _match(bytes, length);
    }
}

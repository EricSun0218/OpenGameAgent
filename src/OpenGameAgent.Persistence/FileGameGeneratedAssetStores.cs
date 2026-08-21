using System.Security.Cryptography;
using OpenGameAgent.Media;

namespace OpenGameAgent.Persistence;

public sealed class FileGameGeneratedAssetJobStore : IGameGeneratedAssetJobStore
{
    private const string Suffix = ".generated-asset.json";
    private readonly FileStore _files;

    public FileGameGeneratedAssetJobStore(
        string directory,
        long maximumFileBytes = 8_000_000,
        int concurrencyStripes = 64)
    {
        _files = new FileStore(directory, maximumFileBytes, concurrencyStripes);
    }

    public async ValueTask<GameGeneratedAssetJob?> LoadAsync(
        GameSessionKey owner,
        string operationId,
        CancellationToken cancellationToken)
    {
        var key = StorageKey(owner, operationId);
        var gate = _files.GateFor(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await _files.AcquireProcessLeaseAsync(key + Suffix, cancellationToken).ConfigureAwait(false);
            var document = await _files.ReadAsync<JobDocument>(
                _files.PathFor(key, Suffix),
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return null;
            }

            var job = Decode(document);
            EnsureStorageIdentity(job, owner, operationId);
            return job;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameGeneratedAssetSaveResult> SaveAsync(
        GameGeneratedAssetJob job,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (job is null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        var key = StorageKey(job.Owner, job.OperationId);
        var gate = _files.GateFor(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await _files.AcquireProcessLeaseAsync(key + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(key, Suffix);
            var document = await _files.ReadAsync<JobDocument>(path, cancellationToken).ConfigureAwait(false);
            var current = document is null ? null : Decode(document);
            if ((current?.Revision ?? 0) != expectedRevision)
            {
                if (current is null)
                {
                    throw new PersistenceException(
                        "The generated asset operation disappeared during a compare-and-swap update.");
                }

                return new GameGeneratedAssetSaveResult(false, current);
            }

            try
            {
                GeneratedAssetValidation.ValidateTransition(current, job, expectedRevision);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw new PersistenceException("The generated asset state transition is invalid.", exception);
            }

            await _files.WriteAtomicAsync(path, Encode(job), cancellationToken).ConfigureAwait(false);
            return new GameGeneratedAssetSaveResult(true, job);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string StorageKey(GameSessionKey owner, string operationId)
    {
        var checkedOwner = GeneratedAssetValidation.RequireOwner(owner, nameof(owner));
        return checkedOwner.SessionId + "\n" + checkedOwner.ActorId + "\n"
            + GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
    }

    private static void EnsureStorageIdentity(
        GameGeneratedAssetJob job,
        GameSessionKey owner,
        string operationId)
    {
        if (job.Owner != owner || !string.Equals(job.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new PersistenceException("The generated asset identity does not match its storage key.");
        }
    }

    private static JobDocument Encode(GameGeneratedAssetJob job) => new()
    {
        FormatVersion = 1,
        OperationId = job.OperationId,
        SessionId = job.Owner.SessionId,
        ActorId = job.Owner.ActorId,
        AssetType = job.AssetType,
        TimelineId = job.Moment.TimelineId,
        Tick = job.Moment.Tick,
        CalendarJson = job.Moment.CalendarJson,
        GeneratorId = job.GeneratorId,
        ModelId = job.ModelId,
        ImporterId = job.ImporterId,
        RequestFingerprint = job.RequestFingerprint,
        RequestMetadataJson = job.RequestMetadataJson,
        MediaKind = job.MediaKind,
        Revision = job.Revision,
        Status = job.Status,
        Manifest = job.Manifest is null ? null : Encode(job.Manifest),
        ImportReceipt = job.ImportReceipt is null ? null : Encode(job.ImportReceipt),
        ErrorCode = job.ErrorCode,
        ErrorMessage = job.ErrorMessage,
    };

    private static ManifestDocument Encode(GameGeneratedAssetManifest manifest) => new()
    {
        AssetId = manifest.AssetId,
        MetadataJson = manifest.MetadataJson,
        ProviderRequestId = manifest.ProviderRequestId,
        Resources = manifest.Resources.Select(static resource => new ResourceDocument
        {
            ResourceId = resource.ResourceId,
            Sha256 = resource.Sha256,
            MediaType = resource.MediaType,
            Bytes = resource.Bytes,
            Name = resource.Name,
        }).ToList(),
    };

    private static ReceiptDocument Encode(GameGeneratedAssetImportReceipt receipt) => new()
    {
        OperationId = receipt.OperationId,
        Outcome = receipt.Outcome,
        ResultJson = receipt.ResultJson,
        StateRevision = receipt.StateRevision,
        Code = receipt.Code,
        Message = receipt.Message,
    };

    private static GameGeneratedAssetJob Decode(JobDocument document)
    {
        if (document.FormatVersion != 1)
        {
            throw new PersistenceException("The generated asset document has an unsupported format.");
        }

        return FileStore.DecodeDocument(
            "generated asset document",
            () => new GameGeneratedAssetJob(
                document.OperationId,
                new GameSessionKey(document.SessionId, document.ActorId),
                document.AssetType,
                new GameMoment(document.TimelineId, document.Tick, document.CalendarJson),
                document.GeneratorId,
                document.ModelId,
                document.ImporterId,
                document.RequestFingerprint,
                document.RequestMetadataJson,
                document.MediaKind,
                document.Revision,
                document.Status,
                document.Manifest is null ? null : Decode(document.Manifest),
                document.ImportReceipt is null ? null : Decode(document.ImportReceipt),
                document.ErrorCode,
                document.ErrorMessage));
    }

    private static GameGeneratedAssetManifest Decode(ManifestDocument document) => new(
        document.AssetId,
        document.Resources.Select(static resource => new GameGeneratedAssetResource(
            resource.ResourceId,
            resource.Sha256,
            resource.MediaType,
            resource.Bytes,
            resource.Name)).ToArray(),
        document.MetadataJson,
        document.ProviderRequestId);

    private static GameGeneratedAssetImportReceipt Decode(ReceiptDocument document) => new(
        document.OperationId,
        document.Outcome,
        document.ResultJson,
        document.StateRevision,
        document.Code,
        document.Message);

    private sealed class JobDocument
    {
        public int FormatVersion { get; set; }

        public string OperationId { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string AssetType { get; set; } = string.Empty;

        public string TimelineId { get; set; } = string.Empty;

        public long Tick { get; set; }

        public string? CalendarJson { get; set; }

        public string GeneratorId { get; set; } = string.Empty;

        public string ModelId { get; set; } = string.Empty;

        public string ImporterId { get; set; } = string.Empty;

        public string RequestFingerprint { get; set; } = string.Empty;

        public string RequestMetadataJson { get; set; } = "{}";

        public GameMediaKind MediaKind { get; set; }

        public long Revision { get; set; }

        public GameGeneratedAssetStatus Status { get; set; }

        public ManifestDocument? Manifest { get; set; }

        public ReceiptDocument? ImportReceipt { get; set; }

        public string? ErrorCode { get; set; }

        public string? ErrorMessage { get; set; }
    }

    private sealed class ManifestDocument
    {
        public string AssetId { get; set; } = string.Empty;

        public List<ResourceDocument> Resources { get; set; } = new();

        public string MetadataJson { get; set; } = "{}";

        public string? ProviderRequestId { get; set; }
    }

    private sealed class ResourceDocument
    {
        public string ResourceId { get; set; } = string.Empty;

        public string Sha256 { get; set; } = string.Empty;

        public string MediaType { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public string? Name { get; set; }
    }

    private sealed class ReceiptDocument
    {
        public string OperationId { get; set; } = string.Empty;

        public GameGeneratedAssetImportOutcome Outcome { get; set; }

        public string ResultJson { get; set; } = "{}";

        public long? StateRevision { get; set; }

        public string? Code { get; set; }

        public string? Message { get; set; }
    }
}

public sealed class FileGameGeneratedAssetResourceStore : IGameGeneratedAssetResourceStore
{
    private const string Suffix = ".generated-asset.bin";
    private readonly FileStore _files;
    private readonly long _maximumResourceBytes;

    public FileGameGeneratedAssetResourceStore(
        string directory,
        long maximumResourceBytes = 100_000_000,
        int concurrencyStripes = 64)
    {
        if (maximumResourceBytes < 1 || maximumResourceBytes > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResourceBytes));
        }

        _maximumResourceBytes = maximumResourceBytes;
        _files = new FileStore(directory, maximumResourceBytes, concurrencyStripes);
    }

    public async ValueTask<GameGeneratedAssetResource> SaveAsync(
        string operationId,
        int outputIndex,
        GameGeneratedAssetBinary resource,
        CancellationToken cancellationToken)
    {
        GeneratedAssetValidation.RequireId(operationId, nameof(operationId));
        if (outputIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputIndex));
        }

        if (resource is null)
        {
            throw new ArgumentNullException(nameof(resource));
        }

        var bytes = resource.Data.ToArray();
        if (bytes.LongLength > _maximumResourceBytes)
        {
            throw new PersistenceException("The generated asset resource exceeds the configured size limit.");
        }

        var hash = GeneratedAssetValidation.Hash(bytes);
        var resourceId = "sha256-" + hash;
        var gate = _files.GateFor(resourceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await _files.AcquireProcessLeaseAsync(resourceId + Suffix, cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(resourceId, Suffix);
            if (File.Exists(path))
            {
                var existing = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
                Verify(hash, existing);
                if (!existing.AsSpan().SequenceEqual(bytes))
                {
                    throw new PersistenceException("A generated asset resource hash collision was detected.");
                }
            }
            else
            {
                await WriteAtomicAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }

            return new GameGeneratedAssetResource(
                resourceId,
                hash,
                resource.MediaType,
                bytes.Length,
                resource.Name);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GameGeneratedAssetBinary> ReadAsync(
        GameGeneratedAssetResource resource,
        CancellationToken cancellationToken)
    {
        if (resource is null)
        {
            throw new ArgumentNullException(nameof(resource));
        }

        var gate = _files.GateFor(resource.ResourceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await _files.AcquireProcessLeaseAsync(
                resource.ResourceId + Suffix,
                cancellationToken).ConfigureAwait(false);
            var path = _files.PathFor(resource.ResourceId, Suffix);
            if (!File.Exists(path))
            {
                throw new PersistenceException("The generated asset resource does not exist.");
            }

            var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            try
            {
                GeneratedAssetValidation.VerifyResource(resource, bytes);
            }
            catch (InvalidDataException exception)
            {
                throw new PersistenceException("The generated asset resource failed its integrity check.", exception);
            }

            return new GameGeneratedAssetBinary(bytes, resource.MediaType, resource.Name);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 1 || stream.Length > _maximumResourceBytes || stream.Length > int.MaxValue)
        {
            throw new PersistenceException("The generated asset resource has an invalid size.");
        }

        var bytes = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(
                bytes,
                offset,
                bytes.Length - offset,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new PersistenceException("The generated asset resource ended unexpectedly.");
            }

            offset += read;
        }

        return bytes;
    }

    private static async ValueTask WriteAtomicAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void Verify(string expectedHash, byte[] bytes)
    {
        var actual = GeneratedAssetValidation.Hash(bytes);
        if (!string.Equals(expectedHash, actual, StringComparison.Ordinal))
        {
            throw new PersistenceException("The generated asset resource failed its integrity check.");
        }
    }
}

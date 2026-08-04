using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Generation;

public sealed class FileGenerationJobStoreOptions
{
    public int MaxJobs { get; set; } = 100_000;

    public int MaxRecordBytes { get; set; } = 64 * 1024 * 1024;

    internal void Validate()
    {
        if (MaxJobs is < 1 or > 1_000_000
            || MaxRecordBytes is < 1_024 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FileGenerationJobStoreOptions));
        }
    }
}

public sealed class FileGenerationJobStore : IGenerationJobStore, IDisposable
{
    private readonly string _root;
    private readonly FileGenerationJobStoreOptions _options;
    private readonly GenerationStoreWriterLease _writerLease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public FileGenerationJobStore(
        string rootDirectory,
        FileGenerationJobStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "A generation job directory is required.",
                nameof(rootDirectory));
        }

        _options = options ?? new FileGenerationJobStoreOptions();
        _options.Validate();
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
        try
        {
            _writerLease = GenerationStoreWriterLease.Acquire(
                Path.Combine(_root, ".writer.lock"));
        }
        catch (IOException exception)
        {
            throw new GenerationOperationException(
                "generation_job_store_writer_active",
                "Another process already owns this generation job store.",
                innerException: exception);
        }
    }

    public async ValueTask<GenerationJob?> TryGetAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        GenerationValidation.Identifier(operationId, nameof(operationId), 128);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(operationId);
            if (!File.Exists(path))
            {
                return null;
            }

            return await ReadAsync(path, operationId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask PutAsync(
        GenerationJob job,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var snapshot = GenerationValidation.SnapshotJob(job);
        GenerationValidation.Identifier(
            snapshot.OperationId,
            nameof(job),
            128);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(snapshot.OperationId);
            if (File.Exists(path))
            {
                var current = await ReadAsync(
                        path,
                        snapshot.OperationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot.Revision != checked(current.Revision + 1))
                {
                    throw new GenerationOperationException(
                        "generation_revision_conflict",
                        "The generation job update is stale.");
                }
            }
            else if (snapshot.Revision != 1)
            {
                throw new GenerationOperationException(
                    "generation_revision_conflict",
                    "A new generation job must start at revision one.");
            }
            else if (Directory.EnumerateFiles(_root, "*.job.json").Take(
                         _options.MaxJobs).Count() >= _options.MaxJobs)
            {
                throw new GenerationOperationException(
                    "generation_job_capacity_exceeded",
                    $"The job store reached its {_options.MaxJobs} job limit.");
            }

            var bytes = GenerationJobCodec.Serialize(snapshot);
            if (bytes.Length > _options.MaxRecordBytes)
            {
                throw new GenerationOperationException(
                    "generation_job_record_too_large",
                    $"The job record exceeds {_options.MaxRecordBytes} bytes.");
            }

            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 useAsync: true))
                {
                    await stream.WriteAsync(bytes, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<GenerationJob>> ListUnfinishedAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (maximumCount < 1 || maximumCount > _options.MaxJobs)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = new List<GenerationJob>();
            var scanned = 0;
            foreach (var path in Directory
                         .EnumerateFiles(_root, "*.job.json")
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                if (scanned > _options.MaxJobs)
                {
                    throw new GenerationOperationException(
                        "generation_job_capacity_exceeded",
                        "The job directory contains more records than configured.");
                }

                var job = await ReadAsync(path, null, cancellationToken)
                    .ConfigureAwait(false);
                if (!GenerationJobStatuses.IsTerminal(job.Status))
                {
                    jobs.Add(job);
                }
            }

            IReadOnlyList<GenerationJob> result = new ReadOnlyCollection<GenerationJob>(
                jobs.OrderBy(job => job.CreatedAt)
                    .ThenBy(job => job.OperationId, StringComparer.Ordinal)
                    .Take(maximumCount)
                    .Select(GenerationValidation.SnapshotJob)
                    .ToArray());
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _writerLease.Dispose();
        _gate.Dispose();
    }

    private async ValueTask<GenerationJob> ReadAsync(
        string path,
        string? expectedOperationId,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length is < 2 || info.Length > _options.MaxRecordBytes)
        {
            throw Corrupt(path, "record size is invalid");
        }

        var bytes = new byte[checked((int)info.Length)];
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         16 * 1024,
                         useAsync: true))
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream
                    .ReadAsync(bytes, offset, bytes.Length - offset, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw Corrupt(path, "record was truncated");
                }

                offset += read;
            }
        }

        try
        {
            var job = GenerationJobCodec.Deserialize(bytes);
            if (expectedOperationId is not null
                && !string.Equals(
                    expectedOperationId,
                    job.OperationId,
                    StringComparison.Ordinal))
            {
                throw Corrupt(path, "operation identity does not match filename");
            }

            return job;
        }
        catch (GenerationOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or FormatException
                or OverflowException)
        {
            throw new GenerationOperationException(
                "generation_job_record_corrupt",
                $"Generation job record '{Path.GetFileName(path)}' is corrupt.",
                innerException: exception);
        }
    }

    private string PathFor(string operationId)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(operationId));
        return Path.Combine(_root, Hex(hash) + ".job.json");
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

    private static GenerationOperationException Corrupt(
        string path,
        string detail) =>
        new(
            "generation_job_record_corrupt",
            $"Generation job record '{Path.GetFileName(path)}' is corrupt: {detail}.");

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(FileGenerationJobStore));
        }
    }
}

internal static class GenerationJobCodec
{
    public static byte[] Serialize(GenerationJob job)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 2);
            writer.WriteString("operationId", job.OperationId);
            writer.WriteString("requestDigest", job.RequestDigest);
            writer.WriteString("modality", job.Modality);
            writer.WriteString("provider", job.Provider);
            WriteString(writer, "providerJobId", job.ProviderJobId);
            writer.WriteString("acceptance", job.Acceptance);
            writer.WriteString("status", job.Status);
            if (job.Progress.HasValue)
            {
                writer.WriteNumber("progress", job.Progress.Value);
            }

            writer.WriteString("createdAt", job.CreatedAt);
            writer.WriteString("updatedAt", job.UpdatedAt);
            if (job.Output.HasValue)
            {
                writer.WritePropertyName("output");
                job.Output.Value.WriteTo(writer);
            }

            writer.WriteStartArray("artifacts");
            foreach (var artifact in job.Artifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("artifactId", artifact.ArtifactId);
                writer.WriteString("uri", artifact.Uri);
                writer.WriteString("mediaType", artifact.MediaType);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteNumber("sizeBytes", artifact.SizeBytes);
                WriteString(writer, "fileName", artifact.FileName);
                if (artifact.SourceExpiresAt.HasValue)
                {
                    writer.WriteString("sourceExpiresAt", artifact.SourceExpiresAt.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("pendingArtifacts");
            foreach (var source in job.PendingArtifacts)
            {
                writer.WriteStartObject();
                WriteString(writer, "remoteUri", source.RemoteUri?.OriginalString);
                if (!source.InlineData.IsEmpty)
                {
                    writer.WriteBase64String("inlineData", source.InlineData.Span);
                }
                else
                {
                    writer.WriteNull("inlineData");
                }

                writer.WriteString("mediaType", source.MediaType);
                WriteString(writer, "fileName", source.FileName);
                WriteString(writer, "sha256", source.Sha256);
                if (source.SizeBytes.HasValue)
                {
                    writer.WriteNumber("sizeBytes", source.SizeBytes.Value);
                }

                if (source.ExpiresAt.HasValue)
                {
                    writer.WriteString("expiresAt", source.ExpiresAt.Value);
                }

                WriteString(writer, "authorizationReference", source.AuthorizationReference);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteString(writer, "errorCode", job.ErrorCode);
            WriteString(writer, "errorMessage", job.ErrorMessage);
            writer.WriteBoolean("retryable", job.Retryable);
            WriteString(writer, "costUsd", job.CostUsd);
            WriteString(writer, "authorityId", job.AuthorityId);
            writer.WriteNumber("revision", job.Revision);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static GenerationJob Deserialize(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        var formatVersion = root.GetProperty("formatVersion").GetInt32();
        if (root.ValueKind != JsonValueKind.Object
            || formatVersion is not 1 and not 2)
        {
            throw new JsonException("Unsupported generation job format.");
        }

        var artifactsElement = root.GetProperty("artifacts");
        if (artifactsElement.ValueKind != JsonValueKind.Array
            || artifactsElement.GetArrayLength() > 1_024)
        {
            throw new JsonException("Invalid artifact collection.");
        }

        var artifacts = new List<GenerationArtifact>();
        foreach (var value in artifactsElement.EnumerateArray())
        {
            artifacts.Add(new GenerationArtifact
            {
                ArtifactId = RequiredString(value, "artifactId", 256),
                Uri = RequiredString(value, "uri", 4_096),
                MediaType = RequiredString(value, "mediaType", 255),
                Sha256 = RequiredString(value, "sha256", 64),
                SizeBytes = value.GetProperty("sizeBytes").GetInt64(),
                FileName = OptionalString(value, "fileName", 255),
                SourceExpiresAt = OptionalDate(value, "sourceExpiresAt")
            });
        }

        var pendingArtifacts = new List<GenerationArtifactSource>();
        if (formatVersion >= 2)
        {
            var pendingElement = root.GetProperty("pendingArtifacts");
            if (pendingElement.ValueKind != JsonValueKind.Array
                || pendingElement.GetArrayLength() > 1_024)
            {
                throw new JsonException("Invalid pending artifact collection.");
            }

            foreach (var value in pendingElement.EnumerateArray())
            {
                var remoteUri = OptionalString(value, "remoteUri", 4_096);
                var inline = value.TryGetProperty("inlineData", out var inlineElement)
                             && inlineElement.ValueKind != JsonValueKind.Null
                    ? inlineElement.GetBytesFromBase64()
                    : Array.Empty<byte>();
                pendingArtifacts.Add(new GenerationArtifactSource
                {
                    RemoteUri = remoteUri is null ? null : new Uri(remoteUri, UriKind.Absolute),
                    InlineData = inline,
                    MediaType = RequiredString(value, "mediaType", 255),
                    FileName = OptionalString(value, "fileName", 255),
                    Sha256 = OptionalString(value, "sha256", 64),
                    SizeBytes = value.TryGetProperty("sizeBytes", out var sizeBytes)
                        ? sizeBytes.GetInt64()
                        : null,
                    ExpiresAt = OptionalDate(value, "expiresAt"),
                    AuthorizationReference = OptionalString(
                        value,
                        "authorizationReference",
                        256)
                });
            }
        }

        var status = RequiredString(root, "status", 64);
        var acceptance = RequiredString(root, "acceptance", 64);
        if (!GenerationJobStatuses.IsKnown(status)
            || acceptance != GenerationAcceptance.Accepted
               && acceptance != GenerationAcceptance.NotAccepted
               && acceptance != GenerationAcceptance.Unknown)
        {
            throw new JsonException("Invalid generation job state.");
        }

        var job = new GenerationJob
        {
            OperationId = RequiredString(root, "operationId", 128),
            RequestDigest = RequiredString(root, "requestDigest", 64),
            Modality = RequiredString(root, "modality", 64),
            Provider = RequiredString(root, "provider", 128),
            ProviderJobId = OptionalString(root, "providerJobId", 256),
            Acceptance = acceptance,
            Status = status,
            Progress = OptionalDouble(root, "progress"),
            CreatedAt = root.GetProperty("createdAt").GetDateTimeOffset(),
            UpdatedAt = root.GetProperty("updatedAt").GetDateTimeOffset(),
            Output = root.TryGetProperty("output", out var output)
                ? output.Clone()
                : null,
            Artifacts = new ReadOnlyCollection<GenerationArtifact>(artifacts),
            PendingArtifacts = new ReadOnlyCollection<GenerationArtifactSource>(
                pendingArtifacts),
            ErrorCode = OptionalString(root, "errorCode", 256),
            ErrorMessage = OptionalString(root, "errorMessage", 8_192),
            Retryable = root.GetProperty("retryable").GetBoolean(),
            CostUsd = OptionalString(root, "costUsd", 128),
            AuthorityId = OptionalString(root, "authorityId", 128),
            Revision = root.GetProperty("revision").GetInt64()
        };
        GenerationValidation.ValidateJob(job);
        return job;
    }

    private static void WriteString(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string RequiredString(
        JsonElement parent,
        string name,
        int maximumLength)
    {
        var value = parent.GetProperty(name).GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
        {
            throw new JsonException($"Property '{name}' is invalid.");
        }

        return value;
    }

    private static string? OptionalString(
        JsonElement parent,
        string name,
        int maximumLength)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = value.GetString();
        if (text is null || text.Length > maximumLength)
        {
            throw new JsonException($"Property '{name}' is invalid.");
        }

        return text;
    }

    private static DateTimeOffset? OptionalDate(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null
            ? value.GetDateTimeOffset()
            : null;

    private static double? OptionalDouble(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null
            ? value.GetDouble()
            : null;
}

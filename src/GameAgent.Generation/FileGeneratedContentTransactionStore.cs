using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Generation;

public sealed class FileGeneratedContentTransactionStoreOptions
{
    public int MaxTransactions { get; set; } = 100_000;

    public int MaxRecordBytes { get; set; } = 16 * 1024 * 1024;

    public GeneratedContentLimits ContentLimits { get; set; } = new();

    internal void Validate()
    {
        if (MaxTransactions is < 1 or > 1_000_000
            || MaxRecordBytes is < 1_024 or > 64 * 1024 * 1024
            || ContentLimits is null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FileGeneratedContentTransactionStoreOptions));
        }

        ContentLimits.Validate();
    }
}

public sealed class FileGeneratedContentTransactionStore
    : IGeneratedContentTransactionStore,
      IDisposable
{
    private readonly string _root;
    private readonly FileGeneratedContentTransactionStoreOptions _options;
    private readonly GenerationStoreWriterLease _writerLease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public FileGeneratedContentTransactionStore(
        string rootDirectory,
        FileGeneratedContentTransactionStoreOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "A generated content transaction directory is required.",
                nameof(rootDirectory));
        }

        _options = options ?? new FileGeneratedContentTransactionStoreOptions();
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
                "content_transaction_store_writer_active",
                "Another process already owns this content transaction store.",
                innerException: exception);
        }
    }

    public async ValueTask<GeneratedContentTransaction?> TryGetAsync(
        string transactionId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        GenerationValidation.Identifier(transactionId, nameof(transactionId), 128);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(transactionId);
            return File.Exists(path)
                ? await ReadAsync(path, transactionId, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask PutAsync(
        GeneratedContentTransaction transaction,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var snapshot = ContentValidation.ValidateTransaction(
            transaction,
            _options.ContentLimits);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(snapshot.TransactionId);
            if (File.Exists(path))
            {
                var current = await ReadAsync(
                        path,
                        snapshot.TransactionId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot.Revision != checked(current.Revision + 1))
                {
                    throw new GenerationOperationException(
                        "content_transaction_revision_conflict",
                        "The content transaction update is stale.");
                }
            }
            else if (snapshot.Revision != 1)
            {
                throw new GenerationOperationException(
                    "content_transaction_revision_conflict",
                    "A new content transaction must start at revision one.");
            }
            else if (Directory.EnumerateFiles(_root, "*.content.json").Take(
                         _options.MaxTransactions).Count() >= _options.MaxTransactions)
            {
                throw new GenerationOperationException(
                    "content_transaction_capacity_exceeded",
                    "The content transaction store is full.");
            }

            var bytes = GeneratedContentTransactionCodec.Serialize(snapshot);
            if (bytes.Length > _options.MaxRecordBytes)
            {
                throw new GenerationOperationException(
                    "content_transaction_record_too_large",
                    $"The content transaction record exceeds {_options.MaxRecordBytes} bytes.");
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

    public async ValueTask<IReadOnlyList<GeneratedContentTransaction>> ListUnfinishedAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (maximumCount < 1 || maximumCount > _options.MaxTransactions)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transactions = new List<GeneratedContentTransaction>();
            var scanned = 0;
            foreach (var path in Directory
                         .EnumerateFiles(_root, "*.content.json")
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > _options.MaxTransactions)
                {
                    throw new GenerationOperationException(
                        "content_transaction_capacity_exceeded",
                        "The content transaction directory contains too many records.");
                }

                var transaction = await ReadAsync(path, null, cancellationToken)
                    .ConfigureAwait(false);
                if (!ContentValidation.IsTerminalState(transaction.State))
                {
                    transactions.Add(transaction);
                }
            }

            IReadOnlyList<GeneratedContentTransaction> result =
                new ReadOnlyCollection<GeneratedContentTransaction>(
                    transactions.OrderBy(value => value.CreatedAt)
                        .ThenBy(value => value.TransactionId, StringComparer.Ordinal)
                        .Take(maximumCount)
                        .Select(ContentValidation.Snapshot)
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

    private async ValueTask<GeneratedContentTransaction> ReadAsync(
        string path,
        string? expectedTransactionId,
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
                var read = await stream.ReadAsync(
                        bytes,
                        offset,
                        bytes.Length - offset,
                        cancellationToken)
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
            var transaction = GeneratedContentTransactionCodec.Deserialize(
                bytes,
                _options.ContentLimits);
            if (expectedTransactionId is not null
                && !string.Equals(
                    expectedTransactionId,
                    transaction.TransactionId,
                    StringComparison.Ordinal))
            {
                throw Corrupt(path, "transaction identity does not match filename");
            }

            return transaction;
        }
        catch (GenerationOperationException exception)
            when (exception.ReasonCode is not "content_transaction_record_corrupt")
        {
            throw new GenerationOperationException(
                "content_transaction_record_corrupt",
                $"Content transaction record '{Path.GetFileName(path)}' is corrupt.",
                innerException: exception);
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or FormatException
                or OverflowException
                or ArgumentException)
        {
            throw new GenerationOperationException(
                "content_transaction_record_corrupt",
                $"Content transaction record '{Path.GetFileName(path)}' is corrupt.",
                innerException: exception);
        }
    }

    private string PathFor(string transactionId)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(transactionId));
        return Path.Combine(_root, Hex(hash) + ".content.json");
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
            "content_transaction_record_corrupt",
            $"Content transaction record '{Path.GetFileName(path)}' is corrupt: {detail}.");

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(
                nameof(FileGeneratedContentTransactionStore));
        }
    }
}

internal static class GeneratedContentTransactionCodec
{
    public static byte[] Serialize(GeneratedContentTransaction transaction)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteString("transactionId", transaction.TransactionId);
            writer.WriteString("state", transaction.State);
            WriteOptionalString(writer, "hostReceiptId", transaction.HostReceiptId);
            if (transaction.HostResult.HasValue)
            {
                writer.WritePropertyName("hostResult");
                transaction.HostResult.Value.WriteTo(writer);
            }

            WriteOptionalString(writer, "reasonCode", transaction.ReasonCode);
            writer.WriteString("createdAt", transaction.CreatedAt);
            writer.WriteString("updatedAt", transaction.UpdatedAt);
            writer.WriteNumber("revision", transaction.Revision);
            WriteManifest(writer, transaction.Manifest);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static GeneratedContentTransaction Deserialize(
        ReadOnlySpan<byte> bytes,
        GeneratedContentLimits limits)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.GetProperty("formatVersion").GetInt32() != 1)
        {
            throw new JsonException("Unsupported content transaction format.");
        }

        var manifestElement = root.GetProperty("manifest");
        var artifacts = ReadArtifacts(manifestElement, limits.MaxArtifacts);
        var scripts = ReadScripts(manifestElement, limits.MaxScripts);
        var dependencies = ReadDependencies(
            manifestElement,
            limits.MaxDependencies);
        var provenance = ReadProvenance(
            manifestElement,
            limits.MaxProvenanceEntries);
        var manifest = new GeneratedContentManifest
        {
            ContentId = RequiredString(manifestElement, "contentId", 128),
            Kind = RequiredString(manifestElement, "kind", 128),
            Version = RequiredString(manifestElement, "version", 64),
            SourceOperationId = RequiredString(
                manifestElement,
                "sourceOperationId",
                128),
            Data = manifestElement.GetProperty("data").Clone(),
            Artifacts = new ReadOnlyCollection<GenerationArtifact>(artifacts),
            Scripts = new ReadOnlyCollection<GeneratedScriptAsset>(scripts),
            Dependencies = new ReadOnlyCollection<string>(dependencies),
            Provenance = provenance,
            Digest = RequiredString(manifestElement, "digest", 64)
        };
        var transaction = new GeneratedContentTransaction
        {
            TransactionId = RequiredString(root, "transactionId", 128),
            Manifest = manifest,
            State = RequiredString(root, "state", 64),
            HostReceiptId = OptionalString(root, "hostReceiptId", 256),
            HostResult = root.TryGetProperty("hostResult", out var result)
                ? result.Clone()
                : null,
            ReasonCode = OptionalString(root, "reasonCode", 256),
            CreatedAt = root.GetProperty("createdAt").GetDateTimeOffset(),
            UpdatedAt = root.GetProperty("updatedAt").GetDateTimeOffset(),
            Revision = root.GetProperty("revision").GetInt64()
        };
        return ContentValidation.ValidateTransaction(transaction, limits);
    }

    private static void WriteManifest(
        Utf8JsonWriter writer,
        GeneratedContentManifest manifest)
    {
        writer.WriteStartObject("manifest");
        writer.WriteString("contentId", manifest.ContentId);
        writer.WriteString("kind", manifest.Kind);
        writer.WriteString("version", manifest.Version);
        writer.WriteString("sourceOperationId", manifest.SourceOperationId);
        writer.WritePropertyName("data");
        manifest.Data.WriteTo(writer);
        writer.WriteStartArray("artifacts");
        foreach (var artifact in manifest.Artifacts)
        {
            writer.WriteStartObject();
            writer.WriteString("artifactId", artifact.ArtifactId);
            writer.WriteString("uri", artifact.Uri);
            writer.WriteString("mediaType", artifact.MediaType);
            writer.WriteString("sha256", artifact.Sha256);
            writer.WriteNumber("sizeBytes", artifact.SizeBytes);
            WriteOptionalString(writer, "fileName", artifact.FileName);
            if (artifact.SourceExpiresAt.HasValue)
            {
                writer.WriteString("sourceExpiresAt", artifact.SourceExpiresAt.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("scripts");
        foreach (var script in manifest.Scripts)
        {
            writer.WriteStartObject();
            writer.WriteString("scriptId", script.ScriptId);
            writer.WriteString("language", script.Language);
            writer.WriteString("sourceText", script.SourceText);
            WriteOptionalString(writer, "entryPoint", script.EntryPoint);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("dependencies");
        foreach (var dependency in manifest.Dependencies)
        {
            writer.WriteStringValue(dependency);
        }

        writer.WriteEndArray();
        writer.WriteStartObject("provenance");
        foreach (var pair in manifest.Provenance.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
        writer.WriteString("digest", manifest.Digest);
        writer.WriteEndObject();
    }

    private static GenerationArtifact[] ReadArtifacts(
        JsonElement manifest,
        int maximumCount)
    {
        var values = manifest.GetProperty("artifacts");
        EnsureArray(values, maximumCount, "artifacts");
        return values.EnumerateArray().Select(value => new GenerationArtifact
        {
            ArtifactId = RequiredString(value, "artifactId", 256),
            Uri = RequiredString(value, "uri", 4_096),
            MediaType = RequiredString(value, "mediaType", 255),
            Sha256 = RequiredString(value, "sha256", 64),
            SizeBytes = value.GetProperty("sizeBytes").GetInt64(),
            FileName = OptionalString(value, "fileName", 255),
            SourceExpiresAt = OptionalDate(value, "sourceExpiresAt")
        }).ToArray();
    }

    private static GeneratedScriptAsset[] ReadScripts(
        JsonElement manifest,
        int maximumCount)
    {
        var values = manifest.GetProperty("scripts");
        EnsureArray(values, maximumCount, "scripts");
        return values.EnumerateArray().Select(value => new GeneratedScriptAsset
        {
            ScriptId = RequiredString(value, "scriptId", 128),
            Language = RequiredString(value, "language", 64),
            SourceText = RequiredString(value, "sourceText", int.MaxValue),
            EntryPoint = OptionalString(value, "entryPoint", 256)
        }).ToArray();
    }

    private static string[] ReadDependencies(
        JsonElement manifest,
        int maximumCount)
    {
        var values = manifest.GetProperty("dependencies");
        EnsureArray(values, maximumCount, "dependencies");
        return values.EnumerateArray()
            .Select(value => value.GetString()
                             ?? throw new JsonException(
                                 "A dependency cannot be null."))
            .ToArray();
    }

    private static Dictionary<string, string> ReadProvenance(
        JsonElement manifest,
        int maximumCount)
    {
        var value = manifest.GetProperty("provenance");
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Provenance must be an object.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (result.Count >= maximumCount)
            {
                throw new JsonException("Provenance exceeds its collection limit.");
            }

            result.Add(
                property.Name,
                property.Value.GetString()
                ?? throw new JsonException("Provenance values must be strings."));
        }

        return result;
    }

    private static void EnsureArray(
        JsonElement value,
        int maximumCount,
        string name)
    {
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() > maximumCount)
        {
            throw new JsonException($"Property '{name}' exceeds its collection limit.");
        }
    }

    private static void WriteOptionalString(
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
}

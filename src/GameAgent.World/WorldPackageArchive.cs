using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GameAgent.World;

public interface IWorldExtensionCapabilityResolver
{
    bool IsApproved(string capabilityId, string requiredVersionRange);
}

public static class WorldPackageActivationValidator
{
    public static void ValidateRequiredExtensions(
        WorldPackageDefinition package,
        IWorldExtensionCapabilityResolver capabilities)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        if (capabilities is null)
        {
            throw new ArgumentNullException(nameof(capabilities));
        }

        foreach (var requirement in package.RequiredExtensions)
        {
            if (!capabilities.IsApproved(
                    requirement.CapabilityId,
                    requirement.VersionRange))
            {
                throw new WorldDataContractException(
                    WorldDataReasonCodes.MissingExtension,
                    "A required trusted extension is missing or unapproved.");
            }
        }
    }
}

/// <summary>
/// Reads and writes the deterministic native package archive. Entries are
/// ordered, stored without compression, and use a fixed timestamp.
/// </summary>
public static class WorldPackageArchive
{
    private static readonly DateTimeOffset StableTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Write(
        Stream destination,
        WorldPackageDefinition package,
        WorldPackageLimits? limits = null)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "Destination stream must be writable.",
                nameof(destination));
        }

        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        var effectiveLimits = limits ?? new WorldPackageLimits();
        ValidatePackage(package, effectiveLimits);
        var manifest = WorldPackageManifestCodec.WriteCanonical(package);
        if (manifest.LongLength > effectiveLimits.MaxFileBytes)
        {
            throw Error(
                WorldDataReasonCodes.ByteLimitExceeded,
                "Package manifest exceeds its byte limit.");
        }

        using var boundedDestination = new WorldBoundedArchiveWriteStream(
            destination,
            effectiveLimits.MaxCompressedBytes);
        using (var archive = new ZipArchive(
                   boundedDestination,
                   ZipArchiveMode.Create,
                   leaveOpen: true,
                   entryNameEncoding: Encoding.UTF8))
        {
            WriteEntry(
                archive,
                WorldDataContractIds.ManifestPath,
                manifest);
            foreach (var file in package.Files)
            {
                WriteEntry(archive, file.Path, file.ContentSpan);
            }
        }

        boundedDestination.Flush();
    }

    public static WorldPackageDefinition Read(
        Stream source,
        WorldPackageLimits? limits = null)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (!source.CanRead)
        {
            throw new ArgumentException(
                "Source stream must be readable.",
                nameof(source));
        }

        var effectiveLimits = limits ?? new WorldPackageLimits();
        using var boundedArchive = ReadCompressedArchive(
            source,
            effectiveLimits.MaxCompressedBytes);
        using var archive = new ZipArchive(
            boundedArchive,
            ZipArchiveMode.Read,
            leaveOpen: false,
            entryNameEncoding: Encoding.UTF8);
        if (archive.Entries.Count == 0
            || archive.Entries.Count > effectiveLimits.MaxFiles + 1)
        {
            throw Error(
                WorldDataReasonCodes.EntryLimitExceeded,
                "Archive entry count is invalid.");
        }

        var paths = new List<string>(archive.Entries.Count);
        var entries = new Dictionary<string, ZipArchiveEntry>(
            StringComparer.Ordinal);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var path = WorldArchivePath.Validate(
                entry.FullName,
                effectiveLimits.MaxPathUtf8Bytes);
            RejectNonFileEntry(entry);
            ValidateLengths(entry, effectiveLimits);
            expandedBytes = CheckedAdd(expandedBytes, entry.Length);
            if (expandedBytes > effectiveLimits.MaxExpandedBytes)
            {
                throw Error(
                    WorldDataReasonCodes.ByteLimitExceeded,
                    "Archive expanded bytes exceed their limit.");
            }

            paths.Add(path);
            if (!entries.TryAdd(path, entry))
            {
                throw Error(
                    WorldDataReasonCodes.DuplicatePath,
                    "Archive contains a duplicate path.");
            }
        }

        WorldArchivePath.EnsureUnique(paths);
        if (!entries.TryGetValue(
                WorldDataContractIds.ManifestPath,
                out var manifestEntry))
        {
            throw Error(
                WorldDataReasonCodes.ManifestMismatch,
                "Archive does not contain the native manifest.");
        }

        var manifestBytes = ReadEntry(manifestEntry, effectiveLimits);
        var manifest = WorldPackageManifestCodec.Read(
            manifestBytes,
            effectiveLimits);
        if (manifest.Files.Count != entries.Count - 1)
        {
            throw Error(
                WorldDataReasonCodes.ManifestMismatch,
                "Archive entries do not match the manifest.");
        }

        var files = new List<WorldPackageFile>(manifest.Files.Count);
        foreach (var declared in manifest.Files)
        {
            if (!entries.TryGetValue(declared.Path, out var entry)
                || string.Equals(
                    declared.Path,
                    WorldDataContractIds.ManifestPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Error(
                    WorldDataReasonCodes.ManifestMismatch,
                    "A manifest file is missing from the archive.");
            }

            var content = ReadEntry(entry, effectiveLimits);
            if (content.LongLength != declared.Length
                || !string.Equals(
                    WorldDataDigest.Compute(content),
                    declared.Digest,
                    StringComparison.Ordinal))
            {
                throw Error(
                    WorldDataReasonCodes.DigestMismatch,
                    "A package file does not match its declared digest.");
            }

            ValidateJsonIfApplicable(
                declared.Path,
                declared.MediaType,
                content,
                effectiveLimits);
            files.Add(
                new WorldPackageFile(
                    declared.Path,
                    declared.MediaType,
                    content));
        }

        var package = new WorldPackageDefinition(
            manifest.PackageId,
            manifest.ContentVersion,
            files,
            manifest.RequiredExtensions,
            manifest.ExtensionData);
        var canonicalManifest =
            WorldPackageManifestCodec.WriteCanonical(package);
        if (!manifestBytes.AsSpan().SequenceEqual(canonicalManifest))
        {
            throw Error(
                WorldDataReasonCodes.ManifestMismatch,
                "Native manifest is not in canonical form.");
        }

        return package;
    }

    private static void ValidatePackage(
        WorldPackageDefinition package,
        WorldPackageLimits limits)
    {
        if (package.Files.Count > limits.MaxFiles)
        {
            throw Error(
                WorldDataReasonCodes.EntryLimitExceeded,
                "Package exceeds its file-count limit.");
        }

        long total = 0;
        foreach (var file in package.Files)
        {
            _ = WorldArchivePath.Validate(file.Path, limits.MaxPathUtf8Bytes);
            if (file.Length > limits.MaxFileBytes)
            {
                throw Error(
                    WorldDataReasonCodes.ByteLimitExceeded,
                    "Package file exceeds its byte limit.");
            }

            total = CheckedAdd(total, file.Length);
            if (total > limits.MaxExpandedBytes)
            {
                throw Error(
                    WorldDataReasonCodes.ByteLimitExceeded,
                    "Package expanded bytes exceed their limit.");
            }

            ValidateJsonIfApplicable(
                file.Path,
                file.MediaType,
                file.ContentSpan,
                limits);
        }
    }

    private static void ValidateJsonIfApplicable(
        string path,
        string mediaType,
        ReadOnlySpan<byte> content,
        WorldPackageLimits limits)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !mediaType.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            && !mediaType.EndsWith(
                "+json",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var document =
            WorldDataJson.Parse(content, limits, nameof(content));
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        ReadOnlySpan<byte> content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = StableTimestamp;
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(content.ToArray(), 0, content.Length);
    }

    private static MemoryStream ReadCompressedArchive(
        Stream source,
        long maximumBytes)
    {
        var buffer = new byte[81_920];
        var output = new MemoryStream();
        long total = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total = CheckedAdd(total, read);
            if (total > maximumBytes)
            {
                output.Dispose();
                throw Error(
                    WorldDataReasonCodes.ByteLimitExceeded,
                    "Compressed archive exceeds its byte limit.");
            }

            output.Write(buffer, 0, read);
        }

        output.Position = 0;
        return output;
    }

    private static byte[] ReadEntry(
        ZipArchiveEntry entry,
        WorldPackageLimits limits)
    {
        if (entry.Length > limits.MaxFileBytes)
        {
            throw Error(
                WorldDataReasonCodes.ByteLimitExceeded,
                "Archive entry exceeds its byte limit.");
        }

        using var stream = entry.Open();
        using var output = new MemoryStream(
            entry.Length > int.MaxValue ? 0 : checked((int)entry.Length));
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total = CheckedAdd(total, read);
            if (total > limits.MaxFileBytes || total > entry.Length)
            {
                throw Error(
                    WorldDataReasonCodes.ByteLimitExceeded,
                    "Archive entry expanded beyond its declared limit.");
            }

            output.Write(buffer, 0, read);
        }

        if (total != entry.Length)
        {
            throw Error(
                WorldDataReasonCodes.ManifestMismatch,
                "Archive entry length is inconsistent.");
        }

        return output.ToArray();
    }

    private static void RejectNonFileEntry(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        var hasReparsePoint = (entry.ExternalAttributes & 0x0400) != 0;
        if (entry.FullName.EndsWith("/", StringComparison.Ordinal)
            || unixType == 0xa000
            || hasReparsePoint)
        {
            throw Error(
                WorldDataReasonCodes.UnsafeContent,
                "Archive links, reparse points, and directories are rejected.");
        }
    }

    private static void ValidateLengths(
        ZipArchiveEntry entry,
        WorldPackageLimits limits)
    {
        if (entry.Length < 0
            || entry.CompressedLength < 0
            || entry.Length > limits.MaxFileBytes)
        {
            throw Error(
                WorldDataReasonCodes.ByteLimitExceeded,
                "Archive entry length is invalid.");
        }

        if (entry.Length == 0)
        {
            return;
        }

        if (entry.CompressedLength == 0
            || entry.Length
            > CheckedMultiply(
                entry.CompressedLength,
                limits.MaxCompressionRatio))
        {
            throw Error(
                WorldDataReasonCodes.CompressionLimitExceeded,
                "Archive entry exceeds its compression-ratio limit.");
        }
    }

    private static long CheckedAdd(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw Error(
                WorldDataReasonCodes.ByteLimitExceeded,
                "Archive byte accounting overflowed.");
        }
    }

    private static long CheckedMultiply(long left, int right)
    {
        try
        {
            return checked(left * right);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static WorldDataContractException Error(
        string reasonCode,
        string message)
    {
        return new WorldDataContractException(reasonCode, message);
    }
}

internal sealed class WorldBoundedArchiveWriteStream : Stream
{
    private readonly Stream _destination;
    private readonly long _maximumBytes;
    private readonly string _reasonCode;
    private readonly string _limitMessage;
    private long _written;

    public WorldBoundedArchiveWriteStream(
        Stream destination,
        long maximumBytes)
        : this(
            destination,
            maximumBytes,
            WorldDataReasonCodes.CompressionLimitExceeded,
            "Native package exceeds its compressed byte limit.")
    {
    }

    public WorldBoundedArchiveWriteStream(
        Stream destination,
        long maximumBytes,
        string reasonCode,
        string limitMessage)
    {
        _destination = destination
                       ?? throw new ArgumentNullException(
                           nameof(destination));
        _maximumBytes = maximumBytes;
        _reasonCode = reasonCode;
        _limitMessage = limitMessage;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => _destination.CanWrite;

    public override long Length =>
        throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        _destination.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _destination.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
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

    public override void Write(byte[] buffer, int offset, int count)
    {
        Reserve(count);
        _destination.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Reserve(buffer.Length);
        _destination.Write(buffer);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        Reserve(count);
        return _destination.WriteAsync(
            buffer,
            offset,
            count,
            cancellationToken);
    }

    private void Reserve(int count)
    {
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
        throw new WorldDataContractException(
            _reasonCode,
            _limitMessage);
    }
}

internal sealed class WorldPackageManifest
{
    public WorldPackageManifest(
        string packageId,
        string contentVersion,
        IReadOnlyList<WorldPackageManifestFile> files,
        IReadOnlyList<WorldPackageExtensionRequirement> requiredExtensions,
        IReadOnlyDictionary<string, JsonElement> extensionData)
    {
        PackageId = packageId;
        ContentVersion = contentVersion;
        Files = files;
        RequiredExtensions = requiredExtensions;
        ExtensionData = extensionData;
    }

    public string PackageId { get; }

    public string ContentVersion { get; }

    public IReadOnlyList<WorldPackageManifestFile> Files { get; }

    public IReadOnlyList<WorldPackageExtensionRequirement>
        RequiredExtensions
    { get; }

    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; }
}

internal sealed class WorldPackageManifestFile
{
    public WorldPackageManifestFile(
        string path,
        string mediaType,
        long length,
        string digest)
    {
        Path = path;
        MediaType = mediaType;
        Length = length;
        Digest = digest;
    }

    public string Path { get; }

    public string MediaType { get; }

    public long Length { get; }

    public string Digest { get; }
}

internal static class WorldPackageManifestCodec
{
    private static readonly HashSet<string> RootFields =
        new(
            new[]
            {
                "contract",
                "packageId",
                "contentVersion",
                "files",
                "requiredExtensions",
                "extensionData"
            },
            StringComparer.Ordinal);

    private static readonly HashSet<string> FileFields =
        new(
            new[] { "path", "mediaType", "length", "digest" },
            StringComparer.Ordinal);

    private static readonly HashSet<string> ExtensionFields =
        new(
            new[] { "capabilityId", "versionRange" },
            StringComparer.Ordinal);

    public static byte[] WriteCanonical(WorldPackageDefinition package)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", WorldDataContractIds.PackageV1);
            writer.WriteString("packageId", package.PackageId);
            writer.WriteString("contentVersion", package.ContentVersion);
            writer.WritePropertyName("files");
            writer.WriteStartArray();
            foreach (var file in package.Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("mediaType", file.MediaType);
                writer.WriteString(
                    "length",
                    file.Length.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture));
                writer.WriteString("digest", file.Digest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("requiredExtensions");
            writer.WriteStartArray();
            foreach (var extension in package.RequiredExtensions)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "capabilityId",
                    extension.CapabilityId);
                writer.WriteString(
                    "versionRange",
                    extension.VersionRange);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("extensionData");
            writer.WriteStartObject();
            foreach (var pair in package.ExtensionData.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                WorldDataJson.WriteCanonical(writer, pair.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    public static WorldPackageManifest Read(
        ReadOnlySpan<byte> utf8,
        WorldPackageLimits limits)
    {
        using var document =
            WorldDataJson.Parse(utf8, limits, nameof(utf8));
        var root = document.RootElement;
        WorldDataJson.RequireOnlyProperties(root, RootFields);
        var contract = WorldDataJson.RequiredString(root, "contract", 96);
        if (!string.Equals(
                contract,
                WorldDataContractIds.PackageV1,
                StringComparison.Ordinal))
        {
            throw new WorldDataContractException(
                WorldDataReasonCodes.InvalidContract,
                "Unsupported native package contract.");
        }

        var packageId =
            WorldDataJson.RequiredString(root, "packageId", 256);
        var contentVersion =
            WorldDataJson.RequiredString(root, "contentVersion", 128);
        var files = ReadFiles(root, limits);
        var extensions = ReadExtensions(root);
        var extensionData = ReadExtensionData(root);
        return new WorldPackageManifest(
            packageId,
            contentVersion,
            files,
            extensions,
            extensionData);
    }

    private static IReadOnlyList<WorldPackageManifestFile> ReadFiles(
        JsonElement root,
        WorldPackageLimits limits)
    {
        if (!root.TryGetProperty("files", out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() > limits.MaxFiles)
        {
            throw Invalid("Manifest files collection is invalid.");
        }

        var files = new List<WorldPackageManifestFile>(
            value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            WorldDataJson.RequireOnlyProperties(item, FileFields);
            var path = WorldArchivePath.Validate(
                WorldDataJson.RequiredString(item, "path", 1_024),
                limits.MaxPathUtf8Bytes);
            var mediaType =
                WorldDataJson.RequiredString(item, "mediaType", 256);
            var length = WorldDataJson.RequiredCanonicalInt64String(
                item,
                "length",
                minimum: 0);
            var digest =
                WorldDataJson.RequiredString(item, "digest", 64);
            if (length > limits.MaxFileBytes
                || !GameAgent.Core.CanonicalJsonDigest.IsSha256(digest))
            {
                throw Invalid("Manifest file declaration is invalid.");
            }

            files.Add(
                new WorldPackageManifestFile(
                    path,
                    mediaType,
                    length,
                    digest));
        }

        var ordered = files
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        if (!files.Select(file => file.Path)
            .SequenceEqual(
                ordered.Select(file => file.Path),
                StringComparer.Ordinal))
        {
            throw Invalid("Manifest files are not canonically ordered.");
        }

        WorldArchivePath.EnsureUnique(files.Select(file => file.Path));
        return new ReadOnlyCollection<WorldPackageManifestFile>(files);
    }

    private static IReadOnlyList<WorldPackageExtensionRequirement>
        ReadExtensions(JsonElement root)
    {
        if (!root.TryGetProperty("requiredExtensions", out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() > 256)
        {
            throw Invalid(
                "Manifest extension requirements are invalid.");
        }

        var extensions = new List<WorldPackageExtensionRequirement>(
            value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            WorldDataJson.RequireOnlyProperties(item, ExtensionFields);
            extensions.Add(
                new WorldPackageExtensionRequirement(
                    WorldDataJson.RequiredString(
                        item,
                        "capabilityId",
                        256),
                    WorldDataJson.RequiredString(
                        item,
                        "versionRange",
                        128)));
        }

        var ordered = extensions
            .OrderBy(
                extension => extension.CapabilityId,
                StringComparer.Ordinal)
            .ThenBy(
                extension => extension.VersionRange,
                StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < extensions.Count; index++)
        {
            if (!string.Equals(
                    extensions[index].CapabilityId,
                    ordered[index].CapabilityId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    extensions[index].VersionRange,
                    ordered[index].VersionRange,
                    StringComparison.Ordinal))
            {
                throw Invalid(
                    "Manifest extension requirements are not ordered.");
            }
        }

        var duplicate = extensions
            .GroupBy(
                extension => extension.CapabilityId,
                StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
        if (duplicate)
        {
            throw Invalid(
                "Manifest extension capability identifiers are duplicated.");
        }

        return new ReadOnlyCollection<WorldPackageExtensionRequirement>(
            extensions);
    }

    private static IReadOnlyDictionary<string, JsonElement>
        ReadExtensionData(JsonElement root)
    {
        if (!root.TryGetProperty("extensionData", out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Manifest extension data is invalid.");
        }

        var properties = WorldValidation.MaterializeBounded(
            value.EnumerateObject(),
            256,
            () => new ArgumentException(
                "Extension data exceeds its entry limit.",
                "extensionData"));
        var inputKeys = properties.Select(property => property.Name);
        if (!inputKeys.SequenceEqual(
                inputKeys.OrderBy(key => key, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw Invalid("Manifest extension data is not ordered.");
        }

        var values = properties
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);
        var result = WorldDataJson.CopyExtensionData(
            values,
            "extensionData");
        return result;
    }

    private static WorldDataContractException Invalid(string message)
    {
        return new WorldDataContractException(
            WorldDataReasonCodes.ManifestMismatch,
            message);
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public static class WorldDataContractIds
{
    public const string PackageV1 = "game-agent.world-package.v1";

    public const string SaveV1 = "game-agent.world-save.v1";

    public const string ManifestPath = "manifest.json";
}

public static class WorldDataReasonCodes
{
    public const string InvalidContract = "world_data_invalid_contract";
    public const string InvalidPath = "world_data_invalid_path";
    public const string DuplicatePath = "world_data_duplicate_path";
    public const string EntryLimitExceeded = "world_data_entry_limit_exceeded";
    public const string ByteLimitExceeded = "world_data_byte_limit_exceeded";
    public const string CompressionLimitExceeded =
        "world_data_compression_limit_exceeded";
    public const string DigestMismatch = "world_data_digest_mismatch";
    public const string ManifestMismatch = "world_data_manifest_mismatch";
    public const string UnsafeContent = "world_data_unsafe_content";
    public const string InvalidJson = "world_data_invalid_json";
    public const string DuplicateJsonProperty =
        "world_data_duplicate_json_property";
    public const string UnknownField = "world_data_unknown_field";
    public const string MissingExtension = "world_data_missing_extension";
    public const string PackageBindingMismatch =
        "world_data_package_binding_mismatch";
}

public sealed class WorldDataContractException : Exception
{
    public WorldDataContractException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
    }

    public string ReasonCode { get; }
}

/// <summary>
/// Hard bounds used while creating or reading native world artifacts.
/// Consumer-provided values may lower, but not exceed, these defaults.
/// </summary>
public sealed class WorldPackageLimits
{
    public const int HardMaximumFiles = 4_096;
    public const int HardMaximumPathUtf8Bytes = 1_024;
    public const long HardMaximumFileBytes = 64L * 1024 * 1024;
    public const long HardMaximumExpandedBytes = 512L * 1024 * 1024;
    public const long HardMaximumCompressedBytes = 512L * 1024 * 1024;
    public const int HardMaximumCompressionRatio = 1_000;

    public WorldPackageLimits(
        int maxFiles = 512,
        int maxPathUtf8Bytes = 512,
        long maxFileBytes = 16L * 1024 * 1024,
        long maxExpandedBytes = 128L * 1024 * 1024,
        long maxCompressedBytes = 128L * 1024 * 1024,
        int maxCompressionRatio = 100,
        int maxJsonDepth = 64,
        int maxJsonNodes = 250_000,
        int maxJsonStringUtf8Bytes = 4 * 1024 * 1024,
        int maxJsonContainerItems = 100_000)
    {
        MaxFiles = InRange(
            maxFiles,
            1,
            HardMaximumFiles,
            nameof(maxFiles));
        MaxPathUtf8Bytes = InRange(
            maxPathUtf8Bytes,
            16,
            HardMaximumPathUtf8Bytes,
            nameof(maxPathUtf8Bytes));
        MaxFileBytes = InRange(
            maxFileBytes,
            1,
            HardMaximumFileBytes,
            nameof(maxFileBytes));
        MaxExpandedBytes = InRange(
            maxExpandedBytes,
            MaxFileBytes,
            HardMaximumExpandedBytes,
            nameof(maxExpandedBytes));
        MaxCompressedBytes = InRange(
            maxCompressedBytes,
            1,
            HardMaximumCompressedBytes,
            nameof(maxCompressedBytes));
        MaxCompressionRatio = InRange(
            maxCompressionRatio,
            1,
            HardMaximumCompressionRatio,
            nameof(maxCompressionRatio));
        MaxJsonDepth = InRange(maxJsonDepth, 1, 128, nameof(maxJsonDepth));
        MaxJsonNodes = InRange(
            maxJsonNodes,
            1,
            2_000_000,
            nameof(maxJsonNodes));
        MaxJsonStringUtf8Bytes = InRange(
            maxJsonStringUtf8Bytes,
            1,
            16 * 1024 * 1024,
            nameof(maxJsonStringUtf8Bytes));
        MaxJsonContainerItems = InRange(
            maxJsonContainerItems,
            1,
            1_000_000,
            nameof(maxJsonContainerItems));
    }

    public int MaxFiles { get; }

    public int MaxPathUtf8Bytes { get; }

    public long MaxFileBytes { get; }

    public long MaxExpandedBytes { get; }

    public long MaxCompressedBytes { get; }

    public int MaxCompressionRatio { get; }

    public int MaxJsonDepth { get; }

    public int MaxJsonNodes { get; }

    public int MaxJsonStringUtf8Bytes { get; }

    public int MaxJsonContainerItems { get; }

    internal JsonValueLimits CreateJsonLimits(int maximumUtf8Bytes)
    {
        return new JsonValueLimits(
            Math.Min(maximumUtf8Bytes, checked((int)MaxFileBytes)),
            MaxJsonDepth,
            MaxJsonNodes,
            MaxJsonStringUtf8Bytes,
            MaxJsonContainerItems);
    }

    private static int InRange(
        int value,
        int minimum,
        int maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    private static long InRange(
        long value,
        long minimum,
        long maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public sealed class WorldPackageExtensionRequirement
{
    public WorldPackageExtensionRequirement(
        string capabilityId,
        string versionRange)
    {
        CapabilityId = WorldValidation.Required(
            capabilityId,
            nameof(capabilityId),
            256);
        VersionRange = WorldValidation.Required(
            versionRange,
            nameof(versionRange),
            128);
    }

    public string CapabilityId { get; }

    public string VersionRange { get; }
}

/// <summary>
/// One immutable data file in a native package. Content is copied at admission.
/// </summary>
public sealed class WorldPackageFile
{
    private readonly byte[] _content;

    public WorldPackageFile(
        string path,
        string mediaType,
        ReadOnlySpan<byte> content)
    {
        Path = WorldArchivePath.Validate(
            path,
            WorldPackageLimits.HardMaximumPathUtf8Bytes);
        if (string.Equals(
                Path,
                WorldDataContractIds.ManifestPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw DataError(
                WorldDataReasonCodes.InvalidPath,
                "Package content cannot replace the native manifest.");
        }

        MediaType = WorldValidation.Required(
            mediaType,
            nameof(mediaType),
            256);
        if (content.Length > WorldPackageLimits.HardMaximumFileBytes)
        {
            throw DataError(
                WorldDataReasonCodes.ByteLimitExceeded,
                "Package file exceeds the hard byte limit.");
        }

        _content = content.ToArray();
        WorldContentSafety.RejectExecutable(Path, MediaType, _content);
        Digest = WorldDataDigest.Compute(_content);
    }

    public string Path { get; }

    public string MediaType { get; }

    public long Length => _content.LongLength;

    public string Digest { get; }

    public byte[] GetContentCopy()
    {
        return (byte[])_content.Clone();
    }

    public Stream OpenRead()
    {
        return new MemoryStream(_content, writable: false);
    }

    internal ReadOnlySpan<byte> ContentSpan => _content;

    private static WorldDataContractException DataError(
        string reasonCode,
        string message)
    {
        return new WorldDataContractException(reasonCode, message);
    }
}

/// <summary>
/// Immutable, engine-neutral authored content. It contains no live save state
/// and cannot embed executable extensions.
/// </summary>
public sealed class WorldPackageDefinition
{
    public WorldPackageDefinition(
        string packageId,
        string contentVersion,
        IEnumerable<WorldPackageFile> files,
        IEnumerable<WorldPackageExtensionRequirement>?
            requiredExtensions = null,
        IReadOnlyDictionary<string, JsonElement>? extensionData = null)
    {
        PackageId = WorldValidation.Required(
            packageId,
            nameof(packageId),
            256);
        ContentVersion = WorldValidation.Required(
            contentVersion,
            nameof(contentVersion),
            128);
        Files = CopyFiles(files);
        RequiredExtensions = CopyExtensions(requiredExtensions);
        ExtensionData = WorldDataJson.CopyExtensionData(
            extensionData,
            nameof(extensionData));
        PackageDigest = WorldDataDigest.Compute(
            WorldPackageManifestCodec.WriteCanonical(this));
    }

    public string Contract => WorldDataContractIds.PackageV1;

    public string PackageId { get; }

    public string ContentVersion { get; }

    public IReadOnlyList<WorldPackageFile> Files { get; }

    public IReadOnlyList<WorldPackageExtensionRequirement>
        RequiredExtensions
    { get; }

    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; }

    /// <summary>
    /// SHA-256 of the canonical manifest. Saves bind this exact value.
    /// </summary>
    public string PackageDigest { get; }

    private static IReadOnlyList<WorldPackageFile> CopyFiles(
        IEnumerable<WorldPackageFile> files)
    {
        if (files is null)
        {
            throw new ArgumentNullException(nameof(files));
        }

        var copy = WorldValidation.MaterializeBounded(
                files,
                WorldPackageLimits.HardMaximumFiles,
                () => new WorldDataContractException(
                    WorldDataReasonCodes.EntryLimitExceeded,
                    "Package exceeds the hard file-count limit."))
            .Select(
                file => file
                        ?? throw new ArgumentException(
                            "Files cannot contain null entries.",
                            nameof(files)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        WorldArchivePath.EnsureUnique(copy.Select(file => file.Path));
        return new ReadOnlyCollection<WorldPackageFile>(copy);
    }

    private static IReadOnlyList<WorldPackageExtensionRequirement>
        CopyExtensions(
            IEnumerable<WorldPackageExtensionRequirement>? extensions)
    {
        if (extensions is null)
        {
            return Array.Empty<WorldPackageExtensionRequirement>();
        }

        var copy = WorldValidation.MaterializeBounded(
                extensions,
                256,
                nameof(extensions))
            .Select(
                extension => extension
                             ?? throw new ArgumentException(
                                 "Extensions cannot contain null entries.",
                                 nameof(extensions)))
            .OrderBy(
                extension => extension.CapabilityId,
                StringComparer.Ordinal)
            .ThenBy(
                extension => extension.VersionRange,
                StringComparer.Ordinal)
            .ToArray();

        for (var index = 1; index < copy.Length; index++)
        {
            if (string.Equals(
                    copy[index - 1].CapabilityId,
                    copy[index].CapabilityId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Extension capability identifiers must be unique.",
                    nameof(extensions));
            }
        }

        return new ReadOnlyCollection<WorldPackageExtensionRequirement>(copy);
    }
}

internal static class WorldDataDigest
{
    public static string Compute(ReadOnlySpan<byte> content)
    {
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(content.ToArray());
        var result = new StringBuilder(digest.Length * 2);
        foreach (var value in digest)
        {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }
}

using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAgent.Core;
using GameAgent.Protocol;

namespace GameAgent.Persistence;

public enum SkillPackageSourceTrust
{
    Untrusted,
    Trusted,
    Builtin
}

public static class SkillPackageDiagnosticCodes
{
    public const string AggregateBytesExceeded =
        "skill_package_aggregate_bytes_exceeded";
    public const string DeclaredTrustIgnored =
        "skill_package_declared_trust_ignored";
    public const string DiagnosticCountExceeded =
        "skill_package_diagnostic_count_exceeded";
    public const string DirectoryDepthExceeded =
        "skill_package_directory_depth_exceeded";
    public const string DuplicateResource =
        "skill_package_resource_duplicate";
    public const string EntryCountExceeded =
        "skill_package_entry_count_exceeded";
    public const string FileBytesExceeded =
        "skill_package_file_bytes_exceeded";
    public const string FileIdentityChanged =
        "skill_package_file_identity_changed";
    public const string FileIdentityUnavailable =
        "skill_package_file_identity_unavailable";
    public const string ImportFailed =
        "skill_package_import_failed";
    public const string JsonInvalid =
        "skill_package_json_invalid";
    public const string LinkRejected =
        "skill_package_link_rejected";
    public const string ManifestCountExceeded =
        "skill_package_manifest_count_exceeded";
    public const string MediaTypeUnsupported =
        "skill_package_media_type_unsupported";
    public const string PathBytesExceeded =
        "skill_package_path_bytes_exceeded";
    public const string PathCollision =
        "skill_package_path_collision";
    public const string PathEscapesRoot =
        "skill_package_path_escapes_root";
    public const string PathInvalid =
        "skill_package_path_invalid";
    public const string PathUnavailable =
        "skill_package_path_unavailable";
    public const string PlatformUnsupported =
        "skill_package_platform_unsupported";
    public const string RegistryReplaceFailed =
        "skill_package_registry_replace_failed";
    public const string ResourceCountExceeded =
        "skill_package_resource_count_exceeded";
    public const string ResourceDigestInvalid =
        "skill_package_resource_digest_invalid";
    public const string ResourceDigestMismatch =
        "skill_package_resource_digest_mismatch";
    public const string ResourceSizeMismatch =
        "skill_package_resource_size_mismatch";
    public const string RootUnavailable =
        "skill_package_root_unavailable";
    public const string StrictUtf8Required =
        "skill_package_strict_utf8_required";
}

public sealed class LocalSkillPackageSource
{
    public LocalSkillPackageSource(
        string sourceId,
        string rootPath,
        SkillPackageSourceTrust trust = SkillPackageSourceTrust.Untrusted)
    {
        SourceId = RuntimeGuard.RequiredUtf8(
            sourceId,
            128,
            nameof(sourceId));
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException(
                "A skill-package root path is required.",
                nameof(rootPath));
        }

        RootPath = Path.GetFullPath(rootPath);
        Trust = trust;
        EffectiveTrust = trust switch
        {
            SkillPackageSourceTrust.Untrusted => "untrusted",
            SkillPackageSourceTrust.Trusted => "trusted",
            SkillPackageSourceTrust.Builtin => "builtin",
            _ => throw new ArgumentOutOfRangeException(nameof(trust))
        };
    }

    public string SourceId { get; }

    public string RootPath { get; }

    public SkillPackageSourceTrust Trust { get; }

    internal string EffectiveTrust { get; }
}

public sealed class LocalSkillPackageOptions
{
    public int MaxSources { get; set; } = 32;

    public int MaxScannedEntries { get; set; } = 4_096;

    public int MaxDirectoryDepth { get; set; } = 8;

    public int MaxPackages { get; set; } = 512;

    public int MaxManifestBytes { get; set; } = 1_048_576;

    public int MaxResourcesPerPackage { get; set; } = 256;

    public int MaxResourceBytes { get; set; } = 262_144;

    public int MaxResourceJsonDepth { get; set; } = 32;

    public int MaxResourceJsonNodes { get; set; } = 8_192;

    public long MaxAggregateBytes { get; set; } = 32L * 1_048_576;

    public int MaxRelativePathUtf8Bytes { get; set; } = 1_024;

    public int MaxDiagnostics { get; set; } = 1_024;

    internal LocalSkillPackageOptions Snapshot()
    {
        if (MaxSources is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSources));
        }

        if (MaxScannedEntries is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxScannedEntries));
        }

        if (MaxDirectoryDepth is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDirectoryDepth));
        }

        if (MaxPackages is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPackages));
        }

        if (MaxManifestBytes is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxManifestBytes));
        }

        if (MaxResourcesPerPackage is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResourcesPerPackage));
        }

        if (MaxResourceBytes is < 1
            or > CanonicalJsonDigest.MaximumUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResourceBytes));
        }

        if (MaxResourceJsonDepth is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResourceJsonDepth));
        }

        if (MaxResourceJsonNodes is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResourceJsonNodes));
        }

        if (MaxAggregateBytes < Math.Max(
                MaxManifestBytes,
                MaxResourceBytes)
            || MaxAggregateBytes > 512L * 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAggregateBytes));
        }

        if (MaxRelativePathUtf8Bytes is < 16 or > 8_192)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRelativePathUtf8Bytes));
        }

        if (MaxDiagnostics is < 2 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDiagnostics));
        }

        return new LocalSkillPackageOptions
        {
            MaxSources = MaxSources,
            MaxScannedEntries = MaxScannedEntries,
            MaxDirectoryDepth = MaxDirectoryDepth,
            MaxPackages = MaxPackages,
            MaxManifestBytes = MaxManifestBytes,
            MaxResourcesPerPackage = MaxResourcesPerPackage,
            MaxResourceBytes = MaxResourceBytes,
            MaxResourceJsonDepth = MaxResourceJsonDepth,
            MaxResourceJsonNodes = MaxResourceJsonNodes,
            MaxAggregateBytes = MaxAggregateBytes,
            MaxRelativePathUtf8Bytes = MaxRelativePathUtf8Bytes,
            MaxDiagnostics = MaxDiagnostics
        };
    }
}

public sealed class SkillPackageDiagnostic
{
    internal SkillPackageDiagnostic(
        string sourceId,
        string? relativePath,
        string severity,
        string code,
        string message)
    {
        SourceId = sourceId;
        RelativePath = relativePath;
        Severity = severity;
        Code = code;
        Message = message;
    }

    public string SourceId { get; }

    public string? RelativePath { get; }

    public string Severity { get; }

    public string Code { get; }

    public string Message { get; }
}

public sealed class LocalSkillPackageInfo
{
    internal LocalSkillPackageInfo(
        string sourceId,
        string manifestPath,
        string skillId,
        string version,
        string effectiveTrust,
        string manifestFileDigest,
        string sourceDigest,
        string skillContentDigest)
    {
        SourceId = sourceId;
        ManifestPath = manifestPath;
        SkillId = skillId;
        Version = version;
        EffectiveTrust = effectiveTrust;
        ManifestFileDigest = manifestFileDigest;
        SourceDigest = sourceDigest;
        SkillContentDigest = skillContentDigest;
    }

    public string SourceId { get; }

    public string ManifestPath { get; }

    public string SkillId { get; }

    public string Version { get; }

    public string EffectiveTrust { get; }

    public string ManifestFileDigest { get; }

    public string SourceDigest { get; }

    public string SkillContentDigest { get; }
}

public sealed class LocalSkillPackageReloadResult
{
    internal LocalSkillPackageReloadResult(
        bool applied,
        bool changed,
        long generation,
        string catalogDigest,
        IReadOnlyList<LocalSkillPackageInfo> packages,
        IReadOnlyList<SkillPackageDiagnostic> diagnostics)
    {
        Applied = applied;
        Changed = changed;
        Generation = generation;
        CatalogDigest = catalogDigest;
        Packages = packages;
        Diagnostics = diagnostics;
    }

    public bool Applied { get; }

    public bool Changed { get; }

    public long Generation { get; }

    public string CatalogDigest { get; }

    public IReadOnlyList<LocalSkillPackageInfo> Packages { get; }

    public IReadOnlyList<SkillPackageDiagnostic> Diagnostics { get; }

    public bool HasErrors => Diagnostics.Any(
        item => string.Equals(
            item.Severity,
            SkillDiagnosticSeverities.Error,
            StringComparison.Ordinal));
}

/// <summary>
/// Discovers inert local skill packages from explicit host roots. Source trust
/// is supplied by the host and replaces, rather than inherits, a package's
/// declaration. Reload publishes only a completely valid candidate catalog.
/// </summary>
public sealed class LocalSkillPackageCatalog : ISkillContentResolver
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly object _sync = new();
    private readonly SkillCatalogRegistry _registry;
    private readonly IReadOnlyList<LocalSkillPackageSource> _sources;
    private readonly LocalSkillPackageOptions _options;
    private readonly ILocalSkillPackageFileObserver? _observer;
    private ResolverState _resolverState = ResolverState.Empty;
    private IReadOnlyList<LocalSkillPackageInfo> _currentPackages =
        Array.Empty<LocalSkillPackageInfo>();

    public LocalSkillPackageCatalog(
        SkillCatalogRegistry registry,
        IEnumerable<LocalSkillPackageSource> sources,
        LocalSkillPackageOptions? options = null)
        : this(registry, sources, options, observer: null)
    {
    }

    internal LocalSkillPackageCatalog(
        SkillCatalogRegistry registry,
        IEnumerable<LocalSkillPackageSource> sources,
        LocalSkillPackageOptions? options,
        ILocalSkillPackageFileObserver? observer)
    {
        _registry = registry
                    ?? throw new ArgumentNullException(nameof(registry));
        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        _options = (options ?? new LocalSkillPackageOptions()).Snapshot();
        var sourceSnapshot = RuntimeInputGuard.CopyBounded(
            sources,
            _options.MaxSources,
            source => source
                      ?? throw new ArgumentException(
                          "A skill-package source cannot be null.",
                          nameof(sources)),
            nameof(sources),
            "skill_package_source_count_exceeded");
        Array.Sort(
            sourceSnapshot,
            (left, right) => string.Compare(
                left.SourceId,
                right.SourceId,
                StringComparison.Ordinal));
        if (sourceSnapshot.Length < 1)
        {
            throw new ArgumentException(
                "At least one explicit skill-package source is required.",
                nameof(sources));
        }

        var sourceIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var sourceRoots = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceSnapshot)
        {
            if (!sourceIds.Add(source.SourceId))
            {
                throw new ArgumentException(
                    "Skill-package source IDs must be unique across platforms.",
                    nameof(sources));
            }

            if (!sourceRoots.Add(source.RootPath))
            {
                throw new ArgumentException(
                    "Skill-package source roots must be unique across platforms.",
                    nameof(sources));
            }
        }

        _sources = new ReadOnlyCollection<LocalSkillPackageSource>(
            sourceSnapshot);
        _observer = observer;
    }

    public IReadOnlyList<LocalSkillPackageSource> Sources => _sources;

    public IReadOnlyList<LocalSkillPackageInfo> CurrentPackages
    {
        get
        {
            lock (_sync)
            {
                return SnapshotPackages(_currentPackages);
            }
        }
    }

    public LocalSkillPackageReloadResult Reload(
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = new DiagnosticCollector(
                _options.MaxDiagnostics);
            CandidateCatalog? candidate = null;
            try
            {
                candidate = BuildCandidate(
                    diagnostics,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (LocalSkillFileException exception)
            {
                diagnostics.Add(
                    "<catalog>",
                    relativePath: null,
                    SkillDiagnosticSeverities.Error,
                    exception.ReasonCode,
                    exception.Message);
            }
            catch (RuntimeContentLimitException exception)
            {
                diagnostics.Add(
                    "<catalog>",
                    relativePath: null,
                    SkillDiagnosticSeverities.Error,
                    exception.LimitCode,
                    "The local skill-package candidate exceeds a runtime limit.");
            }
            catch (Exception exception)
                when (exception is IOException
                      or UnauthorizedAccessException
                      or JsonException
                      or InvalidDataException
                      or ArgumentException
                      or OverflowException
                      or DecoderFallbackException)
            {
                diagnostics.Add(
                    "<catalog>",
                    relativePath: null,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.ImportFailed,
                    "The local skill-package candidate could not be loaded safely.");
            }

            if (candidate is null || diagnostics.HasErrors)
            {
                return FailedResult(diagnostics.Snapshot());
            }

            cancellationToken.ThrowIfCancellationRequested();
            var before = _registry.Current;
            var previousResolver = Volatile.Read(ref _resolverState);
            var stagedResolver = new ResolverState(
                candidate.Resources,
                previousResolver.Current);
            Volatile.Write(ref _resolverState, stagedResolver);

            SkillCatalogSnapshot applied;
            try
            {
                applied = _registry.Replace(candidate.Manifests);
            }
            catch (Exception exception)
                when (exception is ArgumentException
                      or InvalidDataException
                      or OverflowException)
            {
                Volatile.Write(ref _resolverState, previousResolver);
                diagnostics.Add(
                    "<catalog>",
                    relativePath: null,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.RegistryReplaceFailed,
                    "The validated skill-package catalog could not be published.");
                return FailedResult(diagnostics.Snapshot());
            }

            _currentPackages = SnapshotPackages(candidate.Packages);
            return new LocalSkillPackageReloadResult(
                applied: true,
                changed: applied.Generation != before.Generation,
                applied.Generation,
                applied.Digest,
                SnapshotPackages(_currentPackages),
                diagnostics.Snapshot());
        }
    }

    public ValueTask<SkillContentResolution> ResolveAsync(
        SkillContentResolutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!string.Equals(
                request.Reference.Kind,
                SkillContentReferenceKinds.Resource,
                StringComparison.Ordinal))
        {
            throw new SkillContentResolutionException(
                SkillRuntimeReasonCodes.ResolverError);
        }

        var key = ResolverKey.Create(
            request.Skill.ContentDigest,
            request.Reference);
        var state = Volatile.Read(ref _resolverState);
        if (!state.TryGet(key, out var resource))
        {
            throw new SkillContentResolutionException(
                SkillRuntimeReasonCodes.ResolverError);
        }

        return new ValueTask<SkillContentResolution>(
            resource.ToResolution());
    }

    private CandidateCatalog? BuildCandidate(
        DiagnosticCollector diagnostics,
        CancellationToken cancellationToken)
    {
        var discovered = Discover(
            diagnostics,
            cancellationToken,
            out var aggregateBytes);
        if (diagnostics.HasErrors)
        {
            return null;
        }

        PrepareEffectiveDocuments(discovered, diagnostics);
        var initialImport = Import(
            discovered,
            diagnostics,
            includeWarnings: false);
        if (initialImport is null || diagnostics.HasErrors)
        {
            return null;
        }

        var initialByReference = initialImport.Manifests.ToDictionary(
            manifest => manifest.SkillId + "@" + manifest.Version,
            StringComparer.Ordinal);
        var preparedPackages = new List<PreparedPackage>(
            discovered.Count);
        foreach (var item in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ParsedReference is null
                || !initialByReference.TryGetValue(
                    item.ParsedReference,
                    out var manifest))
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.ImportFailed,
                    "An imported skill manifest cannot be bound to its package.");
                continue;
            }

            var prepared = PreparePackage(
                item,
                manifest,
                diagnostics,
                ref aggregateBytes,
                cancellationToken);
            if (prepared is not null)
            {
                preparedPackages.Add(prepared);
                item.EffectiveJson = ProtocolJson.Serialize(
                    prepared.Manifest);
            }
        }

        if (diagnostics.HasErrors)
        {
            return null;
        }

        var finalImport = Import(
            discovered,
            diagnostics,
            includeWarnings: true);
        if (finalImport is null || diagnostics.HasErrors)
        {
            return null;
        }

        SkillCatalogSnapshot validated;
        try
        {
            validated = new SkillCatalogRegistry().Replace(
                finalImport.Manifests);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                  or InvalidDataException
                  or OverflowException)
        {
            diagnostics.Add(
                "<catalog>",
                relativePath: null,
                SkillDiagnosticSeverities.Error,
                SkillPackageDiagnosticCodes.ImportFailed,
                "The imported skill-package catalog is invalid.");
            return null;
        }

        var preparedByReference = preparedPackages.ToDictionary(
            package => package.Manifest.SkillId
                       + "@"
                       + package.Manifest.Version,
            StringComparer.Ordinal);
        var resources = new Dictionary<ResolverKey, PreparedResource>();
        var packages = new List<LocalSkillPackageInfo>();
        foreach (var entry in validated.Skills)
        {
            if (!preparedByReference.TryGetValue(
                    entry.Reference,
                    out var prepared))
            {
                diagnostics.Add(
                    "<catalog>",
                    relativePath: null,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.ImportFailed,
                    "A validated skill cannot be bound to its local package.");
                continue;
            }

            foreach (var resource in prepared.Resources)
            {
                var key = ResolverKey.Create(
                    entry.ContentDigest,
                    resource.Reference);
                if (!resources.TryAdd(key, resource))
                {
                    diagnostics.Add(
                        prepared.Source.SourceId,
                        prepared.RelativePath,
                        SkillDiagnosticSeverities.Error,
                        SkillPackageDiagnosticCodes.DuplicateResource,
                        "A local skill resource has a duplicate exact identity.");
                }
            }

            packages.Add(
                new LocalSkillPackageInfo(
                    prepared.Source.SourceId,
                    prepared.RelativePath,
                    entry.SkillId,
                    entry.Version,
                    entry.Trust,
                    prepared.ManifestFileDigest,
                    prepared.SourceDigest,
                    entry.ContentDigest));
        }

        if (diagnostics.HasErrors)
        {
            return null;
        }

        packages.Sort(ComparePackages);
        return new CandidateCatalog(
            finalImport.Manifests,
            new ReadOnlyDictionary<ResolverKey, PreparedResource>(
                resources),
            new ReadOnlyCollection<LocalSkillPackageInfo>(packages));
    }

    private List<DiscoveredManifest> Discover(
        DiagnosticCollector diagnostics,
        CancellationToken cancellationToken,
        out long aggregateBytes)
    {
        var result = new List<DiscoveredManifest>();
        var scannedEntries = 0;
        aggregateBytes = 0;
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root;
            try
            {
                root = SecureLocalSkillFiles.OpenCanonicalRoot(
                    source.RootPath);
            }
            catch (LocalSkillFileException exception)
            {
                diagnostics.Add(
                    source.SourceId,
                    relativePath: null,
                    SkillDiagnosticSeverities.Error,
                    exception.ReasonCode,
                    exception.Message);
                continue;
            }

            var pending = new Queue<DirectoryCandidate>();
            pending.Enqueue(new DirectoryCandidate(root, depth: 0));
            var portablePaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Dequeue();
                try
                {
                    SecureLocalSkillFiles.ValidateDirectory(
                        root,
                        directory.Path);
                }
                catch (LocalSkillFileException exception)
                {
                    diagnostics.Add(
                        source.SourceId,
                        SafeRelative(root, directory.Path),
                        SkillDiagnosticSeverities.Error,
                        exception.ReasonCode,
                        exception.Message);
                    continue;
                }

                string[] entries;
                try
                {
                    entries = Directory
                        .EnumerateFileSystemEntries(directory.Path)
                        .Take(checked(
                            _options.MaxScannedEntries
                            - scannedEntries
                            + 1))
                        .OrderBy(
                            path => SecureLocalSkillFiles.RelativePath(
                                root,
                                path),
                            StringComparer.Ordinal)
                        .ToArray();
                }
                catch (Exception exception)
                    when (exception is IOException
                          or UnauthorizedAccessException)
                {
                    diagnostics.Add(
                        source.SourceId,
                        SafeRelative(root, directory.Path),
                        SkillDiagnosticSeverities.Error,
                        SkillPackageDiagnosticCodes.PathUnavailable,
                        "A skill-package directory could not be enumerated.");
                    continue;
                }

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedEntries++;
                    if (scannedEntries > _options.MaxScannedEntries)
                    {
                        diagnostics.Add(
                            source.SourceId,
                            SafeRelative(root, entry),
                            SkillDiagnosticSeverities.Error,
                            SkillPackageDiagnosticCodes.EntryCountExceeded,
                            "Skill-package discovery exceeds its entry limit.");
                        return result;
                    }

                    var relative = SafeRelative(root, entry);
                    if (!ValidateRelativePathSize(
                            source,
                            relative,
                            diagnostics))
                    {
                        continue;
                    }

                    if (!portablePaths.Add(relative))
                    {
                        diagnostics.Add(
                            source.SourceId,
                            relative,
                            SkillDiagnosticSeverities.Error,
                            SkillPackageDiagnosticCodes.PathCollision,
                            "Skill-package paths collide under portable path semantics.");
                        continue;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception exception)
                        when (exception is IOException
                              or UnauthorizedAccessException)
                    {
                        diagnostics.Add(
                            source.SourceId,
                            relative,
                            SkillDiagnosticSeverities.Error,
                            SkillPackageDiagnosticCodes.PathUnavailable,
                            "A skill-package entry became unavailable.");
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(
                            source.SourceId,
                            relative,
                            SkillDiagnosticSeverities.Error,
                            SkillPackageDiagnosticCodes.LinkRejected,
                            "Skill-package links, junctions, and reparse points are rejected.");
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (directory.Depth >= _options.MaxDirectoryDepth)
                        {
                            diagnostics.Add(
                                source.SourceId,
                                relative,
                                SkillDiagnosticSeverities.Error,
                                SkillPackageDiagnosticCodes
                                    .DirectoryDepthExceeded,
                                "Skill-package discovery exceeds its directory-depth limit.");
                        }
                        else
                        {
                            pending.Enqueue(
                                new DirectoryCandidate(
                                    entry,
                                    checked(directory.Depth + 1)));
                        }

                        continue;
                    }

                    if (!string.Equals(
                            Path.GetFileName(entry),
                            "skill.json",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (result.Count >= _options.MaxPackages)
                    {
                        diagnostics.Add(
                            source.SourceId,
                            relative,
                            SkillDiagnosticSeverities.Error,
                            SkillPackageDiagnosticCodes.ManifestCountExceeded,
                            "Skill-package discovery exceeds its manifest limit.");
                        return result;
                    }

                    byte[] bytes;
                    try
                    {
                        bytes = SecureLocalSkillFiles.ReadFile(
                            root,
                            entry,
                            _options.MaxManifestBytes,
                            source.SourceId,
                            relative,
                            _observer,
                            cancellationToken);
                    }
                    catch (LocalSkillFileException exception)
                    {
                        diagnostics.Add(
                            source.SourceId,
                            relative,
                            SkillDiagnosticSeverities.Error,
                            exception.ReasonCode,
                            exception.Message);
                        continue;
                    }

                    if (!ReserveAggregate(
                            bytes.Length,
                            source,
                            relative,
                            diagnostics,
                            ref aggregateBytes))
                    {
                        return result;
                    }

                    string json;
                    try
                    {
                        json = DecodeStrictUtf8(bytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        diagnostics.Add(
                            source.SourceId,
                            relative,
                            SkillDiagnosticSeverities.Error,
                            SkillPackageDiagnosticCodes.StrictUtf8Required,
                            "A skill manifest must be strict UTF-8.");
                        continue;
                    }

                    result.Add(
                        new DiscoveredManifest(
                            source,
                            root,
                            entry,
                            relative,
                            json,
                            FileDigest(bytes)));
                }
            }
        }

        result.Sort(
            (left, right) =>
            {
                var source = StringComparer.Ordinal.Compare(
                    left.Source.SourceId,
                    right.Source.SourceId);
                return source != 0
                    ? source
                    : StringComparer.Ordinal.Compare(
                        left.RelativePath,
                        right.RelativePath);
            });
        return result;
    }

    private void PrepareEffectiveDocuments(
        IEnumerable<DiscoveredManifest> discovered,
        DiagnosticCollector diagnostics)
    {
        foreach (var item in discovered)
        {
            try
            {
                var manifest = ProtocolJson.DeserializeSkillManifest(
                    item.RawJson);
                item.DeclaredTrust = manifest.Trust;
                manifest.Trust = item.Source.EffectiveTrust;
                item.ParsedReference =
                    manifest.SkillId + "@" + manifest.Version;
                item.EffectiveJson = ProtocolJson.Serialize(manifest);
                if (!string.Equals(
                        item.DeclaredTrust,
                        item.Source.EffectiveTrust,
                        StringComparison.Ordinal))
                {
                    diagnostics.Add(
                        item.Source.SourceId,
                        item.RelativePath,
                        SkillDiagnosticSeverities.Warning,
                        SkillPackageDiagnosticCodes.DeclaredTrustIgnored,
                        "Package-declared trust was ignored; host source trust is authoritative.");
                }
            }
            catch (Exception exception)
                when (exception is JsonException
                      or ArgumentException
                      or InvalidOperationException)
            {
                item.EffectiveJson = item.RawJson;
            }
        }
    }

    private SkillImportResult? Import(
        IReadOnlyList<DiscoveredManifest> discovered,
        DiagnosticCollector diagnostics,
        bool includeWarnings)
    {
        SkillImportResult imported;
        try
        {
            var importer = new SkillManifestImporter(
                new SkillManifestImportOptions
                {
                    MaxDocuments = _options.MaxPackages,
                    MaxAggregateUtf8Bytes = _options.MaxAggregateBytes,
                    MaxRetainedManifests = _options.MaxPackages,
                    MaxRetainedDiagnostics = _options.MaxDiagnostics
                });
            imported = importer.Import(
                discovered.Select(
                    item => new SkillManifestDocument(
                        item.DocumentSourceId,
                        item.EffectiveJson)));
        }
        catch (RuntimeContentLimitException exception)
        {
            diagnostics.Add(
                "<catalog>",
                relativePath: null,
                SkillDiagnosticSeverities.Error,
                exception.LimitCode,
                "The skill manifest import exceeds its configured limit.");
            return null;
        }

        var byDocument = discovered.ToDictionary(
            item => item.DocumentSourceId,
            StringComparer.Ordinal);
        foreach (var diagnostic in imported.Diagnostics)
        {
            if (!includeWarnings
                && !string.Equals(
                    diagnostic.Severity,
                    SkillDiagnosticSeverities.Error,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (byDocument.TryGetValue(
                    diagnostic.SourceId,
                    out var item))
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    diagnostic.Severity,
                    diagnostic.Code,
                    diagnostic.Message);
            }
            else
            {
                diagnostics.Add(
                    "<catalog>",
                    relativePath: null,
                    diagnostic.Severity,
                    diagnostic.Code,
                    diagnostic.Message);
            }
        }

        return imported;
    }

    private PreparedPackage? PreparePackage(
        DiscoveredManifest item,
        SkillManifest manifest,
        DiagnosticCollector diagnostics,
        ref long aggregateBytes,
        CancellationToken cancellationToken)
    {
        if (manifest.ResourceRefs.Count > _options.MaxResourcesPerPackage)
        {
            diagnostics.Add(
                item.Source.SourceId,
                item.RelativePath,
                SkillDiagnosticSeverities.Error,
                SkillPackageDiagnosticCodes.ResourceCountExceeded,
                "A skill package exceeds its resource-count limit.");
            return null;
        }

        var packageDirectory =
            Path.GetDirectoryName(item.FullPath) ?? item.Root;
        var resourceUris = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var resources = new List<PreparedResource>();
        foreach (var reference in manifest.ResourceRefs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateResourcePath(
                    reference.Uri,
                    out var relativeResourcePath))
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.PathInvalid,
                    "A skill resource must use a portable relative package path.");
                continue;
            }

            if (Encoding.UTF8.GetByteCount(relativeResourcePath)
                > _options.MaxRelativePathUtf8Bytes)
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.PathBytesExceeded,
                    "A skill resource path exceeds its UTF-8 byte limit.");
                continue;
            }

            if (!resourceUris.Add(relativeResourcePath))
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.DuplicateResource,
                    "Skill resource paths must be unique across platforms.");
                continue;
            }

            string fullPath;
            try
            {
                fullPath = SecureLocalSkillFiles.CombineRelative(
                    packageDirectory,
                    relativeResourcePath);
                SecureLocalSkillFiles.EnsureContained(
                    item.Root,
                    fullPath);
            }
            catch (Exception exception)
                when (exception is ArgumentException
                      or NotSupportedException
                      or LocalSkillFileException)
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.PathEscapesRoot,
                    "A skill resource path escapes its package boundary.");
                continue;
            }

            var sourceRelative = SafeRelative(item.Root, fullPath);
            byte[] bytes;
            try
            {
                bytes = SecureLocalSkillFiles.ReadFile(
                    item.Root,
                    fullPath,
                    _options.MaxResourceBytes,
                    item.Source.SourceId,
                    sourceRelative,
                    _observer,
                    cancellationToken);
            }
            catch (LocalSkillFileException exception)
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    exception.ReasonCode,
                    exception.Message);
                continue;
            }

            if (!ReserveAggregate(
                    bytes.Length,
                    item.Source,
                    item.RelativePath,
                    diagnostics,
                    ref aggregateBytes))
            {
                return null;
            }

            PreparedResource? prepared;
            try
            {
                prepared = PrepareResource(
                    reference,
                    relativeResourcePath,
                    bytes);
            }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.StrictUtf8Required,
                    "A local skill resource must be strict UTF-8.");
                continue;
            }
            catch (JsonException)
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.JsonInvalid,
                    "A local JSON skill resource is invalid.");
                continue;
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    exception.Message,
                    "A local skill resource does not satisfy its content contract.");
                continue;
            }
            catch (RuntimeContentLimitException exception)
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    exception.LimitCode,
                    "A local skill resource exceeds a canonical JSON limit.");
                continue;
            }

            if (prepared.CanonicalBytes > _options.MaxResourceBytes)
            {
                diagnostics.Add(
                    item.Source.SourceId,
                    item.RelativePath,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.FileBytesExceeded,
                    "A wrapped skill resource exceeds its canonical byte limit.");
                continue;
            }

            resources.Add(prepared);
        }

        if (diagnostics.HasErrors)
        {
            return null;
        }

        var sourceDigest = "sha256:" + ComputeSourceDigest(
            item,
            manifest,
            resources);
        var finalManifest = Clone(manifest);
        finalManifest.Trust = item.Source.EffectiveTrust;
        finalManifest.Digest = sourceDigest;
        return new PreparedPackage(
            item.Source,
            item.RelativePath,
            item.ManifestFileDigest,
            sourceDigest,
            finalManifest,
            new ReadOnlyCollection<PreparedResource>(
                resources
                    .OrderBy(
                        resource => resource.Reference.Uri,
                        StringComparer.Ordinal)
                    .ToArray()));
    }

    private PreparedResource PrepareResource(
        ResourceReference reference,
        string relativePath,
        byte[] bytes)
    {
        var mediaType = NormalizeMediaType(reference.MediaType);
        if (mediaType is null
            || (!IsJsonMediaType(mediaType)
                && !mediaType.StartsWith(
                    "text/",
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                SkillPackageDiagnosticCodes.MediaTypeUnsupported);
        }

        var text = DecodeStrictUtf8(bytes);
        JsonElement wrapped;
        if (IsJsonMediaType(mediaType))
        {
            using var document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _options.MaxResourceJsonDepth
                });
            EnsureJsonNodeCount(document.RootElement);
            wrapped = WrapJson(mediaType, document.RootElement);
        }
        else
        {
            wrapped = WrapText(mediaType, text);
        }

        var digest = CanonicalJsonDigest.ComputeSha256(wrapped);
        var canonical = new StringBuilder();
        CanonicalJsonDigest.AppendCanonical(canonical, wrapped);
        var canonicalBytes = Encoding.UTF8.GetByteCount(
            canonical.ToString());
        if (reference.Digest is not null)
        {
            var declaredDigest = NormalizeDigest(reference.Digest);
            if (declaredDigest is null)
            {
                throw new InvalidDataException(
                    SkillPackageDiagnosticCodes.ResourceDigestInvalid);
            }

            if (!string.Equals(
                    declaredDigest,
                    digest,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    SkillPackageDiagnosticCodes.ResourceDigestMismatch);
            }
        }

        if (reference.SizeBytes.HasValue
            && reference.SizeBytes.Value != canonicalBytes)
        {
            throw new InvalidDataException(
                SkillPackageDiagnosticCodes.ResourceSizeMismatch);
        }

        return new PreparedResource(
            Clone(reference),
            relativePath,
            FileDigest(bytes),
            wrapped,
            digest,
            canonicalBytes);
    }

    private void EnsureJsonNodeCount(JsonElement value)
    {
        var count = 0;
        Count(value, depth: 0);
        return;

        void Count(JsonElement item, int depth)
        {
            if (depth > _options.MaxResourceJsonDepth)
            {
                throw new JsonException(
                    "A local skill resource exceeds its JSON depth limit.");
            }

            count++;
            if (count > _options.MaxResourceJsonNodes)
            {
                throw new JsonException(
                    "A local skill resource exceeds its JSON node limit.");
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in item.EnumerateObject())
                {
                    Count(property.Value, checked(depth + 1));
                }
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in item.EnumerateArray())
                {
                    Count(child, checked(depth + 1));
                }
            }
        }
    }

    private static JsonElement WrapJson(
        string mediaType,
        JsonElement content)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.skill-package-resource+json");
            writer.WriteString("mediaType", mediaType);
            writer.WritePropertyName("json");
            content.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement WrapText(string mediaType, string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contentType",
                "application/vnd.game-agent.skill-package-resource+json");
            writer.WriteString("mediaType", mediaType);
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private string ComputeSourceDigest(
        DiscoveredManifest discovered,
        SkillManifest manifest,
        IEnumerable<PreparedResource> resources)
    {
        var digest = new CanonicalDigestBuilder();
        digest.Add("type", "local_skill_package");
        digest.Add("sourceId", discovered.Source.SourceId);
        digest.Add("manifestPath", discovered.RelativePath);
        digest.Add("effectiveTrust", discovered.Source.EffectiveTrust);
        digest.Add("manifestFileDigest", discovered.ManifestFileDigest);
        var semanticManifest = SnapshotSkill(manifest);
        digest.Add(
            "manifestSemanticDigest",
            semanticManifest.ContentDigest);
        foreach (var resource in resources.OrderBy(
                     value => value.Reference.Uri,
                     StringComparer.Ordinal))
        {
            digest.Add("resourcePath", resource.RelativePath);
            digest.Add("resourceMediaType", resource.Reference.MediaType);
            digest.Add("resourceFileDigest", resource.FileDigest);
            digest.Add("resourceContentDigest", resource.ContentDigest);
            digest.Add("resourceContentBytes", resource.CanonicalBytes);
        }

        return digest.Finish();
    }

    private static SkillCatalogEntry SnapshotSkill(
        SkillManifest manifest) =>
        new SkillCatalogRegistry()
            .Replace(new[] { manifest })
            .Skills
            .Single();

    private bool ReserveAggregate(
        int bytes,
        LocalSkillPackageSource source,
        string relativePath,
        DiagnosticCollector diagnostics,
        ref long aggregate)
    {
        try
        {
            aggregate = checked(aggregate + bytes);
        }
        catch (OverflowException)
        {
            aggregate = long.MaxValue;
        }

        if (aggregate <= _options.MaxAggregateBytes)
        {
            return true;
        }

        diagnostics.Add(
            source.SourceId,
            relativePath,
            SkillDiagnosticSeverities.Error,
            SkillPackageDiagnosticCodes.AggregateBytesExceeded,
            "Skill-package files exceed their aggregate byte limit.");
        return false;
    }

    private bool ValidateRelativePathSize(
        LocalSkillPackageSource source,
        string relativePath,
        DiagnosticCollector diagnostics)
    {
        if (Encoding.UTF8.GetByteCount(relativePath)
            <= _options.MaxRelativePathUtf8Bytes)
        {
            return true;
        }

        diagnostics.Add(
            source.SourceId,
            relativePath: null,
            SkillDiagnosticSeverities.Error,
            SkillPackageDiagnosticCodes.PathBytesExceeded,
            "A skill-package relative path exceeds its UTF-8 byte limit.");
        return false;
    }

    private LocalSkillPackageReloadResult FailedResult(
        IReadOnlyList<SkillPackageDiagnostic> diagnostics)
    {
        var current = _registry.Current;
        return new LocalSkillPackageReloadResult(
            applied: false,
            changed: false,
            current.Generation,
            current.Digest,
            SnapshotPackages(_currentPackages),
            diagnostics);
    }

    private static string SafeRelative(string root, string path)
    {
        try
        {
            return SecureLocalSkillFiles.RelativePath(root, path);
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static string DecodeStrictUtf8(byte[] bytes)
    {
        var value = StrictUtf8.GetString(bytes);
        return value.Length > 0 && value[0] == '\uFEFF'
            ? value.Substring(1)
            : value;
    }

    private static string FileDigest(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return "sha256:" + ToHex(sha.ComputeHash(bytes));
    }

    private static string ToHex(IEnumerable<byte> bytes)
    {
        var result = new StringBuilder(64);
        foreach (var value in bytes)
        {
            result.Append(
                value.ToString(
                    "x2",
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static string DocumentSourceId(
        string sourceId,
        string relativePath)
    {
        using var sha = SHA256.Create();
        var bytes = StrictUtf8.GetBytes(
            sourceId + "\0" + relativePath);
        return "local-skill:sha256:" + ToHex(sha.ComputeHash(bytes));
    }

    private static bool TryValidateResourcePath(
        string value,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 4_096
            || value[0] == '/'
            || value[0] == '\\'
            || value.Contains('\\')
            || value.Contains(':')
            || value.Contains('?')
            || value.Contains('#')
            || value.Any(character => character < 0x20))
        {
            return false;
        }

        var segments = value.Split('/');
        if (segments.Any(
                segment => segment.Length == 0
                           || segment is "." or ".."
                           || segment.EndsWith(
                               ".",
                               StringComparison.Ordinal)
                           || segment.EndsWith(
                               " ",
                               StringComparison.Ordinal)
                           || segment.IndexOfAny(
                               new[] { '<', '>', '"', '|', '*', '?' })
                           >= 0
                           || IsWindowsDeviceName(segment)))
        {
            return false;
        }

        normalized = string.Join("/", segments);
        return true;
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith(
                    "LPT",
                    StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9')
        {
            return true;
        }

        return false;
    }

    private static string? NormalizeMediaType(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOf(';') >= 0)
        {
            return null;
        }

        var candidate = value.Trim();
        var separator = candidate.IndexOf('/');
        if (separator <= 0
            || separator == candidate.Length - 1
            || separator != candidate.LastIndexOf('/')
            || !candidate.Take(separator).All(IsMediaTypeToken)
            || !candidate.Skip(separator + 1).All(IsMediaTypeToken))
        {
            return null;
        }

        return candidate.ToLowerInvariant();
    }

    private static bool IsMediaTypeToken(char value) =>
        value is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9'
        or '!'
        or '#'
        or '$'
        or '&'
        or '^'
        or '_'
        or '.'
        or '+'
        or '-';

    private static bool IsJsonMediaType(string value) =>
        string.Equals(
            value,
            "application/json",
            StringComparison.Ordinal)
        || value.EndsWith("+json", StringComparison.Ordinal);

    private static string? NormalizeDigest(string value)
    {
        var candidate = value.StartsWith(
            "sha256:",
            StringComparison.Ordinal)
            ? value.Substring("sha256:".Length)
            : value;
        return CanonicalJsonDigest.IsSha256(candidate)
            ? candidate
            : null;
    }

    private static SkillManifest Clone(SkillManifest manifest) =>
        ProtocolJson.DeserializeSkillManifest(
            ProtocolJson.Serialize(manifest));

    private static ResourceReference Clone(ResourceReference reference) =>
        new()
        {
            Uri = reference.Uri,
            MediaType = reference.MediaType,
            Digest = reference.Digest,
            SizeBytes = reference.SizeBytes
        };

    private static IReadOnlyList<LocalSkillPackageInfo> SnapshotPackages(
        IEnumerable<LocalSkillPackageInfo> packages) =>
        new ReadOnlyCollection<LocalSkillPackageInfo>(
            packages
                .Select(
                    package => new LocalSkillPackageInfo(
                        package.SourceId,
                        package.ManifestPath,
                        package.SkillId,
                        package.Version,
                        package.EffectiveTrust,
                        package.ManifestFileDigest,
                        package.SourceDigest,
                        package.SkillContentDigest))
                .ToArray());

    private static int ComparePackages(
        LocalSkillPackageInfo left,
        LocalSkillPackageInfo right)
    {
        var source = StringComparer.Ordinal.Compare(
            left.SourceId,
            right.SourceId);
        if (source != 0)
        {
            return source;
        }

        var path = StringComparer.Ordinal.Compare(
            left.ManifestPath,
            right.ManifestPath);
        if (path != 0)
        {
            return path;
        }

        var skill = StringComparer.Ordinal.Compare(
            left.SkillId,
            right.SkillId);
        return skill != 0
            ? skill
            : StringComparer.Ordinal.Compare(
                left.Version,
                right.Version);
    }

    private sealed class DiscoveredManifest
    {
        public DiscoveredManifest(
            LocalSkillPackageSource source,
            string root,
            string fullPath,
            string relativePath,
            string rawJson,
            string manifestFileDigest)
        {
            Source = source;
            Root = root;
            FullPath = fullPath;
            RelativePath = relativePath;
            RawJson = rawJson;
            EffectiveJson = rawJson;
            ManifestFileDigest = manifestFileDigest;
            DocumentSourceId = LocalSkillPackageCatalog.DocumentSourceId(
                source.SourceId,
                relativePath);
        }

        public LocalSkillPackageSource Source { get; }

        public string Root { get; }

        public string FullPath { get; }

        public string RelativePath { get; }

        public string RawJson { get; }

        public string EffectiveJson { get; set; }

        public string ManifestFileDigest { get; }

        public string DocumentSourceId { get; }

        public string? DeclaredTrust { get; set; }

        public string? ParsedReference { get; set; }
    }

    private sealed class PreparedPackage
    {
        public PreparedPackage(
            LocalSkillPackageSource source,
            string relativePath,
            string manifestFileDigest,
            string sourceDigest,
            SkillManifest manifest,
            IReadOnlyList<PreparedResource> resources)
        {
            Source = source;
            RelativePath = relativePath;
            ManifestFileDigest = manifestFileDigest;
            SourceDigest = sourceDigest;
            Manifest = manifest;
            Resources = resources;
        }

        public LocalSkillPackageSource Source { get; }

        public string RelativePath { get; }

        public string ManifestFileDigest { get; }

        public string SourceDigest { get; }

        public SkillManifest Manifest { get; }

        public IReadOnlyList<PreparedResource> Resources { get; }
    }

    private sealed class PreparedResource
    {
        public PreparedResource(
            ResourceReference reference,
            string relativePath,
            string fileDigest,
            JsonElement content,
            string contentDigest,
            int canonicalBytes)
        {
            Reference = reference;
            RelativePath = relativePath;
            FileDigest = fileDigest;
            Content = content.Clone();
            ContentDigest = contentDigest;
            CanonicalBytes = canonicalBytes;
        }

        public ResourceReference Reference { get; }

        public string RelativePath { get; }

        public string FileDigest { get; }

        public JsonElement Content { get; }

        public string ContentDigest { get; }

        public int CanonicalBytes { get; }

        public SkillContentResolution ToResolution() =>
            new(
                Content,
                digest: "sha256:" + ContentDigest,
                sizeBytes: CanonicalBytes);
    }

    private sealed class CandidateCatalog
    {
        public CandidateCatalog(
            IReadOnlyList<SkillManifest> manifests,
            IReadOnlyDictionary<ResolverKey, PreparedResource> resources,
            IReadOnlyList<LocalSkillPackageInfo> packages)
        {
            Manifests = manifests;
            Resources = resources;
            Packages = packages;
        }

        public IReadOnlyList<SkillManifest> Manifests { get; }

        public IReadOnlyDictionary<ResolverKey, PreparedResource> Resources
        {
            get;
        }

        public IReadOnlyList<LocalSkillPackageInfo> Packages { get; }
    }

    private sealed class ResolverState
    {
        public static ResolverState Empty { get; } = new(
            new ReadOnlyDictionary<ResolverKey, PreparedResource>(
                new Dictionary<ResolverKey, PreparedResource>()),
            new ReadOnlyDictionary<ResolverKey, PreparedResource>(
                new Dictionary<ResolverKey, PreparedResource>()));

        public ResolverState(
            IReadOnlyDictionary<ResolverKey, PreparedResource> current,
            IReadOnlyDictionary<ResolverKey, PreparedResource> previous)
        {
            Current = current;
            Previous = previous;
        }

        public IReadOnlyDictionary<ResolverKey, PreparedResource> Current
        {
            get;
        }

        public IReadOnlyDictionary<ResolverKey, PreparedResource> Previous
        {
            get;
        }

        public bool TryGet(
            ResolverKey key,
            out PreparedResource resource) =>
            Current.TryGetValue(key, out resource!)
            || Previous.TryGetValue(key, out resource!);
    }

    private readonly struct ResolverKey : IEquatable<ResolverKey>
    {
        private ResolverKey(
            string skillDigest,
            string uri,
            string mediaType,
            string digest,
            long? sizeBytes)
        {
            SkillDigest = skillDigest;
            Uri = uri;
            MediaType = mediaType;
            Digest = digest;
            SizeBytes = sizeBytes;
        }

        private string SkillDigest { get; }

        private string Uri { get; }

        private string MediaType { get; }

        private string Digest { get; }

        private long? SizeBytes { get; }

        public static ResolverKey Create(
            string skillDigest,
            SkillContentReference reference) =>
            new(
                skillDigest,
                reference.Reference,
                reference.MediaType ?? string.Empty,
                reference.Digest ?? string.Empty,
                reference.SizeBytes);

        public static ResolverKey Create(
            string skillDigest,
            ResourceReference reference) =>
            new(
                skillDigest,
                reference.Uri,
                reference.MediaType,
                reference.Digest ?? string.Empty,
                reference.SizeBytes);

        public bool Equals(ResolverKey other) =>
            string.Equals(
                SkillDigest,
                other.SkillDigest,
                StringComparison.Ordinal)
            && string.Equals(Uri, other.Uri, StringComparison.Ordinal)
            && string.Equals(
                MediaType,
                other.MediaType,
                StringComparison.Ordinal)
            && string.Equals(
                Digest,
                other.Digest,
                StringComparison.Ordinal)
            && SizeBytes == other.SizeBytes;

        public override bool Equals(object? obj) =>
            obj is ResolverKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(SkillDigest, StringComparer.Ordinal);
            hash.Add(Uri, StringComparer.Ordinal);
            hash.Add(MediaType, StringComparer.Ordinal);
            hash.Add(Digest, StringComparer.Ordinal);
            hash.Add(SizeBytes);
            return hash.ToHashCode();
        }
    }

    private readonly struct DirectoryCandidate
    {
        public DirectoryCandidate(string path, int depth)
        {
            Path = path;
            Depth = depth;
        }

        public string Path { get; }

        public int Depth { get; }
    }

    private sealed class DiagnosticCollector
    {
        private readonly int _maximum;
        private readonly List<SkillPackageDiagnostic> _items = new();
        private bool _truncated;

        public DiagnosticCollector(int maximum)
        {
            _maximum = maximum;
        }

        public bool HasErrors => _items.Any(
            item => string.Equals(
                item.Severity,
                SkillDiagnosticSeverities.Error,
                StringComparison.Ordinal));

        public void Add(
            string sourceId,
            string? relativePath,
            string severity,
            string code,
            string message)
        {
            if (_items.Count < _maximum - 1)
            {
                _items.Add(
                    new SkillPackageDiagnostic(
                        sourceId,
                        relativePath,
                        severity,
                        code,
                        message));
                return;
            }

            if (_truncated)
            {
                return;
            }

            _truncated = true;
            _items.Add(
                new SkillPackageDiagnostic(
                    "<catalog>",
                    relativePath: null,
                    SkillDiagnosticSeverities.Error,
                    SkillPackageDiagnosticCodes.DiagnosticCountExceeded,
                    "Skill-package diagnostics exceed their retained limit."));
        }

        public IReadOnlyList<SkillPackageDiagnostic> Snapshot()
        {
            var ordered = _items
                .OrderBy(item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(
                    item => item.RelativePath ?? string.Empty,
                    StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .Select(
                    item => new SkillPackageDiagnostic(
                        item.SourceId,
                        item.RelativePath,
                        item.Severity,
                        item.Code,
                        item.Message))
                .ToArray();
            return new ReadOnlyCollection<SkillPackageDiagnostic>(ordered);
        }
    }
}

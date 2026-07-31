namespace GameAgent.Persistence;

using GameAgent.Core;
using GameAgent.World;

public static class InteractiveWorldBundleReasonCodes
{
    public const string InvalidArtifact =
        "interactive_world_bundle_invalid_artifact";

    public const string DigestMismatch =
        "interactive_world_bundle_digest_mismatch";

    public const string BindingMismatch =
        "interactive_world_bundle_binding_mismatch";

    public const string Unsettled =
        "interactive_world_bundle_unsettled";

    public const string QuiescenceRequired =
        "interactive_world_bundle_quiescence_required";

    public const string TopologyUnsupported =
        "interactive_world_bundle_topology_unsupported";

    public const string PrivacyPolicyViolation =
        "interactive_world_bundle_privacy_policy_violation";

    public const string CapacityExceeded =
        "interactive_world_bundle_capacity_exceeded";

    public const string TargetExists =
        "interactive_world_bundle_target_exists";

    public const string UnsafePath =
        "interactive_world_bundle_unsafe_path";

    public const string PublicationFailed =
        "interactive_world_bundle_publication_failed";
}

public sealed class InteractiveWorldBundleException : Exception
{
    public InteractiveWorldBundleException(
        string reasonCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new ArgumentException(
                "A bundle reason code is required.",
                nameof(reasonCode));
        }

        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public enum InteractiveWorldBundleExportMode
{
    PrivateLocal = 0,
    PublicExport = 1
}

/// <summary>
/// Bounded inputs captured from one coordinator-issued settlement topology.
/// Callers cannot replace its outbox or mix sidecars from another topology.
/// </summary>
public sealed class InteractiveWorldBundleCaptureSource
{
    public InteractiveWorldBundleCaptureSource(
        NativeWorldRuntime runtime,
        WorldSettlementTopology topology)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Topology = topology
                   ?? throw new ArgumentNullException(nameof(topology));
        Quiescence = topology.SettlementStore
                     as IWorldSettlementQuiescenceSource
                     ?? throw new InteractiveWorldBundleException(
                         InteractiveWorldBundleReasonCodes
                             .QuiescenceRequired,
                         "The settlement topology outbox does not provide "
                         + "an exclusive settled-state fence.");
        MemoryStore = topology.MemoryStore switch
        {
            null => null,
            FileMemoryStore value => value,
            _ => throw Unsupported("memory")
        };
        GroupInteractionStore = topology.GroupStore switch
        {
            null => null,
            FileGroupInteractionStore value => value,
            _ => throw Unsupported("group interaction")
        };
        PresentationStore = topology.PresentationStore switch
        {
            null => null,
            FileWorldPresentationStore value => value,
            _ => throw Unsupported("presentation")
        };
    }

    public NativeWorldRuntime Runtime { get; }

    public WorldSettlementTopology Topology { get; }

    internal IWorldSettlementQuiescenceSource Quiescence { get; }

    internal FileMemoryStore? MemoryStore { get; }

    internal FileGroupInteractionStore? GroupInteractionStore { get; }

    internal FileWorldPresentationStore? PresentationStore { get; }

    private static InteractiveWorldBundleException Unsupported(
        string sink)
    {
        return new InteractiveWorldBundleException(
            InteractiveWorldBundleReasonCodes.TopologyUnsupported,
            $"The settlement topology {sink} sink is not a supported "
            + "local file store.");
    }
}

public sealed class InteractiveWorldBundleLimits
{
    public InteractiveWorldBundleLimits(
        long maxArchiveBytes = 128L * 1_048_576,
        int maxManifestBytes = 262_144,
        int maxEntryBytes = 64 * 1_048_576,
        int maxMemoryRecords = 25_000,
        int maxGroupSessions = 4_096,
        int maxGroupMessages = 100_000,
        int maxGroupRevisionFrames = 100_000,
        int maxPresentationRecords = 100_000,
        int maxEntityReferences = 1_000_000,
        int maxJsonDepth = 64,
        int maxJsonTokensPerEntry = 1_000_000)
    {
        MaxArchiveBytes = InRange(
            maxArchiveBytes,
            1_024,
            512L * 1_048_576,
            nameof(maxArchiveBytes));
        MaxManifestBytes = InRange(
            maxManifestBytes,
            1_024,
            1_048_576,
            nameof(maxManifestBytes));
        MaxEntryBytes = InRange(
            maxEntryBytes,
            1_024,
            128 * 1_048_576,
            nameof(maxEntryBytes));
        MaxMemoryRecords = InRange(
            maxMemoryRecords,
            0,
            1_000_000,
            nameof(maxMemoryRecords));
        MaxGroupSessions = InRange(
            maxGroupSessions,
            0,
            100_000,
            nameof(maxGroupSessions));
        MaxGroupMessages = InRange(
            maxGroupMessages,
            0,
            1_000_000,
            nameof(maxGroupMessages));
        MaxGroupRevisionFrames = InRange(
            maxGroupRevisionFrames,
            0,
            1_000_000,
            nameof(maxGroupRevisionFrames));
        MaxPresentationRecords = InRange(
            maxPresentationRecords,
            0,
            1_000_000,
            nameof(maxPresentationRecords));
        MaxEntityReferences = InRange(
            maxEntityReferences,
            0,
            2_000_000,
            nameof(maxEntityReferences));
        MaxJsonDepth = InRange(
            maxJsonDepth,
            1,
            128,
            nameof(maxJsonDepth));
        MaxJsonTokensPerEntry = InRange(
            maxJsonTokensPerEntry,
            1_024,
            4_000_000,
            nameof(maxJsonTokensPerEntry));
    }

    public long MaxArchiveBytes { get; }

    public int MaxManifestBytes { get; }

    public int MaxEntryBytes { get; }

    public int MaxMemoryRecords { get; }

    public int MaxGroupSessions { get; }

    public int MaxGroupMessages { get; }

    public int MaxGroupRevisionFrames { get; }

    public int MaxPresentationRecords { get; }

    public int MaxEntityReferences { get; }

    public int MaxJsonDepth { get; }

    public int MaxJsonTokensPerEntry { get; }

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

public sealed class InteractiveWorldBundleOptions
{
    public InteractiveWorldBundleOptions(
        InteractiveWorldBundleLimits? limits = null,
        NativeWorldSaveBridgeOptions? nativeSave = null)
    {
        Limits = limits ?? new InteractiveWorldBundleLimits();
        NativeSave = nativeSave ?? new NativeWorldSaveBridgeOptions();
    }

    public InteractiveWorldBundleLimits Limits { get; }

    public NativeWorldSaveBridgeOptions NativeSave { get; }
}

public sealed class InteractiveWorldBundleImportOptions
{
    public InteractiveWorldBundleImportOptions(
        InteractiveWorldBundleOptions? bundle = null,
        FileWorldAuthoritativeTransactionStoreOptions? authoritativeStore =
            null,
        FileMemoryStoreOptions? memoryStore = null,
        FileGroupInteractionStoreOptions? groupInteractionStore = null,
        FileWorldPresentationStoreOptions? presentationStore = null)
    {
        Bundle = bundle ?? new InteractiveWorldBundleOptions();
        AuthoritativeStore = authoritativeStore
                             ?? new
                                 FileWorldAuthoritativeTransactionStoreOptions();
        MemoryStore = memoryStore ?? new FileMemoryStoreOptions();
        GroupInteractionStore = groupInteractionStore
                                ?? new FileGroupInteractionStoreOptions();
        PresentationStore = presentationStore
                            ?? new FileWorldPresentationStoreOptions();
    }

    public InteractiveWorldBundleOptions Bundle { get; }

    public FileWorldAuthoritativeTransactionStoreOptions
        AuthoritativeStore
    { get; }

    public FileMemoryStoreOptions MemoryStore { get; }

    public FileGroupInteractionStoreOptions GroupInteractionStore
    { get; }

    public FileWorldPresentationStoreOptions PresentationStore { get; }
}

/// <summary>
/// Exact authoritative coordinate and content digests carried by a bundle.
/// </summary>
public sealed class InteractiveWorldBundleBinding
{
    internal InteractiveWorldBundleBinding(
        string packageId,
        string packageContentVersion,
        string packageDigest,
        string worldId,
        string timelineId,
        long timelineEpoch,
        long saveRevision,
        long stateVersion,
        string catalogDigest,
        string stateDigest,
        string saveDigest)
    {
        PackageId = packageId;
        PackageContentVersion = packageContentVersion;
        PackageDigest = packageDigest;
        WorldId = worldId;
        TimelineId = timelineId;
        TimelineEpoch = timelineEpoch;
        SaveRevision = saveRevision;
        StateVersion = stateVersion;
        CatalogDigest = catalogDigest;
        StateDigest = stateDigest;
        SaveDigest = saveDigest;
    }

    public string PackageId { get; }

    public string PackageContentVersion { get; }

    public string PackageDigest { get; }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public long SaveRevision { get; }

    public long StateVersion { get; }

    public string CatalogDigest { get; }

    public string StateDigest { get; }

    public string SaveDigest { get; }
}

public sealed class InteractiveWorldBundleArtifact
{
    private readonly byte[] _bytes;

    internal InteractiveWorldBundleArtifact(
        byte[] bytes,
        string digest,
        InteractiveWorldBundleExportMode exportMode,
        InteractiveWorldBundleBinding binding)
    {
        _bytes = (byte[])bytes.Clone();
        Digest = digest;
        ExportMode = exportMode;
        Binding = binding;
    }

    public string Contract => InteractiveWorldBundle.ContractId;

    public string Digest { get; }

    public InteractiveWorldBundleExportMode ExportMode { get; }

    public InteractiveWorldBundleBinding Binding { get; }

    public long Length => _bytes.LongLength;

    public byte[] GetBytes()
    {
        return (byte[])_bytes.Clone();
    }
}

public sealed class InteractiveWorldBundleImportResult
{
    internal InteractiveWorldBundleImportResult(
        string targetDirectory,
        InteractiveWorldBundleExportMode exportMode,
        InteractiveWorldBundleBinding binding,
        string artifactDigest)
    {
        TargetDirectory = targetDirectory;
        ExportMode = exportMode;
        Binding = binding;
        ArtifactDigest = artifactDigest;
    }

    public string TargetDirectory { get; }

    public string AuthoritativeStorePath =>
        Path.Combine(
            TargetDirectory,
            InteractiveWorldBundle.AuthoritativeStoreFileName);

    public string MemoryStorePath =>
        Path.Combine(
            TargetDirectory,
            InteractiveWorldBundle.MemoryStoreFileName);

    public string GroupInteractionStorePath =>
        Path.Combine(
            TargetDirectory,
            InteractiveWorldBundle.GroupStoreFileName);

    public string PresentationStorePath =>
        Path.Combine(
            TargetDirectory,
            InteractiveWorldBundle.PresentationStoreFileName);

    public InteractiveWorldBundleExportMode ExportMode { get; }

    public InteractiveWorldBundleBinding Binding { get; }

    public string ArtifactDigest { get; }
}

internal sealed class InteractiveWorldSidecarCaptureLease<T>
    : IAsyncDisposable
{
    private SemaphoreSlim? _gate;

    public InteractiveWorldSidecarCaptureLease(
        SemaphoreSlim gate,
        IReadOnlyList<T> items)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public IReadOnlyList<T> Items { get; }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _gate, null)?.Release();
        return default;
    }
}

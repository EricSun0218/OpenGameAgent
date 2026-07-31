using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public sealed class WorldClockSnapshot
{
    public WorldClockSnapshot(string clockId, long epoch, long tick)
    {
        ClockId = WorldValidation.Required(
            clockId,
            nameof(clockId),
            192);
        if (epoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch));
        }

        Epoch = epoch;
        Tick = tick;
    }

    public string ClockId { get; }

    public long Epoch { get; }

    public long Tick { get; }
}

public sealed class WorldTrustedExtensionIdentity
{
    public WorldTrustedExtensionIdentity(
        string capabilityId,
        string version,
        string contentDigest)
    {
        CapabilityId = WorldValidation.Required(
            capabilityId,
            nameof(capabilityId),
            256);
        Version = WorldValidation.Required(
            version,
            nameof(version),
            128);
        if (!CanonicalJsonDigest.IsSha256(contentDigest))
        {
            throw new ArgumentException(
                "Content digest must be lowercase SHA-256.",
                nameof(contentDigest));
        }

        ContentDigest = contentDigest;
    }

    public string CapabilityId { get; }

    public string Version { get; }

    public string ContentDigest { get; }
}

/// <summary>
/// A complete native save snapshot. Authored package content remains outside
/// the save and is bound by an exact digest.
/// </summary>
public sealed class WorldSaveDocument
{
    public WorldSaveDocument(
        string packageId,
        string packageContentVersion,
        string packageDigest,
        string worldId,
        string timelineId,
        long saveRevision,
        string stateVersion,
        IEnumerable<WorldClockSnapshot> clocks,
        JsonElement state,
        JsonElement eventLog,
        JsonElement memoryReferences,
        string? parentTimelineId = null,
        long? parentSaveRevision = null,
        JsonElement? pendingTransaction = null,
        IEnumerable<WorldTrustedExtensionIdentity>? trustedExtensions = null,
        IReadOnlyDictionary<string, JsonElement>? extensionData = null,
        WorldPackageLimits? limits = null)
    {
        PackageId = WorldValidation.Required(
            packageId,
            nameof(packageId),
            256);
        PackageContentVersion = WorldValidation.Required(
            packageContentVersion,
            nameof(packageContentVersion),
            128);
        if (!CanonicalJsonDigest.IsSha256(packageDigest))
        {
            throw new ArgumentException(
                "Package digest must be lowercase SHA-256.",
                nameof(packageDigest));
        }

        WorldId = WorldValidation.Required(
            worldId,
            nameof(worldId),
            256);
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId),
            256);
        if (saveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveRevision));
        }

        StateVersion = WorldValidation.Required(
            stateVersion,
            nameof(stateVersion),
            256);
        ParentTimelineId = WorldValidation.Optional(
            parentTimelineId,
            nameof(parentTimelineId),
            256);
        if (parentSaveRevision is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parentSaveRevision));
        }

        if ((ParentTimelineId is null) != (parentSaveRevision is null))
        {
            throw new ArgumentException(
                "Parent timeline and revision must be supplied together.");
        }

        var effectiveLimits = limits ?? new WorldPackageLimits();
        PackageDigest = packageDigest;
        SaveRevision = saveRevision;
        ParentSaveRevision = parentSaveRevision;
        Clocks = CopyClocks(clocks);
        State = CopyJson(
            state,
            JsonValueKind.Object,
            effectiveLimits,
            nameof(state),
            rejectAuthoritativeNumbers: true);
        EventLog = CopyJson(
            eventLog,
            JsonValueKind.Array,
            effectiveLimits,
            nameof(eventLog),
            rejectAuthoritativeNumbers: true);
        MemoryReferences = CopyJson(
            memoryReferences,
            JsonValueKind.Array,
            effectiveLimits,
            nameof(memoryReferences),
            rejectAuthoritativeNumbers: true);
        if (pendingTransaction.HasValue)
        {
            PendingTransaction = CopyJson(
                pendingTransaction.Value,
                JsonValueKind.Object,
                effectiveLimits,
                nameof(pendingTransaction),
                rejectAuthoritativeNumbers: true);
        }

        TrustedExtensions = CopyTrustedExtensions(trustedExtensions);
        ExtensionData = WorldDataJson.CopyExtensionData(
            extensionData,
            nameof(extensionData));
        SaveDigest = WorldDataDigest.Compute(
            WorldSaveCodec.WriteCanonical(this, effectiveLimits));
    }

    public string Contract => WorldDataContractIds.SaveV1;

    public string PackageId { get; }

    public string PackageContentVersion { get; }

    public string PackageDigest { get; }

    public string WorldId { get; }

    public string TimelineId { get; }

    public string? ParentTimelineId { get; }

    public long? ParentSaveRevision { get; }

    public long SaveRevision { get; }

    public string StateVersion { get; }

    public IReadOnlyList<WorldClockSnapshot> Clocks { get; }

    public JsonElement State { get; }

    public JsonElement EventLog { get; }

    public JsonElement MemoryReferences { get; }

    public JsonElement? PendingTransaction { get; }

    public IReadOnlyList<WorldTrustedExtensionIdentity>
        TrustedExtensions
    { get; }

    public IReadOnlyDictionary<string, JsonElement> ExtensionData { get; }

    public string SaveDigest { get; }

    private static IReadOnlyList<WorldClockSnapshot> CopyClocks(
        IEnumerable<WorldClockSnapshot> clocks)
    {
        if (clocks is null)
        {
            throw new ArgumentNullException(nameof(clocks));
        }

        var copy = WorldValidation.MaterializeBounded(
                clocks,
                256,
                nameof(clocks))
            .Select(
                clock => clock
                         ?? throw new ArgumentException(
                             "Clocks cannot contain null entries.",
                             nameof(clocks)))
            .OrderBy(clock => clock.ClockId, StringComparer.Ordinal)
            .ToArray();

        for (var index = 1; index < copy.Length; index++)
        {
            if (string.Equals(
                    copy[index - 1].ClockId,
                    copy[index].ClockId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Clock identifiers must be unique.",
                    nameof(clocks));
            }
        }

        return new ReadOnlyCollection<WorldClockSnapshot>(copy);
    }

    private static IReadOnlyList<WorldTrustedExtensionIdentity>
        CopyTrustedExtensions(
            IEnumerable<WorldTrustedExtensionIdentity>? extensions)
    {
        if (extensions is null)
        {
            return Array.Empty<WorldTrustedExtensionIdentity>();
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
            .ToArray();

        for (var index = 1; index < copy.Length; index++)
        {
            if (string.Equals(
                    copy[index - 1].CapabilityId,
                    copy[index].CapabilityId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Trusted extension identifiers must be unique.",
                    nameof(extensions));
            }
        }

        return new ReadOnlyCollection<WorldTrustedExtensionIdentity>(copy);
    }

    private static JsonElement CopyJson(
        JsonElement value,
        JsonValueKind requiredKind,
        WorldPackageLimits limits,
        string parameterName,
        bool rejectAuthoritativeNumbers)
    {
        if (value.ValueKind != requiredKind)
        {
            throw new ArgumentException(
                "JSON value has an invalid root kind.",
                parameterName);
        }

        JsonValueInspector.ValidateAndMeasure(
            value,
            limits.CreateJsonLimits(checked((int)limits.MaxFileBytes)),
            parameterName);
        WorldDataJson.ValidateNoDuplicatePropertiesAndUnicode(value);
        ValidateNumbers(
            value,
            rejectAuthoritativeNumbers,
            parameterName);
        return value.Clone();
    }

    private static void ValidateNumbers(
        JsonElement value,
        bool rejectAll,
        string parameterName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    ValidateNumbers(
                        property.Value,
                        rejectAll,
                        parameterName);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ValidateNumbers(item, rejectAll, parameterName);
                }

                break;
            case JsonValueKind.Number:
                if (rejectAll || !value.TryGetInt64(out _))
                {
                    throw new ArgumentException(
                        "Authoritative JSON cannot contain binary or "
                        + "non-integral numeric values. Portable state "
                        + "numbers are canonical strings.",
                        parameterName);
                }

                break;
        }
    }
}

public static class WorldSaveBinding
{
    public static void Validate(
        WorldSaveDocument save,
        WorldPackageDefinition package)
    {
        if (save is null)
        {
            throw new ArgumentNullException(nameof(save));
        }

        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        if (!string.Equals(
                save.PackageId,
                package.PackageId,
                StringComparison.Ordinal)
            || !string.Equals(
                save.PackageContentVersion,
                package.ContentVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                save.PackageDigest,
                package.PackageDigest,
                StringComparison.Ordinal))
        {
            throw new WorldDataContractException(
                WorldDataReasonCodes.PackageBindingMismatch,
                "Save does not bind the exact world package.");
        }
    }
}

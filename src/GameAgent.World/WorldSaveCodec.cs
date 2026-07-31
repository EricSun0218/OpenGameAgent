using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.World;

public static class WorldSaveCodec
{
    private static readonly HashSet<string> RootFields =
        new(
            new[]
            {
                "contract",
                "packageId",
                "packageContentVersion",
                "packageDigest",
                "worldId",
                "timelineId",
                "parentTimeline",
                "saveRevision",
                "stateVersion",
                "clocks",
                "state",
                "eventLog",
                "memoryReferences",
                "pendingTransaction",
                "trustedExtensions",
                "extensionData"
            },
            StringComparer.Ordinal);

    private static readonly HashSet<string> ParentFields =
        new(new[] { "timelineId", "saveRevision" }, StringComparer.Ordinal);

    private static readonly HashSet<string> ClockFields =
        new(new[] { "clockId", "epoch", "tick" }, StringComparer.Ordinal);

    private static readonly HashSet<string> TrustedExtensionFields =
        new(
            new[] { "capabilityId", "version", "contentDigest" },
            StringComparer.Ordinal);

    public static byte[] Write(
        WorldSaveDocument save,
        WorldPackageLimits? limits = null)
    {
        if (save is null)
        {
            throw new ArgumentNullException(nameof(save));
        }

        return WriteCanonical(save, limits);
    }

    public static void Write(
        Stream destination,
        WorldSaveDocument save,
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

        var bytes = Write(save, limits);
        destination.Write(bytes, 0, bytes.Length);
    }

    public static WorldSaveDocument Read(
        ReadOnlySpan<byte> utf8,
        WorldPackageLimits? limits = null)
    {
        var effectiveLimits = limits ?? new WorldPackageLimits();
        using var document =
            WorldDataJson.Parse(utf8, effectiveLimits, nameof(utf8));
        var root = document.RootElement;
        WorldDataJson.RequireOnlyProperties(root, RootFields);
        if (!string.Equals(
                WorldDataJson.RequiredString(root, "contract", 96),
                WorldDataContractIds.SaveV1,
                StringComparison.Ordinal))
        {
            throw Invalid(
                WorldDataReasonCodes.InvalidContract,
                "Unsupported native save contract.");
        }

        var parent = ReadParent(root);
        var clocks = ReadClocks(root);
        var trustedExtensions = ReadTrustedExtensions(root);
        var extensionData = ReadExtensionData(root);
        var state = RequiredValue(root, "state", JsonValueKind.Object);
        var eventLog =
            RequiredValue(root, "eventLog", JsonValueKind.Array);
        var memoryReferences = RequiredValue(
            root,
            "memoryReferences",
            JsonValueKind.Array);
        JsonElement? pendingTransaction = null;
        if (root.TryGetProperty("pendingTransaction", out var pending))
        {
            if (pending.ValueKind == JsonValueKind.Object)
            {
                pendingTransaction = pending.Clone();
            }
            else if (pending.ValueKind != JsonValueKind.Null)
            {
                throw Invalid(
                    WorldDataReasonCodes.InvalidJson,
                    "Pending transaction has an invalid root kind.");
            }
        }
        else
        {
            throw Invalid(
                WorldDataReasonCodes.InvalidJson,
                "Pending transaction field is missing.");
        }

        WorldSaveDocument save;
        try
        {
            save = new WorldSaveDocument(
                WorldDataJson.RequiredString(root, "packageId", 256),
                WorldDataJson.RequiredString(
                    root,
                    "packageContentVersion",
                    128),
                WorldDataJson.RequiredString(
                    root,
                    "packageDigest",
                    64),
                WorldDataJson.RequiredString(root, "worldId", 256),
                WorldDataJson.RequiredString(root, "timelineId", 256),
                WorldDataJson.RequiredCanonicalInt64String(
                    root,
                    "saveRevision",
                    minimum: 0),
                WorldDataJson.RequiredString(root, "stateVersion", 256),
                clocks,
                state,
                eventLog,
                memoryReferences,
                parent.TimelineId,
                parent.SaveRevision,
                pendingTransaction,
                trustedExtensions,
                extensionData,
                effectiveLimits);
        }
        catch (WorldDataContractException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException)
        {
            throw Invalid(
                WorldDataReasonCodes.InvalidJson,
                "Native save fields are invalid.");
        }

        var canonical = WriteCanonical(save, effectiveLimits);
        if (!utf8.SequenceEqual(canonical))
        {
            throw Invalid(
                WorldDataReasonCodes.InvalidJson,
                "Native save is not in canonical form.");
        }

        return save;
    }

    public static WorldSaveDocument Read(
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
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > effectiveLimits.MaxFileBytes)
            {
                throw Invalid(
                    WorldDataReasonCodes.ByteLimitExceeded,
                    "Native save exceeds its byte limit.");
            }

            output.Write(buffer, 0, read);
        }

        return Read(output.ToArray(), effectiveLimits);
    }

    internal static byte[] WriteCanonical(
        WorldSaveDocument save,
        WorldPackageLimits? limits = null)
    {
        var effectiveLimits = limits ?? new WorldPackageLimits();
        using var output = new MemoryStream();
        using var boundedOutput = new WorldBoundedArchiveWriteStream(
            output,
            effectiveLimits.MaxFileBytes,
            WorldDataReasonCodes.ByteLimitExceeded,
            "Native save exceeds its byte limit.");
        using (var writer = new Utf8JsonWriter(boundedOutput))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", WorldDataContractIds.SaveV1);
            writer.WriteString("packageId", save.PackageId);
            writer.WriteString(
                "packageContentVersion",
                save.PackageContentVersion);
            writer.WriteString("packageDigest", save.PackageDigest);
            writer.WriteString("worldId", save.WorldId);
            writer.WriteString("timelineId", save.TimelineId);
            writer.WritePropertyName("parentTimeline");
            if (save.ParentTimelineId is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("timelineId", save.ParentTimelineId);
                writer.WriteString(
                    "saveRevision",
                    save.ParentSaveRevision!.Value.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteString(
                "saveRevision",
                save.SaveRevision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("stateVersion", save.StateVersion);
            writer.WritePropertyName("clocks");
            writer.WriteStartArray();
            foreach (var clock in save.Clocks)
            {
                writer.WriteStartObject();
                writer.WriteString("clockId", clock.ClockId);
                writer.WriteString(
                    "epoch",
                    clock.Epoch.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture));
                writer.WriteString(
                    "tick",
                    clock.Tick.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("state");
            WorldDataJson.WriteCanonical(writer, save.State);
            writer.WritePropertyName("eventLog");
            WorldDataJson.WriteCanonical(writer, save.EventLog);
            writer.WritePropertyName("memoryReferences");
            WorldDataJson.WriteCanonical(
                writer,
                save.MemoryReferences);
            writer.WritePropertyName("pendingTransaction");
            if (save.PendingTransaction.HasValue)
            {
                WorldDataJson.WriteCanonical(
                    writer,
                    save.PendingTransaction.Value);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WritePropertyName("trustedExtensions");
            writer.WriteStartArray();
            foreach (var extension in save.TrustedExtensions)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "capabilityId",
                    extension.CapabilityId);
                writer.WriteString("version", extension.Version);
                writer.WriteString(
                    "contentDigest",
                    extension.ContentDigest);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("extensionData");
            writer.WriteStartObject();
            foreach (var pair in save.ExtensionData.OrderBy(
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

    private static (
        string? TimelineId,
        long? SaveRevision) ReadParent(JsonElement root)
    {
        if (!root.TryGetProperty("parentTimeline", out var parent))
        {
            throw Invalid(
                WorldDataReasonCodes.InvalidJson,
                "Parent timeline field is missing.");
        }

        if (parent.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }

        WorldDataJson.RequireOnlyProperties(parent, ParentFields);
        return (
            WorldDataJson.RequiredString(parent, "timelineId", 256),
            WorldDataJson.RequiredCanonicalInt64String(
                parent,
                "saveRevision",
                minimum: 0));
    }

    private static IReadOnlyList<WorldClockSnapshot> ReadClocks(
        JsonElement root)
    {
        if (!root.TryGetProperty("clocks", out var clocks)
            || clocks.ValueKind != JsonValueKind.Array
            || clocks.GetArrayLength() > 256)
        {
            throw Invalid(
                WorldDataReasonCodes.InvalidJson,
                "Clock collection is invalid.");
        }

        var result = new List<WorldClockSnapshot>(
            clocks.GetArrayLength());
        foreach (var clock in clocks.EnumerateArray())
        {
            WorldDataJson.RequireOnlyProperties(clock, ClockFields);
            result.Add(
                new WorldClockSnapshot(
                    WorldDataJson.RequiredString(clock, "clockId", 192),
                    WorldDataJson.RequiredCanonicalInt64String(
                        clock,
                        "epoch",
                        minimum: 0),
                    WorldDataJson.RequiredCanonicalInt64String(
                        clock,
                        "tick")));
        }

        return new ReadOnlyCollection<WorldClockSnapshot>(result);
    }

    private static IReadOnlyList<WorldTrustedExtensionIdentity>
        ReadTrustedExtensions(JsonElement root)
    {
        if (!root.TryGetProperty("trustedExtensions", out var extensions)
            || extensions.ValueKind != JsonValueKind.Array
            || extensions.GetArrayLength() > 256)
        {
            throw Invalid(
                WorldDataReasonCodes.InvalidJson,
                "Trusted extension collection is invalid.");
        }

        var result = new List<WorldTrustedExtensionIdentity>(
            extensions.GetArrayLength());
        foreach (var extension in extensions.EnumerateArray())
        {
            WorldDataJson.RequireOnlyProperties(
                extension,
                TrustedExtensionFields);
            result.Add(
                new WorldTrustedExtensionIdentity(
                    WorldDataJson.RequiredString(
                        extension,
                        "capabilityId",
                        256),
                    WorldDataJson.RequiredString(
                        extension,
                        "version",
                        128),
                    WorldDataJson.RequiredString(
                        extension,
                        "contentDigest",
                        64)));
        }

        return new ReadOnlyCollection<WorldTrustedExtensionIdentity>(result);
    }

    private static IReadOnlyDictionary<string, JsonElement>
        ReadExtensionData(JsonElement root)
    {
        if (!root.TryGetProperty("extensionData", out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(
                WorldDataReasonCodes.InvalidJson,
                "Save extension data is invalid.");
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
            throw Invalid(
                WorldDataReasonCodes.InvalidJson,
                "Save extension data is not ordered.");
        }

        var values = properties
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);
        return WorldDataJson.CopyExtensionData(values, "extensionData");
    }

    private static JsonElement RequiredValue(
        JsonElement parent,
        string propertyName,
        JsonValueKind kind)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != kind)
        {
            throw Invalid(
                WorldDataReasonCodes.InvalidJson,
                "A required JSON field is missing or invalid.");
        }

        return value.Clone();
    }

    private static WorldDataContractException Invalid(
        string reasonCode,
        string message)
    {
        return new WorldDataContractException(reasonCode, message);
    }
}

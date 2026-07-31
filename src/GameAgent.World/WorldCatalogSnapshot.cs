using System.Collections.ObjectModel;
using GameAgent.Core;

namespace GameAgent.World;

/// <summary>
/// One immutable authority boundary for event and interaction catalogs.
/// Component digests remain inspectable while every bound component exposes
/// the same composite digest used by authoritative world coordinates.
/// </summary>
public sealed class WorldCatalogSnapshot
{
    public WorldCatalogSnapshot(
        string catalogId,
        long generation,
        IEnumerable<WorldEventDefinition> eventDefinitions,
        IEnumerable<InteractionDefinition> interactionDefinitions,
        IReadOnlyDictionary<string, string>?
            additionalComponentDigests = null)
    {
        CatalogId = WorldValidation.Required(
            catalogId,
            nameof(catalogId));
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        Generation = generation;
        var events = new WorldEventCatalogSnapshot(
            CatalogId + ".events",
            generation,
            eventDefinitions
            ?? throw new ArgumentNullException(nameof(eventDefinitions)));
        var interactions = new InteractionCatalogSnapshot(
            CatalogId + ".interactions",
            generation,
            interactionDefinitions
            ?? throw new ArgumentNullException(
                nameof(interactionDefinitions)));
        AdditionalComponentDigests = CopyAdditional(
            additionalComponentDigests);
        Digest = ComputeDigest(
            events.ComponentDigest,
            interactions.ComponentDigest);
        Events = new WorldEventCatalogSnapshot(
            events.CatalogId,
            events.Generation,
            events.Definitions,
            Digest,
            events.ComponentDigest);
        Interactions = new InteractionCatalogSnapshot(
            interactions.CatalogId,
            interactions.Generation,
            interactions.Definitions,
            Digest,
            interactions.ComponentDigest);
    }

    public string CatalogId { get; }

    public long Generation { get; }

    public string Digest { get; }

    public WorldEventCatalogSnapshot Events { get; }

    public InteractionCatalogSnapshot Interactions { get; }

    public IReadOnlyDictionary<string, string>
        AdditionalComponentDigests
    { get; }

    public WorldStateFence CreateFence(
        string worldId,
        string timelineId,
        long timelineEpoch,
        long saveRevision,
        string stateVersion)
    {
        return new WorldStateFence(
            worldId,
            timelineId,
            timelineEpoch,
            saveRevision,
            stateVersion,
            Digest,
            Events.ComponentDigest,
            Interactions.ComponentDigest);
    }

    private string ComputeDigest(
        string eventDigest,
        string interactionDigest)
    {
        return WorldCatalogDigest.Compute(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("catalogId", CatalogId);
                writer.WriteString(
                    "generation",
                    Generation.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteString("events", eventDigest);
                writer.WriteString("interactions", interactionDigest);
                writer.WritePropertyName("additionalComponents");
                writer.WriteStartObject();
                foreach (var component in AdditionalComponentDigests)
                {
                    writer.WriteString(component.Key, component.Value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            "additionalComponentDigests");
    }

    private static IReadOnlyDictionary<string, string> CopyAdditional(
        IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            return new ReadOnlyDictionary<string, string>(
                new SortedDictionary<string, string>(
                    StringComparer.Ordinal));
        }

        var bounded = WorldValidation.MaterializeBounded(
            values,
            64,
            () => new ArgumentException(
                "The additional component collection exceeds its limit.",
                nameof(values)));
        if (bounded.Length == 0)
        {
            return new ReadOnlyDictionary<string, string>(
                new SortedDictionary<string, string>(
                    StringComparer.Ordinal));
        }

        var copy = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var value in bounded)
        {
            var key = WorldValidation.Required(
                value.Key,
                nameof(values),
                192);
            if (!CanonicalJsonDigest.IsSha256(value.Value))
            {
                throw new ArgumentException(
                    "Additional component digests must be lowercase "
                    + "SHA-256 values.",
                    nameof(values));
            }

            copy.Add(key, value.Value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}

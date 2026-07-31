using System.Collections.ObjectModel;
using System.Text.Json;

namespace GameAgent.World;

public enum WorldAgentInvocationPolicy
{
    None = 0,
    OncePerInstance = 1,
    OncePerParticipant = 2
}

/// <summary>
/// A cooldown measured on the trigger's game-defined clock.
/// </summary>
public sealed class WorldEventCooldown
{
    public WorldEventCooldown(long minimumTicks)
    {
        if (minimumTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumTicks));
        }

        MinimumTicks = minimumTicks;
    }

    public long MinimumTicks { get; }
}

/// <summary>
/// A versioned declaration of one fixed event. Handler identifiers bind the
/// declaration to host-owned business logic without embedding that logic in
/// the framework.
/// </summary>
public sealed class WorldEventDefinition
{
    public WorldEventDefinition(
        string definitionId,
        string version,
        string triggerKind,
        int priority,
        string conditionHandlerId,
        string participantSelectorId,
        string resolverId,
        string effectHandlerId,
        IEnumerable<string>? readResourceKeys = null,
        IEnumerable<string>? writeResourceKeys = null,
        WorldEventCooldown? cooldown = null,
        int? maximumOccurrences = null,
        int? maximumParticipants = null,
        WorldAgentInvocationPolicy agentInvocationPolicy =
            WorldAgentInvocationPolicy.None,
        IEnumerable<string>? admissionHandlerIds = null,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        DefinitionId = WorldValidation.Required(
            definitionId,
            nameof(definitionId));
        Version = WorldValidation.Required(version, nameof(version), 96);
        TriggerKind = WorldValidation.Required(
            triggerKind,
            nameof(triggerKind));
        if (priority is < -1_000_000 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        ConditionHandlerId = WorldValidation.Required(
            conditionHandlerId,
            nameof(conditionHandlerId));
        ParticipantSelectorId = WorldValidation.Required(
            participantSelectorId,
            nameof(participantSelectorId));
        ResolverId = WorldValidation.Required(
            resolverId,
            nameof(resolverId));
        EffectHandlerId = WorldValidation.Required(
            effectHandlerId,
            nameof(effectHandlerId));
        ReadResourceKeys = WorldValidation.CopyKeys(
            readResourceKeys,
            nameof(readResourceKeys));
        WriteResourceKeys = WorldValidation.CopyKeys(
            writeResourceKeys,
            nameof(writeResourceKeys));
        if (ReadResourceKeys.Count + WriteResourceKeys.Count
            > WorldValidation.MaximumResourceKeys)
        {
            throw new ArgumentException(
                "The definition exceeds its combined resource-key limit.");
        }

        if (maximumOccurrences is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOccurrences));
        }

        if (maximumParticipants is <= 0
            or > WorldValidation.MaximumParticipants)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumParticipants));
        }

        if (!Enum.IsDefined(
                typeof(WorldAgentInvocationPolicy),
                agentInvocationPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(agentInvocationPolicy));
        }

        Priority = priority;
        Cooldown = cooldown;
        MaximumOccurrences = maximumOccurrences;
        MaximumParticipants = maximumParticipants;
        AgentInvocationPolicy = agentInvocationPolicy;
        AdmissionHandlerIds = WorldValidation.CopyKeys(
            admissionHandlerIds,
            nameof(admissionHandlerIds),
            maximumCount: 32);
        Attributes = WorldValidation.CopyParameters(
            attributes,
            nameof(attributes));
    }

    public string DefinitionId { get; }

    public string Version { get; }

    public string TriggerKind { get; }

    public int Priority { get; }

    public string ConditionHandlerId { get; }

    public string ParticipantSelectorId { get; }

    public string ResolverId { get; }

    public string EffectHandlerId { get; }

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public WorldEventCooldown? Cooldown { get; }

    public int? MaximumOccurrences { get; }

    public int? MaximumParticipants { get; }

    public WorldAgentInvocationPolicy AgentInvocationPolicy { get; }

    public IReadOnlyList<string> AdmissionHandlerIds { get; }

    public IReadOnlyDictionary<string, string> Attributes { get; }
}

/// <summary>
/// Immutable, content-addressed event-definition catalog. Engine-facing
/// authoritative planning accepts this snapshot rather than a loose list so
/// a hot-reloaded state fence cannot accidentally bind older definitions.
/// </summary>
public sealed class WorldEventCatalogSnapshot
{
    private readonly IReadOnlyList<WorldEventDefinition> _definitions;

    public WorldEventCatalogSnapshot(
        string catalogId,
        long generation,
        IEnumerable<WorldEventDefinition> definitions)
        : this(
            catalogId,
            generation,
            definitions,
            authoritativeCatalogDigest: null)
    {
    }

    internal WorldEventCatalogSnapshot(
        string catalogId,
        long generation,
        IEnumerable<WorldEventDefinition> definitions,
        string? authoritativeCatalogDigest,
        string? precomputedComponentDigest = null)
    {
        CatalogId = WorldValidation.Required(
            catalogId,
            nameof(catalogId));
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (definitions is null)
        {
            throw new ArgumentNullException(nameof(definitions));
        }

        var copy = WorldValidation.MaterializeBounded(
                definitions,
                WorldValidation.MaximumCatalogDefinitions,
                nameof(definitions))
            .Select(
                definition => definition
                              ?? throw new ArgumentException(
                                  "Definitions cannot contain null entries.",
                                  nameof(definitions)))
            .OrderBy(
                definition => definition.DefinitionId,
                StringComparer.Ordinal)
            .ThenBy(
                definition => definition.Version,
                StringComparer.Ordinal)
            .ToArray();

        for (var index = 1; index < copy.Length; index++)
        {
            if (string.Equals(
                    copy[index - 1].DefinitionId,
                    copy[index].DefinitionId,
                    StringComparison.Ordinal)
                && string.Equals(
                    copy[index - 1].Version,
                    copy[index].Version,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The event catalog contains a duplicate definition "
                    + "ID and version.",
                    nameof(definitions));
            }
        }

        Generation = generation;
        _definitions =
            new ReadOnlyCollection<WorldEventDefinition>(copy);
        if (precomputedComponentDigest is not null
            && !GameAgent.Core.CanonicalJsonDigest.IsSha256(
                precomputedComponentDigest))
        {
            throw new ArgumentException(
                "A component catalog digest must be a lowercase SHA-256 "
                + "digest.",
                nameof(precomputedComponentDigest));
        }

        ComponentDigest =
            precomputedComponentDigest ?? ComputeDigest();
        if (authoritativeCatalogDigest is not null
            && !GameAgent.Core.CanonicalJsonDigest.IsSha256(
                authoritativeCatalogDigest))
        {
            throw new ArgumentException(
                "An authoritative catalog digest must be a lowercase "
                + "SHA-256 digest.",
                nameof(authoritativeCatalogDigest));
        }

        Digest = authoritativeCatalogDigest ?? ComponentDigest;
    }

    public string CatalogId { get; }

    public long Generation { get; }

    public IReadOnlyList<WorldEventDefinition> Definitions =>
        _definitions;

    public string ComponentDigest { get; }

    /// <summary>
    /// Digest used by the authoritative coordinate. It equals
    /// <see cref="ComponentDigest"/> for a standalone event catalog and the
    /// enclosing composite digest for a bound world catalog.
    /// </summary>
    public string Digest { get; }

    private string ComputeDigest()
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
                writer.WritePropertyName("definitions");
                writer.WriteStartArray();
                foreach (var definition in _definitions)
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "definitionId",
                        definition.DefinitionId);
                    writer.WriteString("version", definition.Version);
                    writer.WriteString(
                        "triggerKind",
                        definition.TriggerKind);
                    writer.WriteNumber("priority", definition.Priority);
                    writer.WriteString(
                        "conditionHandlerId",
                        definition.ConditionHandlerId);
                    writer.WriteString(
                        "participantSelectorId",
                        definition.ParticipantSelectorId);
                    writer.WriteString(
                        "resolverId",
                        definition.ResolverId);
                    writer.WriteString(
                        "effectHandlerId",
                        definition.EffectHandlerId);
                    WriteStrings(
                        writer,
                        "readResourceKeys",
                        definition.ReadResourceKeys);
                    WriteStrings(
                        writer,
                        "writeResourceKeys",
                        definition.WriteResourceKeys);
                    if (definition.Cooldown is null)
                    {
                        writer.WriteNull("cooldownTicks");
                    }
                    else
                    {
                        writer.WriteString(
                            "cooldownTicks",
                            definition.Cooldown.MinimumTicks.ToString(
                                System.Globalization.CultureInfo
                                    .InvariantCulture));
                    }

                    if (definition.MaximumOccurrences.HasValue)
                    {
                        writer.WriteNumber(
                            "maximumOccurrences",
                            definition.MaximumOccurrences.Value);
                    }
                    else
                    {
                        writer.WriteNull("maximumOccurrences");
                    }

                    if (definition.MaximumParticipants.HasValue)
                    {
                        writer.WriteNumber(
                            "maximumParticipants",
                            definition.MaximumParticipants.Value);
                    }
                    else
                    {
                        writer.WriteNull("maximumParticipants");
                    }

                    writer.WriteString(
                        "agentInvocationPolicy",
                        definition.AgentInvocationPolicy.ToString());
                    WriteStrings(
                        writer,
                        "admissionHandlerIds",
                        definition.AdmissionHandlerIds);
                    writer.WritePropertyName("attributes");
                    writer.WriteStartObject();
                    foreach (var attribute in definition.Attributes.OrderBy(
                                 pair => pair.Key,
                                 StringComparer.Ordinal))
                    {
                        writer.WriteString(
                            attribute.Key,
                            attribute.Value);
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            "definitions");
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(
                     item => item,
                     StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}

public sealed class WorldEventParticipant
{
    public WorldEventParticipant(
        string entityId,
        long incarnation,
        string role)
    {
        EntityId = WorldValidation.Required(entityId, nameof(entityId));
        if (incarnation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        }

        Role = WorldValidation.Required(role, nameof(role), 128);
        Incarnation = incarnation;
    }

    public string EntityId { get; }

    public long Incarnation { get; }

    public string Role { get; }

    internal string StableKey =>
        WorldValidation.ComposeStableKey(
            Role,
            EntityId,
            Incarnation.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// One host-resolved event candidate. Resolution keys must be stable for the
/// same definition, trigger, and selected world state.
/// </summary>
public sealed class WorldEventResolution
{
    public WorldEventResolution(
        string resolutionKey,
        IEnumerable<WorldEventParticipant>? participants = null,
        IEnumerable<string>? readResourceKeys = null,
        IEnumerable<string>? writeResourceKeys = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        ResolutionKey = WorldValidation.Required(
            resolutionKey,
            nameof(resolutionKey));
        Participants = CopyParticipants(participants);
        ReadResourceKeys = WorldValidation.CopyKeys(
            readResourceKeys,
            nameof(readResourceKeys));
        WriteResourceKeys = WorldValidation.CopyKeys(
            writeResourceKeys,
            nameof(writeResourceKeys));
        if (ReadResourceKeys.Count + WriteResourceKeys.Count
            > WorldValidation.MaximumResourceKeys)
        {
            throw new ArgumentException(
                "The resolution exceeds its combined resource-key limit.");
        }

        Parameters = WorldValidation.CopyParameters(
            parameters,
            nameof(parameters));
    }

    public string ResolutionKey { get; }

    public IReadOnlyList<WorldEventParticipant> Participants { get; }

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    private static IReadOnlyList<WorldEventParticipant> CopyParticipants(
        IEnumerable<WorldEventParticipant>? participants)
    {
        if (participants is null)
        {
            return Array.Empty<WorldEventParticipant>();
        }

        var copy = WorldValidation.MaterializeBounded(
                participants,
                WorldValidation.MaximumParticipants,
                nameof(participants))
            .Select(
                item => item
                        ?? throw new ArgumentException(
                            "Participants cannot contain null entries.",
                            nameof(participants)))
            .OrderBy(item => item.StableKey, StringComparer.Ordinal)
            .ToArray();

        for (var index = 1; index < copy.Length; index++)
        {
            if (string.Equals(
                    copy[index - 1].StableKey,
                    copy[index].StableKey,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The participant collection contains duplicates.",
                    nameof(participants));
            }
        }

        return new ReadOnlyCollection<WorldEventParticipant>(copy);
    }
}

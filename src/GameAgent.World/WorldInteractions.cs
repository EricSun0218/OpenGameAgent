using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public static class WorldInteractionKinds
{
    public const string Requested = "interaction_requested";

    public const string InputSchemaAttribute =
        "interaction.input_schema";
}

/// <summary>
/// A typed request for a host-defined interaction. The actor, target, schema,
/// and input remain structured and do not imply any particular game system.
/// </summary>
public sealed class InteractionRequestedTrigger : WorldEvolutionTrigger
{
    public InteractionRequestedTrigger(
        string requestId,
        string worldId,
        string timelineId,
        long timelineEpoch,
        GameEntityIdentity actor,
        string inputSchemaId,
        JsonElement input,
        GameEntityIdentity? target = null,
        string? confirmationToken = null,
        GameTimePoint? gameTime = null)
        : base(
            requestId,
            WorldInteractionKinds.Requested,
            worldId,
            timelineId,
            timelineEpoch,
            gameTime,
            BuildPayload(
                actor,
                target,
                inputSchemaId,
                input,
                confirmationToken))
    {
        Actor = actor
                ?? throw new ArgumentNullException(nameof(actor));
        Target = target;
        InputSchemaId = WorldValidation.Required(
            inputSchemaId,
            nameof(inputSchemaId));
        Input = Payload!.Value.GetProperty("input").Clone();
        ConfirmationToken = WorldValidation.Optional(
            confirmationToken,
            nameof(confirmationToken),
            512);
    }

    public GameEntityIdentity Actor { get; }

    public GameEntityIdentity? Target { get; }

    public string InputSchemaId { get; }

    public JsonElement Input { get; }

    public string? ConfirmationToken { get; }

    private static JsonElement BuildPayload(
        GameEntityIdentity actor,
        GameEntityIdentity? target,
        string inputSchemaId,
        JsonElement input,
        string? confirmationToken)
    {
        if (actor is null)
        {
            throw new ArgumentNullException(nameof(actor));
        }

        _ = WorldValidation.Required(
            inputSchemaId,
            nameof(inputSchemaId));
        _ = WorldValidation.Optional(
            confirmationToken,
            nameof(confirmationToken),
            512);
        JsonValueInspector.ValidateAndMeasure(
            input,
            InteractionJsonLimits.Parameters,
            nameof(input));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteIdentity(writer, "actor", actor);
            if (target is not null)
            {
                WriteIdentity(writer, "target", target);
            }

            writer.WriteString("inputSchemaId", inputSchemaId);
            if (confirmationToken is not null)
            {
                writer.WriteString("confirmationToken", confirmationToken);
            }

            writer.WritePropertyName("input");
            input.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteIdentity(
        Utf8JsonWriter writer,
        string propertyName,
        GameEntityIdentity identity)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteString("entityId", identity.EntityId);
        writer.WriteString(
            "incarnation",
            identity.Incarnation.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }
}

/// <summary>
/// Maps interaction admission and effect declarations onto the same event
/// planner used by every other trigger. Admission handlers only check state;
/// the effect handler applies cost and state changes atomically under the
/// declared resource keys.
/// </summary>
public sealed class InteractionDefinition
{
    public InteractionDefinition(
        string interactionId,
        string version,
        string inputSchemaId,
        int priority,
        string availabilityHandlerId,
        string costAdmissionHandlerId,
        string participantSelectorId,
        string resolverId,
        string effectHandlerId,
        string? confirmationAdmissionHandlerId = null,
        IEnumerable<string>? readResourceKeys = null,
        IEnumerable<string>? writeResourceKeys = null,
        WorldEventCooldown? cooldown = null,
        int? maximumOccurrences = null,
        int? maximumParticipants = null,
        WorldAgentInvocationPolicy agentInvocationPolicy =
            WorldAgentInvocationPolicy.None,
        InteractionDefinitionDetails? details = null)
    {
        InteractionId = WorldValidation.Required(
            interactionId,
            nameof(interactionId));
        Version = WorldValidation.Required(version, nameof(version), 96);
        InputSchemaId = WorldValidation.Required(
            inputSchemaId,
            nameof(inputSchemaId));
        AvailabilityHandlerId = WorldValidation.Required(
            availabilityHandlerId,
            nameof(availabilityHandlerId));
        CostAdmissionHandlerId = WorldValidation.Required(
            costAdmissionHandlerId,
            nameof(costAdmissionHandlerId));
        ParticipantSelectorId = WorldValidation.Required(
            participantSelectorId,
            nameof(participantSelectorId));
        ResolverId = WorldValidation.Required(
            resolverId,
            nameof(resolverId));
        EffectHandlerId = WorldValidation.Required(
            effectHandlerId,
            nameof(effectHandlerId));
        ConfirmationAdmissionHandlerId = WorldValidation.Optional(
            confirmationAdmissionHandlerId,
            nameof(confirmationAdmissionHandlerId));
        Priority = priority;
        ReadResourceKeys = WorldValidation.CopyKeys(
            readResourceKeys,
            nameof(readResourceKeys));
        WriteResourceKeys = WorldValidation.CopyKeys(
            writeResourceKeys,
            nameof(writeResourceKeys));
        Cooldown = cooldown
                   ?? (details?.Cooldown is null
                       ? null
                       : new WorldEventCooldown(
                           details.Cooldown.MinimumTicks));
        MaximumOccurrences = maximumOccurrences;
        MaximumParticipants = maximumParticipants;
        AgentInvocationPolicy = agentInvocationPolicy;
        Details = details;
        if (cooldown is not null
            && details?.Cooldown is not null
            && cooldown.MinimumTicks != details.Cooldown.MinimumTicks)
        {
            throw new ArgumentException(
                "The event and interaction cooldown declarations must "
                + "use the same tick count.",
                nameof(details));
        }

        if (details is not null
            && !string.Equals(
                InputSchemaId,
                details.ParameterContract.SchemaId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The interaction input schema ID must match the "
                + "parameter contract schema ID.",
                nameof(details));
        }

        _ = ToEventDefinition();
        ContentDigest =
            InteractionCanonicalJson.ComputeDefinitionDigest(this);
    }

    public string InteractionId { get; }

    public string Version { get; }

    public string InputSchemaId { get; }

    public int Priority { get; }

    public string AvailabilityHandlerId { get; }

    public string CostAdmissionHandlerId { get; }

    public string? ConfirmationAdmissionHandlerId { get; }

    public string ParticipantSelectorId { get; }

    public string ResolverId { get; }

    public string EffectHandlerId { get; }

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public WorldEventCooldown? Cooldown { get; }

    public int? MaximumOccurrences { get; }

    public int? MaximumParticipants { get; }

    public WorldAgentInvocationPolicy AgentInvocationPolicy { get; }

    public InteractionDefinitionDetails? Details { get; }

    public string ContentRevision => Details?.ContentRevision ?? Version;

    public string ContentDigest { get; }

    public WorldEventDefinition ToEventDefinition()
    {
        var admissions = new List<string>
        {
            CostAdmissionHandlerId
        };
        if (ConfirmationAdmissionHandlerId is not null)
        {
            admissions.Add(ConfirmationAdmissionHandlerId);
        }

        return new WorldEventDefinition(
            InteractionId,
            Version,
            WorldInteractionKinds.Requested,
            Priority,
            AvailabilityHandlerId,
            ParticipantSelectorId,
            ResolverId,
            EffectHandlerId,
            ReadResourceKeys,
            WriteResourceKeys,
            Cooldown,
            MaximumOccurrences,
            MaximumParticipants,
            AgentInvocationPolicy,
            admissions,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorldInteractionKinds.InputSchemaAttribute] =
                    InputSchemaId
            });
    }
}

using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

internal static class InteractionCanonicalJson
{
    private const long MaximumDefinitionBytes = 8L * 1024 * 1024;

    public static string ComputeDefinitionDigest(
        InteractionDefinition definition)
    {
        using var buffer = new MemoryStream();
        using var boundedBuffer = new WorldBoundedArchiveWriteStream(
            buffer,
            MaximumDefinitionBytes,
            WorldDataReasonCodes.ByteLimitExceeded,
            "The interaction definition exceeds its byte limit.");
        try
        {
            using (var writer = new Utf8JsonWriter(boundedBuffer))
            {
                WriteDefinition(writer, definition);
            }
        }
        catch (WorldDataContractException exception)
            when (exception.ReasonCode
                  == WorldDataReasonCodes.ByteLimitExceeded)
        {
            throw new ArgumentException(
                "The interaction definition exceeds its byte limit.",
                nameof(definition));
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return WorldLargeCanonicalJsonDigest.Compute(
            document.RootElement,
            MaximumDefinitionBytes,
            nameof(definition));
    }

    public static void WriteDefinition(
        Utf8JsonWriter writer,
        InteractionDefinition definition)
    {
        writer.WriteStartObject();
        writer.WriteString("interactionId", definition.InteractionId);
        writer.WriteString("version", definition.Version);
        writer.WriteString("contentRevision", definition.ContentRevision);
        writer.WriteString("inputSchemaId", definition.InputSchemaId);
        writer.WriteNumber("priority", definition.Priority);
        writer.WriteString(
            "availabilityHandlerId",
            definition.AvailabilityHandlerId);
        writer.WriteString(
            "costAdmissionHandlerId",
            definition.CostAdmissionHandlerId);
        WriteOptionalString(
            writer,
            "confirmationAdmissionHandlerId",
            definition.ConfirmationAdmissionHandlerId);
        writer.WriteString(
            "participantSelectorId",
            definition.ParticipantSelectorId);
        writer.WriteString("resolverId", definition.ResolverId);
        writer.WriteString("effectHandlerId", definition.EffectHandlerId);
        WriteStrings(writer, "readResourceKeys", definition.ReadResourceKeys);
        WriteStrings(
            writer,
            "writeResourceKeys",
            definition.WriteResourceKeys);
        if (definition.Cooldown is not null)
        {
            writer.WriteString(
                "minimumCooldownTicks",
                definition.Cooldown.MinimumTicks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        if (definition.MaximumOccurrences.HasValue)
        {
            writer.WriteNumber(
                "maximumOccurrences",
                definition.MaximumOccurrences.Value);
        }

        if (definition.MaximumParticipants.HasValue)
        {
            writer.WriteNumber(
                "maximumParticipants",
                definition.MaximumParticipants.Value);
        }

        writer.WriteNumber(
            "agentInvocationPolicy",
            (int)definition.AgentInvocationPolicy);
        if (definition.Details is not null)
        {
            WriteDetails(writer, definition.Details);
        }

        writer.WriteEndObject();
    }

    private static void WriteDetails(
        Utf8JsonWriter writer,
        InteractionDefinitionDetails details)
    {
        writer.WritePropertyName("details");
        writer.WriteStartObject();
        writer.WriteString(
            "parameterSchemaId",
            details.ParameterContract.SchemaId);
        writer.WriteString(
            "parameterSchemaVersion",
            details.ParameterContract.SchemaVersion);
        writer.WritePropertyName("parameterSchema");
        details.ParameterContract.Schema.WriteTo(writer);
        if (details.TargetContract is not null)
        {
            writer.WritePropertyName("target");
            writer.WriteStartObject();
            writer.WriteString(
                "schemaId",
                details.TargetContract.SchemaId);
            writer.WriteNumber(
                "minimumTargets",
                details.TargetContract.MinimumTargets);
            writer.WriteNumber(
                "maximumTargets",
                details.TargetContract.MaximumTargets);
            writer.WriteEndObject();
        }

        WriteStrings(writer, "channelIds", details.ChannelIds);
        WriteStrings(writer, "tags", details.Tags);
        WriteStrings(
            writer,
            "requiredCapabilities",
            details.RequiredCapabilities);
        writer.WritePropertyName("costs");
        writer.WriteStartArray();
        foreach (var cost in details.Costs)
        {
            writer.WriteStartObject();
            writer.WriteString("costId", cost.CostId);
            writer.WriteString("numericPath", cost.NumericPath);
            writer.WriteString(
                "numericSchemaId",
                cost.NumericSchemaId);
            writer.WriteString(
                "amount",
                cost.Amount.CanonicalUnits);
            writer.WriteNumber("scale", cost.Amount.Scale);
            writer.WriteString(
                "insufficientReasonCode",
                cost.InsufficientReasonCode);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (details.Cooldown is not null)
        {
            writer.WritePropertyName("cooldown");
            writer.WriteStartObject();
            writer.WriteString("clockId", details.Cooldown.ClockId);
            writer.WriteString(
                "minimumTicks",
                details.Cooldown.MinimumTicks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString(
                "scopeKeyId",
                details.Cooldown.ScopeKeyId);
            writer.WriteEndObject();
        }

        if (details.Duration is not null)
        {
            writer.WritePropertyName("duration");
            writer.WriteStartObject();
            writer.WriteString("clockId", details.Duration.ClockId);
            writer.WriteString(
                "ticks",
                details.Duration.Ticks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString(
                "completionTriggerKind",
                details.Duration.CompletionTriggerKind);
            writer.WriteEndObject();
        }

        writer.WritePropertyName("steps");
        writer.WriteStartArray();
        foreach (var step in details.Steps)
        {
            writer.WriteStartObject();
            writer.WriteString("stepId", step.StepId);
            writer.WriteString(
                "effectHandlerId",
                step.EffectHandlerId);
            writer.WritePropertyName("parameters");
            step.Parameters.WriteTo(writer);
            WriteStrings(
                writer,
                "readResourceKeys",
                step.ReadResourceKeys);
            WriteStrings(
                writer,
                "writeResourceKeys",
                step.WriteResourceKeys);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteOptionalString(
            writer,
            "visibilityHandlerId",
            details.VisibilityHandlerId);
        writer.WritePropertyName("presentation");
        writer.WriteStartObject();
        foreach (var pair in details.Presentation)
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is not null)
        {
            writer.WriteString(propertyName, value);
        }
    }
}

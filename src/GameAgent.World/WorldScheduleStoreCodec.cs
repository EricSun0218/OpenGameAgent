using System.Globalization;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

internal static class WorldScheduleStoreCodec
{
    private static readonly HashSet<string> RecordFields =
        Fields(
            "intent",
            "generation",
            "status",
            "occurrenceId",
            "claim",
            "recordDigest");

    private static readonly HashSet<string> IntentFields =
        Fields(
            "scheduleId",
            "worldId",
            "timelineId",
            "timelineEpoch",
            "dueAt",
            "owner",
            "payloadSchemaId",
            "payloadSchemaVersion",
            "payloadSchemaDigest",
            "payloadSchema",
            "payloadDigest",
            "payload",
            "semanticDigest");

    private static readonly HashSet<string> TimeFields =
        Fields("clockId", "timelineId", "epoch", "tick");

    private static readonly HashSet<string> OwnerFields =
        Fields("entityId", "incarnation");

    private static readonly HashSet<string> ClaimFields =
        Fields("claimantId", "claimToken", "operationId");

    private static readonly HashSet<string> ReceiptFields =
        Fields(
            "worldId",
            "timelineId",
            "timelineEpoch",
            "scheduleId",
            "operationId",
            "kind",
            "requestFingerprint",
            "applied",
            "outcomeCode",
            "resultingGeneration",
            "resultingStatus",
            "occurrenceId",
            "claim",
            "receiptId");

    public static void WriteRecord(
        Utf8JsonWriter writer,
        WorldScheduleRecord record)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("intent");
        WriteIntent(writer, record.Intent);
        writer.WriteNumber("generation", record.Generation);
        writer.WriteNumber("status", (int)record.Status);
        writer.WriteString("occurrenceId", record.OccurrenceId);
        writer.WritePropertyName("claim");
        WriteClaim(writer, record.Claim);
        writer.WriteString("recordDigest", record.RecordDigest);
        writer.WriteEndObject();
    }

    public static WorldScheduleRecord ReadRecord(JsonElement value)
    {
        RequireOnly(value, RecordFields);
        var intent = ReadIntent(RequiredObject(value, "intent"));
        var statusValue = RequiredInt64(
            value,
            "status",
            minimum: 0);
        if (statusValue > (int)WorldScheduleStatus.Completed)
        {
            throw Invalid("A schedule status is invalid.");
        }

        var record = new WorldScheduleRecord(
            intent,
            RequiredInt64(value, "generation", minimum: 0),
            (WorldScheduleStatus)statusValue,
            ReadClaim(RequiredProperty(value, "claim")));
        if (!string.Equals(
                record.OccurrenceId,
                RequiredString(value, "occurrenceId", 192),
                StringComparison.Ordinal)
            || !string.Equals(
                record.RecordDigest,
                RequiredString(value, "recordDigest", 64),
                StringComparison.Ordinal))
        {
            throw Invalid(
                "A schedule occurrence or record digest is invalid.");
        }

        return record;
    }

    public static void WriteReceipt(
        Utf8JsonWriter writer,
        WorldScheduleOperationReceipt receipt)
    {
        writer.WriteStartObject();
        writer.WriteString("worldId", receipt.Scope.WorldId);
        writer.WriteString(
            "timelineId",
            receipt.Scope.TimelineId);
        writer.WriteNumber(
            "timelineEpoch",
            receipt.Scope.TimelineEpoch);
        writer.WriteString("scheduleId", receipt.ScheduleId);
        writer.WriteString("operationId", receipt.OperationId);
        writer.WriteNumber("kind", (int)receipt.Kind);
        writer.WriteString(
            "requestFingerprint",
            receipt.RequestFingerprint);
        writer.WriteBoolean("applied", receipt.Applied);
        writer.WriteString("outcomeCode", receipt.OutcomeCode);
        WriteOptionalNumber(
            writer,
            "resultingGeneration",
            receipt.ResultingGeneration);
        if (receipt.ResultingStatus.HasValue)
        {
            writer.WriteNumber(
                "resultingStatus",
                (int)receipt.ResultingStatus.Value);
        }
        else
        {
            writer.WriteNull("resultingStatus");
        }

        WriteOptionalString(
            writer,
            "occurrenceId",
            receipt.OccurrenceId);
        writer.WritePropertyName("claim");
        WriteClaim(writer, receipt.Claim);
        writer.WriteString("receiptId", receipt.ReceiptId);
        writer.WriteEndObject();
    }

    public static WorldScheduleOperationReceipt ReadReceipt(
        JsonElement value)
    {
        RequireOnly(value, ReceiptFields);
        var kindValue = RequiredInt64(value, "kind", minimum: 0);
        if (kindValue > (int)WorldScheduleOperationKind.Reassign)
        {
            throw Invalid("A schedule operation kind is invalid.");
        }

        WorldScheduleStatus? resultingStatus = null;
        var statusValue = RequiredProperty(
            value,
            "resultingStatus");
        if (statusValue.ValueKind == JsonValueKind.Number)
        {
            if (!statusValue.TryGetInt32(out var rawStatus)
                || rawStatus < 0
                || rawStatus > (int)WorldScheduleStatus.Completed)
            {
                throw Invalid(
                    "A resulting schedule status is invalid.");
            }

            resultingStatus = (WorldScheduleStatus)rawStatus;
        }
        else if (statusValue.ValueKind != JsonValueKind.Null)
        {
            throw Invalid(
                "A resulting schedule status is invalid.");
        }

        long? resultingGeneration = null;
        var generationValue = RequiredProperty(
            value,
            "resultingGeneration");
        if (generationValue.ValueKind == JsonValueKind.Number)
        {
            if (!generationValue.TryGetInt64(out var rawGeneration)
                || rawGeneration < 0)
            {
                throw Invalid(
                    "A resulting schedule generation is invalid.");
            }

            resultingGeneration = rawGeneration;
        }
        else if (generationValue.ValueKind != JsonValueKind.Null)
        {
            throw Invalid(
                "A resulting schedule generation is invalid.");
        }

        var receipt = new WorldScheduleOperationReceipt(
            new WorldTransactionScope(
                RequiredString(value, "worldId", 256),
                RequiredString(value, "timelineId", 256),
                RequiredInt64(
                    value,
                    "timelineEpoch",
                    minimum: 0)),
            RequiredString(value, "scheduleId", 192),
            RequiredString(value, "operationId", 192),
            (WorldScheduleOperationKind)kindValue,
            RequiredString(
                value,
                "requestFingerprint",
                64),
            RequiredBoolean(value, "applied"),
            RequiredString(value, "outcomeCode", 96),
            resultingGeneration,
            resultingStatus,
            OptionalString(value, "occurrenceId", 192),
            ReadClaim(RequiredProperty(value, "claim")));
        if (!string.Equals(
                receipt.ReceiptId,
                RequiredString(value, "receiptId", 64),
                StringComparison.Ordinal))
        {
            throw Invalid(
                "A schedule operation receipt identity is invalid.");
        }

        return receipt;
    }

    private static void WriteIntent(
        Utf8JsonWriter writer,
        WorldScheduleIntent intent)
    {
        writer.WriteStartObject();
        writer.WriteString("scheduleId", intent.ScheduleId);
        writer.WriteString("worldId", intent.Scope.WorldId);
        writer.WriteString("timelineId", intent.Scope.TimelineId);
        writer.WriteNumber(
            "timelineEpoch",
            intent.Scope.TimelineEpoch);
        writer.WritePropertyName("dueAt");
        WriteTime(writer, intent.DueAt);
        writer.WritePropertyName("owner");
        writer.WriteStartObject();
        writer.WriteString("entityId", intent.Owner.EntityId);
        writer.WriteNumber(
            "incarnation",
            intent.Owner.Incarnation);
        writer.WriteEndObject();
        writer.WriteString(
            "payloadSchemaId",
            intent.PayloadSchemaId);
        writer.WriteString(
            "payloadSchemaVersion",
            intent.PayloadSchemaVersion);
        writer.WriteString(
            "payloadSchemaDigest",
            intent.PayloadSchemaDigest);
        writer.WritePropertyName("payloadSchema");
        intent.PayloadSchema.WriteTo(writer);
        writer.WriteString(
            "payloadDigest",
            intent.PayloadDigest);
        writer.WritePropertyName("payload");
        intent.Payload.WriteTo(writer);
        writer.WriteString(
            "semanticDigest",
            intent.SemanticDigest);
        writer.WriteEndObject();
    }

    private static WorldScheduleIntent ReadIntent(JsonElement value)
    {
        RequireOnly(value, IntentFields);
        var due = ReadTime(RequiredObject(value, "dueAt"));
        var ownerValue = RequiredObject(value, "owner");
        RequireOnly(ownerValue, OwnerFields);
        var intent = new WorldScheduleIntent(
            RequiredString(value, "scheduleId", 192),
            new WorldTransactionScope(
                RequiredString(value, "worldId", 256),
                RequiredString(value, "timelineId", 256),
                RequiredInt64(
                    value,
                    "timelineEpoch",
                    minimum: 0)),
            due,
            new GameEntityIdentity(
                RequiredString(ownerValue, "entityId", 128),
                RequiredInt64(
                    ownerValue,
                    "incarnation",
                    minimum: 0)),
            RequiredString(
                value,
                "payloadSchemaId",
                192),
            RequiredString(
                value,
                "payloadSchemaVersion",
                96),
            RequiredProperty(value, "payloadSchema"),
            RequiredProperty(value, "payload"));
        if (!string.Equals(
                intent.PayloadSchemaDigest,
                RequiredString(
                    value,
                    "payloadSchemaDigest",
                    64),
                StringComparison.Ordinal)
            || !string.Equals(
                intent.PayloadDigest,
                RequiredString(value, "payloadDigest", 64),
                StringComparison.Ordinal)
            || !string.Equals(
                intent.SemanticDigest,
                RequiredString(value, "semanticDigest", 64),
                StringComparison.Ordinal))
        {
            throw Invalid(
                "A schedule intent digest is invalid.");
        }

        return intent;
    }

    private static void WriteTime(
        Utf8JsonWriter writer,
        GameTimePoint value)
    {
        writer.WriteStartObject();
        writer.WriteString("clockId", value.ClockId);
        writer.WriteString("timelineId", value.TimelineId);
        writer.WriteNumber("epoch", value.Epoch);
        writer.WriteNumber("tick", value.Tick);
        writer.WriteEndObject();
    }

    private static GameTimePoint ReadTime(JsonElement value)
    {
        RequireOnly(value, TimeFields);
        return new GameTimePoint(
            RequiredString(value, "clockId", 128),
            RequiredString(value, "timelineId", 256),
            RequiredInt64(value, "epoch", minimum: 0),
            RequiredInt64(value, "tick"));
    }

    private static void WriteClaim(
        Utf8JsonWriter writer,
        WorldScheduleClaim? claim)
    {
        if (claim is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("claimantId", claim.ClaimantId);
        writer.WriteString("claimToken", claim.ClaimToken);
        writer.WriteString("operationId", claim.OperationId);
        writer.WriteEndObject();
    }

    private static WorldScheduleClaim? ReadClaim(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireOnly(value, ClaimFields);
        return new WorldScheduleClaim(
            RequiredString(value, "claimantId", 192),
            RequiredString(value, "claimToken", 192),
            RequiredString(value, "operationId", 192));
    }

    private static void WriteOptionalNumber(
        Utf8JsonWriter writer,
        string propertyName,
        long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static JsonElement RequiredProperty(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            throw Invalid("A schedule field is missing.");
        }

        return value;
    }

    private static JsonElement RequiredObject(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredProperty(parent, propertyName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("A schedule object field is invalid.");
        }

        return value;
    }

    private static string RequiredString(
        JsonElement parent,
        string propertyName,
        int maximumUtf8Bytes)
    {
        try
        {
            return WorldDataJson.RequiredString(
                parent,
                propertyName,
                maximumUtf8Bytes);
        }
        catch (WorldDataContractException exception)
        {
            throw Invalid(
                "A schedule string field is invalid.",
                exception);
        }
    }

    private static string? OptionalString(
        JsonElement parent,
        string propertyName,
        int maximumUtf8Bytes)
    {
        var value = RequiredProperty(parent, propertyName);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(
                "A schedule optional string field is invalid.");
        }

        return WorldValidation.Required(
            value.GetString(),
            propertyName,
            maximumUtf8Bytes);
    }

    private static long RequiredInt64(
        JsonElement parent,
        string propertyName,
        long minimum = long.MinValue)
    {
        try
        {
            return WorldDataJson.RequiredInt64(
                parent,
                propertyName,
                minimum);
        }
        catch (WorldDataContractException exception)
        {
            throw Invalid(
                "A schedule integer field is invalid.",
                exception);
        }
    }

    private static bool RequiredBoolean(
        JsonElement parent,
        string propertyName)
    {
        var value = RequiredProperty(parent, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(
                "A schedule Boolean field is invalid.")
        };
    }

    private static void RequireOnly(
        JsonElement value,
        ISet<string> fields)
    {
        WorldDataJson.RequireOnlyProperties(value, fields);
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != fields.Count)
        {
            throw Invalid(
                "A schedule object has missing fields.");
        }
    }

    private static HashSet<string> Fields(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private static WorldScheduleStoreException Invalid(
        string message,
        Exception? innerException = null)
    {
        return new WorldScheduleStoreException(
            WorldScheduleReasonCodes.CorruptStore,
            message,
            innerException);
    }
}

using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public static class WorldMutationKinds
{
    public const string Value = "value";

    public const string Numeric = "numeric";

    public const string Transfer = "transfer";

    public const string Relationship = "relationship";
}

public sealed class WorldMutationValidationException : ArgumentException
{
    public WorldMutationValidationException(
        string reasonCode,
        string message,
        string parameterName)
        : base(message, parameterName)
    {
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
    }

    public string ReasonCode { get; }
}

public interface IWorldMutationIntent
{
    string IntentId { get; }

    string Kind { get; }

    IReadOnlyList<string> ReadResourceKeys { get; }

    IReadOnlyList<string> WriteResourceKeys { get; }

    JsonElement ToPortableJson();
}

public enum WorldValueMutationKind
{
    Set = 0,
    Remove = 1
}

public sealed class WorldValueMutationIntent : IWorldMutationIntent
{
    private readonly JsonElement? _value;

    public WorldValueMutationIntent(
        string intentId,
        GameEntityIdentity entity,
        string componentPath,
        string resourceKey,
        WorldValueMutationKind mutationKind,
        JsonElement? value = null,
        string? expectedValueDigest = null)
    {
        IntentId = WorldValidation.Required(intentId, nameof(intentId));
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        ComponentPath = WorldValidation.Required(
            componentPath,
            nameof(componentPath),
            512);
        if (!Enum.IsDefined(typeof(WorldValueMutationKind), mutationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mutationKind));
        }

        if ((mutationKind == WorldValueMutationKind.Set) != value.HasValue)
        {
            throw new ArgumentException(
                "Set requires a value and remove forbids one.",
                nameof(value));
        }

        if (value.HasValue)
        {
            WorldAuthoritativeJson.Validate(value.Value, nameof(value));
            _value = value.Value.Clone();
        }

        if (expectedValueDigest is not null
            && !CanonicalJsonDigest.IsSha256(expectedValueDigest))
        {
            throw new ArgumentException(
                "Expected value digest must be a lowercase SHA-256 digest.",
                nameof(expectedValueDigest));
        }

        ExpectedValueDigest = expectedValueDigest;
        MutationKind = mutationKind;
        ReadResourceKeys = WorldValidation.CopyKeys(
            new[] { resourceKey },
            nameof(resourceKey));
        WriteResourceKeys = ReadResourceKeys;
    }

    public string IntentId { get; }

    public string Kind => WorldMutationKinds.Value;

    public GameEntityIdentity Entity { get; }

    public string ComponentPath { get; }

    public WorldValueMutationKind MutationKind { get; }

    public JsonElement? Value => _value?.Clone();

    public string? ExpectedValueDigest { get; }

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public JsonElement ToPortableJson()
    {
        return WorldMutationJson.Write(
            writer =>
            {
                WorldMutationJson.WriteHeader(writer, this);
                WorldMutationJson.WriteIdentity(
                    writer,
                    "entity",
                    Entity);
                writer.WriteString("componentPath", ComponentPath);
                writer.WriteNumber("mutationKind", (int)MutationKind);
                if (_value.HasValue)
                {
                    writer.WritePropertyName("value");
                    _value.Value.WriteTo(writer);
                }

                if (ExpectedValueDigest is not null)
                {
                    writer.WriteString(
                        "expectedValueDigest",
                        ExpectedValueDigest);
                }

                WorldMutationJson.WriteResources(writer, this);
            });
    }
}

public enum WorldNumericMutationKind
{
    Set = 0,
    Add = 1,
    Subtract = 2,
    Consume = 3
}

public sealed class WorldNumericMutationIntent : IWorldMutationIntent
{
    public WorldNumericMutationIntent(
        string intentId,
        GameEntityIdentity entity,
        string numericPath,
        string resourceKey,
        string numericSchemaId,
        WorldNumericMutationKind mutationKind,
        WorldFixedPointValue operand)
    {
        IntentId = WorldValidation.Required(intentId, nameof(intentId));
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        NumericPath = WorldValidation.Required(
            numericPath,
            nameof(numericPath),
            512);
        NumericSchemaId = WorldValidation.Required(
            numericSchemaId,
            nameof(numericSchemaId));
        if (!Enum.IsDefined(
                typeof(WorldNumericMutationKind),
                mutationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mutationKind));
        }

        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
        if (mutationKind == WorldNumericMutationKind.Consume
            && operand.Units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operand));
        }

        MutationKind = mutationKind;
        ReadResourceKeys = WorldValidation.CopyKeys(
            new[] { resourceKey },
            nameof(resourceKey));
        WriteResourceKeys = ReadResourceKeys;
    }

    public string IntentId { get; }

    public string Kind => WorldMutationKinds.Numeric;

    public GameEntityIdentity Entity { get; }

    public string NumericPath { get; }

    public string NumericSchemaId { get; }

    public WorldNumericMutationKind MutationKind { get; }

    public WorldFixedPointValue Operand { get; }

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public JsonElement ToPortableJson()
    {
        return WorldMutationJson.Write(
            writer =>
            {
                WorldMutationJson.WriteHeader(writer, this);
                WorldMutationJson.WriteIdentity(
                    writer,
                    "entity",
                    Entity);
                writer.WriteString("numericPath", NumericPath);
                writer.WriteString(
                    "numericSchemaId",
                    NumericSchemaId);
                writer.WriteNumber("mutationKind", (int)MutationKind);
                writer.WriteString(
                    "operand",
                    Operand.CanonicalUnits);
                writer.WriteNumber("scale", Operand.Scale);
                WorldMutationJson.WriteResources(writer, this);
            });
    }
}

/// <summary>
/// One indivisible debit and credit intent between two explicit entity
/// incarnations. The framework does not assign meaning to either path.
/// </summary>
public sealed class WorldTransferMutationIntent : IWorldMutationIntent
{
    public WorldTransferMutationIntent(
        string intentId,
        GameEntityIdentity source,
        string sourceNumericPath,
        string sourceResourceKey,
        GameEntityIdentity target,
        string targetNumericPath,
        string targetResourceKey,
        string numericSchemaId,
        WorldFixedPointValue amount)
    {
        IntentId = WorldValidation.Required(intentId, nameof(intentId));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        SourceNumericPath = WorldValidation.Required(
            sourceNumericPath,
            nameof(sourceNumericPath),
            512);
        TargetNumericPath = WorldValidation.Required(
            targetNumericPath,
            nameof(targetNumericPath),
            512);
        NumericSchemaId = WorldValidation.Required(
            numericSchemaId,
            nameof(numericSchemaId));
        Amount = amount ?? throw new ArgumentNullException(nameof(amount));
        if (amount.Units <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        ReadResourceKeys = WorldValidation.CopyKeys(
            new[] { sourceResourceKey, targetResourceKey },
            nameof(sourceResourceKey));
        WriteResourceKeys = ReadResourceKeys;
    }

    public string IntentId { get; }

    public string Kind => WorldMutationKinds.Transfer;

    public GameEntityIdentity Source { get; }

    public string SourceNumericPath { get; }

    public GameEntityIdentity Target { get; }

    public string TargetNumericPath { get; }

    public string NumericSchemaId { get; }

    public WorldFixedPointValue Amount { get; }

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public JsonElement ToPortableJson()
    {
        return WorldMutationJson.Write(
            writer =>
            {
                WorldMutationJson.WriteHeader(writer, this);
                WorldMutationJson.WriteIdentity(
                    writer,
                    "source",
                    Source);
                writer.WriteString(
                    "sourceNumericPath",
                    SourceNumericPath);
                WorldMutationJson.WriteIdentity(
                    writer,
                    "target",
                    Target);
                writer.WriteString(
                    "targetNumericPath",
                    TargetNumericPath);
                writer.WriteString(
                    "numericSchemaId",
                    NumericSchemaId);
                writer.WriteString(
                    "amount",
                    Amount.CanonicalUnits);
                writer.WriteNumber("scale", Amount.Scale);
                WorldMutationJson.WriteResources(writer, this);
            });
    }
}

public enum WorldRelationshipMutationKind
{
    Upsert = 0,
    Remove = 1
}

/// <summary>
/// A directional edge mutation. No inverse or symmetric mutation is implied.
/// </summary>
public sealed class WorldRelationshipMutationIntent : IWorldMutationIntent
{
    private readonly JsonElement? _value;

    public WorldRelationshipMutationIntent(
        string intentId,
        GameEntityIdentity source,
        GameEntityIdentity target,
        string relationshipTypeId,
        string resourceKey,
        WorldRelationshipMutationKind mutationKind,
        JsonElement? value = null)
    {
        IntentId = WorldValidation.Required(intentId, nameof(intentId));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        RelationshipTypeId = WorldValidation.Required(
            relationshipTypeId,
            nameof(relationshipTypeId));
        if (!Enum.IsDefined(
                typeof(WorldRelationshipMutationKind),
                mutationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(mutationKind));
        }

        if ((mutationKind == WorldRelationshipMutationKind.Upsert)
            != value.HasValue)
        {
            throw new ArgumentException(
                "Upsert requires a value and remove forbids one.",
                nameof(value));
        }

        if (value.HasValue)
        {
            WorldAuthoritativeJson.Validate(value.Value, nameof(value));
            _value = value.Value.Clone();
        }

        MutationKind = mutationKind;
        ReadResourceKeys = WorldValidation.CopyKeys(
            new[] { resourceKey },
            nameof(resourceKey));
        WriteResourceKeys = ReadResourceKeys;
    }

    public string IntentId { get; }

    public string Kind => WorldMutationKinds.Relationship;

    public GameEntityIdentity Source { get; }

    public GameEntityIdentity Target { get; }

    public string RelationshipTypeId { get; }

    public WorldRelationshipMutationKind MutationKind { get; }

    public JsonElement? Value => _value?.Clone();

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public JsonElement ToPortableJson()
    {
        return WorldMutationJson.Write(
            writer =>
            {
                WorldMutationJson.WriteHeader(writer, this);
                WorldMutationJson.WriteIdentity(
                    writer,
                    "source",
                    Source);
                WorldMutationJson.WriteIdentity(
                    writer,
                    "target",
                    Target);
                writer.WriteString(
                    "relationshipTypeId",
                    RelationshipTypeId);
                writer.WriteNumber("mutationKind", (int)MutationKind);
                if (_value.HasValue)
                {
                    writer.WritePropertyName("value");
                    _value.Value.WriteTo(writer);
                }

                WorldMutationJson.WriteResources(writer, this);
            });
    }
}

/// <summary>
/// A complete all-or-nothing mutation proposal bound to one exact world
/// coordinate and catalog snapshot.
/// </summary>
public sealed class WorldAtomicMutationSet
{
    private static readonly JsonValueLimits PortableJsonLimits = new(
        maxUtf8Bytes: 1_048_576,
        maxDepth: 32,
        maxNodes: 32_768,
        maxStringUtf8Bytes: 65_536,
        maxContainerItems: 1_024);

    private readonly IReadOnlyList<IWorldMutationIntent> _intents;

    public WorldAtomicMutationSet(
        string commandId,
        string operationId,
        string worldId,
        string timelineId,
        long timelineEpoch,
        long expectedSaveRevision,
        string expectedStateVersion,
        string catalogDigest,
        IEnumerable<IWorldMutationIntent> intents)
    {
        CommandId = WorldValidation.Required(
            commandId,
            nameof(commandId));
        OperationId = WorldValidation.Required(
            operationId,
            nameof(operationId));
        WorldId = WorldValidation.Required(worldId, nameof(worldId));
        TimelineId = WorldValidation.Required(
            timelineId,
            nameof(timelineId));
        if (timelineEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEpoch));
        }

        if (expectedSaveRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedSaveRevision));
        }

        ExpectedStateVersion = WorldValidation.Required(
            expectedStateVersion,
            nameof(expectedStateVersion));
        if (!CanonicalJsonDigest.IsSha256(catalogDigest))
        {
            throw new ArgumentException(
                "Catalog digest must be a lowercase SHA-256 digest.",
                nameof(catalogDigest));
        }

        if (intents is null)
        {
            throw new ArgumentNullException(nameof(intents));
        }

        var copy = WorldValidation.MaterializeBounded(
                intents,
                512,
                nameof(intents))
            .Select(
                intent => intent
                          ?? throw new ArgumentException(
                              "Intents cannot contain null entries.",
                              nameof(intents)))
            .ToArray();
        if (copy.Length < 1)
        {
            throw new ArgumentException(
                "An atomic mutation set requires 1 through 512 intents.",
                nameof(intents));
        }

        if (copy.Select(intent => intent.IntentId)
            .Distinct(StringComparer.Ordinal)
            .Count() != copy.Length)
        {
            throw new ArgumentException(
                "An atomic mutation set contains duplicate intent IDs.",
                nameof(intents));
        }

        TimelineEpoch = timelineEpoch;
        ExpectedSaveRevision = expectedSaveRevision;
        CatalogDigest = catalogDigest;
        _intents = new ReadOnlyCollection<IWorldMutationIntent>(copy);
        ReadResourceKeys = WorldValidation.CopyKeys(
            copy.SelectMany(intent => intent.ReadResourceKeys)
                .Distinct(StringComparer.Ordinal),
            nameof(intents));
        WriteResourceKeys = WorldValidation.CopyKeys(
            copy.SelectMany(intent => intent.WriteResourceKeys)
                .Distinct(StringComparer.Ordinal),
            nameof(intents));
        PortableJson = BuildPortableJson(nameof(intents));
        JsonValueInspector.ValidateAndMeasure(
            PortableJson,
            PortableJsonLimits,
            nameof(intents));
        Digest = ComputeDigest();
    }

    public string CommandId { get; }

    public string OperationId { get; }

    public string WorldId { get; }

    public string TimelineId { get; }

    public long TimelineEpoch { get; }

    public long ExpectedSaveRevision { get; }

    public string ExpectedStateVersion { get; }

    public string CatalogDigest { get; }

    public IReadOnlyList<IWorldMutationIntent> Intents => _intents;

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }

    public JsonElement PortableJson { get; }

    public string Digest { get; }

    private JsonElement BuildPortableJson(string parameterName)
    {
        return WorldMutationJson.Write(
            writer =>
            {
                writer.WriteString("commandId", CommandId);
                writer.WriteString("operationId", OperationId);
                writer.WriteString("worldId", WorldId);
                writer.WriteString("timelineId", TimelineId);
                writer.WriteString(
                    "timelineEpoch",
                    TimelineEpoch.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture));
                writer.WriteString(
                    "expectedSaveRevision",
                    ExpectedSaveRevision.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture));
                writer.WriteString(
                    "expectedStateVersion",
                    ExpectedStateVersion);
                writer.WriteString("catalogDigest", CatalogDigest);
                writer.WritePropertyName("intents");
                writer.WriteStartArray();
                foreach (var intent in _intents)
                {
                    intent.ToPortableJson().WriteTo(writer);
                }

                writer.WriteEndArray();
            },
            PortableJsonLimits.MaxUtf8Bytes,
            parameterName);
    }

    private string ComputeDigest()
    {
        var binding = WorldMutationJson.Write(
            writer =>
            {
                writer.WriteString("commandId", CommandId);
                writer.WriteString("operationId", OperationId);
                writer.WriteString("worldId", WorldId);
                writer.WriteString("timelineId", TimelineId);
                writer.WriteString(
                    "timelineEpoch",
                    TimelineEpoch.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture));
                writer.WriteString(
                    "expectedSaveRevision",
                    ExpectedSaveRevision.ToString(
                        System.Globalization.CultureInfo
                            .InvariantCulture));
                writer.WriteString(
                    "expectedStateVersion",
                    ExpectedStateVersion);
                writer.WriteString("catalogDigest", CatalogDigest);
                writer.WritePropertyName("intentDigests");
                writer.WriteStartArray();
                foreach (var intent in _intents)
                {
                    writer.WriteStringValue(
                        CanonicalJsonDigest.ComputeSha256(
                            intent.ToPortableJson()));
                }

                writer.WriteEndArray();
            });
        return CanonicalJsonDigest.ComputeSha256(binding);
    }
}

public static class WorldAuthoritativeJson
{
    public static void Validate(JsonElement value, string parameterName)
    {
        JsonValueInspector.ValidateAndMeasure(
            value,
            InteractionJsonLimits.Parameters,
            parameterName);
        RejectJsonNumbers(value, parameterName);
    }

    private static void RejectJsonNumbers(
        JsonElement value,
        string parameterName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                throw new WorldMutationValidationException(
                    WorldNumericReasonCodes.BinaryFloatForbidden,
                    "Authoritative numeric values must use a declared "
                    + "portable schema and canonical string encoding.",
                    parameterName);
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    RejectJsonNumbers(property.Value, parameterName);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    RejectJsonNumbers(item, parameterName);
                }

                break;
        }
    }
}

internal static class WorldMutationJson
{
    private const long DefaultMaximumBytes = 131_072;

    public static JsonElement Write(
        Action<Utf8JsonWriter> writeBody,
        long maximumBytes = DefaultMaximumBytes,
        string parameterName = "value")
    {
        using var buffer = new MemoryStream();
        using var boundedBuffer = new WorldBoundedArchiveWriteStream(
            buffer,
            maximumBytes,
            WorldDataReasonCodes.ByteLimitExceeded,
            "Portable mutation JSON exceeds its byte limit.");
        try
        {
            using (var writer = new Utf8JsonWriter(boundedBuffer))
            {
                writer.WriteStartObject();
                writeBody(writer);
                writer.WriteEndObject();
            }
        }
        catch (WorldDataContractException exception)
            when (exception.ReasonCode
                  == WorldDataReasonCodes.ByteLimitExceeded)
        {
            throw new ArgumentException(
                "Portable mutation JSON exceeds its byte limit.",
                parameterName);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    public static void WriteHeader(
        Utf8JsonWriter writer,
        IWorldMutationIntent intent)
    {
        writer.WriteString("intentId", intent.IntentId);
        writer.WriteString("kind", intent.Kind);
    }

    public static void WriteIdentity(
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

    public static void WriteResources(
        Utf8JsonWriter writer,
        IWorldMutationIntent intent)
    {
        WriteStrings(writer, "readResourceKeys", intent.ReadResourceKeys);
        WriteStrings(writer, "writeResourceKeys", intent.WriteResourceKeys);
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
}

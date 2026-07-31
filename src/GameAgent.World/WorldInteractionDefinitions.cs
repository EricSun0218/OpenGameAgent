using System.Collections.ObjectModel;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

public sealed class InteractionParameterValidationError
{
    internal InteractionParameterValidationError(
        string code,
        string instancePath,
        string schemaPath)
    {
        Code = code;
        InstancePath = instancePath;
        SchemaPath = schemaPath;
    }

    public string Code { get; }

    public string InstancePath { get; }

    public string SchemaPath { get; }
}

public sealed class InteractionParameterValidationResult
{
    internal InteractionParameterValidationResult(
        IReadOnlyList<InteractionParameterValidationError> errors)
    {
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<InteractionParameterValidationError> Errors { get; }
}

/// <summary>
/// A bounded, closed JSON parameter contract. It uses the runtime's safe JSON
/// Schema subset and requires every declared object shape to reject unknown
/// properties.
/// </summary>
public sealed class InteractionParameterContract
{
    private static readonly ToolArgumentValidator Validator = new(
        new ToolArgumentValidationOptions(
            maxErrors: 32,
            maxSchemaDepth: 24,
            maxSchemaProperties: 256,
            maxEnumItems: 256,
            schemaJsonLimits: new JsonValueLimits(
                maxUtf8Bytes: 65_536,
                maxDepth: 24,
                maxNodes: 4_096,
                maxStringUtf8Bytes: 16_384,
                maxContainerItems: 512),
            argumentJsonLimits: InteractionJsonLimits.Parameters));

    private readonly JsonElement _schema;

    public InteractionParameterContract(
        string schemaId,
        string schemaVersion,
        JsonElement schema)
    {
        SchemaId = WorldValidation.Required(
            schemaId,
            nameof(schemaId));
        SchemaVersion = WorldValidation.Required(
            schemaVersion,
            nameof(schemaVersion),
            96);
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "The parameter schema root must be an object.",
                nameof(schema));
        }

        JsonValueInspector.ValidateAndMeasure(
            schema,
            new JsonValueLimits(
                maxUtf8Bytes: 65_536,
                maxDepth: 24,
                maxNodes: 4_096,
                maxStringUtf8Bytes: 16_384,
                maxContainerItems: 512),
            nameof(schema));
        EnsureClosedObjectSchemas(schema, "$");
        EnsurePortableNumericTypes(schema, "$");

        using var emptyDocument = JsonDocument.Parse("{}");
        var validation = Validator.Validate(
            schema,
            emptyDocument.RootElement);
        var schemaError = validation.Errors.FirstOrDefault(
            error => error.Code.StartsWith(
                "schema_",
                StringComparison.Ordinal));
        if (schemaError is not null)
        {
            throw new ArgumentException(
                "The parameter schema is invalid or unsupported: "
                + schemaError.Code
                + ".",
                nameof(schema));
        }

        _schema = schema.Clone();
        Digest = CanonicalJsonDigest.ComputeSha256(_schema);
    }

    public string SchemaId { get; }

    public string SchemaVersion { get; }

    public JsonElement Schema => _schema.Clone();

    public string Digest { get; }

    public InteractionParameterValidationResult Validate(
        JsonElement parameters)
    {
        try
        {
            WorldAuthoritativeJson.Validate(
                parameters,
                nameof(parameters));
        }
        catch (WorldMutationValidationException exception)
        {
            return new InteractionParameterValidationResult(
                new ReadOnlyCollection<
                    InteractionParameterValidationError>(
                    new[]
                    {
                        new InteractionParameterValidationError(
                            exception.ReasonCode,
                            "$",
                            "$")
                    }));
        }

        var result = Validator.Validate(_schema, parameters);
        var errors = result.Errors
            .Select(
                error => new InteractionParameterValidationError(
                    error.Code,
                    error.InstancePath,
                    error.SchemaPath))
            .ToArray();
        return new InteractionParameterValidationResult(
            new ReadOnlyCollection<InteractionParameterValidationError>(
                errors));
    }

    private static void EnsureClosedObjectSchemas(
        JsonElement schema,
        string path)
    {
        var isObject = schema.TryGetProperty("type", out var type)
                       && type.ValueKind == JsonValueKind.String
                       && string.Equals(
                           type.GetString(),
                           "object",
                           StringComparison.Ordinal);
        if (isObject
            && (!schema.TryGetProperty(
                    "additionalProperties",
                    out var additional)
                || additional.ValueKind != JsonValueKind.False))
        {
            throw new ArgumentException(
                "Every object parameter schema must set "
                + "'additionalProperties' to false at "
                + path
                + ".",
                nameof(schema));
        }

        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    EnsureClosedObjectSchemas(
                        property.Value,
                        path + "/properties/" + property.Name);
                }
            }
        }

        if (schema.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Object)
        {
            EnsureClosedObjectSchemas(items, path + "/items");
        }
    }

    private static void EnsurePortableNumericTypes(
        JsonElement schema,
        string path)
    {
        if (schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() is "number" or "integer")
        {
            throw new ArgumentException(
                "Authoritative numeric parameters must use canonical "
                + "strings bound to a portable numeric schema at "
                + path
                + ".",
                nameof(schema));
        }

        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    EnsurePortableNumericTypes(
                        property.Value,
                        path + "/properties/" + property.Name);
                }
            }
        }

        if (schema.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Object)
        {
            EnsurePortableNumericTypes(items, path + "/items");
        }
    }
}

public sealed class InteractionTargetContract
{
    public InteractionTargetContract(
        string schemaId,
        int minimumTargets,
        int maximumTargets)
    {
        SchemaId = WorldValidation.Required(schemaId, nameof(schemaId));
        if (minimumTargets < 0
            || maximumTargets < minimumTargets
            || maximumTargets > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTargets));
        }

        MinimumTargets = minimumTargets;
        MaximumTargets = maximumTargets;
    }

    public string SchemaId { get; }

    public int MinimumTargets { get; }

    public int MaximumTargets { get; }
}

public sealed class InteractionCostDefinition
{
    public InteractionCostDefinition(
        string costId,
        string numericPath,
        string numericSchemaId,
        WorldFixedPointValue amount,
        string insufficientReasonCode)
    {
        CostId = WorldValidation.Required(costId, nameof(costId));
        NumericPath = WorldValidation.Required(
            numericPath,
            nameof(numericPath),
            512);
        NumericSchemaId = WorldValidation.Required(
            numericSchemaId,
            nameof(numericSchemaId));
        Amount = amount ?? throw new ArgumentNullException(nameof(amount));
        if (amount.Units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        InsufficientReasonCode = WorldValidation.Required(
            insufficientReasonCode,
            nameof(insufficientReasonCode),
            96);
    }

    public string CostId { get; }

    public string NumericPath { get; }

    public string NumericSchemaId { get; }

    public WorldFixedPointValue Amount { get; }

    public string InsufficientReasonCode { get; }
}

public sealed class InteractionCooldownDefinition
{
    public InteractionCooldownDefinition(
        string clockId,
        long minimumTicks,
        string scopeKeyId)
    {
        ClockId = WorldValidation.Required(clockId, nameof(clockId));
        if (minimumTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumTicks));
        }

        ScopeKeyId = WorldValidation.Required(
            scopeKeyId,
            nameof(scopeKeyId));
        MinimumTicks = minimumTicks;
    }

    public string ClockId { get; }

    public long MinimumTicks { get; }

    public string ScopeKeyId { get; }
}

public sealed class InteractionDurationDefinition
{
    public InteractionDurationDefinition(
        string clockId,
        long ticks,
        string completionTriggerKind)
    {
        ClockId = WorldValidation.Required(clockId, nameof(clockId));
        if (ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks));
        }

        CompletionTriggerKind = WorldValidation.Required(
            completionTriggerKind,
            nameof(completionTriggerKind));
        Ticks = ticks;
    }

    public string ClockId { get; }

    public long Ticks { get; }

    public string CompletionTriggerKind { get; }
}

public sealed class InteractionStepDefinition
{
    private readonly JsonElement _parameters;

    public InteractionStepDefinition(
        string stepId,
        string effectHandlerId,
        JsonElement parameters,
        IEnumerable<string>? readResourceKeys = null,
        IEnumerable<string>? writeResourceKeys = null)
    {
        StepId = WorldValidation.Required(stepId, nameof(stepId));
        EffectHandlerId = WorldValidation.Required(
            effectHandlerId,
            nameof(effectHandlerId));
        JsonValueInspector.ValidateAndMeasure(
            parameters,
            InteractionJsonLimits.StepParameters,
            nameof(parameters));
        WorldAuthoritativeJson.Validate(parameters, nameof(parameters));
        _parameters = parameters.Clone();
        ReadResourceKeys = WorldValidation.CopyKeys(
            readResourceKeys,
            nameof(readResourceKeys),
            maximumCount: 128);
        WriteResourceKeys = WorldValidation.CopyKeys(
            writeResourceKeys,
            nameof(writeResourceKeys),
            maximumCount: 128);
    }

    public string StepId { get; }

    public string EffectHandlerId { get; }

    public JsonElement Parameters => _parameters.Clone();

    public IReadOnlyList<string> ReadResourceKeys { get; }

    public IReadOnlyList<string> WriteResourceKeys { get; }
}

public sealed class InteractionDefinitionDetails
{
    public InteractionDefinitionDetails(
        string contentRevision,
        InteractionParameterContract parameterContract,
        InteractionTargetContract? targetContract = null,
        IEnumerable<string>? channelIds = null,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? requiredCapabilities = null,
        IEnumerable<InteractionCostDefinition>? costs = null,
        InteractionCooldownDefinition? cooldown = null,
        InteractionDurationDefinition? duration = null,
        IEnumerable<InteractionStepDefinition>? steps = null,
        string? visibilityHandlerId = null,
        IReadOnlyDictionary<string, string>? presentation = null)
    {
        ContentRevision = WorldValidation.Required(
            contentRevision,
            nameof(contentRevision),
            96);
        ParameterContract = parameterContract
                            ?? throw new ArgumentNullException(
                                nameof(parameterContract));
        TargetContract = targetContract;
        ChannelIds = WorldValidation.CopyKeys(
            channelIds,
            nameof(channelIds),
            maximumCount: 64);
        Tags = WorldValidation.CopyKeys(
            tags,
            nameof(tags),
            maximumCount: 64);
        RequiredCapabilities = WorldValidation.CopyKeys(
            requiredCapabilities,
            nameof(requiredCapabilities),
            maximumCount: 64);
        Costs = CopyCosts(costs);
        Cooldown = cooldown;
        Duration = duration;
        Steps = CopySteps(steps);
        VisibilityHandlerId = WorldValidation.Optional(
            visibilityHandlerId,
            nameof(visibilityHandlerId));
        Presentation = WorldValidation.CopyParameters(
            presentation,
            nameof(presentation));
    }

    public string ContentRevision { get; }

    public InteractionParameterContract ParameterContract { get; }

    public InteractionTargetContract? TargetContract { get; }

    public IReadOnlyList<string> ChannelIds { get; }

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyList<string> RequiredCapabilities { get; }

    public IReadOnlyList<InteractionCostDefinition> Costs { get; }

    public InteractionCooldownDefinition? Cooldown { get; }

    public InteractionDurationDefinition? Duration { get; }

    public IReadOnlyList<InteractionStepDefinition> Steps { get; }

    public string? VisibilityHandlerId { get; }

    public IReadOnlyDictionary<string, string> Presentation { get; }

    private static IReadOnlyList<InteractionCostDefinition> CopyCosts(
        IEnumerable<InteractionCostDefinition>? costs)
    {
        var copy = WorldValidation.MaterializeBounded(
                costs ?? Array.Empty<InteractionCostDefinition>(),
                64,
                nameof(costs))
            .Select(
                value => value
                         ?? throw new ArgumentException(
                             "Costs cannot contain null entries.",
                             nameof(costs)))
            .OrderBy(value => value.CostId, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(
            copy.Select(value => value.CostId),
            copy.Length,
            64,
            nameof(costs));
        return new ReadOnlyCollection<InteractionCostDefinition>(copy);
    }

    private static IReadOnlyList<InteractionStepDefinition> CopySteps(
        IEnumerable<InteractionStepDefinition>? steps)
    {
        var copy = WorldValidation.MaterializeBounded(
                steps ?? Array.Empty<InteractionStepDefinition>(),
                128,
                nameof(steps))
            .Select(
                value => value
                         ?? throw new ArgumentException(
                             "Steps cannot contain null entries.",
                             nameof(steps)))
            .ToArray();
        EnsureUnique(
            copy.Select(value => value.StepId),
            copy.Length,
            128,
            nameof(steps));
        return new ReadOnlyCollection<InteractionStepDefinition>(copy);
    }

    private static void EnsureUnique(
        IEnumerable<string> values,
        int count,
        int maximum,
        string parameterName)
    {
        if (count > maximum
            || values.Distinct(StringComparer.Ordinal).Count() != count)
        {
            throw new ArgumentException(
                "The collection is too large or contains duplicate IDs.",
                parameterName);
        }
    }
}

internal static class InteractionJsonLimits
{
    public static readonly JsonValueLimits Parameters = new(
        maxUtf8Bytes: 65_536,
        maxDepth: 24,
        maxNodes: 4_096,
        maxStringUtf8Bytes: 16_384,
        maxContainerItems: 512);

    public static readonly JsonValueLimits QueryContext = new(
        maxUtf8Bytes: 32_768,
        maxDepth: 16,
        maxNodes: 2_048,
        maxStringUtf8Bytes: 8_192,
        maxContainerItems: 256);

    public static readonly JsonValueLimits StepParameters = new(
        maxUtf8Bytes: 32_768,
        maxDepth: 16,
        maxNodes: 2_048,
        maxStringUtf8Bytes: 8_192,
        maxContainerItems: 256);
}

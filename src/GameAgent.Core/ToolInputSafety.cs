using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Protocol;

namespace GameAgent.Core;

/// <summary>
/// A bounded, non-sensitive tool input error. Paths identify locations declared by
/// the tool schema or conflict-scope template; argument values are never included.
/// </summary>
public sealed class ToolSafetyError
{
    internal ToolSafetyError(string code, string instancePath, string schemaPath)
    {
        Code = code;
        InstancePath = instancePath;
        SchemaPath = schemaPath;
    }

    public string Code { get; }

    public string InstancePath { get; }

    public string SchemaPath { get; }
}

public sealed class ToolArgumentValidationOptions
{
    public ToolArgumentValidationOptions(
        int maxErrors = 32,
        int maxSchemaDepth = 32,
        int maxSchemaProperties = 512,
        int maxEnumItems = 256,
        JsonValueLimits? schemaJsonLimits = null,
        JsonValueLimits? argumentJsonLimits = null)
    {
        if (maxErrors < 1 || maxErrors > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maxErrors));
        }

        if (maxSchemaDepth < 1 || maxSchemaDepth > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSchemaDepth));
        }

        if (maxSchemaProperties < 1 || maxSchemaProperties > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSchemaProperties));
        }

        if (maxEnumItems < 1 || maxEnumItems > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEnumItems));
        }

        MaxErrors = maxErrors;
        MaxSchemaDepth = maxSchemaDepth;
        MaxSchemaProperties = maxSchemaProperties;
        MaxEnumItems = maxEnumItems;
        SchemaJsonLimits = schemaJsonLimits ?? new JsonValueLimits(maxUtf8Bytes: 262_144);
        ArgumentJsonLimits = argumentJsonLimits ?? new JsonValueLimits(maxUtf8Bytes: 131_072);
    }

    public int MaxErrors { get; }

    public int MaxSchemaDepth { get; }

    public int MaxSchemaProperties { get; }

    public int MaxEnumItems { get; }

    public JsonValueLimits SchemaJsonLimits { get; }

    public JsonValueLimits ArgumentJsonLimits { get; }
}

public sealed class ToolArgumentValidationResult
{
    internal ToolArgumentValidationResult(IReadOnlyList<ToolSafetyError> errors)
    {
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<ToolSafetyError> Errors { get; }
}

/// <summary>
/// Validates tool arguments without reflection or runtime code generation.
///
/// Supported JSON Schema keywords are: type (single string), required,
/// properties, additionalProperties (boolean), enum, const, minimum, maximum,
/// minLength, maxLength, items (single schema), minItems, and maxItems.
/// The annotation-only title, description, and $comment keywords are accepted
/// and ignored after their value type is checked. The pattern keyword is
/// explicitly rejected. Every other keyword fails closed.
/// </summary>
public sealed class ToolArgumentValidator
{
    private readonly ToolArgumentValidationOptions _options;

    public ToolArgumentValidator(ToolArgumentValidationOptions? options = null)
    {
        _options = options ?? new ToolArgumentValidationOptions();
    }

    public ToolArgumentValidationResult Validate(
        JsonElement schema,
        JsonElement arguments)
    {
        var errors = new ErrorCollector(_options.MaxErrors);
        if (!InspectJson(
                schema,
                _options.SchemaJsonLimits,
                "schema",
                "$",
                "$",
                errors))
        {
            return errors.ToValidationResult();
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add("schema_root_not_object", "$", "$");
            return errors.ToValidationResult();
        }

        var compiled = Compile(schema, "$", 1, errors);
        if (compiled is null || errors.HasErrors)
        {
            return errors.ToValidationResult();
        }

        if (!InspectJson(
                arguments,
                _options.ArgumentJsonLimits,
                "arguments",
                "$",
                "$",
                errors))
        {
            return errors.ToValidationResult();
        }

        ValidateValue(compiled, arguments, "$", errors);
        return errors.ToValidationResult();
    }

    private SchemaNode? Compile(
        JsonElement schema,
        string schemaPath,
        int depth,
        ErrorCollector errors)
    {
        if (depth > _options.MaxSchemaDepth)
        {
            errors.Add("schema_depth_exceeded", "$", schemaPath);
            return null;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add("schema_node_not_object", "$", schemaPath);
            return null;
        }

        string? declaredType = null;
        JsonElement? properties = null;
        JsonElement? required = null;
        JsonElement? additionalProperties = null;
        JsonElement? enumeration = null;
        JsonElement? constant = null;
        JsonElement? minimum = null;
        JsonElement? maximum = null;
        JsonElement? minLength = null;
        JsonElement? maxLength = null;
        JsonElement? items = null;
        JsonElement? minItems = null;
        JsonElement? maxItems = null;

        foreach (var property in schema.EnumerateObject())
        {
            var keywordPath = AppendPointer(schemaPath, property.Name);
            switch (property.Name)
            {
                case "type":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        errors.Add("schema_type_invalid", "$", keywordPath);
                    }
                    else
                    {
                        declaredType = property.Value.GetString();
                        if (!IsSupportedType(declaredType))
                        {
                            errors.Add("schema_type_unsupported", "$", keywordPath);
                        }
                    }

                    break;
                case "properties":
                    properties = property.Value;
                    break;
                case "required":
                    required = property.Value;
                    break;
                case "additionalProperties":
                    additionalProperties = property.Value;
                    break;
                case "enum":
                    enumeration = property.Value;
                    break;
                case "const":
                    constant = property.Value;
                    break;
                case "minimum":
                    minimum = property.Value;
                    break;
                case "maximum":
                    maximum = property.Value;
                    break;
                case "minLength":
                    minLength = property.Value;
                    break;
                case "maxLength":
                    maxLength = property.Value;
                    break;
                case "items":
                    items = property.Value;
                    break;
                case "minItems":
                    minItems = property.Value;
                    break;
                case "maxItems":
                    maxItems = property.Value;
                    break;
                case "title":
                case "description":
                case "$comment":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        errors.Add("schema_annotation_invalid", "$", keywordPath);
                    }

                    break;
                case "pattern":
                    errors.Add("schema_pattern_unsupported", "$", keywordPath);
                    break;
                default:
                    errors.Add(
                        IsKnownUnsupportedKeyword(property.Name)
                            ? "schema_keyword_unsupported"
                            : "schema_keyword_unknown",
                        "$",
                        keywordPath);
                    break;
            }
        }

        if (errors.IsFull)
        {
            return null;
        }

        var node = new SchemaNode(declaredType, schemaPath);
        CompileObjectKeywords(
            node,
            properties,
            required,
            additionalProperties,
            schemaPath,
            depth,
            errors);
        CompileEnumAndConst(node, enumeration, constant, schemaPath, errors);
        CompileNumberKeywords(node, minimum, maximum, schemaPath, errors);
        CompileStringKeywords(node, minLength, maxLength, schemaPath, errors);
        CompileArrayKeywords(node, items, minItems, maxItems, schemaPath, depth, errors);
        ValidateKeywordCompatibility(
            declaredType,
            properties,
            required,
            additionalProperties,
            minimum,
            maximum,
            minLength,
            maxLength,
            items,
            minItems,
            maxItems,
            schemaPath,
            errors);
        return errors.IsFull ? null : node;
    }

    private void CompileObjectKeywords(
        SchemaNode node,
        JsonElement? properties,
        JsonElement? required,
        JsonElement? additionalProperties,
        string schemaPath,
        int depth,
        ErrorCollector errors)
    {
        if (properties.HasValue)
        {
            if (properties.Value.ValueKind != JsonValueKind.Object)
            {
                errors.Add(
                    "schema_properties_invalid",
                    "$",
                    AppendPointer(schemaPath, "properties"));
            }
            else
            {
                var count = 0;
                foreach (var property in properties.Value.EnumerateObject())
                {
                    count++;
                    if (count > _options.MaxSchemaProperties)
                    {
                        errors.Add(
                            "schema_properties_exceeded",
                            "$",
                            AppendPointer(schemaPath, "properties"));
                        break;
                    }

                    var childPath = AppendPointer(
                        AppendPointer(schemaPath, "properties"),
                        property.Name);
                    var child = Compile(property.Value, childPath, depth + 1, errors);
                    if (child is not null && !node.Properties.ContainsKey(property.Name))
                    {
                        node.Properties.Add(property.Name, child);
                    }

                    if (errors.IsFull)
                    {
                        break;
                    }
                }
            }
        }

        if (required.HasValue)
        {
            var path = AppendPointer(schemaPath, "required");
            if (required.Value.ValueKind != JsonValueKind.Array)
            {
                errors.Add("schema_required_invalid", "$", path);
            }
            else
            {
                var index = 0;
                foreach (var value in required.Value.EnumerateArray())
                {
                    var itemPath = AppendPointer(path, index.ToString(CultureInfo.InvariantCulture));
                    if (value.ValueKind != JsonValueKind.String
                        || string.IsNullOrEmpty(value.GetString()))
                    {
                        errors.Add("schema_required_item_invalid", "$", itemPath);
                    }
                    else
                    {
                        var name = value.GetString()!;
                        if (!node.Required.Add(name))
                        {
                            errors.Add("schema_required_item_duplicate", "$", itemPath);
                        }
                    }

                    index++;
                }

                foreach (var name in node.Required)
                {
                    if (!node.Properties.ContainsKey(name))
                    {
                        errors.Add("schema_required_property_undefined", "$", path);
                        break;
                    }
                }
            }
        }

        if (!additionalProperties.HasValue)
        {
            return;
        }

        var additionalPath = AppendPointer(schemaPath, "additionalProperties");
        if (additionalProperties.Value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add("schema_additional_properties_unsupported", "$", additionalPath);
        }
        else
        {
            node.AllowAdditionalProperties =
                additionalProperties.Value.ValueKind == JsonValueKind.True;
        }
    }

    private void CompileEnumAndConst(
        SchemaNode node,
        JsonElement? enumeration,
        JsonElement? constant,
        string schemaPath,
        ErrorCollector errors)
    {
        if (enumeration.HasValue)
        {
            var enumPath = AppendPointer(schemaPath, "enum");
            if (enumeration.Value.ValueKind != JsonValueKind.Array)
            {
                errors.Add("schema_enum_invalid", "$", enumPath);
            }
            else
            {
                var values = new List<JsonElement>();
                var index = 0;
                foreach (var value in enumeration.Value.EnumerateArray())
                {
                    if (values.Count >= _options.MaxEnumItems)
                    {
                        errors.Add("schema_enum_items_exceeded", "$", enumPath);
                        break;
                    }

                    if (ContainsUnsupportedNumber(value))
                    {
                        errors.Add(
                            "schema_enum_number_out_of_supported_range",
                            "$",
                            AppendPointer(
                                enumPath,
                                index.ToString(CultureInfo.InvariantCulture)));
                        index++;
                        continue;
                    }

                    if (values.Any(existing => JsonValueEquals(existing, value)))
                    {
                        errors.Add("schema_enum_item_duplicate", "$", enumPath);
                        index++;
                        continue;
                    }

                    values.Add(value.Clone());
                    index++;
                }

                if (values.Count == 0)
                {
                    errors.Add("schema_enum_empty", "$", enumPath);
                }

                node.EnumValues = values;
            }
        }

        if (constant.HasValue)
        {
            if (ContainsUnsupportedNumber(constant.Value))
            {
                errors.Add(
                    "schema_const_number_out_of_supported_range",
                    "$",
                    AppendPointer(schemaPath, "const"));
            }
            else
            {
                node.Constant = constant.Value.Clone();
            }
        }
    }

    private static void CompileNumberKeywords(
        SchemaNode node,
        JsonElement? minimum,
        JsonElement? maximum,
        string schemaPath,
        ErrorCollector errors)
    {
        if (minimum.HasValue)
        {
            var path = AppendPointer(schemaPath, "minimum");
            if (!JsonNumberValue.TryParse(minimum.Value, out var value))
            {
                errors.Add("schema_minimum_invalid", "$", path);
            }
            else
            {
                node.Minimum = value;
            }
        }

        if (maximum.HasValue)
        {
            var path = AppendPointer(schemaPath, "maximum");
            if (!JsonNumberValue.TryParse(maximum.Value, out var value))
            {
                errors.Add("schema_maximum_invalid", "$", path);
            }
            else
            {
                node.Maximum = value;
            }
        }

        if (node.Minimum.HasValue
            && node.Maximum.HasValue
            && node.Minimum.Value.CompareTo(node.Maximum.Value) > 0)
        {
            errors.Add("schema_numeric_range_invalid", "$", schemaPath);
        }
    }

    private static void CompileStringKeywords(
        SchemaNode node,
        JsonElement? minLength,
        JsonElement? maxLength,
        string schemaPath,
        ErrorCollector errors)
    {
        node.MinLength = ReadNonNegativeInt(
            minLength,
            "schema_min_length_invalid",
            AppendPointer(schemaPath, "minLength"),
            errors);
        node.MaxLength = ReadNonNegativeInt(
            maxLength,
            "schema_max_length_invalid",
            AppendPointer(schemaPath, "maxLength"),
            errors);
        if (node.MinLength.HasValue
            && node.MaxLength.HasValue
            && node.MinLength.Value > node.MaxLength.Value)
        {
            errors.Add("schema_string_range_invalid", "$", schemaPath);
        }
    }

    private void CompileArrayKeywords(
        SchemaNode node,
        JsonElement? items,
        JsonElement? minItems,
        JsonElement? maxItems,
        string schemaPath,
        int depth,
        ErrorCollector errors)
    {
        if (items.HasValue)
        {
            node.Items = Compile(
                items.Value,
                AppendPointer(schemaPath, "items"),
                depth + 1,
                errors);
        }

        node.MinItems = ReadNonNegativeInt(
            minItems,
            "schema_min_items_invalid",
            AppendPointer(schemaPath, "minItems"),
            errors);
        node.MaxItems = ReadNonNegativeInt(
            maxItems,
            "schema_max_items_invalid",
            AppendPointer(schemaPath, "maxItems"),
            errors);
        if (node.MinItems.HasValue
            && node.MaxItems.HasValue
            && node.MinItems.Value > node.MaxItems.Value)
        {
            errors.Add("schema_array_range_invalid", "$", schemaPath);
        }
    }

    private static void ValidateKeywordCompatibility(
        string? declaredType,
        JsonElement? properties,
        JsonElement? required,
        JsonElement? additionalProperties,
        JsonElement? minimum,
        JsonElement? maximum,
        JsonElement? minLength,
        JsonElement? maxLength,
        JsonElement? items,
        JsonElement? minItems,
        JsonElement? maxItems,
        string schemaPath,
        ErrorCollector errors)
    {
        if (declaredType is null)
        {
            return;
        }

        if ((properties.HasValue || required.HasValue || additionalProperties.HasValue)
            && !string.Equals(declaredType, "object", StringComparison.Ordinal))
        {
            errors.Add("schema_object_keyword_type_mismatch", "$", schemaPath);
        }

        if ((minimum.HasValue || maximum.HasValue)
            && declaredType is not ("number" or "integer"))
        {
            errors.Add("schema_numeric_keyword_type_mismatch", "$", schemaPath);
        }

        if ((minLength.HasValue || maxLength.HasValue)
            && !string.Equals(declaredType, "string", StringComparison.Ordinal))
        {
            errors.Add("schema_string_keyword_type_mismatch", "$", schemaPath);
        }

        if ((items.HasValue || minItems.HasValue || maxItems.HasValue)
            && !string.Equals(declaredType, "array", StringComparison.Ordinal))
        {
            errors.Add("schema_array_keyword_type_mismatch", "$", schemaPath);
        }
    }

    private static int? ReadNonNegativeInt(
        JsonElement? value,
        string errorCode,
        string schemaPath,
        ErrorCollector errors)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (value.Value.ValueKind != JsonValueKind.Number
            || !value.Value.TryGetInt32(out var result)
            || result < 0)
        {
            errors.Add(errorCode, "$", schemaPath);
            return null;
        }

        return result;
    }

    private static void ValidateValue(
        SchemaNode schema,
        JsonElement value,
        string instancePath,
        ErrorCollector errors)
    {
        JsonNumberValue? number = null;
        if (schema.DeclaredType is not null
            && !MatchesType(schema.DeclaredType, value, out number))
        {
            errors.Add(
                "argument_type_mismatch",
                instancePath,
                AppendPointer(schema.SchemaPath, "type"));
            return;
        }

        if (schema.EnumValues is not null
            && !schema.EnumValues.Any(candidate => JsonValueEquals(candidate, value)))
        {
            errors.Add(
                "argument_enum_mismatch",
                instancePath,
                AppendPointer(schema.SchemaPath, "enum"));
        }

        if (schema.Constant.HasValue
            && !JsonValueEquals(schema.Constant.Value, value))
        {
            errors.Add(
                "argument_const_mismatch",
                instancePath,
                AppendPointer(schema.SchemaPath, "const"));
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(schema, value, instancePath, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(schema, value, instancePath, errors);
                break;
            case JsonValueKind.String:
                ValidateString(schema, value.GetString() ?? string.Empty, instancePath, errors);
                break;
            case JsonValueKind.Number:
                if (!number.HasValue)
                {
                    if (!JsonNumberValue.TryParse(value, out var parsed))
                    {
                        errors.Add(
                            "argument_number_out_of_supported_range",
                            instancePath,
                            schema.SchemaPath);
                        break;
                    }

                    number = parsed;
                }

                ValidateNumber(schema, number.Value, instancePath, errors);
                break;
        }
    }

    private static void ValidateObject(
        SchemaNode schema,
        JsonElement value,
        string instancePath,
        ErrorCollector errors)
    {
        foreach (var required in schema.Required)
        {
            if (!value.TryGetProperty(required, out _))
            {
                errors.Add(
                    "argument_required_property_missing",
                    AppendPointer(instancePath, required),
                    AppendPointer(schema.SchemaPath, "required"));
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            if (schema.Properties.TryGetValue(property.Name, out var child))
            {
                ValidateValue(
                    child,
                    property.Value,
                    AppendPointer(instancePath, property.Name),
                    errors);
            }
            else if (!schema.AllowAdditionalProperties)
            {
                // The unexpected input property name is intentionally omitted.
                errors.Add(
                    "argument_additional_property_not_allowed",
                    instancePath,
                    AppendPointer(schema.SchemaPath, "additionalProperties"));
                break;
            }

            if (errors.IsFull)
            {
                return;
            }
        }
    }

    private static void ValidateArray(
        SchemaNode schema,
        JsonElement value,
        string instancePath,
        ErrorCollector errors)
    {
        var count = value.GetArrayLength();
        if (schema.MinItems.HasValue && count < schema.MinItems.Value)
        {
            errors.Add(
                "argument_min_items_not_met",
                instancePath,
                AppendPointer(schema.SchemaPath, "minItems"));
        }

        if (schema.MaxItems.HasValue && count > schema.MaxItems.Value)
        {
            errors.Add(
                "argument_max_items_exceeded",
                instancePath,
                AppendPointer(schema.SchemaPath, "maxItems"));
        }

        if (schema.Items is null)
        {
            return;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            ValidateValue(
                schema.Items,
                item,
                AppendPointer(instancePath, index.ToString(CultureInfo.InvariantCulture)),
                errors);
            index++;
            if (errors.IsFull)
            {
                return;
            }
        }
    }

    private static void ValidateString(
        SchemaNode schema,
        string value,
        string instancePath,
        ErrorCollector errors)
    {
        var length = CountUnicodeScalarValues(value);
        if (schema.MinLength.HasValue && length < schema.MinLength.Value)
        {
            errors.Add(
                "argument_min_length_not_met",
                instancePath,
                AppendPointer(schema.SchemaPath, "minLength"));
        }

        if (schema.MaxLength.HasValue && length > schema.MaxLength.Value)
        {
            errors.Add(
                "argument_max_length_exceeded",
                instancePath,
                AppendPointer(schema.SchemaPath, "maxLength"));
        }
    }

    private static void ValidateNumber(
        SchemaNode schema,
        JsonNumberValue value,
        string instancePath,
        ErrorCollector errors)
    {
        if (schema.Minimum.HasValue && value.CompareTo(schema.Minimum.Value) < 0)
        {
            errors.Add(
                "argument_minimum_not_met",
                instancePath,
                AppendPointer(schema.SchemaPath, "minimum"));
        }

        if (schema.Maximum.HasValue && value.CompareTo(schema.Maximum.Value) > 0)
        {
            errors.Add(
                "argument_maximum_exceeded",
                instancePath,
                AppendPointer(schema.SchemaPath, "maximum"));
        }
    }

    private static bool MatchesType(
        string declaredType,
        JsonElement value,
        out JsonNumberValue? parsedNumber)
    {
        parsedNumber = null;
        switch (declaredType)
        {
            case "object":
                return value.ValueKind == JsonValueKind.Object;
            case "array":
                return value.ValueKind == JsonValueKind.Array;
            case "string":
                return value.ValueKind == JsonValueKind.String;
            case "number":
                if (!JsonNumberValue.TryParse(value, out var number))
                {
                    return false;
                }

                parsedNumber = number;
                return true;
            case "integer":
                if (!JsonNumberValue.TryParse(value, out var integer))
                {
                    return false;
                }

                parsedNumber = integer;
                return integer.IsInteger;
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case "null":
                return value.ValueKind == JsonValueKind.Null;
            default:
                return false;
        }
    }

    private static bool JsonValueEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number
            && right.ValueKind == JsonValueKind.Number)
        {
            return JsonNumberValue.TryParse(left, out var leftNumber)
                   && JsonNumberValue.TryParse(right, out var rightNumber)
                   && leftNumber.CompareTo(rightNumber) == 0;
        }

        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var leftProperties = left.EnumerateObject().ToArray();
                    var rightProperties = right.EnumerateObject().ToArray();
                    if (leftProperties.Length != rightProperties.Length)
                    {
                        return false;
                    }

                    foreach (var property in leftProperties)
                    {
                        if (!right.TryGetProperty(property.Name, out var other)
                            || !JsonValueEquals(property.Value, other))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            case JsonValueKind.Array:
                {
                    var leftItems = left.EnumerateArray();
                    var rightItems = right.EnumerateArray();
                    while (leftItems.MoveNext())
                    {
                        if (!rightItems.MoveNext()
                            || !JsonValueEquals(leftItems.Current, rightItems.Current))
                        {
                            return false;
                        }
                    }

                    return !rightItems.MoveNext();
                }
            case JsonValueKind.String:
                return string.Equals(
                    left.GetString(),
                    right.GetString(),
                    StringComparison.Ordinal);
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return false;
        }
    }

    private static bool ContainsUnsupportedNumber(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return !JsonNumberValue.TryParse(value, out _);
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (ContainsUnsupportedNumber(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (ContainsUnsupportedNumber(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static bool InspectJson(
        JsonElement value,
        JsonValueLimits limits,
        string source,
        string instancePath,
        string schemaPath,
        ErrorCollector errors)
    {
        try
        {
            JsonValueInspector.ValidateAndMeasure(value, limits, source);
            return true;
        }
        catch (RuntimeContentLimitException exception)
        {
            errors.Add($"{source}_{exception.LimitCode}", instancePath, schemaPath);
            return false;
        }
        catch (ArgumentException)
        {
            errors.Add($"{source}_json_invalid", instancePath, schemaPath);
            return false;
        }
        catch (OverflowException)
        {
            errors.Add($"{source}_json_size_overflow", instancePath, schemaPath);
            return false;
        }
    }

    private static bool IsSupportedType(string? value)
    {
        return value is "object"
            or "array"
            or "string"
            or "number"
            or "integer"
            or "boolean"
            or "null";
    }

    private static bool IsKnownUnsupportedKeyword(string value)
    {
        return value is "$schema"
            or "$id"
            or "$ref"
            or "$defs"
            or "allOf"
            or "anyOf"
            or "oneOf"
            or "not"
            or "if"
            or "then"
            or "else"
            or "dependentRequired"
            or "dependentSchemas"
            or "patternProperties"
            or "propertyNames"
            or "unevaluatedProperties"
            or "prefixItems"
            or "contains"
            or "minContains"
            or "maxContains"
            or "uniqueItems"
            or "unevaluatedItems"
            or "multipleOf"
            or "exclusiveMinimum"
            or "exclusiveMaximum"
            or "format"
            or "contentEncoding"
            or "contentMediaType"
            or "contentSchema"
            or "default"
            or "examples"
            or "deprecated"
            or "readOnly"
            or "writeOnly";
    }

    private static int CountUnicodeScalarValues(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index])
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
            }

            count++;
        }

        return count;
    }

    internal static string AppendPointer(string path, string segment)
    {
        return path + "/" + segment.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
    }

    private sealed class SchemaNode
    {
        public SchemaNode(string? declaredType, string schemaPath)
        {
            DeclaredType = declaredType;
            SchemaPath = schemaPath;
        }

        public string? DeclaredType { get; }

        public string SchemaPath { get; }

        public Dictionary<string, SchemaNode> Properties { get; } =
            new(StringComparer.Ordinal);

        public HashSet<string> Required { get; } = new(StringComparer.Ordinal);

        public bool AllowAdditionalProperties { get; set; } = true;

        public IReadOnlyList<JsonElement>? EnumValues { get; set; }

        public JsonElement? Constant { get; set; }

        public JsonNumberValue? Minimum { get; set; }

        public JsonNumberValue? Maximum { get; set; }

        public int? MinLength { get; set; }

        public int? MaxLength { get; set; }

        public SchemaNode? Items { get; set; }

        public int? MinItems { get; set; }

        public int? MaxItems { get; set; }
    }

    private sealed class ErrorCollector
    {
        private readonly int _maxErrors;
        private readonly List<ToolSafetyError> _errors = new();

        public ErrorCollector(int maxErrors)
        {
            _maxErrors = maxErrors;
        }

        public bool HasErrors => _errors.Count > 0;

        public bool IsFull => _errors.Count >= _maxErrors;

        public void Add(string code, string instancePath, string schemaPath)
        {
            if (!IsFull)
            {
                _errors.Add(new ToolSafetyError(code, instancePath, schemaPath));
            }
        }

        public ToolArgumentValidationResult ToValidationResult()
        {
            return new ToolArgumentValidationResult(
                new ReadOnlyCollection<ToolSafetyError>(_errors));
        }
    }
}

public sealed class ConflictScopeResolverOptions
{
    public ConflictScopeResolverOptions(
        int maxScopes = ProtocolLimits.MaxToolConflictScopes,
        int maxPlaceholdersPerScope = 16,
        int maxPathSegments = 16,
        int maxTemplateUtf8Bytes = 256,
        int maxScalarUtf8Bytes = 256,
        int maxKeyUtf8Bytes = 256,
        JsonValueLimits? argumentJsonLimits = null,
        int maxTrustedBindings = 16,
        int maxBindingNameUtf8Bytes = 64)
    {
        if (maxScopes < 0 || maxScopes > ProtocolLimits.MaxToolConflictScopes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxScopes));
        }

        if (maxPlaceholdersPerScope < 1 || maxPlaceholdersPerScope > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPlaceholdersPerScope));
        }

        if (maxPathSegments < 1 || maxPathSegments > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPathSegments));
        }

        if (maxTemplateUtf8Bytes < 1 || maxTemplateUtf8Bytes > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTemplateUtf8Bytes));
        }

        if (maxScalarUtf8Bytes < 1 || maxScalarUtf8Bytes > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxScalarUtf8Bytes));
        }

        if (maxKeyUtf8Bytes < 1
            || maxKeyUtf8Bytes
            > ProtocolLimits.MaxActionExpectedEffectUnicodeScalars)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeyUtf8Bytes));
        }

        if (maxTrustedBindings < 0 || maxTrustedBindings > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTrustedBindings));
        }

        if (maxBindingNameUtf8Bytes < 1 || maxBindingNameUtf8Bytes > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBindingNameUtf8Bytes));
        }

        MaxScopes = maxScopes;
        MaxPlaceholdersPerScope = maxPlaceholdersPerScope;
        MaxPathSegments = maxPathSegments;
        MaxTemplateUtf8Bytes = maxTemplateUtf8Bytes;
        MaxScalarUtf8Bytes = maxScalarUtf8Bytes;
        MaxKeyUtf8Bytes = maxKeyUtf8Bytes;
        MaxTrustedBindings = maxTrustedBindings;
        MaxBindingNameUtf8Bytes = maxBindingNameUtf8Bytes;
        ArgumentJsonLimits = argumentJsonLimits ?? new JsonValueLimits(maxUtf8Bytes: 131_072);
    }

    public int MaxScopes { get; }

    public int MaxPlaceholdersPerScope { get; }

    public int MaxPathSegments { get; }

    public int MaxTemplateUtf8Bytes { get; }

    public int MaxScalarUtf8Bytes { get; }

    public int MaxKeyUtf8Bytes { get; }

    public int MaxTrustedBindings { get; }

    public int MaxBindingNameUtf8Bytes { get; }

    public JsonValueLimits ArgumentJsonLimits { get; }
}

public sealed class ConflictScopeResolutionResult
{
    internal ConflictScopeResolutionResult(
        IReadOnlyList<string> keys,
        IReadOnlyList<ToolSafetyError> errors)
    {
        Keys = keys;
        Errors = errors;
    }

    public bool IsSuccess => Errors.Count == 0;

    public IReadOnlyList<string> Keys { get; }

    public IReadOnlyList<ToolSafetyError> Errors { get; }
}

/// <summary>
/// Resolves catalog-owned conflict-scope templates such as
/// "entity:{entityId}" and "inventory:{owner.id}". Placeholder paths traverse
/// JSON objects only. Missing, null, object, and array values fail closed.
/// String substitutions are UTF-8 percent encoded; numbers are normalized.
/// Trusted single-segment runtime bindings take precedence over arguments.
/// Reserved agentId, worldId, runId, and turnId names never fall back to
/// model-controlled arguments when their trusted binding is absent.
/// </summary>
public sealed class ConflictScopeResolver
{
    private readonly ConflictScopeResolverOptions _options;

    public ConflictScopeResolver(ConflictScopeResolverOptions? options = null)
    {
        _options = options ?? new ConflictScopeResolverOptions();
    }

    public ConflictScopeResolutionResult Resolve(
        IEnumerable<string> templates,
        JsonElement arguments)
    {
        return Resolve(
            templates,
            arguments,
            EmptyReadOnlyDictionary<string, string>.Instance);
    }

    public ConflictScopeResolutionResult Resolve(
        IEnumerable<string> templates,
        JsonElement arguments,
        IReadOnlyDictionary<string, string> trustedRuntimeBindings)
    {
        if (templates is null)
        {
            throw new ArgumentNullException(nameof(templates));
        }

        if (trustedRuntimeBindings is null)
        {
            throw new ArgumentNullException(nameof(trustedRuntimeBindings));
        }

        var errors = new List<ToolSafetyError>();
        var bindings = CopyBindings(trustedRuntimeBindings, errors);
        if (errors.Count > 0)
        {
            return Result(Array.Empty<string>(), errors);
        }

        if (!InspectArguments(arguments, errors))
        {
            return Result(Array.Empty<string>(), errors);
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ToolSafetyError(
                "conflict_arguments_not_object",
                "$",
                "$conflictScopes"));
            return Result(Array.Empty<string>(), errors);
        }

        var resolved = new SortedSet<string>(StringComparer.Ordinal);
        var scopeIndex = 0;
        foreach (var template in templates)
        {
            if (scopeIndex >= _options.MaxScopes)
            {
                errors.Add(new ToolSafetyError(
                    "conflict_scope_count_exceeded",
                    "$",
                    "$conflictScopes"));
                break;
            }

            var scopePath = "$conflictScopes/"
                            + scopeIndex.ToString(CultureInfo.InvariantCulture);
            if (!TryResolveTemplate(
                    template,
                    arguments,
                    bindings,
                    scopePath,
                    out var key,
                    out var error))
            {
                errors.Add(error!);
            }
            else
            {
                resolved.Add(key!);
            }

            scopeIndex++;
        }

        if (errors.Count > 0)
        {
            return Result(Array.Empty<string>(), errors);
        }

        return Result(resolved.ToArray(), errors);
    }

    private IReadOnlyDictionary<string, string> CopyBindings(
        IReadOnlyDictionary<string, string> bindings,
        ICollection<ToolSafetyError> errors)
    {
        if (bindings.Count > _options.MaxTrustedBindings)
        {
            errors.Add(new ToolSafetyError(
                "conflict_runtime_binding_count_exceeded",
                "$runtimeBindings",
                "$conflictScopes"));
            return EmptyReadOnlyDictionary<string, string>.Instance;
        }

        var copied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in bindings.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (copied.Count >= _options.MaxTrustedBindings)
            {
                errors.Add(new ToolSafetyError(
                    "conflict_runtime_binding_count_exceeded",
                    "$runtimeBindings",
                    "$conflictScopes"));
                break;
            }

            if (string.IsNullOrWhiteSpace(pair.Key)
                || Encoding.UTF8.GetByteCount(pair.Key)
                > _options.MaxBindingNameUtf8Bytes
                || !IsValidSingleSegmentPath(pair.Key))
            {
                errors.Add(new ToolSafetyError(
                    "conflict_runtime_binding_name_invalid",
                    "$runtimeBindings",
                    "$conflictScopes"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(pair.Value)
                || Encoding.UTF8.GetByteCount(pair.Value) > _options.MaxScalarUtf8Bytes)
            {
                errors.Add(new ToolSafetyError(
                    "conflict_runtime_binding_value_invalid",
                    "$runtimeBindings",
                    "$conflictScopes"));
                continue;
            }

            if (!copied.TryAdd(pair.Key, PercentEncode(pair.Value)))
            {
                errors.Add(new ToolSafetyError(
                    "conflict_runtime_binding_name_duplicate",
                    "$runtimeBindings",
                    "$conflictScopes"));
            }
        }

        return new ReadOnlyDictionary<string, string>(copied);
    }

    private bool InspectArguments(
        JsonElement arguments,
        ICollection<ToolSafetyError> errors)
    {
        try
        {
            JsonValueInspector.ValidateAndMeasure(
                arguments,
                _options.ArgumentJsonLimits,
                nameof(arguments));
            return true;
        }
        catch (RuntimeContentLimitException exception)
        {
            errors.Add(new ToolSafetyError(
                $"conflict_arguments_{exception.LimitCode}",
                "$",
                "$conflictScopes"));
            return false;
        }
        catch (ArgumentException)
        {
            errors.Add(new ToolSafetyError(
                "conflict_arguments_json_invalid",
                "$",
                "$conflictScopes"));
            return false;
        }
        catch (OverflowException)
        {
            errors.Add(new ToolSafetyError(
                "conflict_arguments_json_size_overflow",
                "$",
                "$conflictScopes"));
            return false;
        }
    }

    private bool TryResolveTemplate(
        string? template,
        JsonElement arguments,
        IReadOnlyDictionary<string, string> trustedRuntimeBindings,
        string scopePath,
        out string? key,
        out ToolSafetyError? error)
    {
        key = null;
        error = null;
        if (string.IsNullOrWhiteSpace(template)
            || Encoding.UTF8.GetByteCount(template) > _options.MaxTemplateUtf8Bytes)
        {
            error = new ToolSafetyError("conflict_scope_template_invalid", "$", scopePath);
            return false;
        }

        var output = new StringBuilder(template.Length);
        var placeholders = 0;
        for (var index = 0; index < template.Length;)
        {
            var character = template[index];
            if (character == '}')
            {
                error = new ToolSafetyError(
                    "conflict_scope_template_invalid",
                    "$",
                    scopePath);
                return false;
            }

            if (character != '{')
            {
                output.Append(character);
                index++;
                continue;
            }

            var end = template.IndexOf('}', index + 1);
            if (end < 0 || template.IndexOf('{', index + 1, end - index - 1) >= 0)
            {
                error = new ToolSafetyError(
                    "conflict_scope_template_invalid",
                    "$",
                    scopePath);
                return false;
            }

            placeholders++;
            if (placeholders > _options.MaxPlaceholdersPerScope)
            {
                error = new ToolSafetyError(
                    "conflict_scope_placeholder_count_exceeded",
                    "$",
                    scopePath);
                return false;
            }

            var placeholder = template.Substring(index + 1, end - index - 1);
            if (!TryParsePath(placeholder, out var path))
            {
                error = new ToolSafetyError(
                    "conflict_scope_path_invalid",
                    "$",
                    scopePath);
                return false;
            }

            if (path.Length == 1
                && trustedRuntimeBindings.TryGetValue(path[0], out var trustedValue))
            {
                output.Append(trustedValue);
            }
            else if (IsReservedRuntimeBinding(path[0]))
            {
                error = new ToolSafetyError(
                    "conflict_runtime_binding_missing",
                    ToInstancePath(path),
                    scopePath);
                return false;
            }
            else
            {
                if (!TryResolvePath(arguments, path, out var value))
                {
                    error = new ToolSafetyError(
                        "conflict_scope_value_missing",
                        ToInstancePath(path),
                        scopePath);
                    return false;
                }

                if (!TryFormatScalar(value, out var replacement, out var code))
                {
                    error = new ToolSafetyError(code, ToInstancePath(path), scopePath);
                    return false;
                }

                output.Append(replacement);
            }

            if (Encoding.UTF8.GetByteCount(output.ToString()) > _options.MaxKeyUtf8Bytes)
            {
                error = new ToolSafetyError(
                    "conflict_scope_key_size_exceeded",
                    "$",
                    scopePath);
                return false;
            }

            index = end + 1;
        }

        key = output.ToString();
        if (string.IsNullOrWhiteSpace(key)
            || Encoding.UTF8.GetByteCount(key) > _options.MaxKeyUtf8Bytes)
        {
            error = new ToolSafetyError(
                "conflict_scope_key_size_exceeded",
                "$",
                scopePath);
            key = null;
            return false;
        }

        return true;
    }

    private bool TryParsePath(string value, out string[] segments)
    {
        segments = value.Split('.');
        if (segments.Length == 0 || segments.Length > _options.MaxPathSegments)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment.Length > 64 || !IsPathStart(segment[0]))
            {
                return false;
            }

            for (var index = 1; index < segment.Length; index++)
            {
                if (!IsPathContinuation(segment[index]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsValidSingleSegmentPath(string value)
    {
        if (value.Length == 0 || value.Length > 64 || !IsPathStart(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsPathContinuation(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolvePath(
        JsonElement root,
        IEnumerable<string> path,
        out JsonElement value)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private bool TryFormatScalar(
        JsonElement value,
        out string? replacement,
        out string errorCode)
    {
        replacement = null;
        errorCode = "conflict_scope_value_type_unsupported";
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)
                    || Encoding.UTF8.GetByteCount(text) > _options.MaxScalarUtf8Bytes)
                {
                    errorCode = "conflict_scope_value_size_invalid";
                    return false;
                }

                replacement = PercentEncode(text);
                return true;
            case JsonValueKind.Number:
                if (!JsonNumberValue.TryParse(value, out var number))
                {
                    errorCode = "conflict_scope_number_out_of_supported_range";
                    return false;
                }

                replacement = number.ToCanonicalString(_options.MaxScalarUtf8Bytes);
                if (replacement is null)
                {
                    errorCode = "conflict_scope_value_size_invalid";
                    return false;
                }

                return true;
            case JsonValueKind.True:
                replacement = "true";
                return true;
            case JsonValueKind.False:
                replacement = "false";
                return true;
            default:
                return false;
        }
    }

    private static string PercentEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var output = new StringBuilder(bytes.Length);
        foreach (var item in bytes)
        {
            if (item is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'-'
                or (byte)'.'
                or (byte)'_'
                or (byte)'~')
            {
                output.Append((char)item);
            }
            else
            {
                output.Append('%');
                output.Append(item.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return output.ToString();
    }

    private static bool IsPathStart(char value)
    {
        return value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '_';
    }

    private static bool IsPathContinuation(char value)
    {
        return IsPathStart(value) || value is >= '0' and <= '9' or '-';
    }

    private static bool IsReservedRuntimeBinding(string value)
    {
        return value is "agentId" or "worldId" or "runId" or "turnId";
    }

    private static string ToInstancePath(IEnumerable<string> path)
    {
        var result = "$";
        foreach (var segment in path)
        {
            result = ToolArgumentValidator.AppendPointer(result, segment);
        }

        return result;
    }

    private static ConflictScopeResolutionResult Result(
        IReadOnlyList<string> keys,
        IReadOnlyList<ToolSafetyError> errors)
    {
        return new ConflictScopeResolutionResult(
            new ReadOnlyCollection<string>(keys.ToArray()),
            new ReadOnlyCollection<ToolSafetyError>(errors.ToArray()));
    }

    private static class EmptyReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        public static readonly IReadOnlyDictionary<TKey, TValue> Instance =
            new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>());
    }
}

public sealed class ToolInputSafetyResult
{
    internal ToolInputSafetyResult(
        IReadOnlyList<string> resolvedConflictKeys,
        IReadOnlyList<ToolSafetyError> errors)
    {
        ResolvedConflictKeys = resolvedConflictKeys;
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<string> ResolvedConflictKeys { get; }

    public IReadOnlyList<ToolSafetyError> Errors { get; }
}

/// <summary>
/// Applies both argument-schema validation and catalog-owned conflict-scope
/// resolution. The resolver is never run after validation fails.
/// </summary>
public sealed class ToolInputSafetyGuard
{
    private readonly ToolArgumentValidator _validator;
    private readonly ConflictScopeResolver _resolver;

    public ToolInputSafetyGuard(
        ToolArgumentValidator? validator = null,
        ConflictScopeResolver? resolver = null)
    {
        _validator = validator ?? new ToolArgumentValidator();
        _resolver = resolver ?? new ConflictScopeResolver();
    }

    public ToolInputSafetyResult Validate(
        ToolCatalogEntry tool,
        JsonElement arguments)
    {
        return Validate(
            tool,
            arguments,
            EmptyReadOnlyDictionary<string, string>.Instance);
    }

    public ToolInputSafetyResult Validate(
        ToolCatalogEntry tool,
        JsonElement arguments,
        IReadOnlyDictionary<string, string> trustedRuntimeBindings)
    {
        if (tool is null)
        {
            throw new ArgumentNullException(nameof(tool));
        }

        if (trustedRuntimeBindings is null)
        {
            throw new ArgumentNullException(nameof(trustedRuntimeBindings));
        }

        var validation = _validator.Validate(tool.ParametersSchema, arguments);
        if (!validation.IsValid)
        {
            return new ToolInputSafetyResult(
                Array.Empty<string>(),
                validation.Errors);
        }

        var resolution = _resolver.Resolve(
            tool.ConflictScopes,
            arguments,
            trustedRuntimeBindings);
        return new ToolInputSafetyResult(
            resolution.IsSuccess ? resolution.Keys : Array.Empty<string>(),
            resolution.Errors);
    }

    private static class EmptyReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        public static readonly IReadOnlyDictionary<TKey, TValue> Instance =
            new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>());
    }
}

internal readonly struct JsonNumberValue : IComparable<JsonNumberValue>
{
    private const long MaxSupportedExponentMagnitude = 1_000_000;

    private JsonNumberValue(int sign, string digits, long scale)
    {
        Sign = sign;
        Digits = digits;
        Scale = scale;
    }

    private int Sign { get; }

    private string Digits { get; }

    private long Scale { get; }

    public bool IsInteger => Sign == 0 || Scale >= 0;

    public static bool TryParse(JsonElement value, out JsonNumberValue result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var raw = value.GetRawText();
        var index = 0;
        var sign = 1;
        if (raw.Length > 0 && raw[0] == '-')
        {
            sign = -1;
            index++;
        }

        var exponentStart = raw.IndexOfAny(new[] { 'e', 'E' }, index);
        var mantissaEnd = exponentStart < 0 ? raw.Length : exponentStart;
        var decimalPoint = raw.IndexOf('.', index, mantissaEnd - index);
        var fractionalDigits = decimalPoint < 0 ? 0 : mantissaEnd - decimalPoint - 1;
        var digitsBuilder = new StringBuilder(mantissaEnd - index);
        for (var position = index; position < mantissaEnd; position++)
        {
            if (raw[position] != '.')
            {
                digitsBuilder.Append(raw[position]);
            }
        }

        var exponent = 0L;
        if (exponentStart >= 0
            && !TryParseBoundedExponent(raw, exponentStart + 1, out exponent))
        {
            return false;
        }

        var digits = digitsBuilder.ToString();
        var leading = 0;
        while (leading < digits.Length && digits[leading] == '0')
        {
            leading++;
        }

        if (leading == digits.Length)
        {
            result = new JsonNumberValue(0, "0", 0);
            return true;
        }

        digits = digits.Substring(leading);
        var trailing = 0;
        while (trailing < digits.Length - 1 && digits[digits.Length - trailing - 1] == '0')
        {
            trailing++;
        }

        if (trailing > 0)
        {
            digits = digits.Substring(0, digits.Length - trailing);
        }

        var scale = exponent - fractionalDigits + trailing;
        if (Math.Abs(scale) > MaxSupportedExponentMagnitude)
        {
            return false;
        }

        result = new JsonNumberValue(sign, digits, scale);
        return true;
    }

    public int CompareTo(JsonNumberValue other)
    {
        if (Sign != other.Sign)
        {
            return Sign.CompareTo(other.Sign);
        }

        if (Sign == 0)
        {
            return 0;
        }

        var magnitude = CompareMagnitude(other);
        return Sign > 0 ? magnitude : -magnitude;
    }

    public string? ToCanonicalString(int maxUtf8Bytes)
    {
        if (Sign == 0)
        {
            return "0";
        }

        var prefix = Sign < 0 ? "-" : string.Empty;
        string output;
        if (Scale >= 0 && Digits.Length + Scale <= maxUtf8Bytes - prefix.Length)
        {
            output = prefix + Digits + new string('0', checked((int)Scale));
        }
        else if (Scale < 0 && Digits.Length + Scale > 0)
        {
            var point = checked((int)(Digits.Length + Scale));
            output = prefix + Digits.Substring(0, point) + "." + Digits.Substring(point);
        }
        else if (Scale < 0 && -Scale < maxUtf8Bytes)
        {
            output = prefix
                     + "0."
                     + new string('0', checked((int)(-Scale - Digits.Length)))
                     + Digits;
        }
        else
        {
            output = prefix
                     + Digits
                     + "e"
                     + Scale.ToString(CultureInfo.InvariantCulture);
        }

        return Encoding.UTF8.GetByteCount(output) <= maxUtf8Bytes ? output : null;
    }

    private int CompareMagnitude(JsonNumberValue other)
    {
        var leftMagnitude = Digits.Length + Scale;
        var rightMagnitude = other.Digits.Length + other.Scale;
        if (leftMagnitude != rightMagnitude)
        {
            return leftMagnitude.CompareTo(rightMagnitude);
        }

        var length = Math.Max(Digits.Length, other.Digits.Length);
        for (var index = 0; index < length; index++)
        {
            var left = index < Digits.Length ? Digits[index] : '0';
            var right = index < other.Digits.Length ? other.Digits[index] : '0';
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    private static bool TryParseBoundedExponent(
        string raw,
        int index,
        out long exponent)
    {
        exponent = 0;
        var sign = 1;
        if (index < raw.Length && raw[index] is '+' or '-')
        {
            sign = raw[index] == '-' ? -1 : 1;
            index++;
        }

        if (index >= raw.Length)
        {
            return false;
        }

        for (; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character is < '0' or > '9')
            {
                return false;
            }

            exponent = exponent * 10 + (character - '0');
            if (exponent > MaxSupportedExponentMagnitude)
            {
                return false;
            }
        }

        exponent *= sign;
        return true;
    }
}

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.Workflow;

internal static class WorkflowJson
{
    public static JsonElement CreateEmptyObject()
    {
        return CreateElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        });
    }

    public static JsonElement CreateEmptyArray()
    {
        return CreateElement(writer =>
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
        });
    }

    public static JsonElement CreateElement(Action<Utf8JsonWriter> write)
    {
        if (write is null)
        {
            throw new ArgumentNullException(nameof(write));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            writer.Flush();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static int MeasureUtf8(JsonElement value)
    {
        return Encoding.UTF8.GetByteCount(value.GetRawText());
    }

    public static bool TryResolvePointer(
        JsonElement root,
        string pointer,
        out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0)
        {
            return true;
        }

        if (!TryParsePointer(pointer, out var segments))
        {
            value = default;
            return false;
        }

        foreach (var segment in segments)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                {
                    value = default;
                    return false;
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Array
                && TryParseArrayIndex(segment, out var index)
                && index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }

            value = default;
            return false;
        }

        return true;
    }

    public static bool IsValidPointer(string pointer)
    {
        return TryParsePointer(pointer, out _);
    }

    public static JsonElement BuildDependencyObject(
        IReadOnlyList<(string StageId, JsonElement Output)> values)
    {
        return CreateElement(writer =>
        {
            writer.WriteStartObject();
            foreach (var value in values)
            {
                writer.WritePropertyName(value.StageId);
                value.Output.WriteTo(writer);
            }

            writer.WriteEndObject();
        });
    }

    public static JsonElement BuildReduceInput(
        IReadOnlyList<(string StageId, string InstanceId, JsonElement Output)>
            values)
    {
        return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStartObject();
                writer.WriteString("stageId", value.StageId);
                writer.WriteString("instanceId", value.InstanceId);
                writer.WritePropertyName("output");
                value.Output.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    public static JsonElement BuildOutputArray(
        IEnumerable<JsonElement> values)
    {
        return CreateElement(writer =>
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                value.WriteTo(writer);
            }

            writer.WriteEndArray();
        });
    }

    private static bool TryParsePointer(
        string pointer,
        out IReadOnlyList<string> segments)
    {
        var result = new List<string>();
        segments = result;
        if (pointer.Length == 0)
        {
            return true;
        }

        if (pointer[0] != '/')
        {
            return false;
        }

        var rawSegments = pointer.Substring(1).Split('/');
        foreach (var rawSegment in rawSegments)
        {
            var decoded = new StringBuilder(rawSegment.Length);
            for (var index = 0; index < rawSegment.Length; index++)
            {
                var character = rawSegment[index];
                if (character != '~')
                {
                    decoded.Append(character);
                    continue;
                }

                if (index + 1 >= rawSegment.Length)
                {
                    return false;
                }

                var escape = rawSegment[++index];
                if (escape == '0')
                {
                    decoded.Append('~');
                }
                else if (escape == '1')
                {
                    decoded.Append('/');
                }
                else
                {
                    return false;
                }
            }

            result.Add(decoded.ToString());
        }

        segments = result;
        return true;
    }

    private static bool TryParseArrayIndex(string value, out int result)
    {
        result = 0;
        if (value.Length == 0
            || (value.Length > 1 && value[0] == '0'))
        {
            return false;
        }

        return int.TryParse(
                   value,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out result)
               && result >= 0;
    }
}

public static class WorkflowIdentity
{
    public static string CreateRunId(
        string definitionDigest,
        string inputDigest,
        string runKey)
    {
        RequireDigest(definitionDigest, nameof(definitionDigest));
        RequireDigest(inputDigest, nameof(inputDigest));
        WorkflowValidation.RequiredIdentifier(
            runKey,
            nameof(runKey),
            256,
            allowSlash: true);
        return "wfr_" + Derive(
            "run",
            definitionDigest,
            inputDigest,
            runKey);
    }

    public static string CreateStageInstanceId(string runId, string stageId)
    {
        return "wfs_" + Derive("stage", runId, stageId);
    }

    public static string CreateForeachChildId(
        string stageInstanceId,
        string identityDigest)
    {
        RequireDigest(identityDigest, nameof(identityDigest));
        return "wfi_" + Derive(
            "foreach-item",
            stageInstanceId,
            identityDigest);
    }

    public static string CreateLoopChildId(
        string stageInstanceId,
        int iteration)
    {
        if (iteration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iteration));
        }

        return "wfl_" + Derive(
            "loop-iteration",
            stageInstanceId,
            iteration.ToString("D8", CultureInfo.InvariantCulture));
    }

    public static string ComputeJsonDigest(JsonElement value)
    {
        return CanonicalJsonDigest.ComputeSha256(value);
    }

    private static string Derive(string domain, params string[] values)
    {
        var payload = WorkflowJson.CreateElement(writer =>
        {
            writer.WriteStartArray();
            writer.WriteStringValue("gameagent.workflow.identity.v1");
            writer.WriteStringValue(domain);
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        });
        return CanonicalJsonDigest.ComputeSha256(payload);
    }

    private static void RequireDigest(string value, string name)
    {
        if (!CanonicalJsonDigest.IsSha256(value))
        {
            throw new ArgumentException(
                "The value must be a canonical SHA-256 digest.",
                name);
        }
    }
}

internal static class WorkflowSchema
{
    private static readonly ToolArgumentValidator ValueValidator = new();

    private static readonly HashSet<string> AllowedKeywords =
        new(StringComparer.Ordinal)
        {
            "type",
            "required",
            "properties",
            "additionalProperties",
            "enum",
            "const",
            "minimum",
            "maximum",
            "minLength",
            "maxLength",
            "items",
            "minItems",
            "maxItems",
            "title",
            "description",
            "$comment"
        };

    public static WorkflowDiagnostic? ValidateDefinition(
        JsonElement schema,
        WorkflowLimits limits,
        string label,
        string? stageId)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return Invalid(label, stageId, "Schema root must be an object.");
        }

        if (WorkflowJson.MeasureUtf8(schema) > limits.MaxSchemaBytes)
        {
            return Invalid(label, stageId, "Schema exceeds its byte limit.");
        }

        return ValidateNode(schema, limits, label, stageId, 1);
    }

    public static bool TryValidateValue(
        JsonElement schema,
        JsonElement value,
        int maximumBytes,
        out string reasonCode)
    {
        if (value.ValueKind == JsonValueKind.Undefined
            || WorkflowJson.MeasureUtf8(value) > maximumBytes)
        {
            reasonCode = WorkflowReasonCodes.JsonLimitExceeded;
            return false;
        }

        var validation = ValueValidator.Validate(schema, value);
        if (!validation.IsValid)
        {
            reasonCode = WorkflowReasonCodes.SchemaMismatch;
            return false;
        }

        reasonCode = string.Empty;
        return true;
    }

    private static WorkflowDiagnostic? ValidateNode(
        JsonElement schema,
        WorkflowLimits limits,
        string label,
        string? stageId,
        int depth)
    {
        if (depth > limits.MaxSchemaDepth)
        {
            return Invalid(label, stageId, "Schema depth exceeds the limit.");
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (!AllowedKeywords.Contains(property.Name))
            {
                return Invalid(
                    label,
                    stageId,
                    $"Unsupported schema keyword '{property.Name}'.");
            }

            if (property.Name is "title" or "description" or "$comment"
                && property.Value.ValueKind != JsonValueKind.String)
            {
                return Invalid(
                    label,
                    stageId,
                    $"Schema annotation '{property.Name}' must be a string.");
            }
        }

        if (!schema.TryGetProperty("type", out var typeValue)
            || typeValue.ValueKind != JsonValueKind.String)
        {
            return Invalid(
                label,
                stageId,
                "Every schema node must declare one type.");
        }

        var type = typeValue.GetString();
        if (type is not ("object"
            or "array"
            or "string"
            or "number"
            or "integer"
            or "boolean"
            or "null"))
        {
            return Invalid(label, stageId, "Schema type is unsupported.");
        }

        if (type == "object")
        {
            if (HasAny(
                    schema,
                    "items",
                    "minItems",
                    "maxItems",
                    "minLength",
                    "maxLength",
                    "minimum",
                    "maximum"))
            {
                return Invalid(
                    label,
                    stageId,
                    "Object schemas contain incompatible keywords.");
            }

            if (!schema.TryGetProperty(
                    "additionalProperties",
                    out var additional)
                || additional.ValueKind != JsonValueKind.False)
            {
                return Invalid(
                    label,
                    stageId,
                    "Object schemas must set additionalProperties to false.");
            }

            if (!schema.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                return Invalid(
                    label,
                    stageId,
                    "Object schemas must declare properties.");
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    return Invalid(
                        label,
                        stageId,
                        "Property schemas must be objects.");
                }

                var child = ValidateNode(
                    property.Value,
                    limits,
                    label,
                    stageId,
                    depth + 1);
                if (child is not null)
                {
                    return child;
                }
            }

            if (schema.TryGetProperty("required", out var required))
            {
                if (required.ValueKind != JsonValueKind.Array)
                {
                    return Invalid(
                        label,
                        stageId,
                        "required must be an array.");
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in required.EnumerateArray())
                {
                    var name = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : null;
                    if (name is null
                        || !seen.Add(name)
                        || !properties.TryGetProperty(name, out _))
                    {
                        return Invalid(
                            label,
                            stageId,
                            "required contains an invalid property.");
                    }
                }
            }
        }
        else if (type == "array")
        {
            if (HasAny(
                    schema,
                    "properties",
                    "required",
                    "additionalProperties",
                    "minLength",
                    "maxLength",
                    "minimum",
                    "maximum"))
            {
                return Invalid(
                    label,
                    stageId,
                    "Array schemas contain incompatible keywords.");
            }

            if (!schema.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Object
                || !TryGetBoundedInt(
                    schema,
                    "maxItems",
                    0,
                    limits.MaxForeachItems,
                    out _))
            {
                return Invalid(
                    label,
                    stageId,
                    "Array schemas require object items and bounded maxItems.");
            }

            if (schema.TryGetProperty("minItems", out var minItems)
                && (minItems.ValueKind != JsonValueKind.Number
                    || !minItems.TryGetInt32(out var minimumItems)
                    || minimumItems < 0
                    || minimumItems > schema.GetProperty("maxItems").GetInt32()))
            {
                return Invalid(
                    label,
                    stageId,
                    "Array minItems must be within maxItems.");
            }

            var child = ValidateNode(
                items,
                limits,
                label,
                stageId,
                depth + 1);
            if (child is not null)
            {
                return child;
            }
        }
        else if (type == "string")
        {
            if (HasAny(
                    schema,
                    "properties",
                    "required",
                    "additionalProperties",
                    "items",
                    "minItems",
                    "maxItems",
                    "minimum",
                    "maximum"))
            {
                return Invalid(
                    label,
                    stageId,
                    "String schemas contain incompatible keywords.");
            }

            if (!TryGetBoundedInt(
                    schema,
                    "maxLength",
                    0,
                    262_144,
                    out _))
            {
                return Invalid(
                    label,
                    stageId,
                    "String schemas require bounded maxLength.");
            }

            if (schema.TryGetProperty("minLength", out var minLength)
                && (minLength.ValueKind != JsonValueKind.Number
                    || !minLength.TryGetInt32(out var minimumLength)
                    || minimumLength < 0
                    || minimumLength
                    > schema.GetProperty("maxLength").GetInt32()))
            {
                return Invalid(
                    label,
                    stageId,
                    "String minLength must be within maxLength.");
            }
        }
        else if (type is "number" or "integer")
        {
            if (HasAny(
                    schema,
                    "properties",
                    "required",
                    "additionalProperties",
                    "items",
                    "minItems",
                    "maxItems",
                    "minLength",
                    "maxLength"))
            {
                return Invalid(
                    label,
                    stageId,
                    "Numeric schemas contain incompatible keywords.");
            }

            if (!schema.TryGetProperty("minimum", out var minimum)
                || minimum.ValueKind != JsonValueKind.Number
                || !schema.TryGetProperty("maximum", out var maximum)
                || maximum.ValueKind != JsonValueKind.Number)
            {
                return Invalid(
                    label,
                    stageId,
                    "Numeric schemas require minimum and maximum.");
            }

            if (!JsonValueInspector.TryCompareNumbers(
                    minimum,
                    maximum,
                    out var comparison)
                || comparison > 0)
            {
                return Invalid(
                    label,
                    stageId,
                    "Numeric schema bounds are invalid.");
            }
        }
        else if (HasAny(
                     schema,
                     "properties",
                     "required",
                     "additionalProperties",
                     "items",
                     "minItems",
                     "maxItems",
                     "minLength",
                     "maxLength",
                     "minimum",
                     "maximum"))
        {
            return Invalid(
                label,
                stageId,
                "Scalar schemas contain incompatible keywords.");
        }

        return null;
    }

    private static bool TryGetBoundedInt(
        JsonElement schema,
        string propertyName,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        return schema.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value)
               && value >= minimum
               && value <= maximum;
    }

    private static bool HasAny(
        JsonElement schema,
        params string[] propertyNames)
    {
        return propertyNames.Any(propertyName =>
            schema.TryGetProperty(propertyName, out _));
    }

    private static WorkflowDiagnostic Invalid(
        string label,
        string? stageId,
        string message)
    {
        return new WorkflowDiagnostic(
            WorkflowReasonCodes.SchemaInvalid,
            $"{label}: {message}",
            stageId);
    }
}

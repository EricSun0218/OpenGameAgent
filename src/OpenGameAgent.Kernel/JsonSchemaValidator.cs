using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenGameAgent.Kernel;

internal static class JsonSchemaValidator
{
    private const int MaxDepth = 64;
    private const int MaxNumberCharacters = 4096;
    private static readonly char[] ExponentMarkers = { 'e', 'E' };
    private static readonly string[] UnsupportedAssertionKeywords =
    {
        "$ref",
        "$dynamicRef",
        "if",
        "then",
        "else",
        "dependentRequired",
        "dependentSchemas",
        "patternProperties",
        "propertyNames",
        "unevaluatedProperties",
        "prefixItems",
        "contains",
        "minContains",
        "maxContains",
        "unevaluatedItems",
        "multipleOf",
    };

    public static string? Validate(string schemaJson, JsonElement value)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var schemaError = ValidateSchemaNode(document.RootElement, "$", 0);
            if (schemaError is not null)
            {
                return schemaError;
            }

            var schemaJsonError = ValidateJsonValue(document.RootElement, "$schema", 0);
            if (schemaJsonError is not null)
            {
                return schemaJsonError;
            }

            var valueError = ValidateJsonValue(value, "$", 0);
            if (valueError is not null)
            {
                return valueError;
            }

            return ValidateNode(document.RootElement, value, "$", 0);
        }
        catch (JsonException exception)
        {
            return "The tool schema is invalid: " + exception.Message;
        }
        catch (NumberLimitException exception)
        {
            return exception.Message;
        }
    }

    private static string? ValidateSchemaNode(JsonElement schema, string path, int depth)
    {
        if (depth > MaxDepth)
        {
            return path + " exceeds the maximum schema validation depth.";
        }

        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return null;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "The tool schema must be a JSON object or boolean.";
        }

        if (HasDuplicateProperties(schema))
        {
            return path + " contains duplicate schema keywords.";
        }

        foreach (var keyword in UnsupportedAssertionKeywords)
        {
            if (schema.TryGetProperty(keyword, out _))
            {
                return $"The tool schema keyword '{keyword}' is not supported by the built-in validator.";
            }
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var validType = type.ValueKind == JsonValueKind.String
                ? IsKnownType(type.GetString())
                : type.ValueKind == JsonValueKind.Array
                    && type.GetArrayLength() > 0
                    && type.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && IsKnownType(item.GetString()))
                    && type.EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count()
                        == type.GetArrayLength();
            if (!validType)
            {
                return path + ".type must contain one or more supported JSON types.";
            }
        }

        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(keyword, out var candidates))
            {
                continue;
            }

            if (candidates.ValueKind != JsonValueKind.Array)
            {
                return path + "." + keyword + " must be an array of schemas.";
            }

            if (candidates.GetArrayLength() == 0)
            {
                return path + "." + keyword + " must contain at least one schema.";
            }

            var index = 0;
            foreach (var candidate in candidates.EnumerateArray())
            {
                var error = ValidateSchemaNode(candidate, path + "." + keyword + "[" + index + "]", depth + 1);
                if (error is not null)
                {
                    return error;
                }

                index++;
            }
        }

        if (schema.TryGetProperty("not", out var notSchema))
        {
            var error = ValidateSchemaNode(notSchema, path + ".not", depth + 1);
            if (error is not null)
            {
                return error;
            }
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                return path + ".properties must be an object.";
            }

            if (HasDuplicateProperties(properties))
            {
                return path + ".properties cannot contain duplicate names.";
            }

            foreach (var property in properties.EnumerateObject())
            {
                var error = ValidateSchemaNode(property.Value, path + ".properties." + property.Name, depth + 1);
                if (error is not null)
                {
                    return error;
                }
            }
        }

        if (schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            var error = ValidateSchemaNode(additional, path + ".additionalProperties", depth + 1);
            if (error is not null)
            {
                return error;
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            var error = ValidateSchemaNode(items, path + ".items", depth + 1);
            if (error is not null)
            {
                return error;
            }
        }

        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array
                || required.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                return path + ".required must be an array of strings.";
            }

            var names = required.EnumerateArray().Select(item => item.GetString()!).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            {
                return path + ".required cannot contain duplicate names.";
            }
        }

        if (schema.TryGetProperty("enum", out var enumeration)
            && (enumeration.ValueKind != JsonValueKind.Array || enumeration.GetArrayLength() == 0))
        {
            return path + ".enum must be a non-empty array.";
        }

        if (enumeration.ValueKind == JsonValueKind.Array)
        {
            var canonicalValues = enumeration.EnumerateArray().Select(Canonicalize).ToArray();
            if (canonicalValues.Distinct(StringComparer.Ordinal).Count() != canonicalValues.Length)
            {
                return path + ".enum cannot contain duplicate values.";
            }
        }

        foreach (var keyword in new[] { "minProperties", "maxProperties", "minItems", "maxItems", "minLength", "maxLength" })
        {
            if (schema.TryGetProperty(keyword, out var value)
                && (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetInt32(out var number)
                    || number < 0))
            {
                return path + "." + keyword + " must be a non-negative integer.";
            }
        }

        if (schema.TryGetProperty("uniqueItems", out var unique)
            && unique.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return path + ".uniqueItems must be a boolean.";
        }

        if (schema.TryGetProperty("pattern", out var pattern))
        {
            if (pattern.ValueKind != JsonValueKind.String)
            {
                return path + ".pattern must be a string.";
            }

            try
            {
                _ = new Regex(pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return path + ".pattern contains an invalid regular expression.";
            }
        }

        foreach (var keyword in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" })
        {
            if (schema.TryGetProperty(keyword, out var value)
                && value.ValueKind != JsonValueKind.Number)
            {
                return path + "." + keyword + " must be a finite number.";
            }


            if (value.ValueKind == JsonValueKind.Number)
            {
                _ = ParseNumber(value.GetRawText());
            }
        }

        return null;
    }

    private static bool IsKnownType(string? type) =>
        type is "object" or "array" or "string" or "number" or "integer" or "boolean" or "null";

    private static string? ValidateJsonValue(JsonElement value, string path, int depth)
    {
        if (depth > MaxDepth)
        {
            return path + " exceeds the maximum JSON validation depth.";
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                return path + " cannot contain duplicate property names.";
            }

            foreach (var property in properties)
            {
                var error = ValidateJsonValue(property.Value, path + "." + property.Name, depth + 1);
                if (error is not null)
                {
                    return error;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var error = ValidateJsonValue(item, path + "[" + index + "]", depth + 1);
                if (error is not null)
                {
                    return error;
                }

                index++;
            }
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            _ = ParseNumber(value.GetRawText());
        }

        return null;
    }

    private static string? ValidateNode(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (depth > MaxDepth)
        {
            return path + " exceeds the maximum schema validation depth.";
        }

        if (schema.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            return path + " is rejected by the tool schema.";
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "The tool schema must be a JSON object or boolean.";
        }

        foreach (var keyword in UnsupportedAssertionKeywords)
        {
            if (schema.TryGetProperty(keyword, out _))
            {
                return $"The tool schema keyword '{keyword}' is not supported by the built-in validator.";
            }
        }

        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in allOf.EnumerateArray())
            {
                var error = ValidateNode(candidate, value, path, depth + 1);
                if (error is not null)
                {
                    return error;
                }
            }
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            if (!anyOf.EnumerateArray().Any(candidate => ValidateNode(candidate, value, path, depth + 1) is null))
            {
                return path + " does not match any allowed schema.";
            }
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            var matches = oneOf.EnumerateArray().Count(candidate => ValidateNode(candidate, value, path, depth + 1) is null);
            if (matches != 1)
            {
                return path + " must match exactly one allowed schema.";
            }
        }

        if (schema.TryGetProperty("not", out var notSchema)
            && ValidateNode(notSchema, value, path, depth + 1) is null)
        {
            return path + " matches a disallowed schema.";
        }

        var typeError = ValidateType(schema, value, path);
        if (typeError is not null)
        {
            return typeError;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, value))
        {
            return path + " does not match the required constant value.";
        }

        if (schema.TryGetProperty("enum", out var enumeration) && enumeration.ValueKind == JsonValueKind.Array)
        {
            if (!enumeration.EnumerateArray().Any(candidate => JsonEquals(candidate, value)))
            {
                return path + " is not one of the allowed values.";
            }
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => ValidateObject(schema, value, path, depth),
            JsonValueKind.Array => ValidateArray(schema, value, path, depth),
            JsonValueKind.String => ValidateString(schema, value, path),
            JsonValueKind.Number => ValidateNumber(schema, value, path),
            _ => null,
        };
    }

    private static string? ValidateType(JsonElement schema, JsonElement value, string path)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return null;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return MatchesType(type.GetString()!, value)
                ? null
                : path + " must be of type " + type.GetString() + ".";
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && MatchesType(item.GetString()!, value))
                ? null
                : path + " does not match any allowed JSON type.";
        }

        return "The tool schema type keyword must be a string or array.";
    }

    private static bool MatchesType(string type, JsonElement value)
    {
        return type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };
    }

    private static string? ValidateObject(JsonElement schema, JsonElement value, string path, int depth)
    {
        var properties = value.EnumerateObject().ToArray();
        if (properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            return path + " cannot contain duplicate property names.";
        }

        if (TryGetInt(schema, "minProperties", out var minimum) && properties.Length < minimum)
        {
            return path + " has fewer properties than allowed.";
        }

        if (TryGetInt(schema, "maxProperties", out var maximum) && properties.Length > maximum)
        {
            return path + " has more properties than allowed.";
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String || !value.TryGetProperty(name.GetString()!, out _))
                {
                    return path + "." + (name.GetString() ?? "<invalid>") + " is required.";
                }
            }
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var propertySchemas)
            && propertySchemas.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertySchema in propertySchemas.EnumerateObject())
            {
                declared.Add(propertySchema.Name);
                if (!value.TryGetProperty(propertySchema.Name, out var propertyValue))
                {
                    continue;
                }

                var error = ValidateNode(propertySchema.Value, propertyValue, path + "." + propertySchema.Name, depth + 1);
                if (error is not null)
                {
                    return error;
                }
            }
        }

        if (!schema.TryGetProperty("additionalProperties", out var additional))
        {
            return null;
        }

        foreach (var property in properties.Where(property => !declared.Contains(property.Name)))
        {
            if (additional.ValueKind == JsonValueKind.False)
            {
                return path + "." + property.Name + " is not an allowed property.";
            }

            if (additional.ValueKind == JsonValueKind.Object)
            {
                var error = ValidateNode(additional, property.Value, path + "." + property.Name, depth + 1);
                if (error is not null)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static string? ValidateArray(JsonElement schema, JsonElement value, string path, int depth)
    {
        var items = value.EnumerateArray().ToArray();
        if (TryGetInt(schema, "minItems", out var minimum) && items.Length < minimum)
        {
            return path + " has fewer items than allowed.";
        }

        if (TryGetInt(schema, "maxItems", out var maximum) && items.Length > maximum)
        {
            return path + " has more items than allowed.";
        }

        if (schema.TryGetProperty("uniqueItems", out var unique)
            && unique.ValueKind == JsonValueKind.True
            && items.Select(Canonicalize).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            return path + " must contain unique items.";
        }

        if (!schema.TryGetProperty("items", out var itemSchema))
        {
            return null;
        }

        for (var index = 0; index < items.Length; index++)
        {
            var error = ValidateNode(itemSchema, items[index], path + "[" + index + "]", depth + 1);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static string? ValidateString(JsonElement schema, JsonElement value, string path)
    {
        var text = value.GetString() ?? string.Empty;
        if (TryGetInt(schema, "minLength", out var minimum) && text.Length < minimum)
        {
            return path + " is shorter than allowed.";
        }

        if (TryGetInt(schema, "maxLength", out var maximum) && text.Length > maximum)
        {
            return path + " is longer than allowed.";
        }

        if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String)
        {
            try
            {
                if (!Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                {
                    return path + " does not match the required pattern.";
                }
            }
            catch (ArgumentException)
            {
                return "The tool schema contains an invalid regular expression.";
            }
            catch (RegexMatchTimeoutException)
            {
                return path + " pattern validation timed out.";
            }
        }

        return null;
    }

    private static string? ValidateNumber(JsonElement schema, JsonElement value, string path)
    {
        var number = ParseNumber(value.GetRawText());

        if (TryGetNumber(schema, "minimum", out var minimum) && CompareNumbers(number, minimum) < 0)
        {
            return path + " is smaller than allowed.";
        }

        if (TryGetNumber(schema, "maximum", out var maximum) && CompareNumbers(number, maximum) > 0)
        {
            return path + " is larger than allowed.";
        }

        if (TryGetNumber(schema, "exclusiveMinimum", out var exclusiveMinimum)
            && CompareNumbers(number, exclusiveMinimum) <= 0)
        {
            return path + " must be greater than the exclusive minimum.";
        }

        if (TryGetNumber(schema, "exclusiveMaximum", out var exclusiveMaximum)
            && CompareNumbers(number, exclusiveMaximum) >= 0)
        {
            return path + " must be smaller than the exclusive maximum.";
        }

        return null;
    }

    private static bool IsInteger(JsonElement value)
    {
        var number = ParseNumber(value.GetRawText());
        return number.Digits == "0" || number.Exponent >= BigInteger.Zero;
    }

    private static bool JsonEquals(JsonElement left, JsonElement right) =>
        string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.Ordinal);

    private static string Canonicalize(JsonElement value)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, value);
        return builder.ToString();
    }

    private static void AppendCanonical(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                foreach (var property in value.EnumerateObject()
                             .Select(item => (item.Name, Value: Canonicalize(item.Value)))
                             .OrderBy(item => item.Name, StringComparer.Ordinal)
                             .ThenBy(item => item.Value, StringComparer.Ordinal))
                {
                    AppendLengthPrefixed(builder, property.Name);
                    AppendLengthPrefixed(builder, property.Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                foreach (var item in value.EnumerateArray())
                {
                    var canonical = Canonicalize(item);
                    AppendLengthPrefixed(builder, canonical);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append('s');
                AppendLengthPrefixed(builder, value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                builder.Append('d').Append(CanonicalizeNumber(value.GetRawText()));
                break;
            case JsonValueKind.True:
                builder.Append('t');
                break;
            case JsonValueKind.False:
                builder.Append('f');
                break;
            case JsonValueKind.Null:
                builder.Append('n');
                break;
            default:
                throw new InvalidOperationException("Unsupported JSON value kind.");
        }
    }

    private static void AppendLengthPrefixed(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    private static string CanonicalizeNumber(string raw)
    {
        var number = ParseNumber(raw);
        return (number.Negative ? "-" : string.Empty)
            + number.Digits
            + "e"
            + number.Exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static NumberValue ParseNumber(string raw)
    {
        if (raw.Length > MaxNumberCharacters)
        {
            throw new NumberLimitException($"A JSON number exceeds the {MaxNumberCharacters}-character validation limit.");
        }

        var negative = raw[0] == '-';
        var start = negative ? 1 : 0;
        var exponentIndex = raw.IndexOfAny(ExponentMarkers, start);
        var mantissaEnd = exponentIndex < 0 ? raw.Length : exponentIndex;
        var decimalIndex = raw.IndexOf('.', start, mantissaEnd - start);
        var fractionalDigits = decimalIndex < 0 ? 0 : mantissaEnd - decimalIndex - 1;
        var digits = raw.Substring(start, mantissaEnd - start).Replace(".", string.Empty);
        digits = digits.TrimStart('0');
        if (digits.Length == 0)
        {
            return new NumberValue(false, "0", BigInteger.Zero);
        }

        var exponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(raw.Substring(exponentIndex + 1), CultureInfo.InvariantCulture);
        exponent -= fractionalDigits;
        var trailingZeros = 0;
        while (trailingZeros < digits.Length - 1 && digits[digits.Length - trailingZeros - 1] == '0')
        {
            trailingZeros++;
        }

        if (trailingZeros > 0)
        {
            digits = digits.Substring(0, digits.Length - trailingZeros);
            exponent += trailingZeros;
        }

        return new NumberValue(negative, digits, exponent);
    }

    private static int CompareNumbers(NumberValue left, NumberValue right)
    {
        if (left.Digits == "0" || right.Digits == "0")
        {
            if (left.Digits == right.Digits)
            {
                return 0;
            }

            return left.Digits == "0" ? (right.Negative ? 1 : -1) : (left.Negative ? -1 : 1);
        }

        if (left.Negative != right.Negative)
        {
            return left.Negative ? -1 : 1;
        }

        var absolute = CompareAbsolute(left, right);
        return left.Negative ? -absolute : absolute;
    }

    private static int CompareAbsolute(NumberValue left, NumberValue right)
    {
        var leftMagnitude = left.Exponent + left.Digits.Length;
        var rightMagnitude = right.Exponent + right.Digits.Length;
        var magnitudeComparison = leftMagnitude.CompareTo(rightMagnitude);
        if (magnitudeComparison != 0)
        {
            return magnitudeComparison;
        }

        var width = Math.Max(left.Digits.Length, right.Digits.Length);
        for (var index = 0; index < width; index++)
        {
            var leftDigit = index < left.Digits.Length ? left.Digits[index] : '0';
            var rightDigit = index < right.Digits.Length ? right.Digits[index] : '0';
            if (leftDigit != rightDigit)
            {
                return leftDigit.CompareTo(rightDigit);
            }
        }

        return 0;
    }

    private static bool TryGetInt(JsonElement schema, string name, out int value)
    {
        value = default;
        return schema.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static bool TryGetNumber(JsonElement schema, string name, out NumberValue value)
    {
        value = default;
        if (!schema.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = ParseNumber(property.GetRawText());
        return true;
    }

    private static bool HasDuplicateProperties(JsonElement value) =>
        value.EnumerateObject()
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);

    private readonly struct NumberValue
    {
        public NumberValue(bool negative, string digits, BigInteger exponent)
        {
            Negative = negative;
            Digits = digits;
            Exponent = exponent;
        }

        public bool Negative { get; }

        public string Digits { get; }

        public BigInteger Exponent { get; }
    }

    private sealed class NumberLimitException : Exception
    {
        public NumberLimitException(string message)
            : base(message)
        {
        }
    }
}

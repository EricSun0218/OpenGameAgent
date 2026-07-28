using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

public sealed class JsonValueLimits
{
    public JsonValueLimits(
        int maxUtf8Bytes = 262_144,
        int maxDepth = 32,
        int maxNodes = 8_192,
        int maxStringUtf8Bytes = 65_536,
        int maxContainerItems = 2_048)
    {
        if (maxUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        }

        if (maxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }

        if (maxNodes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNodes));
        }

        if (maxStringUtf8Bytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStringUtf8Bytes));
        }

        if (maxContainerItems < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContainerItems));
        }

        MaxUtf8Bytes = maxUtf8Bytes;
        MaxDepth = maxDepth;
        MaxNodes = maxNodes;
        MaxStringUtf8Bytes = maxStringUtf8Bytes;
        MaxContainerItems = maxContainerItems;
    }

    public int MaxUtf8Bytes { get; }

    public int MaxDepth { get; }

    public int MaxNodes { get; }

    public int MaxStringUtf8Bytes { get; }

    public int MaxContainerItems { get; }
}

public sealed class RuntimeContentLimitException : ArgumentException
{
    public RuntimeContentLimitException(string parameterName, string limitCode, string message)
        : base(message, parameterName)
    {
        LimitCode = limitCode;
    }

    public string LimitCode { get; }
}

public static class JsonValueInspector
{
    public static int ValidateAndMeasure(
        JsonElement value,
        JsonValueLimits limits,
        string parameterName)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "json_undefined",
                "An undefined JSON value is not allowed.");
        }

        var exactUtf8Bytes = Encoding.UTF8.GetByteCount(value.GetRawText());
        if (exactUtf8Bytes > limits.MaxUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "json_bytes_exceeded",
                $"JSON content exceeds {limits.MaxUtf8Bytes} UTF-8 bytes.");
        }

        var state = new InspectionState(limits, parameterName);
        Inspect(value, 1, state);
        return exactUtf8Bytes;
    }

    private static void Inspect(JsonElement value, int depth, InspectionState state)
    {
        if (depth > state.Limits.MaxDepth)
        {
            throw new RuntimeContentLimitException(
                state.ParameterName,
                "json_depth_exceeded",
                $"JSON depth exceeds {state.Limits.MaxDepth}.");
        }

        state.Nodes++;
        if (state.Nodes > state.Limits.MaxNodes)
        {
            throw new RuntimeContentLimitException(
                state.ParameterName,
                "json_nodes_exceeded",
                $"JSON node count exceeds {state.Limits.MaxNodes}.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                InspectObject(value, depth, state);
                break;
            case JsonValueKind.Array:
                InspectArray(value, depth, state);
                break;
            case JsonValueKind.String:
                AddStringBytes(value.GetString() ?? string.Empty, state);
                break;
            case JsonValueKind.Number:
                state.Utf8Bytes = checked(state.Utf8Bytes + value.GetRawText().Length);
                break;
            case JsonValueKind.True:
                state.Utf8Bytes = checked(state.Utf8Bytes + 4);
                break;
            case JsonValueKind.False:
                state.Utf8Bytes = checked(state.Utf8Bytes + 5);
                break;
            case JsonValueKind.Null:
                state.Utf8Bytes = checked(state.Utf8Bytes + 4);
                break;
            default:
                throw new RuntimeContentLimitException(
                    state.ParameterName,
                    "json_kind_unsupported",
                    $"JSON kind '{value.ValueKind}' is not supported.");
        }
    }

    private static void InspectObject(JsonElement value, int depth, InspectionState state)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        state.Utf8Bytes = checked(state.Utf8Bytes + 2);
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (count > state.Limits.MaxContainerItems)
            {
                ThrowContainerLimit(state);
            }

            if (!names.Add(property.Name))
            {
                throw new RuntimeContentLimitException(
                    state.ParameterName,
                    "json_duplicate_property",
                    $"Duplicate JSON property '{property.Name}' is not allowed.");
            }

            AddStringBytes(property.Name, state);
            Inspect(property.Value, depth + 1, state);
        }
    }

    private static void InspectArray(JsonElement value, int depth, InspectionState state)
    {
        var count = 0;
        state.Utf8Bytes = checked(state.Utf8Bytes + 2);
        foreach (var item in value.EnumerateArray())
        {
            count++;
            if (count > state.Limits.MaxContainerItems)
            {
                ThrowContainerLimit(state);
            }

            Inspect(item, depth + 1, state);
        }
    }

    private static void AddStringBytes(string value, InspectionState state)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > state.Limits.MaxStringUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                state.ParameterName,
                "json_string_bytes_exceeded",
                $"A JSON string exceeds {state.Limits.MaxStringUtf8Bytes} UTF-8 bytes.");
        }

        state.Utf8Bytes = checked(state.Utf8Bytes + bytes + 2);
    }

    private static void ThrowContainerLimit(InspectionState state)
    {
        throw new RuntimeContentLimitException(
            state.ParameterName,
            "json_container_items_exceeded",
            $"A JSON container exceeds {state.Limits.MaxContainerItems} items.");
    }

    private sealed class InspectionState
    {
        public InspectionState(JsonValueLimits limits, string parameterName)
        {
            Limits = limits;
            ParameterName = parameterName;
        }

        public JsonValueLimits Limits { get; }

        public string ParameterName { get; }

        public int Nodes { get; set; }

        public int Utf8Bytes { get; set; }
    }
}

internal static class RuntimeGuard
{
    public static string RequiredId(string? value, string parameterName)
    {
        var validated = RequiredUtf8(value, 128, parameterName);
        foreach (var character in validated)
        {
            var allowed = character is >= 'A' and <= 'Z'
                          || character is >= 'a' and <= 'z'
                          || character is >= '0' and <= '9'
                          || character is '.' or '_' or ':' or '-';
            if (!allowed)
            {
                throw new ArgumentException(
                    "An identifier may contain only ASCII letters, digits, '.', '_', ':', and '-'.",
                    parameterName);
            }
        }

        return validated;
    }

    public static string RequiredUtf8(string? value, int maxUtf8Bytes, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        if (Encoding.UTF8.GetByteCount(value) > maxUtf8Bytes)
        {
            throw new RuntimeContentLimitException(
                parameterName,
                "string_bytes_exceeded",
                $"The value exceeds {maxUtf8Bytes} UTF-8 bytes.");
        }

        return value;
    }

    public static IReadOnlyList<string> CopyStrings(
        IEnumerable<string>? values,
        int maxItems,
        int maxItemUtf8Bytes,
        string parameterName,
        bool sort,
        bool requireUnique)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = requireUnique
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;

        foreach (var value in values)
        {
            if (result.Count >= maxItems)
            {
                throw new RuntimeContentLimitException(
                    parameterName,
                    "collection_items_exceeded",
                    $"The collection exceeds {maxItems} items.");
            }

            var validated = RequiredUtf8(value, maxItemUtf8Bytes, parameterName);
            if (seen is not null && !seen.Add(validated))
            {
                throw new ArgumentException(
                    $"Duplicate value '{validated}' is not allowed.",
                    parameterName);
            }

            result.Add(validated);
        }

        if (sort)
        {
            result.Sort(StringComparer.Ordinal);
        }

        return new ReadOnlyCollection<string>(result);
    }

    public static IReadOnlyDictionary<string, JsonElement> CopyExtensions(
        IReadOnlyDictionary<string, JsonElement>? extensions,
        JsonValueLimits limits,
        int maxItems = 64)
    {
        if (extensions is null || extensions.Count == 0)
        {
            return new ReadOnlyDictionary<string, JsonElement>(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }

        if (extensions.Count > maxItems)
        {
            throw new RuntimeContentLimitException(
                nameof(extensions),
                "extension_items_exceeded",
                $"Extensions exceed {maxItems} items.");
        }

        var copied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in extensions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var key = RequiredUtf8(pair.Key, 128, nameof(extensions));
            JsonValueInspector.ValidateAndMeasure(pair.Value, limits, nameof(extensions));
            copied.Add(key, pair.Value.Clone());
        }

        return new ReadOnlyDictionary<string, JsonElement>(copied);
    }
}

internal sealed class CanonicalDigestBuilder
{
    private readonly StringBuilder _builder = new();

    public void Add(string name, string? value)
    {
        AppendLengthPrefixed(name);
        AppendLengthPrefixed(value ?? string.Empty);
    }

    public void Add(string name, long value)
    {
        Add(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Add(string name, IEnumerable<string> values)
    {
        AppendLengthPrefixed(name);
        var materialized = values as IReadOnlyCollection<string> ?? values.ToArray();
        _builder.Append(materialized.Count);
        _builder.Append(':');
        foreach (var value in materialized)
        {
            AppendLengthPrefixed(value);
        }
    }

    public void Add(string name, JsonElement value)
    {
        AppendLengthPrefixed(name);
        WriteCanonicalJson(_builder, value);
    }

    public string Finish()
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(_builder.ToString());
        var digest = sha.ComputeHash(bytes);
        var result = new StringBuilder(digest.Length * 2);
        foreach (var item in digest)
        {
            result.Append(item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private void AppendLengthPrefixed(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        _builder.Append(bytes);
        _builder.Append(':');
        _builder.Append(value);
    }

    private static void WriteCanonicalJson(StringBuilder output, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var firstProperty = true;
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        output.Append(',');
                    }

                    firstProperty = false;
                    WriteJsonString(output, property.Name);
                    output.Append(':');
                    WriteCanonicalJson(output, property.Value);
                }

                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        output.Append(',');
                    }

                    firstItem = false;
                    WriteCanonicalJson(output, item);
                }

                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteJsonString(output, value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                output.Append(value.GetRawText());
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new ArgumentException("Undefined JSON cannot be canonicalized.", nameof(value));
        }
    }

    private static void WriteJsonString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        output.Append("\\u");
                        output.Append(((int)character).ToString(
                            "x4",
                            System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(character);
                    }

                    break;
            }
        }

        output.Append('"');
    }
}

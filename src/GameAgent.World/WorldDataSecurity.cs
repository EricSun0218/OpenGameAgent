using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using GameAgent.Core;

namespace GameAgent.World;

internal static class WorldArchivePath
{
    private static readonly HashSet<string> ReservedDeviceNames =
        new(
            new[]
            {
                "CON",
                "PRN",
                "AUX",
                "NUL",
                "COM1",
                "COM2",
                "COM3",
                "COM4",
                "COM5",
                "COM6",
                "COM7",
                "COM8",
                "COM9",
                "LPT1",
                "LPT2",
                "LPT3",
                "LPT4",
                "LPT5",
                "LPT6",
                "LPT7",
                "LPT8",
                "LPT9"
            },
            StringComparer.OrdinalIgnoreCase);

    public static string Validate(string? path, int maximumUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("\\", StringComparison.Ordinal)
            || path.Contains('\\')
            || path.Contains(':')
            || path.EndsWith("/", StringComparison.Ordinal)
            || path.IndexOf('\0') >= 0
            || Encoding.UTF8.GetByteCount(path) > maximumUtf8Bytes
            || !string.Equals(
                path,
                path.Normalize(NormalizationForm.FormC),
                StringComparison.Ordinal))
        {
            throw Error("Archive path is not a canonical relative path.");
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0
                || string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal)
                || segment.EndsWith(".", StringComparison.Ordinal)
                || segment.EndsWith(" ", StringComparison.Ordinal)
                || segment.Any(character => character < 0x20)
                || IsReserved(segment))
            {
                throw Error("Archive path contains an unsafe segment.");
            }
        }

        return path;
    }

    public static void EnsureUnique(IEnumerable<string> paths)
    {
        var ordinal = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!ordinal.Add(path) || !folded.Add(path))
            {
                throw new WorldDataContractException(
                    WorldDataReasonCodes.DuplicatePath,
                    "Archive contains duplicate or case-colliding paths.");
            }
        }
    }

    private static bool IsReserved(string segment)
    {
        var stem = segment.Split('.')[0];
        return ReservedDeviceNames.Contains(stem);
    }

    private static WorldDataContractException Error(string message)
    {
        return new WorldDataContractException(
            WorldDataReasonCodes.InvalidPath,
            message);
    }
}

internal static class WorldContentSafety
{
    private static readonly HashSet<string> ExecutableExtensions =
        new(
            new[]
            {
                ".app",
                ".bat",
                ".cmd",
                ".com",
                ".cs",
                ".dll",
                ".dylib",
                ".exe",
                ".gd",
                ".jar",
                ".js",
                ".mjs",
                ".msi",
                ".pck",
                ".ps1",
                ".py",
                ".sh",
                ".so",
                ".ts",
                ".vbs",
                ".wasm",
                ".zip"
            },
            StringComparer.OrdinalIgnoreCase);

    public static void RejectExecutable(
        string path,
        string mediaType,
        ReadOnlySpan<byte> content)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (ExecutableExtensions.Contains(extension)
            || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("executable", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("x-msdownload", StringComparison.OrdinalIgnoreCase)
            || HasExecutableMagic(content))
        {
            throw new WorldDataContractException(
                WorldDataReasonCodes.UnsafeContent,
                "Native packages cannot contain executable content.");
        }
    }

    private static bool HasExecutableMagic(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 4
            && ((content[0] == (byte)'M' && content[1] == (byte)'Z')
                || (content[0] == 0x7f
                    && content[1] == (byte)'E'
                    && content[2] == (byte)'L'
                    && content[3] == (byte)'F')
                || (content[0] == 0x00
                    && content[1] == 0x61
                    && content[2] == 0x73
                    && content[3] == 0x6d)))
        {
            return true;
        }

        if (content.Length < 4)
        {
            return false;
        }

        var magic = ((uint)content[0] << 24)
                    | ((uint)content[1] << 16)
                    | ((uint)content[2] << 8)
                    | content[3];
        return magic is 0xfeedface
            or 0xcefaedfe
            or 0xfeedfacf
            or 0xcffaedfe
            or 0xcafebabe;
    }
}

internal static class WorldDataJson
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static JsonDocument Parse(
        ReadOnlySpan<byte> utf8,
        WorldPackageLimits limits,
        string parameterName)
    {
        if (utf8.Length > limits.MaxFileBytes)
        {
            throw Error(
                WorldDataReasonCodes.ByteLimitExceeded,
                "JSON input exceeds its byte limit.");
        }

        try
        {
            _ = StrictUtf8.GetString(utf8.ToArray());
            RejectDuplicateProperties(
                utf8,
                limits.MaxJsonDepth,
                limits.MaxJsonNodes);
            var document = JsonDocument.Parse(
                utf8.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = limits.MaxJsonDepth
                });
            ValidateNoDuplicatePropertiesAndUnicode(document.RootElement);
            JsonValueInspector.ValidateAndMeasure(
                document.RootElement,
                limits.CreateJsonLimits(checked((int)utf8.Length)),
                parameterName);
            return document;
        }
        catch (WorldDataContractException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
            or DecoderFallbackException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            throw Error(
                WorldDataReasonCodes.InvalidJson,
                "JSON input is malformed or exceeds its limits.");
        }
    }

    public static IReadOnlyDictionary<string, JsonElement> CopyExtensionData(
        IReadOnlyDictionary<string, JsonElement>? values,
        string parameterName)
    {
        if (values is null)
        {
            return new ReadOnlyDictionary<string, JsonElement>(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }

        var bounded = WorldValidation.MaterializeBounded(
            values,
            256,
            () => new ArgumentException(
                "Extension data exceeds its entry limit.",
                parameterName));
        if (bounded.Length == 0)
        {
            return new ReadOnlyDictionary<string, JsonElement>(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }

        var copy = new SortedDictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (var pair in bounded)
        {
            var key = WorldValidation.Required(
                pair.Key,
                parameterName,
                256);
            if (!IsNamespaced(key))
            {
                throw new ArgumentException(
                    "Extension data keys must be namespaced.",
                    parameterName);
            }

            JsonValueInspector.ValidateAndMeasure(
                pair.Value,
                new JsonValueLimits(
                    maxUtf8Bytes: 262_144,
                    maxDepth: 32,
                    maxNodes: 8_192,
                    maxStringUtf8Bytes: 65_536,
                    maxContainerItems: 2_048),
                parameterName);
            ValidateNoDuplicatePropertiesAndUnicode(pair.Value);
            RejectNumbers(pair.Value, parameterName);
            if (!copy.TryAdd(key, pair.Value.Clone()))
            {
                throw new ArgumentException(
                    "Extension data contains duplicate keys.",
                    parameterName);
            }
        }

        return new ReadOnlyDictionary<string, JsonElement>(
            new Dictionary<string, JsonElement>(
                copy,
                StringComparer.Ordinal));
    }

    public static void RequireOnlyProperties(
        JsonElement value,
        ISet<string> knownProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Error(
                WorldDataReasonCodes.InvalidJson,
                "A JSON object was required.");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!knownProperties.Contains(property.Name))
            {
                throw Error(
                    WorldDataReasonCodes.UnknownField,
                    "Native data contains an unknown field.");
            }
        }
    }

    public static string RequiredString(
        JsonElement parent,
        string propertyName,
        int maximumUtf8Bytes = 512)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw Error(
                WorldDataReasonCodes.InvalidJson,
                "A required string field is missing or invalid.");
        }

        try
        {
            return WorldValidation.Required(
                value.GetString(),
                propertyName,
                maximumUtf8Bytes);
        }
        catch (ArgumentException)
        {
            throw Error(
                WorldDataReasonCodes.InvalidJson,
                "A required string field is invalid.");
        }
    }

    public static long RequiredInt64(
        JsonElement parent,
        string propertyName,
        long minimum = long.MinValue)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result)
            || result < minimum)
        {
            throw Error(
                WorldDataReasonCodes.InvalidJson,
                "A required integer field is missing or invalid.");
        }

        return result;
    }

    public static long RequiredCanonicalInt64String(
        JsonElement parent,
        string propertyName,
        long minimum = long.MinValue)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw Error(
                WorldDataReasonCodes.InvalidJson,
                "A required canonical Int64 string is invalid.");
        }

        var text = value.GetString();
        if (text is null
            || !long.TryParse(
                text,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var result)
            || result < minimum
            || !string.Equals(
                text,
                result.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw Error(
                WorldDataReasonCodes.InvalidJson,
                "A required canonical Int64 string is invalid.");
        }

        return result;
    }

    public static void ValidateNoDuplicatePropertiesAndUnicode(
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    ValidateUnicode(property.Name);
                    if (!names.Add(property.Name))
                    {
                        throw Error(
                            WorldDataReasonCodes.DuplicateJsonProperty,
                            "JSON input contains a duplicate property.");
                    }

                    ValidateNoDuplicatePropertiesAndUnicode(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ValidateNoDuplicatePropertiesAndUnicode(item);
                }

                break;
            case JsonValueKind.String:
                ValidateUnicode(value.GetString() ?? string.Empty);
                break;
        }
    }

    public static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement value)
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(
                                 item => item.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                value.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Error(
                    WorldDataReasonCodes.InvalidJson,
                    "Undefined JSON cannot be written canonically.");
        }
    }

    public static void RejectNumbers(
        JsonElement value,
        string parameterName)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                throw new ArgumentException(
                    "Authoritative JSON numbers must use canonical strings.",
                    parameterName);
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    RejectNumbers(property.Value, parameterName);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    RejectNumbers(item, parameterName);
                }

                break;
        }
    }

    private static bool IsNamespaced(string key)
    {
        if (key.Contains(':'))
        {
            return true;
        }

        var first = key.IndexOf('.');
        return first > 0 && first < key.LastIndexOf('.');
    }

    private static void ValidateUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsSurrogate(character))
            {
                continue;
            }

            if (!char.IsHighSurrogate(character)
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[index + 1]))
            {
                throw Error(
                    WorldDataReasonCodes.InvalidJson,
                    "JSON contains invalid Unicode.");
            }

            index++;
        }
    }

    private static void RejectDuplicateProperties(
        ReadOnlySpan<byte> utf8,
        int maxDepth,
        int maxNodes)
    {
        var reader = new Utf8JsonReader(
            utf8,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maxDepth
            });
        var objectNames = new Stack<HashSet<string>>();
        var nodes = 0;
        while (reader.Read())
        {
            if (++nodes > maxNodes)
            {
                throw Error(
                    WorldDataReasonCodes.InvalidJson,
                    "JSON input exceeds its node limit.");
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectNames.Push(new HashSet<string>(
                        StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    if (objectNames.Count == 0)
                    {
                        throw Error(
                            WorldDataReasonCodes.InvalidJson,
                            "JSON object nesting is invalid.");
                    }

                    _ = objectNames.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (objectNames.Count == 0
                        || !objectNames.Peek().Add(
                            reader.GetString() ?? string.Empty))
                    {
                        throw Error(
                            WorldDataReasonCodes.DuplicateJsonProperty,
                            "JSON input contains a duplicate property.");
                    }

                    break;
            }
        }
    }

    private static WorldDataContractException Error(
        string reasonCode,
        string message)
    {
        return new WorldDataContractException(reasonCode, message);
    }
}

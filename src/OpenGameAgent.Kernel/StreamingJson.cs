using System.Text;
using System.Text.Json;

namespace OpenGameAgent.Kernel;

/// <summary>
/// Produces a valid JSON object from complete or partially streamed tool arguments.
/// </summary>
public static class StreamingJson
{
    private const int MaximumDepth = 128;

    public static JsonElement ParseWithRepair(string json)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            var repaired = Repair(json);
            if (string.Equals(repaired, json, StringComparison.Ordinal))
            {
                throw;
            }

            using var document = JsonDocument.Parse(repaired, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            return document.RootElement.Clone();
        }
    }

    public static string ParseObject(string? partialJson)
    {
        if (string.IsNullOrWhiteSpace(partialJson))
        {
            return "{}";
        }

        var repaired = Repair(partialJson);
        if (HasExcessiveDepth(repaired))
        {
            return "{}";
        }

        if (TryNormalizeObject(repaired, out var normalized))
        {
            return normalized;
        }

        var completed = CompletePrefix(repaired.AsSpan());
        if (TryNormalizeObject(completed, out normalized))
        {
            return normalized;
        }

        var safeBoundary = LastBoundaryOutsideString(repaired);
        if (safeBoundary >= 0
            && TryNormalizeObject(
                CompletePrefix(repaired.AsSpan(0, safeBoundary + 1)),
                out normalized))
        {
            return normalized;
        }

        return "{}";
    }

    public static string Repair(string json)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        var result = new StringBuilder(json.Length);
        var inString = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (!inString)
            {
                result.Append(character);
                if (character == '"')
                {
                    inString = true;
                }

                continue;
            }

            if (character == '"')
            {
                result.Append(character);
                inString = false;
                continue;
            }

            if (character == '\\')
            {
                var next = index + 1 < json.Length ? json[index + 1] : '\0';
                if (next == 'u'
                    && index + 5 < json.Length
                    && IsHex(json[index + 2])
                    && IsHex(json[index + 3])
                    && IsHex(json[index + 4])
                    && IsHex(json[index + 5]))
                {
                    result.Append(json, index, 6);
                    index += 5;
                    continue;
                }

                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                {
                    result.Append(character).Append(next);
                    index++;
                    continue;
                }

                result.Append("\\\\");
                continue;
            }

            if (character <= '\u001f')
            {
                result.Append(character switch
                {
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => "\\u" + ((int)character).ToString("x4"),
                });
                continue;
            }

            result.Append(character);
        }

        return result.ToString();
    }

    private static string CompletePrefix(ReadOnlySpan<char> prefix)
    {
        var trimmed = prefix.TrimEnd();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return string.Empty;
        }

        var result = new StringBuilder(trimmed.Length + 16);
        var closers = new Stack<char>();
        var inString = false;
        var escaped = false;
        foreach (var character in trimmed)
        {
            result.Append(character);
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (closers.Count >= MaximumDepth)
                    {
                        return string.Empty;
                    }

                    closers.Push('}');
                    break;
                case '[':
                    if (closers.Count >= MaximumDepth)
                    {
                        return string.Empty;
                    }

                    closers.Push(']');
                    break;
                case '}':
                case ']':
                    if (closers.Count == 0 || closers.Pop() != character)
                    {
                        return string.Empty;
                    }

                    break;
            }
        }

        if (escaped)
        {
            result.Append('\\');
        }

        if (inString)
        {
            result.Append('"');
        }

        var last = LastNonWhitespace(result);
        if (last == ':')
        {
            result.Append("null");
        }
        else if (last == ',')
        {
            RemoveLastNonWhitespace(result);
        }

        foreach (var closer in closers)
        {
            result.Append(closer);
        }

        return result.ToString();
    }

    private static bool TryNormalizeObject(string json, out string normalized)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                normalized = document.RootElement.GetRawText();
                return true;
            }
        }
        catch (JsonException)
        {
        }

        normalized = "{}";
        return false;
    }

    private static bool HasExcessiveDepth(string json)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        foreach (var character in json)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character is '{' or '[')
            {
                depth++;
                if (depth > MaximumDepth)
                {
                    return true;
                }
            }
            else if (character is '}' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
        }

        return false;
    }

    private static int LastBoundaryOutsideString(string json)
    {
        var boundary = -1;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character is ':' or ',')
            {
                boundary = index;
            }
        }

        return boundary;
    }

    private static char LastNonWhitespace(StringBuilder builder)
    {
        for (var index = builder.Length - 1; index >= 0; index--)
        {
            if (!char.IsWhiteSpace(builder[index]))
            {
                return builder[index];
            }
        }

        return '\0';
    }

    private static void RemoveLastNonWhitespace(StringBuilder builder)
    {
        for (var index = builder.Length - 1; index >= 0; index--)
        {
            if (!char.IsWhiteSpace(builder[index]))
            {
                builder.Remove(index, 1);
                return;
            }
        }
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9'
        or >= 'a' and <= 'f'
        or >= 'A' and <= 'F';
}

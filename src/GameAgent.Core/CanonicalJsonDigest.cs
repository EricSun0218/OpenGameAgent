using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameAgent.Core;

/// <summary>
/// Computes a stable SHA-256 digest for JSON values. Object property order is
/// ignored, while array order and JSON number representations are preserved.
/// The returned digest is 64 lowercase hexadecimal characters.
/// </summary>
public static class CanonicalJsonDigest
{
    public const int MaximumUtf8Bytes = 262_144;

    public const int MaximumDepth = 32;

    public const int MaximumNodes = 8_192;

    public const int MaximumStringUtf8Bytes = 65_536;

    public const int MaximumContainerItems = 2_048;

    private static readonly JsonValueLimits DigestLimits = new(
        MaximumUtf8Bytes,
        MaximumDepth,
        MaximumNodes,
        MaximumStringUtf8Bytes,
        MaximumContainerItems);

    public static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    public static string ComputeSha256(JsonElement value)
    {
        JsonValueInspector.ValidateAndMeasure(
            value,
            DigestLimits,
            nameof(value));

        var canonical = new StringBuilder();
        AppendCanonical(canonical, value);
        using var sha = SHA256.Create();
        var bytes = StrictUtf8Encoding.GetBytes(canonical.ToString());
        var digest = sha.ComputeHash(bytes);
        var result = new StringBuilder(digest.Length * 2);
        foreach (var item in digest)
        {
            result.Append(item.ToString(
                "x2",
                System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    internal static void AppendCanonical(
        StringBuilder output,
        JsonElement value)
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
                    AppendJsonString(output, property.Name);
                    output.Append(':');
                    AppendCanonical(output, property.Value);
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
                    AppendCanonical(output, item);
                }

                output.Append(']');
                break;
            case JsonValueKind.String:
                AppendJsonString(output, value.GetString() ?? string.Empty);
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
                throw new ArgumentException(
                    "Undefined JSON cannot be canonicalized.",
                    nameof(value));
        }
    }

    private static void AppendJsonString(StringBuilder output, string value)
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

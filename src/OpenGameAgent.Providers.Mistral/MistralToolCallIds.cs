using System.Security.Cryptography;
using System.Text;
using OpenGameAgent.Kernel;

namespace OpenGameAgent.Providers.Mistral;

internal static class MistralToolCallIds
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string From(string source)
    {
        var normalized = new string(source.Where(char.IsLetterOrDigit).ToArray());
        if (normalized.Length == 9)
        {
            return normalized;
        }

        using var algorithm = SHA256.Create();
        var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(source));
        var builder = new StringBuilder(9);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5 && builder.Length < 9)
            {
                bits -= 5;
                builder.Append(Alphabet[(buffer >> bits) & 31]);
            }

            if (builder.Length == 9)
            {
                break;
            }
        }

        return builder.ToString();
    }

    public static ProviderToolCallIdNormalizer CreateNormalizer()
    {
        var forward = new Dictionary<string, string>(StringComparer.Ordinal);
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        return (id, _, _, _) =>
        {
            if (forward.TryGetValue(id, out var existing))
            {
                return existing;
            }

            for (var attempt = 0; ; attempt++)
            {
                var candidate = From(attempt == 0 ? id : id + ":" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!reverse.TryGetValue(candidate, out var owner) || owner == id)
                {
                    forward[id] = candidate;
                    reverse[candidate] = id;
                    return candidate;
                }
            }
        };
    }
}

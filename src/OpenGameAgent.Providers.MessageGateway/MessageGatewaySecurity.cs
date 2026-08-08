using System.Text;
using System.Text.Json;

namespace OpenGameAgent.Providers.MessageGateway;

internal sealed class MessageGatewaySecretRedactor
{
    private const string Replacement = "[redacted]";
    private readonly IReadOnlyList<string> _secrets;

    public MessageGatewaySecretRedactor(MessageGatewaySettings settings, string? accessToken)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        AddSecretVariants(secrets, accessToken);
        AddAuthorizationParts(secrets, accessToken);
        foreach (var pair in settings.Headers)
        {
            if (!IsSensitiveHeader(pair.Key))
            {
                continue;
            }

            AddSecretVariants(secrets, pair.Value);
            AddAuthorizationParts(secrets, pair.Value);
        }

        _secrets = Array.AsReadOnly(secrets
            .OrderByDescending(value => value.Length)
            .ToArray());
    }

    public string Sanitize(string value, int maximumCharacters)
    {
        var redacted = Redact(value);
        var builder = new StringBuilder(Math.Min(redacted.Length, maximumCharacters));
        foreach (var character in redacted)
        {
            if (builder.Length >= maximumCharacters)
            {
                break;
            }

            builder.Append(character is '\r' or '\n' or '\0' || char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }

    private string Redact(string value)
    {
        var result = value;
        foreach (var secret in _secrets)
        {
            result = result.Replace(secret, Replacement);
        }

        return result;
    }

    private static bool IsSensitiveHeader(string name) =>
        name.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("api-key", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("apikey", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0;

    private static void AddSecretVariants(ISet<string> secrets, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        secrets.Add(value);
        var json = JsonSerializer.Serialize(value);
        if (json.Length > 2)
        {
            secrets.Add(json.Substring(1, json.Length - 2));
        }

        try
        {
            secrets.Add(Uri.EscapeDataString(value));
        }
        catch (UriFormatException)
        {
            // The exact and JSON-escaped values remain protected.
        }
    }

    private static void AddAuthorizationParts(ISet<string> secrets, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var separator = value.IndexOf(' ');
        if (separator >= 0 && separator + 1 < value.Length)
        {
            AddSecretVariants(secrets, value.Substring(separator + 1));
        }
    }
}

namespace OpenGameAgent.ProviderTransport;

public static class ProviderHeaderGuard
{
    public const int MaximumHeaders = 64;
    public const int MaximumNameCharacters = 256;
    public const int MaximumValueCharacters = 65_536;

    public static void Validate(
        IEnumerable<KeyValuePair<string, string>> headers,
        string parameterName)
    {
        ValidateCore(headers.Select(pair =>
            new KeyValuePair<string, string?>(pair.Key, pair.Value)), parameterName, allowDeletion: false);
    }

    public static void ValidateMerge(
        IEnumerable<KeyValuePair<string, string?>> headers,
        string parameterName)
    {
        ValidateCore(headers, parameterName, allowDeletion: true);
    }

    private static void ValidateCore(
        IEnumerable<KeyValuePair<string, string?>> headers,
        string parameterName,
        bool allowDeletion)
    {
        if (headers is null)
        {
            throw new ArgumentNullException(nameof(headers));
        }

        var count = 0;
        foreach (var header in headers)
        {
            count++;
            if (count > MaximumHeaders
                || string.IsNullOrWhiteSpace(header.Key)
                || header.Key.Length > MaximumNameCharacters
                || header.Key.Any(character => !IsHeaderNameCharacter(character))
                || IsTransportControlledHeader(header.Key)
                || !allowDeletion && header.Value is null
                || (header.Value?.Length ?? 0) > MaximumValueCharacters
                || header.Value?.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new ArgumentException("Provider headers exceed their count or character bounds.", parameterName);
            }
        }
    }

    public static bool IsTransportControlledHeader(string? name) =>
        string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "TE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Trailer", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase)
        || name?.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsHeaderNameCharacter(char value) =>
        value is >= 'a' and <= 'z'
        || value is >= 'A' and <= 'Z'
        || value is >= '0' and <= '9'
        || value is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}

namespace GameAgent.Core;

internal static class RuntimeEventIdDerivation
{
    internal const int MaximumLength = 128;

    private const string DigestMarker = ":sha256:";
    private const int DigestMarkerLength = 8;
    private const string FallbackPrefix = "event";
    private const int Sha256HexLength = 64;
    private const int MaximumReadablePrefixLength =
        MaximumLength - DigestMarkerLength - Sha256HexLength;

    public static string Derive(string? runId, string? candidate)
    {
        ValidateRunId(runId);
        if (string.IsNullOrEmpty(candidate))
        {
            throw new ArgumentException(
                "A non-empty event identity key is required.",
                nameof(candidate));
        }

        var separator = candidate.IndexOf(':');
        var purpose = separator > 0
            ? candidate.Substring(0, separator)
            : FallbackPrefix;
        var semanticId = separator > 0
            ? candidate.Substring(separator + 1)
            : candidate;
        var prefix = purpose.Length <= MaximumReadablePrefixLength
                     && purpose.All(IsAllowed)
            ? purpose
            : FallbackPrefix;

        var digest = new CanonicalDigestBuilder();
        digest.Add("runId", runId);
        digest.Add("purpose", purpose);
        digest.Add("semanticId", semanticId);
        return prefix + DigestMarker + digest.Finish();
    }

    private static void ValidateRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException(
                "A non-empty run identifier is required.",
                nameof(runId));
        }

        if (runId.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"The run identifier cannot exceed {MaximumLength} characters.",
                nameof(runId));
        }

        foreach (var character in runId)
        {
            if (!IsAllowed(character))
            {
                throw new ArgumentException(
                    "A run identifier may contain only ASCII letters, digits, '.', '_', ':', and '-'.",
                    nameof(runId));
            }
        }
    }

    private static bool IsAllowed(char character)
    {
        return character is >= 'A' and <= 'Z'
               or >= 'a' and <= 'z'
               or >= '0' and <= '9'
               or '.'
               or '_'
               or ':'
               or '-';
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace GameAgent.World;

internal static class WorldValidation
{
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    public const int MaximumIdentifierUtf8Bytes = 192;

    public const int MaximumResourceKeys = 512;

    public const int MaximumParticipants = 4_096;

    public const int MaximumParameters = 128;

    public const int MaximumCatalogDefinitions = 8_192;

    public const int MaximumNumericSchemas = 8_192;

    public const int MaximumStates = 4_096;

    public const int MaximumConditionChildren = 8_192;

    public static string ComposeStableKey(params string[] components)
    {
        if (components is null)
        {
            throw new ArgumentNullException(nameof(components));
        }

        var builder = new StringBuilder();
        foreach (var component in components)
        {
            if (component is null)
            {
                throw new ArgumentException(
                    "Stable-key components cannot be null.",
                    nameof(components));
            }

            builder.Append(
                component.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(component);
        }

        return builder.ToString();
    }

    public static string Required(
        string? value,
        string parameterName,
        int maximumUtf8Bytes = MaximumIdentifierUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A non-empty value is required.",
                parameterName);
        }

        int utf8Bytes;
        try
        {
            utf8Bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                "The value contains invalid Unicode.",
                parameterName);
        }

        if (utf8Bytes > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "The value exceeds its UTF-8 byte limit.",
                parameterName);
        }

        return value;
    }

    public static string? Optional(
        string? value,
        string parameterName,
        int maximumUtf8Bytes = MaximumIdentifierUtf8Bytes)
    {
        return value is null
            ? null
            : Required(value, parameterName, maximumUtf8Bytes);
    }

    public static IReadOnlyList<string> CopyKeys(
        IEnumerable<string>? values,
        string parameterName,
        int maximumCount = MaximumResourceKeys)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var copy = MaterializeBounded(
                values,
                maximumCount,
                parameterName)
            .Select(value => Required(value, parameterName, 512))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        for (var index = 1; index < copy.Length; index++)
        {
            if (string.Equals(
                    copy[index - 1],
                    copy[index],
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The collection contains duplicate values.",
                    parameterName);
            }
        }

        return new ReadOnlyCollection<string>(copy);
    }

    public static T[] MaterializeBounded<T>(
        IEnumerable<T> values,
        int maximumCount,
        string parameterName)
    {
        return MaterializeBounded(
            values,
            maximumCount,
            () => new ArgumentException(
                "The collection exceeds its item limit.",
                parameterName));
    }

    public static T[] MaterializeBounded<T>(
        IEnumerable<T> values,
        int maximumCount,
        Func<Exception> limitExceeded)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        if (maximumCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        if (limitExceeded is null)
        {
            throw new ArgumentNullException(nameof(limitExceeded));
        }

        if (values is ICollection<T> collection
            && collection.Count > maximumCount)
        {
            throw limitExceeded();
        }

        if (values is IReadOnlyCollection<T> readOnlyCollection
            && readOnlyCollection.Count > maximumCount)
        {
            throw limitExceeded();
        }

        var copy = new List<T>(
            values is ICollection<T> knownCollection
                ? Math.Min(knownCollection.Count, maximumCount)
                : 0);
        foreach (var value in values)
        {
            if (copy.Count >= maximumCount)
            {
                throw limitExceeded();
            }

            copy.Add(value);
        }

        return copy.ToArray();
    }

    public static IReadOnlyDictionary<string, string> CopyParameters(
        IReadOnlyDictionary<string, string>? values,
        string parameterName)
    {
        if (values is null)
        {
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var bounded = MaterializeBounded(
            values,
            MaximumParameters,
            () => new ArgumentException(
                "The parameter collection exceeds its item limit.",
                parameterName));
        if (bounded.Length == 0)
        {
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var copy = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var pair in bounded)
        {
            var key = Required(pair.Key, parameterName, 192);
            var value = Required(pair.Value, parameterName, 2_048);
            if (!copy.TryAdd(key, value))
            {
                throw new ArgumentException(
                    "The parameter collection contains duplicate keys.",
                    parameterName);
            }
        }

        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(copy, StringComparer.Ordinal));
    }
}

public static class WorldEvolutionReasonCodes
{
    public const string DefinitionLimitExceeded =
        "world_definition_limit_exceeded";

    public const string CandidateLimitExceeded =
        "world_candidate_limit_exceeded";

    public const string ParticipantLimitExceeded =
        "world_participant_limit_exceeded";

    public const string ResourceLimitExceeded =
        "world_resource_limit_exceeded";

    public const string CascadeLimitExceeded =
        "world_cascade_limit_exceeded";

    public const string BatchLimitExceeded =
        "world_batch_limit_exceeded";

    public const string MissingHandler =
        "world_handler_missing";

    public const string InvalidHandlerResult =
        "world_handler_result_invalid";

    public const string InvalidHistory =
        "world_history_invalid";
}

public sealed class WorldEvolutionLimitException : InvalidOperationException
{
    public WorldEvolutionLimitException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
    }

    public string ReasonCode { get; }
}

public sealed class WorldEventConfigurationException : InvalidOperationException
{
    public WorldEventConfigurationException(
        string reasonCode,
        string message)
        : base(message)
    {
        ReasonCode = WorldValidation.Required(
            reasonCode,
            nameof(reasonCode),
            96);
    }

    public string ReasonCode { get; }
}

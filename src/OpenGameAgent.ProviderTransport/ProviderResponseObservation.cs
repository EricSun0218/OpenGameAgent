using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;

namespace OpenGameAgent.ProviderTransport;

public sealed class ProviderResponseObservation
{
    private const int MaximumIdentifierCharacters = 256;
    private const int MaximumModelCharacters = 1_024;
    private const int MaximumMetadataValueCharacters = 1_024;
    private const int MaximumMetadataEntriesToInspect = 256;
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "anthropic-request-id",
        "request-id",
        "retry-after",
        "retry-after-ms",
        "ratelimit-limit",
        "ratelimit-remaining",
        "ratelimit-reset",
        "x-amzn-requestid",
        "x-amz-request-id",
        "x-goog-request-id",
        "x-request-id",
        "x-ratelimit-limit-input-tokens",
        "x-ratelimit-limit-output-tokens",
        "x-ratelimit-limit-requests",
        "x-ratelimit-limit-tokens",
        "x-ratelimit-remaining-input-tokens",
        "x-ratelimit-remaining-output-tokens",
        "x-ratelimit-remaining-requests",
        "x-ratelimit-remaining-tokens",
        "x-ratelimit-reset-input-tokens",
        "x-ratelimit-reset-output-tokens",
        "x-ratelimit-reset-requests",
        "x-ratelimit-reset-tokens",
    };

    private ProviderResponseObservation(
        string providerId,
        string apiId,
        string model,
        int statusCode,
        IReadOnlyDictionary<string, string> metadata)
    {
        ProviderId = RequireBounded(providerId, MaximumIdentifierCharacters, nameof(providerId));
        ApiId = RequireBounded(apiId, MaximumIdentifierCharacters, nameof(apiId));
        Model = RequireBounded(model, MaximumModelCharacters, nameof(model));
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        StatusCode = statusCode;
        Metadata = metadata;
    }

    public string ProviderId { get; }

    public string ApiId { get; }

    public string Model { get; }

    public int StatusCode { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public static ProviderResponseObservation FromHttpResponse(
        string providerId,
        string apiId,
        string model,
        HttpResponseMessage response)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            if (!AllowedHeaders.Contains(header.Key))
            {
                continue;
            }

            metadata[header.Key.ToLowerInvariant()] = BoundedHeaderValue(header.Value);
        }

        return new ProviderResponseObservation(
            providerId,
            apiId,
            model,
            (int)response.StatusCode,
            new ReadOnlyDictionary<string, string>(metadata));
    }

    public static ProviderResponseObservation FromProviderResponse(
        string providerId,
        string apiId,
        string model,
        int statusCode,
        string? requestId = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            metadata["request-id"] = SanitizeAndBound(requestId, MaximumMetadataValueCharacters);
        }

        return new ProviderResponseObservation(
            providerId,
            apiId,
            model,
            statusCode,
            new ReadOnlyDictionary<string, string>(metadata));
    }

    public static ProviderResponseObservation FromResponseMetadata(
        string providerId,
        string apiId,
        string model,
        int statusCode,
        IReadOnlyDictionary<string, string>? responseHeaders)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var inspected = 0;
            foreach (var header in responseHeaders ?? new Dictionary<string, string>())
            {
                inspected++;
                if (inspected > MaximumMetadataEntriesToInspect)
                {
                    break;
                }

                if (!AllowedHeaders.Contains(header.Key))
                {
                    continue;
                }

                metadata[header.Key.ToLowerInvariant()] = SanitizeAndBound(
                    header.Value ?? string.Empty,
                    MaximumMetadataValueCharacters);
            }
        }
        catch
        {
            metadata.Clear();
        }

        return new ProviderResponseObservation(
            providerId,
            apiId,
            model,
            statusCode,
            new ReadOnlyDictionary<string, string>(metadata));
    }

    private static string BoundedHeaderValue(IEnumerable<string> values)
    {
        var builder = new StringBuilder(Math.Min(MaximumMetadataValueCharacters, 128));
        foreach (var value in values)
        {
            if (builder.Length > 0 && builder.Length < MaximumMetadataValueCharacters)
            {
                builder.Append(',');
            }

            AppendSanitized(builder, value, MaximumMetadataValueCharacters);
            if (builder.Length >= MaximumMetadataValueCharacters)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static string SanitizeAndBound(string value, int maximumCharacters)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        AppendSanitized(builder, value, maximumCharacters);
        return builder.ToString();
    }

    private static void AppendSanitized(StringBuilder builder, string? value, int maximumCharacters)
    {
        if (value is null)
        {
            return;
        }

        for (var index = 0; index < value.Length && builder.Length < maximumCharacters; index++)
        {
            var character = value[index];
            builder.Append(character is '\r' or '\n' or '\0' || char.IsControl(character) ? ' ' : character);
        }
    }

    private static string RequireBounded(string value, int maximumCharacters, string parameterName) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters
            ? throw new ArgumentException($"A value of 1 to {maximumCharacters} characters is required.", parameterName)
            : value;
}

public delegate ValueTask ProviderResponseObserver(
    ProviderResponseObservation observation,
    CancellationToken cancellationToken);

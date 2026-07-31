using System.Globalization;
using System.Text;

namespace GameAgent.Providers.Anthropic;

public sealed class AnthropicProviderOptions
{
    public string ProviderId { get; set; } = "anthropic";

    public Uri Endpoint { get; set; } =
        new("https://api.anthropic.com/v1/messages");

    public string ApiVersion { get; set; } = "2023-06-01";

    public string Model { get; set; } = string.Empty;

    public int MaxOutputTokens { get; set; } = 8_192;

    public int MaxContextTokens { get; set; } = 200_000;

    public int MaxSseLineCharacters { get; set; } = 512 * 1024;

    public int MaxSseEventCharacters { get; set; } = 2 * 1024 * 1024;

    public int MaxStreamCharacters { get; set; } = 16 * 1024 * 1024;

    public int MaxSseEvents { get; set; } = 16_384;

    public int MaxToolArgumentsUtf8Bytes { get; set; } = 256 * 1024;

    public string? InputUsdPerMillionTokens { get; set; }

    public string? CacheReadUsdPerMillionTokens { get; set; }

    public string? CacheWrite5mUsdPerMillionTokens { get; set; }

    public string? CacheWrite1hUsdPerMillionTokens { get; set; }

    public string? OutputUsdPerMillionTokens { get; set; }

    public bool AllowInsecureLoopback { get; set; }

    internal AnthropicProviderOptions Snapshot()
    {
        Validate();
        return new AnthropicProviderOptions
        {
            ProviderId = ProviderId,
            Endpoint = new Uri(Endpoint.AbsoluteUri, UriKind.Absolute),
            ApiVersion = ApiVersion,
            Model = Model,
            MaxOutputTokens = MaxOutputTokens,
            MaxContextTokens = MaxContextTokens,
            MaxSseLineCharacters = MaxSseLineCharacters,
            MaxSseEventCharacters = MaxSseEventCharacters,
            MaxStreamCharacters = MaxStreamCharacters,
            MaxSseEvents = MaxSseEvents,
            MaxToolArgumentsUtf8Bytes = MaxToolArgumentsUtf8Bytes,
            InputUsdPerMillionTokens = InputUsdPerMillionTokens,
            CacheReadUsdPerMillionTokens =
                CacheReadUsdPerMillionTokens,
            CacheWrite5mUsdPerMillionTokens =
                CacheWrite5mUsdPerMillionTokens,
            CacheWrite1hUsdPerMillionTokens =
                CacheWrite1hUsdPerMillionTokens,
            OutputUsdPerMillionTokens = OutputUsdPerMillionTokens,
            AllowInsecureLoopback = AllowInsecureLoopback
        };
    }

    private void Validate()
    {
        ValidateRequiredText(ProviderId, 128, nameof(ProviderId));
        ValidateRequiredText(Model, 256, nameof(Model));

        if (Endpoint is null
            || !Endpoint.IsAbsoluteUri
            || !string.IsNullOrEmpty(Endpoint.UserInfo)
            || !string.IsNullOrEmpty(Endpoint.Query)
            || !string.IsNullOrEmpty(Endpoint.Fragment)
            || !string.Equals(
                Endpoint.AbsolutePath,
                "/v1/messages",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Endpoint must be an absolute /v1/messages URI without credentials, query, or fragment.",
                nameof(Endpoint));
        }

        if (!string.Equals(
                Endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            && (!AllowInsecureLoopback
                || !string.Equals(
                    Endpoint.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                || !Endpoint.IsLoopback))
        {
            throw new ArgumentException(
                "Anthropic endpoints must use HTTPS.",
                nameof(Endpoint));
        }

        if (!string.Equals(
                ApiVersion,
                "2023-06-01",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "This provider dialect requires Anthropic API version 2023-06-01.",
                nameof(ApiVersion));
        }

        if (MaxOutputTokens < 1
            || MaxContextTokens < 1
            || MaxSseLineCharacters < 1
            || MaxSseEventCharacters < 1
            || MaxStreamCharacters < 1
            || MaxSseEvents < 1
            || MaxToolArgumentsUtf8Bytes < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxOutputTokens),
                "Provider limits must be positive.");
        }

        if (MaxSseEventCharacters > MaxStreamCharacters)
        {
            throw new ArgumentException(
                "The SSE event limit cannot exceed the stream limit.",
                nameof(MaxSseEventCharacters));
        }

        if (MaxSseLineCharacters > MaxStreamCharacters)
        {
            throw new ArgumentException(
                "The SSE line limit cannot exceed the stream limit.",
                nameof(MaxSseLineCharacters));
        }

        ValidatePrice(InputUsdPerMillionTokens, nameof(InputUsdPerMillionTokens));
        ValidatePrice(
            CacheReadUsdPerMillionTokens,
            nameof(CacheReadUsdPerMillionTokens));
        ValidatePrice(
            CacheWrite5mUsdPerMillionTokens,
            nameof(CacheWrite5mUsdPerMillionTokens));
        ValidatePrice(
            CacheWrite1hUsdPerMillionTokens,
            nameof(CacheWrite1hUsdPerMillionTokens));
        ValidatePrice(
            OutputUsdPerMillionTokens,
            nameof(OutputUsdPerMillionTokens));
    }

    private static void ValidateRequiredText(
        string? value,
        int maximumUtf8Bytes,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                "The value is missing or exceeds its supported UTF-8 limit.",
                parameterName);
        }
    }

    private static void ValidatePrice(string? value, string parameterName)
    {
        if (value is null)
        {
            return;
        }

        if (!decimal.TryParse(
                value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 0)
        {
            throw new ArgumentException(
                "Provider token pricing must be a non-negative decimal.",
                parameterName);
        }
    }
}

public interface IAnthropicApiKeySource
{
    ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken);
}

public sealed class StaticAnthropicApiKeySource : IAnthropicApiKeySource
{
    private readonly string _apiKey;

    public StaticAnthropicApiKeySource(string apiKey)
    {
        _apiKey = AnthropicApiKeyValidator.ValidateAndTrim(
            apiKey,
            nameof(apiKey));
    }

    public ValueTask<string> GetApiKeyAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<string>(_apiKey);
    }
}

internal static class AnthropicApiKeyValidator
{
    private const int MaximumLength = 8_192;

    internal static string ValidateAndTrim(
        string? apiKey,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException(
                "A non-empty API key is required.",
                parameterName);
        }

        var value = apiKey.Trim();
        if (value.Length > MaximumLength)
        {
            throw new ArgumentException(
                "The API key exceeds the supported length.",
                parameterName);
        }

        foreach (var character in value)
        {
            if (character is < '\u0021' or > '\u007e')
            {
                throw new ArgumentException(
                    "The API key contains an invalid header character.",
                    parameterName);
            }
        }

        return value;
    }
}

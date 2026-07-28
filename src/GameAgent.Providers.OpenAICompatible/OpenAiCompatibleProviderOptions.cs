namespace GameAgent.Providers.OpenAICompatible;

public sealed class OpenAiCompatibleProviderOptions
{
    public string ProviderId { get; set; } = "openai-compatible";

    public Uri BaseUri { get; set; } = new("https://api.deepseek.com");

    public string ChatCompletionsPath { get; set; } = "/chat/completions";

    public string Model { get; set; } = "deepseek-v4-pro";

    public int MaxOutputTokens { get; set; } = 32_768;

    public string? ThinkingMode { get; set; } = "enabled";

    public string? ReasoningEffort { get; set; } = "high";

    public bool IncludeUsage { get; set; } = true;

    public bool ReplayReasoningContent { get; set; } = true;

    public int MaxSseEventCharacters { get; set; } = 2 * 1024 * 1024;

    public int MaxSseLineCharacters { get; set; } = 512 * 1024;

    public int MaxContextTokens { get; set; } = 1_000_000;

    public string InputCacheHitUsdPerMillionTokens { get; set; } = "0.003625";

    public string InputCacheMissUsdPerMillionTokens { get; set; } = "0.435";

    public string OutputUsdPerMillionTokens { get; set; } = "0.87";

    public bool AllowInsecureLoopback { get; set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderId))
        {
            throw new ArgumentException("ProviderId is required.", nameof(ProviderId));
        }

        if (BaseUri is null || !BaseUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "BaseUri must be absolute.",
                nameof(BaseUri));
        }

        if (!string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            if (!AllowInsecureLoopback
                || !string.Equals(
                    BaseUri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.Ordinal)
                || !BaseUri.IsLoopback)
            {
                throw new ArgumentException(
                    "Remote provider endpoints must use HTTPS.",
                    nameof(BaseUri));
            }
        }

        if (!string.IsNullOrEmpty(BaseUri.UserInfo)
            || !string.IsNullOrEmpty(BaseUri.Query)
            || !string.IsNullOrEmpty(BaseUri.Fragment))
        {
            throw new ArgumentException(
                "BaseUri cannot contain credentials, a query, or a fragment.",
                nameof(BaseUri));
        }

        if (string.IsNullOrWhiteSpace(ChatCompletionsPath)
            || !ChatCompletionsPath.StartsWith("/", StringComparison.Ordinal)
            || ChatCompletionsPath.StartsWith("//", StringComparison.Ordinal)
            || Uri.TryCreate(
                ChatCompletionsPath,
                UriKind.Absolute,
                out _))
        {
            throw new ArgumentException(
                "ChatCompletionsPath must be a rooted relative path.",
                nameof(ChatCompletionsPath));
        }

        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 256)
        {
            throw new ArgumentException("Model is invalid.", nameof(Model));
        }

        if (MaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
        }

        if (!IncludeUsage)
        {
            throw new ArgumentException(
                "Streaming usage accounting is required.",
                nameof(IncludeUsage));
        }

        if (MaxSseEventCharacters <= 0
            || MaxSseLineCharacters <= 0
            || MaxContextTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSseEventCharacters),
                "Provider limits must be positive.");
        }

        if (ThinkingMode is not null
            && !string.Equals(ThinkingMode, "enabled", StringComparison.Ordinal)
            && !string.Equals(ThinkingMode, "disabled", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ThinkingMode must be enabled, disabled, or null.",
                nameof(ThinkingMode));
        }

        if (ReasoningEffort is not null
            && !string.Equals(ReasoningEffort, "high", StringComparison.Ordinal)
            && !string.Equals(ReasoningEffort, "max", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ReasoningEffort must be high, max, or null.",
                nameof(ReasoningEffort));
        }

        ValidatePrice(
            InputCacheHitUsdPerMillionTokens,
            nameof(InputCacheHitUsdPerMillionTokens));
        ValidatePrice(
            InputCacheMissUsdPerMillionTokens,
            nameof(InputCacheMissUsdPerMillionTokens));
        ValidatePrice(
            OutputUsdPerMillionTokens,
            nameof(OutputUsdPerMillionTokens));
    }

    internal OpenAiCompatibleProviderOptions Snapshot()
    {
        Validate();
        return new OpenAiCompatibleProviderOptions
        {
            ProviderId = ProviderId,
            BaseUri = new Uri(BaseUri.AbsoluteUri, UriKind.Absolute),
            ChatCompletionsPath = ChatCompletionsPath,
            Model = Model,
            MaxOutputTokens = MaxOutputTokens,
            ThinkingMode = ThinkingMode,
            ReasoningEffort = ReasoningEffort,
            IncludeUsage = IncludeUsage,
            ReplayReasoningContent = ReplayReasoningContent,
            MaxSseEventCharacters = MaxSseEventCharacters,
            MaxSseLineCharacters = MaxSseLineCharacters,
            MaxContextTokens = MaxContextTokens,
            InputCacheHitUsdPerMillionTokens =
                InputCacheHitUsdPerMillionTokens,
            InputCacheMissUsdPerMillionTokens =
                InputCacheMissUsdPerMillionTokens,
            OutputUsdPerMillionTokens = OutputUsdPerMillionTokens,
            AllowInsecureLoopback = AllowInsecureLoopback
        };
    }

    private static void ValidatePrice(string value, string parameterName)
    {
        if (!decimal.TryParse(
                value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 0)
        {
            throw new ArgumentException(
                "Provider token pricing must be a non-negative decimal.",
                parameterName);
        }
    }
}

public interface IProviderCredentialSource
{
    ValueTask<string> GetBearerTokenAsync(CancellationToken cancellationToken);
}

public sealed class StaticBearerTokenSource : IProviderCredentialSource
{
    private readonly string _token;

    public StaticBearerTokenSource(string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new ArgumentException(
                "A non-empty single-line bearer token is required.",
                nameof(token));
        }

        _token = token.Trim();
    }

    public ValueTask<string> GetBearerTokenAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<string>(_token);
    }
}

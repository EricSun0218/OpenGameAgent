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

        if (!IsRootedRelativePath(ChatCompletionsPath))
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

    private static bool IsRootedRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 8_192
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.IndexOf('\\') >= 0
            || value.IndexOf('#') >= 0
            || value.Any(char.IsControl)
            || HasPathTraversal(value))
        {
            return false;
        }

        var relative = value.Substring(1);
        return !HasSchemePrefix(relative)
               && !Uri.TryCreate(relative, UriKind.Absolute, out _)
               && Uri.TryCreate(relative, UriKind.Relative, out _)
               && Uri.IsWellFormedUriString(relative, UriKind.Relative);
    }

    private static bool HasPathTraversal(string value)
    {
        var query = value.IndexOf('?');
        var path = value.Substring(
            1,
            query < 0 ? value.Length - 1 : query - 1);
        for (var decodePass = 0; decodePass < 5; decodePass++)
        {
            if (path.StartsWith("/", StringComparison.Ordinal)
                || path.IndexOf('\\') >= 0
                || path.Split('/').Any(
                    segment => string.Equals(
                                   segment,
                                   ".",
                                   StringComparison.Ordinal)
                               || string.Equals(
                                   segment,
                                   "..",
                                   StringComparison.Ordinal)))
            {
                return true;
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(path);
            }
            catch (UriFormatException)
            {
                return true;
            }

            if (string.Equals(decoded, path, StringComparison.Ordinal))
            {
                return false;
            }

            path = decoded;
        }

        return true;
    }

    private static bool HasSchemePrefix(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var slash = value.IndexOf('/');
        var query = value.IndexOf('?');
        var firstBoundary = value.Length;
        if (slash >= 0)
        {
            firstBoundary = slash;
        }

        if (query >= 0 && query < firstBoundary)
        {
            firstBoundary = query;
        }

        return colon < firstBoundary
               && Uri.CheckSchemeName(value.Substring(0, colon));
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
        _token = BearerTokenValidator.ValidateAndTrim(
            token,
            nameof(token));
    }

    public ValueTask<string> GetBearerTokenAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<string>(_token);
    }
}

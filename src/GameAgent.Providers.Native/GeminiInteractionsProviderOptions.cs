namespace GameAgent.Providers.Native;

public sealed class GeminiInteractionsProviderOptions
{
    public string ProviderId { get; set; } = "gemini-interactions";

    public Uri BaseUri { get; set; } =
        new("https://generativelanguage.googleapis.com/v1beta");

    public string InteractionsPath { get; set; } = "/interactions";

    public string Model { get; set; } = string.Empty;

    public int MaxContextTokens { get; set; }

    public int MaxOutputTokens { get; set; } = 32_768;

    public int MaxTools { get; set; } = 128;

    public string ToolChoice { get; set; } = "auto";

    public bool SupportsThinkingLevel { get; set; }

    public string? DefaultThinkingLevel { get; set; }

    public bool IncludeThoughtSummaries { get; set; } = true;

    public bool SupportsSamplingControls { get; set; }

    public bool SupportsSeed { get; set; }

    public bool AllowInsecureLoopback { get; set; }

    public int MaxSseLineCharacters { get; set; } = 262_144;

    public int MaxSseEventCharacters { get; set; } = 1_048_576;

    public int MaxStreamCharacters { get; set; } = 67_108_864;

    public int MaxSseEvents { get; set; } = 100_000;

    internal GeminiInteractionsProviderOptions Snapshot()
    {
        var providerId = NativeProviderLimits.Required(
            ProviderId, 128, nameof(ProviderId));
        var model = NativeProviderLimits.Required(
            Model, 256, nameof(Model));
        if (MaxContextTokens < 0
            || MaxOutputTokens is < 1 or > 1_000_000
            || MaxTools is < 1 or > NativeProviderLimits.MaxTools)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
        }

        if (!string.Equals(ToolChoice, "auto", StringComparison.Ordinal)
            && !string.Equals(ToolChoice, "none", StringComparison.Ordinal)
            && !string.Equals(ToolChoice, "required", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The tool-choice mode is unsupported.",
                nameof(ToolChoice));
        }

        var level = DefaultThinkingLevel;
        if (level is not null)
        {
            level = NativeProviderLimits.Required(
                level, 32, nameof(DefaultThinkingLevel));
            if (!IsThinkingLevel(level))
            {
                throw new ArgumentException(
                    "The thinking level is unsupported.",
                    nameof(DefaultThinkingLevel));
            }

            if (!SupportsThinkingLevel)
            {
                throw new ArgumentException(
                    "A default thinking level requires route support.",
                    nameof(DefaultThinkingLevel));
            }
        }

        NativeProviderLimits.ValidateSseLimits(
            MaxSseLineCharacters,
            MaxSseEventCharacters,
            MaxStreamCharacters,
            MaxSseEvents);
        return new GeminiInteractionsProviderOptions
        {
            ProviderId = providerId,
            BaseUri = BaseUri,
            InteractionsPath = InteractionsPath,
            Model = model,
            MaxContextTokens = MaxContextTokens,
            MaxOutputTokens = MaxOutputTokens,
            MaxTools = MaxTools,
            ToolChoice = ToolChoice,
            SupportsThinkingLevel = SupportsThinkingLevel,
            DefaultThinkingLevel = level,
            IncludeThoughtSummaries = IncludeThoughtSummaries,
            SupportsSamplingControls = SupportsSamplingControls,
            SupportsSeed = SupportsSeed,
            AllowInsecureLoopback = AllowInsecureLoopback,
            MaxSseLineCharacters = MaxSseLineCharacters,
            MaxSseEventCharacters = MaxSseEventCharacters,
            MaxStreamCharacters = MaxStreamCharacters,
            MaxSseEvents = MaxSseEvents
        };
    }

    internal static bool IsThinkingLevel(string value) =>
        string.Equals(value, "minimal", StringComparison.Ordinal)
        || string.Equals(value, "low", StringComparison.Ordinal)
        || string.Equals(value, "medium", StringComparison.Ordinal)
        || string.Equals(value, "high", StringComparison.Ordinal);
}

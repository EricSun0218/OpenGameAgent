namespace GameAgent.Providers.Native;

public sealed class OpenAiResponsesProviderOptions
{
    public string ProviderId { get; set; } = "openai-responses";

    public Uri BaseUri { get; set; } = new("https://api.openai.com/v1");

    public string ResponsesPath { get; set; } = "/responses";

    public string Model { get; set; } = string.Empty;

    public int MaxContextTokens { get; set; }

    public int MaxOutputTokens { get; set; } = 32_768;

    public int MaxTools { get; set; } = 128;

    public string ToolChoice { get; set; } = "auto";

    public bool ParallelToolCalls { get; set; } = true;

    public bool StrictToolSchemas { get; set; } = true;

    public bool SupportsSamplingControls { get; set; }

    public bool SupportsSeed { get; set; }

    public bool SupportsPromptCacheKey { get; set; } = true;

    public bool SupportsReasoningEffort { get; set; }

    public string? DefaultReasoningEffort { get; set; }

    public bool AllowInsecureLoopback { get; set; }

    public int MaxSseLineCharacters { get; set; } = 262_144;

    public int MaxSseEventCharacters { get; set; } = 1_048_576;

    public int MaxStreamCharacters { get; set; } = 67_108_864;

    public int MaxSseEvents { get; set; } = 100_000;

    internal OpenAiResponsesProviderOptions Snapshot()
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

        var effort = DefaultReasoningEffort;
        if (effort is not null)
        {
            effort = NativeProviderLimits.Required(
                effort, 32, nameof(DefaultReasoningEffort));
            if (!IsReasoningEffort(effort))
            {
                throw new ArgumentException(
                    "The reasoning effort is unsupported.",
                    nameof(DefaultReasoningEffort));
            }

            if (!SupportsReasoningEffort)
            {
                throw new ArgumentException(
                    "A default reasoning effort requires route support.",
                    nameof(DefaultReasoningEffort));
            }
        }

        NativeProviderLimits.ValidateSseLimits(
            MaxSseLineCharacters,
            MaxSseEventCharacters,
            MaxStreamCharacters,
            MaxSseEvents);
        return new OpenAiResponsesProviderOptions
        {
            ProviderId = providerId,
            BaseUri = BaseUri,
            ResponsesPath = ResponsesPath,
            Model = model,
            MaxContextTokens = MaxContextTokens,
            MaxOutputTokens = MaxOutputTokens,
            MaxTools = MaxTools,
            ToolChoice = ToolChoice,
            ParallelToolCalls = ParallelToolCalls,
            StrictToolSchemas = StrictToolSchemas,
            SupportsSamplingControls = SupportsSamplingControls,
            SupportsSeed = SupportsSeed,
            SupportsPromptCacheKey = SupportsPromptCacheKey,
            SupportsReasoningEffort = SupportsReasoningEffort,
            DefaultReasoningEffort = effort,
            AllowInsecureLoopback = AllowInsecureLoopback,
            MaxSseLineCharacters = MaxSseLineCharacters,
            MaxSseEventCharacters = MaxSseEventCharacters,
            MaxStreamCharacters = MaxStreamCharacters,
            MaxSseEvents = MaxSseEvents
        };
    }

    internal static bool IsReasoningEffort(string value) =>
        string.Equals(value, "none", StringComparison.Ordinal)
        || string.Equals(value, "minimal", StringComparison.Ordinal)
        || string.Equals(value, "low", StringComparison.Ordinal)
        || string.Equals(value, "medium", StringComparison.Ordinal)
        || string.Equals(value, "high", StringComparison.Ordinal)
        || string.Equals(value, "xhigh", StringComparison.Ordinal);
}

using System.Collections.ObjectModel;

namespace GameAgent.Core;

public static class ProviderCapabilityCodes
{
    public const string Streaming = "streaming";
    public const string ToolCalling = "tool_calling";
    public const string JsonOutput = "json_output";
    public const string ReasoningInput = "reasoning_input";
    public const string ParallelToolCalls = "parallel_tool_calls";
    public const string TextInput = "text_input";
    public const string StructuredInput = "structured_input";
    public const string ImageInput = "image_input";
    public const string AudioInput = "audio_input";
    public const string ReasoningEffort = "reasoning_effort";
    public const string ReasoningTokenBudget = "reasoning_token_budget";
    public const string SamplingControls = "sampling_controls";
    public const string Seed = "seed";
    public const string PromptCaching = "prompt_caching";
    public const string AutomaticPromptCaching = "automatic_prompt_caching";
    public const string PromptCacheKey = "prompt_cache_key";
    public const string PromptCacheRetention = "prompt_cache_retention";
    public const string StatefulContinuation = "stateful_continuation";
    public const string ToolCount = "tool_count";
    public const string ToolSchemaBytes = "tool_schema_bytes";
    public const string ContextTokens = "context_tokens";
    public const string OutputTokens = "output_tokens";
}

/// <summary>
/// Provider-neutral requirements for selecting a configured model route.
/// Zero numeric limits mean that the caller does not require a declared
/// minimum. A provider limit of zero remains "unspecified", never infinite.
/// </summary>
public sealed class ProviderCapabilityRequirements
{
    public bool Streaming { get; set; } = true;

    public bool ToolCalling { get; set; }

    public bool JsonOutput { get; set; }

    public bool ReasoningInput { get; set; }

    public bool ParallelToolCalls { get; set; }

    public bool TextInput { get; set; } = true;

    public bool StructuredInput { get; set; }

    public bool ImageInput { get; set; }

    public bool AudioInput { get; set; }

    public bool ReasoningEffort { get; set; }

    public bool ReasoningTokenBudget { get; set; }

    public bool SamplingControls { get; set; }

    public bool Seed { get; set; }

    public bool PromptCaching { get; set; }

    public bool AutomaticPromptCaching { get; set; }

    public bool PromptCacheKey { get; set; }

    public bool PromptCacheRetention { get; set; }

    public bool StatefulContinuation { get; set; }

    public int MinimumTools { get; set; }

    public int MinimumToolSchemaUtf8Bytes { get; set; }

    public int MinimumContextTokens { get; set; }

    public int MinimumOutputTokens { get; set; }

    internal ProviderCapabilityRequirements Snapshot()
    {
        if (MinimumTools < 0
            || MinimumToolSchemaUtf8Bytes < 0
            || MinimumContextTokens < 0
            || MinimumOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumTools),
                "Provider capability requirements cannot be negative.");
        }

        return new ProviderCapabilityRequirements
        {
            Streaming = Streaming,
            ToolCalling = ToolCalling,
            JsonOutput = JsonOutput,
            ReasoningInput = ReasoningInput,
            ParallelToolCalls = ParallelToolCalls,
            TextInput = TextInput,
            StructuredInput = StructuredInput,
            ImageInput = ImageInput,
            AudioInput = AudioInput,
            ReasoningEffort = ReasoningEffort,
            ReasoningTokenBudget = ReasoningTokenBudget,
            SamplingControls = SamplingControls,
            Seed = Seed,
            PromptCaching = PromptCaching,
            AutomaticPromptCaching = AutomaticPromptCaching,
            PromptCacheKey = PromptCacheKey,
            PromptCacheRetention = PromptCacheRetention,
            StatefulContinuation = StatefulContinuation,
            MinimumTools = MinimumTools,
            MinimumToolSchemaUtf8Bytes = MinimumToolSchemaUtf8Bytes,
            MinimumContextTokens = MinimumContextTokens,
            MinimumOutputTokens = MinimumOutputTokens
        };
    }
}

public sealed class ProviderCapabilityMatch
{
    internal ProviderCapabilityMatch(
        ProviderModelCatalogEntry route,
        IReadOnlyList<string> missingCapabilities)
    {
        Route = route;
        MissingCapabilities = missingCapabilities;
    }

    public ProviderModelCatalogEntry Route { get; }

    public IReadOnlyList<string> MissingCapabilities { get; }

    public bool IsMatch => MissingCapabilities.Count == 0;
}

public sealed class ProviderModelCatalogEntry
{
    private readonly ProviderCapabilities _capabilities;

    internal ProviderModelCatalogEntry(ProviderRouteIdentity identity)
    {
        ProviderId = identity.ProviderId;
        ModelId = identity.ModelId;
        TransportDialect = identity.TransportDialect;
        DialectSemanticDigest = identity.DialectSemanticDigest;
        CapabilityDigest = identity.CapabilityDigest;
        RouteDigest = identity.RouteDigest;
        _capabilities = identity.CapabilitiesSnapshot();
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public string TransportDialect { get; }

    public string DialectSemanticDigest { get; }

    public string CapabilityDigest { get; }

    public string RouteDigest { get; }

    public ProviderCapabilities Capabilities => _capabilities.Clone();

    internal ProviderModelCatalogEntry Snapshot()
    {
        return new ProviderModelCatalogEntry(
            ProviderId,
            ModelId,
            TransportDialect,
            DialectSemanticDigest,
            CapabilityDigest,
            RouteDigest,
            _capabilities);
    }

    private ProviderModelCatalogEntry(
        string providerId,
        string modelId,
        string transportDialect,
        string dialectSemanticDigest,
        string capabilityDigest,
        string routeDigest,
        ProviderCapabilities capabilities)
    {
        ProviderId = providerId;
        ModelId = modelId;
        TransportDialect = transportDialect;
        DialectSemanticDigest = dialectSemanticDigest;
        CapabilityDigest = capabilityDigest;
        RouteDigest = routeDigest;
        _capabilities = capabilities.Clone();
    }
}

/// <summary>
/// Immutable catalog of the exact model routes configured for one runtime.
/// It intentionally does not download a mutable global model list at startup.
/// </summary>
public sealed class ProviderModelCatalog
{
    public const int MaximumRoutes = 256;

    private readonly IReadOnlyList<ProviderModelCatalogEntry> _routes;
    private readonly IReadOnlyDictionary<string, ProviderModelCatalogEntry>
        _byProvider;

    private ProviderModelCatalog(
        IReadOnlyList<ProviderModelCatalogEntry> routes)
    {
        _routes = routes;
        _byProvider = new ReadOnlyDictionary<
            string,
            ProviderModelCatalogEntry>(
            routes.ToDictionary(
                route => route.ProviderId,
                route => route,
                StringComparer.Ordinal));
    }

    public IReadOnlyList<ProviderModelCatalogEntry> Routes => _routes;

    public static ProviderModelCatalog Capture(
        IEnumerable<IStreamingModelProvider> providers)
    {
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        var routes = new List<ProviderModelCatalogEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider is null)
            {
                throw new ArgumentException(
                    "Provider catalogs cannot contain null entries.",
                    nameof(providers));
            }

            if (routes.Count >= MaximumRoutes)
            {
                throw new RuntimeContentLimitException(
                    nameof(providers),
                    "provider_model_catalog_too_large",
                    "The configured provider model catalog is too large.");
            }

            var providerId = RuntimeGuard.RequiredUtf8(
                provider.ProviderId,
                128,
                nameof(providers));
            if (!ids.Add(providerId))
            {
                throw new ArgumentException(
                    "Configured provider IDs must be unique.",
                    nameof(providers));
            }

            var metadata = provider is IProviderRouteMetadataSource source
                ? source.RouteMetadata
                : new ProviderRouteMetadata(
                    "unspecified",
                    ProviderDialectContract.LegacyCustom(
                        "custom.unspecified"));
            var identity = new ProviderRouteIdentity(
                providerId,
                metadata,
                provider.Capabilities);
            routes.Add(new ProviderModelCatalogEntry(identity));
        }

        if (routes.Count == 0)
        {
            throw new ArgumentException(
                "At least one provider route is required.",
                nameof(providers));
        }

        return new ProviderModelCatalog(
            new ReadOnlyCollection<ProviderModelCatalogEntry>(
                routes.Select(route => route.Snapshot()).ToArray()));
    }

    public ProviderModelCatalogEntry? Find(string providerId)
    {
        var boundedProviderId = RuntimeGuard.RequiredUtf8(
            providerId,
            128,
            nameof(providerId));

        return _byProvider.TryGetValue(boundedProviderId, out var route)
            ? route.Snapshot()
            : null;
    }

    public IReadOnlyList<ProviderCapabilityMatch> Evaluate(
        ProviderCapabilityRequirements requirements)
    {
        var snapshot = (requirements
                        ?? throw new ArgumentNullException(
                            nameof(requirements)))
            .Snapshot();
        return new ReadOnlyCollection<ProviderCapabilityMatch>(
            _routes
                .Select(
                    route => new ProviderCapabilityMatch(
                        route.Snapshot(),
                        Missing(route.Capabilities, snapshot)))
                .ToArray());
    }

    public IReadOnlyList<ProviderModelCatalogEntry> Select(
        ProviderCapabilityRequirements requirements)
    {
        return new ReadOnlyCollection<ProviderModelCatalogEntry>(
            Evaluate(requirements)
                .Where(match => match.IsMatch)
                .Select(match => match.Route.Snapshot())
                .ToArray());
    }

    private static IReadOnlyList<string> Missing(
        ProviderCapabilities capabilities,
        ProviderCapabilityRequirements requirements)
    {
        var missing = new List<string>();
        Require(requirements.Streaming, capabilities.Streaming,
            ProviderCapabilityCodes.Streaming, missing);
        Require(requirements.ToolCalling, capabilities.ToolCalling,
            ProviderCapabilityCodes.ToolCalling, missing);
        Require(requirements.JsonOutput, capabilities.JsonOutput,
            ProviderCapabilityCodes.JsonOutput, missing);
        Require(requirements.ReasoningInput, capabilities.ReasoningInput,
            ProviderCapabilityCodes.ReasoningInput, missing);
        Require(requirements.ParallelToolCalls, capabilities.ParallelToolCalls,
            ProviderCapabilityCodes.ParallelToolCalls, missing);
        Require(requirements.TextInput, capabilities.TextInput,
            ProviderCapabilityCodes.TextInput, missing);
        Require(requirements.StructuredInput, capabilities.StructuredInput,
            ProviderCapabilityCodes.StructuredInput, missing);
        Require(requirements.ImageInput, capabilities.ImageInput,
            ProviderCapabilityCodes.ImageInput, missing);
        Require(requirements.AudioInput, capabilities.AudioInput,
            ProviderCapabilityCodes.AudioInput, missing);
        Require(requirements.ReasoningEffort, capabilities.ReasoningEffort,
            ProviderCapabilityCodes.ReasoningEffort, missing);
        Require(requirements.ReasoningTokenBudget,
            capabilities.ReasoningTokenBudget,
            ProviderCapabilityCodes.ReasoningTokenBudget, missing);
        Require(requirements.SamplingControls, capabilities.SamplingControls,
            ProviderCapabilityCodes.SamplingControls, missing);
        Require(requirements.Seed, capabilities.Seed,
            ProviderCapabilityCodes.Seed, missing);
        Require(requirements.PromptCaching, capabilities.PromptCaching,
            ProviderCapabilityCodes.PromptCaching, missing);
        Require(requirements.AutomaticPromptCaching,
            capabilities.AutomaticPromptCaching,
            ProviderCapabilityCodes.AutomaticPromptCaching, missing);
        Require(requirements.PromptCacheKey, capabilities.PromptCacheKey,
            ProviderCapabilityCodes.PromptCacheKey, missing);
        Require(requirements.PromptCacheRetention,
            capabilities.PromptCacheRetention,
            ProviderCapabilityCodes.PromptCacheRetention, missing);
        Require(requirements.StatefulContinuation,
            capabilities.StatefulContinuation,
            ProviderCapabilityCodes.StatefulContinuation, missing);
        RequireLimit(requirements.MinimumTools, capabilities.MaxTools,
            ProviderCapabilityCodes.ToolCount, missing);
        RequireLimit(requirements.MinimumToolSchemaUtf8Bytes,
            capabilities.MaxToolSchemaUtf8Bytes,
            ProviderCapabilityCodes.ToolSchemaBytes, missing);
        RequireLimit(requirements.MinimumContextTokens,
            capabilities.MaxContextTokens,
            ProviderCapabilityCodes.ContextTokens, missing);
        RequireLimit(requirements.MinimumOutputTokens,
            capabilities.MaxOutputTokens,
            ProviderCapabilityCodes.OutputTokens, missing);
        return new ReadOnlyCollection<string>(missing.ToArray());
    }

    private static void Require(
        bool required,
        bool available,
        string code,
        ICollection<string> missing)
    {
        if (required && !available)
        {
            missing.Add(code);
        }
    }

    private static void RequireLimit(
        int required,
        int available,
        string code,
        ICollection<string> missing)
    {
        if (required > 0 && (available == 0 || available < required))
        {
            missing.Add(code);
        }
    }
}

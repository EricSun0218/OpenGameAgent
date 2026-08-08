using OpenGameAgent.Kernel;
using OpenGameAgent.Models;

namespace OpenGameAgent.Extensions;

public sealed class GameModelCatalogExtension : IGameAgentExtension
{
    private readonly GameModelCatalog _catalog;

    public GameModelCatalogExtension(GameModelCatalog catalog, string extensionId = "models")
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Descriptor = new GameAgentExtensionDescriptor(
            extensionId,
            "1.0.0",
            "Provider discovery, model capabilities, authentication state, and model selection.",
            new[] { "model-catalog", "provider-auth", "dynamic-models" });
    }

    public GameAgentExtensionDescriptor Descriptor { get; }

    public void Configure(GameAgentExtensionApi api)
    {
        if (api is null)
        {
            throw new ArgumentNullException(nameof(api));
        }

        api.RegisterService("catalog", _catalog);
        foreach (var provider in _catalog.GetProviders())
        {
            api.RegisterModelProvider(
                provider.Descriptor.ProviderId,
                _catalog.CreateProvider(provider.Descriptor.ProviderId));
        }
    }

    public GameModelSelection Select(
        string providerId,
        string modelId,
        GameReasoningLevel reasoning = GameReasoningLevel.Off,
        ModelParameters? baseline = null,
        GameModelInputCapabilities requiredInput = GameModelInputCapabilities.None,
        GameModelOutputCapabilities requiredOutput = GameModelOutputCapabilities.None)
    {
        var resolution = _catalog.Resolve(
            providerId,
            modelId,
            reasoning,
            requiredInput,
            requiredOutput);
        return new GameModelSelection(
            resolution.Model.ModelId,
            parameters: resolution.CreateParameters(baseline),
            provider: _catalog.CreateProvider(resolution.Model.ProviderId),
            contextWindowTokens: resolution.Model.ContextWindowTokens,
            maximumOutputTokens: resolution.Model.MaximumOutputTokens);
    }
}
